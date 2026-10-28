using System.Collections.Generic;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The soil's looks on the relief (format 3, docs/map-format3-plan.md Ф4):
/// bare earth, stones, dunes and swamp - the renders that chose them are
/// tools/render/ground.py.
///
/// <b>The raster holds the form, the shader the drawing</b> - the rocks' rule
/// (<see cref="ReliefRocks"/>). How much of each look a point is comes in the
/// mesh's CUSTOM0 (<see cref="Relief.Look"/>, blurred as the kinds are, so a
/// look ends in a soft shore and not a hexagon); the dunes' ridges are in the
/// height (<c>Relief.Dunes</c>); the pebbles, ripples, tussocks, pools and
/// duckweed are drawn per pixel here, where the 1/12 raster cannot blur them.
///
/// <b>The reeds stand up.</b> A swamp read flat until its reeds had height
/// (the user's call on the renders), so they are stalks: crossed blades in
/// clumps over every swamp cell, one mesh a board, built with the relief.
/// </summary>
public sealed partial class Stage3D
{
    /// <summary>The wind the dunes' ripples run across, degrees on the ground
    /// (0 along +X): the board's climate, set with the relief.</summary>
    public float SoilWind { get; set; } = RockClimate.DefaultWind;

    private const string SoilPaint = @"
// The soil's looks (Stage3D.Soil): how much of each a point is - bare earth,
// stones, dunes, swamp - from Relief.Look, the drawing per pixel.
varying vec4 soil_look;
uniform vec2 soil_wind = vec2(0.906, 0.423);
uniform vec3 soil_earth : source_color = vec3(0.50, 0.38, 0.26);
uniform vec3 soil_stone : source_color = vec3(0.64, 0.62, 0.58);
uniform vec3 soil_bog : source_color = vec3(0.31, 0.29, 0.19);
uniform vec3 soil_tussock : source_color = vec3(0.44, 0.48, 0.22);
uniform vec3 soil_pool : source_color = vec3(0.16, 0.22, 0.21);
uniform vec3 soil_weed : source_color = vec3(0.48, 0.60, 0.22);

vec3 soil_look_at(vec3 soil, float grain) {
    vec2 q = ground.xz / hex_r;
    // bare earth, and under the stones: the art's grain in an earth tone
    float bare = soil_look.x + soil_look.y;
    if (bare > 0.01)
        soil = mix(soil, soil_earth * (0.7 + 0.6 * grain), smoothstep(0.2, 0.6, bare));
    if (soil_look.y > 0.01) {
        // pebbles everywhere, cobbles here and there, a boulder now and then -
        // the renders' sizes, boulders 0.05-0.10 of a circumradius
        float st = smoothstep(0.3, 0.7, soil_look.y);
        vec2 pe = rock_cells(q / 0.05);
        float peb = 1.0 - smoothstep(0.22, 0.36, pe.x);
        vec2 co = rock_cells(q / 0.14 + 3.7);
        float cob = (1.0 - smoothstep(0.26, 0.40, co.x)) * step(0.55, rock_hash(floor(q / 0.14 + 3.7)));
        vec2 bo = rock_cells(q / 0.4 + 11.0);
        float bou = (1.0 - smoothstep(0.17, 0.24, bo.x)) * step(0.75, rock_hash(floor(q / 0.4 + 11.0)));
        float tone = 0.8 + 0.35 * rock_noise(q / 0.04);
        soil = mix(soil, soil_stone * tone * 0.9, 0.6 * peb * st);
        soil = mix(soil, soil_stone * tone, cob * st);
        soil = mix(soil, soil_stone * (tone + 0.12), bou * st);
    }
    if (soil_look.z > 0.01) {
        // ripples across the wind, wavering, on the dunes the raster raised
        float along = dot(q, soil_wind);
        float across = dot(q, vec2(-soil_wind.y, soil_wind.x));
        float rip = sin((along + 0.025 * sin(across * 7.0)) * 6.2832 / 0.06);
        soil *= 1.0 + 0.08 * rip * smoothstep(0.3, 0.7, soil_look.z);
    }
    if (soil_look.w > 0.01) {
        // swamp: dark wet ground, tussocks, pools between them with duckweed
        float sw = smoothstep(0.3, 0.7, soil_look.w);
        vec3 c = soil_bog * (0.75 + 0.5 * grain);
        // tussocks: a round hump of sedge in about half the cells of a coarse
        // grid, not a speckle in every one
        vec2 tu = rock_cells(q / 0.2 + 5.0);
        float tuss = (1.0 - smoothstep(0.24, 0.38, tu.x)) * step(0.45, rock_hash(floor(q / 0.2 + 5.0)));
        c = mix(c, soil_tussock * (0.8 + 0.4 * rock_noise(q / 0.03)), tuss);
        float wet = rock_fbm(q / 0.35 + 13.0);
        float pool = smoothstep(0.55, 0.57, wet) * (1.0 - tuss);
        // duckweed in the still middle of a pool, in drifts
        float weed = smoothstep(0.55, 0.7, rock_noise(q / 0.05) * 0.6 + rock_noise(q / 0.012) * 0.4)
                   * smoothstep(0.6, 0.66, wet);
        c = mix(c, soil_pool, pool);
        c = mix(c, soil_weed, pool * weed * 0.85);
        soil = mix(soil, c, sw);
    }
    return soil;
}
";

