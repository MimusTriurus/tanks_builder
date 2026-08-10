using Godot;

namespace TankSpriteTest;

/// <summary>
/// The ground that stands in front of one tank and above it, repainted over
/// that tank.
///
/// <b>Why a node per tank rather than a z index per cell.</b> The rule a board
/// with height needs is two comparisons - a cell hides a tank when it is nearer
/// <i>and</i> higher - and a z index is one number. Every single-key arrangement
/// tried here gets one of the four cases wrong and each wrong case is a picture
/// somebody has already reported: depth alone lets the flat cell being driven
/// into cut the hull off at the tracks, and level-then-depth lets the hill
/// standing behind a tank paint its wall across the turret.
///
/// The two comparisons are per tank, so the answer belongs to the tank. This
/// node is a child of one tank's sprite at <c>ZIndex = 1</c>, which puts it
/// immediately above that tank and below the next thing at any other depth -
/// so a cell repainted for the tank it hides cannot land on a different tank
/// that happens to stand in front of it.
///
/// <b>The cell is drawn twice and that is deliberate.</b> The field paints the
/// whole board under everything, as it always has; this repaints the handful of
/// cells that have to come back over. Skipping them in the field pass instead
/// would leave a hole wherever a cell is above one tank and below another, which
/// is the same cell.
///
/// <b>And drawing it twice is what puts a ceiling on how many of these there can
/// be.</b> The plate's rim is anti-aliased, so a pixel at alpha <c>a</c> comes
/// out at <c>2a - a^2</c> after a second pass and converges on the art's own
/// border colour after several. With three tanks it is one extra pass on a
/// handful of cells and does not show. Hanging one of these off every tree was
/// tried and is why this paragraph exists: on a wooded board dozens of caps
/// repaint the same cell, the rim saturates, and the hill comes out drawn in
/// white - 221 near-white pixels on a board that had none. Anything that wants
/// this for many observers at once needs the cells promoted properly rather than
/// repainted per observer.
///
/// <b>The parent's transform is undone rather than avoided.</b> The sprite node
/// carries the class size on its <c>Scale</c>, so ground drawn in its space
/// would be drawn at 0.85x or 1.15x. Composing the field's transform with the
/// inverse of this node's global one puts the pen back in field space exactly,
/// which is what lets <see cref="HexField.PaintCell"/> be the one description of
/// how a cell is drawn.
///
/// On a board with no relief <see cref="HexField.Occluders"/> returns nothing
/// and this draws nothing at all - which is the whole of why the feature can
/// land in a working bench.
/// </summary>
public sealed partial class ReliefCap : Node2D
{
    public HexField? Field;

    /// <summary>The belt marks, so a repainted cell comes back with the ruts that
    /// were on it. Null leaves the ground repainted bare, which is how this
    /// behaved when it was reported.</summary>
    public TrackMarks? Marks;

    /// <summary>Where the tank stands, in the field's local space, with the
    /// height taken out - the ground row. Set by the harness each frame, because
    /// what this needs is where the tank is now and not the cell it last
    /// reached: a tank spends most of a step between two cells, and that is
    /// exactly the frame being looked at.</summary>
    public float FlatRow;

    /// <summary>How far off the datum the tank is standing, in screen px - the
    /// height it is drawn at. It was the higher of the two cells it is crossing,
    /// which called it up on the crown at the first pixel of a climb; see
    /// <see cref="HexField.HeightBetween"/>.</summary>
    public float Standing;

    /// <summary>What this tank occupies, in the field's local space. A cell that
    /// cannot touch it is not repainted however near and high it stands - see
    /// <see cref="HexField.Occluders"/> for the board that blinks without it.
    /// Generous: the frame the sprite is drawn from, at the class's size, about
    /// the anchor. Too wide costs one repaint of a cell that covers nothing; too
    /// narrow drops one that covers something.</summary>
    public Rect2 Box;

    public override void _Ready()
    {
        // Relative, so this rides whatever depth the harness gives the tank
        // rather than needing to be told it a second time.
        ZIndex = 1;
        ZAsRelative = true;
    }

    /// <summary>Redrawn every frame the board has height, because what this
    /// paints depends on things that change without the tank moving: the route
    /// marks on the cells, and the other tanks. Gated, so a flat board is not
    /// asked to redraw an empty node sixty times a second.</summary>
    public override void _Process(double delta)
    {
        if (Field is null || !Field.HasRelief)
            return;
        // The same sampling the field uses, because this is the same cell drawn
        // again and the two versions have to be the same pixels. Ground art
        // painted at four times the frame is minified to a quarter, and the field
        // asks for mipmaps so the gravel does not crawl as the board pans; this
        // node hangs off a tank and would otherwise inherit whatever its parent
        // chain happens to say, which is the project default.
        //
        // ParentNode is resolved rather than copied for that reason: the field's
        // own chain ends at the project default too, and the project samples
        // canvas textures nearest.
        TextureFilter = Field.TextureFilter == TextureFilterEnum.ParentNode
            ? TextureFilterEnum.Nearest
            : Field.TextureFilter;
        QueueRedraw();
    }

    public override void _Draw()
    {
        if (Field is null || !Field.HasRelief)
            return;
        var over = Field.Occluders(FlatRow, Standing, Box);
        if (over.Count == 0)
            return;
        DrawSetTransformMatrix(GetGlobalTransform().AffineInverse()
                               * Field.GetGlobalTransform());
        // Clipped, because nothing is painted after this: the field drew the
        // whole board in order and this puts a handful of cells back on top, so
        // a cell's full column would undo the cells in front of it that had
        // covered its lower half. See HexField.VisibleSide.
        foreach (Vector2I cell in over)
            Field.PaintCell(this, cell, Field.InkFor(cell),
                            Field.VisibleSide(cell, Standing));

        // And the belt marks that were on those cells, which the terrain pass
        // does not carry - see TrackMarks.PaintOn. In the marks' own space, the
        // same trick the field gets above, so neither has to know where the other
        // node sits.
        if (Marks is null)
            return;
        DrawSetTransformMatrix(GetGlobalTransform().AffineInverse()
                               * Marks.GetGlobalTransform());
        Marks.PaintOn(this, Field, Marks.GlobalPosition - Field.GlobalPosition,
                      over);
    }
}
