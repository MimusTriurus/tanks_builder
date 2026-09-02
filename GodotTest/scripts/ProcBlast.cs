using System;
using System.Globalization;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// A high-explosive round going off in the ground: the flash and fireball at the
/// seat, the cone of earth thrown up out of it, and the dust that outlives both.
///
/// <b>Built the way the forest's fire is built, and that is a decision rather
/// than a habit.</b> C# owns the numbers, the clock and the size of the quad; the
/// fragment owns the section across one element and the stacking. The elements'
/// paths are ballistic, which - like a tree's straight climb and unlike a tank's
/// vent - has a closed form, so they are worked out per fragment and no array
/// crosses the boundary. See <c>ProcSmoke</c> for the case that has to be marched
/// in C# instead, and why: the split is about whether the path closes, not about
/// which language is nicer.
///
/// <b>Two quads, dust under fire, and the order is load-bearing.</b> The fire is
/// additive, so what is behind it decides how much of it arrives, and on this
/// pale ground almost none does. The tree's smoke is already written down as
/// existing for exactly that, in those words; a ground burst has more of it and
/// needs it more, because half of what makes a burst read is that the earth it
/// throws up is *dark*.
///
/// <b>The ink is the forest's and the tank's, not a third one.</b>
/// <see cref="Stage3D.FlameInk"/> is one statement of what an element is - its
/// ellipse, its section, its dark lip, its ramp - and the fire here is that
/// statement with a shorter life. The dust needs a colour the ink does not have,
/// so it adds <c>dust_blob</c> beside <c>flame_blob</c> and keeps the same
/// section underneath it: two copies of a cross-section would part at whichever
/// number nobody put side by side.
///
/// <b>Everything is quoted in tile widths.</b> The burst is a thing that happens
/// on a cell, so a cell is its unit - the same move by which <c>engine_fire</c>
/// quotes a plume against the hull and the tree's fire against its own height. A
/// board drawn at another zoom, or a hex atlas re-rendered at another size, moves
/// the burst with it and needs no number touched.
///
/// <b>An event, not a loop, and that is why there is no phase table and no
/// wrap.</b> The clock runs forward from the shot and goes to -1 when the last
/// dust is gone - <c>HitLoop</c>'s convention, and for its reason: a layer whose
/// clock is out draws nothing at all rather than resting on a first frame. The
/// flame's own machinery is the opposite (it closes a lap by construction) and
/// none of it applies.
///
/// <b>Time is a uniform, not <c>TIME</c>.</b> Two runs of the same flags have to
/// come out the same picture - <see cref="FrameClock.FixedStep"/> and
/// <see cref="SceneRoot.SettleForProof"/> exist for that, and every measurement
/// in this project is a difference between two shots.
/// </summary>
public sealed partial class ProcBlast : Node3D
{
    // --- the shape of it, in tile widths ------------------------------------

    /// <summary>How far up the tips of the cone get. Just over half a cell: a
    /// burst that reaches a whole one reads as a bomb, and one that reaches a
    /// quarter reads as a puff of dust.</summary>
    public float Reach = ReachDefault;

    /// <summary>
    /// The three numbers C# owns, as their own constants.
    ///
    /// <b>Named because something else has to be able to ask what they are.</b>
    /// The frame's uniforms are declared at zero on purpose - so a run that forgot
    /// to write one draws nothing instead of drawing something plausible - which
    /// means the shader text is no longer where a panel can read the opening value
    /// of <c>reach</c>. It reads this instead, and the field is initialised from
    /// the same constant, so the number still has one home.
    /// </summary>
    public const float ReachDefault = 0.30f;
    public const float LifeDefault = 2.40f;
    public const float RingReachDefault = 0.42f;

    /// <summary>
    /// Half the quad, across, and how far up it goes.
    ///
    /// <b>Measured off the drawn pixels rather than derived, and then cut.</b>
    /// Every fragment of this quad runs the whole burst - eighty-odd sections for
    /// the dust alone - so a quad with margin to spare is not free the way an
    /// oversized sprite sheet cell is free. Set generously at first, the burst
    /// used 0.62 of its width and 0.67 of its height at the widest frame of its
    /// life; these are those numbers plus a margin, which halves the area the
    /// fragment stage covers.
    ///
    /// The measurement is the pipeline's own <c>max_edge_alpha == 0</c> rule
    /// asked of a quad instead of a tile: shoot the burst against the same board
    /// not bursting, take the bounding box of what changed, and compare it with
    /// the quad. Anything reaching the edge is sliced off flat, and a burst
    /// missing its outermost clods looks like a burst with fewer clods rather
    /// than like a bug.
    ///
    /// <b>But the quad holds what the model can produce, not what it happens to
    /// draw</b> - which is a full tenth of a tile wider, and the difference is
    /// the reason the check is on the model as well as on the pixels. The
    /// outermost clod arrives at the rim already shrunk and faded to nothing, so
    /// a quad cut to the bounding box passes every screenshot and clips the day
    /// somebody lengthens a clod's life or softens its fade. The widest thing in
    /// here is not the cone at all: it is the collar, whose elements lie flat and
    /// so reach out by their own length rather than their own radius.
    ///
    /// <b>And the check earns its keep every time these numbers move.</b> Giving
    /// the column its longer life and its higher climb - the event's own tail -
    /// put it 0.592 of a tile up against a quad 0.520 tall, and the model check
    /// said so by name before any screenshot was taken. The quad follows the
    /// model; the model is not cut to fit the quad.
    /// </summary>
    public float Flank = 0.48f;
    public float Tall = 0.72f;

    /// <summary>
    /// How far out the rings on the ground get, in tile widths, and how much of
    /// their own plane they are allowed to reach.
    ///
    /// <b>Taken from the CC0 pack, which spends two emitters on this</b> - a
    /// flaring cone and a flat disc, both cylinders lying on the ground - and it
    /// is the one thing in that pack this board had nothing of. A burst on a hex
    /// grid has to say *which cell*, and a shape that spreads along the ground
    /// says it in one frame where a column of dust says it in none.
    /// </summary>
    public float RingReach = RingReachDefault;

    /// <summary>How far above the contact point the burst is seated, and the band
    /// it fades out over below that - both in tile widths, and both for the
    /// tree's reason: under this camera "below the contact point" and "behind the
    /// ground" are the same test, so anything reaching below the seat is not
    /// faded by the ground, it is sliced off flat by it.</summary>
    public float Root = 0.03f;
    public float Fade = 0.055f;

    // --- how long, in seconds ------------------------------------------------

