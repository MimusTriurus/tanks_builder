using System;

namespace TankSpriteTest;

/// <summary>
/// How a tank class moves. Light, medium and heavy drove identically before
/// this, which threw away the cheapest source of character the harness has.
///
/// Acceleration separates the classes far more legibly than top speed does. A
/// viewer cannot judge 240 px/s against 310 without something to compare it
/// with, but "leaps away" against "takes a moment to gather itself" reads
/// immediately, and it reads on every single order rather than only on long
/// runs.
///
/// The ramps are deliberately slow. The first version used 900 px/s^2 for
/// everything, which reaches full speed in a quarter second and reads as an
/// electric car, not as forty tonnes. Half a second and up is where mass starts
/// to show. It also makes both movement effects legible: the pitch is driven by
/// acceleration, and the rumble scales with speed, so a longer ramp gives them
/// both more to work with.
/// </summary>
public sealed class MovementProfile
{
	public string Tag { get; init; } = "MTP";

	/// <summary>Cruise speed, px/s on screen.</summary>
	public double TopSpeed { get; init; } = 240.0;

	/// <summary>Used for both acceleration and braking, px/s^2.</summary>
	public double Accel { get; init; } = 420.0;

	/// <summary>Hull yaw rate, deg/s.</summary>
	public double TurnRate { get; init; } = 200.0;

	/// <summary>
	/// How big this class is drawn, against the medium.
	///
	/// Authored, and it has to be: the models carry no relative scale whatever.
	/// The generator normalises each one to about unit size, so all three hulls
	/// render within 3% of the same length (see <see cref="AtlasSet.HullSpan"/>)
	/// and the heavy's turning circle is the smallest of the three in world
	/// units. There is no measurement to recover a real size from - a light tank
	/// is smaller than a heavy one because we say so, or not at all.
	///
	/// It lives on the class profile rather than in a table of its own for the
	/// reason the parts-built tanks were listed here rather than left to the
	/// fallback: a second table keyed by the same tag is a second thing to keep
	/// in agreement, and it disagrees the first time a class is added to one and
	/// not the other. Size is not movement, but it is per class, and per class is
	/// what this table is.
	///
	/// The medium is 1.00 by definition, so it is the tank every other number is
	/// read against and the one whose atlas is what the renderer produced.
	/// </summary>
	public double Size { get; init; } = 1.0;

	/// <summary>
	/// Speed kept while pivoting at a bend, as a fraction of TopSpeed.
	///
	/// Not zero. Tracked vehicles do turn on the spot, but coming to a dead
	/// halt at every corner of a winding path reads as the pathing snagging
	/// rather than as a vehicle. Creeping through the turn keeps it continuous;
	/// the fraction stays small so the sideways drift while the sprite swings
	/// round is only a dozen pixels or so.
	///
	/// A standing start still pivots in place: the crawl is a floor to slow
	/// down to, never a speed to accelerate up to.
	/// </summary>
	public double CornerFraction { get; init; } = 0.12;

	public double CornerSpeed => TopSpeed * CornerFraction;

	/// <summary>
	/// Speed allowed while going up or down a step, as a fraction of TopSpeed.
	///
	/// <b>One number for all three classes, and it is deliberately not per class.</b>
	/// Every other figure in this table is a property of the tank; this one is a
	/// property of the hill, and the hill is the same hill under all three. Spread
	/// it by class and the board would be telling three different stories about how
	/// steep it is - the same objection that keeps the ramp's own grade a single
	/// board constant. What differs between the classes is what two thirds of their
	/// cruise comes to (207 / 160 / 117 px/s), and that is the spread already
	/// chosen.
	///
	/// <b>A flag, not a curve.</b> The board carries exactly one grade
	/// (<see cref="HexField.StepGrade"/>), so a factor proportional to steepness
	/// would be a rule with one value in it - indistinguishable from a constant and
	/// pretending to knowledge the board does not have. Whether the leg changes
	/// height is the whole of the question; see <see cref="HexField.IsGrade"/>.
	///
	/// Both ways, and the slip to guard against is capping only the climb. A
	/// descent is the harder one to drive: a tank rides its brakes down a bank
	/// rather than freewheeling, and one that dropped off the crown at cruise while
	/// crawling up it would read as falling rather than as driving.
	///
	/// Two thirds is above <see cref="CornerFraction"/> by a wide margin, which is
	/// what keeps the two from fighting: a bend on a slope still slows to the
	/// crawl, and the crawl is still a floor rather than a target.
	/// </summary>
	public const double GradeFraction = 2.0 / 3.0;

