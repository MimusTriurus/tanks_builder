using System;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The dust a gun blows off the ground when it fires: the pale mass that leaves
/// down the bore, the skirt of it pushed out along the ground, and the brown
/// veil that outlives both with embers still in it.
///
/// <b>The half of a shot this project did not have, and the reference is what
/// said so.</b> Four frames of a tank firing, in sequence: the muzzle flame is
/// compact - two or three calibres, and it is *inside* the cloud rather than in
/// front of it - while the thing that fills the frame is dust, displaced
/// downrange, sitting on the ground and nowhere near the muzzle. By the last
/// frame the flame is gone, the mass has gone brown, and what is left glowing in
/// it is embers. <see cref="ProcFlash"/> and <see cref="ProcFume"/> are both
/// welded to the bore and both live 34 frames; neither of them can be this, and
/// widening either to try would be the same mistake in two places.
///
/// <b>So it is split by anchor, which is how this board splits everything.</b>
/// The flash and the muzzle smoke stay in the tank's own canvas because they are
/// welded to the gun - they shear with it, they are ordered against the turret,
/// and they are already measured there. This is welded to the *ground*, so it
/// goes on the board through <see cref="Stage3D.Kick"/> exactly as a shell's
/// burst does. It is also simply too big for the other space: a staged sprite
/// lives in a 512px render target (<see cref="Stage3D.PaintSize"/>) and a cloud
/// of this size drawn there would be sliced off flat by the edge of it.
///
/// <b>Built out of the burst's own dust, not a second dust.</b>
/// <see cref="ProcBlast.DustInk"/> is one statement of what earth in the air is -
/// the profile, the tearing, the packing, the light on the finished mass - and
/// this is that statement with different elements in it. What is this effect's
/// own is exactly two things: where the elements go, and the colour, because a
/// muzzle's dust starts pale and a shell's never is.
///
/// <b>The colour walks, and that is read straight off the frames.</b> Frames two
/// and three are a white mass with no internal structure at all - clipped, not
/// textured - and frame four has gone warm grey while frame one is brown with
/// orange in it. So there are two pairs of stops rather than one, mixed by the
/// mass's own mean age: the accumulator <c>years</c> that <c>dust_part</c> has
/// always kept and that the burst never used.
///
/// <b>Quoted in tile widths, for <see cref="ProcBlast"/>'s reason.</b> A shot is
/// a thing that happens on a cell, so a cell is its unit, and a board at another
/// zoom moves the cloud with it. <see cref="Might"/> is then what a calibre is:
/// a 152 is this effect at 1.4 rather than a different one.
///
/// <b>Time is a uniform, not <c>TIME</c></b>, and the clock goes to -1 when the
/// last dust is gone - <see cref="ProcBlast"/>'s convention and its reasons, both
/// of them: two runs of the same flags have to come out the same picture, and a
/// layer whose clock is out draws nothing rather than resting on a first frame.
///
/// <b>One limitation, named rather than discovered later.</b> The cloud is drawn
/// on an upright billboard seated on the contact point, so - the rule that quad
/// is written under - below the contact line and behind the ground are the same
/// test, and anything reaching below it is sliced off flat. A gun fired *toward*
/// the camera throws its dust toward the camera, which on this board is
/// downward and forward: exactly the half that would be cut. So the downrange
/// direction is clamped to the horizon (<see cref="Aim"/>), and such a shot grows
/// its cloud at the seat instead of translating it - which is what a cloud coming
/// at the viewer does on a billboard anyway. The proper answer, if it reads badly,
/// is the one the burst's rings already use: a plane of its own lying on the
/// ground.
/// </summary>
public sealed partial class ProcKick : Node3D
{
    // --- the shape of it, in tile widths ------------------------------------

    /// <summary>
    /// How far down the bore the mass gets.
    ///
    /// <b>Under two thirds of a cell, and that is a decision about the board
    /// rather than about the reference.</b> On the frames the cloud is several
    /// tank lengths across, because the camera is standing off to one side at
    /// ground level with a long lens. Here the camera looks down at a hex grid
    /// where a cell is the unit of everything, and a cloud taken at reference
    /// scale covers the cell that was fired at along with the two beside it - so
    /// the shot would hide its own consequence. At this it reads as a shot and
    /// leaves the board legible; the elements' own radii carry the total to about
    /// a cell and a half across, which is where it was wanted.
    /// </summary>
    public float Reach = ReachDefault;

    /// <summary>The three numbers something else has to be able to ask for -
    /// <see cref="ProcBlast.ReachDefault"/>'s arrangement and its reason: the
    /// frame's uniforms are declared at zero on purpose, so the shader text is
    /// not where a panel can read an opening value.</summary>
    public const float ReachDefault = 0.62f;
    public const float LifeDefault = 1.60f;
    public const float BackDefault = 0.22f;