    /// <summary>
    /// The whole event.
    ///
    /// <b>The dust sets it, and the ratio is the shape of the thing.</b> The
    /// flash is out in six hundredths, the fireball in a fifth, the earth is down
    /// in two thirds of a second and the column is still climbing at two - so the
    /// fire is a twentieth of the event and the rest is what it left behind. That
    /// spread is what the CC0 pack does with an AnimationPlayer of separate
    /// windows per layer, and it is the half of it worth having: at the 1.15s this
    /// was, with every family starting together, a burst read as one puff rather
    /// than as a thing that happened and then settled.
    /// </summary>
    public float Life = LifeDefault;

    private float _clock = -1.0f;

    /// <summary>Whether the burst is running. -1 is out, and out means the two
    /// quads are hidden rather than resting on a frame.</summary>
    public bool Alive => _clock >= 0.0f;

    /// <summary>How far in it is, in seconds - the number the shader is driven
    /// by, and the one a readout should print.</summary>
    public float Age => _clock;

    private MeshInstance3D? _dirt;
    private MeshInstance3D? _fire;
    private MeshInstance3D? _ring;
    private ShaderMaterial? _dirtInk;
    private ShaderMaterial? _fireInk;
    private ShaderMaterial? _ringInk;

    /// <summary>
    /// The quad the burst is drawn on, in screen pixels: from <c>-Foot</c> across
    /// <c>Size</c>, which is what <see cref="Stage3D.Stem"/> takes.
    ///
    /// Its bottom edge is the contact point exactly, so the seat sits on the
    /// ground line and <c>foot_v</c> is 1. Given as a static function of the tile
    /// rather than read off a built node, so how big a burst is can be asserted
    /// without a board under it - <see cref="Stage3D.PyreQuad"/>'s arrangement.
    /// </summary>
    public static (Vector2 Foot, Vector2 Size) Quad(float tile, float flank, float tall)
    {
        float half = flank * tile, up = tall * tile;
        return (new Vector2(half, up), new Vector2(half * 2.0f, up));
    }

    /// <summary>
    /// Build the pair of quads. <paramref name="tile"/> is the hex's own width in
    /// screen pixels - every length above is in those - and the two camera terms
    /// are the field's, handed in rather than read so this needs no board.
    /// </summary>
    public void Build(float tile, float squash, float rise)
    {
        (Vector2 foot, Vector2 size) = Quad(tile, Flank, Tall);
        ArrayMesh shape = Stage3D.Stem(foot, size, rise);

        _dirtInk = Ink(Dusting);
        _fireInk = Ink(Bursting);
        _dirt = Slab(shape, _dirtInk);
        _fire = Slab(shape, _fireInk);

        // Toward the camera by the board's own clearance, the fire by twice it -
        // the arrangement the burning tree uses for the same pair, so the dark
        // half is behind the additive one rather than sorting against it by the
        // coin toss two coplanar quads get.
        Vector3 nudge = _nudge = Stage3D.Clear(squash, rise);
        _dirt.Position = nudge;
        _fire.Position = nudge * 2.0f;

        // <b>The rings get a plane of their own, lying on the ground.</b> They
        // cannot go on the upright quad with everything else, and the reason is
        // the rule that quad is written under: below the contact point and behind
        // the ground are the same test under this camera, so anything drawn there
        // is sliced off flat. A ring spreads *toward* the camera as well as away
        // from it, and the half of it that comes forward is exactly the half that
        // would be cut. The pack has the same answer for the same reason - its two
        // impact emitters draw cylinder meshes lying flat.
        //
        // A PlaneMesh is already in XZ, and this node's frame is the board's, so
        // the plane lands on the ground with no rotation of its own. Lifted by the
        // board's own clearance so it does not fight the cell's top face.
        _ringInk = new ShaderMaterial { Shader = Ringing, RenderPriority = Stage3D.StandOrder };
        _ringInk.SetShaderParameter("level", 1.0f);
        _ringInk.SetShaderParameter("time", 0.0f);
        _ring = new MeshInstance3D
        {
            Mesh = new PlaneMesh
            {
                Size = new Vector2(2.0f * RingReach * tile, 2.0f * RingReach * tile),
            },
            SortingUseAabbCenter = false,
            MaterialOverride = _ringInk,
            Visible = false,
            Position = nudge * 0.5f,
        };
        AddChild(_ring);
    }

    /// <summary>
    /// How big this particular burst is, as a multiple of the tuned one.
    ///
    /// <b>The one number the panel did not have, and the one a game asks for
    /// first.</b> Every length in here is already in tile widths - how far the
    /// puffs get, how wide one is, how high the column goes, where it is seated -
    /// so a 152mm round is not a different effect, it is this effect at 1.4. Spread
    /// across four dials that was four numbers to keep in step per calibre; as one
    /// it is what <see cref="Ordnance"/> already has a list of.
    ///
    /// <b>A scale on the node rather than on the four numbers, and that is not
    /// laziness.</b> The model's lengths are only half of what has to grow: the
    /// depth nudge, the ring's own plane, the stretch, the seat all follow from
    /// them, and a scale on the transform gets every one of those for free and
    /// cannot get one of them wrong. It scales <b>about the seat</b> - the origin is
    /// left where <see cref="Stage3D.Trunk"/> put it - because the seat is the point
    /// the crater is dug at, and a burst that grew about its own middle would drift
    /// off its own mark. That is the coordinate-space failure this project has
    /// already paid for once, so there is a check on it.
    /// </summary>
    public float Might
    {
        get => _might;
        set
        {
            _might = Mathf.Max(value, 0.01f);
            Stand();
        }
    }

    private float _might = 1.0f;
    private Transform3D _seat = Transform3D.Identity;

    /// <summary>Where the burst stands: the flat point it went off on and the
    /// lift of the cell under it, through the same transform a tree gets.
    /// </summary>
    public void Sit(Vector2 ground, float lift, float squash, float rise)
    {
        _seat = Stage3D.Trunk(ground, lift, 0.0f, squash, rise);
        Stand();
    }

    /// <summary>The seat and the size together - <see cref="SheetBlast"/>'s
    /// arrangement, and the three quads' own nudges are divided back out for its
    /// reason: a clearance is a clearance whatever size the burst is.</summary>
    private void Stand()
    {
        Transform = _seat.ScaledLocal(Vector3.One * _might);
        if (_dirt is not null)
            _dirt.Position = _nudge / _might;
        if (_fire is not null)
            _fire.Position = _nudge * 2.0f / _might;
        if (_ring is not null)
            _ring.Position = _nudge * 0.5f / _might;
    }

