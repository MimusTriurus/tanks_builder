using System;
using System.Collections.Generic;
using Godot;

namespace TankSpriteTest;

/// <summary>What a rock is built as (docs/rocks-plan.md): its form in the
/// relief's raster. <see cref="Crag"/> is the rock as it always was - ridged
/// crags in a wall - and every rock nobody named.</summary>
public enum RockType { Crag, Mesa, Yardang, Moutonnee }

/// <summary>
/// A rock's paint: the layers' colours, its cap and its moss, and the numbers
/// the shader reads beside them. Paint only - the form is the type's
/// (<see cref="RockType"/>), and a palette changes without the raster.
/// Lengths in circumradii, as the renders that chose them (out/rocks/rocks.py).
/// </summary>
public sealed class RockPalette
{
    public required string Name { get; init; }
    /// <summary>The layers, chosen by a hash of each layer's number; six.</summary>
    public required Color[] Layers { get; init; }
    /// <summary>A mesa's cap; the moss of a roche moutonnee.</summary>
    public Color Cap { get; init; } = new(0.74f, 0.55f, 0.40f);
    /// <summary>The caprock's dark lip; a moutonnee's lichen.</summary>
    public Color Lip { get; init; } = new(0.50f, 0.35f, 0.28f);
    /// <summary>Scree and sand at the foot.</summary>
    public Color Scree { get; init; } = new(0.80f, 0.58f, 0.42f);
    /// <summary>A layer's thickness, circumradii; 0 - no layers.</summary>
    public float Thick { get; init; } = 0.075f;
    /// <summary>How much moss or scrub, 0-1.</summary>
    public float Moss { get; init; }
    /// <summary>How dark the desert varnish runs down the walls, 0-1.</summary>
    public float Varnish { get; init; } = 0.3f;
    /// <summary>A gneiss's own folded bands, 0-1.</summary>
    public float Bands { get; init; }

    private static Color C(float r, float g, float b) => new(r, g, b);

    private static readonly RockPalette[] All =
    {
        new() { Name = "grey", Layers = Six(C(0.58f, 0.56f, 0.54f)), Thick = 0.0f, Varnish = 0.0f },
        new()
        {
            Name = "red", Thick = 0.075f, Moss = 0.35f,
            Layers = new[] { C(0.80f, 0.50f, 0.34f), C(0.87f, 0.66f, 0.48f), C(0.72f, 0.41f, 0.29f),
                             C(0.90f, 0.75f, 0.59f), C(0.66f, 0.36f, 0.26f), C(0.84f, 0.58f, 0.40f) },
            Cap = C(0.74f, 0.55f, 0.40f), Lip = C(0.50f, 0.35f, 0.28f), Scree = C(0.80f, 0.58f, 0.42f),
        },
        new()
        {
            Name = "ochre", Thick = 0.10f, Moss = 0.35f,
            Layers = new[] { C(0.86f, 0.70f, 0.45f), C(0.78f, 0.60f, 0.38f), C(0.90f, 0.78f, 0.56f),
                             C(0.70f, 0.52f, 0.33f), C(0.86f, 0.70f, 0.45f), C(0.78f, 0.60f, 0.38f) },
            Cap = C(0.70f, 0.56f, 0.34f), Lip = C(0.52f, 0.39f, 0.25f), Scree = C(0.86f, 0.70f, 0.45f),
        },
        new()
        {
            Name = "painted", Thick = 0.05f, Moss = 0.35f,
            Layers = new[] { C(0.86f, 0.52f, 0.40f), C(0.92f, 0.86f, 0.76f), C(0.70f, 0.45f, 0.42f),
                             C(0.80f, 0.70f, 0.62f), C(0.62f, 0.40f, 0.38f), C(0.86f, 0.52f, 0.40f) },
            Cap = C(0.83f, 0.77f, 0.68f), Lip = C(0.47f, 0.30f, 0.29f), Scree = C(0.86f, 0.52f, 0.40f),
        },
        new()
        {
            Name = "buff", Thick = 0.04f, Varnish = 0.22f,
            Layers = new[] { C(0.80f, 0.72f, 0.58f), C(0.74f, 0.66f, 0.53f), C(0.84f, 0.77f, 0.63f),
                             C(0.70f, 0.62f, 0.50f), C(0.80f, 0.72f, 0.58f), C(0.74f, 0.66f, 0.53f) },
            Scree = C(0.86f, 0.76f, 0.55f),
        },
        new()
        {
            Name = "slate", Thick = 0.04f, Varnish = 0.22f,
            Layers = new[] { C(0.72f, 0.71f, 0.68f), C(0.64f, 0.63f, 0.61f), C(0.78f, 0.77f, 0.73f),
                             C(0.58f, 0.57f, 0.55f), C(0.72f, 0.71f, 0.68f), C(0.64f, 0.63f, 0.61f) },
            Scree = C(0.86f, 0.76f, 0.55f),
        },
        new()
        {
            Name = "pink", Thick = 0.04f, Varnish = 0.22f,
            Layers = new[] { C(0.86f, 0.66f, 0.52f), C(0.80f, 0.58f, 0.46f), C(0.90f, 0.74f, 0.60f),
                             C(0.74f, 0.52f, 0.42f), C(0.86f, 0.66f, 0.52f), C(0.80f, 0.58f, 0.46f) },
            Scree = C(0.86f, 0.76f, 0.55f),
        },
        new()
        {
            Name = "granite", Thick = 0.0f, Moss = 0.35f, Varnish = 0.0f,
            Layers = Six(C(0.64f, 0.63f, 0.60f)), Cap = C(0.33f, 0.42f, 0.22f), Lip = C(0.80f, 0.78f, 0.66f),
        },
        new()
        {
            Name = "dark", Thick = 0.0f, Moss = 0.15f, Varnish = 0.0f,
            Layers = Six(C(0.52f, 0.52f, 0.54f)), Cap = C(0.33f, 0.42f, 0.22f), Lip = C(0.80f, 0.78f, 0.66f),
        },
        new()
        {
            Name = "gneiss", Thick = 0.0f, Moss = 0.5f, Varnish = 0.0f, Bands = 0.35f,
            Layers = Six(C(0.70f, 0.60f, 0.56f)), Cap = C(0.33f, 0.42f, 0.22f), Lip = C(0.80f, 0.78f, 0.66f),
        },
    };

