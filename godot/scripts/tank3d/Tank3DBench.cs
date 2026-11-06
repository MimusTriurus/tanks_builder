using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
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
    /// <summary>The board as one smooth ground (BOT-11, <see cref="Relief"/>),
    /// its levels joined by slopes, masonry on flat pads (BOT-12), in a sea:
    /// the board by default since 2026-11-01 (the user's call, docs/relief.md).
    /// <c>--ramps</c>: the old prisms and ramps.</summary>
    private bool _reliefOn = true;

    /// <summary>The rocks' looks on the relief (<c>--rock</c>, docs/rocks-plan.md):
    /// one look for every rock or a list dealt out in reading order, dressed
    /// over what the map's notes say - the look changes, not the map. Null -
    /// the map's own (<see cref="RockStyles.Of"/>).</summary>
    private RockStyles? _rocks;
    /// <summary><c>--rock-wind</c>, <c>--ice</c>: over the map's climate; null
    /// - the map's.</summary>
    private float? _rockWind, _rockIce;
    /// <summary>The ice the relief was built by, for the shader's striae.</summary>
    private float _iceBuilt = RockClimate.DefaultIce;
    /// <summary>The wind it was built by, for the dunes' ripples.</summary>
    private float _windBuilt = RockClimate.DefaultWind;
    /// <summary><c>--soil e|k|n|b</c>: every floor that look is for dressed in
    /// it, over the map's soil - for pictures, as <c>--rock</c>.</summary>
    private char? _soilAll;
    /// <summary><c>--brow</c>: the relief's brow lines on from the start
    /// (off by default, the user's call; the panel switches them).</summary>
    private bool _browOn;
    /// <summary><c>--lawn</c>: the lawn photograph on the flats, not the
    /// relief's painted grass (<see cref="Stage3D.ShowLawn"/>).</summary>
    private bool _lawnOn;
    /// <summary><c>--bed</c>: the pebble photograph on the water's bed, not
    /// the flat ink (<see cref="Stage3D.ShowBed"/>; docs/new_style.md).</summary>
    private bool _bedOn;
    /// <summary><c>--earth</c>: the earth photograph in place of the soil art
    /// (<see cref="Stage3D.ShowEarth"/>; docs/new_style.md).</summary>
    private bool _earthOn;
    /// <summary><c>--crag</c>: the rocks in photographs
    /// (<see cref="Stage3D.ShowCrag"/>; docs/new_style.md).</summary>
    private bool _cragOn;
    /// <summary><c>--sand</c>: the sand in a photograph's grain
    /// (<see cref="Stage3D.ShowSand"/>; docs/new_style.md).</summary>
    private bool _sandOn;
    /// <summary><c>--forest</c>: the forest floor in a photograph
    /// (<see cref="Stage3D.ShowForest"/>; docs/new_style.md).</summary>
    private bool _forestOn;
    /// <summary><c>--road-photo</c>: the roads' surfaces in photographs
    /// (<see cref="Stage3D.ShowRoadPhoto"/>; docs/new_style.md).</summary>
    private bool _roadPhotoOn;
    /// <summary><c>--no-turf</c>: no grass on the relief's ground texture; with
    /// it the ground is bare soil everywhere (<see cref="Stage3D.ShowTurf"/>).</summary>
    private bool _turfOn = true;
    private Relief? _relief;
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

    /// <summary>Which class each model drives as - <see cref="Lineup.Pairs"/>,
    /// shared with the editor, which stands the same tanks on its homes.</summary>
    private static (string Model, string Class, string Name)[] Pairs => Lineup.Pairs;

    private static int PairAt(string model) => Lineup.PairAt(model);

    private static MovementProfile ClassFor(string model) => Lineup.ClassFor(model);

    /// <summary>World px a model unit, for this model in this class
    /// (<see cref="Lineup.PixelsFor"/>).</summary>
    private float PixelsFor(TankModel model, MovementProfile profile) =>
        Lineup.PixelsFor(model, profile, HexWidth);

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

    public override void _Ready() => _ = Build();

    /// <summary>The bench is built: until then the frame and the input wait.
    /// Only ever false across frames under the editor's veil
    /// (<see cref="Loading.Stage"/>); launched straight, the build is over
    /// before <c>_Ready</c> returns.</summary>
    private bool _built;

    /// <summary>The bench, built in stages the editor's veil shows a bar for
    /// (<see cref="Loading"/>).</summary>
    private async Task Build()
    {
        L.Apply();
        // --battle-run: a match on the rules core alone, nothing of the scene
        // built (BattleRun, docs/battle-core-plan.md Д10); the frame stays waiting
        if (BattleRun.Asked(OS.GetCmdlineUserArgs()))
        {
            GetTree().Quit(BattleRun.Main(OS.GetCmdlineUserArgs()));
            return;
        }
        // --ai-tasks: the AI's tactical problems, nothing of the scene built (AiTasks)
        if (AiTasks.Asked(OS.GetCmdlineUserArgs()))
        {
            GetTree().Quit(AiTasks.Main(OS.GetCmdlineUserArgs()));
            return;
        }
        // --map-fair: is a duel map fair - a reading on the core, no scene (MapFair)
        if (MapFair.Asked(OS.GetCmdlineUserArgs()))
        {
            GetTree().Quit(MapFair.Main(OS.GetCmdlineUserArgs()));
            return;
        }
        // --fire-study: the roof against fire over the hulls (FireStudy)
        if (FireStudy.Asked(OS.GetCmdlineUserArgs()))
        {
            GetTree().Quit(FireStudy.Main(OS.GetCmdlineUserArgs()));
            return;
        }
        // --range-study: how far the guns kill, and what a range would take (RangeStudy)
        if (RangeStudy.Asked(OS.GetCmdlineUserArgs()))
        {
            GetTree().Quit(RangeStudy.Main(OS.GetCmdlineUserArgs()));
            return;
        }
        try
        {
            await Building();
        }
        catch (Exception e)
        {
            // An async build throws into its task, where nobody looks: said
            // here, and the veil let go of, rather than a black screen.
            GD.PrintErr($"tank3d: the build threw: {e}");
        }
        _built = true;
        Loading.Built(this);
    }

    private async Task Building()
    {
        await Loading.Stage(this, "tile and world", 0.0f);
        ReadFlags();
        _models = ModelsOnDisk();
        _rig = new Node3D { Name = "Rig" };
        AddChild(_rig);
        // The board's tile first: the camera's angle, the board's size and the
        // effects' pools all come off it, and none of them follows the tank.
        _tile = LoadTile("MTP");
        // the quick battle's lobby and build, before the board they choose (Tank3DBench.Quick)
        if (Quick && Match)
        {
            await QuickScreens();
            // the board, the wood, the grass and the six built next, seconds of it:
            // under a veil with its bar, not on the hangar's last frame
            await Loading.Cover(this, L.T("loading.battle"));
        }

        GetViewport().Msaa3D = Viewport.Msaa.Msaa4X;
        BuildWorld();
        await Loading.Stage(this, "board", 0.04f);
        if (!_flat)
            BuildBoard();
        // After the board: the stage makes its own camera current as it enters
        // the tree, and this one has to be current over it.
        BuildCamera();
        if (_field is null)
            BuildGround();
        await Loading.Stage(this, "trees", 0.12f);
        await BuildWood();
        await Loading.Stage(this, "mines and marks", 0.6f);
        BuildMines();
        BuildMarks();
        await Loading.Stage(this, $"tank: {_modelTag}", 0.62f);
        Park();
        Mount(_modelTag);
        await BuildParked();
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

        await Loading.Stage(this, "panels", 0.9f);
        var layer = new CanvasLayer();
        AddChild(layer);
        // the status and controls text, top left: off the screen, F1 (F9 in a
        // match) shows it as a readout for debugging
        _hud = new Label { Position = new Vector2(12, 8), Modulate = new Color(1, 1, 1, 0.85f), Visible = false };
        layer.AddChild(_hud);
        // under the panel, which is drawn after it
        BuildCourse(layer);
        BuildAim(layer);
        BuildCard(layer);
        BuildPeek(layer);
        if (!_noUi)
        {
            BuildPanel(layer);
            // the bench's tuning, not the board's: folded for a board the
            // editor sent, Tab opens it
            if (_handed)
                _panel?.Flip();
        }
        BuildBack(layer);
        BuildMatchUi(layer);
        BuildPerf(layer);
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
            Log.Print($"tank3d: no {tag} tile ({e.Message}) - flat ground, 248 px hex");
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
        TankModel next;
        using (Prof.Measure("mount.load"))
            next = TankModel.Load(model, toon: !_pbr, bare: Bare);
        Mount(next, model);
    }

    /// <summary>
    /// The scenes <see cref="TankModel.Load"/> kept per tag, dressed and
    /// unposed: the ghosts are copied out of them (<see cref="TankModel.Copy"/>)
    /// instead of reading the glTF again on every hand-over.
    ///
    /// <b>Out of the tree</b>, freed with the bench: under it, hidden, the
    /// bench's own walks over its nodes found them - the rig's measures and the
    /// blast went from 10 to 100 ms. So the perf run counts them as orphans, a
    /// constant ~230 a match, and a leak is a count that grows past that.
    /// </summary>
    private Node Bare => _bareNode ??= new Node { Name = "BareModels" };
    private Node? _bareNode;

    public override void _ExitTree()
    {
        CursorReset();
        _bareNode?.Free();
        _bareNode = null;
        // a quit from the bench: the editor parked behind it goes too
        // (Session.Parked), or the run ends on Godot's leak report. Not on a
        // restart, and on the way back Loading.Back has already taken it.
        if (!_keepEditor && Session.Parked is { } parked && IsInstanceValid(parked))
        {
            Session.Parked = null;
            parked.Free();
        }
        // a quit leaves the tree: the run's last lines out before the process goes
        Log.Drain();
    }

    /// <summary>A model already loaded on the rig - the one a hand-over takes
    /// off its parking (<see cref="TakeOver"/>). Whatever the rig had is
    /// freed; <see cref="Unmount"/> with <c>keep</c> first to have it back.</summary>
    private void Mount(TankModel next, string model)
    {
        MovementProfile profile = ClassFor(model);
        next.ScaleTo(PixelsFor(next, profile));
        using (Prof.Measure("mount.unmount"))
            Unmount();
        _modelTag = model;
        _profile = profile;
        _model = next;
        SidePaint(_model, _side);
        _rig.AddChild(_model);
        _beltPaint = null;
        RigMeasures known;
        using (Prof.Measure("mount.measure"))
            known = MeasureRig(_model, _rig, model);
        (_deckPx, _foot, _roofPx) = (known.Deck, known.Foot, known.Roof);
        using (Prof.Measure("mount.effects"))
            BuildEffects();
    }

    /// <summary>What <see cref="Mount"/> measures off a model on the rig: the
    /// deck and the roof, lift px, and the footprint.</summary>
    private readonly record struct RigMeasures(
        float Deck, (float Along, float Across, float HalfLen, float HalfWide) Foot, float Roof);

    /// <summary>
    /// <see cref="RigMeasures"/> per tag, taken once: the same tank measures the
    /// same on every hand-over, and the measures walk every vertex of it - 8-16 ms,
    /// the mesh read back from the GPU. A match takes them as it stands its tanks
    /// up (<see cref="Stand"/>), so no hand-over measures or prints: a print in a
    /// frame costs up to 100 ms (docs/perf.md, BOT-7).
    /// </summary>
    private readonly Dictionary<string, RigMeasures> _measured = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The tag's <see cref="RigMeasures"/>, off <paramref name="model"/>
    /// standing on <paramref name="rig"/> unturned the first time - with the
    /// tank's line and its footprint printed then - and kept after.</summary>
    private RigMeasures MeasureRig(TankModel model, Node3D rig, string tag)
    {
        if (_measured.TryGetValue(tag, out RigMeasures known))
            return known;
        known = new RigMeasures(MeasureDeck(model), MeasureFootprint(model, rig, tag), MeasureRoof(model));
        _measured[tag] = known;
        MovementProfile profile = ClassFor(tag);
        Log.Print($"tank3d: {tag} class {profile.Tag} x{profile.Size:F2}, {model.PixelsPerUnit:F2} px/unit, "
                 + $"hull {model.HullLength:F4} x {model.HullWidth:F4} = {model.HullLength * model.PixelsPerUnit:F0} px, "
                 + $"bore r {model.BoreRadius:F4}, deck {known.Deck:F1} px, roof {known.Roof:F1} of {model.Size.Y * model.PixelsPerUnit * RiseFactor:F1}, squash {Squash:F4} = {Mathf.RadToDeg(Mathf.Asin(Squash)):F2} deg, "
                 + $"hex {HexWidth:F0} px");
        return known;
    }

    /// <summary>Take the tank off the rig: everything <see cref="Mount"/> and
    /// <see cref="BuildEffects"/> made for it. Reset first, so the debris a
    /// destroyed tank threw is back in the model that gets freed.</summary>
    /// <param name="keep">Hand the model back, wiped as loaded
    /// (<see cref="TankModel.Wipe"/>), instead of freeing it.</param>
    private TankModel? Unmount(bool keep = false)
    {
        if (_model is null)
            return null;
        ResetTank();
        FreeEffects();
        _paint.Clear();
        _hullHits = null;
        _hitLocal = Vector3.Zero;
        TankModel was = _model;
        _rig.RemoveChild(was);
        _model = null!;
        if (keep)
        {
            was.Wipe();
            Uncharred(was);
            return was;
        }
        was.QueueFree();
        return null;
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
            _note = L.T("bench.match_take_click");
            return;
        }
        try
        {
            Mount(model);
        }
        catch (Exception e)
        {
            Log.Print($"tank3d: {model}: {e.Message}");
            _note = $"{model} did not load";
        }
    }

    /// <summary>How far the camera stands back along its view: the stage's own
    /// number and for its reason - an orthographic depth buffer is linear, and
    /// at 4000 back the board's rims, lifted two units off the faces, fell into
    /// its noise (<c>Stage3D.Back</c>).</summary>
    private const float Back = 1500.0f;

    /// <summary>Depth kept past the board's ground either way, for what
    /// stands on it and for the frame (<see cref="FrameCamera"/>).</summary>
    private const float DepthSpare = 600.0f;

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
        _glideTo = null;
        if (cursor is not Vector2 c)
            return;
        Vector2 window = GetViewport().GetVisibleRect().Size;
        Vector2 off = c - 0.5f * window;
        Vector2 at = _viewAt ?? _viewNow;
        // the plane point under the cursor, kept: at + off·(H/was)/H = at' + off·(H/zoom)/H
        _viewAt = at + PlaneOf(off) * (1.0f / was - 1.0f / _zoom);
    }

    /// <summary>
    /// Screen pixels as the view's plane units (<see cref="_viewAt"/>: x the
    /// ground's X, y its Z drawn at the game's angle) at one plane unit a
    /// pixel: the screen itself at the game's angle; turned (<see cref="Orbit"/>),
    /// the screen's right and down laid on the ground first.
    /// </summary>
    private Vector2 PlaneOf(Vector2 screen)
    {
        if (!Orbited)
            return screen;
        float pitch = Mathf.Asin(Squash) + _orbitPitch;
        var right = new Vector3(Mathf.Cos(_orbitYaw), 0.0f, -Mathf.Sin(_orbitYaw));
        var down = new Vector3(Mathf.Sin(_orbitYaw), 0.0f, Mathf.Cos(_orbitYaw)) / Mathf.Sin(pitch);
        Vector3 m = right * screen.X + down * screen.Y;
        return new Vector2(m.X, m.Z * Squash);
    }

    /// <summary>
    /// The point on the ground the view looks at, as (X, Z·<see cref="Squash"/>)
    /// - <see cref="PlaneOf"/>'s units - once the player has moved it: the
    /// keys (<see cref="PanKeys"/>) or the wheel about the cursor; null while
    /// it follows the tank. <c>Home</c> lets go.
    /// </summary>
    private Vector2? _viewAt;

    /// <summary>The view's middle as last framed, held inside the board -
    /// what a drag or the wheel starts from while the view still follows.</summary>
    private Vector2 _viewNow;

    /// <summary>The view turned off the game's angle by the middle button
    /// (<see cref="OrbitBy"/>): about the up axis, and its pitch added to the
    /// game's, radians. Both nought is the game's view; Home brings it back.</summary>
    private float _orbitYaw, _orbitPitch;

    /// <summary>How far the drag turns the view, radians a screen px.</summary>
    private const float OrbitRate = 0.006f;
    /// <summary>The pitch's bounds, degrees off the ground: not flat, where
    /// the far plane and the sun's map run out, nor straight down.</summary>
    private const float OrbitPitchLow = 20.0f, OrbitPitchHigh = 85.0f;

    private bool Orbited => _orbitYaw != 0.0f || _orbitPitch != 0.0f;

    /// <summary>
    /// The view the bench opens at and Home returns to (the author's choice,
    /// 2026-10-30): isometric - 35.26° above the ground, the angle at which
    /// the three axes of a cube are drawn alike - turned off the board's axis
    /// by 60°, a step of the turn (<see cref="YawStepDeg"/>; it was 45°, which
    /// no step reaches, until 2026-11-01). <c>--game-angle</c> opens at the
    /// old one instead, the sprites' (yaw 0, the pitch the hex's art is drawn
    /// at): a capture to set beside one from before.
    /// </summary>
    private const float IsoYawDeg = -60.0f, IsoPitchDeg = 35.264f;

    /// <summary>The view's turn goes in steps of a hex's side (the author's
    /// choice, 2026-11-01, as tactics games turn theirs): from one the
    /// flat-topped hexes look as from any other. Q/E in a match turn a step;
    /// the middle button turns freely and, let go, the view comes round to the
    /// nearest step.</summary>
    private const float YawStepDeg = 60.0f;

    /// <summary>The nearest step of the turn to <paramref name="yaw"/>, radians.</summary>
    private static float YawStep(float yaw)
    {
        float step = Mathf.DegToRad(YawStepDeg);
        return Mathf.Wrap(Mathf.Round(yaw / step) * step, -Mathf.Pi, Mathf.Pi);
    }

    /// <summary>Q/E: the view a step round, from where a turn under way is going.</summary>
    private void TurnViewStep(int way)
    {
        float from = _turnT >= 0.0f ? _turnYawTo : YawStep(_orbitYaw);
        CameraTurnTo(from + way * Mathf.DegToRad(YawStepDeg), Mathf.Asin(Squash) + (_turnT >= 0.0f ? _turnPitchTo : _orbitPitch));
    }

    /// <summary>The middle button let go: the view round to the nearest step,
    /// at the pitch it was left at.</summary>
    private void OrbitLetGo()
    {
        float yaw = YawStep(_orbitYaw);
        if (Mathf.Abs(Mathf.Wrap(yaw - _orbitYaw, -Mathf.Pi, Mathf.Pi)) > 1e-4f)
            CameraTurnTo(yaw, Mathf.Asin(Squash) + _orbitPitch);
    }
    private bool _gameAngle;
    /// <summary>The default to be put on at the next frame: its pitch is an
    /// offset from the game's, which needs the board (<see cref="Squash"/>).</summary>
    private bool _angleDefault = true;

    /// <summary>
    /// The middle button held and dragged (the author's choice, 2026-10-30):
    /// across - the view turns about the up axis; along - it tilts. The
    /// board is real 3D under the game's view (only the angle makes it look
    /// as the sprites did), so it turns whole; what is drawn for the game's
    /// angle - the sprite effects, the marks lifted toward the eye - is
    /// right only there, and Home brings it back.
    /// </summary>
    private void OrbitBy(Vector2 screen)
    {
        _glideTo = null;
        // the hand takes the view from a turn under way (Tank3DBench.Deploy)
        _turnT = -1.0f;
        _orbitYaw = Mathf.Wrap(_orbitYaw - screen.X * OrbitRate, -Mathf.Pi, Mathf.Pi);
        float game = Mathf.Asin(Squash);
        _orbitPitch = Mathf.Clamp(_orbitPitch + screen.Y * OrbitRate,
                                  Mathf.DegToRad(OrbitPitchLow) - game, Mathf.DegToRad(OrbitPitchHigh) - game);
    }

    /// <summary>The default angle put on, once the board is there (its pitch
    /// is an offset from the game's, <see cref="Squash"/>).</summary>
    private void AngleDefault()
    {
        if (!_angleDefault)
            return;
        _angleDefault = false;
        _orbitYaw = _gameAngle ? 0.0f : Mathf.DegToRad(IsoYawDeg);
        _orbitPitch = _gameAngle ? 0.0f : Mathf.DegToRad(IsoPitchDeg) - Mathf.Asin(Squash);
    }

    /// <summary>The view's pitch above the ground, radians.</summary>
    private float ViewPitch => Mathf.Asin(Squash) + _orbitPitch;

    /// <summary>Toward the eye from the ground, unit: the camera stands along it.</summary>
    private Vector3 ViewBack
    {
        get
        {
            float p = ViewPitch;
            return new Vector3(Mathf.Sin(_orbitYaw) * Mathf.Cos(p), Mathf.Sin(p), Mathf.Cos(_orbitYaw) * Mathf.Cos(p));
        }
    }

    /// <summary>The screen's right and up in the world, at the view's angle.</summary>
    private Vector3 ViewRight => new(Mathf.Cos(_orbitYaw), 0.0f, -Mathf.Sin(_orbitYaw));

    private Vector3 ViewUp => ViewBack.Cross(ViewRight);

    /// <summary>The point on the ground's zero drawn where <paramref name="world"/>
    /// is: down the line of sight.</summary>
    private Vector3 GroundUnder(Vector3 world)
    {
        Vector3 back = ViewBack;
        return world - back * (world.Y / Mathf.Max(back.Y, 0.05f));
    }

    /// <summary>How fast the keys move the view, screen px a second; Shift doubles it.</summary>
    private const float PanSpeed = 900.0f;

    /// <summary>
    /// The view moved by the keys held (the author's choice, 2026-10-30, for
    /// the middle button's drag): the arrows always, W/A/S/D in a match -
    /// outside one they drive the tank or step its order. Every frame, before
    /// the flight (<see cref="GlideTick"/>), which a key cuts as a drag did.
    /// </summary>
    private void PanKeys(float dt)
    {
        bool wasd = Match;
        float x = (Input.IsKeyPressed(Key.Right) || (wasd && Input.IsKeyPressed(Key.D)) ? 1.0f : 0.0f)
                  - (Input.IsKeyPressed(Key.Left) || (wasd && Input.IsKeyPressed(Key.A)) ? 1.0f : 0.0f);
        float y = (Input.IsKeyPressed(Key.Down) || (wasd && Input.IsKeyPressed(Key.S)) ? 1.0f : 0.0f)
                  - (Input.IsKeyPressed(Key.Up) || (wasd && Input.IsKeyPressed(Key.W)) ? 1.0f : 0.0f);
        if (x == 0.0f && y == 0.0f)
            return;
        float speed = PanSpeed * (Input.IsKeyPressed(Key.Shift) ? 2.0f : 1.0f);
        // the view goes the key's way: the board under it the other
        PanBy(-new Vector2(x, y).Normalized() * speed * dt);
    }

    /// <summary>The board moved by <paramref name="screen"/> pixels under the
    /// view - the keys (<see cref="PanKeys"/>) and <c>--do pan=X,Y</c>.</summary>
    private void PanBy(Vector2 screen)
    {
        float h = Mathf.Max(GetViewport().GetVisibleRect().Size.Y, 1.0f);
        _glideTo = null;
        _viewAt = (_viewAt ?? _viewNow) - PlaneOf(screen) * (_camera.Size / h);
    }

    /// <summary>Back to following the tank, at the default angle.</summary>
    private void FollowTank()
    {
        _glideTo = null;
        _turnT = -1.0f;
        _viewAt = null;
        _angleDefault = true;
        Log.Print("tank3d: camera follows the tank");
    }

    /// <summary>How long the view takes to fly to a unit taken, s.</summary>
    private const float GlideTime = 0.45f;

    /// <summary>A flight of the view's middle under way: from where it stood
    /// to the unit, eased in and out; the keys, the wheel or Home end it.</summary>
    private Vector2? _glideTo;
    private Vector2 _glideFrom;
    private float _glideT;

    /// <summary>
    /// The view's middle flown to <paramref name="world"/>, the unit taken
    /// (<see cref="TakeOver"/>, the roster): from the middle as last framed,
    /// not a jump. Following the tank (<see cref="FollowTank"/>) holds the view
    /// inside the board and at the fit did not move; the flight ends as a view
    /// the player moved, whose middle only is kept over the board
    /// (<c>FrameCamera</c>), so the unit stands in the middle of the screen.
    /// </summary>
    private void GlideTo(Vector3 world)
    {
        _glideFrom = _viewNow;
        // the view's middle is a point on the ground: the one drawn where the unit is
        Vector3 at = GroundUnder(world);
        _glideTo = new Vector2(at.X, at.Z * Squash);
        _glideT = 0.0f;
        _viewAt = _glideFrom;
    }

    /// <summary>Every frame, before <c>FrameCamera</c>.</summary>
    private void GlideTick(float dt)
    {
        if (_glideTo is not Vector2 to)
            return;
        _glideT = Mathf.Min(1.0f, _glideT + dt / GlideTime);
        _viewAt = _glideFrom.Lerp(to, Mathf.SmoothStep(0.0f, 1.0f, _glideT));
        if (_glideT >= 1.0f)
            _glideTo = null;
    }

    /// <summary>
    /// The board as a box in the world, measured once (the board does not
    /// move): the cells' ground and, on the relief, its sea and frame round
    /// them (<c>Relief.Frame</c>), on the plan - x the world's X, y its Z - and
    /// the lowest and highest the ground goes. Null on the flat ground.
    /// </summary>
    private (Rect2 Plan, float Low, float High)? BoardGround()
    {
        if (_boardGround is not null || _field is null || _tile is null)
            return _boardGround;
        Vector2 half = (Vector2)_tile.HexRect.Size * 0.5f;
        var lo = new Vector2(float.MaxValue, float.MaxValue);
        var hi = new Vector2(float.MinValue, float.MinValue);
        float low = 0.0f, high = 0.0f;
        for (int r = 0; r < _field.Rows; r++)
        for (int q = 0; q < _field.Columns; q++)
        {
            var cell = new Vector2I(q, r);
            if (!_field.InBounds(cell))
                continue;
            Vector2 c = _field.FlatAnchor(cell) + _field.CentreOffset;
            var plan = new Vector2(c.X, c.Y / Squash);
            // the lift is drawn up the screen at the game's angle: the height over it
            float y = LiftAt(new Vector3(plan.X, 0.0f, plan.Y)) / RiseFactor;
            low = Mathf.Min(low, y);
            high = Mathf.Max(high, y);
            lo = new Vector2(Mathf.Min(lo.X, plan.X - half.X), Mathf.Min(lo.Y, plan.Y - half.Y / Squash));
            hi = new Vector2(Mathf.Max(hi.X, plan.X + half.X), Mathf.Max(hi.Y, plan.Y + half.Y / Squash));
        }
        if (lo.X > hi.X)
            return null;
        if (_reliefOn && _relief is not null)
        {
            Rect2 frame = _relief.Frame.Grow(Stage3D.FrameWide * half.X);
            lo = new Vector2(Mathf.Min(lo.X, frame.Position.X), Mathf.Min(lo.Y, frame.Position.Y));
            hi = new Vector2(Mathf.Max(hi.X, frame.End.X), Mathf.Max(hi.Y, frame.End.Y));
        }
        // what stands on it - a tree, a hull, the mesa's top - over the highest ground
        _boardGround = (new Rect2(lo, hi - lo), low - half.X, high + 2.0f * half.X);
        return _boardGround;
    }

    private (Rect2 Plan, float Low, float High)? _boardGround;

    /// <summary>The board box's eight corners on the screen at the view's angle,
    /// world units: across along the screen's right, up along its up.</summary>
    private Rect2 BoardOnScreen((Rect2 Plan, float Low, float High) board)
    {
        Vector3 right = ViewRight, up = ViewUp;
        float x0 = float.MaxValue, x1 = float.MinValue, y0 = float.MaxValue, y1 = float.MinValue;
        for (int i = 0; i < 8; i++)
        {
            var corner = new Vector3((i & 1) != 0 ? board.Plan.End.X : board.Plan.Position.X,
                                     (i & 4) != 0 ? board.High : board.Low,
                                     (i & 2) != 0 ? board.Plan.End.Y : board.Plan.Position.Y);
            float x = corner.Dot(right), y = corner.Dot(up);
            x0 = Mathf.Min(x0, x);
            x1 = Mathf.Max(x1, x);
            y0 = Mathf.Min(y0, y);
            y1 = Mathf.Max(y1, y);
        }
        return new Rect2(x0, y0, x1 - x0, y1 - y0);
    }

    /// <summary>The zoom the whole board fits the window at, with
    /// <see cref="FitMargin"/>, at the view's angle (its box's corners as the
    /// camera draws them); the old default, 2.5, on the flat ground.</summary>
    private float FitZoom()
    {
        if (BoardGround() is not { } board)
            return 2.5f;
        Vector2 view = GetViewport().GetVisibleRect().Size;
        Rect2 seen = BoardOnScreen(board);
        if (seen.Size.X <= 0.0f || seen.Size.Y <= 0.0f)
            return 2.5f;
        return Mathf.Min(view.X / seen.Size.X, view.Y / seen.Size.Y) / FitMargin;
    }

    /// <summary>
    /// The camera on its boom (2026-11-01, as tactics games frame theirs):
    /// <list type="bullet">
    /// <item><b>A point on the ground it looks at</b> - the tank, a little
    /// below the middle so a plume or a thrown turret has room over it; the
    /// view the player moved (<see cref="_viewAt"/>); at the fit, the whole
    /// board's middle.</item>
    /// <item><b>That point held over the board's plan</b>, not the view's edges
    /// inside it: the edges were held in the game angle's own plane, and
    /// turned, the view went off to one side of the board.</item>
    /// <item><b>The camera far back along the line of sight</b>, by the board's
    /// box: its near and far planes take the whole board at any angle - they
    /// were set for the game's angle, and turned, the near edge fell in front
    /// of the near plane and was cut off.</item>
    /// </list>
    /// Orthographic, so how far back does not change the picture.
    /// </summary>
    private void FrameCamera()
    {
        AngleDefault();
        Vector2 window = GetViewport().GetVisibleRect().Size;
        if (_zoomFit)
            _zoom = FitZoom();
        _camera.Size = window.Y / _zoom;
        Vector3 back = ViewBack;
        (Rect2 Plan, float Low, float High)? board = BoardGround();
        Vector3 focus;
        if (_viewAt is Vector2 view)
            // moved by the player: the plane point itself, on the ground
            focus = new Vector3(view.X, 0.0f, view.Y / Squash);
        else if (_zoomFit && board is { } whole)
        {
            // the fit: the middle of the board as drawn
            Rect2 seen = BoardOnScreen(whole);
            Vector2 mid = seen.GetCenter();
            focus = GroundUnder(ViewRight * mid.X + ViewUp * mid.Y);
        }
        else
        {
            // Not up with the wreck's hop, nor down into its crater or the
            // ground after it (Tank3DBench.Pits, Gone): the board stays where it is.
            Vector3 pivot = _rig.Position - Vector3.Up * _wreckLift;
            // Both hulls in the picture while a target stands near: toward the
            // middle between them, eased off with the distance so the rammer
            // stays on the screen - a switch at one distance was a jump of the view.
            if (_other is not null)
            {
                Vector3 mid = 0.5f * (_rig.Position + _other.Rig.Position);
                float d = mid.DistanceTo(_rig.Position) / _camera.Size;
                pivot = pivot.Lerp(mid, 1.0f - Smooth(0.3f, 0.6f, d));
            }
            focus = GroundUnder(pivot);
            // up the screen by a sixth of the view: on the ground, away from
            // the eye by that over the pitch's sine
            var away = new Vector3(-back.X, 0.0f, -back.Z);
            if (away.LengthSquared() > 1e-6f)
                focus += away.Normalized() * (_camera.Size / 6.0f / Mathf.Max(Mathf.Sin(ViewPitch), 0.2f));
        }
        if (board is { } b)
        {
            focus.X = Mathf.Clamp(focus.X, b.Plan.Position.X, b.Plan.End.X);
            focus.Z = Mathf.Clamp(focus.Z, b.Plan.Position.Y, b.Plan.End.Y);
        }
        focus.Y = 0.0f;
        _viewNow = new Vector2(focus.X, focus.Z * Squash);
        // a drag past the edge is not stored past it, or the way back would
        // first have to undo the overshoot
        if (_viewAt is not null)
            _viewAt = _viewNow;
        // back along the line of sight past the whole board, whatever the angle
        float reach = Back, depth = 2.0f * Back;
        if (board is { } box)
        {
            var centre = new Vector3(box.Plan.GetCenter().X, 0.5f * (box.Low + box.High), box.Plan.GetCenter().Y);
            float radius = new Vector3(box.Plan.Size.X, box.High - box.Low, box.Plan.Size.Y).Length() * 0.5f;
            reach = focus.DistanceTo(centre) + radius + DepthSpare;
            depth = 2.0f * reach;
        }
        _camera.Position = focus + back * reach;
        _camera.Rotation = new Vector3(-ViewPitch, _orbitYaw, 0.0f);
        _camera.Far = depth;
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
            Log.Print("tank3d: no tile to lay a board with - the flat ground instead");
            return;
        }
        if (MapToPlay() is not BoardMap map)
            return;
        // --relief: the board in a sea of deep water cells, at least one hex
        // between the land and the frame for a rammed tank to go into
        // (BoardMap.Moated); the cells the flags name are the map's, moved
        _shownMoat = Vector2I.Zero;
        if (_reliefOn)
        {
            map = map.Moated();
            _moat = map.Shift;
            _shownMoat = _moat;
            if (_startCell is Vector2I start)
                _startCell = start + _moat;
            if (_hoverFixed is Vector2I hover)
                _hoverFixed = hover + _moat;
            if (_otherCell is Vector2I other)
                _otherCell = other + _moat;
        }
        TankFromBoard(map);
        _parkings = map.Parked;
        _arena = map.Arena;
        TerrainSet terrain = TerrainSet.Load(AssetRoot.Terrains);
        Log.Print($"tank3d: board {map.Name} {map.Columns}x{map.Rows}, terrain {terrain.Note}");
        _field = new HexField
        {
            Terrain = terrain,
            Paint = map.Paint,
            Trees = false,
            Columns = map.Columns, Rows = map.Rows, Plot = map.Plot,
            Sea = map.Sea,
        };
        _field.SetKinds(map.Kinds);
        _field.SetGround(map.Ground);
        // the walls' cells too on the relief: each is a pad there
        // (Relief.Pad, BOT-12), and BuildWalls stands its masonry on it
        _field.SetCover(map.Over);
        // --relief joins the levels by slopes: no ramps, a level up or down
        // is one step from any side (HexField.Slopes; BOT-11, a departure
        // from the GDD's ramp kept to the flag)
        _field.SetRelief(map.Levels, _reliefOn ? null : map.Ramps);
        _field.Slopes = _reliefOn;
        // After the relief - the tank bench's reason: the water's guards are
        // asked about levels and ramps.
        _field.SetWater(map.Water);
        // the map's own sides before the relief: a wall's foot is shaded
        // where it stands (Relief.Pads); BuildWalls writes them again off
        // the bricks
        foreach ((Vector2I cell, Masonry wall) in map.Walling)
            _field.SetSides(cell, wall.Built);
        _field.SetSoil(Dressed(map));
        // the roads' way through each cell, for the drawing; the rule is the
        // floor the map already laid (Foundation.Road)
        _field.Roads = map.Roads;
        // which trees its woods grow (format 3's wood and trees, Species)
        _field.WoodMix = map.WoodMix;
        _field.TreeCells = map.TreeCells;
        AddChild(_field);
        _field.Atlas = _tile;
        if (_reliefOn)
            BuildRelief(map);
        _home = _startCell ?? (map.Homes.Count > 0 ? map.Homes[0] : new Vector2I(map.Columns / 2, map.Rows / 2));
        // a match: who stands where, and which of them rides the rig
        LineUpSides();
        PointSetup();
        // the tide's rings, off the board before anything floods (Tank3DBench.Tide)
        TideSetup(map);

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
            // --relief: one smooth ground for the prisms (BOT-11)
            Relief = _relief,
            RockIce = _iceBuilt,
            SoilWind = _windBuilt,
            ShowBrow = _browOn,
            ShowLawn = _lawnOn,
            ShowBed = _bedOn,
            ShowEarth = _earthOn,
            ShowCrag = _cragOn,
            ShowSand = _sandOn,
            ShowForest = _forestOn,
            ShowRoadPhoto = _roadPhotoOn,
            ShowTurf = _turfOn,
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

    /// <summary>How far the map's cells moved on the board (the sea of
    /// <c>--relief</c>, <see cref="BoardMap.Moated"/>): a cell a flag or a
    /// <c>--do</c> names is the map's, and this is added to it.</summary>
    private Vector2I _moat;

    /// <summary>A cell a flag or a <c>--do</c> names, on the board.</summary>
    private Vector2I MapCell(int q, int r) => new Vector2I(q, r) + _moat;

    /// <summary><see cref="_moat"/> for what prints a cell, the bench's nested
    /// records among them (Leg): static, one bench at a time.</summary>
    private static Vector2I _shownMoat;

    /// <summary>A cell as the HUD and the log say it: the map's, without the
    /// sea's shift (the user's call, 2026-10-25), so it is the cell a flag
    /// names and the editor shows.</summary>
    private static string MapAt(Vector2I cell) => (cell - _shownMoat).ToString();

    private static string MapAt(Vector2I? cell) => cell is Vector2I c ? MapAt(c) : "-";

    private static string MapAt(IEnumerable<Vector2I> cells, string between)
    {
        var said = new List<string>();
        foreach (Vector2I c in cells)
            said.Add(MapAt(c));
        return string.Join(between, said);
    }

    private static string MapAt(Steps.Route route) => route.ToString(_shownMoat);

    /// <summary>The map's soil looks, every cell's resolved
    /// (<see cref="BoardMap.Looks"/>), with <c>--soil</c> dressed over the
    /// floor its look is for.</summary>
    private char[]? Dressed(BoardMap map)
    {
        char[]? looks = map.Looks();
        if (_soilAll is not char all)
            return looks;
        looks ??= map.Looks(always: true)!;
        for (int at = 0; at < looks.Length; at++)
            if (!map.Water[at] && map.Ground[at] == Soil.FloorOf(all))
                looks[at] = all;
        return looks;
    }

    /// <summary>The board's smooth fields (<c>--relief</c>), and their
    /// pictures, looked at before anything stands on them.</summary>
    private void BuildRelief(BoardMap map)
    {
        string name = map.Name;
        // the map's rocks and climate (format 3, docs/rocks-plan.md С4), the
        // flags dressed over them
        RockStyles? rocks = _rocks ?? RockStyles.Of(map.Rocking, map.Climate);
        if (rocks is null && (_rockWind is not null || _rockIce is not null))
            rocks = new RockStyles();
        if (rocks is not null)
        {
            rocks.Wind = _rockWind ?? map.Climate.Wind;
            rocks.Ice = _rockIce ?? map.Climate.Ice;
        }
        _iceBuilt = rocks?.Ice ?? map.Climate.Ice;
        _windBuilt = rocks?.Wind ?? map.Climate.Wind;
        // the bunkers dug in (Relief.Pads): every capon whose model stands
        var berms = new HashSet<Vector2I>();
        if (_show.Walls && _show.Bunkers)
            foreach (Vector2I cell in map.Walled())
                if (map.MasonryAt(cell)?.Kind == Masonry.Sort.Capon)
                    berms.Add(cell);
        _relief = Relief.Build(_field!, rocks: rocks, berms: berms);
        // the tide builds it again with the same (Tank3DBench.Tide)
        _tideRocks = rocks;
        _tideBerms = berms;
        if (_relief is null)
            return;
        foreach (RockGroup g in _relief.Rocks)
            Log.Print($"tank3d: rock {g.Index} at {MapAt(g.Cells[0])}, {g.Cells.Count} cells, {g.Look}");
        string path = _relief.SavePictures(_field!, AssetRoot.Out + "/relief", name);
        // from here on the tank, its effects and what stands on the board
        // stand on the drawn ground (HexField.Surface); the relief itself was
        // read off the prisms before this
        _field!.Surface = _relief.HeightAt;
        Log.Print($"tank3d: relief {_relief.Nx}x{_relief.Ny} points, step {_relief.Cell:0.0} px, "
                  + $"{_relief.Seconds * 1000:0} ms; {path}");
    }

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
        Log.Print($"tank3d: {terrain.Note}");
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
            if (_show.Read(args, ref i) || ReadPerfFlag(args, ref i) || AiArg(args[i], args, ref i))
                continue;
            string a = args[i];
            bool more = i + 1 < args.Length;
            if (a == "--model" && more) { _modelTag = args[++i].ToUpperInvariant(); _modelNamed = true; }
            else if (a == "--sprites" && more)
                Log.Print($"tank3d: --sprites {args[++i]} ignored - the 3D tank reads no sprite set");
            else if (a == "--map" && more) { _mapName = args[++i]; _mapNamed = true; }
            // the bench as a game, two sides of five (Tank3DBench.Match; Match3D.tscn sets it)
            else if (a == "--match") Match = true;
            // a quick battle against the AI: the lobby and the build first (Tank3DBench.Quick)
            else if (a == "--quick") Quick = Match = true;
            // the same with the squad built in a hangar (Tank3DBench.Hangar); the old build stays
            else if (a == "--hangar") Quick = Match = _qHangar = true;
            else if (a == "--quick-do" && more)
                _quickDo.AddRange(args[++i].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            // the old view, the sprites' angle, rather than the isometric one (Orbit)
            else if (a == "--game-angle") _gameAngle = true;
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
            // the relief is the board by default (the user's call, 2026-11-01):
            // --relief is said for nothing, --ramps brings the prisms back
            else if (a == "--relief") _reliefOn = true;
            else if (a == "--ramps") _reliefOn = false;
            // --rock mesa:ochre,yardang,moutonnee: the rocks' looks, dealt out
            // in reading order (RockStyles); the relief comes with them
            else if (a == "--rock" && more)
            {
                string said = args[++i];
                _rocks = RockStyles.Parse(said, out string bad);
                if (_rocks is null)
                    GD.PrintErr($"tank3d: --rock {said}: no such look '{bad}' "
                                + "(type crag|mesa|yardang|moutonnee, :palette, :preset 1-3)");
                else
                    _reliefOn = true;
            }
            // --soil n: every sand a dune field (Soil, format 3)
            else if (a == "--soil" && more)
            {
                string said = args[++i];
                if (said.Length == 1 && Soil.FloorOf(said[0]) is not null)
                    _soilAll = said[0];
                else
                    GD.PrintErr($"tank3d: --soil {said}: no such look (g e k on solid, r n on sand, p b on mud)");
            }
            else if (a == "--rock-wind" && more) _rockWind = F(args[++i], RockClimate.DefaultWind);
            else if (a == "--ice" && more) _rockIce = F(args[++i], RockClimate.DefaultIce);
            else if (a == "--brow") _browOn = true;
            else if (a == "--lawn") _lawnOn = true;
            else if (a == "--bed") _bedOn = true;
            else if (a == "--earth") _earthOn = true;
            else if (a == "--crag") _cragOn = true;
            else if (a == "--sand") _sandOn = true;
            else if (a == "--forest") _forestOn = true;
            else if (a == "--road-photo") _roadPhotoOn = true;
            else if (a == "--no-turf") _turfOn = false;
            else if (a == "--pbr") _pbr = true;
            else if (a == "--fx2d") _fx2d = true;
            else if (a == "--zoom" && more) { _zoom = F(args[++i], _zoom); _zoomFit = false; }
            else if (a == "--tremble" && more) _trembleLevel = F(args[++i], _trembleLevel);
            else if (a == "--heading" && more) _heading = F(args[++i], _heading);
            else if (a == "--capture" && more) _capturePath = args[++i];
            else if (a == "--no-ui") _noUi = true;
            else if (a == "--capture-at" && more) _captureAt = (int)F(args[++i], _captureAt);
            else if (a == "--edit-at" && more) _editAt = (int)F(args[++i], _editAt);
            // --tide-at FRAME [EVERY]: the sea up a ring at that frame of the
            // battle, and again every EVERY frames (Tank3DBench.Tide) - to look
            // at a rise without playing ten quiet rounds to it
            else if (a == "--tide-at" && more)
            {
                _tideAt = (int)F(args[++i], -1);
                if (i + 1 < args.Length && int.TryParse(args[i + 1], out int every))
                {
                    _tideEvery = every;
                    i++;
                }
            }
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
            // --first green|brown: player 1 said, no toss (Tank3DBench.Match)
            else if (a == "--first" && more)
                _firstSaid = args[++i].ToLowerInvariant() == Parking.Brown ? Side.Brown : Side.Green;
            // --commands "go:3,5;fire:5,3;pass": a match by commands alone, each
            // given when the last is done (Tank3DBench.Commands, CommandsTick)
            else if (a == "--commands" && more)
                foreach (string one in args[++i].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    _commands.Enqueue(one);
            // --state-out PATH: the match as the core reads it off the bench, once
            // the commands are given and it has settled - BattleRun's result to diff
            else if (a == "--state-out" && more)
                _stateOut = args[++i];
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
            case "reset": ResetTank(); WoodReset(); WallReset(); MinesReset(); CancelOrder(); break;
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
            case "pass": PassByHand(); break;
            // the move taken back (Tank3DBench.Undo)
            case "undo": UndoMove(); break;
            case "ram-mode": RamToggle(); break;
            // the pointer on the end-turn button, for a capture (Tank3DBench.Badges)
            case "pass-hover": _passHeld = !_passHeld; break;
            // the turn on the spot's rose (Tank3DBench.Spin)
            case "spin": _spinPinned = !_spinPinned; break;
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
                        Log.Print($"tank3d: --do {what}: {_modelTag} is a casemate, it has no turret");
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
                            if (!RamOrder(MapCell(q, r)))
                                ShotOrder(MapCell(q, r));
                        }
                        else
                            AimCell(MapCell(q, r));
                    }
                }
                else if (what.StartsWith("hover=", StringComparison.Ordinal))
                {
                    // hover=Q,R: --hover moved mid-run, for a capture that aims
                    // at one unit and then at another
                    string[] qr = what[6..].Split(',');
                    if (qr.Length == 2 && int.TryParse(qr[0], out int q) && int.TryParse(qr[1], out int r))
                        _hoverFixed = MapCell(q, r);
                }
                // view-step=N: the view N steps of the turn round, as Q/E (TurnViewStep)
                else if (what.StartsWith("view-step=", StringComparison.Ordinal))
                    TurnViewStep(int.Parse(what[10..], CultureInfo.InvariantCulture));
                // orbit=DX,DY: the middle button dragged that many pixels (OrbitBy)
                else if (what.StartsWith("orbit=", StringComparison.Ordinal))
                {
                    string[] xy = what[6..].Split(',');
                    if (xy.Length == 2)
                        OrbitBy(new Vector2(F(xy[0], 0), F(xy[1], 0)));
                }
                else if (what.StartsWith("pan=", StringComparison.Ordinal))
                {
                    // pan=DX,DY: the board moved that many pixels under the view, as the keys move it.
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
                else if (what.StartsWith("tap=", StringComparison.Ordinal))
                {
                    // tap=X,Y: the left button on that screen pixel - a rose's
                    // disc, where no hex's middle is
                    string[] xy = what[4..].Split(',');
                    if (xy.Length == 2 && float.TryParse(xy[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
                        && float.TryParse(xy[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y))
                        if (!ActClick(new Vector2(x, y)))
                            OrderClick(new Vector2(x, y), false);
                }
                else if (what.StartsWith("warp=", StringComparison.Ordinal))
                {
                    // warp=X,Y: the pointer moved to that screen pixel, no
                    // button - what the hover shows (the placement's ghost)
                    string[] xy = what[5..].Split(',');
                    if (xy.Length == 2 && float.TryParse(xy[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
                        && float.TryParse(xy[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y))
                        GetViewport().WarpMouse(new Vector2(x, y));
                }
                else if (what.StartsWith("press=", StringComparison.Ordinal) || what.StartsWith("release=", StringComparison.Ordinal))
                {
                    // press=X,Y / release=X,Y: the left button only down or
                    // only up there - a carry held over frames, for a capture of its middle
                    bool down = what[0] == 'p';
                    string[] xy = what[(down ? 6 : 8)..].Split(',');
                    if (xy.Length == 2)
                    {
                        var at = new Vector2(F(xy[0], 0), F(xy[1], 0));
                        GetViewport().WarpMouse(at);
                        Input.ParseInputEvent(new InputEventMouseButton
                        {
                            ButtonIndex = MouseButton.Left, Pressed = down, Position = at, GlobalPosition = at,
                            ButtonMask = down ? MouseButtonMask.Left : 0,
                        });
                    }
                }
                else if (what.StartsWith("drag=", StringComparison.Ordinal))
                {
                    // drag=X1,Y1,X2,Y2: the left button down at the first
                    // pixel and up at the second, through Input (the placement's carry)
                    string[] xy = what[5..].Split(',');
                    if (xy.Length == 4)
                    {
                        var a = new Vector2(F(xy[0], 0), F(xy[1], 0));
                        var b = new Vector2(F(xy[2], 0), F(xy[3], 0));
                        GetViewport().WarpMouse(a);
                        Input.ParseInputEvent(new InputEventMouseButton
                        {
                            ButtonIndex = MouseButton.Left, Pressed = true, Position = a, GlobalPosition = a,
                            ButtonMask = MouseButtonMask.Left,
                        });
                        GetViewport().WarpMouse(b);
                        Input.ParseInputEvent(new InputEventMouseButton
                        {
                            ButtonIndex = MouseButton.Left, Pressed = false, Position = b, GlobalPosition = b,
                        });
                    }
                }
                else if (what.StartsWith("mouse=", StringComparison.Ordinal))
                {
                    // mouse=X,Y: a real press and release of the left button at
                    // that screen pixel, through Input - the GUI first, as the
                    // hand's own does; tap= goes round it
                    string[] xy = what[6..].Split(',');
                    if (xy.Length == 2 && float.TryParse(xy[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
                        && float.TryParse(xy[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y))
                    {
                        var at = new Vector2(x, y);
                        GetViewport().WarpMouse(at);
                        foreach (bool down in new[] { true, false })
                            Input.ParseInputEvent(new InputEventMouseButton
                            {
                                ButtonIndex = MouseButton.Left, Pressed = down, Position = at, GlobalPosition = at,
                                ButtonMask = down ? MouseButtonMask.Left : 0,
                            });
                    }
                }
                else if (what.StartsWith("click=", StringComparison.Ordinal))
                {
                    // click=Q,R: the left button where the camera shows that hex's
                    // middle - the mouse's own path, PickAt and all.
                    string[] qr = what[6..].Split(',');
                    if (qr.Length == 2 && int.TryParse(qr[0], out int q) && int.TryParse(qr[1], out int r))
                    {
                        Vector2 at = _camera.UnprojectPosition(CellWorld(MapCell(q, r)));
                        if (!ActClick(at))
                            OrderClick(at, false);
                    }
                }
                // roster-hover=N: the pointer held on the roster's N-th icon, from 1;
                // roster-click=N: a click on it (Tank3DBench.Roster)
                else if (what.StartsWith("roster-hover=", StringComparison.Ordinal))
                    _rosterHeld = int.TryParse(what[13..], out int n) ? n - 1 : -1;
                else if (what.StartsWith("roster-click=", StringComparison.Ordinal))
                {
                    if (int.TryParse(what[13..], out int n) && n >= 1 && n <= _rosterCount)
                        RosterTake(n - 1);
                }
                else if (what.StartsWith("take=", StringComparison.Ordinal))
                {
                    // take=Q,R: the click on the tank standing on that hex,
                    // through the same screen test the pointer gets.
                    string[] qr = what[5..].Split(',');
                    if (qr.Length == 2 && int.TryParse(qr[0], out int q) && int.TryParse(qr[1], out int r)
                        && _parked.Find(p => p.Cell == MapCell(q, r)) is { } standing)
                        OrderClick(_camera.UnprojectPosition(standing.Rig.Position
                            + Vector3.Up * (standing.Model.Size.Y * standing.Model.PixelsPerUnit * 0.4f)), false);
                }
                // ignite=Q,R: the unit on that hex set alight (Tank3DBench.Blast)
                else if (what.StartsWith("ignite=", StringComparison.Ordinal))
                {
                    string[] qr = what[7..].Split(',');
                    if (qr.Length == 2 && int.TryParse(qr[0], out int q) && int.TryParse(qr[1], out int r))
                        IgniteAt(MapCell(q, r));
                }
                // lock=Q,R: the first click on a cell in reach; face=S: the rose's disc
                else if (what.StartsWith("lock=", StringComparison.Ordinal))
                {
                    string[] qr = what[5..].Split(',');
                    if (qr.Length == 2 && int.TryParse(qr[0], out int q) && int.TryParse(qr[1], out int r))
                        Lock(MapCell(q, r));
                }
                else if (what.StartsWith("face=", StringComparison.Ordinal))
                    FaceTo((int)F(what[5..], 90));
                else if (what.StartsWith("rose-hover=", StringComparison.Ordinal))
                    _roseHeld = (int)F(what[11..], 90);
                else if (what.StartsWith("spin-hover=", StringComparison.Ordinal))
                    _spinHeld = (int)F(what[11..], 90);
                else if (what.StartsWith("spin-to=", StringComparison.Ordinal))
                    SpinTo((int)F(what[8..], 90));
                // act=move|attack|ram|ambush|smoke|fiery|mine|repair|winch|off: the action panel's modes (Tank3DBench.Acts)
                else if (what == "act=move")
                    MoveOn();
                // tip=NAME|off: the action panel's card held up for that act's button (Tank3DBench.ActTips)
                else if (what.StartsWith("tip=", StringComparison.Ordinal))
                    _tipHeld = what == "tip=off" ? null : what[4..];
                // deploy-reset: «Вернуть ряд» in placement (Tank3DBench.Deploy)
                else if (what == "deploy-reset")
                    DeployReset();
                else if (what.StartsWith("act=", StringComparison.Ordinal))
                {
                    Gear gear = what[4..] switch { "mine" => Gear.Mine, "repair" => Gear.Repair, "winch" => Gear.Winch, _ => Gear.None };
                    if (gear != _gearMode)
                        GearToggle(gear == Gear.None ? _gearMode : gear);
                    if ((what[4..] == "fiery") != _fieryMode)
                        FieryToggle();
                    if ((what[4..] == "smokeshell") != _smokeShotMode)
                        SmokeShotToggle();
                    if ((what[4..] == "onway") != _onWayMode)
                        OnWayToggle();
                    if ((what[4..] == "ram") != _ramMode)
                        RamToggle();
                    if ((what[4..] == "ambush") != _ambushMode)
                        AmbushToggle();
                    if ((what[4..] == "smoke") != _smokeMode)
                        SmokeToggle();
                    if ((what[4..] == "attack") != _attackMode)
                        AttackToggle();
                }
                // cmd=KIND[:Q,R][:SIDE]: a command, as the AI will give it (Tank3DBench.Commands)
                else if (what.StartsWith("cmd=", StringComparison.Ordinal))
                {
                    if (ParseCommand(what[4..]) is { } command)
                        Execute(command);
                    else
                        Log.Print($"tank3d: --do {what}: not a command");
                }
                else if (what.StartsWith("go=", StringComparison.Ordinal))
                {
                    // go=Q,R: the left button on that hex, under the rules.
                    string[] qr = what[3..].Split(',');
                    if (qr.Length == 2 && int.TryParse(qr[0], out int q) && int.TryParse(qr[1], out int r))
                    {
                        SetRules(true, " (--do go)");
                        OrderTo(MapCell(q, r));
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
                    Log.Print($"tank3d: --do {what}: no such event");
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
        _model.LooseDebris(true);
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
        _model.LooseDebris(false);
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
        // a bench still building under the editor's veil (Build)
        if (!_built)
            return;
        // the frame before closed (Prof), before anything of this one is measured
        PerfTick(delta);
        // The way back, before the capture - the harness's order: the picture
        // that lands on disk is the editor it returned to.
        if (_editAt >= 0 && _frame == _editAt && _editing)
        {
            Edit();
            return;
        }
        float dt = (float)Math.Min(delta, 0.1);
        Prof.Scope bench = Prof.Measure("bench");
        using (Prof.Measure("script"))
            foreach (var (frame, what) in _script)
                if (frame == _frame && !_restarted)
                    Do(what);
        Prof.Scope drive = Prof.Measure("drive");

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
            // past the limits only as far as a laid shot took it, shrinking
            // back as the tube comes home (TankModel.ElevStretch)
            if (_aim is null)
                _model.ElevStretch = _model.PastLimits(_model.Elevation);
            _model.Elevation = Mathf.Clamp(_model.Elevation + elevIn * Mathf.DegToRad(ElevDeg) * dt,
                                           _model.ElevLow, _model.ElevHigh);
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
        drive.Dispose();
        using (Prof.Measure("water"))
            WaterTick(dt);
        using (Prof.Measure("wall"))
            WallTick(dt);
        using (Prof.Measure("model"))
            _model.Apply();
        using (Prof.Measure("fx"))
        {
            FxProcess(dt, _speed, a);
            MarksTick();
        }
        using (Prof.Measure("course"))
            CourseTick();
        RosterTick(dt);
        using (Prof.Measure("aim"))
            AimTick();
        CardTick();
        PeekTick();
        ActsTick();
        UndoTick();
        CursorTick();
        using (Prof.Measure("ram"))
            RamFrame(dt, _camera.GlobalBasis);
        using (Prof.Measure("shells"))
            ShellsTick(dt);
        using (Prof.Measure("wood"))
            WoodTick(dt);
        using (Prof.Measure("match"))
            MatchTick(dt);
        using (Prof.Measure("camera"))
        {
            PanKeys(dt);
            GlideTick(dt);
            FrameCamera();
        }
        Prof.Scope hud = Prof.Measure("hud");
        // Rebuilt only when what it says changed: built every frame it was
        // 6 KB a frame of strings for a text that stood still (BOT-3). Off the
        // screen it is not built at all.
        _hud.Visible = _hints && !_noUi;
        if (_hud.Visible)
        {
            int stamp = HudStamp();
            if (stamp != _hudStamp)
            {
                _hudStamp = stamp;
                HudText();
            }
        }
        hud.Dispose();
        bench.Dispose();
        Shots();
        _frame++;
    }

    private int? _hudStamp;

    /// <summary>Everything <see cref="HudText"/> reads, hashed - the strings by
    /// their content, which allocates nothing.</summary>
    private int HudStamp()
    {
        var h = new HashCode();
        h.Add(_modelTag);
        h.Add(_profile.Tag);
        h.Add(_field is null);
        h.Add(_mapName);
        if (_field is not null)
            h.Add(_field.FlatCellAt(Board(_rig.Position)));
        h.Add(Drowning);
        h.Add(_fate);
        h.Add(Swimming);
        h.Add(_note);
        h.Add(_other?.Tag);
        h.Add(_other?.Cell);
        h.Add(_ramNote);
        h.Add(_wallNote);
        h.Add(_rules);
        h.Add(_orderNote);
        h.Add(_courseNote);
        h.Add(_aimNote);
        h.Add(_judgedNote);
        h.Add(Match);
        h.Add(_model.Turreted);
        if (Match)
            MatchHudStamp(ref h);
        return h.ToHashCode();
    }

    private void HudText()
    {
        string where = _field is null ? "flat ground"
            : $"{_mapName} {MapAt(_field.FlatCellAt(Board(_rig.Position)))}";
        string fate = Drowning ? "Drowned" : _fate.ToString();
        if (_fate == Fate.Alive && Swimming)
            fate = "Afloat";
        string ram = (_other is null ? "" : $"  target {_other.Tag} {MapAt(_other.Cell)}"
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
                    + "R/F gun   -/= wheel zoom to cursor   MMB drag pan   Home follow tank   Tab panel   F3 perf   F12 shot";
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
                Log.Print(err == Error.Ok ? $"capture: {_capturePath}" : $"capture to {_capturePath} failed: {err}");
            }
        }
        // Asked on every frame, not only on a saved one: a sequence whose end
        // is not on its step saved its last frame short of the end and ran on.
        bool done = (_capturePath is null || _frame >= _captureAt)
                    && (_sequenceDir is null || _frame >= _sequenceTo);
        // a --perf run ends itself (PerfTick), its pictures taken on the way
        if (done && _perfDir is null && (_capturePath is not null || _sequenceDir is not null))
            GetTree().Quit();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (!_built)
            return;
        // the placement before the first turn takes the buttons (Tank3DBench.Deploy)
        if (Match && DeployInput(@event))
            return;
        // the AI's turn: the camera is the player's, the board and the orders not (Tank3DBench.Ai)
        if (AiTurn && @event is InputEventMouseButton { ButtonIndex: MouseButton.Left or MouseButton.Right, Pressed: true })
            return;
        if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: true } click)
        {
            AimAt(click.Position);
            return;
        }
        // The left button - an order, under the rules (Tank3DBench.Orders); a
        // double click on the target is the ram.
        if (_rules && @event is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } left)
        {
            // with the ram on a tank under it is the right button's
            // (Tank3DBench.Acts)
            if (!ActClick(left.Position))
                OrderClick(left.Position, left.DoubleClick);
            return;
        }
        if (_rules && !AiTurn && @event is InputEventKey { Pressed: true, Echo: false } order)
        {
            // in a match W/A/S/D are the camera's (PanKeys), not orders
            switch (Match && order.Keycode is Key.W or Key.A or Key.S or Key.D ? Key.None : order.Keycode)
            {
                case Key.W: if (_chosen) OrderStep(back: false); return;
                case Key.S: if (_chosen) OrderStep(back: true); return;
                // with a cell fixed, A/D turn its ghost (Tank3DBench.Ghost)
                // with none, the rose round the tank opens and they walk its
                // pick (Tank3DBench.Spin); where the rose may not be had, the
                // old sixty degrees an order
                case Key.A: if (_lockCell is not null) FaceStep(left: true); else if (_chosen && (_spinOpen || SpinMay)) SpinStep(left: true); else if (_chosen) OrderTurn(left: true); return;
                case Key.D: if (_lockCell is not null) FaceStep(left: false); else if (_chosen && (_spinOpen || SpinMay)) SpinStep(left: false); else if (_chosen) OrderTurn(left: false); return;
                // Enter: an order waiting to be confirmed goes - the ambush's
                // axis, the rose's pick, the fixed cell; with none, in a match,
                // the turn handed over (the author's note, 2026-11-01; Ctrl+Enter
                // hands it over always, as before)
                case Key.Enter or Key.KpEnter:
                    if (Match && order.CtrlPressed) PassByHand();
                    else if (AmbushConfirm()) { }
                    else if (_spinOpen && _spinPick is int pick) SpinTo(pick);
                    else if (_lockCell is not null) Confirm();
                    else if (Match) PassByHand();
                    return;
                // the fixed cell let go; an order in hand dropped; with none,
                // the tank let go
                case Key.Escape:
                    // the ram (Tank3DBench.RamPlan) off first
                    if (_spinPick is not null || _spinPinned) SpinLetGo();
                    else if (_attackMode) AttackToggle();
                    else if (_ambushMode) AmbushToggle();
                    else if (_smokeMode) SmokeToggle();
                    else if (_fieryMode) FieryToggle();
                    else if (_smokeShotMode) SmokeShotToggle();
                    else if (_onWayMode) OnWayToggle();
                    else if (_gearMode != Gear.None) GearToggle(_gearMode);
                    else if (_ramMode && _ramWaiting is null) RamToggle();
                    else if (_lockCell is not null && !_ramLock) Unlock();
                    else if (RouteMoving || _turnGoal is not null || _shotWaiting is not null || _ramWaiting is not null) CancelOrder();
                    else Choose(false);
                    return;
            }
        }
        // The middle button held: the view turned and tilted (OrbitBy); let
        // go, it comes round to the nearest step of the turn (OrbitLetGo).
        if (@event is InputEventMouseMotion drag && (drag.ButtonMask & MouseButtonMask.Middle) != 0)
        {
            OrbitBy(drag.Relative);
            return;
        }
        if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Middle, Pressed: false })
        {
            OrbitLetGo();
            return;
        }
        // Q/E in a match: the view a step round (YawStepDeg) - outside one
        // they turn the turret
        if (Match && @event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Q or Key.E } turnKey
            && !turnKey.CtrlPressed && !turnKey.AltPressed)
        {
            TurnViewStep(turnKey.Keycode == Key.Q ? 1 : -1);
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
        // Backspace (and Ctrl+Z) in a match: the move taken back
        // (Tank3DBench.Undo; the author's note, 2026-11-01)
        if (Match && (key.Keycode == Key.Z ? key.CtrlPressed : key.Keycode == Key.Backspace && !key.CtrlPressed))
        {
            Execute(new Command(CommandKind.Undo));
            return;
        }
        string kind = key.CtrlPressed ? "he-" : key.ShiftPressed ? "pierce-" : "ricochet-";
        // A match plays by the rules: the bench's keys that fire, strike, burn,
        // knock out or reset the tank on the rig past them are off - Ctrl+Backspace
        // restarts the whole match instead (Tank3DBench.Match).
        if (Match && key.Keycode is Key.Space or (>= Key.Key1 and <= Key.Key9)
                or Key.J or Key.K or Key.X or Key.T or Key.Backspace
                or (>= Key.F1 and <= Key.F8))
        {
            if (key.Keycode == Key.Backspace)
                Restart();
            // J, the bench's fire, puts the unit's fire out (Tank3DBench.Blast)
            else if (key.Keycode == Key.J)
                Execute(new Command(CommandKind.Douse));
            // 1 to 9, the bench's hits, press the action panel's buttons and F1 to F8 take the roster's units
            // (Tank3DBench.Acts); the hints, the overlay and Edit move to F9-F11
            else
                ActKey(key.Keycode);
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
            // the status and controls text on and off (HudText; in a match
            // with both sides' line, Tank3DBench.Match)
            case Key.F1: _hints = !_hints; break;
            case Key.F9 when Match: _hints = !_hints; break;
            case Key.F10 when Match: TogglePerf(); break;
            case Key.F11 when Match: Edit(); break;
            // the relief's hex grid on and off (Stage3D.Relief); no free single key left
            case Key.G when key.ShiftPressed && _stage?.Relief is not null:
                _stage.ShowEdges = !_stage.ShowEdges;
                break;
            case Key.Minus: ZoomBy(1.0f / 1.25f); break;
            case Key.Equal: ZoomBy(1.25f); break;
            case Key.F5: Edit(); break;
            // the frame's cost (PerfOverlay)
            case Key.F3: TogglePerf(); break;
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
        _panel.Heading("tank3d.drive", L.T("bench.drive"));
        _panel.Toggle("tank3d.drive.cells", L.T("bench.drive_cells"),
                      () => _rules, v => SetRules(v));
        // the line of fire, beads from the muzzle to the hull (Tank3DBench.Aim)
        _panel.Toggle("tank3d.drive.axis", L.T("bench.fire_axis"), () => _fireAxis, v => _fireAxis = v);
        _panel.Toggle("tank3d.drive.reticle", L.T("bench.reticle"), () => _reticle, v => _reticle = v);
        _panel.Readout("tank3d.drive.note", () => _rules
            ? L.T("bench.drive_note_cells")
              + (_orderNote.Length > 0 ? "\n" + _orderNote : "")
            : L.T("bench.drive_note_free"));
        _panel.Expand("tank3d.drive", true);
        _panel.Heading("tank3d.tank", L.T("bench.tank"));
        var labels = new List<string>();
        foreach (string model in _models)
            labels.Add(PairAt(model) is int i and >= 0 ? $"{model}  {Pairs[i].Name}" : model);
        if (labels.Count > 0)
            _panel.Choice("tank3d.tank.model", L.T("bench.model"), labels,
                          () => _models.FindIndex(m => string.Equals(m, _modelTag, StringComparison.OrdinalIgnoreCase)),
                          i => Pick(_models[i]));
        _panel.Readout("tank3d.tank.note", () =>
            L.T("bench.tank_note", _profile.Tag, _profile.Size, _model.PixelsPerUnit)
            + (_model.Turreted ? "" : L.T("bench.no_turret")));
        _panel.Expand("tank3d.tank", true);
        // The relief's lines (Stage3D.Relief): the brow off by default
        if (_stage?.Relief is not null)
        {
            _panel.Heading("tank3d.relief", L.T("bench.relief"));
            _panel.Toggle("tank3d.relief.brow", L.T("bench.brow"),
                          () => _stage.ShowBrow, v => _stage.ShowBrow = v);
            _panel.Toggle("tank3d.relief.grid", L.T("bench.grid"),
                          () => _stage.ShowEdges, v => _stage.ShowEdges = v);
            _panel.Toggle("tank3d.relief.turf", L.T("bench.turf"),
                          () => _stage.ShowTurf, v => _stage.ShowTurf = v);
            if (Stage3D.Lawn() is not null)
                _panel.Toggle("tank3d.relief.lawn", L.T("bench.lawn"),
                              () => _stage.ShowLawn, v => _stage.ShowLawn = v);
            if (Stage3D.Pebbles() is not null)
                _panel.Toggle("tank3d.relief.bed", L.T("bench.bed"),
                              () => _stage.ShowBed, v => _stage.ShowBed = v);
            if (Stage3D.EarthPhoto() is not null)
                _panel.Toggle("tank3d.relief.earth", L.T("bench.earth"),
                              () => _stage.ShowEarth, v => _stage.ShowEarth = v);
            if (Stage3D.CragsOnDisk)
                _panel.Toggle("tank3d.relief.crag", L.T("bench.crag"),
                              () => _stage.ShowCrag, v => _stage.ShowCrag = v);
            if (Stage3D.SandPhoto() is not null)
                _panel.Toggle("tank3d.relief.sand", L.T("bench.sand"),
                              () => _stage.ShowSand, v => _stage.ShowSand = v);
            if (Stage3D.ForestPhoto() is not null)
                _panel.Toggle("tank3d.relief.forest", L.T("bench.forest"),
                              () => _stage.ShowForest, v => _stage.ShowForest = v);
            if (Stage3D.RoadPhotosOnDisk)
                _panel.Toggle("tank3d.relief.roads", L.T("bench.roads"),
                              () => _stage.ShowRoadPhoto, v => _stage.ShowRoadPhoto = v);
            _panel.Expand("tank3d.relief", true);
        }
        // Whether the pond is swum or drowned in - see TankTick.Amphibious.
        _panel.Heading("tank3d.water", L.T("bench.water"));
        _panel.Toggle("tank3d.water.amphibious", L.T("bench.amphibious"),
                      () => _amphibious, v => _amphibious = v);
        _panel.Toggle("tank3d.water.ripples", L.T("bench.ripples"), () => _ripples.Enabled, v =>
        {
            _ripples.Enabled = v;
            if (!v)
                _ripples.Settle();
        });
        _panel.Press("tank3d.water.pond", L.T("bench.pond"), ToPond);
        _panel.Readout("tank3d.water.note", () =>
            L.T("bench.water_note", _deckPx, _deckPx * _profile.Draught)
            + (Drowning ? L.T("bench.sinking", Sink) : Swimming ? L.T("bench.afloat") : ""));
        _panel.Expand("tank3d.water", true);
        // The ram (Tank3DBench.Ram): which hull is rammed, where it stands, go.
        _panel.Heading("tank3d.ram", L.T("bench.ram"));
        if (labels.Count > 0)
            _panel.Choice("tank3d.ram.target", L.T("bench.ram_target"), labels,
                          () => _models.FindIndex(m => string.Equals(m, _otherTag, StringComparison.OrdinalIgnoreCase)),
                          i => PickOther(_models[i]));
        _panel.Press("tank3d.ram.place", L.T("bench.ram_place"), () => LineUp());
        _panel.Press("tank3d.ram.go", L.T("bench.ram_go"), RamGo);
        _panel.Press("tank3d.ram.remove", L.T("bench.ram_remove"), RemoveOther);
        _panel.Readout("tank3d.ram.note", () => _other is null
            ? L.T("bench.no_target")
            : L.T("bench.ram_note", _other.Tag, _other.Profile.Mass, _profile.Mass)
              + (_ramNote.Length > 0 ? $"\n{_ramNote}" : ""));
        _panel.Expand("tank3d.ram", true);
        // The brick walls (Tank3DBench.Walls): the ram through one.
        if (_walls.Count > 0)
        {
            _panel.Heading("tank3d.walls", L.T("bench.walls"));
            _panel.Press("tank3d.walls.ram", L.T("bench.wall_ram"), () => WallRamGo(true));
            _panel.Readout("tank3d.walls.note", () =>
                (_profile.Bulldozes ? L.T("bench.ht_bulldozes") : L.T("bench.wall_holds"))
                + (_wallNote.Length > 0 ? "\n" + _wallNote : ""));
            _panel.Expand("tank3d.walls", true);
        }
        // The round in the air (Tank3DBench.Shell): the sprites' two switches.
        _panel.Heading("tank3d.shell", L.T("bench.shell"));
        _panel.Toggle("tank3d.shell.tracer", L.T("bench.tracer"), () => _tracerOn, v => _tracerOn = v);
        _panel.Toggle("tank3d.shell.smoke", L.T("bench.tracer_smoke"), () => _tracerSmoke, v => _tracerSmoke = v);
        _panel.Toggle("tank3d.shell.soft", L.T("bench.tracer_soft"), () => _tracerSoft, v => _tracerSoft = v);
        _panel.Expand("tank3d.shell", true);
        // The wreck (Tank3DBench.Fx): what it leaves on the ground.
        _panel.Heading("tank3d.wreck", L.T("bench.wreck"));
        _panel.Toggle("tank3d.wreck.scorch", L.T("bench.scorch"), () => _scorchOn, v =>
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
