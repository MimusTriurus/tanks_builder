using Godot;

namespace TankSpriteTest;

/// <summary>
/// The action panel, bottom left by the unit's card (docs/hud3d-plan.md,
/// «Панель действий», 2026-10-30): what is left of the chosen unit's turn, in
/// a match. GDD match.md «Структура хода» (U-22): the move, then two slots,
/// one of each - the <b>attack</b> (the shot) and the <b>bonus act</b> (the
/// ram, putting a fire out).
/// <list type="bullet">
/// <item><b>Two plates, the slots</b>: lit while the slot is to be had, dimmed
/// once spent. They are not buttons - the move and the shot stay the mouse's,
/// as they were: the left button on a cell, the right one on a tank.</item>
/// <item><b>Buttons only for the bonus acts</b>, which the mouse cannot say on
/// its own: <b>Таран</b> - a mode (Tab too), the left button on a tank then
/// rams it as the right one does (<c>AimAt</c>); <b>Тушить</b> - at once
/// (<see cref="Douse"/>).</item>
/// <item><b>What may not be done is not there</b> (the author's choice, as
/// with the roses): no bonus buttons once the bonus act is spent, no
/// <b>Тушить</b> on a hull that is not burning.</item>
/// <item>Keys <b>1</b> (the ram) and <b>2</b> (put it out), free in a match -
/// they are the bench's hits off it; Esc leaves the ram.</item>
/// </list>
/// </summary>
public sealed partial class Tank3DBench
{
    private HBoxContainer? _acts;
    private PanelContainer? _slotAttack, _slotBonus;
    private Label? _slotAttackText, _slotBonusText;
    private Button? _actRam, _actDouse;
    /// <summary>The slots as last drawn: the plates are restyled when they change, not every frame.</summary>
    private int _slotsShown = -1;

    private const string SlotAttack = "●  Атака", SlotAttackSpent = "✓  Атака";
    private const string SlotBonus = "●  Доп. действие", SlotBonusSpent = "✓  Доп. действие";
    private static readonly Color SlotDim = new(0.42f, 0.43f, 0.40f);

    private void BuildActs(CanvasLayer layer)
    {
        _acts = new HBoxContainer
        {
            Name = "Acts",
            AnchorTop = 1.0f, AnchorBottom = 1.0f,
            OffsetLeft = 12.0f + PeekW + 8.0f, OffsetTop = -12.0f - 44.0f, OffsetBottom = -12.0f,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Visible = false,
        };
        _acts.AddThemeConstantOverride("separation", 6);
        (_slotAttack, _slotAttackText) = Slot(SlotAttack);
        (_slotBonus, _slotBonusText) = Slot(SlotBonus);
        _actRam = ActButton("Таран   1", toggle: true, RamToggle);
        _actDouse = ActButton("Тушить   2", toggle: false, Douse);
        layer.AddChild(_acts);
    }

    private (PanelContainer, Label) Slot(string text)
    {
        var plate = new PanelContainer
        {
            CustomMinimumSize = new Vector2(0.0f, 44.0f),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        var label = new Label
        {
            Text = text,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        label.AddThemeFontSizeOverride("font_size", 16);
        plate.AddChild(label);
        _acts!.AddChild(plate);
        return (plate, label);
    }

    private Button ActButton(string text, bool toggle, System.Action pressed)
    {
        var b = new Button
        {
            Text = text,
            ToggleMode = toggle,
            FocusMode = Control.FocusModeEnum.None,
            CustomMinimumSize = new Vector2(104.0f, 44.0f),
        };
        b.AddThemeFontSizeOverride("font_size", 16);
        b.AddThemeStyleboxOverride("normal", ActBox(new Color(0.34f, 0.35f, 0.31f), 1));
        b.AddThemeStyleboxOverride("hover", ActBox(CardPale, 1));
        b.AddThemeStyleboxOverride("pressed", ActBox(CardSpend, 2));
        b.AddThemeStyleboxOverride("hover_pressed", ActBox(CardSpend, 2));
        b.AddThemeColorOverride("font_pressed_color", CardSpend);
        b.AddThemeColorOverride("font_hover_pressed_color", CardSpend);
        b.Pressed += pressed;
        _acts!.AddChild(b);
        return b;
    }

    private static StyleBoxFlat ActBox(Color rim, int width)
    {
        var box = new StyleBoxFlat { BgColor = new Color(0.08f, 0.085f, 0.075f, 0.9f), BorderColor = rim };
        box.SetBorderWidthAll(width);
        box.SetCornerRadiusAll(8);
        box.ContentMarginLeft = box.ContentMarginRight = 14.0f;
        return box;
    }

    private void SlotStyle(PanelContainer plate, Label label, bool open, string text, string spent)
    {
        // a pill with no fill, not a button's box: it is read, not pressed
        StyleBoxFlat box = ActBox(open ? CardPale : SlotDim, 1);
        box.BgColor = new Color(0.08f, 0.085f, 0.075f, 0.45f);
        box.SetCornerRadiusAll(22);
        plate.AddThemeStyleboxOverride("panel", box);
        label.Text = open ? text : spent;
        label.AddThemeColorOverride("font_color", open ? CardPale : SlotDim);
    }

    /// <summary>Keys 1 and 2 in a match.</summary>
    private void ActKey(Key key)
    {
        switch (key)
        {
            case Key.Key1: RamToggle(); break;
            case Key.Key2: Douse(); break;
        }
    }

    /// <summary>The left button with the ram on: on a tank it is the right
    /// button; anywhere else it only says what it wants. False with the ram
    /// off - the click is an order, as always.</summary>
    private bool ActClick(Vector2 screen)
    {
        if (!Match || !_ramMode)
            return false;
        if (_overTarget || _overParked is not null)
            AimAt(screen);
        else
            _orderNote = "таран: щёлкни по танку";
        return true;
    }

    /// <summary>Every frame, after <c>AimTick</c>: the slots' plates and which
    /// bonus buttons there are. Allocates nothing but on a change of the slots.</summary>
    private void ActsTick()
    {
        if (_acts is null)
            return;
        bool ours = _chosen && _fate == Fate.Alive && Match && !MatchOver && _side == _turn;
        _acts.Visible = ours;
        if (!ours)
            return;
        bool attack = !MatchShotSpent, bonus = !MatchBonusSpent;
        int slots = (attack ? 1 : 0) | (bonus ? 2 : 0);
        if (slots != _slotsShown)
        {
            _slotsShown = slots;
            SlotStyle(_slotAttack!, _slotAttackText!, attack, SlotAttack, SlotAttackSpent);
            SlotStyle(_slotBonus!, _slotBonusText!, bonus, SlotBonus, SlotBonusSpent);
        }
        _actRam!.Visible = bonus;
        _actDouse!.Visible = bonus && Burning(_side, _modelTag);
        _actRam.SetPressedNoSignal(_ramMode);
    }
}
