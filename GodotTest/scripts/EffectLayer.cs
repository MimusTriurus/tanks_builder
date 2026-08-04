using System;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// One rendered effect layer - the smoke or the fire - drawn on the tank's
/// shared anchor.
///
/// It is a node of its own for one reason: blend mode in Godot lives on the
/// CanvasItem, not on the draw call, and these two need different ones. Fire
/// adds light to what is behind it and smoke takes it away, which is why the
/// renderer keeps them apart in the first place. Trying to draw both from
/// <see cref="TankSprite"/> would mean one blend for both and a choice between
/// smoke that glows or fire that dims what it covers.
///
/// Children draw after their parent, so adding smoke before fire puts the fire
/// on top, which is the order the renderer assumes.
///
/// It holds no state of its own and pulls everything from the tank at draw
/// time - phase, heading, heave, tilt. Pushing it would have been the obvious
/// arrangement and it is wrong here: a child is redrawn when it is marked
/// dirty, not when its parent is, so values set during the parent's draw can
/// land a frame late. Several of these phases are held for a single frame, so
/// one frame late is one phase wrong.
/// </summary>
public sealed partial class EffectLayer : Node2D
{
    public TankSprite? Tank;
    public string Layer = "flash";

    /// <summary>
    /// Which part's heading picks the frame, and whose tilt the layer rides.
    ///
    /// The muzzle flash is bolted to the gun and the exhaust to the engine deck,
    /// so they are indexed by different things. Get it backwards and the tank
    /// trails smoke from wherever the turret happens to be pointing - which
    /// looks like a bug in the atlas rather than in the harness, and would be
    /// hunted for in the wrong place.
    /// </summary>
    public bool FollowsHull;

    /// <summary>
    /// Which clock drives this layer, and so what "phase" means for it.
    ///
    /// The shot has a first frame and a last one and runs off the
    /// <see cref="Hold"/> table; the exhaust and the fire have neither and go
    /// round. The burning pair is two entries rather than one because the flame
    /// and its column run at different rates off the same rendered phases - see
    /// <see cref="BurnLoop"/>.
    /// </summary>
    public enum Clocks
    {
        Shot, Exhaust, BurningSmoke, BurningFire, HitBurst, HitDust, Scar, Track,
        /// <summary>No clock at all: part of the tank, always drawn, one frame
        /// per heading. The turret is here because *where* it composites matters
        /// - see <see cref="Turret"/>.</summary>
        Body,

        /// <summary>
        /// The gun tube recoiling: an event that never goes off.
        ///
        /// A third shape, and worth its own entry rather than being folded into
        /// <see cref="Shot"/>. The shot's clock runs out into -1 and its layers
        /// stop being drawn; this one returns to phase 0, because the tube is
        /// part of the tank and there is no frame without a gun on it. See
        /// <see cref="RecoilLoop"/>.
        /// </summary>
        Recoil,
    }

    public Clocks Clock = Clocks.Shot;

    /// <summary>Whether this layer goes round rather than running out.</summary>
    public bool Loops => Clock == Clocks.Exhaust
                         || Clock == Clocks.BurningSmoke
                         || Clock == Clocks.BurningFire
                         || Clock == Clocks.Track;

    /// <summary>Whether this layer is placed away from the shared anchor.
    ///
    /// Only a shell landing is - the pair that shows it arriving and the mark
    /// it leaves. Everything else is welded to the tank and draws at -anchor
    /// with no offset of any kind, which is the invariant the whole pipeline is
    /// built to keep.
    ///
    /// The two are placed by different things and have to agree anyway: the
    /// burst is one heading-less render dropped wherever the shell landed, and
    /// the mark was rendered on its own plate at every heading and only slides
    /// along it. Same shell, so the same distance along - see
    /// <see cref="TankSprite.ScarOffset"/>.
    /// </summary>
    public bool Placed => Clock == Clocks.HitBurst || Clock == Clocks.HitDust
                          || Clock == Clocks.Scar;

    /// <summary>Where this layer is drawn from, in pixels off the anchor. The
    /// scars work this out per mark instead - see <see cref="DrawMarks"/>.</summary>
    private Vector2 Place => Tank is not null
                             && Clock is Clocks.HitBurst or Clocks.HitDust
        ? Tank.HitOffset : Vector2.Zero;

