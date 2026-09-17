using System;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The fireball of a destroyed tank: the plate burst's picture grown into a
/// body the size of a cell - <c>docs/blast.md</c>, "Шар взрыва".
///
/// <b>The fourth answer to what an explosion looks like, and the one that
/// asks for no new brushes.</b> <see cref="SheetBlast"/> is a simulation on a
/// sheet, <see cref="ToonBlast"/> a drawing, <see cref="ProcWave"/> a front
/// crossing to the neighbours; this is the board's own flame and dust -
/// <see cref="Stage3D.FlameInk"/>, <see cref="ProcBlast.DustInk"/>, the same
/// texts <see cref="ProcSlam"/> and <see cref="ProcRack"/> are made of - at the
/// scale of the reference photograph of a real detonation from above: a white
/// heart, a ball of fire rolling out of it in every direction, a skirt of dust
/// along the ground and a dark head that outlives the fire. It was asked for in
/// exactly those words: the burst on masonry, only bigger, so the style holds.
///
/// <b>Radial, and that is what separates it from both of its parents.</b> The
/// plate burst leaves along a normal and the rack leaves upward through the
/// hatches; a hull that has come apart confines nothing, so every family here
/// leaves the heart in a hemisphere - <c>ball_dir</c> - with the downward
/// throws shortened, because down there is the ground. There is no bearing,
/// no <c>away</c>, no <c>squat</c>, and <see cref="Bounds"/> has one case.
///
/// <b>What scaling alone would not have given, and why this is a class rather
/// than a <see cref="ProcSlam.Might"/> of five.</b> The families' counts are
/// loop bounds, so a small burst scaled is a big burst with six lobes - a
/// balloon. A body a cell wide wants more elements, each smaller against the
/// whole, and it wants its own clock: gas at this size rises for two seconds
/// where the plate burst is over in one and a half. Both are numbers of this
/// text, not of the shared one.
///
/// <b>One quad, and the limitation is stated where it is paid.</b> A body two
/// cells across sorts against a neighbouring tank as one thing at its seat; the
/// wave's twelve arcs exist for that and this does not have them. What rises
/// above the hulls reads right either way; the skirt on the ground is where it
/// can be caught out.
/// </summary>
public sealed partial class ProcBall : Node3D
{
    /// <summary>
    /// How far the ball gets from the heart, in tile widths - every other length
    /// in the two shaders is a ratio to it, so a bigger detonation is this one at
    /// a bigger <see cref="Might"/>.
    ///
    /// <b>A cell, near enough.</b> The neighbours' centres stand 0.87 of a tile
    /// away, their far edges at 1.30; the photograph's ball covers its own cell
    /// and licks the next, which is this.
    /// </summary>
    public float Reach = ReachDefault;

    /// <summary>The numbers a bench has to be able to reset a dial to -
    /// <see cref="SheetBlast.ReachDefault"/>'s reason.</summary>
    public const float ReachDefault = 1.30f;
    public const float LifeDefault = 2.60f;

    /// <summary>Where the heart is before anything has measured a hull - the
    /// shaders' own <c>heart</c> default, kept here as well because the reader
    /// of uniforms reads numbers and this is a pair; the self test holds the two
    /// copies together.</summary>
    public static readonly Vector2 HeartDefault = new(0.0f, 0.22f);

    /// <summary>
    /// The quad, in tile widths: half-width either side of the seat, and height
    /// above it. The widest quad on this board and nearly the tallest, and it has
    /// to be: see <see cref="Bounds"/>, which is asked of the model and checked
    /// against these two.
    /// </summary>
    public float Flank = 2.30f;
    public float Tall = 3.15f;

    /// <summary>The burst frame's own <c>root</c> pair - unchanged, the ground
    /// under a wreck is the same ground.</summary>
    public float Root = 0.02f;
    public float Fade = 0.050f;

    /// <summary>How far the rings' plane reaches, in tile widths. Wider than
    /// the rack's by the ratio of the throws: this one lights the neighbours'
    /// ground, which is the event.</summary>
    public float RingReach = 1.45f;

    /// <summary>How far the ground plane the dust spreads on reaches, in tile
    /// widths - past the neighbours' far edges (1.30), because that is where the
    /// dust has to be seen arriving.</summary>
    public float DustReach = 1.55f;

    /// <summary>
    /// The whole event, in seconds. Longer than anything else on the board
    /// except the wreck's own fire: the fire is out by the first second, and the
    /// rest is the head rising and the skirt settling, which is what a big
    /// explosion spends its time on.
    /// </summary>
    public float Life = LifeDefault;

    private float _clock = -1.0f;

    /// <summary>Whether it is running. -1 is out, and out means nothing drawn.
    /// </summary>
    public bool Alive => _clock >= 0.0f;