    // --- the reeds -------------------------------------------------------------

    private MeshInstance3D? _reeds;

    /// <summary>Clumps a swamp cell, stalks a clump, and a stalk's height in
    /// metres (the grass's metre, <see cref="BoardDress.GrassPpm"/>).</summary>
    private const int ReedClumps = 9, ReedStalksLo = 9, ReedStalksHi = 15;
    private const float ReedTallLo = 1.8f, ReedTallHi = 2.9f;

    private static readonly Color ReedFoot = new(0.24f, 0.31f, 0.13f);
    private static readonly Color ReedTip = new(0.58f, 0.62f, 0.30f);
    private static readonly Color ReedHead = new(0.36f, 0.22f, 0.12f);

    /// <summary>
    /// The swamp's reeds over the relief just built: clumps at hashed points
    /// within each swamp cell (<see cref="HexField.SoilAt"/>), each stalk two
    /// crossed blades narrowing to the tip, a few with a cattail's head. One
    /// mesh, unshaded in the cel look, no wind - nothing on the board sways
    /// that is not grass. None, and the old ones gone, on a board with no
    /// swamp.
    /// </summary>
    private void Reeds(Relief f)
    {
        if (Field.Atlas is null)
            return;
        float r = Field.Atlas.HexRect.Size.X * 0.5f;
        float m = BoardDress.GrassPpm;
        var verts = new List<Vector3>();
        var colors = new List<Color>();
        for (int q = 0; q < Field.Columns; q++)
        for (int w = 0; w < Field.Rows; w++)
        {
            var cell = new Vector2I(q, w);
            if (!Field.InBounds(cell) || Field.SoilAt(cell) != TankSpriteTest.Soil.Swamp)
                continue;
            Vector2 mid = Field.FlatAnchor(cell) + Field.CentreOffset;
            var rng = new RandomNumberGenerator { Seed = (ulong)(7919 * q + 104729 * w + 17) };
            for (int k = 0; k < ReedClumps; k++)
            {
                float a = rng.RandfRange(0.0f, Mathf.Tau), d = Mathf.Sqrt(rng.Randf()) * 0.72f * r;
                Vector2 clump = mid + new Vector2(Mathf.Cos(a), Mathf.Sin(a) * Field.Squash) * d;
                int stalks = rng.RandiRange(ReedStalksLo, ReedStalksHi);
                for (int s = 0; s < stalks; s++)
                {
                    float b = rng.RandfRange(0.0f, Mathf.Tau), e = rng.Randf() * 0.09f * r;
                    Vector2 flat = clump + new Vector2(Mathf.Cos(b), Mathf.Sin(b) * Field.Squash) * e;
                    var foot = new Vector3(flat.X + Origin.X, f.HeightAt(flat) / RiseFactor,
                                           (flat.Y + Origin.Y) / Squash);
                    // none on a road through the swamp (Stage3D.Roads)
                    if (RoadNear(new Vector2(foot.X, foot.Z)) < (Road.Half + 0.08f) * r)
                        continue;
                    float tall = rng.RandfRange(ReedTallLo, ReedTallHi) * m;
                    var lean = new Vector3(rng.RandfRange(-0.12f, 0.12f), 1.0f,
                                           rng.RandfRange(-0.12f, 0.12f)).Normalized();
                    float turn = rng.RandfRange(0.0f, Mathf.Pi);
                    Stalk(verts, colors, foot, lean * tall, 0.2f * m, turn, ReedFoot, ReedTip);
                    if (rng.Randf() < 0.25f)
                        Stalk(verts, colors, foot + lean * tall * 0.70f, lean * tall * 0.16f,
                              0.36f * m, turn, ReedHead, ReedHead, taper: 1.0f);
                }
            }
        }
        if (verts.Count == 0)
        {
            _reeds?.QueueFree();
            _reeds = null;
            return;
        }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts.ToArray();
        arrays[(int)Mesh.ArrayType.Color] = colors.ToArray();
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        if (_reeds is null)
        {
            _reeds = new MeshInstance3D
            {
                Name = "Reeds", CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                MaterialOverride = new StandardMaterial3D
                {
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    VertexColorUseAsAlbedo = true,
                    CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                },
            };
            AddChild(_reeds);
        }
        _reeds.Mesh = mesh;
    }

    /// <summary>Two crossed blades from <paramref name="foot"/> along
    /// <paramref name="up"/>, <paramref name="wide"/> across at the foot and
    /// <paramref name="taper"/> of that at the tip.</summary>
    private static void Stalk(List<Vector3> verts, List<Color> colors, Vector3 foot, Vector3 up,
                              float wide, float turn, Color low, Color high, float taper = 0.25f)
    {
        for (int k = 0; k < 2; k++)
        {
            float a = turn + k * Mathf.Pi * 0.5f;
            var side = new Vector3(Mathf.Cos(a), 0.0f, Mathf.Sin(a)) * (wide * 0.5f);
            Vector3 a0 = foot - side, a1 = foot + side;
            Vector3 b0 = foot + up - side * taper, b1 = foot + up + side * taper;
            verts.AddRange(new[] { a0, a1, b1, a0, b1, b0 });
            colors.AddRange(new[] { low, low, high, low, high, high });
        }
    }
}
