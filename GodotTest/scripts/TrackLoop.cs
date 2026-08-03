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

    /// <summary>
    /// Fastest the tread may be shown running, in links per screen frame.
    ///
    /// Half a link per frame is where the phase sequence aliases outright. 0.35
    /// leaves enough margin that the motion still reads as forward rather than
    /// as ambiguous, and on MT it works out at about 145px/s - which is the same
    /// honest-sync speed the pipeline notes arrived at from the other end, by
    /// asking how fine a tread the model has.
    ///
    /// Per frame and not per second, because aliasing is a property of the
    /// screen's sample rate. Converted against the frame time so the cap is the
    /// same picture whatever the frame rate happens to be.
    /// </summary>
    public double MaxPitchesPerFrame = 0.35;

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
    public double SyncSpeed => Pitch * MaxPitchesPerFrame * CapFrameRate;

    /// <summary>
    /// Wind the belt on by <paramref name="distance"/> pixels of travel.
    ///
    /// Signed: a tank backing up runs its belt backwards, which falls out of
    /// the same arithmetic and needs no case of its own.
    /// </summary>
    public void Advance(double distance, double delta)
    {
        if (Phases <= 0 || Pitch <= 0.0)
        {
            Slip = 1.0;
            return;
        }
        double want = distance / Pitch;
        double limit = MaxPitchesPerFrame * CapFrameRate * Math.Max(delta, 0.0);
        double take = Math.Clamp(want, -limit, limit);
        Slip = Math.Abs(want) > 1e-9 ? Math.Abs(take / want) : 1.0;
        _phase = Mod(_phase + take * Phases, Phases);
    }

    public void Reset()
    {
        _phase = 0.0;
        Slip = 1.0;
    }

    private static double Mod(double a, double n) => (a % n + n) % n;
}