    /// <summary>
    /// Half the quad, across, and how far up it goes.
    ///
    /// <b>Symmetric across the seat although the cloud is not, and the waste is
    /// paid back in one dot product.</b> The mass goes one way - down the bore -
    /// and which way that is on screen flips with the heading, so a quad cut to
    /// the cloud would have to be rebuilt or mirrored per shot, and mirroring it
    /// flips the sun with it. Symmetric, the quad is 1.4x the area it needs; the
    /// fragment tests <c>dot(at, blow)</c> once and leaves before touching an
    /// element, which is cheaper than either alternative and cannot get the light
    /// wrong.
    ///
    /// <b>And it holds what the model can produce, not what it happens to
    /// draw</b> - the burst's rule, and this effect broke it on the first
    /// picture. At 1.06 by 0.58 the settled dust came out with a straight top
    /// edge and a straight right one, which is a cloud sliced off flat by its own
    /// quad; a cloud missing its outermost lumps looks like a smaller cloud
    /// rather than like a bug, which is why the check is on the model as well as
    /// on the pixels. See <see cref="Bounds"/>: the widest thing in here is the
    /// veil, which is born late, climbs longest and is the biggest element there
    /// is, and it reaches 0.55 up against a quad that was 0.58 tall.
    ///
    /// <b>Both halves have to hold the snout, and the up one is the surprise.</b>
    /// The cloud starts at the muzzle, which is not merely ahead of the seat but
    /// *above* it: measured over the three loaded tanks and all 24 headings, the
    /// worst muzzle stands 0.384 of a tile up the screen from its own contact
    /// point - a gun fired away from the camera - and the cloud then climbs from
    /// there. So the model reaches 1.14 out and 1.03 up, and the quad is those
    /// plus a margin. Every one of those numbers came off the check rather than
    /// off a screenshot.
    ///
    /// <b>The area is 4.6 times the burst's, and that is the price of the
    /// symmetry.</b> Half of it is behind the gun and leaves in the bail; a quad
    /// cut to the cloud would need mirroring, and mirroring <c>at.x</c> without
    /// mirroring the sun means unpicking the shared light. Named because it is a
    /// real cost, not because it is a problem to be fixed today.
    /// </summary>
    public float Flank = 1.45f;
    public float Tall = 1.10f;

    /// <summary>How far behind the seat anything reaches, in tile widths: the
    /// skirt goes back under the hull a little, because a muzzle blast pushes
    /// dust out in every direction and only mostly forward. This is what the
    /// fragment's early bail is measured against.</summary>
    public float Back = BackDefault;

    /// <summary>How far above the contact point the cloud is seated, and the
    /// band it fades out over below that - <see cref="ProcBlast.Root"/>'s pair
    /// and its reason. Smaller than the burst's, because this sits *on* the
    /// ground rather than a little above it.</summary>
    public float Root = 0.02f;
    public float Fade = 0.050f;

    /// <summary>
    /// The whole event, in seconds.
    ///
    /// <b>The veil sets it, and the ratio is what the four frames are.</b> The
    /// glow is out in a sixth of a second, the pale mass in a second, and the
    /// brown veil is still there at one and a half - so the light is a tenth of
    /// the event and the rest is what it left standing. That spread is the whole
    /// difference between a shot and a flashbulb, and it is three times the 0.57s
    /// the flash and the muzzle smoke run for. They are not this event's clock and
    /// were never meant to be.
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
    /// the bore, and how far up.
    ///
    /// <b>Asked of the model rather than of the pixels, and both are needed.</b>
    /// A quad cut to a screenshot's bounding box passes every screenshot and
    /// clips the day somebody lengthens an element's life or softens its fade -
    /// the burst's finding, and this effect's first picture was the same failure
    /// arrived at without the check. Each family's furthest is where it gets at
    /// the age it gets there plus its own radius at that age; the radii grow with
    /// age, so the two peaks are not at the same moment and the maximum is taken
    /// over the family rather than at the end of it.
    ///
    /// <paramref name="snout"/> is how far the muzzle is from the seat, since
    /// that is where every element starts.
    /// </summary>
    public static (float Along, float Up) Bounds(float reach, Vector2 snout)
    {
        float run = reach;
        // The wash: furthest lead, furthest radius, at a = 1.
        float wash = Uniform("wash_born") + run
                     + Uniform("wash_size") * 1.45f * 1.62f;
        float veil = run * Uniform("veil_creep") * 1.25f
                     + Uniform("veil_size") * 1.50f * 1.55f;
        float skirt = run * Uniform("skirt_lead")
                      + Uniform("skirt_spread")
                      + Uniform("skirt_size") * 1.45f * 1.55f * Uniform("skirt_flat");
        float along = MathF.Max(MathF.Max(wash, veil), skirt)
                      + MathF.Abs(snout.X);
        float up = MathF.Max(
            Uniform("wash_climb") * 1.25f + Uniform("wash_size") * 1.45f * 1.62f,
            Uniform("veil_climb") * 1.25f + Uniform("veil_size") * 1.50f * 1.55f)
            + MathF.Abs(snout.Y);
        return (along, up);
    }

    /// <summary>One of the dust shader's own numbers, by name - so
    /// <see cref="Bounds"/> asks the shader what it says rather than carrying a
    /// second copy of it.</summary>
    private static float Uniform(string name) =>
        ProcBlast.Uniform(DustKickCode, name, 0.0f);