	public double GradeSpeed => TopSpeed * GradeFraction;

	/// <summary>
	/// Speed allowed while wading, as a fraction of TopSpeed.
	///
	/// <b>One number for all three classes, by <see cref="GradeFraction"/>'s
	/// argument word for word</b>: everything else in this table is a property of
	/// the tank, and this is a property of the water, and the water is the same
	/// water under all three. The spread is already there in what 45% of three
	/// different cruises comes to.
	///
	/// <b>Below the grade's two thirds, and that is the whole of what makes a ford
	/// a ford.</b> Equal to it and water would be a hill that happens to be blue:
	/// two terrains with one cost is one terrain. Wading is the slower of the two
	/// because a tank in water is pushing it out of the way the whole time, where a
	/// bank is over in a cell.
	///
	/// Still well above <see cref="CornerFraction"/>, which is what keeps the two
	/// from fighting - a bend in the water slows to the crawl, and the crawl stays
	/// a floor rather than a target.
	/// </summary>
	public const double WaterFraction = 0.45;

	public double WaterSpeed => TopSpeed * WaterFraction;

	/// <summary>
	/// Speed allowed on a leg with deep water at either end, as a fraction of
	/// TopSpeed - a swimming hull, not a wading one.
	///
	/// <b>Below the ford's, and by the same argument that put the ford below the
	/// grade:</b> a hull wading is still on its tracks and pushes the water aside;
	/// a hull afloat has nothing under its tracks at all and is moved by what
	/// little the belts and the wake do against the water. Two waters at one
	/// cost would be one water with two colours. Still above the corner's crawl,
	/// for <see cref="WaterFraction"/>'s reason. Taken by <see cref="TankTick.SpeedCap"/>
	/// as the lower of it and everything else on the leg, never a product -
	/// docs/swim-plan.md, step 4.
	/// </summary>
	public const double SwimFraction = 0.30;

	public double SwimSpeed => TopSpeed * SwimFraction;

	/// <summary>
	/// Speed allowed while shoving masonry, in px/s and the same figure for
	/// every class.
	///
	/// <b>A speed and not a fraction, which is where this parts company with the
	/// grade and the ford.</b> Those two are fractions because what they cost is
	/// power against weight, so a stronger machine really does carry more of its
	/// cruise through them. Masonry is not going anywhere until it is broken, so
	/// what limits the tank is the wall giving way, and that is one figure
	/// whoever is leaning on it. As a fraction it came to 68 / 53 / 39 px/s, and
	/// that spread was the ram's rather than the wall's.
	///
	/// <b>And it was the whole of the scatter.</b> Measured on the ring, same
	/// leaf and same count let go: the light tank arrived fastest and put the
	/// pile 12.74 cells out against the heavy's 2.67 - in that order, which is
	/// the order of the three caps. What the spread showed was not the classes
	/// but how fast each of them happened to hit.
	///
	/// <b>The slowest going on the board, and it has to be.</b> A bank is over in
	/// a cell and water is pushed aside; a wall is broken, and until it is broken
	/// it does not move at all. Equal to the ford and masonry would be water that
	/// happens to be brown, which is <see cref="WaterFraction"/>'s own objection
	/// arriving a third time.
	///
	/// <b>Below <see cref="CornerFraction"/> at two classes of three, and the
	/// two still cannot fight.</b> The crawl is a floor in the branch that turns
	/// and nowhere else - <c>TankTick.AdvanceOrder</c> - and a hull swinging on
	/// the spot is not ramming, which is the whole of <c>WallRig.Advance</c>. On
	/// the straight the cap is honoured on its own, so a tank that comes out of a
	/// bend at the crawl slows into the wall's figure at <see cref="WallBrake"/>
	/// instead of being lifted to the crawl by it. What the fraction had to
	/// promise, the branch gives for nothing.
	///
	/// <b>What it does not do is stop the tank.</b> The wall gives no other
	/// feedback than this: how far the machine gets and where it parks are still
	/// decided by the order, and driving through masonry is a ceiling on the way
	/// rather than a wall to bounce off. That debt is named in the bench's notes
	/// and this is not it - this is the going, and the going is heavy.
	/// </summary>
	public const double WallSpeed = 48.0;

