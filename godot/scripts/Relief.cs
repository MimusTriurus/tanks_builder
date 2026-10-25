using System;
using System.Collections.Generic;
using System.IO;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The board as smooth fields rather than prisms (BOT-11, <c>--relief</c>):
/// the hexes rasterised on a fine grid and blurred, the way MA_ContentMaker's
/// <c>pipeline/hex_map.py</c> (<c>Fields</c>) and <c>hex_level.py</c> (<c>_data</c>)
/// make a level out of a hex map - here at load, in the engine, from the
/// <see cref="HexField"/> the bench already filled.
///
/// <b>The raster is in world XZ, not in flat space.</b> Flat space is the
/// screen's, its rows squashed by the camera (<see cref="HexField.Squash"/>);
/// a blur there would be twice as long across the board as up it. A raster
/// point is <c>(x, z)</c> of <c>Stage3D.World</c>: <c>x = flat.X</c>,
/// <c>z = flat.Y / squash</c>, so one sigma is one distance on the ground.
///
/// <b>The height is the prisms' own surface, blurred.</b> <see cref="HexField.TopOn"/>
/// of the cell under each point - a ramp a tilted plane, flat deep water its
/// drawn bed (<see cref="HexField.BedAt"/>) - then a Gaussian of
/// <see cref="Sigma"/> circumradii. A ramp is already a slope a hex long and
/// stays a gentle one; a level step with no ramp is a wall and becomes a steep
/// bank about <c>4 sigma</c> wide. So a climb and a cliff still read apart with
/// no honeycomb drawn. Heights are in the board's unit, screen px of lift off
/// the datum, the same number <see cref="HexField.TopAtPoint"/> answers.
///
/// <b>The rules do not read this.</b> Levels, ramps, passability and the line
/// of fire stay the hexes'; this is the surface a tank and its effects stand
/// on and the ground is drawn as.
/// </summary>
public sealed class Relief
{
    /// <summary>The blur of the height, in circumradii (hex_map's
    /// <c>COAST_BLUR</c> is 0.45, its <c>KIND_BLUR</c> 0.35; ours is a little
    /// tighter, because a level here is a rule and wants to stay legible).</summary>
    public const float Sigma = 0.3f;

    /// <summary>The blur where the levels are joined by slopes
    /// (<see cref="HexField.Slopes"/>): no ramp is left to stand for the way
    /// up, so every bank is one - about a hex wide, gentle enough to read as
    /// driven over, not a cliff with its edge rounded.</summary>
    public const float SlopeSigma = 0.45f;

    /// <summary>The blur of the kinds' shares, in circumradii (hex_map's
    /// <c>KIND_BLUR</c>).</summary>
    public const float KindSigma = 0.35f;

    /// <summary>The raster step, in circumradii: about 15 px on the bench's
    /// tile, 16 points across a hex.</summary>
    public const float Step = 0.125f;

    /// <summary>Ground past the board's edge on every side, in circumradii -
    /// hex_map's <c>MARGIN</c>, smaller: our board is the whole of the scene.</summary>
    public const float Margin = 1.5f;

    /// <summary>The plain's slow swell, in levels (hex_level's <c>SWELL</c>).</summary>
    public const float Swell = 0.08f;

    /// <summary>A rock's crest, in levels over its blurred height.</summary>
    public const float Crag = 0.35f;

    public enum Kind { Plain, Sand, Mud, Rock, Forest, Water }

    public readonly int Nx, Ny;
    /// <summary>World x, z of raster point (0, 0), and the step between points.</summary>
    public readonly float X0, Z0, Cell;
    public readonly float Squash;
    /// <summary>Height of every point, row-major (z then x), lift px.</summary>
    public readonly float[] Height;
    /// <summary>The water's surface over a point, lift px; NaN where no water
    /// is within a hex.</summary>
    public readonly float[] Surface;
    /// <summary>Signed distance to the waterline, world px: + dry, - wet.</summary>
    public readonly float[] Shore;
    /// <summary>Each kind's blurred share, 0..1, indexed by <see cref="Kind"/>.</summary>
    public readonly float[][] Share;
    /// <summary>The hex under each point, or (-1, -1) off the board.</summary>
    public readonly Vector2I[] Hex;

