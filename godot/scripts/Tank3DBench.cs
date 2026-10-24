using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// <c>Tank3D.tscn</c>: one 3D tank (<see cref="TankModel"/>) on the tank
/// bench's board, with the bench's own effects hung off its joints - the pilot
/// for moving the board from sprite atlases to 3D units.
///
/// <b>The space is <see cref="Stage3D"/>'s, on purpose.</b> An orthographic
/// camera pitched by the elevation the hex tile declares, <c>Size</c> the
/// viewport height over the zoom, so one world unit is one screen pixel at zoom
/// 1: +X right, +Y up, +Z toward the camera. The Node3D effects
/// (<see cref="ProcKick"/>, <see cref="ProcSpall"/>, <see cref="ProcSlam"/>,
/// <see cref="SheetBlast"/>) were written for exactly that space, so they are
/// raised here with the numbers the board raises them with, not re-tuned.
///
/// <b>And so is the ground: the board is <see cref="Stage3D"/>'s own.</b> A
/// <see cref="HexField"/> laid from a <see cref="BoardMap"/> (<c>test</c>, the
/// tank bench's strip, unless <c>--map</c> names another) and handed to a stage
/// that draws it as prisms - levels, ramps, the pond - in the same world the
/// model stands in. The tank drives on it: its height and tilt are the face
/// under it, and <see cref="HexField.Passable"/> stops it at a cliff, deep water
/// or the edge. <c>--flat</c> puts back the ground this scene had before, the
/// art laid flat with no depth.
///
/// <b>The motion is the Blender previews' (<c>repro_kit.py</c> clip_*), live.</b>
/// Same springs - <c>KICK</c> is <see cref="Recoil"/>'s, <c>SWAY</c> is
/// <see cref="BodyPitch"/>'s - and the same recoil stroke, wreck pose and toss,
/// so what the GIFs showed is what this plays.
/// </summary>
public sealed partial class Tank3DBench : Node3D
{
    /// <summary>One spring, semi-implicit Euler in substeps - the bench's
    /// integrator, and the previews'.</summary>
    private sealed class Spring
    {
        private readonly float _k, _c;
        public float X, V;
        public Spring(float k, float c) { _k = k; _c = c; }
        public void Kick(float dv) => V += dv;
        public float Step(float dt, float target = 0.0f, int n = 8)
        {
            float h = dt / n;
            for (int i = 0; i < n; i++)
            {
                V += (-_k * (X - target) - _c * V) * h;
                X += V * h;
            }
            return X;
        }
        public void Reset() { X = 0.0f; V = 0.0f; }
    }

    private enum Fate { Alive, Knocked, Destroyed }

    // --- flags -----------------------------------------------------------

    private string _modelTag = "LTR";
    private string _mapName = "events";
    private bool _flat;
    /// <summary><c>--pbr</c>: the glTF's own materials, no cel shading and no
    /// ink - the look before <see cref="Toon"/>, to hold against it.</summary>
    private bool _pbr;
    /// <summary><c>--fx2d</c>: the sprites' fire and column on the card, as
    /// before <see cref="CelBurn"/>, to hold against it.</summary>
    private bool _fx2d;
    /// <summary><c>--soft-water</c>: the board's water as the sprite benches draw
    /// it - foam and glints faded in rather than stepped (<see cref="Stage3D.CelWater"/>).</summary>
    private bool _softWater;
    private float _zoom = 2.5f;
    /// <summary>The zoom is the board's fit (<see cref="FitZoom"/>), kept so
    /// as the window changes, until the wheel, <c>-/=</c> or <c>--zoom</c>
    /// set one.</summary>
    private bool _zoomFit = true;
    private float _heading = 215.0f;
    private string? _capturePath;
    private bool _noUi;
    private int _captureAt = 60;
    private string? _sequenceDir;
    private int _sequenceFrom, _sequenceTo, _sequenceStep = 1;
    private readonly List<(int Frame, string What)> _script = new();

    // --- scene -----------------------------------------------------------

    private TankModel _model = null!;
    private Node3D _rig = null!;
    private Camera3D _camera = null!;
    private DirectionalLight3D _sun = null!;
    private Label _hud = null!;
    private ControlPanel? _panel;
    private int _frame;
    private string _note = "";

    /// <summary>The board's tile, and so the camera's angle, the effects' size
    /// and the tanks' scale: the medium's hexagon, as <see cref="TankBench"/>
    /// takes it - one tile for every tank, so switching the tank does not resize
    /// the board. The board's, not the tank's: nothing of the tank on it is read
    /// off a sprite set.</summary>
    private AtlasSet? _tile;

    // --- the board ---------------------------------------------------------

    private HexField? _field;
    private Stage3D? _stage;

    /// <summary>The ground's up under the tank, eased toward the face it is on
    /// so crossing onto a ramp tips the hull rather than snapping it.</summary>
    private Vector3 _groundUp = Vector3.Up;

    /// <summary>Hull heading, degrees about +Y: 0 faces the camera (+Z).</summary>
    private float Heading
    {
        get => _heading;
        set { _heading = Mathf.PosMod(value, 360.0f); ApplyRig(); }
    }

    private float Squash => _tile is { } a && a.HexRect.Size.X > 0
        ? 2.0f * a.HexRect.Size.Y / (Mathf.Sqrt(3.0f) * a.HexRect.Size.X)
        : 0.5f;

    private float RiseFactor => Mathf.Sqrt(Mathf.Max(0.0f, 1.0f - Squash * Squash));

    private float HexWidth => _tile?.HexRect.Size.X ?? 248.0f;

    // --- the tanks ---------------------------------------------------------

    /// <summary>
    /// Which class each model drives as, and what the panel calls it.
    ///
    /// <b>Written down, because the sidecar does not say it</b> (see
    /// docs/tank3d.md). The class gives the tank its speed and its size - no
    /// sprite set is read for either.
    /// </summary>
    private static readonly (string Model, string Class, string Name)[] Pairs =
    {
        ("LTR", "LTP", "лёгкий"),
        ("MTR", "MTP", "средний"),
        ("HTR", "HTP", "тяжёлый"),
        ("TDR", "TDP", "ПТ-САУ"),
        ("HMR", "HMP", "мортира"),
    };

    private static int PairAt(string model) =>
        Array.FindIndex(Pairs, p => string.Equals(p.Model, model, StringComparison.OrdinalIgnoreCase));

    /// <summary>The pair's class, or the medium's for a model not in the table:
    /// the medium is the class every other figure is read against.</summary>
    private static MovementProfile ClassFor(string model)
    {
        string want = PairAt(model) is int i and >= 0 ? Pairs[i].Class : "MTP";
        MovementProfile? medium = null;
        foreach (MovementProfile p in MovementProfile.All)
        {
            if (string.Equals(p.Tag, want, StringComparison.OrdinalIgnoreCase))
                return p;
            if (p.Tag == "MTP")
                medium = p;
        }
        return medium ?? MovementProfile.Light;
    }

    /// <summary>
    /// How long the medium's hull is against the hex it stands on.
    ///
    /// <b>The sprite bench's own proportion, taken once and then left to the
    /// hex</b>: MTP draws its 0.883-unit hull at 167.7 px a unit, 148 px on a
    /// 248 px hex - 0.60. Every model's hull is brought to this, then scaled by
    /// its class's <see cref="MovementProfile.Size"/>, which is how the 2D
    /// benches size theirs (<see cref="Fleet.Resize"/>): the generator makes
    /// every tank about one unit long, so its own length says nothing about
    /// which is the heavy.
    /// </summary>
    private const float HullOfHex = 0.60f;

    /// <summary>World px a model unit, for this model in this class.</summary>
    private float PixelsFor(TankModel model, MovementProfile profile) =>
        HullOfHex * HexWidth * (float)profile.Size / Mathf.Max(model.HullLength, 1e-3f);

    /// <summary>The models on disk - a folder under <c>Models/</c> with both
    /// files in it - in the pairs' order, then any others by name.</summary>
    private static List<string> ModelsOnDisk()
    {
        var found = new List<string>();
        string root = AssetRoot.Repo + "/Models";
        if (Directory.Exists(root))
            foreach (string dir in Directory.GetDirectories(root))
                if (File.Exists(dir + "/tank.glb") && File.Exists(dir + "/tank.json"))
                    found.Add(Path.GetFileName(dir));
        found.Sort((a, b) =>
        {
            int ia = PairAt(a), ib = PairAt(b);
            ia = ia < 0 ? int.MaxValue : ia;
            ib = ib < 0 ? int.MaxValue : ib;
            return ia != ib ? ia.CompareTo(ib) : string.CompareOrdinal(a, b);
        });
        return found;
    }

    private List<string> _models = new();

    // --- motion ----------------------------------------------------------

    private static readonly (float K, float C) KickSpring = (1150.0f, 38.0f);
    private static readonly (float K, float C) SwaySpring = (480.0f, 28.5f);

    private readonly Spring _kickPitch = new(KickSpring.K, KickSpring.C);
    private readonly Spring _kickRoll = new(KickSpring.K, KickSpring.C);
    private readonly Spring _swayPitch = new(SwaySpring.K, SwaySpring.C);
    private readonly Spring _swayRoll = new(SwaySpring.K, SwaySpring.C);
    private readonly Spring _heave = new(SwaySpring.K, SwaySpring.C);
    private readonly Spring _tip = new(260.0f, 18.0f);
    private readonly RandomNumberGenerator _rng = new() { Seed = 7 };

    /// <summary>World px per second at full speed, and the ramp and the turn -
    /// the class's own <see cref="MovementProfile"/>, as the board drives it.
    /// </summary>
    private float MaxSpeed => (float)_profile.TopSpeed;
    private float Accel => (float)_profile.Accel;
    private float TurnRate => (float)_profile.TurnRate;
    private float _speed;
    private float _nextBump;

    /// <summary>The engine's tremble - the sprites' own clock
    /// (<see cref="EngineTremble"/>: 11.5 and 7.3 Hz at rest, a share faster
    /// and harder under load), its stern travel turned into an angle about the
    /// bow by the hull's length on the board. A standing model was dead still
    /// with its engine running; the sprites' was not.</summary>
    private readonly EngineTremble _tremble = new();
    /// <summary>The engine running, eased: it dies with the tank
    /// (<c>Wreck.Out</c>) and does not stop dead.</summary>
    private float _trembleOn = 1.0f;
    /// <summary>A multiplier over the sprites' stern travel - the panel's
    /// <c>EngineTremble.Level</c> on the board.</summary>
    private float _trembleLevel = TrembleLevel;
    private const float TrembleLevel = 1.0f;
    private float _sinceShot = 99.0f;
    private float _sinceFate;
    private Fate _fate = Fate.Alive;

    /// <summary>The light class's slew, degrees a second - the previews'.</summary>
    private const float SlewDeg = 240.0f;
    private const float ElevDeg = 20.0f;

