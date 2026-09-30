using System;
using System.Collections.Generic;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// Cel shading and an inked outline for a model the glTF loader made: every
/// <see cref="StandardMaterial3D"/> becomes a <see cref="ShaderMaterial"/> on
/// <see cref="CelShader"/> with <see cref="InkShader"/> as its next pass.
///
/// <b>The outline is an inverted hull, pushed out along a smoothed normal.</b>
/// The game variant's edges are hard - two-segment bevels, split normals - and a
/// shell pushed along split normals tears open at every such edge. So each
/// surface is rebuilt with <c>CUSTOM0</c> holding the normal averaged over all
/// the vertices on one spot (xyz) and how much ink the piece takes (w); the
/// cel pass keeps the mesh's own normals.
///
/// <b>The ink is board pixels, not screen pixels</b>: the sprites' ink is drawn
/// into the atlas and grows with the zoom, and a 3D tank beside them should
/// read the same - with <see cref="InkMinPx"/> screen pixels as the floor, so
/// zoomed out the line does not break up.
///
/// <b>Small pieces take no ink</b> (<see cref="InkFrom"/>, <see cref="InkFull"/>):
/// a rivet, a bolt or a handle with a shell of its own is a black dot, and a
/// hull full of rivets reads as dirt. The painted rim the bake gives a rivet
/// (<c>K.rivets</c>, the <c>Ink</c> rim) is what draws it.
/// </summary>
public static class Toon
{
    /// <summary>How wide the outline is, board px (world units).</summary>
    public const float InkWidth = 1.1f;
    /// <summary>The outline is never thinner than this many screen px.</summary>
    public const float InkMinPx = 1.0f;
    /// <summary>How dark the ink is against the paint under it: the sprites'
    /// line is the paint's own colour, darkened, not black.</summary>
    public const float InkDark = 0.28f;
    /// <summary>A piece - vertices joined by triangles or by sharing a spot -
    /// whose box diagonal is under this share of the model's length takes no
    /// ink; from <see cref="InkFull"/> up it takes all of it.</summary>
    public const float InkFrom = 0.035f, InkFull = 0.07f;

    /// <summary>Faces meeting at more than this, degrees, meet at a crease for
    /// the light (<see cref="Facet"/>); under it they shade as one surface.</summary>
    public const float CreaseDeg = 35.0f;

    /// <summary>
    /// The light as three tones, in the paint's own colour: <c>shade</c> is
    /// what a face turned from the sun keeps (the scene's ambient is off here,
    /// this stands for it, occlusion and all), the middle tone adds
    /// <c>mid</c> of the sun above <c>edge_dark</c> of its cosine, full sun
    /// comes in above <c>edge_lit</c>; <c>soft</c> is how wide each step is.
    /// Shadow is folded into the same cosine, so a cast shadow steps down to
    /// the shade tone as a turned-away face does.
    ///
    /// A light that is not the sun (<see cref="CelBurn"/>'s fire) steps the
    /// same way and also paints its colour on over the paint (<c>glow_paint</c>),
    /// since through a green albedo an orange light only greens.
    ///
    /// Its uniforms and its <c>light()</c>, shared by everything the cel look
    /// is on: the model (<see cref="CelShader"/>) and the 3D effects beside it
    /// (<see cref="CelBurn"/>'s smoke), so one sun steps them the same.
    /// </summary>
    public const string RampCode = @"
uniform vec3 shade = vec3(0.36, 0.38, 0.44);
uniform float edge_dark = 0.12;
uniform float edge_lit = 0.7;
uniform float mid = 0.5;
uniform float soft = 0.035;
uniform float sun = 1.0;
uniform float glow_paint = 0.45;
// Whether the sun's cast shadow steps this surface down. Off for a tree's
// leaves and cores (Tree3DBench): a crown shadows itself leaf by leaf, and on
// the ramp's step every leaf's shadow on the leaves under it was a dark
// triangle - flecks over the lit side, more or fewer as the shadow map's
// texel fell at the tree's depth. The crown's own normals shade it.
uniform bool sun_shadow = true;
// How much of the paint is soot (the fire's scorch): a local light lays no
// colour of its own on it - painted over, a scorch under its own fire went
// orange-olive and the dark base the flame stands on was gone while it burned.
varying float sooted;
void light() {
    float t = clamp(dot(NORMAL, LIGHT), 0.0, 1.0)
            * (LIGHT_IS_DIRECTIONAL && !sun_shadow ? 1.0 : ATTENUATION);
    float v = mid * smoothstep(edge_dark - soft, edge_dark + soft, t);
    v = mix(v, 1.0, smoothstep(edge_lit - soft, edge_lit + soft, t));
    DIFFUSE_LIGHT += v * sun * LIGHT_COLOR / PI;
    // A local light - a fire - also lays its own colour over the paint, as a
    // painted glow does: through the albedo alone, orange on green paint
    // comes back as a little more green.
    if (!LIGHT_IS_DIRECTIONAL)
        SPECULAR_LIGHT += v * glow_paint * (1.0 - sooted) * LIGHT_COLOR / PI;
}
";

    /// <summary>The model's surfaces on <see cref="RampCode"/>: the glTF's
    /// paint and its occlusion (ORM's R).</summary>
    public static readonly Shader CelShader = new() { Code = CelCode(0) };

    /// <summary><see cref="CelShader"/> marking its pixels
    /// <see cref="TurretStencil"/> - the turret's, see <see cref="MarkTurret"/>.</summary>
    public static readonly Shader CelTurretShader = new() { Code = CelCode(TurretStencil) };

    /// <summary><see cref="CelShader"/> for foliage: both sides drawn, and the
    /// back side lit by the file's normal rather than its flip - a leaf is an
    /// open sheet whose normal is its puff's (<see cref="Dress"/>).</summary>
    public static readonly Shader CelLeafShader = new() { Code = CelCode(0, both: true) };

