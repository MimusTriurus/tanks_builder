using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The blast wave of a destroyed tank crossing on to its neighbours: a ring of
/// pressure with a skirt of dust, and a front of flame behind it, both lying on
/// the ground and both stopping at the edge of the six cells the rules say the
/// explosion reaches; standing on that front, tongues of flame and a wall of
/// smoke, with sparks thrown up off the tongues.
///
/// <b>The picture of a rule, and the rule is hex-shaped.</b> GDD states.md: the
/// explosion strikes the six neighbouring hexes <em>of its own level</em>, and
/// a neighbour a level up or down is untouched. A circle cannot say that - it
/// would wash over the hill beside the wreck exactly as over the plain - so the
/// wave is drawn on a quad over the flower of seven cells and cut by a mask of
/// six flags, one per neighbour (<see cref="Reach"/>). Where a flag is down the
/// wave ends on that cell's edge, which is the level rule seen: fire comes to
/// the bank of the ford and stops.
///
/// <b>What it replaced, and why.</b> The first cut of the destroyed event
/// dropped six small ground bursts on the neighbours - shells' pictures - and
/// they read as six shots, not as one explosion reaching out. What ignites a
/// tank next door is the wave itself, so the wave is what is drawn, and the
/// consequences on each cell are timed to its front (<see cref="ArrivesAt"/>).
///
/// <b>Two planes on the ground, two curtains standing on it.</b> The planes, as
/// <see cref="ProcRack"/> has two slabs: dust darkens and must mix, fire is
/// light the ground catches and must add. They sort <em>under</em> the tanks
/// (<see cref="Stage3D.RingOrder"/>): a glow on the ground is not seen through
/// a hull standing on it. The curtains are rings of cylinder wall the vertex
/// shader expands with the front - tongues of flame on the front's radius, in
/// two shells for depth, and a wall of smoke behind it. <b>Each curtain is
/// twelve arcs, each its own node seated on its own arc's midpoint</b>, because
/// the stage sorts what stands by its origin: one ring would be wholly in front
/// of every tank or wholly behind, and twelve arcs are twelve answers, each
/// right for the tank nearest it.
///
/// <b>Built from the shared texts.</b> <see cref="Stage3D.FlameInk"/> for what a
/// lick is - <c>flame_blob</c>, the same body of revolution with the same dark
/// lip and bright shell the tank's and the tree's flames are made of, so the
/// tongues here have the volume those have and burn the same hue; its
/// <c>flame_spark</c> for the sparks, as <see cref="ProcSpall"/> throws them;
/// <see cref="Stage3D.EmberNoiseCode"/> for the tear. Every length is in hex
/// circumradii (the tile's half-width), so one wave fits every board.
/// </summary>
public sealed partial class ProcWave : Node3D
{
    /// <summary>The far edge of the neighbours, in circumradii: their centres
    /// stand at √3 and their far vertices one radius beyond.</summary>
    public const float ReachDefault = 2.75f;

    /// <summary>The plane's half-size in circumradii - past the reach, so the
    /// outer half of the band is never cut by the plane's own edge.</summary>
    public const float Half = 3.0f;

    /// <summary>Seconds for the shock to reach the far edge, and how far the
    /// flame runs behind it. The rack's fireball is at its widest at a quarter
    /// second; a wave that beat it read as a second explosion.</summary>
    public const float ShockAtDefault = 0.45f;
    public const float FlameLagDefault = 0.10f;

    /// <summary>The whole event, after which the plane goes dark.</summary>
    public const float LifeDefault = 1.60f;

    /// <summary>Where a front stands at its own age <paramref name="a"/> (0 at
    /// its start, 1 at the far edge), in circumradii: fast off the hull and
    /// slowing - the shaders' <c>run</c>, here so the arcs can be seated and
    /// the queue can time what the front does when it gets there.</summary>
    public static float Run(float a, float reach = ReachDefault)
    {
        float k = Mathf.Clamp(a, 0.0f, 1.0f);
        return reach * (1.0f - (1.0f - k) * (1.0f - k));
    }

    /// <summary>Where the shock stands at <paramref name="t"/> seconds.</summary>
    public static float ShockAt(float t, float reach = ReachDefault,
                                float shockAt = ShockAtDefault) =>
        Run(t / Mathf.Max(shockAt, 1e-4f), reach);

    /// <summary>Seconds until the flame front reaches a point
    /// <paramref name="radii"/> out - the inverse of <see cref="Run"/> plus the
    /// flame's lag. The neighbours' centres are √3 out, so
    /// <c>ArrivesAt(√3)</c> is when a tank next door catches.</summary>
    public static float ArrivesAt(float radii, float reach = ReachDefault,
                                  float shockAt = ShockAtDefault,
                                  float lag = FlameLagDefault)
    {
        float s = Mathf.Clamp(radii / Mathf.Max(reach, 1e-4f), 0.0f, 1.0f);
        return shockAt * (1.0f - Mathf.Sqrt(1.0f - s)) + lag;
    }

    /// <summary>√3: where a neighbour's centre stands, in circumradii.</summary>
    public const float NeighbourAt = 1.7320508f;

