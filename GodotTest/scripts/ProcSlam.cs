using System;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// A high-explosive round bursting on the face of a plate: the core, the
/// fireball rolling off the armour, the soot and dust that follows it, the
/// fragments, and what runs down the hull and pools at the tracks.
///
/// <b>The frame that says the shell stopped, and it is the opposite of a
/// ricochet in the one way that matters.</b> <see cref="ProcSpall"/> is a
/// reflection: everything in it leaves along the mirror of the incoming round
/// about the plate's outward normal, and the incoming bearing is therefore half
/// of what the picture is. Nothing here leaves along anything. HE does not go
/// away - it stops dead and its gases go where the metal is not, which is
/// <em>out along the plate's own normal</em> and very nearly nowhere else. So
/// the bearing the round arrived on drops out of the model entirely, and
/// <see cref="Vehicle.Blown"/> is <see cref="Vehicle.Graze"/> with the
/// subtraction taken off it.
///
/// <b>Which makes this a relative of the burst rather than of the ricochet, and
/// the families say so.</b> Spall is countable metal - thirty-four hair-thin
/// streaks and a deflected shell - so its ink is <see cref="Stage3D.FlameInk"/>
/// and its dust is an afterthought. HE is a medium: a core, a ball of flame and a
/// mass of soot, none of which has a countable element in it. So the weight is
/// the other way round - <see cref="ProcBlast.DustInk"/> carries the picture and
/// the additive half is the short bright start of it - and what would be the
/// ricochet's fan is here a dozen fragments that <em>fall</em>, with no bolt among
/// them at all. The one long streak of a round that carried on is exactly the
/// thing an HE hit must not have, and its absence is as much of the difference as
/// the fireball is.
///
/// <b>Drawn instead of the rendered burst, and the scar is nobody's business
/// here.</b> Which picture gets drawn and what mark the plate keeps are decided
/// in two different places: the mark comes out of
/// <see cref="TankSprite.DamageTo"/>, which runs <em>above</em> the fork in
/// <see cref="TankTick.Land"/> and so is reached by every hit alike - a burst, a
/// bounce and a penetration each leave the mark their own level earned, and a
/// bounce's is the level-0 scorch. So "the scar already exists and stays", which
/// is what docs/blast.md asks of this effect, needs no line of code at all: it is
/// a consequence of where the scar is written, and the fork is two lines over a
/// threshold that already existed.
///
/// <b>What the ammunition means, and the boundary is deliberate.</b> This gives
/// <see cref="TankTick.Ammo"/> a meaning against armour it did not have, and only
/// a pictorial one: <see cref="Gunnery.Penetration"/> is untouched, HE does no
/// more or less damage than AP, and a round of either kind kills on the same
/// third penetration. What changes is that a shell which bursts is now drawn
/// bursting. Writing HE into the damage table is a change to what the bench
/// <i>does</i>, it is a separate piece of work, and it is not this one.
///
/// <b>The impact frame is <see cref="ProcSpall.SpallFrame"/>, shared rather than
/// copied</b> - <see cref="ProcBlast.DustInk"/>'s argument at a smaller scale.
/// Both effects are a thing happening at a measured point on a hull and leaving
/// along a measured direction; the direction's meaning differs and its arithmetic
/// does not, and the frame carries the two findings that cost the most
/// (<c>squat_floor</c> and <c>cone_open</c>), which apply here word for word.
/// What is <em>not</em> shared is which elements there are and what colour they
/// come out - exactly the line the shared dust draws too.
///
/// <b>Quoted in tile widths</b> so <see cref="Might"/> is what a calibre is, and
/// <b>time is a uniform, not <c>TIME</c></b> - <see cref="ProcBlast"/>'s two
/// conventions, both for their own reasons.
///
/// <b>Two surfaces, one text, and the switch is a single uniform.</b>
/// docs/blast.md's <c>wall_he</c> turned out to be exactly what it was predicted
/// to be - this picture with a paler dust, hardly any fire and its mass running
/// along the wall instead of out from it - so it is <see cref="Face"/> rather than
/// a second shader. Every number of both sets lives in the shader text, as the
/// tuned numbers on this board always do; what C# writes is the one number that
/// says which surface this is, and every masonry term is a <c>mix</c> against
/// zero, so an armour burst is the same arithmetic it was before the second
/// surface existed.
///
/// <b>What masonry differs by, and each of the four is a fact about brick rather
/// than a taste.</b> The dust is lime and there is more of it, because a wall is
/// made of the stuff that ends up in the air. The fire is a third of the armour
/// burst's, because the round's filling is the whole of the fuel and a plate at
/// least has paint on it. The mass runs <em>along</em> the face, because a wall is
/// a plane and it channels what bursts against it. And <b>the fragments are drawn
/// at zero gain, because <see cref="WallRig"/> throws real brick bodies</b> - a
/// drawn shard beside a simulated one is two accounts of the same debris, and the
/// simulated one is the one that knocks the wall down.
/// </summary>
public sealed partial class ProcSlam : Node3D
{
    // --- the shape of it, in tile widths ------------------------------------

    /// <summary>
    /// How far the burst gets off the plate.
    ///
    /// <b>Shorter than the ricochet's reach and much wider than it, and that pair
    /// is the whole silhouette.</b> Spall is fragments leaving at a kilometre a
    /// second, and its reach is what makes it read as thrown; this is a volume of
    /// gas expanding into air that stops it at once, so it goes three tenths of a
    /// cell in every direction rather than four tenths in one. The eye reads the
    /// two apart on shape before it reads either on colour.
    /// </summary>
    public float Reach = ReachDefault;

    /// <summary>The two numbers something else has to be able to ask for -
    /// <see cref="ProcBlast.ReachDefault"/>'s arrangement and its reason: the
    /// frame's uniforms are declared at zero on purpose, so the shader text is not
    /// where a panel can read an opening value.</summary>
    public const float ReachDefault = 0.30f;
    public const float LifeDefault = 1.50f;