    private static readonly Dictionary<(int, bool), Shader> Marked = new();

    /// <summary>The cel shader that leaves <paramref name="stencil"/> on what it
    /// draws: 0 is <see cref="CelShader"/> or <see cref="CelLeafShader"/>, any
    /// other made once and kept.</summary>
    private static Shader CelMarked(int stencil, bool both)
    {
        if (stencil == 0)
            return both ? CelLeafShader : CelShader;
        if (!Marked.TryGetValue((stencil, both), out Shader? shader))
            Marked[(stencil, both)] = shader = new Shader { Code = CelCode(stencil, both) };
        return shader;
    }

    /// <summary>
    /// The stencil a turret's visible pixels carry, so the fire can be drawn
    /// over a turret thrown onto the grilles and over nothing else
    /// (<see cref="CelBurn"/>). Every other surface of the model, and the
    /// smoke, writes 0: the mark is left only by what is seen at a pixel,
    /// whichever of them was drawn last.
    /// </summary>
    public const int TurretStencil = 7;

    /// <summary>Hit marks one cel material carries at once (the shader's
    /// arrays) - see <see cref="CelHit"/>.</summary>
    public const int MaxMarks = 16;

    /// <summary>Value noise in three dimensions: the scorch here, the fire and
    /// the smoke in <see cref="CelBurn"/>.</summary>
    public const string NoiseCode = @"
float hash3(vec3 p) {
    p = fract(p * 0.3183099 + vec3(0.71, 0.113, 0.419));
    p *= 17.0;
    return fract(p.x * p.y * p.z * (p.x + p.y + p.z));
}
float noise3(vec3 x) {
    vec3 i = floor(x);
    vec3 f = fract(x);
    f = f * f * (3.0 - 2.0 * f);
    return mix(mix(mix(hash3(i), hash3(i + vec3(1, 0, 0)), f.x),
                   mix(hash3(i + vec3(0, 1, 0)), hash3(i + vec3(1, 1, 0)), f.x), f.y),
               mix(mix(hash3(i + vec3(0, 0, 1)), hash3(i + vec3(1, 0, 1)), f.x),
                   mix(hash3(i + vec3(0, 1, 1)), hash3(i + vec3(1, 1, 1)), f.x), f.y), f.z);
}
";

    /// <summary>
    /// A tree model's burn contract (<c>tree_gen.py</c>, <c>tree.json</c>
    /// <c>model.burn</c>): <c>UV2.x</c> - glTF's <c>TEXCOORD_1</c> - is when each
    /// piece goes. <c>burn_role</c> 0 is everything that is not a tree (every
    /// tank): nothing here runs. 1 - leaves and puff cores - is gone once
    /// <c>burn</c> passes it, and chars on the way over the last
    /// <c>burn_window</c> of it; 2 - twigs - shows once <c>burn</c> passes it,
    /// charred; 3 - bark - chars with <c>charred</c>, the paint's own clock
    /// (<c>Wildfire.Coat.Char</c>), and never goes. Its uniforms and a
    /// <c>burn_gone()</c> the cel pass, the ink and the crown's mask
    /// (<c>Tree3DBench</c>) all ask, so the three lose a piece on one frame.
    ///
    /// <b>A puff's core is eaten, not charred</b> (<c>burn_eat</c>): its
    /// threshold comes after its leaves', so for a while it stands bare, and
    /// charred whole it was a black ball hung in the burnt crown. Over its
    /// window it is cut away by a noise in the world instead - holes that open
    /// and grow, their edge glowing - the hard cut the smoke goes by. In the
    /// world, so the ink and the mask cut the same holes and the line follows
    /// them in. Needs <see cref="NoiseCode"/> before it.
    /// </summary>
    public const string BurnCode = @"
uniform int burn_role = 0;
uniform float burn = 0.0;
uniform float burn_window = 0.12;
uniform float charred = 0.0;
uniform bool burn_eat = false;
uniform float burn_grain = 5.0;
// How far into its own going a piece is: 0 whole .. 1 gone (role 1).
float burn_k(float at) {
    return clamp((burn - at) / max(burn_window, 1e-4) + 1.0, 0.0, 1.0);
}
// What the eating has left: under 0 the piece is gone here.
float burn_left(float at, vec3 p) {
    return noise3(p / burn_grain) * 0.8 + noise3(p / (burn_grain * 0.35) + vec3(3.1)) * 0.2
           - burn_k(at);
}
bool burn_gone(float at, vec3 p) {
    return (burn_role == 1 && (burn > at || (burn_eat && burn_left(at, p) < 0.0)))
        || (burn_role == 2 && burn <= at);
}
";

