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

    public static EffectLayer Additive(string layer) => new()
    {
        Layer = layer,
        Material = new CanvasItemMaterial
        {
            BlendMode = CanvasItemMaterial.BlendModeEnum.Add,
        },
    };

    public static EffectLayer Normal(string layer) => new() { Layer = layer };

    public override void _Ready() => TextureFilter = TextureFilterEnum.Linear;

    /// <summary>The phase this layer should be showing, or -1 for nothing.</summary>
    private int Wanted =>
        Tank is not null && Tank.ActiveSource == FlashSource.Rendered
        && Tank.Atlas?.Has(Layer) == true
            ? Tank.ShotPhase
            : -1;

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
        int frame = atlas.EffectFrame(Layer, phase, Tank.TurretFacing);
        // its *own* anchor: the effect layers are rendered at a wider tile than
        // the tank so a long flash is not clipped at the frame edge, and the
        // anchor scales with the tile. Using the hull's would throw it 64px out.
        Vector2 anchor = atlas.AnchorOf(Layer);
        DrawSetTransformMatrix(Tank.ShearFor(true));
        DrawTextureRectRegion(atlas.Texture(Layer),
            new Rect2(-anchor + new Vector2(0.0f, Tank.HeaveFor(true)),
                atlas.TileOf(Layer)),
            atlas.Region(Layer, frame));
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
