using System;
using System.Collections.Generic;
using System.Linq;

namespace TankSpriteTest;

/// <summary>
/// The quick battle's squad (GDD meta.md, «Быстрый бой: PvP и PvAI»; the
/// author's М-5, 2026-11-02): three machines bought on a budget of points
/// before each battle - no hangar, no experience.
/// <list type="bullet">
/// <item><b>A class is free</b>; at most <see cref="SameClass"/> of one.</item>
/// <item><b>A branch's level costs its number</b>: level 1 one point, 2 two,
/// 3 three. A machine goes one branch (upgrades.md, «Дерево прокачки») and
/// carries every upgrade of it up to its level.</item>
/// <item><b>The quick start</b> - one of the operation's four companies
/// (meta.md, «Старт операции») at level 1.</item>
/// <item><b>The AI's squad</b> - its doctrine's classes and branches, its own
/// budget, blind to the player's (<see cref="ForAi"/>).</item>
/// </list>
/// The trees are the class files' (docs/gdd/units/*.md, «Дерево прокачки»).
/// An upgrade the bench has no rule for yet carries no <see cref="Kit"/> flag
/// (<see cref="Upgrade.Flag"/> null): it is bought and shown, and the machine
/// takes nothing for it into the battle.
/// </summary>
public static class QuickSquad
{
    public const int Size = 3, SameClass = 2, BudgetDefault = 6, BudgetMax = 12, CompanyLevel = 1;

    /// <summary>An upgrade on a tree: its name, what kind it is, its rule in a
    /// line, and the bench's flag for it - null where there is no rule yet;
    /// whether it is an ability and an attack, apart from the words of its kind;
    /// its picture, <c>Icons/&lt;Icon&gt;.svg</c> (docs/upgrade-icons-plan.md).</summary>
    public sealed record Upgrade(string Name, string Kind, string Text, Kit? Flag, bool Ability = false, bool Attack = false, string Icon = "");

    /// <summary>A branch of a class's tree: three levels, in order.</summary>
    public sealed record Branch(string Id, string Name, string Tag, Upgrade[] Levels);

    /// <summary>A class as the build screen says it: the profile's tag, the
    /// short name, its duty in two words (the card's head), the role and the
    /// trait (units/*.md), its two branches.</summary>
    public sealed record Class(string Tag, string Short, string Name, string Duty, string Role, string Trait, Branch[] Branches)
    {
        public MovementProfile Profile => MovementProfile.For(Tag);

        public Branch? BranchOf(string? id) => Branches.FirstOrDefault(b => b.Id == id);
    }

    /// <summary>One machine of a squad: its class (a profile tag) or none,
    /// its branch or none, its level 0..3. Level 0 is the neutral machine.</summary>
    public record struct Slot(string? Class, string? Branch, int Level)
    {
        public bool Empty => Class is null;
    }

    /// <summary>A company of the operation's start (meta.md, «Старт операции»):
    /// three machines, each with the branch it goes.</summary>
    public sealed record Company(string Name, string Play, (string Class, string Branch)[] Slots);

    /// <summary>An opponent's doctrine (meta.md, «Доктрины»): the classes its
    /// squads are made of and the branches it levels.</summary>
    public sealed record Doctrine(string Id, string Name, string[] Pool, string[] Branches, string Play);

    private static Upgrade U(string icon, string name, string kind, string text, Kit? flag = null) =>
        new(name, L.T(kind), text, flag, kind.StartsWith("squad.kind.ability", StringComparison.Ordinal), kind == "squad.kind.ability_attack", icon);

