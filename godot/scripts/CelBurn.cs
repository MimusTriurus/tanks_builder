using System.Collections.Generic;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// A burning 3D tank's fire and smoke, drawn in the model's own look - cel
/// tones and ink (<see cref="Toon"/>) - rather than on a card in front of it.
/// The sprites' <see cref="ProcFire"/> and column <see cref="ProcSmoke"/> are
/// left as they are for the 2D tanks; the 3D bench hides them and runs this.
///
/// <b>Why not the sprites' effects on the model.</b> They are a picture drawn
/// flat in front of the tank and cut to it by the height map: on a sprite that
/// is the only way, on a model it reads as a stain painted over the grille -
/// no tongue ever rises off the deck, the glow over green paint turns to a
/// yellow-green smear, the soft gradients sit beside hard cel steps and ink,
/// and nothing round the fire is lit by it. Here each part is in the world:
///
/// <list type="bullet">
/// <item><b>Tongues</b> - one quad facing the eye over the grilles, and in it
/// one field: every tongue of every <c>Exhaust.N</c> node is a teardrop,
/// the field is their union, and the bands - yellow core, orange, red, a dark
/// red rim - are cut on the union once. A quad a tongue had each tongue banded
/// on its own, and where they crossed one's rim lay over another's core: a row
/// of striped candles. The quad stands in the world at the grilles and writes
/// depth, so the deck in front hides the fire's foot; the turret hides a
/// grille's fire whole or not at all (<see cref="TurretAt"/>) - no map
/// decides it.</item>
/// <item><b>Puffs</b> - lumpy spheres rising off the fire, on the model's
/// ramp (<see cref="Toon.RampCode"/>): the sun lights their tops as it lights
/// the turret, and they are inked as the tank is. They are eaten away by a
/// hard cut, edges first, the way <see cref="ToonBlast"/>'s clusters go.</item>
/// <item><b>The fire's light</b> - an omni light over the ports, flickering:
/// the ramp steps it into a warm band on the deck, the turret and the smoke's
/// foot, which is what seats the fire on the tank.</item>
/// </list>
///
/// <b>Closed form, like <see cref="ToonBlast"/>.</b> Every tongue and puff is
/// arithmetic on its index and the clock - age is a fraction of its own loop -
/// so a capture repeats to the pixel and the column never has a first frame.
///
/// All lengths are shares of the hull's length on the board
/// (<see cref="Build"/>): the classes' sizes come with it.
/// </summary>
public sealed partial class CelBurn : Node3D
{
    // ------------------------------------------------------------ the fire

    /// <summary>Tongues on each port - the shader's loop, so a constant.</summary>
    public const int Tongues = 4;
    /// <summary>Ports the shader takes; more are dropped.</summary>
    public const int MaxPorts = 4;
    /// <summary>One tongue's life, s: born at the grille, it rises, narrows
    /// and is cut away. The body of each port has none - see the shader.</summary>
    public float TongueLife = 0.9f;
    /// <summary>A tongue's width and height at birth, hull lengths.</summary>
    public float TongueWidth = 0.23f, TongueHeight = 0.35f;
    /// <summary>How far a tongue rises over its life, hull lengths.</summary>
    public float TongueRise = 0.20f;
    /// <summary>How far round a port the tongues are born, hull lengths.</summary>
    public float PortSpread = 0.06f;

    /// <summary>The scorch round each port, hull lengths at full growth, and
    /// how long it takes to grow, s.</summary>
    public float ScorchReach = 0.14f, ScorchGrow = 1.6f;

    /// <summary>How long the fire takes to come up to what it is asked for,
    /// and to go down to it, s: a fire starts and goes out, it is not
    /// switched. The smoke follows the same way - see <see cref="Column"/>.</summary>
    public float FireRise = 1.2f, FireFall = 1.5f;
    public float SmokeRise = 1.0f, SmokeFall = 1.5f;

    /// <summary>The fire's light: colour, energy at full fire, reach in hull
    /// lengths.</summary>
    public Color GlowColor = new(1.0f, 0.52f, 0.22f);
    public float GlowEnergy = 0.7f, GlowReach = 0.75f;

    // ------------------------------------------------------------ the smoke

