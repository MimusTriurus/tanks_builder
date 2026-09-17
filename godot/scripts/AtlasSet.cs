using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// One tank's rendered output - the hull, turret and hex layers that
/// sprite_atlas.py wrote into Sprites/&lt;TAG&gt;/.
///
/// Loaded from an absolute path at run time instead of being imported as
/// project resources. Re-render the atlases in Blender and the next run picks
/// them up, with no copying and no re-import step; for a harness whose whole
/// job is to look at freshly rendered sprites that matters more than export
/// portability (Image.LoadFromFile does not work from a packed export).
/// </summary>
public sealed class AtlasSet
{
    /// <summary>The layers without which there is no tank: the hull and the tile
    /// it stands on. A set missing either fails to load and says so.</summary>
    /// <remarks>
    /// <b>The turret came out of this list</b>, and not as a relaxation: a
    /// vehicle with no ring has no turret layer, because its mount is welded to
    /// the hull and is rendered in the hull layer. Requiring one would refuse to
    /// load the two casemates in this project - and the alternative, rendering a
    /// box as a turret and spinning it, is what this replaced. It is optional
    /// rather than absent, so a turreted set still loads it and
    /// <see cref="HasTurretLayer"/> is the question to ask about it.
    /// </remarks>
    public static readonly string[] LayerNames = { "hull", "hex" };

    /// <summary>
    /// Effect layers, if the scene had them: smoke first, then fire.
    ///
    /// Optional because they need a `Barrel` separated by hand in the .blend.
    /// All three tanks on disk have one now, but the option is not vestigial: a
    /// tank without them still loads and still drives, and the harness falls back
    /// to the painted sheet, which is also what makes the two comparable side by
    /// side.
    ///
    /// The order is the draw order, and it is not arbitrary: smoke occludes and
    /// goes on with normal alpha, fire emits and goes on additively, so fire is
    /// last and on top.
    /// </summary>
    public static readonly string[] EffectNames = { "smoke", "flash" };

    /// <summary>
    /// The engine plume, which is an effect layer and is not one of those.
    ///
    /// Kept out of <see cref="EffectNames"/> deliberately: that array is the
    /// shot's pair, and <see cref="HasEffects"/> - which decides whether the
    /// rendered flash can be shown at all - requires every name in it. Adding
    /// the exhaust there would switch the rendered flash off on any tank that
    /// had a flash but no separated Engine, which is a different question
    /// answered wrongly.
    ///
    /// It also differs in both of the things a layer is indexed by. It follows
    /// the *hull*, because the outlet is bolted to the engine deck, where the
    /// flash follows the turret; and it loops rather than running once, so it
    /// has its own clock in <see cref="ExhaustLoop"/> instead of a table of
    /// held frames.
    /// </summary>
    public const string ExhaustName = "exhaust";

    /// <summary>
    /// The burning wreck: a red flame at the engine deck and a black column
    /// above it.
    ///
    /// Two layers for the reason the shot is two - the flame emits and is added,
    /// the column occludes and is not - and kept out of
    /// <see cref="EffectNames"/> for the reason the exhaust is.
    ///
    /// Both follow the *hull*, like the exhaust and unlike the flash: all three
    /// are built on the same stamped ports, and those are bolted to the engine
    /// deck.
    /// </summary>
    public const string FireName = "fire";
    public const string BurnName = "burn";

    /// <summary>
    /// A shell arriving: the burst and the dust under it.
    ///
    /// Alone among the layers these are rendered with *no headings at all* -
    /// one column of phases - because a hit is not welded to the tank the way a
    /// muzzle flash is welded to a gun, and a ball of light looks the same from
    /// every azimuth. The game puts it where the hit was, off the plate table
    /// the renderer stamps into the same JSON. That is also why
    /// <see cref="CountOf"/> exists.
    ///
    /// Kept out of <see cref="EffectNames"/> for the reason the exhaust is, and
    /// required in pairs for the reason the burning halves are: the dust is not
    /// decoration, it is what the additive burst is composited against.
    /// </summary>
    public const string BurstName = "burst";
    public const string DustName = "dust";

    /// <summary>
    /// The mark a shell leaves, one layer per plate.
    ///
    /// The opposite of the burst in every way that matters, and worth stating
    /// as such. It is welded to the tank, so it is rendered at every heading
    /// and drawn at the shared anchor with no offset - it is not
    /// <see cref="EffectLayer.Placed"/>. And its phases are not time: they are
    /// how much damage the plate has taken, so nothing steps them and there is
    /// no clock. A plate holds its level until something changes it.
    ///
    /// Because it turns with the hull, the renderer could hold the tank out of
    /// it, which is why nothing here decides front-or-behind: a mark on a plate
    /// facing away is simply an empty frame.
    /// </summary>
    public static readonly string[] ScarNames =
        { "scar_front", "scar_rear", "scar_left", "scar_right" };

    /// <summary>
    /// The two track belts, drawn as layers of their own so they can wind while
    /// the hull they are bolted to does not.
    ///
    /// Both required, for the burning pair's reason and more bluntly: one belt
    /// running and the other standing still is not half the effect, it is a
    /// broken tank.
    ///
    /// Kept out of <see cref="EffectNames"/> like everything else that is not
    /// the shot's pair, but these are not effects at all - they are part of the
    /// vehicle, welded to the hull, and they follow its heading for the same
    /// reason the exhaust does. What makes them a layer is only that they move
    /// relative to the hull, which is exactly what a single hull sprite cannot
    /// show.
    /// </summary>
    public static readonly string[] TrackNames = { "track_left", "track_right" };

    /// <summary>
    /// The gun tube, which recoils into the turret when the gun fires.
    ///
    /// Not an effect either, for the belts' reason and even more plainly: it is
    /// the gun. It became a layer because the turret gave it up so that it could
    /// move, and it moves relative to the turret - which is the one thing a
    /// single turret sprite cannot show.
    ///
    /// So it follows the *turret's* heading, unlike the belts and the exhaust,
    /// and unlike them it is on an event rather than a cycle. Kept out of
    /// <see cref="EffectNames"/> like everything that is not the shot's pair.
    ///
    /// Optional, because it needs a `Barrel` separated by hand and five phases
    /// rendered off it. A tank without it simply has its tube back inside
    /// `turret_atlas`, drawn as part of the turret and never moving - which is
    /// what every tank looked like before this.
    /// </summary>
    public const string BarrelName = "barrel";

    /// <summary>
    /// The dark the tank puts on the tile it stands on.
    ///
    /// A contact shadow rather than a cast one, so it has no direction and no
    /// turret in it - see ground_shadow.py for why both follow from the other.
    /// It is on the hull's heading because that is the footprint that turns.
    ///
    /// The one layer that is neither the tank nor an effect on it: it is the
    /// ground, darker. Which is why it does not composite with the rest at all
    /// but goes under every tank on the board, at the selection ring's own
    /// z-index - see <see cref="TankSprite.ShadowZ"/>.
    ///
    /// Optional like the rest, and its absence is a fact about the render
    /// rather than about the scene: every tank has ground under it.
    /// </summary>
    public const string ShadowName = "shadow";

    /// <summary>
    /// How high the surface under every pixel of the hull and the belts stands,
    /// a byte apiece - see sprite_height.py, which renders it.
    ///
    /// It exists because <see cref="Groundline"/> answers a different question
    /// and was being asked this one. The groundline is where a tank meets the
    /// bottom; lifting it by the depth of the water gives the waterline only if
    /// the pixel that projects lowest in a column sits at the same distance
    /// from the camera as the surface the water plane crosses there. Measured
    /// against the geometry on LT_PARTS, over every column of four headings:
    /// the drawn line stood 3.6 to 15.2px above the true one on the median
    /// column and up to 33.8px out at worst, on 34px of water. One-way, so a
    /// tank read as less sunk than it was - and it wore the shape of its own
    /// underside at the waterline, which is where the six-pixel step at each
    /// track guard came from.
    ///
    /// Not drawn, ever. It is a table that happens to have been rendered, and
    /// what reads it is <see cref="Waterline"/>.
    /// </summary>
    public const string HeightName = "height";

    /// <summary>
    /// The knocked-out tank: a canted turret with its gun dropped, and two belts
    /// gone slack. Not effects and not a separate tank - the same layers in
    /// another pose, standing in for the live ones once the hull is dead.
    ///
    /// **There is no wreck hull, and that is the whole shape of this.** A dead
    /// tank's hull geometry is a live tank's hull geometry, so `hull_atlas` is
    /// already the wreck's to the pixel; the same goes for the contact shadow
    /// (zenith light, and the turret's footprint falls inside the hull's) and for
    /// the scars, which sit on plates that have not moved. Only what actually
    /// changed shape is re-rendered.
    ///
    /// The turret stays a layer of its own rather than being baked into one wreck
    /// sprite, and the reason is the moment of death: a tank dies with its gun at
    /// whatever bearing it had, so a baked hull-to-turret angle would snap the
    /// gun round the instant it was hit. See wreck_pose.py.
    /// </summary>
    public const string WreckTurretName = "wreck_turret";

    /// <summary>
    /// The other shape of that answer: the gun dropped to its stop, on a vehicle
    /// whose mount is welded to the hull.
    ///
    /// <b>A casemate does not lose its box.</b> The mount is part of the hull, so
    /// it is rendered *in* the hull layer and drawn on a dead tank exactly as on
    /// a live one - it chars with the hull and burns with it. What changes shape
    /// when such a vehicle dies is the gun alone, so this layer is the whole of
    /// its wreck pose, and the set carries no <see cref="WreckTurretName"/> at
    /// all. See wreck_pose.barrel_layer.
    ///
    /// Exactly one of the two is ever on disk, which is what
    /// <see cref="WreckMount"/> is for.
    /// </summary>
    public const string WreckBarrelName = "wreck_barrel";

    public static readonly string[] WreckTrackNames =
        { "wreck_track_left", "wreck_track_right" };

    /// <summary>
    /// Layers ordered against the turret rather than held out of it, and so the
    /// ones <see cref="OverTurret"/> speaks for.
    /// </summary>
    /// <remarks>
    /// These are indexed by the hull's heading while the turret traverses
    /// against the hull, so a turret cut into their alpha is cut at one relative
    /// bearing out of twenty-four and the atlas has no axis to vary it along.
    /// Turn the gun and the bite stays where the turret used to be. The fix is
    /// not a better render, it is to draw the turret whole and put these on the
    /// correct side of it - which is a property of the heading, so the harness
    /// can do it. See draw_order.py.
    ///
    /// It also retired a pair of wreck twins built for the same symptom: a
    /// turret canted 24 degrees is just another bearing the baked bite is wrong
    /// for, and ordering covers the wreck's cant and the live tank's traverse
    /// with one rule.
    /// </remarks>
    public static readonly string[] OrderedNames =
        new[] { ExhaustName, FireName, BurnName }.Concat(ScarNames).ToArray();

    /// <summary>Layers that load if they are there and are silently skipped if
    /// they are not. All but the hit layers need a piece separated by hand in
    /// the .blend; the hit is measured off the hull and so is always there.</summary>
    private static readonly string[] OptionalNames =
        new[] { "smoke", "flash", ExhaustName, FireName, BurnName, BurstName,
                DustName, BarrelName, ShadowName, HeightName, WreckTurretName,
                WreckBarrelName, "turret" }
            .Concat(ScarNames).Concat(TrackNames).Concat(WreckTrackNames).ToArray();

    public string Tag { get; private set; } = "";
    public string Error { get; private set; } = "";

    public Vector2I Tile { get; private set; } = new(256, 256);
    public int Count { get; private set; } = 12;
    public double UnitsPerPixel { get; private set; }

    /// <summary>
    /// Where spin_pivot lands inside a tile, in pixels from its top-left
    /// corner. Every layer shares it - that is what lets the layers be drawn
    /// straight on top of each other - so drawing a tile at
    /// (position - Anchor) puts the tank's rotation axis exactly on
    /// <c>position</c>.
    /// </summary>
    public Vector2 Anchor { get; private set; } = new(128, 128);

    /// <summary>
    /// Opaque bounds of the hex tile. Grid spacing is derived from this rather
    /// than read from metadata, so the field re-tessellates correctly if the
    /// tile is ever re-rendered at a different size. See <see cref="HexField"/>.
    /// </summary>
    public Rect2I HexRect { get; private set; }

    /// <summary>Camera elevation the atlas was rendered at, degrees. Needed to
    /// turn a world heading into a screen direction, and read from the file
    /// rather than assumed so a re-render at another angle still works.</summary>
    public double Elevation { get; private set; } = 30.0;

    /// <summary>Offset from the shared anchor down to the ground plane, in
    /// pixels. The anchor is the turret's rotation axis, which the renderer
    /// places at the mid-height of the fitted bounds, so it floats well above
    /// the ground the tank stands on - about 61px on these atlases. Anything
    /// that needs the contact point rather than the pivot goes through here.</summary>
    public Vector2 GroundOffset => HexRect.Position + (Vector2)HexRect.Size * 0.5f - Anchor;

    /// <summary>
    /// How tall a tank is drawn above the ground it stands on, in board px.
    ///
    /// <b>Twice the anchor's float, which is the atlas's own statement rather than
    /// a measurement of pixels.</b> The anchor is the turret axis raised to the
    /// middle of the fitted bounds' height, so what stands above the contact point
    /// is about twice the distance from that point up to it - see
    /// <see cref="GroundOffset"/>.
    ///
    /// Here rather than at its one caller because it is a fact about the atlas and
    /// because the check has to be able to ask the same question: a threshold on
    /// how much of a tank goes under water, judged against a height the checker
    /// worked out for itself, is a threshold with two definitions.
    /// </summary>
    public float DrawnHeight => 2.0f * GroundOffset.Y;

    /// <summary>Screen-space direction of a world heading on the ground plane.
    /// Not normalised: the isometric view squashes the ground by
    /// sin(elevation), and anything moving along it should shrink to match.</summary>
    public Vector2 GroundDirection(double headingDegrees)
    {
        double rad = Mathf.DegToRad(headingDegrees);
        double squash = Math.Sin(Mathf.DegToRad(Elevation));
        return new Vector2((float)Math.Cos(rad), (float)(-Math.Sin(rad) * squash));
    }

    /// <summary>
    /// Where the engine breathes, in the model's own world units: the point on
    /// the deck, the direction the vent faces, and how wide it is.
    ///
    /// World rather than pixels, which is the opposite of the plate table beside
    /// it and for the other half of the same reason. A plate's screen offset is
    /// a *result* of fitting the camera, so working it out anywhere but between
    /// the render and the check would be a second guess at it. A port is a fact
    /// about the tank and does not move when the camera does, and this end
    /// already holds the two numbers that turn world into pixels - see
    /// <see cref="Project"/>. Stamped in pixels it would freeze a projection
    /// that can be done exactly, and freeze it per heading.
    ///
    /// Read by the procedural column and by nothing else. A set without the
    /// block reports none, which is what makes the rendered layer the fallback
    /// rather than an error: every atlas shipped before <c>stamp_ports.py</c>
    /// existed is such a set.
    /// </summary>
    public readonly record struct Port(Vector3 Point, Vector3 Dir, float Radius);

    private readonly List<Port> _ports = new();

    public IReadOnlyList<Port> Ports => _ports;

    /// <summary>The hull's longest world axis - what every length in the smoke
    /// and the flame is quoted against, the way <c>engine_fire</c> quotes them.
    /// Zero where no port block arrived.</summary>
    public double HullLength { get; private set; }

    /// <summary>Whether this set can build its burning pair itself.</summary>
    public bool HasPorts => _ports.Count > 0 && HullLength > 0.0
                            && UnitsPerPixel > 0.0;

