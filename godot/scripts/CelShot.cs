using Godot;

namespace TankSpriteTest;

/// <summary>
/// A 3D tank's shot in the model's own look: the flash at the muzzle, its
/// light, the propellant smoke thrown out along the bore and the dust the
/// blast lifts off the ground under it. The sprites' flash
/// (<see cref="ProcFlash"/>), fume (<see cref="ProcFume"/>) and muzzle cloud
/// (<see cref="ProcKick"/>) are left as they are for the 2D tanks; on the model
/// they were soft white glows and a haze, and the 3D bench hides them and runs
/// this.
///
/// <list type="bullet">
/// <item><b>Flash</b> - one field on a quad facing the eye at the muzzle, cut
/// into the fire's bands (<see cref="CelBurn"/>'s tongues), in the stages a
/// gun's flash has, frame by frame at 60 fps: (1) the primary flash, one
/// frame - small, near white, right at the muzzle, a short cone and rays in
/// a fan; (2-3) the fireball - the propellant gas burning as it meets the
/// air, a big yellow-orange ball a calibre or two ahead of the muzzle,
/// drawn out along the bore, on a thin neck back to it; (4-6) it cools - a
/// little bigger, the core shrinking, the edge going red, torn into
/// tongues while the smoke comes up round it; (7-8) red scraps, then
/// nothing. Everything is ahead of the muzzle, and the "ahead" is the bore
/// as the screen sees it - a gun pointed at the eye has a round flash.</item>
/// <item><b>Light</b> - an omni light at the muzzle for as long, brightest
/// in the primary flash: the ramp steps it into a warm band on the barrel,
/// the turret front and the ground.</item>
/// <item><b>Blast wave</b> - a ring of pale dust racing out over the ground
/// under the muzzle, pushed ahead the way the gun points, torn up as it
/// goes, in <see cref="RingTime"/>: what sells a gun's weight in a
/// cartoon.</item>
/// <item><b>Smoke</b> and <b>dust</b> - puffs in one cloud each
/// (<see cref="CelCloud"/>): the smoke flung out along the bore and stopped
/// by the air, swelling, rising a little and drifting; the dust spread over
/// the ground under the muzzle, mostly the way the gun points.</item>
/// </list>
///
/// Closed form from the moment of the shot: every puff is arithmetic on its
/// index and the time since, so a capture repeats to the pixel. The puffs
/// stay where the shot left them in the world - and so do the flash and its
/// light, at the muzzle as it was when the gun fired: the gas is out of the
/// tube before it recoils, and following the muzzle back the flash had the
/// tube run out through it as it came forward again. All lengths are shares of the hull's length on the board
/// (<see cref="Build"/>).
/// </summary>
public sealed partial class CelShot : Node3D
{
    /// <summary>The tanks its puffs may not stand through, or none.</summary>
    public CelSolids? Solids;

    /// <summary>The blast wave and the dust off the ground are drawn - both
    /// off: the user took them away, leaving the flash, its light and the
    /// smoke. Kept rather than cut, to be turned back on.</summary>
    public bool HasRing, HasDust;

    // ------------------------------------------------------------ the flash

    /// <summary>How long the flash lasts, s - its eight frames at 60 fps.
    /// The stages are counted in those frames (<see cref="FlashShader"/>).</summary>
    public float FlashTime = 8.0f / 60.0f;
    /// <summary>The flash's reach along the bore, hull lengths, at its
    /// biggest; the primary flash and the fireball are shares of it.</summary>
    public float FlashLength = 0.42f;
    /// <summary>The light: colour, energy at the flash's peak, reach in hull
    /// lengths. Short: the ramp steps whatever it reaches straight up to the
    /// middle tone, and at 0.9 hull the whole front of the tank went yellow
    /// for the flash's frames.</summary>
    public Color GlowColor = new(1.0f, 0.72f, 0.36f);
    public float GlowEnergy = 0.6f, GlowReach = 0.30f;

    // ------------------------------------------------------------ the blast wave

