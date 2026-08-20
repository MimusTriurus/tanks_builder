using System;

namespace TankSpriteTest;

/// <summary>
/// The jolt of driving over uneven ground: a whole-pixel vertical offset that
/// advances with distance travelled.
///
/// Three things make this read as ground rather than as a defect.
///
/// Phase comes from distance, not from time. Bumps are in the terrain, so a
/// tank at half speed should meet half as many per second. Driven off a clock
/// instead, the rate is the same at every speed and reads as an idling engine.
///
/// The offset lands on a whole pixel. A fractional offset resamples the sprite
/// every frame - the tank layer filters linearly, for reasons the pitch shear
/// needed - and that softens it into a shimmer. On whole pixels nothing is
/// resampled at all: the sprite is either where it was or one pixel off, which
/// is what a jolt looks like.
///
/// A whole pixel <em>of the buffer the sprite is rasterised into</em>, which is
/// not this class's business and used to be: see <see cref="Heave"/> and
/// <see cref="TankSprite.SnapHeave"/>. What is held here is the amplitude, and
/// it is held per bump - so the rounding downstream makes one decision per bump
/// as well.
///
/// Each bump is sampled once and held, rather than rounding a continuous wave.
/// That was the first attempt and it buzzes: a smooth signal crosses the
/// rounding threshold several times per cycle, so the picture changed every two
/// or three frames at 60fps regardless of how the wave was tuned, and constant
/// change reads as a rendering fault rather than as terrain. Sample-and-hold
/// makes exactly one decision per bump, so the jolt rate *is* the bump rate and
/// each offset sits still long enough to be seen.
/// </summary>
public sealed class BodyRumble
{
    /// <summary>Peak of the signal in pixels before rounding.
    ///
    /// 1.4 caps the offset at one pixel and that is invisible at 1:1 - one
    /// pixel on a tank nearly two hundred tall is half a percent, and "a one
    /// pixel jitter" is better read as the floor of the effect than as the
    /// target. 2.2 was legible but spent a third of its bumps at two pixels,
    /// which starts to read as hopping. 1.7 lands between: about three fifths
    /// of bumps at one pixel, a tenth at two, the rest level, so the jolts
    /// differ in size without any of them being a lurch.</summary>
    public double Amplitude = 1.7;

    /// <summary>Pixels of travel per bump. At full speed this is about 8 bumps
    /// a second, a handful per hex cell; at a crawl it is a slow judder.</summary>
    public double BumpSpacing = 30.0;

    /// <summary>Speed at which the jolt reaches full strength. Below it the
    /// signal shrinks, so rounding leaves more bumps at zero and the rumble
    /// thins out by itself rather than switching on.</summary>
    public double FullSpeed = 120.0;

    /// <summary>
    /// What is left of the jolt when the tank is barely moving.
    ///
    /// <b>Without it the crawl is not thin, it is empty.</b> The harness sets
    /// FullSpeed to half of cruise and a tank creeping through a bend does 0.12
    /// of cruise, so the gain was 0.24 and the signal 0.41px - and no sample can
    /// round that to a pixel, because the sample is in [-1, 1). Measured: 1200
    /// frames of crawling gave exactly nought on all three classes, and the check
    /// that says otherwise was one-sided and passed on nought.
    ///
    /// <b>The physics is on this side of the argument too.</b> How far a bump
    /// lifts the hull is the geometry of a track riding over an obstacle and
    /// hardly depends on speed; what grows with speed is the vertical
    /// acceleration, not the travel. And the rate already comes from the distance,
    /// so speed is in the effect twice over.
    ///
    /// <b>0.35 rather than something smaller, and that is arithmetic.</b> A jolt
    /// only shows once the signal can round to a pixel, which needs
    /// <c>gain > 0.5 / Amplitude</c> = 0.294: below that the floor does nothing
    /// whatever. So this is the first setting on which the lever works at all,
    /// and it puts about a sixth of a crawling tank's bumps at one pixel.
    /// </summary>
    public double GainFloor = 0.35;

    /// <summary>
    /// What is left of a bump when the ground under the tank is a pond bottom.
    ///
    /// <b>The ford was getting a nearly full ride, and it is the one place the
    /// jolt argues with something.</b> The harness caps a wading tank at 0.45 of
    /// cruise against a FullSpeed of half of it, so the gain came out 0.935 - and
    /// the waterline is cut per column from a table in the sprite's own tile
    /// space, which the heave moves the sprite <em>inside</em>: every jolt slid
    /// the wet edge a pixel or two along the hull. Damping the ride answers that
    /// and the ride at the same time, which is why it is done here rather than by
    /// handing the offset to the shader.
    ///
    /// One number for all three classes, by <see cref="MovementProfile.WaterFraction"/>'s
    /// argument: the water is the same water under all of them, and the spread is
    /// already carried by three different cruises.
    ///
    /// 0.35 leaves about a tenth of a wading tank's bumps showing one pixel,
    /// against two thirds of them on dry land at the same speed.
    /// </summary>
    public const double WetDamping = 0.35;

    /// <summary>What the ground is doing to the ride: 1 on dry land,
    /// <see cref="WetDamping"/> in a ford. Pushed in by the harness every frame,
    /// the way the tremble's level is, and read at the latch rather than live -
    /// so entering the water mid-bump finishes that bump dry, which is one bump
    /// and not a decision the class has to make twice.</summary>
    public double Damping = 1.0;

    /// <summary>Below this the tank counts as stopped and the body comes level.
    /// Not a threshold the effect switches on at - the strength is
    /// <see cref="FullSpeed"/>'s business and shrinks smoothly - this is only
    /// what ends the hold: a bump's gain is latched when the bump arrives, and a
    /// tank that stops half way over it must not keep the jolt for ever. An
    /// arriving tank spends a few frames at hundredths of a pixel a second, so
    /// zero would leave it twitching at the end of every order.</summary>
    public double StillSpeed = 1.0;

