using System;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// A tank's ammunition going off: the detonation that ends a hull rather than
/// the one that lands on it - <c>ammo_rack</c> in <c>docs/blast.md</c>.
///
/// <b>This is the first effect on this board whose job is a seam rather than a
/// picture, and the seam already existed and was already wrong.</b>
/// <see cref="TankTick.Kill"/> sets <c>Burning</c> on the frame a hull dies and
/// <see cref="Wreck.Blaze"/> answered one for the first ten seconds, so the
/// flame and the column arrived at full strength on that same frame - a lamp
/// switched on, which is the failure this project names in
/// <see cref="ProcSmoke"/>'s own words about conveyors. So the detonation owns
/// the first second and a half and the wreck's fire comes up behind it; see
/// <see cref="Wreck.RiseSeconds"/>, which is the whole of what changed over
/// there.
///
/// <b>The handover is a found alignment rather than an invented one.</b>
/// <see cref="Wreck.CharSeconds"/> is 1.4 and was already the length of the
/// aftermath - how long the paint takes to blacken - so the paint chars over
/// exactly the window the fireball dies in. One number, and it is the one that
/// was there.
///
/// <b>There is no column here, and that is the seam stated as a model.</b> A
/// short black head, yes - the ball has to leave something - but the long one
/// belongs to the wreck. Drawn here as well, the board would carry two columns
/// off one hull, one fading while the other grows, which is the double account
/// this project spends its docstrings refusing: see <c>stone_spray</c>, which is
/// nought for the same reason.
///
/// <b>It is <see cref="ProcBlast"/> stood on end.</b> A round in the ground
/// throws earth, dust dominates and everything spreads out along the ground;
/// here the hull confines the gases and they leave by the ways out - the ring,
/// the hatches, the deck grille - so fire dominates and it is <em>vertical</em>.
/// Same texts either way: <see cref="Stage3D.FlameInk"/> for what a lick is,
/// <see cref="ProcBlast.DustInk"/> for what a cloud is,
/// <see cref="ProcBlast.FrameCode"/> for the quad, and
/// <see cref="ProcBlast.RingCode"/> outright for the rings on the ground.
///
/// <b>There is no bearing in this model at all, and that is the difference from
/// every other burst on the board.</b> The ricochet leaves along a mirror, an HE
/// round goes off along the plate's normal, a kick follows the gun - each needs
/// to know where something was pointing. Gases venting out of a wrecked hull do
/// not care which way the hull was facing, so there is no <c>away</c> here, no
/// <c>squat</c>, and <see cref="Bounds"/> has nothing to sweep: the worst case is
/// the only case. <see cref="ProcSpall.SpallFrame"/> is deliberately not
/// included for that reason - three of its four numbers would be constants.
///
/// <b>And the seat costs nothing to measure, which is why this effect works on
/// every tank in the project including the single-mesh ones.</b>
/// <c>anchor_px</c> <em>is</em> the turret ring - hull, turret and tile share one
/// axis of rotation, and that sharing is the whole of what holds the layers
/// together - so where a rack goes off is the anchor plus the height of the deck.
/// No stamp in Blender, no <c>Barrel</c>, no <c>Engine</c>, and no
/// <see cref="Vehicle.Plated"/>: contrast <see cref="ProcSlam"/>, which needed a
/// measured plate and then needed it divided back out again.
///
/// <b>On the board rather than on the sprite's canvas, and the reason is new
/// rather than borrowed.</b> The ricochet and the plate burst both left section
/// A of <c>docs/blast.md</c> because they start on the outside of the armour with
/// nothing to cut them against; this one leaves for its height. The column goes
/// most of two tile widths up and the canvas is 512px with the anchor leaving
/// 0.4645 of a tile overhead - <c>engine_fire</c> has already lost 28px of its
/// column to exactly that.
/// </summary>
public sealed partial class ProcRack : Node3D
{
    /// <summary>
    /// How high it throws, in tile widths - <see cref="ProcBlast.Reach"/>'s
    /// number and its argument entire: every other length in here is a ratio to
    /// it, so a bigger rack is this rack at a bigger <see cref="Might"/> rather
    /// than a second set of numbers.
    ///
    /// <b>Twice the plate burst's, and that is the model rather than the
    /// taste.</b> A shell bursting on armour vents into the open air off one
    /// facet; a hull full of propellant vents through a hole the size of a turret
    /// ring, and what comes out is a column rather than a bloom.
    /// </summary>
    public float Reach = ReachDefault;

    /// <summary>The two numbers something else has to be able to ask for -
    /// <see cref="ProcBlast.ReachDefault"/>'s reason: a bench that wants to reset
    /// a dial needs the tuned value, and reading it off a live instance gets
    /// whatever the last shot left.</summary>
    public const float ReachDefault = 0.55f;