    /// <summary>
    /// The gun's bore, in world units - where a shot leaves, which way, and how
    /// wide the tube is there. Stamped onto the two shot layers by
    /// <c>stamp_bore.py</c>.
    ///
    /// <b>The radius is the point of it.</b> Every number the flash and its
    /// smoke are made of is quoted in bore radii - which is what lets one
    /// <c>muzzle_flash.CONFIG</c> fit three tanks - and the bore is nowhere in
    /// the frames, which carry pixels and the view and nothing about the gun the
    /// pixels are of. Same argument as <see cref="Ports"/>, other end of the
    /// tank.
    ///
    /// <b>The point is here for its height and for how far it moves, never for
    /// where it is.</b> Where the muzzle is drawn comes from
    /// <see cref="Muzzle(int)"/>, which is measured off the barrel layer per
    /// heading and is therefore exact; projecting this point instead would carry
    /// <see cref="Project"/>'s rotation about the origin, where the render spins
    /// about the stamped ring axis - up to 3.3px on <c>LTP</c>. What the
    /// projection cannot give and this can is <c>z</c>, which is what the height
    /// map is compared against - and the <em>difference</em> between this point
    /// level and laid, which is what the measured mouth has to be moved by when
    /// the tube goes up. That error cancels in a difference, and
    /// <see cref="Muzzle(double, int)"/> is the whole of that argument.
    ///
    /// A set without the block reports none, which leaves the rendered flash the
    /// fallback rather than an error.
    /// </summary>
    public Port Bore { get; private set; }

    /// <summary>Whether this set can build its shot itself.</summary>
    public bool HasBore => Bore.Radius > 0.0f && Bore.Dir.LengthSquared() > 0.0f
                           && UnitsPerPixel > 0.0;

    /// <summary>
    /// A point or a vector of the model's own frame, in pixels off the shared
    /// anchor, as the hull would be drawn at <paramref name="headingDegrees"/>.
    ///
    /// <b>Said through <see cref="GroundDirection"/> rather than as a formula of
    /// its own</b>, because the squash is already stated there and a second
    /// statement of it is the way two layers come apart. The ground half is that
    /// call verbatim; the height half is one cosine, the same one
    /// <see cref="HeightSpanPx"/> uses to turn a world height into screen rows.
    ///
    /// <b>The ninety degrees is the front being -Y</b>, which is the import
    /// convention and not a measurement - so it is asserted rather than trusted:
    /// the self-test projects the stamped port and requires it to land inside
    /// the rendered column's own silhouette at every heading, which is what a
    /// quarter turn of error looks like from the outside.
    /// </summary>
    public Vector2 Project(Vector3 world, double headingDegrees)
    {
        double bearing = Mathf.RadToDeg(Math.Atan2(world.Y, world.X));
        float reach = MathF.Sqrt(world.X * world.X + world.Y * world.Y);
        Vector2 flat = GroundDirection(bearing + headingDegrees + 90.0) * reach;
        float up = world.Z * (float)Math.Cos(Mathf.DegToRad(Elevation));
        return new Vector2(flat.X, flat.Y - up) / (float)UnitsPerPixel;
    }

    private readonly Dictionary<string, ImageTexture> _textures = new();
    private readonly Dictionary<string, int> _columns = new();

    /// <summary>
    /// Per-layer frame size and anchor.
    ///
    /// These used to be one pair for the whole set, taken off the hull, which
    /// was true until the effect layers arrived: they are rendered at a wider
    /// tile so a flash longer than the tank's half-tile is not cut off at the
    /// frame edge. The camera widens with the tile, so `units_per_pixel` is
    /// identical and the layers still composite - but only if each is drawn at
    /// *its own* anchor. Drawing a 384 tile at the hull's 256 anchor puts the
    /// flash 64px off in both directions.
    /// </summary>
    private readonly Dictionary<string, Vector2I> _tiles = new();
    private readonly Dictionary<string, Vector2> _anchors = new();
    private readonly Dictionary<string, int> _phases = new();

    /// <summary>The barrel layer's ladder of laying angles, empty when the set
    /// was rendered with a level gun. Held per layer for the same reason the
    /// phase count is: it belongs to the layer that has it.</summary>
    private readonly Dictionary<string, double[]> _elev = new();

    /// <summary>
    /// Headings a layer was rendered at, which until the hit arrived was the
    /// same number for every layer and so was read off the hull.
    ///
    /// The hit burst breaks that: it is not welded to the tank, so it renders
    /// once with no headings at all and the game puts it where the hit was.
    /// Its count is 1, and indexing it against the hull's 12 would run straight
    /// off the end of a ten-frame atlas.
    /// </summary>
    private readonly Dictionary<string, int> _counts = new();

    /// <summary>Frame size of a layer. Falls back to the tank's tile for a
    /// layer that is not present.</summary>
    public Vector2I TileOf(string layer) =>
        _tiles.TryGetValue(layer, out Vector2I tile) ? tile : Tile;

    /// <summary>Anchor of a layer, in that layer's own pixels.</summary>
    public Vector2 AnchorOf(string layer) =>
        _anchors.TryGetValue(layer, out Vector2 anchor) ? anchor : Anchor;

    /// <summary>How many time steps an effect layer holds, 0 if absent.</summary>
    public int PhasesOf(string layer) =>
        _phases.TryGetValue(layer, out int phases) ? phases : 0;

    public bool Has(string layer) => _textures.ContainsKey(layer);

    /// <summary>True when this tank was rendered with the effect layers.</summary>
    public bool HasEffects
    {
        get
        {
            foreach (string layer in EffectNames)
                if (!Has(layer))
                    return false;
            return true;
        }
    }

    /// <summary>Phases shared by the effect layers, 0 if they are missing.</summary>
    public int EffectPhases => HasEffects ? PhasesOf(EffectNames[0]) : 0;

    /// <summary>True when this tank was rendered with an engine plume.</summary>
    public bool HasExhaust => Has(ExhaustName);

    /// <summary>True when this tank was rendered with ground under it.</summary>
    public bool HasShadow => Has(ShadowName);

    /// <summary>Phases in the exhaust loop, 0 if it is missing.</summary>
    public int ExhaustPhases => PhasesOf(ExhaustName);

    /// <summary>True when this tank was rendered burning. Both halves are
    /// required, and that is not tidiness: the column is not decoration, it is
    /// what the additive flame is composited against, and the flame alone on the
    /// game's pale field washes out to nothing much.</summary>
    public bool HasBurning => Has(FireName) && Has(BurnName);

    /// <summary>Phases in the burning loop, 0 if it is missing. Both layers
    /// share the count - one event, one render job.</summary>
    public int BurnPhases => HasBurning ? PhasesOf(FireName) : 0;

    /// <summary>True when this tank was rendered with the hit pair *and* the
    /// plate table that says where to put it. Both halves, for the burning
    /// pair's reason: an additive burst with nothing dark under it is the
    /// field, faintly tinted.</summary>
    public bool HasHit => Has(BurstName) && Has(DustName) && _plates.Count > 0;

    /// <summary>Phases in a hit, 0 if it is missing.</summary>
    public int HitPhases => HasHit ? PhasesOf(BurstName) : 0;

    /// <summary>True when this tank was rendered with belts that wind. Both, and
    /// the pitch as well: a belt with no link length is a belt that cannot be
    /// tied to the ground, and one running against a ground it does not match
    /// is worse than one that does not run.</summary>
    public bool HasTracks
    {
        get
        {
            foreach (string layer in TrackNames)
                if (!Has(layer))
                    return false;
            return TrackPitch > 0.0;
        }
    }

    /// <summary>Phases in the belt's cycle, 0 if it is missing. Both sides share
    /// the count - one render job, one belt design.</summary>
    public int TrackPhases => Has(TrackNames[0]) ? PhasesOf(TrackNames[0]) : 0;

    /// <summary>True when the tube was rendered as its own layer and can recoil.
    /// Two phases is the minimum that means anything: rest and somewhere
    /// else - and it is two poses of <i>travel</i>, not two frames of the layer:
    /// a tube laid at eleven angles and never recoiling would have 11 phases and
    /// nothing to animate. See <see cref="RecoilPhases"/>.</summary>
    public bool HasRecoil => Has(BarrelName) && RecoilPhases > 1;

    /// <summary>
    /// True when this tank was rendered in a knocked-out pose.
    ///
    /// The turret alone, because it is the half that cannot be faked: the belts'
    /// slack is a nice-to-have and a wreck without it is still plainly a wreck,
    /// whereas a wreck whose turret sits level and whose gun still points at the
    /// horizon is a live tank that has stopped moving. So the belts are asked for
    /// separately, and a set with only the turret gets the turret.
    /// </summary>
    public bool HasWreck => WreckMount != "";

    /// <summary>
    /// Which layer carries the wreck's pose for the mount in *this* set, or "".
    /// </summary>
    /// <remarks>
    /// Asked of the set rather than named by the layer that needs it, because
    /// the answer is a fact about the vehicle: a turret fell into its ring and
    /// took the gun with it, so one layer holds both; a welded box did not move,
    /// so the drooped gun is the pose and there is no turret half. Both are "the
    /// wreck's mount", exactly one is rendered, and every reader wants whichever
    /// one that is - see EffectLayer.Wanted.
    /// </remarks>
    public string WreckMount =>
        Has(WreckTurretName) ? WreckTurretName
        : Has(WreckBarrelName) ? WreckBarrelName : "";

    /// <summary>
    /// Whether this set was rendered from a vehicle with a ring.
    /// </summary>
    /// <remarks>
    /// <b>The picture's own answer to what <see cref="MovementProfile.Turreted"/>
    /// asserts</b>, and the two must agree. The renderer no longer spins a
    /// casemate: a vehicle with no ring has no turret layer, because its mount is
    /// part of the hull - so the absence of that layer is the fact, on disk, and
    /// a class flagged turreted whose atlas has no turret is a class whose gun
    /// will never appear to traverse however fast its table says it can.
    /// </remarks>
    public bool HasTurretLayer => Has("turret");

    /// <summary>True when both belts were rendered slack as well.</summary>
    public bool HasWreckTracks
    {
        get
        {
            foreach (string layer in WreckTrackNames)
                if (!Has(layer))
                    return false;
            return true;
        }
    }

    /// <summary>
    /// Poses of <i>recoil</i> the tube was rendered in, rest included, 0 if
    /// absent. The renderer picks this off the travel - about one phase per
    /// pixel of stroke - so it is read rather than assumed.
    /// </summary>
    /// <remarks>
    /// <b>Not the layer's phase count, and the difference is a shipped bug.</b>
    /// The barrel layer carries two axes now - <c>phase = rung * travel +
    /// travel</c> - so on a set with a ladder <see cref="PhasesOf"/> returns 55
    /// where the recoil has 5. This read the raw count, handed it to
    /// <c>RecoilLoop</c>, and every shot walked the whole ladder: the tube
    /// recoiled, sprang out at +3.6 degrees and recoiled again, then -3.6, then
    /// wider, and never came home, because the loop's holds grow 1.35 per pose
    /// and fifty-four of them run to 1.4e8 frames. Divide by the rungs and both
    /// kinds of set answer 5.
    /// </remarks>
    public int RecoilPhases =>
        Has(BarrelName) ? Math.Max(1, PhasesOf(BarrelName) / ElevRungs) : 0;

    /// <summary>The angles the tube was laid at, in the order the phases are
    /// stored. Empty on a set rendered with a level gun.</summary>
    public IReadOnlyList<double> ElevTableDeg =>
        _elev.TryGetValue(BarrelName, out double[]? table) ? table : Array.Empty<double>();

    /// <summary>Rungs of laying the tube was rendered at - 1 when it was not
    /// laid at all, so that dividing by it is safe on every set.</summary>
    public int ElevRungs => Math.Max(1, ElevTableDeg.Count);

    /// <summary>
    /// The rung that is the level gun, which is the one the bench currently
    /// fires from.
    /// </summary>
    /// <remarks>
    /// Found rather than assumed to be 0. The pipeline builds the table level
    /// first on purpose - so that a set with a ladder reads as the old
    /// five-phase set to anything that ignores the second axis - but that is its
    /// choice to change, and a wrong guess here does not distort the tube, it
    /// fires the gun at an angle nobody asked for.
    ///
    /// This is also the seam: when the bench starts choosing a rung from the
    /// target's level, this call is what it replaces.
    /// </remarks>
    public int LevelRung
    {
        get
        {
            IReadOnlyList<double> table = ElevTableDeg;
            int best = 0;
            for (int i = 1; i < table.Count; i++)
                if (Math.Abs(table[i]) < Math.Abs(table[best]))
                    best = i;
            return best;
        }
    }

    /// <summary>
    /// The steepest angle this set was rendered at, in degrees - the ladder's
    /// cap. Nought on a set with a level gun.
    ///
    /// The renderer takes it from the grade rather than from taste
    /// (<c>barrel_recoil.ladder</c>: it must cover <c>atan(step_grade)</c>, one
    /// level over one cell, which is the commonest shot through a level), and
    /// past it there is one legal position the mounting cannot answer: two
    /// levels over one cell, 26.565 degrees.
    ///
    /// <b>A rotation, and so not what the gun reaches.</b> What the mounting can
    /// be laid at is this on top of wherever the tube rests - see
    /// <see cref="LayBand"/>, which is what the bench compares a wanted angle
    /// against. This stays because it is the pipeline's own figure and the two
    /// sides compare it; it is not a bearing and was read as one.
    /// </summary>
    public double ElevCapDeg
    {
        get
        {
            double cap = 0.0;
            foreach (double deg in ElevTableDeg)
                cap = Math.Max(cap, Math.Abs(deg));
            return cap;
        }
    }

    /// <summary>
    /// Where the tube points with the ladder at rung 0, in degrees above the
    /// ground - which is not nought, and on one tank is not close to it.
    ///
    /// <b>A rung is a rotation the renderer applied, not a bearing.</b>
    /// <c>barrel_recoil.pose</c> turns the tube by <c>table_deg</c> from
    /// wherever the model left it, so where it ends up is that plus this. The
    /// four gun tanks rest within two degrees of level and nobody noticed;
    /// <c>HMP</c>'s mortar is <b>modelled at 26.46 degrees</b>, the way a mortar
    /// is built, so its rendered ladder actually spans 12.4 to 40.5 and the
    /// bench, reading the rungs as bearings, believed it spanned -14 to 14.
    ///
    /// <b>Read off the stamped bore rather than stamped again.</b> The bore's
    /// direction is measured on the tube itself (<c>muzzle_point.set_muzzle</c>)
    /// and its <c>z</c> is the sine of exactly this angle - a second number for
    /// it in the atlas would be a second thing to keep in step. A set with no
    /// bore answers nought, which is what every set did before this existed.
    /// </summary>
    public double RestDeg => HasBore
        ? Mathf.RadToDeg(Math.Asin(Math.Clamp(Bore.Dir.Z, -1.0f, 1.0f)))
        : 0.0;

    /// <summary>Where the tube points on a rung, in degrees above the ground:
    /// the rung's own rotation on top of <see cref="RestDeg"/>.</summary>
    public double LayOf(int rung) => RestDeg + ElevOf(rung);

    /// <summary>
    /// The shallowest and steepest the tube can be laid, in degrees above the
    /// ground - the ladder seen from the board rather than from the mounting.
    ///
    /// <b>Asymmetric wherever the tube does not rest level</b>, which is every
    /// tank and badly so on the mortar. The ladder itself is symmetric because
    /// the board is (<c>barrel_recoil.ladder</c>); what it reaches is not.
    /// </summary>
    public (double Low, double High) LayBand
    {
        get
        {
            IReadOnlyList<double> table = ElevTableDeg;
            if (table.Count == 0)
                return (RestDeg, RestDeg);
            double low = double.MaxValue, high = double.MinValue;
            for (int i = 0; i < table.Count; i++)
            {
                double at = LayOf(i);
                low = Math.Min(low, at);
                high = Math.Max(high, at);
            }
            return (low, high);
        }
    }

