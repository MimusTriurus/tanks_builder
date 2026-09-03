using System;

namespace TankSpriteTest;

/// <summary>
/// A tank that has stopped being a machine.
///
/// The prototype spends no new pixels, and that is a claim about where the read
/// comes from rather than a saving. A tank on this field trembles, smokes,
/// scans with its turret and rocks when it moves; switching all of that off is
/// itself the statement, and it is available before any atlas is re-rendered.
/// What the paint does on top - see <see cref="TankSprite.Char"/> - is a tint,
/// and a tint cannot invent soot streaming from a hatch. If it turns out not to
/// carry, the charred body is about 4MB a tank, because a wreck has no phase
/// axis and so costs like <c>hull</c> rather than like <c>burn</c>.
///
/// Death is an event and a wreck is a state, which is the same split the hit
/// pair and the exhaust already stand on either side of. So there is one clock
/// with an age on it and everything else is a function of that age: how charred
/// the paint is, how hard it is still burning, how far the turret has been
/// knocked round. Nothing here is a table.
/// </summary>
public sealed class Wreck
{
    /// <summary>How long the paint takes to char. Not instant, because a tank
    /// that goes black on the frame it is hit reads as a sprite swap; not slow,
    /// because the thing being shown is over by then.</summary>
    public const double CharSeconds = 1.4;

    /// <summary>
    /// How long the fire takes to come up, and it is <see cref="CharSeconds"/>
    /// because they are one window.
    ///
    /// <b>This is the seam <see cref="ProcRack"/> exists for, and it was wrong
    /// before there was anything to put in it.</b> A hull dies, <c>Burning</c>
    /// goes true and <see cref="Blaze"/> answered <em>one</em> on that same
    /// frame - so the flame and the column arrived at full strength instantly,
    /// which is the lamp-switched-on failure <see cref="ProcSmoke"/> names in its
    /// own words about conveyors. Nothing was drawing the moment of death, so
    /// there was nothing for the fire to come up behind.
    ///
    /// <b>Equal to the char rather than tuned beside it.</b> The paint blackening
    /// was already the aftermath and already this long; the detonation dies over
    /// exactly that window, so one ramp does both and there is no second number
    /// to keep in step. Written as an alias rather than as 1.4 so that moving the
    /// char moves the handover with it - they are the same second of the same
    /// event.
    ///
    /// <b>And it is a ramp on death only.</b> A tank set alight by the key burns
    /// at full from the first frame, because being on fire and being dead are
    /// different things and only one of them starts with an explosion.
    /// </summary>
    public const double RiseSeconds = CharSeconds;

    /// <summary>How long it burns hard before the flame starts to go, and how
    /// long it takes to go. A burning wreck is what the fire layers were
    /// rendered for; what is new here is that it eventually stops.</summary>
    public const double BlazeSeconds = 10.0;
    public const double DieSeconds = 8.0;

    /// <summary>What is left of the column once the flame is out. Not zero: a
    /// burnt-out hull smokes for the rest of the battle, and the column is the
    /// only thing on the board that says where one died from across the
    /// field.</summary>
    public const double SmokeFloor = 0.55;

    // The turret used to be knocked 47 degrees off the hull and slipped three
    // pixels down the screen, and both are gone. It was the cheapest strong read
    // available - the turret is its own layer with its own heading, so a cant is
    // a number rather than a frame - but it is a claim about what happened to
    // this particular tank, and the wreck does not know that. The slip went with
    // it rather than staying: displaced without being turned, a layer reads as
    // one that has come unstuck, which is the failure it was sized to avoid.

    public bool Dead { get; private set; }

    /// <summary>
    /// Whether the ammunition went off, as against the fuel - which of the two
    /// deaths this was.
    ///
    /// <b>A fact about the killing blow, kept on the wreck because the wreck is
    /// what outlives it.</b> The plate that took the last round decides it - see
    /// <see cref="TankTick.Kill"/> - and once it is decided nothing may change
    /// it, for <see cref="Kill"/>'s own reason: a hull that goes on being shot at
    /// does not die a second and different death.
    ///
    /// <b>What it is for beyond choosing the picture.</b> Two things this project
    /// has already argued about. <c>fuel_flash</c> is the other half of the fork
    /// and is not built. And the turret's cant - knocked off the hull, which this
    /// file removed on the grounds that it is "a claim about what happened to this
    /// particular tank, and the wreck does not know that" - is a claim a rack
    /// detonation is entitled to make and a fire in the engine bay is not. So the
    /// wreck does know now, and the cant is waiting on a heading offset for the
    /// wreck's turret layer rather than on the argument.
    /// </summary>
    public bool Racked { get; private set; }

