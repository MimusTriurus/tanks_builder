using System;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// A 3D tank's death in the model's own look: the ammunition going up out of
/// the turret ring. The sprites' fireball (<see cref="ProcBall"/>) is left as
/// it is for the 2D tanks; on the model it was a soft orange haze over half
/// the screen, and the 3D bench runs this instead - for the knock-out's flash
/// too, smaller (<see cref="Blow"/>'s <c>might</c>).
///
/// <list type="bullet">
/// <item><b>Fire</b> - a cluster of fireballs, each <see cref="CelBlast"/>'s
/// fire field slowed: the main one out of the ring and climbing, a smaller
/// one thrown higher as the column's head, two out to the sides and one
/// away from the eye, each a beat after the first. Each goes through the
/// field's stages - a white core, a lumpy ball, cooling and torn into
/// tongues - on its own clock, so the cluster tears unevenly. It hides the
/// turret leaving.</item>
/// <item><b>Fire wave</b> - the blast rolling out over the ground as one
/// wall of fire round the tank, out to the middles of the six neighbouring
/// cells (<see cref="WaveReach"/>): a tank blowing up sets every
/// tank next to it alight (docs/gdd/states.md), and this is the picture of
/// that. Only a wall stops it - the face of ground higher than the blast's;
/// over lower ground it goes on at the blast's height.</item>
/// <item><b>Light</b> - a hard flash on the tank and the ground, dying in
/// <see cref="GlowTime"/>: the one place the ramp's flood is wanted.</item>
/// <item><b>Blast wave</b> - <see cref="CelShot"/>'s ring over the ground,
/// round the tank, bigger and slower, out ahead of the fire.</item>
/// <item><b>Soot</b> - one cloud (<see cref="CelCloud"/>) fed from the ring
/// for a while: a column climbing and a cap spreading at its head, darkest
/// fresh, greying; the burning wreck's own smoke (<see cref="CelBurn"/>)
/// takes over under it.</item>
/// <item><b>Dust</b> - the ground thrown up behind the fire wave.</item>
/// <item><b>Fragments</b> - hot pieces out and up round the ring, falling
/// in arcs and going dark. The model's own debris flies as well.</item>
/// </list>
///
/// Closed form from the moment of the blast; lengths are shares of the
/// hull's length on the board, and the wave's reach of the cell's width
/// (<see cref="Build"/>).
/// </summary>
public sealed partial class CelDeath : Node3D
{
    /// <summary>A fireball of the cluster: where it goes, hull lengths, from
    /// the ring (to the eye's right, up, away from the eye) at the start and
    /// at its highest; its radius; how long after the blast it starts and how
    /// long its stages take, s.</summary>
    private readonly record struct Ball(Vector3 From, Vector3 To, float R, float Delay, float Life);

    private static readonly Ball[] Balls =
    {
        new(new Vector3(0.0f, 0.10f, 0.0f), new Vector3(0.0f, 0.45f, 0.0f), 0.40f, 0.0f, 0.60f),
        new(new Vector3(0.0f, 0.22f, 0.0f), new Vector3(0.05f, 0.78f, 0.0f), 0.30f, 0.03f, 0.50f),
        new(new Vector3(0.18f, 0.08f, 0.0f), new Vector3(0.42f, 0.28f, 0.0f), 0.24f, 0.02f, 0.42f),
        new(new Vector3(-0.18f, 0.08f, 0.0f), new Vector3(-0.40f, 0.22f, 0.0f), 0.22f, 0.04f, 0.40f),
        new(new Vector3(0.0f, 0.10f, 0.12f), new Vector3(-0.05f, 0.30f, 0.34f), 0.26f, 0.05f, 0.46f),
    };

    /// <summary>How many of <see cref="Balls"/> a knock-out's flash has.</summary>
    private const int SmallBalls = 2;

