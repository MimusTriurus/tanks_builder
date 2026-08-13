using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// Where the muzzle flash comes from. Both are kept so the two can be compared
/// on the same tank in the same frame - the rendered one is the point of the
/// whole pipeline, and "it looks better" is not a claim to make from memory.
/// </summary>
public enum FlashSource
{
    /// <summary>The painted sheet, rotated and squashed into place.</summary>
    Sheet,

    /// <summary>Layers rendered in Blender with the tank, on the shared
    /// anchor.</summary>
    Rendered,
}

/// <summary>
/// Hull and turret drawn as two independent layers on one shared anchor.
///
/// The whole pipeline exists so these two can point in different directions.
/// Both tiles are drawn at (0,0) minus the shared anchor, with no per-layer
/// offset of any kind - if a tank only looks right when one layer is nudged,
/// the atlases are wrong, not this code.
/// </summary>
public sealed partial class TankSprite : Node2D
{
    public AtlasSet? Atlas;
    public double HullFacing = 270.0;
    public double TurretFacing = 270.0;
    public bool ShowHull = true;
    public bool ShowTurret = true;

    /// <summary>
    /// How big this tank is drawn, against the size its atlas was rendered at.
    /// The class's <see cref="MovementProfile.Size"/> times whatever the panel's
    /// dial is on.
    ///
    /// It is the node's own scale, and that is the whole of the implementation
    /// for a reason worth stating: every layer in this harness is drawn at
    /// <c>-Anchor</c> in this node's space, so the node's origin *is* the turret
    /// axis, and scaling the node scales about that axis exactly. Children come
    /// with it, so the belts, the turret, the scars, the plume and a shell's
    /// offset on its plate all grow by the same factor about the same point,
    /// with no per-layer arithmetic to keep in agreement. Scaling each layer's
    /// draw rect instead would be that arithmetic, in eight places.
    ///
    /// Uniform by construction: a tank drawn wider than it is tall is not a
    /// bigger tank, it is a distorted render.
    ///
    /// One thing outside this node does have to follow it, and only one: the
    /// belt's link is a length on the ground, so <see cref="TrackLoop.Pitch"/>
    /// takes the same factor or the tread slips by exactly the amount the tank
    /// was scaled by.
    /// </summary>
    public float BodyScale
    {
        get => Scale.X;
        set
        {
            Scale = new Vector2(value, value);
            QueueRedraw();
        }
    }

    /// <summary>Draws a cross on the rotation axis. An axis error does not
    /// distort a sprite, it walks it around a circle as the turret turns, and
    /// that is hard to see without a fixed mark to judge against.</summary>
    public bool ShowAxis;

    /// <summary>
    /// True: the turret holds its world heading while the hull turns under it -
    /// a gun staying on target through a manoeuvre.
    /// False: the turret is rigid with the hull and swings with it.
    ///
    /// It was called <c>TurretLocked</c>, and that name reads backwards to
    /// anyone who has met a real one: a traverse lock locks the turret <i>to the
    /// hull</i>, so "locked on" ought to mean it rides round with it - the
    /// opposite of what this does. Nothing about the behaviour changed; the name
    /// now says which of the two it is, because "locked" never said locked to
    /// what.
    /// </summary>
    public bool TurretHoldsHeading = HoldsHeadingByDefault;

    /// <summary>
    /// Which of the two a tank comes up in: riding the hull.
    ///
    /// The other one is the headline - a gun holding its world heading through
    /// a manoeuvre is the first thing the layered atlases exist to show - and
    /// it is still one keypress away. Riding is what a tank does when nobody is
    /// laying the gun, so it is the resting state rather than the demo, and the
    /// bench now opens on the tank rather than on the point being made.
    ///
    /// A named constant for the reason the tracer's and the motor's are: a
    /// default sitting in a field initialiser is a thing nobody reads, and this
    /// one decides what every screenshot of a turning tank shows.
    /// </summary>
    public const bool HoldsHeadingByDefault = false;

    /// <summary>The only place hull heading changes, so the turret modes cannot
    /// be bypassed by some other code path - driving and manual turns both come
    /// through here.</summary>
    public void TurnHull(double delta)
    {
        if (Math.Abs(delta) < 1e-9)
            return;
        HullFacing = Mod(HullFacing + delta, 360.0);
        if (!TurretHoldsHeading)
            TurretFacing = Mod(TurretFacing + delta, 360.0);
        QueueRedraw();
    }

    public static double Mod(double a, double n) => (a % n + n) % n;

    /// <summary>Body pitch as a shear amplitude; positive is nose down.
    /// See <see cref="PitchTransform"/> for why a shear and not a rotation.</summary>
    public double Pitch;

    /// <summary>Whole pixels of vertical jolt from the ground. Applied to the
    /// draw rect rather than to Position, which the movement code compares
    /// against cell anchors and must stay exact.</summary>
    public int Shake;

    /// <summary>Roll, as a shear amplitude, positive displacing the top of the
    /// hull to its left: the shear runs along GroundDirection(HullFacing + 90)
    /// and headings count anticlockwise. Shares the machinery with
    /// <see cref="Pitch"/>, and <see cref="Recoil"/> depends on the sign.</summary>
    public double Roll;

    /// <summary>Engine tremble, as pitch and roll shear amplitudes. Held apart
    /// from <see cref="Pitch"/> and <see cref="Roll"/> only so the panel and the
    /// trace can name the source; the shear sums them, which is exact because
    /// the displacement is linear in both.</summary>
    public double TremblePitch;
    public double TrembleRoll;

    /// <summary>The kick of firing. On its own channel because it is the one
    /// tilt the stabiliser does not get to reject - see
    /// <see cref="TurretStabilised"/>.</summary>
    public double RecoilPitch;
    public double RecoilRoll;

    /// <summary>The muzzle flash sheet, and which of its frames is showing.
    /// -1 for not firing.</summary>
    public FlashSheet? Flash;
    public int FlashFrame = -1;

    /// <summary>Sheet pixels to sprite pixels. The sheet is drawn at 256 to a
    /// frame and the gun is about 68px long, so a flash wants to come out around
    /// 150px: a little longer than the barrel, which is what a tank gun looks
    /// like.</summary>
    public float FlashScale = 0.60f;

    /// <summary>Which flash to draw. Rendered where the atlas has it, and the
    /// sheet is the fallback rather than the loser: a scene whose barrel has not
    /// been cut out yet has no rendered flash to show. All three tanks on disk
    /// have one, so the sheet path is now only there for the A/B.</summary>
    public FlashSource Source = FlashSource.Rendered;