    /// <summary>
    /// How long the whole event runs.
    ///
    /// <b>Tied to the handover rather than to the picture.</b> The fire is out by
    /// 0.85s and the black head is gone by 1.4 - which is
    /// <see cref="Wreck.CharSeconds"/>, the length the paint takes to blacken -
    /// and the rest is the head thinning into nothing while the wreck's own
    /// column comes up under it. Ending sooner leaves a hole between the two;
    /// ending later draws the second column this class exists not to draw.
    /// </summary>
    public const float LifeDefault = 1.60f;

    /// <summary>
    /// The quad, in tile widths: half-width either side of the seat, and height
    /// above it.
    ///
    /// <b>The tallest quad on this board, and it has to be.</b> Every other burst
    /// is wider than it is high because it throws along the ground; this one is
    /// the other way round by a factor of two, and a quad cut to a burst's
    /// proportions would slice the column off flat. See <see cref="Bounds"/>,
    /// which is asked of the model and checked against these two.
    /// </summary>
    public float Flank = 0.72f;
    public float Tall = 1.85f;

    /// <summary>How far above the contact point the effect is seated, and the
    /// band it fades out over below it - <see cref="ProcBlast.Root"/>'s pair,
    /// unchanged, because the ground under a wreck is the same ground.</summary>
    public float Root = 0.02f;
    public float Fade = 0.050f;

    /// <summary>How far the rings' own plane reaches, in tile widths - the plane
    /// is what carries them, so this is a mesh size rather than a uniform.
    /// Short of <see cref="ProcBlast.RingReachDefault"/>: a rack lifts the hull it
    /// is inside rather than the cell it is standing on, so the ring on the ground
    /// is the smaller half of the event here and the larger half there.</summary>
    public float RingReach = 0.34f;

    public float Life = LifeDefault;

    private float _clock = -1.0f;

    /// <summary>Whether it is running. -1 is out, and out means nothing drawn -
    /// <see cref="HitLoop"/>'s convention and by its reason.</summary>
    public bool Alive => _clock >= 0.0f;

    /// <summary>Where the clock is, for a bench that shows what one is
    /// doing.</summary>
    public float Age => _clock;

    private MeshInstance3D? _dust;
    private MeshInstance3D? _fire;
    private MeshInstance3D? _ring;
    private ShaderMaterial? _dustInk;
    private ShaderMaterial? _fireInk;
    private ShaderMaterial? _ringInk;
    private Vector3 _nudge;

    /// <summary>
    /// The furthest the model can put anything, in tile widths: how far out
    /// sideways, and how far up.
    ///
    /// <b>No sweep, and the absence is the finding rather than a shortcut.</b>
    /// <see cref="ProcSpall.Bounds"/> and <see cref="ProcSlam.Bounds"/> both walk
    /// a bearing a degree at a time because the projection takes reach away and
    /// gives it to spread, so their worst case is mid-sweep. Nothing here has a
    /// bearing - see the class note - so there is one case and this is it.
    ///
    /// <paramref name="deck"/> is where the ring sits over the seat, because that
    /// is where every element of this effect starts. It is the whole of the
    /// horizontal offset and most of the vertical one.
    /// </summary>
    public static (float Along, float Up) Bounds(float reach, Vector2 deck)
    {
        float along = 0.0f, up = 0.0f;

        // <b>A pad per axis rather than one for both, and that is not tidiness.</b>
        // Every family here is drawn stretched along its own travel, which is
        // straight up - so its half-length and its half-width are different
        // numbers by a factor of three on the jets. Padded by the larger of the
        // two on both axes, the model claimed to reach 0.74 of a tile sideways
        // when nothing in it goes past 0.53, and the quad had to be a third wider
        // than the widest thing it holds.
        void Reach(Vector2 at, float across, float tall)
        {
            along = MathF.Max(along, MathF.Abs(at.X) + across);
            up = MathF.Max(up, MathF.Abs(at.Y) + tall);
        }

        // The flash, which does not travel: it is the charge going off inside the
        // hull, and all it does is grow.
        float flashR = Uniform("flash_size") * 1.25f * 1.35f;
        Reach(Vector2.Zero, flashR, flashR);

        // The jets, at the top of the fastest one's climb. <b>Seated by their own
        // foot</b>, which the model does too and for a reason a picture found: a
        // section is symmetric about its centre, so a tongue placed at the ring
        // hangs half its length below it and draws the hottest part of the event
        // on the gun mantlet.
        float jetTop = Uniform("jet_rise") * reach * 1.35f;
        float jetR = Uniform("jet_size") * 1.70f;
        float jetLong = jetR * Uniform("jet_stretch");
        Reach(new Vector2(jetTop * Uniform("jet_rake"), jetTop + jetLong * 0.80f),
              jetR, jetLong);

        // The fireball, at the top of its climb and the far edge of its cone. The
        // widest thing the fire draws, which is what makes it a ball rather than
        // more jet.
        float ballTop = Uniform("ball_climb") * reach * 1.30f;
        float ballR = Uniform("ball_size") * 1.55f;
        Reach(new Vector2(ballTop * Uniform("ball_spread"), ballTop),
              ballR, ballR * Uniform("ball_stretch"));

        // The black head, which is the widest thing here and usually what the
        // quad is cut to.
        float sootTop = Uniform("soot_climb") * reach * 1.25f;
        float sootR = Uniform("soot_size") * 1.50f;
        Reach(new Vector2(sootTop * Uniform("soot_cone"), sootTop),
              sootR, sootR * 1.45f);

        // And the skirt, whose height is the ground's rather than the deck's: it
        // runs out from under the hull, so it is measured from the seat and the
        // deck is taken back off it - ProcSlam's spill's arrangement, and the one
        // family in this model that goes downward. Stretched along the ground, so
        // its long axis is the across one here.
        float skirtR = Uniform("skirt_size") * 1.50f;
        Reach(new Vector2(Uniform("skirt_run") * reach * 1.40f,
                          Uniform("skirt_seat") - deck.Y),
              skirtR * Uniform("skirt_long"), skirtR);

        // Everything above is an offset from the ring, and the quad is measured
        // from the contact point.
        return (along + MathF.Abs(deck.X), up + MathF.Abs(deck.Y));
    }

