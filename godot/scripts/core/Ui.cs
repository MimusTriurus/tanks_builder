using Godot;

namespace TankSpriteTest;

/// <summary>
/// The game's interface theme, one for every screen of the 3D line - the quick
/// battle's lobby and build, the hangar, and (to come) the match's HUD: the
/// palette by role, the type scale, the rim in whole screen pixels, the faces,
/// and the two pieces every screen is made of, a text and a box.
///
/// Decided 2026-11-03 (author): the hangar and the match looked like two games,
/// each with its own colours, sizes, corners and tips, and one colour meaning one
/// thing on one screen and another on the next. A screen takes its colours here
/// by role, never as a literal of its own; a new role is added here, with what it
/// means. Contrast on <see cref="Panel"/>: text 4.5:1 and more, rims and marks 3:1
/// (WCAG 2.x), checked when the palette was set (2026-11-02).
/// </summary>
public static class Ui
{
    // --- surfaces -------------------------------------------------------------------

    /// <summary>The screen behind everything.</summary>
    public static readonly Color Bg = new("131712");

    /// <summary>A panel on the screen.</summary>
    public static readonly Color Panel = new("20261E");

    /// <summary>A well inside a panel: a branch not chosen, a map's frame.</summary>
    public static readonly Color Inset = new("171C16");

    /// <summary>A panel's and a box's rim, 3:1 on <see cref="Panel"/>.</summary>
    public static readonly Color Line = new("667160");

    // --- text -----------------------------------------------------------------------

    public static readonly Color Ink = new("DEE4D5");

    /// <summary>What is said second: a kind, a meta line, a dimmed name.</summary>
    public static readonly Color Muted = new("98A18F");

    // --- roles ----------------------------------------------------------------------

    /// <summary>The olive: selected, bought, the action the screen leads to.</summary>
    public static readonly Color Accent = new("A3B65E");

    /// <summary>Text and marks on <see cref="Accent"/>.</summary>
    public static readonly Color OnAccent = new("131712");

    /// <summary>The fill of what is selected, under an <see cref="Accent"/> rim.</summary>
    public static readonly Color AccentFill = new("2A3322");

    /// <summary>The amber: what the next click buys, and a warning.</summary>
    public static readonly Color Extra = new("E3A73C");

    /// <summary>The red: no, not affordable, the enemy's, deleting.</summary>
    public static readonly Color Danger = new("E2695E");

    /// <summary>The match's movement: the move's slot and its badge - the move zone's
    /// blue, lighter (the zone's own ink is dark for a mark on the panel's black).</summary>
    public static readonly Color Move = new(0.31f, 0.61f, 0.91f);

    /// <summary>The match's attack: the attack's slot and its zone - orange, between
    /// the ambush's red and the bonus acts' amber.</summary>
    public static readonly Color Attack = new(1.0f, 0.47f, 0.12f);

    // --- tips -----------------------------------------------------------------------

    /// <summary>A tip's fill: a step over <see cref="Panel"/>, the tip being over it.</summary>
    public static readonly Color TipBg = new("2B3329");

    public static readonly Color TipInk = new("EEF1E8");
    public static readonly Color TipMuted = new("A4AD99");

    /// <summary>A tip's rim and the ring round what it tells of: lighter than
    /// <see cref="Line"/>, the tip being over the panels.</summary>
    public static readonly Color TipRim = new("98A18F");

    // --- type -----------------------------------------------------------------------

    /// <summary>Screens set in the large scale (<see cref="Size"/>): the hangar's pages
    /// past the lobby. The lobby and the old build keep their sizes.</summary>
    public static bool Large;

    /// <summary>The type scale (author 2026-11-03, after XAG 101: 18 px of glyph at
    /// 1080p on a PC is the floor, a font size of 17 in the 1600 × 900 layout): five
    /// steps the screens' sizes go to by role - caption 17 (kinds, meta lines,
    /// kickers), body 18, subhead 20, heading 22, title 28.</summary>
    public static int Size(int size) => !Large ? size : size switch
    {
        <= 12 => 17,
        13 => 18,
        <= 16 => 20,
        <= 18 => 22,
        <= 22 => 28,
        _ => size,
    };

    /// <summary>The bold face, the display text's: the language's own typeface as
    /// it is (Black Ops One is heavy already, and emboldened it smears), else the
    /// fallback font emboldened.</summary>
    public static readonly Font BoldFont = L.Font
        ?? new FontVariation { BaseFont = ThemeDB.FallbackFont, VariationEmbolden = 0.9f };