    public override void _Ready()
    {
        ReadFlags();
        _models = ModelsOnDisk();
        _rig = new Node3D { Name = "Rig" };
        AddChild(_rig);
        // The board's tile first: the camera's angle, the board's size and the
        // effects' pools all come off it, and none of them follows the tank.
        _tile = LoadTile("MTP");

        GetViewport().Msaa3D = Viewport.Msaa.Msaa4X;
        BuildWorld();
        if (!_flat)
            BuildBoard();
        // After the board: the stage makes its own camera current as it enters
        // the tree, and this one has to be current over it.
        BuildCamera();
        if (_field is null)
            BuildGround();
        BuildWood();
        BuildMines();
        BuildMarks();
        Park();
        Mount(_modelTag);
        BuildParked();
        if (_otherWanted)
        {
            if (_otherCell is Vector2I at)
                PlaceOther(at, _otherHeading);
            else
                LineUp();
        }
        // a target to aim at from the start (Tank3DBench.Aim); --no-target none
        else if (_targetDefault)
            StandTarget();

        var layer = new CanvasLayer();
        AddChild(layer);
        _hud = new Label { Position = new Vector2(12, 8), Modulate = new Color(1, 1, 1, 0.85f), Visible = !_noUi };
        layer.AddChild(_hud);
        // under the panel, which is drawn after it
        BuildCourse(layer);
        BuildAim(layer);
        if (!_noUi)
            BuildPanel(layer);
        BuildBack(layer);
        BuildMatchUi(layer);
    }

    /// <summary>The board's tile: the hex of a sprite set, the one thing here
    /// read off one - null if it does not load, which leaves the flat ground and
    /// a 248 px hex.</summary>
    private static AtlasSet? LoadTile(string tag)
    {
        try
        {
            return AtlasSet.Load(AssetRoot.Sprites, tag);
        }
        catch (Exception e)
        {
            GD.Print($"tank3d: no {tag} tile ({e.Message}) - flat ground, 248 px hex");
            return null;
        }
    }

    /// <summary>
    /// Put a tank on the rig: its model, its class and the effects that read
    /// it. What happens at start and on every pick from the panel.
    ///
    /// <b>The new model is loaded before the old one is taken down</b>, so a
    /// tank that fails to load leaves the one that was standing there. The rig,
    /// its place and its heading, the board and the effect pools stay: they are
    /// the bench's, not the tank's.
    /// </summary>
    private void Mount(string model)
    {
        MovementProfile profile = ClassFor(model);
        TankModel next = TankModel.Load(model, toon: !_pbr);
        next.ScaleTo(PixelsFor(next, profile));
        Unmount();
        _modelTag = model;
        _profile = profile;
        _model = next;
        SidePaint(_model, _side);
        _rig.AddChild(_model);
        _deckPx = MeasureDeck();
        _beltPaint = null;
        _foot = MeasureFootprint(_model, _rig, _modelTag);
        _roofPx = MeasureRoof();
        BuildEffects();
        GD.Print($"tank3d: {_modelTag} class {_profile.Tag} x{_profile.Size:F2}, {_model.PixelsPerUnit:F2} px/unit, "
                 + $"hull {_model.HullLength:F4} x {_model.HullWidth:F4} = {_model.HullLength * _model.PixelsPerUnit:F0} px, "
                 + $"bore r {_model.BoreRadius:F4}, deck {_deckPx:F1} px, roof {_roofPx:F1} of {_model.Size.Y * _model.PixelsPerUnit * RiseFactor:F1}, squash {Squash:F4} = {Mathf.RadToDeg(Mathf.Asin(Squash)):F2} deg, "
                 + $"hex {HexWidth:F0} px");
    }

    /// <summary>Take the tank off the rig: everything <see cref="Mount"/> and
    /// <see cref="BuildEffects"/> made for it. Reset first, so the debris a
    /// destroyed tank threw is back in the model that gets freed.</summary>
    private void Unmount()
    {
        if (_model is null)
            return;
        ResetTank();
        FreeEffects();
        _paint.Clear();
        _hullHits = null;
        _hitLocal = Vector3.Zero;
        _rig.RemoveChild(_model);
        _model.QueueFree();
        _model = null!;
    }

    /// <summary>The panel's pick and <c>--do model=</c>: another tank on the same
    /// spot, heading unchanged, whole.</summary>
    private void Pick(string model)
    {
        if (string.Equals(model, _modelTag, StringComparison.OrdinalIgnoreCase))
            return;
        // a match's unit is what it is: another one is taken by a click on it
        if (Match)
        {
            _note = "в партии танк берут щелчком по нему";
            return;
        }
        try
        {
            Mount(model);
        }
        catch (Exception e)
        {
            GD.Print($"tank3d: {model}: {e.Message}");
            _note = $"{model} did not load";
        }
    }

    /// <summary>How far the camera stands back along its view: the stage's own
    /// number and for its reason - an orthographic depth buffer is linear, and
    /// at 4000 back the board's rims, lifted two units off the faces, fell into
    /// its noise (<c>Stage3D.Back</c>).</summary>
    private const float Back = 1500.0f;

    private void BuildCamera()
    {
        _camera = new Camera3D
        {
            Projection = Camera3D.ProjectionType.Orthogonal,
            KeepAspect = Camera3D.KeepAspectEnum.Height,
            Near = 1.0f,
            Far = Back * 2.0f,
            RotationDegrees = new Vector3(-Mathf.RadToDeg(Mathf.Asin(Squash)), 0.0f, 0.0f),
            // Everything but the model's stand-ins, which are the height map's.
            CullMask = 0xFFFFF & ~GhostLayer,
            Current = true,
        };
        AddChild(_camera);
        _camera.MakeCurrent();
    }

    /// <summary>A wheel notch's zoom.</summary>
    private const float WheelStep = 1.1f;

    /// <summary>How far in the zoom goes: two hexes across the window's height.</summary>
    private const float ZoomMost = 8.0f;

    /// <summary>The board with this much of itself round it, so its rim and
    /// the prisms' sides are not on the window's edge.</summary>
    private const float FitMargin = 1.04f;

    /// <summary>The zoom by <paramref name="k"/>, between a little under the
    /// board's fit and <see cref="ZoomMost"/>; the fit is let go.
    ///
    /// <b>About the cursor, as strategy games do it</b>, when one is given (the
    /// wheel): the ground under it stays under it - the view's middle moves
    /// toward the cursor zooming in and away from it zooming out, by the share
    /// the size changed. The camera is orthographic and its plane is the
    /// board's, so this is exact: a point <c>s</c> pixels off the middle is
    /// <c>s·Size/H</c> off it in the plane, before and after. The view comes
    /// loose from the tank for it (<see cref="_viewAt"/>). Without a cursor
    /// (the keys) it zooms about the middle and leaves the follow alone.</summary>
    private void ZoomBy(float k, Vector2? cursor = null)
    {
        float least = Mathf.Min(0.5f, 0.8f * FitZoom());
        float was = _zoom;
        _zoom = Mathf.Clamp(_zoom * k, least, ZoomMost);
        _zoomFit = false;
        if (cursor is not Vector2 c)
            return;
        Vector2 window = GetViewport().GetVisibleRect().Size;
        Vector2 off = c - 0.5f * window;
        Vector2 at = _viewAt ?? _viewNow;
        // the plane point under the cursor, kept: at + off·(H/was)/H = at' + off·(H/zoom)/H
        _viewAt = at + off * (1.0f / was - 1.0f / _zoom);
    }

    /// <summary>
    /// Where the view's middle stands in the camera's plane (x across, y down
    /// the screen, <see cref="BoardBox"/>'s units) once the player has moved
    /// it - the middle button's drag (<see cref="PanBy"/>) or the wheel about
    /// the cursor - or null while it follows the tank. <c>Home</c> lets go.
    /// </summary>
    private Vector2? _viewAt;

    /// <summary>The view's middle as last framed, held inside the board -
    /// what a drag or the wheel starts from while the view still follows.</summary>
    private Vector2 _viewNow;

    /// <summary>The middle button held: the board dragged with the mouse by
    /// <paramref name="screen"/> pixels - the ground under the cursor stays
    /// under it.</summary>
    private void PanBy(Vector2 screen)
    {
        float h = Mathf.Max(GetViewport().GetVisibleRect().Size.Y, 1.0f);
        _viewAt = (_viewAt ?? _viewNow) - screen * (_camera.Size / h);
    }

    /// <summary>Back to following the tank.</summary>
    private void FollowTank()
    {
        _viewAt = null;
        GD.Print("tank3d: camera follows the tank");
    }

    /// <summary>
    /// The board's box in the camera's own plane, world units: x across the
    /// screen, y down it - <see cref="Board"/> of the ground, less the height a
    /// raised cell stands at, which is how far up the screen its top is drawn.
    /// Null on the flat ground. Measured once: the board does not move.
    /// </summary>
    private Rect2? BoardBox()
    {
        if (_boardBox is not null || _field is null || _tile is null)
            return _boardBox;
        Vector2 half = (Vector2)_tile.HexRect.Size * 0.5f;
        var lo = new Vector2(float.MaxValue, float.MaxValue);
        var hi = new Vector2(float.MinValue, float.MinValue);
        for (int r = 0; r < _field.Rows; r++)
        for (int q = 0; q < _field.Columns; q++)
        {
            var cell = new Vector2I(q, r);
            if (!_field.InBounds(cell))
                continue;
            Vector2 c = _field.FlatAnchor(cell) + _field.CentreOffset;
            float lift = LiftAt(new Vector3(c.X, 0.0f, c.Y / Squash));
            lo = new Vector2(Mathf.Min(lo.X, c.X - half.X), Mathf.Min(lo.Y, c.Y - half.Y - Mathf.Max(lift, 0.0f)));
            hi = new Vector2(Mathf.Max(hi.X, c.X + half.X), Mathf.Max(hi.Y, c.Y + half.Y - Mathf.Min(lift, 0.0f)));
        }
        if (lo.X > hi.X)
            return null;
        _boardBox = new Rect2(lo, hi - lo);
        return _boardBox;
    }

    private Rect2? _boardBox;

    /// <summary>The zoom the whole board fits the window at, with
    /// <see cref="FitMargin"/>; the old default, 2.5, on the flat ground.</summary>
    private float FitZoom()
    {
        if (BoardBox() is not { } box || box.Size.X <= 0.0f || box.Size.Y <= 0.0f)
            return 2.5f;
        Vector2 view = GetViewport().GetVisibleRect().Size;
        return Mathf.Min(view.X / box.Size.X, view.Y / box.Size.Y) / FitMargin;
    }