    /// <summary>
    /// Shear amplitude of the roll - one track riding up over a bump.
    ///
    /// Heave alone is invisible half the time. A bump lifts the tank in world
    /// space, and world vertical projects to screen vertical at every heading,
    /// so the jolt is always straight up and down on screen. That reads only
    /// when it runs across the direction of travel: driving up or down the
    /// screen the tank is already moving along that axis and the jolt vanishes
    /// into the motion, while on a diagonal the travel is mostly horizontal and
    /// the same jolt stands out.
    ///
    /// Roll displaces the top of the hull sideways *relative to the heading*,
    /// and the geometry falls out in our favour: driving up or down the screen,
    /// the lateral direction is exactly horizontal, so the roll is at its most
    /// visible precisely where the heave is at its least.
    /// </summary>
    public double RollAmplitude = 0.025;

    /// <summary>Pixels of vertical jolt, before the draw lands it on a whole
    /// pixel of the buffer it is rasterised into. Held per bump, like the roll.
    ///
    /// <b>This was an <c>int</c>, and the type was the defect rather than the
    /// guarantee it was documented as.</b> A whole number here is whole in the
    /// sprite's <em>own</em> space, and the node carries the class scale - 0.85
    /// for a light, 1.15 for a heavy - so one pixel of jolt was drawn as 0.85
    /// and as 1.15 of a screen pixel on two tanks out of three, resampled,
    /// which is the shimmer this class exists to avoid. The rounding belongs
    /// where the scale is known.
    ///
    /// <see cref="CameraShake"/> cites this class for the rule and then gets it
    /// right - it snaps in screen pixels "because zoom is between the two". The
    /// one place the rule was broken was the one that found it.</summary>
    public double Heave { get; private set; }

    /// <summary>Shear amplitude of the roll, positive displacing the top of the
    /// hull to its left - the shear runs along GroundDirection(heading + 90),
    /// and headings count anticlockwise. Not quantised - it feeds a shear, which resamples whatever
    /// the value is - but held per bump, so it changes as rarely as the
    /// heave.</summary>
    public double Roll { get; private set; }

    private double _phase;
    private double _gain;

    public void Advance(double distance, double speed)
    {
        _phase += distance / BumpSpacing;
        double moving = Math.Abs(speed);
        var bump = (long)Math.Floor(_phase);
        // Latched when the bump arrives rather than read live, and this is the
        // sample-and-hold the class promises rather than an optimisation. The
        // gain is what the sample is worth, so a gain that moves inside a bump
        // is a second decision about the same bump. Measured on the ramp before
        // it was latched: bump zero went 0 -> -1 -> -2 while the tank was still
        // on it, on all three classes - that is every start from rest, not an
        // edge case.
        if (bump != Bump)
        {
            Bump = bump;
            _gain = Math.Clamp(GainFloor + (1.0 - GainFloor) * moving / FullSpeed,
                               0.0, 1.0) * Damping;
        }
        // And the hold has to end when the motion does, or the latch outlives
        // it: a tank braking to a halt half way over a bump would settle with
        // the jolt still under it and keep it until it drove on. A stopped tank
        // is level - see StillSpeed.
        double held = moving > StillSpeed ? _gain : 0.0;
        Heave = Sample(bump, 0UL) * Amplitude * held;
        // a separate draw off the same bump: the same ground, but which track
        // caught it is not tied to how hard it hit
        Roll = Sample(bump, 0x632BE59BD9B4E019UL) * RollAmplitude * held;
    }

    public void Reset()
    {
        _phase = 0.0;
        Bump = long.MinValue;
        _gain = 0.0;
        Heave = 0.0;
        Roll = 0.0;
    }

    /// <summary>
    /// Which tank this is, so that two of them do not meet the same ground.
    ///
    /// <b>The index is the odometer and nothing else, so without this two tanks
    /// that have driven the same distance get bit-identical bumps</b> - whatever
    /// route they took, wherever they are on the board. Ordered onto parallel
    /// routes, three tanks jolt as one, which is one animation played three
    /// times: exactly the fault <see cref="TurretScan.Seed"/> was added for, in
    /// the last of the harness's clocks that was still shared.
    ///
    /// A seed rather than a clock, and that half is not optional: --capture and
    /// --trace pin the time step so two runs can be diffed, so the same tank has
    /// to meet the same ground twice while different tanks do not. Both halves
    /// are asserted.
    ///
    /// Not zero for the first tank, so a seed left unset is distinguishable from
    /// a seed meant to be the first tank's - the reason the scan's is set that
    /// way too.
    /// </summary>
    public ulong Seed;

    /// <summary>Which bump the tank is on. Public because "one bump, one
    /// decision" is the claim this class is built on, and a claim about bumps
    /// cannot be asserted without being able to see one.</summary>
    public long Bump { get; private set; } = long.MinValue;

    /// <summary>A value in [-1, 1) for bump number <paramref name="index"/>.
    /// <paramref name="salt"/> gives independent draws off the same bump.
    /// A hash rather than a random generator so a replay - or a screenshot
    /// comparison - lands on the same ground twice.</summary>
    private double Sample(long index, ulong salt)
    {
        unchecked
        {
            var x = ((ulong)index ^ salt ^ (Seed * 0xD1342543DE82EF95UL))
                    * 0x9E3779B97F4A7C15UL;
            x ^= x >> 29;
            x *= 0xBF58476D1CE4E5B9UL;
            x ^= x >> 32;
            return (double)(x & 0xFFFFFF) / 0x800000 - 1.0;
        }
    }
}
