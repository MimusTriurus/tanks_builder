using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// Test harness for the tank sprite atlases: a hex field, one tank driving
/// around it, and independent control of hull and turret headings.
///
/// What it is actually for, in order:
///   1. does the turret turn independently of the hull, with no seam gap
///   2. does the turret stay pinned to its axis instead of orbiting (SPACE)
///   3. does the tank sit centred in its cell at all twelve headings
///   4. does the field tessellate without gaps or overlap
/// </summary>
public sealed partial class Main : Node2D
{
    private const string SpritesRoot = "D:/Projects/AgentCoding/BlenderMCP/Sprites";

    /// <summary>Where <c>stage_sounds.sh</c> puts the audio. Off an absolute path
    /// for the reason the atlases are, and one more besides: the bytes are not in
    /// this repository - see the script - so this directory is often simply
    /// absent, and everything downstream of it has to be happy about that.
    /// </summary>
    private const string SoundsRoot = "D:/Projects/AgentCoding/BlenderMCP/Sounds";
    /// <summary>The tanks to look for on disk, in key order: one per class,
    /// all three built from separate parts - hull, turret, barrel, engine and
    /// two belts as their own meshes.
    ///
    /// The single-mesh HT/MT/LT used to sit alongside them so a winding belt
    /// could be judged against a still one. They are gone: their belts are not
    /// separate meshes, so they cannot wind at all, and a tank that slides on
    /// dead tracks is not a comparison, it is the old bug still on screen.
    /// The atlases stay on disk under Sprites/; nothing here loads them.</summary>
    private static readonly string[] Tags = { "LTP", "MTP", "HTP" };

    /// <summary>
    /// One directory every class reads its pixels from, or null for one per tag.
    ///
    /// Null, which is the normal state: all three classes now have an atlas of
    /// their own, so each wears its own pixels and the field shows three tanks
    /// rather than one tank three times.
    ///
    /// It is there for the case it was used for twice - a new layer landing on
    /// one tank ahead of the others. Setting it to a tag makes every class read
    /// that tank's atlases, so the layer can be judged in motion instead of on a
    /// contact sheet. It swaps the *pixels*, not the class: <see
    /// cref="AtlasSet.Tag"/> stays the tag it was asked for, so <see
    /// cref="MovementProfile"/> still gives each tank its own speed, acceleration
    /// and size - which makes it a sharper test of the size logic than the real
    /// thing, not a weaker one, because three identical sprites at 0.85, 1.00 and
    /// 1.15 have nothing but the scale to explain any difference between them.
    ///
    /// Whichever tank is being looked at is the one to lend. The heavy earned its
    /// turn because its gun is a short fat howitzer rather than a rod: the muzzle
    /// the bench finds off the barrel layer is a stub 27x19 px on the away
    /// headings, the hardest case for <see cref="AtlasSet.FindMuzzles"/> and the
    /// one its no-erosion fallback pass exists for.
    /// </summary>
    private const string? SharedSpriteDir = null;

    private const double SpinSpeed = 90.0;      // deg/sec, for the wobble check

    private readonly Dictionary<string, AtlasSet> _atlases = new();
    private readonly List<string> _loaded = new();

    /// <summary>One sound set per class, plus the shared one. Split the way the
    /// pixels are: what a tank is, per tank; what happens to it, once - see
    /// <see cref="SoundSet"/>.</summary>
    private readonly Dictionary<string, SoundSet> _sounds = new();
    private SoundSet _commonSounds = null!;

    /// <summary>Sound on, as every other effect is on a switch. Off silences,
    /// it does not unload, so an A/B is one keypress apart.</summary>
    private bool _soundEnabled = true;

    /// <summary>
    /// All three tanks, in the order they loaded, which is also key order and the
    /// order they are parked in from left to right.
    ///
    /// Three at once rather than one wearing three atlases in turn, because the
    /// size difference is a comparison and a comparison needs both terms on
    /// screen: 0.85x against 1.15x cannot be judged from memory across a key
    /// press. Everything a tank owns is in <see cref="Vehicle"/>; what is left
    /// here belongs to the harness.
    /// </summary>
    private readonly List<Vehicle> _vehicles = new();

    /// <summary>Which one the keys drive. Selected by clicking it, by a number
    /// key, or from the panel - three ways into one field, which is what stops
    /// them disagreeing.</summary>
    private int _active = 1;                    // MTP - the reference tank

    private Vehicle Active => _vehicles[_active];

    /// <summary>The sprite of the tank being driven. A property rather than a
    /// field so that every line written against the old single-tank harness still
    /// says what it said - it just now says it about whichever tank is
    /// selected.</summary>
    private TankSprite _tank => Active.Sprite;

    private HexField _field = null!;

    /// <summary>The ring on the ground under the tank being driven, or null on a
    /// run that has no interface - see <see cref="SelectionRing"/>.</summary>
    private SelectionRing? _ring;

    /// <summary>The same mark in red, under whoever the driven tank is shooting
    /// at. Two rings rather than one that changes colour: both statements are
    /// true at once and about different tanks.</summary>
    private SelectionRing? _targetRing;

    /// <summary>What the gunnery marks were last painted for. See the repaint in
    /// <see cref="_Process"/>.</summary>
    private (int, Vector2I, Vehicle?, Vector2I?, bool) _painted;

    private Label _hud = null!;
    private Camera2D _camera = null!;

    private readonly Vector2 _origin = new(220, 200);

    /// <summary>Where the three are parked: one row, every other column. The same
    /// row means the same screen height, and two columns apart - 372px against a
    /// heavy's 212px of hull - means the silhouettes never touch, so what the eye
    /// compares is size rather than spacing.</summary>
    private static readonly Vector2I[] HomeCells =
        { new(2, 2), new(4, 2), new(6, 2) };

    private Vector2I _cell
    {
        get => Active.Cell;
        set => Active.Cell = value;
    }

    private List<Vector2I> _path
    {
        get => Active.Path;
        set => Active.Path = value;
    }

    private int _pathStep
    {
        get => Active.PathStep;
        set => Active.PathStep = value;
    }

    private double _speed
    {
        get => Active.Speed;
        set => Active.Speed = value;
    }

    private MovementProfile _profile => Active.Profile;

    /// <summary>A multiplier over every class's <see cref="MovementProfile.Size"/>,
    /// on the panel and on --size. A multiplier rather than a size, for the reason
    /// the tremble level is one: the three class figures are deliberately spread,
    /// and a dial that set the size directly would flatten that spread the first
    /// time it was touched. This asks "all of them bigger" and leaves the ratios
    /// where they were chosen.</summary>
    private double _sizeLevel = 1.0;
    /// <summary>
    /// The driven tank's own state, read through the names the harness has always
    /// used for it.
    ///
    /// Every one of these used to be a field, when there was one tank. As
    /// properties onto <see cref="Active"/> they mean what they always meant -
    /// "the tank being driven" - so a key, a panel row and the trace all keep
    /// pointing at the right vehicle without a hundred call sites learning about
    /// the list. The per-frame update methods take a <see cref="Vehicle"/>
    /// explicitly instead, because those have to run for the tanks nobody is
    /// driving too: three idle tanks with dead engines would be three sprites.
    /// </summary>
    private BodyPitch _pitch => Active.Pitch;

    /// <summary>Body pitch is off unless asked for - key P, or --pitch on a
    /// capture run. It is an interpretation of the sprites rather than
    /// something the atlases contain, so the plain rendered motion stays the
    /// default and the effect is something you switch on to compare against
    /// it.</summary>
    private bool _pitchEnabled;

    private BodyRumble _rumble => Active.Rumble;
    /// <summary>Ground rumble - key B, or --rumble. Off again: the whole-pixel
    /// jolt turned out to be a stronger reading of the ground than wanted, and
    /// the engine tremble now covers moving as well as standing, so the two
    /// stack rather than divide the work.</summary>
    private bool _rumbleEnabled;

    private EngineTremble _tremble => Active.Tremble;
    /// <summary>Engine tremble, standing or moving. The one vibration that is on
    /// by default: a tank that slides over the ground with nothing moving on it
    /// is wrong in a way the plain render is not. Key I, or --no-tremble.</summary>
    private bool _trembleEnabled = true;

    private TurretScan _scan => Active.Scan;
    /// <summary>Idle turret traverse - key N, or --scan.</summary>
    private bool _scanEnabled;

    private TrackLoop _track => Active.Track;
    /// <summary>The belts winding - key C, or --no-tracks. On by default, and
    /// not really an option: a tank has tracks and they move. The switch is so
    /// the hull layer can be looked at without them.</summary>
    private bool _tracksEnabled = true;

    /// <summary>
    /// The gun tube sliding back on the shot - key '[', or --no-barrel-recoil.
    ///
    /// On by default, like the belts and the exhaust and for the same reason: it
    /// is a rendered layer, not an interpretation laid over one, so there is
    /// nothing to be cautious about. The switch earns its place as an A/B - the
    /// travel is about four pixels and whether that reads is a question you
    /// answer by turning it off, not by remembering.
    ///
    /// Off does not hide the layer. The tube holds its rest pose, which is
    /// exactly how every tank looked before it had one, so the comparison is
    /// against the old picture rather than against a tank with no gun.
    /// </summary>
    private bool _recoilTube = true;

    /// <summary>
    /// The hull rocking back on the shot - key ']', or --recoil-shear.
    ///
    /// **Off, and it is the only effect here that is off because something else
    /// replaced it.** The burning tank is off because burning is the exception;
    /// this is off because the gun now recoils for real. <see cref="Recoil"/>
    /// shears the rendered sprite about a pivot to fake a body that pitched,
    /// which is the same class of approximation as the turret yaw that could not
    /// be drawn at all: the sprite has no depth, so the second term of a rotation
    /// is unavailable and the vertical part has to be damped to a quarter to stop
    /// a rigid hull reading as rubber.
    ///
    /// The tube's recoil needs none of that - it is geometry that was rendered
    /// from twelve angles, so it is simply true. Leaving a shear on top of it
    /// means every shot shows one real movement and one invented one, and the
    /// invented one is the larger.
    ///
    /// Kept rather than deleted, for the painted flash sheet's reason: "the
    /// rendered one is better" stays an assertion until the two can be put side
    /// by side. Asking for a level with --recoil, or for a pivot with
    /// --recoil-turret, switches it back on - asking for the setting is asking
    /// for the effect.
    /// </summary>
    private bool _recoilShear = Recoil.ShearOnByDefault;

    private ExhaustLoop _exhaust => Active.Exhaust;
    /// <summary>Engine exhaust - key O, or --no-exhaust. On by default, and for
    /// the same reason as the tremble: an engine that is running is running, and
    /// a tank showing no sign of it reads as switched off. Unlike the tremble it
    /// costs nothing to be sure of - the plume is a layer that was rendered, not
    /// an interpretation laid over one.</summary>
    private bool _exhaustEnabled = true;

    private BurnLoop _burn => Active.Burn;
    /// <summary>The tank on fire - key J, or --burning. Off by default, and
    /// unlike the exhaust that is not a preference: a tank that is not burning
    /// is the normal case, and a harness that opens with one on fire would be
    /// showing the exception. It gets its own key rather than riding on the
    /// exhaust's so the two can be seen apart - they come off the same stamped
    /// port and it is worth being able to prove the layers are not the same
    /// layer.</summary>
    private bool _burning
    {
        get => Active.Burning;
        set => Active.Burning = value;
    }

    /// <summary>A shell arriving - key U. An event rather than a mode, so there
    /// is nothing to switch on: each press is one hit.</summary>
    private HitLoop _hit => Active.Hit;

    /// <summary>
    /// Which side of the hex the shooter is standing on, as an index into
    /// <see cref="HexField.EdgeHeadings"/>.
    ///
    /// Six bearings, not any bearing: a tank is hit from a neighbouring cell,
    /// and a neighbour is across a flat side. So the six directions a tank can
    /// drive in are exactly the six it can be shot from, and there is one list
    /// of them rather than a movement list and a gunnery list.
    ///
    /// It walked a free bearing in steps of 90 before this, seeded off the
    /// plate normals so the choice between two neighbouring plates got
    /// exercised. Two of the six now land square on a normal, which is not a
    /// regression but the grid: where the shooter can stand is not a knob. The
    /// boundary is asserted in the self-test at 44 and 46 degrees off, which
    /// never depended on the key anyway.
    ///
    /// Six against four plates means a lap cannot put one shell on each - two
    /// plates take two. It does reach all four from every hull heading, which
    /// is the property worth keeping and is checked.
    /// </summary>
    private int _hitSide = HexField.EdgeHeadings.Length - 1;   // U steps to 0 first

    private double HitFrom => HexField.EdgeHeadings[_hitSide];

    /// <summary>The hex side a bearing arrives on. Any angle from anywhere -
    /// the command line, a future game - is answered with one of the six.</summary>
    public static int SideFor(double bearing)
    {
        int best = 0;
        double bestGap = double.MaxValue;
        for (int i = 0; i < HexField.EdgeHeadings.Length; i++)
        {
            double gap = Math.Abs(Mod(HexField.EdgeHeadings[i] - bearing + 180.0, 360.0)
                                  - 180.0);
            if (gap < bestGap)
            {
                bestGap = gap;
                best = i;
            }
        }
        return best;
    }

    private int _hitCount
    {
        get => Active.HitCount;
        set => Active.HitCount = value;
    }

    /// <summary>Calibres, as a multiplier on the rendered hit - key Y.
    ///
    /// The layer is a sprite, so this is a scale on the drawn rect and costs
    /// nothing; what it cannot do is change the shape of the burst, only its
    /// size. Kept modest for that reason - past about half again the rendered
    /// size the dust starts to read as soft rather than as bigger, and the
    /// answer there is another render, not a bigger number.
    /// </summary>
    private static readonly float[] Calibres = { 0.7f, 1.0f, 1.4f };

