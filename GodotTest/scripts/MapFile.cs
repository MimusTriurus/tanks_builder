using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// A board written to and read from <c>maps/*.json</c>, so that authoring one
/// does not mean editing C# and rebuilding.
///
/// <b>JSON as the container and the picture inside it.</b> The grid is less
/// legible in JSON than as plain text - quotes and commas around a drawing - so
/// the rows stay arrays of strings in the same alphabet <see cref="BoardMap"/>
/// has always used, and what the container buys is the rest: the size, the
/// homes, the paint and a format version, none of which a text file could carry
/// without inventing a header. The house style is already this - see
/// <c>terrain.json</c>, <c>walls.json</c>, <c>panel.json</c>, down to the
/// <c>"_"</c> block for comments.
///
/// <b>One decoder, and it is <see cref="BoardMap.FromGround"/>.</b> This parses
/// the file and hands the two grids over; the letters, the plot, the cliffs, the
/// kinds and the guards are all where they were. A second decoder beside it is
/// the failure this project keeps naming - two places that agree until somebody
/// edits one - and the self test's own note says what a check against a second
/// decoder of the same grid is worth.
///
/// <b>Read from an absolute path past the importer</b>, the reason the atlases
/// and the settings files are: edit, restart, see it, nothing to rebuild.
///
/// <b>A broken map file is fatal, and that is the opposite of the settings
/// files.</b> <see cref="WallConfig"/> falls back to compiled numbers because a
/// wall with no entry is still a wall; a board that did not parse is not a board
/// at all, and coming up as the bench instead would be the harness silently
/// showing a different map from the one named on the command line.
/// </summary>
public static class MapFile
{
    /// <summary>The format the reader speaks. Written into every file, checked
    /// on the way in: a file from a later format is refused by name rather than
    /// read as far as it happens to parse.</summary>
    public const int Format = 1;

    public const string Extension = ".json";

    /// <summary>Where the maps live - beside the project, like the settings
    /// files.</summary>
    public static string Dir =>
        Path.Combine(ProjectSettings.GlobalizePath("res://"), "maps");

    // --- the folder ----------------------------------------------------------