    /// <summary>
    /// Half the quad, across, and how far up it goes.
    ///
    /// <b>Symmetric across the seat although the burst is one-sided</b>, for
    /// <see cref="ProcSpall.Flank"/>'s reason entire: which way the normal points
    /// flips with the plate and with the heading, and a quad cut to it would have
    /// to be mirrored per hit - which mirrors the sun with it.
    ///
    /// <b>It holds what the model can produce, not what it happens to draw</b> -
    /// the burst's rule, and <see cref="Bounds"/> is the model asked directly.
    /// Most of what is up there is the impact point itself: a plate is on the side
    /// of a hull, so the burst starts most of a tank's height above the contact
    /// point the quad stands on.
    /// </summary>
    public float Flank = 1.05f;
    public float Tall = 1.25f;

    /// <summary>How far above the contact point the effect is seated, and the band
    /// it fades out over below that - <see cref="ProcBlast.Root"/>'s pair and its
    /// reason, at the ricochet's values because the ground is the ground.
    /// </summary>
    public float Root = 0.02f;
    public float Fade = 0.050f;

    /// <summary>
    /// The whole event, in seconds.
    ///
    /// <b>Half again the ricochet's and a third of the burst's, and being in
    /// between is what it says.</b> A bounce is over - a flash, a fan, a small
    /// grey puff, 0.95s. A shell in the ground throws earth that has to come back
    /// down and a column that has to settle, which is 2.40s. This has no ground
    /// under it to feed a column and nothing ballistic to wait for: the mass
    /// leaves the plate, thins, and is gone. What sets the number is the dust,
    /// exactly as in the other two.
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
    private ShaderMaterial? _dustInk;
    private ShaderMaterial? _fireInk;
    private Vector3 _nudge;

    /// <summary>
    /// The furthest the model can put anything, in tile widths: how far out along
    /// the plate's normal, and how far up.
    ///
    /// <b>Asked of the model rather than of the pixels</b>, and swept over the
    /// normal's own bearing a degree at a time - <see cref="ProcSpall.Bounds"/>'s
    /// arrangement and its finding: the projection takes the reach away and gives
    /// it to the spread (see <c>cone_open</c>), so the two things a bearing decides
    /// pull against each other and the worst case is somewhere in the middle of the
    /// sweep rather than at either end of it.
    ///
    /// <paramref name="plate"/> is where the round hit, over the seat, because that
    /// is where every element starts - and on this effect, as on the ricochet, it
    /// is the larger of the two terms going up.
    /// </summary>
    public static (float Along, float Up) Bounds(float reach, Vector2 plate)
    {
        float open = Uniform("cone_open");
        float floor = Uniform("squat_floor");
        float coreR = Uniform("core_size") * 1.50f;
        float ballR = Uniform("ball_size") * 1.50f * Uniform("ball_stretch");
        float sprayR = Uniform("spray_size") * 1.70f * Uniform("spray_long") * 1.65f;
        float cloudR = Uniform("cloud_size") * 1.50f * 1.35f;
        float spillR = Uniform("spill_size") * 1.55f * Uniform("spill_long");
        float washR = Uniform("wash_size") * 1.50f * Uniform("wash_long");
        float along = 0.0f, up = 0.0f;

        void Reach(Vector2 at, float pad)
        {
            along = MathF.Max(along, MathF.Abs(at.X) + pad);
            up = MathF.Max(up, MathF.Abs(at.Y) + pad);
        }

        for (int deg = 0; deg <= 90; deg++)
        {
            double th = deg * Math.PI / 180.0;
            // AtlasSet.GroundDirection flipped into the quad's frame and clamped at
            // the horizon - Aim's two lines, and the only copy of them.
            var flat = new Vector2((float)Math.Cos(th),
                                   (float)(Math.Sin(th) * Squashed));
            float squat = flat.Length();
            Vector2 outward = squat < 1e-6f ? Vector2.Right : flat / squat;
            var side = new Vector2(-outward.Y, outward.X);
            float run = reach * Mathf.Lerp(floor, 1.0f, squat);
            float wider = 1.0f + (1.0f - squat) * open;

            // The core, which barely moves: out of the plate by a fraction of its
            // own size.
            Reach(outward * (Uniform("core_size") * Uniform("core_lead")), coreR);
            // The fireball, at the far edge of its cone and at the top of its
            // climb. The climb is added rather than maximised against, because hot
            // gas rises while it is still going out.
            float ball = run * Uniform("ball_lead") * 1.30f;
            float ballCone = Uniform("ball_spread") * wider;
            Reach(outward * ball + side * (ball * ballCone)
                  + new Vector2(0.0f, ball * ballCone + run * Uniform("ball_climb")),
                  ballR);
            // The fragments, at the top of their speed spread and the far edge of
            // their cone. The drop is not taken off: it is worst late and the cone
            // is worst early.
            float fan = run * 1.25f;
            float fanCone = Uniform("spray_cone") * wider;
            Reach(outward * fan + side * (fan * fanCone)
                  + new Vector2(0.0f, fan * (Uniform("spray_rake")
                                             + fanCone * Uniform("spray_lift"))),
                  sprayR);
            // The mass of soot and dust, which is the widest thing here and the one
            // the quad is usually cut to.
            float cloud = run * Uniform("cloud_lead") * 1.30f;
            float cloudCone = Uniform("cloud_cone") * wider;
            Reach(outward * cloud + side * (cloud * cloudCone)
                  + new Vector2(0.0f, cloud * cloudCone
                                      + run * Uniform("cloud_climb")), cloudR);
            // The shock scrubbing the plate: across it, and it does not leave.
            Reach(side * (run * Uniform("wash_grow")), washR);
            // <b>And the spill, whose height is the ground's rather than the
            // plate's.</b> It runs down the hull, so it is measured from the seat
            // and the plate is taken back off it below - the ricochet's grains'
            // arrangement, and the one term in either model that goes downward.
            float creep = run * Uniform("spill_creep");
            Reach(outward * creep
                  + new Vector2(0.0f, Uniform("spill_seat") - plate.Y), spillR);
        }

        // Everything above is an offset from the point of impact, and the quad is
        // measured from the seat.
        return (along + MathF.Abs(plate.X), up + MathF.Abs(plate.Y));
    }

