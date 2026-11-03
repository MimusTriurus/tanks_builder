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

    /// <summary>The brow line at each terrace's rim. Off by default (the
    /// user's call, 2026-10-25): the terraces' bare banks say the levels, and
    /// the line is there to switch on (<c>--brow</c>, the panel).</summary>
    public bool ShowBrow
    {
        get => _showBrow;
        set
        {
            _showBrow = value;
            if (Relief is not null && _tops?.MaterialOverride is ShaderMaterial tops)
                tops.Param("brow_on", value ? 1.0f : 0.0f);
        }
    }

    private bool _showBrow;

    /// <summary>How far under a level's top the brow runs, in levels. At the
    /// bench's 0.15 a pit's near bank, which faces away from the camera, hides
    /// its line behind the rim; at 0.04 the line closes round the whole rim,
    /// and under that the ground's own noise speckles the flats (measured on
    /// a pit, 2026-10-26). The editor's is 0.04.</summary>
    public float BrowBelow { get; set; } = 0.15f;

    /// <summary>The brow's half width, screen px (the bench's 1.3).</summary>
    public float BrowPx { get; set; } = 1.3f;

    /// <summary>The lawn photograph on the flats instead of the painted
    /// patches (<see cref="AssetRoot.Ground"/>, <c>grass_basecolor.jpg</c>).
    /// Off by default (the user went back to the painted grass); <c>--lawn</c>
    /// and the panel switch it on when the file is there.</summary>
    public bool ShowLawn
    {
        get => _showLawn;
        set
        {
            _showLawn = value;
            if (Relief is not null && _tops?.MaterialOverride is ShaderMaterial tops)
                tops.Param("lawn_on", value && Lawn() is not null ? 1.0f : 0.0f);
        }
    }

    private bool _showLawn;

    /// <summary>Grass on the ground's own texture at all - the lawn
    /// photograph or the painted patches. On by default - the painted grass,
    /// which the user asked back after trying bare soil (2026-10-25). Off,
    /// the ground is the soil art the slopes wear, everywhere, and the tufts
    /// stand on it as before.</summary>
    public bool ShowTurf
    {
        get => _showTurf;
        set
        {
            _showTurf = value;
            if (Relief is not null && _tops?.MaterialOverride is ShaderMaterial tops)
                tops.Param("turf_on", value ? 1.0f : 0.0f);
        }
    }

    private bool _showTurf = true;

    /// <summary>The pebble photograph on the bed of the water instead of the
    /// flat bed ink (<see cref="AssetRoot.Ground"/>,
    /// <c>river_small_rocks/river_small_rocks_diff_2k.jpg</c>, Poly Haven, CC0).
    /// Off by default, a trial of the new style (docs/new_style.md, the
    /// user's call, 2026-11-03); <c>--bed</c> and the panel switch it on when
    /// the file is there.</summary>
    public bool ShowBed
    {
        get => _showBed;
        set
        {
            _showBed = value;
            if (Relief is not null && _tops?.MaterialOverride is ShaderMaterial tops)
                tops.Param("bed_on", value && Pebbles() is not null ? 1.0f : 0.0f);
        }
    }

    private bool _showBed;

    /// <summary>A tile of the lawn photograph, in circumradii.</summary>
    private const float LawnTile = 1.4f;

    /// <summary>A tile of the pebble photograph, in circumradii. Not to
    /// scale: the scan is about a metre and a half of river bed, a hex some
    /// seven, and stones that small read as noise under the water - these are
    /// cobbles a board can see.</summary>
    private const float BedTile = 2.2f;

    /// <summary>The new style's photographs read so far, by file: each read
    /// once, null when it is not on disk (docs/new_style.md).</summary>
    private static readonly Dictionary<string, ImageTexture?> _photos = new();

    /// <summary>A tiling photograph under <see cref="AssetRoot.Ground"/>,
    /// read once, with its mipmaps; null when it is not on disk.</summary>
    private static ImageTexture? Photo(string file, string what)
    {
        if (_photos.TryGetValue(file, out ImageTexture? had))
            return had;
        _photos[file] = null;
        string path = AssetRoot.Ground + "/" + file;
        if (!System.IO.File.Exists(path))
            return null;
        Image img = Image.LoadFromFile(path);
        if (img is null || img.IsEmpty())
            return null;
        img.GenerateMipmaps();
        ImageTexture tex = ImageTexture.CreateFromImage(img);
        _photos[file] = tex;
        Log.Print($"stage3d: {what} {path} {img.GetWidth()}x{img.GetHeight()}");
        return tex;
    }

    /// <summary>The pebble photograph on the water's bed (<see cref="ShowBed"/>).</summary>
    internal static ImageTexture? Pebbles() => Photo("river_small_rocks/river_small_rocks_diff_2k.jpg", "bed");

    /// <summary>The earth photograph in place of the soil art (<see cref="ShowEarth"/>,
    /// Poly Haven dry_ground_rocks, CC0): of four bare earths tried on the
    /// slopes the nearest the painted soil (the user's pick, 2026-11-03);
    /// ambientCG Ground037 before it was moss and greyed them.</summary>
    internal static ImageTexture? EarthPhoto() => Photo("dry_ground_rocks/dry_ground_rocks_diff_2k.jpg", "earth");

    /// <summary>The stone of a rock nobody named (<see cref="ShowCrag"/>,
    /// ambientCG Rock023, CC0).</summary>
    internal static ImageTexture? Crag() => Photo("Rock023/Rock023_2K-JPG_Color.jpg", "crag");

    /// <summary>The grain of a rock with a type of its own - its layers keep
    /// their palette, the photograph gives them its light and dark
    /// (<see cref="ShowCrag"/>, Poly Haven rock_face, CC0).</summary>
    internal static ImageTexture? Face() => Photo("rock_face/rock_face_diff_2k.jpg", "face");

    /// <summary>Both rock photographs are on disk (<see cref="ShowCrag"/>).</summary>
    internal static bool CragsOnDisk => Crag() is not null && Face() is not null;

    /// <summary>The earth photograph (<see cref="EarthPhoto"/>) in place of the
    /// soil art the slopes, banks and bare ground wear, and so of the grain
    /// everything over it carries. Off by default, the new style
    /// (docs/new_style.md); <c>--earth</c> and the panel switch it on when the
    /// file is there.</summary>
    public bool ShowEarth
    {
        get => _showEarth;
        set
        {
            _showEarth = value;
            if (Relief is not null && _tops?.MaterialOverride is ShaderMaterial tops)
                tops.Param("earth_on", value && EarthPhoto() is not null ? 1.0f : 0.0f);
        }
    }

    private bool _showEarth;

    /// <summary>The rocks in photographs: a crag in the stone of
    /// <see cref="Crag"/>, a typed rock's layers grained by <see cref="Face"/>.
    /// Off by default, the new style (docs/new_style.md); <c>--crag</c> and
    /// the panel switch it on when both files are there.</summary>
    public bool ShowCrag
    {
        get => _showCrag;
        set
        {
            _showCrag = value;
            if (Relief is not null && _tops?.MaterialOverride is ShaderMaterial tops)
                tops.Param("crag_on", value && CragsOnDisk ? 1.0f : 0.0f);
        }
    }

    private bool _showCrag;

    /// <summary>A tile of the earth photograph, in circumradii: as the lawn's.</summary>
    private const float EarthTile = 1.4f;

    /// <summary>A tile of the rock photographs, in circumradii: a level's wall
    /// is about a third of a hex tall, and the stone's beds read across it.</summary>
    private const float CragTile = 1.2f;

    private static ImageTexture? _lawn;
    private static bool _lawnRead;

    /// <summary>The lawn photograph, read once, with its mipmaps; null when
    /// it is not on disk.</summary>
    internal static ImageTexture? Lawn()
    {
        if (_lawnRead)
            return _lawn;
        _lawnRead = true;
        string path = AssetRoot.Ground + "/grass_basecolor.jpg";
        if (!System.IO.File.Exists(path))
            return null;
        Image img = Image.LoadFromFile(path);
        if (img is null || img.IsEmpty())
            return null;
        img.GenerateMipmaps();
        _lawn = ImageTexture.CreateFromImage(img);
        Log.Print($"stage3d: lawn {path} {img.GetWidth()}x{img.GetHeight()}");
        return _lawn;
    }

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
        // which rock a point is (Relief.Rock) and its own mask (Relief.RockAux),
        // for the rocks' paint (ReliefRocks, RockPaint)
        var rock = new Vector2[n];
        // the soil's looks (Relief.Look, format 3): bare earth, stones, dunes,
        // swamp - CUSTOM0, read by SoilPaint
        var look = new float[n * 4];
        var index = new int[(nx - 1) * (ny - 1) * 6];
        float[] sand = f.Share[(int)Relief.Kind.Sand],
                forest = f.Share[(int)Relief.Kind.Forest];
        for (int j = 0; j < ny; j++)
            for (int i = 0; i < nx; i++)
            {
                int p = j * nx + i;
                float x = f.X0 + i * f.Cell + Origin.X, z = f.Z0 + j * f.Cell + Origin.Y / Squash;
                at[p] = new Vector3(x, f.Height[p] / rise, z);
                // on a rock, over two points either side: a wall is one or
                // two points across, and the next point's height alone
                // turned each facet of it its own way to the sun
                int reach = f.Cliff[p] > 0.05f ? 2 : 1;
                int i0 = Mathf.Max(i - reach, 0), i1 = Mathf.Min(i + reach, nx - 1);
                int j0 = Mathf.Max(j - reach, 0), j1 = Mathf.Min(j + reach, ny - 1);
                float hx = (f.Height[j * nx + i1] - f.Height[j * nx + i0]) / (2.0f * reach * f.Cell * rise);
                float hz = (f.Height[j1 * nx + i] - f.Height[j0 * nx + i]) / (2.0f * reach * f.Cell * rise);
                normal[p] = new Vector3(-hx, 1.0f, -hz).Normalized();
                // what the ground is, for the shader: sand, rock, wood, and how
                // far under the water it lies (the bed darker than the bank)
                float under = Mathf.Clamp(-f.Shore[p] / (f.Cell * 4.0f), 0.0f, 1.0f);
                paint[p] = new Color(sand[p], f.Cliff[p], forest[p], under);
                // and how deep in a hollow it lies, for its shade (Relief.Hollow)
                levels[p] = new Vector2(f.Levels[p] & 0xFFFFFF, f.Hollow[p]);
                rock[p] = new Vector2(f.Rock[p], f.RockAux[p]);
                for (int k = 0; k < 4; k++)
                    look[p * 4 + k] = f.Look[k][p];
            }
        int t = 0;
        for (int j = 0; j + 1 < ny; j++)
            for (int i = 0; i + 1 < nx; i++)
            {
                int a = j * nx + i, b = a + 1, c = a + nx, d = c + 1;
                // cut along the diagonal whose ends lie closer in height: one
                // way for every quad sawed a wall running across it into teeth
                // at the rim and facets that took the light in turns
                if (Mathf.Abs(at[a].Y - at[d].Y) <= Mathf.Abs(at[b].Y - at[c].Y))
                {
                    index[t++] = a; index[t++] = b; index[t++] = d;
                    index[t++] = a; index[t++] = d; index[t++] = c;
                }
                else
                {
                    index[t++] = a; index[t++] = b; index[t++] = c;
                    index[t++] = b; index[t++] = d; index[t++] = c;
                }
            }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = at;
        arrays[(int)Mesh.ArrayType.Normal] = normal;
        arrays[(int)Mesh.ArrayType.Color] = paint;
        arrays[(int)Mesh.ArrayType.TexUV2] = levels;
        arrays[(int)Mesh.ArrayType.TexUV] = rock;
        arrays[(int)Mesh.ArrayType.Custom0] = look;
        arrays[(int)Mesh.ArrayType.Index] = index;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays, null, null,
            (Mesh.ArrayFormat)((long)Mesh.ArrayCustomFormat.RgbaFloat
                               << (int)Mesh.ArrayFormat.FormatCustom0Shift));
        _tops.Mesh = mesh;
        _sides.Mesh = null;
        _edges.Mesh = null;
        BakeRelief(f);
        _tops.MaterialOverride = ReliefTurf(f, art);
        _glaze.MaterialOverride = null;
        BuildFrame(f);
        Roads(f);
        Reeds(f);
    }

    /// <summary>The board's frame (<see cref="Relief.Frame"/>); null until
    /// a relief is built, its mesh dropped on the prisms.</summary>
    private MeshInstance3D? _frame;

    /// <summary>The frame's width, in circumradii, and its top's bevel.</summary>
    internal const float FrameWide = 0.42f;
    private const float FrameBevel = 0.07f;

    /// <summary>The frame's top over the highest ground at its inner edge,
    /// in levels.</summary>
    private const float FrameLip = 0.25f;

    /// <summary>
    /// A wooden frame round the board's rectangle, as round a tabletop board:
    /// its top a lip over the ground and the sea at its inner edge, its outer
    /// face down past the deepest bed, so the raster's cut edge - the
    /// hexes' ends - never shows (the user's call, 2026-10-25).
    /// </summary>
    private void BuildFrame(Relief f)
    {
        if (_frame is null)
        {
            _frame = new MeshInstance3D { CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            AddChild(_frame);
        }
        float rise = RiseFactor, lift = Field.Lift;
        float r = Field.Atlas!.HexRect.Size.X * 0.5f;
        Rect2 inner = f.Frame;
        // the highest ground along the inner edge, and the lowest anywhere
        float edgeTop = f.SeaTop, low = float.MaxValue;
        foreach (float h in f.Height)
            low = Mathf.Min(low, h);
        int steps = 200;
        for (int k = 0; k <= steps; k++)
        {
            float t = k / (float)steps;
            float x = inner.Position.X + inner.Size.X * t, z = inner.Position.Y + inner.Size.Y * t;
            edgeTop = Mathf.Max(edgeTop, Mathf.Max(f.Sample(f.Height, x, inner.Position.Y), f.Sample(f.Height, x, inner.End.Y)));
            edgeTop = Mathf.Max(edgeTop, Mathf.Max(f.Sample(f.Height, inner.Position.X, z), f.Sample(f.Height, inner.End.X, z)));
        }
        float top = (edgeTop + FrameLip * lift) / rise;
        float bottom = (low - lift) / rise;
        float bevel = FrameBevel * r;
        var off = new Vector2(Origin.X, Origin.Y / Squash);
        Rect2 i = new(inner.Position + off, inner.Size);
        Rect2 b = i.Grow(FrameWide * r - bevel), o = i.Grow(FrameWide * r);

        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        // the four sides, each as its profile's faces: the inner wall, the
        // top, the bevel and the outer face
        for (int side = 0; side < 4; side++)
        {
            FrameFace(st, i, top, i, bottom, side, 0.0f);
            FrameFace(st, b, top, i, top, side, 1.0f);
            FrameFace(st, o, top - bevel * 0.6f, b, top, side, 2.0f);
            FrameFace(st, o, bottom, o, top - bevel * 0.6f, side, 3.0f);
        }
        _frame.Mesh = st.Commit();
        _frame.MaterialOverride ??= new ShaderMaterial { Shader = FrameShader };
        var ink = (ShaderMaterial)_frame.MaterialOverride;
        ink.Param("sun_dir", Sun.Normalized());
        if (Oak() is { } oak)
        {
            ink.Param("wood_tex", oak);
            ink.Param("wood_tile", 1.0f / (r * OakTile));
        }
        ink.Param("wood_on", Oak() is not null ? 1.0f : 0.0f);
    }

    /// <summary>A tile of the oak photograph, in circumradii: across it a
    /// plank or so the frame's width.</summary>
    private const float OakTile = 2.5f;

    private static ImageTexture? _oak;
    private static bool _oakRead;

    /// <summary>The frame's oak veneer (<see cref="AssetRoot.Ground"/>,
    /// <c>wood_basecolor.jpg</c>, Poliigon WoodVeneerOak 7760), read once with
    /// its mipmaps; null when it is not on disk - the frame's painted grain
    /// then.</summary>
    internal static ImageTexture? Oak()
    {
        if (_oakRead)
            return _oak;
        _oakRead = true;
        string path = AssetRoot.Ground + "/wood_basecolor.jpg";
        if (!System.IO.File.Exists(path))
            return null;
        Image img = Image.LoadFromFile(path);
        if (img is null || img.IsEmpty())
            return null;
        img.GenerateMipmaps();
        _oak = ImageTexture.CreateFromImage(img);
        Log.Print($"stage3d: oak {path} {img.GetWidth()}x{img.GetHeight()}");
        return _oak;
    }

    /// <summary>One face of the frame on one side: the quad between rect
    /// <paramref name="a"/>'s edge at height <paramref name="ya"/> and rect
    /// <paramref name="c"/>'s at <paramref name="yc"/>, corners mitred by the
    /// rects' own corners. Side 0 is -Z (far), 1 +X, 2 +Z (near), 3 -X;
    /// UV runs along the side (world units) and says which face it is.</summary>
    private static void FrameFace(SurfaceTool st, Rect2 a, float ya, Rect2 c, float yc, int side, float face)
    {
        Vector3 Corner(Rect2 rc, float y, int k) => k switch
        {
            0 => new Vector3(rc.Position.X, y, rc.Position.Y),
            1 => new Vector3(rc.End.X, y, rc.Position.Y),
            2 => new Vector3(rc.End.X, y, rc.End.Y),
            _ => new Vector3(rc.Position.X, y, rc.End.Y),
        };
        int k0 = side, k1 = (side + 1) % 4;
        Vector3 a0 = Corner(a, ya, k0), a1 = Corner(a, ya, k1);
        Vector3 c0 = Corner(c, yc, k0), c1 = Corner(c, yc, k1);
        Vector3 n = (a1 - a0).Cross(c0 - a0).Normalized();
        // outward-facing whichever way round the corners came
        Vector3 mid = (a0 + a1 + c0 + c1) * 0.25f;
        Vector3 hub = new((a.Position.X + a.End.X) * 0.5f, mid.Y, (a.Position.Y + a.End.Y) * 0.5f);
        bool flip = face == 0 ? n.Dot(hub - mid) < 0.0f : face == 1 ? n.Y < 0.0f : n.Dot(mid - hub) < 0.0f;
        if (flip)
            n = -n;
        bool alongX = side % 2 == 0;
        foreach (Vector3 v in flip ? new[] { a0, c0, c1, a0, c1, a1 } : new[] { a0, a1, c1, a0, c1, c0 })
        {
            st.SetNormal(n);
            st.SetUV(new Vector2(alongX ? v.X : v.Z, face));
            st.SetUV2(new Vector2(alongX ? v.Z : v.X, 0.0f));
            st.AddVertex(v);
        }
    }

    private static readonly Shader FrameShader = new()
    {
        Code = @"shader_type spatial;
render_mode unshaded, cull_disabled;
// The board's frame: dark wood, its grain along each side, stepped by the
// turn to the sun as the ground is (Stage3D.Relief, ReliefPaint).
uniform vec3 wood : source_color = vec3(0.40, 0.27, 0.16);
uniform vec3 wood_dark : source_color = vec3(0.23, 0.15, 0.09);
uniform vec3 sun_dir = vec3(0.0, 1.0, 0.0);
// The oak photograph: one tile per this many world units, and how bright
uniform sampler2D wood_tex : source_color, filter_linear_mipmap, repeat_enable, hint_default_white;
uniform float wood_on = 0.0;
uniform float wood_tile = 0.003;
uniform float wood_gain = 1.0;
varying vec3 world;
varying vec3 wn;

float hash(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }
float noise(vec2 p) {
    vec2 i = floor(p), f = fract(p);
    vec2 u = f * f * (3.0 - 2.0 * f);
    return mix(mix(hash(i), hash(i + vec2(1.0, 0.0)), u.x),
               mix(hash(i + vec2(0.0, 1.0)), hash(i + vec2(1.0, 1.0)), u.x), u.y);
}

void vertex() {
    world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
    wn = normalize((MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz);
}

void fragment() {
    vec3 n = normalize(wn);
    // along the side (UV.x) and across it: the height on the faces that
    // stand, the distance in from the edge on the top (UV2.x)
    float across = abs(n.y) > 0.5 ? UV2.x : world.y;
    float grain = noise(vec2(UV.x * 0.006, across * 0.08));
    grain = 0.6 * grain + 0.4 * noise(vec2(UV.x * 0.02, grain * 6.0));
    vec3 col = mix(wood_dark, wood, smoothstep(0.25, 0.75, grain));
    // the oak photograph (AssetRoot.Ground), its grain - the picture's
    // height - along the side
    if (wood_on > 0.5)
        col = texture(wood_tex, vec2(across, UV.x) * wood_tile).rgb * wood_gain;
    float d = dot(n, sun_dir);
    float flat_d = sun_dir.y;
    float step_ = d > flat_d - 0.08 ? 1.0 : d > flat_d - 0.3 ? 0.86 : d > 0.0 ? 0.72 : 0.6;
    col *= step_;
    // the bevel a shade lighter, the edge it catches the light on
    if (UV.y > 1.5 && UV.y < 2.5)
        col *= 1.18;
    ALBEDO = col;
}
",
    };

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
        ink.Param("brow_on", ShowBrow ? 1.0f : 0.0f);
        ink.Param("brow_below", BrowBelow);
        ink.Param("brow_px", BrowPx);
        ink.Param("hollow_ink", Relief.HollowInk);
        ink.Param("rock_tab", RockTable(f));
        float wind = Mathf.DegToRad(SoilWind);
        ink.Param("soil_wind", new Vector2(Mathf.Cos(wind), Mathf.Sin(wind)));
        if (Lawn() is { } lawn)
        {
            ink.Param("lawn_tex", lawn);
            // a tile about two thirds of a hex: the photograph is a couple of
            // metres of turf, a hex some seven
            ink.Param("lawn_tile", 1.0f / (r * LawnTile));
        }
        ink.Param("lawn_on", ShowLawn && Lawn() is not null ? 1.0f : 0.0f);
        if (Pebbles() is { } bed)
        {
            ink.Param("bed_tex", bed);
            ink.Param("bed_tile", 1.0f / (r * BedTile));
        }
        ink.Param("bed_on", ShowBed && Pebbles() is not null ? 1.0f : 0.0f);
        if (EarthPhoto() is { } earth)
        {
            ink.Param("earth_tex", earth);
            ink.Param("earth_tile", 1.0f / (r * EarthTile));
        }
        ink.Param("earth_on", ShowEarth && EarthPhoto() is not null ? 1.0f : 0.0f);
        if (CragsOnDisk)
        {
            ink.Param("crag_tex", Crag()!);
            ink.Param("face_tex", Face()!);
            ink.Param("crag_tile", 1.0f / (r * CragTile));
        }
        ink.Param("crag_on", ShowCrag && CragsOnDisk ? 1.0f : 0.0f);
        ink.Param("turf_on", ShowTurf ? 1.0f : 0.0f);
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
                // off the plot is the sea, and its hexes are drawn too
                bool on = Field.InBounds(cell);
                Color ink = on ? Field.InkFor(cell) : Colors.White;
                img.SetPixel(q, r, new Color(ink.R, ink.G, ink.B, 1.0f));
            }
        return ImageTexture.CreateFromImage(img);
    }

    /// <summary>The ground's triangles in world units for what stands on it -
    /// the raster's quads, at every other point: the shadow's skin and the
    /// rubble need the ground's shape, not its grain. For the
    /// <paramref name="skin"/> the rocks and the walls are left out (<see
    /// cref="Skin"/>); the rubble keeps them to rest against.</summary>
    private List<Vector3> ReliefTriangles(Relief f, bool skin = false)
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
                // near a rock only the flat keeps the skin: its blurred mask
                // runs on past the foot of its wall, and the wall's lower half
                // is no steeper there than a bank
                float rock = Mathf.Max(Mathf.Max(f.Cliff[j * f.Nx + i], f.Cliff[j * f.Nx + i + Skip]),
                                       Mathf.Max(f.Cliff[(j + Skip) * f.Nx + i], f.Cliff[(j + Skip) * f.Nx + i + Skip]));
                float upright = !skin ? 0.0f : rock > RockSkin ? FlatSkin : WallSkin;
                // the gentler diagonal, as the ground's own (BuildRelief)
                if (Mathf.Abs(a.Y - d.Y) <= Mathf.Abs(b.Y - c.Y))
                {
                    Skin(outp, a, b, d, upright);
                    Skin(outp, a, d, c, upright);
                }
                else
                {
                    Skin(outp, a, b, c, upright);
                    Skin(outp, b, d, c, upright);
                }
            }
        return outp;
    }

    /// <summary>
    /// A triangle of the skin, unless it stands less <paramref name="upright"/>
    /// than that (its normal's height in its length) - a wall. At every other
    /// point the skin cannot follow a steep face a point or two across: it
    /// hung in front of the wall here and sank behind it there, and the
    /// shadow it carried - the rock's own, cast on it - lay over the face in
    /// a stepped dark wedge and see-through teeth. A wall has its own shade
    /// (<see cref="ReliefPaint"/>, one tone a side); the ground at its foot
    /// keeps the skin and the shadow on it.
    /// </summary>
    private static void Skin(List<Vector3> outp, Vector3 a, Vector3 b, Vector3 c, float upright)
    {
        Vector3 n = (b - a).Cross(c - a);
        if (Mathf.Abs(n.Y) < upright * n.Length())
            return;
        outp.Add(a); outp.Add(b); outp.Add(c);
    }

    /// <summary>The skin's steepest away from a rock: a triangle whose normal
    /// stands less upright than this (about 60 degrees of slope) is a wall.</summary>
    private const float WallSkin = 0.5f;

    /// <summary>Near a rock (a corner of the quad this far into <see
    /// cref="Relief.Cliff"/>), only this upright - about 25 degrees of slope.</summary>
    private const float FlatSkin = 0.9f;
    private const float RockSkin = 0.02f;

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

        // the sea round the board: deep, over the whole frame
        {
            var off = new Vector2(Origin.X, Origin.Y / Squash);
            Rect2 sea = new(f.Frame.Position + off, f.Frame.Size);
            hub[SeaSlot] = sea.GetCenter();
            sink[SeaSlot] = 1.0f;
            low = new Vector2(Mathf.Min(low.X, sea.Position.X), Mathf.Min(low.Y, sea.Position.Y));
            high = new Vector2(Mathf.Max(high.X, sea.End.X), Mathf.Max(high.Y, sea.End.Y));
        }

        int nx = f.Nx;
        float rise = RiseFactor, reach = f.Cell * 3.0f;
        // which body (its first cell's slot) each point's water belongs to
        var ord_ = new int[f.Height.Length];
        // and how much of it is the deep water's murk, point by point
        // (DeepShare) - the slot alone switched it a raster quad at a time
        var share = new float[f.Height.Length];
        for (int p = 0; p < ord_.Length; p++)
        {
            ord_[p] = float.IsNaN(f.Surface[p]) ? -1 : WaterSlot(f, p);
            if (ord_[p] >= 0)
                share[p] = DeepShare(f, p, sink[ord_[p]]);
        }
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
                    st.SetUV2(new Vector2(slot, share[p]));
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
            // the murk off each vertex's own share, not off its slot (DeepShare)
            _deepInk.Param("sink_smooth", 1.0f);
            _deepInk.Param("rim_n", normal);
            _deepInk.Param("rim_d", stand);
            _deepInk.Param("rim_span", inradius);
            _deepInk.Param("squash", Squash);
            _deepInk.Param("rise", RiseFactor);
            // the grid on the water as on the ground: the sea's hexes are
            // cells cut by the frame, and read as hexes only with their sides
            Vector2 hexHub = Origin + Field.CentreOffset;
            _deepInk.Param("hex_r", Field.Atlas!.HexRect.Size.X * 0.5f);
            _deepInk.Param("hex_off", new Vector2(hexHub.X, hexHub.Y / Squash));
            _deepInk.Param("hex_color", new Color(GridInk.R, GridInk.G, GridInk.B));
            _deepInk.Param("hex_opacity", GridInk.A);
        }
    }

    /// <summary>How far the soft edge between deep water and a ford reaches
    /// into each, as a share of half the distance between two cells' middles.</summary>
    private const float DeepBand = 0.35f;

    /// <summary>
    /// How much of a raster point's water is the deep water's murk: 1 inside a
    /// deep hex, 0 inside a ford, and between them a ramp across the real hex
    /// edge, ½ on the edge itself.
    ///
    /// <b>Off the hex's own geometry, not off the raster.</b> The murk used to be
    /// switched by the slot each quad of the sheet took from its first corner's
    /// owner, so a deep hex came up as a staircase of R/12 quads, pushed a step
    /// down and to the right of its outline, its edge a hard cut over a bed that
    /// slopes smoothly - a dark sticker on the ford rather than a hole in it
    /// (2026-10-26). Here the point's hex is found by its position, the edges
    /// it shares with water of the other kind are measured, and the nearest
    /// sets the share. Interpolated across the sheet's triangles it is smooth,
    /// and both sides of an edge agree on ½, so nothing steps there.
    ///
    /// The sea is deep. A point on a dry bank keeps its water's own share
    /// (<paramref name="slotShare"/>): there is no edge of two waters on it.
    /// </summary>
    private float DeepShare(Relief f, int p, float slotShare)
    {
        int nx = f.Nx;
        var at = new Vector2(f.X0 + (p % nx) * f.Cell + Origin.X, f.Z0 + (p / nx) * f.Cell + Origin.Y / Squash);
        Vector2I cell = Field.FlatCellAt(new Vector2(at.X - Origin.X, at.Y * Squash - Origin.Y));
        if (Deepness(cell) is not bool deep)
            return slotShare;
        Vector2 hub = Hub(cell);
        float nearest = float.MaxValue, band = 1.0f;
        foreach (int heading in HexField.EdgeHeadings)
        {
            Vector2I next = HexField.Step(cell, heading);
            if (Deepness(next) is not bool other || other == deep)
                continue;
            Vector2 axis = Hub(next) - hub;
            float half = axis.Length() * 0.5f;
            // how far in from the edge the two cells share
            float inside = half - (at - hub).Dot(axis / Mathf.Max(axis.Length(), 0.0001f));
            if (inside < nearest)
            {
                nearest = inside;
                band = half * DeepBand;
            }
        }
        if (nearest == float.MaxValue)
            return deep ? 1.0f : 0.0f;
        float s = Mathf.SmoothStep(0.0f, band, Mathf.Max(nearest, 0.0f));
        return deep ? 0.5f + 0.5f * s : 0.5f - 0.5f * s;
    }

    /// <summary>Deep, a ford, or not water at all (null). Off the board is the
    /// sea, and the sea is deep.</summary>
    private bool? Deepness(Vector2I cell) =>
        !Field.InBounds(cell) ? true : Field.IsWater(cell) ? Field.IsDeep(cell) : null;

    /// <summary>A cell's middle on the water's plane, world x and z.</summary>
    private Vector2 Hub(Vector2I cell)
    {
        Vector2 flat = Origin + Field.FlatAnchor(cell) + Field.CentreOffset;
        return new Vector2(flat.X, flat.Y / Squash);
    }

    /// <summary>The water slot a raster point's water belongs to: its own
    /// hex's if that is water, else the neighbour whose surface it took.</summary>
    private int WaterSlot(Relief f, int p)
    {
        Vector2I c = f.Owner[p];
        if (c.X < 0)
            return SeaSlot;
        if (Field.IsWater(c))
            return Mathf.Min(Field.WaterIndex(c), MaxWaterCells - 1);
        bool sea = false;
        for (int h = 30; h < 360; h += 60)
        {
            Vector2I o = HexField.Step(c, h);
            if (Field.InBounds(o) && Field.IsWater(o) && Mathf.IsEqualApprox(Field.WaterTop(o), f.Surface[p]))
                return Mathf.Min(Field.WaterIndex(o), MaxWaterCells - 1);
            sea |= !Field.InBounds(o);
        }
        return sea && Mathf.IsEqualApprox(f.SeaTop, f.Surface[p]) ? SeaSlot : -1;
    }

    /// <summary>The sea's water slot (<see cref="Relief.SeaTop"/>): the last,
    /// deep, no cell's.</summary>
    private const int SeaSlot = MaxWaterCells - 1;

    private static readonly Shader ReliefSoil = new()
    {
        Code = SoilShader
            .Replace("varying vec3 ground;", ReliefHead)
            .Replace("    ground = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;",
                     "    ground = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;\n"
                     + "    ground_n = normalize((MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz);\n"
                     + "    ground_levels = int(UV2.x + 0.5);\n"
                     + "    ground_hollow = UV2.y;\n"
                     + "    rock_id = int(round(UV.x));\n"
                     + "    rock_aux = UV.y;\n"
                     + "    soil_look = CUSTOM0;")
            .Replace("    vec4 paint = texture(art, UV) * COLOR;", ReliefPaint)
            .Replace("    ALBEDO = lit;", ReliefGrid),
    };

    private const string ReliefHead = @"varying vec3 ground;