    /// <summary>Where the clock is, for a bench that shows what one is doing.
    /// </summary>
    public float Age => _clock;

    private MeshInstance3D? _dust;
    private MeshInstance3D? _fire;
    private MeshInstance3D? _ring;
    private MeshInstance3D? _carpet;
    private ShaderMaterial? _dustInk;
    private ShaderMaterial? _fireInk;
    private ShaderMaterial? _ringInk;
    private ShaderMaterial? _carpetInk;
    private Vector3 _nudge;

    /// <summary>
    /// The furthest the model can put anything, in tile widths: out sideways,
    /// and up. One case, no sweep - nothing here has a bearing.
    /// <paramref name="heart"/> is where the charge went off over the seat,
    /// because every family starts there.
    /// </summary>
    public static (float Along, float Up) Bounds(float reach, Vector2 heart)
    {
        float along = 0.0f, up = 0.0f;
        void Reach(Vector2 at, float across, float tall)
        {
            along = MathF.Max(along, MathF.Abs(at.X) + across);
            up = MathF.Max(up, MathF.Abs(at.Y) + tall);
        }

        // The flash, which grows and does not travel.
        float flashR = Uniform("flash_size") * 1.30f;
        Reach(Vector2.Zero, flashR, flashR);

        // The ball, at the end of its run in the widest and the highest direction
        // (the fastest element, 1.30 of the mean), grown to its full size.
        float ballOut = Uniform("ball_lead") * reach * 1.30f;
        float ballUp = ballOut + Uniform("ball_climb") * reach * 1.30f;
        float ballR = Uniform("ball_size") * 1.45f * (0.45f + Uniform("ball_grow"));
        Reach(new Vector2(ballOut, ballUp), ballR * 1.15f, ballR * 1.15f);

        // The embers, at the top of the fastest one's arc: out at its speed, up by
        // the same before the fall takes it back. Below the ground does not count,
        // the footing takes that.
        float emberOut = Uniform("ember_speed") * reach * 1.25f;
        float emberR = Uniform("ember_size") * 1.70f * Uniform("ember_long");
        Reach(new Vector2(emberOut, emberOut), emberR, emberR);

        // The head, which is the widest and highest thing here and what the quad
        // is cut to.
        float sootOut = Uniform("soot_lead") * reach * 1.25f;
        float sootUp = sootOut + Uniform("soot_climb") * reach * 1.30f;
        float sootR = Uniform("soot_size") * 1.40f * (0.50f + Uniform("soot_grow"));
        Reach(new Vector2(sootOut, sootUp), sootR * 1.20f, sootR * 1.20f);

        // The skirt, whose height is the ground's: measured from the seat, so the
        // heart is taken back off it.
        float skirtOut = Uniform("skirt_run") * reach * 1.35f;
        float skirtR = Uniform("skirt_size") * 1.50f;
        Reach(new Vector2(skirtOut, Uniform("skirt_seat") - heart.Y),
              skirtR * Uniform("skirt_long"), skirtR);

        // Everything above is an offset from the heart, and the quad is measured
        // from the contact point.
        return (along + MathF.Abs(heart.X), up + MathF.Abs(heart.Y));
    }

    /// <summary>One of the two shaders' own numbers, by name - the fire first,
    /// the dust's names fall through to it.</summary>
    private static float Uniform(string name) =>
        ProcBlast.Uniform(FireCode, name,
                          ProcBlast.Uniform(DustCode, name, 0.0f));

    /// <summary>One of the three shaders' declared defaults, or NaN when none of
    /// them has the name - what a panel opens a dial on and what the self test
    /// looks for. <see cref="ProcWave.Declared"/>'s arrangement.</summary>
    public static float Declared(string uniform)
    {
        foreach (string code in new[] { FireCode, DustCode, GroundCode, ProcBlast.RingCode })
        {
            float found = ProcBlast.Uniform(code, uniform);
            if (!float.IsNaN(found))
                return found;
        }
        return float.NaN;
    }

    /// <summary>The quad, in screen px - <see cref="ProcBlast.Quad"/> entire.
    /// </summary>
    public static (Vector2 Foot, Vector2 Size) Quad(float tile, float flank,
                                                    float tall)
        => ProcBlast.Quad(tile, flank, tall);

