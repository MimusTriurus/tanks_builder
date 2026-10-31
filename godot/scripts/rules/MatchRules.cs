using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TankSpriteTest;

/// <summary>
/// The match's numbers that playtests are to settle (GDD match.md), read once
/// from <c>data/match.json</c> beside the project - the core
/// (<see cref="Battle"/>) and the bench both ask here, so they cannot hold two
/// figures. A missing file or number leaves the compiled one, and
/// <see cref="Said"/> says which happened.
/// </summary>
public static class MatchRules
{
    public const string FileName = "match.json";

    /// <summary>The compiled stalemate: four rounds (match.md, «Пат — ничья»).</summary>
    public const int QuietRoundsDefault = 4;

    private static int? _quiet;
    private static string _said = "";

    /// <summary>Rounds in a row without a shot at a tank that end the match
    /// drawn.</summary>
    public static int QuietRoundsDraw
    {
        get
        {
            Load();
            return _quiet!.Value;
        }
    }

    /// <summary>Where the numbers came from: the file, or why not.</summary>
    public static string Said
    {
        get
        {
            Load();
            return _said;
        }
    }

    private static void Load()
    {
        if (_quiet is not null)
            return;
        _quiet = QuietRoundsDefault;
        string path = Path.Combine(AssetRoot.Data, FileName);
        try
        {
            if (!File.Exists(path))
            {
                _said = $"no {FileName} - the compiled {QuietRoundsDefault} quiet rounds";
                return;
            }
            var file = JsonSerializer.Deserialize<FileShape>(File.ReadAllText(path),
                new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip });
            if (file?.QuietRoundsDraw is int n && n > 0)
            {
                _quiet = n;
                _said = $"{FileName}: {n} quiet rounds";
            }
            else
                _said = $"{FileName} has no quiet_rounds_draw - the compiled {QuietRoundsDefault}";
        }
        catch (Exception e)
        {
            _said = $"{FileName}: {e.Message} - the compiled {QuietRoundsDefault}";
        }
    }

    private sealed class FileShape
    {
        [JsonPropertyName("quiet_rounds_draw")] public int? QuietRoundsDraw { get; set; }
    }
}
