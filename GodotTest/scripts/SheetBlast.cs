using System;
using System.Collections.Generic;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The reference project's explosion, standing on this board.
///
/// <b>What was imported is the art and the shading model; the machinery was
/// not, because none of it survives the move.</b> The pack (Duarte David, MIT -
/// see <c>assets/ExplosionVFX/NOTICE.md</c>) is a <c>GPUParticles3D</c> pair
/// under a perspective camera with a directional light and a
/// <c>WorldEnvironment</c> doing glow and ACES, and its soft edge reads the
/// depth buffer through <c>hint_depth_texture</c>. This board is orthographic,
/// one world unit to one screen pixel, has no lights and no environment at all,
/// and runs on <c>gl_compatibility</c>, where there is no depth texture to
/// read. Four of the pack's five moving parts are answers to questions this
/// board does not ask.
///
/// The fifth is the one worth having, and it is the reason this exists:
/// <b>sixty-four frames of a simulated smoke puff, with a signed normal map on
/// every frame.</b> That is curl and self-shadowing that no noise field
/// computes - <see cref="ProcBlast"/> spends thirteen uniforms on the shape of
/// its dust and still reads as a smudge, because a sum of ellipses is a sum of
/// ellipses. So the flipbook is kept whole, and everything round it is rewritten
/// in this project's own terms:
///
/// - <b>Its particles become a <see cref="MultiMesh"/> this class writes every
///   frame.</b> A GPU particle cannot be put on a frame, and every measurement
///   on this bench is a pixel diff of two captures at a pinned step - the
///   blocker that stopped the other CC0 pack cold. Twenty-odd puffs is a model
///   small enough to keep on the CPU, so the model is on the CPU, deterministic
///   by index, and the shader is left with the section across one puff. The
///   division <see cref="ProcSmoke"/> and <see cref="ProcBlast"/> already run
///   under.
/// - <b>Its depth fade becomes an analytic fade against the ground.</b> The pack
///   needs the depth buffer because it does not know what its puffs will
///   intersect; here the only thing under a burst is the cell it went off on, and
///   the height above it is known in the vertex stage. Cheaper, works in
///   Compatibility, and identical to the eye.
/// - <b>Its lighting becomes the board's own sun.</b> The pack hands a normal to
///   a lit material and lets a <c>DirectionalLight3D</c> do the lambert. There
///   are no lights here, so the lambert is one dot product in the fragment
///   against <see cref="Stage3D.Sun"/> - the same vector the terrain's shadows,
///   the water's glint and the dust cone all read, so a puff is lit from where
///   everything else on the board is lit from.
/// - <b>Its glow does not become anything, and that is the one visible
///   loss.</b> <c>emission_energy</c> of 5 is a number for a tonemapped pipeline
///   with bloom: it means "clip to white and bleed". Written straight into
///   ALBEDO with no environment it means "white disc", so the default here is
///   1.7 and the flames are colour rather than light. Named in
///   <c>docs/blast.md</c> as the next thing to try, because it wants a
///   <c>WorldEnvironment</c> and that is a decision about the whole board rather
///   than about one effect.
///
/// The pack's three ramps are kept exactly as its <c>.tres</c> has them (see
/// <see cref="Ramp"/>), except the smoke's, which was the default black-to-white
/// - grey smoke on a brown board. It is the dust cone's own earth instead, so
/// the two bursts throw up the same ground.
/// </summary>
public sealed partial class SheetBlast : Node3D
{
    // ---------------------------------------------------------------- the model

    /// <summary>
    /// How many puffs are in the cloud proper.
    ///
    /// The pack's first emitter has twenty at <c>explosiveness 0.91</c>, which is
    /// twenty out at once; this is that, plus a couple, because a hex is 248px
    /// and the cloud is drawn bigger here than the pack's is on screen.
    /// </summary>
    public const int PuffsDefault = 20;

    public int Puffs = PuffsDefault;

    /// <summary>
    /// How many puffs come afterwards, spread over the whole life.
    ///
    /// The pack's second emitter: five, no explosiveness, same lifetime - so they
    /// keep arriving while the first twenty are dying. <b>This is what stops a
    /// flipbook cloud reading as one object fading out</b>, which is what twenty
    /// puffs on their own do: they are all on the same frame of the same sheet at
    /// the same time, so the whole cloud thins in step.
    /// </summary>
    public const int TrailsDefault = 9;

    public int Trails = TrailsDefault;

    /// <summary>The whole event, in seconds. The pack's 2.13 shortened: at 2.13
    /// the tail is a grey smear standing on the board long after the shot reads
    /// as over, and a burst that outlasts the shell's own dust cone
    /// (<see cref="ProcBlast.LifeDefault"/>) would be the slower of the two
    /// bursts.</summary>
    public const float LifeDefault = 2.05f;

    public float Life = LifeDefault;

    /// <summary>How far the cloud's own puffs get from the seat, in tile widths.
    /// The one size of the thing, <see cref="ProcBlast.Reach"/>'s role.</summary>
    public const float ReachDefault = 0.17f;

    public float Reach = ReachDefault;

    /// <summary>How wide one puff is drawn at birth, in tile widths. Note that
    /// the sheet grows the puff on its own - the flipbook is a puff expanding -
    /// so this is the size of the first frame, not of the biggest one.</summary>
    public const float SizeDefault = 0.200f;

    public float Size = SizeDefault;

    /// <summary>How much bigger a puff's quad ends than it starts, on top of what
    /// the sheet does. A little, and not none: the sheet's own growth stops at
    /// frame twenty and its last forty frames are a fixed-size cloud
    /// dissolving.</summary>
    public const float GrowDefault = 1.85f;

    public float Grow = GrowDefault;

    /// <summary>How far the middle of the cloud rises over its life, in tile
    /// widths. The pack has <c>gravity 0</c> and no climb at all - it is an
    /// explosion in the air, seen head-on. A burst in the ground is seen from
    /// above and has to leave the ground.</summary>
    public const float ClimbDefault = 0.53f;

    public float Climb = ClimbDefault;

    /// <summary>Where the middle of the cloud starts, in tile widths above the
    /// contact point. Not zero: half of every puff would be below the ground line
    /// and this camera buries that half - see <see cref="Stage3D.Stem"/>.</summary>
    public const float SeatDefault = 0.105f;