    /// <summary>How much of a ground length the camera leaves standing up the
    /// screen: <c>sin</c> of the board's own tilt, and the declared convention
    /// rather than a reading - <see cref="ProcSpall.Squashed"/>, whose note this is
    /// and whose value this has to be.</summary>
    public const float Squashed = ProcSpall.Squashed;

    /// <summary>One of the two shaders' own numbers, by name - so
    /// <see cref="Bounds"/> asks the shaders what they say rather than carrying a
    /// second copy of it. The dust first, because the mass is what the quad is cut
    /// to and most of these are its; the fire's names fall through to it.</summary>
    private static float Uniform(string name) =>
        ProcBlast.Uniform(DustSlamCode, name,
                          ProcBlast.Uniform(FireCode, name, 0.0f));

    /// <summary>The quad, in screen px: from <c>-Foot</c> across <c>Size</c>, which
    /// is what <see cref="Stage3D.Stem"/> takes. Its bottom edge is the contact
    /// point exactly, so <c>foot_v</c> is 1 - <see cref="ProcBlast.Quad"/> entire,
    /// and a static function of the tile for its reason: how big a burst on armour
    /// is can be asserted with no board under it.</summary>
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

        _dustInk = Ink(Sooting, dust: true);
        _fireInk = Ink(Bursting, dust: false);
        _dust = Slab(shape, _dustInk);
        _fire = Slab(shape, _fireInk);