    /// <summary>One of the three shaders' own numbers, by name - so
    /// <see cref="Bounds"/> asks the shaders what they say rather than carrying a
    /// second copy. The fire first, because most of this event is its; the dust's
    /// names fall through to it.</summary>
    private static float Uniform(string name) =>
        ProcBlast.Uniform(FireCode, name,
                          ProcBlast.Uniform(DustCode, name, 0.0f));

    /// <summary>The quad, in screen px: from <c>-Foot</c> across <c>Size</c>,
    /// which is what <see cref="Stage3D.Stem"/> takes -
    /// <see cref="ProcBlast.Quad"/> entire, and static for its reason: how big a
    /// detonation is can be asserted with no board under it.</summary>
    public static (Vector2 Foot, Vector2 Size) Quad(float tile, float flank,
                                                    float tall)
        => ProcBlast.Quad(tile, flank, tall);

    /// <summary>
    /// Build the three quads. <paramref name="tile"/> is the hex's own width in
    /// screen px - every length above is in those - and the two camera terms are
    /// the field's, handed in rather than read so this needs no board.
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

        // Toward the camera by the board's own clearance, the fire by twice it -
        // the burst's arrangement, so the dark half is behind the additive one
        // rather than sorting against it by the coin toss two coplanar quads get.
        _nudge = Stage3D.Clear(squash, rise);

