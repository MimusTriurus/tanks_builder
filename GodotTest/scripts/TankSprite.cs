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
    /// sheet is the fallback rather than the loser: HT and LT have no separated
    /// barrel yet, so they have no rendered flash to show.</summary>
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
    public static readonly string[] LayerOrder =
        AtlasSet.ScarNames.Concat(new[]
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

    /// <summary>Take one more hit on a plate, at <paramref name="along"/> of the
    /// way across it. Returns the level of the mark it left, or -1 if this tank
    /// has no marks to leave.</summary>
    public int Damage(string face, float along = 0.0f)
    {
        if (Atlas?.HasScars != true || Atlas.ScarLayer(face) == "")
            return -1;
        if (!_scars.TryGetValue(face, out List<Mark>? marks))
            _scars[face] = marks = new List<Mark>();
        int level = Math.Min(marks.Count, Atlas.ScarLevels - 1);
        marks.Add(new Mark(level, along));
        while (marks.Count > Atlas.ScarLevels)
            marks.RemoveAt(0);
        QueueRedraw();
        return level;
    }

    /// <summary>Back to a tank off the line.</summary>
    public void Repair()
    {
        _scars.Clear();
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

    /// <summary>Tilt a layer receives. Exposed so the stabilisation policy can
    /// be asserted rather than read off the screen.</summary>
    public Vector2 TiltFor(bool turret) =>
        // Recoil first, and outside the stabiliser: a two-plane stabiliser
        // rejects the hull moving under the gun, and cannot reject the gun's own
        // firing impulse - it arrives through the mount as fast as the gun does.
        // A hull rocking under a turret nailed in place would look broken on the
        // one event where the turret is what is being watched.
        TiltDisplacement(RecoilPitch, RecoilRoll)
        + (turret && TurretStabilised
            ? Vector2.Zero
            : TiltDisplacement(Pitch + TremblePitch, Roll + TrembleRoll));

    /// <summary>Heave a layer receives. Never depends on the layer - see
    /// <see cref="TurretStabilised"/>.</summary>
    public int HeaveFor(bool turret) => Shake;

    internal Transform2D ShearFor(bool turret)
    {
        float groundY = Atlas!.GroundOffset.Y;
        Vector2 d = TiltFor(turret);
        return new Transform2D(
            new Vector2(1.0f, 0.0f),                            // x is untouched
            new Vector2(-d.X, 1.0f - d.Y),
            new Vector2(groundY * d.X, groundY * d.Y));
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
        if (ShowTurret)
            DrawLayerTilted("turret", TurretFacing, tilted, true);
        // the rendered flash is drawn by the child layers, which need their own
        // blend modes; only the painted sheet is drawn from here
        if (ActiveSource == FlashSource.Sheet && FlashFrame >= 0
            && Flash?.Texture is not null)
            DrawFlash();
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
        bool shear = tilted && TiltFor(turret) != Vector2.Zero;
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
    private void DrawFlash()
    {
        Vector2 along = Atlas!.GroundDirection(TurretFacing);
        Vector2 across = new Vector2(-along.Y, along.X).Normalized();
        Vector2 muzzle = Atlas.Muzzle(Atlas.FrameFor(TurretFacing)) - Atlas.Anchor
                         + new Vector2(0.0f, HeaveFor(true));
        var place = new Transform2D(along * FlashScale, across * FlashScale, muzzle);

        DrawSetTransformMatrix(ShearFor(true) * place);
        DrawTextureRectRegion(Flash!.Texture,
            new Rect2(-Flash.Origin, Flash.Tile), Flash.Region(FlashFrame));
        DrawSetTransformMatrix(Transform2D.Identity);
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
