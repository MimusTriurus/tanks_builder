using System;

namespace TankSpriteTest;

/// <summary>
/// Idle turret sway: the gunner searching while the tank sits.
///
/// **One frame either side of the bearing the turret was left on, and the
/// amplitude is the whole of what this is.** It cannot be less. The smallest
/// turret motion the atlas can express *is* one frame step - a sway of a few
/// degrees shows nothing at all until it happens to cross a frame boundary, at
/// which point it shows the full step - and faking the remainder with a shear
/// does not rescue it: a yaw displaces a point by its distance from the axis
/// along the *ground*, screen y mixes that distance with height above the ring,
/// and at this elevation the error works out about the size of the effect it
/// would be adding.
///
/// What changed is the step, not the argument. At twelve headings this was 30
/// degrees and <see cref="MaxSteps"/> was 2, because one step read as a snap and
/// a sector needed two of them; at twenty-four it is 15, and one step either
/// side is a sway rather than a pirouette. So the number here follows the atlas
/// and the reason is written down: **raise it and the arc stops being a sway.**
///
/// One step also keeps the sway clear of the firing rule, and that is not luck
/// worth relying on silently. A gun fires only down a flat side of the hex, six
/// bearings 60 degrees apart; a swayed turret is off its base by 15 or 30, and
/// neither is a multiple of 60, so no leg of the sway can quietly lay the gun
/// down a lane it was not ordered to fire along. Asserted rather than assumed -
/// four steps of 15 would reach exactly one lane over.
///
/// Dwell, traverse, dwell again. The traverse rate only decides when the frame
/// change lands, but it is written as a rate because that is the part that stays
/// true if the turret is ever rendered finer still, and then the same code
/// sweeps smoothly.
///
/// The base is a bearing, and it is re-taken every time something else has had
/// the turret
/// -----------------------------------------------------------------------------
/// <see cref="Offset"/> is degrees away from a bearing this class never stored,
/// which was harmless while nothing else moved the turret and wrong the moment
/// something did: a tank that lays its gun down a lane, fires, and then goes
/// back to idling would carry the old offset into the new rest bearing and sway
/// around a point up to two steps off the lane. So the caller announces the base
/// with <see cref="Rest"/> when the sway resumes and <see cref="Suspend"/> when
/// anything takes the turret away, and the base is always a bearing that was
/// rendered - which costs nothing on screen, because the sprite is drawn at the
/// nearest rendered heading anyway.
/// </summary>
public sealed class TurretScan
{
    /// <summary>Degrees per second. Slow: a searching traverse, not a snap onto
    /// a target.</summary>
    public double SlewRate = 14.0;

    public double DwellMin = 1.2;
    public double DwellMax = 3.6;

    /// <summary>
    /// How much the traverse rate varies from leg to leg, as a fraction of
    /// <see cref="SlewRate"/>.
    ///
    /// The rate only decides when the frame change lands, which is exactly why
    /// varying it is worth anything: the whole sway is two snaps and a wait, so
    /// when the snap comes is most of what there is to see. A gunner does not
    /// traverse at one speed twice running.
    /// </summary>
    public double SlewJitter = 0.35;

    /// <summary>
    /// What makes this gunner not the same gunner as the one on the next tank.
    ///
    /// Everything here is hashed on the leg number so that a trace or a capture
    /// replays the same scan - and with nothing else in the hash, every tank on
    /// the board drew the same sequence of dwells and the same legs, from the
    /// same leg zero, at the same moment. Three turrets swaying in step read as
    /// one animation played three times, which is the objection the per-vehicle
    /// clocks were split up over in the first place: a shared
    /// <see cref="EngineTremble"/> would have three tanks shivering in time.
    ///
    /// It is a number the caller assigns, not one taken from the clock, because
    /// the replay is the point: same board, same frame, same picture.
    /// </summary>
    public ulong Seed;

    /// <summary>How far either side of the base bearing the sway may get, in
    /// frame steps. One - see the class note: at twenty-four headings that is 15
    /// degrees, and it is the smallest arc the atlas can draw at all.</summary>
    public int MaxSteps = 1;

