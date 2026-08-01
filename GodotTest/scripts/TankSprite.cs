using System;
using Godot;

namespace TankSpriteTest;

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

    public override void _Draw()
    {
        if (Atlas is null)
            return;
        if (ShowHull)
            DrawLayer("hull", HullFacing);
        if (ShowTurret)
            DrawLayer("turret", TurretFacing);
        if (ShowAxis)
        {
            var colour = new Color(1.0f, 0.2f, 0.2f, 0.9f);
            const float arm = 10.0f;
            DrawLine(new Vector2(-arm, 0), new Vector2(arm, 0), colour);
            DrawLine(new Vector2(0, -arm), new Vector2(0, arm), colour);
        }
    }

    private void DrawLayer(string layer, double facing)
    {
        int index = Atlas!.FrameFor(facing);
        DrawTextureRectRegion(Atlas.Texture(layer),
            new Rect2(-Atlas.Anchor, Atlas.Tile),
            Atlas.Region(layer, index));
    }

    public Vector2I FrameIndices() => Atlas is null
        ? Vector2I.Zero
        : new Vector2I(Atlas.FrameFor(HullFacing), Atlas.FrameFor(TurretFacing));
}
