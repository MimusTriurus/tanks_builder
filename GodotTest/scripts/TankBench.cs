using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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
/// <b>And the third thing on the board is a wall, on the board that carries
/// one.</b> The subject of this bench is no longer quite "one tank": it is one
/// tank and the thing it can drive into. What that costs is the two hundred odd
/// lines below that know a prop exists - which cells stop a shell, which prop a
/// landed round belongs to, and the box the hull pushes with. What it does not
/// cost is a second wall: the plan, the fit, the rubble, the drawing and the
/// solver are <see cref="WallBench"/>'s, taken whole through
/// <see cref="WallProp"/>. A bench that laid its own masonry would be a bench
/// measuring itself, which is the rule this file is written under twice already.
///
/// <b>Three boards now, and the third is the same bench with one string
/// changed.</b> <c>TankTest.tscn</c> stands the tank on
/// <see cref="BoardMap.Test"/> - the level, one grade and one ford, which is
/// where every dial here was tuned. <c>WaterTankTest.tscn</c> stands it on
/// <see cref="BoardMap.WaterMap"/>, open water with a rosette of land in the
/// middle: there the water is the board, so every one of the six ways off the
/// middle is a wade and the descent can be judged on whichever bearing reads
/// best. <c>WallTankTest.tscn</c> stands it on <see cref="BoardMap.WallMap"/>,
/// a hexagon of level ground three cells across with one brick wall in the
/// middle of it: a ram, an HE round and an AP round, which are the three things
/// that break masonry and none of which a tank could do before. Nothing in this
/// file knows which of the three it is on - see <see cref="Map"/>.
///
/// Run it with the scene as the positional argument, leaving project.godot
/// alone:
///
///     Godot_v4.7.2-stable_mono_win64.exe --path GodotTest res://TankTest.tscn
///     Godot_v4.7.2-stable_mono_win64.exe --path GodotTest res://WaterTankTest.tscn
///     Godot_v4.7.2-stable_mono_win64.exe --path GodotTest res://WallTankTest.tscn
/// </summary>
public sealed partial class TankBench : SceneRoot
{
	/// <summary>Forty frames, because half of what this bench shows is a tank
	/// that has been driving for a while.</summary>
	public TankBench() : base(40) { }

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

    // --- the wall ------------------------------------------------------------

    /// <summary>
    /// One prop per cell the map marks with <see cref="BoardMap.Wall"/>, and
    /// nothing at all on a board that marks none.
    ///
    /// <b>Off the map rather than out of the scene</b> - <see cref="BoardMap.Wall"/>
    /// carries the reason. What is here is the wiring: which cells stop a shell,
    /// which prop a landed round belongs to, and the box the tank pushes with.
    ///
    /// <b>A recipe apiece, because the board carries several.</b> Two props off
    /// one recipe are two identical walls - the seed is in it - so the ring gets
    /// <see cref="_brick"/>, which is what the panel's dials write into, and every
    /// sample gets its own out of <see cref="Samples"/>. That is also why
    /// <see cref="Relaid"/> stands each prop up on its own bearing and coverage
    /// instead of pushing the dials' pair into all of them.
    /// </summary>
    private readonly List<WallProp> _walls = new();
    private readonly HashSet<Vector2I> _walled = new();

    /// <summary>The ring's own recipe: the one the dials drive. Six sides,
    /// because the board's subject is a tank inside a walled yard - see
    /// <see cref="BoardMap.WallMap"/> - and a yard with one face is a fence.
    /// </summary>
    private readonly WallKit.Recipe _brick = new() { Sides = RingSides };

    /// <summary>How far round its cell the ring goes. Named because the board is
    /// authored around it: the tank's home is the ring's cell, so a ring that
    /// stopped covering every side would leave the machine standing in a gap
    /// nobody chose - and no number in any readout would say which side it was.
    /// </summary>
    public const int RingSides = 6;

    /// <summary>What <c>walls.json</c> calls the ring. A constant rather than a
    /// literal at the one call site, because the self-test holds the file against
    /// this list and a name spelt twice is a name that can be spelt two ways.
    /// </summary>
    public const string RingName = "ring";

    /// <summary>Every wall this bench lays, by name: the table
    /// <see cref="WallConfig"/> fills in. Declared here rather than in the file
    /// for that class's reason - which walls exist is a cell, a bearing and a row
    /// of dials, none of which a file can supply.</summary>
    public static IEnumerable<string> WallNames
    {
        get
        {
            yield return RingName;
            foreach ((Vector2I _, float _, string name, WallKit.Recipe _) in Samples)
                yield return name;
        }
    }

    /// <summary>
    /// The walls that are not the ring: where each stands, which way it faces and
    /// what it is made of.
    ///
    /// <b>Here rather than in <see cref="BoardMap"/>, for the same reason a wood
    /// is.</b> The map says which cells carry a wall; what a wall <i>is</i> - how
    /// high, how thick, which seed - is the bench's, exactly as the map says a
    /// wood grows on a cell and <see cref="Grove"/> decides which trees stand on
    /// it. A recipe in the map would also be a recipe no dial can reach.
    ///
    /// <b>One thing at a time, and that is the whole point of four of them.</b>
    /// Height, thickness, seed and run are four dials whose effect is hard to
    /// judge one wall at a time - a taller wall looks like a nearer wall, a
    /// thicker one like a longer one - so each sample moves one of them off the
    /// ring's own numbers and the rest stay put.
    ///
    /// <b>Each faces the ring.</b> The bearing is the side the wall stands on and
    /// the side a shot arrives from, one dial for both, so a tank driving out of
    /// the ring along that lane meets the masonry square on rather than at its
    /// end - which is the difference between a ram and a graze, and it is
    /// measured: a shot off the axis moves a tenth of the pieces.
    ///
    /// <b>The name is the id <c>walls.json</c> speaks to and the word the readout
    /// prints, and it is one name on purpose.</b> A label beside an id is two
    /// names for one wall, and they part company on the line nobody re-reads.
    /// </summary>
    private static readonly (Vector2I Cell, float Bearing, string Name,
                             WallKit.Recipe Recipe)[] Samples =
    {
        (new Vector2I(3, 0), 270.0f, "taller",
         new WallKit.Recipe { Courses = 14 }),
        (new Vector2I(0, 2), 330.0f, "thicker",
         new WallKit.Recipe { Leaves = 4 }),
        (new Vector2I(6, 2), 210.0f, "reseeded",
         new WallKit.Recipe { Seed = 47 }),
        (new Vector2I(3, 6), 90.0f, "longer",
         new WallKit.Recipe { Sides = 3, Seed = 23 }),
    };

    private WallProp? Wall => _walls.Count > 0 ? _walls[0] : null;

    /// <summary>The class the bench opened on, so R can go back to it. See
    /// <see cref="Reset"/>.</summary>
    private int _opened;

    /// <summary>Which flat side the wall stands on, as an index into
    /// <see cref="HexField.EdgeHeadings"/>. One dial for both halves of it -
    /// <see cref="WallBench"/>'s argument, and here it decides the run-up too.
    /// Opens on 270, so the wall faces the camera and the tank comes at it from
    /// the near side.</summary>
    private int _wallSide = Array.IndexOf(HexField.EdgeHeadings, 270);

    /// <summary>A multiplier over what the round already carries. The calibre is
    /// the shot's own size - an ordered three - and a second table beside it
    /// would be a second thing to keep in step, so this only scales it.</summary>
    private double _wallForce = 1.0;

    /// <summary>Whether the rig draws its own line into the wall on top of the
    /// shell's tracer. Off: the round has been drawn all the way in already, and
    /// a second line over it is a second answer to where it came from. Kept as a
    /// switch because that claim is worth being able to look at.</summary>
    private bool _wallBeam;

    /// <summary>Whether masonry slows the tank down - see
    /// <see cref="TankTick.Shoving"/>.
    ///
    /// On, because a tank that goes through a wall at cruise is the picture this
    /// replaces. A switch all the same, and for the reason every effect on this
    /// board has one: a shot is proof, and taking the pair of it must not need a
    /// hand on the keyboard. What it turns off is only the ceiling - the box, the
    /// sweep and the collapse are untouched, so the A/B is the going and nothing
    /// else.</summary>
    private bool _shove = true;

    /// <summary>Whether the order under way is a ram - see
    /// <see cref="Sweeps"/>.
    ///
    /// <b>Held on the bench rather than read off the tank, because it is a fact
    /// about the order and the tank has no field for one.</b> A path is a path;
    /// what makes this one a ram is that <c>M</c> asked for it, and the only
    /// place that knows is where the key is handled. Cleared inside
    /// <see cref="OrderTo"/> and raised only by the two calls <see cref="Ram"/>
    /// makes, so the ordinary way to order is also the way to clear it: a flag
    /// whose clearing has to be remembered at every call site is a flag that
    /// eventually is not.</summary>
    private bool _ramming;

    /// <summary>Whether ordinary driving breaks masonry, the way it used to.
    /// Off - <c>--drive-rams</c>, the A/B the gate is judged by, and the only
    /// honest base picture: it puts back the wall that fell to any order, so
    /// what the pair measures is the gate and nothing else.</summary>
    private bool _driveRams;

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

    private double _recoilLevel = 1.0;
    private bool _shadow = true;
    private bool _ruts = true;
    private bool _spinning;
    private double _sizeLevel = 1.0;
    private double _traverse = 1.0;
    private bool _edges = true;
    private bool _board = true;

    /// <summary>The bench panel's own settings file, beside the project - the
    /// harness's <c>panel.json</c> arrangement on this panel's rows. One file
    /// for all three bench scenes, because they share one panel: what differs
    /// between the boards is the ground, not the dials, and a file per scene
    /// would be three copies of one row list disagreeing after the first edit
    /// in any of them.</summary>
    public const string PanelFile = "bench.json";

    /// <summary>Captions, tooltips, ranges and opening values off
    /// <see cref="PanelFile"/>. Held on the bench rather than only on the
    /// panel because --no-ui frees the panel, and the defaults still have to
    /// be applied and the trace still has to say whether the file was read -
    /// a settings file that quietly did not load looks exactly like one whose
    /// settings did nothing.</summary>
    private PanelText _benchText = new();

