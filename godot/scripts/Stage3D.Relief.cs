using System.Collections.Generic;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The board as one smooth ground (BOT-11, <c>--relief</c>): the mesh of
/// <see cref="Relief"/>'s raster in place of the prisms, its tops, walls and
/// rim bands, with the hex grid drawn by the ground's own shader.
///
/// <b>The hexes are a line on the ground, not its shape.</b> MA_ContentMaker's
/// <c>look/hex_lines.gdshaderinc</c>, ported to this board's frame: per
/// fragment, the plan distance to the nearest side of the hex it lies in,
/// a line of steady width wherever the ground is, gone on steep banks, where
/// a side's line in plan would smear down the whole of the slope. A ribbon of
/// geometry over the ground was that project's first try and hung off its
/// gorges' walls like a shelf.
///
/// <b>The soil shader with its paint swapped, not a second shader.</b> The
/// sun's march, the mud and the ash stay word for word (<see cref="SoilShader"/>);
/// what changes is where the colour comes from: the art tiled over the world
/// instead of one plate per hex, toned by the kinds' shares the mesh carries,
/// and stepped by the slope's turn to the sun - the walls' <see cref="CelSide"/>,
/// which a smooth ground has no walls to wear.
/// </summary>
public sealed partial class Stage3D
{
    /// <summary>The board's smooth fields; null for the prisms.</summary>
    public Relief? Relief;

    /// <summary>Which fields the mesh was last built from: past that, a
    /// rebuild only repaints the cells (<see cref="Build"/>).</summary>
    private Relief? _reliefBuilt;

    /// <summary>The grid's ink: the rim bands' own (<see cref="EdgeInk"/>), a
    /// little heavier, because a line has none of a band's width to carry it.</summary>
    private static readonly Color GridInk = new(0.05f, 0.04f, 0.03f, 0.42f);

    /// <summary>How much of the art one tile spans across the ground, in
    /// circumradii: the plate's inner square repeats, mirrored, every this far.</summary>
    private const float ArtTile = 1.6f;

    private void BuildRelief(Relief f, Texture2D? art)
    {
        int nx = f.Nx, ny = f.Ny, n = nx * ny;
        float rise = RiseFactor;
        var at = new Vector3[n];
        var normal = new Vector3[n];
        var paint = new Color[n];
        // the levels near each point, for the brow (Relief.Levels): a float
        // holds the bits exactly, and the shader reads them flat
        var levels = new Vector2[n];
        var index = new int[(nx - 1) * (ny - 1) * 6];
        float[] sand = f.Share[(int)Relief.Kind.Sand],
                forest = f.Share[(int)Relief.Kind.Forest];
        for (int j = 0; j < ny; j++)
            for (int i = 0; i < nx; i++)
            {
                int p = j * nx + i;
                float x = f.X0 + i * f.Cell + Origin.X, z = f.Z0 + j * f.Cell + Origin.Y / Squash;
                at[p] = new Vector3(x, f.Height[p] / rise, z);
                float hx = (f.Height[j * nx + Mathf.Min(i + 1, nx - 1)] - f.Height[j * nx + Mathf.Max(i - 1, 0)])
                           / (2.0f * f.Cell * rise);
                float hz = (f.Height[Mathf.Min(j + 1, ny - 1) * nx + i] - f.Height[Mathf.Max(j - 1, 0) * nx + i])
                           / (2.0f * f.Cell * rise);
                normal[p] = new Vector3(-hx, 1.0f, -hz).Normalized();
                // what the ground is, for the shader: sand, rock, wood, and how
                // far under the water it lies (the bed darker than the bank)
                float under = Mathf.Clamp(-f.Shore[p] / (f.Cell * 4.0f), 0.0f, 1.0f);
                paint[p] = new Color(sand[p], f.Cliff[p], forest[p], under);
                levels[p] = new Vector2(f.Levels[p] & 0xFFFFFF, 0.0f);
            }
        int t = 0;
        for (int j = 0; j + 1 < ny; j++)
            for (int i = 0; i + 1 < nx; i++)
            {
                int a = j * nx + i, b = a + 1, c = a + nx, d = c + 1;
                index[t++] = a; index[t++] = b; index[t++] = d;
                index[t++] = a; index[t++] = d; index[t++] = c;
            }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = at;
        arrays[(int)Mesh.ArrayType.Normal] = normal;
        arrays[(int)Mesh.ArrayType.Color] = paint;
        arrays[(int)Mesh.ArrayType.TexUV2] = levels;
        arrays[(int)Mesh.ArrayType.Index] = index;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        _tops.Mesh = mesh;
        _sides.Mesh = null;
        _edges.Mesh = null;
        BakeRelief(f);
        _tops.MaterialOverride = ReliefTurf(f, art);
        _glaze.MaterialOverride = null;
    }