    /// <summary>
    /// Build the three quads. <paramref name="tile"/> is the hex's own width in
    /// screen px and the two camera terms are the field's, handed in so this
    /// needs no board - <see cref="ProcRack.Build"/>'s arrangement throughout.
    /// </summary>
    public void Build(float tile, float squash, float rise)
    {
        _tile = Mathf.Max(tile, 1.0f);
        (Vector2 foot, Vector2 size) = Quad(tile, Flank, Tall);
        ArrayMesh shape = Stage3D.Stem(foot, size, rise);

        _dustInk = Ink(Sooting, dust: true);
        _fireInk = Ink(Blazing, dust: false);
        _dust = Slab(shape, _dustInk);
        _fire = Slab(shape, _fireInk);
        _nudge = Stage3D.Clear(squash, rise);

        // The rings on their own plane on the ground, ProcBlast's arrangement -
        // and brighter than the rack's, because this one lights the cell and the
        // six around it rather than the inside of a hull.
        _ringInk = new ShaderMaterial
        {
            Shader = ProcBlast.Ringing,
            RenderPriority = Stage3D.StandOrder,
        };
        _ringInk.SetShaderParameter("level", 1.0f);
        _ringInk.SetShaderParameter("time", 0.0f);
        _ringInk.SetShaderParameter("ring_gain", 0.36f);
        _ringInk.SetShaderParameter("ring_life", 0.55f);
        // <b>The disc is the flash reaching the neighbours' ground</b>, and it is
        // the one part of the fire this camera lets on to the near cells: the
        // upright quad stops at the ground line, so a ball that reaches the cell
        // in front of the wreck can only be shown lighting it. Wide and slow for
        // that reason - a ring plane's disc is a lamp under a shell for 0.11s;
        // this one is a fireball standing on the cell for half a second.
        _ringInk.SetShaderParameter("disc_gain", 0.90f);
        _ringInk.SetShaderParameter("disc_wide", 0.70f);
        _ringInk.SetShaderParameter("disc_life", 0.45f);
        _ring = new MeshInstance3D
        {
            Mesh = new PlaneMesh
            {
                Size = new Vector2(2.0f * RingReach * tile,
                                   2.0f * RingReach * tile),
            },
            SortingUseAabbCenter = false,
            MaterialOverride = _ringInk,
            Visible = false,
        };
        AddChild(_ring);

        // <b>The dust on its own plane on the ground, and that is what makes it
        // reach the neighbours in every direction rather than left and right.</b>
        // An upright quad has one horizontal: its skirt runs along the screen, so
        // the cells in front of and behind the wreck - which under this camera are
        // down and up the screen - never see any dust. A plane lying on the ground
        // gets the board's own projection for free: a ring on it is the ellipse
        // the eye expects, and dust thrown "toward the camera" on it lands on the
        // near cell. ProcWave's ground planes, and their reason.
        //
        // <b>Sorted under what stands</b> (RingOrder), the wave's argument: dust at
        // foot level around a neighbour's tracks is dust it is standing in, and a
        // plane over the tanks would be a sheet laid on them.
        _carpetInk = Ink(Carpeting, dust: true);
        _carpetInk.RenderPriority = Stage3D.RingOrder;
        _carpetInk.SetShaderParameter("carpet", DustReach);
        _carpet = new MeshInstance3D
        {
            Mesh = new PlaneMesh
            {
                Size = new Vector2(2.0f * DustReach * tile,
                                   2.0f * DustReach * tile),
            },
            SortingUseAabbCenter = false,
            MaterialOverride = _carpetInk,
            Visible = false,
        };
        AddChild(_carpet);
        Stand();
    }

    /// <summary>How big this one is, as a multiple of the tuned ball - the
    /// victim's size, <see cref="ProcRack.Might"/>'s reason. What it does not
    /// scale is where the heart is: see <see cref="Place"/>.</summary>
    public float Might
    {
        get => _might;
        set
        {
            _might = Mathf.Max(value, 0.01f);
            Stand();
            Place();
        }
    }

    private float _might = 1.0f;
    private Transform3D _seat = Transform3D.Identity;
    private float _side = 1.0f;
    private float _tile = 1.0f;

    /// <summary>Where it stands: the flat point the tank is touching and the
    /// lift of the cell under it. <paramref name="behind"/> puts it on the far
    /// side of the tank's own surface - one flag for the whole event, the rack's
    /// stated simplification.</summary>
    public void Sit(Vector2 ground, float lift, float squash, float rise,
                    bool behind = false)
    {
        _seat = Stage3D.Trunk(ground, lift, 0.0f, squash, rise);
        _side = behind ? -1.0f : 1.0f;
        Stand();
    }

    private void Stand()
    {
        Transform = _seat.ScaledLocal(Vector3.One * _might);
        if (_dust is not null)
            _dust.Position = _nudge * _side / _might;
        if (_fire is not null)
            _fire.Position = _nudge * 2.0f * _side / _might;
        if (_ring is not null)
            _ring.Position = _nudge * 0.5f / _might;
        if (_carpet is not null)
            _carpet.Position = _nudge * 0.75f / _might;
    }

    /// <summary>Which side of the tank's own surface this one is on.</summary>
    public bool Behind => _side < 0.0f;