    /// <summary>
    /// A standing tree model in the wind, the step before <see cref="FallCode"/>:
    /// in the model's metres. The crown leans downwind (<c>wind_dir</c>, the
    /// model's x, z) by <c>wind_lean</c> of its height at the top, less down
    /// the tree - <c>w = s^1.5</c>, <c>s</c> from <c>wind_y0</c> to <c>wind_h</c>
    /// (the fall's bend band), so the trunk's foot stands still. The lean is
    /// the sprite wood's (<c>Grove</c>: its drift over its height), worked out
    /// on the CPU per tree and handed in. On top, what a flat picture cannot
    /// do: <c>wind_billow</c> m of a slow noise, so the puffs move a little
    /// each its own way, and <c>wind_flutter</c> m of quick flutter, set on the
    /// leaves alone. All zero - every tank - nothing here runs. The cel pass,
    /// the ink and the crown's mask call it alike, so the line keeps to the
    /// crown. Needs <see cref="NoiseCode"/> before it.
    /// </summary>
    public const string WindCode = @"
uniform vec2 wind_dir = vec2(1.0, 0.0);
uniform float wind_lean = 0.0;
uniform float wind_billow = 0.0;
uniform float wind_flutter = 0.0;
uniform float wind_time = 0.0;
uniform float wind_y0 = 2.2;
uniform float wind_h = 7.4;
void wind_pose(inout vec3 v) {
    if (wind_lean == 0.0 && wind_billow == 0.0 && wind_flutter == 0.0) return;
    float s = clamp((v.y - wind_y0) / max(wind_h - wind_y0, 1e-3), 0.0, 1.0);
    float w = s * sqrt(s);
    vec3 o = vec3(wind_dir.x, 0.0, wind_dir.y) * wind_lean * wind_h * w;
    if (wind_billow > 0.0) {
        // a slow noise through the crown, a few metres long: puffs drift a
        // little each its own way, a leaf moves as one
        vec3 q = v * 0.45 + vec3(wind_time * 0.7, wind_time * 0.2, wind_time * 0.35);
        o += (vec3(noise3(q), noise3(q + vec3(5.2)), noise3(q + vec3(9.7))) - 0.5) * 2.0 * wind_billow * w;
    }
    if (wind_flutter > 0.0) {
        // the leaves only: quick and small, its phase changing a little from
        // leaf to leaf, not across one
        float ph = dot(v, vec3(0.9, 0.7, 0.8));
        o += vec3(sin(wind_time * 7.0 + ph), 0.5 * sin(wind_time * 9.0 + ph * 1.3),
                  cos(wind_time * 6.0 + ph * 0.7)) * wind_flutter * w;
    }
    v += o;
}
";

    /// <summary>
    /// A tree model going over (<c>tree.json</c> <c>model.fall</c>,
    /// <c>tree_gen.py</c>'s <c>_pose</c> and <c>_crush</c>): in the model's own
    /// metres, glTF Y up, the foot at the origin. The whole tree turns about a
    /// hinge on the ground, <c>fall_hinge</c> out toward <c>fall_dir</c> (the
    /// model's x, z) and across it, by the trunk's angle plus the crown's
    /// spring weighted by the bend - <c>w = clamp((y - y0) / (H - y0))^2</c>, the
    /// formula the pipeline writes to <c>TEXCOORD_2</c> and worked out here off
    /// the vertex's own height, since glTF's third UV comes in as
    /// <c>CUSTOM0</c> and the ink's normal is written over it. Then what went
    /// under the ground is crushed into a mat on it, soft rather than cut:
    /// <c>y' = m ln(1 + e^(y / m))</c>, <c>m = fall_mat * fall_down</c>, never
    /// under <c>fall_lift</c> - but not what stood under the ground from the
    /// start, the root plate: what goes further under stays under, hidden by
    /// the ground, and crushed it came up as a mat of earth over the lying
    /// crown. The normal turns with it. <c>fall_on</c> off -
    /// every tank - nothing here runs. The cel pass, the ink and the crown's
    /// mask (<c>Tree3DBench</c>) all call <c>fall_pose</c>, so the three and the
    /// shadow go over as one.
    /// </summary>
    public const string FallCode = @"
uniform bool fall_on = false;
uniform vec2 fall_dir = vec2(1.0, 0.0);
uniform float fall_hinge = 0.44;
uniform float fall_trunk = 0.0;
uniform float fall_crown = 0.0;
uniform float fall_down = 0.0;
uniform float fall_y0 = 2.2;
uniform float fall_h = 7.4;
uniform float fall_mat = 0.08;
uniform float fall_lift = 0.01;
vec3 fall_turn(vec3 r, vec3 k, float th) {
    float c = cos(th), s = sin(th);
    return r * c + cross(k, r) * s + k * dot(k, r) * (1.0 - c);
}
void fall_pose(inout vec3 v, inout vec3 n) {
    if (!fall_on) return;
    bool under = v.y < 0.0;
    float w = clamp((v.y - fall_y0) / max(fall_h - fall_y0, 1e-3), 0.0, 1.0);
    w *= w;
    vec3 d = normalize(vec3(fall_dir.x, 0.0, fall_dir.y));
    vec3 k = cross(vec3(0.0, 1.0, 0.0), d);
    vec3 piv = d * fall_hinge;
    float th = fall_trunk + fall_crown * w;
    v = piv + fall_turn(v - piv, k, th);
    n = fall_turn(n, k, th);
    float m = fall_mat * max(fall_down, 1e-3);
    if (!under && v.y <= 4.0 * m)
        v.y = max(m * log(1.0 + exp(v.y / m)), fall_lift * min(fall_down * 4.0, 1.0));
}
";

