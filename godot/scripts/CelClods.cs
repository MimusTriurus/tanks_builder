using System;
using System.Collections.Generic;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// Mud thrown off the belts in the model's look: clods flung up and back off
/// each belt's trailing end, falling back under <see cref="Gravity"/> and
/// sinking into the mud where they land. What <see cref="CelDust"/> is on dry
/// ground, this is in mud - and in mud there is no dust: a cloud of pale
/// soil going up behind a tank in a bog read as a tank in a desert.
///
/// <b>A clod is a lump, not a puff.</b> Each one is a quad facing the eye
/// (one <see cref="MultiMesh"/> for all of them), its shape a disc with a torn
/// rim - the radius pushed in and out by a noise round it, seeded per clod -
/// cut hard, with an ink line round it and a lit side toward the board's light,
/// two flat tones and the line, the model's own look. Opaque with a discard, so
/// the hull's depth hides the clods behind it and is hidden by those before.
///
/// <b>Thrown with the belt, not with the hull.</b> A clod leaves the top of the
/// belt's turn at the rear sprocket going back and up; the hull's own way
/// forward does not carry it, which is why a tank's clods fly back at all.
/// Rate and reach go with the belt's pace, so a crawl drops a few lumps
/// behind it and a run in mud throws a spray.
/// </summary>
public sealed partial class CelClods : Node3D
{
    /// <summary>World px a second squared - <see cref="CelSplash.Gravity"/>,
    /// so mud and water fall alike.</summary>
    public const float Gravity = CelSplash.Gravity;

    /// <summary>Clods in the air at most, all belts together.</summary>
    public const int Pool = 160;

    /// <summary>Clods a second per belt at the mud's full pace.</summary>
    public float Rate = 40.0f;

    /// <summary>Speeds off the belt at full pace, hull lengths a second: back,
    /// up, and out to the side. A clod's reach and height go as the square of
    /// these, so the first cut (1.1, 1.6, 0.35) was taken down by root two to
    /// halve the spray - the user found it thrown too far.</summary>
    public float Back = 0.78f, Up = 1.13f, Out = 0.25f;

    /// <summary>A clod's size, hull lengths, smallest to largest.</summary>
    public float SizeMin = 0.035f, SizeMax = 0.085f;

    /// <summary>How long a landed clod takes to sink away, s.</summary>
    public float Sink = 0.6f;

    /// <summary>Wet mud: its body, its lit side and its ink. The body is the
    /// hull's wet coat (<c>Toon</c>'s <c>mud_wet</c>), so what flies off the
    /// belts is what sticks to the plates; browner, it read apart from it.</summary>
    public Color Body = new(0.36f, 0.34f, 0.29f);
    public Color Lit = new(0.50f, 0.48f, 0.42f);
    public Color Ink = new(0.10f, 0.10f, 0.09f);

    /// <summary>The ground's height under a point, world - the bench's
    /// <c>Foot</c>. Without it a clod falls to the height it was thrown from.</summary>
    public Func<Vector3, float>? Ground;

    /// <summary>What is added to a clod where it is drawn: the stage's
    /// clearance off the ground, so a clod lying on it is not lost to the
    /// depth test.</summary>
    public Vector3 Lift;

    /// <summary>One belt this frame: its trailing end on the ground, the way
    /// back off it (flat, unit), the way out from under the hull (flat, unit),
    /// and its pace as a share of the mud's top speed - nought throws nothing.</summary>
    public readonly record struct Belt(Vector3 At, Vector3 Astern, Vector3 Outward, float Pace);

    private struct Clod
    {
        public Vector3 At, Way;
        public float Size, Seed, Landed;
        public bool Live, Down;
    }

    private readonly Clod[] _clods = new Clod[Pool];
    private readonly List<float> _owed = new();
    private readonly RandomNumberGenerator _rng = new();
    private MultiMeshInstance3D _mesh = null!;
    private float _hull = 150.0f;
    private int _next;