    /// <summary>
    /// Where the charge went off over the contact point, in tile widths - the
    /// rack's deck, and for a wreck it is the same measurement: the hull's own
    /// height, which <c>anchor_px</c> gives for free. Unset, the shader's own
    /// default stands, which is a hull of the middle size.
    /// </summary>
    public void Aim(Vector2 heart)
    {
        Heart = heart;
        Place();
    }

    /// <summary>Where the heart is, over the seat, in tile widths - what
    /// <see cref="Aim"/> was told, unscaled; NaN until it was told.</summary>
    public Vector2 Heart { get; private set; } = new(float.NaN, float.NaN);

    /// <summary>The heart as the shaders have it: the measurement divided by the
    /// size, so the transform's multiply gives back the measurement.</summary>
    public Vector2 Placed => Heart / Mathf.Max(_might, 0.01f);

    private void Place()
    {
        if (float.IsNaN(Heart.X))
            return;
        Vector2 at = Placed;
        _dustInk?.SetShaderParameter("heart", at);
        _fireInk?.SetShaderParameter("heart", at);
    }

    /// <summary>Set it off. Restarts rather than refusing -
    /// <see cref="SheetBlast.Fire"/>'s reason.</summary>
    public void Fire() => _clock = 0.0f;

    /// <summary>Whether the clock stands still - <see cref="SheetBlast.Hold"/>.
    /// </summary>
    public bool Hold;

    /// <summary>Where the clock is, settable, so a held ball can be scrubbed to
    /// a frame.</summary>
    public float Clock
    {
        get => _clock;
        set => _clock = Mathf.Clamp(value, 0.0f, Life);
    }

    /// <summary>Put it out with nothing drawn.</summary>
    public void Douse() => _clock = -1.0f;

    /// <summary>
    /// Whether the two ground planes draw - the rings and disc of the shock on
    /// the ground, and the carpet of dust it throws across the neighbours.
    ///
    /// <b>Off for a blast that happens inside a hull.</b> The knock-out flash
    /// (<c>Stage3D.Flash</c>) is this fireball at a third of the size, and at that
    /// size the rings under the tank were the thing seen: a shock running out
    /// across the ground from a round that went off inside the armour, which has
    /// nothing outside to shock. A death keeps both - the blast that kills
    /// reaches the six neighbours and the ground is how that is shown. Reset to
    /// on by every <c>Stage3D.Ball</c> before the caller decides, so a pooled
    /// ball does not carry the last caller's answer.
    /// </summary>
    public bool Grounded = true;

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
        if (_ring is not null)
            _ring.Visible = on && Grounded;
        if (_carpet is not null)
            _carpet.Visible = on && Grounded;
        if (!on)
            return;