    public float Seconds { get; private set; }

    private Relief(int nx, int ny, float x0, float z0, float cell, float squash)
    {
        Nx = nx; Ny = ny; X0 = x0; Z0 = z0; Cell = cell; Squash = squash;
        int n = nx * ny;
        Height = new float[n];
        Surface = new float[n];
        Shore = new float[n];
        Hex = new Vector2I[n];
        Share = new float[Enum.GetValues<Kind>().Length][];
        for (int k = 0; k < Share.Length; k++)
            Share[k] = new float[n];
    }

    /// <summary>The fields of a filled board, its <see cref="HexField.Atlas"/>
    /// set. Null without one - there is no hex size to raster by.</summary>
    public static Relief? Build(HexField field, int seed = 11)
    {
        if (field.Atlas is null)
            return null;
        ulong t0 = Time.GetTicksUsec();
        float squash = field.Squash;
        float r = field.Atlas.HexRect.Size.X * 0.5f;
        float cell = r * Step;
        float lo = float.MaxValue, hi = float.MinValue, top = float.MaxValue, bottom = float.MinValue;
        for (int q = 0; q < field.Columns; q++)
            for (int row = 0; row < field.Rows; row++)
            {
                Vector2 c = field.FlatAnchor(q, row) + field.CentreOffset;
                lo = Mathf.Min(lo, c.X); hi = Mathf.Max(hi, c.X);
                top = Mathf.Min(top, c.Y / squash); bottom = Mathf.Max(bottom, c.Y / squash);
            }
        float pad = r * (1.0f + Margin);
        float x0 = lo - pad, z0 = top - pad;
        int nx = (int)Mathf.Ceil((hi - lo + 2 * pad) / cell) + 1;
        int ny = (int)Mathf.Ceil((bottom - top + 2 * pad) / cell) + 1;
        var f = new Relief(nx, ny, x0, z0, cell, squash);
        f.Fill(field, r, seed);
        f.Seconds = (Time.GetTicksUsec() - t0) / 1e6f;
        return f;
    }

    private void Fill(HexField field, float r, int seed)
    {
        int n = Nx * Ny;
        float lift = field.Lift;

        // Off the plot - the margin, and a holed board's holes - the board's
        // lowest dry ground: a lowland round it rather than a cliff into nothing.
        float floor = float.MaxValue;
        for (int q = 0; q < field.Columns; q++)
            for (int row = 0; row < field.Rows; row++)
            {
                var c = new Vector2I(q, row);
                if (field.InBounds(c) && !field.IsWater(c))
                    floor = Mathf.Min(floor, field.TopAt(c));
            }
        if (floor == float.MaxValue)
            floor = 0.0f;

        var raw = new float[n];
        var mask = new float[Share.Length][];
        for (int k = 0; k < mask.Length; k++)
            mask[k] = new float[n];
        for (int j = 0; j < Ny; j++)
            for (int i = 0; i < Nx; i++)
            {
                int p = j * Nx + i;
                Vector2 flat = Flat(i, j);
                Vector2I c = field.FlatCellAt(flat);
                bool on = field.InBounds(c);
                Hex[p] = on ? c : new Vector2I(-1, -1);
                if (!on)
                {
                    // Past the rectangle the edge cell's ground goes on, so the
                    // board does not stand on a bank of its own; a hole in the
                    // plot, or water at the edge, is the lowland.
                    Vector2I edge = field.ClampCell(c);
                    raw[p] = edge != c && field.InBounds(edge) && !field.IsWater(edge)
                        ? field.TopAt(edge) : floor;
                    mask[(int)Kind.Plain][p] = 1.0f;
                    continue;
                }
                raw[p] = field.IsDeep(c) && !field.IsRamp(c) ? field.BedAt(c) : field.TopOn(c, flat);
                mask[(int)KindOf(field, c)][p] = 1.0f;
            }

        float sig = (field.Slopes ? SlopeSigma : Sigma) * r / Cell;
        Array.Copy(raw, Height, n);
        Blur(Height, Nx, Ny, sig);
        for (int k = 0; k < Share.Length; k++)
        {
            Array.Copy(mask[k], Share[k], n);
            Blur(Share[k], Nx, Ny, KindSigma * r / Cell);
        }

        // The swell on dry plain, the crest on rock (hex_level's SWELL, its
        // chasm rim's noise).
        var noise = new FastNoiseLite
        {
            Seed = seed,
            NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin,
            Frequency = 1.0f / (r * 1.6f),
        };
        var grain = new FastNoiseLite
        {
            Seed = seed + 1,
            NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin,
            Frequency = 1.0f / (r * 0.35f),
        };
        float[] water = Share[(int)Kind.Water], rock = Share[(int)Kind.Rock];
        for (int j = 0; j < Ny; j++)
            for (int i = 0; i < Nx; i++)
            {
                int p = j * Nx + i;
                float x = X0 + i * Cell, z = Z0 + j * Cell;
                float dry = 1.0f - Mathf.SmoothStep(0.0f, 0.5f, water[p]);
                float swell = noise.GetNoise2D(x, z) * 0.6f + grain.GetNoise2D(x, z) * 0.4f;
                Height[p] += lift * Swell * swell * dry;
                float crag = Mathf.SmoothStep(0.2f, 0.7f, rock[p]);
                Height[p] += lift * Crag * crag * (0.5f + 0.5f * grain.GetNoise2D(x * 1.7f, z * 1.7f));
            }

        FillWater(field);
    }

