using System;
using System.Collections.Generic;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// One crater, drawn: a bowl, the soil thrown out of it, and the stones left on
/// top.
///
/// <b>Its own plane lying on the ground, and not a mark in the ash map, and the
/// reason is a measurement rather than a preference.</b> The ash map is 8 world
/// units to the texel (<c>Stage3D.AshGrain</c>) - right for a fire, which covers
/// whole cells - and a crater is 42 units across. <b>Five texels.</b> Everything
/// that makes a crater read as a crater rather than as a smudge is finer than
/// that: the ragged rim, the fingers of thrown soil, the streaks converging on the
/// hole, the spatter breaking up the outer edge. So the mark moved off the map and
/// onto a plane of its own, where a fragment is a fragment and the detail is
/// whatever the shader draws.
///
/// The arrangement <see cref="ProcBlast"/> already uses for the rings it pushes
/// along the ground, for its own stated reason: a <c>PlaneMesh</c> is already in
/// XZ, so lying it on the board needs no rotation, and the board's own projection
/// squashes the circle into the ellipse the eye expects. Which is also why the
/// reference art of a crater is an ellipse: it is a circle seen from where this
/// camera stands.
///
/// <b>The depth is painted, and that is not a compromise.</b> A real dent is
/// fought by this camera twice over: below the ground line and behind the ground
/// are the same test here (see <see cref="Stage3D.Stem"/>), so geometry pushed
/// down is geometry buried by the cell it sits in; and the cell top is a flat
/// hexagon with no vertices to dent. What reads as depth in the reference is not
/// geometry either - it is value and convergence: dark at the middle, radial
/// streaks pointing into it, the far inner wall catching the light and the near one
/// not, and a lit lip standing above the rim. All four of those are a fragment
/// shader's own business.
/// </summary>
public sealed partial class PitArt : Node3D
{
    private MeshInstance3D? _decal;
    private ShaderMaterial? _ink;
    private PlaneMesh? _plane;
    private readonly Dictionary<string, float> _live = new();

    /// <summary>Build the plane. The two camera terms are the field's, handed in
    /// rather than read, for <see cref="ProcBlast.Build"/>'s reason.</summary>
    public void Build(float squash, float rise)
    {
        _ink = new ShaderMaterial { Shader = Etching, RenderPriority = Stage3D.StandOrder };
        _ink.SetShaderParameter("sun", Stage3D.Sun);
        _ink.SetShaderParameter("cast", Stage3D.SunCast);
        _plane = new PlaneMesh { Size = Vector2.One };
        _decal = new MeshInstance3D
        {
            Mesh = _plane,
            SortingUseAabbCenter = false,
            MaterialOverride = _ink,
            Visible = false,
            // Lifted clear of the face it lies on and slid back so the lift costs
            // no screen pixels - hit_scar's coin toss, which a decal is the exact
            // case of.
            Position = Stage3D.Clear(squash, rise),
        };
        AddChild(_decal);
    }

    /// <summary>
    /// Put this crater on the board.
    ///
    /// <paramref name="spot"/> is board space and <paramref name="lift"/> the
    /// ground's height there - the pair <see cref="Craters.Pit"/> carries, and the
    /// pair <see cref="Stage3D.Ground"/> takes. <paramref name="wide"/> is the
    /// mark's radius in screen px: the plane is twice that across, and the bowl is
    /// the inner share of it (see <c>bowl</c>), so the apron has room to reach the
    /// plane's edge without being cut by it.
    /// </summary>
    public void Show(Vector2 spot, float lift, float wide, float ink, float seed,
                     float squash, float rise)
    {
        if (_decal is null || _plane is null || _ink is null)
            return;
        _plane.Size = new Vector2(2.0f * wide, 2.0f * wide);
        Position = Stage3D.Ground(spot, lift, squash, rise);
        _ink.SetShaderParameter("ink", ink);
        _ink.SetShaderParameter("seed", seed);
        // How many screen px the plane is, so the speckle and the stones can be a
        // size on screen rather than a share of a crater - a big crater is not a
        // small one with bigger gravel in it.
        _ink.SetShaderParameter("span", 2.0f * wide);
        _decal.Visible = true;
    }

