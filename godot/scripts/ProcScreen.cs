using System;
using System.Collections.Generic;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// A smoke screen standing on one hex: the grenade goes off, a small flash, and a
/// cloud hangs there until the rules say otherwise.
///
/// <b>The one effect on this board whose clock is not its own.</b> Every other
/// thing in <c>Proc*</c> is an event with a length - a burst lives 1.6s, a
/// fireball 2.4s, a wave 1.6s - and its whole picture is a function of seconds
/// since it went off. A screen lives <em>two rounds</em>, and a round is not a
/// number of seconds; it is whatever the queue says it is. So the life here is
/// three states rather than a ramp - <see cref="Phase.Rising"/>,
/// <see cref="Phase.Holding"/>, <see cref="Phase.Clearing"/> - and the hold has no
/// timer in it at all. <see cref="Density"/> is the whole model and is a pure
/// function, so what the picture is worth at any point of a life can be asserted
/// without a board under it.
///
/// <b>Drawn with the pack's own sheet, and the first version was not.</b> That one
/// built the cloud out of <see cref="ProcBlast.DustInk"/> elements on three
/// concentric walls of arcs. It went through three pictures - a glass tube, a
/// waterfall, a rock - and what ended it was not a tuning problem: a smoke screen
/// is a <em>ball of smoke hanging in the air</em>, and what this board already has
/// for exactly that is <see cref="SheetBlast"/>'s flipbook. So the screen is that
/// art with another placement and another life, and it shares
/// <see cref="SheetBlast.Puffing"/> rather than owning a second answer to what a
/// puff looks like - <c>DustInk</c>'s own rule at the next scale up.
///
/// <b>What the sheet gives for free, and it is most of the effect.</b> Sixty-four
/// frames of a cloud curling, with a signed normal on every one of them, so the
/// mass is lit rather than tinted; and a dissolve at the end of the sheet, which
/// <em>is</em> the dispersal - the screen does not need to invent one, it lets the
/// playhead run.
///
/// <b>The puffs are recycled, and a parked playhead was the mistake.</b> A
/// flipbook is an event: play it and it ends. The first cut answered that by
/// parking every puff in the sheet's middle and letting it creep, which is a
/// contradiction dressed as a number - creep slowly and the cloud is a still
/// photograph, creep quickly and it dissolves without being lifted. It did both:
/// alive for a few seconds, frozen solid after thirteen.
///
/// What a steady cloud actually is, is a <em>population</em>. Every puff walks its
/// whole sheet over <see cref="Churn"/> seconds - born small, opening, dissolving -
/// and is then reborn somewhere else in the cell with a new size and a new turn.
/// Their births are staggered across the cycle, so at any instant some are opening
/// and some are going and the cloud as a whole neither moves nor stops. That is
/// motion with no loop in it: nothing is looped, so there is no seam to place, and
/// a screen held for a minute churns exactly as it did in its first second.
///
/// <b>The rebirth is hashed on the cycle, not only on the puff.</b> Reborn at the
/// same spot a puff pulses in place, which reads as a blinking sprite; hashed on
/// which time round it is, it comes back somewhere else and the cloud turns over.
///
/// <b>And it turns over at two speeds, because the source stops.</b> While the
/// charge is throwing smoke the cloud boils; a second later there is no source at
/// all and what is left only leans about. <see cref="Pace"/> is that: fast while
/// the cell fills, eased down to a drift over <see cref="Settle"/> seconds of
/// standing. It is spent on how fast the picture's clock runs rather than on
/// <see cref="Churn"/> itself - moving the divisor would move every puff's place
/// in its own life at once, and the whole cloud would jump at the handover.
///
/// <b>The sheet is crossfaded, and that is not a polish item.</b> A screen plays
/// its flipbook an order of magnitude slower than a burst does, so a step from one
/// cell to the next is a step the eye can see - the cloud reads as a dropped frame
/// rather than as slow smoke. <c>frame_blend</c> is a dial <see cref="SheetBlast"/>
/// measured, found worth nothing on a burst, and kept for exactly this case.
///
/// <b>The flash is the sheet's own flame, not a second effect.</b> The puff shader
/// carries a fire term keyed to the event's clock and out after <c>flame_out</c>
/// seconds. A smoke grenade's burst is exactly that: brief, small, low, and over
/// before the cloud is built. It is turned down from the burst's (see
/// <see cref="Flash"/>), not turned off.
///
/// <b>Its own wind, because the board's is not a wind.</b> <c>--wind</c> is
/// <see cref="Grove.Wind"/>: an amplitude with gusts that sways trees, and it has
/// no direction to lean along. So the lean is this effect's own two numbers
/// (<see cref="Drift"/> as strength, <see cref="Bearing"/> as a board heading), and
/// it is carried by how high a puff is - the cloud leans, its foot stays on the
/// cell it was laid on.
///
/// <b>One node, so one sort key against the tanks - named rather than
/// discovered.</b> The version this replaced cut the cloud into twelve arcs so a
/// tank standing inside it sorted between them. A MultiMesh cannot do that: every
/// puff of a screen sorts at the screen's own origin. It matters less than it did,
/// because this cloud hangs <em>over</em> the cell rather than standing round it,
/// and <see cref="SheetBlast"/> has always lived with the same limit - but a tank
/// halfway into a screen is wholly in front of it or wholly behind. There is no
/// tank on the effects bench to judge that on; it is revisited when the screen
/// reaches the event bench.
///
/// Every length is in hex circumradii, the board's rule, so one screen fits every
/// board and every zoom.
/// </summary>
public sealed partial class ProcScreen : Node3D
{
    /// <summary>How many puffs the cloud is. Twenty-two: <see cref="SheetBlast"/>
    /// spends twenty on a shell's column, and a screen is wider and shallower for
    /// about the same amount of sky.</summary>
    public const int PuffsDefault = 34;