    /// <summary>The quad, in screen px: from <c>-Foot</c> across <c>Size</c>,
    /// which is what <see cref="Stage3D.Stem"/> takes. Its bottom edge is the
    /// contact point exactly, so <c>foot_v</c> is 1. A static function of the
    /// tile rather than read off a built node, so how big a kick is can be
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

        _dustInk = Ink(Kicking);
        _glowInk = Ink(Glowing);
        _dust = Slab(shape, _dustInk);
        _glow = Slab(shape, _glowInk);

        // Toward the camera by the board's own clearance, the glow by twice it -
        // the burst's arrangement and the burning tree's before it, so the dark
        // half is behind the additive one rather than sorting against it by the
        // coin toss two coplanar quads get.
        _nudge = Stage3D.Clear(squash, rise);
        _dust.Position = _nudge;
        _glow.Position = _nudge * 2.0f;
    }

    /// <summary>How big this one is, as a multiple of the tuned kick -
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

    /// <summary>Where it stands: the flat point the tank is touching and the lift
    /// of the cell under it, through the same transform a tree gets.
    ///
    /// <b>The tank's contact point, not the muzzle.</b> The muzzle is 60-70px out
    /// along a gun and up in the air, and its height is the one thing this quad
    /// cannot use - the quad's bottom edge *is* the ground. Where the dust ends up
    /// is the model's business (<see cref="Reach"/> down the bore), and a seat on
    /// the ground is a seat whose lift is known exactly. Same choice
    /// <c>Grove.Shock</c> makes off the same trigger, for a related reason.
    /// </summary>
    public void Sit(Vector2 ground, float lift, float squash, float rise)
    {
        _seat = Stage3D.Trunk(ground, lift, 0.0f, squash, rise);
        Stand();
    }

    /// <summary>The seat and the size together, the quads' own nudges divided
    /// back out - the burst's <c>Stand</c>'s reason: a clearance is a
    /// clearance whatever size the cloud is.</summary>
    private void Stand()
    {
        Transform = _seat.ScaledLocal(Vector3.One * _might);
        if (_dust is not null)
            _dust.Position = _nudge / _might;
        if (_glow is not null)
            _glow.Position = _nudge * 2.0f / _might;
    }

    /// <summary>
    /// Which way the gun is pointing, as <see cref="AtlasSet.GroundDirection"/>
    /// hands it over: a screen vector whose direction is downrange and whose
    /// <em>length</em> is the share of a ground length that survived the
    /// projection.
    ///
    /// <b>Both halves are used, and they mean different things.</b> The direction
    /// says which way the dust goes; the length says how far it appears to,
    /// because a shot into the screen throws its dust the same distance in the
    /// world and a shorter one across the picture. Same number the camera shake
    /// already takes off the same trigger, and for the same reason.
    ///
    /// <b>Screen y points down and the quad's y points up</b>, so the vertical
    /// part is negated coming in - and then clamped at the horizon, which is the
    /// limitation in the class note: a shot toward the camera would put its dust
    /// under the contact line, where there is no drawing at all.
    ///
    /// <b><paramref name="snout"/> is where the dust starts, and leaving it out
    /// was a real error rather than a rough edge.</b> The quad is seated on the
    /// tank's contact point, because that is the one point on the board whose
    /// lift is known exactly - see <see cref="Sit"/> - but the contact point is
    /// under the middle of the hull, and a cloud placed from there begins *inside
    /// the tank*: the first shot came out as an orange mass lying on the engine
    /// deck. The muzzle is half a hull further along and the caller measures it
    /// per heading anyway (<see cref="Vehicle.Bore"/>), so it is handed over
    /// rather than guessed at. In screen px off the contact point, which the tile
    /// turns into the quad's own units.
    /// </summary>
    public void Aim(Vector2 along, Vector2 snout)
    {
        float squat = Mathf.Clamp(along.Length(), 0.0f, 1.0f);
        var quad = new Vector2(along.X, -along.Y);
        if (quad.Y < 0.0f)
            quad.Y = 0.0f;
        Vector2 blow = quad.LengthSquared() < 1e-8f
            ? Vector2.Right : quad.Normalized();
        // Screen y down, quad y up - the same flip the direction takes, and for
        // the same reason.
        var muzzle = new Vector2(snout.X, -snout.Y) / Mathf.Max(_tile, 1.0f);
        foreach (ShaderMaterial? ink in new[] { _dustInk, _glowInk })
        {
            ink?.SetShaderParameter("blow", blow);
            ink?.SetShaderParameter("squat", squat);
            ink?.SetShaderParameter("snout", muzzle);
        }
        Blow = blow;
        Squat = squat;
        Snout = muzzle;
    }

    /// <summary>Which way the dust went and how much of the throw survived the
    /// projection, as <see cref="Aim"/> worked them out - for a bench that has to
    /// show what one is doing, and for the check that the clamp happened.
    /// </summary>
    public Vector2 Blow { get; private set; } = Vector2.Right;
    public float Squat { get; private set; } = 1.0f;

    /// <summary>Where the muzzle sits over the seat, in tile widths - what
    /// <see cref="Aim"/> made of the offset it was handed.</summary>
    public Vector2 Snout { get; private set; } = Vector2.Zero;

    /// <summary>The hex's own width in screen px, kept because <see cref="Aim"/>
    /// is handed the muzzle in px and every length in the model is in tiles.
    /// </summary>
    private float _tile = 1.0f;

    /// <summary>Set it off. Restarts rather than refusing, for
    /// <see cref="ProcBlast.Fire"/>'s reason: the organ that fires it is a key on
    /// a bench.</summary>
    public void Fire() => _clock = 0.0f;

    /// <summary>Whether the clock stands still - <see cref="ProcBlast.Hold"/>,
    /// and the main way an event gets looked at.</summary>
    public bool Hold;

    /// <summary>Where the clock is, settable, so a held cloud can be scrubbed to
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
        if (_glow is not null)
            _glow.Visible = on;
        if (!on)
            return;

        _dustInk?.SetShaderParameter("time", _clock);
        _glowInk?.SetShaderParameter("time", _clock);
    }

    /// <summary>One number of one of the two shaders, live - read out of the
    /// shader's own text the first time it is asked for and never kept twice.
    /// <see cref="ProcBlast.Dial(ProcBlast.Part, string)"/>'s arrangement and its
    /// reason: the uniforms are where the numbers live, so a panel carrying its
    /// own copy of a default agrees until the first edit lands in one of
    /// them.</summary>
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
        Part.Dust => DustKickCode,
        Part.Glow => GlowCode,
        // Both frames, because both are the text the two halves share: the
        // burst's says how big the quad is and where the seat sits in it, this
        // effect's says which way the gun pointed.
        _ => ProcBlast.FrameCode + KickFrame,
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
        ink.SetShaderParameter("blow", Vector2.Right);
        ink.SetShaderParameter("squat", 1.0f);
        ink.SetShaderParameter("back", Back);
        ink.SetShaderParameter("snout", Vector2.Zero);
        // <b>Half the burst's helping of the shared banding, and the reason is
        // the same one that made it half in the first place.</b> The steps read
        // as shading on a mass in transition and as *slabs* on one that
        // saturates; the burst is a dense cone a cell wide and lands on the
        // first, but this is a thin sheet spread over two cells, so a band there
        // covers a large flat region and the line between two of them is back to
        // being a hard edge. Measured on the picture: at 0.55 the settled dust
        // came out polygonal plates with the hex seam showing through them.
        //
        // Written here rather than changed in the shared text, because the shared
        // number is right for the effect it was tuned on.
        ink.SetShaderParameter("dust_band", 0.28f);
        // <b>Torn harder than the burst, for the same reason the banding is taken
        // less.</b> Fifty-odd elements spread over two cells overlap into
        // saturation almost everywhere, and a saturated field with a soft edge is
        // a smear - which is what the settled dust came out as, in those words.
        // The burst's cone is deep and narrow and gets its structure from the
        // elements' own arrangement; a sheet has to get it from the field.
        ink.SetShaderParameter("dust_tear", 0.55f);
        return ink;
    }

    /// <summary>What both halves are told about which way the gun pointed, and
    /// the one test that lets a symmetric quad carry an asymmetric cloud. Shared
    /// text rather than a copy per shader, for the burst's frame's reason at a
    /// smaller scale still.</summary>
    internal const string KickFrame = @"
