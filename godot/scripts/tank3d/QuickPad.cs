using System;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// One drawn piece of the quick battle's screens (<c>Tank3DBench.Quick</c>),
/// in the prototype's look (the artifact «Сборка отряда: быстрый бой»): a hex
/// - a class's button, a level's node, a budget's pip - or a box - a button,
/// a fact of the battle, the machine's picture - with its rim, its fill, its
/// text and an optional picture, drawn on a hover too. A click calls
/// <see cref="Clicked"/>; the pointer resting on it calls <see cref="Over"/>,
/// which the screens answer with their own tooltip.
/// </summary>
public partial class QuickPad : Control
{
    public enum Shape { Hex, Box }

    public Shape Form = Shape.Box;
    public string Text = "";
    public int FontSize = 15;
    public bool Bold;
    public Color Rim, Fill, Ink;
    /// <summary>The fill under the pointer; null keeps <see cref="Fill"/>.</summary>
    public Color? HoverFill;
    /// <summary>The rim under the pointer; null keeps <see cref="Rim"/>.</summary>
    public Color? HoverRim;
    /// <summary>The rim's width, px: the hex's ring, the box's border.</summary>
    public float RimWidth = 1.0f;
    public bool Disabled;
    /// <summary>The rim drawn in dashes: a thing that is there but does not work yet.</summary>
    public bool Dashed;
    public Texture2D? Picture;
    /// <summary>The picture's box inside the pad, px; zero fits the pad.</summary>
    public Vector2 PictureSize;
    /// <summary>A soft oval under the picture, the prototype's stage light.</summary>
    public Color? Glow;
    public HorizontalAlignment Align = HorizontalAlignment.Center;

    public event Action? Clicked;
    /// <summary>The pointer came on (true) or went off (false).</summary>
    public event Action<QuickPad, bool>? Over;

    private bool _hover;

    private static readonly Color FocusRing = new("A3B65E");

    /// <summary>The bold face: the fallback font emboldened, the display text's.</summary>
    public static readonly Font BoldFont = new FontVariation { BaseFont = ThemeDB.FallbackFont, VariationEmbolden = 0.9f };

    public static Font FontOf(bool bold) => bold ? BoldFont : ThemeDB.FallbackFont;

    /// <summary>White in the middle to nothing at 72% of the way out, tinted by <see cref="Glow"/>.</summary>
    private static readonly GradientTexture2D GlowTexture = new()
    {
        Width = 128, Height = 128, Fill = GradientTexture2D.FillEnum.Radial,
        FillFrom = new Vector2(0.5f, 0.5f), FillTo = new Vector2(1.0f, 0.5f),
        Gradient = new Gradient { Offsets = new[] { 0.0f, 0.72f }, Colors = new[] { Colors.White, new Color(1, 1, 1, 0) } },
    };

    public QuickPad()
    {
        MouseFilter = MouseFilterEnum.Stop;
        MouseEntered += () => { _hover = true; QueueRedraw(); Over?.Invoke(this, true); };
        MouseExited += () => { _hover = false; QueueRedraw(); Over?.Invoke(this, false); };
        // the keyboard reaches what the pointer does: Tab and the arrows move,
        // the focus raises the tip, Enter or Space clicks
        FocusEntered += () => { QueueRedraw(); Over?.Invoke(this, true); };
        FocusExited += () => { QueueRedraw(); Over?.Invoke(this, false); };
    }

    public override void _Ready() =>
        FocusMode = MouseFilter == MouseFilterEnum.Ignore ? FocusModeEnum.None : FocusModeEnum.All;

    /// <summary>A box that fits its text with <paramref name="pad"/> round it.</summary>
    public QuickPad Fit(Vector2 pad)
    {
        Vector2 size = FontOf(Bold).GetMultilineStringSize(Text, HorizontalAlignment.Left, -1, FontSize);
        CustomMinimumSize = new Vector2(Mathf.Max(CustomMinimumSize.X, size.X + 2 * pad.X),
                                        Mathf.Max(CustomMinimumSize.Y, size.Y + 2 * pad.Y));
        return this;
    }

