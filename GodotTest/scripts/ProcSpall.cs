using System;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// A round that did not get in: the flash on the plate, the fan of incandescent
/// spall thrown off it, the shell itself going away, and the coat knocked loose.
///
/// <b>The one frame that says a hit did nothing, and the board did not have
/// it.</b> A ricochet and a penetration are already different events everywhere
/// else on this bench - <see cref="Gunnery.Penetration"/> decides which,
/// <see cref="VehicleAudio.ImpactFor"/> picks a different recording for each, and
/// <see cref="Gunnery.PenetrationsToKill"/> counts only one of them - and they
/// looked identical, because both drew the rendered <c>burst_atlas</c>. So the
/// ear knew and the tally knew and the picture did not.
///
/// <b>What a ricochet is, as a picture, is a direction.</b> Everything here
/// leaves along the reflection of the incoming round about the plate's own
/// outward normal, and both halves of that are already measured:
/// <see cref="AtlasSet.HitBearing"/> is where a plate looks as an offset from the
/// hull's heading - the number <see cref="AtlasSet.FaceFor"/> chose the plate
/// with - and the bearing the round came in on is settled at the trigger and
/// travels with it. <see cref="Vehicle.Graze"/> is the one place that mirror is
/// taken. A grazing hit therefore throws its spall sideways and a square one
/// throws it back at the shooter, which is the whole of why the effect is worth
/// drawing rather than a puff at the point of contact.
///
/// <b>Specular, and the cone is what pays for that being a simplification.</b> A
/// real shot does not mirror: it digs in, slides up the slope and leaves closer
/// to the plate's tangent than a mirror would. The departure is inside the fan's
/// own half-angle (<c>shard_cone</c>, 24 degrees), so a knob for it would be a
/// second statement of a spread that is already there.
///
/// <b>On the board rather than in the tank's canvas, which is a departure from
/// the plan and a named one.</b> <c>docs/blast.md</c> puts every armour effect on
/// the canvas, on the argument that the tank's layers share an anchor and the
/// hull's alpha is next to them to cut against. Nothing here wants either: spall
/// starts on the outside of a plate and leaves, so there is nothing to clip it
/// with, and what it does want is the two things the canvas cannot give - depth
/// against the world, so a fan is hidden by the wall the tank is behind, and no
/// 512px edge to be sliced off by (<see cref="Stage3D.PaintSize"/>). The deflected
/// shell alone crosses two thirds of a cell. What is given up is the A/B against
/// the rendered burst in one frame; <c>--spall off</c> is that comparison across
/// two runs, which for a layer this size is what it was going to be anyway.
///
/// <b>Built out of the burst's own dust and the forest's own ink</b>, like
/// <see cref="ProcKick"/> and for its reason: <see cref="ProcBlast.DustInk"/> is
/// one statement of what dust in the air is, and this is that statement with
/// different elements and a different colour in it.
///
/// <b>Quoted in tile widths</b>, so <see cref="Might"/> is what a calibre is and
/// a board at another zoom carries the effect with it - <see cref="ProcBlast"/>'s
/// convention. <b>Time is a uniform, not <c>TIME</c></b>, and the clock goes to
/// -1 when the last dust is gone, for both of that convention's reasons.
///
/// <b>One limitation, named rather than found later.</b> The billboard is seated
/// on the victim's contact point, so - the rule that quad is written under -
/// below the contact line and behind the ground are the same test. A plate facing
/// the camera reflects toward it, which on this board is downward, so the
/// horizontal part of the reflection is clamped at the horizon
/// (<see cref="Aim"/>) and such a ricochet climbs instead of coming out at the
/// viewer. That is what spall does on a billboard whatever is written here; what
/// the clamp costs is the difference between the near plates and the far ones,
/// and <see cref="Sit"/>'s depth side is what puts that difference back.
/// </summary>
public sealed partial class ProcSpall : Node3D
{
    // --- the shape of it, in tile widths ------------------------------------

    /// <summary>
    /// How far the fan of spall gets.
    ///
    /// <b>A third of a cell, and it is the fan that is quoted rather than the
    /// event.</b> Spall is fragments of a shell and of a plate's face - grams,
    /// not kilograms - and on a board where a cell is 248px a third of one is
    /// about half a hull length: far enough to read as thrown off the armour,
    /// near enough that a hit still happens to the tank rather than to the cell.
    /// The deflected round goes <c>bolt_far</c> times this, which is what makes
    /// the fan look like the debris of something that carried on.
    /// </summary>
    public float Reach = ReachDefault;

    /// <summary>The two numbers something else has to be able to ask for -
    /// <see cref="ProcBlast.ReachDefault"/>'s arrangement and its reason: the
    /// frame's uniforms are declared at zero on purpose, so the shader text is
    /// not where a panel can read an opening value.</summary>
    public const float ReachDefault = 0.40f;
    public const float LifeDefault = 0.95f;

    /// <summary>
    /// Half the quad, across, and how far up it goes.
    ///
    /// <b>Symmetric across the seat although the effect is one-sided</b>, for
    /// <see cref="ProcKick.Flank"/>'s reason entire: which way the reflection
    /// points flips with the heading and with the plate, so a quad cut to it
    /// would have to be mirrored per hit, and mirroring the fragment's <c>x</c>
    /// mirrors the sun with it. What that costs is fill rate on a half of the
    /// quad the fan does not always use - and it turned out to be a half that
    /// cannot be culled either, see <c>spall_behind</c>'s absence in the shared
    /// frame.
    ///
    /// <b>It holds what the model can produce, not what it happens to draw.</b>
    /// The burst's rule, and the kick broke it on its first picture: a fan
    /// missing its outermost sparks looks like a smaller fan rather than like a
    /// bug. <see cref="Bounds"/> is the model asked directly, and the check
    /// asserts the quad against it at every heading of every plate of every tank
    /// - which is also the only way the impact point's own reach gets measured,
    /// since the fan starts up on the armour rather than at the seat.
    /// </summary>
    public float Flank = 1.55f;
    public float Tall = 1.50f;