    /// <summary>
    /// The model's paint on the ramp, and the fire's scorch in it
    /// (<see cref="CelBurn"/> drives the numbers): round each port, within
    /// <c>scorch_r</c> world px grown by <c>scorch</c>, the paint goes to char,
    /// its edge torn by a noise and cut hard as the tones are; in its outer
    /// part, embers - specks of a climbing noise, lit by <c>ember</c>. Measured in the world, so it lies on the deck round the
    /// grille and on a turret wall standing next to it alike: the flame no
    /// longer grows out of clean green paint.
    /// </summary>
    private static string CelCode(int stencil, bool both = false) => @"
shader_type spatial;
render_mode " + (both ? "cull_disabled" : "cull_back") + @", specular_disabled, ambient_light_disabled;
stencil_mode write, compare_always, " + stencil + @";
uniform vec4 albedo : source_color = vec4(1.0);
uniform sampler2D albedo_tex : source_color, hint_default_white, filter_linear_mipmap_anisotropic;
uniform sampler2D orm_tex : hint_default_white, filter_linear_mipmap;
uniform bool has_orm = false;
uniform float ao_light = 0.5;
uniform vec3 scorch_at[4];
uniform int scorch_n = 0;
uniform float scorch_r = 20.0;
uniform float scorch = 0.0;
uniform float ember = 0.0;
uniform float scorch_time = 0.0;
uniform vec3 char_tone : source_color = vec3(0.07, 0.065, 0.06);
uniform float char_cover = 0.9;
uniform vec3 ember_tone : source_color = vec3(1.0, 0.36, 0.06);
// Hit marks (CelHit drives them): each is a spot on a plate in the world -
// its middle and radius, the way a glancing round went on (and the kind:
// 0 a gouge, 1 a hole, 2 an HE splash), the plate's normal and when it came.
uniform vec4 mark_at[" + MaxMarks + @"];
uniform vec4 mark_dir[" + MaxMarks + @"];
uniform vec4 mark_nrm[" + MaxMarks + @"];
uniform int mark_count = 0;
uniform float mark_now = 0.0;
uniform vec3 steel : source_color = vec3(0.50, 0.52, 0.56);
uniform vec3 hole_tone : source_color = vec3(0.035, 0.03, 0.028);
uniform vec3 mark_soot : source_color = vec3(0.17, 0.16, 0.15);
uniform vec3 hole_wall : source_color = vec3(0.16, 0.15, 0.15);
// Leaves burning: the last of a leaf's window glows before it goes (BurnCode).
uniform bool burn_ember = false;
// The waterline (Tank3DBench.Water drives it): foam where the water's surface,
// world height water_y, cuts the armour - water_on is how much of it there is
// (nought out of the water and gone as the hull drowns), water_pace how fast
// the hull goes through it, which thickens the foam.
uniform float water_on = 0.0;
uniform float water_y = 0.0;
uniform float water_pace = 0.0;
uniform float water_band = 3.0;
uniform float water_push = 3.5;
uniform float water_wet = 4.0;
uniform vec3 water_foam : source_color = vec3(0.97, 1.0, 1.0);
uniform vec3 water_edge : source_color = vec3(0.62, 0.83, 0.87);
varying vec3 world;
" + NoiseCode + RampCode + BurnCode + WindCode + FallCode + @"
void vertex() {
    vec3 n = NORMAL;
    wind_pose(VERTEX);
    fall_pose(VERTEX, n);
    NORMAL = n;
    world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}
void fragment() {
    if (burn_role != 0 && burn_gone(UV2.x, world)) {
        discard;
    }
    " + (both ? "if (!FRONT_FACING) { NORMAL = -NORMAL; }" : "") + @"
    vec4 c = texture(albedo_tex, UV) * albedo;
    float ao = has_orm ? texture(orm_tex, UV).r : 1.0;
    vec3 glow = vec3(0.0);
    sooted = 0.0;
    if (burn_role != 0) {
        // How far into its own going this piece is: a leaf or a core over its
        // window, a twig charred from the moment it shows, bark by the clock.
        float k = burn_role == 1 ? burn_k(UV2.x) : burn_role == 2 ? 1.0 : charred;
        if (burn_eat) {
            // Eaten: the core keeps its green, charred only in a band at the
            // holes' edge and glowing at the very edge of it.
            float left = burn_left(UV2.x, world);
            float edge = 1.0 - step(0.10, left);
            c.rgb = mix(c.rgb, char_tone, char_cover * edge);
            sooted = edge;
            glow = ember_tone * (1.0 - step(0.035, left)) * step(0.001, k) * 1.4;
        } else {
            c.rgb = mix(c.rgb, char_tone, char_cover * k);
            sooted = k;
            if (burn_ember && burn_role == 1)
                glow = ember_tone * smoothstep(0.55, 0.9, k) * 1.4;
        }
    }
    if (scorch > 0.0 && scorch_n > 0) {
        float near = 1e9;
        for (int i = 0; i < 4; i++) {
            if (i >= scorch_n) break;
            near = min(near, distance(world, scorch_at[i]));
        }
        // From nothing and back to it: the scorch grows in with the fire and
        // shrinks away as it goes out.
        float reach = scorch_r * sqrt(scorch);
        // Torn at the edge, but not into islands: a coarse noise under a third
        // of the reach bends it, a fine one small enough to leave no islands
        // makes it jagged - with the coarse one alone it was a clean oval.
        float torn = (noise3(world / (scorch_r * 0.45)) - 0.5) * 0.20
                   + (noise3(world / (scorch_r * 0.10) + vec3(7.3)) - 0.5) * 0.22;
        float d = near / reach + torn;
        // The char is a colour of its own laid over the paint, not the paint
        // darkened: darkened, green went olive-brown and read as camouflage.
        // One tone only - a grey ring of soot round it read as a stain.
        float burnt = 1.0 - step(1.0, d);
        c.rgb = mix(c.rgb, char_tone, char_cover * burnt);
        sooted = burnt;
        // Embers in the char round the fire's foot - the middle is under the
        // flame anyway.
        float speck = step(0.72, noise3(world / (scorch_r * 0.05) + vec3(0.0, -scorch_time * 1.3, 0.0)));
        float ring = step(0.40, d) * burnt;
        glow = ember_tone * speck * ring * ember * 1.3;
    }
    // The plate's own facing in the world, for the marks: the lighting
    // normals are per face (Facet), so it is flat across a plate.
    vec3 face_n = normalize((INV_VIEW_MATRIX * vec4(NORMAL, 0.0)).xyz);
    for (int i = 0; i < " + MaxMarks + @"; i++) {
        if (i >= mark_count) break;
        vec3 d = world - mark_at[i].xyz;
        float r = mark_at[i].w;
        if (dot(d, d) > 16.0 * r * r) continue;
        vec3 n = mark_nrm[i].xyz;
        float h = dot(d, n);
        // Onto a neighbouring plate turned from this one by up to about 65
        // degrees, but not round a sharper corner or onto the plate behind:
        // held to its own plate, a mark by a seam was cut square along it.
        if (dot(face_n, n) < 0.42 || abs(h) > r) continue;
        vec3 q = d - h * n;
        // The way along the plate the round went on; its length is how much
        // it glanced (1 along the plate, 0 square on).
        float glance = length(mark_dir[i].xyz);
        vec3 way = mark_dir[i].xyz / max(glance, 1e-4);
        float kind = mark_dir[i].w;
        float a = dot(q, way);
        float b = length(q - a * way);
        float age = mark_now - mark_nrm[i].w;
        // A hole's rim stays hot longer than a scrape: more metal was torn.
        float heat = exp(-age / (kind > 0.5 && kind < 1.5 ? 1.5 : 0.9));
        // Out, not a red that never quite goes: a cooled hole stayed brown.
        heat *= step(0.08, heat);
        float torn = (noise3(world / (r * 0.45) + vec3(float(i) * 3.7)) - 0.5) * 0.30
                   + (noise3(world / (r * 0.16) + vec3(float(i) * 1.9)) - 0.5) * 0.18;
        float e;
        float bare;
        float hole = -1.0;
        if (kind < 0.5) {
            // A gouge: a scrape from the point of impact on the way the round
            // glanced, short behind it, long ahead.
            // Square on it is a round dent, glancing a long furrow.
            float aa = a < 0.0 ? a / (r * mix(0.8, 0.6, glance)) : a / (r * mix(0.8, 2.8, glance));
            e = length(vec2(aa, b / (r * mix(0.8, 0.55, glance)))) + torn * 0.6;
            bare = 0.62;
        } else if (kind < 1.5) {
            // A hole: black in the middle with the far inside wall showing at
            // its top, a torn rim of bare steel petals bent in, the paint
            // burnt round it.
            vec3 t1 = normalize(way);
            vec3 t2 = cross(n, t1);
            float ang = atan(dot(q, t2), dot(q, t1));
            float petals = pow(abs(cos(ang * 3.5 + float(i) * 2.1)), 3.0);
            e = length(q) / r + torn * 0.5;
            bare = 0.74 + 0.12 * petals;
            hole = 0.58 - 0.14 * petals;
        } else {
            // HE on armour: a round burn with a lumpy, torn edge, a bare
            // pitted middle. Not a star: its long rays read as a sticker.
            vec3 t1 = normalize(way);
            vec3 t2 = cross(n, t1);
            float ang = atan(dot(q, t2), dot(q, t1));
            float lumps = noise3(vec3(cos(ang) * 1.6, sin(ang) * 1.6, float(i) * 2.3)) - 0.5;
            e = length(q) / r * (1.0 - 0.28 * lumps) + torn * 0.7;
            float pit = step(0.62, noise3(world / (r * 0.18) + vec3(float(i) * 5.1)));
            bare = 0.25 + 0.30 * pit;
        }
        if (e >= 1.0) continue;
        c.rgb = mark_soot;
        sooted = 1.0;
        if (e < bare) {
            c.rgb = steel;
            // Fresh, the scraped metal is hot.
            glow += ember_tone * heat * (kind < 1.5 ? 1.2 : 0.6);
        }
        if (e < hole) {
            c.rgb = hole_tone;
            // The inside wall across from the eye's side: the upper part of
            // the hole as the plate stands, a band a little lighter.
            vec3 down = vec3(0.0, -1.0, 0.0) - n * dot(vec3(0.0, -1.0, 0.0), n);
            float lo = dot(q, normalize(down + vec3(1e-4))) / r;
            if (e > hole * 0.55 && lo < -hole * 0.25)
                c.rgb = hole_wall;
            // Fire inside, flickering and dying down.
            float fire = exp(-age / 1.0) * (0.6 + 0.4 * sin(mark_now * 23.0 + float(i)));
            fire *= step(0.06, exp(-age / 1.0));
            if (e < hole * 0.6)
                glow += ember_tone * fire * 0.9;
        }
    }
    // The waterline: a band of foam at the surface's height on the armour,
    // wavering with the water and torn along it, white with a pale edge under
    // it, and the paint a little darker - wet - just over it. Upright plates
    // only: laid by height, a deck at the water's level went white entire as
    // the surface passed it. What is under the line the pond's own surface
    // tints by its depth; this is only the line.
    if (water_on > 0.001) {
        float wob = (noise3(vec3(world.xz * 0.09, TIME * 0.7)) - 0.5) * 2.4
                  + (noise3(vec3(world.xz * 0.27, TIME * 1.7 + 3.0)) - 0.5) * 1.3;
        float h = world.y - water_y - wob;
        float upright = 1.0 - smoothstep(0.55, 0.9, abs(face_n.y));
        float w = (water_band + water_pace * water_push) * upright * water_on;
        float torn = step(0.26 - 0.16 * water_pace, noise3(vec3(world.xz * 0.13, TIME * 0.45 + 9.0)));
        float foam = step(0.6, w) * step(-0.45 * w, h) * step(h, w) * torn;
        float wet = step(0.6, w) * step(w, h) * step(h, w + water_wet * upright) * water_on;
        c.rgb *= 1.0 - 0.18 * wet;
        if (foam > 0.5) {
            c.rgb = h < 0.15 * w ? water_edge : water_foam;
            glow += c.rgb * 0.45;
            sooted = 1.0;
        }
    }
    ALBEDO = c.rgb;
    EMISSION = c.rgb * shade * ao + glow;
    AO = ao;
    AO_LIGHT_AFFECT = ao_light;
}
";