    /// <summary>The fire wave: how far its front rolls, cell widths - its
    /// leading edge, a little ahead of the front, at the neighbours' middles
    /// (0.866 of the corner-to-corner width the cells are measured by); how
    /// fast it stops, s; how long it burns, s; how thick the roll of fire
    /// is at the start and at the far end, hull lengths, before its tongues.</summary>
    public float WaveReach = 0.78f, WaveStop = 0.20f, WaveLife = 0.70f;
    public float WaveLow = 0.20f, WaveTall = 0.30f;
    /// <summary>How much higher ground has to stand than the blast's to stop
    /// the wave, cell widths: a rise's face is a wall.</summary>
    public float WaveStep = 0.08f;
    /// <summary>The ground's height under a point in the world - the walls
    /// that stop the wave, and the dust. Null is flat, at the blast's foot.</summary>
    public Func<Vector3, float>? Ground;

    /// <summary>Bearings round the blast the walls are looked for on - the
    /// shader's array.</summary>
    private const int Bearings = 64;

    /// <summary>The light: colour, energy, reach in cell widths, how long, s -
    /// over the neighbours as well.</summary>
    public Color GlowColor = new(1.0f, 0.72f, 0.38f);
    public float GlowEnergy = 1.4f, GlowReach = 1.1f, GlowTime = 0.55f;

    /// <summary>The blast wave over the ground: how long, s, how far, cell
    /// widths, how thick at the start as a share of that.</summary>
    public float RingTime = 0.35f, RingReach = 1.1f, RingWidth = 0.03f;
    public Color RingColor = new(0.96f, 0.88f, 0.72f);

    /// <summary>Soot: puffs, how long the ring feeds them, s, their life, s,
    /// how high the column climbs and how wide its cap, hull lengths, radius
    /// at birth and grown, grey fresh and old.</summary>
    public int SootPuffs = 44;
    public float SootFeed = 0.7f, SootLife = 3.0f, SootClimb = 1.5f, SootCap = 0.55f;
    public float SootBorn = 0.06f, SootGrown = 0.22f;
    public float SootDark = 0.24f, SootGrey = 0.34f, SootBlend = 0.04f;
    public Color SootTint = new(0.95f, 0.92f, 0.88f);

    /// <summary>Dust off the ground: puffs, life, s, reach, hull lengths.</summary>
    public int DustPuffs = 48;
    public float DustLife = 1.6f, DustReach = 0.80f, DustTone = 0.56f;
    public Color DustTint = new(0.92f, 0.82f, 0.64f);

    /// <summary>Fragments: how many, life, s, speed, hull lengths a second,
    /// how wide round the vertical, deg, how hard they fall.</summary>
    public int Fragments = 26;
    public float FragmentLife = 1.1f, FragmentSpeed = 3.2f, FragmentCone = 75.0f, FragmentFall = 5.0f;

    /// <summary>Where the wind takes soot and dust, hull lengths a second.</summary>
    public Vector3 Wind = new(0.12f, 0.0f, -0.06f);

    // ------------------------------------------------------------ the machinery

    private float _hull = 150.0f, _hex = 250.0f;
    private MeshInstance3D? _wall;
    private ShaderMaterial? _wallLook;
    private readonly float[] _stops = new float[Bearings];
    private float _since = -1.0f;
    private int _blasts;
    private float _might = 1.0f;
    private bool _grounded;
    private Vector3 _at, _foot;
    private readonly MeshInstance3D[] _fires = new MeshInstance3D[Balls.Length];
    private readonly ShaderMaterial[] _fireLooks = new ShaderMaterial[Balls.Length];
    private OmniLight3D? _glow;
    private MeshInstance3D? _ring;
    private ShaderMaterial? _ringLook;
    private MultiMesh? _bits;
    private CelCloud? _soot, _dust;