        _dustInk?.SetShaderParameter("time", _clock);
        _fireInk?.SetShaderParameter("time", _clock);
        _ringInk?.SetShaderParameter("time", _clock);
        _carpetInk?.SetShaderParameter("time", _clock);
    }

    /// <summary>One number of one of the three shaders, live -
    /// <see cref="ProcSlam.Dial(ProcSlam.Part, string)"/>'s arrangement.</summary>
    public float Dial(Part part, string uniform)
    {
        string key = part + "." + uniform;
        if (!_live.TryGetValue(key, out float now))
        {
            now = ProcBlast.Uniform(Source(part), uniform);
            _live[key] = now;
        }
        return now;
    }

    /// <summary>The same number, written - a count goes as an int, because a
    /// float in an int uniform is a loop that runs zero times.</summary>
    public void Dial(Part part, string uniform, float value)
    {
        _live[part + "." + uniform] = value;
        bool counted = Source(part).Contains("uniform int " + uniform + " = ",
                                             StringComparison.Ordinal);
        Variant sent = counted ? Mathf.RoundToInt(value) : value;
        if (part == Part.Frame)
        {
            _dustInk?.SetShaderParameter(uniform, sent);
            _fireInk?.SetShaderParameter(uniform, sent);
            _carpetInk?.SetShaderParameter(uniform, sent);
            return;
        }
        Coat(part)?.SetShaderParameter(uniform, sent);
    }

    /// <summary>Which of the three a number belongs to; <see cref="Part.Frame"/>
    /// is the pair the quad itself declares.</summary>
    public enum Part { Frame, Dust, Fire, Ring, Ground }

    private readonly System.Collections.Generic.Dictionary<string, float> _live
        = new();

    private static string Source(Part part) => part switch
    {
        Part.Dust => DustCode,
        Part.Fire => FireCode,
        Part.Ring => ProcBlast.RingCode,
        Part.Ground => GroundCode,
        _ => ProcBlast.FrameCode,
    };

    private ShaderMaterial? Coat(Part part) => part switch
    {
        Part.Dust => _dustInk,
        Part.Fire => _fireInk,
        Part.Ring => _ringInk,
        Part.Ground => _carpetInk,
        _ => null,
    };

    private MeshInstance3D Slab(ArrayMesh shape, ShaderMaterial ink)
    {
        var node = new MeshInstance3D
        {
            Mesh = shape,
            SortingUseAabbCenter = false,
            MaterialOverride = ink,
            Visible = false,
        };
        AddChild(node);
        return node;
    }

    private ShaderMaterial Ink(Shader how, bool dust)
    {
        var ink = new ShaderMaterial
        {
            Shader = how,
            RenderPriority = Stage3D.StandOrder,
        };
        ink.SetShaderParameter("foot_v", 1.0f);
        ink.SetShaderParameter("tall", Tall);
        ink.SetShaderParameter("wideq", Flank * 2.0f);
        ink.SetShaderParameter("root", Root);
        ink.SetShaderParameter("floor_fade", Fade);
        ink.SetShaderParameter("reach", Reach);
        ink.SetShaderParameter("level", 1.0f);
        ink.SetShaderParameter("time", 0.0f);
        ink.SetShaderParameter("sun", Stage3D.Sun);
        if (!dust)
            return ink;
        // The shared banding at the burst's own helping - this is the biggest
        // mass on the board, and the shared 0.55 was tuned on a mass.
        ink.SetShaderParameter("dust_band", 0.50f);
        ink.SetShaderParameter("dust_tear", 0.56f);
        // Hides the wreck outright, the rack's reason: this is the tank coming
        // apart, and a head you can read the hatches through says it did not.
        ink.SetShaderParameter("dust_ink", 0.80f);
        return ink;
    }

    /// <summary>
    /// The fire: the flash, the ball rolling out of it in every direction, and
    /// the embers thrown past the ball's edge and falling.
    ///
    /// <b>The ball is the event, and it is the plate burst's ball with the
    /// normal taken out.</b> Sixteen bodies leaving one point in a hemisphere,
    /// decelerating hard - gas into air - and rising while they go, each on its
    /// own place on the ramp, grown half again by the time it dies. Read at one
    /// stop and one size the whole thing composites into a flat orange disc,
    /// which is ProcBlast's ball's finding and paid for there.
    ///
    /// <b>The embers are the photograph's streamers</b>: what the heart throws
    /// past the ball and what falls back through the dust afterwards, cooling
    /// from near-white to the ink's own ember colour. The plate burst has none
    /// on masonry because real bricks fly there; nothing simulated flies here.
    ///
    /// Additive and premultiplied, the fire's reason everywhere on this board.
    /// </summary>
    private const string FireBallShader = @"
shader_type spatial;
render_mode unshaded, cull_disabled, blend_add, depth_draw_never;

FLAME_NOISE

FLAME_INK

BLAST_FRAME

// <b>Where the charge went off over the contact point</b>, in tile widths -
// ProcBall.Heart; the dust half declares the same number. A hull of the middle
// size, so the bench draws something sensible before any tank has been measured.
uniform vec2 heart = vec2(0.0, 0.22);

// Which way an element leaves, from a hash: a hemisphere and a little under the
// horizon, with the downward throws shortened - down there is the ground, and a
// ball drawn into it is a ball cut off flat by the footing.
uniform float under = 0.35;
vec2 ball_dir(float h) {
    float th = mix(-0.22, 1.22, h) * 3.14159265;
    vec2 dir = vec2(cos(th), sin(th));
    if (dir.y < 0.0) {
        dir.y *= under;
    }
    return dir;
}

// The flash: the charge going off. Bigger than any flash on the board, and past
// one in gain deliberately - the white heart is the clip of an orange body added
// to itself, brightness rather than a whiter colour (ProcBlast's flash's note).
uniform float flash_life = 0.11;
uniform float flash_size = 0.60;
uniform float flash_gain = 3.60;

// The ball.
uniform int balls = 22;
uniform float ball_life = 1.10;
uniform float ball_stagger = 0.08;
// How far out a body's centre gets, as a share of the reach; how much it rises
// on top of that by the end, as another share.
uniform float ball_lead = 0.55;
uniform float ball_climb = 0.28;
// A body's radius, and how much it grows over its life, as a multiple.
uniform float ball_size = 0.30;
uniform float ball_grow = 1.30;
uniform float ball_gain = 1.05;

// The embers.
uniform int embers = 26;
uniform float ember_life = 1.30;
uniform float ember_stagger = 0.12;
// Reach per life, and how far they fall by the end, quadratic in age.
uniform float ember_speed = 1.35;
uniform float ember_fall = 0.90;
uniform float ember_size = 0.012;
uniform float ember_long = 3.00;
uniform float ember_gain = 0.85;
uniform vec3 ember_hot = vec3(1.000, 0.920, 0.720);
uniform float ember_cool = 0.45;

