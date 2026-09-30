using System;
using System.Collections.Generic;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// Grass on a cell of the volumetric board (<see cref="Stage3D"/>), three ways
/// to hold side by side (docs/props.md, "Tree3D: трава"):
///
/// <see cref="Kind.Painted"/> - no volume: a hexagon laid a hair over the
/// ground and painted by its shader - cel bands of tone, a grain of blade
/// marks, and the gust as a lighter band running over it.
/// <see cref="Kind.Shells"/> - a short lawn out of <see cref="ShellCount"/>
/// hexagons stacked up, each cut down to the blades that reach it.
/// <see cref="Kind.PaintedTufts"/> - the painted ground with tufts of blades
/// standing on it, gathered in clumps.
/// <see cref="Kind.CelTufts"/> - the same, the tufts lit on the models' cel
/// ramp (<see cref="Toon.RampCode"/>) by the normal of a dome over the tuft
/// and its clump, not of the blade.
///
/// <b>What all three read alike</b>, so a cell of one next to a cell of another
/// is the same grass: one palette; the wind (<see cref="Wind"/>, the gust wave
/// of the sprite wood, <c>Grove</c>, and of the tree models - the same rate and
/// travel, so a band crossing the grass reaches the crowns in step); and one
/// press map over the board - how flat the grass is pressed and which way
/// (<see cref="Press"/>), and where it is torn out (<see cref="Cut"/>). What
/// presses it is the scene's to say: this does not know about hulls or trees.
///
/// <b>Lit as the ground is</b>: the board is unshaded and a shadow is
/// <c>ShadowInk</c> of the light going. The volumes take the sun's shadow map
/// the same way - their own colour, less <see cref="ShadowInk"/> of it where
/// the sun does not reach - and no normal: a blade shaded by its own facing
/// broke the grass up into flecks, which is what the crowns did before they
/// stopped shadowing themselves. The painted hexagon sits under the board's
/// shadow skin and is darkened by it like the ground.
///
/// World units are board px; <see cref="Ppm"/> says how many to a metre.
/// </summary>
public sealed partial class Grass3D : Node3D
{
    public enum Kind { Painted, Shells, PaintedTufts, CelTufts }

    /// <summary>Board px to a metre, the models' (<c>px_per_m</c> over the tier).</summary>
    public float Ppm = 17.0f;
    /// <summary>How hard the wind is, 1 the sprite wood's; 0 still.</summary>
    public float Wind = 1.0f;
    public float GustRate = 0.55f, GustTravel = 0.0035f;
    /// <summary>Downwind on the ground (x, z).</summary>
    public Vector2 WindWay = Vector2.Right;
    /// <summary>How much of the light a shadow takes (<c>Stage3D.ShadowInk.A</c>).</summary>
    public float ShadowInk = 0.45f;

    /// <summary>The lawn's height and the tufts', m.</summary>
    public const float ShellTallM = 0.32f, TuftTallM = 0.55f;
    public const int ShellCount = 14;
    /// <summary>Tufts to a square metre, and blades to a tuft.</summary>
    public const float TuftsPerM2 = 7.0f;
    /// <summary>How far in from the cell's rim the grass thins out, m.</summary>
    public const float FadeM = 0.8f;

    /// <summary>The press map's cell, board px.</summary>
    private const float Texel = 2.0f;
    /// <summary>Over the ground by this: the pit is at 0.4, the shadow skin at 2.</summary>
    private const float Lift = 1.0f;
    /// <summary>Pressed grass comes back up at this much a second, as far as
    /// <see cref="Keep"/>: a hull's lane stays in it.</summary>
    private const float Relax = 1.0f / 25.0f, Keep = 0.45f;

    private readonly List<ShaderMaterial> _inks = new();
    private Rect2 _span;
    private int _w, _h;
    private float[] _press = Array.Empty<float>(), _cut = Array.Empty<float>();
    private Vector2[] _way = Array.Empty<Vector2>();
    private byte[] _bytes = Array.Empty<byte>();
    private Image? _image;
    private ImageTexture? _tex;
    private bool _dirty, _relaxing;
    private float _clock;