        // Toward the camera by the board's own clearance, the fire by twice it -
        // the burst's arrangement, so the dark half is behind the additive one
        // rather than sorting against it by the coin toss two coplanar quads get.
        _nudge = Stage3D.Clear(squash, rise);
        Stand();
        // The surface last, because it writes to the materials the two lines
        // above have only just made - and because a pool rebuilt under a bench
        // that had set one must not quietly come back as armour.
        Wear();
    }

    /// <summary>How big this one is, as a multiple of the tuned burst -
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
    /// cell under it, and which side of that tank's own surface it is on -
    /// <see cref="ProcSpall.Sit"/> word for word, including that the seat is the
    /// victim's contact point and not the point of impact, and including why the
    /// depth side cannot be worked out from a quad standing on a hull's own foot.
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
        if (_fire is not null)
            _fire.Position = _nudge * 2.0f * _side / _might;
    }

    /// <summary>Which side of the tank's own surface this one is on, as
    /// <see cref="Sit"/> was told - for the check, which cannot read a node's depth
    /// off a picture.</summary>
    public bool Behind => _side < 0.0f;

    /// <summary>
    /// What the round burst against: armour, or masonry.
    ///
    /// <b>A number written to both materials rather than a second effect</b>, for
    /// the reason in the class note: the two pictures are the same six families
    /// with four differences, and each of the four is a <c>mix</c> the shader
    /// takes against this. Settable at any time and applied at once, so
    /// <see cref="Stage3D.Slam"/> can name the surface at the moment a round
    /// arrives - which is the only moment anybody knows it.
    ///
    /// Armour is the default because it is what the effect was tuned on, and
    /// because a burst whose surface nobody set should draw the picture that was
    /// judged by eye rather than the one derived from it.
    /// </summary>
    public Surface Face
    {
        get => _face;
        set
        {
            _face = value;
            Wear();
        }
    }

    /// <summary>
    /// The three: a burst on steel, a burst on brick, and a round that went
    /// <em>through</em> brick.
    ///
    /// <b>The third is here because leaving it out drew the wrong event</b>, and
    /// it was visible in one frame: an armour-piercing round punching a wall came
    /// out as the same lime burst with the same orange flare, and what an AP round
    /// does to masonry is make a hole and a cloud of dust. It has no filling, so
    /// it has no fire at all, and it puts less of the wall in the air because it
    /// spends its energy going through rather than pushing outward.
    ///
    /// <b>It is not <c>pierce</c> from docs/blast.md's list, and it must not be
    /// read as it.</b> That one is a hole with light coming out of it and a wisp
    /// afterwards, and none of that is drawn here - this is the masonry burst with
    /// the filling taken out, which is a stand-in and is named as one. Item 2 of
    /// that list is still unwritten.
    /// </summary>
    public enum Surface { Armour, Masonry, Pierced }

    private Surface _face = Surface.Armour;

    /// <summary>The surface written to both halves - the two numbers C# owns of
    /// the three sets, and the reason none of the three is duplicated here. Two
    /// rather than one because the second says which <em>kind</em> of masonry
    /// event it is, and the pair is what lets three pictures be one text.
    /// </summary>
    private void Wear()
    {
        float brick = _face is Surface.Masonry or Surface.Pierced ? 1.0f : 0.0f;
        float through = _face == Surface.Pierced ? 1.0f : 0.0f;
        foreach (ShaderMaterial? ink in new[] { _dustInk, _fireInk })
        {
            ink?.SetShaderParameter("masonry", brick);
            ink?.SetShaderParameter("pierce", through);
        }
    }

    /// <summary>
    /// Which way the plate faces, and where on the tank the round burst.
    ///
    /// <paramref name="outward"/> is the plate's own outward normal as
    /// <see cref="AtlasSet.GroundDirection"/> hands it over: a screen vector whose
    /// direction is where the gases go and whose <em>length</em> is the share of a
    /// ground length that survived the projection. <b>Both halves are used</b>, for
    /// <see cref="ProcSpall.Aim"/>'s reason - and here the projection matters more
    /// than it does there, because a burst on a plate turned away from the camera
    /// is the common case rather than the awkward one: a shell arrives on the plate
    /// facing the shooter, and the shooter is as often up the board as down it.
    ///
    /// <b>Screen y points down and the quad's y points up</b>, so the vertical part
    /// is negated coming in - and then clamped at the horizon, which is
    /// <see cref="ProcSpall"/>'s named limitation and is this effect's too. What the
    /// clamp costs is smaller here: a fireball is round, and a round thing that
    /// climbs when it should come at the viewer is still a fireball, where a fan of
    /// streaks doing it is a tongue of flame.
    ///
    /// <paramref name="plate"/> is where the round hit, in screen px off the contact
    /// point - handed over whole rather than assembled here, for
    /// <see cref="ProcSpall.Aim"/>'s reason and out of <see cref="Vehicle.Blown"/>,
    /// which is where the measurement lives.
    /// </summary>
    public void Aim(Vector2 outward, Vector2 plate)
    {
        float squat = Mathf.Clamp(outward.Length(), 0.0f, 1.0f);
        var quad = new Vector2(outward.X, -outward.Y);
        if (quad.Y < 0.0f)
            quad.Y = 0.0f;
        Vector2 faced = quad.LengthSquared() < 1e-8f
            ? Vector2.Right : quad.Normalized();
        // Screen y down, quad y up - the same flip the direction takes, and for the
        // same reason.
        var hit = new Vector2(plate.X, -plate.Y) / Mathf.Max(_tile, 1.0f);
        foreach (ShaderMaterial? ink in new[] { _dustInk, _fireInk })
        {
            ink?.SetShaderParameter("away", faced);
            ink?.SetShaderParameter("squat", squat);
            ink?.SetShaderParameter("plate", hit);
        }
        Away = faced;
        Squat = squat;
        Plate = hit;
    }

    /// <summary>Which way the plate faces and how much of the throw survived the
    /// projection, as <see cref="Aim"/> worked them out - for a bench that has to
    /// show what one is doing, and for the check that the clamp happened.</summary>
    public Vector2 Away { get; private set; } = Vector2.Right;
    public float Squat { get; private set; } = 1.0f;

    /// <summary>Where the round burst, over the seat, in tile widths - what
    /// <see cref="Aim"/> made of the offset it was handed.</summary>
    public Vector2 Plate { get; private set; } = Vector2.Zero;

    /// <summary>The hex's own width in screen px, kept because <see cref="Aim"/> is
    /// handed the impact point in px and every length in the model is in tiles.
    /// </summary>
    private float _tile = 1.0f;

    /// <summary>Set it off. Restarts rather than refusing, for
    /// <see cref="ProcBlast.Fire"/>'s reason: the organ that fires it is a key on a
    /// bench.</summary>
    public void Fire() => _clock = 0.0f;

    /// <summary>Whether the clock stands still - <see cref="ProcBlast.Hold"/>, and
    /// the main way an event gets looked at.</summary>
    public bool Hold;

    /// <summary>Where the clock is, settable, so a held burst can be scrubbed to
    /// the moment being judged.</summary>
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
        if (_fire is not null)
            _fire.Visible = on;
        if (!on)
            return;

        _dustInk?.SetShaderParameter("time", _clock);
        _fireInk?.SetShaderParameter("time", _clock);
    }

    /// <summary>One number of one of the two shaders, live - read out of the
    /// shader's own text the first time it is asked for and never kept twice.
    /// <see cref="ProcSpall.Dial(ProcSpall.Part, string)"/>'s arrangement and its
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

    /// <summary>The same number, written - and to both materials when the frame is
    /// what owns it, because the frame is the text they are both built from.
    /// </summary>
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
            _fireInk?.SetShaderParameter(uniform, sent);
        }
    }

    /// <summary>Which of the two a number belongs to. <see cref="Part.Frame"/> is
    /// the text they share.</summary>
    public enum Part { Frame, Dust, Fire }

    private readonly System.Collections.Generic.Dictionary<string, float> _live = new();

    private static string Source(Part part) => part switch
    {
        Part.Dust => DustSlamCode,
        Part.Fire => FireCode,
        // Both frames, because both are text these two halves share: the burst's
        // says how big the quad is and where the seat sits in it, the ricochet's
        // says which way the surface faces and where it was hit.
        _ => ProcBlast.FrameCode + ProcSpall.SpallFrame,
    };

    private ShaderMaterial? Coat(Part part) => part switch
    {
        Part.Dust => _dustInk,
        Part.Fire => _fireInk,
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
        if (!dust)
            return ink;
        // <b>Nearer the burst's own helping of the shared banding than the
        // ricochet's, and that is the size of the mass talking.</b> The steps read
        // as shading on a body in transition and as slabs on one that saturates: a
        // ricochet is a handful of elements over a third of a cell and saturates
        // wherever any two overlap, so it takes half of what the shared text says;
        // this is sixteen elements making one volume, which is the thing the shared
        // 0.55 was tuned on.
        ink.SetShaderParameter("dust_band", 0.46f);
        ink.SetShaderParameter("dust_tear", 0.58f);
        // <b>And it hides more of the tank than either of them.</b> What comes off
        // a plate under a ricochet is grams of paint and has to show the armour
        // through it; a shell bursting on the same plate puts its own filling into
        // the air as well, and a mass you can read the hull's rivets through says
        // the round was a firework. Still short of the shared 0.72 by a hair,
        // because this stands against the hull rather than against the ground, and
        // the hull is what it is standing on.
        ink.SetShaderParameter("dust_ink", 0.70f);
        return ink;
    }

    /// <summary>
    /// The soot: the mass that comes off the plate, what runs down the hull, and
    /// the shock scrubbing the armour flat.
    ///
    /// <b>Dark and barely warm, and that is a statement about what it is rather
    /// than a tint.</b> The four pairs on this board now say four things: the
    /// burst's is earth thrown out of the ground, the kick's is propellant, the
    /// ricochet's is paint and road dirt off a plate, and this is the soot of a
    /// filling that has just burnt, mixed with the same paint. So it is the darkest
    /// of the four and the only one with any warmth in it.
    ///
    /// <b>The dark end is where it is because of the finding, not despite it.</b>
    /// <c>dust_seat</c> is 0.52 and a saturated mass takes <c>dust_dense</c> off
    /// it, so a dense element shades near 0.24 and nearly every pixel shows the
    /// <em>dark</em> stop - which means the dark stop is the brightness of the whole
    /// cloud. Taken to a real soot black the mass reads as a hole cut in the board,
    /// which is the burst's own measured failure at <c>dust_ink</c> 1.0 arrived at
    /// from the other direction. This is a third of the way from the ricochet's
    /// neutral grey to black, which is as far as it goes before it stops being
    /// smoke.
    ///
    /// <b>One pass that adds up dust and one that shades the result</b>, and
    /// <b>ordinary alpha, premultiplied inside and divided back out</b> - the
    /// burst's two findings, unchanged and for their own reasons.
    /// </summary>
    private const string DustSlamShader = @"
shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_never;

FLAME_NOISE

FLAME_INK

BLAST_FRAME

DUST_INK

SPALL_FRAME

// The cloud: the whole mass of soot, burnt paint and lifted dirt, and the half of
// this effect that carries it. Born at the impact and leaving the plate in a cone
// so wide it is very nearly a hemisphere - which is the point, and the one number
// that separates this silhouette from a ricochet's at a glance. A shell that
// stopped has no direction left except out of the metal.
uniform int cloud = 18;
uniform float cloud_life = 1.05;
uniform float cloud_stagger = 0.060;
// How far along the normal the mass is carried, as a share of the reach.
uniform float cloud_lead = 0.46;
// Half-slope of the cone it leaves in. Multiplied by the distance travelled rather
// than used as an offset - the kick's embers' finding: what leaves a point leaves
// in a cone, and at age zero every element is at the point of impact.
uniform float cloud_cone = 0.80;
// And how much it rises by the end, as a share of the reach. Hot gas goes up while
// it is still going out, so this is added to the cone rather than replacing it - a
// mass that only rises is a chimney and one that only spreads is a stain.
uniform float cloud_climb = 0.34;
uniform float cloud_size = 0.086;
uniform float cloud_ink = 1.00;

// The spill: what runs down the side of the hull and flattens out at the tracks.
//
// <b>The element that makes the burst belong to this tank rather than float in
// front of it.</b> Everything else here happens up on a plate and could be
// happening a cell away for all the picture says; this reaches the ground, and the
// ground is the one thing in the quad whose position is known exactly. It is also
// the only downward term in either impact effect - see ProcSpall's grains, which
// are the same idea arrived at from sparks.
uniform int spill = 5;
uniform float spill_life = 0.90;
uniform float spill_born = 0.10;
uniform float spill_stagger = 0.16;
// How much of the drop from the plate to the ground it takes by the end of its
// life, and how far out it creeps once it is down there.
uniform float spill_fall = 0.88;
uniform float spill_creep = 0.34;
// Where it settles above the ground line. Seated rather than on it, because
// blast_footing fades out over a band above the line and an element at zero is an
// element nobody sees - the ricochet's grains' number and its reason.
uniform float spill_seat = 0.030;
uniform float spill_size = 0.060;
uniform float spill_long = 1.70;
uniform float spill_ink = 0.55;

// The wash: the shock going along the face of the plate, flat against it and gone
// almost at once.
//
// <b>Cheap, and it is what says the plate took the round rather than that
// something went off nearby.</b> Stretched across the normal rather than along it,
// which on a billboard is the only way a surface can be drawn being hit: the mass
// in front of the plate says an explosion happened, and a smear lying on the plate
// says the explosion was against that.
uniform int wash = 3;
uniform float wash_life = 0.30;
uniform float wash_grow = 0.30;
uniform float wash_size = 0.030;
uniform float wash_long = 3.20;
uniform float wash_ink = 0.42;

// <b>Soot and burnt paint, dark and lit.</b> Warm, unlike the ricochet's pair,
// because what is burning is a filling and not the road.
uniform vec3 soot_dark = vec3(0.352, 0.338, 0.325);
uniform vec3 soot_lit = vec3(0.818, 0.806, 0.788);

// <b>What surface this burst is on: 0 armour, 1 masonry - see ProcSlam.Face.</b>
// Every masonry term below is a mix against this, so at zero the arithmetic is
// what it was before the second surface existed and the picture judged by eye is
// the picture that comes out.
uniform float masonry = 0.0;

// <b>Lime, and much paler than soot.</b> What comes off a plate is the soot of a
// filling that has just burnt; what comes off a wall is the wall - mortar dust and
// crushed brick, which is nearly white and only faintly warm. It is the pale end
// of the four pairs on this board where the soot is the dark end, and the two
// stand furthest apart of any two dusts here on purpose: a burst on brick and a
// burst on steel are told apart at a glance by colour before anything else.
uniform vec3 lime_dark = vec3(0.512, 0.494, 0.455);
uniform vec3 lime_lit = vec3(0.902, 0.884, 0.836);

// <b>How much of the mass's travel goes ALONG the face rather than out of it.</b>
// A wall is a plane and it channels what bursts against it - the dust runs off
// both ways down the courses instead of standing out from the impact in a
// hemisphere, and that is the one thing in this picture that is about the shape of
// the surface rather than about what it is made of. Armour has no such shape to
// give: a hull is a box of facets the size of the burst itself.
uniform float along_face = 0.55;

// And how opaque it is against the soot's own helping. <b>Under one, and the
// first pass had it at 1.30 on the argument that a wall is made of the stuff that
// ends up in the air - which is true of how much dust there is and wrong about
// what to spend it on.</b> 1.30 over dust_ink 0.70 is 0.91, and a mass that
// saturates reads as a hole cut in the board: the courtyard went white and the
// near wall with it. The extra dust a wall gives is spent on the spread instead -
// see along_face, which is where the difference belongs anyway, because it is the
// wall's shape that makes a burst on brick look unlike one on steel.
uniform float stone_dust = 0.92;

// <b>And whether the round went through rather than bursting: 0 or 1, and only
// meaningful with masonry set - see ProcSlam.Surface.Pierced.</b> A round that
// pierces puts less of the wall in the air, because it spends itself going through
// instead of pushing outward.
uniform float pierce = 0.0;
uniform float bore_dust = 0.62;