    /// <summary>The sun's height map off the raster itself: one texel a point,
    /// the march's bilinear fetch landing on the points the mesh was built on.</summary>
    private void BakeRelief(Relief f)
    {
        _heights = null;
        _map = null;
        _mapReach = 0.0f;
        float lo = float.MaxValue, hi = float.MinValue;
        foreach (float h in f.Height)
        {
            lo = Mathf.Min(lo, h);
            hi = Mathf.Max(hi, h);
        }
        float rise = RiseFactor;
        float loY = lo / rise, relief = (hi - lo) / rise;
        if (relief < 0.001f)
            return;
        var map = new byte[f.Nx * f.Ny];
        for (int p = 0; p < map.Length; p++)
            map[p] = (byte)Mathf.RoundToInt(Mathf.Clamp((f.Height[p] / rise - loY) / relief, 0.0f, 1.0f) * 255.0f);
        _heights = ImageTexture.CreateFromImage(Image.CreateFromData(f.Nx, f.Ny, false, Image.Format.R8, map));
        _map = map;
        _mapWide = f.Nx;
        _mapTall = f.Ny;
        // texel centres on the points: the texture's edge half a step outside
        float x0 = f.X0 + Origin.X - f.Cell * 0.5f, z0 = f.Z0 + Origin.Y / Squash - f.Cell * 0.5f;
        _mapAt = new Vector4(x0, z0, 1.0f / (f.Nx * f.Cell), 1.0f / (f.Ny * f.Cell));
        _mapSpan = new Vector2(loY, relief);
        _mapReach = relief * SunCast.Length();
    }

    private ShaderMaterial ReliefTurf(Relief f, Texture2D? art)
    {
        ShaderMaterial ink = Turf(art, soil: ReliefSoil);
        float r = Field.Atlas!.HexRect.Size.X * 0.5f;
        ink.Param("hex_r", r);
        // hex (0, 0)'s centre in world XZ: its flat anchor is the origin
        Vector2 hub = Origin + Field.CentreOffset;
        ink.Param("hex_off", new Vector2(hub.X, hub.Y / Squash));
        ink.Param("hex_on", ShowEdges ? 1.0f : 0.0f);
        ink.Param("hex_color", new Color(GridInk.R, GridInk.G, GridInk.B));
        ink.Param("hex_opacity", GridInk.A);
        ink.Param("cells", CellsTexture());
        ink.Param("cells_size", new Vector2I(Field.Columns, Field.Rows));
        ink.Param("sun_dir", Sun.Normalized());
        // the brow line's levels, in world Y
        ink.Param("level_world", Field.Lift / RiseFactor);
        ink.Param("level_base", f.LevelBase);
        ink.Param("art_tile", 1.0f / (r * ArtTile));
        if (Field.Terrain is { Any: true } terrain && terrain.Plate.Size.X > 0)
        {
            // the plate's inscribed square, short of its antialiased rim: the
            // part of a hexagon of art that tiles
            Vector2 frame = (Vector2)terrain.Frame;
            Vector2 middle = (Vector2)terrain.Plate.Position + (Vector2)terrain.Plate.Size * 0.5f;
            float half = Mathf.Min(terrain.Plate.Size.X, terrain.Plate.Size.Y) * 0.5f * 0.62f;
            ink.Param("art_rect", new Vector4((middle.X - half) / frame.X, (middle.Y - half) / frame.Y,
                                              2.0f * half / frame.X, 2.0f * half / frame.Y));
        }
        return ink;
    }