    /// <summary>The ground the press map covers, x and z.</summary>
    public void Map(Rect2 xz)
    {
        _span = xz;
        _w = Math.Max(1, Mathf.CeilToInt(xz.Size.X / Texel));
        _h = Math.Max(1, Mathf.CeilToInt(xz.Size.Y / Texel));
        _press = new float[_w * _h];
        _cut = new float[_w * _h];
        _way = new Vector2[_w * _h];
        _bytes = new byte[_w * _h * 4];
        _image = Image.CreateEmpty(_w, _h, false, Image.Format.Rgba8);
        _dirty = true;
        Upload();
        _tex = ImageTexture.CreateFromImage(_image);
    }

    /// <summary>
    /// Grass over one cell's top: <paramref name="mid"/> its middle on the
    /// ground, <paramref name="radius"/> its corner radius (flat-topped, the
    /// board's). <paramref name="clear"/> - trunks, px from the middle given -
    /// keeps the tufts off where something stands.
    /// </summary>
    public void Lay(Kind kind, Vector3 mid, float radius, int seed, IEnumerable<Vector3>? clear = null)
    {
        if (kind is Kind.Painted or Kind.PaintedTufts or Kind.CelTufts)
            AddChild(new MeshInstance3D
            {
                Name = $"Painted{seed}",
                Mesh = Hexes(mid, radius, 1),
                MaterialOverride = Ink(PaintedShader, mid, radius, seed),
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            });
        if (kind == Kind.Shells)
        {
            ShaderMaterial ink = Ink(ShellShader, mid, radius, seed);
            ink.SetShaderParameter("tall", ShellTallM * Ppm);
            AddChild(new MeshInstance3D
            {
                Name = $"Shells{seed}",
                Mesh = Hexes(mid, radius, ShellCount + 1, 24),
                MaterialOverride = ink,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            });
        }
        if (kind is Kind.PaintedTufts or Kind.CelTufts)
            AddChild(new MeshInstance3D
            {
                Name = $"Tufts{seed}",
                Mesh = Tufts(mid, radius, seed, clear),
                MaterialOverride = Ink(kind == Kind.CelTufts ? CelTuftShader : TuftShader, mid, radius, seed),
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            });
    }

    /// <summary>
    /// Grass pressed flat over a rectangle on the ground: <paramref name="at"/>
    /// its middle, <paramref name="along"/> the way it lies over (the way the
    /// thing pressing it went), <paramref name="length"/> by
    /// <paramref name="width"/> px, <paramref name="much"/> 0-1. Pressing is
    /// the most of what was and what is: a hull over its own lane twice does
    /// not flatten it more.
    /// </summary>
    public void Press(Vector3 at, Vector2 along, float length, float width, float much)
    {
        if (_press.Length == 0 || along.LengthSquared() < 1e-8f)
            return;
        Vector2 dir = along.Normalized(), perp = new(-dir.Y, dir.X);
        float reach = 0.5f * Mathf.Sqrt(length * length + width * width);
        var c = new Vector2(at.X, at.Z);
        Each(c, reach, (i, rel) =>
        {
            float a = Mathf.Abs(rel.Dot(dir)), b = Mathf.Abs(rel.Dot(perp));
            float e = Mathf.Min(length * 0.5f - a, width * 0.5f - b);
            if (e <= 0.0f)
                return;
            float v = much * Mathf.SmoothStep(0.0f, 2.5f, e);
            if (v <= _press[i])
                return;
            _way[i] = _press[i] < 0.05f ? dir : _way[i].Lerp(dir, v).Normalized();
            _press[i] = v;
            _dirty = _relaxing = true;
        });
    }