    /// <summary>
    /// How far out the puffs are scattered, in circumradii.
    ///
    /// Inside the hex's own inradius (0.866), so the screen reads as sitting on that
    /// cell rather than spilling across the edge. Smoke does spill in life; on a
    /// board where a cell is a rule, a cloud whose foot crosses the boundary is a
    /// cloud somebody will ask which cell it is on.
    /// </summary>
    public const float SpreadDefault = 0.66f;

    /// <summary>How high the cloud's heart sits and how far the puffs scatter above
    /// and below it, in circumradii. Low and shallow: the reference is a ball
    /// sitting on the ground, not a column.</summary>
    public const float HeartDefault = 0.60f;
    public const float DeepDefault = 0.38f;

    /// <summary>How wide one puff is drawn, in circumradii, and how much bigger the
    /// cloud gets between the flash and the full screen.</summary>
    public const float SizeDefault = 1.05f;
    public const float GrowDefault = 1.60f;

    /// <summary>Seconds from the grenade going off to the cell being full.</summary>
    public const float RiseDefault = 0.45f;

    /// <summary>Seconds from the rules lifting the screen to clear air - the
    /// ordinary end, two rounds after it was laid.</summary>
    public const float FadeDefault = 2.60f;

    /// <summary>Seconds when a tank drives through it: the same clearing, faster,
    /// because a hull going past does not wait for the wind.</summary>
    public const float GustDefault = 0.90f;

    /// <summary>How long one puff takes to walk its whole sheet, in seconds. Its
    /// life, and thereby how fast the cloud turns over: short and the screen boils,
    /// long and it drifts.</summary>
    public const float ChurnDefault = 3.40f;

    /// <summary>How far a puff wanders over that life, in circumradii - out from
    /// where it was born and up. Smoke rolls; a population that is only replaced
    /// twinkles instead.</summary>
    public const float RollDefault = 0.20f;

    /// <summary>
    /// How much faster the cloud turns over while the grenade is still throwing it,
    /// what it settles to once the cell is full, and how long that settling takes.
    ///
    /// <b>The source stops, so the churn has to.</b> While the charge is burning
    /// there is smoke arriving under pressure and the cloud boils; a second later
    /// there is no source at all and what is left just leans about. Run at one speed
    /// the effect picks between the two and is wrong for the other half of its life
    /// - at the fast one a screen that has stood for ten seconds is still visibly
    /// being pumped, at the slow one the grenade going off has no urgency in it.
    ///
    /// <b>Spent on the clock's rate rather than on <see cref="Churn"/>.</b> A puff's
    /// place in its own life is <c>clock / churn</c>, so moving the divisor moves
    /// every puff at once - the whole cloud would jump at the handover. Moving how
    /// fast the clock runs changes nothing that is already drawn and only how
    /// quickly the next frame differs from it, which is what a slowdown is.
    /// </summary>
    public const float BustleDefault = 2.70f;
    public const float CalmDefault = 0.55f;
    public const float SettleDefault = 1.80f;

    /// <summary>How much of a life is spent easing in and out, so a puff is worth
    /// nothing at both ends of its own cycle. Belt and braces over the sheet's own
    /// opening and dissolve: the rebirth must not be visible, and this is what
    /// guarantees it whatever the art does at its edges.</summary>
    public const float BlinkDefault = 0.11f;