    private Vector3 _nudge;

    /// <summary>Set it off. Restarts rather than refusing, because the organ that
    /// fires it is a key on a bench and a key that answers once every one and a
    /// third seconds reads as a key that is half broken.</summary>
    public void Fire() => _clock = 0.0f;

    /// <summary>
    /// Whether the clock stands still where it is.
    ///
    /// <b>The one control a burst needs that a loop does not.</b> An event is
    /// judged on a particular moment of itself - the frame where the earth is at
    /// its apex, the frame where the ring passes the rim - and at sixty frames a
    /// second those moments are gone before a slider can be dragged. Held, the
    /// same moment can be looked at while the numbers move under it. The CC0 pack
    /// spends a whole uniform on this (<c>still_frame</c>) and calls it a
    /// debugging aid; here it is the main way the thing gets looked at.
    /// </summary>
    public bool Hold;

    /// <summary>Where the clock is, in seconds, settable - so a held burst can be
    /// scrubbed to the moment being judged.</summary>
    public float Clock
    {
        get => _clock;
        set => _clock = Mathf.Clamp(value, 0.0f, Life);
    }

    /// <summary>
    /// One number of one of the three shaders, live.
    ///
    /// <b>Read out of the shader's own text the first time it is asked for, and
    /// never kept twice.</b> The uniforms are where the numbers live - tuning them
    /// is what a sweep does - so a panel that carried its own copy of a default
    /// would agree with the shader until the first edit landed in one of them.
    /// This asks the compiled source, which is the same thing the self-test does
    /// for the same reason.
    /// </summary>
    public float Dial(Part part, string uniform)
    {
        string key = part + "." + uniform;
        if (!_live.TryGetValue(key, out float now))
        {
            now = Uniform(Source(part), uniform);
            _live[key] = now;
        }
        return now;
    }

    /// <summary>The same number, written - to the material that owns it, and to
    /// both of them when the frame is what owns it.</summary>
    public void Dial(Part part, string uniform, float value)
    {
        _live[part + "." + uniform] = value;
        // An int uniform written with a float is not the number that was asked
        // for - see Uniform on the same trap from the reading side.
        bool counted = Source(part).Contains("uniform int " + uniform + " = ",
                                             StringComparison.Ordinal);
        Variant sent = counted ? Mathf.RoundToInt(value) : value;
        Coat(part)?.SetShaderParameter(uniform, sent);
        // The frame's own numbers (reach, spread, the seat) are declared in the
        // text both the dust and the fire are built from, so writing one of them
        // to a single material would leave the halves disagreeing about how far
        // the burst throws.
        if (part == Part.Frame)
        {
            _dirtInk?.SetShaderParameter(uniform, sent);
            _fireInk?.SetShaderParameter(uniform, sent);
        }
    }

    /// <summary>Which of the three a number belongs to. <see cref="Part.Frame"/>
    /// is the text the dust and the fire share.</summary>
    public enum Part { Frame, Dust, Fire, Ring }

    private readonly System.Collections.Generic.Dictionary<string, float> _live = new();

    private static string Source(Part part) => part switch
    {
        Part.Dust => DustCode,
        Part.Fire => FireCode,
        Part.Ring => RingCode,
        _ => BlastFrame,
    };

    private ShaderMaterial? Coat(Part part) => part switch
    {
        Part.Dust => _dirtInk,
        Part.Fire => _fireInk,
        Part.Ring => _ringInk,
        _ => null,
    };

    /// <summary>How wide the ring's own plane is, live: the mesh, not a uniform,
    /// because the rings are drawn in its UV and the plane is what carries them
    /// into tile widths.</summary>
    public void Replane(float tile)
    {
        if (_ring?.Mesh is PlaneMesh flat)
            flat.Size = new Vector2(2.0f * RingReach * tile, 2.0f * RingReach * tile);
    }

    /// <summary>Put it out with nothing drawn - what a reset wants.</summary>
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
        if (_dirt is not null)
            _dirt.Visible = on;
        if (_fire is not null)
            _fire.Visible = on;
        if (_ring is not null)
            _ring.Visible = on;
        if (!on)
            return;

        _dirtInk?.SetShaderParameter("time", _clock);
        _fireInk?.SetShaderParameter("time", _clock);
        _ringInk?.SetShaderParameter("time", _clock);
    }

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

    private ShaderMaterial Ink(Shader how)
    {
        var ink = new ShaderMaterial { Shader = how, RenderPriority = Stage3D.StandOrder };
        ink.SetShaderParameter("foot_v", 1.0f);
        ink.SetShaderParameter("tall", Tall);
        ink.SetShaderParameter("wideq", Flank * 2.0f);
        ink.SetShaderParameter("root", Root);
        ink.SetShaderParameter("floor_fade", Fade);
        ink.SetShaderParameter("reach", Reach);
        ink.SetShaderParameter("level", 1.0f);
        ink.SetShaderParameter("time", 0.0f);
        ink.SetShaderParameter("sun", Stage3D.Sun);
        return ink;
    }

    /// <summary>What both halves of the burst are told about the quad they are
    /// drawn on, and where the burst sits in it. Shared text rather than a copy
    /// per shader, for <see cref="Stage3D.FlameInk"/>'s reason at a smaller
    /// scale: a dust cone and the fire in the middle of it, seated at two
    /// different heights, are two effects that happen to be in the same
    /// place.</summary>
    internal static string FrameCode => BlastFrame;

    private const string BlastFrame = @"
// <b>The four numbers C# owns are declared at zero, and that is on purpose.</b>
// Each of them is written by ProcBlast.Ink every time a material is made - the
// quad's own size has to be known on both sides of the boundary, because C# is
// what builds the mesh - so a default here is a second declaration of the same
// fact, and the second one wins on any run that forgets to write it. Zero cannot
// be mistaken for a burst: nothing is thrown, nothing is drawn, and the omission
// is loud. Read the other way round, the shader's default silently drew a burst
// of a different size from the quad that was built for it, and it took the check
// on the model to notice.
//
// The quad in tile widths, and where the contact point sits down it.
uniform float foot_v = 0.0;
uniform float tall = 0.0;
uniform float wideq = 0.0;
// The seat above the contact point, and the band the burst fades out over below
// it. There is no drawing under the contact point at all - see the C# note.
uniform float root = 0.03;
uniform float floor_fade = 0.055;
// How high the burst throws, in tile widths: the one size in the whole thing, and
// every other length here is a ratio to it. ProcBlast.Reach is where it lives -
// see above on why this is zero.
uniform float reach = 0.0;
// Seconds since the round went off, and whether anything is drawn at all.
uniform float time = 0.0;
uniform float level = 0.0;
uniform vec3 sun = vec3(-0.40, 0.82, -0.41);