    /// <summary>
    /// Grass torn out: a disc of <paramref name="radius"/> round
    /// <paramref name="at"/>, only behind <paramref name="before"/> px along
    /// <paramref name="way"/> - a root plate's pit, whose front is still in the
    /// ground.
    /// </summary>
    public void Cut(Vector3 at, float radius, Vector2 way, float before)
    {
        if (_cut.Length == 0 || radius <= 0.0f)
            return;
        Vector2 dir = way.LengthSquared() > 1e-8f ? way.Normalized() : Vector2.Zero;
        Each(new Vector2(at.X, at.Z), radius, (i, rel) =>
        {
            if (rel.Dot(dir) > before)
                return;
            float v = Mathf.SmoothStep(0.0f, 2.0f, radius - rel.Length());
            if (v <= _cut[i])
                return;
            _cut[i] = v;
            _dirty = true;
        });
    }

    /// <summary>All of it standing again.</summary>
    public void Heal()
    {
        Array.Clear(_press);
        Array.Clear(_cut);
        _dirty = true;
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        _clock += dt;
        if (_relaxing)
        {
            _relaxing = false;
            for (int i = 0; i < _press.Length; i++)
                if (_press[i] > Keep)
                {
                    _press[i] = Mathf.Max(Keep, _press[i] - Relax * dt);
                    _relaxing = _dirty = true;
                }
        }
        if (_dirty)
        {
            Upload();
            if (_image is not null)
                _tex?.Update(_image);
        }
        foreach (ShaderMaterial m in _inks)
        {
            m.SetShaderParameter("g_time", _clock);
            m.SetShaderParameter("wind", Wind);
            m.SetShaderParameter("wind_way", WindWay.Normalized());
        }
    }