    /// <summary>
    /// Which panel row each start-up flag speaks for - the harness's
    /// <c>Main.FlagRows</c> on this bench's flags, and for its reason: the
    /// list is only correct when it can be read at once. A flag missing from
    /// it still works and is then quietly overruled by <see cref="PanelFile"/>,
    /// which from outside looks exactly like the flag not arriving.
    ///
    /// The wall-shape trio (--sides, --courses, --leaves) is here even though
    /// the shipped file carries no default for those rows - walls.json owns
    /// that opening shape, and two files answering one default is what the
    /// ui doc forbids twice. The claim protects the flag from a default a
    /// user adds anyway, which nothing can stop and the row's description
    /// says so.
    /// </summary>
    private static readonly Dictionary<string, string[]> FlagRows = new()
    {
        ["--tank"] = new[] { "tank.pick.class" },
        ["--size"] = new[] { "tank.pick.size" },
        ["--turret"] = new[] { "tank.aim.turret" },
        ["--scan"] = new[] { "tank.aim.scan" },
        ["--no-tremble"] = new[] { "tank.ride.tremble" },
        ["--tremble"] = new[] { "tank.ride.tremble_level" },
        ["--rumble"] = new[] { "tank.ride.rumble" },
        ["--no-tracks"] = new[] { "tank.ride.tracks" },
        ["--no-exhaust"] = new[] { "tank.smoke.on" },
        ["--exhaust"] = new[] { "tank.smoke.level" },
        ["--burning"] = new[] { "tank.smoke.burn" },
        ["--hit"] = new[] { "tank.armour.side" },
        ["--hit-scale"] = new[] { "tank.armour.calibre" },
        ["--no-shadow"] = new[] { "tank.armour.shadow" },
        ["--strike"] = new[] { "wall.ammo" },
        ["--ammo"] = new[] { "wall.ammo" },
        ["--force"] = new[] { "wall.force" },
        ["--bearing"] = new[] { "wall.side" },
        ["--sides"] = new[] { "wall.sides" },
        ["--courses"] = new[] { "wall.courses" },
        ["--leaves"] = new[] { "wall.leaves" },
        ["--wall-beam"] = new[] { "wall.beam" },
        ["--no-shove"] = new[] { "wall.shove" },
        ["--drive-rams"] = new[] { "wall.gate" },
        ["--no-shunt"] = new[] { "wall.shunt" },
        ["--no-breach"] = new[] { "wall.breach" },
        ["--no-crumble"] = new[] { "wall.crumble" },
        ["--hulls"] = new[] { "wall.hulls" },
        ["--no-ripples"] = new[] { "tank.view.ripples" },
        ["--ripple"] = new[] { "tank.view.ripples", "tank.view.shove" },
    };

    /// <summary>Rows a flag on this run's command line has claimed, so the
    /// file's opening values keep off them - the conventional order: built-in
    /// default, then file, then flag.</summary>
    private readonly HashSet<string> _flagged = new();

    /// <summary>Which side the next hand-dealt hit comes from, and the heading it
    /// names. On the tick, with the calibre - see
    /// <see cref="TankTick.HitSide"/>.</summary>
    private int _hitSide
    {
        get => _tick.HitSide;
        set => _tick.HitSide = value;
    }

    private double HitFrom => _tick.HitFrom;

    /// <summary>The board's own corner, the same one the harness parks its grid
    /// at. Any value would do on a bench of fifteen cells; this one keeps the
    /// two boards' numbers comparable when a figure is read off both.</summary>
    private readonly Vector2 _origin = new(220, 200);

    // --- flags ---------------------------------------------------------------

    private int _frame;

    /// <summary>What the view opens at, or null for "measure it" - see
    /// <see cref="FitZoom"/>. Null rather than a number, because the board is
    /// fifteen cells and the panel eats three hundred pixels of the window: a
    /// tuned figure would be right for this board at this window size and wrong
    /// for the next cell added to either.</summary>
    private string? _startTag;
    /// <summary>The legs of a queued <c>--drive</c>, in order, and how many of
    /// them have been ordered. A list rather than one cell because the flag is
    /// how a run is made to repeat: out of the ring, back into it and away again
    /// is three orders, and until there were three the only thing measurable from
    /// the command line was a board nobody had driven on yet - which is exactly
    /// the state the wall's opening frame is already in. One leg per occurrence
    /// of the flag, and the next is ordered when the one before it arrives.
    ///
    /// Each leg carries whether it is a ram, because <c>--charge</c> shares the
    /// queue: what a right click on the board says is "this same drive, as a
    /// ram", so the two flags have to be able to say it in one run and in
    /// order.</summary>
    private readonly List<(Vector2I Cell, bool Ram)> _driveTo = new();
    private int _driveLeg;
    private bool _fireAtStart;
    private double? _hitAtStart;
    private int _damageAtStart;
    private bool _destroyAtStart;
    private bool _burnAtStart;
    private bool _ramAtStart;
    private double? _turretAtStart;

    /// <summary>How much of the cell the wall takes. The wall bench's dial, and
    /// its number: 0.97 leaves about two pixels of tile between each end of the
    /// wall and the cell's vertex.</summary>
    private float _wallCoverage = 0.97f;

    /// <summary>How fast the tank was going, in metres a second, when the wall
    /// first let go of a piece - negative until it has.
    ///
    /// <b>Reported, because it is neither of the two numbers anybody would
    /// guess.</b> The medium's cruise is 240px/s, which at 30.5px to the metre is
    /// 7.9 m/s against the <see cref="WallRig.RamSpeed"/> of 3.9 the shot was
    /// tuned at - but a tank braking to stop on the wall's cell meets the masonry
    /// an apothem short of where it is aiming to stand, so what it actually
    /// arrives at is neither. Without this figure "the wall went off like a break
    /// shot" cannot be told from "the wall is tuned wrong".</summary>
    private double _ramSpeed = -1.0;

