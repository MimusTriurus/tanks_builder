using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The game's words, read from <c>data/lang/&lt;lang&gt;.json</c> beside the
/// project: one flat object, key to text, <c>{0}</c> for what is filled in, an
/// array of forms where the text counts something (<see cref="N"/>). Read by
/// absolute path past Godot's importer, as every <c>data/*.json</c> is: edit,
/// restart, see.
///
/// The language is <c>--lang xx</c> on the command line, else <see cref="Default"/>,
/// and it is read here on first use rather than set by a scene: the squad's
/// upgrades and classes are static fields, built before any scene parses its
/// flags. A key the language lacks falls back to <see cref="Base"/>, and one
/// that lacks too shows as the key itself - visible on screen and said once in
/// the log, never an empty label.
/// </summary>
public static class L
{
    /// <summary>The language the game speaks without <c>--lang</c>.</summary>
    public const string Default = "en";

    /// <summary>The language every key is written in first: the fallback.</summary>
    public const string Base = "ru";

    private static Dictionary<string, JsonElement>? _lang, _base;
    private static string _code = "";
    private static Font? _font;
    private static bool _applied;
    private static readonly HashSet<string> _missed = new();

    /// <summary>The language in use, after <c>--lang</c> and a file that exists.</summary>
    public static string Code
    {
        get
        {
            Load();
            return _code;
        }
    }

    /// <summary>The language's own typeface - its file's <c>_font</c>, a name in
    /// <see cref="AssetRoot.Fonts"/> - or null where it has none (Russian: the
    /// faces ratel has are Latin only) and Godot's own font speaks it.</summary>
    public static Font? Font
    {
        get
        {
            Load();
            return _font;
        }
    }

    /// <summary>Makes <see cref="Font"/> the font every control and every
    /// <c>DrawString(ThemeDB.FallbackFont, …)</c> uses: called by a scene before it
    /// builds its interface, once a process.</summary>
    public static void Apply()
    {
        if (_applied)
            return;
        _applied = true;
        if (Font is not { } f)
            return;
        // Controls read the default theme's font, which took the fallback when it was built
        ThemeDB.FallbackFont = f;
        ThemeDB.GetDefaultTheme().DefaultFont = f;
    }

    /// <summary>The text of <paramref name="key"/>.</summary>
    public static string T(string key) => Text(Find(key), key, 0);

    public static string T(string key, object a) => string.Format(T(key), a);

    public static string T(string key, object a, object b) => string.Format(T(key), a, b);

    public static string T(string key, object a, object b, object c) => string.Format(T(key), a, b, c);

    public static string T(string key, params object[] args) => string.Format(T(key), args);

    /// <summary>The form of <paramref name="key"/> that goes with
    /// <paramref name="n"/>, with <c>{0}</c> as <paramref name="n"/>: the value
    /// is an array of forms - Russian one / few / many, English one / other.</summary>
    public static string N(string key, int n) => string.Format(Text(Find(key), key, n), n);

    private static JsonElement? Find(string key)
    {
        Load();
        if (_lang!.TryGetValue(key, out var v) || _base!.TryGetValue(key, out v))
            return v;
        if (_missed.Add(key))
            GD.PrintErr($"lang: no \"{key}\" in {_code}.json or {Base}.json");
        return null;
    }

    private static string Text(JsonElement? v, string key, int n)
    {
        if (v is not { } e)
            return key;
        if (e.ValueKind == JsonValueKind.String)
            return e.GetString()!;
        if (e.ValueKind != JsonValueKind.Array || e.GetArrayLength() == 0)
            return key;
        int len = e.GetArrayLength();
        return e[Math.Min(Form(n), len - 1)].GetString() ?? key;
    }

    /// <summary>Which of the forms counts <paramref name="n"/> in this language.</summary>
    private static int Form(int n)
    {
        n = Math.Abs(n);
        if (_code != "ru")
            return n == 1 ? 0 : 1;
        int m10 = n % 10, m100 = n % 100;
        return m10 == 1 && m100 != 11 ? 0
            : m10 is >= 2 and <= 4 && (m100 < 12 || m100 > 14) ? 1
            : 2;
    }

    private static void Load()
    {
        if (_lang is not null)
            return;
        string code = Default;
        string[] args = OS.GetCmdlineUserArgs();
        for (int i = 0; i + 1 < args.Length; i++)
            if (args[i] == "--lang")
                code = args[i + 1].ToLowerInvariant();
        _base = Read(Base) ?? new();
        _lang = code == Base ? _base : Read(code);
        if (_lang is null)
        {
            GD.PrintErr($"lang: no {code}.json - speaking {Base}");
            _lang = _base;
            code = Base;
        }
        _code = code;
        if (_lang.TryGetValue("_font", out var name) && name.ValueKind == JsonValueKind.String)
        {
            string path = Path.Combine(AssetRoot.Fonts, name.GetString()!);
            var file = new FontFile();
            if (File.Exists(path) && file.LoadDynamicFont(path) == Error.Ok)
                _font = file;
            else
                GD.PrintErr($"lang: {code}.json names the font {path}, and it does not load");
        }
    }

    private static Dictionary<string, JsonElement>? Read(string code)
    {
        string path = Path.Combine(AssetRoot.Data, "lang", code + ".json");
        if (!File.Exists(path))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
            var map = new Dictionary<string, JsonElement>();
            foreach (var p in doc.RootElement.EnumerateObject())
                if (p.Name != "_")
                    map[p.Name] = p.Value.Clone();
            return map;
        }
        catch (Exception e)
        {
            GD.PrintErr($"lang: {code}.json: {e.Message}");
            return null;
        }
    }
}
