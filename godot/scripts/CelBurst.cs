using Godot;

namespace TankSpriteTest;

/// <summary>
/// A round bursting in the ground beside a 3D tank, in the model's look. The
/// board's burst (<see cref="SheetBlast"/>, a rendered sheet) and crater
/// (<see cref="PitArt"/>, a soft blot) are left as they are for the 2D tanks;
/// with a model on the board the 3D bench runs this instead.
///
/// <list type="bullet">
/// <item><b>Fire</b> - <see cref="CelBlast"/>'s field (its shader), off the
/// ground and up: the detonation's core and rays, the fireball pushed up and
/// spread along the ground, cooling into tongues, the blast wave's ring.
/// A short light with it.</item>
/// <item><b>The fountain</b> - the earth the charge throws up, in one cloud
/// (<see cref="CelCloud"/>): puffs thrown up in a narrow cone and stopped by
/// the air at different heights, dark soil at first and paling to dust as
/// they hang, swell, sink and drift - a column, not a ball.</item>
/// <item><b>Clods</b> - dark lumps of soil thrown up and out on arcs,
/// landing round the crater and gone where they land.</item>
/// <item><b>Dust</b> - a low ring of pale earth running out over the ground
/// round the foot, a beat after the fire.</item>
/// <item><b>The crater</b> - <see cref="CelCrater"/>: a rim of thrown earth
/// the sun lights as it lights the hulls, round a flat bowl, and clods. It
/// stays, as the board's does.</item>
/// </list>
///
/// Closed form from the moment of the burst, every piece arithmetic on its
/// index and the time since, so a capture repeats to the pixel. Lengths are
/// shares of the hull's length on the board (<see cref="Build"/>); the
/// crater's are shares of the hex.
/// </summary>
public sealed partial class CelBurst : Node3D
{
    /// <summary>How long the fire lasts, s, and the fireball's radius, hull
    /// lengths - a little bigger than a burst on a plate: the ground does not
    /// take any of it.</summary>
    public float FireTime = 9.0f / 60.0f, FireSize = 0.34f;
    public Color GlowColor = new(1.0f, 0.70f, 0.34f);
    public float GlowEnergy = 0.5f, GlowReach = 0.32f;

    /// <summary>The fountain: puffs, their life, s, how high they go, hull
    /// lengths, and how wide the cone, degrees off the upright.</summary>
    public int EarthPuffs = 48;
    public float EarthLife = 2.0f, EarthHigh = 1.05f, EarthCone = 26.0f;
    /// <summary>How fast the air stops the throw, s.</summary>
    public float EarthStop = 0.16f;
    /// <summary>A puff's radius at birth and at the end, hull lengths.</summary>
    public float EarthBorn = 0.05f, EarthGrown = 0.15f;
    /// <summary>The soil's grey fresh, and the dust's it pales to.</summary>
    public float EarthDark = 0.36f, EarthPale = 0.66f;
    public Color EarthTint = new(0.86f, 0.72f, 0.55f);

    /// <summary>Clods: how many, how fast up, hull lengths a second, how wide
    /// the cone, degrees, and how hard they fall.</summary>
    public int Clods = 22;
    public float ClodSpeed = 3.0f, ClodCone = 40.0f, ClodFall = 7.5f;
    public Color ClodDark = new(0.17f, 0.12f, 0.08f), ClodLit = new(0.38f, 0.28f, 0.18f);

    /// <summary>The dust round the foot: puffs, life, s, reach, hull lengths.</summary>
    public int DustPuffs = 22;
    public float DustLife = 1.3f, DustReach = 0.7f;
    public float DustTone = 0.66f;
    public Color DustTint = new(0.92f, 0.82f, 0.64f);

    /// <summary>Where the wind takes it all, hull lengths a second - the
    /// shot's (<see cref="CelShot.Wind"/>).</summary>
    public Vector3 Wind = new(0.10f, 0.0f, -0.05f);
    /// <summary>How far apart two puffs still flow into one, hull lengths.</summary>
    public float Blend = 0.06f;

    /// <summary>The craters it leaves - set it up through this
    /// (<see cref="CelCrater.SunWay"/>).</summary>
    public CelCrater Craters { get; private set; } = null!;

    private float _hull = 150.0f;
    private float _since = -1.0f;
    private int _bursts;
    private Vector3 _at;
    private MeshInstance3D? _fire;
    private ShaderMaterial? _fireLook;
    private OmniLight3D? _glow;
    private MultiMesh? _clods;
    private CelCloud? _earth, _dust;
    private System.Func<Vector3, float>? _ground;


