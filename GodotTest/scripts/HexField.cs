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

    /// <summary>How wide the board is. Fourteen because of the last five: the
    /// rosette in <c>Main.Relief</c> is a hill with a ramp on each of its six
    /// faces, and a ramp needs both of its axis neighbours on the board, so the
    /// patch is a disc of radius two - five columns, and it had to go beside the
    /// nine rather than into them. Wider than the window at zoom 1; the wheel and
    /// the middle button reach the rest, and <c>--zoom</c> is how a capture
    /// does.</summary>
    public int Columns = 14;
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
    /// Which way each cell's top face tilts up, as the world heading of its high
    /// edge, or -1 for a cell whose top is flat. Null for a board with no ramps
    /// on it at all.
    ///
    /// <b>Null rather than an array of -1, for <see cref="_levels"/>'s reason</b>
    /// and it buys the same thing: with no ramps <see cref="Passable"/> falls
    /// back to <see cref="MaxClimb"/>, which is how every board walked before
    /// there were ramps. A relief board with no ramps on it paths exactly as it
    /// did.
    /// </summary>
    private int[]? _ramps;

    /// <summary>Whether this board carries any ramp at all - the flag that
    /// decides whether a cliff may be driven up.</summary>
    public bool HasRamps => _ramps is not null;

    public bool IsRamp(Vector2I cell) => RampHeading(cell) >= 0;

    /// <summary>The world heading of a ramp's high edge, or -1 where the top is
    /// flat.</summary>
    public int RampHeading(Vector2I cell) =>
        _ramps is null || !InBounds(cell) ? -1 : _ramps[cell.Y * Columns + cell.X];

    /// <summary>
    /// How high a cell's surface stands where a tank parked on it would sit, in
    /// screen px off the datum.
    ///
    /// <b>Half a level on a ramp, and that number is the geometry rather than a
    /// choice.</b> The height runs linearly across the cell from its low edge to
    /// its high one, so at the centre - which is where a parked tank stands and
    /// where every leg begins and ends - it is exactly half. It is also where the
    /// two corners on the ramp's own axis sit, which is what the modelled tile
    /// reports.
    ///
    /// <b>Told apart from <see cref="LevelAt"/> on purpose, and both survive.</b>
    /// The integer is what pathing and occlusion are written in - which level a
    /// cell belongs to - and a ramp belongs to its low one. This is where its
    /// surface is. Folding the two into one number would answer two questions
    /// with one, which is the mistake <see cref="HeightBetween"/> already records
    /// having made once.
    /// </summary>
    public float TopAt(Vector2I cell) =>
        LevelAt(cell) * Lift + (IsRamp(cell) ? Lift * 0.5f : 0.0f);

    /// <summary>
    /// How high each of the six corners of a cell's top face stands, in screen px
    /// off the datum, indexed as <see cref="Corners"/> is.
    ///
    /// <b>A rotated pattern, with no trigonometry in it</b> - the same reason
    /// <see cref="Corners"/> has none. The two corners bounding the high edge are
    /// at the full level, the two on the ramp's own axis at half, the two
    /// bounding the low edge at zero: <c>[1, 1, 1/2, 0, 0, 1/2]</c> read from the
    /// high edge's own index. Every corner of a flat cell is at its level, which
    /// is the same expression with no rotation to do.
    /// </summary>
    public float[] TopCorners(Vector2I cell)
    {
        float floor = LevelAt(cell) * Lift;
        var top = new float[6];
        int heading = RampHeading(cell);
        if (heading < 0)
        {
            for (int i = 0; i < 6; i++)
                top[i] = floor;
            return top;
        }
        int high = EdgeIndex(heading);
        for (int i = 0; i < 6; i++)
            top[i] = floor + Lift * RampCorner[((i - high) % 6 + 6) % 6];
        return top;
    }

    private static readonly float[] RampCorner =
        { 1.0f, 1.0f, 0.5f, 0.0f, 0.0f, 0.5f };

    /// <summary>Which edge of <see cref="Corners"/> faces a world heading. Corner
    /// i and i+1 span the edge facing <c>330 - 60i</c>, so this inverts that -
    /// see <see cref="Corners"/> and <see cref="NearHeading"/>, which are the two
    /// places that relation is already relied on.</summary>
    public static int EdgeIndex(int heading) =>
        ((330 - (((heading % 360) + 360) % 360)) / 60 % 6 + 6) % 6;

    public static int Reverse(int heading) => (((heading % 360) + 360) % 360 + 180) % 360;

    /// <summary>
    /// How steep one level is, as a fraction of the distance to a neighbour.
    ///
    /// A grade rather than a height in pixels, so it is the same slope on any
    /// tile: the pixels follow from <see cref="Reach"/>, which is measured off
    /// the rendered hexagon. Named in the one place both the lift and the
    /// climb-cost would read it from.
    /// </summary>
    /// A property rather than a field because the draw order is cached and this
    /// is one of the two things that can change it: depth carries the lift, so a
    /// steeper board can reorder a rise against the flat ground beside it. The
    /// other is <see cref="SetRelief"/>, which clears the same cache. Without
    /// this the slider leaves the board sorted for the grade it had.
    public double StepGrade
    {
        get => _stepGrade;
        set
        {
            if (Math.Abs(value - _stepGrade) < 1e-9)
                return;
            _stepGrade = value;
            _ordered = null;
            QueueRedraw();
        }
    }

    private double _stepGrade = 0.25;

    /// <summary>How many levels a tank may take in one step. One, because two is
    /// a cliff: at this grade a two-level step is half the distance to the
    /// neighbour, and a route that walks a pond up onto a crown in a single hex
    /// reads as the pathing ignoring the board.</summary>
    public int MaxClimb = 1;

    /// <summary>
    /// Put height on the board, and optionally the ramps that let it be driven.
    ///
    /// <b>A ramp is marked, and which way it tilts is measured off the levels.</b>
    /// The high edge is the one facing the neighbour a level above, so a mark
    /// cannot disagree with the field it bridges. Handed the direction instead,
    /// the two can part company - and then the high edge does not meet the wall
    /// it is there to bridge, which is a seam and reports as nothing at all. Same
    /// argument as <c>track_split</c>'s: the tread is measured rather than
    /// marked because a mark that stops early does not read as a smaller tread.
    ///
    /// <b>Ambiguity raises and names the candidates rather than choosing.</b> A
    /// cell with two higher neighbours in two directions genuinely does not
    /// determine which of them its ramp bridges, and picking the first would be
    /// <c>sprite_atlas</c> choosing an axis holder in hash order - a wrong answer
    /// that no output number mentions.
    /// </summary>
    /// <param name="ramps">One flag per cell, or null for a board of flat tops.
    /// </param>
    public void SetRelief(int[]? levels, bool[]? ramps = null)
    {
        _levels = levels is not null && levels.Length == Columns * Rows
            ? levels : null;
        _ramps = _levels is null || ramps is null
                 || ramps.Length != Columns * Rows
            ? null : DeriveRamps(ramps);
        _ordered = null;
        QueueRedraw();
    }

    private int[]? DeriveRamps(bool[] marked)
    {
        var headings = new int[Columns * Rows];
        bool any = false;
        for (int r = 0; r < Rows; r++)
        for (int q = 0; q < Columns; q++)
        {
            int at = r * Columns + q;
            headings[at] = -1;
            var cell = new Vector2I(q, r);
            if (!marked[at])
                continue;
            var up = new List<int>();
            foreach (int heading in EdgeHeadings)
            {
                Vector2I next = Step(cell, heading);
                if (InBounds(next) && LevelAt(next) == LevelAt(cell) + 1)
                    up.Add(heading);
            }
            if (up.Count != 1)
                throw new InvalidOperationException(
                    $"the ramp at ({q},{r}) on level {LevelAt(cell)} has "
                    + (up.Count == 0
                        ? "no neighbour a level above it, so there is nothing "
                          + "for it to bridge"
                        : $"{up.Count} neighbours a level above it - headings "
                          + string.Join(", ", up)
                          + " - so which edge is its high one is not decided by "
                          + "the map"));
            headings[at] = up[0];
            any = true;
        }
        return any ? headings : null;
    }

    /// <summary>
    /// Whether a tank may take the step out of <paramref name="cell"/> along
    /// <paramref name="heading"/>.
    ///
    /// <b>With ramps on the board a cliff is not driveable at all</b>, and that is
    /// the whole point of having them: the level changes where the map says it
    /// may, which is what makes a plateau a place you have to find the way up to
    /// rather than a cell one level higher. Without ramps the old
    /// <see cref="MaxClimb"/> rule stands, so every board that walked before this
    /// walks the same.
    ///
    /// <b>A ramp is entered and left along its own axis only.</b> Across a side
    /// edge its face runs from one level to the other, so it meets a flat
    /// neighbour at one point and stands above or below it everywhere else -
    /// there is no continuous surface to drive over, and the two ends of a tank
    /// crossing there would sit at heights nothing on the board explains.
    /// </summary>
    public bool Passable(Vector2I cell, int heading)
    {
        Vector2I next = Step(cell, heading);
        if (!InBounds(cell) || !InBounds(next))
            return false;
        if (_levels is null)
            return true;
        if (_ramps is null)
            return Math.Abs(LevelAt(next) - LevelAt(cell)) <= MaxClimb;

        int here = RampHeading(cell), there = RampHeading(next);
        int back = Reverse(heading);
        // Off a ramp: along its axis, and the level it reaches is the one that
        // end of the ramp is at.
        if (here >= 0)
        {
            if (heading != here && heading != Reverse(here))
                return false;
            if (there >= 0)
                return false;
            return LevelAt(next) == LevelAt(cell) + (heading == here ? 1 : 0);
        }
        // Onto a ramp: the shared edge seen from the ramp's side.
        if (there >= 0)
        {
            if (back != there && back != Reverse(there))
                return false;
            return LevelAt(cell) == LevelAt(next) + (back == there ? 1 : 0);
        }
        return LevelAt(next) == LevelAt(cell);
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
    /// <b>Level ground was let through here once and it was a wrong turn.</b>
    /// The argument was true as far as it went: a tank's belts hang forward of
    /// the turret axis, so it is drawn reaching 49px below its own contact point
    /// on the heavy, and those pixels are track standing in the next cell. What
    /// was false was that this needed fixing at all. It is how the bench has
    /// drawn a heavy at a boundary since long before there was any height, on a
    /// flat board, and nobody has ever reported it. The report it was written
    /// for was about a <i>raised</i> hex that failed to cover - the depth key was
    /// short by more than a row, see <see cref="GroundRow"/> - and a clause about
    /// level ground could not have fixed it and did not.
    ///
    /// What it cost is worth writing down, because both are the shape of trouble
    /// a whole-cell repaint makes. Admitting cells is all or nothing, so a cell
    /// that qualifies goes down over <b>everything</b> the tank is drawn against:
    /// the only part of the selection ring you ever see is its near edges, which
    /// are exactly the edges shared with the cells in front, so turning relief on
    /// erased the ring. And the test the clause carried - the face may only begin
    /// at or below the contact row - is a test the geometry passes continuously
    /// and then fails all at once, halfway along every leg, so the belt went from
    /// covered to uncovered in one frame on every step between level cells.
    ///
    /// So: only higher ground. On a board with nothing raised that is nothing at
    /// all, and a tank with nothing raised in front of it is drawn frame for
    /// frame as it is on a flat board - which is a picture that can be diffed,
    /// and was.
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
    /// <b><paramref name="box"/> is what the observer occupies</b>, in the same
    /// space, and a cell that cannot touch it is dropped however near and high it
    /// is. That clause reads like an optimisation and is not: admitting a cell is
    /// all or nothing, so a cell admitted goes down over the ruts, the marks and
    /// the board wherever it happens to be. Nothing needed it while the set was
    /// local - a rise in front of a tank is beside that tank - but a tank
    /// <i>descending</i> is below every level cell on the board at once, and one
    /// step into the pit at (6,5) promoted the entire bottom row, (0,5) included,
    /// six columns away. On screen that is the far side of the board blinking and
    /// its belt marks going out, which is what it was reported as.
    /// </summary>
    public List<Vector2I> Occluders(float flatRow, float standing, Rect2? box = null)
    {
        var over = new List<Vector2I>();
        if (_levels is null || Atlas is null)
            return over;
        float depth = Depth(flatRow, standing);
        foreach (Vector2I cell in Ordered())
            if (DepthOf(cell) > depth && LevelAt(cell) * Lift > standing + 0.5f
                && (box is not { } seen || seen.Intersects(Column(cell))))
                over.Add(cell);
        return over;
    }

    /// <summary>Everything a cell paints, top and side, as a box in field space.
    /// The side is the whole drop rather than the clipped one, because this is
    /// asked before anybody knows who is looking.</summary>
    public Rect2 Column(Vector2I cell)
    {
        Vector2 size = Atlas!.HexRect.Size;
        Vector2 centre = CellCentre(cell);
        return new Rect2(centre.X - size.X * 0.5f, centre.Y - size.Y * 0.5f,
                         size.X, size.Y + SkirtDrop(cell));
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
    /// free, without anything that stands on the board knowing about it.
    ///
    /// <b>Off the surface, not off the floor</b>, and on a ramp those are half a
    /// level apart - see <see cref="TopAt"/>. Written as the level's lift, a tank
    /// parked on a ramp stood on the cell's floor while the cell's own top face
    /// rose over it, and the depth buffer ate everything below the turret ring:
    /// measured, the hull and both belts. It read as the tank sinking into the
    /// board, which is what it was doing.</summary>
    public Vector2 CellAnchor(int q, int r) =>
        FlatAnchor(q, r) - new Vector2(0.0f, TopAt(new Vector2I(q, r)));

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
            // <b>A ramp answers for its own level only, and its top half is
            // therefore picked as the cell behind it.</b> Letting it answer for
            // the level above as well was tried and is worse than the thing it
            // fixes: the inverse drops the point by a whole lift and lands it in
            // some cell's footprint from a cell away, so a ramp accepted at
            // level+1 anywhere on its face steals clicks from cells that have
            // nothing to do with it - measured, 48 of 1620 probes, first a point
            // in (2,2) answered by the ramp at (3,2).
            //
            // Answering it properly means inverting a sloped plane rather than a
            // flat one - the lift to undo is a function of where the point lands,
            // which is what this loop over constant lifts cannot express. Half a
            // cell of slop on ramp cells is the price, and it lands on the cell
            // the slope leads to.
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

    /// <summary>Whether a point lies on a cell's top face, both in this space and
    /// both as drawn - so a mark laid on a raised cell is tested against where
    /// that cell is, with no height to put back.
    ///
    /// In units of the drawn tile a flat-top hexagon is |y| &lt;= 1 and
    /// 2|x| + |y| &lt;= 2, which is <see cref="Corners"/> read as inequalities: no
    /// camera angle in it, for the reason there is none there.</summary>
    public bool OnCell(Vector2 point, Vector2I cell)
    {
        if (Atlas is null)
            return false;
        Vector2 d = point - CellCentre(cell);
        float x = Mathf.Abs(d.X) / (Atlas.HexRect.Size.X * 0.5f);
        float y = Mathf.Abs(d.Y) / (Atlas.HexRect.Size.Y * 0.5f);
        return y <= 1.0f && 2.0f * x + y <= 2.0f;
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
                    // route it cannot walk. See Passable, which is where the one
                    // statement of what a step may cross lives.
                    || !Passable(cell, heading))
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
            // By the ground row, and by nothing else. Not by the drawn row -
            // that walks the hill backwards, a raised cell moving up the screen
            // without becoming one step further away - and no longer by
            // <see cref="DepthOf"/> either, which carries the lift.
            //
            // The lift belongs in the key that sorts a cell against a tank,
            // where the question is which of two things at different heights is
            // nearer. It does not belong here, because a cell is not at a
            // height: since <see cref="SkirtDrop"/> every cell is a column
            // standing on the board's floor, and one column is in front of
            // another exactly when its ground is.
            cells.Sort((a, b) => GroundRow(a).CompareTo(GroundRow(b)));
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
    /// <param name="seenFrom">How high the observer this is being painted for
    /// stands, in screen px, or null for the board's own pass. The field paints
    /// whole columns and lets the cells in front cover what they cover;
    /// <see cref="ReliefCap"/> paints one cell over a tank with nothing painted
    /// after it, so it has to say who is looking - see
    /// <see cref="VisibleSide"/>.</param>
    public void PaintCell(CanvasItem into, Vector2I cell, Color tint,
                          float? seenFrom = null)
    {
        if (Atlas is null)
            return;
        DrawWalls(into, cell, seenFrom);

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
    /// <see cref="NearHeading"/>.
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

    /// <summary>The three edges of a flat-top hexagon that face the camera, in
    /// the order <see cref="Corners"/> gives them - so edge <c>i</c> spans
    /// corners <c>i</c> and <c>i+1</c>. The other three face away and are listed
    /// nowhere, which is the point: a column is only ever seen from the front,
    /// and a side extruded from a far edge leaves the hexagon near its vertex
    /// and lands on the cell behind it.</summary>
    public static readonly int[] NearHeading = { 330, 270, 210 };

    /// <summary>
    /// How far below its own near edges a cell's side carries, in screen px.
    ///
    /// <b>Every cell is a column standing on one floor, not a plate with faces
    /// fitted between it and its neighbours.</b> That is the whole of this
    /// change and it replaces a rule per pair of cells - who is higher, which of
    /// the six edges, how many levels between them - with one number per cell
    /// that does not mention neighbours at all.
    ///
    /// The pair rule was tried and each version of it was wrong somewhere,
    /// always for the same underlying reason: <b>a cell is not a point, and
    /// lifting one breaks the tiling.</b> Raise a hexagon and it no longer meets
    /// its neighbours edge to edge - it rides up over the ones behind and pulls
    /// away from the ones in front - so which piece of which cell belongs on top
    /// stops being answerable by one key per cell, and the faces fitted into the
    /// gaps have to be right about a neighbour they are drawn without. Three
    /// reports came out of that, each a different symptom of it.
    ///
    /// A column has no gaps to fit anything into. It is solid from its own top
    /// down to the floor, so <b>whatever is nearer covers it, and that is the
    /// only rule left</b> - see <see cref="Ordered"/>, which sorts on the ground
    /// row and nothing else. The visible height of a cliff is then not computed
    /// at all: it is what is left of the higher column after the lower one in
    /// front has been drawn over it.
    ///
    /// <b>The floor is the lowest level on the board</b>, so the deepest cells
    /// carry no side and a board with no relief carries none anywhere - it draws
    /// exactly as it did, which is the invariant this feature has kept from the
    /// start. The height is sufficient rather than tight: the worst gap a cell
    /// has to cover is a neighbour <c>k</c> levels below it, which opens
    /// <c>k*Lift - 54.5</c> px, and <c>(level - low)*Lift</c> is at least that
    /// for every k it can meet.
    /// </summary>
    public float SkirtDrop(Vector2I cell) =>
        _levels is null ? 0.0f : (LevelAt(cell) - LevelRange.Low) * Lift;

    /// <summary>How high a thing is, <paramref name="done"/> of the way from one
    /// cell to the next, in screen px.
    ///
    /// <b>This is the height that answers what may hide it, and that is the whole
    /// reason it is a function here rather than a line in the harness.</b> There
    /// were two heights - this one for drawing and the higher of the two ends for
    /// occlusion - and the second called a tank up on the crown at the first pixel
    /// of a climb, so it spent the leg drawn over cells at a level it had not
    /// reached. A tank covers a hex when it has come up to that hex's level; the
    /// height it is drawn at is what says whether it has.</summary>
    /// <remarks><b>Off <see cref="TopAt"/> rather than the levels</b>, which is
    /// what carries a ramp: its ends are half a level up, so a leg onto one
    /// climbs half and the leg off it climbs the other half. On a board of flat
    /// tops the two expressions are the same number, which is why nothing else
    /// had to be told.</remarks>
    public float HeightBetween(Vector2I from, Vector2I onto, float done) =>
        Mathf.Lerp(TopAt(from), TopAt(onto), Mathf.Clamp(done, 0.0f, 1.0f));

    /// <summary>How high the surface stands at the middle of the edge
    /// <paramref name="cell"/> shares with <paramref name="toward"/>: the mean of
    /// the two corners bounding it, off <see cref="TopCorners"/> so there is one
    /// description of where a face is and this is not a second one.
    ///
    /// <b>Both cells give the same answer, which is what makes the surface
    /// continuous</b> - a ramp's high edge is a full level up and so is the floor
    /// of the cell it climbs to. That is the rule the ramp's corner pattern exists
    /// to keep, read at the one place two cells meet.</summary>
    public float EdgeTop(Vector2I cell, Vector2I toward)
    {
        int heading = HeadingTo(cell, toward);
        if (heading < 0)
            return TopAt(cell);
        float[] top = TopCorners(cell);
        int edge = EdgeIndex(heading);
        return (top[edge] + top[(edge + 1) % 6]) * 0.5f;
    }

    /// <summary>
    /// How high the <b>ground</b> is under something crossing from one cell to
    /// the next - which is not <see cref="HeightBetween"/>, and the difference is
    /// a quarter of a level.
    ///
    /// <b>HeightBetween is a chord between two cell centres, and a ramp's surface
    /// is not a chord.</b> A ramp rises a whole level across itself, so its centre
    /// is half a level up while the edge being driven at is a full level up: the
    /// straight line between centres runs <c>Lift/4</c> below the face at the
    /// boundary. For anything <i>drawn as</i> a billboard that is a fine
    /// approximation - the sprite is a flat card either way. For anything that
    /// <i>lies on</i> the ground it is not: it sinks under the face it lies on and
    /// the face hides it, which is what the belt marks did on the upper half of
    /// every ramp and the far half of every crown.
    ///
    /// Piecewise through the shared edge, and that is the whole of it: centre to
    /// edge, edge to centre. Exact for a plane rather than close to it, because
    /// each half really is linear - and continuous with the next leg's answer
    /// because <see cref="EdgeTop"/> is.
    /// </summary>
    public float SurfaceBetween(Vector2I from, Vector2I onto, float done)
    {
        float part = Mathf.Clamp(done, 0.0f, 1.0f);
        float edge = EdgeTop(from, onto);
        return part < 0.5f
            ? Mathf.Lerp(TopAt(from), edge, part * 2.0f)
            : Mathf.Lerp(edge, TopAt(onto), (part - 0.5f) * 2.0f);
    }

    /// <summary>
    /// How much of one face of a cell's side an observer standing
    /// <paramref name="standing"/> px off the datum may be shown.
    ///
    /// <b>The field never asks this.</b> It paints the whole column and lets the
    /// cells in front cover what they cover, and paint order gets this exactly
    /// right for free. <see cref="ReliefCap"/> has to compute it, because it puts
    /// one cell back over a tank with nothing painted after it - so whatever it
    /// carries stays.
    ///
    /// Two things bound a face, and both were needed one at a time. The observer
    /// is one: a cliff below the ground a tank stands on is the part the field in
    /// front hides, and carried anyway it ate the top half of the cell in front -
    /// 64030px of it. <b>The neighbour across that very edge is the other</b>, and
    /// leaving it out is the same picture again one level up: two cells of a
    /// plateau side by side, and the back one's face carried a whole lift onto the
    /// front one's top, which is level with it and hides all of it. Per edge
    /// rather than per cell, because the three near edges have three different
    /// neighbours and a plateau's corner cell meets a drop on one and its own kind
    /// on the others.
    /// </summary>
    public float VisibleSide(Vector2I cell, int heading, float standing)
    {
        Vector2I front = Step(cell, heading);
        float held = InBounds(front) ? LevelAt(front) * Lift
                                     : LevelRange.Low * Lift;
        return Mathf.Clamp(LevelAt(cell) * Lift - Mathf.Max(standing, held),
                           0.0f, SkirtDrop(cell));
    }

    /// <param name="seenFrom">How high the one observer this is being painted for
    /// stands, or null for the board's own pass, which paints whole columns. See
    /// <see cref="VisibleSide"/>.</param>
    private void DrawWalls(CanvasItem into, Vector2I cell, float? seenFrom)
    {
        if (_levels is null)
            return;
        float full = SkirtDrop(cell);
        if (full <= 0.0f)
            return;
        Vector2 centre = CellCentre(cell);
        Vector2[] corner = Corners();

        for (int i = 0; i < NearHeading.Length; i++)
        {
            float drop = seenFrom is { } eye
                ? VisibleSide(cell, NearHeading[i], eye) : full;
            if (drop <= 0.0f)
                continue;
            var down = new Vector2(0.0f, drop);
            Vector2 a = centre + corner[i], b = centre + corner[(i + 1) % 6];
            var quad = new[] { a, b, b + down, a + down };
            into.DrawColoredPolygon(quad, WallInk(NearHeading[i]));
        }
    }


    /// <summary>
    /// A face's colour, by which way it faces.
    ///
    /// <b>Flat earth, and it used to be a band of the cell's own ground stretched
    /// down the drop.</b> That version could not disagree with the top about what
    /// kind of ground it was and gave the cut vertical streaks for free, and it
    /// is in the history if it is ever wanted back; what it also did was carry
    /// the grass down the wall, so a cliff read as a smeared top face rather than
    /// as earth under one. A drawn cliff would beat both and drops straight in
    /// here - this is what the board looks like until somebody paints one.
    ///
    /// <b>The two shades are what makes it a block.</b> The face square to the
    /// camera keeps more of the light than the two flanking it; without that, a
    /// column of one colour reads as a hexagon with a skirt, and no amount of
    /// picking the brown fixes it.
    /// </summary>
    private static Color WallInk(int heading)
    {
        float k = heading is 270 or 90 ? 1.0f : 0.74f;
        return new Color(Earth.R * k, Earth.G * k, Earth.B * k);
    }

    /// <summary>The cut earth itself, lit. Brown rather than the ground's own
    /// khaki, which was chosen when the face carried the ground's texture and
    /// only had to agree with it.</summary>
    private static readonly Color Earth = new(0.42f, 0.30f, 0.20f);
}