    /// <summary>
    /// The shell: back faces only, every vertex moved out in view space along
    /// the smoothed normal and back from the eye by the same, so where it is
    /// not the silhouette it stays under the surface it wraps.
    /// </summary>
    public static readonly Shader InkShader = new() { Code = InkCode(0) };

    /// <summary><see cref="InkShader"/> with the turret's mark: the fire over
    /// a thrown turret covers its outline too, not only its paint.</summary>
    public static readonly Shader InkTurretShader = new() { Code = InkCode(TurretStencil) };

    private static string InkCode(int stencil) => @"
shader_type spatial;
render_mode unshaded, cull_front, skip_vertex_transform, shadows_disabled, fog_disabled;
stencil_mode write, compare_always, " + stencil + @";
uniform vec4 albedo : source_color = vec4(1.0);
uniform sampler2D albedo_tex : source_color, hint_default_white, filter_linear_mipmap;
uniform float width = 1.0;
uniform float min_px = 1.0;
uniform float dark = 0.3;
varying vec3 world;
" + NoiseCode + BurnCode + WindCode + FallCode + @"
void vertex() {
    vec3 v = VERTEX;
    vec3 cn = CUSTOM0.xyz;
    wind_pose(v);
    fall_pose(v, cn);
    world = (MODEL_MATRIX * vec4(v, 1.0)).xyz;
    VERTEX = (MODELVIEW_MATRIX * vec4(v, 1.0)).xyz;
    vec3 n = normalize(mat3(MODELVIEW_MATRIX) * cn);
    // One screen px in view units, for an orthographic eye.
    float px = 2.0 / (PROJECTION_MATRIX[1][1] * VIEWPORT_SIZE.y);
    float w = max(width, min_px * px) * CUSTOM0.w;
    VERTEX.xy += n.xy * w;
    VERTEX.z -= w;
    NORMAL = n;
}
void fragment() {
    if (burn_role != 0 && burn_gone(UV2.x, world)) {
        discard;
    }
    ALBEDO = texture(albedo_tex, UV).rgb * albedo.rgb * dark;
}
";