// Downrange, as a unit vector in the quad's own frame: x across the screen, y up
// it. Written by ProcKick.Aim every frame a kick is dressed - declared pointing
// right so a run that forgot to write it draws a sideways cloud rather than
// nothing, which is the opposite of the burst's frame and deliberate: this one
// cannot be zero and still mean anything.
uniform vec2 blow = vec2(1.0, 0.0);
// How much of a ground length survived the projection, in 0..1. A shot into the
// screen covers the same distance in the world and less of the picture.
uniform float squat = 1.0;
// How far behind the seat anything reaches, in tile widths - what the early bail
// is measured against. See ProcKick.Back.
uniform float back = 0.22;
// Where the muzzle sits over the seat, in tile widths - see ProcKick.Aim. The
// seat is the tank's contact point because that is where the ground is known;
// the dust starts at the gun.
uniform vec2 snout = vec2(0.0);

// Across the bore, in the quad's own frame. The lateral scatter of the elements
// goes along this, which is an approximation and a knowing one: on the ground
// lateral is a direction in depth as well as across, and a billboard has no
// depth. It is the skirt's flat elements that carry the ground-hugging read, not
// this.
vec2 kick_across() {
    return vec2(-blow.y, blow.x);
}

// <b>Everything is on one side, so the other side leaves in one dot product.</b>
// The quad is symmetric because which way downrange is flips with the heading -
// see ProcKick.Flank - and this is what pays for that.
bool kick_behind(vec2 at) {
    return dot(at - snout, blow) < -back;
}
";

    /// <summary>
    /// The dust: the pale mass down the bore, the skirt along the ground, and the
    /// brown veil that outlives them.
    ///
    /// <b>Nothing here is thrown, and that is the difference from the burst.</b> A
    /// shell's earth leaves a seat ballistically and comes back down - it has a
    /// closed form and an apex, which is what <c>blast_throw</c> states. A muzzle
    /// blast does not throw earth up, it *pushes dust out*: the elements leave
    /// fast and decelerate, and the whole thing is a lobe lying along the bore
    /// rather than a cone standing on a hole. So the frame's <c>blast_throw</c>
    /// goes unused here, and the model is three deceleration curves.
    ///
    /// <b>Two pairs of stops, mixed by the mass's mean age.</b> The reference's
    /// first frames are a clipped white mass and its last is brown; one pair of
    /// stops cannot be both, and a fade on alpha is not the same thing at all -
    /// that gives a pale cloud going transparent, where what happens is a pale
    /// cloud becoming a brown one. The mean age comes out of <c>dust_part</c>'s
    /// <c>years</c>, which has been accumulated since the burst was written and
    /// never once read.
    ///
    /// <b>Ordinary alpha, premultiplied inside and divided back out</b> - the
    /// burst's arrangement, unchanged, and for the finding underneath it: summed
    /// instead, an element's dark lip *brightens* the one behind it and the mass
    /// melts into one bar.
    /// </summary>
    private const string DustKickShader = @"
shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_never;

FLAME_NOISE

FLAME_INK

BLAST_FRAME

DUST_INK

KICK_FRAME

// The wash: the mass that leaves down the bore, and the one family that is most
// of the picture on the reference's middle frames. Born at the muzzle end,
// decelerating, growing, climbing slowly.
uniform int wash = 26;
uniform float wash_life = 0.95;
uniform float wash_stagger = 0.09;
// How far out along the bore they start, in tile widths. Every element leaving
// through the same point makes the root of the cloud a needle - the tree's licks
// and the burst's clods have the same number for the same reason.
uniform float wash_born = 0.07;
// <b>The exponent on the age, and the whole reason this reads as blown rather
// than as thrown.</b> Under 1 the element covers most of its distance in the
// first fifth of its life and then crawls, which is what a mass pushed by an
// overpressure that has already passed does. At 1 it drifts at a constant rate
// and reads as a balloon on a string.
uniform float wash_drag = 0.42;
uniform float wash_climb = 0.19;
uniform float wash_size = 0.098;
// Flattened along the bore: the mass is a lobe lying down the gun, not a ball
// sitting at the end of it.
uniform float wash_long = 1.70;
uniform float wash_ink = 1.05;

// The skirt: dust pushed out along the ground both ways from under the blast, and
// the half of the effect that says the dust came off the *ground* rather than out
// of the barrel. Its elements lie flat - wide across and shallow up - for the
// burst's collar's reason: under this camera a spread along the ground is an
// ellipse, and a circle reads as a ball.
uniform int skirt = 15;
uniform float skirt_life = 0.55;
uniform float skirt_stagger = 0.05;
uniform float skirt_spread = 0.30;
// How much of the wash's reach the skirt is also carried downrange by. Not zero:
// the blast is directional, so even the sideways dust drifts down the bore.
uniform float skirt_lead = 0.22;
uniform float skirt_climb = 0.055;
uniform float skirt_size = 0.074;
uniform float skirt_flat = 2.40;
uniform float skirt_ink = 0.72;

// The veil: what is still standing when the pale mass has gone, and the thing
// that gives the event its length - alive for eight times as long as the glow.
// Born late, big, slow, climbing.
uniform int veil = 11;
uniform float veil_life = 1.15;
uniform float veil_born = 0.22;
uniform float veil_stagger = 0.26;
uniform float veil_creep = 0.36;
uniform float veil_climb = 0.28;
uniform float veil_size = 0.128;
uniform float veil_ink = 0.62;

// <b>What the dust is coloured, young and old, and the two pairs are the
// finding.</b> Young is very nearly white and very nearly neutral: on frames two
// and three the mass is clipped, with no structure in it at all, and a pale grey
// pair plus the shared light is what that looks like without pretending it is
// emitting. Old is the burst's earth exactly - the same ground in the air with
// the same sun on it, and there is no reason for two answers to that.
//
// <b>Whitened, and it is the *dark* end that moved.</b> Asked to be whiter, the
// pair looks like it should be pushed at the lit end - and pushing there does
// almost nothing, because of where the shared light term sits: dust_seat is 0.52
// and a saturated mass takes dust_dense off it, so a dense young cloud shades at
// about 0.24 and nearly every pixel of it is showing pale_dark. 0.360 is a middle
// grey; that was the colour of the pale phase, whatever the other stop said.
//
// The cost is real and is the reason it is not pushed all the way: dark to lit is
// the whole of the volume, and 0.68..0.99 is a shorter span than 0.36..0.97 was,
// so a whiter cloud is a flatter one. Kept warm by a hair rather than neutral, or
// it reads blue against this ground.
uniform vec3 pale_dark = vec3(0.680, 0.668, 0.642);
uniform vec3 pale_lit = vec3(0.995, 0.988, 0.962);
//
// <b>And the old end is smoke, not earth - which is a change of what this effect
// is, not a tint.</b> It was the burst's own pair, 0.115/0.082/0.055 to
// 0.790/0.650/0.430, on the reading that the reference's last frame is raised
// ground: brown, with embers in it. That reading is defensible and it is not the
// one taken here. A tank gun fires propellant over a hard surface and what
// leaves the muzzle is white; the brown was the *ground* getting into a thing
// that should have stayed the gun's.
//
// <b>The near-black came from the same place, and it is worth naming because it
// looks like a bug and is not.</b> The burst's dark stop is 0.115 - the darkest
// it has - and the shared light term puts a dense mass at about 0.24, so the core
// of an old cloud showed that stop almost neat. Nothing was mixed in and nothing
// was multiplied twice: the second end of the ramp simply was nearly black. Both
// dark ends are high now, so the volume is carried by the light term's own
// spread across the mass rather than by the distance between the pairs.
//
// Cool rather than warm, and still darker than the young pair, so the walk from
// one to the other survives: a shot goes white to grey, which is what separates
// smoke that has just left the barrel from smoke that has been hanging a second.
uniform vec3 smoke_dark = vec3(0.560, 0.562, 0.578);
uniform vec3 smoke_lit = vec3(0.985, 0.986, 0.996);
// Where on the mass's mean age the young pair has fully gone over to the cool
// one. Held long: the pale end is the interesting one, and at 0.42 the mass had
// cooled before anyone could see it was white.
uniform float wane = 0.55;