    public static EffectLayer Additive(string layer) => new()
    {
        Layer = layer,
        Material = new CanvasItemMaterial
        {
            BlendMode = CanvasItemMaterial.BlendModeEnum.Add,
        },
    };

    public static EffectLayer Normal(string layer) => new() { Layer = layer };

    /// <summary>The engine plume: normal alpha like the muzzle smoke, because it
    /// occludes rather than emits, but on the hull's heading and on a clock that
    /// never ends.</summary>
    public static EffectLayer Exhaust(string layer) => new()
    {
        Layer = layer,
        FollowsHull = true,
        Clock = Clocks.Exhaust,
    };

    /// <summary>The burning wreck's column: normal alpha, on the hull's heading.
    /// It has to be added to the tree *before* the flame, because children draw
    /// in order and the whole reason the column exists is to put something dark
    /// behind an additive flame.</summary>
    public static EffectLayer BurningSmoke(string layer) => new()
    {
        Layer = layer,
        FollowsHull = true,
        Clock = Clocks.BurningSmoke,
    };

    /// <summary>The burning wreck's flame: additive like the muzzle flash,
    /// because it emits, but on the hull's heading and on a clock with no
    /// end.</summary>
    public static EffectLayer BurningFire(string layer) => new()
    {
        Layer = layer,
        FollowsHull = true,
        Clock = Clocks.BurningFire,
        Material = new CanvasItemMaterial
        {
            BlendMode = CanvasItemMaterial.BlendModeEnum.Add,
        },
    };

    /// <summary>A shell arriving: dust with normal alpha, and it goes into the
    /// tree before the burst for the burning column's reason - it is what the
    /// additive half is composited against.</summary>
    public static EffectLayer HitDust(string layer) => new()
    {
        Layer = layer,
        FollowsHull = true,
        Clock = Clocks.HitDust,
    };

    /// <summary>A shell arriving: the burst, additive, over its own dust.</summary>
    public static EffectLayer HitBurst(string layer) => new()
    {
        Layer = layer,
        FollowsHull = true,
        Clock = Clocks.HitBurst,
        Material = new CanvasItemMaterial
        {
            BlendMode = CanvasItemMaterial.BlendModeEnum.Add,
        },
    };

    /// <summary>What a shell left on one plate: normal alpha, on the hull's
    /// heading, at the shared anchor.
    ///
    /// Its "phase" is not time. Nothing advances it - the plate holds a level
    /// until it is hit again - so there is no clock here to go with
    /// <see cref="ExhaustLoop"/> and the rest, and nothing to check for
    /// seamlessness. That is the whole difference between a state and an
    /// event, and it is why this is a layer with no loop and no hold table.
    /// </summary>
    public static EffectLayer Scar(string layer) => new()
    {
        Layer = layer,
        FollowsHull = true,
        Clock = Clocks.Scar,
    };

    /// <summary>
    /// One track belt: normal alpha, on the hull's heading, at the shared
    /// anchor.
    ///
    /// The only layer here that is not an effect at all - it is part of the
    /// tank, and it is a layer of its own for exactly one reason: it moves
    /// relative to the hull it is bolted to, which is the one thing a single
    /// hull sprite cannot show. Everything else about it follows the hull,
    /// including its tilt, because a belt that did not lean with the vehicle
    /// would come off it on the first bump.
    ///
    /// Its clock is neither an event nor a timer - see
    /// <see cref="TrackLoop"/>: the belt winds with the ground.
    /// </summary>
    public static EffectLayer Track(string layer) => new()
    {
        Layer = layer,
        FollowsHull = true,
        Clock = Clocks.Track,
    };

