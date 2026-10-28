using System;
using System.Collections.Generic;
using Godot;

namespace TankSpriteTest;

/// <summary>The six roads (tools/render/roads.py): how a road looks, not what
/// it is - every one of them is <see cref="Foundation.Road"/> to the rules.</summary>
public enum RoadType { Dirt, Gravel, Cobble, Corduroy, Forest, Asphalt }

/// <summary>One road: a chain of neighbouring cells, and its look.</summary>
public sealed record RoadPath(RoadType Type, IReadOnlyList<Vector2I> Cells)
{
    public RoadPath Moved(Vector2I by)
    {
        var cells = new Vector2I[Cells.Count];
        for (int i = 0; i < cells.Length; i++)
            cells[i] = Cells[i] + by;
        return this with { Cells = cells };
    }

    public override string ToString() =>
        $"{Road.Say(Type)} " + string.Join(" ", Cells);
}

/// <summary>
/// The roads of format 3 (docs/map-format3-plan.md, Ф6): paths along the hex
/// sides, as a tank drives - it crosses a hex only side to side, so the road's
/// axis runs from the middle of the side it comes in by to the middle of the
/// side it leaves by, straight across opposite sides and otherwise the arc
/// that meets both square (<see cref="Through"/>). The road is one side wide.
///
/// <b>A path, not a grid of letters.</b> Two roads in neighbouring cells do not
/// say whether they join; a path does. A fork is a path that starts on a cell
/// of another, and leaves it by the side the other came in by.
/// </summary>
public static class Road
{
    /// <summary>The road's half width, in circumradii: a hex side is one
    /// circumradius long.</summary>
    public const float Half = 0.5f;

    public static string Say(RoadType type) => type.ToString().ToLowerInvariant();

    public static RoadType? Named(string said)
    {
        foreach (RoadType t in Enum.GetValues<RoadType>())
            if (Say(t) == said.ToLowerInvariant())
                return t;
        return null;
    }

    public static IEnumerable<string> Names
    {
        get
        {
            foreach (RoadType t in Enum.GetValues<RoadType>())
                yield return Say(t);
        }
    }

    /// <summary>Every cell any road crosses.</summary>
    public static HashSet<Vector2I> Cells(IReadOnlyList<RoadPath> roads)
    {
        var cells = new HashSet<Vector2I>();
        foreach (RoadPath road in roads)
            foreach (Vector2I c in road.Cells)
                cells.Add(c);
        return cells;
    }

    /// <summary>
    /// What is wrong with a set of roads, each said once: a road of one cell,
    /// a step to a cell that is no neighbour, a step straight back, a cell
    /// twice on one road, a cell no road may cross (<paramref name="refuses"/>
    /// answers why, or null), and a stretch two roads share - a road drawn
    /// twice, of which one would quietly not show.
    /// </summary>
    public static List<string> Faults(IReadOnlyList<RoadPath> roads, Func<Vector2I, string?> refuses)
    {
        var wrong = new List<string>();
        var stretches = new Dictionary<(Vector2I, Vector2I), int>();
        for (int k = 0; k < roads.Count; k++)
        {
            IReadOnlyList<Vector2I> p = roads[k].Cells;
            string at = $"road {k + 1} ({Say(roads[k].Type)})";
            if (p.Count < 2)
            {
                wrong.Add($"{at} is {p.Count} cell - a road joins two");
                continue;
            }
            var seen = new HashSet<Vector2I>();
            for (int i = 0; i < p.Count; i++)
            {
                if (!seen.Add(p[i]))
                    wrong.Add($"{at} crosses ({p[i].X},{p[i].Y}) twice");
                if (refuses(p[i]) is { } why)
                    wrong.Add($"{at} crosses ({p[i].X},{p[i].Y}), {why}");
                if (i == 0)
                    continue;
                if (HexField.HeadingTo(p[i - 1], p[i]) < 0)
                    wrong.Add($"{at} steps from ({p[i - 1].X},{p[i - 1].Y}) to ({p[i].X},{p[i].Y}), "
                              + "which is no neighbour");
                else if (i >= 2 && p[i] == p[i - 2])
                    wrong.Add($"{at} turns straight back at ({p[i - 1].X},{p[i - 1].Y})");
                (Vector2I, Vector2I) key = Less(p[i - 1], p[i]) ? (p[i - 1], p[i]) : (p[i], p[i - 1]);
                if (stretches.TryGetValue(key, out int other) && other != k)
                    wrong.Add($"roads {other + 1} and {k + 1} share ({key.Item1.X},{key.Item1.Y})-"
                              + $"({key.Item2.X},{key.Item2.Y})");
                else
                    stretches[key] = k;
            }
        }
        return wrong;
    }

    private static bool Less(Vector2I a, Vector2I b) => a.Y < b.Y || (a.Y == b.Y && a.X < b.X);

    /// <summary>One road's axis in world XZ, densely sampled, with the arc
    /// length at each point, both in world units.</summary>
    public readonly record struct Axis(Vector2[] Points, float[] Along, RoadType Type);