	/// <summary>
	/// How much harder masonry takes speed off than the engine's own brakes,
	/// as a multiple of <see cref="Accel"/> - and it applies to the slowing
	/// only, never to getting the speed back.
	///
	/// <b>The one exception to "no faster than the engine can", and a collision
	/// is what earns it.</b> Every other ceiling on this board is terrain, and
	/// terrain is arrived at rather than hit: a bank appears under the tracks and
	/// the tank eases onto it, so closing on the cap at anything but its own
	/// retardation would be a stop dead in one frame - eleven times what the
	/// engine can do, and it is that objection that put the ramp in
	/// <see cref="TankTick.AdvanceOrder"/> in the first place. A wall is the
	/// thing that <i>can</i> take speed off faster than brakes, because it is not
	/// slowing the tank down, it is being hit.
	///
	/// <b>Without it the cap is nearly ornamental at speed, and that is measured
	/// rather than argued.</b> The band a nose presses through is 1.5m, which a
	/// hull at cruise crosses in a sixth of a second; at 420px/s/s that is 56px/s
	/// off 240 and the wall is already behind. Three times that reaches
	/// <see cref="WallSpeed"/> inside the same sixth of a second, so the tank
	/// arrives at the far side of a leaf doing a fifth of its cruise instead of
	/// three quarters.
	///
	/// <b>One-sided on purpose.</b> Coming out the other side is the engine's
	/// work and goes at the engine's rate, which is what makes the shape read as
	/// pushing through something rather than as a dip in the road: a hard edge
	/// going in, half a second of pulling away coming out.
	///
	/// Still not a stop in one frame: three times 420px/s/s is 21px per frame at
	/// 60Hz, so the whole of a cruise takes nine frames to go. What the pitch
	/// sees is a full nose-down, which is what ramming a wall should look like -
	/// the ratio is clamped, so this cannot drive the body harder than a full
	/// brake already does.
	/// </summary>
	public const double WallBrake = 3.0;

	/// <summary>
	/// How hard this gun hits: the GDD's firepower ordinal, I..V written 1..5.
	/// Light 1, medium 2, heavy 3, destroyer 4, mortar 5.
	///
	/// <b>This was one field called <c>Rank</c>, and the two classes that arrived
	/// last are what split it in half.</b> Across three classes the gun and the
	/// hull said the same thing - the heavy has both the heaviest gun and the
	/// heaviest plate - so a single ordinal answered the shell's question and the
	/// ram's at once. The destroyer and the mortar are exactly the pair where
	/// that stops being true: a IV rides on a medium's hull and a V on a light's.
	/// One number could only ever have kept one of those two tables right.
	///
	/// Ordinal on purpose, and still is: nothing reads it as a number of
	/// anything - it answers "does this gun out-class that plate", which is a
	/// comparison. Where the magnitude turns into a level the scar layer actually
	/// carries is <see cref="Gunnery.Penetration"/>, and it is capped there.
	///
	/// Here rather than in a gunnery table for the reason <see cref="Size"/> is:
	/// a second table keyed by the same tag is a second thing to keep in
	/// agreement, and it disagrees the first time a class is added to one and not
	/// the other.
	/// </summary>
	public int Might { get; init; } = 2;

	/// <summary>
	/// What this hull weighs, and by the same number what its front plate is
	/// worth: the GDD's mass ordinal, I..III written 1..3. Light 1, medium 2,
	/// heavy 3, destroyer 2, mortar 1.
	///
	/// <b>One field for two of the GDD's columns, because they are the same
	/// column.</b> Mass reads I/II/III/II/I across the five classes and front
	/// armour reads I/II/III/II/I - not two tables that happen to agree but one
	/// statement made twice: how much tank there is. Held apart they would be two
	/// things to keep in step with nothing keeping them.
	///
	/// It answers two questions and only two: what a shell has to out-class
	/// (<see cref="Gunnery.Penetration"/>) and who may shove whom
	/// (<see cref="Gunnery.RamLevel"/>).
	/// </summary>
	public int Mass { get; init; } = 2;