    private void ReadFlags()
    {
        // Before the switch, never after it: --seed and its four neighbours write
        // the same fields, and a flag is the more specific statement - see
        // WallConfig. Said out loud rather than left to load order, because a
        // flag the file quietly overrules reads exactly like a flag that did not
        // arrive.
        var walls = WallConfig.Load();
        if (walls.Loaded)
        {
            int spoke = walls.Apply(RingName, _brick) ? 1 : 0;
            foreach ((Vector2I _, float _, string name, WallKit.Recipe recipe)
                     in Samples)
                if (walls.Apply(name, recipe))
                    spoke++;
            GD.Print($"tank bench: walls json, {spoke} wall(s) named");
        }
        else
        {
            GD.Print($"tank bench: walls built-in ({walls.Error})");
        }
        string[] args = OS.GetCmdlineUserArgs();
        for (int i = 0; i < args.Length; i++)
        {
            // The five every root here takes - see SceneRoot.
            if (ReadCommonFlag(args, ref i))
                continue;
            // Claimed before parsed, so a branch that rejects its argument
            // still shields its row: a flag that was said and did not parse
            // should read as a flag that failed, never as the file winning.
            if (FlagRows.TryGetValue(args[i], out string[]? spoken))
                _flagged.UnionWith(spoken);
            switch (args[i])
            {
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
                    if (Spot(args[++i]) is Vector2I leg)
                        _driveTo.Add((leg, false));
                    break;
                // The same leg, driven as a ram - what a right click on the
                // target hex does. A flag of its own rather than a modifier on
                // --drive, so that a run can be half ordinary driving and half
                // ram: a snapshot is proof, and taking one must not need a hand
                // on the mouse.
                case "--charge" when i + 1 < args.Length:
                    if (Spot(args[++i]) is Vector2I rush)
                        _driveTo.Add((rush, true));
                    break;
                case "--fire":
                    _fireAtStart = true;
                    break;
                // Where the gun points to begin with. The harness has had this
                // for as long as it has had a turret; the bench needed it the
                // moment there was something on the board worth laying on,
                // because a shot at a wall cannot be captured with the gun
                // pointing the way the tank parked.
                case "--turret" when i + 1 < args.Length
                                     && Number(args[i + 1]) is double lay:
                    _turretAtStart = lay;
                    i++;
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
                // Which of the three the gun is loaded with, by the harness's
                // name for it and snapped through the harness's list: a gun has
                // the calibres it has, and a control that reads 1.0x with a 1.4
                // round loaded is a control lying about what it will fire. It
                // reaches the wall too - the round's calibre is what the burst
                // is scaled by - so this is how the shot's own size is swept.
                case "--hit-scale" when i + 1 < args.Length
                                        && Number(args[i + 1]) is double bore:
                    _tick.Calibre = Ordnance.For((float)bore);
                    i++;
                    break;
                case "--burning":
                    _burnAtStart = true;
                    break;
                case "--size" when i + 1 < args.Length && Number(args[i + 1]) is double s:
                    _sizeLevel = Mathf.Clamp(s, 0.5, 2.0);
                    i++;
                    break;
                case "--tremble" when i + 1 < args.Length && Number(args[i + 1]) is double t:
                    _tick.TrembleLevel = Mathf.Clamp(t, 0.0, 2.5);
                    i++;
                    break;
                case "--exhaust" when i + 1 < args.Length && Number(args[i + 1]) is double e:
                    _tick.ExhaustLevel = Mathf.Clamp(e, 0.0, 3.0);
                    i++;
                    break;
                case "--no-tremble":
                    _tick.TrembleEnabled = false;
                    break;
                case "--no-tracks":
                    _tick.TracksEnabled = false;
                    break;
                case "--no-exhaust":
                    _tick.ExhaustEnabled = false;
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
                    _tick.RumbleEnabled = true;
                    break;
                case "--scan":
                    _tick.ScanEnabled = true;
                    break;

                // --- the wall ---------------------------------------------
                //
                // What hits it at the start. A convenience over the two below:
                // a ram is an order and the other two are a round, so the flag
                // sets the ammunition and pulls the trigger.
                case "--strike" when i + 1 < args.Length:
                    switch (args[++i])
                    {
                        case "ram": _ramAtStart = true; break;
                        case "he": _tick.Ammo = Shell.Kind.He; _fireAtStart = true; break;
                        case "ap": _tick.Ammo = Shell.Kind.Ap; _fireAtStart = true; break;
                        default:
                            GD.PushWarning($"--strike {args[i]} is none of ram, "
                                           + "he, ap");
                            break;
                    }
                    break;
                case "--ram":
                    _ramAtStart = true;
                    break;
                // Ram every wall on the board in turn and print what each ram
                // did to the masonry it was not aimed at - see TankBench.Tour.
                // It ends the run itself, because the run is the measurement.
                case "--ram-tour":
                    _tour = true;
                    break;
                case "--ammo" when i + 1 < args.Length:
                    _tick.Ammo = args[++i] == "ap" ? Shell.Kind.Ap : Shell.Kind.He;
                    if (args[i] is not ("he" or "ap"))
                        GD.PushWarning($"--ammo {args[i]} is neither he nor ap");
                    break;
                case "--force" when i + 1 < args.Length
                                    && Number(args[i + 1]) is double f:
                    _wallForce = Mathf.Clamp(f, 0.0, WallBench.MostForce);
                    i++;
                    break;
                // Snapped, so the flag cannot quietly stand the wall through a
                // corner: the six are where a tank can be, and an angle between
                // two of them is not a weaker version of either.
                case "--bearing" when i + 1 < args.Length
                                      && Number(args[i + 1]) is double b:
                    _wallSide = Angles.SideFor(b);
                    i++;
                    break;
                case "--seed" when i + 1 < args.Length
                                   && int.TryParse(args[i + 1], out int seed):
                    _brick.Seed = seed;
                    i++;
                    break;
                case "--sides" when i + 1 < args.Length
                                    && int.TryParse(args[i + 1], out int runs):
                    _brick.Sides = Math.Clamp(runs, 1, 6);
                    i++;
                    break;
                case "--courses" when i + 1 < args.Length
                                      && int.TryParse(args[i + 1], out int high):
                    _brick.Courses = Math.Clamp(high, 1, WallBench.MostCourses);
                    i++;
                    break;
                case "--leaves" when i + 1 < args.Length
                                     && int.TryParse(args[i + 1], out int thick):
                    _brick.Leaves = Math.Clamp(thick, 1, WallBench.MostLeaves);
                    i++;
                    break;
                case "--coverage" when i + 1 < args.Length
                                       && Number(args[i + 1]) is double cov:
                    _wallCoverage = Mathf.Clamp((float)cov, 0.4f, 1.2f);
                    i++;
                    break;
                // The rig's own line over the shell's tracer. Off by default -
                // the round has been drawn all the way in already - and kept as a
                // flag because "the tracer is enough" is a claim until the two
                // can be put side by side.
                case "--wall-beam":
                    _wallBeam = true;
                    break;
                // Masonry costs nothing to drive through - the A/B the going is
                // judged by. Only the ceiling goes; the wall still comes down.
                case "--no-shove":
                    _shove = false;
                    break;
                // Any order breaks masonry, as it used to - the A/B the ram gate
                // is judged by. See Sweeps.
                case "--drive-rams":
                    _driveRams = true;
                    break;
                // The rubble stays where it landed - the A/B the clearing is
                // judged by, and the flag the wall bench carries under the same
                // name.
                case "--no-crumble":
                    WallRig.Crumbles = false;
                    break;
                // The solver's own boxes, drawn over the picture. Off by
                // default and a flag rather than a key for WallHulls' reason:
                // a debug frame that wandered into an A/B would be measuring
                // itself, and a capture is the proof, so putting one in must
                // not need a hand on the mouse.
                case "--hulls":
                    WallHulls.Shown = true;
                    break;
                // The ram lets masonry go and never pushes it, as it used to -
                // the A/B the shunt is judged by. See WallRig.RamCarry.
                case "--no-shunt":
                    WallRig.Shunts = false;
                    break;
                // The wall only loses what the strike itself reached, as it used
                // to - the A/B a breach is judged by. See WallRig.Breaches.
                case "--no-breach":
                    WallRig.Breaches = false;
                    break;
                // Every strike crosses sections again, as it used to - the A/B
                // a segment is judged by. Named for the blast because that was
                // the only field it cut when it was written; it cuts the band
                // and the cascade too now, and the name is the narrower of the
                // two. Kept rather than renamed: it is one switch on both
                // benches, and every command written down with it still has to
                // work. See WallRig.Segmented.
                case "--wide-blast":
                    WallRig.Segmented = false;
                    break;
                // Every piece takes the depth buffer, as it used to - the A/B
                // rubble giving that up is judged by. See WallStack.Dressed.
                case "--solid-rubble":
                    WallStack.Dressed = false;
                    break;
            }
        }
        // A capture is evidence, so the step is pinned for the harness's reason:
        // two runs of the same flags have to be diffable. Stage3D reads this very
        // field, which is why it is Main's and not a local one - see
        // FrameClock.FixedStep.
        //
        // A tour is evidence of the same kind and pins it for the same reason:
        // it is a measurement whose whole output is printed numbers, and one
        // taken on wall-clock frames is a measurement of the machine it ran on.
        SettleForProof(CapturePath is not null || _tour);
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
        // After the stage, because a prop wants the ground it stands on and the
        // triangles come off the stage - and after ApplySize, because the ram's
        // box is measured through the class scale.
        Walls();
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
        foreach (string tag in MovementProfile.Tags)
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
        // Held, because R means "the bench as it opened" and the class is part
        // of that. Read back rather than worked out a second time: the medium
        // fallback and the flag are one decision, and a second copy of it would
        // send a reset to a tank the flags never named.
        _opened = _pick;
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

    /// <summary>
    /// Stand a wall on every cell the map marks with one.
    ///
    /// <b>All of it is <see cref="WallProp"/>'s</b>, which is the point of that
    /// type existing: the plan, the fit, the rubble, the drawing and the solver
    /// are the wall bench's, dead literally, and what a second bench adds is a
    /// board to stand them on. What is written here is which cells, which side
    /// and which recipe.
    ///
    /// <b>Nothing is borrowed for the ram.</b> <see cref="WallStack.Tank"/> draws
    /// a tank out of an atlas because the wall bench has no
    /// <see cref="Vehicle"/>; this board has three, and a borrowed sprite would
    /// be a second tank standing where the real one is.
    /// </summary>
    private void Walls()
    {
        if (_stage is null || _field.Atlas is null)
            return;
        // The ring first, so it is _walls[0] - the one the dials drive, the one
        // the readout prints and the one the tank stands in. Everything that says
        // "the wall" on this bench means that one.
        Vector2I ring = _map.Homes.Count > 0 ? _map.Homes[0] : new Vector2I(-1, -1);
        foreach (Vector2I cell in _map.Walled())
        {
            if (cell != ring)
                continue;
            Stand(cell, HexField.EdgeHeadings[_wallSide], _wallCoverage, _brick);
            break;
        }
        foreach ((Vector2I cell, float bearing, string _, WallKit.Recipe recipe)
                 in Samples)
        {
            if (!_walled.Contains(cell) && _map.Walled().Contains(cell))
                Stand(cell, bearing, _wallCoverage, recipe);
        }
        // Any cell the map marked that no sample named. Stood rather than skipped:
        // a letter on the board with nothing on it is the one failure this whole
        // arrangement can have, and it looks exactly like open ground.
        foreach (Vector2I cell in _map.Walled())
            if (!_walled.Contains(cell))
                Stand(cell, HexField.EdgeHeadings[_wallSide], _wallCoverage,
                      new WallKit.Recipe());
        if (_walls.Count > 0)
        {
            // What the yard leaves against what stands in it. Both numbers,
            // because either alone says nothing: a six-sided ring on the tank's
            // own cell is 2.96m clear and a medium hull turns through 3.26m, so
            // the machine does not fit and clips brick at every heading. That is
            // the board as asked for, and it is only harmless because the ram
            // lets go of what it has driven into rather than what it stands in -
            // see WallRig.Drive. Named here so that a wall that starts eating
            // tanks again is one printed line away from being explained.
            float clear = Wall?.Clearance() ?? 0.0f;
            Vector3 box = _garage.Count > 0
                ? WallProp.Box(Tank, _field.Atlas.HexRect.Size.X * 0.5f)
                : Vector3.Zero;
            float corner = 0.5f * new Vector2(box.X, box.Z).Length();
            GD.Print($"tank bench: {_walls.Count} wall(s), ring on "
                     + $"({ring.X},{ring.Y}) over {_brick.Sides} sides, "
                     + $"{clear:F2}m clear against a {corner:F2}m hull corner, "
                     + "samples "
                     + string.Join(" ", Samples.Select(
                         x => $"({x.Cell.X},{x.Cell.Y}) {x.Name}")));
        }
    }

    /// <summary>One wall on one cell. Every field a prop needs is handed in, so
    /// which walls a board has is a list rather than a special case per
    /// wall.</summary>
    private void Stand(Vector2I cell, float bearing, float coverage,
                       WallKit.Recipe recipe)
    {
        var prop = new WallProp
        {
            Field = _field,
            Stage = _stage!,
            Cell = cell,
            Recipe = recipe,
            Borrow = null,
            // Its own collision bit, because every rig is centred on the world
            // origin: on one bit the ring's rubble and a sample's lie in the
            // same phantom space, and ramming the sample scatters the ring's
            // heap - see WallRig.Channel.
            Channel = _walls.Count,
        };
        AddChild(prop);
        prop.Bearing = bearing;
        prop.Coverage = coverage;
        prop.Build();
        _walls.Add(prop);
        _walled.Add(cell);
    }

    /// <summary>Lay them all again - what a size dial, a seed or a side does.
    /// Locked while anything is in the air, for the wall bench's reason twice
    /// over: this replaces the bodies rather than turning the world under
    /// them.</summary>
    private void Relay(Action change)
    {
        if (Wall?.Rig is { Struck: true })
        {
            // Refused out loud, once per wall: a dial that is silently held
            // reads as a dial that does nothing - measured as "the wall
            // changes with the direction I ram", because the turns of this
            // dial after the first strike were going nowhere.
            if (!_lockSaid)
                GD.Print("wall dials are locked while the wall lies - "
                         + "R lays a new one");
            _lockSaid = true;
            return;
        }
        change();
        Relaid();
    }

    /// <summary>Whether the locked-dials line has been printed for this wall.
    /// Once per wall, not per turn of the dial: a slider dragged against the
    /// lock is one refusal, not forty.</summary>
    private bool _lockSaid;

    /// <summary>Stand every wall up again, whatever state the last one was left
    /// in. The half of <see cref="Relay"/> that has no guard on it, because
    /// <see cref="Reset"/> is the one caller that must work on rubble - a struck
    /// wall has no undo, so putting it back is building another one.</summary>
    private void Relaid()
    {
        _ramSpeed = -1.0;
        _wallSaid = false;
        _lockSaid = false;
        // The box is the old rig's body, and the old rigs are about to be
        // freed with their walls; forgetting the prop here is what keeps
        // Rebox from dismounting a rig that no longer exists.
        _boxed = null;
        // The dials are the ring's: which side it stands on and how much of the
        // cell it takes. A sample keeps the bearing it was stood on, because that
        // bearing is what makes it face the ring - see Samples.
        if (Wall is WallProp ring)
        {
            ring.Bearing = HexField.EdgeHeadings[_wallSide];
            ring.Coverage = _wallCoverage;
        }
        foreach (WallProp prop in _walls)
            prop.Build();
    }

    /// <summary>
    /// A round that went into the board rather than into a tank, offered to the
    /// wall it stopped at.
    ///
    /// <b>The direction is the round's own flight, not the bearing</b>, so the
    /// line that was drawn and the line the damage landed on cannot point
    /// different ways - which is the whole claim <see cref="WallRig.Beam"/> is
    /// written under. A level gun has the same lift at both ends, so the
    /// difference of the two drawn rows is the ground direction with nothing
    /// mixed in.
    ///
    /// <b>The force is what the round already carries.</b>
    /// <see cref="Ordnance"/> is an ordered three and the bench's dial is a
    /// multiplier over it; a second table of wall forces beside the calibres
    /// would be a second thing to hold in step.
    ///
    /// <b>And the rig draws no line of its own</b> - see <c>_wallBeam</c>.
    /// </summary>
    /// <summary>Whether the wall on a cell stands across a round leaving it -
    /// <see cref="TankTick.Barred"/> answered by whichever prop is on that cell.
    ///
    /// This is what lets a tank in the ring shoot its way out: the walk skips the
    /// cell it fires from, so without it the one wall on this board a tank is
    /// actually inside was the one wall it could not hit.</summary>
    private bool Barring(Vector2I cell, Vector2 dir)
    {
        foreach (WallProp prop in _walls)
            if (prop.Cell == cell)
                return prop.Bars(dir);
        return false;
    }

    /// <summary>Whether the tank has its nose in masonry - the answer to
    /// <see cref="TankTick.Shoving"/>, asked of the bricks
    /// (<see cref="WallProp.Against"/>).
    ///
    /// <b>Under a ram and never under an ordinary order</b> - the same gate the
    /// event has (<see cref="Sweeps"/>): a wall an order did not ask to break
    /// does not slow the hull that clips through it, which is what the old
    /// driven box also answered by only being pushed in under a ram.
    ///
    /// Asked of every wall the tank is beside, and of the driven tank only: the
    /// other two in the garage are parked somewhere else.</summary>
    private bool Pressing(Vehicle v)
    {
        if (v != Tank || _stage is null || _field.Atlas is null
            || !Sweeps(v.Moving, _ramming, _driveRams))
            return false;
        Vector2I here = _field.CellAt(v.GroundPoint - _origin);
        Vector3 box = WallProp.Box(v, _field.Atlas.HexRect.Size.X * 0.5f);
        Vector3 foot = _stage.Contact(v);
        Vector2 way = v.Atlas.GroundDirection(v.Sprite.HullFacing);
        foreach (WallProp prop in _walls)
            if (Beside(here, prop.Cell) && prop.Against(foot, way, box) > 0)
                return true;
        return false;
    }

    private void Struck(Shell round)
    {
        if (round.Blocked is not Vector2I cell)
            return;
        foreach (WallProp prop in _walls)
        {
            if (prop.Cell != cell)
                continue;
            Vector2 flight = round.To - round.From;
            if (flight.LengthSquared() < 1.0f)
                return;
            // Where the round began, when it began on this wall's own cell: a
            // tank in a ring fires from the middle of it, which in the prop's
            // frame is nought along any heading. Left at "outside" otherwise, and
            // that is exact rather than approximate - a round from another cell
            // starts further back than any piece of this wall, which is what
            // negative infinity says. See WallRig.Fire's own from.
            // Asked of the tank, not of Shell.From and not of CellAt: the muzzle
            // is a drawn point some forty pixels above the ground, and a drawn
            // row is not a ground row on a board that stands on a plinth - the
            // fourth place this board has charged for that difference, and
            // measured here as the shot going on answering the wrong leaf. The
            // bench has the one tank that fired, and the cell it is on is a fact
            // it already holds.
            bool inside = Tank.Cell == prop.Cell;
            prop.Fire(round.Ammo == Shell.Kind.Ap
                          ? WallRig.Strike.Ap : WallRig.Strike.He,
                      prop.Into(flight.Normalized()),
                      (float)(round.Calibre * _wallForce), _wallBeam,
                      inside ? 0.0f : float.NegativeInfinity);
            return;
        }
    }

    /// <summary>Whether a crossing breaks masonry: only under an order that
    /// asked to ram.
    ///
    /// <b>A ram is an intent, and driving past a wall is not one.</b> The board
    /// stands the tank inside a ring it does not fit in - 2.76m of clear yard
    /// against half-hulls of 2.62 / 3.00 / 3.43m - so a rule that broke
    /// masonry on any contact read as a hull demolishing a yard by driving out
    /// of it, which was exactly the complaint.
    ///
    /// <b>What is given up is the sentence this was refused with once:</b> a
    /// frozen piece is a static body, so a tank driving at standing masonry it
    /// did not ask to ram passes through it. A hull clipping a wall is visible;
    /// a wall falling down for free was visible and also wrong.
    ///
    /// <paramref name="moving"/> is kept in the gate even though a crossing
    /// implies movement, because the gate is also what cuts the shove count
    /// (<see cref="Pressing"/>) and a parked hull presses on nothing.
    ///
    /// <paramref name="always"/> - <c>--drive-rams</c> - puts the old answer
    /// back, so the two can be looked at side by side.</summary>
    public static bool Sweeps(bool moving, bool ramming, bool always) =>
        moving && (ramming || always);

    /// <summary>The driven tank's nose cell as of the last look, so a crossing
    /// is a difference rather than a state - null before the first frame.
    ///
    /// <b>Frozen through a pivot, and that is the pivot rule's whole
    /// residence.</b> The nose sweeps an arc a half-hull wide while the contact
    /// point stands still, so tracked raw it would bank rim crossings that no
    /// ram made; frozen, the first driven frame diffs against where the nose
    /// stood before the pivot and a swept rim still fires once, as a
    /// crossing. The debt this runs up is that the diff is not always one
    /// step, which is what <see cref="HexField.Chain"/> pays out.</summary>
    private Vector2I? _noseCell;

    /// <summary>The last ram event: which wall's cell, and the section the rig
    /// said the hull entered. What the tour latches - see
    /// <c>TankBench.Tour</c>.</summary>
    private (Vector2I Cell, int Face)? _ramNamed;

    /// <summary>
    /// The driven ram: fire an event at the wall whose plane the hull's nose
    /// has crossed this frame, if an order asked to ram.
    ///
    /// <b>An event, mirroring the shot, and that is the whole revision.</b> The
    /// shot's path - <see cref="TankTick.Landed"/> into
    /// <see cref="WallProp.Fire"/> - has never needed a degenerate case tamed;
    /// the swept kinematic box needed five (see <see cref="WallRig.Rammed"/>).
    /// A ram is now the same shape: the nose crosses a wall-bearing rim, the
    /// rig answers once, and how fast the tank arrives, where it stops and how
    /// far it turns stay the board's business.
    ///
    /// <b>The nose, not the contact point.</b> A wall's outer plane stands on
    /// the rim, so the front face reaching the rim is the front face reaching
    /// the masonry; measured from the contact point instead, the hull would
    /// clip through half its length of standing wall before anything gave.
    ///
    /// <b>A pivot fires nothing while it turns</b>: <see cref="_noseCell"/>
    /// freezes below walking pace, and the first driven frame diffs against
    /// where the nose stood before the turn. That diff can span more than one
    /// rim - a pivot wider than a step parks the nose two cells from where it
    /// was - so it is walked as a chain of adjacent cells and every rim in it
    /// is answered once. It used to be dropped instead, whenever
    /// <c>HeadingTo</c> said the pair were not neighbours, with the latch
    /// already moved: the wall never heard about the crossing and the tank
    /// drove on through standing masonry.
    /// </summary>
    private void Ram()
    {
        if (_stage is null || _walls.Count == 0 || _garage.Count == 0
            || _field.Atlas is null)
            return;
        Vehicle tank = Tank;
        Vector2 way = tank.Atlas.GroundDirection(tank.Sprite.HullFacing);
        float half = (float)(tank.Atlas.HullSpan * 0.5 * tank.Sprite.BodyScale);
        Vector2I nose = _field.CellAt(tank.GroundPoint + way * half - _origin);
        // The box rides always, order or none: mounted on the nearest wall
        // the hull stands on, beside, or will drive through, posed to the
        // hull every frame. It used to live only while a ram order did, and
        // the seam showed: the moment the order ended, the hull went back to
        // ghosting through the heap its own ram had made. The body is not
        // what breaks masonry - standing pieces are static and the kinematic
        // box passes through them - so a permanent box shoves what is already
        // loose and touches nothing that stands.
        Rebox(Boxable());
        bool ramming = Sweeps(tank.Moving, _ramming, _driveRams);
        // What the intent still bounds is the naming: the gate exists only
        // under a ram order, and a face named by the last ram is forgotten
        // with it - an armed box on a plain drive would keep releasing and
        // breaching, finishing a wall the ram only entered.
        if (!ramming)
            _boxed?.Disarm();
        _boxed?.Drive(_stage.Contact(tank), way, (float)tank.Speed,
                      ramming ? Gate(tank, way, half) : null);
        // No intent - follow silently, so a later ram diffs against the
        // present rather than against wherever the last one ended.
        if (!ramming)
        {
            _noseCell = nose;
            return;
        }
        // A pivot is not a ram - see _noseCell.
        if (tank.Speed < 1.0)
        {
            _noseCell ??= nose;
            return;
        }
        Vector2I was = _noseCell ?? nose;
        _noseCell = nose;
        if (was == nose)
            return;
        Vector3 box = WallProp.Box(tank, _field.Atlas.HexRect.Size.X * 0.5f);
        Vector3 foot = _stage.Contact(tank);
        // Every rim between where the nose stood and where it is, each
        // answered once. One pair at driving speed - but held through a pivot
        // wider than a step the diff spans two rims, and the first version
        // returned on HeadingTo's -1 with _noseCell already moved: the
        // crossing was consumed unanswered and the tank drove on through
        // standing masonry. A chain owes an answer for every rim it names,
        // and each heading is a flat side by construction.
        List<Vector2I> chain = HexField.Chain(was, nose);
        for (int leg = 1; leg < chain.Count; leg++)
        {
            int heading = HexField.HeadingTo(chain[leg - 1], chain[leg]);
            if (heading < 0)
                continue;
            Vector2 dir = tank.Atlas.GroundDirection(heading);
            foreach (WallProp prop in _walls)
            {
                // A wall stands on the rim of its own cell, so the crossed rim
                // belongs to one of the two cells it parts. Which section - if
                // any - is the rig's answer, off the bricks.
                if (prop.Cell != chain[leg - 1] && prop.Cell != chain[leg])
                    continue;
                int face = prop.Rammed(foot, dir, (float)tank.Speed, box);
                if (face < 0)
                    continue;
                _ramNamed = (prop.Cell, face);
                // How fast it was going the moment the masonry gave, and only
                // the first such moment: a speed read afterwards is the speed
                // of a tank already slowed by what it broke.
                if (_ramSpeed < 0.0)
                    _ramSpeed = tank.Speed * WallRig.MetresPerCell
                                / (_field.Atlas.HexRect.Size.X * 0.5);
            }
        }
    }

    /// <summary>The prop whose rig carries the driven box, or null when no
    /// wall is anywhere near the hull. One box, one wall: the box lives on
    /// the target rig's collision bit, so every other wall is invisible to
    /// it. The box itself is permanent company now - what a ram order still
    /// owns is the naming, not the body (see <see cref="Ram"/>).</summary>
    private WallProp? _boxed;

    /// <summary>The wall the hull is about: the first prop whose cell the
    /// tank stands on, will drive through, or stands beside. Asked every
    /// frame rather than latched, so a charge past two walls hands the box
    /// from one to the next as the first is passed - and a wall behind the
    /// tank drops out by itself, because a driven path only shortens.
    ///
    /// <b>The neighbours are in the walk because heaps spill.</b> A felled
    /// section throws its pieces a cell out, so a hull parked beside the
    /// wall's own cell is parked in that wall's rubble - and a box mounted
    /// only on the cell itself left exactly that hull ghosting through the
    /// heap. Own cell first, then the path: during a charge the wall being
    /// driven at must win over one merely stood beside.</summary>
    private WallProp? Boxable()
    {
        foreach (WallProp prop in _walls)
            if (prop.Cell == Tank.Cell)
                return prop;
        for (int i = Tank.PathStep; i < Tank.Path.Count; i++)
            foreach (WallProp prop in _walls)
                if (prop.Cell == Tank.Path[i])
                    return prop;
        foreach (WallProp prop in _walls)
            foreach (int heading in HexField.EdgeHeadings)
                if (_field.Neighbour(Tank.Cell, heading) == prop.Cell)
                    return prop;
        return null;
    }

    /// <summary>The point on the rim the current order still has to cross with
    /// the boxed wall's masonry on it - the naming gate of
    /// <see cref="WallRig.Drive"/>, null when there is no such rim. Walked off
    /// the remaining path: every consecutive pair touching the wall's cell is
    /// a rim the order crosses, and the farthest one still ahead of the nose
    /// is how far the hull is entitled to name masonry for itself. A world
    /// point rather than a distance, because the distance was first measured
    /// in flat screen coordinates where the isometric squash halves vertical
    /// lengths: a vertical ram's grant came out half its true size, the
    /// naming arrived late, and the leaf fell flat off the swallowed branch
    /// while diagonal rams ploughed correctly. A wall the order parks short
    /// of never enters this walk, which is what keeps a hull braking to a
    /// stop in its own ring from naming the leaf its nose merely clears by
    /// less than a corner.</summary>
    private Vector3? Gate(Vehicle tank, Vector2 way, float half)
    {
        if (_boxed is null || _stage is null || _field.Atlas is null)
            return null;
        Vector2 nose = tank.GroundPoint + way * half - _origin;
        Vector3? gate = null;
        float best = float.NegativeInfinity;
        Vector2I prev = tank.Cell;
        for (int i = tank.PathStep; i < tank.Path.Count; i++)
        {
            Vector2I next = tank.Path[i];
            if (prev != _boxed.Cell && next != _boxed.Cell)
            {
                prev = next;
                continue;
            }
            // Only while the hull actually faces this crossing: measured off
            // the pose alone, a hull swinging through its turn at the start
            // of a return leg pointed its nose into the mitre of the leaf
            // NEXT to the breach for a few frames, named it, and BreachShare
            // took the whole neighbour - a section felled by a pivot, the
            // exact sentence the nose-cell freeze exists to forbid. Thirty
            // degrees of the crossing's own direction is a hull that is
            // driving at the rim, not past it; both vectors carry the same
            // squash, so the angle test is consistent with itself - and both
            // are normalized, because GroundDirection is not: a vertical
            // heading comes back squash long (0.5), so an unnormalized dot
            // could never reach 0.87 and the gate stayed shut on every
            // vertical ram - the leaf fell flat off the late-named branch
            // while diagonals ploughed.
            Vector2 dir = _field.FlatAnchor(next) - _field.FlatAnchor(prev);
            if (dir.LengthSquared() >= 1e-6f && way.LengthSquared() >= 1e-6f
                && dir.Normalized().Dot(way.Normalized()) >= 0.87f)
            {
                Vector2 mid = (_field.FlatAnchor(prev)
                               + _field.FlatAnchor(next)) * 0.5f
                              + _field.CentreOffset;
                float d = (mid - nose).Dot(way);
                if (d > best)
                {
                    best = d;
                    // The same expression WallProp.Build stands its wall
                    // with, so the gate cannot disagree with where the rim
                    // is drawn.
                    gate = Stage3D.World(
                        _stage.Origin + mid,
                        _field.LevelAt(_boxed.Cell) * _field.Lift,
                        _field.Squash, _field.RiseFactor);
                }
            }
            prev = next;
        }
        return gate;
    }

    /// <summary>Move the driven box to <paramref name="prop"/>'s rig - a
    /// no-op when it is already there. Mounting takes the tank's pose as it
    /// stands, so the birth exceptions (see <see cref="WallRig.Mount"/>) cover
    /// exactly what the hull is standing in at this moment.</summary>
    private void Rebox(WallProp? prop)
    {
        if (_boxed == prop)
            return;
        _boxed?.Dismount();
        _boxed = prop;
        if (prop is null || _stage is null || _field.Atlas is null)
            return;
        Vehicle tank = Tank;
        float radius = _field.Atlas.HexRect.Size.X * 0.5f;
        prop.Mount(_stage.Contact(tank),
                   tank.Atlas.GroundDirection(tank.Sprite.HullFacing),
                   WallProp.Box(tank, radius), WallProp.Bow(tank, radius),
                   WallProp.Crown(tank, radius));
    }

    /// <summary>Drive at the wall from the side it stands on.
    ///
    /// <b>An order to its cell, and nothing stops the tank there.</b> The
    /// geometry lines up with the rig by itself: a neighbour's centre is two
    /// apothems from the wall's, and the rig clamps its own ram at exactly that -
    /// so where a tank ends up is the same number either way, and none of the
    /// travel had to be rewritten. What is given up is named in the plan: the
    /// wall does not stop it, so it parks in the rubble it made.
    ///
    /// From the wall's own side, because that is what the side dial means: the
    /// run-up is the lane the wall faces, and the pathing walks it.</summary>
    private void Ram(bool order)
    {
        if (Wall is not null && order)
            RamAt(Wall, _wallSide);
    }

    /// <summary>The same drive, at a named wall on a named side - what
    /// <see cref="Ram(bool)"/> does to the ring, and what a tour does to each
    /// wall on the board in turn.
    ///
    /// <b>One copy, because the two legs are the awkward part.</b> A caller that
    /// wrote its own run-up would be a second answer to "and arrive along this
    /// heading", and the two agree until the day one of them is edited.</summary>
    private void RamAt(WallProp wall, int side)
    {
        side = Mathf.PosMod(side, HexField.EdgeHeadings.Length);
        // Out through the named side when the tank is already in the ring, which
        // is where the board opens. One leg, not two: the run-up exists because
        // the pathing cannot say "and arrive along this heading", and from inside
        // the cell the first step is the heading. What it rams is its own wall,
        // which is what a machine in a walled yard does to get out.
        if (Tank.Cell == wall.Cell)
        {
            OrderTo(HexField.Step(wall.Cell, HexField.EdgeHeadings[side]));
            _ramming = true;
            _lineUp = null;
            return;
        }
        Vector2I from = HexField.Step(wall.Cell, HexField.EdgeHeadings[side]);
        // Line up first when it is not already on the lane: a path that comes at
        // the wall round a corner is a ram along whatever heading the search
        // happened to leave on, which is not the side the dial names.
        OrderTo(Tank.Cell == from ? wall.Cell : from);
        // Both legs, because both are this order: the run-up is only there
        // because the pathing cannot say "and arrive along this heading", and a
        // tank that lined up and then stopped ramming would drive through the
        // leaf it came for.
        _ramming = true;
        _lineUp = Tank.Cell != from ? (wall, side) : null;
    }

    /// <summary>Which wall and which side the drive under way is only the
    /// run-up for, so that reaching its end orders the ram itself, or null when
    /// the drive is not a run-up. Two legs rather than one, for the reason
    /// above; held rather than pathed in one go because the board's pathing has
    /// no way to say "and arrive along this heading".
    ///
    /// <b>The wall is carried rather than assumed to be the ring</b>: a tour
    /// rams every wall on the board in turn, and a second leg that always came
    /// in at the ring would walk the tank home in the middle of ramming a
    /// sample.</summary>
    private (WallProp Wall, int Side)? _lineUp;

    /// <summary>Whether the settle report has been printed for this wall. One
    /// line per collapse, and a new wall gets a new line.</summary>
    private bool _wallSaid;

    /// <summary>Whether two cells are the same one or neighbours. Through
    /// <see cref="HexField.Step"/>, which is the only definition of what is next
    /// to what - a second one written in axial arithmetic is wrong on the odd
    /// columns only.</summary>
    private static bool Beside(Vector2I a, Vector2I b)
    {
        if (a == b)
            return true;
        foreach (int heading in HexField.EdgeHeadings)
            if (HexField.Step(a, heading) == b)
                return true;
        return false;
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

            // Where a round's node hangs. Not the tank: a shell must not ride
            // the hull that fired it, and this bench is staged, where the sprite
            // lives in a render target of its own. See TankTick.Deck.
            _tick.Deck = this;

            _tick.TurretHeld = _ => _spinning;
            // No gunnery: one tank has nobody to shoot at, and the lane, the
            // armour and *whose* shell is in the air are all statements about
            // two. See TankTick.Aim. The shell itself is about one tank and the
            // tick has it, so Z fires a whole round here - it just never has
            // anybody to hit, and goes at the ground. See TankTick.Launch.
            _tick.Aim = null;
            _tick.Launch = null;
            // What else on the board stops a shell, and what to do about it when
            // one is stopped. Both pushed with the world rather than held,
            // because both are facts about the board this frame - the third hook
            // beside Aim and Launch, and null on a board with no walls so the
            // shot goes into the field exactly as it did.
            _tick.Obstacles = _walled;
            _tick.Landed = _walls.Count > 0 ? Struck : null;
            _tick.Barred = _walls.Count > 0 ? Barring : null;
            _tick.Shoving = _walls.Count > 0 && _shove ? Pressing : null;
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
        float zoom = ZoomAt ?? Fit(board);
        _camera.Zoom = new Vector2(zoom, zoom);
        float hidden = NoUi ? 0.0f : ControlPanel.Width;
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
        float free = Mathf.Max(window.X - (NoUi ? 0.0f : ControlPanel.Width),
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
        if (_turretAtStart is double lay)
        {
            Sprite.TurretFacing = Mathf.PosMod(lay, 360.0);
            Sprite.QueueRedraw();
        }
        if (_burnAtStart)
            Tank.Burning = true;
        if (_hitAtStart is double bearing)
            Hit(bearing);
        if (_fireAtStart)
            Tick.Fire(Tank);
        if (_destroyAtStart)
            Tick.Kill(Tank);
        NextLeg();
        Ram(_ramAtStart);
    }

    // --- the frame -----------------------------------------------------------

    public override void _Process(double delta)
    {
        if (_garage.Count == 0)
            return;
        delta = FrameClock.FixedStep ?? delta;

        Tick.Run(Tank, delta);
        // After the tank has moved, for TankTick.Fly's reason.
        Tick.Fly(delta);
        // And after both, for the same reason: the box the wall feels is where
        // the tank got to this frame, and a round that landed this frame has
        // already been offered to the wall by Fly.
        Ram();
        // The second leg of a ram, once the run-up has arrived. Here rather than
        // in the order itself because the board's pathing has no way to say
        // "and come in along this heading" - see RamAt.
        if (!Tank.Moving && _lineUp is (WallProp lineWall, int lineSide)
            && Tank.Cell == HexField.Step(lineWall.Cell,
                                          HexField.EdgeHeadings[lineSide]))
        {
            _lineUp = null;
            OrderTo(lineWall.Cell);
            _ramming = true;
        }
        // And the next leg of a queued --drive, once the one before it has
        // arrived. After the ram's own second leg and behind its flag, so the two
        // do not both claim the same standstill.
        if (_lineUp is null)
            NextLeg();

        // And the tour, last of the three, for the same reason the queue is
        // behind the run-up: three things that all wake on a standstill have to
        // be given one order, and the tour is the one that only exists when it
        // was asked for.
        Tour(delta);

        if (_tick.ShakeOn)
            _shake.Update(delta);
        else if (_shake.Moving)
            _shake.Reset();
        Vector2 want = _tick.ShakeOn ? _shake.ScreenOffset(_camera.Zoom.X) : Vector2.Zero;
        if (_camera.Offset != want)
            _camera.Offset = want;

        if (_stage is not null)
        {
            _stage.Selected = NoUi ? null : Tank;
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

        if (_hud is not null && !NoUi)
            _hud.Text = Note();

        // Said out loud once the last piece has stopped, and printed rather than
        // left to the panel: the collapse is judged on numbers - WallBench's
        // reason, and here it is load-bearing twice over, because a live solver
        // is the one thing on this board that does not repeat, so a capture
        // cannot be diffed and the figures are the whole of the evidence.
        // Nothing moving is the only end a live collapse has.
        //
        // Not under a tour: that prints a line of its own per ram, out of the
        // same tallies, and a one-shot line about the first of ten collapses
        // sitting among them reads as a figure for the lot.
        if (!_tour && Wall?.Rig is { Struck: true } settling && !_wallSaid
            && settling.Clock > 0.6f && settling.Awake == 0)
        {
            _wallSaid = true;
            GD.Print("tank bench: " + WallNote().Replace("\n", " - "));
        }

        _frame++;
        if (CapturePath is not null && _frame >= CaptureAt)
        {
            // And said at the shutter too, if settling never said it.
            //
            // <b>A collapse that never ends and a shot that moved nothing are
            // the same silence.</b> Measured on the ring at HE force 3: the
            // solver frees a jammed pile with an enormous impulse, a block
            // leaves at 324 m/s, and nothing is ever all at rest again - so the
            // one force the picture was being judged at printed no numbers at
            // all. A named frame is a defined measurement whether or not the
            // heap has stopped, and saying which of the two it is costs a word.
            if (!_wallSaid && Wall?.Rig is { Struck: true })
            {
                _wallSaid = true;
                GD.Print($"tank bench: at frame {_frame}, still settling - "
                         + WallNote().Replace("\n", " - "));
            }
            Capture(CapturePath);
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
               + (_walls.Count > 0 ? "\n" + WallNote() : "")
               + "\nleft click drives, right click stops, middle drag pans"
               + "\nTAB the panel, R resets, F12 shoots";
    }

    /// <summary>
    /// What the wall is doing, in the numbers a picture cannot answer.
    ///
    /// The same figures the wall bench prints, for its reason: a heap of sixty
    /// pieces on a cell 109px tall reads as a mass whichever way it went, so
    /// whether it is lying down is a number rather than a look. Printed here as
    /// well as on the panel because under <c>--capture</c> there is no panel.
    /// </summary>
    private string WallNote()
    {
        if (Wall is not { Rig: { } rig } prop)
            return "no wall";
        (float reach, float top, int off, int awake) = prop.Pile();
        // What the nose is pressing against, asked the way the speed cap asks
        // it - see Pressing.
        int shoving = _stage is not null && _field.Atlas is not null
                      && _garage.Count > 0
            ? prop.Against(_stage.Contact(Tank),
                           Tank.Atlas.GroundDirection(Tank.Sprite.HullFacing),
                           WallProp.Box(Tank, _field.Atlas.HexRect.Size.X * 0.5f))
            : 0;
        // What actually struck it, off the rig, and never the ammunition dial:
        // a ram is not a round, so the loaded shell says nothing about a wall a
        // tank drove into - which is what the first version of this line did.
        return $"wall (the ring, {_walls.Count - 1} samples off it) "
               + (rig.Struck ? $"{rig.Shot} " : $"{_tick.Ammo} x{_wallForce:F2} ")
               + $"on side {HexField.EdgeHeadings[_wallSide]}  "
               + (rig.Struck
                   ? $"{rig.Clock:F2}s, {awake} moving, {rig.Loose} let go  "
                   : "standing, nothing has reached it  ")
               + $"reach {reach:F2}, top {top:F2}, {off} of {rig.Count} "
               + "off the cell"
               // And how many whole sections went, which the piece count cannot
               // say: one leaf levelled and three levelled differ by a number
               // that reads as a heavier charge. WallRig.Broken.
               + (rig.Broken > 0 ? $", {rig.Broken} section(s) breached" : "")
               // And how many went over the edge of the board, which is a
               // different fact: rubble on a neighbour is what was asked for,
               // rubble in the void is the board being too small for the shot.
               // WallRig.Fell's own words.
               + (rig.Fell > 0 ? $", {rig.Fell} off the board" : "")
               // And how many of those it let go of never moved, which is the
               // one fact the leaf tally cannot carry: unfrozen dry masonry with
               // no bed joint is a stack of boxes that stands, so a leaf can
               // report all of itself gone and still be standing in the picture.
               // WallRig.Stuck.
               + (rig.Stuck > 0 ? $", {rig.Stuck} let go but standing" : "")
               // And that the size and side dials are refused while it lies,
               // in the line that is always on screen: a held dial with no
               // notice reads as a dial that does nothing. See Relay.
               + (rig.Struck ? ", dials locked (R lays a new wall)" : "")
               // And how many have cleared themselves, which is a third fact
               // again: a swept cell and a shot that moved nothing are the same
               // empty picture. WallRig.Crumbled.
               + (rig.Crumbled > 0 ? $", {rig.Crumbled} gone" : "")
               // And what the nose is pressing against this instant, which is the
               // cause of the speed the line above reports: a tank crawling at a
               // fifth of its cruise and a tank whose order has gone stale are the
               // same picture, and the cap is the only thing that tells them
               // apart. See WallRig.Against.
               + (shoving > 0 ? $", shoving {shoving}" : "")
               // And which leaves that came out of, which the total cannot say:
               // a leaf driven through and a leaf caught by the corner of the
               // band are the same count and different pictures. WallRig.Leaves.
               + (rig.Struck ? $"\nleaves {rig.Leaves()}" : "")
               // And whether the drive under way is a ram at all, because a tank
               // driving past a standing wall and a tank whose ram did nothing
               // are the same picture. See Sweeps.
               + (Tank.Moving
                   ? _ramming || _driveRams ? ", ramming" : ", driving past"
                   : "")
               // The one figure the picture cannot give and nobody can guess -
               // see _ramSpeed.
               + (_ramSpeed >= 0.0
                   ? $"\nram at {_ramSpeed:F1} m/s against RamSpeed "
                     + $"{WallRig.RamSpeed:F1}"
                   : "")
               // And what else stands on the board, and whether it still
               // stands. Every sample carries its own recipe, so a wall that
               // ignores the sliders is this working rather than failing - and a
               // sample the tank has driven through reads exactly like one it has
               // not until this line says so, which is the whole of "the ram is
               // taking walls it was never aimed at". See Samples.
               + "\n" + string.Join("  ", _walls.Skip(1).Select(
                   w => $"({w.Cell.X},{w.Cell.Y}) "
                        + (w.Rig is { Struck: true } r2
                            ? $"{r2.Loose} let go, {r2.Stuck} standing"
                            : "standing")));
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

    /// <summary>Order the next queued <c>--drive</c> leg, if the tank is not
    /// already on one. Ordering is what advances the index, so a leg that goes
    /// nowhere - the cell the tank is already on - is spent rather than retried
    /// every frame.</summary>
    private void NextLeg()
    {
        if (_driveLeg >= _driveTo.Count || Tank.Moving)
            return;
        (Vector2I cell, bool ram) = _driveTo[_driveLeg++];
        if (ram)
            Charge(cell);
        else
            OrderTo(cell);
    }

    /// <summary>Drive to <paramref name="cell"/>, and say whether an order was
    /// actually given: a click on a cell the board does not have, on the one the
    /// tank is already standing on, or on nothing at all with a wreck is not an
    /// order, and <see cref="Charge"/> must not call one a ram.</summary>
    private bool OrderTo(Vector2I cell)
    {
        if (!_field.InBounds(cell) || Tank.Wreck.Dead)
            return false;
        // Already on the way there: the path is left alone rather than reissued.
        // A right click on the hex already being driven to is how a ram is
        // asked for, and a reissue would restart the leg from the cell behind
        // the tank - a visible jerk for nothing.
        if (Tank.Moving && Tank.Path.Count > 0 && Tank.Path[^1] == cell)
        {
            _ramming = false;
            return true;
        }
        if (cell == Tank.Cell)
            return false;
        // Every order is an ordinary one until the ram says otherwise - see
        // _ramming. Cleared here rather than at each call site, because the one
        // place every order goes through is the one place that cannot forget.
        _ramming = false;
        Tank.Path = _field.FindPath(Tank.Cell, cell, new HashSet<Vector2I>());
        Tank.PathStep = 0;
        _spinning = false;
        _field.Highlight = Tank.Path;
        _field.QueueRedraw();
        return true;
    }

    /// <summary>The same order, driven as a ram - <b>a right click on the hex
    /// being driven to.</b>
    ///
    /// <b>The other button on the same gesture</b>, because that is what the two
    /// are to each other: one drive, and whether the hull is meant to go through
    /// what is standing there. Left says where, right says where and through. A
    /// key would have to name a target the mouse has already named, and <c>M</c>
    /// is the version that does - it rams the ring by the side dial, which is
    /// the wall the bench is about and not the one under the cursor.
    ///
    /// <b>Standing still is ESC alone now, which is where it already was.</b>
    /// The bench has one tank, so the right button had nothing to aim at and was
    /// a second way to cancel an order; a ram has a target, and a target is what
    /// the mouse can say and the keyboard cannot.
    ///
    /// <b>By cell, like every other button on this board</b>, and through
    /// <see cref="OrderTo"/> so that a right click on a hex nobody can drive to
    /// is not an order at all, let alone a ram. That includes the cell the tank
    /// is standing on: a machine already inside the ring rams its way out with
    /// <c>M</c>, which names a side, and a click on its own hex names none.
    ///
    /// A right click on the hex already being driven to adds the flag and
    /// nothing else, so an ordinary drive turns into a ram mid-leg: it is why
    /// <see cref="OrderTo"/> leaves a path alone when it is asked for the one
    /// already under way, rather than restarting it from the cell behind the
    /// tank.</summary>
    private void Charge(Vector2I cell)
    {
        if (OrderTo(cell))
            _ramming = true;
    }

    private void Hit(double bearing)
    {
        _hitSide = Angles.SideFor(bearing);
        Tick.TakeHit(Tank, HitFrom, Ordnance.At(_tick.Calibre), null,
                     Ordnance.BiteFor(_tick.Calibre));
    }

    /// <summary>
    /// The bench as it opened, which is more than putting the tanks back.
    ///
    /// <b>Everything that holds a pose with nothing driving it has to be named
    /// here</b> - <see cref="Main.ResetAll"/>'s list, arriving at the other end
    /// of the board, and this is that list rather than a shorter one on purpose:
    /// a spring left leaning, a clock left mid-cycle, a rut left in the mud or a
    /// wall left in rubble is state the next measurement is taken against, and
    /// R's whole job is being the one key that makes two measurements
    /// comparable. A reset that leaves any of it behind is lying about what it
    /// did, and every one of them is invisible in the same way: the picture looks
    /// like a bench that opened, and the numbers come out of the one before.
    ///
    /// <b>The wall is re-laid rather than tidied</b>, because a struck wall has
    /// no undo - the pieces are bodies the solver has moved. <see cref="WallProp.Build"/>
    /// frees the old stack and rig and stands a new one, which is exactly what
    /// <c>WallBench.Fire</c> does before a second shot and for the same reason:
    /// a shot into rubble is a different experiment.
    ///
    /// <b>The dials that are levels go back to their tuned values, and the dials
    /// that are choices do not.</b> The harness's split: a level of one is the
    /// value worth being able to get back to in one key, whereas which calibre is
    /// loaded, which side a hit comes from and which side the wall stands on are
    /// what to do next, and a reset that answered those would be undoing the
    /// setup rather than the run.
    /// </summary>
    private void Reset()
    {
        _spinning = false;
        _lineUp = null;
        _ramming = false;
        // Before the tanks are repaired, not after: a round still in the air
        // would land on armour that had just been made good, which is the one way
        // this reset could leave a mark behind it.
        Tick.ClearRounds();
        // The ruts, which are the longest-lived thing on the board: they outlast
        // the drive that made them by design, so a board that was not swept reads
        // as one that had been driven on before anybody touched it.
        _marks?.Clear();
        foreach (Vehicle vehicle in _garage)
        {
            Tick.CancelOrder(vehicle);
            TankSprite s = vehicle.Sprite;
            s.HullFacing = 270.0;
            s.TurretFacing = 270.0;
            vehicle.Pitch.Reset();
            s.Pitch = 0.0;
            vehicle.Rumble.Reset();
            s.Shake = 0.0;
            s.Roll = 0.0;
            vehicle.Tremble.Reset();
            s.TremblePitch = 0.0;
            s.TrembleYaw = 0.0;
            vehicle.Exhaust.Reset();
            s.ExhaustPhase = -1;
            vehicle.TrackLeft.Reset();
            vehicle.TrackRight.Reset();
            s.TrackPhaseLeft = -1;
            s.TrackPhaseRight = -1;
            s.TrackBlurLeft = 0.0;
            s.TrackBlurRight = 0.0;
            // Before the fire is put out, because a wreck is what keeps
            // relighting it - see TankTick.UpdateWreck.
            vehicle.Wreck.Reset();
            s.Wrecked = false;
            s.Char = 0.0;
            s.FireDensity = 1.0f;
            s.SmokeDensity = 1.0f;
            vehicle.Burning = false;
            vehicle.Burn.Reset();
            s.Burning = false;
            s.FirePhase = -1;
            s.BurnPhase = -1;
            // A ceasefire is part of it for the repair's reason: a tank left
            // engaging would start putting holes back into armour just fixed.
            vehicle.Target = null;
            vehicle.Solution = Gunnery.None;
            vehicle.ReloadLeft = 0.0;
            vehicle.Hit.Reset();
            vehicle.HitCount = 0;
            s.HitPhase = -1;
            s.Repair();
            vehicle.Scan.Reset();
            vehicle.Recoil.Reset();
            vehicle.Barrel.Reset();
            s.RecoilPhase = 0;
            vehicle.Tremble.Level = 1.0;
            vehicle.Recoil.Level = 1.0;
            vehicle.ShotFrame = -1;
            s.FlashFrame = -1;
            s.ShotPhase = -1;
            s.RecoilPitch = 0.0;
            s.RecoilRoll = 0.0;
            vehicle.Cell = vehicle.HomeCell;
            Tick.Park(vehicle);
        }
        // The class the bench opened on, which is a choice the command line
        // already made - so unlike the other choices this one has a stated
        // answer to go back to. Pick() moves the incoming tank onto the outgoing
        // one's cell, so leaving it alone would also leave every tank in the
        // garage parked somewhere the flags never asked for.
        _pick = _opened;
        OnlyOne();
        _recoilLevel = 1.0;
        _sizeLevel = 1.0;
        _traverse = 1.0;
        // One line rather than one per tank, because the traverse knob is not
        // held on a machine - see Gunnery.TraverseLevel.
        Gunnery.TraverseLevel = 1.0;
        ApplySize();
        Relaid();
        _shake.Reset();
        _camera.Offset = Vector2.Zero;
        _sea?.Settle();
        _wake?.Clear();
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
    /// builds would fail that check as a description of nothing. These rows have
    /// a file of their own instead - <see cref="PanelFile"/>, read through the
    /// same <see cref="PanelText"/>, checked against these rows in both
    /// directions at start-up (see <see cref="Vet"/>), its opening values
    /// applied through the same setters a click uses and kept off any row a
    /// flag has claimed.</summary>
    private void Panel(CanvasLayer layer)
    {
        _benchText = PanelText.Load(Path.Combine(
            ProjectSettings.GlobalizePath("res://"), PanelFile));
        if (!_benchText.Loaded)
            GD.Print($"tank bench: panel built-in ({PanelFile}: "
                     + $"{_benchText.Error})");
        else
            GD.Print("tank bench: panel json");
        _panel = new ControlPanel { Text = _benchText };
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
                      () => _tick.ScanEnabled, on => _tick.ScanEnabled = on);

        _panel.Heading("tank.ride", "ride");
        _panel.Toggle("tank.ride.pitch", "body pitch  (P)", () => _tick.PitchEnabled,
                      on => { _tick.PitchEnabled = on; Tick.UpdatePitch(Tank, 0.0, 0.0); });
        _panel.Toggle("tank.ride.rumble", "ground rumble  (B)", () => _tick.RumbleEnabled,
                      on => { _tick.RumbleEnabled = on; Tick.UpdateRumble(Tank, 0.0); });
        _panel.Toggle("tank.ride.tremble", "engine tremble  (I)", () => _tick.TrembleEnabled,
                      on => { _tick.TrembleEnabled = on; Tick.UpdateTremble(Tank, 0.0); });
        _panel.Slide("tank.ride.tremble_level", "tremble level", 0.0, 2.5, 0.05,
                     () => _tick.TrembleLevel, v => _tick.TrembleLevel = v, "x",
                     () => $"{Tank.Tremble.TravelAt(Tank.Speed, _tick.TrembleLevel):F2}px "
                           + "of stern travel at this speed");
        _panel.Toggle("tank.ride.tracks", "belts wind  (C)", () => _tick.TracksEnabled,
                      on => { _tick.TracksEnabled = on; Tick.UpdateTracks(Tank, (0.0, 0.0), 0.0); });
        _panel.Toggle("tank.ride.ruts", "leave ruts", () => _ruts,
                      on => _ruts = on);
        _panel.Readout("tank.ride.belt", () =>
            $"belt {Tank.Track.Phase:F2}, slip {Tank.Track.Slip:F2}, "
            + $"blur {Tank.Track.Blur:F2}");

        _panel.Heading("tank.smoke", "exhaust and fire");
        _panel.Toggle("tank.smoke.on", "exhaust plume  (O)", () => _tick.ExhaustEnabled,
                      on => { _tick.ExhaustEnabled = on; Tick.UpdateExhaust(Tank, 0.0); });
        _panel.Slide("tank.smoke.level", "plume rate", 0.0, 3.0, 0.05,
                     () => _tick.ExhaustLevel, v => _tick.ExhaustLevel = v, "x",
                     () =>
                     {
                         double rate = Tank.Exhaust.RateAt(Tank.Speed, _tick.ExhaustLevel);
                         return $"{rate:F1} phases/s, "
                                + $"{60.0 / Math.Max(rate, 0.01):F1} frames a phase";
                     });
        _panel.Toggle("tank.smoke.ramp", "analogue load ramp", () => _tick.ExhaustRamp,
                      on => _tick.ExhaustRamp = on);
        _panel.Toggle("tank.smoke.burn", "set it alight  (J)",
                      () => Tank.Burning, on => Tank.Burning = on);

        _panel.Heading("tank.gun", "the gun");
        _panel.Press("tank.gun.fire", "fire  (Z)", () => Tick.Fire(Tank));
        _panel.Toggle("tank.gun.tube", "barrel recoil  ([)", () => _tick.RecoilTube,
                      on => _tick.RecoilTube = on);
        _panel.Toggle("tank.gun.shear", "body shear on the shot  (])",
                      () => _tick.RecoilShear, on => _tick.RecoilShear = on);
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
        _panel.Toggle("tank.gun.shake", "camera shake", () => _tick.ShakeOn,
                      on =>
                      {
                          _tick.ShakeOn = on;
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
                      () => _tick.Calibre,
                      i => _tick.Calibre = Math.Clamp(i, 0, Ordnance.Count - 1));
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

        // Only on a board that has one. A group of rows that move nothing is
        // worse than no group: it reads as the wall being broken rather than as
        // there being no wall.
        if (_walls.Count > 0)
        {
            _panel.Heading("wall", "the wall");
            _panel.Choice("wall.ammo", "loaded",
                          new[] { "HE - bursts on the face",
                                  "AP - straight through" },
                          () => (int)_tick.Ammo,
                          i => _tick.Ammo = (Shell.Kind)Math.Clamp(i, 0, 1));
            _panel.Slide("wall.force", "force", 0.0, WallBench.MostForce, 0.05,
                         () => _wallForce, v => _wallForce = v, "x",
                         // What one setting buys, in the unit the chosen shot is
                         // measured in - the wall bench's own line, so the two
                         // benches cannot describe one force differently.
                         () => WallRig.Costs(
                             _tick.Ammo == Shell.Kind.Ap
                                 ? WallRig.Strike.Ap : WallRig.Strike.He,
                             (float)(_wallForce * Ordnance.At(_tick.Calibre))));
            _panel.Radio("wall.side",
                         () => $"it stands on side {_wallSide + 1} of 6, "
                               + $"{HexField.EdgeHeadings[_wallSide]} deg",
                         Array.ConvertAll(HexField.EdgeHeadings, d => $"{d}"),
                         () => _wallSide,
                         i => Relay(() => _wallSide = Math.Clamp(
                             i, 0, HexField.EdgeHeadings.Length - 1)));
            _panel.PressPair("wall.ram", "drive into it  (M)", () => Ram(true),
                             "next seed",
                             () => Relay(() => _brick.Seed++));
            _panel.Readout("wall.ram_note", () =>
                _ramSpeed < 0.0
                    ? $"nothing rammed yet - it is {WallRig.RamSpeed:F1} m/s the "
                      + "shot was tuned at"
                    : $"arrived at {_ramSpeed:F1} m/s against "
                      + $"{WallRig.RamSpeed:F1} tuned");
            _panel.Slide("wall.sides", "sides", 1.0, 6.0, 1.0,
                         () => _brick.Sides,
                         v => Relay(() => _brick.Sides = (int)Mathf.Round(v)), "");
            _panel.Slide("wall.courses", "height", 1.0, WallBench.MostCourses, 1.0,
                         () => _brick.Courses,
                         v => Relay(() => _brick.Courses = (int)Mathf.Round(v)),
                         " courses");
            _panel.Slide("wall.leaves", "thickness", 1.0, WallBench.MostLeaves, 1.0,
                         () => _brick.Leaves,
                         v => Relay(() => _brick.Leaves = (int)Mathf.Round(v)),
                         " leaves");
            _panel.Toggle("wall.beam", "the rig draws its own line too",
                          () => _wallBeam, on => _wallBeam = on);
            _panel.Toggle("wall.shove", "masonry is heavy going  (--no-shove)",
                          () => _shove, on => _shove = on);
            _panel.Toggle("wall.gate", "any order rams  (--drive-rams)",
                          () => _driveRams, on => _driveRams = on);
            _panel.Toggle("wall.shunt", "the ram shoves what it frees  "
                                        + "(--no-shunt)",
                          () => WallRig.Shunts, on => WallRig.Shunts = on);
            _panel.Toggle("wall.breach", "a breach takes the whole side  "
                                         + "(--no-breach)",
                          () => WallRig.Breaches, on => WallRig.Breaches = on);
            _panel.Toggle("wall.crumble", "fallen pieces clear  (--no-crumble)",
                          () => WallRig.Crumbles, on => WallRig.Crumbles = on);
            _panel.Toggle("wall.hulls", "draw the colliders  (--hulls)",
                          () => WallHulls.Shown, on => WallHulls.Shown = on);
            _panel.Readout("wall.state", () => WallNote());
        }

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

        // The file's opening values, before the NoUi branch can free the
        // panel: the setters write into the bench, not into widgets, so a
        // capture reads the same file a session does - the harness's "the
        // panel is always built" argument, arrived at from the other side.
        _panel.OpenDefaults(_flagged);
        Vet();

        // Opened on the tank, closed elsewhere. The harness opens every group
        // shut because it has fifty rows in ten of them; here there are eight
        // groups, and the one the bench exists for should not need a click to
        // be found.
        _panel.Expand("tank.pick", true);
        if (!NoUi)
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

    /// <summary>Check <see cref="PanelFile"/> against the rows this panel just
    /// built, in both directions - the harness's panel.json check, moved to
    /// where the bench's row list actually lives. It cannot run in the
    /// self-test: these rows are delegates onto a live bench, and a second
    /// declaration of the list just for checking would be the two-statements
    /// failure the check exists to catch.
    ///
    /// <b>The wall group is excused only on a board with no wall</b>: the file
    /// is one for all three bench scenes, and the group it describes is built
    /// exactly when there is masonry to point it at - an entry for a row a
    /// walled board does build is a description, not a leftover.</summary>
    private void Vet()
    {
        if (!_benchText.Loaded || _panel is null)
            return;
        var built = _panel.Ids;
        var haveRows = built.Select(pair => pair.Id).ToHashSet();
        var fileRows = _benchText.RowIds.ToHashSet();
        foreach ((string id, string group) in built)
        {
            if (!fileRows.Contains(id))
                GD.PushWarning($"tank bench: {PanelFile} has no entry for "
                               + $"row {id}");
            else if (_benchText.GroupOf(id) != group)
                GD.PushWarning($"tank bench: {PanelFile} puts {id} in group "
                               + $"'{_benchText.GroupOf(id)}', the panel in "
                               + $"'{group}'");
        }
        foreach (string id in fileRows)
            if (!haveRows.Contains(id)
                && !(_walls.Count == 0 && _benchText.GroupOf(id) == "wall"))
                GD.PushWarning($"tank bench: {PanelFile} names a row this "
                               + $"panel does not build: {id}");
        // And the flag table, for the harness's reason: a flag claiming a row
        // that does not exist protects nothing, so the file overrules it and
        // the flag reads as one that never arrived.
        foreach (string id in FlagRows.Values.SelectMany(v => v).Distinct())
            if (!haveRows.Contains(id)
                && !(_walls.Count == 0 && id.StartsWith("wall.")))
                GD.PushWarning($"tank bench: a flag claims row {id}, which "
                               + "this panel does not build");
    }

    // --- input ---------------------------------------------------------------

    /// <summary>The cell under the cursor. By cell, like every button on the
    /// harness's board: the silhouette overhangs its own hexagon, so picking
    /// by pixels would order a drive by clicking the ground beside it.</summary>
    private Vector2I Pointed() =>
        _field.ClampCell(_field.CellAt(_field.ToLocal(GetGlobalMousePosition())));

    public override void _UnhandledInput(InputEvent @event)
    {
        if (_garage.Count == 0)
            return;

        if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Middle } middle)
        {
            // Drag pans, tap destroys - SceneRoot.MiddleTapped.
            if (MiddleTapped(middle, out Vector2 _))
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
                    OrderTo(Pointed());
                    return;
                case MouseButton.Right:
                    // The other button on the same hex is a ram - see Charge.
                    // Standing still is ESC, which is where it already was.
                    Charge(Pointed());
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
                _tick.PitchEnabled = !_tick.PitchEnabled;
                Tick.UpdatePitch(Tank, 0.0, 0.0);
                break;
            case Key.B:
                _tick.RumbleEnabled = !_tick.RumbleEnabled;
                Tick.UpdateRumble(Tank, 0.0);
                break;
            case Key.I:
                _tick.TrembleEnabled = !_tick.TrembleEnabled;
                Tick.UpdateTremble(Tank, 0.0);
                break;
            case Key.C:
                _tick.TracksEnabled = !_tick.TracksEnabled;
                Tick.UpdateTracks(Tank, (0.0, 0.0), 0.0);
                break;
            case Key.O:
                _tick.ExhaustEnabled = !_tick.ExhaustEnabled;
                Tick.UpdateExhaust(Tank, 0.0);
                break;
            case Key.N:
                _tick.ScanEnabled = !_tick.ScanEnabled;
                break;
            // The ram. M rather than a letter that says so, because A-Z is full
            // on this bench and M is what is left free on both roots; the panel
            // is where it is found by name.
            case Key.M:
                Ram(true);
                break;
            case Key.Bracketleft:
                _tick.RecoilTube = !_tick.RecoilTube;
                break;
            case Key.Bracketright:
                _tick.RecoilShear = !_tick.RecoilShear;
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

}
