using System.Collections.Generic;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The act's card (docs/hud3d-plan.md, «Карточка действия»; the author's note,
/// 2026-11-01): the pointer resting on a button of the action panel brings up,
/// after the unit card's wait (<see cref="PeekDelay"/>), a window over the
/// button drawn as the unit's card is (the theme's box, Tank3DBench.Peek), and
/// joined to the icon by its leader - a slant out of the icon, then up:
/// <list type="bullet">
/// <item>the act's name and its key;</item>
/// <item>its type - the group's shape and name in the group's colour, as the
/// frame's mark (<see cref="MarkAt"/>): Движение, Основное действие, Доп. действие;</item>
/// <item>what it does, a few lines;</item>
/// <item>with the slot spent, why the button is grey.</item>
/// </list>
/// It stands in for the engine's tooltip, which the buttons no longer have.
/// <c>--do tip=NAME</c> (the icon's name: move, attack, ram…) holds it up for a
/// capture with no pointer; <c>tip=off</c> lets it go.
/// </summary>
public sealed partial class Tank3DBench
{
    private const float TipW = 340.0f;
    /// <summary>The leader's slant leg out of the icon, and its rise from the
    /// knee into the card, px.</summary>
    private const float TipLeg = 30.0f, TipRise = 16.0f;

    /// <summary>A button's card: its icon's name (the text's key), the act's
    /// name, its group - none for the corner's buttons (<see cref="TurnTipHook"/>),
    /// which carry their key here.</summary>
    private sealed class ActTip
    {
        public required Button Button;
        public required string Icon;
        public required string Name;
        public GroupArt? Group;
        public string Key = "";
    }

    /// <summary>Each group's art by its row of buttons (<see cref="Group"/>).</summary>
    private readonly Dictionary<HBoxContainer, GroupArt> _rowGroup = new();
    private readonly List<ActTip> _actTips = new();
    private Control? _actTip;
    /// <summary>The button under the pointer, by the buttons' own enter and exit.</summary>
    private Button? _tipOver;
    /// <summary>The card shown and how long the pointer has rested on it, s.</summary>
    private ActTip? _tipShown;
    private float _tipWait, _tipUp;
    /// <summary>What the card says the spent slot is for, found when the
    /// button greys, not every frame.</summary>
    private string _tipWhy = "";
    private bool _tipWasOff;
    /// <summary><c>--do tip=NAME</c>: the card held up with no pointer.</summary>
    private string? _tipHeld;

    /// <summary>The group's name on the card, by its shape - as Baldur's Gate 3
    /// names its movement, action and bonus action, not as the buttons «Ход»
    /// and «Атака» are, which the card would say twice.</summary>
    private static string GroupName(GroupShape shape) => shape switch
    {
        GroupShape.Diamond => L.T("acttips.group_move"),
        GroupShape.Circle => L.T("acttips.group_action"),
        _ => L.T("acttips.group_bonus"),
    };

    /// <summary>What each act does, by its icon's name: the rules as the bench
    /// plays them, in the player's words.</summary>
    private static readonly Dictionary<string, string> ActText = new()
    {
        ["move"] = L.T("acttips.text_move"),
        ["attack"] = L.T("acttips.text_attack"),
        ["ambush"] = L.T("acttips.text_ambush"),
        ["incendiary"] = L.T("acttips.text_incendiary"),
        ["ram"] = L.T("acttips.text_ram"),
        ["smoke"] = L.T("acttips.text_smoke"),
        ["mine"] = L.T("acttips.text_mine"),
        ["repair"] = L.T("acttips.text_repair"),
        ["winch"] = L.T("acttips.text_winch", Battle.WinchReach),
        ["douse"] = L.T("acttips.text_douse"),
        // the corner's buttons (TurnTipHook)
        ["endturn"] = L.T("acttips.text_endturn"),
        ["ready"] = L.T("acttips.text_ready"),
        ["undo"] = L.T("acttips.text_undo"),
    };

    /// <summary>A corner button's card (Tank3DBench.Undo, Match): no group, the
    /// card to the left of the button, its key given.</summary>
    private void TurnTipHook(Button b, string icon, string key)
    {
        _actTips.Add(new ActTip { Button = b, Icon = icon, Name = icon, Key = key });
        b.MouseEntered += () => _tipOver = b;
        b.MouseExited += () =>
        {
            if (_tipOver == b)
                _tipOver = null;
        };
    }