    /// <summary>How bright the grenade's flash is and how long it lasts, in seconds
    /// of the screen's own life. Against the burst's 3.10 and 0.17: a smoke round
    /// pops, it does not detonate.</summary>
    public const float FlashDefault = 2.30f;

    /// <summary>How far above the cell the flash survives, in world units - see
    /// where it is written.</summary>
    public const float FlashReach = 135.0f;

    /// <summary>How much of the way to the next cell of the sheet is drawn as the
    /// next cell. One - fully crossfaded - see where it is written.</summary>
    public const float BlendDefault = 1.0f;
    public const float FlashOutDefault = 0.26f;

    /// <summary>Where a screen is in its life. The hold has no timer, which is the
    /// whole of why this is three states and not a ramp.</summary>
    public enum Phase
    {
        /// <summary>Nothing drawn.</summary>
        Gone,
        /// <summary>Filling, <see cref="Rise"/> seconds of it.</summary>
        Rising,
        /// <summary>Full, until the rules say otherwise.</summary>
        Holding,
        /// <summary>Clearing, over whatever the lift asked for.</summary>
        Clearing,
    }

    /// <summary>
    /// How dense the screen is, given where it is in its life - the whole model, and
    /// a pure function so a check can walk a life without a board.
    ///
    /// Eased at both ends rather than linear: a screen that arrives at full density
    /// on a straight line reads as a fade-in on a title card.
    /// </summary>
    public static float Density(Phase phase, float age, float rise, float fade)
    {
        return phase switch
        {
            // <b>Front-loaded, and the smoothstep it replaced was the mistake.</b>
            // A grenade ejects its cloud at once and the cell is most of the way
            // full in the first fifth of a second; an S-curve spends that fifth
            // near zero, which reads as a cross-fade and leaves the flash nothing
            // to light. Most of this is over in the first third of the rise.
            Phase.Rising => Burst(Mathf.Clamp(age / Mathf.Max(rise, 1e-4f),
                                              0.0f, 1.0f)),
            Phase.Holding => 1.0f,
            // The going is the ordinary ease: smoke thins out, it does not snap.
            Phase.Clearing => Ease(Mathf.Clamp(
                1.0f - age / Mathf.Max(fade, 1e-4f), 0.0f, 1.0f)),
            _ => 0.0f,
        };
    }

    /// <summary>The curve the cell fills on: nought at nought, one at one, and
    /// most of the way there early.</summary>
    public static float Burst(float x)
    {
        float left = 1.0f - Mathf.Clamp(x, 0.0f, 1.0f);
        return 1.0f - left * left * left;
    }

    /// <summary>The curve <see cref="Density"/> runs on: smooth at both ends.</summary>
    public static float Ease(float x) => x * x * (3.0f - 2.0f * x);

    /// <summary>The same curve, undone: what x produced this value. Newton would be
    /// silly for a cubic this tame - the closed form of <c>3x^2 - 2x^3 = y</c> is one
    /// arcsine. Needed because <see cref="Lift"/> has to start clearing from the
    /// density that is actually on screen.</summary>
    public static float Unease(float y) =>
        0.5f - Mathf.Sin(Mathf.Asin(1.0f - 2.0f * Mathf.Clamp(y, 0.0f, 1.0f)) / 3.0f);

    /// <summary>
    /// Where one puff is in its own life, in [0, 1): born at nought, dissolved at
    /// one, and round again. <paramref name="offset"/> is its place in the queue, so
    /// twenty-two of them are spread across the cycle rather than stepping together.
    ///
    /// Pure, like <see cref="Density"/>, and for the same reason: what a screen is
    /// doing at any moment is a claim, and a claim wants a check. It has no end
    /// condition at all, which is the point - the smoke moves for as long as
    /// somebody keeps calling it.
    /// </summary>
    public static float Walk(float clock, float churn, float offset)
    {
        float t = clock / Mathf.Max(churn, 1e-3f) + offset;
        return t - Mathf.Floor(t);
    }

