using System;

namespace TankSpriteTest;

/// <summary>
/// The clock behind the track belt: a phase that goes round with the ground.
///
/// Phase comes from distance travelled, and here that is not a preference the
/// way it is for <see cref="BodyRumble"/>. The belt is the part of the tank
/// that touches the ground; the ground is what it is winding against. Run it
/// off a timer and the tread slides at every speed but one, which is the single
/// most obvious thing that can be wrong with a tracked vehicle.
///
/// One turn of the cycle is one link of the belt, because that is what the
/// renderer built: the last phase is a slide of exactly one link and comes out
/// identical to the first (see track_cycle.py). So the tank has to cover one
/// link's worth of ground per cycle, and that link is a length the atlas
/// carries - in world units, divided here by the atlas's own units-per-pixel.
/// A pitch in pixels stamped at render time would have been a second guess at a
/// number the camera fit had not yet decided.
///
/// The cap is the interesting part
/// -------------------------------
/// A tread cannot be shown at speed and this is arithmetic, not tuning. MT's
/// link is 6.9px on screen and its cruise is 240px/s, so at 60fps the tank
/// covers 4px a frame - well over half a link. Past half a link per frame the
/// eight phases alias, and an aliased conveyor does not blur, it runs
/// *backwards*: 4.6 phases forward out of 8 reads as 3.4 back. Tracks running
/// the wrong way under a tank driving forward is about the most legible error
/// available.
///
/// So the phase rate is capped below that, and above the cap the belt slips
/// against the ground instead. Slipping is wrong and looks fine; reversing is
/// wrong and looks broken. <see cref="Slip"/> says how much is being given up,
/// so the trade is a number on the panel rather than a silent decision.
///
/// What the cap cost, and what buys it back
/// ----------------------------------------
/// A cap alone flattens the classes, which is the thing it was not supposed to
/// do. Measured at cruise: LTP 310px/s, MTP 240, HTP 175 - a spread of 1.77 -
/// and the tread showing 135, 145 and 175px/s, a spread of 1.30 with the order
/// <em>inverted</em>. The fastest tank had the slowest-looking belt, because
/// two of the three were pinned against the same limit.
///
/// The way out is not a higher cap - Nyquist does not move, and it is a
/// property of the tread pattern rather than of how many poses were rendered,
/// so more phases buy nothing. It is that a real belt at speed is a
/// <em>smear</em>, and a smear has no pattern left to read backwards. So the
/// belt fades toward the average of all its phases as it goes, and that average
/// is the honest picture of a belt going too fast to count - see
/// <see cref="Blur"/>. Once nothing crisp is left the rate may keep rising, and
/// the classes separate again on both counts at once: HTP crisp and in step,
/// MTP running true under a light smear, LTP a full blur.
/// </summary>
public sealed class TrackLoop
{
    /// <summary>How many phases the atlas holds. Read off the layer rather than
    /// assumed - the renderer's count is a config value, and a loop that wraps
    /// anywhere but at the seam pops there.</summary>
    public int Phases = 8;

    /// <summary>Ground covered by one turn of the cycle, in screen pixels. One
    /// link of the belt. Comes from the atlas; the fallback is only so a tank
    /// without the metadata still walks at a plausible rate.</summary>
    public double Pitch = 8.0;

    /// <summary>How big the tank is being drawn - see
    /// <see cref="TankSprite.BodyScale"/>.
    ///
    /// It belongs here rather than being multiplied into <see cref="Pitch"/> by
    /// the caller because the two numbers answer different questions and only one
    /// of them comes off the atlas: the link is a length the renderer measured,
    /// the scale is a size the harness chose. Kept apart, the panel can show the
    /// atlas figure while the arithmetic uses the drawn one, and the rule that
    /// they multiply is something the self-test can state.</summary>
    public double Scale = 1.0;

