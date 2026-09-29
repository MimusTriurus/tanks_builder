using Godot;

namespace TankSpriteTest;

/// <summary>
/// An HE round bursting on a 3D tank's plate, in the model's own look. The
/// sprites' burst (<see cref="ProcSlam"/>) is left as it is for the 2D tanks;
/// on the model the 3D bench runs this instead. The mark it leaves is
/// <see cref="CelHit"/>'s splash.
///
/// A shell that bursts stops dead, and its gases go where the metal is not:
/// out along the plate's normal and spread along its face. So nothing here
/// leaves along the way the round came - the opposite of a ricochet.
///
/// <list type="bullet">
/// <item><b>Fire</b> - one field on a quad facing the eye, cut into the
/// fire's bands, by frames at 60 fps: (0) the detonation - a white-hot core
/// on the plate, ragged rays round it and the blast wave's ring leaving it;
/// (1-2) the fireball - a lumpy ball pushed out off the plate and spread
/// along it, the ring racing out and breaking up; (3-7) it cools, the core
/// going first, torn into tongues and eaten through while the soot comes up
/// through it; then nothing. Its depth is a body round its middle, so the
/// plate hides what is behind it.</item>
/// <item><b>Soot</b> - dark puffs in one cloud (<see cref="CelCloud"/>),
/// thrown out off the plate and along it, stopped by the air, swelling,
/// rising and drifting, and greying as they go.</item>
/// <item><b>Fragments</b> - a dozen dark pieces of the shell thrown out
/// round the normal and falling, hot at first: no bright streaks and no long
/// one - that would be a round that carried on.</item>
/// <item><b>Dust</b> - the blast reaching the ground under a low hit, spread
/// over it the way the plate faces; less the higher the hit, none on the
/// roof.</item>
/// </list>
///
/// Closed form from the moment of the burst: every piece is arithmetic on its
/// index and the time since, so a capture repeats to the pixel. All lengths
/// are shares of the hull's length on the board (<see cref="Build"/>).
/// </summary>
public sealed partial class CelBlast : Node3D
{
    // ------------------------------------------------------------ the fire

    /// <summary>How long the fire lasts, s - its frames at 60 fps; the stages
    /// are counted in those (<see cref="FireShader"/>).</summary>
    public float FireTime = 8.0f / 60.0f;
    /// <summary>The fireball's radius at its biggest, hull lengths.</summary>
    public float FireSize = 0.20f;
    /// <summary>How far its middle is pushed off the plate, as a share of
    /// its radius, at the start and once grown.</summary>
    public float PushFrom = 0.15f, PushTo = 0.55f;
    /// <summary>The light: colour, energy, reach in hull lengths - short, for
    /// the ramp's reason (<see cref="CelShot.GlowReach"/>).</summary>
    public Color GlowColor = new(1.0f, 0.70f, 0.34f);
    public float GlowEnergy = 0.5f, GlowReach = 0.28f;

    // ------------------------------------------------------------ the soot

    /// <summary>Puffs of soot, their life, s, and how far they are thrown,
    /// hull lengths.</summary>
    public int SootPuffs = 26;
    public float SootLife = 1.5f, SootReach = 0.30f;
    /// <summary>How fast the air stops the soot, s.</summary>
    public float SootStop = 0.10f;
    /// <summary>A soot puff's radius at birth and at the end, hull lengths.</summary>
    public float SootBorn = 0.03f, SootGrown = 0.10f;
    /// <summary>Its grey fresh and at the end of its life.</summary>
    public float SootDark = 0.32f, SootGrey = 0.54f;
    public Color SootTint = new(0.95f, 0.92f, 0.88f);
    /// <summary>How far apart two soot puffs still flow into one, hull
    /// lengths - less than the shot's: at its, the cloud was one smooth lump.</summary>
    public float SootBlend = 0.03f;

    // ------------------------------------------------------------ the fragments