	/// <summary>
	/// Whether driving through masonry is a step rather than a ram - the GDD's
	/// "Бульдозер HT" (classes.md): for the heavy the edge with a wall on it is
	/// passable at the cost of an ordinary step, the wall is destroyed by the
	/// crossing, and the action is not spent.
	///
	/// <b>Read off <see cref="Mass"/> rather than off the tag</b>, for the
	/// reason <see cref="Mass"/> itself is one field: a table keyed by the class
	/// name is a second thing to keep in step, and it disagrees the first time a
	/// class is added to one and not the other. What the rule is about is how
	/// much tank there is, and III is the only one of the five that is enough.
	/// </summary>
	public bool Bulldozes => Mass >= 3;

	/// <summary>
	/// Whether its rounds go over what is in the way instead of through it -
	/// the GDD's "HM — Навес" (classes.md): the mortar's bomb "приходит сверху,
	/// на любой уровень, минуя всё, что стоит между стрелком и гексом".
	///
	/// <b>Read off <see cref="Might"/> rather than off the tag</b>, for
	/// <see cref="Bulldozes"/>'s reason word for word - and the table answers it
	/// exactly once: a V is the mortar's and nothing else in the game carries
	/// one. What the rule is about is the gun, and this is the gun.
	///
	/// A verb of the class and never a mode: there is no switch anywhere that
	/// makes a tank gun lob or a mortar shoot flat, because the GDD has no such
	/// unit. See <see cref="Shell.Arc"/>, which is the picture of it.
	/// </summary>
	public bool Lobs => Might >= 5;

	/// <summary>
	/// Whether the gun sits in a ring or in the hull.
	///
	/// <b>False is most of what a destroyer and a mortar are.</b> Both carry the
	/// gun in the hull, so laying it turns the whole tank - which on the GDD
	/// costs steps out of the same budget as driving, and is the destroyer's
	/// price for a IV and the mortar's for a V. Here it is spent in degrees per
	/// second off <see cref="TurnRate"/> instead: see <c>Main.UpdateAttack</c>.
	///
	/// <b>It is a fact about the picture before it is a fact about the rules.</b>
	/// The renderer knows nothing of casemates - it spins the turret layer about
	/// the ring axis on every set, because that is what a turret layer is - so a
	/// casemate handed to the harness unflagged rotates on the spot, which is a
	/// vehicle the game does not have. This is what stops it:
	/// <see cref="TankSprite.Turreted"/> welds the layer to the hull, and every
	/// write to <see cref="TankSprite.TurretFacing"/> then goes nowhere.
	/// </summary>
	public bool Turreted { get; init; } = true;