    /// <summary>Each cell's ink and whether it is board, as one texel a cell
    /// (col, row): the grid stops at the plot's edge, and a route or an aim
    /// marks its cells (<see cref="HexField.InkFor"/>) on the ground as it
    /// did on the tops.</summary>
    private ImageTexture CellsTexture()
    {
        var img = Image.CreateEmpty(Field.Columns, Field.Rows, false, Image.Format.Rgba8);
        for (int q = 0; q < Field.Columns; q++)
            for (int r = 0; r < Field.Rows; r++)
            {
                var cell = new Vector2I(q, r);
                bool on = Field.InBounds(cell);
                Color ink = on ? Field.InkFor(cell) : Colors.White;
                img.SetPixel(q, r, new Color(ink.R, ink.G, ink.B, on ? 1.0f : 0.0f));
            }
        return ImageTexture.CreateFromImage(img);
    }

    /// <summary>The ground's triangles in world units for what stands on it -
    /// the raster's quads, at every other point: the shadow's skin and the
    /// rubble need the ground's shape, not its grain.</summary>
    private List<Vector3> ReliefTriangles(Relief f)
    {
        var outp = new List<Vector3>();
        const int Skip = 2;
        float rise = RiseFactor;
        Vector3 Point(int i, int j) => new(f.X0 + i * f.Cell + Origin.X,
                                           f.Height[j * f.Nx + i] / rise,
                                           f.Z0 + j * f.Cell + Origin.Y / Squash);
        for (int j = 0; j + Skip < f.Ny; j += Skip)
            for (int i = 0; i + Skip < f.Nx; i += Skip)
            {
                Vector3 a = Point(i, j), b = Point(i + Skip, j), c = Point(i, j + Skip), d = Point(i + Skip, j + Skip);
                outp.Add(a); outp.Add(b); outp.Add(d);
                outp.Add(a); outp.Add(d); outp.Add(c);
            }
        return outp;
    }