    /// <summary>
    /// The turret, and it is a layer rather than a parent draw for exactly one
    /// reason: it has to composite <em>after</em> the belts.
    ///
    /// A belt is lower than the turret but it is not always <em>behind</em> it.
    /// At 30 degrees of depression the sight line off the far belt's top rises
    /// 0.577 per unit travelled, so by the hull's near flank it is well above the
    /// deck - which is why the far belt legitimately shows over the hull, and why
    /// the hull holdout in the belt layer leaves it there. The turret is the one
    /// thing tall enough to be in the way: a quarter of the width along, that
    /// same sight line is barely off the ring, and the turret stands a third of a
    /// unit above it.
    ///
    /// Drawn by the parent, the turret went down before its children and the belt
    /// painted straight over it - measured at up to 1455px of belt inside the
    /// turret's silhouette on HTP, at ten of twelve headings, and the same with
    /// the turret slaved to the hull as with it turned away. It read as the track
    /// showing through the turret, which is what it was.
    ///
    /// Nothing is given up by moving it: the <em>near</em> belt overlaps the
    /// turret by exactly zero at every heading, because it is below and in front,
    /// so no belt pixel that ought to be seen is lost to this. Which is also why
    /// this is a draw-order fix and not a holdout: holding the turret out of the
    /// belt render would bake in the turret's heading, and a skirt this far from
    /// round (0.666 on HTP) does not survive being baked at one angle and drawn
    /// at another.
    /// </summary>
    public static EffectLayer Turret(string layer) => new()
    {
        Layer = layer,
        FollowsHull = false,          // it has its own heading, that is the point
        Clock = Clocks.Body,
    };

    /// <summary>
    /// The gun tube: normal alpha, on the <em>turret's</em> heading, at the
    /// shared anchor.
    ///
    /// The heading is the one thing to get right here, and it is the opposite of
    /// the belts and the exhaust. Those are bolted to the hull; the tube is
    /// bolted to the turret, so it is indexed by where the gun points. Index it
    /// by the hull and the gun swings away from the turret it is mounted in as
    /// soon as the two disagree - which reads as the atlas being broken.
    ///
    /// It rides the turret's tilt too, for the same reason: whatever the hull
    /// does under it, the tube is part of the mount. That falls out of
    /// <c>FollowsHull = false</c> - the same flag decides the frame and the
    /// shear, which is what keeps the gun from leaving the turret face on a
    /// bump.
    ///
    /// Drawn straight after the turret, so the tube is over the mantlet it slides
    /// into rather than under it, and before everything that happens *to* the
    /// tank. Normal alpha: it is painted metal, not light.
    /// </summary>
    public static EffectLayer Barrel(string layer) => new()
    {
        Layer = layer,
        FollowsHull = false,
        Clock = Clocks.Recoil,
    };

    /// <summary>The plate a scar layer belongs to, or "" for any other
    /// layer.</summary>
    public static string FaceOf(string layer) =>
        layer.StartsWith("scar_", StringComparison.Ordinal)
            ? layer["scar_".Length..] : "";

    public override void _Ready() => TextureFilter = TextureFilterEnum.Linear;

    /// <summary>The phase this layer should be showing, or -1 for nothing.</summary>
    private int Wanted
    {
        get
        {
            if (Tank is null || Tank.Atlas?.Has(Layer) != true)
                return -1;
            return Clock switch
            {
                Clocks.Exhaust => Tank.ShowExhaust ? Tank.ExhaustPhase : -1,
                Clocks.BurningSmoke => Tank.Burning ? Tank.BurnPhase : -1,
                Clocks.BurningFire => Tank.Burning ? Tank.FirePhase : -1,
                Clocks.HitBurst or Clocks.HitDust => Tank.HitPhase,
                Clocks.Track => Tank.ShowTracks ? Tank.TrackPhase : -1,
                // No phase axis, so frame 0 of one - EffectFrame clamps the
                // phase against PhasesOf and lands on the heading column.
                Clocks.Body => Tank.ShowTurret ? 0 : -1,
                // Rest is a pose, so this never asks for -1 while the turret is
                // up: the tube is part of the tank. It hides with the turret
                // because a gun without the mount it comes out of is not a
                // halfway state worth drawing.
                Clocks.Recoil => Tank.ShowTurret ? Tank.RecoilPhase : -1,
                // A plate carrying several marks has no single phase, so this
                // is only "is there anything to draw". Which is all the redraw
                // gate needs: what the layer actually puts on screen depends on
                // the hull's heading as much as on the damage.
                Clocks.Scar => Tank.MarksOn(FaceOf(Layer)).Count > 0 ? 0 : -1,
                _ => Tank.ActiveSource == FlashSource.Rendered
                    ? Tank.ShotPhase : -1,
            };
        }
    }

    /// <summary>What the last <see cref="_Draw"/> actually put on screen.</summary>
    private int _shown = -1;

