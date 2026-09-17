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
    // <b>Halved 2026-09-08</b>, from 10 and 8: on the board a hull that burnt for
    // eighteen seconds and then took four more to go was a battle waiting on a
    // picture. The shape is unchanged - hard, then out, then gone.
    public const double BlazeSeconds = 5.0;
    public const double DieSeconds = 4.0;

    /// <summary>
    /// How long the burnt-out hull takes to leave the board once the fire is
    /// out, and when that starts.
    ///
    /// <b>GDD states.md, "Уничтожен": the hex no longer blocks movement or the
    /// line of fire.</b> A knocked-out or sunk hull stays until it is finished
    /// off; a destroyed one is not a hull any more, it is an event whose
    /// consequences have been dealt to its neighbours - so the rule frees the
    /// cell, and the picture has to agree with the rule or the board lies about
    /// where a tank can drive. The scorch in the ash map (<c>Stage3D.Scald</c>)
    /// is what stays: the cell is free and looks like where one died.
    ///
    /// <b>After the fire, not after the blast.</b> A burning hull fading out
    /// reads as a bug - flames on nothing; a hull that has burnt out and then
    /// goes is the board taking the piece off. So the fade starts when
    /// <see cref="Blaze"/> reaches nought and is over two seconds later, some
    /// eleven seconds after the death. The rule itself does not wait for the
    /// picture: <see cref="Dead"/> is what frees the cell, on the frame of death.
    /// </summary>
    public const double FadeSeconds = 2.0;
    public static double GoneAt => BlazeSeconds + DieSeconds;

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
    /// Knocked out - GDD states.md "Подбит": the hull stands, does not move,
    /// does not fire, blocks movement and the line of fire, stays a target, and
    /// any hit on it whatever the armour says destroys it. The first
    /// penetration puts a tank here; see <see cref="TankTick.FateOf"/>.
    ///
    /// <b>This is what replaced three penetrations.</b> The stand counted rounds
    /// past the paint to a kill because it had no middle state to put a tank in;
    /// the rules have one, and it is this. So the matchup table still says who
    /// can hurt whom - a light gun that only scorches a heavy still cannot knock
    /// one out - and the count is gone: one round in, and the next round of any
    /// kind ends it.
    ///
    /// False again once <see cref="Kill"/> has run: "выведен из строя" is the
    /// rules' name for a hull that is still on the board to be finished, and a
    /// destroyed one is not.
    /// </summary>
    public bool Disabled { get; private set; }

    /// <summary>Knocked out or destroyed - the switch every part of a running
    /// machine reads: tremble, exhaust, scan, engine. Both states stop the
    /// engine; only the second changes the pose.</summary>
    public bool Out => Disabled || Dead;

    /// <summary>Seconds since it was knocked out, for the picture's ramps.</summary>
    public double OutAge { get; private set; }

    /// <summary>
    /// The knocked-out picture: how far the paint dims, over how long, and how
    /// thick the smoke it gives off is, over how long.
    ///
    /// <b>The pose is the wreck's - since 2026-09-08.</b> The drooped turret and
    /// the slack belts (<see cref="TankSprite.Wrecked"/>, on <see cref="Out"/>)
    /// were the destroyed tank's picture, and they are the knocked-out one's now:
    /// a hull that has stopped and gone a little darker read, at board zoom, as
    /// a live hull that happened to be standing still - which is what every live
    /// tank on this board does most of the time. A dropped gun reads from
    /// across the field; a tint does not. The destroyed tank moved on to a
    /// picture of its own - no turret at all, see <see cref="TankSprite.Turretless"/>.
    ///
    /// <b>The dimming is over half way</b>, the same shader as the wreck's char:
    /// darker and greyer than a machine that is running, and still green, with a
    /// step left between it and the black of a destroyed hull standing beside it.
    /// Was 0.30 - measured a fifth darker on the hull band, and asked for
    /// stronger.
    ///
    /// <b>The smoke is the column at half density and grey</b>
    /// (<see cref="ProcSmoke.SmoulderInk"/>), no fire under it: what says across
    /// the board that this one is out and not merely parked. Both come up over
    /// their first seconds rather than switching on - the lamp failure this
    /// file names on the wreck, and it would be the same lamp here.
    /// </summary>
    public const double Dim = 0.55;
    public const double DimSeconds = 1.0;
    public const double SmoulderDensity = 0.45;
    public const double SmoulderSeconds = 1.5;

    /// <summary>
    /// The flare: flame out of the engine deck on the hit that knocks the tank
    /// out, dying into the grey smoke over <see cref="FlareSeconds"/>.
    ///
    /// <b>A flash of fire, not a fire, and the length is the rule.</b> GDD
    /// states.md has "Горит" as a state of its own with consequences - a burning
    /// tank goes up at the end of its owner's turn, putting it out costs an
    /// activation - so a knocked-out hull drawn with a steady flame would be a
    /// hull the player reads as burning when the rules say it is not. The flare
    /// is over before the hit's own picture is: it comes up in a sixth of a
    /// second and is gone in under three, and a flame that stays past that on a
    /// knocked-out hull is the rules' fire (<c>Vehicle.Burning</c>), drawn black
    /// and full through <see cref="Blaze"/>.
    /// </summary>
    public const double FlareSeconds = 2.6;
    public const double FlareRiseSeconds = 0.16;

    /// <summary>
    /// Whether the ammunition went off, as against the fuel - which of the two
    /// deaths this was. <b>Since 2026-09-08 every death through
    /// <see cref="TankTick.Kill"/> is the rack</b> - the rules have one death,
    /// see <see cref="TankTick.RackedBy"/> - and the flag stays as the wreck's
    /// own record of it, false only on a wreck killed by a caller that said so.
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
    /// <summary>Knock it out. Refuses a second time and refuses on a wreck: a
    /// hull is knocked out once, and a destroyed one is past it.</summary>
    public bool Disable(bool seated = false)
    {
        if (Dead || Disabled)
            return false;
        Disabled = true;
        Seated = seated;
        OutAge = 0.0;
        return true;
    }

    /// <summary>
    /// Whether the turret is still on its ring - the knocked-out pose with the
    /// mount left alone.
    ///
    /// <b>What knocked a tank out says which parts of it are wrecked, and the
    /// board had one answer for every cause.</b> A round through the armour
    /// drops the gun and slackens the belts together, which is right for a round.
    /// A mine is a charge under a track: it breaks the running gear and leaves
    /// the fighting compartment whole, so the belts go slack and the turret stays
    /// where it was pointing. One flag rather than a second pose, because the
    /// wreck's parts are already separate layers - see
    /// <c>EffectLayer.Mount</c>, which is the half of the pair that reads this.
    ///
    /// False on a destroyed hull, and it cannot be otherwise:
    /// <see cref="TankSprite.Turretless"/> takes the turret off the board
    /// entirely, so there is no ring left for anything to sit on.
    /// </summary>
    public bool Seated { get; private set; }

    public bool Kill(bool racked)
    {
        if (Dead)
            return false;
        // Where the paint is when it dies - a knocked-out hull is already dimmed
        // - so the char runs on from there rather than restarting at clean. See
        // CharAtDeath.
        CharAtDeath = Char;
        Dead = true;
        Disabled = false;
        // Whatever was still bolted on is not any more - Seated's own note.
        Seated = false;
        Racked = racked;
        Age = 0.0;
        return true;
    }

    /// <summary>
    /// How charred the paint already was on the frame of death.
    ///
    /// <b>The char is a ramp from here to one, not from nought.</b> A hull
    /// finished off from knocked-out is at <see cref="Dim"/> when the round
    /// lands, and a ramp from nought put it back to clean paint on the frame of
    /// the blast - a flash of green under the fireball, a fifth of a second of
    /// the hull getting lighter before it got darker. Noticed on the events
    /// bench, pierce then ricochet. Nought on a hull killed from running, so
    /// that picture is what it was.
    /// </summary>
    public double CharAtDeath { get; private set; }

    public void Reset()
    {
        Dead = false;
        Disabled = false;
        Seated = false;
        CharAtDeath = 0.0;
        OutAge = 0.0;
        Racked = false;
        Age = 0.0;
    }

    public void Update(double delta)
    {
        if (Dead)
            Age += delta;
        else if (Disabled)
            OutAge += delta;
    }

    /// <summary>How charred the paint is, 0 to 1 - and on a knocked-out hull,
    /// how far it has dimmed toward <see cref="Dim"/>. A dead hull chars from
    /// <see cref="CharAtDeath"/> to one over <see cref="CharSeconds"/>.</summary>
    public double Char =>
        Dead ? CharAtDeath + (1.0 - CharAtDeath) * Math.Clamp(Age / CharSeconds, 0.0, 1.0)
        : Disabled ? Dim * Math.Clamp(OutAge / DimSeconds, 0.0, 1.0)
        : 0.0;

    /// <summary>How thick the knocked-out hull's smoke is: up to
    /// <see cref="SmoulderDensity"/> over <see cref="SmoulderSeconds"/>, nought
    /// on anything else - a live or a dead tank's column is <see cref="Smoke"/>.</summary>
    public double Smoulder =>
        Disabled && !Dead
            ? SmoulderDensity * Math.Clamp(OutAge / SmoulderSeconds, 0.0, 1.0)
            : 0.0;

    /// <summary>How much flare is out of the deck: up in
    /// <see cref="FlareRiseSeconds"/>, gone by <see cref="FlareSeconds"/>, and
    /// nought on anything but a knocked-out hull - a destroyed one's flame is
    /// <see cref="Blaze"/>.</summary>
    public double Flare =>
        Disabled && !Dead
            ? Math.Clamp(OutAge / FlareRiseSeconds, 0.0, 1.0)
              * Math.Pow(Math.Clamp(1.0 - OutAge / FlareSeconds, 0.0, 1.0), 1.4)
            : 0.0;

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

    /// <summary>How much of the hull is still drawn: one until the fire is out,
    /// nought <see cref="FadeSeconds"/> later. One on a live tank.</summary>
    public double Presence =>
        !Dead ? 1.0
        : 1.0 - Math.Clamp((Age - GoneAt) / FadeSeconds, 0.0, 1.0);

    /// <summary>Whether the hull has left the board altogether - the picture's
    /// half of the rule; the rule's half is <see cref="Dead"/>.</summary>
    public bool Vanished => Dead && Presence <= 0.0;

}