    /// <summary>
    /// How fast the picture's clock runs, given where the screen is in its life -
    /// the third model, and the one that says the grenade has stopped throwing.
    ///
    /// Fast while the cell fills, then eased down to a drift over
    /// <paramref name="settle"/> seconds of standing. Continuous at the handover by
    /// construction: the hold opens with <paramref name="lived"/> at nought, which
    /// is where the ease still reads the bustling rate.
    ///
    /// A clearing keeps whatever the hold had got to - what carries a lifted screen
    /// away is the sheet, not the churn, and speeding the churn up to see it go
    /// would be the cloud getting livelier as it died.
    /// </summary>
    public static float Pace(Phase phase, float lived, float bustle, float calm,
                             float settle) => phase switch
    {
        Phase.Gone => 0.0f,
        Phase.Rising => bustle,
        _ => Mathf.Lerp(bustle, calm,
                        Mathf.SmoothStep(0.0f, Mathf.Max(settle, 1e-3f), lived)),
    };

    /// <summary>Which time round the cycle a puff is on - what its next position is
    /// hashed against, so a reborn puff comes back somewhere else.</summary>
    public static int Round(float clock, float churn, float offset) =>
        Mathf.FloorToInt(clock / Mathf.Max(churn, 1e-3f) + offset);

    /// <summary>
    /// Where that puts its playhead on the sheet.
    ///
    /// <b>The clearing is the one thing that overrides the walk.</b> While the screen
    /// stands, a puff's sheet position is simply where it is in its own life. When
    /// the rules take the screen away, every puff is carried to the end of the sheet
    /// over the clearing - so whatever each was doing, they all dissolve together and
    /// the art's own last frames are the dispersal.
    /// </summary>
    public static float Sheet(Phase phase, float walk, float gone) =>
        phase == Phase.Clearing
            ? Mathf.Lerp(walk, 1.0f, Mathf.Clamp(gone, 0.0f, 1.0f))
            : walk;

    /// <summary>What one puff is worth over its own life: nothing at both ends, so a
    /// rebirth cannot be seen. <paramref name="blink"/> is how much of the life each
    /// end takes.</summary>
    public static float Breath(float walk, float blink)
    {
        float edge = Mathf.Clamp(blink, 0.001f, 0.49f);
        return Mathf.SmoothStep(0.0f, edge, walk)
               * (1.0f - Mathf.SmoothStep(1.0f - edge, 1.0f, walk));
    }

    /// <summary>Which cell this screen is laid on. The stage's, kept here so one
    /// grenade on a cell twice is one screen rather than two stacked.</summary>
    public Vector2I Cell { get; set; }

    public float Rise = RiseDefault;
    public float Fade = FadeDefault;
    public float Gust = GustDefault;

    public int Puffs = PuffsDefault;
    public float Spread = SpreadDefault;
    public float Heart = HeartDefault;
    public float Deep = DeepDefault;
    public float Size = SizeDefault;
    public float Grow = GrowDefault;
    public float Churn = ChurnDefault;
    public float Bustle = BustleDefault;
    public float Calm = CalmDefault;
    public float Settle = SettleDefault;
    public float Roll = RollDefault;
    public float Blink = BlinkDefault;
    public int Seed = 7;

    /// <summary>How far the cloud leans, in circumradii, and which way - a board
    /// heading in degrees, the way every other bearing here is spelled. Spent on a
    /// puff in proportion to how high it is, so the foot stays on the cell.</summary>
    public float Drift;
    public float Bearing = 90.0f;

    /// <summary>Hold both clocks where they are, for a bench looking at one frame. A
    /// held screen is a still frame and a capture of one is repeatable.</summary>
    public bool Hold;

    private Phase _phase = Phase.Gone;
    private float _age;
    private float _lived;
    private float _churn;
    private float _over = FadeDefault;
    private float _radius = 124.0f;
    private float _rise = 1.0f;
    private Transform3D _seat = Transform3D.Identity;
    private Vector3 _nudge;
    private int[]? _order;

    private MultiMeshInstance3D? _cloud;
    private MultiMesh? _many;
    private ShaderMaterial? _ink;

    public Phase State => _phase;
    public bool Alive => _phase != Phase.Gone;

    /// <summary>Whether the rules still have this screen standing - what a line of
    /// sight would ask, as against <see cref="Alive"/>, which is whether anything is
    /// still drawn.</summary>
    public bool Standing => _phase == Phase.Rising || _phase == Phase.Holding;

    /// <summary>Seconds into the state it is in. Not into the screen's whole life:
    /// the hold has no length, so a life has no total to be a fraction of.</summary>
    public float Age => _age;

    /// <summary>Seconds this screen has spent standing full. The crawl's clock, kept
    /// across the lift so a screen that stood a long time starts its dissolve from
    /// further along the sheet than one just laid.</summary>
    public float Lived => _lived;

    /// <summary>What the cloud is drawn at this frame.</summary>
    public float Level => Density(_phase, _age, Rise, _over);

