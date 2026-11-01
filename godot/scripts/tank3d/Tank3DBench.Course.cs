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
/// ends: mud, deep water without the snorkel - not a mine, not a wood
/// (drawn through instead, Tank3DBench.Wood).</item>
/// <item><b>The way</b> - a ribbon through the cells' middles lying on the
/// ground, chevrons along it, the steps spent by each cell in a disc (brown on
/// sand, which costs two), a pill for what it risks
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
        // the relief's zone: its own mesh, built when the reach changes
        var rim = new ShaderMaterial { Shader = new Shader { Code = ZoneShader }, RenderPriority = Stage3D.RingOrder };
        rim.SetShaderParameter("line_ink", new Color(CourseInk, 0.95f));
        _zone = new MeshInstance3D
        {
            Name = "Zone",
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            MaterialOverride = rim,
        };
        AddChild(_zone);
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
        && _fate == Fate.Alive && !RouteMoving && _turnGoal is null && !RamRunning && MatchMayMove
        // «Засада» on: the turn ends in the ambush, so there is no move to show (Tank3DBench.Ambush)
        && !_ambushMode
        // a bonus act's hex being picked: its hexes instead (Tank3DBench.Gear, PickZone)
        && !PickMode;

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
        Log.Print($"tank3d: way {_wayIx + 1} of {_ways.Count} to {MapAt(_hoverCell)}: {MapAt(_ways[_wayIx])}");
        return true;
    }

    /// <summary><c>--do forks</c>: every cell in reach with more than one way of
    /// its cost, to the log - where Tab has something to step through.</summary>
    private void Forks()
    {
        if (_reach is null)
        {
            Log.Print("tank3d: forks: no reach (the tank is not selected and standing)");
            return;
        }
        var forks = _reach.Cells.Keys.Select(c => (c, _reach.RoutesTo(c))).Where(f => f.Item2.Count > 1).ToList();
        Log.Print($"tank3d: forks from {MapAt(_reach.Start.Cell)} facing {_reach.Start.Heading}, budget {_reach.Budget}: "
                 + (forks.Count == 0 ? "none" : string.Join("; ", forks.Select(f => $"{MapAt(f.c)} x{f.Item2.Count}"))));
    }

    /// <summary>Every frame: the reach searched again when the tank's pose or
    /// the board's tanks moved, the way when the pointer did; then drawn.</summary>
    private void CourseTick()
    {
        if (_course?.Mesh is not ImmediateMesh mesh)
            return;
        mesh.ClearSurfaces();
        _courseNote = "";
        SpinTick();
        RoseFadeTick();
        if (_zone is not null)
            _zone.Visible = CourseShown;
        if (!CourseShown)
        {
            _hoverCell = null;
            _ways.Clear();
            Unlock();
            GhostTick(null);
            // «Засада» on: the hexes its axes watch instead, in red (Tank3DBench.Ambush)
            AmbushZone(mesh);
            // «Дым», «Мина», «Ремонт», «Лебёдка» on: the hexes it may be done on, in amber
            PickZone(mesh);
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
            Steps.Mover mover = RigMover(_reachBarred);
            // the zone gives no mine away (Steps.Mover.MinesUnseen); the hull finds it
            mover.MinesUnseen = true;
            _reach = Steps.Plan(_field!, new Steps.Pose(here, side), mover);
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
            // a disc of the turn's rose over the cell beside: the disc's (Tank3DBench.Spin)
            if (SpinAt(mouse) is not null)
                cell = null;
            if (cell != _hoverCell || fresh)
            {
                if (cell != _hoverCell)
                    _wayIx = 0;
                _hoverCell = cell;
                _ways = cell is { } c && _reach!.Reaches(c) ? _reach.RoutesTo(c) : new List<Steps.Route>();
            }
        }

        if (_relief is not null)
            ReliefZone(_reach!);
        // an empty surface is an engine error: on the relief the zone is
        // its own mesh, and with no way shown there is nothing here
        if (_relief is null || _ways.Count > 0)
        {
            mesh.SurfaceBegin(Mesh.PrimitiveType.Triangles);
            if (_relief is null)
                Zone(mesh, _reach!);
            if (_ways.Count > 0)
                Ribbon(mesh, _ways[_wayIx % _ways.Count]);
            mesh.SurfaceEnd();
        }

        Steps.Route? shown = _ways.Count > 0 ? _ways[_wayIx % _ways.Count] : null;
        RoseHoverTick();
        GhostTick(shown);
        // the unit's card lights the steps it spends (Tank3DBench.Card)
        _wayCost = shown is not null ? shown.Spent + (_reach!.TurnTo(shown, GhostFace(shown)) ?? 0)
            : SpinShown() is int spin ? SpinCost(spin) ?? 0 : 0;
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
        // where and with which plate the enemy's ambushes meet it (Tank3DBench.Ambush)
        string forecast = shown is not null ? AmbushForecast(shown, GhostFace(shown))
            : SpinShown() is int about && about != HullSide() ? AmbushForecast(null, about) : "";
        if (forecast.Length > 0)
            _courseNote += forecast;
        _courseInk?.QueueRedraw();
    }

    // --- the mesh ---------------------------------------------------------

    private Vector2 CellFlat(Vector2I cell) => _field!.FlatAnchor(cell) + _field.CentreOffset;

    /// <summary>A board point on the top of <paramref name="cell"/>, in the
    /// world, nudged toward the eye like the marks.</summary>
    private Vector3 OnTop(Vector2I cell, Vector2 flat) =>
        _relief is not null ? OnGround(cell, flat)
        : new Vector3(flat.X, TopOrWater(cell, flat) / RiseFactor, flat.Y / Squash) + Lie;

    /// <summary>
    /// How the course is held off the ground: toward the eye along the
    /// camera's own ray, as far as <c>Stage3D.Clear</c> and
    /// <see cref="MarkNudge"/> together - no pixel moves, the camera being
    /// orthographic, and depth is bought. <c>Clear</c> itself is up and back
    /// along z: the ray of the camera the board was made for, and once the
    /// camera turned a real shift, under which the slopes facing one way
    /// covered the ribbon (the author's note, 2026-10-31).
    /// </summary>
    private Vector3 Lie =>
        _camera.GlobalBasis.Z.Normalized() * (Stage3D.Clear(Squash, RiseFactor).Length() + MarkNudge * HexWidth);

    /// <summary>The cell's top, or over a water cell (a ramp's face aside)
    /// its water's surface: the zone and the way laid on the top of a pond
    /// cell hung over the water by the bank's height, and the outline broke
    /// where the cell met the shore (the author's note, 2026-10-29; the
    /// relief's zone had it already, <see cref="OnGround"/>).</summary>
    private float TopOrWater(Vector2I cell, Vector2 flat)
    {
        HexField f = _field!;
        return f.InBounds(cell) && f.IsWater(cell) && !f.IsRamp(cell) ? f.WaterTop(cell) : f.TopOn(cell, flat);
    }

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

    /// <summary>
    /// The zone's fill (2026-10-29, the author's note: some hexes of the
    /// zone looked bare, the joints and the tank's own hex unfilled). One
    /// tone, the whole hexagon, and the cell the tank stands on with them.
    /// Before, the last step's cells were drawn at 0.13 against 0.30 - on
    /// grass, not there at all - every hexagon at 0.95 of its size, so the
    /// board's grid showed between them, and the start was left out, it not
    /// being a cell a move ends on (<see cref="Steps.Reach.Cells"/>).
    /// </summary>
    private const float ZoneTone = 0.30f;

    private readonly List<Vector2I> _zoneFill = new();

    private void ZoneCell(ImmediateMesh mesh, Color tint, Vector2I cell, Vector2[] corner)
    {
        Vector2 mid = CellFlat(cell);
        for (int i = 0; i < 6; i++)
            Tri(mesh, tint, cell, mid, mid + corner[i], mid + corner[(i + 1) % 6]);
    }

    private void Zone(ImmediateMesh mesh, Steps.Reach reach)
    {
        Vector2[] corner = Corners();
        var tint = new Color(CourseInk, ZoneTone);
        foreach ((Vector2I cell, int _) in reach.Cells)
            ZoneCell(mesh, tint, cell, corner);
        ZoneCell(mesh, tint, reach.Start.Cell, corner);
        var line = new Color(CourseInk, 0.95f);
        ZoneBand(reach, corner, 1);
        for (int i = 0; i < _band.Count; i++)
        {
            mesh.SurfaceSetColor(line);
            mesh.SurfaceAddVertex(_band[i]);
        }
    }

    /// <summary>An edge of the zone's outline: from <see cref="A"/> to
    /// <see cref="B"/> on the rim of <see cref="Cell"/>, flat-space points,
    /// <see cref="In"/> toward its middle on the ground.</summary>
    private struct Edge
    {
        public Vector2 A, B, In;
        public Vector2I Cell;
        public bool Done;
    }

    private readonly List<Edge> _edges = new();
    private readonly Dictionary<Vector2I, int> _edgeFrom = new(), _edgeTo = new();
    /// <summary>The outline's band, as triangles in the world (<see cref="ZoneBand"/>).</summary>
    private readonly List<Vector3> _band = new();
    private readonly List<Vector3> _rim = new();
    private readonly List<Vector3> _rimIn = new();

    /// <summary>The outline's width, of a hexagon's circumradius.</summary>
    private const float ZoneLine = 0.08f;

    /// <summary>
    /// The zone's outline as one band, as wide all round.
    /// <list type="bullet">
    /// <item><b>At the joints</b> (2026-10-29, the author's note: it narrowed
    /// where hexes met). Each cell drew its own edges as trapezoids toward
    /// its middle, and where the outline went on into the next cell the two
    /// inner ends met at an angle and the band pinched. Now the rim's edges
    /// are chained into loops - every cell's corners go round the same way,
    /// so an edge's end is the next one's start - and walked as one line.</item>
    /// <item><b>Over the ground's levels</b> (the same day: it changed width
    /// where the ground rose or fell). Its inner side was offset on the
    /// ground and laid on it, so a slope toward the eye drew it wide and one
    /// away thin. Now the outer side is the rim laid on the ground, and the
    /// inner side is that point moved a width in the camera's own plane,
    /// square to the line as the eye sees it and mitred at each bend - the
    /// camera is orthographic, so a width there is the same on the screen
    /// at any height.</item>
    /// </list>
    /// <paramref name="pieces"/> cuts each edge for the relief's ground.
    /// </summary>
    private void ZoneBand(Steps.Reach reach, Vector2[] corner, int pieces)
    {
        // the cells a move ends on and the one the tank stands on - filled
        // with them (ZoneTone), it is ringed with them: before, the edges
        // facing it were skipped and its own never walked, and the outline
        // broke off on every side of it toward a tank or a wall
        _zoneFill.Clear();
        foreach ((Vector2I c, int _) in reach.Cells)
            _zoneFill.Add(c);
        _zoneFill.Add(reach.Start.Cell);
        ZoneBand(corner, pieces);
    }

    /// <summary>The cells of <see cref="_zoneFill"/> as a set, for the outline's
    /// «is the cell beside in the zone» - kept, not made each frame.</summary>
    private readonly HashSet<Vector2I> _zoneSet = new();

    /// <summary>The outline round the cells in <see cref="_zoneFill"/> - the
    /// move's zone, or the ambush's axes (Tank3DBench.Ambush).</summary>
    private void ZoneBand(Vector2[] corner, int pieces)
    {
        _edges.Clear();
        _edgeFrom.Clear();
        _edgeTo.Clear();
        _band.Clear();
        _zoneSet.Clear();
        foreach (Vector2I cell in _zoneFill)
            _zoneSet.Add(cell);
        foreach (Vector2I cell in _zoneFill)
        {
            Vector2 mid = CellFlat(cell);
            foreach (int side in HexField.EdgeHeadings)
            {
                Vector2I next = HexField.Step(cell, side);
                if (_zoneSet.Contains(next))
                    continue;
                int k = EdgeOf(cell, side, corner);
                Vector2 a = mid + corner[k], b = mid + corner[(k + 1) % 6];
                _edgeFrom[CornerKey(a)] = _edges.Count;
                _edgeTo[CornerKey(b)] = _edges.Count;
                _edges.Add(new Edge { A = a, B = b, In = mid - (a + b) * 0.5f, Cell = cell });
            }
        }
        Basis eye = _camera.GlobalBasis;
        float w = ZoneLine * _tile!.HexRect.Size.X * 0.5f;
        // open chains from their first edge, then the closed loops left
        for (int pass = 0; pass < 2; pass++)
            for (int s = 0; s < _edges.Count; s++)
            {
                if (_edges[s].Done || (pass == 0 && _edgeTo.ContainsKey(CornerKey(_edges[s].A))))
                    continue;
                Chain(s, pieces, eye, w);
            }
    }

    /// <summary>One run of the outline from edge <paramref name="start"/>: its
    /// rim on the ground, and its inner side on the ground too
    /// (<see cref="Run"/>). Where two edges meet at two heights - a prism's
    /// step between levels - the run is cut there, rather than drawn up the
    /// step and mitred on it into a hook.</summary>
    private void Chain(int start, int pieces, Basis eye, float w)
    {
        _rim.Clear();
        _rimFlat.Clear();
        _rimCell.Clear();
        int i = start;
        bool cut = false;
        Vector3 end = Vector3.Zero;
        Vector2 endFlat = Vector2.Zero;
        Vector2I endCell = Vector2I.Zero;
        while (true)
        {
            Edge e = _edges[i];
            e.Done = true;
            _edges[i] = e;
            Vector3 from = OnTop(e.Cell, e.A);
            if (_rim.Count > 0 && Mathf.Abs(from.Y - end.Y) > 1e-3f)
            {
                RimAdd(end, endFlat, endCell);
                Run(false, eye, w);
                _rim.Clear();
                _rimFlat.Clear();
                _rimCell.Clear();
                cut = true;
            }
            RimAdd(from, e.A, e.Cell);
            for (int k = 1; k < pieces; k++)
            {
                Vector2 p = e.A.Lerp(e.B, (float)k / pieces);
                RimAdd(OnTop(e.Cell, p), p, e.Cell);
            }
            end = OnTop(e.Cell, e.B);
            endFlat = e.B;
            endCell = e.Cell;
            if (!_edgeFrom.TryGetValue(CornerKey(e.B), out int n) || _edges[n].Done)
            {
                bool closed = !cut && n == start && Mathf.Abs(end.Y - _rim[0].Y) <= 1e-3f;
                if (!closed)
                    RimAdd(end, endFlat, endCell);
                Run(closed, eye, w);
                return;
            }
            i = n;
        }
    }

    private void RimAdd(Vector3 at, Vector2 flat, Vector2I cell)
    {
        _rim.Add(at);
        _rimFlat.Add(flat);
        _rimCell.Add(cell);
    }

    /// <summary>The rim's flat-space points and the cells they are on, beside
    /// <see cref="_rim"/>.</summary>
    private readonly List<Vector2> _rimFlat = new();
    private readonly List<Vector2I> _rimCell = new();

    /// <summary>
    /// A run of the rim in <see cref="_rim"/> made into band triangles, both
    /// sides on the ground that is seen (2026-10-31, the author's notes).
    /// <list type="bullet">
    /// <item>The inner side first is the rim moved <paramref name="w"/> in
    /// the camera's own plane, square to the line as the eye sees it and
    /// mitred at the bends: one width on the screen at any height, the
    /// camera being orthographic.</item>
    /// <item>That point is then slid along the eye's ray on to the ground
    /// seen there (<see cref="Seen"/>): the screen does not see it move. Left
    /// in the camera's plane, it went under the ground on the zone's far
    /// edges, which hid half the band; drawn toward the eye to clear it, the
    /// band hung over the hulls in front; moved inward on the ground, it ran
    /// up a rise's face in wedges and fell behind a crest.</item>
    /// </list>
    /// </summary>
    private void Run(bool closed, Basis eye, float w)
    {
        _rimIn.Clear();
        int count = _rim.Count;
        if (count < 2)
            return;
        Vector3 ex = eye.X.Normalized(), ey = eye.Y.Normalized();
        Vector2 Plane2(Vector3 p) => new(p.Dot(ex), p.Dot(ey));
        // Square to a segment as the eye sees it, on the side of its own
        // cell's middle - judged on the ground's plane, where the eye's
        // projection keeps which side is which, not with the heights: one
        // side for the whole run, from the sum of the ways to the cells'
        // middles, came out at random on a closed ring - the sum nearly
        // nothing - and the band lay outside the zone under a rise (the
        // author's note, 2026-10-31).
        // Which hand the cell is on is read off the plan, and carried to the
        // screen by the projection's own handedness: compared on the screen,
        // a segment up a slope's face turned there by its heights, the side
        // came out the other way for it than for its neighbours, and the band
        // twisted into a loop at the bend (the author's note, the same day).
        float hand = Mathf.Sign(ex.X * ey.Z - ex.Z * ey.X);
        Vector2 Square(int a, int b)
        {
            Vector2 d = Plane2(_rim[b]) - Plane2(_rim[a]);
            if (d.LengthSquared() < 1e-8f)
                return Vector2.Zero;
            Vector2 n = d.Normalized().Orthogonal();
            Vector2 t = _rimFlat[b] - _rimFlat[a];
            Vector2 to = CellFlat(_rimCell[a]) - (_rimFlat[a] + _rimFlat[b]) * 0.5f;
            // the plan's cross, in world x, z (flat y is z squashed)
            float plan = (t.X * to.Y - t.Y * to.X) / (Squash * Squash);
            // Orthogonal() lies on d's negative hand on the screen
            return plan * hand > 0.0f ? -n : n;
        }
        for (int k = 0; k < count; k++)
        {
            int prev = k > 0 ? k - 1 : closed ? count - 1 : -1;
            int next = k + 1 < count ? k + 1 : closed ? 0 : -1;
            Vector2 n0 = prev >= 0 ? Square(prev, k) : Vector2.Zero;
            Vector2 n1 = next >= 0 ? Square(k, next) : Vector2.Zero;
            Vector2 m = Mitre(n0 == Vector2.Zero ? n1 : n0, n1 == Vector2.Zero ? n0 : n1) * w;
            _rimIn.Add(Seen(k, _rim[k] + ex * m.X + ey * m.Y, eye));
        }
        int last = closed ? count : count - 1;
        for (int k = 0; k < last; k++)
        {
            int j = (k + 1) % count;
            _band.Add(_rim[k]); _band.Add(_rim[j]); _band.Add(_rimIn[j]);
            _band.Add(_rim[k]); _band.Add(_rimIn[j]); _band.Add(_rimIn[k]);
        }
    }

    /// <summary>
    /// <paramref name="p"/>, a point by rim point <paramref name="k"/>, slid
    /// along the eye's ray - which does not move it on the screen - on to the
    /// ground there, as <see cref="OnTop"/> lays the rim: a few steps of the
    /// height it is off by. Flat space is read off the rim's own, so the
    /// board's offsets cancel.
    /// </summary>
    private Vector3 Seen(int k, Vector3 p, Basis eye)
    {
        Vector3 back = eye.Z.Normalized();
        float rise = Mathf.Max(back.Y, 0.2f);
        Vector3 rim = _rim[k];
        for (int step = 0; step < 5; step++)
        {
            Vector3 d = p - rim;
            Vector3 g = OnTop(_rimCell[k], _rimFlat[k] + new Vector2(d.X, d.Z * Squash));
            float off = g.Y - p.Y;
            if (Mathf.Abs(off) < 0.05f)
                break;
            p += back * (off / rise);
        }
        return p;
    }

    /// <summary>The way and length, per unit of width, to offset a joint of
    /// two segments with normals <paramref name="n"/> and <paramref name="m"/>.</summary>
    private static Vector2 Mitre(Vector2 n, Vector2 m)
    {
        Vector2 half = n + m;
        if (half.LengthSquared() < 1e-6f)
            return n;
        half = half.Normalized();
        // a sharp turn would send the mitre far off: held to twice the width
        return half / Mathf.Max(0.5f, half.Dot(n));
    }

    /// <summary>A corner's key: the same point reached from two cells.</summary>
    private static Vector2I CornerKey(Vector2 p) => new(Mathf.RoundToInt(p.X * 4.0f), Mathf.RoundToInt(p.Y * 4.0f));


    private MeshInstance3D? _zone;
    private int _zoneBuilt = -1;

    /// <summary>
    /// The zone on the relief (BOT-11): the same tint and outline as
    /// <see cref="Zone"/>, laid on the drawn ground - and on the water's
    /// surface over a water cell, not on its bed.
    ///
    /// <b>Six flat triangles a cell were the prisms' zone</b>, a plane each
    /// face; on the relief their middles cut through the terraces and over a
    /// pond they lay on the bed, seen through the water and torn by its shore.
    /// <b>Then a fan cut in fifths</b>, its corners on the ground: on a hill's
    /// rounded slope the straight pieces between them went under the drawn
    /// ground, and the slope showed untinted in patches (the author's note,
    /// 2026-10-31). Now the zone is the relief's own triangles under each
    /// hexagon, cut to it (<see cref="ReliefCell"/>) - the drawn ground itself,
    /// a nudge toward the eye over it. Built once a reach
    /// (<see cref="_reachVersion"/>), not every frame.
    /// </summary>
    private void ReliefZone(Steps.Reach reach)
    {
        if (_zone is null || _zoneBuilt == _reachVersion)
            return;
        _zoneBuilt = _reachVersion;
        // the cells a move ends on, and the one the tank stands on (ZoneTone)
        _zoneFill.Clear();
        foreach ((Vector2I c, int _) in reach.Cells)
            _zoneFill.Add(c);
        _zoneFill.Add(reach.Start.Cell);
        ReliefZoneBuild(CourseInk);
        // another zone shown on the mesh is built again when it comes back
        _zoneDrawn.Clear();
    }

    /// <summary>The cells and the ink of the zone last put on the relief's
    /// mesh by <see cref="ReliefZoneShow"/> - the ambush's or a bonus act's.</summary>
    private readonly List<Vector2I> _zoneDrawn = new();
    private Color _zoneDrawnInk;

    /// <summary>
    /// <see cref="_zoneFill"/> on the relief as the move's zone is drawn -
    /// the same mesh, the rim moved in by <see cref="ZoneGap"/> and inked by
    /// the shader - in <paramref name="ink"/>: the ambush's axes, a bonus
    /// act's hexes (the author's note, 2026-11-01: those ran to the hexes'
    /// edges, the move's zone stands off them). Built again only when the
    /// cells or the ink change.
    /// </summary>
    private void ReliefZoneShow(Color ink)
    {
        if (_zone is null)
            return;
        _zone.Visible = _zoneFill.Count > 0;
        if (_zoneFill.Count == 0)
            return;
        bool same = ink == _zoneDrawnInk && _zoneDrawn.Count == _zoneFill.Count;
        for (int i = 0; same && i < _zoneFill.Count; i++)
            same = _zoneDrawn[i] == _zoneFill[i];
        if (same)
            return;
        _zoneDrawn.Clear();
        _zoneDrawn.AddRange(_zoneFill);
        _zoneDrawnInk = ink;
        // the move's zone is built again when it comes back
        _zoneBuilt = -1;
        ReliefZoneBuild(ink);
    }

    /// <summary>The relief's zone mesh off <see cref="_zoneFill"/>: the tint
    /// and the moved rim in <paramref name="ink"/>.</summary>
    private void ReliefZoneBuild(Color ink)
    {
        if (_zone is null)
            return;
        // Over the water, said for sure: the course's rung (RingOrder - 1) is
        // the water's own, and on a tie the pond, drawn after, covered the
        // zone and the ribbon laid on its surface. The marks share the rung
        // above, and draw nothing since HUD stage 4.
        if (_course?.MaterialOverride is BaseMaterial3D course)
            course.RenderPriority = Stage3D.RingOrder;
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        Vector2[] corner = Corners();
        _zoneSet.Clear();
        foreach (Vector2I cell in _zoneFill)
            _zoneSet.Add(cell);
        RimLoops(corner);
        if (_zone.MaterialOverride is ShaderMaterial rim)
        {
            if (_rimSegs.Count > RimMax)
                GD.PrintErr($"tank3d: the zone's rim has {_rimSegs.Count} sides, the shader takes {RimMax}");
            var segs = new Vector4[RimMax];
            for (int i = 0; i < _rimSegs.Count && i < RimMax; i++)
                segs[i] = _rimSegs[i];
            rim.SetShaderParameter("rim_seg", segs);
            rim.SetShaderParameter("line_ink", new Color(ink, 0.95f));
            rim.SetShaderParameter("rim_count", Mathf.Min(_rimSegs.Count, RimMax));
            rim.SetShaderParameter("line_w", ZoneLine * _tile!.HexRect.Size.X * 0.5f);
            rim.SetShaderParameter("lift", Lie.Length());
        }
        var tint = new Color(ink, ZoneTone);
        foreach (Vector2I cell in _zoneFill)
            ReliefCell(st, cell, corner, tint);
        _zone.Mesh = st.Commit();
    }

    /// <summary>A flat-space point on the ground's plan, world units.</summary>
    private Vector2 Plan(Vector2 flat) => new(flat.X, flat.Y / Squash);

    /// <summary>A side of the zone's rim on the plan, from <see cref="A"/> to
    /// <see cref="B"/>, <see cref="In"/> toward its cell's middle, moved in by
    /// <see cref="Off"/> (<see cref="RimLoops"/>).</summary>
    private struct RimSide
    {
        public Vector2 A, B, In;
        public float Off;
        public Vector2I Next;
        public bool Done;
    }

    private readonly List<RimSide> _rimSides = new();
    private readonly Dictionary<Vector2I, int> _rimFrom = new();
    private readonly List<int> _rimWalk = new();
    private readonly List<Vector2> _rimCorner = new();
    /// <summary>The moved rim's sides, (a.x, a.z, b.x, b.z) on the plan: the
    /// shader's <c>rim_seg</c>.</summary>
    private readonly List<Vector4> _rimSegs = new();

    /// <summary>The most sides of the moved rim the shader takes.</summary>
    private const int RimMax = 256;

    /// <summary>
    /// The zone's rim moved in, as closed loops on the plan, into
    /// <see cref="_rimSegs"/>: every side by <see cref="ZoneGap"/>, a side
    /// facing a rock off its wall (<see cref="RockInset"/>), and each corner
    /// where a moved side meets the next one's line.
    /// <para>
    /// The cells cut themselves before, each by its own sides moved: where a
    /// side facing a rock, moved far, met a plain one in the next cell, or two
    /// cells by one rock were moved by different amounts, the two cuts met
    /// the side between the cells at two points, and the line stepped and
    /// hooked there (the author's note, 2026-10-31). Joined on the loop, the
    /// sides meet whatever their moves.
    /// </para>
    /// </summary>
    private void RimLoops(Vector2[] corner)
    {
        _rimSides.Clear();
        _rimFrom.Clear();
        _rimSegs.Clear();
        float gap = ZoneGap * _tile!.HexRect.Size.X * 0.5f;
        foreach (Vector2I cell in _zoneFill)
        {
            Vector2 mid = CellFlat(cell);
            float wall = -1.0f;
            foreach (int side in HexField.EdgeHeadings)
            {
                Vector2I next = HexField.Step(cell, side);
                if (_zoneSet.Contains(next))
                    continue;
                int k = EdgeOf(cell, side, corner);
                Vector2 a = mid + corner[k], b = mid + corner[(k + 1) % 6];
                float off = gap;
                if (_field!.FoundationAt(next) == Foundation.Rock)
                {
                    if (wall < 0.0f)
                        wall = RockInset(cell, corner);
                    off = Mathf.Max(gap, wall);
                }
                Vector2 pa = Plan(a), pb = Plan(b);
                Vector2 n = (pb - pa).Orthogonal().Normalized();
                if (n.Dot(Plan(mid) - pa) < 0.0f)
                    n = -n;
                // every cell's corners go round the same way, so a side's end
                // is the next one's start
                _rimFrom[CornerKey(a)] = _rimSides.Count;
                _rimSides.Add(new RimSide { A = pa, B = pb, In = n, Off = off, Next = CornerKey(b) });
            }
        }
        for (int s = 0; s < _rimSides.Count; s++)
        {
            if (_rimSides[s].Done)
                continue;
            _rimWalk.Clear();
            for (int i = s; ;)
            {
                RimSide e = _rimSides[i];
                e.Done = true;
                _rimSides[i] = e;
                _rimWalk.Add(i);
                if (!_rimFrom.TryGetValue(e.Next, out int n) || _rimSides[n].Done)
                    break;
                i = n;
            }
            int count = _rimWalk.Count;
            _rimCorner.Clear();
            for (int k = 0; k < count; k++)
            {
                RimSide e0 = _rimSides[_rimWalk[k]], e1 = _rimSides[_rimWalk[(k + 1) % count]];
                Vector2 p = e0.A + e0.In * e0.Off, q = e1.A + e1.In * e1.Off;
                _rimCorner.Add(Meet(p, e0.B - e0.A, q, e1.B - e1.A, e0.B + (e0.In * e0.Off + e1.In * e1.Off) * 0.5f));
            }
            for (int k = 0; k < count; k++)
            {
                Vector2 p = _rimCorner[(k + count - 1) % count], q = _rimCorner[k];
                _rimSegs.Add(new Vector4(p.X, p.Y, q.X, q.Y));
            }
        }
    }

    /// <summary>Where the line through <paramref name="p"/> along
    /// <paramref name="d"/> meets the one through <paramref name="q"/> along
    /// <paramref name="u"/>; <paramref name="or"/> if they run together.</summary>
    private static Vector2 Meet(Vector2 p, Vector2 d, Vector2 q, Vector2 u, Vector2 or)
    {
        float den = d.Cross(u);
        if (Mathf.Abs(den) < 1e-6f * d.Length() * u.Length())
            return or;
        return p + d * ((q - p).Cross(u) / den);
    }

    /// <summary>A polygon being cut to a hexagon (<see cref="ReliefCell"/>):
    /// points in the relief's x, height, z - kept, the zone being built
    /// once a reach.</summary>
    private readonly List<Vector3> _cutIn = new(), _cutOut = new();
    private readonly Vector2[] _hexXZ = new Vector2[6], _hexIn = new Vector2[6];

    /// <summary>How high over its middle's ground a point of a cell beside a
    /// rock has to stand to be the rock's wall, of a level
    /// (<see cref="RockInset"/>): over the relief's own warp and terraces.</summary>
    private const float RockRise = 0.04f;

    /// <summary>The space left between the wall's foot and the zone's side
    /// moved off it, of a hexagon's circumradius.</summary>
    private const float RockGap = 0.04f;

    /// <summary>How far in from the hexagons' sides the zone's rim lies on
    /// the relief, of a circumradius (the author's suggestion, 2026-10-31):
    /// the same space all round as by a rock, the line off the board's grid
    /// line and off the foot of the slope beyond.</summary>
    private const float ZoneGap = 0.07f;

    /// <summary>The hexagon of <paramref name="cell"/> on the plan, into
    /// <see cref="_hexXZ"/>, its sides' inward normals into
    /// <see cref="_hexIn"/>.</summary>
    private void HexPlan(Vector2I cell, Vector2[] corner)
    {
        Vector2 mid = CellFlat(cell);
        Vector2 centre = Plan(mid);
        for (int k = 0; k < 6; k++)
            _hexXZ[k] = Plan(mid + corner[k]);
        for (int k = 0; k < 6; k++)
        {
            Vector2 h = _hexXZ[k], n = (_hexXZ[(k + 1) % 6] - h).Orthogonal().Normalized();
            _hexIn[k] = n.Dot(centre - h) > 0.0f ? n : -n;
        }
    }

    /// <summary>
    /// How far a rock's wall reaches into <paramref name="cell"/> over its
    /// sides facing a rock, and <see cref="RockGap"/> past that; 0 if it does
    /// not. Its wall spills over the hexagon's side: the rock's, and no cell a
    /// tank drives on (the author's note, 2026-10-31: a mesa's wall tinted,
    /// the outline up its face). The points standing over
    /// <see cref="RockRise"/> above the middle's ground count, by the side
    /// they are nearest (the one they lie past, outside), if it faces a rock.
    /// A point is a triangle's corner, and its triangles carry its height a
    /// step of the grid and more inward: with only the points inside counted,
    /// a wall point just past the side still drew the tint up its face in
    /// teeth. Cut at a height, or at the blurred rock share, the line lay on
    /// the wall's steep foot, where every step of the raster stood up into a
    /// tooth; moved off it, it runs straight along flat ground.
    /// </summary>
    private float RockInset(Vector2I cell, Vector2[] corner)
    {
        Relief r = _relief!;
        HexPlan(cell, corner);
        int rocky = 0;
        foreach (int side in HexField.EdgeHeadings)
            if (_field!.FoundationAt(HexField.Step(cell, side)) == Foundation.Rock)
                rocky |= 1 << EdgeOf(cell, side, corner);
        (int i0, int i1, int j0, int j1) = HexRaster(r);
        float base_ = r.HeightAt(CellFlat(cell)), wall = RockRise * _field!.Lift, carry = r.Cell * 1.5f;
        float inset = 0.0f;
        for (int j = Mathf.Max(j0 - 2, 0); j <= Mathf.Min(j1 + 3, r.Ny - 1); j++)
            for (int i = Mathf.Max(i0 - 2, 0); i <= Mathf.Min(i1 + 3, r.Nx - 1); i++)
            {
                if (r.Height[j * r.Nx + i] - base_ <= wall)
                    continue;
                var at = new Vector2(r.X0 + i * r.Cell, r.Z0 + j * r.Cell);
                int near = -1;
                float least = float.MaxValue;
                for (int k = 0; k < 6; k++)
                {
                    float dist = (at - _hexXZ[k]).Dot(_hexIn[k]);
                    if (dist < least)
                    {
                        least = dist;
                        near = k;
                    }
                }
                if (least > -carry && (rocky & (1 << near)) != 0)
                    inset = Mathf.Max(inset, least + carry);
            }
        return inset > 0.0f ? inset + RockGap * _tile!.HexRect.Size.X * 0.5f : 0.0f;
    }

    /// <summary>The squares of the relief's grid under the hexagon in
    /// <see cref="_hexXZ"/>.</summary>
    private (int I0, int I1, int J0, int J1) HexRaster(Relief r)
    {
        float x0 = float.MaxValue, x1 = float.MinValue, z0 = float.MaxValue, z1 = float.MinValue;
        for (int k = 0; k < 6; k++)
        {
            x0 = Mathf.Min(x0, _hexXZ[k].X);
            x1 = Mathf.Max(x1, _hexXZ[k].X);
            z0 = Mathf.Min(z0, _hexXZ[k].Y);
            z1 = Mathf.Max(z1, _hexXZ[k].Y);
        }
        return (Mathf.Clamp(Mathf.FloorToInt((x0 - r.X0) / r.Cell), 0, r.Nx - 2),
                Mathf.Clamp(Mathf.FloorToInt((x1 - r.X0) / r.Cell), 0, r.Nx - 2),
                Mathf.Clamp(Mathf.FloorToInt((z0 - r.Z0) / r.Cell), 0, r.Ny - 2),
                Mathf.Clamp(Mathf.FloorToInt((z1 - r.Z0) / r.Cell), 0, r.Ny - 2));
    }

    /// <summary>
    /// The relief's triangles under <paramref name="cell"/>'s hexagon, cut to
    /// it, into <paramref name="st"/>: each square of the height grid split
    /// along the diagonal the ground's mesh takes (the gentler one,
    /// Stage3D.BuildRelief), each triangle clipped by the hexagon's six sides
    /// with its height carried on its own plane - so the tint lies on the
    /// ground that is drawn, at every point and not only at its corners. UV
    /// is the point on the plan, world units: the shader keeps what lies
    /// inside the moved rim (<see cref="RimLoops"/>).
    /// </summary>
    private void ReliefCell(SurfaceTool st, Vector2I cell, Vector2[] corner, Color tint)
    {
        Relief r = _relief!;
        HexPlan(cell, corner);
        (int i0, int i1, int j0, int j1) = HexRaster(r);
        bool wet = _field!.IsWater(cell);
        float water = wet ? _field.WaterTop(cell) : 0.0f;
        Vector3 At(int i, int j) => new(r.X0 + i * r.Cell, r.Height[j * r.Nx + i], r.Z0 + j * r.Cell);
        void Put(Vector3 p)
        {
            var flat = new Vector2(p.X, p.Z * Squash);
            float top = wet ? Mathf.Max(p.Y, water) : p.Y;
            if (r.WaterAt(flat) is var sea && !float.IsNaN(sea))
                top = Mathf.Max(top, sea);
            st.SetColor(tint);
            st.SetUV(new Vector2(p.X, p.Z));
            // on the ground itself: the shader lifts it toward the eye (lift)
            st.AddVertex(new Vector3(p.X, top / RiseFactor, p.Z));
        }
        void Cut(Vector3 a, Vector3 b, Vector3 c)
        {
            _cutIn.Clear();
            _cutIn.Add(a);
            _cutIn.Add(b);
            _cutIn.Add(c);
            for (int k = 0; k < 6 && _cutIn.Count >= 3; k++)
            {
                Vector2 h = _hexXZ[k], n = _hexIn[k];
                _cutOut.Clear();
                for (int m = 0; m < _cutIn.Count; m++)
                {
                    Vector3 p = _cutIn[m], q = _cutIn[(m + 1) % _cutIn.Count];
                    float sp = (new Vector2(p.X, p.Z) - h).Dot(n);
                    float sq = (new Vector2(q.X, q.Z) - h).Dot(n);
                    if (sp >= 0.0f)
                        _cutOut.Add(p);
                    if ((sp >= 0.0f) != (sq >= 0.0f))
                        _cutOut.Add(p.Lerp(q, sp / (sp - sq)));
                }
                _cutIn.Clear();
                _cutIn.AddRange(_cutOut);
            }
            for (int m = 1; m + 1 < _cutIn.Count; m++)
            {
                Put(_cutIn[0]);
                Put(_cutIn[m]);
                Put(_cutIn[m + 1]);
            }
        }
        for (int j = j0; j <= j1; j++)
            for (int i = i0; i <= i1; i++)
            {
                Vector3 a = At(i, j), b = At(i + 1, j), c = At(i, j + 1), d = At(i + 1, j + 1);
                if (Mathf.Abs(a.Y - d.Y) <= Mathf.Abs(b.Y - c.Y))
                {
                    Cut(a, b, d);
                    Cut(a, d, c);
                }
                else
                {
                    Cut(a, b, c);
                    Cut(b, d, c);
                }
            }
    }

    /// <summary>
    /// The relief's zone: its tint, and the outline drawn by the tint's own
    /// fragments - those nearer the moved rim than <c>line_w</c> as the
    /// screen sees it (2026-10-31, the author's notes: the band was high over
    /// the ground, of other widths, twisted into loops at a cliff's bends).
    /// <list type="bullet">
    /// <item>The band was a mesh of its own, offset in the camera's plane -
    /// and the zone is built once a reach, so when the camera turned the band
    /// kept the old view's offsets and broke into blobs; on a cliff the rim
    /// laid on the ground jumped between the top and the foot, and no offset
    /// of that line on the screen lies flat.</item>
    /// <item>Here the line is part of the ground's tint: each fragment knows
    /// where it is on the plan (UV), keeps itself if it lies inside the moved
    /// rim's loops (<c>rim_seg</c>, a crossing count), finds the plan
    /// distance to their nearest side, and turns it into pixels by how fast
    /// it changes on the screen - so it lies on whatever ground is drawn and
    /// is as wide everywhere and from any view.</item>
    /// </list>
    /// </summary>
    private const string ZoneShader = @"