	/// <summary>
	/// How deep the hull sits when it swims, as a fraction of its deck's height
	/// (<see cref="AtlasSet.DeckHeightPx"/> - the roof of the hull's own
	/// silhouette, turret and gun left out by the height map's design; on a
	/// casemate that roof is the fighting compartment's).
	///
	/// <b>Authored, like <see cref="Size"/>, and for the same reason:</b> nothing
	/// measurable says how a model floats, and the one thing the picture has to
	/// do is keep the upper hull dry - deck, turret ring, casemate roof - with the
	/// water on the upper sides. <b>Of the deck and not of the height range</b>,
	/// because the range runs to the highest point on the hull and the deck is
	/// not it: on the medium the deck lies at three quarters of the range, so a
	/// draught named in the range put the line a hand above the tracks at six
	/// tenths and over the deck at 0.85, with nothing in between that read as
	/// swimming. Named against the deck, 0.95 was the line at the deck's edge
	/// on every turreted class alike, the turret dry - and it read as an
	/// outline: a waterline measured by height that stands exactly at the deck's
	/// rim runs along the rim, the glacis' edge and the fenders, so the collar
	/// traced the silhouette rather than crossing it (the user's screenshot,
	/// docs/swim-plan.md step 10). <b>Three quarters</b> puts the line across
	/// the flat upper sides, where a level reads as a level, with the deck, the
	/// ring and the top of the sides dry. <b>The casemates' deck is the
	/// hull's</b>, measured: the compartment is narrow, so most columns' roof is
	/// the hull deck under it (TDP 49px of a 100px range, HMP 64 of 102), and
	/// 0.7 of that puts the water on the hull's upper sides with the compartment
	/// well out. Both figures are to be judged by eye on EventsTank and moved;
	/// the bench's dial (<see cref="TankTick.DraughtScale"/>) is the way to try a
	/// value before it is written here.</summary>
	/// <remarks>What the water actually reaches is the lesser of this and the
	/// pond's drawn depth - see <see cref="TankTick.Buoyancy"/>: with the bed
	/// drawn at its level the events pond is 39px deep and a medium wants more,
	/// which is why the bed is drawn under the level (<see cref="HexField.DeepBed"/>).
	///
	/// Read by <see cref="TankTick.Buoyancy"/>, where it becomes a height above
	/// the bed - or the bed itself, when the pond is shallower than the draught
	/// and the hull stands on the bottom as it does in a ford. docs/swim-plan.md,
	/// step 2.</remarks>
	public double Draught { get; init; } = 0.75;

	/// <summary>
	/// Turret traverse, deg/s. Separate from <see cref="TurnRate"/> because a
	/// turret and a hull are two different machines, and it is the one that
	/// decides how an engagement feels: the hull rate is spent on a corner
	/// nobody is watching, the traverse is spent while a target is on screen
	/// waiting to be shot at.
	///
	/// Ordered the way everything else here is - the light swings fastest - and
	/// the spread from light to heavy is 2.0x, so a rear target still costs the
	/// heavy about twice what it costs the light. What the gun can be laid on is
	/// quantised to 15 degrees by the atlas, so this only decides *when* the
	/// picture jumps; it is written as a rate because that is what stays right
	/// the day the turret is rendered finer, which is the same argument
	/// <see cref="TurretScan"/> makes.
	///
	/// **These were 95 / 70 / 48 and read as sluggish, and the number that says
	/// why is the ratio against the hull, not the rate itself.** At those
	/// figures the turret was 2.7-2.9x slower than its own hull on every class,
	/// so spinning the tank was the quicker way to point the gun - which is
	/// backwards for a turreted vehicle, and it is what the eye was reporting.
	/// At 240 / 175 / 120 the two are comparable (0.92 / 0.88 / 0.86 of the hull
	/// rate) and neither is obviously the way round to do it.
	///
	/// Not raised past the hull rate, which was the other candidate. The hull
	/// rates here are arcade-fast in absolute terms - 260 deg/s is a full spin
	/// in 1.4s - so "the turret must beat the hull" is a claim about this
	/// harness's hull numbers rather than about tanks, and matching them is as
	/// far as that reasoning carries. Past it the traverse stops costing
	/// anything at all and the heavy's reload becomes the only thing that makes
	/// it heavy while standing still.
	///
	/// The exact figure is meant to be argued with, which is what
	/// <see cref="Gunnery.TraverseLevel"/> is for: 0.4x on the slider is the old
	/// feel, and the caption under it prints the swing against the hull's.
	///
	/// <b>Zero on a class with <see cref="Turreted"/> false, and zero rather than
	/// a plausible figure.</b> There is no ring on a casemate, so any number here
	/// would be a rate for a mechanism that is not on the vehicle - and the two
	/// readers say so honestly when it is nought: the panel prints "no ring" in
	/// place of a swing time, and the traverse motor never opens its gate because
	/// the ring's offset from the hull cannot change. What lays those guns is
	/// <see cref="TurnRate"/>.
	/// </summary>
	public double TurretRate { get; init; } = 175.0;

