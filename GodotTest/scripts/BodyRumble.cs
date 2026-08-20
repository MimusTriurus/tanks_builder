using System;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The jolt of driving over uneven ground: a whole-pixel vertical offset, drawn
/// from the patch of ground the tank is standing on.
///
/// Three things make this read as ground rather than as a defect.
///
/// A bump is a place, not a distance. It used to be an odometer - the index was
/// <c>floor(travelled / BumpSpacing)</c> - and that is a clock in disguise: it
/// gave the right <em>rate</em> (half the speed, half the bumps a second, which
/// a real clock cannot do) and the wrong <em>ground</em>. Two tanks that had
/// driven the same distance met bit-identical bumps wherever they were, driving
/// the same route twice met different ones, and <c>Reset</c> re-rolled the
/// board. Now the ground is a lattice on the field, <see cref="BumpSpacing"/>
/// across, and the index is which square the contact patch is in: crossing into
/// the next square is a bump, so the rate still follows the speed, but a place
/// keeps what it had. It is also what makes ground <em>kinds</em> possible at
/// all - a patch can now be asked what it is made of.
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
/// A patch is a square of the lattice <em>and</em> the cell it is on, and the
/// second half is there for the kinds. What the ground is made of changes at a
/// cell edge, and the gain is latched per bump - so with the square alone a tank
/// that had driven onto stony ground went on riding the soft kind until the next
/// square, up to a quarter of a cell late, and a tank entering a ford kept its
/// dry ride for the same distance past the water's edge. Crossing an edge is a
/// decision, so the kind takes effect where it changes.
///
/// It also gives the board's cells a jolt of their own, which the plan asked for
/// under the heading of relief - and it is the half of that heading which
/// survives measurement. There is no step in the surface anywhere on this board:
/// ramps exist so that the ground stays continuous, so a jolt at a height
/// discontinuity would never once fire. And a climb is not silent already: the
/// harness indexes the lattice in flat space, which carries the tank's own
/// height, so going up a ramp adds that height to the distance walked across the
/// lattice and meets about a sixth more bumps for it. What relief has left over
/// is a break in the <em>grade</em> - the foot of a ramp and its crest - and that
/// is the one place the board already answers, by springing the surface plane.
///
/// One bump, two tracks. The heave and the roll were two independent draws
/// off the patch, which left the hull free to lift straight up while tipping
/// any which way: <see cref="RollAmplitude"/> called itself "one track riding
/// up over a bump" and no line of code said so. Now the patch gives a height
/// to each track and the body is their sum and their difference -
/// <c>heave = (l+r)/2</c>, <c>roll = (l-r)/2</c>, off the two salts that used
/// to be the two effects. Nothing new is configured; the two become one event.
///
/// It shows as a bound rather than as a correlation. The two are still
/// uncorrelated - sign agreement measured 0.505 over 34668 bumps - but their
/// joint support is now a diamond, <c>|heave|/Amplitude + |roll|/RollAmplitude
/// &lt;= 1</c>, where before it was the whole square with half of every bump
/// outside that diamond. Which is the physical statement: lifting the whole
/// tank takes both tracks agreeing, so one bump cannot be a full lift and a
/// full tip at once.
///
/// Two draws off one patch, not one draw off two places. The tracks are two
/// places really - the gauge is 69 to 84 field pixels, two or three lattice
/// squares - so a sample under each would come out fully independent anyway,
/// and would fire a bump whenever either track crossed: twice the rate, and
/// the heading and the atlas gauge dragged in to buy the same distribution.
/// The lattice is coarser than the tank, so a patch is the ground the tank is
/// on, and what it presents to each side of it.
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
    /// <summary>Peak of the signal in pixels before rounding - the height one
    /// track can ride up, which the body reaches only when both of them do.
    ///
    /// 1.4 caps the offset at one pixel and that is invisible at 1:1 - one
    /// pixel on a tank nearly two hundred tall is half a percent, and "a one
    /// pixel jitter" is better read as the floor of the effect than as the
    /// target. 2.2 was legible but spent a third of its bumps at two pixels,
    /// which starts to read as hopping. 1.7 landed between: three fifths of
    /// bumps at one pixel, an eighth at two, the rest level.
    ///
    /// <b>Then the pair rehabilitated the number that had been rejected.</b>
    /// Those fractions describe a flat draw, and the mean of two is triangular,
    /// so the same peak buys far fewer extremes: 1.7 paired measures level
    /// 0.50, one pixel 0.49, two pixels 0.015 - the two-pixel jolt is not
    /// quieter, it is gone, and the check that asks for more than one step is
    /// what finds that. 2.2 paired measures 0.40 / 0.50 / 0.100, putting the
    /// tenth back where it was tuned. The objection to 2.2 was never its peak,
    /// it was how often the peak came up.
    ///
    /// What does not come back is the level share - 0.29 before, 0.40 now,
    /// because what the heave gave up is in the roll instead. That is the
    /// direction the duty cycle wants anyway: two thirds of frames displaced is
    /// the standing complaint about this effect.</summary>
    public double Amplitude = 2.2;

    /// <summary>How wide a patch of ground is, in field pixels. At full speed a
    /// tank crosses about 8 a second, a handful per hex cell; at a crawl it is a
    /// slow judder.
    ///
    /// <b>A square lattice, and what that costs is a rate that leans on the
    /// heading.</b> A straight line of length L crosses <c>L(|cos|+|sin|)/s</c>
    /// squares, so a tank driving into a corner of the lattice meets sqrt(2) as
    /// many bumps as one driving along it. Measured across the six hex headings
    /// the spread comes to about 1.2, which is under the 1.5 a hex lattice would
    /// have been worth building for.</summary>
    public double BumpSpacing = 30.0;

    /// <summary>
    /// <b>There is no hysteresis on the boundary, and that is a decision that was
    /// made twice.</b> It was built - a dead zone, so that a tank running along a
    /// boundary could not cross it back and forth - and it has to come out,
    /// because any margin makes the patch a function of the <em>path</em> rather
    /// than of the place: a coarse walk and a fine walk down one line end on
    /// different ground (measured, -0.649 against -0.232 at 512px), which is the
    /// frame-rate invariance this class is built on. Both spellings were tried,
    /// gate the entry or make the patch sticky, and both fail it for the same
    /// reason.
    ///
    /// What it was protecting against turns out not to exist. Chatter needs a
    /// position that wobbles across a boundary, and a contact patch does not
    /// wobble: it walks a straight line between cell anchors, and the jolt is a
    /// draw offset that never feeds back into it. A line either crosses a
    /// boundary once or runs parallel to it, and a constant coordinate floors to
    /// a constant square.
    ///
    /// Re-entering a patch is safe anyway, which is the other half of why a
    /// lattice is affordable: the sample is a function of the square, so backing
    /// over a boundary gives back the same bump rather than a new one.
    /// </summary>

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
    /// <c>gain > 0.5 / Amplitude</c>: below that the floor does nothing
    /// whatever. That was 0.294 while the amplitude was 1.7, so 0.35 was the
    /// first setting on which the lever worked at all. The pair raised the
    /// amplitude to 2.2 and the threshold fell with it to 0.227, so there is
    /// headroom under this figure now - left where it was measured, because it
    /// is the one number keeping a crawl from being silent. It puts about an
    /// eighth of a crawling tank's bumps at one pixel, a sixth before the
    /// pair.
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
    /// against well over half of them on dry land at the same speed. The pair
    /// barely touched this one - 0.101 before it, 0.092 after - because at this
    /// gain the signal is small enough that the shape of the draw decides
    /// little; what it moved is the dry figure beside it, down from two
    /// thirds.
    /// </summary>
    public const double WetDamping = 0.35;

    /// <summary>What the ground is doing to the ride: 1 on dry land,
    /// <see cref="WetDamping"/> in a ford. Pushed in by the harness every frame,
    /// the way the tremble's level is, and read at the latch rather than live -
    /// so entering the water mid-bump finishes that bump dry, which is one bump
    /// and not a decision the class has to make twice.</summary>
    public double Damping = 1.0;

    /// <summary>
    /// How rough the kind of ground under the tank is, as a multiplier on the
    /// jolt: <see cref="TerrainSet.RideOf"/>, which measures it off the art.
    ///
    /// <b>Its own number beside the damping rather than folded into it</b>, for
    /// the reason the board keeps two shadow switches: they are two statements -
    /// what the ground is made of, and whether it is under water - and a single
    /// figure could not say which of them made a ride soft. Water wins where they
    /// meet by being much the smaller.
    ///
    /// This is what the lattice was for. While the bump was an odometer reading
    /// there was nowhere to ask the question: a patch of ground had no place, so
    /// it had no kind.
    /// </summary>
    public double Roughness = 1.0;

    /// <summary>Below this the tank counts as stopped and the body comes level.
    /// Not a threshold the effect switches on at - the strength is
    /// <see cref="FullSpeed"/>'s business and shrinks smoothly - this is only
    /// what ends the hold: a bump's gain is latched when the bump arrives, and a
    /// tank that stops half way over it must not keep the jolt for ever. An
    /// arriving tank spends a few frames at hundredths of a pixel a second, so
    /// zero would leave it twitching at the end of every order.</summary>
    public double StillSpeed = 1.0;

    /// <summary>
    /// How long the body carries a bump before it comes level again, whether or
    /// not it has driven off the patch.
    ///
    /// <b>A bump used to be held until the next one arrived, and at a crawl the
    /// next one is a second away.</b> Measured at 20px/s: the offset stood for
    /// 83 frames at a stretch, 270 at the worst, and the tank was displaced a
    /// quarter of the time. That is not a jolt, it is a lean - and it is where
    /// the standing complaint about this effect actually lives.
    ///
    /// <b>A time rather than a fraction of the patch, and that is arithmetic
    /// rather than taste.</b> Holding the first third of each bump is the
    /// obvious spelling and it is impossible at speed: at cruise a patch is
    /// crossed in 5.8 frames, so a third of it is 1.9 - the flicker this class
    /// rejects rounding a continuous wave for. Nor is there a fraction that
    /// works: a jolt needs three frames to read and so does the return, and
    /// 5.8 frames cannot be cut into two threes. The fraction is undoable
    /// exactly where the complaint was written down, and unnecessary exactly
    /// where it is real.
    ///
    /// So the split is the honest one: the ground says <em>where</em> and
    /// <em>how hard</em>, the body says <em>how long</em>. Holding until the
    /// next patch put both of those in the ground.
    ///
    /// <b>0.20s is bounded from below by the road.</b> It has to outlast the
    /// longest bump interval any tank meets at cruise, or the ride on a road
    /// starts blinking: that worst case is a heavy along a lattice axis, where
    /// the crossing is the full <see cref="BumpSpacing"/> and the interval
    /// 30/175 = 0.171s. Measured against an uncapped run on that heading, 0.20
    /// changes nothing on any of the three - not one frame in six thousand -
    /// and 0.15 changes 418 frames of a heavy's. It also lands where
    /// <see cref="BodyPitch"/>'s spring peaks, 0.16s, which is what makes it a
    /// figure about suspension rather than a fudge.
    ///
    /// What it buys, at 20px/s: displaced frames 24.4% -> 4.6%, longest run
    /// 270 -> 20, mean run 83 -> 12. At cruise, by construction, nothing.
    /// </summary>
    public double HoldSeconds = 0.20;

    /// <summary>
    /// Shear amplitude of the roll - one track riding up over a bump, which is
    /// now what it is rather than what it was called: the roll is half the
    /// difference of the two track heights, so a pure roll is one track up and
    /// the other not.
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

    /// <summary>Pixels of vertical jolt - the mean of the two track heights -
    /// before the draw lands it on a whole pixel of the buffer it is rasterised
    /// into. Held per bump, like the roll.
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

    /// <summary>Shear amplitude of the roll - half the difference of the two
    /// track heights - positive displacing the top of the hull to its left - the shear runs along GroundDirection(heading + 90),
    /// and headings count anticlockwise. Not quantised - it feeds a shear, which resamples whatever
    /// the value is - but held per bump, so it changes as rarely as the
    /// heave.</summary>
    public double Roll { get; private set; }

    private double _gain;

    /// <summary>Seconds since this bump arrived, which is what
    /// <see cref="HoldSeconds"/> spends. Zero on the frame it arrives, so the
    /// bump always gets at least that frame.</summary>
    private double _age;

    /// <summary>
    /// Where the tank is standing, in the field's own pixels, and how fast it is
    /// going.
    ///
    /// <b>The flat ground point, not the drawn one.</b> A tank a level down is
    /// drawn 64.8px above the row its ground is on, so a lattice indexed off the
    /// drawn row would hand two tanks at different heights the same square - the
    /// fourth place this board has charged for that difference, after the
    /// selection ring, the pond and the wood. See <see cref="HexField.Bare"/>.
    /// </summary>
    public void Advance(Vector2 ground, Vector2I cell, double speed, double dt)
    {
        double moving = Math.Abs(speed);
        double qx = ground.X / BumpSpacing, qy = ground.Y / BumpSpacing;
        var square = new Vector2I((int)Math.Floor(qx), (int)Math.Floor(qy));
        // Which square, and nothing about how it got here: see the note where
        // the hysteresis used to be.
        // Latched when the bump arrives rather than read live, and this is the
        // sample-and-hold the class promises rather than an optimisation. The
        // gain is what the sample is worth, so a gain that moves inside a bump
        // is a second decision about the same bump. Measured on the ramp before
        // it was latched: bump zero went 0 -> -1 -> -2 while the tank was still
        // on it, on all three classes - that is every start from rest, not an
        // edge case.
        if (square != Patch || cell != Cell)
        {
            Patch = square;
            Cell = cell;
            Bump++;
            _gain = Math.Clamp(GainFloor + (1.0 - GainFloor) * moving / FullSpeed,
                               0.0, 1.0) * Damping * Roughness;
            _age = 0.0;
        }
        else
            _age += dt;
        // And the hold has to end when the motion does, or the latch outlives
        // it: a tank braking to a halt half way over a bump would settle with
        // the jolt still under it and keep it until it drove on. A stopped tank
        // is level - see StillSpeed.
        //
        // It also ends when the body has carried the bump long enough, which is
        // the suspension's business rather than the ground's - see HoldSeconds.
        // Letting go is not a second decision about the bump: the gain is not
        // read again, it is dropped.
        double held = moving > StillSpeed && _age < HoldSeconds ? _gain : 0.0;
        // The height this patch presents to each track, and the body is their
        // sum and their difference. The same two draws as before - they used to
        // be the heave and the roll outright - so the ground has not moved,
        // only what the tank does with it. See the pair in the class note.
        double left = Sample(Patch, Cell, LeftTrack), right = Sample(Patch, Cell, RightTrack);
        Heave = (left + right) * 0.5 * Amplitude * held;
        Roll = (left - right) * 0.5 * RollAmplitude * held;
    }

    public void Reset()
    {
        Patch = Nowhere;
        Cell = Nowhere;
        Bump = 0;
        _gain = 0.0;
        _age = 0.0;
        Heave = 0.0;
        Roll = 0.0;
    }

    /// <summary>Which patch of ground the tank is standing on. Public because
    /// what the ground gives is now a function of this and nothing else, which
    /// is the claim worth being able to check.</summary>
    public Vector2I Patch { get; private set; } = Nowhere;

    /// <summary>Which cell of the board the tank is standing on, the other half
    /// of the patch. Pushed in rather than worked out here: which cell a point is
    /// on is <see cref="HexField"/>'s one answer, and a second one derived from a
    /// position would disagree with the picture the moment the board gained a
    /// level.</summary>
    public Vector2I Cell { get; private set; } = Nowhere;

    /// <summary>How many bumps have been decided. Public because "one bump, one
    /// decision" is the claim this class is built on, and a claim about bumps
    /// cannot be asserted without being able to see one. A count rather than the
    /// patch itself, so that leaving a patch and coming back is two decisions
    /// about the same ground - which is what it is.</summary>
    public long Bump { get; private set; }

    /// <summary>No patch yet, so that the first one latches wherever it is -
    /// including inside a dead zone, which is otherwise a tank that never gets
    /// its first bump.</summary>
    private static readonly Vector2I Nowhere =
        new Vector2I(int.MinValue, int.MinValue);

    /// <summary>Which track a draw belongs to. Two named salts rather than one
    /// hashed against an index, so that the pair is the pair that was already
    /// here before the tracks were: the same patch gives back the same two
    /// numbers it always did.</summary>
    private const ulong LeftTrack = 0UL;

    /// <inheritdoc cref="LeftTrack"/>
    private const ulong RightTrack = 0x632BE59BD9B4E019UL;

    /// <summary>A value in [-1, 1) for a patch of ground - which is the lattice
    /// square and the cell, so that an edge crossing draws new ground rather than
    /// merely re-latching the gain.
    /// <paramref name="salt"/> gives independent draws off the same patch.
    /// A hash rather than a random generator so a replay - or a screenshot
    /// comparison, or the same tank coming back this way - lands on the same
    /// ground again.</summary>
    private static double Sample(Vector2I patch, Vector2I cell, ulong salt)
    {
        unchecked
        {
            var x = ((ulong)(uint)patch.X * 0x9E3779B97F4A7C15UL)
                    ^ ((ulong)(uint)patch.Y * 0xC2B2AE3D27D4EB4FUL)
                    ^ ((ulong)(uint)cell.X * 0xD6E8FEB86659FD93UL)
                    ^ ((ulong)(uint)cell.Y * 0xA24BAED4963EE407UL) ^ salt;
            x ^= x >> 29;
            x *= 0xBF58476D1CE4E5B9UL;
            x ^= x >> 32;
            return (double)(x & 0xFFFFFF) / 0x800000 - 1.0;
        }
    }
}