    /// <summary>
    /// The rendered rung nearest an angle, in degrees above the ground.
    ///
    /// <b>Nearest and not interpolated, because the ladder is the render.</b>
    /// There are eleven poses of the tube on the sheet and nothing between them;
    /// an angle asked for is answered with the pose that exists, which on this
    /// board is usually exact - the rungs <em>are</em> the legal positions
    /// (<c>atan(grade·levels/cells)</c>), so a gun laid at a cell it can
    /// actually shoot at lands on its own rung rather than near one.
    ///
    /// <b>This is the seam <see cref="LevelRung"/> named.</b> A set rendered
    /// with a level gun has one rung and answers 0 to everything, which is what
    /// it drew before any of this existed.
    /// </summary>
    public int RungFor(double deg)
    {
        IReadOnlyList<double> table = ElevTableDeg;
        int best = 0;
        double gap = double.MaxValue;
        for (int i = 0; i < table.Count; i++)
        {
            // Against where the tube ends up, not against the rotation that
            // put it there - see RestDeg, which is the whole of the difference
            // and is 26 degrees on the mortar.
            double d = Math.Abs(LayOf(i) - deg);
            if (d < gap)
            {
                gap = d;
                best = i;
            }
        }
        return best;
    }

    /// <summary>
    /// The barrel layer's phase for a laying rung and a recoil pose - the one
    /// place the renderer's <c>phase = elev * len(travel) + travel</c> is
    /// written down on this side.
    /// </summary>
    public int BarrelPhase(int rung, int travel)
    {
        int travels = Math.Max(1, RecoilPhases);
        rung = Math.Clamp(rung, 0, ElevRungs - 1);
        travel = Math.Clamp(travel, 0, travels - 1);
        return rung * travels + travel;
    }

    /// <summary>The angle a rung stands at, in degrees above the ground; nought
    /// for a rung that is not on the ladder, which includes every rung of a set
    /// rendered with a level gun.</summary>
    public double ElevOf(int rung)
    {
        IReadOnlyList<double> table = ElevTableDeg;
        return rung >= 0 && rung < table.Count ? table[rung] : 0.0;
    }

    /// <summary>
    /// The mounting the tube lays on: the point it turns about, in world units.
    ///
    /// <b>Stamped beside the ladder because the ladder is poses and this is what
    /// made them.</b> The renderer lays a rung by one rigid turn about this
    /// point (<c>barrel_recoil.pose</c>: translate, rotate, translate back), so
    /// anything on the tube - the muzzle, the bore, the flash hanging off it -
    /// moves by that same turn and by nothing else. Without the pivot this side
    /// can see that the tube went up and cannot say where its mouth went, which
    /// is a gun firing out of its own middle.
    /// </summary>
    public Vector3 Trunnion { get; private set; }

    /// <summary>Which way the tube turns to lay, signed so that a positive angle
    /// raises the muzzle - <c>barrel_recoil.elev_axis</c> says that is the only
    /// thing about it a caller should have to know.</summary>
    public Vector3 ElevAxis { get; private set; }

    /// <summary>Whether the mounting arrived. A set rendered before it was
    /// stamped keeps its level answers rather than erroring: the tube is then
    /// drawn laid and its mouth is measured level, which is what every set did
    /// before any of this.</summary>
    public bool HasMounting { get; private set; }

    private void TakeMounting(ElevMeta elev)
    {
        if (elev.Trunnion.Length < 3 || elev.Axis.Length < 3)
            return;
        var axis = new Vector3((float)elev.Axis[0], (float)elev.Axis[1],
                               (float)elev.Axis[2]);
        if (axis.LengthSquared() <= 0.0f)
            return;
        Trunnion = new Vector3((float)elev.Trunnion[0], (float)elev.Trunnion[1],
                               (float)elev.Trunnion[2]);
        ElevAxis = axis.Normalized();
        HasMounting = true;
    }

    /// <summary>A world vector turned by the laying angle, about
    /// <see cref="ElevAxis"/> - Rodrigues, the same turn
    /// <c>barrel_recoil.pose</c> puts on the tube.</summary>
    public Vector3 Laid(Vector3 world, double deg)
    {
        if (!HasMounting || Math.Abs(deg) < 1e-9)
            return world;
        double rad = Mathf.DegToRad(deg);
        var c = (float)Math.Cos(rad);
        var s = (float)Math.Sin(rad);
        return world * c + ElevAxis.Cross(world) * s
               + ElevAxis * ElevAxis.Dot(world) * (1.0f - c);
    }

    /// <summary>
    /// The gun's bore as it stands with the tube on <paramref name="rung"/> -
    /// <see cref="Bore"/> put through the mounting's own turn.
    ///
    /// <b>The direction is the half that matters.</b> Everything the flash and
    /// its smoke are made of is laid out along <c>Dir</c> in bore radii, so a
    /// laid tube drawn with a level bore does not merely start its flame in the
    /// wrong place - it points it somewhere the tube is not, and the longer the
    /// flame the wider that opens. The point is here for its <c>z</c>, which is
    /// what the height map is compared against and which the laying moves.
    /// </summary>
    /// <summary>
    /// How far the tube tipped <em>on screen</em> between level and a rung, in
    /// radians - for the one thing that cannot be laid in world units.
    ///
    /// The drawn flash sheet is a picture of a flame, not a model of one: it is
    /// laid along the ground direction and scaled by it, so there is nothing to
    /// turn about an axis. What there is, is a screen angle, and it is taken as
    /// a difference for <see cref="Muzzle(double, int)"/>'s reason - the level
    /// rung then comes out at exactly nought and a sheet drawn on a set with no
    /// mounting is the sheet it always was.
    /// </summary>
    public float Tip(double facing, int rung)
    {
        if (!HasMounting || !HasBore)
            return 0.0f;
        Vector2 level = Project(Bore.Dir, facing);
        Vector2 laid = Project(BoreAt(rung).Dir, facing);
        return level.LengthSquared() <= 0.0f || laid.LengthSquared() <= 0.0f
            ? 0.0f : level.AngleTo(laid);
    }

    public Port BoreAt(int rung)
    {
        double deg = ElevOf(rung);
        if (!HasMounting || Math.Abs(deg) < 1e-9)
            return Bore;
        return new Port(Trunnion + Laid(Bore.Point - Trunnion, deg),
                        Laid(Bore.Dir, deg), Bore.Radius);
    }

    /// <summary>
    /// Ground covered by one turn of the belt's cycle, in pixels - one link.
    ///
    /// Stamped in world units and divided here, rather than stamped in pixels.
    /// A pixel is what the camera fit produces, so a pitch in pixels written
    /// before the render would be the renderer's second guess at its own
    /// answer; the atlas already carries `units_per_pixel` and dividing is this
    /// side's business.
    /// </summary>
    public double TrackPitch { get; private set; }

    /// <summary>
    /// Half the track gauge in atlas pixels - the radius the belts wind about
    /// when the tank turns on the spot. 0 when there are no belts to measure.
    ///
    /// Measured rather than estimated, and the three tanks are why. It used to
    /// be a quarter of <see cref="HullSpan"/>, which cannot express what is
    /// there: the hulls come out within 3% of one length (188/183/182px) while
    /// the gauges do not (84/78/69px). So the one number that was supposed to
    /// carry the difference between the classes was very nearly the same for
    /// all of them, and it was 12%, 17% and 32% too wide besides - worst on the
    /// heavy, whose belts sit closest together for its length.
    ///
    /// Half the separation of the two belts, not either belt's distance from
    /// the anchor. The anchor is the turret axis and there is no promise it
    /// sits on the hull's centreline - on HTP it is 5.5px off it, which would
    /// have put the fixed number back by another sixteenth.
    /// </summary>
    public double TrackArm { get; private set; }

    /// <summary>
    /// How wide one belt is in atlas pixels - the width of the rut it leaves.
    /// 0 when there are no belts to measure.
    ///
    /// The narrowest a belt is across the screen, over every heading. The belt
    /// is a long thin thing lying in the ground plane, so the heading that puts
    /// its length into the screen shows nothing across but its width, and every
    /// other heading shows more. Taking the minimum finds that heading without
    /// having to know which one it is - the same trick <see cref="TrackArm"/>
    /// and <see cref="HullSpan"/> use, with the extremum the other way up.
    ///
    /// Horizontal only, for the reason the gauge is: the vertical is the
    /// camera's tilt, and folding it in would report a width that grows with
    /// the elevation the scene was rendered at. Comes out 29/21/22 on the three
    /// tanks - within a quarter of each other, unlike the gauge.
    /// </summary>
    public double TrackWidth { get; private set; }

    /// <summary>
    /// How long one belt is in atlas pixels. 0 when there are no belts.
    ///
    /// The same scan as <see cref="TrackWidth"/> with the extremum the right way
    /// up: the widest a belt is across the screen is the heading that lays its
    /// length across, so that is the length. 153/146/162 on the three tanks.
    ///
    /// It is here for the ruts. A mark is left by a long belt rather than by a
    /// point, and a belt cannot lay a corner tighter than its own half-length -
    /// it rolls over it. So this is the window the trail is smoothed through,
    /// which is the difference between a rut that turns and a rut with a kink
    /// in it.
    /// </summary>
    public double TrackLength { get; private set; }

    /// <summary>True when every plate has a mark to leave. All four, because a
    /// tank that can be holed on three sides and not the fourth is worse than
    /// one that cannot be holed at all - the clean side reads as armour that
    /// held.</summary>
    public bool HasScars
    {
        get
        {
            foreach (string layer in ScarNames)
                if (!Has(layer))
                    return false;
            return HitFaces.Count > 0;
        }
    }

    /// <summary>Levels of damage a plate can be in, counting the clean state
    /// this layer does not draw. 0 if the layers are missing.</summary>
    public int ScarLevels => HasScars ? PhasesOf(ScarNames[0]) : 0;

    /// <summary>The layer that carries a given plate's mark, or "" if this tank
    /// has none. Faces and layers are matched by name rather than by position:
    /// <see cref="HitFaces"/> comes from the render and could in principle come
    /// back in another order or short a plate.</summary>
    public string ScarLayer(string face)
    {
        string name = "scar_" + face;
        return Has(name) ? name : "";
    }

    /// <summary>The plates this tank was measured to have, in a stable order.</summary>
    public IReadOnlyList<string> HitFaces { get; private set; } = Array.Empty<string>();

    private readonly Dictionary<string, PlateMeta> _plates = new();

    /// <summary>
    /// Which plate a shell arriving from <paramref name="fromBearing"/> lands
    /// on, given where the hull is pointing.
    ///
    /// The plate whose outward normal comes closest to facing the shooter. Each
    /// plate's bearing is stamped as an offset from the hull's own heading, so
    /// this stays right as the tank turns - a world angle would have been a
    /// number that is only true at heading zero.
    /// </summary>
    public string FaceFor(double fromBearing, double hullFacing)
    {
        string best = HitFaces.Count > 0 ? HitFaces[0] : "";
        double bestGap = double.MaxValue;
        foreach (string face in HitFaces)
        {
            double outward = hullFacing + _plates[face].Bearing;
            double gap = Math.Abs(Mod(outward - fromBearing + 180.0, 360.0) - 180.0);
            if (gap < bestGap)
            {
                bestGap = gap;
                best = face;
            }
        }
        return best;
    }

    /// <summary>Where a plate looks, as an offset from the hull's own heading.
    /// Exposed so <see cref="FaceFor"/> can be asserted against the geometry
    /// rather than against itself.</summary>
    public double HitBearing(string face) =>
        _plates.TryGetValue(face, out PlateMeta? plate) ? plate.Bearing : 0.0;

    /// <summary>Where a hit on <paramref name="face"/> sits, in pixels from the
    /// layer's anchor, with the hull at <paramref name="hullFacing"/>.</summary>
    public Vector2 HitOffset(string face, double hullFacing) =>
        HitFrame(face, hullFacing) is { } row
            ? new Vector2((float)row.Offset[0], (float)row.Offset[1])
            : Vector2.Zero;

    /// <summary>How squarely the plate faces the camera: +1 straight at it, -1
    /// straight away. The sign alone decides whether the hit is drawn in front
    /// of the tank or behind it.</summary>
    public double HitFacing(string face, double hullFacing) =>
        HitFrame(face, hullFacing)?.Facing ?? 1.0;

    /// <summary>Half the plate's width, as a screen vector, so a hit can be
    /// scattered along the plate rather than off it.</summary>
    public Vector2 HitTangent(string face, double hullFacing) =>
        HitFrame(face, hullFacing) is { } row
            ? new Vector2((float)row.Tangent[0], (float)row.Tangent[1])
            : Vector2.Zero;

    /// <summary>
    /// The plate's other half-extent - one half-height *up its own slope* - as a
    /// screen vector. The second axis of the scatter, and the reason a group of
    /// hits is a patch rather than a row.
    ///
    /// Up the slope rather than up the screen, and that is the whole point of
    /// taking it from the table: a glacis leaning back at 45 degrees foreshortens,
    /// so the same fraction of it is fewer screen pixels than on a vertical flank,
    /// and the shift stays on the metal by construction. Stamped all along beside
    /// <see cref="HitTangent"/> - see hit_point.py, "pixels for one half-extent up
    /// the plate's slope" - and simply never read, so nothing needed re-rendering
    /// to start using it.
    /// </summary>
    public Vector2 HitSlope(string face, double hullFacing) =>
        HitFrame(face, hullFacing) is { } row
            ? new Vector2((float)row.Slope[0], (float)row.Slope[1])
            : Vector2.Zero;

    private HitFrameMeta? HitFrame(string face, double hullFacing)
    {
        if (!_plates.TryGetValue(face, out PlateMeta? plate) || plate.Frames.Length == 0)
            return null;
        // the table is in frame-index order, which is what FrameFor returns
        int index = Math.Clamp(FrameFor(hullFacing), 0, plate.Frames.Length - 1);
        return plate.Frames[index];
    }

    /// <summary>Muzzle point per turret frame, in tile pixels.</summary>
    private Vector2[] _muzzle = Array.Empty<Vector2>();

    /// <summary>
    /// Where the gun ends, for the frame showing that heading, in tile pixels,
    /// <b>with the tube level</b> - see the overload for a laid one.
    ///
    /// Measured off the turret layer rather than tuned by hand, which buys two
    /// things a constant cannot. It survives a re-render at another size or with
    /// another model; and it foreshortens by itself - the same gun measures 68px
    /// from the axis across the screen and 35px pointing away from the camera,
    /// because that is what the sprite actually shows.
    /// </summary>
    public Vector2 Muzzle(int frameIndex) =>
        frameIndex >= 0 && frameIndex < _muzzle.Length ? _muzzle[frameIndex] : Anchor;