    public void Build(float hullPx, float hexPx)
    {
        _hull = Mathf.Max(hullPx, 1.0f);
        _hex = Mathf.Max(hexPx, 1.0f);
        _wallLook = new ShaderMaterial { Shader = WallShader };
        _wallLook.SetShaderParameter("lump_px", 0.20f * _hull);
        _wallLook.SetShaderParameter("ink_px", Toon.InkWidth);
        _wall = new MeshInstance3D
        {
            Name = "Wave", Mesh = new QuadMesh { Size = Vector2.One }, MaterialOverride = _wallLook,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Visible = false,
        };
        AddChild(_wall);
        for (int i = 0; i < Balls.Length; i++)
        {
            _fireLooks[i] = new ShaderMaterial { Shader = CelBlast.FireShader };
            _fireLooks[i].SetShaderParameter("wave_on", 0.0f);
            _fireLooks[i].SetShaderParameter("rays", 0.45f);
            _fires[i] = new MeshInstance3D
            {
                Name = $"Fire{i}", Mesh = new QuadMesh { Size = Vector2.One }, MaterialOverride = _fireLooks[i],
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Visible = false,
            };
            AddChild(_fires[i]);
        }
        _glow = new OmniLight3D
        {
            Name = "Glow", LightColor = GlowColor, OmniAttenuation = 0.0f, ShadowEnabled = false,
            LightEnergy = 0.0f, Visible = false,
        };
        AddChild(_glow);
        _ringLook = new ShaderMaterial { Shader = CelShot.RingShader };
        _ringLook.SetShaderParameter("tone", RingColor);
        _ringLook.SetShaderParameter("ink_tone", RingColor * 0.45f);
        _ring = new MeshInstance3D
        {
            Name = "Ring", Mesh = new QuadMesh { Size = Vector2.One }, MaterialOverride = _ringLook,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Visible = false,
        };
        AddChild(_ring);
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
        _dust = new CelCloud(this, "Dust", DustTint, 0.05f * _hull);
    }

    /// <summary>The tank blows up at <paramref name="at"/> (the ring, in the
    /// world) over the ground at <paramref name="foot"/>. <paramref name="might"/>
    /// 1 is the death; less is the knock-out's flash - two fireballs, smaller,
    /// a little soot, and with <paramref name="grounded"/> false no blast wave
    /// and no dust.</summary>
    public void Blow(Vector3 at, Vector3 foot, float might, bool grounded)
    {
        _since = 0.0f;
        _blasts++;
        _at = at;
        _foot = foot;
        _might = Mathf.Clamp(might, 0.05f, 1.0f);
        _grounded = grounded;
        Walls();
        _wallLook?.SetShaderParameter("stops", _stops);
    }

    /// <summary>When the fire wave's front gets <paramref name="d"/> px out
    /// from the blast's foot, s after <see cref="Blow"/> - <see cref="Wave"/>'s
    /// radius the other way round; infinity past its reach. A blast that is
    /// not grounded has no wave.</summary>
    public float WaveArrives(float d)
    {
        float from = 0.3f * _hull, reach = WaveReach * _hex;
        if (d <= from)
            return 0.02f;
        float k = (d - from) / Mathf.Max(reach - from, 1e-3f);
        return k >= 1.0f ? float.PositiveInfinity : 0.02f - WaveStop * Mathf.Log(1.0f - k);
    }

    public void Reset()
    {
        _since = -1.0f;
        foreach (MeshInstance3D f in _fires)
            if (f is not null)
                f.Visible = false;
        if (_wall is not null)
            _wall.Visible = false;
        if (_glow is not null)
            _glow.Visible = false;
        if (_ring is not null)
            _ring.Visible = false;
        if (_bits is not null)
            _bits.VisibleInstanceCount = 0;
        _soot?.Hide();
        _dust?.Hide();
    }

    /// <summary>How big everything is against the death's: the knock-out's
    /// flash is a third of its might and about half its size.</summary>
    private float Scale => Mathf.Sqrt(_might);