varying vec3 ground_n;
varying flat int ground_levels;
// How deep in a hollow (Relief.Hollow), and how much of the light that takes.
varying float ground_hollow;
uniform float hollow_ink = 0.3;
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
// The pebble photograph (AssetRoot.Ground) on the bed instead of bed_ink, one
// tile per this many world units, and how bright under the water
uniform sampler2D bed_tex : source_color, filter_linear_mipmap, repeat_enable, hint_default_white;
uniform float bed_on = 0.0;
uniform float bed_tile = 0.01;
uniform float bed_gain = 0.8;
// how far the stones stand out of their own mean tone: the water flattens them
uniform float bed_contrast = 1.7;
// The new style's photographs (docs/new_style.md), each one tile per this
// many world units, laid from three sides like the art: the earth in place
// of the soil art; a crag's stone; a typed rock's grain off the face's
// light and dark, its mean and how far that is stretched
uniform sampler2D earth_tex : source_color, filter_linear_mipmap, repeat_enable, hint_default_white;
uniform float earth_on = 0.0;
uniform float earth_tile = 0.01;
uniform sampler2D crag_tex : source_color, filter_linear_mipmap, repeat_enable, hint_default_white;
uniform sampler2D face_tex : source_color, filter_linear_mipmap, repeat_enable, hint_default_white;
uniform float crag_on = 0.0;
uniform float crag_tile = 0.01;
uniform float crag_gain = 1.0;
uniform float face_mean = 0.3;
uniform float face_contrast = 10.0;

