using System;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The air a drowning hull lets go of, seen from above: not the bubbles on
/// their way up - on this board the water is forty px deep and a bubble under
/// it is a lighter pixel nobody would find - but where each one meets the
/// surface and breaks. A pop is a bright dome that opens into a ring and is gone
/// in a third of a second, and every pop dents the ripple field, so the water
/// over a sunk tank keeps shivering for as long as it lies there. docs/swim-plan.md, step 11.
///
/// <b>From the moment the water closes over the turret, and for good.</b> The
/// first cut gushed while the hull went down and trickled out inside a quarter
/// of a minute - the physics of a flooding compartment. The user wanted
/// neither: nothing while any of the tank still shows (the tank itself says it
/// is sinking), and a steady seep once it is under, kept up for as long as the
/// wreck lies there, because on a board where a drowned tank is a dark shape
/// under dark water the pops are the marker that says "a tank is down here".
/// So <see cref="Rate"/> is a step: nought below <see cref="TankTick.SunkAt"/>
/// = 1, <see cref="Seep"/> at it. Public so the check can read it.
///
/// <b>Stepped, not closed-form, and that is a difference from
/// <see cref="Plunge"/>.</b> A plunge is one event with a start; this is a
/// stream whose start is a tank's own clock, and its pops are drawn from a
/// seeded generator advanced once per pop. At a pinned step the stream is
/// still the same stream twice, which is all the captures need.
/// </summary>
public sealed partial class Bubbles : Node3D
{
    /// <summary>Pops a second off a sunk medium, for as long as it lies there;
    /// a heavy's footprint scales it up through <see cref="Sit"/>. Steady and
    /// sparse - a marker, not an event - but not so sparse that a frame misses
    /// it: at four a second a capture at frame 150 showed none (a pop lives a
    /// third of a second, so a quarter of all frames were empty); at six, one
    /// frame in nine.</summary>
    public const float Seep = 6.0f;

    /// <summary>How long one pop is on the surface: dome, ring, gone.</summary>
    public const float Life = 0.36f;

    /// <summary>The most pops on the surface at once: <see cref="Seep"/> ·
    /// <see cref="Life"/> is under two, a heavy's under three; the rest is
    /// headroom for a bench slider.</summary>
    public const int Most = 24;

    /// <summary>
    /// Pops a second for a hull this far under (<paramref name="sink"/>, nought
    /// to one - <see cref="TankTick.SunkAt"/>): nought until the water is over
    /// the last of it, <see cref="Seep"/> from then on, without end.
    /// </summary>
    public static float Rate(float sink) => sink >= 1.0f ? Seep : 0.0f;

    /// <summary>What one pop looks like <paramref name="age"/> seconds in, nought
    /// to <see cref="Life"/>: how many times its own size it is drawn, how
    /// opaque the ring is and how opaque the dome. A bubble arrives as a bright
    /// dome the size of itself; it breaks at a third of its life, the dome is
    /// gone and the ring it leaves runs out to twice the size while it thins
    /// away. Two alphas rather than one because the first cut drew dome and
    /// ring together for the whole life, and a dot in a ring is a reticle.</summary>
    public static (float grow, float ring, float dome) Look(float age)
    {
        float a = Mathf.Clamp(age / Life, 0.0f, 1.0f);
        float grow = 0.9f + 1.3f * Mathf.SmoothStep(0.25f, 1.0f, a);
        float dome = 0.85f * (1.0f - Mathf.SmoothStep(0.15f, 0.4f, a));
        float ring = 0.7f * Mathf.SmoothStep(0.2f, 0.35f, a)
                     * (1.0f - Mathf.SmoothStep(0.45f, 1.0f, a));
        return (grow, ring, dome);
    }

    /// <summary>What a pop does to the ripple field - a fraction of a plunge's
    /// <see cref="Stage3D.Waves"/>, so the water shivers rather than rings. The
    /// first figure, 0.08, at twenty-six pops a second chopped the surface into
    /// small fast waves and the glints on them flickered faster than the pond's
    /// own - read off the bench as the water "speeding up" while the hull went
    /// under (the user's question, step 11). A quarter of that: a pop is a
    /// bubble, not a stone. (That gush is gone since; the figure stays.)</summary>
    public const float Dent = 0.02f;