    /// <summary>Phase of the rendered shot, or -1 between shots. The sheet path
    /// uses <see cref="FlashFrame"/> instead; they are counted separately
    /// because the sheet has sixteen frames and the rendered set has eight, and
    /// collapsing them into one number would tie the two tempos together.</summary>
    public int ShotPhase = -1;

    /// <summary>Phase of the engine plume's loop, or -1 with the effect off.
    /// Driven by <see cref="ExhaustLoop"/>, which wraps rather than ending -
    /// there is no "between shots" for an engine that is running.</summary>
    public int ExhaustPhase = -1;

    /// <summary>How much of the plume's alpha survives: an idling engine
    /// breathes a haze and a working one smokes.</summary>
    public float ExhaustDensity = 1.0f;

    public bool ShowExhaust = true;

    /// <summary>Whether this tank is on fire.</summary>
    public bool Burning;

    /// <summary>How much of the flame and of its column survive. The flame goes
    /// out on a wreck that has finished burning and the column thins and stays -
    /// see <see cref="Wreck"/>. Pulled by the layer at draw time like
    /// <see cref="ExhaustDensity"/>, and for the same reason.</summary>
    public float FireDensity = 1.0f;
    public float SmokeDensity = 1.0f;

    /// <summary>Phases of the burning wreck's two loops, or -1 when it is not
    /// burning. Two numbers rather than one because the flame and the column run
    /// at different rates over the same rendered phases - see
    /// <see cref="BurnLoop"/>.</summary>
    public int FirePhase = -1;
    public int BurnPhase = -1;

    /// <summary>Phase of a shell arriving, or -1 between hits. An event like the
    /// shot, so it runs out rather than wrapping - see <see cref="HitLoop"/>.</summary>
    public int HitPhase = -1;

    /// <summary>Where each belt is in its cycle, or -1 with no belts to show.
    /// Driven by <see cref="TrackLoop"/> off distance travelled, not off a
    /// timer: the belt is what the ground is winding against.
    ///
    /// One per side, because the two sides do not agree - see
    /// <see cref="Vehicle.TrackLeft"/>. On a pivot they run opposite ways, and
    /// on a turning drive at different rates.</summary>
    public int TrackPhaseLeft = -1;

    public int TrackPhaseRight = -1;

    /// <summary>How much of each belt is showing as a smear rather than as
    /// links: 0 crisp, 1 nothing but the phase average. From
    /// <see cref="TrackLoop.Blur"/>; the layer pulls it at draw time. Per side
    /// for the reason the phase is, and it shows: a tank cornering has its
    /// outer belt smearing while the inner one is still countable.</summary>
    public double TrackBlurLeft;

    public double TrackBlurRight;

    /// <summary>Whether the belts are showing at all - either side will do,
    /// since they are set and cleared together.</summary>
    public bool TracksRunning => TrackPhaseLeft >= 0 || TrackPhaseRight >= 0;

    private static bool IsLeftBelt(string layer) => layer == AtlasSet.TrackNames[0];

    public int TrackPhaseOf(string layer) =>
        IsLeftBelt(layer) ? TrackPhaseLeft : TrackPhaseRight;

    public double TrackBlurOf(string layer) =>
        IsLeftBelt(layer) ? TrackBlurLeft : TrackBlurRight;

    /// <summary>Whether the belts are drawn at all. On by default and not an
    /// option in the sense the movement effects are - a tank has tracks - but
    /// worth being able to switch off, because "does the hull layer still cover
    /// everything it should" is a question you answer by looking at it
    /// without them.</summary>
    public bool ShowTracks = true;

    /// <summary>
    /// Which pose the gun tube is in: 0 for rest, up to the atlas's last phase
    /// at full travel.
    ///
    /// **Zero, not -1, and it is the only phase field here that starts there.**
    /// Every other event on this list is absent between events - the shot, the
    /// hit - so -1 is their idle. The tube is part of the tank and rest is a pose
    /// it is drawn in, so this idles at 0 and a -1 would blink the gun out.
    /// Driven by <see cref="RecoilLoop"/>.
    /// </summary>
    public int RecoilPhase;

    /// <summary>Where the live hit is drawn, in pixels from the layer's anchor.
    /// The only layer in the whole set that is placed rather than anchored, and
    /// the offset comes from the plate table the renderer stamps beside the
    /// atlas rather than from anything worked out here.</summary>
    public Vector2 HitOffset;

    /// <summary>Whether the live hit is on a plate turned away from the camera,
    /// and so belongs behind the tank rather than on it.</summary>
    public bool HitBehind;

    /// <summary>How big the live hit is drawn, against the size it was rendered
    /// at. A shell has a calibre and the layer does not, so this is the one
    /// place where an effect is scaled on screen rather than in Blender.
    ///
    /// It scales about the point of impact, not about the anchor: the layer is
    /// placed on a plate, and scaling about the anchor would slide it off the
    /// plate by the same fraction it grew. Both halves take it together -
    /// a bigger shell throws more dust as well as more light, and thinning one
    /// against the other is what turns the pair tan (see hit_burst.py).
    /// </summary>
    /// <summary>The calibre of the shell currently on screen, pushed here by the
    /// harness from <see cref="HitLoop.Scale"/> each frame the hit is live.
    ///
    /// Not the dial. The dial says what the *next* round will be; this says what
    /// the one being drawn went off at, and the two are different numbers the
    /// moment anybody touches the dial while the dust is settling.</summary>
    public float HitScale = 1.0f;

