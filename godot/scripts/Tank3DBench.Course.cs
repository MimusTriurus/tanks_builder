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
        // the relief's zone: its own mesh, built when the reach changes
        _zone = new MeshInstance3D
        {
            Name = "Zone",
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            MaterialOverride = _course.MaterialOverride,
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
        if (_zone is not null)
            _zone.Visible = CourseShown;
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
            Steps.Mover mover = Steps.Mover.Of(_profile, _amphibious, _reachBarred);
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
        GhostTick(shown);
        // the unit's card lights the steps it spends (Tank3DBench.Card)
        _wayCost = shown is null ? 0 : shown.Spent + (_reach!.TurnTo(shown, GhostFace(shown)) ?? 0);
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
        _relief is not null ? OnGround(cell, flat)
        : new Vector3(flat.X, TopOrWater(cell, flat) / RiseFactor, flat.Y / Squash)
          + Stage3D.Clear(Squash, RiseFactor) + _camera.GlobalBasis.Z * MarkNudge * HexWidth;

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
        _edges.Clear();
        _edgeFrom.Clear();
        _edgeTo.Clear();
        _band.Clear();
        // the cells a move ends on and the one the tank stands on - filled
        // with them (ZoneTone), it is ringed with them: before, the edges
        // facing it were skipped and its own never walked, and the outline
        // broke off on every side of it toward a tank or a wall
        _zoneFill.Clear();
        foreach ((Vector2I c, int _) in reach.Cells)
            _zoneFill.Add(c);
        _zoneFill.Add(reach.Start.Cell);
        foreach (Vector2I cell in _zoneFill)
        {
            Vector2 mid = CellFlat(cell);
            foreach (int side in HexField.EdgeHeadings)
            {
                Vector2I next = HexField.Step(cell, side);
                if (reach.Reaches(next) || next == reach.Start.Cell)
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
    /// rim on the ground, its inner side a width off it in the camera's plane.
    /// Where two edges meet at two heights - a prism's step between levels -
    /// the run is cut there, rather than drawn up the step and mitred on it
    /// into a hook.</summary>
    private void Chain(int start, int pieces, Basis eye, float w)
    {
        _rim.Clear();
        int i = start;
        bool cut = false;
        Vector3 inward = Vector3.Zero;
        Vector3 end = Vector3.Zero;
        while (true)
        {
            Edge e = _edges[i];
            e.Done = true;
            _edges[i] = e;
            Vector3 from = OnTop(e.Cell, e.A);
            if (_rim.Count > 0 && Mathf.Abs(from.Y - end.Y) > 1e-3f)
            {
                _rim.Add(end);
                Run(false, inward, eye, w);
                _rim.Clear();
                inward = Vector3.Zero;
                cut = true;
            }
            inward += new Vector3(e.In.X, 0.0f, e.In.Y / Squash);
            _rim.Add(from);
            for (int k = 1; k < pieces; k++)
                _rim.Add(OnTop(e.Cell, e.A.Lerp(e.B, (float)k / pieces)));
            end = OnTop(e.Cell, e.B);
            if (!_edgeFrom.TryGetValue(CornerKey(e.B), out int n) || _edges[n].Done)
            {
                bool closed = !cut && n == start && Mathf.Abs(end.Y - _rim[0].Y) <= 1e-3f;
                if (!closed)
                    _rim.Add(end);
                Run(closed, inward, eye, w);
                return;
            }
            i = n;
        }
    }

    /// <summary>A run of the rim in <see cref="_rim"/> made into band triangles.</summary>
    private void Run(bool closed, Vector3 inward, Basis eye, float w)
    {
        _rimIn.Clear();
        int count = _rim.Count;
        if (count < 2)
            return;
        // the line on the camera's plane, and which of its sides is inside
        Vector2 Plane(Vector3 p) => new(p.Dot(eye.X), p.Dot(eye.Y));
        Vector2 Square(int a, int b)
        {
            Vector2 d = Plane(_rim[b]) - Plane(_rim[a]);
            return d.LengthSquared() < 1e-8f ? Vector2.Zero : d.Normalized().Orthogonal();
        }
        float sign = Square(0, 1).Dot(Plane(inward)) >= 0.0f ? 1.0f : -1.0f;
        for (int k = 0; k < count; k++)
        {
            int prev = k > 0 ? k - 1 : closed ? count - 1 : -1;
            int next = k + 1 < count ? k + 1 : closed ? 0 : -1;
            Vector2 n0 = prev >= 0 ? Square(prev, k) : Vector2.Zero;
            Vector2 n1 = next >= 0 ? Square(k, next) : Vector2.Zero;
            Vector2 m = Mitre(n0 == Vector2.Zero ? n1 : n0, n1 == Vector2.Zero ? n0 : n1) * (w * sign);
            _rimIn.Add(_rim[k] + eye.X * m.X + eye.Y * m.Y);
        }
        int last = closed ? count : count - 1;
        for (int k = 0; k < last; k++)
        {
            int j = (k + 1) % count;
            _band.Add(_rim[k]); _band.Add(_rim[j]); _band.Add(_rimIn[j]);
            _band.Add(_rim[k]); _band.Add(_rimIn[j]); _band.Add(_rimIn[k]);
        }
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

    /// <summary>How many pieces a hexagon's fan triangle and an outline edge
    /// are cut into on the relief: enough for the tint to follow a terrace.</summary>
    private const int ZoneCuts = 5;

    /// <summary>
    /// The zone on the relief (BOT-11): the same tint and outline as
    /// <see cref="Zone"/>, cut fine and laid on the drawn ground - and on the
    /// water's surface over a water cell, not on its bed.
    ///
    /// <b>Six flat triangles a cell were the prisms' zone</b>, a plane each
    /// face; on the relief their middles cut through the terraces and over a
    /// pond they lay on the bed, seen through the water and torn by its shore.
    /// Built once a reach (<see cref="_reachVersion"/>), not every frame: a
    /// few thousand triangles.
    /// </summary>
    private void ReliefZone(Steps.Reach reach)
    {
        if (_zone is null || _zoneBuilt == _reachVersion)
            return;
        _zoneBuilt = _reachVersion;
        // Over the water, said for sure: the course's rung (RingOrder - 1) is
        // the water's own, and on a tie the pond, drawn after, covered the
        // zone and the ribbon laid on its surface. The marks share the rung
        // above, and draw nothing since HUD stage 4.
        if (_zone.MaterialOverride is BaseMaterial3D ink)
            ink.RenderPriority = Stage3D.RingOrder;
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        Vector2[] corner = Corners();
        void Put(Color ink, Vector2I cell, Vector2 p)
        {
            st.SetColor(ink);
            st.AddVertex(OnGround(cell, p));
        }
        var tint = new Color(CourseInk, ZoneTone);
        // the cells a move ends on, and the one the tank stands on (ZoneTone)
        _zoneFill.Clear();
        foreach ((Vector2I c, int _) in reach.Cells)
            _zoneFill.Add(c);
        _zoneFill.Add(reach.Start.Cell);
        foreach (Vector2I cell in _zoneFill)
        {
            Vector2 mid = CellFlat(cell);
            for (int i = 0; i < 6; i++)
            {
                Vector2 a = corner[i], b = corner[(i + 1) % 6];
                // the fan triangle (mid, a, b) cut into ZoneCuts² pieces
                for (int u = 0; u < ZoneCuts; u++)
                    for (int v = 0; u + v < ZoneCuts; v++)
                    {
                        Vector2 P(float s, float t) => mid + a * (s / ZoneCuts) + b * (t / ZoneCuts);
                        Put(tint, cell, P(u, v)); Put(tint, cell, P(u + 1, v)); Put(tint, cell, P(u, v + 1));
                        if (u + v + 2 <= ZoneCuts)
                        {
                            Put(tint, cell, P(u + 1, v)); Put(tint, cell, P(u + 1, v + 1)); Put(tint, cell, P(u, v + 1));
                        }
                    }
            }
        }
        var line = new Color(CourseInk, 0.95f);
        ZoneBand(reach, corner, ZoneCuts);
        foreach (Vector3 p in _band)
        {
            st.SetColor(line);
            st.AddVertex(p);
        }
        _zone.Mesh = st.Commit();
    }

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
        return new Vector3(flat.X, top / RiseFactor, flat.Y / Squash)
               + Stage3D.Clear(Squash, RiseFactor) + _camera.GlobalBasis.Z * MarkNudge * HexWidth;
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
            // a mine is no stop sign (2026-10-29, the author's note): it is
            // driven on to and goes off - the ghost on it shows that
            // (Tank3DBench.Ghost, GhostMined), the mine is seen on the board
            if (Steps.Stops(_field!, cell) || (_field!.IsDeep(cell) && !_amphibious))
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
        // The way's risk - a mine, deep water, fire - is the ghost's to show
        // and the sign's over it (Tank3DBench.Ghost, Tank3DBench.Badges), not
        // a word at the end (the author's note, 2026-10-29).
        Vector2 end = Screen(way.End.Cell);
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
