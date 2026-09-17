using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The event bench: a board with every kind of cell on it, five tanks a shot
/// apart, and a panel whose buttons are named after the events the rules will
/// raise - "knocked out", "ricochet", "smoke on this cell" - rather than after
/// the dials that make a picture.
///
/// <b>The ninth root, and the first one built to be driven by events.</b> The
/// tank benches judge one machine under every dial; the effects bench judges
/// one burst with no machine at all. Neither can show a ricochet into the next
/// tank over, a ram that shoves, or an explosion lighting its neighbours, and
/// none of them can be told "do event X on frame N" from the command line. This
/// one can, and it exists so that every effect the game needs has a button
/// before it has a picture - a button that says <c>TODO</c> over the tank until
/// the effect lands, so the list of what is missing is on the screen rather
/// than in a document.
///
/// <b>Three scenes, one board</b> - <see cref="BoardMap.Events"/>. The scenes
/// differ in <see cref="Set"/>: which group of the panel opens unfolded and
/// where the camera starts. Not three boards, because an event on the field
/// lights a tank and an event on a tank lights the field, and the seams are the
/// point.
///
/// <b>The mechanics are assembled, never rewritten</b> - the rule every bench
/// here lives under. Tanks are stood up by <see cref="Fleet"/>, driven by
/// <see cref="TankTick"/>, drawn by <see cref="Stage3D"/>; what this file owns
/// is the board, the panel and, in time, the queue that turns an event into a
/// call on those three. See docs/effects-benches.md for the plan this is the
/// first step of.
/// </summary>
public sealed partial class EventBench : SceneRoot
{
    /// <summary>The bench shoots on frame forty, like the tank bench: an
    /// event takes a second to be worth looking at.</summary>
    public EventBench() : base(40) { }

    /// <summary>Which of the three scenes this is: <c>tank</c>, <c>field</c>
    /// or <c>overlay</c>. Decides which panel group opens unfolded and where the
    /// camera starts; nothing about the board.</summary>
    [Export] public string Set = "tank";

    /// <summary>The panel's names, notes and opening values, read through the
    /// same <see cref="PanelText"/> as the harness's and the tank bench's.</summary>
    public const string PanelFile = "events.json";

    private BoardMap _map = null!;
    private HexField _field = null!;
    private TerrainSet _terrain = null!;
    private PropSet _props = null!;
    private Grove _grove = null!;
    private Wildfire _fire = null!;
    private Stage3D? _stage;
    private Camera2D _camera = null!;
    private Label? _hud;
    private ControlPanel? _panel;
    private PanelText _text = PanelText.Load(null);
    private readonly TankTick _tick = new();
    private readonly CameraShake _shake = new();
    private readonly Craters _pits = new();
    private Swell? _sea;
    private Ripples? _wash;
    private WaterArt? _surf;
    private FlashSheet? _sheet;
    private TrackMarks? _marks;

    private readonly Dictionary<string, AtlasSet> _atlases = new();
    private readonly List<Vehicle> _vehicles = new();

    /// <summary>Who acts and who is acted on - the two common rows every
    /// event reads. Indices into <see cref="_vehicles"/>.</summary>
    private int _actor;
    private int _target = 1;

    /// <summary>The cell an event on the field happens on. The middle button
    /// picks it, as on the effects bench.</summary>
    private Vector2I _cell;

    /// <summary>The cell <c>--cell</c> named, or null: the board's own default
    /// is read once the board exists, and a flag must survive that.</summary>
    private Vector2I? _cellAsked;

    /// <summary>How far under the bank the deep water stands, if
    /// <c>--deep-depth</c> said, or negative for the field's own default -
    /// see <see cref="HexField.DeepDepth"/>. Read before the field exists,
    /// like the cell.</summary>
    private double _deepDepth = -1.0;

    /// <summary>And how far under its level the pond's bed is drawn, if
    /// <c>--deep-bed</c> said - see <see cref="HexField.DeepBed"/>.</summary>
    private double _deepBed = -1.0;

    /// <summary>Which look a hull's plunge into the pond is thrown in - see
    /// <see cref="Plunge.Style"/>; <c>--splash calm|cinematic</c>, the row
    /// <c>bench.splash</c>. Names in <see cref="Main.SplashNames"/>.</summary>
    private Plunge.Style _splash = Plunge.Style.Cinematic;

    /// <summary>Which side of the hex a shot comes from, as an index into
    /// <see cref="HexField.EdgeHeadings"/>.</summary>
    private int _face = 3;

    /// <summary>Which way the target's hull points, degrees in the stand's
    /// bearings (270 is the default, bow to the camera). A row and a flag so a
    /// picture that depends on the heading - the thrown turret lands astern,
    /// the wreck's pose, the deck fire's sort against the turret - can be
    /// judged from every side without driving the tank round. The turret goes
    /// with it: a parked tank has its gun over the bow.</summary>
    private double _heading = 270.0;

