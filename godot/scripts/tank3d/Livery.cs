using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// A squad's paint on a 3D tank (docs/tank3d.md, «Окраска»), after the jeep game's shop (jackal_port/ratel,
/// <c>Level3DBtr.paint</c>) but drawn in the cel shader (<see cref="Toon"/>) and
/// not made on the CPU.
/// <list type="bullet">
/// <item><b>The paint</b> is a colour (<c>data/paints.json</c>: a base, and four
/// colours light to dark) and a pattern - none, or a mask in
/// <c>assets/Images/Camo/</c>, a layer a channel - kept as one id,
/// <c>olive</c> or <c>olive/woodland</c>. The shader takes the army green - the
/// texels <c>side_brown</c> takes - to it, each keeping how much darker or
/// lighter than the paint it was, and lays the pattern out in the model's frame
/// (<see cref="Toon.Dress"/>'s <c>space</c>), so it runs on across the parts and
/// turns with the turret.</item>
/// </list>
/// A side's mark on the roof was tried and taken off (author 2026-11-04): a
/// disc in the side's ink read as a lit part, not as paint.
/// </summary>
public static class Livery
{
    public sealed record Colour(string Id, Color Base, Color[] Palette);
    public sealed record Pattern(string Id, float Tile);

    public static readonly IReadOnlyList<Colour> Colours;
    public static readonly IReadOnlyList<Pattern> Patterns;

    /// <summary>The model's own paint, which paints nothing; the other side's of old.</summary>
    public static readonly string Own, Brown;

    /// <summary>A pattern's tile, hull lengths, unless the pattern says.</summary>
    private static readonly float Tile;

