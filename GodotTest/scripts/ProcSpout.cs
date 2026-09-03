using Godot;

namespace TankSpriteTest;

/// <summary>
/// A round bursting on water: the spray it throws, the smoke column that comes
/// out of the middle of it and the fire at its foot - <c>water_he</c> in
/// <c>docs/blast.md</c>.
///
/// <b>What made this item a bug rather than a gap.</b> The other unbuilt entries
/// in that taxonomy are pictures nobody has drawn yet; this one had a picture
/// already and it was the wrong one. A shell into the pond raised
/// <see cref="ProcBlast"/> - a cone of thrown earth, a collar of dust and a dirty
/// column - because <see cref="TankTick.Landed"/> hands the board a point and a
/// lift and the board never asked whether that point was wet. So the fix is one
/// question in <see cref="Stage3D.Land"/> and this class is the answer to it.
///
/// <b>The shape of it is read off a reference sheet of nine frames, and the
/// taxonomy's own line about it was wrong.</b> That line says "white geyser,
/// spray, a ring on the surface, and no fire at all" - which describes a charge
/// detonating <em>under</em> water. A shell bursting <em>on</em> water is a
/// different event, and the reference shows it in this order:
///
/// <list type="number">
/// <item>white <b>spikes</b> only - sharp, radiating, and the whole picture for
/// the first fifth of a second, with a low ring already spreading;</item>
/// <item>a <b>dark mass</b> appears in the middle and grows past them;</item>
/// <item>the dark column <b>dominates</b> - billowing, taller than wide - with
/// small bright <b>fire at its foot</b>, and the white is a skirt around its
/// base;</item>
/// <item>the fire dies, the column pales and drifts, and a wide flat ring of foam
/// is what is left on the water.</item>
/// </list>
///
/// So the mass of this event is <b>smoke</b>, not water, and there is fire in it.
/// The water is the first thing seen and the last thing left, and it is never the
/// biggest thing. That is the opposite of what was built first, twice over.
///
/// <b>Three quads, which is the most of any effect on this board, and the reason
/// is three lights rather than three substances.</b> Water and smoke are both
/// masses of many elements summed into one volume - <see cref="ProcBlast.DustInk"/>
/// entire - but earth <em>darkens</em> as it thickens and water <em>brightens</em>,
/// so the shared ink's <c>dust_dense</c> would have to have two signs at once. It
/// cannot, on one material; it can on two. See <see cref="Dense"/>, and see
/// <see cref="Part"/> for what each quad is. Fire is the third for the reason it
/// always is: it is light, so it adds.
///
/// <b>The spray is drawn as spikes and that is the single thing that most decides
/// whether this reads as water.</b> Every other mass on this board is made of
/// soft round elements, because dust and smoke are soft and round. Thrown water
/// is not: it is sheets and jets that tear into long sharp fingers, and the
/// reference's first two frames are nothing but those. So this family takes the
/// crispest profile in the project and the longest stretch, and it is the one
/// place where reading an element individually is <em>correct</em>.
///
/// <b>Water's white is a colour rather than a brightness, which is this board's
/// own rule read backwards for the one case it does not cover.</b>
/// <see cref="ProcRack"/>, <see cref="ProcSlam"/> and <see cref="ProcBlast"/> all
/// buy their white heart by adding an orange body to itself until it clips -
/// because they are light, and an additive layer's white <em>is</em> that clip.
/// Foam is not light, it is a surface: air whipped into water, and it is white
/// the way paint is white.
///
/// <b>The water's dark stop is the pond's own colour and is measured off it</b>,
/// not picked: <c>shallow</c> in the computed surface's shader is
/// <c>(0.24, 0.52, 0.50)</c>, and water in the air is the pond with light through
/// it. A plume whose shadowed side is grey reads as dust, which is the failure
/// this whole class exists to correct.
///
/// <b><see cref="Root"/> is nought, alone on this board.</b> Every other burst is
/// seated a little above the face it went off on, so its elements do not draw
/// through ground that is opaque and beneath them. Here the face <em>is</em> the
/// water and the water is where the spray goes back to, so the surface is exactly
/// where this effect ends - and <c>blast_footing</c>, which needed no change at
/// all, is what cuts it there.
///
/// <b>There is no ring layer in here, and its absence is the finding.</b> Every
/// other burst lays rings on a plane of its own (<see cref="ProcBlast.RingCode"/>,
/// which <see cref="ProcRack"/> takes outright) because the ground it went off on
/// cannot answer for itself. Water can: <see cref="Ripples"/> is a damped wave
/// field with reflecting banks, and a hole punched in it spreads a ring at the
/// right speed and comes back off the shore for free. See
/// <see cref="Ripples.Strike"/>. A drawn ring beside a solved one would be two
/// accounts of one surface - the double model this project spends its docstrings
/// refusing - and the drawn one would be the account that walked through the bank.
///
/// <b>And it digs nothing.</b> Sixth time that sentence is written across these
/// classes and the first time it needs no argument at all: a hole in water is the
/// effect.
/// </summary>
public sealed partial class ProcSpout : Node3D
{
    /// <summary>
    /// How high it throws water, in tile widths - <see cref="ProcBlast.Reach"/>'s
    /// number and its argument entire: every length in the spray is a ratio to
    /// it, so a bigger round is this plume at a bigger <see cref="Might"/> rather
    /// than a second set of numbers.
    ///
    /// <b>It governs the water and not the smoke</b>, which is the structure the
    /// reference shows: the spray is thrown once and comes back, and the column
    /// keeps rising long after it has gone. The column's height is
    /// <c>smoke_climb</c>, and it is the taller of the two.
    /// </summary>
    public float Reach = ReachDefault;