    /// <summary>
    /// The effect layers, in the order they are added to the tree - which,
    /// because children draw after their parent and in order, is the order they
    /// composite in. This array is the compositing policy, and it is public so
    /// the policy can be asserted rather than read off the screen.
    ///
    /// Exhaust first, because it is the ambient one and everything else should
    /// read over it. Then the wreck's column and its flame - dark first,
    /// additive second, which is not a preference: the column exists to give the
    /// flame something dark to be added to, and reversed it would grey out the
    /// fire it is there to back. Then the shot's pair on top, because a gun
    /// going off is the event and should read over a fire that is always there.
    /// </summary>
    /// Then the hit's pair last of all - dark first, additive second again -
    /// because a shell landing is the newest thing to have happened. Its two
    /// layers are also the only ones that may end up *behind* the tank, which
    /// they do by their own flag rather than by their place in this list: where
    /// a hit sits in the stack and which side of the hull it is on are separate
    /// questions, and the second one changes as the tank turns.
    /// And the scars first of all, because they are the only layers that are
    /// *on* the tank rather than in front of it: paint on the armour, under
    /// smoke drifting across it and under a shell landing on it.
    /// The belts come before even those, because they are not on the tank -
    /// they *are* the tank, the part of it the hull layer had held out. They go
    /// straight after the hull and under everything that happens to it.
    ///
    /// And the turret is in this list, immediately after the belts, rather than
    /// being drawn by the parent alongside the hull. "Straight after the hull"
    /// used to be a comment this list could not keep: children draw after the
    /// parent, so a turret drawn up there went down before every belt and the
    /// belt painted over it. See <see cref="EffectLayer.Turret"/> for the
    /// geometry and the measurement.
    /// And the gun tube goes straight after the turret, which is the only place
    /// it can go: it slides into the mantlet, so it has to be over the turret
    /// rather than under it, and it is part of the tank, so it comes before
    /// everything that happens to the tank. It left the turret layer for the
    /// belts' reason - it moves relative to what it is bolted to.
    /// <summary>
    /// Where the contact shadow sits in the canvas, as an absolute z.
    ///
    /// Not in <see cref="LayerOrder"/>'s ordering at all, and that is the point:
    /// the shadow is not on the tank, it is the ground with the tank's dark on
    /// it, so it has to go under *every* tank on the board rather than under
    /// this one. Two heavies in neighbouring columns overlap - 209px of broadside
    /// against 186px between centres - and a shadow ordered with its own tank
    /// would land on its neighbour.
    ///
    /// One above the selection ring, which is one above the field: the ring is
    /// paint on the ground and a shadow falls across paint. The tanks take their
    /// z from where they stand, which is always positive on this board.
    ///
    /// It is still a child of the tank's node, because that is what gives it the
    /// class scale about the anchor and the shared anchor itself for free -
    /// z-index is the only thing it takes from somewhere else.
    /// </summary>
    public const int ShadowZ = -98;

    /// <summary>Whether the contact shadow is drawn. An option like the movement
    /// effects and unlike the belts, because the A/B is the whole argument for
    /// it: the complaint it answers is that the tank reads as a decal, and that
    /// is a comparison, not a measurement.</summary>
    public bool ShowShadow = true;

    /// <summary>
    /// What charring does to the paint, as a shader on the tank's own layers.
    ///
    /// A shader rather than <c>Modulate</c> because a modulate is a per-channel
    /// multiply and cannot desaturate: the paint here is green, and scaling a
    /// green tank down gives a dark green tank. Burnt metal has no hue left, so
    /// the colour has to be pulled toward its own luminance first and darkened
    /// after.
    ///
    /// <c>COLOR</c> already holds the texel, and nothing here samples
    /// <c>TEXTURE</c> at all. That is not a shortcut, it is the trap: written
    /// the obvious way - sample, then multiply into COLOR - this backend gives
    /// screen = texel x texel, so every tank on the board opened <em>already
    /// darkened</em> and burn = 0 was not a no-op. Measured exactly, which is
    /// what named it: 153 -> 92, 88 -> 30, 134 -> 70, all of them c squared to
    /// the level. Two wrong fixes came first, an sRGB encode and a sqrt, and
    /// both are the same mistake again - they treat a doubled multiply as a
    /// transfer function. The check that it is gone is bit exactness: an intact
    /// tank with this material is pixel for pixel a tank with no material.
    ///
    /// <c>darken</c> was chosen off the picture. Measured on the hull band
    /// against the same frame alive: 0.55 leaves the mean at 41 of 68 and reads
    /// as a grey tank rather than a burnt one, 0.22 lands at 16 and is nearly a
    /// silhouette. 0.35 gives 26 with 40 at the 95th percentile - the cant of
    /// the turret and the tube still read, and it is plainly charred. Note the
    /// soot on the plates needed the opposite: a scar has a lit plate around it
    /// to be dark against, while a wreck has nothing behind it but pale ground
    /// and has to carry its own shape.
    ///
    /// Saturation goes to zero rather than most of the way. Burnt metal has no
    /// hue, and green that survives reads as a tank in shadow - the exact
    /// failure a tint is prone to and the whole reason this is a shader rather
    /// than a modulate. Measured: 35 levels of saturation alive against 0.4
    /// charred.
    ///
    /// It only ever touches the tank. The additive layers - flame, flash, burst -
    /// must not be tinted at all: darkening light is the opposite of what the
    /// wreck needs, since a dark hull under an additive flame is precisely what
    /// makes the flame work. That falls out of which layers ask for the parent's
    /// material; see <see cref="EffectLayer.Armour"/>.
    /// </summary>
    private static readonly Shader CharShader = new()
    {
        Code = """
            shader_type canvas_item;

            uniform float burn : hint_range(0.0, 1.0) = 0.0;
            uniform float darken : hint_range(0.0, 1.0) = 0.35;
            uniform float grey : hint_range(0.0, 1.0) = 1.0;

            void fragment() {
                float lum = dot(COLOR.rgb, vec3(0.299, 0.587, 0.114));
                vec3 soot = mix(COLOR.rgb, vec3(lum), grey) * darken;
                COLOR.rgb = mix(COLOR.rgb, soot, burn);
            }
            """,
    };

    /// <summary>The char shader's source, so the self-test can assert the one
    /// thing that brought every tank on the board out already darkened: this
    /// works on <c>COLOR</c>, which already holds the texel, and must never
    /// sample <c>TEXTURE</c> again.</summary>
    internal static string CharShaderCode => CharShader.Code;

    /// <summary>This tank's own instance of it. Per sprite and not shared: the
    /// uniform is how charred *this* hull is, and one material across the board
    /// would burn all three the moment one died.</summary>
    private ShaderMaterial? _char;

    private double _charAt = -1.0;

    /// <summary>
    /// Whether this tank is a wreck, and so which pose its own layers draw.
    ///
    /// Separate from <see cref="Char"/> and not derived from it, because the two
    /// happen at different speeds and both are right: the turret drops and the gun
    /// falls at the moment of the hit, and the paint blackens over the second and a
    /// half after it. A flag read off the char would hold the turret level until
    /// the hull had begun to blacken, which reads as the geometry lagging the
    /// colour.
    /// </summary>
    public bool Wrecked
    {
        get => _wrecked;
        set
        {
            if (value == _wrecked)
                return;
            _wrecked = value;
            QueueRedraw();
        }
    }

    private bool _wrecked;

    /// <summary>How charred the paint is, 0 clean to 1 burnt out.</summary>
    public double Char
    {
        get => Math.Max(_charAt, 0.0);
        set
        {
            double want = Math.Clamp(value, 0.0, 1.0);
            if (want == _charAt)
                return;
            _charAt = want;
            _char?.SetShaderParameter("burn", want);
            QueueRedraw();
        }
    }