    /// <summary>How long the ring runs, s, how far it gets over the ground,
    /// hull lengths, and how thick it is at the start, as a share of that.</summary>
    public float RingTime = 0.20f, RingReach = 0.85f, RingWidth = 0.035f;
    /// <summary>How far ahead the ring's middle is pushed as it grows, as a
    /// share of its radius: the blast goes out along the bore.</summary>
    public float RingAhead = 0.35f;
    public Color RingColor = new(0.96f, 0.88f, 0.72f);

    // ------------------------------------------------------------ the smoke

    /// <summary>Puffs of smoke, their life, s, and how far out along the
    /// bore they are flung, hull lengths.</summary>
    public int SmokePuffs = 14;
    public float SmokeLife = 1.3f, SmokeReach = 0.75f;
    /// <summary>How fast the air stops the smoke, s: most of the reach is
    /// covered in this.</summary>
    public float SmokeStop = 0.14f;
    /// <summary>A smoke puff's radius at birth and at the end, hull lengths.</summary>
    public float SmokeBorn = 0.035f, SmokeGrown = 0.13f;
    public float SmokeTone = 0.50f;
    public Color SmokeTint = new(1.0f, 0.97f, 0.92f);

    // ------------------------------------------------------------ the dust

    /// <summary>Puffs of dust, their life, s, and how far they spread over
    /// the ground, hull lengths. Many and small, low and light: a few big
    /// dark ones were one brown lump by the belt - a rock, not dust.</summary>
    public int DustPuffs = 18;
    public float DustLife = 0.9f, DustReach = 0.60f;
    public float DustBorn = 0.03f, DustGrown = 0.085f;
    public float DustTone = 0.62f;
    public Color DustTint = new(0.92f, 0.82f, 0.64f);

    /// <summary>Where the wind takes smoke and dust, hull lengths a second -
    /// the burning column's way (<see cref="CelBurn.Drift"/>), slower.</summary>
    public Vector3 Wind = new(0.10f, 0.0f, -0.05f);
    /// <summary>How far apart two puffs still flow into one, hull lengths.</summary>
    public float Blend = 0.05f;

    // ------------------------------------------------------------ the machinery

    private float _hull = 150.0f;
    /// <summary>Seconds since the shot, or below nought with none running.</summary>
    private float _since = -1.0f;
    private int _shots;
    private Vector3 _from, _bore, _foot, _flat;
    private MeshInstance3D? _flash;
    private ShaderMaterial? _flashLook;
    private OmniLight3D? _glow;
    private MeshInstance3D? _ring;
    private ShaderMaterial? _ringLook;
    private CelCloud? _smoke, _dust;

    public void Build(float hullPx)
    {
        _hull = Mathf.Max(hullPx, 1.0f);
        _flashLook = new ShaderMaterial { Shader = FlashShader };
        _flash = new MeshInstance3D
        {
            Name = "Flash",
            Mesh = new QuadMesh { Size = Vector2.One },
            MaterialOverride = _flashLook,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Visible = false,
        };
        AddChild(_flash);
        _glow = new OmniLight3D
        {
            Name = "Glow", LightColor = GlowColor, OmniRange = GlowReach * _hull,
            // As the fire's (CelBurn): no falloff but the range, no shadow.
            OmniAttenuation = 0.0f, ShadowEnabled = false, LightEnergy = 0.0f, Visible = false,
        };
        AddChild(_glow);
        _ringLook = new ShaderMaterial { Shader = RingShader };
        _ringLook.SetShaderParameter("tone", RingColor);
        _ringLook.SetShaderParameter("ink_tone", RingColor * 0.45f);
        _ring = new MeshInstance3D
        {
            Name = "Ring",
            Mesh = new QuadMesh { Size = Vector2.One },
            MaterialOverride = _ringLook,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Visible = false,
        };
        AddChild(_ring);
        _smoke = new CelCloud(this, "Smoke", SmokeTint, Blend * _hull);
        _dust = new CelCloud(this, "Dust", DustTint, Blend * _hull);
    }

    /// <summary>The gun fires from <paramref name="muzzle"/> along
    /// <paramref name="bore"/> (both in the world), over the ground at
    /// <paramref name="foot"/>. A shot still running is cut short.</summary>
    public void Fire(Vector3 muzzle, Vector3 bore, Vector3 foot)
    {
        _since = 0.0f;
        _shots++;
        _from = muzzle;
        _bore = bore.Normalized();
        _foot = foot;
        Vector3 flat = new(_bore.X, 0.0f, _bore.Z);
        _flat = flat.LengthSquared() > 1e-6f ? flat.Normalized() : Vector3.Forward;
    }

