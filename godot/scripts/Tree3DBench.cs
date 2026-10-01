using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// <c>res://Tree3D.tscn</c>: the generator's tree models (<c>pipeline/tree_gen.py</c>,
/// <c>tree.glb</c> + <c>tree.json</c>) on the volumetric board, under
/// <see cref="Toon"/>, and nothing else on it: no sprite wood, bushes or stones -
/// the board's undergrowth is sprite art, and only the models are looked at here.
///
/// <b>Tank3D's board, camera, sun and shadow skin, and nothing of its tank</b>:
/// the tree models are what is being looked at, and Tank3D cannot stand up
/// without a tank in <c>Models/</c>.
///
/// <b>A rammed tree goes over</b> (docs/props.md, "Tree3D: падение от тарана"):
/// a stand-in hull drives into it from a side, and it falls away from it on
/// the sprite's pendulum (<c>Grove.Timber</c>) with the model's own crown spring
/// and lie from <c>tree.json</c> (<c>model.fall</c>), posed by
/// <see cref="Toon.FallCode"/>. Keys <c>1</c>-<c>8</c> ram the tree under the
/// cursor to a compass point of the screen (1 away from the eye, 3 right, 5 at
/// the eye, 7 left), <c>--ram i:k,..</c> the same at the start; RMB pushes the
/// tree nearest the click over away from it.
///
/// <b>The wood burns</b> (docs/props.md, "Tree3D: лесной пожар"): the board's
/// own <see cref="Wildfire"/> - cells, spread, each tree's stagger and its
/// <see cref="Wildfire.Coat"/> - with the model burning itself by its burn
/// contract (<see cref="Toon.BurnCode"/>) and a 3D tank's fire and smoke
/// (<see cref="CelBurn"/>) in its crown. LMB lights the cell under the cursor,
/// <c>R</c> puts the wood back.
/// </summary>
public sealed partial class Tree3DBench : Node3D
{
    // --- flags -----------------------------------------------------------

    /// <summary><c>--trees A,B,..</c>: which folders under
    /// <c>assets/Models/Trees/</c> (<see cref="AssetRoot.Trees"/>) to stand, in
    /// the order of <see cref="Spots"/>.</summary>
    private string[] _trees = { "Oak_001", "Oak_002", "Oak_003", "Oak_004", "Oak_005", "Oak_006" };
    private float _zoom = 2.2f;
    private bool _pbr;
    /// <summary><c>--no-outline</c>: the crowns without their line, to hold
    /// against it.</summary>
    private bool _noOutline;
    private bool _row;
    /// <summary><c>--wood N</c>: a wooded cell instead of the spots - N models
    /// (the <c>--trees</c> in turn) on the middle cell, each turned its own way,
    /// for a hull to drive through (<see cref="WoodSpots"/>).</summary>
    private int _wood;
    private static readonly Vector2I WoodCell = new(2, 1);
    /// <summary><c>--grass</c>: the middle row as four ways to do grass, left
    /// to right (<see cref="Grass3D.Kind"/>), a tree on each - the first of
    /// <c>--trees</c> on all four, so only the grass differs. The view is
    /// drawn back to take the four in (<see cref="MeadowZoom"/>).</summary>
    private bool _grassy;
    /// <summary>Each meadow's number in <see cref="_grass"/>, and where its
    /// front starts once it is lit (null before).</summary>
    private readonly List<int> _meadowIds = new();
    private readonly List<Vector2?> _meadowFrom = new();
    /// <summary>Where LMB lit a cell, so a meadow lit by hand burns out from
    /// the click.</summary>
    private readonly Dictionary<Vector2I, Vector2> _litAt = new();
    /// <summary>Where a grass front crossed into a cell from its neighbour -
    /// the point on their shared edge it got to first - for that cell's own
    /// front to start from (<see cref="Spread"/>).</summary>
    private readonly Dictionary<Vector2I, Vector2> _entry = new();
    /// <summary>Which meadow each cell of grass is, for <see cref="Spread"/>.</summary>
    private readonly Dictionary<Vector2I, int> _meadowAt = new();

    /// <summary><c>--no-grass</c>: the board bare, as it was before the grass.
    /// Otherwise every cell is grass of <see cref="BoardGrass"/>.</summary>
    private bool _bare;
    /// <summary>The grass the board wears when it is not comparing kinds: the
    /// tufts on the cel ramp, the models' light (docs/props.md, "Трава").</summary>
    private const Grass3D.Kind BoardGrass = Grass3D.Kind.CelTufts;
    private readonly List<Vector2I> _meadowCells = new();
    private const float MeadowZoom = 1.85f;
    private bool _zoomed;
    private Grass3D? _grass;
    private static readonly (Vector2I Cell, Grass3D.Kind Kind, string Name)[] Meadows =
    {
        (new Vector2I(1, 1), Grass3D.Kind.Painted, "A: painted"),
        (new Vector2I(2, 1), Grass3D.Kind.Shells, "B: shells"),
        (new Vector2I(3, 1), Grass3D.Kind.PaintedTufts, "A+C: painted + tufts"),
        (new Vector2I(4, 1), Grass3D.Kind.CelTufts, "A+C cel: tufts on the Toon ramp"),
    };
    /// <summary>Where the tree stands on a grass cell: back and to the left,
    /// so the grass in front of it and the way it falls are open.</summary>
    private static readonly Vector2 MeadowTree = new(-38.0f, -22.0f);
    /// <summary><c>--drive</c>: a hull across the three meadows at
    /// <c>--ram-at</c>, left to right in front of the trees - one lane over all
    /// three, to hold their tracks side by side. <c>T</c> sends another.</summary>
    private bool _drive;
    private string? _capturePath;
    private int _captureAt = 30;
    /// <summary><c>--burn q,r</c>: the cell lit <see cref="LightAfter"/> s in,
    /// so a capture has a start to count from.</summary>
    private Vector2I? _burnAt;
    /// <summary><c>--ram i:k,..</c>: tree <c>i</c> rammed toward compass point
    /// <c>k</c> (<see cref="Compass"/>) at <c>--ram-at</c> s (<see cref="LightAfter"/>).</summary>
    private readonly List<(int Tree, int Point)> _ramAt = new();
    /// <summary><c>--fell i:k,..</c>: the same trees knocked over with no hull
    /// - what comes out of the ground, with nothing standing on it.</summary>
    private readonly List<(int Tree, int Point)> _fellAt = new();
    private float _ramWhen = LightAfter;
    /// <summary><c>--rmb x,y</c>: a right click at screen px (x, y) at
    /// <c>--ram-at</c> - what RMB does, for a capture.</summary>
    private Vector2? _rmbAt;
    /// <summary><c>--record dir</c>: every <c>--record-every</c> th frame to
    /// <c>dir/NNNN.png</c> for <c>--record-for</c> s, then quit - a fire to
    /// be looked at as a film (with <c>--fixed-fps</c>).</summary>
    private string? _recordDir;
    private int _recordEvery = 6;
    private float _recordFor = 18.0f;
    /// <summary><c>--record-from s</c>: nothing saved before this, for every
    /// frame of a short stretch late in a fire.</summary>
    private float _recordFrom;
    private const float LightAfter = 0.5f;
    private float _clock;

