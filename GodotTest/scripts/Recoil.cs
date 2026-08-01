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

    /// <summary>Angular velocity a shot injects. Peaks around 0.045 with the
    /// spring above - half again the driving pitch, which is right for a gun
    /// against a ramp, and still inside the amplitude that reads as a rigid
    /// body rather than as jelly.</summary>
    public double Impulse = 6.2;

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
        _pitchRate -= Impulse * Math.Cos(rad);
        _rollRate -= Impulse * Math.Sin(rad);
    }

    public void Update(double delta)
    {
        _pitchRate += (-Stiffness * Pitch - Damping * _pitchRate) * delta;
        _rollRate += (-Stiffness * Roll - Damping * _rollRate) * delta;
        Pitch += _pitchRate * delta;
        Roll += _rollRate * delta;
    }

    public bool Moving => Math.Abs(Pitch) > 1e-5 || Math.Abs(Roll) > 1e-5
                          || Math.Abs(_pitchRate) > 1e-4 || Math.Abs(_rollRate) > 1e-4;

    public void Reset()
    {
        Pitch = Roll = _pitchRate = _rollRate = 0.0;
    }
}
