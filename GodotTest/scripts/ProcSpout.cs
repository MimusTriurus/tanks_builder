using Godot;

namespace TankSpriteTest;

/// <summary>
/// A round going into water: the plume it throws up and the spray that comes
/// back down - <c>water_he</c> in <c>docs/blast.md</c>.
///
/// <b>What made this item a bug rather than a gap.</b> The other five unbuilt
/// entries in that taxonomy are pictures nobody has drawn yet; this one had a
/// picture already and it was the wrong one. A shell into the pond raised
/// <see cref="ProcBlast"/> - a cone of thrown earth, a collar of dust and a
/// dirty column - because <see cref="TankTick.Landed"/> hands the board a point
/// and a lift and the board never asked whether that point was wet. So the fix
/// is one question in <see cref="Stage3D.Land"/> and this class is the answer to
/// it.
///
/// <b>It is the driest of the five bursts and the only one with no fire at
/// all.</b> Not dimmed fire, not a smaller flash: there is no additive quad in
/// this file. Every other burst on this board is two quads - a dark mass and the
/// light in front of it - and the reason is that an explosion in air makes light.
/// A shell going into a pond makes a hole; whatever light there was is under the
/// water by the time anything is thrown. So this is one quad, which makes it the
/// simplest effect on the board and the only one whose shader has no
/// <c>blend_add</c>.
///
/// <b>And its white is a colour rather than a brightness, which is this board's
/// own rule read backwards for the one case it does not cover.</b>
/// <see cref="ProcRack"/>, <see cref="ProcSlam"/> and <see cref="ProcBlast"/> all
/// buy their white heart by adding an orange body to itself until it clips -
/// because they are light, and an additive layer's white <em>is</em> that clip.
/// Foam is not light, it is a surface: air whipped into water, and it is white
/// the way paint is white. So the pale stop of the shared dust ink goes to nearly
/// one here, and that is correct exactly once on this board.
///
/// <b>The dark stop is the pond's own colour and is measured off it</b>, not
/// picked: <c>shallow</c> in the computed surface's shader is
/// <c>(0.24, 0.52, 0.50)</c>, and water in the air is the pond with light through
/// it - so the dark stop is that lifted toward white rather than a grey that
/// happens to look wet. A plume whose shadowed side is grey reads as dust, which
/// is the failure this whole class exists to correct.
///
/// <b>Water comes back down, and nothing else on this board does.</b> Dust
/// climbs and thins, fire climbs and dies, a clod of earth arcs and lands
/// <em>on</em> the ground it came from. Spray arcs and lands in the surface it
/// came out of, and the surface is what cuts it: <see cref="Root"/> is nought
/// here, alone among the effects on this board, because every other one stands
/// <em>on</em> a face and this one comes <em>out</em> of one. See
/// <c>blast_footing</c>, which is what does the cutting and needed no change to
/// do it.
///
/// <b>There is no ring layer in here, and its absence is the finding.</b> Every
/// other burst lays rings on a plane of its own (<see cref="ProcBlast.RingCode"/>,
/// which <see cref="ProcRack"/> takes outright) because the ground it went off on
/// cannot answer for itself. Water can: <see cref="Ripples"/> is a damped wave
/// field with reflecting banks, and a hole punched in it spreads a ring at the
/// right speed and comes back off the shore for free. See
/// <see cref="Ripples.Strike"/>. A drawn ring beside a solved one would be two
/// accounts of one surface - the double model this project spends its docstrings
/// refusing - and the drawn one would be the account that walked through the
/// bank.
///
/// <b>And it digs nothing.</b> Sixth time that sentence is written across these
/// classes and the first time it needs no argument at all: a hole in water is the
/// effect, and it is over in a second and a half.
///
/// Same texts as the rest: <see cref="ProcBlast.FrameCode"/> for the quad and the
/// ballistic throw, <see cref="ProcBlast.DustInk"/> for what a mass of many
/// elements is and how one surface is lit off it, <see cref="Stage3D.FlameInk"/>
/// for the section an element is drawn with. <see cref="Stage3D.FlameInk"/> is in
/// here for its geometry and none of its colour - <c>flame_reach</c> is what
/// <c>dust_part</c> is built on.
/// </summary>
public sealed partial class ProcSpout : Node3D
{
    /// <summary>
    /// How high it throws, in tile widths - <see cref="ProcBlast.Reach"/>'s
    /// number and its argument entire: every other length in here is a ratio to
    /// it, so a bigger round is this plume at a bigger <see cref="Might"/> rather
    /// than a second set of numbers.
    ///
    /// <b>It governs the BODY of water and not the vapour</b>, which is the
    /// structure of an underwater burst and is read off a reference film: the bulk
    /// goes up a fraction of what the vapour does and is back in the pond while
    /// the vapour still stands over it. The vapour's height is
    /// <c>column_climb</c>, and it is more than twice this.
    /// </summary>
    public float Reach = ReachDefault;