    /// <summary>
    /// Redraw while there is something to show, and once more after there is
    /// not.
    ///
    /// That second clause is the whole of it. Gating on "a shot is live" alone
    /// looks right and leaves the last phase on screen for good: the moment the
    /// phase goes to -1 the layer stops asking to be redrawn, so it never gets
    /// the chance to draw nothing. The parent marking itself dirty does not
    /// help - a child CanvasItem is redrawn when *it* is dirty, not when its
    /// parent is. It showed up as smoke that never cleared, because by the last
    /// phase the fire has all but gone and the smoke has not.
    ///
    /// Comparing what is wanted against what was drawn covers the other two
    /// ways the layer goes quiet as well: swapping to the painted sheet
    /// mid-shot, and switching to a tank that has no rendered flash.
    /// </summary>
    public override void _Process(double delta)
    {
        // A hit on a plate turned away from the camera is behind the tank, and
        // Godot's own back-to-front flag is the honest way to say so: a child
        // with it set draws before its parent, and the two hit layers keep
        // their order among themselves. The alternative - baking the occlusion
        // into the layer's alpha with a holdout, as every other effect does -
        // is not available here, because this layer is rendered once and used
        // at all twelve headings.
        // The hit pair only - the mark is placed too and must never go behind,
        // because the renderer already held the tank out of it: a plate facing
        // away is an empty frame, not a frame to draw somewhere else.
        if (Tank is not null && Clock is Clocks.HitBurst or Clocks.HitDust)
            ShowBehindParent = Tank.HitBehind;
        if (NeedsRedraw())
            QueueRedraw();
    }

    /// <summary>Whether this layer has to be redrawn now.</summary>
    public bool NeedsRedraw() => MustRedraw(Wanted, _shown);

    /// <summary>What this layer would put on screen: a phase, or -1 for
    /// nothing. For the scars it is only whether the plate has anything on
    /// it - see <see cref="Wanted"/>.</summary>
    public int Showing => Wanted;

    /// <summary>
    /// Whether a layer currently showing <paramref name="shown"/> has to be
    /// redrawn when <paramref name="wanted"/> is what it should show. Exposed
    /// so the two cases that were wrong can be asserted rather than remembered.
    ///
    /// Note that it is true when the two agree. That looks wasteful and is the
    /// whole point: what a layer draws is a frame chosen by a *heading*, not
    /// just by a phase, so "nothing about the damage changed" is no reason to
    /// keep the old frame. Gating the scars on their own damage counter instead
    /// left the mark showing the plate as it looked at the heading the shell
    /// arrived on, frozen through every turn after it.
    /// </summary>
    public static bool MustRedraw(int wanted, int shown) => wanted >= 0 || shown >= 0;

    public override void _Draw()
    {
        AtlasSet? atlas = Tank?.Atlas;
        if (Clock == Clocks.Scar)
        {
            DrawMarks(atlas);
            return;
        }
        int phase = Wanted;
        _shown = phase;
        if (Tank is null || atlas is null || phase < 0)
            return;
        bool turret = !FollowsHull;
        double facing = FollowsHull ? Tank.HullFacing : Tank.TurretFacing;
        int frame = atlas.EffectFrame(Layer, phase, facing);
        // its *own* anchor: the flash layers are rendered at a wider tile than
        // the tank so a long flash is not clipped at the frame edge, and the
        // anchor scales with the tile. Using the hull's would throw it 64px out.
        // The plume is rendered at the tank's own tile and needs none of that -
        // which is exactly why the anchor is read per layer rather than per set.
        Vector2 anchor = atlas.AnchorOf(Layer);
        // Density is pulled here rather than pushed into Modulate for the same
        // reason the phase is: a child redraws on its own schedule, and a value
        // written during the parent's draw can be a frame stale.
        // Only the exhaust is thinned: its density is the throttle showing. The
        // fire has no such input - see BurnLoop.
        var tint = new Color(1.0f, 1.0f, 1.0f,
            Clock == Clocks.Exhaust ? Tank.ExhaustDensity : 1.0f);
        // The one layer drawn away from the anchor, and it is pulled here for
        // the reason the phase is - a child redraws on its own schedule. So is
        // the calibre, which is a scale on the rect rather than on the node:
        // scaling the node would scale the offset that puts it on the plate.
        Vector2 place = Place;
        // Calibre is the shell's, so it scales the shell - not the hole. The
        // mark was rendered at the size it is and a bigger round makes a
        // deeper level, not a stretched decal.
        float size = Clock is Clocks.HitBurst or Clocks.HitDust
            ? Tank.HitScale : 1.0f;
        DrawSetTransformMatrix(Tank.ShearFor(turret));
        DrawTextureRectRegion(atlas.Texture(Layer),
            Frame(anchor, atlas.TileOf(Layer),
                place + new Vector2(0.0f, Tank.HeaveFor(turret)), size),
            atlas.Region(Layer, frame), tint);
        DrawSetTransformMatrix(Transform2D.Identity);
    }