    /// <summary>
    /// The same mouth with the tube laid on <paramref name="rung"/>: the
    /// measured point plus the displacement the laying put on it.
    ///
    /// <b>Measured base, computed difference, and the split is the point.</b>
    /// The scan finds the extreme of a silhouette, which is a bore radius away
    /// from the bore and in a direction that depends on which pixel won a tie -
    /// so measuring each rung separately and subtracting measures mostly that
    /// tie. Broadside on <c>HTP</c> the scan says the mouth drops eleven pixels
    /// when the tube rises 3.6 degrees, because the rightmost column stops being
    /// the top of the muzzle disc and becomes the bottom of it. The displacement
    /// has no such trouble: the tube is rigid and the renderer turned it about
    /// <see cref="Trunnion"/>, so the mouth went exactly where that turn put it.
    ///
    /// Checked against the scan rather than argued: with the extreme replaced by
    /// the centroid of the extremes - which is the bore's own centre, and too
    /// slow to do at load - measurement and computation agree to 0.4 to 1.4px
    /// across all five sets, every rung and every heading.
    ///
    /// <b>One projection, not two.</b> <see cref="Project"/> is linear in its
    /// world vector - the bearing and reach it opens with reassemble into
    /// <c>x·cos(h+90) - y·sin(h+90)</c> - so projecting the difference is the
    /// difference of the projections, and the systematic error the class note
    /// warns about (the render spins about the ring axis, this rotates about the
    /// origin) is identical in both and cancels.
    /// </summary>
    public Vector2 Muzzle(double facing, int rung)
    {
        Vector2 rest = Muzzle(FrameFor(facing));
        if (!HasMounting || !HasBore)
            return rest;
        Vector3 lift = BoreAt(rung).Point - Bore.Point;
        return lift.LengthSquared() <= 0.0f ? rest : rest + Project(lift, facing);
    }

    /// <summary>
    /// Heading in degrees to frame index, taken from the per-frame labels the
    /// renderer wrote ("side @ 270 deg"). Reading it from the file rather than
    /// assuming front_dir = 270 means a model imported at another heading still
    /// lines up here.
    /// </summary>
    private readonly Dictionary<int, int> _facings = new();

    private AtlasSet() { }

    private void Take(string layer, Image image, LayerMeta meta)
    {
        _textures[layer] = ImageTexture.CreateFromImage(image);
        _columns[layer] = Math.Max(1, meta.Grid.Columns);
        _tiles[layer] = new Vector2I(meta.Tile[0], meta.Tile[1]);
        _anchors[layer] = new Vector2((float)meta.AnchorPx[0], (float)meta.AnchorPx[1]);
        _phases[layer] = Math.Max(1, meta.Phases);
        _counts[layer] = Math.Max(1, meta.Count);
        if (meta.Elev is not null && meta.Elev.TableDeg.Length > 0)
        {
            _elev[layer] = meta.Elev.TableDeg;
            TakeMounting(meta.Elev);
        }
        TakePlacement(layer, meta);
    }

    /// <summary>
    /// The trimmed rectangles, if this set has them.
    /// </summary>
    /// <remarks>
    /// A frame is stored at its own size rather than in a cell sized for the
    /// worst frame the layer holds, which is what the burning column's 384px
    /// cell costs every muzzle flash that fills two per cent of one. Measured
    /// over MTP's eighteen layers: 741MB of cells against 47MB packed.
    ///
    /// Kept as a parallel array rather than folded into the region arithmetic
    /// so that a set rendered before the change - anything in Sprites/Obsolete,
    /// or a tank that has not been re-run - still loads. Absent here means the
    /// grid, and <see cref="Region"/> is the only place that has to know.
    /// </remarks>
    private void TakePlacement(string layer, LayerMeta meta)
    {
        if (meta.OverTurret is { Length: > 0 })
            _over[layer] = meta.OverTurret;
        if (meta.Frames.Length == 0 || meta.Frames.All(f => f.Rect is null))
            return;
        var rects = new Rect2I[meta.Frames.Length];
        var offs = new Vector2I[meta.Frames.Length];
        for (int i = 0; i < meta.Frames.Length; i++)
        {
            int[]? r = meta.Frames[i].Rect;
            int[]? o = meta.Frames[i].Off;
            // An empty frame - a scar on a plate the hull stands in front of -
            // is a zero-sized rectangle rather than a missing entry, so the
            // draw sites all skip it by asking the same question.
            rects[i] = r is null ? new Rect2I(0, 0, 0, 0)
                                 : new Rect2I(r[0], r[1], r[2], r[3]);
            offs[i] = o is null ? Vector2I.Zero : new Vector2I(o[0], o[1]);
        }
        _rects[layer] = rects;
        _offs[layer] = offs;
    }

    private readonly Dictionary<string, Rect2I[]> _rects = new();
    private readonly Dictionary<string, Vector2I[]> _offs = new();
    private readonly Dictionary<string, bool[]> _over = new();

    /// <summary>
    /// Whether <paramref name="layer"/> is drawn in front of the turret at this
    /// hull heading, measured in Blender and stamped by the pipeline.
    /// </summary>
    /// <remarks>
    /// False when the set does not carry the flag, which is what keeps an older
    /// atlas correct: there the turret is cut into the layer's alpha, so drawing
    /// it under the turret changes nothing and drawing it over would show the
    /// bite. The flag and the holdout are two halves of one decision and they
    /// arrive together or not at all.
    ///
    /// See draw_order.py for why this is a property of the heading and not of
    /// the turret's bearing: the turret sits on its ring in the middle of the
    /// hull, so traversing it changes its silhouette and not its depth.
    /// </remarks>
    public bool OverTurret(string layer, int heading) =>
        _over.TryGetValue(layer, out bool[]? f) && f.Length > 0
        && f[((heading % f.Length) + f.Length) % f.Length];

    /// <summary>The same, asked with the hull's bearing rather than its column.
    /// Resolved through <see cref="FrameFor"/> so the side a layer is drawn on
    /// and the frame it is drawn from can never disagree about which heading
    /// this is.</summary>
    public bool OverTurretAt(string layer, double facing) =>
        OverTurret(layer, FrameFor(facing));

    /// <summary>Whether this set carries trimmed frames at all. False for one
    /// rendered before the change, and the bench draws both.</summary>
    public bool Packed => _rects.Count > 0;

    /// <summary>The layers that came off disk, smears excluded - those are built
    /// here and are still whole tiles. Exposed so the packing can be asserted
    /// over the whole set rather than over whichever layer a test remembered.</summary>
    public IEnumerable<string> LoadedLayers =>
        _textures.Keys.Where(name => !name.Contains('#'));

    /// <summary>
    /// The name under which a layer's phase average is kept.
    ///
    /// Not in <see cref="LayerNames"/> or <see cref="OptionalNames"/> and never
    /// loaded from disk: it is derived here from frames that are already in
    /// memory, so it costs no render and cannot go stale against the layer it
    /// comes from. The '#' keeps it out of the way of anything that matches
    /// layer names by substring.
    /// </summary>
    public static string SmearName(string layer) => layer + "#smear";

    /// <summary>
    /// Build a layer's phase average: one frame per heading, each the mean of
    /// that heading's phases.
    ///
    /// This is what a belt going too fast to count actually looks like, and it
    /// is not an approximation of one - a smear <em>is</em> the average of the
    /// positions passed through. So there is nothing to tune here and no filter
    /// to pick: the answer is arithmetic on frames the renderer already
    /// produced.
    ///
    /// Averaged premultiplied and divided back out, which matters only at the
    /// silhouette edge but costs nothing to get right. The alpha comes out as
    /// the mean too - and since the belt's outline barely moves between phases
    /// (the links slide along a loop that stays where it is), that mean is
    /// within a hair of every phase's own alpha. Which is what makes the
    /// cross-fade safe: a belt is part of the tank, and one that went
    /// translucent halfway through the blend would show the ground through the
    /// hull.
    /// </summary>
    private void TakeSmear(string layer)
    {
        if (!_textures.TryGetValue(layer, out ImageTexture? texture))
            return;
        int phases = PhasesOf(layer);
        int count = CountOf(layer);
        Vector2I tile = TileOf(layer);
        if (phases <= 1 || count <= 0 || tile.X <= 0 || tile.Y <= 0)
            return;

        Image source = texture.GetImage();
        source.Convert(Image.Format.Rgba8);
        int width = count * tile.X;
        var to = new byte[width * tile.Y * 4];
        // The smear stays a plain grid - one cell per heading, no phases - and
        // is built here rather than loaded, so there is nothing to trim it
        // against and nothing on disk it could disagree with.
        var phaseData = new byte[phases][];

        for (int frame = 0; frame < count; frame++)
        {
            for (int phase = 0; phase < phases; phase++)
                phaseData[phase] = TileFrom(source, layer, phase * count + frame)
                                   .GetData();
            for (int y = 0; y < tile.Y; y++)
            {
                for (int x = 0; x < tile.X; x++)
                {
                    double r = 0.0, g = 0.0, b = 0.0, a = 0.0;
                    for (int phase = 0; phase < phases; phase++)
                    {
                        byte[] from = phaseData[phase];
                        int o = (y * tile.X + x) * 4;
                        if (o < 0 || o + 3 >= from.Length)
                            continue;
                        double weight = from[o + 3] / 255.0;
                        r += from[o] * weight;
                        g += from[o + 1] * weight;
                        b += from[o + 2] * weight;
                        a += weight;
                    }
                    int d = (y * width + frame * tile.X + x) * 4;
                    if (a > 0.0)
                    {
                        to[d] = (byte)Math.Clamp(r / a, 0.0, 255.0);
                        to[d + 1] = (byte)Math.Clamp(g / a, 0.0, 255.0);
                        to[d + 2] = (byte)Math.Clamp(b / a, 0.0, 255.0);
                    }
                    to[d + 3] = (byte)Math.Clamp(a / phases * 255.0, 0.0, 255.0);
                }
            }
        }

        string name = SmearName(layer);
        Image smeared = Image.CreateFromData(
            width, tile.Y, false, Image.Format.Rgba8, to);
        _textures[name] = ImageTexture.CreateFromImage(smeared);
        _columns[name] = count;
        _counts[name] = count;
        _tiles[name] = tile;
        _anchors[name] = AnchorOf(layer);
        _phases[name] = 1;
    }

    /// <summary>
    /// Measure <see cref="TrackArm"/> off the two belt layers.
    ///
    /// The widest the two belts are apart on screen, over every heading, is the
    /// gauge: the gauge axis lies in the ground plane, so the heading that puts
    /// it across the screen shows it whole and every other heading foreshortens
    /// it. Taking the maximum picks that heading without having to know which
    /// one it is - the same trick <see cref="HullSpan"/> uses to find the
    /// broadside, and for the same reason frame 0 would not do.
    ///
    /// Horizontal only. The vertical separation is the camera's tilt rather
    /// than anything about the tank, and folding it in would report a gauge
    /// that grows with the elevation the scene was rendered at.
    /// </summary>
    private void MeasureGauge()
    {
        string left = TrackNames[0], right = TrackNames[1];
        if (!_textures.TryGetValue(left, out ImageTexture? leftTex)
            || !_textures.TryGetValue(right, out ImageTexture? rightTex))
            return;
        double[] leftAt = BeltCentres(left, leftTex);
        double[] rightAt = BeltCentres(right, rightTex);
        double widest = 0.0;
        for (int f = 0; f < Math.Min(leftAt.Length, rightAt.Length); f++)
            if (!double.IsNaN(leftAt[f]) && !double.IsNaN(rightAt[f]))
                widest = Math.Max(widest, Math.Abs(rightAt[f] - leftAt[f]));
        TrackArm = widest / 2.0;
        (double Narrow, double Wide) l = BeltSpan(left, leftTex);
        (double Narrow, double Wide) r = BeltSpan(right, rightTex);
        double narrow = Math.Min(l.Narrow, r.Narrow);
        TrackWidth = double.IsPositiveInfinity(narrow) ? 0.0 : narrow;
        TrackLength = Math.Max(l.Wide, r.Wide);
    }

    /// <summary>The least and the most a belt spans across the frame over every
    /// heading - its width and its length. See <see cref="TrackWidth"/>. Phase 0
    /// only, like <see cref="BeltCentres"/>: winding along the loop changes
    /// neither.</summary>
    private (double Narrow, double Wide) BeltSpan(string layer, ImageTexture texture)
    {
        Image image = texture.GetImage();
        image.Convert(Image.Format.Rgba8);
        int count = CountOf(layer);
        Vector2I tile = TileOf(layer);
        double narrowest = double.PositiveInfinity;
        double widest = 0.0;
        for (int frame = 0; frame < count; frame++)
        {
            byte[] data = TileFrom(image, layer, frame).GetData();
            int lo = int.MaxValue, hi = int.MinValue;
            for (int y = 0; y < tile.Y; y++)
            for (int x = 0; x < tile.X; x++)
            {
                int o = (y * tile.X + x) * 4;
                if (o + 3 >= data.Length || data[o + 3] <= 32)
                    continue;
                if (x < lo) lo = x;
                if (x > hi) hi = x;
            }
            if (hi < lo)
                continue;
            narrowest = Math.Min(narrowest, hi - lo + 1);
            widest = Math.Max(widest, hi - lo + 1);
        }
        return (narrowest, widest);
    }

    /// <summary>Where a belt sits across the frame, per heading, in tile pixels;
    /// NaN for a frame with nothing in it. Phase 0 only - the belt winds along
    /// its loop and does not move sideways doing it.</summary>
    private double[] BeltCentres(string layer, ImageTexture texture)
    {
        Image image = texture.GetImage();
        image.Convert(Image.Format.Rgba8);
        int count = CountOf(layer);
        Vector2I tile = TileOf(layer);
        var centres = new double[count];
        for (int frame = 0; frame < count; frame++)
        {
            byte[] data = TileFrom(image, layer, frame).GetData();
            double sum = 0.0;
            long seen = 0;
            for (int y = 0; y < tile.Y; y++)
            {
                for (int x = 0; x < tile.X; x++)
                {
                    int o = (y * tile.X + x) * 4;
                    if (o + 3 >= data.Length || data[o + 3] <= 32)
                        continue;
                    sum += x;
                    seen++;
                }
            }
            centres[frame] = seen > 0 ? sum / seen : double.NaN;
        }
        return centres;
    }

    /// <summary>The plate table, straight out of the layer that gets placed by
    /// it. Ordered front, rear, left, right when they are all present, so the
    /// harness cycles them in a way that makes sense rather than in whatever
    /// order the JSON happened to come in.</summary>
    /// <summary>Take the stamped port block. Called off whichever burning layer
    /// carries it; both do, and they carry the same one, so the second call is a
    /// no-op rather than a conflict - see <c>stamp_ports.py</c> on why the block
    /// goes into both.</summary>
    private void TakePorts(PortsMeta ports)
    {
        if (ports.List.Length == 0 || ports.HullLength <= 0.0)
            return;
        HullLength = ports.HullLength;
        _ports.Clear();
        foreach (PortMeta port in ports.List)
        {
            if (port.Point.Length < 3 || port.Dir.Length < 3)
                continue;
            var dir = new Vector3((float)port.Dir[0], (float)port.Dir[1],
                                  (float)port.Dir[2]);
            if (dir.LengthSquared() <= 0.0f)
                continue;
            _ports.Add(new Port(
                new Vector3((float)port.Point[0], (float)port.Point[1],
                            (float)port.Point[2]),
                dir.Normalized(), (float)port.Radius));
        }
    }

    /// <summary>Take the stamped bore block. Both shot layers carry the same
    /// one, for <c>stamp_bore.py</c>'s reason, so the second call is a no-op
    /// rather than a conflict.</summary>
    private void TakeBore(BoreMeta bore)
    {
        if (bore.Point.Length < 3 || bore.Dir.Length < 3 || bore.Radius <= 0.0)
            return;
        var dir = new Vector3((float)bore.Dir[0], (float)bore.Dir[1],
                              (float)bore.Dir[2]);
        if (dir.LengthSquared() <= 0.0f)
            return;
        Bore = new Port(new Vector3((float)bore.Point[0], (float)bore.Point[1],
                                    (float)bore.Point[2]),
                        dir.Normalized(), (float)bore.Radius);
        // The shot layers carry the hull's length too, so a set that ships a
        // bore and no ports can still place what it builds. Never overwritten:
        // the ports block is the one stamp_ports fitted, and two fits of one
        // number are two numbers.
        if (HullLength <= 0.0 && bore.HullLength > 0.0)
            HullLength = bore.HullLength;
    }

