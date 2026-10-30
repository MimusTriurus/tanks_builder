using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The map editor's preset brushes (stage 5 of docs/editor-ux-plan.md): a pond,
/// a ford, a hill, a wooded hill, a swamp - one stroke for what took two or
/// three tabs.
///
/// <b>Data, not code.</b> A preset is a row of <c>editor_presets.json</c>
/// beside the project: layers laid in order, each one painting the brush's own
/// cells (<c>core</c>) or the ring just outside them (<c>edge</c>), on every
/// cell or a share of them (<c>chance</c>, by a hash of the cell, so the same
/// stroke always lays the same swamp). The file is read when the tab is laid
/// out, so a preset is added without a build.
///
/// <b>Through <see cref="MapEdit"/>'s own writes</b> - Floor, Water, Over,
/// Level - so a preset can make nothing the brushes could not, and a layer
/// the alphabet has no letter for is refused per cell like any stroke.
/// </summary>
public static class MapPresets
{
    public const string FileName = "editor_presets.json";

    public sealed class Layer
    {
        [JsonPropertyName("ring")] public string Ring { get; set; } = "core";
        [JsonPropertyName("chance")] public float? Chance { get; set; }
        [JsonPropertyName("floor")] public string? Floor { get; set; }
        [JsonPropertyName("water")] public bool? Water { get; set; }
        [JsonPropertyName("cover")] public string? Cover { get; set; }
        [JsonPropertyName("level")] public int? Level { get; set; }

        public bool Edge => Ring.Equals("edge", StringComparison.OrdinalIgnoreCase);
    }

    public sealed class Preset
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("say")] public string Say { get; set; } = "";
        [JsonPropertyName("layers")] public List<Layer> Layers { get; set; } = new();

        /// <summary>Whether it paints the ring outside the brush too - the
        /// brush's cells grow by one for it.</summary>
        public bool Edged => Layers.Any(l => l.Edge);
    }

    private sealed class Shape
    {
        [JsonPropertyName("presets")] public List<Preset>? Presets { get; set; }
    }

    public static string PathOf => Path.Combine(AssetRoot.Data, FileName);

    /// <summary>
    /// The presets on disk, each one judged on the way in - a floor, a cover
    /// or a ring nobody has is named here, in the author's words, rather than
    /// skipped by the brush. A file that is missing or does not parse is no
    /// presets and a reason.
    /// </summary>
    public static IReadOnlyList<Preset> Load(out string problem)
    {
        problem = "";
        if (!File.Exists(PathOf))
        {
            problem = $"no {FileName} beside the project";
            return Array.Empty<Preset>();
        }
        try
        {
            Shape? file = JsonSerializer.Deserialize<Shape>(File.ReadAllText(PathOf), new JsonSerializerOptions
            {
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            var kept = new List<Preset>();
            foreach (Preset preset in file?.Presets ?? new List<Preset>())
            {
                string? wrong = Judge(preset);
                if (wrong is null)
                    kept.Add(preset);
                else
                    problem = $"{FileName}: {preset.Name}: {wrong}";
            }
            return kept;
        }
        catch (Exception e)
        {
            problem = $"{FileName}: {e.Message}";
            return Array.Empty<Preset>();
        }
    }

    private static string? Judge(Preset preset)
    {
        if (preset.Name.Length == 0)
            return "a preset needs a name";
        foreach (Layer layer in preset.Layers)
        {
            if (!layer.Ring.Equals("core", StringComparison.OrdinalIgnoreCase) && !layer.Edge)
                return $"ring \"{layer.Ring}\" - a layer paints the core or the edge";
            if (layer.Floor is { } floor && FloorOf(floor) is null)
                return $"floor \"{floor}\" - solid, sand, mud, rock or deep";
            if (layer.Cover is { } cover && CoverOf(cover) is null)
                return $"cover \"{cover}\" - none, forest, trench or minefield";
            if (layer.Level is int level && (level < -1 || level > 1))
                return $"level {level} - a brush lays -1, 0 or 1";
        }
        return null;
    }

    private static Foundation? FloorOf(string name) => name.ToLowerInvariant() switch
    {
        "solid" => Foundation.Solid,
        "sand" => Foundation.Sand,
        "mud" => Foundation.Mud,
        "rock" => Foundation.Rock,
        "deep" => Foundation.Deep,
        _ => null,
    };

    private static Cover? CoverOf(string name) => name.ToLowerInvariant() switch
    {
        "none" => global::TankSpriteTest.Cover.None,
        "forest" => global::TankSpriteTest.Cover.Forest,
        "trench" => global::TankSpriteTest.Cover.Trench,
        "minefield" => global::TankSpriteTest.Cover.Minefield,
        _ => null,
    };

    /// <summary>
    /// A preset laid with <paramref name="core"/> as the brush's cells; the
    /// edge is the cells next to them and not among them. Cells in
    /// <paramref name="keep"/> take no edge layer - the core an earlier step
    /// of the same drag laid, which the next step's edge would otherwise wash
    /// back. Answers the refusals, cell by cell.
    /// </summary>
    public static List<string> Lay(MapEdit edit, Preset preset, IReadOnlyCollection<Vector2I> core,
                                   ISet<Vector2I>? keep = null)
    {
        var inner = new HashSet<Vector2I>(core.Where(edit.Inside));
        var edge = new List<Vector2I>();
        if (preset.Edged)
        {
            var seen = new HashSet<Vector2I>(inner);
            foreach (Vector2I cell in inner)
            foreach (int heading in HexField.EdgeHeadings)
            {
                Vector2I next = HexField.Step(cell, heading);
                if (edit.Inside(next) && seen.Add(next) && (keep is null || !keep.Contains(next)))
                    edge.Add(next);
            }
        }
        var refused = new List<string>();
        foreach (Layer layer in preset.Layers)
            foreach (Vector2I cell in layer.Edge ? edge : inner.OrderBy(c => c.Y).ThenBy(c => c.X).ToList())
            {
                if (layer.Chance is float chance && Roll(cell, preset.Name) >= chance)
                    continue;
                if (!Paint(edit, cell, layer, out string why) && why.Length > 0)
                    refused.Add($"({cell.X},{cell.Y}): {why}");
            }
        return refused;
    }

    /// <summary>One layer on one cell: floor, level, water, cover, in that
    /// order - water sets its own level, and a cover goes on what is there.</summary>
    private static bool Paint(MapEdit edit, Vector2I cell, Layer layer, out string why)
    {
        why = "";
        if (!edit.OnBoard(cell))
            return true;
        bool ok = true;
        if (layer.Floor is { } floor && FloorOf(floor) is Foundation f)
            ok &= edit.Floor(cell, f, out why);
        if (ok && layer.Level is int level)
            ok &= edit.Level(cell, level, out why);
        if (ok && layer.Water is bool wet && edit.GlyphAt(cell).Water != wet)
            ok &= edit.Water(cell, wet, out why);
        if (ok && layer.Cover is { } cover && CoverOf(cover) is Cover c)
            ok &= edit.Over(cell, c, out why);
        return ok;
    }

    /// <summary>A number in [0, 1) that a cell and a preset always give.</summary>
    private static float Roll(Vector2I cell, string salt)
    {
        unchecked
        {
            uint h = 2166136261;
            foreach (char ch in salt)
                h = (h ^ ch) * 16777619;
            h = (h ^ (uint)cell.X) * 16777619;
            h = (h ^ (uint)cell.Y) * 16777619;
            h ^= h >> 13;
            h *= 0x5bd1e995;
            h ^= h >> 15;
            return (h & 0xFFFFFF) / (float)0x1000000;
        }
    }
}