    public float Seat = SeatDefault;

    /// <summary>How much of a puff's throw is sideways rather than up. One is a
    /// disc, zero is a column. The pack throws into a full sphere, of which the
    /// bottom half is under the ground here.</summary>
    public const float FlatDefault = 0.62f;

    public float Flat = FlatDefault;

    /// <summary>How much a puff slows down over its own life: the pack's
    /// <c>linear_accel -1</c> against velocities of 0.5 to 1, which is a puff
    /// that has spent most of its travel by the time it is half dead. Nought is
    /// constant speed, one comes to a stop exactly at the end.</summary>
    public const float SlowDefault = 0.86f;

    public float Slow = SlowDefault;

    /// <summary>How much the cloud's puffs disagree about when to leave, as a
    /// share of the life. The pack's <c>explosiveness 0.91</c> is 0.09 of this;
    /// a little more, because a perfectly simultaneous cloud has one silhouette
    /// and reads as a single sprite.</summary>
    public const float StaggerDefault = 0.055f;

    public float Stagger = StaggerDefault;

    /// <summary>How much of its own life one puff gets, as a share of the whole
    /// event. Under one so a puff born late still finishes its sheet inside the
    /// event - a puff cut off mid-flipbook vanishes at full opacity, which is the
    /// one artefact of a flipbook that cannot be softened.</summary>
    public const float PuffLifeDefault = 0.90f;

    public float PuffLife = PuffLifeDefault;

    /// <summary>
    /// How many of the cloud's puffs are lances: fast, thin, stretched along their
    /// own flight and thrown further than the mass.
    ///
    /// <b>The single feature that makes a burst read as thrown earth rather than
    /// as smoke</b>, and the reference photograph is made of them: its silhouette
    /// is not a cloud outline but forty-odd radial spikes, each tapered, each
    /// overshooting the body it came out of. A flipbook cannot draw one - the sheet
    /// is a round puff - but a quad can be turned and stretched, and a round puff
    /// drawn on a quad four times as long as it is wide is a streak with the
    /// sheet's own curl inside it.
    ///
    /// The same trick the computed burst plays on its clods
    /// (<c>clod_stretch</c>), which is worth saying because it means the idea was
    /// already in the project and only the flipbook is new.
    /// </summary>
    public const int LancesDefault = 26;

    public int Lances = LancesDefault;

    /// <summary>How much faster a lance is than the mass. Over one by definition:
    /// a spike that does not overshoot the cloud is not a spike, it is part of the
    /// outline.</summary>
    public const float LanceFastDefault = 2.20f;

    public float LanceFast = LanceFastDefault;

    /// <summary>How much thinner a lance is than a puff of the mass.</summary>
    public const float LanceThinDefault = 0.44f;

    public float LanceThin = LanceThinDefault;

    /// <summary>
    /// How many times its own width a puff is drawn along its flight.
    ///
    /// Applied to everything, not only to the lances - the reference's mass is
    /// itself made of vertically drawn-out lumps, because everything in a burst is
    /// travelling. It relaxes back toward round as a puff slows, which is what a
    /// streak that has stopped is.
    /// </summary>
    public const float StretchDefault = 2.35f;

    public float Stretch = StretchDefault;

    /// <summary>
    /// How far the middle of the cloud rises over its life, in tile widths.
    ///
    /// <b>Read together with <see cref="Reach"/> this is the whole of the shape,
    /// and the reference is unambiguous about which of the two wins.</b> Frame one
    /// is wider than it is tall; frame three is three times taller than it is
    /// wide, over a base that has stopped growing. So the radial half of the throw
    /// stalls (that is <see cref="Slow"/>) while the vertical half keeps going -
    /// see <see cref="Rising"/>.
    /// </summary>
    public const float RisingDefault = 0.085f;

    /// <summary>
    /// How quickly the climb is spent, as a share of a puff's own life.
    ///
    /// <b>It used to be the power of a monotone curve, and that was the bug.</b>
    /// See <see cref="Climbing"/>: small numbers here are what make the rise an
    /// event rather than a habit.
    /// </summary>
    public float Rising = RisingDefault;

    /// <summary>
    /// How far into the sheet a puff is born, as a share of it.
    ///
    /// <b>Nought is the honest reading of the flipbook and the wrong first
    /// frame.</b> Cell zero of the sheet is empty and cell one is a dot: born at
    /// the start, twenty-two puffs spend the first tenth of a second being
    /// nothing at all, so the burst opens on a bare cell and grows into itself -
    /// which is a puff of smoke starting, not a shell going off. Started a few
    /// frames in, the cloud arrives already formed, which is what a detonation
    /// does.
    ///
    /// <b>It shifts the sheet and not the age.</b> The flame is keyed to the
    /// puff's own age, so a phase that moved both would put the fire out before
    /// the smoke had arrived - which is why the two travel in separate channels of
    /// the instance data.
    /// </summary>
    public const float StartDefault = 0.11f;

    public float Start = StartDefault;

    /// <summary>How much of its climb the dust gives back before it is gone, as a
    /// share of the climb. Small: dust sags, it does not land - see
    /// <see cref="Climbing"/>.</summary>
    public const float SettleDefault = 0.16f;

    public float Settle = SettleDefault;

    /// <summary>How much of its climb a lance gives back by the end of its life, as
    /// a share of the climb. Coarse earth is thrown, and thrown things fall; at one
    /// it is back where it started.</summary>
    public const float FallDefault = 0.98f;

    public float Fall = FallDefault;

    /// <summary>Which cloud this is. The model is a hash of the index and this,
    /// so the same seed is the same burst to the pixel and a different one is a
    /// different cloud with the same numbers.</summary>
    public int Seed = 1;

