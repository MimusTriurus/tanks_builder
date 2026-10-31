using System;
using System.Collections.Generic;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The roster down the left edge, in a match (docs/hud3d-plan.md, «Ростер»,
/// 2026-10-30, the author's layout): an icon a unit of the side whose turn it
/// is, in <see cref="Roster"/>'s order.
/// <list type="bullet">
/// <item><b>The icon</b> - the unit's own model rendered
/// (<c>Tank3DBench.RosterArt</c>), the class drawn from above until it is
/// in; the class under it, the state's token in the corner - the same one
/// over the hull (<see cref="Sign"/>). The one in play is framed in the
/// side's colour; the ones that cannot be taken now are dimmed; a destroyed
/// one is grey and crossed.</item>
/// <item><b>The card</b> - the unit's panel beside its icon, only while the
/// pointer rests on the icon (<see cref="RosterDwell"/>), gone as it leaves
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
    private const float RosterLeft = 12.0f, RosterTop = 64.0f, RosterIcon = 76.0f, RosterGap = 8.0f;
    /// <summary>How long the pointer rests on an icon before its card shows, s.</summary>
    private const float RosterDwell = 0.4f;
    /// <summary>The card stands this far right of the column.</summary>
    private const float RosterCardOff = 18.0f;

    private static readonly Color RosterHull = new(0.20f, 0.21f, 0.19f);
    private static readonly Color RosterGrey = new(0.48f, 0.48f, 0.46f);
    private static readonly Color RosterCross = new(0.89f, 0.29f, 0.29f);

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
    private StyleBoxFlat? _rosterBox, _rosterChosen;
    private readonly RosterSlot[] _rosterSlots = new RosterSlot[8];
    private int _rosterCount;
    /// <summary>The icon under the pointer and how long it has been, or -1.</summary>
    private int _rosterOver = -1;
    private float _rosterDwelt;
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
        _rosterBox = new StyleBoxFlat
        {
            BgColor = new Color(0.08f, 0.085f, 0.075f, 0.88f),
            BorderColor = new Color(0.34f, 0.35f, 0.31f),
        };
        _rosterBox.SetBorderWidthAll(1);
        _rosterBox.SetCornerRadiusAll(8);
        _rosterChosen = (StyleBoxFlat)_rosterBox.Duplicate();
        _rosterChosen.SetBorderWidthAll(3);
        // the column takes the mouse - a click on an icon is not an order on
        // the board under it; the card beside it is drawn past its rect
        _roster = new Control
        {
            Name = "Roster",
            Position = new Vector2(RosterLeft, RosterTop),
            Size = new Vector2(RosterIcon, 5 * (RosterIcon + RosterGap)),
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        _roster.Draw += DrawRoster;
        _roster.GuiInput += RosterInput;
        RosterArt();
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
            _rosterDwelt = 0.0f;
        }
        _rosterDwelt = _rosterHeld >= 0 ? RosterDwell : _rosterDwelt + dt;
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
        if (_roster is not { } c || _rosterBox is null || _field is null)
            return;
        Font font = ThemeDB.FallbackFont;
        Color ink = SideInk(_mt.Turn);
        _rosterChosen!.BorderColor = ink;
        for (int i = 0; i < _rosterCount; i++)
        {
            RosterSlot s = _rosterSlots[i];
            var box = new Rect2(0.0f, i * (RosterIcon + RosterGap), RosterIcon, RosterIcon);
            bool gone = s.Fate == Fate.Destroyed;
            // the one in play, and who else could be taken now
            bool live = s.Riding || (s.Parked is { } p && Takeable(p));
            float a = live || gone ? 1.0f : 0.5f;
            c.DrawStyleBox(s.Riding ? _rosterChosen : _rosterBox, box);
            if (i == _rosterOver)
                c.DrawRect(box.Grow(-3.0f), new Color(1.0f, 1.0f, 1.0f, 0.06f));
            MovementProfile cls = RosterClass(s);
            // the model's own picture (Tank3DBench.RosterArt); the silhouette until it is in
            if (_rosterArt.TryGetValue((s.Tag, s.Riding ? _side : _mt.Turn), out Texture2D? art))
            {
                // fitted into the space over the label, its own proportions kept
                var room = new Rect2(box.Position + new Vector2(7.0f, 7.0f), RosterIcon - 14.0f, RosterIcon - 30.0f);
                Vector2 size = art.GetSize();
                float fit = Mathf.Min(room.Size.X / size.X, room.Size.Y / size.Y);
                Vector2 drawn = size * fit;
                c.DrawTextureRect(art, new Rect2(room.Position + (room.Size - drawn) * 0.5f, drawn), false,
                                  gone ? new Color(0.35f, 0.35f, 0.35f, 1.0f) : new Color(1.0f, 1.0f, 1.0f, a));
            }
            else
                RosterTank(c, box.Position + new Vector2(RosterIcon * 0.5f, 22.0f), cls,
                           gone ? RosterGrey : ink, a);
            int ci = Array.IndexOf(MovementProfile.All, cls);
            c.DrawString(font, box.Position + new Vector2(0.0f, RosterIcon - 9.0f), ci >= 0 ? ClassShort[ci] : cls.Tag,
                         HorizontalAlignment.Center, RosterIcon, 12, new Color(gone ? RosterGrey : CardPale, a));
            if (gone)
            {
                var m = box.Grow(-12.0f);
                c.DrawLine(m.Position, m.End, RosterCross, 3.0f, true);
                c.DrawLine(new Vector2(m.End.X, m.Position.Y), new Vector2(m.Position.X, m.End.Y), RosterCross, 3.0f, true);
                continue;
            }
            Badge badge = s.Riding ? BadgeOf(_unitId, _fate, CellHere) : BadgeOf(s.Id, s.Fate, s.Cell);
            if (badge != Badge.None)
                Sign(c, badge, box.Position + new Vector2(RosterIcon - 12.0f, 12.0f), 8.0f, a: a);
        }

        // the card: only while the pointer rests on an icon, gone as it leaves
        int shown = _rosterOver >= 0 && _rosterDwelt >= RosterDwell ? _rosterOver : -1;
        if (shown < 0 || shown >= _rosterCount || _rosterSlots[shown].Fate == Fate.Destroyed)
            return;
        RosterSlot u = _rosterSlots[shown];
        float rowMid = shown * (RosterIcon + RosterGap) + RosterIcon * 0.5f;
        float top = Mathf.Clamp(rowMid - 26.0f, -RosterTop + 8.0f,
                                Mathf.Max(-RosterTop + 8.0f, c.GetViewportRect().Size.Y - RosterTop - PeekH - 8.0f));
        var card = new Rect2(RosterIcon + RosterCardOff, top, PeekW, PeekH);
        // a short leader from the icon into the card's title
        c.DrawLine(new Vector2(RosterIcon + 2.0f, rowMid), new Vector2(card.Position.X, rowMid), PeekShade, 4.0f);
        c.DrawLine(new Vector2(RosterIcon + 2.0f, rowMid), new Vector2(card.Position.X, rowMid), PeekLine, 1.6f);
        MovementProfile ucls = RosterClass(u);
        if (u.Riding)
        {
            if (!ReferenceEquals(_cardModel, _modelTag))
            {
                _cardModel = _modelTag;
                (_cardTitle, _cardName) = PanelNames(_profile, _modelTag);
            }
            UnitPanel(c, font, card, _profile, _cardTitle, _cardName, _side,
                      BadgeOf(_unitId, _fate, CellHere), turn: true);
        }
        else
        {
            var (title, name) = RosterNames(ucls, u.Tag);
            UnitPanel(c, font, card, ucls, title, name, _mt.Turn, BadgeOf(u.Id, u.Fate, u.Cell), turn: false);
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