    /// <summary>The water's surface over each point and the signed distance to
    /// where the ground comes out of it.
    ///
    /// <b>The waterline is where the blurred ground crosses the surface, not
    /// where the water's share is a half.</b> One body is one surface
    /// (<see cref="HexField.WaterTop"/>, the waterfall guard makes it so); a
    /// point takes the highest surface among its own hex and the six round it,
    /// which reaches as far as the blur carries a bed into a bank. What lies
    /// under it is wet - so the shore comes out round, and as far into the
    /// bank as the ground really dips under the surface.</summary>
    private void FillWater(HexField field)
    {
        int n = Nx * Ny;
        var wet = new bool[n];
        for (int p = 0; p < n; p++)
        {
            Surface[p] = float.NaN;
            Vector2I c = Hex[p];
            if (c.X < 0)
                continue;
            float best = float.NaN;
            if (field.IsWater(c))
                best = field.WaterTop(c);
            for (int h = 30; h < 360; h += 60)
            {
                Vector2I o = HexField.Step(c, h);
                if (field.InBounds(o) && field.IsWater(o))
                {
                    float s = field.WaterTop(o);
                    if (float.IsNaN(best) || s > best)
                        best = s;
                }
            }
            Surface[p] = best;
            wet[p] = !float.IsNaN(best) && Height[p] < best;
        }
        var toWet = Distance(wet, Nx, Ny);
        var invert = new bool[n];
        for (int p = 0; p < n; p++)
            invert[p] = !wet[p];
        var toDry = Distance(invert, Nx, Ny);
        for (int p = 0; p < n; p++)
            Shore[p] = (toWet[p] - toDry[p]) * Cell;
    }

    private static Kind KindOf(HexField field, Vector2I c)
    {
        if (field.IsWater(c))
            return Kind.Water;
        switch (field.FoundationAt(c))
        {
            case Foundation.Sand: return Kind.Sand;
            case Foundation.Mud: return Kind.Mud;
            case Foundation.Rock: return Kind.Rock;
        }
        return field.CoverAt(c) == Cover.Forest ? Kind.Forest : Kind.Plain;
    }

    // --- reading it -------------------------------------------------------

    /// <summary>The flat-space point of raster point (i, j).</summary>
    public Vector2 Flat(int i, int j) => new(X0 + i * Cell, (Z0 + j * Cell) * Squash);

    /// <summary>The height under a flat-space point, lift px, bilinear;
    /// clamped to the raster's edge.</summary>
    public float HeightAt(Vector2 flat) => Sample(Height, flat.X, flat.Y / Squash);

