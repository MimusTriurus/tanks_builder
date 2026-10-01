using Godot;

namespace TankSpriteTest;

/// <summary>
/// Smoke, dust or steam in the model's look as <b>one cloud</b>: puffs handed
/// in as discs, drawn on one quad facing the eye and flowed into one shape by
/// a smooth union of their distances, with one ink line round it - the
/// burning column (<see cref="CelBurn"/>), the exhaust (<see cref="CelExhaust"/>),
/// the shot (<see cref="CelShot"/>) and the rest. Drawn one by one as
/// spheres, each inked on its own, the same puffs were a heap of cartoon
/// balls.
///
/// A frame is <see cref="Clear"/>, an <see cref="Add"/> a puff, then
/// <see cref="Draw"/>. The puffs' motion is the owner's; this knows only
/// where each is this frame, how big, its grey, and how far it is eaten.
/// </summary>
public sealed class CelCloud
{
    /// <summary>Puffs in one cloud at most - the shader's arrays.</summary>
    public const int Pool = 64;

    private readonly MeshInstance3D _quad;
    private readonly ShaderMaterial _look;
    private readonly Vector4[] _where = new Vector4[Pool];
    private readonly Vector4[] _looks = new Vector4[Pool];
    private readonly float _blend;
    private int _n;

    /// <param name="tint">The colour the puffs' grey is taken in.</param>
    /// <param name="blendPx">How far apart two puffs still flow into one, px.</param>
    public CelCloud(Node parent, string name, Color tint, float blendPx)
    {
        _blend = blendPx;
        _look = new ShaderMaterial { Shader = Shader };
        _look.SetShaderParameter("tint", tint);
        _look.SetShaderParameter("blend", blendPx);
        _look.SetShaderParameter("ink_width", Toon.InkWidth);
        _look.SetShaderParameter("ink_min_px", Toon.InkMinPx);
        // The ramp's steps sharper than the model's: the cloud's normal turns
        // slowly across a big puff, and the model's width of step was a soft
        // band ten pixels wide round every lit patch of a burning column.
        _look.SetShaderParameter("soft", 0.008f);
        _quad = new MeshInstance3D
        {
            Name = name,
            Mesh = new QuadMesh { Size = Vector2.One },
            MaterialOverride = _look,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Visible = false,
        };
        parent.AddChild(_quad);
    }

    public void Clear() => _n = 0;

    public void Hide()
    {
        _n = 0;
        _quad.Visible = false;
    }

    /// <summary>A puff this frame: its middle in the world, its radius, px,
    /// its grey, a seed for its lumps, how far it is eaten (0..1) and its age
    /// as a share of its life. Past <see cref="Pool"/> it is dropped.</summary>
    public void Add(Vector3 at, float r, float tone, float seed, float erode, float age)
    {
        if (_n >= Pool)
            return;
        _where[_n] = new Vector4(at.X, at.Y, at.Z, r);
        _looks[_n] = new Vector4(tone, seed, erode, age);
        _n++;
    }

    /// <summary>The puffs added since <see cref="Clear"/>, facing
    /// <paramref name="eye"/> (the camera's basis), each thinned by
    /// <paramref name="solids"/> - the tanks it may not stand through.</summary>
    public void Draw(Basis eye, CelSolids? solids = null)
    {
        if (_n == 0)
        {
            _quad.Visible = false;
            return;
        }
        if (solids is not null)
            for (int i = 0; i < _n; i++)
                _where[i].W *= solids.Thin(new Vector3(_where[i].X, _where[i].Y, _where[i].Z), _where[i].W);
        Vector3 right = eye.X.Normalized(), up = eye.Y.Normalized(), back = eye.Z.Normalized();
        Vector3 mid = Vector3.Zero;
        for (int i = 0; i < _n; i++)
            mid += new Vector3(_where[i].X, _where[i].Y, _where[i].Z);
        mid /= _n;
        // The quad: facing the eye over every puff, its lumps and the ink,
        // in front of them all (its own depth is not used - the shader writes
        // the cloud's).
        float wide = 0.0f, tall = 0.0f, front = 0.0f;
        for (int i = 0; i < _n; i++)
        {
            Vector3 at = new(_where[i].X, _where[i].Y, _where[i].Z);
            float reach = _where[i].W * 1.4f + _blend + 2.0f;
            wide = Mathf.Max(wide, Mathf.Abs((at - mid).Dot(right)) + reach);
            tall = Mathf.Max(tall, Mathf.Abs((at - mid).Dot(up)) + reach);
            front = Mathf.Max(front, (at - mid).Dot(back) + _where[i].W);
        }
        _quad.GlobalTransform = new Transform3D(
            new Basis(right * (2.0f * wide), up * (2.0f * tall), back), mid + back * front);
        _quad.Visible = true;
        _look.SetShaderParameter("puffs", _where);
        _look.SetShaderParameter("looks", _looks);
        _look.SetShaderParameter("count", _n);
    }