    /// <summary>Degrees away from <see cref="Base"/>. Bounded by
    /// <see cref="MaxSteps"/>, so the turret cannot creep round over time -
    /// which it would, since each leg is chosen at random.</summary>
    public double Offset { get; private set; }

    /// <summary>The bearing the sway is measured from, in world degrees. Only
    /// meaningful while <see cref="Based"/>.</summary>
    public double Base { get; private set; }

    /// <summary>Whether the sway currently knows what it is swaying around.
    /// False after <see cref="Suspend"/> and before the first <see cref="Rest"/>
    /// - the caller must announce a base before <see cref="Advance"/> means
    /// anything.</summary>
    public bool Based { get; private set; }

    private double _target;
    private double _dwell;
    private long _leg;

    /// <summary>
    /// The turret has been left pointing at <paramref name="bearing"/> and the
    /// sway may start from there.
    ///
    /// The caller passes a bearing that was rendered; nothing here can work that
    /// out, since the frame step belongs to the atlas and the facing belongs to
    /// the sprite.
    /// </summary>
    public void Rest(double bearing)
    {
        Base = bearing;
        Based = true;
        Offset = 0.0;
        _target = 0.0;
        _dwell = 0.0;      // first Advance picks a leg and dwells once, not twice
        _leg = 0;
    }

    /// <summary>Something else is driving the turret. Idempotent, because the
    /// caller says this every frame the sway is gated off rather than tracking
    /// the edge.</summary>
    public void Suspend()
    {
        Based = false;
        Offset = 0.0;
        _target = 0.0;
        _dwell = 0.0;
        _leg = 0;
    }

    /// <summary>Degrees to turn the turret this frame. Returned rather than
    /// applied so the caller keeps the one path through which turret heading
    /// changes.</summary>
    public double Advance(double stepDegrees, double delta)
    {
        if (!Based)
            return 0.0;
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

        double move = Math.Sign(gap) * Math.Min(Math.Abs(gap), RateOn(_leg) * delta);
        Offset += move;
        return move;
    }

    /// <summary>The traverse rate for one leg. Hashed on the leg rather than
    /// rolled per frame: a rate that changed mid-traverse would be an
    /// acceleration, and what is wanted is that this swing is not the same swing
    /// as the last one. Never zero or negative, whatever the jitter is set
    /// to.</summary>
    public double RateOn(long leg) =>
        SlewRate * Math.Max(0.1,
            1.0 + Math.Clamp(SlewJitter, 0.0, 0.9) * (2.0 * Unit(leg, 0x2545F4914F6CDD1DUL) - 1.0));

    /// <summary>The fastest a leg can traverse. Named so "it traverses rather
    /// than snapping" can be asserted against what the jitter actually allows
    /// instead of against the nominal rate, which the jitter is meant to
    /// exceed.</summary>
    public double PeakRate => SlewRate * (1.0 + Math.Clamp(SlewJitter, 0.0, 0.9));

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

    /// <summary>The same thing as <see cref="Suspend"/>: forgetting the base is
    /// all there is to reset, and the turret is left pointing where it is
    /// pointing rather than swung home. Kept as its own name because the callers
    /// mean different things by it - the harness resets, the frame suspends.</summary>
    public void Reset() => Suspend();

    /// <summary>A value in [0, 1) for leg number <paramref name="index"/>.
    /// Hashed rather than random so a trace or a screenshot comparison replays
    /// the same scan - and hashed on <see cref="Seed"/> as well, so that
    /// replaying the same scan does not mean every tank replaying one
    /// scan.</summary>
    private double Unit(long index, ulong salt)
    {
        unchecked
        {
            var x = ((ulong)index ^ salt ^ (Seed * 0xD1342543DE82EF95UL))
                    * 0x9E3779B97F4A7C15UL;
            x ^= x >> 29;
            x *= 0xBF58476D1CE4E5B9UL;
            x ^= x >> 32;
            return (double)(x & 0xFFFFFF) / 0x1000000;
        }
    }
}

