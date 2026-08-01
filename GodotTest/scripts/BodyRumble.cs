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
/// The offset is a whole number of pixels. A fractional offset resamples the
/// sprite every frame - the tank layer filters linearly, for reasons the pitch
/// shear needed - and that softens it into a shimmer. On whole pixels nothing
/// is resampled at all: the sprite is either where it was or one pixel off,
/// which is what a jolt looks like.
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

    /// <summary>Whole pixels of vertical offset. Integer on purpose: it is the
    /// type that guarantees no resampling.</summary>
    public int Offset { get; private set; }

    /// <summary>Shear amplitude of the roll, positive displacing the top of the
    /// hull to its left - the shear runs along GroundDirection(heading + 90),
    /// and headings count anticlockwise. Not quantised - it feeds a shear, which resamples whatever
    /// the value is - but held per bump, so it changes as rarely as the
    /// heave.</summary>
    public double Roll { get; private set; }

    private double _phase;

    public void Advance(double distance, double speed)
    {
        _phase += distance / BumpSpacing;
        double gain = Math.Clamp(Math.Abs(speed) / FullSpeed, 0.0, 1.0);
        var bump = (long)Math.Floor(_phase);
        Offset = (int)Math.Round(Sample(bump, 0UL) * Amplitude * gain);
        // a separate draw off the same bump: the same ground, but which track
        // caught it is not tied to how hard it hit
        Roll = Sample(bump, 0x632BE59BD9B4E019UL) * RollAmplitude * gain;
    }

    public void Reset()
    {
        _phase = 0.0;
        Offset = 0;
        Roll = 0.0;
    }

    /// <summary>A value in [-1, 1) for bump number <paramref name="index"/>.
    /// <paramref name="salt"/> gives independent draws off the same bump.
    /// A hash rather than a random generator so a replay - or a screenshot
    /// comparison - lands on the same ground twice.</summary>
    private static double Sample(long index, ulong salt)
    {
        unchecked
        {
            var x = ((ulong)index ^ salt) * 0x9E3779B97F4A7C15UL;
            x ^= x >> 29;
            x *= 0xBF58476D1CE4E5B9UL;
            x ^= x >> 32;
            return (double)(x & 0xFFFFFF) / 0x800000 - 1.0;
        }
    }
}