        // <b>And the rings get a plane of their own, lying on the ground</b> -
        // ProcBlast's own note, which applies here word for word: below the
        // contact point and behind the ground are one test under this camera, so
        // a ring drawn on the upright quad loses exactly the half of itself that
        // spreads toward the camera.
        _ringInk = new ShaderMaterial
        {
            Shader = ProcBlast.Ringing,
            RenderPriority = Stage3D.StandOrder,
        };
        _ringInk.SetShaderParameter("level", 1.0f);
        _ringInk.SetShaderParameter("time", 0.0f);
        // <b>Dimmer than the ground burst's, which is already dimmed once.</b>
        // A round in the ground lights the cell it went off on; a rack lights the
        // inside of a hull, and what reaches the ground is what got past the
        // running gear. Read at ProcBlast's own gain the ring came out saying the
        // cell had been hit rather than the tank on it.
        _ringInk.SetShaderParameter("ring_gain", 0.30f);
        _ringInk.SetShaderParameter("disc_gain", 0.62f);
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
        Stand();
    }

    /// <summary>
    /// How big this one is, as a multiple of the tuned detonation.
    ///
    /// <b>The victim's size, not the shooter's</b>, and that is the one place this
    /// effect parts company with every other burst on the board. A ricochet, a
    /// plate burst and a crater are all sized by <see cref="Ordnance"/>, which is
    /// the calibre of the round that arrived; what goes off here is the
    /// ammunition of the tank that died. See <see cref="TankTick.Detonated"/>,
    /// including why the hull's own measured length is the wrong lever for it on
    /// these assets.
    ///
    /// <b>What it does <em>not</em> scale is where the deck is</b> - see
    /// <see cref="Place"/>. That defect has been paid for once already on
    /// <see cref="ProcSlam"/> and it is silent both times: the picture stays
    /// plausible and simply happens in the wrong place.
    /// </summary>
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

    /// <summary>
    /// Where it stands: the flat point the tank is touching and the lift of the
    /// cell under it.
    ///
    /// <b>The contact point, not the deck</b> - the deck is
    /// <see cref="Deck"/> and it is written into the shaders, because the seat is
    /// what the rings are drawn around and the rings are on the ground. Same
    /// split as <see cref="ProcSlam.Sit"/>, where the seat is the victim's foot
    /// and the plate is the point of impact.
    ///
    /// <paramref name="behind"/> puts it on the far side of the tank's own
    /// surface. <b>Left as one flag for the whole event, and that is a stated
    /// simplification rather than an oversight.</b> What comes out of a ring is
    /// outside the hull by the time it is drawn, so in front is right for the jet,
    /// the ball and the head; the flash alone would read better behind the turret,
    /// since it is the one element that starts <em>under</em> it. It is seated at
    /// the ring and lasts 0.06s, which is what carries the read today. A depth
    /// split by family is the obvious next lever if it turns out not to.
    /// </summary>
    public void Sit(Vector2 ground, float lift, float squash, float rise,
                    bool behind = false)
    {
        _seat = Stage3D.Trunk(ground, lift, 0.0f, squash, rise);
        _side = behind ? -1.0f : 1.0f;
        Stand();
    }

    /// <summary>The seat, the size and the side together, the quads' own nudges
    /// divided back out - <see cref="ProcSlam.Stand"/>'s reason: a clearance is a
    /// clearance whatever size the effect is.</summary>
    private void Stand()
    {
        Transform = _seat.ScaledLocal(Vector3.One * _might);
        if (_dust is not null)
            _dust.Position = _nudge * _side / _might;
        if (_fire is not null)
            _fire.Position = _nudge * 2.0f * _side / _might;
        // Half the clearance, and on the ground rather than toward the camera:
        // the plane lies on the cell's own top face and must not fight it.
        if (_ring is not null)
            _ring.Position = _nudge * 0.5f / _might;
    }

    /// <summary>Which side of the tank's own surface this one is on.</summary>
    public bool Behind => _side < 0.0f;

    /// <summary>
    /// Where the turret ring sits over the contact point, in tile widths - the
    /// one measurement this effect needs and the one it gets for free.
    ///
    /// <b><c>anchor_px</c> is the ring.</b> Hull, turret and tile share one axis
    /// of rotation and that sharing is what lets the layers stack at all, so the
    /// horizontal term is nought by construction and the vertical one is the
    /// height of the deck above the ground. A tank whose deck height is not known
    /// gets nought and the event happens at its tracks, which is wrong and
    /// visibly so rather than quietly so.
    /// </summary>
    public void Aim(Vector2 deck)
    {
        Deck = deck;
        Place();
    }

    /// <summary>Where the ring is, over the seat, in tile widths - what
    /// <see cref="Aim"/> was told, unscaled.</summary>
    public Vector2 Deck { get; private set; } = Vector2.Zero;

    /// <summary>The same point as the shaders actually have it: divided by the
    /// size, so the transform's own multiply gives back the measurement. Exposed
    /// so a check can assert the product rather than assert that the number does
    /// not move - which is what the first check on <see cref="ProcSlam"/> did,
    /// and it was asserting the bug.</summary>
    public Vector2 Placed => Deck / Mathf.Max(_might, 0.01f);

    /// <summary>The deck as the shaders see it - the measurement divided by the
    /// size, because <see cref="Might"/> is a scale on the transform and would
    /// otherwise move a measured place. See <see cref="Might"/>.</summary>
    private void Place()
    {
        Vector2 at = Placed;
        _dustInk?.SetShaderParameter("deck", at);
        _fireInk?.SetShaderParameter("deck", at);
    }

    /// <summary>Set it off. Restarts rather than refusing, for
    /// <see cref="ProcBlast.Fire"/>'s reason: a pool hands out the oldest slot,
    /// and a slot that refused to restart would be a burst that did not
    /// happen.</summary>
    public void Fire() => _clock = 0.0f;

    /// <summary>Whether the clock stands still - <see cref="ProcBlast.Hold"/>,
    /// and by its reason.</summary>
    public bool Hold;

    /// <summary>Where the clock is, settable, so a held detonation can be
    /// scrubbed to a frame for a contact sheet.</summary>
    public float Clock
    {
        get => _clock;
        set => _clock = value;
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
        if (_dust is not null)
            _dust.Visible = on;
        if (_fire is not null)
            _fire.Visible = on;
        if (_ring is not null)
            _ring.Visible = on;
        if (!on)
            return;

        _dustInk?.SetShaderParameter("time", _clock);
        _fireInk?.SetShaderParameter("time", _clock);
        _ringInk?.SetShaderParameter("time", _clock);
    }

    /// <summary>One number of one of the three shaders, live - read out of the
    /// material rather than off the text, so a panel shows what is running.
    /// <see cref="ProcSlam.Dial(ProcSlam.Part, string)"/>'s arrangement.</summary>
    public float Dial(Part part, string uniform)
    {
        if (_live.TryGetValue(part + ":" + uniform, out float held))
            return held;
        return ProcBlast.Uniform(Source(part), uniform, 0.0f);
    }

    /// <summary>The same number, written - and to every material when the frame is
    /// asked for, because the frame's numbers are the quad's and the quad is one
    /// shape under three shaders.</summary>
    public void Dial(Part part, string uniform, float value)
    {
        _live[part + ":" + uniform] = value;
        if (part == Part.Frame)
        {
            _dustInk?.SetShaderParameter(uniform, value);
            _fireInk?.SetShaderParameter(uniform, value);
            _ringInk?.SetShaderParameter(uniform, value);
            return;
        }
        Coat(part)?.SetShaderParameter(uniform, value);
    }

    /// <summary>Which of the three a number belongs to.
    /// <see cref="Part.Frame"/> is the pair the quad itself declares.</summary>
    public enum Part { Frame, Dust, Fire, Ring }

    private readonly System.Collections.Generic.Dictionary<string, float> _live
        = new();

    private static string Source(Part part) => part switch
    {
        Part.Dust => DustCode,
        Part.Fire => FireCode,
        Part.Ring => ProcBlast.RingCode,
        _ => FireCode,
    };

    private ShaderMaterial? Coat(Part part) => part switch
    {
        Part.Dust => _dustInk,
        Part.Fire => _fireInk,
        Part.Ring => _ringInk,
        _ => _fireInk,
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
        ink.SetShaderParameter("deck", Vector2.Zero);
        if (!dust)
            return ink;
        // <b>The burst's own helping of the shared banding rather than the
        // ricochet's half of it</b>, and the size of the mass is what decides:
        // fourteen elements making one volume is what the shared 0.55 was tuned
        // on, and a ricochet's handful that saturates wherever two overlap is
        // what takes half.
        ink.SetShaderParameter("dust_band", 0.50f);
        ink.SetShaderParameter("dust_tear", 0.56f);
        // <b>And it hides the tank outright, which none of the other three
        // do.</b> The others draw something happening to a tank that is still
        // there to be seen; this one is the tank coming apart, and a head of soot
        // you can read the turret's hatches through says the ammunition did not
        // really go off.
        ink.SetShaderParameter("dust_ink", 0.78f);
        return ink;
    }

    /// <summary>
    /// The fire: the filling going off inside the hull, the jets it leaves by and
    /// the ball that rolls off the top of them.
    ///
    /// <b>Vertical, and that is the whole of what makes it a rack rather than a
    /// large plate burst.</b> Every family here climbs and none of them has a
    /// bearing: what confines the gases is the hull, and the only ways out are
    /// upward. A cone leaning along a normal - which is what
    /// <see cref="ProcSlam"/> draws - would be a shell that went off <em>on</em>
    /// this tank rather than <em>in</em> it, and the two events are meant to be
    /// told apart at a glance.
    ///
    /// <b>The jets are the new idea and the rest is the board's.</b> They are what
    /// a confined detonation looks like from outside: not a ball expanding freely
    /// but a handful of long tongues coming out of the openings, each stretched
    /// along its own travel. <see cref="ProcBlast"/>'s cone of clods is the same
    /// arrangement with the earth taken out and the stretch turned upright.
    ///
    /// Additive and premultiplied, for the fire's reason everywhere else on this
    /// board: it is light, and it has nothing to hide behind.
    /// </summary>
    private const string FireRackShader = @"