    /// <summary>The two numbers something else has to be able to ask for -
    /// <see cref="ProcBlast.ReachDefault"/>'s reason: a bench that wants to reset
    /// a dial needs the tuned value, and reading it off a live instance gets
    /// whatever the last shot left.</summary>
    public const float ReachDefault = 1.05f;

    /// <summary>
    /// How long the whole event runs.
    ///
    /// <b>The longest of the seven, and the reference is what says so.</b> Its
    /// nine frames end with a column still standing and a ring of foam still
    /// spreading; the fire is out by a third of the way through and the spray by
    /// half. What decides the length is the smoke, exactly as it does for the
    /// burst in earth - and for the same reason, which is that smoke is the only
    /// thing here that neither falls nor burns out.
    /// </summary>
    public const float LifeDefault = 2.00f;

    /// <summary>
    /// The quad, in tile widths: half-width either side of the seat, and height
    /// above it.
    ///
    /// Checked against <see cref="Bounds"/>, which is asked of the model - the
    /// quad holds what the model CAN produce, not what it happens to draw.
    /// </summary>
    public float Flank = 0.66f;
    public float Tall = 1.52f;

    /// <summary>
    /// How far above the contact point the effect is seated, and the band it
    /// fades out over below it.
    ///
    /// <b>Nought, alone on this board, and it is the model rather than an
    /// omission</b> - see the class note. The fade band is what makes that a
    /// waterline rather than a cut: a couple of pixels over which foam thins into
    /// the surface it is falling into.
    /// </summary>
    public float Root = 0.0f;
    public float Fade = 0.045f;

    public float Life = LifeDefault;

    private float _clock = -1.0f;

    /// <summary>Whether it is running. -1 is out, and out means nothing drawn -
    /// <see cref="HitLoop"/>'s convention and by its reason.</summary>
    public bool Alive => _clock >= 0.0f;

    /// <summary>Where the clock is, for a bench that shows what one is
    /// doing.</summary>
    public float Age => _clock;

    private MeshInstance3D? _foam;
    private MeshInstance3D? _fire;
    private ShaderMaterial? _foamInk;
    private ShaderMaterial? _fireInk;
    private Vector3 _nudge;

