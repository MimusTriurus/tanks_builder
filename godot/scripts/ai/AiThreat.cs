using System;
using System.Collections.Generic;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The threat map (docs/ai-plan.md, «Оценка позиции», И2): what one
/// activation of a unit can take - from every cell its move reaches
/// (<see cref="Steps.Plan"/>, kept per pose and board in a cache) and from
/// where it stands, every shot at a standing enemy on an axis in range
/// (<see cref="Battle.Probe"/>: plate, pass, ricochet and chain without
/// touching the match), and every ram of an enemy beside onto a mine, into
/// deep water or a burning wood (<see cref="Ramming.Of"/>). A casemate is let
/// turn to the axis wherever it stands - a little more than the steps may
/// give it. One per choice: not safe to share between threads.
/// </summary>
public sealed class AiThreat
{
    private readonly bool _full;
    private int[] _marks = new int[32];
    private readonly Dictionary<(int Id, Vector2I Cell, int Heading, long Board), Steps.Reach> _reach = new();
    private readonly List<Vector2I> _poses = new();
    private static readonly Func<BattleUnit, double> Value = AiEval.Value;

    /// <summary>Plans asked of the cache, and how many it had.</summary>
    public int Asked, Kept;

    /// <param name="full">Ricochets and chains seen (the veteran); off, the
    /// fighter's sight: the hull struck and nothing after.</param>
    public AiThreat(bool full) => _full = full;

    /// <summary>The best single activation of <paramref name="side"/>'s
    /// units: its worth, and the sum of the other units' best.</summary>
    public (double Worst, double Others) Of(BattleState s, Camp side)
    {
        double worst = 0.0, sum = 0.0;
        foreach (BattleUnit e in s.Units)
        {
            if (e.Side != side || e.Fate != UnitFate.Alive)
                continue;
            double g = Gain(s, e);
            sum += g;
            worst = Math.Max(worst, g);
        }
        return (worst, sum - worst);
    }

    /// <summary>What <paramref name="e"/>'s side gains by its best shot or
    /// ram in one activation.</summary>
    public double Gain(BattleState s, BattleUnit e)
    {
        if (_marks.Length < s.Units.Count)
            _marks = new int[s.Units.Count * 2];
        _poses.Clear();
        _poses.Add(e.Cell);
        if (!e.Pinned)
            foreach (Vector2I c in Reach(s, e).Cells.Keys)
                _poses.Add(c);
        int lobReach = Gunnery.LobReachOf(e.Kit);
        bool two = (e.Kit & Kit.PushTwo) != 0;
        double best = 0.0;
        foreach (BattleUnit t in s.Units)
        {
            if (t.Side == e.Side || !t.Standing)
                continue;
            foreach (Vector2I c in _poses)
            {
                if (!HexField.OnAxis(c, t.Cell))
                    continue;
                (int heading, int range) = s.Board.LaneTo(c, t.Cell);
                if (heading < 0 || Gunnery.MayTarget(e.Class.Lobs, range, true, lobReach) != Gunnery.Distance.Fits)
                    continue;
                Battle.Probe(s, e, c, heading, t.Cell, _marks, _full);
                best = Math.Max(best, Battle.ProbeGain(s, _marks, e.Side, Value, AiEval.AlightShare, AiEval.OutShare));
            }
            if (t.Fate == UnitFate.Alive)
                best = Math.Max(best, RamGain(s, e, t, two));
        }
        return best;
    }

    /// <summary>The best ram of <paramref name="t"/> from a pose beside it.</summary>
    private double RamGain(BattleState s, BattleUnit e, BattleUnit t, bool two)
    {
        double best = 0.0;
        foreach (Vector2I c in _poses)
        {
            int heading = HexField.HeadingTo(c, t.Cell);
            if (heading < 0)
                continue;
            Vector2I from = c;
            Ramming.Shove shove = Ramming.Of(s.Board, cell => cell == from || (cell != e.Cell && s.At(cell) is not null),
                                             e.Class, t.Class, t.Cell, heading, two);
            if (!shove.Allowed || shove.Legs.Count == 0)
                continue;
            Vector2I land = shove.Legs[^1];
            double before = t.Burning ? AiEval.AlightShare : 1.0, after = before;
            if (s.Board.IsDeep(land) && s.Board.RampHeading(land) < 0 && (t.Kit & Kit.Snorkel) == 0)
                after = AiEval.OutShare;
            else if (s.Board.CoverAt(land) == Cover.Minefield && s.Board.CoverStateAt(land) == CoverState.Intact)
                after = (t.Kit & Kit.MineGuard) != 0 ? Math.Min(before, AiEval.AlightShare) : AiEval.OutShare;
            else if (s.Board.CoverStateAt(land) == CoverState.Burning && !s.Board.IsWater(land))
                after = Math.Min(before, AiEval.AlightShare);
            best = Math.Max(best, AiEval.Value(t) * (before - after));
        }
        return best;
    }

    /// <summary>The unit's reach on this board, from the cache when its pose,
    /// the standing units and the board's walls, woods, mines and smoke are as
    /// they were.</summary>
    private Steps.Reach Reach(BattleState s, BattleUnit e)
    {
        long board = Stamp(s, e);
        var key = (e.Id, e.Cell, e.Heading, board);
        Asked++;
        if (_reach.TryGetValue(key, out Steps.Reach? had))
        {
            Kept++;
            return had;
        }
        if (_reach.Count > 4096)
            _reach.Clear();
        Steps.Reach reach = Steps.Plan(s.Board, new Steps.Pose(e.Cell, e.Heading), Battle.MoverOf(s, e));
        _reach[key] = reach;
        return reach;
    }

    /// <summary>What the move's plan reads off the match besides the pose: the
    /// standing units but <paramref name="e"/> and every cell's walls, cover
    /// state and smoke.</summary>
    private static long Stamp(BattleState s, BattleUnit e)
    {
        long h = 17;
        foreach (BattleUnit u in s.Units)
            if (u.Standing && u.Id != e.Id)
                h = h * 31 + (u.Cell.X * 1009 + u.Cell.Y);
        for (int r = 0; r < s.Board.Rows; r++)
            for (int q = 0; q < s.Board.Columns; q++)
            {
                var c = new Vector2I(q, r);
                h = h * 31 + (s.Board.SidesAt(c) * 7 + (int)s.Board.CoverStateAt(c)) * 2 + (s.Board.Smoked(c) ? 1 : 0);
            }
        return h;
    }
}