    /// <remarks>
    /// The wreck's layers sit immediately beside the ones they stand in for, and
    /// that placement is the whole of what the order has to say about them:
    /// exactly one of each pair ever draws, so where the pair sits matters and
    /// which half is showing does not. Putting them at the end instead would have
    /// worked today and been wrong the moment anything composited between the
    /// belts and the turret - the reason the turret is in this list at all.
    /// </remarks>
    /// <remarks>
    /// The scars moved ahead of the turret when it came out of their holdout.
    /// They are paint on the hull and the turret stands over them at every one
    /// of the twenty-four headings - measured, all four plates - so what used to
    /// be cut out of them is now simply drawn on top of them. Leaving them after
    /// the turret would have put a mark on the hull's front plate over the
    /// turret that covers it.
    /// </remarks>
    public static readonly string[] LayerOrder =
        new[] { AtlasSet.ShadowName }
            .Concat(AtlasSet.TrackNames).Concat(AtlasSet.WreckTrackNames)
            .Concat(AtlasSet.ScarNames)
            .Concat(new[] { "turret", AtlasSet.BarrelName,
                            AtlasSet.WreckTurretName })
            .Concat(new[]
        {
            AtlasSet.ExhaustName, AtlasSet.BurnName, AtlasSet.FireName,
            "smoke", "flash", AtlasSet.DustName, AtlasSet.BurstName,
        }).ToArray();

    /// <summary>
    /// The z-index a layer draws at, given where the hull is pointing.
    /// </summary>
    /// <remarks>
    /// Tree order is the ordering for everything that never moves in the stack.
    /// The three engine-deck effects do move: each is in front of the turret on
    /// some headings and behind it on others, and which is stamped in the atlas
    /// (see <see cref="AtlasSet.OverTurret"/>). Z-index is how a sibling gets to
    /// jump the queue without being reparented every frame.
    ///
    /// Doubling the slot leaves a gap under the turret to land in, and landing
    /// all three in the same slot is deliberate: siblings tied on z-index fall
    /// back to tree order, which already has them in the right order among
    /// themselves. One number, no table.
    ///
    /// A set with no flag reports false everywhere, so every layer keeps its
    /// tree slot and nothing about it changes - which is what has to happen,
    /// because such a set has the turret cut into its alpha instead.
    /// </remarks>
    public static int ZFor(string layer, bool overTurret)
    {
        int slot = Array.IndexOf(LayerOrder, layer);
        if (slot < 0)
            return 0;
        if (overTurret || Array.IndexOf(AtlasSet.OrderedNames, layer) < 0)
            return 2 * slot;
        return 2 * Array.IndexOf(LayerOrder, "turret") - 1;
    }

    /// <summary>Which kind of layer each name is. Kept beside
    /// <see cref="LayerOrder"/> so there is one list, not a list and a
    /// hand-written sequence that has to agree with it.</summary>
    /// <remarks>Internal rather than private so the self-test can ask, for
    /// every name in <see cref="LayerOrder"/>, whether it chars with the hull.
    /// That is one claim about a list, and reading it off the list is the only
    /// way it cannot drift from it.</remarks>
    internal static EffectLayer MakeLayer(string layer) => layer switch
    {
        AtlasSet.ShadowName => EffectLayer.Shadow(layer),
        AtlasSet.ExhaustName => EffectLayer.Exhaust(layer),
        AtlasSet.BurnName => EffectLayer.BurningSmoke(layer),
        AtlasSet.FireName => EffectLayer.BurningFire(layer),
        AtlasSet.DustName => EffectLayer.HitDust(layer),
        AtlasSet.BurstName => EffectLayer.HitBurst(layer),
        "turret" => EffectLayer.Turret(layer),
        AtlasSet.BarrelName => EffectLayer.Barrel(layer),
        AtlasSet.WreckTurretName => EffectLayer.WreckTurret(layer),
        _ when Array.IndexOf(AtlasSet.WreckTrackNames, layer) >= 0
            => EffectLayer.WreckTrack(layer),
        _ when Array.IndexOf(AtlasSet.TrackNames, layer) >= 0
            => EffectLayer.Track(layer),
        _ when EffectLayer.FaceOf(layer) != "" => EffectLayer.Scar(layer),
        "smoke" => EffectLayer.Normal(layer),
        _ => EffectLayer.Additive(layer),
    };

    /// <summary>How much damage each plate is carrying: absent or -1 for clean,
    /// otherwise a level index into the scar layer's phases.
    ///
    /// State rather than event, which is the whole shape of this layer: it is
    /// set when something hits the plate and then stays until something else
    /// does. Nothing here advances with time.
    /// </summary>
    /// <summary>One round's worth of damage: how bad, and where on the plate it
    /// landed - along it as a fraction of its half-width, and up its slope as a
    /// fraction of its half-height.
    ///
    /// Fractions rather than a pixel offset, because both of the plate's screen
    /// axes turn with the hull: stored in pixels they would be right at the
    /// heading they were taken on and wrong at the other twenty-three. The mark's
    /// *base* position needs no storing at all - the layer was rendered on its
    /// plate at every heading, so the centroid is already where it belongs, and
    /// these only say how far from it.
    ///
    /// Two numbers rather than one, and the second one is not decoration: with
    /// only <c>Along</c> every round on a plate landed at the same height, so
    /// three hits read as a row of stamps rather than as a group. They are kept
    /// apart rather than as one screen vector because the two rulers are
    /// different lengths - see <see cref="MarkOffset"/>.
    /// </summary>
    public readonly record struct Mark(int Level, float Along, float Up);

    /// <summary>
    /// What each plate is carrying, oldest first.
    ///
    /// A list rather than one mark, because rounds do not land on top of each
    /// other: a plate that has taken three has three holes in it, at the three
    /// places they arrived. The level is how many times *that plate* had been
    /// hit when each one landed, so an early scorch stays a scorch after a
    /// later round punches through beside it - the marks are a record of what
    /// happened, not a single state that gets overwritten.
    ///
    /// Capped at one per level, which is both a budget and the thing that keeps
    /// them looking different: the decals are rendered art, so two marks at the
    /// same level are the same drawing twice and read as stamped rather than as
    /// damage. Past the cap the oldest goes.
    /// </summary>
    private readonly Dictionary<string, List<Mark>> _scars = new();

    private static readonly IReadOnlyList<Mark> NoMarks = Array.Empty<Mark>();

    public IReadOnlyList<Mark> MarksOn(string face) =>
        _scars.TryGetValue(face, out List<Mark>? marks) ? marks : NoMarks;