    public override void _Ready()
    {
        _rng.Seed = 7331;
        var look = new ShaderMaterial { Shader = ClodShader };
        look.SetShaderParameter("body", Body);
        look.SetShaderParameter("lit", Lit);
        look.SetShaderParameter("ink", Ink);
        look.SetShaderParameter("ink_width", Toon.InkWidth);
        look.SetShaderParameter("ink_min_px", Toon.InkMinPx);
        var multi = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseCustomData = true,
            Mesh = new QuadMesh { Size = Vector2.One },
            InstanceCount = Pool,
            VisibleInstanceCount = 0,
        };
        _mesh = new MultiMeshInstance3D
        {
            Name = "Clods",
            Multimesh = multi,
            MaterialOverride = look,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            // The clods fly anywhere about the hull: the node's own bounds
            // say nothing about where they are.
            ExtraCullMargin = 4000.0f,
        };
        AddChild(_mesh);
    }

    public void Build(float hullPx) => _hull = Mathf.Max(hullPx, 1.0f);

    /// <summary>Everything in the air gone.</summary>
    public void Reset()
    {
        for (int i = 0; i < Pool; i++)
            _clods[i].Live = false;
        for (int i = 0; i < _owed.Count; i++)
            _owed[i] = 0.0f;
        if (_mesh is not null)
            _mesh.Multimesh.VisibleInstanceCount = 0;
    }

    public void Tick(float dt, IReadOnlyList<Belt> belts)
    {
        while (_owed.Count < belts.Count)
            _owed.Add(0.0f);
        for (int i = 0; i < belts.Count; i++)
        {
            Belt b = belts[i];
            if (b.Pace <= 0.0f)
            {
                _owed[i] = 0.0f;
                continue;
            }
            float pace = Mathf.Min(b.Pace, 1.5f);
            _owed[i] += Rate * pace * dt;
            while (_owed[i] >= 1.0f)
            {
                _owed[i] -= 1.0f;
                Throw(b, pace);
            }
        }

        MultiMesh multi = _mesh.Multimesh;
        int n = 0;
        for (int i = 0; i < Pool; i++)
        {
            ref Clod c = ref _clods[i];
            if (!c.Live)
                continue;
            if (!c.Down)
            {
                c.Way.Y -= Gravity * dt;
                c.At += c.Way * dt;
                float ground = Ground?.Invoke(c.At) ?? float.NegativeInfinity;
                if (c.At.Y <= ground && c.Way.Y < 0.0f)
                {
                    c.At.Y = ground;
                    c.Down = true;
                    c.Landed = 0.0f;
                }
            }
            else
            {
                c.Landed += dt;
                if (c.Landed >= Sink)
                {
                    c.Live = false;
                    continue;
                }
            }
            // Landed, it flattens and goes down into the mud.
            float sunk = c.Down ? c.Landed / Sink : 0.0f;
            float size = c.Size * (1.0f - sunk * sunk);
            var basis = Basis.Identity.Scaled(new Vector3(size, size, size));
            multi.SetInstanceTransform(n, new Transform3D(basis, c.At + Lift));
            multi.SetInstanceCustomData(n, new Color(c.Seed, c.Down ? 1.0f : 0.0f, 0.0f, 0.0f));
            n++;
        }
        multi.VisibleInstanceCount = n;
    }

    private void Throw(Belt b, float pace)
    {
        // The oldest slot when the pool is full: a clod cut short in the air
        // is one of a spray nobody counts.
        int slot = -1;
        for (int k = 0; k < Pool; k++)
        {
            int j = (_next + k) % Pool;
            if (!_clods[j].Live)
            {
                slot = j;
                break;
            }
        }
        if (slot < 0)
            slot = _next;
        _next = (slot + 1) % Pool;

        float reach = _hull * (0.6f + 0.4f * pace);
        Vector3 way = b.Astern * (Back * reach * _rng.RandfRange(0.4f, 1.2f))
                    + b.Outward * (Out * reach * _rng.RandfRange(-0.6f, 1.0f))
                    + Vector3.Up * (Up * reach * _rng.RandfRange(0.45f, 1.1f));
        // Off the width of the belt, not one point of it.
        Vector3 at = b.At + b.Outward * (_rng.RandfRange(-0.5f, 0.5f) * 0.12f * _hull)
                   + Vector3.Up * (0.06f * _hull);
        _clods[slot] = new Clod
        {
            At = at,
            Way = way,
            Size = _hull * _rng.RandfRange(SizeMin, SizeMax),
            Seed = _rng.Randf(),
            Live = true,
        };
    }

    /// <summary>
    /// A clod: a billboard quad, a disc whose radius a noise round it pushes in
    /// and out (seeded by <c>INSTANCE_CUSTOM.r</c>), cut hard. The ink is a
    /// band inside the rim no thinner than <c>ink_min_px</c>; the lit side is
    /// the part of the lump toward the upper left of the screen, where the
    /// board's light comes from, one flat step. A landed clod (<c>.g</c>) is
    /// squashed flat, the way a lump of mud lands.
    /// </summary>
    private static readonly Shader ClodShader = new()
    {
        Code = @"
shader_type spatial;
render_mode unshaded, cull_disabled, shadows_disabled;
uniform vec3 body : source_color;
uniform vec3 lit : source_color;
uniform vec3 ink : source_color;
uniform float ink_width = 1.1;
uniform float ink_min_px = 1.0;
varying float seed;
varying float down;

void vertex() {
    seed = INSTANCE_CUSTOM.r;
    down = INSTANCE_CUSTOM.g;
    float size = length(MODEL_MATRIX[0].xyz);
    MODELVIEW_MATRIX = VIEW_MATRIX * mat4(INV_VIEW_MATRIX[0], INV_VIEW_MATRIX[1],
                                          INV_VIEW_MATRIX[2], MODEL_MATRIX[3]);
    MODELVIEW_MATRIX = MODELVIEW_MATRIX * mat4(vec4(size, 0.0, 0.0, 0.0),
                                               vec4(0.0, size, 0.0, 0.0),
                                               vec4(0.0, 0.0, size, 0.0),
                                               vec4(0.0, 0.0, 0.0, 1.0));
}

float clod_hash(float x) {
    return fract(sin(x * 91.7 + seed * 431.3) * 43758.5453);
}

float clod_noise(float x) {
    float i = floor(x);
    float f = fract(x);
    f = f * f * (3.0 - 2.0 * f);
    // Wrapped round the disc: 7 cells, the last back to the first.
    return mix(clod_hash(mod(i, 7.0)), clod_hash(mod(i + 1.0, 7.0)), f);
}

void fragment() {
    vec2 p = vec2(UV.x * 2.0 - 1.0, 1.0 - UV.y * 2.0);
    // Landed: flat on the ground, as wide and half as tall.
    if (down > 0.5)
        p.y = (p.y + 0.5) * 2.0;
    float ang = atan(p.y, p.x) / 6.2831853 + 0.5;
    float r = 0.62 + 0.30 * clod_noise(ang * 7.0);
    float d = length(p) / r;
    if (d > 1.0)
        discard;
    float px = fwidth(d);
    float band = max(0.16 * ink_width, ink_min_px * px * 1.2);
    vec3 c = body;
    // The lit side: toward the upper left, one step.
    vec2 light = normalize(vec2(-0.55, 0.85));
    if (dot(p / r, light) > 0.18 && d < 0.8)
        c = lit;
    if (d > 1.0 - band)
        c = ink;
    ALBEDO = c;
}
",
    };
}
