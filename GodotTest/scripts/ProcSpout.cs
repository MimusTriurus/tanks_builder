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
    /// <b>Half again the ground burst's, and that is the model rather than the
    /// taste.</b> Earth has to be broken before it can be thrown and most of the
    /// energy goes into breaking it; water is already loose, so the same round
    /// puts more of itself into height. Short of the rack's, which is a confined
    /// charge venting through a hole.
    /// </summary>
    public float Reach = ReachDefault;

    /// <summary>The two numbers something else has to be able to ask for -
    /// <see cref="ProcBlast.ReachDefault"/>'s reason: a bench that wants to reset
    /// a dial needs the tuned value, and reading it off a live instance gets
    /// whatever the last shot left.</summary>
    public const float ReachDefault = 0.58f;

    /// <summary>
    /// How long the whole event runs.
    ///
    /// Shorter than the ground burst's 2.4s and longer than the rack's 1.6.
    /// Nothing here is left burning and nothing hands over to anything - the
    /// plume falls back, the spray follows it in, and what is last to go is the
    /// haze standing over the entry. Water leaves no mark, so when it is finished
    /// the cell is exactly what it was.
    /// </summary>
    public const float LifeDefault = 1.90f;

    /// <summary>
    /// The quad, in tile widths: half-width either side of the seat, and height
    /// above it.
    ///
    /// Taller than wide, and by less than the rack: a plume is a column with a
    /// crown round its foot, so the crown sets the width and the stem sets the
    /// height. Checked against <see cref="Bounds"/>, which is asked of the model.
    /// </summary>
    public float Flank = 0.68f;
    public float Tall = 1.00f;

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

        // The crown: the sheet of water standing up round the entry. The widest
        // thing here, and what the quad's width is cut to.
        float crownOut = Uniform("crown_run") * reach * 1.40f;
        float crownUp = Uniform("crown_climb") * reach * 1.20f;
        float crownR = Uniform("crown_size") * 1.50f;
        Reach(new Vector2(crownOut, crownUp),
              crownR * Uniform("crown_flat"), crownR);

        // The stem: the jet up the middle. The tallest thing here, and what the
        // quad's height is cut to. Stretched upright, so its long axis is the
        // vertical one.
        // The fastest element at the top of its climb, and it spans its own
        // height from half of it - see stem_stretch, which is a span here and not
        // a stretch.
        float stemUp = Uniform("stem_rise") * reach * 1.20f;
        float stemR = Uniform("stem_size") * 1.70f;
        Reach(new Vector2(stemUp * Uniform("stem_lean"), stemUp * 0.5f),
              stemR, stemUp * Uniform("stem_stretch") * 0.5f + stemR);

        // The spray, at the top of the fastest drop's arc and at the far end of
        // the widest one's. Two different elements, which is why the pads are
        // taken apart: the apex of blast_throw is run*fast, at the middle of a
        // life, and the reach of it is run*spread*sin(lean) at the end of one -
        // no element is at both.
        float dropR = Uniform("drop_size") * 1.70f;
        float fast = reach * 1.0f;
        float wide = reach * ProcBlast.Uniform(SpoutCode, "spread", 1.15f)
                     * Mathf.Sin(Uniform("drop_wide"));
        Reach(new Vector2(wide, fast), dropR, dropR * 2.0f);

        // And the haze, which outlives the rest and is the last thing to leave
        // the quad.
        float mistR = Uniform("mist_size") * 1.50f;
        Reach(new Vector2(Uniform("mist_run") * reach * 1.30f,
                          Uniform("mist_climb") * reach * 1.25f),
              mistR * 1.30f, mistR);

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
        // <b>And the throw goes up more than it goes out</b> - spread is the
        // shared frame's own number (see ProcBlast.FrameCode), tuned at 1.15 on
        // earth thrown out of a crater, whose walls are what aim it sideways in
        // the first place. Water leaves a hole with no walls, so its drops keep
        // more of the throw as height; read at the earth's number half the spray
        // landed on the dry bank two cells away.
        ink.SetShaderParameter("spread", 0.85f);
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