    // --- scene -----------------------------------------------------------

    private Camera3D _camera = null!;
    private DirectionalLight3D _sun = null!;
    private AtlasSet? _tile;
    private HexField? _field;
    private Stage3D? _stage;
    private readonly List<Node3D> _models = new();
    private int _frame;

    private float Squash => _tile is { } a && a.HexRect.Size.X > 0
        ? 2.0f * a.HexRect.Size.Y / (Mathf.Sqrt(3.0f) * a.HexRect.Size.X)
        : 0.5f;

    private float RiseFactor => Mathf.Sqrt(Mathf.Max(0.0f, 1.0f - Squash * Squash));

    /// <summary>The camera's stand-off, Tank3D's and for its reason: an
    /// orthographic depth is linear, and at 4000 the board's rims drowned in it.</summary>
    private const float Back = 1500.0f;

    /// <summary>The board: five by three of plain ground, flat - its own map,
    /// because every named board carries water, relief or a wood.</summary>
    private static readonly string[] Ground =
    {
        // 01234
        ".....", // r0
        ".....", // r1
        ".....", // r2
    };

    /// <summary>Where each model stands: a cell and an offset from its centre in
    /// ground px (x right, y down the screen). One alone on each side of the
    /// middle, one in the middle, three on one cell as a wood carries several,
    /// one on the far row.</summary>
    private static readonly (Vector2I Cell, Vector2 Off)[] Spots =
    {
        (new Vector2I(1, 1), Vector2.Zero),
        (new Vector2I(3, 1), new Vector2(-40.0f, -22.0f)),
        (new Vector2I(3, 1), new Vector2(42.0f, -12.0f)),
        (new Vector2I(3, 1), new Vector2(0.0f, 30.0f)),
        (new Vector2I(2, 0), Vector2.Zero),
        (new Vector2I(2, 1), Vector2.Zero),
    };

    /// <summary><c>--row</c>: three models in a row across the middle cell, left
    /// to right, a crown and a little apart - the looks of one seed held side
    /// by side (<c>tree_gen.remodel</c>).</summary>
    private static readonly (Vector2I Cell, Vector2 Off)[] Row =
    {
        (new Vector2I(2, 1), new Vector2(-125.0f, 0.0f)),
        (new Vector2I(2, 1), Vector2.Zero),
        (new Vector2I(2, 1), new Vector2(125.0f, 0.0f)),
    };

    /// <summary>Where the trees of a wooded cell stand, ground px from its
    /// middle (y squashed as the screen has it, <see cref="Stand"/>'s): one in
    /// the middle, the rest on a ring 60-80 px out, broken up by a hash - the
    /// crowns overlap as a wood's do.</summary>
    private Vector2[] WoodSpots(int n)
    {
        var at = new Vector2[n];
        for (int i = 1; i < n; i++)
        {
            float a = Mathf.Tau * (i - 1) / Mathf.Max(n - 1, 1) + 0.4f + 0.35f * CelPuff.Hash(i, 613);
            float r = 60.0f + 20.0f * CelPuff.Hash(i, 617);
            at[i] = new Vector2(Mathf.Cos(a) * r, Mathf.Sin(a) * r * Squash);
        }
        return at;
    }

    public override void _Ready()
    {
        ReadFlags();
        if (_grassy && !_zoomed)
            _zoom = MeadowZoom;
        _tile = LoadTile("MTP");
        GetViewport().Msaa3D = Viewport.Msaa.Msaa4X;
        BuildWorld();
        BuildBoard();
        BuildCamera();
        if (!_pbr && !_noOutline)
            BuildOutline();
        if (_grassy)
        {
            foreach ((Vector2I cell, _, _) in Meadows)
                Stand(_trees[0], cell, MeadowTree);
        }
        else if (_wood > 0)
        {
            Vector2[] wood = WoodSpots(_wood);
            for (int i = 0; i < wood.Length; i++)
                Stand(_trees[i % _trees.Length], WoodCell, wood[i],
                      Mathf.Tau * CelPuff.Hash(i, 611));
        }
        else
        {
            (Vector2I Cell, Vector2 Off)[] spots = _row ? Row : Spots;
            for (int i = 0; i < Math.Min(_trees.Length, spots.Length); i++)
                Stand(_trees[i], spots[i].Cell, spots[i].Off);
        }
        // after the trees: the tufts keep off their trunks
        if (_grassy || !_bare)
            BuildGrass();
        FrameCamera();
        if (_stage is not null && _fire is not null)
            _stage.Blaze = _fire;
        GD.Print("tree3d: LMB lights a cell, RMB pushes the tree nearest it over away from the click, "
                 + "1-8 ram it to a compass point, " + (_grassy ? "T drives hulls over the grass, " : "")
                 + "W wind, R puts it all back");
    }

    public override void _Process(double delta)
    {
        // The stage's per-frame pass (Tank3D never calls it): nothing of it is
        // needed while the board has no wood and no vehicles, but a board is
        // left as the stage expects to run.
        _stage?.Place(Array.Empty<Vehicle>());
        FrameCamera();
        Burn((float)delta);
        Topple((float)delta);
        Sway((float)delta);
        Trample();
        _frame++;
        if (_recordDir is not null && _frame % _recordEvery == 0 && _clock >= _recordFrom)
        {
            GetViewport().GetTexture().GetImage().SavePng($"{_recordDir}/{_frame / _recordEvery:D4}.png");
            if (_clock >= _recordFor)
                GetTree().Quit();
        }
        if (_capturePath is not null && _frame == _captureAt)
        {
            Error err = GetViewport().GetTexture().GetImage().SavePng(_capturePath);
            GD.Print(err == Error.Ok ? $"capture: {_capturePath}" : $"capture to {_capturePath} failed: {err}");
            GetTree().Quit();
        }
    }

    // --- the trees ---------------------------------------------------------

