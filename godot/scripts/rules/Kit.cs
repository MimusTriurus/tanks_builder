using System;
using System.Collections.Generic;

namespace TankSpriteTest;

/// <summary>
/// What a tank carries on top of its class - the upgrades (GDD upgrades.md):
/// equipment on one tank, fitted between battles and kept through one. A unit's
/// own, never the bench's: the wading gear was a switch for every tank on the
/// board (<c>Tank3DBench._amphibious</c>) until the match gave each unit its
/// kit (docs/battle-core-plan.md, Д1).
///
/// <b>Fourteen of upgrades.md's twenty-two have a flag here, each read by the
/// rules</b> (Д1, Д8; the 2026-11-02 revision: the trawl became the mine guard,
/// the auto-extinguisher and the double turn pay with the bonus act). The
/// eight added on 2026-11-02 are on the trees (<see cref="QuickSquad"/>) only.
/// </summary>
[Flags]
public enum Kit
{
    None = 0,
    /// <summary>ОПВТ: afloat in deep water, not drowned.</summary>
    Snorkel = 1 << 0,
    /// <summary>Противоминная защита: a mine under the hull, however it came on
    /// to it, goes off and is spent - the hull is set alight, not knocked out,
    /// and its way ends there (upgrades.md, 2026-11-02; it was the trawl).</summary>
    MineGuard = 1 << 1,
    /// <summary>Дымовая шашка: smoke on a neighbouring hex, the bonus act.</summary>
    Smoke = 1 << 2,
    /// <summary>Зажигательный снаряд: the attack's round sets alight what it meets.</summary>
    Incendiary = 1 << 3,
    /// <summary>Широкие гусеницы: mud is ground.</summary>
    WideTracks = 1 << 4,
    /// <summary>Задний ход: back two hexes, turning.</summary>
    Reverse = 1 << 5,
    /// <summary>Противоосколочный подбой: a neighbour's blast does not set it alight.</summary>
    SpallLiner = 1 << 6,
    /// <summary>Автоматическое тушение: a burning unit goes out at п. 6 by
    /// itself, for its bonus act - this turn's, if it acted and has it still,
    /// else its next activation's (<see cref="BattleUnit.OwesBonus"/>).</summary>
    Extinguisher = 1 << 7,
    /// <summary>Тепловизор: its shot and its ambush's axis see through smoke.</summary>
    Thermal = 1 << 8,
    /// <summary>Лебёдка: a hull two out along an axis pulled in, the bonus act.</summary>
    Winch = 1 << 9,
    /// <summary>Минный заградитель: a mine on a neighbouring hex, the bonus act.</summary>
    MineLayer = 1 << 10,
    /// <summary>Ремкомплект: a knocked-out own neighbour back in the fight, the bonus act.</summary>
    RepairKit = 1 << 11,
    /// <summary>Двойной поворот: the hull turned sixty degrees with no step,
    /// the bonus act, after the move (<see cref="Battle.Pivot"/>).</summary>
    DoubleTurn = 1 << 12,
    /// <summary>Стабилизатор засады: the ambush outlives a ram and its first round.</summary>
    AmbushStabilizer = 1 << 13,
}

public static class Kits
{
    /// <summary>Each upgrade: its flag, the name a map writes, the name the HUD
    /// says - in upgrades.md's order.</summary>
    public static readonly (Kit Kit, string Id, string Name)[] All =
    {
        (Kit.MineGuard, "mine_guard", L.T("kit.mine_guard")),
        (Kit.Snorkel, "snorkel", L.T("kit.snorkel")),
        (Kit.Smoke, "smoke", L.T("kit.smoke")),
        (Kit.Incendiary, "incendiary", L.T("kit.incendiary")),
        (Kit.WideTracks, "wide_tracks", L.T("kit.wide_tracks")),
        (Kit.Reverse, "reverse", L.T("kit.reverse")),
        (Kit.SpallLiner, "spall_liner", L.T("kit.spall_liner")),
        (Kit.Extinguisher, "extinguisher", L.T("kit.extinguisher")),
        (Kit.Thermal, "thermal", L.T("kit.thermal")),
        (Kit.Winch, "winch", L.T("kit.winch")),
        (Kit.MineLayer, "mine_layer", L.T("kit.mine_layer")),
        (Kit.RepairKit, "repair_kit", L.T("kit.repair_kit")),
        (Kit.DoubleTurn, "double_turn", L.T("kit.double_turn")),
        (Kit.AmbushStabilizer, "ambush_stabilizer", L.T("kit.ambush_stabilizer")),
    };

    /// <summary>
    /// The stub until the meta layer fits them (GDD match.md, «Вход и выход
    /// боя»; the author's Р-4, 2026-10-31): the starting squad's one upgrade
    /// each - LT the wading gear, MT the mine guard, HM the smoke pot. HT and TD
    /// carry nothing. Read off the class's numbers, as the class's verbs are
    /// (<see cref="MovementProfile.FreeOrder"/>, <see cref="MovementProfile.Lobs"/>).
    /// A home that names its kit (<see cref="Parking.Kit"/>) is not stubbed.
    /// </summary>
    public static Kit Starter(MovementProfile cls) => cls.Might switch
    {
        <= 1 => Kit.Snorkel,
        2 when cls.Turreted => Kit.MineGuard,
        >= 5 => Kit.Smoke,
        _ => Kit.None,
    };

    /// <summary>The kit's names as the HUD and the log write them.</summary>
    public static string Names(Kit kit)
    {
        if (kit == Kit.None)
            return "—";
        var said = new List<string>();
        foreach ((Kit k, string _, string name) in All)
            if ((kit & k) != 0)
                said.Add(name);
        return string.Join(", ", said);
    }

    /// <summary>The kit's ids as a map writes them, in <see cref="All"/>'s order.</summary>
    public static List<string> Ids(Kit kit)
    {
        var ids = new List<string>();
        foreach ((Kit k, string id, string _) in All)
            if ((kit & k) != 0)
                ids.Add(id);
        return ids;
    }

    /// <summary>The upgrade a map names, or null for a name no upgrade has.
    /// <c>trawl</c> is the mine guard's name from before 2026-11-02.</summary>
    public static Kit? Parse(string id)
    {
        if (string.Equals(id, "trawl", StringComparison.OrdinalIgnoreCase))
            return Kit.MineGuard;
        foreach ((Kit k, string name, string _) in All)
            if (string.Equals(name, id, StringComparison.OrdinalIgnoreCase))
                return k;
        return null;
    }
}