	/// <summary>
	/// How far the view jolts when this gun fires, in screen pixels of peak
	/// displacement at zoom 1. See <see cref="CameraShake"/>.
	///
	/// In this table rather than in a table of its own, by the rule the size
	/// followed here first: a second table keyed on the class is a second thing
	/// to keep in agreement, and it comes apart the day a class is added to one
	/// and forgotten in the other.
	///
	/// It is the one number that makes the heavy's gun feel like a heavy's gun
	/// while it is standing still - the reload does that too, but by absence.
	/// The spread is wider than the reload's on purpose: 2.3x from light to
	/// heavy against 2.2x, but a jolt is judged against nothing and a reload
	/// against a clock, so the jolt needs the room.
	/// </summary>
	public double ShotShake { get; init; } = 4.5;

	/// <summary>
	/// Seconds between rounds.
	///
	/// Longer than the gun's own report on every class - 1.2s on the light and
	/// 3.4s on the heavy, measured off the staged samples - because a tank that
	/// fires over the tail of its last shot sounds like two tanks. That is the
	/// floor; the spread above it is class character, and it is the one figure
	/// that makes a heavy feel heavy while standing still.
	/// </summary>
	public double ReloadTime { get; init; } = 3.2;

	/// <summary>
	/// How big this gun's tracer is drawn, medium being one.
	///
	/// In this table for the reason <see cref="ShotShake"/> and <see cref="Size"/>
	/// are: a second table keyed on the class is a second thing to keep in
	/// agreement, and it comes apart the day a class is added to one and
	/// forgotten in the other.
	///
	/// **Not the hit calibre dial, and deliberately not multiplied by it.** That
	/// dial is the harness's control over what an *arriving* round looks like and
	/// it snaps to three values of its own; letting it size the tracer as well
	/// would make one control move two things, so every A/B taken on it would be
	/// measuring both. This is the gun, not the round.
	///
	/// 0.80 / 1.00 / 1.35, a 1.7x spread - wider than the size spread of 1.35x,
	/// because a tracer is a few pixels of bright on a field of grass and the
	/// difference has to survive being small. **The bottom is held up rather than
	/// spread down** for that same reason: a light gun's head at 0.60 would be
	/// under a pixel, and a tracer that has to be looked for is not one.
	///
	/// Settable, unlike everything else here, because
	/// <see cref="ClassConfig"/> may override it from a file and the profiles are
	/// singletons - a setter is how a file reaches them. The same goes for
	/// <see cref="SmokeCalibre"/>, and for nothing else in this table.
	/// </summary>
	public double TracerCalibre { get; internal set; } = 1.0;

	/// <summary>
	/// How thick the smoke trail behind this gun's round is drawn, medium being
	/// one. See <see cref="Shell.SmokeSize"/>.
	///
	/// **Split from <see cref="TracerCalibre"/> because the two are judged against
	/// different things, and one number could only ever be right for one of
	/// them.** The tracer is two pixels of bright: its spread is compressed at the
	/// bottom to keep the light gun's head visible at all. The trail is a soft
	/// line three to eighteen pixels across, which has room the head does not - so
	/// it spreads wider (0.70 / 1.00 / 1.45, 2.07x against the tracer's 1.7x) and
	/// the class shows in the smoke more than in the streak.
	///
	/// It sizes the whole trail - the puff, its wobble across the path and the
	/// spacing along it (see <see cref="Shell.Step"/>) - so what differs between
	/// the classes is how wide the trail is and nothing else. Sizing the puff
	/// alone left the light's 2.1px puffs 4px apart, which is a dotted line
	/// rather than a thin one.
	/// </summary>
	public double SmokeCalibre { get; internal set; } = 1.0;

	/// <summary>Seconds from rest to cruise - the number that actually carries
	/// the class difference.</summary>
	public double RampTime => TopSpeed / Accel;

	/// <summary>Distance covered getting up to cruise, px. Worth knowing
	/// against the grid: a row step is about 107px, so a single-cell move
	/// spends most of itself ramping and never reaches TopSpeed at all.</summary>
	public double RampDistance => TopSpeed * TopSpeed / (2.0 * Accel);