void fragment() {
    if (level <= 0.0) {
        ALBEDO = vec3(0.0);
        ALPHA = 0.0;
    } else {
        vec2 at = blast_at(UV);
        {
            vec2 side_dir = spall_across();
            float run = spall_run(reach);
            float cone = spall_cone(cloud_cone);

            float dens = 0.0;
            vec2 lean = vec2(0.0);
            float years = 0.0;

            // How far the mass leans off the normal and onto the face, and it
            // is nought on armour - see along_face.
            float lane_at = along_face * masonry;
            for (int k = 0; k < cloud; k++) {
                float fk = float(k);
                float born = cloud_stagger * ember_hash(vec2(fk, 71.0));
                float a = (time - born) / max(cloud_life, 1e-3);
                if (a > 0.0 && a < 1.0) {
                    float fast = 0.45 + 0.85 * ember_hash(vec2(fk, 72.0));
                    float out_at = cloud_lead * run * fast * pow(a, 0.42);
                    float wide = out_at * cone
                                 * (2.0 * ember_hash(vec2(fk, 73.0)) - 1.0);
                    float up = out_at * cone
                               * (1.45 * ember_hash(vec2(fk, 74.0)) - 0.45)
                               + cloud_climb * run * pow(a, 0.85)
                                 * (0.30 + 0.95 * ember_hash(vec2(fk, 75.0)));
                    // The lane this one takes: out of the surface on armour, and
                    // on masonry leaning down the courses one way or the other.
                    // Its own side per element rather than one side for the
                    // family, because a wall channels dust both ways.
                    float hand = ember_hash(vec2(fk, 77.0)) < 0.5 ? -1.0 : 1.0;
                    vec2 lane = normalize(mix(away, side_dir * hand, lane_at)
                                          + vec2(1e-5, 1e-5));
                    vec2 p = plate + lane * out_at + side_dir * wide
                             + vec2(0.0, up);
                    float r = cloud_size * (0.60 + 0.90 * ember_hash(vec2(fk, 76.0)))
                              * (0.62 + 0.65 * a);
                    float gain = cloud_ink * (1.0 - exp(-a / 0.020))
                                 * pow(max(1.0 - a, 0.0), 1.20);
                    dust_part(at, p, r, r * 1.20, vec2(0.0, 1.0), fk + 71.0,
                              gain, dust_soft, a, dens, lean, years);
                }
            }

            for (int k = 0; k < spill; k++) {
                float fk = float(k);
                float born = spill_born + spill_stagger * ember_hash(vec2(fk, 81.0));
                float a = (time - born) / max(spill_life, 1e-3);
                if (a > 0.0 && a < 1.0) {
                    // Down the hull towards the seat, which in this frame is the
                    // plate's own height taken back off it - and further out the
                    // lower it gets, because that is what a mass hitting the ground
                    // does.
                    float drop = (plate.y - spill_seat) * spill_fall
                                 * pow(a, 0.75)
                                 * (0.70 + 0.55 * ember_hash(vec2(fk, 82.0)));
                    float out_at = run * spill_creep * pow(a, 0.60)
                                   * (0.40 + 1.00 * ember_hash(vec2(fk, 83.0)));
                    float wide = out_at * 0.45
                                 * (2.0 * ember_hash(vec2(fk, 84.0)) - 1.0);
                    vec2 p = plate + away * out_at + side_dir * wide
                             + vec2(0.0, -drop);
                    float r = spill_size * (0.55 + 0.95 * ember_hash(vec2(fk, 85.0)))
                              * (0.45 + 1.05 * a);
                    float gain = spill_ink * (1.0 - exp(-a / 0.09))
                                 * pow(max(1.0 - a, 0.0), 1.35);
                    // Lying along the ground by the end, standing up the hull at
                    // the start: the flow turns as the element falls, which is what
                    // stops five ellipses reading as five ellipses.
                    vec2 flow = normalize(mix(vec2(0.0, 1.0), away,
                                              clamp(a * 1.30, 0.0, 1.0))
                                          + vec2(1e-5, 1e-5));
                    dust_part(at, p, r, r * spill_long, flow, fk + 81.0,
                              gain, dust_soft, a, dens, lean, years);
                }
            }

            for (int k = 0; k < wash; k++) {
                float fk = float(k);
                float a = time / max(wash_life, 1e-3);
                if (a > 0.0 && a < 1.0) {
                    float off = wash_grow * run * pow(a, 0.40)
                                * (2.0 * ember_hash(vec2(fk, 91.0)) - 1.0);
                    vec2 p = plate + side_dir * off + away * (wash_size * 0.35);
                    float r = wash_size * (0.70 + 0.70 * ember_hash(vec2(fk, 92.0)))
                              * (0.50 + 1.00 * a);
                    float gain = wash_ink * (1.0 - exp(-a / 0.03))
                                 * pow(max(1.0 - a, 0.0), 1.60);
                    dust_part(at, p, r, r * wash_long, side_dir, fk + 91.0,
                              gain, dust_soft, a, dens, lean, years);
                }
            }

            if (dens <= 1e-4) {
                ALBEDO = vec3(0.0);
                ALPHA = 0.0;
            } else {
                float mass = dust_mass(at, dens);
                float lit = dust_lit(mass, lean);
                ALBEDO = mix(mix(soot_dark, soot_lit, lit),
                             mix(lime_dark, lime_lit, lit), masonry);
                ALPHA = clamp(mass * dust_ink * mix(1.0, stone_dust, masonry)
                              * mix(1.0, bore_dust, pierce)
                              * level * blast_footing(at), 0.0, 1.0);
            }
        }
    }
}
";

    /// <summary>
    /// The light: the core of the detonation, the fireball rolling off the plate,
    /// and the fragments it throws.
    ///
    /// <b>Three windows, and the spread between them is the event.</b> The core is
    /// out in three frames, the ball in a quarter of a second, the fragments are
    /// falling at a third of one, and the soot is still standing when all three are
    /// gone. Held the same length they would be one flash; staggered like this they
    /// are a shell arriving and its consequences - the ricochet's argument, and it
    /// is the same argument because it is the same kind of picture.
    ///
    /// <b>The fireball climbs while it spreads, and it is the one thing here that
    /// is not the ricochet with different numbers.</b> Spall leaves along one line
    /// and air stops it; burning gas is lighter than the air it is in, so it goes
    /// out of the plate and then up, and a ball that only goes out reads as a splash
    /// of paint. What it must not do is go up <em>only</em>, which is a tank on fire
    /// - see <see cref="ProcFire"/>, whose picture that is.
    ///
    /// <b>Nothing here is a bolt, and the absence is load-bearing.</b> The
    /// ricochet's two long streaks are the round itself carrying on, and they are
    /// most of what makes it read as a bounce. An HE round does not carry on. So the
    /// fragments are shorter than spall's, blunter, a third as many, and they
    /// <em>fall</em> - <c>spray_drop</c> is nearly twice the ricochet's for that
    /// reason alone.
    ///
    /// <b>The white is bought with brightness and not with a whiter colour</b>, and
    /// the fragments cool to the ink's own ember colour - the two rules everything
    /// additive on this board is written under.
    /// </summary>
    private const string FireSlamShader = @"