    public void Tick(float dt, Basis eye)
    {
        if (_since < 0.0f || _soot is null || _dust is null)
            return;
        float t = _since;
        _since += dt;
        if (t > SootFeed + SootLife * 1.2f + 0.2f)
        {
            Reset();
            return;
        }
        Fire(t, eye);
        Wave(t, eye);
        Light(t);
        Ring(t);
        Fly(t);
        Soot(t);
        Dust(t);
        _soot.Draw(eye);
        _dust.Draw(eye);
    }

    private void Fire(float t, Basis eye)
    {
        Vector3 right = eye.X.Normalized(), up = eye.Y.Normalized(), back = eye.Z.Normalized();
        // The eye's right and back, flat: the balls go to its sides and back
        // over the deck as the screen has them.
        Vector3 side = new Vector3(right.X, 0.0f, right.Z).Normalized();
        Vector3 away = new Vector3(-back.X, 0.0f, -back.Z).Normalized();
        int count = _might < 0.99f ? SmallBalls : Balls.Length;
        for (int i = 0; i < Balls.Length; i++)
        {
            Ball b = Balls[i];
            float tt = t - b.Delay;
            float life = b.Life * Mathf.Lerp(0.6f, 1.0f, _might);
            bool on = i < count && tt >= 0.0f && tt < life;
            _fires[i].Visible = on;
            if (!on)
                continue;
            float go = 1.0f - Mathf.Exp(-tt / (0.35f * life));
            Vector3 off = b.From.Lerp(b.To, go) * Scale * (_grounded ? 1.35f : 1.0f);
            Vector3 mid = _at + (side * off.X + Vector3.Up * off.Y + away * off.Z) * _hull;
            // The death's cluster bigger: beside the wave it was a small fire
            // in the middle of a big one.
            float size = b.R * Scale * _hull * (_grounded ? 1.35f : 1.0f);
            _fires[i].GlobalTransform = new Transform3D(new Basis(right * (3.2f * size), up * (3.2f * size), back), mid);
            ShaderMaterial m = _fireLooks[i];
            m.SetShaderParameter("dir", Vector2.Up);
            m.SetShaderParameter("along", 0.0f);
            // The field's eight frames over this ball's life - but the
            // detonation, its frame 0, only the first ball's and only two
            // frames long: slowed with the rest it was a small star for four
            // frames before any fire, and the later balls each flashed one
            // again in the middle of the fireball.
            float star = 2.0f / 60.0f;
            float g = i == 0 && tt < star ? 0.0f
                    : 1.0f + 7.0f * Mathf.Max(tt - (i == 0 ? star : 0.0f), 0.0f) / (life - (i == 0 ? star : 0.0f));
            // Out before the field's last scraps: slowed, they hung over the
            // soot as red bows.
            if (g >= 6.2f)
            {
                _fires[i].Visible = false;
                continue;
            }
            m.SetShaderParameter("frame", g);
            m.SetShaderParameter("size_px", size);
            m.SetShaderParameter("seed", CelPuff.Hash(_blasts * 7 + i, 113));
            m.SetShaderParameter("ink", Toon.InkWidth / size);
        }
    }

