using System;
using System.Collections.Generic;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The roster down the left edge, in a match (docs/hud3d-plan.md, «Ростер»,
/// 2026-10-30, the author's layout): an icon a unit of the side whose turn it
/// is, in <see cref="Roster"/>'s order.
/// <list type="bullet">
/// <item><b>The icon</b> - the class's octagon, as the hangar's tabs show it
/// (<see cref="RosterIconOf"/>; the model's own picture until 2026-11-03), the
/// class drawn from above with no icon on disk; the class under it, the state's
/// token in the corner - the same one over the hull (<see cref="Sign"/>). The one
/// in play is framed olive, the selected of every screen; the ones that cannot be
/// taken now are dimmed; a destroyed one is grey and crossed in red.</item>
/// <item><b>The card</b> - the unit's panel beside its icon in the match's tip
/// (<see cref="UiTip"/>: its wait, its beak, the icon ringed), gone as the pointer leaves
/// (the author's note, 2026-10-30). The one in play's has the turn in it.</item>
/// <item><b>A click</b> takes the unit, as the left button on its hull does
/// (<see cref="MatchTakes"/>), and the view flies to it
/// (<see cref="GlideTo"/>); on the one in play, only the flight.</item>
/// </list>
/// Outside a match there is no roster and the card stays bottom left
/// (<c>Tank3DBench.Card</c>). <c>--do roster-hover=N</c> holds the pointer on
/// the N-th icon (from 1) for a capture.
/// </summary>
public sealed partial class Tank3DBench
{
    private const float RosterLeft = 12.0f, RosterIcon = 76.0f, RosterGap = 8.0f;

    private static readonly Color RosterHull = new(0.20f, 0.21f, 0.19f);
    /// <summary>The F-key over each icon, as Baldur's Gate 3 labels its party.</summary>
    private static readonly string[] RosterKeys = { "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8" };

    private struct RosterSlot
    {
        public int Id;
        public string Tag;
        public Fate Fate;
        public Vector2I Cell;
        public int Order;
        public bool Riding;
        /// <summary>Standing, or null: the one on the rig, or the own one taken
        /// for a target (<c>_other</c>).</summary>
        public Parked? Parked;
    }

    private Control? _roster;
    private readonly RosterSlot[] _rosterSlots = new RosterSlot[8];
    private int _rosterCount;
    /// <summary>The icon under the pointer, or -1.</summary>
    private int _rosterOver = -1;
    /// <summary><c>--do roster-hover=N</c>: the icon held, from 0; -1 for none.</summary>
    private int _rosterHeld = -1;
    /// <summary>The pointer is on the column: the board under it reads nothing (<c>UnderTick</c>).</summary>
    private bool _rosterUnder;

    // class and names per tag, looked up once: the lineup's lookups allocate
    private readonly Dictionary<string, MovementProfile> _rosterClass = new();
    private readonly Dictionary<string, (string Title, string Name)> _rosterNames = new();

    private void BuildRoster(CanvasLayer layer)
    {
        if (!Match || _noUi)
            return;
        // the column takes the mouse - a click on an icon is not an order on
        // the board under it; the card beside it is drawn past its rect
        _roster = new Control
        {
            Name = "Roster",
            Position = new Vector2(RosterLeft, 0.0f),
            Size = new Vector2(RosterIcon, 5 * (RosterIcon + RosterGap)),
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        _roster.Draw += DrawRoster;
        _roster.GuiInput += RosterInput;
        layer.AddChild(_roster);
    }

    /// <summary>Every frame, before <c>AimTick</c>: the side's units into the
    /// slots, the pointer on them. Allocates nothing.</summary>
    private void RosterTick(float dt)
    {
        _rosterUnder = false;
        if (_roster is null)
            return;
        _rosterCount = 0;
        if (_side == _mt.Turn)
            RosterAdd(_unitId, _modelTag, _fate, CellHere, riding: true, null);
        foreach (Parked p in _parked)
            if (p.Side == _mt.Turn)
                RosterAdd(p.Id, p.Tag, p.Fate, p.Cell, riding: false, p);
        if (_other is { Unit: true } o && o.Side == _mt.Turn)
            RosterAdd(o.Id, o.Tag, o.Fate, o.Cell, riding: false, null);
        // in the roster's order: a handful, sorted in place
        for (int i = 1; i < _rosterCount; i++)
            for (int j = i; j > 0 && _rosterSlots[j - 1].Order > _rosterSlots[j].Order; j--)
                (_rosterSlots[j - 1], _rosterSlots[j]) = (_rosterSlots[j], _rosterSlots[j - 1]);
        _roster.Size = new Vector2(RosterIcon, Math.Max(1, _rosterCount) * (RosterIcon + RosterGap));
        // the column on the screen's middle, as many icons as it has (the
        // author's note, 2026-11-01; it stood at the top under the HUD's lines)
        float column = Math.Max(1, _rosterCount) * (RosterIcon + RosterGap) - RosterGap;
        _roster.Position = new Vector2(RosterLeft, Mathf.Round((_roster.GetViewportRect().Size.Y - column) * 0.5f));

        int over = _rosterHeld;
        if (over < 0 && _roster.GetGlobalRect().HasPoint(GetViewport().GetMousePosition()))
        {
            _rosterUnder = true;
            Vector2 local = _roster.GetLocalMousePosition();
            int i = (int)(local.Y / (RosterIcon + RosterGap));
            if (local.Y - i * (RosterIcon + RosterGap) <= RosterIcon && i < _rosterCount)
                over = i;
        }
        if (over != _rosterOver)
        {
            _rosterOver = over;
            RosterTip(over);
        }
        // the card says the turn as it goes: redrawn while up
        if (_hudTips is { Showing: true } tips && Array.IndexOf(_rosterTipKeys, tips.For) >= 0)
            tips.Body?.QueueRedraw();
        _roster.QueueRedraw();
    }

    private void RosterAdd(int id, string tag, Fate fate, Vector2I cell, bool riding, Parked? parked)
    {
        if (_rosterCount >= _rosterSlots.Length)
            return;
        _rosterSlots[_rosterCount++] = new RosterSlot
        {
            Id = id, Tag = tag, Fate = fate, Cell = cell, Riding = riding, Parked = parked,
            // the class's place, then the unit's: two of a class keep their order
            Order = Array.IndexOf(Roster, tag) * 64 + id,
        };
    }

    private MovementProfile RosterClass(in RosterSlot s)
    {
        if (s.Riding)
            return _profile;
        if (!_rosterClass.TryGetValue(s.Tag, out MovementProfile? cls))
            _rosterClass[s.Tag] = cls = ClassFor(s.Tag);
        return cls;
    }

    private (string Title, string Name) RosterNames(MovementProfile cls, string tag)
    {
        if (!_rosterNames.TryGetValue(tag, out var names))
            _rosterNames[tag] = names = PanelNames(cls, tag);
        return names;
    }

    private void RosterInput(InputEvent e)
    {
        if (e is not InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } click)
            return;
        _roster!.AcceptEvent();
        int i = (int)(click.Position.Y / (RosterIcon + RosterGap));
        if (i < 0 || i >= _rosterCount || click.Position.Y - i * (RosterIcon + RosterGap) > RosterIcon)
            return;
        RosterTake(i);
    }

    /// <summary>The icon's unit taken, as the left button on its hull takes it
    /// (<c>OrderClick</c>); the view flies to it (<see cref="GlideTo"/>, in
    /// <see cref="TakeOver"/>). On the one in play, only the flight.</summary>
    private void RosterTake(int i)
    {
        RosterSlot s = _rosterSlots[i];
        if (s.Riding)
        {
            GlideTo(_rig.Position);
            return;
        }
        if (s.Parked is { } standing)
        {
            if (MatchTakes(standing))
                TakeOver(standing);
            return;
        }
        // the own one taken for a target: back to standing, then over
        DropTarget();
        if (_parked.Count > 0 && _parked[^1].Id == s.Id && MatchTakes(_parked[^1]))
            TakeOver(_parked[^1]);
    }

    private void DrawRoster()
    {
        if (_roster is not { } c || _field is null)
            return;
        Font font = ThemeDB.FallbackFont;
        for (int i = 0; i < _rosterCount; i++)
        {
            RosterSlot s = _rosterSlots[i];
            var box = new Rect2(0.0f, i * (RosterIcon + RosterGap), RosterIcon, RosterIcon);
            bool gone = s.Fate == Fate.Destroyed;
            // the one in play, and who else could be taken now
            bool live = s.Riding || (s.Parked is { } p && Takeable(p));
            float a = live || gone ? 1.0f : 0.5f;
            // the theme's square tile (Ui); the one in play rimmed olive, 3 px - selected is
            // the olive on every screen; the side's colour stays on the models (step 4, 2026-11-03)
            Ui.DrawBox(c, box, Ui.PanelOver, s.Riding ? Ui.Accent : Ui.Line, s.Riding ? 3.0f : 1.0f);
            if (i == _rosterOver)
                c.DrawRect(box.Grow(-3.0f), new Color(1.0f, 1.0f, 1.0f, 0.06f));
            MovementProfile cls = RosterClass(s);
            int ci = Array.IndexOf(MovementProfile.All, cls);
            // the class's octagon, the hangar's tabs' (Icons/class_*.svg): one picture of a
            // class on every screen (step 4); the drawn silhouette with no icon on disk
            if (RosterIconOf(ci) is { } art)
            {
                // the octagon fills the space over the label: the icon's own margins
                // (its shield is 0.654 by 0.623 of it, QhTabPad) run past that space
                var mid = new Vector2(box.Position.X + RosterIcon * 0.5f, box.Position.Y + 6.0f + (RosterIcon - 32.0f) * 0.5f);
                Vector2 size = art.GetSize();
                Vector2 drawn = size * (RosterIconPx * 1.25f / Mathf.Max(size.X, size.Y));
                c.DrawTextureRect(art, new Rect2(mid - drawn * 0.5f, drawn), false,
                                  gone ? new Color(0.35f, 0.35f, 0.35f, 1.0f) : new Color(1.0f, 1.0f, 1.0f, a));
            }
            else
                RosterTank(c, box.Position + new Vector2(RosterIcon * 0.5f, 22.0f), cls,
                           gone ? Ui.Spent : SideInk(_mt.Turn), a);
            // the key that takes it (Tank3DBench.Acts): a disc sat on the top
            // left corner, half off the icon, as a game's hotkey badge - the
            // state's token has the other corner
            if (i < RosterKeys.Length)
            {
                const float r = 14.0f;
                Vector2 at = box.Position + new Vector2(3.0f, 3.0f);
                Color key = gone ? Ui.Spent : Ui.Ink;
                c.DrawCircle(at, r + 1.5f, new Color(key, 0.85f * a));
                c.DrawCircle(at, r, new Color(Ui.Bg, a));
                c.DrawString(font, at + new Vector2(-r, Ui.Caption * 0.36f), RosterKeys[i],
                             HorizontalAlignment.Center, 2.0f * r, Ui.Caption, new Color(key, a));
            }
            c.DrawString(font, box.Position + new Vector2(0.0f, RosterIcon - 8.0f), ci >= 0 ? ClassShort[ci] : cls.Tag,
                         HorizontalAlignment.Center, RosterIcon, Ui.Caption, new Color(gone ? Ui.Spent : Ui.Ink, a));
            if (gone)
            {
                var m = box.Grow(-12.0f);
                c.DrawLine(m.Position, m.End, Ui.Danger, 3.0f, true);
                c.DrawLine(new Vector2(m.End.X, m.Position.Y), new Vector2(m.Position.X, m.End.Y), Ui.Danger, 3.0f, true);
                continue;
            }
            Badge badge = s.Riding ? BadgeOf(_unitId, _fate, CellHere) : BadgeOf(s.Id, s.Fate, s.Cell);
            if (badge != Badge.None)
                Sign(c, badge, box.Position + new Vector2(RosterIcon - 12.0f, 12.0f), 8.0f, a: a);
        }
    }

    /// <summary>The class's octagon by its place in <see cref="MovementProfile.All"/>, as the
    /// hangar's tabs show it (<c>class_LT</c>…, <see cref="QIcon"/>), looked up once a class:
    /// the roster draws every frame, and the icon's lookup builds its key.</summary>
    private Texture2D? RosterIconOf(int ci)
    {
        if (ci < 0 || ci >= _rosterIcons.Length)
            return null;
        if (!_rosterIconsLooked[ci])
        {
            _rosterIconsLooked[ci] = true;
            _rosterIcons[ci] = QIcon($"class_{ClassShort[ci]}", RosterIconPx);
        }
        return _rosterIcons[ci];
    }

    /// <summary>The octagon's raster on a tile, px in the layout.</summary>
    private const int RosterIconPx = 48;
    private readonly Texture2D?[] _rosterIcons = new Texture2D?[MovementProfile.All.Length];
    private readonly bool[] _rosterIconsLooked = new bool[MovementProfile.All.Length];

    /// <summary>The roster's tip keys, one an icon: the card shown tells which icon is its.</summary>
    private readonly object[] _rosterTipKeys = { new(), new(), new(), new(), new(), new(), new(), new() };
    /// <summary>The icon whose card the tip holds.</summary>
    private int _rosterTipAt = -1;

    /// <summary>The card of icon <paramref name="over"/> in the match's tip (<see cref="UiTip"/>):
    /// beside the icon, its beak to it, the icon ringed, after the tips' wait; gone as the
    /// pointer leaves (the author's note, 2026-10-30). None for a destroyed one.</summary>
    private void RosterTip(int over)
    {
        if (_hudTips is null || _roster is null)
            return;
        if (over < 0 || over >= _rosterCount || _rosterSlots[over].Fate == Fate.Destroyed)
        {
            if (Array.IndexOf(_rosterTipKeys, _hudTips.For) >= 0)
            {
                _hudTips.Hold = false;
                _hudTips.Hide(null);
            }
            return;
        }
        _rosterTipAt = over;
        _hudTips.Hold = _rosterHeld >= 0;
        int i = over;
        // no ring round the icon, as on the action panel (Tank3DBench.ActTips)
        _hudTips.Show(_rosterTipKeys[i], () => RosterIconRect(i), RosterCardBody, ring: false);
    }

    private Rect2 RosterIconRect(int i) =>
        new(_roster!.GetGlobalRect().Position + new Vector2(0.0f, i * (RosterIcon + RosterGap)), RosterIcon, RosterIcon);

    /// <summary>The card's body: the unit's panel, drawn each frame the tip is up (the
    /// one in play's says its turn as it goes), the tip's own surface round it.</summary>
    private Control RosterCardBody()
    {
        // the tip's margins (Ui.TipStyle, 11) round it: the card as large as the hull's
        var c = new Control { CustomMinimumSize = new Vector2(PeekW - 22.0f, PeekH - 22.0f), MouseFilter = Control.MouseFilterEnum.Ignore };
        c.Draw += () => RosterCardDraw(c);
        return c;
    }

    private void RosterCardDraw(Control c)
    {
        int shown = _rosterTipAt;
        if (shown < 0 || shown >= _rosterCount)
            return;
        RosterSlot u = _rosterSlots[shown];
        // drawn over the tip's margins, laid out as the hull's card is
        var card = new Rect2(-11.0f, -11.0f, PeekW, PeekH);
        Font font = ThemeDB.FallbackFont;
        MovementProfile ucls = RosterClass(u);
        if (u.Riding)
        {
            if (!ReferenceEquals(_cardModel, _modelTag))
            {
                _cardModel = _modelTag;
                (_cardTitle, _cardName) = PanelNames(_profile, _modelTag);
            }
            UnitPanel(c, font, card, _profile, _cardTitle, _cardName, _side,
                      BadgeOf(_unitId, _fate, CellHere), turn: true, PanelSurface.None);
        }
        else
        {
            var (title, name) = RosterNames(ucls, u.Tag);
            UnitPanel(c, font, card, ucls, title, name, _mt.Turn, BadgeOf(u.Id, u.Fate, u.Cell), turn: false, PanelSurface.None);
        }
    }

    /// <summary>
    /// The class from above, gun up, in ink: tracks either side, the hull in
    /// the side's colour. Told apart by what a player tells them by - size by
    /// mass; a turret and a gun as long as the class hits hard, or the
    /// casemate's gun out of its front plate: the destroyer's long, the
    /// mortar's a stub with a wide muzzle.
    /// </summary>
    private static void RosterTank(Control c, Vector2 at, MovementProfile cls, Color ink, float a)
    {
        float s = 0.72f + 0.12f * cls.Mass;
        float w = 18.0f * s, h = 24.0f * s, track = 5.0f * s;
        var body = new Rect2(at.X - w * 0.5f, at.Y - h * 0.5f + 4.0f, w, h);
        var dark = new Color(RosterHull, a);
        var tracks = new Color(0.52f, 0.53f, 0.50f, a);
        var fill = new Color(ink.Darkened(0.3f), a);
        var lit = new Color(ink, a);
        var left = new Rect2(body.Position.X - track, body.Position.Y - 1.5f, track, h + 3.0f);
        var right = new Rect2(body.End.X, body.Position.Y - 1.5f, track, h + 3.0f);
        c.DrawRect(left.Grow(1.0f), dark);
        c.DrawRect(right.Grow(1.0f), dark);
        c.DrawRect(left, tracks);
        c.DrawRect(right, tracks);
        c.DrawRect(body.Grow(1.0f), dark);
        c.DrawRect(body, fill);
        if (cls.Turreted)
        {
            var hub = new Vector2(at.X, body.Position.Y + h * 0.58f);
            float gun = h * (0.45f + 0.18f * cls.Mass), bore = (1.6f + 0.5f * cls.Mass) * s;
            Gun(c, hub, gun, bore, dark, lit);
            float r = (5.0f + 1.0f * cls.Mass) * s;
            c.DrawCircle(hub, r + 1.2f, dark);
            c.DrawCircle(hub, r, lit);
        }
        else
        {
            // the fixed superstructure forward, the gun out of its plate
            var cab = new Rect2(body.Position.X + 2.0f * s, body.Position.Y + 1.0f * s, w - 4.0f * s, h * 0.6f);
            var mouth = new Vector2(at.X, cab.Position.Y + 2.0f);
            if (cls.Lobs)
            {
                Gun(c, mouth, h * 0.32f, 3.6f * s, dark, lit);
                c.DrawCircle(mouth - new Vector2(0.0f, h * 0.32f), 3.4f * s, dark);
                c.DrawCircle(mouth - new Vector2(0.0f, h * 0.32f), 2.4f * s, lit);
            }
            else
                Gun(c, mouth, h * 0.8f, 2.4f * s, dark, lit);
            c.DrawRect(cab.Grow(1.0f), dark);
            c.DrawRect(cab, lit);
        }
    }

    private static void Gun(Control c, Vector2 from, float length, float bore, Color dark, Color lit)
    {
        var to = from - new Vector2(0.0f, length);
        c.DrawLine(from, to, dark, bore + 2.4f);
        c.DrawLine(from, to - new Vector2(0.0f, -1.0f), lit, bore);
    }
}