    /// <summary>
    /// One model on the board (<see cref="TreeModel.Load"/>: read, dressed by
    /// <see cref="Toon"/>, as tall as its own sprite) set down on the ground at
    /// its foot, into the crowns' mask, with its fire and its fall.
    /// </summary>
    private void Stand(string name, Vector2I cell, Vector2 off, float yaw = 0.0f)
    {
        ulong t0 = Time.GetTicksMsec();
        if (TreeModel.Load(name, _pbr, out string? error) is not { } model)
        {
            GD.Print($"tree3d: {error}");
            return;
        }
        Node scene = model.Scene;
        JsonElement j = model.Json;
        float ppm = model.Ppm;
        var tree = new TreeFire { Cell = cell };
        tree.Burn.AddRange(model.Burn);
        tree.Leaves.AddRange(model.Leaves);
        _outline?.Ghost(scene, tree.Burn, tree.Leaves);
        var holder = new Node3D
        {
            Name = $"{name}.{_models.Count}", Scale = Vector3.One * ppm,
            Rotation = new Vector3(0.0f, yaw, 0.0f),
        };
        holder.AddChild(scene);
        AddChild(holder);
        if (_field is not null)
        {
            Vector2 flat = _field.FlatAnchor(cell) + _field.CentreOffset + off;
            holder.Position = Foot(new Vector3(flat.X, 0.0f, flat.Y / Squash));
        }
        _models.Add(holder);
        tree.Holder = holder;
        float h = model.Height, w = model.Width, d = model.Depth;
        foreach (Vector3 p in TreeModel.Seats)
            tree.Seats.Add(new Vector3(p.X * w, p.Y * h, p.Z * d));
        Vector2 at = _field is null ? Vector2.Zero : _field.FlatAnchor(cell) + _field.CentreOffset + off;
        tree.Stagger = tree.Hashed = TreeModel.StaggerOf(at);
        tree.Fire = TreeModel.Fire(this, name, model);
        tree.Fall = TreeFall.For(model, holder, tree.Burn, this);
        (tree.SwayPhase, tree.SwayRate) = TreeModel.SwayOf(at);
        tree.Ppm = ppm;
        tree.Height = h;
        tree.Width = w;
        TreeModel.Margin(holder);
        _burning.Add(tree);
        _wooded.Add(cell);
        GD.Print($"tree3d: {name} on {cell} +{off}, {ppm:F2} px/m, "
                 + $"{j.GetProperty("height").GetSingle() * ppm:F0} px tall, "
                 + $"{j.GetProperty("model").GetProperty("tris").GetInt32()} tris, "
                 + $"dressed in {Time.GetTicksMsec() - t0} ms");
    }

    // --- the fire ------------------------------------------------------------

    /// <summary>One model on fire: its cell, how late it catches, the
    /// materials its burn goes into, and its flame and smoke.</summary>
    private sealed class TreeFire
    {
        public Node3D Holder = null!;
        public Vector2I Cell;
        public float Stagger;
        /// <summary>The stagger off where it stands, for a cell with no grass
        /// front to catch from - and to go back to when the wood is put back.</summary>
        public float Hashed;
        public readonly List<ShaderMaterial> Burn = new();
        /// <summary>The leaves' cel material and their mask: the flutter.</summary>
        public readonly List<ShaderMaterial> Leaves = new();
        public float SwayPhase, SwayRate;
        public readonly List<Vector3> Seats = new();
        public CelBurn Fire = null!;
        public readonly List<Vector3> Ports = new();
        // the fall (TreeFall)
        public TreeFall Fall = null!;
        public float Ppm, Height, Width;
        public bool Burnt, Burning;
        public float Spent;
    }

    private readonly List<TreeFire> _burning = new();
    private readonly HashSet<Vector2I> _wooded = new();
    private Wildfire? _fire;

    private void Burn(float dt)
    {
        _clock += dt;
        if (_fire is null)
            return;
        if (_burnAt is Vector2I lit && _clock >= LightAfter)
        {
            _fire.Light(lit);
            _burnAt = null;
        }
        _fire.Tick(dt);
        Spread();
        Scorch();
        Basis eye = _camera.GlobalBasis;
        foreach (TreeFire t in _burning)
        {
            Wildfire.Coat coat = _fire.Of(t.Cell, t.Stagger);
            // The crown goes with the fuel (Coat.Spent: between the flame
            // coming up and the sprite's handover shutting), the paint with the
            // char - the two clocks the sprite's pair has.
            foreach (ShaderMaterial m in t.Burn)
            {
                m.SetShaderParameter("burn", coat.Spent);
                m.SetShaderParameter("charred", coat.Char);
            }
            t.Fire.Fire = coat.Flame;
            t.Fire.Smoke = coat.Smoke;
            t.Fire.Smoulder = coat.Burnt;
            // The flame goes down with the crown: from the crown's front onto
            // the limbs and the fork as the fuel goes - left where the crown
            // was, it hung in the air round the bare twigs.
            float down = coat.Spent * coat.Spent;
            t.Burnt = coat.Burnt || coat.Spent >= 1.0f;
            t.Spent = coat.Spent;
            t.Burning = coat.Flame > 0.01f;
            t.Ports.Clear();
            foreach (Vector3 p in t.Seats)
                t.Ports.Add(t.Holder.ToGlobal(t.Fall.Pose(p.Lerp(new Vector3(p.X * 0.4f, p.Y * 0.62f, p.Z * 0.3f), down))));
            t.Fire.Tick(dt, t.Ports, eye);
        }
    }

