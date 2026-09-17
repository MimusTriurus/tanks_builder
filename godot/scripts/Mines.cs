using System;
using System.Collections.Generic;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// Where the mine on a cell actually lies, and whether a hull has come over it.
///
/// <b>A mine is a point, and the cell is only where it is kept.</b> Everything
/// else on this board that a cover carries is spread over the whole hex - trees
/// are scattered, masonry stands on the edges, a trench is the cell - but a mine
/// goes off <em>under a tank</em>, and "under" is a question about two rectangles
/// and a point rather than about a hex. So the point is settled here, once, and
/// the two things that need it read the same answer: the marker that is drawn on
/// the board and the burst that goes off when somebody drives over it. A marker
/// and a blast a third of a cell apart would be the one failure this whole layer
/// is written to avoid, and it would look exactly like the mine having moved.
///
/// <b>Hashed off the cell, so it does not move.</b> Not drawn at load and kept,
/// because then a reset, a reload or a second scene would each get their own
/// board; not random per frame for the obvious reason. Hashed, it is the same
/// point in every run, which is also what makes a capture of it comparable.
///
/// <b>Nearer the middle than the rim</b> - <see cref="Inset"/>. A mine on the
/// rim reads as belonging to the neighbour and can end up under a tank that is
/// standing on the cell next door; a mine exactly at the centre is a mine that
/// has no place at all, and six of them in a row read as a stamp. A third of the
/// way out is off-centre enough to be a thing and far enough in to be this
/// cell's.
///
/// The rules this serves are GDD field.md, "мины": visible to both players
/// always, triggered by driving through the hex as well as by stopping on it,
/// spent afterwards, and hurting only the tank on their own cell.
/// </summary>
public static class Mines
{
    /// <summary>How far out of the middle a mine may lie, as a share of the way
    /// to the rim. See the class note.</summary>
    public const float Inset = 0.35f;

    /// <summary>How wide a hull is against its own length. A tank is about half
    /// as broad as it is long, and the atlas measures only the length
    /// (<see cref="AtlasSet.HullSpan"/>) - the belts are measured, but across
    /// the screen rather than across the hull. A share rather than a second
    /// measurement, because what this decides is a moment during a drive that is
    /// straight down the hull's own axis anyway.</summary>
    public const float Broad = 0.55f;

    /// <summary>
    /// Where the mine on this cell lies, as an offset from the cell's centre in
    /// the board's own drawn space.
    ///
    /// <paramref name="tile"/> is the drawn hexagon's bounding box - the offset
    /// is taken on the ellipse inscribed in it rather than on a circle, so the
    /// scatter is squashed exactly as the hexagon is and a mine near the rim is
    /// near the rim at every camera this board has had.
    ///
    /// The root on the reach is what makes it even over the area: without it
    /// every second mine sits in the middle sixth of the disc, which reads as a
    /// stamp with a wobble.
    ///
    /// Its own static, taking numbers rather than a field, so the scatter can be
    /// asserted without a board - <see cref="Vehicle.WadingAt"/>'s reason.
    /// </summary>
    public static Vector2 Offset(Vector2I cell, Vector2 tile)
    {
        int key = cell.Y * 1021 + cell.X;
        float turn = Plumes.Hash01(key, 7331) * MathF.Tau;
        float reach = Inset * MathF.Sqrt(Plumes.Hash01(key, 104729));
        return new Vector2(MathF.Cos(turn) * tile.X, MathF.Sin(turn) * tile.Y)
               * 0.5f * reach;
    }

    /// <summary>The mine on this cell, in the field's own space - add the
    /// board's origin for a point on the board, as every other caller of
    /// <see cref="HexField.CellCentre"/> does.</summary>
    public static Vector2 At(HexField field, Vector2I cell) =>
        field.CellCentre(cell)
        + (field.Atlas is { } atlas ? Offset(cell, atlas.HexRect.Size)
                                    : Vector2.Zero);

    /// <summary>
    /// Whether <paramref name="point"/> is under this tank's hull, and how far
    /// along the hull it is - positive toward the nose, negative toward the
    /// tail, in the ground's own pixels.
    ///
    /// <b>One test, and it answers "which end" without being asked.</b> The
    /// frame this first becomes true is the frame the <em>leading</em> end of
    /// the hull reached the point, whichever end that was: a tank driving
    /// forward covers it with its nose, one shoved backwards by a ram covers it
    /// with its tail, and neither case is written down anywhere. What the sign
    /// of <paramref name="along"/> is for is the picture afterwards - which end
    /// gets thrown up (<c>TankTick.UpdateMines</c>) and, when it exists, which
    /// end the plough is on: GDD classes.md gives a tank reversing on to a mine
    /// no protection from a trawl, because the trawl is at the front.
    ///
    /// <b>Measured on the hull's own two axes, not on the screen's.</b> The
    /// board is drawn at 30 degrees, so a rectangle on the ground is a
    /// parallelogram on screen; <see cref="AtlasSet.GroundDirection"/> gives the
    /// two ground axes as they land on screen, and solving the offset in that
    /// pair takes the projection back out. That is the same trick
    /// <see cref="Vehicle.Graze"/> uses at the other end of the tank.
    /// </summary>
    /// <summary>
    /// The cells that carry a marker: a minefield nobody has set off yet.
    ///
    /// <b>The rule in one place, because two readers would drift.</b> The board
    /// draws what this returns (<c>Stage3D</c>'s markers) and a check asserts it
    /// without a board; a spent mine is off the list by the same line that takes it
    /// off the screen - GDD field.md, "мина израсходована", which is
    /// <see cref="CoverState.Cleared"/> here.
    /// </summary>
    public static List<Vector2I> Marked(HexField field)
    {
        var due = new List<Vector2I>();
        for (int r = 0; r < field.Rows; r++)
        for (int q = 0; q < field.Columns; q++)
        {
            var cell = new Vector2I(q, r);
            if (field.CoverAt(cell) == Cover.Minefield
                && field.CoverStateAt(cell) == CoverState.Intact)
                due.Add(cell);
        }
        return due;
    }

    /// <summary>
    /// Whether a charge at <paramref name="point"/> is on the camera's side of the
    /// hull's contact line.
    ///
    /// <b>One comparison, and it is the whole of "is the blast in front of the
    /// tank".</b> Board y grows toward the camera (<c>Stage3D.World</c>) and a
    /// tank's own sprite is hinged at its contact point, so a point with more of it
    /// than the foot has is between the camera and the hull. A mine goes off under
    /// the leading end, so the answer is really "which way was it driving": at the
    /// viewer, and the charge is in front; up the board, and the same charge is
    /// behind the hull.
    ///
    /// Beside <see cref="Under"/> because it is the same kind of question about the
    /// same pair of points, and because a root that draws the burst should not be
    /// the place this convention is written down.
    /// </summary>
    public static bool Ahead(Vehicle tank, Vector2 point) =>
        point.Y > tank.GroundPoint.Y;

    public static bool Under(Vehicle tank, Vector2 point, out double along)
    {
        along = 0.0;
        // The offset written on the two ground axes: Cramer, because the pair is
        // two vectors and not a rotation - they are only orthogonal on the
        // ground, and on screen they are not. The box and the solve both live in
        // Footing.Tread now: this question is asked of felled trunks too, and
        // one atlas answered by two Cramers is one Cramer that will be wrong.
        return Footing.Tread.Of(tank) is { } tread
               && tread.Covers(point, out along);
    }
}