    /// <summary>The picture's own clock: seconds the puffs have been turning over.
    /// Not the rules' - it runs all the way through a hold, which is the whole
    /// difference between a screen and a photograph of one.</summary>
    public float Churned => _churn;

    /// <summary>Where the first puff's playhead is this frame, for the readout. The
    /// others are spread across the cycle behind it.</summary>
    public float Head => Sheet(_phase, Walk(_churn, Churn, 0.0f), Gone);

    /// <summary>How fast the cloud is turning over this frame, as a multiple of one
    /// life per <see cref="Churn"/> seconds.</summary>
    public float Turning => Pace(_phase, _lived, Bustle, Calm, Settle);

    /// <summary>How far through a clearing the screen is, 0 while it stands.</summary>
    public float Gone => _phase == Phase.Clearing
        ? Mathf.Clamp(_age / Mathf.Max(_over, 1e-4f), 0.0f, 1.0f) : 0.0f;

    /// <summary>How long the clearing under way takes - <see cref="Fade"/> for the
    /// ordinary end, <see cref="Gust"/> for a hull going through.</summary>
    public float Clearing => _over;

    /// <summary>How far up its rise the screen is: what the cloud's <em>size</em>
    /// follows, as against <see cref="Level"/>, which is what its density follows.
    /// One all the way through a clearing, because a cloud that shrinks back into
    /// the grenade as it disperses is smoke running backwards.</summary>
    public float Bloom => _phase switch
    {
        Phase.Gone => 0.0f,
        Phase.Rising => Density(Phase.Rising, _age, Rise, _over),
        _ => 1.0f,
    };

    /// <summary>
    /// The screen's own smoke ramp: brightness of the sheet to the colour of the
    /// cloud.
    ///
    /// <b>The third statement of what smoke is coloured on this board.</b> The
    /// burst's ramp is earth - dark, warm, climbing to sand - because that is ground
    /// thrown into the air; the water burst's is a pond walked to white. A grenade's
    /// is neither: pale from the first stop and faintly cool, and it <em>climbs</em>
    /// rather than darkening, because what makes a screen a screen is that it
    /// scatters light rather than absorbing it. Flat white at every stop would throw
    /// the sheet's own shading away - the curl in those sixty-four frames is only
    /// visible as a spread of brightness - so the dark stop is a real grey.
    /// </summary>
    internal static readonly (float, Color)[] ScreenStops =
    {
        (0.00f, new Color(0.395f, 0.410f, 0.445f)),
        (0.50f, new Color(0.760f, 0.775f, 0.800f)),
        (1.00f, new Color(0.980f, 0.985f, 1.000f)),
    };

