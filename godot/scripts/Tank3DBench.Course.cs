using System.Collections.Generic;
using System.Linq;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// What a move would be, shown before it is ordered (docs/hud3d-plan.md,
/// stage 2): the cells the selected tank can reach this turn, and the way to
/// the one under the pointer - off <see cref="Steps"/>, the same answer the
/// order then drives.
///
/// <list type="bullet">
/// <item><b>The reach</b> - the cells tinted in two tones, bright where the tank
/// arrives with a step to spare for a turn, faint where it arrives with none,
/// and a contour round the whole. A red stop sign on the cells where a move
/// ends: mud, wood, a mine, deep water without the snorkel.</item>
/// <item><b>The way</b> - a ribbon through the cells' middles lying on the
/// ground, chevrons along it, the steps spent by each cell in a disc (brown on
/// sand, which costs two), "+N пов." where it turns, a pill for what it risks
/// (мина, огонь, утонет), and an arrowhead at the end for where the hull will
/// point.</item>
/// <item><b>One way, the next by Tab.</b> Ways of the same cost are the
/// player's choice (the mine, the fire, the ambush depend on the cells), shown
/// one at a time; Tab steps to the next while there is more than one, and opens
/// the panel otherwise. The click orders the way shown.</item>
/// </list>
///
/// The tint and the ribbon are a mesh on the cells' tops, so the hull and the
/// trees hide what is behind them, as the marks do (<c>Tank3DBench.Select</c>);
/// the numbers and signs are drawn on the screen over everything, where they
/// stay legible. Shown only while the tank is selected, alive and standing with
/// no order; <c>--no-ui</c> shows none. <c>--hover Q,R</c> holds the pointer on
/// a cell for a capture, <c>--do way</c> is Tab's step.
/// </summary>
public sealed partial class Tank3DBench
{
    private static readonly Color CourseInk = new(0.09f, 0.37f, 0.65f);
    private static readonly Color StopInk = new(0.64f, 0.18f, 0.18f);
    private static readonly Color SandInk = new(0.52f, 0.31f, 0.04f);
    private static readonly Color TurnInk = new(0.98f, 0.78f, 0.46f);

    private MeshInstance3D? _course;
    private Control? _courseInk;
    private Steps.Reach? _reach;
    private Vector2I? _hoverCell;
    /// <summary><c>--hover Q,R</c>: the pointer held on a cell.</summary>
    private Vector2I? _hoverFixed;
    private Vector2 _hoverMouse = new(-1, -1);
    private List<Steps.Route> _ways = new();
    private int _wayIx;
    private string _courseNote = "";