    /// <summary>The worst a plate is carrying, or -1 if it is clean.</summary>
    public int ScarLevel(string face)
    {
        int worst = -1;
        foreach (Mark mark in MarksOn(face))
            worst = Math.Max(worst, mark.Level);
        return worst;
    }

    /// <summary>
    /// Where a mark is drawn, in pixels from the anchor.
    ///
    /// Both of the plate's own axes, which is what makes the offset stay on the
    /// armour whatever the plate is doing on screen: the tangent shortens as the
    /// flank turns away, the slope shortens as the glacis leans back, and a shift
    /// expressed in halves of each shortens with them.
    /// </summary>
    public Vector2 MarkOffset(string face, Mark mark) =>
        Atlas is null
            ? Vector2.Zero
            : Atlas.HitTangent(face, HullFacing) * mark.Along
              + Atlas.HitSlope(face, HullFacing) * mark.Up;

    /// <summary>
    /// How far into a plate each shell has got, in levels. Kept apart from the
    /// marks because the marks are capped and this is not: once a plate carries
    /// its third hole the oldest is dropped, and a wear count read off the list
    /// would then stop climbing and start reporting the plate as fresher than it
    /// is.
    /// </summary>
    private readonly Dictionary<string, int> _wear = new();

    /// <summary>How deep this plate has been worked, in levels.</summary>
    public int Wear(string face) => _wear.TryGetValue(face, out int worn) ? worn : 0;

    /// <summary>
    /// How many rounds have got past this tank's paint, anywhere on it, from
    /// anybody. <see cref="Gunnery.PenetrationsToKill"/> of them finish it.
    ///
    /// The tank's, not a plate's and not a pair's: a hull shot by two mediums is
    /// being shot by two mediums, and it takes what it takes. Same reasoning as
    /// the victim owning the shell count that picks the impact take.
    ///
    /// **Not derived from the wear, and that is the trap worth naming.** Summing
    /// <see cref="Wear"/> across the plates looks like the same number and is not:
    /// wear is per plate and caps at the deepest level, so three rounds spread
    /// over three plates would count differently from three into one, and a plate
    /// a heavy has already breached stops climbing while rounds keep arriving. The
    /// kill is about the tank, so it is counted on the tank - the same argument
    /// that keeps the wear itself apart from the capped list of marks.
    ///
    /// Counted inside <see cref="Damage"/> and <see cref="DamageTo"/> rather than
    /// by whoever calls them, because those two are the only places a level is
    /// produced. A caller cannot forget to count, and a second path into the
    /// armour cannot arrive uncounted.
    /// </summary>
    public int Penetrations { get; private set; }

    /// <summary>Whether <paramref name="level"/> got past the paint. Zero is
    /// exactly "the gun did not out-class the armour" - see
    /// <see cref="Gunnery.Penetration"/> - so this is the same threshold the
    /// impact sound switches on, not a second one beside it.</summary>
    public static bool Penetrating(int level) => level >= 1;

    /// <summary>
    /// Take one more hit on a plate, at <paramref name="along"/> of the way
    /// across it, with a round that goes <paramref name="bite"/> levels deep.
    /// Returns the level of the mark it left, or -1 if this tank has no marks to
    /// leave.
    ///
    /// The bite is the calibre's, and it is the whole of what the calibre means
    /// to the armour: a light round scuffs the paint where a heavy one goes
    /// through on the first try. Without it the level came from the hit count
    /// alone, so every calibre walked a plate scorch -> gouge -> breach at the
    /// same rate and the dial changed nothing but the size of the flash.
    ///
    /// Damage still accumulates on top of that, which is the other half and not
    /// in tension with it: three light rounds reach the same breach one heavy
    /// round does, they just take three goes. What a plate carries is how much
    /// has been done to it, and the calibre only sets how much each shell does.
    /// </summary>
    public int Damage(string face, float along = 0.0f, float up = 0.0f, int bite = 1)
    {
        if (Atlas?.HasScars != true || Atlas.ScarLayer(face) == "")
            return -1;
        if (!_scars.TryGetValue(face, out List<Mark>? marks))
            _scars[face] = marks = new List<Mark>();
        int worn = Math.Min(Wear(face) + Math.Max(bite, 1), Atlas.ScarLevels);
        _wear[face] = worn;
        // The mark shows what this round did, so it is the level the plate has
        // now reached - not one per hit. A heavy round on clean armour leaves a
        // breach and no scorch beneath it: one shell, one hole.
        int level = Math.Clamp(worn - 1, 0, Atlas.ScarLevels - 1);
        if (Penetrating(level))
            Penetrations++;
        marks.Add(new Mark(level, along, up));
        while (marks.Count > Atlas.ScarLevels)
            marks.RemoveAt(0);
        QueueRedraw();
        return level;
    }

    /// <summary>
    /// Take a hit from a round that gets exactly <paramref name="level"/> deep
    /// and no deeper, however many times it lands.
    ///
    /// The other half of the armour model, and the opposite half of
    /// <see cref="Damage"/>: there the round advances the plate by its bite and
    /// the marks accumulate, here the round has a ceiling of its own. Both are
    /// wanted, because they answer for two different kinds of shell. One
    /// conjured by a key knows only the calibre dial, so all it can do is dig;
    /// one fired by a tank knows its class against the class it hit, and the
    /// whole content of that is a ceiling - see
    /// <see cref="Gunnery.Penetration"/>. Under the accumulating rule a light
    /// tank plinking a heavy holes it on the third shot, which is precisely what
    /// the matchup exists to forbid.
    ///
    /// The plate still keeps the worst it has ever taken, from anybody. A light
    /// round onto armour a heavy already breached leaves a scorch and does not
    /// mend the hole - the mark is what *this* shell did, the wear is what the
    /// plate has been through, and they part company exactly here.
    ///
    /// **Every shell leaves its own mark, in its own place.** This refused a
    /// second mark at a level the plate already carried when it was written, on
    /// the argument that two marks of one level are one rendered drawing shown
    /// twice - and that argument was inherited from the accumulating rule, where
    /// it never bit because every shell reached a *new* level. Under a ceiling
    /// the level is constant for a given pair of classes, so the guard meant a
    /// plate recorded the first hit and then nothing, ever. Reported as holes no
    /// longer appearing in different places, which is what it was.
    ///
    /// The economy was never about the drawing anyway: two marks of one level sit
    /// at two different points along the plate, and that reads as two hits
    /// because it is. What bounds the cost is the count - a plate carries as many
    /// marks as there are levels and the oldest drops off, which is unchanged.
    /// </summary>
    public int DamageTo(string face, float along, float up, int level)
    {
        if (Atlas?.HasScars != true || Atlas.ScarLayer(face) == "")
            return -1;
        if (!_scars.TryGetValue(face, out List<Mark>? marks))
            _scars[face] = marks = new List<Mark>();
        int at = Math.Clamp(level, 0, Atlas.ScarLevels - 1);
        _wear[face] = Math.Min(Math.Max(Wear(face), at + 1), Atlas.ScarLevels);
        if (Penetrating(at))
            Penetrations++;
        marks.Add(new Mark(at, along, up));
        while (marks.Count > Atlas.ScarLevels)
            marks.RemoveAt(0);
        QueueRedraw();
        return at;
    }

