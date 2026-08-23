using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// One tank on a strip of board, with every dial that decides how it behaves.
///
/// <b>Separate from the harness for <see cref="WoodBench"/>'s reason, which is
/// <c>flash_bench.sheet()</c>'s:</b> what is judged here is what one tank does,
/// and judging it inside <see cref="Main"/> means three machines on fifty-four
/// cells with a wood, a pond, a firefight and a panel of fifty rows, of which
/// the tank is a handful. Here the whole subject is one hull, and the panel is
/// the dials that move it.
///
/// <b>Every moving part of it is the harness's own.</b> <see cref="TankTick"/>
/// drives the tank and winds its clocks, <see cref="Stage3D"/> draws the ground
/// and the billboard, <see cref="ControlPanel"/> is the panel itself, and
/// <see cref="BoardMap"/> holds the board. A bench that reimplemented any of
/// those would be a bench measuring itself - the argument the panel is a
/// <i>view</i> of the harness's state for, and not a second copy of it. What is
/// written here is which tank, where it stands, and which rows the panel shows.
///
/// <b>The tick was lifted out of <see cref="Main"/> to make this possible, and
/// that was the expensive half of the work.</b> The alternative was to write the
/// sequence again - a drive, a placement and eleven clocks in one exact order -
/// and two copies of it would have agreed until the first edit landed in one of
/// them. See <see cref="TankTick"/>: the harness and this file call the same
/// object, so a tank behaves the same on both boards by construction rather than
/// by care.
///
/// <b>Five columns by three, with a rise and a pool</b> - see
/// <see cref="BoardMap.Test"/>. Not one hex: half of what a tank does is keyed
/// on ground gone past rather than on the clock, so a board with nowhere to
/// drive would have shown the turret, the gun and the damage and nothing that
/// moves. Not a full map either - what is here is the level, one grade and one
/// ford, which are the three things the ground can be.
///
/// <b>One tank on the board and three in the garage.</b> The class is a dial,
/// because that is the setting being tested; but each class keeps its own
/// vehicle, its own atlas and its own clocks for good, so switching is a swap
/// rather than a rebuild - the harness's own rule, and the reason a tank you
/// left burning is still burning when you come back to it. The one that is not
/// shown is not ticked and is not drawn.
///
/// <b>Two boards, and the second is the same bench with one string
/// changed.</b> <c>TankTest.tscn</c> stands the tank on
/// <see cref="BoardMap.Test"/> - the level, one grade and one ford, which is
/// where every dial here was tuned. <c>WaterTankTest.tscn</c> stands it on
/// <see cref="BoardMap.WaterMap"/>, open water with a rosette of land in the
/// middle: there the water is the board, so every one of the six ways off the
/// middle is a wade and the descent can be judged on whichever bearing reads
/// best. Nothing in this file knows which of the two it is on - see
/// <see cref="Map"/>.
///
/// Run it with the scene as the positional argument, leaving project.godot
/// alone:
///
///     Godot_v4.7.2-stable_mono_win64.exe --path GodotTest res://TankTest.tscn
///     Godot_v4.7.2-stable_mono_win64.exe --path GodotTest res://WaterTankTest.tscn
/// </summary>
public sealed partial class TankBench : Node2D
{
    /// <summary>The board. Named rather than built here, for the reason
    /// <c>Abbey</c> is a map and not a bench: a board is data, and a second
    /// place that authors one is a second place to disagree with the guards
    /// and with the self-test that judges every map whether or not a scene
    /// opened it.
    ///
    /// <b>An export rather than a constant, which is what makes a bench on
    /// another board cost four lines</b> - <see cref="Main.Map"/>'s arrangement
    /// and its reason: a Godot scene can set an exported property, so
    /// <c>WaterTankTest.tscn</c> is this same node with this string changed, and
    /// every dial, clock and readout comes with it. <c>--map</c> still
    /// overrides, because a flag is the more specific statement - a capture
    /// taken with one has to mean what it says whichever scene it was launched
    /// from.</summary>
    [Export] public string Map = "test";

    private BoardMap _map = null!;
    private HexField _field = null!;
    private TerrainSet? _terrain;
    private TrackMarks? _marks;
    private Swell? _sea;
    private Wake? _wake;
    private Ripples? _wash;

    /// <summary>Whether the pond is simulated at all, and how hard a hull shoves
    /// it. See --no-ripples and --ripple.
    ///
    /// <b>The first water flags this bench parses, and they are here because it is
    /// the board the field is looked at on.</b> Every other water switch belongs to
    /// the harness and is silently ignored here - which the docs name as a defect
    /// in waiting, because a measurement set against such a flag comes back at zero
    /// difference and reads as a layer that does nothing. Two of them earn their
    /// way now: the water board is twelve by twelve of pond, so it is where an A/B
    /// of the surface is actually worth taking.</summary>
    private bool _ripples = true;
    private float _rippleLevel = 1.0f;
    private WaterArt? _surf;
    private Stage3D? _stage;
    private Camera2D _camera = null!;
    private ControlPanel? _panel;
    private Label? _hud;

    /// <summary>What a frame does to the tank. One object, shared with the
    /// harness - see <see cref="TankTick"/>.</summary>
    private readonly TankTick _tick = new();

    /// <summary>The view's spring, for the gun. One for the board rather than
    /// one per tank, which on a bench with one tank is the same thing said
    /// twice - but it is the harness's arrangement and there is no reason to
    /// hold a different one here.</summary>
    private readonly CameraShake _shake = new();

    /// <summary>Every class that loaded, one vehicle apiece. Only
    /// <see cref="Tank"/> is drawn and only <see cref="Tank"/> is ticked; the
    /// rest keep whatever state they were left in.</summary>
    private readonly List<Vehicle> _garage = new();
    private readonly Dictionary<string, AtlasSet> _atlases = new();
    private int _pick;

    private Vehicle Tank => _garage[_pick];
    private TankSprite Sprite => Tank.Sprite;

    // --- the dials -----------------------------------------------------------

