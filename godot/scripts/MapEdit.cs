using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// A board being drawn: the letter grid, the ramp grid, the brushes that write
/// them and the stack that takes a stroke back.
///
/// <b>No Godot in here beyond <see cref="Vector2I"/>, and that is the point.</b>
/// Everything the editor does to a board is a question about two grids, so it is
/// answerable without a scene, a field or a frame - which is what lets the self
/// test judge the brushes rather than judging a screenshot of them.
///
/// <b>The state is the letters, not the axes.</b> The three tabs read as three
/// independent axes and they are not: the alphabet is not a cross product - see
/// <see cref="MapFile.Alphabet"/> - so sand only lies at the datum and a trench
/// never stands on a rise. Holding the axes and encoding on the way out would
/// mean a third encoder and a draft that cannot be saved and does not know it.
/// Holding the letters means every state this type can reach is a state that can
/// be written, and a brush that would leave one that cannot says so instead.
///
/// <b>Undo is a snapshot per stroke.</b> A board is two arrays of one cell each
/// - under a kilobyte at ABBEY's size - so a stroke costs a copy, and a delta
/// list would be the same behaviour with more to get wrong. One entry per
/// stroke, not per cell: a drag across forty cells is one undo.
/// </summary>
public sealed class MapEdit
{
    /// <summary>Which axis a brush writes - the editor's three tabs, and what
    /// the eraser means depends on which one is up.</summary>
    public enum Tab
    {
        Foundation,
        Cover,
        Level,
        /// <summary>The homes as the tanks on them: a brush per side and class
        /// (<see cref="Unit"/>), the eraser takes a home off.</summary>
        Units,
        /// <summary>The preset brushes (<see cref="MapPresets"/>), over the
        /// four axes rather than instead of them: the eraser clears every axis
        /// - plain ground at the datum, nothing on it.</summary>
        Presets,
    }

    public string Name { get; set; } = "untitled";

    /// <summary>The plate the board comes up on, or null to let it be derived -
    /// see <see cref="MapFile.PaintFor"/>, which carries the reason a default
    /// here would be wrong.</summary>
    public string? Paint { get; set; }

    public bool Height { get; set; } = true;

    public int Plinth { get; set; }

    public int Columns { get; private set; }
    public int Rows { get; private set; }

    private char[] _ground = Array.Empty<char>();

    /// <summary>
    /// Format 3's floor grid (docs/map-format3-plan.md, Ф3): <c>s</c> or
    /// <c>u</c> where sand or mud lies under a letter that cannot say it - a
    /// wood, a trench, a mine, a hill, a hollow - and a dot everywhere else.
    /// Written only by <see cref="Put"/>, which takes the pair from
    /// <see cref="MapFile.Encode"/>, and dropped by <see cref="Sync"/> under
    /// a letter that says its own floor; read through <see cref="GlyphAt"/>,
    /// so every brush and the rules' draft see the floor and not the letter.
    /// </summary>
    private char[] _under = Array.Empty<char>();

    /// <summary>
    /// Format 3's soil (docs/map-format3-plan.md, Ф4): each cell's own look,
    /// <see cref="Soil.As"/> where it says nothing, and the board's look for
    /// each floor beside it (<see cref="SoilBase"/>). Look only; a look on a
    /// floor it is not for is dropped by <see cref="Sync"/>, as the masonry is
    /// off a cell that stopped being a wall.
    /// </summary>
    private char[] _soil = Array.Empty<char>();

    /// <summary>The board's look for each floor it names (<c>soil.base</c>).</summary>
    public Dictionary<Foundation, char> SoilBase { get; private set; } = new();

    private bool[] _ramps = Array.Empty<bool>();
    private List<Parking> _homes = new();

    /// <summary>
    /// What the walled cells carry, keyed by cell.
    ///
    /// <b>Kept in step with the letters by <see cref="Sync"/> rather than
    /// alongside them.</b> The letter is what says a cell has a wall on it, so
    /// an entry for a cell that stopped being one is a shape nobody can see and
    /// a save that writes masonry on to open ground - which
    /// <see cref="BoardMap.FromGround"/> refuses, one board too late. Every
    /// write of a letter goes through <see cref="Put"/> or <see cref="Plot"/>,
    /// and both of them end here.
    /// </summary>
    private Dictionary<Vector2I, Masonry> _walls = new();

    /// <summary>
    /// What the rocks look like (format 3's <c>rocks</c>), by <b>every</b> cell
    /// of each rock that has a note rather than by the one cell the file
    /// writes it at.
    ///
    /// <b>Kept by the cell because a stroke cuts and joins rocks</b>
    /// (docs/rocks-plan.md С5): a rock cut in two leaves both halves holding the
    /// note, two rocks joined keep the note most of their cells hold
    /// (<see cref="Rocking"/>), and nothing has to know which stroke did it.
    /// Dropped with the letter by <see cref="Sync"/>, as the masonry is.
    /// </summary>
    private Dictionary<Vector2I, RockNote> _rocks = new();

    /// <summary>The board's wind and ice (format 3's <c>climate</c>).</summary>
    public RockClimate Climate { get; set; }

    /// <summary>The parkings, cell and class each - see
    /// <see cref="Parking"/>.</summary>
    public IReadOnlyList<Parking> Homes => _homes;

    /// <summary>Whether a cell is one of them, which is the question the plot
    /// brush and the panel ask.</summary>
    public int SlotAt(Vector2I cell)
    {
        for (int i = 0; i < _homes.Count; i++)
            if (_homes[i].Cell == cell)
                return i;
        return -1;
    }

    // --- making one ----------------------------------------------------------

    /// <summary>
    /// A new board of open ground.
    ///
    /// <b>The proportion is the caller's to get right and this says so.</b> A
    /// board square on the ground has <c>Rows = 0.866 * Columns</c>, because the
    /// column step is 1.5R and the row step is sqrt(3)R while the squash belongs
    /// to the camera - so a board typed as twenty by twenty is stretched where
    /// the tanks drive and looks fine on screen. See <see cref="Square"/>.
    /// </summary>
    public static MapEdit Blank(string name, int columns, int rows)
    {
        if (columns < 1 || rows < 1)
            throw new InvalidOperationException(
                $"a board is at least one cell each way, and this is {columns} "
                + $"x {rows}");
        var edit = new MapEdit
        {
            Name = name,
            Columns = columns,
            Rows = rows,
            // A level of plinth, and it is what makes a new board look like a
            // board at all. Stage3D extrudes a prism from a cell's top face down
            // to the board's floor and skips the sides when the two are the same
            // height - see the plinth parameter on BoardMap.FromGround - so a
            // board flat on the datum is hexagons of ground and nothing else,
            // which reads as the 3D stage having failed to come up. That is
            // exactly how it read. One level gives every cell its sides,
            // uniformly, so no relative height in the grid moves and Passable
            // reads the same board.
            Plinth = 1,
            _ground = Enumerable.Repeat(BoardMap.Plain, columns * rows).ToArray(),
            _under = Enumerable.Repeat(BoardMap.Plain, columns * rows).ToArray(),
            _soil = Enumerable.Repeat(Soil.As, columns * rows).ToArray(),
            _ramps = new bool[columns * rows],
        };
        // One home, in the middle. A board with none is refused on the way out -
        // the reachability audit has nothing to say about it - and starting with
        // that refusal standing would be a new board that is broken before it is
        // touched.
        edit._homes.Add(new Parking(new Vector2I(columns / 2, rows - 1)));
        return edit;
    }

    /// <summary>
    /// What a board is when nobody said: ten by ten.
    ///
    /// <b>One place, because it is asked from two.</b> The scene opened with no
    /// flags and the new-board dialog have to agree, and two literals agree until
    /// somebody edits one.
    ///
    /// <b>Not <see cref="Square"/> of ten, which would be nine.</b> The
    /// proportion is a suggestion the dialog makes when the width changes, not a
    /// rule about what a fresh board is; ten by ten is what was asked for, and
    /// the hint still says what square on the ground would be.
    /// </summary>
    public const int DefaultColumns = 10;

    public const int DefaultRows = 10;

    /// <summary>How many rows make a board square where the tanks drive, for a
    /// given width. The dialog's suggestion, and the one number about a new
    /// board that cannot be read off the screen.</summary>
    public static int Square(int columns) =>
        Math.Max(1, (int)Math.Round(columns * 0.866));