    /// <summary>
    /// Build the cloud. <paramref name="tile"/> is the hex's own width in screen px
    /// - every length above is in those - and the two camera terms are the field's,
    /// handed in so this needs no board.
    /// </summary>
    public void Build(float tile, float squash, float rise)
    {
        _radius = Mathf.Max(tile, 1.0f) * 0.5f;
        _rise = Mathf.Max(rise, 0.0001f);
        _nudge = Stage3D.Clear(squash, rise);

        _ink = new ShaderMaterial
        {
            Shader = SheetBlast.Puffing,
            RenderPriority = Stage3D.StandOrder,
        };
        _ink.SetShaderParameter("sheet",
            SheetBlast.Art("puff_sheet.png", ref SheetBlast.SheetArt));
        _ink.SetShaderParameter("bulge",
            SheetBlast.Art("puff_bulge.png", ref SheetBlast.BulgeArt));
        _ink.SetShaderParameter("dent",
            SheetBlast.Art("puff_dent.png", ref SheetBlast.DentArt));
        _ink.SetShaderParameter("smoke_ramp", SheetBlast.Ramp(ScreenStops, 256));
        _ink.SetShaderParameter("flame_ramp",
            SheetBlast.Ramp(SheetBlast.FlameStops, 256));
        _ink.SetShaderParameter("flame_fade",
            SheetBlast.Ramp(SheetBlast.FadeStops, 64));
        _ink.SetShaderParameter("sun", Stage3D.Sun);
        _ink.SetShaderParameter("seat_y", 0.0f);
        // The screen's own three, against the burst's. A grenade's flash is small,
        // brief and low; the cloud over it is pale all the way up rather than dark
        // at the core, because it never was earth.
        _ink.SetShaderParameter("flame_gain", FlashDefault);
        _ink.SetShaderParameter("flame_out", FlashOutDefault);
        _ink.SetShaderParameter("pale", 0.16f);
        // <b>And it has to reach the cloud, which took a picture to notice.</b>
        // The burst keeps its fire in the bottom 46 world units because a shell's
        // fireball sits on the ground under a column of earth. This cloud's heart
        // is ninety units up, so every puff of it was above the cut and the flash
        // was not merely dim - it was not drawn at all.
        _ink.SetShaderParameter("flame_low", FlashReach);
        // <b>And it stays white.</b> The burst walks its flame lookup a long way
        // along the ramp with age, because a shell's fireball goes white, yellow,
        // red and out. A smoke round has no fire in it at all - what is bright is
        // the ejection charge, and that is white for the fifth of a second it
        // lasts. Left at the burst's 0.42 the walk reached the ramp's red stop
        // inside the flash and the grenade read as a small fire in the cell.
        _ink.SetShaderParameter("flame_shift", 0.10f);
        // <b>And the sheet is crossfaded, where the burst leaves it stepped.</b>
        // This is the one dial SheetBlast measured, found worth nothing, and kept
        // anyway "for a shorter sheet, or one drawn bigger" - and a smoke screen
        // turns out to be the third case it was written for, which is a sheet
        // played <em>slowly</em>. A burst walks sixty-four cells in two seconds, so
        // one step of it moves a fraction of a screen pixel; a screen standing on a
        // cell walks the same sixty-four over ten, which is under eight cells a
        // second, and a flipbook at eight frames a second does not look like slow
        // smoke - it looks like the game has dropped frames. Crossfaded, the
        // playhead can run as slowly as the picture wants and never step.
        _ink.SetShaderParameter("frame_blend", BlendDefault);

        _many = new MultiMesh
        {
            Mesh = new QuadMesh { Size = Vector2.One },
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseCustomData = true,
            InstanceCount = Mathf.Max(Puffs, 1),
        };
        _cloud = new MultiMeshInstance3D
        {
            Multimesh = _many,
            MaterialOverride = _ink,
            SortingUseAabbCenter = false,
            Visible = false,
            // Toward the camera by the board's own clearance, like every other thing
            // that stands on a cell - two coplanar surfaces are hit_scar's coin toss.
            Position = _nudge,
        };
        AddChild(_cloud);
        Dress();
    }

    /// <summary>Seat it on a cell of the board - the burst's <c>Sit</c>.</summary>
    public void Sit(Vector2 ground, float lift, float squash, float rise)
    {
        _seat = Stage3D.Trunk(ground, lift, 0.0f, squash, rise);
        _rise = Mathf.Max(rise, 0.0001f);
        _nudge = Stage3D.Clear(squash, rise);
        Transform = _seat;
        if (_cloud is not null)
            _cloud.Position = _nudge;
        _ink?.SetShaderParameter("seat_y", Transform.Origin.Y);
    }

    private static float Hash(int k, int salt) =>
        (float)Grove.Hash01(k, salt, 733_003);

    /// <summary>
    /// Which puff goes in draw slot <paramref name="i"/>: the far side of the cell
    /// first, then the near side.
    ///
    /// <b>Because a MultiMesh has no depth sort.</b> The instances are drawn in index
    /// order with the depth buffer switched off, so the order they are written in
    /// <em>is</em> the compositing order - the rule <see cref="ProcSmoke"/> states as
    /// seat first, tip last. The slots do not move, so which order that is settles
    /// once: ordered by <c>sin</c> of a puff's own azimuth, which under this camera
    /// is its depth across the cell.
    /// </summary>
    private int Slot(int i, int count)
    {
        if (_order is null || _order.Length != count)
        {
            var made = new int[count];
            for (int k = 0; k < count; k++)
                made[k] = k;
            Array.Sort(made, (a, b) =>
                Mathf.Sin(Mathf.Tau * Hash(b, Seed + 1))
                     .CompareTo(Mathf.Sin(Mathf.Tau * Hash(a, Seed + 1))));
            _order = made;
        }
        return _order[i];
    }

