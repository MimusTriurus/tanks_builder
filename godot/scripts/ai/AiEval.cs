using System;
using System.Collections.Generic;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// How good a match is for one side (docs/ai-plan.md, «Оценка позиции»,
/// И1): the material, the units on the control point, the way to the enemy -
/// <see cref="Quick"/>, asked of every play - and, for the plays that come
/// out on top, what the enemy's next turn can knock out of where it stands
/// (<see cref="Full"/>): the threat map's first, standing half. Positive is
/// good for <c>me</c>. The weights are this file's until the levels are
/// calibrated (И5, <c>data/ai.json</c>).
/// </summary>
public static class AiEval
{
    /// <summary>A side wins or loses: past anything the material can say.</summary>
    public const double Decided = 10000.0;

    /// <summary>A draw, for the side asking: it would rather fight on.</summary>
    private const double Drawn = -2.0;
    /// <summary>A quiet round on the stalemate's clock.</summary>
    private const double Quiet = 0.4;
    /// <summary>A step short of (or past) where a unit wants to be from the nearest enemy.</summary>
    private const double Approach = 0.2;

    /// <summary>What a unit is worth (ai-plan.md, вопрос 2): LT 3, MT 4,
    /// HT 6, TD 5, HM 5 by its gun, a quarter a piece of kit on top.</summary>
    public static double Value(BattleUnit u)
    {
        double cls = u.Class.Might switch { 1 => 3.0, 2 => 4.0, 3 => 6.0, 4 => 5.0, _ => 5.0 };
        return cls + 0.25 * System.Numerics.BitOperations.PopCount((uint)u.Kit);
    }

    /// <summary>How much of its worth a unit still is: whole alive, half
    /// alight (the fire takes it unless its next activation puts it out),
    /// a fifth knocked out or drowned - it still blocks, goes up in a chain,
    /// can be repaired - nothing destroyed.</summary>
    public static double Share(BattleUnit u) => u.Fate switch
    {
        UnitFate.Alive => u.Burning ? 0.55 : 1.0,
        UnitFate.Knocked or UnitFate.Drowned => 0.2,
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

    /// <summary>The cheap score, asked of every play.</summary>
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
        // on the side to move, so it goes and shoots at a tank
        score -= Quiet * s.QuietRounds;
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
                score -= sign * Approach * Math.Abs(near - want);
            }
            // the control point: standing on it
            if (s.Point is { } point && Array.IndexOf(point, u.Cell) >= 0)
                score += sign * 0.4;
        }
        return score;
    }

    /// <summary>
    /// The full score: <see cref="Quick"/> less what the enemy's next turn
    /// can knock out from where its units stand (each tried on a copy - a shot
    /// along the axis, ricochet, pass and chain as the rules have them), the
    /// worst of it most, for one unit acts; and a little for what
    /// <paramref name="me"/> could knock out after, the initiative. The move
    /// before the shot is not tried yet (И2).
    /// </summary>
    public static double Full(BattleState s, Camp me)
    {
        double score = Quick(s, me);
        if (s.Over)
            return score;
        Camp enemy = me == Camp.Green ? Camp.Brown : Camp.Green;
        (double worst, double rest) = Threats(s, enemy);
        // the nearer the stalemate, the less the fear of standing in a line of fire
        double fear = Math.Max(0.3, 1.0 - 0.25 * s.QuietRounds);
        score -= fear * (0.8 * worst + 0.15 * rest);
        (double best, _) = Threats(s, me);
        score += 0.3 * best;
        return score;
    }

    /// <summary>What <paramref name="shooters"/>' side could take with one
    /// shot from where each stands: the best single shot's worth, and the sum
    /// of the others' best.</summary>
    public static (double Worst, double Others) Threats(BattleState s, Camp shooters)
    {
        double worst = 0.0, sum = 0.0;
        foreach (BattleUnit e in s.Units)
        {
            if (e.Side != shooters || e.Fate != UnitFate.Alive)
                continue;
            double best = 0.0;
            foreach (BattleUnit t in s.Units)
            {
                if (t.Side == shooters || t.Fate != UnitFate.Alive)
                    continue;
                best = Math.Max(best, ShotWorth(s, e, t));
            }
            sum += best;
            worst = Math.Max(worst, best);
        }
        return (worst, sum - worst);
    }

    /// <summary>
    /// What a shot from <paramref name="shooter"/>, standing where it stands,
    /// at <paramref name="target"/> takes from the target's side - the
    /// material the shooter's side gains by it, chains and ricochets on
    /// either side counted. Nought when the axis, the distance or the line
    /// of fire says no. A casemate is let turn to the axis: its move would.
    /// </summary>
    public static double ShotWorth(BattleState s, BattleUnit shooter, BattleUnit target)
    {
        (int heading, int range) = s.Board.LaneTo(shooter.Cell, target.Cell);
        if (heading < 0)
            return 0.0;
        if (Gunnery.MayTarget(shooter.Class.Lobs, range, true, Gunnery.LobReachOf(shooter.Kit)) != Gunnery.Distance.Fits)
            return 0.0;
        BattleState t = s.Clone();
        t.Events.Clear();
        t.Turn = shooter.Side;
        t.Active = shooter.Id;
        t.Drove = t.Fired = t.Bonus = t.Overdrive = false;
        t.OnWay = null;
        BattleUnit sh = t.Unit(shooter.Id)!;
        if (!sh.Class.Turreted)
            sh.Heading = heading;
        double before = Material(t, shooter.Side);
        if (Battle.Fire(t, target.Cell) is not null)
            return 0.0;
        return Math.Max(0.0, Material(t, shooter.Side) - before);
    }

    /// <summary>Steps from <paramref name="u"/> to the nearest standing enemy
    /// able to fight, or <see cref="int.MaxValue"/> for none.</summary>
    public static int Nearest(BattleState s, BattleUnit u)
    {
        int best = int.MaxValue;
        foreach (BattleUnit o in s.Units)
            if (o.Side != u.Side && o.Fate == UnitFate.Alive)
                best = Math.Min(best, HexField.Distance(u.Cell, o.Cell));
        return best;
    }

    /// <summary>The nearest enemy able to fight, or null.</summary>
    public static BattleUnit? NearestEnemy(BattleState s, BattleUnit u)
    {
        BattleUnit? best = null;
        int d = int.MaxValue;
        foreach (BattleUnit o in s.Units)
        {
            if (o.Side == u.Side || o.Fate != UnitFate.Alive)
                continue;
            int here = HexField.Distance(u.Cell, o.Cell);
            if (here < d)
            {
                d = here;
                best = o;
            }
        }
        return best;
    }
}