    public override void _GuiInput(InputEvent e)
    {
        bool hit = e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true }
                   || (e.IsActionPressed("ui_accept") && !e.IsEcho());
        if (hit && !Disabled && Clicked is not null)
        {
            AcceptEvent();
            Clicked.Invoke();
        }
    }

    public override void _Draw()
    {
        bool focus = HasFocus();
        bool lit = (_hover || focus) && !Disabled && Clicked is not null;
        Color rim = lit && HoverRim is { } hr ? hr : Rim;
        Color fill = lit && HoverFill is { } hf ? hf : Fill;
        float a = Disabled ? 0.4f : 1.0f;
        Vector2 s = Size;
        if (Form == Shape.Hex && Dashed)
        {
            Vector2[] h = Hex(Vector2.Zero, s);
            DrawColoredPolygon(h, new Color(fill, fill.A * a));
            for (int k = 0; k < 6; k++)
                DrawDashedLine(h[k], h[(k + 1) % 6], new Color(rim, rim.A * a), RimWidth * 1.5f, 4.0f);
        }
        else if (Form == Shape.Hex)
        {
            DrawColoredPolygon(Hex(Vector2.Zero, s), new Color(rim, rim.A * a));
            var inset = new Vector2(RimWidth * 2.0f, RimWidth * 2.0f);
            DrawColoredPolygon(Hex(inset, s - 2 * inset), new Color(fill, fill.A * a));
        }
        else
        {
            DrawRect(new Rect2(Vector2.Zero, s), new Color(fill, fill.A * a));
            if (RimWidth > 0)
                DrawRect(new Rect2(Vector2.Zero, s), new Color(rim, rim.A * a), false, RimWidth);
        }
        if (focus)
        {
            // the keyboard's place, on any pad: the prototype's olive ring
            if (Form == Shape.Hex)
            {
                Vector2[] h = Hex(Vector2.Zero, s);
                DrawPolyline(new[] { h[0], h[1], h[2], h[3], h[4], h[5], h[0] }, FocusRing, 2.0f);
            }
            else
                DrawRect(new Rect2(Vector2.One, s - 2 * Vector2.One), FocusRing, false, 2.0f);
        }
        if (Glow is { } glow)
        {
            // the prototype's radial-gradient(ellipse 55% 42% at 50% 72%): an
            // oval of light low in the box, gone at 72% of its reach
            var reach = new Vector2(s.X * 0.55f, s.Y * 0.42f);
            DrawTextureRect(GlowTexture, new Rect2(new Vector2(s.X * 0.5f, s.Y * 0.72f) - reach, reach * 2.0f), false,
                            new Color(glow, glow.A * a));
        }
        if (Picture is { } pic)
        {
            Vector2 box = PictureSize == Vector2.Zero ? s : PictureSize;
            Vector2 ps = pic.GetSize();
            float k = Mathf.Min(box.X / Mathf.Max(ps.X, 1), box.Y / Mathf.Max(ps.Y, 1));
            Vector2 at = (s - ps * k) * 0.5f;
            DrawTextureRect(pic, new Rect2(at, ps * k), false, new Color(1, 1, 1, a));
        }
        if (Text.Length > 0)
        {
            Font font = FontOf(Bold);
            string[] lines = Text.Split('\n');
            float line = font.GetHeight(FontSize);
            float top = (s.Y - line * lines.Length) * 0.5f + font.GetAscent(FontSize);
            float pad = Align == HorizontalAlignment.Center ? 0.0f : 12.0f;
            for (int i = 0; i < lines.Length; i++)
                DrawString(font, new Vector2(pad, top + i * line), lines[i], Align, s.X - 2 * pad, FontSize, new Color(Ink, Ink.A * a));
        }
    }

    /// <summary>The prototype's hex: flat top and bottom, points left and right.</summary>
    private static Vector2[] Hex(Vector2 at, Vector2 s) => new[]
    {
        at + new Vector2(s.X * 0.25f, 0), at + new Vector2(s.X * 0.75f, 0), at + new Vector2(s.X, s.Y * 0.5f),
        at + new Vector2(s.X * 0.75f, s.Y), at + new Vector2(s.X * 0.25f, s.Y), at + new Vector2(0, s.Y * 0.5f),
    };
}