    private static Color[] Six(Color c) => new[] { c, c, c, c, c, c };

    public static RockPalette? Named(string name)
    {
        foreach (RockPalette p in All)
            if (p.Name == name)
                return p;
        return null;
    }

    /// <summary>The palette a type wears when none is named.</summary>
    public static RockPalette For(RockType type) => Named(type switch
    {
        RockType.Mesa => "red",
        RockType.Yardang => "buff",
        RockType.Moutonnee => "granite",
        _ => "grey",
    })!;
}

/// <summary>One rock's look: its type, its palette and the type's preset.</summary>
public readonly record struct RockLook(RockType Type, RockPalette Palette, int Preset)
{
    public static readonly RockLook Crag = new(RockType.Crag, RockPalette.For(RockType.Crag), 1);

    /// <summary><c>type[:palette[:preset]]</c>, as <c>--rock</c> says it; null
    /// with a word it does not know.</summary>
    public static RockLook? Parse(string said)
    {
        string[] part = said.Split(':');
        RockType? type = part[0].ToLowerInvariant() switch
        {
            "crag" => RockType.Crag,
            "mesa" => RockType.Mesa,
            "yardang" => RockType.Yardang,
            "moutonnee" => RockType.Moutonnee,
            _ => null,
        };
        if (type is not { } t)
            return null;
        RockPalette palette = RockPalette.For(t);
        if (part.Length > 1 && part[1].Length > 0)
        {
            if (RockPalette.Named(part[1].ToLowerInvariant()) is not { } p)
                return null;
            palette = p;
        }
        int preset = 1;
        if (part.Length > 2 && (!int.TryParse(part[2], out preset) || preset < 1 || preset > 3))
            return null;
        return new RockLook(t, palette, preset);
    }

    public override string ToString() => $"{Type.ToString().ToLowerInvariant()}:{Palette.Name}:{Preset}";
}