    /// <summary>
    /// The fire wave: one roll of fire, <see cref="WallShader"/>, on a quad
    /// facing the eye over the whole ring; its radius and thickness this frame,
    /// how far it has cooled, and where each bearing is stopped.
    /// </summary>
    private void Wave(float t, Basis eye)
    {
        float tt = t - 0.02f;
        bool on = _grounded && tt >= 0.0f && tt < WaveLife;
        _wall!.Visible = on;
        if (!on)
            return;
        Vector3 right = eye.X.Normalized(), up = eye.Y.Normalized(), back = eye.Z.Normalized();
        float reach = WaveReach * _hex;
        float go = 1.0f - Mathf.Exp(-tt / WaveStop);
        float radius = 0.3f * _hull + (reach - 0.3f * _hull) * go;
        float thick = _hull * Mathf.Lerp(WaveLow, WaveTall, go);
        float half = radius + thick * 4.0f;
        _wall.GlobalTransform = new Transform3D(new Basis(right * (2.0f * half), up * (2.0f * half), back),
                                                _foot + Vector3.Up * thick);
        _wallLook!.SetShaderParameter("centre", _foot);
        _wallLook.SetShaderParameter("radius", radius);
        _wallLook.SetShaderParameter("thick", thick);
        _wallLook.SetShaderParameter("cool", Mathf.SmoothStep(0.12f, WaveLife, tt));
        _wallLook.SetShaderParameter("time_s", tt);
        _wallLook.SetShaderParameter("seed", CelPuff.Hash(_blasts, 197));
    }

    /// <summary>
    /// Where the wave stops on each of <see cref="Bearings"/> bearings round
    /// the blast, px from its foot: the first point out along it where the
    /// ground stands higher than the blast's own by <see cref="WaveStep"/> -
    /// the face of a rise is a wall. Lower ground does not stop it: the wave
    /// goes on over it at the blast's height.
    /// </summary>
    private void Walls()
    {
        float reach = WaveReach * _hex;
        for (int b = 0; b < Bearings; b++)
        {
            float ang = (b + 0.5f) / Bearings * Mathf.Tau - Mathf.Pi;
            var way = new Vector3(Mathf.Cos(ang), 0.0f, Mathf.Sin(ang));
            _stops[b] = 2.0f * reach;
            if (Ground is null)
                continue;
            for (float d = 0.2f * _hull; d <= reach * 1.05f; d += 0.03f * _hex)
                if (Ground(_foot + way * d) > _foot.Y + WaveStep * _hex)
                {
                    _stops[b] = d;
                    break;
                }
        }
    }

    /// <summary>Whether the ground out along <paramref name="way"/> to
    /// <paramref name="out_"/> is open to the blast - no rise in the way - and
    /// its height there.</summary>
    private bool Open(Vector3 way, float out_, out float ground)
    {
        ground = _foot.Y;
        float ang = Mathf.Atan2(way.Z, way.X);
        int b = Mathf.Clamp((int)((ang + Mathf.Pi) / Mathf.Tau * Bearings), 0, Bearings - 1);
        if (out_ > _stops[b])
            return false;
        if (Ground is not null)
            ground = Mathf.Min(Ground(_foot + way * out_), _foot.Y);
        return true;
    }

    private void Light(float t)
    {
        bool on = t < GlowTime;
        _glow!.Visible = on;
        if (!on)
            return;
        _glow.GlobalPosition = _at + Vector3.Up * (0.3f * _hull * Scale);
        _glow.OmniRange = _grounded ? GlowReach * _hex : 1.4f * _hull * Scale;
        float f = t / GlowTime;
        _glow.LightEnergy = GlowEnergy * (t < 3.0f / 60.0f ? 1.0f : 0.7f * (1.0f - f) * (1.0f - f)) * Scale;
    }

    private void Ring(float t)
    {
        float f = t / Mathf.Max(RingTime, 1e-3f);
        bool on = _grounded && f < 1.0f;
        _ring!.Visible = on;
        if (!on)
            return;
        float reach = RingReach * _hex;
        float r = reach * (1.0f - Mathf.Exp(-t / (0.3f * RingTime))) / (1.0f - Mathf.Exp(-1.0f / 0.3f));
        float half = reach * 1.1f;
        _ring.GlobalTransform = new Transform3D(
            new Basis(new Vector3(2.0f * half, 0.0f, 0.0f), new Vector3(0.0f, 0.0f, -2.0f * half), Vector3.Up),
            _foot + Vector3.Up * 0.6f);
        _ringLook!.SetShaderParameter("radius", r / half);
        _ringLook.SetShaderParameter("width", RingWidth * reach / half);
        _ringLook.SetShaderParameter("ink", Toon.InkWidth / half);
        _ringLook.SetShaderParameter("fade", f);
        _ringLook.SetShaderParameter("seed", CelPuff.Hash(_blasts, 127));
    }

