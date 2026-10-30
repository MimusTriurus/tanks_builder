using Godot;

namespace TankSpriteTest;

/// <summary>
/// The rocks' paint on the relief (docs/rocks-plan.md, stage 2): a rock with a
/// type of its own - a mesa, yardangs, a roche moutonnee - draws its layers,
/// cap, scrub, varnish, moss and striae per pixel, from the world point and
/// its height, because laid in the raster they blur away on the game's mesh
/// (<see cref="ReliefRocks"/>). A crag, and every rock nobody named, keeps the
/// one line it always had.
///
/// <b>One row a rock in a small table</b> (<see cref="RockTable"/>): its
/// palette and the numbers the shader needs, read by the rock's index the
/// mesh carries in <c>UV.x</c> (flat). A palette changes by rewriting the
/// table; the mesh does not move.
/// </summary>
public sealed partial class Stage3D
{
    /// <summary>The table's width: six layers, cap, lip, scree, and three
    /// rows of numbers.</summary>
    private const int RockColumns = 12;

    /// <summary>The renders' heights are in circumradii with a level a third
    /// of a hex (<c>ReliefRocks.LevelR</c>).</summary>
    private const float RockLevelR = 0.35f;

    /// <summary>
    /// The rocks' table, a row each: 0-5 the layers, 6 the cap (a
    /// moutonnee's moss), 7 the lip (its lichen), 8 the scree; 9 = type,
    /// layer thickness, moss, varnish; 10 = the rock's top, the layer scale
    /// (world units a circumradius of height), the gneiss's bands; 11 = the
    /// ice's way on the ground.
    /// </summary>
    private ImageTexture RockTable(Relief f)
    {
        int rows = Mathf.Max(1, f.Rocks.Count);
        Image img = Image.CreateEmpty(RockColumns, rows, false, Image.Format.Rgbaf);
        float up = Field.Lift / RiseFactor / RockLevelR;
        float ice = Mathf.DegToRad(RockIce);
        foreach (RockGroup g in f.Rocks)
        {
            RockPalette pal = g.Look.Palette;
            int row = g.Index;
            for (int k = 0; k < 6; k++)
                img.SetPixel(k, row, pal.Layers[k]);
            img.SetPixel(6, row, pal.Cap);
            img.SetPixel(7, row, pal.Lip);
            img.SetPixel(8, row, pal.Scree);
            img.SetPixel(9, row, new Color((int)g.Look.Type, pal.Thick * up, pal.Moss, pal.Varnish));
            img.SetPixel(10, row, new Color(g.Top / RiseFactor, up, pal.Bands, 0.0f));
            img.SetPixel(11, row, new Color(Mathf.Cos(ice), Mathf.Sin(ice), 0.0f, 0.0f));
        }
        return ImageTexture.CreateFromImage(img);
    }

    /// <summary>The ice's way the moutonnees' striae run along; the board's
    /// climate (<see cref="RockStyles.Ice"/>), set with the relief.</summary>
    public float RockIce { get; set; } = -30.0f;