// How wide the burst throws, as a multiple of how high it throws. Here rather
// than with the earth because the sparks are thrown by the same explosion: two
// numbers for one throw would part at whichever of them nobody touched.
uniform float spread = 1.15;

// <b>Where something thrown out of this burst is, and which way it is going.</b>
// Stated as an apex rather than as a launch speed and a gravity: written the
// second way - which is how this started - the two numbers fight, the fall takes
// most of the throw back, and the cone comes out a low heap of earth sitting in a
// hole however far the speed is pushed. Here the parabola is stated by where it
// is going: up by <c>run * fast</c> at the middle of the element's life, back on
// the ground exactly at the end of it, and out sideways all the while.
//
// <c>a</c> is the element's own age in 0..1, <c>lean</c> its launch angle off
// straight up and <c>side</c> which way it went. Returns the offset in xy and the
// direction of travel in zw, so the element can be stretched along its own
// velocity - a clod at the top of its arc lying vertically is the single thing
// that makes a cone read as thrown earth rather than as a fan.
vec4 blast_throw(float a, float run, float fast, float lean, float side) {
    float up = run * 4.0 * fast * a * (1.0 - a);
    float out_at = sin(lean) * side * run * spread * fast * a;
    vec2 vel = vec2(sin(lean) * side * run * spread * fast,
                    run * 4.0 * fast * (1.0 - 2.0 * a));
    vec2 flow = length(vel) < 1e-4 ? vec2(0.0, 1.0) : normalize(vel);
    return vec4(out_at, up, flow.x, flow.y);
}

// The fragment in tile widths from the seat: x across from the middle, y up.
// Takes the UV rather than reading it: a built-in is a local of the function it
// is declared in, and reaching for UV inside a helper fails to compile.
vec2 blast_at(vec2 uv) {
    return vec2((uv.x - 0.5) * wideq, (foot_v - uv.y) * tall - root);
}

// Out before the ground line rather than at it - the band is above the line
// because there is no below.
float blast_footing(vec2 at) {
    return smoothstep(0.0, max(floor_fade, 1e-4), at.y + root);
}
";

    /// <summary>
    /// The earth: a cone of clods thrown up out of the seat, a dome spreading
    /// along the ground and a dirty column climbing out of it.
    ///
    /// <b>Ballistic, in closed form.</b> An element leaves the seat along its own
    /// launch direction and falls back under one constant, so where it is and
    /// which way it is travelling are both algebra in its age - which is what
    /// lets the stretch lie along the velocity instead of along the quad. A clod
    /// at the top of its arc lying vertically is the single thing that makes a
    /// cone read as thrown earth rather than as a fan.
    ///
    /// <b>Dark, and lit off the board's own sun.</b> The section already computes
    /// a facing, and a facing is a normal: the same number that shades an element
    /// from the middle out puts the light on the side the sun is on. That is what
    /// the reference project spends a signed normal map on, and here it is free.
    /// A flat grey cone is the failure this is against - it reads as a smudge on
    /// the ground rather than as earth in the air.
    ///
    /// <b>Ordinary alpha, and premultiplied inside then divided back out.</b>
    /// Elements composite over each other with alpha - the flame's own finding:
    /// summed instead, an element's dark lip *brightens* the one behind it and the
    /// cone melts into one bar - and the quad is handed to blend_mix, which wants
    /// straight alpha. So the accumulation is premultiplied and the colour is
    /// divided by the coverage on the way out.
    /// </summary>
    private const string DustShader = @"
shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_never;

FLAME_NOISE

FLAME_INK

BLAST_FRAME

DUST_INK

// The cone: how many, how long each lives, how wide it opens and how hard it
// falls back. The lean is measured from straight up, so 0.20 is a tight fountain
// and 1.10 is a fan lying on the ground.
uniform int clods = 44;
uniform float clod_life = 0.62;
uniform float clod_stagger = 0.05;
uniform float clod_narrow = 0.16;
uniform float clod_wide = 0.90;
uniform float clod_size = 0.026;
uniform float clod_stretch = 1.35;
uniform float clod_ink = 1.10;
// How far off the middle a clod starts. The tree's licks have the same number for
// the same reason, in its own words: every element leaving through the exact
// centre makes the base of the column a needle - and here it made the whole cone
// a symmetrical flower, twice, before this was added.
uniform float clod_born = 0.075;

// The collar: dust pushed out along the ground, and it is the half of a burst
// that says where on the board it happened. Its elements lie flat - wide across
// and shallow up - because under this camera a ring on the ground is an ellipse,
// and circles here read as a ball sitting in a hole.
// Settles sooner than it used to, and that is about the middle of the event
// rather than about the collar: at 0.95s it was still the widest thing on the
// board at half a second, and a burst whose middle phase is a flat dark slab
// reads as a hedge. Out by 0.70 it hands the middle over to the column, which is
// what the long tail is for.
uniform int collar = 24;
uniform float collar_life = 0.70;
uniform float collar_born = 0.05;
uniform float collar_stagger = 0.12;
uniform float collar_spread = 0.32;
uniform float collar_climb = 0.08;
uniform float collar_size = 0.038;
uniform float collar_flat = 2.30;
uniform float collar_ink = 0.78;

// The column: what is still there when the earth has come down, and the thing
// that gives the event its length - it is alive for eight times as long as the
// fire is. Thin, climbing, thickening as it goes.
uniform int column = 16;
// 1.85 and not 2.10, and the check is what said so: born at 0.14, staggered by up
// to 0.40 and living 2.10 puts the last puff's end at 2.64s against a clock of
// 2.40 - so the tail of the event was being cut off in mid-air rather than
// finishing. The clock is the declared length of the thing; the families fit
// inside it.
uniform float column_life = 1.85;
uniform float column_born = 0.14;
uniform float column_stagger = 0.40;
uniform float column_sway = 0.15;
uniform float column_climb = 0.58;
uniform float column_size = 0.050;
uniform float column_ink = 1.00;

// What earth in the air is, at its darkest and fully lit. Warm rather than grey,
// for the tree's smoke's reason: a neutral column on this ground reads as fog -
// and nothing like as dark as soot, because this is the ground itself in the air
// with the sun on it. Read too dark first, and the burst was a black smear.
uniform vec3 dirt_dark = vec3(0.115, 0.082, 0.055);
uniform vec3 dirt_lit = vec3(0.790, 0.650, 0.430);
// <b>The profile, the tearing, the packing and the light are the shared dust
// ink's</b> - ProcBlast.DustInk, substituted in above and shared with ProcKick.
// What stays here is what is this burst's own: the colour of earth in the air,
// and the crisper profile a clod keeps because a clod of earth *is* a small body
// - but nowhere near a sphere's.
uniform float clod_soft = 1.85;