    private void Fly(float t)
    {
        int shown = 0;
        int count = _grounded ? Fragments : Fragments / 3;
        for (int k = 0; k < count; k++)
        {
            float h1 = CelPuff.Hash(_blasts * 89 + k, 131), h2 = CelPuff.Hash(_blasts * 89 + k, 137);
            float h3 = CelPuff.Hash(_blasts * 89 + k, 139);
            float life = FragmentLife * (0.5f + 0.7f * h3) * Scale;
            float a = t / life;
            if (a >= 1.0f)
                continue;
            float spread = Mathf.DegToRad(FragmentCone) * Mathf.Sqrt(h1);
            float ang = h2 * Mathf.Tau;
            var dir = new Vector3(Mathf.Cos(ang) * Mathf.Sin(spread), Mathf.Cos(spread), Mathf.Sin(ang) * Mathf.Sin(spread));
            float speed = FragmentSpeed * _hull * (0.45f + 0.8f * h3) * Scale;
            Vector3 vel = dir * speed + Vector3.Down * (FragmentFall * _hull * t);
            Vector3 at = _at + dir * (speed * t) + Vector3.Down * (0.5f * FragmentFall * _hull * t * t);
            // Not through the ground.
            if (at.Y < _foot.Y)
                continue;
            float len = Mathf.Clamp(vel.Length() / 60.0f * 0.7f, 2.5f, 8.0f);
            float thick = 2.2f + 1.6f * h1;
            Vector3 x = vel.Normalized();
            Vector3 y = x.Cross(Vector3.Up);
            if (y.LengthSquared() < 1e-6f)
                y = Vector3.Right;
            y = y.Normalized();
            Vector3 z = x.Cross(y).Normalized();
            _bits!.SetInstanceTransform(shown, new Transform3D(new Basis(x * len, y * thick, z * thick), at));
            Color hot = new(1.0f, 0.66f, 0.22f), cold = new(0.16f, 0.13f, 0.11f);
            _bits.SetInstanceColor(shown, hot.Lerp(cold, Mathf.SmoothStep(0.25f, 0.8f, a)));
            shown++;
        }
        _bits!.VisibleInstanceCount = shown;
    }

    private void Soot(float t)
    {
        _soot!.Clear();
        int count = Mathf.CeilToInt(SootPuffs * (_grounded ? 1.0f : 0.35f));
        float feed = SootFeed * Scale;
        for (int k = 0; k < count; k++)
        {
            float h1 = CelPuff.Hash(_blasts * 131 + k, 149), h2 = CelPuff.Hash(_blasts * 131 + k, 151);
            float h3 = CelPuff.Hash(_blasts * 131 + k, 157);
            // Fed from the ring for a while, the first ones at once.
            float born = feed * ((float)k / count) * (0.6f + 0.4f * h3);
            float tt = t - born;
            if (tt < 0.0f)
                continue;
            float life = SootLife * (0.6f + 0.5f * h3) * Scale * (1.0f - 0.4f * born / Mathf.Max(feed, 1e-3f));
            float a = tt / life;
            if (a >= 1.0f)
                continue;
            // Climbing fast and slowing; the ones that get highest spread
            // into the cap.
            float climb = SootClimb * _hull * Scale * (0.35f + 0.65f * h1) * (1.0f - Mathf.Exp(-a / 0.35f));
            float ang = h2 * Mathf.Tau;
            var round = new Vector3(Mathf.Cos(ang), 0.0f, Mathf.Sin(ang));
            float wide = _hull * Scale * (0.10f + SootCap * h1 * h1 * Mathf.SmoothStep(0.1f, 0.7f, a));
            Vector3 at = _at + Vector3.Up * climb + round * wide + Wind * (_hull * t);
            float r = _hull * Scale * Mathf.Lerp(SootBorn, SootGrown * (0.6f + 0.8f * h2), 1.0f - Mathf.Pow(1.0f - a, 2.2f))
                      // Under the fire first: seen through its first frames it
                      // was a dark lump in it.
                      * Mathf.SmoothStep(0.10f, 0.30f, t) * Mathf.SmoothStep(0.0f, 0.05f, a);
            float tone = Mathf.Lerp(SootDark, SootGrey, Mathf.SmoothStep(0.2f, 1.0f, a)) * (0.9f + 0.2f * h1);
            _soot.Add(at, r, tone, h1, Mathf.SmoothStep(0.4f, 1.0f, a), a);
        }
    }