    /// <summary>
    /// The names on disk, lowercased, in reading order.
    ///
    /// Not cached: the editor writes into this folder while the session is
    /// running, and a list settled at startup would be a folder that stops
    /// answering the moment somebody saves.
    /// </summary>
    public static IReadOnlyList<string> Names()
    {
        if (!Directory.Exists(Dir))
            return Array.Empty<string>();
        return Directory.GetFiles(Dir, "*" + Extension)
            .Select(f => Path.GetFileNameWithoutExtension(f).ToLowerInvariant())
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Names on disk that a compiled map already answers to.
    ///
    /// <b>Refused rather than resolved, and the refusal is the point.</b>
    /// <c>maps/abbey.json</c> beside the compiled <c>Abbey</c> is two references
    /// under one name, and one of them is what the self test judges. Whichever
    /// way the tie were broken, half the session would be looking at a board the
    /// other half was judging.
    /// </summary>
    public static IReadOnlyList<string> Clashes(IEnumerable<string> compiled)
    {
        var known = new HashSet<string>(compiled, StringComparer.OrdinalIgnoreCase);
        return Names().Where(known.Contains).ToList();
    }

    public static string PathOf(string name) =>
        Path.Combine(Dir, name.Trim().ToLowerInvariant() + Extension);

    public static bool Has(string name) => File.Exists(PathOf(name));

    // --- reading -------------------------------------------------------------

    public static BoardMap Load(string name) =>
        Read(PathOf(name), name.Trim().ToLowerInvariant());

    public static BoardMap Read(string path, string? name = null)
    {
        if (!File.Exists(path))
            throw new InvalidOperationException($"no map file at {path}");
        return Parse(File.ReadAllText(path),
                     name ?? Path.GetFileNameWithoutExtension(path)
                                 .ToLowerInvariant());
    }

    /// <summary>
    /// The map a file's text describes, or a throw naming what is wrong with it.
    ///
    /// <b>The name comes from the filename and not from the file.</b> A map is
    /// asked for by the name it is saved under, so a <c>"name"</c> inside that
    /// disagreed would be a board that answers to one name and calls itself
    /// another. The field is still written - a file read on its own has to say
    /// what it is - and it is checked rather than used.
    /// </summary>
    public static BoardMap Parse(string text, string name)
    {
        Shape? file;
        try
        {
            file = JsonSerializer.Deserialize<Shape>(text, new JsonSerializerOptions
            {
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        }
        catch (JsonException e)
        {
            throw new InvalidOperationException($"{name}: {e.Message}");
        }

        if (file is null)
            throw new InvalidOperationException($"{name}: the file is empty");
        if (file.Format != Format)
            throw new InvalidOperationException(
                $"{name}: this reader speaks format {Format} and the file says "
                + $"{file.Format}");
        if (file.Ground is null || file.Ground.Length == 0)
            throw new InvalidOperationException($"{name}: no ground grid");
        if (file.Ramps is null)
            throw new InvalidOperationException(
                $"{name}: no ramp grid - a board with no ramps writes one of "
                + "dots, because a missing grid and a board of flat tops are "
                + "different statements");
        if (file.Name is { Length: > 0 } said
            && !said.Equals(name, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"{name}: the file calls itself {said}, and a map is asked for "
                + "by the name it is saved under");

        // The size is checked against the grids rather than believed. A ragged
        // row still throws inside BoardMap.Read, which is where that rule lives
        // and says why; what this catches is the other typo - a size field that
        // stopped agreeing with the drawing under it.
        if (file.Size is { Length: 2 })
        {
            int rows = file.Ground.Length;
            int columns = file.Ground[0]?.Length ?? 0;
            if (file.Size[0] != columns || file.Size[1] != rows)
                throw new InvalidOperationException(
                    $"{name}: size says {file.Size[0]} x {file.Size[1]} and the "
                    + $"ground grid is {columns} x {rows}");
        }

        var homes = new List<Vector2I>();
        foreach (int[] home in file.Homes ?? Array.Empty<int[]>())
        {
            if (home is not { Length: 2 })
                throw new InvalidOperationException(
                    $"{name}: a home is a pair of numbers, and one of them has "
                    + $"{home?.Length ?? 0}");
            homes.Add(new Vector2I(home[0], home[1]));
        }
        MustHaveHomes(homes, name);

        string paint = file.Paint is { Length: > 0 }
            ? file.Paint
            : PaintFor(file.Ground);

        return BoardMap.FromGround(name, file.Ground, file.Ramps, homes, paint,
                                   file.Height ?? true, file.Plinth ?? 0);
    }

    /// <summary>
    /// A board with no homes is refused.
    ///
    /// Here rather than at each caller because both of them - the file and the
    /// editor - are asking the same question about a finished board, and the
    /// reason is one sentence: the reachability audit is the check that catches
    /// a broken map, and it has nothing to say about a board nothing can be
    /// parked on.
    /// </summary>
    public static void MustHaveHomes(IReadOnlyList<Vector2I> homes, string name)
    {
        if (homes.Count == 0)
            throw new InvalidOperationException(
                $"{name}: no homes - a board nothing can be parked on is a board "
                + "the reachability audit has nothing to say about");
    }

    /// <summary>
    /// The plate a board comes up on when it does not say.
    ///
    /// <b>Derived rather than defaulted, and that distinction cost every tree on
    /// a map once.</b> <see cref="TerrainSet.Default"/> is itself a named plate,
    /// and a named plate sits ABOVE the map's own kinds - so a board whose
    /// letters authored woods and rises came up with 992 props and not one tree
    /// among them. See <see cref="BoardMap.Paint"/>.
    /// </summary>
    public static string PaintFor(IReadOnlyList<string> ground) =>
        ground.Any(row => row.Any(
            c => c is BoardMap.Wood or BoardMap.WoodedHill or BoardMap.WoodedLow
                   or BoardMap.Hill or BoardMap.Rock))
            ? TerrainSet.Mixed
            : TerrainSet.Default;

    // --- writing -------------------------------------------------------------

    /// <summary>
    /// One letter per cell, back out of a built board.
    ///
    /// <b>The one place a second encoding of the alphabet exists, and the round
    /// trip is what pays for it.</b> There is no way to write a file from a map
    /// without it, and a self test that writes and reads back names the cell
    /// where the two disagree.
    ///
    /// <b>Heights outside the alphabet are refused rather than flattened.</b>
    /// The letters reach -1, 0, +1 and +2-as-a-rock; a cell standing anywhere
    /// else cannot be written, and writing the nearest letter would be a board
    /// silently changing shape on the way to disk.
    /// </summary>
    public static string[] Ground(BoardMap map)
    {
        var rows = new string[map.Rows];
        for (int r = 0; r < map.Rows; r++)
        {
            var row = new StringBuilder(map.Columns);
            for (int q = 0; q < map.Columns; q++)
            {
                var cell = new Vector2I(q, r);
                row.Append(Letter(map, cell));
            }
            rows[r] = row.ToString();
        }
        return rows;
    }

    private static char Letter(BoardMap map, Vector2I cell)
    {
        if (!map.OnBoard(cell))
            return BoardMap.Off;
        int at = map.At(cell);

        // Relative to the plinth, because that is what the letters were written
        // against: on a board standing a level up, plain ground is at level 1
        // and writing its height out would come back as a hill. See
        // BoardMap.Plinth, which exists because the round trip found this.
        char? letter = Letter(map.Ground[at], map.Over[at],
                              map.Levels[at] - map.Plinth,
                              map.Water[at], map.Cliffs[at]);
        if (letter is char found)
            return found;
        throw new InvalidOperationException(
            $"{map.Name}: ({cell.X},{cell.Y}) is "
            + $"{map.Ground[at]}/{map.Over[at]} at level "
            + $"{map.Levels[at] - map.Plinth}, and the alphabet has no letter "
            + "for that - so this cell cannot be written without changing what "
            + "it is");
    }

    // --- the alphabet --------------------------------------------------------

    /// <summary>
    /// What one letter of a map says about a cell.
    ///
    /// <b>The alphabet is not a cross product, and this table is where that
    /// shows.</b> Thirteen letters cover four axes - floor, cover, height and
    /// flood - so most combinations of them have no letter at all: sand only
    /// lies at the datum, a trench never stands on a rise, a rock is a floor and
    /// a height and a declaration at once. That is a property of the map format
    /// rather than a gap in this table, and what the editor needs from it is the
    /// refusal: a brush that would make an unwritable cell has to say so instead
    /// of leaving a board that cannot be saved.
    ///
    /// <b>The reader of record is still <see cref="BoardMap.FromGround"/>.</b>
    /// This is the second copy - the one the writing side and the editor need -
    /// and the self test holds the two against each other letter by letter.
    /// </summary>
    public readonly struct Glyph
    {
        public Glyph(char letter, Foundation floor, Cover over, int level,
                     bool water = false, bool cliff = false, bool wall = false,
                     bool wood = false)
        {
            Letter = letter;
            Floor = floor;
            Over = over;
            Level = level;
            Water = water;
            Cliff = cliff;
            Wall = wall;
            Wood = wood;
        }

        public char Letter { get; }
        public Foundation Floor { get; }
        public Cover Over { get; }

        /// <summary>Relative to the board's plinth, like the letter itself.
        /// </summary>
        public int Level { get; }

        public bool Water { get; }
        public bool Cliff { get; }
        public bool Wall { get; }

        /// <summary>Whether the letter also paints the cell as woodland. Carried
        /// because <see cref="Cover.Forest"/> and the kind are two statements the
        /// same letter makes - see <see cref="BoardMap.Wood"/>.</summary>
        public bool Wood { get; }
    }

    /// <summary>Every letter a map may carry, in the order
    /// <see cref="BoardMap"/> declares them.</summary>
    public static IReadOnlyList<Glyph> Alphabet { get; } = new[]
    {
        new Glyph(BoardMap.Off, Foundation.Solid, Cover.None, 0),
        new Glyph(BoardMap.Plain, Foundation.Solid, Cover.None, 0),
        new Glyph(BoardMap.Wood, Foundation.Solid, Cover.Forest, 0, wood: true),
        new Glyph(BoardMap.Hill, Foundation.Solid, Cover.None, 1),
        new Glyph(BoardMap.WoodedHill, Foundation.Solid, Cover.Forest, 1,
                  wood: true),
        new Glyph(BoardMap.Low, Foundation.Solid, Cover.None, -1),
        new Glyph(BoardMap.WoodedLow, Foundation.Solid, Cover.Forest, -1,
                  wood: true),
        new Glyph(BoardMap.Wet, Foundation.Solid, Cover.None, -1, water: true),
        new Glyph(BoardMap.Rock, Foundation.Rock, Cover.None, 2, cliff: true),
        new Glyph(BoardMap.Wall, Foundation.Solid, Cover.Walls, 0, wall: true),
        new Glyph(BoardMap.Sand, Foundation.Sand, Cover.None, 0),
        new Glyph(BoardMap.Trench, Foundation.Solid, Cover.Trench, 0),
        new Glyph(BoardMap.Mine, Foundation.Solid, Cover.Minefield, 0),
    };

    /// <summary>What a letter says, or null when nothing does.</summary>
    public static Glyph? Of(char letter)
    {
        foreach (Glyph glyph in Alphabet)
            if (glyph.Letter == letter)
                return glyph;
        return null;
    }

    /// <summary>
    /// The letter for a cell of those four axes, or null when the alphabet has
    /// none.
    ///
    /// <b>Off the board wins over everything and is not in here</b>: it is the
    /// edge of the world rather than a kind of cell, so the caller that knows
    /// whether a cell is on the plot is the one that writes that letter.
    /// </summary>
    public static char? Letter(Foundation floor, Cover over, int level,
                               bool water = false, bool cliff = false)
    {
        foreach (Glyph glyph in Alphabet)
        {
            if (glyph.Letter == BoardMap.Off)
                continue;
            if (glyph.Floor == floor && glyph.Over == over
                && glyph.Level == level && glyph.Water == water
                && glyph.Cliff == cliff)
                return glyph.Letter;
        }
        return null;
    }

    public static string[] Ramps(BoardMap map)
    {
        var rows = new string[map.Rows];
        for (int r = 0; r < map.Rows; r++)
        {
            var row = new StringBuilder(map.Columns);
            for (int q = 0; q < map.Columns; q++)
                row.Append(map.Ramps[r * map.Columns + q] ? 'r' : '.');
            rows[r] = row.ToString();
        }
        return rows;
    }

    /// <summary>
    /// The file's text.
    ///
    /// <b>Written by hand rather than serialised.</b> A serialiser puts every
    /// row of the grid on its own indented line inside an array and the drawing
    /// survives; what it does not survive is the field order, and the field order
    /// is what makes the file readable as a map - name and size first, the
    /// picture last and unbroken.
    /// </summary>
    public static string Write(BoardMap map)
    {
        string[] ground = Ground(map);
        string[] ramps = Ramps(map);
        var text = new StringBuilder();
        text.Append("{\n");
        text.Append($"  \"format\": {Format},\n");
        text.Append($"  \"name\": {Quote(map.Name)},\n");
        text.Append($"  \"size\": [{map.Columns}, {map.Rows}],\n");
        text.Append($"  \"paint\": {Quote(map.Paint)},\n");
        text.Append($"  \"height\": {(map.Height ? "true" : "false")},\n");
        if (map.Plinth != 0)
            text.Append($"  \"plinth\": {map.Plinth},\n");
        text.Append("  \"homes\": ["
                    + string.Join(", ", map.Homes.Select(h => $"[{h.X}, {h.Y}]"))
                    + "],\n");
        text.Append("\n");
        text.Append("  \"ground\": [\n");
        text.Append(string.Join(",\n", ground.Select(r => "    " + Quote(r))));
        text.Append("\n  ],\n");
        text.Append("  \"ramps\": [\n");
        text.Append(string.Join(",\n", ramps.Select(r => "    " + Quote(r))));
        text.Append("\n  ]\n");
        text.Append("}\n");
        return text.ToString();
    }

    /// <summary>Write a board to <c>maps/</c> and return the path it went to.
    /// The folder is made if it is not there: the first save on a fresh checkout
    /// is the one that would otherwise fail for a reason that is not about the
    /// map.</summary>
    public static string Save(BoardMap map, string? path = null)
    {
        // The clash is refused here as well as reported by Clashes, and this is
        // the half that matters: a board opened from the compiled ABBEY and
        // saved keeps its name, so the ordinary way to end up with two
        // references under one name is to press save - and by the time ByName
        // refuses, the file is already on disk and every scene is broken.
        if (path is null
            && BoardMap.Compiled.Contains(map.Name,
                                          StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"{map.Name} is a board this build already writes, and two "
                + "references under one name is what the self test judges one "
                + "of - save it under another name");
        path ??= PathOf(map.Name);
        string? folder = Path.GetDirectoryName(path);
        if (folder is { Length: > 0 })
            Directory.CreateDirectory(folder);
        File.WriteAllText(path, Write(map));
        return path;
    }

    private static string Quote(string raw) =>
        "\"" + raw.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    // --- file shape ----------------------------------------------------------

    private sealed class Shape
    {
        [JsonPropertyName("format")] public int Format { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("size")] public int[]? Size { get; set; }
        [JsonPropertyName("paint")] public string? Paint { get; set; }
        [JsonPropertyName("height")] public bool? Height { get; set; }
        [JsonPropertyName("plinth")] public int? Plinth { get; set; }
        [JsonPropertyName("homes")] public int[][]? Homes { get; set; }
        [JsonPropertyName("ground")] public string[]? Ground { get; set; }
        [JsonPropertyName("ramps")] public string[]? Ramps { get; set; }
    }
}