    private int _calibre = 1;

    /// <summary>What the next shell will go off at.
    ///
    /// Read once, when the trigger goes, and handed to the hit - see
    /// <see cref="HitLoop.Scale"/>. It used to be written straight onto the tank
    /// and read again on every draw, which made it a property of the dial rather
    /// than of the round: turning it while the dust was settling resized a shell
    /// that had already landed. Same argument as the scatter, and the same
    /// answer.</summary>
    private float Calibre => Calibres[_calibre];

    /// <summary>How many levels of armour the loaded round goes through in one
    /// hit.
    ///
    /// The position in the calibre list rather than a table beside it: the
    /// calibres are ordered, there are as many of them as there are levels of
    /// damage, and "the smallest round does one level" is the whole rule. A
    /// second list would be a second thing to keep in step with the first.
    /// </summary>
    private int Bite => BiteFor(_calibre);

    /// <summary>How deep the nth calibre goes. Static so the self-test can
    /// assert the list of calibres and the levels of damage still line up -
    /// adding a fourth round or a fourth decal without the other is the way this
    /// stops meaning anything.</summary>
    public static int BiteFor(int calibre) => calibre + 1;

    public static int CalibreCount => Calibres.Length;

    /// <summary>Which of the three a scale asked for on the command line means.
    ///
    /// Snapped rather than taken as given, for the reason a bearing is snapped
    /// to a side of the hex: a gun has the calibres it has, and there is one
    /// list of them rather than a keyboard list and a flag list. An arbitrary
    /// number was accepted here before, and the cost was a dropdown reading
    /// 1.0x with a 1.4 round loaded - the control lying about what it would
    /// fire, which is the whole failure this pass is about.</summary>
    public static int CalibreFor(float scale)
    {
        int best = 0;
        for (int i = 1; i < Calibres.Length; i++)
            if (Math.Abs(Calibres[i] - scale) < Math.Abs(Calibres[best] - scale))
                best = i;
        return best;
    }

    private FlashSheet _flash = null!;
    private Recoil _recoil => Active.Recoil;
    /// <summary>Screen frames since the shot went off, or -1 between shots.</summary>
    private int _shotFrame
    {
        get => Active.ShotFrame;
        set => Active.ShotFrame = value;
    }

    private bool _spinning;
    private bool _aimWithMouse;

    // `godot --path <dir> -- --capture <file>` renders a few frames, saves a
    // screenshot and quits, so the harness can be checked without a human at
    // the keyboard. F12 does the same thing interactively.
    private string? _capturePath;
    private int _captureAfter = 4;
    private int _frames;
    private bool _selfTest;
    private Vector2I? _driveTo;
    // Prints one line of state per frame and quits. Picking a frame to
    // screenshot by arithmetic does not work - the pivot length depends on
    // which heading the pathfinder chose - so read it off instead.
    private int _traceFrames;
    private string? _startTag;
    private bool _fireAtStart;
    /// <summary>--hit &lt;deg&gt;: take one on the way in, from that bearing. A
    /// bearing rather than a plate name, because that is what the game has.</summary>
    private double? _hitAtStart;
    /// <summary>--attack &lt;n&gt;: the driven tank opens fire on vehicle n, by the
    /// index the number keys use.</summary>
    private int? _attackAtStart;
    /// <summary>--damage &lt;n&gt;: every plate already marked n levels deep, so a
    /// capture of a knocked-about tank does not need n presses of U.</summary>
    private int _damageAtStart;

    /// <summary>The side panel, and whether to build it at all.
    ///
    /// Off under --capture and --trace unless --ui says otherwise, because a
    /// capture is evidence: an A/B of two sprite renders must not differ by a
    /// panel that happened to be open. --ui forces it back on for a screenshot
    /// of the harness itself.</summary>
    private ControlPanel? _panel;
    private bool _noPanel;
    private bool _showPanel;
    private double? _startTurret;
    /// <summary>Which flash to start with - key V, or --flash sheet|rendered.
    /// Rendered by default: it is the one the pipeline exists to produce, and
    /// it falls back on its own for a tank that has none.</summary>
    private FlashSource _flashSource = FlashSource.Rendered;

    /// <summary>Recoil taken by the turret alone - key L, or --recoil-turret.
    /// Held here as well as on the tank because the flag is parsed before the
    /// tank exists, exactly as the flash source is.</summary>
    private bool _recoilTurretOnly;

    /// <summary>
    /// Three more flag values held until there is a tank to put them on:
    /// --burning, --tremble and --recoil.
    ///
    /// The reason is the one already written above for the flash source and the
    /// recoil mode, and it grew teeth when the harness went to three tanks: these
    /// three used to be written straight through the properties that mean "the
    /// tank being driven", and the argument loop runs before any tank exists. What
    /// used to be a write to a field that was simply there became an index into an
    /// empty list - the window opened, the scene never built, and --capture hung
    /// rather than failing. Anything a flag sets that belongs to a vehicle waits
    /// here until the vehicles are built.
    /// </summary>
    private bool _burnAtStart;
    private double _trembleAtStart = 1.0;
    private double _recoilAtStart = 1.0;
    private bool _rollOnly;

    private bool Moving => _pathStep < _path.Count;

