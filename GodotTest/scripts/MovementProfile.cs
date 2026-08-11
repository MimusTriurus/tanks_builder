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
    /// Where this class sits between the others - 0 light, 1 medium, 2 heavy.
    ///
    /// The only thing in this table that is not a quantity, and it is here rather
    /// than in a gunnery table for the reason <see cref="Size"/> is: a second
    /// table keyed by the same tag is a second thing to keep in agreement, and it
    /// disagrees the first time a class is added to one and not the other.
    ///
    /// Ordinal on purpose. Nothing reads it as a number of anything - it answers
    /// "does this gun out-class that armour", which is a comparison, and every
    /// other figure here already carries the magnitude.
    /// </summary>
    public int Rank { get; init; } = 1;

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
        Size = 0.85, TurretRate = 240.0, ReloadTime = 2.2, Rank = 0,
        ShotShake = 3.0, TracerCalibre = 0.80, SmokeCalibre = 0.70,
    };

    public static readonly MovementProfile Medium = new()
    {
        Tag = "MTP", TopSpeed = 240.0, Accel = 420.0, TurnRate = 200.0,
        Size = 1.00, TurretRate = 175.0, ReloadTime = 3.2, Rank = 1,
        ShotShake = 4.5, TracerCalibre = 1.00, SmokeCalibre = 1.00,
    };

    public static readonly MovementProfile Heavy = new()
    {
        Tag = "HTP", TopSpeed = 175.0, Accel = 260.0, TurnRate = 140.0,
        Size = 1.15, TurretRate = 120.0, ReloadTime = 4.8, Rank = 2,
        ShotShake = 7.0, TracerCalibre = 1.35, SmokeCalibre = 1.45,
    };

    /// <summary>One profile per class, and the tags are the parts-built tanks
    /// because those are the only tanks now. The single-mesh HT/MT/LT are gone
    /// from the harness - their belts do not wind - and the profiles were always
    /// about the class rather than about how the scene was cut, so retagging them
    /// is the whole of that change here.</summary>
    public static readonly MovementProfile[] All = { Light, Medium, Heavy };

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