shader_type spatial;
render_mode unshaded, cull_disabled, blend_mix;

uniform vec4 line_ink;
uniform float line_w = 0.05;
uniform float lift = 0.0;
uniform vec4 rim_seg[256];
uniform int rim_count = 0;

void vertex() {
    // toward the eye along its own ray, however the camera has turned since
    // the mesh was built - the orthographic screen does not see it move
    vec4 v = MODELVIEW_MATRIX * vec4(VERTEX, 1.0);
    v.z += lift;
    POSITION = PROJECTION_MATRIX * v;
}

void fragment() {
    vec2 p = UV;
    float d = 1e9;
    bool inside = false;
    for (int i = 0; i < rim_count; i++) {
        vec4 s = rim_seg[i];
        vec2 a = s.xy, ab = s.zw - s.xy;
        float t = clamp(dot(p - a, ab) / max(dot(ab, ab), 1e-6), 0.0, 1.0);
        d = min(d, length(p - a - ab * t));
        if ((s.y > p.y) != (s.w > p.y) && p.x < a.x + ab.x * (p.y - s.y) / ab.y)
            inside = !inside;
    }
    // plan units a pixel here, and the line's width in pixels: the camera is
    // orthographic, so the view's x a pixel across is world units a pixel
    float g = max(length(vec2(dFdx(d), dFdy(d))), 1e-6);
    float wide = line_w / max(abs(dFdx(VERTEX.x)), 1e-6);
    if (!inside)
        discard;
    float on = 1.0 - smoothstep(wide - 0.7, wide + 0.7, d / g);
    ALBEDO = mix(COLOR.rgb, line_ink.rgb, on);
    ALPHA = mix(COLOR.a, line_ink.a, on);
}
";

    /// <summary><see cref="OnTop"/> on the relief: the drawn ground, or the
    /// water's surface where water is drawn over it - a water cell's own, and
    /// also the water the relief lays over a dry cell's ground: a dry pit
    /// under the sea's level is drawn flooded, its bowl tens of px under the
    /// surface, and the outline laid on the bowl bent out of shape under the
    /// water (the author's note, 2026-10-29; drill10's (3, 0)).</summary>
    private Vector3 OnGround(Vector2I cell, Vector2 flat)
    {
        float top = _field!.TopAtPoint(flat);
        if (_field.IsWater(cell))
            top = Mathf.Max(top, _field.WaterTop(cell));
        if (_relief?.WaterAt(flat) is float wet && !float.IsNaN(wet))
            top = Mathf.Max(top, wet);
        return new Vector3(flat.X, top / RiseFactor, flat.Y / Squash) + Lie;
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
        if (_courseInk is not { } ink)
            return;
        DrawWay(ink);
        // The roses over the way, and on their way out after it: an order
        // given or a cell let go hides the course at once, the rose fades
        // (Tank3DBench.Spin, RoseFadeTime).
        if (_field is null || _tile is null)
            return;
        DrawRose(ink);
        DrawSpin(ink);
    }

    private void DrawWay(Control ink)
    {
        if (!CourseShown || _reach is null)
            return;
        Font font = ThemeDB.FallbackFont;
        float hex = (Screen(_reach.Start.Cell) - _camera.UnprojectPosition(
            OnTop(_reach.Start.Cell, CellFlat(_reach.Start.Cell) + new Vector2(_tile!.HexRect.Size.X * 0.5f, 0)))).Length();
        float r = Mathf.Clamp(hex * 0.16f, 8.0f, 16.0f);
        int size = Mathf.RoundToInt(r * 1.15f);

        foreach (Vector2I cell in _reach.Cells.Keys)
            // a mine is no stop sign (2026-10-29, the author's note): it is
            // driven on to and goes off - the ghost on it shows that
            // (Tank3DBench.Ghost, GhostMined), the mine is seen on the board;
            // nor a wood (2026-10-31): it stands drawn through in the reach
            // (Tank3DBench.Wood, WoodSee), and the sign lay on its crowns
            if ((Steps.Stops(_field!, cell, _reach.Mover) || (_field!.IsDeep(cell) && !RigSnorkel))
                && _field.CoverAt(cell) != Cover.Forest)
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
            (Vector2I cell, int spent, _) = stops[i];
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
            // no "+N пов." pill where the way turns (the author's note,
            // 2026-10-29): the chevrons bend there, and the next disc's
            // count takes in the turn's steps
        }
        // The way's risk - a mine, deep water, fire - is the ghost's to show
        // and the sign's over it (Tank3DBench.Ghost, Tank3DBench.Badges), not
        // a word at the end (the author's note, 2026-10-29).
        Vector2 end = Screen(way.End.Cell);
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