/// <summary>
/// The looks a board's rocks take, and its climate: the wind the yardangs lie
/// along and the way the ice ran over the moutonnees - one each a board, or the
/// ridges read as noise (docs/rocks-plan.md). Until the map carries them
/// (stage 3) the bench's <c>--rock</c> hands them out: one look to every rock,
/// or a list dealt out to the rocks in reading order.
/// </summary>
public sealed class RockStyles
{
    public readonly List<RockLook> Looks = new();
    /// <summary>The wind, degrees on the ground (0 along +X, 90 along +Z).</summary>
    public float Wind = 25.0f;
    /// <summary>Where the ice ran, degrees, the same way.</summary>
    public float Ice = -30.0f;

    public RockLook For(int rock) => Looks.Count == 0 ? RockLook.Crag : Looks[rock % Looks.Count];

    /// <summary>Whether every rock stays a crag: the relief is then built by
    /// the one path it always was.</summary>
    public bool AllCrag
    {
        get
        {
            foreach (RockLook look in Looks)
                if (look.Type != RockType.Crag)
                    return false;
            return true;
        }
    }

    /// <summary><c>mesa:ochre,yardang,moutonnee</c>; null and the word it
    /// did not know.</summary>
    public static RockStyles? Parse(string said, out string bad)
    {
        var styles = new RockStyles();
        bad = "";
        foreach (string one in said.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (RockLook.Parse(one.Trim()) is not { } look)
            {
                bad = one;
                return null;
            }
            styles.Looks.Add(look);
        }
        return styles.Looks.Count > 0 ? styles : null;
    }
}

/// <summary>A rock: a group of rock cells joined by their sides, and its look.</summary>
public sealed class RockGroup
{
    public required int Index { get; init; }
    public required List<Vector2I> Cells { get; init; }
    public required RockLook Look { get; init; }
    /// <summary>Its top, lift px: its highest cell's.</summary>
    public required float Top { get; init; }

    /// <summary>A mesa's ledge only on a rock of three cells or more (the
    /// user's call, 2026-10-27): on a smaller one the upper tier does not fit
    /// and the rock reads lower than its level.</summary>
    public const int BenchCells = 3;
}

/// <summary>
/// The rocks' forms in the relief's raster (docs/rocks-plan.md, stage 1): the
/// mesa, the yardangs and the roche moutonnee, each laid over its own rock's
/// window. The crag is <see cref="Relief.Fill"/>'s own, untouched.
///
/// <b>The raster holds the form, the shader the drawing.</b> Measured on the
/// game's raster (1/12 R) against a fine one: the walls, ledges, ridges and
/// backs survive the mesh, the scrub, striae and cracks laid in the raster
/// blur away - so they are drawn per pixel (<c>Stage3D.Relief</c>), and only
/// the form and masks as wide as a few points come from here.
///
/// <b>Every rock keeps to its cells.</b> The wall stands inside the rock's own
/// cells (its tight share past a half), the scree and sand lie between the
/// cell's side and the wall, and nothing climbs past the crag's ceiling
/// (<see cref="Relief.Crag"/> levels over the rock's top): the rules read the
/// cell's level for sight and fire, and a ridge drawn higher would hide what
/// the rules can see.
/// </summary>
internal static class ReliefRocks
{
    /// <summary>How far round its cells a rock's own points reach, in
    /// circumradii: past its blur, short of the next rock's (rocks never share
    /// a side, so two are at least a cell apart).</summary>
    private const float Reach = 0.55f;

    /// <summary>The renders' heights are in circumradii with a level a third
    /// of a hex: lift px per circumradius.</summary>
    private const float LevelR = 0.35f;

    private readonly record struct MesaShape(float Tear, float Scale, float Talus, float Cap, float Bench);
    private readonly record struct YardangShape(float Length, float Gap, float Tall);
    private readonly record struct MoutonneeShape(float Tall, float Block);

    private static readonly MesaShape[] Mesas =
    {
        new(0.55f, 0.9f, 0.38f, 0.08f, 0.0f),
        new(0.40f, 0.6f, 0.30f, 0.10f, 0.55f),
        new(0.80f, 1.3f, 0.45f, 0.04f, 0.0f),
    };

    private static readonly YardangShape[] Yardangs =
    {
        new(2.4f, 0.42f, 0.95f),
        new(3.4f, 0.30f, 0.85f),
        new(1.3f, 0.62f, 1.25f),
    };

