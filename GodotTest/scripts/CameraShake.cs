using System;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The view jolting when a gun goes off.
///
/// An impulse into a spring, not a curve over time, for <see cref="Recoil"/>'s
/// reason and it is the same event: the blast is over in a millisecond and what
/// is left is a camera already moving. Stiffer and less damped than the hull's
/// spring - about 8Hz at a quarter damping - so it reads as a jolt with two or
/// three shrinking rebounds rather than as a pendulum.
///
/// Three things about it are decisions rather than tuning.
///
/// It moves in whole screen pixels
/// -------------------------------
/// <see cref="BodyRumble"/> already found that a fractional offset resamples a
/// sprite and turns motion into shimmer. Here it is worse by the size of the
/// board: the camera's offset moves *everything* - three tanks, the field, the
/// route marks - so half a pixel of camera is half a pixel of blur on every
/// object on screen at once. So the offset is snapped, and snapped in *screen*
/// pixels rather than world units, because zoom is between the two and the
/// bench zooms.
///
/// It has a direction, and takes it from the gun
/// ----------------------------------------------
/// Random jitter would have been the obvious shake and it throws away the one
/// thing this event knows: which way the gun was pointing. The kick goes along
/// the shot's own ground direction, taken **unnormalised** from the atlas, so a
/// gun fired across the screen jolts the view further than one fired into it -
/// which is the projection doing the work, the same trick the muzzle flash uses
/// to keep from growing a full-length plume when it fires away from the camera.
///
/// At two to five pixels a noise field and a decaying spring are hard to tell
/// apart anyway, and the spring is the one that is the same in two runs - which
/// `--capture` requires and a random one could not give.
///
/// It is off by default
/// ---------------------
/// Not because it is wrong, and not because it was superseded the way the
/// hull's shear was. Because it moves the whole frame, and nearly every
/// measurement in this bench is a pixel diff between two captures: with the
/// shake on, an A/B taken anywhere near a shot measures the shake instead of
/// whatever it was aimed at. Named in <see cref="OnByDefault"/> rather than left
/// in a field initialiser, for the reason that constant's neighbour gives.
///
/// What it deliberately does not have: any falloff with distance from the
/// camera. All three tanks are on screen at once and at much the same distance,
/// so a falloff would be a parameter with nothing on screen to judge it by. It
/// is the first thing to add on the day the camera follows one tank.
/// </summary>
public sealed class CameraShake
{
    /// <summary>Whether the harness shakes the camera on a shot at all. False -
    /// see the class note. One fact two places need: the harness reads it as
    /// the default for its switch and the self-test asserts it.</summary>
    public const bool OnByDefault = false;

    /// <summary>About 8Hz. Fast enough to read as a jolt, slow enough that a
    /// 1/60 step still has seven or eight samples in a cycle - past about 12Hz
    /// the integrator is describing a wave the screen cannot show, which is the
    /// ceiling <see cref="EngineTremble"/> ran into from the other side.</summary>
    public double Stiffness = 2500.0;

    /// <summary>A quarter of critical. Under-damped on purpose: a shot wants a
    /// snap and a couple of clear rebounds, where the drive ramp wants to settle
    /// without ringing at all.</summary>
    public double Damping = 25.0;

    /// <summary>A multiplier over the per-class amplitude, and the one number
    /// the panel puts on a slider.
    ///
    /// Read at the moment of firing, like <see cref="Recoil.Level"/> and for the
    /// same reason: the kick is delivered once and the rest is a spring ringing
    /// down. A knob that reached back into a shot already fired would be
    /// measuring itself rather than the shot.</summary>
    public double Level = 1.0;

    private Vector2 _offset;
    private Vector2 _rate;

    /// <summary>Where the view is displaced to, in world units, unsnapped. The
    /// snapping belongs with the zoom - see <see cref="ScreenOffset"/>.</summary>
    public Vector2 Offset => _offset;

    /// <summary>
    /// The peak displacement a unit impulse produces, in the same units the
    /// impulse is given in.
    ///
    /// Run through the same integrator at the same step rather than solved on
    /// paper, which is <see cref="Recoil.PeakFor"/>'s lesson: the closed form
    /// and a 1/60 semi-implicit Euler step disagree by enough that a caption
    /// quoting the analytic figure would describe a different effect. Dividing
    /// by it is what lets <see cref="Fire"/> be asked for pixels and deliver
    /// pixels.
    /// </summary>
    public double UnitPeak
    {
        get
        {
            const double step = 1.0 / 60.0;
            double rate = 1.0, x = 0.0, peak = 0.0;
            for (int i = 0; i < 120; i++)
            {
                rate += (-Stiffness * x - Damping * rate) * step;
                x += rate * step;
                peak = Math.Max(peak, Math.Abs(x));
            }
            return peak;
        }
    }

    /// <summary>
    /// A gun goes off along <paramref name="along"/>, wanting a peak of
    /// <paramref name="pixels"/> at <see cref="Level"/> 1.
    ///
    /// <paramref name="along"/> is the atlas's ground direction and must arrive
    /// **unnormalised**: its length is how much of a horizontal thing survived
    /// the projection, so a shot into the screen delivers half the jolt of one
    /// across it without anything here having to know about elevation.
    ///
    /// Added to whatever the spring is already doing rather than replacing it,
    /// so a second gun firing during the first one's ring-down compounds. Three
    /// tanks in an engagement is the case, and a shake that restarted would read
    /// as the earlier shot being cancelled by the later one.
    /// </summary>
    public void Fire(Vector2 along, double pixels)
    {
        double peak = UnitPeak;
        if (peak <= 0.0)
            return;
        _rate += along * (float)(pixels * Level / peak);
    }

    public void Update(double delta)
    {
        _rate += (-(float)Stiffness * _offset - (float)Damping * _rate)
                 * (float)delta;
        _offset += _rate * (float)delta;
    }

    /// <summary>
    /// The offset to hand the camera, snapped so that it lands on whole screen
    /// pixels at this <paramref name="zoom"/>.
    ///
    /// Snapped here rather than in <see cref="Update"/> so the spring stays
    /// continuous: quantising the state would let a small kick round to zero
    /// every step and never move at all.
    /// </summary>
    public Vector2 ScreenOffset(float zoom)
    {
        if (zoom <= 0.0f)
            return Vector2.Zero;
        return new Vector2(Mathf.Round(_offset.X * zoom) / zoom,
                           Mathf.Round(_offset.Y * zoom) / zoom);
    }

    public bool Moving => _offset.Length() > 1e-4f || _rate.Length() > 1e-3f;

    public void Reset()
    {
        _offset = Vector2.Zero;
        _rate = Vector2.Zero;
    }
}