    /// <summary>Where every puff of this screen sits and what its playhead
    /// reads.</summary>
    private void Dress()
    {
        if (_many is null)
            return;
        int count = Mathf.Max(Puffs, 1);
        if (_many.InstanceCount != count)
        {
            _many.InstanceCount = count;
            _order = null;
        }
        float level = Level;
        float gone = Gone;
        float bearing = Mathf.DegToRad(Bearing);
        var lean = new Vector2(Mathf.Cos(bearing), Mathf.Sin(bearing)) * Drift;
        // The cloud is thrown out of the grenade rather than faded in at full size.
        float fill = Mathf.Lerp(1.0f / Mathf.Max(Grow, 1.0f), 1.0f, Bloom);

        for (int i = 0; i < count; i++)
        {
            // Far side of the cell first: the slot order is the draw order.
            int k = Slot(i, count);
            // Spread across the cycle, so the population is always part opening and
            // part going rather than all of it doing one thing at once.
            float offset = Hash(k, Seed + 8);
            float walk = Walk(_churn, Churn, offset);
            // Hashed on which time round it is as well as on which puff it is, or a
            // reborn puff comes back where it died and pulses in place.
            int lap = Round(_churn, Churn, offset);
            int born = k * 131 + lap * 977;

            float turn = Mathf.Tau * Hash(born, Seed + 1);
            // Square-rooted, or every puff crowds the middle: a disc's area grows
            // with the square of its radius, so an even scatter wants the root.
            float outAt = Spread * Mathf.Sqrt(Hash(born, Seed + 2));
            float high = Heart + Deep * (2.0f * Hash(born, Seed + 3) - 1.0f);
            float wide = Size * (0.72f + 0.56f * Hash(born, Seed + 4));

            // And it rolls while it lives: out from where it was born and up. Smoke
            // that is only replaced twinkles; smoke that moves is smoke.
            float away = Roll * walk * (0.45f + 1.10f * Hash(born, Seed + 9));
            float climb = Roll * walk * (0.30f + 0.90f * Hash(born, Seed + 10));
            outAt = (outAt + away) * fill;
            high = Mathf.Max(high + climb, 0.06f) * fill;
            wide *= fill;

            float x = Mathf.Cos(turn) * outAt + lean.X * high;
            float z = Mathf.Sin(turn) * outAt + lean.Y * high;

            // The quad stands upright in the node's own billboard frame, which is
            // what Trunk builds; the up axis is divided by the rise for SheetBlast's
            // reason - the frame is squashed and a puff must not be.
            float px = wide * _radius;
            var basis = new Basis(new Vector3(px, 0.0f, 0.0f),
                                  new Vector3(0.0f, px / _rise, 0.0f),
                                  new Vector3(0.0f, 0.0f, 1.0f));
            _many.SetInstanceTransform(i, new Transform3D(basis,
                new Vector3(x * _radius, high * _radius / _rise, z * _radius)));

            // The turn, the puff's own age, its playhead, and how much of it is
            // drawn - the last being the screen's density times this puff's own
            // breath, which is nought at both ends of a life so the rebirth cannot
            // be seen.
            float mine = Sheet(_phase, walk, gone);
            _many.SetInstanceCustomData(i, new Color(
                Hash(born, Seed + 6), mine, mine,
                level * Breath(walk, Blink) * (0.80f + 0.30f * Hash(born, Seed + 7))));
        }
    }

    // --- the life ------------------------------------------------------------

    /// <summary>The grenade goes off: the cell starts filling. Laid on a screen
    /// already standing, it restarts the rise rather than stacking a second cloud -
    /// one cell, one screen, which is what the rules mean by it.</summary>
    public void Lay()
    {
        _phase = Phase.Rising;
        _age = 0.0f;
        _lived = 0.0f;
        _churn = 0.0f;
    }

    /// <summary>
    /// The rules take it away: two rounds are up, or a hull went through.
    ///
    /// <paramref name="gust"/> is the second of those - the same clearing over
    /// <see cref="Gust"/> instead of <see cref="Fade"/>. One method rather than two
    /// because it is one thing happening at two speeds, and a second entry point
    /// would be where the two ends of the ramp parted.
    ///
    /// <b>It starts from the density that is on screen</b>, not from full: lifting a
    /// screen halfway up its rise must not first fill the cell. That is what the ease
    /// has to be invertible for.
    /// </summary>
    public void Lift(bool gust = false)
    {
        if (_phase == Phase.Gone || _phase == Phase.Clearing)
            return;
        float now = Density(_phase, _age, Rise, _over);
        _over = gust ? Gust : Fade;
        _phase = Phase.Clearing;
        _age = (1.0f - Unease(now)) * _over;
    }

    /// <summary>Gone at once and nothing drawn - the reset.</summary>
    public void Douse()
    {
        _phase = Phase.Gone;
        _age = 0.0f;
        _lived = 0.0f;
        _churn = 0.0f;
    }