    /// <param name="hullPx">The tank's hull length on the board, px - every
    /// length of the burst is a share of it.</param>
    /// <param name="ground">The ground's height under a world point - where
    /// a clod lands.</param>
    public void Build(float hullPx, float squash, float rise, System.Func<Vector3, float> ground)
    {
        _hull = Mathf.Max(hullPx, 1.0f);
        _ground = ground;
        Craters = new CelCrater { Name = "Craters" };
        AddChild(Craters);
        Craters.Build(squash, rise);
        _fireLook = new ShaderMaterial { Shader = CelBlast.FireShader };
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
        _clods = new MultiMesh
        {
            Mesh = new BoxMesh { Size = Vector3.One },
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true,
            InstanceCount = Clods,
            VisibleInstanceCount = 0,
        };
        AddChild(new MultiMeshInstance3D
        {
            Name = "Clods", Multimesh = _clods,
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                VertexColorUseAsAlbedo = true,
            },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });
        _earth = new CelCloud(this, "Earth", EarthTint, Blend * _hull);
        _dust = new CelCloud(this, "Dust", DustTint, Blend * _hull);
    }

    /// <summary>A round bursts in the ground at <paramref name="at"/> (world,
    /// on the ground), and leaves a crater <paramref name="radius"/> out to its
    /// rim's foot, world px. A burst still running is cut short; its crater
    /// stays.</summary>
    public void Burst(Vector3 at, float radius)
    {
        _since = 0.0f;
        _bursts++;
        _at = at;
        Craters.Dig(at, radius);
    }

    /// <summary>The burst stopped and every crater off the board.</summary>
    public void Reset()
    {
        _since = -1.0f;
        if (_fire is not null)
            _fire.Visible = false;
        if (_glow is not null)
            _glow.Visible = false;
        if (_clods is not null)
            _clods.VisibleInstanceCount = 0;
        _earth?.Hide();
        _dust?.Hide();
        Craters?.Reset();
    }

    public void Tick(float dt, Basis eye)
    {
        Craters?.Tick(dt);
        if (_since < 0.0f || _fire is null || _earth is null || _dust is null)
            return;
        float t = _since;
        _since += dt;
        if (t > Mathf.Max(EarthLife, DustLife) * 1.4f + 0.1f)
        {
            _since = -1.0f;
            _fire.Visible = false;
            _glow!.Visible = false;
            _clods!.VisibleInstanceCount = 0;
            _earth.Hide();
            _dust.Hide();
            return;
        }
        Fire(t, eye);
        Throw(t);
        Fountain(t);
        Dust(t);
        _earth.Draw(eye);
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
        float g = t * 60.0f * (8.0f / 60.0f) / Mathf.Max(FireTime, 1e-3f);
        Vector3 right = eye.X.Normalized(), up = eye.Y.Normalized(), back = eye.Z.Normalized();
        var screen = new Vector2(Vector3.Up.Dot(right), Vector3.Up.Dot(up));
        float along = screen.Length();
        Vector2 dir = along > 1e-4f ? screen / along : Vector2.Up;
        float size = FireSize * _hull;
        // Up off the ground, a little more than off a plate: the ground
        // gives the ball nowhere else to go.
        float push = Mathf.Lerp(0.25f, 0.75f, Mathf.SmoothStep(0.0f, 4.0f, g));
        _fire.GlobalTransform = new Transform3D(
            new Basis(right * (3.2f * size), up * (3.2f * size), back), _at + Vector3.Up * (push * size));
        _fireLook!.SetShaderParameter("dir", dir);
        _fireLook.SetShaderParameter("along", Mathf.Clamp(along, 0.0f, 1.0f));
        _fireLook.SetShaderParameter("frame", g);
        _fireLook.SetShaderParameter("size_px", size);
        _fireLook.SetShaderParameter("seed", CelPuff.Hash(_bursts, 167));
        _fireLook.SetShaderParameter("ink", Toon.InkWidth / size);
        _glow.GlobalPosition = _at + Vector3.Up * (0.12f * _hull);
        _glow.OmniRange = GlowReach * _hull;
        _glow.LightEnergy = GlowEnergy * (g < 1.0f ? 1.0f : 0.7f * (1.0f - f));
    }

    /// <summary>The clods: up and out on arcs, dark soil catching a little
    /// light, gone where they come down.</summary>
    private void Throw(float t)
    {
        int shown = 0;
        for (int k = 0; k < Clods; k++)
        {
            float h1 = CelPuff.Hash(_bursts * 97 + k, 211), h2 = CelPuff.Hash(_bursts * 97 + k, 223);
            float h3 = CelPuff.Hash(_bursts * 97 + k, 227);
            float spread = Mathf.DegToRad(ClodCone) * (0.25f + 0.75f * Mathf.Sqrt(h1));
            float ang = h2 * Mathf.Tau;
            Vector3 dir = new Vector3(Mathf.Sin(spread) * Mathf.Cos(ang), Mathf.Cos(spread), Mathf.Sin(spread) * Mathf.Sin(ang));
            float speed = ClodSpeed * _hull * (0.45f + 0.7f * h3);
            float fall = ClodFall * _hull;
            Vector3 at = _at + dir * (speed * t) + Vector3.Down * (0.5f * fall * t * t);
            if (t > 0.05f && at.Y <= (_ground?.Invoke(at) ?? _at.Y))
                continue;
            Vector3 vel = dir * speed + Vector3.Down * (fall * t);
            float size = (2.6f + 3.2f * h1) * (_hull / 150.0f);
            // A lump tumbling, not a streak: square-ish, turned by its index.
            var turn = new Basis(new Vector3(h2, h3, h1).Normalized(), t * (6.0f + 8.0f * h3) + h1 * 6.0f);
            _clods!.SetInstanceTransform(shown, new Transform3D(turn.Scaled(new Vector3(size, size * 0.8f, size * 0.9f)), at));
            _clods.SetInstanceColor(shown, ClodDark.Lerp(ClodLit, Mathf.Clamp(vel.Normalized().Y * 0.5f + 0.5f * h2, 0.0f, 1.0f) * 0.6f));
            shown++;
        }
        _clods!.VisibleInstanceCount = shown;
    }

    /// <summary>The fountain: a column of earth thrown up, stopped by the air
    /// at different heights, paling to dust as it hangs and sinks.</summary>
    private void Fountain(float t)
    {
        _earth!.Clear();
        for (int k = 0; k < EarthPuffs; k++)
        {
            float h1 = CelPuff.Hash(_bursts * 131 + k, 233), h2 = CelPuff.Hash(_bursts * 131 + k, 239);
            float h3 = CelPuff.Hash(_bursts * 131 + k, 241);
            // A third of it a beat later, from the foot: the column keeps its
            // foot in the crater rather than leaving it as one lump.
            bool late = k % 3 == 2;
            float born = late ? 0.04f + 0.18f * h3 : 0.012f * (k % 4);
            float tt = t - born;
            if (tt < 0.0f)
                continue;
            float life = EarthLife * (0.55f + 0.6f * h3) * (late ? 0.75f : 1.0f);
            float a = tt / life;
            if (a >= 1.0f)
                continue;
            float spread = Mathf.DegToRad(EarthCone) * Mathf.Sqrt(h1);
            float ang = h2 * Mathf.Tau;
            var way = new Vector3(Mathf.Sin(spread) * Mathf.Cos(ang), Mathf.Cos(spread), Mathf.Sin(spread) * Mathf.Sin(ang));
            // Evenly up the column, not in a cap and a foot.
            float reach = EarthHigh * _hull * ((k + 0.5f) / EarthPuffs * 0.85f + 0.15f * h2) * (late ? 0.4f : 1.0f);
            float thrown = reach * (1.0f - Mathf.Exp(-tt / Mathf.Max(EarthStop * (0.6f + 0.8f * h1), 1e-3f)));
            // Heavy with soil, it comes down again a little once the throw is
            // spent; the dust it turns into hangs.
            float sink = 0.10f * _hull * Mathf.Pow(Mathf.Max(a - 0.25f, 0.0f), 1.5f);
            Vector3 at = _at + way * thrown + Vector3.Down * sink + Wind * (_hull * tt)
                         + new Vector3(way.X, 0.0f, way.Z) * (0.12f * _hull * a);
            float big = 0.6f + 0.9f * h3;
            float r = _hull * big * Mathf.Lerp(EarthBorn, EarthGrown, 1.0f - Mathf.Pow(1.0f - a, 2.2f))
                      * Mathf.SmoothStep(0.0f, 0.05f, a)
                      // Only as the fire dies, the shot's smoke's lesson: born
                      // in it, it was a brown lump in the fireball.
                      * Mathf.SmoothStep(0.25f * FireTime, 0.9f * FireTime, t);
            float tone = Mathf.Lerp(EarthDark, EarthPale, Mathf.SmoothStep(0.15f, 0.9f, a)) * (0.92f + 0.16f * h1);
            _earth.Add(at, r, tone, h1, Mathf.SmoothStep(0.45f, 1.0f, a), a);
        }
    }

    private void Dust(float t)
    {
        _dust!.Clear();
        for (int k = 0; k < DustPuffs; k++)
        {
            float h1 = CelPuff.Hash(_bursts * 173 + k, 251), h2 = CelPuff.Hash(_bursts * 173 + k, 257);
            float h3 = CelPuff.Hash(_bursts * 173 + k, 263);
            float tt = t - 0.05f;
            if (tt < 0.0f)
                continue;
            float life = DustLife * (0.7f + 0.5f * h3);
            float a = tt / life;
            if (a >= 1.0f)
                continue;
            float ang = (k + 0.5f * h1) / DustPuffs * Mathf.Tau;
            var way = new Vector3(Mathf.Cos(ang), 0.0f, Mathf.Sin(ang));
            float reach = DustReach * _hull * (0.55f + 0.45f * h2);
            float out_ = reach * (1.0f - Mathf.Exp(-tt / 0.22f));
            Vector3 at = _at + way * out_ + Vector3.Up * (0.03f * _hull + 0.06f * _hull * a) + Wind * (_hull * tt);
            float r = _hull * Mathf.Lerp(0.03f, 0.075f * (0.7f + 0.6f * h3), 1.0f - Mathf.Pow(1.0f - a, 2.0f))
                      * Mathf.SmoothStep(0.0f, 0.06f, a);
            _dust.Add(at, r, DustTone * (0.9f + 0.2f * h1), h2, Mathf.SmoothStep(0.25f, 1.0f, a), a);
        }
    }
}