	/// <summary>The light. It is the one where the class matters most to what the
	/// belts do: 310 px/s against a 7.58px link is the worst case for the track
	/// limiter in the whole set.</summary>
	public static readonly MovementProfile Light = new()
	{
		Tag = "LTP", TopSpeed = 310.0, Accel = 620.0, TurnRate = 260.0,
		Size = 0.85, TurretRate = 240.0, ReloadTime = 2.2,
		Might = 1, Mass = 1,
		ShotShake = 3.0, TracerCalibre = 0.80, SmokeCalibre = 0.70,
	};

	public static readonly MovementProfile Medium = new()
	{
		Tag = "MTP", TopSpeed = 240.0, Accel = 420.0, TurnRate = 200.0,
		Size = 1.00, TurretRate = 175.0, ReloadTime = 3.2,
		Might = 2, Mass = 2,
		ShotShake = 4.5, TracerCalibre = 1.00, SmokeCalibre = 1.00,
	};

	public static readonly MovementProfile Heavy = new()
	{
		Tag = "HTP", TopSpeed = 175.0, Accel = 260.0, TurnRate = 140.0,
		Size = 1.10, TurretRate = 120.0, ReloadTime = 4.8,
		Might = 3, Mass = 3,
		ShotShake = 7.0, TracerCalibre = 1.35, SmokeCalibre = 1.45,
	};

	/// <summary>
	/// The destroyer. <b>A medium's hull carrying a gun that out-classes the
	/// heavy's, with no ring to put it in.</b>
	///
	/// That is the GDD's own sentence about this class - "MT and TD differ only
	/// in the turret" - and it is why every driving figure here is the medium's
	/// to the digit: cruise, ramp, hull rate and mass are all copied rather than
	/// invented. The two of them standing side by side on the home row is then a
	/// clean A/B of the one thing that does differ, which is what the bench is
	/// for. Its price for the IV is that laying the gun turns the tank.
	///
	/// <b><see cref="Size"/> is the one figure that is not the medium's, and it
	/// is not meant to be.</b> The GDD's sentence covers speed, armour and mass;
	/// what a class is drawn at answers to nothing in the rules and is authored
	/// for legibility, so a IV on a medium hull is free to read as a big machine.
	/// 1.15x, the largest of the five and over the heavy's 1.10, which means the
	/// pair on the home row is no longer an A/B of size: it is one of where the
	/// gun sits.
	///
	/// <b>That is this class's own figure and not a rule about guns.</b> The row
	/// is authored class by class and is not ordered by the gun - the mortar
	/// out-guns this one and is drawn under the heavy. What <i>is</i> ordered by
	/// might is the gun's own row, <see cref="TracerCalibre"/> and
	/// <see cref="SmokeCalibre"/>, and that is where the claim is asserted.
	///
	/// <b>The reload does not carry that price a second time.</b> 5.0s continues
	/// the ordering the other three set (2.2 / 3.2 / 4.8 up the might), and it
	/// stops there rather than being stretched to punish a IV: the cost of this
	/// gun is already on screen as a hull swinging round, and charging it twice
	/// would leave a class that does almost nothing per minute.
	/// </summary>
	public static readonly MovementProfile Destroyer = new()
	{
		Tag = "TDP", TopSpeed = 240.0, Accel = 420.0, TurnRate = 200.0,
		Size = 1.15, TurretRate = 0.0, ReloadTime = 5.0,
		Might = 4, Mass = 2, Turreted = false, Draught = 0.7,
		ShotShake = 8.5, TracerCalibre = 1.55, SmokeCalibre = 1.70,
	};

