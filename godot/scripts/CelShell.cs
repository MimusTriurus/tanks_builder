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
/// <item><b>The tracer</b> - a streak that glows: one field on a quad facing
/// the eye (<see cref="LanceShader"/>), a thin white-hot core drawn out to a
/// point at both ends, fullest toward the head (<see cref="Peak"/>), in a
/// soft halo (<see cref="Hot"/> at the head going to <see cref="Cool"/> at
/// the tail) that runs on a little past its ends - no ink line round it. The
/// cel lance before it (an inked capsule cut into flat bands of the fire's
/// colours) read as a stick, a mortar's as a fat sausage - its calibre 1.35
/// and its loom at the top as much again, both on the width; the user's
/// reference is Dust Front's tracer, a needle of light. <b>Its length is
/// the round's smear</b> - how far it goes in <see cref="Blur"/> at its pace
/// now (<see cref="Round.Pace"/>), the same for every gun - and not a
/// length of its own: a mortar's bomb, slow and slowest at the top, drew a
/// longer streak than a gun's round at twice the pace, longest just where
/// it all but hangs, and the straight streak stood off its arc as a stick
/// carried along it. Now it is short and thick going up, near a ball of
/// light at the top and drawn out again coming down; a direct round, at the
/// sprites' pace, three quarters of the sprites' streak. Never shorter than
/// <see cref="Stub"/> of its core's width. <b>The gun's calibre is in the
/// width</b> (<see cref="CoreHalf"/>, <see cref="GlowHalf"/> and
/// <see cref="Thick"/> on <see cref="MovementProfile.TracerCalibre"/>, the
/// gun's row: LT 0.8 to HM 1.75), with the root of the loom - a heavier gun
/// draws a fatter streak, not a longer one. Straight along the head's
/// tangent as the sprites' (<see cref="Shell.Heading"/>) - bent along the
/// path, a mortar's bomb at the top of its arc was a banana. In the world,
/// with its depth written along its line, so a hull in front of the round
/// hides it and a round going into a plate goes into it.</item>
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

    /// <summary>The tracer's colours: the white-hot core, and its halo warm
    /// at the head and cooling to a rose red down the tail.</summary>
    public Color Core = new(1.0f, 0.98f, 0.90f), Hot = new(1.0f, 0.74f, 0.38f), Cool = new(1.0f, 0.46f, 0.42f);

    /// <summary>The banded look's band: fuller than the halo's tones, which
    /// are thinned over the ground - flat, they read pastel.</summary>
    public Color BandHot = new(1.0f, 0.60f, 0.16f), BandCool = new(0.97f, 0.30f, 0.24f);

    /// <summary>The core's and the halo's half widths at their fullest, world
    /// units at a calibre of 1 (a medium's), and how much the halo covers
    /// there.</summary>
    public float CoreHalf = 1.6f, GlowHalf = 7.0f, Glow = 0.9f;

    /// <summary>Where the streak is fullest, as a share of it from the tail:
    /// a short blunt run to the head, a long taper back to the tail.</summary>
    public float Peak = 0.8f;

    /// <summary>How thick the streak is drawn against <see cref="CoreHalf"/>
    /// and <see cref="GlowHalf"/>, and how much of the sprites' streak a
    /// round at their pace (<see cref="Shell.BaseSpeed"/>) draws.</summary>
    public float Thick = 1.35f, StreakShare = 0.75f;

    /// <summary>How the width goes with the gun's calibre: its power. On the
    /// calibre alone (1) the five guns' 0.8-1.75 drew streaks a glance could
    /// not tell apart - the halo is the most of the width, and it was all
    /// about the same; squared and a little under, LT is 0.67 of a medium's
    /// and HM 2.7.</summary>
    public float WidthPower = 1.8f;

    /// <summary>The tracer in flat bands (the cel look, the fire's): a hard
    /// white core, a flat warm band round it cooling to rose down the tail
    /// past <see cref="CoolFrom"/>, a faint soft glow outside - rather than
    /// the one smooth halo of <c>--tracer-soft</c>.</summary>
    public bool Soft;

    /// <summary>The banded look's warm band, its half width against the
    /// core's, and how far from the tail, as a share of the band, it is the
    /// cooled tone.</summary>
    public float BandHalf = 2.3f, CoolFrom = 0.42f;

    /// <summary>The shortest streak, in core widths at its fullest: under it
    /// the spindle is a spot of light, not a streak.</summary>
    public float Stub = 2.6f;

    /// <summary>How long a time the streak smears the round over, s - a
    /// medium's sprite streak at the sprites' pace, by
    /// <see cref="StreakShare"/>; the same for every gun.</summary>
    public float Blur => StreakShare * Shell.StreakSize / Shell.BaseSpeed;

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

        /// <summary>How fast it goes now, world units a second - a throw's
        /// own pace on its step (slowest at the top of its arc), or
        /// <see cref="Speed"/>.</summary>
        public float Pace
        {
            get
            {
                if (Every <= 0.0f || Points.Length < 2)
                    return Speed;
                int k = Mathf.Clamp((int)(Clock / Every), 0, Points.Length - 2);
                return (Along[k + 1] - Along[k]) / Every;
            }
        }

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
                float loom = r.Lofted ? 1.0f + (Shell.Loom - 1.0f) * r.High : 1.0f;
                // The gun's calibre and the loom on the width alone, the
                // length the smear at its pace: a heavier gun is not a faster
                // round, nor a bomb faster at the top. No longer than the path
                // that made it: at point blank a full streak is a beam from
                // the muzzle to the plate.
                float wide = Thick * Mathf.Pow(r.Calibre, WidthPower) * Mathf.Sqrt(loom);
                float len = Mathf.Max(r.Pace * Blur, Stub * 2.0f * CoreHalf * wide);
                Lance(used++, r, Mathf.Min(len, r.Flown), wide, eye);
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
    /// along its tangent, <paramref name="wide"/> times the medium's width -
    /// on a quad facing the eye, long along the tracer as the screen sees it,
    /// the halo's width round it, and in front of both its ends (the shader
    /// writes the depth).</summary>
    private void Lance(int k, Round r, float len, float wide, Basis eye)
    {
        float half = GlowHalf * wide;
        var (quad, look) = LanceAt(k);
        Vector3 back = eye.Z.Normalized();
        Vector3 head = r.Head, way = r.Way(r.Flown);
        Vector3 tail = head - way * len;
        Vector3 d = head - tail;
        Vector3 flat = d - back * d.Dot(back);
        Vector3 x = flat.LengthSquared() > 1e-4f ? flat.Normalized() : eye.X.Normalized();
        Vector3 y = back.Cross(x).Normalized();
        float margin = 1.3f * half + 2.0f;
        Vector3 mid = 0.5f * (head + tail);
        quad.GlobalTransform = new Transform3D(
            new Basis(x * (flat.Length() + 2.0f * margin), y * (2.0f * margin), back),
            mid + back * (0.5f * Mathf.Abs(d.Dot(back)) + half + 1.0f));
        look.SetShaderParameter("head", head);
        look.SetShaderParameter("tail", tail);
        look.SetShaderParameter("core_r", CoreHalf * wide);
        look.SetShaderParameter("glow_r", half);
        look.SetShaderParameter("glow", Glow);
        look.SetShaderParameter("peak", Peak);
        look.SetShaderParameter("core_tone", Core);
        look.SetShaderParameter("hot_tone", Hot);
        look.SetShaderParameter("cool_tone", Cool);
        look.SetShaderParameter("bands", !Soft);
        look.SetShaderParameter("band_r", BandHalf * CoreHalf * wide);
        look.SetShaderParameter("cool_from", CoolFrom);
        look.SetShaderParameter("band_hot", BandHot);
        look.SetShaderParameter("band_cool", BandCool);
        quad.Visible = true;
    }

    /// <summary>
    /// The tracer as one field, from <c>tail</c> to <c>head</c>: a spindle -
    /// fullest at <c>peak</c> of the way from the tail, drawn to a point both
    /// ways, bluntly to the head and long to the tail - whose core is
    /// white-hot, <c>core_r</c> at the fullest and never under a pixel; round
    /// it a halo, <c>glow_r</c>, falling off as a bell from its bright run -
    /// a capsule round at both ends, not the spindle's shape: on a short
    /// streak that was a fan cut off square at the head - warm at the head
    /// and cooling to the tail, dimmer down the tail. Premultiplied: the core covers what is
    /// behind, the halo covers it by <c>cover</c> and lights it by the rest -
    /// mixed over the grass it went brown, added it would wash out to yellow.
    /// <b>In bands</b> (<c>bands</c>, the default): the core's edge hard (a
    /// pixel's smoothing, no more, or it crawls in flight), round it a flat
    /// band <c>band_r</c> - a capsule along the bright run, thinner to the
    /// tail - of the warm tone, cut to the cooled one past <c>cool_from</c>
    /// from its tail, and only outside it the soft glow, fainter: the fire's
    /// flat tones, still a light - no ink line, which on a light is a solid
    /// stick. The depth is the line's own, a core's width toward the eye.
    /// </summary>
    public static readonly Shader LanceShader = new()
    {
        Code = @"
shader_type spatial;
render_mode unshaded, cull_disabled, shadows_disabled, blend_premul_alpha, depth_draw_never;
uniform vec3 head;
uniform vec3 tail;
uniform float core_r = 1.25;
uniform float glow_r = 6.0;
uniform float glow = 0.85;
// how much of the halo covers what is behind, and how much it lights it
uniform float cover = 0.25;
uniform float peak = 0.8;
uniform vec3 core_tone : source_color;
uniform vec3 hot_tone : source_color;
uniform vec3 cool_tone : source_color;
uniform bool bands = true;
uniform float band_r = 3.7;
uniform float cool_from = 0.42;
uniform vec3 band_hot : source_color;
uniform vec3 band_cool : source_color;
void fragment() {
    vec2 p = VERTEX.xy;
    vec3 a = (VIEW_MATRIX * vec4(tail, 1.0)).xyz;
    vec3 b = (VIEW_MATRIX * vec4(head, 1.0)).xyz;
    vec2 ba = b.xy - a.xy;
    float len = max(length(ba), 1e-3);
    vec2 dir = ba / len;
    float t = dot(p - a.xy, dir) / len;
    float q = abs(dot(p - a.xy, vec2(-dir.y, dir.x)));
    float tc = clamp(t, 0.0, 1.0);
    // the spindle: blunt to the head, a long taper to the tail
    float s = tc < peak ? tc / peak : (1.0 - tc) / (1.0 - peak);
    float prof = tc < peak ? pow(s, 0.75) : pow(s, 0.4);
    // world units to a pixel; the matrix's y may be flipped
    float px = abs(2.0 / (PROJECTION_MATRIX[1][1] * VIEWPORT_SIZE.y));
    float wc = max(core_r * prof, 0.0);
    float core = bands
        ? 1.0 - smoothstep(max(wc, 0.5 * px) - 0.5 * px, max(wc, 0.5 * px) + 0.5 * px, q)
        : 1.0 - smoothstep(max(wc - px, 0.0) * 0.7, max(wc, 0.5 * px) + px, q);
    core *= bands ? step(0.06, tc) * step(t, 1.0) : smoothstep(0.0, 0.12, tc) * (1.0 - smoothstep(0.985, 1.0, t));
    // the halo: a bell round the streak's bright run - a capsule, round at
    // both ends, so a short streak glows as a ball and is not cut off
    float hc = clamp(t, 0.2, 0.92);
    float d = length(p - mix(a.xy, b.xy, hc));
    float halo = exp(-2.4 * d * d / (glow_r * glow_r)) * glow * mix(0.35, 1.0, smoothstep(0.0, peak, tc));
    vec3 tone = mix(cool_tone, hot_tone, smoothstep(0.15, 0.95, tc));
    if (bands) {
        // the warm band: a capsule along the bright run, thinner to the tail
        vec2 a2 = mix(a.xy, b.xy, 0.08), b2 = mix(a.xy, b.xy, 0.96);
        vec2 ab = b2 - a2;
        float kb = clamp(dot(p - a2, ab) / max(dot(ab, ab), 1e-6), 0.0, 1.0);
        float rb = band_r * mix(0.4, 1.0, smoothstep(0.0, 0.8, kb));
        float sd = length(p - a2 - ab * kb) - rb;
        float band = 1.0 - smoothstep(-0.5 * px, 0.5 * px, sd);
        float cut = (kb - cool_from) * length(ab);
        vec3 btone = mix(band_cool, band_hot, smoothstep(-0.5 * px, 0.5 * px, cut));
        halo *= 0.75;
        if (core + band + halo < 0.004) discard;
        vec3 under = band * btone + (1.0 - band) * tone * halo;
        ALBEDO = core_tone * core + under * (1.0 - core);
        ALPHA = core + (band + (1.0 - band) * cover * halo) * (1.0 - core);
    } else {
        if (core + halo < 0.004) discard;
        // premultiplied: the core covers, the halo half covers and half lights
        ALBEDO = core_tone * core + tone * halo * (1.0 - core);
        ALPHA = core + cover * halo * (1.0 - core);
    }
    float z = mix(a.z, b.z, tc) + core_r;
    vec4 clip = PROJECTION_MATRIX * vec4(p, z, 1.0);
    DEPTH = clip.z / clip.w * 0.5 + 0.5;
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