void fragment() {
    if (level <= 0.0) {
        ALBEDO = vec3(0.0);
        ALPHA = 0.0;
    } else {
        vec2 at = blast_at(UV);

        // <b>One pass that adds up earth, and one that shades the result.</b>
        // Compositing each element over the last - which is what this did, and
        // what the flame rightly does - gives every one of them its own dark lip
        // and its own lit ridge, and the eye then counts them: the first version
        // of this read as a pile of potatoes, in those words. A flame wants that,
        // because a lick is a thing; a cloud is not a set of things, it is one
        // volume of varying thickness. So the elements are summed into a density
        // and a single surface is lit at the end.
        //
        // What is kept is where the density comes from: ballistic clods, a collar
        // along the ground and a column climbing out of it, each with its own
        // life. The internal structure survives as thickness, which is what makes
        // the silhouette ragged instead of round.
        float dens = 0.0;
        vec2 lean = vec2(0.0);
        float years = 0.0;

        for (int k = 0; k < column; k++) {
            float fk = float(k);
            float born = column_born + column_stagger * ember_hash(vec2(fk, 51.0));
            float a = (time - born) / max(column_life, 1e-3);
            if (a > 0.0 && a < 1.0) {
                float sway = column_sway * (2.0 * ember_hash(vec2(fk, 52.0)) - 1.0)
                             * pow(a, 0.75);
                float up = column_climb * pow(a, 0.70)
                           * (0.30 + 0.95 * ember_hash(vec2(fk, 53.0)));
                float r = column_size * (0.55 + 0.9 * ember_hash(vec2(fk, 54.0)))
                          * (0.45 + 1.05 * a);
                float gain = column_ink * (1.0 - exp(-a / 0.10))
                             * pow(max(1.0 - a, 0.0), 1.25);
                dust_part(at, vec2(sway, up), r, r * 1.25, vec2(0.0, 1.0),
                          fk + 51.0, gain, dust_soft, a, dens, lean, years);
            }
        }

        for (int k = 0; k < collar; k++) {
            float fk = float(k);
            float born = collar_born + collar_stagger * ember_hash(vec2(fk, 61.0));
            float a = (time - born) / max(collar_life, 1e-3);
            if (a > 0.0 && a < 1.0) {
                // Out along the ground and up, the second far slower than the
                // first: a burst spreads before it climbs, and a cloud doing both
                // at one rate is a ball.
                float side = 2.0 * ember_hash(vec2(fk, 62.0)) - 1.0;
                float out_at = collar_spread * side * pow(a, 0.55);
                float up = collar_climb * pow(a, 0.85)
                           * (0.35 + 0.85 * ember_hash(vec2(fk, 63.0)));
                float r = collar_size * (0.55 + 0.9 * ember_hash(vec2(fk, 64.0)))
                          * (0.45 + 1.10 * a);
                float gain = collar_ink * (1.0 - exp(-a / 0.06))
                             * pow(max(1.0 - a, 0.0), 1.30);
                dust_part(at, vec2(out_at, up), r, r * collar_flat,
                          vec2(1.0, 0.0), fk + 61.0, gain, dust_soft, a,
                          dens, lean, years);
            }
        }

        for (int k = 0; k < clods; k++) {
            float fk = float(k);
            float born = clod_stagger * ember_hash(vec2(fk, 71.0));
            float a = (time - born) / max(clod_life, 1e-3);
            if (a > 0.0 && a < 1.0) {
                float leaning = mix(clod_narrow, clod_wide,
                                    ember_hash(vec2(fk, 72.0)));
                float side = ember_hash(vec2(fk, 73.0)) < 0.5 ? -1.0 : 1.0;
                float fast = mix(0.45, 1.0, ember_hash(vec2(fk, 74.0)));
                vec2 seat = vec2(clod_born * (2.0 * ember_hash(vec2(fk, 76.0)) - 1.0),
                                 clod_born * 0.45 * ember_hash(vec2(fk, 77.0)));
                vec4 thrown = blast_throw(a, reach, fast, leaning, side);
                vec2 p = seat + thrown.xy;
                vec2 flow = thrown.zw;
                float r = clod_size * (0.40 + 1.7 * ember_hash(vec2(fk, 75.0)))
                          * (1.0 - 0.25 * a);
                // Shrinks and thins out together, and neither reaches zero early:
                // earth stops being visible because it has landed, not because it
                // evaporated.
                float gain = clod_ink * (1.0 - exp(-a / 0.05))
                             * (1.0 - smoothstep(0.60, 1.0, a));
                dust_part(at, p, r, r * (1.0 + clod_stretch * fast), flow,
                          fk + 71.0, gain, clod_soft, a, dens, lean, years);
            }
        }

        if (dens <= 1e-4) {
            ALBEDO = vec3(0.0);
            ALPHA = 0.0;
        } else {
            // One surface, lit once, out of the summed thickness - both halves
            // are the shared dust ink's, and the two colours are this burst's.
            float mass = dust_mass(at, dens);
            float lit = dust_lit(mass, lean);
            ALBEDO = mix(dirt_dark, dirt_lit, lit);
            ALPHA = clamp(mass * dust_ink * level * blast_footing(at), 0.0, 1.0);
        }
    }
}
";

    /// <summary>
    /// The fire: a flash at the seat, a short fireball over it, and sparks going
    /// out with the earth.
    ///
    /// <b>Three windows, all of them short.</b> The flash is out in a twelfth of
    /// a second and the ball in a third, against the dust's one and a third. That
    /// ratio is most of what separates a shell from a fuel fire: the same
    /// elements held twice as long read as something burning rather than
    /// something detonating.
    ///
    /// <b>The ramp is the ink's, walked from its hot end.</b> No stop in it is
    /// near white, which is the rule the forest's fire is written under - red
    /// saturating is not the failure, green and blue coming up with it is - so the
    /// white heart of a burst is bought with brightness and a small tight element,
    /// not with a whiter colour.
    /// </summary>
    private const string FireShader = @"
shader_type spatial;
render_mode unshaded, cull_disabled, blend_add, depth_draw_never;

FLAME_NOISE

FLAME_INK

BLAST_FRAME

