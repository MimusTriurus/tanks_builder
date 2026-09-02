using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// A board: how wide, how high, what each cell is made of, and where the tanks
/// start.
///
/// <b>A map is data, not a mechanism, and that is why this is a type rather
/// than a bench.</b> <see cref="WoodBench"/>, <see cref="WallBench"/> and
/// <see cref="ReliefBench"/> exist because each isolates a mechanism - burning,
/// collapse, the tilt on a slope - and a scene of its own is the cheapest way to
/// look at one thing. A map isolates nothing: the harness already owns relief,
/// ramps, water, the grove, the terrain art, <see cref="Stage3D"/>, the panel
/// and the self test. A second copy of board authoring beside it would be two
/// places that agree until somebody edits one.
///
/// So a scene wanting a different board is <see cref="Main"/> with a different
/// map, and everything that reads a board keeps reading it.
///
/// <b>The field still takes four separate arrays, and that has not changed.</b>
/// How high a cell stands, whether its top is flat, whether it is flooded and
/// what it is made of are four different questions about one cell - see
/// <see cref="HexField.SetWater"/>, which says exactly that about water. What
/// this type settles is how a map is <i>written</i>, and the two maps here are
/// written differently on purpose:
///
/// <list type="bullet">
/// <item><see cref="Bench"/> keeps its three grids, because its relief, its
/// ramps and its water are switched independently at runtime and each grid is
/// the thing a flag turns on. Byte for byte the board it always was.</item>
/// <item><see cref="Abbey"/> is one letter grid plus a ramp grid. At three
/// hundred and forty cells what fails is not the API, it is five grids that have
/// to agree cell by cell - so a cell is described once, in a picture of the map
/// that can be read as one, and the arrays are derived. The ramps stay a second
/// grid because whether a cell's top is flat really is a second question about
/// it, and because ramp placement is hand-tuned against the guard in
/// <see cref="HexField.SetRelief"/>.</item>
/// </list>
///
/// <b>Ragged rows throw rather than clip.</b> The decoders in <see cref="Main"/>
/// clip, and they are right to: they stand in for a board somebody might widen,
/// and coming up empty would read as the flag not working. An authored map is
/// the opposite case - a nineteen-character row on a twenty-wide board is a
/// typo, and clipped it becomes a column of plain that nobody drew.
/// </summary>
public sealed partial class BoardMap
{
    // --- what the letters mean -----------------------------------------------

    /// <summary>Open ground, at the datum.</summary>
    public const char Plain = '.';

    /// <summary>Trees on open ground. Not a plate and never will be - see
    /// <see cref="HexField.KindAt"/>; the cell is soil with a grove standing on
    /// it.</summary>
    public const char Wood = 'f';

    /// <summary>A rise, one level up, with a ramp somewhere on to it.</summary>
    public const char Hill = '1';

    /// <summary>The same, wooded. Level and kind are independent axes, so this
    /// costs nothing to have.</summary>
    public const char WoodedHill = 'F';

    /// <summary>Low ground, one level down and dry.</summary>
    public const char Low = 'v';

    public const char WoodedLow = 'V';

    /// <summary>Low ground with water standing in it.</summary>
    public const char Wet = 'w';

    /// <summary>
    /// A rise nothing can climb - two levels up, never ramped.
    ///
    /// <b>Five types were asked for and there are six, and the sixth is
    /// forced.</b> With ramps on the board <see cref="HexField.Passable"/>
    /// reduces to <c>LevelAt(next) == LevelAt(cell)</c> - every change of level
    /// goes through a ramp - so a rise with no ramp on it already is an
    /// unclimbable rock, which is what the black masses on a World of Tanks
    /// minimap are. But then a rise the author meant to be unreachable and a
    /// valley accidentally cut off are the same picture, and the one check that
    /// catches a broken map - every cell reachable from every spawn - has no way
    /// to tell them apart. So the rock is declared rather than derived.
    /// </summary>
    public const char Rock = '#';

    /// <summary>
    /// Open ground with a brick wall standing on it.
    ///
    /// <b>Data rather than a literal in a scene, and the slot for it already
    /// existed.</b> The precedent is <see cref="Wood"/>: the map says trees grow
    /// here and <see cref="Grove"/> is what sows them, so the letter describes
    /// the cell and a mechanism reads it. The same split here - the cell is level
    /// ground, and <see cref="WallProp"/> is what stands a wall on it.
    ///
    /// An exported string on the scene was the alternative and is worse in the
    /// way this file keeps naming: it drifts away from the map without a word,
    /// and the self test that judges every board whether or not a scene opened it
    /// can say nothing at all about it.
    /// </summary>
    public const char Wall = 'W';

    /// <summary>Sand: a floor a tank crosses slowly. The first letter that is a
    /// foundation and nothing else - no height, no flood, no props - which is
    /// what makes it the cheapest proof that the floor is its own slot.</summary>
    public const char Sand = 's';

    /// <summary>A trench: cover with no art and no props behind it, which is
    /// exactly why it is here. A cover that is only rules is the test that this
    /// layer is rules rather than a list of things that get drawn.</summary>
    public const char Trench = 't';

    /// <summary>A minefield. Cover, hidden, and nothing on the board says so -
    /// see <see cref="CoverRule.Hidden"/>, which is carried and read by nothing
    /// yet.</summary>
    public const char Mine = 'm';

    /// <summary>Not on the board at all - see <see cref="HexField.Plot"/>. The
    /// hatched border of a World of Tanks minimap, and drawn as nothing rather
    /// than as a wall: it is the edge of the world, not a feature of it.
    /// </summary>
    public const char Off = '-';

    // --- the map -------------------------------------------------------------

    public string Name { get; }
    public int Columns { get; }
    public int Rows { get; }

    /// <summary>Where the tanks park, one per class. The first is the one a
    /// single-tank run gets.
    ///
    /// <b>Cells only, and <see cref="Parked"/> is the whole statement.</b> Every
    /// reader that wants a place wants this; the class a home asks for is a
    /// second field on a second reader, and a parallel array of classes beside
    /// this one is the arrangement this project keeps refusing.</summary>
    public IReadOnlyList<Vector2I> Homes { get; }

    /// <summary>
    /// The parkings a board declares: a cell each, and optionally which class
    /// the board wants standing on it.
    ///
    /// <b>A preference and not a roster.</b> Which tanks exist is still the
    /// harness's - one per atlas that loaded - so a class named here says where
    /// that one goes if it is on the board, and a home that names nobody takes
    /// whoever is left. See <c>Main.Park</c>, which is the one place the two
    /// meet.
    /// </summary>
    public IReadOnlyList<Parking> Parked { get; }

    /// <summary>Height per cell, for <see cref="HexField.SetRelief"/>.</summary>
    public int[] Levels { get; }

    /// <summary>Which cells carry a ramp, for the same call. Never true on a
    /// rock: that is the one rise not meant to be driven.</summary>
    public bool[] Ramps { get; }

    /// <summary>Which cells are flooded, for <see cref="HexField.SetWater"/>.
    /// </summary>
    public bool[] Water { get; }

    /// <summary>Which cells carry a wall. Not handed to the field - nothing
    /// there needs it, because a wall is a prop standing on the ground rather
    /// than a property of it - and read by whoever builds the props, the way
    /// <see cref="Kinds"/> is read by the grove.</summary>
    public bool[] Walls { get; }

    /// <summary>
    /// What the walled cells carry, by cell - the edges the masonry stands on
    /// and the four numbers that are its shape.
    ///
    /// <b>Only the cells that said something.</b> A walled cell with no entry is
    /// a closed ring on the compiled figures, which is what a bare wall letter
    /// has always meant - see <see cref="HexField.SetCover"/>. So a board that
    /// never authored a wall's shape is a board with an empty table here, and
    /// nothing downstream has to tell that from a board that authored a ring.
    /// Ask <see cref="MasonryAt"/>, which answers for both.
    /// </summary>
    public IReadOnlyDictionary<Vector2I, Masonry> Walling { get; }

