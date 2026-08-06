using Godot;

namespace TankSpriteTest;

/// <summary>
/// A ring on the ground under the tank being driven.
///
/// Three tanks on one field need one of them marked, and where the mark goes was
/// the whole question. Two answers were ruled out before this one:
///
/// - <b>Outlining the sprite.</b> A tank is eight layers, and an outline taken off
///   the alpha would trace the exhaust plume and the burning column too. It also
///   works in the one channel the bench exists to judge - what the rendered sprite
///   looks like.
/// - <b>Dimming the other two.</b> Fine in a game, a lie here: whether a plume
///   shifts the picture and whether the flame stays red are measured against the
///   ground, and a dimmed tank is a different picture.
///
/// The ground is the honest place. Nothing of the sprite is touched, the mark reads
/// as "this cell is yours" - which is the language the grid is already in - and
/// because it is drawn under every tank, its far side passes behind the hull. That
/// last part is not a side effect: it is what makes the ring read as lying on the
/// ground rather than as a decoration floating over it.
///
/// Sized from the cell, never from the tank. It marks where the tank stands, and a
/// ring that grew with the class would be a second statement about size competing
/// with the one the tanks are making.
/// </summary>
public sealed partial class SelectionRing : Node2D
{
    /// <summary>The tank to mark, or null to draw nothing.</summary>
    public Vehicle? Target;

    /// <summary>The field, for the cell's drawn size.</summary>
    public HexField Field = null!;

    /// <summary>How far inside the cell's edge the ring sits, as a fraction of the
    /// cell. Fully on the edge it reads as a grid line rather than as a mark, and
    /// the neighbouring cell's edge is drawn there too.</summary>
    public float Inset = 0.86f;

    /// <summary>
    /// Cyan, because the two colours already on the ground are the path's amber and
    /// its yellow-green, and a mark that has to be told apart from the path is not
    /// a mark. The dark line under it is the same lesson the additive effect layers
    /// taught: a colour tuned on the pale tile disappears on the amber one, so the
    /// ring carries its own contrast rather than borrowing the ground's.
    ///
    /// A field rather than a constant so the same ring can mark the tank being
    /// shot at, in the red the lanes are drawn in. One class for both because
    /// they are the same statement about the same kind of thing - which tank on
    /// the ground this is about - and two classes would be two chances for the
    /// mark to stop lying on the ground.
    /// </summary>
    public Color Ink = new(0.30f, 0.85f, 1.0f);
    public Color Backing = new(0.0f, 0.1f, 0.15f, 0.55f);

    /// <summary>The red one, for the target. Named here so the shade is chosen
    /// once beside the cyan it has to be told apart from.</summary>
    public static readonly Color Hostile = new(1.0f, 0.36f, 0.30f);

    /// <summary>
    /// The cell's outline as a closed polyline, centred on the origin.
    ///
    /// A flat-top hexagon of circumradius R has its vertices at (+-R, 0) and
    /// (+-R/2, +-sqrt(3)R/2); the isometric view squashes the ground plane
    /// vertically, and the drawn tile is exactly that squash - so in terms of the
    /// tile's own drawn width and height the vertices are (+-w/2, 0) and
    /// (+-w/4, +-h/2), with no trigonometry and no elevation to keep in agreement
    /// with the renderer. Static so the shape can be asserted without a node.
    /// </summary>
    public static Vector2[] Outline(float width, float height, float inset)
    {
        float w = width * inset, h = height * inset;
        return new[]
        {
            new Vector2(-w * 0.5f, 0.0f),
            new Vector2(-w * 0.25f, -h * 0.5f),
            new Vector2(w * 0.25f, -h * 0.5f),
            new Vector2(w * 0.5f, 0.0f),
            new Vector2(w * 0.25f, h * 0.5f),
            new Vector2(-w * 0.25f, h * 0.5f),
            new Vector2(-w * 0.5f, 0.0f),
        };
    }

    /// <summary>Under every tank and over the field. The tanks take their z from
    /// where they stand, which is always positive on this board, so one step above
    /// the field's own -100 puts the ring between them.</summary>
    public override void _Ready() => ZIndex = -99;

    /// <summary>
    /// Follows the tank's contact patch every frame rather than snapping to the
    /// cell it last reached: a tank spends most of an order between two cells, and
    /// a mark left on the cell behind it reads as the selection lagging.
    /// </summary>
    public override void _Process(double _)
    {
        Vector2 want = Target?.GroundPoint ?? Vector2.Zero;
        bool show = Target is not null && Field.Atlas is not null;
        if (show == _drawn && (!show || want == Position))
            return;
        Position = want;
        QueueRedraw();
    }

    private bool _drawn;

    /// <summary>Whether the last draw put anything on screen. Exposed so "no
    /// target, no mark" is asserted rather than assumed.</summary>
    public bool Drawn => _drawn;

    public override void _Draw()
    {
        _drawn = Target is not null && Field.Atlas is not null;
        if (!_drawn)
            return;
        Vector2[] points = Outline(Field.Atlas!.HexRect.Size.X,
                                  Field.Atlas.HexRect.Size.Y, Inset);
        DrawPolyline(points, Backing, 5.0f, true);
        DrawPolyline(points, Ink, 2.0f, true);
    }
}
