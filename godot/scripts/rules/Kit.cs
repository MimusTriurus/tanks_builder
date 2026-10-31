using System;

namespace TankSpriteTest;

/// <summary>
/// What a tank carries on top of its class - the upgrades (GDD upgrades.md):
/// equipment on one tank, fitted between battles and kept through one. A unit's
/// own, never the bench's: the wading gear was a switch for every tank on the
/// board (<c>Tank3DBench._amphibious</c>) until the match gave each unit its
/// kit (docs/battle-core-plan.md, Д1).
///
/// <b>Read so far: the wading gear alone.</b> The trawl and the smoke pot are
/// fitted as the starting squad has them and read by nothing yet - the bench
/// sets mines off under any hull and has no smoke (stage Д8). The rest of
/// upgrades.md comes in with them.
/// </summary>
[Flags]
public enum Kit
{
    None = 0,
    /// <summary>ОПВТ: afloat in deep water, not drowned.</summary>
    Snorkel = 1,
    /// <summary>Минный трал: a mine driven over nose first is cleared, not set off.</summary>
    Trawl = 2,
    /// <summary>Дымовая шашка: smoke on a neighbouring hex, the bonus act.</summary>
    Smoke = 4,
}

public static class Kits
{
    /// <summary>
    /// The stub until the meta layer fits them (GDD match.md, «Вход и выход
    /// боя»; the author's Р-4, 2026-10-31): the starting squad's one upgrade
    /// each - LT the wading gear, MT the trawl, HM the smoke pot. HT and TD
    /// carry nothing. Read off the class's numbers, as the class's verbs are
    /// (<see cref="MovementProfile.FreeOrder"/>, <see cref="MovementProfile.Lobs"/>).
    /// </summary>
    public static Kit Starter(MovementProfile cls) => cls.Might switch
    {
        <= 1 => Kit.Snorkel,
        2 when cls.Turreted => Kit.Trawl,
        >= 5 => Kit.Smoke,
        _ => Kit.None,
    };

    /// <summary>The kit's names as the HUD and the log write them.</summary>
    public static string Names(Kit kit)
    {
        if (kit == Kit.None)
            return "—";
        string s = "";
        if ((kit & Kit.Snorkel) != 0) s += "ОПВТ";
        if ((kit & Kit.Trawl) != 0) s += (s.Length > 0 ? ", " : "") + "трал";
        if ((kit & Kit.Smoke) != 0) s += (s.Length > 0 ? ", " : "") + "шашка";
        return s;
    }
}