    /// <summary>An existing board opened for editing.</summary>
    public static MapEdit Of(BoardMap map)
    {
        string[] ground = MapFile.Ground(map);
        string[]? floors = MapFile.Floors(map);
        var edit = new MapEdit
        {
            Name = map.Name,
            Paint = map.Paint,
            Height = map.Height,
            Plinth = map.Plinth,
            Columns = map.Columns,
            Rows = map.Rows,
            _ground = new char[map.Columns * map.Rows],
            _under = Enumerable.Repeat(BoardMap.Plain, map.Columns * map.Rows).ToArray(),
            _soil = map.SoilCells is { } looks ? (char[])looks.Clone()
                : Enumerable.Repeat(Soil.As, map.Columns * map.Rows).ToArray(),
            SoilBase = new Dictionary<Foundation, char>(map.SoilBase),
            _ramps = (bool[])map.Ramps.Clone(),
            _homes = map.Parked.ToList(),
            // Copied rather than referenced: the board a draft was opened from
            // is somebody else's, and a cached BoardMap.Abbey edited in place
            // would be every scene in the session drawing the edit.
            _walls = map.Walling.ToDictionary(kv => kv.Key,
                                              kv => kv.Value.Copy()),
            Climate = map.Climate,
        };
        for (int r = 0; r < map.Rows; r++)
        for (int q = 0; q < map.Columns; q++)
        {
            var cell = new Vector2I(q, r);
            edit._ground[edit.At(cell)] = ground[r][q];
            if (floors is not null)
                edit._under[edit.At(cell)] = floors[r][q];
            // And the entry every wall letter means, for the cells the board
            // said nothing about. A board that authored no shapes - which is
            // every compiled one - would otherwise open with masonry the letters
            // declare and the panel cannot see, and the first save would write
            // the same board back without it.
            edit.Sync(cell);
        }
        edit.Spread(map.Rocking);
        return edit;
    }

    /// <summary>A file's rock notes, each laid over every cell of its rock
    /// (<see cref="_rocks"/>). A note off a rock is dropped: a draft is
    /// allowed to be wrong, and the letter is what says what is a rock.</summary>
    private void Spread(IReadOnlyDictionary<Vector2I, RockNote>? rocking)
    {
        if (rocking is null || rocking.Count == 0)
            return;
        foreach (List<Vector2I> rock in Rocks())
            foreach (Vector2I c in rock)
                if (rocking.TryGetValue(c, out RockNote? note))
                {
                    foreach (Vector2I d in rock)
                        _rocks[d] = note;
                    break;
                }
    }

    // --- rocks ---------------------------------------------------------------

    /// <summary>The draft's rocks, as <see cref="BoardMap.RockGroups"/> finds
    /// them on a board.</summary>
    public List<List<Vector2I>> Rocks() =>
        BoardMap.RockGroups(Columns, Rows, c => GlyphAt(c).Floor == Foundation.Rock);

    /// <summary>The rock a cell is part of, or empty off a rock.</summary>
    public IReadOnlyList<Vector2I> RockOf(Vector2I cell)
    {
        if (!Inside(cell) || GlyphAt(cell).Floor != Foundation.Rock)
            return Array.Empty<Vector2I>();
        foreach (List<Vector2I> rock in Rocks())
            if (rock.Contains(cell))
                return rock;
        return Array.Empty<Vector2I>();
    }

    /// <summary>The note the cell's rock holds there, or null - a crag, as a
    /// bare letter always was.</summary>
    public RockNote? RockAt(Vector2I cell) =>
        _rocks.TryGetValue(cell, out RockNote? note) ? note : null;

    /// <summary>
    /// Give a whole rock one note, or none (null or <see cref="RockNote.Plain"/>
    /// takes it back to a crag). Names and figures are the file's to judge -
    /// the panel offers only what the file reads - so what is refused here is
    /// a cell that is no rock.
    /// </summary>
    public bool Note(Vector2I cell, RockNote? note, out string why)
    {
        IReadOnlyList<Vector2I> rock = RockOf(cell);
        if (rock.Count == 0)
        {
            why = $"({cell.X},{cell.Y}) is no rock";
            return false;
        }
        why = "";
        foreach (Vector2I c in rock)
            if (note is null || note.Plain)
                _rocks.Remove(c);
            else
                _rocks[c] = note;
        return true;
    }

    /// <summary>
    /// The rocks' notes as the file writes them: one a rock, at its first cell
    /// in reading order of the walk, and the one most of its cells hold. A rock
    /// two joined by a stroke keeps the larger one's; a tie with the cells
    /// that hold none is a crag, so a stroke never gives a rock a look more
    /// of it did not have.
    /// </summary>
    public Dictionary<Vector2I, RockNote> Rocking()
    {
        var said = new Dictionary<Vector2I, RockNote>();
        if (_rocks.Count == 0)
            return said;
        foreach (List<Vector2I> rock in Rocks())
        {
            var count = new Dictionary<RockNote, int>();
            int plain = 0;
            foreach (Vector2I c in rock)
                if (_rocks.TryGetValue(c, out RockNote? n))
                    count[n] = count.GetValueOrDefault(n) + 1;
                else
                    plain++;
            RockNote? best = null;
            int most = 0;
            foreach ((RockNote n, int k) in count)
                if (k > most)
                    (best, most) = (n, k);
            if (best is not null && most > plain)
                said[rock[0]] = best;
        }
        return said;
    }

    // --- reading -------------------------------------------------------------

    public int At(Vector2I cell) => cell.Y * Columns + cell.X;

    public bool Inside(Vector2I cell) =>
        cell.X >= 0 && cell.X < Columns && cell.Y >= 0 && cell.Y < Rows;

    public char LetterAt(Vector2I cell) =>
        Inside(cell) ? _ground[At(cell)] : BoardMap.Off;

    /// <summary>What the cell's letter says. Never null: a letter that is not in
    /// the alphabet cannot get into the grid, because every write goes through
    /// <see cref="Put"/>.</summary>
    public MapFile.Glyph GlyphAt(Vector2I cell) =>
        MapFile.Under(MapFile.Of(LetterAt(cell)) ?? MapFile.Of(BoardMap.Off)!.Value,
                      Inside(cell) ? _under[At(cell)] : BoardMap.Plain);

    public bool OnBoard(Vector2I cell) =>
        Inside(cell) && _ground[At(cell)] != BoardMap.Off;

    public bool IsRamp(Vector2I cell) => Inside(cell) && _ramps[At(cell)];

    /// <summary>The grids as the file writes them, so a draft can be looked at
    /// as the picture it is.</summary>
    public string[] Ground()
    {
        var rows = new string[Rows];
        for (int r = 0; r < Rows; r++)
            rows[r] = new string(_ground, r * Columns, Columns);
        return rows;
    }

    /// <summary>The floor grid as the file writes it, or null when no cell
    /// needs one (<see cref="MapFile.Floors"/>).</summary>
    public string[]? Floors()
    {
        if (_under.All(c => c == BoardMap.Plain))
            return null;
        var rows = new string[Rows];
        for (int r = 0; r < Rows; r++)
            rows[r] = new string(_under, r * Columns, Columns);
        return rows;
    }

    /// <summary>The soil grid as the file writes it, or null when no cell
    /// has a look of its own.</summary>
    public string[]? SoilRows() => MapFile.Rows(_soil, Columns, Rows);

    /// <summary>A cell's own look, <see cref="Soil.As"/> when it has none.</summary>
    public char SoilAt(Vector2I cell) => Inside(cell) ? _soil[At(cell)] : Soil.As;

    /// <summary>The look the cell wears, the three layers resolved
    /// (<see cref="Soil.Resolve"/>).</summary>
    public char LookAt(Vector2I cell)
    {
        MapFile.Glyph g = GlyphAt(cell);
        return Soil.Resolve(SoilAt(cell), g.Floor, g.Water, SoilBase);
    }

    /// <summary>Every cell's look, resolved - <see cref="HexField.SetSoil"/>'s
    /// array; null when the board draws nothing apart from today's ground.</summary>
    public char[]? Looks()
    {
        if (SoilBase.Count == 0 && _soil.All(c => c == Soil.As))
            return null;
        var looks = new char[Columns * Rows];
        for (int r = 0; r < Rows; r++)
            for (int q = 0; q < Columns; q++)
                looks[r * Columns + q] = LookAt(new Vector2I(q, r));
        return looks;
    }

    /// <summary>
    /// Give a cell a look of its own, or none (<see cref="Soil.As"/>, the
    /// eraser's). A look not of the cell's floor is refused and named - dunes
    /// want sand - rather than written to be dropped.
    /// </summary>
    public bool Wear(Vector2I cell, char look, out string why)
    {
        why = "";
        if (!OnBoard(cell))
        {
            why = $"({cell.X},{cell.Y}) is off the board";
            return false;
        }
        MapFile.Glyph g = GlyphAt(cell);
        if (look != Soil.As && (g.Water || Soil.FloorOf(look) != g.Floor))
        {
            why = $"{Soil.Name(look)} is a look of {Soil.FloorOf(look)?.ToString().ToLowerInvariant()}, "
                  + $"and ({cell.X},{cell.Y}) is " + (g.Water ? "water" : g.Floor.ToString().ToLowerInvariant());
            return false;
        }
        _soil[At(cell)] = look;
        return true;
    }

    /// <summary>The board's look for a floor, or none to take it back to
    /// today's.</summary>
    public void Lay(Foundation floor, char? look)
    {
        if (look is char l && Soil.FloorOf(l) == floor && l != Soil.Today(floor))
            SoilBase[floor] = l;
        else
            SoilBase.Remove(floor);
    }