    /// <summary>Where the ripple field is struck, in the field's own ground
    /// coordinates, and how hard. Set by the stage; a stand without water has
    /// nothing to strike and leaves it null.</summary>
    public Action<Vector2, float>? Struck;

    // ------------------------------------------------------------- the machinery

    private record struct Pop(float Born, Vector2 Ground, float Size, float Turn);

    private readonly Pop[] _pops = new Pop[Most];
    private int _next;
    private MultiMeshInstance3D? _sheet;
    private MultiMesh? _many;
    private readonly RandomNumberGenerator _dice = new();
    private float _clock;
    private float _due;
    private float _sink;
    private Vector3 _along = Vector3.Right;
    private Vector3 _across = Vector3.Back;
    private float _halfLen = 60.0f;
    private float _halfWide = 33.0f;
    private float _size = 1.0f;

    private static Texture2D? RingArt;

    public void Build(ulong seed)
    {
        _dice.Seed = seed;
        var ink = new ShaderMaterial { Shader = Popping, RenderPriority = Stage3D.StandOrder + 1 };
        ink.SetShaderParameter("ring", RingArt ??= Ring());
        _many = new MultiMesh
        {
            Mesh = new QuadMesh { Size = Vector2.One },
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseCustomData = true,
            InstanceCount = Most,
        };
        _sheet = new MultiMeshInstance3D
        {
            Multimesh = _many,
            MaterialOverride = ink,
            SortingUseAabbCenter = false,
            Visible = false,
        };
        AddChild(_sheet);
        for (int k = 0; k < Most; k++)
            _pops[k] = new Pop(float.NegativeInfinity, Vector2.Zero, 0.0f, 0.0f);
    }

    /// <summary>
    /// Where the hull lies and how big it is: its contact point on the board,
    /// the water's height there, its screen-space ground heading and its two
    /// half-lengths in ground px. The pops come up inside that footprint. Given
    /// every frame, because a drowning hull is still settling on the bed.
    /// </summary>
    public void Sit(Vector2 ground, float top, Vector2 heading, float halfLen,
                    float halfWide, float squash, float rise)
    {
        Position = Stage3D.Trunk(ground, top, 0.0f, squash, rise).Origin;
        var along = new Vector3(heading.X, 0.0f, heading.Y / Mathf.Max(squash, 0.0001f));
        _along = along.LengthSquared() > 1e-6f ? along.Normalized() : Vector3.Right;
        _across = new Vector3(-_along.Z, 0.0f, _along.X);
        _halfLen = Mathf.Max(halfLen, 1.0f);
        _halfWide = Mathf.Max(halfWide, 1.0f);
        // A medium's half-width is the unit: a heavy's pops are bigger and
        // more, a light's fewer and smaller, by the hull and not by a table.
        _size = Mathf.Clamp(_halfWide / 33.0f, 0.6f, 1.6f);
    }

    /// <summary>How far under the hull is this frame - <see cref="TankTick.SunkAt"/>.</summary>
    public void Feed(float sink)
    {
        _sink = sink;
    }

    /// <summary>Whether anything is on the surface or still to come - true for
    /// as long as the hull is under, and a pop's life after it is raised.</summary>
    public bool Busy => Rate(_sink) > 0.0f || _clock - Latest() < Life;

    private float Latest()
    {
        float latest = float.NegativeInfinity;
        foreach (Pop pop in _pops)
            latest = Mathf.Max(latest, pop.Born);
        return latest;
    }

    public void Tick(double delta)
    {
        float dt = (float)delta;
        _clock += dt;
        _due += Rate(_sink) * _size * dt;
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
        // compartment is where the air is - small, with the odd bigger one.
        float u = (_dice.Randf() + _dice.Randf() - 1.0f) * 0.85f;
        float v = (_dice.Randf() + _dice.Randf() - 1.0f) * 0.9f;
        var ground = new Vector2(_halfLen * u, _halfWide * v);
        float pick = _dice.Randf();
        float size = (2.5f + 2.5f * pick + (pick > 0.85f ? 3.0f : 0.0f)) * _size;
        _pops[_next] = new Pop(_clock, ground, size, _dice.Randf() * Mathf.Tau);
        _next = (_next + 1) % Most;
        if (Struck is not null)
        {
            Vector3 at = Position + _along * ground.X + _across * ground.Y;
            Struck(new Vector2(at.X, at.Z), Stage3D.Waves * Dent * size / 5.0f);
        }
    }