void fragment() {
    if (level <= 0.0) {
        ALBEDO = vec3(0.0);
        ALPHA = 0.0;
    } else {
        vec2 at = blast_at(UV);
        if (kick_behind(at)) {
            ALBEDO = vec3(0.0);
            ALPHA = 0.0;
        } else {
            vec2 side_dir = kick_across();
            float run = reach * squat;

            // One pass that adds up dust and one that shades the result - the
            // burst's finding, in its own words: a cloud is not a set of things,
            // it is one volume of varying thickness, and compositing the elements
            // over each other gives every one of them a lip and a ridge for the
            // eye to count.
            float dens = 0.0;
            vec2 lean = vec2(0.0);
            float years = 0.0;

            for (int k = 0; k < wash; k++) {
                float fk = float(k);
                float born = wash_stagger * ember_hash(vec2(fk, 81.0));
                float a = (time - born) / max(wash_life, 1e-3);
                if (a > 0.0 && a < 1.0) {
                    float lead = mix(0.32, 1.0, ember_hash(vec2(fk, 82.0)));
                    float out_at = wash_born + run * lead * pow(a, wash_drag);
                    float off = wash_born * 1.6
                                * (2.0 * ember_hash(vec2(fk, 83.0)) - 1.0);
                    float up = wash_climb * pow(a, 0.78)
                               * (0.30 + 0.95 * ember_hash(vec2(fk, 84.0)));
                    vec2 p = snout + blow * out_at + side_dir * off + vec2(0.0, up);
                    float r = wash_size * (0.60 + 0.85 * ember_hash(vec2(fk, 85.0)))
                              * (0.42 + 1.20 * a);
                    float gain = wash_ink * (1.0 - exp(-a / 0.045))
                                 * pow(max(1.0 - a, 0.0), 1.15);
                    dust_part(at, p, r, r * wash_long, blow, fk + 81.0, gain,
                              dust_soft, a, dens, lean, years);
                }
            }

            for (int k = 0; k < skirt; k++) {
                float fk = float(k);
                float born = skirt_stagger * ember_hash(vec2(fk, 91.0));
                float a = (time - born) / max(skirt_life, 1e-3);
                if (a > 0.0 && a < 1.0) {
                    float which = 2.0 * ember_hash(vec2(fk, 92.0)) - 1.0;
                    float out_at = skirt_spread * which * pow(a, 0.55);
                    float up = skirt_climb * pow(a, 0.85)
                               * (0.35 + 0.85 * ember_hash(vec2(fk, 93.0)));
                    // Out sideways from the seat and carried down the bore, the
                    // second the smaller of the two: a blast spreads before it
                    // travels.
                    vec2 p = snout + side_dir * out_at + blow * (run * skirt_lead * a)
                             + vec2(0.0, up);
                    float r = skirt_size * (0.55 + 0.90 * ember_hash(vec2(fk, 94.0)))
                              * (0.45 + 1.10 * a);
                    float gain = skirt_ink * (1.0 - exp(-a / 0.05))
                                 * pow(max(1.0 - a, 0.0), 1.30);
                    // Lying flat, across the screen rather than along the bore -
                    // this family is on the ground and the ground is horizontal
                    // whichever way the gun points.
                    dust_part(at, p, r, r * skirt_flat, vec2(1.0, 0.0),
                              fk + 91.0, gain, dust_soft, a, dens, lean, years);
                }
            }

            for (int k = 0; k < veil; k++) {
                float fk = float(k);
                float born = veil_born + veil_stagger * ember_hash(vec2(fk, 71.0));
                float a = (time - born) / max(veil_life, 1e-3);
                if (a > 0.0 && a < 1.0) {
                    float out_at = run * veil_creep * pow(a, 0.55)
                                   * (0.35 + 0.90 * ember_hash(vec2(fk, 72.0)));
                    float up = veil_climb * pow(a, 0.70)
                               * (0.30 + 0.95 * ember_hash(vec2(fk, 73.0)));
                    float off = wash_born * 2.2
                                * (2.0 * ember_hash(vec2(fk, 74.0)) - 1.0);
                    vec2 p = snout + blow * out_at + side_dir * off + vec2(0.0, up);
                    float r = veil_size * (0.55 + 0.95 * ember_hash(vec2(fk, 75.0)))
                              * (0.50 + 1.05 * a);
                    float gain = veil_ink * (1.0 - exp(-a / 0.12))
                                 * pow(max(1.0 - a, 0.0), 1.20);
                    dust_part(at, p, r, r * 1.25, vec2(0.0, 1.0), fk + 71.0,
                              gain, dust_soft, a, dens, lean, years);
                }
            }

            if (dens <= 1e-4) {
                ALBEDO = vec3(0.0);
                ALPHA = 0.0;
            } else {
                float mass = dust_mass(at, dens);
                float lit = dust_lit(mass, lean);
                // <b>The mass's own mean age, not the event's clock.</b> A young
                // element born late is young: what has gone brown is dust that has
                // been in the air a while, and where the wash and the veil overlap
                // the two mix by how much each of them put there. The clock cannot
                // say that, and dividing by the density is what makes it a mean
                // rather than a total.
                float old = smoothstep(0.0, max(wane, 1e-3), years / dens);
                ALBEDO = mix(mix(pale_dark, smoke_dark, old),
                             mix(pale_lit, smoke_lit, old), lit);
                ALPHA = clamp(mass * dust_ink * level * blast_footing(at),
                              0.0, 1.0);
            }
        }
    }
}
";

    /// <summary>
    /// The light: the pale core that clips the picture, and the embers left in the
    /// dust after it.
    ///
    /// <b>Two windows, and the ratio is what the reference frames are.</b> The
    /// core is out in a sixth of a second against the dust's one and a half, so
    /// the light is a tenth of the event. That is what separates a gun going off
    /// from something burning: the same elements held four times as long read as a
    /// fire on the deck.
    ///
    /// <b>The white is bought with brightness, not with a whiter colour.</b> No
    /// stop of the shared ramp is near white - the rule the forest's fire and the
    /// burst are both written under, and for the measured reason: red saturating
    /// is not the failure, green and blue coming up with it is. An additive layer
    /// at a gain over one clips its own middle to yellow-white on its own, which is
    /// exactly what the reference's overexposed core is: a clipped sensor, not a
    /// white flame.
    ///
    /// <b>The embers are the last frame's whole content.</b> Once the flame is
    /// gone what is still glowing in the brown mass is points, they outlive the
    /// core by three times, and they are what makes settled dust read as
    /// *burnt* dust rather than as fog. They take the ink's own ember colour,
    /// which is pulled toward red rather than the yellow a real spark is - a few
    /// pixels of low-alpha yellow added to a pale field is a grey speck, measured.
    /// </summary>
    private const string GlowShader = @"
