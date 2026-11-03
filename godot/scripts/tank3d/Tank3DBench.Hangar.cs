using System;
using System.Linq;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The quick battle's squad built in a hangar: <c>--hangar</c> on
/// <c>Match3D.tscn</c>, the old build screen (<see cref="QuickBuild"/>) left as
/// it is. The artifact «Ангар отряда», then the author's corrections
/// (2026-11-03): no cards along the bottom - the squad is told on the sheet's
/// «Отряд вместе»; the arrows by the podium go round the squad's machines,
/// a carousel; only the machine in front turns; the ready squads on the left.
/// <list type="bullet">
/// <item><b>Top</b>: «В БОЙ», on to the placement as «К расстановке» was
/// (<see cref="QuickTryGo"/>); the budget's pips are the tree's head.</item>
/// <item><b>Middle</b>: the ready squads on the left (<see cref="QuickHangarCompany"/>)
/// - the podium tells the squad itself - and under them the battle: the
/// minimap, the map and mode, the sides, the AI, a door to the parameters
/// (<see cref="QuickHangarBattle"/>); the podium - a ring of the
/// three that turns smoothly to the slot in front (<see cref="QuickHangarSpin"/>):
/// that one large in the middle, live and turning, the two others round the
/// back dimmed and still, a click brings one forward, the arrows step to the next or the last; the
/// five tags under it make the slot's class; the tree of the podium's slot
/// (<see cref="QuickBranch"/>).</item>
/// </list>
/// Its rules and its dialogs are the build's own.
/// </summary>
public sealed partial class Tank3DBench
{
    /// <summary><c>--hangar</c>: the build is the hangar.</summary>
    private bool _qHangar;

    /// <summary>The slot on the podium.</summary>
    private int _qhAt;

    /// <summary>The podium's views, px: one size for all three, scaled by
    /// where the ring has them, so bringing one forward loads nothing. Large:
    /// the one in front is drawn near this size, not blown up.</summary>
    private static readonly Vector2I QhView = new(840, 600);

    /// <summary>How big the two behind stand against the one in front.</summary>
    private const float QhBack = 0.42f;

    /// <summary>The ring as shown, in slots: eased toward <see cref="_qhAt"/>
    /// each frame (<see cref="QuickHangarSpin"/>), per second.</summary>
    private float _qhSpin;
    private const float QhEase = 7.0f;

    /// <summary>The podium and its three: each slot's view, its pick, its name.</summary>
    private Control? _qhStage;
    private readonly (Control? View, QuickPad? Pick, Label? Name)[] _qhRing = new (Control?, QuickPad?, Label?)[QuickSquad.Size];

    /// <summary>The podium's head and tag strip, px.</summary>
    private const float QhHeadHigh = 40, QhStripHigh = 52;

    /// <summary>How near the front each slot stands, 1 in front: the ring's draw order.</summary>
    private readonly float[] _qhNear = new float[QuickSquad.Size];

    private Control QuickHangar()
    {
        QuickSetup q = _qs;
        _qhAt = Math.Clamp(_qhAt, 0, QuickSquad.Size - 1);
        var page = new VBoxContainer();
        page.AddThemeConstantOverride("separation", 12);
        page.AddChild(QuickHangarTop());
        var mid = new HBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        mid.AddThemeConstantOverride("separation", 12);
        mid.AddChild(QuickHangarSheet());
        mid.AddChild(QuickHangarStage());
        mid.AddChild(QuickHangarTree());
        page.AddChild(mid);
        // a model the page no longer shows goes with it
        for (int i = 0; i < q.Squad.Length; i++)
            if (q.Squad[i].Empty)
                QuickStageDrop(i);
        return page;
    }

    // --- top: «В бой» ---------------------------------------------------------------

