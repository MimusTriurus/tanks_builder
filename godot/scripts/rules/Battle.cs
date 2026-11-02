using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace TankSpriteTest;

/// <summary>The two sides of a match. Not <c>Side</c>: outside the bench that
/// name is <c>Godot.Side</c>.</summary>
public enum Camp { Green, Brown }

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
    /// beside it (units.md, «Режим засады»; classes.md, «MT»); -1 for none.</summary>
    public int Watch = -1, Watch2 = -1;
    /// <summary>Rounds the ambush has left: one, two with the stabilizer
    /// (upgrades.md, «Стабилизатор засады»); nought reads as one.</summary>
    public int WatchShots;

    public bool Out => Fate is UnitFate.Knocked or UnitFate.Drowned;
    public bool Standing => Fate != UnitFate.Destroyed;
    public bool Ambushing => Watch >= 0;

    public BattleUnit Clone() => (BattleUnit)MemberwiseClone();

    public override string ToString() => Say(Vector2I.Zero);

    /// <summary>The unit in a line, its cell less <paramref name="shift"/> -
    /// the map's cell on a board in a sea (<see cref="BattleState.Shift"/>).</summary>
    public string Say(Vector2I shift) => $"{Side} {Tag}#{Id} ({Cell.X - shift.X},{Cell.Y - shift.Y}) h{Heading} {Fate}{(Burning ? " burning" : "")}"
                                         + (Ambushing ? $" watching {Watch}{(Watch2 >= 0 ? $"+{Watch2}" : "")}{(WatchShots > 1 ? $" x{WatchShots}" : "")}" : "");
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
    public int Round = 1;
    /// <summary>Turns played, the first nought: a fire's clock.</summary>
    public int TurnNo;
    /// <summary>The unit this turn belongs to, once it has been taken.</summary>
    public int? Active;
    public bool Drove, Fired, Bonus;
    /// <summary>The round of the last shot at a tank, nought for none yet
    /// (match.md, «Пат — ничья»).</summary>
    public int LastStrike;
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

    /// <summary>Whole rounds played since the last shot at a tank, or the point
    /// changing hands - the stalemate's count, the one under way not yet in it.</summary>
    public int QuietRounds => Math.Max(0, Round - 1 - Math.Max(LastStrike, LastShift));

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