    /// <summary>
    /// Where the water's mass sits on its own light ramp, and which way thickness
    /// moves it - the shared dust ink's two numbers, overridden on the water's
    /// quad and named so a check can assert the <em>sign</em>.
    ///
    /// <b>Negative, alone on this board, and it is the physics rather than a
    /// preference.</b> <see cref="ProcBlast.DustInk"/> lights a mass as
    /// <c>seat + ridge*side*(1-mass) - dense*mass</c>: earth in the air darkens
    /// as it thickens, because earth absorbs. Water brightens as it thickens, for
    /// the same reason a thin film of it is clear - light scatters out of it
    /// rather than being eaten.
    ///
    /// <b>And it is the whole reason this effect has two mass quads rather than
    /// one.</b> The smoke on the other quad wants the sign the ink is written
    /// with, unchanged, because smoke is what that ink was written for; one
    /// material cannot hold both.
    /// </summary>
// <b>Seated high with a large negative lean, which is the correction of a
    /// picture that read as steam.</b> At seat 0.42 with a lean of -0.10 the
    /// arithmetic put the thin sunward edge at 0.94 and the dense core at 0.52 -
    /// bright rims round a mid-grey interior, which is exactly how a cloud is lit
    /// and exactly not how a column of water is. White where it is thick.
    public const float Seat = 0.55f;

    /// <inheritdoc cref="Seat"/>
    public const float Dense = -0.35f;

    /// <summary>
    /// The furthest the model can put anything, in tile widths: how far out
    /// sideways, and how far up.
    ///
    /// <b>No sweep, for <see cref="ProcRack.Bounds"/>'s reason.</b> The ricochet
    /// and the plate burst walk a bearing a degree at a time because the
    /// projection takes reach away from one axis and gives it to the other, so
    /// their worst case is mid-sweep. A round arriving at water has a bearing and
    /// this model does not use it: what comes out of a hole in a surface goes up
    /// and outward in every direction, which is one case and this is it.
    ///
    /// <b>A pad per axis</b>, also the rack's finding: every family here is drawn
    /// stretched along its own travel, so its half-length and its half-width are
    /// different numbers - padded by the larger on both axes the model claims a
    /// width nothing in it reaches.
    /// </summary>
    public static (float Along, float Up) Bounds(float reach)
    {
        float along = 0.0f, up = 0.0f;

        void Reach(Vector2 at, float across, float tall)
        {
            along = Mathf.Max(along, Mathf.Abs(at.X) + across);
            up = Mathf.Max(up, Mathf.Abs(at.Y) + tall);
        }

        // The column, at the top of its envelope: the tallest element is the
        // one that sits at the head of it, and its own body is drawn stretched
        // upright.
        float coneR = Uniform("cone_size") * 1.45f * (1.0f + Uniform("cone_bloom"));
        Reach(new Vector2(Uniform("cone_foot"), reach * 1.20f),
              coneR, coneR * Uniform("cone_long"));

        // The skirt on the surface: the widest thing here, and its long axis is
        // the across one because it lies flat.
        float skirtR = Uniform("skirt_size") * 1.45f * 1.55f;
        Reach(new Vector2(Uniform("skirt_run"), Uniform("skirt_climb") * 1.20f),
              skirtR * Uniform("skirt_flat"), skirtR);

        // And the flash, which does not travel: it sits at the waterline and
        // grows.
        float fireR = Uniform("blaze_size") * 1.50f * 1.30f;
        Reach(new Vector2(Uniform("blaze_run"),
                          Uniform("blaze_seat") + Uniform("blaze_climb")),
              fireR, fireR);

        return (along, up);
    }

    /// <summary>One of the three shaders' own numbers, by name - so
    /// <see cref="Bounds"/> asks the shaders what they say rather than carrying a
    /// second copy. The water first, because the frame is measured on its throw;
    /// the other two fall through to it.</summary>
    private static float Uniform(string name) =>
        ProcBlast.Uniform(FoamCode, name,
                          ProcBlast.Uniform(FireCode, name, 0.0f));