    /// <summary>How many arcs each curtain is cut into - see the class note -
    /// and how many facets each arc has round.</summary>
    public const int Arcs = 12;
    private const int FacetsPerArc = 8;

    /// <summary>The two shells of tongues: the outer on the front's radius, the
    /// inner a little behind it and dimmer, which is what puts one tongue in
    /// front of another and reads as depth.</summary>
    public const int LickShells = 2;
    public const float InnerShell = 0.86f;

    public float Life = LifeDefault;

    private MeshInstance3D? _dust;
    private MeshInstance3D? _fire;
    private ShaderMaterial? _dustInk;
    private ShaderMaterial? _fireInk;

    private enum Kind { Lick, Smoke }

    /// <summary>One arc of one curtain: its node, its own material (the arc's
    /// midpoint angle and shell are uniforms of it), and what it is.</summary>
    private sealed record Wall(MeshInstance3D Node, ShaderMaterial Ink, float Mid,
                               Kind Kind, int Shell);

    private readonly List<Wall> _walls = new();
    private Transform3D _seat = Transform3D.Identity;
    private Vector3 _nudge;
    private float _radius = 124.0f;
    private float _clock = -1.0f;

    /// <summary>The materials, for anything written to all of them.</summary>
    private IEnumerable<ShaderMaterial> Inks()
    {
        if (_dustInk is not null) yield return _dustInk;
        if (_fireInk is not null) yield return _fireInk;
        foreach (Wall wall in _walls)
            yield return wall.Ink;
    }

    public bool Alive => _clock >= 0.0f;
    public float Age => _clock;

    /// <summary>Hold the clock, for a bench scrubbing through the event.</summary>
    public bool Hold;

    public float Clock
    {
        get => _clock;
        set => _clock = value;
    }

    /// <summary>Build the planes and the arcs. <paramref name="tile"/> is the
    /// hex's own width in screen px - the circumradius is half of it - and the
    /// two camera terms are the field's, handed in so this needs no board.</summary>
    public void Build(float tile, float squash, float rise)
    {
        _radius = Mathf.Max(tile, 1.0f) * 0.5f;
        float size = 2.0f * Half * _radius;
        // On the ground, under what stands - RingOrder's own argument: a light
        // on the ground is not seen through a hull, and drawn over the tanks the
        // wave would be right for the ground and wrong for every tank on it.
        _dustInk = Ink(Dusting, Stage3D.RingOrder);
        _fireInk = Ink(Blazing, Stage3D.RingOrder);
        _dust = Plane(size, _dustInk);
        _fire = Plane(size, _fireInk);

        for (int i = 0; i < Arcs; i++)
        {
            float from = i * Mathf.Tau / Arcs;
            float to = (i + 1) * Mathf.Tau / Arcs;
            float mid = (from + to) * 0.5f;
            ArrayMesh arc = Curtain(from, to);
            for (int shell = 0; shell < LickShells; shell++)
            {
                ShaderMaterial ink = Ink(Licking, Stage3D.StandOrder);
                ink.SetShaderParameter("arc_mid", mid);
                ink.SetShaderParameter("shell", (float)shell);
                ink.SetShaderParameter("ring_scale", shell == 0 ? 1.0f : InnerShell);
                _walls.Add(new Wall(Slab(arc, ink), ink, mid, Kind.Lick, shell));
            }
            ShaderMaterial smoke = Ink(Smoking, Stage3D.StandOrder);
            smoke.SetShaderParameter("arc_mid", mid);
            _walls.Add(new Wall(Slab(arc, smoke), smoke, mid, Kind.Smoke, 0));
        }
        _nudge = Stage3D.Clear(squash, rise);
        Stand();
    }

    private ShaderMaterial Ink(Shader how, int order)
    {
        var ink = new ShaderMaterial { Shader = how, RenderPriority = order };
        ink.SetShaderParameter("time", 0.0f);
        ink.SetShaderParameter("level", 1.0f);
        ink.SetShaderParameter("half", Half);
        ink.SetShaderParameter("radius_px", _radius);
        ink.SetShaderParameter("mask_a", Vector3.One);
        ink.SetShaderParameter("mask_b", Vector3.One);
        ink.SetShaderParameter("sun", Stage3D.Sun);
        return ink;
    }

    private MeshInstance3D Plane(float size, ShaderMaterial ink)
    {
        var node = new MeshInstance3D
        {
            Mesh = new PlaneMesh { Size = new Vector2(size, size) },
            SortingUseAabbCenter = false,
            MaterialOverride = ink,
            Visible = false,
        };
        AddChild(node);
        return node;
    }