    /// <summary>What stands on a walled cell: what the board authored, or a
    /// closed ring on the compiled figures. Null on a cell with no wall.
    /// </summary>
    public Masonry? MasonryAt(Vector2I cell) =>
        !IsWall(cell) ? null
        : Walling.TryGetValue(cell, out Masonry? said) ? said : new Masonry();

    /// <summary>What each cell is made of, or null to leave it to the mix. See
    /// <see cref="HexField.SetKinds"/>.</summary>
    public string?[] Kinds { get; }

    /// <summary>Which rises are declared unreachable. Not handed to the field -
    /// nothing there needs it, because the ramps already say what is climbable -
    /// and kept so the reachability audit can tell a decision from a mistake.
    /// </summary>
    public bool[] Cliffs { get; }

    /// <summary>The cells that are on the board, or null for the whole
    /// rectangle. See <see cref="HexField.Plot"/>.</summary>
    public IReadOnlySet<Vector2I>? Plot { get; }

    /// <summary>
    /// The paint the board comes up as.
    ///
    /// <b>A board that authored its kinds has to come up on the mix, and this
    /// exists because it did not.</b> <see cref="HexField.KindAt"/> puts a named
    /// plate above the authored map on purpose - the dropdown is an A/B tool and
    /// <c>--terrain plain</c> has to mean one plate everywhere - but the built-in
    /// default <i>is</i> a named plate (<see cref="TerrainSet.Default"/>), so the
    /// map's own kinds sat underneath it and the woods never reached the screen.
    /// Measured rather than reasoned about: 992 props sown with not one tree
    /// among them, against 2034 with <c>--terrain mixed</c>.
    ///
    /// <see cref="TerrainSet.Mixed"/> is not a pun here. It means every cell
    /// picks its own, and on an authored board what the cell picks is what the
    /// map wrote.
    /// </summary>
    public string Paint { get; }

    /// <summary>
    /// Whether the board is meaningless without height.
    ///
    /// <b>The bench answers no, and its reason does not carry to a map.</b>
    /// There relief is behind a flag because everything the bench measures is a
    /// pixel difference between two captures and a hill would be in both halves
    /// of every one of them. A map whose whole content is a rise, a lake and a
    /// river, opened flat, is a board of woods and nothing else - which reads as
    /// the map not working.
    ///
    /// It moves the defaults only. The panel still turns relief, ramps, water
    /// and the 3D stage off one at a time, which is where the A/B lives.
    /// </summary>
    public bool Height { get; }

    /// <summary>
    /// How many levels the whole board stands above the datum.
    ///
    /// <b>Kept rather than applied and forgotten, because the letters are
    /// written relative to it.</b> A cell drawn as plain on a board with a
    /// plinth of one stands at level 1, so re-encoding its height would write it
    /// out as a hill - see <see cref="MapFile.Ground"/>, whose round trip is what
    /// found this. Authoring provenance, and it is the only kind this type keeps.
    /// </summary>
    public int Plinth { get; }

    /// <summary>
    /// What each cell's floor is made of - the first of the two slots a hex has.
    ///
    /// <b>Water is not in here</b>, for the reason <see cref="HexField"/> gives
    /// over its own ground grid: the flood is authored separately because it has
    /// a guard and an ordering that a material does not, and
    /// <see cref="HexField.FoundationAt"/> reads a flooded cell as shallow so
    /// that every reader still sees one slot.
    ///
    /// Neither is the board's edge: <see cref="Plot"/> already says which cells
    /// are on it, and a second copy of that is two lists that agree until
    /// somebody edits a map.
    /// </summary>
    public Foundation[] Ground { get; }

    /// <summary>What stands on each cell - the second slot. One cover per cell,
    /// exclusive; see <see cref="TankSpriteTest.Cover"/> for why that is a
    /// decision rather than a limit of the storage.</summary>
    public Cover[] Over { get; }

    private BoardMap(string name, int columns, int rows,
                     IReadOnlyList<Parking> homes,
                     int[] levels, bool[] ramps, bool[] water,
                     string?[] kinds, bool[] cliffs, bool[] walls,
                     Foundation[] ground, Cover[] over,
                     IReadOnlySet<Vector2I>? plot,
                     string paint, bool height, int plinth = 0,
                     IReadOnlyDictionary<Vector2I, Masonry>? walling = null)
    {
        Name = name;
        Columns = columns;
        Rows = rows;
        Parked = homes;
        Homes = homes.Select(p => p.Cell).ToList();
        Levels = levels;
        Ramps = ramps;
        Water = water;
        Kinds = kinds;
        Cliffs = cliffs;
        Walls = walls;
        Walling = walling ?? new Dictionary<Vector2I, Masonry>();
        Ground = ground;
        Over = over;
        Plot = plot;
        Paint = paint;
        Height = height;
        Plinth = plinth;
    }

    /// <summary>Every cell carrying a wall, in reading order. A list rather than
    /// the array, because what a caller wants is to build one prop per wall and
    /// not to walk the board asking.</summary>
    public IReadOnlyList<Vector2I> Walled()
    {
        var found = new List<Vector2I>();
        for (int r = 0; r < Rows; r++)
        for (int q = 0; q < Columns; q++)
            if (OnBoard(new Vector2I(q, r)) && Walls[At(new Vector2I(q, r))])
                found.Add(new Vector2I(q, r));
        return found;
    }

    public int At(Vector2I cell) => cell.Y * Columns + cell.X;

    /// <summary>
    /// This map's heights laid onto a field of another size: every cell the map
    /// has, and the datum everywhere it does not reach.
    ///
    /// <b>Three of these rather than one map handed over whole, because the two
    /// readers ask about a field rather than about the map.</b> The self test
    /// builds fields of whatever size the assertion needs and
    /// <see cref="Relief3D"/> has its own columns and rows, so what they want is
    /// the bench's hill projected onto that - which is what the three decoders
    /// that used to sit on <see cref="Main"/> did, character by character, off
    /// the grids. Off the decoded map instead: one decode, so a cell cannot mean
    /// one thing to the board and another to the check reading the same grid.
    ///
    /// Clipped rather than refused when the sizes disagree - <see cref="Read"/>
    /// throws for a map, because a ragged map is an authoring mistake, but a
    /// field wider than the board it borrows a hill from is the ordinary case
    /// here.
    /// </summary>
    public int[] LevelsFor(int columns, int rows) => Onto(Levels, columns, rows);

    /// <summary>Which cells carry a ramp, on a field of another size. Separate
    /// from <see cref="LevelsFor"/> so a board can be given height without them -
    /// which is what every relief assertion written before ramps existed runs on,
    /// and what <see cref="HexField.Passable"/> falls back to.</summary>
    public bool[] RampsFor(int columns, int rows) => Onto(Ramps, columns, rows);

    /// <summary>Which cells are flooded, on a field of another size.</summary>
    public bool[] WaterFor(int columns, int rows) => Onto(Water, columns, rows);

    /// <summary>This map's floors on a field of another size. Solid off the end
    /// of the map, which is what <see cref="Foundation.Solid"/> being the value
    /// a board that said nothing carries already means.</summary>
    public Foundation[] GroundFor(int columns, int rows) =>
        Onto(Ground, columns, rows);

    /// <summary>What stands on each cell, on a field of another size.</summary>
    public Cover[] OverFor(int columns, int rows) => Onto(Over, columns, rows);