    /// <summary>
    /// Every road's axis, given each cell's middle in world XZ. A road's end
    /// cell is crossed straight to its far side, unless it lies on another road:
    /// a fork leaves its cell by the other road's way in, a road that runs into
    /// another goes on by the other's way out - the two share that half of the
    /// hex, as the renders drew it.
    /// </summary>
    public static List<Axis> Axes(IReadOnlyList<RoadPath> roads, Func<Vector2I, Vector2> middle,
                                  float step)
    {
        var axes = new List<Axis>();
        for (int k = 0; k < roads.Count; k++)
        {
            IReadOnlyList<Vector2I> cells = roads[k].Cells;
            if (cells.Count < 2)
                continue;
            int n = cells.Count;
            var at = new Vector2[n];
            for (int i = 0; i < n; i++)
                at[i] = middle(cells[i]);
            Vector2 before = Neighbour(roads, k, cells[0], cells[1], -1) is Vector2I b
                ? middle(b) : 2.0f * at[0] - at[1];
            Vector2 after = Neighbour(roads, k, cells[n - 1], cells[n - 2], +1) is Vector2I a
                ? middle(a) : 2.0f * at[n - 1] - at[n - 2];
            var points = new List<Vector2>();
            for (int i = 0; i < n; i++)
            {
                Vector2 prev = i == 0 ? before : at[i - 1];
                Vector2 next = i == n - 1 ? after : at[i + 1];
                Through(points, (prev + at[i]) * 0.5f, (at[i] - prev).Normalized(),
                        (at[i] + next) * 0.5f, (next - at[i]).Normalized(), step);
            }
            points.Add((at[n - 1] + after) * 0.5f);
            var along = new float[points.Count];
            for (int i = 1; i < points.Count; i++)
                along[i] = along[i - 1] + points[i].DistanceTo(points[i - 1]);
            axes.Add(new Axis(points.ToArray(), along, roads[k].Type));
        }
        return axes;
    }

    /// <summary>How far a point lies from the nearest of the axes.</summary>
    public static float Distance(List<Axis> axes, Vector2 p)
    {
        float best = float.MaxValue;
        foreach (Axis a in axes)
            for (int i = 0; i + 1 < a.Points.Length; i++)
            {
                Vector2 p0 = a.Points[i], seg = a.Points[i + 1] - p0;
                float t = Mathf.Clamp((p - p0).Dot(seg) / Mathf.Max(seg.LengthSquared(), 1e-6f), 0.0f, 1.0f);
                best = Mathf.Min(best, p.DistanceTo(p0 + seg * t));
            }
        return best;
    }

    /// <summary>The cell another road takes before (<paramref name="way"/>
    /// -1) or after (+1) <paramref name="cell"/>, where this road's end lies on
    /// it - and is not the cell this road goes on to.</summary>
    private static Vector2I? Neighbour(IReadOnlyList<RoadPath> roads, int self, Vector2I cell,
                                       Vector2I inner, int way)
    {
        for (int k = 0; k < roads.Count; k++)
        {
            if (k == self)
                continue;
            IReadOnlyList<Vector2I> p = roads[k].Cells;
            for (int i = 0; i < p.Count; i++)
                if (p[i] == cell && i + way >= 0 && i + way < p.Count && p[i + way] != inner)
                    return p[i + way];
        }
        return null;
    }

    /// <summary>
    /// The axis across one hex, from the side it comes in by (<paramref name="a"/>,
    /// heading <paramref name="da"/>) to the side it leaves by: straight across
    /// opposite sides, otherwise the circle arc perpendicular to both - its
    /// centre where the two sides' normals cross, so 60 degrees is a gentle
    /// bend and 120 a hairpin round the shared corner (roads.py, through).
    /// </summary>
    private static void Through(List<Vector2> into, Vector2 a, Vector2 da, Vector2 b, Vector2 db,
                                float step)
    {
        float cross = da.X * db.Y - da.Y * db.X;
        if (Mathf.Abs(cross) < 1e-4f)
        {
            int n = Mathf.Max(2, (int)(a.DistanceTo(b) / step));
            for (int i = 0; i < n; i++)
                into.Add(a.Lerp(b, (float)i / n));
            return;
        }
        var na = new Vector2(-da.Y, da.X);
        var nb = new Vector2(-db.Y, db.X);
        // a + na t = b + nb u, solved for t
        Vector2 d = b - a;
        float det = na.X * -nb.Y - (-nb.X) * na.Y;
        float t = (d.X * -nb.Y - (-nb.X) * d.Y) / det;
        Vector2 c = a + na * t;
        float a0 = Mathf.Atan2(a.Y - c.Y, a.X - c.X), a1 = Mathf.Atan2(b.Y - c.Y, b.X - c.X);
        float turn = Mathf.PosMod(a1 - a0 + Mathf.Pi, Mathf.Tau) - Mathf.Pi;
        float radius = Mathf.Abs(t);
        int m = Mathf.Max(3, (int)(Mathf.Abs(turn) * radius / step));
        for (int i = 0; i < m; i++)
        {
            float ang = a0 + turn * i / m;
            into.Add(c + new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * radius);
        }
    }
}