    /// <summary>
    /// The water on the relief: a sheet at each body's surface over every
    /// raster point the water reaches or nearly reaches, and no flanks.
    ///
    /// <b>The shore is where the ground comes up through the sheet, and the
    /// pond's shader draws it already.</b> <see cref="DeepShader"/> takes its
    /// thickness off the depth buffer and discards where the ground stands over
    /// the surface - so a sheet cut a little past the waterline is clipped to
    /// the real one, round, wherever the blurred bank crosses it. Its foam
    /// along a thin film (<c>lip</c>) is that shore's own; the foam along a
    /// hex's dry sides (<c>rim_mask</c>) is a honeycomb, and is left off.
    /// The flanks were the prisms' cut-away pond walls, and a smooth bank has
    /// none.
    /// </summary>
    private void BuildReliefWater(Relief f, Vector3[] corner, float halfX, float halfZ)
    {
        _flank.Mesh = null;
        var hub = new Vector2[MaxWaterCells];
        var mask = new float[MaxWaterCells];
        var sink = new float[MaxWaterCells];
        var low = new Vector2(float.MaxValue, float.MaxValue);
        var high = new Vector2(float.MinValue, float.MinValue);
        long wetStamp = 1469598103934665603L;
        for (int q = 0; q < Field.Columns; q++)
            for (int r = 0; r < Field.Rows; r++)
            {
                var cell = new Vector2I(q, r);
                if (!Field.IsWater(cell))
                    continue;
                int ord = Mathf.Min(Field.WaterIndex(cell), MaxWaterCells - 1);
                Vector2 flat = Origin + Field.FlatAnchor(cell) + Field.CentreOffset;
                Vector3 top = World(flat, Field.WaterTop(cell));
                hub[ord] = new Vector2(top.X, top.Z);
                sink[ord] = Field.IsDeep(cell) ? 1.0f : 0.0f;
                wetStamp = (wetStamp ^ (q * 1013 + r)) * 1099511628211L;
                // the field of waves reaches as far as the sheet may
                low = new Vector2(Mathf.Min(low.X, top.X - 2 * halfX), Mathf.Min(low.Y, top.Z - 2 * halfZ));
                high = new Vector2(Mathf.Max(high.X, top.X + 2 * halfX), Mathf.Max(high.Y, top.Z + 2 * halfZ));
            }

        int nx = f.Nx;
        float rise = RiseFactor, reach = f.Cell * 3.0f;
        // which body (its first cell's slot) each point's water belongs to
        var ord_ = new int[f.Height.Length];
        for (int p = 0; p < ord_.Length; p++)
            ord_[p] = float.IsNaN(f.Surface[p]) ? -1 : WaterSlot(f, p);
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        int quads = 0;
        for (int j = 0; j + 1 < f.Ny; j++)
            for (int i = 0; i + 1 < nx; i++)
            {
                int a = j * nx + i, b = a + 1, c = a + nx, d = c + 1;
                if (ord_[a] < 0 || ord_[b] < 0 || ord_[c] < 0 || ord_[d] < 0)
                    continue;
                // a quad at all near the water: past the waterline by a little,
                // which the shader clips back to it
                if (f.Shore[a] > reach && f.Shore[b] > reach && f.Shore[c] > reach && f.Shore[d] > reach)
                    continue;
                float level = Mathf.Max(Mathf.Max(f.Surface[a], f.Surface[b]), Mathf.Max(f.Surface[c], f.Surface[d]));
                int slot = ord_[a];
                foreach (int p in new[] { a, b, d, a, d, c })
                {
                    int pi = p % nx, pj = p / nx;
                    var at = new Vector3(f.X0 + pi * f.Cell + Origin.X, level / rise, f.Z0 + pj * f.Cell + Origin.Y / Squash);
                    st.SetColor(Pond);
                    st.SetUV(new Vector2(at.X / (2.0f * halfX), at.Z / (2.0f * halfZ)));
                    st.SetUV2(new Vector2(slot, 0.0f));
                    st.AddVertex(at);
                }
                quads++;
            }
        if (quads == 0)
        {
            _pond.Mesh = null;
            Wash?.Drain();
            return;
        }
        st.GenerateNormals();
        _pond.Mesh = st.Commit();
        Wash?.Fit(new Rect2(low, high - low), Squash, wetStamp,
                  p => !float.IsNaN(f.Sample(f.Surface, p.X - Origin.X, p.Y - Origin.Y / Squash))
                       && f.Sample(f.Shore, p.X - Origin.X, p.Y - Origin.Y / Squash) < 0.0f);
        if (_deepInk is not null)
        {
            (Vector2[] normal, float[] stand, float inradius) = Rim(corner);
            _deepInk.Param("hub", hub);
            _deepInk.Param("rim_mask", mask);
            _deepInk.Param("sink", sink);
            _deepInk.Param("rim_n", normal);
            _deepInk.Param("rim_d", stand);
            _deepInk.Param("rim_span", inradius);
            _deepInk.Param("squash", Squash);
            _deepInk.Param("rise", RiseFactor);
        }
    }

    /// <summary>The water slot a raster point's water belongs to: its own
    /// hex's if that is water, else the neighbour whose surface it took.</summary>
    private int WaterSlot(Relief f, int p)
    {
        Vector2I c = f.Owner[p];
        if (c.X < 0)
            return -1;
        if (Field.IsWater(c))
            return Mathf.Min(Field.WaterIndex(c), MaxWaterCells - 1);
        for (int h = 30; h < 360; h += 60)
        {
            Vector2I o = HexField.Step(c, h);
            if (Field.InBounds(o) && Field.IsWater(o) && Mathf.IsEqualApprox(Field.WaterTop(o), f.Surface[p]))
                return Mathf.Min(Field.WaterIndex(o), MaxWaterCells - 1);
        }
        return -1;
    }

    private static readonly Shader ReliefSoil = new()
    {
        Code = SoilShader
            .Replace("varying vec3 ground;", ReliefHead)
            .Replace("    ground = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;",
                     "    ground = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;\n"
                     + "    ground_n = normalize((MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz);\n"
                     + "    ground_levels = int(UV2.x + 0.5);")
            .Replace("    vec4 paint = texture(art, UV) * COLOR;", ReliefPaint)
            .Replace("    ALBEDO = lit;", ReliefGrid),
    };