    /// <summary>Size the camera to the zoom and keep the tank a little below
    /// the middle, so a plume or a thrown turret has sky to go into - but no
    /// further out than the board's edge: along each axis of the screen the
    /// view is held inside the board, and where the board is narrower than
    /// the view it stands in the middle of it. At the fit that is the whole
    /// board, still; zoomed in, it is the tank, until it drives to an edge.</summary>
    private void FrameCamera()
    {
        const float back = Back;
        Vector2 window = GetViewport().GetVisibleRect().Size;
        float height = window.Y;
        if (_zoomFit)
            _zoom = FitZoom();
        _camera.Size = height / _zoom;
        // Not up with the wreck's hop, nor down into its crater or the ground
        // after it (Tank3DBench.Pits, Gone): the board stays where it is.
        Vector3 pivot = _rig.Position - Vector3.Up * _wreckLift;
        bool loose = _viewAt is not null;
        // Both hulls in the picture while a target stands near: toward the
        // middle between them, eased off with the distance so the rammer stays
        // on the screen - a switch at one distance was a jump of the view.
        if (_other is not null && !loose)
        {
            Vector3 mid = 0.5f * (_rig.Position + _other.Rig.Position);
            float d = mid.DistanceTo(_rig.Position) / _camera.Size;
            pivot = pivot.Lerp(mid, 1.0f - Smooth(0.3f, 0.6f, d));
        }
        // Up the screen by a sixth of the view: screen up is -Z on the ground.
        pivot.Z -= _camera.Size / 6.0f / Squash;
        // Moved by the player: the plane point itself, on the ground's zero.
        if (_viewAt is Vector2 view)
            pivot = new Vector3(view.X, 0.0f, view.Y / Squash);
        // Held inside the board. The middle of the view in the camera's
        // plane is the pivot's Board() less its height drawn up the screen.
        if (BoardBox() is { } box)
        {
            var half = new Vector2(_camera.Size * window.X / Mathf.Max(window.Y, 1.0f), _camera.Size) * 0.5f;
            var at = new Vector2(pivot.X, pivot.Z * Squash - pivot.Y * RiseFactor);
            // Following, the view is kept inside the board; moved by the
            // player, only its middle is - as strategy games hold the camera's
            // focus over the map. Held by its edges, a view at the fit, where
            // the board is smaller than the view both ways, had nowhere to go
            // and the middle button's drag did nothing.
            float Hold(float c, float lo, float size, float h) =>
                loose ? Mathf.Clamp(c, lo, lo + size)
                : size <= 2.0f * h ? lo + 0.5f * size : Mathf.Clamp(c, lo + h, lo + size - h);
            var held = new Vector2(Hold(at.X, box.Position.X, box.Size.X, half.X),
                                   Hold(at.Y, box.Position.Y, box.Size.Y, half.Y));
            pivot.X += held.X - at.X;
            pivot.Z += (held.Y - at.Y) / Squash;
            // A drag past the edge is not stored past it, or the way back
            // would first have to undo the overshoot.
            if (loose)
                _viewAt = held;
            _viewNow = held;
        }
        else
            _viewNow = new Vector2(pivot.X, pivot.Z * Squash - pivot.Y * RiseFactor);
        _camera.Position = pivot + new Vector3(0.0f, back * Squash, back * RiseFactor);
        // Camera2D.Offset's sense: +y moves the view down the screen.
        Vector2 shake = ShakeOffset();
        _camera.Position += _camera.Basis.X * shake.X - _camera.Basis.Y * shake.Y;
        FrameShadow();
        WoodFollow();
    }

    /// <summary>The first split's share of the distance: next to nothing, the
    /// eye's side of the board, which nothing casts into.</summary>
    private const float ShadowNear = 0.05f;

    /// <summary>
    /// The sun's map on the depth up to the far edge of the picture - where
    /// the ray through the top of the view meets the ground - not on the whole
    /// of the camera's 3000.
    ///
    /// <b>This is what the cel ramp needed.</b> Over all 3000 of the view's
    /// depth a texel came to ~1.5 px of the board, and a side the sun grazes
    /// shadowed itself in soft diagonal stripes - acne, blurred by the filter -
    /// which the tone's threshold cut into blots across the skirts.
    ///
    /// <b>To the edge of the view, not to the tank.</b> Up to the tank and
    /// three of its reaches past it, the end of the map was a line across the
    /// screen a hull or two above the tank that went where the camera went:
    /// the bunker three rows up had no shadow inside it or on the grass, lost
    /// it a band at a time as the tank came up, and the trees past it the
    /// same. The edge of the view is the same depth wherever the camera is,
    /// so the map stays put on the board.
    ///
    /// <b>One split from near the eye, not a slab round the tank.</b> The
    /// slab was the second of two splits, from the tank's depth less the
    /// reach (split 0.57 of the distance at MTR on the events board): under
    /// <c>gl_compatibility</c> with this orthographic eye it cast nothing at
    /// all - the board's skin read the sun's visibility as one everywhere,
    /// next to the hull too, and the grass, the walls and the tank lay on the
    /// board with no shadow. Measured by the skin's light painted red, the
    /// shadow blue: none with the slab; with the first split down to 0.05-0.1
    /// of the same distance, or one split over the view, the hull's shadow is
    /// there. And no fade: from 0.8 of the distance - the default - it was
    /// going out just past the tank, which is where its shadow falls.
    /// </summary>
    private void FrameShadow()
    {
        if (_model is null)
            return;
        // The ground there is taken a reach under the lower of the tank's and
        // the board's zero: a pond's floor or a step down ends further off.
        Vector3 top = _camera.Position + _camera.Basis.Y * (_camera.Size * 0.5f);
        float ground = Mathf.Min(0.0f, _rig.Position.Y) - Reach;
        _sun.DirectionalShadowMaxDistance = (top.Y - ground) / _camera.Basis.Z.Y;
        _sun.DirectionalShadowSplit1 = ShadowNear;
    }

    private void BuildWorld()
    {
        // Flat: the soil art's mean, lit about as the cells are, so the seams
        // between their soft rims read as more ground (BoardDress.World).
        AddChild(BoardDress.World(_flat));
        _sun = BoardDress.Sun();
        AddChild(_sun);
    }

    /// <summary>
    /// The board, as <see cref="TankBench"/> lays it: the map's kinds, ground,
    /// cover, relief and water on a <see cref="HexField"/>, and a
    /// <see cref="Stage3D"/> that draws it. The field draws nothing itself - the
    /// stage owns the board, as on every bench that has one.
    ///
    /// <b>The stage's ground writes depth, and the effect cards are why that
    /// needed an answer</b>: a card slid back along the view ray passes under
    /// the ground with its lower half. The cards' shader lifts whatever is below
    /// the ground back up the same ray (<see cref="CardShader"/>), which is the
    /// stage's own bend for its sprites (<c>Stage3D.Body</c>) done per vertex.
    /// </summary>
    private void BuildBoard()
    {
        if (_tile is null)
        {
            GD.Print("tank3d: no tile to lay a board with - the flat ground instead");
            return;
        }
        if (MapToPlay() is not BoardMap map)
            return;
        TankFromBoard(map);
        _parkings = map.Parked;
        TerrainSet terrain = TerrainSet.Load(AssetRoot.Terrains);
        GD.Print($"tank3d: board {map.Name} {map.Columns}x{map.Rows}, terrain {terrain.Note}");
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
        // After the relief - the tank bench's reason: the water's guards are
        // asked about levels and ramps.
        _field.SetWater(map.Water);
        AddChild(_field);
        _field.Atlas = _tile;
        _home = _startCell ?? (map.Homes.Count > 0 ? map.Homes[0] : new Vector2I(map.Columns / 2, map.Rows / 2));

        // What the stage aims its own camera by: it mirrors a 2D camera, and
        // this scene has none - its camera is its own (BuildCamera), current
        // over the stage's, which is left idle. In the tree so it is freed with
        // the scene, disabled so it moves no canvas.
        var eye = new Camera2D { Enabled = false };
        AddChild(eye);
        _stage = new Stage3D
        {
            Field = _field, Origin = Vector2.Zero, Eye = eye,
            Surf = WaterArt.Load(AssetRoot.Water, _tile.HexRect),
            // The pond as a wave field (Ripples): the stage fits it to the water
            // and reads it for the surface's normals and foam; the hull, the
            // splash and the air strike it (Tank3DBench.Water).
            Wash = _ripples,
            CelWater = !_softWater,
            // the relief in the models' look: the sides stepped by the sun
            // (Stage3D.CelRelief)
            CelRelief = !_pbr && !_flatRelief,
        };
        AddChild(_stage);
        _field.ShowField = false;
        BuildShadows();
        _celRipples = new CelRipples { Name = "CelRipples" };
        AddChild(_celRipples);
        _celRipples.Build(_field, Squash, RiseFactor, HexWidth * 0.5f);
        BuildWalls(map);
    }

    /// <summary>The cell the tank opens on: the map's first parking.</summary>
    private Vector2I _home;

    /// <summary>Where the model's shadow falls - <see cref="BoardDress.Shadows"/>,
    /// the skin over the board's tops.</summary>
    private void BuildShadows()
    {
        if (_stage is not null && BoardDress.Shadows(_stage, Squash, RiseFactor) is { } skin)
            AddChild(skin);
    }

    // --- standing on the board ----------------------------------------------

    /// <summary>How high the ground stands under a world point, in screen px -
    /// the board's own unit for it (<see cref="HexField.TopAtPoint"/>); nought
    /// on the flat ground.</summary>
    private float LiftAt(Vector3 w) => _field?.TopAtPoint(Board(w)) ?? 0.0f;

    /// <summary>The ground point under a world point.</summary>
    private Vector3 Foot(Vector3 w) => new(w.X, LiftAt(w) / RiseFactor, w.Z);

    /// <summary>Stand the rig on the home cell's centre, at its height.</summary>
    private void Park()
    {
        if (_field is not null)
        {
            Vector2 flat = _field.FlatAnchor(_home) + _field.CentreOffset;
            _rig.Position = Foot(new Vector3(flat.X, 0.0f, flat.Y / Squash));
        }
        Settle(0.0f, snap: true);
    }

    /// <summary>
    /// Whether the tank may go from one world point to the next, travelling
    /// <paramref name="way"/>: the board's own rule for the step between cells
    /// (<see cref="HexField.Passable"/> - no cliff without a ramp, a ramp only
    /// along its axis, not off the board; into deep water off any bank, out of
    /// it by the ramp alone). Anywhere on the flat ground.
    ///
    /// <b>Asked of the middle and of the leading end.</b> The middle alone let
    /// the front half of the hull out over the board's edge before it stopped;
    /// the end asks the same question of the step from the middle's cell to its
    /// own, which is at most a neighbour - half a hull is shorter than a cell's
    /// edge.
    /// </summary>
    private bool CanDrive(Vector3 from, Vector3 to, Vector3 way)
    {
        if (_field is null)
            return true;
        float half = _model.Size.Z * 0.5f * _model.PixelsPerUnit;
        Vector2I a = _field.FlatCellAt(Board(from)), b = _field.FlatCellAt(Board(to));
        Vector2I end = _field.FlatCellAt(Board(to + way * half));
        return Step(a, b) && Step(b, end);

        bool Step(Vector2I here, Vector2I there)
        {
            if (here == there)
                return true;
            if (!_field.InBounds(there))
                return false;
            int heading = HexField.HeadingTo(here, there);
            // the ground, then the masonry on the edge and a capon's gate
            // (Tank3DBench.Walls)
            return heading >= 0 && _field.Passable(here, heading) && !WallStops(here, heading)
                   && !ShelterRefuses(there, way);
        }
    }