    public string[] Ramps()
    {
        var rows = new string[Rows];
        for (int r = 0; r < Rows; r++)
            rows[r] = new string(Enumerable.Range(0, Columns)
                .Select(q => _ramps[r * Columns + q] ? 'r' : '.').ToArray());
        return rows;
    }

    // --- strokes -------------------------------------------------------------

    /// <summary>Everything a stroke can change, in one value. A named shape
    /// rather than a tuple written out five times: the day a fifth grid is
    /// added, a tuple is five places to add it and four to forget.</summary>
    /// <remarks>The size is in it since the board could be resized
    /// (<see cref="Reframe"/>): a stroke's grids are only read at the size
    /// they were taken at.</remarks>
    private readonly record struct Snap(char[] Ground, char[] Under, char[] Soil,
                                        Dictionary<Foundation, char> SoilBase, bool[] Ramps,
                                        List<Parking> Homes,
                                        Dictionary<Vector2I, Masonry> Walls,
                                        Dictionary<Vector2I, RockNote> Rocks,
                                        RockClimate Climate,
                                        int Columns, int Rows);

    private Snap Taken() => new((char[])_ground.Clone(), (char[])_under.Clone(),
                                (char[])_soil.Clone(), new Dictionary<Foundation, char>(SoilBase),
                                (bool[])_ramps.Clone(), _homes.ToList(),
                                _walls.ToDictionary(kv => kv.Key,
                                                    kv => kv.Value.Copy()),
                                new Dictionary<Vector2I, RockNote>(_rocks), Climate,
                                Columns, Rows);

    private void Restore(Snap was) =>
        (_ground, _under, _soil, SoilBase, _ramps, _homes, _walls, _rocks, Climate, Columns, Rows) =
            (was.Ground, was.Under, was.Soil, was.SoilBase, was.Ramps, was.Homes, was.Walls, was.Rocks, was.Climate,
             was.Columns, was.Rows);

    private readonly List<Snap> _back = new();

    private readonly List<Snap> _forward = new();

    /// <summary>How many strokes can still be taken back.</summary>
    public int Depth => _back.Count;

    /// <summary>
    /// Start a stroke: everything written until the next <see cref="Begin"/> is
    /// one entry on the stack.
    ///
    /// <b>The redo stack is dropped here rather than on the write.</b> A stroke
    /// that turns out to write nothing - a brush dragged over cells it cannot
    /// change - should not throw away a redo the author can still see.
    /// </summary>
    public void Begin()
    {
        _back.Add(Taken());
        _forward.Clear();
    }

    /// <summary>
    /// End a stroke: the entry <see cref="Begin"/> made is taken off again when
    /// the stroke changed nothing - a click on a cell already that floor, a drag
    /// over cells the brush cannot change - so Z takes back the last stroke that
    /// did something rather than a click nobody saw. Answers whether the entry
    /// stays.
    /// </summary>
    public bool End()
    {
        if (_back.Count == 0)
            return false;
        Snap was = _back[^1];
        bool same = was.Columns == Columns && was.Rows == Rows
                    && was.Ground.SequenceEqual(_ground) && was.Under.SequenceEqual(_under)
                    && was.Soil.SequenceEqual(_soil) && was.SoilBase.Count == SoilBase.Count
                    && was.SoilBase.All(kv => SoilBase.TryGetValue(kv.Key, out char now) && now == kv.Value)
                    && was.Ramps.SequenceEqual(_ramps)
                    && was.Homes.SequenceEqual(_homes) && was.Walls.Count == _walls.Count
                    && was.Walls.All(kv => _walls.TryGetValue(kv.Key, out Masonry? now)
                                           && now.Say() == kv.Value.Say())
                    && was.Climate == Climate && was.Rocks.Count == _rocks.Count
                    && was.Rocks.All(kv => _rocks.TryGetValue(kv.Key, out RockNote? now)
                                           && now == kv.Value);
        if (same)
            _back.RemoveAt(_back.Count - 1);
        return !same;
    }

    public bool Undo()
    {
        if (_back.Count == 0)
            return false;
        _forward.Add(Taken());
        Restore(_back[^1]);
        _back.RemoveAt(_back.Count - 1);
        return true;
    }

    public bool Redo()
    {
        if (_forward.Count == 0)
            return false;
        _back.Add(Taken());
        Restore(_forward[^1]);
        _forward.RemoveAt(_forward.Count - 1);
        return true;
    }

    // --- writing -------------------------------------------------------------

    /// <summary>
    /// Write the four axes as one letter, or refuse naming what has no letter.
    ///
    /// <b>Every write goes through here.</b> That is what keeps the invariant
    /// the type is built on - the grid only ever holds letters - and it is what
    /// makes the refusals uniform: the author is told which combination the
    /// alphabet does not carry, rather than finding out when a save fails.
    /// </summary>
    private bool Put(Vector2I cell, Foundation floor, Cover over, int level,
                     bool water, bool cliff, out string why)
    {
        why = "";
        if (!Inside(cell))
        {
            why = $"({cell.X},{cell.Y}) is off the grid";
            return false;
        }
        // a letter, and the floor grid's mark where the letter cannot say the
        // floor - sand under a wood, mud on a hill (MapFile.Encode)
        if (MapFile.Encode(floor, over, level, water, cliff) is not { } found)
        {
            why = $"the alphabet has no letter for {Say(floor, over, level, water)}"
                  + $" - see MapFile.Alphabet, which is not a cross product";
            return false;
        }
        _ground[At(cell)] = found.Letter;
        _under[At(cell)] = found.Under;
        Sync(cell);
        return true;
    }

    /// <summary>
    /// Bring a cell's masonry into line with its letter: a cell that has just
    /// become a wall gets the closed ring every bare wall letter means, and one
    /// that has stopped being a wall loses its entry.
    ///
    /// <b>Dropped rather than kept for the author who paints it back.</b> A
    /// remembered shape would come back on a cell the author walled again for
    /// other reasons, which is the editor writing a wall nobody drew; and an
    /// undo brings the whole entry back anyway, which is where "I did not mean
    /// that" already has an answer.
    /// </summary>
    private void Sync(Vector2I cell)
    {
        // a letter that says its own floor takes no mark under it - plain, sand,
        // water, a rock, a wall, the edge
        if (Inside(cell) && !MapFile.TakesUnder(LetterAt(cell)))
            _under[At(cell)] = BoardMap.Plain;
        // a look stays only on the floor it is for
        if (Inside(cell) && _soil[At(cell)] != Soil.As
            && (GlyphAt(cell).Water || Soil.FloorOf(_soil[At(cell)]) != GlyphAt(cell).Floor))
            _soil[At(cell)] = Soil.As;
        if (GlyphAt(cell).Wall)
            _walls.TryAdd(cell, new Masonry());
        else
            _walls.Remove(cell);
        if (GlyphAt(cell).Floor != Foundation.Rock)
            _rocks.Remove(cell);
    }

    private static string Say(Foundation floor, Cover over, int level, bool water)
    {
        string what = water ? "water" : floor.ToString().ToLowerInvariant();
        if (over != Cover.None)
            what += " with " + over.ToString().ToLowerInvariant() + " on it";
        return $"{what} at level {level}";
    }

    /// <summary>
    /// Paint the floor.
    ///
    /// <b>The rock is the whole cell and is refused on to an occupied one.</b>
    /// A rock is a floor, a height and a declaration at once - see
    /// <see cref="BoardMap.Rock"/> - so it cannot be laid under a wood or a wall
    /// without deciding on the author's behalf what happened to them.
    ///
    /// <b>Shallow and deep are not floors a map may write.</b> Every shipped
    /// board says water with the flood letter, which is low ground with water
    /// standing in it; a floor made of water is a thing the model has and the
    /// format does not. Named rather than silently missing from the palette.
    /// </summary>
    public bool Floor(Vector2I cell, Foundation want, out string why)
    {
        MapFile.Glyph was = GlyphAt(cell);
        switch (want)
        {
            case Foundation.Shallow:
                why = "a ford has no floor letter - shallow water on a map is "
                      + "the flood letter, which is low ground with water "
                      + "standing in it";
                return false;
            case Foundation.Deep:
                // Deep water is the hollow filled to the brim, so the letter
                // carries the level the same way the ford's does - see
                // BoardMap.DeepWet.
                return Put(cell, Foundation.Deep, Cover.None, -1, true, false,
                           out why);
            case Foundation.Void:
                return Plot(cell, false, out why);
            case Foundation.Rock:
                if (was.Over != Cover.None || was.Wall)
                {
                    why = $"({cell.X},{cell.Y}) carries "
                          + was.Over.ToString().ToLowerInvariant()
                          + ", and a rock is the whole cell - clear it first";
                    return false;
                }
                return Put(cell, Foundation.Rock, Cover.None, 2, false, true,
                           out why);
            default:
                return Put(cell, want, was.Over, was.Cliff ? 0 : was.Level,
                           was.Water, false, out why);
        }
    }