shader_type spatial;
render_mode unshaded, cull_disabled, blend_add, depth_draw_never;

FLAME_NOISE

FLAME_INK

BLAST_FRAME

SPALL_FRAME

// The core: the filling going off against the metal. One body, three frames, the
// brightest thing on the board while it is there, and seated on the plate itself -
// it has nowhere to go yet.
//
// <b>Twice the size it was first tuned at, and the argument is the shell rather
// than the picture: an HE round has a filling.</b> The first pass kept the fire
// deliberately tight so it would read as the heart of the mass rather than as an
// orange stain inside it - which it does - but tight enough to read as a heart it
// also read as an impact spark, and what is going off here is explosive. Doubled
// together with the ball below, because the two are one event: a large white flash
// in front of a small orange ball is two things happening.
uniform float core_life = 0.055;
uniform float core_size = 0.100;
// How far off the plate it sits, in its own widths. Small and not zero: the
// detonation is in front of the armour rather than inside it, and at zero the core
// reads as a lamp behind the hull.
uniform float core_lead = 0.55;
// Past one, and deliberately - ProcBlast's flash's note: a white heart is bought
// by adding an orange body to itself until it clips, which is brightness rather
// than a whiter colour.
//
// <b>And it had to go up when the core grew, which is that same note read
// backwards.</b> Doubling the radius at 1.85 spread the same light over four times
// the area and the fire came out a flat orange disc - bigger, and no longer a
// detonation. The lever is the gain, exactly as the note says; a paler colour here
// would have bought the white by giving up the fire.
uniform float core_gain = 2.45;

// The ball: the fireball rolling off the face of the plate.
uniform int balls = 6;
uniform float ball_life = 0.26;
uniform float ball_stagger = 0.035;
// Doubled with the core, and for its reason - see the note there.
uniform float ball_size = 0.088;
uniform float ball_lead = 0.30;
uniform float ball_spread = 0.55;
uniform float ball_climb = 0.30;
uniform float ball_gain = 1.00;
uniform float ball_stretch = 1.30;

// The fragments: pieces of the casing and of the plate's face. See the class note
// on why there is no bolt among them.
uniform int spray = 20;
uniform float spray_life = 0.30;
uniform float spray_stagger = 0.030;
uniform float spray_drag = 0.45;
uniform float spray_cone = 0.75;
uniform float spray_lift = 0.60;
uniform float spray_rake = 0.22;
// Nearly twice the ricochet's, and quadratic in the age because that is what
// falling is. A fragment of a burst shell has nowhere to be going.
uniform float spray_drop = 0.55;
uniform float spray_size = 0.0052;
uniform float spray_long = 3.40;
uniform float spray_gain = 0.70;
// The hot end of the walk a cooling fragment takes - nearly white, because
// incandescent steel is. The cold end is the ink's own ember colour, shared with
// everything else on this board that throws sparks.
uniform vec3 spray_hot = vec3(1.000, 0.900, 0.680);
uniform float spray_cool = 0.70;

// <b>What surface this burst is on: 0 armour, 1 masonry - ProcSlam.Face, and the
// dust half declares the same number for the same reason.</b>
uniform float masonry = 0.0;

// <b>How much of the FIREBALL a burst on brick keeps</b>, and the word matters:
// against armour the round's filling burns and the plate's paint and primer burn
// with it, while a wall is lime and fired clay and has nothing to add. So the gas
// that rolls off a wall is less of it than rolls off a hull.
//
// <b>What this deliberately no longer touches is the core, and that was a real
// defect rather than a taste.</b> It scaled both, so a shell that demolishes a
// wall detonated more dimly than the same shell against a plate - and the
// detonation is the filling going off, which is the same filling whatever it meets.
// Reported as the flash being too small for a round that breaks masonry, and
// correctly: the surface may take the fireball away and it has no business taking
// the explosion away with it.
uniform float stone_fire = 0.55;

// <b>And no fragments whatever, which is the one place a number is zero because
// something else already draws the thing.</b> WallRig.Burst pushes real brick
// bodies out of the courses; a drawn shard beside a simulated one is two accounts
// of the same debris, and the simulated one is the account that knocks the wall
// down. Kept as a gain rather than a count because a count cannot be a uniform
// this loop reads - the loop still runs and draws nothing, which is cheap and is
// named here rather than discovered.
uniform float stone_spray = 0.0;

// <b>Whether the round went through rather than bursting - the dust half declares
// the same number, see ProcSlam.Surface.Pierced.</b> There is no fire in this case
// at all, and that is not a small number but a nought: an armour-piercing round
// has no filling, so the only light a wall gives it is none.
uniform float pierce = 0.0;

