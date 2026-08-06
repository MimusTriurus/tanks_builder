using System;
using System.Collections.Generic;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// A tessellated hex field drawn from the static <c>hex</c> layer of an
/// <see cref="AtlasSet"/>, plus the grid arithmetic that goes with it.
///
/// Spacing is derived from the rendered tile's own opaque size, which is exact
/// rather than approximate. For a flat-top hex of circumradius R seen at
/// azimuth 0 and elevation e, the sprite is 2R/upp wide and
/// sqrt(3)*R*sin(e)/upp tall. Neighbours sit 1.5R apart across columns and
/// sqrt(3)*R apart down a column, so in screen pixels:
///
///     column spacing = 0.75 * sprite width
///     row spacing    = 1.00 * sprite height
///     odd columns are pushed down by half a row
///
/// The row spacing coming out exactly equal to the sprite height is not a
/// coincidence: flat-top hexes touch edge to edge going down a column.
///
/// Cells are addressed in odd-q offset coordinates (col, row). Anything that
/// needs real hex arithmetic - rounding a click to a cell, walking a path -
/// converts to axial first, because offset coordinates do not add.
/// </summary>
public sealed partial class HexField : Node2D
{
    /// <summary>World headings of the six edge directions of a flat-top hex.</summary>
    public static readonly int[] EdgeHeadings = { 30, 90, 150, 210, 270, 330 };

    public AtlasSet? Atlas;
    public int Columns = 9;
    public int Rows = 6;
    public bool ShowField = true;

    /// <summary>Cells to tint as the current move order, destination last.</summary>
    public IReadOnlyList<Vector2I> Highlight = Array.Empty<Vector2I>();

    /// <summary>Every cell the driven tank could shoot into from where it
    /// stands - the six lanes, drawn faint. See <see cref="Lane"/> for why the
    /// gun has lanes at all; drawn because the rule is otherwise invisible
    /// until a shot fails to happen, and what the player needs to know is not
    /// "no line" but where to drive to get one.</summary>
    public IReadOnlyList<Vector2I> Arcs = Array.Empty<Vector2I>();

    /// <summary>The lane a live firing solution runs down, target last. Drawn
    /// over the arcs and over the route, because it is the one that is about to
    /// matter.</summary>
    public IReadOnlyList<Vector2I> Aim = Array.Empty<Vector2I>();

    public override void _Ready() => ZIndex = -100;

    // --- layout ------------------------------------------------------------

    /// <summary>Anchor point of a cell relative to the field's origin. Both the
    /// tile and any tank standing on it are drawn against this one point, so
    /// they cannot drift apart.</summary>
    public Vector2 CellAnchor(int q, int r)
    {
        if (Atlas is null)
            return Vector2.Zero;
        float w = Atlas.HexRect.Size.X;
        float h = Atlas.HexRect.Size.Y;
        float stagger = q % 2 != 0 ? h * 0.5f : 0.0f;
        return new Vector2(q * w * 0.75f, r * h + stagger);
    }

    public Vector2 CellAnchor(Vector2I cell) => CellAnchor(cell.X, cell.Y);

    /// <summary>Offset from a cell's anchor to the centre of its hexagon.
    ///
    /// These are not the same point and assuming they are puts every click one
    /// cell out near a boundary. The anchor is the turret's rotation axis
    /// projected to screen, and the renderer places that axis at the mid-height
    /// of the fitted bounds, so it floats above the ground plane the hexagon
    /// lies on. The gap is constant across cells, which is what makes it a
    /// single subtraction rather than a per-cell correction.</summary>
    public Vector2 CentreOffset => Atlas?.GroundOffset ?? Vector2.Zero;

    /// <summary>Centre of a cell's drawn hexagon, in this node's local space.
    /// This is the point <see cref="CellAt"/> inverts - not the anchor, which
    /// sits about 60px higher on the current atlases.</summary>
    public Vector2 CellCentre(Vector2I cell) => CellAnchor(cell) + CentreOffset;

    /// <summary>Cell under a point in this node's local space.
    ///
    /// The rendered field is a regular hex grid squashed vertically by
    /// sin(elevation), so the squash is undone before the standard flat-top
    /// pixel-to-hex conversion. Rounding is done in cube coordinates: rounding
    /// col and row independently would pick the wrong cell along every slanted
    /// edge, which is most of the border.</summary>
    public Vector2I CellAt(Vector2 local)
    {
        if (Atlas is null)
            return Vector2I.Zero;
        Vector2 p = local - CentreOffset;
        float w = Atlas.HexRect.Size.X;
        float h = Atlas.HexRect.Size.Y;
        float size = w * 0.5f;                              // circumradius, px
        float y = p.Y * (Mathf.Sqrt(3.0f) * w) / (2.0f * h);  // undo the squash
        float q = 2.0f / 3.0f * p.X / size;
        float r = (-1.0f / 3.0f * p.X + Mathf.Sqrt(3.0f) / 3.0f * y) / size;
        return AxialToOffset(CubeRound(q, r));
    }