    /// <summary>How far above the contact point the effect is seated, and the
    /// band it fades out over below that - <see cref="ProcBlast.Root"/>'s pair
    /// and its reason. The kick's numbers, because the ground is the ground.
    /// </summary>
    public float Root = 0.02f;
    public float Fade = 0.050f;

    /// <summary>
    /// The whole event, in seconds.
    ///
    /// <b>Short, and being short is half of what it says.</b> A ricochet is over:
    /// the flash is gone in three frames, the fan in a quarter of a second, and
    /// what is left standing is a small grey puff. A penetration has a column
    /// afterwards and a burning tank has a minute of one; an event whose dust
    /// hangs about is an event that did something. This is a shade over half the
    /// kick's 1.60s and it is the dust that sets it, exactly as there.
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
    private MeshInstance3D? _glow;
    private ShaderMaterial? _dustInk;
    private ShaderMaterial? _glowInk;
    private Vector3 _nudge;

    /// <summary>
    /// The furthest the model can put anything, in tile widths: how far out along
    /// the reflection, and how far up.
    ///
    /// <b>Asked of the model rather than of the pixels</b>, for
    /// <see cref="ProcKick.Bounds"/>'s reason: a quad cut to a screenshot's
    /// bounding box passes every screenshot and clips the day somebody lengthens
    /// an element's life. Each family's furthest is where it gets plus its own
    /// radius there; the radii grow with age, so the maximum is taken over the
    /// family rather than at the end of it.
    ///
    /// <paramref name="plate"/> is where the round hit, over the seat, because
    /// that is where every element starts - and on this effect it is the larger
    /// of the two terms going up. A plate is on the side of a hull, so the impact
    /// point stands most of a tank's height above its own contact point, and the
    /// fan climbs from there.
    /// </summary>
    public static (float Along, float Up) Bounds(float reach, Vector2 plate)
    {
        float rake = Uniform("shard_rake");
        float lift = Uniform("shard_lift");
        float open = Uniform("cone_open");
        float fanR = Uniform("shard_size") * 1.70f * Uniform("shard_long") * 1.65f;
        float boltR = Uniform("bolt_size") * 1.70f * Uniform("bolt_long");
        float wakeR = Uniform("wake_size") * 1.55f * Uniform("wake_long");
        float puffR = Uniform("puff_size") * 1.50f * 1.35f;
        float grainR = Uniform("grain_size") * 1.50f * 1.25f;
        float along = 0.0f, up = 0.0f;

        void Reach(Vector2 at, float pad)
        {
            along = MathF.Max(along, MathF.Abs(at.X) + pad);
            up = MathF.Max(up, MathF.Abs(at.Y) + pad);
        }

        // Swept over the reflection's own bearing, a degree at a time, because
        // that is the one parameter the whole model turns on and the two things
        // it decides pull against each other: a ricochet going away from the
        // camera has the shortest reach on screen and the widest cone, and the
        // furthest anything gets is somewhere in between. Bounded family by
        // family instead, the two would have been allowed to be at their worst
        // at once - which is a quad half again too tall for a fan that cannot
        // reach the top of it.
        for (int deg = 0; deg <= 90; deg++)
        {
            double th = deg * Math.PI / 180.0;
            // AtlasSet.GroundDirection, flipped into the quad's frame and clamped
            // at the horizon - Aim's two lines, and the only copy of them.
            var flat = new Vector2((float)Math.Cos(th),
                                   (float)(Math.Sin(th) * Squashed));
            float squat = flat.Length();
            Vector2 away = squat < 1e-6f ? Vector2.Right : flat / squat;
            var side = new Vector2(-away.Y, away.X);
            float cone = Uniform("shard_cone") * (1.0f + (1.0f - squat) * open);
            // The floor under the reach, which is the shader's own - see
            // squat_floor. Two statements of it would be a quad that fits the
            // fan at one bearing and clips it at another.
            float floored = Mathf.Lerp(Uniform("squat_floor"), 1.0f, squat);

            // The fan, at the top of its own speed spread and at the far edge of
            // the cone. The vertical share of the cone and the rake are one axis;
            // the cone across is the other.
            float fan = reach * floored * 1.35f;
            Reach(away * fan + side * (fan * cone)
                  + new Vector2(0.0f, fan * (rake + cone * lift)), fanR);
            // The round going away: further out, and hardly spread at all.
            float bolt = reach * Uniform("bolt_far") * 1.15f * floored;
            Reach(away * bolt + side * (bolt * Uniform("bolt_cone"))
                  + new Vector2(0.0f, bolt * rake), boltR);
            // The wake it drags, and the coat off the plate.
            float wake = reach * floored * Uniform("wake_creep");
            Reach(away * wake + side * (wake * 0.22f)
                  + new Vector2(0.0f, Uniform("wake_climb")), wakeR);
            float puff = reach * floored * Uniform("puff_lead");
            Reach(away * puff + side * Uniform("puff_spread")
                  + new Vector2(0.0f, Uniform("puff_climb")), puffR);
            // The sparks on the ground, whose height is the ground's rather than
            // the plate's - so this one is measured from the seat, not from the
            // impact, and the plate is taken back off it below.
            float grain = reach * floored * 1.15f;
            Reach(away * grain + side * (grain * cone * 0.8f)
                  + new Vector2(0.0f, Uniform("grain_seat")
                                      + Uniform("grain_hop") - plate.Y), grainR);
        }

        // Everything above is an offset from the point of impact, and the quad is
        // measured from the seat.
        return (along + MathF.Abs(plate.X), up + MathF.Abs(plate.Y));
    }

    /// <summary>
    /// How much of a ground length the camera leaves standing up the screen:
    /// <c>sin</c> of the board's own tilt.
    ///
    /// <b>A constant rather than a reading, and it is the declared convention
    /// rather than a measurement</b> - the tilt is 30 degrees everywhere in this
    /// project (see the root CLAUDE.md, and <see cref="AtlasSet.Elevation"/>,
    /// whose default it is). <see cref="Bounds"/> needs it because the reflection
    /// is a direction <em>on the ground</em>: its screen y is
    /// <c>sin(bearing) * sin(tilt)</c>, so this is the steepest anything the model
    /// throws can climb before the rake is added to it, and a bound taken without
    /// it has to allow a fan to go straight up the screen at full reach - which
    /// no bearing can ask for.
    /// </summary>
    public const float Squashed = 0.50f;

