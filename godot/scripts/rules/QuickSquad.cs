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
    /// line, and the bench's flag for it - null where there is no rule yet.</summary>
    public sealed record Upgrade(string Name, string Kind, string Text, Kit? Flag);

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

    private static Upgrade U(string name, string kind, string text, Kit? flag = null) => new(name, kind, text, flag);

    // the upgrades the trees share, said once
    private static readonly Upgrade Snorkel = U("ОПВТ", "пассивный", "В глубокой воде танк плывёт, а не тонет. Выезд на берег с любой стороны.", Kit.Snorkel);
    private static readonly Upgrade WideTracks = U("Широкие гусеницы", "пассивный", "Грязь для танка — обычная земля: не останавливает и не режет шаги.", Kit.WideTracks);
    private static readonly Upgrade SmokePot = U("Дымовая шашка", "способность · доп. действие", "Дым на соседний гекс. На лес и на танк не ложится.", Kit.Smoke);
    private static readonly Upgrade Incendiary = U("Зажигательный снаряд", "способность · атака", "Вместо обычного выстрела. Лес загорается, непробитый танк загорается.", Kit.Incendiary);
    private static readonly Upgrade MineLayer = U("Минный заградитель", "способность · доп. действие", "Мина на соседний гекс. Видна всем и бьёт и своих.", Kit.MineLayer);
    private static readonly Upgrade Stabilizer = U("Стабилизатор засады", "пассивный", "Засада не снимается тараном и не тратится первым выстрелом. У MT даёт только защиту от тарана.", Kit.AmbushStabilizer);
    private static readonly Upgrade Thermal = U("Тепловизор", "пассивный", "Свой выстрел и ось засады идут сквозь дым.", Kit.Thermal);
    private static readonly Upgrade MineGuard = U("Противоминная защита", "пассивный", "Мина не подбивает, а поджигает танк. Путь обрывается на гексе мины.");
    private static readonly Upgrade Winch = U("Лебёдка", "способность · доп. действие", "Притягивает танк с дистанции 2 по оси на гекс между. Цель не тяжелее.", Kit.Winch);
    private static readonly Upgrade RepairKit = U("Ремкомплект", "способность · доп. действие", "Соседний свой подбитый танк снова активен. Утопленного не поднять.", Kit.RepairKit);
    private static readonly Upgrade SpallLiner = U("Противоосколочный подбой", "пассивный", "Взрыв соседнего танка этот танк не поджигает.", Kit.SpallLiner);
    private static readonly Upgrade Extinguisher = U("Автоматическое тушение", "пассивный, с ценой", "Горящий танк гаснет сам. Цена — одно дополнительное действие.", Kit.Extinguisher);
    private static readonly Upgrade DoubleTurn = U("Двойной поворот", "способность · доп. действие", "Поворот корпуса на 60° без шага, после движения.", Kit.DoubleTurn);
    private static readonly Upgrade Reverse = U("Задний ход", "пассивный", "Назад до двух клеток и с поворотом.", Kit.Reverse);

    /// <summary>The five classes, in <see cref="Lineup.Roster"/>'s order.</summary>
    public static readonly Class[] Classes =
    {
        new("LTP", "LT", "Лёгкий танк", "разведка и фланг",
            "Охотник и разведчик. В лоб не пробивает никого, живёт огнём в борт и корму и подвижностью.",
            "Свободный порядок: может выстрелить до движения и уйти.",
            new[]
            {
                new Branch("raid", "Рейд", "подвижность", new[] { Snorkel, WideTracks,
                    U("Выстрел на ходу", "пассивный, классовый", "Атака LT на любом гексе пути, между шагами. Засада на ходу не ставится.") }),
                new Branch("sabotage", "Диверсия", "инструменты", new[] { SmokePot, Incendiary, MineLayer }),
            }),
        new("MTP", "MT", "Средний танк", "засада и опора",
            "Рабочая лошадка и часовой, середина во всём.",
            "Секторная засада: караулит две соседние оси, на каждой свой выстрел.",
            new[]
            {
                new Branch("sentry", "Часовой", "засада", new[] { Stabilizer, Thermal,
                    U("Широкий сектор", "пассивный, классовый", "Секторная засада на три оси (120°), выстрелов по-прежнему два.") }),
                new Branch("prop", "Опора", "отряд", new[] { MineGuard, Winch, RepairKit }),
            }),
        new("HTP", "HT", "Тяжёлый танк", "прорыв",
            "Якорь и бульдозер. В лоб его берут только TD и HM.",
            "Таранит с ходу, посреди пути; единственный, кто убирает укрытия с карты.",
            new[]
            {
                new Branch("ram", "Таран", "убрать с пути", new[]
                {
                    U("Форсированный двигатель", "способность · до движения", "В этот ход пересечение оси засады не вызывает выстрела, если танк не остановился на ней."),
                    U("Таранный отвал", "пассивный", "Протараненный теряет движение в свою ближайшую активацию."),
                    U("Толчок на два гекса", "пассивный, классовый", "Таран отбрасывает цель на два гекса. Стены на пути сносятся."),
                }),
                new Branch("storm", "Штурм", "дойти", new[] { SpallLiner, MineGuard, Extinguisher }),
            }),
        new("TDP", "TD", "Истребитель танков", "огонь сквозь преграду",
            "Огневая мощь через препятствие. Башни нет, наводится корпусом за шаги.",
            "Тяжёлый снаряд проходит сквозь одну стену или один танк.",
            new[]
            {
                new Branch("aim", "Наводка", "корпус", new[] { DoubleTurn, Reverse,
                    U("Сквозь лес", "пассивный, классовый", "Тяжёлый снаряд проходит гекс леса с мощью −1. Лес не горит.") }),
                new Branch("ambush", "Засада", "ожидание", new[] { Thermal, Stabilizer, SmokePot }),
            }),
        new("HMP", "HM", "Тяжёлый миномёт", "навес издалека",
            "Артиллерия. Пробивает всех, но гибнет от любого попадания в борт.",
            "Навес на 3–5 гексов поверх всего; мёртвая зона 1–2.",
            new[]
            {
                new Branch("support", "Поддержка", "навес", new[]
                {
                    U("Дымовой снаряд", "способность · атака", "Дым на гекс оси вместо выстрела, навесом на 3–5."),
                    Incendiary,
                    U("Вилка 3–6", "пассивный, классовый", "Дальность миномёта 6 вместо 5. Мёртвая зона 1–2 та же."),
                }),
                new Branch("guard", "Самооборона", "выжить", new[] { SmokePot, Reverse, DoubleTurn }),
            }),
    };

    /// <summary>The operation's four companies (meta.md, «Старт операции»),
    /// «Сводная» first: the timer's fill in PvP.</summary>
    public static readonly Company[] Companies =
    {
        new("Сводная", "всего понемногу", new[] { ("LTP", "raid"), ("MTP", "prop"), ("HMP", "guard") }),
        new("Бронегруппа", "давление и прорыв", new[] { ("HTP", "ram"), ("TDP", "aim"), ("MTP", "sentry") }),
        new("Разведрота", "обход и засады", new[] { ("LTP", "raid"), ("LTP", "sabotage"), ("MTP", "sentry") }),
        new("Артдивизион", "дистанция и навес", new[] { ("HMP", "support"), ("MTP", "prop"), ("TDP", "ambush") }),
    };

    /// <summary>The three doctrines (meta.md, «Доктрины»).</summary>
    public static readonly Doctrine[] Doctrines =
    {
        new("armour", "Бронегруппа", new[] { "HTP", "TDP", "MTP" }, new[] { "ram", "storm", "aim" },
            "Медленно давит, ломает укрытия"),
        new("recon", "Разведбат", new[] { "LTP", "MTP" }, new[] { "raid", "sabotage", "sentry" },
            "Обходит, ставит дым, мины и засады"),
        new("artillery", "Артдивизион", new[] { "HMP", "MTP", "TDP" }, new[] { "support", "prop", "ambush" },
            "Держит дистанцию, бьёт навесом, чинит своих"),
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
        return others >= SameClass ? $"не больше {SameClass} машин одного класса" : "";
    }

    /// <summary>Why slot <paramref name="at"/> may not go to <paramref name="level"/>
    /// of <paramref name="branch"/> on <paramref name="budget"/>, or empty: the
    /// branch taken is kept (another one only from level nought), and the
    /// level's points must be in the budget.</summary>
    public static string LevelWhy(IReadOnlyList<Slot> squad, int at, string branch, int level, int budget)
    {
        Slot s = squad[at];
        if (s.Empty)
            return "сначала класс";
        if (level < 0 || level > 3)
            return "уровней три";
        int others = Spent(squad) - s.Level;
        return others + level > budget ? $"не хватает очков: уровень {level} стоит {level}, свободно {budget - others}" : "";
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
