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
    /// <summary>
    /// The format the writer writes. Written into every file, checked on the way
    /// in: a file from a later format is refused by name rather than read as far
    /// as it happens to parse.
    ///
    /// <b>2 is 1 with mud in the alphabet</b> (<see cref="BoardMap.Mud"/>, the
    /// editor's stage 4, docs/editor3d-plan.md) - and a capon the editor lays
    /// rather than one typed into the walls block, which the block could always
    /// say. Every letter of 1 means in 2 what it meant, so a format-1 file is
    /// read as written (<see cref="Oldest"/>); what is refused is a file that
    /// says 1 and carries a letter 1 never had - mislabelled, and a build that
    /// speaks only 1 would have stopped on that letter with no word about why.
    /// The point of the bump is that build: it now refuses a file by its number.
    ///
    /// <b>3 is 2 with room for the look of the board</b> - rocks, the floor
    /// under a cover, soil, roads, tree species (docs/map-format3-plan.md) -
    /// and the last bump those take. All their blocks are named at once in
    /// <see cref="Shape"/>, a key no block answers to is refused rather than
    /// skipped (<see cref="Reading"/>), and a block this build does not read yet
    /// is refused by its name (<see cref="Unread"/>). A build that knows rocks
    /// and not roads therefore stops on the roads, instead of showing the board
    /// without them; the blocks can land in any order and none bumps again.
    /// </summary>
    public const int Format = 3;

    /// <summary>The oldest format still read - the ones between it and
    /// <see cref="Format"/> are taken up by <see cref="Upgrade"/>.</summary>
    public const int Oldest = 1;

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
            file = JsonSerializer.Deserialize<Shape>(text, Reading);
        }
        catch (JsonException e)
        {
            throw new InvalidOperationException($"{name}: {Said(e)}");
        }

        if (file is null)
            throw new InvalidOperationException($"{name}: the file is empty");
        if (file.Format < Oldest || file.Format > Format)
            throw new InvalidOperationException(
                $"{name}: this reader speaks formats {Oldest} to {Format} and the "
                + $"file says {file.Format}");
        if (file.Ground is null || file.Ground.Length == 0)
            throw new InvalidOperationException($"{name}: no ground grid");
        Upgrade(file, name);
        Unread(file, name);
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

        IReadOnlyList<Parking> homes = Parkings(file.Homes, name);
        MustHaveHomes(homes, name);

        string paint = file.Paint is { Length: > 0 }
            ? file.Paint
            : PaintFor(file.Ground);

        return BoardMap.FromGround(name, file.Ground, file.Ramps, homes, paint,
                                   file.Height ?? true, file.Plinth ?? 0,
                                   Walling(file.Walls, name),
                                   Rocking(file.Rocks, name), Climate(file.Climate, name),
                                   file.Floor, SoilBase(file.Soil, name), file.Soil?.Cells,
                                   Paths(file.Roads, name));
    }

    /// <summary>
    /// The roads block (format 3, Ф6): a type and a path a road. What the
    /// path crosses and how it steps is the map's to judge
    /// (<see cref="BoardMap.FromGround"/>, <see cref="Road.Faults"/>); here a
    /// type nothing answers to, or a cell that is not a pair, is refused.
    /// </summary>
    private static IReadOnlyList<RoadPath>? Paths(RoadShape[]? said, string name)
    {
        if (said is null || said.Length == 0)
            return null;
        var roads = new List<RoadPath>();
        foreach (RoadShape shape in said)
        {
            RoadType type = shape.Type is { } t
                ? Road.Named(t) ?? throw new InvalidOperationException(
                    $"{name}: a road is \"{t}\", and the roads are " + string.Join(", ", Road.Names))
                : throw new InvalidOperationException($"{name}: a road says no type");
            var cells = new List<Vector2I>();
            foreach (int[] at in shape.Path ?? Array.Empty<int[]>())
            {
                if (at is not { Length: 2 })
                    throw new InvalidOperationException(
                        $"{name}: a {Road.Say(type)} road's cell is a pair of numbers");
                cells.Add(new Vector2I(at[0], at[1]));
            }
            roads.Add(new RoadPath(type, cells));
        }
        return roads;
    }

    /// <summary>
    /// The soil block's <c>base</c> (format 3, Ф4): a look a floor, by the
    /// floor's key. That a look lies on its floor is the map's to judge
    /// (<see cref="BoardMap.FromGround"/>); here a key or a letter that is no
    /// word at all is refused.
    /// </summary>
    private static IReadOnlyDictionary<Foundation, char>? SoilBase(SoilShape? said, string name)
    {
        if (said?.Base is not { Count: > 0 } laid)
            return null;
        var looks = new Dictionary<Foundation, char>();
        foreach ((string key, string look) in laid)
        {
            int k = Array.FindIndex(Soil.Floors, f => f.Key == key);
            if (k < 0)
                throw new InvalidOperationException(
                    $"{name}: the soil's base names \"{key}\", and its floors are "
                    + string.Join(", ", Soil.Floors.Select(f => f.Key)));
            if (look.Length != 1)
                throw new InvalidOperationException(
                    $"{name}: the soil's base lays \"{look}\" on {key}, and a look is one letter");
            looks[Soil.Floors[k].Floor] = look[0];
        }
        return looks;
    }

    /// <summary>
    /// The rocks block (format 3, docs/rocks-plan.md С3): a note a rock, at
    /// any of its cells. Which cell is a rock and that no rock has two notes is
    /// the map's to judge (<see cref="BoardMap.FromGround"/>), as for the
    /// walls; here a name or a figure that means nothing is refused rather
    /// than read as a crag.
    /// </summary>
    private static IReadOnlyDictionary<Vector2I, RockNote>? Rocking(RockShape[]? said, string name)
    {
        if (said is null || said.Length == 0)
            return null;
        var rocking = new Dictionary<Vector2I, RockNote>();
        foreach (RockShape shape in said)
        {
            if (shape.At is not { Length: 2 })
                throw new InvalidOperationException(
                    $"{name}: a rock's at is a pair of numbers, and one of them "
                    + $"has {shape.At?.Length ?? 0}");
            var cell = new Vector2I(shape.At[0], shape.At[1]);
            if (rocking.ContainsKey(cell))
                throw new InvalidOperationException(
                    $"{name}: ({cell.X},{cell.Y}) has two rock notes, and the "
                    + "second would win silently");
            RockType? type = null;
            if (shape.Type is { } t)
                type = RockNote.TypeNamed(t) ?? throw new InvalidOperationException(
                    $"{name}: ({cell.X},{cell.Y}) is a \"{t}\", and a rock is a "
                    + "crag, a mesa, a yardang or a moutonnee");
            string? palette = null;
            if (shape.Palette is { } p)
                palette = RockPalette.Named(p.ToLowerInvariant())?.Name
                          ?? throw new InvalidOperationException(
                              $"{name}: ({cell.X},{cell.Y}) wears \"{p}\", and the "
                              + "palettes are " + string.Join(", ", RockPalette.Names));
            if (shape.Preset is int preset && (preset < 1 || preset > RockNote.Presets))
                throw new InvalidOperationException(
                    $"{name}: ({cell.X},{cell.Y}) asks for preset {preset}, and a "
                    + $"rock has 1 to {RockNote.Presets}");
            rocking[cell] = new RockNote
            {
                Type = type, Palette = palette, Preset = shape.Preset, Seed = shape.Seed,
            };
        }
        return rocking;
    }

    /// <summary>The climate block: two headings, each said or silent.</summary>
    private static RockClimate Climate(ClimateShape? said, string name)
    {
        if (said is null)
            return default;
        foreach ((string what, float? deg) in new[] { ("wind", said.Wind), ("ice", said.Ice) })
            if (deg is float d && (d < -360.0f || d > 360.0f || float.IsNaN(d)))
                throw new InvalidOperationException(
                    $"{name}: the {what} runs at {d}, and a heading is -360 to 360 degrees");
        return new RockClimate(said.Wind, said.Ice);
    }

    /// <summary>
    /// An older file brought to <see cref="Format"/>, a step at a time, in
    /// place. A step is where a letter changes meaning or a field moves; one
    /// that changes nothing still checks the file keeps to its own format.
    /// </summary>
    private static void Upgrade(Shape file, string name)
    {
        if (file.Format == 1)
        {
            // 1 -> 2: nothing moves. A 1 that carries mud is not a 1.
            for (int r = 0; r < file.Ground!.Length; r++)
            {
                int q = file.Ground[r]?.IndexOf(BoardMap.Mud) ?? -1;
                if (q >= 0)
                    throw new InvalidOperationException(
                        $"{name}: ({q},{r}) is '{BoardMap.Mud}', mud, which came in "
                        + "format 2 and the file says 1");
            }
            file.Format = 2;
        }
        if (file.Format == 2)
        {
            // 2 -> 3: nothing moves. A 2 that carries a block of 3 is not a 2.
            if (Blocks(file).FirstOrDefault(b => b.Said) is { Key: { } key })
                throw new InvalidOperationException(
                    $"{name}: \"{key}\" came in format 3 and the file says 2");
            file.Format = 3;
        }
    }

    /// <summary>
    /// How every map, draft and stamp is read.
    ///
    /// <b>A key no field answers to is refused, and that is what format 3 is
    /// for.</b> The serializer's default skips it, so a build that does not
    /// know a block would read the board without it and nobody would see the
    /// difference - a road missing from the board is not an error anywhere
    /// else. The refusal names the key.
    /// </summary>
    private static readonly JsonSerializerOptions Reading = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>What the serializer refused, in the file's words: a stray key
    /// by its path (<c>$.roadz</c>), anything else as the serializer says
    /// it.</summary>
    private static string Said(JsonException e) =>
        e.Message.Contains("could not be mapped") && e.Path is { Length: > 0 } at
            ? $"{at}: no such field in a map file"
            : e.Message;

    /// <summary>The blocks format 3 brought, by the key the file writes:
    /// whether this file said each, and whether this build reads it.</summary>
    private static IEnumerable<(string Key, bool Said, bool Read)> Blocks(Shape file)
    {
        yield return ("floor", file.Floor is not null, true);
        yield return ("climate", file.Climate is not null, true);
        yield return ("rocks", file.Rocks is not null, true);
        yield return ("soil", file.Soil is not null, true);
        yield return ("roads", file.Roads is not null, true);
        yield return ("wood", file.Wood is not null, false);
        yield return ("trees", file.Trees is not null, false);
    }

    /// <summary>
    /// A block of format 3 this build does not read yet, refused by name.
    ///
    /// Each comes off this list in the stage that reads it
    /// (docs/map-format3-plan.md, Ф2-Ф6): <c>climate</c> and <c>rocks</c>
    /// came off in Ф2. Until then a file that says one is a board this build
    /// would show wrong, so it is not shown at all.
    /// </summary>
    private static void Unread(Shape file, string name)
    {
        if (Blocks(file).FirstOrDefault(b => b.Said && !b.Read) is { Key: { } key })
            throw new InvalidOperationException(
                $"{name}: \"{key}\" is a block of format 3 this build does not read yet");
    }

    /// <summary>
    /// The masonry block, cell by cell.
    ///
    /// <b>Beside the picture rather than in it, because the alphabet is one
    /// letter per cell and this is six flags and four numbers.</b> The letter
    /// still says which cells carry wall - a cell named here and not drawn as
    /// one is refused in <see cref="BoardMap.FromGround"/> - so the drawing
    /// stays the map and the block is an annotation on it.
    ///
    /// <b>What is absent is silence and not a default</b>, which is
    /// <see cref="WallConfig"/>'s rule over the same four numbers: a wall that
    /// did not name its courses takes whatever the wall being laid uses. What is
    /// present and out of range is refused rather than clamped, and that is
    /// where this differs from the settings file - a settings file is edited
    /// live beside a running bench, and a map is the board itself, so a figure
    /// that means nothing has to be found when the file is read rather than
    /// when a wall comes up two bricks tall.
    /// </summary>
    private static IReadOnlyDictionary<Vector2I, Masonry>? Walling(
        WallShape[]? said, string name)
    {
        if (said is null || said.Length == 0)
            return null;
        var walling = new Dictionary<Vector2I, Masonry>();
        foreach (WallShape shape in said)
        {
            if (shape.At is not { Length: 2 })
                throw new InvalidOperationException(
                    $"{name}: a wall's at is a pair of numbers, and one of them "
                    + $"has {shape.At?.Length ?? 0}");
            var cell = new Vector2I(shape.At[0], shape.At[1]);
            if (walling.ContainsKey(cell))
                throw new InvalidOperationException(
                    $"{name}: ({cell.X},{cell.Y}) has two wall entries, and the "
                    + "second would win silently");

            var wall = new Masonry();
            if (shape.Kind is { } kind)
            {
                wall.Kind = kind.ToLowerInvariant() switch
                {
                    "wall" => Masonry.Sort.Wall,
                    "capon" => Masonry.Sort.Capon,
                    _ => throw new InvalidOperationException(
                        $"{name}: ({cell.X},{cell.Y}) is a \"{kind}\", and a "
                        + "walled cell carries a wall or a capon"),
                };
            }
            if (shape.Facing is int facing)
            {
                if (!Masonry.Headings.Contains(facing))
                    throw new InvalidOperationException(
                        $"{name}: ({cell.X},{cell.Y}) faces {facing}, and a "
                        + "capon's slit faces an edge: "
                        + string.Join(", ", Masonry.Headings));
                if (wall.Kind != Masonry.Sort.Capon)
                    throw new InvalidOperationException(
                        $"{name}: ({cell.X},{cell.Y}) has a facing and is not a "
                        + "capon; a wall's front is its edges'");
                wall.Facing = facing;
            }
            if (shape.Edges is not null && wall.Kind == Masonry.Sort.Capon)
                throw new InvalidOperationException(
                    $"{name}: ({cell.X},{cell.Y}) is a capon and lists edges; a "
                    + "capon stands on every edge but its gate, which its facing "
                    + "says");
            if (shape.Edges is not null)
            {
                if (shape.Edges.Length == 0)
                    throw new InvalidOperationException(
                        $"{name}: ({cell.X},{cell.Y}) lists no edges - masonry "
                        + "standing on none of them is a breach rather than a "
                        + "wall, so take the letter off instead");
                wall.Edges = 0;
                foreach (int heading in shape.Edges)
                {
                    if (!Masonry.Headings.Contains(heading))
                        throw new InvalidOperationException(
                            $"{name}: ({cell.X},{cell.Y}) walls the heading "
                            + $"{heading}, and a flat-topped hex has its edges "
                            + "at " + string.Join(", ", Masonry.Headings));
                    wall.Edges |= 1 << Masonry.Bit(heading);
                }
            }
            foreach (Masonry.Dial dial in Masonry.Dials)
            {
                int? asked = dial switch
                {
                    Masonry.Dial.Seed => shape.Seed,
                    Masonry.Dial.Columns => shape.Columns,
                    Masonry.Dial.Courses => shape.Courses,
                    _ => shape.Leaves,
                };
                if (asked is not int figure)
                    continue;
                (int low, int high) = Masonry.Range(dial);
                if (figure < low || figure > high)
                    throw new InvalidOperationException(
                        $"{name}: ({cell.X},{cell.Y}) asks for {figure} "
                        + $"{dial.ToString().ToLowerInvariant()}, and a wall "
                        + $"takes {low} to {high}");
                wall.Set(dial, figure);
            }
            walling[cell] = wall;
        }
        return walling;
    }

    /// <summary>
    /// The parkings a file declares, in either of the two shapes it may write
    /// them.
    ///
    /// <b>A bare pair and an object mean the same thing, and both stay.</b>
    /// <c>[8, 16]</c> is a parking that names nobody - which is what every board
    /// wrote before a class could be asked for - and
    /// <c>{ "at": [8, 16], "class": "HTP" }</c> is one that does. Reading only
    /// the object would break every file on disk; writing only the object would
    /// rewrite every board the first time one was saved, which is a diff nobody
    /// asked for over a field nobody set.
    /// </summary>
    private static IReadOnlyList<Parking> Parkings(JsonElement[]? said,
                                                   string name)
    {
        var homes = new List<Parking>();
        foreach (JsonElement home in said ?? Array.Empty<JsonElement>())
        {
            if (home.ValueKind == JsonValueKind.Array)
            {
                homes.Add(new Parking(Pair(home, name)));
                continue;
            }
            if (home.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException(
                    $"{name}: a home is a pair of numbers or an object with an "
                    + $"\"at\" in it, and this is {home.ValueKind}");
            if (!home.TryGetProperty("at", out JsonElement at))
                throw new InvalidOperationException(
                    $"{name}: a home written as an object needs an \"at\"");
            // read by hand, so Reading's refusal of a stray key is said here
            foreach (JsonProperty field in home.EnumerateObject())
                if (field.Name is not ("at" or "class" or "side" or "facing"))
                    throw new InvalidOperationException(
                        $"{name}: a home has no field \"{field.Name}\"");
            string? crew = home.TryGetProperty("class", out JsonElement said_)
                           && said_.ValueKind == JsonValueKind.String
                ? said_.GetString() : null;
            // a match's side and the edge it starts facing; the values are
            // judged with the class, in BoardMap.MustCrew
            string? side = home.TryGetProperty("side", out JsonElement team)
                           && team.ValueKind == JsonValueKind.String
                ? team.GetString()?.ToLowerInvariant() : null;
            int? facing = null;
            if (home.TryGetProperty("facing", out JsonElement face))
            {
                if (face.ValueKind != JsonValueKind.Number || !face.TryGetInt32(out int f))
                    throw new InvalidOperationException(
                        $"{name}: a home's \"facing\" is an edge heading, a number, "
                        + $"and this is {face.ValueKind}");
                facing = f;
            }
            homes.Add(new Parking(Pair(at, name),
                                  crew is { Length: > 0 } ? crew : null,
                                  side is { Length: > 0 } ? side : null,
                                  facing));
        }
        return homes;
    }

    private static Vector2I Pair(JsonElement pair, string name)
    {
        if (pair.ValueKind != JsonValueKind.Array
            || pair.GetArrayLength() != 2)
            throw new InvalidOperationException(
                $"{name}: a home is a pair of numbers, and one of them has "
                + (pair.ValueKind == JsonValueKind.Array
                    ? pair.GetArrayLength().ToString() : pair.ValueKind.ToString()));
        return new Vector2I(pair[0].GetInt32(), pair[1].GetInt32());
    }

    /// <summary>One home as the file writes it: the bare pair when it said
    /// nothing else - see <see cref="Parkings"/>, where both shapes are argued -
    /// else an object with only the fields it said.</summary>
    private static string Home(Parking p)
    {
        if (p.Plain)
            return $"[{p.Cell.X}, {p.Cell.Y}]";
        var said = new List<string> { $"\"at\": [{p.Cell.X}, {p.Cell.Y}]" };
        if (p.Class is { Length: > 0 } tag)
            said.Add($"\"class\": {Quote(tag)}");
        if (p.Side is { Length: > 0 } side)
            said.Add($"\"side\": {Quote(side)}");
        if (p.Facing is int facing)
            said.Add($"\"facing\": {facing}");
        return "{ " + string.Join(", ", said) + " }";
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
    public static void MustHaveHomes(IReadOnlyList<Parking> homes, string name)
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
                   or BoardMap.Hill or BoardMap.Rock or BoardMap.WallHill))
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
        // the floor grid says what this letter cannot (Encode, Floors)
        // the floor under any road: a road is the roads block's, not a letter
        if (Encode(map.UnpavedAt(at), map.Over[at], map.Levels[at] - map.Plinth,
                   map.Water[at], map.Cliffs[at]) is { } found)
            return found.Letter;
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
        new Glyph(BoardMap.DeepWet, Foundation.Deep, Cover.None, -1,
                  water: true),
        new Glyph(BoardMap.Rock, Foundation.Rock, Cover.None, 2, cliff: true),
        new Glyph(BoardMap.Wall, Foundation.Solid, Cover.Walls, 0, wall: true),
        new Glyph(BoardMap.WallHill, Foundation.Solid, Cover.Walls, 1,
                  wall: true),
        new Glyph(BoardMap.Sand, Foundation.Sand, Cover.None, 0),
        new Glyph(BoardMap.Mud, Foundation.Mud, Cover.None, 0),
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

    // --- the floor under a cover --------------------------------------------

    /// <summary>
    /// A cell as a letter and, where the letter cannot say its floor, the
    /// format-3 <c>floor</c> grid's mark - or null where neither can
    /// (docs/map-format3-plan.md, Ф3).
    ///
    /// <b>One way to say each cell.</b> A cell the alphabet has a letter for
    /// takes it and leaves the floor grid a dot - plain sand is <c>s</c> in the
    /// ground grid, as it always was. Only a cell the alphabet has no letter
    /// for - sand or mud under a wood, a trench, a mine, on a hill or in a
    /// hollow - is written as the solid letter with <c>s</c> or <c>u</c> under
    /// it. Never under water, a rock or a wall: there the floor is said.
    /// </summary>
    public static (char Letter, char Under)? Encode(Foundation floor, Cover over, int level,
                                                    bool water = false, bool cliff = false)
    {
        if (Letter(floor, over, level, water, cliff) is char own)
            return (own, BoardMap.Plain);
        if (UnderOf(floor) is char under && !water && !cliff && over != Cover.Walls
            && Letter(Foundation.Solid, over, level) is char solid)
            return (solid, under);
        return null;
    }

    /// <summary>The floor grid's mark for a floor that may lie under a
    /// letter: the floor's own letter, so the grid reads as the picture it
    /// is.</summary>
    private static char? UnderOf(Foundation floor) => floor switch
    {
        Foundation.Sand => BoardMap.Sand,
        Foundation.Mud => BoardMap.Mud,
        _ => null,
    };

    /// <summary>What a floor-grid mark lays, or null for one it does not
    /// know.</summary>
    public static Foundation? FloorUnder(char mark) => mark switch
    {
        BoardMap.Sand => Foundation.Sand,
        BoardMap.Mud => Foundation.Mud,
        _ => null,
    };

    /// <summary>Whether a letter leaves its floor for the floor grid to say:
    /// solid ground with something on it or off the datum - a wood, a trench,
    /// a mine, a hill, a hollow - and no water, rock or wall.</summary>
    public static bool TakesUnder(char letter) =>
        letter != BoardMap.Plain && Of(letter) is { Floor: Foundation.Solid, Water: false,
                                                    Cliff: false, Wall: false };

    /// <summary>A letter's glyph with the floor the grid laid under it.</summary>
    public static Glyph Under(Glyph glyph, char mark) =>
        FloorUnder(mark) is Foundation floor
            ? new Glyph(glyph.Letter, floor, glyph.Over, glyph.Level, glyph.Water,
                        glyph.Cliff, glyph.Wall, glyph.Wood)
            : glyph;

    /// <summary>The floor grid of a board, or null when no cell needs one -
    /// the file then writes no block, as format 2 did.</summary>
    public static string[]? Floors(BoardMap map)
    {
        var rows = new string[map.Rows];
        bool any = false;
        for (int r = 0; r < map.Rows; r++)
        {
            var row = new StringBuilder(map.Columns);
            for (int q = 0; q < map.Columns; q++)
            {
                var cell = new Vector2I(q, r);
                char mark = BoardMap.Plain;
                if (map.OnBoard(cell))
                {
                    int at = map.At(cell);
                    mark = Encode(map.UnpavedAt(at), map.Over[at], map.Levels[at] - map.Plinth,
                                  map.Water[at], map.Cliffs[at])?.Under ?? BoardMap.Plain;
                }
                any |= mark != BoardMap.Plain;
                row.Append(mark);
            }
            rows[r] = row.ToString();
        }
        return any ? rows : null;
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
        // A parking that named nobody is written as the bare pair it always
        // was - see Parkings, where both shapes are argued.
        text.Append("  \"homes\": ["
                    + string.Join(", ", map.Parked.Select(Home))
                    + "],\n");
        text.Append("\n");
        text.Append("  \"ground\": [\n");
        text.Append(string.Join(",\n", ground.Select(r => "    " + Quote(r))));
        text.Append("\n  ],\n");
        text.Append("  \"ramps\": [\n");
        text.Append(string.Join(",\n", ramps.Select(r => "    " + Quote(r))));
        text.Append("\n  ]");
        Grid(text, "floor", Floors(map));
        // The masonry last, under the picture it annotates, and only for the
        // cells that said something - see Masonry.Plain. A line per walled cell
        // saying the ring its letter already says would be a block that grows
        // with the board rather than with the authoring.
        var said = map.Walled()
            .Where(c => map.Walling.TryGetValue(c, out Masonry? w) && !w.Plain)
            .ToList();
        if (said.Count > 0)
        {
            text.Append(",\n\n  \"walls\": [\n");
            text.Append(string.Join(",\n",
                said.Select(c => "    " + Line(c, map.Walling[c]))));
            text.Append("\n  ]");
        }
        Look(text, map.Rocking, map.Climate, map.SoilBase,
             map.SoilCells is { } cells ? Rows(cells, map.Columns, map.Rows) : null);
        Paved(text, map.Roads);
        text.Append("\n}\n");
        return text.ToString();
    }

    /// <summary>
    /// Format 3's look blocks under the picture and the masonry, each only
    /// when it said something: a board that never named a rock or a wind
    /// writes as it did in format 2, bar the number.
    /// </summary>
    private static void Look(StringBuilder text, IReadOnlyDictionary<Vector2I, RockNote> rocking,
                             RockClimate climate, IReadOnlyDictionary<Foundation, char> soilBase,
                             string[]? soilCells)
    {
        Soiled(text, soilBase, soilCells);
        Rocked(text, rocking, climate);
    }

    /// <summary>The roads block, a road a line, in the order drawn: a fork
    /// after the road it leaves.</summary>
    private static void Paved(StringBuilder text, IReadOnlyList<RoadPath> roads)
    {
        if (roads.Count == 0)
            return;
        text.Append(",\n\n  \"roads\": [\n" + string.Join(",\n", roads.Select(r =>
            $"    {{ \"type\": {Quote(Road.Say(r.Type))}, \"path\": ["
            + string.Join(", ", r.Cells.Select(c => $"[{c.X}, {c.Y}]")) + "] }")) + "\n  ]");
    }

    /// <summary>A board's grid of one char a cell as rows, or null when every
    /// cell is <see cref="Soil.As"/>.</summary>
    public static string[]? Rows(char[] cells, int columns, int rows)
    {
        if (Array.TrueForAll(cells, c => c == Soil.As))
            return null;
        var said = new string[rows];
        for (int r = 0; r < rows; r++)
            said[r] = new string(cells, r * columns, columns);
        return said;
    }

    /// <summary>The soil block: the base on one line, the cells as a grid.</summary>
    private static void Soiled(StringBuilder text, IReadOnlyDictionary<Foundation, char> laid,
                               string[]? cells)
    {
        if (laid.Count == 0 && cells is null)
            return;
        var parts = new List<string>();
        if (laid.Count > 0)
            parts.Add("    \"base\": { " + string.Join(", ", Soil.Floors
                .Where(f => laid.ContainsKey(f.Floor))
                .Select(f => $"\"{f.Key}\": \"{laid[f.Floor]}\"")) + " }");
        if (cells is not null)
            parts.Add("    \"cells\": [\n"
                      + string.Join(",\n", cells.Select(r => "      " + Quote(r))) + "\n    ]");
        text.Append(",\n\n  \"soil\": {\n" + string.Join(",\n", parts) + "\n  }");
    }

    private static void Rocked(StringBuilder text, IReadOnlyDictionary<Vector2I, RockNote> rocking,
                               RockClimate climate)
    {
        if (!climate.Plain)
        {
            var parts = new List<string>();
            if (climate.SaidWind is float wind)
                parts.Add($"\"wind\": {Number(wind)}");
            if (climate.SaidIce is float ice)
                parts.Add($"\"ice\": {Number(ice)}");
            text.Append(",\n\n  \"climate\": { " + string.Join(", ", parts) + " }");
        }
        var notes = rocking.Where(kv => !kv.Value.Plain)
                           .OrderBy(kv => kv.Key.Y).ThenBy(kv => kv.Key.X).ToList();
        if (notes.Count > 0)
            text.Append(",\n\n  \"rocks\": [\n"
                        + string.Join(",\n", notes.Select(kv => "    " + Line(kv.Key, kv.Value)))
                        + "\n  ]");
    }

    /// <summary>A grid under the ramps, in the picture's own layout, when the
    /// board has one to say.</summary>
    private static void Grid(StringBuilder text, string key, string[]? rows)
    {
        if (rows is null)
            return;
        text.Append($",\n  \"{key}\": [\n");
        text.Append(string.Join(",\n", rows.Select(r => "    " + Quote(r))));
        text.Append("\n  ]");
    }

    private static string Number(float x) =>
        x.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>One rock's note on one line, only the fields it said.</summary>
    private static string Line(Vector2I cell, RockNote note)
    {
        var parts = new List<string> { $"\"at\": [{cell.X}, {cell.Y}]" };
        if (note.Type is { } type)
            parts.Add($"\"type\": {Quote(RockNote.Say(type))}");
        if (note.Palette is { } palette)
            parts.Add($"\"palette\": {Quote(palette)}");
        if (note.Preset is int preset)
            parts.Add($"\"preset\": {preset}");
        if (note.Seed is int seed)
            parts.Add($"\"seed\": {seed}");
        return "{ " + string.Join(", ", parts) + " }";
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
        // <b>Read back before it replaces anything</b> - ratel's level editor
        // does the same (Level3DIO.save_path). The text goes to a .part beside
        // the file, is parsed as any reader would parse it, and must write
        // itself out again letter for letter; only then does it take the
        // name. A writer and a reader that drifted apart cost a board somebody
        // drew, and the .part is where that is found rather than the next open.
        string text = Write(map);
        string part = path + ".part";
        File.WriteAllText(part, text);
        string? wrong = null;
        try
        {
            BoardMap back = Read(part, map.Name);
            if (Write(back) != text)
                wrong = "the file read back does not write the same text";
            else if (!Ground(back).SequenceEqual(Ground(map)) || !Ramps(back).SequenceEqual(Ramps(map)))
                wrong = "the file read back is another board";
        }
        catch (Exception e)
        {
            wrong = e.Message;
        }
        if (wrong is not null)
        {
            File.Delete(part);
            throw new InvalidOperationException(
                $"{map.Name}: not saved - {wrong}; the file on disk is as it was");
        }
        File.Move(part, path, overwrite: true);
        return path;
    }

    /// <summary>One wall on one line, because it is one statement about one
    /// cell and a serialiser would put its six edges on six.</summary>
    private static string Line(Vector2I cell, Masonry wall)
    {
        var parts = new List<string> { $"\"at\": [{cell.X}, {cell.Y}]" };
        if (wall.Kind == Masonry.Sort.Capon)
        {
            parts.Add("\"kind\": \"capon\"");
            if (wall.Facing is int facing)
                parts.Add($"\"facing\": {facing}");
            if (wall.Seed is int seed)
                parts.Add($"\"seed\": {seed}");
            return "{ " + string.Join(", ", parts) + " }";
        }
        if (wall.Edges != Masonry.Ring)
            parts.Add("\"edges\": [" + string.Join(", ", wall.Standing()) + "]");
        foreach (Masonry.Dial dial in Masonry.Dials)
            if (wall.Of(dial) is int figure)
                parts.Add($"\"{dial.ToString().ToLowerInvariant()}\": {figure}");
        return "{ " + string.Join(", ", parts) + " }";
    }

    private static string Quote(string raw) =>
        "\"" + raw.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    // --- drafts: the autosave ------------------------------------------------

    /// <summary>
    /// A board being drawn, as the file it would be - written from the letters,
    /// not from a built board, because a draft is usually illegal and
    /// <see cref="BoardMap.FromGround"/> would refuse it. The editor's
    /// autosave (stage 4 of docs/editor-ux-plan.md), kept in
    /// <c>user://autosave/</c> and never in <see cref="Dir"/>: a half-drawn
    /// board there would break every scene that reads the folder.
    /// </summary>
    public static string WriteDraft(MapEdit edit)
    {
        var text = new StringBuilder();
        text.Append("{\n");
        text.Append($"  \"format\": {Format},\n");
        text.Append($"  \"name\": {Quote(edit.Name)},\n");
        text.Append($"  \"size\": [{edit.Columns}, {edit.Rows}],\n");
        if (edit.Paint is { Length: > 0 } paint)
            text.Append($"  \"paint\": {Quote(paint)},\n");
        text.Append($"  \"height\": {(edit.Height ? "true" : "false")},\n");
        if (edit.Plinth != 0)
            text.Append($"  \"plinth\": {edit.Plinth},\n");
        text.Append("  \"homes\": [" + string.Join(", ", edit.Homes.Select(Home)) + "],\n\n");
        text.Append("  \"ground\": [\n");
        text.Append(string.Join(",\n", edit.Ground().Select(r => "    " + Quote(r))));
        text.Append("\n  ],\n");
        text.Append("  \"ramps\": [\n");
        text.Append(string.Join(",\n", edit.Ramps().Select(r => "    " + Quote(r))));
        text.Append("\n  ]");
        Grid(text, "floor", edit.Floors());
        var said = edit.Walled().Where(c => edit.MasonryAt(c) is { Plain: false }).ToList();
        if (said.Count > 0)
            text.Append(",\n\n  \"walls\": [\n"
                        + string.Join(",\n", said.Select(c => "    " + Line(c, edit.MasonryAt(c)!)))
                        + "\n  ]");
        Look(text, edit.Rocking(), edit.Climate, edit.SoilBase, edit.SoilRows());
        Paved(text, edit.Roads);
        text.Append("\n}\n");
        return text.ToString();
    }

    /// <summary>A draft back off the disk (<see cref="WriteDraft"/>), letters
    /// checked and nothing else.</summary>
    public static MapEdit ReadDraft(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        Shape? file;
        try
        {
            file = JsonSerializer.Deserialize<Shape>(File.ReadAllText(path), Reading);
        }
        catch (JsonException e)
        {
            throw new InvalidOperationException($"{name}: {Said(e)}");
        }
        if (file?.Ground is not { Length: > 0 } ground)
            throw new InvalidOperationException($"{name}: no ground grid");
        Unread(file, name);
        return MapEdit.Raw(file.Name is { Length: > 0 } said ? said : name, ground,
                           file.Ramps ?? Array.Empty<string>(), Parkings(file.Homes, name),
                           Walling(file.Walls, name), file.Paint is { Length: > 0 } p ? p : null,
                           file.Height ?? true, file.Plinth ?? 0,
                           Rocking(file.Rocks, name), Climate(file.Climate, name), file.Floor,
                           SoilBase(file.Soil, name), file.Soil?.Cells, Paths(file.Roads, name));
    }

    // --- stamps ----------------------------------------------------------------

    /// <summary>
    /// Where the editor's stamps live: <c>maps/stamps/</c>, beside the maps
    /// and out of their way - <see cref="Names"/> reads the top of
    /// <see cref="Dir"/> only, so no scene and no self test ever opens one
    /// as a board (stage 2 of docs/editor-ux-plan.md).
    /// </summary>
    public static string StampDir => Path.Combine(Dir, "stamps");

    public static IReadOnlyList<string> StampNames()
    {
        if (!Directory.Exists(StampDir))
            return Array.Empty<string>();
        return Directory.GetFiles(StampDir, "*" + Extension)
            .Select(f => Path.GetFileNameWithoutExtension(f).ToLowerInvariant())
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// A piece of a board as a file: the map's own shape - ground, ramps,
    /// homes, walls - cut to the piece's box, with <c>-</c> where the piece has
    /// no cell, plus the cell it is held by.
    ///
    /// <b>Moved to the box by an even number of columns</b>, so its odd
    /// columns stay odd and the drawing in the file is the hexes as they
    /// touched (<see cref="MapEdit.Shift"/>). <b>Not a board</b>: nothing here
    /// goes through <see cref="BoardMap.FromGround"/>, because a piece has no
    /// homes to audit and ramps whose neighbours it never took.
    /// </summary>
    public static string SaveStamp(string name, MapEdit.Piece piece)
    {
        if (piece.Cells.Count == 0)
            throw new InvalidOperationException("an empty piece is not a stamp");
        int left = piece.Cells.Min(b => b.Cell.X), top = piece.Cells.Min(b => b.Cell.Y);
        var by = new Vector2I(left - (left & 1), top);
        int columns = piece.Cells.Max(b => b.Cell.X) - by.X + 1;
        int rows = piece.Cells.Max(b => b.Cell.Y) - by.Y + 1;
        var ground = Enumerable.Range(0, rows).Select(_ => new StringBuilder(new string(BoardMap.Off, columns))).ToArray();
        var ramps = Enumerable.Range(0, rows).Select(_ => new StringBuilder(new string('.', columns))).ToArray();
        // the floor under a cover is the cell's rule, so a stamp keeps it
        var under = Enumerable.Range(0, rows).Select(_ => new StringBuilder(new string('.', columns))).ToArray();
        var walls = new List<string>();
        foreach (MapEdit.Bit bit in piece.Cells)
        {
            Vector2I at = bit.Cell - by;
            ground[at.Y][at.X] = bit.Letter;
            under[at.Y][at.X] = bit.Under;
            if (bit.Ramp)
                ramps[at.Y][at.X] = 'r';
            if (bit.Wall is { Plain: false } wall)
                walls.Add("    " + Line(at, wall));
        }
        Vector2I anchor = piece.Anchor - by;
        var text = new StringBuilder();
        text.Append("{\n");
        text.Append($"  \"format\": {Format},\n");
        text.Append($"  \"name\": {Quote(name)},\n");
        text.Append($"  \"size\": [{columns}, {rows}],\n");
        text.Append($"  \"anchor\": [{anchor.X}, {anchor.Y}],\n");
        text.Append("  \"homes\": ["
                    + string.Join(", ", piece.Homes.Select(h => Home(h with { Cell = h.Cell - by })))
                    + "],\n\n");
        text.Append("  \"ground\": [\n");
        text.Append(string.Join(",\n", ground.Select(r => "    " + Quote(r.ToString()))));
        text.Append("\n  ],\n");
        text.Append("  \"ramps\": [\n");
        text.Append(string.Join(",\n", ramps.Select(r => "    " + Quote(r.ToString()))));
        text.Append("\n  ]");
        Grid(text, "floor", piece.Cells.Any(b => b.Under != BoardMap.Plain)
            ? under.Select(r => r.ToString()).ToArray() : null);
        if (walls.Count > 0)
            text.Append(",\n\n  \"walls\": [\n" + string.Join(",\n", walls) + "\n  ]");
        text.Append("\n}\n");

        Directory.CreateDirectory(StampDir);
        string path = Path.Combine(StampDir, name + Extension);
        File.WriteAllText(path, text.ToString());
        // read back, as a map is, before the author is told it was kept
        LoadStamp(name);
        return path;
    }

    /// <summary>A stamp off the disk, held where it was saved held.</summary>
    public static MapEdit.Piece LoadStamp(string name)
    {
        string path = Path.Combine(StampDir, name.Trim().ToLowerInvariant() + Extension);
        if (!File.Exists(path))
            throw new InvalidOperationException($"no stamp at {path}");
        Shape? file;
        try
        {
            file = JsonSerializer.Deserialize<Shape>(File.ReadAllText(path), Reading);
        }
        catch (JsonException e)
        {
            throw new InvalidOperationException($"stamp {name}: {Said(e)}");
        }
        if (file?.Ground is not { Length: > 0 } ground)
            throw new InvalidOperationException($"stamp {name}: no ground grid");
        Unread(file, "stamp " + name);
        // a piece carries cells, not a board's look: a note in one would be
        // read and dropped
        if (file.Rocks is not null || file.Climate is not null || file.Soil is not null
            || file.Roads is not null)
            throw new InvalidOperationException(
                $"stamp {name}: a stamp carries no rocks, climate, soil or roads block");
        string[] ramps = file.Ramps ?? Array.Empty<string>();
        IReadOnlyDictionary<Vector2I, Masonry> walling =
            Walling(file.Walls, "stamp " + name) ?? new Dictionary<Vector2I, Masonry>();
        var bits = new List<MapEdit.Bit>();
        for (int r = 0; r < ground.Length; r++)
        for (int q = 0; q < ground[r].Length; q++)
        {
            char letter = ground[r][q];
            if (letter == BoardMap.Off)
                continue;
            if (Of(letter) is null)
                throw new InvalidOperationException(
                    $"stamp {name}: ({q},{r}) is '{letter}', which no map letter is");
            var cell = new Vector2I(q, r);
            bool ramp = r < ramps.Length && q < ramps[r].Length && ramps[r][q] == 'r';
            char mark = file.Floor is { } fl && r < fl.Length && q < fl[r].Length
                        && FloorUnder(fl[r][q]) is not null && TakesUnder(letter)
                ? fl[r][q] : BoardMap.Plain;
            bits.Add(new MapEdit.Bit(cell, letter, ramp,
                                     walling.TryGetValue(cell, out Masonry? wall) ? wall : null,
                                     Under: mark));
        }
        if (bits.Count == 0)
            throw new InvalidOperationException($"stamp {name}: not one cell on it");
        Vector2I anchor = file.Anchor is { Length: 2 } a ? new Vector2I(a[0], a[1]) : bits[0].Cell;
        return new MapEdit.Piece(bits, Parkings(file.Homes, "stamp " + name), anchor);
    }

    // --- file shape ----------------------------------------------------------

    private sealed class Shape
    {
        [JsonPropertyName("format")] public int Format { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("size")] public int[]? Size { get; set; }
        [JsonPropertyName("paint")] public string? Paint { get; set; }
        [JsonPropertyName("height")] public bool? Height { get; set; }
        [JsonPropertyName("plinth")] public int? Plinth { get; set; }
        [JsonPropertyName("homes")] public JsonElement[]? Homes { get; set; }
        [JsonPropertyName("ground")] public string[]? Ground { get; set; }
        [JsonPropertyName("ramps")] public string[]? Ramps { get; set; }
        [JsonPropertyName("walls")] public WallShape[]? Walls { get; set; }
        /// <summary>A stamp's only: the cell it is held by (SaveStamp).</summary>
        [JsonPropertyName("anchor")] public int[]? Anchor { get; set; }

        // format 3 (docs/map-format3-plan.md): named so that a file saying one
        // is refused by Unread rather than read without it; each gets its own
        // shape in the stage that reads it
        [JsonPropertyName("floor")] public string[]? Floor { get; set; }
        [JsonPropertyName("climate")] public ClimateShape? Climate { get; set; }
        [JsonPropertyName("rocks")] public RockShape[]? Rocks { get; set; }
        [JsonPropertyName("soil")] public SoilShape? Soil { get; set; }
        [JsonPropertyName("roads")] public RoadShape[]? Roads { get; set; }
        [JsonPropertyName("wood")] public JsonElement? Wood { get; set; }
        [JsonPropertyName("trees")] public JsonElement? Trees { get; set; }
    }

    private sealed class RockShape
    {
        [JsonPropertyName("at")] public int[]? At { get; set; }
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("palette")] public string? Palette { get; set; }
        [JsonPropertyName("preset")] public int? Preset { get; set; }
        [JsonPropertyName("seed")] public int? Seed { get; set; }
    }

    private sealed class RoadShape
    {
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("path")] public int[][]? Path { get; set; }
    }

    private sealed class SoilShape
    {
        [JsonPropertyName("base")] public Dictionary<string, string>? Base { get; set; }
        [JsonPropertyName("cells")] public string[]? Cells { get; set; }
    }

    private sealed class ClimateShape
    {
        [JsonPropertyName("wind")] public float? Wind { get; set; }
        [JsonPropertyName("ice")] public float? Ice { get; set; }
    }

    private sealed class WallShape
    {
        [JsonPropertyName("at")] public int[]? At { get; set; }
        [JsonPropertyName("kind")] public string? Kind { get; set; }
        [JsonPropertyName("facing")] public int? Facing { get; set; }
        [JsonPropertyName("edges")] public int[]? Edges { get; set; }
        [JsonPropertyName("seed")] public int? Seed { get; set; }
        [JsonPropertyName("columns")] public int? Columns { get; set; }
        [JsonPropertyName("courses")] public int? Courses { get; set; }
        [JsonPropertyName("leaves")] public int? Leaves { get; set; }
    }
}