    private void TakePlates(Dictionary<string, PlateMeta> plates)
    {
        foreach ((string face, PlateMeta plate) in plates)
            _plates[face] = plate;
        string[] preferred = { "front", "rear", "left", "right" };
        var ordered = new List<string>();
        foreach (string face in preferred)
            if (_plates.ContainsKey(face))
                ordered.Add(face);
        foreach (string face in _plates.Keys)
            if (!ordered.Contains(face))
                ordered.Add(face);
        HitFaces = ordered;
    }

    /// <summary>
    /// Load one tank's layers.
    ///
    /// <paramref name="dir"/> is which directory the pixels come from and
    /// defaults to <paramref name="tag"/>, which is the normal case. They are
    /// separable so that several classes can wear one tank's atlases while the
    /// others are being re-rendered - see <c>Main.SharedSpriteDir</c>. The tag is
    /// what the harness knows this tank *as* and keeps driving it by, so
    /// borrowing pixels never borrows a class.
    /// </summary>
    public static AtlasSet Load(string root, string tag, string? dir = null)
    {
        var atlas = new AtlasSet { Tag = tag };
        dir ??= tag;
        LayerMeta? hull = null;
        Image? turretImage = null;
        Image? hullImage = null;
        Image? barrelImage = null;
        Image? heightImage = null;
        LayerMeta? heightMeta = null;

        foreach (string layer in LayerNames)
        {
            string basePath = $"{root}/{dir}/{layer}_atlas";
            string jsonPath = basePath + ".json";
            string pngPath = basePath + ".png";

            if (!File.Exists(jsonPath) || !File.Exists(pngPath))
            {
                atlas.Error = $"missing {basePath}.png/.json";
                return atlas;
            }

            LayerMeta? meta;
            try
            {
                meta = JsonSerializer.Deserialize<LayerMeta>(File.ReadAllText(jsonPath));
            }
            catch (JsonException e)
            {
                atlas.Error = $"unreadable json {jsonPath}: {e.Message}";
                return atlas;
            }

            if (meta is null)
            {
                atlas.Error = $"empty json {jsonPath}";
                return atlas;
            }

            Image? image = Image.LoadFromFile(pngPath);
            if (image is null)
            {
                atlas.Error = $"unreadable png {pngPath}";
                return atlas;
            }

            atlas.Take(layer, image, meta);
            if (layer == "hex")
                // Off the tile, not off the file. HexRect is in tile pixels -
                // GroundOffset subtracts the anchor from it, and the terrain art
                // is scaled to it - and once the frames are trimmed the file is
                // the hexagon with the empty margin already gone, so its used
                // rect starts at zero and the tile's own position is lost.
                // Caught by looking: every number was fine and the grass tiles
                // came out clipped.
                atlas.HexRect = atlas.TileImage("hex", 0).GetUsedRect();
            if (layer == "hull")
            {
                hull = meta;
                hullImage = image;
            }
        }

        // Effects are optional and silent about it. Each needs a piece split out
        // by hand in the .blend - a Barrel for the shot, an Engine for the plume
        // - which only some scenes have; a tank without them still loads, and
        // the harness draws the painted sheet instead of the rendered flash. A
        // missing file here is a fact about the scene, not an error.
        foreach (string layer in OptionalNames)
        {
            string basePath = $"{root}/{dir}/{layer}_atlas";
            if (!File.Exists(basePath + ".json") || !File.Exists(basePath + ".png"))
                continue;
            LayerMeta? meta;
            try
            {
                meta = JsonSerializer.Deserialize<LayerMeta>(
                    File.ReadAllText(basePath + ".json"));
            }
            catch (JsonException)
            {
                continue;
            }
            Image? image = meta is null ? null : Image.LoadFromFile(basePath + ".png");
            if (meta is null || image is null)
                continue;
            atlas.Take(layer, image, meta);
            if (layer == "turret")
                turretImage = image;
            if (layer == BarrelName)
                barrelImage = image;
            if (layer == HeightName)
            {
                heightImage = image;
                heightMeta = meta;
            }
            if (layer == BurstName && meta.Hits is not null)
                atlas.TakePlates(meta.Hits.Faces);
            if (meta.Ports is not null)
                atlas.TakePorts(meta.Ports);
            if (meta.Bore is not null)
                atlas.TakeBore(meta.Bore);
            if (layer == TrackNames[0] && meta.Track is { Length: > 0 }
                && meta.UnitsPerPixel > 0.0)
                atlas.TrackPitch = meta.Track[0].Pitch / meta.UnitsPerPixel;
        }

        // The belts get a phase average each, built from the frames just loaded.
        // Only the belts: everything else is either a one-shot nobody watches
        // long enough to blur or a loop that is meant to be read frame by frame.
        foreach (string layer in TrackNames)
            atlas.TakeSmear(layer);
        atlas.MeasureGauge();

        if (hull is null)
        {
            atlas.Error = "no hull layer";
            return atlas;
        }

        atlas.Tile = new Vector2I(hull.Tile[0], hull.Tile[1]);
        atlas.Count = Math.Max(1, hull.Count);
        atlas.Anchor = new Vector2((float)hull.AnchorPx[0], (float)hull.AnchorPx[1]);
        atlas.UnitsPerPixel = hull.UnitsPerPixel;
        atlas.Elevation = hull.View.Elevation;
        atlas.ReadFacings(hull);
        // Off the gun's own layer where there is one, and off the turret only as
        // a fallback. Not a refinement - a necessity: once the tube moved to a
        // layer of its own the turret's alpha stops at the mantlet, so the
        // "furthest solid pixel" of a turret frame is a corner of the turret and
        // the sheet flash comes off the roof. Both muzzle self-tests caught it.
        //
        // It is also the better measurement. Reading the turret was always a
        // proxy that had to reject the things on a turret that are not the gun,
        // which is what MuzzleErode is for; a layer holding nothing but the tube
        // has nothing to reject, so the erosion drops to 1 and only fends off the
        // antialiased fringe. The two layers share a tile and an anchor - one
        // render_set job gives every layer of one size the same anchor - so the
        // measurement means the same thing either way.
        if (barrelImage is not null && atlas._columns.ContainsKey(BarrelName))
            atlas.FindMuzzles(barrelImage, BarrelName, 1);
        else if (turretImage is not null)
            atlas.FindMuzzles(turretImage, "turret", MuzzleErode);
        if (hullImage is not null)
            atlas.MeasureHull(hullImage, "hull");
        // Last, because they read the tile, the count and the anchor that were
        // only just settled off the hull's own metadata. The groundline stays
        // whether or not the height map arrived: it is what a set rendered
        // before sprite_height.py existed still cuts its waterline against, and
        // it is still the honest answer to the question it was written for.
        atlas.TakeGroundline();
        if (heightImage is not null && heightMeta is not null)
            atlas.TakeHeights(heightImage, heightMeta);
        // Straight off the map that was just read, and cheap: the deck is where
        // deep water cuts a hull, and unlike the waterline it does not move when
        // the pond does - see TakeDeckline.
        atlas.TakeDeckline();
        // After TakeHeights: the crown is the hull's measured height plus the
        // drawn gap between the two silhouettes' tops, so it needs both images
        // and the map's number to stand on.
        if (turretImage is not null && hullImage is not null)
            atlas.MeasureTurret(turretImage, hullImage);
        // And where that turret's own bottom edge is, per column per heading -
        // what clamps the deck out of the turret. The gun is not in it: see
        // TurretFootAt.
        if (turretImage is not null)
            atlas.MeasureTurretFoot(turretImage);
        return atlas;
    }

    /// <summary>
    /// Furthest *solid* pixel of each turret frame along the heading it shows.
    ///
    /// Solid, not merely opaque, and that word is the whole of it. Taking the
    /// furthest opaque pixel puts the muzzle on the wireless aerial: it stands
    /// higher than anything else on the tank, so the moment the gun points away
    /// from the camera and foreshortens, the aerial wins and the flash comes out
    /// of the radio. Requiring the neighbours a few pixels out to be opaque too
    /// throws away anything thinner than the barrel, which is exactly the class
    /// of thing that can beat it - aerials, hatch handles, tow cables.
    /// </summary>
    private const int MuzzleErode = 3;

    /// <summary>
    /// How long the hull reads at its broadest heading, in atlas pixels.
    ///
    /// Measured rather than configured, and it is the number that says the
    /// generator hands over no scale at all: LTP, MTP and HTP come out 188, 183
    /// and 182px, so all three tanks are rendered the same physical size and the
    /// heavy's turning circle is actually the *smallest* of the three in world
    /// units. Whatever size difference the classes have on screen is therefore
    /// entirely <see cref="MovementProfile.Size"/>'s doing, not the models'.
    ///
    /// The widest of the twelve frames, not frame 0: which heading frame 0 shows
    /// is a fact about each model's shape, and reading it there gave 138 / 110 /
    /// 108px - a spread that looks like class and is nothing but the carousel.
    /// Broadside is the same view of every tank, so it is the one that compares.
    /// </summary>
    public int HullSpan { get; private set; }

    /// <summary>
    /// Where the tank meets the ground in each column of each heading, in tile
    /// rows - the table a waterline is cut against.
    ///
    /// <b>It exists because a billboard has no depth, and the water is the first
    /// thing that wanted some.</b> A sprite pixel's screen row is
    /// <c>height*cos(e) + depth*sin(e)</c> and the sprite knows only the sum, so
    /// the end of the hull further from the camera is drawn higher without being
    /// higher: measured on MTP, 183px of hull along the ground lands as 93px of
    /// screen row nose-on, against 36px of water. A cut at one row is therefore
    /// not an approximation of the right answer, it is a different one - the tail
    /// stands dry in the same pond the nose is under.
    ///
    /// <b>What fixes it is already in the pixels.</b> The visible surface of a
    /// tank at any column is its near one, and where that surface meets the
    /// ground is exactly the bottom of the silhouette in that column. So the
    /// waterline is "this far above the bottom of the tank in this column", which
    /// follows the hull round every heading with no geometry and no re-render -
    /// the same trick <see cref="FindMuzzles"/>, <see cref="MeasureHull"/> and
    /// <see cref="BeltCentres"/> already play on the loaded atlas.
    ///
    /// <b>Only the layers that stand on the ground go into it</b>, and leaving the
    /// gun out is the whole of why it can be trusted. A barrel swung out past the
    /// tracks is the one part whose lowest pixel is nowhere near the ground it
    /// hangs over; excluded, its columns hold no entry at all and fall back to the
    /// flat cut, which is far below it - so the tube stays dry instead of dipping.
    ///
    /// Stored as one row per heading, R being the row and A saying whether there
    /// is an answer at all. A byte per column is a pixel of quantisation on a
    /// boundary that is one pixel wide anyway.
    /// </summary>
    private ImageTexture? _shore;

    /// <summary>The groundline table, or null on a set this could not be built
    /// from - in which case the waterline falls back to the flat cut, which is
    /// how it behaved before this existed.</summary>
    public Texture2D? Groundline => _shore;

    /// <summary>The table's own answer for one column of one heading, in tile
    /// rows, or -1 where it has none. Named so the check can ask it the same
    /// question the shader asks rather than re-deriving the packing.</summary>
    public int GroundlineAt(int frame, int column) =>
        _shoreRows is null || frame < 0 || frame >= Count
        || column < 0 || column >= Tile.X
            ? -1 : _shoreRows[frame * Tile.X + column];

    private int[]? _shoreRows;

    /// <summary>The height map, one byte per pixel of every heading: 0 where
    /// there is no tank, else 1..255 up the tank's own height. Kept as bytes
    /// rather than as a texture because nothing samples it - it is scanned, and
    /// the texture that comes out of the scan is <see cref="Waterline"/>.
    /// </summary>
    private byte[]? _heights;

    /// <summary>One byte of the height map, or 0 off the end of it. Exposed so
    /// the tables taken off the map - <see cref="Waterline"/>,
    /// <see cref="DecklineAt"/> - can be checked against the map itself rather
    /// than against each other, which would only say they agree.</summary>
    public byte HeightAt(int frame, int column, int row) =>
        _heights is null || frame < 0 || frame >= Count
        || column < 0 || column >= Tile.X || row < 0 || row >= Tile.Y
            ? (byte)0 : _heights[(frame * Tile.Y + row) * Tile.X + column];

    /// <summary>How many screen pixels of rise the whole byte range is worth.
    /// The one number that turns the map into an answer about a water plane
    /// standing so many pixels above the tank's feet.</summary>
    public double HeightSpanPx { get; private set; }

    /// <summary>
    /// How high the deck is, in the same screen pixels of rise as
    /// <see cref="HeightSpanPx"/> - the height the roof of the hull's silhouette
    /// stands at, which on a turreted hull is the deck and on a casemate the
    /// roof of the fighting compartment.
    ///
    /// <b>Not the top of the range, and the difference is a quarter of the
    /// medium.</b> The range runs to the highest point on the hull, which is a
    /// hatch or an engine cover and not the plate the turret sits on, so a
    /// draught named against it (<see cref="MovementProfile.Draught"/>) put the
    /// water a hand over the tracks at six tenths and over the whole deck at
    /// 0.85, with no value between that read as swimming. Measured as the
    /// median of the height code at the top of every hull column of every frame
    /// - the deck is most of what a column's roof is, on every heading - so the
    /// one number holds for the whole turntable. Nought without a height map.
    /// </summary>
    public double DeckHeightPx { get; private set; }

    /// <summary>
    /// How tall the hull and its tracks are, in ground pixels - the same scale
    /// <see cref="HullSpan"/> is in, so the two convert to metres by the one
    /// chain.
    ///
    /// <b>Not <see cref="HeightSpanPx"/>, and the difference is the camera.</b>
    /// The span is the rise as drawn, which is the true height shortened by
    /// cos(elevation); this is the map's own range divided by the render's
    /// units-per-pixel, which is the height a tape measure would find. It can
    /// be trusted for exactly the reason the map stores height rather than
    /// depth: height is invariant under the turntable, so this is one number
    /// and not one per heading.
    ///
    /// <b>The turret is not in it, by the map's own design</b> - it turns
    /// against the hull the map is indexed by. For the one reader this serves,
    /// the ram's box, that is the right height anyway: what pushes masonry is
    /// the hull and its running gear, and a box the turret's height would press
    /// on courses the glacis never reaches. Nought on a set rendered before
    /// sprite_height.py existed, and the reader falls back to the declared
    /// <c>WallRig.TankTall</c> - which is how every set behaved until this
    /// arrived.
    /// </summary>
    public double HullTallPx { get; private set; }

    /// <summary>
    /// How long the glacis runs, nose tip to deck, in ground pixels - the
    /// measured slope of the frontal armour, for the wedge the ram drives.
    ///
    /// <b>Read off the broadside frame of the height map</b>, the one heading
    /// where along-the-hull is along-the-screen: <see cref="FrameFor"/>(0)
    /// faces screen-right (<see cref="GroundDirection"/>(0) is +X), so the
    /// nose is the rightmost inked column and needs no guessing at. Each
    /// column's crest is the tallest surface in it - the near track and the
    /// hull compete, and the winner is whichever actually meets a brick first,
    /// which is the shape a plough wants. The bow is then the distance from
    /// the nose back to where the crest reaches nine tenths of its climb to
    /// the deck.
    ///
    /// <b>Nought when it cannot be trusted</b>: no map at all, or a measured
    /// run outside a twentieth-to-seven-tenths of the silhouette - a profile
    /// that flat or that steep is a misread frame, not a tank - and the reader
    /// keeps its declared slope instead of building a plate or a ramp the
    /// length of the hull. Ground pixels, like <see cref="HullTallPx"/>:
    /// screen columns are unforeshortened along the ground, so the count is
    /// the world length.
    /// </summary>
    public double HullBowPx { get; private set; }