    static Livery()
    {
        var colours = new List<Colour>();
        var patterns = new List<Pattern>();
        string own = "olive", brown = "brown";
        float tile = 1.1f;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AssetRoot.Data, "paints.json")));
            JsonElement root = doc.RootElement;
            tile = root.TryGetProperty("tile", out JsonElement t) ? t.GetSingle() : tile;
            own = root.TryGetProperty("own", out JsonElement o) ? o.GetString()! : own;
            brown = root.TryGetProperty("brown", out JsonElement b) ? b.GetString()! : brown;
            foreach (JsonElement c in root.GetProperty("colours").EnumerateArray())
            {
                var pal = new Color[4];
                int i = 0;
                foreach (JsonElement p in c.GetProperty("palette").EnumerateArray())
                    if (i < 4)
                        pal[i++] = new Color(p.GetString()!);
                colours.Add(new Colour(c.GetProperty("id").GetString()!, new Color(c.GetProperty("base").GetString()!), pal));
            }
            foreach (JsonElement p in root.GetProperty("patterns").EnumerateArray())
                patterns.Add(new Pattern(p.GetProperty("id").GetString()!,
                                         p.TryGetProperty("tile", out JsonElement pt) ? pt.GetSingle() : tile));
        }
        catch (Exception e)
        {
            GD.PrintErr($"livery: data/paints.json: {e.Message}");
        }
        if (colours.Count == 0)
            colours.Add(new Colour(own, new Color("586636"), new[] { new Color("7a8052"), new Color("6b5032"), new Color("364228"), new Color("1c1c1a") }));
        if (patterns.Count == 0)
            patterns.Add(new Pattern("", tile));
        Colours = colours;
        Patterns = patterns;
        Own = own;
        Brown = brown;
        Tile = tile;
    }

    // --- the id ------------------------------------------------------------------

    public static string Id(string colour, string pattern) => pattern.Length == 0 ? colour : colour + "/" + pattern;

    public static string ColourOf(string id)
    {
        int slash = id.IndexOf('/');
        return slash < 0 ? id : id[..slash];
    }

    public static string PatternOf(string id)
    {
        int slash = id.IndexOf('/');
        return slash < 0 ? "" : id[(slash + 1)..];
    }

    public static int ColourIndex(string id)
    {
        string c = ColourOf(id);
        for (int i = 0; i < Colours.Count; i++)
            if (Colours[i].Id == c)
                return i;
        return 0;
    }

    public static int PatternIndex(string id)
    {
        string p = PatternOf(id);
        for (int i = 0; i < Patterns.Count; i++)
            if (Patterns[i].Id == p)
                return i;
        return 0;
    }

    /// <summary>Whether <paramref name="id"/> is a paint there is.</summary>
    public static bool Valid(string id)
    {
        string c = ColourOf(id), p = PatternOf(id);
        bool colour = false, pattern = false;
        foreach (Colour x in Colours)
            colour |= x.Id == c;
        foreach (Pattern x in Patterns)
            pattern |= x.Id == p;
        return colour && pattern;
    }

    // --- the paint ---------------------------------------------------------------

    private static readonly Dictionary<string, Texture2D?> Masks = new();

    /// <summary>A pattern's mask, off disk the first time, then kept; null when it is missing.</summary>
    public static Texture2D? Mask(string pattern)
    {
        if (pattern.Length == 0)
            return null;
        if (Masks.TryGetValue(pattern, out Texture2D? had))
            return had;
        string path = AssetRoot.Repo + "/assets/Images/Camo/" + pattern + ".png";
        Texture2D? tex = null;
        if (File.Exists(path) && Image.LoadFromFile(path) is { } image && !image.IsEmpty())
        {
            image.GenerateMipmaps();
            tex = ImageTexture.CreateFromImage(image);
        }
        else
            GD.PrintErr($"livery: no mask {path}");
        return Masks[pattern] = tex;
    }

    /// <summary><paramref name="model"/> in paint <paramref name="id"/>; the model's own
    /// green - <see cref="Own"/> without a pattern - paints nothing.</summary>
    public static void Apply(TankModel model, string id)
    {
        Colour colour = Colours[ColourIndex(id)];
        Pattern pattern = Patterns[PatternIndex(id)];
        bool on = colour.Id != Own || pattern.Id.Length > 0;
        Texture2D? mask = Mask(pattern.Id);
        bool camo = mask is not null;
        Color c0 = camo ? colour.Palette[0] : colour.Base;
        float[] lums = PaintLums(model);
        for (int i = 0; i < model.Cel.Count; i++)
        {
            ShaderMaterial m = model.Cel[i];
            m.Param("paint_on", on);
            if (!on)
                continue;
            m.Param("paint_lum", lums[i]);
            m.Param("paint_c0", c0);
            m.Param("paint_c1", camo ? colour.Palette[1] : c0);
            m.Param("paint_c2", camo ? colour.Palette[2] : c0);
            m.Param("paint_c3", camo ? colour.Palette[3] : c0);
            m.Param("camo_on", camo);
            if (camo)
            {
                m.Param("camo_tex", mask!);
                m.Param("camo_tile", Mathf.Max(model.HullLength, 0.01f) * pattern.Tile);
            }
        }
    }

    private static readonly Dictionary<string, float[]> Lums = new();

    /// <summary>Each cel material's paint as bright as it is on the whole - its
    /// green texels' mean as stored - the Compatibility renderer's shader reads them so, not
    /// linear: measured linear the paint came out twice as light - for the paint to
    /// keep each texel's shade against; once per tag.</summary>
    private static float[] PaintLums(TankModel model)
    {
        if (Lums.TryGetValue(model.Tag, out float[]? had) && had.Length == model.Cel.Count)
            return had;
        var lums = new float[model.Cel.Count];
        for (int i = 0; i < model.Cel.Count; i++)
        {
            lums[i] = 0.25f;
            if (model.Cel[i].Param("albedo_tex").As<Texture2D>() is not { } tex || tex.GetImage() is not { } image)
                continue;
            if (image.IsCompressed())
                image.Decompress();
            image.Resize(128, 128, Image.Interpolation.Bilinear);
            Color tint = model.Cel[i].Param("albedo").AsColor();
            double sum = 0.0;
            int n = 0;
            for (int y = 0; y < 128; y++)
                for (int x = 0; x < 128; x++)
                {
                    Color c = image.GetPixel(x, y) * tint;
                    if (c.G - Mathf.Max(c.R, c.B) < 0.05f)
                        continue;
                    sum += 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
                    n++;
                }
            if (n > 64)
                lums[i] = (float)(sum / n);
        }
        return Lums[model.Tag] = lums;
    }

    private static readonly Dictionary<string, Texture2D> Swatches = new();
    private static readonly Dictionary<string, Image?> MaskImages = new();

    /// <summary>Paint <paramref name="id"/> as a swatch <paramref name="px"/> square -
    /// its pattern in its colours, a tile's corner, or its colour - for the hangar;
    /// made once and kept.</summary>
    public static Texture2D Swatch(string id, int px)
    {
        string key = $"{id}@{px}";
        if (Swatches.TryGetValue(key, out Texture2D? had))
            return had;
        Colour colour = Colours[ColourIndex(id)];
        string pattern = PatternOf(id);
        var image = Image.CreateEmpty(px, px, false, Image.Format.Rgb8);
        Image? mask = null;
        if (pattern.Length > 0)
        {
            if (!MaskImages.TryGetValue(pattern, out mask))
            {
                string path = AssetRoot.Repo + "/assets/Images/Camo/" + pattern + ".png";
                mask = File.Exists(path) ? Image.LoadFromFile(path) : null;
                // a third of the tile, as much of it as a hull side shows
                if (mask is not null && !mask.IsEmpty())
                {
                    mask.Crop(mask.GetWidth() / 3, mask.GetHeight() / 3);
                    mask.Resize(px * 2, px * 2, Image.Interpolation.Bilinear);
                }
                MaskImages[pattern] = mask;
            }
        }
        for (int y = 0; y < px; y++)
            for (int x = 0; x < px; x++)
            {
                Color c = mask is null ? colour.Base : colour.Palette[0];
                if (mask is not null)
                {
                    Color m = mask.GetPixel(Mathf.Min(x * mask.GetWidth() / px, mask.GetWidth() - 1),
                                            Mathf.Min(y * mask.GetHeight() / px, mask.GetHeight() - 1));
                    c = c.Lerp(colour.Palette[1], m.R).Lerp(colour.Palette[2], m.G).Lerp(colour.Palette[3], m.B);
                }
                image.SetPixel(x, y, c);
            }
        return Swatches[key] = ImageTexture.CreateFromImage(image);
    }
}
