using System.Collections.Generic;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The act's card (docs/hud3d-plan.md, «Карточка действия»; the author's note,
/// 2026-11-01): the pointer resting on a button of the action panel - or of the
/// turn's corner - brings up the game's one tip (<see cref="UiTip"/>, 2026-11-03):
/// over the button, its beak down to it, the button ringed, after the tips' wait:
/// <list type="bullet">
/// <item>its type over the name - the group's shape and name in the group's
/// colour, as the frame's mark (<see cref="MarkAt"/>): Движение, Основное
/// действие, Доп. действие;</item>
/// <item>the act's name and its key;</item>
/// <item>what it does, a few lines;</item>
/// <item>with the slot spent, why the button is grey.</item>
/// </list>
/// It stands in for the engine's tooltip, which the buttons no longer have.
/// <c>--do tip=NAME</c> (the icon's name: move, attack, ram…) holds it up for a
/// capture with no pointer; <c>tip=off</c> lets it go.
/// </summary>
public sealed partial class Tank3DBench
{
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
    /// <summary>The match's tips (<see cref="UiTip"/>): the acts', the corner's, the
    /// roster's cards and the placement's buttons.</summary>
    private UiTip? _hudTips;
    /// <summary>Whether the shown card's button was off when its body was built.</summary>
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
        ["smoke_shell"] = L.T("acttips.text_smoke_shell"),
        ["fire_on_move"] = L.T("acttips.text_fire_on_move"),
        ["ram"] = L.T("acttips.text_ram"),
        ["smoke"] = L.T("acttips.text_smoke"),
        ["mine"] = L.T("acttips.text_mine"),
        ["repair"] = L.T("acttips.text_repair"),
        ["winch"] = L.T("acttips.text_winch", Battle.WinchReach),
        ["douse"] = L.T("acttips.text_douse"),
        ["double_turn"] = L.T("acttips.text_pivot"),
        ["overdrive"] = L.T("acttips.text_overdrive"),
        // the corner's buttons (TurnTipHook)
        ["endturn"] = L.T("acttips.text_endturn"),
        ["ready"] = L.T("acttips.text_ready"),
        ["undo"] = L.T("acttips.text_undo"),
    };

    /// <summary>A corner button's card (Tank3DBench.Undo, Match): no group, its key given.</summary>
    private void TurnTipHook(Button b, string icon, string key) =>
        TipHook(new ActTip { Button = b, Icon = icon, Name = icon, Key = key });

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
        _hudTips = new UiTip();
        layer.AddChild(_hudTips);
        // over it, an upgrade's name on its icon in the roster's card (Tank3DBench.Roster)
        _kitTip = new UiTip();
        layer.AddChild(_kitTip);
    }

    /// <summary>A button's card, and the pointer's enter and exit told to it.</summary>
    private void ActTipHook(Button b, HBoxContainer row, string icon, string name) =>
        TipHook(new ActTip { Button = b, Icon = icon, Name = name, Group = _rowGroup[row] });

    private void TipHook(ActTip tip)
    {
        _actTips.Add(tip);
        tip.Button.MouseEntered += () => ActTipShow(tip);
        tip.Button.MouseExited += () => _hudTips?.Hide(tip);
    }

    /// <summary>The card of <paramref name="tip"/>: over its button, the beak down to it.</summary>
    private void ActTipShow(ActTip tip)
    {
        if (_hudTips is null || (tip.Group is not null && _acts is { Visible: false }))
            return;
        _tipWasOff = tip.Button.Disabled;
        // no ring round the button: the beak says which it is, and a grey ring
        // over the button's own rim read as a frame of its own (the author's note, 2026-11-06)
        _hudTips.Show(tip, tip.Button.GetGlobalRect, () => ActTipBody(tip), UiTip.Prefer.Above, ring: false);
    }

    /// <summary>Every frame, from <c>ActsTick</c>: a held card up, a card gone with its
    /// panel, built anew when its button greys or comes back.</summary>
    private void ActTipTick()
    {
        if (_hudTips is null || _acts is null)
            return;
        if (_tipHeld is not null)
        {
            for (int i = 0; i < _actTips.Count; i++)
                if (_actTips[i].Icon == _tipHeld && _hudTips.For != _actTips[i] && _actTips[i].Button.IsVisibleInTree())
                {
                    _hudTips.Hold = true;
                    ActTipShow(_actTips[i]);
                }
        }
        else if (_hudTips.Hold && _hudTips.For is ActTip)
        {
            _hudTips.Hold = false;
            _hudTips.Hide(null);
        }
        if (_hudTips.For is not ActTip t)
            return;
        if ((t.Group is not null && !_acts.Visible) || !t.Button.IsVisibleInTree())
            _hudTips.Hide(t);
        else if (t.Button.Disabled != _tipWasOff)
        {
            _tipWasOff = t.Button.Disabled;
            _hudTips.Renew();
        }
    }

    /// <summary>What a card says: the group's mark and name over the act's name and key,
    /// what the act does, and why a grey one is grey.</summary>
    private Control ActTipBody(ActTip tip)
    {
        (string name, string textKey) = TipWords(tip);
        string text = ActText.TryGetValue(textKey, out string? t) ? t : "";
        string key = tip.Group is null ? tip.Key : ActKeyOf(tip.Button);
        string why = !tip.Button.Disabled ? ""
            : tip.Group is null ? (tip.Icon == "undo" ? UndoWhy() : L.T("acttips.why_over"))
            : tip.Group.Shape == GroupShape.Diamond ? MatchMoveWhy()
            : tip.Group.Shape == GroupShape.Circle ? MatchShotWhy(CellHere) is { Length: > 0 } s ? s : L.T("acttips.why_shot_spent")
            : BonusGoneSay("acttips.why_bonus_spent");
        Control? mark = null;
        if (tip.Group is { } g)
        {
            var m = new Control { CustomMinimumSize = new Vector2(14, 14), MouseFilter = Control.MouseFilterEnum.Ignore };
            m.Draw += () => MarkAt(m, m.Size * 0.5f, g.Shape, g.Ink, true, 5.5f);
            mark = m;
        }
        return UiTip.Box(tip.Group is { } gr ? GroupName(gr.Shape) : "", name, text, state: why, large: true,
                         key: key.Length > 0 ? L.T("acttips.key", key) : null,
                         kickerInk: tip.Group?.Ink, kickerMark: mark, stateInk: Ui.Danger);
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