    private const string ReliefHead = @"varying vec3 ground;
varying vec3 ground_n;
varying flat int ground_levels;
// The art tiled over the world: its plate's inscribed square (uv rect), one
// tile per this many world units, mirrored so a seam is a reflection.
uniform vec4 art_rect = vec4(0.0, 0.0, 1.0, 1.0);
uniform float art_tile = 0.01;
// Cel steps by the slope's turn to the sun.
uniform vec3 sun_dir = vec3(0.0, 1.0, 0.0);
uniform vec3 sand_ink : source_color = vec3(0.86, 0.76, 0.55);
uniform vec3 rock_ink : source_color = vec3(0.58, 0.56, 0.54);
uniform vec3 wood_ink : source_color = vec3(0.30, 0.38, 0.20);
uniform vec3 bed_ink : source_color = vec3(0.30, 0.27, 0.20);
// Grass3D's own tones, mid and tip
uniform vec3 grass_ink : source_color = vec3(0.34, 0.43, 0.17);
uniform vec3 grass_tip : source_color = vec3(0.55, 0.61, 0.29);
// The hex grid (MA_ContentMaker's hex_lines.gdshaderinc, in this board's
// frame): flat-top, odd columns shifted down half a row, circumradius hex_r,
// hex (0, 0)'s centre at hex_off in world XZ; rows run with +Z.
uniform float hex_on = 1.0;
uniform float hex_r = 124.0;
uniform vec2 hex_off = vec2(0.0);
uniform vec3 hex_color : source_color = vec3(0.05, 0.04, 0.03);
uniform float hex_opacity = 0.42;
uniform float hex_w = 1.2;
uniform float hex_px = 1.0;
// The brow line: one level in world units, how far under a level's top the
// line runs (in levels), its width in pixels, ink and weight.
uniform float level_world = 54.0;
uniform float brow_below = 0.15;
uniform float brow_px = 1.3;
uniform vec3 brow_ink : source_color = vec3(0.10, 0.08, 0.06);
uniform float brow_opacity = 0.85;
uniform float brow_on = 1.0;
// Relief.LevelBase: bit 0 of a point's level mask (UV2.x, read flat)
uniform int level_base = 0;
// A texel a cell: its ink (a route, an aim) and whether it is board.
uniform sampler2D cells : filter_nearest, hint_default_white;
uniform ivec2 cells_size = ivec2(1);

vec2 tri(vec2 x) { return abs(fract(x * 0.5) * 2.0 - 1.0); }

// The hex a world point lies in, as (col, row), and the plan distance to its
// nearest side.
vec3 hex_at(vec2 xz) {
    vec2 p = xz - hex_off;
    float q = (2.0 / 3.0) * p.x / hex_r;
    float r = (-p.x / 3.0 + 0.5773503 * p.y) / hex_r;
    float s = -q - r;
    float rq = round(q), rr = round(r), rs = round(s);
    float dq = abs(rq - q), dr = abs(rr - r), ds = abs(rs - s);
    if (dq > dr && dq > ds) {
        rq = -rr - rs;
    } else if (dr > ds) {
        rr = -rq - rs;
    }
    vec2 v = p - vec2(1.5 * hex_r * rq, 1.7320508 * hex_r * (rr + rq * 0.5));
    float m = max(abs(v.y), max(abs(0.8660254 * v.x + 0.5 * v.y), abs(0.8660254 * v.x - 0.5 * v.y)));
    int col = int(rq);
    int row = int(rr) + (col - (col & 1)) / 2;
    return vec3(float(col), float(row), hex_r * 0.8660254 - m);
}

vec4 cell_ink(vec2 cr) {
    ivec2 c = ivec2(cr);
    if (c.x < 0 || c.y < 0 || c.x >= cells_size.x || c.y >= cells_size.y)
        return vec4(1.0, 1.0, 1.0, 0.0);
    return texelFetch(cells, c, 0);
}";