    /// <summary>The two numbers something else has to be able to ask for -
    /// <see cref="ProcBlast.ReachDefault"/>'s reason: a bench that wants to reset
    /// a dial needs the tuned value, and reading it off a live instance gets
    /// whatever the last shot left.</summary>
    /// <inheritdoc cref="Reach"/>
    public const float ReachDefault = 0.40f;

    /// <summary>
    /// How long the whole event runs.
    ///
    /// Shorter than the ground burst's 2.4s and longer than the rack's 1.6.
    /// Nothing here is left burning and nothing hands over to anything - the
    /// plume falls back, the spray follows it in, and what is last to go is the
    /// haze standing over the entry. Water leaves no mark, so when it is finished
    /// the cell is exactly what it was.
    /// </summary>
    public const float LifeDefault = 2.00f;

    /// <summary>
    /// The quad, in tile widths: half-width either side of the seat, and height
    /// above it.
    ///
    /// Taller than wide, and by less than the rack: a plume is a column with a
    /// crown round its foot, so the crown sets the width and the stem sets the
    /// height. Checked against <see cref="Bounds"/>, which is asked of the model.
    /// </summary>
    public float Flank = 0.62f;
    public float Tall = 1.45f;

    /// <summary>
    /// How far above the contact point the effect is seated, and the band it
    /// fades out over below it.
    ///
    /// <b>Nought, alone on this board, and it is the model rather than an
    /// omission.</b> Every other burst is seated a little above the face it
    /// stands on so that its elements do not draw through the ground - the ground
    /// being opaque and beneath them. Here the face <em>is</em> the water and the
    /// water is where the plume goes back to, so the surface is exactly where
    /// this effect ends. The fade band is what makes that a waterline rather than
    /// a cut: a couple of pixels over which foam thins into the surface it is
    /// falling into.
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
    private ShaderMaterial? _foamInk;
    private Vector3 _nudge;

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

        float spread = ProcBlast.Uniform(SpoutCode, "spread", 1.15f);

        // <b>The thrown water, and it is the only family <paramref name="reach"/>
        // governs</b> - the burst's arrangement exactly: the cone is a ratio of
        // the throw, and the collar and the column are absolute tile widths,
        // because how far dust is pushed along the ground is not a share of how
        // hard earth was thrown.
        //
        // Two elements, not one, which is why the pads are taken apart: the apex
        // of blast_throw is <c>reach*fast</c> at the middle of a life, and its
        // furthest is <c>reach*spread*sin(lean)</c> at the end of one, and no
        // element is at both.
        float born = Uniform("clod_born");
        float clodR = Uniform("clod_size") * 2.10f;
        float clodLong = clodR * (1.0f + Uniform("clod_stretch"));
        Reach(new Vector2(born + reach * spread * Mathf.Sin(Uniform("clod_wide")),
                          reach + born * 0.45f),
              clodR, clodLong);

        // The collar along the surface: the widest thing low down, and its long
        // axis is the across one because it lies flat.
        float collarR = Uniform("collar_size") * 1.45f * 1.55f;
        Reach(new Vector2(Uniform("collar_spread"),
                          Uniform("collar_climb") * 1.20f),
              collarR * Uniform("collar_flat"), collarR);