    public void Hide()
    {
        if (_decal is not null)
            _decal.Visible = false;
    }

    /// <summary>One number of the crater shader, live - <see cref="ProcBlast.Dial"/>
    /// exactly, read out of the shader's own text for its reason.</summary>
    public float Dial(string uniform)
    {
        if (!_live.TryGetValue(uniform, out float now))
        {
            now = ProcBlast.Uniform(EtchCode, uniform);
            _live[uniform] = now;
        }
        return now;
    }

    public void Dial(string uniform, float value)
    {
        _live[uniform] = value;
        bool counted = EtchCode.Contains("uniform int " + uniform + " = ",
                                         StringComparison.Ordinal);
        Variant sent = counted ? Mathf.RoundToInt(value) : value;
        _ink?.SetShaderParameter(uniform, sent);
    }

    private static readonly Shader Etching = new() { Code = EtchCode };

    /// <summary>The shader's text, for the panel and the check to read the numbers
    /// out of.</summary>
    internal static string Code => EtchCode;

    /// <summary>
    /// The crater itself.
    ///
    /// Read off the reference art, which has three rings and a scatter on top of
    /// them, all of it in one silhouette that is nowhere circular:
    ///
    /// - <b>the bowl</b>: nearly black at the middle, with streaks converging on
    ///   it, and its far wall lighter than its near one;
    /// - <b>the apron</b>: soil thrown out and lying on the surface, <b>lighter
    ///   than the ground</b> - fresh subsoil - fingered and breaking into spatter
    ///   at the outside;
    /// - <b>the lip</b>: a thin bright edge where the apron meets the hole, which
    ///   is what says the rim stands above the ground rather than being drawn on
    ///   it;
    /// - <b>the stones</b>: three dozen chunks with a lit top, a dark side and a
    ///   cast shadow, sitting on top of everything.
    ///
    /// <b>The apron being lighter is the half that no mark in the ash map could
    /// ever have drawn</b>, and it is most of why the old one read as a stain: that
    /// map darkens, and a crater is a dark hole in the middle of a *pale* splash.
    /// </summary>
    private const string EtchCode = @"
shader_type spatial;
render_mode unshaded, cull_disabled, blend_mix, depth_draw_never;

// The board's own sun, and where a thing one unit up throws its shadow along the
// ground - Stage3D.Sun and Stage3D.SunCast. The stones' shadows fall along the
// second one, so they lie the same way every other shadow on this board does.
uniform vec3 sun = vec3(0.0, 1.0, 0.0);
uniform vec2 cast = vec2(0.0, 0.0);

// How much of the mark is drawn at all, and which crater this is.
uniform float ink = 0.80;
uniform float seed = 0.0;
// The plane's own size in screen px, so grain is grain and not a share.
uniform float span = 84.0;

// How much of the plane's radius is the hole. The rest is what came out of it.
uniform float bowl = 0.46;
// How ragged the outer edge is, as a share of the radius. A crater's outline is
// the one thing that must not be a circle: at zero this is a coin on the ground.
uniform float rag = 0.30;
// How many fingers of thrown soil there are round the apron, and how far out the
// detached spatter carries past them.
uniform float fingers = 6.0;
uniform float spatter = 0.34;
// How many streaks converge on the hole. High, because these are scour marks
// rather than shapes - the reference has dozens.
uniform float streaks = 26.0;

// How dark the middle of the bowl is, and how much lighter than the ground the
// thrown soil is. <b>Both directions at once, which is the whole point:</b> a
// crater is a dark hole in a pale splash, and a map that only darkens can draw
// exactly one of those two things.
uniform float deep = 0.86;
uniform float apron = 0.52;
// How much the sun lights the far inner wall against the near one. This is the
// depth: with it at zero the bowl is a flat dark disc, whatever else is drawn.
uniform float wall = 0.70;
// The bright lip on the rim - the other half of the depth, and the half that says
// the rim is above the ground rather than painted on it.
uniform float lip = 0.45;

// The gravel: how strong the fine speckle is and how big its grain is on screen.
uniform float grit = 0.20;
uniform float grain = 3.1;

// The stones. Chunks with a lit top, a dark side and a shadow - the part of the
// reference that makes it read as drawn rather than as blurred.
uniform int stones = 8;
uniform float stone_size = 5.2;
uniform float stone_shade = 0.55;

// Earth, subsoil and rock. Not on the panel: ControlPanel has no organ for a
// colour, and three sliders in place of one is a way to make mud.
// <b>The subsoil is darker than the ground, not lighter, and that is the first
// thing the picture corrected.</b> Read off the reference alone it looks pale -
// but the reference stands on nothing, and this board's ground is dry pale tan,
// so a pale apron came out invisible: a black hole with no splash round it at
// all. What is thrown out is damp earth from under a dry crust, so it reads
// darker, and the pale is kept for the thin outer spatter where the coat is one
// grain deep and the crust shows through it.
uniform vec3 soil_dark : source_color = vec3(0.050, 0.036, 0.024);
uniform vec3 soil_raw : source_color = vec3(0.300, 0.220, 0.130);
uniform vec3 soil_pale : source_color = vec3(0.560, 0.470, 0.310);
uniform vec3 rock_lit : source_color = vec3(0.430, 0.375, 0.295);
uniform vec3 rock_dark : source_color = vec3(0.130, 0.110, 0.090);

float pit_hash(vec2 p) {
    p = fract(p * vec2(443.897, 441.423) + seed * 17.31);
    p += dot(p, p + 19.19);
    return fract(p.x * p.y);
}

float pit_noise(vec2 p) {
    vec2 i = floor(p), f = fract(p);
    vec2 u = f * f * (3.0 - 2.0 * f);
    return mix(mix(pit_hash(i), pit_hash(i + vec2(1.0, 0.0)), u.x),
               mix(pit_hash(i + vec2(0.0, 1.0)), pit_hash(i + vec2(1.0, 1.0)), u.x),
               u.y);
}

float pit_fbm(vec2 p) {
    float sum = 0.0, amp = 0.5;
    for (int k = 0; k < 4; k++) {
        sum += amp * pit_noise(p);
        p *= 2.02;
        amp *= 0.5;
    }
    return sum;
}

void fragment() {
    vec2 d = (UV - 0.5) * 2.0;
    float r = length(d);
    if (r > 1.0) {
        // Outside its own plane, and said so rather than left to the alpha: the
        // corners of a square plane are 1.41 of its radius, and a crater drawn out
        // there would be a mark whose shape is the plane's.
        ALBEDO = vec3(0.0);
        ALPHA = 0.0;
    } else {
        float turn = atan(d.y, d.x);

        // <b>Ragged, and the hole has to be ragged too.</b> Rag applied to the
        // apron alone left the thing an ellipse: the apron was invisible against
        // this ground, so the outline being read was the bowl's own perfect circle.
        // One noise walked round the circumference, so it is the crater's own edge
        // rather than a pattern the crater sits in.
        float rough = 1.0 - rag * (0.30 + 0.85 * pit_fbm(vec2(turn * 2.2, 4.3)));
        float cup_at = bowl * rough;
        float reach = rough * (0.74 + 0.50 * pit_fbm(vec2(turn * fingers, 11.7)));

        // Which side of the crater the sun is on, in the ground's own XZ. Used
        // twice: the far inner wall catches light, and the stones cast along it.
        vec2 lit_way = normalize(-cast + vec2(1e-5));
        float face = dot(normalize(d + vec2(1e-5)), lit_way);

        // ---- the apron: what came out, lying on top --------------------------
        float out_at = clamp((r - cup_at) / max(reach - cup_at, 1e-3), 0.0, 1.0);
        float speck = pit_fbm(d * span / max(grain, 0.2));
        // Thick at the rim, thinning outward, and broken into spatter on the way:
        // past the fingers it is dots rather than a coat, which is what the outer
        // half of the reference is made of.
        float laid = 1.0 - smoothstep(0.35, 1.0, out_at);
        laid = clamp(laid + spatter * (speck - 0.58) * (1.0 - 0.5 * out_at), 0.0, 1.0);
        laid *= step(r, reach);

        // Damp earth at the rim, drying to a pale dust of single grains at the
        // outside - the two directions the ash map could never draw between.
        vec3 skin = mix(soil_raw, soil_pale, smoothstep(0.62, 1.0, out_at));
        skin = mix(skin, skin * 1.18, grit * speck);

        // ---- the bowl: the hole ---------------------------------------------
        float in_at = clamp(r / max(cup_at, 1e-3), 0.0, 1.0);
        float hole = 1.0 - smoothstep(0.86, 1.02, in_at);
        // Streaks converging on the middle. Angular frequency, so they narrow as
        // they go in - which is what convergence is - and they scour the wall
        // rather than the floor, so they fade out at the centre.
        float scour = (pit_fbm(vec2(turn * streaks, r * 7.0 + 3.1)) - 0.5)
                      * smoothstep(0.10, 0.85, in_at);
        // <b>The depth, and it is added rather than multiplied.</b> Multiplied into
        // a floor already crushed to black by `deep` it did nothing at all: the
        // far wall lighting is the whole of what separates a bowl from a stain, so
        // it has to be light put back rather than darkness scaled.
        float sink = deep * (1.0 - pow(in_at, 1.55)) + 0.42 * scour;
        vec3 cup = mix(soil_raw * 0.85, soil_dark, clamp(sink, 0.0, 1.0));
        cup += soil_pale * wall * clamp(face, 0.0, 1.0)
               * smoothstep(0.22, 0.98, in_at) * 0.55;

        // ---- the lip: the rim standing above the ground ----------------------
        // A thin bright edge just outside the hole, brightest where the sun is -
        // the other half of the depth, and the half that says the rim is above the
        // ground rather than painted on it.
        float brow = (1.0 - smoothstep(0.0, 0.20, abs(r - cup_at) / max(cup_at, 1e-3)))
                     * lip * clamp(0.25 + 0.85 * face, 0.0, 1.0) * step(cup_at, r);

        vec3 tone = mix(skin + soil_pale * brow, cup, hole);
        float cover = clamp(max(laid, hole), 0.0, 1.0);

        // ---- the stones, on top of all of it ---------------------------------
        // <b>Round lumps with a hashed aspect, not facets.</b> Five-lobed radii at
        // four pixels across drew grey stars, which is the one thing worse than
        // no stones at all.
        float stony = 0.0, shadowy = 0.0, stone_face = 0.0, stone_tint = 0.0;
        for (int k = 0; k < stones; k++) {
            float fk = float(k);
            float ang = pit_hash(vec2(fk, 3.0)) * 6.2831853;
            // Out on the apron for the most part - a stone in the hole is one that
            // fell back in, and the reference has one or two of those.
            float at = cup_at * 0.5 + (reach - cup_at * 0.5) * pit_hash(vec2(fk, 5.0));
            vec2 c = vec2(cos(ang), sin(ang)) * at;
            float size = stone_size / max(span, 1.0)
                         * (0.60 + 0.85 * pit_hash(vec2(fk, 7.0)));
            vec2 v = d - c;
            // Squashed a little, by its own hash, so a field of them is not a field
            // of circles.
            v.y /= 0.72 + 0.5 * pit_hash(vec2(fk, 13.0));
            float dist = length(v);
            float body = 1.0 - smoothstep(size * 0.80, size, dist);
            if (body > stony) {
                stony = body;
                stone_face = dot(normalize(v + vec2(1e-5)), lit_way);
                stone_tint = pit_hash(vec2(fk, 17.0));
            }
            // Its own cast shadow, along the board's sun - the offset every other
            // shadow on this board uses.
            shadowy = max(shadowy,
                          1.0 - smoothstep(size * 0.7, size * 1.3,
                                           length(v + lit_way * -size * 1.4)));
        }
        shadowy = max(shadowy - stony, 0.0);
        tone = mix(tone, tone * (1.0 - stone_shade), shadowy);
        cover = max(cover, shadowy * 0.8);
        vec3 rock = mix(rock_dark, rock_lit, clamp(0.35 + 0.75 * stone_face, 0.0, 1.0));
        rock *= 0.80 + 0.40 * stone_tint;
        tone = mix(tone, rock, stony);
        cover = max(cover, stony);

        ALBEDO = tone;
        ALPHA = clamp(cover * ink, 0.0, 1.0);
    }
}

";
}