// The crown: the conical sheet that stands up round the hole on the first frames.
// Outward fast and upward slowly, every element lying along its own way out - the
// burst's collar, turned up off the ground, and it is the family that says a
// *surface* was hit rather than a volume.
uniform int crown = 12;
// <b>Long enough to still be there when the stem is up, which is not how a
// crown behaves and is what the picture asked for anyway.</b> At 0.22 the family
// was over before the jet had climbed, and between the two there was a plume
// standing in the air with nothing at all at the waterline - a shape flying over
// the pond rather than coming out of it. What holds a splash to its surface is
// the foam at the foot of it, and this is the family that has any.
uniform float crown_life = 0.38;
uniform float crown_stagger = 0.02;
// <b>Short, and it has to be: this length is spent across the screen and the
// cell is only so wide.</b> At 0.62 of the throw the two sides of the crown
// reached a quarter of a tile past the water and the family drew a sheet of foam
// standing on the dry bank - the one thing a splash cannot do. What says a
// surface was hit is the angle of the sheet, not how far it gets.
uniform float crown_run = 0.40;
// <b>Climbing nearly as fast as it runs, and that is what makes it a crown.</b>
// Every element here is stretched along its own way out, so the ratio of these
// two IS the angle of the sheet: at 0.52 against 0.72 the family came out as two
// flat wings lying on the water - a shape spreading under the surface rather than
// a wall of water standing up out of it.
// <b>Low, and lower than the run rather than level with it.</b> Read at 0.62 the
// sheet climbed to a third of a tile and, stretched along its own way out, drew
// two long blades standing over the far bank - a pair of wings, and the stem was
// behind them. A crown is a SKIRT: what it says is that a surface was hit, and it
// says it at the waterline. The height in this effect belongs to the stem.
uniform float crown_climb = 0.42;
uniform float crown_size = 0.044;
// How much longer than wide, along its own way out. A sheet, not a puff: what
// stands up round a splash is a wall of water with a torn top edge.
uniform float crown_flat = 1.35;
uniform float crown_ink = 1.15;

// The stem: the jet that comes up the middle once the hole starts closing. Late,
// narrow, tall, and by far the most of what a splash reads as at a distance.
// <b>Ten rather than seven, and their speeds within a third of each other.</b>
// Seven elements over a range of 0.55 to 1.40 separated into two or three tall
// tongues flying apart with clear water between them - which is what a handful of
// independent bodies looks like, and it is the comb failure ProcSpall paid for
// turned inside out. A jet is one body that breaks up at its top.
uniform int stem = 10;
// <b>Born after the crown rather than with it, and that is the whole of the
// sequencing.</b> A jet leaves when the water rushing back in from all sides
// meets itself, so it cannot start on the frame the hole is made - and drawn from
// nought it composited into the crown and the pair came out a single white bush.
uniform float stem_born = 0.055;
uniform float stem_life = 0.75;
uniform float stem_stagger = 0.030;
uniform float stem_rise = 1.00;
uniform float stem_size = 0.040;
// <b>How much of its own height an element spans - which is not a stretch, and
// the difference is the one structural finding in this model.</b>
//
// Written as a stretch, the way every other family on this board is written, the
// jet is a body of a fixed shape that travels: it leaves the surface and by the
// time it is up there is clear water between it and the pond, so the plume flies
// over the cell instead of coming out of it. Measured on the picture: a hundred
// pixels of gap, with the foam still sitting on the water below it.
//
// A Rayleigh jet is attached to its own surface by definition - it IS the water
// being squeezed out of the closing hole - so an element here spans from about the
// waterline to its own height, and is seated at half of it. Slightly over one, so
// the foot goes a little under the surface and is cut by it rather than ending on
// it. Nothing travels; the column grows.
uniform float stem_stretch = 1.15;
// How far off vertical they lean. Small, and not nought, for the rack's reason:
// seven tongues on one line is a comb.
// <b>Nearly straight up, because the lean opens with height and a fork is what
// that draws.</b> At 0.10 the ten elements split into two prongs over the top half
// of the climb - a pair of ears, and the eye reads two jets instead of one
// breaking up. Not nought, for the comb.
uniform float stem_lean = 0.055;
uniform float stem_ink = 1.20;