    /// <summary>Dress every mesh under <paramref name="scene"/>: its surfaces
    /// rebuilt with the ink's normal, its materials swapped for cel ones, one
    /// per source material. Returns the cel materials.
    ///
    /// <paramref name="keep"/> names the surfaces whose light normals are the
    /// file's own and not <see cref="Facet"/>'s: a tree's leaves carry their
    /// puff's normal (<c>tree_gen._puff_normals</c>), and made again from each
    /// leaf's plane they lit the crown in flecks. Null - every tank - facets
    /// everything, as before.
    ///
    /// <paramref name="foliage"/> names open sheets - leaves: they keep their
    /// normals too, are drawn from both sides (<see cref="CelLeafShader"/>) and
    /// get no ink. The ink is an inside-out hull and needs a closed volume; on
    /// a sheet turned away from the eye it drew the whole leaf in ink, and half
    /// a crown's leaves came out as dark blots.
    ///
    /// <paramref name="stencil"/> is left on every pixel the model's own
    /// surfaces draw (not its ink): what a pass that belongs to this kind of
    /// model alone reads - a tree's crown outline (<c>Tree3DBench</c>). 0, the
    /// tanks', is what every other surface writes.</summary>
    public static List<ShaderMaterial> Dress(Node scene, Func<Material, bool>? keep = null,
                                             Func<Material, bool>? foliage = null, int stencil = 0)
    {
        var made = new Dictionary<Material, ShaderMaterial>();
        var meshes = new List<MeshInstance3D>();
        Collect(scene, meshes);
        float length = 0.0f;
        foreach (MeshInstance3D m in meshes)
            length = Mathf.Max(length, m.Mesh.GetAabb().Size.Length());
        foreach (MeshInstance3D m in meshes)
            m.Mesh = Rebuild(m.Mesh, length, made, keep, foliage, stencil);
        return new List<ShaderMaterial>(made.Values);
    }

    /// <summary>
    /// Put every cel material under <paramref name="turret"/> on the turret's
    /// shaders - the same numbers, the stencil <see cref="TurretStencil"/> -
    /// one copy per material, and list the copies with the others.
    /// </summary>
    public static void MarkTurret(Node turret, List<ShaderMaterial> cel)
    {
        var made = new Dictionary<ShaderMaterial, ShaderMaterial>();
        var meshes = new List<MeshInstance3D>();
        Collect(turret, meshes);
        foreach (MeshInstance3D m in meshes)
        {
            if (m.Mesh is not ArrayMesh mesh)
                continue;
            for (int s = 0; s < mesh.GetSurfaceCount(); s++)
            {
                if (mesh.SurfaceGetMaterial(s) is not ShaderMaterial src || src.Shader != CelShader)
                    continue;
                if (!made.TryGetValue(src, out ShaderMaterial? copy))
                {
                    copy = Copy(src, CelTurretShader);
                    if (src.NextPass is ShaderMaterial ink)
                        copy.NextPass = Copy(ink, InkTurretShader);
                    made[src] = copy;
                    cel.Add(copy);
                }
                mesh.SurfaceSetMaterial(s, copy);
            }
        }
    }

    private static ShaderMaterial Copy(ShaderMaterial src, Shader to)
    {
        var copy = new ShaderMaterial { Shader = to };
        foreach (Godot.Collections.Dictionary u in to.GetShaderUniformList())
        {
            StringName name = u["name"].AsStringName();
            // Disposed here: a variant holding a texture left to the finalizer
            // is let go after the renderer has shut down, and the exit prints
            // the texture as leaked.
            using Variant value = src.GetShaderParameter(name);
            copy.SetShaderParameter(name, value);
        }
        return copy;
    }