    /// <summary>
    /// Whether this burst went off in water rather than in earth.
    ///
    /// <b>One ramp, and that is the whole of the difference - which is the
    /// finding, not a shortcut.</b> A shell into a pond throws the same event this
    /// class already draws: a mass of many puffs that goes up, curls, sags and
    /// spreads, with the round's own flash at the bottom of it. The substance the
    /// mass is made of changes what colour it is and nothing else about how it
    /// moves, so what changes here is <see cref="SmokeStops"/> for
    /// <see cref="FoamStops"/> and not one number of the model.
    ///
    /// <b>Four builds of a hand-written water plume were thrown away to arrive
    /// at that sentence.</b> Each of them was its own model - its own families,
    /// its own envelope, its own elements - and each was reported as wrong for the
    /// same reason underneath: sixty-four rendered frames of a simulated cloud
    /// carry curl and self-shadowing that a sum of ellipses does not have, and no
    /// amount of tuning on the sum buys it. The sheet has them. So the sheet is
    /// what water gets too.
    ///
    /// Read in <see cref="Build"/>, so it is set on a node before it is built -
    /// which is what <see cref="Stage3D.Splash"/>'s own pool does. The flame ramp
    /// and the fade are left exactly as the pack wrote them: the flash of the
    /// charge is the same flash whatever it went off in.
    /// </summary>
    public bool Wet;

    private float _clock = -1.0f;

    public bool Alive => _clock >= 0.0f;

    public float Age => _clock;

    /// <summary>Whether the clock stands still - <see cref="ProcBlast.Hold"/>'s
    /// reason, word for word, and the pack agrees: it spends a uniform
    /// (<c>still_frame</c>) on freezing every particle on one frame of the sheet.
    /// That uniform is here too, and this is the other half of it - the frame the
    /// model is on rather than the frame the sheet is on.</summary>
    public bool Hold;

    public float Clock
    {
        get => _clock;
        set => _clock = Mathf.Clamp(value, 0.0f, Life);
    }

    // ------------------------------------------------------------- the machinery

    private MultiMeshInstance3D? _cloud;
    private MultiMesh? _many;
    private ShaderMaterial? _ink;
    private float _tile = 248.0f;
    private float _rise = 1.0f;

    private static Texture2D? _sheet;
    private static Texture2D? _bulge;
    private static Texture2D? _dent;

    /// <summary>
    /// Build the cloud. <paramref name="tile"/> is the hex's width in screen px -
    /// every length above is in those - and the two camera terms are the field's,
    /// handed in rather than read, for <see cref="ProcBlast.Build"/>'s reason.
    /// </summary>
    public void Build(float tile, float squash, float rise)
    {
        _tile = tile;
        _rise = Mathf.Max(rise, 0.0001f);

        _ink = new ShaderMaterial { Shader = Puffing, RenderPriority = Stage3D.StandOrder };
        _ink.SetShaderParameter("sheet", Art("puff_sheet.png", ref _sheet));
        _ink.SetShaderParameter("bulge", Art("puff_bulge.png", ref _bulge));
        _ink.SetShaderParameter("dent", Art("puff_dent.png", ref _dent));
        _ink.SetShaderParameter("smoke_ramp", Ramp(Smokes(Wet), 256));
        _ink.SetShaderParameter("flame_ramp", Ramp(FlameStops, 256));
        _ink.SetShaderParameter("flame_fade", Ramp(FadeStops, 64));
        _ink.SetShaderParameter("sun", Stage3D.Sun);
        _ink.SetShaderParameter("seat_y", 0.0f);

        _many = new MultiMesh
        {
            Mesh = new QuadMesh { Size = Vector2.One },
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseCustomData = true,
            InstanceCount = Count,
        };
        _cloud = new MultiMeshInstance3D
        {
            Multimesh = _many,
            MaterialOverride = _ink,
            SortingUseAabbCenter = false,
            Visible = false,
            // Toward the camera by the board's own clearance, like every other
            // thing that stands on a cell - two coplanar surfaces are hit_scar's
            // coin toss.
            Position = _nudge = Stage3D.Clear(squash, rise),
        };
        AddChild(_cloud);
        Dress();
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
    private Vector3 _nudge;

    /// <summary>Where the burst stands, through the transform a tree gets - see
    /// <see cref="ProcBlast.Sit"/>. The seat's own world height goes to the
    /// shader as well, because the ground fade is measured from it.</summary>
    public void Sit(Vector2 ground, float lift, float squash, float rise)
    {
        _seat = Stage3D.Trunk(ground, lift, 0.0f, squash, rise);
        Stand();
    }

    /// <summary>The seat and the size, put together. Apart from <see cref="Sit"/>
    /// so the size can be turned while a burst is in the air, which is what a panel
    /// is for.</summary>
    private void Stand()
    {
        Transform = _seat.ScaledLocal(Vector3.One * _might);
        _ink?.SetShaderParameter("seat_y", Transform.Origin.Y);
        // The depth nudge is a fact about this camera's depth buffer, not about how
        // big the burst is, so it is divided back out - see Stage3D.Clear on why
        // two units is the number and why less than one stopped working.
        if (_cloud is not null)
            _cloud.Position = _nudge / _might;
    }

    public void Fire() => _clock = 0.0f;

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
        if (_cloud is not null)
            _cloud.Visible = on;
        if (!on)
            return;
        // The event's own clock, which the flame is keyed to - see flame_out.
        _ink?.SetShaderParameter("time", _clock);
        Dress();
    }