    private static readonly MoutonneeShape[] Moutonnees =
    {
        new(1.1f, 0.20f),
        new(1.3f, 0.30f),
        new(0.8f, 0.14f),
    };

    /// <summary>The field's rocks, in reading order of their first cell, each
    /// with the look the styles deal it.</summary>
    public static List<RockGroup> Groups(HexField field, RockStyles? styles)
    {
        var left = new HashSet<Vector2I>();
        for (int row = 0; row < field.Rows; row++)
            for (int q = 0; q < field.Columns; q++)
            {
                var c = new Vector2I(q, row);
                if (field.InBounds(c) && field.FoundationAt(c) == Foundation.Rock)
                    left.Add(c);
            }
        var groups = new List<RockGroup>();
        for (int row = 0; row < field.Rows; row++)
            for (int q = 0; q < field.Columns; q++)
            {
                var start = new Vector2I(q, row);
                if (!left.Remove(start))
                    continue;
                var cells = new List<Vector2I> { start };
                for (int k = 0; k < cells.Count; k++)
                    for (int h = 30; h < 360; h += 60)
                    {
                        Vector2I o = HexField.Step(cells[k], h);
                        if (left.Remove(o))
                            cells.Add(o);
                    }
                float top = float.MinValue;
                foreach (Vector2I c in cells)
                    top = Mathf.Max(top, field.TopAt(c));
                groups.Add(new RockGroup
                {
                    Index = groups.Count, Cells = cells, Top = top,
                    Look = styles?.For(groups.Count) ?? RockLook.Crag,
                });
            }
        return groups;
    }

    /// <summary>The ground a rock cell stands on: its lowest dry neighbour that
    /// is no rock, lift px - the level the rock rises from.</summary>
    public static float Under(HexField field, Vector2I c)
    {
        float under = float.MaxValue;
        for (int h = 30; h < 360; h += 60)
        {
            Vector2I o = HexField.Step(c, h);
            if (!field.InBounds(o) || field.FoundationAt(o) == Foundation.Rock)
                continue;
            under = Mathf.Min(under, field.IsWater(o) ? field.WaterTop(o) : field.TopAt(o));
        }
        return under < float.MaxValue ? under : field.TopAt(c) - 2.0f * field.Lift;
    }

    /// <summary>Marks every raster point with the rock it belongs to - inside
    /// its cells or within <see cref="Reach"/> of them - and -1 elsewhere.</summary>
    public static void Mark(Relief f, List<RockGroup> groups, float r)
    {
        Array.Fill(f.Rock, -1);
        int n = f.Nx * f.Ny;
        float reach = Reach * r / f.Cell;
        var best = new float[n];
        Array.Fill(best, float.MaxValue);
        var mask = new bool[n];
        foreach (RockGroup g in groups)
        {
            var cells = new HashSet<Vector2I>(g.Cells);
            for (int p = 0; p < n; p++)
                mask[p] = cells.Contains(f.Hex[p]);
            float[] d = Relief.Distance(mask, f.Nx, f.Ny);
            for (int p = 0; p < n; p++)
                if (d[p] <= reach && d[p] < best[p])
                {
                    best[p] = d[p];
                    f.Rock[p] = g.Index;
                }
        }
    }