    /// <summary>
    /// How high the turret's roof stands above the ground, in ground pixels -
    /// the same scale <see cref="HullTallPx"/> is in, for the one reader the
    /// hull height deliberately leaves short: the ram's box stops at the deck,
    /// and bricks riding over it cross the turret sprite as though it were not
    /// there.
    ///
    /// <b>Read off the sprites, because the height map cannot say it</b> - the
    /// map is indexed by hull heading and the turret turns against it, which is
    /// why sprite_height.py excludes the turret by design. What the sprites do
    /// know is how far the turret's topmost drawn pixel stands above the
    /// hull's own in the same broadside frame: that gap is world rise shortened
    /// by cos(elevation), and the hull's top is <see cref="HullTallPx"/> - a
    /// number the map already vouches for - so the roof is the sum. Measured as
    /// a difference within one frame rather than against the anchor, because
    /// the anchor is not on the ground (it stands a third of a tile above the
    /// groundline, caught by printing both); and taken as the mean of the two
    /// broadsides, so the ground-depth term of the two top points - the part of
    /// drawn height that is nearness to the camera rather than tallness -
    /// enters once from each side and mostly cancels.
    ///
    /// <b>Solid pixels, not opaque ones</b> - the same word doing the same work
    /// as in <see cref="FindMuzzles"/>: the topmost <i>opaque</i> pixel of a
    /// turret is its aerial, and a box grown to the aerial's tip would press on
    /// courses no armour reaches. Nought when there is no turret layer, no
    /// height map to stand the difference on, or no gap survived the erosion -
    /// and the reader keeps the hull-high box it always had.
    /// </summary>
    public double TurretTallPx { get; private set; }

    /// <summary>
    /// How high the hull's silhouette stands, in the height map's screen px of
    /// rise (<see cref="HeightSpanPx"/>'s scale), with the thinnest half a
    /// percent of its pixels left out - the aerial, a muzzle's tip, a lifted
    /// mortar's mouth. The top of the range is exactly those pixels: on the
    /// destroyer the range runs to 87px where the casemate's roof stands at
    /// 69, so a drowning measured against the range shrank the hull for the
    /// sake of a gun tip. Read through <see cref="RoofRisePx"/>. Nought
    /// without a map.
    /// </summary>
    public double HullCrestPx { get; private set; }

    /// <summary>The turret's roof as a rise in the same screen px -
    /// <see cref="TurretTallPx"/> foreshortened by the camera, so that it
    /// compares with <see cref="HeightSpanPx"/> and <see cref="DeckHeightPx"/>
    /// directly. Nought where the turret's height is unknown.</summary>
    public double TurretRisePx => TurretTallPx * Math.Cos(Mathf.DegToRad(Elevation));

    /// <summary>How high the whole tank stands, turret and all, in screen px
    /// of rise: the higher of the turret's roof and the hull's crest. What a
    /// drowning has to put under - <see cref="Stage3D.Shrunk"/> measures the
    /// water against this and shrinks the tank only by what this overtops
    /// it. Measured on the five at their default sizes: 67 to 85px over the
    /// contact.</summary>
    public double RoofRisePx => Math.Max(HullCrestPx, TurretRisePx);

    /// <summary>The world z the height map's floor and ceiling stand for.
    /// Needed by anything that wants to put its own world z on the map's scale -
    /// see <see cref="ProcSmoke"/>, which compares a puff's height against the
    /// tank's at the same pixel to decide which is in front.</summary>
    public double HeightLow { get; private set; }
    public double HeightHigh { get; private set; }

    /// <summary>Whether this set carries a height map at all. A set rendered
    /// before sprite_height.py existed does not, and falls back to the
    /// groundline lifted by the depth - which is how every set behaved until
    /// this arrived.</summary>
    public bool HasHeights => _heights is not null;

    private void TakeHeights(Image image, LayerMeta meta)
    {
        if (meta.HeightRange is not { Length: 2 } || meta.UnitsPerPixel <= 0.0
            || Tile.X <= 0 || Tile.Y <= 0)
            return;
        HeightLow = meta.HeightRange[0];
        HeightHigh = meta.HeightRange[1];
        HeightSpanPx = (meta.HeightRange[1] - meta.HeightRange[0])
                       * Math.Cos(Mathf.DegToRad(meta.View.Elevation))
                       / meta.UnitsPerPixel;
        if (HeightSpanPx <= 0.0)
            return;
        Vector2I tile = TileOf(HeightName);
        int count = Math.Min(Count, CountOf(HeightName));
        var map = new byte[Count * Tile.X * Tile.Y];
        image.Convert(Image.Format.Rgba8);
        for (int frame = 0; frame < count; frame++)
        {
            byte[] data = TileFrom(image, HeightName, frame).GetData();
            for (int y = 0; y < Math.Min(tile.Y, Tile.Y); y++)
            for (int x = 0; x < Math.Min(tile.X, Tile.X); x++)
            {
                int at = (y * tile.X + x) * 4;
                if (data[at + 3] < 128)
                    continue;
                map[(frame * Tile.Y + y) * Tile.X + x] = Math.Max((byte)1, data[at]);
            }
        }
        _heights = map;
        HullTallPx = (meta.HeightRange[1] - meta.HeightRange[0])
                     / meta.UnitsPerPixel;
        // And how high the silhouette stands with its whiskers left out - see
        // HullCrestPx: the codes tallied, and the tally read at the 99.5th
        // centile.
        var tally = new int[256];
        int inked = 0;
        foreach (byte code in map)
        {
            if (code == 0)
                continue;
            tally[code]++;
            inked++;
        }
        if (inked > 0)
        {
            int seen = 0, crest = 1;
            for (int code = 1; code < 256; code++)
            {
                seen += tally[code];
                if (seen >= inked * 0.995)
                {
                    crest = code;
                    break;
                }
            }
            HullCrestPx = HeightSpanPx * Math.Max(0, crest - 1) / 254.0;
        }
        MeasureProw();
    }

    /// <summary>Measure <see cref="HullBowPx"/> off the height map's broadside
    /// frame - see that property for what is read and why it can be.</summary>
    private void MeasureProw()
    {
        if (_heights is null || Tile.X <= 0 || Count <= 0)
            return;
        int frame = FrameFor(0.0);
        if (frame < 0 || frame >= Count)
            return;
        var crest = new byte[Tile.X];
        for (int x = 0; x < Tile.X; x++)
        for (int y = 0; y < Tile.Y; y++)
        {
            byte code = _heights[(frame * Tile.Y + y) * Tile.X + x];
            if (code > crest[x])
                crest[x] = code;
        }
        int nose = -1, rear = -1, deck = 0;
        for (int x = 0; x < Tile.X; x++)
        {
            if (crest[x] == 0)
                continue;
            if (rear < 0)
                rear = x;
            nose = x;
            deck = Math.Max(deck, crest[x]);
        }
        if (nose <= rear)
            return;
        double brow = crest[nose] + 0.9 * (deck - crest[nose]);
        int at = nose;
        while (at > rear && crest[at] < brow)
            at--;
        int bow = nose - at;
        if (bow < (nose - rear) * 0.05 || bow > (nose - rear) * 0.7)
            return;
        HullBowPx = bow;
    }

    /// <summary>
    /// The top of the hull in a column of a frame - the deck - and -1 in a
    /// column the hull is not drawn in.
    ///
    /// <b>What a drowned tank's waterline is, and it is a rule rather than a
    /// measurement.</b> In deep water the pond stands a whole level over a bed
    /// that is itself a level down, which is about four times as tall as a hull;
    /// measured honestly against that plane a tank is under it entire, turret and
    /// all, and the board would show a green rectangle where a tank was. So the
    /// board rules instead: <b>a hull in deep water is flooded to its deck,
    /// whatever the pond is doing.</b> The engine is under, the turret is out,
    /// and the picture says "утоплен" at a glance rather than saying "somewhere
    /// under here is a tank".
    ///
    /// <b>Which is also how the gun leaves the calculation.</b> The map this is
    /// taken off is the hull's alone - <c>sprite_height.py</c> renders the
    /// engine, the hull, the tracks and the rolls, and leaves the turret out
    /// because the map is indexed by the hull's heading while the turret turns
    /// against it. The barrel is a layer of its own and is not in it either. So
    /// a column that holds only gun holds no deck, answers -1, and the shader
    /// reads that as dry: the tube stays out of the water without anything
    /// having to know it is a tube. The threshold scan needed a table of the
    /// turret's foot and a clamp in both shaders to get the same picture, and
    /// got it to within 297 hull pixels; this needs neither and is exact.
    ///
    /// <b>One table, built once.</b> <see cref="Waterline"/> is a scan against a
    /// water plane and is rebuilt whenever the plane moves; the deck does not
    /// depend on the plane at all, so this is measured at load and read for ever
    /// after. Fords keep the scan - there the plane really is inside the hull and
    /// the map really can answer where.
    ///
    /// Encoded exactly as <see cref="Waterline"/> is - <c>r</c> the row as a
    /// fraction of the tile's last row, alpha nought for "no answer" - because
    /// the two are handed to the same uniform.
    /// </summary>
    public int DecklineAt(int frame, int column) =>
        _deckRows is null || frame < 0 || frame >= Count
        || column < 0 || column >= Tile.X
            ? -1 : _deckRows[frame * Tile.X + column];

    /// <summary>The deck table as a texture, or null on a set with no height
    /// map - in which case a drowned tank falls back to the groundline and the
    /// flat cut, exactly as a wading one does.</summary>
    public Texture2D? Deckline => _deck;

    private ImageTexture? _deck;
    private int[]? _deckRows;

    /// <summary>Fill <see cref="DecklineAt"/> and <see cref="Deckline"/>: the
    /// topmost row the height map holds in each column of each frame. Cheap -
    /// one pass over the map, which is already in memory for the scan.</summary>
    private void TakeDeckline()
    {
        if (_heights is null || Tile.X <= 0 || Tile.Y <= 1 || Count <= 0)
            return;
        var rows = new int[Count * Tile.X];
        Array.Fill(rows, -1);
        for (int frame = 0; frame < Count; frame++)
        for (int x = 0; x < Tile.X; x++)
        for (int y = 0; y < Tile.Y; y++)
            if (_heights[(frame * Tile.Y + y) * Tile.X + x] != 0)
            {
                rows[frame * Tile.X + x] = y;
                break;
            }
        int max = Tile.Y - 1;
        var bytes = new byte[rows.Length * 4];
        // And the height the roof stands at, for DeckHeightPx: the code at the
        // top of each column, median over every column of every frame.
        var roof = new List<int>();
        for (int i = 0; i < rows.Length; i++)
        {
            if (rows[i] < 0)
                continue;
            bytes[i * 4] = (byte)Math.Clamp(rows[i] * 255 / max, 0, 255);
            bytes[i * 4 + 3] = 255;
            int frame = i / Tile.X, x = i % Tile.X;
            roof.Add(_heights[(frame * Tile.Y + rows[i]) * Tile.X + x]);
        }
        if (roof.Count > 0)
        {
            roof.Sort();
            // The ramp was rendered over 1..255 so that 0 could mean "no tank" -
            // see Waterline, which decodes the same way.
            DeckHeightPx = HeightSpanPx * Math.Max(0, roof[roof.Count / 2] - 1) / 254.0;
        }
        _deckRows = rows;
        _deck = ImageTexture.CreateFromImage(Image.CreateFromData(
            Tile.X, Count, false, Image.Format.Rgba8, bytes));
    }

    /// <summary>
    /// The lowest row the turret is drawn on in a column of a frame - its foot,
    /// and -1 in a column the turret does not stand in.
    ///
    /// <b>What the deck alone cannot say, and the picture said it loudly.</b>
    /// <see cref="DecklineAt"/> is the top of the hull's own silhouette in a
    /// column, and on an isometric board that top is the <b>far</b> rim of the
    /// deck - drawn high on the screen, above the near, lower edge of a turret
    /// standing in front of it. Cut there, the water ran across the middle of
    /// the turret and tinted its bottom half; reported as "part of the turret is
    /// under the water, and the water should only cover the hull". Rear and
    /// three-quarter headings are where it shows, because that is where the most
    /// deck is drawn behind the turret.
    ///
    /// So the line is <c>max(deck, foot)</c>: the turret's own bottom edge where
    /// there is a turret in the column, the hull's top where there is not. Both
    /// numbers are needed and neither is enough - the deck is what puts the foam
    /// along the hull's own edge in every column beside the turret, and this is
    /// what keeps it out of the turret in the columns the turret is in.
    ///
    /// <b>The turret alone, and the gun is deliberately not in it.</b> An
    /// earlier cut unioned the barrel layer in, so that the tube could not be
    /// cut; that also dragged the line down into every column the tube swings
    /// out into and made the gun part of where the water stands. Left out, such
    /// a column holds no hull and no turret, answers -1, and the shader reads
    /// that as dry - the tube stays out of the water without the water knowing
    /// anything about tubes. Where the gun is drawn over the hull it is tinted
    /// with the hull, which is what a gun below the waterline looks like.
    ///
    /// <b>Per frame, not a union over the frames.</b> A union is a row the
    /// turret is never drawn below: safe, and not right - in a column the turret
    /// only reaches at some other angle it holds a foot where this frame has
    /// none, and the hull above it comes out dry. Measured on LTP's front frame,
    /// 879 of 9827 visible hull pixels - the whole stern - read off the picture
    /// as "the bow is under the water and the stern is over it".
    ///
    /// Opaque rather than solid, unlike <see cref="TopRow"/> - the aerial margin
    /// is there to keep a whisker out of a height, and here a whisker of turret
    /// is still turret that must not be cut.
    ///
    /// Read by <see cref="Stage3D"/>'s two halves of the waterline, which clamp
    /// their line to it, and at the turret's own heading rather than the hull's:
    /// the table is built per frame and the turret does not turn with the hull.
    /// </summary>
    public int TurretFootAt(int frame, int column) =>
        _turretFoot is null || frame < 0 || frame >= Count
        || column < 0 || column >= Tile.X
            ? -1 : _turretFoot[frame * Tile.X + column];

    /// <summary>The same table as a texture, one row per frame, for the
    /// sprite's own shader: <c>r</c> is the foot as a fraction of the tile's
    /// last row, exactly as <see cref="DecklineAt"/> encodes a row, <c>g</c> the
    /// crown (<see cref="TurretCrownAt"/>) the same way, and alpha nought is a
    /// column the turret does not stand in.</summary>
    public Texture2D? TurretFoot { get; private set; }

    private int[]? _turretFoot;

    /// <summary>
    /// The turret's own crown in one column of one heading: the topmost
    /// <i>solid</i> row, -1 where no turret stands. The other end of
    /// <see cref="TurretFootAt"/>, and the two together are where the water is
    /// on a turret going under: the line climbs from the foot as the surface
    /// passes the deck, and once it would stand above this row the column is
    /// under entire - see <c>PaintShader</c>'s <c>turret_foot</c> and
    /// <see cref="Stage3D.TurretLine"/>.
    ///
    /// Solid rather than opaque, like <see cref="TopRow"/> and unlike the foot:
    /// the whisker above a turret is an aerial, and a column ruled "still out"
    /// for an aerial's sake would wear a bar of foam across open water above
    /// the roof. A column that has a foot and no solid pixel at all is a
    /// whisker on its own and takes its foot for a crown, so that water
    /// reaching the deck covers it.
    /// </summary>
    public int TurretCrownAt(int frame, int column) =>
        _turretCrown is null || frame < 0 || frame >= Count
        || column < 0 || column >= Tile.X
            ? -1 : _turretCrown[frame * Tile.X + column];

    private int[]? _turretCrown;