    /// <summary>
    /// Put every puff where it is now.
    ///
    /// <b>The whole model, and it is deliberately arithmetic.</b> One puff is a
    /// direction, a speed that decays, a birth and a size; where it is at time t
    /// is that in closed form, which is the property that lets the clock be held
    /// and scrubbed. A simulation stepped by delta could not be scrubbed
    /// backwards and could not be captured twice - see <see cref="Hold"/>.
    /// </summary>
    /// <summary>
    /// How high one puff has climbed, as a share of the full climb, at
    /// <paramref name="a"/> of its own life.
    ///
    /// <b>This is the shape of the whole event in one function, and the version it
    /// replaced had no third act at all.</b> That one was <c>pow(a, rising)</c> -
    /// monotone, reaching its maximum on the puff's very last frame - so the plume
    /// rose for the entire two seconds and then stopped existing. Nothing ever
    /// settled, because nothing was ever finished rising. Said out loud it is
    /// obvious; on the picture it reads as smoke that keeps being emitted upward,
    /// which is exactly what it was.
    ///
    /// What a burst does instead has three acts, and two of them are here:
    ///
    /// - <b>Up fast.</b> The ejection is instantaneous - one shove, not a chimney -
    ///   so the rise is spent early and then saturates. <see cref="Rising"/> is how
    ///   quickly, as a share of the puff's life; at the default the climb is 90%
    ///   done in the first fifth of it.
    /// - <b>Then down slowly.</b> Which is where the two fractions part company.
    ///   <b>Coarse earth comes back.</b> A lance is thrown, and thrown things fall:
    ///   its profile is a parabola over its own apex, and <see cref="Fall"/> says
    ///   how much of the climb it has given back by the end. <b>Dust does not
    ///   come back, it sags.</b> The mass and the fine fraction lose only
    ///   <see cref="Settle"/> of their height, late and slowly, which is what a
    ///   dust column cooling and spreading looks like - and the settling is mostly
    ///   read off the spreading (see <see cref="Grow"/>) rather than off the sinking.
    ///
    /// Public because the self-test asks the model rather than restating it - the
    /// arrangement <see cref="LastFinish"/> is written under.
    /// </summary>
    internal float Climbing(float a, Role role)
    {
        if (role == Role.Lance)
        {
            // A parabola normalised so Climb still means how high it gets, whatever
            // Fall is set to: the apex of a*(1-f*a) is 1/(4f).
            float f = Mathf.Clamp(Fall, 0.05f, 1.4f);
            return 4.0f * f * a * (1.0f - f * a);
        }
        float tau = Mathf.Max(Rising, 0.01f);
        float up = (1.0f - Mathf.Exp(-a / tau)) / (1.0f - Mathf.Exp(-1.0f / tau));
        // The sag, and only in the last two thirds: a cloud that starts sinking
        // while it is still going up has no apex, and an apex is what the eye reads
        // as the moment the throw was spent.
        return up - Settle * Mathf.SmoothStep(0.35f, 1.0f, a);
    }

    /// <summary>How many quads the cloud is: the mass, its lances and the smoke
    /// after them.</summary>
    internal int Count =>
        Mathf.Max(Puffs, 0) + Mathf.Max(Lances, 0) + Mathf.Max(Trails, 0);

    private void Dress()
    {
        if (_many is null)
            return;
        if (_many.InstanceCount != Count)
            _many.InstanceCount = Count;

        float t = Mathf.Max(_clock, 0.0f);
        float life = Mathf.Max(Life, 1e-3f);
        float seat = Seat * _tile;
        for (int k = 0; k < Count; k++)
        {
            Role role = Of(k);
            bool trail = role == Role.Trail;
            bool lance = role == Role.Lance;
            float born = Birth(k, role) * life;
            float mine = Share(k, born / life, role) * life;
            float a = (t - born) / mine;
            if (a <= 0.0f || a >= 1.0f)
            {
                // Nothing drawn rather than nothing sent: a degenerate quad
                // rasterises no pixels, and a hole in the instance array would
                // have to be closed by renumbering every puff every frame.
                _many.SetInstanceTransform(k, new Transform3D(
                    Basis.Identity.Scaled(Vector3.Zero), Vector3.Zero));
                _many.SetInstanceCustomData(k, new Color(0.0f, 0.0f, 0.0f, 0.0f));
                continue;
            }

            // <b>Where it is thrown: an elevation, not a direction round a
            // circle.</b> A ground burst throws into a hemisphere, and how much of
            // that hemisphere is sideways rather than up is the one number that
            // decides whether the thing is a disc or a column. Flat=1 spreads the
            // elevations evenly from the horizon to straight up; Flat=0 crushes
            // them against the vertical.
            float lift = Mathf.Lerp(Mathf.Pi * 0.5f, Mathf.DegToRad(14.0f),
                                    Mathf.Pow(Hash(k, 23),
                                              1.0f + 2.0f * (1.0f - Mathf.Clamp(Flat, 0.0f, 1.0f))));
            float side = Hash(k, 19) < 0.5f ? -1.0f : 1.0f;
            float spd = (0.45f + 0.85f * Hash(k, 31))
                        * (trail ? 0.85f : 1.0f)
                        * (lance ? Mathf.Max(LanceFast, 1.0f) : 1.0f);
            // <b>The late puffs are the skirt, and that is what the reference does
            // with them.</b> Its frames two and three have a low dark collar
            // spreading along the ground under the column, and it is not there in
            // frame one - it arrives, like they do. So they are pushed toward the
            // horizontal and given almost none of the climb, which also stops them
            // reading as a second, weaker plume inside the first.
            if (trail)
                lift = Mathf.Min(lift, Mathf.DegToRad(26.0f));
            // And a lance is thrown up rather than out, because in the reference
            // the spikes that read are the ones standing clear of the head. A
            // horizontal one lands inside the skirt and is never seen.
            if (lance)
                lift = Mathf.Max(lift, Mathf.DegToRad(40.0f));

            // The throw's own speed decays - the pack's negative linear accel,
            // integrated, with Slow=1 coming to a stop exactly at the end.
            float run = a * (1.0f - 0.5f * Slow * a) / Mathf.Max(1.0f - 0.5f * Slow, 1e-3f);

            float thrown = Reach * _tile * spd * run;
            float x = Mathf.Cos(lift) * thrown * side;
            float y = seat + Mathf.Sin(lift) * thrown
                      + Climb * _tile * (trail ? 0.30f : 1.0f) * Climbing(a, role);
            float wide = Size * _tile
                         * (trail ? 1.45f : 1.0f)
                         * (lance ? Mathf.Max(LanceThin, 0.05f) : 1.0f)
                         * (0.72f + 0.56f * Hash(k, 37))
                         * Mathf.Lerp(1.0f, Mathf.Max(Grow, 0.05f), a);

            // <b>Turned and stretched along its own flight.</b> The quad's up axis
            // is laid along the line from the seat to where the puff is now and
            // lengthened; its cross axis keeps the width. That is the reference's
            // whole silhouette - radial tapered spikes - out of a sheet that only
            // knows how to draw a round puff. It relaxes back toward round as the
            // puff slows, because a streak that has stopped travelling is a lump.
            var along = new Vector2(x, y - seat);
            along = along.LengthSquared() < 1.0f ? Vector2.Up : along.Normalized();
            // <b>Most of the stretch is the lance's, and giving all of it to the
            // mass as well was the first thing to look wrong:</b> twenty long thin
            // puffs and twenty-six longer thinner ones is not a plume with spikes
            // coming out of it, it is a feather duster. The reference's mass is
            // drawn out, not drawn thin - the spikes are what is thin, and they
            // read as spikes because there is a body behind them.
            float pull = 1.0f + (Mathf.Max(Stretch, 1.0f) - 1.0f)
                                * (lance ? 1.9f : 0.42f)
                                * (1.0f - 0.65f * a);
            // Built in screen px and divided into world on the way out, so the
            // stretch looks aligned on screen rather than in the squashed frame the
            // board is drawn in.
            var basis = new Basis(
                new Vector3(-along.Y * wide, along.X * wide / _rise, 0.0f),
                new Vector3(along.X * wide * pull, along.Y * wide * pull / _rise, 0.0f),
                new Vector3(0.0f, 0.0f, 1.0f));
            _many.SetInstanceTransform(k, new Transform3D(
                basis, new Vector3(x, y / _rise, Hash(k, 41) * 4.0f)));
            // The turn as a share of a full one (custom data is safest kept
            // inside the unit range), the puff's own age, where in the sheet that
            // age puts it, and how much of the puff is drawn at all. <b>The last
            // two are not the same number</b> - see Start.
            float phase = Mathf.Clamp(Start, 0.0f, 0.95f);
            _many.SetInstanceCustomData(k, new Color(
                Hash(k, 43), a, phase + (1.0f - phase) * a,
                trail ? 0.62f : lance ? 0.80f : 1.0f));
        }
    }