    /// <summary>A field's value under a world x, z, bilinear.</summary>
    public float Sample(float[] a, float x, float z)
    {
        float fi = Mathf.Clamp((x - X0) / Cell, 0.0f, Nx - 1.001f);
        float fj = Mathf.Clamp((z - Z0) / Cell, 0.0f, Ny - 1.001f);
        int i = (int)fi, j = (int)fj;
        float u = fi - i, v = fj - j;
        int p = j * Nx + i;
        float top = Mathf.Lerp(a[p], a[p + 1], u);
        float bottom = Mathf.Lerp(a[p + Nx], a[p + Nx + 1], u);
        return Mathf.Lerp(top, bottom, v);
    }

    // --- raster helpers (hex_map's) ----------------------------------------

    /// <summary>A Gaussian of <paramref name="sigma"/> points by three box
    /// passes per axis, edges clamped (hex_map's <c>blur</c>).</summary>
    public static void Blur(float[] a, int nx, int ny, float sigma)
    {
        int half = Math.Max(0, (int)Mathf.Round((Mathf.Sqrt(4.0f * sigma * sigma + 1.0f) - 1.0f) / 2.0f));
        if (half == 0)
            return;
        var line = new float[Math.Max(nx, ny)];
        var sum = new double[Math.Max(nx, ny) + 1];
        for (int pass = 0; pass < 3; pass++)
        {
            for (int j = 0; j < ny; j++)
                Box(a, j * nx, 1, nx, half, line, sum);
            for (int i = 0; i < nx; i++)
                Box(a, i, nx, ny, half, line, sum);
        }
    }

    private static void Box(float[] a, int start, int stride, int len, int half, float[] line, double[] sum)
    {
        sum[0] = 0.0;
        for (int k = 0; k < len; k++)
            sum[k + 1] = sum[k] + a[start + k * stride];
        float width = 2 * half + 1;
        for (int k = 0; k < len; k++)
        {
            // edges clamped: the missing samples are the edge's own value
            int lo = k - half, hi = k + half;
            double s = sum[Math.Min(hi, len - 1) + 1] - sum[Math.Max(lo, 0)];
            if (lo < 0)
                s += -lo * a[start];
            if (hi > len - 1)
                s += (hi - len + 1) * a[start + (len - 1) * stride];
            line[k] = (float)(s / width);
        }
        for (int k = 0; k < len; k++)
            a[start + k * stride] = line[k];
    }

    /// <summary>Exact Euclidean distance, in points, from every point to the
    /// nearest true one (Felzenszwalb and Huttenlocher, separable).</summary>
    public static float[] Distance(bool[] mask, int nx, int ny)
    {
        const float Big = 1e20f;
        int n = nx * ny;
        var g = new float[n];
        for (int p = 0; p < n; p++)
            g[p] = mask[p] ? 0.0f : Big;
        int most = Math.Max(nx, ny);
        var f = new float[most];
        var d = new float[most];
        var v = new int[most];
        var zz = new float[most + 1];
        for (int i = 0; i < nx; i++)
        {
            for (int j = 0; j < ny; j++)
                f[j] = g[j * nx + i];
            Line(f, ny, d, v, zz);
            for (int j = 0; j < ny; j++)
                g[j * nx + i] = d[j];
        }
        for (int j = 0; j < ny; j++)
        {
            for (int i = 0; i < nx; i++)
                f[i] = g[j * nx + i];
            Line(f, nx, d, v, zz);
            for (int i = 0; i < nx; i++)
                g[j * nx + i] = Mathf.Sqrt(d[i]);
        }
        return g;
    }

    private static void Line(float[] f, int n, float[] d, int[] v, float[] z)
    {
        int k = 0;
        v[0] = 0;
        z[0] = float.NegativeInfinity;
        z[1] = float.PositiveInfinity;
        for (int q = 1; q < n; q++)
        {
            float s = ((f[q] + q * q) - (f[v[k]] + v[k] * v[k])) / (2.0f * q - 2.0f * v[k]);
            while (s <= z[k])
            {
                k--;
                s = ((f[q] + q * q) - (f[v[k]] + v[k] * v[k])) / (2.0f * q - 2.0f * v[k]);
            }
            k++;
            v[k] = q;
            z[k] = s;
            z[k + 1] = float.PositiveInfinity;
        }
        k = 0;
        for (int q = 0; q < n; q++)
        {
            while (z[k + 1] < q)
                k++;
            d[q] = (q - v[k]) * (q - v[k]) + f[v[k]];
        }
    }

