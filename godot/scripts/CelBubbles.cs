using System;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The air a drowned 3D tank lets go of, in the model's look - the board's
/// <see cref="Bubbles"/> for the sprites, whose pop is a soft white blur off
/// a texture. Same stream: nothing while any of the tank still shows, a
/// steady <see cref="Bubbles.Seep"/> once the water is over it, for good, and
/// every pop dents the ripple field (<see cref="Struck"/>).
///
/// <list type="bullet">
/// <item><b>A pop</b> arrives as a dome - a disc of foam with the water's ink
/// round it and a crescent of the pale water on its far side from the light -
/// swelling for a third of its life, then breaks: the dome is gone and a flat
/// ring of foam runs out from where it stood, thinning to a line, no ink (the
/// rings on the water have none - ratel's lesson, <see cref="CelSplash"/>).
/// Two flat bands each, like everything else on the water.</item>
/// <item><b>Over the hull, on its cell.</b> The air comes straight up: each
/// pop is on the surface above a point of the hull's footprint. The board's
/// stream set the pops along the eye's ray from the deck - over the tank on
/// the screen, but on the cell in front of its own in the world, a hex nearer
/// the eye than the tank that drowned (the user caught it).</item>
/// </list>
/// Stepped from a seeded generator, as the board's: at a pinned step the
/// stream is the same stream twice.
/// </summary>
public sealed partial class CelBubbles : Node3D
{
    /// <summary>How long one pop lives, s - a little longer than the board's
    /// third of a second: the dome is a shape to be read, not a glint.</summary>
    public const float Life = 0.5f;

    /// <summary>The share of its life a pop is a dome before it breaks.</summary>
    public const float Breaks = 0.32f;

    public const int Most = 24;

    /// <summary>Where the ripple field is struck, world X and Z, and how hard.</summary>
    public Action<Vector2, float>? Struck;

    private record struct Pop(float Born, Vector2 Ground, float Size, float Seed);

    private readonly Pop[] _pops = new Pop[Most];
    private int _next;
    private MultiMesh? _many;
    private MultiMeshInstance3D? _sheet;
    private readonly RandomNumberGenerator _dice = new();
    private float _clock, _due, _sink;
    private Vector3 _seat;
    private Vector3 _along = Vector3.Back, _across = Vector3.Right;
    private float _halfLen = 60.0f, _halfWide = 33.0f, _size = 1.0f;
    private float _squash = 0.5f, _rise = 0.86f;

    public void Build(ulong seed, float squash, float rise)
    {
        _dice.Seed = seed;
        _squash = squash;
        _rise = rise;
        var look = new ShaderMaterial { Shader = PopShader, RenderPriority = Stage3D.DressOrder };
        look.SetShaderParameter("foam", CelSplash.Crest);
        look.SetShaderParameter("wash", CelSplash.Body);
        look.SetShaderParameter("ink", CelSplash.Ink);
        _many = new MultiMesh
        {
            Mesh = new PlaneMesh { Size = Vector2.One * 2.0f },
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseCustomData = true,
            InstanceCount = Most,
        };
        _sheet = new MultiMeshInstance3D
        {
            Name = "Pops", Multimesh = _many, MaterialOverride = look,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            // After the pond, the rings' reason (CelSplash).
            SortingOffset = -100000.0f, Visible = false,
        };
        AddChild(_sheet);
        for (int k = 0; k < Most; k++)
            _pops[k] = new Pop(float.NegativeInfinity, Vector2.Zero, 0.0f, 0.0f);
    }

    /// <summary>Where the hull lies: <paramref name="seat"/> the water's
    /// surface straight over its middle (world), the way it points and its
    /// half-lengths, world px. Every frame - it is still settling.</summary>
    public void Sit(Vector3 seat, Vector3 ahead, float halfLen, float halfWide)
    {
        _seat = seat;
        var flat = new Vector3(ahead.X, 0.0f, ahead.Z);
        _along = flat.LengthSquared() > 1e-6f ? flat.Normalized() : Vector3.Back;
        _across = new Vector3(_along.Z, 0.0f, -_along.X);
        _halfLen = Mathf.Max(halfLen, 1.0f);
        _halfWide = Mathf.Max(halfWide, 1.0f);
        _size = Mathf.Clamp(_halfWide / 33.0f, 0.6f, 1.6f);
    }

    /// <summary>How far under the hull is, nought to one.</summary>
    public void Feed(float sink) => _sink = sink;

    public bool Busy => Bubbles.Rate(_sink) > 0.0f || _clock - Latest() < Life;

    private float Latest()
    {
        float latest = float.NegativeInfinity;
        foreach (Pop pop in _pops)
            latest = Mathf.Max(latest, pop.Born);
        return latest;
    }

    public void Reset()
    {
        _sink = 0.0f;
        _due = 0.0f;
        for (int k = 0; k < Most; k++)
            _pops[k] = new Pop(float.NegativeInfinity, Vector2.Zero, 0.0f, 0.0f);
        if (_sheet is not null)
            _sheet.Visible = false;
    }