    // the upgrades the trees share, said once
    private static readonly Upgrade Snorkel = U("snorkel", L.T("squad.up.snorkel.name"), "squad.kind.passive", L.T("squad.up.snorkel.text"), Kit.Snorkel);
    private static readonly Upgrade WideTracks = U("wide_tracks", L.T("squad.up.wide_tracks.name"), "squad.kind.passive", L.T("squad.up.wide_tracks.text"), Kit.WideTracks);
    private static readonly Upgrade SmokePot = U("smoke", L.T("squad.up.smoke_pot.name"), "squad.kind.ability_bonus", L.T("squad.up.smoke_pot.text"), Kit.Smoke);
    private static readonly Upgrade Incendiary = U("incendiary", L.T("squad.up.incendiary.name"), "squad.kind.ability_attack", L.T("squad.up.incendiary.text"), Kit.Incendiary);
    private static readonly Upgrade MineLayer = U("mine", L.T("squad.up.mine_layer.name"), "squad.kind.ability_bonus", L.T("squad.up.mine_layer.text"), Kit.MineLayer);
    private static readonly Upgrade Stabilizer = U("ambush_stabilizer", L.T("squad.up.stabilizer.name"), "squad.kind.passive", L.T("squad.up.stabilizer.text"), Kit.AmbushStabilizer);
    private static readonly Upgrade Thermal = U("thermal", L.T("squad.up.thermal.name"), "squad.kind.passive", L.T("squad.up.thermal.text"), Kit.Thermal);
    private static readonly Upgrade MineGuard = U("trawl", L.T("squad.up.mine_guard.name"), "squad.kind.passive", L.T("squad.up.mine_guard.text"), Kit.MineGuard);
    private static readonly Upgrade Winch = U("winch", L.T("squad.up.winch.name"), "squad.kind.ability_bonus", L.T("squad.up.winch.text"), Kit.Winch);
    private static readonly Upgrade RepairKit = U("repair", L.T("squad.up.repair_kit.name"), "squad.kind.ability_bonus", L.T("squad.up.repair_kit.text"), Kit.RepairKit);
    private static readonly Upgrade SpallLiner = U("spall_liner", L.T("squad.up.spall_liner.name"), "squad.kind.passive", L.T("squad.up.spall_liner.text"), Kit.SpallLiner);
    private static readonly Upgrade Extinguisher = U("extinguisher", L.T("squad.up.extinguisher.name"), "squad.kind.passive_cost", L.T("squad.up.extinguisher.text"), Kit.Extinguisher);
    private static readonly Upgrade DoubleTurn = U("double_turn", L.T("squad.up.double_turn.name"), "squad.kind.ability_bonus", L.T("squad.up.double_turn.text"), Kit.DoubleTurn);
    private static readonly Upgrade Reverse = U("reverse", L.T("squad.up.reverse.name"), "squad.kind.passive", L.T("squad.up.reverse.text"), Kit.Reverse);

    /// <summary>The five classes, in <see cref="Lineup.Roster"/>'s order.</summary>
    public static readonly Class[] Classes =
    {
        new("LTP", "LT", L.T("squad.class.LTP.name"), L.T("squad.class.LTP.duty"),
            L.T("squad.class.LTP.role"),
            L.T("squad.class.LTP.trait"),
            new[]
            {
                new Branch("raid", L.T("squad.branch.raid.name"), L.T("squad.branch.raid.tag"), new[] { Snorkel, WideTracks,
                    U("fire_on_move", L.T("squad.up.shoot_on_move.name"), "squad.kind.passive_class", L.T("squad.up.shoot_on_move.text")) }),
                new Branch("sabotage", L.T("squad.branch.sabotage.name"), L.T("squad.branch.sabotage.tag"), new[] { SmokePot, Incendiary, MineLayer }),
            }),
        new("MTP", "MT", L.T("squad.class.MTP.name"), L.T("squad.class.MTP.duty"),
            L.T("squad.class.MTP.role"),
            L.T("squad.class.MTP.trait"),
            new[]
            {
                new Branch("sentry", L.T("squad.branch.sentry.name"), L.T("squad.branch.sentry.tag"), new[] { Stabilizer, Thermal,
                    U("wide_sector", L.T("squad.up.wide_sector.name"), "squad.kind.passive_class", L.T("squad.up.wide_sector.text")) }),
                new Branch("prop", L.T("squad.branch.prop.name"), L.T("squad.branch.prop.tag"), new[] { MineGuard, Winch, RepairKit }),
            }),
        new("HTP", "HT", L.T("squad.class.HTP.name"), L.T("squad.class.HTP.duty"),
            L.T("squad.class.HTP.role"),
            L.T("squad.class.HTP.trait"),
            new[]
            {
                new Branch("ram", L.T("squad.branch.ram.name"), L.T("squad.branch.ram.tag"), new[]
                {
                    U("overdrive", L.T("squad.up.boost.name"), "squad.kind.ability_premove", L.T("squad.up.boost.text"), Kit.Overdrive),
                    U("dozer_blade", L.T("squad.up.ram_blade.name"), "squad.kind.passive", L.T("squad.up.ram_blade.text"), Kit.RamBlade),
                    U("push_two", L.T("squad.up.double_push.name"), "squad.kind.passive_class", L.T("squad.up.double_push.text"), Kit.PushTwo),
                }),
                new Branch("storm", L.T("squad.branch.storm.name"), L.T("squad.branch.storm.tag"), new[] { SpallLiner, MineGuard, Extinguisher }),
            }),
        new("TDP", "TD", L.T("squad.class.TDP.name"), L.T("squad.class.TDP.duty"),
            L.T("squad.class.TDP.role"),
            L.T("squad.class.TDP.trait"),
            new[]
            {
                new Branch("aim", L.T("squad.branch.aim.name"), L.T("squad.branch.aim.tag"), new[] { DoubleTurn, Reverse,
                    U("through_wood", L.T("squad.up.through_forest.name"), "squad.kind.passive_class", L.T("squad.up.through_forest.text"), Kit.ThroughWood) }),
                new Branch("ambush", L.T("squad.branch.ambush.name"), L.T("squad.branch.ambush.tag"), new[] { Thermal, Stabilizer, SmokePot }),
            }),
        new("HMP", "HM", L.T("squad.class.HMP.name"), L.T("squad.class.HMP.duty"),
            L.T("squad.class.HMP.role"),
            L.T("squad.class.HMP.trait"),
            new[]
            {
                new Branch("support", L.T("squad.branch.support.name"), L.T("squad.branch.support.tag"), new[]
                {
                    U("smoke_shell", L.T("squad.up.smoke_round.name"), "squad.kind.ability_attack", L.T("squad.up.smoke_round.text")),
                    Incendiary,
                    U("fork_36", L.T("squad.up.bracket.name"), "squad.kind.passive_class", L.T("squad.up.bracket.text"), Kit.Bracket),
                }),
                new Branch("guard", L.T("squad.branch.guard.name"), L.T("squad.branch.guard.tag"), new[] { SmokePot, Reverse, DoubleTurn }),
            }),
    };