    /// <summary>The quad, in screen px: from <c>-Foot</c> across <c>Size</c>,
    /// which is what <see cref="Stage3D.Stem"/> takes -
    /// <see cref="ProcBlast.Quad"/> entire, and static for its reason: how big a
    /// splash is can be asserted with no board under it.</summary>
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

        _foamInk = Ink(Foaming, Part.Foam);
        _fireInk = Ink(Blazing, Part.Fire);
        _foam = Slab(shape, _foamInk);
        _fire = Slab(shape, _fireInk);

        // <b>The order of the three nudges is the order of the reference.</b> The
        // column is behind, the spray is in front of it - the white is drawn over
        // the dark on every frame of the sheet - and the fire is in front of both,
        // at the foot. Two alpha-blended quads sorting against each other by the
        // coin toss two coplanar surfaces get is exactly the failure the burst's
        // own clearance exists to prevent, and here it would swap the spray and
        // the column at some camera angles and not others.
        _nudge = Stage3D.Clear(squash, rise);
        Stand();
    }

    /// <summary>
    /// How big this one is, as a multiple of the tuned plume.
    ///
    /// The calibre of the round that arrived, like the crater and the ground
    /// burst and unlike the rack - see <see cref="ProcRack.Might"/>, where the
    /// size is the victim's. Nothing is hit here; the water is hit.
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
    private float _tile = 1.0f;

    /// <summary>
    /// Where it stands: the flat point the round went into, and the height of the
    /// water there.
    ///
    /// <b>The water's own top and not the cell's floor</b>, which is the one
    /// number this effect needs the field for - see
    /// <see cref="HexField.WaterTop"/>. A shell carries the shooter's lift with
    /// it, on the stated assumption that a tank gun is level (see
    /// <c>TankTick.Loose</c>), and a pond's surface stands
    /// <see cref="HexField.WaterRise"/> above the bed it fills: seated on the
    /// lift that travelled with the round, the plume would come up out of the
    /// bottom of the pond and the skirt would be drawn under the surface.
    /// </summary>
    public void Sit(Vector2 ground, float top, float squash, float rise)
    {
        _seat = Stage3D.Trunk(ground, top, 0.0f, squash, rise);
        Stand();
    }

    /// <summary>The seat and the size together, each quad's own nudge divided
    /// back out - <see cref="ProcRack"/>'s reason: a clearance is a clearance
    /// whatever size the effect is.</summary>
    private void Stand()
    {
        Transform = _seat.ScaledLocal(Vector3.One * _might);
        if (_foam is not null)
            _foam.Position = _nudge / _might;
        if (_fire is not null)
            _fire.Position = _nudge * 2.0f / _might;
    }

    /// <summary>Set it off. Restarts rather than refusing, for
    /// <see cref="ProcBlast.Fire"/>'s reason: a pool hands out the oldest slot,
    /// and a slot that refused to restart would be a splash that did not
    /// happen.</summary>
    public void Fire() => _clock = 0.0f;

    /// <summary>Whether the clock stands still - <see cref="ProcBlast.Hold"/>,
    /// and by its reason.</summary>
    public bool Hold;

    /// <summary>Where the clock is, settable, so a held splash can be scrubbed to
    /// a frame for a contact sheet.</summary>
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
        if (_foam is not null)
            _foam.Visible = on;
        if (_fire is not null)
            _fire.Visible = on;
        if (!on)
            return;

        _foamInk?.SetShaderParameter("time", _clock);
        _fireInk?.SetShaderParameter("time", _clock);
    }

    /// <summary>Which of the three a number belongs to.
    /// <see cref="Part.Frame"/> is the pair the quad itself declares.</summary>
    public enum Part { Frame, Foam, Fire }

    /// <summary>One number of one of the three shaders, live - read out of the
    /// material rather than off the text, so a panel shows what is running.
    /// <see cref="ProcRack.Dial(ProcRack.Part, string)"/>'s arrangement.</summary>
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
            _foamInk?.SetShaderParameter(uniform, value);
            _fireInk?.SetShaderParameter(uniform, value);
            return;
        }
        Coat(part)?.SetShaderParameter(uniform, value);
    }

    private readonly System.Collections.Generic.Dictionary<string, float> _live
        = new();

    private static string Source(Part part) => part switch
    {
        Part.Fire => FireCode,
        _ => FoamCode,
    };

    private ShaderMaterial? Coat(Part part) => part switch
    {
        Part.Fire => _fireInk,
        _ => _foamInk,
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

    private ShaderMaterial Ink(Shader how, Part part)
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
        if (part == Part.Foam)
        {
            // <b>The water hides what is behind it, which the earth's dust
            // deliberately does not.</b> ProcBlast.DustInk's cap is 0.72 because
            // earth in the air is not a wall and a mass that saturates to one read
            // as a hole cut in the board. Foam is nearer a wall than dust is - it
            // is water, and there is water behind it.
            ink.SetShaderParameter("dust_ink", 0.88f);
            // The sign flip, and the whole reason there are two mass quads - see
            // Dense.
            ink.SetShaderParameter("dust_seat", Seat);
            ink.SetShaderParameter("dust_dense", Dense);
            // The sunward edge no longer has to carry the whole of the white, so a
            // term tuned to do that now only shapes it.
            ink.SetShaderParameter("dust_ridge", 0.35f);
            // <b>Torn harder than earth, and this is the number that makes a sheet
            // of water into fingers.</b> Dust is ragged at its rim because that is
            // where it thins; thrown water is ragged all through, because it is
            // tearing into drops as it goes.
            ink.SetShaderParameter("dust_tear", 0.45f);
            ink.SetShaderParameter("dust_grain", 14.0f);
        }
        return ink;
    }

    /// <summary>
    /// The water: the spikes it throws and the skirt they leave on the surface.
    ///
    /// <b>Spikes, and the crispest profile in the project.</b> Every other mass on
    /// this board is soft and round because dust and smoke are; thrown water is
    /// sheets and jets tearing into long sharp fingers, and the reference's first
    /// two frames are nothing else. This is the one family here where reading an
    /// element individually is correct - which is why the first version of this
    /// class was reported as cartoonish for making them round, and the second for
    /// making them a cloud.
    /// </summary>
    private const string FoamShader = @"
shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_never;

FLAME_NOISE

FLAME_INK

BLAST_FRAME

DUST_INK

// <b>The column: one mass of water, going up fast and coming down slowly.</b>
//
// <b>It is a stack rather than a throw, and that is what makes it a column.</b>
// Each element sits at its own fraction of the column's current height, and the
// column's width at that fraction runs from cone_foot at the waterline to
// cone_head at the top - so the family IS the shape, and it grows and sags as a
// whole instead of forty things flying apart. Written as a throw, which is where
// this class started twice, the elements separate by construction: forty bodies
// on forty paths stop overlapping, and on white water each one then reads as its
// own object.
// <b>Ninety-six, and the count is set by the trunk rather than by taste.</b> A
// trunk 0.42 of a tile across has to be several elements wide before its edge can
// be torn rather than scalloped, and its whole height has to be layered: read at
// sixty-four elements of 0.075 each blob WAS the trunk, and the column came out a
// heap of soft puffs - which is the same failure as the feathers with the sign
// reversed, an element the eye can pick out.
uniform int cone = 120;
uniform float cone_life = 1.80;
uniform float cone_stagger = 0.10;
// How high it goes: <c>reach</c>, and here that is the whole column rather than a
// throw. Three times the width of its own trunk, which is the reference.

// <b>Where in its life it tops out, and the two exponents either side.</b> This
// is the request stated as one envelope: up in a sixth of the time, down over the
// other five sixths.
//
// <b>The shared blast_throw cannot say it, and that is why it is not used
// here.</b> A parabola stated as an apex is symmetric - equal time up and equal
// time down - which is right for a clod of earth and wrong for water: what comes
// down slowly is the divided part, held by drag rather than falling free. Fast
// with deceleration on the way up, slow then accelerating on the way down.
uniform float cone_peak = 0.15;
uniform float cone_rise = 0.55;
uniform float cone_fall = 1.60;
// The trunk, at the waterline and at the head. Wide foot, narrow top: the taper
// is what says water rather than smoke, which entrains and mushrooms.
uniform float cone_foot = 0.175;
uniform float cone_head = 0.09;
uniform float cone_size = 0.038;
// How much longer than wide, along its own travel. <b>Small, and it is the number
// two builds of this class got wrong.</b> At 2.6 and at 4.2 the elements became
// readable objects - drops, then feathers - and forty readable objects are not a
// mass however they are coloured. What a stretch is for here is the grain of the
// column, not the shape of a drop.
uniform float cone_long = 1.30;
// <b>Soft, near the cloud's own.</b> dust_soft is 2.55 because a mass in
// transition everywhere has no edge to count; a clod of earth keeps 1.85 because
// it is a small hard body. Water in a column of this size is nearer the first,
// and the 0.95 an earlier pass reached for is what drew the feathers: an element
// with an edge is an element the eye can pick out.
uniform float cone_soft = 2.20;
uniform float cone_ink = 1.70;
// How much bigger an element is at the head than at the foot. The cauliflower:
// what tears on the reference is the top, because that is where the water has
// been in the air longest.
uniform float cone_bloom = 0.45;