    /// <summary>How fast the events play, as a multiplier on the frame.</summary>
    private double _speed = 1.0;

    /// <summary>The size of a burst into a cell, on Ordnance's scale.</summary>
    private double _might = 1.0;

    /// <summary>The multiplier over the classes' authored sizes - the tank
    /// bench's "size level", and like there never a size of its own.</summary>
    private double _sizeLevel = 1.0;

    /// <summary>The queue that turns a button into a picture.</summary>
    private Playback? _play;

    /// <summary>Buttons <c>--play</c> asked for, pressed on <see cref="_playAt"/>.
    /// </summary>
    private readonly List<string> _queued = new();

    /// <summary>The cells <c>--drive</c> named, driven to by the target in this
    /// order once the queued buttons have been pressed.</summary>
    private readonly List<Vector2I> _drives = new();
    private int _playAt = 1;
    private bool _played;

    /// <summary>The TODO labels over tanks and cells: what the board cannot
    /// show yet, said on the board. Each dies after a few seconds of playback.
    /// </summary>
    private readonly List<(Label Tag, Vehicle? Over, Vector2I? At, double Until)> _todo = new();
    private CanvasLayer? _layer;
    private double _clock;

    private readonly Vector2 _origin = new(220, 200);
    private int _frame;

    /// <summary>The rows a flag has claimed, kept off the file's opening
    /// values - the tank bench's arrangement.</summary>
    private readonly HashSet<string> _flagged = new();

    private Vehicle Actor => _vehicles[_actor];
    private Vehicle Target => _vehicles[Math.Min(_target, _vehicles.Count - 1)];

    private TankTick Tick
    {
        get
        {
            Bind();
            return _tick;
        }
    }

    // --- start-up ------------------------------------------------------------

    public override void _Ready()
    {
        ReadFlags();
        // A capture that cannot start must end: an exception here leaves an
        // engine with nothing to quit it, and a run that was started for one
        // screenshot then sits until it is killed. Interactive runs keep the
        // engine's own behaviour - the error is on the console and the window
        // stays to be read.
        try
        {
            Open();
        }
        catch (Exception e) when (CapturePath is not null)
        {
            GD.PushError($"events: could not open - {e}");
            GetTree().Quit(1);
        }
    }