    /// <summary>Fill <see cref="TurretFootAt"/>, <see cref="TurretCrownAt"/>
    /// and <see cref="TurretFoot"/>: the lowest opaque and the topmost solid
    /// row per column per heading of the turret layer.</summary>
    private void MeasureTurretFoot(Image turretImage)
    {
        if (Tile.X <= 0 || Tile.Y <= 1 || Count <= 0)
            return;
        var foot = new int[Count * Tile.X];
        var crown = new int[Count * Tile.X];
        Array.Fill(foot, -1);
        Array.Fill(crown, -1);
        FootOf(turretImage, "turret", foot);
        CrownOf(turretImage, "turret", crown);
        int max = Tile.Y - 1;
        var bytes = new byte[foot.Length * 4];
        for (int i = 0; i < foot.Length; i++)
        {
            if (foot[i] < 0)
                continue;
            // A whisker on its own - see TurretCrownAt.
            if (crown[i] < 0)
                crown[i] = foot[i];
            bytes[i * 4] = (byte)Math.Clamp(foot[i] * 255 / max, 0, 255);
            bytes[i * 4 + 1] = (byte)Math.Clamp(crown[i] * 255 / max, 0, 255);
            bytes[i * 4 + 3] = 255;
        }
        _turretFoot = foot;
        _turretCrown = crown;
        TurretFoot = ImageTexture.CreateFromImage(Image.CreateFromData(
            Tile.X, Count, false, Image.Format.Rgba8, bytes));
    }

    /// <summary>One layer's contribution to <see cref="TurretCrownAt"/>: the
    /// topmost solid row of every column of every frame, folded into the
    /// heading that frame shows. Through
    /// <see cref="TileFrom"/> rather than off the packed atlas, unlike
    /// <see cref="FootOf"/>: solidity is asked of a pixel's neighbours, and at
    /// a packed frame's edge the neighbour is the next frame's.</summary>
    private void CrownOf(Image whole, string layer, int[] crown)
    {
        if (!Has(layer) || TileOf(layer) != Tile)
            return;
        Image rgba = whole;
        if (rgba.GetFormat() != Image.Format.Rgba8)
        {
            rgba = (Image)whole.Duplicate();
            rgba.Convert(Image.Format.Rgba8);
        }
        int frames = Math.Max(1, PhasesOf(layer)) * Count;
        if (_rects.TryGetValue(layer, out Rect2I[]? rects))
            frames = Math.Min(frames, rects.Length);
        for (int index = 0; index < frames; index++)
        {
            byte[] data = TileFrom(rgba, layer, index).GetData();
            int at = index % Count * Tile.X;
            for (int x = 0; x < Tile.X; x++)
            for (int y = 0; y < Tile.Y; y++)
                if (Solid(data, Tile.X, 0, 0, x, y, 2))
                {
                    if (crown[at + x] < 0 || y < crown[at + x])
                        crown[at + x] = y;
                    break;
                }
        }
    }

    /// <summary>
    /// Which frame of the gun layer a tank with this turret heading and this
    /// recoil pose is drawing, or -1 while it draws none.
    ///
    /// <b>Named here because two places have to agree on it and one of them is
    /// a shader.</b> <see cref="EffectLayer"/> composes the phase out of the
    /// laying rung and the recoil pose and then asks
    /// <see cref="EffectFrame"/> for the frame; <see cref="Stage3D"/> has to
    /// arrive at the same number to keep the water off the tube - see
    /// <c>PaintShader</c>'s <c>gun_box</c>. Two copies of that composition is
    /// one copy too many: the failure would be a gun exempted at the wrong
    /// elevation, which is a tube-shaped dry patch beside the tube.
    /// </summary>
    public int GunFrame(double facing, int recoil, int rung) =>
        !Has(BarrelName) ? -1
            : EffectFrame(BarrelName, BarrelPhase(rung, recoil), facing);

    /// <summary>One layer's contribution to <see cref="MeasureTurretFoot"/>:
    /// the lowest opaque row of every frame, folded into the heading that frame
    /// shows. Read straight off the packed atlas rather than through
    /// <see cref="TileFrom"/> - blitting a frame into a full tile to look at its
    /// bottom row is most of a tile touched to read one row of it.</summary>
    private void FootOf(Image whole, string layer, int[] foot)
    {
        if (!Has(layer) || TileOf(layer) != Tile)
            return;
        Image rgba = whole;
        if (rgba.GetFormat() != Image.Format.Rgba8)
        {
            rgba = (Image)whole.Duplicate();
            rgba.Convert(Image.Format.Rgba8);
        }
        byte[] data = rgba.GetData();
        int wide = rgba.GetWidth(), tall = rgba.GetHeight();
        int frames = Math.Max(1, PhasesOf(layer)) * Count;
        if (_rects.TryGetValue(layer, out Rect2I[]? rects))
            frames = Math.Min(frames, rects.Length);
        _offs.TryGetValue(layer, out Vector2I[]? offs);
        for (int index = 0; index < frames; index++)
        {
            Rect2 region = Region(layer, index);
            int rw = (int)region.Size.X, rh = (int)region.Size.Y;
            if (rw <= 0 || rh <= 0)
                continue;
            int rx = (int)region.Position.X, ry = (int)region.Position.Y;
            Vector2I off = offs is not null && index < offs.Length
                ? offs[index] : Vector2I.Zero;
            int at = index % Count * Tile.X;
            for (int x = 0; x < rw; x++)
            {
                int col = off.X + x;
                if (col < 0 || col >= Tile.X || rx + x < 0 || rx + x >= wide)
                    continue;
                for (int y = rh - 1; y >= 0; y--)
                {
                    if (ry + y < 0 || ry + y >= tall
                        || data[((ry + y) * wide + rx + x) * 4 + 3] <= 8)
                        continue;
                    int row = off.Y + y;
                    if (row >= 0 && row < Tile.Y && row > foot[at + col])
                        foot[at + col] = row;
                    break;
                }
            }
        }
    }

    /// <summary>Measure <see cref="TurretTallPx"/> off the two broadside
    /// frames - see that property for what is read and why a within-frame
    /// difference, averaged over the two sides, is the honest height.</summary>
    private void MeasureTurret(Image turretImage, Image hullImage)
    {
        if (HullTallPx <= 0.0)
            return;
        double gap = 0.0;
        int seen = 0;
        foreach (double facing in new[] { 0.0, 180.0 })
        {
            int index = FrameFor(facing);
            if (index < 0 || index >= Count)
                continue;
            int roof = TopRow(turretImage, "turret", index);
            int deck = TopRow(hullImage, "hull", index);
            if (roof < 0 || deck < 0)
                continue;
            gap += deck - roof;
            seen++;
        }
        if (seen == 0 || gap <= 0.0)
            return;
        TurretTallPx = HullTallPx
                       + gap / seen / Math.Cos(Mathf.DegToRad(Elevation));
    }

    /// <summary>The topmost solid row of one frame of one layer, or -1 when
    /// nothing survives the erosion. Solid with the aerial-rejecting margin,
    /// for <see cref="TurretTallPx"/>'s reason.</summary>
    private int TopRow(Image image, string layer, int index)
    {
        Image rgba = image;
        if (rgba.GetFormat() != Image.Format.Rgba8)
        {
            rgba = (Image)image.Duplicate();
            rgba.Convert(Image.Format.Rgba8);
        }
        byte[] data = TileFrom(rgba, layer, index).GetData();
        for (int y = 0; y < Tile.Y; y++)
        for (int x = 0; x < Tile.X; x++)
            if (Solid(data, Tile.X, 0, 0, x, y, 2))
                return y;
        return -1;
    }

    /// <summary>
    /// The waterline: which row of each column of each heading the water leaves
    /// the tank at, for water standing <paramref name="dip"/> tile pixels above
    /// its feet. -1 in a column the water never reaches.
    ///
    /// <b>Scanned up from the bottom, stopping at the first dry pixel</b>, and
    /// both halves of that matter. Stopping is what keeps the line on the near
    /// side of the tank: the far belt shows over the hull at the ends of some
    /// headings and is genuinely below the surface, so a rule that took the
    /// highest wet pixel would carry the line to the top of the sprite there.
    /// Passing through empty pixels is the other half - a gap between hull and
    /// belt is water seen through the tank, and what is above it is wet.
    ///
    /// <b>A column the water covers whole is the case this map cannot answer,
    /// and it took deep water to find out.</b> The scan above assumes there is a
    /// dry pixel to stop at. Once the water stands over the hull's roof there is
    /// none, and the scan runs to the top of the column - which would be right if
    /// the map were the tank. It is the hull: <c>sprite_height.py</c> renders the
    /// engine, the hull, the tracks and the rolls and excludes the turret by
    /// design, because the map is indexed by hull heading and the turret turns
    /// against it (see <see cref="TurretTallPx"/>). So above the deck the map
    /// goes on describing hull the picture does not show - the deck behind the
    /// turret - and the line lands inside a turret standing over it.
    ///
    /// Measured on LTP's front frame, centre column: the map holds hull from row
    /// 76 to 185, and the turret sprite occupies rows 54 to 123. The scan
    /// returned 76, forty-seven rows up inside the turret, so the collar was
    /// drawn across the turret and everything under it tinted - reported as "only
    /// half the tank shows, and the turret is above the water".
    ///
    /// <b>So this table is a ford's, and deep water is not asked of it at all</b>
    /// - see <see cref="DecklineAt"/>, which rules the line to the deck instead
    /// of scanning for it. Everything above is why: over the roof the scan has
    /// no dry pixel to stop at, and no amount of patching a threshold makes one
    /// row per column able to say "wet, except this island of turret". Two
    /// patches were built and measured before the rule replaced them - a foot
    /// per turret frame, then the gun's layer unioned into it - and they got the
    /// error down to 297 of 9827 hull pixels and no further.
    ///
    /// Row nought stays as the answer for a column with no dry pixel: it says
    /// the column is under entire, the tint takes it whole and the foam has
    /// nothing to sit on. A ford cannot reach it - a pond a level deep can, and
    /// a ford is by definition less - but a sentinel that is right when it is
    /// never used costs nothing and lies about nothing.
    ///
    /// <b>No ford moves</b>, and that is the point of hanging it on "no dry
    /// pixel": water below the roof stops the scan the way it always did.
    /// Measured - the ford bench comes out bit-identical.
    ///
    /// Rebuilt only when the depth changes, which is a dial nobody drags: the
    /// scan is a byte compare over the whole map and the result is a table the
    /// two shaders share, so the line on the armour and the line on the water
    /// cannot be two answers.
    /// </summary>
    public Texture2D? Waterline(float dip)
    {
        if (_heights is null || HeightSpanPx <= 0.0)
            return null;
        if (_waterAt is not null && Mathf.Abs(dip - _waterDip) < 0.01f)
            return _waterAt;
        // The byte a surface exactly at the water plane would carry. The ramp
        // was rendered over 1..255 so that 0 could mean "no tank", so the
        // fraction runs (code - 1) / 254 - see sprite_height.py's floor.
        double wet = 1.0 + 254.0 * dip / HeightSpanPx;
        var rows = new int[Count * Tile.X];
        for (int frame = 0; frame < Count; frame++)
        for (int x = 0; x < Tile.X; x++)
        {
            int line = -1;
            bool any = false, dry = false;
            for (int y = Tile.Y - 1; y >= 0; y--)
            {
                byte code = _heights[(frame * Tile.Y + y) * Tile.X + x];
                if (code == 0)
                    continue;
                any = true;
                if (code > wet)
                {
                    dry = true;
                    break;
                }
                line = y;
            }
            // The whole column under, which the scan above cannot answer - see
            // the note. Row nought: the tint takes the column entire and the
            // foam has nothing to sit on. Where a turret stands in it the line
            // comes back up to that turret's foot, and that is the reader's to
            // do, because only the reader knows which way the turret is facing.
            if (any && !dry)
                line = 0;
            rows[frame * Tile.X + x] = line;
        }
        _waterRows = rows;
        _waterDip = dip;
        int max = Tile.Y - 1;
        var bytes = new byte[rows.Length * 4];
        for (int i = 0; i < rows.Length; i++)
        {
            if (rows[i] < 0)
                continue;
            bytes[i * 4] = (byte)Math.Clamp(rows[i] * 255 / max, 0, 255);
            bytes[i * 4 + 3] = 255;
        }
        _waterAt = ImageTexture.CreateFromImage(Image.CreateFromData(
            Tile.X, Count, false, Image.Format.Rgba8, bytes));
        return _waterAt;
    }

    /// <summary>The waterline table's own answer for one column, in tile rows,
    /// or -1 where the water never reaches that column. Ask
    /// <see cref="Waterline"/> first: this reads what that last built.</summary>
    public int WaterlineAt(int frame, int column) =>
        _waterRows is null || frame < 0 || frame >= Count
        || column < 0 || column >= Tile.X
            ? -1 : _waterRows[frame * Tile.X + column];

    private ImageTexture? _waterAt;
    private int[]? _waterRows;
    private float _waterDip = float.NaN;

    private void TakeGroundline()
    {
        if (Tile.X <= 0 || Tile.Y <= 1 || Count <= 0)
            return;
        var bottom = new int[Count * Tile.X];
        Array.Fill(bottom, -1);
        bool any = false;
        foreach (string layer in new[] { "hull" }.Concat(TrackNames))
        {
            if (!_textures.TryGetValue(layer, out ImageTexture? texture))
                continue;
            Vector2I tile = TileOf(layer);
            // Into the hull frame's own pixels. The layers of one size share an
            // anchor - one render_set job gives them one - so this is a no-op
            // today; written out because a set where it is not would otherwise
            // put the shoreline a few pixels up the wrong layer, silently.
            Vector2 shift = AnchorOf(layer) - Anchor;
            Image source = texture.GetImage();
            source.Convert(Image.Format.Rgba8);
            int count = Math.Min(Count, CountOf(layer));
            for (int frame = 0; frame < count; frame++)
            {
                // Phase 0 of a belt, because the loop's own cycle moves the tread
                // and not the ground it stands on - the same fixing the check
                // sheets do for the same reason.
                byte[] data = TileFrom(source, layer, frame).GetData();
                for (int x = 0; x < tile.X; x++)
                {
                    int col = (int)Math.Round(x - shift.X);
                    if (col < 0 || col >= Tile.X)
                        continue;
                    for (int y = tile.Y - 1; y >= 0; y--)
                    {
                        if (!Opaque(data, tile.X, x, y))
                            continue;
                        int row = (int)Math.Round(y - shift.Y);
                        // Anything whose own lowest pixel is above the turret axis
                        // is not standing on the ground - an aerial, a hatch
                        // handle, a tow cable clear of the hull below it. Six
                        // columns of MTP are like that, and left in they would put
                        // a wet band across an aerial. Same failure the gun has,
                        // arriving through a part that could not simply be left
                        // out of the layer list.
                        if (row <= Anchor.Y)
                            break;
                        int at = frame * Tile.X + col;
                        if (row > bottom[at])
                        {
                            bottom[at] = row;
                            any = true;
                        }
                        break;
                    }
                }
            }
        }
        if (!any)
            return;

        int max = Tile.Y - 1;
        var bytes = new byte[bottom.Length * 4];
        for (int i = 0; i < bottom.Length; i++)
        {
            if (bottom[i] < 0)
                continue;
            bytes[i * 4] = (byte)Math.Clamp(bottom[i] * 255 / max, 0, 255);
            bytes[i * 4 + 3] = 255;
        }
        _shoreRows = bottom;
        _shore = ImageTexture.CreateFromImage(Image.CreateFromData(
            Tile.X, Count, false, Image.Format.Rgba8, bytes));
    }

    private void MeasureHull(Image image, string layer)
    {
        int widest = 0;
        for (int i = 0; i < Count; i++)
            widest = Math.Max(widest,
                              TileFrom(image, layer, i).GetUsedRect().Size.X);
        HullSpan = widest;
    }

