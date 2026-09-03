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
    public const float ReachDefault = 0.52f;

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
    public const float LifeDefault = 2.80f;

    /// <summary>
    /// The quad, in tile widths: half-width either side of the seat, and height
    /// above it.
    ///
    /// Checked against <see cref="Bounds"/>, which is asked of the model - the
    /// quad holds what the model CAN produce, not what it happens to draw.
    /// </summary>
    public float Flank = 0.78f;
    public float Tall = 1.55f;

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

    private MeshInstance3D? _smoke;
    private MeshInstance3D? _foam;
    private MeshInstance3D? _fire;
    private ShaderMaterial? _smokeInk;
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
    public const float Seat = 0.62f;

    /// <inheritdoc cref="Seat"/>
    public const float Dense = -0.10f;

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

        float spread = ProcBlast.Uniform(FoamCode, "spread", 1.15f);

        // The spikes, at the far end of the widest one's throw and at the apex of
        // the fastest one's. Two elements, not one, which is why the pads are
        // taken apart: the apex of blast_throw is reach*fast at the middle of a
        // life and its furthest is reach*spread*sin(lean) at the end of one, and
        // no element is at both.
        float born = Uniform("spike_born");
        float spikeR = Uniform("spike_size") * 2.00f;
        float spikeLong = spikeR * Uniform("spike_stretch") * 1.55f;
        Reach(new Vector2(born + reach * spread * Mathf.Sin(Uniform("spike_wide")),
                          reach + born * 0.45f),
              spikeR, spikeLong);

        // The skirt on the surface: the widest thing here, and its long axis is
        // the across one because it lies flat.
        float skirtR = Uniform("skirt_size") * 1.45f * 1.55f;
        Reach(new Vector2(Uniform("skirt_run"), Uniform("skirt_climb") * 1.20f),
              skirtR * Uniform("skirt_flat"), skirtR);

        // The column: the tallest thing here, and what the quad's height is cut
        // to. It widens with height, so its own cone is part of the reach.
        float smokeR = Uniform("smoke_size") * 1.45f * 1.60f;
        float smokeUp = Uniform("smoke_climb") * 1.30f;
        Reach(new Vector2(Uniform("smoke_sway") + smokeUp * Uniform("smoke_cone"),
                          smokeUp),
              smokeR, smokeR * 1.25f);

        // And the fire, which does not travel: it sits at the foot and grows.
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
                          ProcBlast.Uniform(SmokeCode, name,
                                            ProcBlast.Uniform(FireCode, name,
                                                              0.0f)));

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

        _smokeInk = Ink(Smoking, Part.Smoke);
        _foamInk = Ink(Foaming, Part.Foam);
        _fireInk = Ink(Blazing, Part.Fire);
        _smoke = Slab(shape, _smokeInk);
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
        if (_smoke is not null)
            _smoke.Position = _nudge / _might;
        if (_foam is not null)
            _foam.Position = _nudge * 2.0f / _might;
        if (_fire is not null)
            _fire.Position = _nudge * 3.0f / _might;
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
        if (_smoke is not null)
            _smoke.Visible = on;
        if (_foam is not null)
            _foam.Visible = on;
        if (_fire is not null)
            _fire.Visible = on;
        if (!on)
            return;

        _smokeInk?.SetShaderParameter("time", _clock);
        _foamInk?.SetShaderParameter("time", _clock);
        _fireInk?.SetShaderParameter("time", _clock);
    }

    /// <summary>Which of the three a number belongs to.
    /// <see cref="Part.Frame"/> is the pair the quad itself declares.</summary>
    public enum Part { Frame, Smoke, Foam, Fire }

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
            _smokeInk?.SetShaderParameter(uniform, value);
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
        Part.Smoke => SmokeCode,
        Part.Fire => FireCode,
        _ => FoamCode,
    };

    private ShaderMaterial? Coat(Part part) => part switch
    {
        Part.Smoke => _smokeInk,
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
            ink.SetShaderParameter("dust_ridge", 0.62f);
            // <b>Torn harder than earth, and this is the number that makes a sheet
            // of water into fingers.</b> Dust is ragged at its rim because that is
            // where it thins; thrown water is ragged all through, because it is
            // tearing into drops as it goes.
            ink.SetShaderParameter("dust_tear", 0.62f);
            ink.SetShaderParameter("dust_grain", 18.0f);
        }
        if (part == Part.Smoke)
        {
            // <b>The smoke takes the shared ink as written, and that is the point
            // of it being a second quad.</b> Every number in ProcBlast.DustInk was
            // tuned on a mass of earth and smoke in the air: the profile, the
            // tearing, the packing, the seat, the banding and the sign of the
            // thickness term. Nothing here overrides any of them except how much
            // of the board it may hide.
            ink.SetShaderParameter("dust_ink", 0.92f);
            // <b>Torn harder and coarser than the burst's, because this column is
            // eight times the size of the one that number was tuned on.</b> A
            // tearing field at the earth's grain over a mass this big is a fine
            // stipple, and what a billowing column reads by is lumps the size of
            // its own puffs.
            ink.SetShaderParameter("dust_tear", 0.50f);
            ink.SetShaderParameter("dust_grain", 8.0f);
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

// The spikes: thrown water, on the shared parabola. Sharp, long, radiating, and
// the whole picture for the first fifth of a second.
uniform int spikes = 40;
// <b>Short, because the spray is over before the column is up.</b> On the
// reference the white has stopped climbing by the third frame of nine and is a
// skirt from there on; what is long here is the skirt and the smoke.
uniform float spike_life = 0.55;
// <b>Spread wide, because a spray that all leaves at once is a spray that
// detaches.</b> At 0.045 all forty went up together and by a quarter of a second
// there was nothing at the surface at all - the same failure the column of an
// earlier pass had, and the same answer the burst already uses: the family holds
// on to its own foot by the stagger of its births, not by any mechanism.
uniform float spike_stagger = 0.20;
// The narrowest and the widest launch, off straight up in radians. Wide: a hole
// in a surface has no walls to aim what leaves it, which is what a crater has and
// why earth's cone is narrower than this.
uniform float spike_narrow = 0.22;
uniform float spike_wide = 1.05;
// <b>Thin and very long, which is what a spike is.</b> The stretch is the largest
// on this board after the rack's jets, and unlike those it is on an element the
// size of a drop rather than of a tongue.
uniform float spike_size = 0.017;
uniform float spike_stretch = 4.20;
uniform float spike_ink = 1.25;
// How far off the middle one starts - the burst's number and its reason:
// everything leaving through the exact centre makes the base a needle.
uniform float spike_born = 0.055;
// <b>The crispest profile in the project, and it is the point of the family.</b>
// dust_soft is 2.55 because a cloud is in transition everywhere and a clod of
// earth keeps 1.85 because it is a small body; a finger of water has an edge, and
// the whole read of thrown water is that its edges are sharp. Short of the
// flame's 0.5, which has infinite slope at the rim.
uniform float spike_soft = 0.95;

// The skirt: what the spray leaves lying on the surface, spreading. Flat, both
// ways, and the longest-lived thing in the water - on the reference it is still
// widening in the last frame, after the fire is out and the column has paled.
uniform int skirt = 26;
uniform float skirt_life = 1.90;
uniform float skirt_born = 0.03;
uniform float skirt_stagger = 0.30;
uniform float skirt_run = 0.34;
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

        for (int k = 0; k < spikes; k++) {
            float fk = float(k);
            float born = spike_stagger * ember_hash(vec2(fk, 71.0));
            float a = (time - born) / max(spike_life, 1e-3);
            if (a > 0.0 && a < 1.0) {
                float leaning = mix(spike_narrow, spike_wide,
                                    ember_hash(vec2(fk, 72.0)));
                float side = ember_hash(vec2(fk, 73.0)) < 0.5 ? -1.0 : 1.0;
                float fast = mix(0.45, 1.0, ember_hash(vec2(fk, 74.0)));
                vec2 seat = vec2(spike_born * (2.0 * ember_hash(vec2(fk, 76.0)) - 1.0),
                                 spike_born * 0.45 * ember_hash(vec2(fk, 77.0)));
                // The shared parabola, stated as an apex - see blast_throw. Back on
                // the surface exactly at the end of its life, which for water is
                // the whole of what happens to it: a drop does not settle, it is
                // absorbed.
                vec4 thrown = blast_throw(a, reach, fast, leaning, side);
                vec2 p = seat + thrown.xy;
                vec2 flow = thrown.zw;
                float r = spike_size * (0.45 + 1.55 * ember_hash(vec2(fk, 75.0)));
                // <b>Longest while it is being thrown and shortening as it
                // stops.</b> A finger of water is drawn by its own travel: at the
                // top of an arc it is a short blob and on the way out it is a
                // streak, which is what every photograph of a splash shows and
                // what a fixed length cannot say.
                float draw = spike_stretch * (0.45 + 1.10 * fast)
                             * (1.0 - 0.55 * a);
                dust_part(at, p, r, r * max(draw, 1.0), flow,
                          fk + 71.0, spike_ink * (1.0 - smoothstep(0.55, 1.0, a)),
                          spike_soft, a, dens, lean, years);
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
    /// The smoke: the column that comes out of the middle and is the mass of this
    /// event.
    ///
    /// <b>The burst's own column, and it takes the shared ink as written.</b> That
    /// ink was tuned on exactly this - a mass of smoke and earth in the air - so
    /// nothing here overrides its profile, its tearing, its packing, its seat or
    /// the sign of its thickness term. What is this file's own is that the column
    /// is the <em>subject</em> rather than the tail: it is born late, it outlives
    /// everything, it widens as it rises, and it pales as it dies.
    ///
    /// <b>It mushrooms, and that is the reference rather than the earth.</b> A
    /// pass of this class had the column taper upward, on the argument that a
    /// vapour plume is fed from below; the sheet shows a billowing head wider than
    /// its own foot from the third frame on, because what is rising is hot gas and
    /// it entrains as it goes.
    /// </summary>
    private const string SmokeShader = @"
shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_never;

FLAME_NOISE

FLAME_INK

BLAST_FRAME

DUST_INK

// The column. Born after the water, because on the reference the first two frames
// have none of it at all - and that gap is what makes the spray read as the thing
// that happened first.
uniform int smoke = 44;
uniform float smoke_life = 2.30;
uniform float smoke_born = 0.09;
uniform float smoke_stagger = 0.40;
// How high it goes, in tile widths. <b>Not a ratio of the throw</b>: the water is
// thrown once and comes back, and this keeps rising long after it is gone - so the
// two heights are two facts and the taller of them is this one.
uniform float smoke_climb = 0.86;
// How far off the middle it leans as it climbs, and how much it sways. The cone is
// what makes the head wider than the foot.
uniform float smoke_cone = 0.26;
uniform float smoke_sway = 0.10;
// <b>Half again the burst's column, because here the column IS the burst.</b>
// Read at the earth's size it came out a thin wisp trailing off the pond - which
// is what the burst's column is for, being its tail. On the reference this is
// the largest thing in seven frames out of nine.
uniform float smoke_size = 0.130;
uniform float smoke_ink = 1.75;

// <b>What the smoke of a shell over water is, at its darkest and fully lit.</b>
// Grey and cool, against the burst's warm earth: what is in this column is
// propellant and burnt filling with water vapour through it, not the ground. Read
// at the earth's stops it came out tan and the picture was a burst on a beach.
// Still short of a real black, for ProcBlast.DustInk's measured reason - a
// saturating mass at a black stop reads as a hole cut in the board.
uniform vec3 smoke_dark : source_color = vec3(0.150, 0.148, 0.152);
uniform vec3 smoke_lit : source_color = vec3(0.620, 0.612, 0.600);
// <b>How much of the way to its lit stop the whole column drifts as it dies.</b>
// The last two frames of the reference are the same cloud gone pale - it is
// thinning, and thin smoke is lit through. Written as a lift of the ramp rather
// than as a second colour, so there is one pair of stops and this is where on it
// the cloud sits.
// <b>A quarter, not a half.</b> Read at 0.45 the column was pale from the moment
// it appeared - the drift is off the MEAN age of the mass, and a column being fed
// from below has old elements in it throughout, so a large lift makes the whole
// cloud grey rather than making its end grey. On the reference the pallor is the
// last two frames of nine and nothing before them.
uniform float smoke_pale = 0.22;

void fragment() {
    if (level <= 0.0) {
        ALBEDO = vec3(0.0);
        ALPHA = 0.0;
    } else {
        vec2 at = blast_at(UV);
        float dens = 0.0;
        vec2 lean = vec2(0.0);
        float years = 0.0;

        for (int k = 0; k < smoke; k++) {
            float fk = float(k);
            float born = smoke_born + smoke_stagger * ember_hash(vec2(fk, 51.0));
            float a = (time - born) / max(smoke_life, 1e-3);
            if (a > 0.0 && a < 1.0) {
                float fast = 0.35 + 0.95 * ember_hash(vec2(fk, 52.0));
                float up = smoke_climb * fast * pow(a, 0.62);
                // Out with height rather than with age: the head is wide because it
                // is high, which is what entrainment looks like from outside.
                float wide = up * smoke_cone
                             * (2.0 * ember_hash(vec2(fk, 53.0)) - 1.0)
                           + smoke_sway * (2.0 * ember_hash(vec2(fk, 56.0)) - 1.0)
                             * pow(a, 0.75);
                float r = smoke_size * (0.55 + 0.9 * ember_hash(vec2(fk, 54.0)))
                          * (0.45 + 1.15 * a);
                float gain = smoke_ink * (1.0 - exp(-a / 0.09))
                             * pow(max(1.0 - a, 0.0), 1.20);
                dust_part(at, vec2(wide, up), r, r * 1.25, vec2(0.0, 1.0),
                          fk + 51.0, gain, dust_soft, a, dens, lean, years);
            }
        }

        if (dens <= 1e-4) {
            ALBEDO = vec3(0.0);
            ALPHA = 0.0;
        } else {
            float mass = dust_mass(at, dens);
            float lit = dust_lit(mass, lean);
            // The mean age of the mass, which the shared ink already accumulates -
            // ProcKick's arrangement, and here it drifts the whole cloud up its own
            // ramp as it thins. See smoke_pale.
            float old = clamp(years / max(dens, 1e-4), 0.0, 1.0);
            ALBEDO = mix(smoke_dark, smoke_lit,
                         clamp(lit + smoke_pale * old, 0.0, 1.0));
            ALPHA = clamp(mass * dust_ink * level * blast_footing(at), 0.0, 1.0);
        }
    }
}
";

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

// The blaze at the waterline. Late, brief and low - the reference has none in its
// first two frames, most of it in the middle three and none in the last.
uniform int blaze = 9;
// <b>Late, and it is the reference that says how late.</b> Its first two frames
// have no fire and no smoke; the orange core appears once the column is already
// standing, burns through three frames and is out before the last two. Born at
// 0.16 it lit before there was any smoke to be the only warm thing in, and read
// as an orange ball floating on the pond.
uniform float blaze_born = 0.40;
uniform float blaze_life = 0.95;
uniform float blaze_stagger = 0.35;
// How far it sits above the surface, and how far out along it. Low and wide
// rather than tall: what is burning is at the base of the column, spread over the
// water it is standing in.
uniform float blaze_seat = 0.045;
uniform float blaze_run = 0.10;
uniform float blaze_size = 0.050;
uniform float blaze_climb = 0.10;
// Squat: the fire is wider than it is high, which is what keeps it at the foot
// rather than reading as a small column of its own.
uniform float blaze_squat = 0.72;
uniform float blaze_gain = 1.45;

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
    internal static readonly string SmokeCode =
        SmokeShader.Replace("FLAME_NOISE", Stage3D.EmberNoiseCode)
                   .Replace("FLAME_INK", Stage3D.FlameInk)
                   .Replace("BLAST_FRAME", ProcBlast.FrameCode)
                   .Replace("DUST_INK", ProcBlast.DustInk);

    /// <inheritdoc cref="FoamCode"/>
    internal static readonly string FireCode =
        FireShader.Replace("FLAME_NOISE", Stage3D.EmberNoiseCode)
                  .Replace("FLAME_INK", Stage3D.FlameInk)
                  .Replace("BLAST_FRAME", ProcBlast.FrameCode);

    private static readonly Shader Foaming = new() { Code = FoamCode };
    private static readonly Shader Smoking = new() { Code = SmokeCode };
    private static readonly Shader Blazing = new() { Code = FireCode };
}