    private void Dust(float t)
    {
        _dust!.Clear();
        if (!_grounded)
            return;
        for (int k = 0; k < DustPuffs; k++)
        {
            float h1 = CelPuff.Hash(_blasts * 173 + k, 163), h2 = CelPuff.Hash(_blasts * 173 + k, 167);
            float h3 = CelPuff.Hash(_blasts * 173 + k, 173);
            float tt = t - 0.08f;
            if (tt < 0.0f)
                continue;
            float life = DustLife * (0.7f + 0.6f * h3);
            float a = tt / life;
            if (a >= 1.0f)
                continue;
            float ang = (k + 0.6f * h1) / DustPuffs * Mathf.Tau;
            var way = new Vector3(Mathf.Cos(ang), 0.0f, Mathf.Sin(ang));
            // Behind the fire, as far as it got.
            float reach = DustReach * _hex * (0.25f + 0.75f * h2);
            float out_ = 0.25f * _hull + reach * (1.0f - Mathf.Exp(-tt / (WaveStop * 1.3f)));
            if (!Open(way, out_, out float ground))
                continue;
            Vector3 at = new Vector3(_foot.X, ground, _foot.Z) + way * out_
                         + Vector3.Up * (0.04f * _hull + 0.08f * _hull * a)
                         + Wind * (_hull * tt);
            // Big: spread over the neighbours, puffs a tank's size of this one
            // were stones on the ground.
            float r = _hull * Mathf.Lerp(0.10f, 0.28f * (0.7f + 0.6f * h3), 1.0f - Mathf.Pow(1.0f - a, 2.0f))
                      * Mathf.SmoothStep(0.0f, 0.05f, a);
            _dust.Add(at, r, DustTone * (0.9f + 0.2f * h1), h2, Mathf.SmoothStep(0.2f, 1.0f, a), a);
        }
    }