    public bool InBounds(Vector2I cell) =>
        cell.X >= 0 && cell.X < Columns && cell.Y >= 0 && cell.Y < Rows;

    public Vector2I ClampCell(Vector2I cell) =>
        new(Mathf.Clamp(cell.X, 0, Columns - 1), Mathf.Clamp(cell.Y, 0, Rows - 1));

    // --- grid arithmetic ---------------------------------------------------

    /// <summary>The cell one step from <paramref name="cell"/> along a world
    /// heading, without clamping. Returns the cell unchanged if the heading
    /// points at a corner rather than an edge.</summary>
    public static Vector2I Step(Vector2I cell, int heading)
    {
        bool odd = cell.X % 2 != 0;
        int dir = ((heading % 360) + 360) % 360;
        return dir switch
        {
            90 => cell + new Vector2I(0, -1),
            270 => cell + new Vector2I(0, 1),
            30 => cell + new Vector2I(1, odd ? 0 : -1),
            330 => cell + new Vector2I(1, odd ? 1 : 0),
            150 => cell + new Vector2I(-1, odd ? 0 : -1),
            210 => cell + new Vector2I(-1, odd ? 1 : 0),
            _ => cell,
        };
    }

    public Vector2I Neighbour(Vector2I cell, double facing) =>
        ClampCell(Step(cell, (int)Mathf.Round(Mathf.PosMod((float)facing, 360.0f))));

    /// <summary>World heading that steps from one cell to an adjacent one, or
    /// -1 if they are not neighbours.</summary>
    public static int HeadingTo(Vector2I from, Vector2I to)
    {
        foreach (int heading in EdgeHeadings)
            if (Step(from, heading) == to)
                return heading;
        return -1;
    }

    /// <summary>
    /// The cells outward from one along a flat-side heading, nearest first,
    /// stopping at the edge of the board.
    ///
    /// A lane is where a gun can shoot. The six of them are the same six
    /// <see cref="EdgeHeadings"/> the tank drives along and is shot from, which
    /// is the point of there being one list: a shell leaves along a flat side
    /// and arrives across one, so the shooter's heading and the plate the
    /// target presents are two ends of the same number rather than two
    /// conventions to keep in agreement.
    ///
    /// Walked with <see cref="Step"/> rather than multiplied out in cube
    /// coordinates, for the reason <see cref="FindPath"/> is breadth-first: Step
    /// is already the one definition of what is next to what, and a second one
    /// derived from axial arithmetic is a second thing that can disagree with
    /// the board - most likely on the odd columns, where it would be wrong
    /// every other file and right in a screenshot.
    /// </summary>
    public List<Vector2I> Lane(Vector2I cell, int heading, int limit = int.MaxValue)
    {
        var lane = new List<Vector2I>();
        Vector2I at = cell;
        while (lane.Count < limit)
        {
            Vector2I next = Step(at, heading);
            // Step hands back the cell unchanged for a heading that points at a
            // corner rather than an edge, so this is what stops a bogus heading
            // spinning here forever.
            if (next == at || !InBounds(next))
                break;
            lane.Add(next);
            at = next;
        }
        return lane;
    }

    /// <summary>
    /// The lane <paramref name="to"/> stands on as seen from
    /// <paramref name="from"/>, and how many cells down it - or (-1, 0) when it
    /// stands on none.
    ///
    /// Both at once because the walk finds both, and the range is wanted
    /// wherever the heading is: the cells in between are what a shot has to
    /// pass through.
    /// </summary>
    public (int Heading, int Range) LaneTo(Vector2I from, Vector2I to)
    {
        if (from == to)
            return (-1, 0);
        foreach (int heading in EdgeHeadings)
        {
            List<Vector2I> lane = Lane(from, heading);
            int at = lane.IndexOf(to);
            if (at >= 0)
                return (heading, at + 1);
        }
        return (-1, 0);
    }