    private void FindMuzzles(Image image, string layer, int erode)
    {
        Image rgba = image;
        if (rgba.GetFormat() != Image.Format.Rgba8)
        {
            rgba = (Image)image.Duplicate();
            rgba.Convert(Image.Format.Rgba8);
        }

        _muzzle = new Vector2[Count];
        for (int i = 0; i < _muzzle.Length; i++)
            _muzzle[i] = Anchor;

        foreach ((int facing, int index) in _facings)
        {
            if (index < 0 || index >= Count)
                continue;
            Vector2 dir = GroundDirection(facing).Normalized();
            // Index runs to Count whatever the layer's phase count, because the
            // barrel layer's first row is the tube at rest - which is where a
            // muzzle is when the gun has not just fired.
            byte[] data = TileFrom(rgba, layer, index).GetData();
            int width = Tile.X, ox = 0, oy = 0;
            // Erode, then again with none if that left this heading with nothing.
            // Pointing away from the camera the tube foreshortens to a stub a few
            // pixels across and the turret's holdout has taken a bite out of that,
            // so an erosion wide enough to be useful elsewhere can reject the
            // whole silhouette - and a heading with no muzzle keeps the seeded
            // anchor, which puts the flash in the middle of the tank.
            for (int pass = 0; pass < 2; pass++)
            {
                int wide = pass == 0 ? erode : 0;
                float best = float.MinValue;
                for (int y = 0; y < Tile.Y; y++)
                for (int x = 0; x < Tile.X; x++)
                {
                    if (!Solid(data, width, ox, oy, x, y, wide))
                        continue;
                    float d = (new Vector2(x, y) - Anchor).Dot(dir);
                    if (d <= best)
                        continue;
                    best = d;
                    _muzzle[index] = new Vector2(x, y);
                }
                if (best > float.MinValue)
                    break;
            }
        }
    }

    private bool Solid(byte[] data, int width, int ox, int oy, int x, int y,
                       int erode)
    {
        if (x < erode || y < erode
            || x >= Tile.X - erode || y >= Tile.Y - erode)
            return false;
        return Opaque(data, width, ox + x, oy + y)
               && Opaque(data, width, ox + x - erode, oy + y)
               && Opaque(data, width, ox + x + erode, oy + y)
               && Opaque(data, width, ox + x, oy + y - erode)
               && Opaque(data, width, ox + x, oy + y + erode);
    }

    private static bool Opaque(byte[] data, int width, int x, int y) =>
        data[(y * width + x) * 4 + 3] >= 32;

    private void ReadFacings(LayerMeta meta)
    {
        foreach (FrameMeta frame in meta.Frames)
        {
            int facing = -1;
            if (!string.IsNullOrEmpty(frame.Label))
            {
                // "side @ 270 deg" / "corner @ 300 deg"
                foreach (string part in frame.Label.Split(' '))
                    if (int.TryParse(part, out int parsed))
                    {
                        facing = parsed;
                        break;
                    }
            }

            if (facing < 0)
                // no labels: fall back to the stated import convention
                facing = (int)Math.Round(Mod(270.0 + frame.Angle, 360.0));

            _facings[Mod(facing, 360)] = frame.Index;
        }
    }

    public Texture2D Texture(string layer) => _textures[layer];

    /// <summary>Frame showing the part pointing at <paramref name="facing"/>
    /// degrees, snapped to the nearest heading that was actually rendered.</summary>
    public int FrameFor(double facing)
    {
        double step = 360.0 / Count;
        int snapped = Mod((int)Math.Round(Mod(facing, 360.0) / step) * (int)Math.Round(step), 360);
        if (_facings.TryGetValue(snapped, out int index))
            return index;

        // labels did not land on the step grid - take the nearest by angle
        int best = 0;
        double bestGap = double.MaxValue;
        foreach ((int key, int value) in _facings)
        {
            double gap = Math.Abs(Mod(key - facing + 180.0, 360.0) - 180.0);
            if (gap < bestGap)
            {
                bestGap = gap;
                best = value;
            }
        }
        return best;
    }

    /// <summary>
    /// Where a frame's pixels are in the atlas image.
    ///
    /// The single point at which the layout is known, which is why trimming the
    /// frames touched so little: everything else addresses a frame by index and
    /// measures in tile pixels. A packed set answers with the frame's own
    /// rectangle, an older one with its cell.
    /// </summary>
    public Rect2 Region(string layer, int index)
    {
        if (_rects.TryGetValue(layer, out Rect2I[]? rects))
            return index >= 0 && index < rects.Length
                ? new Rect2(rects[index].Position, rects[index].Size)
                : new Rect2();
        int columns = _columns[layer];
        Vector2I tile = TileOf(layer);
        return new Rect2(
            new Vector2(index % columns * tile.X, index / columns * tile.Y),
            tile);
    }

    /// <summary>
    /// How much was trimmed off the top-left of a frame, in tile pixels.
    ///
    /// Zero for an untrimmed set, which is what lets every draw site take the
    /// same two lines. Subtracting this from the anchor and drawing the frame's
    /// own size puts the pixels exactly where the whole tile would have put
    /// them - including under a class scale, because the offset scales with the
    /// tile it was measured in.
    /// </summary>
    public Vector2 OffsetOf(string layer, int index) =>
        _offs.TryGetValue(layer, out Vector2I[]? offs)
        && index >= 0 && index < offs.Length ? offs[index] : Vector2.Zero;

    /// <summary>The size a frame draws at, in tile pixels: its own if the set
    /// is trimmed, the whole tile otherwise, and zero for a frame that came out
    /// empty - a scar on a plate the hull is standing in front of.</summary>
    public Vector2 SizeOf(string layer, int index) =>
        _rects.TryGetValue(layer, out Rect2I[]? rects)
        && index >= 0 && index < rects.Length ? rects[index].Size
                                              : TileOf(layer);

    /// <summary>
    /// One frame pasted back into a tile-sized image, for the load-time passes
    /// that scan pixels: <see cref="HullSpan"/>, <see cref="FindMuzzles"/>,
    /// <see cref="TakeSmear"/>, <see cref="BeltCentres"/>.
    ///
    /// The mirror of `tank_pipeline._tile_of`, and the same bargain: those four
    /// each did their own cell arithmetic and now none of them does any. They
    /// measure in tile pixels and always did - the anchor, the muzzle point and
    /// the gauge are all in that frame - so handing them a tile keeps every
    /// number they produce the number it was.
    /// </summary>
    public Image TileImage(string layer, int index) =>
        TileFrom(Texture(layer).GetImage(), layer, index);

    /// <summary>The same, from an image already in hand. The scanning passes
    /// walk every frame of a layer, and pulling the whole atlas back off the
    /// GPU once per frame would be the expensive part of loading.</summary>
    private Image TileFrom(Image whole, string layer, int index)
    {
        Vector2I tile = TileOf(layer);
        var into = Image.CreateEmpty(tile.X, tile.Y, false, Image.Format.Rgba8);
        Rect2 region = Region(layer, index);
        if (region.Size.X <= 0 || region.Size.Y <= 0)
            return into;
        Vector2I off = _offs.TryGetValue(layer, out Vector2I[]? offs)
                       && index >= 0 && index < offs.Length
            ? offs[index] : Vector2I.Zero;
        into.BlitRect(whole, new Rect2I((Vector2I)region.Position,
                                        (Vector2I)region.Size), off);
        return into;
    }

    /// <summary>Frame of an effect layer showing <paramref name="phase"/> of the
    /// shot at <paramref name="facing"/> degrees. The grid is one row per phase
    /// and one column per heading, so the index is the obvious product - and
    /// `Count` is angles per phase, not tiles in the file, exactly so that it
    /// stays the number a heading is resolved against.</summary>
    public int EffectFrame(string layer, int phase, double facing)
    {
        int count = CountOf(layer);
        // a layer rendered without headings has exactly one column, and asking
        // which of them faces anywhere is a question with one answer
        int frame = count <= 1 ? 0 : FrameFor(facing);
        return Math.Clamp(phase, 0, PhasesOf(layer) - 1) * count + frame;
    }

    /// <summary>Headings a layer holds; the tank's count for anything absent.</summary>
    public int CountOf(string layer) =>
        _counts.TryGetValue(layer, out int count) ? count : Count;

    /// <summary>The headings this tank was actually rendered at, ascending.</summary>
    public IReadOnlyList<int> RenderedFacings() => _facings.Keys.OrderBy(k => k).ToList();

    private static double Mod(double a, double n) => (a % n + n) % n;
    private static int Mod(int a, int n) => (a % n + n) % n;

    // --- json shapes -------------------------------------------------------

    private sealed class LayerMeta
    {
        [JsonPropertyName("tile")] public int[] Tile { get; set; } = { 256, 256 };
        [JsonPropertyName("grid")] public GridMeta Grid { get; set; } = new();
        [JsonPropertyName("count")] public int Count { get; set; } = 1;
        [JsonPropertyName("phases")] public int Phases { get; set; } = 1;
        [JsonPropertyName("anchor_px")] public double[] AnchorPx { get; set; } = { 128, 128 };
        [JsonPropertyName("units_per_pixel")] public double UnitsPerPixel { get; set; }
        [JsonPropertyName("frames")] public FrameMeta[] Frames { get; set; } = Array.Empty<FrameMeta>();
        [JsonPropertyName("view")] public ViewMeta View { get; set; } = new();
        [JsonPropertyName("hits")] public HitsMeta? Hits { get; set; }

        /// <summary>The engine port, in world units, on the two burning layers.
        /// Absent everywhere else and on any set stamped before
        /// <c>stamp_ports.py</c> - which is what leaves the rendered column as
        /// the fallback rather than a failure.</summary>
        [JsonPropertyName("ports")] public PortsMeta? Ports { get; set; }

        /// <summary>The gun's bore, in world units, on the two shot layers.
        /// Absent everywhere else and on any set stamped before
        /// <c>stamp_bore.py</c> - which is what leaves the rendered flash the
        /// fallback rather than a failure.</summary>
        [JsonPropertyName("bore")] public BoreMeta? Bore { get; set; }

        /// <summary>The angles the tube was laid at, on the barrel layer.
        /// Absent everywhere else and on any set rendered before that layer grew
        /// its second axis - and absent means one rung, which is what keeps the
        /// arithmetic the same on both.</summary>
        [JsonPropertyName("elev")] public ElevMeta? Elev { get; set; }

        /// <summary>Per hull heading: is this layer in front of the turret.
        /// Absent on a set rendered before the turret came out of these layers'
        /// holdouts, and absent is the right default - such a set has the turret
        /// baked into its alpha and must not be ordered over it as well.</summary>
        [JsonPropertyName("over_turret")] public bool[]? OverTurret { get; set; }
        [JsonPropertyName("track")] public TrackMeta[]? Track { get; set; }

        /// <summary>The world z the height layer's 1 and 255 stand for. Absent
        /// on every other layer, and on a height layer rendered before it was
        /// stamped - in which case the map cannot be read and is not.</summary>
        [JsonPropertyName("height_range")] public double[]? HeightRange { get; set; }
    }

    private sealed class TrackMeta
    {
        /// <summary>One link, in world units. See <see cref="TrackPitch"/> for
        /// why it is not in pixels.</summary>
        [JsonPropertyName("pitch")] public double Pitch { get; set; }

        [JsonPropertyName("links")] public int Links { get; set; }

        /// <summary>Whether the belt was laid out so its cycle closes exactly,
        /// rather than measured and hoped for. Carried through so a belt that
        /// will pop at the seam says so in the metadata rather than on
        /// screen.</summary>
        [JsonPropertyName("closes_by_construction")]
        public bool Closes { get; set; }
    }

    private sealed class HitsMeta
    {
        [JsonPropertyName("faces")]
        public Dictionary<string, PlateMeta> Faces { get; set; } = new();
    }

    private sealed class PlateMeta
    {
        /// <summary>Where the plate looks, as an offset from the hull's own
        /// heading. Not a world angle: the hull turns.</summary>
        [JsonPropertyName("bearing")] public double Bearing { get; set; }

        [JsonPropertyName("frames")]
        public HitFrameMeta[] Frames { get; set; } = Array.Empty<HitFrameMeta>();
    }

    private sealed class HitFrameMeta
    {
        [JsonPropertyName("offset")] public double[] Offset { get; set; } = { 0, 0 };
        [JsonPropertyName("tangent")] public double[] Tangent { get; set; } = { 0, 0 };
        [JsonPropertyName("slope")] public double[] Slope { get; set; } = { 0, 0 };
        [JsonPropertyName("normal")] public double[] Normal { get; set; } = { 0, 0 };
        [JsonPropertyName("facing")] public double Facing { get; set; } = 1.0;
    }

    private sealed class PortsMeta
    {
        [JsonPropertyName("hull_length")] public double HullLength { get; set; }
        [JsonPropertyName("list")]
        public PortMeta[] List { get; set; } = Array.Empty<PortMeta>();
    }

    private sealed class BoreMeta
    {
        [JsonPropertyName("point")] public double[] Point { get; set; } = { 0, 0, 0 };
        [JsonPropertyName("dir")] public double[] Dir { get; set; } = { 0, -1, 0 };
        [JsonPropertyName("radius")] public double Radius { get; set; }
        [JsonPropertyName("hull_length")] public double HullLength { get; set; }
    }

    private sealed class ElevMeta
    {
        /// <summary>Degrees, in the order the phases are stored - not sorted.
        /// Index 0 is the level gun by construction; it is looked up anyway,
        /// see <see cref="LevelRung"/>.</summary>
        [JsonPropertyName("table_deg")]
        public double[] TableDeg { get; set; } = Array.Empty<double>();

        /// <summary>The mounting the ladder was rendered on: the point the tube
        /// turns about and the axis it turns around, both in world units. See
        /// <see cref="Trunnion"/> - these are the renderer's own two numbers,
        /// not this side's guess at them.</summary>
        [JsonPropertyName("trunnion")]
        public double[] Trunnion { get; set; } = Array.Empty<double>();

        [JsonPropertyName("axis")]
        public double[] Axis { get; set; } = Array.Empty<double>();
    }

    private sealed class PortMeta
    {
        [JsonPropertyName("point")] public double[] Point { get; set; } = { 0, 0, 0 };
        [JsonPropertyName("dir")] public double[] Dir { get; set; } = { 0, 0, 1 };
        [JsonPropertyName("radius")] public double Radius { get; set; } = 0.1;
    }

    private sealed class ViewMeta
    {
        [JsonPropertyName("elevation")] public double Elevation { get; set; } = 30.0;
        [JsonPropertyName("azimuth")] public double Azimuth { get; set; }
    }

    private sealed class GridMeta
    {
        [JsonPropertyName("columns")] public int Columns { get; set; } = 1;
        [JsonPropertyName("rows")] public int Rows { get; set; } = 1;
    }

    private sealed class FrameMeta
    {
        [JsonPropertyName("index")] public int Index { get; set; }
        [JsonPropertyName("angle")] public double Angle { get; set; }
        [JsonPropertyName("label")] public string? Label { get; set; }

        /// <summary>Where this frame's pixels are in the atlas, as x, y, w, h.
        /// Null for a frame that came out empty, and absent altogether in a set
        /// rendered before the frames were trimmed - see
        /// <see cref="AtlasSet.Region"/>.</summary>
        [JsonPropertyName("rect")] public int[]? Rect { get; set; }

        /// <summary>Where the trimmed box sat inside the tile it was rendered
        /// in. The tile is still the coordinate system; this is what puts the
        /// quad back where the untrimmed frame would have drawn it.</summary>
        [JsonPropertyName("off")] public int[]? Off { get; set; }
    }
}