    private void BuildCourse(CanvasLayer layer)
    {
        if (_noUi)
            return;
        _course = new MeshInstance3D
        {
            Name = "Course",
            Mesh = new ImmediateMesh(),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                VertexColorUseAsAlbedo = true,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                // under the marks, over the water
                RenderPriority = Stage3D.RingOrder - 1,
            },
        };
        AddChild(_course);
        _courseInk = new Control { Name = "CourseInk", MouseFilter = Control.MouseFilterEnum.Ignore };
        _courseInk.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _courseInk.Draw += DrawCourse;
        layer.AddChild(_courseInk);
    }

    /// <summary>The cells no step may enter: the other tanks - the order's
    /// reading and the HUD's, one list.</summary>
    private HashSet<Vector2I> Barred(bool ram = false)
    {
        var barred = new HashSet<Vector2I>();
        FillBarred(barred, ram);
        return barred;
    }

    /// <summary><see cref="Barred"/> into a set the caller keeps - the
    /// course's, every frame, with no set and no LINQ made for it.</summary>
    private void FillBarred(HashSet<Vector2I> into, bool ram = false)
    {
        into.Clear();
        if (_other is { } o && !ram && o.Fate != Fate.Destroyed)
            into.Add(OtherCell(o));
        foreach (Parked p in _parked)
            if (p.Fate != Fate.Destroyed)
                into.Add(p.Cell);
    }

    /// <summary>What the reach was searched for - the tank's cell and side
    /// and the cells barred - to tell a frame that needs a new search;
    /// <see cref="_reachVersion"/> counts the searches, for the keys that
    /// build on it (<c>Post</c>, <c>RamPlan</c>).</summary>
    private Vector2I _reachHere = new(int.MinValue, int.MinValue);
    private int _reachSide = int.MinValue;
    private HashSet<Vector2I> _reachBarred = new();
    private readonly HashSet<Vector2I> _barredNow = new();
    private int _reachVersion;

    private bool CourseShown =>
        _course is not null && _field is not null && _tile is not null && _rules && _chosen
        && _fate == Fate.Alive && !RouteMoving && _turnGoal is null && !RamRunning && MatchMayMove;

    /// <summary>The way the click on <paramref name="cell"/> orders: the one
    /// shown, if it is to that cell.</summary>
    private Steps.Route? ShownWay(Vector2I cell) =>
        (_lockCell ?? _hoverCell) == cell && _ways.Count > 0 ? _ways[_wayIx % _ways.Count] : null;

    /// <summary>Tab: the next way of the same cost. False when there is no
    /// other, and Tab is the panel's.</summary>
    private bool NextWay()
    {
        if (!CourseShown || _ways.Count < 2)
            return false;
        _wayIx = (_wayIx + 1) % _ways.Count;
        // a fixed cell's ghost faces where the new way arrives
        if (_lockCell is not null)
            _lockFace = _ways[_wayIx].End.Heading;
        GD.Print($"tank3d: way {_wayIx + 1} of {_ways.Count} to {_hoverCell}: {_ways[_wayIx]}");
        return true;
    }

    /// <summary><c>--do forks</c>: every cell in reach with more than one way of
    /// its cost, to the log - where Tab has something to step through.</summary>
    private void Forks()
    {
        if (_reach is null)
        {
            GD.Print("tank3d: forks: no reach (the tank is not selected and standing)");
            return;
        }
        var forks = _reach.Cells.Keys.Select(c => (c, _reach.RoutesTo(c))).Where(f => f.Item2.Count > 1).ToList();
        GD.Print($"tank3d: forks from {_reach.Start.Cell} facing {_reach.Start.Heading}, budget {_reach.Budget}: "
                 + (forks.Count == 0 ? "none" : string.Join("; ", forks.Select(f => $"{f.c} x{f.Item2.Count}"))));
    }

    /// <summary>Every frame: the reach searched again when the tank's pose or
    /// the board's tanks moved, the way when the pointer did; then drawn.</summary>
    private void CourseTick()
    {
        if (_course?.Mesh is not ImmediateMesh mesh)
            return;
        mesh.ClearSurfaces();
        _courseNote = "";
        if (!CourseShown)
        {
            _hoverCell = null;
            _ways.Clear();
            Unlock();
            GhostTick(null);
            _courseInk?.QueueRedraw();
            return;
        }
        Vector2I here = CellHere;
        int side = HullSide();
        FillBarred(_barredNow);
        bool fresh = here != _reachHere || side != _reachSide || !_barredNow.SetEquals(_reachBarred);
        if (fresh)
        {
            _reachHere = here;
            _reachSide = side;
            // a set of its own: the search keeps it
            _reachBarred = new HashSet<Vector2I>(_barredNow);
            _reachVersion++;
            _reach = Steps.Plan(_field!, new Steps.Pose(here, side), Steps.Mover.Of(_profile, _amphibious, _reachBarred));
        }
        // A fixed cell holds the way shown until it is ordered or let go - or
        // falls out of reach, when the board moved under it.
        if (_lockCell is Vector2I locked && !_reach!.Reaches(locked))
            Unlock();
        if (_lockCell is Vector2I held)
        {
            if (fresh || _hoverCell != held)
            {
                _hoverCell = held;
                _ways = _reach!.RoutesTo(held);
                if (_lockFace is int f && _ways.Count > 0 && _reach.TurnTo(_ways[_wayIx % _ways.Count], f) is null)
                    _lockFace = _ways[_wayIx % _ways.Count].End.Heading;
            }
        }
        Vector2 mouse = GetViewport().GetMousePosition();
        if (_lockCell is null && (fresh || mouse != _hoverMouse))
        {
            _hoverMouse = mouse;
            Vector2I? cell = _hoverFixed;
            if (cell is null && PickAt(mouse) is var (point, onTank, onOwn) && !onTank && !onOwn)
                cell = _field!.FlatCellAt(Board(point));
            if (cell != _hoverCell || fresh)
            {
                if (cell != _hoverCell)
                    _wayIx = 0;
                _hoverCell = cell;
                _ways = cell is { } c && _reach!.Reaches(c) ? _reach.RoutesTo(c) : new List<Steps.Route>();
            }
        }

        mesh.SurfaceBegin(Mesh.PrimitiveType.Triangles);
        Zone(mesh, _reach!);
        if (_ways.Count > 0)
            Ribbon(mesh, _ways[_wayIx % _ways.Count]);
        mesh.SurfaceEnd();

        Steps.Route? shown = _ways.Count > 0 ? _ways[_wayIx % _ways.Count] : null;
        GhostTick(shown);
        if (shown is not null)
        {
            string tab = _ways.Count > 1 ? $", Tab — другой ({_wayIx % _ways.Count + 1}/{_ways.Count})" : "";
            if (_postLock)
            {
                int turn = _reach!.TurnTo(shown, GhostFace(shown)) ?? 0;
                _courseNote = $"{(_ramLock ? "таран" : "огонь")} после хода: {shown.Spent}{(turn > 0 ? $"+{turn}" : "")} из {_reach.Budget}{tab}";
            }
            else if (_lockCell is not null)
            {
                int turn = _reach!.TurnTo(shown, GhostFace(shown)) ?? 0;
                _courseNote = $"выбрано: {shown.Spent}{(turn > 0 ? $"+{turn}" : "")} из {_reach.Budget}{tab};"
                              + " разворот — роза/A/D/колесо, Enter — ехать, Esc — отмена";
            }
            else
                _courseNote = $"путь {shown.Spent} из {_reach!.Budget}{tab}; щелчок — выбрать клетку";
        }
        else
            _courseNote = $"{_reach!.Budget} шагов, клеток в досягаемости {_reach.Cells.Count}";
        _courseInk?.QueueRedraw();
    }

    // --- the mesh ---------------------------------------------------------

    private Vector2 CellFlat(Vector2I cell) => _field!.FlatAnchor(cell) + _field.CentreOffset;

    /// <summary>A board point on the top of <paramref name="cell"/>, in the
    /// world, nudged toward the eye like the marks.</summary>
    private Vector3 OnTop(Vector2I cell, Vector2 flat) =>
        new Vector3(flat.X, _field!.TopOn(cell, flat) / RiseFactor, flat.Y / Squash)
        + Stage3D.Clear(Squash, RiseFactor) + _camera.GlobalBasis.Z * MarkNudge * HexWidth;

    /// <summary>The hexagon's corners about its middle, board units: the
    /// marks' (<see cref="Band"/>).</summary>
    private Vector2[] Corners()
    {
        Vector2 size = _tile!.HexRect.Size;
        float w = size.X * 0.5f, h = size.Y * 0.5f;
        return new[]
        {
            new Vector2(-w, 0.0f), new Vector2(-w * 0.5f, -h), new Vector2(w * 0.5f, -h),
            new Vector2(w, 0.0f), new Vector2(w * 0.5f, h), new Vector2(-w * 0.5f, h),
        };
    }

    /// <summary>The corner pair whose edge faces <paramref name="side"/>: the
    /// one whose middle points at that neighbour - asked of the board, not of a
    /// table of angles.</summary>
    private int EdgeOf(Vector2I cell, int side, Vector2[] corner)
    {
        Vector2 way = CellFlat(HexField.Step(cell, side)) - CellFlat(cell);
        int best = 0;
        float dot = float.MinValue;
        for (int i = 0; i < 6; i++)
        {
            float d = ((corner[i] + corner[(i + 1) % 6]) * 0.5f).Dot(way);
            if (d > dot)
            {
                dot = d;
                best = i;
            }
        }
        return best;
    }

    private void Tri(ImmediateMesh mesh, Color ink, Vector2I cell, Vector2 a, Vector2 b, Vector2 c)
    {
        // three calls, not a loop over new[] { a, b, c }: a few hundred
        // triangles a frame, 10 KB of arrays (BOT-3)
        mesh.SurfaceSetColor(ink);
        mesh.SurfaceAddVertex(OnTop(cell, a));
        mesh.SurfaceSetColor(ink);
        mesh.SurfaceAddVertex(OnTop(cell, b));
        mesh.SurfaceSetColor(ink);
        mesh.SurfaceAddVertex(OnTop(cell, c));
    }

    private void Quad(ImmediateMesh mesh, Color ink, Vector2I cell, Vector2 a, Vector2 b, Vector2 c, Vector2 d)
    {
        Tri(mesh, ink, cell, a, b, c);
        Tri(mesh, ink, cell, a, c, d);
    }

    private void Zone(ImmediateMesh mesh, Steps.Reach reach)
    {
        Vector2[] corner = Corners();
        foreach ((Vector2I cell, int cost) in reach.Cells)
        {
            Vector2 mid = CellFlat(cell);
            var tint = new Color(CourseInk, reach.Budget - cost >= 1 ? 0.30f : 0.13f);
            for (int i = 0; i < 6; i++)
                Tri(mesh, tint, cell, mid, mid + corner[i] * 0.95f, mid + corner[(i + 1) % 6] * 0.95f);
            foreach (int side in HexField.EdgeHeadings)
            {
                Vector2I next = HexField.Step(cell, side);
                if (reach.Reaches(next) || next == reach.Start.Cell)
                    continue;
                int e = EdgeOf(cell, side, corner);
                Vector2 a = corner[e], b = corner[(e + 1) % 6];
                Quad(mesh, new Color(CourseInk, 0.95f), cell,
                     mid + a * 0.99f, mid + b * 0.99f, mid + b * 0.90f, mid + a * 0.90f);
            }
        }
    }

    /// <summary>The ribbon: a strip through the middles of the cells the way
    /// drives, cut into short pieces so each lies on the cell under it.</summary>
    private void Ribbon(ImmediateMesh mesh, Steps.Route way)
    {
        var ink = new Color(CourseInk, 0.9f);
        float half = HexWidth * 0.055f;
        Vector2I at = way.Legs[0].At.Cell;
        foreach (Vector2I next in way.Cells())
        {
            Vector2 a = CellFlat(at), b = CellFlat(next);
            Vector2 across = (b - a).Normalized().Orthogonal() * half;
            const int pieces = 8;
            for (int k = 0; k < pieces; k++)
            {
                Vector2 p = a.Lerp(b, (float)k / pieces), q = a.Lerp(b, (float)(k + 1) / pieces);
                Vector2I under = _field!.FlatCellAt((p + q) * 0.5f);
                Quad(mesh, ink, under, p + across, q + across, q - across, p - across);
            }
            at = next;
        }
    }

    // --- the screen -------------------------------------------------------

    private Vector2 Screen(Vector2I cell) => _camera.UnprojectPosition(OnTop(cell, CellFlat(cell)));

    private void DrawCourse()
    {
        if (_courseInk is not { } ink || !CourseShown || _reach is null)
            return;
        Font font = ThemeDB.FallbackFont;
        float hex = (Screen(_reach.Start.Cell) - _camera.UnprojectPosition(
            OnTop(_reach.Start.Cell, CellFlat(_reach.Start.Cell) + new Vector2(_tile!.HexRect.Size.X * 0.5f, 0)))).Length();
        float r = Mathf.Clamp(hex * 0.16f, 8.0f, 16.0f);
        int size = Mathf.RoundToInt(r * 1.15f);

        foreach (Vector2I cell in _reach.Cells.Keys)
            if (Steps.Stops(_field!, cell) || _field!.CoverAt(cell) == Cover.Minefield
                || (_field.IsDeep(cell) && !_amphibious))
            {
                Vector2 at = Screen(cell) + new Vector2(hex * 0.42f, -hex * 0.30f);
                ink.DrawCircle(at, r * 0.8f, StopInk);
                ink.DrawRect(new Rect2(at - new Vector2(r * 0.5f, r * 0.14f), new Vector2(r, r * 0.28f)), Colors.White);
            }

        if (_ways.Count == 0)
            return;
        Steps.Route way = _ways[_wayIx % _ways.Count];
        var stops = new List<(Vector2I Cell, int Spent, int Turns)> { (way.Legs[0].At.Cell, 0, 0) };
        foreach (Steps.Leg leg in way.Legs.Skip(1))
        {
            if (leg.Act == Steps.Act.Turn)
                stops[^1] = (stops[^1].Cell, stops[^1].Spent, stops[^1].Turns + 1);
            else
                stops.Add((leg.At.Cell, leg.Spent, 0));
        }
        for (int i = 1; i < stops.Count; i++)
        {
            Vector2 a = Screen(stops[i - 1].Cell), b = Screen(stops[i].Cell);
            Vector2 mid = (a + b) * 0.5f, d = (b - a).Normalized(), n = d.Orthogonal();
            float c = r * 0.55f;
            if (way.Back)
                d = -d;
            ink.DrawPolyline(new[] { mid - d * c + n * c, mid, mid - d * c - n * c }, Colors.White, 3.0f, true);
        }
        for (int i = 0; i < stops.Count; i++)
        {
            (Vector2I cell, int spent, int turns) = stops[i];
            Vector2 at = Screen(cell);
            // the last disc steps back along the ribbon, off the ghost standing there
            if (i > 0 && i == stops.Count - 1 && _ghost is not null)
                at = at.Lerp(Screen(stops[i - 1].Cell), 0.38f);
            if (i > 0)
            {
                bool sand = _field!.FoundationAt(cell) == Foundation.Sand;
                ink.DrawCircle(at, r, sand ? SandInk : new Color(0.02f, 0.17f, 0.33f));
                ink.DrawArc(at, r, 0, Mathf.Tau, 24, Colors.White, 1.5f, true);
                string n = spent.ToString();
                Vector2 w = font.GetStringSize(n, HorizontalAlignment.Left, -1, size);
                ink.DrawString(font, at + new Vector2(-w.X * 0.5f, size * 0.36f), n, HorizontalAlignment.Left, -1, size, Colors.White);
            }
            if (turns > 0)
                Pill(ink, font, at + new Vector2(-hex * 0.45f, hex * 0.32f), $"+{turns} пов.", TurnInk, new Color(0.25f, 0.14f, 0.01f), size);
        }
        string? risk = way.Risk.HasFlag(Steps.Risk.Mine) ? "мина"
            : way.Risk.HasFlag(Steps.Risk.Drown) ? "утонет"
            : way.Risk.HasFlag(Steps.Risk.Fire) ? "огонь" : null;
        Vector2 end = Screen(way.End.Cell);
        if (risk is not null)
            Pill(ink, font, end + new Vector2(0, -hex * 0.48f), risk, new Color(0.94f, 0.58f, 0.58f), new Color(0.31f, 0.07f, 0.07f), size);
        DrawRose(ink, font, size);
        // Where the hull will point: the ghost says it (Tank3DBench.Ghost); the
        // arrowhead only stands in when its model did not load.
        if (_ghost is not null)
            return;
        Vector2 nose = (_camera.UnprojectPosition(OnTop(way.End.Cell, CellFlat(way.End.Cell)
                        + (CellFlat(HexField.Step(way.End.Cell, way.End.Heading)) - CellFlat(way.End.Cell)) * 0.5f)) - end).Normalized();
        Vector2 tip = end + nose * hex * 0.62f, flank = nose.Orthogonal() * r * 0.8f;
        ink.DrawColoredPolygon(new[] { tip, tip - nose * r * 1.4f + flank, tip - nose * r * 1.4f - flank }, CourseInk);
        ink.DrawPolyline(new[] { tip, tip - nose * r * 1.4f + flank, tip - nose * r * 1.4f - flank, tip }, Colors.White, 1.5f, true);
    }

    private static void Pill(Control ink, Font font, Vector2 at, string text, Color fill, Color letters, int size)
    {
        Vector2 w = font.GetStringSize(text, HorizontalAlignment.Left, -1, size);
        var box = new Rect2(at - new Vector2(w.X * 0.5f + 7, size * 0.75f), new Vector2(w.X + 14, size * 1.5f));
        ink.DrawRect(box, fill);
        ink.DrawString(font, at + new Vector2(-w.X * 0.5f, size * 0.36f), text, HorizontalAlignment.Left, -1, size, letters);
    }
}