void fragment() {
    if (level <= 0.0) {
        ALBEDO = vec3(0.0);
        ALPHA = 0.0;
    } else {
        vec2 at = blast_at(UV);
        vec4 lit = vec4(0.0);

        for (int k = 0; k < balls; k++) {
            float fk = float(k);
            float born = ball_stagger * ember_hash(vec2(fk, 201.0));
            float a = (time - born) / max(ball_life, 1e-3);
            if (a > 0.0 && a < 1.0) {
                vec2 dir = ball_dir(ember_hash(vec2(fk, 202.0)));
                float fast = 0.45 + 0.85 * ember_hash(vec2(fk, 203.0));
                float out_at = ball_lead * reach * fast * pow(a, 0.42);
                float climb = ball_climb * reach * pow(a, 1.10)
                              * (0.40 + 0.90 * ember_hash(vec2(fk, 204.0)));
                vec2 p = heart + dir * out_at + vec2(0.0, climb);
                float r = ball_size * (0.55 + 0.90 * ember_hash(vec2(fk, 205.0)))
                          * (0.45 + ball_grow * a);
                // Stretched a little along its own travel while it is being
                // pushed, round once it has stopped - the jets' finding.
                float draw = mix(1.35, 1.0, clamp(a * 1.6, 0.0, 1.0));
                // Each body its own place on the ramp, and the far ones further
                // down it: the photograph's ball is white at the heart and red
                // at the rim, and read at one stop the whole thing composites
                // into a flat orange disc - ProcBlast's ball's finding.
                float rim = out_at / max(ball_lead * reach, 1e-3);
                lit = flame_over(lit,
                                 flame_blob(at, p, r, r * draw, dir, fk + 201.0,
                                            clamp(a + 0.35
                                                  * ember_hash(vec2(fk, 206.0))
                                                  + 0.25 * rim,
                                                  0.0, 1.0),
                                            ball_gain * flame_life(a, 0.85)));
            }
        }

        for (int k = 0; k < embers; k++) {
            float fk = float(k);
            float born = ember_stagger * ember_hash(vec2(fk, 211.0));
            float a = (time - born) / max(ember_life, 1e-3);
            if (a > 0.0 && a < 1.0) {
                vec2 dir = ball_dir(ember_hash(vec2(fk, 212.0)));
                float fast = 0.35 + 0.90 * ember_hash(vec2(fk, 213.0));
                float out_at = ember_speed * reach * fast * pow(a, 0.70);
                vec2 p = heart + dir * out_at
                         - vec2(0.0, ember_fall * reach * a * a);
                vec2 trail = p - heart;
                vec2 flow = length(trail) < 1e-4 ? dir : normalize(trail);
                float r = ember_size * (0.50 + 1.20 * ember_hash(vec2(fk, 214.0)));
                float draw = mix(ember_long
                                 * (0.45 + 1.15 * ember_hash(vec2(fk, 215.0))),
                                 1.0, clamp(a * 1.3, 0.0, 1.0));
                float gain = ember_gain * (1.0 - exp(-a / 0.03))
                             * pow(max(1.0 - a, 0.0), 1.20);
                vec3 body = mix(ember_hot, ember_colour,
                                smoothstep(0.0, max(ember_cool, 1e-3), a));
                lit = flame_over(lit, flame_spark(at, p, r, r * draw, flow,
                                                  fk + 211.0, body, gain));
            }
        }

        // The flash last and on top - the rack's finding: composited first it
        // is covered by the ball leaving the same point on the same frame, and
        // the event opens orange with no beginning.
        float fa = time / max(flash_life, 1e-4);
        if (fa < 1.0) {
            float r = flash_size * (0.40 + 0.90 * sqrt(fa));
            float gain = flash_gain * pow(max(1.0 - fa, 0.0), 1.30);
            lit = flame_over(lit,
                             flame_blob(at, heart, r, r * 0.92,
                                        vec2(0.0, 1.0), 0.0, 0.0, gain));
        }

        ALBEDO = lit.rgb * (level * blast_footing(at));
        ALPHA = 1.0;
    }
}
";

    /// <summary>
    /// The dust: the head the ball turns into, and the skirt the shock pushes
    /// out along the ground.
    ///
    /// <b>The head leaves the heart the way the ball does and keeps going after
    /// the fire is out</b> - slower, wider, and rising all the while, so the
    /// ball is read as turning into it rather than vanishing into a smoke that
    /// was already there. Twenty-eight elements: the count is what makes a body
    /// this size a body rather than a balloon.
    ///
    /// <b>The skirt is the photograph's dust ring</b>: lifted off the ground by
    /// the shock, flat, and running out past the ball on both sides. It is the
    /// half of this picture that says the ground took it too.
    ///
    /// <b>Earth and soot, and the pair sits between the plate burst's and the
    /// rack's.</b> What burns is a tank's own fuel, which is the rack's black;
    /// what the shock lifts is the cell, which is the burst's earth. Neither
    /// stop is a real black, for ProcBlast.DustInk's measured reason.
    /// </summary>
    private const string DustBallShader = @"
shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_never;

FLAME_NOISE

FLAME_INK

BLAST_FRAME

DUST_INK

uniform vec2 heart = vec2(0.0, 0.22);

uniform float under = 0.35;
vec2 ball_dir(float h) {
    float th = mix(-0.22, 1.22, h) * 3.14159265;
    vec2 dir = vec2(cos(th), sin(th));
    if (dir.y < 0.0) {
        dir.y *= under;
    }
    return dir;
}

// The head.
uniform int soot = 44;
uniform float soot_life = 2.20;
uniform float soot_stagger = 0.22;
uniform float soot_lead = 0.52;
uniform float soot_climb = 0.65;
uniform float soot_size = 0.26;
uniform float soot_grow = 1.60;
uniform float soot_ink = 1.00;

// The skirt.
uniform int skirt = 10;
uniform float skirt_life = 1.50;
uniform float skirt_stagger = 0.08;
uniform float skirt_run = 0.70;
uniform float skirt_size = 0.16;
uniform float skirt_long = 2.10;
uniform float skirt_seat = 0.05;
uniform float skirt_ink = 0.55;

uniform vec3 soot_dark = vec3(0.225, 0.205, 0.192);
uniform vec3 soot_lit = vec3(0.680, 0.640, 0.596);

void fragment() {
    if (level <= 0.0) {
        ALBEDO = vec3(0.0);
        ALPHA = 0.0;
    } else {
        vec2 at = blast_at(UV);
        float dens = 0.0;
        float years = 0.0;
        vec2 lean = vec2(0.0);

        for (int k = 0; k < soot; k++) {
            float fk = float(k);
            float born = soot_stagger * ember_hash(vec2(fk, 221.0));
            float a = (time - born) / max(soot_life, 1e-3);
            if (a > 0.0 && a < 1.0) {
                vec2 dir = ball_dir(ember_hash(vec2(fk, 222.0)));
                float fast = 0.40 + 0.85 * ember_hash(vec2(fk, 223.0));
                float out_at = soot_lead * reach * fast * pow(a, 0.40);
                float climb = soot_climb * reach * pow(a, 0.90)
                              * (0.45 + 0.85 * ember_hash(vec2(fk, 224.0)));
                vec2 p = heart + dir * out_at + vec2(0.0, climb);
                float r = soot_size * (0.60 + 0.80 * ember_hash(vec2(fk, 225.0)))
                          * (0.50 + soot_grow * a);
                float gain = soot_ink * (1.0 - exp(-a / 0.050))
                             * pow(max(1.0 - a, 0.0), 0.90);
                dust_part(at, p, r, r * 1.15, vec2(0.0, 1.0), fk + 221.0,
                          gain, dust_soft, a, dens, lean, years);
            }
        }

        for (int k = 0; k < skirt; k++) {
            float fk = float(k);
            float born = skirt_stagger * ember_hash(vec2(fk, 231.0));
            float a = (time - born) / max(skirt_life, 1e-3);
            if (a > 0.0 && a < 1.0) {
                float hand = ember_hash(vec2(fk, 232.0)) < 0.5 ? -1.0 : 1.0;
                float out_at = skirt_run * reach * pow(a, 0.50)
                               * (0.35 + 1.00 * ember_hash(vec2(fk, 233.0)));
                vec2 p = vec2(heart.x + hand * out_at, skirt_seat + 0.05 * a);
                float r = skirt_size * (0.55 + 0.95 * ember_hash(vec2(fk, 234.0)))
                          * (0.45 + 1.10 * a);
                float gain = skirt_ink * (1.0 - exp(-a / 0.040))
                             * pow(max(1.0 - a, 0.0), 1.40);
                dust_part(at, p, r, r * skirt_long, vec2(hand, 0.0), fk + 231.0,
                          gain, dust_soft, a, dens, lean, years);
            }
        }

        if (dens <= 1e-4) {
            ALBEDO = vec3(0.0);
            ALPHA = 0.0;
        } else {
            float mass = dust_mass(at, dens);
            float lit = dust_lit(mass, lean);
            ALBEDO = mix(soot_dark, soot_lit, lit);
            ALPHA = clamp(mass * dust_ink * level * blast_footing(at), 0.0, 1.0);
        }
    }
}
";

    /// <summary>
    /// The dust on the ground: the head's own elements laid flat, leaving the
    /// seat in every direction over the six neighbours.
    ///
    /// <b>Radial in the plane, which is what the upright quad cannot be.</b> An
    /// element's direction is a full turn from a hash, its run a share of the
    /// reach, and the plane's own projection turns the circle into the ellipse
    /// the board draws everything else in. So the dust arriving on the cell in
    /// front of the wreck is drawn arriving there, which is the sentence the
    /// rules' explosion has to say about its neighbours.
    ///
    /// The shared frame is included for its <c>sun</c>, <c>time</c> and
    /// <c>level</c>; its <c>blast_at</c> is not used, the plane has its own.
    /// </summary>
    private const string GroundBallShader = @"
shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_never;

FLAME_NOISE

FLAME_INK

BLAST_FRAME

DUST_INK

// Half the plane, in tile widths - ProcBall.DustReach, so a UV is a length.
uniform float carpet = 1.55;

uniform int puffs = 40;
uniform float puff_life = 2.00;
uniform float puff_stagger = 0.18;
// How far out an element gets, as a share of the reach; the fastest get 1.30
// of it, so the plane has to hold that.
uniform float puff_run = 0.85;
uniform float puff_size = 0.20;
uniform float puff_grow = 1.60;
uniform float puff_ink = 0.90;

uniform vec3 soot_dark = vec3(0.225, 0.205, 0.192);
uniform vec3 soot_lit = vec3(0.680, 0.640, 0.596);

void fragment() {
    if (level <= 0.0) {
        ALBEDO = vec3(0.0);
        ALPHA = 0.0;
    } else {
        vec2 at = (UV - 0.5) * 2.0 * carpet;
        float dens = 0.0;
        float years = 0.0;
        vec2 lean = vec2(0.0);

        for (int k = 0; k < puffs; k++) {
            float fk = float(k);
            float born = puff_stagger * ember_hash(vec2(fk, 241.0));
            float a = (time - born) / max(puff_life, 1e-3);
            if (a > 0.0 && a < 1.0) {
                float th = 6.2831853 * ember_hash(vec2(fk, 242.0));
                vec2 dir = vec2(cos(th), sin(th));
                float fast = 0.45 + 0.85 * ember_hash(vec2(fk, 243.0));
                float out_at = puff_run * reach * fast * pow(a, 0.45);
                vec2 p = dir * out_at;
                float r = puff_size * (0.60 + 0.80 * ember_hash(vec2(fk, 244.0)))
                          * (0.45 + puff_grow * a);
                float gain = puff_ink * (1.0 - exp(-a / 0.050))
                             * pow(max(1.0 - a, 0.0), 0.95);
                // Stretched along its own travel: dust pushed out along the
                // ground lies along the push.
                dust_part(at, p, r, r * 1.50, dir, fk + 241.0,
                          gain, dust_soft, a, dens, lean, years);
            }
        }

        if (dens <= 1e-4) {
            ALBEDO = vec3(0.0);
            ALPHA = 0.0;
        } else {
            float mass = dust_mass(at, dens);
            float lit = dust_lit(mass, lean);
            ALBEDO = mix(soot_dark, soot_lit, lit);
            // Out at the plane's own rim rather than cut by it.
            float rim = 1.0 - smoothstep(carpet * 0.80, carpet, length(at));
            ALPHA = clamp(mass * dust_ink * level * rim, 0.0, 1.0);
        }
    }
}
";

    internal static readonly string GroundCode =
        GroundBallShader.Replace("FLAME_NOISE", Stage3D.EmberNoiseCode)
                        .Replace("FLAME_INK", Stage3D.FlameInk)
                        .Replace("BLAST_FRAME", ProcBlast.FrameCode)
                        .Replace("DUST_INK", ProcBlast.DustInk);

    private static readonly Shader Carpeting = new() { Code = GroundCode };

    /// <summary>The dust as it is compiled - the shared noise, ink, frame and
    /// dust, so a check can ask whether this is one text with the others.</summary>
    internal static readonly string DustCode =
        DustBallShader.Replace("FLAME_NOISE", Stage3D.EmberNoiseCode)
                      .Replace("FLAME_INK", Stage3D.FlameInk)
                      .Replace("BLAST_FRAME", ProcBlast.FrameCode)
                      .Replace("DUST_INK", ProcBlast.DustInk);

    internal static readonly string FireCode =
        FireBallShader.Replace("FLAME_NOISE", Stage3D.EmberNoiseCode)
                      .Replace("FLAME_INK", Stage3D.FlameInk)
                      .Replace("BLAST_FRAME", ProcBlast.FrameCode);

    private static readonly Shader Sooting = new() { Code = DustCode };
    private static readonly Shader Blazing = new() { Code = FireCode };
}