// The flash: one squat body at the seat, brightest thing in the burst and gone
// before the earth has cleared the ground.
uniform float flash_life = 0.060;
uniform float flash_size = 0.055;
// Past one, and deliberately: the ink has no stop near white, because red
// saturating is not the failure and green coming up with it is. A white heart is
// bought by adding an orange body to itself until it clips - brightness, not a
// whiter colour - which is what this number does and why it is past one.
uniform float flash_gain = 1.90;

// The ball: a handful of bodies climbing a little out of the seat.
uniform int balls = 7;
uniform float ball_life = 0.185;
uniform float ball_stagger = 0.04;
uniform float ball_size = 0.036;
uniform float ball_climb = 0.11;
uniform float ball_spread = 0.075;
uniform float ball_gain = 0.95;
uniform float ball_stretch = 1.35;

// The sparks: out along the cone with the earth, and further than the ball.
// Small and long - a spark is a streak, and at the size these were first given
// they were petals on a flower.
uniform int sparks = 9;
uniform float spark_life = 0.30;
uniform float spark_size = 0.0090;
uniform float spark_stretch = 3.40;
uniform float spark_gain = 0.45;
uniform float spark_lean = 0.80;
uniform float spark_stagger = 0.045;
uniform float spark_reach = 1.25;

void fragment() {
    if (level <= 0.0) {
        ALBEDO = vec3(0.0);
        ALPHA = 0.0;
    } else {
        vec2 at = blast_at(UV);
        vec4 fire = vec4(0.0);

        // The flash. Grows fast and dies faster; seated low, because the round
        // went off in the ground and not over it.
        float fa = time / max(flash_life, 1e-4);
        if (fa < 1.0) {
            float r = flash_size * (0.45 + 0.75 * sqrt(fa));
            float gain = flash_gain * pow(max(1.0 - fa, 0.0), 1.30);
            fire = flame_over(fire,
                              flame_blob(at, vec2(0.0, r * 0.40), r, r * 0.70,
                                         vec2(0.0, 1.0), 2.0, 0.0, gain));
        }

        // The ball.
        for (int k = 0; k < balls; k++) {
            float fk = float(k);
            float born = ball_stagger * ember_hash(vec2(fk, 81.0));
            float a = (time - born) / max(ball_life, 1e-3);
            if (a > 0.0 && a < 1.0) {
                float side = 2.0 * ember_hash(vec2(fk, 82.0)) - 1.0;
                float off = ball_spread * side * pow(a, 0.70);
                float up = ball_climb * pow(a, 0.75)
                           * (0.30 + 0.95 * ember_hash(vec2(fk, 83.0)));
                float r = ball_size * (0.55 + 0.9 * ember_hash(vec2(fk, 84.0)))
                          * (1.0 - 0.35 * a);
                fire = flame_over(fire,
                                  flame_blob(at, vec2(off, up), r, r * ball_stretch,
                                             vec2(0.0, 1.0), fk + 81.0,
                                             // Each body its own place on the
                                             // ramp as well as its own age: read
                                             // at one stop the ball composites
                                             // into a flat orange disc, which is
                                             // what it was.
                                             clamp(a + 0.45 * ember_hash(vec2(fk, 85.0)),
                                                   0.0, 1.0),
                                             ball_gain * flame_life(a, 0.85)));
            }
        }

        // The sparks.
        for (int k = 0; k < sparks; k++) {
            float fk = float(k);
            // Its own birth and its own life, which is what the first pass got
            // wrong: one age shared by every spark puts them all at the same
            // distance from the seat on every frame, and a dozen equal spokes is
            // a flower rather than a shower.
            float born = spark_stagger * ember_hash(vec2(fk, 90.0));
            float mine = spark_life * (0.55 + 0.75 * ember_hash(vec2(fk, 94.0)));
            float a = (time - born) / max(mine, 1e-3);
            if (a > 0.0 && a < 1.0) {
                // Weighted toward straight up rather than spread evenly: an
                // even fan of leans is an even fan on screen, which is the
                // flower again.
                float tilt = ember_hash(vec2(fk, 91.0));
                float lean = spark_lean * pow(abs(2.0 * tilt - 1.0), 1.60)
                             * (tilt < 0.5 ? -1.0 : 1.0);
                float fast = mix(0.45, 1.0, ember_hash(vec2(fk, 92.0)));
                // The cone's own throw, reaching a little further - one
                // statement of how anything this burst throws travels.
                vec4 thrown = blast_throw(a, reach * spark_reach, fast,
                                          abs(lean), lean < 0.0 ? -1.0 : 1.0);
                vec2 p = thrown.xy;
                vec2 flow = thrown.zw;
                float r = spark_size * (0.6 + 0.9 * ember_hash(vec2(fk, 93.0)));
                fire = flame_over(fire,
                                  flame_spark(at, p, r, r * spark_stretch, flow,
                                              fk + 91.0,
                                              ember_colour,
                                              spark_gain * flame_life(a, 1.10)));
            }
        }

        // Premultiplied by the compositing above, so a solid alpha is handed
        // over: blend_add lays down colour times alpha and adds this layer
        // exactly once.
        ALBEDO = fire.rgb * (level * blast_footing(at));
        ALPHA = 1.0;
    }
}
";

    /// <summary>
    /// One of a shader's own uniform defaults, read out of its text.
    ///
    /// <b>Read rather than kept a second time in C#, and that is the point.</b>
    /// The tuning numbers belong in the shader, because tuning them is what a
    /// sweep does; but how big the burst is has to be checkable against how big
    /// its quad is, and a C# copy of <c>reach</c> would agree with the shader's
    /// until the first sweep landed in one of them. So the check reads the
    /// numbers the shader actually compiles with.
    ///
    /// Returns <paramref name="ifMissing"/> when the name is not there, so a
    /// renamed uniform fails the check that uses it rather than throwing inside
    /// the test run.
    /// </summary>
    internal static float Uniform(string code, string name, float ifMissing = float.NaN)
    {
        // <b>Counts as well as sizes.</b> Six of these are declared int - how many
        // clods, balls, sparks, puffs, rings - and read as floats only, they came
        // back NaN, which a panel then wrote into the shader and every loop it
        // governed ran zero times. The layer simply vanished, with no error
        // anywhere: exactly the shape of failure a missing name should not have.
        string kind = code.Contains("uniform int " + name + " = ",
                                    StringComparison.Ordinal)
            ? "uniform int " : "uniform float ";
        int at = code.IndexOf(kind + name + " = ", StringComparison.Ordinal);
        if (at < 0)
            return ifMissing;
        at += kind.Length + name.Length + " = ".Length;
        int end = code.IndexOf(';', at);
        return end < 0
               || !float.TryParse(code[at..end], NumberStyles.Float,
                                  CultureInfo.InvariantCulture, out float value)
            ? ifMissing : value;
    }

    /// <summary>
    /// The rings a burst pushes out along the ground: a bright disc under it at
    /// the instant of the shot, and two rings racing out of that and fading.
    ///
    /// <b>Its own plane and its own space.</b> Drawn in the plane's UV rather than
    /// in the quad's tile widths, so a ring is a circle here and the board's own
    /// projection squashes it into the ellipse the eye expects. Written on the
    /// upright quad instead it would have needed the camera's squash as a uniform
    /// and would still have lost its near half to the ground line.
    ///
    /// <b>Torn, not drawn with a compass.</b> A perfect annulus reads as a decal;
    /// the same noise the flame and the dust are broken up with, sampled round the
    /// ring, gives it a rim that varies - and the ring is where a burst is most
    /// likely to look like a sprite.
    ///
    /// Additive and premultiplied, for the fire's reason: this is light on the
    /// ground.
    /// </summary>
    private const string RingShader = @"