    /// <summary>Puffs in the column at once.</summary>
    public int Puffs = 18;
    /// <summary>One puff's life, s, from the fire to gone.</summary>
    public float PuffLife = 3.2f;
    /// <summary>A puff's width at birth and at the end, hull lengths.</summary>
    public float PuffBorn = 0.15f, PuffGrown = 0.60f;
    /// <summary>How high the column's puffs get, hull lengths.</summary>
    public float ColumnHeight = 1.35f;
    /// <summary>Where the wind takes the column's top, hull lengths per
    /// column height, in the world: to the screen's right and a little away.</summary>
    public Vector3 Drift = new(0.45f, 0.0f, -0.20f);
    /// <summary>Where a puff is born over the ports, hull lengths: at the
    /// fire's top, out of it - born at the grille, a dark puff sat inside the
    /// flame like a hole in it.</summary>
    public float SmokeSeat = 0.30f;
    /// <summary>When a puff starts to be eaten, as a share of its life.</summary>
    public float ErodeFrom = 0.35f;
    /// <summary>The smoke's grey: burning, and smouldering after it.</summary>
    public float SootTone = 0.26f, SmoulderTone = 0.50f;

    // ------------------------------------------------------------ the inputs

    /// <summary>How much fire is asked for, 0..1 - the wreck's blaze. What
    /// burns is <see cref="Heat"/>, which follows it.</summary>
    public float Fire;
    /// <summary>How much smoke is asked for, 0..1.</summary>
    public float Smoke;

    /// <summary>The fire as it burns: <see cref="Fire"/>, come up to over
    /// <see cref="FireRise"/> and gone down from over <see cref="FireFall"/>.</summary>
    public float Heat => _heat;
    /// <summary>Light smoke of a smouldering wreck, not the burning column.</summary>
    public bool Smoulder;
    /// <summary>
    /// The turret against the fire, <b>by grille, not by pixel</b>: a port
    /// nearer the eye than the turret's ring (<see cref="TurretAt"/>) has its
    /// tongues drawn over the turret whole, one farther has them hidden by it
    /// whole. By depth, a tongue's surface went through the turret's wall
    /// wherever the two stood at one depth - from the side the grilles do -
    /// and the turret cut the flame with a straight edge. It is the stencil
    /// the turret's visible pixels carry (<see cref="Toon.TurretStencil"/>):
    /// the flame's first pass is never drawn on it, the second only there,
    /// with no depth test and only for the ports in front.
    ///
    /// A thrown turret lies on the deck over the grilles (<see cref="OverTurret"/>):
    /// every port is in front of it, as the board draws the sprites' thrown
    /// turret under the fire. <b>Not by depth</b> either: pulled toward the eye
    /// far enough to clear it, the flame's foot came out over the stern down
    /// to the ground.
    /// </summary>
    public Vector3? TurretAt;
    public bool OverTurret;
    /// <summary>The model's cel materials: the scorch is painted into them.</summary>
    public IReadOnlyList<ShaderMaterial> Paint = System.Array.Empty<ShaderMaterial>();

    // ------------------------------------------------------------ the machinery

    private float _hull = 150.0f;
    private float _clock;
    /// <summary>The smoke's grey before the last change of it and when that
    /// was: a puff keeps the grey of the moment it was born, so the black of a
    /// knock-out's flare climbs away above the pale smoulder under it, rather
    /// than the whole column turning pale in one frame.</summary>
    private float _toneBefore = -1.0f, _toneNow = -1.0f, _toneAt;
    /// <summary>How far the scorch has grown, 0..1. It grows while the fire
    /// burns and goes with it: as <see cref="Heat"/> falls it shrinks by the
    /// same share, and is gone when the fire is. Left on a tank the fire had
    /// left, it read as a stain.</summary>
    private float _scorch;
    private float _scorchClock;
    private float _heat, _smoke;
    /// <summary>
    /// When the column's births began and stopped, on its clock: a puff is
    /// there only if it was born while the smoke was on. The column is closed
    /// form, so every age is there at once - drawn as it is, it stood full
    /// height from the first frame and vanished whole in the last. With
    /// these, it climbs off the fire as the fire starts, and when it goes out
    /// the last puffs are born and rise away. <c>_gap</c> is the last time it
    /// was off, for smoke that stops and starts again inside one column.
    /// </summary>
    private float _smokeFrom, _smokeTo = -1.0f, _gapFrom = 1.0f, _gapTo = 0.0f;
    private bool _births;
    private readonly Vector3[] _scorchAt = new Vector3[MaxPorts];
    private MultiMesh? _puffs;
    private MeshInstance3D? _flame;
    private ShaderMaterial? _flameInk;
    private MultiMeshInstance3D? _puffCloud;
    private OmniLight3D? _glow;
    private readonly List<(float Depth, Transform3D Where, Color Mine)> _order = new();

