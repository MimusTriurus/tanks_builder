using System;
using System.Collections.Generic;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// Rounds in the air on the 3D bench, in the model's look: the sprites'
/// tracer (<see cref="Shell"/>) as a lance in the world, and the smoke it
/// leaves. The path is the owner's - a list of points from the muzzle to
/// wherever it ends (<see cref="Fly"/>), worked out at the shot - and this
/// only flies along it at <see cref="Shell.Speed"/> and says when it is done.
///
/// <list type="bullet">
/// <item><b>The tracer</b> - in the model's look rather than the sprites':
/// one field on a quad facing the eye (<see cref="LanceShader"/>), a capsule
/// from the tail to the head, <b>round at both ends</b>, the head the fatter
/// (<see cref="TailWidth"/>), with the model's ink line round it - the same
/// width on the screen as the hulls' (<see cref="Toon.InkWidth"/>) - and
/// cut inside into flat bands as the fire is: a near-white core toward the
/// head, the warm body, the tail's end cooled to red past
/// <see cref="CoolFrom"/> of its length. The sprites' lance
/// (<see cref="Shell.Lance"/>: a trapezium pointed at both ends, graded
/// under an outset hem) was three ribbons here, and its points were the
/// thing that read as a 2D picture on the model; the user asked for the cel
/// look and no points. The length and the width are still the sprites'
/// (<see cref="Shell.StreakSize"/>, <see cref="Shell.BodySize"/> and
/// <see cref="Shell.Edge"/> on the class's calibre), straight along the
/// head's tangent as theirs (<see cref="Shell.Heading"/>) - bent along the
/// path, a mortar's bomb at the top of its arc was a banana. In the world,
/// with its depth written along it, so a hull in front of the round hides it
/// and a round going into a plate goes into it.</item>
/// <item><b>The trail</b> - off by default, as the sprites' is
/// (<see cref="Shell.SmokeOnByDefault"/>): smoke laid along the path as the
/// head passes, standing where it was laid and ageing from there - the
/// sprites' rule, and the reason it reads as smoke and not as a comet. <b>A
/// band, not a cloud</b>: the sprites' soft grey, widening and thinning out
/// with its age, wandering off the path. As puffs of one
/// <see cref="CelCloud"/> it was a string of inked beads - a round lays some
/// 1600 px of smoke a second, the cloud holds 64 puffs, and spaced to fit
/// they were each a dot with its own ink line.</item>
/// <item><b>The loom</b> - a round over the board (<see cref="Round.Lofted"/>)
/// swells by <see cref="Shell.Loom"/> at the top of its arc; its shadow is
/// the owner's to draw, off <see cref="Round.Head"/> and
/// <see cref="Round.High"/>.</item>
/// </list>
/// </summary>
public sealed partial class CelShell : Node3D
{
    /// <summary>The tracer is drawn - the flight runs either way, as the
    /// sprites' (<see cref="Shell.TracerOnByDefault"/>).</summary>
    public bool Tracer = Shell.TracerOnByDefault;

    /// <summary>The smoke trail is laid (<see cref="Shell.SmokeOnByDefault"/>).</summary>
    public bool Smoke = Shell.SmokeOnByDefault;

    /// <summary>The tracer's bands: the ink, the core, the body and the
    /// cooled tail - the fire's colours (<see cref="CelShot"/>'s flash).</summary>
    public Color Ink = new(0.10f, 0.05f, 0.02f), Core = new(1.0f, 0.97f, 0.80f),
                 Body = new(1.0f, 0.62f, 0.16f), Cool = new(0.92f, 0.26f, 0.08f);

    /// <summary>The tail's half width against the head's: thinner, round all
    /// the same.</summary>
    public float TailWidth = 0.78f;

    /// <summary>How far back from the head, as a share of the tracer, the
    /// body cools to red - an arc about the head, not a cut across.</summary>
    public float CoolFrom = 0.62f;

    /// <summary>The core: its half width against the fill's, and how far back
    /// from the head it reaches, as a share of the tracer.</summary>
    public float CoreWidth = 0.55f, CoreReach = 0.55f;

    /// <summary>The trail's grey and how much of it covers at birth - the
    /// sprites' (<see cref="Shell"/>'s <c>_Draw</c>, <c>SmokeAlpha</c>).</summary>
    public Color TrailInk = new(0.40f, 0.38f, 0.36f, 0.55f);

