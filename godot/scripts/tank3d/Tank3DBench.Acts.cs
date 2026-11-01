using System.Collections.Generic;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The action panel, bottom centre (docs/hud3d-plan.md, «Панель действий»;
/// by the unit's card until the roster, 2026-10-30): what is left of the chosen unit's turn, in
/// a match. GDD match.md «Структура хода» (U-22): the move, then two slots,
/// one of each - the <b>attack</b> and the <b>bonus act</b>.
/// <list type="bullet">
/// <item><b>Two groups, each in its own frame</b> (the author's layout,
/// 2026-10-31): the attack's - <b>Атака</b> 1, <b>Засада</b> 2 and, for a
/// unit with the rounds, <b>Зажиг.</b> - and the bonus act's, numbered on
/// from 3 in the order they stand. A spent slot's frame dims and holds only
/// a tick and its name.</item>
/// <item><b>Атака</b> - a mode: the left button on a tank then fires as the
/// right one does (<c>AimAt</c>), and an own unit is a target only so - by
/// default ours are not fired on (<c>MatchAims</c>, <c>FriendOn</c>). The
/// right button on an enemy fires with the mode off as ever. Spent with the
/// round (<c>Launch</c>). <b>Засада</b> (Д7) - a mode, the left button on a
/// hex picks the axis (Tank3DBench.Ambush). <b>Зажиг.</b> (Д8) - the next
/// shot is incendiary (Tank3DBench.Incendiary), no key.</item>
/// <item>The bonus acts: <b>Таран</b> - a mode (Tab too), the left button on
/// a tank then rams it, own ones too; <b>Тушить</b> - at once (J too,
/// <see cref="Douse"/>); <b>Таран стены</b>, the kit's <b>Дым</b>,
/// <b>Мина</b>, <b>Ремонт</b>, <b>Лебёдка</b> (Д5, Д8).</item>
/// <item><b>What may not be done is not there</b> (the author's choice, as
/// with the roses): no <b>Тушить</b> on a hull that is not burning, no kit's
/// button without the kit.</item>
/// <item>Keys 1 to 9 press the buttons by their numbers; the roster's units
/// are F1 to F8 (the digits were theirs from 2026-10-30 to 2026-10-31).</item>
/// </list>
/// </summary>
public sealed partial class Tank3DBench
{
    private HBoxContainer? _acts;
    private PanelContainer? _groupAttack, _groupBonus;
    private Label? _spentAttack, _spentBonus;
    private Button? _actAttack, _actRam, _actDouse, _actRamWall;
    /// <summary>«Атака» on: the left button on a tank fires, and ours may be fired on.</summary>
    private bool _attackMode;
    /// <summary>The bonus group's buttons in their order, and what each does
    /// - the numbers run over the ones shown.</summary>
    private readonly List<(Button Button, string Text, System.Action Act)> _bonusActs = new();
    private System.Action? _attackAct, _ambushAct;
    /// <summary>The slots and the bonus buttons shown, as last drawn: the
    /// frames and the numbers are redone when they change, not every frame.</summary>
    private int _slotsShown = -1, _bonusShown = -1;

    private static readonly Color SlotDim = new(0.42f, 0.43f, 0.40f);

    private void BuildActs(CanvasLayer layer)
    {
        // bottom centre (the author's layout, 2026-10-31): the attack's
        // frame, a gap, the bonus act's
        _acts = new HBoxContainer
        {
            Name = "Acts",
            AnchorLeft = 0.5f, AnchorRight = 0.5f, AnchorTop = 1.0f, AnchorBottom = 1.0f,
            OffsetTop = -12.0f - 56.0f, OffsetBottom = -12.0f,
            GrowHorizontal = Control.GrowDirection.Both,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Visible = false,
        };
        _acts.AddThemeConstantOverride("separation", 18);
        HBoxContainer attack;
        (_groupAttack, attack, _spentAttack) = Group("✓  Атака");
        _attackAct = AttackToggle;
        _actAttack = ActButton(attack, "Атака   1", toggle: true, _attackAct);
        // the attack held back (Д7, Tank3DBench.Ambush): a mode
        _ambushAct = AmbushToggle;
        _actAmbush = ActButton(attack, AmbushText + "   2", toggle: true, _ambushAct);
        // the incendiary round (Д8, Tank3DBench.Incendiary): the next shot's, no key
        _actFiery = ActButton(attack, "Зажиг.", toggle: true, FieryToggle);
        HBoxContainer bonus;
        (_groupBonus, bonus, _spentBonus) = Group("✓  Доп. действие");
        _actRam = BonusButton(bonus, "Таран", toggle: true, RamToggle);
        // the wall ahead rammed through (Д5)
        _actRamWall = BonusButton(bonus, "Таран стены", toggle: false, () => Execute(new Command(CommandKind.RamWall, Side: HullSide())));
        // the grenade (Д8, Tank3DBench.Smoke): a mode
        _actSmoke = BonusButton(bonus, "Дым", toggle: true, SmokeToggle);
        // the kit's acts on a hex (Д8, Tank3DBench.Gear): modes
        _actMine = BonusButton(bonus, "Мина", toggle: true, () => GearToggle(Gear.Mine));
        _actRepair = BonusButton(bonus, "Ремонт", toggle: true, () => GearToggle(Gear.Repair));
        _actWinch = BonusButton(bonus, "Лебёдка", toggle: true, () => GearToggle(Gear.Winch));
        _actDouse = BonusButton(bonus, "Тушить", toggle: false, () => Execute(new Command(CommandKind.Douse)));
        layer.AddChild(_acts);
    }

    /// <summary>A slot's frame: its buttons in a row, and the tick shown in
    /// their place once the slot is spent.</summary>
    private (PanelContainer, HBoxContainer, Label) Group(string spent)
    {
        var frame = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 6);
        var label = new Label
        {
            Text = spent,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            CustomMinimumSize = new Vector2(0.0f, 44.0f),
            Visible = false,
        };
        label.AddThemeFontSizeOverride("font_size", 16);
        label.AddThemeColorOverride("font_color", SlotDim);
        row.AddChild(label);
        frame.AddChild(row);
        _acts!.AddChild(frame);
        return (frame, row, label);
    }

    private Button BonusButton(HBoxContainer row, string text, bool toggle, System.Action pressed)
    {
        Button b = ActButton(row, text, toggle, pressed);
        _bonusActs.Add((b, text, pressed));
        return b;
    }

    private static Button ActButton(HBoxContainer row, string text, bool toggle, System.Action pressed)
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
        row.AddChild(b);
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

    /// <summary>A group's frame: pale while its slot is to be had, dim once spent.</summary>
    private static void GroupStyle(PanelContainer frame, Label spent, bool open)
    {
        StyleBoxFlat box = ActBox(open ? CardPale : SlotDim, open ? 2 : 1);
        box.BgColor = new Color(0.08f, 0.085f, 0.075f, 0.55f);
        box.SetCornerRadiusAll(12);
        box.ContentMarginLeft = box.ContentMarginRight = box.ContentMarginTop = box.ContentMarginBottom = 6.0f;
        frame.AddThemeStyleboxOverride("panel", box);
        spent.Visible = !open;
    }

    /// <summary>«Атака» on or off: on, the left button on a tank fires, and an
    /// own unit may be the target.</summary>
    private void AttackToggle()
    {
        if (!_attackMode && MatchShotSpent)
        {
            _orderNote = "атака этого хода уже сделана";
            return;
        }
        _attackMode = !_attackMode;
        if (_attackMode)
        {
            _ramMode = _smokeMode = _ambushMode = false;
            _ambushPick = null;
            _gearMode = Gear.None;
        }
        _orderNote = _attackMode ? "атака: щёлкни по танку, свой тоже" : "атака выключена";
    }

    /// <summary>Keys 1 to 9 in a match: the panel's buttons by their numbers -
    /// 1 «Атака», 2 «Засада», 3 on the bonus acts shown. F1 to F8 take the
    /// roster's units, top down, as a click on its icon (Tank3DBench.Roster).</summary>
    private void ActKey(Key key)
    {
        if (key is >= Key.F1 and <= Key.F8)
        {
            int n = (int)(key - Key.F1);
            if (n < _rosterCount)
                RosterTake(n);
            return;
        }
        if (_acts is not { Visible: true })
            return;
        int k = (int)(key - Key.Key1);
        if (k == 0 && _actAttack!.Visible)
            _attackAct!();
        else if (k == 1 && _actAmbush!.Visible)
            _ambushAct!();
        for (int i = 0, shown = 2; k >= 2 && i < _bonusActs.Count; i++)
            if (_bonusActs[i].Button.Visible && shown++ == k)
            {
                _bonusActs[i].Act();
                return;
            }
    }

    /// <summary>The left button with a mode on: «Атака» or the ram on a tank
    /// is the right button; anywhere else it only says what it wants. False
    /// with none - the click is an order, as always.</summary>
    private bool ActClick(Vector2 screen)
    {
        if (Match && AmbushClick(screen))
            return true;
        if (Match && SmokeClick(screen))
            return true;
        if (Match && GearClick(screen))
            return true;
        if (!Match || !_ramMode && !_attackMode)
            return false;
        // the tank at the click itself made the target: a click from --do has
        // no pointer over it
        if (ParkedAt(screen) is { } standing)
            MatchAims(standing);
        if (_overTarget || _overParked is not null || PickAt(screen) is (_, true, _))
            AimAt(screen);
        else
            _orderNote = _ramMode ? "таран: щёлкни по танку" : "атака: щёлкни по танку";
        return true;
    }

    /// <summary>An own unit, not destroyed, standing on <paramref name="cell"/>
    /// or the target there: not fired on without «Атака».</summary>
    private bool FriendOn(Vector2I cell)
    {
        if (_other is { Unit: true } o && o.Side == _side && o.Fate != Fate.Destroyed && OtherCell(o) == cell)
            return true;
        foreach (Parked p in _parked)
            if (p.Cell == cell && p.Side == _side && p.Fate != Fate.Destroyed)
                return true;
        return false;
    }

    /// <summary>Every frame, after <c>AimTick</c>: the frames and which
    /// buttons there are. Allocates nothing but on a change of the slots or of
    /// the bonus buttons shown.</summary>
    private void ActsTick()
    {
        if (_acts is null)
            return;
        // an own unit left as the target once «Атака» and the ram are off:
        // back to standing, so the right button does not fire on it
        if (Match && !_attackMode && !_ramMode && _other is { Unit: true } own && own.Side == _side
            && !RamRunning && _aim is null && _shotWaiting is null && _ramWaiting is null && (_shells?.Rounds.Count ?? 0) == 0)
            DropTarget();
        bool ours = _chosen && _fate == Fate.Alive && Match && !MatchOver && _side == _mt.Turn;
        _acts.Visible = ours;
        if (!ours)
            return;
        bool attack = !MatchShotSpent, bonus = !MatchBonusSpent;
        int slots = (attack ? 1 : 0) | (bonus ? 2 : 0);
        if (slots != _slotsShown)
        {
            _slotsShown = slots;
            GroupStyle(_groupAttack!, _spentAttack!, attack);
            GroupStyle(_groupBonus!, _spentBonus!, bonus);
        }
        _actAttack!.Visible = attack;
        _actAttack.SetPressedNoSignal(_attackMode);
        _actAmbush!.Visible = attack;
        _actAmbush.SetPressedNoSignal(_ambushMode);
        _actFiery!.Visible = attack && RigBurns;
        _actFiery.SetPressedNoSignal(_fieryMode);
        _actRam!.Visible = bonus;
        _actRam.SetPressedNoSignal(_ramMode);
        _actRamWall!.Visible = bonus && WallAhead;
        _actSmoke!.Visible = bonus && RigSmokes;
        _actSmoke.SetPressedNoSignal(_smokeMode);
        _actMine!.Visible = bonus && RigHas(Kit.MineLayer);
        _actMine.SetPressedNoSignal(_gearMode == Gear.Mine);
        _actRepair!.Visible = bonus && RigHas(Kit.RepairKit);
        _actRepair.SetPressedNoSignal(_gearMode == Gear.Repair);
        _actWinch!.Visible = bonus && RigHas(Kit.Winch);
        _actWinch.SetPressedNoSignal(_gearMode == Gear.Winch);
        _actDouse!.Visible = bonus && Burning(_unitId);
        // the numbers run over the bonus buttons shown, from 3
        int shownMask = 0;
        for (int i = 0; i < _bonusActs.Count; i++)
            if (_bonusActs[i].Button.Visible)
                shownMask |= 1 << i;
        if (shownMask != _bonusShown)
        {
            _bonusShown = shownMask;
            for (int i = 0, n = 3; i < _bonusActs.Count; i++)
                if (_bonusActs[i].Button.Visible)
                    _bonusActs[i].Button.Text = n <= 9 ? $"{_bonusActs[i].Text}   {n++}" : _bonusActs[i].Text;
        }
    }
}
