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
        Size = 0.85,
    };

    public static readonly MovementProfile Medium = new()
    {
        Tag = "MTP", TopSpeed = 240.0, Accel = 420.0, TurnRate = 200.0,
        Size = 1.00,
    };

    public static readonly MovementProfile Heavy = new()
    {
        Tag = "HTP", TopSpeed = 175.0, Accel = 260.0, TurnRate = 140.0,
        Size = 1.15,
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