    /// <summary>Fragments: how many, their life, s, speed, hull lengths a
    /// second, how wide round the normal, deg, and how hard they fall.</summary>
    public int Fragments = 14;
    public float FragmentLife = 0.45f, FragmentSpeed = 2.0f, FragmentCone = 70.0f, FragmentFall = 7.0f;

    // ------------------------------------------------------------ the dust

    /// <summary>Puffs of dust off the ground, their life, s, and spread,
    /// hull lengths; how high over the ground a hit still raises it, hull
    /// lengths.</summary>
    public int DustPuffs = 12;
    public float DustLife = 1.0f, DustReach = 0.40f, DustHeight = 0.40f;
    public float DustTone = 0.62f;
    public Color DustTint = new(0.92f, 0.82f, 0.64f);

    /// <summary>Where the wind takes soot and dust, hull lengths a second -
    /// as the shot's (<see cref="CelShot.Wind"/>).</summary>
    public Vector3 Wind = new(0.10f, 0.0f, -0.05f);
    /// <summary>How far apart two puffs still flow into one, hull lengths.</summary>
    public float Blend = 0.05f;

    // ------------------------------------------------------------ the machinery

    private float _hull = 150.0f;
    /// <summary>Seconds since the burst, or below nought with none running.</summary>
    private float _since = -1.0f;
    private int _bursts;
    private Vector3 _at, _n, _foot, _flat;
    /// <summary>How much dust the ground gives: 1 for a hit at the ground, 0
    /// at <see cref="DustHeight"/> or on the roof.</summary>
    private float _low;
    private MeshInstance3D? _fire;
    private ShaderMaterial? _fireLook;
    private OmniLight3D? _glow;
    private MultiMesh? _bits;
    private CelCloud? _soot, _dust;

