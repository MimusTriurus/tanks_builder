using System;

namespace TankSpriteTest;

/// <summary>
/// Body pitch as a damped spring driven by acceleration - the nose lifting on
/// a start and dipping on a stop.
///
/// Pitch follows acceleration, not speed, so it only exists if the tank has a
/// speed ramp at all: with instant start and stop there is nothing to drive.
/// The spring is deliberately underdamped. Overshoot is what reads as weight;
/// a critically damped version just leans and looks like a slope.
///
/// Kept free of Godot types so the self-test can run the whole start-stop cycle
/// without a scene - none of this is visible in a still frame.
/// </summary>
public sealed class BodyPitch
{
    /// <summary>Shear amplitude. Positive is nose down.</summary>
    public double Angle { get; private set; }

    /// <summary>Amplitude at sustained full acceleration. 0.030 moves the roof
    /// of a 68px-tall hull by about 2px. 0.055 was tried first and read as
    /// jelly: past roughly 3px the eye stops seeing a heavy thing shifting on
    /// its suspension and starts seeing the sprite itself deform.</summary>
    public double Gain = 0.030;

    /// <summary>
    /// zeta = Damping / (2*sqrt(Stiffness)) = 0.65 and omega = 21.9 rad/s, so
    /// the body overshoots by about 7% and is back under a tenth of its
    /// amplitude in 0.16s. One visible dip, then still.
    ///
    /// Both ends of this were wrong first. 260/16 is zeta = 0.50 at omega =
    /// 16.1: it rings for half a second at nearly double the amplitude, and
    /// that ringing is most of what "jelly" actually is. Overcorrecting to
    /// 340/30 (zeta = 0.81) killed it entirely - the overshoot peak then lands
    /// at 0.29s, later than the 0.25s the acceleration lasts, so the body never
    /// reaches it and merely leans, which looks like a parked slope.
    /// Fast and slightly springy beats slow, in both directions.
    /// </summary>
    public double Stiffness = 480.0;
    public double Damping = 28.5;

    private double _velocity;

    /// <summary><paramref name="accelRatio"/> is acceleration over its maximum,
    /// so +1 is flat out and -1 is full braking.</summary>
    public void Update(double accelRatio, double delta)
    {
        if (delta <= 0.0)
            return;
        double target = -Gain * Math.Clamp(accelRatio, -1.0, 1.0);
        _velocity += (-Stiffness * (Angle - target) - Damping * _velocity) * delta;
        Angle += _velocity * delta;
    }

    public void Reset()
    {
        Angle = 0.0;
        _velocity = 0.0;
    }
}