    /// <summary>One of the two shaders' own numbers, by name - so
    /// <see cref="Bounds"/> asks the shaders what they say rather than carrying a
    /// second copy of it. The glow first, because the fan is the far half and
    /// most of these are its; the dust's names fall through to it.</summary>
    private static float Uniform(string name) =>
        ProcBlast.Uniform(GlowCode, name,
                          ProcBlast.Uniform(DustSpallCode, name, 0.0f));

    /// <summary>The quad, in screen px: from <c>-Foot</c> across <c>Size</c>,
    /// which is what <see cref="Stage3D.Stem"/> takes. Its bottom edge is the
    /// contact point exactly, so <c>foot_v</c> is 1. A static function of the tile
    /// rather than read off a built node, so how big a ricochet is can be
    /// asserted with no board under it - <see cref="ProcBlast.Quad"/>'s
    /// arrangement.</summary>
    public static (Vector2 Foot, Vector2 Size) Quad(float tile, float flank, float tall)
        => ProcBlast.Quad(tile, flank, tall);

    /// <summary>
    /// Build the pair of quads. <paramref name="tile"/> is the hex's own width in
    /// screen px - every length above is in those - and the two camera terms are
    /// the field's, handed in rather than read so this needs no board.
    /// </summary>
    public void Build(float tile, float squash, float rise)
    {
        _tile = Mathf.Max(tile, 1.0f);
        (Vector2 foot, Vector2 size) = Quad(tile, Flank, Tall);
        ArrayMesh shape = Stage3D.Stem(foot, size, rise);

        _dustInk = Ink(Coating);
        _glowInk = Ink(Sparking);
        _dust = Slab(shape, _dustInk);
        _glow = Slab(shape, _glowInk);

        // Toward the camera by the board's own clearance, the glow by twice it -
        // the burst's arrangement, so the dark half is behind the additive one
        // rather than sorting against it by the coin toss two coplanar quads get.
        _nudge = Stage3D.Clear(squash, rise);
        Stand();
    }

    /// <summary>How big this one is, as a multiple of the tuned ricochet -
    /// <see cref="ProcBlast.Might"/>'s argument entire, including that it scales
    /// about the seat rather than about its own middle.</summary>
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
    private float _side = 1.0f;

    /// <summary>
    /// Where it stands: the flat point the tank is touching and the lift of the
    /// cell under it, through the same transform a tree gets - and which side of
    /// that tank's own surface it is on.
    ///
    /// <b>The victim's contact point, not the point of impact.</b> The impact is
    /// up on a plate, and its height is the one thing this quad cannot use: the
    /// quad's bottom edge <em>is</em> the ground. Where the impact is is the
    /// model's business, handed to <see cref="Aim"/> as an offset - the same split
    /// the kick makes with its muzzle, and for the same reason: a seat on the
    /// ground is a seat whose lift is known exactly.
    ///
    /// <b><paramref name="behind"/> is the whole of what a billboard on a hull's
    /// own foot cannot otherwise say.</b> Two quads standing on one contact point
    /// sort by nothing, so spall thrown off the far side of a tank would be drawn
    /// over the hull it came from - a fan of sparks in front of the armour that
    /// stopped the round. The clearance is a vector toward the camera that leaves
    /// the picture where it is (see <see cref="Stage3D.Clear"/>), so the sign of
    /// it is exactly "in front of the tank" or "behind it", and the caller already
    /// knows which: <see cref="AtlasSet.HitFacing"/>, whose sign is what the
    /// rendered layer has always used for this. Nothing about the drawing changes
    /// with it, only what covers what.
    /// </summary>
    public void Sit(Vector2 ground, float lift, float squash, float rise,
                    bool behind = false)
    {
        _seat = Stage3D.Trunk(ground, lift, 0.0f, squash, rise);
        _side = behind ? -1.0f : 1.0f;
        Stand();
    }

    /// <summary>The seat, the size and the side together, the quads' own nudges
    /// divided back out - the burst's <c>Stand</c>'s reason: a clearance is a
    /// clearance whatever size the effect is.</summary>
    private void Stand()
    {
        Transform = _seat.ScaledLocal(Vector3.One * _might);
        if (_dust is not null)
            _dust.Position = _nudge * _side / _might;
        if (_glow is not null)
            _glow.Position = _nudge * 2.0f * _side / _might;
    }

    /// <summary>Which side of the tank's own surface this one is on, as
    /// <see cref="Sit"/> was told - for the check, which cannot read a node's
    /// depth off a picture.</summary>
    public bool Behind => _side < 0.0f;

    /// <summary>
    /// Which way the spall went, and where on the tank it left from.
    ///
    /// <paramref name="away"/> is the reflected direction as
    /// <see cref="AtlasSet.GroundDirection"/> hands it over: a screen vector whose
    /// direction is where the round went and whose <em>length</em> is the share of
    /// a ground length that survived the projection. <b>Both halves are used</b>,
    /// for <see cref="ProcKick.Aim"/>'s reason: the direction says which way, the
    /// length says how far it appears to, because a ricochet into the screen
    /// covers the same distance in the world and less of the picture.
    ///
    /// <b>Screen y points down and the quad's y points up</b>, so the vertical
    /// part is negated coming in - and then clamped at the horizon, which is the
    /// limitation in the class note.
    ///
    /// <paramref name="plate"/> is where the round hit, in screen px off the
    /// contact point. Handed over rather than assembled here, and this is the
    /// error the kick already paid for once: the quad is seated on the contact
    /// point because that is the one point whose lift is known, and the contact
    /// point is under the middle of the hull - so a fan placed from there starts
    /// <em>inside the tank</em>. Every term of the impact point is measured
    /// already, on the plate's own two axes (see <see cref="Vehicle.Graze"/>), so
    /// it comes in whole.
    /// </summary>
    public void Aim(Vector2 away, Vector2 plate)
    {
        float squat = Mathf.Clamp(away.Length(), 0.0f, 1.0f);
        var quad = new Vector2(away.X, -away.Y);
        if (quad.Y < 0.0f)
            quad.Y = 0.0f;
        Vector2 gone = quad.LengthSquared() < 1e-8f
            ? Vector2.Right : quad.Normalized();
        // Screen y down, quad y up - the same flip the direction takes, and for
        // the same reason.
        var hit = new Vector2(plate.X, -plate.Y) / Mathf.Max(_tile, 1.0f);
        foreach (ShaderMaterial? ink in new[] { _dustInk, _glowInk })
        {
            ink?.SetShaderParameter("away", gone);
            ink?.SetShaderParameter("squat", squat);
            ink?.SetShaderParameter("plate", hit);
        }
        Away = gone;
        Squat = squat;
        Plate = hit;
    }

