using System;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// How good a match is for one side (docs/ai-plan.md, «Оценка позиции»):
/// <see cref="Quick"/> - the material, the units on the control point, the
/// way to the enemy, the stalemate's clock - asked of every play and every
/// reply; <see cref="Full"/> - that and the threat map (<see cref="AiThreat"/>):
/// what the enemy's next activation can take, what ours could after.
/// Positive is good for <c>me</c>. The weights are this file's until the
/// levels are calibrated (И5, <c>data/ai.json</c>).
/// </summary>
public static class AiEval
{
    /// <summary>A side wins or loses: past anything the material can say.</summary>
    public const double Decided = 10000.0;

    /// <summary>A unit alight, of its worth: the fire takes it unless its next activation puts it out.</summary>
    public const double AlightShare = 0.55;
    /// <summary>A unit knocked out or drowned, of its worth: it still blocks,
    /// goes up in a chain, can be repaired.</summary>
    public const double OutShare = 0.2;

    /// <summary>A draw, for the side asking: it would rather fight on.</summary>
    private const double Drawn = -4.0;
    /// <summary>A quiet round on the stalemate's clock.</summary>
    private const double Quiet = 0.4;
    /// <summary>A step short of (or past) where a unit wants to be from the nearest enemy.</summary>
    private const double Approach = 0.2;
    /// <summary>A quiet round's extra weight on a point of material lead.</summary>
    private const double Lead = 0.15;
    /// <summary>The enemy's best activation, and the rest of its units' best:
    /// one of them acts next.</summary>
    private const double Worst = 0.9, Others = 0.1;
    /// <summary>Our own best activation after: the initiative, the enemy's turn between.</summary>
    private const double Initiative = 0.25;

    /// <summary>What a unit is worth (ai-plan.md, вопрос 2): LT 3, MT 4,
    /// HT 6, TD 5, HM 5 by its gun, a quarter a piece of kit on top.</summary>
    public static double Value(BattleUnit u)
    {
        double cls = u.Class.Might switch { 1 => 3.0, 2 => 4.0, 3 => 6.0, 4 => 5.0, _ => 5.0 };
        return cls + 0.25 * System.Numerics.BitOperations.PopCount((uint)u.Kit);
    }

    /// <summary>How much of its worth a unit still is.</summary>
    public static double Share(BattleUnit u) => u.Fate switch
    {
        UnitFate.Alive => u.Burning ? AlightShare : 1.0,
        UnitFate.Knocked or UnitFate.Drowned => OutShare,
        _ => 0.0,
    };

    /// <summary>The material: <paramref name="me"/>'s units' worth less the enemy's.</summary>
    public static double Material(BattleState s, Camp me)
    {
        double sum = 0.0;
        foreach (BattleUnit u in s.Units)
            sum += (u.Side == me ? 1.0 : -1.0) * Value(u) * Share(u);
        return sum;
    }

    /// <summary>The cheap score, asked of every play and every reply.</summary>
    public static double Quick(BattleState s, Camp me)
    {
        if (s.Won is Camp w)
            return w == me ? Decided : -Decided;
        // a draw: the AI fights to the end (meta.md, «ИИ противника и мета») -
        // worse than level, better only when well behind
        if (s.Drawn)
            return Drawn;
        double score = Material(s, me);
        // the stalemate's clock (match.md, «Пат — ничья»): quiet rounds weigh
        // on the side asking, so it goes and shoots at a tank - the more, the
        // further ahead it is (ai-plan.md, «Пат»: the leader has a win to lose)
        score -= s.QuietRounds * (Quiet + Lead * Math.Max(0.0, score));
        foreach (BattleUnit u in s.Units)
        {
            if (u.Fate != UnitFate.Alive)
                continue;
            double sign = u.Side == me ? 1.0 : -1.0;
            // to the enemy: the mortar to its band, the guns to the nearest
            int near = Nearest(s, u);
            if (near < int.MaxValue)
            {
                int want = u.Class.Lobs ? 4 : u.Class.Turreted ? 2 : 3;
                // the pull grows with the stalemate's clock: a side that sits
                // still the whole match is drawn
                score -= sign * Approach * (1.0 + 0.5 * s.QuietRounds) * Math.Abs(near - want);
            }
            // the control point: standing on it
            if (s.Point is { } point && Array.IndexOf(point, u.Cell) >= 0)
                score += sign * 0.4;
        }
        return score;
    }

    /// <summary>
    /// The full score: <see cref="Quick"/> less what the enemy's next
    /// activation can take - its best most, for one unit acts; the fear eases
    /// as the stalemate nears - and a little for what ours could take after.
    /// </summary>
    public static double Full(BattleState s, Camp me, AiThreat threats)
    {
        double score = Quick(s, me);
        if (s.Over)
            return score;
        (double worst, double others) = threats.Of(s, Across(me));
        double fear = Math.Max(0.3, 1.0 - 0.25 * s.QuietRounds);
        score -= fear * (Worst * worst + Others * others);
        return score + Initiative * threats.Of(s, me).Worst;
    }

    /// <summary>The leaf after the enemy's reply: <see cref="Quick"/> and our
    /// best activation, which is next.</summary>
    public static double AfterReply(BattleState s, Camp me, AiThreat threats) =>
        s.Over ? Quick(s, me) : Quick(s, me) + Initiative * threats.Of(s, me).Worst;

    public static Camp Across(Camp c) => c == Camp.Green ? Camp.Brown : Camp.Green;

    /// <summary>Steps from <paramref name="u"/> to the nearest enemy able to
    /// fight - with none left, the nearest still standing, out of the fight:
    /// the match is won only once every enemy is destroyed (match.md) - or
    /// <see cref="int.MaxValue"/> for none.</summary>
    public static int Nearest(BattleState s, BattleUnit u) =>
        NearestEnemy(s, u, u.Cell) is { } e ? HexField.Distance(u.Cell, e.Cell) : int.MaxValue;

    /// <summary>The enemy of <paramref name="u"/> nearest <paramref name="from"/>:
    /// one able to fight, else one still standing; null for none.</summary>
    public static BattleUnit? NearestEnemy(BattleState s, BattleUnit u, Godot.Vector2I from)
    {
        BattleUnit? alive = null, standing = null;
        int da = int.MaxValue, ds = int.MaxValue;
        foreach (BattleUnit o in s.Units)
        {
            if (o.Side == u.Side || !o.Standing)
                continue;
            int d = HexField.Distance(from, o.Cell);
            if (o.Fate == UnitFate.Alive && d < da)
            {
                da = d;
                alive = o;
            }
            else if (o.Fate != UnitFate.Alive && d < ds)
            {
                ds = d;
                standing = o;
            }
        }
        return alive ?? standing;
    }
}
