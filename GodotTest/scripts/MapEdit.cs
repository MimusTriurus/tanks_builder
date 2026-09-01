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
    private bool[] _ramps = Array.Empty<bool>();
    private List<Vector2I> _homes = new();

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

    public IReadOnlyList<Vector2I> Homes => _homes;

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
            _ramps = new bool[columns * rows],
        };
        // One home, in the middle. A board with none is refused on the way out -
        // the reachability audit has nothing to say about it - and starting with
        // that refusal standing would be a new board that is broken before it is
        // touched.
        edit._homes.Add(new Vector2I(columns / 2, rows - 1));
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
        var edit = new MapEdit
        {
            Name = map.Name,
            Paint = map.Paint,
            Height = map.Height,
            Plinth = map.Plinth,
            Columns = map.Columns,
            Rows = map.Rows,
            _ground = new char[map.Columns * map.Rows],
            _ramps = (bool[])map.Ramps.Clone(),
            _homes = map.Homes.ToList(),
            // Copied rather than referenced: the board a draft was opened from
            // is somebody else's, and a cached BoardMap.Abbey edited in place
            // would be every scene in the session drawing the edit.
            _walls = map.Walling.ToDictionary(kv => kv.Key,
                                              kv => kv.Value.Copy()),
        };
        for (int r = 0; r < map.Rows; r++)
        for (int q = 0; q < map.Columns; q++)
        {
            var cell = new Vector2I(q, r);
            edit._ground[edit.At(cell)] = ground[r][q];
            // And the entry every wall letter means, for the cells the board
            // said nothing about. A board that authored no shapes - which is
            // every compiled one - would otherwise open with masonry the letters
            // declare and the panel cannot see, and the first save would write
            // the same board back without it.
            edit.Sync(cell);
        }
        return edit;
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
        MapFile.Of(LetterAt(cell))
        ?? MapFile.Of(BoardMap.Off)!.Value;

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
    private readonly record struct Snap(char[] Ground, bool[] Ramps,
                                        List<Vector2I> Homes,
                                        Dictionary<Vector2I, Masonry> Walls);

    private Snap Taken() => new((char[])_ground.Clone(),
                                (bool[])_ramps.Clone(), _homes.ToList(),
                                _walls.ToDictionary(kv => kv.Key,
                                                    kv => kv.Value.Copy()));

    private void Restore(Snap was) =>
        (_ground, _ramps, _homes, _walls) =
            (was.Ground, was.Ramps, was.Homes, was.Walls);

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
        char? letter = MapFile.Letter(floor, over, level, water, cliff);
        if (letter is not char found)
        {
            why = $"the alphabet has no letter for {Say(floor, over, level, water)}"
                  + $" - see MapFile.Alphabet, which is not a cross product";
            return false;
        }
        _ground[At(cell)] = found;
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
        if (GlyphAt(cell).Wall)
            _walls.TryAdd(cell, new Masonry());
        else
            _walls.Remove(cell);
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
            case Foundation.Deep:
                why = "a floor made of water has no letter - water on a map is "
                      + "the flood letter, which is low ground with water "
                      + "standing in it";
                return false;
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
        return wet
            ? Put(cell, Foundation.Solid, Cover.None, -1, true, false, out why)
            : Put(cell, was.Floor, was.Over, was.Level, false, was.Cliff,
                  out why);
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
        if (!on && _homes.Contains(cell))
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
        _ => Ramp(cell, false, out why) && Level(cell, 0, out why),
    };

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
            _homes.Add(cell);
        else
            _homes[slot] = cell;
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
            Homes = _homes.ToList(),
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
                                                       kv => kv.Value.Copy()));
    }

    /// <summary>Write the draft to <c>maps/</c> and return the path.
    /// <b>It builds first</b>, so a file that cannot be loaded cannot be saved:
    /// the folder is judged by the self test, and a board that refuses in it
    /// would break every scene rather than the one being drawn.</summary>
    public string Save(string? path = null) => MapFile.Save(Build(), path);
}