    // --- the schematic ----------------------------------------------------

    private static readonly Color[] KindColour =
    {
        new(0.89f, 0.86f, 0.77f),   // plain
        new(0.93f, 0.84f, 0.65f),   // sand
        new(0.48f, 0.40f, 0.30f),   // mud
        new(0.55f, 0.53f, 0.52f),   // rock
        new(0.25f, 0.36f, 0.23f),   // forest
        new(0.47f, 0.66f, 0.80f),   // water
    };

    /// <summary>The fields as two pictures, as hex_map's schematic is looked at
    /// before Blender: <c>_kinds</c> - the shares mixed, shaded by the height,
    /// the waterline and the hexes over them - and <c>_height</c>, grey from
    /// the lowest point to the highest. Returns the kinds picture's path.</summary>
    public string SavePictures(HexField field, string dir, string name)
    {
        Directory.CreateDirectory(dir);
        float lo = float.MaxValue, hi = float.MinValue;
        foreach (float h in Height)
        {
            lo = Mathf.Min(lo, h);
            hi = Mathf.Max(hi, h);
        }
        float span = Mathf.Max(hi - lo, 1e-3f);
        float r = field.Atlas!.HexRect.Size.X * 0.5f;
        var kinds = Image.CreateEmpty(Nx, Ny, false, Image.Format.Rgb8);
        var height = Image.CreateEmpty(Nx, Ny, false, Image.Format.Rgb8);
        for (int j = 0; j < Ny; j++)
            for (int i = 0; i < Nx; i++)
            {
                int p = j * Nx + i;
                float t = (Height[p] - lo) / span;
                height.SetPixel(i, j, new Color(t, t, t));

                Color c = new(0, 0, 0);
                float total = 0.0f;
                for (int k = 0; k < Share.Length; k++)
                {
                    c += KindColour[k] * Share[k][p];
                    total += Share[k][p];
                }
                c /= Mathf.Max(total, 1e-3f);
                if (Shore[p] < 0.0f)
                    c = c.Lerp(KindColour[(int)Kind.Water], 0.75f);
                // shaded by the slope toward the upper left, as the sun stands
                float dx = Height[Math.Min(p + 1, p - i + Nx - 1)] - Height[Math.Max(p - 1, p - i)];
                float dz = Height[Math.Min(p + Nx, Nx * Ny - 1)] - Height[Math.Max(p - Nx, 0)];
                float shade = Mathf.Clamp(1.0f - (dx + dz) / (4.0f * Cell), 0.6f, 1.25f);
                c *= shade * (0.8f + 0.25f * t);
                if (Mathf.Abs(Shore[p]) < Cell * 0.75f)
                    c = new Color(0.12f, 0.2f, 0.3f);
                if (OnHexLine(field, p, r))
                    c = c.Lerp(new Color(0.31f, 0.25f, 0.33f), 0.6f);
                kinds.SetPixel(i, j, c);
            }
        // a point is 15 px of board: three pixels a point to be looked at
        kinds.Resize(Nx * 3, Ny * 3, Image.Interpolation.Nearest);
        height.Resize(Nx * 3, Ny * 3, Image.Interpolation.Bilinear);
        string path = $"{dir}/{name}_kinds.png";
        kinds.SavePng(path);
        height.SavePng($"{dir}/{name}_height.png");
        return path;
    }

    /// <summary>Whether a raster point lies on a hex's side: a neighbour point
    /// is under another hex.</summary>
    private bool OnHexLine(HexField field, int p, float r)
    {
        Vector2I me = Hex[p];
        if (me.X < 0)
            return false;
        int i = p % Nx;
        return (i + 1 < Nx && Hex[p + 1] != me && Hex[p + 1].X >= 0)
               || (p + Nx < Hex.Length && Hex[p + Nx] != me && Hex[p + Nx].X >= 0);
    }
}
