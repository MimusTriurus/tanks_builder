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

    /// <summary>A panel over the match's board: <see cref="Panel"/>, the board a
    /// shade through it.</summary>
    public static readonly Color PanelOver = new(Panel, 0.92f);

    /// <summary>A button's face over the board: an olive light enough for the icons'
    /// near-black ink, which vanished on the panel's dark (2026-11-01).</summary>
    public static readonly Color Face = new(0.27f, 0.29f, 0.23f);

    /// <summary>What is spent or off: a slot used this turn, its rim and its key - grey,
    /// no colour.</summary>
    public static readonly Color Spent = new(0.42f, 0.43f, 0.40f);

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

    /// <summary>The amber: what is to be had beyond the base - what the next click buys
    /// in the hangar, the match's bonus acts (their group, their picking zone) - and a
    /// warning (the move's budget cut).</summary>
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

    /// <summary>A tip's last line, what a click would do or why it cannot: an amber
    /// lighter than <see cref="Extra"/>, for its size on <see cref="TipBg"/>.</summary>
    public static readonly Color TipState = new("E9B860");

    /// <summary>A tip's surface (<see cref="UiTip"/>): its fill, a soft shadow under it,
    /// 11 round its content; the rim is drawn on it in whole screen pixels.</summary>
    public static readonly StyleBoxFlat TipStyle = NewTipStyle();

    private static StyleBoxFlat NewTipStyle()
    {
        var style = new StyleBoxFlat { BgColor = TipBg, ShadowColor = new Color(0, 0, 0, 0.28f), ShadowSize = 12 };
        style.SetContentMarginAll(11);
        return style;
    }

    /// <summary>A tip's surface drawn into a canvas: a card a unit's leader runs to
    /// (<c>Tank3DBench.Peek</c>), as <see cref="UiTip"/>'s box looks.</summary>
    public static void DrawTip(CanvasItem on, Rect2 r)
    {
        on.DrawStyleBox(TipStyle, r);
        DrawRim(on, r, TipRim, 1.0f);
    }

    // --- type -----------------------------------------------------------------------

    /// <summary>The type scale (author 2026-11-03, after XAG 101: 18 px of glyph at
    /// 1080p on a PC is the floor, a font size of 17 in the 1600 × 900 layout): five
    /// steps by role, and a display size over them.</summary>
    /// <remarks>Caption: kinds, meta lines, kickers, key caps, a slot's label.</remarks>
    public const int Caption = 17;
    /// <remarks>Body: what is read - a tip's text, a button's word.</remarks>
    public const int Body = 18;
    /// <remarks>Subhead: a group's or a branch's name.</remarks>
    public const int Subhead = 20;
    /// <remarks>Heading: a panel's or a card's name.</remarks>
    public const int Heading = 22;
    /// <remarks>Title: a page's name.</remarks>
    public const int Title = 28;
    /// <remarks>Display: a banner over the board (the round, the winner).</remarks>
    public const int Display = 36;

    /// <summary>Screens set in the large scale (<see cref="Size"/>): the hangar's pages
    /// past the lobby. The lobby and the old build keep their sizes. The match's HUD
    /// takes the steps by name, always in the scale.</summary>
    public static bool Large;

    /// <summary>A screen's old size to its step of the scale when <see cref="Large"/>.</summary>
    public static int Size(int size) => Size(size, Large);

    /// <summary>A screen's old size to its step of the scale when <paramref name="large"/>:
    /// the match's tips are always in it, whatever screen came before.</summary>
    public static int Size(int size, bool large) => !large ? size : size switch
    {
        <= 12 => Caption,
        13 => Body,
        <= 16 => Subhead,
        <= 18 => Heading,
        <= 22 => Title,
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
    public static void DrawRim(CanvasItem on, Vector2 size, Color color, float px) =>
        DrawRim(on, new Rect2(Vector2.Zero, size), color, px);

    /// <summary>The rim of <paramref name="r"/>, as <see cref="DrawRim(CanvasItem, Vector2, Color, float)"/>:
    /// for a box drawn inside another's canvas (the match's cards).</summary>
    public static void DrawRim(CanvasItem on, Rect2 r, Color color, float px)
    {
        float t = RimLayout(on, px);
        Vector2 p = r.Position, s = r.Size;
        on.DrawRect(new Rect2(p.X, p.Y, s.X, t), color);
        on.DrawRect(new Rect2(p.X, p.Y + s.Y - t, s.X, t), color);
        on.DrawRect(new Rect2(p.X, p.Y + t, t, s.Y - 2 * t), color);
        on.DrawRect(new Rect2(p.X + s.X - t, p.Y + t, t, s.Y - 2 * t), color);
    }

    /// <summary>A box drawn into a canvas: its fill and its rim of 1 (<see cref="Line"/>
    /// unless given), square, the rim in whole screen pixels.</summary>
    public static void DrawBox(CanvasItem on, Rect2 r, Color fill, Color? rim = null, float px = 1.0f)
    {
        on.DrawRect(r, fill);
        DrawRim(on, r, rim ?? Line, px);
    }

    /// <summary>A style with a fill and nothing else, for a container or a button
    /// whose rim is drawn on it in whole screen pixels (<see cref="Rimmed"/>): a
    /// style's border is in whole layout pixels and fell on one screen pixel or two.</summary>
    public static StyleBoxFlat Fill(Color bg, float padX = 0, float padY = 0)
    {
        var box = new StyleBoxFlat { BgColor = bg };
        box.ContentMarginLeft = box.ContentMarginRight = padX;
        box.ContentMarginTop = box.ContentMarginBottom = padY;
        return box;
    }

    /// <summary>A rim drawn on <paramref name="c"/> each time it draws, in the colour
    /// and width <paramref name="rim"/> says then: a button's by its state.</summary>
    public static void Rimmed(Control c, System.Func<(Color Color, float Px)> rim) =>
        c.Draw += () =>
        {
            (Color color, float px) = rim();
            DrawRim(c, c.Size, color, px);
        };

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
    /// size before <see cref="Size"/> - or, with <paramref name="step"/>, a step of
    /// the scale taken as it is (<see cref="Body"/> and the rest: the match's HUD); with
    /// <paramref name="large"/>, in the large scale whatever <see cref="Large"/> says.</summary>
    public static Label Text(string text, int size, Color ink, bool bold = false, bool wrap = false, bool step = false,
                             bool large = false)
    {
        var l = new Label { Text = text, AutowrapMode = wrap ? TextServer.AutowrapMode.WordSmart : TextServer.AutowrapMode.Off };
        l.AddThemeFontSizeOverride("font_size", step ? size : Size(size, large || Large));
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