    public override void _Ready()
    {
        string[] userArgs = OS.GetCmdlineUserArgs();
        for (int i = 0; i < userArgs.Length; i++)
        {
            if (userArgs[i] == "--capture" && i + 1 < userArgs.Length)
                _capturePath = userArgs[i + 1];
            else if (userArgs[i].StartsWith("--capture="))
                _capturePath = userArgs[i]["--capture=".Length..];
            else if (userArgs[i] == "--selftest")
                _selfTest = true;
            else if (userArgs[i] == "--capture-at" && i + 1 < userArgs.Length
                     && int.TryParse(userArgs[i + 1], out int at))
                _captureAfter = at;
            else if (userArgs[i] == "--pitch")
                _pitchEnabled = true;
            else if (userArgs[i] == "--rumble")
                _rumbleEnabled = true;
            else if (userArgs[i] == "--no-tremble")
                _trembleEnabled = false;
            else if (userArgs[i] == "--scan")
                _scanEnabled = true;
            else if (userArgs[i] == "--no-exhaust")
                _exhaustEnabled = false;
            else if (userArgs[i] == "--no-tracks")
                _tracksEnabled = false;
            else if (userArgs[i] == "--no-sound")
                _soundEnabled = false;
            else if (userArgs[i] == "--no-barrel-recoil")
                _recoilTube = false;
            else if (userArgs[i] == "--burning")
                _burnAtStart = true;
            else if (userArgs[i] == "--fire")
                _fireAtStart = true;
            else if (userArgs[i] == "--hit" && i + 1 < userArgs.Length
                     && double.TryParse(userArgs[i + 1], out double from))
                _hitAtStart = from;
            // Which tank the driven one opens fire on, by the same index the
            // number keys use. A capture of an engagement otherwise needs a hand
            // on the mouse, which is the one thing --capture exists to avoid.
            else if (userArgs[i] == "--attack" && i + 1 < userArgs.Length
                     && int.TryParse(userArgs[i + 1], out int quarry))
                _attackAtStart = quarry;
            // Invariant culture, and this is the first flag that needs saying
            // so: the machine is set to a locale whose decimal mark is a comma,
            // so a plain TryParse of "1.4" fails, leaves the default in place
            // and produces two identical captures from two different calibres -
            // which reads as "the scale does nothing" rather than as a parse.
            else if (userArgs[i] == "--damage" && i + 1 < userArgs.Length
                     && int.TryParse(userArgs[i + 1], out int deep))
                _damageAtStart = deep;
            else if (userArgs[i] == "--no-ui")
                _noPanel = true;
            else if (userArgs[i] == "--ui")
                _showPanel = true;
            // Snapped to one of the three, like --hit is snapped to a side of
            // the hex: the gun has the calibres it has, and the flag picks from
            // the same list the key and the dropdown do.
            else if (userArgs[i] == "--hit-scale" && i + 1 < userArgs.Length
                     && float.TryParse(userArgs[i + 1], NumberStyles.Float,
                         CultureInfo.InvariantCulture, out float calibre))
                _calibre = CalibreFor(calibre);
            // The panel sliders, on the command line for the same reason the
            // calibre is: a slider is judged by a picture, and taking that
            // picture twice at two settings cannot need a hand on the mouse.
            else if (userArgs[i] == "--size" && i + 1 < userArgs.Length
                     && double.TryParse(userArgs[i + 1], NumberStyles.Float,
                         CultureInfo.InvariantCulture, out double sizeLevel))
                _sizeLevel = sizeLevel;
            else if (userArgs[i] == "--tremble" && i + 1 < userArgs.Length
                     && double.TryParse(userArgs[i + 1], NumberStyles.Float,
                         CultureInfo.InvariantCulture, out double trembleLevel))
                _trembleAtStart = trembleLevel;
            // Both of these turn the shear back on as well as configuring it.
            // It is off by default now that the tube recoils for real, and a flag
            // that set a level on a switched-off effect would look like a flag
            // that did not arrive - the same failure --hit-scale had when the
            // decimal separator ate it.
            else if (userArgs[i] == "--recoil" && i + 1 < userArgs.Length
                     && double.TryParse(userArgs[i + 1], NumberStyles.Float,
                         CultureInfo.InvariantCulture, out double recoilLevel))
            {
                _recoilAtStart = recoilLevel;
                _recoilShear = true;
            }
            // A mode rather than a level, and it needs a flag for the reason the
            // two slider levels do: the pair is judged by a screenshot, and
            // taking one on each setting should not need a hand on the keyboard.
            else if (userArgs[i] == "--recoil-turret")
            {
                _recoilTurretOnly = true;
                _recoilShear = true;
            }
            // The plain switch, for looking at the shear against the tube that
            // replaced it without also changing its level or its pivot.
            else if (userArgs[i] == "--recoil-shear")
                _recoilShear = true;
            else if (userArgs[i] == "--flash" && i + 1 < userArgs.Length)
                _flashSource = userArgs[i + 1].Equals("sheet",
                    StringComparison.OrdinalIgnoreCase)
                    ? FlashSource.Sheet
                    : FlashSource.Rendered;
            else if (userArgs[i] == "--turret" && i + 1 < userArgs.Length
                     && double.TryParse(userArgs[i + 1], out double bearing))
                _startTurret = bearing;
            else if (userArgs[i] == "--tank" && i + 1 < userArgs.Length)
                _startTag = userArgs[i + 1].ToUpperInvariant();
            else if (userArgs[i] == "--roll-only")
            {
                // Isolates the roll by silencing the heave. The two cannot be
                // told apart in a screenshot otherwise: a vertical jolt shifts
                // content between rows, and where the silhouette narrows - the
                // gun, the turret - that reads as a horizontal shift of over
                // two pixels which has nothing to do with roll.
                //
                // The amplitude waits for the tanks to exist, like the other
                // per-vehicle flag values above.
                _rumbleEnabled = true;
                _rollOnly = true;
            }
            else if (userArgs[i] == "--trace" && i + 1 < userArgs.Length
                     && int.TryParse(userArgs[i + 1], out int frames))
                _traceFrames = frames;
            else if (userArgs[i] == "--drive" && i + 1 < userArgs.Length)
            {
                string[] parts = userArgs[i + 1].Split(',');
                if (parts.Length == 2
                    && int.TryParse(parts[0], out int col)
                    && int.TryParse(parts[1], out int row))
                {
                    _driveTo = new Vector2I(col, row);
                    _captureAfter = 90;   // long enough to be mid-path
                }
            }
        }

        // A capture is evidence, so it gets no panel unless one is asked for.
        if ((_capturePath is not null || _traceFrames > 0) && !_showPanel)
            _noPanel = true;

        var failures = new List<string>();
        foreach (string tag in Tags)
        {
            AtlasSet atlas = AtlasSet.Load(SpritesRoot, tag,
                                           SharedSpriteDir ?? tag);
            if (atlas.Error.Length > 0)
                failures.Add($"{tag}: {atlas.Error}");
            else
            {
                _atlases[tag] = atlas;
                _loaded.Add(tag);
            }
        }

        _flash = FlashSheet.Load($"{SpritesRoot}/Fire_rgba.png");
        if (_flash.Error.Length > 0)
            failures.Add($"flash: {_flash.Error}");

        // Sound is loaded but never required. A missing set does not go into
        // `failures`, because that list is about a bench that cannot show what it
        // was asked to show, and this one still can - it just does it quietly.
        // Printed instead, once per set, so "I hear nothing" has an answer in the
        // log rather than in the source.
        _commonSounds = SoundSet.Load(SoundsRoot, SoundSet.CommonTag);
        GD.Print(_commonSounds.Summary());
        foreach (string tag in _loaded)
        {
            SoundSet set = SoundSet.Load(SoundsRoot, tag);
            GD.Print(set.Summary());
            _sounds[tag] = set;
        }

        _field = new HexField();
        AddChild(_field);

        // One vehicle per atlas that loaded, parked along HomeCells. Built here
        // rather than swapped later: each one keeps its own atlas for good, so
        // nothing has to be reconfigured when the selection changes - which is
        // most of what switching class used to be, and every line of it was a
        // chance for one clock to be left pointing at the previous tank.
        for (int i = 0; i < _loaded.Count; i++)
        {
            string tag = _loaded[i];
            var sprite = new TankSprite
            {
                Flash = _flash, Source = _flashSource,
                RecoilTurretOnly = _recoilTurretOnly,
                Atlas = _atlases[tag],
            };
            AddChild(sprite);
            var vehicle = new Vehicle
            {
                Tag = tag,
                Atlas = _atlases[tag],
                Sprite = sprite,
                Profile = MovementProfile.For(tag),
                HomeCell = HomeCells[Math.Min(i, HomeCells.Length - 1)],
            };
            vehicle.Cell = vehicle.HomeCell;
            _vehicles.Add(vehicle);
            // Phases come off each layer, never assumed: the renderer's count is
            // a config value, and a clock that wraps anywhere but at the seam
            // pops there.
            vehicle.Track.Phases = vehicle.Atlas.TrackPhases;
            vehicle.Track.Pitch = vehicle.Atlas.TrackPitch;
            vehicle.Exhaust.Phases = vehicle.Atlas.ExhaustPhases;
            vehicle.Burn.Phases = vehicle.Atlas.BurnPhases;
            // The tube's poses, read off the layer like every other count: the
            // renderer picks it from the travel, about one phase per pixel of
            // stroke, so a hard-coded table here would drop the last pose the
            // day the travel changes.
            vehicle.Barrel.Phases = vehicle.Atlas.RecoilPhases;
            // Per class, so a heavy at its own cruise is as worked as a light at
            // its own instead of idling along because it happens to be slower.
            vehicle.Exhaust.TopSpeed = vehicle.Profile.TopSpeed;
            vehicle.Tremble.TopSpeed = vehicle.Profile.TopSpeed;
            // and the rumble reaches full strength at half cruise, whatever
            // cruise is for this class
            vehicle.Rumble.FullSpeed = vehicle.Profile.TopSpeed * 0.5;

            // Its own players, for the reason its clocks are its own: one shared
            // engine node would put three tanks in phase, and three engines
            // beating as one is one engine three times as loud. Skipped entirely
            // when nothing loaded rather than built mute, so a silent bench has
            // no audio nodes at all.
            SoundSet own = _sounds.TryGetValue(tag, out SoundSet? s)
                ? s : SoundSet.Load(SoundsRoot, tag);
            if (own.Any || _commonSounds.Any)
            {
                var audio = new VehicleAudio
                {
                    Own = own,
                    Common = _commonSounds,
                    Enabled = _soundEnabled && !_selfTest,
                };
                AddChild(audio);
                vehicle.Audio = audio;
            }
        }

        // Judging whether two layers line up is a pixel-level question, so the
        // harness has to be able to get close. Sprites are drawn at 1:1 by
        // default; zoom only changes the view, never the atlas sampling.
        _camera = new Camera2D { Position = new Vector2(760, 500), Enabled = true };
        AddChild(_camera);

        var layer = new CanvasLayer();
        AddChild(layer);
        // The only thing left on the scene itself: the message that says nothing
        // loaded. Everything the corner used to carry - the key list, and the
        // live numbers beside it - is in the panel now, where the numbers sit
        // next to the control that moves them instead of in a block that had to
        // be read against one. A run with --no-ui gets neither, which is what a
        // capture wants.
        _hud = new Label { Position = new Vector2(16, 12) };
        _hud.AddThemeColorOverride("font_color", new Color(0.92f, 0.95f, 1.0f));
        _hud.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.85f));
        _hud.AddThemeConstantOverride("outline_size", 5);
        layer.AddChild(_hud);

        if (_loaded.Count == 0)
        {
            _hud.Text = $"No atlases loaded from {SpritesRoot}\n\n"
                        + string.Join("\n", failures);
            return;
        }
        if (failures.Count > 0)
            GD.PushWarning("some atlases failed: " + string.Join(", ", failures));

        if (_startTag is not null && _loaded.IndexOf(_startTag) >= 0)
            _active = _loaded.IndexOf(_startTag);
        // Clamped because how many tanks are on disk decides how many there are:
        // the default points at the medium, and a run with only one atlas present
        // must select that one rather than throw.
        _active = Math.Clamp(_active, 0, _vehicles.Count - 1);
        // The field takes its tile from a tank, and now there are three of them.
        // The medium's, always: the grid is one grid, so it cannot follow the
        // selection, and the reference tank is the one drawn as rendered.
        _field.Atlas = _vehicles[Math.Min(1, _vehicles.Count - 1)].Atlas;
        ApplySize();
        foreach (Vehicle vehicle in _vehicles)
            Park(vehicle);
        // The flag may have picked a tank other than the first, and the trim is
        // set on selection - so it has to be set once at the start too, or two of
        // the three come up forward until something is clicked.
        FocusSound();

        // The mark on whoever is being driven. Absent under --capture and --trace
        // for the reason the panel is: a capture is evidence, and an A/B of two
        // renders must not differ by a marker. --ui brings both back.
        if (!_noPanel)
        {
            _ring = new SelectionRing { Field = _field, Target = Active };
            AddChild(_ring);
            _targetRing = new SelectionRing
            {
                Field = _field,
                Target = null,
                Ink = SelectionRing.Hostile,
                // Tighter than the selection's, so on the day a tank is both
                // yours and somebody's target the two rings are two rings.
                Inset = 0.70f,
            };
            AddChild(_targetRing);
        }

        if (_selfTest)
        {
            int failed = SelfTest.Run(_field, _tank, _atlases, _vehicles, _active,
                                      _ring, _commonSounds);
            GetTree().Quit(failed == 0 ? 0 : 1);
            return;
        }
        // After the self-test returns, so a headless run never builds it, and
        // before the start-up flags, so the panel's first Sync sees what they
        // did rather than the defaults.
        if (!_noPanel)
        {
            _panel = new ControlPanel();
            layer.AddChild(_panel);
            _panel.AddHandle();
            BuildPanel();
        }
        // The flag values that had to wait for a tank to exist. The two levels go
        // on every tank: they are harness settings, and a slider judged on one
        // tank while the other two ran at a different amplitude would be measuring
        // the difference. Burning goes on the selected one alone, because that is
        // what J does and what makes a burning tank next to an intact one possible.
        foreach (Vehicle vehicle in _vehicles)
        {
            vehicle.Tremble.Level = _trembleAtStart;
            vehicle.Recoil.Level = _recoilAtStart;
            if (_rollOnly)
                vehicle.Rumble.Amplitude = 0.0;
        }
        Active.Burning = _burnAtStart;
        if (_startTurret is not null)
            _tank.TurretFacing = Mod(_startTurret.Value, 360.0);
        for (int i = 0; i < _damageAtStart; i++)
            foreach (string face in _tank.Atlas?.HitFaces
                                    ?? (IReadOnlyList<string>)Array.Empty<string>())
                _tank.Damage(face, ScatterAt(++_hitCount));
        if (_driveTo is not null)
            OrderMoveTo(_field.ClampCell(_driveTo.Value));
        if (_attackAtStart is int foe && foe >= 0 && foe < _vehicles.Count)
            Engage(Active, _vehicles[foe]);
        if (_fireAtStart)
            Fire();
        if (_hitAtStart is not null)
            TakeHit(_hitAtStart.Value);
    }

    private string CurrentTag() => Active.Tag;

    /// <summary>
    /// Take control of one of the tanks.
    ///
    /// All it does is move the selection. Switching class used to rebuild every
    /// clock and clear the damage, because one tank was wearing another tank's
    /// atlas; now each vehicle keeps its own for good, so a burning tank left
    /// selected stays burning, a holed plate stays holed, and coming back to a
    /// tank finds it as it was left. That is the property the bench needs - three
    /// tanks in three different states, side by side.
    /// </summary>
    private void Select(int index)
    {
        if (index < 0 || index >= _vehicles.Count)
            return;
        _active = index;
        // The ring marks whoever is being driven, so it is set here and nowhere
        // else - it follows the tank's own contact patch from there, including
        // while it drives.
        if (_ring is not null)
            _ring.Target = Active;
        // The highlight belongs to whoever is being driven, so it follows the
        // selection rather than staying on the last order given.
        _field.Highlight = Active.Path.GetRange(
            Math.Min(Active.PathStep, Active.Path.Count),
            Active.Path.Count - Math.Min(Active.PathStep, Active.Path.Count));
        _field.QueueRedraw();
        // The gunnery marks belong to the driven tank too, for the same reason
        // the route does: every tank may be engaging something, and what is
        // painted on the ground is what the tank in hand can see.
        PaintGunnery();
        FocusSound();
        _panel?.Sync();
        _tank.QueueRedraw();
    }

    /// <summary>
    /// Point a tank's gun at another one, or at nobody.
    ///
    /// One way in, called by the right button, by the panel and by --attack, so
    /// the three cannot disagree about what a target is. A tank cannot be given
    /// itself: right-clicking your own tank means "stop shooting", which is the
    /// same shape as left-clicking it meaning "stay put".
    /// </summary>
    private void Engage(Vehicle shooter, Vehicle? target)
    {
        shooter.Target = ReferenceEquals(shooter, target) ? null : target;
        shooter.Solution = Gunnery.None;
        if (shooter == Active)
        {
            // Both of these drive the turret and would fight the gun for it,
            // which is the same reason a move order takes them back.
            _aimWithMouse = false;
            _spinning = false;
        }
        PaintGunnery();
    }

    /// <summary>
    /// The lanes, the live one and the ring under the target - all off the driven
    /// tank, all recomputed together.
    ///
    /// The six arcs are only drawn once there is a target, and that is the point
    /// of them: with nobody to shoot at they are clutter, and with a target that
    /// is not on a lane they are the answer to the question the player now has,
    /// which is not "why did it not fire" but "where do I have to drive".
    /// </summary>
    private void PaintGunnery()
    {
        if (_targetRing is not null)
            _targetRing.Target = Active.Target;
        if (Active.Target is null)
        {
            _field.Arcs = Array.Empty<Vector2I>();
            _field.Aim = Array.Empty<Vector2I>();
            _field.QueueRedraw();
            return;
        }
        var arcs = new List<Vector2I>();
        foreach (int heading in HexField.EdgeHeadings)
            foreach (Vector2I cell in _field.Lane(Active.Cell, heading))
            {
                arcs.Add(cell);
                // Stops at the first tank, that one included. A lane painted
                // straight through a hull would promise reach the shell has not
                // got, and where it stops is the same "blocked" the solution
                // reports - said on the ground, where it does not have to be
                // read.
                if (Vehicle.At(_vehicles, cell) is not null)
                    break;
            }
        _field.Arcs = arcs;
        Shot shot = Gunnery.Solve(_field, _vehicles, Active, Active.Target);
        _field.Aim = shot.Clear
            ? _field.Lane(Active.Cell, shot.Heading, shot.Range)
            : Array.Empty<Vector2I>();
        _field.QueueRedraw();
    }

    /// <summary>
    /// Bring the driven tank's voice forward and set the other two back.
    ///
    /// Set here beside the ring, and for the same reason it is: both say which
    /// tank is yours, and one place to say it is one place for the two to
    /// disagree. The ring can put its mark on the ground because the sprites are
    /// what is being judged and must not be touched; sound has no ground, and
    /// three engines at one level sum into one, so the only mark available is
    /// level. See <see cref="VehicleAudio.Focused"/>.
    /// </summary>
    private void FocusSound()
    {
        for (int i = 0; i < _vehicles.Count; i++)
            if (_vehicles[i].Audio is not null)
                _vehicles[i].Audio!.Focused = i == _active;
    }

    /// <summary>
    /// Which tank a click on a cell means, or -1 for "no tank - this is a move
    /// order".
    ///
    /// A cell rather than the sprite's pixels: a tank occupies a cell, that is the
    /// unit the game is played in, and the two readings differ only where a
    /// silhouette overhangs its own hex - where a pixel test would let you select
    /// a tank by clicking the ground beside it.
    ///
    /// The tank already being driven does not count, so clicking the one you are
    /// driving is an order to stay put rather than a selection that does nothing.
    /// Static and pure so the routing can be asserted: "a click on an occupied
    /// cell selects instead of ordering" is the whole feature.
    /// </summary>
    public static int SelectionFor(IReadOnlyList<Vehicle> vehicles, int active,
                                  Vector2I cell)
    {
        for (int i = 0; i < vehicles.Count; i++)
            if (i != active && vehicles[i].Cell == cell)
                return i;
        return -1;
    }

    /// <summary>
    /// The class's size onto the tank, and onto the one thing outside it that
    /// has to follow.
    ///
    /// The belt's link is a length of ground, so a tank drawn at 0.85 has a
    /// 0.85 link and must wind that much sooner or the tread slips by exactly
    /// the factor the tank was scaled by - the one coupling <see
    /// cref="TankSprite.BodyScale"/> cannot take care of by scaling the node.
    /// It moves <see cref="TrackLoop.SyncSpeed"/> with it, which is right and
    /// visible: a bigger tank has a coarser link and so stays in step further up
    /// its speed range.
    ///
    /// Speeds are not touched. They are screen px/s per class, and how large a
    /// tank is drawn is not how fast it goes.
    /// </summary>
    private void ApplySize()
    {
        foreach (Vehicle vehicle in _vehicles)
        {
            float was = vehicle.Sprite.BodyScale;
            var now = (float)(vehicle.Profile.Size * _sizeLevel);
            if (now == was)
                continue;
            vehicle.Sprite.BodyScale = now;
            vehicle.Track.Scale = now;
            // Hold the contact patch still. The scale pivots on the anchor, which
            // floats above the ground, so resizing alone would lift or sink the
            // tank - see StandOn. Corrected by the change rather than re-parked,
            // so dragging the dial while a tank is under way does not snap it back
            // to the cell it last reached.
            vehicle.Sprite.Position -= vehicle.Atlas.GroundOffset * (now - was);
        }
    }

    /// <summary>Put a tank on its cell.
    ///
    /// The z index comes off the cell's screen height, which is the whole of the
    /// overlap rule: a tank further down the screen is nearer the camera and draws
    /// over one behind it. Tree order cannot say that - it is fixed at build time
    /// and the tanks move - and without it the tank that happens to have been
    /// created last wins every overlap, which reads as one driving through
    /// another.</summary>
    /// <summary>
    /// Where a tank's sprite has to sit for the tank to be standing on a cell.
    ///
    /// On its <em>contact patch</em>, not on its anchor, and that distinction is
    /// worth the method. Every layer of one tank shares the anchor, so drawing a
    /// tank at its cell's anchor is what keeps hull, turret and belts together -
    /// but the anchor is the turret axis lifted to the mid-height of the fitted
    /// bounds, so it floats 50 to 59 px above the ground. Two things then move the
    /// tank off the ground the field is drawing, and both were introduced here
    /// rather than by the renderer, which centres tile and footprint to within
    /// two pixels:
    ///
    /// - the size scales about the anchor, so a tank drawn at 0.85 has that 51px
    ///   of float shortened to 43 and stands 8px high in its hex, and a 1.15 one
    ///   sinks 8px into it;
    /// - the field draws one tile under all three, and the tiles do not agree on
    ///   where the ground is: 49.7px below the anchor on the light's, 59.0 on the
    ///   medium's, which is another 9px.
    ///
    /// They happened to cancel on the heavy and to add on the light, so the tank
    /// that looked wrong was the one where the two errors agreed - which is why
    /// this reads as "sometimes off centre" rather than as an offset.
    ///
    /// Asking instead for the ground point to land on the cell's ground centre
    /// answers both at once, at any size and with any tile under it.
    /// </summary>
    private Vector2 StandOn(Vehicle vehicle, Vector2I cell) =>
        _origin + _field.CellAnchor(cell) + _field.CentreOffset
        - vehicle.Atlas.GroundOffset * vehicle.Sprite.BodyScale;

    private void Park(Vehicle vehicle)
    {
        vehicle.Cell = _field.ClampCell(vehicle.Cell);
        _field.Position = _origin;
        vehicle.Sprite.Position = StandOn(vehicle, vehicle.Cell);
        Depth(vehicle);
        // Belt travel is read back off the heading rather than reported by
        // whatever turned it, so the baseline has to be laid down wherever the
        // heading is set from outside. Left stale, the first frame after a reset
        // sees the whole of the difference as one frame's swing and the belts
        // jump.
        vehicle.LastHullFacing = vehicle.Sprite.HullFacing;
        vehicle.Sprite.QueueRedraw();
    }

    /// <summary>The overlap order, off where the tank actually is rather than off
    /// the cell it last reached. Taken from the live position because a tank spends
    /// most of a move between two cells: stamped on arrival only, one crossing the
    /// row of another would hold the wrong order for the whole step, which is
    /// exactly the frame you would be looking at.
    ///
    /// From the contact patch, not from the sprite's origin, for the same reason
    /// <see cref="StandOn"/> exists: what decides which tank is nearer the camera
    /// is where each one stands, and the origin sits a scaled float above that. Off
    /// the origin, three tanks on one row got three different depths - the light by
    /// 16 - and the order between them was decided by their sizes.</summary>
    private void Depth(Vehicle vehicle) =>
        vehicle.Sprite.ZIndex =
            Mathf.RoundToInt(vehicle.GroundPoint.Y - _origin.Y);

    private void SnapToCell() => Park(Active);

    /// <summary>
    /// A global effect toggle reaching every tank.
    ///
    /// These five are settings of the harness, not of a vehicle: "show me the
    /// ground rumble" is a question about the effect, and answering it on one tank
    /// while the other two sat still would make the comparison the toggle exists
    /// for impossible. Burning is the exception and stays per tank - see
    /// <see cref="Vehicle.Burning"/>.
    ///
    /// One method per toggle, called by both the key and the panel row, so the two
    /// cannot come to mean different things.
    /// </summary>
    private void PitchChanged()
    {
        foreach (Vehicle v in _vehicles)
            UpdatePitch(v, 0.0, 0.0);
    }

    private void RumbleChanged()
    {
        foreach (Vehicle v in _vehicles)
            UpdateRumble(v, 0.0);
    }

    private void TrembleChanged()
    {
        foreach (Vehicle v in _vehicles)
            UpdateTremble(v, 0.0);
    }

    private void ExhaustChanged()
    {
        foreach (Vehicle v in _vehicles)
            UpdateExhaust(v, 0.0);
    }

    private void TracksChanged()
    {
        foreach (Vehicle v in _vehicles)
            UpdateTracks(v, 0.0, 0.0);
    }

    /// <summary>
    /// Roughly half the track gauge, as a fraction of the hull's broadside span.
    ///
    /// An estimate, and it only has to be one: it decides how fast the belts wind
    /// during a pivot, and the difference between "the tracks are working" and
    /// "the tank is turning on frozen tracks" is not a few per cent. Nothing in
    /// the atlas measures the gauge - <see cref="AtlasSet.HullSpan"/> is the
    /// widest frame, which is the hull's length - so this is a proportion rather
    /// than a measurement, and it is named here instead of buried in the
    /// expression so it can be argued with.
    /// </summary>
    private const double PivotRadiusFraction = 0.25;

    /// <summary>
    /// Ground the belts covered this frame: what the tank drove, plus what it
    /// swung.
    ///
    /// The second half was missing and it showed. A tank pivoting from a standing
    /// start has <c>Speed</c> of zero - the cornering crawl is a floor, not a
    /// target - and so did the belts, so the hull swung round on tracks that were
    /// not moving. Turning on the spot is the most track-heavy thing a tank does
    /// and it was the one case with no belt motion at all.
    ///
    /// One number rather than a separate path for the sound, which is the whole
    /// reason it is fixed here: <see cref="TrackLoop"/> takes the travel, the
    /// sprite takes its phase from that and <see cref="VehicleAudio"/> takes its
    /// rate from the same clock, so the belts cannot be heard turning while they
    /// are seen standing still.
    ///
    /// Unsigned in the pivot term, and that is the part still owed. A real pivot
    /// runs the two belts in opposite directions; the sprite carries one phase for
    /// both (<see cref="TankSprite.TrackPhase"/>), so they can only wind together
    /// and one of them is going the wrong way. Both moving is still nearer the
    /// truth than both frozen - the error is a sign on one belt rather than a
    /// missing motion on two - and closing it properly means a phase per side,
    /// which the atlas is already ready for: track_left and track_right are
    /// separate layers.
    /// </summary>
    private double BeltTravel(Vehicle v, double delta)
    {
        double swing = WrapAngle(v.Sprite.HullFacing - v.LastHullFacing);
        v.LastHullFacing = v.Sprite.HullFacing;
        double radius = v.Atlas.HullSpan * PivotRadiusFraction * v.Sprite.BodyScale;
        double pivot = Math.Abs(Mathf.DegToRad(swing)) * radius;
        // The swing takes the drive's sign rather than always adding, so a tank
        // reversing round a corner does not have its belts partly cancelled by
        // its own turn.
        double drive = v.Speed * delta;
        return drive + (drive < 0.0 ? -pivot : pivot);
    }

    private void ScanChanged()
    {
        if (_scanEnabled)
            return;
        foreach (Vehicle v in _vehicles)
            v.Scan.Reset();
    }

    /// <summary>Push the switch to every tank. Setting the flag alone would leave
    /// the loops running: they are gated by volume rather than stopped, so that
    /// nudging the stick does not restart the belt from sample zero.</summary>
    private void SoundChanged()
    {
        foreach (Vehicle v in _vehicles)
            if (v.Audio is not null)
                v.Audio.Enabled = _soundEnabled;
    }

    /// <summary>
    /// The gunnery field for <c>--trace</c> and for the panel.
    ///
    /// It reports the reason rather than a yes or no, because from outside a
    /// tank that is not shooting looks the same whether it has no line, has
    /// somebody in the way, is still swinging its gun round or is simply
    /// reloading - and the first two are answered by driving somewhere else
    /// while the last two are answered by waiting.
    /// </summary>
    private string AimLine()
    {
        Vehicle v = Active;
        if (v.Target is null)
            return "-";
        if (!v.Solution.OnLane)
            return $"{v.Target.Tag} no lane";
        string state = v.Solution.BlockedAt is Vector2I b ? $"blocked ({b.X},{b.Y})"
            : v.Moving ? "moving"
            : !Gunnery.Laid(v.Sprite.TurretFacing, v.Solution.Heading) ? "laying"
            : v.ReloadLeft > 0.0 ? $"loading {v.ReloadLeft:F1}s"
            : "ready";
        // What the shooting has achieved, which is otherwise unreadable from
        // outside: the trace prints the driven tank, and in an engagement the
        // driven tank is usually the one doing the damage rather than taking it.
        // The plate this lane lands on, how deep this gun can ever get into it,
        // and how deep it has got - so a light tank stuck at 0/0 on a heavy is
        // the matchup working rather than the shells missing.
        string face = v.Target.Atlas.FaceFor(
            Mod(v.Solution.Heading + 180.0, 360.0), v.Target.Sprite.HullFacing);
        int cap = Gunnery.Penetration(v.Profile, v.Target.Profile);
        return $"{v.Target.Tag}@{v.Solution.Heading}deg/{v.Solution.Range} {state}"
               + $" {face} {v.Target.Sprite.ScarLevel(face)}/{cap}";
    }

    /// <summary>
    /// The audio field for <c>--trace</c>: what was asked for, then what came out.
    ///
    /// The peak is read off the master bus rather than inferred from the gates,
    /// and that is the whole value of the line. Gates are this code's own opinion
    /// of how loud it is being; the peak is the mixer's, and the two part company
    /// exactly where the interesting failures are - a stream that loaded but is
    /// silent, a device that never opened, a loop paused at the wrong moment.
    /// </summary>
    private string SoundLine()
    {
        VehicleAudio? a = Active.Audio;
        if (a is null)
            return "none";
        float peak = Mathf.Max(AudioServer.GetBusPeakVolumeLeftDb(0, 0),
                               AudioServer.GetBusPeakVolumeRightDb(0, 0));
        // Every tank's trim, not just the driven one's, because the whole claim
        // is about the *balance* between them: one number cannot show that two
        // others moved back.
        var trims = new List<string>();
        foreach (Vehicle vehicle in _vehicles)
            trims.Add(vehicle.Audio is null ? "--" : $"{vehicle.Audio.AppliedDb:F0}");
        return $"{(_soundEnabled ? "on " : "off")}"
               + $" eng {a.EngineGate:F2}@{a.EnginePitch:F2}"
               + $" trk {a.TrackGate:F2}@{a.TrackPitch:F2}"
               + $" brn {a.BurnGate:F2}"
               + $" mix[{string.Join("/", trims)}]"
               + $" peak {(float.IsNegativeInfinity(peak) ? -99.0f : peak),6:F1}dB";
    }

    // --- orders ------------------------------------------------------------

    private void OrderMoveTo(Vector2I target)
    {
        if (!_field.InBounds(target) || target == _cell)
            return;
        // Round the others rather than through them. A blocked destination gives
        // no path at all, which is why a click on an occupied cell is read as a
        // selection before it ever gets here.
        _path = _field.FindPath(_cell, target,
                                Vehicle.Occupied(_vehicles, Active));
        _pathStep = 0;
        // Mouse aim would override both turret modes and make the feature look
        // broken while the tank drives, so an order takes the turret back.
        _aimWithMouse = false;
        _spinning = false;
        _field.Highlight = _path;
        _field.QueueRedraw();
    }

    private void CancelOrder() => CancelOrder(Active);

    private void CancelOrder(Vehicle v)
    {
        v.Path = new List<Vector2I>();
        v.PathStep = 0;
        v.Speed = 0.0;
        if (v != Active)
            return;
        _field.Highlight = Array.Empty<Vector2I>();
        _field.QueueRedraw();
    }

    /// <summary>Distance left in the current straight run, and the speed to be
    /// doing by the end of it.
    ///
    /// The run stops at the first bend on purpose: the tank has to slow there
    /// to swing round, so looking past the bend would start the braking too
    /// late. What it slows *to* depends on what the bend is: the crawl at a
    /// corner, a full stop only at the destination.</summary>
    private (double Distance, double EndSpeed) RemainingRun(Vehicle v)
    {
        double total = (StandOn(v, v.Path[v.PathStep]) - v.Sprite.Position).Length();
        int heading = HexField.HeadingTo(v.Cell, v.Path[v.PathStep]);
        int i = v.PathStep;
        for (; i + 1 < v.Path.Count; i++)
        {
            if (HexField.HeadingTo(v.Path[i], v.Path[i + 1]) != heading)
                break;
            total += (_field.CellAnchor(v.Path[i + 1])
                      - _field.CellAnchor(v.Path[i])).Length();
        }
        bool endOfPath = i + 1 >= v.Path.Count;
        return (total, endOfPath ? 0.0 : v.Profile.CornerSpeed);
    }

    private void AdvanceOrder(Vehicle v, double delta)
    {
        Vector2I next = v.Path[v.PathStep];
        int heading = HexField.HeadingTo(v.Cell, next);
        if (heading < 0)
        {
            CancelOrder(v);     // path went stale - do not drive off the grid
            return;
        }

        double diff = WrapAngle(heading - v.Sprite.HullFacing);
        double accelRatio;

        if (Math.Abs(diff) > 0.5)
        {
            // Slow to the cornering crawl and swing round while still creeping.
            // The crawl is a floor, never a target to speed up to, so a standing
            // start still pivots in place - which is what a tank does - while a
            // bend taken at speed stays continuous.
            double crawl = v.Profile.CornerSpeed;
            accelRatio = v.Speed > crawl ? -1.0 : 0.0;
            if (v.Speed > crawl)
                v.Speed = Math.Max(crawl, v.Speed - v.Profile.Accel * delta);
            double budget = v.Profile.TurnRate * delta;
            v.Sprite.TurnHull(Math.Abs(diff) <= budget
                ? diff
                : Math.Sign(diff) * budget);
        }
        else
        {
            (double remaining, double endSpeed) = RemainingRun(v);
            // A ceiling rather than a brake pedal, and the difference is the
            // whole of why a tank used to arrive still doing a third of its
            // cruise and then stop dead in one frame.
            //
            // The old form asked "is the distance left below the braking
            // distance" once a frame and then applied full retardation. The test
            // is only as fine as the step: at cruise the tank covers 4px between
            // asks, so braking began up to 4px late, and 4px of missed braking is
            // sqrt(2 * 420 * 4) = 58px/s still on the clock at the goal. Measured
            // at 72. Nothing was wrong with the arithmetic - the trigger was late,
            // and a trigger can always be late.
            //
            // This is the same curve read the other way round: the fastest this
            // tank may be going and still be able to stop in what is left. It has
            // no moment of engagement to miss, and as `remaining` runs out the
            // ceiling runs down to `endSpeed` on its own, so the deceleration
            // finishes instead of being cut off by arrival.
            double ceiling = Math.Sqrt(endSpeed * endSpeed
                                       + 2.0 * v.Profile.Accel * Math.Max(remaining, 0.0));
            double before = v.Speed;
            v.Speed = Math.Min(
                Math.Min(v.Speed + v.Profile.Accel * delta, v.Profile.TopSpeed),
                ceiling);
            // What the body actually felt, rather than a flag saying which branch
            // was taken. The pitch is driven by acceleration, and under the
            // ceiling the retardation varies instead of being all or nothing -
            // reporting -1 throughout would give the nose a constant dip where
            // the tank has a smooth one.
            accelRatio = delta > 0.0
                ? Math.Clamp((v.Speed - before) / (v.Profile.Accel * delta), -1.0, 1.0)
                : 0.0;
        }

        UpdatePitch(v, accelRatio, delta);
        UpdateRumble(v, delta);

        // The same StandOn the parking uses, or the tank would drive to where its
        // anchor belongs and stop the height of its float short of the cell.
        Vector2 goal = StandOn(v, next);
        Vector2 to = goal - v.Sprite.Position;
        var budgetPx = (float)(v.Speed * delta);
        if (to.Length() <= budgetPx || (budgetPx <= 0.0f && to.Length() < 0.5f))
        {
            v.Cell = next;
            v.PathStep++;
            // Park rather than a bare Position: arriving in a cell is also where
            // the depth order changes, and a tank that drove past another without
            // its z index following would pass through it.
            Park(v);
            if (!v.Moving && v == Active)
            {
                _field.Highlight = Array.Empty<Vector2I>();
                _field.QueueRedraw();
            }
        }
        else if (budgetPx > 0.0f)
        {
            v.Sprite.Position += to.Normalized() * budgetPx;
            Depth(v);
        }
        v.Sprite.QueueRedraw();
    }

    private void UpdatePitch(Vehicle v, double accelRatio, double delta)
    {
        if (!_pitchEnabled)
        {
            v.Pitch.Reset();
            v.Sprite.Pitch = 0.0;
            return;
        }
        v.Pitch.Update(accelRatio, delta);
        v.Sprite.Pitch = v.Pitch.Angle;
    }

    private void UpdateRumble(Vehicle v, double delta)
    {
        if (!_rumbleEnabled)
        {
            v.Rumble.Reset();
            v.Sprite.Shake = 0;
            v.Sprite.Roll = 0.0;
            return;
        }
        v.Rumble.Advance(v.Speed * delta, v.Speed);
        v.Sprite.Shake = v.Rumble.Offset;
        v.Sprite.Roll = v.Rumble.Roll;
    }



    /// <summary>
    /// Winds the belts on by the ground that went past.
    ///
    /// Keyed on distance like the rumble, and unlike it that is not a reading of
    /// the terrain but the belt's definition: it is the part in contact with the
    /// ground. So this one is driven from the same <c>_speed * delta</c> and
    /// goes still the moment the tank does, which is correct - a stationary
    /// tank's tracks are stationary, whatever the engine is doing.
    /// </summary>
    private void UpdateTracks(Vehicle v, double distance, double delta)
    {
        if (!_tracksEnabled || v.Atlas.HasTracks != true)
        {
            if (v.Sprite.TrackPhase < 0)
                return;
            v.Track.Reset();
            v.Sprite.TrackPhase = -1;
            v.Sprite.QueueRedraw();
            return;
        }
        v.Track.Advance(distance, delta);
        v.Sprite.TrackPhase = v.Track.Frame;
    }

    /// <summary>Runs every frame, moving or not - that is the whole point of it.
    /// The rumble is keyed on distance and goes silent the instant the tank
    /// stops; the engine does not. Speed only moves the frequency, so there is
    /// no threshold anywhere for the effect to switch on or off at.</summary>
    private void UpdateTremble(Vehicle v, double delta)
    {
        if (!_trembleEnabled)
        {
            if (v.Sprite.TremblePitch == 0.0 && v.Sprite.TrembleRoll == 0.0)
                return;
            v.Tremble.Reset();
            v.Sprite.TremblePitch = 0.0;
            v.Sprite.TrembleRoll = 0.0;
            v.Sprite.QueueRedraw();
            return;
        }
        v.Tremble.Advance(v.Speed, delta);
        v.Sprite.TremblePitch = v.Tremble.Pitch;
        v.Sprite.TrembleRoll = v.Tremble.Roll;
        v.Sprite.QueueRedraw();
    }

    /// <summary>Runs every frame, moving or not, like the tremble and for the
    /// same reason: it is the engine, not the ground. Speed only moves the rate
    /// and the density, so there is no threshold to switch on at.</summary>
    private void UpdateExhaust(Vehicle v, double delta)
    {
        if (!_exhaustEnabled || !v.Atlas.HasExhaust)
        {
            if (v.Sprite.ExhaustPhase < 0)
                return;
            v.Exhaust.Reset();
            v.Sprite.ExhaustPhase = -1;
            v.Sprite.QueueRedraw();
            return;
        }
        v.Exhaust.Advance(v.Speed, delta);
        v.Sprite.ExhaustPhase = v.Exhaust.Frame;
        v.Sprite.ExhaustDensity = (float)v.Exhaust.Density;
    }

    /// <summary>Runs every frame like the exhaust, but takes no speed: see
    /// <see cref="BurnLoop"/>. Both halves are required before anything is
    /// shown, because the flame without its column is the washed-out half of the
    /// effect rather than a cheaper version of it.</summary>
    private void UpdateBurn(Vehicle v, double delta)
    {
        if (!v.Burning || !v.Atlas.HasBurning)
        {
            if (!v.Sprite.Burning && v.Sprite.FirePhase < 0 && v.Sprite.BurnPhase < 0)
                return;
            v.Burn.Reset();
            v.Sprite.Burning = false;
            v.Sprite.FirePhase = -1;
            v.Sprite.BurnPhase = -1;
            v.Sprite.QueueRedraw();
            return;
        }
        v.Burn.Advance(delta);
        v.Sprite.Burning = true;
        v.Sprite.FirePhase = v.Burn.FireFrame;
        v.Sprite.BurnPhase = v.Burn.SmokeFrame;
    }

    /// <summary>Fire. Restarts the flash from frame zero rather than being
    /// ignored while one is running, so holding the key reads as a rate of fire
    /// instead of doing nothing.</summary>
    private void Fire() => Fire(Active);

    /// <summary>
    /// One round out of one tank's gun.
    ///
    /// Takes the vehicle rather than working on the driven one, because a tank
    /// left engaging a target goes on firing after you have selected somebody
    /// else - which is the whole of the attack scene. Key Z is the same thing
    /// aimed at nothing in particular.
    /// </summary>
    private void Fire(Vehicle v)
    {
        v.ShotFrame = 0;
        v.ReloadLeft = v.Profile.ReloadTime;
        // The sprite shear only if it is asked for: the tube's own recoil is the
        // real article now, and the two together show one true movement and one
        // invented one.
        if (_recoilShear)
            v.Recoil.Fire(WrapAngle(v.Sprite.TurretFacing - v.Sprite.HullFacing));
        // The tube goes back on the same trigger and on its own clock: the two
        // events last different lengths of time, so one counter would give one of
        // them the wrong tempo. See RecoilLoop.
        v.Barrel.Fire();
        // Third thing off one trigger, and a third clock, for the same reason:
        // the gun's report is 1.2s on the light and 3.4s on the heavy, and neither
        // is the 34 frames the flash runs or the 28 the tube takes to come home.
        v.Audio?.Fire();
    }

    /// <summary>The shot runs on screen frames, not seconds. The sheet is a
    /// hand-timed sequence of held frames, so counting frames is what it is
    /// timed in; under --capture the clock is fixed at 1/60 anyway, which is
    /// what makes a shot land on the same sheet frame in two runs.</summary>
    private void UpdateShot(Vehicle v, double delta)
    {
        if (_recoilShear)
        {
            v.Recoil.Update(delta);
            v.Sprite.RecoilPitch = v.Recoil.Pitch;
            v.Sprite.RecoilRoll = v.Recoil.Roll;
        }
        else if (v.Recoil.Moving || v.Sprite.RecoilPitch != 0.0
                 || v.Sprite.RecoilRoll != 0.0)
        {
            // Switched off part way through a ring-down, which has to put the
            // body back rather than leave it leaning: the spring is the only
            // effect here that holds a pose with nothing driving it.
            v.Recoil.Reset();
            v.Sprite.RecoilPitch = 0.0;
            v.Sprite.RecoilRoll = 0.0;
            v.Sprite.QueueRedraw();
        }

        // Both clocks run, whichever source is showing. They are separate
        // tables - sixteen sheet frames against eight rendered phases - but
        // they add up to the same 34 frames, so flipping V mid-shot swaps the
        // picture without moving the moment, which is what makes an A/B
        // comparison one.
        int frame = v.ShotFrame < 0 ? -1 : FlashSheet.FrameAt(v.ShotFrame);
        int phase = v.ShotFrame < 0 ? -1 : EffectLayer.PhaseAt(v.ShotFrame);
        if (v.ShotFrame >= 0)
        {
            v.ShotFrame++;
            if (frame < 0 && phase < 0)
                v.ShotFrame = -1;
        }
        // The gun tube, on its own clock and its own count of frames. Switched
        // off it holds the rest pose rather than disappearing - the gun is still
        // a gun, it just stops moving, which is the A/B against every tank
        // rendered before this layer existed.
        int tube = 0;
        if (v.Atlas.HasRecoil && _recoilTube)
        {
            v.Barrel.Advance();
            tube = v.Barrel.Phase;
        }
        else
        {
            v.Barrel.Reset();
        }

        if (frame != v.Sprite.FlashFrame || phase != v.Sprite.ShotPhase
            || tube != v.Sprite.RecoilPhase || v.Recoil.Moving)
        {
            v.Sprite.FlashFrame = frame;
            v.Sprite.ShotPhase = phase;
            v.Sprite.RecoilPhase = tube;
            v.Sprite.QueueRedraw();
        }
    }

    /// <summary>
    /// Take a hit from <paramref name="fromBearing"/>, a world heading.
    ///
    /// The plate is chosen by the atlas rather than named here, because that is
    /// the path the game will take: a shooter stands somewhere, and which
    /// armour that lands on is geometry. The scatter runs along the plate's own
    /// screen tangent, so it slides across the metal instead of off it.
    /// </summary>
    private void TakeHit(double fromBearing)
    {
        // The key and the flag aim the harness dial, so this one moves it. A
        // shell arriving from another tank must not: the dial says which side
        // the *next* manual hit comes from, and a firefight rewriting it would
        // make the panel a report of what happened rather than a control.
        _hitSide = SideFor(fromBearing);
        TakeHit(Active, HitFrom, Calibre, null, Bite);
    }

    /// <summary>
    /// One tank's round into another tank, with the classes deciding how deep it
    /// gets.
    ///
    /// The shell that came out of a gun knows something the key-press shell
    /// cannot: who fired it. That is all the difference between the two paths -
    /// <see cref="Gunnery.Penetration"/> turns the pair of classes into a level,
    /// and the level is a ceiling rather than a bite, so a light tank never holes
    /// a heavy however long it keeps at it.
    ///
    /// The calibre dial still sizes the burst. It is the harness's control over
    /// what a shell looks like, and nothing about the matchup makes it wrong -
    /// what it no longer does on this path is decide the damage, which is now the
    /// classes' business.
    /// </summary>
    private void TakeHit(Vehicle shooter, Vehicle victim, double fromBearing) =>
        TakeHit(victim, fromBearing, Calibre,
                Gunnery.Penetration(shooter.Profile, victim.Profile), 1);

    /// <summary>
    /// One shell into one tank, from a world bearing.
    ///
    /// The victim is a parameter for the reason the shooter is: a tank is shot
    /// at by another tank now, and the one being driven is as likely to be the
    /// gunner as the target. Everything else is unchanged - which is the point,
    /// because the armour model is exactly as good at answering a shell that
    /// came from a neighbouring tank as one that came from a keypress.
    ///
    /// <paramref name="level"/> is the ceiling the round cannot get past, or null
    /// for a round with no ceiling, which digs by <paramref name="bite"/>
    /// instead. Exactly one of the two is in force, and which one is the whole
    /// difference between a shell fired by a tank and a shell asked for by a key
    /// - see <see cref="TankSprite.DamageTo"/>.
    /// </summary>
    private void TakeHit(Vehicle victim, double fromBearing, float calibre,
                         int? level, int bite)
    {
        TankSprite sprite = victim.Sprite;
        AtlasSet atlas = victim.Atlas;
        if (!atlas.HasHit)
            return;
        // Snapped, not taken as given: a shell arrives from a neighbouring
        // cell, so every angle anything hands in resolves to one of the six
        // sides rather than to itself.
        double from = HexField.EdgeHeadings[SideFor(fromBearing)];
        // Deterministic under --capture and --trace, which fix the time step so
        // two runs can be diffed; a random scatter would put the two hits in
        // different places and the diff would measure that instead.
        victim.HitCount++;
        // How far along the plate this one landed, as a fraction of its
        // half-width. It is passed to both halves - the burst that shows the
        // shell arriving and the mark it leaves - because it is one shell, and
        // a hole appearing in the middle of a plate the flash went off the end
        // of is the whole reason this is a shared number rather than two.
        //
        // +-0.45 rather than the burst's old +-0.6: the mark has to stay on the
        // armour, and it is about 17px wide against half-widths of 38 to 61.
        float scatter = ScatterAt(victim.HitCount);
        string face = atlas.FaceFor(from, sprite.HullFacing);
        // The calibre goes the same way as the scatter and for the same reason:
        // both are settled the moment the round lands, so both travel with it
        // rather than being looked up again while it is on screen.
        victim.Hit.Strike(face, scatter, calibre);
        // How deep it goes is the calibre's business - see TankSprite.Damage.
        // A light round still walks a plate scorch -> gouge -> breach over three
        // hits, which is what shows that the phase axis is damage and not three
        // renders of one drawing; a heavy one gets there in one.
        int got = level is int cap
            ? sprite.DamageTo(face, scatter, cap)
            : sprite.Damage(face, scatter, bite);
        // The sound is chosen by the same level the mark is, so a round that
        // bounces is heard bouncing and one that goes through is heard going
        // through. Not the calibre directly: a light round on armour already
        // twice hit does go through, and the ear should be told the same thing
        // the plate is.
        //
        // On the tank that was hit rather than on the one that fired: the ricochet
        // rings off this armour, and its player is positioned there.
        //
        // The shell count picks which of the two takes plays - see Struck. It is
        // the victim's, so it counts shells into this hull however many guns are
        // pointed at it.
        victim.Audio?.Struck(got, victim.HitCount);
    }

    /// <summary>
    /// One tank engaging another: lay the gun, and fire when it is laid, loaded
    /// and there is a line.
    ///
    /// **The rule that shapes all of it is that a gun fires down a flat side of
    /// the hex and nowhere else.** Six lanes out of a cell, so a target that is
    /// not standing on one cannot be shot at from here at all - the turret comes
    /// round to the nearest lane and stops there, visibly not lined up, and the
    /// six arcs go down on the ground saying where to drive. Moving is the
    /// player's business: the right button says who, the left button says where,
    /// and a tank that drove itself into a firing position would be answering
    /// the second question with the first.
    ///
    /// Three more gates, each of which is a thing that would otherwise have to
    /// be noticed on screen:
    ///
    /// - **Stopped.** The lane is computed from the cell the tank is standing
    ///   in, and a tank between two cells is standing in neither. Firing on the
    ///   move would mean picking one of them, which is a decision with no right
    ///   answer rather than an implementation detail.
    /// - **Not through somebody.** A tank in the lane stops the shell. It is
    ///   also the only reason a solution can go away without either tank moving,
    ///   which is why <see cref="Shot"/> reports it separately.
    /// - **Loaded.** Per class, and the one figure that makes a heavy feel heavy
    ///   while standing perfectly still.
    /// </summary>
    private void UpdateAttack(Vehicle v, double delta)
    {
        if (v.ReloadLeft > 0.0)
            v.ReloadLeft = Math.Max(0.0, v.ReloadLeft - delta);
        if (v.Target is null)
        {
            v.Solution = Gunnery.None;
            return;
        }
        v.Solution = Gunnery.Solve(_field, _vehicles, v, v.Target);

        // Where the gun is allowed to point. On a lane that is the lane; off one
        // it is the nearest flat side to where the target actually is, so the
        // turret tracks rather than parking at whatever heading it was left on.
        // The eye then reads "aimed at it but not down a lane", which is exactly
        // the situation.
        int onto = v.Solution.OnLane
            ? v.Solution.Heading
            : HexField.EdgeHeadings[SideFor(Gunnery.HeadingOf(
                  v.Target.GroundPoint - v.GroundPoint))];
        double was = v.Sprite.TurretFacing;
        v.Sprite.TurretFacing = Gunnery.Traverse(
            was, onto, v.Profile.TurretRate * delta);
        if (v.Sprite.TurretFacing != was)
            v.Sprite.QueueRedraw();

        if (v.Moving || !v.Solution.Clear || v.ReloadLeft > 0.0
            || !Gunnery.Laid(v.Sprite.TurretFacing, v.Solution.Heading))
            return;
        Fire(v);
        // The shell arrives from the far end of the lane it left along, which is
        // the whole reason the gun is restricted to the six: the bearing the
        // armour model wants is the firing heading turned round, exactly, with
        // nothing snapped and nothing approximated. See AtlasSet.FaceFor.
        TakeHit(v, v.Target, Mod(v.Solution.Heading + 180.0, 360.0));
    }

    /// <summary>The hit runs on screen frames for the shot's reason: it is a
    /// hand-timed table of held frames, and under --capture the clock is fixed
    /// at 1/60 so the same frame lands in two runs.</summary>
    /// <summary>
    /// Every row in the side panel, each one a pair of delegates onto the field
    /// the keys already write.
    ///
    /// Not one control here owns a value. That is the whole design and it is
    /// worth the awkwardness of the lambdas: state changed from anywhere - a
    /// key, a start-up flag, R resetting the lot, a class switch clearing the
    /// damage - shows up in the panel next frame without anything having to
    /// tell it. The alternative keeps a copy per widget and they drift.
    ///
    /// The key each row duplicates is on its label, because the panel is a way
    /// to find the keys, not a replacement for them: a screenshot of a session
    /// should still be reproducible from the keyboard.
    /// </summary>
    private void BuildPanel()
    {
        ControlPanel ui = _panel!;

        ui.Heading("tank");
        // Which of the three is being driven. Not "(1/2/3)": how many tanks load
        // is whatever is on disk, so enumerating them here goes stale every time
        // one is added, and it already had.
        ui.Choice("driving  (number keys, or click it)", _loaded,
            () => _active, Select);
        // A multiplier over the three class figures, not a size - see _sizeLevel.
        // The caption is in pixels of hull against pixels of cell, because "1.15x"
        // answers nothing the dial is pulled for: whether the heavy reads heavier
        // than the medium, and whether it has outgrown the hex it stands on.
        ui.Slide("size level", 0.5, 1.5, 0.05,
            () => _sizeLevel, v => { _sizeLevel = v; ApplySize(); }, "x",
            () =>
            {
                AtlasSet a = _tank.Atlas!;
                double span = a.HullSpan * _tank.BodyScale;
                double cell = a.HexRect.Size.X * 0.75;
                // Short enough to fit the panel's width: the caption is clipped,
                // not wrapped, and a number that has run off the edge is not a
                // number.
                return $"class {_profile.Size:F2}x   {span:F0}px hull broadside"
                       + $" / {cell:F0}px cell";
            });
        ui.Readout(() =>
        {
            AtlasSet a = _tank.Atlas!;
            string order = Moving
                ? $"-> ({_path[^1].X},{_path[^1].Y}), {_path.Count - _pathStep} left"
                : "idle";
            return $"{a.Count} headings, {a.Tile.X}px, {a.UnitsPerPixel:F5} u/px\n"
                   + $"cell ({_cell.X},{_cell.Y})  {order}";
        });

        ui.Heading("heading");
        // Stepped by the atlas's own frame, not smoothly: twelve rendered
        // frames is what there is, and a slider that glides between them says
        // the sprite turns more finely than it does.
        ui.Slide("hull  (A/D)", 0.0, 330.0, 30.0,
            () => Math.Round(_tank.HullFacing / 30.0) * 30.0,
            v => { CancelOrder(); _tank.HullFacing = Mod(v, 360.0); }, " deg");
        ui.Slide("turret  (Q/E)", 0.0, 330.0, 30.0,
            () => Math.Round(_tank.TurretFacing / 30.0) * 30.0,
            v => { _aimWithMouse = false; _tank.TurretFacing = Mod(v, 360.0); }, " deg");
        ui.Toggle("turret locked  (F)",
            () => _tank.TurretLocked, on => _tank.TurretLocked = on);
        ui.Toggle("aim with mouse  (M)",
            () => _aimWithMouse, on => _aimWithMouse = on);
        ui.Toggle("spin turret  (SPACE)", () => _spinning, on => _spinning = on);
        ui.Toggle("scan on the spot  (N)", () => _scanEnabled, on =>
        {
            _scanEnabled = on;
            ScanChanged();
        });
        // Which frame each heading resolves to. The pair is the thing worth
        // seeing rather than either alone: two layers off the same axis, and a
        // sprite set that has come apart says so here first.
        ui.Readout(() =>
        {
            Vector2I frames = _tank.FrameIndices();
            return $"hull {_tank.HullFacing,6:F1} deg -> frame {frames.X,2}\n"
                   + $"turret {_tank.TurretFacing,6:F1} deg -> frame {frames.Y,2}"
                   + $"   scan {_scan.Offset,6:F1} deg";
        });

        ui.Heading("ride");
        ui.Toggle("body pitch  (P)", () => _pitchEnabled, on =>
        {
            _pitchEnabled = on;
            PitchChanged();
        });
        ui.Toggle("ground rumble  (B)", () => _rumbleEnabled, on =>
        {
            _rumbleEnabled = on;
            RumbleChanged();
        });
        ui.Toggle("engine tremble  (I)", () => _trembleEnabled, on =>
        {
            _trembleEnabled = on;
            TrembleChanged();
        });
        // A multiplier over the tuned pair rather than a raw amplitude - see
        // EngineTremble.Level for why the standing and moving figures must keep
        // their ratio. The caption carries the roof travel in pixels, because
        // the multiplier says nothing about whether it is visible and the pixel
        // figure is the whole argument the value was chosen on.
        ui.Slide("engine tremble level", 0.0, 2.5, 0.05,
            () => _tremble.Level, v => _tremble.Level = v, "x",
            () => $"{_tremble.GainAt(_speed) * EngineTremble.RoofLeverPx:F2}px"
                  + " of roof travel at this speed");
        ui.Toggle("turret stabiliser  (K)",
            () => _tank.TurretStabilised, on => _tank.TurretStabilised = on);
        ui.Readout(() =>
            $"speed {_speed,6:F0} / {_profile.TopSpeed:F0} px/s"
            + $"   ramp {_profile.RampTime:F2}s over {_profile.RampDistance:F0}px\n"
            + $"turn {_profile.TurnRate:F0} deg/s   corner {_profile.CornerSpeed:F0} px/s\n"
            + $"pitch {_tank.Pitch,7:F4}   roll {_tank.Roll,7:F4}"
            + $"   heave {_tank.Shake,2}px\n"
            + $"tremble {_tank.TremblePitch,7:F4} / {_tank.TrembleRoll,7:F4}"
            + $" at {_tremble.PitchRateAt(_speed),4:F1} Hz");

        ui.Heading("effects");
        ui.Toggle("tracks wind  (C)", () => _tracksEnabled, on =>
        {
            _tracksEnabled = on;
            TracksChanged();
        });
        // The belt is the one layer tied to the ground rather than to a clock,
        // so what it is worth saying about it is how well it is keeping up. The
        // cap is a real limit and not a setting to hide: past it the tread would
        // alias and run backwards, so it slips instead, and that shows here.
        ui.Readout(() =>
        {
            AtlasSet a = _tank.Atlas!;
            if (!a.HasTracks)
                return "tracks  [none - needs a parts-built model]";
            string phase = _tank.TrackPhase < 0 ? " -" : $"{_tank.TrackPhase,2}";
            return $"track {phase} / {a.TrackPhases}"
                   // as drawn, not as rendered: the size dial moves it, and the
                   // sync speed below moves with it
                   + $"   link {_track.LinkOnScreen,5:F1}px"
                   + $"\nin step to {_track.SyncSpeed,3:F0} px/s"
                   + $" of {_profile.TopSpeed:F0}"
                   + $"   slipping {(1.0 - _track.Slip) * 100.0,3:F0}%";
        });
        ui.Toggle("engine exhaust  (O)", () => _exhaustEnabled, on =>
        {
            _exhaustEnabled = on;
            ExhaustChanged();
        });
        ui.Toggle("on fire  (J)", () => _burning, on =>
        {
            _burning = on;
            UpdateBurn(Active, 0.0);
        });
        ui.Choice("flash source  (V)", new[] { "rendered", "sheet" },
            () => _tank.Source == FlashSource.Rendered ? 0 : 1,
            i =>
            {
                _tank.Source = i == 0 ? FlashSource.Rendered : FlashSource.Sheet;
                _tank.QueueRedraw();
            });
        // Above its own level and pivot, because it gates both: a slider on a
        // switched-off effect is a slider that looks broken.
        ui.Toggle("hull shear on the shot  (])",
            () => _recoilShear, on => _recoilShear = on);
        // Read when the trigger goes, not while the hull is rocking, so this
        // sets the next shot rather than the one in the air.
        ui.Slide("recoil level", 0.0, 2.5, 0.05,
            () => _recoil.Level, v => _recoil.Level = v, "x",
            () => _recoilShear
                ? $"kick peaks at {_recoil.PeakFor(_recoil.Level):F3}"
                  + $", rigid body ends at {Recoil.RigidBodyPeak:F3}"
                : "the shear is off - the tube recoils for real instead");
        ui.Toggle("recoil on turret only  (L)",
            () => _tank.RecoilTurretOnly, on => _tank.RecoilTurretOnly = on);
        ui.Toggle("gun tube recoils  ([)",
            () => _recoilTube, on => _recoilTube = on);
        // What the tube is doing and what it cost. The stroke in pixels is the
        // number the whole layer is judged on - four is what it was authored to,
        // and it is a fraction of that on the headings where the bore points at
        // the camera, which is projection rather than a fault. Said here because
        // "phase 2 of 5" answers nothing about whether it reads.
        ui.Readout(() =>
        {
            AtlasSet a = _tank.Atlas!;
            if (!a.HasRecoil)
                return "gun tube  [none - split a Barrel and render it]";
            string phase = _tank.RecoilPhase == 0
                ? "rest" : $"{_tank.RecoilPhase,2}";
            RecoilLoop tube = Active.Barrel;
            return $"tube {phase} / {a.RecoilPhases - 1}"
                   + $"   {tube.Duration} frames"
                   + $" ({tube.Duration / 60.0,4:F2}s)"
                   + $"\nheld {string.Join("/", RecoilLoop.HoldsFor(a.RecoilPhases))}"
                   + "   kick under the flash, return in the open";
        });
        ui.Press("fire  (Z)", Fire);
        // Phase counters against the number of phases that were rendered. A
        // clock that has walked off the end of its atlas shows up here as a
        // number out of range, and nowhere else until the layer goes blank.
        ui.Readout(() =>
        {
            AtlasSet a = _tank.Atlas!;
            string phase(int p) => p < 0 ? " -" : $"{p,2}";
            return $"exhaust {phase(_tank.ExhaustPhase)} / {a.ExhaustPhases}"
                   + $" at {_exhaust.RateAt(_speed),4:F1} /s"
                   + $"   density {_exhaust.Density,4:F2}"
                   + (a.HasExhaust ? "" : "   [none - split an Engine]")
                   + $"\nfire {phase(_tank.FirePhase)} at {_burn.FireRate,4:F1} /s"
                   + $"   smoke {phase(_tank.BurnPhase)} at {_burn.SmokeRate,4:F1} /s"
                   + $" / {a.BurnPhases}"
                   + (a.HasBurning ? "" : "   [none - split an Engine]")
                   + "\n" + (_tank.ActiveSource == FlashSource.Rendered
                       ? $"shot phase {phase(_tank.ShotPhase)} / {EffectLayer.Hold.Length}"
                         + $"   tile {a.TileOf("flash").X}px"
                       : $"shot frame {phase(_tank.FlashFrame)} / {FlashSheet.Hold.Length}"
                         + $"   muzzle {a.Muzzle(a.FrameFor(_tank.TurretFacing))}")
                   + (_tank.Source == FlashSource.Rendered && !_tank.CanRender
                       ? "   [falling back - split a Barrel]" : "")
                   // Which body is taking the kick, and about what. The seam cost
                   // is not printed because it is zero in both arrangements by
                   // construction - shared, the two move together; on the turret
                   // alone, the pivot *is* the seam. That is asserted in the
                   // self-test, where a claim that cannot vary belongs.
                   + $"\nrecoil {_recoil.Pitch,7:F4} / {_recoil.Roll,7:F4}"
                   + "   on " + (_tank.RecoilTurretOnly
                       ? "turret, about the ring"
                       : "hull+turret, about the ground");
        });

        ui.Heading("gunnery");
        // A dropdown rather than the six buttons the shot bearing gets: those
        // are directions and have a shape, this is a list of tanks and how many
        // there are is whatever loaded. "nobody" is first so index 0 is the
        // ceasefire, which is what the right button on empty ground does too.
        ui.Choice("target  (right-click a tank)",
            new[] { "nobody" }.Concat(_loaded).ToArray(),
            () => Active.Target is null ? 0 : _vehicles.IndexOf(Active.Target) + 1,
            i => Engage(Active, i == 0 ? null : _vehicles[i - 1]));
        // The reason, not a yes or no - see AimLine. The second line is what the
        // rule costs, which is the question the rule raises: a gun that fires
        // down six lanes can be pointed at a tank it cannot hit, and the number
        // of cells it *could* hit from here is the answer to "so where do I go".
        ui.Readout(() =>
            $"{AimLine()}\nreload {Active.Profile.ReloadTime:F1}s, "
            + $"traverse {Active.Profile.TurretRate:F0} deg/s, "
            + $"{_field.Arcs.Count} cells down the six lanes");

        ui.Heading("armour");
        // A side of the hex rather than a bearing, because that is what the
        // game has: a shell comes from a neighbouring cell, and there are six
        // of those. A slider ran 0..355 here and offered 66 bearings that
        // cannot happen.
        ui.Radio(() => $"shot comes from side {_hitSide + 1}"
                       + $" - {HitFrom:F0} deg  (U steps)",
            Enumerable.Range(1, HexField.EdgeHeadings.Length)
                .Select(i => i.ToString()).ToArray(),
            () => _hitSide, i => _hitSide = i);
        ui.Choice("calibre  (Y)", new[] { "0.7x", "1.0x", "1.4x" },
            () => _calibre,
            i =>
            {
                // Only what the *next* shell will be. Nothing on screen moves:
                // a round already in the air keeps the calibre it went off at.
                _calibre = i;
            });
        // Takes the bearing on the slider rather than walking it on, so the
        // panel can aim and the key can sweep.
        ui.PressPair("take a hit", () => TakeHit(HitFrom),
                     "repair", () => _tank.Repair());
        ui.Readout(() =>
        {
            AtlasSet a = _tank.Atlas!;
            // Two calibres side by side on purpose: what the next round will be,
            // and what the one on screen went off at. They differ for as long as
            // a shell outlives a turn of the dial, and seeing that is the point.
            string burst = $"burst {(_tank.HitPhase < 0 ? " -" : $"{_tank.HitPhase,2}")}"
                           + $" / {a.HitPhases}"
                           + $"   {(_hit.Face == "" ? "-" : _hit.Face)}"
                           + $"   {(_tank.HitBehind ? "behind" : "in front")}"
                           + $"\nloaded x{Calibre:F2}, {Bite} level"
                           + (Bite == 1 ? "" : "s") + " of armour"
                           + (_hit.Live ? $"   in the air x{_hit.Scale:F2}" : "")
                           + (a.HasHit ? "" : "   [no plate table - re-render]");
            if (!a.HasScars)
                return burst + "\nno scar layers - re-render this scene";
            // The levels each plate is carrying, oldest first - "front 012" is a
            // plate that has taken three. The list rather than the worst,
            // because the marks accumulate and how many there are is the thing
            // no still frame shows.
            return burst + "\n" + string.Join("\n", a.HitFaces.Select(f =>
                $"{f,-6} {(_tank.MarksOn(f).Count == 0 ? "clean" : string.Concat(_tank.MarksOn(f).Select(m => m.Level)))}"
                + $"   worked {_tank.Wear(f)} / {a.ScarLevels}"));
        });

        ui.Heading("view");
        ui.Slide("zoom  (wheel)", 0.25, 8.0, 0.05,
            () => _camera.Zoom.X,
            v => _camera.Zoom = new Vector2((float)v, (float)v), "x");
        ui.Toggle("hull layer  (H)", () => _tank.ShowHull, on => _tank.ShowHull = on);
        ui.Toggle("turret layer  (T)",
            () => _tank.ShowTurret, on => _tank.ShowTurret = on);
        ui.Toggle("hex field  (G)", () => _field.ShowField, on =>
        {
            _field.ShowField = on;
            _field.QueueRedraw();
        });
        ui.Toggle("axis cross  (X)", () => _tank.ShowAxis, on => _tank.ShowAxis = on);
        ui.PressPair("reset  (R)", ResetAll,
                     "screenshot  (F12)", () => Capture(
                         $"{ProjectSettings.GlobalizePath("res://")}shot_{Time.GetTicksMsec()}.png"));
    }

    /// <summary>Where the nth shell lands along its plate, as a fraction of the
    /// plate's half-width. Deterministic on purpose: --capture and --trace fix
    /// the time step so two runs can be diffed, and a random scatter would be
    /// measuring itself.</summary>
    private static float ScatterAt(int n) => (((n * 37) % 100) / 100.0f - 0.5f) * 0.9f;

    private void UpdateHit(Vehicle v, double delta)
    {
        AtlasSet atlas = v.Atlas;
        TankSprite sprite = v.Sprite;
        HitLoop hit = v.Hit;
        int phase = hit.Phase;
        if (phase >= 0)
        {
            // Read every frame, not once at the strike: the hull can turn while
            // the dust is still settling, and the hit has to stay on the plate
            // it landed on rather than sliding round with the heading.
            Vector2 tangent = atlas.HitTangent(hit.Face, sprite.HullFacing);
            sprite.HitOffset = atlas.HitOffset(hit.Face, sprite.HullFacing)
                               + tangent * hit.Scatter;
            sprite.HitBehind = atlas.HitFacing(hit.Face, sprite.HullFacing) <= 0.0;
            // The shell's calibre, not the dial's. Pushed from here alongside
            // the other two things the layer needs and cannot work out for
            // itself, so what is drawn is what was fired.
            sprite.HitScale = hit.Scale;
        }
        if (phase != sprite.HitPhase)
        {
            sprite.HitPhase = phase;
            sprite.QueueRedraw();
        }
        hit.Advance();
    }

    /// <summary>Suspended while under way, and while anything else is driving
    /// the turret. A locked turret holding its world heading through a
    /// manoeuvre is the feature this harness exists to show; a scan quietly
    /// walking it off that heading would look exactly like the lock failing.</summary>
    private void UpdateScan(Vehicle v, double delta)
    {
        // The spin and the mouse only ever drive the tank being controlled, so
        // they only suspend that one's scan: a tank standing off to the side keeps
        // looking around while you spin the turret of the one you are driving.
        // A tank with a target is laying its gun, and the scan is what it does
        // with nothing to look at - so the target suspends it on that tank only,
        // exactly as the spin and the mouse suspend it on the driven one.
        if (!_scanEnabled || v.Moving || v.Target is not null
            || (v == Active && (_spinning || _aimWithMouse)))
            return;
        double move = v.Scan.Advance(360.0 / v.Atlas.Count, delta);
        if (move == 0.0)
            return;
        v.Sprite.TurretFacing = Mod(v.Sprite.TurretFacing + move, 360.0);
        v.Sprite.QueueRedraw();
    }

    // --- frame -------------------------------------------------------------

    public override void _Process(double delta)
    {
        // give the panel and both layers a couple of frames to settle first
        if (_capturePath is not null && ++_frames > _captureAfter)
        {
            Capture(_capturePath);
            GetTree().Quit();
            return;
        }

        if (_vehicles.Count == 0 || _tank.Atlas is null)
            return;

        // A capture run steps at a fixed rate so two runs land on the same
        // state at the same frame number. On real deltas they drift a frame or
        // two apart, and comparing a pitch-on shot against a pitch-off shot
        // then measures the drift instead: caught once when the two runs were
        // mid-pivot on either side of a 30 deg sprite boundary and "differed"
        // by 91k pixels.
        if (_capturePath is not null || _traceFrames > 0)
            delta = 1.0 / 60.0;

        if (_traceFrames > 0)
        {
            // Which tank the line is about. There are three of them now and only
            // one is being driven, so a trace without this says nothing about
            // whether the selection went where the flag asked - the same reason
            // the zoom and the size are printed here.
            GD.Print($"{_frames,4}  {Active.Tag,-3}"
                     + $"  hull {_tank.HullFacing,6:F1}  speed {_speed,6:F1}"
                     + $"  pitch {_tank.Pitch,8:F5}  shake {_tank.Shake,2}"
                     + $"  roll {_tank.Roll,8:F5}  trem {_tank.TremblePitch,8:F5}"
                     + $"/{_tank.TrembleRoll,8:F5}  scan {_scan.Offset,6:F1}"
                     + $"  turret {_tank.TurretFacing,6:F1}  shot {_tank.FlashFrame,3}"
                     + $"  recoil {_recoil.Pitch,8:F5}/{_recoil.Roll,8:F5}"
                     // Which body is taking it, in the channel that survives
                     // --no-ui. A mode that quietly failed to apply would send
                     // you looking at the spring rather than at the flag - the
                     // zoom is printed here for the same reason.
                     + $"@{(_tank.RecoilTurretOnly ? "turret" : "body")}"
                     // and whether it is on at all. Off is the default now, so a
                     // trace of zeroes is the normal case rather than a spring
                     // that failed to fire - which is exactly the confusion the
                     // '@turret'/'@body' marker was added to prevent.
                     + $"{(_recoilShear ? "" : "!off")}"
                     + $"  exh {_tank.ExhaustPhase,2}@{_exhaust.Phase,5:F2}"
                     // slip as well as phase: the cap is a real limit, and a
                     // belt quietly running at two thirds of the ground is not
                     // something to have to work out from the phase alone
                     + $"  trk {_tank.TrackPhase,2}@{_track.Phase,5:F2}"
                     + $" x{_track.Slip,4:F2}"
                     // The tube, in the channel that survives --no-ui, and with
                     // the switch beside it: 'rest' with the flag off and 'rest'
                     // between shots are the same number and not the same thing.
                     + $"  tube {_tank.RecoilPhase,2}"
                     + $"{(_recoilTube ? "" : "!off")}"
                     + $"  burn {_tank.FirePhase,2}/{_tank.BurnPhase,2}"
                     + $"  hit {_tank.HitPhase,2}@{(_hit.Face == "" ? "-" : _hit.Face)}"
                     + $" x{_hit.Scale:F2}"
                     // Sound has no screenshot, so this is the only place it can
                     // be judged from outside. The gates and pitches say what was
                     // asked for; the master bus peak says what actually came out,
                     // and only the second one can tell a set that loaded from a
                     // set that is audible. A trace with rising gates and a peak
                     // pinned at -inf is the shape of a device that never opened.
                     + $"  snd {SoundLine()}"
                     // What the gun thinks, in the channel that survives
                     // --no-ui. "no lane" and "blocked" are two different
                     // reasons a tank is standing there not firing, and from
                     // outside they look identical.
                     + $"  aim {AimLine()}"
                     + $"  cell ({_cell.X},{_cell.Y})"
                     // Size for the same reason as the zoom: two captures at two
                     // sizes that came out identical would send you looking at
                     // the scale code rather than at whether the flag parsed.
                     + $"  size {_tank.BodyScale:F2}x"
                     // Zoom, because a capture run has no panel to read it off
                     // and a stray wheel over the window has already cost one
                     // measurement: the A/B then differs by the whole picture
                     // rather than by the silhouette.
                     + $"  zoom {_camera.Zoom.X:F2}x");
            if (++_frames >= _traceFrames)
            {
                GetTree().Quit();
                return;
            }
        }

        // Every tank, not just the one being driven. Three tanks with two of them
        // frozen would not be a comparison of three tanks: the engine of a tank
        // nobody is driving is still running, and the whole reason the tremble is
        // on by default is that a vehicle with nothing moving on it is wrong in a
        // way a still render is not.
        foreach (Vehicle v in _vehicles)
        {
            if (v.Moving)
                AdvanceOrder(v, delta);
            else if (v.Pitch.Angle != 0.0 || v.Speed != 0.0 || v.Sprite.Shake != 0)
            {
                // let the body settle after the stop instead of snapping level
                v.Speed = 0.0;
                UpdatePitch(v, 0.0, delta);
                UpdateRumble(v, delta);
                v.Sprite.QueueRedraw();
            }

            // after the order has moved the tank, so the belts see the ground
            // that actually went past this frame rather than last frame's
            UpdateTracks(v, BeltTravel(v, delta), delta);
            UpdateTremble(v, delta);
            UpdateExhaust(v, delta);
            UpdateBurn(v, delta);
            UpdateScan(v, delta);
            // Before the shot, because it is what pulls the trigger: the gun
            // going off this frame has to reach UpdateShot with the flash on
            // frame 0 rather than one frame stale.
            UpdateAttack(v, delta);
            UpdateShot(v, delta);
            UpdateHit(v, delta);
            // Last, after every clock has stepped. The audio reads results of
            // this frame - the belt's achieved phase rate above all - and reading
            // them before the step is reading last frame's, which is the trap
            // EffectLayer already documents from the other side.
            v.Audio?.Update(v, delta);
        }

        // The marks are about the driven tank and both tanks move, so they are
        // repainted on change rather than on a target being set: a lane that was
        // clear when the order was given stops being clear when somebody drives
        // into it. Memoised because that is a handful of cells rebuilt and a
        // redraw of the whole field, and neither is free at sixty a second.
        var mark = (_active, Active.Cell, Active.Target, Active.Target?.Cell,
                    Active.Solution.Clear);
        if (mark != _painted)
        {
            _painted = mark;
            PaintGunnery();
        }

        // Both of these are turret controls, and a tank that has been told what
        // to shoot at is already using its turret. Left in, the spin would wind
        // the gun off the target once a frame and the engagement would read as a
        // turret that cannot hold a lay.
        if (Active.Target is not null)
        {
            // nothing - UpdateAttack has the gun
        }
        else if (!Moving && _spinning)
        {
            _tank.TurretFacing = Mod(_tank.TurretFacing + SpinSpeed * delta, 360.0);
            _tank.QueueRedraw();
        }
        else if (!Moving && _aimWithMouse)
        {
            Vector2 toMouse = GetGlobalMousePosition() - _tank.GlobalPosition;
            if (toMouse.Length() > 8.0f)
            {
                double facing = Gunnery.HeadingOf(toMouse);
                if (Math.Abs(facing - _tank.TurretFacing) > 0.01)
                {
                    _tank.TurretFacing = facing;
                    _tank.QueueRedraw();
                }
            }
        }
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton { Pressed: true } mouse)
        {
            if (mouse.ButtonIndex == MouseButton.Left && _vehicles.Count > 0
                && _tank.Atlas is not null)
            {
                Vector2 local = _field.ToLocal(GetGlobalMousePosition());
                Vector2I cell = _field.ClampCell(_field.CellAt(local));
                // A tank there means "drive that one from now on"; empty ground
                // means "go there". One click, and which of the two it is decided
                // by what is standing on the cell rather than by a modifier.
                int pick = SelectionFor(_vehicles, _active, cell);
                if (pick >= 0)
                    Select(pick);
                else
                    OrderMoveTo(cell);
                return;
            }
            // The right button is the gun and nothing else. Symmetrical with the
            // left: what is standing on the cell decides, so a tank there means
            // "shoot that one" and empty ground means "stop shooting". The
            // driven tank's own cell resolves to the second - see Engage - which
            // is the same reading left-clicking yourself gets.
            if (mouse.ButtonIndex == MouseButton.Right && _vehicles.Count > 0
                && _tank.Atlas is not null)
            {
                Vector2 local = _field.ToLocal(GetGlobalMousePosition());
                Vector2I cell = _field.ClampCell(_field.CellAt(local));
                Engage(Active, Vehicle.At(_vehicles, cell));
                _panel?.Sync();
                return;
            }
            float factor = mouse.ButtonIndex switch
            {
                MouseButton.WheelUp => 1.25f,
                MouseButton.WheelDown => 0.8f,
                _ => 1.0f,
            };
            if (factor != 1.0f)
            {
                float zoom = Mathf.Clamp(_camera.Zoom.X * factor, 0.25f, 8.0f);
                _camera.Zoom = new Vector2(zoom, zoom);
            }
            return;
        }
        if (@event is InputEventMouseMotion motion
            && (motion.ButtonMask & MouseButtonMask.Middle) != 0)
        {
            _camera.Position -= motion.Relative / _camera.Zoom;
            return;
        }
        if (@event is not InputEventKey { Pressed: true, Echo: false } key)
            return;
        if (_vehicles.Count == 0 || _tank.Atlas is null)
            return;

        double step = 360.0 / _tank.Atlas.Count;
        switch (key.Keycode)
        {
            case Key.A: _tank.TurnHull(step); break;
            case Key.D: _tank.TurnHull(-step); break;
            case Key.Q:
                _aimWithMouse = false;
                _tank.TurretFacing = Mod(_tank.TurretFacing + step, 360.0);
                break;
            case Key.E:
                _aimWithMouse = false;
                _tank.TurretFacing = Mod(_tank.TurretFacing - step, 360.0);
                break;
            case Key.W:
                OrderMoveTo(_field.Neighbour(_cell, _tank.HullFacing));
                break;
            case Key.S:
                OrderMoveTo(_field.Neighbour(_cell, Mod(_tank.HullFacing + 180.0, 360.0)));
                break;
            case Key.F: _tank.TurretLocked = !_tank.TurretLocked; break;
            case Key.P:
                _pitchEnabled = !_pitchEnabled;
                PitchChanged();
                break;
            case Key.B:
                _rumbleEnabled = !_rumbleEnabled;
                RumbleChanged();
                break;
            case Key.I:
                _trembleEnabled = !_trembleEnabled;
                TrembleChanged();
                break;
            case Key.N:
                _scanEnabled = !_scanEnabled;
                ScanChanged();
                break;
            case Key.O:
                _exhaustEnabled = !_exhaustEnabled;
                ExhaustChanged();
                break;
            case Key.C:
                _tracksEnabled = !_tracksEnabled;
                TracksChanged();
                break;
            // Beside the belts, because it is the same kind of switch: both are
            // parts of the tank on layers of their own, and both are off only to
            // be compared against.
            //
            // '[' because every letter is taken - A to Z are all bound, and so
            // are Space, Tab, Escape, F12 and Key1..Key9 - and this is the first
            // free key outside an existing range. The bracket at least points the
            // way the tube goes.
            case Key.Bracketleft:
                _recoilTube = !_recoilTube;
                break;
            // '\' because the note on '[' has now come true twice over: A-Z are
            // all bound, '[' went to the tube and ']' to the shear, so this is
            // the next free key in the same physical cluster. There is no
            // mnemonic left to have - the shortage is the reason, and the panel
            // is where a switch is found by name.
            //
            // Harness-wide like the pitch and the rumble rather than per tank:
            // judging sound on one tank while two run silent throws away the
            // comparison it is on a switch for.
            case Key.Backslash:
                _soundEnabled = !_soundEnabled;
                SoundChanged();
                break;
            // Next to the tube it was replaced by, which is the comparison this
            // key exists for. ']' for the same reason '[' is '[': the letters are
            // gone.
            case Key.Bracketright:
                _recoilShear = !_recoilShear;
                break;
            case Key.J:
                _burning = !_burning;
                UpdateBurn(Active, 0.0);
                break;
            case Key.K: _tank.TurretStabilised = !_tank.TurretStabilised; break;
            // Next to the stabiliser deliberately: both answer "what does the
            // turret do that the hull does not".
            case Key.L: _tank.RecoilTurretOnly = !_tank.RecoilTurretOnly; break;
            case Key.Z: Fire(); break;
            case Key.U:
                TakeHit(HexField.EdgeHeadings[
                    (_hitSide + 1) % HexField.EdgeHeadings.Length]);
                break;
            case Key.Y:
                _calibre = (_calibre + 1) % Calibres.Length;
                break;
            case Key.V:
                _tank.Source = _tank.Source == FlashSource.Rendered
                    ? FlashSource.Sheet
                    : FlashSource.Rendered;
                _tank.QueueRedraw();
                break;
            case Key.Escape: CancelOrder(); break;
            // A range rather than one label per tank, and that is the fix for a
            // bug this line has now had twice: the list ended at Key4 when MTP
            // was added and at Key5 when HTP was, and each time the new tank was
            // simply unreachable from the keyboard while everything else about it
            // worked. The clamp below already answers "what if that tank is not
            // there", so the labels never needed to know how many there are.
            case >= Key.Key1 and <= Key.Key9:
                // Godot's Key enum is backed by long, so this needs the cast.
                // Clamped rather than assumed to be in range: the number of
                // tanks that loaded is whatever is on disk, and a key for one
                // that is not there should do nothing rather than throw.
                int pick = (int)(key.Keycode - Key.Key1);
                if (pick >= _vehicles.Count)
                    break;
                Select(pick);
                break;
            case Key.Space: _spinning = !_spinning; break;
            case Key.M: _aimWithMouse = !_aimWithMouse; break;
            case Key.H: _tank.ShowHull = !_tank.ShowHull; break;
            case Key.T: _tank.ShowTurret = !_tank.ShowTurret; break;
            case Key.G:
                _field.ShowField = !_field.ShowField;
                _field.QueueRedraw();
                break;
            case Key.X: _tank.ShowAxis = !_tank.ShowAxis; break;
            case Key.F12:
                Capture($"{ProjectSettings.GlobalizePath("res://")}shot_{Time.GetTicksMsec()}.png");
                return;
            case Key.R:
                ResetAll();
                break;
            case Key.Tab:
                _panel?.Flip();
                break;
            default: return;
        }

        _tank.QueueRedraw();
    }

    /// <summary>Everything back to how it started. A method rather than a case
    /// in the key switch because the panel's Reset has to be the same reset -
    /// two lists of things to clear is one list that will fall behind.
    ///
    /// All three tanks, not just the one being driven, and back to their own home
    /// cells. "How it started" is the bench as it opens: three tanks parked in a
    /// row. A reset that tidied one of them and left the other two where they had
    /// driven to would be the one key you cannot trust.</summary>
    private void ResetAll()
    {
        _spinning = false;
        _aimWithMouse = false;
        foreach (Vehicle v in _vehicles)
        {
            CancelOrder(v);
            TankSprite s = v.Sprite;
            s.HullFacing = 270.0;
            s.TurretFacing = 270.0;
            v.Pitch.Reset();
            s.Pitch = 0.0;
            v.Rumble.Reset();
            s.Shake = 0;
            s.Roll = 0.0;
            v.Tremble.Reset();
            s.TremblePitch = 0.0;
            s.TrembleRoll = 0.0;
            v.Exhaust.Reset();
            s.ExhaustPhase = -1;
            v.Track.Reset();
            s.TrackPhase = -1;
            v.Burning = false;
            v.Burn.Reset();
            s.Burning = false;
            s.FirePhase = -1;
            s.BurnPhase = -1;
            // The ceasefire is part of the reset for the reason the repair is:
            // R means the bench as it opened, and a tank left engaging would
            // start putting holes back into the armour that was just fixed.
            v.Target = null;
            v.Solution = Gunnery.None;
            v.ReloadLeft = 0.0;
            v.Hit.Reset();
            v.HitCount = 0;
            s.HitPhase = -1;
            s.Repair();
            v.Scan.Reset();
            v.Recoil.Reset();
            // The levels go back to their tuned values along with everything
            // else. They are settings rather than state, which argues the other
            // way, but a reset whose job is "back to the baseline" that leaves
            // the shake at two and a half would be lying about what it did - and
            // the tuned value is the one worth being able to get back to in one
            // key.
            v.Tremble.Level = 1.0;
            v.Recoil.Level = 1.0;
            v.ShotFrame = -1;
            s.FlashFrame = -1;
            s.ShotPhase = -1;
            s.Source = _flashSource;
            // Back to what the command line asked for, not to false: "how it
            // started" is well defined here because there is a flag, which is the
            // same reason the flash source above goes back to _flashSource.
            s.RecoilTurretOnly = _recoilTurretOnly;
            s.RecoilPitch = 0.0;
            s.RecoilRoll = 0.0;
            v.Cell = v.HomeCell;
            Park(v);
        }
        _sizeLevel = 1.0;
        ApplySize();
        PaintGunnery();
        _camera.Zoom = Vector2.One;
        _camera.Position = new Vector2(760, 500);
    }

    private void Capture(string path)
    {
        Image image = GetViewport().GetTexture().GetImage();
        Error err = image.SavePng(path);
        if (err != Error.Ok)
            GD.PushError($"screenshot to {path} failed: {err}");
        else
            GD.Print($"screenshot: {path}");
    }

    private static double Mod(double a, double n) => (a % n + n) % n;

    private static double WrapAngle(double degrees) => Mod(degrees + 180.0, 360.0) - 180.0;
}
