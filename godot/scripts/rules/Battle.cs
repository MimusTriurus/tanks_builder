using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace TankSpriteTest;

/// <summary>The two sides of a match. Not <c>Side</c>: outside the bench that
/// name is <c>Godot.Side</c>.</summary>
public enum Camp { Green, Brown }

/// <summary>A head start for player 2 under trial (docs/ai-plan.md, вопрос 7):
/// not a rule yet, only <c>--battle-run --handicap</c> sets it.
/// <list type="bullet">
/// <item><b>Ambush</b> - player 2's guns start the match in ambush along the hull;</item>
/// <item><b>Hold</b> - the match's first turn fires no round;</item>
/// <item><b>Snake</b> - the round's lead swaps each round: 1-2, 2-1, 1-2…</item>
/// </list></summary>
public enum Handicap { None, Ambush, Hold, Snake }

/// <summary>A unit's state by the rules (GDD states.md). <see cref="Knocked"/>
/// and <see cref="Drowned"/> are both «выведен из строя»: they stand, block and
/// go up at the next round; the drowned cannot be put right (upgrades.md).</summary>
public enum UnitFate { Alive, Knocked, Drowned, Destroyed }

/// <summary>One unit of a match as the rules see it.</summary>
public sealed class BattleUnit
{
    public int Id;
    public string Tag = "";
    public MovementProfile Class = MovementProfile.Medium;
    public Camp Side;
    public Vector2I Cell;
    /// <summary>The hull's flat side, one of <see cref="HexField.EdgeHeadings"/>.</summary>
    public int Heading = 90;
    public UnitFate Fate = UnitFate.Alive;
    public bool Burning;
    /// <summary>The turn it caught fire in (<see cref="BattleState.TurnNo"/>).</summary>
    public int BurnSince;
    public Kit Kit;
    /// <summary>The ambush's axis and, for the medium's sector, the second one
    /// beside it (units.md, «Режим засады»; classes.md, «MT») and, with the
    /// wide sector (upgrades.md), the third; -1 for none. A sector's axis that
    /// has fired is struck off, the rest moved up (units/mt.md).</summary>
    public int Watch = -1, Watch2 = -1, Watch3 = -1;
    /// <summary>Rounds the ambush has left: one, two with the stabilizer
    /// (upgrades.md, «Стабилизатор засады»), two for a sector - one an axis,
    /// any two of the wide sector's three; nought reads as one.</summary>
    public int WatchShots;
    /// <summary>The auto-extinguisher put its fire out on credit (upgrades.md,
    /// «Автоматическое тушение»): its next activation goes without the bonus
    /// act, and that pays it.</summary>
    public bool OwesBonus;
    /// <summary>Rammed by a hull with the ram blade (upgrades.md, «Таранный
    /// отвал»): its next activation has no move - no drive, no turn of the
    /// hull; a second ram before it adds nothing.</summary>
    public bool Pinned;

    public bool Out => Fate is UnitFate.Knocked or UnitFate.Drowned;
    public bool Standing => Fate != UnitFate.Destroyed;
    public bool Ambushing => Watch >= 0;

    public BattleUnit Clone() => (BattleUnit)MemberwiseClone();

    public override string ToString() => Say(Vector2I.Zero);

    /// <summary>The unit in a line, its cell less <paramref name="shift"/> -
    /// the map's cell on a board in a sea (<see cref="BattleState.Shift"/>).</summary>
    public string Say(Vector2I shift) => $"{Side} {Tag}#{Id} ({Cell.X - shift.X},{Cell.Y - shift.Y}) h{Heading} {Fate}{(Burning ? " burning" : "")}"
                                         + (Ambushing ? $" watching {Watch}{(Watch2 >= 0 ? $"+{Watch2}" : "")}{(Watch3 >= 0 ? $"+{Watch3}" : "")}{(WatchShots > 1 ? $" x{WatchShots}" : "")}" : "")
                                         + (OwesBonus ? " owes the bonus act" : "")
                                         + (Pinned ? " pinned" : "");
}