    private T[] Onto<T>(T[] cells, int columns, int rows)
    {
        var into = new T[columns * rows];
        for (int r = 0; r < rows && r < Rows; r++)
        for (int q = 0; q < columns && q < Columns; q++)
            into[r * columns + q] = cells[r * Columns + q];
        return into;
    }


    public bool OnBoard(Vector2I cell) =>
        cell.X >= 0 && cell.X < Columns && cell.Y >= 0 && cell.Y < Rows
        && (Plot is null || Plot.Contains(cell));

    public bool IsCliff(Vector2I cell) => OnBoard(cell) && Cliffs[At(cell)];

    public bool IsWall(Vector2I cell) => OnBoard(cell) && Walls[At(cell)];

    /// <summary>
    /// The first cell of the asked-for wetness with nothing but water around it
    /// - open water when asked wet, an island when asked dry - or null if the
    /// board has no such place.
    ///
    /// <b>One predicate serves both because both are defined by what surrounds
    /// them rather than by where they are:</b> open water is a wet cell no shore
    /// touches, and an island is a dry one.
    ///
    /// <b>An off-board neighbour counts as water, and that is deliberate rather
    /// than sloppy.</b> Open water at the board's edge is still open water - on
    /// <see cref="Test"/> every wet cell with no shore on it is at the edge, so
    /// the strict reading answers nothing at all. Which makes this "no shore
    /// touches it" and not "surrounded"; an island wants the stronger thing, and
    /// the check on it asserts that separately.
    ///
    /// <b>It lives on the map because the map is what knows the answer.</b>
    /// <see cref="TankBench"/> drives to both from its panel, and a literal
    /// there names the island on <see cref="Test"/> and names nothing on
    /// <c>--map bench</c> - a button that drives to nothing in particular reads
    /// as the pathing being broken. Here it is also askable of a board no scene
    /// has opened, which is where the check on it lives.
    /// </summary>
    public Vector2I? Ringed(bool wet)
    {
        for (int r = 0; r < Rows; r++)
        for (int q = 0; q < Columns; q++)
        {
            var cell = new Vector2I(q, r);
            if (!OnBoard(cell) || Water[At(cell)] != wet)
                continue;
            bool ringed = true;
            foreach (int heading in HexField.EdgeHeadings)
            {
                Vector2I next = HexField.Step(cell, heading);
                if (OnBoard(next) && !Water[At(next)])
                    ringed = false;
            }
            if (ringed)
                return cell;
        }
        return null;
    }

    /// <summary>The cell standing at the board's top level, first one found -
    /// where <see cref="TankBench"/>'s "to the rise" goes. Measured for
    /// <see cref="Ringed"/>'s reason.</summary>
    public Vector2I? Crown()
    {
        Vector2I? best = null;
        for (int r = 0; r < Rows; r++)
        for (int q = 0; q < Columns; q++)
        {
            var cell = new Vector2I(q, r);
            if (OnBoard(cell)
                && (best is null || Levels[At(cell)] > Levels[At(best.Value)]))
                best = cell;
        }
        return best;
    }

    /// <summary>How many cells of each letter, for the audit and the trace.
    /// </summary>
    public IReadOnlyList<(string What, int Count)> Tally()
    {
        int plain = 0, wood = 0, hill = 0, low = 0, wet = 0, rock = 0, off = 0;
        for (int r = 0; r < Rows; r++)
        for (int q = 0; q < Columns; q++)
        {
            var cell = new Vector2I(q, r);
            if (!OnBoard(cell))
            {
                off++;
                continue;
            }
            int at = At(cell);
            if (Cliffs[at]) rock++;
            else if (Water[at]) wet++;
            else if (Levels[at] > 0) hill++;
            else if (Levels[at] < 0) low++;
            else plain++;
            if (Kinds[at] == TerrainSet.Forest) wood++;
        }
        int walled = 0;
        foreach (bool w in Walls)
            if (w)
                walled++;
        return new[]
        {
            ("plain", plain), ("hill", hill), ("low", low), ("water", wet),
            ("rock", rock), ("wooded", wood), ("walled", walled),
            ("off board", off),
        };
    }


    /// <summary>
    /// The dry cells a water surface would swallow if it were let find its
    /// level, in reading order. Empty on a map that obeys sea level.
    ///
    /// <b>Computed and reported, never applied.</b>
    /// <see cref="HexField.SetWater"/> refuses this rather than filling it, and
    /// its docstring carries the reason; what this exists for is to make the fix
    /// mechanical. The guard throws naming the offending pairs, which is where
    /// the water gets in - but the water then keeps going, so the list of cells
    /// to redraw is longer than the list of cells that are wrong. Same shape as
    /// the ramps: the audit enumerates and the map picks off the list.
    ///
    /// <b>A ramp stops it, and that is the whole of the exemption.</b> It has no
    /// one surface height - which is the first thing
    /// <see cref="HexField.SetWater"/> refuses water on - so it holds nothing and
    /// dams instead, and that is what a shore driven down into a lake is.
    /// Evidence the exemption was not carved to fit the map it was written for:
    /// the bench board, authored long before any of this, passes with exactly one
    /// exception and that exception is its ford at (7,4).
    /// </summary>
    public IReadOnlyList<Vector2I> Flood() => MapRules.Flood(MapRules.Draft.Of(this));

    /// <summary>The same on loose arrays, so the rule can be asserted against a
    /// board built for it. Both shipped maps obey it, and a guard nothing ever
    /// trips is a guard whose state nobody knows.</summary>
    public static IReadOnlyList<Vector2I> Flood(
        int columns, int rows, int[] levels, bool[] ramps, bool[] water,
        IReadOnlySet<Vector2I>? plot) =>
        MapRules.Flood(new MapRules.Draft
        {
            Columns = columns, Rows = rows, Levels = levels, Ramps = ramps,
            Water = water, Plot = plot,
        });

    /// <summary>
    /// The map, once nothing is left to add to it, or a throw naming what is
    /// wrong with it.
    ///
    /// <b>Sea level is checked here and not in
    /// <see cref="HexField.SetWater"/>, and the reason is that the field is
    /// handed boards that are not maps on purpose.</b> Its two guards survive
    /// that - a ramp is still a ramp and one body is still one surface on a
    /// board with the heights taken off - but this one cannot: flatten a board
    /// and every pond on it is instantly water beside dry ground at its own
    /// level. And flattening is exactly what the self test does to ask
    /// <c>Stage3D.Shoreline</c> about a made-up mask, right down to flooding the
    /// whole board and punching one dry cell out of it. That cell is the
    /// question being asked, and it is unanswerable on any legal board.
    ///
    /// So the rule belongs where a board is <i>declared</i>, beside the ragged
    /// row and the ramp on a rock: satisfiable only by a whole map, and asked
    /// only of one.
    /// </summary>
    private static BoardMap Sealed(BoardMap map)
    {
        IReadOnlyList<Vector2I> flood = map.Flood();
        if (flood.Count > 0)
            throw new InvalidOperationException(
                map.Name + ": water finds its level, and these cells stand at a "
                + "surface's own height beside it, so they are under it: "
                + string.Join(" ", flood.Select(c => $"({c.X},{c.Y})")));
        return map;
    }

    // --- authoring -----------------------------------------------------------

    /// <summary>Reads a char grid, refusing a ragged one. See the type's note on
    /// why this throws where <see cref="LevelsFor"/> clips.</summary>
    private static char[,] Read(string what, string[] grid, int columns, int rows)
    {
        if (grid.Length != rows)
            throw new InvalidOperationException(
                $"{what} has {grid.Length} rows and the board has {rows}");
        var cells = new char[columns, rows];
        for (int r = 0; r < rows; r++)
        {
            if (grid[r].Length != columns)
                throw new InvalidOperationException(
                    $"{what} row {r} is {grid[r].Length} characters and the "
                    + $"board is {columns} wide: {grid[r]}");
            for (int q = 0; q < columns; q++)
                cells[q, r] = grid[r][q];
        }
        return cells;
    }