// The skirt: what the spray leaves lying on the surface, spreading. Flat, both
// ways, and the longest-lived thing in the water - on the reference it is still
// widening in the last frame, after the fire is out and the column has paled.
uniform int skirt = 26;
uniform float skirt_life = 1.90;
uniform float skirt_born = 0.03;
uniform float skirt_stagger = 0.30;
uniform float skirt_run = 0.40;
uniform float skirt_climb = 0.10;
uniform float skirt_size = 0.046;
uniform float skirt_flat = 1.90;
uniform float skirt_ink = 0.62;

// <b>What water in the air is, at its darkest and fully lit.</b> The dark stop is
// measured off the pond rather than picked: Stage3D's computed water declares
// shallow = (0.24, 0.52, 0.50), and water in the air is that with light through
// it, because what makes a plume pale is the air whipped into it.
uniform vec3 water_dark : source_color = vec3(0.455, 0.655, 0.645);
// <b>And the pale stop is very nearly white, which is correct exactly once on
// this board.</b> Every other effect buys white by clipping an additive sum, and
// a white colour there would only wash the fire out. Foam is a surface, not
// light: it is white the way paint is.
uniform vec3 water_lit : source_color = vec3(0.970, 0.988, 1.0);

void fragment() {
    if (level <= 0.0) {
        ALBEDO = vec3(0.0);
        ALPHA = 0.0;
    } else {
        vec2 at = blast_at(UV);
        float dens = 0.0;
        vec2 lean = vec2(0.0);
        float years = 0.0;

        for (int k = 0; k < skirt; k++) {
            float fk = float(k);
            float born = skirt_born + skirt_stagger * ember_hash(vec2(fk, 61.0));
            float a = (time - born) / max(skirt_life, 1e-3);
            if (a > 0.0 && a < 1.0) {
                float side = 2.0 * ember_hash(vec2(fk, 62.0)) - 1.0;
                float out_at = skirt_run * side * pow(a, 0.45);
                float up = skirt_climb * pow(a, 0.85)
                           * (0.35 + 0.85 * ember_hash(vec2(fk, 63.0)));
                float r = skirt_size * (0.55 + 0.9 * ember_hash(vec2(fk, 64.0)))
                          * (0.45 + 1.10 * a);
                float gain = skirt_ink * (1.0 - exp(-a / 0.05))
                             * pow(max(1.0 - a, 0.0), 1.10);
                // Lying along the surface, which is the whole of what this family
                // says: an element standing up here would be a small column, and
                // the column is on another quad.
                dust_part(at, vec2(out_at, up), r, r * skirt_flat,
                          vec2(1.0, 0.0), fk + 61.0, gain, dust_soft, a,
                          dens, lean, years);
            }
        }

        for (int k = 0; k < cone; k++) {
            float fk = float(k);
            float born = cone_stagger * ember_hash(vec2(fk, 71.0));
            float a = (time - born) / max(cone_life, 1e-3);
            if (a > 0.0 && a < 1.0) {
                // Where up the column this element sits, and therefore how wide
                // the column is where it is.
                float s = ember_hash(vec2(fk, 72.0));
                // <b>Up fast, down slowly - and the top comes down first.</b> The
                // exponent of the fall is scaled by how high the element is, so
                // the head collapses while the foot is still standing, which is
                // what a column of water does and what one envelope for all of
                // them cannot say.
                float fall = cone_fall * (1.35 - 0.55 * s);
                float env = a < cone_peak
                    ? pow(a / max(cone_peak, 1e-3), cone_rise)
                    : 1.0 - pow((a - cone_peak) / max(1.0 - cone_peak, 1e-3),
                                fall);
                env = clamp(env, 0.0, 1.0);
                float fast = 0.75 + 0.45 * ember_hash(vec2(fk, 73.0));
                float up = reach * fast * env * s;
                float span = mix(cone_foot, cone_head, s) * (0.55 + 0.60 * env);
                float wide = span * (2.0 * ember_hash(vec2(fk, 74.0)) - 1.0);
                // <b>And an element grows with the column, which is what keeps
                // the trunk solid while it extends.</b> The same ninety-six
                // elements are spread over three times the height at the top of
                // the envelope as at the start, so a fixed radius makes coverage
                // fall as the column rises - measured on the picture: solid at
                // the fifth frame, a chain of separate beads by the eighteenth.
                float r = cone_size * (0.55 + 0.90 * ember_hash(vec2(fk, 75.0)))
                          * (1.0 - cone_bloom + 2.0 * cone_bloom * s)
                          * (0.70 + 0.75 * env);
                float gain = cone_ink * (1.0 - exp(-a / 0.020))
                             * pow(max(1.0 - a, 0.0), 0.65);
                dust_part(at, vec2(wide, up), r, r * cone_long,
                          vec2(0.0, 1.0), fk + 71.0, gain, cone_soft, a,
                          dens, lean, years);
            }
        }

        if (dens <= 1e-4) {
            ALBEDO = vec3(0.0);
            ALPHA = 0.0;
        } else {
            float mass = dust_mass(at, dens);
            float lit = dust_lit(mass, lean);
            ALBEDO = mix(water_dark, water_lit, lit);
            ALPHA = clamp(mass * dust_ink * level * blast_footing(at), 0.0, 1.0);
        }
    }
}
";

    /// <summary>

    /// <summary>
    /// The fire at the foot: the filling burning at the waterline.
    ///
    /// <b>It exists, and the taxonomy said it would not.</b> That line - "no fire
    /// at all" - is true of a charge detonating under water, where whatever light
    /// there was is under the surface before anything is thrown. A shell bursting
    /// <em>on</em> water burns in the open air: five frames of the reference sheet
    /// have an orange core at the base of the column, bright enough to put a
    /// reflection on the water.
    ///
    /// <b>At the foot and small, which is what separates it from every other fire
    /// on this board.</b> The rack's is a column, the burst's is a ball, the plate
    /// burst's rolls off a facet; this one sits in the water at the bottom of a
    /// mass of smoke and never leaves it. What makes it read is that the smoke
    /// above it is dark - it is the only warm thing in the picture.
    ///
    /// Additive and premultiplied, for the fire's reason everywhere else here: it
    /// is light, and it has nothing to hide behind.
    /// </summary>
    private const string FireShader = @"