    public void Reset()
    {
        _since = -1.0f;
        if (_flash is not null)
            _flash.Visible = false;
        if (_glow is not null)
            _glow.Visible = false;
        if (_ring is not null)
            _ring.Visible = false;
        _smoke?.Hide();
        _dust?.Hide();
    }

    /// <summary>A frame: the clock on by <paramref name="dt"/>; the flash, its
    /// light, the smoke and the dust where the shot left them.
    /// <paramref name="eye"/> is the camera's basis.</summary>
    public void Tick(float dt, Basis eye)
    {
        if (_flash is null || _glow is null || _smoke is null || _dust is null || _flashLook is null)
            return;
        if (_since < 0.0f)
            return;
        float t = _since;
        _since += dt;
        if (t > Mathf.Max(SmokeLife, DustLife) * 1.3f + 0.2f)
        {
            Reset();
            return;
        }
        Flash(t, _from, _bore, eye);
        if (HasRing)
            Ring(t);
        else
            _ring!.Visible = false;
        Smoke(t);
        if (HasDust)
            Dust(t);
        else
            _dust.Clear();
        _smoke.Draw(eye, Solids);
        _dust.Draw(eye, Solids);
    }

    private void Flash(float t, Vector3 muzzle, Vector3 bore, Basis eye)
    {
        float f = t / Mathf.Max(FlashTime, 1e-3f);
        bool on = f < 1.0f;
        _flash!.Visible = on;
        _glow!.Visible = on;
        if (!on)
            return;
        Vector3 right = eye.X.Normalized(), up = eye.Y.Normalized(), back = eye.Z.Normalized();
        // The bore as the screen has it: its way, and how much of it is left
        // after the eye's foreshortening.
        var screen = new Vector2(bore.Dot(right), bore.Dot(up));
        float along = screen.Length();
        Vector2 dir = along > 1e-4f ? screen / along : Vector2.Up;
        float size = FlashLength * _hull;
        _flash.GlobalTransform = new Transform3D(
            new Basis(right * (2.4f * size), up * (2.4f * size), back),
            // On the muzzle: the shader puts each pixel's depth where the fire
            // is. Pulled toward the eye to clear the smoke born at the muzzle,
            // the flash was drawn over the tube too, and with the gun pointed
            // away the neck ran through the barrel's end.
            muzzle);
        _flashLook!.SetShaderParameter("dir", dir);
        _flashLook.SetShaderParameter("along", Mathf.Clamp(along, 0.0f, 1.0f));
        _flashLook.SetShaderParameter("frame", t * 60.0f);
        _flashLook.SetShaderParameter("size_px", size);
        // The bore's depth per unit of length: toward the eye positive.
        _flashLook.SetShaderParameter("bore_z", bore.Dot(back));
        _flashLook.SetShaderParameter("seed", CelPuff.Hash(_shots, 5));

        _glow.GlobalPosition = muzzle + bore * (0.12f * _hull);
        _glow.OmniRange = GlowReach * _hull;
        // Brightest in the primary flash, the fireball after it dimmer.
        _glow.LightEnergy = GlowEnergy * (t * 60.0f < 1.0f ? 1.0f : 0.7f * (1.0f - f));
    }

    /// <summary>The blast wave: a ring over the ground under the muzzle,
    /// racing out and slowing, its middle pushed ahead along the bore.</summary>
    private void Ring(float t)
    {
        float f = t / Mathf.Max(RingTime, 1e-3f);
        bool on = f < 1.0f;
        _ring!.Visible = on;
        if (!on)
            return;
        float reach = RingReach * _hull;
        float r = reach * (1.0f - Mathf.Exp(-t / (0.3f * RingTime))) / (1.0f - Mathf.Exp(-1.0f / 0.3f));
        // The quad: flat on the ground, a little over the reach each way.
        float half = reach * (1.0f + RingAhead) * 1.1f;
        Vector3 mid = _foot + _flat * (r * RingAhead) + Vector3.Up * 0.6f;
        _ring.GlobalTransform = new Transform3D(
            new Basis(new Vector3(2.0f * half, 0.0f, 0.0f), new Vector3(0.0f, 0.0f, -2.0f * half), Vector3.Up), mid);
        _ringLook!.SetShaderParameter("radius", r / half);
        _ringLook.SetShaderParameter("width", RingWidth * reach / half);
        _ringLook.SetShaderParameter("ink", Toon.InkWidth / half);
        _ringLook.SetShaderParameter("fade", f);
        _ringLook.SetShaderParameter("seed", CelPuff.Hash(_shots, 7));
    }