    /// <summary>A map written as one letter per cell plus a ramp grid.</summary>
    /// <param name="plinth">How many levels the whole board stands above the
    /// datum. <b>Not a per-cell height and never a thickness of somebody's
    /// choosing:</b> <see cref="Stage3D"/> extrudes a prism from a cell's top
    /// face down to the board's floor and skips the walls when the two are the
    /// same height, so a board flat on the datum is hexagons of ground and
    /// nothing else. The floor is always the datum whatever the map says
    /// (<c>HexField.LevelRange</c> starts at zero), so one level up is all it
    /// takes - which is exactly what <see cref="WallBench"/> does to its rosette,
    /// and for the same reason. Uniform, so every relative height in the grid is
    /// untouched and <see cref="Passable"/> reads the same board.</param>
    internal static BoardMap FromGround(string name, string[] ground,
                                        string[] ramps,
                                        IReadOnlyList<Parking> homes,
                                        string paint, bool height,
                                        int plinth = 0,
                                        IReadOnlyDictionary<Vector2I, Masonry>?
                                            walling = null)
    {
        MustCrew(name, homes);
        int rows = ground.Length;
        int columns = rows == 0 ? 0 : ground[0].Length;
        char[,] g = Read(name + " ground", ground, columns, rows);
        char[,] rr = Read(name + " ramps", ramps, columns, rows);

        var levels = new int[columns * rows];
        var water = new bool[columns * rows];
        var kinds = new string?[columns * rows];
        var cliffs = new bool[columns * rows];
        var walls = new bool[columns * rows];
        var floor = new Foundation[columns * rows];
        var over = new Cover[columns * rows];
        var ramped = new bool[columns * rows];
        var plot = new HashSet<Vector2I>();
        var wrong = new List<string>();

        for (int r = 0; r < rows; r++)
        for (int q = 0; q < columns; q++)
        {
            int at = r * columns + q;
            var cell = new Vector2I(q, r);
            char c = g[q, r];
            bool on = c != Off;
            if (on)
                plot.Add(cell);
            switch (c)
            {
                case Off:
                case Plain:
                    break;
                case Wood:
                    kinds[at] = TerrainSet.Forest;
                    over[at] = Cover.Forest;
                    break;
                case Sand:
                    floor[at] = Foundation.Sand;
                    break;
                case Trench:
                    over[at] = Cover.Trench;
                    break;
                case Mine:
                    over[at] = Cover.Minefield;
                    break;
                case Wall:
                    // Level ground under it, and that is not a simplification:
                    // with ramps on the board Passable reduces to "the same
                    // level", so a wall's cell raised above its neighbours could
                    // not be driven on to at all and the ram would be
                    // impossible. What gives the prisms their sides is the
                    // plinth, which lifts the whole board at once.
                    walls[at] = true;
                    over[at] = Cover.Walls;
                    break;
                case Hill:
                    levels[at] = 1;
                    kinds[at] = TerrainSet.Rise;
                    break;
                case WoodedHill:
                    levels[at] = 1;
                    kinds[at] = TerrainSet.Forest;
                    over[at] = Cover.Forest;
                    break;
                case Low:
                    levels[at] = -1;
                    break;
                case WoodedLow:
                    levels[at] = -1;
                    kinds[at] = TerrainSet.Forest;
                    over[at] = Cover.Forest;
                    break;
                case Wet:
                    levels[at] = -1;
                    water[at] = true;
                    break;
                case Rock:
                    // Two levels up, not one, and that is what makes the letter
                    // mean anything. At +1 a rock beside a climbable hill is the
                    // same level as it, so Passable lets a tank drive straight
                    // on to it - and with the same plate under both, a rock was
                    // indistinguishable from more hilltop. At +2 nothing reaches
                    // it from the hill either, because that step wants a ramp
                    // too and a rock never carries one. It also survives
                    // --no-ramps, where MaxClimb is 1.
                    levels[at] = 2;
                    kinds[at] = TerrainSet.Rise;
                    cliffs[at] = true;
                    floor[at] = Foundation.Rock;
                    break;
                default:
                    wrong.Add($"({q},{r}) is an unknown kind {c}");
                    break;
            }

            if (rr[q, r] != 'r')
                continue;
            // A ramp off the board, or a ramp on a rock, is the map disagreeing
            // with itself - and both read as a ramp that simply does not work
            // rather than as a ramp that was refused.
            if (!on)
                wrong.Add($"the ramp at ({q},{r}) is off the board");
            else if (c == Rock)
                wrong.Add($"the ramp at ({q},{r}) is on a rock, which is the "
                          + "one rise that is not meant to be driven");
            else
                ramped[at] = true;
        }

        // Masonry on a cell carrying no wall is the map disagreeing with
        // itself, and it reads as a wall shape that quietly did nothing - the
        // failure WallConfig.Strays exists to catch one folder over.
        foreach (Vector2I cell in walling?.Keys ?? Enumerable.Empty<Vector2I>())
            if (cell.X < 0 || cell.X >= columns || cell.Y < 0 || cell.Y >= rows
                || !walls[cell.Y * columns + cell.X])
                wrong.Add($"the masonry at ({cell.X},{cell.Y}) is on a cell "
                          + "carrying no wall");

        if (wrong.Count > 0)
            throw new InvalidOperationException(
                name + ": " + string.Join("; ", wrong));

        if (plinth != 0)
            for (int at = 0; at < levels.Length; at++)
                levels[at] += plinth;

        return Sealed(new BoardMap(name, columns, rows, homes, levels, ramped,
                                   water, kinds, cliffs, walls, floor, over,
                                   plot, paint, height, plinth, walling));
    }