    public void Build(float hullPx)
    {
        _hull = Mathf.Max(hullPx, 1.0f);
        _fireLook = new ShaderMaterial { Shader = FireShader };
        _fire = new MeshInstance3D
        {
            Name = "Fire", Mesh = new QuadMesh { Size = Vector2.One }, MaterialOverride = _fireLook,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Visible = false,
        };
        AddChild(_fire);
        _glow = new OmniLight3D
        {
            Name = "Glow", LightColor = GlowColor, OmniAttenuation = 0.0f, ShadowEnabled = false,
            LightEnergy = 0.0f, Visible = false,
        };
        AddChild(_glow);
        _bits = new MultiMesh
        {
            Mesh = new BoxMesh { Size = Vector3.One },
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true,
            InstanceCount = Fragments,
            VisibleInstanceCount = 0,
        };
        AddChild(new MultiMeshInstance3D
        {
            Name = "Fragments", Multimesh = _bits,
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                VertexColorUseAsAlbedo = true,
            },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });
        _soot = new CelCloud(this, "Soot", SootTint, SootBlend * _hull);
        _dust = new CelCloud(this, "Dust", DustTint, Blend * _hull);
    }

    /// <summary>A round bursts at <paramref name="at"/> on a plate facing
    /// <paramref name="n"/> (both in the world), over the ground at
    /// <paramref name="foot"/>. A burst still running is cut short.</summary>
    public void Burst(Vector3 at, Vector3 n, Vector3 foot)
    {
        _since = 0.0f;
        _bursts++;
        _at = at;
        _n = n.Normalized();
        _foot = foot;
        Vector3 flat = new(_n.X, 0.0f, _n.Z);
        _flat = flat.LengthSquared() > 1e-6f ? flat.Normalized() : Vector3.Forward;
        float high = (at.Y - foot.Y) / (DustHeight * _hull);
        _low = Mathf.Clamp(1.0f - high, 0.0f, 1.0f) * Mathf.Clamp(1.2f - 1.4f * _n.Y, 0.0f, 1.0f);
    }

    public void Reset()
    {
        _since = -1.0f;
        if (_fire is not null)
            _fire.Visible = false;
        if (_glow is not null)
            _glow.Visible = false;
        if (_bits is not null)
            _bits.VisibleInstanceCount = 0;
        _soot?.Hide();
        _dust?.Hide();
    }

    public void Tick(float dt, Basis eye)
    {
        if (_since < 0.0f || _fire is null || _soot is null || _dust is null)
            return;
        float t = _since;
        _since += dt;
        if (t > Mathf.Max(SootLife, DustLife) * 1.3f + 0.1f)
        {
            Reset();
            return;
        }
        Fire(t, eye);
        Fly(t);
        Soot(t);
        Dust(t);
        _soot.Draw(eye);
        _dust.Draw(eye);
    }

    private void Fire(float t, Basis eye)
    {
        float f = t / Mathf.Max(FireTime, 1e-3f);
        bool on = f < 1.0f;
        _fire!.Visible = on;
        _glow!.Visible = on && GlowEnergy > 0.0f;
        if (!on)
            return;
        float g = t * 60.0f;
        Vector3 right = eye.X.Normalized(), up = eye.Y.Normalized(), back = eye.Z.Normalized();
        var screen = new Vector2(_n.Dot(right), _n.Dot(up));
        float along = screen.Length();
        Vector2 dir = along > 1e-4f ? screen / along : Vector2.Up;
        float size = FireSize * _hull;
        float push = Mathf.Lerp(PushFrom, PushTo, Mathf.SmoothStep(0.0f, 4.0f, g));
        // The quad is 3.2 radii across: the ring gets out to 1.55.
        _fire.GlobalTransform = new Transform3D(
            new Basis(right * (3.2f * size), up * (3.2f * size), back), _at + _n * (push * size));
        _fireLook!.SetShaderParameter("dir", dir);
        _fireLook.SetShaderParameter("along", Mathf.Clamp(along, 0.0f, 1.0f));
        _fireLook.SetShaderParameter("frame", g);
        _fireLook.SetShaderParameter("size_px", size);
        _fireLook.SetShaderParameter("seed", CelPuff.Hash(_bursts, 67));
        _fireLook.SetShaderParameter("ink", Toon.InkWidth / size);
        _glow.GlobalPosition = _at + _n * (0.10f * _hull);
        _glow.OmniRange = GlowReach * _hull;
        _glow.LightEnergy = GlowEnergy * (g < 1.0f ? 1.0f : 0.7f * (1.0f - f));
    }

    /// <summary>The fragments: short dark pieces round the normal, falling,
    /// from orange to near black.</summary>
    private void Fly(float t)
    {
        Vector3 a1 = _n.Cross(Vector3.Up);
        if (a1.LengthSquared() < 1e-6f)
            a1 = Vector3.Right;
        a1 = a1.Normalized();
        Vector3 a2 = a1.Cross(_n).Normalized();
        int shown = 0;
        for (int k = 0; k < Fragments; k++)
        {
            float h1 = CelPuff.Hash(_bursts * 89 + k, 71), h2 = CelPuff.Hash(_bursts * 89 + k, 73);
            float h3 = CelPuff.Hash(_bursts * 89 + k, 79);
            float life = FragmentLife * (0.6f + 0.7f * h3);
            float a = t / life;
            if (a >= 1.0f)
                continue;
            float spread = Mathf.DegToRad(FragmentCone) * Mathf.Sqrt(h1);
            float ang = h2 * Mathf.Tau;
            Vector3 dir = (_n * Mathf.Cos(spread)
                           + (a1 * Mathf.Cos(ang) + a2 * Mathf.Sin(ang)) * Mathf.Sin(spread)).Normalized();
            // Tossed up and falling in an arc: thrown straight at the eye, a
            // piece sat still on the screen, a dead pixel in the cloud.
            dir = (dir + Vector3.Up * 0.6f).Normalized();
            float speed = FragmentSpeed * _hull * (0.5f + 0.8f * h3);
            Vector3 vel = dir * speed + Vector3.Down * (FragmentFall * _hull * t);
            Vector3 at = _at + dir * (speed * t) + Vector3.Down * (0.5f * FragmentFall * _hull * t * t);
            // Short and thick, a piece rather than a streak.
            float len = Mathf.Clamp(vel.Length() / 60.0f * 0.7f, 2.0f, 6.0f);
            float thick = 1.8f + 1.2f * h1;
            Vector3 x = vel.Normalized();
            Vector3 y = x.Cross(Vector3.Up);
            if (y.LengthSquared() < 1e-6f)
                y = Vector3.Right;
            y = y.Normalized();
            Vector3 z = x.Cross(y).Normalized();
            _bits!.SetInstanceTransform(shown, new Transform3D(new Basis(x * len, y * thick, z * thick), at));
            Color hot = new(1.0f, 0.62f, 0.20f), cold = new(0.16f, 0.13f, 0.11f);
            _bits.SetInstanceColor(shown, hot.Lerp(cold, Mathf.SmoothStep(0.2f, 0.8f, a)));
            shown++;
        }
        _bits!.VisibleInstanceCount = shown;
    }

    private void Soot(float t)
    {
        _soot!.Clear();
        Vector3 a1 = _n.Cross(Vector3.Up);
        if (a1.LengthSquared() < 1e-6f)
            a1 = Vector3.Right;
        a1 = a1.Normalized();
        Vector3 a2 = a1.Cross(_n).Normalized();
        for (int k = 0; k < SootPuffs; k++)
        {
            float h1 = CelPuff.Hash(_bursts * 131 + k, 83), h2 = CelPuff.Hash(_bursts * 131 + k, 89);
            float h3 = CelPuff.Hash(_bursts * 131 + k, 97);
            // Half the puffs are the burst's first throw; the rest well up at
            // the plate after it, so the cloud keeps its foot on the hit and
            // does not fly off whole - thrown at once, it left as one lump.
            bool late = k % 2 == 1;
            float born = late ? 0.05f + 0.30f * h3 : 0.0f;
            float tt = t - born;
            if (tt < 0.0f)
                continue;
            float life = SootLife * (0.6f + 0.6f * h3) * (late ? 0.8f : 1.0f);
            float a = tt / life;
            if (a >= 1.0f)
                continue;
            // Out off the plate and along it: the gas goes where the metal
            // is not.
            float ang = h2 * Mathf.Tau;
            Vector3 face = a1 * Mathf.Cos(ang) + a2 * Mathf.Sin(ang);
            Vector3 way = (_n * (0.5f + 0.7f * h1) + face * (1.0f - 0.4f * h1)).Normalized();
            float reach = SootReach * _hull * (0.35f + 0.65f * h1) * (late ? 0.45f : 1.0f);
            float out_ = reach * (1.0f - Mathf.Exp(-tt / Mathf.Max(SootStop * (0.6f + 0.8f * h1), 1e-3f)));
            Vector3 at = _at + _n * (0.06f * _hull) + way * out_
                         // The hot gas climbs once the throw is spent - what
                         // makes it a cloud and not a stain on a plate that
                         // faces the eye.
                         + Vector3.Up * ((0.28f + 0.22f * h2) * _hull * Mathf.Pow(a, 1.3f))
                         + Wind * (_hull * t);
            float big = 0.5f + 1.0f * h2;
            float r = _hull * big * Mathf.Lerp(SootBorn, SootGrown, 1.0f - Mathf.Pow(1.0f - a, 2.5f))
                      // Only as the fire dies: born under it, it was a dark
                      // lump in the fire's middle (the shot's smoke's lesson).
                      * Mathf.SmoothStep(0.3f * FireTime, FireTime, t) * Mathf.SmoothStep(0.0f, 0.08f, a);
            float tone = Mathf.Lerp(SootDark, SootGrey, Mathf.SmoothStep(0.25f, 1.0f, a)) * (0.9f + 0.2f * h1);
            _soot.Add(at, r, tone, h1, Mathf.SmoothStep(0.35f, 1.0f, a), a);
        }
    }

    private void Dust(float t)
    {
        _dust!.Clear();
        if (_low <= 0.0f)
            return;
        float spin = Mathf.Atan2(_flat.Z, _flat.X);
        int count = Mathf.CeilToInt(DustPuffs * _low);
        for (int k = 0; k < count; k++)
        {
            float h1 = CelPuff.Hash(_bursts * 173 + k, 101), h2 = CelPuff.Hash(_bursts * 173 + k, 103);
            float h3 = CelPuff.Hash(_bursts * 173 + k, 107);
            // A beat late: the blast has to come down to the ground.
            float tt = t - 0.04f;
            if (tt < 0.0f)
                continue;
            float life = DustLife * (0.75f + 0.5f * h3);
            float a = tt / life;
            if (a >= 1.0f)
                continue;
            // Mostly the way the plate faces, some along the tank.
            float ang = spin + (h1 - 0.5f) * 2.6f;
            var way = new Vector3(Mathf.Cos(ang), 0.0f, Mathf.Sin(ang));
            float reach = DustReach * _hull * (0.3f + 0.7f * h2) * (0.5f + 0.5f * _low);
            float out_ = reach * (1.0f - Mathf.Exp(-tt / 0.2f));
            Vector3 at = _foot + _flat * (0.05f * _hull) + way * out_
                         + Vector3.Up * (0.03f * _hull + 0.05f * _hull * a)
                         + Wind * (_hull * tt);
            float r = _hull * Mathf.Lerp(0.03f, 0.08f * (0.7f + 0.6f * h3), 1.0f - Mathf.Pow(1.0f - a, 2.0f))
                      * Mathf.SmoothStep(0.0f, 0.06f, a) * (0.6f + 0.4f * _low);
            _dust.Add(at, r, DustTone * (0.9f + 0.2f * h1), h2, Mathf.SmoothStep(0.2f, 1.0f, a), a);
        }
    }

    /// <summary>
    /// The fire, by frames since the burst (<c>frame</c>), as one field cut
    /// into the fire's bands, the bands climbing as it cools. Quad units: 1 is
    /// the fireball's radius, the quad 3.2 across, its middle the ball's.
    /// <list type="number">
    /// <item>frame 0 - the detonation: a hot core and seven ragged rays of
    /// uneven length, near white.</item>
    /// <item>frames 1-2 - the fireball: lumpy from the first, spread across
    /// <c>dir</c> (the normal as the screen has it) - along the plate - and
    /// flatter toward it.</item>
    /// <item>frames 3-7 - a little bigger; the field sinks and a noise tears
    /// its edge into tongues and eats it through.</item>
    /// </list>
    /// Round it for the first frames, the blast wave: a pale ring racing out,
    /// thinning, breaking into arcs (<c>wave_on</c> 0 leaves it out). <c>rays</c>
    /// scales the detonation's rays. <see cref="CelDeath"/>'s fireballs are
    /// this field, slowed.
    /// </summary>
    internal static readonly Shader FireShader = new()
    {
        Code = @"
shader_type spatial;
render_mode unshaded, cull_disabled, shadows_disabled, fog_disabled;
stencil_mode write, compare_always, 0;
uniform vec3 core : source_color = vec3(1.0, 0.98, 0.84);
uniform vec3 body : source_color = vec3(1.0, 0.78, 0.28);
uniform vec3 edge : source_color = vec3(1.0, 0.44, 0.10);
uniform vec3 rim : source_color = vec3(0.45, 0.10, 0.03);
uniform vec3 wave : source_color = vec3(1.0, 0.96, 0.86);
uniform vec3 wave_ink : source_color = vec3(0.52, 0.46, 0.36);
uniform vec2 dir = vec2(0.0, 1.0);
uniform float along = 1.0;
uniform float frame = 0.0;
uniform float seed = 0.0;
uniform float size_px = 30.0;
uniform float ink = 0.03;
uniform float rays = 1.0;
uniform float wave_on = 1.0;
" + Toon.NoiseCode + @"
float hash1(float x) { return fract(sin(x * 12.9898 + 4.1414) * 43758.5453); }
void fragment() {
    vec2 p = (UV - 0.5) * vec2(3.2, -3.2);
    vec2 side = vec2(-dir.y, dir.x);
    float g = frame;
    float f = -1.0;
    float cool = 0.0;
    float thick = 0.0;
    if (g < 1.0) {
        float d = length(p);
        float ang = atan(p.y, p.x) + seed * 6.28;
        float k = floor(ang / 6.2832 * 7.0 + 0.5);
        float off = abs(ang - k / 7.0 * 6.2832);
        float ray_len = mix(0.60, 1.10, hash1(k + seed * 17.0));
        float ray_w = 0.16 * rays * (1.0 - d / ray_len);
        f = max(1.0 - d / 0.42, (ray_w - off * d) * 3.0);
        thick = 0.3 * sqrt(clamp(1.0 - d / 0.42, 0.0, 1.0));
    } else {
        float h = g - 1.0;
        float grow = mix(0.62, 1.0, smoothstep(0.0, 1.5, h)) * (1.0 + 0.06 * max(h - 1.5, 0.0));
        float x = dot(p, dir);
        float y = dot(p, side);
        // Spread along the plate, flatter toward it: seen face on, round.
        float ra = mix(1.0, 0.86, along) * grow;
        float rc = mix(1.0, 1.12, along) * grow;
        vec2 u = vec2(x / (x < 0.0 ? ra * 0.8 : ra), y / rc);
        float a0 = atan(u.y, u.x);
        float lump = noise3(vec3(cos(a0) * 1.4, sin(a0) * 1.4, seed * 5.0 + h * 0.25)) - 0.5;
        f = 1.0 - length(u) + 0.42 * lump;
        thick = min(ra, rc) * sqrt(clamp(f, 0.0, 1.0));
        cool = smoothstep(1.0, 6.5, h);
        float n = noise3(vec3(cos(a0) * 1.9, sin(a0) * 1.9, seed * 13.0 + h * 0.35));
        float m = noise3(vec3(p * 5.0, seed * 7.0 + h * 0.5));
        f -= cool * (0.30 + 0.55 * n) + cool * cool * 0.40 + (m - 0.5) * 0.34 * cool;
    }
    vec3 col = rim;
    if (f >= 0.0) {
        if (g < 1.0) {
            col = f > 0.30 ? core : (f > 0.10 ? body : edge);
        } else {
            if (f > 0.08 + 0.12 * cool) col = edge;
            if (f > 0.26 + 0.34 * cool) col = body;
            if (f > 0.58 + 0.70 * cool) col = core;
        }
    } else {
        // The blast wave, the first frames only, under the fire.
        if (g >= 4.0 || wave_on < 0.5) discard;
        float rr = mix(0.55, 1.55, 1.0 - exp(-(g + 0.4) / 1.4));
        float d = length(p);
        float a1 = atan(p.y, p.x);
        float n = noise3(vec3(cos(a1) * 2.5, sin(a1) * 2.5, seed * 11.0));
        if (n < (g / 4.0) * 0.9) discard;
        float w = 0.09 * (0.6 + 0.8 * n) * (1.0 - 0.75 * g / 4.0);
        float band = w - abs(d - rr);
        if (band < 0.0) discard;
        col = band < ink ? wave_ink : wave;
        thick = 0.0;
    }
    float z = VERTEX.z + thick * size_px;
    vec4 clip = PROJECTION_MATRIX * vec4(VERTEX.xy, z, 1.0);
    DEPTH = clip.z / clip.w * 0.5 + 0.5;
    ALBEDO = col;
}
",
    };
}