    private void Douse()
    {
        _fire?.Douse();
        _entry.Clear();
        _clock = 0.0f;
        foreach (TreeFire t in _burning)
        {
            t.Fire.Reset();
            t.Fall.Reset();
        }
        foreach (Ram r in _rams)
            r.Body.QueueFree();
        _rams.Clear();
        _dust?.Clear();
        _grass?.Heal();
        Burn(0.0f);
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventKey { Pressed: true, Echo: false, Keycode: Key.R })
            Douse();
        else if (e is InputEventKey { Pressed: true, Echo: false, Keycode: Key.T } && _grassy)
            DriveAcross();
        else if (e is InputEventKey { Pressed: true, Echo: false, Keycode: Key.W })
            GD.Print($"tree3d: wind {((_windOn = !_windOn) ? "on" : "off")}");
        else if (e is InputEventKey { Pressed: true, Echo: false } k
                 && k.Keycode >= Key.Key1 && k.Keycode <= Key.Key8)
        {
            TreeFire? t = Nearest(GetViewport().GetMousePosition());
            if (t is not null)
                RamInto(t, (int)(k.Keycode - Key.Key1) + 1);
        }
        else if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right } push)
        {
            // Pushed over from where the click is: a hull comes from that side
            // and the tree falls away from it. On the trunk itself, to the right.
            TreeFire? t = Nearest(push.Position);
            if (t is null || GroundAt(push.Position) is not { } at)
                return;
            Vector3 foot = t.Holder.GlobalPosition;
            var dir = new Vector2(foot.X - at.X, foot.Z - at.Z);
            RamInto(t, dir.Length() < 6.0f ? Vector2.Right : dir);
        }
        else if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } b
                 && _field is not null && _fire is not null)
        {
            if (GroundAt(b.Position) is not { } g)
                return;
            Vector2I cell = _field.CellUnder(new Vector2(g.X, g.Z * Squash));
            if (_fire.Light(cell))
            {
                _litAt[cell] = new Vector2(g.X, g.Z);
                GD.Print($"tree3d: {cell} lit");
            }
            else
                GD.Print($"tree3d: {cell} has nothing to burn");
        }
    }

    /// <summary>Where a screen point lands on the ground plane, if it does.</summary>
    private Vector3? GroundAt(Vector2 screen)
    {
        Vector3 from = _camera.ProjectRayOrigin(screen), way = _camera.ProjectRayNormal(screen);
        return Mathf.Abs(way.Y) < 1e-4f ? null : from + way * (-from.Y / way.Y);
    }

    // --- the grass ---------------------------------------------------------------

    /// <summary>The grass: with <c>--grass</c> the four kinds side by side
    /// (<see cref="Meadows"/>), each labelled on its near rim; otherwise
    /// <see cref="BoardGrass"/> on every cell of the board. The press map over
    /// the whole board either way. The fire burns it only where there is wood
    /// (<see cref="Scorch"/>): grass is decoration, not fuel.</summary>
    private void BuildGrass()
    {
        if (_field is null || _tile is null)
            return;
        float r = _tile.HexRect.Size.X * 0.5f;
        var span = new Rect2();
        bool first = true;
        for (int q = 0; q < _field.Columns; q++)
        for (int w = 0; w < _field.Rows; w++)
        {
            Vector3 m = CellMiddle(new Vector2I(q, w));
            var box = new Rect2(m.X - r, m.Z - r, 2.0f * r, 2.0f * r);
            span = first ? box : span.Merge(box);
            first = false;
        }
        _grass = new Grass3D
        {
            Name = "Grass", Ppm = _burning.Count > 0 ? _burning[0].Ppm : 17.0f,
            GustRate = TreeModel.GustRate, GustTravel = TreeModel.GustTravel, ShadowInk = Stage3D.ShadowInk.A,
        };
        AddChild(_grass);
        _grass.Map(span);
        // The spread is the fronts' from here (Spread): the fire's own clock
        // would light a neighbour whether or not the flame line had got there.
        if (_fire is not null)
            _fire.Spreads = false;
        var lay = new List<(Vector2I Cell, Grass3D.Kind Kind, string? Name)>();
        if (_grassy)
            foreach ((Vector2I cell, Grass3D.Kind kind, string name) in Meadows)
                lay.Add((cell, kind, name));
        else
            for (int q = 0; q < _field.Columns; q++)
            for (int w = 0; w < _field.Rows; w++)
                if (_field.InBounds(new Vector2I(q, w)))
                    lay.Add((new Vector2I(q, w), BoardGrass, null));
        ulong t0 = Time.GetTicksMsec();
        for (int i = 0; i < lay.Count; i++)
        {
            (Vector2I cell, Grass3D.Kind kind, string? name) = lay[i];
            Vector3 mid = CellMiddle(cell);
            var trunks = new List<Vector3>();
            foreach (TreeFire t in _burning)
                if (t.Cell == cell)
                    trunks.Add(t.Holder.GlobalPosition);
            _meadowAt[cell] = _meadowIds.Count;
            _meadowIds.Add(_grass.Lay(kind, mid, r, 101 + i, trunks));
            _meadowCells.Add(cell);
            _meadowFrom.Add(null);
            if (name is not null)
                AddChild(new Label3D
            {
                Text = name, FontSize = 40, OutlineSize = 10, PixelSize = 0.25f,
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, NoDepthTest = true,
                Position = mid + new Vector3(0.0f, 4.0f, r * 1.05f),
            });
        }
        GD.Print($"tree3d: grass on {lay.Count} cells in {Time.GetTicksMsec() - t0} ms, r {r:F0} px, press map {span}");
    }

    /// <summary>A hull across each meadow, left to right, 30 px in front of its
    /// middle, from off the cell to off it: the same lane on each, and the
    /// tree (<see cref="MeadowTree"/>) clear of its width. One straight lane
    /// over all three missed the middle one - it stands half a cell back, as
    /// every other column does.</summary>
    private void DriveAcross()
    {
        if (_tile is null)
            return;
        float r = _tile.HexRect.Size.X * 0.5f;
        foreach ((Vector2I cell, _, _) in Meadows)
        {
            Vector3 mid = CellMiddle(cell) + new Vector3(0.0f, 0.0f, 30.0f);
            var body = new Node3D { Name = "Ram" };
            var paint = new StandardMaterial3D { AlbedoColor = new Color(0.36f, 0.40f, 0.24f) };
            body.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = Hull, Material = paint },
                                               Position = Vector3.Up * (Hull.Y * 0.5f) });
            body.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(40, 14, 42), Material = paint },
                                               Position = new Vector3(0, Hull.Y + 7.0f, 8.0f) });
            body.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(6, 6, 56), Material = paint },
                                               Position = new Vector3(0, Hull.Y + 8.0f, -34.0f) });
            Toon.Dress(body);
            AddChild(body);
            float reach = r + Hull.Z * 0.5f + 10.0f;
            var ram = new Ram
            {
                Body = body, Way = Vector3.Right, Through = true, Harmless = true,
                At = mid - Vector3.Right * reach, Left = 2.0f * reach,
            };
            _rams.Add(ram);
            Place(ram);
        }
    }

    /// <summary>
    /// The meadows' fire: each cell's clock from the board's fire, and where
    /// its front starts - fixed on the frame it is lit, and with it when each
    /// tree on the cell catches: as the front gets to its foot
    /// (<see cref="Grass3D.Arrival"/>) instead of the hash of where it stands.
    /// A cell with no grass keeps the hash. Lit from a neighbour,
    /// at the edge between them, a little out on the neighbour's side (the
    /// neighbour whose delay it is: the one lit longest past it, as
    /// <see cref="Wildfire.Tick"/> hands on); lit by hand, the click; by
    /// <c>--burn</c>, the middle.
    /// </summary>
    private void Scorch()
    {
        if (_grass is null || _fire is null)
            return;
        // The front has to be off the far corner by the time the last tree may
        // catch: the fire holds every tree on a cell to CatchWithin of it
        // (Wildfire: the cell is Burnt once its last tree is), so a tree the
        // front reached later would stand in flame on a burnt cell.
        float within = _fire.CatchWithin - CatchLag;
        for (int i = 0; i < _meadowIds.Count; i++)
        {
            Vector2I cell = _meadowCells[i];
            float age = _fire.AgeAt(cell);
            bool lit = age >= 0.0f && _meadowFrom[i] is null;
            if (age < 0.0f)
                _meadowFrom[i] = null;
            else if (lit)
                _meadowFrom[i] = FrontFrom(cell);
            _grass.Burn(_meadowIds[i], age, _meadowFrom[i] ?? Vector2.Zero, within);
            var catches = new List<string>();
            foreach (TreeFire t in _burning)
            {
                if (t.Cell != cell)
                    continue;
                if (age < 0.0f)
                    t.Stagger = t.Hashed;
                else if (lit)
                {
                    // The tree catches off the grass: when the flame line gets
                    // to its foot, and a beat later, the crown taking from under.
                    Vector3 foot = t.Holder.GlobalPosition;
                    float at = _grass.Arrival(_meadowIds[i], new Vector2(foot.X, foot.Z)) + CatchLag;
                    t.Stagger = Mathf.Clamp(at / Mathf.Max(_fire.CatchWithin, 1e-3f), 0.0f, 1.0f);
                    catches.Add($"{t.Holder.Name} {t.Stagger * _fire.CatchWithin:F1}");
                }
            }
            if (lit && _meadowFrom[i] is { } from)
                GD.Print($"tree3d: {cell} lit at {_clock:F2} s, front from {from.X:F0},{from.Y:F0}"
                         + (catches.Count > 0 ? $", trees catch at s: {string.Join(", ", catches)}" : ""));
        }
    }

    /// <summary>
    /// The fire into the next cell, when the grass is laid (the fire's own
    /// spread is off, see <see cref="BuildGrass"/>): a wooded neighbour is lit
    /// the moment this cell's front gets to the edge between them - the first
    /// of <see cref="EdgeSamples"/> points along it, the grass noise and all
    /// (<see cref="Grass3D.Arrival"/>) - and its own front starts from that
    /// point, so the line goes on over the edge instead of coming up anew
    /// somewhere else. A lit cell with no grass hands the fire on as the fire
    /// does, on the edge's delay (<see cref="Wildfire.Delay"/>). A neighbour
    /// with no wood is not lit (<see cref="Wildfire.Light"/> refuses): the grass
    /// is decoration and carries nothing.
    /// </summary>
    private void Spread()
    {
        if (_grass is null || _fire is null || _field is null || _fire.Spreads || _tile is null)
            return;
        float r = _tile.HexRect.Size.X * 0.5f;
        for (int q = 0; q < _field.Columns; q++)
        for (int w = 0; w < _field.Rows; w++)
        {
            var cell = new Vector2I(q, w);
            float age = _fire.AgeAt(cell);
            if (age < 0.0f)
                continue;
            bool grassy = _meadowAt.TryGetValue(cell, out int meadow);
            int mi = grassy ? _meadowIds[meadow] : -1;
            // its front is set on the frame it is lit (Scorch): not before
            if (grassy && _meadowFrom[meadow] is null)
                continue;
            Vector3 mid = CellMiddle(cell);
            foreach (int heading in HexField.EdgeHeadings)
            {
                Vector2I next = HexField.Step(cell, heading);
                if (next == cell || !_field.InBounds(next) || _fire.LitAt(next) || !_wooded.Contains(next))
                    continue;
                if (!grassy)
                {
                    if (age >= _fire.Delay(cell, next))
                        _fire.Light(next);
                    continue;
                }
                // the shared edge: across the line between the two middles,
                // halfway, one side long (a regular hexagon's side is its radius)
                Vector3 there = CellMiddle(next);
                var c = new Vector2((mid.X + there.X) * 0.5f, (mid.Z + there.Z) * 0.5f);
                Vector2 along = new Vector2(there.Z - mid.Z, mid.X - there.X).Normalized();
                float first = float.MaxValue;
                Vector2 at = c;
                for (int k = 0; k < EdgeSamples; k++)
                {
                    Vector2 p = c + along * (r * ((float)k / (EdgeSamples - 1) - 0.5f));
                    float when = _grass.Arrival(mi, p);
                    if (when < first)
                    {
                        first = when;
                        at = p;
                    }
                }
                if (age >= first && _fire.Light(next))
                    _entry[next] = at;
            }
        }
    }

    private const int EdgeSamples = 7;

    /// <summary>How long after the grass fire gets to a trunk its tree is
    /// alight, s.</summary>
    private const float CatchLag = 0.2f;

    private Vector2 FrontFrom(Vector2I cell)
    {
        Vector3 mid = CellMiddle(cell);
        if (_litAt.Remove(cell, out Vector2 click))
            return click;
        if (_entry.Remove(cell, out Vector2 entry))
            return entry;
        Vector2I? best = null;
        float most = float.MinValue;
        foreach (int heading in HexField.EdgeHeadings)
        {
            Vector2I next = HexField.Step(cell, heading);
            if (next == cell || _field is null || !_field.InBounds(next) || _fire is null)
                continue;
            float past = _fire.AgeAt(next);
            if (past < 0.0f)
                continue;
            past -= _fire.Delay(next, cell);
            if (past > most)
            {
                most = past;
                best = next;
            }
        }
        if (best is not { } from)
            return new Vector2(mid.X, mid.Z);
        Vector3 edge = mid + (CellMiddle(from) - mid) * 0.62f;
        return new Vector2(edge.X, edge.Z);
    }

    /// <summary>
    /// What presses the grass this frame: each hull's two tracks, flat, and its
    /// belly between them, less; and the pit a fallen tree's plate tore, cut
    /// out. The fallen tree itself presses nothing: the flattened patch along
    /// its trunk read as a pale mat laid under it, not as grass under a log.
    /// </summary>
    private void Trample()
    {
        if (_grass is null)
            return;
        _grass.Wind = _windOn ? _wind : 0.0f;
        foreach (Ram r in _rams)
        {
            var way = new Vector2(r.Way.X, r.Way.Z);
            Vector3 across = Vector3.Up.Cross(r.Way).Normalized();
            const float track = 13.0f;
            foreach (float side in new[] { -1.0f, 1.0f })
                _grass.Press(r.At + across * (side * (Hull.X - track) * 0.5f), way, Hull.Z, track, 1.0f);
            _grass.Press(r.At, way, Hull.Z * 0.9f, Hull.X - 2.0f * track, 0.35f);
        }
        foreach (TreeFire t in _burning)
        {
            if (!t.Fall.Going)
                continue;
            Vector3 foot = t.Holder.GlobalPosition;
            float plate = t.Fall.Of.Plate > 0.0f ? t.Fall.Of.Plate : 1.4f;
            float grow = Mathf.SmoothStep(0.02f, 0.30f, (float)t.Fall.Angle);
            _grass.Cut(foot, plate * t.Ppm * grow * 0.85f, t.Fall.WorldDir, t.Fall.Of.Hinge * t.Ppm);
        }
    }

    // --- the wind ---------------------------------------------------------------

    /// <summary><c>--wind x</c>: how hard, the sprite wood's wind times this (1
    /// by default, so the models sway with the sprites beside them); 0 still.
    /// <c>W</c> turns it off and on.</summary>
    private float _wind = 1.0f;
    private bool _windOn = true;
    private float _weather;

    /// <summary>
    /// A frame of the wind on every tree (<see cref="TreeModel.Sway"/>): it
    /// goes out as the trunk goes over (1 - down², as the sprite's), and a
    /// burnt tree keeps a third, eased in with its crown going.
    /// </summary>
    private void Sway(float dt)
    {
        _weather += dt;
        float wind = _windOn ? _wind : 0.0f;
        foreach (TreeFire t in _burning)
        {
            float still = 1.0f;
            if (t.Fall.Going)
            {
                float down = t.Fall.Down;
                still = 1.0f - down * down;
            }
            TreeModel.Sway(t.Holder, t.Burn, t.Leaves, _weather, t.SwayPhase, t.SwayRate, wind, still, t.Spent);
        }
    }

    // --- the fall -------------------------------------------------------------

    /// <summary>The screen's compass: 1 away from the eye, clockwise to 8 -
    /// on the ground (world x, z), which the screen squashes in depth.</summary>
    private static Vector2 Compass(int point) =>
        Vector2.Up.Rotated(Mathf.DegToRad(45.0f * (Mathf.Clamp(point, 1, 8) - 1)));

    /// <summary>
    /// Knock <paramref name="t"/> over toward <paramref name="way"/> (the
    /// ground's x, z; <see cref="TreeFall.Start"/>). A burning tree does not
    /// go over (GDD, "HT - Бульдозер"); a tree already going is left to it.
    /// </summary>
    private bool Fell(TreeFire t, Vector2 way, float shove) =>
        !t.Burning && t.Fall.Start(way, shove, t.Burnt, "tree3d");

    /// <summary>
    /// A frame of every falling tree (<see cref="TreeFall.Step"/>), the dust
    /// where each crown comes down, and what the flags and the drive ask for.
    /// </summary>
    private void Topple(float dt)
    {
        foreach ((int ti, int point) in _ramAt)
            if (_clock >= _ramWhen && ti >= 0 && ti < _burning.Count)
                RamInto(_burning[ti], point);
        foreach ((int ti, int point) in _fellAt)
            if (_clock >= _ramWhen && ti >= 0 && ti < _burning.Count)
                Fell(_burning[ti], Compass(point), 0.6f);
        if (_rmbAt is { } click && _clock >= _ramWhen)
        {
            _rmbAt = null;
            _UnhandledInput(new InputEventMouseButton
            {
                ButtonIndex = MouseButton.Right, Pressed = true, Position = click,
            });
        }
        if (_drive && _clock >= _ramWhen)
        {
            _drive = false;
            DriveAcross();
        }
        if (_clock >= _ramWhen)
        {
            _ramAt.Clear();
            _fellAt.Clear();
        }
        Drive(dt);
        foreach (TreeFire t in _burning)
            if (t.Fall.Step(dt))
            {
                _dust ??= new FallDust(this);
                _dust.Add(t.Fall, t.Burnt);
            }
        _dust?.Settle(dt, _camera.GlobalBasis, Foot);
    }

    /// <summary>The dust of the crowns coming down (<see cref="FallDust"/>).</summary>
    private FallDust? _dust;

    // --- the ram: a stand-in hull ---------------------------------------------

    /// <summary>A hull driving into a tree: no tank model on the machine this
    /// was made on, so a box with a turret on it, on the cel look. It comes
    /// from <see cref="RamFrom"/> px back, at <see cref="RamSpeed"/>, knocks the
    /// tree over on contact - glacis to the trunk - and stops
    /// <see cref="RamPast"/> on: driven on over where the tree stood, it sat on
    /// the fallen trunk, and the crown alone lying there read as a bush.</summary>
    private sealed class Ram
    {
        public Node3D Body = null!;
        public Vector3 At, Way;
        public float Left;
        /// <summary>Through a wood (<c>--wood</c>): on across the cell, felling
        /// whatever is in its way. Otherwise it stops at the first tree.</summary>
        public bool Through, Struck;
        /// <summary>Only drives (<see cref="DriveAcross"/>): the lanes pass
        /// the next cell's tree closer than a hull's width.</summary>
        public bool Harmless;
        /// <summary>The tree it was sent at, off a wood: the only one it
        /// fells. Coming from 240 px back it crossed the next cell's tree on
        /// the grass board and felled that one instead.</summary>
        public TreeFire? Target;
    }

    private readonly List<Ram> _rams = new();
    private const float RamFrom = 240.0f, RamSpeed = 110.0f, RamPast = 12.0f;
    /// <summary>The stand-in's hull, board px: long, wide, high - a medium
    /// tank's size on the board, near enough.</summary>
    private static readonly Vector3 Hull = new(58.0f, 24.0f, 100.0f);

    /// <summary>A hull at <paramref name="t"/>, or through its cell in a wood.</summary>
    private void RamInto(TreeFire t, int point) => RamInto(t, Compass(point));

    /// <summary>The same, driving along <paramref name="dir"/> (x, z) - the way
    /// the tree goes over.</summary>
    private void RamInto(TreeFire t, Vector2 dir)
    {
        if (_wood > 0)
            RamAt(CellMiddle(t.Cell), dir, true);
        else if (!t.Fall.Going)
            RamAt(t.Holder.GlobalPosition, dir, false, t);
    }

    private Vector3 CellMiddle(Vector2I cell)
    {
        if (_field is null)
            return Vector3.Zero;
        Vector2 flat = _field.FlatAnchor(cell) + _field.CentreOffset;
        return Foot(new Vector3(flat.X, 0.0f, flat.Y / Squash));
    }

    private void RamAt(Vector3 foot, Vector2 dir, bool through, TreeFire? target = null)
    {
        dir = dir.Normalized();
        var way = new Vector3(dir.X, 0.0f, dir.Y);
        var body = new Node3D { Name = "Ram" };
        var paint = new StandardMaterial3D { AlbedoColor = new Color(0.36f, 0.40f, 0.24f) };
        body.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = Hull, Material = paint },
                                           Position = Vector3.Up * (Hull.Y * 0.5f) });
        body.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(40, 14, 42), Material = paint },
                                           Position = new Vector3(0, Hull.Y + 7.0f, 8.0f) });
        body.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(6, 6, 56), Material = paint },
                                           Position = new Vector3(0, Hull.Y + 8.0f, -34.0f) });
        Toon.Dress(body);
        AddChild(body);
        // across a wood: from its edge out past the far one
        float cross = (_tile?.HexRect.Size.X ?? 200.0f) * 0.5f + Hull.Z * 0.5f;
        var ram = new Ram
        {
            Body = body, Way = way, Through = through, Target = target,
            At = foot - way * RamFrom,
            Left = RamFrom + (through ? cross : 0.0f),
        };
        _rams.Add(ram);
        Place(ram);
    }

    private void Place(Ram r)
    {
        Vector3 at = Foot(new Vector3(r.At.X, 0.0f, r.At.Z));
        r.Body.GlobalTransform = new Transform3D(Basis.LookingAt(r.Way, Vector3.Up), at);
    }

    private void Drive(float dt)
    {
        foreach (Ram r in _rams)
        {
            if (r.Left <= 0.0f)
                continue;
            float go = Mathf.Min(RamSpeed * dt, r.Left);
            r.At += r.Way * go;
            r.Left -= go;
            Place(r);
            if (r.Harmless)
            {
                // off the cell it crossed: out of the way of the next one's picture
                r.Body.Visible = r.Left > 0.0f;
                continue;
            }
            // Every standing trunk in its way: the glacis at the trunk (half
            // the hull ahead of its middle and the collar's radius), and
            // across, within the hull's width and the collar's.
            Vector3 across = Vector3.Up.Cross(r.Way).Normalized();
            foreach (TreeFire t in _burning)
            {
                if (t.Fall.Going || r.Left <= 0.0f || (r.Target is not null && t != r.Target))
                    continue;
                Vector3 rel = t.Holder.GlobalPosition - r.At;
                float ahead = rel.Dot(r.Way), side = rel.Dot(across);
                float collar = 0.55f * t.Ppm;
                if (ahead > Hull.Z * 0.5f + collar || ahead < -Hull.Z * 0.5f
                    || Mathf.Abs(side) > Hull.X * 0.5f + collar)
                    continue;
                if (t.Burning)
                {
                    // It stands (GDD), and the hull stops at it.
                    r.Left = 0.0f;
                    GD.Print($"tree3d: {t.Holder.Name} is burning and does not go over");
                    continue;
                }
                // Away from the hull's line as well as on along it: a trunk off
                // to one side of the glacis goes over to that side, the way
                // Grove.Topple lays a wood either side of a hull's path - and
                // not all the same distance.
                float h = CelPuff.Hash(_burning.IndexOf(t), 619);
                float off = (side >= 0.0f ? 1.0f : -1.0f) * Mathf.DegToRad(6.0f + 24.0f * h)
                            * Mathf.Clamp(Mathf.Abs(side) / (Hull.X * 0.5f), 0.3f, 1.0f);
                Vector3 over = r.Way.Rotated(Vector3.Up, -off);
                Fell(t, new Vector2(over.X, over.Z), RamSpeed / 120.0f);
                if (!r.Through && !r.Struck)
                    r.Left = RamPast;
                r.Struck = true;
            }
        }
    }

    /// <summary>The tree nearest the cursor on the screen, by its foot.</summary>
    private TreeFire? Nearest(Vector2 cursor)
    {
        TreeFire? best = null;
        float near = float.MaxValue;
        foreach (TreeFire t in _burning)
        {
            Vector3 mid = t.Holder.GlobalPosition + Vector3.Up * (t.Height * t.Ppm * 0.4f);
            float d = _camera.UnprojectPosition(mid).DistanceTo(cursor);
            if (d < near)
            {
                near = d;
                best = t;
            }
        }
        return best;
    }

    // --- the crown's outline -------------------------------------------------

    /// <summary>The crowns' line (<see cref="CrownOutline"/>); none under
    /// <c>--pbr</c> or <c>--no-outline</c>.</summary>
    private CrownOutline? _outline;

    private void BuildOutline()
    {
        _outline = new CrownOutline { Name = "CrownOutline" };
        AddChild(_outline);
        _outline.Build(_camera);
    }

    // --- the board ---------------------------------------------------------

    private static AtlasSet? LoadTile(string tag)
    {
        try
        {
            return AtlasSet.Load(AssetRoot.Sprites, tag);
        }
        catch (Exception e)
        {
            GD.Print($"tree3d: no {tag} tile ({e.Message})");
            return null;
        }
    }

    private void BuildWorld()
    {
        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = new Color(0.3f, 0.3f, 0.3f),
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = new Color(0.62f, 0.64f, 0.70f),
            AmbientLightEnergy = 0.55f,
        };
        AddChild(new WorldEnvironment { Environment = env });
        // Tank3D's sun: over the camera's left shoulder, the side the sprites
        // are lit from, so the models' light agrees with the wood beside them.
        _sun = new DirectionalLight3D
        {
            RotationDegrees = new Vector3(-52.0f, -35.0f, 0.0f),
            LightEnergy = 1.25f,
            ShadowEnabled = true,
            DirectionalShadowMode = DirectionalLight3D.ShadowMode.Parallel2Splits,
            DirectionalShadowMaxDistance = 3000.0f,
            ShadowNormalBias = 4.0f,
        };
        AddChild(_sun);
    }

    private void BuildBoard()
    {
        if (_tile is null)
            return;
        BoardMap map = BoardMap.FromGround("tree3d", Ground, new[] { ".....", ".....", "....." },
                                           Array.Empty<Parking>(), TerrainSet.Mixed, true);
        TerrainSet terrain = TerrainSet.Load(AssetRoot.Terrains);
        _field = new HexField
        {
            Terrain = terrain,
            Paint = map.Paint,
            Trees = false,
            Columns = map.Columns, Rows = map.Rows, Plot = map.Plot,
        };
        _field.SetKinds(map.Kinds);
        _field.SetGround(map.Ground);
        _field.SetCover(map.Over);
        _field.SetRelief(map.Levels, map.Ramps);
        _field.SetWater(map.Water);
        AddChild(_field);
        _field.Atlas = _tile;

        var eye = new Camera2D { Enabled = false };
        AddChild(eye);
        _stage = new Stage3D
        {
            Field = _field, Origin = Vector2.Zero, Eye = eye,
            Surf = WaterArt.Load(AssetRoot.Water, _tile.HexRect),
        };
        AddChild(_stage);
        _field.ShowField = false;
        BuildShadows();
        _fire = new Wildfire { Field = _field, Enabled = true, Wooded = _wooded.Contains };
    }

    private float LiftAt(Vector3 w) => _field?.TopAtPoint(new Vector2(w.X, w.Z * Squash)) ?? 0.0f;

    private Vector3 Foot(Vector3 w) => new(w.X, LiftAt(w) / RiseFactor, w.Z);

    /// <summary>Tank3D's shadow skin (see its <c>BuildShadows</c>): the board is
    /// unshaded, so the sun has nothing to lay the models' shadow on but this.</summary>
    private void BuildShadows()
    {
        if (_stage is null)
            return;
        List<Vector3> tris = _stage.GroundTriangles();
        if (tris.Count == 0)
            return;
        Vector3 lie = Stage3D.Clear(Squash, RiseFactor);
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        foreach (Vector3 v in tris)
        {
            st.SetNormal(Vector3.Up);
            st.AddVertex(v + lie);
        }
        var ink = new ShaderMaterial { Shader = ShadeShader, RenderPriority = Stage3D.ShadowOrder };
        ink.SetShaderParameter("ink", Stage3D.ShadowInk.A);
        AddChild(new MeshInstance3D
        {
            Name = "Shadows",
            Mesh = st.Commit(),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            MaterialOverride = ink,
        });
    }

    private static readonly Shader ShadeShader = new()
    {
        Code = @"
shader_type spatial;
render_mode blend_mul, depth_draw_never, cull_disabled, ambient_light_disabled;
uniform float ink = 0.45;
void fragment() {
    ALBEDO = vec3(1.0);
    EMISSION = vec3(1.0 - ink);
}
void light() {
    DIFFUSE_LIGHT += vec3(ink * ATTENUATION);
}",
    };

    // --- the camera --------------------------------------------------------

    private void BuildCamera()
    {
        _camera = new Camera3D
        {
            Projection = Camera3D.ProjectionType.Orthogonal,
            KeepAspect = Camera3D.KeepAspectEnum.Height,
            Near = 1.0f,
            Far = Back * 2.0f,
            RotationDegrees = new Vector3(-Mathf.RadToDeg(Mathf.Asin(Squash)), 0.0f, 0.0f),
            Current = true,
        };
        AddChild(_camera);
        _camera.MakeCurrent();
    }

    /// <summary>The board's middle a little below the view's, so the crowns have
    /// room; the sun's map on the slab of depth the trees stand in (Tank3D's
    /// <c>FrameShadow</c>, for the same acne).</summary>
    private void FrameCamera()
    {
        float height = GetViewport().GetVisibleRect().Size.Y;
        _camera.Size = height / _zoom;
        Vector3 pivot = Vector3.Zero;
        if (_field is not null)
        {
            Vector2 flat = _field.FlatAnchor(new Vector2I(2, 1)) + _field.CentreOffset;
            pivot = new Vector3(flat.X, 0.0f, flat.Y / Squash);
            if (_grassy)
            {
                // between the four meadows: two of them stand half a cell back
                pivot = Vector3.Zero;
                foreach ((Vector2I cell, _, _) in Meadows)
                    pivot += CellMiddle(cell) / Meadows.Length;
                pivot.Y = 0.0f;
                pivot.Z += 40.0f;
            }
        }
        pivot.Z -= _camera.Size / 8.0f / Squash;
        _camera.Position = pivot + new Vector3(0.0f, Back * Squash, Back * RiseFactor);
        float depth = (pivot - _camera.Position).Dot(-_camera.Basis.Z);
        _outline?.Follow(_camera, _zoom, depth);
        const float reach = 600.0f;
        _sun.DirectionalShadowMaxDistance = depth + reach;
        _sun.DirectionalShadowSplit1 = Mathf.Clamp((depth - reach) / (depth + reach), 0.05f, 0.95f);
    }

    // --- flags -------------------------------------------------------------

    private void ReadFlags()
    {
        string[] args = OS.GetCmdlineUserArgs();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            bool more = i + 1 < args.Length;
            if (a == "--trees" && more) _trees = args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries);
            else if (a == "--zoom" && more)
            {
                _zoom = F(args[++i], _zoom);
                _zoomed = true;
            }
            else if (a == "--pbr") _pbr = true;
            else if (a == "--no-outline") _noOutline = true;
            else if (a == "--row") _row = true;
            else if (a == "--grass") _grassy = true;
            else if (a == "--no-grass") _bare = true;
            else if (a == "--drive") _grassy = _drive = true;
            else if (a == "--wind" && more) _wind = F(args[++i], _wind);
            else if (a == "--wood" && more) _wood = Math.Max(1, (int)F(args[++i], 7));
            else if (a == "--ram-at" && more) _ramWhen = F(args[++i], _ramWhen);
            else if ((a == "--ram" || a == "--fell") && more)
            {
                List<(int, int)> into = a == "--ram" ? _ramAt : _fellAt;
                foreach (string pair in args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (pair.Split(':') is { Length: 2 } tk && int.TryParse(tk[0], out int ti)
                        && int.TryParse(tk[1], out int tp))
                        into.Add((ti, tp));
                }
            }
            else if (a == "--rmb" && more && args[++i].Split(',') is { Length: 2 } xy)
                _rmbAt = new Vector2(F(xy[0], 0.0f), F(xy[1], 0.0f));
            else if (a == "--record" && more) _recordDir = args[++i];
            else if (a == "--record-every" && more) _recordEvery = Math.Max(1, (int)F(args[++i], _recordEvery));
            else if (a == "--record-for" && more) _recordFor = F(args[++i], _recordFor);
            else if (a == "--record-from" && more) _recordFrom = F(args[++i], 0.0f);
            else if (a == "--burn" && more && args[++i].Split(',') is { Length: 2 } qr
                     && int.TryParse(qr[0], out int q) && int.TryParse(qr[1], out int r))
                _burnAt = new Vector2I(q, r);
            else if (a == "--capture" && more) _capturePath = args[++i];
            else if (a == "--capture-at" && more) _captureAt = (int)F(args[++i], _captureAt);
        }
    }

    private static float F(string s, float fallback) =>
        float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : fallback;
}
