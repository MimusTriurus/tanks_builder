using System.Collections.Generic;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The act's card (docs/hud3d-plan.md, «Карточка действия»; the author's note,
/// 2026-11-01): the pointer resting on a button of the action panel brings up,
/// after the unit card's wait (<see cref="PeekDelay"/>), a window over the
/// button drawn as the unit's card is (<c>_peekBox</c>, Tank3DBench.Peek), and
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
    private const float TipW = 300.0f;
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
        GroupShape.Diamond => "Движение",
        GroupShape.Circle => "Основное действие",
        _ => "Доп. действие",
    };

    /// <summary>What each act does, by its icon's name: the rules as the bench
    /// plays them, in the player's words.</summary>
    private static readonly Dictionary<string, string> ActText = new()
    {
        ["move"] = "Танк едет по синей зоне. Первый щелчок по клетке выбирает её и открывает розу разворота, второй — едет. "
                   + "Если разворота на клетке не остаётся, едет с первого щелчка. После атаки или доп. действия ехать может только LT.",
        ["attack"] = "Выстрел по цели на оси огня. Оранжевым залиты гексы, куда орудие бьёт с места. "
                     + "На цель вне осей наведи — покажет, откуда попадёт после хода. Свой танк — цель только в этом режиме; "
                     + "ПКМ по врагу стреляет и без него.",
        ["ambush"] = "Орудие ждёт на оси: танк, въехавший на неё, получает выстрел. Ставится последней — ход на этом кончается. "
                     + "MT держит сектор из двух соседних осей, каземат — ось корпуса. Снимается любым приказом юниту.",
        ["incendiary"] = "Следующий выстрел — зажигательный. Непробитый корпус загорается, лес, где остановился снаряд, тоже. "
                         + "Пробитый подбит, как обычно.",
        ["ram"] = "Въехать в танк рядом и сдвинуть его на гекс, если хватает массы; свой тоже можно. "
                  + "Со стеной перед носом щелчок по гексу за ней проламывает стену.",
        ["smoke"] = "Дымовая шашка на соседний гекс. Дым закрывает линии огня, как лес, и держится два раунда "
                    + "или пока в него не въедут.",
        ["mine"] = "Мина на соседний гекс, живая сразу: танк, въехавший на неё, подбит.",
        ["repair"] = "Свой подбитый танк рядом снова в бою.",
        ["winch"] = $"Танк в {Battle.WinchReach} гексах по оси подтягивается на гекс ближе — со всем, что этот гекс с ним сделает.",
        ["douse"] = "Потушить пожар на своём корпусе. Тратит доп. действие, выстрел остаётся.",
        // the corner's buttons (TurnTipHook)
        ["endturn"] = "Передать ход другой стороне. Чего юнит не сделал в этом ходу, то пропадает. "
                      + "Если приказ ждёт подтверждения — клетка выбрана, ось засады наведена, — Enter сперва его подтверждает.",
        ["ready"] = "Расстановка закончена: экран уходит другой стороне, а после её «В бой» начинается первый раунд.",
        ["undo"] = "Вернуть юнит туда, откуда он поехал, с тем же курсом, — ход можно сделать заново. "
                   + "Только пока после хода ничего не сделано и юнит цел. Ctrl+Backspace — партия заново.",
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
        "endturn" when Deploying => ("В бой", "ready"),
        "endturn" => ("Конец хода", "endturn"),
        "undo" => ("Отменить ход", "undo"),
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
                : tip.Group is null ? (tip.Icon == "undo" ? UndoWhy() : "партия окончена")
                : tip.Group.Shape == GroupShape.Diamond ? MatchMoveWhy()
                : tip.Group.Shape == GroupShape.Circle ? MatchShotWhy(CellHere) is { Length: > 0 } s ? s : "атака этого хода уже сделана"
                : "доп. действие этого хода уже сделано";
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
        if (_actTip is not { } c || _peekBox is null || _tipShown is not { } tip)
            return;
        Font font = ThemeDB.FallbackFont;
        (string name, string textKey) = TipWords(tip);
        string text = ActText.TryGetValue(textKey, out string? t) ? t : "";
        const float pad = 14.0f;
        float textW = TipW - 2.0f * pad;
        float textH = text.Length > 0 ? font.GetMultilineStringSize(text, HorizontalAlignment.Left, textW, 13).Y : 0.0f;
        float whyH = _tipWhy.Length > 0 ? font.GetMultilineStringSize(_tipWhy, HorizontalAlignment.Left, textW, 13).Y + 8.0f : 0.0f;
        // a corner button's card has no type line
        float head = tip.Group is null ? 58.0f : 78.0f;
        float h = head + textH + whyH + 10.0f;

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
            c.DrawStyleBox(_peekBox, new Rect2(left, top, TipW, h));
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
            c.DrawStyleBox(_peekBox, new Rect2(left, top, TipW, h));
        }

        // the name, and the key on the right
        c.DrawString(font, new Vector2(left + pad, top + 28.0f), name, HorizontalAlignment.Left, -1, 20, CardPale);
        string key = tip.Group is null ? tip.Key : ActKeyOf(tip.Button);
        if (key.Length > 0)
        {
            string says = $"клавиша {key}";
            float w = font.GetStringSize(says, HorizontalAlignment.Left, -1, 13).X;
            c.DrawString(font, new Vector2(left + TipW - pad - w, top + 26.0f), says, HorizontalAlignment.Left, -1, 13, CardDim);
        }
        float y = top + head;
        if (tip.Group is { } g)
        {
            // the type: the group's shape and name in its colour
            MarkAt(c, new Vector2(left + pad + 6.0f, top + 48.0f), g.Shape, g.Ink, true, 5.5f);
            c.DrawString(font, new Vector2(left + pad + 18.0f, top + 53.0f), GroupName(g.Shape),
                         HorizontalAlignment.Left, -1, 13, g.Ink);
        }
        c.DrawLine(new Vector2(left + 12.0f, y - 18.0f), new Vector2(left + TipW - 12.0f, y - 18.0f),
                   new Color(0.34f, 0.35f, 0.31f), 1.0f);
        // what it does; why it is grey
        if (text.Length > 0)
            c.DrawMultilineString(font, new Vector2(left + pad, y), text, HorizontalAlignment.Left, textW, 13, -1, CardPale);
        if (_tipWhy.Length > 0)
            c.DrawMultilineString(font, new Vector2(left + pad, y + textH + 8.0f), _tipWhy, HorizontalAlignment.Left, textW, 13, -1,
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
