using System;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// What a slope does to the drawn body: an image rotation, sprung.
///
/// <b>Why a rotation and not a shear.</b> The ground direction of a hex heading
/// is <c>(cos h, -sin h)</c>, so the pitch axis - horizontal and across the
/// travel - is <c>(sin h, cos h)</c>, and its component along the view is
/// <c>cos(h)*cos(e)</c>. That single factor decides the whole of it. On four of
/// the six headings it is 0.75 and the pitch is very nearly an image rotation.
/// On the other two - straight up and down the screen - it is zero, and the
/// pitch is pure foreshortening, which <b>no transform of a flat sprite
/// reaches</b>: the hull's ground extent wants sin(e+t)/sin(e) = 1.39 along a
/// screen direction where the turret's height wants cos(e+t)/cos(e) = 0.83.
/// Affine gives one direction one factor; projective adds a keystone an
/// orthographic camera never produces. What is missing is depth per pixel.
///
/// So this draws the part that is a rigid motion and leaves the rest to
/// <see cref="TankSprite.Rise"/> - which is the answer that came out of trying:
/// a shear past a few pixels reads as the sprite deforming, because nothing on a
/// tank shears, and an anisotropic scale reads as jelly for the same reason. A
/// rotation keeps every length the silhouette had, so the only thing limiting it
/// is whether the pitch is real, and fourteen degrees of it is.
///
/// <b>Sprung, and the spring is the whole of why this is a class.</b> Applied
/// straight, the tilt arrives in one frame at the start of a leg and leaves in
/// one frame at the end - a body that snaps to an angle and snaps back has not
/// climbed anything. Same idiom as <see cref="BodyPitch"/>, stiffer and better
/// damped: omega 12, zeta 0.9, so it arrives over the climb and does not ring
/// afterwards.
/// </summary>
public sealed class ClimbLean
{
    /// <summary>omega squared and 2*zeta*omega, for omega 12 and zeta 0.9. Named
    /// as the two coefficients rather than as the two ratios because that is how
    /// they are used, and the ratios are in the remarks where the reasoning
    /// is.</summary>
    public double Stiffness = 144.0;
    public double Damping = 21.6;

    /// <summary>The image rotation, in radians. Zero on level ground, which is
    /// what makes this cost nothing on a board without relief.</summary>
    public double Angle { get; private set; }

    private double _rate;

    /// <summary>
    /// What the slope is worth as a rotation, with no spring in it.
    ///
    /// Negated because a climb is nose <i>up</i>: the travel direction is where
    /// the nose is, and Godot's positive rotation carries +x down the screen, so
    /// the sign that reads as climbing is the one that reads as diving in the
    /// arithmetic.
    /// </summary>
    /// <param name="grade">Rise over run along the travel - positive climbing.
    /// </param>
    /// <param name="heading">Hull heading in degrees.</param>
    /// <param name="rise">cos(elevation), which is
    /// <see cref="HexField.RiseFactor"/>. Passed rather than assumed, so the one
    /// place the camera angle is measured stays the tile.</param>
    public static double Target(double grade, double heading, double rise) =>
        -Math.Atan(grade) * Math.Cos(Mathf.DegToRad(heading)) * rise;

    public void Update(double grade, double heading, double rise, double delta)
    {
        double target = Target(grade, heading, rise);
        _rate += (-Stiffness * (Angle - target) - Damping * _rate) * delta;
        Angle += _rate * delta;
    }

    public void Reset()
    {
        Angle = 0.0;
        _rate = 0.0;
    }
}