    /// <summary>Which way the spall went and how much of the throw survived the
    /// projection, as <see cref="Aim"/> worked them out - for a bench that has to
    /// show what one is doing, and for the check that the clamp happened.
    /// </summary>
    public Vector2 Away { get; private set; } = Vector2.Right;
    public float Squat { get; private set; } = 1.0f;

    /// <summary>Where the round hit, over the seat, in tile widths - what
    /// <see cref="Aim"/> made of the offset it was handed.</summary>
    public Vector2 Plate { get; private set; } = Vector2.Zero;

    /// <summary>The hex's own width in screen px, kept because <see cref="Aim"/>
    /// is handed the impact point in px and every length in the model is in
    /// tiles.</summary>
    private float _tile = 1.0f;

    /// <summary>Set it off. Restarts rather than refusing, for
    /// <see cref="ProcBlast.Fire"/>'s reason: the organ that fires it is a key on
    /// a bench.</summary>
    public void Fire() => _clock = 0.0f;

    /// <summary>Whether the clock stands still - <see cref="ProcBlast.Hold"/>,
    /// and the main way an event gets looked at.</summary>
    public bool Hold;

    /// <summary>Where the clock is, settable, so a held ricochet can be scrubbed
    /// to the moment being judged.</summary>
    public float Clock
    {
        get => _clock;
        set => _clock = Mathf.Clamp(value, 0.0f, Life);
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
        if (_glow is not null)
            _glow.Visible = on;
        if (!on)
            return;

        _dustInk?.SetShaderParameter("time", _clock);
        _glowInk?.SetShaderParameter("time", _clock);
    }

    /// <summary>One number of one of the two shaders, live - read out of the
    /// shader's own text the first time it is asked for and never kept twice.
    /// <see cref="ProcKick.Dial(ProcKick.Part, string)"/>'s arrangement and its
    /// reason.</summary>
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

    /// <summary>The same number, written - and to both materials when the frame
    /// is what owns it, because the frame is the text they are both built
    /// from.</summary>
    public void Dial(Part part, string uniform, float value)
    {
        _live[part + "." + uniform] = value;
        bool counted = Source(part).Contains("uniform int " + uniform + " = ",
                                             StringComparison.Ordinal);
        Variant sent = counted ? Mathf.RoundToInt(value) : value;
        Coat(part)?.SetShaderParameter(uniform, sent);
        if (part == Part.Frame)
        {
            _dustInk?.SetShaderParameter(uniform, sent);
            _glowInk?.SetShaderParameter(uniform, sent);
        }
    }

    /// <summary>Which of the two a number belongs to. <see cref="Part.Frame"/> is
    /// the text they share.</summary>
    public enum Part { Frame, Dust, Glow }

    private readonly System.Collections.Generic.Dictionary<string, float> _live = new();

    private static string Source(Part part) => part switch
    {
        Part.Dust => DustSpallCode,
        Part.Glow => GlowCode,
        // Both frames, because both are the text the two halves share: the
        // burst's says how big the quad is and where the seat sits in it, this
        // effect's says which way the round went and where it hit.
        _ => ProcBlast.FrameCode + SpallFrame,
    };

    private ShaderMaterial? Coat(Part part) => part switch
    {
        Part.Dust => _dustInk,
        Part.Glow => _glowInk,
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
        ink.SetShaderParameter("away", Vector2.Right);
        ink.SetShaderParameter("squat", 1.0f);
        ink.SetShaderParameter("plate", Vector2.Zero);
        // <b>The kick's helping of the shared banding and tearing, and for its
        // reasons at a smaller size still.</b> The steps read as shading on a
        // mass in transition and as slabs on one that saturates; this is a
        // handful of elements over a third of a cell, which saturates wherever
        // they overlap at all. Written on the material rather than in the shared
        // text, because the shared numbers are right for the effect they were
        // tuned on - the burst's dense cone a cell across.
        ink.SetShaderParameter("dust_band", 0.28f);
        ink.SetShaderParameter("dust_tear", 0.52f);
        // <b>And less opaque than either, which is this effect's own.</b> What is
        // knocked off a plate is grams of paint and dirt: it has to show the
        // armour through it, or a ricochet reads as a smoke grenade going off on
        // the hull. The shared 0.72 is a shell's worth of earth.
        ink.SetShaderParameter("dust_ink", 0.62f);
        return ink;
    }