vec3 photo3(sampler2D t, vec3 p, vec3 tw, float k) {
    return texture(t, p.xz * k).rgb * tw.y + texture(t, p.zy * k).rgb * tw.x
         + texture(t, p.xy * k).rgb * tw.z;
}
// The lawn photograph (AssetRoot.Ground), one tile per this many world units,
// and how bright: on when it is on disk and switched on
uniform sampler2D lawn_tex : source_color, filter_linear_mipmap, repeat_enable, hint_default_white;
uniform float lawn_on = 0.0;
// Grass on the ground's texture at all (photo or patches); off: bare earth
uniform float turf_on = 0.0;
uniform float lawn_tile = 0.01;
uniform float lawn_gain = 1.0;
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
    // past the board's rectangle: the sea, its hexes drawn as the board's
    if (c.x < 0 || c.y < 0 || c.x >= cells_size.x || c.y >= cells_size.y)
        return vec4(1.0, 1.0, 1.0, 1.0);
    return texelFetch(cells, c, 0);
}
" + RockPaint + SoilPaint + RoadPaint;

    private const string ReliefPaint = @"    // the art from above on the flats, from the side on a wall: laid from
    // above only, a wall a level tall drew one row of it down its whole height
    vec3 tw = pow(abs(normalize(ground_n)), vec3(4.0));
    tw /= tw.x + tw.y + tw.z;
    vec3 soil = texture(art, art_rect.xy + tri(ground.xz * art_tile) * art_rect.zw).rgb * tw.y;
    soil += texture(art, art_rect.xy + tri(ground.zy * art_tile) * art_rect.zw).rgb * tw.x;
    soil += texture(art, art_rect.xy + tri(ground.xy * art_tile) * art_rect.zw).rgb * tw.z;
    if (earth_on > 0.5)
        soil = photo3(earth_tex, ground, tw, earth_tile);
    float grain = dot(soil, vec3(0.299, 0.587, 0.114));
    // the kinds, each its own colour carrying the art's grain
    soil = mix(soil, sand_ink * (0.6 + 0.6 * grain), smoothstep(0.25, 0.6, COLOR.r));
    // the soil's looks over the kinds (Stage3D.Soil, format 3)
    soil = soil_look_at(soil, grain);
    // rock: the tight share (Relief.Cliff), cut where the wall stands; a rock
    // with a type of its own paints itself (RockPaint), a crag as it always was
    if (rock_id >= 0 && texelFetch(rock_tab, ivec2(9, rock_id), 0).x > 0.5) {
        float rgrain = grain;
        if (crag_on > 0.5)
            rgrain = clamp(0.5 + (dot(photo3(face_tex, ground, tw, crag_tile), vec3(0.299, 0.587, 0.114))
                                  - face_mean) * face_contrast, -0.5, 1.5);
        vec4 rk = rock_look(rock_id, ground, normalize(ground_n), rgrain, COLOR.g, rock_aux);
        soil = mix(soil, rk.rgb, rk.a);
    } else {
        vec3 crag = crag_on > 0.5 ? photo3(crag_tex, ground, tw, crag_tile) * crag_gain
                                  : rock_ink * (0.55 + 0.7 * grain);
        soil = mix(soil, crag, smoothstep(0.5, 0.62, COLOR.g));
    }
    soil = mix(soil, wood_ink * (0.6 + 0.6 * grain), 0.6 * smoothstep(0.3, 0.7, COLOR.b));
    // grass over the rest, in patches, the earth through it on steep banks
    // (the cells' painted lawn, Grass3D, is a hexagon a cell - off on the relief)
    float bare = max(smoothstep(0.25, 0.6, COLOR.r), smoothstep(0.25, 0.6, COLOR.g));
    float bank = length(normalize(ground_n).xz);
    float patch_ = mire_fbm(ground.xz * 0.018 + vec2(7.0, 3.0));
    // none on a terrace's slope: grass on the flats, earth on the banks
    // (the tufts keep off them too, BoardDress.GrassSlope)
    float flat_ = (1.0 - bare) * (1.0 - COLOR.a) * (1.0 - smoothstep(0.2, 0.32, bank))
                * (1.0 - smoothstep(0.2, 0.6, soil_look.x + soil_look.y + soil_look.w));
    float lawn = flat_ * smoothstep(0.30, 0.42, patch_ + 0.25);
    vec3 turf = mix(grass_ink, grass_tip, smoothstep(0.35, 0.75, mire_fbm(ground.xz * 0.05)));
    if (turf_on < 0.5) {
        // bare earth everywhere: the soil art the slopes wear (the user's call)
    } else if (lawn_on > 0.5) {
        // the photograph (AssetRoot.Ground) over every flat, not in patches:
        // two scales, the second turned, mixed by a noise, so its tile does
        // not repeat across a board
        vec2 q = ground.xz * lawn_tile;
        vec2 q2 = mat2(vec2(0.8, 0.6), vec2(-0.6, 0.8)) * ground.xz * lawn_tile * 0.37 + vec2(0.31, 0.17);
        vec3 photo = mix(texture(lawn_tex, q).rgb, texture(lawn_tex, q2).rgb,
                         smoothstep(0.35, 0.65, mire_fbm(ground.xz * 0.008)));
        soil = mix(soil, photo * lawn_gain, flat_ * smoothstep(0.0, 0.12, patch_ + 0.25));
    } else
        soil = mix(soil, turf * (0.75 + 0.5 * grain), lawn);
    vec3 bed = bed_ink * (0.6 + 0.6 * grain);
    if (bed_on > 0.5) {
        // the pebbles, two scales mixed by a noise like the lawn, so the tile
        // does not repeat across a pond
        vec2 b = ground.xz * bed_tile;
        vec2 b2 = mat2(vec2(0.8, 0.6), vec2(-0.6, 0.8)) * ground.xz * bed_tile * 0.43 + vec2(0.57, 0.23);
        bed = mix(texture(bed_tex, b).rgb, texture(bed_tex, b2).rgb,
                  smoothstep(0.35, 0.65, mire_fbm(ground.xz * 0.011 + vec2(3.0, 9.0))));
        float mean_ = dot(bed, vec3(0.299, 0.587, 0.114));
        bed = max(mix(vec3(mean_), bed, 1.15) + (mean_ - 0.42) * (bed_contrast - 1.0), vec3(0.0)) * bed_gain;
    }
    soil = mix(soil, bed, COLOR.a);
    // the roads over all of it (Stage3D.Roads, format 3)
    soil = road_at(soil, grain);
    vec3 hexc = hex_at(ground.xz);
    vec4 cink = cell_ink(hexc.xy);
    // cel steps by the turn to the sun: three, as the prisms' walls had
    float d = dot(normalize(ground_n), sun_dir);
    float flat_d = sun_dir.y;
    float step_ = d > flat_d - 0.08 ? 1.0 : d > flat_d - 0.3 ? 0.86 : 0.72;
    // a wall - a rock's face - one step darker still, so a cliff reads as
    // one and not as the far side of a hill
    // On a rock the wall starts lower and takes one tone a side - lit or
    // shaded by which way it faces, not by how steep each facet of it is:
    // facets either side of the cut flipped between steps into patches.
    vec3 wn_ = normalize(ground_n);
    float steep = length(wn_.xz);
    if (steep > 0.8 || (COLOR.g > 0.5 && steep > 0.55)) {
        vec2 sun_h = sun_dir.xz / max(length(sun_dir.xz), 1e-4);
        step_ = COLOR.g > 0.5 ? mix(0.5, 0.64, smoothstep(-0.35, 0.35, dot(wn_.xz / max(steep, 1e-4), sun_h)))
                              : min(step_, 0.56);
    }
    // a pit's floor in shade: from above it is the same lawn as its rim
    // - not on a rock: the foot of its wall lies under the blurred cap, and
    // a bay of it took the hollow's shade in a stepped wedge
    step_ *= 1.0 - hollow_ink * ground_hollow * (1.0 - smoothstep(0.05, 0.2, COLOR.g));
    vec4 paint = vec4(soil * step_ * mix(vec3(1.0), cink.rgb, cink.a), 1.0);";

    private const string ReliefGrid = @"    {
        float hl = length(normalize(ground_n).xz);
        float px = length(fwidth(ground.xz));
        float lw = max(hex_w, hex_px * px);
        float aa = max(fwidth(hexc.z), 1e-4);
        float line = (1.0 - smoothstep(lw, lw + aa, hexc.z)) * hex_on * cink.a
                     * (1.0 - smoothstep(0.45, 0.7, hl))
                     // none on the rock: it is no cell anything stands on
                     * (1.0 - smoothstep(0.5, 0.65, COLOR.g));
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
        // Relief.Cliff crosses 0.5-0.72), and off anything as steep as a
        // wall: a neighbour's brow ran on across the rock's face
        float brow = (1.0 - smoothstep(bw, bw + fwidth(t), off)) * brow_on * near
                     * (1.0 - COLOR.a) * (1.0 - smoothstep(0.1, 0.22, COLOR.g))
                     * (1.0 - smoothstep(0.82, 0.9, length(normalize(ground_n).xz)))
                     // nor across the dunes: their crests cross a level's
                     // line with no terrace there (Relief.Dunes)
                     * (1.0 - smoothstep(0.1, 0.3, soil_look.z));
        lit = mix(lit, brow_ink, brow * brow_opacity);
    }
    ALBEDO = lit;";
}
