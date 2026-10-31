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

    public bool Out => Fate is UnitFate.Knocked or UnitFate.Drowned;
    public bool Standing => Fate != UnitFate.Destroyed;
    public bool Ambushing => Watch >= 0;

    public BattleUnit Clone() => (BattleUnit)MemberwiseClone();

    public override string ToString() => $"{Side} {Tag}#{Id} ({Cell.X},{Cell.Y}) h{Heading} {Fate}{(Burning ? " burning" : "")}"
                                         + (Ambushing ? $" watching {Watch}{(Watch2 >= 0 ? $"+{Watch2}" : "")}" : "");
}

public enum BattleEventKind
{
    Moved, Turned, MineBlown, MineCleared, Hit, Glanced, Ricochet, Passed, Spent,
    Knocked, Drowned, Exploded, Ignited, Doused, Afloat,
    WoodAlight, WoodBurnt, WoodCleared, WallDown, Shoved, RamRefused,
    TurnEnded, FieldTick, Won, Drawn,
    AmbushSet, AmbushFired, AmbushLifted,
    SmokeLaid, SmokeGone,
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
    public List<BattleUnit> Units = new();
    public Camp Turn = Camp.Green;
    public int Round = 1;
    /// <summary>Turns played, the first nought: a fire's clock.</summary>
    public int TurnNo;
    /// <summary>The unit this turn belongs to, once it has been taken.</summary>
    public int? Active;
    public bool Drove, Fired, Bonus;
    /// <summary>The round of the last shot at a tank, nought for none yet
    /// (match.md, «Пат — ничья»).</summary>
    public int LastStrike;
    public Camp? Won;
    public bool Drawn;
    /// <summary>Burning woods and the rounds they have left (2 when lit, GDD match.md).</summary>
    public Dictionary<Vector2I, int> Fires = new();
    /// <summary>Smoke and the rounds it has left (2 when laid, field.md «дым»); the
    /// board says where it is (<see cref="BattleBoard.Smoked"/>).</summary>
    public Dictionary<Vector2I, int> Smoke = new();
    public List<BattleEvent> Events = new();

    public bool Over => Won is not null || Drawn;

    /// <summary>Whole rounds played since the last shot at a tank - the
    /// stalemate's count, the one under way not yet in it.</summary>
    public int QuietRounds => Math.Max(0, Round - 1 - LastStrike);

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

    public BattleState Clone()
    {
        var s = (BattleState)MemberwiseClone();
        s.Board = Board.Clone();
        s.Units = Units.Select(u => u.Clone()).ToList();
        s.Fires = new Dictionary<Vector2I, int>(Fires);
        s.Smoke = new Dictionary<Vector2I, int>(Smoke);
        s.Events = new List<BattleEvent>(Events);
        return s;
    }
}
