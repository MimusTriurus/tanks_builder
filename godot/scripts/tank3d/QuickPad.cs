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
    /// <summary>Hex: a level's node, a pip; Box: a button; Shield: the class
    /// icons' octagon (Icons/class_*.svg), round a class's icon.</summary>
    public enum Shape { Hex, Box, Shield }

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
    /// <summary>The picture's opacity: a level not yet open shows its upgrade dimmed.</summary>
    public float PictureAlpha = 1.0f;
    /// <summary>A soft oval under the picture, the prototype's stage light.</summary>
    public Color? Glow;
    /// <summary>The oval's centre, as shares of the pad's size: the prototype's 50% 72%;
    /// the hangar's podium sets it under its machine in front.</summary>
    public Vector2 GlowAt = new(0.5f, 0.72f);
    public HorizontalAlignment Align = HorizontalAlignment.Center;
    /// <summary>A word in a disc on the box's right edge: a node's price.</summary>
    public string Badge = "";
    public int BadgeSize = 13;
    public Color BadgeRim, BadgeFill, BadgeInk;

    /// <summary>A ring just outside the shape, in this colour: the pad an open tip tells
    /// of, paired with the tip by its rim's colour; null draws none.</summary>
    public Color? Mark;

    public event Action? Clicked;
    /// <summary>The right button, or Delete / Backspace on the keyboard: the undoing
    /// click, kept off the left one (a bought level is dropped by it, not by a click
    /// that looks like the buying one).</summary>
    public event Action? Dropped;
    /// <summary>The pointer came on (true) or went off (false).</summary>
    public event Action<QuickPad, bool>? Over;

    private bool _hover;

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

    /// <summary>Whether the keyboard stops on it; false for a pad that only frames
    /// others (the hangar's podium), which would otherwise wear the focus ring when clicked.</summary>
    public bool Focusable = true;

    public override void _Ready() =>
        FocusMode = MouseFilter == MouseFilterEnum.Ignore || !Focusable ? FocusModeEnum.None : FocusModeEnum.All;

    /// <summary>A box that fits its text with <paramref name="pad"/> round it.</summary>
    public QuickPad Fit(Vector2 pad)
    {
        Vector2 size = Ui.FontOf(Bold).GetMultilineStringSize(Text, HorizontalAlignment.Left, -1, FontSize);
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
            return;
        }
        bool drop = e is InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: true }
                    || e is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Delete or Key.Backspace };
        if (drop && !Disabled && Dropped is not null)
        {
            AcceptEvent();
            Dropped.Invoke();
        }
    }

    public override void _Draw()
    {
        bool focus = HasFocus();
        bool lit = (_hover || focus) && !Disabled && (Clicked is not null || Dropped is not null);
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
        else if (Form == Shape.Shield)
        {
            Vector2[] o = Shield(Vector2.Zero, s);
            DrawColoredPolygon(o, new Color(fill, fill.A * a));
            if (RimWidth > 0)
                DrawPolyline(new[] { o[0], o[1], o[2], o[3], o[4], o[5], o[6], o[7], o[0] }, new Color(rim, rim.A * a), RimWidth, true);
        }
        else
        {
            DrawRect(new Rect2(Vector2.Zero, s), new Color(fill, fill.A * a));
            if (RimWidth > 0)
                Ui.DrawRim(this, s, new Color(rim, rim.A * a), RimWidth);
        }
        if (focus)
        {
            // the keyboard's place, on any pad: the prototype's olive ring
            if (Form == Shape.Hex)
            {
                Vector2[] h = Hex(Vector2.Zero, s);
                DrawPolyline(new[] { h[0], h[1], h[2], h[3], h[4], h[5], h[0] }, Ui.Accent, 2.0f);
            }
            else if (Form == Shape.Shield)
            {
                Vector2[] o = Shield(Vector2.Zero, s);
                DrawPolyline(new[] { o[0], o[1], o[2], o[3], o[4], o[5], o[6], o[7], o[0] }, Ui.Accent, 2.0f);
            }
            else
                DrawRect(new Rect2(Vector2.One, s - 2 * Vector2.One), Ui.Accent, false, 2.0f);
        }
        if (Mark is { } mark)
        {
            // outside the shape, clear of its own rim: the node's state stays as it is
            float w = Ui.RimLayout(this, 2);
            var o = new Vector2(3 + w * 0.5f, 3 + w * 0.5f);
            if (Form == Shape.Hex)
            {
                // the hex grown by o along its width, by its slope's share along its height
                Vector2[] h = Hex(-o, s + 2 * o);
                DrawPolyline(new[] { h[0], h[1], h[2], h[3], h[4], h[5], h[0] }, mark, w, true);
            }
            else if (Form == Shape.Shield)
            {
                Vector2[] g = Shield(-o, s + 2 * o);
                DrawPolyline(new[] { g[0], g[1], g[2], g[3], g[4], g[5], g[6], g[7], g[0] }, mark, w, true);
            }
            else
                DrawRect(new Rect2(-o, s + 2 * o), mark, false, w);
        }
        if (Glow is { } glow)
        {
            // the prototype's radial-gradient(ellipse 55% 42% at 50% 72%): an
            // oval of light low in the box, gone at 72% of its reach
            var reach = new Vector2(s.X * 0.55f, s.Y * 0.42f);
            DrawTextureRect(GlowTexture, new Rect2(s * GlowAt - reach, reach * 2.0f), false,
                            new Color(glow, glow.A * a));
        }
        if (Picture is { } pic)
        {
            Vector2 box = PictureSize == Vector2.Zero ? s : PictureSize;
            Vector2 ps = pic.GetSize();
            float k = Mathf.Min(box.X / Mathf.Max(ps.X, 1), box.Y / Mathf.Max(ps.Y, 1));
            Vector2 at = (s - ps * k) * 0.5f;
            DrawTextureRect(pic, new Rect2(at, ps * k), false, new Color(1, 1, 1, a * PictureAlpha));
        }
        if (Text.Length > 0)
        {
            Font font = Ui.FontOf(Bold);
            string[] lines = Text.Split('\n');
            float line = font.GetHeight(FontSize);
            float top = (s.Y - line * lines.Length) * 0.5f + font.GetAscent(FontSize);
            float pad = Align == HorizontalAlignment.Center ? 0.0f : 12.0f;
            for (int i = 0; i < lines.Length; i++)
                DrawString(font, new Vector2(pad, top + i * line), lines[i], Align, s.X - 2 * pad, FontSize, new Color(Ink, Ink.A * a));
        }
        if (Badge.Length > 0)
        {
            // on the right point of the hex, mostly beside it: read with the node, not inside
            // it, and clear of the name and kind over and under it (on the top corner it
            // covered the kind of the node above)
            Font font = Ui.FontOf(true);
            float r = font.GetHeight(BadgeSize) * 0.62f;
            var at = new Vector2(s.X + r * 0.35f, s.Y * 0.5f);
            DrawCircle(at, r, new Color(BadgeFill, BadgeFill.A * a));
            DrawArc(at, r, 0, Mathf.Tau, 32, new Color(BadgeRim, BadgeRim.A * a), Ui.RimLayout(this, 1.5f), true);
            float w = font.GetStringSize(Badge, HorizontalAlignment.Left, -1, BadgeSize).X;
            DrawString(font, new Vector2(at.X - w * 0.5f, at.Y - font.GetHeight(BadgeSize) * 0.5f + font.GetAscent(BadgeSize)),
                       Badge, HorizontalAlignment.Left, -1, BadgeSize, new Color(BadgeInk, BadgeInk.A * a));
        }
    }

    /// <summary>The class icons' frame octagon (Icons/out/src/build_classes.sh,
    /// <c>oct</c>): a wide top, sides from 15% to 76% down, a narrow bottom.</summary>
    private static Vector2[] Shield(Vector2 at, Vector2 s) => new[]
    {
        at + new Vector2(s.X * 0.235f, 0), at + new Vector2(s.X * 0.765f, 0),
        at + new Vector2(s.X, s.Y * 0.15f), at + new Vector2(s.X, s.Y * 0.765f),
        at + new Vector2(s.X * 0.68f, s.Y), at + new Vector2(s.X * 0.32f, s.Y),
        at + new Vector2(0, s.Y * 0.765f), at + new Vector2(0, s.Y * 0.15f),
    };

    /// <summary>The prototype's hex: flat top and bottom, points left and right.</summary>
    private static Vector2[] Hex(Vector2 at, Vector2 s) => new[]
    {
        at + new Vector2(s.X * 0.25f, 0), at + new Vector2(s.X * 0.75f, 0), at + new Vector2(s.X, s.Y * 0.5f),
        at + new Vector2(s.X * 0.75f, s.Y), at + new Vector2(s.X * 0.25f, s.Y), at + new Vector2(0, s.Y * 0.5f),
    };
}