    private static readonly Transform3D Gone =
        new(Basis.Identity.Scaled(Vector3.Zero), Vector3.Zero);

    private void Dress()
    {
        if (_many is null || _sheet is null)
            return;
        bool any = false;
        for (int k = 0; k < Most; k++)
        {
            Pop pop = _pops[k];
            float age = _clock - pop.Born;
            if (age < 0.0f || age >= Life)
            {
                _many.SetInstanceTransform(k, Gone);
                continue;
            }
            any = true;
            (float grow, float ring, float dome) = Look(age);
            float s = pop.Size * grow;
            // Flat on the water, not upright: a ring on the surface is a ring
            // on the surface, and the camera foreshortens it like the pond.
            // Turned by its own angle, so the ring's uneven side is not the
            // same side on every pop.
            float c = Mathf.Cos(pop.Turn), n = Mathf.Sin(pop.Turn);
            var basis = new Basis(new Vector3(c * s, 0.0f, n * s),
                                  new Vector3(-n * s, 0.0f, c * s),
                                  new Vector3(0.0f, 1.0f, 0.0f));
            Vector3 at = _along * pop.Ground.X + _across * pop.Ground.Y;
            _many.SetInstanceTransform(k, new Transform3D(basis, at));
            _many.SetInstanceCustomData(k, new Color(ring, dome, 0.0f, 1.0f));
        }
        _sheet.Visible = any;
    }

    /// <summary>The two pictures of a pop in two channels, drawn once: the rim
    /// it leaves in red - soft, and uneven round its circle, because a broken
    /// bubble is not a compass ring - and the dome it arrives as in green. The
    /// shader weighs each by its own alpha from <see cref="Look"/>.</summary>
    private static Texture2D Ring()
    {
        const int n = 32;
        var image = Image.CreateEmpty(n, n, false, Image.Format.Rgba8);
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            float dx = (x + 0.5f) / n * 2.0f - 1.0f, dy = (y + 0.5f) / n * 2.0f - 1.0f;
            float r = Mathf.Sqrt(dx * dx + dy * dy);
            float t = Mathf.Atan2(dy, dx);
            float wobble = 0.65f + 0.35f * Mathf.Sin(3.0f * t + 0.7f) * Mathf.Cos(2.0f * t);
            float ring = 1.0f - Mathf.Clamp(Mathf.Abs(r - 0.68f) / 0.26f, 0.0f, 1.0f);
            float dome = 1.0f - Mathf.SmoothStep(0.1f, 0.6f, r);
            image.SetPixel(x, y, new Color(ring * ring * wobble, dome, 0.0f, 1.0f));
        }
        return ImageTexture.CreateFromImage(image);
    }

    /// <summary>Unshaded, blended, no depth written and none tested: the pops
    /// are on the water's surface and the hull they belong to is under it, so
    /// the rung above the stand is always right - see <see cref="Plunge"/> for
    /// why the board's own test is not wanted on a small bright thing.</summary>
    private static readonly Shader Popping = new()
    {
        Code = @"
shader_type spatial;
render_mode unshaded, blend_mix, depth_draw_never, depth_test_disabled, cull_disabled;
uniform sampler2D ring : filter_linear, repeat_disable;
uniform vec4 tint : source_color = vec4(0.92, 0.97, 1.0, 1.0);
varying vec4 mine;
void vertex() {
    mine = INSTANCE_CUSTOM;
}
void fragment() {
    vec2 two = texture(ring, UV).rg;
    ALBEDO = tint.rgb;
    ALPHA = clamp(two.r * mine.r + two.g * mine.g, 0.0, 1.0);
}
",
    };
}