        // The column: the tallest thing here, and what the quad's height is cut
        // to.
        // The widest an element of it gets is at its foot, where the taper is
        // at 1.20 - see the column loop, which is the one place this file departs
        // from the burst's own code.
        float columnR = Uniform("column_size") * 1.45f * 1.20f;
        Reach(new Vector2(Uniform("column_sway"), Uniform("column_climb") * 1.25f),
              columnR, columnR * 1.25f);

        return (along, up);
    }

    /// <summary>The one shader's own numbers, by name - so <see cref="Bounds"/>
    /// asks the shader what it says rather than carrying a second copy. One
    /// source, because there is one quad.</summary>
    private static float Uniform(string name) =>
        ProcBlast.Uniform(SpoutCode, name, 0.0f);

    /// <summary>The quad, in screen px: from <c>-Foot</c> across <c>Size</c>,
    /// which is what <see cref="Stage3D.Stem"/> takes -
    /// <see cref="ProcBlast.Quad"/> entire, and static for its reason: how big a
    /// splash is can be asserted with no board under it.</summary>
    public static (Vector2 Foot, Vector2 Size) Quad(float tile, float flank,
                                                    float tall)
        => ProcBlast.Quad(tile, flank, tall);

    /// <summary>
    /// Build the quad. <paramref name="tile"/> is the hex's own width in screen
    /// px - every length above is in those - and the two camera terms are the
    /// field's, handed in rather than read so this needs no board.
    /// </summary>
    public void Build(float tile, float squash, float rise)
    {
        _tile = Mathf.Max(tile, 1.0f);
        (Vector2 foot, Vector2 size) = Quad(tile, Flank, Tall);
        _foamInk = Ink();
        _foam = new MeshInstance3D
        {
            Mesh = Stage3D.Stem(foot, size, rise),
            SortingUseAabbCenter = false,
            MaterialOverride = _foamInk,
            Visible = false,
        };
        AddChild(_foam);
        // Toward the camera by the board's own clearance - the burst's
        // arrangement. One quad, so there is no second nudge and nothing here can
        // sort against itself.
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
    /// bottom of the pond and the crown would be drawn under the surface.
    /// </summary>
    public void Sit(Vector2 ground, float top, float squash, float rise)
    {
        _seat = Stage3D.Trunk(ground, top, 0.0f, squash, rise);
        Stand();
    }

    /// <summary>The seat and the size together, the quad's own nudge divided back
    /// out - <see cref="ProcRack.Stand"/>'s reason: a clearance is a clearance
    /// whatever size the effect is.</summary>
    private void Stand()
    {
        Transform = _seat.ScaledLocal(Vector3.One * _might);
        if (_foam is not null)
            _foam.Position = _nudge / _might;
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
        if (!on)
            return;

        _foamInk?.SetShaderParameter("time", _clock);
    }

    /// <summary>One number of the shader, live - read out of the material rather
    /// than off the text, so a panel shows what is running.
    /// <see cref="ProcRack.Dial(ProcRack.Part, string)"/>'s arrangement with one
    /// part instead of three.</summary>
    public float Dial(string uniform)
    {
        if (_live.TryGetValue(uniform, out float held))
            return held;
        return ProcBlast.Uniform(SpoutCode, uniform, 0.0f);
    }

    /// <inheritdoc cref="Dial(string)"/>
    public void Dial(string uniform, float value)
    {
        _live[uniform] = value;
        _foamInk?.SetShaderParameter(uniform, value);
    }

    private readonly System.Collections.Generic.Dictionary<string, float> _live
        = new();

    /// <summary>
    /// Where this mass sits on its own light ramp, and which way thickness moves
    /// it - the shared dust ink's two numbers, overridden here and named so a
    /// check can assert the <em>sign</em>.
    ///
    /// <b>Negative, alone on this board, and it is the physics rather than a
    /// preference.</b> <see cref="ProcBlast.DustInk"/> lights a mass as
    /// <c>seat + ridge*side*(1-mass) - dense*mass</c>: earth in the air darkens
    /// as it thickens, because earth absorbs. Water brightens as it thickens,
    /// for the same reason a thin film of it is clear - light scatters out of it
    /// rather than being eaten - so the term changes sign and the core becomes
    /// the brightest part of the plume instead of the darkest.
    /// </summary>
    public const float Seat = 0.62f;

    /// <inheritdoc cref="Seat"/>
    public const float Dense = -0.10f;

    private ShaderMaterial Ink()
    {
        var ink = new ShaderMaterial
        {
            Shader = Foaming,
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
        // <b>It hides the water under it, which the earth's dust deliberately
        // does not.</b> ProcBlast.DustInk's cap is 0.72 because earth in the air
        // is not a wall and a mass that saturates to one read as a hole cut in
        // the board. Foam is nearer a wall than dust is - it is water, and there
        // is water behind it - and the pond it stands on is the darkest surface
        // on this board, so a plume that lets it through comes out translucent
        // grey rather than white.
        ink.SetShaderParameter("dust_ink", 0.85f);
        // <b>And the sign of the shading is flipped, which is the one number in
        // the shared ink that water cannot take as written.</b>
        // ProcBlast.DustInk lights a mass by seat + ridge - dense*mass: a cloud
        // of earth <em>darkens</em> as it thickens, because earth absorbs. Water
        // does the opposite - a thick plume is white for the same reason a thin
        // one is clear, which is that light scatters out of it rather than being
        // eaten - so the dense term goes negative and the core becomes the
        // brightest part instead of the darkest.
        //
        // Measured before it was believed: read at the earth's stops the plume
        // came out a teal shape with a white rim, which reads as something
        // rising <em>under</em> the surface rather than standing out of it.
        // <b>And it is a lean the other way, not a flood.</b> Taken to
        // seat 0.72 with dense at -0.18 the core sat at 0.90 on a mass that
        // saturates to one, so the plume came out as flat white paint: no bands,
        // no interior, and the three-step quantiser the whole board is shaded
        // with had nothing left to quantise. Seated where the earth sits it and
        // leaned by half as much, the core is bright, the thin parts carry the
        // shading, and the bands come back.
        ink.SetShaderParameter("dust_seat", Seat);
        ink.SetShaderParameter("dust_dense", Dense);
        // And the ridge comes down with it: the sunward edge no longer has to
        // carry the whole of the white, so a term tuned to do that now only
        // shapes it.
        ink.SetShaderParameter("dust_ridge", 0.62f);
        // <b>And torn harder than earth is.</b> A cloud of dust is ragged at its
        // rim because the rim is where it thins out; a plume of water is ragged
        // because it is breaking up into drops as it rises, which is a coarser
        // thing and happens all through it. The shared field is what does the
        // tearing either way - see ProcBlast.DustInk - so this is one number
        // rather than a second mechanism.
        ink.SetShaderParameter("dust_tear", 0.52f);
        // <b>And the throw is the burst's throw, left alone.</b> It was
        // overridden here once, when the spray was twenty long crisp drops and
        // half of them landed on the dry bank; what was wrong was the drops, not
        // the parabola they were on.
        ink.SetShaderParameter("dust_grain", 16.0f);
        return ink;
    }

    /// <summary>
    /// The plume: the crown that stands up round the entry, the stem that comes
    /// out of the middle of it, the spray that arcs back in and the haze that
    /// outlives all three.
    ///
    /// <b>One pass that adds up water and one that shades the result</b> -
    /// <see cref="ProcBlast.DustInk"/>'s finding, and it is the same finding here
    /// for the same reason: forty elements composited over one another are forty
    /// things with forty rims, and what a plume is is one volume of varying
    /// thickness.
    ///
    /// <b>The order of the four families is the order of the event, and it is
    /// what makes it read as water.</b> The crown is instant and outward, the
    /// stem is late and upward, the spray falls all the way through, and the haze
    /// is left. A splash drawn with the four windows on top of one another is a
    /// white bush - which is what this looked like when the crown and the stem
    /// shared a life, the failure <see cref="ProcRack"/> paid for once already
    /// with its jets and its fireball.
    /// </summary>
    private const string FoamShader = @"
shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_never;

FLAME_NOISE

FLAME_INK

BLAST_FRAME

DUST_INK

// <b>The body of water, and it is the family that rises less and comes back
// fast.</b> That pairing is the whole structure of an underwater burst, and it was
// read off a reference film rather than reasoned: the bulk of the water goes up a
// fraction of what the vapour does and is back in the pond while the vapour is
// still standing over it. So this family is ballistic - it rises and falls under
// blast_throw, which is what earth's cone already did - and the vapour above it
// does not fall at all. See column.
//
// <b>Bigger, softer and more numerous than the burst's clods, because it is a
// mass rather than a fringe.</b> Earth's cone IS the thrown clods and its column
// is a thin tail behind them; here the cone has to be the trunk, so its elements
// are two thirds larger, the profile is nearer the cloud's than the clod's, and
// the count is what makes them overlap into one volume. The first version of this
// was twenty long crisp drops on their own paths and every one read as a separate
// white shape - reported, in those words, as cartoonish.
uniform int clods = 52;
// <b>The burst's own, after a pass spent shortening it for the wrong reason.</b>
// The cone fans out towards the end of its life and its elements stop overlapping,
// which on white foam reads as lumps rather than as spray - and a shorter life
// makes that WORSE, not better, because the age is a fraction of it: at 0.46 the
// same element is further along its own arc at the same instant. What carries the
// mass is not this family at all; see column.
uniform float clod_life = 0.85;
uniform float clod_stagger = 0.05;
uniform float clod_narrow = 0.16;
// <b>Narrower than earth's, and for the reason the size is bigger: this is a
// trunk.</b> The wide cone is what a crater's walls do to earth leaving them; the
// body of water lifted by a charge under it goes up as a column and only its
// outside sprays. What leaves sideways is the collar and the fastest few of these.
uniform float clod_wide = 0.70;
uniform float clod_size = 0.040;
uniform float clod_stretch = 1.35;
// <b>The burst's gain, restored after being lowered.</b> Cohesion here comes from
// DENSITY, not from restraint: the shared ink saturates a pile-up, so a cone whose
// elements overlap is one mass and the same cone at three quarters of the gain is
// forty-four visible lumps. Lowering this was an attempt to hide the late thinning
// and it broke the early frames instead - the fix for the late half is that there
// is no late half. See clod_life.
uniform float clod_ink = 1.10;
// How far off the middle an element starts - the burst's number and its reason:
// everything leaving through the exact centre makes the base of the column a
// needle and the cone a symmetrical flower.
uniform float clod_born = 0.060;
// <b>Softer than a clod of earth and short of the cloud, which is what a body of
// water in the air actually is.</b> The burst keeps 1.85 because a clod is a small
// hard body; this family is the trunk, so it wants most of the cloud's transition
// and none of the crisp 1.15 the first version reached for - that number is what
// drew pills.
uniform float clod_soft = 2.20;

// The collar: what is pushed out along the surface, and the half of the event
// that says which cell it happened on. Elements lie flat - wide across and
// shallow up - because under this camera a ring on the ground is an ellipse.
// <b>Climbing rather more than the burst's, and that is the crown.</b> Dust is
// shoved along the ground; water thrown out of a hole in a surface stands up as it
// goes, and the angle of that sheet is the second thing that says a SURFACE was
// hit.
uniform int collar = 24;
uniform float collar_life = 0.55;
uniform float collar_born = 0.05;
uniform float collar_stagger = 0.12;
uniform float collar_spread = 0.26;
uniform float collar_climb = 0.16;
// The burst's own, after being widened by a third for one pass: at 0.050 with
// this stretch the family reached most of a tile either way and the middle of the
// event was a flat white slab lying across the pond - which is the hedge the
// burst's own note warns about, arrived at from the other direction.
uniform float collar_size = 0.038;
uniform float collar_flat = 2.30;
uniform float collar_ink = 1.00;

// <b>The vapour and the fine spray: the tallest thing here by a distance, and the
// slowest to leave.</b> This is the other half of the structure the reference
// shows - it goes up twice as far as the body of water does and it does not come
// back, because what is in it is too fine to fall. Earth's column is the same
// family doing the same thing for a different reason: dust hangs because it is
// ground fine enough to be carried. Water arrives at the same picture by boiling.
//
// So the two are told apart by height and by fate, not by colour: the body rises
// less and drops fast, the vapour rises most and thins slowly. Read at the body's
// own height - which is where this started, at 0.52 against a throw of 0.42 - the
// two families sat on top of each other and the plume had no column at all.
uniform int column = 26;
uniform float column_life = 1.45;
uniform float column_born = 0.02;
uniform float column_stagger = 0.26;
uniform float column_sway = 0.10;
uniform float column_climb = 0.95;
uniform float column_size = 0.085;
uniform float column_ink = 1.00;

// <b>What water in the air is, at its darkest and fully lit</b> - and this pair
// is the whole of what makes the burst's own model into a plume. The dark stop is
// measured off the pond rather than picked: Stage3D's computed surface declares
// shallow = (0.24, 0.52, 0.50), and water in the air is that with light through
// it, because what makes a plume pale is the air whipped into it.
uniform vec3 water_dark : source_color = vec3(0.455, 0.655, 0.645);
// <b>And the pale stop is very nearly white, which is correct exactly once on
// this board.</b> Every other effect buys white by clipping an additive sum, and
// a white colour there would only wash the fire out. Foam is a surface, not
// light: it is white the way paint is.
uniform vec3 water_lit : source_color = vec3(0.965, 0.985, 1.0);

void fragment() {
    if (level <= 0.0) {
        ALBEDO = vec3(0.0);
        ALPHA = 0.0;
    } else {
        vec2 at = blast_at(UV);
        // One pass that adds up water and one that shades the result - the
        // burst's finding, unchanged and the reason this file is the burst: an
        // element hands back how much it puts in the way and which way its
        // surface leans, and a single surface is lit at the end. Compositing each
        // over the last gives every one of them its own rim, and then the eye
        // counts them.
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
                // <b>Thinner the higher it is, which is the one line of the
                // burst's own loop that water turns round.</b> Earth's column
                // widens with age and mushrooms, because dust spreads as it rises
                // and there is nothing feeding it from below. A vapour column over
                // a plume is fed from below all the while, so it is widest at its
                // foot and tapers - and read the earth's way this family drew a
                // head wider than its own trunk with a waist between them, which
                // is a mushroom cloud rather than a spout.
                // <b>Not called ''tall'': that is the quad's own height, declared
                // by the shared frame.</b> A local of that name compiles to nothing
                // on this backend and the whole quad draws solid black - which is
                // how a failed shader looks here, and it looks nothing like a
                // mistake in a number.
                float high = clamp(up / max(column_climb, 1e-3), 0.0, 1.0);
                float r = column_size * (0.55 + 0.9 * ember_hash(vec2(fk, 54.0)))
                          * (1.20 - 0.60 * high);
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
                // Out along the surface and up, the second slower than the first:
                // a burst spreads before it climbs, and a mass doing both at one
                // rate is a ball.
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
                // The shared parabola, stated as an apex - see blast_throw. On the
                // surface exactly at the end of its life, which for water is the
                // whole of what happens to it: a drop does not settle, it is
                // absorbed.
                vec4 thrown = blast_throw(a, reach, fast, leaning, side);
                vec2 p = seat + thrown.xy;
                vec2 flow = thrown.zw;
                float r = clod_size * (0.40 + 1.7 * ember_hash(vec2(fk, 75.0)))
                          * (1.0 - 0.25 * a);
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
            // are the shared dust ink's, and the two colours and the SIGN of the
            // thickness term are this effect's own. See ProcSpout.Dense.
            float mass = dust_mass(at, dens);
            float lit = dust_lit(mass, lean);
            ALBEDO = mix(water_dark, water_lit, lit);
            ALPHA = clamp(mass * dust_ink * level * blast_footing(at), 0.0, 1.0);
        }
    }
}
";

    /// <summary>The plume as it is compiled - the shared noise, the shared ink,
    /// the burst's frame and the burst's dust, put together in one place so a
    /// check can ask whether the six effects on this board are one text.</summary>
    internal static readonly string SpoutCode =
        FoamShader.Replace("FLAME_NOISE", Stage3D.EmberNoiseCode)
                  .Replace("FLAME_INK", Stage3D.FlameInk)
                  .Replace("BLAST_FRAME", ProcBlast.FrameCode)
                  .Replace("DUST_INK", ProcBlast.DustInk);

    private static readonly Shader Foaming = new() { Code = SpoutCode };
}
