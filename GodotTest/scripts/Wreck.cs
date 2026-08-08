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

    /// <summary>
    /// How far the turret is knocked round, and how far it slips off the ring.
    ///
    /// The cheapest strong read in the genre and it costs nothing at all: the
    /// turret is already its own layer with its own heading, so canting it is a
    /// number, not a frame. Deliberately not a multiple of the frame step -
    /// snapping to a rendered heading is what a live turret does, and a turret
    /// sitting square on a lane is exactly what a dead one should not look like.
    ///
    /// The slip is small on purpose. Enough to break the seam the whole pipeline
    /// was polished to close, which is the point; more than that and it reads as
    /// a layer that has come loose rather than as a turret off its ring.
    /// </summary>
    public const double Cant = 47.0;
    public const double Slip = 3.0;

    public bool Dead { get; private set; }

    /// <summary>Seconds since it died. Counted up rather than compared against a
    /// clock, for the reason the reload is: --capture and --trace fix the time
    /// step so two runs can be diffed, and a count is the same number of frames
    /// in both.</summary>
    public double Age { get; private set; }

    /// <summary>Kill it, and say whether this was the killing blow. False on a
    /// wreck, so a hull that goes on being shot at does not restart its own
    /// death - the sound, the turret's cant and the age all belong to the one
    /// round that did it.</summary>
    public bool Kill()
    {
        if (Dead)
            return false;
        Dead = true;
        Age = 0.0;
        return true;
    }

    public void Reset()
    {
        Dead = false;
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

    /// <summary>How much flame is left. One on a live tank, so a hull set
    /// burning by the key burns at full - being on fire and being dead are
    /// different things and only one of them ends.</summary>
    public double Blaze =>
        !Dead ? 1.0
        : Age <= BlazeSeconds ? 1.0
        : Math.Clamp(1.0 - (Age - BlazeSeconds) / DieSeconds, 0.0, 1.0);

    /// <summary>How much column is left: it thins with the flame and then
    /// stays.</summary>
    public double Smoke => 1.0 - (1.0 - SmokeFloor) * (1.0 - Blaze);

    /// <summary>Where the turret has been knocked to, relative to the hull, and
    /// how far it has slipped down the screen. Both come up with the char rather
    /// than snapping, so the whole thing is one settling movement.</summary>
    public double CantNow => Cant * Char;
    public double SlipNow => Slip * Char;
}