    /// <summary>How far apart the trail's band is measured along the path,
    /// world units.</summary>
    public float TrailStep = 6.0f;

    public sealed class Round
    {
        internal readonly Vector3[] Points;
        internal readonly float[] Along;
        /// <summary>The path's length, world units.</summary>
        public readonly float Total;
        /// <summary>World units a second along the path.</summary>
        public readonly float Speed;
        /// <summary>The streak's calibre (<see cref="Shell.TracerSize"/>) and
        /// the trail's (<see cref="Shell.SmokeSize"/>).</summary>
        public readonly float Calibre, SmokeCalibre;
        /// <summary>Over the board, not along it: swells at the top and has a
        /// shadow (<see cref="Shell.Lofted"/>).</summary>
        public readonly bool Lofted;
        internal readonly Action? Landed;
        internal readonly int Serial;
        /// <summary>Seconds between the path's points when it is a throw
        /// walked in equal steps of time - the round then goes at the throw's
        /// own pace, slowest at the top of its arc; nought flies it at
        /// <see cref="Speed"/> along the path.</summary>
        internal readonly float Every;
        internal float Clock;
        /// <summary>Each point's height over the line between the path's ends,
        /// as a share of the most - for <see cref="High"/>.</summary>
        private readonly float[] _over;
        /// <summary>World units flown, never past <see cref="Total"/>.</summary>
        public float Flown { get; internal set; }
        /// <summary>Seconds since it arrived, below nought until then.</summary>
        public float Dead { get; internal set; } = -1.0f;
        public bool Arrived => Dead >= 0.0f;

        internal Round(IReadOnlyList<Vector3> path, float speed, float calibre, float smoke, bool lofted,
                       Action? landed, int serial, float every)
        {
            Points = new Vector3[path.Count];
            Along = new float[path.Count];
            float run = 0.0f;
            for (int i = 0; i < path.Count; i++)
            {
                if (i > 0)
                    run += path[i].DistanceTo(path[i - 1]);
                Points[i] = path[i];
                Along[i] = run;
            }
            Total = run;
            Every = every;
            // Timed: the speed the trail ages at is the throw's mean.
            Speed = every > 0.0f && path.Count > 1
                ? Mathf.Max(run / (every * (path.Count - 1)), 1.0f) : Mathf.Max(speed, 1.0f);
            _over = new float[path.Count];
            float most = 0.0f;
            for (int i = 0; i < path.Count; i++)
            {
                float k = path.Count > 1 ? i / (float)(path.Count - 1) : 0.0f;
                _over[i] = path[i].Y - Mathf.Lerp(path[0].Y, path[^1].Y, k);
                most = Mathf.Max(most, _over[i]);
            }
            for (int i = 0; i < path.Count; i++)
                _over[i] = most > 1e-3f ? Mathf.Max(_over[i], 0.0f) / most : 0.0f;
            Calibre = calibre;
            SmokeCalibre = smoke;
            Lofted = lofted;
            Landed = landed;
            Serial = serial;
        }

        /// <summary>Where the path is <paramref name="s"/> world units along it.</summary>
        public Vector3 At(float s)
        {
            s = Mathf.Clamp(s, 0.0f, Total);
            int i = Array.BinarySearch(Along, s);
            if (i >= 0)
                return Points[i];
            i = ~i;
            if (i <= 0)
                return Points[0];
            if (i >= Points.Length)
                return Points[^1];
            float span = Mathf.Max(Along[i] - Along[i - 1], 1e-6f);
            return Points[i - 1].Lerp(Points[i], (s - Along[i - 1]) / span);
        }

        /// <summary>The way the path goes <paramref name="s"/> along it.</summary>
        public Vector3 Way(float s)
        {
            Vector3 d = At(s + 1.0f) - At(s - 1.0f);
            return d.LengthSquared() > 1e-8f ? d.Normalized() : Vector3.Forward;
        }

        /// <summary>The head, now.</summary>
        public Vector3 Head => At(Flown);