    /// <summary>Back to a tank off the line.</summary>
    public void Repair()
    {
        _scars.Clear();
        _wear.Clear();
        Penetrations = 0;
        QueueRedraw();
    }

    /// <summary>Whether this tank can show the rendered flash at all.</summary>
    public bool CanRender => Atlas?.HasEffects == true;

    /// <summary>The source actually in use - Rendered falls back to Sheet on a
    /// tank whose scene has no separated barrel.</summary>
    public FlashSource ActiveSource =>
        Source == FlashSource.Rendered && CanRender
            ? FlashSource.Rendered
            : FlashSource.Sheet;

    /// <summary>
    /// Pitch, applied as a shear rather than a rotation.
    ///
    /// The sprite is already projected, so rotating it in screen space is roll,
    /// not pitch. What a small pitch actually does in an isometric view is move
    /// a point at height h forward by about theta*h - a displacement
    /// proportional to height above the ground, which is exactly a shear. Height
    /// above ground is height above the contact point in sprite pixels, and the
    /// forward direction is the hull heading projected onto the ground plane,
    /// unnormalised so that driving into the screen pitches less, as it should.
    /// </summary>
    /// <summary>
    /// How much of the shear's vertical component to keep.
    ///
    /// A real pitch moves a point forward by theta*h AND down by theta*f, where
    /// f is how far forward it sits. A sprite has no depth, so f is
    /// unrecoverable - screen y mixes height and distance - and only the first
    /// term can be modelled. Where the heading runs across the screen that is
    /// fine, because the shear is horizontal and reads as a tilt. Where it runs
    /// into the screen the direction is almost pure vertical, the shear becomes
    /// a vertical squash, and a rigid object that squashes reads as rubber.
    ///
    /// So the vertical part is held down to a quarter. The pitch is then weaker
    /// when driving toward or away from the camera, which is also true of the
    /// real thing: pitch toward the viewer shows mostly as foreshortening of
    /// the top face, which a flat sprite cannot do either.
    /// </summary>
    private const float SquashDamping = 0.25f;

    /// <summary>
    /// Two-plane stabilisation: the turret holds its attitude while the hull
    /// tilts under it.
    ///
    /// It exempts the turret from pitch and roll only. Heave is shared, and has
    /// to be: a bump lifts the whole vehicle, and the turret is bolted to the
    /// ring, so letting it stay put while the hull jumps would part them by a
    /// pixel or two at the seam on every jolt - the one join this whole
    /// pipeline exists to keep tight.
    ///
    /// The engine tremble is exempted along with them, and the seam is what
    /// makes that affordable rather than free: it costs tilt times the ring
    /// height, which at the tremble amplitude is under half a pixel - too small
    /// to part visibly, so the turret can sit still over a trembling hull
    /// without the two coming apart. It also removes the aerial, the tallest
    /// thing on the tank and so the thing with the longest lever, from the
    /// tremble entirely. That is most of what the tremble was.
    ///
    /// The seam still parts a little under the driving tilts, by tilt times the
    /// ring's height above the ground: about a pixel at the amplitudes in use,
    /// which the turret's skirt covers.
    /// </summary>
    public bool TurretStabilised = true;

    /// <summary>
    /// Recoil taken by the turret alone, with the hull standing still. Key L, or
    /// --recoil-turret. Off by default: the shared kick is what a tank does, and
    /// this is the separate reading to compare it against.
    ///
    /// It is not just "the hull skips it", because that would be the wrong
    /// motion. The other tilts pivot about the contact patch, which is right for
    /// them - a hull rocks on its suspension. A gun whose recoil the *mount*
    /// absorbs pivots about the mount, so this one pivots about the ring, and the
    /// ring is where the anchor already is. Two consequences, both wanted:
    /// the seam cannot part, because at the ring the displacement is zero by
    /// construction; and what moves is the top of the turret and the gun, which
    /// is what recoil into a mount looks like.
    ///
    /// Pivoting it about the ground instead would slide the whole turret sprite
    /// some 2.2px across a stationary hull - measurably at the edge of what the
    /// skirt covers, and it reads as the turret coming loose rather than as a gun
    /// firing.
    ///
    /// The impulse is the same either way. A lighter body would really be kicked
    /// harder, but making the modes differ in strength as well as in placement
    /// would mean the A/B measured two things at once - the same reason the two
    /// flash tracks are held to one duration.
    /// </summary>
    public bool RecoilTurretOnly;

    /// <summary>Pitch and roll are the same operation in different directions -
    /// both tilt the body about a horizontal axis through the contact point, so
    /// both displace a point by an amount proportional to its height. Summing
    /// the two directions and shearing once is exact, not an approximation, and
    /// avoids composing two transforms.</summary>
    private Vector2 TiltDisplacement(double pitch, double roll)
    {
        Vector2 displacement =
            Atlas!.GroundDirection(HullFacing) * (float)pitch
            + Atlas.GroundDirection(HullFacing + 90.0) * (float)roll;
        displacement.Y *= SquashDamping;
        return displacement;
    }

    /// <summary>Tilt a layer receives about the contact patch. Exposed so the
    /// stabilisation policy can be asserted rather than read off the
    /// screen.</summary>
    public Vector2 TiltFor(bool turret) =>
        // Recoil first, and outside the stabiliser: a two-plane stabiliser
        // rejects the hull moving under the gun, and cannot reject the gun's own
        // firing impulse - it arrives through the mount as fast as the gun does.
        // A hull rocking under a turret nailed in place would look broken on the
        // one event where the turret is what is being watched.
        // Unless the kick has been given to the turret alone, in which case it is
        // not a body tilt at all and pivots elsewhere - see MountTiltFor.
        (RecoilTurretOnly ? Vector2.Zero : TiltDisplacement(RecoilPitch, RecoilRoll))
        + (turret && TurretStabilised
            ? Vector2.Zero
            : TiltDisplacement(Pitch + TremblePitch, Roll + TrembleRoll));