shader_type spatial;
render_mode unshaded, cull_disabled, blend_add, depth_draw_never;

FLAME_NOISE

FLAME_INK

uniform float time = 0.0;
uniform float level = 0.0;

// The rings: how many, how long each lasts, and how far apart they set off.
uniform int rings = 2;
uniform float ring_life = 0.34;
uniform float ring_stagger = 0.09;
// How wide the band is and how much of the plane a ring is allowed to reach -
// short of 1 so the band's outer half is never cut by the plane's own edge.
uniform float ring_wide = 0.115;
uniform float ring_far = 0.88;
// Dimmer than it wants to be, and that is the note worth keeping: read at the
// gain a ring looks right at on its own, two of them on pale ground came out as
// lava - a bright orange annulus is the single most sprite-like thing a burst can
// draw. It is light on the ground, so it has to be light the ground merely
// catches.
uniform float ring_gain = 0.42;
uniform float ring_tear = 0.78;

// The disc under the burst at the instant of the shot: what makes the ground look
// lit rather than merely marked.
uniform float disc_life = 0.11;
uniform float disc_wide = 0.42;
uniform float disc_gain = 0.85;

void fragment() {
    if (level <= 0.0) {
        ALBEDO = vec3(0.0);
        ALPHA = 1.0;
    } else {
        vec2 d = (UV - 0.5) * 2.0;
        float r = length(d);
        vec3 lit = vec3(0.0);

        // The disc, first and briefest.
        float da = time / max(disc_life, 1e-4);
        if (da < 1.0) {
            float edge = disc_wide * (0.55 + 0.75 * da);
            float body = 1.0 - smoothstep(edge * 0.35, edge, r);
            lit += flame_ramp(0.04 + 0.30 * da).rgb * body
                   * (disc_gain * pow(max(1.0 - da, 0.0), 1.60));
        }

        // The rings.
        float turn = atan(d.y, d.x);
        for (int k = 0; k < rings; k++) {
            float fk = float(k);
            float born = ring_stagger * fk;
            float a = (time - born) / max(ring_life, 1e-3);
            if (a > 0.0 && a < 1.0) {
                float far = ring_far * pow(a, 0.55);
                float wide = ring_wide * (0.55 + 0.9 * a);
                float band = 1.0 - smoothstep(0.0, wide, abs(r - far));
                // Torn round its own circumference, and the noise is walked along
                // the ring rather than across the plane so the tear travels with
                // it instead of sitting still on the ground.
                float torn = 1.0 - ring_tear
                             + ring_tear * ember_fbm(vec2(turn * 2.4 + fk * 7.7,
                                                          far * 3.1));
                lit += flame_ramp(0.10 + 0.62 * a).rgb * band * torn
                       * (ring_gain * pow(max(1.0 - a, 0.0), 1.90));
            }
        }

        ALBEDO = lit * level;
        ALPHA = 1.0;
    }
}
";

    /// <summary>The dust as it is compiled - the shared noise, the shared ink and
    /// the shared frame, put together in one place so a check can ask whether the
    /// burst and the forest's fire are one text.</summary>
    /// <summary>
    /// What a cloud of earth is made of - the profile, the tearing, the packing
    /// and the light on the finished mass - as one text, shared with
    /// <see cref="ProcKick"/>.
    ///
    /// <b><see cref="Stage3D.FlameInk"/>'s move at a smaller scale, and for its
    /// reason.</b> A shell's earth and a gun's blown dust are the same dust; what
    /// differs is the colour and which elements there are, and those stay with
    /// each shader. Eleven tuned numbers written out twice would agree until the
    /// first sweep landed in one of them.
    ///
    /// Substituted after <see cref="BlastFrame"/> and after the ink, because it
    /// reads <c>time</c> and <c>sun</c> from the first and <c>flame_reach</c> from
    /// the second.
    /// </summary>
    internal const string DustInk = @"
// <b>What a cloud of earth is made of, shared by everything on this board that
// raises one.</b> <see cref=""Stage3D.FlameInk""/> at a smaller scale and for
// exactly its reason: the dust a shell throws up and the dust a gun blows off the
// ground are the same dust - the same profile, the same tearing, the same
// packing, the same light - and eleven tuned numbers written twice part company
// at whichever of them nobody put side by side.
//
// What is *not* in here is what differs: the colour, and which elements there
// are. A burst's earth is dark and warm and thrown up out of a seat; a muzzle's
// is pale at first and blown along the bore. Those are the two shaders' own.

