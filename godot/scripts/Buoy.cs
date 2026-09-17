using System;

namespace TankSpriteTest;

/// <summary>
/// The bob of a floating hull: a damped spring on its ride, in screen px, at
/// rest on the height <see cref="TankTick.Afloat"/> says and kicked off it when
/// the hull drops into the water.
///
/// <b>Slow, and that is what tells it from the suspension.</b> <see cref="BodyPitch"/>
/// rings at 22 rad/s because a tank on its springs settles in a fifth of a second;
/// a hull in water is held up by the water, and water is slow - a period near a
/// second and a half, two dips visible and a third felt, still in about two
/// seconds. zeta = Damping / (2*sqrt(Stiffness)) = 0.35 at omega = 4 rad/s. The
/// same shape as the pitch spring so the two read alike, and its own class
/// because the two have nothing to share but the arithmetic. docs/swim-plan.md,
/// step 3.
/// </summary>
public sealed class Buoy
{
    /// <summary>How far off the rest height the hull rides, in screen px.
    /// Positive is up.</summary>
    public double Offset { get; private set; }

    public double Stiffness = 16.0;
    public double Damping = 2.8;

    private double _velocity;

    /// <summary>
    /// The swell under a hull that is simply afloat: a slow heave that never
    /// stops, in screen px, on top of the spring. The spring says how the
    /// hull arrives; this says that it is on water at all - a hull lying dead
    /// still on a pond read as parked on a blue floor (the user's note, step
    /// 10 of docs/swim-plan.md). Small, because a pond is not a sea:
    /// <see cref="SwellAmplitude"/> px either way over <see cref="SwellPeriod"/>
    /// seconds, and each hull at its own <see cref="Phase"/> so a fleet does
    /// not rise and fall as one.
    /// </summary>
    public double Idle => SwellAmplitude * Math.Sin(2.0 * Math.PI * Time / SwellPeriod + Phase);

    public double SwellAmplitude = 1.6;
    public double SwellPeriod = 2.6;

    /// <summary>Where in its swell this hull is, radians - set once from the
    /// vehicle's seed (<see cref="Fleet.Crew"/>), so two hulls put on the pond
    /// together are not in step.</summary>
    public double Phase;

    /// <summary>The swell's own clock, seconds since the hull was built. Runs
    /// on land too, which costs nothing and means a hull driving into the pond
    /// meets the swell mid-wave rather than at its zero.</summary>
    public double Time { get; private set; }

    /// <summary>Whether the spring has anything left to do.</summary>
    public bool Moving => Math.Abs(Offset) > 0.01 || Math.Abs(_velocity) > 0.01;

    /// <summary>Kick the hull: an impulse in px per second, negative is down -
    /// the way a hull that has just dropped into the pond is still going.</summary>
    public void Jolt(double velocity) => _velocity += velocity;

    public void Update(double delta)
    {
        Time += delta;
        if (!Moving)
        {
            Offset = 0.0;
            _velocity = 0.0;
            return;
        }
        _velocity += (-Stiffness * Offset - Damping * _velocity) * delta;
        Offset += _velocity * delta;
    }

    public void Reset()
    {
        Offset = 0.0;
        _velocity = 0.0;
    }
}
