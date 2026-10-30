using System;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The kick of firing: the hull rocking back on its suspension.
///
/// An impulse, not a target. The driving pitch chases a level that acceleration
/// sets and holds; a shot is over in a millisecond and what is left is a body
/// already moving. So this injects angular velocity into the spring and lets it
/// ring down, which is the same spring maths reaching its peak on its own terms
/// rather than being told where to go.
///
/// It is stiffer and less damped than the driving pitch, because it is a
/// different event: the drive ramp is half a second of steady push and wants to
/// settle without ringing, while a shot wants a snap and one clear rebound.
///
/// Two axes, because the gun is not always pointing where the hull is. The
/// recoil pushes back along the *gun*, so a turret traversed ninety degrees
/// rocks the tank sideways and not at all backwards. Split by the angle between
/// them and the existing shear takes both at once.
/// </summary>
public sealed class Recoil
{
    public double Stiffness = 1150.0;
    public double Damping = 38.0;

    /// <summary>The amplitude past which the hull has stopped reading as a rigid
    /// thing on suspension and started reading as rubber. Named here rather than
    /// left as a number in the self-test because the panel prints it beside the
    /// level slider: a setting that leaves the rigid-body range should be
    /// visible before it is fired, not afterwards.</summary>
    public const double RigidBodyPeak = 0.06;

    /// <summary>
    /// Whether the harness shears the sprite on a shot at all. False.
    ///
    /// Named here beside <see cref="RigidBodyPeak"/> and for the same reason: it
    /// is one fact two places need - the harness reads it as the default for its
    /// switch, and the self-test asserts it - and a default that lives only in a
    /// field initialiser is a default nothing can notice being flipped back.
    ///
    /// **This is the one effect in the bench that is off because something else
    /// replaced it**, rather than because it is an exception the way a burning
    /// tank is. The class below fakes a hull that pitched by shearing a flat
    /// sprite about a pivot, which needs the vertical term damped to a quarter to
    /// stop a rigid hull reading as rubber, and cannot express the second term of
    /// a rotation at all because the sprite has no depth. The gun tube's recoil
    /// has none of those problems - it is geometry, rendered from twelve angles.
    /// Left on together, every shot would show one true movement and one invented
    /// one, with the invented one the larger of the two.
    ///
    /// The spring stays because "the rendered one is better" is an assertion until
    /// the two can be put side by side, which is the painted flash sheet's
    /// argument exactly.
    /// </summary>
    public const bool ShearOnByDefault = false;

    /// <summary>Angular velocity a shot injects. Peaks around 0.045 with the
    /// spring above - half again the driving pitch, which is right for a gun
    /// against a ramp, and still inside the amplitude that reads as a rigid
    /// body rather than as jelly.</summary>
    public double Impulse = 6.2;

    /// <summary>
    /// A multiplier on that impulse, and the one number the panel puts on a
    /// slider.
    ///
    /// Read at the moment of firing rather than applied to the running spring,
    /// which is where it belongs: the kick is delivered once and everything
    /// after it is a spring ringing down on its own. Turning the knob while the
    /// hull is still rocking does not reach back into a shot that has already
    /// gone off - and a knob that did would be measuring itself on the next
    /// frame rather than the shot.
    ///
    /// Goes past 1 for the same reason the tremble's does. The tuned value is
    /// half again the driving pitch, which is right for a gun against a ramp;
    /// double it and the hull leaves the rigid-body amplitude and starts to read
    /// as rubber, and being able to see that is the point of the range.
    /// </summary>
    public double Level = 1.0;

    public double Pitch { get; private set; }
    public double Roll { get; private set; }

    private double _pitchRate;
    private double _rollRate;

    /// <summary>
    /// Fire, with the turret <paramref name="turretRelative"/> degrees off the
    /// hull heading.
    ///
    /// Signs are taken from what the shear actually does, not from prose: pitch
    /// displaces the top of the hull along <c>GroundDirection(HullFacing)</c>
    /// and roll along <c>GroundDirection(HullFacing + 90)</c>, which is the
    /// hull's left. Recoil drives the top of the tank *backwards along the gun*,
    /// so it lands at the gun angle plus 180 - hence both terms negative.
    /// </summary>
    public void Fire(double turretRelative)
    {
        double rad = Mathf.DegToRad(turretRelative);
        _pitchRate -= Impulse * Level * Math.Cos(rad);
        _rollRate -= Impulse * Level * Math.Sin(rad);
    }

    public void Update(double delta)
    {
        _pitchRate += (-Stiffness * Pitch - Damping * _pitchRate) * delta;
        _rollRate += (-Stiffness * Roll - Damping * _rollRate) * delta;
        Pitch += _pitchRate * delta;
        Roll += _rollRate * delta;
    }

    /// <summary>
    /// What a shot at <paramref name="level"/> would peak at, in the same units
    /// as <see cref="Pitch"/>.
    ///
    /// Run through the same integrator at the same step rather than solved on
    /// paper. The closed form for an impulse into this spring says 0.0945 at
    /// level 1; a semi-implicit Euler step of 1/60 against omega = 33.9 says
    /// 0.0397, which is what the screen does. Two and a half times apart, so a
    /// caption quoting the analytic figure would call the tuned setting
    /// dangerous and the dangerous one fine.
    /// </summary>
    public double PeakFor(double level)
    {
        const double step = 1.0 / 60.0;
        double rate = -Impulse * level, angle = 0.0, peak = 0.0;
        for (int i = 0; i < 60; i++)
        {
            rate += (-Stiffness * angle - Damping * rate) * step;
            angle += rate * step;
            peak = Math.Max(peak, Math.Abs(angle));
        }
        return peak;
    }

    public bool Moving => Math.Abs(Pitch) > 1e-5 || Math.Abs(Roll) > 1e-5
                          || Math.Abs(_pitchRate) > 1e-4 || Math.Abs(_rollRate) > 1e-4;

    public void Reset()
    {
        Pitch = Roll = _pitchRate = _rollRate = 0.0;
    }
}