    /// <summary>
    /// Stand water on a cell, or take it off.
    ///
    /// <b>The letter carries its own level and that is not a side effect.</b>
    /// The alphabet's water is low ground with water in it - one statement - so
    /// painting water puts the cell a level down, and raising it afterwards is
    /// refused by the same table. Taking the water off leaves the hollow, which
    /// is the cell the map drew.
    /// </summary>
    public bool Water(Vector2I cell, bool wet, out string why)
    {
        MapFile.Glyph was = GlyphAt(cell);
        // Draining deep water leaves the same hollow a ford leaves: the floor
        // named the depth, and dry ground has no depth to name.
        Foundation dry = was.Floor == Foundation.Deep ? Foundation.Solid : was.Floor;
        return wet
            ? Put(cell, Foundation.Solid, Cover.None, -1, true, false, out why)
            : Put(cell, dry, was.Over, was.Level, false, was.Cliff, out why);
    }

    /// <summary>Paint what stands on the cell.</summary>
    public bool Over(Vector2I cell, Cover want, out string why)
    {
        MapFile.Glyph was = GlyphAt(cell);
        if (was.Cliff && want != Cover.None)
        {
            why = $"({cell.X},{cell.Y}) is a rock, and nothing stands on one";
            return false;
        }
        return Put(cell, was.Floor, want, was.Level, was.Water, was.Cliff,
                   out why);
    }

    /// <summary>
    /// Move a cell up or down.
    ///
    /// <b>Level +2 is refused, and that is the rock being declared rather than
    /// derived.</b> Two levels up with no ramp on to it is what a rock is made
    /// of, so a cell raised there by the height brush would be a cliff nobody
    /// declared - and the one check that catches a broken map cannot tell a
    /// deliberate one from a valley cut off by mistake. The rock is a floor, and
    /// it is painted as one.
    /// </summary>
    public bool Level(Vector2I cell, int want, out string why)
    {
        MapFile.Glyph was = GlyphAt(cell);
        if (was.Cliff)
        {
            why = $"({cell.X},{cell.Y}) is a rock, which stands where it stands "
                  + "- paint it something else first";
            return false;
        }
        if (want >= 2)
        {
            why = "two levels up with nothing on to it is a rock, and a rock is "
                  + "declared rather than raised into - paint it on the floor "
                  + "tab";
            return false;
        }
        return Put(cell, was.Floor, was.Over, want, was.Water, false, out why);
    }

    public bool Raise(Vector2I cell, int by, out string why) =>
        Level(cell, GlyphAt(cell).Level + by, out why);

    /// <summary>Put a cell on the board or take it off it. Not the eraser: the
    /// edge of the world is a brush of its own, because an empty cell and a cell
    /// that is not there are different statements.</summary>
    public bool Plot(Vector2I cell, bool on, out string why)
    {
        why = "";
        if (!Inside(cell))
        {
            why = $"({cell.X},{cell.Y}) is off the grid";
            return false;
        }
        if (!on && SlotAt(cell) >= 0)
        {
            why = $"({cell.X},{cell.Y}) is a home - move it before taking its "
                  + "cell off the board";
            return false;
        }
        _ground[At(cell)] = on ? BoardMap.Plain : BoardMap.Off;
        if (!on)
            _ramps[At(cell)] = false;
        Sync(cell);
        return true;
    }

    /// <summary>
    /// Mark or clear a ramp.
    ///
    /// <b>Whether it is legal is not asked here.</b> A ramp with two neighbours
    /// above it is a fault to be painted, not a brush to be refused - the board
    /// under a brush is illegal almost always, which is the whole reason
    /// <see cref="MapRules"/> returns a list. What is refused is a ramp on a
    /// rock, because that is the map disagreeing with itself rather than a board
    /// halfway through being drawn.
    /// </summary>
    public bool Ramp(Vector2I cell, bool on, out string why)
    {
        why = "";
        if (!Inside(cell))
        {
            why = $"({cell.X},{cell.Y}) is off the grid";
            return false;
        }
        if (on && !OnBoard(cell))
        {
            why = $"({cell.X},{cell.Y}) is not on the board";
            return false;
        }
        if (on && GlyphAt(cell).Cliff)
        {
            why = $"({cell.X},{cell.Y}) is a rock, which is the one rise that is "
                  + "not meant to be driven";
            return false;
        }
        _ramps[At(cell)] = on;
        return true;
    }

    /// <summary>
    /// The eraser, and what it means depends on which tab is up.
    ///
    /// One key, three meanings, because "empty" is a different cell on each
    /// axis: no cover, plain floor, the datum. Off the board is not among them -
    /// see <see cref="Plot"/>.
    /// </summary>
    public bool Erase(Vector2I cell, Tab tab, out string why) => tab switch
    {
        Tab.Cover => Over(cell, Cover.None, out why),
        Tab.Foundation => Floor(cell, Foundation.Solid, out why),
        Tab.Units => Unhome(cell, out why),
        Tab.Presets => Wipe(cell, out why),
        _ => Ramp(cell, false, out why) && Level(cell, 0, out why),
    };

    /// <summary>Every axis of a cell back to plain ground at the datum - the
    /// Presets tab's eraser. The home on it stays: tanks are the Units tab's.</summary>
    private bool Wipe(Vector2I cell, out string why)
    {
        why = OnBoard(cell) ? "" : $"({cell.X},{cell.Y}) is not on the board";
        return OnBoard(cell) && Clear(new[] { cell }, homes: false) >= 0;
    }

    /// <summary>
    /// A copy to try a stroke on - the preset's ghost lays it here and draws
    /// what changed. No history: it is thrown away the same frame.
    /// </summary>
    public MapEdit Fork() => new()
    {
        Name = Name, Paint = Paint, Height = Height, Plinth = Plinth,
        Columns = Columns, Rows = Rows,
        _ground = (char[])_ground.Clone(),
        _under = (char[])_under.Clone(),
        _soil = (char[])_soil.Clone(),
        SoilBase = new Dictionary<Foundation, char>(SoilBase),
        _ramps = (bool[])_ramps.Clone(),
        _homes = _homes.ToList(),
        _walls = _walls.ToDictionary(kv => kv.Key, kv => kv.Value.Copy()),
        _rocks = new Dictionary<Vector2I, RockNote>(_rocks),
        Climate = Climate,
    };

    /// <summary>
    /// The cells of a straight line from <paramref name="a"/> to
    /// <paramref name="b"/>, both ends in - the line tool. Walked in axial
    /// coordinates, a cell per step of hex distance, each point rounded to the
    /// nearest hex, so the line has no gaps and no doubled cells on any
    /// heading (the odd-q offset would bend it).
    /// </summary>
    public static IReadOnlyList<Vector2I> Line(Vector2I a, Vector2I b)
    {
        Vector2I from = Axial(a), to = Axial(b);
        int dx = to.X - from.X, dz = to.Y - from.Y;
        int n = (Math.Abs(dx) + Math.Abs(dz) + Math.Abs(dx + dz)) / 2;
        var cells = new List<Vector2I>(n + 1);
        for (int i = 0; i <= n; i++)
        {
            float t = n == 0 ? 0.0f : (float)i / n;
            // nudged off the ties a line along an edge would land on
            float x = from.X + dx * t + 1e-4f, z = from.Y + dz * t + 2e-4f, y = -x - z;
            int rx = (int)MathF.Round(x), rz = (int)MathF.Round(z), ry = (int)MathF.Round(y);
            float ex = MathF.Abs(rx - x), ey = MathF.Abs(ry - y), ez = MathF.Abs(rz - z);
            if (ex > ey && ex > ez)
                rx = -ry - rz;
            else if (ez > ey)
                rz = -rx - ry;
            Vector2I cell = Offset(new Vector2I(rx, rz));
            if (cells.Count == 0 || cells[^1] != cell)
                cells.Add(cell);
        }
        return cells;
    }

    /// <summary>The home on a cell taken off, or a refusal when there is none
    /// or it is the last.</summary>
    public bool Unhome(Vector2I cell, out string why)
    {
        int slot = SlotAt(cell);
        if (slot < 0)
        {
            why = $"no tank on ({cell.X},{cell.Y})";
            return false;
        }
        // the others keep who they are as this one's place in the order goes
        Freeze();
        return DropHome(slot, out why);
    }