shader_type spatial;
render_mode unshaded, cull_disabled, blend_add, depth_draw_never;

FLAME_NOISE

FLAME_INK

BLAST_FRAME

// <b>Where the turret ring sits over the contact point</b>, in tile widths -
// ProcRack.Deck, and the dust half declares the same number for the same reason.
// Nought is the tracks, which is wrong and looks it.
uniform vec2 deck = vec2(0.0);

// The flash: the charge going off inside the hull. One body, three frames, the
// brightest thing this board draws, and it does not travel at all - it is behind
// the armour when it starts and the armour is what it is about to remove.
uniform float flash_life = 0.075;
uniform float flash_size = 0.095;
// Past one, and deliberately - ProcBlast's flash's note, which every fire on this
// board now spends: a white heart is bought by adding an orange body to itself
// until it clips. That is brightness, not a whiter colour, and a paler colour
// here would buy the white by giving up the fire.
// <b>Raised until the heart went white, which is the note read forwards for
// once.</b> At 2.70 over this radius the first frames came out the same orange as
// the jets standing on top of them, so the event opened with no beginning - a
// torch lighting rather than a charge going off. There is no paler colour to
// reach for: an additive layer clips its own middle, and the white is that clip.
uniform float flash_gain = 4.20;

// The jets: what leaves by the ring, the hatches and the deck grille. Long, thin
// and upright, each stretched along its own travel.
uniform int jets = 7;
// <b>Shorter than the ball rather than alongside it, and that was the whole of
// why this read as one tongue.</b> At 0.30 the jets were still climbing while the
// ball was climbing behind them on the same axis, so the two families composited
// into a single column half a second long - a flame thrower. Sequenced, the event
// has three acts instead of one: a spike, a bloom, and what the bloom leaves.
uniform float jet_life = 0.22;
uniform float jet_stagger = 0.028;
// How far up the throw a jet gets, as a share of it.
uniform float jet_rise = 1.15;
uniform float jet_size = 0.046;
// How much longer than wide. The largest stretch on the board by a distance, and
// it is what says confined: a jet as round as it is long is a fireball, and a
// fireball is the family below.
uniform float jet_stretch = 3.20;
// How far off vertical they lean. Small: gases leaving a hole go where the hole
// points, and every hole in a tank's roof points up. Not zero, because seven
// tongues on one line is a comb - the failure ProcSlam's fragments paid for
// twice.
uniform float jet_rake = 0.34;
uniform float jet_gain = 1.30;