    /// <summary>
    /// Put the rig on the ground under it: its height, and its up eased toward
    /// the face it is on.
    ///
    /// <b>One face, the one under the middle</b>, extrapolated under all four
    /// samples (<see cref="HexField.TopOn"/>): a hull over a cell's rim then
    /// lies in its own cell's plane rather than bridging to the neighbour's,
    /// which is the board's rule for anything a cell wide.
    ///
    /// <b>The height is the ride, not always the face</b> - in deep water the
    /// hull floats, drowns down to the drawn bed and falls in off the bank
    /// (Tank3DBench.Water); afloat it lies level, whatever the face under it.
    /// </summary>
    private void Settle(float dt, bool snap = false)
    {
        Vector3 want = Vector3.Up;
        if (_field is not null)
        {
            Vector2 flat = Board(_rig.Position);
            Vector2I cell = _field.CellUnder(flat);
            float d = 0.25f * HexWidth;
            float Y(Vector2 f) => _field.TopOn(cell, f) / RiseFactor;
            float sx = (Y(flat + new Vector2(d, 0.0f)) - Y(flat - new Vector2(d, 0.0f))) / (2.0f * d);
            // A world step of d in Z is d times the squash on the flat board.
            float sz = (Y(flat + new Vector2(0.0f, d * Squash)) - Y(flat - new Vector2(0.0f, d * Squash))) / (2.0f * d);
            float ride = RideOn(cell, flat, out bool afloat);
            want = afloat || _falling ? Vector3.Up : new Vector3(-sx, 1.0f, -sz).Normalized();
            float y = Ride(cell, ride, afloat, dt, snap);
            // Over the craters' rims and into their bowls (Tank3DBench.Pits).
            _wreckLift = 0.0f;
            if (!afloat && !_falling && _wreckPit)
                y += _wreckLift = WreckLay(ref want);
            else if (!afloat && !_falling)
                y += Pits(dt, ref want, snap);
            else
                PitsLeave();
            // Down into the mud (Tank3DBench.Mire).
            float mired = MireSettle(cell, dt, snap);
            if (!afloat && !_falling)
                y -= mired;
            // The burnt-out wreck into the ground (Gone).
            if (Gone > 0.0f)
                _wreckLift -= Gone * GoneDepth;
            y -= Gone > 0.0f ? Gone * GoneDepth : 0.0f;
            _rig.Position = new Vector3(_rig.Position.X, y, _rig.Position.Z);
        }
        _groundUp = snap ? want : _groundUp.Lerp(want, 1.0f - Mathf.Exp(-10.0f * dt)).Normalized();
        ApplyRig();
    }

    /// <summary>The rig's basis: the heading about the ground's up.</summary>
    private void ApplyRig()
    {
        if (_rig is null)
            return;
        float h = Mathf.DegToRad(_heading);
        var ahead = new Vector3(Mathf.Sin(h), 0.0f, Mathf.Cos(h));
        Vector3 up = _groundUp;
        Vector3 z = (ahead - up * ahead.Dot(up)).Normalized();
        _rig.Basis = new Basis(up.Cross(z), up, z);
    }

    /// <summary>
    /// The ground with <c>--flat</c>: the board's cells if there is art for
    /// them, a plain plane if not - never both, because neither may write depth
    /// (see <see cref="BuildCells"/>) and two opaque layers that do not are drawn
    /// in whatever order the renderer likes.
    /// </summary>
    private void BuildGround()
    {
        if (BuildCells())
            return;
        var mat = new StandardMaterial3D
        {
            AlbedoColor = new Color(134 / 255.0f, 110 / 255.0f, 70 / 255.0f),
            Roughness = 1.0f,
            DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Disabled,
        };
        var ground = new MeshInstance3D
        {
            Name = "Ground",
            Mesh = new PlaneMesh { Size = new Vector2(8000.0f, 8000.0f) },
            MaterialOverride = mat,
        };
        AddChild(ground);
    }

    /// <summary>
    /// The board's own ground art, one hexagon per cell, laid where the flat
    /// board draws it: the art is painted already squashed for this camera, so
    /// each cell is a lying quad whose outline on screen is the rectangle
    /// <see cref="TerrainSet.RectAt"/> gives - a screen row is depth times the
    /// squash.
    ///
    /// <b>Not in the depth buffer</b>, and that is load-bearing: an effect card
    /// is slid back along the view ray to stand behind the tank (see
    /// <see cref="Tank3DBench"/>.Fx), and its lower half then passes under the
    /// ground - a ground that wrote depth would cut it off there. Nothing stands
    /// below the ground, so it loses nothing by it. The gaps between the art's
    /// soft rims show the background, which is the soil's own mean.
    /// </summary>
    private bool BuildCells()
    {
        TerrainSet terrain = TerrainSet.Load(AssetRoot.Terrains);
        GD.Print($"tank3d: {terrain.Note}");
        if (!terrain.Any || _tile is null)
            return false;
        var hexRect = new Rect2I((Vector2I)_tile.HexRect.Position, (Vector2I)_tile.HexRect.Size);
        float scale = terrain.ScaleTo(hexRect);
        float w = _tile.HexRect.Size.X, h = _tile.HexRect.Size.Y;
        var mats = new Dictionary<string, StandardMaterial3D>();
        const int reach = 7;
        for (int q = -reach; q <= reach; q++)
            for (int r = -reach; r <= reach; r++)
            {
                var cell = new Vector2I(q, r);
                string kind = terrain.MixedAt(cell);
                if (terrain.Texture(kind) is not Texture2D tex)
                    continue;
                if (!mats.TryGetValue(kind, out StandardMaterial3D? m))
                {
                    m = new StandardMaterial3D
                    {
                        AlbedoTexture = tex,
                        Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor,
                        DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Disabled,
                        Roughness = 1.0f,
                        // Squashed 2:1 on screen, the mip chain would pick the
                        // level for the short axis and smear the art to its mean.
                        TextureFilter = BaseMaterial3D.TextureFilterEnum.Linear,
                    };
                    mats[kind] = m;
                }
                var centre = new Vector2(q * 0.75f * w, (r + 0.5f * (q & 1)) * h);
                Rect2 rect = terrain.RectAt(centre, scale);
                var tile = new MeshInstance3D
                {
                    Mesh = new PlaneMesh { Size = new Vector2(rect.Size.X, rect.Size.Y / Squash) },
                    MaterialOverride = m,
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                    Position = new Vector3(rect.Position.X + rect.Size.X * 0.5f, 0.0f,
                                           (rect.Position.Y + rect.Size.Y * 0.5f) / Squash),
                };
                AddChild(tile);
            }
        return true;
    }

    // --- flags -----------------------------------------------------------

    /// <summary>Which of the dressing is built - the editor's switches, read
    /// the same way (<see cref="BoardDress.Show"/>).</summary>
    private readonly BoardDress.Show _show = new();

