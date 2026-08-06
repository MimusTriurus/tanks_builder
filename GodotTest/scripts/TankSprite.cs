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
    /// Locked: the turret holds its world heading while the hull turns under
    /// it - a turret staying on target through a manoeuvre.
    /// Unlocked: the turret is rigid with the hull and swings with it.
    /// </summary>
    public bool TurretLocked = true;

    /// <summary>The only place hull heading changes, so the turret modes cannot
    /// be bypassed by some other code path - driving and manual turns both come
    /// through here.</summary>
    public void TurnHull(double delta)
    {
        if (Math.Abs(delta) < 1e-9)
            return;
        HullFacing = Mod(HullFacing + delta, 360.0);
        if (!TurretLocked)
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

    /// <summary>Phases of the burning wreck's two loops, or -1 when it is not
    /// burning. Two numbers rather than one because the flame and the column run
    /// at different rates over the same rendered phases - see
    /// <see cref="BurnLoop"/>.</summary>
    public int FirePhase = -1;
    public int BurnPhase = -1;

    /// <summary>Phase of a shell arriving, or -1 between hits. An event like the
    /// shot, so it runs out rather than wrapping - see <see cref="HitLoop"/>.</summary>
    public int HitPhase = -1;

    /// <summary>Where the belts are in their cycle, or -1 with no belts to show.
    /// Driven by <see cref="TrackLoop"/> off distance travelled, not off a
    /// timer: the belt is what the ground is winding against.</summary>
    public int TrackPhase = -1;

    /// <summary>How much of the belts is showing as a smear rather than as
    /// links: 0 crisp, 1 nothing but the phase average. From
    /// <see cref="TrackLoop.Blur"/>; the layer pulls it at draw time.</summary>
    public double TrackBlur;

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
    public static readonly string[] LayerOrder =
        AtlasSet.TrackNames.Concat(new[] { "turret", AtlasSet.BarrelName })
            .Concat(AtlasSet.ScarNames).Concat(new[]
        {
            AtlasSet.ExhaustName, AtlasSet.BurnName, AtlasSet.FireName,
            "smoke", "flash", AtlasSet.DustName, AtlasSet.BurstName,
        }).ToArray();

    /// <summary>Which kind of layer each name is. Kept beside
    /// <see cref="LayerOrder"/> so there is one list, not a list and a
    /// hand-written sequence that has to agree with it.</summary>
    private static EffectLayer MakeLayer(string layer) => layer switch
    {
        AtlasSet.ExhaustName => EffectLayer.Exhaust(layer),
        AtlasSet.BurnName => EffectLayer.BurningSmoke(layer),
        AtlasSet.FireName => EffectLayer.BurningFire(layer),
        AtlasSet.DustName => EffectLayer.HitDust(layer),
        AtlasSet.BurstName => EffectLayer.HitBurst(layer),
        "turret" => EffectLayer.Turret(layer),
        AtlasSet.BarrelName => EffectLayer.Barrel(layer),
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
    /// <summary>One round's worth of damage: how bad, and where along the plate
    /// it landed as a fraction of the plate's half-width.
    ///
    /// A fraction rather than a pixel offset, because the plate's screen
    /// tangent turns with the hull: stored in pixels it would be right at the
    /// heading it was taken on and wrong at the other eleven. The mark's *base*
    /// position needs no storing at all - the layer was rendered on its plate
    /// at every heading, so the centroid is already where it belongs, and this
    /// only says how far along from it.
    /// </summary>
    public readonly record struct Mark(int Level, float Along);

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

    /// <summary>Where a mark is drawn, in pixels from the anchor.</summary>
    public Vector2 MarkOffset(string face, Mark mark) =>
        Atlas is null ? Vector2.Zero : Atlas.HitTangent(face, HullFacing) * mark.Along;

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
    public int Damage(string face, float along = 0.0f, int bite = 1)
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
        marks.Add(new Mark(level, along));
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
    public int DamageTo(string face, float along, int level)
    {
        if (Atlas?.HasScars != true || Atlas.ScarLayer(face) == "")
            return -1;
        if (!_scars.TryGetValue(face, out List<Mark>? marks))
            _scars[face] = marks = new List<Mark>();
        int at = Math.Clamp(level, 0, Atlas.ScarLevels - 1);
        _wear[face] = Math.Min(Math.Max(Wear(face), at + 1), Atlas.ScarLevels);
        marks.Add(new Mark(at, along));
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
        TiltFor(turret) != Vector2.Zero || MountTiltFor(turret) != Vector2.Zero;

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
    internal Transform2D ShearFor(bool turret)
    {
        float groundY = Atlas!.GroundOffset.Y;
        Vector2 ground = TiltFor(turret);
        Vector2 both = ground + MountTiltFor(turret);
        return new Transform2D(
            new Vector2(1.0f, 0.0f),                            // x is untouched
            new Vector2(-both.X, 1.0f - both.Y),
            new Vector2(groundY * ground.X, groundY * ground.Y));
    }

    public override void _Ready()
    {
        // These are 3D renders, not pixel art, so linear costs nothing at 1:1 -
        // sample centres line up and it is bit-identical to nearest. It only
        // matters once the pitch shear resamples: with nearest, whole pixel rows
        // snap sideways one at a time as the shear changes, and that shimmer
        // reads as wobble on top of whatever the pitch is doing.
        TextureFilter = TextureFilterEnum.Linear;

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
                      || Math.Abs(RecoilPitch) > 1e-6 || Math.Abs(RecoilRoll) > 1e-6;
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
        DrawTextureRectRegion(Atlas.Texture(layer),
            new Rect2(-Atlas.Anchor + new Vector2(0.0f, Shake), Atlas.Tile),
            Atlas.Region(layer, index));
    }

    public Vector2I FrameIndices() => Atlas is null
        ? Vector2I.Zero
        : new Vector2I(Atlas.FrameFor(HullFacing), Atlas.FrameFor(TurretFacing));
}