    private void Each(Vector2 c, float reach, Action<int, Vector2> at)
    {
        int x0 = Math.Max(0, (int)((c.X - reach - _span.Position.X) / Texel));
        int x1 = Math.Min(_w - 1, (int)((c.X + reach - _span.Position.X) / Texel));
        int y0 = Math.Max(0, (int)((c.Y - reach - _span.Position.Y) / Texel));
        int y1 = Math.Min(_h - 1, (int)((c.Y + reach - _span.Position.Y) / Texel));
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
            var p = new Vector2(_span.Position.X + (x + 0.5f) * Texel, _span.Position.Y + (y + 0.5f) * Texel);
            at(y * _w + x, p - c);
        }
    }

    /// <summary>The map into its texture: r pressed, g b the way it lies
    /// (0.5 is none), a torn out.</summary>
    private void Upload()
    {
        _dirty = false;
        if (_image is null)
            return;
        for (int i = 0; i < _press.Length; i++)
        {
            _bytes[4 * i] = (byte)(Mathf.Clamp(_press[i], 0.0f, 1.0f) * 255.0f);
            _bytes[4 * i + 1] = (byte)Mathf.Clamp(128.0f + 127.0f * _way[i].X, 0.0f, 255.0f);
            _bytes[4 * i + 2] = (byte)Mathf.Clamp(128.0f + 127.0f * _way[i].Y, 0.0f, 255.0f);
            _bytes[4 * i + 3] = (byte)(Mathf.Clamp(_cut[i], 0.0f, 1.0f) * 255.0f);
        }
        _image.SetData(_w, _h, false, Image.Format.Rgba8, _bytes);
    }

    private ShaderMaterial Ink(Shader shader, Vector3 mid, float radius, int seed)
    {
        var ink = new ShaderMaterial { Shader = shader };
        ink.SetShaderParameter("hex_mid", mid + Vector3.Up * Lift);
        ink.SetShaderParameter("hex_r", radius);
        ink.SetShaderParameter("hex_fade", FadeM * Ppm);
        ink.SetShaderParameter("seed", (seed % 97) * 1.37f);
        ink.SetShaderParameter("shadow_ink", ShadowInk);
        ink.SetShaderParameter("gust_rate", GustRate);
        ink.SetShaderParameter("gust_travel", GustTravel);
        ink.SetShaderParameter("ppm", Ppm);
        if (_tex is not null)
        {
            ink.SetShaderParameter("press_tex", _tex);
            ink.SetShaderParameter("press_map", new Vector4(_span.Position.X, _span.Position.Y,
                                                            1.0f / _span.Size.X, 1.0f / _span.Size.Y));
        }
        _inks.Add(ink);
        return ink;
    }

    /// <summary><paramref name="layers"/> hexagons over the cell, the layer's
    /// share of the height in UV.x - 0 the ground, 1 the top - in world
    /// place, so the shader needs no matrix. Each sixth cut into
    /// <paramref name="cuts"/>² triangles: the shells bend at their vertices,
    /// and six triangles read the press map at seven points - a hull's lane
    /// across them did not show.</summary>
    private static ArrayMesh Hexes(Vector3 mid, float radius, int layers, int cuts = 1)
    {
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        var corner = new Vector3[6];
        for (int k = 0; k < 6; k++)
        {
            float a = Mathf.Tau * k / 6.0f;
            corner[k] = new Vector3(Mathf.Cos(a) * radius, 0.0f, Mathf.Sin(a) * radius);
        }
        Vector3 at = mid + Vector3.Up * Lift;
        for (int l = 0; l < layers; l++)
        {
            float f = layers > 1 ? l / (float)(layers - 1) : 0.0f;
            for (int k = 0; k < 6; k++)
            {
                Vector3 e1 = corner[k] / cuts, e2 = corner[(k + 1) % 6] / cuts;
                for (int i = 0; i < cuts; i++)
                for (int j = 0; i + j < cuts; j++)
                {
                    Vector3 o = e1 * i + e2 * j;
                    var tris = new List<Vector3> { o, o + e1, o + e2 };
                    if (i + j + 1 < cuts)
                        tris.AddRange(new[] { o + e1, o + e1 + e2, o + e2 });
                    foreach (Vector3 v in tris)
                    {
                        st.SetUV(new Vector2(f, 0.0f));
                        st.SetNormal(Vector3.Up);
                        st.AddVertex(at + v);
                    }
                }
            }
        }
        return st.Commit();
    }

    /// <summary>How far inside a flat-topped hexagon of corner radius
    /// <paramref name="r"/> a point is, px; the shaders' <c>hex_in</c>.</summary>
    private static float HexIn(Vector2 p, float r)
    {
        p = p.Abs();
        return r * 0.8660254f - Mathf.Max(p.Y, p.Dot(new Vector2(0.8660254f, 0.5f)));
    }

    /// <summary>
    /// The tufts of a cell, baked into one mesh in world place: a tuft is 5-8
    /// blades fanned out from a root and leaning out, each a tapered strip of
    /// three rows; UV.x how far up the blade, UV.y the tuft's own tone, UV2 its
    /// root, which the wind and the press are read at - a tuft bends as one.
    /// More than half stand in clumps (a meadow is patchy), and they thin out
    /// toward the rim as the painted ground does.
    ///
    /// <b>The normal is a dome's, not the blade's</b>, for the cel ramp
    /// (<see cref="Kind.CelTufts"/>; the flat tufts ignore it): a dome over the
    /// tuft, from a point under its root, and for a tuft in a clump, two thirds
    /// of a dome over the clump. Lit by its own facing a blade was a fleck; lit so, a
    /// tuft and a clump turn one lit side to the sun and one shaded side from
    /// it, and the root, facing out, comes out in the dark step.
    ///
    /// <b>Blades turned to face the eye, give or take.</b> The camera is
    /// orthographic and looks along -z: a blade whose flat is along z is a
    /// line, and a random fan was half lines.
    /// </summary>
    private ArrayMesh Tufts(Vector3 mid, float radius, int seed, IEnumerable<Vector3>? clear)
    {
        var rng = new RandomNumberGenerator { Seed = (ulong)(seed * 7919 + 17) };
        var keep = new List<Vector2>();
        if (clear is not null)
            foreach (Vector3 c in clear)
                keep.Add(new Vector2(c.X - mid.X, c.Z - mid.Z));
        float area = 2.598f * radius * radius / (Ppm * Ppm);
        int count = (int)(area * TuftsPerM2);
        var clumps = new Vector2[Math.Max(1, (int)(area / 3.0f))];
        for (int i = 0; i < clumps.Length; i++)
            clumps[i] = new Vector2(rng.RandfRange(-radius, radius), rng.RandfRange(-radius, radius));
        float fade = FadeM * Ppm;
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        Vector3 ground = mid + Vector3.Up * Lift;
        int placed = 0;
        for (int n = 0; n < count * 3 && placed < count; n++)
        {
            Vector2 p;
            float tall = 1.0f;
            Vector2? clump = null;
            if (rng.Randf() < 0.6f)
            {
                Vector2 c = clumps[rng.RandiRange(0, clumps.Length - 1)];
                p = c + new Vector2((float)rng.Randfn(0.0f, 0.55f), (float)rng.Randfn(0.0f, 0.55f)) * Ppm;
                tall = 1.15f;
                clump = c;
            }
            else
                p = new Vector2(rng.RandfRange(-radius, radius), rng.RandfRange(-radius, radius));
            float inside = HexIn(p, radius);
            float edge = Mathf.SmoothStep(0.0f, fade, inside - 0.2f * fade);
            if (inside < 0.0f || rng.Randf() > edge)
                continue;
            tall *= 0.4f + 0.6f * edge;
            bool blocked = false;
            foreach (Vector2 k in keep)
                blocked |= p.DistanceTo(k) < 0.35f * Ppm;
            if (blocked)
                continue;
            placed++;
            var root = new Vector3(ground.X + p.X, ground.Y, ground.Z + p.Y);
            float h = TuftTallM * Ppm * tall * rng.RandfRange(0.6f, 1.15f);
            float tone = rng.Randf();
            int blades = rng.RandiRange(5, 8);
            // the clump's dome at this root: out from its middle, up at its top
            Vector3 over = Vector3.Up;
            if (clump is { } cc)
            {
                Vector2 d = (p - cc) / (0.8f * Ppm);
                if (d.Length() > 1.0f)
                    d = d.Normalized();
                over = new Vector3(d.X, Mathf.Sqrt(Mathf.Max(0.15f, 1.0f - d.LengthSquared())), d.Y).Normalized();
            }
            Vector3 under = root - Vector3.Up * (0.25f * h);
            for (int b = 0; b < blades; b++)
            {
                float a = Mathf.Tau * (b + rng.Randf() * 0.7f) / blades;
                var outward = new Vector3(Mathf.Cos(a), 0.0f, Mathf.Sin(a));
                Vector3 foot = root + outward * (rng.RandfRange(0.0f, 0.06f) * Ppm);
                float lean = rng.RandfRange(0.12f, 0.55f), len = h * rng.RandfRange(0.65f, 1.0f);
                float wide = rng.RandfRange(0.07f, 0.10f) * Ppm;
                // the flat's across: x, turned no more than 45 degrees off it
                Vector3 side = Vector3.Right.Rotated(Vector3.Up, rng.RandfRange(-0.8f, 0.8f));
                Vector3 Row(float t, float s) =>
                    foot + Vector3.Up * (len * t) + outward * (len * Mathf.Sin(lean) * t * t) + side * (s * wide);
                Vector3[] v = { Row(0, -0.5f), Row(0, 0.5f), Row(0.45f, -0.3f), Row(0.45f, 0.3f), Row(1, 0) };
                float[] ts = { 0, 0, 0.45f, 0.45f, 1 };
                foreach (int i in new[] { 0, 1, 2, 1, 3, 2, 2, 3, 4 })
                {
                    st.SetUV(new Vector2(ts[i], tone));
                    st.SetUV2(new Vector2(root.X, root.Z));
                    Vector3 dome = (v[i] - under).Normalized();
                    st.SetNormal(clump is null ? dome : (0.35f * dome + 0.65f * over).Normalized());
                    st.AddVertex(v[i]);
                }
            }
        }
        GD.Print($"grass3d: {placed} tufts on {area:F0} m2");
        return st.Commit();
    }

    // --- shaders ---------------------------------------------------------------

    /// <summary>What every grass shader shares: the cell, the palette, the
    /// press map, the wind and the tone of a blade by how far up it is.</summary>
    private const string GrassCode = @"