    /// <summary>Tilt a layer receives about the <em>ring</em> rather than about
    /// the contact patch, which only the turret-only recoil mode produces. Zero
    /// for everything else, so the ordinary path is untouched.</summary>
    public Vector2 MountTiltFor(bool turret) =>
        RecoilTurretOnly && turret
            ? TiltDisplacement(RecoilPitch, RecoilRoll)
            : Vector2.Zero;

    /// <summary>Whether a layer is displaced at all, by either pivot. The draw
    /// gate needs both: in the turret-only mode a stabilised turret standing
    /// still can have no ground tilt whatever while the kick is at its peak, and
    /// gating on the ground tilt alone would have drawn the recoil nowhere.
    /// </summary>
    public bool Sheared(bool turret) =>
        TiltFor(turret) != Vector2.Zero || MountTiltFor(turret) != Vector2.Zero
        || Climbing;

    /// <summary>
    /// The slope the body is drawn on, as an image rotation in radians - see
    /// <see cref="ClimbLean"/> for why a rotation and why it is sprung. Zero on
    /// level ground, and on a board with no relief it is zero forever, which is
    /// what keeps this off every measurement the bench already makes.
    /// </summary>
    public double Climb;

    /// <summary>
    /// The same slope as it reaches a layer <em>printed on</em> the ground: the
    /// two coefficients of the screen map a flat footprint lying in the face
    /// takes, from <see cref="ClimbLean.Print"/>. Zero on level ground.
    ///
    /// Held beside <see cref="Climb"/> rather than derived from it because it
    /// cannot be: the body's rotation vanishes driving straight into the screen
    /// and the plane under it does not. Both come off one sprung plane - see
    /// <see cref="ClimbLean.Slope"/> - which is what keeps a shadow from
    /// disagreeing with the hull standing on it.
    /// </summary>
    public Vector2 Slope;

    /// <summary>
    /// The uniform scale the tank's height is worth: nearer the camera is
    /// bigger, and on this projection height is the only thing that moves a tank
    /// nearer without moving it down the screen.
    ///
    /// <b>Uniform, and that word is the finding.</b> A rigid body drawn larger is
    /// still rigid; one axis scaled against the other is a body that squashes,
    /// and the eye reads that as jelly at a fraction of the amount that reads as
    /// distance. It is also what makes this compose for free - a uniform scale
    /// commutes with the rotation, so both go in one matrix.
    ///
    /// Driven by how high the tank is, not by the slope it is on, so it arrives
    /// over the climb and stays up there. Bound to it the tank breathed: 1.00 at
    /// rest, 0.73 mid-leg, 1.00 again at the top.
    /// </summary>
    public float Rise = 1.0f;

    /// <summary>
    /// How much bigger one level makes a tank.
    ///
    /// Six percent, and the ceiling is not a taste - it is the class spread.
    /// Light, medium and heavy are drawn at 0.85, 1.00 and 1.15, which is what
    /// says a heavy is a heavy; a height that moved a tank a comparable amount
    /// would be a second thing saying it, arguing with the first. Two levels of
    /// climb is 12%, which is under the 15% between one class and the next, so
    /// the two never cross.
    /// </summary>
    public const float RisePerLevel = 0.06f;

    /// <summary>Whether any of the three is doing anything. One test, because
    /// they are one matrix.
    ///
    /// The slope is in it and has to be, or the gate would shut on the one case
    /// the shadow most needs: straight into the screen the body's rotation is
    /// zero, the height may be too - a ramp's own cell is half a level up - and
    /// the footprint is still stretched half again along the view.</summary>
    public bool Climbing =>
        Math.Abs(Climb) > 1e-6 || Math.Abs(Rise - 1.0f) > 1e-6
        || Slope != Vector2.Zero;

    /// <summary>Heave a layer receives. Never depends on the layer - see
    /// <see cref="TurretStabilised"/>.</summary>
    public int HeaveFor(bool turret) => Shake;

    /// <summary>
    /// The one shear a layer is drawn through, for both pivots at once.
    ///
    /// A point h above the anchor is displaced by <c>ground * (h + groundY)</c>
    /// by a body tilt and by <c>mount * h</c> by a kick about the ring. That sum
    /// is still linear in h, so it is still a single shear: slope is the total,
    /// and only the constant term stays on the ground tilt. Composing two
    /// transforms would give the same answer and resample twice.
    /// </summary>
    internal Transform2D ShearFor(bool turret, bool grounded = false)
    {
        float groundY = Atlas!.GroundOffset.Y;
        Vector2 ground = TiltFor(turret);
        Vector2 both = ground + MountTiltFor(turret);
        var shear = new Transform2D(
            new Vector2(1.0f, 0.0f),                            // x is untouched
            new Vector2(-both.X, 1.0f - both.Y),
            new Vector2(groundY * ground.X, groundY * ground.Y));
        if (!Climbing)
            return shear;
        // Both branches turn about the contact patch - the pivot every tilt in
        // the project uses, because the ground a tank stands on does not move -
        // and both compose outside the shear, so they carry the shear's result
        // round rather than the two fighting over the vertical. One matrix rather
        // than two transforms, which would resample the sprite twice.
        Vector2 foot = Atlas.GroundOffset;
        Transform2D climb;
        if (grounded)
        {
            // The contact shadow is printed on the ground, so it is not turned by
            // the slope - it is foreshortened by it, which is a different
            // transform and, for a flat patch under an orthographic camera, an
            // exact one. See ClimbLean.Print for the arithmetic and for why this
            // is the layer that gets the true answer while the body gets a
            // rotation. Fourteen degrees of rotation on a shadow is a shadow that
            // has come off the ground it is a shadow of; the bumps stay in,
            // because two pixels of shear reads as the tank pressing into it.
            //
            // The rise is in, and its absence was a fault waiting: everything at
            // this height is nearer the camera, the ground it stands on included,
            // so a shadow left unscaled is six percent small per level and the
            // hull overhangs it - which is the very failure the flag was written
            // to prevent, arriving from the other side.
            climb = new Transform2D(new Vector2(Rise, -Slope.X * Rise),
                                    new Vector2(0.0f, (1.0f + Slope.Y) * Rise),
                                    Vector2.Zero);
        }
        else
        {
            float c = Mathf.Cos((float)Climb) * Rise;
            float s = Mathf.Sin((float)Climb) * Rise;
            climb = new Transform2D(new Vector2(c, s), new Vector2(-s, c),
                                    Vector2.Zero);
        }
        climb.Origin = foot - climb.BasisXform(foot);
        return climb * shear;
    }