    /// <summary>
    /// One arc of a unit cylinder wall - radius one, height one, open top and
    /// bottom - from <paramref name="from"/> to <paramref name="to"/> radians,
    /// with <c>u</c> the whole ring's fraction (so noise walked by <c>u</c> is
    /// continuous across arcs) and <c>v</c> up it, normals outward. The vertex
    /// shader scales it to where the front stands and pulls it back by the
    /// arc's midpoint, which is where the node is seated.
    /// </summary>
    private static ArrayMesh Curtain(float from, float to)
    {
        var verts = new List<Vector3>();
        var norms = new List<Vector3>();
        var uvs = new List<Vector2>();
        var idx = new List<int>();
        for (int i = 0; i <= FacetsPerArc; i++)
        {
            float ang = Mathf.Lerp(from, to, (float)i / FacetsPerArc);
            var n = new Vector3(Mathf.Cos(ang), 0.0f, Mathf.Sin(ang));
            verts.Add(new Vector3(n.X, 0.0f, n.Z));
            verts.Add(new Vector3(n.X, 1.0f, n.Z));
            norms.Add(n);
            norms.Add(n);
            float u = ang / Mathf.Tau;
            uvs.Add(new Vector2(u, 0.0f));
            uvs.Add(new Vector2(u, 1.0f));
        }
        for (int i = 0; i < FacetsPerArc; i++)
        {
            int a = i * 2;
            idx.Add(a); idx.Add(a + 1); idx.Add(a + 2);
            idx.Add(a + 1); idx.Add(a + 3); idx.Add(a + 2);
        }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts.ToArray();
        arrays[(int)Mesh.ArrayType.Normal] = norms.ToArray();
        arrays[(int)Mesh.ArrayType.TexUV] = uvs.ToArray();
        arrays[(int)Mesh.ArrayType.Index] = idx.ToArray();
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    private MeshInstance3D Slab(ArrayMesh arc, ShaderMaterial ink)
    {
        var node = new MeshInstance3D
        {
            Mesh = arc,
            // Sorted by the origin, which Place puts on the arc's midpoint at
            // the front's radius - the same key the tanks sort by.
            SortingUseAabbCenter = false,
            MaterialOverride = ink,
            Visible = false,
            // The mesh is a unit arc the vertex shader blows up to three radii
            // and pulls about; the culler has to be told, or it drops the arc
            // when the unit arc leaves the screen.
            CustomAabb = new Aabb(new Vector3(-1000.0f, -10.0f, -1000.0f),
                                  new Vector3(2000.0f, 800.0f, 2000.0f)),
        };
        AddChild(node);
        return node;
    }

    /// <summary>Seat it on a point of the board - the rack's <c>Sit</c>.</summary>
    public void Sit(Vector2 ground, float lift, float squash, float rise)
    {
        _seat = Stage3D.Trunk(ground, lift, 0.0f, squash, rise);
        Stand();
    }

    private void Stand()
    {
        Transform = _seat;
        // On the ground rather than toward the camera, half a clearance like
        // the rack's rings; the fire a quarter more so it is never under the
        // dust by the coin toss two coplanar planes get.
        if (_dust is not null)
            _dust.Position = _nudge * 0.5f;
        if (_fire is not null)
            _fire.Position = _nudge * 0.75f;
        Place();
    }

    /// <summary>A shared number as the shaders have it now: what a dial
    /// turned, else the text's default.</summary>
    private float Knob(string name)
    {
        if (_live.TryGetValue(name, out float held))
            return held;
        float declared = Declared(name);
        return float.IsNaN(declared) ? 0.0f : declared;
    }

    /// <summary>
    /// Seat every arc on its own midpoint at the front's radius this frame - the
    /// same curve the vertex shader runs, so the node's origin is where its
    /// wall is drawn. This is what the sort against the tanks reads.
    /// </summary>
    private void Place()
    {
        float af = (_clock - Knob("flame_lag")) / Mathf.Max(Knob("shock_at"), 1e-4f);
        float front = Run(af, Knob("reach"));
        float back = Knob("smoke_back");
        foreach (Wall wall in _walls)
        {
            float scale = wall.Kind == Kind.Smoke ? back
                : wall.Shell == 0 ? 1.0f : InnerShell;
            float rr = Mathf.Max(front * scale, 0.02f) * _radius;
            Vector3 lift = _nudge * (wall.Kind == Kind.Smoke ? 0.5f : 0.75f);
            wall.Node.Position = lift + new Vector3(Mathf.Cos(wall.Mid), 0.0f,
                                                    Mathf.Sin(wall.Mid)) * rr;
        }
    }

    /// <summary>
    /// Which of the six neighbours the wave reaches, in
    /// <see cref="HexField.EdgeHeadings"/>' order (30, 90, 150, 210, 270, 330).
    /// The rules' answer - same level, on the board - is the caller's to give;
    /// this only draws it.
    /// </summary>
    public void Reach(IReadOnlyList<bool> reached)
    {
        float At(int i) => i < reached.Count && reached[i] ? 1.0f : 0.0f;
        var a = new Vector3(At(0), At(1), At(2));
        var b = new Vector3(At(3), At(4), At(5));
        foreach (ShaderMaterial ink in Inks())
        {
            ink.SetShaderParameter("mask_a", a);
            ink.SetShaderParameter("mask_b", b);
        }
    }

    public void Fire() => _clock = 0.0f;

    /// <summary>
    /// The wave as it plays under the fireball: the ground half only.
    ///
    /// <b>Two accounts of one death, and this is the one that gives way.</b> With
    /// <see cref="ProcBall"/> standing on the wreck and reaching the neighbours
    /// itself, a curtain of tongues round them at the same moment is a second
    /// fire drawn round the first. What the wave still owns is the rule: the
    /// pressure line, the light on the ground and the dust skirt crossing to the
    /// cells the rules reach, and <see cref="ArrivesAt"/> for the queue. The
    /// tongues, their sparks and the smoke wall go to nought; the effects bench's
    /// <c>W</c> shows the whole wave, the boards show this.
    /// </summary>
    public void Hush()
    {
        Dial("lick_gain", 0.0f);
        Dial("spark_gain", 0.0f);
        Dial("smoke_gain", 0.0f);
    }
    public void Douse() => _clock = -1.0f;

    public void Tick(double delta)
    {
        if (_clock >= 0.0f && !Hold)
        {
            _clock += (float)delta;
            if (_clock > Life)
                _clock = -1.0f;
        }
        bool on = _clock >= 0.0f;
        if (_dust is not null)
            _dust.Visible = on;
        if (_fire is not null)
            _fire.Visible = on;
        foreach (Wall wall in _walls)
            wall.Node.Visible = on;
        if (!on)
            return;
        foreach (ShaderMaterial ink in Inks())
            ink.SetShaderParameter("time", _clock);
        Place();
    }

    // --- the dials -----------------------------------------------------------

    private readonly Dictionary<string, float> _live = new();

    /// <summary>One number of the shaders, live - the rack's <c>Dial</c>: what
    /// was written, else the text's own default.</summary>
    public float Dial(string uniform) => Knob(uniform);

    /// <summary>The text's own default for a name, looked up in the four
    /// shaders in turn; NaN when none declares it.</summary>
    public static float Declared(string uniform)
    {
        foreach (string code in new[] { FireCode, DustCode, LickCode, SmokeCode })
        {
            float found = ProcBlast.Uniform(code, uniform);
            if (!float.IsNaN(found))
                return found;
        }
        return float.NaN;
    }

    /// <summary>The same number, written to every material: the frame's
    /// numbers - reach, timing, the mask - are shared, and a material that
    /// lacks a name ignores it. The arcs are re-seated, since the curve may
    /// have moved.</summary>
    public void Dial(string uniform, float value)
    {
        _live[uniform] = value;
        foreach (ShaderMaterial ink in Inks())
            ink.SetShaderParameter(uniform, value);
        Place();
    }

    // --- the shaders ---------------------------------------------------------

    /// <summary>What every material shares: the clock, the frame in
    /// circumradii, the six flags, and the two curves - where the shock and
    /// the front stand at <c>time</c>. <see cref="Run"/> is the curve in C#.</summary>
    private const string Frame = @"
uniform float time = 0.0;
uniform float level = 1.0;
// The plane's half-size and the far edge of the wave, in circumradii; the
// circumradius itself in screen px, for the curtains.
uniform float half = 3.0;
uniform float reach = 2.75;
uniform float radius_px = 124.0;
// Seconds for the shock to reach the far edge; the flame's lag behind it; and
// the whole event.
uniform float shock_at = 0.45;
uniform float flame_lag = 0.10;
uniform float life = 1.60;
// The six neighbours, in EdgeHeadings' order: 30 90 150 | 210 270 330.
uniform vec3 mask_a = vec3(1.0);
uniform vec3 mask_b = vec3(1.0);
uniform vec3 sun = vec3(-0.40, 0.82, -0.41);

// Which of the seven cells a ground point is in, and whether the wave reaches
// it. Axial coordinates of a flat-top lattice with circumradius one, screen y
// downward: x = 3/2 q, y = sqrt(3) (r + q/2). Cube-rounded to the nearest
// centre, which is exactly the hex tiling.
float hex_mask(vec2 p) {
    float q = (2.0 / 3.0) * p.x;
    float rr = (-1.0 / 3.0) * p.x + (0.57735027) * p.y;
    float x = q;
    float z = rr;
    float y = -x - z;
    float rx = round(x);
    float ry = round(y);
    float rz = round(z);
    float dx = abs(rx - x);
    float dy = abs(ry - y);
    float dz = abs(rz - z);
    if (dx > dy && dx > dz) {
        rx = -ry - rz;
    } else if (dy > dz) {
        ry = -rx - rz;
    } else {
        rz = -rx - ry;
    }
    int iq = int(rx);
    int ir = int(rz);
    if (iq == 0 && ir == 0) { return 1.0; }
    if (iq == 1 && ir == -1) { return mask_a.x; }
    if (iq == 0 && ir == -1) { return mask_a.y; }
    if (iq == -1 && ir == 0) { return mask_a.z; }
    if (iq == -1 && ir == 1) { return mask_b.x; }
    if (iq == 0 && ir == 1) { return mask_b.y; }
    if (iq == 1 && ir == 0) { return mask_b.z; }
    return 0.0;
}

// The mask read along a ring rather than at a point: three taps a step apart
// along the tangent, so a curtain standing on the ring thins over a sixth of a
// radius at a cell the wave does not reach instead of ending on a vertical
// line. The planes keep the hard mask - on the ground the cell's edge is the
// right edge.
float ring_mask(vec2 foot_) {
    vec2 t = normalize(vec2(-foot_.y, foot_.x) + vec2(1e-5, 0.0));
    return (hex_mask(foot_ - t * 0.16) + hex_mask(foot_) + hex_mask(foot_ + t * 0.16)) / 3.0;
}

// Fast off the hull and slowing: where a front stands at its own age 0..1.
float run(float a) {
    float k = clamp(a, 0.0, 1.0);
    return reach * (1.0 - (1.0 - k) * (1.0 - k));
}

// The two ages the planes and the curtains all read: the shock's and the
// flame's, each 0 at its start and 1 at the far edge.
float shock_age() { return time / max(shock_at, 1e-4); }
float flame_age() { return (time - flame_lag) / max(shock_at, 1e-4); }
";

    /// <summary>What the curtain shaders share on top of the frame: the arc's
    /// seat and the vertex work that stands a unit arc on the front.</summary>
    private const string CurtainFrame = @"
// This arc's midpoint angle, where its node is seated; the front's radius this
// wall stands at, as a share of the flame's.
uniform float arc_mid = 0.0;
uniform float ring_scale = 1.0;

varying vec2 foot;
varying float tall;
varying float ring_r;

// Stand the unit arc on the ring: out to the front's radius, up to the wall's
// height, and pulled back by the arc's own midpoint, which is where the node
// sits - so the mesh stays about its origin and the sort reads the right point.
// A function of the vertex rather than of VERTEX, which the shading language
// keeps out of user functions; the caller assigns the result and the varyings.
vec3 stand(vec3 v, float rr, float h, out vec2 foot_out) {
    vec2 unit = v.xz;
    vec2 mid = vec2(cos(arc_mid), sin(arc_mid));
    foot_out = unit * rr;
    return vec3((unit.x - mid.x) * rr * radius_px,
                v.y * h * radius_px,
                (unit.y - mid.y) * rr * radius_px);
}
";

    /// <summary>The fire plane: the shock as a thin hot ring, the front of
    /// flame behind it, and the embers it leaves on the ground.</summary>
    private const string FireShader = @"
shader_type spatial;
render_mode unshaded, cull_disabled, blend_add, depth_draw_never;

FLAME_NOISE

FLAME_INK

WAVE_FRAME

// The shock: a thin bright ring, light the ground catches.
uniform float shock_wide = 0.09;
uniform float shock_gain = 0.55;
// The front: how deep the band of flame is behind its leading edge, how bright,
// and how torn into tongues along its length.
uniform float flame_wide = 0.80;
uniform float flame_gain = 0.60;
uniform float flame_tear = 0.90;
// What the front leaves: embers on the ground, fading with the event.
uniform float glow_gain = 0.30;
uniform float glow_grain = 2.6;

void fragment() {
    vec2 p = (UV - 0.5) * 2.0 * half;
    float m = hex_mask(p);
    if (level <= 0.0 || m <= 0.0 || time < 0.0) {
        ALBEDO = vec3(0.0);
        ALPHA = 1.0;
    } else {
        float r = length(p);
        float turn = atan(p.y, p.x);
        float a = shock_age();
        float af = flame_age();
        float rs = run(a);
        float rf = run(af);
        float fade = pow(max(1.0 - time / max(life, 1e-3), 0.0), 1.30);
        vec3 lit = vec3(0.0);

        // The shock.
        if (a < 1.15) {
            float band = 1.0 - smoothstep(0.0, shock_wide * (0.6 + 0.8 * a),
                                          abs(r - rs));
            float torn = 0.55 + 0.45 * ember_fbm(vec2(turn * 2.2, rs * 2.7));
            lit += flame_ramp(0.03 + 0.35 * a).rgb * band * torn
                   * shock_gain * pow(max(1.0 - a / 1.15, 0.0), 1.4);
        }

        // The front: a band of flame behind its leading edge, torn into
        // tongues along its length, and dying once it has run its reach - a
        // front that stopped at the far edge and stayed lit read as a rim.
        if (af > 0.0 && af < 1.4 && r < rf + 0.05) {
            float back = rf - r;
            float tongue = ember_fbm(vec2(turn * 3.1 + 5.0, r * 1.7 - time * 2.4));
            float depth = flame_wide * (1.0 - flame_tear * 0.7
                                        + flame_tear * 1.4 * tongue);
            float edge = smoothstep(-0.06, 0.10, back);
            float tail = 1.0 - smoothstep(depth * 0.35, depth, back);
            float torn = 0.25 + 0.75 * ember_fbm(vec2(turn * 4.6 + 11.0,
                                                     back * 2.6 + time * 0.8));
            float front = edge * tail * torn;
            float heat = 0.06 + 0.50 * clamp(back / max(depth, 1e-3), 0.0, 1.0)
                         + 0.30 * clamp(af, 0.0, 1.0);
            vec4 ink = flame_ramp(heat);
            lit += ink.rgb * ink.a * front * flame_gain
                   * (1.0 - 0.45 * clamp(af, 0.0, 1.0))
                   * (1.0 - smoothstep(0.85, 1.4, af)) * (0.4 + 0.6 * fade);
        }

        // Embers where the front has been.
        if (r < rf) {
            float grain = ember_fbm(p * glow_grain + vec2(7.3, 1.9));
            float ember = smoothstep(0.55, 0.95, grain);
            float near = 1.0 - smoothstep(0.0, reach, r);
            lit += flame_ramp(0.55 + 0.35 * (1.0 - fade)).rgb * ember
                   * glow_gain * fade * (0.5 + 0.5 * near);
        }

        ALBEDO = lit * level * m;
        ALPHA = 1.0;
    }
}
";

    /// <summary>The dust plane: the pressure line that bends the ground, the
    /// skirt the shock pushes ahead of the flame, and the soot left where the
    /// flame passed - all darkening or displacing the ground.</summary>
    private const string DustShader = @"
shader_type spatial;
render_mode unshaded, cull_disabled, blend_mix, depth_draw_never;

FLAME_NOISE

WAVE_FRAME

// The shock as pressure: a thin line where the ground is seen through
// compressed air. The screen is read back and pulled outward across the band,
// which is what a pressure front does to what stands behind it, and a bright
// hairline rides its leading edge. The strength is in screen fractions.
uniform sampler2D screen : hint_screen_texture, filter_linear;
uniform float shock_warp = 0.022;
uniform float shock_line = 0.06;

// The skirt: how deep behind the shock it trails, how thick, how torn.
uniform float dust_wide = 0.85;
uniform float dust_gain = 0.80;
uniform float dust_tear = 0.60;
uniform float dust_grain = 1.9;
// The soot the flame leaves, and how long it lingers as a share of life.
uniform float soot_gain = 0.42;
uniform float soot_linger = 0.85;
uniform vec3 dust_colour = vec3(0.21, 0.16, 0.11);
uniform vec3 soot_colour = vec3(0.10, 0.09, 0.08);

void fragment() {
    vec2 p = (UV - 0.5) * 2.0 * half;
    float m = hex_mask(p);
    if (level <= 0.0 || m <= 0.0 || time < 0.0) {
        ALBEDO = vec3(0.0);
        ALPHA = 0.0;
    } else {
        float r = length(p);
        float turn = atan(p.y, p.x);
        float a = shock_age();
        float af = flame_age();
        float rs = run(a);
        float rf = run(af);
        float fade = pow(max(1.0 - time / max(life, 1e-3), 0.0), 1.10);

        // The skirt, behind the shock and ahead of the flame.
        float skirt = 0.0;
        if (a > 0.0 && r < rs) {
            float back = rs - r;
            float torn = 1.0 - dust_tear
                         + dust_tear * ember_fbm(vec2(turn * 2.6, r * dust_grain
                                                                  - time * 1.3));
            skirt = (1.0 - smoothstep(0.0, dust_wide, back)) * torn * dust_gain
                    * pow(max(1.0 - a / 1.7, 0.0), 0.9);
        }

        // The soot where the flame has been, lingering.
        float soot = 0.0;
        if (r < rf) {
            float grain = ember_fbm(p * 1.4 + vec2(3.1, 9.7));
            float linger = pow(max(1.0 - time / max(life * soot_linger, 1e-3), 0.0), 0.8);
            soot = (0.35 + 0.65 * grain) * soot_gain * linger
                   * smoothstep(0.0, 0.5, rf - r);
        }

        float alpha = clamp(skirt + soot, 0.0, 1.0) * level * m;
        vec3 tone = mix(soot_colour, dust_colour,
                        skirt / max(skirt + soot, 1e-4));
        vec3 colour = tone;
        alpha *= 0.35 + 0.65 * fade;

        // The pressure line: a narrow band at the shock, the screen behind it
        // pulled outward, sharpest at the start and softening as it slows.
        if (a > 0.0 && a < 1.1) {
            float wide = shock_line * (0.6 + 1.2 * a);
            float band = 1.0 - smoothstep(0.0, wide, abs(r - rs));
            float strength = band * pow(max(1.0 - a / 1.1, 0.0), 1.2);
            vec2 outward = normalize(p + vec2(1e-5, 0.0));
            vec2 shift = outward * shock_warp * strength;
            vec3 seen = texture(screen, SCREEN_UV + shift).rgb;
            // A hairline of heat on the leading edge, over the pulled ground -
            // white-hot at the start, and the one thing here that is allowed to
            // be bright, because it is a line and not a band.
            float edge = 1.0 - smoothstep(0.0, wide * 0.30, abs(r - rs - wide * 0.4));
            seen += mix(vec3(1.0, 0.95, 0.85), vec3(0.9, 0.6, 0.35), a)
                    * edge * strength * 1.4;
            colour = mix(colour, seen, strength);
            alpha = max(alpha, strength * m * level);
        }
        ALBEDO = colour;
        ALPHA = alpha;
    }
}
";

    /// <summary>
    /// The licks: tongues of flame standing on the front, and the sparks
    /// thrown up off them.
    ///
    /// <b>Each tongue is a body, not a cut-out.</b> <c>flame_blob</c> is the
    /// element the tank's and the tree's flames are made of - a body of
    /// revolution with a facing term for its cross-section, a dark lip at the
    /// rim and a bright shell inside it - and here it stands on the ring in
    /// the wall's own frame: across is arc length round the ring, up is height.
    /// Tongues sit in slots a fixed arc length apart, each with its own seed
    /// for height, width, lean and flicker, and a fragment asks the three slots
    /// nearest it. Two shells of the wall (<c>shell</c>) with different seeds,
    /// the inner behind and dimmer, are what puts one tongue in front of
    /// another.
    ///
    /// <b>The sparks are <see cref="ProcSpall"/>'s</b>: <c>flame_spark</c>, a
    /// streak whose long axis is its velocity, thrown up off each tongue when
    /// the front passes and falling back under the same gravity the ricochet's
    /// shards fall under.
    /// </summary>
    private const string LickShader = @"
shader_type spatial;
render_mode unshaded, cull_disabled, blend_add, depth_draw_never;

FLAME_NOISE

FLAME_INK

WAVE_FRAME

CURTAIN_FRAME

uniform float shell = 0.0;
// How tall the tongues stand at their tallest, in radii, and how bright; how
// far apart their slots are round the ring, in radii; how long after the front
// stops they burn, as a share of the shock's run.
uniform float lick_height = 0.95;
uniform float lick_gain = 1.10;
uniform float lick_space = 0.42;
uniform float lick_linger = 0.35;
// The sparks: how bright, how fast they leave (radii per second), how hard
// they fall, how big and how long a streak, how long they live.
uniform float spark_gain = 1.20;
uniform float spark_speed = 2.4;
uniform float spark_fall = 3.2;
uniform float spark_size = 0.022;
uniform float spark_long = 6.0;
uniform float spark_life = 0.75;
uniform vec3 spark_hot = vec3(1.000, 0.900, 0.680);

// Up quickly, held while the front runs, down as it dies.
float lick_rise(float af) {
    float up = smoothstep(0.0, 0.25, af);
    float down = 1.0 - smoothstep(1.0, 1.0 + lick_linger * 3.0, af);
    return up * down;
}

void vertex() {
    float af = flame_age();
    float rr = max(run(af) * ring_scale, 0.02);
    // The wall is taller than the tongues, so the sparks have room to fly.
    float h = lick_height * 1.9 * max(lick_rise(af), 0.05);
    vec2 f;
    VERTEX = stand(VERTEX, rr, h, f);
    foot = f;
    tall = h;
    ring_r = rr;
}

void fragment() {
    float m = ring_mask(foot);
    float af = flame_age();
    float rise = lick_rise(af);
    if (level <= 0.0 || m <= 0.0 || time < 0.0 || af <= 0.0 || rise <= 0.001) {
        ALBEDO = vec3(0.0);
        ALPHA = 1.0;
    } else {
        float circ = 6.2831853 * ring_r;
        float slots = max(floor(circ / lick_space), 6.0);
        float space = circ / slots;
        float u_len = UV.x * circ;
        float up = UV.y * tall;
        float i0 = floor(u_len / space);
        float since = time - flame_lag;
        vec4 acc = vec4(0.0);

        for (int k = -1; k <= 1; k++) {
            float fi = mod(i0 + float(k), slots);
            vec2 seed = vec2(fi, 3.0 + shell * 7.0);
            float h1 = ember_hash(seed);
            float h2 = ember_hash(seed + vec2(11.0, 0.0));
            float h3 = ember_hash(seed + vec2(23.0, 0.0));
            float h4 = ember_hash(seed + vec2(37.0, 0.0));
            // Where the slot's tongue stands: its centre, jittered, and the
            // fragment's offset from it round the ring - wrapped, so the seam
            // at u = 0 is a slot like any other.
            float cx = (fi + 0.5 + 0.6 * (h1 - 0.5)) * space;
            float dx = u_len - cx;
            dx -= circ * round(dx / circ);

            // The tongue: a blob standing on the ring, leaning a little and
            // flickering in height.
            float flick = 0.80 + 0.35 * ember_noise(vec2(fi * 3.1, time * 6.0 + h2 * 9.0));
            float height = lick_height * rise * (0.45 + 0.55 * h2) * flick;
            float half_w = space * (0.22 + 0.20 * h3);
            vec2 flow = normalize(vec2(0.35 * (ember_noise(vec2(fi * 1.7, time * 2.2)) - 0.5),
                                       1.0));
            float age = 0.04 + 0.45 * clamp(af, 0.0, 1.0) + 0.18 * shell;
            acc = flame_over(acc,
                             flame_blob(vec2(dx, up), vec2(0.0, height * 0.15),
                                        half_w, height, flow, h4 * 9.0, age,
                                        lick_gain * (1.0 - 0.35 * shell)));

            // The spark this slot threw when the front passed: born at the
            // tongue's foot, up along a lean of its own, falling back.
            float born = 0.05 * h3;
            float t = since - born;
            if (t > 0.0 && t < spark_life) {
                float vy = spark_speed * (0.55 + 0.75 * h4);
                float vx = spark_speed * 0.35 * (h1 - 0.5);
                vec2 pos = vec2(vx * t, vy * t - 0.5 * spark_fall * t * t);
                vec2 vel = vec2(vx, vy - spark_fall * t);
                vec2 dir = normalize(vel + vec2(0.0, 1e-4));
                float dying = pow(max(1.0 - t / spark_life, 0.0), 0.7);
                vec3 body = mix(spark_hot, ember_colour, clamp(t / spark_life, 0.0, 1.0));
                acc = flame_over(acc,
                                 flame_spark(vec2(dx, up), pos, spark_size,
                                             spark_size * spark_long
                                                 * (0.5 + 0.5 * length(vel) / spark_speed),
                                             dir, h2 * 5.0, body,
                                             spark_gain * dying));
            }
        }
        float fade = 1.0 - smoothstep(0.85, 1.0 + lick_linger * 3.0, af);
        ALBEDO = acc.rgb * fade * level * m;
        ALPHA = 1.0;
    }
}
";

    /// <summary>
    /// The smoke: a wall of dust behind the flame front, climbing as the wave
    /// runs and lingering after the flame is gone - the volume the flat soot
    /// could not give. Puffs are cut out of the wall by noise, lit on the side
    /// the sun is on by the ring's own outward normal, and shaded darker at
    /// the foot where the mass is thickest.
    /// </summary>
    private const string SmokeShader = @"
shader_type spatial;
render_mode unshaded, cull_disabled, blend_mix, depth_draw_never;

FLAME_NOISE

WAVE_FRAME

CURTAIN_FRAME

// How tall the smoke climbs, in radii; how thick; how far behind the flame's
// radius its wall stands; how much of life it takes to clear.
uniform float smoke_height = 1.60;
uniform float smoke_gain = 0.90;
uniform float smoke_back = 0.85;
uniform float smoke_linger = 1.10;
uniform vec3 smoke_colour = vec3(0.13, 0.11, 0.10);
uniform vec3 smoke_lit = vec3(0.42, 0.37, 0.32);

varying vec3 out_n;

float smoke_rise(float af) {
    // Climbs behind the front and keeps climbing slowly after it stops.
    return smoothstep(0.0, 0.6, af) * (0.7 + 0.3 * clamp(af / 2.5, 0.0, 1.0));
}

void vertex() {
    float af = flame_age();
    float rr = max(run(af) * smoke_back, 0.02);
    float h = smoke_height * max(smoke_rise(af), 0.02);
    vec2 f;
    VERTEX = stand(VERTEX, rr, h, f);
    foot = f;
    tall = h;
    ring_r = rr;
    out_n = NORMAL;
}

void fragment() {
    float m = ring_mask(foot);
    float af = flame_age();
    float linger = pow(max(1.0 - time / max(life * smoke_linger, 1e-3), 0.0), 0.9);
    if (level <= 0.0 || m <= 0.0 || time < 0.0 || af <= 0.0 || linger <= 0.001) {
        ALBEDO = vec3(0.0);
        ALPHA = 0.0;
    } else {
        float round_ = UV.x * 12.0;
        // Puffs: coarse noise round the ring and up it, drifting upward.
        float n = ember_fbm(vec2(round_ + time * 0.3, UV.y * 1.6 - time * 0.9));
        float n2 = ember_fbm(vec2(round_ * 1.9 + 31.0, UV.y * 2.4 - time * 1.3));
        float top = 0.30 + 0.70 * n;
        float body = 1.0 - smoothstep(top * 0.35, top, UV.y);
        float lump = 0.5 + 0.5 * smoothstep(0.25, 0.70, n2);
        // Thick at the foot, thinning up: a wall of dust is heaviest where it
        // was thrown from.
        float dens = body * lump * (0.55 + 0.45 * (1.0 - UV.y));
        // Lit on the sun's side, off the ring's outward normal.
        float side = dot(normalize(out_n), normalize(vec3(sun.x, 0.0, -sun.z)));
        float lit = 0.35 + 0.35 * side + 0.30 * UV.y;
        vec3 colour = mix(smoke_colour, smoke_lit, clamp(lit, 0.0, 1.0));
        ALBEDO = colour;
        ALPHA = clamp(dens * smoke_gain, 0.0, 1.0) * linger * level * m;
    }
}
";

    internal static readonly string FireCode =
        FireShader.Replace("WAVE_FRAME", Frame)
                  .Replace("FLAME_NOISE", Stage3D.EmberNoiseCode)
                  .Replace("FLAME_INK", Stage3D.FlameInk);

    internal static readonly string DustCode =
        DustShader.Replace("WAVE_FRAME", Frame)
                  .Replace("FLAME_NOISE", Stage3D.EmberNoiseCode);

    internal static readonly string LickCode =
        LickShader.Replace("WAVE_FRAME", Frame)
                  .Replace("CURTAIN_FRAME", CurtainFrame)
                  .Replace("FLAME_NOISE", Stage3D.EmberNoiseCode)
                  .Replace("FLAME_INK", Stage3D.FlameInk);

    internal static readonly string SmokeCode =
        SmokeShader.Replace("WAVE_FRAME", Frame)
                   .Replace("CURTAIN_FRAME", CurtainFrame)
                   .Replace("FLAME_NOISE", Stage3D.EmberNoiseCode);

    private static readonly Shader Blazing = new() { Code = FireCode };
    private static readonly Shader Dusting = new() { Code = DustCode };
    private static readonly Shader Licking = new() { Code = LickCode };
    private static readonly Shader Smoking = new() { Code = SmokeCode };
}