    private Control QuickHangarTop()
    {
        QuickSetup q = _qs;
        int used = QuickSquad.Spent(q.Squad), left = q.Budget - used, missing = q.Squad.Count(s => s.Empty);
        var top = new HBoxContainer();
        top.AddThemeConstantOverride("separation", 12);

        // the battle's facts are the sheet's, under the squad: this side only keeps «В бой» in the middle
        top.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });

        // «В бой»: the placement next, as «К расстановке» was
        var go = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        go.AddThemeConstantOverride("separation", 4);
        QuickPad button = QBtn(L.T("hangar.go"), QuickTryGo, primary: true);
        button.FontSize = 22;
        button.CustomMinimumSize = Vector2.Zero;
        button.Fit(new Vector2(46, 12));
        button.Disabled = missing > 0;
        go.AddChild(button);
        var sub = QText(missing > 0 ? L.T("hangar.in_squad", QuickSquad.Size - missing, QuickSquad.Size)
                        : left > 0 ? L.T("hangar.left_then_deploy", Pts(left)) : L.T("hangar.then_deploy"),
                        12, missing > 0 ? QRed : QMuted);
        sub.HorizontalAlignment = HorizontalAlignment.Center;
        go.AddChild(sub);
        top.AddChild(go);

        // the budget is the tree's, where it is spent (QuickHangarTree): this side balances the first
        top.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        return top;
    }

    /// <summary>A pad's insides deaf to the pointer: the pad takes the click and the hover.</summary>
    private static void QuickHangarDeaf(Control c)
    {
        c.MouseFilter = Control.MouseFilterEnum.Ignore;
        foreach (Node n in c.GetChildren())
            if (n is Control k)
                QuickHangarDeaf(k);
    }

    // --- left: the ready squads, the battle -----------------------------------------

    private Control QuickHangarSheet()
    {
        QuickSetup q = _qs;
        int start = QuickSquad.Size * QuickSquad.CompanyLevel;
        var col = new VBoxContainer { CustomMinimumSize = new Vector2(290, 0) };
        col.AddThemeConstantOverride("separation", 8);
        col.AddChild(QText(L.T("hangar.presets"), 17, QInk, bold: true));
        foreach (QuickSquad.Company c in QuickSquad.Companies)
            col.AddChild(QuickHangarCompany(c, start));
        col.AddChild(new Control { SizeFlagsVertical = Control.SizeFlags.ExpandFill });
        col.AddChild(QuickHangarBattle());
        return QBox(col, QPanelC, 14);
    }

    /// <summary>The battle under the squad: the minimap with the zones, the map
    /// and mode, the sides, the AI's doctrine and budget; a door to the parameters.</summary>
    private Control QuickHangarBattle()
    {
        QuickSetup q = _qs;
        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 6);
        var map = QuickMinimap(q.Map, q.Player, 190, labels: true);
        map.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter;
        col.AddChild(map);
        // short lines, unwrapped: a wrapped label measures itself a word a line first
        col.AddChild(QText(QuickMode(q.Map), 15, QInk, bold: true));
        col.AddChild(QText(L.T("hangar.params_door"), 12, QOlive));
        QuickHangarDeaf(col);
        // a box the layout sizes; a click on it opens the parameters
        PanelContainer door = QBox(col, QInset, 10);
        door.MouseDefaultCursorShape = Control.CursorShape.PointingHand;
        door.GuiInput += e =>
        {
            if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true })
            {
                _qModal = QuickModal.Params;
                QuickShow();
            }
        };
        return door;
    }

    // --- middle: the podium ---------------------------------------------------------

    private Control QuickHangarStage()
    {
        QuickSetup q = _qs;
        int at = _qhAt;
        QuickSquad.Slot s = q.Squad[at];
        var stage = new QuickPad
        {
            Rim = QLine, Fill = QInset, Glow = QSel.Lightened(0.15f), MouseFilter = Control.MouseFilterEnum.Pass,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(420, 360), ClipContents = true,
        };

        // the three on the ring, in slot order; the picks over them all, for the clicks
        for (int k = 0; k < q.Squad.Length; k++)
        {
            QuickSquad.Slot b = q.Squad[k];
            Control view = !b.Empty && QuickStage(k, b.Class!, q.Player, QhView) is { } box ? box
                : new QuickPad
                {
                    Form = QuickPad.Shape.Hex, Dashed = true, Rim = QMuted, Fill = new Color(0, 0, 0, 0), RimWidth = 2,
                    Ink = QMuted, FontSize = 22, Size = (Vector2)QhView * 0.55f, MouseFilter = Control.MouseFilterEnum.Ignore,
                    Text = !b.Empty ? "" : k == at ? L.T("hangar.slot_empty_front", k + 1) : L.T("hangar.slot_empty", k + 1),
                    Picture = b.Empty ? null : QuickArtOf(b.Class!, q.Player),
                };
            stage.AddChild(view);
            _qhRing[k].View = view;
        }
        for (int k = 0; k < q.Squad.Length; k++)
        {
            int slot = k;
            var pick = new QuickPad { Fill = new Color(0, 0, 0, 0), RimWidth = 0, HoverRim = QOlive };
            pick.Clicked += () => { _qhAt = slot; QuickShow(); };
            stage.AddChild(pick);
            QuickSquad.Slot o = q.Squad[k];
            string said = o.Empty ? L.T("hangar.empty")
                : $"{QuickSquad.ClassOf(o.Class!).Short} · "
                  + (QuickSquad.ClassOf(o.Class!).BranchOf(o.Branch) is { } br && o.Level > 0 ? $"{br.Name} {QRoman(o.Level)}" : L.T("hangar.no_upgrades"))
                  + $" · {Pts(o.Level)}";
            var name = QText(said, k == at ? 15 : 12, k == at ? QInk : QMuted, bold: true);
            stage.AddChild(name);
            _qhRing[k].Pick = pick;
            _qhRing[k].Name = name;
        }

        QuickPad clear = QBtn(L.T("quick.clear_squad"), () => QuickAsk(true, L.T("quick.clear_ask"),
            L.T("quick.clear_text"), L.T("quick.clear"), () => { _qs.Squad = QuickSquad.Empty(); _qhAt = 0; _qhSpin = 0; }), small: true);
        clear.Ink = QRed;
        clear.HoverRim = new Color(QRed, 0.6f);
        clear.Disabled = q.Squad.All(x => x.Empty);
        stage.AddChild(clear);

        QuickPad back = QuickHangarStep(-1), next = QuickHangarStep(+1);
        stage.AddChild(back);
        stage.AddChild(next);

        // where the class is among the five; a class there are two of is shut
        var strip = new HBoxContainer();
        strip.AddThemeConstantOverride("separation", 6);
        foreach (QuickSquad.Class c in QuickSquad.Classes)
        {
            bool mine = s.Class == c.Tag, full = !mine && QuickSquad.ClassWhy(q.Squad, at, c.Tag).Length > 0;
            QuickSquad.Class one = c;
            var tag = new QuickPad
            {
                Text = c.Short, FontSize = 13, Bold = true, CustomMinimumSize = new Vector2(52, 30),
                Rim = mine ? QOlive : QLine, Fill = mine ? QOlive : QPanelC, Ink = mine ? QOnOlive : QMuted,
                HoverRim = QOlive, Disabled = full,
            };
            tag.Clicked += () => QuickHangarSet(at, one.Tag);
            QTip(tag, () => QuickClassTip(one, full, s.Level > 0 && !mine ? L.T("quick.class_change", Pts(s.Level)) : null));
            strip.AddChild(tag);
        }
        stage.AddChild(strip);

        stage.Resized += () =>
        {
            Vector2 size = stage.Size;
            clear.Position = new Vector2(size.X - clear.Size.X - 12, 8);
            back.Size = next.Size = new Vector2(40, 66);
            back.Position = new Vector2(12, size.Y * 0.5f - 33);
            next.Position = new Vector2(size.X - 52, size.Y * 0.5f - 33);
            strip.Size = strip.GetCombinedMinimumSize();
            strip.Position = new Vector2((size.X - strip.Size.X) * 0.5f, size.Y - QhStripHigh + 8);
            QuickHangarRing();
        };
        _qhStage = stage;
        return stage;
    }

    /// <summary>Each frame of the screens: the ring turned on toward the slot in
    /// front, eased, the short way round; allocates nothing.</summary>
    private void QuickHangarSpin()
    {
        if (_qhStage is null || !IsInstanceValid(_qhStage) || !_qhStage.IsInsideTree())
            return;
        int n = QuickSquad.Size;
        float d = ((_qhAt - _qhSpin) % n + n * 1.5f) % n - n * 0.5f;
        if (Mathf.Abs(d) < 1e-3f)
        {
            if (_qhSpin == _qhAt)
                return;
            _qhSpin = _qhAt;
        }
        else
            _qhSpin += d * Mathf.Min(1.0f, (float)GetProcessDeltaTime() * QhEase);
        QuickHangarRing();
    }

    /// <summary>The three where the ring has them at <see cref="_qhSpin"/>: the
    /// slot in front large and lit in the middle of the podium, the others
    /// round the back, smaller, higher, dimmer. Its pick is off - its model
    /// takes the drag.</summary>
    private void QuickHangarRing()
    {
        if (_qhStage is not { } stage || !IsInstanceValid(stage))
            return;
        Vector2 size = stage.Size;
        float room = size.Y - QhHeadHigh - QhStripHigh;
        float k = Mathf.Min((size.X - 120) / QhView.X, room / QhView.Y);
        int n = QuickSquad.Size;
        for (int i = 0; i < n; i++)
        {
            var (view, pick, name) = _qhRing[i];
            if (view is null || !IsInstanceValid(view) || pick is null || name is null)
                continue;
            float turn = Mathf.Tau * (i - _qhSpin) / n;
            // 1 in front, 0 at the back
            float near = (Mathf.Cos(turn) + 0.5f) / 1.5f;
            float scale = k * Mathf.Lerp(QhBack, 1.0f, near);
            var middle = new Vector2(size.X * 0.5f + size.X * 0.34f * Mathf.Sin(turn),
                                     QhHeadHigh + room * Mathf.Lerp(0.36f, 0.5f, near));
            view.Scale = new Vector2(scale, scale);
            view.Position = middle - view.Size * scale * 0.5f;
            _qhNear[i] = near;
            view.Modulate = new Color(1, 1, 1).Lerp(new Color(0.62f, 0.66f, 0.6f, 0.85f), 1.0f - near);
            Vector2 hit = (Vector2)QhView * scale * new Vector2(0.5f, 0.6f);
            pick.Position = middle - hit * 0.5f;
            pick.Size = hit;
            bool front = near > 0.9f;
            pick.MouseFilter = front ? Control.MouseFilterEnum.Ignore : Control.MouseFilterEnum.Stop;
            name.Position = new Vector2(middle.X - name.Size.X * 0.5f, middle.Y - QhView.Y * scale * 0.42f);
        }
        // the far ones drawn first: the views are the podium's first children,
        // moved among themselves - a z index would lift them over the tips and dialogs too
        for (int place = 0; place < n; place++)
        {
            int far = -1;
            for (int i = 0; i < n; i++)
                if (_qhRing[i].View is { } v && IsInstanceValid(v) && v.GetIndex() >= place
                    && (far < 0 || _qhNear[i] < _qhNear[far]))
                    far = i;
            if (far >= 0 && _qhRing[far].View!.GetIndex() != place)
                stage.MoveChild(_qhRing[far].View, place);
        }
    }

    /// <summary>An arrow by the podium: the squad's next or last machine to the
    /// front, round the three - a carousel; the class is the tags' to change.</summary>
    private QuickPad QuickHangarStep(int dir)
    {
        int to = (_qhAt + dir + QuickSquad.Size) % QuickSquad.Size;
        var arrow = new QuickPad
        {
            Text = dir < 0 ? "‹" : "›", FontSize = 30, Bold = true, Rim = QLine, Fill = QPanelC, Ink = QInk,
            HoverFill = QSel, HoverRim = QOlive,
        };
        arrow.Clicked += () => { _qhAt = to; QuickShow(); };
        QuickSquad.Slot s = _qs.Squad[to];
        QTip(arrow, () => QTipBox(dir < 0 ? L.T("hangar.prev_tank") : L.T("hangar.next_tank"), L.T("hangar.slot", to + 1),
            s.Empty ? L.T("hangar.slot_unset") : QuickSquad.ClassOf(s.Class!).Name, null, null));
        return arrow;
    }

    /// <summary>A ready squad (meta.md, «Старт операции»): its name and play,
    /// its three machines with their branches. A click puts it in place of the
    /// squad built, asked first when there is one; shut when the budget is short.</summary>
    private QuickPad QuickHangarCompany(QuickSquad.Company c, int start)
    {
        QuickSetup q = _qs;
        bool short_ = q.Budget < start;
        var pad = new QuickPad { Rim = QLine, Fill = QInset, HoverRim = QOlive, HoverFill = QSel, Disabled = short_ };
        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 2);
        // the pad's width, less the margins: a play too long for it ends in "…", not past the rim
        col.AnchorRight = 1;
        col.OffsetLeft = 10;
        col.OffsetRight = -10;
        col.OffsetTop = 6;
        var head = new HBoxContainer();
        head.AddThemeConstantOverride("separation", 8);
        head.AddChild(QText(c.Name, 14, QInk, bold: true));
        var play = QText(c.Play, 12, QMuted);
        play.ClipText = true;
        play.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        play.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        head.AddChild(play);
        col.AddChild(head);
        col.AddChild(QText(string.Join(" · ", c.Slots.Select(x =>
            $"{QuickSquad.ClassOf(x.Class).Short} {QuickSquad.ClassOf(x.Class).BranchOf(x.Branch)!.Name}")), 12, QOlive));
        pad.AddChild(col);
        QuickHangarDeaf(col);
        // two short lines, unwrapped: a fixed height, a label measures nothing before the layout
        pad.CustomMinimumSize = new Vector2(0, 54);
        if (short_)
            col.Modulate = new Color(1, 1, 1, 0.45f);
        pad.Clicked += () => QuickAsk(q.Squad.Any(x => !x.Empty), L.T("quick.replace_ask"),
            L.T("quick.replace_text", c.Name, QuickSquad.CompanyLevel),
            L.T("quick.replace"), () => { q.Squad = QuickSquad.OfCompany(c); _qhAt = 0; _qhSpin = 0; });
        QTip(pad, () => QTipBox(L.T("quick.preset_tip", Pts(start)), c.Name,
            string.Join("\n", c.Slots.Select(x => L.T("quick.preset_slot", QuickSquad.ClassOf(x.Class).Short, QuickSquad.ClassOf(x.Class).BranchOf(x.Branch)!.Name, QuickSquad.CompanyLevel))),
            null, short_ ? L.T("hangar.preset_short", q.Budget, Pts(start))
                : L.T("quick.preset_note", $"{char.ToUpper(c.Play[0])}{c.Play[1..]}",
                  q.Budget > start ? L.T("quick.preset_left", Pts(q.Budget - start)) : L.T("quick.preset_all"))));
        return pad;
    }

    /// <summary>Slot <paramref name="at"/> made <paramref name="tag"/>, its level off; the same class keeps it.</summary>
    private void QuickHangarSet(int at, string tag)
    {
        if (_qs.Squad[at].Class != tag)
            _qs.Squad[at] = new QuickSquad.Slot(tag, null, 0);
        QuickShow();
    }

    // --- right: the tree, the machine under it -------------------------------------

    private Control QuickHangarTree()
    {
        QuickSetup q = _qs;
        QuickSquad.Slot s = q.Squad[_qhAt];
        int left = q.Budget - QuickSquad.Spent(q.Squad);
        var col = new VBoxContainer { CustomMinimumSize = new Vector2(320, 0) };
        col.AddThemeConstantOverride("separation", 10);
        var head = new HBoxContainer();
        var h = QText(L.T("hangar.upgrades"), 17, QInk, bold: true);
        h.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        head.AddChild(h);
        // the budget by the tree that spends it, the squad's whole: spent olive, free dark
        if (q.Budget > 0)
        {
            Control pips = QuickPips(QuickSquad.Spent(q.Squad), q.Budget, 18);
            pips.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            head.AddChild(pips);
        }
        else
            head.AddChild(QHint(L.T("hangar.budget_zero")));
        col.AddChild(head);
        if (s.Empty)
            col.AddChild(QHint(L.T("hangar.tree_later"), wrap: true));
        else
        {
            var tree = new HBoxContainer();
            tree.AddThemeConstantOverride("separation", 8);
            foreach (QuickSquad.Branch b in QuickSquad.ClassOf(s.Class!).Branches)
                tree.AddChild(QuickBranch(_qhAt, s, b, left));
            col.AddChild(tree);
            col.AddChild(QHint(L.T("hangar.dash_note"), wrap: true));
        }
        // the machine under its tree: what it is, its numbers, what it carries
        col.AddChild(new ColorRect { Color = QLine, CustomMinimumSize = new Vector2(0, 1) });
        QuickHangarUnit(col, s);
        var box = QBox(col, QPanelC, 14);
        // the tips stand beside the panel, the tree stays open
        box.SetMeta(QTipBeside, true);
        return box;
    }

    /// <summary>The podium's machine under its tree: the class, its duty and
    /// role, its numbers, its trait.</summary>
    private void QuickHangarUnit(VBoxContainer col, QuickSquad.Slot s)
    {
        if (s.Empty)
        {
            col.AddChild(QHint(L.T("hangar.slot_empty_hint", _qhAt + 1), wrap: true));
            return;
        }
        QuickSquad.Class c = QuickSquad.ClassOf(s.Class!);
        var name = new HBoxContainer();
        name.AddThemeConstantOverride("separation", 8);
        name.AddChild(QText(c.Name, 18, QInk, bold: true));
        var duty = QText(c.Duty, 13, QOlive, bold: true);
        duty.VerticalAlignment = VerticalAlignment.Center;
        name.AddChild(duty);
        col.AddChild(name);
        col.AddChild(QText(c.Role, 13, QMuted, wrap: true));
        // the numbers two lines, not a table: the tree over them has the height
        (string, string)[] n = QuickClassRows(c);
        col.AddChild(QText(L.T("hangar.unit_numbers", n[0].Item2, n[1].Item2, n[3].Item2, n[4].Item2), 13, QInk, wrap: true));
        col.AddChild(QText(L.T("hangar.unit_armour", n[2].Item2), 13, QInk, wrap: true));
        col.AddChild(QText(L.T("quick.trait", c.Trait), 13, QInk, wrap: true));
        // what it carries is the tree's open nodes: nothing said again (as on the old build)
    }
}