    private const string RockPaint = @"
// The rocks (Stage3D.Rocks): which rock a point is, flat, and its own mask.
varying flat int rock_id;
varying float rock_aux;
uniform sampler2D rock_tab : filter_nearest, hint_default_black;

vec4 rock_row(int g, int k) { return texelFetch(rock_tab, ivec2(k, g), 0); }

float rock_hash(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }

float rock_noise(vec2 p) {
    vec2 i = floor(p);
    vec2 f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    return mix(mix(rock_hash(i), rock_hash(i + vec2(1.0, 0.0)), f.x),
               mix(rock_hash(i + vec2(0.0, 1.0)), rock_hash(i + vec2(1.0, 1.0)), f.x), f.y);
}

float rock_fbm(vec2 p) {
    return rock_noise(p) * 0.6 + rock_noise(p * 2.13 + 5.7) * 0.3 + rock_noise(p * 4.41 + 1.3) * 0.1;
}

// Worley: the nearest and second nearest of a jittered point a cell.
vec2 rock_cells(vec2 p) {
    vec2 i = floor(p);
    float f1 = 9.0, f2 = 9.0;
    for (int y = -1; y <= 1; y++)
        for (int x = -1; x <= 1; x++) {
            vec2 c = i + vec2(float(x), float(y));
            vec2 at = c + vec2(rock_hash(c), rock_hash(c + 17.31));
            float d = length(p - at);
            f2 = d < f1 ? f1 : min(f2, d);
            f1 = min(f1, d);
        }
    return vec2(f1, f2);
}

// A rock's colour here and how much of the ground it covers: share is its
// Relief.Cliff, aux its ReliefRocks mask. Lengths in circumradii (hex_r),
// heights in circumradii of height (row 10's y).
vec4 rock_look(int g, vec3 at, vec3 n, float grain, float share, float aux) {
    vec4 pa = rock_row(g, 9);
    vec4 pb = rock_row(g, 10);
    int ty = int(pa.x + 0.5);
    vec2 q = at.xz / hex_r;
    float hy = at.y / pb.y;
    float hl = length(n.xz);
    float face = smoothstep(0.55, 0.8, hl);
    // the layers, by height, waving a little across the rock
    vec3 c = rock_row(g, 0).rgb;
    if (pa.y > 0.0) {
        float t = (at.y + 0.025 * pb.y * (rock_noise(q / 0.9) * 2.0 - 1.0)) / pa.y;
        int pick = int(rock_hash(vec2(floor(t), 7.0 + float(g))) * 6.0);
        c = rock_row(g, clamp(pick, 0, 5)).rgb;
    }
    c *= 0.85 + 0.25 * grain;
    // desert varnish: dark streaks down the steep faces
    float streak = rock_noise(vec2(q.x * 6.0 + q.y * 3.7, hy * 0.9)) * 0.6
                 + rock_noise(vec2(q.x * 13.0 - q.y * 9.1, hy * 1.6)) * 0.4;
    c *= 1.0 - pa.w * face * smoothstep(0.5, 0.72, streak);
    float cover = smoothstep(0.5, 0.62, share);
    if (ty == 1) {
        // the mesa's cap: paler sand-swept top, a dark lip at the rim, scrub
        float lid = smoothstep(pb.x - 0.07 * pb.y, pb.x - 0.03 * pb.y, at.y) * (1.0 - face);
        vec3 cap = mix(rock_row(g, 6).rgb, rock_row(g, 7).rgb, 1.0 - smoothstep(0.55, 0.8, share));
        float bush = (1.0 - smoothstep(0.15, 0.28, rock_cells(q / 0.16).x))
                   * step(0.45, rock_noise(q / 0.5)) * smoothstep(0.75, 0.9, share) * clamp(pa.z / 0.35, 0.0, 1.0);
        cap = mix(cap, vec3(0.36, 0.38, 0.22), bush);
        c = mix(c, cap * (0.85 + 0.3 * grain), lid);
        vec3 scree = rock_row(g, 8).rgb * (0.8 + 0.4 * rock_noise(q / 0.05));
        c = mix(c, scree, aux * (1.0 - cover));
        cover = max(cover, aux);
    } else if (ty == 2) {
        // the yardangs' sand in the lee
        vec3 sand = rock_row(g, 8).rgb * (0.9 + 0.2 * grain);
        c = mix(c, sand, aux * (1.0 - cover));
        cover = max(cover, aux);
    } else if (ty == 3) {
        // the moutonnee: striae along the ice on its back, cracks between the
        // plucked blocks of its lee, moss and lichen on what is not a wall
        vec2 flow = rock_row(g, 11).xy;
        float along = dot(q, flow), across = dot(q, vec2(-flow.y, flow.x));
        if (pb.z > 0.0) {
            float fold = sin(across / 0.06 + 2.5 * (rock_noise(q / 0.6) * 2.0 - 1.0));
            c = mix(c, c * 0.7, pb.z * smoothstep(0.4, 0.9, fold));
        }
        float stria = step(abs(rock_noise(vec2(along / 1.5, across / 0.025)) * 2.0 - 1.0), 0.08) * (1.0 - aux);
        c *= 1.0 - 0.25 * stria;
        vec2 w = rock_cells(q / 0.2);
        c *= 1.0 - 0.5 * (1.0 - smoothstep(0.0, 0.1, w.y - w.x)) * aux;
        float flat_r = 1.0 - smoothstep(0.35, 0.7, hl);
        // the renders' fbm ran -1..1, this one 0..1
        float moss = flat_r * smoothstep(0.45 - pa.z, 0.7 - pa.z, rock_fbm(q / 0.35) * 2.0 - 1.0);
        c = mix(c, rock_row(g, 6).rgb * (0.8 + 0.4 * grain), moss);
        float spot = (1.0 - smoothstep(0.08, 0.15, rock_cells(q / 0.14).x)) * flat_r;
        c = mix(c, rock_row(g, 7).rgb, 0.5 * spot);
    }
    return vec4(c, cover);
}
";
}
