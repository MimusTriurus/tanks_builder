using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The per-class numbers that are tuning rather than character, read from
/// `classes.json` beside the project.
///
/// **The file fills in a table the code declares; it does not declare the table.**
/// Which classes exist is <see cref="MovementProfile.All"/> - a class is a profile
/// with a tag, a speed, a rank and a reload, none of which a file can supply
/// meaningfully - and this supplies, by tag, the two numbers that are pure
/// appearance and want to be argued with: how big the tracer is drawn and how
/// thick its smoke trail is. That is the same division of authority
/// <see cref="PanelText"/> holds over the panel, and it fails the same way if it
/// is broken: an entry with no class behind it is a setting for a tank that does
/// not exist, and a class with no entry is a tank that quietly kept the compiled
/// figure. So both directions are asserted in the self-test.
///
/// Read from an absolute path rather than imported as a resource, the reason the
/// atlases and panel.json are: edit, restart, see it, nothing to rebuild. A
/// missing or broken file is not fatal - the compiled triples stand and the trace
/// says which happened, because a settings file that quietly did not load looks
/// exactly like one whose settings did nothing.
///
/// **There is no precedence question with the flags here, and that is by
/// construction.** The file sets the per-class triple; the sliders and
/// --tracer-size / --smoke-size set a *level* over it. They are different
/// quantities, so neither can overrule the other - unlike panel.json, which sets
/// the same field a flag does and therefore has to lose to it.
///
/// What the file may do and nothing can stop: flatten the class spread by giving
/// three classes the same number. That is the panel.json step warning again. The
/// self-test complains, because a spread that has been flattened is the thing
/// those triples exist to carry.
/// </summary>
public sealed class ClassConfig
{
    public const string FileName = "classes.json";

    /// <summary>Whether a file was found and parsed. False means every profile is
    /// running on the figure compiled into it.</summary>
    public bool Loaded { get; private set; }

    /// <summary>Why not, when not. Printed rather than thrown: the bench is
    /// perfectly usable on the compiled numbers.</summary>
    public string Error { get; private set; } = "";

    private readonly Dictionary<string, ClassEntry> _classes =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Which tags the file speaks for, so the self-test can hold them
    /// against the classes the code has - in both directions.</summary>
    public IEnumerable<string> Tags => _classes.Keys;

    /// <summary>Ids the file lists more than once. A duplicate is not a merge: the
    /// second entry wins silently and the first one reads as a number that did
    /// nothing.</summary>
    public IReadOnlyList<string> Duplicates => _duplicates;

    private readonly List<string> _duplicates = new();

    public static ClassConfig Load(string? path = null)
    {
        var config = new ClassConfig();
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
            if (file?.Classes is null)
            {
                config.Error = "no classes in the file";
                return config;
            }
            foreach (ClassEntry entry in file.Classes)
            {
                if (string.IsNullOrEmpty(entry.Id))
                    continue;
                if (config._classes.ContainsKey(entry.Id))
                    config._duplicates.Add(entry.Id);
                config._classes[entry.Id] = entry;
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
    /// Write what the file says onto the profiles, leaving anything it is silent
    /// about alone.
    ///
    /// A number that is absent, zero or negative is silence rather than an
    /// instruction: a tracer at zero is a round with no tracer, which is what the
    /// switch is for, and a file is not a place to discover that from.
    /// </summary>
    /// <returns>How many classes the file actually spoke for.</returns>
    public int Apply()
    {
        int spoken = 0;
        foreach (MovementProfile profile in MovementProfile.All)
        {
            if (!_classes.TryGetValue(profile.Tag, out ClassEntry? entry))
                continue;
            spoken++;
            if (entry.TracerSize is > 0.0)
                profile.TracerCalibre = entry.TracerSize.Value;
            if (entry.SmokeSize is > 0.0)
                profile.SmokeCalibre = entry.SmokeSize.Value;
        }
        return spoken;
    }

    /// <summary>Tags in the file that are not classes the harness has. Named
    /// rather than ignored: it is the half of the check that catches a typo,
    /// which otherwise reads as a number that did nothing.</summary>
    public IEnumerable<string> Strays()
    {
        foreach (string tag in _classes.Keys)
        {
            bool known = false;
            foreach (MovementProfile profile in MovementProfile.All)
                if (string.Equals(profile.Tag, tag, StringComparison.OrdinalIgnoreCase))
                    known = true;
            if (!known)
                yield return tag;
        }
    }

    // --- file shape --------------------------------------------------------

    private sealed class FileShape
    {
        [JsonPropertyName("classes")] public ClassEntry[]? Classes { get; set; }
    }

    private sealed class ClassEntry
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("tracer_size")] public double? TracerSize { get; set; }
        [JsonPropertyName("smoke_size")] public double? SmokeSize { get; set; }
    }
}
