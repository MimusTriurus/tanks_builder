using System;

namespace TankSpriteTest;

/// <summary>
/// The clock behind the gun tube sliding back into the turret.
///
/// An event, like the shot and the hit, and not a loop like the exhaust or the
/// belts - the tube goes back once per round.
///
/// **But it never goes off, and that is what makes it a third shape.** Every
/// other event layer stops being drawn when its clock runs out: the flash, the
/// burst and the dust all end at -1 and the layer draws nothing. The tube is
/// part of the tank, so there is no frame on which it is absent. This clock
/// returns to phase 0 - the rest pose - and phase 0 is what it reads between
/// shots. It walks 0 -> ... -> 0 rather than 0 -> ... -> off.
///
/// Its own clock rather than the shot's, for <see cref="BurnLoop"/>'s reason:
/// the two events have different durations. The shot runs 34 screen frames and
/// the recoil about 28, and tying them together would mean choosing which one
/// gets the wrong tempo.
///
/// The tempo is the whole of what lives here, and it is deliberate that the
/// renderer left it to this table. `barrel_recoil.curve` spaces the poses
/// **evenly in travel**, because what a rendered phase owes is a pose distinct
/// from the pose next door; how long each is held is a question about time, and
/// this is where time is answered. Getting that split wrong cost 24 of 72 frames
/// on the renderer's first attempt.
///
/// So the table is short at the front and long at the back. A real gun goes back
/// in 30-50 ms and returns over 200-400 ms, and the muzzle flash - six frames -
/// covers the whole of the stroke. Which means the recoil is invisible exactly
/// while it happens and what the eye reads is the tube creeping home afterwards.
/// Spending frames on the kick would be spending them under the flash.
/// </summary>
public sealed class RecoilLoop
{
    /// <summary>Screen frames at full travel. Short: the flash is over the
    /// muzzle for all of it.</summary>
    public const int Kick = 3;

    /// <summary>Frames on the first phase of the return, and how much longer
    /// each one after it is held. Growing, so the tube visibly slows as it
    /// settles - the last pixel takes the longest, which is what a recuperator
    /// does.</summary>
    public const int ReturnBase = 6;
    public const double ReturnGrowth = 1.35;

    /// <summary>
    /// Frames to hold each moving phase, for an atlas with
    /// <paramref name="phases"/> rendered poses including rest.
    ///
    /// Derived from the phase count rather than written out, because the count
    /// is the renderer's to choose: it follows the travel at about one phase per
    /// pixel of stroke, so a longer recoil renders more poses and this has to
    /// hold them all. A hand-written table would silently drop the last pose.
    /// </summary>
    public static int[] HoldsFor(int phases)
    {
        int moving = Math.Max(1, phases - 1);
        var holds = new int[moving];
        holds[0] = Kick;
        for (int i = 1; i < moving; i++)
            holds[i] = (int)Math.Round(ReturnBase * Math.Pow(ReturnGrowth, i - 1));
        return holds;
    }

    /// <summary>Screen frames one full recoil takes.</summary>
    public static int DurationFor(int phases)
    {
        int total = 0;
        foreach (int hold in HoldsFor(phases))
            total += hold;
        return total;
    }

    /// <summary>
    /// Phase to show <paramref name="elapsed"/> frames into a shot, given an
    /// atlas of <paramref name="phases"/> poses.
    ///
    /// Returns 0 - rest - before the shot and after it, never -1. A caller that
    /// treats this like <see cref="HitLoop.PhaseAt"/> and hides the layer on a
    /// negative would blink the gun out of existence between rounds.
    /// </summary>
    public static int PhaseAt(int elapsed, int phases)
    {
        if (phases < 2 || elapsed < 0)
            return 0;
        int[] holds = HoldsFor(phases);
        int t = 0;
        for (int i = 0; i < holds.Length; i++)
        {
            t += holds[i];
            if (elapsed < t)
                return Math.Min(i + 1, phases - 1);
        }
        return 0;
    }

    /// <summary>How many poses the atlas has, rest included. Held so that the
    /// phase can be asked for without the atlas being passed in every time -
    /// set once when the tank is built.</summary>
    public int Phases { get; set; }

    private int _frame = -1;

    public int Phase => PhaseAt(_frame, Phases);

    /// <summary>Whether the tube is away from rest. Not "is the clock
    /// running": the answer that matters to a viewer is whether the gun is out
    /// of position.</summary>
    public bool Live => Phase > 0;

    public int Duration => DurationFor(Phases);

    /// <summary>Fire. Restarts from the kick rather than being ignored, for
    /// <see cref="HitLoop.Strike"/>'s reason: a second round is a second recoil,
    /// and the useful thing to see is the newest.</summary>
    public void Fire() => _frame = 0;

    public void Advance()
    {
        if (_frame < 0)
            return;
        _frame++;
        if (_frame >= Duration)
            _frame = -1;          // back to rest, which Phase reports as 0
    }

    public void Reset() => _frame = -1;
}