// <b>How soft an element is, as the exponent of its own profile</b>, and this is
// the number the whole look turns on.
//
// The flame's section is a sphere's - <c>sqrt(1 - q)</c>, an exponent of 0.5 - and
// that is exactly right for a lick, which is a body: bright in the middle, thin
// and dark at a rim the eye can find. It is exactly wrong for dust, because a
// sphere's profile has *infinite slope* at that rim: alpha climbs off zero inside
// a pixel or two, and no amount of softening anywhere else can undo an edge that
// steep. That was the hard edge.
//
// <b>2.55 is measured, not chosen.</b> The CC0 pack's painted puff - the thing
// whose smoke reads soft - fits <c>(1 - q)^2.57</c> across all sixty-four of its
// cells, and its alpha peaks at 0.35 and reaches 0.95 nowhere in the sprite at
// all: every pixel of it is in transition. Five times the flame's exponent, and
// that ratio is the difference between a stone and a cloud.
//
// A body of earth small enough to read as a body - a clod - keeps a crisper one,
// and that is the shader's own number rather than this block's.
uniform float dust_soft = 2.55;
uniform float dust_seat = 0.52;
uniform float dust_ridge = 0.78;
uniform float dust_dense = 0.28;
// How fast piled-up elements stop adding to the mass. The whole reason the
// silhouette is one shape - see the note on fragment().
uniform float dust_pack = 3.00;
// How many steps the light is quantised to, and 0 for none. Taken from the CC0
// pack's stylised smoke, whose own value is three: banding a cloud's shading
// reads as a drawn cloud rather than as an airbrushed one, and it is the cheapest
// thing in this file.
uniform float dust_steps = 3.0;
// How much of the stepping is actually taken. <b>Not all of it, and that is the
// second half of the hard-edge finding.</b> The pack quantises the light on smoke
// that is never denser than 0.35 alpha, so its bands read as shading; taken whole
// on a mass that saturates to one they read as slabs, and the line between two
// slabs is a hard edge however soft the alpha under it is. Half-taken, the banding
// is visible as shading and the boundary is not a line.
uniform float dust_band = 0.55;
// The most of the ground a cloud may hide. Earth in the air is not a wall, and a
// mass that saturates to one is: at 1.0 the burst read as a hole in the board.
uniform float dust_ink = 0.72;
// <b>How the silhouette is torn.</b> Soft is not the same as ragged: with the
// profile alone the cloud is a smooth blob with a gentle edge, which reads as
// airbrush. The pack gets its raggedness from a painted sprite; here the summed
// density is eaten by a noise field, which is what the tree's own smoke does with
// its <c>torn</c> term. Drifting, because a static field is a stencil the cloud
// slides under.
uniform float dust_tear = 0.38;
uniform float dust_grain = 13.0;
uniform float dust_drift = 0.10;
// A toe on the way out, so thin dust fades over a longer distance than a dense
// core does. The pack spends a whole ramp on this idea - soft_blend_ramp, which
// widens the depth fade for its wispiest pixels - and on a board with no depth
// fade to widen it lands here instead.
uniform float dust_toe = 1.20;

// <b>What an element contributes, rather than what it looks like.</b> This is the
// change that took the burst from a heap of ellipses to a cloud: an element hands
// back how much earth it puts in the way and which way its own surface leans, and
// nothing else. It has no colour, no rim and no light of its own, because those
// are what made forty-four of them read as forty-four things.
//
// <c>w</c> is its coverage, <c>lean</c> its outward direction weighted by that
// coverage - summed over the mass it points where the cloud thins, which is the
// only normal a cloud has.
void dust_part(vec2 at, vec2 centre, float half_w, float along, vec2 flow,
               float lump_seed, float gain, float soft, float age,
               inout float dens, inout vec2 lean, inout float years) {
    float down;
    // The shared frame, and this element's own profile on it - see flame_reach.
    float q = flame_reach(at, centre, half_w, along, flow, lump_seed, down);
    if (q >= 1.0) {
        return;
    }
    float w = pow(1.0 - q, soft) * gain;
    dens += w;
    lean += normalize(at - centre + vec2(1e-5, 1e-5)) * w;
    years += w * age;
}

// <b>Saturating, so a pile-up stops reading as a pile.</b> Overlaps add thickness
// and thickness runs out of effect, which is what a volume does - and it is what
// keeps twenty elements in one place from being twenty times as opaque as one.
//
// Torn first, so the noise eats the density rather than the picture: a field
// applied to the finished alpha is a stencil lying on the screen, while one
// applied to the thickness is dust that is genuinely thinner in places.
float dust_mass(vec2 at, float dens) {
    float torn = 1.0 - dust_tear
                 + dust_tear * ember_fbm(at * dust_grain
                                         + vec2(0.0, -time * dust_drift
                                                      * dust_grain));
    float mass = 1.0 - exp(-dens * torn * dust_pack);
    // And a toe: thin dust leaves over a longer distance than thick dust.
    return pow(mass, dust_toe);
}

// Where on its own ramp the finished cloud sits: one surface lit once, off the
// sum of the elements' leans rather than per element - see dust_part.
//
// The ridge lives at the edge, where the mass is thin, and the body darkens as it
// thickens.
//
// <b>Seated high enough that three steps land on three bands.</b> The quantiser
// floors, so a light term that sits at 0.16 - which is what a seat of 0.46 minus
// a dense body gives - floors to nothing and the whole cloud comes out at its
// darkest stop. Measured: near-black. Seated at 0.52 the three bands land where
// they are wanted: the dense core away from the sun floors to the darkest stop,
// the body lands mid and the thin sunward edge tops out. Seated at 0.62 nothing
// reached the bottom band at all and the cloud was the same value as the ground
// it stood on - a pale smudge, which on this board is the other way to be
// invisible.
float dust_lit(float mass, vec2 lean) {
    // The cloud's own normal, out of the sum: where the mass thins.
    vec2 face = length(lean) < 1e-5 ? vec2(0.0, 1.0) : normalize(lean);
    float side = dot(face, normalize(sun.xy));
    float lit = clamp(dust_seat + dust_ridge * side * (1.0 - mass)
                      - dust_dense * mass, 0.0, 1.0);
    if (dust_steps > 1.5) {
        // The pack's own formula, over-ranging by design: it brightens the top
        // band, which is what stops three steps reading as three greys.
        float banded = clamp(floor(lit * dust_steps) / (dust_steps - 1.0),
                             0.0, 1.0);
        lit = mix(lit, banded, dust_band);
    }
    return lit;
}
";

    internal static readonly string DustCode =
        DustShader.Replace("FLAME_NOISE", Stage3D.EmberNoiseCode)
                  .Replace("FLAME_INK", Stage3D.FlameInk)
                  .Replace("BLAST_FRAME", BlastFrame)
                  .Replace("DUST_INK", DustInk);

    internal static readonly string FireCode =
        FireShader.Replace("FLAME_NOISE", Stage3D.EmberNoiseCode)
                  .Replace("FLAME_INK", Stage3D.FlameInk)
                  .Replace("BLAST_FRAME", BlastFrame);

    internal static readonly string RingCode =
        RingShader.Replace("FLAME_NOISE", Stage3D.EmberNoiseCode)
                  .Replace("FLAME_INK", Stage3D.FlameInk);

    private static readonly Shader Dusting = new() { Code = DustCode };
    private static readonly Shader Ringing = new() { Code = RingCode };
    private static readonly Shader Bursting = new() { Code = FireCode };
}