    /// <summary>
    /// Every class a board's parkings ask for is a class this build has, and no
    /// two ask for the same one.
    ///
    /// <b>Both halves are a statement the board could not honour.</b> A tag
    /// nothing answers to is a home that will silently take whoever is left -
    /// <see cref="WallConfig.Strays"/>'s failure, one folder over - and two
    /// homes asking for the same class is two parkings for one tank, which
    /// leaves the second holding nobody however the pairing is written.
    /// </summary>
    private static void MustCrew(string name, IReadOnlyList<Parking> homes)
    {
        var asked = new List<string>();
        foreach (Parking home in homes)
        {
            if (home.Class is not { Length: > 0 } tag)
                continue;
            if (!MovementProfile.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"{name}: the home at ({home.Cell.X},{home.Cell.Y}) asks for "
                    + $"class {tag}, and this build has "
                    + string.Join(", ", MovementProfile.Tags));
            if (asked.Contains(tag, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"{name}: two homes ask for class {tag}, and there is one "
                    + "tank of each - so the second would stand empty whatever "
                    + "the pairing does");
            asked.Add(tag);
        }
    }

    /// <summary>A map written as separate relief, ramp and water grids - the
    /// harness board, unchanged.</summary>
    private static BoardMap FromLayers(string name, int columns, int rows,
                                       string[] relief, string[] ramps,
                                       string[] water,
                                       IReadOnlyList<Parking> homes,
                                       string paint, bool height)
    {
        MustCrew(name, homes);
        char[,] g = Read(name + " relief", relief, columns, rows);
        char[,] rr = Read(name + " ramps", ramps, columns, rows);
        char[,] ww = Read(name + " water", water, columns, rows);
        var levels = new int[columns * rows];
        var ramped = new bool[columns * rows];
        var wet = new bool[columns * rows];
        for (int r = 0; r < rows; r++)
        for (int q = 0; q < columns; q++)
        {
            int at = r * columns + q;
            levels[at] = g[q, r] switch
            {
                '1' => 1, '2' => 2, 'v' => -1, _ => 0,
            };
            ramped[at] = rr[q, r] == 'r';
            wet[at] = ww[q, r] == 'w';
        }
        // Solid everywhere with nothing standing on it: a board written as
        // three grids says nothing about either slot, and that is what a board
        // which said nothing is made of. Its flood is the wet grid, as it was.
        return Sealed(new BoardMap(name, columns, rows, homes, levels, ramped,
                                   wet, new string?[columns * rows],
                                   new bool[columns * rows],
                                   new bool[columns * rows],
                                   new Foundation[columns * rows],
                                   new Cover[columns * rows], null, paint,
                                   height));
    }

    // --- the maps ------------------------------------------------------------

    private static BoardMap? _bench;
    private static BoardMap? _abbey;

    /// <summary>
    /// The one board with height on it, until a map format exists.
    ///
    /// Written as rows of characters for the reason the relief bench's is: the
    /// shape of the hill has to be legible in the source, and an int array of
    /// fifty-four entries is not.
    ///
    /// <b>The hill is in front of the tanks, not behind them, and that is the
    /// whole of why it is where it is.</b> Ground only hides what is behind it,
    /// so a hill up at the top of the board is a hill nothing is ever occluded
    /// by - it would show the lift and the walls and say nothing at all about the
    /// one rule this slice is for. It sits one row in front of the home line,
    /// where standing still is already the test.
    ///
    /// The three home cells stay level - all on row 2 - so the bench still opens
    /// with the tanks in a line at one height, which is the comparison every
    /// other thing here is judged by. The ditch is off to one side for the other
    /// direction: a cell below the datum is drawn <i>down</i>, and its far
    /// neighbours own the walls.
    ///
    /// Clipped to the board rather than refused when the two disagree: this is a
    /// stand-in, and a board that came up empty because somebody widened the
    /// field would read as the flag not working.
    /// </summary>
    /// <summary>
    /// The board's height, one character per cell.
    ///
    /// <b>A ramp is a cell of the lower of the two levels it joins</b>, marked
    /// '/' - its own level is read off the neighbours exactly as any other cell's
    /// is, and which way it tilts is derived from them by
    /// <see cref="HexField.SetRelief"/>. So a ramp up onto a plateau is a '/' on
    /// the flat beside it, and a ramp down into a pit is a '/' in the pit at its
    /// mouth.
    ///
    /// <b>Every level change on this board goes through exactly one ramp, and
    /// that is the map being a map.</b> The plateaus are reached up column 3 and
    /// column 5; the pit is entered at (7,4) and opens out inside. Drive at a
    /// cliff anywhere else and the route refuses, which is what
    /// <see cref="HexField.Passable"/> is for.
    ///
    /// <b>The pit had to be widened to six cells to hold its ramp.</b> A ramp
    /// wants exactly one neighbour a level above it, and at the old four-cell
    /// pit's mouth (8,4) there were three - so the map did not decide which edge
    /// was the high one and the derivation refused it, correctly. Surrounding
    /// (7,4) with pit on its other five edges is what leaves one.
    ///
    /// <b>Every ramp rises away from the camera, and that is measured rather than
    /// tidy.</b> A slope's face projects by <c>sin(e) -/+ grade*cos(e)</c> of its
    /// own footprint, so one rising toward the camera is <i>compressed</i> - the
    /// first draft put the plateau ramps on row 2, where a 100px cell came out
    /// 57px tall, read as the plateau simply being one cell bigger, and pushed the
    /// ground art far enough down its mipmaps to come back as a smooth pale band.
    /// Rising away it is stretched instead, which is what a slope looks like.
    ///
    /// <b>Both of a ramp's axis neighbours have to be on the board.</b> The
    /// obvious fix - ramps on row 5, the near edge of the plateau - makes them
    /// unreachable: the low edge points off the board, and a ramp is entered
    /// along its axis only, so the only way in was from the plateau it exists to
    /// reach. The plateaus moved up a row instead.
    ///
    /// <b>The right-hand five columns are the rosette: one hill cell with a ramp
    /// on every one of its six flat faces.</b> The rest of this board exercises
    /// one ramp heading - 90, away from the camera - because that is the one that
    /// looks right, and a slope is drawn from a corner pattern rotated to its high
    /// edge, so five of the six rotations were never rendered at all. Here they
    /// all are, and the hill is reachable six ways.
    ///
    /// <b>Two of them rise toward the camera and read badly, and that is the
    /// point rather than an oversight.</b> A face rising at the viewer projects to
    /// <c>sin(e) - grade*cos(e)</c> of its footprint against <c>sin(e) +
    /// grade*cos(e)</c> going away, so the near pair is compressed to about half
    /// the far pair's height. The lesson is written on the plateau ramps above,
    /// which is where the map obeys it; this patch is where it can be seen.
    ///
    /// <b>The ring cells are all adjacent to each other and none of them may be
    /// driven to another.</b> Two adjacent ramps are refused by
    /// <see cref="HexField.Passable"/>, so the way from one face to the next is
    /// out to the flat, round, and back in - which is what makes each of the six
    /// its own test rather than one lap of the hill.
    /// </summary>
    private static readonly string[] BenchRelief =
    {
        "..............",
        "..............",
        "...1.1........",
        "...1.1.....1..",
        "......vvv.....",
        "......vvv.....",
    };

    /// <summary>Where the ramps are. Kept beside <see cref="BenchRelief"/> rather than
    /// mixed into it because the two answer different questions about a cell -
    /// how high it stands, and whether its top is flat - and a ramp reads its own
    /// level out of the same grid every other cell does.
    ///
    /// The six around column 11 are the rosette's, and they are not placed by eye:
    /// they are <c>Step((11,3), h)</c> for each of the six edge headings, and their
    /// entry cells are one more step of the same heading. Both had to land on the
    /// board, which is what set the width at fourteen columns.</summary>
    private static readonly string[] BenchRamps =
    {
        "..............",
        "..............",
        "...........r..",
        "..........r.r.",
        "...r.r.r..rrr.",
        "..............",
    };

    /// <summary>
    /// Which cells are flooded. A third grid beside the levels and the ramps,
    /// because water is a third statement about a cell and not a shade of either
    /// of the other two - see <see cref="HexField.SetWater"/>.
    ///
    /// <b>It is the pit, and putting it there is most of the point.</b> The pit
    /// already exists, it is already a level down, and it already has exactly one
    /// way in - so the pond is somewhere a tank has to drive down to rather than a
    /// puddle standing on the plain, and the first order that reaches it walks a
    /// ramp and a ford back to back, which is both speed ceilings in one leg.
    ///
    /// <b>The mouth cell (7,4) is dry, and it has to be.</b> It is the pit's ramp,
    /// and a slope has no one surface height for water to stand at - the guard
    /// refuses it rather than wedging the pond, which is the same refusal the ramp
    /// derivation makes about an undecided high edge. What that leaves is a beach:
    /// the tank comes down the slope on dry ground and enters the water at (7,5),
    /// which is where a ford is entered from.
    ///
    /// <b>Painted onto a flat board it is still legal, and that is the A/B.</b>
    /// With no relief every level is nought, so the five cells are one flat pond on
    /// open ground - no ramp under it to refuse and no step between them to be a
    /// waterfall.
    /// </summary>
    private static readonly string[] BenchWater =
    {
        "..............",
        "..............",
        "..............",
        "..............",
        "......www.....",
        "......www.....",
    };



    /// <summary>The row the comparison line stands on. Named because the self-test
    /// asks which tanks are on it, and a literal there would be a second copy of
    /// this one.</summary>
    public const int BenchHomeRow = 2;

    /// <summary>Where the three are parked: one row, every other column. The same
    /// row means the same screen height, and two columns apart - 372px against a
    /// heavy's 212px of hull - means the silhouettes never touch, so what the eye
    /// compares is size rather than spacing.
    ///
    /// <b>The light stands on the rosette's hill instead, and it costs the thing
    /// the line is for.</b> Three abreast at one height is how the class sizes are
    /// judged - see MovementProfile.Size - and with the light on (11,3) that
    /// comparison is down to a pair. It is there because a hill with six approaches
    /// is worth having a tank on: the lean, the standing height and the six ramps
    /// meeting the top are all things a tank on the summit says at a glance and an
    /// empty summit does not. R puts it back there rather than into the line, which
    /// is what makes it a home and not a start.</summary>
    public static readonly Parking[] BenchHomes =
    {
        new Vector2I(11, 3), new Vector2I(4, BenchHomeRow),
        new Vector2I(6, BenchHomeRow),
    };

    /// <summary>The harness board, off the three grids above.
    ///
    /// <b>They used to live on <see cref="Main"/>, and this read them from
    /// there.</b> That put the one thing a map is - its data - inside the scene
    /// that draws it, and left the other three boards here; the self test and
    /// <see cref="Relief3D"/> then reached into the harness for a level array.
    /// They ask this map for one now - see <see cref="LevelsFor"/>.</summary>
    public static BoardMap Bench => _bench ??= FromLayers(
        "bench", 14, 6, BenchRelief, BenchRamps, BenchWater, BenchHomes,
        TerrainSet.Default, false);

    public static BoardMap Abbey => _abbey ??= FromGround(
        "abbey", AbbeyGround, AbbeyRamps, AbbeyHomes, TerrainSet.Mixed, true);
    /// <summary>
    /// The strip <see cref="TankBench"/> stands its tank on: seven columns by
    /// four, with a rise in one corner and a pool holding an island in the
    /// other.
    ///
    /// <b>Small because the subject is one tank, and not smaller because a tank
    /// that cannot be driven cannot be judged.</b> Half of what a vehicle does
    /// here is keyed on ground gone past rather than on the clock - the belts,
    /// the ruts, the jolt, the pitch - so a single hex would have shown the
    /// turret, the gun and the damage and nothing that moves. Three cells of
    /// straight is the least that is not all ramp-up: the medium wants 69px to
    /// reach its own cruise against 107 to the row.
    ///
    /// <b>The rise and the pool are here because they are the two things the
    /// ground does to a tank that flat soil does not.</b> A grade caps the speed
    /// at two thirds and tilts the hull; water caps it at 45%, damps the ride
    /// and cuts the sprite at the waterline. Both are one cell's drive from the
    /// middle, so neither needs looking for.
    ///
    /// <b>The island is why this board is seven by four and not five by three,
    /// and the size is forced twice over.</b> A cell has all six neighbours on
    /// the board only inside <c>1..Columns-2</c> by <c>1..Rows-2</c>, so on five
    /// by three the only candidates were (1,1), (2,1) and (3,1) - and the ring
    /// around any of them takes two of the three homes and cuts the mainland in
    /// two. Then the fourth row: a ramp is entered and left along its own axis,
    /// so <i>both</i> its ends have to be on the board, and on three rows every
    /// cell of the outer two has half its axes off it. The beaches that would
    /// still have fitted had their soles two columns away, which drags the pool
    /// across the strip the board exists for.
    ///
    /// <b>The island stands a level above the water because the alternative is
    /// forbidden, not because it looks better.</b> An island at the pond's own
    /// level is a dry hole in a lake, and <see cref="Flood"/> puts it under -
    /// correctly: a flat dry cell at a surface's own height beside it is beneath
    /// that surface. So the island sits at the datum with the pool a level down,
    /// and getting on to it costs a ramp.
    ///
    /// <b>Reaching it is a lap around it, and that is the axis rule again rather
    /// than the pathing going the long way.</b> A landing is entered from its
    /// sole, and the sole is the cell on the far side from the island, so a tank
    /// always comes at the shore head-on: in at the ford on the north bank, west
    /// past the island, round its front and up from the east.
    /// </summary>
    private static readonly string[] TestGround =
    {
        // 0123456
        "www..11", // r0   the pool's north bank, the ford, and the rise
        "wwww...", // r1
        "w.ww...", // r2   the island at (1,2), water on all six sides of it
        "wwww...", // r3
    };

    /// <summary>The three ways off a level: up to the rise at (5,1), out of the
    /// pool at (2,1), and on to the island at (2,3).
    ///
    /// <b>All three rise along 30, 90 or 150 - away from the camera - so the
    /// audit names none of them compressed</b>, unlike the single beach this
    /// board used to carry. That cost the pool its shape rather than luck: a
    /// ford wants exactly one neighbour a level above it, so it has to sit where
    /// the shore turns, and the only turn that leaves its high edge pointing
    /// away from the camera is the one at (2,1) looking east on to (3,0).
    ///
    /// <b>The two in the water are flooded, and that is the point of them.</b> A
    /// ramp has no single surface height, so the pond's plane crosses it - at
    /// <see cref="HexField.WaterDepth"/> of its run - and the back of each
    /// stands under water while its head stands dry. See
    /// <see cref="HexField.SetWater"/>, whose guard used to refuse exactly
    /// this.</summary>
    private static readonly string[] TestRamps =
    {
        // 0123456
        ".......", // r0
        "..r..r.", // r1   the ford east on to (3,0), the hill's climb north
        ".......", // r2
        "..r....", // r3   the island's landing, north-west on to (1,2)
    };

    /// <summary>Where the tank stands. The middle cell first, because the bench
    /// shows one tank and it should open in the middle of its board with the
    /// rise on one hand and the pool on the other - at (4,2) both are one step
    /// away; the other two are there so that <c>--map test</c> on the harness,
    /// which builds one vehicle per atlas, does not park three of them on one
    /// hex.</summary>
    private static readonly Parking[] TestHomes =
    {
        new Vector2I(4, 2), new Vector2I(4, 3), new Vector2I(5, 3),
    };

    private static BoardMap? _test;

    public static BoardMap Test => _test ??= FromGround(
        "test", TestGround, TestRamps, TestHomes, TerrainSet.Mixed, true);

    /// <summary>
    /// Open water twelve by twelve, with one rosette of land in the middle of
    /// it: a flat centre and six ramps running off it down into the pond.
    ///
    /// <b>Seven land cells and six of them are the shore, which is what makes
    /// this board a different question from <see cref="Test"/>.</b> There the
    /// pool is a feature one cell's drive from a strip of dry ground, and a
    /// tank meets the water on the one beach that fits; here the water is the
    /// board and the land is the feature, so <i>every</i> way off the middle is
    /// a wade. Six of them, one per flat side, so a run into the water can be
    /// taken on any bearing without redrawing anything - and the descent, the
    /// waterline creeping up the hull, the speed ceiling and the wake are all
    /// measured on whichever face the camera reads best rather than on the only
    /// one there is.
    ///
    /// <b>The ring is flooded ramps rather than dry ones, and that is what the
    /// ask means by a descent.</b> A ramp's floor is its own level, so the
    /// pond's surface runs onto it at <see cref="HexField.WaterTop"/> and the
    /// slope cuts that plane: the back <see cref="HexField.WaterDepth"/> of
    /// every one of the six stands under water and the head of it stands dry.
    /// See <see cref="HexField.SetWater"/>, whose second guard used to refuse
    /// exactly this.
    ///
    /// <b>The centre stands a level above the pond because the alternative is
    /// forbidden</b> - <see cref="Flood"/>'s rule, the same one that puts
    /// <see cref="Test"/>'s island on the datum: a flat dry cell at a surface's
    /// own height beside it is beneath that surface. So the pond is a level
    /// down, the centre is at the datum, and each ring cell is a ramp bridging
    /// the one step between them. Each has exactly one neighbour a level above
    /// it - the centre - so the ramp headings are settled off the levels with
    /// nothing left for the map to declare.
    ///
    /// <b>The ring cells cannot be driven to one another, and that is the axis
    /// rule rather than an oversight.</b> Two adjacent ramps are refused by
    /// <see cref="HexField.Passable"/>, and all six of these are adjacent; a
    /// ramp is entered from its sole and left to its head, so getting from one
    /// face to the next is out into the water, round, and back in - or across
    /// the centre. Which makes each of the six its own run rather than one lap
    /// of a hill.
    ///
    /// <b>Three of the six point their high edge at the camera and the audit
    /// says so, and here that cannot be fixed.</b> A high edge along 30, 90 or
    /// 150 is drawn at full height and one along 210, 270 or 330 at about half
    /// - see <see cref="Abbey"/>, where every ramp that had a choice took the
    /// far-facing one. A rosette has no choice: it is all six headings by
    /// definition, so half of it is compressed by construction. That is the
    /// board rather than a defect in it, and the compressed three are worth
    /// having for the same reason the far three are - a wade seen foreshortened
    /// is a wade the game will draw.
    ///
    /// <b>One home, because one cell on this board is a place a tank may
    /// park.</b> The self test wants a home that is dry, flat and not a rock,
    /// and only the centre is all three - the ring is ramp and the rest is
    /// water. So <c>--map water</c> on the harness, which builds one vehicle per
    /// atlas, stands all three of them on that cell: this board belongs to the
    /// one-tank bench, and says so here rather than by looking wrong there.
    ///
    /// Which also collapses three of the bench's four "go" buttons onto one
    /// destination - the rise, the island and the middle are the same hex,
    /// because the only high ground here is an island. True of the board rather
    /// than worth special-casing the panel for.
    /// </summary>
    private static readonly string[] WaterGround =
    {
        // 012345678901
        "wwwwwwwwwwww", // r0
        "wwwwwwwwwwww", // r1
        "wwwwwwwwwwww", // r2
        "wwwwwwwwwwww", // r3
        "wwwwwwwwwwww", // r4
        "wwwwwwwwwwww", // r5   the ring: (5,5) (6,5) (7,5)
        "wwwwww.wwwww", // r6   the centre at (6,6), flanked by (5,6) and (7,6)
        "wwwwwwwwwwww", // r7   and (6,7)
        "wwwwwwwwwwww", // r8
        "wwwwwwwwwwww", // r9
        "wwwwwwwwwwww", // r10
        "wwwwwwwwwwww", // r11
    };

    /// <summary>The six ways off the centre - all of them, which is the board.
    ///
    /// The ring is the six neighbours of (6,6) on a flat-top grid with an even
    /// column, so it is not the square block around it: 90 and 270 stay in
    /// column 6, and the four diagonals land a row <i>up</i> from (6,6) on the
    /// even column and level with it on the odd ones. Hence three marks on r5,
    /// two on r6 and one on r7 - see <see cref="HexField.Step"/>, which is the
    /// only definition of what is next to what.</summary>
    private static readonly string[] WaterRamps =
    {
        // 012345678901
        "............", // r0
        "............", // r1
        "............", // r2
        "............", // r3
        "............", // r4
        ".....rrr....", // r5
        ".....r.r....", // r6
        "......r.....", // r7
        "............", // r8
        "............", // r9
        "............", // r10
        "............", // r11
    };

    /// <summary>The centre, and nothing else can be one - see the ground's
    /// note.</summary>
    private static readonly Parking[] WaterHomes = { new Vector2I(6, 6) };

    private static BoardMap? _water;

    /// <summary>
    /// The rosette-in-a-pond board, named <c>water</c>.
    ///
    /// <b><c>WaterMap</c> rather than <c>Water</c> because the instance array
    /// already owns that name</b> - <see cref="Water"/> is which cells of a
    /// board are flooded, and this is a whole board. The name a scene and a
    /// flag ask for is still <c>"water"</c>.
    ///
    /// <see cref="TerrainSet.Default"/> and not the mix: this board authors no
    /// kinds, and the self test asserts that pairing in both directions. A
    /// board with kinds has to come up on the mix or a named plate hides them;
    /// a board without them names its plate, so that what is drawn is a
    /// decision rather than a hash.
    /// </summary>
    public static BoardMap WaterMap => _water ??= FromGround(
        "water", WaterGround, WaterRamps, WaterHomes, TerrainSet.Default, true);

    /// <summary>
    /// One brick wall in the middle of a hexagon of level ground three cells
    /// across, and nothing else on it.
    ///
    /// <b>Level throughout, and that is forced rather than chosen.</b> With
    /// ramps on the board <see cref="HexField.Passable"/> reduces to "the two
    /// cells stand at the same level", so a wall's cell raised above its
    /// neighbours cannot be driven on to at all - and the whole subject of this
    /// board is a tank driving into a wall. What gives the prisms their sides is
    /// the plinth: the <i>whole</i> board a level up, exactly as
    /// <see cref="WallBench"/> lifts its rosette, so nothing is relatively higher
    /// than anything else and the rim still reads as ground.
    ///
    /// <b>Three cells of approach on every side, not two.</b> Which side the wall
    /// stands on is a dial, so each of the six has to carry both a run-up and a
    /// lane to shoot down. Two cells is the minimum that works at all; the third
    /// is where the tank's braking curve becomes visible, and on a board about a
    /// tank arriving at a wall that is half the subject.
    ///
    /// <b>No kinds, so it names a plate</b> - the pairing the self test asserts
    /// in both directions. The subject is masonry, and hashed ground kinds pull
    /// the eye and bring a wood with them.
    ///
    /// <b>It belongs to the one-tank bench, and says so here rather than by
    /// looking wrong there</b> - <see cref="WaterMap"/>'s note, for a different
    /// reason. The harness builds no props from <see cref="Walls"/>, so
    /// <c>--map wall</c> on it is this hexagon with nothing standing in the
    /// middle. Three homes all the same, because the harness stands one vehicle
    /// per atlas and would otherwise put all three on one hex.
    ///
    /// <b>Five walls: a ring on the middle cell and four samples, one on the end
    /// of each of four lanes out of it.</b> The map says which cells carry one
    /// and nothing else about them - what each is made of is the bench's, the
    /// same split the letter <c>f</c> already has: the map says a wood grows
    /// here and <see cref="Grove"/> decides which trees. See
    /// <c>TankBench.Walls</c>.
    ///
    /// The samples stand at the far end of a lane on purpose. Each is three cells
    /// from the middle, so the drive out of the ring to any of them shows the
    /// whole braking curve, and each faces the ring, so a tank coming out of it
    /// arrives square on the masonry rather than at its end.
    /// </summary>
    private static readonly string[] WallGround =
    {
        // 0123456
        "---W---", // r0   taller
        "-.....-", // r1
        "W.....W", // r2   thicker (0,2), another seed (6,2)
        "...W...", // r3   the ring at (3,3), with the tank inside it
        ".......", // r4
        ".......", // r5
        "--.W.--", // r6   a longer run
    };

    /// <summary>None. Every cell is the same level, so there is no step for a
    /// ramp to bridge - which is the board.</summary>
    private static readonly string[] WallRamps =
    {
        // 0123456
        ".......", // r0
        ".......", // r1
        ".......", // r2
        ".......", // r3
        ".......", // r4
        ".......", // r5
        ".......", // r6
    };

    /// <summary>The middle of the hexagon, and two off to the sides.
    ///
    /// <b>The first is the ring's own cell, so a single-tank run opens with the
    /// tank inside the walls.</b> That is the board's subject: a machine in a
    /// walled yard with a face on every side of it, so the ram has six answers
    /// and none of them needs a run-up first. It used to stand three cells down
    /// the 270 lane, which is the same drive the <c>M</c> key still makes towards
    /// any of the samples.
    ///
    /// The other two are only there so that <c>--map wall</c> on the harness,
    /// which builds one vehicle per atlas, does not stand three of them on one
    /// hex - and they sit two cells out on the 210 and 330 lanes, which carry no
    /// wall.</summary>
    private static readonly Parking[] WallHomes =
    {
        new Vector2I(3, 3), new Vector2I(1, 4), new Vector2I(5, 4),
    };

    private static BoardMap? _wall;

    /// <summary>The one-wall board, named <c>wall</c>. <c>WallMap</c> rather than
    /// <c>Wall</c> because the letter already owns that name - see
    /// <see cref="WaterMap"/>, which is named the same way for the same
    /// reason.</summary>
    public static BoardMap WallMap => _wall ??= FromGround(
        "wall", WallGround, WallRamps, WallHomes, TerrainSet.Default, true,
        plinth: 1);

    /// <summary>
    /// The effects bench's board: five by five, standing a level above the datum,
    /// with the three things a burst wants something to happen against.
    ///
    /// <b>A level up rather than flat on the datum, and that is what makes the
    /// hexes read as hexes.</b> <see cref="Stage3D"/> extrudes a prism from a
    /// cell's top face down to the board's floor and <b>skips the walls when the
    /// two are the same height</b> - so a board on the datum is drawn as flat
    /// hexagons of ground with no sides at all, which is exactly what this bench
    /// looked like. One level of plinth gives every cell its side faces, and
    /// nothing relative moves: <see cref="Passable"/> reads the same board.
    /// <see cref="WallMap"/> does this for the same reason.
    ///
    /// <b>The pond is a level below its neighbours because it has to be.</b>
    /// <see cref="Flood"/> refuses a flat dry cell at a surface's own height
    /// beside it, so <c>w</c> puts it at the plinth minus one and the dry ring
    /// round it stands a step above - which is also why it is in a corner: three
    /// neighbours to bank rather than six.
    ///
    /// <b>Homes: the middle, which is where the ring of brick stands.</b> No tank
    /// is built on this bench at all, so this is the cell the wall is stood on -
    /// <c>WallProp</c> is told a cell, and <see cref="Walled"/> is where it reads
    /// which. The middle, because a burst is judged in the middle of the window.
    /// </summary>
    private static readonly string[] EffectsGround =
    {
        // 01234
        "f....", // r0  a wood in the top-left corner
        ".....", // r1
        "..W..", // r2  a ring of brick in the middle
        ".....", // r3
        "w....", // r4  a pond in the bottom-left corner
    };

    /// <summary>
    /// No ramps, and <b>this map is deliberately not in <see cref="Names"/>.</b>
    ///
    /// It was put there, and the audit theme took it apart in three passes, each
    /// one right: a pond with no ramp into it leaves the wading exemption untested;
    /// a ramp with three neighbours a level above it has no decided high edge; and
    /// water reachable only by wading off a corner ramp strands the cells behind
    /// it. Every one of those is a statement about <b>driving</b>, and the fix for
    /// each was to bend this pond further out of shape - an L in the corner with a
    /// slipway nobody uses.
    ///
    /// <b>So the exemption is named instead of engineered around.</b> No vehicle
    /// is ever built on the effects bench: it has no garage, no tick and no
    /// <c>Vehicle</c> at all, so passability, reachability and beaches are rules
    /// about a machine that is not there. The five named maps are boards a tank
    /// drives on and are judged as such; this is a backdrop for one effect, and the
    /// three things on it are there to stand in front of a burst.
    ///
    /// What it does <b>not</b> get exempted from is the structural half, which is
    /// not optional and not skippable: <see cref="FromGround"/> still throws on an
    /// unknown letter, a ramp off the board or a ramp on a rock, and
    /// <see cref="Flood"/> still refuses water it cannot bank. Those are about the
    /// map being a map.
    /// </summary>
    private static readonly string[] EffectsRamps =
    {
        ".....",
        ".....",
        ".....",
        ".....",
        ".....",
    };

    private static BoardMap? _effects;

    /// <summary>
    /// The mix, and <b>that is forced rather than chosen - it is the trap this
    /// file's own remark on <see cref="Paint"/> warns about.</b>
    ///
    /// <see cref="HexField.KindAt"/> returns a named plate for every cell if the
    /// board has one, and never looks at <see cref="Kinds"/>: so with the plain
    /// plate the wood in the corner is not a wood, it is a plain cell that the
    /// grove plants nothing on. Measured, not reasoned - the first run of this
    /// board printed <c>grove 0 props</c>.
    ///
    /// What it costs is the opinion this bench held while its board was bare: a
    /// burst is judged partly on how it stands against the ground, and the mix
    /// moves that backdrop from cell to cell. So an A/B of two bursts is worth
    /// taking on the same cell now, which it always was and now matters.
    /// </summary>
    public static BoardMap Effects => _effects ??= FromGround(
        "effects", EffectsGround, EffectsRamps,
        new Parking[] { new Vector2I(2, 2) }, TerrainSet.Mixed, true,
        plinth: 1);

    /// <summary>The maps written in this file. Five, and they are the
    /// references: the self test judges them, and a file on disk answering to
    /// one of these names is refused rather than allowed to shadow it.</summary>
    public static IReadOnlyList<string> Compiled { get; } =
        new[] { "bench", "abbey", "test", "water", "wall" };

    /// <summary>
    /// The board of that name, compiled or off disk.
    ///
    /// <b>Unknown is still the bench, and that has not changed.</b> A name
    /// nobody wrote is a typo on a command line, and coming up on the harness
    /// board is what every scene has always done with one.
    ///
    /// <b>A clash throws whichever map was asked for.</b> A file called
    /// <c>abbey</c> beside the compiled ABBEY is two references under one name -
    /// see <see cref="MapFile.Clashes"/> - and a checkout in that state is broken
    /// for every scene, not only for the scene that happened to ask.
    ///
    /// <b>Files are not cached and the compiled maps still are.</b> The editor
    /// writes into that folder while the session is running, so a map read once
    /// and held would be the board going stale the moment it was saved.
    /// </summary>
    public static BoardMap ByName(string name)
    {
        IReadOnlyList<string> clash = MapFile.Clashes(Compiled);
        if (clash.Count > 0)
            throw new InvalidOperationException(
                "these map files answer to a name this file already writes, and "
                + "one of the two is what the self test judges: "
                + string.Join(" ", clash.Select(c => $"maps/{c}.json")));

        string key = name.Trim().ToLowerInvariant();
        return key switch
        {
            "abbey" => Abbey,
            "test" => Test,
            "water" => WaterMap,
            "wall" => WallMap,
            "bench" => Bench,
            _ => MapFile.Has(key) ? MapFile.Load(key) : Bench,
        };
    }

    /// <summary>Every board this session can open, compiled first. Not cached:
    /// a name list settled at startup is a folder that stops answering the
    /// moment the editor saves into it.</summary>
    public static IReadOnlyList<string> Names =>
        Compiled.Concat(MapFile.Names().Where(
            n => !Compiled.Contains(n, StringComparer.OrdinalIgnoreCase))).ToList();
}