    /// <summary>
    /// The cloud: every puff a disc on one quad facing the eye, and the discs
    /// flowed into one shape - a smooth union of their distances (<c>blend</c>
    /// px wide), so the exhaust has an outline of its own and one ink line
    /// round it. Each disc's rim is made lumpy by a noise in its own frame, so
    /// the lumps go with the puff, and it is eaten as it ages by a noise across
    /// it. The light is the model's ramp on a normal blended from the puffs'
    /// spheres with the union's weights, so the sun steps one lit top and one
    /// shaded foot over the whole cloud; its depth is the blended front of the
    /// same spheres, so the turret still hides what is behind it.
    /// </summary>
    public static readonly Shader Shader = new()
    {
        Code = @"
shader_type spatial;
render_mode cull_disabled, specular_disabled, ambient_light_disabled, shadows_disabled;
stencil_mode write, compare_always, 0;
uniform vec4 puffs[" + Pool + @"];
uniform vec4 looks[" + Pool + @"];
uniform int count = 0;
uniform float blend = 6.0;
uniform vec3 tint : source_color = vec3(0.9, 0.94, 1.0);
uniform float ink_width = 1.1;
uniform float ink_min_px = 1.0;
uniform float ink_dark = 0.4;
" + Toon.NoiseCode + Toon.RampCode + @"
void fragment() {
    vec2 p = VERTEX.xy;
    float sd = 1e9;
    float wsum = 0.0;
    float zsum = 0.0;
    float tsum = 0.0;
    vec3 nsum = vec3(0.0);
    float px = 2.0 / (PROJECTION_MATRIX[1][1] * VIEWPORT_SIZE.y);
    float ink = max(ink_width, ink_min_px * px);
    for (int i = 0; i < " + Pool + @"; i++) {
        if (i >= count) break;
        vec3 c = (VIEW_MATRIX * vec4(puffs[i].xyz, 1.0)).xyz;
        float r = max(puffs[i].w, 1e-3);
        vec4 lk = looks[i];
        vec2 q = p - c.xy;
        float len = length(q);
        vec2 u = q / max(len, 1e-4);
        // Eaten as it ages, from the rim in, by a noise round it: holes cut
        // through the middle, each inked, read as cheese.
        float bite = noise3(vec3(u * 2.2 + lk.y * 31.0, lk.w * 1.2));
        // A share of what is left, not of the whole puff: bitten by a share of
        // the whole, a puff nearly gone was ten times wider one way than the
        // other - a star, inked.
        float left = r * (1.0 - lk.z) * (1.0 + 0.3 * (bite - 0.5));
        // Gone, rather than a dot of ink.
        if (left < 2.0 * ink) continue;
        // Lumps round the rim, in the puff's own frame so they go with it, and
        // of what is left of it: of the whole puff, the last of a cloud went
        // to inked splinters.
        float bump = noise3(vec3(u * 1.4 + lk.y * 17.0, lk.w * 1.5)) - 0.5;
        float di = len - left * (1.0 + 0.35 * bump);
        // Round in the middle: the bite and the lumps are noises of the way
        // round, which has no way at the centre, and the weights below took
        // their jumps there - a pale star in the middle of a big puff.
        float plain = r * (1.0 - lk.z);
        di = mix(len - plain, di, smoothstep(0.2, 0.7, len / max(plain, 1e-3)));
        float h = sqrt(max(0.0, 1.0 - len * len / (r * r)));
        float w = exp(-clamp(di, -r, 3.0 * blend) / blend);
        nsum += normalize(vec3(q / r, max(h, 0.2))) * w;
        zsum += (c.z + r * h) * w;
        tsum += lk.x * w;
        wsum += w;
        float k = clamp(0.5 + 0.5 * (di - sd) / blend, 0.0, 1.0);
        sd = mix(di, sd, k) - blend * k * (1.0 - k);
    }
    if (sd > 0.0 || wsum <= 0.0) discard;
    float tone = tsum / wsum;
    vec4 clip = PROJECTION_MATRIX * vec4(p, zsum / wsum, 1.0);
    DEPTH = clip.z / clip.w * 0.5 + 0.5;
    vec3 col = vec3(tone) * tint;
    if (sd > -ink) {
        ALBEDO = vec3(0.0);
        EMISSION = col * ink_dark;
    } else {
        ALBEDO = col;
        EMISSION = col * shade;
        NORMAL = normalize(nsum);
    }
}
",
    };
}