    /// <summary>What both halves are told about which way the round went and
    /// where it hit, and the one test that lets a symmetric quad carry a
    /// one-sided effect. Shared text rather than a copy per shader, for the
    /// burst's frame's reason.</summary>
    internal const string SpallFrame = @"
// Where the round went, as a unit vector in the quad's own frame: x across the
// screen, y up it. Written by ProcSpall.Aim every frame one is dressed - declared
// pointing right so a run that forgot to write it throws its spall sideways
// rather than nothing, which is the opposite of the burst's frame and deliberate:
// this one cannot be zero and still mean anything.
uniform vec2 away = vec2(1.0, 0.0);
// How much of a ground length survived the projection, in 0..1. A ricochet into
// the screen covers the same distance in the world and less of the picture.
uniform float squat = 1.0;
// Where the round hit, over the seat, in tile widths - see ProcSpall.Aim. The
// seat is the victim's contact point because that is where the ground is known;
// the spall starts on the armour, which is most of a hull's height above it.
uniform vec2 plate = vec2(0.0);
// <b>How much of the fan's own size survives when the projection has taken
// everything it can.</b> squat is right for the axial travel - a ricochet into
// the screen covers the same ground and less of the picture, which is why it is
// there - and wrong for the fan itself: a cone has a real width in every
// direction and does not get smaller because the camera is looking down its
// axis. Without this floor a hit on a plate turned away came out a short bright
// stub standing above the hull, a quarter of the area for the same shell, and it
// read as something burning on the engine deck rather than as sparks. The
// steepest a ground direction can be on this board leaves squat at 0.5, so this
// is what that case is worth.
//
// <b>In the shared frame because both halves spend it</b>, and that is not a
// tidiness note: declared in the glow alone, the dust half compiled to nothing
// at all - unknown identifier squat_floor - and a material whose shader
// failed draws a black rectangle the size of its quad over the board. See the
// check that both halves take it from here.
uniform float squat_floor = 0.55;

// Across the reflection, in the quad's own frame. The fan's lateral spread goes
// along this, which is an approximation and a knowing one: on the ground lateral
// is a direction in depth as well as across, and a billboard has no depth. What
// carries the fan's read is the rake and the stretch, not this.
vec2 spall_across() {
    return vec2(-away.y, away.x);
}

// <b>There is no early bail here, and that is a finding rather than an
// omission.</b> The kick has one because its dust is a lobe lying along one
// direction, so half its quad is reliably empty and leaves in a dot product.
// This is a splash, not a lobe: the fan opens to forty degrees, the cone opens
// further as the projection closes it, the wake and the coat leave the plate in
// every direction, and the sparks that reach the ground are a hull's height
// below the impact. Written anyway - across the screen, a fifth of a tile of
// margin - it cut the up-and-left half off a fan aimed up and right, and the
// result read as a narrow tongue: the plume this effect must not look like,
// arrived at by an optimisation. Every element still bails inside flame_reach
// before it costs anything, which is where a fan of sixty small elements wants
// its bail anyway.
";

    /// <summary>
    /// The dust: the coat knocked off the plate, and the thin wake the fan drags
    /// with it.
    ///
    /// <b>Grey, and that is a statement about what this dust is rather than a
    /// tint.</b> The burst's pair is earth and the kick's young pair is
    /// propellant; what comes off a plate is paint, primer, dirt and a few grams
    /// of the plate's own face. Taking either of the other pairs would have put
    /// a puff of brown ground or a puff of white smoke on the side of a tank, and
    /// both of those say the round did something it did not.
    ///
    /// <b>One pair of stops, not the kick's two.</b> The kick walks white to grey
    /// because the reference frames do - a mass that starts as propellant and
    /// ends as hanging dust is two materials. This is one material for the whole
    /// of a second, and a walk would be inventing a phase the event does not
    /// have.
    ///
    /// <b>The dark end is high, and it is high on purpose rather than by
    /// taste.</b> The finding is the kick's and it is paid for once:
    /// <c>dust_seat</c> is 0.52 and a saturated mass takes <c>dust_dense</c> off
    /// it, so a dense element shades at about 0.24 and nearly every pixel of it
    /// shows the <em>dark</em> stop. A pair whose dark end is a dark grey is a
    /// near-black puff whatever its lit end says.
    ///
    /// <b>Ordinary alpha, premultiplied inside and divided back out</b> - the
    /// burst's arrangement, unchanged, and for the finding underneath it: summed
    /// instead, an element's dark lip <em>brightens</em> the one behind it.
    /// </summary>
    private const string DustSpallShader = @"
shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_never;

FLAME_NOISE

FLAME_INK

BLAST_FRAME

DUST_INK

SPALL_FRAME

// The puff: what the round knocked off the face of the plate. Born at the impact
// point, leaving it in a wide cone, climbing a little, and outliving everything
// that glows.
uniform int puff = 14;
uniform float puff_life = 0.55;
uniform float puff_stagger = 0.045;
// How far along the reflection the puff is carried, as a share of the fan's
// reach. Small: the coat comes off where the round hit and mostly stays there,
// which is what makes it read as belonging to the plate rather than to the fan.
uniform float puff_lead = 0.22;
// How far it spreads on its own, in tile widths, and how far it climbs. A plate
// is vertical, so what leaves it leaves sideways before it rises.
uniform float puff_spread = 0.075;
uniform float puff_climb = 0.085;
uniform float puff_size = 0.052;
uniform float puff_ink = 0.95;

// The wake: the thin dust the fan drags along with it, and the half of this
// shader that stops the sparks reading as a firework. A spark with nothing around
// it is a dot; a spark inside a wisp of dust is debris.
uniform int wake = 9;
uniform float wake_life = 0.62;
uniform float wake_born = 0.05;
uniform float wake_stagger = 0.13;
uniform float wake_creep = 0.78;
uniform float wake_climb = 0.16;
uniform float wake_size = 0.052;
// Stretched along the reflection: this family is the fan's own trail, and a round
// trail is a row of balls.
uniform float wake_long = 2.10;
uniform float wake_ink = 0.34;

// <b>What comes off a plate, dark and lit.</b> Neutral and a shade cool - steel
// primer and road dirt, not earth - and kept just off neutral rather than dead
// grey, which against this tan ground reads blue.
uniform vec3 coat_dark = vec3(0.545, 0.548, 0.562);
uniform vec3 coat_lit = vec3(0.955, 0.958, 0.972);

void fragment() {
    if (level <= 0.0) {
        ALBEDO = vec3(0.0);
        ALPHA = 0.0;
    } else {
        vec2 at = blast_at(UV);
        {
            vec2 side_dir = spall_across();
            float run = reach * mix(squat_floor, 1.0, squat);

            // One pass that adds up dust and one that shades the result - the
            // burst's finding, in its own words: a cloud is not a set of things,
            // it is one volume of varying thickness, and compositing the elements
            // over each other gives every one of them a lip for the eye to count.
            float dens = 0.0;
            vec2 lean = vec2(0.0);
            float years = 0.0;

            for (int k = 0; k < puff; k++) {
                float fk = float(k);
                float born = puff_stagger * ember_hash(vec2(fk, 61.0));
                float a = (time - born) / max(puff_life, 1e-3);
                if (a > 0.0 && a < 1.0) {
                    // Out of the plate in a wide cone, so the distance off the
                    // reflection grows with the distance travelled - the kick's
                    // embers' finding, and it applies to anything that leaves a
                    // point rather than a volume.
                    float out_at = puff_lead * run * pow(a, 0.45);
                    float wide = puff_spread * pow(a, 0.5)
                                 * (2.0 * ember_hash(vec2(fk, 62.0)) - 1.0);
                    float up = puff_climb * pow(a, 0.75)
                               * (0.30 + 0.95 * ember_hash(vec2(fk, 63.0)));
                    vec2 p = plate + away * out_at + side_dir * wide
                             + vec2(0.0, up);
                    float r = puff_size * (0.60 + 0.90 * ember_hash(vec2(fk, 64.0)))
                              * (0.40 + 1.10 * a);
                    float gain = puff_ink * (1.0 - exp(-a / 0.05))
                                 * pow(max(1.0 - a, 0.0), 1.25);
                    dust_part(at, p, r, r * 1.35, vec2(0.0, 1.0), fk + 61.0,
                              gain, dust_soft, a, dens, lean, years);
                }
            }

            for (int k = 0; k < wake; k++) {
                float fk = float(k);
                float born = wake_born + wake_stagger * ember_hash(vec2(fk, 51.0));
                float a = (time - born) / max(wake_life, 1e-3);
                if (a > 0.0 && a < 1.0) {
                    float lead = mix(0.30, 1.0, ember_hash(vec2(fk, 52.0)));
                    float out_at = run * wake_creep * lead * pow(a, 0.50);
                    float wide = out_at * 0.22
                                 * (2.0 * ember_hash(vec2(fk, 53.0)) - 1.0);
                    float up = wake_climb * pow(a, 0.70)
                               * (0.35 + 0.90 * ember_hash(vec2(fk, 54.0)));
                    vec2 p = plate + away * out_at + side_dir * wide
                             + vec2(0.0, up);
                    float r = wake_size * (0.55 + 0.95 * ember_hash(vec2(fk, 55.0)))
                              * (0.45 + 1.10 * a);
                    float gain = wake_ink * (1.0 - exp(-a / 0.07))
                                 * pow(max(1.0 - a, 0.0), 1.30);
                    dust_part(at, p, r, r * wake_long, away, fk + 51.0,
                              gain, dust_soft, a, dens, lean, years);
                }
            }

            if (dens <= 1e-4) {
                ALBEDO = vec3(0.0);
                ALPHA = 0.0;
            } else {
                float mass = dust_mass(at, dens);
                float lit = dust_lit(mass, lean);
                ALBEDO = mix(coat_dark, coat_lit, lit);
                ALPHA = clamp(mass * dust_ink * level * blast_footing(at),
                              0.0, 1.0);
            }
        }
    }
}
";