	/// <summary>
	/// The mortar. <b>Three steps a turn like the heavy, on a light hull.</b>
	///
	/// So the cruise is the heavy's and everything that follows from mass is the
	/// light's: it gathers itself in half a second (175 over 330 is 0.53s against
	/// the heavy's 0.67 and the light's 0.50), swings its hull faster than a
	/// medium. "Slow along the ground and quick about its own axis" is a character
	/// none of the other four have, and it falls straight out of the GDD's two
	/// columns rather than being reached for.
	///
	/// <b>Drawn at 1.05x - a touch over the medium, under the heavy, and it
	/// out-guns both.</b> That is a decision and not a drift. See
	/// <see cref="Size"/>: the figure is authored per class and answers to nothing
	/// in the rules, so the drawn row is not the gun's order and is not asked to
	/// be - a light hull goes on reading as a light hull on the board. What
	/// carries the V is the gun's own row, which <i>is</i> ordered by might and
	/// asserted so: <see cref="TracerCalibre"/> 1.75, <see cref="SmokeCalibre"/>
	/// 2.05 and <see cref="ShotShake"/> 10.0 - the widest streak, the fattest
	/// trail and the hardest kick of the five.
	///
	/// A V goes through anything in the game and any hit in its flank finishes
	/// it, which on this bench is the one table read both ways rather than a rule
	/// of its own. What the bench does <i>not</i> show is the half that makes it
	/// a mortar: indirect fire over cover, and a minimum range of three. Both are
	/// board rules with no picture in them, so they stay in the GDD.
	/// </summary>
	public static readonly MovementProfile Mortar = new()
	{
		Tag = "HMP", TopSpeed = 175.0, Accel = 330.0, TurnRate = 230.0,
		Size = 1.05, TurretRate = 0.0, ReloadTime = 6.5,
		Might = 5, Mass = 1, Turreted = false, Draught = 0.7,
		ShotShake = 10.0, TracerCalibre = 1.75, SmokeCalibre = 2.05,
	};

	/// <summary>One profile per class, and the tags are the parts-built tanks
	/// because those are the only tanks now. The single-mesh HT/MT/LT are gone
	/// from the harness - their belts do not wind - and the profiles were always
	/// about the class rather than about how the scene was cut, so retagging them
	/// is the whole of that change here.
	///
	/// <b>In the GDD's own order, LT MT HT TD HM</b>, because this list is read
	/// as an order in three places that have nothing else in common: the number
	/// keys, the class dropdowns, and <see cref="Parking.Pair"/>, which hands the
	/// first tag to the first parking a board declares. Sorting it by anything
	/// else would move tanks about the board as a side effect.</summary>
	public static readonly MovementProfile[] All =
		{ Light, Medium, Heavy, Destroyer, Mortar };

	/// <summary>The tanks to look for on disk, in key order.
	///
	/// <b>Derived from the profiles rather than written beside them, and it used
	/// to be written on <see cref="Main"/>.</b> Two lists of the same three
	/// strings is two places a fourth class has to be remembered in, and the
	/// bench that shows one tank at a time was reaching into the harness for the
	/// list of which tanks there are - which is a question about the classes, not
	/// about the board they stand on.
	///
	/// One per class, all three built from separate parts - hull, turret, barrel,
	/// engine and two belts as their own meshes. The single-mesh HT/MT/LT used to
	/// sit alongside them so a winding belt could be judged against a still one.
	/// They are gone: their belts are not separate meshes, so they cannot wind at
	/// all, and a tank that slides on dead tracks is not a comparison, it is the
	/// old bug still on screen. The atlases stay on disk under Sprites/; nothing
	/// loads them.</summary>
	public static readonly string[] Tags = Array.ConvertAll(All, p => p.Tag);

	/// <summary>Profile for an atlas tag, medium for anything unrecognised.</summary>
	public static MovementProfile For(string tag)
	{
		foreach (MovementProfile profile in All)
			if (string.Equals(profile.Tag, tag, StringComparison.OrdinalIgnoreCase))
				return profile;
		return Medium;
	}

	/// <summary>
	/// How hard the engine is working, 0 at rest and 1 at cruise.
	///
	/// One definition because there are now three consumers and there were
	/// already two copies of it, character for character, private to
	/// <see cref="EngineTremble"/> and <see cref="ExhaustLoop"/>. They agree
	/// today; the point is that nothing was keeping them agreeing, and this is
	/// the ramp every effect that says "the engine is under load" hangs off - the
	/// tremble's amplitude and frequency, the plume's rate and density, and the
	/// engine note.
	///
	/// Per class rather than absolute, so a heavy at its own cruise is working as
	/// hard as a light at its. Signed speed folded, because reversing is work.
	/// </summary>
	public static double LoadAt(double speed, double topSpeed) =>
		Math.Clamp(Math.Abs(speed) / Math.Max(topSpeed, 1e-6), 0.0, 1.0);
}