    private void Smoke(float t)
    {
        _smoke!.Clear();
        Vector3 side = _bore.Cross(Vector3.Up);
        side = side.LengthSquared() > 1e-6f ? side.Normalized() : Vector3.Right;
        for (int k = 0; k < SmokePuffs; k++)
        {
            float h1 = CelPuff.Hash(_shots * 131 + k, 41), h2 = CelPuff.Hash(_shots * 131 + k, 43);
            float h3 = CelPuff.Hash(_shots * 131 + k, 47);
            // The last two go out sideways - the blast off the muzzle's face.
            bool flank = k >= SmokePuffs - 2;
            // The ones that stop near the muzzle are smaller and go sooner:
            // as big and as long as the far ones, they sat in the tube's
            // mouth like cotton wool.
            float near = flank ? 0.5f : 0.4f + 0.6f * h1;
            float life = SmokeLife * (0.75f + 0.5f * h3) * near;
            float a = t / life;
            if (a >= 1.0f)
                continue;
            float reach = SmokeReach * _hull * (flank ? 0.35f : 0.15f + 0.85f * h1);
            Vector3 way = flank ? side * (k % 2 == 0 ? 1.0f : -1.0f) + _bore * 0.3f : _bore;
            float out_ = reach * (1.0f - Mathf.Exp(-t / Mathf.Max(SmokeStop * (0.6f + 0.8f * h1), 1e-3f)));
            Vector3 at = _from + way.Normalized() * out_
                         + side * ((h2 - 0.5f) * 0.22f * _hull * Mathf.Sqrt(a))
                         + Vector3.Up * ((0.10f + 0.12f * h3) * _hull * a)
                         + Wind * (_hull * t);
            // Further out, bigger: the gas has spread more by the time it is there.
            float grown = SmokeGrown * (0.55f + 0.9f * h2) * near;
            // Only as the flash dies: it is born under it, and seen through
            // the flash's first frames it was a dark lump in the fire.
            float r = _hull * Mathf.Lerp(SmokeBorn, grown, 1.0f - Mathf.Pow(1.0f - a, 2.5f))
                      * Mathf.SmoothStep(0.4f * FlashTime, FlashTime, t);
            _smoke.Add(at, r, SmokeTone * (0.92f + 0.16f * h2), h1, Mathf.SmoothStep(0.10f, 1.0f, a), a);
        }
    }

    private void Dust(float t)
    {
        _dust!.Clear();
        float spin = Mathf.Atan2(_flat.Z, _flat.X);
        for (int k = 0; k < DustPuffs; k++)
        {
            float h1 = CelPuff.Hash(_shots * 173 + k, 53), h2 = CelPuff.Hash(_shots * 173 + k, 59);
            float h3 = CelPuff.Hash(_shots * 173 + k, 61);
            float life = DustLife * (0.75f + 0.5f * h3);
            // A beat late: the blast has to reach the ground.
            float tt = t - 0.03f;
            if (tt < 0.0f)
                continue;
            float a = tt / life;
            if (a >= 1.0f)
                continue;
            // Mostly the way the gun points, some to either side.
            float ang = spin + (h1 - 0.5f) * 2.4f;
            var way = new Vector3(Mathf.Cos(ang), 0.0f, Mathf.Sin(ang));
            float reach = DustReach * _hull * (0.35f + 0.65f * h2);
            float out_ = reach * (1.0f - Mathf.Exp(-tt / 0.18f));
            Vector3 at = _foot + _flat * (0.12f * _hull) + way * out_
                         + Vector3.Up * (0.03f * _hull + 0.06f * _hull * a)
                         + Wind * (_hull * tt);
            float r = _hull * Mathf.Lerp(DustBorn, DustGrown * (0.7f + 0.6f * h3), 1.0f - Mathf.Pow(1.0f - a, 2.0f))
                      * Mathf.SmoothStep(0.0f, 0.06f, a);
            _dust.Add(at, r, DustTone * (0.9f + 0.2f * h1), h2, Mathf.SmoothStep(0.2f, 1.0f, a), a);
        }
    }