uniform vec3 hex_mid;
uniform float hex_r = 110.0;
uniform float hex_fade = 14.0;
uniform float seed = 0.0;
uniform float ppm = 17.0;
uniform sampler2D press_tex : filter_linear, hint_default_black;
uniform vec4 press_map = vec4(0.0, 0.0, 1.0, 1.0);
uniform vec2 wind_way = vec2(1.0, 0.0);
uniform float wind = 1.0;
uniform float gust_rate = 0.55;
uniform float gust_travel = 0.0035;
uniform float g_time = 0.0;
uniform float shadow_ink = 0.45;
uniform vec3 tone_root : source_color = vec3(0.19, 0.26, 0.10);
uniform vec3 tone_mid : source_color = vec3(0.34, 0.43, 0.17);
uniform vec3 tone_tip : source_color = vec3(0.55, 0.61, 0.29);
uniform vec3 tone_dry : source_color = vec3(0.62, 0.60, 0.38);
" + Toon.NoiseCode + @"
float g_noise(vec2 p) { return noise3(vec3(p, seed)); }
float g_hash(vec2 p) { return hash3(vec3(p, seed + 3.7)); }
// px inside the flat-topped cell
float hex_in(vec2 xz) {
    vec2 p = abs(xz - hex_mid.xz);
    return hex_r * 0.8660254 - max(p.y, dot(p, vec2(0.8660254, 0.5)));
}
// 1 where the grass is, thinning out to the rim on a ragged line
float grass_here(vec2 xz) {
    float rag = (g_noise(xz * 0.05) - 0.5) * 1.3 + (g_noise(xz * 0.21) - 0.5) * 0.5;
    return smoothstep(0.0, hex_fade, hex_in(xz) - hex_fade * 0.25 + rag * hex_fade);
}
// r pressed, gb the way it lies, a torn out
vec4 press_at(vec2 xz) {
    vec2 uv = (xz - press_map.xy) * press_map.zw;
    if (uv.x < 0.0 || uv.y < 0.0 || uv.x > 1.0 || uv.y > 1.0) return vec4(0.0, 0.5, 0.5, 0.0);
    return texture(press_tex, uv);
}
// the sprite wood's gust wave, crossing along x
float gust_at(vec2 xz) { return 0.5 + 0.5 * sin(g_time * gust_rate - xz.x * gust_travel); }
// how far and which way the grass at a root leans, radians along the ground:
// the wind, eased out as it is pressed, and the press's own way
vec2 grass_lean(vec2 root, float phase, vec4 pr) {
    float gust = gust_at(root);
    float sway = (0.08 + 0.34 * gust) * (0.7 + 0.3 * sin(g_time * 2.3 + phase));
    vec2 flutter = vec2(sin(g_time * 5.1 + phase * 3.0), cos(g_time * 4.3 + phase * 2.0)) * 0.05;
    vec2 blown = (wind_way * sway + flutter) * wind;
    vec2 way = pr.gb * 2.0 - 1.0;
    return mix(blown, way * 1.45, pr.r);
}
// a point t up a blade of height rise, bent by lean: over along it, and down
vec3 grass_bend(vec3 v, float rise, float t, vec2 lean) {
    float m = length(lean);
    vec2 d = m > 1e-4 ? lean / m : vec2(0.0);
    float a = min(m, 1.45) * t;
    v.xz += d * rise * sin(a);
    v.y = hex_mid.y + rise * cos(a);
    return v;
}
// the patchiness of a meadow: 0-1, slow
float grass_patch(vec2 xz) {
    return g_noise(xz * 0.022) * 0.65 + g_noise(xz * 0.07 + 11.0) * 0.35;
}
vec3 grass_tone(float t, float patch, vec2 xz, float pressed, float own) {
    vec3 c = mix(tone_root, tone_mid, smoothstep(0.0, 0.55, t));
    c = mix(c, tone_tip, smoothstep(0.55, 1.0, t));
    // cel steps, not a gradient, across the patches
    c *= patch < 0.40 ? 0.86 : (patch > 0.64 ? 1.09 : 1.0);
    c *= 0.94 + 0.12 * own;
    // the gust: a lighter band where the grass is bent over, the blades'
    // paler backs up
    c = mix(c, tone_tip, 0.30 * smoothstep(0.74, 0.84, gust_at(xz)) * min(wind, 1.0) * (0.3 + 0.7 * t));
    return mix(c, tone_dry, 0.62 * pressed);
}
";

    /// <summary>The volumes' light: their own colour where the sun reaches,
    /// less <c>shadow_ink</c> of it where it does not - the board's shadow skin
    /// in the light pass, and nothing from any other light.</summary>
    private const string LitCode = @"