    public override void _Ready()
    {
        // These are 3D renders, not pixel art, so linear costs nothing at 1:1 -
        // sample centres line up and it is bit-identical to nearest. It only
        // matters once the pitch shear resamples: with nearest, whole pixel rows
        // snap sideways one at a time as the shear changes, and that shimmer
        // reads as wobble on top of whatever the pitch is doing.
        TextureFilter = TextureFilterEnum.Linear;

        // The hull is drawn by this node itself, so the char material goes here
        // and the tank's other layers take it by asking for their parent's - see
        // EffectLayer.Armour. Everything else keeps its own, which is what stops
        // the additive layers being darkened along with the paint.
        _char = new ShaderMaterial { Shader = CharShader };
        _char.SetShaderParameter("burn", 0.0);
        Material = _char;

        // Children draw after the parent and in order, so adding them in
        // LayerOrder is what puts them on screen in it. See there.
        foreach (string name in LayerOrder)
        {
            EffectLayer layer = MakeLayer(name);
            layer.Tank = this;
            AddChild(layer);
            // The painted sheet goes straight after the turret, which is where
            // it was when the turret was a parent draw. See SheetFlash.
            if (name == "turret")
                AddChild(new SheetFlash { Tank = this });
        }
    }

    public override void _Draw()
    {
        if (Atlas is null)
            return;
        bool tilted = Math.Abs(Pitch) > 1e-6 || Math.Abs(Roll) > 1e-6
                      || Math.Abs(TremblePitch) > 1e-6 || Math.Abs(TrembleRoll) > 1e-6
                      || Math.Abs(RecoilPitch) > 1e-6 || Math.Abs(RecoilRoll) > 1e-6
                      || Climbing;
        if (ShowHull)
            DrawLayerTilted("hull", HullFacing, tilted, false);
        // The turret is not drawn here - it is a child layer, so that it lands
        // after the belts rather than before them. See LayerOrder.
        // Neither flash is drawn from here any more: the rendered one is a child
        // layer because it needs its own blend mode, and the painted sheet is a
        // child because it has to land over the turret. See SheetFlash.
        if (ShowAxis)
        {
            var colour = new Color(1.0f, 0.2f, 0.2f, 0.9f);
            const float arm = 10.0f;
            DrawLine(new Vector2(-arm, 0), new Vector2(arm, 0), colour);
            DrawLine(new Vector2(0, -arm), new Vector2(0, arm), colour);
        }
    }

    private void DrawLayerTilted(string layer, double facing, bool tilted, bool turret)
    {
        bool shear = tilted && Sheared(turret);
        if (shear)
            DrawSetTransformMatrix(ShearFor(turret));
        DrawLayer(layer, facing);
        if (shear)
            DrawSetTransformMatrix(Transform2D.Identity);
    }

    /// <summary>
    /// The flash, on the muzzle, pointing where the gun does.
    ///
    /// The direction comes straight from <c>GroundDirection</c>, unnormalised,
    /// and that is the whole trick. Its length is 1 across the screen and
    /// sin(elevation) - a half here - straight into it, which is exactly how
    /// much of a horizontal thing survives the projection. Rotating without it
    /// gives a gun firing away from the camera a full-length flash standing up
    /// over the turret like a bonfire.
    ///
    /// Only the length is foreshortened. Across the barrel the flash is roughly
    /// round, and its cross-section projects to an ellipse that depends on how
    /// much of the puff is vertical - which the sprite does not know. At flash
    /// scale, and for a tenth of a second, that is not worth modelling.
    ///
    /// It rides the turret's transform, not the hull's: the flash is fixed to
    /// the gun, so whatever the gun is doing it does too.
    /// </summary>
    /// <summary>
    /// The painted flash sheet, and it is a node for the same reason the turret
    /// is: it has to land <em>over</em> the turret, and the turret is a child
    /// now. Drawn from the parent it would go under one.
    ///
    /// It is added immediately after the turret, so where it sits relative to
    /// the turret is what it always was.
    /// </summary>
    private sealed partial class SheetFlash : Node2D
    {
        public TankSprite Tank = null!;

        public override void _Ready() => TextureFilter = TextureFilterEnum.Linear;

        // Asked for every frame while there is one, and for one frame after it
        // goes: the same reason the effect layers do it. A node that stops
        // marking itself dirty never gets the chance to draw nothing, and the
        // last frame of the flash would stay on screen for good.
        public override void _Process(double _)
        {
            bool want = Tank.ActiveSource == FlashSource.Sheet && Tank.FlashFrame >= 0;
            if (want || _drawn)
                QueueRedraw();
        }

        private bool _drawn;

        public override void _Draw()
        {
            FlashSheet? sheet = Tank.Flash;
            AtlasSet? atlas = Tank.Atlas;
            _drawn = Tank.ActiveSource == FlashSource.Sheet && Tank.FlashFrame >= 0
                     && sheet?.Texture is not null && atlas is not null;
            if (!_drawn)
                return;
            Vector2 along = atlas!.GroundDirection(Tank.TurretFacing);
            Vector2 across = new Vector2(-along.Y, along.X).Normalized();
            Vector2 muzzle = atlas.Muzzle(atlas.FrameFor(Tank.TurretFacing))
                             - atlas.Anchor + new Vector2(0.0f, Tank.HeaveFor(true));
            var place = new Transform2D(along * Tank.FlashScale,
                                        across * Tank.FlashScale, muzzle);
            DrawSetTransformMatrix(Tank.ShearFor(true) * place);
            DrawTextureRectRegion(sheet!.Texture,
                new Rect2(-sheet.Origin, sheet.Tile), sheet.Region(Tank.FlashFrame));
            DrawSetTransformMatrix(Transform2D.Identity);
        }
    }

    private void DrawLayer(string layer, double facing)
    {
        int index = Atlas!.FrameFor(facing);
        // Frames are stored trimmed to what they draw, so the quad is the
        // frame's own box moved by whatever came off the tile's top-left. An
        // untrimmed set reports no offset and the whole tile, which is what it
        // always was.
        Vector2 size = Atlas.SizeOf(layer, index);
        if (size.X <= 0.0f || size.Y <= 0.0f)
            return;
        DrawTextureRectRegion(Atlas.Texture(layer),
            new Rect2(-Atlas.Anchor + Atlas.OffsetOf(layer, index)
                      + new Vector2(0.0f, Shake), size),
            Atlas.Region(layer, index));
    }

    public Vector2I FrameIndices() => Atlas is null
        ? Vector2I.Zero
        : new Vector2I(Atlas.FrameFor(HullFacing), Atlas.FrameFor(TurretFacing));
}