    /// <summary>
    /// The flash, by its stages in 60ths of a second since the shot
    /// (<c>frame</c>), as one field cut into the fire's bands - the bands
    /// climbing as it cools, so the core goes first and the red last:
    /// <list type="number">
    /// <item>frame 0 - the primary flash: a short cone along <c>dir</c>, four
    /// rays in a fan about it, a hot spot at the muzzle; near white.</item>
    /// <item>frames 1-2 - the fireball: an egg <c>along</c> the bore, its
    /// middle half a flash length ahead, on a thin neck back to the muzzle.</item>
    /// <item>frames 3-5 - a little bigger; the field sinks and a noise round
    /// the ball tears its edge into tongues.</item>
    /// <item>frames 6-7 - what is left: red scraps.</item>
    /// </list>
    /// No side lobes: those are a muzzle brake's, and none of the five guns
    /// has one - drawn anyway, they made a cross of the flash.
    /// </summary>
    private static readonly Shader FlashShader = new()
    {
        Code = @"
shader_type spatial;
render_mode unshaded, cull_disabled, shadows_disabled, fog_disabled;
stencil_mode write, compare_always, 0;
uniform vec3 core : source_color = vec3(1.0, 0.98, 0.84);
uniform vec3 body : source_color = vec3(1.0, 0.80, 0.30);
uniform vec3 edge : source_color = vec3(1.0, 0.46, 0.10);
uniform vec3 rim : source_color = vec3(0.45, 0.10, 0.03);
uniform vec2 dir = vec2(1.0, 0.0);
uniform float along = 1.0;
uniform float frame = 0.0;
uniform float seed = 0.0;
uniform float size_px = 50.0;
uniform float bore_z = 0.0;
" + Toon.NoiseCode + @"
// 1 on the axis of a lobe from the origin along w, 0 at its edge: a
// teardrop as long as len, as wide as wide.
float lobe(vec2 p, vec2 w, float len, float wide) {
    float x = dot(p, w) / max(len, 1e-3);
    float y = dot(p, vec2(-w.y, w.x));
    if (x < -0.15 || x > 1.0) return -1.0;
    float half_w = wide * pow(max(1.0 - x, 0.0), 0.7) * smoothstep(-0.15, 0.2, x);
    return 1.0 - abs(y) / max(half_w, 1e-3);
}
vec2 turn(vec2 w, float a) {
    return vec2(w.x * cos(a) - w.y * sin(a), w.x * sin(a) + w.y * cos(a));
}
void fragment() {
    // Quad units: the quad is 2.4 flash lengths across, the flash length 1,
    // the muzzle at the middle.
    vec2 p = (UV - 0.5) * vec2(2.4, -2.4);
    vec2 side = vec2(-dir.y, dir.x);
    // How much of ahead the screen leaves: pointed at the eye, the fire is
    // round and on the muzzle.
    float ahead = mix(0.25, 1.0, along);
    float f;
    float cool = 0.0;
    // How far toward the eye of the bore's axis the fire's surface is, in
    // flash lengths: a body round the axis, not a sheet.
    float thick = 0.04;
    if (frame < 1.0) {
        f = lobe(p, dir, 0.34 * ahead, 0.12);
        f = max(f, lobe(p, turn(dir, 0.50), 0.24 * ahead, 0.05));
        f = max(f, lobe(p, turn(dir, -0.50), 0.24 * ahead, 0.05));
        f = max(f, lobe(p, turn(dir, 1.05), 0.15 * ahead, 0.04));
        f = max(f, lobe(p, turn(dir, -1.05), 0.15 * ahead, 0.04));
        f = max(f, 1.0 - length(p) / 0.09);
    } else {
        float g = frame - 1.0;
        float grow = mix(0.85, 1.0, smoothstep(0.0, 1.0, g)) * (1.0 + 0.05 * max(g - 2.0, 0.0));
        vec2 c = dir * (0.50 * ahead * along);
        float ra = mix(0.30, 0.44, along) * grow;
        float rc = 0.30 * grow;
        vec2 q = p - c;
        // Its back, toward the muzzle, flatter than its front.
        float x = dot(q, dir);
        vec2 u = vec2(x / (x < 0.0 ? ra * 0.75 : ra), dot(q, side) / rc);
        // Lumpy from the first: a smooth egg read as a sticker, a sun.
        float ang0 = atan(u.y, u.x);
        float lump = noise3(vec3(cos(ang0) * 1.3, sin(ang0) * 1.3, seed * 5.0 + g * 0.2)) - 0.5;
        f = 1.0 - length(u) + 0.30 * lump;
        thick = rc * sqrt(clamp(f, 0.0, 1.0));
        // The neck back to the muzzle, thinning as the ball goes out - from
        // the muzzle, not behind it.
        float neck = 0.8 * lobe(p, dir, 0.50 * ahead * along + 0.08, 0.08 * (1.0 - 0.1 * g));
        if (neck > f) {
            f = neck;
            thick = 0.03;
        }
        cool = smoothstep(1.5, 6.5, g);
        // Torn into tongues round its edge as it cools, and eaten through.
        float ang = atan(u.y, u.x);
        float n = noise3(vec3(cos(ang) * 1.8, sin(ang) * 1.8, seed * 13.0 + g * 0.35));
        float m = noise3(vec3(q * 9.0, seed * 7.0 + g * 0.5));
        f -= cool * (0.30 + 0.55 * n) + cool * cool * 0.40 + (m - 0.5) * 0.30 * cool;
    }
    if (f < 0.0) discard;
    // Its depth: the point of the bore the pixel lies over - how far ahead of
    // the muzzle on the screen, over how much of the bore the screen shows -
    // nearer the eye by the fire's thickness there. So the tube in front of
    // a gun pointed away hides the neck and the fire's foot, as it should.
    float ahead_len = clamp(dot(p, dir) / max(along, 0.25), 0.0, 1.5);
    float z = VERTEX.z + (bore_z * ahead_len + thick) * size_px;
    vec4 clip = PROJECTION_MATRIX * vec4(VERTEX.xy, z, 1.0);
    DEPTH = clip.z / clip.w * 0.5 + 0.5;
    vec3 col = rim;
    if (frame < 1.0) {
        col = f > 0.20 ? core : (f > 0.07 ? body : edge);
    } else {
        if (f > 0.08 + 0.12 * cool) col = edge;
        if (f > 0.26 + 0.34 * cool) col = body;
        if (f > 0.52 + 0.70 * cool) col = core;
    }
    ALBEDO = col;
}
",
    };

