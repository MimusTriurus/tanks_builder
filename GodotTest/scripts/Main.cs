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

	/// <summary>The hand-drawn ground. Absolute for the reason the atlases are -
	/// redraw a hex, restart, see it - and optional for the reason the sounds
	/// are: without it the field falls back to the rendered tile and everything
	/// else is unchanged.</summary>
	private const string TerrainsRoot = "D:/Projects/AgentCoding/BlenderMCP/Images/Terrains";
	private const string PropsRoot = "D:/Projects/AgentCoding/BlenderMCP/Images/Vegetation";

	/// <summary>The pond's drawn surface. Its own folder rather than a file in
	/// Terrains, because TerrainSet reads that folder as kinds of ground and a
	/// kind is picked by hash - which would scatter open water across the
	/// hilltops. Water is a map, not paint; see <see cref="WaterArt"/>.</summary>
	internal const string WaterRoot = "D:/Projects/AgentCoding/BlenderMCP/Images/Water";
	/// <summary>The tanks to look for on disk, in key order: one per class,
	/// all three built from separate parts - hull, turret, barrel, engine and
	/// two belts as their own meshes.
	///
	/// The single-mesh HT/MT/LT used to sit alongside them so a winding belt
	/// could be judged against a still one. They are gone: their belts are not
	/// separate meshes, so they cannot wind at all, and a tank that slides on
	/// dead tracks is not a comparison, it is the old bug still on screen.
	/// The atlases stay on disk under Sprites/; nothing here loads them.</summary>
	internal static readonly string[] Tags = { "LTP", "MTP", "HTP" };

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

	/// <summary>Which directory the pixels actually come from this run:
	/// <see cref="SharedSpriteDir"/> unless `--sprites` said otherwise.
	///
	/// The flag exists because the case this is for - a layer landing on one
	/// tank before the others - is exactly the case where editing a constant and
	/// rebuilding is the wrong shape of work. It is the same argument the two
	/// slider flags make: the thing being judged is a picture, and taking it
	/// twice should not need a source edit between the two.</summary>
	private string? _spriteDir = SharedSpriteDir;

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
	/// The tracer drawn, or the round flying invisibly - key none, --tracer, or
	/// the panel. **On by default, and it switches the drawing only.**
	///
	/// The flight itself is not optional and must not become so: it is what puts
	/// time between the report and the impact, which is the whole reason the
	/// round exists (see <see cref="Shell"/>). Switching this off gives back
	/// exactly the picture the bench had before there was a tracer, with the
	/// timing that came with it - which is the A/B worth having, and would not be
	/// one if the shell stopped flying too.
	///
	/// **It was off, and the reason it was has expired.** A debug line that
	/// crept into an A/B would be measuring itself, so while the tracer was one
	/// stroke of plain code that was the right default. It is now a drawn layer
	/// with a calibre, a trail and two levels of its own - something to be judged
	/// rather than something to keep out of the way - and a feature you have to
	/// switch on to see is a feature nobody looks at. The comparison is still one
	/// click away, which is all it ever needed to be.
	///
	/// Default named here rather than left in the initialiser for the reason
	/// <see cref="Recoil.ShearOnByDefault"/> is: a default nobody can point at is
	/// a default that quietly changes.
	/// </summary>
	public const bool TracerOnByDefault = true;

	private bool _tracerVisible = TracerOnByDefault;

	/// <summary>
	/// The turret's traverse motor - --no-turret-sound, or the panel. On.
	/// 
	/// It came up off while it was the newest and least settled voice on the
	/// tank: synthesised rather than recorded, its level chosen by argument
	/// rather than by ear, and the question about a background hum - is it
	/// missed when it stops - can only be asked by stopping it. That has been
	/// asked and answered, so the default is now the tank as it sounds, and the
	/// flag turns it off for the A/B rather than on.
	/// </summary>
	public const bool TurretSoundOnByDefault = true;

	private bool _turretSound = TurretSoundOnByDefault;

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
	private TerrainSet? _terrain;
	private TrackMarks? _marks;
	private PropSet? _props;
	private Grove? _grove;

	/// <summary>Whether the board grows trees at all, and whether one may stand
	/// where it would cross a tank on its own cell. Parked here for --terrain's
	/// reason: both are read before the field exists. How *much* forest there is
	/// is the terrain paint - forest is a kind of ground, not a dial.</summary>
	private bool _trees = true;
	private bool _clearFront;
	private float _ghost = Grove.GhostByDefault;
	private double _wind = -1.0;
	private double _blast = -1.0;
	private double _brush = -1.0;

	/// <summary>Whether the belts leave anything behind. On by default: a tank
	/// that leaves no mark is the picture the bench already had, so the A/B this
	/// layer is judged by is the one that needs the flag, not the one that needs
	/// the default.</summary>
	private bool _rutsEnabled = true;

	/// <summary>Which ground the board is painted with - a kind's name, or
	/// mixed. Parked here rather than on the field because --terrain is read
	/// before the field exists, the trap named beside <c>_flashSource</c>.
	/// </summary>
	private string _paint = TerrainSet.Default;

	/// <summary>Whether the board has height on it. See --relief.</summary>
	private bool _relief;

	/// <summary>Whether the height on it may be driven. Kept apart from
	/// <see cref="_relief"/> because they are two different statements about the
	/// board: one puts levels on it, the other says where they may be crossed -
	/// and a board with levels and no ramps is exactly how this walked before
	/// there were ramps, which is the A/B. See --no-ramps.</summary>
	private bool _ramps = true;

	/// <summary>
	/// Whether this run gets ramps at all - which is to say, whether the board is
	/// being drawn by something that can draw a slope.
	///
	/// <b>Ramps are a 3D-stage feature, and that is a limit rather than a
	/// choice.</b> The 2D board draws a cell as one flat hexagon at one lift and
	/// has no way to express a tilted top, so a ramp there is a cell that paths
	/// like a slope and is painted like a step. Worse, the rule that decides what
	/// hides a tank - <see cref="HexField.Occluders"/> and <see cref="ReliefCap"/>
	/// under it - is written in whole levels, so a surface half a level up is a
	/// height it cannot answer for: measured, 24 of 28 climbs let a tank out from
	/// behind the ground early once a ramp was in the leg.
	///
	/// The stage has none of that machinery - the depth buffer answers instead -
	/// so this is the one mode where a ramp is both drawn and judged correctly.
	/// </summary>
	private bool Ramped => _ramps && _stage3d;

	/// <summary>Whether the board is flooded where the map says it is. See
	/// --no-water.</summary>
	private bool _water = true;

	/// <summary>The depth asked for on the command line, or negative for
	/// whatever the field and panel.json settle on. Parked here rather than
	/// written straight to the field for --terrain's reason: the flag is read
	/// before the field exists.</summary>
	private double _depth = -1.0;

	/// <summary>
	/// Whether this run gets water at all - which is to say, whether the board is
	/// being drawn by something that can draw a surface standing above the ground.
	///
	/// <b>A stage feature, for <see cref="Ramped"/>'s reason and not a weaker
	/// one.</b> The 2D board draws a cell as one flat hexagon and has no way to
	/// express anything standing over it, so the most it could do is tint the
	/// plate - and a tinted plate with a tank standing dry on top of it is not a
	/// ford, it is a blue cell. What makes this water is that the tank goes into
	/// it, and going into it is a height, which is the one thing the flat board
	/// has never had.
	/// </summary>
	private bool Watered => _water && _stage3d;

	/// <summary>The drawn surface, once the tile it is checked against exists.
	/// </summary>
	private WaterArt? _waterArt;

	/// <summary>Whether the pond wears its art or the flat colour. See
	/// --flat-water: the fallback colour is the art's own average, so this
	/// toggle swaps the texture and nothing else.</summary>
	private bool _waterPaint = true;

	/// <summary>What the stage is handed: the strip, or nothing when the toggle
	/// is off or the file is not on disk.</summary>
	private WaterArt? Surf => _waterPaint ? _waterArt : null;

	/// <summary>Seconds for one turn of the water's frames, and whether one frame
	/// is carried into the next. Parked here for --terrain's reason: both are read
	/// before the stage exists, and both are pushed at it every frame rather than
	/// on the drag, so a stage built later does not come up on the built-in.
	/// </summary>
	private double _swellPeriod = Stage3D.SwellPeriodDefault;
	private bool _swellBlend = Stage3D.SwellBlendDefault;

	/// <summary>How much foam a stirred cell throws, over the strip that already
	/// said it was stirred. Lives on the bench rather than on a tank for the
	/// reason every effect switch does: how foamy water is is a question about
	/// the water, and the pond is one body of it.</summary>
	private float _foam = Stage3D.FoamDefault;

	/// <summary>Whether the pond is computed or drawn from the strip. See
	/// --drawn-water: the surface as it was, which is the A/B this is judged by.
	/// </summary>
	private bool _deepWater = Stage3D.DeepDefault;

	/// <summary>Whether the board's own things cast shadows along the sun. Apart
	/// from the tank's contact shadow, which is a different claim: that one says
	/// "a tank is standing here" and commits to no direction at all, this one is
	/// the board saying where the sun is. Two switches because they are two
	/// statements, and because the A/B for either is a pair of frames the other
	/// must not be moving in.</summary>
	private bool _castShadows = true;

	/// <summary>How stirred up each flooded cell is. Built with the field, ticked
	/// here and drawn by the stage - the same division the belt marks have, and for
	/// the same reason: what the tanks did to the ground is neither the ground's
	/// business nor the renderer's.</summary>
	private Swell? _sea;

	/// <summary>The trail the tanks leave on the water. Built beside the swell
	/// and for the same division of labour: what the tanks did to the water is
	/// neither the water's business nor the renderer's.</summary>
	private Wake? _wake;

	/// <summary>Whether a trail is laid at all. See --no-wake.</summary>
	private bool _wakes = true;

	/// <summary>Whether the water piles up ahead of a wading hull. See --no-bow.
	/// </summary>
	private bool _bows = true;

	/// <summary>Whether the water answers the tanks. See --still-water: the pond
	/// as it was, which is the A/B this is judged by.</summary>
	private bool _seaReacts = true;

	/// <summary>
	/// Whether the board and the tanks are drawn by the depth buffer rather than
	/// by paint order. See <see cref="Stage3D"/>.
	///
	/// A flag while the two exist side by side, and not because the answer is in
	/// doubt - the ground rule it replaces is four members of
	/// <see cref="HexField"/> and all of <see cref="ReliefCap"/>, and it replaces
	/// them with nothing. It is a flag because the A/B is the only honest way to
	/// hand the board over one piece at a time: every measurement this bench has
	/// taken is a pixel diff of two runs, and a switch is what lets the two runs
	/// exist.
	///
	/// <b>It says which board this run has, not which board --3d asked for, and
	/// that distinction cost the ramps.</b> The flag is read before the stage
	/// exists - <see cref="SetRelief"/> hangs the ramp mask on it while the field
	/// is being built - so it cannot simply be <see cref="Staged"/>. But it was
	/// only ever written by the flag, and the stage has three other ways in: the
	/// panel row, panel.json's default for it, and the key. Opened with
	/// <c>view.stage</c> defaulted true and no <c>--3d</c> on the line, the stage
	/// came up and this stayed false, so <see cref="Ramped"/> stayed false and the
	/// board had no ramps on it - a mode switch that arrives by every path except
	/// the one the feature reads. So the setter of <see cref="Staged"/> writes it
	/// too, and the board is the truth.
	/// </summary>
	private bool _stage3d;

	/// <summary>Whether <c>--no-3d</c> was given. Held apart from
	/// <see cref="_stage3d"/> rather than clearing it, because the two are asked at
	/// different times: the flag is parsed before the field exists and the refusal
	/// has to survive <c>panel.json</c>'s default for <c>view.stage</c>, which is
	/// what closed the only door to the 2D board.</summary>
	private bool _noStage;

	private Stage3D? _stage;

	/// <summary>
	/// Whether each cell of the stage wears an outline at its rim. See
	/// <see cref="Stage3D.ShowEdges"/> for why it has to be drawn rather than
	/// uncovered.
	///
	/// Held here and not only on the stage for <see cref="Staged"/>'s rule: the
	/// stage is built and thrown away by a click, and an answer that lives only
	/// on it comes back as the default every time it is rebuilt - which is one
	/// row silently un-choosing what the user chose.
	/// </summary>
	private bool _cellEdges = true;

	/// <summary>
	/// Whether the board is drawn, asked of whichever board is drawing it.
	///
	/// <b>The G key and its panel row were writing the 2D field's flag
	/// unconditionally</b>, so under the stage either one drew the canvas board
	/// back over the entire 3D world - the same trap <see cref="FlagRows"/>
	/// exists for, except reachable by clicking rather than by editing. One
	/// definition rather than a check at each of the two, because a third caller
	/// is how the two come to disagree.
	/// </summary>
	private bool BoardShown
	{
		get => _stage?.ShowBoard ?? _field.ShowField;
		set
		{
			if (_stage is not null)
			{
				_stage.ShowBoard = value;
				return;
			}
			_field.ShowField = value;
			_field.QueueRedraw();
		}
	}

	/// <summary>
	/// Whether the depth buffer is drawing the board and the tanks.
	///
	/// <b>Live rather than a restart, and everything awkward about it is one
	/// idea: what the stage has not taken over yet is suppressed, never
	/// assigned.</b> The obvious way to keep the ruts off a 3D board is to clear
	/// the flag their own panel row reads - and then turning the stage off again
	/// hands that row back a value the user never chose. One row writing another
	/// row's field is <see cref="FlagRows"/>'s trap with the file swapped for a
	/// click. So the ruts, the trees and the 2D rings keep their own answers and
	/// are merely hidden while somebody else owns the board; the board's own row
	/// is carried across by hand, because there the two modes have genuinely
	/// different nodes to say it to.
	///
	/// The cap is the exception and is destroyed rather than hidden: a null cap
	/// is already the one word for "the depth buffer decides here" - see
	/// <see cref="Depth"/> - and a hidden one would be a second.
	/// </summary>
	private bool Staged
	{
		get => _stage is not null;
		set
		{
			if (value == Staged)
				return;
			if (value)
				StageOn();
			else
				StageOff();
			// The flag follows the board, not the other way round - see
			// <see cref="_stage3d"/>. Written before the ramps are re-asked,
			// because Ramped reads it.
			_stage3d = value;
			Suppress2D(Staged);
			// Whether a ramp exists at all is a property of who draws the board,
			// so handing the board over is a change to its heights.
			ApplyRamps();
			ApplyWater();
		}
	}

	private void StageOn()
	{
		// Asked before the switch and told after it, because the two modes keep
		// it in different places and the question is the user's, not the mode's.
		bool board = BoardShown;
		_stage = new Stage3D
		{
			Field = _field, Origin = _origin, Eye = _camera,
			// The ruts are laid in the marks' own space and read in the space a
			// tank's GroundPoint is in; both nodes are children of this one, so
			// the offset is the marks' own position. Handed over rather than
			// assumed to be zero, which is what it is - see Stage3D.MarksAt.
			Marks = _marks, MarksAt = _marks?.Position ?? Vector2.Zero,
			// The wood, by the same division: the grove goes on deciding where
			// its trees stand and how they lean, the stage draws them where the
			// depth buffer can judge them. No shift to hand over - a PropNode's
			// own position already carries Origin.
			Wood = _grove,
			// The pond's surface, by the same division as the wood: the field goes
			// on deciding which cells are wet and how deep, the stage draws the
			// surface over them.
			Surf = Surf,
			// And what each of its cells is doing, by the same division again.
			Sea = _sea,
		};
		AddChild(_stage);
		foreach (Vehicle vehicle in _vehicles)
		{
			_stage.Take(vehicle);
			// Freed now rather than queued, and measured rather than reasoned:
			// queued, the bench came down with leaked-instance warnings when the
			// switch was thrown from the panel, though not when it was thrown by
			// the flag one phase earlier. Immediate, neither does. Whatever the
			// deferred free is waiting on, it is waiting on something the panel
			// has already changed.
			vehicle.Cap?.Free();
			vehicle.Cap = null;
		}
		_field.ShowField = false;
		_stage.ShowEdges = _cellEdges;
		_stage.ShowBoard = board;
	}

	private void StageOff()
	{
		if (_stage is null)
			return;
		bool board = BoardShown;
		_stage.Give();
		_stage.QueueFree();
		_stage = null;
		foreach (Vehicle vehicle in _vehicles)
		{
			var cap = new ReliefCap { Field = _field, Marks = _marks };
			vehicle.Sprite.AddChild(cap);
			vehicle.Cap = cap;
			// A cap paints nothing until it is told where its tank stands, and
			// it is told by Depth - which otherwise runs only on a step and on
			// Park. Without this the board came back with no cliff hiding
			// anything, standing tanks drawn whole over the cell in front of
			// them, and the tank had to be driven a pixel to repair itself.
			Depth(vehicle);
		}
		_field.ShowField = board;
		_field.QueueRedraw();
	}

	/// <summary>Hide, never clear: canvas items are drawn over the entire 3D
	/// world, so every 2D thing the stage has not taken over yet has to be out
	/// of the way while it owns the board - and has to come back saying what its
	/// own row says, not what this one wanted. The list is exactly what slices B
	/// and C are.</summary>
	private void Suppress2D(bool staged)
	{
		if (_marks is not null)
			_marks.Visible = !staged;                   // the stage draws its own
		if (_grove is not null)
			_grove.Visible = !staged;                   // the stage draws its own
		if (_ring is not null)
			_ring.Visible = !staged;                    // the stage draws its own
		if (_targetRing is not null)
			_targetRing.Visible = !staged;
	}

	/// <summary>How steep one level is, or negative for the tuned default -
	/// zero is a legal setting here, being a board that is flat while still
	/// carrying levels.</summary>
	private double _grade = -1.0;

	/// <summary>
	/// The one board with height on it, until a map format exists.
	///
	/// Written as rows of characters for the reason the relief bench's is: the
	/// shape of the hill has to be legible in the source, and an int array of
	/// fifty-four entries is not.
	///
	/// <b>The hill is in front of the tanks, not behind them, and that is the
	/// whole of why it is where it is.</b> Ground only hides what is behind it,
	/// so a hill up at the top of the board is a hill nothing is ever occluded
	/// by - it would show the lift and the walls and say nothing at all about the
	/// one rule this slice is for. It sits one row in front of the home line,
	/// where standing still is already the test.
	///
	/// The three home cells stay level - all on row 2 - so the bench still opens
	/// with the tanks in a line at one height, which is the comparison every
	/// other thing here is judged by. The ditch is off to one side for the other
	/// direction: a cell below the datum is drawn <i>down</i>, and its far
	/// neighbours own the walls.
	///
	/// Clipped to the board rather than refused when the two disagree: this is a
	/// stand-in, and a board that came up empty because somebody widened the
	/// field would read as the flag not working.
	/// </summary>
	/// <summary>
	/// The board's height, one character per cell.
	///
	/// <b>A ramp is a cell of the lower of the two levels it joins</b>, marked
	/// '/' - its own level is read off the neighbours exactly as any other cell's
	/// is, and which way it tilts is derived from them by
	/// <see cref="HexField.SetRelief"/>. So a ramp up onto a plateau is a '/' on
	/// the flat beside it, and a ramp down into a pit is a '/' in the pit at its
	/// mouth.
	///
	/// <b>Every level change on this board goes through exactly one ramp, and
	/// that is the map being a map.</b> The plateaus are reached up column 3 and
	/// column 5; the pit is entered at (7,4) and opens out inside. Drive at a
	/// cliff anywhere else and the route refuses, which is what
	/// <see cref="HexField.Passable"/> is for.
	///
	/// <b>The pit had to be widened to six cells to hold its ramp.</b> A ramp
	/// wants exactly one neighbour a level above it, and at the old four-cell
	/// pit's mouth (8,4) there were three - so the map did not decide which edge
	/// was the high one and the derivation refused it, correctly. Surrounding
	/// (7,4) with pit on its other five edges is what leaves one.
	///
	/// <b>Every ramp rises away from the camera, and that is measured rather than
	/// tidy.</b> A slope's face projects by <c>sin(e) -/+ grade*cos(e)</c> of its
	/// own footprint, so one rising toward the camera is <i>compressed</i> - the
	/// first draft put the plateau ramps on row 2, where a 100px cell came out
	/// 57px tall, read as the plateau simply being one cell bigger, and pushed the
	/// ground art far enough down its mipmaps to come back as a smooth pale band.
	/// Rising away it is stretched instead, which is what a slope looks like.
	///
	/// <b>Both of a ramp's axis neighbours have to be on the board.</b> The
	/// obvious fix - ramps on row 5, the near edge of the plateau - makes them
	/// unreachable: the low edge points off the board, and a ramp is entered
	/// along its axis only, so the only way in was from the plateau it exists to
	/// reach. The plateaus moved up a row instead.
	///
	/// <b>The right-hand five columns are the rosette: one hill cell with a ramp
	/// on every one of its six flat faces.</b> The rest of this board exercises
	/// one ramp heading - 90, away from the camera - because that is the one that
	/// looks right, and a slope is drawn from a corner pattern rotated to its high
	/// edge, so five of the six rotations were never rendered at all. Here they
	/// all are, and the hill is reachable six ways.
	///
	/// <b>Two of them rise toward the camera and read badly, and that is the
	/// point rather than an oversight.</b> A face rising at the viewer projects to
	/// <c>sin(e) - grade*cos(e)</c> of its footprint against <c>sin(e) +
	/// grade*cos(e)</c> going away, so the near pair is compressed to about half
	/// the far pair's height. The lesson is written on the plateau ramps above,
	/// which is where the map obeys it; this patch is where it can be seen.
	///
	/// <b>The ring cells are all adjacent to each other and none of them may be
	/// driven to another.</b> Two adjacent ramps are refused by
	/// <see cref="HexField.Passable"/>, so the way from one face to the next is
	/// out to the flat, round, and back in - which is what makes each of the six
	/// its own test rather than one lap of the hill.
	/// </summary>
	private static readonly string[] Relief =
	{
		"..............",
		"..............",
		"...1.1........",
		"...1.1.....1..",
		"......vvv.....",
		"......vvv.....",
	};

	/// <summary>Where the ramps are. Kept beside <see cref="Relief"/> rather than
	/// mixed into it because the two answer different questions about a cell -
	/// how high it stands, and whether its top is flat - and a ramp reads its own
	/// level out of the same grid every other cell does.
	///
	/// The six around column 11 are the rosette's, and they are not placed by eye:
	/// they are <c>Step((11,3), h)</c> for each of the six edge headings, and their
	/// entry cells are one more step of the same heading. Both had to land on the
	/// board, which is what set the width at fourteen columns.</summary>
	private static readonly string[] Ramps =
	{
		"..............",
		"..............",
		"...........r..",
		"..........r.r.",
		"...r.r.r..rrr.",
		"..............",
	};

	public static int[] ReliefMap(int columns, int rows)
	{
		var levels = new int[columns * rows];
		for (int r = 0; r < rows && r < Relief.Length; r++)
		for (int q = 0; q < columns && q < Relief[r].Length; q++)
			levels[r * columns + q] = Relief[r][q] switch
			{
				'1' => 1,
				'2' => 2,
				'v' => -1,
				_ => 0,
			};
		return levels;
	}

	/// <summary>Which cells carry a ramp. Separate from <see cref="ReliefMap"/>
	/// so a board can be given height without them - which is what every relief
	/// assertion written before ramps existed runs on, and what
	/// <see cref="HexField.Passable"/> falls back to.</summary>
	public static bool[] RampMask(int columns, int rows)
	{
		var ramps = new bool[columns * rows];
		for (int r = 0; r < rows && r < Ramps.Length; r++)
		for (int q = 0; q < columns && q < Ramps[r].Length; q++)
			ramps[r * columns + q] = Ramps[r][q] == 'r';
		return ramps;
	}

	/// <summary>
	/// Which cells are flooded. A third grid beside the levels and the ramps,
	/// because water is a third statement about a cell and not a shade of either
	/// of the other two - see <see cref="HexField.SetWater"/>.
	///
	/// <b>It is the pit, and putting it there is most of the point.</b> The pit
	/// already exists, it is already a level down, and it already has exactly one
	/// way in - so the pond is somewhere a tank has to drive down to rather than a
	/// puddle standing on the plain, and the first order that reaches it walks a
	/// ramp and a ford back to back, which is both speed ceilings in one leg.
	///
	/// <b>The mouth cell (7,4) is dry, and it has to be.</b> It is the pit's ramp,
	/// and a slope has no one surface height for water to stand at - the guard
	/// refuses it rather than wedging the pond, which is the same refusal the ramp
	/// derivation makes about an undecided high edge. What that leaves is a beach:
	/// the tank comes down the slope on dry ground and enters the water at (7,5),
	/// which is where a ford is entered from.
	///
	/// <b>Painted onto a flat board it is still legal, and that is the A/B.</b>
	/// With no relief every level is nought, so the five cells are one flat pond on
	/// open ground - no ramp under it to refuse and no step between them to be a
	/// waterfall.
	/// </summary>
	private static readonly string[] Water =
	{
		"..............",
		"..............",
		"..............",
		"..............",
		"......w.w.....",
		"......www.....",
	};

	public static bool[] WaterMask(int columns, int rows)
	{
		var water = new bool[columns * rows];
		for (int r = 0; r < rows && r < Water.Length; r++)
		for (int q = 0; q < columns && q < Water[r].Length; q++)
			water[r * columns + q] = Water[r][q] == 'w';
		return water;
	}

	/// <summary>What the terrain dropdown offers: mixed, then whatever loaded.
	/// Built from the set rather than listed here, so drawing a new hex is a
	/// file drop and nothing else.</summary>
	private readonly List<string> _paints = new() { TerrainSet.Mixed };

	/// <summary>The two things a turret can do while the hull turns under it,
	/// in the order the panel offers them. Holding is index 0 because it is what
	/// the layered atlases exist to show.</summary>
	private static readonly string[] TurretModes =
		{ "holds its heading", "rides the hull" };

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

	/// <summary>What the view opens at. See --zoom, and the panel's missing zoom
	/// row for why this is a flag and not a slider.</summary>
	private float _zoomAt = 1.0f;

	/// <summary>Where the view is parked, and where R puts it back.
	///
	/// Read off the board rather than written down. It was <c>(760, 500)</c>,
	/// which was the middle of a nine-column board and became the middle of
	/// nothing when the board went to fourteen - the bench opened on the left half
	/// with the rosette off screen, so seeing the thing it was widened for took a
	/// drag of the middle button every run. Flat anchors, not lifted ones: which
	/// way the view looks should not move when a hill is put on the board.</summary>
	private Vector2 ViewHome =>
		_origin + (_field.FlatAnchor(0, 0)
				   + _field.FlatAnchor(_field.Columns - 1, _field.Rows - 1)) * 0.5f
		+ _field.CentreOffset;

	/// <summary>The view's own spring. One for the board, not one per tank: the
	/// camera is a single thing and three guns firing into it is three impulses
	/// into one spring, which is what compounding means and what a shake per
	/// vehicle could not express.</summary>
	private readonly CameraShake _shake = new();

	private bool _shakeOn = CameraShake.OnByDefault;

	private readonly Vector2 _origin = new(220, 200);

	/// <summary>The row the comparison line stands on. Named because the self-test
	/// asks which tanks are on it, and a literal there would be a second copy of
	/// this one.</summary>
	public const int HomeRow = 2;

	/// <summary>Where the three are parked: one row, every other column. The same
	/// row means the same screen height, and two columns apart - 372px against a
	/// heavy's 212px of hull - means the silhouettes never touch, so what the eye
	/// compares is size rather than spacing.
	///
	/// <b>The light stands on the rosette's hill instead, and it costs the thing
	/// the line is for.</b> Three abreast at one height is how the class sizes are
	/// judged - see MovementProfile.Size - and with the light on (11,3) that
	/// comparison is down to a pair. It is there because a hill with six approaches
	/// is worth having a tank on: the lean, the standing height and the six ramps
	/// meeting the top are all things a tank on the summit says at a glance and an
	/// empty summit does not. R puts it back there rather than into the line, which
	/// is what makes it a home and not a start.</summary>
	private static readonly Vector2I[] HomeCells =
		{ new(11, 3), new(4, HomeRow), new(6, HomeRow) };

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

	/// <summary>
	/// How hard the engines shake, over the tuned pair - the panel's slider and
	/// --tremble.
	///
	/// <b>Held here rather than on the driven tank's own
	/// <see cref="EngineTremble.Level"/>, which is where it was, and that was the
	/// bug.</b> How hard an engine shakes is a question about the effect, so it
	/// reaches all three - the same rule the switch beside it follows, and the
	/// reason the switch has always been a field here. Written on the vehicle it
	/// left the other two shaking at the tuned figure, so dragging the slider
	/// changed a third of the board and read as a slider that did nothing.
	///
	/// Pushed in <see cref="UpdateTremble"/> rather than on the drag, so a tank
	/// built later cannot miss it.
	/// </summary>
	private double _trembleLevel = 1.0;

	private TurretScan _scan => Active.Scan;
	/// <summary>Idle turret traverse - key N, or --scan.</summary>
	private bool _scanEnabled;

	private TrackLoop _track => Active.Track;
	/// <summary>The belts winding - key C, or --no-tracks. On by default, and
	/// not really an option: a tank has tracks and they move. The switch is so
	/// the hull layer can be looked at without them.</summary>
	private bool _tracksEnabled = true;

	/// <summary>Whether the tanks are drawn standing on their shadows.
	///
	/// No key, and that is not an oversight: A-Z are all bound, and so are
	/// Space, Tab, Escape, F12, Key1..Key9, '[' and ']'. The panel is where a
	/// switch goes once the mnemonics have run out - the tracer and the traverse
	/// motor are there for the same reason - and the flag exists because a
	/// screenshot is the evidence and taking it twice should not need a hand on
	/// the mouse.</summary>
	private bool _shadowEnabled = true;

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

	/// <summary>
	/// How fast the plume walks its loop, as a multiplier over the pair of rates
	/// - see <see cref="ExhaustLoop.Level"/>.
	///
	/// The board's, not the driven tank's, for the reason the tremble's is: how
	/// hard the exhaust cycles is a question about the effect and not about one
	/// machine, so it reaches all three. A level written onto the selected
	/// vehicle moves a third of the board and reads as a slider that does
	/// nothing.
	///
	/// Pushed in <see cref="UpdateExhaust"/> rather than on the drag, so a tank
	/// built later cannot miss it.
	/// </summary>
	private double _exhaustLevel = 1.0;

	/// <summary>
	/// The analogue load ramp instead of the two states - the panel's "load
	/// ramp" row, or --exhaust-ramp. Off by default, and the only effect switch
	/// here that names a superseded model rather than an optional effect: the
	/// fire is off because a burning tank is exceptional, the hull shear because
	/// the tube recoils for real, and this because the ramp was a faithful model
	/// of something twelve rendered poses cannot carry - see
	/// <see cref="ExhaustLoop.Binary"/>.
	///
	/// Kept rather than deleted for the reason the flash sheet is kept: "the step
	/// reads better" stays a claim until both are on screen.
	/// </summary>
	private bool _exhaustRamp = !ExhaustLoop.BinaryByDefault;

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

	/// <summary>Whether <c>--capture-at</c> was on the line.
	///
	/// <b>Named because --drive used to overwrite it and say nothing.</b> An order
	/// carries its own default frame - ninety, far enough along to be mid-path -
	/// and it wrote that over an explicit frame whenever --drive came after
	/// --capture-at on the command line. Which is a flag that works or does not by
	/// argument order, and it fails in the quietest possible way: the capture is
	/// taken, of the right board, at the wrong moment. Two frames of a pond that
	/// nobody had driven into yet compared identical, and the conclusion drawn
	/// from them was that the water did not answer the tanks.</summary>
	private bool _captureAsked;
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

	/// <summary>Whether the driven tank opens already destroyed. Held here
	/// rather than written straight through the property, for the reason every
	/// other start-up state is: the flags are parsed before the vehicles exist,
	/// and reaching for "the driven tank" then is a read of an empty list -
	/// which hung --capture rather than failing it.</summary>
	private bool _deadAtStart;
	private double _recoilAtStart = 1.0;
	private bool _rollOnly;

	private bool Moving => _pathStep < _path.Count;

	/// <summary>
	/// The step every animated thing on the bench takes, or null to run on the
	/// real one. Set once a capture or a trace is asked for.
	///
	/// <b>Static because the pin has to reach past this node, and once did not.</b>
	/// It used to be a local in <see cref="_Process"/>, which pinned the tanks and
	/// left <see cref="Stage3D"/> - a sibling with a _Process of its own - turning
	/// the pond on the wall clock. So the water was never the same twice under
	/// --capture, and every A/B taken over it carried that as noise: measured at
	/// <b>58 446 px, peak 10</b>, in a box that is exactly the pond. Anything the
	/// bench animates from here on reads this, or it is outside the guarantee
	/// --capture exists to give.
	/// </summary>
	public static double? FixedStep;

	public override void _Ready()
	{
		string[] userArgs = OS.GetCmdlineUserArgs();
		if (Array.IndexOf(userArgs, "--capture") >= 0
			|| Array.IndexOf(userArgs, "--trace") >= 0)
			FixedStep = 1.0 / 60.0;
		// Which panel rows the command line has spoken for, so panel.json does
		// not then overrule it. The order is compiled-in default, then the file,
		// then the flag: a flag is the more specific statement, and a capture
		// taken with one has to mean what it says whatever the file holds.
		//
		// Read off the arguments here rather than set inside each branch,
		// because there are twenty-odd of them and a branch that forgot to
		// claim its row would fail in the quietest way there is - the flag
		// works, and then the file undoes it a hundred lines later.
		foreach (string arg in userArgs)
		{
			string name = arg.Split('=')[0];
			if (FlagRows.TryGetValue(name, out string[]? claimed))
				foreach (string id in claimed)
					_flagged.Add(id);
		}
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
			{
				_captureAfter = at;
				_captureAsked = true;
			}
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
			else if (userArgs[i] == "--no-shadow")
				_shadowEnabled = false;
			else if (userArgs[i] == "--no-cast-shadows")
				_castShadows = false;
			else if (userArgs[i] == "--no-sound")
				_soundEnabled = false;
			// On, so the shape here is --no-mcp turning it off - and --mcp kept
			// beside it because it was the flag that switched it on, and a flag
			// that silently stops existing reads as a flag that stopped working.
			// Neither reaches --capture, --trace or --selftest: those refuse the
			// connection at StartMcp whatever was asked for, which is where a
			// guarantee about evidence belongs. See Main.Mcp.cs.
			else if (userArgs[i] == "--mcp")
				_mcpEnabled = true;
			else if (userArgs[i] == "--no-mcp")
				_mcpEnabled = false;
			// Both off by default, so both flags switch *on* - the opposite
			// shape to --no-tracks and its neighbours, and the same shape as
			// --pitch and --rumble, which are also off.
			else if (userArgs[i] == "--tracer")
				_tracerVisible = true;
			else if (userArgs[i] == "--no-tracer-smoke")
				Shell.SmokeOn = false;
			else if (userArgs[i] == "--smoke-life" && i + 1 < userArgs.Length
					 && double.TryParse(userArgs[i + 1], NumberStyles.Float,
						 CultureInfo.InvariantCulture, out double smokeLife))
				Shell.SmokeSeconds = (float)smokeLife;
			// Asking for the size is asking for the tracer, the shape
			// --recoil <x> already set: a flag that tuned something invisible
			// would look like a flag that did not arrive.
			else if (userArgs[i] == "--tracer-size" && i + 1 < userArgs.Length
					 && double.TryParse(userArgs[i + 1], NumberStyles.Float,
						 CultureInfo.InvariantCulture, out double tracerLevel))
			{
				Shell.TracerLevel = tracerLevel;
				_tracerVisible = true;
			}
			// Same shape, and asking for the trail's size is asking for the
			// trail: a level over a thing that is switched off is a flag that
			// did not arrive as far as the picture is concerned.
			else if (userArgs[i] == "--smoke-size" && i + 1 < userArgs.Length
					 && double.TryParse(userArgs[i + 1], NumberStyles.Float,
						 CultureInfo.InvariantCulture, out double smokeLevel))
			{
				Shell.SmokeLevel = smokeLevel;
				Shell.SmokeOn = true;
				_tracerVisible = true;
			}
			else if (userArgs[i] == "--shell-speed" && i + 1 < userArgs.Length
					 && double.TryParse(userArgs[i + 1], NumberStyles.Float,
						 CultureInfo.InvariantCulture, out double shellLevel))
				Shell.SpeedLevel = shellLevel;
			else if (userArgs[i] == "--no-turret-sound")
				_turretSound = false;
			else if (userArgs[i] == "--no-barrel-recoil")
				_recoilTube = false;
			else if (userArgs[i] == "--burning")
				_burnAtStart = true;
			else if (userArgs[i] == "--destroy")
				_deadAtStart = true;
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
				// Straight onto the board's level, not onto a start-up copy of it:
				// the vehicles read it every frame, so there is nothing to wait
				// for - see _trembleLevel. The copy was needed while the slider
				// wrote one tank's own amplitude and the flag had to write three.
				_trembleLevel = trembleLevel;
			// Asking for a rate is not asking for the effect, unlike the recoil
			// level: the exhaust is on by default, so --no-exhaust and this one
			// are about different things and neither implies the other.
			else if (userArgs[i] == "--exhaust" && i + 1 < userArgs.Length
					 && double.TryParse(userArgs[i + 1], NumberStyles.Float,
						 CultureInfo.InvariantCulture, out double exhaustLevel))
				_exhaustLevel = exhaustLevel;
			// A mode and not a level, so it gets a bare flag - the same shape as
			// --recoil-turret. It does not imply --exhaust: the level means the
			// same thing to both models, because it multiplies the pair either
			// way.
			else if (userArgs[i] == "--exhaust-ramp")
				_exhaustRamp = true;
			// Straight onto the static rather than waiting for the vehicles like
			// the tremble does: this one is not held on a machine, because the
			// rate has two readers that share no object. See Gunnery.TraverseRate.
			else if (userArgs[i] == "--traverse" && i + 1 < userArgs.Length
					 && double.TryParse(userArgs[i + 1], NumberStyles.Float,
						 CultureInfo.InvariantCulture, out double traverseLevel))
				Gunnery.TraverseLevel = traverseLevel;
			// Both of these turn the shear back on as well as configuring it.
			// It is off by default now that the tube recoils for real, and a flag
			// that set a level on a switched-off effect would look like a flag
			// that did not arrive - the same failure --hit-scale had when the
			// decimal separator ate it.
			// On, and optionally at a level: `--shake` alone turns it on, and a
			// number after it sets the level too. One flag rather than two
			// because they are one question - the level is meaningless with the
			// shake off, so asking for a level is asking for the shake.
			else if (userArgs[i] == "--shake")
			{
				_shakeOn = true;
				if (i + 1 < userArgs.Length
					&& double.TryParse(userArgs[i + 1], NumberStyles.Float,
						CultureInfo.InvariantCulture, out double shakeLevel))
				{
					_shake.Level = shakeLevel;
					i++;
				}
			}
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
			else if (userArgs[i] == "--sprites" && i + 1 < userArgs.Length)
				_spriteDir = userArgs[++i];
			// A kind's name, or "mixed". A capture of one kind against another
			// is the whole reason a paint exists, and it must not need a hand on
			// the mouse - the argument every other A/B flag here makes.
			else if (userArgs[i] == "--terrain" && i + 1 < userArgs.Length)
				_paint = userArgs[++i];
			else if (userArgs[i] == "--no-ruts")
				_rutsEnabled = false;
			// Height on the board. A flag rather than the default, because
			// everything the bench measures is a pixel difference between two
			// captures and a hill in the frame is a hill in both halves of every
			// one of them. Off, the field draws exactly what it drew before there
			// was relief - see HexField.HasRelief.
			else if (userArgs[i] == "--relief")
				_relief = true;
			// The A/B the ramps are judged by, and the reason it is a flag rather
			// than only a panel row: a screenshot is the evidence, and taking it
			// twice must not need an edit between the two. Off, every level
			// change goes back to being a cliff a tank may simply climb, which is
			// how the board walked before ramps - see HexField.Passable.
			else if (userArgs[i] == "--no-ramps")
				_ramps = false;
			// The A/B the water is judged by, and the only one there is: the pond
			// is five cells of one board, so what it costs and what it looks like
			// are both differences against that same board dry.
			else if (userArgs[i] == "--no-water")
				_water = false;
			// And the way back on, for a run whose panel.json turned it off.
			else if (userArgs[i] == "--water")
				_water = true;
			// The pond wearing the flat colour instead of its own surface. A
			// second A/B inside the first, and a narrow one on purpose: the
			// fallback colour is the strip's measured average, so the two frames
			// differ by the texture and by nothing else. Anything the paint is
			// blamed for that survives this flag is the water, not the art.
			else if (userArgs[i] == "--flat-water")
				_waterPaint = false;
			// How fast the swell goes round, in seconds. Seconds and not a level,
			// because there is no per-class triple under it - the same call
			// --smoke-life made.
			else if (userArgs[i] == "--swell" && i + 1 < userArgs.Length
					 && double.TryParse(userArgs[i + 1], NumberStyles.Float,
										CultureInfo.InvariantCulture, out double turn))
			{
				_swellPeriod = turn;
				i++;
			}
			// And the stepped loop back, which is the A/B the blend is judged by:
			// four frames cannot be made smooth by choosing when to change them,
			// so this is the only way to see what choosing when actually buys.
			else if (userArgs[i] == "--hard-swell")
				_swellBlend = false;
			// The pond that answers nobody, which is the A/B this is judged by:
			// the chop and the splash are the only things on the board that read
			// the tanks, so the difference against a still pond is the whole
			// claim. Not the same as --flat-water - that takes the picture away,
			// this leaves it and takes away the reaction.
			else if (userArgs[i] == "--still-water")
				_seaReacts = false;
			// The pond with the strip doing the talking on its own, which is the
			// A/B the foam is judged by - and it is the strong form of it: off
			// has to give back the very same picture, byte for byte, or the foam
			// is not a detail on the band but a replacement for it.
			// The drawn strip back, which is the A/B the computed surface is
			// judged by. Not the same as --flat-water: that one chooses between
			// two ways of drawing the strip, this chooses whether there is a
			// strip at all.
			// The water without a trail on it, which is the A/B the wake is
			// judged by. Its own flag rather than a share of --still-water: the
			// swell is what a cell is doing and this is a line across cells, and
			// one switch for both would measure two things.
			else if (userArgs[i] == "--no-wake")
				_wakes = false;
			// And the water without a crest ahead of the hull, which is the A/B
			// the bow is judged by. Its own flag beside --no-wake and not a share
			// of it: what the water does in front of a tank and what it does
			// behind are two statements, and an A/B of either wants the other to
			// hold still.
			else if (userArgs[i] == "--no-bow")
				_bows = false;
			else if (userArgs[i] == "--drawn-water")
				_deepWater = false;
			else if (userArgs[i] == "--no-foam")
				_foam = 0.0f;
			// How much of it, over the row above rather than instead of it: asking
			// for an amount is asking for the foam, the argument --recoil <x>
			// already makes.
			else if (userArgs[i] == "--foam" && i + 1 < userArgs.Length
					 && float.TryParse(userArgs[i + 1], NumberStyles.Float,
									   CultureInfo.InvariantCulture, out float froth))
			{
				_foam = Mathf.Max(0.0f, froth);
				i++;
			}
			// How deep, over the row above rather than instead of it: a depth
			// asked for is water asked for, or the flag would look like one that
			// did not arrive - the argument --recoil <x> already makes.
			else if (userArgs[i] == "--depth" && i + 1 < userArgs.Length
					 && double.TryParse(userArgs[i + 1], NumberStyles.Float,
										CultureInfo.InvariantCulture, out double sunk))
			{
				_depth = sunk;
				_water = true;
				i++;
			}
			// Height is the whole reason this exists, so asking for the stage
			// asks for the board that needs it - the argument --grade already
			// makes for --relief.
			else if (userArgs[i] == "--3d")
			{
				_stage3d = true;
				_relief = true;
			}
			// And the way back, which panel.json's default for view.stage took
			// away: the stage opens by default now, so the 2D board - the thing
			// every measurement on the stage is measured against - had no path
			// from the command line at all. A flag rather than an edit to the
			// file, by the rule the whole bench runs on: a capture is evidence,
			// and taking it twice must not need an edit between the two.
			else if (userArgs[i] == "--no-3d")
				_noStage = true;
			// The A/B the outline is judged by, and the reason it is a flag: the
			// question it answers is whether the board still reads as ground with
			// a line on every cell, and that is two captures, not a hand on the
			// mouse.
			else if (userArgs[i] == "--no-cell-edges")
				_cellEdges = false;
			// Invariant culture, the trap named beside --hit-scale.
			else if (userArgs[i] == "--grade" && i + 1 < userArgs.Length
					 && double.TryParse(userArgs[i + 1], NumberStyles.Float,
										CultureInfo.InvariantCulture, out double grade))
			{
				i++;
				_grade = grade;
				_relief = true;     // asking how steep it is asks for it
			}
			// The one control the panel refuses to carry, and for the reason it
			// refuses: zoom moves the whole picture, so a row on it could silently
			// rescale any A/B taken from the panel, and one measurement has already
			// been lost to a stray wheel over the window. A start-up flag has none
			// of that - it is fixed for the run and printed by --trace - and the
			// board is now wider than the window, so a capture of all of it needs
			// this. Around the view's own centre, which is where the wheel zooms.
			else if (userArgs[i] == "--zoom" && i + 1 < userArgs.Length
					 && float.TryParse(userArgs[i + 1], NumberStyles.Float,
									   CultureInfo.InvariantCulture, out float zoomAt))
			{
				i++;
				_zoomAt = Mathf.Clamp(zoomAt, 0.25f, 8.0f);
			}
			// No number: how much forest there is comes from --terrain, because
			// forest is one of the kinds of ground. This is the A/B - the same
			// board with the tree layer off.
			else if (userArgs[i] == "--no-forest")
				_trees = false;
			// Invariant culture, the trap named beside --hit-scale.
			else if (userArgs[i] == "--ghost" && i + 1 < userArgs.Length
					 && float.TryParse(userArgs[i + 1], NumberStyles.Float,
									   CultureInfo.InvariantCulture, out float ghost))
			{
				i++;
				_ghost = Math.Clamp(ghost, 0.0f, 1.0f);
			}
			else if (userArgs[i] == "--clear-front")
				_clearFront = true;
			// Invariant culture, the trap named beside --hit-scale. Zero is
			// still weather - the still board - so this is not a switch.
			else if (userArgs[i] == "--wind" && i + 1 < userArgs.Length
					 && double.TryParse(userArgs[i + 1], NumberStyles.Float,
										CultureInfo.InvariantCulture,
										out double gust))
			{
				i++;
				_wind = Math.Max(0.0, gust);
			}
			// Same shape as --wind and for the same reason: zero is the A/B, a
			// board where a gun goes off in a wood and the wood does not notice.
			else if (userArgs[i] == "--blast" && i + 1 < userArgs.Length
					 && double.TryParse(userArgs[i + 1], NumberStyles.Float,
										CultureInfo.InvariantCulture,
										out double blast))
			{
				i++;
				_blast = Math.Max(0.0, blast);
			}
			else if (userArgs[i] == "--brush" && i + 1 < userArgs.Length
					 && double.TryParse(userArgs[i + 1], NumberStyles.Float,
										CultureInfo.InvariantCulture,
										out double brush))
			{
				i++;
				_brush = Math.Max(0.0, brush);
			}
			else if (userArgs[i] == "--drive" && i + 1 < userArgs.Length)
			{
				string[] parts = userArgs[i + 1].Split(',');
				if (parts.Length == 2
					&& int.TryParse(parts[0], out int col)
					&& int.TryParse(parts[1], out int row))
				{
					_driveTo = new Vector2I(col, row);
					// Long enough to be mid-path, and only when nobody said
					// when - see _captureAsked. Written unconditionally, this
					// line made --capture-at work or not by which side of
					// --drive it was written on.
					if (!_captureAsked)
						_captureAfter = 90;
				}
			}
		}

		// A capture is evidence, so it gets no panel unless one is asked for.
		if ((_capturePath is not null || _traceFrames > 0) && !_showPanel)
			_noPanel = true;

		var failures = new List<string>();
		foreach (string tag in Tags)
		{
			AtlasSet atlas = AtlasSet.Load(SpritesRoot, tag, _spriteDir ?? tag);
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

		// Loaded but never required, exactly like the sound: a missing set does
		// not go into `failures`, because that list is about a bench that cannot
		// show what it was asked to show, and this one still can.
		_terrain = TerrainSet.Load(TerrainsRoot);
		GD.Print("terrain: " + _terrain.Note);
		_paints.AddRange(_terrain.Names);
		// Forest is a kind too, and the one that is not a file - so it is not
		// held against the loaded plates here. Whether it can be offered at all
		// is settled a few lines down, once the props are in.
		if (_paint != TerrainSet.Mixed && _paint != TerrainSet.Forest
			&& !_terrain.Has(_paint))
		{
			GD.PushWarning($"--terrain {_paint} is not one of "
						   + string.Join(", ", _terrain.Names) + "; using mixed");
			_paint = TerrainSet.Mixed;
		}

		_props = PropSet.Load(PropsRoot);
		GD.Print("props: " + _props.Note);
		// Offered only when there is something to grow. A board scattered with
		// cells that claim to be woods and are soil would be worse than one with
		// no woods on it - the same argument that refuses a --terrain naming a
		// kind that did not load.
		_trees &= _props.Any;
		if (_trees)
			_paints.Add(TerrainSet.Forest);
		if (_paint == TerrainSet.Forest && !_trees)
		{
			GD.PushWarning("--terrain forest with no tree art; using mixed");
			_paint = TerrainSet.Mixed;
		}

		_field = new HexField { Terrain = _terrain, Paint = _paint, Trees = _trees };
		if (_grade >= 0.0)
			_field.StepGrade = _grade;
		if (_depth >= 0.0)
			_field.WaterDepth = _depth;
		if (_relief)
			_field.SetRelief(ReliefMap(_field.Columns, _field.Rows),
							 Ramped ? RampMask(_field.Columns, _field.Rows) : null);
		// After the relief and never before it: the water's guards are asked about
		// levels and ramps, so water laid on a board that has not got its heights
		// yet is water judged against a board that does not exist.
		if (Watered)
			_field.SetWater(WaterMask(_field.Columns, _field.Rows));
		// After the mask, because the cells it keeps a state for are the field's
		// list and that list is settled by SetWater.
		_sea = new Swell { Field = _field, Enabled = _seaReacts };
		_wake = new Wake { Enabled = _wakes };
		AddChild(_field);
		_marks = new TrackMarks { Enabled = _rutsEnabled };
		AddChild(_marks);
		// Added before the vehicles, so a tree and a tank that come out on the
		// same foot row - the same ZIndex - resolve to the tank. Being stood on
		// is the tank's job.
		_grove = new Grove
		{
			Field = _field, Props = _props, Origin = _origin,
			Enabled = _trees, ClearFront = _clearFront, Ghost = _ghost,
		};
		// Negative means the flag was not given, so the tuned default stands -
		// zero is a legal setting here, not an absence, because a still board
		// is the A/B this one is judged against.
		if (_wind >= 0.0)
			_grove.Wind = _wind;
		if (_blast >= 0.0)
			_grove.Blast = _blast;
		if (_brush >= 0.0)
			_grove.Brushing = _brush;
		AddChild(_grove);

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
			// The ground that gets to stand in front of this one. A child of the
			// sprite, so its z index is relative to the tank's and no cell
			// repainted for this tank can land on another one - see ReliefCap.
			// Built whether or not the board has relief: it draws nothing at all
			// on a flat one, and a node that only exists under a flag is a node
			// nobody exercises.
			// Built here even when the bench is about to open on the stage, and
			// taken away by StageOn: the stage is a switch now, so "which mode
			// did we start in" must stop deciding which nodes exist at all.
			var cap = new ReliefCap { Field = _field, Marks = _marks };
			sprite.AddChild(cap);
			vehicle.Cap = cap;
			_vehicles.Add(vehicle);
			// Phases come off each layer, never assumed: the renderer's count is
			// a config value, and a clock that wraps anywhere but at the seam
			// pops there.
			foreach (TrackLoop belt in new[] { vehicle.TrackLeft, vehicle.TrackRight })
			{
				belt.Phases = vehicle.Atlas.TrackPhases;
				belt.Pitch = vehicle.Atlas.TrackPitch;
			}
			// What stops three turrets swaying in step - see TurretScan.Seed.
			// Its place in the list rather than its tag: the identity wanted here
			// is "which machine on this board", and --sprites can put three
			// machines on one tank's pixels without making them one machine.
			// Not zero for the first, so a seed left unset is distinguishable
			// from a seed meant to be the first tank's.
			vehicle.Scan.Seed = (ulong)i + 1UL;
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
					TurretMotor = _turretSound,
				};
				AddChild(audio);
				vehicle.Audio = audio;
			}
		}

		// Judging whether two layers line up is a pixel-level question, so the
		// harness has to be able to get close. Sprites are drawn at 1:1 by
		// default; zoom only changes the view, never the atlas sampling.
		// Parked below, once the field has its atlas: ViewHome is measured in tiles
		// and HexField.FlatAnchor answers Vector2.Zero until there is one, so asked
		// here it would quietly hand back the origin - which is a corner of the
		// board and looks like a view that simply was not moved.
		_camera = new Camera2D { Zoom = new Vector2(_zoomAt, _zoomAt), Enabled = true };
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
		// And now the view can be parked on the board, which is measured in that
		// tile - see the camera above.
		_camera.Position = ViewHome;
		// Said out loud rather than drawn crooked. The hexagon's aspect ratio is
		// its camera angle, and CellAt inverts the *rendered* tile's - so ground
		// drawn at another elevation stops the hexagon on screen being the cell
		// under the mouse, and the miss grows toward the board's edge rather
		// than showing up in the middle where anyone would look.
		// The pond's surface, here rather than beside the terrain, because it is
		// checked against the rendered tile and the tile is only settled on the
		// line above. Optional the same way: no strip on disk leaves the water the
		// flat colour it was, which is the A/B the toggle is for.
		_waterArt = WaterArt.Load(WaterRoot, _field.Atlas.HexRect);
		GD.Print("water: " + _waterArt.Note);
		if (_terrain is not null && _terrain.Any
			&& !_terrain.CameraAgrees(_field.Atlas.HexRect))
			GD.PushWarning(
				$"terrain art is {_terrain.CameraError(_field.Atlas.HexRect) * 100.0f:F0}%"
				+ " off the rendered tile's camera - clicks will drift from the"
				+ " hexagons they land on");
		// The stage, before the tanks are parked: it reparents each sprite into
		// its own render target, and a sprite moved after it has been placed
		// would be placed in the space it has just left.
		if (_stage3d)
			Staged = true;
		ApplySize();
		foreach (Vehicle vehicle in _vehicles)
			Park(vehicle);
		SowGrove();
		// Once, here, rather than inside the sowing: it runs again on every
		// slider drag, and a log line per drag is a log nobody reads.
		if (_grove is not null)
			GD.Print("grove: " + _grove.Note());
		// The flag may have picked a tank other than the first, and the trim is
		// set on selection - so it has to be set once at the start too, or two of
		// the three come up forward until something is clicked.
		FocusSound();
		// Same shape: --no-shadow is parsed before any vehicle exists, so the
		// switch has to be pushed once the vehicles do. The trap this repeats is
		// named beside `_flashSource` - flags that wrote straight through
		// "the driven tank" hung the capture instead of failing it.
		ShadowChanged();

		// The mark on whoever is being driven. Absent under --capture and --trace
		// for the reason the panel is: a capture is evidence, and an A/B of two
		// renders must not differ by a marker. --ui brings both back.
		// On the stage the ring is geometry lying on the cell - see
		// Stage3D.BuildRing. A canvas ring would be drawn over the tanks, and a
		// mark that covers what it points at is not a mark. So both are built
		// whatever mode the bench opens in and merely hidden while the stage
		// owns the ground - the switch is live now, and a node that exists only
		// in one mode cannot be switched into the other.
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
			Suppress2D(Staged);
		}

		// Before the self-test, which now checks the rows against panel.json,
		// and before the start-up flags, so the panel's first Sync sees what they
		// did rather than the defaults.
		//
		// Built whether or not it is shown, because the rows are now the only
		// path panel.json has into the harness - each opening value goes through
		// the very setter the checkbox and the key use, so the file cannot reach
		// anything the panel cannot. A capture wants the file's settings just as
		// much as a session does; what --no-ui withholds is the widget, not the
		// wiring. Attached to the tree only when it is to be seen: standing off
		// the tree, Prepare() has already made the frame the rows go into, which
		// is the same thing the self-test relies on.
		// The per-class tracer and trail sizes, beside the panel's file because
		// they are the same kind of thing: authored numbers that want restarting
		// rather than rebuilding to change. Applied here rather than at parse time
		// because there is nothing to race - the file sets the class triple, the
		// flags set a level over it, so neither can overrule the other.
		_classConfig = ClassConfig.Load(null);
		if (_classConfig.Loaded)
			_classConfig.Apply();
		else
			GD.Print($"[classes] {ClassConfig.FileName}: {_classConfig.Error}"
					 + " - falling back to the compiled per-class figures");

		_panelText = PanelText.Load(null);
		if (!_panelText.Loaded)
			GD.Print($"[panel] {PanelText.FileName}: {_panelText.Error}"
					 + " - falling back to the built-in labels and defaults");
		_panel = new ControlPanel { Text = _panelText };
		_panel.Prepare();
		if (!_noPanel)
		{
			layer.AddChild(_panel);
			_panel.AddHandle();
		}
		BuildPanel();
		_panel.OpenDefaults(_flagged);
		if (_noPanel)
		{
			// Nothing will ever draw or sync it, and a detached branch of
			// Controls left lying about is a wall of leak warnings at exit that
			// would hide a real one later.
			_panel.QueueFree();
			_panel = null;
		}

		if (_selfTest)
		{
			int failed = SelfTest.Run(_field, _tank, _atlases, _vehicles, _active,
									  _ring, _commonSounds, _sounds, _panel, _grove,
									  _stage);
			GetTree().Quit(failed == 0 ? 0 : 1);
			return;
		}
		// The flag values that had to wait for a tank to exist. The recoil level
		// goes on every tank: it is a harness setting, and a slider judged on one
		// tank while the other two ran at a different amplitude would be measuring
		// the difference. Burning goes on the selected one alone, because that is
		// what J does and what makes a burning tank next to an intact one possible.
		//
		// The tremble level is not here any more: it is the board's, pushed every
		// frame by UpdateTremble, so a copy applied once at start-up would be
		// overwritten by it - which is exactly what happened, and it read as the
		// flag not arriving.
		foreach (Vehicle vehicle in _vehicles)
		{
			vehicle.Recoil.Level = _recoilAtStart;
			if (_rollOnly)
				vehicle.Rumble.Amplitude = 0.0;
		}
		// Said out loud rather than left to the row claim: --no-3d works because
		// FlagRows keeps panel.json's default off view.stage, and a table entry is
		// not where a refusal should live on its own - drop the entry and the flag
		// stops working with nothing to read. Here it also covers the file having
		// turned the stage on before this point.
		if (_noStage)
			Staged = false;
		Active.Burning = _burnAtStart;
		// After the burning flag, because a wreck lights its own fire and would
		// otherwise be put out by a flag that was not asked for.
		if (_deadAtStart)
			Kill(Active);
		if (_startTurret is not null)
			_tank.TurretFacing = Mod(_startTurret.Value, 360.0);
		for (int i = 0; i < _damageAtStart; i++)
			foreach (string face in _tank.Atlas?.HitFaces
									?? (IReadOnlyList<string>)Array.Empty<string>())
			{
				// One increment, read twice: the two axes are two views of the
				// same round, and stepping the counter between them would scatter
				// a shell that does not exist.
				int n = ++_hitCount;
				_tank.Damage(face, ScatterAt(n), RiseAt(n));
			}
		if (_driveTo is not null)
			OrderMoveTo(_field.ClampCell(_driveTo.Value));
		if (_attackAtStart is int foe && foe >= 0 && foe < _vehicles.Count)
			Engage(Active, _vehicles[foe]);
		if (_fireAtStart)
			Fire();
		if (_hitAtStart is not null)
			TakeHit(_hitAtStart.Value);

		// Last, after every start-up flag has landed. An agent's first call is for
		// the state, and a connection opened before --drive or --destroy had been
		// applied would answer about a board still being set up.
		StartMcp();
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
		// Neither end of an engagement may be a wreck: a burnt-out hull has no
		// gun, and shooting at one is a fight with nothing on the other side of
		// it. Both refusals land here rather than at the three call sites, which
		// is what this one way in is for.
		if (shooter.Wreck.Dead || target?.Wreck.Dead == true)
			target = null;
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
			vehicle.TrackLeft.Scale = now;
			vehicle.TrackRight.Scale = now;
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

	/// <param name="placed">Whether the tank is being <i>put</i> here rather than
	/// having driven here. It decides one thing - whether the body is sat on the
	/// face it stands on or left to the spring - and getting it wrong is loud: a
	/// cell is parked on at the end of <b>every</b> leg, so sitting the body on
	/// arrival snapped a tank cresting a ramp flat in one frame, and the whole
	/// point of <see cref="ClimbLean"/> is that it does not do that. Placing is
	/// start-up, a reset, and the key that drops a tank on a cell; those have no
	/// motion to preserve and want the lean already right on the first frame.
	/// </param>
	private void Park(Vehicle vehicle, bool placed = true)
	{
		vehicle.Cell = _field.ClampCell(vehicle.Cell);
		_field.Position = _origin;
		// The surface, not the floor: the two are half a level apart on a ramp,
		// and this has to agree with the drawn position - HexField.CellAnchor is
		// lifted off the same number, and Depth subtracts this one back out of
		// that one to get the flat row. Written as the level's lift, the depth
		// term and the drawn row disagreed by half a lift on every ramp cell.
		vehicle.Height = _field.TopAt(vehicle.Cell);
		vehicle.Standing = vehicle.Height;
		// A tank standing on a cell stands at its centre, and there the chord and
		// the surface are the same point - but the clearance is still owed, because
		// marks are laid on the frame a leg ends too. See HexField.MarkAt.
		vehicle.Ground = _field.MarkAt(vehicle.Cell);
		// Not cleared but asked, for the reason OnSlope below is: parking is where
		// a tank comes to rest in a ford, and that is the case this exists for.
		vehicle.Waterline = _field.IsWater(vehicle.Cell)
			? _field.WaterTop(vehicle.Cell) : float.NegativeInfinity;
		vehicle.Trailing = vehicle.Height;
		vehicle.Travel = Vector2.Zero;
		vehicle.Levelling = false;
		// Not cleared but asked: parking is where a tank comes to rest on a ramp,
		// and that is the case this exists for.
		vehicle.OnSlope = _field.IsRamp(vehicle.Cell);
		vehicle.Sprite.Position = StandOn(vehicle, vehicle.Cell);
		// Cleared before the lean is asked: the leg that just ended left this at
		// one, and on the cell arrived at the two faces being mixed are the same
		// one anyway - but the next leg's first frame must not begin already
		// crossed.
		vehicle.LegBlend = 0.0f;
		// Sat on the face it is standing on rather than sprung up to it, but only
		// for a tank being put here - see the parameter. Asked after the position,
		// because SurfaceGrade reads the heading off the sprite.
		if (placed)
		{
			vehicle.Lean.Reset(SurfaceSlope(vehicle));
			vehicle.Sprite.Climb =
				vehicle.Lean.Angle(vehicle.Sprite.HullFacing, _field.RiseFactor);
			vehicle.Sprite.Slope = ClimbLean.Print(vehicle.Lean.Slope,
												   _field.Squash,
												   _field.RiseFactor);
		}
		// Moved rather than driven, so the ribbon must break here or the jump is
		// drawn as a line across the board - and only then, which is the same
		// question the parameter above already answers. A cell is parked on at the
		// end of *every* leg, so lifting the pen unconditionally broke the ribbon
		// at every cell boundary of an ordinary drive: with the belt-smoothing
		// window as long as a run, each cell's worth of trail collapsed towards its
		// own middle and a four-cell route was drawn as four blobs. Measured on the
		// 3D board, where the marks are geometry and the collapse is plain: 1081
		// rut pixels in two patches 52px apart against 3476 over the whole 186px
		// of the route. The old peak was the darker of the two (67 against 34)
		// because a collapsed run draws its every segment on top of itself.
		if (placed)
			_marks?.Lift(vehicle);
		Depth(vehicle);
		// Belt travel is read back off the heading rather than reported by
		// whatever turned it, so the baseline has to be laid down wherever the
		// heading is set from outside. Left stale, the first frame after a reset
		// sees the whole of the difference as one frame's swing and the belts
		// jump.
		vehicle.LastHullFacing = vehicle.Sprite.HullFacing;
		// Same baseline, same reason, one line down: the ring's rate is read back
		// off the angle, so a reset that moved the turret and left this stale
		// would report the whole move as one frame's traverse and blip the motor.
		vehicle.LastTurretOffset =
			WrapAngle(vehicle.Sprite.TurretFacing - vehicle.Sprite.HullFacing);
		// And the third baseline, for the third thing read back rather than
		// announced. Left stale, a tank put back on its home cell would count
		// the whole jump as one frame's travel and shoulder the wood aside on
		// the frame after a reset.
		vehicle.LastGroundPoint = vehicle.GroundPoint - _origin;
		vehicle.Sprite.QueueRedraw();
	}

	/// <summary>
	/// Plant the board, with the keep-out and the clearance constant taken from
	/// the tanks that are actually on it rather than written down.
	///
	/// The keep-out is the contact patch swept over every heading: half the belt
	/// length and the half-gauge are the two sides of it, so its radius is their
	/// diagonal. The widest tank of the three, but at the size it was
	/// <i>rendered</i> - the class scale is the bench's, named in CLAUDE.md as
	/// something that will not exist in the game, and a clearing sized by it
	/// would be 15% too big for the same reason the tanks are 15% apart.
	///
	/// It matters more than it looks. At 1.15 the radius is 101 ground px
	/// against a hexagon 107 tall, so the ring left over is six pixels deep at
	/// the top - thinner than a tree's roots, and a wooded cell came out with
	/// two trees on it whatever the lattice was set to. Unscaled it is 83, and
	/// the ring is 24 deep.
	/// </summary>
	private void SowGrove()
	{
		if (_grove is null)
			return;
		double keepOut = 0.0, below = 0.0;
		foreach (Vehicle vehicle in _vehicles)
		{
			double half = vehicle.Atlas.TrackLength * 0.5;
			double arm = vehicle.Atlas.TrackArm;
			if (half > 0.0 && arm > 0.0)
				keepOut = Math.Max(keepOut, Math.Sqrt(half * half + arm * arm));
			below = Math.Max(below, ReachBelow(vehicle.Atlas));
		}
		if (keepOut > 0.0)
			_grove.KeepOut = keepOut;
		if (below > 0.0)
			_grove.TankBelow = below;
		_grove.Plant();
	}

	/// <summary>
	/// Every cell a tank is in the way of: where its body is, and where it is
	/// headed.
	///
	/// Two readings, because the wood has to open before the tank arrives and
	/// close after it has gone, and neither end is the cell its centre is in.
	///
	/// <b>Where its body is</b> is the contact patch, not the centre point - so
	/// a cell stays counted until the tank has entirely left it rather than
	/// until its middle has. Sampled by asking which cell six points on the
	/// patch's rim fall in, rather than by measuring a disc against a hexagon:
	/// the cell arithmetic answers that exactly, slanted edges and all, and an
	/// outside-distance to a hexagon would be a second description of the same
	/// shape - the trap <see cref="Grove.EdgeRoom"/> already names from the
	/// other side.
	///
	/// <b>Where it is headed</b> is the step it is on, taken from the order the
	/// moment it starts rather than when the patch first touches. At rest the
	/// patch reaches 88 ground px and the neighbour's edge is 107 away, so a
	/// tank that has just been told to drive into a wood is still 19px short of
	/// touching it - and a wood that opens 19px late opens after the tank has
	/// visibly set off, which reads as the trees noticing.
	/// </summary>
	private HashSet<Vector2I> Standing()
	{
		var cells = new HashSet<Vector2I>();
		double reach = _grove?.KeepOut ?? 0.0;
		double squash = _grove?.Squash ?? 0.5;
		foreach (Vehicle vehicle in _vehicles)
		{
			Vector2 at = vehicle.GroundPoint - _origin;
			cells.Add(_field.ClampCell(_field.CellAt(at)));
			for (int k = 0; k < 6; k++)
			{
				double a = Math.PI * k / 3.0;
				var rim = new Vector2(
					at.X + (float)(Math.Cos(a) * reach),
					at.Y + (float)(Math.Sin(a) * reach * squash));
				Vector2I cell = _field.CellAt(rim);
				if (_field.InBounds(cell))
					cells.Add(cell);
			}
			if (vehicle.Moving)
				cells.Add(vehicle.Path[vehicle.PathStep]);
		}
		return cells;
	}

	/// <summary>Cells carrying trees.</summary>
	/// <summary>How hard the driven tank is shouldering the water, 0 parked
	/// through 1 at the ford's own ceiling, and nought when it is not in the
	/// water at all. For the trace: the crest is drawn by the surface out of a
	/// vector nothing else prints, so a bow that never appears could be the
	/// switch, the shader or a tank that is dry, and those are three fixes.
	/// </summary>
	private float BowPush()
	{
		Vehicle? v = Active;
		if (v is null || !v.Wading)
			return 0.0f;
		return Mathf.Clamp(
			(float)(v.Speed / Math.Max(v.Profile.WaterSpeed, 1e-4)), 0.0f, 1.0f);
	}

	private int Wooded()
	{
		if (_grove is null)
			return 0;
		int n = 0;
		for (int q = 0; q < _field.Columns; q++)
		for (int r = 0; r < _field.Rows; r++)
			if (_grove.IsForest(new Vector2I(q, r)))
				n++;
		return n;
	}

	/// <summary>Trees on the thinnest wooded cell, and on the fullest. The two
	/// numbers the two counts are about: a total says nothing about the cell
	/// that came out a field, nor about the one that came out a thicket.
	/// </summary>
	private int Thinnest() => Extreme(true);

	private int Thickest() => Extreme(false);

	private int Extreme(bool least)
	{
		if (_grove is null || _grove.Planted == 0)
			return 0;
		var counts = new Dictionary<Vector2I, int>();
		foreach (PropNode tree in _grove.Trees)
			counts[tree.Cell] = counts.GetValueOrDefault(tree.Cell) + 1;
		int found = least ? int.MaxValue : 0;
		for (int q = 0; q < _field.Columns; q++)
		for (int r = 0; r < _field.Rows; r++)
		{
			var cell = new Vector2I(q, r);
			if (!_grove.IsForest(cell))
				continue;
			int n = counts.GetValueOrDefault(cell);
			found = least ? Math.Min(found, n) : Math.Max(found, n);
		}
		return found == int.MaxValue ? 0 : found;
	}

	/// <summary>How far a tank reaches below its own contact point, in tile px.
	/// Not zero and not small: the belts hang in front of the turret axis, so a
	/// tank covers ground it is not standing on, and a prop that ignored it
	/// would clear the contact point and cross the tracks.</summary>
	private static double ReachBelow(AtlasSet atlas)
	{
		double contact = atlas.Anchor.Y + atlas.GroundOffset.Y;
		double below = 0.0;
		foreach (string layer in new[] { "hull", AtlasSet.TrackNames[0],
										 AtlasSet.TrackNames[1] })
		{
			if (!atlas.Has(layer))
				continue;
			int frames = atlas.CountOf(layer) * Math.Max(1, atlas.PhasesOf(layer));
			for (int i = 0; i < frames; i++)
			{
				Vector2 size = atlas.SizeOf(layer, i);
				if (size.Y <= 0.0f)
					continue;
				below = Math.Max(below,
								 atlas.OffsetOf(layer, i).Y + size.Y - contact);
			}
		}
		return below;
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
	private void Depth(Vehicle vehicle)
	{
		// The row it would stand on with the board flattened. The position
		// already carries the lift - CellAnchor is lifted - so putting it back is
		// how the two terms of the depth get separated again, and there is only
		// ever one place the height is subtracted.
		float row = vehicle.GroundPoint.Y - _origin.Y + vehicle.Height;
		vehicle.Sprite.ZIndex = Mathf.RoundToInt(_field.Depth(row, vehicle.Height));
		if (vehicle.Cap is null)
			return;
		vehicle.Cap.FlatRow = row;
		vehicle.Cap.Standing = vehicle.Height;
		vehicle.Cap.Box = TankBox(vehicle);
		vehicle.Cap.QueueRedraw();
	}

	/// <summary>What a tank occupies, in the field's local space, for the one
	/// question that needs it: which cells could hide it. The frame its layers
	/// are drawn from, at the class's size, about the anchor - so it is wider than
	/// the tank and narrower than the gap between two columns of the grid, which
	/// is the property that matters. Exact would mean the union of eight layers,
	/// re-measured every frame, to decide whether to repaint a cell that is beside
	/// the tank rather than on it.</summary>
	private Rect2 TankBox(Vehicle vehicle)
	{
		Vector2 size = (Vector2)vehicle.Atlas.Tile * vehicle.Sprite.BodyScale;
		Vector2 at = vehicle.Sprite.Position - _origin;
		return new Rect2(at - size * 0.5f, size);
	}

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

	/// <summary>Push the switch to every tank. The shadow is a question about
	/// the effect and not about one vehicle, so it reaches all three - the same
	/// rule the movement effects follow, and the opposite of the fire, which is
	/// a state one tank is in.</summary>
	private void ShadowChanged()
	{
		foreach (Vehicle v in _vehicles)
		{
			v.Sprite.ShowShadow = _shadowEnabled;
			v.Sprite.QueueRedraw();
		}
	}

	private void TracksChanged()
	{
		foreach (Vehicle v in _vehicles)
			UpdateTracks(v, (0.0, 0.0), 0.0);
	}

	/// <summary>
	/// Roughly half the track gauge, as a fraction of the hull's broadside span.
	///
	/// The fallback now, and only for a tank with no belt layers to measure -
	/// see <see cref="AtlasSet.TrackArm"/>. It was the answer, and it was wrong
	/// by 12-32%: <see cref="AtlasSet.HullSpan"/> is the widest frame, which is
	/// the hull's *length*, and the three hulls are one length to within 3%
	/// while their gauges range over a fifth. Kept because a tank without belts
	/// still turns, and named rather than buried so it can be argued with.
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
	/// Signed, and per side, which is the debt this used to carry openly. A real
	/// pivot runs the two belts in opposite directions; one unsigned number for
	/// both wound them the same way, so one of the two was always climbing its
	/// loop backwards. The fix is arithmetic rather than a case: a belt at
	/// <c>arm</c> from the centre covers <c>drive + omega*arm</c>, and the two
	/// sides differ only in the sign of <c>arm</c>.
	///
	/// It was the ruts that forced it. A spinning belt only suggests a direction
	/// and the eye forgives it; a mark left on the ground records one, and two
	/// arcs curling the same way where they should be counter-rotating is the
	/// first thing anyone would see.
	///
	/// The radius is <see cref="AtlasSet.TrackArm"/>, measured off those two
	/// layers, and only falls back to a fraction of the hull when there are no
	/// belts to measure. The fraction was wrong by 12-32%, worst on the heavy,
	/// and wrong in a way it could not have been right: the three hulls are one
	/// length to within 3% and the three gauges are not.
	/// </summary>
	private (double Left, double Right) BeltTravel(Vehicle v, double delta)
	{
		double swing = WrapAngle(v.Sprite.HullFacing - v.LastHullFacing);
		v.LastHullFacing = v.Sprite.HullFacing;
		double radius = PivotArm(v);
		return TrackLoop.Split(v.Speed * delta, swing, radius);
	}

	/// <summary>The radius each belt winds about when the hull turns - half the
	/// gauge, on screen.</summary>
	private static double PivotArm(Vehicle v) =>
		(v.Atlas.TrackArm > 0.0
			? v.Atlas.TrackArm
			: v.Atlas.HullSpan * PivotRadiusFraction) * v.Sprite.BodyScale;

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
			{
				v.Audio.Enabled = _soundEnabled;
				v.Audio.TurretMotor = _turretSound;
			}
	}

	/// <summary>
	/// The tracer on and off, on the rounds already up as well as the next one.
	///
	/// Reaching into the shells in flight is the point rather than an extra:
	/// switching a drawing off has to take effect on what is being drawn, and a
	/// round that kept its tracer because it was launched a moment ago would read
	/// as the switch not working.
	/// </summary>
	private void TracerChanged()
	{
		foreach (Shell shell in _shells)
			shell.Visible = _tracerVisible;
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
		// Rounds in the air, and how far the nearest one has got. A shot that
		// seems to do nothing is either a shell still flying or a shell that was
		// never launched, and from outside those are the same picture.
		// Rounds still in the air, not rounds still on screen: the smoke hangs
		// for a second after the last of them has landed, and a trace that
		// counted those would report a shot that is over as a shot in progress.
		List<Shell> up = _shells.FindAll(s => !s.Arrived);
		string flying = up.Count == 0
			? (_shells.Count == 0 ? "" : $" [{_shells.Count} smoking]")
			: $" [{up.Count} up, {up[0].Fraction:F2}"
			  // How far off its own barrel this one is going to land. Normally
			  // zero by construction; anything else is the clamp that keeps the
			  // mark on the armour, and that is not visible in the picture.
			  + $", bore {up[0].BoreMiss:F1}px"
			  // How fast and how big, because both are levels now and a level
			  // that did not arrive looks exactly like one that did.
			  // Two calibres, because they are two settings now: a trail tuned
			  // against a streak that turned out to be the same number would be
			  // two dials that agree by accident.
			  + $", {Shell.Speed:F0}px/s cal {up[0].TracerSize:F2}"
			  + $"/{up[0].SmokeSize:F2}"
			  + $"{(Shell.SmokeOn ? "" : " nosmoke")}"
			  + $"{(_tracerVisible ? "" : " unseen")}]";
		// And how near the end the target is, which the plate cannot say: three
		// rounds finish it wherever they landed, so a tank whose front plate reads
		// 1/1 may be one round from dead or three, depending on what its flanks
		// have taken.
		string near = $" pen {v.Target.Sprite.Penetrations}"
					  + $"/{Gunnery.PenetrationsToKill}";
		return $"{v.Target.Tag}@{v.Solution.Heading}deg/{v.Solution.Range} {state}"
			   + $" {face} {v.Target.Sprite.ScarLevel(face)}/{cap}{near}{flying}";
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
			   // The ring, which is the only loop whose gate cannot be guessed
			   // from anything else on the line: a stabilised turret is being
			   // driven hard while the tank drives straight and the gun holds
			   // still on screen.
			   + $" trt {a.TurretGate:F2}@{a.TurretPitch:F2}"
			   // 'off' and 'not turning' are the same two numbers and not the
			   // same thing - the tube's lesson, and this one is off by default
			   // so it is the reading you get unless you asked otherwise.
			   + $"{(_turretSound ? "" : "!off")}"
			   + $" brn {a.BurnGate:F2}"
			   + $" mix[{string.Join("/", trims)}]"
			   + $" peak {(float.IsNegativeInfinity(peak) ? -99.0f : peak),6:F1}dB";
	}

	// --- orders ------------------------------------------------------------

	private void OrderMoveTo(Vector2I target)
	{
		// A wreck does not take orders. Refused here rather than at the click,
		// so the panel and a future script get the same answer the mouse does.
		if (!_field.InBounds(target) || target == _cell || Active.Wreck.Dead)
			return;
		// Round the others rather than through them. A blocked destination gives
		// no path at all, which is why a click on an occupied cell is read as a
		// selection before it ever gets here.
		_path = _field.FindPath(_cell, target, Barred());
		_pathStep = 0;
		// Mouse aim would override both turret modes and make the feature look
		// broken while the tank drives, so an order takes the turret back.
		_aimWithMouse = false;
		_spinning = false;
		_field.Highlight = _path;
		_field.QueueRedraw();
	}

	/// <summary>Everywhere the driven tank may not go. The other tanks and
	/// nothing else: a forest cell has a clearing in it by construction, so
	/// every hex on the board is drivable and the woods cost movement nothing
	/// yet. When they do, this is where that goes.</summary>
	private HashSet<Vector2I> Barred() =>
		new(Vehicle.Occupied(_vehicles, Active));

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

	/// <summary>The fastest this tank may cruise at where it is now: its class
	/// figure on the level, two thirds of it going up or down a step, 45% of it
	/// through water - see <see cref="MovementProfile.GradeFraction"/> and
	/// <see cref="MovementProfile.WaterFraction"/>.
	///
	/// A method rather than the expression inlined at the one place it drives the
	/// speed, because the trace and the panel have to be able to say which of the
	/// two is in force. A tank crawling up a bank and a tank whose order has gone
	/// stale are the same picture, and the cap is the only number that tells them
	/// apart.
	///
	/// <b>The lower of the two where both apply, and never their product.</b> A
	/// leg is a slope or it is not and it is wet or it is not; multiplying would
	/// invent a third terrain nothing on the board is made of, and the one place
	/// the two meet on this map - the beach at the mouth of the pit - is exactly
	/// where that invention would show.</summary>
	private double SpeedCap(Vehicle v)
	{
		double cap = v.Profile.TopSpeed;
		if (!v.Moving)
			return cap;
		Vector2I next = v.Path[v.PathStep];
		if (_field.IsGrade(v.Cell, next))
			cap = Math.Min(cap, v.Profile.GradeSpeed);
		if (_field.IsWet(v.Cell, next))
			cap = Math.Min(cap, v.Profile.WaterSpeed);
		return cap;
	}

	/// <summary>What is holding this tank back, in one word - or in nothing at all
	/// where the answer is its own class.
	///
	/// <b>Beside <see cref="SpeedCap"/> rather than inside the two places that
	/// print it</b>, because a cap and the reason for it are one answer: a trace
	/// reading "160" with the panel reading "on a grade" while the tank is in the
	/// water is two statements about one number, and the second one is the one
	/// somebody acts on.</summary>
	private string SpeedCapWhy(Vehicle v)
	{
		if (!v.Moving)
			return "";
		Vector2I next = v.Path[v.PathStep];
		bool grade = _field.IsGrade(v.Cell, next), wet = _field.IsWet(v.Cell, next);
		return grade && wet ? "wading a grade" : wet ? "wading"
			: grade ? "grade" : "";
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
			// And no faster than a step allows, whichever way the step goes - see
			// SpeedCap.
			double allowed = Math.Min(SpeedCap(v), ceiling);
			double before = v.Speed;
			// Slowed into rather than snapped to, and the two are told apart here
			// because only one of them can be. The braking ceiling descends at the
			// tank's own retardation by construction, so clamping to it was already
			// gradual; the grade's cap appears the frame a leg onto a bank begins and
			// is 80px/s below cruise, which as a bare Min would be a stop dead in one
			// frame - eleven times what the engine can do - and would report itself
			// to the pitch as a single frame of full brake where the tank has a
			// smooth dip.
			v.Speed = v.Speed > allowed
				? Math.Max(allowed, v.Speed - v.Profile.Accel * delta)
				: Math.Min(v.Speed + v.Profile.Accel * delta, allowed);
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
			// its z index following would pass through it. Driven, not placed, so
			// the lean is left to its spring - see Park.
			Park(v, placed: false);
			if (!v.Moving && v == Active)
			{
				_field.Highlight = Array.Empty<Vector2I>();
				_field.QueueRedraw();
			}
		}
		else if (budgetPx > 0.0f)
		{
			v.Sprite.Position += to.Normalized() * budgetPx;
			Climb(v, next);
			Depth(v);
		}
		v.Sprite.QueueRedraw();
	}

	/// <summary>
	/// The height, part way from one cell to the next.
	///
	/// <see cref="Vehicle.Height"/> is taken from how far along the leg the tank
	/// is rather than integrated frame by frame, for the reason the belts read
	/// their travel back off the heading: a height stepped forward alongside the
	/// driving would have to be remembered by everything else that moves a tank -
	/// the W/S keys, a reset, an order cancelled mid-step. Read off the position
	/// it is exact at both ends of the leg whatever happened in between, and the
	/// drive between two lifted anchors is a straight line, so linear is not an
	/// approximation of it.
	///
	/// <b>One height, and there were two.</b> The occlusion rule used to be asked
	/// against the higher of the two ends, so that a tank halfway up a wall was
	/// called up already - which reads well for the cell it is climbing onto and
	/// is wrong about every other cell at that level. Climbing (3,2) to (4,3) the
	/// tank was promoted to the crown at the first pixel of the leg and spent the
	/// whole climb drawn over (3,3), a cell it had not reached and was not level
	/// with. The rule is that a tank covers a hex when it has come up to that
	/// hex's level, so the height that answers it is the height it is drawn at,
	/// and the two only ever differed while that answer was wrong.
	/// </summary>
	private void Climb(Vehicle v, Vector2I next)
	{
		Vector2 from = StandOn(v, v.Cell), goal = StandOn(v, next);
		float span = (goal - from).Length();
		float done = span <= 0.001f ? 1.0f
			: Mathf.Clamp((v.Sprite.Position - from).Length() / span, 0.0f, 1.0f);
		// Which cell's water it is in, if any - the one its contact point is over,
		// which changes at the shared edge halfway along the leg. A body of water
		// is one flat surface, so there is nothing to interpolate: a tank is in it
		// or it is not, and the moment it becomes so is the shoreline the board
		// draws.
		//
		// Ahead of the relief gate, because a flooded board need not be a raised
		// one - and because a waterline left over from last frame is a tank still
		// wading a hundred pixels up the beach.
		Vector2I under = done >= 0.5f ? next : v.Cell;
		v.Waterline = _field.IsWater(under) ? _field.WaterTop(under)
											: float.NegativeInfinity;
		if (!_field.HasRelief)
			return;
		v.Height = _field.HeightBetween(v.Cell, next, done);
		// And where the ground under it actually is, which the line above is a
		// chord of - see Vehicle.Ground. Only the belt marks read it, because only
		// they lie in the ground rather than standing on it.
		v.Ground = _field.MarkBetween(v.Cell, next, done);
		// The heights are the step's, not the frame's - see StepHeights, which
		// carries the whole of why the two ends of one hull are two numbers.
		// The gait is how far the contact point leads the trailing end, as a
		// fraction of this leg: half a hull, in the leg's own units.
		float gait = span <= 0.001f ? 0.0f
			: v.Atlas.HullSpan * v.Sprite.BodyScale * 0.5f / span;
		(v.Standing, v.Trailing) = StepHeights(_field, v.Cell, next, done, gait,
			GearCover * v.Sprite.BodyScale);
		v.Travel = span <= 0.001f ? Vector2.Zero : (goal - from) / span;
		// How much of the hull has crossed onto the far cell's face - see
		// Vehicle.LegBlend. Nought until the nose reaches the seam at 0.5 - gait,
		// one when the tail has passed it at 0.5 + gait, so the span of the change
		// is the hull's own length in the leg's units and needs no number of its
		// own. The same gait as the two ends of the hull above, for the same
		// reason: it is where the hull is.
		v.LegBlend = gait <= 0.001f ? (done >= 0.5f ? 1.0f : 0.0f)
			: Mathf.Clamp((done - (0.5f - gait)) / (2.0f * gait), 0.0f, 1.0f);
		// The climb's split fades driving straight up or down the screen: its
		// seam is the flanker's edge, and there the mounted hex and the ground
		// being passed share the sprite's columns, so the only right split is
		// none - the crown for the whole sprite, the old behaviour. The
		// descent's seam is the leg's own rim and never degenerates, which is
		// why the fade lives here, next to the knowledge of which leg this is,
		// and not in the stage.
		v.Levelling = _field.LevelAt(next) != _field.LevelAt(v.Cell);
		v.OnSlope = _field.IsRamp(v.Cell) || _field.IsRamp(next);
		bool down = _field.LevelAt(next) < _field.LevelAt(v.Cell);
		if (!down)
		{
			v.Trailing = v.Standing
				+ (v.Trailing - v.Standing) * Mathf.Abs(v.Travel.X);
			v.SeamLine = SeamLine(_field, _origin, v.Cell, next);
			return;
		}
		// A descent's seam is not pinned to the board at all - it is a wipe.
		// The board's own lines were both tried and each dumped the crown
		// side in a lump: a fragment over the plateau's top is visible at
		// exactly the crown and swallowed whole a hair under it, so whatever
		// single moment flips a region, that whole region blinks out on that
		// frame - measured on (4,3)->(4,2) three ways (six hundred to eight
		// hundred pixels in a frame or two, wherever the moment was put).
		// The only smooth transition available is spatial: a line parallel
		// to the leg's shared edge sweeping down the sprite over the second
		// half of the leg, from above everything (the whole tank still holds
		// the crown, as it did while on the top) to below everything (the
		// whole tank on the drawn ramp, which at the far anchor is exactly
		// the parked picture - so parking pops nothing). Fragments flip one
		// row at a time, and each flip is a few rows a frame.
		// Bottom first, which is why the normal is turned around: the wipe's
		// crown side (visible, still up on the top) must be the sprite's
		// upper part and the sinking nose side its lower, or the mid-hull is
		// cut at the rim while the tracks are still drawn on the plateau
		// below it - a tank torn in two, measured and seen. Swept over the
		// whole leg, because the drawn ramp sinks the sprite from the first
		// pixel and half a leg at cruise is a handful of frames.
		Vector3 axis = SeamEdge(_field, _origin, v.Cell, next);
		float reach = v.Atlas.HullSpan * v.Sprite.BodyScale;
		// The range is the rows where the two depths actually differ - from
		// just under the shadow's far edge to the top of the parked cut.
		// Wider spends sweep on rows where nothing shows and concentrates
		// the visible part of the wipe into fewer frames.
		float slide = Mathf.Lerp(0.25f * reach, -0.35f * reach, done);
		v.SeamLine = new Vector3(-axis.X, -axis.Y,
			-(axis.X * v.GroundPoint.X + axis.Y * v.GroundPoint.Y) + slide);
	}

	/// <summary>
	/// The screen-space line along which the stage splits a climbing tank's
	/// depth, as <c>(a, b, c)</c> with <c>a*x + b*y &gt;= c</c> the mounted
	/// cell's side: the shared edge between the cell being mounted and the
	/// raised flanking neighbour nearest the camera, extended.
	///
	/// The edge and nothing simpler, because the simpler lines were tried and
	/// each left a bite. A vertical through the leg's midpoint cannot separate
	/// two neighbouring hexes at all - they overlap by half a width in x - so
	/// the mounted hex's own corner reached across it and sat on the tank
	/// riding up. The shared edge is the one line with the mounted cell wholly
	/// on one side and the covering neighbour wholly on the other, a convex
	/// hex lying entirely on its own side of each of its edges: the mounted
	/// hex never covers the tank mounting it, and the neighbour's cover runs
	/// exactly to the rim it draws.
	///
	/// Two hex centres are equidistant from their shared edge, so the line is
	/// the perpendicular bisector of the two anchors, dropped by the
	/// neighbour's lift to where that neighbour's rim is drawn.
	/// <b>Perpendicular in the flat world, not on the screen:</b> the camera
	/// squashes the ground and does not preserve right angles, and the
	/// bisector taken in screen coordinates missed the edge by enough to spill
	/// hex corners across it - the self-test caught it on its first run. The
	/// screen normal of a flat-perpendicular line is <c>(dx*s, dy/s)</c> for a
	/// screen centre difference <c>(dx, dy)</c> and squash <c>s</c>. A leg
	/// with no flanker to read - the board's edge - falls back to the
	/// perpendicular through the leg's own midpoint, where there is nothing to
	/// cover either way.
	/// </summary>
	/// <summary>The raised ground a climbing tank passes beside: of the two
	/// cells adjacent to both ends of the leg, the one nearer the camera - the
	/// only one whose rim can cover the tank. Null on a board edge. Found by
	/// adjacency rather than heading arithmetic, because the heading numbers do
	/// not go round the ring in steps of sixty - 330 is down-right and 90 is
	/// straight down - and a +-60 guess picked the cell straight behind the
	/// leg, which laid the seam across the hull instead of along the boundary.
	/// The self-test only half-caught that one: it made the same guess, and
	/// the two wrongs agreed - which is why it now asks this method rather
	/// than its own copy.</summary>
	public static Vector2I? SeamFlank(HexField field, Vector2I cell,
		Vector2I next)
	{
		Vector2I? best = null;
		foreach (int h in HexField.EdgeHeadings)
		{
			Vector2I cand = HexField.Step(cell, h);
			if (cand == next || !field.InBounds(cand)
				|| HexField.HeadingTo(cand, next) < 0)
				continue;
			if (best is null
				|| field.GroundRow(cand) > field.GroundRow(best.Value))
				best = cand;
		}
		return best;
	}

	public static Vector3 SeamLine(HexField field, Vector2 origin,
		Vector2I cell, Vector2I next)
	{
		if (SeamFlank(field, cell, next) is Vector2I flank)
			return Bisector(field,
				origin + field.FlatAnchor(next) + field.CentreOffset,
				origin + field.FlatAnchor(flank) + field.CentreOffset,
				field.LevelAt(flank) * field.Lift);
		return SeamEdge(field, origin, cell, next);
	}

	/// <summary>The leg's own shared edge, in the flat world. A descent's
	/// wipe (see <see cref="Climb"/>) runs parallel to it - only the normal
	/// is read, the offset is swept - and a climb on a board edge falls back
	/// to it, where there is no flanker and nothing to cover either way.
	/// Flat rather than dropped to a drawn rim, because two lifts could
	/// claim the drop and neither owns this line.</summary>
	public static Vector3 SeamEdge(HexField field, Vector2 origin,
		Vector2I cell, Vector2I next) =>
		Bisector(field,
			origin + field.FlatAnchor(next) + field.CentreOffset,
			origin + field.FlatAnchor(cell) + field.CentreOffset,
			0.0f);

	/// <summary>The shared hex edge as a screen half-plane, with the side of
	/// <paramref name="cn"/> - the cell being driven onto, whose fragments
	/// carry <c>Vehicle.Standing</c> - positive. Perpendicular in the flat
	/// world, not on the screen (the camera does not preserve right angles),
	/// and dropped by <paramref name="lift"/> to where the covering rim is
	/// drawn.</summary>
	private static Vector3 Bisector(HexField field, Vector2 cn, Vector2 other,
		float lift)
	{
		Vector2 d = cn - other;
		float s = Mathf.Max(field.Squash, 0.0001f);
		Vector2 n = new Vector2(d.X * s, d.Y / s).Normalized();
		Vector2 mid = (cn + other) * 0.5f - new Vector2(0.0f, lift);
		return new Vector3(n.X, n.Y, n.Dot(mid));
	}

	/// <summary>
	/// How high the two ends of the hull count as standing, <paramref name="done"/>
	/// of the way from one cell to the next, with the contact point leading the
	/// trailing end by <paramref name="gait"/> of the leg.
	///
	/// A named static rather than four lines in <see cref="Climb"/> for
	/// <c>ImpactFor</c>'s reason: the self-test walks every climb on the board
	/// through it, and a copy of the arithmetic in the test would be a second
	/// truth to keep in agreement.
	///
	/// <b>The nose is the crown of the step while the tank is on the wall</b>,
	/// then down onto what it is arriving at. A staircase has no ramp on it, so
	/// a tank drawn halfway up one at its honest height is a tank halfway inside
	/// the block - and the stage, unlike a z index, says so, cutting the sprite
	/// along that block's top plane. The wall belongs to the higher cell, and a
	/// tank on it is not inside it, so for as long as it is on the wall it
	/// counts as up on the crown. Climbing, the higher cell is the one being
	/// driven onto and the tank is never off it, so the crown holds for the
	/// whole leg. Descending toward the viewer the tank is on the near face, in
	/// plain sight the whole way, so the crown holds there too - a single pixel
	/// of early descent puts it under the level of the shoulder it is passing,
	/// measured on (5,3)->(6,4), where the corner of (5,4)'s top face took a
	/// wedge out of the hull. Whether a wall faces the camera is which of the
	/// two cells is nearer, not which is higher.
	///
	/// <b>Descending behind the rim the split reappears, the other way up.</b>
	/// The single number tried both answers here and each was a visible jolt:
	/// the drawn ramp bites the foot out of the plateau it is still standing
	/// on, and the crown squeezed the whole descent into the second half of
	/// the leg at double rate - measured on (4,3)->(4,2), the rim swallowed
	/// thirty percent of the silhouette in four frames and handed part of it
	/// back by arrival. So the nose rides the ramp the sprite is drawn on and
	/// sinks behind the rim exactly as fast as it is drawn sinking, while the
	/// tail holds the crown for as long as it is over the top - the same two
	/// statements the climb makes, read in the other order. The seam is the
	/// leg's own shared edge (<see cref="SeamEdge"/>): the climb cuts along
	/// the flanker's rim because that is the rim doing the covering, and here
	/// the covering rim is the one being driven over.
	///
	/// <b>The tail is still down where the hull came from.</b> One depth for the
	/// whole sprite could not say so: a drawn row fixes the family
	/// <c>(row + L, L)</c>, so the promotion that stands the nose out of the
	/// block it is entering also stood the tail level with ground it had not
	/// come up to - promoted at the first pixel of (3,2)->(4,3), the tank spent
	/// the whole leg drawn over (3,3), a lateral hex it was not level with until
	/// the end, and the hex stopped covering it the moment the leg began.
	/// Climbing, the trailing end therefore reads its height off the leg half a
	/// hull behind the contact point, so the low end of the sprite stays below
	/// the level it has not reached and the ground at that level goes back over
	/// it - the same statement the 2D board's climb rule makes, asked of the end
	/// of the tank it is true of. The 2D board bought both answers with a
	/// per-cell sort key taken at the far edge; the stage buys them with a
	/// height per end, laid along the travel axis by <c>Stage3D.Body</c>.
	/// Parked, level and descending in plain sight the tail is the nose: the
	/// two ends have nothing to disagree about, and a split of zero is the old
	/// single number.
	///
	/// <b>But the ground ahead may take the running gear and no more</b> -
	/// <paramref name="cover"/> floors the tail that far under the nose. The
	/// fully honest tail was built and looked wrong for a reason no geometry
	/// fixes: on (3,2)->(4,3) the step ahead honestly hid half the tank, but the
	/// wall doing the hiding faces away from the camera and its top wears the
	/// same dirt as the flat ground, so the picture read as level ground painted
	/// over a tank rather than a tank behind a rise. Capped at the gear, the
	/// tracks and the shadow tuck behind the rim while the hull and turret stay
	/// whole - a tank cresting an edge, which is the reading a climb wants.
	/// </summary>
	public static (float Nose, float Tail) StepHeights(HexField field,
		Vector2I cell, Vector2I next, float done, float gait, float cover)
	{
		// Surface heights, not levels: a ramp's ends are half a level up, so the
		// crown of a leg onto one is half a lift and the level test would call
		// that leg flat. Every cell of a board with no ramps on it reports the
		// same number either way, which is why this reads as a rename.
		float onto = field.TopAt(next);
		float crown = Mathf.Max(field.TopAt(cell), onto);
		bool behind = field.GroundRow(next) < field.GroundRow(cell);
		// Descending behind the rim, the split works the other way up: the
		// nose rides the ramp the sprite is drawn on, and the tail holds the
		// crown - not until some moment, but for the whole leg. There is no
		// moment to pick: a fragment drawn over the plateau's top face is
		// visible at exactly the crown and swallowed whole a hair under it,
		// so any tail that descends by number - the old half-leg lerp, a
		// climbing-style catch-up, anything - dumps everything still drawn
		// over the top in a single frame. Measured on (4,3)->(4,2) twice:
		// the lerp cost thirty percent of the silhouette in four frames, the
		// catch-up six hundred pixels in one. What drains the crown side
		// instead is the seam, swept down the sprite by Climb - fragment by
		// fragment, a few rows a frame - which is why this split needs no
		// closing: by the time the leg parks, the sweep has left nothing on
		// the crown side to pop.
		// No cover cap here either: the parked tank behind this rim wears
		// the full cut already, and a floor would pop off at parking.
		if (behind && onto < field.TopAt(cell))
			return (field.HeightBetween(cell, next, done), crown);
		// Everything else stands on the crown: climbing it is the cell being
		// entered, level it is both, and descending toward the camera it is
		// the near face the tank is in plain sight on - a pixel of early
		// descent there put it under the shoulder it was passing, measured on
		// (5,3)->(6,4). The park at the far anchor snaps that crown to the
		// floor, and it is allowed to: measured on the two worst legs, not
		// one pixel of the tank changes - by the time it parks it is a full
		// half-cell clear of anything the crown was holding it out of.
		float nose = crown;
		// From a gait behind the contact at the near anchor to level with the
		// nose at the far one, so the split closes exactly where the leg parks:
		// held at a plain lag it would still be half a lift down on arrival,
		// and parking would pop the tail's depth.
		float tail = onto > field.TopAt(cell)
			? Mathf.Max(
				field.HeightBetween(cell, next, done * (1.0f + gait) - gait),
				nose - cover)
			: nose;
		return (nose, tail);
	}

	/// <summary>How much of a climbing tank the ground ahead may hide, in
	/// atlas px of depth under the crown - about the height of the running
	/// gear, scaled by the class before use. See <see cref="StepHeights"/> for
	/// why the honest number was retired.</summary>
	public const float GearCover = 24.0f;

	/// <summary>
	/// What the slope is doing to the drawn body, this frame.
	///
	/// The grade is worked out from the live state rather than stashed when the
	/// leg begins, and that is not tidiness. Parking happens at every cell of a
	/// multi-cell climb, so a stored grade would be cleared and set again at each
	/// boundary - a notch in the middle of a climb, once per hex, which reads as
	/// the suspension catching rather than as anything about the ground.
	///
	/// Runs for a standing tank too, because settling back to level after
	/// arriving is the other half of the spring. On a board with no relief the
	/// target is zero for ever and the angle never leaves it, so nothing here
	/// costs a flat board a pixel.
	/// </summary>
	/// <summary>
	/// Put height on the board, or take it off, with everything standing on it
	/// already standing on it.
	///
	/// The switch is not the interesting part - the settling afterwards is, and
	/// every line of it is something that would otherwise be wrong until the next
	/// time it happened to be recomputed:
	///
	/// <list type="bullet">
	/// <item><b>Orders are cancelled.</b> A route was found against the board it
	/// was found on, and the climb limit is part of that board: a path that
	/// walked a cell which has just become a two-level step is a path the driving
	/// will refuse, and a tank that stops halfway through an order reads as the
	/// pathing being broken.</item>
	/// <item><b>Everybody is parked.</b> Standing still is where the two heights
	/// are read and where the depth is stamped, and a tank whose cell just rose
	/// forty-six pixels is otherwise left hanging under it.</item>
	/// <item><b>The lean is reset rather than sprung down.</b> It is not a slope
	/// that changed, it is the board; letting the spring settle from wherever it
	/// was would draw a tank leaning into ground that has just been taken
	/// away.</item>
	/// <item><b>The ruts are cleared.</b> They were laid at the height the ground
	/// was then, and there is no honest place to put them now - a ribbon that
	/// half climbs is worse than no ribbon.</item>
	/// <item><b>The wood is re-sown.</b> Trees are planted off the cell centre
	/// and sorted off its depth, both of which just moved.</item>
	/// </list>
	/// </summary>
	/// <summary>Put the ramps on the board or take them off. <see cref="Settle"/>
	/// for the same reason relief does: a route found with a ramp in it is not
	/// walkable without one, so the orders cannot stand.</summary>
	private void SetRamps(bool on)
	{
		// Already there is not a change, for SetRelief's reason - panel.json
		// applies its defaults through this setter, and Settle cancels orders.
		if (on == _field.HasRamps)
			return;
		_ramps = on;
		ApplyRamps();
	}

	/// <summary>Put the board's ramps where <see cref="Ramped"/> says they should
	/// be. Two ways in, and both are a change of mind about the board rather than
	/// about the ramps: the row above, and handing the board to the stage or
	/// taking it back. Silent when it agrees already, because <see cref="Settle"/>
	/// is not free - it cancels orders, which is the trap named on
	/// <see cref="SetRelief"/>.</summary>
	private void ApplyRamps()
	{
		if (_field is null || !_relief || Ramped == _field.HasRamps)
			return;
		_field.SetRelief(ReliefMap(_field.Columns, _field.Rows),
						 Ramped ? RampMask(_field.Columns, _field.Rows) : null);
		Reflood();
		Settle();
	}

	/// <summary>Ask the water's guards again, because what they are asked about
	/// has just moved. The mask never changes; whether it is legal does - a pond
	/// that is a flat five cells on a board with no relief is a pond with a ramp
	/// under it once the pit arrives, and the failure that guard prevents is a
	/// surface wedged along a slope rather than a refusal.</summary>
	private void Reflood()
	{
		if (_field is not null && _field.HasWater)
			_field.SetWater(WaterMask(_field.Columns, _field.Rows));
	}

	/// <summary>Put the water on the board or take it off. <see cref="Settle"/>
	/// for <see cref="SetRelief"/>'s reason and one of its own: a tank parked in
	/// the pond has a waterline on it, and the frame after the water goes away it
	/// must not still be wearing one.</summary>
	private void SetWater(bool on)
	{
		if (on == _field.HasWater)
			return;
		_water = on;
		ApplyWater();
	}

	/// <summary>Put the board's water where <see cref="Watered"/> says it should
	/// be. Two ways in, exactly as <see cref="ApplyRamps"/> has: the row, and
	/// handing the board to the stage or taking it back.</summary>
	private void ApplyWater()
	{
		if (_field is null || Watered == _field.HasWater)
			return;
		_field.SetWater(Watered ? WaterMask(_field.Columns, _field.Rows) : null);
		Settle();
	}

	/// <summary>Paint the pond or leave it flat. No <see cref="Settle"/> and no
	/// reflood: nothing about the board's shape, its depths or anybody's waterline
	/// moves - this swaps a texture for a colour that was measured off that same
	/// texture, which is the whole point of it being an A/B. The stage notices on
	/// its own, because whether the surface is drawn is part of its signature.
	/// </summary>
	private void SetWaterPaint(bool on)
	{
		_waterPaint = on;
		if (_stage is not null)
			_stage.Surf = Surf;
	}

	/// <summary>How tall a tank is drawn above the ground, at the medium's scale -
	/// see <see cref="AtlasSet.DrawnHeight"/>. The medium because the depth is one
	/// number for the board while three tanks of three sizes stand in it, and the
	/// class that is 1.00 by definition is the one to quote it against.</summary>
	private float DrawnHeight => _field.Atlas?.DrawnHeight ?? 0.0f;

	/// <summary>Where the depth stops reading as a ford in either direction.
	/// Named rather than written into the caption, because the check measures
	/// against them too and a threshold with two copies is a threshold that moves
	/// in one of them.</summary>
	public const double ShallowAt = 0.15;
	public const double SunkAt = 0.45;

	/// <summary>How long a single frame of the water may be held before the loop
	/// stops reading as motion, in screen frames. Named because the caption and
	/// the check both want it, and a threshold with two copies is a threshold that
	/// moves in one of them.
	///
	/// Twenty is a third of a second. It is the far side of the same argument
	/// <see cref="ExhaustLoop.FloorFramesPerPhase"/> makes from the near side -
	/// under two frames a cycle flickers, over twenty it is a slideshow - and the
	/// tuned 2.4s sits at 36, which is why the water was called jerky.</summary>
	public const double StepsAt = 20.0;

	/// <summary>How deep the water stands. Everything settles for
	/// <see cref="SetGrade"/>'s reason narrowed to one thing: no height on the
	/// board moves, but every waterline on a tank standing in it does, and those
	/// are pushed per frame from the cell rather than held - so this is really
	/// only here to keep the row and the field one answer.</summary>
	private void SetDepth(double depth)
	{
		if (Math.Abs(depth - _field.WaterDepth) < 1e-9)
			return;
		_depth = depth;
		_field.WaterDepth = depth;
		_field.QueueRedraw();
	}

	private void SetRelief(bool on)
	{
		_relief = on;
		// Already there is not a change, and the settling below is not free: it
		// cancels orders. panel.json applies its defaults through this setter -
		// which is the point of them being defaults rather than field
		// initialisers - so a board that comes up flat and is told to be flat
		// would have thrown away whatever --drive had just asked for, and the
		// capture would show a tank standing where it started.
		if (on == _field.HasRelief)
			return;
		_field.SetRelief(on ? ReliefMap(_field.Columns, _field.Rows) : null,
						 on && Ramped ? RampMask(_field.Columns, _field.Rows)
									  : null);
		Reflood();
		Settle();
	}

	/// <summary>The settling every change to the board's heights needs. See
	/// <see cref="SetRelief"/> for what each line is for.</summary>
	private void Settle()
	{
		foreach (Vehicle v in _vehicles)
		{
			CancelOrder(v);
			v.Lean.Reset();
			v.Sprite.Climb = 0.0;
			v.Sprite.Slope = Vector2.Zero;
			v.Sprite.Rise = 1.0f;
			Park(v);
		}
		_marks?.Clear();
		// The pond too, and beside the belt marks because it is the same kind of
		// thing: what the tanks did to the ground. A swell left standing comes
		// back as water that was breaking before anybody drove into it - and this
		// runs when the water itself is taken off the board, where a state held
		// against cells that no longer exist is worse than wrong.
		_sea?.Settle();
		SowGrove();
		_field.QueueRedraw();
	}

	/// <summary>
	/// Which cells are drawn over this tank, for the trace.
	///
	/// Prints the level it stands on beside the count, because a count of none
	/// has two quite different meanings and only the level tells them apart:
	/// nothing on the board is above this tank - which is the answer whenever it
	/// is up on the crown - or something is and the promotion failed. The first
	/// is the rule working; the second is a bug, and on screen they are the same
	/// picture.
	/// </summary>
	private string Hiding(Vehicle v)
	{
		if (!_field.HasRelief)
			return "flat";
		float row = v.GroundPoint.Y - _origin.Y + v.Height;
		var over = _field.Occluders(row, v.Height, TankBox(v));
		(int _, int high) = _field.LevelRange;
		int level = Mathf.RoundToInt(v.Height / Math.Max(_field.Lift, 0.01f));
		string where = $"on {level:+0;-0;0} of {high:+0;-0;0}";
		// The climb split while there is one: "the hex covers the tail and not
		// the nose" and "the promotion failed" are the same picture, and only
		// the pair tells them apart.
		if (Mathf.Abs(v.Standing - v.Trailing) > 0.5f)
			where += $", tail {v.Trailing:F0} off nose {v.Standing:F0}";
		return over.Count == 0
			? $"none, {where}"
			: $"{over.Count} ({string.Join(" ", over.Select(c => $"{c.X},{c.Y}"))})"
			  + $", {where}";
	}

	/// <summary>
	/// How steep a level is. Everything standing on the board settles the way it
	/// does for <see cref="SetRelief"/>, and for the same reasons - every height
	/// on the board has just changed.
	/// </summary>
	private void SetGrade(double grade)
	{
		if (Math.Abs(grade - _field.StepGrade) < 1e-9)
			return;
		_grade = grade;
		_field.StepGrade = grade;
		Settle();
	}

	/// <summary>
	/// The plane under a tank, as a gradient in world ground coordinates, for
	/// <see cref="ClimbLean"/>.
	///
	/// <b>A plane and not a grade, because two things are drawn from it and only
	/// one of them is the pitch.</b> The body takes the component along its
	/// heading; the contact shadow lies in the face and takes the whole of it,
	/// including on the headings where the component is nought. A grade along the
	/// travel cannot be turned back into a plane - divide by the cosine and it
	/// blows up in exactly the case that matters, a tank standing broadside on a
	/// face, where the travel grade is zero and the face is not.
	///
	/// <b>Read off the face the tank is standing on, not off the ends of its
	/// leg</b>, and that is what lets a tank stopped on a ramp stay leaning. A
	/// slope is a property of the ground; asked as "how much higher is the cell I
	/// am going to", the question has no answer once the tank has arrived, so the
	/// spring levelled it out - a tank standing on a visible slope, drawn flat.
	///
	/// A ramp's top is <see cref="HexField.StepGrade"/> steep and its gradient
	/// points along its axis, so what the hull feels is that steepness projected
	/// onto its heading: full up the axis, negated coming down it, and nothing
	/// across it. The projection also removes the sign that used to be taken from
	/// which end was higher - one statement where there were two.
	///
	/// <b>Across the slope it says level, and that is the class rather than the
	/// map.</b> A tank standing broadside on a ramp really is rolled, and
	/// <see cref="ClimbLean"/> is one rotation about the pitch axis - the roll
	/// would want the other one, and the two do not compose into a rigid motion of
	/// a flat sprite any more than the foreshortening does. Named because it is
	/// visible: park across a face and the body stands square on a slanted tile.
	/// Its shadow, which can express the whole plane, is not square on it.
	///
	/// Steps between two flat levels keep their old answer, the difference in
	/// levels laid along the travel: there is no face there, only a wall, and a
	/// tank crossing it is climbing nothing - it is being carried up. Given a
	/// direction anyway, so the shadow follows the body it belongs to rather than
	/// staying flat under a hull the same fiction has tipped.
	/// </summary>
	private Vector2 SurfaceSlope(Vehicle v)
	{
		if (!_field.HasRelief)
			return Vector2.Zero;
		Vector2I next = v.Moving ? v.Path[v.PathStep] : v.Cell;
		// Mixed between the two cells by how much of the hull has crossed, not
		// taken from whichever of them is a ramp - see Vehicle.LegBlend. Driving
		// onto a face this rises from nothing as the nose reaches it; cresting
		// onto the flat top it falls away the same way, which is the same
		// statement read backwards. Mixed as planes rather than as grades, which
		// is the same number for the body - a lerp of the projections is the
		// projection of the lerp - and the only one of the two the shadow can use.
		if (_field.IsRamp(v.Cell) || _field.IsRamp(next))
			return FaceSlope(v.Cell).Lerp(FaceSlope(next), v.LegBlend);
		int heading = HexField.HeadingTo(v.Cell, next);
		if (!v.Moving || heading < 0)
			return Vector2.Zero;
		return ClimbLean.Plane(
			(_field.LevelAt(next) - _field.LevelAt(v.Cell)) * _field.StepGrade,
			heading);
	}

	/// <summary>The gradient of one cell's own top: a ramp's steepness pointed up
	/// its axis, and nothing at all for a cell whose top is flat. Asked of the
	/// cell and not of the tank, because a face is a property of the ground - the
	/// projection onto whoever is standing on it happens later, in
	/// <see cref="ClimbLean.Along"/>.</summary>
	private Vector2 FaceSlope(Vector2I cell)
	{
		int face = _field.RampHeading(cell);
		return face < 0 ? Vector2.Zero
						: ClimbLean.Plane(_field.StepGrade, face);
	}

	/// <summary>What the sprung plane is worth along a hull's heading, for the
	/// trace: the number the pitch is drawn from.</summary>
	private double SurfaceGrade(Vehicle v) =>
		ClimbLean.Along(SurfaceSlope(v), v.Sprite.HullFacing);

	private void UpdateLean(Vehicle v, double delta)
	{
		v.Lean.Update(SurfaceSlope(v), delta);
		v.Sprite.Climb = v.Lean.Angle(v.Sprite.HullFacing, _field.RiseFactor);
		// The same plane as the line above, in the terms a layer printed on the
		// ground needs it in - see TankSprite.Slope. One spring, two readings, so
		// the shadow cannot lag the hull standing on it.
		v.Sprite.Slope = ClimbLean.Print(v.Lean.Slope, _field.Squash,
										 _field.RiseFactor);
		v.Sprite.Rise = _field.Lift <= 0.0f
			? 1.0f
			: 1.0f + TankSprite.RisePerLevel * (v.Height / _field.Lift);
		if (v.Sprite.Climbing)
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
	private void UpdateTracks(Vehicle v, (double Left, double Right) travel,
							  double delta)
	{
		if (!_tracksEnabled || v.Atlas.HasTracks != true)
		{
			if (!v.Sprite.TracksRunning)
				return;
			v.TrackLeft.Reset();
			v.TrackRight.Reset();
			v.Sprite.TrackPhaseLeft = -1;
			v.Sprite.TrackPhaseRight = -1;
			v.Sprite.TrackBlurLeft = 0.0;
			v.Sprite.TrackBlurRight = 0.0;
			v.Sprite.QueueRedraw();
			return;
		}
		v.TrackLeft.Advance(travel.Left, delta);
		v.TrackRight.Advance(travel.Right, delta);
		v.Sprite.TrackPhaseLeft = v.TrackLeft.Frame;
		v.Sprite.TrackPhaseRight = v.TrackRight.Frame;
		v.Sprite.TrackBlurLeft = v.TrackLeft.Blur;
		v.Sprite.TrackBlurRight = v.TrackRight.Blur;
	}

	/// <summary>Runs every frame, moving or not - that is the whole point of it.
	/// The rumble is keyed on distance and goes silent the instant the tank
	/// stops; the engine does not. Speed only moves the frequency, so there is
	/// no threshold anywhere for the effect to switch on or off at.</summary>
	private void UpdateTremble(Vehicle v, double delta)
	{
		// A dead engine does not tremble. Of every switch this state throws,
		// this is the one that carries most: it is on by default precisely
		// because a tank with nothing moving on it reads as wrong, and that is
		// exactly what a wreck is supposed to read as.
		if (!_trembleEnabled || v.Wreck.Dead)
		{
			if (v.Sprite.TremblePitch == 0.0 && v.Sprite.TrembleYaw == 0.0)
				return;
			v.Tremble.Reset();
			v.Sprite.TremblePitch = 0.0;
			v.Sprite.TrembleYaw = 0.0;
			v.Sprite.QueueRedraw();
			return;
		}
		// Here rather than at the drag, so every tank carries the same level and a
		// tank built after it was set cannot be left on the tuned figure - see
		// _trembleLevel.
		v.Tremble.Level = _trembleLevel;
		v.Tremble.Advance(v.Speed, delta);
		v.Sprite.TremblePitch = v.Tremble.Pitch;
		v.Sprite.TrembleYaw = v.Tremble.Yaw;
		v.Sprite.QueueRedraw();
	}

	/// <summary>Runs every frame, moving or not, like the tremble and for the
	/// same reason: it is the engine, not the ground. Speed picks one of two
	/// states, or walks the ramp under --exhaust-ramp.</summary>
	private void UpdateExhaust(Vehicle v, double delta)
	{
		// Both settings before the early return, not after it. The panel's
		// caption reads the rate off this instance, so a clock left on last
		// frame's model while the exhaust is switched off would freeze under the
		// very control describing it - and switching the model back on with the
		// plume off, then on, would show the stale one for a frame.
		v.Exhaust.Level = _exhaustLevel;
		v.Exhaust.Binary = !_exhaustRamp;
		// A wreck's engine is not idling, and this is the plainest of the
		// several ways the bench says so.
		if (!_exhaustEnabled || v.Wreck.Dead || !v.Atlas.HasExhaust)
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

	/// <summary>
	/// Kill one tank.
	///
	/// Three rounds past the paint do it, and nothing else - see
	/// <see cref="Land"/> and <see cref="Gunnery.PenetrationsToKill"/>. That is
	/// still an assumption and worth naming as one: the bench has no hit points,
	/// and inventing some would be a second damage model beside the one the class
	/// matchup already is. Counting penetrations leaves the matchup table deciding
	/// *who* can kill whom, which is what it was written to say - a light gun that
	/// can only ever scorch a heavy cannot destroy one however long it keeps at it
	/// - and leaves the count deciding *when*.
	///
	/// The first version had the deepest scar level be death, and the two
	/// questions were one: a heavy killed a medium with its first shell and a
	/// medium killed a light never, so every cell of the table was one shot or
	/// nothing. Three rounds is what put the middle back.
	///
	/// Everything here is the tank ceasing to be a machine. Most of the read is
	/// in this list rather than in any pixel: on this field a live tank trembles,
	/// smokes, scans with its turret and rocks when it moves.
	/// </summary>
	private void Kill(Vehicle v)
	{
		if (!v.Wreck.Kill())
			return;
		v.Path.Clear();
		v.PathStep = 0;
		v.Speed = 0.0;
		v.Target = null;
		v.Scan.Suspend();
		// It burns because it has just been destroyed, not because the key was
		// pressed - and the key can no longer put it out, since a wreck that
		// stops smouldering on a keystroke is a switch pretending to be a state.
		v.Burning = true;
		// Nobody goes on shooting at a wreck. Otherwise the engagement runs for
		// ever against a target that cannot answer, which reads as a gunnery
		// fault rather than as a fight that is over.
		foreach (Vehicle other in _vehicles)
			if (other.Target == v)
				other.Target = null;
		// Counted off the shells this hull took, like the impact takes are: the
		// three recordings are variety, and the same one every time reads as one
		// event rather than as three tanks dying.
		v.Audio?.Destroyed(1 + v.HitCount % 3);
		// The biggest of the three shoves, and it goes out from the hull that
		// just let go. In Kill rather than in Land so it happens once: the round
		// that did it has already sent its own, and a wreck taking further fire
		// does not blow up again.
		if (_grove is not null)
			_grove.Shock(v.GroundPoint - _origin, _grove.DeathBlast);
	}

	/// <summary>
	/// The wreck settling, once a frame.
	///
	/// Everything it writes is a function of one age - see <see cref="Wreck"/> -
	/// so there is no table here and no second clock. It runs before the burn so
	/// the densities it sets are this frame's rather than last frame's, the same
	/// ordering the audio and the layers already need.
	/// </summary>
	private static void UpdateWreck(Vehicle v, double delta)
	{
		if (!v.Wreck.Dead)
			return;
		v.Wreck.Update(delta);
		TankSprite s = v.Sprite;
		// The pose swaps on the hit and the paint blackens over the second after
		// it: the turret dropping is the event, the char is the aftermath. Set
		// here rather than in Kill so that a reset which clears the wreck clears
		// this too, through the one path that owns the state.
		s.Wrecked = true;
		s.Char = v.Wreck.Char;
		s.FireDensity = (float)v.Wreck.Blaze;
		s.SmokeDensity = (float)v.Wreck.Smoke;
		// Once the flame is out there is nothing left to advance but the column,
		// and it is still asked for: the smoke is what says where a tank died
		// from the other side of the board.
		s.QueueRedraw();
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
		// Fourth thing off the one trigger, and the only one that is not about
		// the tank at all: the view. Along the gun's own ground direction and
		// unnormalised, so a shot into the screen jolts less than one across it.
		// Any tank's shot, not just the driven one - a gun going off beside you
		// is the same event as your own, and the bench has all three on screen.
		if (_shakeOn && v.Atlas is not null)
			_shake.Fire(v.Atlas.GroundDirection(v.Sprite.TurretFacing),
						v.Profile.ShotShake);
		// And the wood, which is the same event told to the other thing on the
		// board that can answer it. Off the tank's contact point rather than the
		// muzzle: the muzzle stands 60-70px out along the gun, and nothing grows
		// within 83 of a cell's centre, so the source and the tube's end are on
		// the same side of every tree there is - it would move the number and
		// not the picture. Any tank's gun, not just the driven one, for the
		// reason the camera shake gives just above.
		if (_grove is not null)
			_grove.Shock(v.GroundPoint - _origin,
						 v.Profile.ShotShake * _grove.ShotBlast);
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
	/// Put a round in the air, aimed at the plate the lane arrives on.
	///
	/// Everything about the hit is settled here and carried by the round -
	/// which plate, where on it, how deep, how loud - rather than looked up
	/// again when it lands. Same rule the scatter and the calibre already
	/// follow: what the trigger decided travels with the shell, because the
	/// alternative is a round that changes calibre in flight because somebody
	/// turned a dial.
	///
	/// The muzzle comes off the turret layer for the frame the gun is showing, so
	/// it foreshortens by itself and comes out of the tube on every heading -
	/// the same measurement the flash is placed by, and taken from the same
	/// place so the two cannot part company.
	/// </summary>
	private void Launch(Vehicle shooter, Vehicle victim, double fromBearing)
	{
		AtlasSet atlas = victim.Atlas;
		if (!atlas.HasHit)
			return;
		double from = HexField.EdgeHeadings[SideFor(fromBearing)];
		victim.HitCount++;
		double hull = victim.Sprite.HullFacing;
		string face = atlas.FaceFor(from, hull);
		Vector2 centroid = atlas.HitOffset(face, hull);
		Vector2 tangent = atlas.HitTangent(face, hull);
		Vector2 slope = atlas.HitSlope(face, hull);

		AtlasSet gun = shooter.Atlas;
		Vector2 muzzle = gun.Muzzle(gun.FrameFor(shooter.Sprite.TurretFacing))
						 - gun.Anchor;
		Vector2 launched = shooter.Sprite.ToGlobal(muzzle);
		// The bore as the target's own layers see it. Carried through both
		// transforms as a pair of points rather than as an angle, so a scaled
		// class and a hull leaning on its springs are handled by the transforms
		// that already express them instead of by arithmetic repeated here.
		//
		// The direction is the ground direction of the turret's heading, not the
		// muzzle minus the anchor: a tank gun is level, and a level line in the
		// world projects to exactly the ground direction on screen, while the
		// ring-to-muzzle vector also carries however far the trunnions sit above
		// the ring. The same measurement the flash points itself by.
		Vector2 eye = victim.Sprite.ToLocal(launched);
		Vector2 down = shooter.Sprite.ToGlobal(
			muzzle + gun.GroundDirection(shooter.Sprite.TurretFacing) * 100.0f);
		Vector2 bore = victim.Sprite.ToLocal(down) - eye;

		// The vertical fraction stays on its hash and the tangential one follows
		// it onto the gun's axis - see Gunnery.ScatterOntoBore. Only on this
		// path: a round asked for by the U key has no shooter and so no bore, and
		// it keeps both hashes.
		//
		// The hash is not gone, it is demoted to what it should always have been:
		// dispersion about an aim point, a tenth of a plate rather than the whole
		// of it. Without it the solve is one number per geometry, so a gun
		// shooting twice from the same place would put its second round exactly
		// through the first - and where the solve is clamped that is a column of
		// holes down one edge, which is the row-of-stamps failure again in the
		// other axis.
		float rise = RiseAt(victim.HitCount);
		float scatter = AimedScatter(eye, bore, centroid, tangent, slope, rise,
									 victim.HitCount);
		Vector2 impact = centroid + tangent * scatter + slope * rise;

		var shell = new Shell
		{
			Shooter = shooter,
			Target = victim,
			ImpactLocal = impact,
			From = launched,
			BoreMiss = Gunnery.BoreMiss(eye, bore, impact),
			Serial = victim.HitCount,
			// Carried rather than re-read on arrival, for the reason above.
			Face = face,
			Scatter = scatter,
			Rise = rise,
			Calibre = Calibre,
			Level = Gunnery.Penetration(shooter.Profile, victim.Profile),
		};
		// Invisible is still in the air: the flight is what separates the report
		// from the impact, and only the drawing is on a switch.
		shell.Visible = _tracerVisible;
		_shells.Add(shell);
		AddChild(shell);
	}

	/// <summary>Rounds in the air. A list rather than one per vehicle because
	/// nothing about a shell belongs to the gun once it has left: the reload is
	/// longer than any flight, so there is at most one per tank today, and the
	/// day there is not this does not have to change.</summary>
	private readonly List<Shell> _shells = new();

	/// <summary>
	/// Fly every round on, and land the ones that arrive.
	///
	/// The damage is applied here rather than inside <see cref="Shell"/>, which
	/// is a drawing node: a thing that must happen exactly once, in a known
	/// order, next to the other things that happen when a tank is hit, does not
	/// belong in _Draw's neighbourhood.
	/// </summary>
	private void AdvanceShells(double delta)
	{
		for (int i = _shells.Count - 1; i >= 0; i--)
		{
			Shell shell = _shells[i];
			shell.Advance(delta);
			// Landing and being finished with stopped being the same frame when
			// the smoke was allowed to outlive the round, so the strike is
			// guarded and the node is freed on the trail rather than on the hit.
			if (shell.Arrived && !shell.Struck)
			{
				shell.Struck = true;
				Strike(shell);
			}
			if (!shell.Expired)
				continue;
			_shells.RemoveAt(i);
			shell.QueueFree();
		}
	}

	private void ClearShells()
	{
		foreach (Shell shell in _shells)
			shell.QueueFree();
		_shells.Clear();
	}

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
		//
		// Two axes, because one made every round on a plate land at the same
		// height - see RiseAt for why the vertical range is the tighter of the two.
		float scatter = ScatterAt(victim.HitCount);
		float rise = RiseAt(victim.HitCount);
		string face = atlas.FaceFor(from, sprite.HullFacing);
		Land(victim, face, scatter, rise, calibre, level, bite);
	}

	/// <summary>A round arriving, with everything about it already settled.
	///
	/// Split out of <see cref="TakeHit"/> when the shell got a flight: the key
	/// press works out which plate at the moment it lands, and a fired round
	/// worked it out at the trigger and carried it. Both end here, so there is
	/// one place where a tank takes a hit however the hit was arranged.</summary>
	private void Land(Vehicle victim, string face, float scatter, float rise,
					  float calibre, int? level, int bite)
	{
		TankSprite sprite = victim.Sprite;
		// The calibre goes the same way as the scatter and for the same reason:
		// both are settled the moment the round leaves, so both travel with it
		// rather than being looked up again while it is on screen.
		victim.Hit.Strike(face, scatter, rise, calibre);
		// How deep it goes is the calibre's business - see TankSprite.Damage.
		// A light round still walks a plate scorch -> gouge -> breach over three
		// hits, which is what shows that the phase axis is damage and not three
		// renders of one drawing; a heavy one gets there in one.
		int got = level is int cap
			? sprite.DamageTo(face, scatter, rise, cap)
			: sprite.Damage(face, scatter, rise, bite);
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
		// The wood answers the burst, from the hull it went off against. Every
		// round, not only the ones that go through: a shell breaking up on
		// armour is the louder of the two events, and a wood that only flinched
		// at penetrations would be reading the damage table off the trees.
		if (_grove is not null)
			_grove.Shock(victim.GroundPoint - _origin, _grove.HitBlast);
		// Three rounds past the paint kill, and the matchup decides which rounds
		// those are. The two halves are deliberately apart - see
		// Gunnery.PenetrationsToKill. The tally is counted inside Damage/DamageTo,
		// so this asks the tank what it has taken rather than judging the shell
		// that just landed: a plate a heavy has already breached goes no deeper,
		// and a kill read off this round's level would stall there.
		if (sprite.Penetrations >= Gunnery.PenetrationsToKill)
			Kill(victim);
	}

	/// <summary>A round that has flown its path. Nothing is decided here - see
	/// <see cref="Launch"/>.</summary>
	private void Strike(Shell shell) =>
		Land(shell.Target, shell.Face, shell.Scatter, shell.Rise, shell.Calibre,
			 shell.Level, 1);

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
		// A wreck holds no target and cannot be given one, so this is belt and
		// braces - but the gate is stated here as well as at the kill because
		// the gun is the one thing that must not go off from a burnt-out hull.
		if (v.Target is null || v.Wreck.Dead)
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
			was, onto, Gunnery.TraverseRate(v.Profile) * delta);
		if (v.Sprite.TurretFacing != was)
			v.Sprite.QueueRedraw();

		if (v.Moving || !v.Solution.Clear || v.ReloadLeft > 0.0
			|| !Gunnery.Laid(v.Sprite.TurretFacing, v.Solution.Heading))
			return;
		Fire(v);
		// The round goes on its way rather than landing here. It arrives from the
		// far end of the lane it left along, which is the whole reason the gun is
		// restricted to the six: the bearing the armour model wants is the firing
		// heading turned round, exactly, with nothing snapped and nothing
		// approximated. See AtlasSet.FaceFor.
		Launch(v, v.Target, Mod(v.Solution.Heading + 180.0, 360.0));
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
	/// <summary>
	/// Which panel row each start-up flag speaks for.
	///
	/// One table rather than a claim inside each parsing branch, because it is
	/// the sort of list that is only correct when it can be read at once: a flag
	/// missing from it still works and is then quietly overruled by panel.json,
	/// which looks like the flag not arriving at all. The self-test walks it and
	/// requires every id in it to be a row the panel actually has.
	/// </summary>
	private static readonly Dictionary<string, string[]> FlagRows = new()
	{
		["--tank"] = new[] { "tank.driving" },
		["--size"] = new[] { "tank.size" },
		["--turret"] = new[] { "heading.turret" },
		["--scan"] = new[] { "heading.scan" },
		["--pitch"] = new[] { "ride.pitch" },
		["--rumble"] = new[] { "ride.rumble" },
		["--roll-only"] = new[] { "ride.rumble" },
		["--no-tremble"] = new[] { "ride.tremble" },
		["--tremble"] = new[] { "ride.tremble_level" },
		["--no-shadow"] = new[] { "effects.shadow" },
		["--no-tracks"] = new[] { "effects.tracks" },
		["--no-ruts"] = new[] { "effects.ruts" },
		["--no-exhaust"] = new[] { "effects.exhaust" },
		["--exhaust"] = new[] { "effects.exhaust_level" },
		["--exhaust-ramp"] = new[] { "effects.exhaust_ramp" },
		["--burning"] = new[] { "effects.fire" },
		["--flash"] = new[] { "effects.flash_source" },
		["--recoil-shear"] = new[] { "effects.hull_shear" },
		["--recoil"] = new[] { "effects.hull_shear", "effects.recoil_level" },
		["--recoil-turret"] = new[] { "effects.hull_shear", "effects.recoil_turret" },
		["--no-barrel-recoil"] = new[] { "effects.tube_recoil" },
		["--shake"] = new[] { "effects.camera_shake", "effects.shake_level" },
		["--tracer"] = new[] { "gunnery.tracer" },
		["--no-tracer-smoke"] = new[] { "gunnery.tracer_smoke" },
		["--smoke-life"] = new[] { "gunnery.smoke_life" },
		["--tracer-size"] = new[] { "gunnery.tracer_size", "gunnery.tracer" },
		["--smoke-size"] = new[]
			{ "gunnery.smoke_size", "gunnery.tracer_smoke", "gunnery.tracer" },
		["--shell-speed"] = new[] { "gunnery.shell_speed" },
		["--traverse"] = new[] { "gunnery.traverse_level" },
		["--hit-scale"] = new[] { "armour.calibre" },
		["--destroy"] = new[] { "armour.destroy" },
		["--turret-sound"] = new[] { "sound.turret_motor" },
		["--relief"] = new[] { "ground.relief" },
		["--no-ramps"] = new[] { "ground.ramps" },
		["--no-water"] = new[] { "ground.water" },
		["--water"] = new[] { "ground.water" },
		["--depth"] = new[] { "ground.water", "ground.depth" },
		["--flat-water"] = new[] { "ground.water_paint" },
		["--swell"] = new[] { "ground.swell" },
		["--hard-swell"] = new[] { "ground.swell_blend" },
		["--still-water"] = new[] { "ground.water_react" },
		["--drawn-water"] = new[] { "ground.deep" },
		["--no-wake"] = new[] { "ground.wake" },
	["--no-bow"] = new[] { "ground.bow" },
		["--no-foam"] = new[] { "ground.foam" },
		["--foam"] = new[] { "ground.foam" },
		// The stage asks for height as well as for itself, so it declares both -
		// the board came up flat the first time because "ground.relief" was not
		// declared, which from outside looks exactly like a flag that did not
		// arrive.
		//
		// <b>Four rows were declared here and two of them were the wrong fix.</b>
		// "view.field" was added after the 2D board came back over the whole 3D
		// world and cost an afternoon of measuring the wrong board; the ruts and
		// the trees followed, on the same reasoning and with a worse trap, since
		// nothing has driven on the opening frame and a rut layer switched back
		// on by the file draws nothing until somebody gives an order. But a
		// declaration only silences the file - it left the row itself writing
		// straight through to the 2D node, one click from the same picture. All
		// three now suppress rather than assign (see <see cref="Staged"/>) and
		// the board's row addresses whichever board is drawing, so the file is
		// free to answer them and this list is down to what the flag really owns.
		["--3d"] = new[] { "view.stage", "ground.relief" },
		["--no-3d"] = new[] { "view.stage" },
		["--no-cell-edges"] = new[] { "view.edges" },
		["--grade"] = new[] { "ground.relief", "ground.grade" },
		["--terrain"] = new[] { "ground.terrain" },
		["--no-forest"] = new[] { "ground.forest" },
		["--no-cast-shadows"] = new[] { "ground.shadows" },
		["--ghost"] = new[] { "ground.ghost" },
		["--clear-front"] = new[] { "ground.clearfront" },
		["--wind"] = new[] { "ground.wind" },
		["--blast"] = new[] { "ground.blast" },
		["--brush"] = new[] { "ground.brush" },
	};

	private readonly HashSet<string> _flagged = new();

	/// <summary>Names, descriptions, ranges and opening values, off panel.json.
	/// Held on the harness rather than only on the panel because --no-ui frees
	/// the panel and the trace still has to be able to say whether the file was
	/// read.</summary>
	private PanelText _panelText = PanelText.Load(null);

	/// <summary>Held for the same reason: the trace has to be able to say whether
	/// the per-class file loaded, and under --no-ui there is no panel to notice it
	/// on. A file that quietly did not load looks exactly like a file whose
	/// numbers did nothing.</summary>
	private ClassConfig _classConfig = new();

	/// <summary>Every row a start-up flag speaks for, flattened. Exposed so the
	/// self-test can require each one to be a row the panel really has: a flag
	/// naming a row that does not exist protects nothing, so panel.json
	/// overrules it and the flag looks like it never arrived.</summary>
	public static IEnumerable<string> FlaggedRows =>
		FlagRows.Values.SelectMany(v => v).Distinct();

	private void BuildPanel()
	{
		ControlPanel ui = _panel!;

		ui.Heading("tank");
		// Which of the three is being driven. Not "(1/2/3)": how many tanks load
		// is whatever is on disk, so enumerating them here goes stale every time
		// one is added, and it already had.
		ui.Choice("tank.driving", "driving  (number keys, or click it)", _loaded,
			() => _active, Select);
		// A multiplier over the three class figures, not a size - see _sizeLevel.
		// The caption is in pixels of hull against pixels of cell, because "1.15x"
		// answers nothing the dial is pulled for: whether the heavy reads heavier
		// than the medium, and whether it has outgrown the hex it stands on.
		ui.Slide("tank.size", "size level", 0.5, 1.5, 0.05,
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
		ui.Readout("tank.info", () =>
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
		ui.Slide("heading.hull", "hull  (A/D)", 0.0, 330.0, 30.0,
			() => Math.Round(_tank.HullFacing / 30.0) * 30.0,
			v => { CancelOrder(); _tank.HullFacing = Mod(v, 360.0); }, " deg");
		ui.Slide("heading.turret", "turret  (Q/E)", 0.0, 330.0, 30.0,
			() => Math.Round(_tank.TurretFacing / 30.0) * 30.0,
			v => { _aimWithMouse = false; _tank.TurretFacing = Mod(v, 360.0); }, " deg");
		// Two named states rather than a checkbox, and the reason is the reading
		// that went wrong: it used to say "turret locked", and a traverse lock
		// locks the turret *to the hull*, so the label promised the opposite of
		// what the switch does. A checkbox can only name one of the two states
		// and leaves the other to be guessed - which is exactly what happened.
		// A caption long enough to name both is clipped, not wrapped.
		ui.Choice("heading.mode", "turret through a hull turn  (F)", TurretModes,
			() => _tank.TurretHoldsHeading ? 0 : 1,
			i => _tank.TurretHoldsHeading = i == 0);
		ui.Toggle("heading.mouse", "aim with mouse  (M)",
			() => _aimWithMouse, on => _aimWithMouse = on);
		ui.Toggle("heading.spin", "spin turret  (SPACE)", () => _spinning, on => _spinning = on);
		ui.Toggle("heading.scan", "scan on the spot  (N)", () => _scanEnabled, on =>
		{
			_scanEnabled = on;
			ScanChanged();
		});
		// Which frame each heading resolves to. The pair is the thing worth
		// seeing rather than either alone: two layers off the same axis, and a
		// sprite set that has come apart says so here first.
		ui.Readout("heading.frames", () =>
		{
			Vector2I frames = _tank.FrameIndices();
			return $"hull {_tank.HullFacing,6:F1} deg -> frame {frames.X,2}\n"
				   + $"turret {_tank.TurretFacing,6:F1} deg -> frame {frames.Y,2}"
				   // The sway, and the arc it is allowed - a bare offset says
				   // nothing about whether one frame either side is what is on
				   // screen, which is the only thing anyone turns this on to see.
				   + $"   scan {_scan.Offset,6:F1} of +-"
				   + $"{_scan.MaxSteps * 360.0 / _tank.Atlas!.Count:F0} deg";
		});

		ui.Heading("ride");
		ui.Toggle("ride.pitch", "body pitch  (P)", () => _pitchEnabled, on =>
		{
			_pitchEnabled = on;
			PitchChanged();
		});
		ui.Toggle("ride.rumble", "ground rumble  (B)", () => _rumbleEnabled, on =>
		{
			_rumbleEnabled = on;
			RumbleChanged();
		});
		ui.Toggle("ride.tremble", "engine tremble  (I)", () => _trembleEnabled, on =>
		{
			_trembleEnabled = on;
			TrembleChanged();
		});
		// A multiplier over the tuned pair rather than a raw amplitude - see
		// EngineTremble.Level for why the standing and moving figures must keep
		// their ratio. The caption carries the stern travel in pixels, because
		// the multiplier says nothing about whether it is visible and the pixel
		// figure is the whole argument the value was chosen on.
		//
		// Reads and writes the board's level, not the driven tank's - see
		// _trembleLevel.
		ui.Slide("ride.tremble_level", "engine tremble level", 0.0, 2.5, 0.05,
			() => _trembleLevel, v => _trembleLevel = v, "x",
			() => $"{_tremble.TravelAt(_speed, _trembleLevel):F2}px"
				  + " of stern travel at this speed");
		ui.Toggle("ride.stabiliser", "turret stabiliser  (K)",
			() => _tank.TurretStabilised, on => _tank.TurretStabilised = on);
		ui.Readout("ride.info", () =>
			$"speed {_speed,6:F0} / {SpeedCap(Active):F0} px/s"
			+ (SpeedCap(Active) < _profile.TopSpeed
				? $" {SpeedCapWhy(Active)} (of {_profile.TopSpeed:F0})" : "")
			+ $"   ramp {_profile.RampTime:F2}s over {_profile.RampDistance:F0}px\n"
			+ $"turn {_profile.TurnRate:F0} deg/s   corner {_profile.CornerSpeed:F0} px/s\n"
			+ $"pitch {_tank.Pitch,7:F4}   roll {_tank.Roll,7:F4}"
			+ $"   heave {_tank.Shake,2}px\n"
			+ $"tremble {_tank.TremblePitch,7:F4} / {_tank.TrembleYaw,7:F4}"
			+ $" at {_tremble.PitchRateAt(_speed),4:F1} Hz\n"
			// Where it lands, which the two amplitudes above do not say: the
			// stern's own travel and the bow's, the pair whose ratio is the whole
			// of what this effect was fixed for. See TankSprite.SternWeight.
			+ $"stern {_tank.TrembleTravelPx(bow: false),5:F2}px   bow {_tank.TrembleTravelPx(bow: true),5:F2}px");

		ui.Heading("effects");
		// First, because it is the only thing here that is under the tank
		// rather than on it, and because it is the one switch whose A/B is the
		// whole argument for the layer: with it off the tank reads as a decal
		// laid on the field, which is the complaint it answers.
		ui.Toggle("effects.shadow", "contact shadow", () => _shadowEnabled, on =>
		{
			_shadowEnabled = on;
			ShadowChanged();
		});
		ui.Readout("effects.shadow_info", () =>
		{
			AtlasSet? a = _tank.Atlas;
			string where = _spriteDir is null ? "" : $", borrowed from {_spriteDir}";
			// headings beside it, because the two arrived together and the
			// second is the one nothing on screen announces: a tank rendered at
			// twelve turns in 30 deg steps beside one that turns in 15
			return a?.HasShadow == true
				? $"shadow  rendered, {a.Count} headings{where}"
				: $"shadow  [none - {a?.Count ?? 0} headings{where}, re-render]";
		});
		ui.Toggle("effects.tracks", "tracks wind  (C)", () => _tracksEnabled, on =>
		{
			_tracksEnabled = on;
			TracksChanged();
		});
		// No key: the alphabet is gone, and so are [ ] and \. The panel is
		// where a switch is found by name; the flag is there because a capture
		// is evidence and taking it twice must not need a hand on the mouse.
		ui.Toggle("effects.ruts", "track marks  (--no-ruts)", () => _marks?.Enabled == true, on =>
		{
			if (_marks is null)
				return;
			_marks.Enabled = on;
			_marks.QueueRedraw();
		});
		ui.Readout("effects.ruts_info", () =>
		{
			AtlasSet a = _tank.Atlas!;
			if (!a.HasTracks || _marks is null)
				return "ruts  [none - needs a parts-built model]";
			// Width and gauge together, because the pair is what the eye reads:
			// a rut of the right width at the wrong gauge is two ruts of some
			// other tank.
			return $"ruts {_marks.Count} pts, fading over {TrackMarks.Life:F0}s\n"
				   + $"{a.TrackWidth * _tank.BodyScale:F0}px wide at "
				   + $"{a.TrackArm * 2.0 * _tank.BodyScale:F0}px gauge, "
				   + $"{a.TrackPitch * _tank.BodyScale:F1}px shoe";
		});
		// The belt is the one layer tied to the ground rather than to a clock,
		// so what it is worth saying about it is how well it is keeping up. The
		// cap is a real limit and not a setting to hide: past it the tread would
		// alias and run backwards, so it slips instead, and that shows here.
		ui.Readout("effects.track_info", () =>
		{
			AtlasSet a = _tank.Atlas!;
			if (!a.HasTracks)
				return "tracks  [none - needs a parts-built model]";
			string phase = _tank.TrackPhaseLeft < 0
				? " - / -"
				: $"{_tank.TrackPhaseLeft,2} /{_tank.TrackPhaseRight,2}";
			return $"track {phase} of {a.TrackPhases}"
				   // as drawn, not as rendered: the size dial moves it, and the
				   // sync speed below moves with it
				   + $"   link {_track.LinkOnScreen,5:F1}px"
				   + $"\nin step to {_track.SyncSpeed,3:F0} px/s"
				   + $" of {_profile.TopSpeed:F0}"
				   + $"   slipping {(1.0 - _track.Slip) * 100.0,3:F0}%"
				   // Both thresholds, because they answer different questions:
				   // where the links stop being countable, and where the belt
				   // stops keeping up with the ground.
				   + $"\nsmears from {_track.BlurSpeed,3:F0} px/s"
				   + $"   blur {_track.Blur * 100.0,3:F0}%";
		});
		ui.Toggle("effects.exhaust", "engine exhaust  (O)", () => _exhaustEnabled, on =>
		{
			_exhaustEnabled = on;
			ExhaustChanged();
		});
		// Above the level rather than below it, because it decides what the
		// level's caption is describing.
		ui.Toggle("effects.exhaust_ramp", "load ramp instead of two states",
			() => _exhaustRamp, on => _exhaustRamp = on);
		// Frames per phase and not just the rate, because that is the question
		// the drag runs into: the plume is twelve rendered poses stepped a few
		// times a second, and the ceiling is how briefly a pose can be held
		// before the loop reads as a flicker rather than as smoke. The rate
		// alone does not say how close to it you are, and the multiplier says
		// less than that.
		//
		// Reads the board's level rather than the driven tank's - see
		// _exhaustLevel and ExhaustLoop.RateAt(speed, level).
		ui.Slide("effects.exhaust_level", "exhaust rate", 0.0, 3.0, 0.05,
			() => _exhaustLevel, v => _exhaustLevel = v, "x",
			() =>
			{
				double rate = _exhaust.RateAt(_speed, _exhaustLevel);
				double held = ExhaustLoop.FramesPerPhase(rate);
				// Which state, and not only the rate it works out to: the two
				// models can agree on a number and mean different things by it,
				// and "resting" on a tank that is plainly rolling is the one
				// failure this row could show and otherwise would not.
				return (_exhaustRamp
						   ? $"ramp {_exhaust.Response(_speed) * 100.0,3:F0}%"
						   : _exhaust.Response(_speed) > 0.0 ? "working" : "resting")
					   + $",  {rate,4:F1} phases/s at this speed"
					   + (double.IsInfinity(held)
						   ? ",  stopped"
						   : $",  {held:F1} frames a phase")
					   + (held < ExhaustLoop.FloorFramesPerPhase
						   ? $" - under {ExhaustLoop.FloorFramesPerPhase:F0}, flickers"
						   : "");
			});
		ui.Toggle("effects.fire", "on fire  (J)", () => _burning, on =>
		{
			_burning = on;
			UpdateBurn(Active, 0.0);
		});
		ui.Choice("effects.flash_source", "flash source  (V)", new[] { "rendered", "sheet" },
			() => _tank.Source == FlashSource.Rendered ? 0 : 1,
			i =>
			{
				_tank.Source = i == 0 ? FlashSource.Rendered : FlashSource.Sheet;
				_tank.QueueRedraw();
			});
		// Above its own level and pivot, because it gates both: a slider on a
		// switched-off effect is a slider that looks broken.
		ui.Toggle("effects.hull_shear", "hull shear on the shot  (])",
			() => _recoilShear, on => _recoilShear = on);
		// Read when the trigger goes, not while the hull is rocking, so this
		// sets the next shot rather than the one in the air.
		ui.Slide("effects.recoil_level", "recoil level", 0.0, 2.5, 0.05,
			() => _recoil.Level, v => _recoil.Level = v, "x",
			() => _recoilShear
				? $"kick peaks at {_recoil.PeakFor(_recoil.Level):F3}"
				  + $", rigid body ends at {Recoil.RigidBodyPeak:F3}"
				: "the shear is off - the tube recoils for real instead");
		// Under the recoil rows because it is the same trigger seen from
		// outside the tank: those two say what the vehicle does, this says what
		// the view does.
		ui.Toggle("effects.camera_shake", "camera shake on the shot",
			() => _shakeOn, on =>
			{
				_shakeOn = on;
				if (!on)
				{
					_shake.Reset();
					_camera.Offset = Vector2.Zero;
				}
			});
		ui.Slide("effects.shake_level", "shake level", 0.0, 2.5, 0.05,
			() => _shake.Level, v => _shake.Level = v, "x",
			() => _shakeOn
				// The class amplitude is what the level multiplies, so quoting
				// the level alone would answer nothing: the same 1.00x is three
				// pixels on the light and seven on the heavy. Across the screen,
				// because that is where the unnormalised direction is longest
				// and so where the number is the one worth guarding.
				? $"{_profile.ShotShake * _shake.Level:F1}px broadside, "
				  + $"{_profile.ShotShake * _shake.Level * 0.5:F1}px into the "
				  + "screen"
				: "off - every A/B near a shot would measure this instead");
		ui.Toggle("effects.recoil_turret", "recoil on turret only  (L)",
			() => _tank.RecoilTurretOnly, on => _tank.RecoilTurretOnly = on);
		ui.Toggle("effects.tube_recoil", "gun tube recoils  ([)",
			() => _recoilTube, on => _recoilTube = on);
		// What the tube is doing and what it cost. The stroke in pixels is the
		// number the whole layer is judged on - four is what it was authored to,
		// and it is a fraction of that on the headings where the bore points at
		// the camera, which is projection rather than a fault. Said here because
		// "phase 2 of 5" answers nothing about whether it reads.
		ui.Readout("effects.tube_info", () =>
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
		ui.Press("effects.fire_button", "fire  (Z)", Fire);
		// Phase counters against the number of phases that were rendered. A
		// clock that has walked off the end of its atlas shows up here as a
		// number out of range, and nowhere else until the layer goes blank.
		ui.Readout("effects.phase_info", () =>
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
		ui.Choice("gunnery.target", "target  (right-click a tank)",
			new[] { "nobody" }.Concat(_loaded).ToArray(),
			() => Active.Target is null ? 0 : _vehicles.IndexOf(Active.Target) + 1,
			i => Engage(Active, i == 0 ? null : _vehicles[i - 1]));
		// The reason, not a yes or no - see AimLine. The second line is what the
		// rule costs, which is the question the rule raises: a gun that fires
		// down six lanes can be pointed at a tank it cannot hit, and the number
		// of cells it *could* hit from here is the answer to "so where do I go".
		// The drawing only - the round flies either way, because the flight is
		// what puts time between the report and the impact. Off by default, so
		// the switch shows what the bench looked like before there was a tracer
		// *with* the timing that came with it, which is the comparison worth
		// having.
		ui.Toggle("gunnery.tracer", "shell tracer  (--tracer)",
			() => _tracerVisible,
			on => { _tracerVisible = on; TracerChanged(); });
		// The trail is what makes the tracer continuous once the round outruns
		// its own streak, so the switch is here to be able to see that rather
		// than to be told it.
		ui.Toggle("gunnery.tracer_smoke", "tracer smoke  (--no-tracer-smoke)",
			() => Shell.SmokeOn, on => Shell.SmokeOn = on);
		// Seconds, not a multiplier, because there is no class triple under it -
		// see Shell.SmokeSeconds. The caption turns it into the length of trail
		// it buys at the speed currently set, which is the thing being looked at,
		// and says how long the line hangs after the round has gone.
		ui.Slide("gunnery.smoke_life", "tracer smoke life", 0.1, 3.0, 0.05,
			() => Shell.SmokeSeconds, v => Shell.SmokeSeconds = (float)v, "s",
			() => $"{Shell.SmokeSeconds * Shell.Speed:F0}px of trail,"
				  + $" hangs {Shell.SmokeSeconds:F1}s after impact");
		// Sized against the class triple, so this is a multiplier - the rule the
		// tremble, size and traverse levels follow. The caption prints the head
		// in pixels for the driven tank, because a few pixels of bright on a
		// field of grass is the thing being judged and "1.00x" says nothing
		// about it.
		ui.Slide("gunnery.tracer_size", "tracer size level", 0.3, 3.0, 0.05,
			() => Shell.TracerLevel, v => Shell.TracerLevel = v, "x",
			() =>
			{
				double cal = Active.Profile.TracerCalibre * Shell.TracerLevel;
				return $"{2.1 * cal:F1}px head, {Shell.Streak * cal:F0}px streak"
					   + $" on the {Active.Tag}";
			});
		// A second level rather than the same one, because the streak and the
		// trail are judged against different things - see Shell.SmokeSize. Its
		// triple lives in classes.json and is wider than the tracer's: a soft line
		// has room a two-pixel head has not.
		ui.Slide("gunnery.smoke_size", "tracer smoke size level", 0.3, 3.0, 0.05,
			() => Shell.SmokeLevel, v => Shell.SmokeLevel = v, "x",
			() =>
			{
				double cal = Active.Profile.SmokeCalibre * Shell.SmokeLevel;
				// Width across, at birth and at the end of a puff's life, because
				// "a line that softens" and "a cloud that blooms" are the same
				// effect at two numbers and the second one reads as a road.
				double born = 2.0 * Shell.SmokeSeed * cal;
				double old = 2.0 * (Shell.SmokeSeed
									+ Shell.SmokeGrow * Shell.SmokeSeconds) * cal;
				return $"{born:F1}px wide fresh, {old:F0}px by the end"
					   + $" on the {Active.Tag}";
			});
		// Frames, not px/s, is what this slider is about: the round exists to put
		// time between the report and the impact, and the point-blank shot is
		// where that time runs out first. Four frames is the floor the self-test
		// holds; the slider is allowed past it and says so.
		ui.Slide("gunnery.shell_speed", "shell speed level", 0.4, 2.5, 0.05,
			() => Shell.SpeedLevel, v => Shell.SpeedLevel = v, "x",
			() =>
			{
				double frames = Shell.PointBlank / Shell.Speed * 60.0;
				return $"{Shell.Speed:F0} px/s, {frames:F1} frames point blank"
					   + (frames < 4.0 ? " - inside the gun report" : "");
			});
		// A multiplier over the three class figures, not a rate - see
		// Gunnery.TraverseLevel. The caption carries the swing beside the hull's
		// own, because the ratio between those two is what "sluggish" was
		// reporting and the multiplier on its own says nothing about it.
		ui.Slide("gunnery.traverse_level", "turret traverse level", 0.2, 3.0, 0.05,
			() => Gunnery.TraverseLevel, v => Gunnery.TraverseLevel = v, "x",
			() =>
			{
				MovementProfile p = Active.Profile;
				double rate = Math.Max(Gunnery.TraverseRate(p), 1e-6);
				return $"{rate:F0} deg/s, 180 deg in {180.0 / rate:F2}s"
					   + $" vs hull {180.0 / p.TurnRate:F2}s";
			});
		ui.Readout("gunnery.info", () =>
			$"{AimLine()}\nreload {Active.Profile.ReloadTime:F1}s, "
			+ $"traverse {Gunnery.TraverseRate(Active.Profile):F0} deg/s"
			+ $" ({Active.Profile.TurretRate:F0} x {Gunnery.TraverseLevel:F2}), "
			+ $"{_field.Arcs.Count} cells down the six lanes");

		ui.Heading("armour");
		// A side of the hex rather than a bearing, because that is what the
		// game has: a shell comes from a neighbouring cell, and there are six
		// of those. A slider ran 0..355 here and offered 66 bearings that
		// cannot happen.
		ui.Radio("armour.side", () => $"shot comes from side {_hitSide + 1}"
					   + $" - {HitFrom:F0} deg  (U steps)",
			Enumerable.Range(1, HexField.EdgeHeadings.Length)
				.Select(i => i.ToString()).ToArray(),
			() => _hitSide, i => _hitSide = i);
		ui.Choice("armour.calibre", "calibre  (Y)", new[] { "0.7x", "1.0x", "1.4x" },
			() => _calibre,
			i =>
			{
				// Only what the *next* shell will be. Nothing on screen moves:
				// a round already in the air keeps the calibre it went off at.
				_calibre = i;
			});
		// Takes the bearing on the slider rather than walking it on, so the
		// panel can aim and the key can sweep.
		ui.PressPair("armour.hit", "take a hit", () => TakeHit(HitFrom),
					 "repair", () => _tank.Repair());
		// No key. A-Z are gone, and so are the brackets and the backslash, and
		// the next free key is no longer a mnemonic - the tracer and the
		// traverse motor took the same answer. A button rather than a switch
		// because destruction happens rather than is; putting it back is what
		// reset and repair are for.
		ui.Press("armour.destroy", "destroy this tank  (--destroy)",
				 () => Kill(Active));
		ui.Readout("armour.wreck", () =>
			// How near the end it is, first, because that is the number a
			// firefight makes you want and the picture cannot give: a tank two
			// rounds in looks exactly like a tank one round in.
			!Active.Wreck.Dead
			? $"intact, {_tank.Penetrations} of {Gunnery.PenetrationsToKill}"
			  + " penetrations taken"
			: $"wrecked {Active.Wreck.Age:F1}s ago"
			  + $"   char {Active.Wreck.Char:F2}"
			  + $"   flame {Active.Wreck.Blaze:F2}, column {Active.Wreck.Smoke:F2}"
			  // Whether the pose is rendered at all, beside the char. A tank
			  // whose atlas predates the wreck layers is charred and level, and
			  // from the picture that is indistinguishable from a pose that did
			  // not take.
			  + (Active.Atlas.HasWreck
				 ? Active.Atlas.HasWreckTracks ? "   posed" : "   posed, belts live"
				 : "   no wreck pose in this atlas"));
		ui.Readout("armour.info", () =>
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

		// The sound had no rows at all until now, which was an omission rather
		// than a decision: the panel is where a switch is found by name, and '\'
		// is not a name. The motor gets its own line beside the master because it
		// is the one voice that was built rather than recorded and the one whose
		// level was argued for rather than heard.
		ui.Heading("sound");
		ui.Toggle("sound.master", "sound  (\\)", () => _soundEnabled,
				  on => { _soundEnabled = on; SoundChanged(); });
		ui.Toggle("sound.turret_motor", "turret motor  (--no-turret-sound)", () => _turretSound,
				  on => { _turretSound = on; SoundChanged(); });
		ui.Readout("sound.info", () =>
		{
			VehicleAudio? a = Active.Audio;
			if (a is null)
				return "no sound loaded - run stage_sounds.sh";
			// The bus peak rather than the gates: gates say what was asked for,
			// the peak says what came out, and only the second one tells a set
			// that loaded from a set that is audible.
			float peak = AudioServer.GetBusPeakVolumeLeftDb(0, 0);
			return $"ring {a.TurretGate:F2} at {a.TurretPitch:F2}x, "
				   + $"motor {a.TurretDb:F0}dB\n"
				   + $"bus peak {(float.IsNegativeInfinity(peak) ? -99.0f : peak):F1}dB";
		});

		ui.Heading("ground");
		// A dropdown rather than numbered buttons: how many kinds load is
		// whatever is on disk, so a fixed row of them goes stale the first time
		// one is drawn - the lesson the tank list already taught. "mixed" heads
		// it because it is the board as a map would build it; the rest are there
		// to hold one kind still and look at it.
		ui.Choice("ground.terrain", "terrain  (--terrain)", _paints,
			() => Math.Max(0, _paints.IndexOf(_field.Paint)),
			i =>
			{
				_field.Paint = _paints[i];
				_paint = _field.Paint;
				// Forest is a kind of ground, so choosing the ground chooses
				// where the woods are - and the trees are nodes rather than
				// pixels of the plate, so redrawing the field does not touch
				// them. Left out, the board painted forest came up as bare soil
				// until something else happened to re-sow, which reads as the
				// kind not existing rather than as a missing call.
				SowGrove();
				_field.QueueRedraw();
			});
		// The board's shape, where the row above is its paint. Two rows and not
		// one, because they are two different things about the ground and only
		// one of them is a picture: a kind does not cost movement, block a lane
		// or hide a tank behind it, and a level does all three. See TerrainSet.
		ui.Toggle("ground.relief", "height on the board  (--relief)",
			() => _field.HasRelief, SetRelief);
		// Reads the board rather than the request, and that is deliberate: outside
		// --3d there is nothing that can draw a slope, so the box springs back and
		// says so. A row that claimed the ramps were on while the board had none
		// would be the quiet half of the same failure.
		ui.Toggle("ground.ramps", "ramps on the levels  (--no-ramps)",
			() => _field.HasRamps, SetRamps);
		// The board rather than the request, for the row above's reason exactly:
		// outside --3d nothing can draw a surface standing over the ground, so the
		// box springs back and says so.
		ui.Toggle("ground.water", "water in the pit  (--no-water)",
			() => _field.HasWater, SetWater);
		// How deep it stands, in the units it is authored in and the units it is
		// judged in. The fraction is what carries across tiles; the pixels and what
		// they cover are what says whether it reads as a ford, and only the second
		// pair answers the question the slider is dragged to answer.
		ui.Slide("ground.depth", "how deep the water is  (--depth)",
			0.10, 1.20, 0.05,
			() => _field.WaterDepth, SetDepth,
			"of a level", () =>
			{
				float deep = _field.WaterRise;
				float tall = DrawnHeight;
				double part = tall > 0.0f ? deep / tall : 0.0;
				return $"{deep:F0}px, {part:P0} of a medium's drawn height"
					   + (part > SunkAt
						  ? $"   - past {SunkAt:P0} it reads as sinking, not fording"
						  : part < ShallowAt
							  ? $"   - under {ShallowAt:P0} there is nothing to see"
							  : "");
			});
		// Reads the flag rather than the stage, and that is not laziness: the row
		// has to say what was asked for even on a run where there is no strip on
		// disk to draw, or a bench with the art missing would show the box ticking
		// itself off and blame the toggle for the file.
		ui.Toggle("ground.water_paint", "drawn water surface  (--flat-water)",
			() => _waterPaint, SetWaterPaint);
		// How fast the swell turns, and what it costs in the units the complaint
		// was made in: how long one frame is held, and how many screen frames that
		// is. Both, because the first says how the water reads and the second says
		// when it stops being an animation at all.
		ui.Slide("ground.swell", "water speed  (--swell)",
			0.4, 6.0, 0.2,
			() => _swellPeriod, v => _swellPeriod = v,
			"s per turn", () =>
			{
				int frames = _waterArt?.Frames ?? 0;
				if (frames < 1 || _swellPeriod <= 0.0)
					return "no drawn surface to turn";
				double held = _swellPeriod / frames;
				return $"{frames} frames, {held:F2}s each = {held * 60.0:F0} screen"
					   + " frames"
					   + (_swellBlend
						  ? "   - crossed, so the step is not seen"
						  : held * 60.0 > StepsAt
							  ? $"   - over {StepsAt:F0} it reads as a slideshow"
							  : "");
			});
		// The A/B, and the only thing that answers "jerky": four frames step at
		// any speed, so the choice is between stepping and dissolving.
		ui.Toggle("ground.swell_blend", "carry each frame into the next  (--hard-swell)",
			() => _swellBlend, v => _swellBlend = v);
		// Whether the water answers the tanks at all, and what it is doing right
		// now - the second is a readout because the bench measures it and nobody
		// sets it, which is the whole of what a readout is.
		// Which pond this is. Above the rows that describe the strip, because
		// with this on most of them are describing something that is not being
		// drawn.
		ui.Toggle("ground.deep", "computed surface  (--drawn-water)",
			() => _deepWater, v => _deepWater = v);
		ui.Toggle("ground.wake", "tanks leave a wake  (--no-wake)",
			() => _wakes, v => _wakes = v);
		ui.Toggle("ground.bow", "water piles up at the bow  (--no-bow)",
			() => _bows, v => _bows = v);
		ui.Toggle("ground.water_react", "water answers the tanks  (--still-water)",
			() => _seaReacts, v => _seaReacts = v);
		ui.Readout("ground.water_state", () =>
		{
			if (_sea is null || _field.WaterCells.Count == 0)
				return "no water on the board";
			float peak = _sea.Peak;
			string band = peak >= Swell.SplashBand - 0.5f ? "breaking"
						: peak >= Swell.ChurnBand - 0.5f ? "chop" : "calm";
			return $"{peak:F2} of {Swell.SplashBand:F0} at its busiest, {band}"
				   + $"   ({_field.WaterCells.Count} cells)";
		});
		// A multiplier over none, not an opacity: what the foam is allowed to say
		// is set by the band under it, and a knob that wrote the amount directly
		// would let it speak where the strip says calm - which is the one thing it
		// must not do. The line under it names the rung, because "1.00x" answers
		// none of the questions the slider gets dragged for.
		ui.Slide("ground.foam", "foam on stirred water  (--foam / --no-foam)",
			0.0, 2.0, 0.05,
			() => _foam, v => _foam = (float)v,
			"x", () =>
			{
				if (_sea is null || _field.WaterCells.Count == 0)
					return "no water on the board";
				if (_foam <= 0.0f)
					return "off - the strip talks on its own, as it did before this";
				float peak = _sea.Peak;
				return peak >= Swell.SplashBand - 0.5f
						   ? "a raft, on the breaking band"
					   : peak >= Swell.ChurnBand - 0.5f
						   ? "flecks, on the chop"
						   : "nothing to foam - calm water takes none by construction";
			});
		// The number that decides whether a hill reads as one, and the only
		// reason it is a dial rather than a constant: a level is worth
		// grade * sqrt(3) * R * cos(e) on screen, and against a hexagon 54.5px
		// from centre to edge that is what says how much of a tank standing
		// behind a rise the rise gets to hide. See the note under it.
		ui.Slide("ground.grade", "how steep one level is  (--grade)",
			0.05, 0.60, 0.05,
			() => _field.StepGrade, SetGrade,
			"rise per hex", () =>
			{
				float lift = _field.Lift;
				float half = _field.Atlas is null ? 0.0f
					: _field.Atlas.HexRect.Size.Y * 0.5f;
				// What a rise one row in front takes off a tank behind it. Its
				// face begins at contact + 2*half - lift, so it reaches the hull
				// only once a level is worth more than half a hexagon - which at
				// the tuned 1:4 it is not, and that is the whole of why a hill
				// there hides tracks and nothing else.
				float hull = lift - half;
				return $"1:{1.0 / Math.Max(0.001, _field.StepGrade):F1}, "
					   + $"{lift:F0}px a level"
					   + (hull > 0.0f
						  ? $"   - a rise one hex ahead hides {hull:F0}px of a "
							+ "tank's hull behind it"
						  : $"   - below {half:F0}px a rise hides tracks only, "
							+ $"never hull (needs 1:{2.0 * half / (lift / Math.Max(0.001, _field.StepGrade)):F1})");
			});
		// What the cursor is over, and everything that cell uses to decide
		// whether it covers the driven tank.
		//
		// Here rather than on the scene, where the numbers would be next to the
		// thing they are about: the panel is the one place this bench shows its
		// figures, and a label following the mouse is in every screenshot. It is
		// in the ground group because it is about the ground.
		//
		// It exists because "that hex should be covering the tank" cannot be
		// settled by looking - it took two rounds of screenshots to find that the
		// ordering was right and the grade was too shallow. All of it is one
		// question asked in four numbers: which cell, how high, is it in front,
		// and where its face begins against where the tank stands.
		ui.Readout("ground.hover", () =>
		{
			if (_field.Atlas is null)
				return "no atlas";
			Vector2 local = _field.ToLocal(GetGlobalMousePosition());
			Vector2I cell = _field.CellAt(local);
			if (!_field.InBounds(cell))
				return "off the board";
			string what = $"hex {cell.X},{cell.Y}   level "
						  + $"{_field.LevelAt(cell):+0;-0;0}   {_field.KindAt(cell)}";
			if (!_field.HasRelief)
				return what + "\nflat board - nothing covers anything";

			Vehicle v = Active;
			float row = v.GroundPoint.Y - _origin.Y + v.Height;
			bool hides = _field.Occluders(row, v.Height).Contains(cell);
			// Where this cell's face begins against where the tank's belts touch
			// down. Positive means the face starts above the contact row, which
			// is the only way a cell can reach hull rather than track - and the
			// number that said the hill was too low to reach either.
			float half = _field.Atlas.HexRect.Size.Y * 0.5f;
			float top = _field.CellCentre(cell).Y - half;
			float over = (row - v.Height) - top;
			float nearer = _field.DepthOf(cell) - _field.Depth(row, v.Height);
			return what + $"\nvs {v.Tag} on {cell.X},{cell.Y} == "
				   + $"{(cell == v.Cell ? "same cell" : $"{v.Cell.X},{v.Cell.Y}")}"
				   + $"   {(hides ? "COVERS it" : "does not cover it")}\n"
				   + $"face starts {over:+0.0;-0.0;0.0}px "
				   + (over > 0.0f ? "above" : "below") + " its contact row, "
				   + $"{nearer:+0.0;-0.0;0.0}px nearer the camera";
		});
		// A switch, not a dial: how much forest there is comes from the terrain
		// list above, because forest is one of the kinds of ground. This is the
		// A/B - the same board with the trees taken off it.
		ui.Toggle("ground.forest", "trees  (--no-forest)",
			() => _grove?.Enabled == true,
			on =>
			{
				if (_grove is null) return;
				_grove.Enabled = on;
				_field.Trees = on;
				SowGrove();
				_field.QueueRedraw();
			});
		// Read live rather than at the moment a tank enters, unlike the shell's
		// calibre: this is not something a round carries, it is how the wood is
		// being drawn right now, and dragging it while a tank sits in the trees
		// is exactly how it gets judged.
		ui.Toggle("ground.shadows", "cast shadows  (--no-cast-shadows)",
			() => _castShadows, on => _castShadows = on);
		ui.Slide("ground.ghost", "trees over a tank in them  (--ghost)",
			0.0, 1.0, 0.05,
			() => _grove?.Ghost ?? 0.0,
			v => { if (_grove is not null) _grove.Ghost = (float)v; },
			"alpha", () =>
			{
				if (_grove is null)
					return "";
				int hidden = _grove.Trees.Count(t => t.Modulate.A < 0.99f);
				return $"{hidden} trees faded now"
					   + (_grove.Ghost <= 0.001f
						  ? "   - gone: a tank standing in a field"
						  : _grove.Ghost >= 0.999f
							? "   - solid: the wood keeps the tank" : "");
			});
		// Two sliders stepping whole trees, clamped against each other in the
		// setters rather than left to disagree - a floor above the ceiling is
		// not a board anyone wanted to see, and refusing it here is cheaper
		// than deciding which of the two wins during sowing.
		ui.Slide("ground.least", "trees per wooded cell, at least", 0.0, 20.0, 1.0,
			() => _grove?.Minimum ?? 0,
			v =>
			{
				if (_grove is null) return;
				_grove.Minimum = (int)v;
				if (_grove.Maximum > 0 && _grove.Maximum < _grove.Minimum)
					_grove.Maximum = _grove.Minimum;
				SowGrove();
			},
			"", () => _grove is null ? "" : $"{Wooded()} wooded cells, "
				+ $"{_grove.Planted} trees, {Thinnest()} on the emptiest");
		ui.Slide("ground.most", "and at most  (0 for no ceiling)", 0.0, 20.0, 1.0,
			() => _grove?.Maximum ?? 0,
			v =>
			{
				if (_grove is null) return;
				_grove.Maximum = (int)v;
				if (_grove.Maximum > 0 && _grove.Maximum < _grove.Minimum)
					_grove.Minimum = _grove.Maximum;
				SowGrove();
			},
			"", () => _grove is null ? "" : $"{Thickest()} on the fullest"
				+ (_grove.Maximum > 0 ? "" : "   - whatever the ground allows"));
		// What actually grew, by kind. The spot picks among the trees that fit
		// it, so a wide prop is thinned by the ground rather than by any
		// setting - and there is nowhere else that would say so: a kind that
		// never gets planted looks exactly like a kind nobody drew.
		ui.Readout("ground.props", () =>
		{
			if (_props is null || !_props.Any)
				return "no tree art";
			var grown = new int[_props.Count];
			foreach (PropNode tree in _grove?.Trees ?? new List<PropNode>())
				grown[tree.Species]++;
			return string.Join("\n", Enumerable.Range(0, _props.Count).Select(
				k => $"{_props.NameOf(k)}  {grown[k]} grown, "
					 + $"{_props.RiseOf(k) * (_grove?.DrawScale ?? 0.0f):F0}px tall, "
					 + $"base {_props.RootOf(k) * (_grove?.DrawScale ?? 0.0f):F1}px"));
		});
		// The rule that cannot be had, kept as a switch so that is visible
		// rather than asserted. See Grove.ClearFront: a 111px tree needs 142px
		// of clearance in front of a tank and a hexagon offers 54, so switching
		// it on empties the front of every clearing.
		// In crown pixels, because that is what it looks like - the shear it
		// becomes is what makes a taller tree lean further, and that part is
		// not up for tuning. The caption reads the widest lean on the board
		// right now rather than the setting, because the gust travels and the
		// number the setting names is only reached where the gust is.
		ui.Slide("ground.wind", "wind, crown drift  (--wind)", 0.0, 8.0, 0.2,
			() => _grove?.Wind ?? 0.0,
			v => { if (_grove is not null) _grove.Wind = v; },
			"px", () =>
			{
				if (_grove is null || _grove.Planted == 0)
					return "";
				double most = _grove.Trees.Max(t => Math.Abs(t.Drift));
				return _grove.Wind <= 0.0
					? "still - the board is a photograph of a board"
					: $"{most:F1}px at the most leaning crown just now";
			});
		// A multiplier over the three per-event strengths, not a strength: a
		// shot, a hit and a tank going up are meant to be told apart by how hard
		// the wood answers, and a knob that set the amplitude directly would
		// flatten that on the first drag - the argument the tremble level and
		// the class size both make. Read at the moment something goes off, like
		// the recoil level: the kick is delivered once and the rest is a spring
		// ringing down.
		ui.Slide("ground.blast", "blast wave, crown shove  (--blast)",
			0.0, 3.0, 0.1,
			() => _grove?.Blast ?? 0.0,
			v => { if (_grove is not null) _grove.Blast = v; },
			"x", () =>
			{
				if (_grove is null || _grove.Planted == 0)
					return "";
				if (_grove.Blast <= 0.0)
					return "deaf - a gun goes off in the wood and nothing moves";
				double most = _grove.Trees.Max(t => Math.Abs(t.Flinch * t.Rise));
				return $"{_grove.ShotBlast * _profile.ShotShake * _grove.Blast:F1}"
					   + $"/{_grove.HitBlast * _grove.Blast:F0}"
					   + $"/{_grove.DeathBlast * _grove.Blast:F0}px"
					   + " at the source for a shot/hit/kill"
					   + (_grove.Waves > 0
						  ? $"   - {_grove.Waves} crossing, {most:F1}px flinching"
						  : "");
			});
		// Its own row rather than a share of the blast's, because it is its own
		// mechanism: a force while the hull is near against an impulse once, and
		// the pair of them is the A/B that shows the difference. In crown pixels
		// like the wind, because that is what both are looked at in.
		ui.Slide("ground.brush", "hull through the trees  (--brush)",
			0.0, 15.0, 0.5,
			() => _grove?.Brushing ?? 0.0,
			v => { if (_grove is not null) _grove.Brushing = v; },
			"px", () =>
			{
				if (_grove is null)
					return "";
				if (_grove.Brushing <= 0.0)
					return "a tank drives through the wood and nothing gives";
				double sped = Math.Min(1.0, Active.Speed / _grove.BrushSpeed);
				return $"{_grove.Brushing * sped:F1}px right against the hull at "
					   + $"{Active.Speed:F0}px/s"
					   + (Active.Speed <= 0.0
						  ? "   - parked, so nothing is being pushed" : "");
			});
		ui.Toggle("ground.clearfront", "no tree may cross a tank  (--clear-front)",
			() => _grove?.ClearFront == true,
			on => { if (_grove is not null) { _grove.ClearFront = on; SowGrove(); } });
		ui.Readout("ground.info", () =>
		{
			if (_terrain is null || !_terrain.Any)
				return "no terrain art - rendered tile";
			Rect2I hex = _field.Atlas!.HexRect;
			// The camera error in the panel as well as the log, because it is
			// the one number here that decides whether the picture can be
			// trusted at all, and a warning printed at startup has scrolled off
			// by the time anyone is looking at the board.
			return $"{_terrain.Names.Count} kinds, plate {_terrain.Plate.Size.X}"
				   + $"x{_terrain.Plate.Size.Y} at {_terrain.ScaleTo(hex):F3}x\n"
				   + $"camera {_terrain.CameraError(hex) * 100.0f:F1}% off"
				   + (_terrain.CameraAgrees(hex) ? "" : "  <- clicks will drift");
		});

		ui.Heading("view");
		// No zoom row. The wheel already does it, and a slider on it is the one
		// control that would silently rescale every A/B taken from the panel:
		// almost everything measured here is a pixel diff of two captures, and
		// a nudged zoom moves the whole picture rather than a silhouette. It is
		// in --trace for exactly that reason, and a readout is what it wants to
		// be, not a knob.
		ui.Toggle("view.hull", "hull layer  (H)", () => _tank.ShowHull, on => _tank.ShowHull = on);
		ui.Toggle("view.turret", "turret layer  (T)",
			() => _tank.ShowTurret, on => _tank.ShowTurret = on);
		ui.Toggle("view.field", "hex field  (G)", () => BoardShown,
				  on => BoardShown = on);
		// No key: A-Z, the brackets and the backslash are all spoken for, and the
		// next free key is not a mnemonic. The panel is where a switch is found
		// by name - the same answer the tracer and the traverse motor got.
		ui.Toggle("view.stage", "3d stage", () => Staged, on => Staged = on);
		ui.Toggle("view.edges", "cell outlines", () => _cellEdges, on =>
		{
			_cellEdges = on;
			if (_stage is not null)
				_stage.ShowEdges = on;
		});
		ui.Toggle("view.axis", "axis cross  (X)", () => _tank.ShowAxis, on => _tank.ShowAxis = on);
		ui.PressPair("view.reset", "reset  (R)", ResetAll,
					 "screenshot  (F12)", () => Capture(
						 $"{ProjectSettings.GlobalizePath("res://")}shot_{Time.GetTicksMsec()}.png"));
	}

	/// <summary>Where the nth shell lands along its plate, as a fraction of the
	/// plate's half-width. Deterministic on purpose: --capture and --trace fix
	/// the time step so two runs can be diffed, and a random scatter would be
	/// measuring itself.</summary>
	internal static float ScatterAt(int n) => (((n * 37) % 100) / 100.0f - 0.5f) * 0.9f;

	/// <summary>
	/// How far along its plate a hit may sit, as a fraction of the half-width.
	///
	/// It bounds <see cref="ScatterAt"/> above, which is why they are one number:
	/// the hash was written to reach exactly this far, and a fired round now has
	/// its offset *solved* rather than hashed - see
	/// <see cref="Gunnery.ScatterOntoBore"/> - so the limit had to become a thing
	/// with a name instead of a 0.9 buried in a hash. Both paths land on the same
	/// armour and the mark is the same size on it.
	/// </summary>
	internal const float ScatterLimit = 0.45f;

	/// <summary>
	/// How far a fired round disperses about where the gun was actually aimed,
	/// as a fraction of the plate's half-width.
	///
	/// A quarter of <see cref="ScatterLimit"/>, and the difference between the
	/// two numbers is the whole change: the old hash reached the full 0.45
	/// because it *was* the placement, and 0.45 of a 28 to 53px half-width runs
	/// almost exactly across the shot, so it was up to 24px of visible daylight
	/// between the tube and its own hole. A group of rounds fired at one tank
	/// from one place is a group, not a plate-wide spread.
	/// </summary>
	internal const float ScatterSpread = 0.12f;

	/// <summary>
	/// Where the nth round from this gun lands along that plate: aimed, then
	/// dispersed, then kept on the armour.
	///
	/// One function rather than the three steps written out at the trigger,
	/// because the self-test has to be able to ask what actually happens. A check
	/// that re-assembled the same three steps for itself would keep passing after
	/// the trigger stopped doing one of them - the shape of failure this project
	/// names as a check whose answer goes nowhere.
	/// </summary>
	internal static float AimedScatter(Vector2 muzzle, Vector2 bore,
									   Vector2 centroid, Vector2 tangent,
									   Vector2 slope, float rise, int n) =>
		Math.Clamp(
			Gunnery.ScatterOntoBore(muzzle, bore, centroid, tangent, slope, rise,
									ScatterLimit, ScatterAt(n))
			+ ScatterSpread * (ScatterAt(n) / ScatterLimit),
			-ScatterLimit, ScatterLimit);

	/// <summary>
	/// And where up the plate's slope, as a fraction of its half-height.
	///
	/// **+-0.30 against the tangent's +-0.45, and the asymmetry is the plate's,
	/// not a preference.** A plate is two to three times wider than it is tall
	/// (tangent 28-53px against slope 7-22px across the three tanks), so even an
	/// equal fraction would already draw an ellipse - which is right, a group of
	/// rounds on a wide short plate *is* wider than it is tall. What makes the
	/// vertical fraction the smaller one as well is that the mark is nearly as
	/// tall as the plate is: <c>scar_fit</c> runs 0.62 to 1.30, so vertical
	/// headroom is 5-8% of a half-height on the rear plates and negative on HTP's.
	/// The mark exceeding its plate is not the same as leaving the tank - it slides
	/// onto the deck above or the lower hull - but it is the scarce direction, and
	/// this is the number to pull back if <c>scar_off_armour</c> ever complains.
	///
	/// A different modulus, not just a different multiplier, and that is not
	/// fussiness: 61n and 37n are both invertible mod 100, so 61n = 53*(37n) and
	/// the vertical would be a *function* of the horizontal - every pair sitting
	/// on one of a handful of lines. 97 is prime and coprime to 100, so the pair
	/// does not repeat for 9700 rounds.
	/// </summary>
	internal static float RiseAt(int n) => (((n * 61) % 97) / 97.0f - 0.5f) * 0.6f;

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
			sprite.HitOffset = atlas.HitOffset(hit.Face, sprite.HullFacing)
							   + atlas.HitTangent(hit.Face, sprite.HullFacing)
								 * hit.Scatter
							   + atlas.HitSlope(hit.Face, sprite.HullFacing)
								 * hit.Rise;
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
		if (!_scanEnabled || v.Moving || v.Target is not null || v.Wreck.Dead
			|| (v == Active && (_spinning || _aimWithMouse)))
		{
			v.Scan.Suspend();
			return;
		}
		double step = 360.0 / v.Atlas.Count;
		if (!v.Scan.Based)
		{
			// The sway is an offset, so it needs a bearing to be an offset from,
			// and the honest one is where the turret has just been left - a tank
			// that has finished laying its gun down a lane sways about that lane.
			// Snapped to a rendered bearing, which costs nothing on screen: the
			// sprite is drawn at the nearest rendered heading regardless, so this
			// moves the number and not the picture.
			double onto = Mod(Math.Round(v.Sprite.TurretFacing / step) * step, 360.0);
			v.Scan.Rest(onto);
			if (onto != v.Sprite.TurretFacing)
			{
				v.Sprite.TurretFacing = onto;
				v.Sprite.QueueRedraw();
			}
		}
		double move = v.Scan.Advance(step, delta);
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
			delta = FixedStep ?? 1.0 / 60.0;

		if (_traceFrames > 0)
		{
			// Which tank the line is about. There are three of them now and only
			// one is being driven, so a trace without this says nothing about
			// whether the selection went where the flag asked - the same reason
			// the zoom and the size are printed here.
			GD.Print($"{_frames,4}  {Active.Tag,-3}"
					 + $"  hull {_tank.HullFacing,6:F1}  speed {_speed,6:F1}"
					 // What it is allowed to be doing, and only when that is not the
					 // class figure. A tank crawling up a bank and a tank that lost
					 // its order look the same on a still and read the same in a bare
					 // speed; "grade" is the frame this leg is capped on. See
					 // Main.SpeedCap.
					 + (SpeedCap(Active) < _profile.TopSpeed
						? $"/{SpeedCap(Active),5:F0} {SpeedCapWhy(Active)}" : "")
					 // Whether it is in the water at all, which the cap above does
					 // not say: a tank parked in a ford is capped by nothing,
					 // because it is not moving, and it is still wading.
					 + (Active.Wading ? $"  wet {Active.Waterline,5:F0}" : "")
					 // The slope under the tank and what it is drawn as. Both,
					 // because they answer different questions: a grade with no
					 // angle beside it is a spring that did not arrive, and an angle
					 // with no grade is a lean nothing on the board asked for. And
					 // here rather than only on the panel, because a tank standing
					 // level on a visible slope is exactly the failure a capture is
					 // taken to judge - see Main.SurfaceGrade.
					 + $"  lean {Active.Sprite.Climb,8:F5}@{SurfaceGrade(Active),6:F3}"
					 // And what the same plane does to the shadow, which the lean
					 // beside it cannot report: driving straight into the screen
					 // the body's rotation is zero and the footprint is still
					 // stretched half again. Two numbers that are only ever both
					 // nought on flat ground. See TankSprite.Slope.
					 + $"/{Active.Sprite.Slope.X,6:F3},{Active.Sprite.Slope.Y,6:F3}"
					 // Which of the two ways the stage is drawing it, because the
					 // grade beside it does not answer that: a tank parked across a
					 // ramp feels no slope along its heading and is still standing on
					 // one. See Vehicle.OnSlope.
					 + (Active.Levelling || Active.OnSlope ? " over" : " depth")
					 + $"  pitch {_tank.Pitch,8:F5}  shake {_tank.Shake,2}"
					 + $"  roll {_tank.Roll,8:F5}  trem {_tank.TremblePitch,8:F5}"
					 + $"/{_tank.TrembleYaw,8:F5}"
					 // The two ends, because the amplitudes beside them cannot say
					 // which end of the tank moves and that is the whole statement
					 // this effect makes. A bow figure that grows with the stern's is
					 // a tremble that has gone back to shaking the whole hull.
					 + $"@{_tank.TrembleTravelPx(bow: false),4:F1}/{_tank.TrembleTravelPx(bow: true),4:F1}px"
					 + $"  scan {_scan.Offset,6:F1}"
					 + $"  turret {_tank.TurretFacing,6:F1}"
					 // The rate the gun is driven at, in the channel that
					 // survives --no-ui. A traverse that is quietly still on the
					 // tuned figure looks exactly like one the flag reached, and
					 // the only difference between the two is how long a lay
					 // takes - which a still frame cannot show at all.
					 + $"  trv {Gunnery.TraverseRate(Active.Profile),4:F0}"
					 + $"x{Gunnery.TraverseLevel,4:F2}"
					 + $"  shot {_tank.FlashFrame,3}"
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
					 // The level beside the phase, for the reason the traverse
				 // prints its own: "the tuned figure is in" and "the flag
				 // arrived" are the same picture.
				 + $"  exh {_tank.ExhaustPhase,2}@{_exhaust.Phase,5:F2}"
				 + $" x{_exhaustLevel,4:F2}"
				 // Which model, because the two agree at both ends: a trace taken
				 // standing still, or at cruise, cannot tell them apart, and those
				 // are the two frames most likely to be traced.
				 + (_exhaustRamp
					 ? $"ramp{_exhaust.Response(_speed),4:F2}"
					 : _exhaust.Response(_speed) > 0.0 ? "work" : "rest")
					 // slip as well as phase: the cap is a real limit, and a
					 // belt quietly running at two thirds of the ground is not
					 // something to have to work out from the phase alone
					 + $"  trk {_tank.TrackPhaseLeft,2}/{_tank.TrackPhaseRight,2}"
					 + $"@{_track.Phase,5:F2}"
					 + $" x{_track.Slip,4:F2}"
					 // and the smear, because a fully blurred belt and a stopped
					 // one look alike in a phase number and nothing alike on
					 // screen
					 + $" b{_track.Blur,4:F2}"
					 // The tube, in the channel that survives --no-ui, and with
					 // the switch beside it: 'rest' with the flag off and 'rest'
					 // between shots are the same number and not the same thing.
					 + $"  tube {_tank.RecoilPhase,2}"
					 + $"{(_recoilTube ? "" : "!off")}"
					 + $"  burn {_tank.FirePhase,2}/{_tank.BurnPhase,2}"
					 // The wreck, and the char beside it: a tank that is dead
					 // and a tank that is dead and has finished blackening are
					 // the same word and different pictures, and the panel is
					 // not there under --capture.
					 + $"  wreck {(Active.Wreck.Dead ? $"{Active.Wreck.Age,5:F1}s" : " alive")}"
					 // How far through the three it is. Alive is now a range
					 // rather than a state, and nothing in the picture says which
					 // end of it a tank is at.
					 + $" {_tank.Penetrations}/{Gunnery.PenetrationsToKill}"
					 // whether the pose exists, because a charred level turret
					 // and a pose that failed are the same picture
					 + (Active.Atlas.HasWreck
						? Active.Atlas.HasWreckTracks ? " posed" : " posed-turret"
						: " unposed")
					 + $" c{Active.Wreck.Char:F2}"
					 + $" f{_tank.FireDensity:F2}"
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
					 + $"  zoom {_camera.Zoom.X:F2}x"
					 // Which ground is drawn over this tank, and which cells.
					 // "It should be hidden by that hex" is an argument about a
					 // picture and cannot be settled on one: the two answers -
					 // the cell is not higher, and the cell is higher and the
					 // repaint did not happen - look identical on screen and are
					 // repaired in different places. This says which.
					 + $"  hidden {Hiding(Active)}"
					 // A capture whose pixels came off another tank has to say
					 // so: nothing in the picture does, and every conclusion
					 // drawn from it is about that tank's atlas rather than
					 // this class's.
					 + (_spriteDir is null ? "" : $"  sprites {_spriteDir}")
					 // Whether the settings file was read, in the channel that
					 // survives --no-ui. A panel.json that quietly did not load
					 // looks exactly like one whose settings did nothing, and
					 // under --no-ui there is no panel to notice it on.
					 + $"  panel {(_panelText.Loaded ? "json" : "built-in")}"
					 // And the same for the per-class file. Two files now, and
					 // they answer for different things: one for the panel's text
					 // and openings, one for the class triples. A capture taken on
					 // the compiled figures is a capture of a different tank.
					 + $"  classes {(_classConfig.Loaded ? "json" : "built-in")}"
					 // Same argument as the line above: a board of one kind and
					 // a board of a mix are two different pictures, and neither
					 // says which it is.
					 + $"  ground {_field.Paint}"
					 // What the water is doing, and under the driven tank as well
					 // as at its worst. Two numbers because they fail apart: a
					 // pond that reacts to nobody and one that reacts to the wrong
					 // cell are the same still picture, and the trace is the only
					 // channel that survives --no-ui.
					 + (_sea is null || _field.WaterCells.Count == 0 ? "  sea -"
						: $"  sea {_sea.StateAt(_field.CellAt(Active.GroundPoint - _origin)):F2}"
						  + $"/{_sea.Peak:F2}"
						  // How many cells are running the milder half of their
						  // band, which is what a cell being left over shows.
						  // Counted rather than shown for the driven tank's own
						  // cell: the cell it is leaving is by definition the one
						  // it is no longer standing in, so read off the tank it
						  // would always be nought.
						  + $"/{_sea.Mild}mild"
						  // What the foam is set to, and !off rather than a
						  // nought, because a pond nobody has stirred and a pond
						  // with the foam turned off are the same clean picture.
						  + (_foam > 0.0f ? $"/{_foam:F2}foam" : "/!off")
						  + (_deepWater ? "/deep" : "/strip")
						  + (_wakes ? $"/{_wake?.Count ?? 0}wake" : "/!nowake")
						  // How hard the driven tank is shouldering the water,
						  // because a crest that is not there and a crest whose
						  // push came out nought are the same still water.
						  + (_bows ? $"/{BowPush():F2}bow" : "/!nobow")
						  + (_seaReacts ? "" : "!still"))
					 // Trees, wooded cells, and how many of those no tank may
					 // enter. Three numbers because a board with no props, a
					 // board whose props were all refused and a board sown at
					 // zero coverage are one empty picture and three faults.
					 + $"  forest {_grove?.Planted ?? 0}t/{Wooded()}c"
					 + $"/{Thinnest()}min"
					 // How many are standing aside right now. The one number
					 // that says when the wood opened and when it closed, and
					 // neither end is visible in a still frame: the fade is
					 // gradual by design, so a screenshot shows a state and not
					 // the moment it started.
					 + $"/{_grove?.Trees.Count(t => t.Modulate.A < 0.99f) ?? 0}fade"
					 + (_grove?.Enabled == true ? "" : "!off")
					 + (_grove?.ClearFront == true ? "!clear" : "")
					 // The blast: fronts still crossing and the hardest-shoved
					 // crown - which counts the hulls pushing through as well,
					 // because both drive the one spring. A wave delivers over a
					 // third of a second, so a single frame of a capture can sit
					 // either side of the one it was aimed at, and the two look
					 // the same in a still.
					 // '!deaf' rather than nothing, for the shake's reason -
					 // switched off and nothing having gone off are two zeroes.
					 + $"  blast {_grove?.Waves ?? 0}w/"
					 + $"{(_grove is { Planted: > 0 } g ? g.Trees.Max(t => Math.Abs(t.Flinch * t.Rise)) : 0.0f):F1}px"
					 + (_grove is null || _grove.Blast > 0.0 ? "" : "!deaf")
					 // '!off' rather than nothing, because a still camera and a
					 // switched-off one are the same two zeroes - the marker the
					 // recoil spring and the tracer already carry.
					 + $"  ruts {_marks?.Count ?? 0}@{_marks?.WidthOf(Active) ?? 0.0:F1}px"
					 + (_marks?.Enabled == true ? "" : "!off")
					 + $"  shake {_shake.ScreenOffset(_camera.Zoom.X)}"
					 + (_shakeOn ? "" : "!off"));
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

			// After the order has moved it, for the belts' reason: the lean is
			// about the ground this tank is on now, and read a frame early it
			// would still be about the cell it just left.
			UpdateLean(v, delta);

			// after the order has moved the tank, so the belts see the ground
			// that actually went past this frame rather than last frame's
			(double Left, double Right) belts = BeltTravel(v, delta);
			UpdateTracks(v, belts, delta);
			_marks?.Lay(v, belts, delta);
			UpdateTremble(v, delta);
			UpdateExhaust(v, delta);
			// Before the burn, so the densities it writes are this frame's.
			UpdateWreck(v, delta);
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

		// After every tank has moved, so a round aimed at a tank sees where that
		// tank got to this frame rather than last frame - the same ordering the
		// belts and the audio already need, and for the same reason.
		AdvanceShells(delta);

		// Same ordering argument, one step further out: which cells are occupied
		// is a fact about where the tanks ended up, so the wood clears for the
		// cell a tank is entering rather than the one it left. Live cells rather
		// than reached ones - a tank spends most of an order between two, and
		// the trees have to be out of the way for the crossing, not after it.
		_grove?.Reveal(Standing(), delta);
		// The hulls shoulder the wood aside, before Blow integrates what they
		// and the blast between them asked for. Every tank rather than the
		// driven one, for the reason its gun is: a tank left under orders goes
		// on driving after you have selected somebody else.
		if (_grove is not null)
			foreach (Vehicle vehicle in _vehicles)
			{
				Vector2 now = vehicle.GroundPoint - _origin;
				_grove.Brush(now, now - vehicle.LastGroundPoint, delta);
				vehicle.LastGroundPoint = now;
			}
		_grove?.Blow(delta);

		// The view last of all, after everything that could have fired this
		// frame: a shot has to reach the spring on the frame it goes off, or the
		// jolt lands one frame behind its own flash.
		//
		// Written whenever the spring is moving *or* the camera is displaced, so
		// that switching the shake off mid-ring-down puts the view back rather
		// than leaving it parked a few pixels out - the trap the recoil spring
		// already documents.
		if (_shakeOn)
			_shake.Update(delta);
		else if (_shake.Moving)
			_shake.Reset();
		Vector2 want = _shakeOn ? _shake.ScreenOffset(_camera.Zoom.X)
								: Vector2.Zero;
		if (_camera.Offset != want)
			_camera.Offset = want;

		// The stage after both: it mirrors the camera and follows every tank, so
		// anything written to either past this point would be a frame late on
		// the board and on time on the tanks - which reads as the hill sliding
		// under them.
		if (_stage is not null)
		{
			_stage.Selected = _noPanel ? null : Active;
			// Every frame rather than on the drag, for the tremble level's reason:
			// a stage handed the board later would otherwise sit on the built-in
			// while the row it is a view of says something else.
			_stage.SwellPeriod = (float)_swellPeriod;
			_stage.SwellBlend = _swellBlend;
			_stage.Foam = _foam;
			_stage.Deep = _deepWater;
			_stage.CastShadows = _castShadows;
			_stage.Trail = _wake;
			_stage.Bows = _bows;
			// Before Place, because the tint a wading tank wears is read off its
			// own cell's state and Place is where that is pushed at the sprite.
			if (_sea is not null)
			{
				_sea.Enabled = _seaReacts;
				// Every tank noted before anything is stepped: the chop is "is
				// anybody in this cell now", which is not known until they all
				// have been. Indexed by place in the list rather than by the
				// object, because the swell has no business knowing what a tank
				// is - see Swell.Note.
				for (int i = 0; i < _vehicles.Count; i++)
				{
					// Less the origin, like every other read of a ground point
					// against the board: the point is in the tanks' space and
					// the field answers in its own. Left off, every tank stirs
					// a cell it is nowhere near, and the pond looks like a pond
					// that answers nobody.
					// The point itself as well as the cell it falls in, and in the
					// tanks' own space: the cell is what the water reacts to, the
					// point is where the foam sits. Not the origin-less one - the
					// surface is built in this space too, so the two agree.
					_sea.Note(i, _field.CellAt(_vehicles[i].GroundPoint - _origin),
							  _vehicles[i].Speed > Swell.StirAbove,
							  _vehicles[i].GroundPoint);
					// And the trail, off the same point and the same threshold.
					// In water rather than in a flooded cell: what a wake needs
					// is a surface to be left on, which is the same question the
					// waterline already answers.
					_wake?.Note(i, _vehicles[i].GroundPoint,
								_vehicles[i].Speed > Wake.DriveAbove,
								_field.IsWater(_field.CellAt(
									_vehicles[i].GroundPoint - _origin)));
				}
				_sea.Tick(delta);
				_wake?.Tick(delta);
			}
			_stage.Place(_vehicles);
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

	/// <summary>
	/// How far the pointer may travel between pressing the middle button and
	/// letting it go and still count as a click rather than a pan.
	///
	/// In screen pixels, not world ones: the same hand movement is eight times
	/// the world distance at zoom 8, and whether somebody tapped or dragged is
	/// about the hand.
	/// </summary>
	private const float MiddleTapSlack = 4.0f;

	/// <summary>Whether a middle press that ended at <paramref name="to"/> was a
	/// tap. Split out so the one thing overloading this button risks - a pan that
	/// destroys a tank on the way past - is asserted rather than assumed.</summary>
	internal static bool MiddleTap(Vector2 from, Vector2 to) =>
		from.DistanceTo(to) <= MiddleTapSlack;

	/// <summary>Where the middle button went down, or null if it is up.</summary>
	private Vector2? _middleFrom;

	public override void _UnhandledInput(InputEvent @event)
	{
		// The middle button does two things, and the gesture decides which: a
		// drag pans, a tap destroys whatever is standing on the cell. Sharing it
		// rather than taking another key because there is no key left worth
		// taking, and because destroying a tank for a test wants to be aimed at
		// one - which is a thing the mouse can say and the keyboard cannot.
		//
		// Resolved on release, since that is the earliest moment the two are
		// distinguishable. The pan itself is unchanged and still runs on motion,
		// so a drag pans all the way and then declines to kill anything.
		if (@event is InputEventMouseButton
			{ ButtonIndex: MouseButton.Middle } middle)
		{
			if (middle.Pressed)
			{
				_middleFrom = GetGlobalMousePosition();
				return;
			}
			Vector2? from = _middleFrom;
			_middleFrom = null;
			if (from is not Vector2 down || !MiddleTap(down, GetGlobalMousePosition())
				|| _vehicles.Count == 0 || _tank.Atlas is null)
				return;
			Vector2I at = _field.ClampCell(
				_field.CellAt(_field.ToLocal(GetGlobalMousePosition())));
			// By cell, like the left and right buttons: the cell is the unit the
			// game is played in, and a silhouette overhangs its own hex, so
			// picking by pixels would let a click on the ground beside a tank
			// destroy it.
			if (Vehicle.At(_vehicles, at) is Vehicle mark)
			{
				Kill(mark);
				_panel?.Sync();
			}
			return;
		}
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
			case Key.F: _tank.TurretHoldsHeading = !_tank.TurretHoldsHeading; break;
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
			case Key.G: BoardShown = !BoardShown; break;
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
		// Before the tanks are repaired, not after: a round still in the air would
		// land on armour that had just been made good, which is the one way this
		// reset could leave a mark behind it.
		ClearShells();
		_marks?.Clear();
		// With the shells and for the same reason: a front still crossing would
		// arrive at a wood on a board that had just been put back, and the
		// flinch is the one thing in the grove that holds a pose with nothing
		// driving it. The wind is not reset - it is weather, and it was blowing
		// before the key was pressed.
		_grove?.Calm();
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
			s.TrembleYaw = 0.0;
			v.Exhaust.Reset();
			s.ExhaustPhase = -1;
			v.TrackLeft.Reset();
			v.TrackRight.Reset();
			s.TrackPhaseLeft = -1;
			s.TrackPhaseRight = -1;
			s.TrackBlurLeft = 0.0;
			s.TrackBlurRight = 0.0;
			// Before the fire is put out, because a wreck is what keeps
			// relighting it - see UpdateWreck.
			v.Wreck.Reset();
			s.Wrecked = false;
			s.Char = 0.0;
			s.FireDensity = 1.0f;
			s.SmokeDensity = 1.0f;
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
		// With the other levels, and it is one line rather than one per tank
		// because the traverse knob is not held on a machine - see
		// Gunnery.TraverseLevel.
		Gunnery.TraverseLevel = 1.0;
		Shell.SpeedLevel = 1.0;
		Shell.TracerLevel = 1.0;
		Shell.SmokeLevel = 1.0;
		Shell.SmokeOn = Shell.SmokeOnByDefault;
		Shell.SmokeSeconds = Shell.TunedSmokeSeconds;
		ApplySize();
		PaintGunnery();
		_camera.Zoom = new Vector2(_zoomAt, _zoomAt);
		_camera.Position = ViewHome;
		// and the shake, which holds a displacement with nothing driving it -
		// the same reason the recoil spring is reset rather than left leaning
		_shake.Reset();
		_camera.Offset = Vector2.Zero;
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