    /// <summary>
    /// Lays every rock that is not a crag over the relief: its form into
    /// <see cref="Relief.Height"/>, its share into <see cref="Relief.Cliff"/>
    /// (what the shader, the grass and the brow read as rock), and its own mask
    /// into <see cref="Relief.RockAux"/> - the scree or sand at a mesa's and a
    /// yardang's foot, the lee of a moutonnee.
    /// </summary>
    /// <param name="cliff0">The rock's tight share before the crag's tear.</param>
    /// <param name="flat">The ground with no rock on it: a rock cell at the
    /// level it rises from (<see cref="Under"/>), blurred as the terraces.</param>
    public static void Lay(Relief f, HexField field, List<RockGroup> groups, RockStyles styles,
                           float[] cliff0, float[] flat, float lift, float r, int seed)
    {
        int n = f.Nx * f.Ny;
        float hr = lift / LevelR;
        // the rock's share blurred wide: where a mesa's apron and its ledge's
        // second wall lie, how deep into the rock a dome's back is
        var soft = new float[n];
        for (int p = 0; p < n; p++)
            soft[p] = f.Hex[p].X >= 0 && field.FoundationAt(f.Hex[p]) == Foundation.Rock ? 1.0f : 0.0f;
        var wide = (float[])soft.Clone();
        Relief.Blur(soft, f.Nx, f.Ny, 0.32f * r / f.Cell);
        Relief.Blur(wide, f.Nx, f.Ny, 0.35f * r / f.Cell);
        foreach (RockGroup g in groups)
        {
            int s = seed + 7919 * (g.Cells[0].X + 131 * g.Cells[0].Y);
            switch (g.Look.Type)
            {
                case RockType.Mesa: Mesa(f, g, cliff0, flat, soft, lift, hr, r, s); break;
                case RockType.Yardang: Yardang(f, g, cliff0, flat, styles.Wind, lift, hr, r, s); break;
                case RockType.Moutonnee: Moutonnee(f, g, cliff0, flat, wide, styles.Ice, lift, hr, r, s); break;
            }
        }
    }

    private static float Ss(float a, float b, float x)
    {
        float t = Mathf.Clamp((x - a) / (b - a), 0.0f, 1.0f);
        return t * t * (3.0f - 2.0f * t);
    }

    /// <summary>What brings FastNoiseLite's Perlin to the renders' -1..1.</summary>
    private const float Rescale = 1.41f;

    /// <summary>Perlin of period one, read at coordinates in circumradii - one
    /// octave, as the renders that chose the forms: Godot's default is five of
    /// fractal, and they turned the yardangs' smooth ridges into spikes.</summary>
    private static FastNoiseLite Perlin(int seed) => new()
    {
        Seed = seed, NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin, Frequency = 1.0f,
        FractalType = FastNoiseLite.FractalTypeEnum.None,
    };

    /// <summary>The highest a rock may stand: the crag's own ceiling.</summary>
    private static float Ceiling(RockGroup g, float lift) => g.Top + Relief.Crag * lift;

    /// <summary>
    /// The mesa (Monument Valley): a flat cap at the rock's top, a sheer wall
    /// a little inside its cells, bays torn into it, scree between the wall and
    /// the cells' side; on a rock of three cells or more the preset may step it,
    /// a second wall standing back from the first at part of the height.
    /// </summary>
    private static void Mesa(Relief f, RockGroup g, float[] cliff0, float[] flat, float[] soft,
                             float lift, float hr, float r, int seed)
    {
        MesaShape m = Mesas[g.Look.Preset - 1];
        bool bench = m.Bench > 0.0f && g.Cells.Count >= RockGroup.BenchCells;
        FastNoiseLite bay = Perlin(seed), nick = Perlin(seed + 1), lid = Perlin(seed + 2),
                      grain = Perlin(seed + 3), step = Perlin(seed + 4);
        float ceiling = Ceiling(g, lift);
        for (int p = 0; p < f.Rock.Length; p++)
        {
            if (f.Rock[p] != g.Index)
                continue;
            float x = (f.X0 + p % f.Nx * f.Cell) / r, z = (f.Z0 + p / f.Nx * f.Cell) / r;
            float c0 = cliff0[p];
            float cm = c0 > 0.99f ? c0
                : Mathf.Clamp(c0 - m.Tear * Mathf.Abs(bay.GetNoise2D(x / m.Scale, z / m.Scale))
                              - 0.2f * Mathf.Abs(nick.GetNoise2D(x / 0.3f, z / 0.3f)), 0.0f, 1.0f);
            float ground = flat[p];
            float top = Mathf.Max(ground, g.Top) + (m.Cap + 0.015f * lid.GetNoise2D(x / 0.4f, z / 0.4f)) * hr;
            float n = 0.85f + 0.3f * (0.5f + 0.5f * grain.GetNoise2D(x / 0.25f, z / 0.25f));
            float talus = Mathf.Max(0.0f, top - ground) * m.Talus * Ss(0.5f, 0.7f, c0) * n;
            float baseH = ground + talus;
            float wall = Ss(0.64f, 0.72f, cm);
            float h;
            if (bench)
            {
                // the upper wall stands back from the lower; the noise bends it,
                // but not so far that a narrow neck of the cap drops to the ledge
                float inner = soft[p] - 0.12f * Mathf.Abs(step.GetNoise2D(x / 0.7f, z / 0.7f));
                float upper = Ss(0.79f, 0.85f, inner);
                float ledge = baseH + (top - baseH) * m.Bench;
                h = baseH + (Mathf.Max(baseH, ledge) - baseH) * wall;
                h += (Mathf.Max(h, top) - h) * upper;
            }
            else
                h = baseH + (Mathf.Max(baseH, top) - baseH) * wall;
            f.Height[p] = Mathf.Min(h, ceiling);
            // the shader's rock past a half: the wall's own foot
            f.Cliff[p] = Mathf.Clamp(cm - 0.14f, 0.0f, 1.0f);
            f.RockAux[p] = Ss(0.02f, 0.06f, talus / hr) * (1.0f - wall);
        }
    }