void fragment() {
    if (level <= 0.0) {
        ALBEDO = vec3(0.0);
        ALPHA = 0.0;
    } else {
        vec2 at = blast_at(UV);
        {
            vec2 side_dir = spall_across();
            float run = spall_run(reach);
            // Alpha-over inside the layer, additive on the way out - surfaces stack
            // the way surfaces stack, and the layer as a whole adds light.
            vec4 lit = vec4(0.0);

            // How much of the FIREBALL this surface feeds - see stone_fire. The
            // core is not in it: that is the shell's own filling and it is the
            // same shell on any surface.
            float fuel = mix(1.0, stone_fire, masonry) * (1.0 - pierce);
            // What the round itself brings, which only a round with no filling at
            // all is without - see pierce.
            float charge = 1.0 - pierce;
            float ca = time / max(core_life, 1e-4);
            if (ca < 1.0) {
                float r = core_size * (0.45 + 0.80 * sqrt(ca));
                float gain = core_gain * charge * pow(max(1.0 - ca, 0.0), 1.30);
                lit = flame_over(lit,
                                 flame_blob(at, plate + away * (core_size * core_lead),
                                            r, r * 0.85, away, 2.0, 0.0, gain));
            }

            float ballCone = spall_cone(ball_spread);
            for (int k = 0; k < balls; k++) {
                float fk = float(k);
                float born = ball_stagger * ember_hash(vec2(fk, 101.0));
                float a = (time - born) / max(ball_life, 1e-3);
                if (a > 0.0 && a < 1.0) {
                    float fast = 0.50 + 0.80 * ember_hash(vec2(fk, 102.0));
                    float out_at = ball_lead * run * fast * pow(a, 0.55);
                    float wide = out_at * ballCone
                                 * (2.0 * ember_hash(vec2(fk, 103.0)) - 1.0);
                    float up = out_at * ballCone
                               * (2.0 * ember_hash(vec2(fk, 104.0)) - 1.0)
                               + ball_climb * run * pow(a, 1.15);
                    vec2 p = plate + away * out_at + side_dir * wide
                             + vec2(0.0, up);
                    float r = ball_size * (0.55 + 0.90 * ember_hash(vec2(fk, 105.0)))
                              * (0.50 + 0.85 * a);
                    lit = flame_over(lit,
                                     flame_blob(at, p, r, r * ball_stretch,
                                                vec2(0.0, 1.0), fk + 101.0,
                                                // Each body its own place on the
                                                // ramp as well as its own age -
                                                // ProcBlast's ball's finding: read
                                                // at one stop the whole thing
                                                // composites into a flat orange
                                                // disc.
                                                clamp(a + 0.40 * ember_hash(vec2(fk, 106.0)),
                                                      0.0, 1.0),
                                                ball_gain * fuel
                                                * flame_life(a, 0.80)));
                }
            }

            float fanCone = spall_cone(spray_cone);
            for (int k = 0; k < spray; k++) {
                float fk = float(k);
                float born = spray_stagger * ember_hash(vec2(fk, 111.0));
                float a = (time - born) / max(spray_life, 1e-3);
                if (a > 0.0 && a < 1.0) {
                    float fast = 0.30 + 0.95 * ember_hash(vec2(fk, 112.0));
                    float out_at = run * fast * pow(a, spray_drag);
                    float wide = out_at * fanCone
                                 * (2.0 * ember_hash(vec2(fk, 113.0)) - 1.0);
                    float up = out_at * (spray_rake
                               + fanCone * spray_lift
                                 * (2.0 * ember_hash(vec2(fk, 114.0)) - 1.0))
                               - run * spray_drop * a * a;
                    vec2 p = plate + away * out_at + side_dir * wide
                             + vec2(0.0, up);
                    // Along the line from where it burst to where it is, and round
                    // when it has stopped - SheetBlast's lances' finding.
                    vec2 trail = p - plate;
                    vec2 flow = length(trail) < 1e-4 ? away : normalize(trail);
                    // Width and length take their own hash each, which is what
                    // stops a fan reading as petals - ProcSpall's shards, and the
                    // finding is paid for there.
                    float r = spray_size * (0.50 + 1.20 * ember_hash(vec2(fk, 115.0)));
                    float draw = mix(spray_long
                                     * (0.45 + 1.15 * ember_hash(vec2(fk, 116.0))),
                                     1.0, clamp(a * 1.45, 0.0, 1.0));
                    float gain = spray_gain * mix(1.0, stone_spray, masonry)
                                 * (1.0 - exp(-a / 0.025))
                                 * pow(max(1.0 - a, 0.0), 1.35);
                    vec3 body = mix(spray_hot, ember_colour,
                                    smoothstep(0.0, max(spray_cool, 1e-3), a));
                    lit = flame_over(lit, flame_spark(at, p, r, r * draw, flow,
                                                      fk + 111.0, body, gain));
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

    /// <summary>The soot as it is compiled - the shared noise, the shared ink, the
    /// burst's frame, the burst's dust and the ricochet's impact frame, put together
    /// in one place so a check can ask whether the four effects on this board are
    /// one text.</summary>
    internal static readonly string DustSlamCode =
        DustSlamShader.Replace("FLAME_NOISE", Stage3D.EmberNoiseCode)
                      .Replace("FLAME_INK", Stage3D.FlameInk)
                      .Replace("BLAST_FRAME", ProcBlast.FrameCode)
                      .Replace("DUST_INK", ProcBlast.DustInk)
                      .Replace("SPALL_FRAME", ProcSpall.SpallFrame);

    internal static readonly string FireCode =
        FireSlamShader.Replace("FLAME_NOISE", Stage3D.EmberNoiseCode)
                      .Replace("FLAME_INK", Stage3D.FlameInk)
                      .Replace("BLAST_FRAME", ProcBlast.FrameCode)
                      .Replace("SPALL_FRAME", ProcSpall.SpallFrame);

    private static readonly Shader Sooting = new() { Code = DustSlamCode };
    private static readonly Shader Bursting = new() { Code = FireCode };
}