    /// <summary>
    /// Every mark this plate is carrying, oldest first.
    ///
    /// The one layer drawn more than once, and the reason is that rounds do not
    /// land on top of each other: three hits on a plate are three holes at the
    /// three places they arrived. One node still, because they are all the same
    /// atlas at the same heading - only the frame and the offset differ.
    ///
    /// Oldest first so a later, deeper mark overlaps an earlier one rather than
    /// being covered by it. They rarely touch, but when they do the newer round
    /// is the one that took the metal away.
    /// </summary>
    private void DrawMarks(AtlasSet? atlas)
    {
        _shown = Wanted;
        if (Tank is null || atlas is null || _shown < 0)
            return;
        string face = FaceOf(Layer);
        Vector2 anchor = atlas.AnchorOf(Layer);
        Vector2 tile = atlas.TileOf(Layer);
        Vector2 heave = new(0.0f, Tank.HeaveFor(false));
        DrawSetTransformMatrix(Tank.ShearFor(false));
        foreach (TankSprite.Mark mark in Tank.MarksOn(face))
        {
            int frame = atlas.EffectFrame(Layer, mark.Level, Tank.HullFacing);
            DrawTextureRectRegion(atlas.Texture(Layer),
                Frame(anchor, tile, Tank.MarkOffset(face, mark) + heave, 1.0f),
                atlas.Region(Layer, frame), Colors.White);
        }
        DrawSetTransformMatrix(Transform2D.Identity);
    }

    /// <summary>
    /// Where a tile goes on screen: its own <paramref name="anchor"/> landing on
    /// <paramref name="at"/>, at <paramref name="size"/> times the size it was
    /// rendered.
    ///
    /// Separated out so the one thing that has to hold can be asserted rather
    /// than read: whatever the scale, the anchor stays on <paramref name="at"/>.
    /// Scaling a rect by its origin instead would walk a hit off its plate by a
    /// fraction of the tile - and the tile is 192px, so half a calibre is
    /// nearly fifty pixels of walk on a plate 49px tall.
    /// </summary>
    public static Rect2 Frame(Vector2 anchor, Vector2 tile, Vector2 at, float size)
        => new(at - anchor * size, tile * size);

    /// <summary>
    /// How many screen frames each phase is held for.
    ///
    /// The rendered set has eight phases against the painted sheet's sixteen
    /// frames, but they cover the same event and should take the same time, or
    /// an A/B between them measures the tempo instead of the artwork. Both come
    /// to 34 frames - 0.57s at 60fps.
    ///
    /// Uneven for the same reason the sheet's table is: the flash is over in a
    /// tenth of a second and the smoke hangs about for half of one. The early
    /// phases are where the fire lives, so they are short.
    ///
    /// The exhaust does not use any of this. A table of held frames ending in a
    /// -1 is the shape of an event, and the plume is a loop with no first frame
    /// to start at and no last one to run out - see <see cref="ExhaustLoop"/>.
    /// </summary>
    public static readonly int[] Hold = { 1, 2, 2, 3, 4, 6, 7, 9 };

    public static int Duration
    {
        get
        {
            int total = 0;
            foreach (int hold in Hold)
                total += hold;
            return total;
        }
    }

    /// <summary>Phase to show <paramref name="elapsed"/> screen frames into a
    /// shot, or -1 once it is over.</summary>
    public static int PhaseAt(int elapsed)
    {
        if (elapsed < 0)
            return -1;
        int t = 0;
        for (int i = 0; i < Hold.Length; i++)
        {
            t += Hold[i];
            if (elapsed < t)
                return i;
        }
        return -1;
    }
}