public enum BattleEventKind
{
    Moved, Turned, MineBlown, MineCleared, Hit, Glanced, Ricochet, Passed, Spent,
    Knocked, Drowned, Exploded, Ignited, Doused, Afloat,
    WoodAlight, WoodBurnt, WoodCleared, WallDown, Shoved, RamRefused,
    TurnEnded, FieldTick, Won, Drawn, Held,
    AmbushSet, AmbushFired, AmbushLifted,
    SmokeLaid, SmokeGone,
    MineLaid, Repaired, Winched,
    /// <summary>The auto-extinguisher's price: Value 1 - paid with this turn's
    /// bonus act, 0 - owed to the unit's next activation; Note says which.</summary>
    BonusOwed,
    /// <summary>A hull rammed by the ram blade: no move in its next activation.</summary>
    Pinned,
    /// <summary>The forced engine said: the move to come is fired on only where it stops.</summary>
    Overdrive,
    /// <summary>The dash said: the attack given up, the move to come the longer one.</summary>
    Dash,
    /// <summary>The shot on the move said: fired as the move comes on to Cell; Note the aim.</summary>
    OnWay,
    /// <summary>The tide (trial, <see cref="BattleState.Tide"/>): Cell went
    /// under the sea; Value the ring.</summary>
    Flooded,
}

/// <summary>What happened, in the order it happened: the protocol the bench
/// will play (docs/battle-core-plan.md, Д4) and the log to read a turn by.</summary>
public readonly record struct BattleEvent(BattleEventKind Kind, int Unit, Vector2I Cell, int Value = 0, string Note = "")
{
    public override string ToString() =>
        $"{Kind}{(Unit >= 0 ? $" #{Unit}" : "")} ({Cell.X},{Cell.Y}){(Value != 0 ? $" {Value}" : "")}{(Note.Length > 0 ? $" {Note}" : "")}";
}