// The ball: what rolls off the top of the jets once the pressure is gone. Slower,
// rounder and by far the longest-lived of the three - this is the part of the
// event a player half a board away actually sees.
uniform int balls = 8;
uniform float ball_life = 0.60;
uniform float ball_stagger = 0.10;
uniform float ball_size = 0.135;
// <b>Climbing less and spreading twice as much, because it was drawing the jets
// again.</b> A family that goes up 0.78 of the throw with a cone of 0.30 is a
// column, and there was already a column in front of it; what a fireball does is
// bloom - it gets wider faster than it gets higher, and it is the widest thing the
// fire draws.
uniform float ball_climb = 0.62;
uniform float ball_spread = 0.62;
// Slightly squat rather than slightly tall, for the same reason: stretched
// upright it read as more jet.
uniform float ball_stretch = 0.95;
uniform float ball_gain = 1.00;

void fragment() {
    if (level <= 0.0) {
        ALBEDO = vec3(0.0);
        ALPHA = 0.0;
    } else {
        vec2 at = blast_at(UV);
        // Alpha-over inside the layer, additive on the way out - surfaces stack
        // the way surfaces stack, and the layer as a whole adds light.
        vec4 lit = vec4(0.0);

        for (int k = 0; k < jets; k++) {
            float fk = float(k);
            float born = jet_stagger * ember_hash(vec2(fk, 121.0));
            float a = (time - born) / max(jet_life, 1e-3);
            if (a > 0.0 && a < 1.0) {
                float fast = 0.55 + 0.80 * ember_hash(vec2(fk, 122.0));
                float up = jet_rise * reach * fast * pow(a, 0.62);
                float wide = up * jet_rake
                             * (2.0 * ember_hash(vec2(fk, 123.0)) - 1.0);
                // Width and length take their own hash each - see below - and the
                // length is needed here as well as there, because of what a
                // stretched section is: flame_blob is symmetric about its own
                // centre, so a tongue seated AT the ring hangs half its length
                // below it. On this tank that half landed on the gun mantlet, and
                // the hottest part of the event was drawn on the barrel rather
                // than coming out of the roof. So a jet is seated by its own foot:
                // it leaves the hole, it does not straddle it.
                float r = jet_size * (0.55 + 1.05 * ember_hash(vec2(fk, 124.0)));
                // Long while it is being pushed, shortening as it stops: a
                // tongue that keeps its length after the pressure has gone reads
                // as a streak drawn on the screen.
                float draw = mix(jet_stretch
                                 * (0.55 + 1.00 * ember_hash(vec2(fk, 125.0))),
                                 1.0, clamp(a * 1.30, 0.0, 1.0));
                vec2 p = deck + vec2(wide, up + r * draw * 0.80);
                // Along the line from the ring to where it is - the way a jet is
                // going is the way it came, and the stretch has to lie on it or
                // the tongue reads as an ellipse someone rotated.
                vec2 trail = p - deck;
                vec2 flow = length(trail) < 1e-4 ? vec2(0.0, 1.0)
                                                 : normalize(trail);
                // Its width and its length take their own hash each, which is
                // what stops seven of them reading as one comb - ProcSpall's
                // shards, and the finding is paid for there. Both are worked out
                // above, where the seating needs the length.
                lit = flame_over(lit,
                                 flame_blob(at, p, r, r * draw, flow,
                                            fk + 121.0,
                                            clamp(a + 0.25
                                                  * ember_hash(vec2(fk, 126.0)),
                                                  0.0, 1.0),
                                            jet_gain * flame_life(a, 0.75)));
            }
        }

        for (int k = 0; k < balls; k++) {
            float fk = float(k);
            float born = ball_stagger * ember_hash(vec2(fk, 131.0));
            float a = (time - born) / max(ball_life, 1e-3);
            if (a > 0.0 && a < 1.0) {
                float fast = 0.45 + 0.85 * ember_hash(vec2(fk, 132.0));
                float up = ball_climb * reach * fast * pow(a, 0.80);
                float wide = up * ball_spread
                             * (2.0 * ember_hash(vec2(fk, 133.0)) - 1.0);
                vec2 p = deck + vec2(wide, up);
                float r = ball_size
                          * (0.55 + 0.90 * ember_hash(vec2(fk, 134.0)))
                          * (0.55 + 0.80 * a);
                lit = flame_over(lit,
                                 flame_blob(at, p, r, r * ball_stretch,
                                            vec2(0.0, 1.0), fk + 131.0,
                                            // Each body its own place on the ramp
                                            // as well as its own age -
                                            // ProcBlast's ball's finding: read at
                                            // one stop the whole thing composites
                                            // into a flat orange disc.
                                            clamp(a + 0.35
                                                  * ember_hash(vec2(fk, 135.0)),
                                                  0.0, 1.0),
                                            ball_gain * flame_life(a, 0.82)));
            }
        }

        // <b>The flash last, on top of everything, and that took a picture to
        // work out.</b> Composited first - which is where the plate burst's core
        // sits and where this one started - it was covered by seven jets leaving
        // the same point on the same frame, so the event opened orange and the
        // charge going off had no beginning at all. The plate burst gets away with
        // the other order because core_lead pushes its core out along the plate's
        // normal, away from what follows it; nothing here leads anywhere, because
        // nothing here has a bearing.
        //
        // Brightest thing on the board and therefore last: this is a layer whose
        // white is the clip of its own sum, and a dimmer surface laid over that
        // sum takes the clip back.
        float fa = time / max(flash_life, 1e-4);
        if (fa < 1.0) {
            float r = flash_size * (0.40 + 0.85 * sqrt(fa));
            float gain = flash_gain * pow(max(1.0 - fa, 0.0), 1.30);
            lit = flame_over(lit,
                             flame_blob(at, deck, r, r * 0.92,
                                        vec2(0.0, 1.0), 0.0, 0.0, gain));
        }

        // Premultiplied by the compositing above, so the alpha is spent and a
        // nought is handed over.
        ALBEDO = lit.rgb * (level * blast_footing(at));
        ALPHA = 1.0;
    }
}
";

    /// <summary>
    /// The soot: the black head the fireball leaves, and the dust it pushes out
    /// from under the running gear.
    ///
    /// <b>Short, and the shortness is the point of the whole class.</b> This is
    /// not the wreck's column - see the class note - it is the head, and it is
    /// here so the ball has something to turn into instead of vanishing. It is
    /// gone by <see cref="Wreck.CharSeconds"/>, which is when the wreck's own
    /// column has come all the way up behind it.
    ///
    /// <b>The darkest of the five pairs on this board.</b> The burst's is earth,
    /// the kick's is propellant, the ricochet's is paint and road dirt, the plate
    /// burst's is a burnt filling mixed with paint - and this is a tank's own fuel
    /// and stowage burning inside its armour. <see cref="ProcBlast.DustInk"/>'s
    /// finding still bounds it: <c>dust_seat</c> minus <c>dust_dense</c> is where
    /// nearly every pixel lands, so the dark stop <em>is</em> the brightness of
    /// the cloud, and taken to a real black the mass reads as a hole cut in the
    /// board rather than as smoke.
    ///
    /// <b>The skirt is the ground half, and it is the only family here with no
    /// vertical travel at all.</b> A detonation inside a hull blows dust out
    /// under the sponsons in a low ring - which on a hex board is the second thing
    /// that says <em>this cell</em>, the rings on the ground being the first.
    /// </summary>
    private const string DustRackShader = @"