    /// <summary>
    /// How late in the event the last trail puff may be born, as a share of it.
    ///
    /// <b>This was 0.72, and it was the emitter.</b> The pack's second emitter has
    /// no explosiveness at all - five puffs spread over its whole lifetime - which
    /// is right for a fireball hanging in the air and wrong for a shell: nine puffs
    /// arriving one at a time over a second and a half, each starting small and
    /// growing, reads as a chimney. <b>A burst ejects once.</b> So the late
    /// fraction is now born inside the ejection with everything else, and what
    /// makes it the late fraction is that it is slower and lives longer - which is
    /// what a fine fraction is.
    /// </summary>
    public const float TrailSpread = 0.16f;

    /// <summary>
    /// When one puff is born, as a share of the event.
    ///
    /// The cloud's are scattered by hash across the stagger - the pack's
    /// explosiveness - and the trails are spaced evenly across
    /// <see cref="TrailSpread"/>, because six of them arriving at random would
    /// leave gaps in exactly the stretch they exist to fill.
    /// </summary>
    internal float Birth(int k, Role role) =>
        role switch
        {
            Role.Trail => (k - Mathf.Max(Puffs, 0) - Mathf.Max(Lances, 0) + 0.5f)
                          / Mathf.Max(Trails, 1) * TrailSpread,
            // A lance leaves at the front of the event or not at all: it is the
            // ejecta, and ejecta is thrown by the detonation rather than carried
            // by the plume.
            Role.Lance => Hash(k, 11) * Stagger * 0.35f,
            _ => Hash(k, 11) * Stagger,
        };

    /// <summary>What one puff is. Three, and the reference photograph is why there
    /// are not two: the mass, the spikes coming out of it, and the smoke that keeps
    /// arriving after both.</summary>
    public enum Role { Cloud, Lance, Trail }

    /// <summary>Which role index <paramref name="k"/> has. Ordered mass, lances,
    /// trails, so a count can be turned without renumbering the others.</summary>
    internal Role Of(int k) =>
        k < Mathf.Max(Puffs, 0) ? Role.Cloud
        : k < Mathf.Max(Puffs, 0) + Mathf.Max(Lances, 0) ? Role.Lance
        : Role.Trail;

    /// <summary>
    /// How much of the event one puff gets, as a share of it.
    ///
    /// <b>Never more than is left, and that is the whole of this function.</b> A
    /// flipbook cut off mid-sheet does not fade - it disappears at whatever opacity
    /// that frame happens to have, which is the one hard edge this effect can show
    /// and the one thing no amount of tuning softens. A trail born at 0.72 of the
    /// event and given the full 0.78 of it ran to 1.50 and vanished on frame
    /// thirty-two, in full view, every single burst: found by the check that asks
    /// this question rather than by looking, because a puff popping out of a
    /// dissolving cloud reads as part of the dissolve.
    ///
    /// So a late puff plays its sheet faster instead. That is the right trade of
    /// the two available - the other is to stop the trails arriving late, which is
    /// the entire reason they exist.
    /// </summary>
    internal float Share(int k, float born, Role role)
    {
        // <b>Compressing the births moved the problem to the deaths.</b> With
        // everything ejected at once - which is what a shell does - every puff was
        // also on the same frame of the same sheet at the same time, so the whole
        // cloud thinned in step and the settle act ended in one go: the plume was
        // simply gone between 0.9s and 1.2s. That is the failure the pack solves by
        // emitting late, and emitting late is the thing being got rid of. So the
        // spread is in how long a puff lives rather than in when it starts, which
        // has the same effect on the eye and none of the chimney.
        float mine = role switch
        {
            // Coarse earth is thrown and lands: it is finished long before the dust.
            Role.Lance => PuffLife * 0.62f,
            // The fine fraction is what is still there at the end - that is the
            // whole of what makes it fine.
            Role.Trail => PuffLife * 1.20f,
            _ => PuffLife,
        } * Mathf.Lerp(0.82f, 1.18f, Hash(k, 53));
        return Mathf.Max(Mathf.Min(mine, 1.0f - born), 1e-3f);
    }

    /// <summary>How far through the event the last puff to finish finishes, as a
    /// share of it. One or less is the invariant; the self-test asserts it here
    /// rather than recomputing the model, so the two cannot drift.</summary>
    internal float LastFinish()
    {
        float last = 0.0f;
        for (int k = 0; k < Count; k++)
        {
            Role role = Of(k);
            float born = Birth(k, role);
            last = Mathf.Max(last, born + Share(k, born, role));
        }
        return last;
    }