    /// <summary>
    /// The light: the flash on the plate, the fan of spall, the round going away
    /// and the sparks that reached the ground.
    ///
    /// <b>Four windows, and the spread between them is what the event is.</b> The
    /// flash is out in three frames, the fan in a quarter of a second, the round
    /// is gone before the fan is, and the last sparks are skittering on the ground
    /// at half a second. Held the same length they would be a small firework;
    /// staggered like this they are one thing arriving and its consequences.
    ///
    /// <b>Nothing here is thrown ballistically, and it is not the kick's reason
    /// either.</b> A shell's earth leaves a seat and comes back down, which is
    /// what <c>blast_throw</c> states; a muzzle blast pushes dust out and it
    /// decelerates. Spall does both at once and neither in the burst's form: it
    /// leaves fast along one direction, air stops it hard, and what is left
    /// falls. So each family is a deceleration along its own line with a drop
    /// term under it, and the drop is quadratic in the age because that is what
    /// falling is.
    ///
    /// <b>The fan cools, and it cools to the ink's own ember colour.</b>
    /// Incandescent steel goes white to yellow to red and then out, which is a
    /// ramp - but not the flame's ramp: <see cref="Stage3D.FlameInk"/>'s is a
    /// gas's, and its hot end is the orange a burning tree wants. A spark's hot
    /// end is very nearly white, so this walks its own two colours and both ends
    /// of that walk are stated here.
    ///
    /// <b>The white is bought with brightness and not with a whiter colour</b> -
    /// the rule the forest's fire, the burst and the kick's core are all written
    /// under. An additive layer over one clips its own middle to white on its own.
    ///
    /// <b>The stretch lies along where the element came from, not along the
    /// quad.</b> <see cref="SheetBlast"/>'s lances' finding: a streak whose long
    /// axis is on the line from the point of impact to where it is now reads as
    /// something travelling, and one drawn along the quad reads as a scratch on
    /// the screen. And it rounds off as the element slows, because a streak that
    /// has stopped is a lump.
    /// </summary>
    private const string GlowSpallShader = @"
shader_type spatial;
render_mode unshaded, cull_disabled, blend_add, depth_draw_never;

FLAME_NOISE

FLAME_INK

BLAST_FRAME

SPALL_FRAME

// The flash: what the round makes of itself on the plate. Three frames, at the
// point of contact, and it is the one part of a ricochet a penetration also has -
// so it is small and it is early, and everything that distinguishes the two comes
// after it.
uniform int bloom = 3;
uniform float bloom_life = 0.075;
uniform float bloom_stagger = 0.012;
uniform float bloom_size = 0.042;
uniform float bloom_long = 1.20;
// <b>Its own pale body rather than a place on the shared ramp</b>, for the
// kick's core's reason, arrived at there and paid for there: the ramp's hot end is
// an orange, correctly, because it is a flame's, and a flame on the side of a
// tank is a tank on fire. What this is is a clipped sensor - white with a yellow
// edge - and the honest way to say that is a pale body and a gain under one.
uniform vec3 bloom_colour = vec3(1.000, 0.958, 0.882);
uniform float bloom_gain = 0.95;

// The fan: fragments of the shell and of the plate's face, leaving along the
// reflection.
//
// <b>They all leave at once, which is the kick's embers' finding used rather than
// rediscovered.</b> Staggered over any real fraction of the event they would be
// fresh sparks appearing in settled dust - a source that keeps running, which is
// what a fire looks like. The stagger left is the width of one impact.
uniform int shards = 34;
uniform float shard_life = 0.34;
uniform float shard_stagger = 0.035;
// The exponent on the age. Well under 1: a fragment of a plate is small and light
// and air stops it inside a few of its own lengths, so it covers most of its
// distance in the first fifth of its life and crawls after that.
uniform float shard_drag = 0.42;
// Half-slope of the cone they leave in, and how much of it goes up rather than
// across. Multiplied by the distance travelled rather than used as an offset -
// the kick's embers again: what leaves a point leaves in a cone, and at age zero
// every fragment is at the point of impact.
uniform float shard_cone = 0.52;
uniform float shard_lift = 0.85;
// <b>How much wider the fan opens as the projection takes its reach away, and
// this is the answer to the one case that read badly.</b> A ricochet going
// straight away from the camera has no horizontal share left at all, so on a
// billboard every fragment goes up the screen and thirty-four of them in a
// narrow column is a tongue of flame - which is the one thing this effect must
// not look like. Seen end-on a cone <em>is</em> a disc, so what squat takes off
// the distance it gives to the spread, and such a hit splashes rather than
// stands up. It costs the quad almost nothing: the reach shrinks by squat while
// the cone grows by this, and the product peaks at 1.06 of the square-on case.
uniform float cone_open = 1.60;
// <b>How much the fan is raked up off the plate, as a share of how far out it
// gets.</b> Not decoration and not the cone: armour is sloped, so a round that
// fails to get in is deflected upward as well as sideways, and a fan lying flat
// off the side of a hull reads as something sprayed rather than something
// bounced. It is also what keeps the near plates drawable - see the class note on
// the horizon clamp.
uniform float shard_rake = 0.38;
// How much of the reach the fall takes back by the end, and it is quadratic in
// the age because that is what falling is.
uniform float shard_drop = 0.30;
uniform float shard_size = 0.0072;
// How long a fragment is drawn against how wide, while it is still fast. It
// rounds off as it slows.
uniform float shard_long = 7.00;
uniform float shard_gain = 1.25;
// The two ends of the walk a cooling fragment takes. The hot end is nearly white
// because incandescent steel is; the cold end is the ink's own ember colour,
// which is pulled toward red rather than the yellow a real spark is - a few
// pixels of low-alpha yellow on a pale field is a grey speck, measured.
uniform vec3 shard_hot = vec3(1.000, 0.900, 0.680);
uniform float shard_cool = 0.85;

// The round itself, going away: one or two streaks brighter than the fan, far
// past it, and almost on the reflection rather than spread about it.
//
// <b>This is the element that says the shot bounced rather than broke up</b>, and
// it is why the quad is as wide as it is: it is the only thing here that leaves
// the tank's own cell. Two, because a shell that shatters on the way off leaves
// its core and its jacket, and because one streak reads as a bug.
uniform int bolts = 2;
uniform float bolt_life = 0.22;
uniform float bolt_far = 2.05;
uniform float bolt_drag = 0.68;
uniform float bolt_cone = 0.085;
uniform float bolt_size = 0.011;
uniform float bolt_long = 9.00;
uniform float bolt_gain = 1.35;

// The sparks that got to the ground, and the cheapest thing in the file: they are
// what puts the event in the world rather than on the tank. Born late, low,
// bouncing with a decaying hop, out along the reflection.
//
// <b>Seated above the ground line rather than on it</b>, because there is no
// drawing below it - blast_footing fades out over a band above the line, so a
// spark at zero is a spark nobody sees.
uniform int grains = 10;
uniform float grain_life = 0.30;
uniform float grain_born = 0.075;
uniform float grain_stagger = 0.20;
uniform float grain_seat = 0.016;
uniform float grain_hop = 0.055;
// How fast the hop decays and how many of them there are inside a life. Two and a
// bit: more reads as a housefly, fewer as a dropped stone.
uniform float grain_settle = 2.40;
uniform float grain_beat = 13.0;
uniform float grain_size = 0.016;
uniform float grain_gain = 0.42;

void fragment() {
    if (level <= 0.0) {
        ALBEDO = vec3(0.0);
        ALPHA = 0.0;
    } else {
        vec2 at = blast_at(UV);
        {
            vec2 side_dir = spall_across();
            float run = reach * mix(squat_floor, 1.0, squat);
            // The fan's half-slope, opened by however much the projection has
            // taken off its reach - see cone_open.
            float cone = shard_cone * (1.0 + (1.0 - squat) * cone_open);
            // Alpha-over inside the layer, additive on the way out - the flash's
            // arrangement and the burst's fire's: surfaces stack the way surfaces
            // stack, and the layer as a whole adds light.
            vec4 lit = vec4(0.0);

            for (int k = 0; k < bloom; k++) {
                float fk = float(k);
                float born = bloom_stagger * ember_hash(vec2(fk, 11.0));
                float a = (time - born) / max(bloom_life, 1e-3);
                if (a > 0.0 && a < 1.0) {
                    float off = bloom_size * 0.5
                                * (2.0 * ember_hash(vec2(fk, 12.0)) - 1.0);
                    float up = bloom_size * 0.5
                               * (2.0 * ember_hash(vec2(fk, 13.0)) - 1.0);
                    vec2 p = plate + side_dir * off + vec2(0.0, up)
                             + away * (bloom_size * 0.7 * a);
                    float r = bloom_size * (0.65 + 0.75 * ember_hash(vec2(fk, 14.0)))
                              * (0.55 + 1.05 * a);
                    // Brightest the instant it arrives and still growing as it
                    // dims, which is what makes it read as a thing happening
                    // rather than as a lamp switched on.
                    float gain = bloom_gain * (1.0 - exp(-a / 0.015))
                                 * pow(max(1.0 - a, 0.0), 1.70);
                    lit = flame_over(lit, flame_spark(at, p, r, r * bloom_long,
                                                      away, fk + 11.0,
                                                      bloom_colour, gain));
                }
            }

            for (int k = 0; k < shards; k++) {
                float fk = float(k);
                float born = shard_stagger * ember_hash(vec2(fk, 21.0));
                float a = (time - born) / max(shard_life, 1e-3);
                if (a > 0.0 && a < 1.0) {
                    float fast = 0.25 + 1.10 * ember_hash(vec2(fk, 22.0));
                    float out_at = run * fast * pow(a, shard_drag);
                    // The cone: a direction taken at the plate, so the distance
                    // off the reflection is the distance travelled times the
                    // slope.
                    float wide = out_at * cone
                                 * (2.0 * ember_hash(vec2(fk, 23.0)) - 1.0);
                    float up = out_at * (shard_rake
                               + cone * shard_lift
                                 * (2.0 * ember_hash(vec2(fk, 24.0)) - 1.0))
                               - run * shard_drop * a * a;
                    vec2 p = plate + away * out_at + side_dir * wide
                             + vec2(0.0, up);
                    // Along the line from where it hit to where it is - see the
                    // class note. Round when it has stopped.
                    vec2 trail = p - plate;
                    vec2 flow = length(trail) < 1e-4 ? away : normalize(trail);
                    // <b>Width and length vary independently, and that is what
                    // stops thirty-four streaks reading as a flower.</b> Tied
                    // together - one hash for both - every element is the same
                    // shape at a different size, and a fan of one shape radiating
                    // from one point is petals. Spall is mostly hair-thin with a
                    // few long ones in it, so the two rulers get their own hash.
                    float r = shard_size * (0.45 + 1.25 * ember_hash(vec2(fk, 25.0)));
                    float draw = mix(shard_long
                                     * (0.45 + 1.20 * ember_hash(vec2(fk, 26.0))),
                                     1.0, clamp(a * 1.35, 0.0, 1.0));
                    float gain = shard_gain * (1.0 - exp(-a / 0.02))
                                 * pow(max(1.0 - a, 0.0), 1.30);
                    vec3 body = mix(shard_hot, ember_colour,
                                    smoothstep(0.0, max(shard_cool, 1e-3), a));
                    lit = flame_over(lit, flame_spark(at, p, r, r * draw, flow,
                                                      fk + 21.0, body, gain));
                }
            }

            for (int k = 0; k < bolts; k++) {
                float fk = float(k);
                float a = time / max(bolt_life, 1e-3);
                if (a > 0.0 && a < 1.0) {
                    float out_at = run * bolt_far
                                   * (0.80 + 0.35 * ember_hash(vec2(fk, 31.0)))
                                   * pow(a, bolt_drag);
                    float wide = out_at * bolt_cone
                                 * (2.0 * ember_hash(vec2(fk, 32.0)) - 1.0);
                    float up = out_at * shard_rake
                               - run * shard_drop * 0.5 * a * a;
                    vec2 p = plate + away * out_at + side_dir * wide
                             + vec2(0.0, up);
                    vec2 trail = p - plate;
                    vec2 flow = length(trail) < 1e-4 ? away : normalize(trail);
                    float r = bolt_size * (0.80 + 0.40 * ember_hash(vec2(fk, 33.0)));
                    float gain = bolt_gain * (1.0 - exp(-a / 0.015))
                                 * pow(max(1.0 - a, 0.0), 1.10);
                    vec3 body = mix(shard_hot, ember_colour,
                                    smoothstep(0.0, 1.0, a));
                    lit = flame_over(lit, flame_spark(at, p, r, r * bolt_long,
                                                      flow, fk + 31.0, body,
                                                      gain));
                }
            }

            for (int k = 0; k < grains; k++) {
                float fk = float(k);
                float born = grain_born + grain_stagger * ember_hash(vec2(fk, 41.0));
                float a = (time - born) / max(grain_life, 1e-3);
                if (a > 0.0 && a < 1.0) {
                    float out_at = run * (0.20 + 0.95 * ember_hash(vec2(fk, 42.0)))
                                   * pow(a, 0.55);
                    float wide = out_at * cone * 0.8
                                 * (2.0 * ember_hash(vec2(fk, 43.0)) - 1.0);
                    // Skittering: a hop whose height decays, so it settles rather
                    // than stopping.
                    float hop = grain_hop * exp(-grain_settle * a)
                                * abs(sin(a * grain_beat));
                    vec2 p = plate + away * out_at + side_dir * wide
                             + vec2(0.0, grain_seat + hop - plate.y);
                    float r = grain_size * (0.65 + 0.80 * ember_hash(vec2(fk, 44.0)));
                    float gain = grain_gain * (1.0 - exp(-a / 0.04))
                                 * pow(max(1.0 - a, 0.0), 1.50);
                    lit = flame_over(lit, flame_spark(at, p, r, r * 1.25, away,
                                                      fk + 41.0, ember_colour,
                                                      gain));
                }
            }

            // Premultiplied by the compositing above, so the alpha is spent and a
            // nought is handed over: an adding layer has light to give and nothing
            // to hide behind.
            ALBEDO = lit.rgb * (level * blast_footing(at));
            ALPHA = 1.0;
        }
    }
}
";

    /// <summary>The dust as it is compiled - the shared noise, the shared ink, the
    /// burst's frame, the burst's dust and this effect's own frame, put together
    /// in one place so a check can ask whether the ricochet, the kick and the
    /// burst are one text.</summary>
    internal static readonly string DustSpallCode =
        DustSpallShader.Replace("FLAME_NOISE", Stage3D.EmberNoiseCode)
                       .Replace("FLAME_INK", Stage3D.FlameInk)
                       .Replace("BLAST_FRAME", ProcBlast.FrameCode)
                       .Replace("DUST_INK", ProcBlast.DustInk)
                       .Replace("SPALL_FRAME", SpallFrame);

    internal static readonly string GlowCode =
        GlowSpallShader.Replace("FLAME_NOISE", Stage3D.EmberNoiseCode)
                       .Replace("FLAME_INK", Stage3D.FlameInk)
                       .Replace("BLAST_FRAME", ProcBlast.FrameCode)
                       .Replace("SPALL_FRAME", SpallFrame);

    private static readonly Shader Coating = new() { Code = DustSpallCode };
    private static readonly Shader Sparking = new() { Code = GlowCode };
}
