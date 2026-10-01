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
/// <b>It burns as decoration</b> (<see cref="Burn"/>): the scene hands in a
/// cell's fire clock and where the fire came into it from, and a front runs
/// over the grass from there - a flame line, char and stubble behind it, embers
/// going out, a pale smoke off the line. Whether a cell burns is the fire's
/// (the wood's, <c>Wildfire</c>): grass is not fuel in the rules.
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

    /// <summary>How fast the fire runs over grass, m/s: across a cell in about
    /// the three seconds its trees take to catch (<c>Wildfire.CatchWithin</c>),
    /// so the grass is gone under a crown by the time it is in flame.</summary>
    public const float FrontM = 4.5f;
    /// <summary>How long the flame stands at a point once the front is over
    /// it, s (the shaders' <c>flame_for</c>): the tufts and the lawn flare up
    /// for this, the painted ground under them glows a line for
    /// <see cref="FlameLine"/>. At 0.7 on both the front was a flat orange
    /// band three metres deep.</summary>
    public const float FlameFor = 0.45f, FlameLine = 0.22f;
    /// <summary>The smoke off the front: a puff every this many s while the
    /// line is on the cell, each living <see cref="SmokeLife"/>.</summary>
    private const float SmokeEvery = 0.05f, SmokeLife = 1.8f;

    /// <summary>The press map's cell, board px.</summary>
    private const float Texel = 2.0f;
    /// <summary>Over the ground by this: the pit is at 0.4, the shadow skin at 2.</summary>
    private const float Lift = 1.0f;
    /// <summary>Pressed grass comes back up at this much a second, as far as
    /// <see cref="Keep"/>: a hull's lane stays in it.</summary>
    private const float Relax = 1.0f / 25.0f, Keep = 0.45f;

    private readonly List<ShaderMaterial> _inks = new();

    /// <summary>A cell of grass, for its fire: its materials, where it is, and
    /// its front's smoke.</summary>
    private sealed class Meadow
    {
        public readonly List<ShaderMaterial> Inks = new();
        public Vector3 Mid;
        public float Radius, Age = -1.0f, Reach, Due, Seed, Speed, Stop = float.PositiveInfinity;
        public Vector2 From;
        public CelCloud? Smoke;
        public readonly List<(Vector3 At, float Born, float Seed)> Puffs = new();
    }

    private readonly List<Meadow> _meadows = new();
    private Meadow? _laying;
    private readonly RandomNumberGenerator _rng = new() { Seed = 7_331 };
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
    /// keeps the tufts off where something stands. Returns the meadow's
    /// number, for <see cref="Burn"/>.
    /// </summary>
    public int Lay(Kind kind, Vector3 mid, float radius, int seed, IEnumerable<Vector3>? clear = null)
    {
        _laying = new Meadow { Mid = mid, Radius = radius, Seed = (seed % 97) * 1.37f, Speed = FrontM * Ppm };
        _meadows.Add(_laying);
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
        _laying = null;
        return _meadows.Count - 1;
    }

    /// <summary>
    /// Meadow <paramref name="meadow"/>'s fire this frame: <paramref name="age"/>
    /// s since its cell was lit (the fire's clock; below 0 not lit, and the
    /// grass stands again), and <paramref name="from"/> (x, z) where the front
    /// starts - the edge the fire came in over, or where it was lit. The front
    /// is a ring out from there at <see cref="FrontM"/>, broken up by a noise -
    /// faster where it has to be off the cell's far corner within
    /// <paramref name="within"/> s (0: no bound), fixed on the frame it is lit.
    /// <paramref name="stop"/>, px from <paramref name="from"/>, is as far as it
    /// goes - on a ragged line, the grass past it untouched: a blast's fire
    /// wave burning the grass it rolls over and no further (the scene's;
    /// infinity, a fire that takes the cell). The far corner is then the stop.
    /// </summary>
    public void Burn(int meadow, float age, Vector2 from, float within = 0.0f, float stop = float.PositiveInfinity)
    {
        if (meadow < 0 || meadow >= _meadows.Count)
            return;
        Meadow m = _meadows[meadow];
        if (age < 0.0f)
        {
            if (m.Age >= 0.0f)
            {
                m.Puffs.Clear();
                m.Smoke?.Hide();
            }
            m.Age = -1.0f;
        }
        else
        {
            if (m.Age < 0.0f)
            {
                // the far side of the cell from the start: when the line is off it
                float far = 0.0f;
                for (int k = 0; k < 6; k++)
                {
                    float a = Mathf.Tau * k / 6.0f;
                    var corner = new Vector2(m.Mid.X + Mathf.Cos(a) * m.Radius, m.Mid.Z + Mathf.Sin(a) * m.Radius);
                    far = Mathf.Max(far, corner.DistanceTo(from));
                }
                m.Stop = stop;
                m.Reach = Mathf.Min(far, stop);
                far = m.Reach;
                m.Due = 0.0f;
                m.Speed = Mathf.Max(FrontM * Ppm, within > 0.0f ? far / within : 0.0f);
            }
            m.Age = age;
            m.From = from;
        }
        foreach (ShaderMaterial ink in m.Inks)
        {
            ink.SetShaderParameter("burn_age", m.Age);
            ink.SetShaderParameter("burn_from", m.From);
            ink.SetShaderParameter("burn_speed", m.Speed);
            ink.SetShaderParameter("burn_reach", float.IsInfinity(m.Stop) ? 1e9f : m.Stop);
        }
    }

    /// <summary>
    /// When meadow <paramref name="meadow"/>'s front reaches
    /// <paramref name="xz"/>, s after its cell was lit - the shaders'
    /// <c>burn_tau</c> the other way round, its ragged noise and all, so a
    /// tree lit by it (the scene's) catches as the flame line gets to its foot.
    /// Meaningful once <see cref="Burn"/> has been told the cell is lit.
    /// </summary>
    public float Arrival(int meadow, Vector2 xz)
    {
        if (meadow < 0 || meadow >= _meadows.Count)
            return 0.0f;
        Meadow m = _meadows[meadow];
        float rag = (Noise3(new Vector3(xz.X * 0.035f + 7.0f, xz.Y * 0.035f + 7.0f, m.Seed)) - 0.5f) * 2.4f * Ppm
                    + (Noise3(new Vector3(xz.X * 0.16f + 2.0f, xz.Y * 0.16f + 2.0f, m.Seed)) - 0.5f) * 0.6f * Ppm;
        return Mathf.Max(xz.DistanceTo(m.From) + rag, 0.0f) / Mathf.Max(m.Speed, 1e-3f);
    }

    /// <summary><c>Toon.NoiseCode</c>'s <c>hash3</c> and <c>noise3</c>, on the
    /// CPU, for <see cref="Arrival"/>.</summary>
    private static float Hash3(Vector3 p)
    {
        static float Fract(float v) => v - Mathf.Floor(v);
        p = new Vector3(Fract(p.X * 0.3183099f + 0.71f), Fract(p.Y * 0.3183099f + 0.113f),
                        Fract(p.Z * 0.3183099f + 0.419f)) * 17.0f;
        return Fract(p.X * p.Y * p.Z * (p.X + p.Y + p.Z));
    }

    private static float Noise3(Vector3 x)
    {
        var i = new Vector3(Mathf.Floor(x.X), Mathf.Floor(x.Y), Mathf.Floor(x.Z));
        Vector3 f = x - i;
        f = f * f * (Vector3.One * 3.0f - 2.0f * f);
        float H(float a, float b, float c) => Hash3(i + new Vector3(a, b, c));
        return Mathf.Lerp(Mathf.Lerp(Mathf.Lerp(H(0, 0, 0), H(1, 0, 0), f.X), Mathf.Lerp(H(0, 1, 0), H(1, 1, 0), f.X), f.Y),
                          Mathf.Lerp(Mathf.Lerp(H(0, 0, 1), H(1, 0, 1), f.X), Mathf.Lerp(H(0, 1, 1), H(1, 1, 1), f.X), f.Y),
                          f.Z);
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
        foreach (Meadow m in _meadows)
            Fume(m, dt);
    }

    /// <summary>
    /// The smoke off a meadow's front: pale puffs born on the flame line while
    /// it is on the cell, rising two metres and a half, drifting downwind,
    /// eaten as they go - one cloud a cell (<see cref="CelCloud"/>), in the
    /// look of the dust and the tank's smoke. Grass smoke is thin and light;
    /// the wood's dark column is the trees'.
    /// </summary>
    private void Fume(Meadow m, float dt)
    {
        float speed = m.Speed;
        if (m.Age >= 0.0f && m.Age * speed < m.Reach + FlameFor * speed)
        {
            m.Smoke ??= new CelCloud(this, "GrassSmoke", new Color(0.80f, 0.80f, 0.78f), 7.0f);
            m.Due -= dt;
            while (m.Due <= 0.0f)
            {
                m.Due += SmokeEvery;
                float ring = Mathf.Max(m.Age * speed - 0.3f * Ppm, 0.0f);
                for (int tries = 0; tries < 8; tries++)
                {
                    float a = _rng.RandfRange(0.0f, Mathf.Tau);
                    var at = new Vector2(m.From.X + Mathf.Cos(a) * ring, m.From.Y + Mathf.Sin(a) * ring);
                    if (HexIn(at - new Vector2(m.Mid.X, m.Mid.Z), m.Radius) < FadeM * Ppm)
                        continue;
                    m.Puffs.Add((new Vector3(at.X, m.Mid.Y, at.Y), _clock, _rng.Randf() * 13.0f));
                    break;
                }
            }
        }
        if (m.Smoke is null)
            return;
        m.Smoke.Clear();
        for (int i = m.Puffs.Count - 1; i >= 0; i--)
        {
            (Vector3 at, float born, float seed) = m.Puffs[i];
            float a = (_clock - born) / SmokeLife;
            if (a >= 1.0f)
            {
                m.Puffs.RemoveAt(i);
                continue;
            }
            var drift = new Vector3(WindWay.X, 0.0f, WindWay.Y) * (1.2f * a * Wind * Ppm);
            // off the ground at once and up fast: born low and slow, the
            // puffs lay over the char as grey stones
            Vector3 up = Vector3.Up * ((0.6f + 4.0f * Mathf.Sqrt(a)) * Ppm);
            float r = (0.12f + 0.45f * Mathf.Sqrt(a)) * Ppm;
            m.Smoke.Add(at + up + drift, r, 0.62f - 0.06f * a, seed, Mathf.SmoothStep(0.2f, 1.0f, a), a);
        }
        if (m.Puffs.Count == 0)
            m.Smoke.Hide();
        else if (GetViewport()?.GetCamera3D() is { } eye)
            m.Smoke.Draw(eye.GlobalBasis);
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
        ink.SetShaderParameter("seed", _laying?.Seed ?? (seed % 97) * 1.37f);
        ink.SetShaderParameter("shadow_ink", ShadowInk);
        ink.SetShaderParameter("gust_rate", GustRate);
        ink.SetShaderParameter("gust_travel", GustTravel);
        ink.SetShaderParameter("ppm", Ppm);
        ink.SetShaderParameter("burn_speed", _laying?.Speed ?? FrontM * Ppm);
        ink.SetShaderParameter("flame_for", shader == PaintedShader ? FlameLine : FlameFor);
        _laying?.Inks.Add(ink);
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
// the fire: s since the cell was lit (-1 never), where the front starts, how
// fast it runs, px/s; and what it leaves
uniform float burn_age = -1.0;
uniform vec2 burn_from = vec2(0.0);
uniform float burn_speed = 76.0;
// as far from burn_from as the front goes, px, on a ragged line: a blast's wave
uniform float burn_reach = 1e9;
uniform vec3 char_tone : source_color = vec3(0.15, 0.13, 0.11);
uniform vec3 ash_tone : source_color = vec3(0.27, 0.26, 0.24);
// how long the flame stands at a point, s: the tufts and the lawn flare up as
// tongues; the painted ground under them only glows a line
uniform float flame_for = 0.45;
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
// s since the front passed here; below 0 not yet, -1000 no fire. The ring is
// broken up by a noise of a few metres, so it arrives as a ragged line
float burn_tau(vec2 xz) {
    if (burn_age < 0.0) return -1000.0;
    float rag = (g_noise(xz * 0.035 + 7.0) - 0.5) * 2.4 * ppm + (g_noise(xz * 0.16 + 2.0) - 0.5) * 0.6 * ppm;
    float d = length(xz - burn_from);
    if (d > burn_reach + ((g_noise(xz * 0.06 + 13.0) - 0.5) * 1.6 + (g_noise(xz * 0.25 + 4.0) - 0.5) * 0.5) * ppm)
        return -1000.0;
    return burn_age - max(d + rag, 0.0) / burn_speed;
}
// how much of its height the grass keeps: all of it ahead of the front, a
// flickering flare in the flame, stubble behind it
float burn_keep(float tau, float phase) {
    if (tau < 0.0) return 1.0;
    if (tau < flame_for)
        return 1.25 + 0.35 * sin(g_time * 19.0 + phase * 6.28) * (1.0 - tau / flame_for);
    return mix(1.0, 0.22, smoothstep(flame_for, flame_for + 0.8, tau));
}
// the fire's paint over the grass's own: .a is 1 where it glows - the flame
// and the embers, which the sun's shadow does not dim. Steps, not a blend: the
// flame in three tones as the tank's is, orange at the leading edge, yellow
// in the middle, red going out; then char, and later ash over it in two low
// steps, the stubble's tips a shade lighter than its foot
vec4 burn_paint(vec3 c, float tau, vec2 xz, float t, float flame) {
    if (tau < -0.6) return vec4(c, 0.0);
    if (tau < 0.0) return vec4(mix(c, tone_dry, 0.5 * (1.0 + tau / 0.6)), 0.0);
    if (tau < flame) {
        float k = tau / flame + (g_noise(xz * 0.3 + vec2(0.0, g_time * 3.0)) - 0.5) * 0.35 - t * 0.25;
        vec3 fl = k < 0.25 ? vec3(1.0, 0.56, 0.12) : (k < 0.62 ? vec3(1.0, 0.90, 0.50) : vec3(0.82, 0.24, 0.06));
        return vec4(fl, 1.0);
    }
    // embers: a few round specks glowing on behind the line, out one by one
    float cell = 0.3 * ppm;
    vec2 id = floor(xz / cell);
    float h = g_hash(id + 61.0);
    vec2 in_cell = fract(xz / cell) - 0.5;
    if (h > 0.955 && length(in_cell) < 0.28 && tau < flame + 0.5 + 2.5 * g_hash(id + 67.0))
        return vec4(sin(g_time * 6.0 + h * 40.0) > 0.0 ? vec3(1.0, 0.55, 0.12) : vec3(0.85, 0.25, 0.05), 1.0);
    // one step of paler ash, in patches a couple of metres across: two steps
    // at a metre read as camouflage
    float n = g_noise(xz * 0.035 + 5.0);
    float a = smoothstep(2.0, 8.0, tau) * 0.7 * step(0.52, n);
    return vec4(mix(char_tone, ash_tone, a) * (0.9 + 0.35 * t), 0.0);
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
    ALBEDO = burn_paint(c, burn_tau(xz), xz, t, flame_for).rgb;
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
    float keep = burn_keep(burn_tau(VERTEX.xz), g_noise(VERTEX.xz * 0.2));
    float rise = f * tall * (1.0 - pr.a) * keep;
    vec2 lean = grass_lean(VERTEX.xz, g_noise(VERTEX.xz * 0.05) * 6.28, pr) * min(keep, 1.0);
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
    // burnt down to stubble; in the flame the layers are stretched instead
    float tau = burn_tau(xz);
    h *= min(burn_keep(tau, 0.0), 1.0);
    if (f > 0.0) {
        if (f > h) discard;
        if (length(q) > 0.55 * (1.0 - f / h)) discard;
    }
    // the lawn's floor glows a line only, as the painted ground does: a
    // floor in flame for as long as the blades made the front a carpet
    vec4 b = burn_paint(grass_tone(f, patch, xz, pr.r, g_hash(id + 2.0)), tau, xz, f,
                        f > 0.0 ? flame_for : flame_for * 0.5);
    ALBEDO = b.a > 0.5 ? vec3(0.0) : b.rgb;
    EMISSION = b.a > 0.5 ? b.rgb : b.rgb * (1.0 - shadow_ink);
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
varying float tau;
void vertex() {
    t = UV.x;
    own = UV.y;
    root = UV2;
    vec4 pr = press_at(root);
    pressed = pr.r;
    // a tuft burns as one, at its root
    tau = burn_tau(root);
    float keep = burn_keep(tau, own);
    float rise = (VERTEX.y - hex_mid.y) * (1.0 - pr.a) * keep;
    vec2 lean = grass_lean(root, own * 6.28, pr) * min(keep, 1.0);
    VERTEX = grass_bend(VERTEX, rise, t, lean);
}
void fragment() {
    if (!FRONT_FACING) { NORMAL = -NORMAL; }
    sooted = 0.0;
    vec4 b = burn_paint(grass_tone(t, grass_patch(root), root, pressed, own), tau, root, t, flame_for);
    vec3 c = b.rgb * paint;
    ALBEDO = b.a > 0.5 ? vec3(0.0) : c;
    EMISSION = b.a > 0.5 ? b.rgb : c * shade;
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
varying float tau;
void vertex() {
    t = UV.x;
    own = UV.y;
    root = UV2;
    vec4 pr = press_at(root);
    pressed = pr.r;
    // a tuft burns as one, at its root
    tau = burn_tau(root);
    float keep = burn_keep(tau, own);
    float rise = (VERTEX.y - hex_mid.y) * (1.0 - pr.a) * keep;
    vec2 lean = grass_lean(root, own * 6.28, pr) * min(keep, 1.0);
    VERTEX = grass_bend(VERTEX, rise, t, lean);
}
void fragment() {
    vec4 b = burn_paint(grass_tone(t, grass_patch(root), root, pressed, own), tau, root, t, flame_for);
    ALBEDO = b.a > 0.5 ? vec3(0.0) : b.rgb;
    EMISSION = b.a > 0.5 ? b.rgb : b.rgb * (1.0 - shadow_ink);
}
" + LitCode,
    };
}