    /// <summary>Ground covered by one turn of the cycle as actually drawn. This
    /// is the number the belt has to keep up with: a tank drawn at 0.85 has a
    /// 0.85 link on the ground, and winding it at the unscaled pitch would slip
    /// by exactly the factor the tank was scaled by.</summary>
    public double LinkOnScreen => Pitch * Scale;

    /// <summary>
    /// Where the tread stops being countable, in links per screen frame, and so
    /// where <see cref="Blur"/> starts coming in.
    ///
    /// Half a link per frame is where the phase sequence aliases outright. 0.35
    /// leaves enough margin that the motion still reads as forward rather than
    /// as ambiguous, and on MT it works out at about 145px/s - which is the same
    /// honest-sync speed the pipeline notes arrived at from the other end, by
    /// asking how fine a tread the model has.
    ///
    /// This used to be the hard cap. It is now the point the smear begins,
    /// which is the same measurement doing the job it was always the right
    /// number for: it is where the eye loses the links, not where the belt has
    /// to stop.
    ///
    /// Per frame and not per second, because aliasing is a property of the
    /// screen's sample rate. Converted against the frame time so it is the same
    /// picture whatever the frame rate happens to be.
    /// </summary>
    public double BlurOnset = 0.35;

    /// <summary>
    /// Fastest the tread may be shown running, in links per screen frame, and
    /// also where <see cref="Blur"/> reaches 1.
    ///
    /// Above Nyquist on purpose, and the two facts are the same fact: this is
    /// not where the pattern stays readable, it is where there is no pattern
    /// left to be read. Between <see cref="BlurOnset"/> and here the crisp belt
    /// is fading out linearly, so by 0.50 - where a reversal could first appear
    /// - it is down to half, and by 0.65 it is gone. What is left is the phase
    /// average, which looks the same whichever way it is going.
    ///
    /// Past this the belt slips, as it always did. It is just much further up:
    /// on LTP the limiter now bites at 251px/s against a 310 cruise rather than
    /// at 135.
    /// </summary>
    public double MaxPitchesPerFrame = 0.65;

    /// <summary>Frame time the cap is quoted against.</summary>
    public double CapFrameRate = 60.0;

    private double _phase;

    /// <summary>Continuous position round the loop, in phases.</summary>
    public double Phase => _phase;

    /// <summary>The frame to show, or -1 when there are no phases.</summary>
    public int Frame => Phases <= 0
        ? -1
        : Math.Clamp((int)Math.Floor(Mod(_phase, Phases)), 0, Phases - 1);

    /// <summary>How much of the ground the belt actually kept up with on the
    /// last step: 1 when it is in step, less when the cap is holding it back.
    /// 1 while standing still, because a belt that is not moving is not
    /// slipping.</summary>
    public double Slip { get; private set; } = 1.0;

    /// <summary>Distance per second at which the cap starts to bite. Exposed so
    /// the panel and the assertions read the threshold rather than recompute
    /// it.</summary>
    public double SyncSpeed => LinkOnScreen * MaxPitchesPerFrame * CapFrameRate;

    /// <summary>Distance per second at which the belt starts to smear. The
    /// companion to <see cref="SyncSpeed"/>, and the lower of the two.</summary>
    public double BlurSpeed => LinkOnScreen * BlurOnset * CapFrameRate;

    /// <summary>
    /// How much of the belt is showing as a smear rather than as links: 0 crisp,
    /// 1 nothing but the phase average.
    ///
    /// Taken from the rate the ground <em>wanted</em>, not the one the cap
    /// allowed, so it stays at 1 above the cap instead of dropping back when
    /// the limiter starts holding the phase down.
    ///
    /// 1 at a standstill would be wrong twice over and it cannot happen: a
    /// parked tank wants no rate at all, so this is 0 and the links are there to
    /// be counted, which is exactly when they are looked at.
    /// </summary>
    public double Blur { get; private set; }