    /// <summary>One number of the puff shader, live - <see cref="ProcBlast.Dial"/>
    /// exactly, and read out of the shader's own text for the same reason: the
    /// uniforms are where the numbers live.</summary>
    public float Dial(string uniform)
    {
        if (!_live.TryGetValue(uniform, out float now))
        {
            now = ProcBlast.Uniform(PuffCode, uniform);
            _live[uniform] = now;
        }
        return now;
    }

    public void Dial(string uniform, float value)
    {
        _live[uniform] = value;
        bool counted = PuffCode.Contains("uniform int " + uniform + " = ",
                                         StringComparison.Ordinal);
        Variant sent = counted ? Mathf.RoundToInt(value) : value;
        _ink?.SetShaderParameter(uniform, sent);
    }

    private readonly Dictionary<string, float> _live = new();

    /// <summary>
    /// One number of the model, by name.
    ///
    /// <b>A switch rather than reflection</b>, because the panel's table is
    /// checked against this: a name that is not here comes back NaN, and the
    /// self-test says which one. Three of them are counts and come back as their
    /// integer selves.
    /// </summary>
    public float Model(string name) => name switch
    {
        "puffs" => Puffs,
        "trails" => Trails,
        "life" => Life,
        "reach" => Reach,
        "size" => Size,
        "grow" => Grow,
        "climb" => Climb,
        "seat" => Seat,
        "flat" => Flat,
        "slow" => Slow,
        "stagger" => Stagger,
        "puff_life" => PuffLife,
        "start" => Start,
        "seed" => Seed,
        "lances" => Lances,
        "lance_fast" => LanceFast,
        "lance_thin" => LanceThin,
        "stretch" => Stretch,
        "rising" => Rising,
        "settle" => Settle,
        "fall" => Fall,
        _ => float.NaN,
    };

    public void Model(string name, float value)
    {
        switch (name)
        {
            case "puffs": Puffs = Mathf.RoundToInt(value); break;
            case "trails": Trails = Mathf.RoundToInt(value); break;
            case "life": Life = value; break;
            case "reach": Reach = value; break;
            case "size": Size = value; break;
            case "grow": Grow = value; break;
            case "climb": Climb = value; break;
            case "seat": Seat = value; break;
            case "flat": Flat = value; break;
            case "slow": Slow = value; break;
            case "stagger": Stagger = value; break;
            case "puff_life": PuffLife = value; break;
            case "start": Start = value; break;
            case "lances": Lances = Mathf.RoundToInt(value); break;
            case "lance_fast": LanceFast = value; break;
            case "lance_thin": LanceThin = value; break;
            case "stretch": Stretch = value; break;
            case "rising": Rising = value; break;
            case "settle": Settle = value; break;
            case "fall": Fall = value; break;
            case "seed": Seed = Mathf.RoundToInt(value); break;
            default:
                GD.PushWarning($"sheet blast: no model number called {name}");
                return;
        }
        Dress();
    }

    /// <summary>Every name <see cref="Model(string)"/> answers, so the panel and
    /// the check can both be built off one list rather than off two copies of
    /// one.</summary>
    internal static readonly string[] ModelNames =
    {
        "puffs", "trails", "life", "reach", "size", "grow", "climb", "seat",
        "flat", "slow", "stagger", "puff_life", "start", "seed",
        "lances", "lance_fast", "lance_thin", "stretch", "rising",
        "settle", "fall",
    };

    /// <summary>
    /// One index's own random number.
    ///
    /// <b>Deterministic and cheap, and it has to be both.</b> Every claim about
    /// this bench is a pixel diff of two captures, so a cloud built off
    /// <see cref="GD.Randf"/> could not be captured twice; and it is called some
    /// two hundred times a frame, so it cannot allocate. The integer hash the
    /// ember noise uses, in C#.
    /// </summary>
    private float Hash(int k, int salt)
    {
        unchecked
        {
            uint h = (uint)(k * 374761393 + salt * 668265263 + Seed * 1274126177);
            h = (h ^ (h >> 13)) * 1274126177u;
            return ((h ^ (h >> 16)) & 0xFFFFFF) / 16777215.0f;
        }
    }

    /// <summary>
    /// One of the pack's sheets, loaded once for every burst there will ever be.
    ///
    /// <b>Off the filesystem rather than as a project resource</b> -
    /// <see cref="AssetRoot"/>'s route, for its stated reason: what is loaded by
    /// path is what is on disk, so a re-exported sheet is picked up by the next
    /// run with no re-import step in between.
    ///
    /// <b>It does not avoid the importer, and it was written here as though it
    /// did.</b> The sheets sit under <c>res://</c>, so the editor imports them
    /// anyway - the three <c>.import</c> files are in that folder, committed - and
    /// what this route actually buys is that nothing here reads the imported copy.
    /// Named rather than quietly fixed, because the alternative is to move 7.6MB
    /// of art out of the project to save three small text files, and that trade is
    /// the wrong way round.
    ///
    /// Static, because they are 16MB each as RGBA8 and six bursts sharing one
    /// board must not be six copies of 48MB.
    /// </summary>
    private static Texture2D Art(string file, ref Texture2D? kept)
    {
        if (kept is not null)
            return kept;
        string path = ProjectSettings.GlobalizePath("res://assets/ExplosionVFX/") + file;
        Image? art = Image.LoadFromFile(path);
        if (art is null)
        {
            // Said out loud and answered with a texture, not with a null: a null
            // sampler is white, which is a screen-filling white square - a failure
            // that looks like a bug in the effect rather than a missing file.
            GD.PushWarning($"sheet blast: no {file} under assets/ExplosionVFX - "
                           + "the puff will be blank");
            art = Image.CreateEmpty(4, 4, false, Image.Format.Rgba8);
            art.Fill(new Color(0.0f, 0.0f, 0.0f, 0.0f));
        }
        kept = ImageTexture.CreateFromImage(art);
        return kept;
    }

