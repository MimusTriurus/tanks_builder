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

    public override void _Process(double delta)
    {
        // cheap, and it removes the question of who marks whom dirty: while a
        // shot is live this redraws every frame, and between shots it does not
        if (Tank is not null && Tank.ShotPhase >= 0)
            QueueRedraw();
    }

    public override void _Draw()
    {
        AtlasSet? atlas = Tank?.Atlas;
        if (Tank is null || atlas is null || Tank.ShotPhase < 0
            || Tank.Source != FlashSource.Rendered || !atlas.Has(Layer))
            return;
        int frame = atlas.EffectFrame(Layer, Tank.ShotPhase, Tank.TurretFacing);
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
