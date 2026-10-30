using System;
using System.IO;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The muzzle flash sheet: sixteen frames, four by four, the flash pointing
/// along +x.
///
/// Loaded from an absolute path like the tank atlases, and for the same reason -
/// re-key the sheet and the next run picks it up.
///
/// The file it wants is the keyed one. What the generator produced was drawn on
/// opaque white paper with no alpha channel at all, and the three traps in
/// lifting that into RGBA are written up in CLAUDE.md; this class expects the
/// job already done.
/// </summary>
public sealed class FlashSheet
{
    public ImageTexture? Texture { get; private set; }
    public string Error { get; private set; } = "";

    public int Columns { get; private set; } = 4;
    public int Rows { get; private set; } = 4;
    public Vector2I Tile { get; private set; } = new(256, 256);

    /// <summary>Where the muzzle sits inside a cell, in sheet pixels. The flash
    /// does not start at the cell edge - it blooms backwards around the muzzle
    /// as well as forwards - so this is measured from the art, not zero.</summary>
    public Vector2 Origin = new(110, 128);

    /// <summary>
    /// How many screen frames each sheet frame is held for.
    ///
    /// Not one rate for the whole thing. The flash proper is over in about a
    /// tenth of a second and the smoke hangs about for half of one, so an even
    /// rate either turns the flash into a lingering fire or cuts the smoke off
    /// while it is still thick. Six single frames, six doubles, then four
    /// quads: 0.57s in all at 60fps.
    /// </summary>
    public static readonly int[] Hold = { 1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 2, 2, 4, 4, 4, 4 };

    public static int Duration
    {
        get
        {
            int total = 0;
            foreach (int hold in Hold)
                total += hold;
            return total;
        }
    }

    /// <summary>Sheet frame to show <paramref name="elapsed"/> screen frames
    /// into a shot, or -1 once it is over.</summary>
    public static int FrameAt(int elapsed)
    {
        if (elapsed < 0)
            return -1;
        int t = 0;
        for (int i = 0; i < Hold.Length; i++)
        {
            t += Hold[i];
            if (elapsed < t)
                return i;
        }
        return -1;
    }

    public static FlashSheet Load(string path)
    {
        var sheet = new FlashSheet();
        if (!File.Exists(path))
        {
            sheet.Error = $"missing {path}";
            return sheet;
        }
        Image? image = Image.LoadFromFile(path);
        if (image is null)
        {
            sheet.Error = $"unreadable {path}";
            return sheet;
        }
        sheet.Tile = new Vector2I(image.GetWidth() / sheet.Columns,
            image.GetHeight() / sheet.Rows);
        sheet.Texture = ImageTexture.CreateFromImage(image);
        return sheet;
    }

    public Rect2 Region(int index)
    {
        int i = Math.Clamp(index, 0, Columns * Rows - 1);
        return new Rect2(new Vector2(i % Columns * Tile.X, i / Columns * Tile.Y), Tile);
    }
}