    /// <summary>
    /// A gradient as a strip of pixels.
    ///
    /// The pack keeps these as <c>GradientTexture1D</c> sub-resources of its
    /// material; here they are built from the same stops in code, because this
    /// project has no <c>.tres</c> files and a colour is not something
    /// <see cref="ControlPanel"/> has an organ for anyway. The stops are copied
    /// from <c>ExplosionMaterial.tres</c> unchanged except the smoke's - see the
    /// remarks on <see cref="SmokeStops"/>.
    /// </summary>
    private static ImageTexture Ramp((float At, Color Ink)[] stops, int wide)
    {
        Image strip = Image.CreateEmpty(wide, 1, false, Image.Format.Rgba8);
        for (int x = 0; x < wide; x++)
        {
            float u = (x + 0.5f) / wide;
            Color ink = stops[0].Ink;
            for (int s = 0; s + 1 < stops.Length; s++)
            {
                if (u < stops[s].At)
                    break;
                ink = u >= stops[s + 1].At
                    ? stops[s + 1].Ink
                    : stops[s].Ink.Lerp(stops[s + 1].Ink,
                        (u - stops[s].At)
                        / Mathf.Max(stops[s + 1].At - stops[s].At, 1e-5f));
            }
            strip.SetPixel(x, 0, ink);
        }
        return ImageTexture.CreateFromImage(strip);
    }

    /// <summary>Brightness of the sheet to the colour of the smoke. <b>The one
    /// ramp not copied from the pack:</b> its own is the default black-to-white,
    /// which is grey smoke, and grey smoke on this board is the wrong earth. These
    /// are the dust cone's own two dirt colours, so the two bursts throw up the
    /// same ground.</summary>
    internal static readonly (float, Color)[] SmokeStops =
    {
        (0.00f, new Color(0.168f, 0.130f, 0.098f)),
        (0.50f, new Color(0.470f, 0.385f, 0.290f)),
        (1.00f, new Color(0.930f, 0.820f, 0.630f)),
    };

    /// <summary>
    /// The same ramp for a burst in water: white, and measured rather than
    /// picked.
    ///
    /// <b>Dark, mid and light of the pond it comes out of, walked to white.</b>
    /// The computed surface's own <c>shallow</c> is (0.24, 0.52, 0.50), and water
    /// thrown into the air is that water with light through it: so the dark stop
    /// is the pond lifted, the middle is where foam starts and the top is white
    /// with the faintest of that green-blue left in it. Neutral grey would read as
    /// smoke, and pure white at every stop would throw the sheet's own shading
    /// away - the curl in those sixty-four frames is <em>only</em> visible as a
    /// spread of brightness, so a flat ramp is a flat cloud.
    ///
    /// <b>And it climbs where the earth's ramp is dark.</b> A mass of earth
    /// darkens as it thickens, because earth absorbs; water brightens, for the
    /// reason a thin film of it is clear. Which way round the ramp runs is the
    /// whole of that, and it is why this is a ramp swap rather than a tint.
    /// </summary>
    internal static readonly (float, Color)[] FoamStops =
    {
        (0.00f, new Color(0.395f, 0.585f, 0.580f)),
        (0.50f, new Color(0.780f, 0.880f, 0.885f)),
        (1.00f, new Color(0.985f, 0.995f, 1.000f)),
    };

    /// <summary>Which of the two the burst is made of - <see cref="Wet"/>'s one
    /// consequence, as a method so a check can ask for it.</summary>
    internal static (float, Color)[] Smokes(bool wet) => wet ? FoamStops : SmokeStops;

    /// <summary>Brightness plus the age shift to the colour of the flame, copied
    /// stop for stop from the pack: nothing, then white, then a warm yellow, then
    /// red, then out. Read left to right it is the life of a fireball, and the
    /// age shift is what walks along it.</summary>
    private static readonly (float, Color)[] FlameStops =
    {
        (0.0169f, new Color(0.0f, 0.0f, 0.0f, 0.0f)),
        (0.380f, new Color(0.965f, 0.965f, 0.965f)),
        (0.461f, new Color(1.000f, 0.800f, 0.502f)),
        (0.550f, new Color(1.000f, 0.000f, 0.000f)),
        (0.602f, new Color(0.0f, 0.0f, 0.0f)),
    };

    /// <summary>A puff's own age to how far its flame lookup has walked. The
    /// pack's, unchanged: nearly all of the walk is spent in the first third of a
    /// puff's life, which is why its flames go out long before its smoke
    /// does.</summary>
    private static readonly (float, Color)[] FadeStops =
    {
        (0.000f, new Color(0.0f, 0.0f, 0.0f)),
        (0.043f, new Color(0.439f, 0.439f, 0.439f)),
        (0.320f, new Color(1.0f, 1.0f, 1.0f)),
    };

    private static readonly Shader Puffing = new() { Code = PuffCode };

    /// <summary>The shader's text, for the panel and the check to read the
    /// numbers out of - <see cref="ProcBlast.FrameCode"/>'s arrangement.</summary>
    internal static string Code => PuffCode;

