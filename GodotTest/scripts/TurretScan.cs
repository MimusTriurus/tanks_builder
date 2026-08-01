using System;

namespace TankSpriteTest;

/// <summary>
/// Idle turret traverse: the gunner searching while the tank sits.
///
/// The asked-for version of this was a sway of a few degrees. That cannot be
/// drawn. The turret layer holds twelve frames, one every 30 degrees, so the
/// smallest turret motion the atlas can express *is* 30 degrees: a 3 degree sway
/// shows nothing at all until it happens to cross a frame boundary, at which
/// point it shows the full 30. Faking it with a shear does not rescue it either
/// - a yaw displaces a point by an amount set by its distance from the axis
/// along the *ground*, screen y mixes that distance with height above the ring,
/// and at this elevation the resulting error works out about the same size as
/// the 2px effect it would be adding. A real few-degree sway needs frames at a
/// few degrees; nothing else gets there.
///
/// So this scans in whole frame steps and admits it. Dwell, traverse to a new
/// bearing, dwell again, staying within a couple of steps of where it started so
/// it searches an arc rather than wandering off. On this atlas each leg reads as
/// one snap; the traverse rate only decides when the snap lands. It is written
/// as a rate anyway because that is the part that stays true if the turret is
/// ever rendered at finer steps, and then the same code sweeps smoothly.
/// </summary>
public sealed class TurretScan
{
    /// <summary>Degrees per second. Slow: a searching traverse, not a snap onto
    /// a target.</summary>
    public double SlewRate = 14.0;

    public double DwellMin = 1.2;
    public double DwellMax = 3.6;

    /// <summary>How far either side of the rest bearing the scan may get, in
    /// frame steps. Two is 60 degrees on this atlas - a sector, not a
    /// pirouette.</summary>
    public int MaxSteps = 2;

    /// <summary>Degrees away from where the scan started. Bounded by
    /// <see cref="MaxSteps"/>, so the turret cannot creep round over time -
    /// which it would, since each leg is chosen at random.</summary>
    public double Offset { get; private set; }

    private double _target;
    private double _dwell;
    private long _leg;

    /// <summary>Degrees to turn the turret this frame. Returned rather than
    /// applied so the caller keeps the one path through which turret heading
    /// changes.</summary>
    public double Advance(double stepDegrees, double delta)
    {
        if (_dwell > 0.0)
        {
            _dwell -= delta;
            return 0.0;
        }

        double gap = _target - Offset;
        if (Math.Abs(gap) < 1e-9)
        {
            _dwell = DwellMin + (DwellMax - DwellMin) * Unit(_leg, 0UL);
            _target = NextTarget(stepDegrees);
            _leg++;
            return 0.0;
        }

        double move = Math.Sign(gap) * Math.Min(Math.Abs(gap), SlewRate * delta);
        Offset += move;
        return move;
    }

    /// <summary>A bearing a whole number of frame steps from the rest bearing,
    /// never the one it is already on. Landing on a step matters: it means the
    /// turret always comes to rest on a bearing that was actually rendered,
    /// instead of parking mid-frame where the drawn heading and the real one
    /// differ by up to half a step.</summary>
    private double NextTarget(double stepDegrees)
    {
        int current = (int)Math.Round(Offset / stepDegrees);
        int span = 2 * MaxSteps;                      // steps other than `current`
        var pick = (int)(Unit(_leg, 0x9E3779B97F4A7C15UL) * span);
        pick = Math.Min(pick, span - 1);
        int chosen = -MaxSteps + pick;
        if (chosen >= current)
            chosen++;                                  // skip over `current`
        return Math.Clamp(chosen, -MaxSteps, MaxSteps) * stepDegrees;
    }

    public void Reset()
    {
        Offset = 0.0;
        _target = 0.0;
        _dwell = 0.0;      // first Advance picks a leg and dwells once, not twice
        _leg = 0;
    }

    /// <summary>A value in [0, 1) for leg number <paramref name="index"/>.
    /// Hashed rather than random so a trace or a screenshot comparison replays
    /// the same scan.</summary>
    private static double Unit(long index, ulong salt)
    {
        unchecked
        {
            var x = ((ulong)index ^ salt) * 0x9E3779B97F4A7C15UL;
            x ^= x >> 29;
            x *= 0xBF58476D1CE4E5B9UL;
            x ^= x >> 32;
            return (double)(x & 0xFFFFFF) / 0x1000000;
        }
    }
}