    private void Open()
    {
        _map = BoardMap.Events;
        _cell = _cellAsked
                ?? (_map.Homes.Count > 1 ? _map.Homes[1] : new Vector2I(0, 0));

        _terrain = TerrainSet.Load(AssetRoot.Terrains);
        GD.Print("events: terrain " + _terrain.Note);
        _props = PropSet.Load(AssetRoot.Props);
        GD.Print("events: props " + _props.Note);
        // What the rules put on a cell and the player has to see - MineArt, whose
        // own note says why it is not in the prop set. Said out loud because an
        // absent folder is a legal state and a silently unmarked minefield is not
        // a difference anybody would spot on a board full of ground.
        GD.Print("events: markers "
                 + (MineArt.Read(AssetRoot.Markers)
                     ? $"mine {MineArt.Span:F0}px of art at {MineArt.Wide:F2} of a tile"
                     : $"none at {AssetRoot.Markers}"));

        _field = new HexField
        {
            Terrain = _terrain,
            Paint = _map.Paint,
            Trees = _props.Any,
            Columns = _map.Columns, Rows = _map.Rows, Plot = _map.Plot,
        };
        _field.SetKinds(_map.Kinds);
        _field.SetGround(_map.Ground);
        _field.SetCover(_map.Over);
        _field.SetRelief(_map.Levels, _map.Ramps);
        if (_deepDepth >= 0.0)
            _field.DeepDepth = _deepDepth;
        if (_deepBed >= 0.0)
            _field.DeepBed = _deepBed;
        // After the relief and never before it - the tank bench's reason: the
        // water's guards are asked about levels and ramps.
        _field.SetWater(_map.Water);
        _field.Position = _origin;
        AddChild(_field);

        _marks = new TrackMarks();
        AddChild(_marks);
        _grove = new Grove
        {
            Field = _field, Props = _props, Origin = _origin, Enabled = _props.Any,
        };
        AddChild(_grove);
        // Lit by events and never by itself: the rules count fire in rounds,
        // and a wood that spread on its own clock would be a second author of
        // the board. Enabled, because Light refuses on a disabled fire; not
        // spreading, because that is the rules' decision - see Wildfire.Spreads.
        // Ruled, so it does not go out on its own either: it holds at full flame
        // until a field tick has counted its two rounds (Wildfire.Ruled), and the
        // masonry bars the spread the way the GDD says it does.
        _fire = new Wildfire
        {
            Field = _field, Enabled = true, Spreads = false, Ruled = true,
            Wooded = _grove.Carrying, Barred = Walled,
        };
        _grove.Fire = _fire;

        Garage();

        _camera = new Camera2D { Enabled = true };
        AddChild(_camera);

        if (_vehicles.Count == 0)
        {
            GD.PushError($"events: no atlases loaded from {AssetRoot.Sprites}");
            return;
        }

        _field.Atlas = _atlases.TryGetValue("MTP", out AtlasSet? medium)
            ? medium : _vehicles[0].Atlas;
        _surf = WaterArt.Load(AssetRoot.Water, _field.Atlas.HexRect);
        _sea = new Swell { Field = _field };
        _wash = new Ripples();
        _grove.Plant();
        GD.Print("events: grove " + _grove.Note());

        Stage();
        // Before the park, so the contact patch the park sets is the scaled
        // tank's - and at all, which the first cut of this bench forgot: five
        // classes at the generator's one size, the light as large as the heavy.
        foreach (Vehicle vehicle in _vehicles)
        {
            Fleet.Resize(vehicle, _sizeLevel);
            Tick.Park(vehicle);
        }
        Face();
        Home();
        _play = new Playback
        {
            Tick = Tick, Field = _field, Stage = _stage, Fire = _fire,
            Origin = _origin, Bricks = _bricks, Todo = Todo,
        };

        var layer = new CanvasLayer();
        AddChild(layer);
        _layer = layer;
        if (!NoUi)
        {
            _hud = new Label { Position = new Vector2(16.0f, 12.0f) };
            _hud.AddThemeColorOverride("font_color", new Color(0.92f, 0.95f, 1.0f));
            _hud.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.85f));
            _hud.AddThemeConstantOverride("outline_size", 5);
            layer.AddChild(_hud);
        }
        Panel(layer);
    }

    private void ReadFlags()
    {
        string[] args = OS.GetCmdlineUserArgs();
        for (int i = 0; i < args.Length; i++)
        {
            if (ReadCommonFlag(args, ref i))
                continue;
            switch (args[i])
            {
                case "--set" when i + 1 < args.Length:
                    Set = args[++i].ToLowerInvariant();
                    break;
                case "--actor" when i + 1 < args.Length
                                   && int.TryParse(args[i + 1], out int actor):
                    _actor = actor;
                    _flagged.Add("bench.actor");
                    i++;
                    break;
                case "--target" when i + 1 < args.Length
                                    && int.TryParse(args[i + 1], out int target):
                    _target = target;
                    _flagged.Add("bench.target");
                    i++;
                    break;
                case "--cell" when i + 1 < args.Length:
                    string[] qr = args[++i].Split(',');
                    if (qr.Length == 2 && int.TryParse(qr[0], out int q)
                        && int.TryParse(qr[1], out int r))
                        _cellAsked = new Vector2I(q, r);
                    break;
                case "--face" when i + 1 < args.Length
                                  && int.TryParse(args[i + 1], out int deg):
                    _face = Angles.SideFor(deg);
                    _flagged.Add("bench.face");
                    i++;
                    break;
                case "--heading" when i + 1 < args.Length
                                     && double.TryParse(args[i + 1],
                                         System.Globalization.NumberStyles.Float,
                                         System.Globalization.CultureInfo.InvariantCulture,
                                         out double heading):
                    _heading = ((heading % 360.0) + 360.0) % 360.0;
                    _flagged.Add("bench.heading");
                    i++;
                    break;
                case "--size" when i + 1 < args.Length
                                  && double.TryParse(args[i + 1],
                                      System.Globalization.NumberStyles.Float,
                                      System.Globalization.CultureInfo.InvariantCulture,
                                      out double size):
                    _sizeLevel = Math.Clamp(size, 0.5, 2.0);
                    i++;
                    break;
                case "--might" when i + 1 < args.Length
                                   && double.TryParse(args[i + 1],
                                       System.Globalization.NumberStyles.Float,
                                       System.Globalization.CultureInfo.InvariantCulture,
                                       out double might):
                    _might = might;
                    _flagged.Add("field.blast.might");
                    i++;
                    break;
                // Which buttons to press, and on which frame. Pressed after the
                // board is up rather than here, because a button is a call on
                // tanks that do not exist yet.
                case "--play" when i + 1 < args.Length:
                    _queued.AddRange(args[++i].Split(',',
                        StringSplitOptions.RemoveEmptyEntries));
                    break;
                case "--play-at" when i + 1 < args.Length
                                     && int.TryParse(args[i + 1], out int at):
                    _playAt = Math.Max(1, at);
                    i++;
                    break;
                // Invariant culture for SceneRoot.ReadCommonFlag's reason: this
                // machine's locale reads "0.85" as nothing.
                case "--deep-depth" when i + 1 < args.Length
                                        && double.TryParse(args[i + 1], NumberStyles.Float,
                                                           CultureInfo.InvariantCulture,
                                                           out double brim):
                    _deepDepth = brim;
                    _flagged.Add("bench.deep_depth");
                    i++;
                    break;
                // The other picture of the pond: no wading gear, the engine
                // stops on the first cell of water - see TankTick.Amphibious.
                case "--no-amphibious":
                    _tick.Amphibious = false;
                    _flagged.Add("bench.amphibious");
                    break;
                // A dial over every class's draught - see TankTick.DraughtScale.
                case "--draught" when i + 1 < args.Length
                                     && double.TryParse(args[i + 1], NumberStyles.Float,
                                                        CultureInfo.InvariantCulture,
                                                        out double draught):
                    _tick.DraughtScale = draught;
                    _flagged.Add("bench.draught");
                    i++;
                    break;
                // A drive of the target, cell by cell, after the buttons - so a
                // run can put a tank somewhere and then order it out, which is
                // how "out of the pond by the ramp alone" is shown. Several are
                // queued in order; each waits for the one before to arrive.
                case "--drive" when i + 1 < args.Length:
                    string[] qrd = args[++i].Split(',');
                    if (qrd.Length == 2 && int.TryParse(qrd[0], out int dq)
                        && int.TryParse(qrd[1], out int dr))
                        _drives.Add(new Vector2I(dq, dr));
                    else
                        GD.PushWarning($"events: --drive wants q,r, got '{args[i]}'");
                    break;
                case "--deep-bed" when i + 1 < args.Length
                                      && double.TryParse(args[i + 1], NumberStyles.Float,
                                                         CultureInfo.InvariantCulture,
                                                         out double bed):
                    _deepBed = bed;
                    _flagged.Add("bench.deep_bed");
                    i++;
                    break;
                // Which look the plunge is thrown in - see Plunge.Style.
                case "--splash" when i + 1 < args.Length:
                    int look = Array.IndexOf(Main.SplashNames, args[++i].ToLowerInvariant());
                    if (look >= 0)
                    {
                        _splash = (Plunge.Style)look;
                        _flagged.Add("bench.splash");
                    }
                    else
                        GD.PushWarning($"events: --splash wants calm or cinematic, got '{args[i]}'");
                    break;
                default:
                    GD.PushWarning($"events: unknown flag {args[i]}");
                    break;
            }
        }
        SettleForProof(CapturePath is not null);
    }

    /// <summary>One tank per parking, in the class the parking names - all
    /// five, since the board parks all five; an atlas the board did not ask
    /// for would not be loaded.</summary>
    private void Garage()
    {
        _sheet = FlashSheet.Load(AssetRoot.Sprites + "/Fire_rgba.png");
        if (_sheet.Error.Length > 0)
            GD.PushWarning("events: flash sheet - " + _sheet.Error);
        var missing = new List<string>();
        foreach (Parking park in _map.Parked)
        {
            string tag = park.Class ?? "MTP";
            if (!_atlases.TryGetValue(tag, out AtlasSet? atlas))
            {
                try
                {
                    atlas = AtlasSet.Load(AssetRoot.Sprites, tag);
                }
                catch (Exception e)
                {
                    missing.Add($"{tag}: {e.Message}");
                    continue;
                }
                if (atlas.Error.Length > 0)
                {
                    missing.Add($"{tag}: {atlas.Error}");
                    continue;
                }
                _atlases[tag] = atlas;
            }
            _vehicles.Add(Fleet.Crew(this, tag, atlas, _sheet, park.Cell,
                                     (ulong)_vehicles.Count + 1UL));
        }
        if (missing.Count > 0)
            GD.PushWarning("events: " + string.Join("; ", missing));
        _actor = Math.Clamp(_actor, 0, Math.Max(0, _vehicles.Count - 1));
        _target = Math.Clamp(_target, 0, Math.Max(0, _vehicles.Count - 1));
    }

    /// <summary>Stage-only, for the tank bench's reason and three more: the
    /// ramps, the ford, the deep pond and the brick on this board are all
    /// drawn by <see cref="Stage3D"/> and by nothing else.</summary>
    private void Stage()
    {
        _stage = new Stage3D
        {
            Field = _field, Origin = _origin, Eye = _camera,
            Marks = _marks, MarksAt = _marks?.Position ?? Vector2.Zero,
            Wood = _grove, Blaze = _fire, Pits = _pits,
            Surf = _surf, Sea = _sea, Wash = _wash,
        };
        AddChild(_stage);
        foreach (Vehicle vehicle in _vehicles)
            _stage.Take(vehicle);
        _field.ShowField = false;
        _grove.Visible = false;
        if (_marks is not null)
            _marks.Visible = false;
        // The board's masonry as one thing, before the first wall stands on it:
        // the record, the round and the ram watcher are WallField's, and what
        // this bench adds is a board and the events that act on it. No Order:
        // nothing here drives by pathing of its own - Playback orders the tank.
        _bricks = new WallField
        {
            Field = _field, Stage = _stage, Origin = _origin, Tick = _tick,
            Down = (cell, heading) => GD.Print(
                $"events: wall ({cell.X},{cell.Y}) side {heading} is down"),
        };
        // Bricks on every walled cell, the tank bench's arrangement: the plan,
        // the fit and the fall are WallProp's, and what this bench adds is a
        // board to stand them on.
        // The recipe off the map's own entry - Masonry.Laying, the one join
        // between the map and the builder - so a bare wall letter is the closed
        // ring it has always meant and an authored shape is that shape.
        foreach (Vector2I cell in _map.Walled())
        {
            (WallKit.Recipe recipe, int bearing) = _map.MasonryAt(cell).Laying()
                ?? (new WallKit.Recipe { Sides = TankBench.RingSides },
                    HexField.EdgeHeadings[0]);
            var prop = new WallProp
            {
                Field = _field, Stage = _stage, Cell = cell,
                Recipe = recipe, Borrow = null,
                Channel = _bricks.Walls.Count,
            };
            AddChild(prop);
            prop.Bearing = bearing;
            prop.Build();
            _bricks.Add(prop);
        }
    }

    /// <summary>The masonry on this board and everything a round or a hull does
    /// with it - <see cref="WallField"/>, the same one the tank bench drives.
    /// This bench adds a board to stand it on and the events that act on it.
    /// </summary>
    private WallField _bricks = null!;

    /// <summary>Whether masonry stands between two neighbouring cells - the answer
    /// to <see cref="Wildfire.Barred"/>, and the GDD's "a wall blocks the spread:
    /// wood behind it is not a neighbour while the wall stands".
    ///
    /// <b>Asked of both ends</b>, because a wall belongs to one cell and stands on
    /// the rim between the two: a ring on the far cell bars the fire just as a ring
    /// on this one does, and asking only the source would let the fire in through a
    /// side it cannot get out of.
    ///
    /// <see cref="WallProp.Bars"/> walks the blocks, so a leaf that has been driven
    /// through stops barring - which is the same gate a round gets, and the reason
    /// the fire does not need a rule of its own about rubble.</summary>
    private bool Walled(Vector2I from, Vector2I to)
    {
        foreach (WallProp prop in _bricks.Walls)
        {
            if (prop.Cell == from && prop.Bars(_field.FlatAnchor(to)
                                               - _field.FlatAnchor(from)))
                return true;
            if (prop.Cell == to && prop.Bars(_field.FlatAnchor(from)
                                             - _field.FlatAnchor(to)))
                return true;
        }
        return false;
    }

    /// <summary>Hand the tick this frame's world - the harness's Bind, with
    /// the hooks this bench answers. The gunnery hooks (<c>Aim</c>,
    /// <c>Launch</c>) stay unanswered on purpose: a shot here is an event with
    /// its outcome decided, and <see cref="Playback.Shot"/> hands that answer
    /// to <see cref="TankTick.Fire"/> per call.</summary>
    private void Bind()
    {
        _tick.Field = _field;
        _tick.Origin = _origin;
        _tick.Terrain = _terrain;
        _tick.Marks = _marks;
        _tick.Wood = _grove;
        _tick.Shake = _shake;
        _tick.Vehicles = _vehicles;
        _tick.Driven = _vehicles.Count > 0 ? Actor : null;
        _tick.ViewZoom = _camera?.Zoom.X ?? 1.0f;
        _tick.Staged = true;
        _tick.Deck = this;
        // A round that went into the board: the ground where it landed, or the
        // masonry that stopped it - one hook that does both, WallField.Landing,
        // which is the tank bench's arrangement and for its reasons. Before this
        // the shell went through standing brick and opened a crater behind it:
        // the debt named in docs/effects-plan.md under T7/T8.
        _tick.Landed = _bricks.Landing;
        // What stops a round, asked per cell and per direction: masonry stands
        // on a rim, so it is never a cell in Obstacles - TankTick.Barred's own
        // sentence. Null with no walls, and then the round goes into the field
        // exactly as it did.
        _tick.Barred = _bricks.Walls.Count > 0 ? _bricks.Barring : null;
        _tick.Shoving = _bricks.Walls.Count > 0 ? _bricks.Pressing : null;
        // A hull dropping into the pond throws its bow wave and its fans, heavier
        // by class - see TankTick.Plunged and Plunge; the look is the panel's.
        _tick.Plunged = (v, spot, top, might) =>
        {
            if (_stage is null || v.Atlas is null)
                return;
            _stage.Plunge(spot, top, Plunge.Heading(v),
                          v.Atlas.HullSpan * v.Sprite.BodyScale * 0.5f, might, _splash);
        };
        _tick.Kicked = (v, along) => _stage?.Kick(
            v.GroundPoint, v.LiftOf(v.GroundPoint), along,
            v.Spot(v.Bore(v.Sprite.TurretFacing).Tube) - v.GroundPoint,
            Ordnance.At(_tick.Calibre));
        // Two hulls meeting, and a hull coming down off a bank - one cloud, the
        // gun's without its muzzle offset. See TankTick.Bumped.
        _tick.Bumped = (v, spot, along) => _stage?.Kick(
            spot, v.LiftOf(spot), along, Vector2.Zero,
            TankTick.RamKick, Stage3D.DressOrder);
        // And the metal of that same collision: the ricochet's fan with no round
        // in it, one per hull and seated on the hull - see TankTick.Sparked.
        _tick.Sparked = (v, plate, outward, behind, might) => _stage?.Scrape(
            v.GroundPoint, v.LiftOf(v.GroundPoint), outward,
            v.Spot(plate) - v.GroundPoint, behind, might);
        _tick.Bounced = (v, plate, away, behind) => _stage?.Spall(
            v.GroundPoint, v.LiftOf(v.GroundPoint), away,
            v.Spot(plate) - v.GroundPoint, behind, Ordnance.At(_tick.Calibre));
        _tick.Blasted = (v, plate, outward, behind) => _stage?.Slam(
            v.GroundPoint, v.LiftOf(v.GroundPoint), outward,
            v.Spot(plate) - v.GroundPoint, behind, Ordnance.At(_tick.Calibre));
        _tick.Detonated = (v, deck, might) => _stage?.Detonate(
            v.GroundPoint, v.LiftOf(v.GroundPoint), deck, might);
        _tick.Flashed = (v, deck, might) => _stage?.Flash(
            v.GroundPoint, v.LiftOf(v.GroundPoint), deck, might);
        // A mine: a burst on the ground at the point it was buried, seated there
        // and not on the tank's foot - see Stage3D.Mine, where handing the offset
        // over the way the three hooks above hand a plate's is the mistake that
        // note is about. The lift is the ground's under the tank, because the
        // charge is on the cell the tank is on and that is the height it stands
        // at.
        _tick.Mined = (v, at, away, might) => _stage?.Mine(
            at, v.LiftOf(v.GroundPoint), away, might, Mines.Ahead(v, at));
        // A crown reaching the ground under the heavy: the same cloud again, at
        // the crown's own point and once per trunk - see Grove.Thud for why the
        // wood raises it and not the tick. The lift is the cell's, because what
        // is landing is lying on it.
        _grove.Thud = (cell, at, along) => _stage?.Kick(
            _origin + at, _field.LevelAt(cell) * _field.Lift, along,
            Vector2.Zero, TankTick.FellKick, Stage3D.DressOrder,
            // Earth, not propellant: nobody lit this wood.
            ProcKick.Cloud.Ground);
    }

    // --- the camera ----------------------------------------------------------

    /// <summary>Open on what the set is about: the tanks for <c>tank</c> and
    /// <c>overlay</c>, the wood, the brick and the ponds for <c>field</c>.
    /// Fitted to the board when nobody said a zoom.</summary>
    private void Home()
    {
        Rect2 board = Spread();
        float zoom = ZoomAt ?? Fit(board);
        _camera.Zoom = new Vector2(zoom, zoom);
        float hidden = NoUi ? 0.0f : ControlPanel.Width;
        // Fitted, the whole board; asked for a zoom, the thing the set is
        // about - the cell for the field, the actor otherwise - because a zoom
        // on the middle of the board is a zoom on whatever happens to stand
        // there, and the event is somewhere else.
        // For the tank sets, on the target: it is the one things happen to -
        // shot, knocked out, set alight - and the shooter stands off two cells
        // before it fires, so a zoom on the actor, or on the midpoint, looked
        // at the ground between two tanks or at neither of them.
        Vector2 centre = ZoomAt is null ? board.GetCenter()
            : Set == "field" ? _origin + _field.CellCentre(_cell)
                               - new Vector2(0.0f, _field.TopAt(_cell))
            : _vehicles.Count > 0 ? Target.Sprite.Position : board.GetCenter();
        _camera.Position = centre + new Vector2(hidden * 0.5f / zoom, 0.0f);
        _camera.Offset = Vector2.Zero;
    }

    private Rect2 Spread()
    {
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
        Vector2 tile = _field.Atlas?.HexRect.Size ?? new Vector2(248.0f, 109.0f);
        return new Rect2(low - tile * 0.5f, high - low + tile);
    }

    private float Fit(Rect2 board)
    {
        Vector2 window = GetViewportRect().Size;
        float free = Mathf.Max(window.X - (NoUi ? 0.0f : ControlPanel.Width),
                               200.0f);
        return Mathf.Clamp(Mathf.Min(free * 0.9f / Mathf.Max(board.Size.X, 1.0f),
                                     window.Y * 0.9f / Mathf.Max(board.Size.Y, 1.0f)),
                           0.4f, 4.0f);
    }

    // --- the frame -----------------------------------------------------------

    public override void _Process(double delta)
    {
        if (_vehicles.Count == 0)
            return;
        delta = (FrameClock.FixedStep ?? delta) * _speed;
        _clock += delta;

        // What --play asked for, once, on its frame - after the board is up and
        // before this frame's tick, so the frame the button lands on is the frame
        // the event starts.
        if (!_played && _frame + 1 >= _playAt)
        {
            _played = true;
            foreach (string id in _queued)
                if (!Press(id))
                    GD.PushWarning($"events: --play names no button '{id}'; "
                                   + "there are " + string.Join(" ", ButtonIds));
            foreach (Vector2I onto in _drives)
                _play?.Drive(Target, onto);
            // The first step may have moved a tank - a shooter stands off before
            // it fires - so the camera is homed again after it, on the frame
            // the event starts and not the frame the board opened.
            _play?.Advance(delta);
            Home();
        }
        else
            _play?.Advance(delta);
        foreach (Vehicle vehicle in _vehicles)
            Tick.Run(vehicle, delta);
        // Right after they have moved and before anything reads the board - the
        // harness's order and its reason: a ram is a fact about where a hull got
        // to this frame. See TankTick.RamContacts.
        Tick.RamContacts();
        Tick.Fly(delta);
        // And after both, the tank bench's order and for its reasons: the box
        // the wall feels is where the tank got to this frame, a round that
        // landed this frame has already been offered to the masonry by Fly, and
        // a leaf is taken off its edge by falling rather than by being let go of.
        _bricks.Watch();
        // The wood, in the harness's order and for its reasons (Main._Process):
        // which cells the hulls cover, so the undergrowth there goes under them
        // and ghosts - without this a bush whose foot is nearer the camera than
        // a parked tank's draws over its hull, which is how the first cut of
        // this bench looked; the hulls shouldering it aside; then the fire's own
        // clock - flames age and go out, spread left to events (Wildfire.Spreads)
        // - the wood reading the fire back on to its trees, and the wind.
        _grove.Reveal(Fleet.Standing(_vehicles, _field, _grove, _origin), delta,
                      Fleet.Razing(_vehicles),
                      Fleet.Treading(_vehicles, _origin));
        Fleet.Shoulder(_vehicles, _grove, _origin, delta);
        _fire.Tick(delta);
        _grove.Smoulder();
        _grove.Blow(delta);
        Todos();

        // The spring runs whenever anything can feed it - the gun's shake or the
        // death's, each behind its own switch at the source - and is put down only
        // when both are off, so a switch flipped mid-ring does not park the view
        // a few pixels out. See TankTick.Shook, which is the list of them.
        if (_tick.Shaking)
            _shake.Update(delta);
        else if (_shake.Moving)
            _shake.Reset();
        Vector2 want = _tick.Shaking
            ? _shake.ScreenOffset(_camera.Zoom.X) : Vector2.Zero;
        if (_camera.Offset != want)
            _camera.Offset = want;

        if (_stage is not null)
        {
            // The three rows every event reads, said on the ground in the three
            // colours rather than only in the panel's text: who acts, who is
            // acted on, and which hex it happens on. The panel already had all
            // three and the board had one of them, so a run set up from the
            // flags looked like a board with one tank selected and no way to
            // tell which of the other four the button was about.
            //
            // Under NoUi with the selection, and for its reason: a capture is
            // evidence and an A/B of two renders must not differ by a marker.
            bool mark = !NoUi && _vehicles.Count > 0;
            _stage.Selected = mark ? Actor : null;
            _stage.Quarry = mark ? Target : null;
            _stage.Chosen = NoUi ? null : _cell;
            if (_sea is not null)
            {
                for (int i = 0; i < _vehicles.Count; i++)
                {
                    Vehicle v = _vehicles[i];
                    Vector2I wet = _field.CellAt(v.GroundPoint - _origin);
                    float sea = _field.WaterTop(wet);
                    _sea.Note(i, wet, v.Speed > Swell.StirAbove, v.GroundPoint, sea);
                }
                _sea.Tick(delta);
                _wash?.Tick(delta);
            }
            _stage.Place(_vehicles);
        }

        if (_hud is not null)
            _hud.Text = Note();

        _frame++;
        if (CapturePath is not null && _frame >= CaptureAt)
        {
            Capture(CapturePath);
            GetTree().Quit();
        }
    }

    /// <summary>What every tank is doing, in one line each - the same figures
    /// <c>bench.state</c> prints on the panel.</summary>
    private string Note()
    {
        var lines = new List<string> { $"events: {Set}   cell ({_cell.X},{_cell.Y})" };
        for (int i = 0; i < _vehicles.Count; i++)
        {
            Vehicle v = _vehicles[i];
            lines.Add($"{(i == _actor ? ">" : " ")}{i} {v.Tag} ({v.Cell.X},{v.Cell.Y})"
                      + $" hull {v.Sprite.HullFacing:F0} turret {v.Sprite.TurretFacing:F0}"
                      + (v.Wreck.Dead ? " DEAD" : "")
                      + (v.Burning ? " burning" : "")
                      // Afloat before wading, because a tank in deep water is
                      // wading too and only the first of the two is a state the
                      // rules have a name for.
                      + (_field.IsDeep(v.Cell) ? " afloat"
                         : v.Wading ? " wading" : "")
                      + (i == _target ? "  <target" : ""));
        }
        return string.Join("\n", lines);
    }

    /// <summary>Put the board back as it opened: every tank made good and
    /// parked at home, rounds cleared, craters filled, the water settled.</summary>
    /// <summary>Point the target's hull and turret where the heading row says.
    /// After every park and restore, because both put the hull back on 270.</summary>
    private void Face()
    {
        if (_vehicles.Count == 0)
            return;
        TankSprite s = Target.Sprite;
        s.HullFacing = _heading;
        s.TurretFacing = _heading;
        s.QueueRedraw();
    }

    private void Reset()
    {
        _play?.Clear();
        foreach ((Label tag, _, _, _) in _todo)
            tag.QueueFree();
        _todo.Clear();
        Tick.ClearRounds();
        _marks?.Clear();
        _stage?.Quench();
        _pits.Fill();
        // The board's own covers back as the map laid them - a mine that has
        // gone off is spent, and R is what puts the scene back so it can be
        // played again. The fire has its own restore below and writes the same
        // states; this one is what nothing else owns.
        _field.SetCover(_map.Over);
        _fire.Douse();
        // And the wood a bulldozer took down comes back standing - after the
        // covers, which is what says it may grow there again. See Grove.Regrow.
        _grove.Regrow();
        foreach (Vehicle vehicle in _vehicles)
        {
            Fleet.Restore(vehicle, Tick);
            vehicle.Cell = vehicle.HomeCell;
            Tick.Park(vehicle);
        }
        Face();
        // <b>The camera is not reset, and that is the point of the gesture.</b>
        // R puts the scene back so the same thing can be watched again - and
        // watching it again means from where it was being watched, at the zoom it
        // was being watched at. Homing the view made every replay start by
        // finding the tank a second time. What is cleared here is the shake's own
        // displacement, which is not a view the user chose: a held offset with
        // nothing driving it is the spring's version of the same fault.
        _shake.Reset();
        _sea?.Settle();
        _wash?.Settle();
    }

    // --- what the board cannot show yet -------------------------------------

    /// <summary>Say it on the board: a label over the tank or the cell, alive
    /// for a few seconds of playback. Under <c>--no-ui</c> the label still
    /// exists - a capture is where the TODO list is read.</summary>
    private void Todo(string text, Vehicle? over, Vector2I? at)
    {
        if (_layer is null)
            return;
        var tag = new Label { Text = text };
        tag.AddThemeColorOverride("font_color", new Color(1.0f, 0.85f, 0.35f));
        tag.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.9f));
        tag.AddThemeConstantOverride("outline_size", 6);
        _layer.AddChild(tag);
        _todo.Add((tag, over, at, _clock + 4.0));
        GD.Print($"events: {text}");
    }

    /// <summary>Keep every TODO over what it is about, and take the old ones
    /// down. Screen position through the canvas transform, which is the camera
    /// - the same mapping a click goes through the other way.</summary>
    private void Todos()
    {
        if (_todo.Count == 0)
            return;
        Transform2D view = GetViewport().GetCanvasTransform();
        for (int i = _todo.Count - 1; i >= 0; i--)
        {
            (Label tag, Vehicle? over, Vector2I? at, double until) = _todo[i];
            if (_clock >= until)
            {
                tag.QueueFree();
                _todo.RemoveAt(i);
                continue;
            }
            Vector2 world = over is not null
                ? over.Sprite.Position - new Vector2(0.0f, 110.0f * over.Sprite.BodyScale)
                : at is Vector2I cell
                    ? _origin + _field.CellCentre(cell) - new Vector2(0.0f, 60.0f)
                    : Vector2.Zero;
            Vector2 screen = view * world;
            tag.Position = screen - new Vector2(tag.Size.X * 0.5f, 0.0f);
        }
    }

    // --- input ---------------------------------------------------------------

    private Vector2I? Pointed()
    {
        Vector2I cell = _field.CellAt(_field.ToLocal(GetGlobalMousePosition()));
        return _field.InBounds(cell) ? cell : null;
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Middle } middle)
        {
            // Drag pans, tap picks the cell - SceneRoot.MiddleTapped.
            if (MiddleTapped(middle, out Vector2 _) && Pointed() is Vector2I cell)
                _cell = cell;
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
                _actor = Math.Clamp((int)(key.Keycode - Key.Key1), 0,
                                    _vehicles.Count - 1);
                break;
            case Key.Tab:
                _panel?.Flip();
                break;
            case Key.R:
                Reset();
                break;
            case Key.F12:
                Capture(ProjectSettings.GlobalizePath(
                    $"res://out/events_{Set}_{Time.GetTicksMsec()}.png"));
                break;
        }
    }
}