    public void Tick(float dt)
    {
        _clock += dt;
        _due += Bubbles.Rate(_sink) * _size * dt;
        while (_due >= 1.0f)
        {
            _due -= 1.0f;
            Burst();
        }
        Dress();
    }

    private void Burst()
    {
        // Inside the footprint, denser toward the middle - the fighting
        // compartment is where the air is - small, the odd one bigger.
        float u = (_dice.Randf() + _dice.Randf() - 1.0f) * 0.85f;
        float v = (_dice.Randf() + _dice.Randf() - 1.0f) * 0.9f;
        var ground = new Vector2(_halfLen * u, _halfWide * v);
        float pick = _dice.Randf();
        float size = (2.5f + 2.5f * pick + (pick > 0.85f ? 3.0f : 0.0f)) * _size;
        _pops[_next] = new Pop(_clock, ground, size, _dice.Randf() * 17.0f);
        _next = (_next + 1) % Most;
        if (Struck is not null)
        {
            Vector3 at = _seat + _along * ground.X + _across * ground.Y;
            Struck(new Vector2(at.X, at.Z), Stage3D.Waves * Bubbles.Dent * size / 5.0f);
        }
    }

    private static readonly Transform3D Gone = new(Basis.Identity.Scaled(Vector3.Zero), Vector3.Zero);

    private void Dress()
    {
        if (_many is null || _sheet is null)
            return;
        bool any = false;
        Vector3 lift = Stage3D.Clear(_squash, _rise);
        for (int k = 0; k < Most; k++)
        {
            Pop pop = _pops[k];
            float age = (_clock - pop.Born) / Life;
            if (age < 0.0f || age >= 1.0f)
            {
                _many.SetInstanceTransform(k, Gone);
                continue;
            }
            any = true;
            // The quad holds the ring at its widest, 1.8 sizes out: at 2.4,
            // thin, the rings over a heavy were targets on the water.
            float s = pop.Size * 1.8f;
            Vector3 at = _seat + _along * pop.Ground.X + _across * pop.Ground.Y + lift;
            _many.SetInstanceTransform(k, new Transform3D(Basis.Identity.Scaled(new Vector3(s, 1.0f, s)), at));
            _many.SetInstanceCustomData(k, new Color(age, pop.Seed, pop.Size, 0.0f));
        }
        _sheet.Visible = any;
    }

    /// <summary>
    /// One pop on a quad flat on the water, local -1..1 (1.8 sizes): the dome
    /// to <see cref="Breaks"/> - a disc swelling from 0.36 to half the quad,
    /// foam with a pale crescent and the water's ink round it, the ink's width
    /// a share of the disc but never under a pixel - then the ring, from the
    /// disc's edge out to the quad's, its band thinning from a quarter of the
    /// quad to a fourteenth, uneven round by a noise, foam outside and pale
    /// water inside, no ink.
    /// </summary>
    private static readonly Shader PopShader = new()
    {
        Code = @"
shader_type spatial;
render_mode unshaded, blend_mix, depth_draw_never, cull_disabled, shadows_disabled;
uniform vec3 foam : source_color;
uniform vec3 wash : source_color;
uniform vec3 ink : source_color;
" + Toon.NoiseCode + @"
varying vec2 local;
varying vec4 mine;
void vertex() {
    local = VERTEX.xz;
    mine = INSTANCE_CUSTOM;
}
void fragment() {
    float t = mine.r;
    float r = length(local);
    float px = max(fwidth(r), 1e-4);
    if (t < " + Breaks.ToString(System.Globalization.CultureInfo.InvariantCulture) + @") {
        float k = t / " + Breaks.ToString(System.Globalization.CultureInfo.InvariantCulture) + @";
        float big = mix(0.36, 0.5, sqrt(k));
        if (r > big) discard;
        float line = max(0.16 * big, 1.1 * px);
        vec2 off = local - vec2(-0.35, -0.35) * big;
        ALBEDO = r > big - line ? ink : (length(off) > big * 0.95 ? wash : foam);
    } else {
        float k = (t - " + Breaks.ToString(System.Globalization.CultureInfo.InvariantCulture) + @") / (1.0 - " + Breaks.ToString(System.Globalization.CultureInfo.InvariantCulture) + @");
        float ease = 1.0 - (1.0 - k) * (1.0 - k);
        vec2 around = local / max(r, 1e-4);
        float at = mix(0.5, 1.0, ease) + (noise3(vec3(around * 2.0, mine.g)) - 0.5) * 0.08;
        float band = max(mix(0.26, 0.07, k), 1.1 * px);
        float d = r - at;
        if (d > 0.0 || d < -band) discard;
        ALBEDO = d > -band * 0.5 ? foam : wash;
    }
    ALPHA = 1.0;
}
",
    };
}