    /// <summary>Set the paint of a cel material and of its ink together.</summary>
    public static void Paint(ShaderMaterial cel, Color albedo)
    {
        cel.SetShaderParameter("albedo", albedo);
        (cel.NextPass as ShaderMaterial)?.SetShaderParameter("albedo", albedo);
    }

    public static Color PaintOf(ShaderMaterial cel) => cel.GetShaderParameter("albedo").AsColor();

    private static void Collect(Node node, List<MeshInstance3D> into)
    {
        if (node is MeshInstance3D m && m.Mesh is not null)
            into.Add(m);
        foreach (Node child in node.GetChildren())
            Collect(child, into);
    }

    private static ArrayMesh Rebuild(Mesh src, float length, Dictionary<Material, ShaderMaterial> made,
                                     Func<Material, bool>? keep, Func<Material, bool>? foliage,
                                     int stencil)
    {
        var dst = new ArrayMesh();
        const Mesh.ArrayFormat custom = (Mesh.ArrayFormat)((long)Mesh.ArrayCustomFormat.RgbaFloat
                                                           << (int)Mesh.ArrayFormat.FormatCustom0Shift);
        for (int s = 0; s < src.GetSurfaceCount(); s++)
        {
            Material? mat = src.SurfaceGetMaterial(s);
            bool leaf = mat is not null && foliage is not null && foliage(mat);
            Godot.Collections.Array arrays = leaf || (mat is not null && keep is not null && keep(mat))
                ? src.SurfaceGetArrays(s)
                : Facet(src.SurfaceGetArrays(s));
            arrays[(int)Mesh.ArrayType.Custom0] = InkNormals(arrays, length);
            dst.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays, null, null, custom);
            if (mat is not null)
                dst.SurfaceSetMaterial(s, CelFor(mat, made, leaf, stencil));
        }
        return dst;
    }

    private static ShaderMaterial CelFor(Material mat, Dictionary<Material, ShaderMaterial> made,
                                         bool leaf = false, int stencil = 0)
    {
        if (made.TryGetValue(mat, out ShaderMaterial? cel))
            return cel;
        var std = mat as BaseMaterial3D;
        Color albedo = std?.AlbedoColor ?? Colors.White;
        Texture2D? tex = std?.AlbedoTexture;
        Texture2D? orm = std?.AOTexture;
        var ink = new ShaderMaterial { Shader = InkShader };
        ink.SetShaderParameter("albedo", albedo);
        ink.SetShaderParameter("width", InkWidth);
        ink.SetShaderParameter("min_px", InkMinPx);
        ink.SetShaderParameter("dark", InkDark);
        cel = leaf ? new ShaderMaterial { Shader = CelMarked(stencil, true) }
                   : new ShaderMaterial { Shader = CelMarked(stencil, false), NextPass = ink };
        cel.SetShaderParameter("albedo", albedo);
        if (tex is not null)
        {
            cel.SetShaderParameter("albedo_tex", tex);
            ink.SetShaderParameter("albedo_tex", tex);
        }
        if (orm is not null && std!.AOEnabled)
        {
            cel.SetShaderParameter("orm_tex", orm);
            cel.SetShaderParameter("has_orm", true);
        }
        made[mat] = cel;
        return cel;
    }

    /// <summary>
    /// The light's normals, made again from the faces: each corner takes the
    /// faces round its spot that turn less than <see cref="CreaseDeg"/> from
    /// its own, weighted by their area, and a vertex whose corners come out
    /// different is split.
    ///
    /// <b>The glTF's normals are smooth across the bevels</b>: over the whole
    /// of MTR's skirt panels they lean 8-20 deg off the face toward the
    /// two-segment bevels round them, so a flat plate shades as a slope drawn
    /// across its triangles. The cel ramp's step turns that into stripes where
    /// the plate stands near a threshold - diagonal blots across the skirts,
    /// with or without shadow and occlusion. Made again, a plate is one
    /// normal and steps as one, and a loft's turret or a tube stays round.
    ///
    /// <b>By area, not by angle</b>: a two-segment bevel on a square edge
    /// turns its first face 22.5 deg off the plate, inside any crease angle
    /// that keeps a loft round, and weighted by its corner angle a strip a few
    /// mm wide bent a plate's corner as much as the plate held it. By area the
    /// strip is what it is beside the plate.
    /// </summary>
    private static Godot.Collections.Array Facet(Godot.Collections.Array arrays)
    {
        Vector3[] pos = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        if (arrays[(int)Mesh.ArrayType.Index].VariantType == Variant.Type.Nil)
            return arrays;
        int[] idx = arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
        int faces = idx.Length / 3;
        var fn = new Vector3[faces];
        var area = new float[faces];
        for (int f = 0; f < faces; f++)
        {
            Vector3 a = pos[idx[f * 3]], b = pos[idx[f * 3 + 1]], c = pos[idx[f * 3 + 2]];
            // Godot's front faces wind clockwise.
            Vector3 n = (c - a).Cross(b - a);
            area[f] = n.Length();
            fn[f] = area[f] > 1e-10f ? n / area[f] : Vector3.Zero;
        }
        var bySpot = new Dictionary<(long, long, long), List<int>>();
        var cornerSpot = new List<int>[idx.Length];
        for (int k = 0; k < idx.Length; k++)
        {
            Vector3 p = pos[idx[k]] * 1.0e4f;
            var key = ((long)Mathf.Round(p.X), (long)Mathf.Round(p.Y), (long)Mathf.Round(p.Z));
            if (!bySpot.TryGetValue(key, out List<int>? list))
                bySpot[key] = list = new List<int>();
            list.Add(k);
            cornerSpot[k] = list;
        }
        float limit = Mathf.Cos(Mathf.DegToRad(CreaseDeg));
        var from = new List<int>();
        var normals = new List<Vector3>();
        var made = new Dictionary<(int, long, long, long), int>();
        var index = new int[idx.Length];
        for (int k = 0; k < idx.Length; k++)
        {
            Vector3 own = fn[k / 3];
            Vector3 sum = Vector3.Zero;
            foreach (int o in cornerSpot[k])
                if (fn[o / 3].Dot(own) >= limit)
                    sum += fn[o / 3] * area[o / 3];
            Vector3 n = sum.LengthSquared() > 1e-20f ? sum.Normalized()
                : own != Vector3.Zero ? own : Vector3.Up;
            var key = (idx[k], (long)Mathf.Round(n.X * 1e4f), (long)Mathf.Round(n.Y * 1e4f), (long)Mathf.Round(n.Z * 1e4f));
            if (!made.TryGetValue(key, out int v))
            {
                v = from.Count;
                made[key] = v;
                from.Add(idx[k]);
                normals.Add(n);
            }
            index[k] = v;
        }

        var result = new Godot.Collections.Array();
        result.Resize((int)Mesh.ArrayType.Max);
        int old = pos.Length;
        for (int a = 0; a < (int)Mesh.ArrayType.Max; a++)
        {
            Variant src = arrays[a];
            if (src.VariantType == Variant.Type.Nil || a == (int)Mesh.ArrayType.Index)
                continue;
            result[a] = src.VariantType switch
            {
                Variant.Type.PackedVector3Array => Pick(src.AsVector3Array(), from, 1),
                Variant.Type.PackedVector2Array => Pick(src.AsVector2Array(), from, 1),
                Variant.Type.PackedColorArray => Pick(src.AsColorArray(), from, 1),
                Variant.Type.PackedFloat32Array => Pick(src.AsFloat32Array(), from, src.AsFloat32Array().Length / old),
                Variant.Type.PackedInt32Array => Pick(src.AsInt32Array(), from, src.AsInt32Array().Length / old),
                Variant.Type.PackedByteArray => Pick(src.AsByteArray(), from, src.AsByteArray().Length / old),
                _ => src,
            };
        }
        result[(int)Mesh.ArrayType.Normal] = normals.ToArray();
        result[(int)Mesh.ArrayType.Index] = index;
        return result;
    }

    private static T[] Pick<T>(T[] src, List<int> from, int per)
    {
        var dst = new T[from.Count * per];
        for (int i = 0; i < from.Count; i++)
            System.Array.Copy(src, from[i] * per, dst, i * per, per);
        return dst;
    }

    /// <summary>
    /// Per vertex: the normal averaged over every vertex on its spot, and the
    /// ink its piece takes - a piece being what triangles and shared spots join,
    /// its size the diagonal of its box against the model's length.
    /// </summary>
    private static float[] InkNormals(Godot.Collections.Array arrays, float length)
    {
        Vector3[] pos = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        Vector3[] nor = arrays[(int)Mesh.ArrayType.Normal].AsVector3Array();
        int[] idx = arrays[(int)Mesh.ArrayType.Index].VariantType == Variant.Type.Nil
            ? System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Range(0, pos.Length))
            : arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
        int n = pos.Length;
        var parent = new int[n];
        for (int i = 0; i < n; i++)
            parent[i] = i;
        int Root(int i)
        {
            while (parent[i] != i)
                i = parent[i] = parent[parent[i]];
            return i;
        }
        void Join(int a, int b)
        {
            a = Root(a);
            b = Root(b);
            if (a != b)
                parent[a] = b;
        }

        // One spot: positions within a tenth of a millimetre of a model a metre long.
        var spot = new Dictionary<(long, long, long), int>();
        var spotOf = new int[n];
        var sums = new List<Vector3>();
        for (int i = 0; i < n; i++)
        {
            Vector3 p = pos[i] * 1.0e4f;
            var key = ((long)Mathf.Round(p.X), (long)Mathf.Round(p.Y), (long)Mathf.Round(p.Z));
            if (!spot.TryGetValue(key, out int k))
            {
                k = sums.Count;
                spot[key] = k;
                sums.Add(Vector3.Zero);
                spotOf[i] = k;
            }
            else
            {
                spotOf[i] = k;
            }
            sums[k] += i < nor.Length ? nor[i] : Vector3.Zero;
        }
        var first = new int[sums.Count];
        System.Array.Fill(first, -1);
        for (int i = 0; i < n; i++)
        {
            int k = spotOf[i];
            if (first[k] < 0)
                first[k] = i;
            else
                Join(i, first[k]);
        }
        for (int t = 0; t + 2 < idx.Length; t += 3)
        {
            Join(idx[t], idx[t + 1]);
            Join(idx[t], idx[t + 2]);
        }

        var lo = new Dictionary<int, Vector3>();
        var hi = new Dictionary<int, Vector3>();
        for (int i = 0; i < n; i++)
        {
            int r = Root(i);
            if (!lo.TryGetValue(r, out Vector3 a))
            {
                lo[r] = hi[r] = pos[i];
                continue;
            }
            lo[r] = a.Min(pos[i]);
            hi[r] = hi[r].Max(pos[i]);
        }

        var custom = new float[n * 4];
        for (int i = 0; i < n; i++)
        {
            Vector3 s = sums[spotOf[i]];
            s = s.LengthSquared() > 1e-12f ? s.Normalized() : (i < nor.Length ? nor[i] : Vector3.Up);
            int r = Root(i);
            float size = (hi[r] - lo[r]).Length() / Mathf.Max(length, 1e-6f);
            float ink = Mathf.SmoothStep(InkFrom, InkFull, size);
            custom[i * 4 + 0] = s.X;
            custom[i * 4 + 1] = s.Y;
            custom[i * 4 + 2] = s.Z;
            custom[i * 4 + 3] = ink;
        }
        return custom;
    }
}
