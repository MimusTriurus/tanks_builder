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
        var index = new int[(nx - 1) * (ny - 1) * 6];
        float[] sand = f.Share[(int)Relief.Kind.Sand], rock = f.Share[(int)Relief.Kind.Rock],
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
                paint[p] = new Color(sand[p], rock[p], forest[p], under);
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

    private static readonly Shader ReliefSoil = new()
    {
        Code = SoilShader
            .Replace("varying vec3 ground;", ReliefHead)
            .Replace("    ground = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;",
                     "    ground = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;\n"
                     + "    ground_n = normalize((MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz);")
            .Replace("    vec4 paint = texture(art, UV) * COLOR;", ReliefPaint)
            .Replace("    ALBEDO = lit;", ReliefGrid),
    };

    private const string ReliefHead = @"varying vec3 ground;
varying vec3 ground_n;
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
    soil = mix(soil, rock_ink * (0.55 + 0.7 * grain), smoothstep(0.25, 0.6, COLOR.g));
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
    vec4 paint = vec4(soil * step_ * mix(vec3(1.0), cink.rgb, cink.a), 1.0);";

    private const string ReliefGrid = @"    {
        float hl = length(normalize(ground_n).xz);
        float px = length(fwidth(ground.xz));
        float lw = max(hex_w, hex_px * px);
        float aa = max(fwidth(hexc.z), 1e-4);
        float line = (1.0 - smoothstep(lw, lw + aa, hexc.z)) * hex_on * cink.a
                     * (1.0 - smoothstep(0.45, 0.7, hl));
        lit = mix(lit, hex_color, line * hex_opacity);
    }
    ALBEDO = lit;";
}