shader_type spatial;
render_mode cull_disabled, depth_draw_never;

FLAME_NOISE

FLAME_INK

BLAST_FRAME

DUST_INK

// <b>The ring over the contact point</b> - the fire half declares the same
// number, see ProcRack.Deck.
uniform vec2 deck = vec2(0.0);

// The head: what the fireball turns into. Climbing, spreading and thinning, and
// over before the wreck's own column is fully up.
uniform int soot = 14;
// <b>Long enough to reach the wreck's own column, and that is measured against
// Wreck.RiseSeconds rather than chosen.</b> At 0.95 with a stagger of 0.13 the
// last element was gone by about a second and the wreck's fire was still coming
// up behind it, so the board showed an undamaged-looking green tank with nothing
// on it for half a second and then a flame. A hole between the two is the one
// failure this whole class exists to avoid, and it was in the class itself.
uniform float soot_life = 1.25;
uniform float soot_stagger = 0.16;
uniform float soot_size = 0.105;
uniform float soot_climb = 0.68;
uniform float soot_cone = 0.44;
uniform float soot_ink = 1.00;

// The skirt: dust pushed out from under the hull. Flat, both ways, and seated on
// the ground rather than on the deck.
uniform int skirt = 10;
uniform float skirt_life = 0.75;
uniform float skirt_stagger = 0.05;
uniform float skirt_run = 0.40;
uniform float skirt_size = 0.078;
uniform float skirt_long = 1.85;
// How high off the ground it sits. Not the deck: this one leaves under the tank.
uniform float skirt_seat = 0.035;
// <b>Thicker than a skirt of dust wants to be, and the reason is the belts.</b>
// The pose swaps under this effect - see Wreck.Veil - and the two things that
// change are the turret and the two tracks. The tracks are at the bottom of the
// silhouette, which is where nothing else in this model reaches, so this family
// is the only cover the cut has down there.
uniform float skirt_ink = 0.70;

