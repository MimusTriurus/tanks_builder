using System;
using System.Collections.Generic;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The AI's placement in its zone (docs/ai-plan.md, «Расстановка», И4) - the
/// one decision it takes blind: the enemy's squad and placement are not read,
/// only the board and the enemy's zone.
/// <list type="bullet">
/// <item>Depth by role: HM at the back, LT on a flank, MT between; HT, TD
/// and LT back from the zone's front row too - the first strike reaches the
/// front row (<see cref="Lead"/>, measured: the plan's «HT и TD вперёд» lost
/// 4:14 to the map's own homes on deploy10, back from the front it held them
/// 10:8 and 10:9).</item>
/// <item>The nose to the enemy's zone; a destroyer's to the axis that holds
/// most of it (it turns its hull at the cost of steps).</item>
/// <item>The enemy zone's lines of fire: the mortar and the medium keep out
/// of them, the destroyer wants a long one.</item>
/// <item>Nobody next to an own unit (one blast takes both) and nobody on an
/// open axis with one (a round through, a glance on) - weighed, not barred:
/// a narrow zone may leave no other cell.</item>
/// <item>Each unit picks among the three best cells by chance - before the
/// first turn the GDD lets it (match.md); <paramref name="rng"/> is the
/// match's, so a match plays again.</item>
/// </list>
/// Works on the core's board coordinates; the caller sets the cells.
/// </summary>
public static class AiDeploy
{
    public readonly record struct Spot(int Unit, Vector2I Cell, int Heading);

    /// <summary>
    /// The front row's worth to HT, TD and LT: negative, the back row wanted.
    /// A trial's knob (battle-run <c>--deploy-lead X</c>); measured 2026-11-05
    /// on deploy10, Fighter against Fighter, the AI's side against the map's
    /// homes, seeds 1-12, both first moves: at +1 the AI's green won 4 and lost
    /// 14; at -1 green 10:8, brown 10:9 (the homes against the homes: green
    /// 9:0). The side moving second weighs the row at 0.3 of it.
    /// </summary>
    public static float Lead = -1.0f;

    /// <summary>How far a line of fire is read from a cell, in cells.</summary>
    private const int Reach = 8;

    /// <summary>The order the classes choose in: the front's anchors first.</summary>
    private static readonly string[] Order = { "TD", "HT", "HM", "MT", "LT" };

    public static List<Spot> Place(BattleState s, Camp side, IReadOnlyList<Vector2I> zone,
                                   IReadOnlyList<Vector2I> enemyZone, Random rng)
    {
        BattleBoard b = s.Board;
        var cells = new List<Vector2I>();
        foreach (Vector2I c in zone)
            if (Free(b, c))
                cells.Add(c);
        var units = new List<BattleUnit>();
        foreach (BattleUnit u in s.Units)
            if (u.Side == side && u.Fate == UnitFate.Alive)
                units.Add(u);
        units.Sort((x, y) => Rank(x).CompareTo(Rank(y)));
        var spots = new List<Spot>();
        if (cells.Count == 0 || enemyZone.Count == 0)
            return spots;

        // the zone read once: depth to the enemy, flank, lines of fire
        Vector2 own = Centre(cells), enemy = Centre(enemyZone);
        Vector2 ahead = (enemy - own).Normalized();
        Vector2 across = new(-ahead.Y, ahead.X);
        int n = cells.Count;
        var front = new float[n];
        var flank = new float[n];
        var open = new int[n];
        var face = new int[n];
        var axis = new int[n];
        var axisFace = new int[n];
        float dMin = float.MaxValue, dMax = float.MinValue, latMax = 1e-3f;
        int openMax = 1;
        for (int i = 0; i < n; i++)
        {
            Vector2I c = cells[i];
            int d = int.MaxValue;
            foreach (Vector2I e in enemyZone)
                d = Math.Min(d, HexField.Distance(c, e));
            front[i] = d;
            dMin = Math.Min(dMin, d);
            dMax = Math.Max(dMax, d);
            flank[i] = Math.Abs((Point(c) - own).Dot(across));
            latMax = Math.Max(latMax, flank[i]);
            face[i] = Toward(Point(c), enemy);
            axis[i] = -1;
            foreach (int h in HexField.EdgeHeadings)
            {
                int seen = OpenOn(b, c, h, enemyZone);
                open[i] += seen;
                if (seen > axis[i] || (seen == axis[i] && h == face[i]))
                {
                    axis[i] = seen;
                    axisFace[i] = h;
                }
            }
            openMax = Math.Max(openMax, open[i]);
        }
        for (int i = 0; i < n; i++)
        {
            // 1 the front row, 0 the back
            front[i] = dMax > dMin ? (dMax - front[i]) / (dMax - dMin) : 0.5f;
            flank[i] /= latMax;
        }

        // the side to move second meets the first strike: the front row is
        // worth less to it than its lines of fire are a danger
        float lead = (s.First == side ? 1.0f : 0.3f) * Lead;
        var taken = new List<Vector2I>();
        var scored = new List<(float Score, int Index)>();
        foreach (BattleUnit u in units)
        {
            scored.Clear();
            string cls = Class(u);
            for (int i = 0; i < n; i++)
            {
                if (taken.Contains(cells[i]))
                    continue;
                float exposed = open[i] / (float)openMax;
                float forward = lead * front[i];
                float score = cls switch
                {
                    "HT" => 2.0f * forward - 0.5f * flank[i] - 1.0f * exposed,
                    "TD" => 1.0f * forward + 1.5f * Math.Min(axis[i], 4) / 4.0f - 0.5f * exposed,
                    "HM" => 2.0f * (1.0f - front[i]) - 1.5f * exposed,
                    "LT" => 2.0f * flank[i] + 0.5f * forward - 1.0f * exposed,
                    _ => 1.0f - 2.0f * Math.Abs(front[i] - 0.5f) - 1.0f * exposed,
                };
                score += Sheltered(b, cells[i]) * 0.3f;
                foreach (Vector2I t in taken)
                {
                    if (HexField.Distance(t, cells[i]) == 1)
                        score -= 3.0f;
                    else if (OnOpenAxis(b, cells[i], t))
                        score -= 1.5f;
                }
                scored.Add((score, i));
            }
            if (scored.Count == 0)
                break;
            scored.Sort((x, y) => y.Score.CompareTo(x.Score));
            // among the three best: 60, 25, 15 per cent
            double roll = rng.NextDouble();
            int pick = roll < 0.6 || scored.Count == 1 ? 0 : roll < 0.85 || scored.Count == 2 ? 1 : 2;
            int at = scored[pick].Index;
            taken.Add(cells[at]);
            spots.Add(new Spot(u.Id, cells[at], cls == "TD" && axis[at] > 0 ? axisFace[at] : face[at]));
        }
        return spots;
    }

