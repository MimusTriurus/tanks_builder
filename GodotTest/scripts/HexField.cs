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

    /// <summary>The hand-drawn ground, or null to fall back to the rendered
    /// <c>hex</c> layer. Null is a working state, not a broken one - the art is
    /// outside the repository the way the sounds are, and a bench with none of
    /// it still shows everything it showed before.
    ///
    /// Setting it also settles the filter, which is why it is a property. The
    /// project samples canvas textures nearest so the atlases arrive as they
    /// were rendered, and that is right for art painted at the size it is drawn
    /// at. Art painted finer is drawn smaller than it was painted, and nearest
    /// minification is point-sampling: the gravel crawls as the board pans. So
    /// ground that is drawn down asks for mipmaps, and only that ground - the
    /// fallback <c>hex</c> layer is still 1:1.</summary>
    public TerrainSet? Terrain
    {
        get => _terrain;
        set
        {
            _terrain = value;
            TextureFilter = value is not null && value.AnyDetailed
                ? TextureFilterEnum.LinearWithMipmaps
                : TextureFilterEnum.ParentNode;
        }
    }

    private TerrainSet? _terrain;

    /// <summary>Which kind every cell is: <see cref="TerrainSet.Mixed"/> for one
    /// per cell, or a kind's name to paint the whole board with it.
    ///
    /// A whole-board paint is not how a map would be built - it is how one kind
    /// is judged against another, which is the same reason --sprites exists.
    /// </summary>
    public string Paint = TerrainSet.Mixed;

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

    /// <summary>Bottom of the ladder. Was -100 with the selection ring right
    /// above it at -99, which left no rung for the ruts - they are terrain, so
    /// they belong over this and under the ring. Both moved down rather than
    /// leaning on child order, which decides nothing here by design.</summary>
    public const int GroundZ = -102;

    public override void _Ready() => ZIndex = GroundZ;

    // --- relief --------------------------------------------------------------

    /// <summary>
    /// How high each cell stands, in levels, or null for a board with no relief
    /// in it at all.
    ///
    /// Null rather than an array of zeroes, and that is the whole safety of this
    /// feature landing in a bench that works: with no relief every number below
    /// reduces to what it was before there was height, and
    /// <see cref="Occluders"/> - the one rule that changes how the board is
    /// painted - returns nothing at all. A flat board draws exactly as it did.
    /// </summary>
    private int[]? _levels;

    /// <summary>Whether this board carries height.</summary>
    public bool HasRelief => _levels is not null;

    /// <summary>
    /// How steep one level is, as a fraction of the distance to a neighbour.
    ///
    /// A grade rather than a height in pixels, so it is the same slope on any
    /// tile: the pixels follow from <see cref="Reach"/>, which is measured off
    /// the rendered hexagon. Named in the one place both the lift and the
    /// climb-cost would read it from.
    /// </summary>
    public double StepGrade = 0.25;

    /// <summary>How many levels a tank may take in one step. One, because two is
    /// a cliff: at this grade a two-level step is half the distance to the
    /// neighbour, and a route that walks a pond up onto a crown in a single hex
    /// reads as the pathing ignoring the board.</summary>
    public int MaxClimb = 1;

    public void SetRelief(int[]? levels)
    {
        _levels = levels is not null && levels.Length == Columns * Rows
            ? levels : null;
        _ordered = null;
        QueueRedraw();
    }

    public int LevelAt(Vector2I cell) =>
        _levels is null || !InBounds(cell) ? 0 : _levels[cell.Y * Columns + cell.X];

    /// <summary>The levels this board carries, scanned off it rather than
    /// written down: a range that outlives an edit to the map is a picking rule
    /// that silently stops answering for the level somebody added.</summary>
    public (int Low, int High) LevelRange
    {
        get
        {
            if (_levels is null)
                return (0, 0);
            int low = 0, high = 0;
            foreach (int level in _levels)
            {
                low = Math.Min(low, level);
                high = Math.Max(high, level);
            }
            return (low, high);
        }
    }

    /// <summary>Ground-plane squash - sin(elevation) - read off the rendered
    /// tile rather than declared. The hexagon's height over its width is
    /// (sqrt(3)/2)*sin(e) for a flat-top hex, so the tile <i>is</i> the camera
    /// angle; see <see cref="TerrainSet"/>, which refuses art that disagrees
    /// with it.</summary>
    public float Squash
    {
        get
        {
            if (Atlas is null || Atlas.HexRect.Size.X <= 0)
                return 0.5f;
            return 2.0f * Atlas.HexRect.Size.Y
                   / (Mathf.Sqrt(3.0f) * Atlas.HexRect.Size.X);
        }
    }

    /// <summary>Screen px a ground px of <i>height</i> is worth - cos(elevation),
    /// and the only new number height needs. Follows from the squash rather than
    /// being measured apart from it, because the two are one angle.</summary>
    public float RiseFactor => Mathf.Sqrt(Mathf.Max(0.0f, 1.0f - Squash * Squash));

    /// <summary>Distance to any of the six neighbours, in ground px: sqrt(3)*R
    /// for a flat-top hexagon, and equally twice the inradius. The same number
    /// in all six directions, which is what lets one grade serve every
    /// edge.</summary>
    public float Reach =>
        Atlas is null ? 0.0f : Mathf.Sqrt(3.0f) * Atlas.HexRect.Size.X * 0.5f;

    /// <summary>One level, in screen px.</summary>
    public float Lift => (float)StepGrade * Reach * RiseFactor;

    /// <summary>
    /// How near the camera something is - the number the painter's algorithm
    /// actually wants, and not the ground row once the board has height in it.
    ///
    /// At elevation e a point's distance along the view is
    /// <c>y_ground*cos(e) + z*sin(e)</c>: <b>raising something brings it
    /// nearer</b>, because the camera is above. The ground row is only the first
    /// term, which is why it was the whole answer for as long as z was zero
    /// everywhere.
    ///
    /// Both arguments are screen px - the row as this node draws it, the lift as
    /// the cell or the tank carries it - so nothing outside has to hold ground
    /// units.
    ///
    /// <b>Scaled so that the flat term is the row itself</b>, which is the whole
    /// of tan(e) and worth the line it costs. Depth is only ever compared, so any
    /// positive multiple of it is the same answer - and this particular multiple
    /// is the one that leaves every number that already sorts against a foot row
    /// alone. The trees are at <c>round(footY)</c> and the ruts and rings at
    /// fixed rungs below the board; a key that scaled the row by 1.7 would sort
    /// every tank behind every tree, and it would do it on a board with no relief
    /// on it at all.
    /// </summary>
    public float Depth(float row, float lift)
    {
        float tan = Squash / Mathf.Max(RiseFactor, 0.01f);
        return row + lift * tan * tan;
    }

    /// <summary>
    /// The row a thing standing on this cell sorts at, with the height taken
    /// out.
    ///
    /// <b>The ground centre, not the anchor.</b> A cell's anchor is the turret
    /// axis projected to screen and floats some 59px above the ground plane the
    /// hexagon lies on - see <see cref="CentreOffset"/>, which exists because
    /// assuming the two are one point puts every click a cell out. Everything
    /// that stands on the board sorts where the ground is: a tank off its
    /// <c>GroundPoint</c>, a tree off its foot. So a cell keyed off its anchor
    /// is read 59px further away than it is, and against a hexagon 54.5px from
    /// centre to edge that is more than a whole row - the raised hex diagonally
    /// in front of a tank came out behind it, and the only ground that could
    /// cover anything was a full row further on again.
    ///
    /// Named rather than written out at the one place that needs it, because it
    /// is the space <see cref="Occluders"/> is asked its question in, and the
    /// tests that certified the old answer were the ones that built the row
    /// themselves.
    /// </summary>
    public float GroundRow(Vector2I cell) => FlatAnchor(cell).Y + CentreOffset.Y;

    /// <summary>
    /// A cell's depth, taken at its <b>far</b> edge rather than its centre.
    ///
    /// A face is not a point and cannot be sorted as one: the near half of a low
    /// cell really is in front of a tank up on the terrace behind it, and the far
    /// half really is behind it. The two halves want opposite answers and a
    /// single key can only give one, so it should give the one whose failure is
    /// survivable - a tank drawn over a strip of ground it should be behind is a
    /// pixel or two, and a tank swallowed by the ground in front of it is the
    /// picture that started this.
    ///
    /// Among cells it changes nothing, every key moving by the same apothem, so
    /// this only ever decides a cell weighed against something standing on the
    /// board.
    /// </summary>
    public float DepthOf(Vector2I cell)
    {
        float half = Atlas is null ? 0.0f : Atlas.HexRect.Size.Y * 0.5f;
        return Depth(GroundRow(cell) - half, LevelAt(cell) * Lift);
    }

    /// <summary>
    /// The cells that hide a thing standing at <paramref name="flatRow"/> and
    /// <paramref name="standing"/> px off the ground - nearer than it <b>and</b>
    /// higher.
    ///
    /// <b>Flat ground in front of a thing cannot hide it, at any distance</b>:
    /// the ray from a point back to the camera only ever rises, so ground below
    /// that point is never on it. Only ground standing <i>higher</i> can be - and
    /// on a board where nothing is raised that is nothing at all, which is
    /// exactly why one field node under every tank was right until now. Depth
    /// alone said otherwise twice and the picture showed it both times: the cell
    /// being driven into is nearer than the tank for the first half of every
    /// step, so it went down last and cut the hull off at the tracks.
    ///
    /// <b>But a tank is not a point on the ground, and that is the second
    /// clause.</b> Its belts hang forward of the turret axis, so it is drawn
    /// reaching below its own contact point - 42.5px on the heavy's atlas and
    /// 48.9 once the class size is on it, against a hexagon 54.5px from centre
    /// to edge. Those pixels are ground-level track standing in the cell in
    /// front, and ground level with the tank has every right to cover them: left
    /// uncovered, a heavy parked at a boundary is drawn lying across the next
    /// hex.
    ///
    /// So a cell level with the tank may cover it, and <b>only from the shared
    /// ground down</b>. That one line is what stops the old failure returning,
    /// rather than a promise not to: a face beginning at or below the contact row
    /// cannot reach the hull, because the hull is above it. And the cell being
    /// driven into fails that test from the moment the tank enters it - its far
    /// edge falls behind the contact point - so it goes back to covering nothing.
    ///
    /// The note on <see cref="DepthOf"/> put the cost of the far-edge key at "a
    /// pixel or two". That was the medium at 1.00x; on the heavy it is 49.
    ///
    /// <b><paramref name="flatRow"/> is measured where the ground is</b> - the
    /// same space as <see cref="GroundRow"/> and <see cref="CellCentre"/>, which
    /// is what a tank's <c>GroundPoint</c> and a tree's foot are already in. An
    /// anchor row handed in here is 59px short and the whole rule shifts by more
    /// than a row of the board; see <see cref="GroundRow"/> for the picture that
    /// makes.
    /// </summary>
    public List<Vector2I> Occluders(float flatRow, float standing, float height)
    {
        var over = new List<Vector2I>();
        if (_levels is null || Atlas is null)
            return over;
        float depth = Depth(flatRow, standing);
        float half = Atlas.HexRect.Size.Y * 0.5f;
        // Where the thing touches the ground, on screen. Off the drawn height
        // rather than off the standing one, which is the higher of the two while
        // climbing: taken from the standing height the contact would be read
        // half a wall too high mid-climb, and the cell being climbed onto would
        // eat the hull for exactly the frames this is meant to protect.
        float contact = flatRow - height;
        foreach (Vector2I cell in Ordered())
        {
            if (DepthOf(cell) > depth && LevelAt(cell) * Lift > standing + 0.5f)
            {
                over.Add(cell);
                continue;
            }
            // Level with it, and its face begins at or below the ground the two
            // of them share.
            if (Math.Abs(LevelAt(cell) * Lift - standing) <= 0.5f
                && CellCentre(cell).Y - half >= contact - 0.5f)
                over.Add(cell);
        }
        return over;
    }

    // --- layout ------------------------------------------------------------

    /// <summary>
    /// Anchor point of a cell with the height taken out - the ground row.
    ///
    /// <b>This is the sorting space</b>, and the one thing the lift must never
    /// reach: a raised cell moves up the screen and is not thereby one step
    /// further away. Sorting on the drawn position instead walks the hill
    /// backwards.
    /// </summary>
    public Vector2 FlatAnchor(int q, int r)
    {
        if (Atlas is null)
            return Vector2.Zero;
        float w = Atlas.HexRect.Size.X;
        float h = Atlas.HexRect.Size.Y;
        float stagger = q % 2 != 0 ? h * 0.5f : 0.0f;
        return new Vector2(q * w * 0.75f, r * h + stagger);
    }

    public Vector2 FlatAnchor(Vector2I cell) => FlatAnchor(cell.X, cell.Y);

    /// <summary>Anchor point of a cell as it is drawn. Both the tile and any tank
    /// standing on it are placed against this one point, so they cannot drift
    /// apart - which is what carries the height into the tank's position for
    /// free, without anything that stands on the board knowing about it.</summary>
    public Vector2 CellAnchor(int q, int r) =>
        FlatAnchor(q, r) - new Vector2(0.0f, LevelAt(new Vector2I(q, r)) * Lift);

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

    /// <summary>
    /// Cell under a point in this node's local space, height and all.
    ///
    /// One screen point sits on a low cell and on the raised cell behind it at
    /// the same time, so the flat inverse alone answers with whichever the
    /// arithmetic reaches - which is the low one, every time, and reads as the
    /// picking being broken rather than as the board having height.
    ///
    /// Asked per level instead: drop the point by each level the board carries
    /// and keep the answers that agree about their own level. Of those the
    /// frontmost wins, because frontmost is the one drawn last and therefore the
    /// one being looked at. A board carries a handful of levels, so this is exact
    /// rather than approximate, and deterministic.
    ///
    /// Falls back to the flat answer for a click that landed on a wall rather
    /// than on a top face. A wall belongs to the cell above it, but resolving it
    /// that way would need the walls kept as polygons; the cell in front is close
    /// enough to stand a tank on and is never off the board.
    /// </summary>
    public Vector2I CellAt(Vector2 local)
    {
        if (_levels is null)
            return FlatCellAt(local);

        var best = new Vector2I(-1, -1);
        float front = float.NegativeInfinity;
        (int low, int high) = LevelRange;
        for (int level = low; level <= high; level++)
        {
            Vector2I cell = FlatCellAt(local + new Vector2(0.0f, level * Lift));
            if (!InBounds(cell) || LevelAt(cell) != level)
                continue;
            float row = FlatAnchor(cell).Y;
            if (row <= front)
                continue;
            front = row;
            best = cell;
        }
        return best.X >= 0 ? best : FlatCellAt(local);
    }

    /// <summary>The flat inverse: which cell a point would be in if the board
    /// were level.
    ///
    /// The rendered field is a regular hex grid squashed vertically by
    /// sin(elevation), so the squash is undone before the standard flat-top
    /// pixel-to-hex conversion. Rounding is done in cube coordinates: rounding
    /// col and row independently would pick the wrong cell along every slanted
    /// edge, which is most of the border.</summary>
    private Vector2I FlatCellAt(Vector2 local)
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
                    || (blocked is not null && blocked.Contains(next))
                    // Height is an obstacle of the same kind as another tank, so
                    // it is refused in the same place: a search that took the
                    // cliff and then had the driving refuse it would report a
                    // route it cannot walk.
                    || Math.Abs(LevelAt(next) - LevelAt(cell)) > MaxClimb)
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

    // --- terrain -----------------------------------------------------------

    /// <summary>
    /// What a cell is: a plate kind, or <see cref="TerrainSet.Forest"/>.
    ///
    /// Forest is the first kind that is not a picture. It has no file and never
    /// will - it is soil with trees standing on it, and the trees are props with
    /// their own places on the ground (see <see cref="Grove"/>). Composed rather
    /// than painted for two reasons that are not about memory: a painted forest
    /// hex could not have a tank drive into it without the trees being in a
    /// fixed relationship to the tank, and the ground would be a second dirt
    /// painting meeting soil at a visible seam.
    ///
    /// So it is a kind here, where kinds are chosen, and a plate elsewhere.
    /// </summary>
    public string KindAt(Vector2I cell)
    {
        if (Terrain is null || !Terrain.Any)
            return TerrainSet.Plain;
        if (Paint == TerrainSet.Forest)
            return TerrainSet.Forest;
        if (Terrain.Has(Paint))
            return Paint;
        // In the mix it is one kind among the plates, drawn by the same hash, so
        // "how much forest" is not a second dial arguing with the terrain list.
        return Trees && Terrain.MixedAt(cell, TerrainSet.Forest) is var mixed
            ? mixed : Terrain.MixedAt(cell);
    }

    /// <summary>Whether the forest kind is on offer at all. False with no props
    /// loaded and under --no-forest, and then the mix is plates only - a board
    /// scattered with cells that are soil and claim to be woods would be worse
    /// than one with no woods on it.</summary>
    public bool Trees = true;

    /// <summary>The plate a cell is drawn on. Forest stands on the default
    /// ground rather than on one of its own, so a clearing and the field beside
    /// it are the same dirt and there is no seam at the boundary.</summary>
    public string TypeAt(Vector2I cell)
    {
        string kind = KindAt(cell);
        if (kind != TerrainSet.Forest)
            return kind;
        return Terrain is not null && Terrain.Has(TerrainSet.Default)
            ? TerrainSet.Default : TerrainSet.Plain;
    }

    /// <summary>
    /// Every cell, furthest from the camera first.
    ///
    /// The rendered tile is a flat n-gon that stops at its own edge, so the
    /// order it was drawn in never mattered - the column-major loop this
    /// replaces was fine for exactly as long as nothing stuck out of a cell.
    /// Drawn ground sticks out of every cell: grass over the back edge, a tuft
    /// off the front, and on art with a slab the whole side wall hangs over
    /// whatever is in front of it. Painted in column order, a tuft belonging to
    /// the cell behind lands on top of the cell in front.
    ///
    /// Sorted rather than nested, because the odd columns are pushed down half a
    /// row: there is no loop nesting that walks a staggered grid in screen
    /// order, and one written to look like there is would be right down the even
    /// files and wrong down the odd ones.
    /// </summary>
    public IReadOnlyList<Vector2I> Ordered()
    {
        if (_ordered is null || _orderedFor != new Vector2I(Columns, Rows))
        {
            var cells = new List<Vector2I>(Columns * Rows);
            for (int q = 0; q < Columns; q++)
            for (int r = 0; r < Rows; r++)
                cells.Add(new Vector2I(q, r));
            // By depth rather than by the drawn row, and on a flat board the two
            // are the same order: depth is the row times a constant. With height
            // in it they part company, and sorting on the drawn position walks
            // the hill backwards - a raised cell moves up the screen and is not
            // thereby one step further away.
            cells.Sort((a, b) => DepthOf(a).CompareTo(DepthOf(b)));
            _ordered = cells;
            _orderedFor = new Vector2I(Columns, Rows);
        }
        return _ordered;
    }

    private List<Vector2I>? _ordered;
    private Vector2I _orderedFor = new(-1, -1);

    // --- drawing -----------------------------------------------------------

    /// <summary>Barely off the bare tile. Measured on the first screenshot
    /// rather than chosen: at 0.80,0.52,0.46 the six lanes cover half the board
    /// and read as terrain - two kinds of ground - instead of as something drawn
    /// on it. The route's amber can be loud because it is a handful of cells;
    /// this is thirty of them.</summary>
    private static readonly Color ArcInk = new(1.0f, 0.86f, 0.84f);
    private static readonly Color RouteInk = new(0.85f, 0.95f, 0.65f);
    private static readonly Color DestInk = new(1.0f, 0.78f, 0.30f);
    private static readonly Color AimInk = new(1.0f, 0.40f, 0.32f);

    public override void _Draw()
    {
        if (Atlas is null || !ShowField)
            return;

        // The marks are resolved to one colour per cell before anything is
        // drawn, so each cell is painted exactly once. Drawing the tile again
        // per mark was equivalent while a tile was one opaque hexagon; with
        // terrain it would paint the grass two and three deep, and each pass
        // over an overhang would land on whichever cell happened to be under it.
        //
        // Resolved by asking rather than by a table built here, because a cell
        // repainted over a tank has to arrive at the same answer, and the two
        // lookups would be the place they disagreed. See InkFor for the order
        // between the marks and why they all sit in one function.
        foreach (Vector2I cell in Ordered())
            PaintCell(this, cell, InkFor(cell));
    }

    /// <summary>
    /// The mark a cell carries this frame, so a cell repainted over a tank
    /// carries the same one it did underneath.
    ///
    /// Strongest first, which is the same order the marks used to be written
    /// into a table in - the last write won there, the first match wins here.
    /// Gunnery under the route rather than over it: the arcs say where a shot
    /// could go, the route says where this tank is going, and while both are up
    /// the second is the one being given. Red for both, because amber and
    /// yellow-green are the route's and cyan is the selection ring's - a mark
    /// that has to be told apart from another mark is not a mark.
    /// </summary>
    public Color InkFor(Vector2I cell)
    {
        foreach (Vector2I at in Aim)
            if (at == cell)
                return AimInk;
        for (int i = 0; i < Highlight.Count; i++)
            if (Highlight[i] == cell)
                return i == Highlight.Count - 1 ? DestInk : RouteInk;
        foreach (Vector2I at in Arcs)
            if (at == cell)
                return ArcInk;
        return Colors.White;
    }

    /// <summary>
    /// One cell, walls and top, into whatever node is doing the drawing.
    ///
    /// Into a node rather than onto this one, because a cell has to be painted
    /// twice on a board with height: once here, under everything, and once more
    /// over whichever tank it stands in front of and above - see
    /// <see cref="ReliefCap"/>. Passing the target keeps that from being a second
    /// copy of how a cell is drawn, which is the way the two would come to
    /// disagree.
    /// </summary>
    public void PaintCell(CanvasItem into, Vector2I cell, Color tint)
    {
        if (Atlas is null)
            return;
        DrawWalls(into, cell);

        if (Terrain is not null && Terrain.Any)
        {
            Texture2D? art = Terrain.Texture(TypeAt(cell));
            if (art is not null)
            {
                // Against the cell's centre, not its anchor: the anchor is the
                // turret axis floating above the ground plane, and the art knows
                // only where its own hexagon is.
                into.DrawTextureRect(art,
                    Terrain.RectAt(CellCentre(cell), Terrain.ScaleTo(Atlas.HexRect)),
                    false, tint);
                return;
            }
        }
        into.DrawTextureRectRegion(Atlas.Texture("hex"),
            new Rect2(CellAnchor(cell) - Atlas.Anchor + Atlas.OffsetOf("hex", 0),
                      Atlas.SizeOf("hex", 0)),
            Atlas.Region("hex", 0), tint);
    }

    /// <summary>The hexagon's corners in screen px about a cell's centre, right
    /// first and going clockwise on screen, so corner i and i+1 span the edge
    /// facing <see cref="EdgeHeadings"/>[i] reversed - see
    /// <see cref="WallHeading"/>.
    ///
    /// No trigonometry, for <see cref="SelectionRing"/>'s reason: a flat-top
    /// hexagon of circumradius R has corners at (+/-R, 0) and (+/-R/2,
    /// +/-sqrt(3)R/2), and the isometry squashes the ground vertically - so in
    /// units of the drawn tile those are (+/-w/2, 0) and (+/-w/4, +/-h/2), with
    /// neither the camera angle nor a sine anywhere in it.</summary>
    private Vector2[] Corners()
    {
        float w = Atlas!.HexRect.Size.X * 0.5f, h = Atlas.HexRect.Size.Y * 0.5f;
        return new[]
        {
            new Vector2(w, 0.0f), new Vector2(w * 0.5f, h),
            new Vector2(-w * 0.5f, h), new Vector2(-w, 0.0f),
            new Vector2(-w * 0.5f, -h), new Vector2(w * 0.5f, -h),
        };
    }

    private static readonly int[] WallHeading = { 330, 270, 210, 150, 90, 30 };

    /// <summary>
    /// The faces between a cell and whatever is across each of its edges.
    ///
    /// One rule for all four cases: <b>the higher cell of a pair draws the side
    /// between them</b>. That covers the near side of a hill, the far bank of a
    /// pit, a terrace with a shelf in front and one with a shelf behind, without
    /// anybody knowing who is drawn first - which is what the two hand-written
    /// cases it replaces both got wrong once each.
    ///
    /// Under the cell's own top face, not over it. The wall of a far edge is
    /// covered by the face above it, and drawn afterwards it paints a pale slab
    /// across the grass. The three walls facing the camera are not covered by
    /// anything of this cell's, so they survive either way.
    /// </summary>
    /// <summary>Whether this cell draws the face between it and whatever is
    /// across <paramref name="heading"/>. <b>The higher one of a pair does</b>,
    /// and that is the whole rule - see <see cref="DrawWalls"/>. Named so it can
    /// be asserted rather than read off a picture: the two hand-written cases it
    /// replaced each got one of the four situations wrong, and the failures are a
    /// gap and a double-drawn face, neither of which is loud.</summary>
    public bool DrawsFace(Vector2I cell, int heading) =>
        _levels is not null && LevelAt(cell) > LevelAt(Step(cell, heading));

    private void DrawWalls(CanvasItem into, Vector2I cell)
    {
        if (_levels is null)
            return;
        int level = LevelAt(cell);
        Vector2 centre = CellCentre(cell);
        Vector2[] corner = Corners();

        // The cell's own ground, and its rectangle, so a screen point on this
        // hexagon can be turned into a point in the art. Null on a board with no
        // terrain loaded, which is a working state - see Terrain.
        Texture2D? art = Terrain is { Any: true } ? Terrain.Texture(TypeAt(cell))
                                                  : null;
        Rect2 rect = art is null ? default
            : Terrain!.RectAt(centre, Terrain.ScaleTo(Atlas!.HexRect));

        for (int i = 0; i < 6; i++)
        {
            int drop = level - LevelAt(Step(cell, WallHeading[i]));
            if (!DrawsFace(cell, WallHeading[i]))
                continue;
            var down = new Vector2(0.0f, drop * Lift);
            Vector2 a = centre + corner[i], b = centre + corner[(i + 1) % 6];
            var quad = new[] { a, b, b + down, a + down };
            if (art is null)
            {
                into.DrawColoredPolygon(quad, WallInk(WallHeading[i]));
                continue;
            }

            // The face is a band of this cell's own dirt, taken from just inside
            // the edge and stretched down it.
            //
            // <b>Off the ground rather than off a new file, and that is a choice
            // with a reason rather than a saving.</b> It is the same soil by
            // construction, so it cannot disagree with the top about what kind of
            // ground this is, and at the seam it is not merely similar - the top
            // of the wall samples the very texels the top face has there, so
            // there is no join to line up. Stretching a thin band downward also
            // gives the face vertical streaks for free, which is what a cut in
            // earth looks like; a wall taken from a taller band comes out as
            // smeared grass.
            //
            // A drawn cliff would be better and drops straight in here. This is
            // what the board looks like until somebody paints one.
            Vector2 towardsA = (centre - a).Normalized();
            Vector2 towardsB = (centre - b).Normalized();
            Vector2 uvA = (a + towardsA * WallSeam - rect.Position) / rect.Size;
            Vector2 uvB = (b + towardsB * WallSeam - rect.Position) / rect.Size;
            Vector2 inA = towardsA * WallBand / rect.Size;
            Vector2 inB = towardsB * WallBand / rect.Size;
            into.DrawColoredPolygon(quad, WallTint(WallHeading[i]),
                new[] { uvA, uvB, uvB + inB, uvA + inA }, art);
        }
    }

    /// <summary>
    /// How far inside its own edge the band starts, in screen px.
    ///
    /// Not zero, and the first version was. The art is painted with the hexagon's
    /// border drawn on it - a grey stripe a few px wide - so a band starting at
    /// the edge starts on the border, and stretched down a drop of forty-six px a
    /// four-px stripe becomes twenty: every cliff came out with a wide grey rail
    /// along the top. Starting below it leaves the border where it belongs, on
    /// the top face, and costs the one property the seam had - that the wall's
    /// first row is the very texel the face has there - which is worth nothing
    /// when that texel is a drawn line.
    /// </summary>
    private const float WallSeam = 6.0f;

    /// <summary>How deep into the plate the face's band is taken from, in screen
    /// px, measured from <see cref="WallSeam"/>. Thin, because it is stretched
    /// over the whole drop: wider and the wall reads as smeared grass rather than
    /// as a cut, and thinner and it repeats one row of texels into vertical
    /// stripes.</summary>
    private const float WallBand = 14.0f;

    /// <summary>How much of the light a face keeps, by which way it faces. The
    /// two that face the camera catch more than the four flanking them, which is
    /// all it takes for the block to read as a solid rather than as a hexagon
    /// with a skirt.
    ///
    /// A multiplier now that the face carries the ground's own colour, where it
    /// used to be a colour of its own. That is the whole of why a pond can have
    /// this: the old fixed earth existed to stop a wall taken from the water's
    /// blue giving it vertical blue sides, and a band of the pond's own surface
    /// darkened is a wet cut rather than a slab of glass.</summary>
    private static Color WallTint(int heading)
    {
        float k = heading is 270 or 90 ? 0.72f : 0.55f;
        return new Color(k, k, k);
    }

    /// <summary>The face colour with no ground art loaded. Earth whatever the top
    /// is - see <see cref="WallTint"/>.</summary>
    private static Color WallInk(int heading)
    {
        float k = heading is 270 or 90 ? 0.62f : 0.46f;
        return new Color(Soil.R * k, Soil.G * k, Soil.B * k);
    }

    private static readonly Color Soil = new(0.60f, 0.57f, 0.45f);
}