    /// <summary>
    /// The yardangs: ridges stretched along the board's wind, smoothed so the
    /// wind's work reads and not the noise's, a wall where a ridge stands high
    /// enough, sand drifted at the foot.
    /// </summary>
    private static void Yardang(Relief f, RockGroup g, float[] cliff0, float[] flat, float wind,
                                float lift, float hr, float r, int seed)
    {
        YardangShape y = Yardangs[g.Look.Preset - 1];
        float th = Mathf.DegToRad(wind), cs = Mathf.Cos(th), sn = Mathf.Sin(th);
        FastNoiseLite ridge = Perlin(seed), fine = Perlin(seed + 1), bay = Perlin(seed + 2), drift = Perlin(seed + 3);
        int n = f.Rock.Length;
        var shape = new float[n];
        for (int p = 0; p < n; p++)
        {
            if (f.Rock[p] != g.Index)
                continue;
            float x = (f.X0 + p % f.Nx * f.Cell) / r, z = (f.Z0 + p / f.Nx * f.Cell) / r;
            float u = x * cs + z * sn, v = -x * sn + z * cs;
            // FastNoiseLite's Perlin runs about -0.7..0.7, the renders' -1..1:
            // brought to theirs, or the troughs never reach the ground
            float a = Mathf.Clamp(1.0f - Rescale * Mathf.Abs(ridge.GetNoise2D(u / y.Length, v / y.Gap)), 0.0f, 1.0f);
            float b = Mathf.Clamp(1.0f - Rescale * Mathf.Abs(fine.GetNoise2D(u / (y.Length * 0.5f), v / (y.Gap * 0.5f))),
                                  0.0f, 1.0f);
            shape[p] = Mathf.Clamp(0.8f * Mathf.Pow(a, 2.2f) + 0.2f * b * b, 0.0f, 1.0f);
        }
        // a raster point and a half: under one the blur is no blur at all
        Relief.Blur(shape, f.Nx, f.Ny, 0.08f * r / f.Cell);
        float ceiling = Ceiling(g, lift);
        for (int p = 0; p < n; p++)
        {
            if (f.Rock[p] != g.Index)
                continue;
            float x = (f.X0 + p % f.Nx * f.Cell) / r, z = (f.Z0 + p / f.Nx * f.Cell) / r;
            float u = x * cs + z * sn, v = -x * sn + z * cs;
            float c0 = cliff0[p];
            float cm = c0 > 0.99f ? c0
                : Mathf.Clamp(c0 - 0.5f * Mathf.Abs(bay.GetNoise2D(u / (y.Length * 0.9f), v / y.Gap)), 0.0f, 1.0f);
            float ground = flat[p];
            // the troughs well down and the wall gone in them: the renders' 0.35
            // of the height left a plateau with the ridges only waving its top
            float top = ground + Mathf.Max(0.0f, g.Top - ground) * (0.15f + 1.1f * y.Tall * shape[p]);
            float wall = Ss(0.6f, 0.76f, cm) * Ss(0.3f, 0.5f, shape[p] + 0.1f);
            float sand = Ss(0.5f, 0.7f, c0) * (0.5f + 0.5f * drift.GetNoise2D(u / 0.8f, v / 0.3f)) * 0.12f * hr;
            float baseH = ground + sand;
            f.Height[p] = Mathf.Min(baseH + (Mathf.Max(baseH, top) - baseH) * wall, ceiling);
            f.Cliff[p] = 0.4f * Ss(0.5f, 0.62f, c0) + 0.3f * wall;
            f.RockAux[p] = Ss(0.01f, 0.05f, sand / hr) * (1.0f - Ss(0.3f, 0.5f, wall));
        }
    }

