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

    // <b>The pose swaps on the frame the tank dies, and a mechanism for delaying
    // it was built here and taken back out.</b> The swap is a cut by construction
    // - the drooped turret and the slack belts are different layers of the atlas,
    // and there is no half-way frame between a turret laid on its ring and one
    // knocked off it - so the question was only ever where to put it. Held back
    // 0.22s it landed under the thickest of ProcRack's soot and hid three and a
    // half times as many pixels: 1116 against 3953 of a 19800px hull box.
    //
    // It read worse, by the one criterion a measurement of coverage cannot see. A
    // detonation that goes off on an intact tank and leaves a broken one a fifth
    // of a second later is TWO events. The blast is not what happens before the
    // tank breaks; it IS the tank breaking. So the cut goes at the blast, takes
    // the thinner cover, and the field and the constant that delayed it are gone
    // rather than parked at zero - a knob whose only correct setting is off is not
    // a knob.
    //
    // What survives that experiment is on ProcRack rather than here: the skirt is
    // thicker because the belts are at the bottom of the silhouette, where nothing
    // else in that model reaches, and they change on the frame the flash arrives.

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
