using Godot;

namespace TankSpriteTest;

/// <summary>
/// The action panel, bottom centre (docs/hud3d-plan.md, «Панель действий»;
/// by the unit's card until the roster, 2026-10-30): what is left of the chosen unit's turn, in
/// a match. GDD match.md «Структура хода» (U-22): the move, then two slots,
/// one of each - the <b>attack</b> (the shot) and the <b>bonus act</b> (the
/// ram, putting a fire out).
/// <list type="bullet">
/// <item><b>Two plates, the slots</b>: lit while the slot is to be had, dimmed
/// once spent. They are not buttons - the move and the shot stay the mouse's,
/// as they were: the left button on a cell, the right one on a tank.</item>
/// <item><b>Засада</b> by the attack's plate (Д7): a mode - the left button on
/// a hex then picks the axis (Tank3DBench.Ambush). <b>Зажиг.</b> (Д8), for a
/// unit with the rounds: the next shot is incendiary (Tank3DBench.Incendiary).</item>
/// <item><b>Buttons only for the bonus acts</b>, which the mouse cannot say on
/// its own: <b>Таран</b> - a mode (Tab too), the left button on a tank then
/// rams it as the right one does (<c>AimAt</c>); <b>Тушить</b> - at once
/// (<see cref="Douse"/>); the kit's <b>Дым</b>, <b>Мина</b>, <b>Ремонт</b>,
/// <b>Лебёдка</b> (Д8) - modes, a click on a hex then.</item>
/// <item><b>What may not be done is not there</b> (the author's choice, as
/// with the roses): no bonus buttons once the bonus act is spent, no
/// <b>Тушить</b> on a hull that is not burning.</item>
/// <item>Keys: <b>Tab</b> the ram, <b>J</b> put it out, Esc leaves the ram.
/// 1 and 2 were these two until the roster took 1 to 5 (2026-10-30).</item>
/// </list>
/// </summary>
public sealed partial class Tank3DBench
{
    private HBoxContainer? _acts;
    private PanelContainer? _slotAttack, _slotBonus;
    private Label? _slotAttackText, _slotBonusText;
    private Button? _actRam, _actDouse, _actRamWall;
    /// <summary>The slots as last drawn: the plates are restyled when they change, not every frame.</summary>
    private int _slotsShown = -1;

    private const string SlotAttack = "●  Атака", SlotAttackSpent = "✓  Атака";
    private const string SlotBonus = "●  Доп. действие", SlotBonusSpent = "✓  Доп. действие";
    private static readonly Color SlotDim = new(0.42f, 0.43f, 0.40f);

    private void BuildActs(CanvasLayer layer)
    {
        // bottom centre (the author's layout, 2026-10-30): the attack's group,
        // a gap, the bonus act's - the slot and its buttons
        _acts = new HBoxContainer
        {
            Name = "Acts",
            AnchorLeft = 0.5f, AnchorRight = 0.5f, AnchorTop = 1.0f, AnchorBottom = 1.0f,
            OffsetTop = -12.0f - 44.0f, OffsetBottom = -12.0f,
            GrowHorizontal = Control.GrowDirection.Both,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Visible = false,
        };
        _acts.AddThemeConstantOverride("separation", 6);
        (_slotAttack, _slotAttackText) = Slot(SlotAttack);
        // the attack held back (Д7, Tank3DBench.Ambush): a mode, no key - none is free
        _actAmbush = ActButton(AmbushText, toggle: true, AmbushToggle);
        // the incendiary round (Д8, Tank3DBench.Incendiary): the next shot's, no key
        _actFiery = ActButton("Зажиг.", toggle: true, FieryToggle);
        _acts.AddChild(new Control { CustomMinimumSize = new Vector2(18.0f, 0.0f), MouseFilter = Control.MouseFilterEnum.Ignore });
        (_slotBonus, _slotBonusText) = Slot(SlotBonus);
        _actRam = ActButton("Таран   Tab", toggle: true, RamToggle);
        // the grenade (Д8, Tank3DBench.Smoke): a mode, no key - none is free
        _actSmoke = ActButton("Дым", toggle: true, SmokeToggle);
        // the kit's acts on a hex (Д8, Tank3DBench.Gear): modes, no keys
        _actMine = ActButton("Мина", toggle: true, () => GearToggle(Gear.Mine));
        _actRepair = ActButton("Ремонт", toggle: true, () => GearToggle(Gear.Repair));
        _actWinch = ActButton("Лебёдка", toggle: true, () => GearToggle(Gear.Winch));
        _actDouse = ActButton("Тушить   J", toggle: false, () => Execute(new Command(CommandKind.Douse)));
        // the wall ahead rammed through (Д5): no key of its own - none is free
        _actRamWall = ActButton("Таран стены", toggle: false, () => Execute(new Command(CommandKind.RamWall, Side: HullSide())));
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

    /// <summary>Keys 1 to 5 in a match: the roster's icons, top down - the
    /// unit taken and the view on it, as a click on its icon
    /// (Tank3DBench.Roster). The ram and the fire have Tab and J.</summary>
    private void ActKey(Key key)
    {
        int n = (int)(key - Key.Key1);
        if (n >= 0 && n < _rosterCount)
            RosterTake(n);
    }

    /// <summary>The left button with the ram on: on a tank it is the right
    /// button; anywhere else it only says what it wants. False with the ram
    /// off - the click is an order, as always.</summary>
    private bool ActClick(Vector2 screen)
    {
        if (Match && AmbushClick(screen))
            return true;
        if (Match && SmokeClick(screen))
            return true;
        if (Match && GearClick(screen))
            return true;
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
        bool ours = _chosen && _fate == Fate.Alive && Match && !MatchOver && _side == _mt.Turn;
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
        _actAmbush!.Visible = attack;
        _actAmbush.SetPressedNoSignal(_ambushMode);
        _actFiery!.Visible = attack && RigBurns;
        _actFiery.SetPressedNoSignal(_fieryMode);
        _actRam!.Visible = bonus;
        _actSmoke!.Visible = bonus && RigSmokes;
        _actSmoke.SetPressedNoSignal(_smokeMode);
        _actMine!.Visible = bonus && RigHas(Kit.MineLayer);
        _actMine.SetPressedNoSignal(_gearMode == Gear.Mine);
        _actRepair!.Visible = bonus && RigHas(Kit.RepairKit);
        _actRepair.SetPressedNoSignal(_gearMode == Gear.Repair);
        _actWinch!.Visible = bonus && RigHas(Kit.Winch);
        _actWinch.SetPressedNoSignal(_gearMode == Gear.Winch);
        _actDouse!.Visible = bonus && Burning(_unitId);
        _actRamWall!.Visible = bonus && WallAhead;
        _actRam.SetPressedNoSignal(_ramMode);
    }
}
