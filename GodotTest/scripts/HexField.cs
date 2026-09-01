using System;
using System.Collections.Generic;
using System.Linq;
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
    /// rosette in <c>BoardMap.BenchRelief</c> is a hill with a ramp on each of its six
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

    /// <summary>
    /// How high the surface stands at a <b>point</b> of the ground rather than at
    /// a cell, in screen px off the datum. The flat-space point, which is the
    /// drawn one with its lift put back on - <see cref="CellAt"/>'s convention
    /// inverted, and the one <c>Stage3D.World</c> reads.
    ///
    /// <b>A cell answers for a cell, and a ramp's face is not one height.</b>
    /// <see cref="TopAt"/> is the centre of the face and <see cref="EdgeTop"/> is
    /// the middle of one edge; anything laid across a whole cell - the selection
    /// ring is the case that wanted this - needs the height under each of its own
    /// corners, and on a ramp those differ by a full level. Given one height, such
    /// a thing lies flat while the face beneath it tilts, and the uphill half
    /// sinks under the face and is hidden by it. That is the same failure
    /// <see cref="MarkBetween"/> records for the belt marks, in the other axis:
    /// there the chord ran under the face along the path, here the shape runs
    /// under it across the cell.
    ///
    /// <b>One plane, so one triangle of it is the whole of it.</b> A ramp's top
    /// rises linearly from its low edge to its high one, so the height is affine
    /// in position: read off the centre and two corners, it is exact everywhere on
    /// the face and exact <i>off</i> it too. That matters, because a shape centred
    /// on a tank between cells has corners over the neighbours, and the neighbour's
    /// own plane agrees with this one along the edge they share -
    /// <see cref="EdgeTop"/> is why - so extrapolating and switching cells give the
    /// same number and the result is continuous either way.
    /// </summary>
    public float TopAtPoint(Vector2 flat) =>
        Atlas is null ? 0.0f : TopOn(CellUnder(flat), flat);

    /// <summary>The same plane read for a <b>named</b> cell, which is how anything
    /// that has to keep its shape asks.
    ///
    /// <b>Extrapolating one cell's face beats sampling whatever each point is
    /// over, and both were built.</b> A shape a cell wide has corners past its own
    /// cell whenever the thing carrying it stands between two, and asked point by
    /// point those corners take the ground they are over - so a ring crossing a
    /// ramp's rim had four of its six on the flat either side and stopped being a
    /// hexagon at all. Measured mid-climb: 750 px of band left of 1341, and fitting
    /// one plane through the six samples instead was worse still, 0 px at the worst
    /// frame, because four low corners drag the whole plane under the face. The
    /// face a tank is on is one plane and the mark on it is that plane's hexagon;
    /// where it overhangs, it overhangs.</summary>
    public float TopOn(Vector2I cell, Vector2 flat)
    {
        if (Atlas is null)
            return 0.0f;
        float middle = TopAt(cell);
        if (!IsRamp(cell))
            return middle;
        float[] top = TopCorners(cell);
        Vector2[] corner = Corners();
        Vector2 d = flat - (FlatAnchor(cell) + CentreOffset);
        // d in the basis of the first two corner offsets; the pair spans the
        // plane because no two corners of a hexagon are colinear with its centre.
        float det = corner[0].X * corner[1].Y - corner[0].Y * corner[1].X;
        if (Mathf.Abs(det) < 1e-6f)
            return middle;
        float a = (d.X * corner[1].Y - d.Y * corner[1].X) / det;
        float b = (corner[0].X * d.Y - corner[0].Y * d.X) / det;
        return middle + a * (top[0] - middle) + b * (top[1] - middle);
    }

    /// <summary>Which cell a point of the ground lies on, the point given in flat
    /// space - <see cref="CellAt"/> with the lift already put back on rather than
    /// hunted for a level at a time. Clamped, so a shape whose far corner has
    /// strayed off the board is answered by the edge cell rather than by
    /// nothing.</summary>
    public Vector2I CellUnder(Vector2 flat) => ClampCell(FlatCellAt(flat));


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

    /// <summary>
    /// Which edge of each marked cell is its high one, or a throw naming the
    /// first cell where the map does not decide.
    ///
    /// <b>The rule lives in <see cref="MapRules"/> and this asks it.</b> It was
    /// written three times - here, in the audit and in the self test - and three
    /// copies of one rule agree until somebody edits one; see that type's note
    /// for what that already cost once.
    ///
    /// <b>Only the bridge rule throws, and that has not changed.</b>
    /// <see cref="MapRules.Footed"/> is the check this deliberately does not
    /// make: a ramp that can only be entered from the top is a dead end and not
    /// a board the field cannot build, so it stays something the audit reports
    /// and the field draws.
    /// </summary>
    private int[]? DeriveRamps(bool[] marked)
    {
        MapRules.Draft draft = Drafted(marked);
        foreach (MapRules.Fault fault in MapRules.RampFaults(draft))
            if (fault.Rule == "ramp.bridge")
                throw new InvalidOperationException(fault.Text);

        var headings = new int[Columns * Rows];
        bool any = false;
        for (int r = 0; r < Rows; r++)
        for (int q = 0; q < Columns; q++)
        {
            int at = r * Columns + q;
            headings[at] = -1;
            if (!marked[at])
                continue;
            headings[at] = MapRules.Above(draft, new Vector2I(q, r))[0];
            any = true;
        }
        return any ? headings : null;
    }

    /// <summary>
    /// The board as <see cref="MapRules"/> sees it: this field's own arrays, and
    /// nothing the rules do not read.
    ///
    /// <b>The field is handed boards that are not maps on purpose</b> - the self
    /// test flattens one to ask the shoreline about a made-up mask - so this
    /// carries no homes and no cliffs, and the rules that need those are the ones
    /// a whole map is asked and this is not.
    /// </summary>
    private MapRules.Draft Drafted(bool[]? ramps = null) => new()
    {
        Columns = Columns,
        Rows = Rows,
        Levels = _levels ?? new int[Columns * Rows],
        Ramps = ramps ?? Marked(),
        Water = _water ?? new bool[Columns * Rows],
        Ground = _ground ?? new Foundation[Columns * Rows],
        Cliffs = new bool[Columns * Rows],
        Plot = _plot,
    };

    /// <summary>The derived headings read back as the flags they came from, for
    /// a rule that asks whether a cell carries a ramp rather than which way it
    /// tilts.</summary>
    private bool[] Marked()
    {
        var marked = new bool[Columns * Rows];
        if (_ramps is not null)
            for (int at = 0; at < marked.Length; at++)
                marked[at] = _ramps[at] >= 0;
        return marked;
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
        // The floor, before any of the height arithmetic. Rock and open water
        // are refusals of the same kind as the board's edge - nothing about the
        // step across it makes them drivable - and off-board cells come through
        // here too, which is where Plot finally reaches the pathing. Rock was
        // already unreachable by standing two levels up; saying it here as well
        // costs nothing and means a board that flattens its relief does not
        // quietly open its cliffs.
        if (!TerrainRules.Drivable(FoundationAt(next)))
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

    // --- water ---------------------------------------------------------------

    /// <summary>
    /// Which cells are under water, or null for a dry board.
    ///
    /// <b>A map of its own, beside the levels and the ramps rather than inside
    /// either.</b> It is not paint - <see cref="TerrainSet"/>'s note says a kind
    /// does not cost movement, and this one does, so this is where that table
    /// goes. It is not a level either: a tank in a ford stands on the bottom,
    /// which is the cell's own <see cref="TopAt"/> unchanged, and what is added
    /// is a surface above it. So none of the climbing arithmetic moves.
    ///
    /// Null rather than an array of false, for <see cref="_levels"/>'s reason and
    /// buying the same thing: a board with no water on it is the board that was
    /// here before, down to the sort order of what is drawn on it.
    /// </summary>
    private bool[]? _water;

    /// <summary>Whether this board carries water at all.</summary>
    public bool HasWater => _wet.Count > 0;

    /// <summary>
    /// Whether a cell is under water.
    ///
    /// <b>Two authors, one answer.</b> The flood grid is how every board here
    /// has said it - it has a guard, a panel switch and an ordering that the
    /// mesh and the swell index by - and a map may also name the floor as
    /// shallow or deep outright, which is what the floor being one slot means.
    /// A reader that had to ask both would eventually ask one, so this is the
    /// only place the two meet.
    /// </summary>
    public bool IsWater(Vector2I cell)
    {
        if (!InBounds(cell))
            return false;
        int at = cell.Y * Columns + cell.X;
        return (_water is not null && _water[at])
               || (_ground is not null
                   && (_ground[at] == Foundation.Shallow
                       || _ground[at] == Foundation.Deep));
    }

    /// <summary>
    /// How deep the water stands, as a fraction of one level.
    ///
    /// <b>A fraction, never pixels</b>, for the reason every other size in this
    /// project is: pixels follow from the rendered tile, so a figure in them is a
    /// guess at a number the atlas has already decided. It is judged by neither,
    /// though - what settles it is how much of the drawn tank goes under, which is
    /// what the check measures.
    ///
    /// Deep enough to swallow the running gear and the lower hull and no deeper:
    /// a tank in a ford is a tank driving, and one submerged to the turret ring is
    /// a tank sinking. Those are different pictures and only one of them was asked
    /// for.
    /// </summary>
    public double WaterDepth = 0.55;

    /// <summary>The depth in screen px, which is what everything drawn reads.
    /// </summary>
    public float WaterRise => Lift * (float)WaterDepth;

    /// <summary>
    /// Where the surface of the water over a cell stands, in screen px off the
    /// datum - the cell's <i>floor</i> plus the depth. Meaningless on a dry cell,
    /// and the callers ask <see cref="IsWater"/> first.
    ///
    /// <b>The floor and not <see cref="TopAt"/>, and that is the whole of what
    /// lets a ramp be flooded.</b> On a flat cell the two are the same number, so
    /// every board drawn before this reads bit for bit as it did. On a ramp TopAt
    /// is the middle of the slope, half a level up, so measured from it the
    /// beach's water would stand half a level over the pond it belongs to - a
    /// waterfall along their shared edge, which is the one thing
    /// <see cref="SetWater"/> still refuses. A ramp's floor is its own level, the
    /// same number its flat neighbours carry, so one body of water comes out one
    /// surface by construction rather than by a check.
    /// </summary>
    public float WaterTop(Vector2I cell) => LevelAt(cell) * Lift + WaterRise;

    /// <summary>
    /// Put water on the board.
    ///
    /// <b>One guard, and it refuses rather than repairs</b> - the shape
    /// <see cref="DeriveRamps"/> already has, and for its reason: a map that does
    /// not decide something cannot be made to decide it by picking. <b>A body is
    /// flat.</b> Two water cells side by side at different levels is a waterfall,
    /// which is a different thing entirely and is not what this draws. Asked of
    /// neighbours rather than of the whole board, because two unconnected ponds at
    /// two levels are perfectly reasonable.
    ///
    /// <b>There were two, and the second was "not on a ramp".</b> Its reason - a
    /// slope has no single surface height - was true about the slope and false
    /// about the water: the water is flat, and what a ramp does is cross it. What
    /// the refusal actually bought was that every reader could take a cell's water
    /// as covering the whole of it, and what it cost was the one cell that has to
    /// be half wet: a beach came out as dry sand with a pane of water standing
    /// beside it, and the ground over the back <see cref="WaterDepth"/> of its run
    /// lay under the pond's own surface and was drawn dry. So the surface now runs
    /// onto the ramp at <see cref="WaterTop"/> - the pond's height, because that
    /// is measured off the floor - and the slope cuts it: the ground is opaque and
    /// higher ground is nearer the camera, so the depth buffer clips the water at
    /// exactly the line where the two planes meet, with no shape to author and no
    /// number to keep in agreement.
    ///
    /// Nothing here touches <see cref="Passable"/>. Water is not a wall - the
    /// whole request was a cell a tank can drive into - so the pathing does not
    /// learn a new rule, and the cost of being in it is a speed and not a refusal.
    /// </summary>
    public void SetWater(bool[]? water)
    {
        bool[]? held = _water;
        _water = water is not null && water.Length == Columns * Rows
                 && Array.Exists(water, on => on)
            ? water : null;
        try
        {
            Flat();
        }
        catch (InvalidOperationException)
        {
            _water = held;
            throw;
        }
        Rewet();
    }

    /// <summary>
    /// The guard: one body of water is one surface.
    ///
    /// Two water cells side by side at different levels is a waterfall, which is
    /// a different thing entirely and is not what this draws. Asked of
    /// neighbours rather than of the whole board, because two unconnected ponds
    /// at two levels are perfectly reasonable.
    ///
    /// <b>Asked of <see cref="IsWater"/> rather than of the grid handed in</b>,
    /// so that it judges the board as it will be read - a map that names its
    /// deep water on the floor gets the same guard as one that hands in a flood
    /// mask, and neither can open a waterfall the other would have been refused.
    /// </summary>
    private void Flat()
    {
        // The rule itself is MapRules.StepFaults - see DeriveRamps on why it is
        // there rather than here. What stays is the shape of the refusal: every
        // offending pair in one message, because a waterfall is usually a row of
        // them and one at a time is one round trip each.
        List<string> stepped = MapRules.StepFaults(Drafted())
            .Select(f => f.Brief).ToList();
        if (stepped.Count > 0)
            throw new InvalidOperationException(
                "one body of water is one surface, and these neighbours are "
                + "on two levels, which is a waterfall: "
                + string.Join("; ", stepped.Distinct()));
    }

    /// <summary>The flooded cells in one fixed order, settled here and nowhere
    /// else. Two things index by it - the surface mesh writes an ordinal into
    /// each vertex, and the swell keeps a state per cell - and a second ordering
    /// beside this one is two lists that agree until somebody floods a
    /// cell.</summary>
    private void Rewet()
    {
        _wet = new List<Vector2I>();
        for (int r = 0; r < Rows; r++)
        for (int q = 0; q < Columns; q++)
            if (IsWater(new Vector2I(q, r)))
                _wet.Add(new Vector2I(q, r));
        _wetIndex = new Dictionary<Vector2I, int>();
        for (int i = 0; i < _wet.Count; i++)
            _wetIndex[_wet[i]] = i;
        _ordered = null;
        QueueRedraw();
    }

    /// <summary>
    /// Whether a leg has water at either end - the question the speed ceiling
    /// asks, shaped exactly as <see cref="IsGrade"/> is.
    ///
    /// <b>Either end, so entering and leaving both cost.</b> Wading out of a ford
    /// onto a bank is the harder half of it, and capping only the way in is the
    /// slip the grade's own note names.
    /// </summary>
    public bool IsWet(Vector2I from, Vector2I onto) =>
        IsWater(from) || IsWater(onto);

    private List<Vector2I> _wet = new();
    private Dictionary<Vector2I, int> _wetIndex = new();

    /// <summary>The flooded cells in a fixed order. The surface mesh writes each
    /// cell's place in this list into its own vertices and <see cref="Swell"/>
    /// keeps one state per entry, so this is the only ordering there is.</summary>
    public IReadOnlyList<Vector2I> WaterCells => _wet;

    /// <summary>Where a cell sits in <see cref="WaterCells"/>, or -1 for dry
    /// ground.</summary>
    public int WaterIndex(Vector2I cell) =>
        _wetIndex.TryGetValue(cell, out int i) ? i : -1;

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
    /// edge, which is most of the border.
    ///
    /// <b>Public as the unclamped half of <see cref="CellUnder"/>.</b> That one
    /// clamps on purpose, for a shape whose far corner has strayed off the
    /// board; a probe stepping a radius across the ground wants the opposite -
    /// off the board is nothing at all, and clamping would have it reach into
    /// the edge cell it never touched.</summary>
    public Vector2I FlatCellAt(Vector2 local)
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

    /// <summary>
    /// Which cells of the rectangle are actually board, or null for all of them.
    ///
    /// A rectangle is what a grid of columns and rows is, and a board that is a
    /// rosette of seven hexes is not one - see <see cref="WoodBench"/>, where the
    /// whole subject is one wood and its six neighbours. Written as a hole in
    /// <see cref="InBounds"/> rather than as a second shape to iterate, because
    /// that one predicate is already what says where the board stops: the fire
    /// asks it before it spreads, the wood asks it before it sows and fades, and
    /// <see cref="Ordered"/> is what paints. A second list of cells would be a
    /// second answer to "is this cell on the board", and the two would part on
    /// the one cell nobody checked.
    ///
    /// Null on every board drawn so far, so the rectangle is still the default
    /// and nothing that reads a full board changes.
    /// </summary>
    public IReadOnlySet<Vector2I>? Plot
    {
        get => _plot;
        set
        {
            _plot = value;
            // The paint order is cached by the rectangle it was built for, and a
            // plot is a change to that rectangle without the numbers moving.
            _orderedFor = new Vector2I(-1, -1);
        }
    }

    private IReadOnlySet<Vector2I>? _plot;

    public bool InBounds(Vector2I cell) =>
        cell.X >= 0 && cell.X < Columns && cell.Y >= 0 && cell.Y < Rows
        && (_plot is null || _plot.Contains(cell));

    /// <summary>The nearest cell of the <i>rectangle</i>, which on a board with a
    /// <see cref="Plot"/> may be a cell that is not on it. Clamping into the plot
    /// would need a nearest-cell search over a shape, and every caller that
    /// cares already asks <see cref="InBounds"/> - a click off the board is a
    /// click off the board, not a click on the cell beside it.</summary>
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

    /// <summary>The cells on the straight line from <paramref name="from"/> to
    /// <paramref name="to"/>, both ends included, each adjacent to the next.
    ///
    /// <b>For a crossing nobody watched frame by frame.</b> A diff of "which
    /// cell now" against "which cell last time" normally moves one step at a
    /// time, but held through a pivot or a long frame the pair can come apart
    /// by more than one cell - and every rim between the two was still
    /// crossed. The standard hex line: lerped in cube coordinates and rounded;
    /// the nudge keeps a sample that lands exactly on an edge from flipping
    /// sides mid-line, which is what makes consecutive cells neighbours.
    /// </summary>
    public static List<Vector2I> Chain(Vector2I from, Vector2I to)
    {
        Vector2I a = OffsetToAxial(from), b = OffsetToAxial(to);
        int n = (Mathf.Abs(a.X - b.X) + Mathf.Abs(a.Y - b.Y)
                 + Mathf.Abs(a.X + a.Y - b.X - b.Y)) / 2;
        var line = new List<Vector2I>(n + 1);
        for (int i = 0; i <= n; i++)
        {
            float t = n == 0 ? 0.0f : (float)i / n;
            line.Add(AxialToOffset(CubeRound(
                Mathf.Lerp(a.X + 1e-4f, b.X + 1e-4f, t),
                Mathf.Lerp(a.Y - 2e-4f, b.Y - 2e-4f, t))));
        }
        return line;
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
                                   IReadOnlySet<Vector2I>? blocked = null,
                                   bool masonry = false)
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
                    || !Passable(cell, heading)
                    // And the masonry, which is a statement about the edge
                    // rather than about either cell - asked for rather than
                    // assumed, because driving into a wall is a legal order and
                    // the bench that does it is not asking for a route that
                    // respects one. See Blocked.
                    || (masonry && Blocked(cell, heading)))
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

    private static Vector2I OffsetToAxial(Vector2I cell) =>
        new(cell.X, cell.Y - (cell.X - (cell.X & 1)) / 2);

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
    ///
    /// <b>An authored board sits under a named plate and over the hash.</b> The
    /// dropdown is an A/B tool and <c>--terrain plain</c> has to mean one plate
    /// everywhere, so it wins; the hash is what a board that said nothing gets,
    /// so it loses. A map that named its woods and got as many again from the
    /// hash has not named anything - measured on the first render: thirty two
    /// wooded cells drawn and trees over half the board.
    /// </summary>
    public string KindAt(Vector2I cell)
    {
        if (Terrain is null || !Terrain.Any)
            return TerrainSet.Plain;
        if (Paint == TerrainSet.Forest)
            return TerrainSet.Forest;
        if (Terrain.Has(Paint))
            return Paint;
        if (_kinds is not null && InBounds(cell))
        {
            string? want = _kinds[cell.Y * Columns + cell.X];
            if (want == TerrainSet.Forest && Trees)
                return TerrainSet.Forest;
            if (want is not null && want != TerrainSet.Forest
                && Terrain.Has(want))
                return want;
        }
        // In the mix it is one kind among the plates, drawn by the same hash, so
        // "how much forest" is not a second dial arguing with the terrain list.
        // Except on an authored board, where the hash may vary the dirt and may
        // not add trees: see above.
        return _kinds is null && Trees
               && Terrain.MixedAt(cell, TerrainSet.Forest) is var mixed
            ? mixed : Terrain.MixedAt(cell);
    }

    /// <summary>
    /// What each cell is made of, one entry per cell, null to leave it to the
    /// mix - or null altogether for a board that authored nothing.
    ///
    /// <b>A fourth grid beside the levels, the ramps and the flood, and for
    /// their reason.</b> <see cref="KindAt"/> hashes, which is the right answer
    /// for a board that wants variety and the wrong one for a board that wants a
    /// shape - the same argument by which <see cref="ReliefBench"/> refuses to
    /// hash its heights.
    ///
    /// <b>An array of nothing is not an authored board.</b> The bench hands in
    /// one entry per cell and every one of them null, and read as authorship
    /// that is a board declaring it has no woods - which took the trees off the
    /// bench and turned three known grove failures into seven.
    /// </summary>
    public void SetKinds(string?[]? kinds)
    {
        _kinds = kinds is not null && kinds.Length == Columns * Rows
                 && Array.Exists(kinds, k => k is not null)
            ? kinds : null;
        QueueRedraw();
    }

    /// <summary>Whether a board authored its kinds. Read by nothing that draws;
    /// it is here so the self test can tell a board that said nothing from one
    /// that said soil everywhere.</summary>
    public bool HasKinds => _kinds is not null;

    private string?[]? _kinds;

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

    // --- the two slots -----------------------------------------------------

    /// <summary>
    /// What each cell's floor is made of, one entry per cell - or null for a
    /// board that said nothing and is solid ground all over.
    ///
    /// <b>Water is not in here, and that is the one seam left in the model.</b>
    /// <see cref="FoundationAt"/> reads a flooded cell as
    /// <see cref="Foundation.Shallow"/>, so every reader gets the one slot the
    /// model promises; the authoring stays split because the flood has a guard
    /// (<see cref="SetWater"/> refuses a waterfall), an ordering
    /// (<see cref="WaterCells"/>, which the surface mesh and the swell index by)
    /// and a panel switch, and none of those has an equivalent here. So the read
    /// is one slot and the write is still two, which is a thing to finish rather
    /// than a thing to defend.
    ///
    /// Off-board cells are not in here either: <see cref="Plot"/> already says
    /// which cells are on the board, and a second copy of that is two lists that
    /// agree until somebody edits a map.
    /// </summary>
    private Foundation[]? _ground;

    /// <summary>What stands on each cell, one entry per cell - or null for a
    /// board with nothing on it.</summary>
    private Cover[]? _cover;

    /// <summary>What state each cover is in. Beside <see cref="_cover"/> rather
    /// than packed with it, because the state changes during play - a wood
    /// catches, a wall is breached - and the cover itself does not.</summary>
    private CoverState[]? _coverState;

    /// <summary>Whether this board authored either slot. Read by nothing that
    /// draws; it is here so the self test can tell a board that said nothing
    /// from one that said solid everywhere.</summary>
    public bool HasGround => _ground is not null;

    public bool HasCover => _cover is not null;

    /// <summary>
    /// Lay the floor. One entry per cell, null to take the whole board back to
    /// solid ground.
    ///
    /// Nothing here refuses anything, unlike <see cref="SetWater"/>: a floor
    /// makes no claim about its neighbours, so there is no arrangement of
    /// materials that is a mistake in the way a waterfall is.
    /// </summary>
    public void SetGround(Foundation[]? ground)
    {
        Foundation[]? held = _ground;
        _ground = ground is not null && ground.Length == Columns * Rows
            ? ground : null;
        // A material makes no claim about its neighbours, so there is no
        // arrangement of them that is a mistake - except the one that is also
        // water, which is judged by the same guard the flood is. See Flat.
        try
        {
            Flat();
        }
        catch (InvalidOperationException)
        {
            _ground = held;
            throw;
        }
        Rewet();
    }

    /// <summary>
    /// Put the cover on. One entry per cell; the states are optional and default
    /// to <see cref="CoverState.Intact"/>.
    ///
    /// <b>An array of nothing is not an authored board</b> -
    /// <see cref="SetKinds"/>'s rule, and it is here for the same reason: a
    /// board handing in one entry per cell and every one of them
    /// <see cref="Cover.None"/> is a board declaring it has no woods, which is
    /// a different statement from a board that never spoke. See
    /// <see cref="CoverAt"/>, where the difference shows.
    /// </summary>
    public void SetCover(Cover[]? cover, CoverState[]? states = null)
    {
        _cover = cover is not null && cover.Length == Columns * Rows
                 && Array.Exists(cover, c => c != Cover.None)
            ? cover : null;
        _coverState = _cover is null ? null
            : states is not null && states.Length == Columns * Rows
                ? states : new CoverState[Columns * Rows];
        // A walled cell comes up sealed. A map letter says a cell carries
        // masonry and cannot say which edges - the wall that knows is the one
        // somebody builds - so the board's own answer is the whole ring, and
        // whoever stands a prop on it writes the real sides over this. The
        // alternative is a walled cell that bars nothing until a bench has run,
        // which is a board that reads differently depending on the scene.
        _sides = _cover is null ? null : new byte[Columns * Rows];
        if (_cover is not null)
            for (int at = 0; at < _cover.Length; at++)
                if (_cover[at] == Cover.Walls)
                    _sides![at] = AllSides;
        QueueRedraw();
    }

    /// <summary>
    /// The floor under a cell.
    ///
    /// Off the board first, then the water, then what the map laid: the two that
    /// come first are the two that are true whatever the material underneath is.
    /// A flooded rock is water, and a tank in it is in water.
    /// </summary>
    public Foundation FoundationAt(Vector2I cell)
    {
        if (!InBounds(cell) || (Plot is not null && !Plot.Contains(cell)))
            return Foundation.Void;
        Foundation laid = _ground is null
            ? Foundation.Solid : _ground[cell.Y * Columns + cell.X];
        // A floor that named water is water, at the depth it named. One that did
        // not is water only if the flood mask says so, and then it is the ford
        // every board on this project has had.
        if (laid == Foundation.Shallow || laid == Foundation.Deep)
            return laid;
        return IsWater(cell) ? Foundation.Shallow : laid;
    }

    /// <summary>
    /// What stands on a cell.
    ///
    /// <b>A hashed wood is a wood.</b> A board that authored no cover still has
    /// trees on it - <see cref="KindAt"/> scatters them - and a cell drawn with
    /// trees standing on it that answers <see cref="Cover.None"/> is one answer
    /// too many. So the drawn woods fall through to here until the art layer is
    /// folded in behind this one; on an authored board the hash may not add
    /// woods at all, so the fallback cannot contradict a map that spoke.
    /// </summary>
    public Cover CoverAt(Vector2I cell)
    {
        if (!InBounds(cell))
            return Cover.None;
        if (_cover is not null)
        {
            Cover laid = _cover[cell.Y * Columns + cell.X];
            if (laid != Cover.None)
                return laid;
        }
        return Trees && KindAt(cell) == TerrainSet.Forest
            ? Cover.Forest : Cover.None;
    }

    /// <summary>What state the cover on a cell is in - intact where the board
    /// has no state grid, and intact on a cell with no cover, which is the one
    /// state <see cref="TerrainRules.Allows"/> gives
    /// <see cref="Cover.None"/>.</summary>
    public CoverState CoverStateAt(Vector2I cell)
    {
        if (!InBounds(cell))
            return CoverState.Intact;
        // Masonry does not carry a state of its own: it stands on edges, so what
        // it is worth is the six sides and the cell's word for it is a reading
        // off them. Written down beside them it would be a second answer, and
        // the two would agree until one leaf came down.
        if (CoverAt(cell) == Cover.Walls)
            return SidesAt(cell) == 0 ? CoverState.Breached : CoverState.Intact;
        return _coverState is null || CoverAt(cell) == Cover.None
            ? CoverState.Intact : _coverState[cell.Y * Columns + cell.X];
    }

    /// <summary>
    /// Put the cover on a cell into a state - what a fire spreading or a wall
    /// coming down does to the board.
    ///
    /// <b>Refuses rather than repairs</b>, and refuses off the table: a wood
    /// cannot be breached and a wall cannot burn, and the list of what each may
    /// be is <see cref="CoverRule.States"/>. A caller that gets false has asked
    /// for something that is not a state of the world, and the one thing that
    /// must not happen is that it quietly becomes one.
    /// </summary>
    /// <returns>Whether the cell took it.</returns>
    public bool SetCoverState(Vector2I cell, CoverState state)
    {
        if (!InBounds(cell))
            return false;
        // Asked of the effective cover rather than of the grid, so a hashed wood
        // can be told it is on fire. CoverAt already answers Forest for a cell
        // with trees drawn on it and nothing authored - the board is what
        // scattered them - and a cell that burns on screen while the board says
        // it is untouched is the one answer too many this layer exists to stop.
        // The grid is made here rather than at load for the reason it is null in
        // the first place: a board with nothing on it is the board that was here
        // before, down to what SetCover reads back.
        Cover over = CoverAt(cell);
        if (over == Cover.None || !TerrainRules.Allows(over, state))
            return false;
        _cover ??= new Cover[Columns * Rows];
        _cover[cell.Y * Columns + cell.X] = over;
        // Except masonry, whose state is a reading off its sides - see
        // CoverStateAt. Refused rather than accepted and ignored: a caller that
        // wrote Breached here and then read Intact back would have been told the
        // opposite of what happened. Knock a side down with Breach, or lay the
        // whole ring with SetSides.
        if (over == Cover.Walls)
            return false;
        _coverState ??= new CoverState[Columns * Rows];
        _coverState[cell.Y * Columns + cell.X] = state;
        QueueRedraw();
        return true;
    }

    /// <summary>
    /// Which edges of a cell carry standing masonry, one bit per
    /// <see cref="EdgeHeadings"/> index - or null on a board with no walls.
    ///
    /// <b>Per edge, because that is where a wall stands.</b> A wall is not on a
    /// cell the way a wood is; it is on the rim, and a ring with one side driven
    /// through is a ring a tank drives out of and shoots out of on that side and
    /// no other. The cell was the wrong resolution for both questions, and the
    /// shooting half already knew it - <see cref="WallProp.Bars"/> answers per
    /// direction by walking the bricks. This is the board's own record of the
    /// same fact, kept so that the rules can be asked of a board nobody has
    /// built props for.
    ///
    /// <b>A cell's own six, not the edges it shares.</b> Two neighbours may each
    /// carry masonry on the edge between them - two rings side by side do - so
    /// ownership is not a question that has one answer. What crossing that edge
    /// costs is asked of both, in <see cref="Walled"/>.
    /// </summary>
    private byte[]? _sides;

    /// <summary>Every side of a cell, as a mask. The value a cell with a full
    /// ring on it carries.</summary>
    public const int AllSides = 0b111111;

    /// <summary>Which of a cell's own six edges carry standing masonry.</summary>
    public int SidesAt(Vector2I cell) =>
        _sides is null || !InBounds(cell) || CoverAt(cell) != Cover.Walls
            ? 0 : _sides[cell.Y * Columns + cell.X];

    /// <summary>Whether this cell carries masonry on this side of itself. The
    /// half-question; <see cref="Walled"/> is the one worth asking.</summary>
    public bool SideStands(Vector2I cell, int heading) =>
        (SidesAt(cell) & (1 << EdgeIndex(heading))) != 0;

    /// <summary>
    /// Whether standing masonry lies across the edge between a cell and its
    /// neighbour in a heading - <b>the question everything asks</b>.
    ///
    /// Both sides of the edge, because a wall belongs to the cell it was laid on
    /// and two cells may each have laid one. Nothing crosses an edge either of
    /// them walled, and it does not matter which.
    /// </summary>
    public bool Walled(Vector2I from, int heading) =>
        SideStands(from, heading)
        || SideStands(Step(from, heading), Reverse(heading));

    /// <summary>
    /// Lay the masonry on one cell: which of its edges stand, as a mask.
    ///
    /// Written by whoever built the wall, because the geometry is what knows -
    /// <see cref="WallProp"/> measures its own standing bricks per heading
    /// rather than deriving the sides from the bearing it was laid on, and this
    /// is where that measurement is put so the board can be asked without it.
    /// </summary>
    public void SetSides(Vector2I cell, int mask)
    {
        if (!InBounds(cell))
            return;
        _sides ??= new byte[Columns * Rows];
        _sides[cell.Y * Columns + cell.X] = (byte)(mask & AllSides);
        QueueRedraw();
    }

    /// <summary>
    /// Bring one side of a cell's masonry down - what a ram through a leaf or a
    /// round into it does to the board.
    ///
    /// One side, never the ring: a wall that came down whole because a tank went
    /// through one leaf of it is the thing the six sides exist to stop being
    /// said. <see cref="CoverStateAt"/> reads Breached when the last of them
    /// goes.
    /// </summary>
    /// <returns>Whether that side was standing until now.</returns>
    public bool Breach(Vector2I cell, int heading)
    {
        if (!SideStands(cell, heading))
            return false;
        _sides![cell.Y * Columns + cell.X] &= (byte)~(1 << EdgeIndex(heading));
        QueueRedraw();
        return true;
    }

    /// <summary>The whole cell in one value - both slots, the state, and the
    /// height axis beside them. The one read; see <see cref="HexFace"/> for why
    /// there is one.</summary>
    public HexFace FaceAt(Vector2I cell) => new()
    {
        Ground = FoundationAt(cell),
        Over = CoverAt(cell),
        State = CoverStateAt(cell),
        Level = LevelAt(cell),
        Ramp = RampHeading(cell),
    };

    /// <summary>Whether the cover on a cell stops a round crossing it. Asked by
    /// <see cref="Gunnery.Solve"/> exactly where it asks whether a tank is in
    /// the way, because from the shell's point of view those are the same
    /// question.</summary>
    public bool Screened(Vector2I cell) =>
        TerrainRules.Screens(CoverAt(cell), CoverStateAt(cell));

    /// <summary>
    /// Whether a step from a cell in a heading is refused by what stands on the
    /// edge - the cover half of the question <see cref="Passable"/> answers for
    /// the ground.
    ///
    /// <b>Apart from Passable, and the difference is the whole of how a ram
    /// works.</b> Passable is asked of every step including the last, so masonry
    /// folded into it would make a walled cell unreachable and driving into a
    /// wall an impossible order. Kept apart, the route respects the leaf and a
    /// ram is still something a caller can ask for: <see cref="FindPath"/> takes
    /// it as a flag, and the wall bench - whose whole subject is driving into
    /// masonry - leaves it off on purpose.
    /// </summary>
    public bool Blocked(Vector2I from, int heading) =>
        TerrainRules.Of(Cover.Walls).Blocks && Walled(from, heading);

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
                if (InBounds(new Vector2I(q, r)))
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
    /// <summary>
    /// A mark the scene paints itself, asked before the field's own three.
    ///
    /// <b>A hook rather than a fourth list, for <c>TankTick.Launch</c>'s
    /// reason.</b> The three below are the harness's marks - an arc, a route, an
    /// aim - and they are what a board in a game has on it. The map editor
    /// paints faults, legal ramp sites and the cell under the brush, and none of
    /// those is a thing a board has; teaching this class about them would be the
    /// shared module knowing which scene opened it, which is the one rule every
    /// root here keeps.
    ///
    /// Null for a cell the scene has nothing to say about, so the field's own
    /// marks still answer underneath.
    /// </summary>
    public Func<Vector2I, Color?>? Ink;

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
        if (Ink?.Invoke(cell) is Color painted)
            return painted;
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
    /// How far a flat mark is cleared of a tilted face, as a fraction of a level.
    ///
    /// <b>It is here because a mark and the tank that made it cannot both be
    /// right while the tank is drawn on a chord.</b> A mark is laid at the
    /// tank's own contact patch - it has to be, or it comes out from under
    /// somewhere other than the tracks - so its screen row carries the chord's
    /// error, while its height is the ground's. Reconstructing one from the other
    /// then lands a quarter level away along the face, and the face is rising, so
    /// the surface at the mark's own place is higher than the mark: hidden again,
    /// by a fraction of the first amount.
    ///
    /// So it is cleared instead of chased, which is a deliberate approximation:
    /// on tilted ground the mark is drawn up to this much nearer than the ground
    /// it lies on. It costs no screen pixels - a lift and a slide along the view
    /// cancel, which is the whole of why <see cref="Stage3D.Clear"/> works - and
    /// it buys depth only.
    ///
    /// <b>A quarter level, and both bounds are measured.</b> The residual is at
    /// most <c>Lift/4</c> of chord error times the face's own screen gradient
    /// <c>(Lift/Reach)/sin(e)</c> - 9.6px on this board - and the worst seen on a
    /// descent off the crown was 6px, where every last rut pixel came back. A
    /// quarter level is 16.19px: over both, and the same quarter level the chord
    /// is out by in the first place, so it is one number rather than a second.
    /// </summary>
    public const float MarkClear = 0.25f;

    /// <summary>
    /// How high to lay something flat that is crossing from one cell to the next:
    /// the ground under it, cleared where that ground is tilted.
    ///
    /// The ground alone is <see cref="SurfaceBetween"/> and is what a mark is
    /// really on; this is where it has to be drawn to be seen. See
    /// <see cref="MarkClear"/> for why the two differ and what the difference
    /// costs. On a step with no ramp at either end they are the same number, so a
    /// board of flat tops never pays for this.
    /// </summary>
    public float MarkBetween(Vector2I from, Vector2I onto, float done) =>
        SurfaceBetween(from, onto, done) + (HasRamps ? Lift * MarkClear : 0.0f);

    /// <summary>The same height for something standing on one cell rather than
    /// crossing between two. It has to be the same expression: a cell is parked on
    /// at the end of every leg and marks are laid on that frame too, so a park
    /// that skipped the clearance left the bars at every cell boundary to be eaten
    /// by the face - measured, 201px of one descent, in two clusters at one seam
    /// with the ribbon under them intact.</summary>
    public float MarkAt(Vector2I cell) =>
        TopAt(cell) + (HasRamps ? Lift * MarkClear : 0.0f);

    /// <summary>
    /// The clearance taken back off again: the ground a mark is really on, given
    /// the height it is laid at.
    ///
    /// <b>For anything that reads its own height off the ground rather than being
    /// laid at one.</b> The selection ring asks the board how high it is under
    /// each of its own corners - see <see cref="TopAtPoint"/> - so all it wants
    /// from the tank is <i>where</i> the tank is, and a height is how that is
    /// said: a drawn row is the flat row with the lift taken out of it, so the
    /// lift has to go back on before the board can be asked which cell the point
    /// is over. Given the cleared height it would come back a quarter level along
    /// the face, which on a slope is the mark drawn off the tank.
    ///
    /// Named here rather than subtracted at the caller because it is
    /// <see cref="MarkClear"/> read backwards, and two spellings of one constant
    /// drift the first time one of them is tuned.
    /// </summary>
    public float Bare(float cleared) =>
        cleared - (HasRamps ? Lift * MarkClear : 0.0f);

    /// <summary>
    /// Whether driving from one cell to the next goes up or down at all.
    ///
    /// <b>Asked of the leg's two surfaces, not of the tank.</b> The slope under a
    /// tank is a plane and its travel grade is sprung - see
    /// <see cref="ClimbLean.Along"/> - so reading a climb off either would lag the
    /// board, linger past the crest and answer "no" for the first frames of the leg
    /// that is most of the climb. The two ends of the leg are exact and known before
    /// the tank has moved a pixel.
    ///
    /// <b>Height, not level, and that is what makes a ramp count.</b>
    /// <see cref="LevelAt"/> puts a ramp on its low level, so a leg from flat
    /// ground onto a ramp is a level leg and half a lift of climb - the same
    /// distinction <see cref="TopAt"/> carries. Measured: reading the level instead
    /// leaves 18 of this board's 36 graded legs uncapped, which is the first half of
    /// every climb on it.
    ///
    /// Two cells at the same height are not a grade however tilted they are:
    /// driving along a ramp band broadside climbs nothing, and a tank that crawled
    /// across a face it was not climbing would be paying for the picture rather than
    /// for the hill.
    ///
    /// No <see cref="HasRelief"/> guard, and its absence is the point: with no
    /// relief every cell's <see cref="TopAt"/> is nought, so a flat board answers
    /// "no" to every leg by the same arithmetic rather than by a special case. A
    /// guard that cannot fail is not a guard.
    /// </summary>
    public bool IsGrade(Vector2I from, Vector2I onto) =>
        Mathf.Abs(TopAt(onto) - TopAt(from)) > 0.001f;

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