shader_type spatial;
render_mode unshaded, cull_disabled, blend_add, depth_draw_never;

FLAME_NOISE

FLAME_INK

BLAST_FRAME

KICK_FRAME

// The core: the pale mass that clips. Seated a third of the way down the bore
// rather than at the muzzle, because on the reference it is - the flame is at the
// brake and the bright mass is out in front of it, and the two are not the same
// thing.
uniform int cores = 5;
uniform float core_life = 0.13;
uniform float core_stagger = 0.035;
uniform float core_seat = 0.34;
uniform float core_size = 0.115;
uniform float core_long = 1.45;
// <b>Its own pale body rather than a place on the shared ramp, and that is the
// second thing the first shot said.</b> The ramp's hot end is (1.0, 0.50, 0.25) -
// an orange, correctly, because it is a flame's - and seven elements of it added
// together over a tank came out a fireball sitting on the engine deck rather than
// a shot going off. What the reference's core is is not a flame at all: it is a
// clipped sensor, white with a yellow edge, and the honest way to say that is a
// pale body, not a hotter orange. The ink's no-stop-near-white rule is about the
// ramp the *flame* walks and is untouched by this.
uniform vec3 core_colour = vec3(1.00, 0.945, 0.840);
// Under one, and it was over one. See core_colour: the white is the colour's
// now, so the gain is back to being how strong the light is.
uniform float core_gain = 0.58;

// The embers: burning grains that leave the bore with the shot.
//
// <b>Small, dim and many</b>, which the first cut was not: thirteen at 0.019 of a
// tile and a gain of 0.85 came out a row of orange pins leading away from the
// muzzle - the sparkler ProcFlash's tongues were pulled off, arrived at from the
// other direction. An ember is not a thing to be seen, it is a thing seen
// *through dust*, so it has to be smaller than the mass's own lumps.
//
// <b>And they leave the barrel, which is a statement about where they are and
// the second cut got it wrong.</b> Spread across the whole width of the cloud by
// a *fixed* random offset, they did not fly out of anything - they were simply
// present in the dust, thickest nowhere. What comes out of a bore comes out in a
// cone: an ember's own direction is fixed at the muzzle and its distance off the
// axis is therefore proportional to how far along it has got, which is one
// multiply and is the whole difference between a spray and a scatter. It is
// ProcFlash.Splay's statement, made in the fragment because these have no array.
//
// <b>They also all leave at once.</b> Staggered over 0.46s they were still being
// born half a second after the gun fired, which put fresh sparks in settled dust
// - the reason they read as belonging to the cloud rather than to the shot. The
// stagger left is the width of the event, not a source that keeps running.
uniform int embers = 20;
uniform float ember_life = 0.46;
uniform float ember_born = 0.02;
uniform float ember_stagger = 0.11;
uniform float ember_size = 0.0105;
// How far down the bore they get, as a share of the cloud's own reach. Well
// inside it: they are the part of the shot that is still at the gun.
uniform float ember_creep = 0.40;
// Half-slope of the cone they leave in, across the bore and up it. Multiplied by
// the distance travelled rather than used as an offset - see above.
uniform float ember_cone = 0.34;
uniform float ember_gain = 0.46;