void light() {
    if (LIGHT_IS_DIRECTIONAL)
        DIFFUSE_LIGHT += vec3(shadow_ink * ATTENUATION);
}
";

    private static readonly Shader PaintedShader = new()
    {
        Code = @"
shader_type spatial;
render_mode unshaded, cull_disabled, shadows_disabled, fog_disabled;
" + GrassCode + @"
varying vec3 w;
void vertex() { w = VERTEX; }
void fragment() {
    vec2 xz = w.xz;
    vec4 pr = press_at(xz);
    // the rim and the tear: the ground shows through, on a ragged edge
    if (grass_here(xz) * (1.0 - pr.a) < 0.5) discard;
    float patch = grass_patch(xz);
    // the grain: a tuft painted in some of the cells of 0.34 m - three strokes
    // fanned from a foot, the middle one up the screen (-z), bent with the
    // wind, a dark fleck under the foot. Not every cell, and each shifted and
    // sized by its own hash: a mark to every cell read as a grid.
    float cell = 0.34 * ppm;
    vec2 g = xz / cell;
    vec2 id = floor(g);
    vec2 f = fract(g) - 0.5 - (vec2(g_hash(id), g_hash(id + 17.0)) - 0.5) * 0.35;
    float size = 0.7 + 0.5 * g_hash(id + 29.0);
    vec2 lean = grass_lean(id * cell, g_hash(id + 5.0) * 6.28, pr);
    float v = (0.3 - f.y) / (0.75 * size);          // 0 at the tuft's foot, 1 at its tip
    float draw = step(0.35, g_hash(id + 23.0)) * (1.0 - pr.r);
    float mark = 0.0;
    for (int k = -1; k <= 1; k++) {
        float slope = float(k) * 0.32 + (g_hash(id + float(k) + 41.0) - 0.5) * 0.2;
        float reach = k == 0 ? 1.0 : 0.62 + 0.25 * g_hash(id + float(k) + 47.0);
        float bu = f.x - slope * v - lean.x * v * v * 0.5;
        mark = max(mark, step(abs(bu), 0.075 * size * (1.0 - v / reach)) * step(0.0, v) * step(v, reach));
    }
    mark *= draw;
    float gap = draw * step(length(vec2(f.x, (f.y - 0.33) * 2.2)), 0.2 * size) * (1.0 - mark);
    float t = mix(0.42, 0.95, mark);
    t = mix(t, 0.12, gap);
    // a tone of its own on the mark only: over the whole cell it showed the
    // cells as squares
    vec3 c = grass_tone(t, patch, xz, pr.r, mix(0.5, g_hash(id + 2.0), mark));
    // pressed flat: streaks along the way it lies, not blade marks
    if (pr.r > 0.05) {
        vec2 way = normalize(pr.gb * 2.0 - 1.0 + vec2(1e-4));
        float s = fract(dot(xz, vec2(-way.y, way.x)) / (0.18 * ppm) + g_noise(xz * 0.1) * 0.6);
        c = mix(c, c * (s < 0.5 ? 0.84 : 1.07), pr.r);
    }
    ALBEDO = c;
}",
    };

    private static readonly Shader ShellShader = new()
    {
        Code = @"
shader_type spatial;
render_mode cull_disabled, ambient_light_disabled, specular_disabled, fog_disabled;
uniform float tall = 5.0;
" + GrassCode + @"
varying vec3 rest;
varying float f;
varying float pressed;
void vertex() {
    f = UV.x;
    rest = VERTEX;
    vec4 pr = press_at(VERTEX.xz);
    pressed = pr.r;
    float rise = f * tall * (1.0 - pr.a);
    vec2 lean = grass_lean(VERTEX.xz, g_noise(VERTEX.xz * 0.05) * 6.28, pr);
    VERTEX = grass_bend(VERTEX, rise, f, lean);
}
void fragment() {
    vec2 xz = rest.xz;
    vec4 pr = press_at(xz);
    float here = grass_here(xz) * (1.0 - pr.a);
    if (here < 0.5) discard;
    float patch = grass_patch(xz);
    // a blade to a cell of 0.14 m: its height from a hash and the patch, its
    // round shrinking to the tip
    float cell = 0.14 * ppm;
    vec2 g = xz / cell;
    vec2 id = floor(g);
    vec2 q = fract(g) - 0.5 - (vec2(g_hash(id), g_hash(id + 17.0)) - 0.5) * 0.5;
    float h = (0.35 + 0.65 * g_hash(id + 5.0)) * (0.65 + 0.5 * patch);
    // pressed: lying down, so what stands of it is short
    h *= 1.0 - 0.7 * pr.r;
    // down to a quarter at the rim: at full height the edge stood up as a slab
    h *= 0.25 + 0.75 * smoothstep(0.5, 1.0, here);
    if (f > 0.0) {
        if (f > h) discard;
        if (length(q) > 0.55 * (1.0 - f / h)) discard;
    }
    vec3 c = grass_tone(f, patch, xz, pr.r, g_hash(id + 2.0));
    ALBEDO = c;
    EMISSION = c * (1.0 - shadow_ink);
}
" + LitCode,
    };

    /// <summary>The tufts on the models' ramp: <see cref="Toon.RampCode"/>'s
    /// three steps and its shade tone, the paint scaled by <c>paint</c> so its
    /// lit step comes out near the flat tufts' colour. Its cast shadow steps
    /// down to the shade tone as a crown's does. Both sides lit by the mesh's
    /// normal - a blade is an open sheet, as a leaf is (Toon's leaf shader).</summary>
    private static readonly Shader CelTuftShader = new()
    {
        Code = @"
shader_type spatial;
render_mode cull_disabled, ambient_light_disabled, specular_disabled, fog_disabled;
uniform float paint = 0.85;
" + GrassCode + Toon.RampCode + @"
varying float t;
varying float own;
varying float pressed;
varying vec2 root;
void vertex() {
    t = UV.x;
    own = UV.y;
    root = UV2;
    vec4 pr = press_at(root);
    pressed = pr.r;
    float rise = (VERTEX.y - hex_mid.y) * (1.0 - pr.a);
    vec2 lean = grass_lean(root, own * 6.28, pr);
    VERTEX = grass_bend(VERTEX, rise, t, lean);
}
void fragment() {
    if (!FRONT_FACING) { NORMAL = -NORMAL; }
    sooted = 0.0;
    vec3 c = grass_tone(t, grass_patch(root), root, pressed, own) * paint;
    ALBEDO = c;
    EMISSION = c * shade;
}
",
    };

    private static readonly Shader TuftShader = new()
    {
        Code = @"
shader_type spatial;
render_mode cull_disabled, ambient_light_disabled, specular_disabled, fog_disabled;
" + GrassCode + @"
varying float t;
varying float own;
varying float pressed;
varying vec2 root;
void vertex() {
    t = UV.x;
    own = UV.y;
    root = UV2;
    vec4 pr = press_at(root);
    pressed = pr.r;
    float rise = (VERTEX.y - hex_mid.y) * (1.0 - pr.a);
    vec2 lean = grass_lean(root, own * 6.28, pr);
    VERTEX = grass_bend(VERTEX, rise, t, lean);
}
void fragment() {
    vec3 c = grass_tone(t, grass_patch(root), root, pressed, own);
    ALBEDO = c;
    EMISSION = c * (1.0 - shadow_ink);
}
" + LitCode,
    };
}