// The spray: drops thrown out of the crown, arcing and coming back in. <b>The one
// family on this board whose elements end below where they started</b>, and what
// cuts them is the surface itself - see ProcSpout.Root and blast_footing.
uniform int drops = 40;
uniform float drop_life = 0.85;
uniform float drop_stagger = 0.06;
// <b>Small, and the first number of this whole model that had to be measured
// rather than reasoned.</b> At 0.026 of a tile, stretched by 2.6 and drawn on the
// crispest profile here, twenty of them came out as 120px petals standing in a
// row over the pond - not spray at all, and by half a second they were the entire
// picture. A drop is the smallest body on this board and it has to be drawn like
// one; what makes twenty of them read as spray is that each is a streak, not that
// each is a shape.
uniform float drop_size = 0.008;
// The narrowest and the widest launch, off straight up in radians. Wider than the
// burst's clods: earth leaves a crater in a cone because it was broken out of the
// walls of one, and water leaves a hole in every direction at once.
uniform float drop_narrow = 0.30;
uniform float drop_wide = 0.90;
// How much a drop stretches along its own travel. Large, and it is what makes
// twenty of them read as drops rather than as grit: a drop in flight is drawn as
// the streak it covers in a frame, which is what every photograph of a splash
// shows.
uniform float drop_stretch = 2.60;
// <b>Crisper than the shared cloud, and crisper than a clod of earth.</b>
// dust_soft is 2.55 because a cloud is in transition everywhere; a clod keeps
// 1.85 because it is a small body. A drop of water is the smallest body here and
// the only one with a specular edge in life, so it takes the sharpest profile on
// the board - short of the flame's 0.5, which has infinite slope at the rim.
uniform float drop_soft = 1.15;
uniform float drop_ink = 0.85;

// The haze: what hangs over the entry once the water is back. Low, wide, thin,
// and the last thing to go - so the cell is left settling rather than switched
// off.
uniform int mist = 9;
uniform float mist_life = 1.30;
uniform float mist_born = 0.10;
uniform float mist_stagger = 0.22;
uniform float mist_run = 0.38;
uniform float mist_climb = 0.30;
uniform float mist_size = 0.100;
uniform float mist_ink = 0.55;