    private const string ReliefPaint = @"    vec2 tuv = art_rect.xy + tri(ground.xz * art_tile) * art_rect.zw;
    vec3 soil = texture(art, tuv).rgb;
    float grain = dot(soil, vec3(0.299, 0.587, 0.114));
    // the kinds, each its own colour carrying the art's grain
    soil = mix(soil, sand_ink * (0.6 + 0.6 * grain), smoothstep(0.25, 0.6, COLOR.r));
    // rock: the tight share (Relief.Cliff), cut where the wall stands
    soil = mix(soil, rock_ink * (0.55 + 0.7 * grain), smoothstep(0.38, 0.5, COLOR.g));
    soil = mix(soil, wood_ink * (0.6 + 0.6 * grain), 0.6 * smoothstep(0.3, 0.7, COLOR.b));
    // grass over the rest, in patches, the earth through it on steep banks
    // (the cells' painted lawn, Grass3D, is a hexagon a cell - off on the relief)
    float bare = max(smoothstep(0.25, 0.6, COLOR.r), smoothstep(0.25, 0.6, COLOR.g));
    float bank = length(normalize(ground_n).xz);
    float patch_ = mire_fbm(ground.xz * 0.018 + vec2(7.0, 3.0));
    float lawn = (1.0 - bare) * (1.0 - COLOR.a)
                 * smoothstep(0.30, 0.42, patch_ + 0.25 - bank * 0.9);
    vec3 turf = mix(grass_ink, grass_tip, smoothstep(0.35, 0.75, mire_fbm(ground.xz * 0.05)));
    soil = mix(soil, turf * (0.75 + 0.5 * grain), lawn);
    soil = mix(soil, bed_ink * (0.6 + 0.6 * grain), COLOR.a);
    vec3 hexc = hex_at(ground.xz);
    vec4 cink = cell_ink(hexc.xy);
    // cel steps by the turn to the sun: three, as the prisms' walls had
    float d = dot(normalize(ground_n), sun_dir);
    float flat_d = sun_dir.y;
    float step_ = d > flat_d - 0.08 ? 1.0 : d > flat_d - 0.3 ? 0.86 : 0.72;
    // a wall - a rock's face - one step darker still, so a cliff reads as
    // one and not as the far side of a hill
    if (length(normalize(ground_n).xz) > 0.8)
        step_ = min(step_, 0.56);
    vec4 paint = vec4(soil * step_ * mix(vec3(1.0), cink.rgb, cink.a), 1.0);";

    private const string ReliefGrid = @"    {
        float hl = length(normalize(ground_n).xz);
        float px = length(fwidth(ground.xz));
        float lw = max(hex_w, hex_px * px);
        float aa = max(fwidth(hexc.z), 1e-4);
        float line = (1.0 - smoothstep(lw, lw + aa, hexc.z)) * hex_on * cink.a
                     * (1.0 - smoothstep(0.45, 0.7, hl))
                     // none on the rock: it is no cell anything stands on
                     * (1.0 - smoothstep(0.4, 0.55, COLOR.g));
        lit = mix(lit, hex_color, line * hex_opacity);
        // The brow: an ink line just under every level's top, where the
        // ground crosses it - the upper rim of each terrace's slope. Off the
        // water and the rock (its crags would draw squiggles).
        float t = ground.y / max(level_world, 1e-3) + brow_below;
        float off = abs(fract(t + 0.5) - 0.5);
        float bw = brow_px * fwidth(t);
        // only a level whose ground is near: the rim of a terrace, not the
        // middle of a slope two levels tall (Relief.StampLevels)
        int bit = int(round(t)) - level_base;
        float near = bit >= 0 && bit < 24 ? float((ground_levels >> bit) & 1) : 0.0;
        // and off the rock to the foot of its wall (the wall stands where
        // Relief.Cliff crosses 0.3-0.55), and off anything as steep as a
        // wall: a neighbour's brow ran on across the rock's face
        float brow = (1.0 - smoothstep(bw, bw + fwidth(t), off)) * brow_on * near
                     * (1.0 - COLOR.a) * (1.0 - smoothstep(0.1, 0.22, COLOR.g))
                     * (1.0 - smoothstep(0.82, 0.9, length(normalize(ground_n).xz)));
        lit = mix(lit, brow_ink, brow * brow_opacity);
    }
    ALBEDO = lit;";
}