    /// <summary>
    /// How long the pose waits before it swaps, in seconds.
    ///
    /// <b>Because the swap is one frame and always will be.</b> The paint chars
    /// over a second and a half, but the pose is a different layer of the atlas -
    /// the turret drooped, both belts slack - so it can only ever cut. Reported
    /// as exactly that, and it is not a thing a ramp can fix: there is no half-way
    /// frame between a turret laid on its ring and one knocked off it, and
    /// rendering one would be a third pose to say what a cut already says.
    ///
    /// <b>So the cut is hidden rather than softened, and the difference matters
    /// for what gets built.</b> Making the burst bigger - which is the obvious
    /// answer - buys less than it looks: <see cref="ProcRack"/>'s fire is additive
    /// and additive light brightens what is behind it rather than hiding it. What
    /// hides is the soot, and it is at its thickest a tenth of a second in. Moving
    /// the cut to meet it costs one number; making the fire big enough to blind
    /// the eye through it costs the whole look of the effect.
    ///
    /// <b>Set by the caller and nought by default</b>, because a board that is not
    /// drawing the detonation has nothing to hide behind - see
    /// <see cref="TankTick.Kill"/>. A veil with no picture under it is a tank that
    /// visibly stumbles for a tenth of a second before it dies, which is worse
    /// than the cut it was covering.
    /// </summary>
    public double Veil;

    /// <summary>
    /// The tuned veil - what <see cref="TankTick"/> spends when there is a
    /// detonation drawing.
    ///
    /// <b>Measured rather than picked, and the first guess was wrong by
    /// two-thirds.</b> Read at 0.10s the cut landed in the <em>dip</em>: the
    /// coverage of the hull box runs 21% at 0.03s, sits on a plateau of about 23%,
    /// falls to 17.6% at 0.13s as the jets collapse and the soot has not yet
    /// built, and peaks at 28.7% at 0.27s. So a tenth of a second moved the cut to
    /// the one moment there was least to hide behind. Thirteen frames puts it on
    /// the peak.
    ///
    /// <b>And the honest number is still 29%, not 100%.</b> The cut itself moves
    /// about 4000px of a 19800px box, so this hides most of what changes and not
    /// all of it - see <see cref="Veil"/> on why the alternative, a fire big
    /// enough to blind the eye through, costs the look of the whole effect.
    /// </summary>
    public const double VeilSeconds = 0.22;

    /// <summary>Whether the wrecked pose is showing yet. True on a live tank's
    /// death frame only when nothing was asked to cover it.</summary>
    public bool Posed => Dead && Age >= Veil;

    /// <summary>Seconds since it died. Counted up rather than compared against a
    /// clock, for the reason the reload is: --capture and --trace fix the time
    /// step so two runs can be diffed, and a count is the same number of frames
    /// in both.</summary>
    public double Age { get; private set; }

    /// <summary>Kill it, and say whether this was the killing blow. False on a
    /// wreck, so a hull that goes on being shot at does not restart its own
    /// death - the sound, the turret's cant and the age all belong to the one
    /// round that did it.
    ///
    /// <paramref name="racked"/> is which of the two deaths it was - see
    /// <see cref="Racked"/>. Required rather than defaulted, because a default is
    /// how a fork gets forgotten at one of its call sites and the caller that
    /// forgot it is the caller that knew.</summary>
    public bool Kill(bool racked)
    {
        if (Dead)
            return false;
        Dead = true;
        Racked = racked;
        Age = 0.0;
        return true;
    }

    public void Reset()
    {
        Dead = false;
        Racked = false;
        Veil = 0.0;
        Age = 0.0;
    }

    public void Update(double delta)
    {
        if (Dead)
            Age += delta;
    }

    /// <summary>How charred the paint is, 0 to 1.</summary>
    public double Char =>
        Dead ? Math.Clamp(Age / CharSeconds, 0.0, 1.0) : 0.0;

    /// <summary>How much of the fire has arrived - nought on the frame of death,
    /// one by <see cref="RiseSeconds"/>, and one outright on a tank that is
    /// merely alight. See <see cref="RiseSeconds"/>: this is the whole of the
    /// handover from the detonation to the state it leaves behind.</summary>
    public double Rise =>
        !Dead ? 1.0 : Math.Clamp(Age / RiseSeconds, 0.0, 1.0);

    /// <summary>How much flame is left. One on a live tank, so a hull set
    /// burning by the key burns at full - being on fire and being dead are
    /// different things and only one of them ends.
    ///
    /// <b>Multiplied by the rise rather than starting there</b>, so the shape
    /// below is untouched: the flame comes up over the first second and a half,
    /// burns hard, and then goes exactly as it did.</summary>
    public double Blaze =>
        !Dead ? 1.0
        : Rise * (Age <= BlazeSeconds
                  ? 1.0
                  : Math.Clamp(1.0 - (Age - BlazeSeconds) / DieSeconds, 0.0, 1.0));

    /// <summary>How much column is left: it thins with the flame and then
    /// stays.
    ///
    /// <b>The rise is on this too, and it has to be.</b> The floor is what a
    /// burnt-out hull keeps for the rest of the battle - and taken as read on the
    /// frame of death it put 55% of a column on the board instantly, which is the
    /// same lamp the flame was, only dimmer. A column climbing out of the
    /// detonation is the thing the detonation is handing over.</summary>
    public double Smoke =>
        Rise * (1.0 - (1.0 - SmokeFloor) * (1.0 - Blaze));

}
