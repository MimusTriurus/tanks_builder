using System;
using System.Collections.Generic;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The tide's rise as it is seen (GameModes.md, «Пат — прилив»; docs/tank3d.md,
/// «Прилив»): the board before and after one rise, and when the water reaches
/// each point of the relief's raster between them.
///
/// <b>The ground goes down, the sea stays where it is.</b> The rise floods
/// cells to the sea's level, so the water's sheet of the new board is the old
/// sea's own plane; what moves is the ground under it. A point sinks from its
/// old height to its new one over <see cref="Fall"/> from its
/// <see cref="Arrive"/>, and the water's depth clip turns that into a
/// shoreline crawling inland with its foam on it.
///
/// <b>When the water comes is a path, not a ring.</b> Arrival is the cheapest
/// way in from the old water through the points that change, a step dearer the
/// higher the ground above the sea: the hollows, the fords and the ravines fill
/// first, the brows last - the vessels' spill the rule floods by, drawn. A
/// grain of noise on each step tears the front into tongues.
/// </summary>
public sealed class TideFront
{
    /// <summary>The boards either side of the rise, on one raster.</summary>
    public readonly Relief Old, Next;
    /// <summary>When the water reaches each point, s from the rise; <see cref="Never"/>
    /// where the ground stays as it was.</summary>
    public readonly float[] Arrive;
    /// <summary>The ground under each point at <see cref="Clock"/>, lift px -
    /// what <see cref="HeightAt"/> samples.</summary>
    public readonly float[] Now;
    /// <summary>The points that change, earliest arrival first.</summary>
    public readonly int[] Order;
    /// <summary>The time since the rise, s.</summary>
    public float Clock { get; private set; }
    /// <summary>What changed, for the log.</summary>
    public static string Stats = "";

    public const float Never = 1.0e4f;
    /// <summary>How long the water takes from the old shore to the last point, s.</summary>
    public const float Spread = 2.2f;
    /// <summary>How long one point takes to go under, s.</summary>
    public const float Fall = 0.7f;
    /// <summary>How much dearer a step is per level above the sea.</summary>
    public const float Climb = 1.6f;
    /// <summary>How far a step's price wanders either way, a share.</summary>
    public const float Grain = 0.35f;

    /// <summary>The rise played out: every point under, and a breath after.</summary>
    public float End => Spread + Fall + 0.3f;

    private TideFront(Relief old, Relief next, float[] arrive, int[] order)
    {
        Old = old;
        Next = next;
        Arrive = arrive;
        Order = order;
        Now = (float[])old.Height.Clone();
    }

    /// <summary>The front between two boards, or null when their rasters differ
    /// (a board of another size: the rise is shown at once).</summary>
    public static TideFront? Of(Relief old, Relief next, float lift)
    {
        if (old.Nx != next.Nx || old.Ny != next.Ny || old.Cell != next.Cell
            || old.X0 != next.X0 || old.Z0 != next.Z0)
            return null;
        int nx = next.Nx, ny = next.Ny, n = nx * ny;
        var changed = new bool[n];
        int count = 0, byHeight = 0, byWet = 0;
        float most0 = 0.0f;
        for (int p = 0; p < n; p++)
        {
            bool wetOld = !float.IsNaN(old.Surface[p]) && old.Shore[p] < 0.0f;
            bool wetNew = !float.IsNaN(next.Surface[p]) && next.Shore[p] < 0.0f;
            float dh = Math.Abs(old.Height[p] - next.Height[p]);
            most0 = Math.Max(most0, dh);
            if (dh > 0.5f) byHeight++;
            if (wetOld != wetNew) byWet++;
            changed[p] = dh > 0.5f || wetOld != wetNew;
            if (changed[p])
                count++;
        }
        Stats = $"{byHeight} by height (most {most0:0.0} px), {byWet} by wetness, of {n}";
        var arrive = new float[n];
        Array.Fill(arrive, Never);
        if (count == 0)
            return new TideFront(old, next, arrive, Array.Empty<int>());

        // the sea's plane over the new water: the level a point climbs from
        float sea = float.NaN;
        for (int p = 0; p < n && float.IsNaN(sea); p++)
            if (changed[p] && !float.IsNaN(next.Surface[p]))
                sea = next.Surface[p];
        if (float.IsNaN(sea))
            sea = 0.0f;
        float level = Math.Max(lift, 1.0f);

        var dist = new float[n];
        Array.Fill(dist, float.PositiveInfinity);
        var queue = new PriorityQueue<int, float>();
        // the old water next to a changed point: where the front sets out
        for (int p = 0; p < n; p++)
        {
            if (changed[p] || float.IsNaN(old.Surface[p]) || old.Shore[p] >= 0.0f)
                continue;
            if (!Touches(changed, p, nx, ny))
                continue;
            dist[p] = 0.0f;
            queue.Enqueue(p, 0.0f);
        }
        // no water beside it (a board without a sea): from its own rim
        if (queue.Count == 0)
            for (int p = 0; p < n; p++)
                if (changed[p] && Touches(changed, p, nx, ny, unchanged: true))
                {
                    dist[p] = 0.0f;
                    queue.Enqueue(p, 0.0f);
                }
        while (queue.TryDequeue(out int p, out float d))
        {
            if (d > dist[p])
                continue;
            int i = p % nx, j = p / nx;
            for (int dj = -1; dj <= 1; dj++)
                for (int di = -1; di <= 1; di++)
                {
                    if (di == 0 && dj == 0)
                        continue;
                    int qi = i + di, qj = j + dj;
                    if (qi < 0 || qj < 0 || qi >= nx || qj >= ny)
                        continue;
                    int q = qj * nx + qi;
                    if (!changed[q])
                        continue;
                    float above = Math.Max(0.0f, old.Height[q] - sea) / level;
                    float grain = 1.0f + Grain * (Hash(q) * 2.0f - 1.0f);
                    float step = (di != 0 && dj != 0 ? 1.4142f : 1.0f) * (1.0f + Climb * above) * grain;
                    float nd = d + step;
                    if (nd < dist[q])
                    {
                        dist[q] = nd;
                        queue.Enqueue(q, nd);
                    }
                }
        }
        float most = 0.0f;
        for (int p = 0; p < n; p++)
            if (changed[p] && !float.IsPositiveInfinity(dist[p]))
                most = Math.Max(most, dist[p]);
        var order = new int[count];
        int k = 0;
        for (int p = 0; p < n; p++)
        {
            if (!changed[p])
                continue;
            // a point the walk never reached goes with the last
            arrive[p] = float.IsPositiveInfinity(dist[p]) || most <= 0.0f
                ? Spread
                : dist[p] / most * Spread;
            order[k++] = p;
        }
        Array.Sort(order, (a, b) => arrive[a].CompareTo(arrive[b]));
        return new TideFront(old, next, arrive, order);
    }