    /// <summary>
    /// The pack's fragment stage, ported.
    ///
    /// Line for line it is the same idea: the sheet's brightness is a coordinate
    /// into three ramps - albedo, flame colour, flame survival - and the signed
    /// normal lights it. What changed is listed on the class; what did not is the
    /// part worth keeping.
    /// </summary>
    private const string PuffCode = @"
shader_type spatial;
render_mode unshaded, blend_mix, depth_draw_never, cull_disabled;

// The flipbook and its two normal halves. Nearest would show the cell grid, so
// linear, and no mipmaps: the cell is 256px drawn at about 50, and a mipmap
// chain built across cell boundaries bleeds the neighbouring frame in.
uniform sampler2D sheet : source_color, filter_linear, hint_default_transparent;
uniform sampler2D bulge : filter_linear, hint_default_black;
uniform sampler2D dent : filter_linear, hint_default_black;

uniform sampler2D smoke_ramp : source_color, filter_linear;
uniform sampler2D flame_ramp : source_color, filter_linear;
uniform sampler2D flame_fade : filter_linear;

// The sheet's layout. Eight by eight, and a uniform rather than a constant
// because the next sheet will not be.
uniform int across = 8;
uniform int down = 8;

// Every puff frozen on one frame of the sheet, or -1 for the animation. The
// pack's own debugging aid, kept because it is the only way to look at what one
// frame of the flipbook is doing.
uniform float still = -1.0;

// How much of the sheet's alpha is drawn.
uniform float puff_ink = 0.92;

// How bright the flames are. The pack says 5 and means it - it has ACES and glow
// behind it, so 5 there is 'bleed'. Here it would be 'clip', so this is 1.7.
uniform float flame_gain = 3.10;
// How far a puff's age walks the flame lookup. The pack's 0.5 - it warns that
// high values look wrong, and they do: past about 0.8 the whole cloud starts red.
uniform float flame_shift = 0.42;
// When the flame is out, in seconds <b>of the event</b>. The pack has no such
// number and does not need one: its ramp walks to red and then to black, and with
// energy 5 and bloom behind it the black end reads as a fire going out. With
// neither, the walk alone leaves a red clot sitting on the ground - red on brown,
// at the seat, for the rest of the event - so the flame is faded out as well as
// walked along.
//
// <b>The event's clock and not the puff's, which is the correction that mattered
// most.</b> Keyed to a puff's own age it is the pack's own arrangement and is
// exactly right for the pack, whose particles are all born at once - the two
// clocks are the same number there. Here six puffs arrive late on purpose, and a
// puff born a second in was reading its own age as young and lighting up: a
// white fireball opening at the bottom of a dying grey cloud. Fire happens when
// the shell goes off, not whenever a puff is new.
uniform float flame_out = 0.17;
uniform float time = 0.0;

// The lambert: how much light a puff has before the sun reaches it, and how much
// the sun is worth. Seated well off zero because a puff's dark side is smoke in
// daylight, not smoke at night.
// <b>How far above the seat the fire survives, in world units.</b> The reference
// is emphatic about this and it is the cheapest correction on the list: its
// fireball is a compact bright ball sitting *on the ground*, a small fraction of
// the silhouette, and everything above it is already dark. Ours lit the whole
// cloud, because the flame is keyed to the sheet's brightness and the sheet is
// bright everywhere.
uniform float flame_low = 46.0;

// How much lighter the top of the plume is than its base, and over how many world
// units that happens. Not a light: dust that has been in the air is finer and
// scatters more, which the reference shows as a pale sand-coloured head over a
// nearly black core - a difference no lambert produces, because it is a difference
// in what the dust is rather than in where the sun is.
uniform float pale = 0.50;
uniform float pale_high = 130.0;

uniform float lit_seat = 0.58;
uniform float lit_gain = 0.55;
uniform vec3 sun = vec3(0.0, 1.0, 0.0);

// How much the sheet's own turn varies per puff. Zero draws every puff of the
// cloud in the same orientation, which is the failure a flipbook is most likely
// to show: twenty copies of one picture.
uniform float spin = 1.0;

// The ground fade, in world units: where the seat is, how far below it a puff is
// gone, and the band it fades over. The pack reads the depth buffer for this;
// here the ground is known - see the class.
uniform float seat_y = 0.0;
uniform float root = 4.0;
uniform float floor_fade = 26.0;

varying float high;
varying vec4 mine;

void vertex() {
    mine = INSTANCE_CUSTOM;

    // Which cell of the sheet this puff is on, from its own age.
    float frames = float(across * down);
    float f = floor(mine.z * frames);
    if (still > -0.01) {
        f = floor(still);
    }
    f = clamp(f, 0.0, frames - 1.0);

    // Turned about its middle before it is mapped into the cell, so the cloud is
    // not twenty copies of one picture. Clamped rather than wrapped: a turn pulls
    // the corners in from outside the cell, and outside the cell is the next
    // frame of the animation.
    float turn = mine.x * spin * 6.2831853;
    vec2 uv = UV - 0.5;
    float s = sin(turn), c = cos(turn);
    uv = clamp(vec2(c * uv.x - s * uv.y, s * uv.x + c * uv.y) + 0.5, 0.0, 1.0);
    UV = uv / vec2(float(across), float(down))
         + vec2(mod(f, float(across)) / float(across),
                floor(f / float(across)) / float(down));

    // How far above the ground this corner is, in world units. The seat's own
    // world height comes in as a uniform because the node's transform carries
    // the cell's lift and this needs the difference.
    high = (MODEL_MATRIX * vec4(VERTEX, 1.0)).y - seat_y;
}

void fragment() {
    vec4 puff = texture(sheet, UV);
    float bright = max(max(puff.r, puff.g), puff.b);

    // The signed normal: two unsigned halves subtracted. The epsilon is not
    // decoration - the sheet is transparent over most of its area and normalize
    // of a zero vector is a NaN, which comes out as a black square.
    vec3 face = normalize(texture(bulge, UV).rgb - texture(dent, UV).rgb
                          + vec3(0.0, 0.0, 1e-4));
    float lit = lit_seat + lit_gain * clamp(dot(face, normalize(sun)), 0.0, 1.0);

    vec3 smoke = texture(smoke_ramp, vec2(bright, 0.5)).rgb * lit;
    // Finer dust higher up: the ramp read further along is the same earth with
    // more light in it, so this needs no second palette.
    smoke = mix(smoke, texture(smoke_ramp, vec2(min(bright + 0.35, 1.0), 0.5)).rgb * lit,
                pale * clamp(high / max(pale_high, 1.0), 0.0, 1.0));

    // The flame: the same brightness, walked along its ramp by the puff's age.
    float gone = texture(flame_fade, vec2(mine.y, 0.5)).r;
    vec3 flame = texture(flame_ramp, vec2(bright + flame_shift * gone, 0.5)).rgb
                 * flame_gain
                 * (1.0 - smoothstep(0.0, max(flame_out, 1e-3), time))
                 * (1.0 - smoothstep(0.0, max(flame_low, 1.0), high));

    // Added into the albedo rather than into EMISSION: unshaded, and with no
    // environment on this board, EMISSION would be the same number with an extra
    // step in it - see the class on what is lost with the glow.
    ALBEDO = smoke + flame;
    ALPHA = clamp(puff.a * puff_ink * mine.w, 0.0, 1.0)
            * smoothstep(0.0, max(floor_fade, 1e-4), high + root);
}
";
}