    public void Tick(double delta)
    {
        if (_phase != Phase.Gone && !Hold)
        {
            _age += (float)delta;
            if (_phase == Phase.Holding)
                _lived += (float)delta;
            // The picture's own clock, and it runs through the hold - which is
            // the whole of what "the smoke keeps moving" means. Kept small by the
            // hour, the pond's arrangement, so a bench left open all afternoon has
            // the precision one just started has.
            _churn = Mathf.PosMod(_churn + (float)delta * Turning, 3600.0f);
            if (_phase == Phase.Rising && _age >= Rise)
            {
                // Into the hold, which has no clock of its own: the round does. What
                // does keep running is the crawl - see Playhead.
                _phase = Phase.Holding;
                _age = 0.0f;
            }
            else if (_phase == Phase.Clearing && _age >= _over)
            {
                _phase = Phase.Gone;
                _age = 0.0f;
                _lived = 0.0f;
            }
        }
        bool on = _phase != Phase.Gone;
        if (_cloud is not null)
            _cloud.Visible = on;
        if (!on)
            return;
        // The flash is keyed to the event's clock and not to a puff's own age - see
        // SheetBlast's flame_out on why. Seconds since the grenade went off, which
        // after the rise is the rise plus however long it has stood.
        _ink?.SetShaderParameter(
            "time", _phase == Phase.Rising ? _age : Rise + _lived + _age);
        Dress();
    }

    // --- the dials -----------------------------------------------------------

    /// <summary>The puff shader's own default for a name; NaN when it does not
    /// declare it - <see cref="ProcWave.Declared"/>'s arrangement, and what the self
    /// test looks for.</summary>
    public static float Declared(string uniform) =>
        ProcBlast.Uniform(SheetBlast.Code, uniform);

    /// <summary>A number as this screen has it: what was turned, else the shader's
    /// own default.</summary>
    public float Dial(string uniform)
    {
        if (_live.TryGetValue(uniform, out float held))
            return held;
        float declared = Declared(uniform);
        return float.IsNaN(declared) ? 0.0f : declared;
    }

    /// <summary>The same number, written to the material.</summary>
    public void Dial(string uniform, float value)
    {
        _live[uniform] = value;
        _ink?.SetShaderParameter(uniform, value);
    }

    private readonly Dictionary<string, float> _live = new();

    /// <summary>One number of the model, by the name the panel knows it as; NaN when
    /// there is no such name, which is what the self test looks for -
    /// <see cref="SheetBlast.Model(string)"/>'s arrangement.</summary>
    public float Model(string name) => name switch
    {
        "puffs" => Puffs,
        "spread" => Spread,
        "heart" => Heart,
        "deep" => Deep,
        "size" => Size,
        "grow" => Grow,
        "churn" => Churn,
        "bustle" => Bustle,
        "calm" => Calm,
        "settle" => Settle,
        "roll" => Roll,
        "blink" => Blink,
        "rise" => Rise,
        "fade" => Fade,
        "gust" => Gust,
        "drift" => Drift,
        "bearing" => Bearing,
        _ => float.NaN,
    };

    public void Model(string name, float value)
    {
        switch (name)
        {
            case "puffs": Puffs = Mathf.RoundToInt(value); break;
            case "spread": Spread = value; break;
            case "heart": Heart = value; break;
            case "deep": Deep = value; break;
            case "size": Size = value; break;
            case "grow": Grow = value; break;
            case "churn": Churn = value; break;
            case "bustle": Bustle = value; break;
            case "calm": Calm = value; break;
            case "settle": Settle = value; break;
            case "roll": Roll = value; break;
            case "blink": Blink = value; break;
            case "rise": Rise = value; break;
            case "fade": Fade = value; break;
            case "gust": Gust = value; break;
            case "drift": Drift = value; break;
            case "bearing": Bearing = value; break;
            default: return;
        }
        Dress();
    }

    /// <summary>Every name <see cref="Model(string)"/> answers to, so the check can
    /// ask whether each of them has a dial on it.</summary>
    public static readonly string[] ModelNames =
    {
        "puffs", "spread", "heart", "deep", "size", "grow", "churn", "bustle",
        "calm", "settle", "roll", "blink", "rise", "fade", "gust", "drift",
        "bearing",
    };

    /// <summary>One line saying what is standing here, for the bench's readout and
    /// for a capture's log: the state, what it is worth, and where the sheet is.</summary>
    public string Note =>
        $"screen on {Cell.X},{Cell.Y}: {_phase.ToString().ToLowerInvariant()} "
        + $"{_age:F2}s, level {Level:F2}, churning {_churn:F1}s at x{Turning:F2}, "
        + $"{Puffs} puffs out to {Spread:F2} radii, lean {Drift:F2} at {Bearing:F0}";
}