    /// <summary>The operation's four companies (meta.md, «Старт операции»),
    /// «Сводная» first: the timer's fill in PvP.</summary>
    public static readonly Company[] Companies =
    {
        new(L.T("squad.company.composite.name"), L.T("squad.company.composite.play"), new[] { ("LTP", "raid"), ("MTP", "prop"), ("HMP", "guard") }),
        new(L.T("squad.company.armour.name"), L.T("squad.company.armour.play"), new[] { ("HTP", "ram"), ("TDP", "aim"), ("MTP", "sentry") }),
        new(L.T("squad.company.recon.name"), L.T("squad.company.recon.play"), new[] { ("LTP", "raid"), ("LTP", "sabotage"), ("MTP", "sentry") }),
        new(L.T("squad.company.artillery.name"), L.T("squad.company.artillery.play"), new[] { ("HMP", "support"), ("MTP", "prop"), ("TDP", "ambush") }),
    };

    /// <summary>The three doctrines (meta.md, «Доктрины»).</summary>
    public static readonly Doctrine[] Doctrines =
    {
        new("armour", L.T("squad.doctrine.armour.name"), new[] { "HTP", "TDP", "MTP" }, new[] { "ram", "storm", "aim" },
            L.T("squad.doctrine.armour.play")),
        new("recon", L.T("squad.doctrine.recon.name"), new[] { "LTP", "MTP" }, new[] { "raid", "sabotage", "sentry" },
            L.T("squad.doctrine.recon.play")),
        new("artillery", L.T("squad.doctrine.artillery.name"), new[] { "HMP", "MTP", "TDP" }, new[] { "support", "prop", "ambush" },
            L.T("squad.doctrine.artillery.play")),
    };

    public static Class ClassOf(string tag) =>
        Classes.FirstOrDefault(c => string.Equals(c.Tag, tag, StringComparison.OrdinalIgnoreCase)) ?? Classes[1];

    public static Slot[] Empty() => new Slot[Size];

    /// <summary>The points a squad has spent: its levels summed.</summary>
    public static int Spent(IReadOnlyList<Slot> squad) => squad.Sum(s => s.Empty ? 0 : s.Level);

    /// <summary>How many of the squad drive <paramref name="tag"/>.</summary>
    public static int CountOf(IReadOnlyList<Slot> squad, string tag) =>
        squad.Count(s => string.Equals(s.Class, tag, StringComparison.OrdinalIgnoreCase));

    /// <summary>Why <paramref name="tag"/> may not take slot <paramref name="at"/>, or empty.</summary>
    public static string ClassWhy(IReadOnlyList<Slot> squad, int at, string tag)
    {
        int others = 0;
        for (int i = 0; i < squad.Count; i++)
            if (i != at && string.Equals(squad[i].Class, tag, StringComparison.OrdinalIgnoreCase))
                others++;
        return others >= SameClass ? L.T("squad.why.same_class", SameClass) : "";
    }