    /// <summary>The spots set on the state: cells and headings.</summary>
    public static void Apply(BattleState s, IEnumerable<Spot> spots)
    {
        foreach (Spot p in spots)
            if (s.Unit(p.Unit) is { } u)
            {
                u.Cell = p.Cell;
                u.Heading = p.Heading;
            }
    }

    private static bool Free(BattleBoard b, Vector2I c) =>
        b.InBounds(c) && !b.IsWater(c) && !b.IsRamp(c)
        && b.FoundationAt(c) is not (Foundation.Rock or Foundation.Void)
        && b.CoverAt(c) != Cover.Minefield;

    private static string Class(BattleUnit u) => u.Tag.Length >= 2 ? u.Tag[..2] : u.Tag;

    private static int Rank(BattleUnit u)
    {
        int i = Array.IndexOf(Order, Class(u));
        return i < 0 ? Order.Length : i;
    }

    /// <summary>A cell's middle on a plane where the six steps are one long:
    /// odd-q, flat-top.</summary>
    private static Vector2 Point(Vector2I c) =>
        new(1.5f * c.X, Mathf.Sqrt(3.0f) * (c.Y + 0.5f * (c.X & 1)));

    private static Vector2 Centre(IReadOnlyList<Vector2I> cells)
    {
        Vector2 sum = Vector2.Zero;
        foreach (Vector2I c in cells)
            sum += Point(c);
        return sum / Math.Max(cells.Count, 1);
    }

    /// <summary>The hex side nearest the way to <paramref name="to"/>: heading
    /// 90 is up the rows (y falling), 0 along the columns.</summary>
    private static int Toward(Vector2 from, Vector2 to)
    {
        Vector2 want = to - from;
        int best = HexField.EdgeHeadings[0];
        float most = float.MinValue;
        foreach (int h in HexField.EdgeHeadings)
        {
            float a = Mathf.DegToRad(h);
            float dot = want.Dot(new Vector2(Mathf.Cos(a), -Mathf.Sin(a)));
            if (dot > most)
            {
                most = dot;
                best = h;
            }
        }
        return best;
    }

    /// <summary>Enemy-zone cells on the open line of fire out of
    /// <paramref name="c"/> along <paramref name="h"/>, two to
    /// <see cref="Reach"/> cells out. Open: no wall on an edge crossed, no
    /// screen and no ground over the floor strictly between (field.md).</summary>
    private static int OpenOn(BattleBoard b, Vector2I c, int h, IReadOnlyList<Vector2I> enemyZone)
    {
        int seen = 0;
        Vector2I at = c;
        int level = b.LevelAt(c);
        var between = new List<Vector2I>();
        for (int k = 1; k <= Reach; k++)
        {
            if (b.Walled(at, h))
                break;
            Vector2I next = HexField.Step(at, h);
            if (next == at || !b.InBounds(next))
                break;
            if (k >= 2 && Contains(enemyZone, next) && Clear(b, between, level, b.LevelAt(next)))
                seen++;
            if (b.Screened(next))
                break;
            between.Add(next);
            at = next;
        }
        return seen;
    }

    private static bool Clear(BattleBoard b, List<Vector2I> between, int from, int onto)
    {
        foreach (Vector2I c in between)
            if (Gunnery.Overtops(b, c, from, onto))
                return false;
        return true;
    }

    /// <summary>Whether <paramref name="a"/> and <paramref name="t"/> lie on
    /// one axis within <see cref="Reach"/> with nothing screening between.</summary>
    private static bool OnOpenAxis(BattleBoard b, Vector2I a, Vector2I t)
    {
        (int h, int range) = b.LaneTo(a, t);
        if (h < 0 || range > Reach)
            return false;
        Vector2I at = a;
        for (int k = 1; k < range; k++)
        {
            at = HexField.Step(at, h);
            if (b.Screened(at))
                return false;
        }
        return true;
    }

    /// <summary>Woods and walls next to a cell, 0..1.</summary>
    private static float Sheltered(BattleBoard b, Vector2I c)
    {
        int n = 0;
        foreach (int h in HexField.EdgeHeadings)
        {
            Vector2I next = HexField.Step(c, h);
            if (b.CoverAt(next) is Cover.Forest or Cover.Walls || b.Walled(c, h))
                n++;
        }
        return n / 6.0f;
    }

    private static bool Contains(IReadOnlyList<Vector2I> cells, Vector2I c)
    {
        foreach (Vector2I x in cells)
            if (x == c)
                return true;
        return false;
    }
}