    /// <summary>
    /// The roche moutonnee (Karelia): the ice ran over it - its back rises
    /// smoothly from the edge the ice met to the edge it left, and there it is
    /// plucked into a ragged step. The rise is the point's place along the flow
    /// within its own rock, so a lobe past a notch never sags into a bowl (the
    /// renders' first try did).
    /// </summary>
    private static void Moutonnee(Relief f, RockGroup g, float[] cliff0, float[] flat, float[] wide,
                                  float ice, float lift, float hr, float r, int seed)
    {
        MoutonneeShape m = Moutonnees[g.Look.Preset - 1];
        float th = Mathf.DegToRad(ice), du = Mathf.Cos(th), dv = Mathf.Sin(th);
        int n = f.Rock.Length;
        float lo = float.MaxValue, hi = float.MinValue, most = 1e-3f;
        for (int p = 0; p < n; p++)
        {
            if (f.Rock[p] != g.Index || cliff0[p] <= 0.5f)
                continue;
            float u = (f.X0 + p % f.Nx * f.Cell) * du + (f.Z0 + p / f.Nx * f.Cell) * dv;
            lo = Mathf.Min(lo, u);
            hi = Mathf.Max(hi, u);
            most = Mathf.Max(most, wide[p]);
        }
        if (lo >= hi)
            return;
        var ramp = new float[n];
        for (int p = 0; p < n; p++)
            if (f.Rock[p] == g.Index)
                ramp[p] = Mathf.Clamp(((f.X0 + p % f.Nx * f.Cell) * du + (f.Z0 + p / f.Nx * f.Cell) * dv - lo)
                                      / (hi - lo), 0.0f, 1.0f);
        Relief.Blur(ramp, f.Nx, f.Ny, 0.1f * r / f.Cell);
        var blocks = new FastNoiseLite
        {
            Seed = seed, NoiseType = FastNoiseLite.NoiseTypeEnum.Cellular, Frequency = 1.0f,
            CellularReturnType = FastNoiseLite.CellularReturnTypeEnum.CellValue,
            FractalType = FastNoiseLite.FractalTypeEnum.None,
        };
        FastNoiseLite tear = Perlin(seed + 1);
        float ceiling = Ceiling(g, lift);
        for (int p = 0; p < n; p++)
        {
            if (f.Rock[p] != g.Index)
                continue;
            float wx = f.X0 + p % f.Nx * f.Cell, wz = f.Z0 + p / f.Nx * f.Cell;
            float x = wx / r, z = wz / r;
            float env = Mathf.Clamp(wide[p] / most, 0.0f, 1.0f);
            float lee = f.Sample(cliff0, wx + du * 0.12f * r, wz + dv * 0.12f * r);
            float cut = Ss(0.3f, 0.7f, lee);
            float block = 0.5f * m.Block * 0.5f * blocks.GetNoise2D(x / m.Block, z / m.Block) * (1.0f - cut);
            float ground = flat[p];
            float rise = Mathf.Max(0.3f + m.Tall * Mathf.Pow(Mathf.Clamp(ramp[p], 0.0f, 1.0f), 1.2f), 0.7f * m.Tall * env);
            float dome = Mathf.Max(0.0f, g.Top - ground) * rise * (0.75f + 0.25f * env) + block * hr;
            float c0 = cliff0[p];
            float cm = c0 > 0.99f ? c0
                : Mathf.Clamp(c0 - 0.3f * Mathf.Abs(tear.GetNoise2D(x / 0.5f, z / 0.5f)), 0.0f, 1.0f);
            float wall = Ss(0.5f, 0.6f, cm);
            float top = ground + Mathf.Max(0.0f, dome);
            f.Height[p] = Mathf.Min(ground + (top - ground) * wall, ceiling);
            f.Cliff[p] = cm;
            f.RockAux[p] = 1.0f - cut;
        }
    }
}