    /// <summary>
    /// A tank of <paramref name="side"/> and class <paramref name="tag"/> on a
    /// cell - the Units tab's click.
    ///
    /// <b>Put, or the tank on the cell made this one.</b> A side may field
    /// several of a class (<see cref="BoardMap.MustCrew"/>), so a click puts a
    /// new tank on an empty cell; on a cell with a tank it turns that tank into
    /// the brush's, its facing kept. Moving one is a drag (<see cref="Move"/>).
    /// </summary>
    public bool Unit(Vector2I cell, string side, string tag, out string why)
    {
        why = "";
        if (!OnBoard(cell))
        {
            why = $"({cell.X},{cell.Y}) is not on the board";
            return false;
        }
        if (!Parking.Sides.Contains(side, StringComparer.OrdinalIgnoreCase))
        {
            why = $"{side} is not a side - " + string.Join(", ", Parking.Sides);
            return false;
        }
        if (!MovementProfile.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
        {
            why = $"{tag} is not a class this build has - " + string.Join(", ", MovementProfile.Tags);
            return false;
        }
        side = side.ToLowerInvariant();
        Freeze();
        int here = SlotAt(cell);
        if (here >= 0)
            _homes[here] = _homes[here] with { Class = tag, Side = side };
        else
            _homes.Add(new Parking(cell, tag, side));
        return true;
    }

    /// <summary>A home's tank carried to another cell, facing and all - the
    /// Units tab's drag. Refused onto a cell another home stands on: two tanks
    /// in one hex are one tank on screen.</summary>
    public bool Move(int slot, Vector2I cell, out string why)
    {
        why = "";
        if (slot < 0 || slot >= _homes.Count)
        {
            why = $"there is no home {slot}";
            return false;
        }
        if (SlotAt(cell) is int other && other >= 0 && other != slot)
        {
            why = $"home {other + 1} stands on ({cell.X},{cell.Y})";
            return false;
        }
        // said out loud first, so the move shifts no one's order
        Freeze();
        return Home(cell, slot, out why);
    }

    /// <summary>
    /// Every home the match fields says its side and class out loud - what
    /// <see cref="Lineup.Of"/> gives it now.
    ///
    /// <b>Before the Units brush touches one, all of them.</b> A home that
    /// names nothing takes its side and class from its order among the others
    /// (<see cref="Parking.SidesOf"/>); naming one of them shifts that order,
    /// and a brown tank five homes on would turn green. Said out loud, each
    /// stays who it is whatever the brush does to its neighbours - and the
    /// brush finds the tank it means by name. The facings stay as they were.
    /// </summary>
    private void Freeze()
    {
        foreach (Lineup.Unit u in Lineup.Of(_homes))
        {
            int pair = Lineup.PairAt(u.Model);
            if (pair >= 0)
                _homes[u.Slot] = _homes[u.Slot] with { Side = u.Side, Class = Lineup.Pairs[pair].Class };
        }
    }

    /// <summary>
    /// Put a home on a cell.
    ///
    /// <b>A slot past the end appends.</b> The homes are a list and the first is
    /// the one a single-tank run gets - see <see cref="BoardMap.Homes"/> - so
    /// which slot a home is in matters and adding one is naming the next.
    /// </summary>
    public bool Home(Vector2I cell, int slot, out string why)
    {
        why = "";
        if (!OnBoard(cell))
        {
            why = $"({cell.X},{cell.Y}) is not on the board";
            return false;
        }
        if (slot < 0)
        {
            why = $"there is no home {slot}";
            return false;
        }
        if (slot >= _homes.Count)
            _homes.Add(new Parking(cell));
        else
            // The class the slot asked for is kept: moving a parking is moving
            // it, and a home that forgot who it was for on being dragged is a
            // second statement made by a gesture about the first.
            _homes[slot] = _homes[slot] with { Cell = cell };
        return true;
    }

    /// <summary>
    /// Which class a parking asks for, or null for whoever is left over.
    ///
    /// <b>Refused for a class this build has not got</b> - what
    /// <see cref="BoardMap.MustCrew"/> would throw on at save, refused here,
    /// where the author is looking. Another home asking for the same class is
    /// fine: a side may field several.
    /// </summary>
    public bool Crew(int slot, string? tag, out string why)
    {
        why = "";
        if (slot < 0 || slot >= _homes.Count)
        {
            why = $"there is no home {slot}";
            return false;
        }
        if (tag is { Length: > 0 })
        {
            if (!MovementProfile.Tags.Contains(tag,
                    StringComparer.OrdinalIgnoreCase))
            {
                why = $"{tag} is not a class this build has - "
                      + string.Join(", ", MovementProfile.Tags);
                return false;
            }
        }
        _homes[slot] = _homes[slot] with
        {
            Class = tag is { Length: > 0 } ? tag : null,
        };
        return true;
    }

    /// <summary>
    /// Which side a parking is on in a match, or null for the one its order
    /// gives it (see <see cref="Parking"/>).
    /// </summary>
    public bool Team(int slot, string? side, out string why)
    {
        why = "";
        if (slot < 0 || slot >= _homes.Count)
        {
            why = $"there is no home {slot}";
            return false;
        }
        if (side is { Length: > 0 }
            && !Parking.Sides.Contains(side, StringComparer.OrdinalIgnoreCase))
        {
            why = $"{side} is not a side - " + string.Join(", ", Parking.Sides);
            return false;
        }
        string? to = side is { Length: > 0 } ? side.ToLowerInvariant() : null;
        _homes[slot] = _homes[slot] with { Side = to };
        return true;
    }

    /// <summary>
    /// Which edge a parking's tank starts facing, or null for its side's way
    /// across the board. Only the six edge headings: a tank on a hex faces an
    /// edge.
    /// </summary>
    public bool Face(int slot, int? facing, out string why)
    {
        why = "";
        if (slot < 0 || slot >= _homes.Count)
        {
            why = $"there is no home {slot}";
            return false;
        }
        if (facing is int f && !HexField.EdgeHeadings.Contains(f))
        {
            why = $"{f} is not an edge - " + string.Join(", ", HexField.EdgeHeadings);
            return false;
        }
        _homes[slot] = _homes[slot] with { Facing = facing };
        return true;
    }

    public bool DropHome(int slot, out string why)
    {
        why = "";
        if (slot < 0 || slot >= _homes.Count)
        {
            why = $"there is no home {slot}";
            return false;
        }
        if (_homes.Count == 1)
        {
            why = "a board with no homes is refused on the way out, because the "
                  + "reachability audit has nothing to say about one";
            return false;
        }
        _homes.RemoveAt(slot);
        return true;
    }

    // --- the masonry ----------------------------------------------------------

    /// <summary>Every cell carrying masonry, in reading order.
    /// <see cref="BoardMap.Walled"/>'s answer for a board still being drawn.
    /// </summary>
    public IReadOnlyList<Vector2I> Walled() =>
        _walls.Keys.OrderBy(c => c.Y).ThenBy(c => c.X).ToList();

    /// <summary>What the cell's wall is, or null on a cell with none. A copy:
    /// the entry is written through <see cref="Edge"/> and <see cref="Shape"/>
    /// so that every change to a board is a change this type made.</summary>
    public Masonry? MasonryAt(Vector2I cell) =>
        _walls.TryGetValue(cell, out Masonry? wall) ? wall.Copy() : null;

    /// <summary>
    /// Put masonry on one edge of a cell, or take it off.
    ///
    /// <b>The last edge is refused.</b> A cell whose six sides are all down is
    /// <see cref="CoverState.Breached"/> - a thing that happens to a wall in a
    /// game rather than a thing a map declares - and it would be written to the
    /// file as a wall standing on nothing. Taking the cover off is what an
    /// author means, and it is one tab away.
    ///
    /// <b>Two runs are not refused</b>, and that is the other half of the same
    /// judgement: a wall being drawn passes through being two walls on the way
    /// to being one, so it is reported by <see cref="Masonry.Run"/> and painted
    /// rather than blocked - the rule <see cref="Ramp"/> already follows.
    /// </summary>
    public bool Edge(Vector2I cell, int heading, bool on, out string why)
    {
        why = "";
        if (!_walls.TryGetValue(cell, out Masonry? wall))
        {
            why = $"({cell.X},{cell.Y}) carries no wall";
            return false;
        }
        if (!Masonry.Headings.Contains(heading))
        {
            why = $"{heading} is not an edge of a flat-topped hex";
            return false;
        }
        int bit = 1 << Masonry.Bit(heading);
        if (!on && (wall.Edges & ~bit & Masonry.Ring) == 0)
        {
            why = "a wall standing on no sides at all is a breach rather than a "
                  + "wall - take the cover off instead";
            return false;
        }
        wall.Edges = on ? wall.Edges | bit : wall.Edges & ~bit;
        return true;
    }

    /// <summary>
    /// Make a walled cell a concrete capon with its slit toward
    /// <paramref name="facing"/>, or brick masonry again.
    ///
    /// <b>The capon was written by hand until this</b> - <c>kind</c> and
    /// <c>facing</c> in the file's walls block, nothing in the editor said
    /// either (docs/editor3d-plan.md, stage 4). The facing is one of the six
    /// edges, the gate opposite it; a wall's own facing is
    /// <see cref="Masonry.Run"/>'s to derive, so going back to brick clears it.
    /// </summary>
    public bool Shelter(Vector2I cell, bool capon, int facing, out string why)
    {
        why = "";
        if (!_walls.TryGetValue(cell, out Masonry? wall))
        {
            why = $"({cell.X},{cell.Y}) carries no wall - lay walls first";
            return false;
        }
        if (capon && !Masonry.Headings.Contains(facing))
        {
            why = $"{facing} is not an edge of a flat-topped hex";
            return false;
        }
        wall.Kind = capon ? Masonry.Sort.Capon : Masonry.Sort.Wall;
        wall.Facing = capon ? facing : null;
        if (capon)
            wall.Edges = Masonry.Ring;
        return true;
    }

    /// <summary>One of the four numbers that are a wall's shape, or null to
    /// take the cell back to silence - see <see cref="Masonry"/>, where null
    /// being silence rather than a default is argued.</summary>
    public bool Shape(Vector2I cell, Masonry.Dial dial, int? value,
                      out string why)
    {
        why = "";
        if (!_walls.TryGetValue(cell, out Masonry? wall))
        {
            why = $"({cell.X},{cell.Y}) carries no wall";
            return false;
        }
        if (value is int figure)
        {
            (int low, int high) = Masonry.Range(dial);
            if (figure < low || figure > high)
            {
                why = $"a wall takes {low} to {high} "
                      + $"{dial.ToString().ToLowerInvariant()}, not {figure}";
                return false;
            }
        }
        wall.Set(dial, value);
        return true;
    }

    // --- brushes -------------------------------------------------------------

    /// <summary>
    /// Every cell within <paramref name="radius"/> steps of one, the centre
    /// included.
    ///
    /// <b>A hex ring rather than a square.</b> A square brush on a hex grid puts
    /// more cells under the pointer in some directions than others, which is the
    /// brush lying about its own size.
    ///
    /// Cells off the plot are in it: the edge of the world is a brush too, and a
    /// board is widened by painting its border back on.
    /// </summary>
    public IReadOnlyList<Vector2I> Ring(Vector2I centre, int radius)
    {
        var seen = new HashSet<Vector2I> { centre };
        var edge = new List<Vector2I> { centre };
        for (int step = 0; step < Math.Max(0, radius); step++)
        {
            var next = new List<Vector2I>();
            foreach (Vector2I cell in edge)
            foreach (int heading in HexField.EdgeHeadings)
            {
                Vector2I to = HexField.Step(cell, heading);
                if (Inside(to) && seen.Add(to))
                    next.Add(to);
            }
            edge = next;
        }
        return seen.Where(Inside)
            .OrderBy(c => c.Y).ThenBy(c => c.X).ToList();
    }

    /// <summary>Everything reachable from a cell across neighbours carrying the
    /// same letter - the fill, and it fills what is drawn rather than what is
    /// meant: two cells that read the same are the same cell to a bucket.
    /// </summary>
    public IReadOnlyList<Vector2I> Region(Vector2I from)
    {
        var seen = new HashSet<Vector2I>();
        if (!Inside(from))
            return Array.Empty<Vector2I>();
        char want = LetterAt(from);
        var queue = new Queue<Vector2I>();
        seen.Add(from);
        queue.Enqueue(from);
        while (queue.Count > 0)
        {
            Vector2I at = queue.Dequeue();
            foreach (int heading in HexField.EdgeHeadings)
            {
                Vector2I to = HexField.Step(at, heading);
                if (Inside(to) && LetterAt(to) == want && seen.Add(to))
                    queue.Enqueue(to);
            }
        }
        return seen.OrderBy(c => c.Y).ThenBy(c => c.X).ToList();
    }

    /// <summary>
    /// Run one write over a set of cells, collecting what it refused.
    ///
    /// <b>A refusal on one cell does not stop the stroke.</b> A brush dragged
    /// across a hillside meets cells the alphabet cannot carry, and stopping
    /// there would leave half a stroke and no way to tell which half; the ones
    /// that could take the paint take it, and the rest are reported.
    /// </summary>
    public IReadOnlyList<string> Sweep(IEnumerable<Vector2I> cells,
                                       Func<Vector2I, (bool, string)> write)
    {
        var refused = new List<string>();
        foreach (Vector2I cell in cells)
        {
            (bool ok, string why) = write(cell);
            if (!ok && why.Length > 0)
                refused.Add($"({cell.X},{cell.Y}): {why}");
        }
        return refused;
    }

    // --- pieces: copy, cut, paste, move ----------------------------------------

    /// <summary>One cell of a <see cref="Piece"/>: its letter, its ramp, its
    /// masonry and its rock's note, on the cell it was taken from. A stamp
    /// file keeps no notes (<see cref="MapFile.SaveStamp"/>), so a rock comes
    /// out of one a crag.</summary>
    public readonly record struct Bit(Vector2I Cell, char Letter, bool Ramp, Masonry? Wall,
                                      RockNote? Rock = null, char Under = BoardMap.Plain,
                                      char Soil = Soil.As);

    /// <summary>
    /// A part of a board lifted off it - the clipboard, a stamp, the selection
    /// being dragged (stage 2 of docs/editor-ux-plan.md).
    ///
    /// <b>Cells where they were taken, and the cell it is held by.</b> Put down
    /// elsewhere, every cell keeps its place relative to <see cref="Anchor"/>
    /// on the hex grid rather than on the offset one - see <see cref="Shift"/>.
    /// The homes on it carry their side and class said out loud, so a copied
    /// tank stays who it was whatever order it lands in.
    /// </summary>
    public sealed record Piece(IReadOnlyList<Bit> Cells, IReadOnlyList<Parking> Homes, Vector2I Anchor);

    /// <summary>
    /// Where <paramref name="cell"/> lands when the piece held at
    /// <paramref name="from"/> is put down at <paramref name="to"/>.
    ///
    /// <b>Through axial coordinates, not by adding the offset.</b> The grid is
    /// odd-q: an odd column stands half a row lower than an even one
    /// (<see cref="HexField.Step"/>), so a piece moved an odd number of
    /// columns by adding (dq, dr) would change which cells touch - the moat's
    /// problem (<see cref="BoardMap"/>.Moated). In axial terms a move is a
    /// plain sum and neighbours stay neighbours; the price is that a
    /// rectangle moved an odd number of columns lands as a slightly sheared
    /// one, which is what the same hexes look like there.
    /// </summary>
    public static Vector2I Shift(Vector2I cell, Vector2I from, Vector2I to) =>
        Offset(Axial(cell) - Axial(from) + Axial(to));

    private static Vector2I Axial(Vector2I c) => new(c.X, c.Y - (c.X - (c.X & 1)) / 2);

    private static Vector2I Offset(Vector2I a) => new(a.X, a.Y + (a.X - (a.X & 1)) / 2);

    /// <summary>The cells of the board from one corner to the other, columns
    /// and rows between them - the select tool's rectangle.</summary>
    public IReadOnlyList<Vector2I> Box(Vector2I a, Vector2I b)
    {
        var cells = new List<Vector2I>();
        for (int r = Math.Max(0, Math.Min(a.Y, b.Y)); r <= Math.Min(Rows - 1, Math.Max(a.Y, b.Y)); r++)
        for (int q = Math.Max(0, Math.Min(a.X, b.X)); q <= Math.Min(Columns - 1, Math.Max(a.X, b.X)); q++)
            cells.Add(new Vector2I(q, r));
        return cells;
    }

    /// <summary>
    /// The cells given, lifted into a piece held at <paramref name="anchor"/>.
    ///
    /// <b>Cells off the board are not in it</b>: put down, a piece leaves what
    /// is under its holes alone, so a selection round a bend of a river does
    /// not punch the board's edge into the next one.
    /// </summary>
    public Piece Copy(IEnumerable<Vector2I> cells, Vector2I anchor)
    {
        var bits = new List<Bit>();
        var taken = new HashSet<Vector2I>();
        foreach (Vector2I cell in cells)
        {
            if (!OnBoard(cell) || !taken.Add(cell))
                continue;
            bits.Add(new Bit(cell, LetterAt(cell), IsRamp(cell),
                             _walls.TryGetValue(cell, out Masonry? wall) ? wall.Copy() : null,
                             RockAt(cell), _under[At(cell)], _soil[At(cell)]));
        }
        var homes = new List<Parking>();
        foreach (Lineup.Unit u in Lineup.Of(_homes))
        {
            Parking home = _homes[u.Slot];
            if (!taken.Contains(home.Cell))
                continue;
            // said out loud, as Freeze would say it, without touching the board
            int pair = Lineup.PairAt(u.Model);
            homes.Add(pair >= 0 ? home with { Side = u.Side, Class = Lineup.Pairs[pair].Class } : home);
        }
        return new Piece(bits, homes, anchor);
    }

    /// <summary>
    /// The cells given cleared to plain ground at the datum: no cover, no
    /// ramp, no wall - and with <paramref name="homes"/>, no home. Cut's
    /// second half and Delete. Answers how many cells changed.
    ///
    /// <b>The last home goes too.</b> <see cref="DropHome"/> refuses it, for
    /// a brush; a cut is usually half of a move whose paste brings it back,
    /// and a board left with none says so in the problems panel and on save.
    /// </summary>
    public int Clear(IEnumerable<Vector2I> cells, bool homes = true)
    {
        var cleared = new HashSet<Vector2I>();
        foreach (Vector2I cell in cells)
        {
            if (!OnBoard(cell) || !cleared.Add(cell))
                continue;
            _ground[At(cell)] = BoardMap.Plain;
            _ramps[At(cell)] = false;
            Sync(cell);
        }
        if (homes && _homes.Any(h => cleared.Contains(h.Cell)))
        {
            Freeze();
            _homes.RemoveAll(h => cleared.Contains(h.Cell));
        }
        return cleared.Count;
    }

    /// <summary>
    /// A piece put down with its anchor on <paramref name="at"/>; with
    /// <paramref name="homes"/>, its tanks too, over any home already on the
    /// cell. Answers the cells written and, in <paramref name="off"/>, how many
    /// fell off the grid.
    ///
    /// <b>The letters go in as they are.</b> Every one came off a board, so
    /// each is a letter the alphabet carries and nothing is asked again; the
    /// ramps and walls come with them, the masonry copied so that the piece
    /// can be put down twice.
    /// </summary>
    public int Paste(Piece piece, Vector2I at, bool homes, out int off)
    {
        off = 0;
        int written = 0;
        foreach (Bit bit in piece.Cells)
        {
            Vector2I to = Shift(bit.Cell, piece.Anchor, at);
            if (!Inside(to))
            {
                off++;
                continue;
            }
            _ground[At(to)] = bit.Letter;
            _under[At(to)] = bit.Under;
            _soil[At(to)] = bit.Soil;
            _ramps[At(to)] = bit.Ramp;
            _walls.Remove(to);
            if (bit.Wall is { } wall && GlyphAt(to).Wall)
                _walls[to] = wall.Copy();
            _rocks.Remove(to);
            if (bit.Rock is { } note)
                _rocks[to] = note;
            Sync(to);
            written++;
        }
        if (homes && piece.Homes.Count > 0)
        {
            Freeze();
            foreach (Parking home in piece.Homes)
            {
                Vector2I to = Shift(home.Cell, piece.Anchor, at);
                if (!OnBoard(to))
                    continue;
                int here = SlotAt(to);
                if (here >= 0)
                    _homes[here] = home with { Cell = to };
                else
                    _homes.Add(home with { Cell = to });
            }
        }
        return written;
    }

    // --- the board itself: size, shift, crop ------------------------------------

    /// <summary>The largest board the editor makes, either way - the new-board
    /// dialog's bound.</summary>
    public const int Largest = 64;

    /// <summary>The smallest.</summary>
    public const int Smallest = 4;

    /// <summary>
    /// The board made <paramref name="columns"/> by <paramref name="rows"/>,
    /// what was on cell (q, r) now on (q + dq, r + dr): the one move behind
    /// resizing, shifting and cropping (stage 4 of docs/editor-ux-plan.md).
    /// New cells are plain ground on the board; cells pushed past the edge are
    /// gone, and so are the homes and walls on them. Answers how many homes
    /// were lost, or a refusal.
    ///
    /// <b>The column shift is even, always.</b> An odd column stands half a
    /// row lower (odd-q); moved an odd number of columns, every cell would sit
    /// beside other cells than it did - the moat's problem
    /// (<see cref="BoardMap"/>.Moated). Callers round; this refuses.
    ///
    /// <b>A board left with no home is refused</b>: every save would refuse it
    /// after, and the way to keep one is to move it first.
    /// </summary>
    public bool Reframe(int columns, int rows, int dq, int dr, out int lost, out string why)
    {
        lost = 0;
        why = "";
        if (columns < Smallest || rows < Smallest || columns > Largest || rows > Largest)
        {
            why = $"a board is {Smallest} to {Largest} cells each way, and this would be {columns} x {rows}";
            return false;
        }
        if ((dq & 1) != 0)
        {
            why = $"a shift of {dq} columns would move every odd column half a row against the even ones - shift by an even number";
            return false;
        }
        Vector2I by = new(dq, dr);
        bool Fits(Vector2I cell) => cell.X >= 0 && cell.X < columns && cell.Y >= 0 && cell.Y < rows;
        List<Parking> homes = _homes.Where(h => Fits(h.Cell + by)).Select(h => h with { Cell = h.Cell + by }).ToList();
        if (homes.Count == 0)
        {
            why = "no home would be left on the board - move one inside first";
            return false;
        }
        Freeze();
        homes = _homes.Where(h => Fits(h.Cell + by)).Select(h => h with { Cell = h.Cell + by }).ToList();
        lost = _homes.Count - homes.Count;

        var ground = Enumerable.Repeat(BoardMap.Plain, columns * rows).ToArray();
        var under = Enumerable.Repeat(BoardMap.Plain, columns * rows).ToArray();
        var soil = Enumerable.Repeat(Soil.As, columns * rows).ToArray();
        var ramps = new bool[columns * rows];
        var walls = new Dictionary<Vector2I, Masonry>();
        var rocks = new Dictionary<Vector2I, RockNote>();
        for (int r = 0; r < Rows; r++)
        for (int q = 0; q < Columns; q++)
        {
            var from = new Vector2I(q, r);
            Vector2I to = from + by;
            if (!Fits(to))
                continue;
            ground[to.Y * columns + to.X] = _ground[At(from)];
            under[to.Y * columns + to.X] = _under[At(from)];
            soil[to.Y * columns + to.X] = _soil[At(from)];
            ramps[to.Y * columns + to.X] = _ramps[At(from)];
            if (_walls.TryGetValue(from, out Masonry? wall))
                walls[to] = wall;
            if (_rocks.TryGetValue(from, out RockNote? note))
                rocks[to] = note;
        }
        (Columns, Rows, _ground, _under, _soil, _ramps, _homes, _walls, _rocks) =
            (columns, rows, ground, under, soil, ramps, homes, walls, rocks);
        return true;
    }

    /// <summary>The shift that keeps a board where <paramref name="anchor"/>
    /// says when it grows or shrinks by <paramref name="grow"/>: 0 at the
    /// start, all of it at the end, half in the middle - for the columns,
    /// rounded to even (<see cref="Reframe"/>).</summary>
    public static int Anchored(int grow, int anchor, bool columns)
    {
        int by = anchor switch { 0 => 0, 2 => grow, _ => grow / 2 };
        return columns ? by - (by & 1) : by;
    }

    /// <summary>
    /// A board straight from its grids, with no question asked but whether
    /// each letter is one - the autosave's reader. A draft is usually illegal
    /// (that is why <see cref="MapRules"/> returns a list), so it cannot come
    /// back through <see cref="BoardMap.FromGround"/>, which would refuse it.
    /// </summary>
    public static MapEdit Raw(string name, string[] ground, string[] ramps, IReadOnlyList<Parking> homes,
                              IReadOnlyDictionary<Vector2I, Masonry>? walls, string? paint, bool height, int plinth,
                              IReadOnlyDictionary<Vector2I, RockNote>? rocking = null,
                              RockClimate climate = default, string[]? floors = null,
                              IReadOnlyDictionary<Foundation, char>? soilBase = null,
                              string[]? soilCells = null)
    {
        int rows = ground.Length, columns = rows > 0 ? ground[0].Length : 0;
        if (columns < 1 || ground.Any(row => row.Length != columns))
            throw new InvalidOperationException($"{name}: the ground grid is not a rectangle");
        var edit = new MapEdit
        {
            Name = name, Paint = paint, Height = height, Plinth = plinth,
            Columns = columns, Rows = rows,
            _ground = new char[columns * rows],
            _under = Enumerable.Repeat(BoardMap.Plain, columns * rows).ToArray(),
            _soil = Enumerable.Repeat(Soil.As, columns * rows).ToArray(),
            SoilBase = soilBase is null ? new() : new Dictionary<Foundation, char>(soilBase),
            _ramps = new bool[columns * rows],
            _homes = homes.ToList(),
            Climate = climate,
        };
        for (int r = 0; r < rows; r++)
        for (int q = 0; q < columns; q++)
        {
            char letter = ground[r][q];
            if (MapFile.Of(letter) is null)
                throw new InvalidOperationException($"{name}: ({q},{r}) is '{letter}', which no map letter is");
            var cell = new Vector2I(q, r);
            edit._ground[edit.At(cell)] = letter;
            // a mark the draft cannot hold is dropped by Sync, as a stray
            // masonry entry is: a draft may be wrong, not unwritable
            if (floors is not null && r < floors.Length && q < floors[r].Length
                && MapFile.FloorUnder(floors[r][q]) is not null)
                edit._under[edit.At(cell)] = floors[r][q];
            if (soilCells is not null && r < soilCells.Length && q < soilCells[r].Length)
                edit._soil[edit.At(cell)] = soilCells[r][q];
            edit._ramps[edit.At(cell)] = r < ramps.Length && q < ramps[r].Length && ramps[r][q] == 'r';
            if (walls is not null && walls.TryGetValue(cell, out Masonry? wall) && edit.GlyphAt(cell).Wall)
                edit._walls[cell] = wall.Copy();
            edit.Sync(cell);
        }
        edit.Spread(rocking);
        return edit;
    }

    // --- symmetry ---------------------------------------------------------------

    /// <summary>How a board for two sides is drawn twice by one brush (stage 3
    /// of docs/editor-ux-plan.md): turned half round its middle, or mirrored
    /// left to right, or top to bottom.</summary>
    public enum Symmetry
    {
        None,
        /// <summary>Half a turn about the board's middle - two sides that face
        /// each other across it, each with the other's ground.</summary>
        Point,
        /// <summary>Left and right swap; up stays up.</summary>
        Across,
        /// <summary>Top and bottom swap; left stays left.</summary>
        Down,
    }

    /// <summary>
    /// The cell a cell's twin is, or null when it has none on the grid.
    ///
    /// <b>In doubled rows, so the arithmetic is the hexes' and not the
    /// array's.</b> An odd column stands half a row lower (odd-q), so a cell's
    /// height on the board is <c>2r + (q &amp; 1)</c> half-rows. A reflection is
    /// a grid symmetry only when it sends every column to one of the same
    /// parity (a mirror across a column) or flips the parity together with the
    /// half-row (half a turn) - so the centre is put where that holds, as near
    /// the board's middle as it can go:
    /// <list type="bullet">
    /// <item><b>Point</b> on an even width turns about the edge between the two
    /// middle columns and pairs every cell; on an odd width it turns about the
    /// middle column, and that column's neighbours each leave their last row
    /// without a twin.</item>
    /// <item><b>Across</b> mirrors about a column: the middle one on an odd
    /// width; on an even width the one left of the middle, and the last
    /// column has no twin.</item>
    /// <item><b>Down</b> mirrors about a row of the even columns: every odd
    /// column leaves its last row without a twin, on any size.</item>
    /// </list>
    /// The naive (W-1-q, H-1-r) is exact only for the first case, and on an odd
    /// width it parts neighbours.
    /// </summary>
    public Vector2I? Twin(Vector2I cell, Symmetry how)
    {
        int y2 = 2 * cell.Y + (cell.X & 1);
        int q, y;
        switch (how)
        {
            case Symmetry.Point:
                q = Columns - 1 - cell.X;
                y = ((Columns - 1) & 1) == 1 ? 2 * Rows - 1 - y2 : 2 * Rows - 2 - y2;
                break;
            case Symmetry.Across:
                q = (((Columns - 1) & 1) == 0 ? Columns - 1 : Columns - 2) - cell.X;
                y = y2;
                break;
            case Symmetry.Down:
                q = cell.X;
                y = 2 * Rows - 2 - y2;
                break;
            default:
                return null;
        }
        if (q < 0 || q >= Columns || y < 0)
            return null;
        var twin = new Vector2I(q, (y - (q & 1)) / 2);
        return Inside(twin) ? twin : null;
    }

    /// <summary>An edge heading as the twin sees it: half a turn, or the
    /// mirror's flip (headings count anticlockwise from the right, 90 up).</summary>
    public static int Turned(int heading, Symmetry how) => how switch
    {
        Symmetry.Point => Mathf.PosMod(heading + 180, 360),
        Symmetry.Across => Mathf.PosMod(180 - heading, 360),
        Symmetry.Down => Mathf.PosMod(360 - heading, 360),
        _ => heading,
    };

    /// <summary>The other side.</summary>
    public static string Opposite(string side) =>
        string.Equals(side, Parking.Brown, StringComparison.OrdinalIgnoreCase) ? Parking.Green : Parking.Brown;

    /// <summary>
    /// The home on <paramref name="twin"/> made the twin of the one on
    /// <paramref name="of"/>: the other side, the same class, the facing
    /// turned - and left unsaid when the turn lands on the side's own way
    /// across, as it does for half a turn and the top-bottom mirror.
    /// </summary>
    public bool Pair(Vector2I of, Vector2I twin, Symmetry how, out string why)
    {
        int from = SlotAt(of);
        if (from < 0)
        {
            why = $"no tank on ({of.X},{of.Y})";
            return false;
        }
        Freeze();
        Parking home = _homes[from];
        string side = Opposite(home.Side ?? Parking.Green);
        if (!Unit(twin, side, home.Class ?? "MTP", out why))
            return false;
        int facing = Turned(home.Facing ?? Parking.Way(home.Side ?? Parking.Green), how);
        return Face(SlotAt(twin), facing == Parking.Way(side) ? null : facing, out why);
    }

    /// <summary>What a board says about one symmetry.</summary>
    public readonly record struct Asymmetry(int Cells, Vector2I? First, int Lone, Vector2I? FirstLone,
                                            int Homes, Vector2I? FirstHome);

    /// <summary>
    /// Where the board breaks <paramref name="how"/>: cells whose twin carries
    /// another letter or ramp (both of each pair counted), cells on the board
    /// with no twin on the grid, and homes without a tank of the other side
    /// on their twin. Masonry's edges are not compared; its letter is.
    /// </summary>
    public Asymmetry Audit(Symmetry how)
    {
        int cells = 0, lone = 0, homes = 0;
        Vector2I? first = null, firstLone = null, firstHome = null;
        for (int r = 0; r < Rows; r++)
        for (int q = 0; q < Columns; q++)
        {
            var cell = new Vector2I(q, r);
            if (Twin(cell, how) is not Vector2I twin)
            {
                if (OnBoard(cell))
                {
                    lone++;
                    firstLone ??= cell;
                }
                continue;
            }
            if (LetterAt(twin) != LetterAt(cell) || IsRamp(twin) != IsRamp(cell))
            {
                cells++;
                first ??= cell;
            }
        }
        string[] sides = Parking.SidesOf(_homes);
        for (int i = 0; i < _homes.Count; i++)
        {
            Vector2I cell = _homes[i].Cell;
            int other = Twin(cell, how) is Vector2I twin ? SlotAt(twin) : -1;
            if (other < 0 || other == i || sides[other] == sides[i])
            {
                homes++;
                firstHome ??= cell;
            }
        }
        return new Asymmetry(cells, first, lone, firstLone, homes, firstHome);
    }

    // --- out ------------------------------------------------------------------

    /// <summary>
    /// The draft as the rules see it.
    ///
    /// <b>Built from the letters rather than kept alongside them.</b> A second
    /// set of arrays updated by every brush is the thing this file exists to not
    /// have; at three hundred cells the walk is nothing, and it cannot disagree
    /// with what is drawn.
    /// </summary>
    public MapRules.Draft Draft()
    {
        var draft = new MapRules.Draft
        {
            Name = Name,
            Columns = Columns,
            Rows = Rows,
            Levels = new int[Columns * Rows],
            Ramps = (bool[])_ramps.Clone(),
            Water = new bool[Columns * Rows],
            Ground = new Foundation[Columns * Rows],
            Cliffs = new bool[Columns * Rows],
            Homes = _homes.Select(h => h.Cell).ToList(),
            Walling = _walls.ToDictionary(kv => kv.Key, kv => kv.Value.Copy()),
        };
        var plot = new HashSet<Vector2I>();
        for (int r = 0; r < Rows; r++)
        for (int q = 0; q < Columns; q++)
        {
            var cell = new Vector2I(q, r);
            int at = At(cell);
            MapFile.Glyph glyph = GlyphAt(cell);
            if (glyph.Letter == BoardMap.Off)
                continue;
            plot.Add(cell);
            draft.Levels[at] = glyph.Level + Plinth;
            draft.Water[at] = glyph.Water;
            draft.Ground[at] = glyph.Floor;
            draft.Cliffs[at] = glyph.Cliff;
        }
        draft.Plot = plot;
        return draft;
    }

    /// <summary>Everything wrong with the draft that can be asked without a
    /// field. Reachability is not in it - see <see cref="MapRules.Faults"/>,
    /// which says why.</summary>
    public IReadOnlyList<MapRules.Fault> Faults() => MapRules.Faults(Draft());

    /// <summary>
    /// The board this draft describes, or a throw naming what is wrong with it.
    ///
    /// <b>Through <see cref="BoardMap.FromGround"/> like everything else.</b>
    /// The editor gets the same board the file would, guards and all, so a draft
    /// that builds here is a draft that loads from disk.
    /// </summary>
    public BoardMap Build()
    {
        MapFile.MustHaveHomes(_homes, Name);
        return BoardMap.FromGround(Name, Ground(), Ramps(), _homes.ToList(),
                                   Paint ?? MapFile.PaintFor(Ground()), Height,
                                   Plinth,
                                   _walls.ToDictionary(kv => kv.Key,
                                                       kv => kv.Value.Copy()),
                                   Rocking(), Climate, Floors(),
                                   SoilBase.Count > 0 ? SoilBase : null, SoilRows());
    }

    /// <summary>Write the draft to <c>maps/</c> and return the path.
    /// <b>It builds first</b>, so a file that cannot be loaded cannot be saved:
    /// the folder is judged by the self test, and a board that refuses in it
    /// would break every scene rather than the one being drawn.</summary>
    public string Save(string? path = null) => MapFile.Save(Build(), path);
}