    private static bool Touches(bool[] changed, int p, int nx, int ny, bool unchanged = false)
    {
        int i = p % nx, j = p / nx;
        for (int dj = -1; dj <= 1; dj++)
            for (int di = -1; di <= 1; di++)
            {
                int qi = i + di, qj = j + dj;
                if ((di == 0 && dj == 0) || qi < 0 || qj < 0 || qi >= nx || qj >= ny)
                    continue;
                if (changed[qj * nx + qi] != unchanged)
                    return true;
            }
        return false;
    }

    private static float Hash(int p)
    {
        uint h = (uint)p * 2654435761u;
        h ^= h >> 15;
        h *= 2246822519u;
        h ^= h >> 13;
        return (h & 0xFFFF) / 65535.0f;
    }

    /// <summary>How far under a point with arrival <paramref name="arrive"/> is
    /// at <paramref name="t"/>: 0 its old ground, 1 its new - the shaders' own
    /// smoothstep.</summary>
    public static float Sink(float arrive, float t)
    {
        float s = Mathf.Clamp((t - arrive) / Fall, 0.0f, 1.0f);
        return s * s * (3.0f - 2.0f * s);
    }

    /// <summary>The clock on: the ground under the changing points set again
    /// (no garbage: a frame's tick).</summary>
    public void Tick(float t)
    {
        Clock = t;
        float[] a = Old.Height, b = Next.Height;
        foreach (int p in Order)
            Now[p] = Mathf.Lerp(a[p], b[p], Sink(Arrive[p], t));
    }

    /// <summary>The ground under a flat-space point now, lift px - the
    /// field's <see cref="HexField.Surface"/> while the rise plays.</summary>
    public float HeightAt(Vector2 flat) => Next.Sample(Now, flat.X, flat.Y / Next.Squash);

    /// <summary>When the water reaches the point nearest a flat-space point,
    /// s; <see cref="Never"/> if it stays dry.</summary>
    public float ArriveAt(Vector2 flat)
    {
        int i = Mathf.Clamp(Mathf.RoundToInt((flat.X - Next.X0) / Next.Cell), 0, Next.Nx - 1);
        int j = Mathf.Clamp(Mathf.RoundToInt((flat.Y / Next.Squash - Next.Z0) / Next.Cell), 0, Next.Ny - 1);
        return Arrive[j * Next.Nx + i];
    }

    /// <summary>The way the front runs at a point: down its arrival's slope,
    /// in raster x, z; zero where it cannot tell.</summary>
    public Vector2 Way(int p)
    {
        int nx = Next.Nx, ny = Next.Ny, i = p % nx, j = p / nx;
        float At(int qi, int qj)
        {
            qi = Math.Clamp(qi, 0, nx - 1);
            qj = Math.Clamp(qj, 0, ny - 1);
            float a = Arrive[qj * nx + qi];
            return a >= Never ? Arrive[p] : a;
        }
        var g = new Vector2(At(i + 1, j) - At(i - 1, j), At(i, j + 1) - At(i, j - 1));
        return g.LengthSquared() > 1e-8f ? g.Normalized() : Vector2.Zero;
    }
}