    private void ReadFlags()
    {
        string[] args = OS.GetCmdlineUserArgs();
        for (int i = 0; i < args.Length; i++)
        {
            if (_show.Read(args, ref i))
                continue;
            string a = args[i];
            bool more = i + 1 < args.Length;
            if (a == "--model" && more) { _modelTag = args[++i].ToUpperInvariant(); _modelNamed = true; }
            else if (a == "--sprites" && more)
                GD.Print($"tank3d: --sprites {args[++i]} ignored - the 3D tank reads no sprite set");
            else if (a == "--map" && more) { _mapName = args[++i]; _mapNamed = true; }
            // the bench as a game, two sides of five (Tank3DBench.Match; Match3D.tscn sets it)
            else if (a == "--match") Match = true;
            // the pointer held on a cell, for a capture of the way to it (Tank3DBench.Course)
            else if (a == "--hover" && more)
            {
                string[] qr = args[++i].Split(',');
                if (qr.Length == 2 && int.TryParse(qr[0], out int q) && int.TryParse(qr[1], out int r))
                    _hoverFixed = new Vector2I(q, r);
            }
            else if (a == "--cell" && more)
            {
                string[] qr = args[++i].Split(',');
                if (qr.Length == 2 && int.TryParse(qr[0], out int q) && int.TryParse(qr[1], out int r))
                    _startCell = new Vector2I(q, r);
            }
            // The ram's target (Tank3DBench.Ram): its model, and where it stands -
            // lined up two hexes ahead of the rammer unless a cell is named.
            else if (a == "--target" && more)
            {
                _otherTag = args[++i].ToUpperInvariant();
                _otherWanted = true;
            }
            else if (a == "--target-cell" && more)
            {
                string[] qr = args[++i].Split(',');
                if (qr.Length == 2 && int.TryParse(qr[0], out int q) && int.TryParse(qr[1], out int r))
                    _otherCell = new Vector2I(q, r);
                _otherWanted = true;
            }
            else if (a == "--target-heading" && more) _otherHeading = F(args[++i], _otherHeading);
            else if (a == "--no-target") _targetDefault = false;
            else if (a == "--no-fire-axis") _fireAxis = false;
            else if (a == "--reticle") _reticle = true;
            else if (a == "--no-amphibious") _amphibious = false;
            else if (a == "--no-ripples") _ripples.Enabled = false;
            else if (a == "--no-tracer") _tracerOn = false;
            else if (a == "--tracer-smoke") _tracerSmoke = true;
            else if (a == "--tracer-soft") _tracerSoft = true;
            else if (a == "--free-drive") _rules = false;
            // the rubble stays where it landed (TankBench's flag, same name)
            else if (a == "--no-crumble") WallRig.Crumbles = false;
            else if (a == "--no-cel-relief") _flatRelief = true;
            else if (a == "--scorch") _scorchOn = true;
            else if (a == "--trees" && more) _treeNames = args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries);
            else if (a == "--wood" && more) _woodCount = Math.Max(1, (int)F(args[++i], _woodCount));
            else if (a == "--wind" && more) _wind = F(args[++i], _wind);
            else if (a == "--soft-water") _softWater = true;
            else if (a == "--flat") _flat = true;
            else if (a == "--pbr") _pbr = true;
            else if (a == "--fx2d") _fx2d = true;
            else if (a == "--zoom" && more) { _zoom = F(args[++i], _zoom); _zoomFit = false; }
            else if (a == "--tremble" && more) _trembleLevel = F(args[++i], _trembleLevel);
            else if (a == "--heading" && more) _heading = F(args[++i], _heading);
            else if (a == "--capture" && more) _capturePath = args[++i];
            else if (a == "--no-ui") _noUi = true;
            else if (a == "--capture-at" && more) _captureAt = (int)F(args[++i], _captureAt);
            else if (a == "--edit-at" && more) _editAt = (int)F(args[++i], _editAt);
            else if (a == "--parked") _parkedWanted = true;
            // --sequence DIR FROM TO [STEP]: every STEP-th frame in between, for
            // looking at motion rather than at one moment of it.
            else if (a == "--sequence" && i + 3 < args.Length)
            {
                _sequenceDir = args[++i];
                _sequenceFrom = (int)F(args[++i], 0);
                _sequenceTo = (int)F(args[++i], 0);
                if (i + 1 < args.Length && int.TryParse(args[i + 1], out int step))
                {
                    _sequenceStep = Math.Max(1, step);
                    i++;
                }
            }
            // --do WHAT@FRAME: the same thing the key does, on that frame, so a
            // capture shows an event without anybody at the keyboard.
            else if (a == "--do" && more)
            {
                string[] parts = args[++i].Split('@');
                _script.Add((parts.Length > 1 ? (int)F(parts[1], 0) : 0, parts[0]));
            }
        }
        TakeSession();
        MatchSetup();
    }

    private static float F(string s, float fallback) =>
        float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : fallback;

    // --- events ----------------------------------------------------------

    /// <summary>Everything a key or <c>--do</c> can ask for, by one name.</summary>
    private void Do(string what)
    {
        switch (what)
        {
            case "shot": Shoot(); break;
            case "ricochet-front": Hit(0, false); break;
            case "ricochet-right": Hit(1, false); break;
            case "ricochet-rear": Hit(2, false); break;
            case "ricochet-left": Hit(3, false); break;
            case "pierce-front": Hit(0, true); break;
            case "pierce-right": Hit(1, true); break;
            case "pierce-rear": Hit(2, true); break;
            case "pierce-left": Hit(3, true); break;
            case "he-front": He(0); break;
            case "he-right": He(1); break;
            case "he-rear": He(2); break;
            case "he-left": He(3); break;
            case "ground": FxGround(); break;
            case "burn": FxBurn(true); break;
            case "unburn": FxBurn(false); break;
            case "knock": KnockOut(); break;
            case "destroy": Destroy(); break;
            case "reset": ResetTank(); WallReset(); MinesReset(); CancelOrder(); break;
            case "pond": ToPond(); break;
            case "amphibious": _amphibious = true; break;
            case "no-amphibious": _amphibious = false; break;
            case "heights": SaveHeights(); break;
            // The free drive's script: it switches to the free mode, so a
            // sequence written for the keys still drives as it did.
            case "drive": SetRules(false, " (--do drive)"); _driveScripted = 1.0f; break;
            case "reverse": SetRules(false, " (--do reverse)"); _driveScripted = -1.0f; break;
            case "stop": _driveScripted = 0.0f; break;
            case "left": SetRules(false, " (--do left)"); _turnScripted = 1.0f; break;
            case "right": SetRules(false, " (--do right)"); _turnScripted = -1.0f; break;
            // The cells' orders (Tank3DBench.Orders).
            case "cells": SetRules(true); break;
            case "free": SetRules(false); break;
            case "charge": if (_other is not null) OrderTo(OtherCell(_other), ram: true); break;
            case "cancel": CancelOrder(); break;
            // the end of turn's button (Tank3DBench.Match)
            case "pass": EndTurn(); break;
            case "ram-mode": RamToggle(); break;
            case "douse": Douse(); break;
            case "restart": if (Match) Restart(); break;
            case "way": NextWay(); break;
            case "forks": Forks(); break;
            case "confirm": Confirm(); break;
            case "unlock": Unlock(); break;
            case "select": Choose(true); break;
            case "deselect": Choose(false); break;
            // the left button where the camera shows the own hull's middle
            case "click-own": OrderClick(_camera.UnprojectPosition(_rig.Position + Vector3.Up * 0.3f * HexWidth), false); break;
            case "straight": _turnScripted = 0.0f; break;
            // a wall ahead is rammed through; otherwise the target
            case "ram": if (!WallRamGo(false)) RamGo(); break;
            case "ram-wall": WallRamGo(true); break;
            case "aim-target":
                if (_other is not null)
                    AimCell(OtherCell(_other));
                break;
            // a notch of the mouse wheel
            case "wheel-up": ZoomBy(WheelStep); break;
            case "follow": FollowTank(); break;
            case "wheel-down": ZoomBy(1.0f / WheelStep); break;
            case "target": LineUp(); break;
            case "untarget": RemoveOther(); break;
            default:
                if (what.StartsWith("turret=", StringComparison.Ordinal))
                {
                    if (_model.Turreted)
                        _model.Yaw = Mathf.DegToRad(F(what[7..], 0));
                    else
                        GD.Print($"tank3d: --do {what}: {_modelTag} is a casemate, it has no turret");
                }
                else if (what.StartsWith("elev=", StringComparison.Ordinal))
                    _model.Elevation = Mathf.DegToRad(F(what[5..], 0));
                else if (what.StartsWith("aim-cell=", StringComparison.Ordinal))
                {
                    // aim-cell=Q,R: the right button on that hex.
                    string[] qr = what[9..].Split(',');
                    if (qr.Length == 2 && int.TryParse(qr[0], out int q) && int.TryParse(qr[1], out int r))
                    {
                        if (_rules)
                        {
                            if (!RamOrder(new Vector2I(q, r)))
                                ShotOrder(new Vector2I(q, r));
                        }
                        else
                            AimCell(new Vector2I(q, r));
                    }
                }
                else if (what.StartsWith("pan=", StringComparison.Ordinal))
                {
                    // pan=DX,DY: the middle button dragged that many pixels.
                    string[] xy = what[4..].Split(',');
                    if (xy.Length == 2)
                        PanBy(new Vector2(F(xy[0], 0), F(xy[1], 0)));
                }
                else if (what.StartsWith("wheel-up=", StringComparison.Ordinal) || what.StartsWith("wheel-down=", StringComparison.Ordinal))
                {
                    // wheel-up=X,Y: a notch of the wheel with the cursor at that screen point.
                    bool up = what.StartsWith("wheel-up=", StringComparison.Ordinal);
                    string[] xy = what[(up ? 9 : 11)..].Split(',');
                    if (xy.Length == 2)
                        ZoomBy(up ? WheelStep : 1.0f / WheelStep, new Vector2(F(xy[0], 0), F(xy[1], 0)));
                }
                else if (what.StartsWith("click=", StringComparison.Ordinal))
                {
                    // click=Q,R: the left button where the camera shows that hex's
                    // middle - the mouse's own path, PickAt and all.
                    string[] qr = what[6..].Split(',');
                    if (qr.Length == 2 && int.TryParse(qr[0], out int q) && int.TryParse(qr[1], out int r))
                        OrderClick(_camera.UnprojectPosition(CellWorld(new Vector2I(q, r))), false);
                }
                else if (what.StartsWith("take=", StringComparison.Ordinal))
                {
                    // take=Q,R: the click on the tank standing on that hex,
                    // through the same screen test the pointer gets.
                    string[] qr = what[5..].Split(',');
                    if (qr.Length == 2 && int.TryParse(qr[0], out int q) && int.TryParse(qr[1], out int r)
                        && _parked.Find(p => p.Cell == new Vector2I(q, r)) is { } standing)
                        OrderClick(_camera.UnprojectPosition(standing.Rig.Position
                            + Vector3.Up * (standing.Model.Size.Y * standing.Model.PixelsPerUnit * 0.4f)), false);
                }
                // lock=Q,R: the first click on a cell in reach; face=S: the rose's disc
                else if (what.StartsWith("lock=", StringComparison.Ordinal))
                {
                    string[] qr = what[5..].Split(',');
                    if (qr.Length == 2 && int.TryParse(qr[0], out int q) && int.TryParse(qr[1], out int r))
                        Lock(new Vector2I(q, r));
                }
                else if (what.StartsWith("face=", StringComparison.Ordinal))
                    FaceTo((int)F(what[5..], 90));
                else if (what.StartsWith("go=", StringComparison.Ordinal))
                {
                    // go=Q,R: the left button on that hex, under the rules.
                    string[] qr = what[3..].Split(',');
                    if (qr.Length == 2 && int.TryParse(qr[0], out int q) && int.TryParse(qr[1], out int r))
                    {
                        SetRules(true, " (--do go)");
                        OrderTo(new Vector2I(q, r));
                    }
                }
                else if (what.StartsWith("aim=", StringComparison.Ordinal))
                {
                    // aim=X,Y: the right button at that screen point.
                    string[] xy = what[4..].Split(',');
                    if (xy.Length == 2)
                        AimAt(new Vector2(F(xy[0], 0), F(xy[1], 0)));
                }
                else if (what.StartsWith("heading=", StringComparison.Ordinal))
                    Heading = F(what[8..], _heading);
                else if (what.StartsWith("model=", StringComparison.Ordinal))
                    Pick(what[6..].ToUpperInvariant());
                else if (what.StartsWith("target=", StringComparison.Ordinal))
                    PickOther(what[7..].ToUpperInvariant());
                else
                    GD.Print($"tank3d: --do {what}: no such event");
                break;
        }
    }

    private float _driveScripted, _turnScripted;

    /// <summary><c>--no-cel-relief</c>: the board's prisms as the sprite
    /// boards draw them (<see cref="Stage3D.CelRelief"/> off).</summary>
    private bool _flatRelief;

    /// <summary>The gun fires: the tube strokes back along its bore and the
    /// sprung mass rocks against it - pitch and roll by where the gun points.
    /// </summary>
    private void Shoot()
    {
        if (_fate != Fate.Alive)
            return;
        _sinceShot = 0.0f;
        const float impulse = 2.3f;   // rad/s: a light gun, about two degrees at peak
        // The roof goes opposite the gun: back when it points forward (pitch
        // negative is nose up), to the tank's right when it points left (roll
        // positive is roof to the right).
        _kickPitch.Kick(-impulse * Mathf.Cos(_model.Yaw));
        _kickRoll.Kick(impulse * Mathf.Sin(_model.Yaw));
        FxShot();
        Launch();
    }

    /// <summary>Four sides in the order the previews name them.</summary>
    private static readonly string[] Sides = { "front", "right", "rear", "left" };

    /// <summary>A round arrives from one side: the roof goes away from it and
    /// springs back; a ricochet glances, a penetration rocks harder.</summary>
    private void Hit(int side, bool pierce)
    {
        if (_fate == Fate.Destroyed)
            return;
        Vector3 travel = Travel(side);
        float k = pierce ? 3.2f : 2.8f;
        _kickPitch.Kick(k * travel.Z);
        _kickRoll.Kick(-k * travel.X);
        FxHit(side, travel, pierce);
        RockedOwn(pierce ? 1.0f : 0.75f);
    }

    /// <summary>Which way a round from each side travels, in the hull's frame:
    /// from the front it goes aft (-Z), from the right toward the left (+X).
    /// </summary>
    private static Vector3 Travel(int side) => side switch
    {
        0 => new Vector3(0, 0, -1),
        1 => new Vector3(1, 0, 0),
        2 => new Vector3(0, 0, 1),
        _ => new Vector3(-1, 0, 0),
    };

    /// <summary>HE bursting on a plate: it rocks the hull like a hit.</summary>
    private void He(int side)
    {
        if (_fate == Fate.Destroyed)
            return;
        Vector3 travel = Travel(side);
        _kickPitch.Kick(3.0f * travel.Z);
        _kickRoll.Kick(-3.0f * travel.X);
        FxHe(side, travel);
        RockedOwn(1.0f);
    }

    /// <summary>Knocked out: a penetration from the front, then the turret sits
    /// down in its ring and tips, the gun falls to its stop, the belts go slack
    /// - and it burns.</summary>
    private void KnockOut()
    {
        if (_fate != Fate.Alive)
            return;
        Hit(0, true);
        _fate = Fate.Knocked;
        _sinceFate = 0.0f;
        _speed = 0.0f;
        FxKnocked();
    }

    // The toss, set when the tank is destroyed.
    private sealed class Flying
    {
        public required Node3D Node;
        public Vector3 V;
        public Vector3 Axis;
        public float W;
        public float Half;
        /// <summary>Its longest side, world px - how far it goes down.</summary>
        public float Long;
        public bool Rest;
        /// <summary>Where it came to rest, world Y - what <see cref="Gone"/>
        /// takes it down from.</summary>
        public float? Lay;
    }

    private readonly List<Flying> _flying = new();

    /// <summary>
    /// How far the destroyed tank has left the board, 0 there to 1 gone: the
    /// board's <see cref="Wreck.Presence"/> - one until the fire is out
    /// (<see cref="Wreck.GoneAt"/>), nought <see cref="Wreck.FadeSeconds"/>
    /// later. The rule freed the cell on the frame of the blast
    /// (docs/gdd/states.md, «Уничтожен»: the hulk is a picture, not an
    /// obstacle); this is the picture catching up. The sprite fades out; the
    /// model goes down into the ground, hull, turret on its deck and the
    /// debris round it, eased in and out - a cel model made see-through shows
    /// its own insides and its ink through itself, and the ground hides what
    /// has gone under it. The crater the blast dug is left on a free cell.
    /// </summary>
    private float Gone => _fate == Fate.Destroyed ? Mathf.SmoothStep(0.0f, 1.0f, 1.0f - (float)_wreck.Presence) : 0.0f;

    /// <summary>How deep the wreck goes, world px: all of it, the turret on its
    /// deck as well, and a little more.</summary>
    private float GoneDepth => 1.3f * _model.Size.Y * _model.PixelsPerUnit;

    /// <summary>The destroyed tank's own cell blocks nothing - not a ram's
    /// shove, not a path (docs/gdd/states.md, «Уничтожен», 2026-09-08).</summary>
    private bool Blocks => _fate != Fate.Destroyed;

    /// <summary>How far the wreck is off its ground this frame, world px - its
    /// hop, its settle into the crater, its going under; the camera leaves it
    /// out.</summary>
    private float _wreckLift;
    private Vector3 _tossFrom, _tossTo;
    private float _tossYaw0, _tossYawEnd;
    private Quaternion _tossTilt;
    private readonly List<(Node3D Node, Node Parent, Transform3D Local)> _moved = new();

    /// <summary>Destroyed: the ammunition goes - the debris is thrown out from
    /// the ring and falls bouncing, the turret goes back in a spinning arc, lands
    /// on the engine deck and slides to rest, the hull jolts twice.</summary>
    private void Destroy()
    {
        if (_fate == Fate.Destroyed)
            return;
        if (_fate == Fate.Alive)
        {
            _fate = Fate.Knocked;
            FxKnocked();
        }
        _fate = Fate.Destroyed;
        _sinceFate = 0.0f;
        _speed = 0.0f;
        _kickPitch.Kick(2.5f);
        _heave.Kick(0.35f * 0.3f);

        // A casemate keeps its casemate: nothing is thrown, only the debris.
        if (_model.Turret is { } turret)
        {
            float yawDeg = Mathf.RadToDeg(_model.Yaw);
            TankModel.Landing land = _model.LandingNear(yawDeg + 150.0f);
            _tossFrom = _model.TurretRest;
            _tossTo = land.At;
            _tossYaw0 = yawDeg;
            _tossYawEnd = land.YawDeg + 360.0f * Mathf.Ceil((yawDeg + 300.0f - land.YawDeg) / 360.0f);
            // The landing's rotation with its own yaw taken out, so the spin is
            // ours and the settle is the deck's tilt alone.
            _tossTilt = land.Rest * new Quaternion(Vector3.Up, -Mathf.DegToRad(land.YawDeg));
            _model.TurretOverride = turret.Transform;
        }

        // Debris out of the hierarchy and into the world, thrown out and up
        // from the blast point.
        var rnd = new RandomNumberGenerator { Seed = 3 };
        Vector3 blast = _model.Tank.ToGlobal(_model.BlastAt);
        foreach (TankModel.Piece piece in _model.Debris)
        {
            Node3D n = piece.Node;
            Transform3D world = n.GlobalTransform;
            _moved.Add((n, n.GetParent(), n.Transform));
            n.GetParent().RemoveChild(n);
            AddChild(n);
            n.GlobalTransform = world;
            Vector3 d = world.Origin - blast;
            d.Y = Mathf.Abs(d.Y) + 0.6f * d.Length();
            d = d.Normalized();
            var axis = new Vector3(rnd.RandfRange(-1, 1), rnd.RandfRange(-1, 1), rnd.RandfRange(-1, 1)).Normalized();
            float s = _model.PixelsPerUnit;
            _flying.Add(new Flying
            {
                Node = n,
                V = d * rnd.RandfRange(1.0f, 1.7f) * s,
                Axis = axis,
                W = rnd.RandfRange(6.0f, 14.0f),
                Half = Mathf.Min(piece.Size.X, Mathf.Min(piece.Size.Y, piece.Size.Z)) * 0.5f * s,
                Long = Mathf.Max(piece.Size.X, Mathf.Max(piece.Size.Y, piece.Size.Z)) * s,
            });
        }
        FxDestroyed(blast);
    }

    private void ResetTank()
    {
        _coastTo = null;
        MudClear();
        foreach (var (node, parent, local) in _moved)
        {
            node.GetParent().RemoveChild(node);
            parent.AddChild(node);
            node.Transform = local;
        }
        _moved.Clear();
        foreach (Flying f in _flying)
            f.Node.Visible = true;
        _flying.Clear();
        _model.Visible = true;
        _model.TurretOverride = null;
        _model.Yaw = 0; _model.Elevation = 0; _model.Recoil = 0;
        _model.Droop = 0; _model.Cant = 0; _model.Slackness = 0;
        _tremble.Reset();
        _trembleOn = 1.0f;
        foreach (Spring s in new[] { _kickPitch, _kickRoll, _swayPitch, _swayRoll, _heave, _tip,
                                     _pitPitch, _pitRoll, _pitHeave })
            s.Reset();
        _fate = Fate.Alive;
        _wreckPit = _wreckLanded = false;
        _speed = 0;
        _sinceShot = 99.0f;
        FxReset();
        WaterReset();
        RamReset();
    }

    // --- frame -----------------------------------------------------------

    private static float Smooth(float a, float b, float t)
    {
        float u = Mathf.Clamp((t - a) / (b - a), 0.0f, 1.0f);
        return u * u * (3.0f - 2.0f * u);
    }

    /// <summary>The tube's stroke, 0..1, t seconds after the shot: out in one
    /// frame, held a frame, back on an exponential - <see cref="RecoilLoop"/>'s
    /// shape.</summary>
    private static float Stroke(float t)
    {
        if (t < 0.0f) return 0.0f;
        if (t < 0.04f) return t / 0.04f;
        if (t < 0.08f) return 1.0f;
        return Mathf.Exp(-(t - 0.08f) / 0.12f);
    }

    public override void _Process(double delta)
    {
        // The way back, before the capture - the harness's order: the picture
        // that lands on disk is the editor it returned to.
        if (_editAt >= 0 && _frame == _editAt && _editing)
        {
            Edit();
            return;
        }
        float dt = (float)Math.Min(delta, 0.1);
        foreach (var (frame, what) in _script)
            if (frame == _frame && !_restarted)
                Do(what);

        bool alive = _fate == Fate.Alive;
        float yawIn = (Input.IsKeyPressed(Key.Q) ? 1 : 0) - (Input.IsKeyPressed(Key.E) ? 1 : 0);
        float elevIn = (Input.IsKeyPressed(Key.R) ? 1 : 0) - (Input.IsKeyPressed(Key.F) ? 1 : 0);
        float driveIn = (Input.IsKeyPressed(Key.W) ? 1 : 0) - (Input.IsKeyPressed(Key.S) ? 1 : 0) + _driveScripted;
        float turnIn = (Input.IsKeyPressed(Key.A) ? 1 : 0) - (Input.IsKeyPressed(Key.D) ? 1 : 0) + _turnScripted;
        if (_rules)
        {
            // By cells (Tank3DBench.Orders): the keys are orders, not a throttle,
            // and the turret is the rules'. A/D's turn on the spot is steered here.
            // The T key's ram still drives through _driveScripted.
            // The turret comes onto the target under the pointer (Tank3DBench.Aim).
            yawIn = TrackIn(dt);
            if (elevIn == 0.0f)
                elevIn = LobTrackIn(dt);
            driveIn = _driveScripted;
            turnIn = TurnGoalIn(dt);
        }
        // The right button's point: the gun laid on to it and fired - under the
        // rules not while the route is being driven.
        if (!(_rules && RouteMoving))
            Laying(dt, ref yawIn, ref elevIn, ref turnIn);
        if (alive)
        {
            if (_model.Turreted)          // a casemate aims with its hull
                _model.Yaw += yawIn * Mathf.DegToRad(SlewDeg) * dt;
            _model.Elevation = Mathf.Clamp(_model.Elevation + elevIn * Mathf.DegToRad(ElevDeg) * dt,
                                           Mathf.DegToRad(_model.ElevMin), Mathf.DegToRad(_model.ElevMax));
        }
        else
        {
            driveIn = 0.0f;
            turnIn = 0.0f;
        }

        // Drive: a ramp to the preview's speed, the belts and wheels on the
        // distance, the mass squatting on the pull and nosing down on the stop.
        // Afloat nothing is under the tracks: the class's swimming share
        // (MovementProfile.SwimFraction, the board's cap).
        // In mud the drag (Tank3DBench.Mire).
        float cap = Swimming ? (float)MovementProfile.SwimFraction : MireCap;
        float target, a, turn, step;
        if (RamRunning)
        {
            // A ram has the hull (Tank3DBench.Ram): pushing, waiting against
            // the target, backing off - the keys wait for it, as the board's
            // orders wait for a push.
            float was = _speed;
            step = RamDrive(dt);
            a = (_speed - was) / dt / Accel;
            target = _speed;
            turn = 0.0f;
            turnIn = 0.0f;
        }
        else if (_coastTo is not null)
        {
            // Knocked out on the move (a mine): the way it had, spent on the
            // roll to the cell's middle (Tank3DBench.Mines).
            CoastDrive(dt, out target, out a, out turn, out step);
            turnIn = 0.0f;
        }
        else if (_rules && RouteMoving)
        {
            if (!alive)
            {
                CancelOrder();
                _speed = 0.0f;
                target = a = turn = step = 0.0f;
            }
            else
            {
                RouteDrive(dt, out target, out a, out turn, out step);
                // The route's turn is in degrees a second; the frame below wants
                // it as the free drive's, which is half the turn rate per unit.
                turnIn = turn != 0.0f ? 1.0f : 0.0f;
                turn = Mathf.Clamp(turn, -TurnRate, TurnRate);
            }
        }
        else
        {
            // In a capon the hull keeps to its axis and does not turn.
            if (ShelterHolds(dt))
                turnIn = 0.0f;
            target = Mathf.Clamp(driveIn, -1.0f, 1.0f) * MaxSpeed * cap;
            float accel = Mathf.MoveToward(_speed, target, Accel * dt) - _speed;
            _speed += accel;
            a = accel / dt / Accel;
            turn = Mathf.Clamp(turnIn, -1.0f, 1.0f) * TurnRate * 0.5f;
            Heading += turn * dt;
            float h = Mathf.DegToRad(_heading);
            var ahead = new Vector3(Mathf.Sin(h), 0.0f, Mathf.Cos(h));
            Vector3 next = _rig.Position + ahead * _speed * dt;
            // Another hull in the way: up to it and no further, and the ram if
            // it was driven into (RamMeets). A turn into it on the spot is undone.
            if (RamMeets(ref next) && next == _rig.Position && turn != 0.0f)
                Heading -= turn * dt;
            step = (next - _rig.Position).Dot(ahead) / _model.PixelsPerUnit;
            if (CanDrive(_rig.Position, next, _speed >= 0.0f ? ahead : -ahead))
                _rig.Position = next;
            else
            {
                // The board says no - a cliff, deep water, its edge: the tank stops
                // where it is rather than being steered round it.
                _speed = 0.0f;
                step = 0.0f;
            }
        }
        Settle(dt);
        MinesTick();
        _model.Driven += step;
        _model.Skid += Mathf.DegToRad(turn) * dt * _model.Size.X * 0.5f;
        if (Mathf.Abs(_speed) > 1.0f || Mathf.Abs(turnIn) > 0.0f)
        {
            _nextBump -= Mathf.Abs(step) + Mathf.Abs(turnIn) * dt * 0.1f;
            if (_nextBump <= 0.0f)
            {
                _swayPitch.Kick(_rng.RandfRange(-0.35f, 0.35f));
                _swayRoll.Kick(_rng.RandfRange(-0.25f, 0.25f));
                _nextBump = _rng.RandfRange(0.06f, 0.12f);
            }
        }

        // BodyPitch follows the acceleration: 0.035 rad at full pull, nose up -
        // negative about +X - when pulling away forward.
        float sway = _swayPitch.Step(dt, -0.035f * a * Mathf.Sign(target == 0 ? _speed : target));
        _model.Pitch = _kickPitch.Step(dt) + sway + _pitPitch.Step(dt);
        _model.Roll = _kickRoll.Step(dt) + _swayRoll.Step(dt) + _pitRoll.Step(dt);
        _model.Heave = _heave.Step(dt) + _pitHeave.Step(dt) / Mathf.Max(_model.PixelsPerUnit, 1e-3f);
        Tremble(dt);
        if (_rules)
            TurretByRules(dt);

        _sinceShot += dt;
        _model.Recoil = Stroke(_sinceShot);

        if (_fate != Fate.Alive)
        {
            _sinceFate += dt;
            AdvanceFate(dt);
        }
        WaterTick(dt);
        WallTick(dt);
        _model.Apply();
        FxProcess(dt, _speed, a);
        MarksTick();
        CourseTick();
        AimTick();
        RamFrame(dt, _camera.GlobalBasis);
        ShellsTick(dt);
        WoodTick(dt);
        MatchTick(dt);
        FrameCamera();

        string where = _field is null ? "flat ground"
            : $"{_mapName} {_field.FlatCellAt(Board(_rig.Position))}";
        string fate = Drowning ? "Drowned" : _fate.ToString();
        if (_fate == Fate.Alive && Swimming)
            fate = "Afloat";
        string ram = (_other is null ? "" : $"  target {_other.Tag} {_other.Cell}"
                      + (_ramNote.Length > 0 ? $"  {_ramNote}" : ""))
                     + (_wallNote.Length > 0 ? $"  {_wallNote}" : "");
        string order = (_rules && _orderNote.Length > 0 ? $"  {_orderNote}" : "")
                       + (_courseNote.Length > 0 ? $"  {_courseNote}" : "");
        string status = $"{_modelTag} 3D, class {_profile.Tag}  {where}  {fate}  {_note}{ram}{order}{(_aimNote.Length > 0 ? "  " + _aimNote : "")}{(_judgedNote.Length > 0 ? "  " + _judgedNote : "")}";
        _hud.Text = Match ? MatchHud(status) : status + "\n"
                    + "Space shot   RMB aim and fire (cell fixed: go, then fire)   1-4 ricochet front/right/rear/left   Shift+1-4 pierce   Ctrl+1-4 HE   5 round in the ground\n"
                    + "J burning   K knocked out   X destroyed   T ram   Backspace reset   "
                    + (_rules
                        ? "cells: LMB own select   LMB cell, again go (Enter)   Tab other way   A/D or wheel turn the ghost   LMB x2 on target ram   W/S cell   A/D 60   Esc cancel/let go   "
                        : "WASD drive   " + (_model.Turreted ? "Q/E turret   " : ""))
                    + "R/F gun   -/= wheel zoom to cursor   MMB drag pan   Home follow tank   Tab panel   F12 shot";
        Shots();
        _frame++;
    }

    /// <summary>The engine's tremble on the model, this frame.</summary>
    private void Tremble(float dt)
    {
        _trembleOn = Mathf.MoveToward(_trembleOn, _wreck.Out ? 0.0f : 1.0f, dt * 3.0f);
        _tremble.TopSpeed = _profile.TopSpeed;
        _tremble.Advance(Mathf.Abs(_speed), dt);
        float lever = Mathf.Max(_model.HullLength * _model.PixelsPerUnit, 1.0f);
        float k = (float)EngineTremble.SternLeverPx / lever * _trembleLevel * _trembleOn;
        _model.TremblePitch = (float)_tremble.Pitch * k;
        _model.TrembleYaw = (float)_tremble.Yaw * k;
    }

    private void AdvanceFate(float dt)
    {
        float t = _sinceFate;
        // (a destroyed casemate has no toss to own its turret, and still goes
        // on below: its debris flies)
        if (_fate == Fate.Knocked || (_model.Turreted && _model.TurretOverride is null))
        {
            // Drowning breaks nothing, so the pose does not come with it: the
            // gun, the turret and the belts are as the water found them
            // (TankTick.Drowning, docs/water.md «Утоплен»).
            if (Drowning)
                return;
            // The tip settles with a small overshoot, the gun falls to its stop,
            // the belts sag.
            _model.Cant = _tip.Step(dt, t >= 0.05f ? 1.0f : 0.0f);
            float g = Mathf.Clamp((t - 0.05f) / 0.28f, 0.0f, 1.0f);
            _model.Droop = g * g;
            _model.Slackness = Smooth(0.05f, 0.5f, t);
            return;
        }

        // Destroyed. Gravity so the arc peaks at the sidecar's lift.
        float flight = _model.TossFlight, slide = _model.SlideTime;
        float s = _model.PixelsPerUnit;
        float grav = 8.0f * _model.TossLift / (flight * flight) * s;
        _model.Droop = Smooth(0.0f, 0.6f, t);
        _model.Slackness = Smooth(0.0f, 0.4f, t);
        if (_model.Turreted)
        {
            if (t >= flight && t - dt < flight)
            {
                _kickPitch.Kick(-1.6f);   // the turret lands on the deck: stern down
                FxLanded();
            }

            float tau = Mathf.Min(1.0f, t / flight);
            float u = Mathf.Clamp((t - flight) / slide, 0.0f, 1.0f);
            float e = 1.0f - (1.0f - u) * (1.0f - u);
            float prog = (1.0f - _model.SlideShare) * tau + _model.SlideShare * e;
            Vector3 pos = _tossFrom.Lerp(_tossTo, prog);
            if (tau < 1.0f)
                pos.Y = _tossFrom.Y + (_tossTo.Y - _tossFrom.Y) * tau + 4.0f * _model.TossLift * tau * (1.0f - tau);
            float yaw = _tossYaw0 + (_tossYawEnd - _model.SlideSpin - _tossYaw0) * tau + _model.SlideSpin * e;
            float w = Smooth(0.75f, 1.0f, tau);
            Quaternion rot = Quaternion.Identity.Slerp(_tossTilt, w) * new Quaternion(Vector3.Up, Mathf.DegToRad(yaw));
            _model.TurretOverride = new Transform3D(new Basis(rot), pos);
        }

        foreach (Flying f in _flying)
        {
            if (f.Rest)
                continue;
            f.V.Y -= grav * dt;
            Transform3D x = f.Node.GlobalTransform;
            x.Origin += f.V * dt;
            x.Basis = new Basis(f.Axis, f.W * dt) * x.Basis;
            float floor = BedUnder(x.Origin) + f.Half;
            if (x.Origin.Y < floor)
            {
                x.Origin.Y = floor;
                f.V = new Vector3(f.V.X * 0.5f, -0.3f * f.V.Y, f.V.Z * 0.5f);
                f.W *= 0.5f;
                if (Mathf.Abs(f.V.Y) < 0.15f * s)
                    f.Rest = true;
            }
            f.Node.GlobalTransform = x;
        }
        // The pieces go into the ground with the hull, each by its own size;
        // gone, the whole of it is off the board.
        float gone = Gone;
        foreach (Flying f in _flying)
        {
            if (!f.Rest)
                continue;
            f.Lay ??= f.Node.GlobalPosition.Y;
            Vector3 at = f.Node.GlobalPosition;
            f.Node.GlobalPosition = new Vector3(at.X, f.Lay.Value - gone * 1.2f * Mathf.Max(f.Long, 1.0f), at.Z);
            f.Node.Visible = gone < 1.0f;
        }
        _model.Visible = gone < 1.0f;
    }

    private void Shots()
    {
        bool single = _capturePath is not null && _frame == _captureAt;
        bool seq = _sequenceDir is not null && _frame >= _sequenceFrom && _frame <= _sequenceTo
                   && (_frame - _sequenceFrom) % _sequenceStep == 0;
        if (single || seq)
        {
            Image image = GetViewport().GetTexture().GetImage();
            if (seq)
            {
                Directory.CreateDirectory(_sequenceDir!);
                image.SavePng($"{_sequenceDir}/f{_frame:D4}.png");
            }
            if (single)
            {
                Error err = image.SavePng(_capturePath!);
                GD.Print(err == Error.Ok ? $"capture: {_capturePath}" : $"capture to {_capturePath} failed: {err}");
            }
        }
        // Asked on every frame, not only on a saved one: a sequence whose end
        // is not on its step saved its last frame short of the end and ran on.
        bool done = (_capturePath is null || _frame >= _captureAt)
                    && (_sequenceDir is null || _frame >= _sequenceTo);
        if (done && (_capturePath is not null || _sequenceDir is not null))
            GetTree().Quit();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: true } click)
        {
            AimAt(click.Position);
            return;
        }
        // The left button - an order, under the rules (Tank3DBench.Orders); a
        // double click on the target is the ram.
        if (_rules && @event is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } left)
        {
            OrderClick(left.Position, left.DoubleClick);
            return;
        }
        if (_rules && @event is InputEventKey { Pressed: true, Echo: false } order)
        {
            switch (order.Keycode)
            {
                case Key.W: if (_chosen) OrderStep(back: false); return;
                case Key.S: if (_chosen) OrderStep(back: true); return;
                // with a cell fixed, A/D turn its ghost (Tank3DBench.Ghost)
                case Key.A: if (_lockCell is not null) FaceStep(left: true); else if (_chosen) OrderTurn(left: true); return;
                case Key.D: if (_lockCell is not null) FaceStep(left: false); else if (_chosen) OrderTurn(left: false); return;
                // Ctrl+Enter in a match: the turn handed over (Tank3DBench.Match)
                case Key.Enter or Key.KpEnter: if (Match && order.CtrlPressed) EndTurn(); else Confirm(); return;
                // the fixed cell let go; an order in hand dropped; with none,
                // the tank let go
                case Key.Escape:
                    // the ram (Tank3DBench.RamPlan) off first
                    if (_ramMode && _ramWaiting is null) RamToggle();
                    else if (_lockCell is not null && !_ramLock) Unlock();
                    else if (RouteMoving || _turnGoal is not null || _shotWaiting is not null || _ramWaiting is not null) CancelOrder();
                    else Choose(false);
                    return;
            }
        }
        // The middle button held: the board dragged under the mouse.
        if (@event is InputEventMouseMotion drag && (drag.ButtonMask & MouseButtonMask.Middle) != 0)
        {
            PanBy(drag.Relative);
            return;
        }
        // The wheel: a notch is a tenth in or out; a touchpad's scroll comes
        // in fractions (Factor).
        if (@event is InputEventMouseButton { Pressed: true } wheel
            && wheel.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
        {
            // over a fixed cell the wheel turns its ghost (Tank3DBench.Ghost)
            if (WheelTurns(wheel.Position, wheel.ButtonIndex == MouseButton.WheelUp))
                return;
            float notches = wheel.Factor > 0.0f ? wheel.Factor : 1.0f;
            ZoomBy(Mathf.Pow(WheelStep, wheel.ButtonIndex == MouseButton.WheelUp ? notches : -notches), wheel.Position);
            return;
        }
        if (@event is not InputEventKey { Pressed: true, Echo: false } key)
            return;
        string kind = key.CtrlPressed ? "he-" : key.ShiftPressed ? "pierce-" : "ricochet-";
        // A match plays by the rules: the bench's keys that fire, strike, burn,
        // knock out or reset the tank on the rig past them are off - Backspace
        // restarts the whole match instead (Tank3DBench.Match).
        if (Match && key.Keycode is Key.Space or Key.Key1 or Key.Key2 or Key.Key3 or Key.Key4 or Key.Key5
                or Key.J or Key.K or Key.X or Key.T or Key.Backspace)
        {
            if (key.Keycode == Key.Backspace)
                Restart();
            // J, the bench's fire, puts the unit's fire out (Tank3DBench.Blast)
            else if (key.Keycode == Key.J)
                Douse();
            return;
        }
        switch (key.Keycode)
        {
            case Key.Space: Do("shot"); break;
            case Key.Key1: Do(kind + "front"); break;
            case Key.Key2: Do(kind + "right"); break;
            case Key.Key3: Do(kind + "rear"); break;
            case Key.Key4: Do(kind + "left"); break;
            case Key.Key5: Do("ground"); break;
            case Key.J: Do(_burning ? "unburn" : "burn"); break;
            case Key.K: Do("knock"); break;
            case Key.X: Do("destroy"); break;
            case Key.T: Do("ram"); break;
            case Key.Backspace: Do("reset"); break;
            // the next way of the same cost while there is one; the panel otherwise
            // in a match: the ram on or off (Tank3DBench.RamPlan)
            case Key.Tab: if (Match) RamToggle(); else if (!NextWay()) _panel?.Flip(); break;
            case Key.Home: FollowTank(); break;
            case Key.Minus: ZoomBy(1.0f / 1.25f); break;
            case Key.Equal: ZoomBy(1.25f); break;
            case Key.F5: Edit(); break;
            case Key.F12:
                Directory.CreateDirectory(AssetRoot.Out);
                GetViewport().GetTexture().GetImage().SavePng(AssetRoot.Out + "/tank3d.png");
                break;
        }
    }

    // --- the panel ---------------------------------------------------------

    /// <summary>
    /// The side panel, the benches' own <see cref="ControlPanel"/>: for now one
    /// group - which tank stands on the board.
    ///
    /// The list is what is on disk (<see cref="ModelsOnDisk"/>), each named by
    /// its class; a pick is <see cref="Pick"/>, the same as <c>--do model=</c>.
    /// No file of captions behind it, as the benches' <c>bench.json</c> is:
    /// two rows do not need one, and the code's own captions stand.
    /// </summary>
    private void BuildPanel(CanvasLayer layer)
    {
        _panel = new ControlPanel();
        _panel.Prepare();
        // How the tank is driven (Tank3DBench.Orders): the 2D harness's orders
        // on cells, or the keys.
        _panel.Heading("tank3d.drive", "движение");
        _panel.Toggle("tank3d.drive.cells", "по клеткам, как в 2D  (выкл. — WASD, --free-drive)",
                      () => _rules, v => SetRules(v));
        // the line of fire, beads from the muzzle to the hull (Tank3DBench.Aim)
        _panel.Toggle("tank3d.drive.axis", "линия огня  (--no-fire-axis)", () => _fireAxis, v => _fireAxis = v);
        _panel.Toggle("tank3d.drive.reticle", "прицельный круг  (--reticle)", () => _reticle, v => _reticle = v);
        _panel.Readout("tank3d.drive.note", () => _rules
            ? "ЛКМ по своему — выбрать, ЛКМ — ехать, двойной ЛКМ по цели — таран, ПКМ — выстрел\n"
              + "W/S — клетка вперёд/назад, A/D — 60°, ESC — отмена, без приказа — снять выбор"
              + (_orderNote.Length > 0 ? "\n" + _orderNote : "")
            : "WASD — езда, Q/E — башня, ПКМ — выстрел");
        _panel.Expand("tank3d.drive", true);
        _panel.Heading("tank3d.tank", "танк");
        var labels = new List<string>();
        foreach (string model in _models)
            labels.Add(PairAt(model) is int i and >= 0 ? $"{model}  {Pairs[i].Name}" : model);
        if (labels.Count > 0)
            _panel.Choice("tank3d.tank.model", "модель", labels,
                          () => _models.FindIndex(m => string.Equals(m, _modelTag, StringComparison.OrdinalIgnoreCase)),
                          i => Pick(_models[i]));
        _panel.Readout("tank3d.tank.note", () =>
            $"класс {_profile.Tag} x{_profile.Size:F2}, {_model.PixelsPerUnit:F1} px на единицу"
            + (_model.Turreted ? "" : ", без башни"));
        _panel.Expand("tank3d.tank", true);
        // Whether the pond is swum or drowned in - see TankTick.Amphibious.
        _panel.Heading("tank3d.water", "вода");
        _panel.Toggle("tank3d.water.amphibious", "ОПВТ: плывёт, без него тонет",
                      () => _amphibious, v => _amphibious = v);
        _panel.Toggle("tank3d.water.ripples", "рябь  (--no-ripples)", () => _ripples.Enabled, v =>
        {
            _ripples.Enabled = v;
            if (!v)
                _ripples.Settle();
        });
        _panel.Press("tank3d.water.pond", "к пруду: на берег, носом в воду", ToPond);
        _panel.Readout("tank3d.water.note", () =>
            $"палуба {_deckPx:F0} px, осадка {_deckPx * _profile.Draught:F0} px"
            + (Drowning ? $", тонет {Sink:P0}" : Swimming ? ", на плаву" : ""));
        _panel.Expand("tank3d.water", true);
        // The ram (Tank3DBench.Ram): which hull is rammed, where it stands, go.
        _panel.Heading("tank3d.ram", "таран");
        if (labels.Count > 0)
            _panel.Choice("tank3d.ram.target", "цель", labels,
                          () => _models.FindIndex(m => string.Equals(m, _otherTag, StringComparison.OrdinalIgnoreCase)),
                          i => PickOther(_models[i]));
        _panel.Press("tank3d.ram.place", "цель впереди: два гекса прямо, бортом", () => LineUp());
        _panel.Press("tank3d.ram.go", "таранить  (T)", RamGo);
        _panel.Press("tank3d.ram.remove", "убрать цель", RemoveOther);
        _panel.Readout("tank3d.ram.note", () => _other is null
            ? "цели нет"
            : $"{_other.Tag}, масса {_other.Profile.Mass} против {_profile.Mass}"
              + (_ramNote.Length > 0 ? $"\n{_ramNote}" : ""));
        _panel.Expand("tank3d.ram", true);
        // The brick walls (Tank3DBench.Walls): the ram through one.
        if (_walls.Count > 0)
        {
            _panel.Heading("tank3d.walls", "стены");
            _panel.Press("tank3d.walls.ram", "таранить стену  (T носом к стене)", () => WallRamGo(true));
            _panel.Readout("tank3d.walls.note", () =>
                (_profile.Bulldozes ? "HT ломает стены ездой" : "стена держит; таран — приказом")
                + (_wallNote.Length > 0 ? "\n" + _wallNote : ""));
            _panel.Expand("tank3d.walls", true);
        }
        // The round in the air (Tank3DBench.Shell): the sprites' two switches.
        _panel.Heading("tank3d.shell", "снаряд");
        _panel.Toggle("tank3d.shell.tracer", "трассер  (--no-tracer)", () => _tracerOn, v => _tracerOn = v);
        _panel.Toggle("tank3d.shell.smoke", "дымный след  (--tracer-smoke)", () => _tracerSmoke, v => _tracerSmoke = v);
        _panel.Toggle("tank3d.shell.soft", "трассер без полос  (--tracer-soft)", () => _tracerSoft, v => _tracerSoft = v);
        _panel.Expand("tank3d.shell", true);
        // The wreck (Tank3DBench.Fx): what it leaves on the ground.
        _panel.Heading("tank3d.wreck", "обломок");
        _panel.Toggle("tank3d.wreck.scorch", "выжженное пятно  (--scorch)", () => _scorchOn, v =>
        {
            _scorchOn = v;
            if (!v)
                _celScorch?.Reset();
        });
        _panel.Expand("tank3d.wreck", true);
        layer.AddChild(_panel);
        _panel.AddHandle();
    }
}
