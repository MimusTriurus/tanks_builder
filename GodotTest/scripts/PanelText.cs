using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The panel's names, its Russian descriptions, its slider ranges and what the
/// bench opens on - read from `panel.json` beside the project.
///
/// **The file decorates rows; it does not declare them.** Which rows exist, what
/// each one reads and what flipping it does are delegates onto the harness's own
/// fields, and a delegate cannot live in a file. So the code stays the authority
/// on the row list and on the grouping, and this supplies the text, the range
/// and the opening value by id. The alternative - a file that says which rows
/// there are - fails in the way this project dislikes most: an entry with no
/// code behind it is a control that does nothing, and a row with no entry is a
/// setting that vanished, and neither says a word.
///
/// So both directions are asserted in the self-test: every id the panel
/// registers has an entry, and every entry is an id the panel registers. A
/// mismatch is loud.
///
/// Read from an absolute path rather than imported as a resource, for the reason
/// the atlases are: edit the file, restart, see it. There is nothing to rebuild.
/// A missing or broken file is not fatal - the bench falls back to the English
/// labels compiled into <see cref="Main.BuildPanel"/> and to the defaults named
/// in code, and says so in the trace, because a settings file that quietly did
/// not load looks exactly like one whose settings did nothing.
///
/// Order of precedence, and it is the conventional one: the compiled-in default,
/// then this file, then a command-line flag. A flag is the more specific
/// statement, and a capture taken with one has to mean what it says whatever the
/// file holds.
/// </summary>
public sealed class PanelText
{
    public const string FileName = "panel.json";

    /// <summary>Whether a file was found and parsed. False means every label
    /// below is the English one from the code.</summary>
    public bool Loaded { get; private set; }

    /// <summary>Why not, when not. Printed rather than thrown: the bench is
    /// still usable without its Russian.</summary>
    public string Error { get; private set; } = "";

    private readonly Dictionary<string, GroupEntry> _groups = new();
    private readonly Dictionary<string, RowEntry> _rows = new();

    /// <summary>Which group the file puts each row in, so the self-test can
    /// check that against where the code puts it. Two statements about the same
    /// grouping that are allowed to disagree would be one statement too
    /// many.</summary>
    private readonly Dictionary<string, string> _rowGroup = new();

    public IEnumerable<string> RowIds => _rows.Keys;
    public IEnumerable<string> GroupIds => _groups.Keys;
    public string GroupOf(string id) =>
        _rowGroup.TryGetValue(id, out string? group) ? group : "";

    public static PanelText Load(string? path = null)
    {
        var text = new PanelText();
        path ??= Path.Combine(
            ProjectSettings.GlobalizePath("res://"), FileName);
        try
        {
            if (!File.Exists(path))
            {
                text.Error = $"no {Path.GetFileName(path)} beside the project";
                return text;
            }
            var file = JsonSerializer.Deserialize<FileShape>(
                File.ReadAllText(path),
                new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip });
            if (file?.Groups is null)
            {
                text.Error = "no groups in the file";
                return text;
            }
            foreach (GroupEntry group in file.Groups)
            {
                if (string.IsNullOrEmpty(group.Id))
                    continue;
                text._groups[group.Id] = group;
                foreach (RowEntry row in group.Rows ?? Array.Empty<RowEntry>())
                {
                    if (string.IsNullOrEmpty(row.Id))
                        continue;
                    text._rows[row.Id] = row;
                    text._rowGroup[row.Id] = group.Id;
                }
            }
            text.Loaded = true;
        }
        catch (Exception e)
        {
            text.Error = e.Message;
        }
        return text;
    }

    /// <summary>The heading for a group, or the id itself - which is what the
    /// code was passing before there was a file.</summary>
    public string GroupTitle(string id) =>
        _groups.TryGetValue(id, out GroupEntry? g) && !string.IsNullOrEmpty(g.Title)
            ? g.Title! : id;

    public string GroupNote(string id) =>
        _groups.TryGetValue(id, out GroupEntry? g) ? g.Description ?? "" : "";

    /// <summary>The row's caption, falling back to the English one in the code.
    /// The key each row duplicates stays in the caption whichever language it is
    /// in: the panel is a way to find the keys, not a replacement for them.</summary>
    public string Title(string id, string fallback) =>
        _rows.TryGetValue(id, out RowEntry? r) && !string.IsNullOrEmpty(r.Title)
            ? r.Title! : fallback;

    /// <summary>The Russian description, shown as the control's tooltip. Empty
    /// when there is none, and an empty tooltip is simply no tooltip.</summary>
    public string Note(string id) =>
        _rows.TryGetValue(id, out RowEntry? r) ? r.Description ?? "" : "";

    /// <summary>
    /// A slider's low, high and step, or the ones the code asked for.
    ///
    /// The step is the one field here worth a warning: on some rows it is not
    /// decoration. The heading sliders step by the atlas's own frame, and a file
    /// that smooths them out makes the sprite look as though it turns more
    /// finely than it was rendered. The file can do it; nothing can stop it; the
    /// row's description says so.
    /// </summary>
    public (double Lo, double Hi, double Step) Range(
        string id, double lo, double hi, double step)
    {
        if (_rows.TryGetValue(id, out RowEntry? r) && r.Range is { Length: 3 })
            return (r.Range[0], r.Range[1], r.Range[2]);
        return (lo, hi, step);
    }

    /// <summary>What this row should be set to at start-up, or null to leave the
    /// code's own default alone.</summary>
    public JsonElement? Default(string id) =>
        _rows.TryGetValue(id, out RowEntry? r) ? r.Default : null;

    public bool? Bool(string id)
    {
        JsonElement? value = Default(id);
        return value?.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    public double? Number(string id)
    {
        JsonElement? value = Default(id);
        return value?.ValueKind == JsonValueKind.Number ? value.Value.GetDouble() : null;
    }

    /// <summary>A dropdown's opening choice, named rather than numbered: an
    /// index into a list whose length is whatever loaded is a setting that goes
    /// wrong silently the day a fourth tank appears.</summary>
    public string? Choice(string id)
    {
        JsonElement? value = Default(id);
        return value?.ValueKind == JsonValueKind.String ? value.Value.GetString() : null;
    }

    // --- file shape --------------------------------------------------------

    private sealed class FileShape
    {
        [JsonPropertyName("groups")] public GroupEntry[]? Groups { get; set; }
    }

    private sealed class GroupEntry
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("rows")] public RowEntry[]? Rows { get; set; }
    }

    private sealed class RowEntry
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("range")] public double[]? Range { get; set; }
        [JsonPropertyName("default")] public JsonElement? Default { get; set; }
    }
}