    /// <summary>Why slot <paramref name="at"/> may not go to <paramref name="level"/>
    /// of <paramref name="branch"/> on <paramref name="budget"/>, or empty: the
    /// branch taken is kept (another one only from level nought), and the
    /// level's points must be in the budget.</summary>
    public static string LevelWhy(IReadOnlyList<Slot> squad, int at, string branch, int level, int budget)
    {
        Slot s = squad[at];
        if (s.Empty)
            return L.T("squad.why.class_first");
        if (level < 0 || level > 3)
            return L.T("squad.why.three_levels");
        int others = Spent(squad) - s.Level;
        return others + level > budget ? L.T("squad.why.no_points", level, budget - others) : "";
    }

    /// <summary>The upgrades a slot carries: its branch's levels up to its own.</summary>
    public static IEnumerable<Upgrade> UpgradesOf(Slot s)
    {
        if (s.Empty || s.Level <= 0 || ClassOf(s.Class!).BranchOf(s.Branch) is not { } b)
            yield break;
        for (int i = 0; i < Math.Min(s.Level, b.Levels.Length); i++)
            yield return b.Levels[i];
    }

    /// <summary>The kit the slot's machine takes into the battle: the flags of
    /// its upgrades that have a rule on the bench.</summary>
    public static Kit KitOf(Slot s)
    {
        Kit kit = Kit.None;
        foreach (Upgrade u in UpgradesOf(s))
            if (u.Flag is Kit k)
                kit |= k;
        return kit;
    }

    /// <summary>A budget cut below what is spent takes the top levels off,
    /// the highest first (meta.md: the squad is kept, the excess goes).</summary>
    public static void Trim(Slot[] squad, int budget)
    {
        int over = Spent(squad) - budget;
        while (over > 0)
        {
            int top = -1;
            for (int i = 0; i < squad.Length; i++)
                if (!squad[i].Empty && squad[i].Level > 0 && (top < 0 || squad[i].Level > squad[top].Level))
                    top = i;
            if (top < 0)
                return;
            squad[top].Level--;
            over--;
        }
    }

    /// <summary>The empty slots filled by «Сводная»'s classes, neutral (the
    /// timer run out, meta.md): what the squad has not two of yet.</summary>
    public static void Fill(Slot[] squad)
    {
        string[] fallback = Companies[0].Slots.Select(s => s.Class).ToArray();
        for (int i = 0; i < squad.Length; i++)
            if (squad[i].Empty)
                squad[i] = new Slot(fallback.FirstOrDefault(c => CountOf(squad, c) < SameClass) ?? "MTP", null, 0);
    }

    /// <summary>A company as a squad, every machine at <see cref="CompanyLevel"/>.</summary>
    public static Slot[] OfCompany(Company c) =>
        c.Slots.Select(s => new Slot(s.Class, s.Branch, CompanyLevel)).ToArray();

    /// <summary>
    /// The AI's squad (meta.md, «Быстрый бой»): blind to the player's - by its
    /// doctrine and its budget alone. Classes drawn from the doctrine's pool,
    /// no more than <see cref="SameClass"/> of one; each its class's branch the
    /// doctrine levels (the first where it levels none); the points dealt a
    /// level at a time round the squad, up to level 3 each.
    /// </summary>
    public static Slot[] ForAi(Doctrine doctrine, int budget, Random rng)
    {
        var squad = new Slot[Size];
        for (int i = 0; i < Size; i++)
        {
            string[] open = doctrine.Pool.Where(c => CountOf(squad, c) < SameClass).ToArray();
            string tag = open.Length > 0 ? open[rng.Next(open.Length)] : doctrine.Pool[0];
            Branch[] own = ClassOf(tag).Branches.Where(b => doctrine.Branches.Contains(b.Id)).ToArray();
            Branch branch = own.Length > 0 ? own[rng.Next(own.Length)] : ClassOf(tag).Branches[0];
            squad[i] = new Slot(tag, branch.Id, 0);
        }
        // a level costs its number in all, so a level up costs one more point
        int left = budget;
        for (int k = 0; left > 0 && k < 3 * Size; k++)
        {
            ref Slot s = ref squad[k % Size];
            if (s.Level < 3)
            {
                s.Level++;
                left--;
            }
        }
        return squad;
    }

    /// <summary>The squad in a line, for the log and the reveal.</summary>
    public static string Said(IReadOnlyList<Slot> squad) =>
        string.Join(", ", squad.Select(s => s.Empty ? "—"
            : $"{ClassOf(s.Class!).Short}" + (s.Level > 0 && ClassOf(s.Class!).BranchOf(s.Branch) is { } b ? $" {b.Name} {s.Level}" : " нейтральный")));
}