        /// <summary>How high it is as a share of its top over the line between
        /// its ends, 0 at both and 1 at the top - what the loom and the shadow
        /// are read off. Nought for a round along the board.</summary>
        public float High
        {
            get
            {
                if (!Lofted || Total <= 1e-3f)
                    return 0.0f;
                int i = Array.BinarySearch(Along, Flown);
                if (i >= 0)
                    return _over[i];
                i = ~i;
                if (i <= 0)
                    return _over[0];
                if (i >= _over.Length)
                    return _over[^1];
                float span = Mathf.Max(Along[i] - Along[i - 1], 1e-6f);
                return Mathf.Lerp(_over[i - 1], _over[i], (Flown - Along[i - 1]) / span);
            }
        }
    }

    private readonly List<Round> _rounds = new();
    private int _serial;
    private readonly List<(MeshInstance3D Quad, ShaderMaterial Look)> _lances = new();
    private ImmediateMesh? _trailMesh;
    private bool _built;

    public IReadOnlyList<Round> Rounds => _rounds;

    public void Build()
    {
        _built = true;
        _trailMesh = new ImmediateMesh();
        AddChild(new MeshInstance3D
        {
            Name = "Trail", Mesh = _trailMesh,
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                VertexColorUseAsAlbedo = true,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Disabled,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            ExtraCullMargin = 16384.0f,
        });
    }

    /// <summary>
    /// A round off along <paramref name="path"/> (world, from the muzzle) at
    /// <paramref name="speed"/>; <paramref name="landed"/> is called the frame
    /// it gets to the end. The calibres are the streak's and the trail's
    /// (<see cref="Shell.TracerSize"/>, <see cref="Shell.SmokeSize"/>).
    /// <paramref name="every"/>, when above nought, is the seconds between the
    /// path's points - a throw walked in equal time - and the round keeps to
    /// it rather than to <paramref name="speed"/>.
    /// </summary>
    public Round Fly(IReadOnlyList<Vector3> path, float speed, float calibre, float smoke, bool lofted,
                     Action? landed, float every = 0.0f)
    {
        var round = new Round(path, speed, calibre, smoke, lofted, landed, _serial++, every);
        _rounds.Add(round);
        return round;
    }

    public void Reset()
    {
        _rounds.Clear();
        foreach (var (quad, _) in _lances)
            quad.Visible = false;
        _trailMesh?.ClearSurfaces();
    }

    public void Tick(float dt, Basis eye)
    {
        if (!_built || _trailMesh is null)
            return;
        for (int i = 0; i < _rounds.Count; i++)
        {
            Round r = _rounds[i];
            if (r.Arrived)
            {
                r.Dead += dt;
                continue;
            }
            if (r.Every > 0.0f)
            {
                r.Clock += dt;
                float at = r.Clock / r.Every;
                int k = (int)at;
                r.Flown = k >= r.Points.Length - 1
                    ? r.Total : Mathf.Lerp(r.Along[k], r.Along[k + 1], at - k);
            }
            else
                r.Flown = Mathf.Min(r.Flown + r.Speed * dt, r.Total);
            if (r.Flown >= r.Total)
            {
                r.Dead = 0.0f;
                r.Landed?.Invoke();
            }
        }
        // Gone once landed and its smoke is gone with it.
        _rounds.RemoveAll(r => r.Arrived && (!Smoke || r.Dead > Shell.SmokeSeconds));

        int used = 0;
        if (Tracer)
            foreach (Round r in _rounds)
            {
                // Nothing bright once it has landed - what is left is the smoke.
                if (r.Arrived || r.Flown <= 0.0f)
                    continue;
                float cal = r.Calibre * (r.Lofted ? 1.0f + (Shell.Loom - 1.0f) * r.High : 1.0f);
                // A smear no longer than the path that made it: at point
                // blank a full streak is a beam from the muzzle to the plate.
                float len = Mathf.Min(Shell.StreakSize * cal, r.Flown);
                Lance(used++, r, len, (Shell.BodySize + Shell.Edge) * cal, eye);
            }
        for (int i = used; i < _lances.Count; i++)
            _lances[i].Quad.Visible = false;
        Trail(eye);
    }

    /// <summary>The <paramref name="k"/>-th quad of the pool, made on first use.</summary>
    private (MeshInstance3D Quad, ShaderMaterial Look) LanceAt(int k)
    {
        while (_lances.Count <= k)
        {
            var look = new ShaderMaterial { Shader = LanceShader };
            look.SetShaderParameter("ink_width", Toon.InkWidth);
            look.SetShaderParameter("ink_min_px", Toon.InkMinPx);
            var quad = new MeshInstance3D
            {
                Name = $"Lance{_lances.Count}", Mesh = new QuadMesh { Size = Vector2.One },
                MaterialOverride = look, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                Visible = false,
            };
            AddChild(quad);
            _lances.Add((quad, look));
        }
        return _lances[k];
    }