    private bool _pitch = true;
    private bool _rumble;
    private bool _tremble = true;
    private double _trembleLevel = 1.0;
    private bool _tracks = true;
    private bool _exhaust = true;
    private double _exhaustLevel = 1.0;
    private bool _exhaustRamp;
    private bool _scan;
    private bool _tube = true;
    private bool _shear = Recoil.ShearOnByDefault;
    private double _recoilLevel = 1.0;
    private bool _shakeOn = CameraShake.OnByDefault;
    private bool _shadow = true;
    private bool _ruts = true;
    private bool _spinning;
    private double _sizeLevel = 1.0;
    private double _traverse = 1.0;
    private int _hitSide = HexField.EdgeHeadings.Length - 1;
    private int _calibre = 1;
    private bool _edges = true;
    private bool _board = true;

    private double HitFrom => HexField.EdgeHeadings[_hitSide];

    /// <summary>The board's own corner, the same one the harness parks its grid
    /// at. Any value would do on a bench of fifteen cells; this one keeps the
    /// two boards' numbers comparable when a figure is read off both.</summary>
    private readonly Vector2 _origin = new(220, 200);

    // --- flags ---------------------------------------------------------------

    private string? _capturePath;
    private int _captureAt = 40;
    private int _frame;

    /// <summary>What the view opens at, or null for "measure it" - see
    /// <see cref="FitZoom"/>. Null rather than a number, because the board is
    /// fifteen cells and the panel eats three hundred pixels of the window: a
    /// tuned figure would be right for this board at this window size and wrong
    /// for the next cell added to either.</summary>
    private float? _zoomAt;
    private string? _startTag;
    private Vector2I? _driveTo;
    private bool _fireAtStart;
    private double? _hitAtStart;
    private int _damageAtStart;
    private bool _destroyAtStart;
    private bool _burnAtStart;
    private bool _noUi;
    private bool _showUi;