    /// <summary>The name and text a card says: «Конец хода» is «В бой» in
    /// placement.</summary>
    private (string Name, string TextKey) TipWords(ActTip tip) => tip.Icon switch
    {
        "endturn" when Deploying => (L.T("acttips.ready_name"), "ready"),
        "endturn" => (L.T("acttips.endturn_name"), "endturn"),
        "undo" => (L.T("acttips.undo_name"), "undo"),
        _ => (tip.Name, tip.Icon),
    };

    private void BuildActTip(CanvasLayer layer)
    {
        // the whole screen, as the unit's card: placed by the button it is for
        _actTip = new Control { Name = "ActTip", MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        _actTip.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _actTip.Draw += DrawActTip;
        layer.AddChild(_actTip);
    }

    /// <summary>A button's card, and the pointer's enter and exit told to it.</summary>
    private void ActTipHook(Button b, HBoxContainer row, string icon, string name)
    {
        _actTips.Add(new ActTip { Button = b, Icon = icon, Name = name, Group = _rowGroup[row] });
        b.MouseEntered += () => _tipOver = b;
        b.MouseExited += () =>
        {
            if (_tipOver == b)
                _tipOver = null;
        };
    }

    /// <summary>Every frame, from <c>ActsTick</c>: which card, how far up.
    /// Allocates only when the card or its button's state changes.</summary>
    private void ActTipTick()
    {
        if (_actTip is null || _acts is null)
            return;
        ActTip? tip = null;
        for (int i = 0; i < _actTips.Count; i++)
        {
            ActTip t = _actTips[i];
            if (_tipHeld is not null ? t.Icon == _tipHeld : t.Button == _tipOver)
            {
                tip = t;
                break;
            }
        }
        if (tip is null || (tip.Group is not null && !_acts.Visible) || !tip.Button.IsVisibleInTree())
        {
            _tipShown = null;
            _tipWait = _tipUp = 0.0f;
            _actTip.Visible = false;
            return;
        }
        if (tip != _tipShown)
        {
            _tipShown = tip;
            _tipWait = _tipUp = 0.0f;
            _tipWasOff = !tip.Button.Disabled;
        }
        // the spent slot's reason, asked when the button greys
        if (tip.Button.Disabled != _tipWasOff)
        {
            _tipWasOff = tip.Button.Disabled;
            _tipWhy = !_tipWasOff ? ""
                : tip.Group is null ? (tip.Icon == "undo" ? UndoWhy() : L.T("acttips.why_over"))
                : tip.Group.Shape == GroupShape.Diamond ? MatchMoveWhy()
                : tip.Group.Shape == GroupShape.Circle ? MatchShotWhy(CellHere) is { Length: > 0 } s ? s : L.T("acttips.why_shot_spent")
                : L.T("acttips.why_bonus_spent");
            _actTip.QueueRedraw();
        }
        float dt = (float)GetProcessDeltaTime();
        _tipWait += dt;
        // the unit card's wait, held cards at once
        if (_tipWait < PeekDelay && _tipHeld is null)
        {
            _actTip.Visible = false;
            return;
        }
        if (_tipUp < 1.0f)
        {
            _tipUp = _tipHeld is not null ? 1.0f : Mathf.Min(1.0f, _tipUp + dt / PeekFadeTime);
            _actTip.Modulate = new Color(1.0f, 1.0f, 1.0f, _tipUp);
            _actTip.QueueRedraw();
        }
        _actTip.Visible = true;
    }

    private void DrawActTip()
    {
        if (_actTip is not { } c || _tipShown is not { } tip)
            return;
        Font font = ThemeDB.FallbackFont;
        (string name, string textKey) = TipWords(tip);
        string text = ActText.TryGetValue(textKey, out string? t) ? t : "";
        const float pad = 14.0f;
        float textW = TipW - 2.0f * pad;
        float textH = text.Length > 0 ? font.GetMultilineStringSize(text, HorizontalAlignment.Left, textW, Ui.Body).Y : 0.0f;
        float whyH = _tipWhy.Length > 0 ? font.GetMultilineStringSize(_tipWhy, HorizontalAlignment.Left, textW, Ui.Body).Y + 8.0f : 0.0f;
        // the theme's type (Ui): the name a heading, the type and the key captions, the
        // text a body - a corner button's card has no type line
        float nameH = font.GetHeight(Ui.Heading), typeH = tip.Group is null ? 0.0f : 6.0f + font.GetHeight(Ui.Caption);
        float rule = pad + nameH + typeH + 10.0f, head = rule + 10.0f;
        float h = head + textH + whyH + pad;

        Rect2 b = tip.Button.GetGlobalRect();
        float left, top;
        if (tip.Group is { } group)
        {
            // The leader, as the unit card's (PeekLeader): a dot on the icon's top
            // right, a slant up and right past the frame - clear of the group's
            // mark, which stands over the frame's middle - then straight up into
            // the card, which stands over the knee, held on the screen.
            var foot = new Vector2(b.End.X - 9.0f, b.Position.Y + 9.0f);
            Vector2 knee = foot + new Vector2(TipLeg, -TipLeg);
            left = Mathf.Clamp(knee.X - TipW * 0.5f, 8.0f, Mathf.Max(8.0f, c.Size.X - TipW - 8.0f));
            top = Mathf.Max(8.0f, knee.Y - TipRise - h);
            PeekLeader(c, foot, knee, new Vector2(knee.X, top + h));
            Ui.DrawBox(c, new Rect2(left, top, TipW, h), Ui.PanelOver);
            // the group's colour down the edge, as the side's on the unit's card
            c.DrawRect(new Rect2(left + 1.0f, top + 8.0f, 3.0f, h - 16.0f), group.Ink);
        }
        else
        {
            // in the corner: the card over the button, its right edge on the
            // button's - to the left the other button stands - the leader
            // straight up out of the button's top
            left = Mathf.Max(8.0f, b.End.X - TipW);
            top = Mathf.Max(8.0f, b.Position.Y - TipRise - h);
            float x0 = b.Position.X + b.Size.X * 0.5f;
            var foot = new Vector2(x0, b.Position.Y + 6.0f);
            PeekLeader(c, foot, new Vector2(x0, b.Position.Y - TipRise * 0.5f), new Vector2(x0, top + h));
            Ui.DrawBox(c, new Rect2(left, top, TipW, h), Ui.PanelOver);
        }

        // the name, and the key on the right, on one baseline
        float line = top + pad + font.GetAscent(Ui.Heading);
        c.DrawString(font, new Vector2(left + pad, line), name, HorizontalAlignment.Left, -1, Ui.Heading, Ui.Ink);
        string key = tip.Group is null ? tip.Key : ActKeyOf(tip.Button);
        if (key.Length > 0)
        {
            string says = L.T("acttips.key", key);
            float w = font.GetStringSize(says, HorizontalAlignment.Left, -1, Ui.Caption).X;
            c.DrawString(font, new Vector2(left + TipW - pad - w, line), says, HorizontalAlignment.Left, -1, Ui.Caption, Ui.Muted);
        }
        if (tip.Group is { } g)
        {
            // the type: the group's shape and name in its colour
            float typeLine = top + pad + nameH + 6.0f + font.GetAscent(Ui.Caption);
            MarkAt(c, new Vector2(left + pad + 7.0f, typeLine - Ui.Caption * 0.33f), g.Shape, g.Ink, true, 6.0f);
            c.DrawString(font, new Vector2(left + pad + 20.0f, typeLine), GroupName(g.Shape),
                         HorizontalAlignment.Left, -1, Ui.Caption, g.Ink);
        }
        c.DrawRect(new Rect2(left + 12.0f, top + rule, TipW - 24.0f, Ui.RimLayout(c, 1)), Ui.Line);
        // what it does; why it is grey
        float y = top + head + font.GetAscent(Ui.Body);
        if (text.Length > 0)
            c.DrawMultilineString(font, new Vector2(left + pad, y), text, HorizontalAlignment.Left, textW, Ui.Body, -1, Ui.Ink);
        if (_tipWhy.Length > 0)
            c.DrawMultilineString(font, new Vector2(left + pad, y + textH + 8.0f), _tipWhy, HorizontalAlignment.Left, textW, Ui.Body, -1,
                                  AimPierce);
    }

    /// <summary>The key a button's cap says, or empty.</summary>
    private string ActKeyOf(Button b)
    {
        for (int i = 0; i < _actKeys.Count; i++)
            if (_actKeys[i].Button == b)
                return _actKeys[i].Cap.Visible ? _actKeys[i].Cap.Text : "";
        return "";
    }
}