shader_type spatial;
render_mode unshaded, cull_disabled, blend_add, depth_draw_never;

FLAME_NOISE

FLAME_INK

BLAST_FRAME

// <b>The flash of the burst, at the waterline.</b> The dark column that used to
// stand over this is gone - it was read off a nine-frame sheet of a shell
// bursting on the surface, and the reference this is built to is a charge going
// off under one, which throws water and no smoke at all. What is kept of the fire
// is the flash: the round going off, which is the first thing in every burst on
// this board and is over before the column is up.
uniform int blaze = 6;
uniform float blaze_born = 0.0;
uniform float blaze_life = 0.16;
uniform float blaze_stagger = 0.03;
// How far it sits above the surface, and how far out along it. Low and tight:
// this is the charge, not a fire burning on the water.
uniform float blaze_seat = 0.030;
uniform float blaze_run = 0.070;
uniform float blaze_size = 0.100;
uniform float blaze_climb = 0.045;
uniform float blaze_squat = 0.80;
// Past one, and deliberately - every fire on this board spends this note: a white
// heart is bought by adding an orange body to itself until it clips. That is
// brightness, not a whiter colour.
uniform float blaze_gain = 3.20;

void fragment() {
    if (level <= 0.0) {
        ALBEDO = vec3(0.0);
        ALPHA = 0.0;
    } else {
        vec2 at = blast_at(UV);
        vec4 lit = vec4(0.0);

        for (int k = 0; k < blaze; k++) {
            float fk = float(k);
            float born = blaze_born + blaze_stagger * ember_hash(vec2(fk, 81.0));
            float a = (time - born) / max(blaze_life, 1e-3);
            if (a > 0.0 && a < 1.0) {
                float side = 2.0 * ember_hash(vec2(fk, 82.0)) - 1.0;
                float out_at = blaze_run * side * pow(a, 0.50);
                float up = blaze_seat + blaze_climb * pow(a, 0.70)
                           * ember_hash(vec2(fk, 83.0));
                float r = blaze_size * (0.55 + 0.95 * ember_hash(vec2(fk, 84.0)))
                          * (0.55 + 0.75 * a);
                lit = flame_over(lit,
                                 flame_blob(at, vec2(out_at, up), r,
                                            r * blaze_squat, vec2(0.0, 1.0),
                                            fk + 81.0,
                                            // Each body its own place on the ramp
                                            // as well as its own age -
                                            // ProcBlast's finding: read at one
                                            // stop the whole thing composites into
                                            // a flat orange disc.
                                            clamp(a + 0.30
                                                  * ember_hash(vec2(fk, 85.0)),
                                                  0.0, 1.0),
                                            blaze_gain * flame_life(a, 1.05)));
            }
        }

        // Premultiplied by the compositing above, so the alpha is spent and a
        // nought is handed over.
        ALBEDO = lit.rgb * (level * blast_footing(at));
        ALPHA = 1.0;
    }
}
";

    /// <summary>The three as they are compiled - the shared noise, the shared ink,
    /// the burst's frame and the burst's dust, put together in one place so a
    /// check can ask whether the seven effects on this board are one text.
    /// </summary>
    internal static readonly string FoamCode =
        FoamShader.Replace("FLAME_NOISE", Stage3D.EmberNoiseCode)
                  .Replace("FLAME_INK", Stage3D.FlameInk)
                  .Replace("BLAST_FRAME", ProcBlast.FrameCode)
                  .Replace("DUST_INK", ProcBlast.DustInk);

    /// <inheritdoc cref="FoamCode"/>
    internal static readonly string FireCode =
        FireShader.Replace("FLAME_NOISE", Stage3D.EmberNoiseCode)
                  .Replace("FLAME_INK", Stage3D.FlameInk)
                  .Replace("BLAST_FRAME", ProcBlast.FrameCode);

    private static readonly Shader Foaming = new() { Code = FoamCode };
    private static readonly Shader Blazing = new() { Code = FireCode };
}
