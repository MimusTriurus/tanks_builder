using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace TankSpriteTest;

/// <summary>How far the AI sees (docs/ai-plan.md, «Сложность»): the second
/// axis of the difficulty, the squad's budget the first.</summary>
public enum AiLevel { Recruit, Fighter, Veteran }

/// <summary>The AI's turn chosen, and what it cost: plays tried, the enemy's
/// replies tried behind the best of them, milliseconds.</summary>
public sealed record AiChoice(AiPlay Play, int Plays, double Ms, int Replies = 0);

/// <summary>
/// The AI's mind (docs/ai-plan.md, И1-И2). The side to move's plays
/// (<see cref="AiMoves"/>) go through three sieves, each dearer and narrower:
/// <list type="number">
/// <item>every play by the cheap score (<see cref="AiEval.Quick"/>);</item>
/// <item>the best <see cref="Shortlist"/> by the full one
/// (<see cref="AiEval.Full"/>): the threat map, what the enemy's next
/// activation could take from where its move would take it;</item>
/// <item>the best few again with the enemy's every reply tried - the quiet
/// search: the reply the enemy would pick by the cheap score, and the play
/// worth what is left after it (<see cref="AiEval.AfterReply"/>).</item>
/// </list>
/// The recruit stops at the first sieve and picks among its five best with
/// a temperature; the fighter sees the shot that strikes and nothing after
/// it (no ricochet, no chain) and picks among three with a little; the
/// veteran sees everything and takes the best. Knows only the core, works on
/// its own copy - safe off the main thread. Deterministic for a seed.
/// </summary>
public static class AiBrain
{
    /// <summary>How many plays the full score is asked of.</summary>
    private const int Shortlist = 40;

    public static AiChoice Choose(BattleState state, AiLevel level, Random rng)
    {
        var watch = Stopwatch.StartNew();
        Camp me = state.Turn;
        BattleState root = AiMoves.Copy(state);
        int keep = level == AiLevel.Recruit ? 5 : Shortlist;
        var best = new List<(AiPlay Play, BattleState After)>(keep + 1);
        int plays = AiMoves.Each(root, (play, after) =>
        {
            // a hair of noise so equal plays do not always fall the same way
            play.Score = AiEval.Quick(after, me) + rng.NextDouble() * 1e-4;
            Keep(best, keep, play, after);
        });
        if (best.Count == 0)
            return new AiChoice(new AiPlay(-1, new[] { new Order(OrderKind.Pass) }), 0, watch.Elapsed.TotalMilliseconds);
        if (level == AiLevel.Recruit)
            return new AiChoice(Tempered(best, 5, 0.6, rng), plays, watch.Elapsed.TotalMilliseconds);

        var threats = new AiThreat(full: level == AiLevel.Veteran);
        foreach ((AiPlay p, BattleState after) in best)
            p.Score = AiEval.Full(after, me, threats) + rng.NextDouble() * 1e-4;
        best.Sort((a, b) => b.Play.Score.CompareTo(a.Play.Score));

        int deep = Math.Min(best.Count, level == AiLevel.Veteran ? 10 : 6);
        int replies = 0;
        for (int i = 0; i < deep; i++)
        {
            (AiPlay p, BattleState after) = best[i];
            // the enemy's turn next: a match over, or one it has nobody for, has no reply
            if (after.Over || after.Turn == me)
                continue;
            double low = double.MaxValue;
            BattleState? lowState = null;
            replies += AiMoves.Each(after, (_, reply) =>
            {
                double v = AiEval.Quick(reply, me);
                if (v < low)
                {
                    low = v;
                    lowState = reply;
                }
            });
            if (lowState is not null)
                p.Score = AiEval.AfterReply(lowState, me, threats) + rng.NextDouble() * 1e-4;
        }
        best.RemoveRange(deep, best.Count - deep);
        best.Sort((a, b) => b.Play.Score.CompareTo(a.Play.Score));
        AiPlay chosen = level == AiLevel.Fighter ? Tempered(best, 3, 0.15, rng) : best[0].Play;
        return new AiChoice(chosen, plays, watch.Elapsed.TotalMilliseconds, replies);
    }

    /// <summary>The play into the sorted list of the <paramref name="most"/> best.</summary>
    private static void Keep(List<(AiPlay Play, BattleState After)> best, int most, AiPlay play, BattleState after)
    {
        if (best.Count == most && play.Score <= best[^1].Play.Score)
            return;
        int at = best.Count;
        while (at > 0 && best[at - 1].Play.Score < play.Score)
            at--;
        best.Insert(at, (play, after));
        if (best.Count > most)
            best.RemoveAt(best.Count - 1);
    }

    /// <summary>One of the <paramref name="top"/> best, drawn by their score
    /// at <paramref name="temperature"/> (in material points): the weaker
    /// levels' slips, never a play far below the best.</summary>
    private static AiPlay Tempered(List<(AiPlay Play, BattleState After)> sorted, int top, double temperature, Random rng)
    {
        int n = Math.Min(top, sorted.Count);
        double head = sorted[0].Play.Score;
        // a decided match is not played with
        if (Math.Abs(head) >= AiEval.Decided / 2)
            return sorted[0].Play;
        var weights = new double[n];
        double sum = 0.0;
        for (int i = 0; i < n; i++)
        {
            weights[i] = Math.Exp((sorted[i].Play.Score - head) / temperature);
            sum += weights[i];
        }
        double pick = rng.NextDouble() * sum;
        for (int i = 0; i < n; i++)
        {
            pick -= weights[i];
            if (pick <= 0.0)
                return sorted[i].Play;
        }
        return sorted[0].Play;
    }

    public static AiLevel? LevelOf(string word) => word.ToLowerInvariant() switch
    {
        "recruit" or "easy" => AiLevel.Recruit,
        "fighter" or "normal" => AiLevel.Fighter,
        "veteran" or "hard" => AiLevel.Veteran,
        _ => null,
    };
}