// <b>Darker than it was first written, because over this ground it came out
// tan.</b> The head stands against desert and takes the ball's own orange from
// behind, and read at the plate burst's stops it was a dust cloud rather than the
// soot of a tank burning inside its armour. Still short of a real black, for
// ProcBlast.DustInk's measured reason - a saturating mass at a black stop reads as
// a hole cut in the board.
uniform vec3 soot_dark : source_color = vec3(0.255, 0.244, 0.236);
uniform vec3 soot_lit : source_color = vec3(0.700, 0.686, 0.668);

void fragment() {
    if (level <= 0.0) {
        ALBEDO = vec3(0.0);
        ALPHA = 0.0;
    } else {
        vec2 at = blast_at(UV);
        // One pass that adds up dust and one that shades the result -
        // ProcBlast's finding, unchanged: an element hands back how much it puts
        // in the way and which way its surface leans, and nothing else.
        float dens = 0.0;
        float years = 0.0;
        vec2 lean = vec2(0.0);

        for (int k = 0; k < soot; k++) {
            float fk = float(k);
            float born = soot_stagger * ember_hash(vec2(fk, 141.0));
            float a = (time - born) / max(soot_life, 1e-3);
            if (a > 0.0 && a < 1.0) {
                float fast = 0.40 + 0.85 * ember_hash(vec2(fk, 142.0));
                float up = soot_climb * reach * fast * pow(a, 0.72);
                float wide = up * soot_cone
                             * (2.0 * ember_hash(vec2(fk, 143.0)) - 1.0);
                vec2 p = deck + vec2(wide, up);
                float r = soot_size
                          * (0.60 + 0.90 * ember_hash(vec2(fk, 144.0)))
                          * (0.62 + 0.70 * a);
                float gain = soot_ink * (1.0 - exp(-a / 0.030))
                             * pow(max(1.0 - a, 0.0), 1.15);
                dust_part(at, p, r, r * 1.20, vec2(0.0, 1.0), fk + 141.0,
                          gain, dust_soft, a, dens, lean, years);
            }
        }

        for (int k = 0; k < skirt; k++) {
            float fk = float(k);
            float born = skirt_stagger * ember_hash(vec2(fk, 151.0));
            float a = (time - born) / max(skirt_life, 1e-3);
            if (a > 0.0 && a < 1.0) {
                // Its own side per element, because a hull vents both ways at
                // once - ProcSlam's cloud lane, and the same argument.
                float hand = ember_hash(vec2(fk, 152.0)) < 0.5 ? -1.0 : 1.0;
                float out_at = skirt_run * reach * pow(a, 0.55)
                               * (0.40 + 1.00 * ember_hash(vec2(fk, 153.0)));
                vec2 p = vec2(deck.x + hand * out_at, skirt_seat);
                float r = skirt_size
                          * (0.55 + 0.95 * ember_hash(vec2(fk, 154.0)))
                          * (0.50 + 1.00 * a);
                float gain = skirt_ink * (1.0 - exp(-a / 0.035))
                             * pow(max(1.0 - a, 0.0), 1.45);
                // Lying along the ground, which is the whole of what this family
                // says: an element standing up here would be a small column, and
                // there is one of those already.
                dust_part(at, p, r, r * skirt_long, vec2(hand, 0.0), fk + 151.0,
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

    /// <summary>The soot as it is compiled - the shared noise, the shared ink,
    /// the burst's frame and the burst's dust, put together in one place so a
    /// check can ask whether the five effects on this board are one text.</summary>
    internal static readonly string DustCode =
        DustRackShader.Replace("FLAME_NOISE", Stage3D.EmberNoiseCode)
                      .Replace("FLAME_INK", Stage3D.FlameInk)
                      .Replace("BLAST_FRAME", ProcBlast.FrameCode)
                      .Replace("DUST_INK", ProcBlast.DustInk);

    internal static readonly string FireCode =
        FireRackShader.Replace("FLAME_NOISE", Stage3D.EmberNoiseCode)
                      .Replace("FLAME_INK", Stage3D.FlameInk)
                      .Replace("BLAST_FRAME", ProcBlast.FrameCode);

    private static readonly Shader Sooting = new() { Code = DustCode };
    private static readonly Shader Blazing = new() { Code = FireCode };
}
