using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The shape of every wall the benches lay, read from `walls.json` beside the
/// project: seed, columns, courses, sides and leaves, by name.
///
/// **The file fills in a table the code declares; it does not declare the
/// table.** Which walls exist is the benches' - <see cref="TankBench.WallNames"/>
/// is the ring plus the four samples, <see cref="WallBench.WallName"/> is the one
/// the wall bench stands - because a wall is a cell on a board, a bearing, a
/// panel of dials and a place in a printed line, none of which a file can supply
/// meaningfully. What it supplies is the five numbers that <i>are</i> the wall's
/// shape and are the ones argued with. Same division of authority
/// <see cref="ClassConfig"/> holds over the classes, and it fails the same way if
/// it is broken: an entry with no wall behind it is a recipe for a wall that does
/// not exist, and a wall with no entry is one that quietly kept the compiled
/// figure. Both directions are asserted in the self-test.
///
/// Read from an absolute path rather than imported as a resource, the reason the
/// atlases and panel.json are: edit, restart, see it, nothing to rebuild. A
/// missing or broken file is not fatal - the compiled recipes stand and the bench
/// prints which happened, because a settings file that quietly did not load looks
/// exactly like one whose settings did nothing.
///
/// **There is no precedence question with the flags, and that is by
/// construction.** --seed / --sides / --courses / --leaves write the same fields
/// this does, and they run after it for panel.json's reason: a flag is the more
/// specific statement, so a capture taken with one has to mean what it says
/// whatever file is lying beside it.
///
/// **Coverage is not here, and that is a decision.** It is the prop's fit onto
/// its cell rather than the wall's shape, and on the tank bench one dial holds it
/// for every wall on the board on purpose - the four samples exist to move one
/// dial each off the ring's numbers, so a per-wall coverage would be a fifth
/// difference nobody asked for. The same goes for the profile weights, the notch
/// and the apron: they are not on any panel and do not differ per wall.
///
/// What the file may do and nothing can stop: flatten the samples by giving them
/// the ring's own numbers. That is <see cref="ClassConfig"/>'s spread warning
/// again - a sample that differs from the ring in nothing is a second ring, and
/// the whole arrangement of four of them is that each moves one thing. The
/// self-test complains.
/// </summary>
public sealed class WallConfig
{
    public const string FileName = "walls.json";

    /// <summary>Whether a file was found and parsed. False means every recipe is
    /// running on the figures compiled into it.</summary>
    public bool Loaded { get; private set; }

    /// <summary>Why not, when not. Printed rather than thrown: the benches are
    /// perfectly usable on the compiled numbers.</summary>
    public string Error { get; private set; } = "";

    private readonly Dictionary<string, WallEntry> _walls =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Which names the file speaks for, so the self-test can hold them
    /// against the walls the code has - in both directions.</summary>
    public IEnumerable<string> Names => _walls.Keys;

    /// <summary>Names the file lists more than once. A duplicate is not a merge:
    /// the second entry wins silently and the first reads as a number that did
    /// nothing.</summary>
    public IReadOnlyList<string> Duplicates => _duplicates;

    private readonly List<string> _duplicates = new();

    public static WallConfig Load(string? path = null)
    {
        var config = new WallConfig();
        path ??= Path.Combine(ProjectSettings.GlobalizePath("res://"), FileName);
        try
        {
            if (!File.Exists(path))
            {
                config.Error = $"no {Path.GetFileName(path)} beside the project";
                return config;
            }
            var file = JsonSerializer.Deserialize<FileShape>(
                File.ReadAllText(path),
                new JsonSerializerOptions
                {
                    ReadCommentHandling = JsonCommentHandling.Skip,
                });
            if (file?.Walls is null)
            {
                config.Error = "no walls in the file";
                return config;
            }
            foreach (WallEntry entry in file.Walls)
            {
                if (string.IsNullOrEmpty(entry.Id))
                    continue;
                if (config._walls.ContainsKey(entry.Id))
                    config._duplicates.Add(entry.Id);
                config._walls[entry.Id] = entry;
            }
            config.Loaded = true;
        }
        catch (Exception e)
        {
            config.Error = e.Message;
        }
        return config;
    }

    /// <summary>
    /// Write what the file says about one wall onto its recipe, leaving anything
    /// it is silent about alone.
    ///
    /// A number that is absent or out of range is silence rather than an
    /// instruction: a wall of nought courses is not a thinner wall, it is no
    /// wall, and a file is not the place to discover that from. The ranges are
    /// the panel sliders' own, so a figure typed into the file cannot reach
    /// somewhere a dial cannot.
    /// </summary>
    /// <returns>Whether the file spoke for this wall at all.</returns>
    public bool Apply(string name, WallKit.Recipe recipe)
    {
        if (!_walls.TryGetValue(name, out WallEntry? entry))
            return false;
        if (entry.Seed is int seed)
            recipe.Seed = seed;
        if (entry.Columns is int columns and >= 2 and <= 16)
            recipe.Columns = columns;
        if (entry.Courses is int courses and >= 1
            && courses <= WallBench.MostCourses)
            recipe.Courses = courses;
        if (entry.Sides is int sides and >= 1 and <= 6)
            recipe.Sides = sides;
        if (entry.Leaves is int leaves and >= 1
            && leaves <= WallBench.MostLeaves)
            recipe.Leaves = leaves;
        return true;
    }

    /// <summary>Names in the file that are not walls either bench lays. Named
    /// rather than ignored: it is the half of the check that catches a typo,
    /// which otherwise reads as a recipe that did nothing.</summary>
    public IEnumerable<string> Strays(IEnumerable<string> known)
    {
        var have = new HashSet<string>(known, StringComparer.OrdinalIgnoreCase);
        foreach (string name in _walls.Keys)
            if (!have.Contains(name))
                yield return name;
    }

    /// <summary>What one line of the file says, so the self-test can hold two
    /// walls against each other without laying either. Null when the file is
    /// silent about that wall.</summary>
    public (int? Seed, int? Columns, int? Courses, int? Sides, int? Leaves)?
        Read(string name) =>
        _walls.TryGetValue(name, out WallEntry? e)
            ? (e.Seed, e.Columns, e.Courses, e.Sides, e.Leaves)
            : null;

    // --- file shape --------------------------------------------------------

    private sealed class FileShape
    {
        [JsonPropertyName("walls")] public WallEntry[]? Walls { get; set; }
    }

    private sealed class WallEntry
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("seed")] public int? Seed { get; set; }
        [JsonPropertyName("columns")] public int? Columns { get; set; }
        [JsonPropertyName("courses")] public int? Courses { get; set; }
        [JsonPropertyName("sides")] public int? Sides { get; set; }
        [JsonPropertyName("leaves")] public int? Leaves { get; set; }
    }
}
