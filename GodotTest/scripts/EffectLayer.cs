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

    /// <summary>Whether this layer runs on the looping <see cref="ExhaustLoop"/>
    /// clock instead of the shot's one-off <see cref="Hold"/> table.</summary>
    public bool Loops;

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
        Loops = true,
    };

    public override void _Ready() => TextureFilter = TextureFilterEnum.Linear;

    /// <summary>The phase this layer should be showing, or -1 for nothing.</summary>
    private int Wanted
    {
        get
        {
            if (Tank is null || Tank.Atlas?.Has(Layer) != true)
                return -1;
            if (Loops)
                return Tank.ShowExhaust ? Tank.ExhaustPhase : -1;
            return Tank.ActiveSource == FlashSource.Rendered ? Tank.ShotPhase : -1;
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
        if (MustRedraw(Wanted, _shown))
            QueueRedraw();
    }

    /// <summary>Whether a layer currently showing <paramref name="shown"/> has
    /// to be redrawn when <paramref name="wanted"/> is what it should show.
    /// Exposed so the case that was wrong can be asserted rather than
    /// remembered.</summary>
    public static bool MustRedraw(int wanted, int shown) => wanted >= 0 || shown >= 0;

    public override void _Draw()
    {
        AtlasSet? atlas = Tank?.Atlas;
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
        var tint = new Color(1.0f, 1.0f, 1.0f,
            Loops ? Tank.ExhaustDensity : 1.0f);
        DrawSetTransformMatrix(Tank.ShearFor(turret));
        DrawTextureRectRegion(atlas.Texture(Layer),
            new Rect2(-anchor + new Vector2(0.0f, Tank.HeaveFor(turret)),
                atlas.TileOf(Layer)),
            atlas.Region(Layer, frame), tint);
        DrawSetTransformMatrix(Transform2D.Identity);
    }

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