    /// <summary>
    /// The blast wave: a ring on a quad lying on the ground, <c>radius</c>
    /// and <c>width</c> as shares of the quad's half, inked both sides; as it
    /// goes (<c>fade</c>) it thins and a noise round it breaks it into arcs.
    /// <see cref="CelDeath"/>'s blast wave too.
    /// </summary>
    internal static readonly Shader RingShader = new()
    {
        Code = @"
shader_type spatial;
render_mode unshaded, cull_disabled, shadows_disabled, fog_disabled;
stencil_mode write, compare_always, 0;
uniform vec3 tone : source_color = vec3(0.96, 0.88, 0.72);
uniform vec3 ink_tone : source_color = vec3(0.43, 0.40, 0.32);
uniform float radius = 0.5;
uniform float width = 0.05;
uniform float ink = 0.01;
uniform float fade = 0.0;
uniform float seed = 0.0;
" + Toon.NoiseCode + @"
void fragment() {
    vec2 p = (UV - 0.5) * 2.0;
    float d = length(p);
    float ang = atan(p.y, p.x);
    float n = noise3(vec3(cos(ang) * 2.5, sin(ang) * 2.5, seed * 11.0));
    if (n < fade * 0.95) discard;
    float w = width * (0.6 + 0.8 * n) * (1.0 - 0.7 * fade);
    float band = w - abs(d - radius);
    if (band < 0.0) discard;
    ALBEDO = band < ink ? ink_tone : tone;
}
",
    };
}