    private void ReadFlags()
    {
        string[] args = OS.GetCmdlineUserArgs();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--capture" when i + 1 < args.Length:
                    _capturePath = args[++i];
                    break;
                case "--capture-at" when i + 1 < args.Length
                                         && int.TryParse(args[i + 1], out int at):
                    _captureAt = at;
                    i++;
                    break;
                case "--tank" when i + 1 < args.Length:
                    _startTag = args[++i].ToUpperInvariant();
                    break;
                // Which board. Overrides the scene's export for the reason every
                // other flag here overrides a default: a capture taken with a
                // flag has to mean what it says whatever it was launched from.
                case "--map" when i + 1 < args.Length:
                    Map = args[++i];
                    break;
                case "--drive" when i + 1 < args.Length:
                    _driveTo = Spot(args[++i]);
                    break;
                case "--fire":
                    _fireAtStart = true;
                    break;
                case "--hit" when i + 1 < args.Length && Number(args[i + 1]) is double deg:
                    _hitAtStart = deg;
                    i++;
                    break;
                case "--damage" when i + 1 < args.Length
                                     && int.TryParse(args[i + 1], out int deep):
                    _damageAtStart = Math.Clamp(deep, 0, 3);
                    i++;
                    break;
                case "--destroy":
                    _destroyAtStart = true;
                    break;
                case "--burning":
                    _burnAtStart = true;
                    break;
                case "--zoom" when i + 1 < args.Length && Number(args[i + 1]) is double z:
                    _zoomAt = Mathf.Clamp((float)z, 0.25f, 8.0f);
                    i++;
                    break;
                case "--size" when i + 1 < args.Length && Number(args[i + 1]) is double s:
                    _sizeLevel = Mathf.Clamp(s, 0.5, 2.0);
                    i++;
                    break;
                case "--tremble" when i + 1 < args.Length && Number(args[i + 1]) is double t:
                    _trembleLevel = Mathf.Clamp(t, 0.0, 2.5);
                    i++;
                    break;
                case "--exhaust" when i + 1 < args.Length && Number(args[i + 1]) is double e:
                    _exhaustLevel = Mathf.Clamp(e, 0.0, 3.0);
                    i++;
                    break;
                case "--no-tremble":
                    _tremble = false;
                    break;
                case "--no-tracks":
                    _tracks = false;
                    break;
                case "--no-exhaust":
                    _exhaust = false;
                    break;
                case "--no-shadow":
                    _shadow = false;
                    break;
                case "--no-ripples":
                    _ripples = false;
                    break;
                case "--ripple":
                    // Asking for an amount is asking for the thing - the argument
                    // --recoil <x> already makes - and parsed with the invariant
                    // culture, because this locale writes a comma for the point and
                    // a bare TryParse leaves the default standing.
                    if (i + 1 < args.Length
                        && float.TryParse(args[i + 1], NumberStyles.Float,
                                          CultureInfo.InvariantCulture,
                                          out float shove))
                    {
                        _rippleLevel = Mathf.Max(0.0f, shove);
                        _ripples = true;
                        i++;
                    }
                    break;
                case "--rumble":
                    _rumble = true;
                    break;
                case "--scan":
                    _scan = true;
                    break;
                case "--no-ui":
                    _noUi = true;
                    break;
                case "--ui":
                    _showUi = true;
                    break;
            }
        }
        // A capture is evidence, so the step is pinned for the harness's reason:
        // two runs of the same flags have to be diffable. Stage3D reads this very
        // field, which is why it is Main's and not a local one - see
        // Main.FixedStep.
        if (_capturePath is not null)
        {
            Main.FixedStep = 1.0 / 60.0;
            _noUi = !_showUi;
        }
    }

    /// <summary>Invariant, never the machine's - the trap named beside
    /// <c>--hit-scale</c> in the harness. On a comma locale a plain parse of
    /// "1.4" fails, the default stands, and two captures at two values come back
    /// byte for byte identical, which reads as a dial that does nothing rather
    /// than as a flag that was not understood.</summary>
    private static double? Number(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture,
                        out double value) ? value : null;

    private static Vector2I? Spot(string text)
    {
        string[] parts = text.Split(',');
        if (parts.Length == 2 && int.TryParse(parts[0], out int q)
            && int.TryParse(parts[1], out int r))
            return new Vector2I(q, r);
        GD.PushWarning($"tank bench: {text} is not q,r");
        return null;
    }

    // --- building ------------------------------------------------------------

    public override void _Ready()
    {
        ReadFlags();

        _map = BoardMap.ByName(Map);
        _terrain = TerrainSet.Load(AssetRoot.Terrains);
        GD.Print("tank bench: terrain " + _terrain.Note);

        _field = new HexField
        {
            Terrain = _terrain,
            // The board's own paint, which for an authored board is the mix -
            // a named plate would sit over its kinds and hide them. See
            // BoardMap.Paint, which exists because that happened once and cost
            // every tree on a map.
            Paint = _map.Paint,
            Trees = false,
            Columns = _map.Columns, Rows = _map.Rows, Plot = _map.Plot,
        };
        _field.SetKinds(_map.Kinds);
        _field.SetRelief(_map.Levels, _map.Ramps);
        // After the relief and never before it: the water's guards are asked
        // about levels and ramps, so water laid on a board with no heights yet
        // is water judged against a board that does not exist.
        _field.SetWater(_map.Water);
        _field.Position = _origin;
        AddChild(_field);

        _sea = new Swell { Field = _field };
        _wake = new Wake();
        _wash = new Ripples();
        _marks = new TrackMarks();
        AddChild(_marks);

        Garage();

        _camera = new Camera2D { Enabled = true };
        AddChild(_camera);

        if (_garage.Count == 0)
        {
            Say($"No atlases loaded from {AssetRoot.Sprites}");
            return;
        }

        // The grid takes its tile from a tank, and the medium's always: the tile
        // is one tile, so it cannot follow a dial, and the reference tank is the
        // one drawn as rendered. The same choice the harness makes, for the same
        // reason - and here it also keeps the board the same size whichever class
        // is standing on it.
        _field.Atlas = _atlases.TryGetValue("MTP", out AtlasSet? medium)
            ? medium : _garage[0].Atlas;
        _surf = WaterArt.Load(AssetRoot.Water, _field.Atlas.HexRect);
        GD.Print("tank bench: water " + _surf.Note);
        Home();

        Stage();
        ApplySize();
        foreach (Vehicle vehicle in _garage)
            Tick.Park(vehicle);
        OnlyOne();

        // The stage draws the ring under the driven tank itself - see
        // Stage3D.Selected. No canvas one: the harness keeps both because it can
        // switch modes, and here there is nothing to switch to. The ruts are
        // hidden for the other half of Main.Suppress2D's reason - the stage
        // draws its own, and the canvas copy would be an item over the whole 3D
        // world.
        _marks.Visible = false;

        var layer = new CanvasLayer();
        AddChild(layer);
        _hud = new Label { Position = new Vector2(16.0f, 12.0f) };
        _hud.AddThemeColorOverride("font_color", new Color(0.92f, 0.95f, 1.0f));
        _hud.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.85f));
        _hud.AddThemeConstantOverride("outline_size", 5);
        layer.AddChild(_hud);

        Panel(layer);
        Opening();
    }

    /// <summary>One vehicle per class that loaded.
    ///
    /// Built once and kept, rather than one tank re-dressed on every switch. The
    /// harness moved off re-dressing for a reason worth repeating: a tank that is
    /// rebuilt when the dial turns is a tank whose damage is repaired and whose
    /// clocks are reset by a dial that says nothing about either.</summary>
    private void Garage()
    {
        var missing = new List<string>();
        foreach (string tag in Main.Tags)
        {
            AtlasSet atlas;
            try
            {
                atlas = AtlasSet.Load(AssetRoot.Sprites, tag);
            }
            catch (Exception e)
            {
                missing.Add($"{tag}: {e.Message}");
                continue;
            }
            _atlases[tag] = atlas;

            var sprite = new TankSprite { Atlas = atlas };
            AddChild(sprite);
            var vehicle = new Vehicle
            {
                Tag = tag,
                Atlas = atlas,
                Sprite = sprite,
                Profile = MovementProfile.For(tag),
                HomeCell = _map.Homes[0],
            };
            vehicle.Cell = vehicle.HomeCell;
            // Phase counts come off each layer and are never assumed: the
            // renderer's count is a config value, and a clock that wraps
            // anywhere but at the seam pops there.
            foreach (TrackLoop belt in new[] { vehicle.TrackLeft, vehicle.TrackRight })
            {
                belt.Phases = atlas.TrackPhases;
                belt.Pitch = atlas.TrackPitch;
            }
            vehicle.Exhaust.Phases = atlas.ExhaustPhases;
            vehicle.Burn.Phases = atlas.BurnPhases;
            vehicle.Barrel.Phases = atlas.RecoilPhases;
            vehicle.Exhaust.TopSpeed = vehicle.Profile.TopSpeed;
            vehicle.Tremble.TopSpeed = vehicle.Profile.TopSpeed;
            vehicle.Rumble.FullSpeed = vehicle.Profile.TopSpeed * 0.5;
            // One seed apiece, so the three do not sway in step on the frame
            // two of them are on screen together - which they never are here,
            // but a seed left unset is a seed somebody has to think about later.
            vehicle.Scan.Seed = (ulong)_garage.Count + 1UL;
            _garage.Add(vehicle);
        }
        if (missing.Count > 0)
            GD.PushWarning("tank bench: " + string.Join("; ", missing));
        // The medium unless asked otherwise, because it is the one drawn as
        // rendered - the class scale is 1.00 on it - so the bench opens showing
        // a tank at the size its atlas actually is. The same tank the grid takes
        // its tile from, one line below.
        _pick = Math.Max(0, _garage.FindIndex(v => v.Tag == "MTP"));
        if (_startTag is not null)
        {
            int want = _garage.FindIndex(v => v.Tag == _startTag);
            if (want >= 0)
                _pick = want;
            else
                GD.PushWarning($"--tank {_startTag} is not one of "
                               + string.Join(", ", _garage.Select(v => v.Tag)));
        }
    }

    /// <summary>Hand the board to the stage.
    ///
    /// <b>Stage-only, and that is the board's doing rather than a preference.</b>
    /// This one has a ramp and a ford on it, and both of those are drawn by
    /// <see cref="Stage3D"/>: in 2D the ring is a flat hexagon over a slope, the
    /// pond has no side to it and the tank is not cut at the waterline. A flat
    /// mode here would be a board that does not show the two things it was laid
    /// out for.</summary>
    private void Stage()
    {
        _stage = new Stage3D
        {
            Field = _field, Origin = _origin, Eye = _camera,
            Marks = _marks, MarksAt = _marks?.Position ?? Vector2.Zero,
            Surf = _surf, Sea = _sea, Wash = _wash,
        };
        AddChild(_stage);
        foreach (Vehicle vehicle in _garage)
            _stage.Take(vehicle);
        _field.ShowField = false;
        _stage.ShowEdges = _edges;
        _stage.ShowBoard = _board;
    }

    /// <summary>The tick with this frame's world on it - <see cref="Main.Tick"/>'s
    /// arrangement and its reason: reaching for it and telling it what the world
    /// is are one act, so a dial cannot be applied through a stale binding and a
    /// call made before the first frame cannot reach a tick with no board.
    /// </summary>
    private TankTick Tick
    {
        get
        {
            _tick.Field = _field;
            _tick.Origin = _origin;
            _tick.Terrain = _terrain;
            _tick.Marks = _ruts ? _marks : null;
            _tick.Shake = _shake;
            _tick.Vehicles = _garage.Count > 0
                ? new[] { Tank } : Array.Empty<Vehicle>();
            _tick.Driven = _garage.Count > 0 ? Tank : null;
            _tick.ViewZoom = _camera?.Zoom.X ?? 1.0f;
            _tick.Staged = true;

            _tick.PitchEnabled = _pitch;
            _tick.RumbleEnabled = _rumble;
            _tick.TracksEnabled = _tracks;
            _tick.TrembleEnabled = _tremble;
            _tick.TrembleLevel = _trembleLevel;
            _tick.ExhaustEnabled = _exhaust;
            _tick.ExhaustLevel = _exhaustLevel;
            _tick.ExhaustRamp = _exhaustRamp;
            _tick.ScanEnabled = _scan;
            _tick.RecoilTube = _tube;
            _tick.RecoilShear = _shear;
            _tick.ShakeOn = _shakeOn;
            _tick.TurretHeld = _ => _spinning;
            // No gunnery: one tank has nobody to shoot at, and the lane, the
            // armour and the shell in the air are all statements about two.
            // See TankTick.Aim.
            _tick.Aim = null;
            return _tick;
        }
    }

    /// <summary>Park the view on the board at a zoom that shows all of it.
    ///
    /// Measured off the cells rather than tuned, and the measuring is the point:
    /// the window is 1600 wide and the panel takes 300 of it, so a figure that
    /// framed this board would stop framing it the day a cell is added, a level
    /// is raised or the panel widens. A capture's <c>--zoom</c> overrides the
    /// fit, because a shot taken at a stated zoom has to mean that zoom.
    ///
    /// <b>The board is centred in what can be seen, not in the window.</b> The
    /// panel is opaque, so the right three hundred pixels are not part of the
    /// picture; the view is pushed that far right in world units, which slides
    /// the board that far left on screen.</summary>
    private void Home()
    {
        Rect2 board = Spread();
        float zoom = _zoomAt ?? Fit(board);
        _camera.Zoom = new Vector2(zoom, zoom);
        float hidden = _noUi ? 0.0f : ControlPanel.Width;
        _camera.Position = board.GetCenter() + new Vector2(hidden * 0.5f / zoom, 0.0f);
    }

    /// <summary>What the board covers on the canvas: every cell's drawn anchor,
    /// grown by half a tile each way.
    ///
    /// <see cref="HexField.CellAnchor"/> rather than <c>FlatAnchor</c>, because
    /// the anchor carries the level's lift and a raised cell is drawn higher
    /// than the flat row it belongs to - which on this board is the whole top
    /// right corner. The flat rectangle would frame a board that is not the one
    /// on screen.</summary>
    private Rect2 Spread()
    {
        Vector2 tile = _field.Atlas?.HexRect.Size ?? new Vector2(248.0f, 109.0f);
        var low = new Vector2(float.MaxValue, float.MaxValue);
        var high = new Vector2(float.MinValue, float.MinValue);
        for (int r = 0; r < _field.Rows; r++)
        for (int q = 0; q < _field.Columns; q++)
        {
            var cell = new Vector2I(q, r);
            if (!_field.InBounds(cell))
                continue;
            Vector2 at = _origin + _field.CellAnchor(cell) + _field.CentreOffset;
            low = new Vector2(Mathf.Min(low.X, at.X), Mathf.Min(low.Y, at.Y));
            high = new Vector2(Mathf.Max(high.X, at.X), Mathf.Max(high.Y, at.Y));
        }
        if (low.X > high.X)
            return new Rect2(_origin, tile);
        return new Rect2(low - tile * 0.5f, high - low + tile);
    }

    /// <summary>The zoom at which that rectangle fits the free part of the
    /// window, with a tenth of it left as margin.</summary>
    private float Fit(Rect2 board)
    {
        Vector2 window = GetViewportRect().Size;
        float free = Mathf.Max(window.X - (_noUi ? 0.0f : ControlPanel.Width),
                               200.0f);
        return Mathf.Clamp(Mathf.Min(free * 0.9f / Mathf.Max(board.Size.X, 1.0f),
                                     window.Y * 0.9f / Mathf.Max(board.Size.Y, 1.0f)),
                           0.4f, 4.0f);
    }

    /// <summary>Whatever the flags asked for, once there is a tank to ask it
    /// of. Here rather than at parse time for the harness's reason: a flag that
    /// wrote straight through "the driven tank" before the tank existed hung the
    /// capture instead of failing it.</summary>
    private void Opening()
    {
        foreach (Vehicle vehicle in _garage)
            vehicle.Recoil.Level = _recoilLevel;
        Shadow();
        if (_damageAtStart > 0)
            foreach (string face in Tank.Atlas.HitFaces)
                for (int n = 0; n < _damageAtStart; n++)
                    Sprite.Damage(face, TankTick.ScatterAt(n), TankTick.RiseAt(n));
        if (_burnAtStart)
            Tank.Burning = true;
        if (_hitAtStart is double bearing)
            Hit(bearing);
        if (_fireAtStart)
            Tick.Fire(Tank);
        if (_destroyAtStart)
            Tick.Kill(Tank);
        if (_driveTo is Vector2I cell)
            OrderTo(cell);
    }

    // --- the frame -----------------------------------------------------------

    public override void _Process(double delta)
    {
        if (_garage.Count == 0)
            return;
        delta = Main.FixedStep ?? delta;

        Tick.Run(Tank, delta);

        if (_shakeOn)
            _shake.Update(delta);
        else if (_shake.Moving)
            _shake.Reset();
        Vector2 want = _shakeOn ? _shake.ScreenOffset(_camera.Zoom.X) : Vector2.Zero;
        if (_camera.Offset != want)
            _camera.Offset = want;

        if (_stage is not null)
        {
            _stage.Selected = _noUi ? null : Tank;
            _stage.ShowEdges = _edges;
            _stage.ShowBoard = _board;
            if (_sea is not null)
            {
                // The lift of the water beside the point in all three, because
                // the point is a drawn row and these are marks on the pond rather
                // than on its bed - see Stage3D.Ground, which reads the pair back
                // through.
                Vector2I wet = _field.CellAt(Tank.GroundPoint - _origin);
                float sea = _field.WaterTop(wet);
                _sea.Note(0, wet, Tank.Speed > Swell.StirAbove, Tank.GroundPoint,
                          sea);
                _wake?.Note(0, Tank.GroundPoint, sea,
                            Tank.Speed > Wake.DriveAbove, _field.IsWater(wet));
                _sea.Tick(delta);
                _wake?.Tick(delta);
                // And the ripple field, off the same point and the same share of
                // the ford's ceiling the harness reads - see Main, which does this
                // for three tanks and for the same reason.
                if (_wash is not null)
                {
                    _wash.Enabled = _ripples;
                    _wash.Level = _rippleLevel;
                }
                if (_wash is not null && Tank.Wading)
                {
                    float shove = Mathf.Clamp(
                        (float)(Tank.Speed
                                / Mathf.Max(Tank.Profile.WaterSpeed, 1e-4)),
                        0.0f, 1.0f);
                    Vector3 at = _stage.Ground(Tank.GroundPoint, sea);
                    Vector3 way = _stage.World(
                        Tank.Atlas.GroundDirection(Tank.Sprite.HullFacing), 0.0f);
                    _wash.Note(new Vector2(at.X, at.Z),
                               new Vector2(way.X, way.Z), shove);
                }
                _wash?.Tick(delta);
            }
            _stage.Trail = _wake;
            // Every frame and with the whole garage, because Place is also where
            // the billboards are rebuilt: the two that are not shown are hidden
            // sprites, so they cost an empty render target apiece and draw
            // nothing.
            _stage.Place(_garage);
        }

        if (_spinning)
        {
            Sprite.TurretFacing =
                (Sprite.TurretFacing + 60.0 * delta + 360.0) % 360.0;
            Sprite.QueueRedraw();
        }

        if (_hud is not null && !_noUi)
            _hud.Text = Note();

        _frame++;
        if (_capturePath is not null && _frame >= _captureAt)
        {
            Capture(_capturePath);
            GetTree().Quit();
        }
    }

    /// <summary>What the tank is doing, in the corner. Kept beside the panel
    /// rather than replaced by it - <see cref="WallBench"/>'s arrangement:
    /// it is the only figure visible while the panel is shut, and under
    /// <c>--no-ui</c> there is neither.</summary>
    private string Note()
    {
        Vector2I cell = _field.CellAt(Tank.GroundPoint - _origin);
        return $"{Tank.Tag}  cell ({cell.X},{cell.Y}) level {_field.LevelAt(cell)}"
               + (_field.IsRamp(cell) ? " ramp" : "")
               + $"  speed {Tank.Speed:F0}/{Tick.SpeedCap(Tank):F0} px/s "
               + Tick.SpeedCapWhy(Tank)
               + (Tank.Wading ? "  wading" : "")
               + $"\nlean {Sprite.Climb:F3}  pen {Sprite.Penetrations}"
               + $"/{Gunnery.PenetrationsToKill}"
               + (Tank.Wreck.Dead ? "  wrecked" : "")
               + "\nleft click drives, right click stops, middle drag pans"
               + "\nTAB the panel, R resets, F12 shoots";
    }

    // --- what the dials do ---------------------------------------------------

    /// <summary>Draw one of the three and put the other two away.
    ///
    /// The incoming tank is stood where the outgoing one stands, so the dial
    /// swaps the machine and not the situation: a class comparison wants the two
    /// on the same cell at the same heading, and a tank that jumped home on every
    /// switch would be answering a different question.</summary>
    private void OnlyOne()
    {
        for (int i = 0; i < _garage.Count; i++)
            _garage[i].Sprite.Visible = i == _pick;
    }

    private void Pick(int index)
    {
        index = Math.Clamp(index, 0, _garage.Count - 1);
        if (index == _pick)
            return;
        Vehicle going = Tank;
        Vector2I cell = going.Cell;
        double hull = going.Sprite.HullFacing;
        double turret = going.Sprite.TurretFacing;
        // Cancelled while it is still the driven one: CancelOrder clears the
        // route off the board only for the tank being driven, so switching
        // first would leave the outgoing tank's path drawn on a board nothing
        // is following it on.
        Tick.CancelOrder(going);
        _pick = index;
        Tank.Cell = cell;
        Tank.Sprite.HullFacing = hull;
        Tank.Sprite.TurretFacing = turret;
        Tick.Park(Tank);
        OnlyOne();
    }

    private void ApplySize()
    {
        foreach (Vehicle vehicle in _garage)
        {
            float was = vehicle.Sprite.BodyScale;
            var now = (float)(vehicle.Profile.Size * _sizeLevel);
            if (now == was)
                continue;
            vehicle.Sprite.BodyScale = now;
            vehicle.TrackLeft.Scale = now;
            vehicle.TrackRight.Scale = now;
            // Hold the contact patch still: the scale pivots on the anchor,
            // which floats above the ground, so resizing alone would lift or
            // sink the tank. See Main.ApplySize, whose arithmetic this is.
            vehicle.Sprite.Position -= vehicle.Atlas.GroundOffset * (now - was);
        }
    }

    /// <summary>Push the switch to every tank in the garage, not just the one
    /// on the board. It is a question about the effect rather than about a
    /// vehicle - the harness's rule - and a tank that came out wearing the
    /// setting from before the dial was turned would read as the dial not
    /// reaching it.</summary>
    private void Shadow()
    {
        foreach (Vehicle vehicle in _garage)
        {
            vehicle.Sprite.ShowShadow = _shadow;
            vehicle.Sprite.QueueRedraw();
        }
    }

    /// <summary>Drive there if the board has such a place, and do nothing if it
    /// has not. A button that quietly goes somewhere else instead is worse than
    /// one that does nothing: the first reads as the board being wrong.</summary>
    private void Go(Vector2I? cell)
    {
        if (cell is Vector2I at)
            OrderTo(at);
    }

    private void OrderTo(Vector2I cell)
    {
        if (!_field.InBounds(cell) || cell == Tank.Cell || Tank.Wreck.Dead)
            return;
        Tank.Path = _field.FindPath(Tank.Cell, cell, new HashSet<Vector2I>());
        Tank.PathStep = 0;
        _spinning = false;
        _field.Highlight = Tank.Path;
        _field.QueueRedraw();
    }

    private void Hit(double bearing)
    {
        _hitSide = Main.SideFor(bearing);
        Tick.TakeHit(Tank, HitFrom, Main.CalibreAt(_calibre), null,
                     Main.BiteFor(_calibre));
    }

    private void Reset()
    {
        foreach (Vehicle vehicle in _garage)
        {
            Tick.CancelOrder(vehicle);
            vehicle.Sprite.Repair();
            vehicle.Wreck.Reset();
            vehicle.Burning = false;
            vehicle.Hit.Reset();
            vehicle.Cell = vehicle.HomeCell;
            vehicle.Sprite.HullFacing = 270.0;
            vehicle.Sprite.TurretFacing = 270.0;
            Tick.Park(vehicle);
        }
        _shake.Reset();
        _sea?.Settle();
        _wash?.Settle();
        _field.Highlight = Array.Empty<Vector2I>();
        _field.QueueRedraw();
        Home();
    }

    // --- the panel -----------------------------------------------------------

    /// <summary>The side panel: the class, where it points, what moves on it and
    /// what has been done to it.
    ///
    /// <b>The harness's own panel, not a second one.</b> <see cref="ControlPanel"/>
    /// takes a heading and a handful of rows and knows nothing about which bench
    /// it is on, so there was nothing to port - and a panel of this bench's own
    /// would be a second answer to how a row is wired, how it stays in step and
    /// what takes keyboard focus. Every row is a pair of delegates onto the very
    /// field a key writes, so the key and the widget cannot disagree.
    ///
    /// <b>Nothing is added to <c>panel.json</c>, and that is deliberate</b> -
    /// <see cref="WallBench.Panel"/>'s reason: the file is checked against the
    /// harness's panel in both directions, so an entry for a row only this bench
    /// builds would fail that check as a description of nothing. These rows carry
    /// their own captions and take the built-in fallback.</summary>
    private void Panel(CanvasLayer layer)
    {
        _panel = new ControlPanel();
        _panel.Prepare();

        _panel.Heading("tank.pick", "tank");
        _panel.Choice("tank.pick.class", "class",
                      _garage.Select(v => $"{v.Tag}  {v.Profile.TopSpeed:F0} px/s").ToList(),
                      () => _pick, Pick);
        _panel.Slide("tank.pick.size", "size level", 0.5, 2.0, 0.05,
                     () => _sizeLevel,
                     v => { _sizeLevel = v; ApplySize(); }, "x",
                     () => $"class {Tank.Profile.Size:F2}x   "
                           + $"{Tank.Atlas.HullSpan * Sprite.BodyScale:F0}px hull"
                           + $" / {Tank.Atlas.HexRect.Size.X * 0.75f:F0}px cell");
        _panel.Readout("tank.pick.class_note", () =>
            $"{Tank.Profile.TopSpeed:F0} px/s cruise, "
            + $"{Tank.Profile.Accel:F0} px/s^2, "
            + $"{Tank.Profile.ReloadTime:F1}s reload, "
            + $"{Tank.Profile.TurnRate:F0} deg/s hull");

        _panel.Heading("tank.where", "where it is");
        _panel.Readout("tank.where.cell", () =>
        {
            Vector2I cell = _field.CellAt(Tank.GroundPoint - _origin);
            return $"({cell.X},{cell.Y}) level {_field.LevelAt(cell)}"
                   + (_field.IsRamp(cell) ? ", a ramp" : "")
                   + (_field.IsWater(cell) ? ", in the water" : "");
        });
        _panel.Readout("tank.where.speed", () =>
            $"{Tank.Speed:F0} of {Tick.SpeedCap(Tank):F0} px/s "
            + (Tick.SpeedCapWhy(Tank) is { Length: > 0 } why
                ? $"- {why}" : "- on the level"));
        _panel.PressPair("tank.where.go",
                         "to the rise", () => Go(_map.Crown()),
                         "to the water", () => Go(_map.Ringed(true)));
        _panel.PressPair("tank.where.far",
                         "to the island", () => Go(_map.Ringed(false)),
                         "back to the middle", () => OrderTo(_map.Homes[0]));

        _panel.Heading("tank.aim", "heading");
        _panel.Slide("tank.aim.hull", "hull", 0.0, 345.0, 15.0,
                     () => Sprite.HullFacing,
                     v =>
                     {
                         Tick.CancelOrder(Tank);
                         Sprite.HullFacing = v;
                         Sprite.QueueRedraw();
                     }, " deg");
        _panel.Slide("tank.aim.turret", "turret", 0.0, 345.0, 15.0,
                     () => Sprite.TurretFacing,
                     v =>
                     {
                         _spinning = false;
                         Sprite.TurretFacing = v;
                         Sprite.QueueRedraw();
                     }, " deg");
        _panel.Toggle("tank.aim.spin", "spin the turret  (SPACE)",
                      () => _spinning, on => _spinning = on);
        _panel.Toggle("tank.aim.scan", "look around on the halt  (N)",
                      () => _scan, on => _scan = on);

        _panel.Heading("tank.ride", "ride");
        _panel.Toggle("tank.ride.pitch", "body pitch  (P)", () => _pitch,
                      on => { _pitch = on; Tick.UpdatePitch(Tank, 0.0, 0.0); });
        _panel.Toggle("tank.ride.rumble", "ground rumble  (B)", () => _rumble,
                      on => { _rumble = on; Tick.UpdateRumble(Tank, 0.0); });
        _panel.Toggle("tank.ride.tremble", "engine tremble  (I)", () => _tremble,
                      on => { _tremble = on; Tick.UpdateTremble(Tank, 0.0); });
        _panel.Slide("tank.ride.tremble_level", "tremble level", 0.0, 2.5, 0.05,
                     () => _trembleLevel, v => _trembleLevel = v, "x",
                     () => $"{Tank.Tremble.TravelAt(Tank.Speed, _trembleLevel):F2}px "
                           + "of stern travel at this speed");
        _panel.Toggle("tank.ride.tracks", "belts wind  (C)", () => _tracks,
                      on => { _tracks = on; Tick.UpdateTracks(Tank, (0.0, 0.0), 0.0); });
        _panel.Toggle("tank.ride.ruts", "leave ruts", () => _ruts,
                      on => _ruts = on);
        _panel.Readout("tank.ride.belt", () =>
            $"belt {Tank.Track.Phase:F2}, slip {Tank.Track.Slip:F2}, "
            + $"blur {Tank.Track.Blur:F2}");

        _panel.Heading("tank.smoke", "exhaust and fire");
        _panel.Toggle("tank.smoke.on", "exhaust plume  (O)", () => _exhaust,
                      on => { _exhaust = on; Tick.UpdateExhaust(Tank, 0.0); });
        _panel.Slide("tank.smoke.level", "plume rate", 0.0, 3.0, 0.05,
                     () => _exhaustLevel, v => _exhaustLevel = v, "x",
                     () =>
                     {
                         double rate = Tank.Exhaust.RateAt(Tank.Speed, _exhaustLevel);
                         return $"{rate:F1} phases/s, "
                                + $"{60.0 / Math.Max(rate, 0.01):F1} frames a phase";
                     });
        _panel.Toggle("tank.smoke.ramp", "analogue load ramp", () => _exhaustRamp,
                      on => _exhaustRamp = on);
        _panel.Toggle("tank.smoke.burn", "set it alight  (J)",
                      () => Tank.Burning, on => Tank.Burning = on);

        _panel.Heading("tank.gun", "the gun");
        _panel.Press("tank.gun.fire", "fire  (Z)", () => Tick.Fire(Tank));
        _panel.Toggle("tank.gun.tube", "barrel recoil  ([)", () => _tube,
                      on => _tube = on);
        _panel.Toggle("tank.gun.shear", "body shear on the shot  (])",
                      () => _shear, on => _shear = on);
        _panel.Slide("tank.gun.recoil", "shear level", 0.0, 2.5, 0.05,
                     () => _recoilLevel,
                     v =>
                     {
                         _recoilLevel = v;
                         foreach (Vehicle vehicle in _garage)
                             vehicle.Recoil.Level = v;
                     }, "x",
                     () => $"peak {Tank.Recoil.PeakFor(_recoilLevel):F3} against "
                           + $"{Recoil.RigidBodyPeak:F3} for a rigid body");
        _panel.Toggle("tank.gun.shake", "camera shake", () => _shakeOn,
                      on =>
                      {
                          _shakeOn = on;
                          if (!on)
                              _shake.Reset();
                      });
        _panel.Slide("tank.gun.shake_level", "shake level", 0.0, 2.5, 0.05,
                     () => _shake.Level, v => _shake.Level = v, "x",
                     () => $"{Tank.Profile.ShotShake * _shake.Level:F1}px broadside");
        _panel.Slide("tank.gun.traverse", "traverse level", 0.0, 2.5, 0.05,
                     () => _traverse,
                     v => { _traverse = v; Gunnery.TraverseLevel = v; }, "x",
                     () => $"{Gunnery.TraverseRate(Tank.Profile):F0} deg/s");

        _panel.Heading("tank.armour", "armour");
        _panel.Radio("tank.armour.side",
                     () => $"a shell from side {_hitSide + 1} of 6, "
                           + $"{HitFrom:F0} deg",
                     Array.ConvertAll(HexField.EdgeHeadings, d => $"{d}"),
                     () => _hitSide, i => _hitSide = i);
        _panel.Choice("tank.armour.calibre", "calibre",
                      new[] { "light", "medium", "heavy" },
                      () => _calibre,
                      i => _calibre = Math.Clamp(i, 0, Main.CalibreCount - 1));
        _panel.PressPair("tank.armour.hit", "take a hit  (U)", () => Hit(HitFrom),
                         "repair", () =>
                         {
                             Sprite.Repair();
                             Tank.Wreck.Reset();
                             Tank.Burning = false;
                         });
        _panel.Press("tank.armour.kill", "destroy it", () => Tick.Kill(Tank));
        _panel.Readout("tank.armour.state", () =>
            string.Join(", ", Tank.Atlas.HitFaces
                .Select(f => $"{f} {Sprite.Wear(f)}"))
            + $"\n{Sprite.Penetrations} of {Gunnery.PenetrationsToKill} "
            + "penetrations"
            + (Tank.Wreck.Dead ? $", wrecked {Tank.Wreck.Age:F1}s" : ""));
        _panel.Toggle("tank.armour.shadow", "contact shadow", () => _shadow,
                      on => { _shadow = on; Shadow(); });

        _panel.Heading("tank.view", "board");
        _panel.Toggle("tank.view.board", "draw the ground", () => _board,
                      on => _board = on);
        _panel.Toggle("tank.view.edges", "cell outlines", () => _edges,
                      on => _edges = on);
        _panel.Toggle("tank.view.ripples", "the water is simulated  (--no-ripples)",
                      () => _ripples, on => _ripples = on);
        _panel.Slide("tank.view.shove", "how hard a hull shoves  (--ripple)",
                     0.0, 6.0, 0.25,
                     () => _rippleLevel, v => _rippleLevel = (float)v,
                     "x", () =>
                     {
                         if (_wash is not { Wide: > 0 })
                             return "no field on the board";
                         if (!_ripples)
                             return "off - flat water, as it was before this";
                         return $"{_wash.Peak:F2} units standing, "
                                + $"{_wash.Wide}x{_wash.Tall} texels";
                     });
        _panel.Press("tank.view.reset", "reset  (R)", Reset);

        // Opened on the tank, closed elsewhere. The harness opens every group
        // shut because it has fifty rows in ten of them; here there are eight
        // groups, and the one the bench exists for should not need a click to
        // be found.
        _panel.Expand("tank.pick", true);
        if (!_noUi)
        {
            layer.AddChild(_panel);
            _panel.AddHandle();
        }
        else
        {
            // Nothing will draw or sync it, and a detached branch of Controls
            // left lying about is a wall of leak warnings at exit that would
            // hide a real one later - the harness's reason.
            _panel.QueueFree();
            _panel = null;
        }
    }

    // --- input ---------------------------------------------------------------

    private Vector2? _middleFrom;

    public override void _UnhandledInput(InputEvent @event)
    {
        if (_garage.Count == 0)
            return;

        if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Middle } middle)
        {
            if (middle.Pressed)
            {
                _middleFrom = GetGlobalMousePosition();
                return;
            }
            Vector2? from = _middleFrom;
            _middleFrom = null;
            if (from is Vector2 down && Main.MiddleTap(down, GetGlobalMousePosition()))
                Tick.Kill(Tank);
            return;
        }
        if (@event is InputEventMouseMotion motion
            && (motion.ButtonMask & MouseButtonMask.Middle) != 0)
        {
            _camera.Position -= motion.Relative / _camera.Zoom;
            return;
        }
        if (@event is InputEventMouseButton { Pressed: true } mouse)
        {
            switch (mouse.ButtonIndex)
            {
                case MouseButton.Left:
                    // By cell, like every button on the harness's board: the
                    // silhouette overhangs its own hexagon, so picking by pixels
                    // would order a drive by clicking the ground beside it.
                    OrderTo(_field.ClampCell(
                        _field.CellAt(_field.ToLocal(GetGlobalMousePosition()))));
                    return;
                case MouseButton.Right:
                    Tick.CancelOrder(Tank);
                    return;
                case MouseButton.WheelUp:
                case MouseButton.WheelDown:
                    float factor = mouse.ButtonIndex == MouseButton.WheelUp
                        ? 1.25f : 0.8f;
                    float zoom = Mathf.Clamp(_camera.Zoom.X * factor, 0.25f, 8.0f);
                    _camera.Zoom = new Vector2(zoom, zoom);
                    return;
            }
            return;
        }
        if (@event is not InputEventKey { Pressed: true, Echo: false } key)
            return;
        switch (key.Keycode)
        {
            case >= Key.Key1 and <= Key.Key9:
                Pick((int)(key.Keycode - Key.Key1));
                break;
            case Key.A:
                Sprite.TurnHull(-15.0);
                Sprite.QueueRedraw();
                break;
            case Key.D:
                Sprite.TurnHull(15.0);
                Sprite.QueueRedraw();
                break;
            case Key.Q:
                Sprite.TurretFacing = (Sprite.TurretFacing - 15.0 + 360.0) % 360.0;
                Sprite.QueueRedraw();
                break;
            case Key.E:
                Sprite.TurretFacing = (Sprite.TurretFacing + 15.0) % 360.0;
                Sprite.QueueRedraw();
                break;
            case Key.Space:
                _spinning = !_spinning;
                break;
            case Key.Z:
                Tick.Fire(Tank);
                break;
            case Key.U:
                _hitSide = (_hitSide + 1) % HexField.EdgeHeadings.Length;
                Hit(HitFrom);
                break;
            case Key.J:
                Tank.Burning = !Tank.Burning;
                break;
            case Key.P:
                _pitch = !_pitch;
                Tick.UpdatePitch(Tank, 0.0, 0.0);
                break;
            case Key.B:
                _rumble = !_rumble;
                Tick.UpdateRumble(Tank, 0.0);
                break;
            case Key.I:
                _tremble = !_tremble;
                Tick.UpdateTremble(Tank, 0.0);
                break;
            case Key.C:
                _tracks = !_tracks;
                Tick.UpdateTracks(Tank, (0.0, 0.0), 0.0);
                break;
            case Key.O:
                _exhaust = !_exhaust;
                Tick.UpdateExhaust(Tank, 0.0);
                break;
            case Key.N:
                _scan = !_scan;
                break;
            case Key.Bracketleft:
                _tube = !_tube;
                break;
            case Key.Bracketright:
                _shear = !_shear;
                break;
            case Key.G:
                _board = !_board;
                break;
            case Key.Escape:
                Tick.CancelOrder(Tank);
                break;
            case Key.Tab:
                _panel?.Flip();
                break;
            case Key.R:
                Reset();
                break;
            case Key.F12:
                Capture($"{AssetRoot.Out}/tank_{Time.GetTicksMsec()}.png");
                break;
        }
    }

    private void Say(string what)
    {
        var layer = new CanvasLayer();
        AddChild(layer);
        var label = new Label { Position = new Vector2(16.0f, 12.0f), Text = what };
        label.AddThemeColorOverride("font_color", new Color(1.0f, 0.7f, 0.6f));
        layer.AddChild(label);
        GD.PushWarning("tank bench: " + what);
    }

    private void Capture(string path)
    {
        Image image = GetViewport().GetTexture().GetImage();
        DirAccess.MakeDirRecursiveAbsolute(path.GetBaseDir());
        Error err = image.SavePng(path);
        GD.Print(err == Error.Ok ? $"tank bench: wrote {path}"
                                 : $"tank bench: could not write {path}: {err}");
    }
}