    /// <summary>
    /// Phases per second the belt actually turned on the last step - the capped
    /// rate, not the wanted one.
    ///
    /// This is what the track's *sound* runs at, and taking it from here rather
    /// than from the speed is the whole point. Above <see cref="SyncSpeed"/> the
    /// picture slips against the ground on purpose; a rattle driven off raw speed
    /// would keep up with the ground and so drift against the picture, which is
    /// the one disagreement worth more than the error it was meant to fix. Fed
    /// from here the two cannot part, and they slip together.
    ///
    /// Signed, so backing up reads as negative and the caller can use the
    /// magnitude without having to know about reverse.
    /// </summary>
    public double PhaseRate { get; private set; }

    /// <summary>
    /// The fastest <see cref="PhaseRate"/> the cap will ever allow, phases per
    /// second.
    ///
    /// The reference the track's sound is pitched against, and derived rather
    /// than tuned on purpose. It is the speed at which the belt is showing all
    /// the ground it is willing to show, so a rattle played at unity here and
    /// scaled below it slows down with the picture and then stops slowing down
    /// with the picture - both halves of the cap, for free. A number picked by
    /// ear would have had to be re-picked every time the cap moved.
    /// </summary>
    public double MaxPhaseRate => MaxPitchesPerFrame * CapFrameRate * Phases;

    /// <summary>
    /// Wind the belt on by <paramref name="distance"/> pixels of travel.
    ///
    /// Signed: a tank backing up runs its belt backwards, which falls out of
    /// the same arithmetic and needs no case of its own.
    /// </summary>
    public void Advance(double distance, double delta)
    {
        if (Phases <= 0 || LinkOnScreen <= 0.0)
        {
            Slip = 1.0;
            PhaseRate = 0.0;
            Blur = 0.0;
            return;
        }
        double want = distance / LinkOnScreen;
        double limit = MaxPitchesPerFrame * CapFrameRate * Math.Max(delta, 0.0);
        double take = Math.Clamp(want, -limit, limit);
        Slip = Math.Abs(want) > 1e-9 ? Math.Abs(take / want) : 1.0;
        Blur = BlurAt(delta > 0.0 ? Math.Abs(want) / (delta * CapFrameRate) : 0.0);
        _phase = Mod(_phase + take * Phases, Phases);
        PhaseRate = delta > 0.0 ? take * Phases / delta : 0.0;
    }

    /// <summary>
    /// One frame's ground split between the two belts.
    ///
    /// A belt sitting <paramref name="arm"/> from the centre covers the drive
    /// plus what the turn sweeps it through, and the two sides differ only in
    /// the sign of the arm - so a pivot with no drive at all comes out equal and
    /// opposite, which is what a tank does and what one unsigned number for both
    /// could never say.
    ///
    /// A rising heading turns counter-clockwise, and a point to the hull's left
    /// then travels backwards along it: omega cross r points behind. So a left
    /// turn is the right belt going forward.
    ///
    /// Static and split out for the reason <see cref="BlurAt"/> is: the claim is
    /// about arithmetic, and it should be assertable without driving a tank into
    /// the state that produces it.
    /// </summary>
    public static (double Left, double Right) Split(double drive,
                                                    double swingDegrees,
                                                    double arm)
    {
        double pivot = swingDegrees * Math.PI / 180.0 * arm;
        return (drive - pivot, drive + pivot);
    }

    /// <summary>The smear that goes with a wanted rate of
    /// <paramref name="perFrame"/> links per screen frame. Split out so the ramp
    /// can be asserted at its two ends without driving a tank to them.</summary>
    public double BlurAt(double perFrame)
    {
        double span = MaxPitchesPerFrame - BlurOnset;
        return span <= 0.0
            ? (perFrame > BlurOnset ? 1.0 : 0.0)
            : Math.Clamp((perFrame - BlurOnset) / span, 0.0, 1.0);
    }

    public void Reset()
    {
        _phase = 0.0;
        Slip = 1.0;
        PhaseRate = 0.0;
        Blur = 0.0;
    }

    private static double Mod(double a, double n) => (a % n + n) % n;
}