    public static Font FontOf(bool bold) => bold ? BoldFont : ThemeDB.FallbackFont;

    // --- rims -----------------------------------------------------------------------

    /// <summary>A box's rim, <paramref name="px"/> layout pixels rounded to whole screen
    /// pixels, inside its edges, whatever the window stretches the layout by: four
    /// filled strips of whole screen pixels each cover that many pixel rows, where a
    /// layout pixel's line (1.25 screen pixels in a 2000-wide window) covered one
    /// or two by where it stood, and two boxes' rims differed (author 2026-11-03).</summary>
    public static void DrawRim(CanvasItem on, Vector2 size, Color color, float px)
    {
        float t = RimLayout(on, px);
        on.DrawRect(new Rect2(0, 0, size.X, t), color);
        on.DrawRect(new Rect2(0, size.Y - t, size.X, t), color);
        on.DrawRect(new Rect2(0, t, t, size.Y - 2 * t), color);
        on.DrawRect(new Rect2(size.X - t, t, t, size.Y - 2 * t), color);
    }

    /// <summary>A rim of <paramref name="px"/> layout pixels as <see cref="DrawRim"/> draws
    /// it: rounded to whole screen pixels, one at least, given back in layout pixels.</summary>
    public static float RimLayout(CanvasItem on, float px)
    {
        float k = on.GetViewport() is Window w ? ScreenScale(w) : 1.0f;
        return Mathf.Max(1.0f, Mathf.Round(px * k)) / k;
    }

    /// <summary>Screen pixels to a layout pixel in <paramref name="w"/>: the 1600 × 900
    /// layout stretched to the window (<c>canvas_items</c>) - 1.6 on a 2560 × 1440 screen.</summary>
    public static float ScreenScale(Window w)
    {
        Vector2I size = w.Size, layout = w.ContentScaleSize;
        float stretch = layout.X > 0 && layout.Y > 0
            ? Mathf.Min(size.X / (float)layout.X, size.Y / (float)layout.Y) : 1.0f;
        return Mathf.Max(0.01f, stretch * w.ContentScaleFactor);
    }

    // --- pieces ---------------------------------------------------------------------

    /// <summary>A line of text in the scale: <paramref name="size"/> is the role's
    /// size before <see cref="Size"/>.</summary>
    public static Label Text(string text, int size, Color ink, bool bold = false, bool wrap = false)
    {
        var l = new Label { Text = text, AutowrapMode = wrap ? TextServer.AutowrapMode.WordSmart : TextServer.AutowrapMode.Off };
        l.AddThemeFontSizeOverride("font_size", Size(size));
        l.AddThemeColorOverride("font_color", ink);
        if (bold)
            l.AddThemeFontOverride("font", BoldFont);
        if (wrap)
            l.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        return l;
    }

    /// <summary>A box round <paramref name="inner"/>: a fill, <paramref name="pad"/>
    /// round its content, square corners and a rim of 1 (<see cref="Line"/> unless
    /// given) in whole screen pixels.</summary>
    /// <param name="band">A band across the box's top, under its rim, <c>High</c> tall and
    /// flush with its edges: the inner's first child stands on it (the top margin goes).
    /// Read each draw, so a hover can change its colour.</param>
    public static PanelContainer Box(Control inner, Color bg, float pad, Color? rim = null,
                                     System.Func<(Color Fill, Color? Line, float High)>? band = null)
    {
        // the rim drawn over the fill in whole screen pixels (DrawRim), not the
        // style's border: that is whole layout pixels, 1.25 screen pixels in a
        // 2000-wide window, and fell on one pixel or two by where the panel stood
        var box = new StyleBoxFlat { BgColor = bg, BorderColor = rim ?? Line };
        box.SetContentMarginAll(pad);
        if (band is not null)
            box.ContentMarginTop = 0;
        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel", box);
        panel.Draw += () =>
        {
            if (band?.Invoke() is { } b)
            {
                panel.DrawRect(new Rect2(0, 0, panel.Size.X, b.High), b.Fill);
                if (b.Line is { } line)
                {
                    float t = RimLayout(panel, 1);
                    panel.DrawRect(new Rect2(0, b.High - t, panel.Size.X, t), line);
                }
            }
            DrawRim(panel, panel.Size, box.BorderColor, 1.0f);
        };
        panel.AddChild(inner);
        return panel;
    }
}