    /// <summary>One round's tracer: <paramref name="len"/> back from the head
    /// along its tangent, <paramref name="half"/> wide at the head, ink
    /// included - on a quad facing the eye, long along the tracer as the
    /// screen sees it and in front of both its ends (the shader writes the
    /// depth).</summary>
    private void Lance(int k, Round r, float len, float half, Basis eye)
    {
        var (quad, look) = LanceAt(k);
        Vector3 back = eye.Z.Normalized();
        Vector3 head = r.Head, way = r.Way(r.Flown);
        Vector3 tail = head - way * len;
        Vector3 d = head - tail;
        Vector3 flat = d - back * d.Dot(back);
        Vector3 x = flat.LengthSquared() > 1e-4f ? flat.Normalized() : eye.X.Normalized();
        Vector3 y = back.Cross(x).Normalized();
        float margin = half + 2.0f;
        Vector3 mid = 0.5f * (head + tail);
        quad.GlobalTransform = new Transform3D(
            new Basis(x * (flat.Length() + 2.0f * margin), y * (2.0f * margin), back),
            mid + back * (0.5f * Mathf.Abs(d.Dot(back)) + half + 1.0f));
        look.SetShaderParameter("head", head);
        look.SetShaderParameter("tail", tail);
        look.SetShaderParameter("r_head", half);
        look.SetShaderParameter("r_tail", half * TailWidth);
        look.SetShaderParameter("core_width", CoreWidth);
        look.SetShaderParameter("core_reach", CoreReach);
        look.SetShaderParameter("cool_from", CoolFrom);
        look.SetShaderParameter("ink_tone", Ink);
        look.SetShaderParameter("core_tone", Core);
        look.SetShaderParameter("body_tone", Body);
        look.SetShaderParameter("cool_tone", Cool);
        quad.Visible = true;
    }

    /// <summary>
    /// The tracer as one field: a capsule from <c>tail</c> to <c>head</c>,
    /// its radius running from <c>r_tail</c> to <c>r_head</c>, round at both
    /// ends; inked inside its edge by the model's line width, at least
    /// <c>ink_min_px</c> screen px (<see cref="CelCloud"/>'s rule); inside, a
    /// second, thinner capsule from <c>core_reach</c> back to the head is the
    /// core, and past <c>cool_from</c> of the length from the head the body
    /// is the cooled tone. Flat bands, no gradient - the fire's. The depth
    /// is the capsule's own, front of its round section along the line.
    /// </summary>
    public static readonly Shader LanceShader = new()
    {
        Code = @"
shader_type spatial;
render_mode unshaded, cull_disabled, shadows_disabled;
uniform vec3 head;
uniform vec3 tail;
uniform float r_head = 4.0;
uniform float r_tail = 2.5;
uniform float core_width = 0.55;
uniform float core_reach = 0.55;
uniform float cool_from = 0.62;
uniform float ink_width = 1.1;
uniform float ink_min_px = 1.0;
uniform vec3 ink_tone : source_color;
uniform vec3 core_tone : source_color;
uniform vec3 body_tone : source_color;
uniform vec3 cool_tone : source_color;
// Signed distance to a capsule from a (radius ra) to b (radius rb), and how
// far along it the nearest point is.
float capsule(vec2 p, vec2 a, vec2 b, float ra, float rb, out float k) {
    vec2 ba = b - a;
    k = clamp(dot(p - a, ba) / max(dot(ba, ba), 1e-6), 0.0, 1.0);
    return length(p - a - ba * k) - mix(ra, rb, k);
}
void fragment() {
    vec2 p = VERTEX.xy;
    vec3 a = (VIEW_MATRIX * vec4(tail, 1.0)).xyz;
    vec3 b = (VIEW_MATRIX * vec4(head, 1.0)).xyz;
    float k;
    float sd = capsule(p, a.xy, b.xy, r_tail, r_head, k);
    if (sd > 0.0) discard;
    float px = 2.0 / (PROJECTION_MATRIX[1][1] * VIEWPORT_SIZE.y);
    float ink = max(ink_width, ink_min_px * px);
    float len = max(length(b.xy - a.xy), 1e-3);
    vec3 col;
    if (sd > -ink) {
        col = ink_tone;
    } else {
        float kc;
        vec2 c0 = mix(b.xy, a.xy, core_reach);
        float fill_h = max(r_head - ink, 0.0), fill_t = max(mix(r_head, r_tail, core_reach) - ink, 0.0);
        float sc = capsule(p, c0, b.xy, fill_t * core_width, fill_h * core_width, kc);
        if (sc < 0.0)
            col = core_tone;
        else if (length(p - b.xy) > cool_from * (len + r_head))
            col = cool_tone;
        else
            col = body_tone;
    }
    float r = mix(r_tail, r_head, k);
    float off = length(p - mix(a.xy, b.xy, k));
    float z = mix(a.z, b.z, k) + sqrt(max(r * r - off * off, 0.0));
    vec4 clip = PROJECTION_MATRIX * vec4(p, z, 1.0);
    DEPTH = clip.z / clip.w * 0.5 + 0.5;
    ALBEDO = col;
}
",
    };