// <b>The dark stop is the pond's own colour with light through it, and it is
// measured off the surface's own shader rather than picked.</b> Stage3D's
// computed water declares shallow = (0.24, 0.52, 0.50); water in the air is that
// lifted toward white, because what makes a plume pale is the air whipped into
// it. Read at a grey stop the whole thing came out as dust - which is the very
// picture this class exists to replace.
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
        float dens = 0.0;
        float years = 0.0;
        vec2 lean = vec2(0.0);

        for (int k = 0; k < crown; k++) {
            float fk = float(k);
            float born = crown_stagger * ember_hash(vec2(fk, 211.0));
            float a = (time - born) / max(crown_life, 1e-3);
            if (a > 0.0 && a < 1.0) {
                float side = ember_hash(vec2(fk, 212.0)) < 0.5 ? -1.0 : 1.0;
                float fast = 0.45 + 0.95 * ember_hash(vec2(fk, 213.0));
                float out_at = crown_run * reach * side * fast * pow(a, 0.55);
                float up = crown_climb * reach * fast * pow(a, 0.62);
                float r = crown_size * (0.60 + 0.90 * ember_hash(vec2(fk, 214.0)))
                          * (0.55 + 0.95 * a);
                // Along its own way out, which is what makes it a sheet: the
                // element is stretched on the line from the hole to where it is,
                // so the family reads as one wall opening rather than as a row of
                // puffs standing in a circle.
                vec2 p = vec2(out_at, up);
                vec2 flow = length(p) < 1e-4 ? vec2(1.0, 0.0) : normalize(p);
                // Steeply out, because the family outlives its own shape:
                // it is here at 0.38s to hold the waterline, and an element still
                // at full weight late in that is a blade rather than foam.
                float gain = crown_ink * (1.0 - exp(-a / 0.020))
                             * pow(max(1.0 - a, 0.0), 2.20);
                dust_part(at, p, r, r * crown_flat, flow, fk + 211.0,
                          gain, dust_soft, a, dens, lean, years);
            }
        }

        for (int k = 0; k < stem; k++) {
            float fk = float(k);
            float born = stem_born + stem_stagger * ember_hash(vec2(fk, 221.0));
            float a = (time - born) / max(stem_life, 1e-3);
            if (a > 0.0 && a < 1.0) {
                float fast = 0.70 + 0.50 * ember_hash(vec2(fk, 222.0));
                float up = stem_rise * reach * fast * pow(a, 0.58);
                float wide = up * stem_lean
                             * (2.0 * ember_hash(vec2(fk, 223.0)) - 1.0);
                float r = stem_size * (0.55 + 1.00 * ember_hash(vec2(fk, 224.0)))
                          * (0.70 + 0.60 * a);
                // Spanning its own height and seated at half of it, so the
                // column is rooted in the surface however high it has got - see
                // stem_stretch, which is what that number means here.
                float draw = max(up * stem_stretch * 0.5, r * 1.10);
                vec2 p = vec2(wide, up * 0.5);
                float gain = stem_ink * (1.0 - exp(-a / 0.045))
                             * pow(max(1.0 - a, 0.0), 1.35);
                dust_part(at, p, r, draw, vec2(0.0, 1.0), fk + 221.0,
                          gain, dust_soft, a, dens, lean, years);
            }
        }

        for (int k = 0; k < drops; k++) {
            float fk = float(k);
            float born = drop_stagger * ember_hash(vec2(fk, 231.0));
            float a = (time - born) / max(drop_life, 1e-3);
            if (a > 0.0 && a < 1.0) {
                float leaning = mix(drop_narrow, drop_wide,
                                    ember_hash(vec2(fk, 232.0)));
                float side = ember_hash(vec2(fk, 233.0)) < 0.5 ? -1.0 : 1.0;
                float fast = mix(0.40, 1.0, ember_hash(vec2(fk, 234.0)));
                // The shared parabola, stated as an apex - see blast_throw. Back
                // on the surface exactly at the end of its life, which for water
                // is the whole of what happens to it: a drop does not settle, it
                // is absorbed.
                vec4 thrown = blast_throw(a, reach, fast, leaning, side);
                float r = drop_size * (0.45 + 1.60 * ember_hash(vec2(fk, 235.0)));
                // Thins on the way in rather than on the way out: what takes a
                // drop out of the picture is the surface, not the air.
                float gain = drop_ink * (1.0 - exp(-a / 0.030))
                             * (1.0 - smoothstep(0.72, 1.0, a));
                dust_part(at, thrown.xy, r, r * drop_stretch, thrown.zw,
                          fk + 231.0, gain, drop_soft, a, dens, lean, years);
            }
        }

        for (int k = 0; k < mist; k++) {
            float fk = float(k);
            float born = mist_born + mist_stagger * ember_hash(vec2(fk, 241.0));
            float a = (time - born) / max(mist_life, 1e-3);
            if (a > 0.0 && a < 1.0) {
                float side = 2.0 * ember_hash(vec2(fk, 242.0)) - 1.0;
                float out_at = mist_run * reach * side * pow(a, 0.45);
                float up = mist_climb * reach * pow(a, 0.65)
                           * (0.40 + 0.90 * ember_hash(vec2(fk, 243.0)));
                float r = mist_size * (0.60 + 0.85 * ember_hash(vec2(fk, 244.0)))
                          * (0.55 + 1.05 * a);
                float gain = mist_ink * (1.0 - exp(-a / 0.14))
                             * pow(max(1.0 - a, 0.0), 1.10);
                // Lying along the surface, which is where a haze over water
                // stands: an element on end here would be a small column, and
                // there is one of those already.
                dust_part(at, vec2(out_at, up), r, r * 1.55, vec2(1.0, 0.0),
                          fk + 241.0, gain, dust_soft, a, dens, lean, years);
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