    /// <summary>Shortest path between two cells, excluding the start.
    ///
    /// Breadth-first rather than a cube-coordinate line: a straight line
    /// between two cells of a staggered rectangle can bulge outside it, and a
    /// search over in-bounds cells simply cannot produce a step off the board.
    /// With no obstacles the two agree anyway - and now there are obstacles, which
    /// is the other half of why the search was written this way: the other tanks
    /// stand in cells, and a breadth-first search takes a blocked set for free
    /// where a line would have had to be repaired around one.
    ///
    /// A blocked destination returns no path at all rather than a path up to it.
    /// The caller wanted that cell; stopping short of it and calling it done is
    /// the kind of near miss that reads as the pathing being wrong.</summary>
    public List<Vector2I> FindPath(Vector2I from, Vector2I to,
                                   IReadOnlySet<Vector2I>? blocked = null)
    {
        var path = new List<Vector2I>();
        if (from == to || !InBounds(from) || !InBounds(to))
            return path;
        if (blocked is not null && blocked.Contains(to))
            return path;

        var cameFrom = new Dictionary<Vector2I, Vector2I> { [from] = from };
        var queue = new Queue<Vector2I>();
        queue.Enqueue(from);
        bool found = false;
        while (queue.Count > 0 && !found)
        {
            Vector2I cell = queue.Dequeue();
            foreach (int heading in EdgeHeadings)
            {
                Vector2I next = Step(cell, heading);
                if (!InBounds(next) || cameFrom.ContainsKey(next)
                    || (blocked is not null && blocked.Contains(next)))
                    continue;
                cameFrom[next] = cell;
                if (next == to)
                {
                    found = true;
                    break;
                }
                queue.Enqueue(next);
            }
        }
        if (!found)
            return path;

        for (Vector2I at = to; at != from; at = cameFrom[at])
            path.Add(at);
        path.Reverse();
        return path;
    }

    // --- cube coordinate helpers -------------------------------------------

    private static Vector2I AxialToOffset(Vector2I axial) =>
        new(axial.X, axial.Y + (axial.X - (axial.X & 1)) / 2);

    private static Vector2I CubeRound(float q, float r)
    {
        float s = -q - r;
        int rq = Mathf.RoundToInt(q);
        int rr = Mathf.RoundToInt(r);
        int rs = Mathf.RoundToInt(s);
        float dq = Mathf.Abs(rq - q);
        float dr = Mathf.Abs(rr - r);
        float ds = Mathf.Abs(rs - s);
        if (dq > dr && dq > ds)
            rq = -rr - rs;
        else if (dr > ds)
            rr = -rq - rs;
        return new Vector2I(rq, rr);
    }

    // --- drawing -----------------------------------------------------------

    public override void _Draw()
    {
        if (Atlas is null || !ShowField)
            return;
        Texture2D texture = Atlas.Texture("hex");
        Rect2 source = Atlas.Region("hex", 0);

        for (int q = 0; q < Columns; q++)
        for (int r = 0; r < Rows; r++)
            DrawTextureRectRegion(texture,
                new Rect2(CellAnchor(q, r) - Atlas.Anchor, Atlas.Tile), source);

        // Gunnery under the route rather than over it: the arcs say where a shot
        // could go, the route says where this tank is going, and while both are
        // up the second is the one being given. Red for both, because amber and
        // yellow-green are the route's and cyan is the selection ring's - a mark
        // that has to be told apart from another mark is not a mark.
        foreach (Vector2I cell in Arcs)
            DrawTextureRectRegion(texture,
                new Rect2(CellAnchor(cell) - Atlas.Anchor, Atlas.Tile), source,
                // Barely off the bare tile. Measured on the first screenshot
                // rather than chosen: at 0.80,0.52,0.46 the six lanes cover half
                // the board and read as terrain - two kinds of ground - instead
                // of as something drawn on it. The route's amber can be loud
                // because it is a handful of cells; this is thirty of them.
                new Color(1.0f, 0.86f, 0.84f));

        for (int i = 0; i < Highlight.Count; i++)
        {
            bool last = i == Highlight.Count - 1;
            var tint = last
                ? new Color(1.0f, 0.78f, 0.30f)
                : new Color(0.85f, 0.95f, 0.65f);
            DrawTextureRectRegion(texture,
                new Rect2(CellAnchor(Highlight[i]) - Atlas.Anchor, Atlas.Tile),
                source, tint);
        }

        foreach (Vector2I cell in Aim)
            DrawTextureRectRegion(texture,
                new Rect2(CellAnchor(cell) - Atlas.Anchor, Atlas.Tile), source,
                new Color(1.0f, 0.40f, 0.32f));
    }
}
