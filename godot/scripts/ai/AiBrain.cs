using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace TankSpriteTest;

/// <summary>How far the AI sees (docs/ai-plan.md, «Сложность»): the second
/// axis of the difficulty, the squad's budget the first.</summary>
public enum AiLevel { Recruit, Fighter, Veteran }

/// <summary>The AI's turn chosen, and what it cost.</summary>
public sealed record AiChoice(AiPlay Play, int Plays, double Ms);

/// <summary>
/// The AI's mind (docs/ai-plan.md, И1): the side to move's plays
/// (<see cref="AiMoves"/>) scored one turn deep. Every play by the cheap
/// score (<see cref="AiEval.Quick"/>); the best few again by the full one
/// (<see cref="AiEval.Full"/> - what the enemy could then knock out). The
/// recruit stops at the cheap score and picks among its five best with a
/// temperature; the others take the best. Knows only the core, works on its
/// own copy - safe off the main thread. Deterministic for a seed.
/// </summary>
public static class AiBrain
{
    /// <summary>How many plays the full score is asked of.</summary>
    private const int Shortlist = 24;

    public static AiChoice Choose(BattleState state, AiLevel level, Random rng)
    {
        var watch = Stopwatch.StartNew();
        Camp me = state.Turn;
        BattleState root = AiMoves.Copy(state);
        var best = new List<(AiPlay Play, BattleState After)>(Shortlist + 1);
        int plays = AiMoves.Each(root, (play, after) =>
        {
            // a hair of noise so equal plays do not always fall the same way
            play.Score = AiEval.Quick(after, me) + rng.NextDouble() * 1e-4;
            if (best.Count == Shortlist && play.Score <= best[^1].Play.Score)
                return;
            int at = best.Count;
            while (at > 0 && best[at - 1].Play.Score < play.Score)
                at--;
            best.Insert(at, (play, after));
            if (best.Count > Shortlist)
                best.RemoveAt(best.Count - 1);
        });
        if (best.Count == 0)
            return new AiChoice(new AiPlay(-1, new[] { new Order(OrderKind.Pass) }), 0, watch.Elapsed.TotalMilliseconds);

        if (level == AiLevel.Recruit)
            return new AiChoice(Tempered(best, 5, 0.6, rng), plays, watch.Elapsed.TotalMilliseconds);

        foreach ((AiPlay p, BattleState after) in best)
            p.Score = AiEval.Full(after, me) + rng.NextDouble() * 1e-4;
        best.Sort((a, b) => b.Play.Score.CompareTo(a.Play.Score));
        AiPlay chosen = level == AiLevel.Fighter ? Tempered(best, 3, 0.15, rng) : best[0].Play;
        return new AiChoice(chosen, plays, watch.Elapsed.TotalMilliseconds);
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