/// <summary>
/// A match as data (docs/battle-core-plan.md, Д3): the board, the units, whose
/// turn and what it has spent, the woods on fire. Cloned whole for a search;
/// changed only by <see cref="Battle"/>.
/// </summary>
public sealed class BattleState
{
    public BattleBoard Board = null!;
    /// <summary>How far the map's cells moved on this board: the relief's sea
    /// round it (<see cref="BoardMap.Moated"/>, <see cref="BoardMap.Shift"/>),
    /// (0, 0) without one. The board and the units are in its cells;
    /// <see cref="Say"/> speaks in the map's, as the bench prints them.</summary>
    public Vector2I Shift;
    public List<BattleUnit> Units = new();
    public Camp Turn = Camp.Green;
    /// <summary>Player 1 (match.md, «Расстановка и первый ход»): the side
    /// that moves first in every round - a toss in the quick battle. The round and the
    /// field's tick end after the other side's turn.</summary>
    public Camp First = Camp.Green;
    /// <summary>Player 2's head start under trial; <see cref="Handicap.None"/> is the GDD.</summary>
    public Handicap Handicap;
    /// <summary>The stalemate on count under trial (<c>--battle-run …
    /// --stalemate count</c>, docs/ai-plan.md): the side with more units able
    /// to fight wins it, an even count is a draw. Off is the GDD's draw.</summary>
    public bool StaleByCount;
    /// <summary>The tide under trial (<c>--battle-run … --stalemate tide</c>,
    /// docs/ai-plan.md, «Прилив»): a stalemate is no draw, the sea rises a
    /// ring instead - every cell of the next ring in from the sea becomes
    /// deep water, a unit on it drowned or, with the snorkel, afloat. Off is
    /// the GDD's draw.</summary>
    public bool Tide;
    /// <summary>Quiet rounds on the tide's clock to a rise (<see cref="TideQuiet"/>).</summary>
    public int TideStep = MatchRules.QuietRoundsDraw;
    /// <summary>Rings the sea has risen (<see cref="Tide"/>).</summary>
    public int TideLevel;
    /// <summary>The round the sea last rose in, nought for never.</summary>
    public int LastTide;
    /// <summary>Each cell's ring: hex steps from the sea, the sea nought;
    /// worked out at the first ask, then shared by every clone.</summary>
    public int[]? TideRings;
    /// <summary>The sea's level, the flooded cells' (<see cref="TideRings"/>).</summary>
    public int TideSea;
    /// <summary>Rounds into the match before the first contact after which
    /// the tide's clock runs anyway: no rise waits on the contact for ever.</summary>
    public const int TideStart = 6;
    public int Round = 1;
    /// <summary>Turns played, the first nought: a fire's clock.</summary>
    public int TurnNo;
    /// <summary>The unit this turn belongs to, once it has been taken.</summary>
    public int? Active;
    public bool Drove, Fired, Bonus;
    /// <summary>The forced engine said this turn (upgrades.md, «Форсированный
    /// двигатель»): the move's crossings of ambush axes draw no fire, only
    /// where it stops (<see cref="Battle.Overdrive"/>).</summary>
    public bool Overdrive;
    /// <summary>The dash said this turn (match.md, «Структура хода», п. 2):
    /// the attack given up for a move of speed × 1.5 up - <c>Fired</c> is set
    /// with it, and the move is still to be had (<see cref="Battle.Dash"/>).</summary>
    public bool Dash;
    /// <summary>The dash is in the rules (<c>--dash</c>, a trial until the
    /// measure in docs/ai-plan.md, «Рывок», settles it).</summary>
    public bool DashOn;
    /// <summary>The dash's budget under trial (<c>--dash-plus</c>): speed + 1
    /// rather than × 1.5 up (docs/ai-plan.md, «Рывок»: the first move won
    /// river12 47 : 12 with × 1.5).</summary>
    public bool DashPlusOne;
    /// <summary>The first round the dash may be said in (<c>--dash-late</c>: 2,
    /// none in the opening round); 1 is the rule as written.</summary>
    public int DashFrom = 1;
    /// <summary>The shot on the move said this turn (upgrades.md, «Выстрел на
    /// ходу»): the move fires at <c>Aim</c> as the hull comes on to
    /// <c>At</c> (<see cref="Battle.OnTheWay"/>).</summary>
    public (Vector2I At, Vector2I Aim)? OnWay;
    /// <summary>The round of the last hit on a tank or ram of one, nought for
    /// none yet - the first is the contact the stalemate's clock waits for
    /// (GameModes.md, «Пат — ничья»).</summary>
    public int LastStrike;
    /// <summary>The round a unit of each side was last knocked out, drowned,
    /// destroyed or set alight, nought for none yet - not a rule: the AI's
    /// clock of a match going nowhere (docs/ai-plan.md, И2-И3). A round that
    /// only glances off a tank puts the stalemate's clock back, not this one;
    /// a side's own loss does not either (<see cref="HarmlessFor"/>).</summary>
    public int LastHarmGreen, LastHarmBrown;
    /// <summary>The control point's hexes (match.md, «Удержание точки»); null in
    /// a match played to the last tank.</summary>
    public Vector2I[]? Point;
    /// <summary>Each side's points left, by <see cref="Camp"/>; read only with a
    /// <see cref="Point"/>.</summary>
    public int[] Score = new int[2];
    /// <summary>Who held the point at the last field tick; null for nobody.</summary>
    public Camp? Holder;
    /// <summary>The round the holding last changed hands in, nought for never -
    /// the stalemate's other clock (match.md, «Пат — ничья»).</summary>
    public int LastShift;
    public Camp? Won;
    public bool Drawn;
    /// <summary>Burning woods and the rounds they have left (2 when lit, GDD match.md).</summary>
    public Dictionary<Vector2I, int> Fires = new();
    /// <summary>Smoke and the rounds it has left (2 when laid, field.md «дым»); the
    /// board says where it is (<see cref="BattleBoard.Smoked"/>).</summary>
    public Dictionary<Vector2I, int> Smoke = new();
    public List<BattleEvent> Events = new();

    public bool Over => Won is not null || Drawn;

    /// <summary>Whole rounds played since the last hit on a tank, a ram of
    /// one or the point changing hands - the stalemate's count, the one under
    /// way not yet in it; nought before the first of them, the contact
    /// (GameModes.md, «Пат — ничья», decided 2026-11-05).</summary>
    public int QuietRounds => LastStrike == 0 && LastShift == 0
        ? 0 : Math.Max(0, Round - 1 - Math.Max(LastStrike, LastShift));