    /// <summary>
    /// The fire wave as one body: a roll of fire round <c>centre</c> on the
    /// blast's ground level, its front at <c>radius</c>, its section a lumpy
    /// half-disc of <c>thick</c>, trailing further behind the front than
    /// ahead of it and thrown up into tongues by a noise moving along it. A
    /// plain wall, seen from the board's steep eye, was a thin ribbon - a line
    /// round the tank, not a wave. Each pixel walks the eye's ray down
    /// through the layer the roll fills and stops where it enters it (then
    /// closes in on the edge), so the near side stands in front of the tank
    /// and the tank hides the far one. The field is 1 in the roll's middle and
    /// 0 at its skin, cut into the fire's bands, climbing as it <c>cool</c>s
    /// while a noise eats it through. A bearing whose wall (<c>stops</c>) is
    /// nearer than the front has no fire: the wave has hit it.
    /// </summary>
    private static readonly Shader WallShader = new()
    {
        Code = @"
shader_type spatial;
render_mode unshaded, cull_disabled, shadows_disabled, fog_disabled;
stencil_mode write, compare_always, 0;
uniform vec3 core : source_color = vec3(1.0, 0.98, 0.84);
uniform vec3 body : source_color = vec3(1.0, 0.78, 0.28);
uniform vec3 edge : source_color = vec3(1.0, 0.44, 0.10);
uniform vec3 rim : source_color = vec3(0.45, 0.10, 0.03);
uniform vec3 centre = vec3(0.0);
uniform float radius = 100.0;
uniform float thick = 30.0;
uniform float cool = 0.0;
uniform float time_s = 0.0;
uniform float seed = 0.0;
uniform float lump_px = 25.0;
uniform float ink_px = 2.0;
uniform float stops[" + Bearings + @"];
" + Toon.NoiseCode + @"
float stop_at(float ang) {
    float u = (ang / 6.2831853 + 0.5) * " + Bearings + @".0 - 0.5;
    float i0 = floor(u);
    int a0 = int(mod(i0, " + Bearings + @".0));
    int a1 = int(mod(i0 + 1.0, " + Bearings + @".0));
    return min(stops[a0], stops[a1]);
}
// The roll's field at a point: above 0 inside.
float roll(vec3 p) {
    vec3 o = p - centre;
    if (o.y < 0.0) return -1.0;
    float r = length(o.xz);
    if (radius > stop_at(atan(o.z, o.x))) return -1.0;
    vec3 w = vec3(o.x, 0.0, o.z) / lump_px;
    float n1 = noise3(w + vec3(0.0, time_s * 1.2, seed * 7.0));
    float n2 = noise3(w * 2.3 + vec3(seed * 3.0, time_s * 2.0, 0.0));
    // Taller where the noise throws a tongue up; longer behind the front.
    float up = 0.5 + 2.2 * n1 * n1 + 0.6 * (n2 - 0.5);
    float dr = r - radius;
    dr /= dr < 0.0 ? 1.8 : 0.8;
    float e = length(vec2(dr, o.y / max(up * (1.0 - 0.4 * cool), 0.1))) / thick;
    float f = 1.0 - e;
    float m = noise3(vec3(o.x, o.y * 1.5 - time_s * 60.0, o.z) / (lump_px * 0.6) + vec3(seed * 5.0));
    f -= cool * (0.25 + 0.6 * m) + cool * cool * 0.40;
    return f;
}
void fragment() {
    vec3 q = (INV_VIEW_MATRIX * vec4(VERTEX, 1.0)).xyz;
    vec3 d = normalize((INV_VIEW_MATRIX * vec4(0.0, 0.0, -1.0, 0.0)).xyz);
    if (d.y > -0.05) discard;
    // The layer the roll can fill: the ground up to its tallest tongue.
    float h = thick * 3.2;
    float s0 = (centre.y + h - q.y) / d.y;
    float s1 = (centre.y - q.y) / d.y;
    float s = s0;
    float ds = (s1 - s0) / 32.0;
    float f = -1.0;
    for (int i = 0; i <= 32; i++) {
        f = roll(q + d * s);
        if (f > 0.0) break;
        s += ds;
    }
    if (f <= 0.0) discard;
    // Closer in on the skin, so the edge is a line and not steps.
    float lo = s - ds, hi = s;
    for (int i = 0; i < 5; i++) {
        float mid = 0.5 * (lo + hi);
        if (roll(q + d * mid) > 0.0) hi = mid; else lo = mid;
    }
    vec3 p = q + d * hi;
    // Its colour from a little way in, where the band is decided.
    f = roll(q + d * (hi + thick * 0.35));
    vec4 clip = PROJECTION_MATRIX * (VIEW_MATRIX * vec4(p, 1.0));
    DEPTH = clip.z / clip.w * 0.5 + 0.5;
    vec3 col = rim;
    if (f > ink_px / thick) col = edge;
    if (f > 0.18 + 0.30 * cool) col = body;
    if (f > 0.40 + 0.60 * cool) col = core;
    ALBEDO = col;
}
",
    };
}