void fragment() {
    if (level <= 0.0) {
        ALBEDO = vec3(0.0);
        ALPHA = 0.0;
    } else {
        vec2 at = blast_at(UV);
        if (kick_behind(at)) {
            ALBEDO = vec3(0.0);
            ALPHA = 0.0;
        } else {
            vec2 side_dir = kick_across();
            float run = reach * squat;
            // Alpha-over inside the layer, additive on the way out - the flash's
            // arrangement and the burst's fire's: surfaces stack the way surfaces
            // stack, and the layer as a whole adds light.
            vec4 lit = vec4(0.0);

            for (int k = 0; k < cores; k++) {
                float fk = float(k);
                float born = core_stagger * ember_hash(vec2(fk, 21.0));
                float a = (time - born) / max(core_life, 1e-3);
                if (a > 0.0 && a < 1.0) {
                    float out_at = run * core_seat
                                   * (0.55 + 0.85 * ember_hash(vec2(fk, 22.0)))
                                   * (0.35 + 0.85 * a);
                    float off = core_size * 0.55
                                * (2.0 * ember_hash(vec2(fk, 23.0)) - 1.0);
                    float up = core_size * 0.85 * ember_hash(vec2(fk, 24.0));
                    vec2 p = snout + blow * out_at + side_dir * off + vec2(0.0, up);
                    float r = core_size * (0.65 + 0.75 * ember_hash(vec2(fk, 25.0)))
                              * (0.55 + 0.80 * a);
                    // Brightest the instant it arrives and still growing as it
                    // dims, which is what makes it read as expanding gas rather
                    // than as a lamp switched on - the flash's phase table says
                    // the same thing with a table.
                    float gain = core_gain * (1.0 - exp(-a / 0.02))
                                 * pow(max(1.0 - a, 0.0), 1.60);
                    lit = flame_over(lit, flame_spark(at, p, r, r * core_long,
                                                      blow, fk + 21.0,
                                                      core_colour, gain));
                }
            }

            for (int k = 0; k < embers; k++) {
                float fk = float(k);
                float born = ember_born + ember_stagger * ember_hash(vec2(fk, 31.0));
                float a = (time - born) / max(ember_life, 1e-3);
                if (a > 0.0 && a < 1.0) {
                    float out_at = run * ember_creep
                                   * (0.25 + 0.95 * ember_hash(vec2(fk, 32.0)))
                                   * pow(a, 0.45);
                    // The cone: a direction taken at the muzzle, so the distance
                    // off the axis is the distance travelled times the slope.
                    // At a = 0 every ember is at the muzzle, which is where they
                    // come from.
                    float off = out_at * ember_cone
                                * (2.0 * ember_hash(vec2(fk, 33.0)) - 1.0);
                    float up = out_at * ember_cone * 0.70
                               * (2.0 * ember_hash(vec2(fk, 34.0)) - 1.0)
                               + 0.035 * pow(a, 0.8);
                    vec2 p = snout + blow * out_at + side_dir * off + vec2(0.0, up);
                    float r = ember_size * (0.55 + 0.95 * ember_hash(vec2(fk, 35.0)));
                    float gain = ember_gain * (1.0 - exp(-a / 0.06))
                                 * pow(max(1.0 - a, 0.0), 1.40);
                    lit = flame_over(lit, flame_spark(at, p, r, r * 1.30, blow,
                                                      fk + 31.0, ember_colour,
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
    /// in one place so a check can ask whether the kick and the burst are one
    /// text.</summary>
    internal static readonly string DustKickCode =
        DustKickShader.Replace("FLAME_NOISE", Stage3D.EmberNoiseCode)
                      .Replace("FLAME_INK", Stage3D.FlameInk)
                      .Replace("BLAST_FRAME", ProcBlast.FrameCode)
                      .Replace("DUST_INK", ProcBlast.DustInk)
                      .Replace("KICK_FRAME", KickFrame);

    internal static readonly string GlowCode =
        GlowShader.Replace("FLAME_NOISE", Stage3D.EmberNoiseCode)
                  .Replace("FLAME_INK", Stage3D.FlameInk)
                  .Replace("BLAST_FRAME", ProcBlast.FrameCode)
                  .Replace("KICK_FRAME", KickFrame);

    private static readonly Shader Kicking = new() { Code = DustKickCode };
    private static readonly Shader Glowing = new() { Code = GlowCode };
}
