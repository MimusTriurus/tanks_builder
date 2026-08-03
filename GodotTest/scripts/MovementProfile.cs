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
    public string Tag { get; init; } = "MT";

    /// <summary>Cruise speed, px/s on screen.</summary>
    public double TopSpeed { get; init; } = 240.0;

    /// <summary>Used for both acceleration and braking, px/s^2.</summary>
    public double Accel { get; init; } = 420.0;

    /// <summary>Hull yaw rate, deg/s.</summary>
    public double TurnRate { get; init; } = 200.0;

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

    public static readonly MovementProfile Light = new()
    {
        Tag = "LT", TopSpeed = 310.0, Accel = 620.0, TurnRate = 260.0,
    };

    public static readonly MovementProfile Medium = new()
    {
        Tag = "MT", TopSpeed = 240.0, Accel = 420.0, TurnRate = 200.0,
    };

    public static readonly MovementProfile Heavy = new()
    {
        Tag = "HT", TopSpeed = 175.0, Accel = 260.0, TurnRate = 140.0,
    };

    /// <summary>The parts-built medium. Same vehicle, so the same numbers -
    /// listed rather than left to the fallback so that changing what an
    /// unrecognised tag gets cannot silently change how this one drives.</summary>
    public static readonly MovementProfile MediumParts = new()
    {
        Tag = "MTP", TopSpeed = 240.0, Accel = 420.0, TurnRate = 200.0,
    };

    /// <summary>The parts-built heavy. Same vehicle as HT, so the same numbers,
    /// and listed for the same reason MTP is.</summary>
    public static readonly MovementProfile HeavyParts = new()
    {
        Tag = "HTP", TopSpeed = 175.0, Accel = 260.0, TurnRate = 140.0,
    };

    public static readonly MovementProfile[] All =
        { Light, Medium, Heavy, MediumParts, HeavyParts };

    /// <summary>Profile for an atlas tag, medium for anything unrecognised.</summary>
    public static MovementProfile For(string tag)
    {
        foreach (MovementProfile profile in All)
            if (string.Equals(profile.Tag, tag, StringComparison.OrdinalIgnoreCase))
                return profile;
        return Medium;
    }
}