    public void Build(float hullPx)
    {
        _hull = Mathf.Max(hullPx, 1.0f);
        _flameInk = new ShaderMaterial
        {
            Shader = FlameShader,
            NextPass = new ShaderMaterial { Shader = FlameOverShader },
        };
        _flame = new MeshInstance3D
        {
            Name = "Tongues",
            Mesh = new QuadMesh { Size = Vector2.One, CenterOffset = new Vector3(0.0f, 0.5f, 0.0f) },
            MaterialOverride = _flameInk,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(_flame);

        (_puffs, _puffCloud, _) = CelPuff.Cloud("Puffs", Puffs);
        AddChild(_puffCloud);

        _glow = new OmniLight3D
        {
            Name = "Glow", LightColor = GlowColor, OmniRange = GlowReach * _hull,
            // No falloff but the range's window: the world is in board px, and
            // the physical 1/d had the light a thirtieth of itself 30 px off.
            // Unshadowed: under gl_compatibility an omni light's shadow washed
            // the whole unlit board out pale. Its range keeps it off the belts.
            OmniAttenuation = 0.0f, ShadowEnabled = false, LightEnergy = 0.0f,
        };
        AddChild(_glow);
    }

    public void Reset()
    {
        Rest();
        _scorch = 0.0f;
        Scorch(System.Array.Empty<Vector3>(), 0.0f);
    }

    /// <summary>The fire and the smoke gone out: their clocks back to nought.
    /// The scorch is gone with the fire by then.</summary>
    private void Rest()
    {
        _clock = 0.0f;
        _toneBefore = _toneNow = -1.0f;
        _heat = _smoke = 0.0f;
        _births = false;
        _smokeFrom = 0.0f;
        _smokeTo = -1.0f;
        _gapFrom = 1.0f;
        _gapTo = 0.0f;
    }

    /// <summary>Whether a puff born at <paramref name="born"/> is there: born
    /// while the smoke was on.</summary>
    private bool Lived(float born) =>
        born >= _smokeFrom && born <= _smokeTo && !(born > _gapFrom && born < _gapTo);

    private static float Follow(float now, float to, float dt, float rise, float fall) =>
        to > now ? Mathf.Min(to, now + dt / Mathf.Max(rise, 1e-3f))
                 : Mathf.Max(to, now - dt / Mathf.Max(fall, 1e-3f));

    /// <summary>
    /// A frame: the clock on by <paramref name="dt"/>, every tongue and puff
    /// put where it is. <paramref name="ports"/> are the grilles in the world,
    /// <paramref name="eye"/> the camera's basis (the tongues face it).
    /// </summary>
    public void Tick(float dt, IReadOnlyList<Vector3> ports, Basis eye)
    {
        if (_flame is null || _puffs is null || _glow is null)
            return;
        float fire = ports.Count > 0 ? Mathf.Clamp(Fire, 0.0f, 1.0f) : 0.0f;
        float smoke = ports.Count > 0 ? Mathf.Clamp(Smoke, 0.0f, 1.0f) : 0.0f;
        float heatBefore = _heat;
        bool marked = _scorch > 0.0f;
        _heat = Follow(_heat, fire, dt, FireRise, FireFall);
        _smoke = Follow(_smoke, smoke, dt, SmokeRise, SmokeFall);

        // The column's births: on while the smoke that follows is up, so the
        // last puffs are born small as the fire dies rather than cut off the
        // moment it is told to.
        bool asked = smoke > 0.001f || _smoke > 0.03f;
        if (asked && !_births)
        {
            if (_smokeTo >= 0.0f)
            {
                // Again, inside a column still rising: skip the time it was off.
                _gapFrom = _smokeTo;
                _gapTo = _clock;
            }
            else
                _smokeFrom = _clock;
            _smokeTo = float.MaxValue;
            _births = true;
        }
        else if (!asked && _births)
        {
            _smokeTo = _clock;
            _births = false;
        }
        bool rising = _births || (_smokeTo >= 0.0f && _clock - _smokeTo < PuffLife);
        bool on = _heat > 0.001f || rising;

        _flame.Visible = on && _heat > 0.001f;
        _puffCloud!.Visible = on;
        _glow.Visible = on && _heat > 0.001f;
        if (_heat < heatBefore)
            _scorch *= _heat / heatBefore;
        else
            _scorch = Mathf.Min(1.0f, _scorch + _heat * dt / Mathf.Max(ScorchGrow, 1e-3f));
        if (_heat <= 0.0f)
            _scorch = 0.0f;
        if (marked || _scorch > 0.0f)
        {
            _scorchClock += dt;
            Scorch(ports, _heat);
        }
        if (!on || ports.Count == 0)
        {
            Rest();
            return;
        }
        _clock += dt;
        Vector3 mid = Vector3.Zero;
        foreach (Vector3 p in ports)
            mid += p;
        mid /= ports.Count;
        Tongue(ports, eye);
        Column(mid, eye);

        // The flicker: two slow sines and a quick one, never dark.
        float flick = 0.78f + 0.12f * Mathf.Sin(_clock * 7.3f) + 0.07f * Mathf.Sin(_clock * 13.1f + 1.7f)
                      + 0.05f * Mathf.Sin(_clock * 23.0f + 0.4f);
        _glow.GlobalPosition = mid + Vector3.Up * (0.16f * _hull);
        _glow.OmniRange = GlowReach * _hull;
        _glow.LightEnergy = GlowEnergy * _heat * flick;
    }

    /// <summary>The scorch into every cel material: the ports in the world,
    /// its reach, how far it has grown, the embers' glow and their clock.</summary>
    private void Scorch(IReadOnlyList<Vector3> ports, float ember)
    {
        int n = Mathf.Min(ports.Count, MaxPorts);
        for (int i = 0; i < MaxPorts; i++)
            _scorchAt[i] = i < n ? ports[i] : Vector3.Zero;
        foreach (ShaderMaterial m in Paint)
        {
            m.SetShaderParameter("scorch_at", _scorchAt);
            m.SetShaderParameter("scorch_n", n);
            m.SetShaderParameter("scorch_r", ScorchReach * _hull);
            m.SetShaderParameter("scorch", _scorch);
            m.SetShaderParameter("ember", ember);
            m.SetShaderParameter("scorch_time", _scorchClock);
        }
    }

    private readonly Vector2[] _portAt = new Vector2[MaxPorts];
    private readonly float[] _portNear = new float[MaxPorts];
    private readonly float[] _front = new float[MaxPorts];

    /// <summary>
    /// The flame's quad: over the ports, facing the eye, its foot a little
    /// under them so the deck hides it; the ports handed to the shader in hull
    /// lengths from the quad's bottom middle, and the clock.
    /// </summary>
    private void Tongue(IReadOnlyList<Vector3> ports, Basis eye)
    {
        Vector3 right = eye.X.Normalized(), up = eye.Y.Normalized();
        Vector3 mid = Vector3.Zero;
        int n = Mathf.Min(ports.Count, MaxPorts);
        for (int i = 0; i < n; i++)
            mid += ports[i];
        mid /= n;
        // Under the ports: a tongue's round foot reaches 0.4 of its height
        // below its base, and its height is up to 1.69 of TongueHeight.
        float foot = 0.4f * 1.69f * TongueHeight;
        Vector3 origin = mid - up * (foot * _hull);  // as the screen has it
        float wide = 0.0f;
        for (int i = 0; i < MaxPorts; i++)
        {
            if (i >= n)
            {
                _portAt[i] = Vector2.Zero;
                continue;
            }
            Vector3 rel = ports[i] - origin;
            _portAt[i] = new Vector2(rel.Dot(right), rel.Dot(up)) / _hull;
            wide = Mathf.Max(wide, Mathf.Abs(_portAt[i].X));
        }
        // Room for the widest tongue either side - the body at 1.3, its drop
        // 0.96 wide, and the lick bending its tip another 0.4 - and the
        // tallest over the rise. A quad any tighter cut the fire off with its
        // own straight edge where a view spread the grilles across the screen.
        float w = 2.0f * (wide + PortSpread + TongueWidth * 1.3f * (0.96f + 0.4f));
        float h = foot + TongueRise + TongueHeight * 1.69f;
        // Upright in the world, not in the screen's plane: the screen's plane
        // leans away from the eye, and a flame in it went back into the turret
        // as it rose, which hid it. Stretched by 1/cos so it spans the same
        // screen height; what the shader sees is screen lengths either way.
        float cos = Mathf.Max(Vector3.Up.Dot(up), 0.2f);
        Vector3 facing = right.Cross(Vector3.Up).Normalized();
        Vector3 seat = mid - Vector3.Up * (foot * _hull / cos);
        _flame!.GlobalTransform = new Transform3D(
            new Basis(right * (w * _hull), Vector3.Up * (h * _hull / cos), -facing), seat);
        // How far each port stands in front of the quad along the eye's ray,
        // px: the quad is one plane, the grilles are not at one depth, and the
        // shader puts each tongue's depth back on its own port.
        Vector3 back = eye.Z.Normalized();
        float across = Mathf.Abs(back.Dot(facing)) > 1e-3f ? back.Dot(facing) : 1e-3f;
        for (int i = 0; i < MaxPorts; i++)
            _portNear[i] = i < n ? (ports[i] - seat).Dot(facing) / across : 0.0f;
        for (int i = 0; i < MaxPorts; i++)
            _front[i] = i < n && (OverTurret || (TurretAt is Vector3 t && (ports[i] - t).Dot(back) > 0.0f))
                ? 1.0f : 0.0f;
        // Both passes the same numbers: the second is the flame again, over
        // the turret, for the ports in front of it.
        var over = (ShaderMaterial)_flameInk!.NextPass;
        foreach (ShaderMaterial m in new[] { _flameInk!, over })
        {
            m.SetShaderParameter("size", new Vector2(w, h));
            m.SetShaderParameter("ports", _portAt);
            m.SetShaderParameter("count", n);
            m.SetShaderParameter("time", _clock);
            m.SetShaderParameter("heat", _heat);
            m.SetShaderParameter("life", TongueLife);
            m.SetShaderParameter("tongue", new Vector2(TongueWidth, TongueHeight));
            m.SetShaderParameter("rise", TongueRise);
            m.SetShaderParameter("spread", PortSpread);
            m.SetShaderParameter("port_near", _portNear);
            m.SetShaderParameter("hull", _hull);
            m.SetShaderParameter("front", _front);
        }
    }

    private void Column(Vector3 seat, Basis eye)
    {
        if (_puffs!.InstanceCount != Puffs)
            _puffs.InstanceCount = Puffs;
        float smoke = _smoke;
        float tone = Smoulder ? SmoulderTone : SootTone;
        if (_toneNow < 0.0f)
            _toneBefore = _toneNow = tone;
        else if (!Mathf.IsEqualApprox(tone, _toneNow))
        {
            _toneBefore = _toneNow;
            _toneNow = tone;
            _toneAt = _clock;
        }
        float height = ColumnHeight * _hull * (Smoulder ? 0.75f : 1.0f);
        float thin = Smoulder ? 0.7f : 1.0f;
        Vector3 back = eye.Z.Normalized();
        // Out of the fire's top while it burns, off the grille when it does not.
        float seatLift = Mathf.Lerp(0.05f, SmokeSeat, _heat) * _hull;
        _order.Clear();
        for (int k = 0; k < Puffs; k++)
        {
            float loop = _clock / PuffLife + (k + 0.6f * CelPuff.Hash(k, 17)) / Puffs;
            float a = loop - Mathf.Floor(loop);
            int lap = (int)Mathf.Floor(loop);
            float h1 = CelPuff.Hash(k * 97 + lap, 19), h2 = CelPuff.Hash(k * 97 + lap, 23), h3 = CelPuff.Hash(k * 97 + lap, 29);
            // Up fast off the fire and slowing, bent by the wind as it climbs.
            float rise = 1.0f - Mathf.Pow(1.0f - a, 2.2f);
            float bend = Mathf.Pow(a, 1.4f);
            float wob = 0.20f * _hull * Mathf.Sqrt(a);
            Vector3 at = seat
                         + Vector3.Up * (seatLift + height * rise)
                         + Drift * (height * bend)
                         + new Vector3(Mathf.Cos(h1 * Mathf.Tau), 0.0f, Mathf.Sin(h1 * Mathf.Tau)) * wob;
            float pop = Mathf.SmoothStep(0.0f, 0.07f, a);
            // Less smoke: smaller puffs, fewer of them, eaten sooner - but every
            // one still born at the fire. The smoke here is the one that
            // follows (_smoke), so a column thickens as the fire comes up. Eaten from birth, the thin smoke of a
            // smouldering wreck lost its foot and hung in the air apart.
            float d = _hull * Mathf.Lerp(PuffBorn, PuffGrown, a) * (0.75f + 0.5f * h2) * pop * thin
                      * (0.55f + 0.45f * smoke);
            float erode = Mathf.SmoothStep(ErodeFrom * (0.55f + 0.45f * smoke), 1.0f, a);
            float born = _clock - a * PuffLife;
            if (h3 > 0.35f + 0.65f * smoke || !Lived(born))
                d = 0.0f;
            var basis = Basis.Identity.Scaled(Vector3.One * Mathf.Max(d, 1e-4f));
            float mine = born < _toneAt ? _toneBefore : _toneNow;
            _order.Add((at.Dot(back), new Transform3D(basis, at), new Color(h2, a, mine, Mathf.Min(erode, 1.0f))));
        }
        CelPuff.Write(_puffs, _order);
    }

    // ------------------------------------------------------------ the shaders


    /// <summary>
    /// A tongue: a teardrop - round foot, pointed tip - bent sideways by a
    /// noise that climbs it, and cut into bands on the same field: yellow core,
    /// orange, red, a dark red rim, nothing. As the tongue ages the field sinks,
    /// so it narrows and breaks at the tip before it goes.
    /// </summary>
    private static readonly Shader FlameShader = new()
    {
        Code = @"
shader_type spatial;
render_mode unshaded, cull_disabled, shadows_disabled, fog_disabled;
stencil_mode read, compare_not_equal, " + Toon.TurretStencil + @";
uniform vec3 core : source_color = vec3(1.0, 0.93, 0.55);
uniform vec3 body : source_color = vec3(1.0, 0.62, 0.16);
uniform vec3 edge : source_color = vec3(0.90, 0.24, 0.06);
uniform vec3 rim : source_color = vec3(0.32, 0.06, 0.03);
uniform vec2 size = vec2(1.0);
uniform vec2 ports[4];
uniform int count = 1;
uniform float time = 0.0;
uniform float heat = 1.0;
uniform float life = 0.6;
uniform vec2 tongue = vec2(0.24, 0.4);
uniform float rise = 0.2;
uniform float spread = 0.06;
uniform float port_near[4];
uniform float hull = 150.0;
uniform float front[4];
uniform bool only_front = false;
" + Toon.NoiseCode + @"
float hash1(float x) { return fract(sin(x * 12.9898 + 4.1414) * 43758.5453); }
// 1 on a tongue's middle line, 0 at its edge, below 0 outside: a round foot,
// full sides, a soft point.
float drop(vec2 q) {
    if (q.y < -0.4 || q.y > 1.0) return -1.0;
    float half_w = 0.96 * pow(max(1.0 - q.y, 0.0), 0.55) * smoothstep(-0.45, 0.25, q.y);
    return 1.0 - abs(q.x) / max(half_w, 1e-3);
}
void fragment() {
    vec2 p = vec2((UV.x - 0.5) * size.x, (1.0 - UV.y) * size.y);
    float f = -1.0;
    // The fire's depth: the port of the tongue that wins the field, and the
    // nearest of the ports whose tongues cover the pixel - see below.
    float near = 0.0;
    float cover = -1e9;
    for (int i = 0; i < 4; i++) {
        if (i >= count) break;
        if (only_front && front[i] < 0.5) continue;
        for (int j = 0; j < " + Tongues + @"; j++) {
            float k = float(i * 8 + j);
            float a, h1, h2, s;
            if (j == 0) {
                // The body of the port: bigger, always there, and with no life
                // of its own - it breathes and sways on slow noises. Reborn
                // every lap as the others are, it changed shape in one frame
                // and the whole fire jumped with it.
                float br = noise3(vec3(time * 0.9, k, 5.0));
                a = 0.22;
                h1 = 0.20 + 0.40 * br;
                h2 = noise3(vec3(time * 0.6, k, 9.0));
                s = 1.1 * (0.85 + 0.20 * br);
            } else {
                float loop = time / life + (float(j) + hash1(k * 7.1) * 0.5) / " + Tongues + @".0;
                a = fract(loop);
                float lap = floor(loop);
                h1 = hash1(k * 13.3 + lap * 1.7);
                h2 = hash1(k * 5.9 + lap * 3.1);
                // The others go out as the fire falls.
                if (h2 > 0.25 + 0.75 * heat) continue;
                // In from nothing and out to nothing: the lap that follows is
                // another tongue, and seen at any size the change was a jump.
                s = (0.5 + 0.45 * h1) * smoothstep(0.0, 0.3, a) * (1.0 - smoothstep(0.5, 1.0, a));
            }
            // From nothing: a fire starting is one small tongue, and the rest
            // join it as it comes up.
            s *= pow(heat, 0.6);
            if (s < 1e-3) continue;
            vec2 base = ports[i] + vec2((h2 - 0.5) * 2.0 * spread * (j == 0 ? 0.3 : 1.0), rise * a * a);
            vec2 q = (p - base) / (tongue * vec2(s, s * (0.8 + 0.5 * h1)));
            // It licks: bent sideways, more the higher up.
            q.x -= (noise3(vec3(q.y * 1.6 - time * 1.3, k, 0.0)) - 0.5) * 0.8 * q.y * q.y;
            float d = drop(q) - a * 0.3;
            if (d > f)
                near = port_near[i];
            if (d > 0.0)
                cover = max(cover, port_near[i]);
            // A smooth union: where two tongues meet the field bridges them,
            // so one sliding over another does not notch the outline.
            float m = clamp(0.5 + 0.5 * (d - f) / 0.06, 0.0, 1.0);
            f = mix(f, d, m) + 0.06 * m * (1.0 - m);
        }
    }
    // A tongue is a body, not a sheet: as near the eye as its own port, and
    // nearer by up to half its width along its middle. As a plane at the
    // ports' middle depth it was cut by whatever stood within that - the
    // turret beside a grille took the tongue off it with a straight edge.
    // The nearest port that covers the pixel, not the winner's: where the far
    // grille's tongue outgrew the near one's inside it, the deck in front of
    // the near grille came through the flame in dark slits. The body's half
    // width, whichever tongue it is: the winner's own jumped where a small
    // tongue took over from the body.
    near = max(near, cover);
    float half_px = 0.5 * tongue.x * 1.1 * pow(heat, 0.6) * hull;
    float thick = half_px * sqrt(clamp(f, 0.0, 1.0));
    vec4 clip = PROJECTION_MATRIX * vec4(VERTEX.xy, VERTEX.z + near + thick, 1.0);
    DEPTH = clip.z / clip.w * 0.5 + 0.5;
    // One noise climbing through the whole fire, biting the top first.
    float n = noise3(vec3(p * vec2(7.0, 5.0) - vec2(0.0, time * 2.8), 3.0));
    f -= (n - 0.4) * 0.35 * clamp(p.y / tongue.y, 0.0, 1.5);
    if (f < 0.0) discard;
    vec3 c = rim;
    if (f > 0.10) c = edge;
    if (f > 0.30) c = body;
    if (f > 0.55) c = core;
    ALBEDO = c;
    // Drawn after everything opaque, for the turret's stencil to be there:
    // a transparent pass for the order alone.
    ALPHA = 1.0;
}
",
    };

    /// <summary>
    /// The flame again, over the turret only: no depth test, drawn where the
    /// turret's stencil is, the ports in front of it only (<c>front</c>) -
    /// see <see cref="TurretAt"/>.
    /// </summary>
    private static readonly Shader FlameOverShader = new()
    {
        Code = FlameShader.Code
            .Replace("render_mode unshaded, cull_disabled, shadows_disabled, fog_disabled;",
                     "render_mode unshaded, cull_disabled, shadows_disabled, fog_disabled, depth_test_disabled;")
            .Replace("stencil_mode read, compare_not_equal, ", "stencil_mode read, compare_equal, ")
            .Replace("uniform bool only_front = false;", "uniform bool only_front = true;"),
    };
}