    /// <summary>Whole rounds since <paramref name="me"/> last harmed the
    /// enemy or the point changed hands (<see cref="LastHarmGreen"/>) - the
    /// AI's clock, not the stalemate's. Its own losses do not wind it back:
    /// a side ahead, pressed by it, would else throw a tank away to be let
    /// off (docs/ai-plan.md, И3: a heavy driven into the sea).</summary>
    /// <summary>The tide's clock (<see cref="Tide"/>): whole rounds since the
    /// last hit on a tank, ram, change of the point or rise - and before the
    /// contact, since <see cref="TideStart"/>.</summary>
    public int TideQuiet => Math.Max(0, Round - 1 - Math.Max(Math.Max(LastStrike, LastShift),
        Math.Max(LastTide, LastStrike == 0 && LastShift == 0 ? TideStart : 0)));

    /// <summary>The ring of <paramref name="cell"/> (<see cref="TideRings"/>):
    /// the sea nought, the board's heart the most.</summary>
    public int Ring(Vector2I cell)
    {
        TideRings ??= Battle.Rings(Board, out TideSea);
        return Board.InBounds(cell) ? TideRings[cell.Y * Board.Columns + cell.X] : 0;
    }

    public int HarmlessFor(Camp me) =>
        Math.Max(0, Round - 1 - Math.Max(me == Camp.Green ? LastHarmBrown : LastHarmGreen, LastShift));

    /// <summary><paramref name="u"/> harmed this round (<see cref="HarmlessFor"/>).</summary>
    public void Harmed(BattleUnit u)
    {
        if (u.Side == Camp.Green)
            LastHarmGreen = Round;
        else
            LastHarmBrown = Round;
    }

    public BattleUnit? Unit(int id)
    {
        foreach (BattleUnit u in Units)
            if (u.Id == id)
                return u;
        return null;
    }

    /// <summary>The unit standing on a cell - not a destroyed one, which is no
    /// longer in anybody's way (states.md, «Уничтожен»).</summary>
    public BattleUnit? At(Vector2I cell)
    {
        foreach (BattleUnit u in Units)
            if (u.Standing && u.Cell == cell)
                return u;
        return null;
    }

    /// <summary>
    /// The match as text, a line a fact, in a fixed order (Д10): whose turn,
    /// the outcome, the point, each unit, the burning woods and the smoke,
    /// each wood, wall and mine not as laid. Two states that say the same
    /// are the same match - the core's run and the bench's are compared by
    /// it. A wood's rounds left are not in it: the bench does not keep them.
    /// </summary>
    public string Say()
    {
        var t = new System.Text.StringBuilder();
        t.Append($"round {Round}, turn {TurnNo}, {Turn} to move, player 1 {First}\n");
        t.Append($"outcome {(Drawn ? "drawn" : Won is Camp w ? $"{w} won" : "on")}, last shot at a tank in round {LastStrike}\n");
        if (Point is not null)
            t.Append($"point {Holder?.ToString() ?? "nobody"} holds, green {Score[0]}, brown {Score[1]}, since round {LastShift}\n");
        foreach (BattleUnit u in Units)
            t.Append(u.Say(Shift)).Append('\n');
        for (int r = 0; r < Board.Rows; r++)
            for (int q = 0; q < Board.Columns; q++)
            {
                var c = new Vector2I(q, r);
                if (Board.CoverAt(c) != Cover.None && Board.CoverStateAt(c) != CoverState.Intact)
                    t.Append($"{Board.CoverAt(c)} ({q - Shift.X},{r - Shift.Y}) {Board.CoverStateAt(c)}\n");
                if (Board.Smoked(c))
                    t.Append($"smoke ({q - Shift.X},{r - Shift.Y})\n");
            }
        return t.ToString();
    }

    public BattleState Clone()
    {
        var s = (BattleState)MemberwiseClone();
        s.Board = Board.Clone();
        s.Units = Units.Select(u => u.Clone()).ToList();
        s.Score = (int[])Score.Clone();
        s.Fires = new Dictionary<Vector2I, int>(Fires);
        s.Smoke = new Dictionary<Vector2I, int>(Smoke);
        s.Events = new List<BattleEvent>(Events);
        return s;
    }
}