    /// <summary>
    /// The smoke: a band along the path from where it has died away to the
    /// head, each stretch of it as old as the time since the head went by
    /// (and since the round landed) - the sprites' <c>PuffAge</c> - and as
    /// wide, as far off the path and as faint as their puff of that age
    /// (<see cref="Shell.PuffLocal"/>, <c>SmokeSeed</c>, <c>SmokeGrow</c>).
    /// </summary>
    private void Trail(Basis eye)
    {
        _trailMesh!.ClearSurfaces();
        if (!Smoke || _rounds.Count == 0)
            return;
        float life = Mathf.Max(Shell.SmokeSeconds, 1e-3f);
        Vector3 back = eye.Z.Normalized();
        bool any = false;
        foreach (Round r in _rounds)
        {
            float size = Mathf.Max(r.SmokeCalibre, 0.05f);
            float dead = Mathf.Max(r.Dead, 0.0f);
            // The oldest smoke still there, along the path.
            float from = Mathf.Max(0.0f, r.Flown - (life - dead) * r.Speed);
            if (r.Flown - from < 1.0f)
                continue;
            int n = Mathf.Clamp((int)((r.Flown - from) / TrailStep), 2, 400);
            Vector3 pl = default, pr = default;
            Color cl = default;
            for (int i = 0; i <= n; i++)
            {
                float s = Mathf.Lerp(from, r.Flown, i / (float)n);
                float age = (r.Flown - s) / r.Speed + dead;
                float t = Mathf.Clamp(age / life, 0.0f, 1.0f);
                Vector3 across = r.Way(s).Cross(back);
                across = across.LengthSquared() > 1e-6f ? across.Normalized() : Vector3.Right;
                // Off the path the more the older, slowly turning: one wander
                // per round, not a jitter per step.
                float wob = Mathf.Sin(s * 0.021f + r.Serial * 1.7f) * 0.6f + Mathf.Sin(s * 0.057f + r.Serial) * 0.4f;
                Vector3 at = r.At(s) + across * (wob * (1.2f + 5.0f * t) * size) + back * 0.5f;
                float w = (1.5f + 7.0f * age) * size;
                // Linear, the sprites' reason: squared took the opacity away
                // just as the width arrived.
                var ink = new Color(TrailInk, TrailInk.A * Mathf.Pow(1.0f - t, 0.7f));
                // The very head fades in, rather than a square end at the round.
                ink.A *= Mathf.Clamp((r.Flown - s) / 12.0f, 0.0f, 1.0f);
                Vector3 l = at + across * w, rr = at - across * w;
                if (i > 0)
                {
                    if (!any)
                    {
                        _trailMesh.SurfaceBegin(Mesh.PrimitiveType.Triangles);
                        any = true;
                    }
                    foreach (var (v, c) in new[] { (pl, cl), (pr, cl), (rr, ink), (pl, cl), (rr, ink), (l, ink) })
                    {
                        _trailMesh.SurfaceSetColor(c);
                        _trailMesh.SurfaceAddVertex(v);
                    }
                }
                pl = l;
                pr = rr;
                cl = ink;
            }
        }
        if (any)
            _trailMesh.SurfaceEnd();
    }
}
