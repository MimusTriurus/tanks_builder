using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;

namespace TankSpriteTest;

/// <summary>How far the AI sees (docs/ai-plan.md, «Сложность»): the second
/// axis of the difficulty, the squad's budget the first.</summary>
public enum AiLevel { Recruit, Fighter, Veteran }

/// <summary>The AI's turn chosen, and what it cost: plays tried, the enemy's
/// replies tried behind the best of them, milliseconds; the activations it
/// looked through (one, two, three) and the turns generated past the second
/// (<see cref="AiBrain"/>, the veteran's search).</summary>
public sealed record AiChoice(AiPlay Play, int Plays, double Ms, int Replies = 0, int Depth = 1, int Deep = 0);

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
/// veteran sees everything, and its best plays go on to a third activation
/// (<see cref="Search"/>, И3): the enemy's likeliest replies and its own
/// best turn after each - the two-turn plan (a wood ground for a gun behind
/// it, a fork). Knows only the core, works on its own copies - safe off the
/// main thread, the veteran's roots on threads of their own. Deterministic for
/// a seed: a root's worth depends on nothing but its state.
/// </summary>
public static class AiBrain
{
    /// <summary>How many plays the full score is asked of.</summary>
    private const int Shortlist = 40;

    /// <param name="shallow">The veteran without its third activation - the
    /// И2 veteran, for <c>--battle-run --ai …:veteran@2</c> to measure the search by.</param>
    public static AiChoice Choose(BattleState state, AiLevel level, Random rng, bool shallow = false, AiStyle? style = null)
    {
        var watch = Stopwatch.StartNew();
        AiStyle st = style ?? AiStyle.Plain;
        Camp me = state.Turn;
        BattleState root = AiMoves.Copy(state);
        int keep = level == AiLevel.Recruit ? 5 : Shortlist;
        var best = new List<(AiPlay Play, BattleState After)>(keep + 1);
        int plays = AiMoves.Each(root, (play, after) =>
        {
            // a hair of noise so equal plays do not always fall the same way
            play.Score = AiEval.Quick(after, me, st) + rng.NextDouble() * 1e-4;
            Keep(best, keep, play, after);
        });
        if (best.Count == 0)
            return new AiChoice(new AiPlay(-1, new[] { new Order(OrderKind.Pass) }), 0, watch.Elapsed.TotalMilliseconds);
        if (level == AiLevel.Recruit)
            return new AiChoice(Tempered(best, 5, 0.6, rng), plays, watch.Elapsed.TotalMilliseconds);

        var threats = new AiThreat(full: level == AiLevel.Veteran);
        foreach ((AiPlay p, BattleState after) in best)
            p.Score = AiEval.Full(after, me, threats, st) + rng.NextDouble() * 1e-4;
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
                double v = AiEval.Quick(reply, me, st);
                if (v < low)
                {
                    low = v;
                    lowState = reply;
                }
            });
            if (lowState is not null)
                p.Score = AiEval.AfterReply(lowState, me, threats, st) + rng.NextDouble() * 1e-4;
        }
        best.RemoveRange(deep, best.Count - deep);
        best.Sort((a, b) => b.Play.Score.CompareTo(a.Play.Score));
        if (level == AiLevel.Fighter)
            return new AiChoice(Tempered(best, 3, 0.15, rng), plays, watch.Elapsed.TotalMilliseconds, replies, 2);
        if (shallow)
            return new AiChoice(best[0].Play, plays, watch.Elapsed.TotalMilliseconds, replies, 2);
        (AiPlay chosen, int searched, bool whole) = Search(best, me, watch, st);
        return new AiChoice(chosen, plays, watch.Elapsed.TotalMilliseconds, replies, whole ? 3 : 2, searched);
    }

    /// <summary>The veteran's roots: its best plays by the second sieve.</summary>
    private const int Roots = 8;
    /// <summary>The enemy's replies looked past, its likeliest by the cheap score.</summary>
    private const int Replies = 6;
    /// <summary>Our turns after a reply given the full score, the best by the cheap one.</summary>
    private const int Answers = 4;
    /// <summary>Past this many milliseconds the third activation is given up
    /// and the second's play stands.</summary>
    private const double Budget = 4000.0;

    /// <summary>
    /// The third activation (docs/ai-plan.md, И3): each of the
    /// <see cref="Roots"/> best plays is worth the least of its
    /// <see cref="Replies"/> likeliest replies, and each reply the most of
    /// our <see cref="Answers"/> best turns after it by the full score - a
    /// minimax on a beam. The first root sets the bar; the others, on
    /// threads, leave a reply as soon as it is under it (alpha-beta: such a
    /// root cannot be the best). A side that moves twice running (the enemy
    /// has nobody, the round turns) is asked as it comes. Over the budget it
    /// stands down to the second activation's choice, the first of
    /// <paramref name="ranked"/>. Returns the play, the turns generated, and
    /// whether the search was whole.
    /// </summary>
    private static (AiPlay Play, int Searched, bool Whole) Search(List<(AiPlay Play, BattleState After)> ranked, Camp me,
                                                                  Stopwatch watch, AiStyle st)
    {
        int n = Math.Min(Roots, ranked.Count);
        if (n < 2 || Math.Abs(ranked[0].Play.Score) >= AiEval.Decided / 2)
            return (ranked[0].Play, 0, false);
        var worth = new double[n];
        var turns = new int[n];
        double deadline = watch.Elapsed.TotalMilliseconds + Budget;
        int first = 0;
        worth[0] = Node(ranked[0].After, me, 2, double.NegativeInfinity, new AiThreat(full: true), ref first, watch, deadline, st);
        turns[0] = first;
        double bar = worth[0];
        Parallel.For(1, n, i =>
        {
            int count = 0;
            worth[i] = Node(ranked[i].After, me, 2, bar, new AiThreat(full: true), ref count, watch, deadline, st);
            turns[i] = count;
        });
        int searched = 0;
        foreach (int c in turns)
            searched += c;
        if (watch.Elapsed.TotalMilliseconds > deadline)
            return (ranked[0].Play, searched, false);
        int at = 0;
        for (int i = 1; i < n; i++)
            if (worth[i] > worth[at])
                at = i;
        AiPlay chosen = ranked[at].Play;
        chosen.Score = worth[at];
        return (chosen, searched, true);
    }

    /// <summary>What <paramref name="s"/> is worth to <paramref name="me"/>
    /// with <paramref name="depth"/> activations still to look through: ours
    /// the best, the enemy's the worst; the last of ours by the full score.
    /// <paramref name="alpha"/>: below it an enemy's node is not worth
    /// finishing.</summary>
    private static double Node(BattleState s, Camp me, int depth, double alpha, AiThreat threats, ref int turns,
                               Stopwatch watch, double deadline, AiStyle st)
    {
        if (s.Over)
            return AiEval.Quick(s, me, st);
        if (depth == 0 || watch.Elapsed.TotalMilliseconds > deadline)
            return s.Turn == me ? AiEval.AfterReply(s, me, threats, st) : AiEval.Full(s, me, threats, st);
        bool mine = s.Turn == me;
        int keep = mine ? Answers : Replies;
        var kept = new List<(double Key, BattleState After)>(keep + 1);
        turns++;
        AiMoves.Each(s, (_, after) =>
        {
            // ours the highest, the enemy's the lowest by our cheap score
            double q = AiEval.Quick(after, me, st);
            double key = mine ? q : -q;
            if (kept.Count == keep && key <= kept[^1].Key)
                return;
            int at = kept.Count;
            while (at > 0 && kept[at - 1].Key < key)
                at--;
            kept.Insert(at, (key, after));
            if (kept.Count > keep)
                kept.RemoveAt(kept.Count - 1);
        });
        if (kept.Count == 0)
            return mine ? AiEval.Full(s, me, threats, st) : AiEval.AfterReply(s, me, threats, st);
        double value = mine ? double.NegativeInfinity : double.PositiveInfinity;
        foreach ((double _, BattleState after) in kept)
        {
            double v = mine && depth == 1
                ? (after.Over ? AiEval.Quick(after, me, st) : AiEval.Full(after, me, threats, st))
                : Node(after, me, depth - 1, alpha, threats, ref turns, watch, deadline, st);
            if (mine)
                value = Math.Max(value, v);
            else
            {
                value = Math.Min(value, v);
                if (value <= alpha)
                    return value;
            }
        }
        return value;
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
