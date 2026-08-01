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

    /// <summary>Body pitch as a shear amplitude; positive is nose down.
    /// See <see cref="PitchTransform"/> for why a shear and not a rotation.</summary>
    public double Pitch;

    /// <summary>Whole pixels of vertical jolt from the ground. Applied to the
    /// draw rect rather than to Position, which the movement code compares
    /// against cell anchors and must stay exact.</summary>
    public int Shake;

    /// <summary>Roll, as a shear amplitude, positive to the right of the
    /// heading. Shares the machinery with <see cref="Pitch"/>.</summary>
    public double Roll;

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
    /// The seam still parts a little, by tilt times the ring's height above the
    /// ground: about a pixel at the amplitudes in use, which the turret's skirt
    /// covers.
    /// </summary>
    public bool TurretStabilised = true;

    /// <summary>Pitch and roll are the same operation in different directions -
    /// both tilt the body about a horizontal axis through the contact point, so
    /// both displace a point by an amount proportional to its height. Summing
    /// the two directions and shearing once is exact, not an approximation, and
    /// avoids composing two transforms.</summary>
    private Vector2 TiltDisplacement()
    {
        Vector2 displacement =
            Atlas!.GroundDirection(HullFacing) * (float)Pitch
            + Atlas.GroundDirection(HullFacing + 90.0) * (float)Roll;
        displacement.Y *= SquashDamping;
        return displacement;
    }

    /// <summary>Tilt a layer receives. Exposed so the stabilisation policy can
    /// be asserted rather than read off the screen.</summary>
    public Vector2 TiltFor(bool turret) =>
        turret && TurretStabilised ? Vector2.Zero : TiltDisplacement();

    /// <summary>Heave a layer receives. Never depends on the layer - see
    /// <see cref="TurretStabilised"/>.</summary>
    public int HeaveFor(bool turret) => Shake;

    private Transform2D ShearFor(bool turret)
    {
        float groundY = Atlas!.GroundOffset.Y;
        Vector2 d = TiltFor(turret);
        return new Transform2D(
            new Vector2(1.0f, 0.0f),                            // x is untouched
            new Vector2(-d.X, 1.0f - d.Y),
            new Vector2(groundY * d.X, groundY * d.Y));
    }

    public override void _Ready() =>
        // These are 3D renders, not pixel art, so linear costs nothing at 1:1 -
        // sample centres line up and it is bit-identical to nearest. It only
        // matters once the pitch shear resamples: with nearest, whole pixel rows
        // snap sideways one at a time as the shear changes, and that shimmer
        // reads as wobble on top of whatever the pitch is doing.
        TextureFilter = TextureFilterEnum.Linear;

    public override void _Draw()
    {
        if (Atlas is null)
            return;
        bool tilted = Math.Abs(Pitch) > 1e-6 || Math.Abs(Roll) > 1e-6;
        if (ShowHull)
            DrawLayerTilted("hull", HullFacing, tilted, false);
        if (ShowTurret)
            DrawLayerTilted("turret", TurretFacing, tilted, true);
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
