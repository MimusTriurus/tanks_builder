using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
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

    /// <summary>The window's size the podium's views were drawn for, px.</summary>
    private Vector2I _qhWindow;
    /// <summary>When the window last changed size, ms; 0, settled.</summary>
    private ulong _qhResized;

    /// <summary>The card of the ready squad the squad was taken from, lit on the sheet;
    /// the podium opens off it (<see cref="QuickHangarBridge"/>). Null with none.</summary>
    private QuickPad? _qhLinked;

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
    private readonly (Control? View, QuickPad? Pick, Control? Name)[] _qhRing = new (Control?, QuickPad?, Control?)[QuickSquad.Size];

    /// <summary>The podium's head and class strip, px.</summary>
    private const float QhHeadHigh = 40, QhStripHigh = 118;

    /// <summary>A class tab: the icon's side; the pad - the icon's shield (its frame is
    /// 0.654 by 0.623 of the icon) and a 6 px halo; the gap - a gun reaches past its shield.</summary>
    private const float QhTabIcon = 112;
    private static readonly Vector2 QhTabPad = new(QhTabIcon * 0.654f + 12, QhTabIcon * 0.623f + 12);
    private const int QhTabGap = 22;

    /// <summary>How near the front each slot stands, 1 in front: the ring's draw order.</summary>
    private readonly float[] _qhNear = new float[QuickSquad.Size];

    private Control QuickHangar()
    {
        QuickSetup q = _qs;
        _qhAt = Math.Clamp(_qhAt, 0, QuickSquad.Size - 1);
        _qhLinked = null;
        // a slot whose class changed lets its view go first, so a class moving to
        // another slot finds it parked instead of loading a second one
        for (int i = 0; i < q.Squad.Length; i++)
            if (_qStage.TryGetValue(i, out var had) && had.Class != q.Squad[i].Class)
                QuickStageDrop(i);
        var page = new VBoxContainer();
        page.AddThemeConstantOverride("separation", 12);
        Control top = QuickHangarTop();
        page.AddChild(top);
        var mid = new HBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        mid.AddThemeConstantOverride("separation", 12);
        Control sheet = QuickHangarSheet(), tree = QuickHangarTree();
        mid.AddChild(sheet);
        mid.AddChild(QuickHangarStage());
        mid.AddChild(tree);
        page.AddChild(mid);
        // «В бой» over the podium's middle, not the screen's: the side columns differ (the
        // tree's is the wider) and centred on the screen it stood off the podium's middle
        // (author 2026-11-03). The top's sides as wide as the columns under them, from
        // their least widths at once (no frame of it elsewhere), then as they are laid out
        var (l, r) = ((Control)top.GetChild(0), (Control)top.GetChild(2));
        l.CustomMinimumSize = new Vector2(sheet.GetCombinedMinimumSize().X, 0);
        r.CustomMinimumSize = new Vector2(tree.GetCombinedMinimumSize().X, 0);
        sheet.Resized += () => l.CustomMinimumSize = new Vector2(sheet.Size.X, 0);
        tree.Resized += () => r.CustomMinimumSize = new Vector2(tree.Size.X, 0);
        // a model the page no longer shows goes with it
        for (int i = 0; i < q.Squad.Length; i++)
            if (q.Squad[i].Empty)
                QuickStageDrop(i);
        return page;
    }

    // --- the loading screen ----------------------------------------------------------

    /// <summary>The hangar's loading screen (author 2026-11-03: the hangar still
    /// stuttered): black, a line saying what is loading and a bar, over the screens
    /// while every class's view is loaded and drawn - its shaders compiled - and every
    /// icon the hangar shows is rasterised, so nothing the hangar does later loads,
    /// compiles or rasterises. A frame between steps, for the bar to move.</summary>
    private async Task QuickHangarLoading()
    {
        var veil = new CanvasLayer { Name = "QuickLoading", Layer = 40 };
        AddChild(veil);
        var black = new ColorRect { Color = Colors.Black, MouseFilter = Control.MouseFilterEnum.Stop };
        black.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        veil.AddChild(black);
        var middle = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        middle.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        veil.AddChild(middle);
        var col = new VBoxContainer { CustomMinimumSize = new Vector2(560, 0) };
        col.AddThemeConstantOverride("separation", 14);
        middle.AddChild(col);
        var head = Ui.Text(L.T("loading.title"), 28, Ui.Ink, bold: true);
        head.HorizontalAlignment = HorizontalAlignment.Center;
        col.AddChild(head);
        var bar = new ProgressBar { ShowPercentage = false, CustomMinimumSize = new Vector2(560, 12), MinValue = 0 };
        bar.AddThemeStyleboxOverride("background", new StyleBoxFlat { BgColor = Ui.Panel });
        bar.AddThemeStyleboxOverride("fill", new StyleBoxFlat { BgColor = Ui.Accent });
        col.AddChild(bar);
        var what = Ui.Text("", 18, Ui.Muted);
        what.HorizontalAlignment = HorizontalAlignment.Center;
        col.AddChild(what);

        // every icon the hangar draws, at every size it draws it
        var icons = new List<(string Name, int Px, bool Grey)>();
        foreach (QuickSquad.Class c in QuickSquad.Classes)
        {
            icons.Add((QuickClassIcon(c), (int)QhTabIcon, false));
            icons.Add((QuickClassIcon(c), 48, false));
            foreach (QuickSquad.Branch b in c.Branches)
                foreach (QuickSquad.Upgrade u in b.Levels)
                    if (u.Icon.Length > 0 && !icons.Contains((u.Icon, 32, false)))
                    {
                        // a node's icon in colour and in grey, the one out of reach
                        icons.Add((u.Icon, 32, false));
                        icons.Add((u.Icon, 32, true));
                    }
        }
        int views = QuickStageWanted().Count, done = 0;
        bar.MaxValue = views * 2 + icons.Count;
        ulong from = Time.GetTicksMsec();
        await QuickStageWarm((name, drawn) =>
        {
            what.Text = drawn ? L.T("loading.shaders", name) : L.T("loading.model", name);
            bar.Value = ++done;
        });
        what.Text = L.T("loading.icons");
        for (int i = 0; i < icons.Count; i++)
        {
            QIcon(icons[i].Name, icons[i].Px, icons[i].Grey);
            bar.Value = ++done;
            if (i % 6 == 5)
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        what.Text = L.T("loading.ready");
        bar.Value = bar.MaxValue;
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Log.Print($"tank3d: hangar: loaded in {Time.GetTicksMsec() - from} ms - {views} views, {icons.Count} icons");
        veil.QueueFree();
    }

    // --- top: «В бой» ---------------------------------------------------------------

    private Control QuickHangarTop()
    {
        QuickSetup q = _qs;
        int missing = q.Squad.Count(s => s.Empty);
        var top = new HBoxContainer();
        top.AddThemeConstantOverride("separation", 12);

        // the battle's facts are the sheet's, under the squad: this side only keeps «В бой»
        // over the podium - the sides are sized to the columns under them (QuickHangar)
        top.AddChild(new Control { MouseFilter = Control.MouseFilterEnum.Ignore });

        // «В бой»: the placement next, as «К расстановке» was
        var go = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        go.AddThemeConstantOverride("separation", 4);
        QuickPad button = QBtn(L.T("hangar.go"), QuickTryGo, primary: true);
        button.FontSize = Ui.Size(22);
        button.CustomMinimumSize = Vector2.Zero;
        button.Fit(new Vector2(46, 12));
        button.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter;
        button.Disabled = missing > 0;
        go.AddChild(button);
        // no line under it (author 2026-11-04) but the one that says why it is shut
        if (missing > 0)
        {
            Label sub = Ui.Text(L.T("hangar.in_squad", QuickSquad.Size - missing, QuickSquad.Size), 12, Ui.Danger);
            sub.HorizontalAlignment = HorizontalAlignment.Center;
            go.AddChild(sub);
        }
        top.AddChild(go);

        // the budget is the tree's, where it is spent (QuickHangarTree)
        top.AddChild(new Control { MouseFilter = Control.MouseFilterEnum.Ignore });
        return top;
    }

    /// <summary>The battle card's figure rows in the hangar, times their size there.</summary>
    private const float QhRowsK = 1.25f;

    /// <summary>The left sheet's width and its panels' padding, px: a ready squad's row is
    /// measured against them (QuickHangarCompany).</summary>
    private const float QhSheetWide = 380, QhSheetPad = 14;

    /// <summary>A ready squad's class icons, px, the octagon itself (QuickIconTight):
    /// large enough to tell LT from MT.</summary>
    private const int QhPresetIcon = 30;

    /// <summary>The battle panel's minimap, px wide.</summary>
    private const int QhMapWide = 110;

    /// <summary>A side panel's caption, the lobby's eyebrow (<see cref="QEyebrow"/>) a
    /// size up for the hangar's larger type: it names the panel and is not read as
    /// one more of its rows, which a bold 17 in ink was (author 2026-11-04).</summary>
    private static Label QuickHangarHead(string text) => Ui.Text(text.ToUpperInvariant(), 13, Ui.Muted, bold: true);

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
        // two panels, as the right one and the podium are (author 2026-11-03): the ready
        // squads, and under them the battle
        var side = new VBoxContainer { CustomMinimumSize = new Vector2(QhSheetWide, 0) };
        side.AddThemeConstantOverride("separation", 12);
        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 8);
        col.AddChild(QuickHangarHead(L.T("hangar.presets")));
        foreach (QuickSquad.Company c in QuickSquad.Companies)
            col.AddChild(QuickHangarCompany(c, start));
        PanelContainer presets = Ui.Box(col, Ui.Panel, QhSheetPad);
        presets.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        side.AddChild(presets);
        side.AddChild(QuickHangarBattle());
        return side;
    }

    /// <summary>The battle under the squad, a panel of its own: the mode as its
    /// heading, the minimap with the zones; the whole panel a door to the parameters
    /// (the map, the sides, the AI's doctrine and budget), its rim lit under the pointer.</summary>
    private Control QuickHangarBattle()
    {
        QuickSetup q = _qs;
        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 8);
        // short lines, unwrapped: a wrapped label measures itself a word a line first
        // the panel's caption, then the mode as its value, centred on the map's axis
        col.AddChild(QuickHangarHead(L.T("quick.mode")));
        Label mode = Ui.Text(QuickMode(q.Map), 20, Ui.Ink, bold: true);
        mode.HorizontalAlignment = HorizontalAlignment.Center;
        col.AddChild(mode);
        // smaller than it was, its room the ready squads' icons' (author 2026-11-04)
        var map = QuickMinimap(q.Map, q.Player, QhMapWide, labels: true, ground: false);
        map.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter;
        col.AddChild(map);
        Label link = Ui.Text(L.T("hangar.params_door"), 12, Ui.Accent);
        link.HorizontalAlignment = HorizontalAlignment.Center;
        col.AddChild(link);
        QuickHangarDeaf(col);
        // a box the layout sizes; a click on it opens the parameters
        PanelContainer door = Ui.Box(col, Ui.Panel, 14);
        door.MouseDefaultCursorShape = Control.CursorShape.PointingHand;
        var rim = (StyleBoxFlat)door.GetThemeStylebox("panel");
        door.MouseEntered += () => { rim.BorderColor = Ui.Accent; door.QueueRedraw(); };
        door.MouseExited += () => { rim.BorderColor = Ui.Line; door.QueueRedraw(); };
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
            Rim = Ui.Line, Fill = Ui.Inset, Glow = Ui.AccentFill.Lightened(0.15f), MouseFilter = Control.MouseFilterEnum.Pass, Focusable = false,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(420, 360),
        };
        // the models cut to the podium by a layer of their own, not by the podium: a
        // clip there cut its own rim's outer screen pixel off on the right
        var ring = new Control { MouseFilter = Control.MouseFilterEnum.Ignore, ClipContents = true };
        ring.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        stage.AddChild(ring);

        // the three on the ring, in slot order; the picks over them all, for the clicks
        for (int k = 0; k < q.Squad.Length; k++)
        {
            QuickSquad.Slot b = q.Squad[k];
            Control view = !b.Empty && QuickStage(k, b.Class!, q.Player, QhView) is { } box ? box
                : new QuickPad
                {
                    Form = QuickPad.Shape.Hex, Dashed = true, Rim = Ui.Muted, Fill = new Color(0, 0, 0, 0), RimWidth = 2,
                    Ink = Ui.Muted, FontSize = 22, Size = (Vector2)QhView * 0.55f, MouseFilter = Control.MouseFilterEnum.Ignore,
                    Text = !b.Empty ? "" : k == at ? L.T("hangar.slot_empty_front", k + 1) : L.T("hangar.slot_empty", k + 1),
                    Picture = b.Empty ? null : QuickArtOf(b.Class!, q.Player),
                };
            ring.AddChild(view);
            _qhRing[k].View = view;
        }
        for (int k = 0; k < q.Squad.Length; k++)
        {
            int slot = k;
            var pick = new QuickPad { Fill = new Color(0, 0, 0, 0), RimWidth = 0, HoverRim = Ui.Accent };
            pick.Clicked += () => { _qhAt = slot; QuickShow(); };
            stage.AddChild(pick);
            Control name = QuickHangarCaption(q.Squad[k], k == at);
            // the two at the back a shade down, chevrons too (author 2026-11-03): the
            // front one reads first; 0.65 keeps the muted text near 3:1 on the stage
            if (k != at)
                name.Modulate = new Color(1, 1, 1, 0.65f);
            stage.AddChild(name);
            _qhRing[k].Pick = pick;
            _qhRing[k].Name = name;
        }

        QuickPad clear = QBtn(L.T("quick.clear_squad"), () => QuickAsk(true, L.T("quick.clear_ask"),
            L.T("quick.clear_text"), L.T("quick.clear"), () => { _qs.Squad = QuickSquad.Empty(); _qs.Preset = null; _qs.Name = null; _qhAt = 0; _qhSpin = 0; }), small: true);
        clear.Ink = Ui.Danger;
        clear.HoverRim = new Color(Ui.Danger, 0.6f);
        clear.Disabled = q.Squad.All(x => x.Empty);
        // hidden for now (author 2026-11-03); --quick-do clear still asks
        clear.Visible = false;
        stage.AddChild(clear);

        Control title = QuickHangarName();
        stage.AddChild(title);

        QuickPad back = QuickHangarStep(-1), next = QuickHangarStep(+1);
        stage.AddChild(back);
        stage.AddChild(next);

        // where the class is among the five, by its icon (its tag where there is none);
        // a class there are two of is shut. The pad is the icon's shield with a halo
        // round it, olive on the chosen one (author 2026-11-03: twice the size, not square)
        var strip = new HBoxContainer();
        strip.AddThemeConstantOverride("separation", QhTabGap);
        foreach (QuickSquad.Class c in QuickSquad.Classes)
        {
            bool mine = s.Class == c.Tag, full = !mine && QuickSquad.ClassWhy(q.Squad, at, c.Tag).Length > 0;
            QuickSquad.Class one = c;
            Texture2D? icon = QIcon(QuickClassIcon(c), (int)QhTabIcon);
            var tag = new QuickPad
            {
                Text = icon is null ? c.Short : "", FontSize = 13, Bold = true,
                Form = icon is null ? QuickPad.Shape.Box : QuickPad.Shape.Shield, RimWidth = 2,
                CustomMinimumSize = icon is null ? new Vector2(52, 30) : QhTabPad,
                Rim = mine ? Ui.Accent : Ui.Line, Fill = mine ? Ui.Accent : new Color(0, 0, 0, 0), Ink = mine ? Ui.OnAccent : Ui.Muted,
                HoverRim = Ui.Accent, Disabled = full, Picture = icon, PictureSize = new Vector2(QhTabIcon, QhTabIcon), TextureFilter = QIconFilter,
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
            title.Size = title.GetCombinedMinimumSize();
            title.Position = new Vector2((size.X - title.Size.X) * 0.5f, 10);
            back.Size = next.Size = new Vector2(40, 66);
            back.Position = new Vector2(12, size.Y * 0.5f - 33);
            next.Position = new Vector2(size.X - 52, size.Y * 0.5f - 33);
            strip.Size = strip.GetCombinedMinimumSize();
            strip.Position = new Vector2((size.X - strip.Size.X) * 0.5f, size.Y - QhStripHigh + 8);
            QuickHangarRing();
        };
        // the ready squad the squad was taken from opens into the podium like a tab
        // (author 2026-11-03): the podium's rim takes the card's, a bridge joins them
        if (_qhLinked is { } card)
        {
            stage.Rim = card.Rim;
            stage.RimWidth = card.RimWidth;
            var bridge = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
            // the stage's first child: over its rim, under all else on it
            stage.AddChild(bridge);
            stage.MoveChild(bridge, 0);
            bridge.Draw += () => QuickHangarBridge(bridge, card, stage);
            card.ItemRectChanged += bridge.QueueRedraw;
            stage.ItemRectChanged += bridge.QueueRedraw;
        }
        _qhStage = stage;
        return stage;
    }

    /// <summary>The tab's bridge from the chosen card to the podium, on the podium's
    /// first layer: the card's fill from its right edge across the gap and both panels'
    /// rims into the podium, edged above and below by the card's rim, so the card,
    /// the bridge and the podium's rim make one outline; across the bridge the card's fill
    /// turns to the podium's, done by the joint (author 2026-11-03), so there is no seam.</summary>
    private static void QuickHangarBridge(Control on, QuickPad card, QuickPad stage)
    {
        if (!IsInstanceValid(card) || !card.IsInsideTree())
            return;
        Transform2D to = on.GetGlobalTransform().AffineInverse();
        Rect2 c = card.GetGlobalRect(), s = stage.GetGlobalRect();
        float t = Ui.RimLayout(on, card.RimWidth);
        Vector2 a = to * new Vector2(c.End.X - t, c.Position.Y), b = to * new Vector2(s.Position.X + t, c.End.Y);
        // the card's fill, from its own edge, turned to the podium's by the joint
        Color fill = card.Fill, end = stage.Fill, rim = card.Rim;
        on.DrawPolygon(new[] { new Vector2(a.X, a.Y + t), new Vector2(b.X, a.Y + t), new Vector2(b.X, b.Y - t), new Vector2(a.X, b.Y - t) },
                       new[] { fill, end, end, fill });
        on.DrawRect(new Rect2(a.X, a.Y, b.X - a.X, t), rim);
        on.DrawRect(new Rect2(a.X, b.Y - t, b.X - a.X, t), rim);
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
        // the light under the machine in front, centred on its turntable's axis where it
        // meets the ground (author 2026-11-04) - fixed at the front's place (near = 1,
        // below), not following the ring as it turns; an empty slot, the place's middle
        var axis = new Vector2(size.X * 0.5f, QhHeadHigh + room * 0.5f);
        if (_qhRing[_qhAt].View is { } fv && IsInstanceValid(fv) && fv.HasMeta(RosterAxisMeta))
        {
            float fd = k / (fv.HasMeta("q_px") ? (float)fv.GetMeta("q_px") : 1.0f);
            axis += ((Vector2)fv.GetMeta(RosterAxisMeta) - new Vector2(0.5f, 0.5f)) * fv.Size * fd;
        }
        var glowAt = size.X > 0 && size.Y > 0 ? axis / size : new Vector2(0.5f, 0.72f);
        if (stage is QuickPad pad && pad.GlowAt != glowAt)
        {
            pad.GlowAt = glowAt;
            pad.QueueRedraw();
        }
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
            // a model's view is drawn in screen pixels (QuickStage): scaled back by as much
            float drawn = view.HasMeta("q_px") ? scale / (float)view.GetMeta("q_px") : scale;
            view.Scale = new Vector2(drawn, drawn);
            view.Position = middle - view.Size * drawn * 0.5f;
            _qhNear[i] = near;
            view.Modulate = new Color(1, 1, 1).Lerp(new Color(0.62f, 0.66f, 0.6f, 0.85f), 1.0f - near);
            Vector2 hit = (Vector2)QhView * scale * new Vector2(0.5f, 0.6f);
            pick.Position = middle - hit * 0.5f;
            pick.Size = hit;
            bool front = near > 0.9f;
            pick.MouseFilter = front ? Control.MouseFilterEnum.Ignore : Control.MouseFilterEnum.Stop;
            // two lines now: its foot where the one line's was, the block grown up
            name.Size = name.GetCombinedMinimumSize();
            name.Position = new Vector2(middle.X - name.Size.X * 0.5f, middle.Y - QhView.Y * scale * 0.42f + 22 - name.Size.Y);
        }
        // the far ones drawn first: the views are the ring layer's children,
        // moved among themselves - a z index would lift them over the tips and dialogs too
        for (int place = 0; place < n; place++)
        {
            int far = -1;
            for (int i = 0; i < n; i++)
                if (_qhRing[i].View is { } v && IsInstanceValid(v) && v.GetIndex() >= place
                    && (far < 0 || _qhNear[i] < _qhNear[far]))
                    far = i;
            if (far >= 0 && _qhRing[far].View!.GetIndex() != place)
                _qhRing[far].View!.GetParent().MoveChild(_qhRing[far].View, place);
        }
    }

    /// <summary>An arrow by the podium: the squad's next or last machine to the
    /// front, round the three - a carousel; the class is the tags' to change.</summary>
    private QuickPad QuickHangarStep(int dir)
    {
        int to = (_qhAt + dir + QuickSquad.Size) % QuickSquad.Size;
        var arrow = new QuickPad
        {
            Text = dir < 0 ? "‹" : "›", FontSize = 30, Bold = true, Rim = Ui.Line, Fill = Ui.Panel, Ink = Ui.Ink,
            HoverFill = Ui.AccentFill, HoverRim = Ui.Accent,
        };
        arrow.Clicked += () => { _qhAt = to; QuickShow(); };
        QuickSquad.Slot s = _qs.Squad[to];
        QTip(arrow, () => UiTip.Box(dir < 0 ? L.T("hangar.prev_tank") : L.T("hangar.next_tank"), L.T("hangar.slot", to + 1),
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
        var pad = new QuickPad { Rim = Ui.Line, Fill = Ui.Inset, HoverRim = Ui.Accent, HoverFill = Ui.AccentFill, Disabled = short_ };
        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 2);
        // the pad's width, less the margins: a play too long for it ends in "…", not past the rim
        col.AnchorRight = 1;
        col.OffsetLeft = 10;
        col.OffsetRight = -10;
        col.OffsetTop = 6;
        // its name and its three; its play is the tip's (in the type scale it no longer fits beside the name)
        Label title = Ui.Text(c.Name, 14, Ui.Ink, bold: true);
        // centred over its three columns (author 2026-11-04)
        title.HorizontalAlignment = HorizontalAlignment.Center;
        col.AddChild(title);
        // its three as the class's icon and the branch under it (author 2026-11-04: the tags
        // were letters to learn, and an icon a line high could not be told apart), the
        // strip's icons under the stage: three equal columns, a name too long for its column
        // ending in "…". The level only past the first
        Font font = Ui.FontOf(true);
        int n = c.Slots.Length;
        var three = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        three.AddThemeConstantOverride("separation", 6);
        for (int i = 0; i < n; i++)
        {
            QuickSquad.Class cls = QuickSquad.ClassOf(c.Slots[i].Class);
            var item = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Ignore };
            item.AddThemeConstantOverride("separation", 0);
            var face = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center, MouseFilter = Control.MouseFilterEnum.Ignore };
            face.AddThemeConstantOverride("separation", 4);
            if (QuickIconTight(QuickClassIcon(cls), QhPresetIcon) is { } icon)
                face.AddChild(new TextureRect
                {
                    Texture = icon, CustomMinimumSize = new Vector2(QhPresetIcon, QhPresetIcon), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                    StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, TextureFilter = QIconFilter,
                });
            if (QuickSquad.CompanyLevel > 1)
                face.AddChild(new QuickRank { Level = QuickSquad.CompanyLevel, High = QhPresetIcon * 0.6f, Lit = Ui.Accent,
                                              Dim = new Color(Ui.Muted, 0.6f), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter });
            item.AddChild(face);
            Label branch = Ui.Text(cls.BranchOf(c.Slots[i].Branch)!.Name, 12, Ui.Muted);
            branch.HorizontalAlignment = HorizontalAlignment.Center;
            branch.ClipText = true;
            branch.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            item.AddChild(branch);
            three.AddChild(item);
        }
        col.AddChild(three);
        pad.AddChild(col);
        QuickHangarDeaf(col);
        // a fixed height, a label measures nothing before the layout: the name, the icons, their names
        pad.CustomMinimumSize = new Vector2(0, Mathf.Ceil(font.GetHeight(Ui.Size(14)) + QhPresetIcon + Ui.FontOf(false).GetHeight(Ui.Size(12)) + 12));
        if (short_)
            col.Modulate = new Color(1, 1, 1, 0.45f);
        // the one the squad was taken from: lit while it is as taken, its rim only once changed
        if (q.Preset == c && !short_)
        {
            bool kept = QuickHangarAsTaken();
            pad.Rim = Ui.Accent;
            pad.RimWidth = kept ? 2 : 1;
            if (kept)
                pad.Fill = Ui.AccentFill;
            _qhLinked = pad;
        }
        pad.SetMeta("q_company", Array.IndexOf(QuickSquad.Companies, c));
        pad.Clicked += () => QuickHangarPick(c);
        QTip(pad, () => QuickHangarCompanyTip(c, start, short_));
        return pad;
    }

    /// <summary>One of the equal gaps the machine's description is spread down its panel
    /// by: what height the tree leaves, shared out.</summary>
    private static Control QuickHangarGap() =>
        new() { CustomMinimumSize = new Vector2(0, 6), SizeFlagsVertical = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Ignore };

    /// <summary>A ready squad's tip: its three as the class in full and the branch, the
    /// level in rank chevrons as over the podium (author 2026-11-04), its play in the
    /// tip's ink.</summary>
    private Control QuickHangarCompanyTip(QuickSquad.Company c, int start, bool short_)
    {
        QuickSetup q = _qs;
        // no kicker, no points and no word on replacing (author 2026-11-04): the tip says what the
        // squad is; only a squad the budget shuts says why
        Control tip = UiTip.Box("", c.Name, "",
            state: short_ ? L.T("hangar.preset_short", q.Budget, Pts(start)) : null,
            coda: $"{char.ToUpper(c.Play[0])}{c.Play[1..]}.");
        var rows = new VBoxContainer();
        rows.AddThemeConstantOverride("separation", 4);
        Font font = Ui.FontOf(false);
        float high = font.GetHeight(Ui.Size(13));
        foreach ((string Class, string Branch) x in c.Slots)
        {
            QuickSquad.Class cls = QuickSquad.ClassOf(x.Class);
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 8);
            row.AddChild(new QuickRank { Level = QuickSquad.CompanyLevel, High = high, Lit = Ui.Accent, Dim = new Color(Ui.TipMuted, 0.6f),
                                         SizeFlagsVertical = Control.SizeFlags.ShrinkCenter });
            row.AddChild(Ui.Text(L.T("hangar.preset_slot", cls.Name, cls.BranchOf(x.Branch)!.Name), 13, Ui.TipInk));
            rows.AddChild(row);
        }
        // under the head, over the play: the box has no kicker, its head comes first
        tip.AddChild(rows);
        tip.MoveChild(rows, 1);
        return tip;
    }

    /// <summary>A ready squad in place of the squad (author 2026-11-03): asked first only
    /// when that would lose something - machines and levels no ready squad has as they
    /// stand, or a name the player gave; one taken and left as it was goes without a word.</summary>
    private void QuickHangarPick(QuickSquad.Company c)
    {
        QuickSetup q = _qs;
        bool own = q.Squad.Any(x => !x.Empty) && !QuickSquad.Companies.Any(o => q.Squad.SequenceEqual(QuickSquad.OfCompany(o)));
        QuickAsk(own || q.Name is not null, L.T("quick.replace_ask"),
            L.T("quick.replace_text", c.Name, QuickSquad.CompanyLevel),
            L.T("quick.replace"), () => { q.Squad = QuickSquad.OfCompany(c); q.Preset = c; q.Name = null; _qhAt = 0; _qhSpin = 0; });
    }

    /// <summary>The squad is still its ready squad's, machine for machine and level for level.</summary>
    private bool QuickHangarAsTaken() =>
        _qs.Preset is { } c && _qs.Squad.SequenceEqual(QuickSquad.OfCompany(c));

    /// <summary>The squad's name over the podium, typed in place (author 2026-11-03):
    /// the player's own, else the ready squad's it was taken from, else the default;
    /// a line under it says when the squad is no longer as taken.</summary>
    private Control QuickHangarName()
    {
        QuickSetup q = _qs;
        var col = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        col.AddThemeConstantOverride("separation", 0);
        var edit = new LineEdit
        {
            Text = q.Name ?? q.Preset?.Name ?? L.T("hangar.squad_default"),
            Alignment = HorizontalAlignment.Center, MaxLength = 32, SelectAllOnFocus = true,
            CustomMinimumSize = new Vector2(360, 0), CaretBlink = true,
        };
        edit.AddThemeFontOverride("font", Ui.BoldFont);
        edit.AddThemeFontSizeOverride("font_size", Ui.Size(22));
        edit.AddThemeColorOverride("font_color", Ui.Ink);
        edit.AddThemeColorOverride("caret_color", Ui.Accent);
        edit.AddThemeColorOverride("selection_color", new Color(Ui.Accent, 0.45f));
        // flat on the podium, a rim under the pointer and while typed in
        edit.AddThemeStyleboxOverride("normal", new StyleBoxEmpty());
        edit.AddThemeStyleboxOverride("hover", QuickHangarNameBox(Ui.Line));
        edit.AddThemeStyleboxOverride("focus", QuickHangarNameBox(Ui.Accent));
        edit.MouseDefaultCursorShape = Control.CursorShape.Ibeam;
        edit.TextChanged += t =>
        {
            string name = t.Trim();
            q.Name = name.Length == 0 || name == (q.Preset?.Name ?? L.T("hangar.squad_default")) ? null : name;
        };
        // Enter or a click away ends the typing; an emptied name comes back
        edit.TextSubmitted += _ => edit.ReleaseFocus();
        edit.FocusExited += () =>
        {
            if (edit.Text.Trim().Length == 0)
                edit.Text = q.Preset?.Name ?? L.T("hangar.squad_default");
        };
        col.AddChild(edit);
        // a ready squad as taken says nothing under its name (author 2026-11-04): the sheet's lit
        // row says it already; taken from one and renamed or changed, it says which
        string note = q.Preset is null ? "" : QuickHangarAsTaken()
            ? q.Name is null ? "" : L.T("hangar.squad_from", q.Preset.Name)
            : L.T("hangar.squad_changed", q.Preset.Name);
        var sub = Ui.Text(note, 12, Ui.Muted);
        sub.HorizontalAlignment = HorizontalAlignment.Center;
        sub.MouseFilter = Control.MouseFilterEnum.Ignore;
        col.AddChild(sub);
        return col;
    }

    private static StyleBoxFlat QuickHangarNameBox(Color rim) => new()
    {
        BgColor = new Color(0, 0, 0, 0.18f), BorderColor = rim,
        BorderWidthBottom = 2, ContentMarginLeft = 10, ContentMarginRight = 10, ContentMarginTop = 2, ContentMarginBottom = 2,
    };

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
        var col = new VBoxContainer { CustomMinimumSize = new Vector2(400, 0) };
        col.AddThemeConstantOverride("separation", 10);
        var head = new HBoxContainer();
        Label h = QuickHangarHead(L.T("hangar.upgrades"));
        h.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
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
            QuickTreeCenter(tree);
            col.AddChild(tree);
        }
        // the machine under its tree: what it is, its numbers, what it carries
        col.AddChild(new ColorRect { Color = Ui.Line, CustomMinimumSize = new Vector2(0, 1) });
        // its own column, no separation: its gaps share out the height the tree leaves
        var unit = new VBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        unit.AddThemeConstantOverride("separation", 0);
        col.AddChild(unit);
        QuickHangarUnit(unit, s);
        var box = Ui.Box(col, Ui.Panel, 14);
        // the tips stand beside the panel, the tree stays open
        box.SetMeta(UiTip.Beside, true);
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
        // the class's icon, the strip's under the stage, before its name
        if (QIcon(QuickClassIcon(c), 48) is { } icon)
            name.AddChild(new TextureRect
            {
                Texture = icon, CustomMinimumSize = new Vector2(48, 48), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, TextureFilter = QIconFilter,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, MouseFilter = Control.MouseFilterEnum.Ignore,
            });
        var title = Ui.Text(c.Name, 18, Ui.Ink, bold: true);
        title.VerticalAlignment = VerticalAlignment.Center;
        name.AddChild(title);
        var duty = Ui.Text(c.Duty, 13, Ui.Muted, bold: true);
        duty.VerticalAlignment = VerticalAlignment.Center;
        // cut to the panel, never widening it: a wider column narrowed the podium and
        // moved everything on it when the class changed (author 2026-11-03)
        duty.ClipText = true;
        duty.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        duty.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        name.AddChild(duty);
        col.AddChild(QuickHangarGap());
        col.AddChild(name);
        // its role is the class tab's tip: in the type scale the column has no room for it.
        // The figures as the battle's card draws them (author 2026-11-04), in two columns
        // level row for row under their captions, then the trait set off as a callout - the
        // three spread down the panel by equal gaps (author 2026-11-04), not packed at its top
        col.AddChild(QuickHangarGap());
        col.AddChild(QuickHangarFigures(c.Profile));
        col.AddChild(QuickHangarGap());
        col.AddChild(QuickHangarTrait(c.Trait));
        col.AddChild(QuickHangarGap());
        // what it carries is the tree's open nodes: nothing said again (as on the old build)
    }

    /// <summary>The machine's figures in the battle card's marks (<c>Tank3DBench.Peek</c>),
    /// <see cref="QhRowsK"/> times their size there: the class's might, steps and mass on
    /// the left, its three plates on the right - three rows each, level, under their
    /// captions (author 2026-11-04: the card's armour heading made a step). The turret
    /// and the roof the card leaves out are left out here too (author 2026-11-04).</summary>
    private Control QuickHangarFigures(MovementProfile p)
    {
        Font font = ThemeDB.FallbackFont;
        const float k = QhRowsK;
        int size = Mathf.RoundToInt(Ui.Caption * k);
        float step = PeekRowStep * k, high = step * 3.0f;
        // a column's slots past its longest label
        float SlotsAt(params string[] keys)
        {
            float w = 0.0f;
            foreach (string key in keys)
                w = Mathf.Max(w, font.GetStringSize(L.T(key), HorizontalAlignment.Left, -1, size).X);
            return Mathf.Ceil(w + 14.0f * k);
        }
        float leftAt = SlotsAt("peek.fire", "peek.move", "peek.mass");
        float rightAt = SlotsAt("peek.front", "peek.side", "peek.rear");
        Control Column(string head, float wide, Action<Control, float> draw)
        {
            var box = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
            box.AddThemeConstantOverride("separation", 2);
            box.AddChild(QuickHangarHead(head));
            var rows = new Control { CustomMinimumSize = new Vector2(wide, high), MouseFilter = Control.MouseFilterEnum.Ignore };
            rows.Draw += () => draw(rows, step * 0.5f);
            box.AddChild(rows);
            return box;
        }
        var two = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        two.AddThemeConstantOverride("separation", 28);
        two.AddChild(Column(L.T("hangar.figures"), leftAt + PeekSlotGap * k * PeekSteps, (c, y) =>
        {
            PeekRow(c, font, 0, leftAt, y, L.T("peek.fire"), p.Might, PeekMight, PeekMark.Dot, Ui.Attack, k);
            PeekRow(c, font, 0, leftAt, y + step, L.T("peek.move"), p.Steps, PeekSteps, PeekMark.Dot, Ui.Ink, k);
            PeekRow(c, font, 0, leftAt, y + step * 2, L.T("peek.mass"), p.Mass, PeekMass, PeekMark.Square, Ui.Ink, k);
        }));
        two.AddChild(Column(L.T("peek.armor"), rightAt + PeekSlotGap * k * PeekArmour, (c, y) =>
        {
            PeekPlate(c, font, p, 0, rightAt, y, L.T("peek.front"), Gunnery.Plate.Front, k);
            PeekPlate(c, font, p, 0, rightAt, y + step, L.T("peek.side"), Gunnery.Plate.Side, k);
            PeekPlate(c, font, p, 0, rightAt, y + step * 2, L.T("peek.rear"), Gunnery.Plate.Rear, k);
        }));
        return two;
    }

    /// <summary>The class's trait as a callout (author 2026-11-04): an olive bar down its
    /// left, its caption in olive, and its name - the words before a colon, where it has
    /// one - a line of its own over what it does.</summary>
    private static Control QuickHangarTrait(string trait)
    {
        var text = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Ignore };
        text.AddThemeConstantOverride("separation", 2);
        Label head = QuickHangarHead(L.T("hangar.trait"));
        head.AddThemeColorOverride("font_color", Ui.Accent);
        text.AddChild(head);
        int colon = trait.IndexOf(':');
        string body = trait;
        if (colon > 0 && colon < 32)
        {
            text.AddChild(Ui.Text(trait[..colon], 14, Ui.Ink, bold: true));
            body = trait[(colon + 1)..].Trim();
            if (body.Length > 0)
                body = char.ToUpper(body[0]) + body[1..];
        }
        text.AddChild(Ui.Text(body, 13, Ui.Ink, wrap: true));
        var callout = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        callout.AddThemeConstantOverride("separation", 10);
        callout.AddChild(new ColorRect { Color = Ui.Accent, CustomMinimumSize = new Vector2(3, 0), MouseFilter = Control.MouseFilterEnum.Ignore });
        callout.AddChild(text);
        return callout;
    }

    /// <summary>The caption over a machine on the ring (author 2026-11-03): its class
    /// in full, under it its branch, and its level as rank chevrons beside both
    /// lines, a sleeve's patch; the price is the head's and the tree's, not said
    /// here. Larger and lit on the front one.</summary>
    private Control QuickHangarCaption(QuickSquad.Slot o, bool front)
    {
        var box = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore, Alignment = BoxContainer.AlignmentMode.Center };
        box.AddThemeConstantOverride("separation", 2);
        Label Line(string text, int size, Color ink, bool left)
        {
            var l = Ui.Text(text, size, ink, bold: true);
            l.HorizontalAlignment = left ? HorizontalAlignment.Left : HorizontalAlignment.Center;
            l.SizeFlagsHorizontal = left ? Control.SizeFlags.ShrinkBegin : Control.SizeFlags.ShrinkCenter;
            return l;
        }
        if (o.Empty)
        {
            box.AddChild(Line(L.T("hangar.empty"), front ? 15 : 12, front ? Ui.Ink : Ui.Muted, false));
            return box;
        }
        QuickSquad.Class c = QuickSquad.ClassOf(o.Class!);
        QuickSquad.Branch? br = c.BranchOf(o.Branch);
        bool lit = br is not null && o.Level > 0;
        int big = front ? 17 : 13, small = front ? 14 : 11;
        box.AddChild(Line(c.Name, big, front ? Ui.Ink : Ui.Muted, lit));
        box.AddChild(Line(lit ? br!.Name : L.T("hangar.no_upgrades"), small, Ui.Muted, lit));
        if (!lit)
            return box;
        // the chevrons stand as tall as the two lines, from the class's top to the branch's foot
        Font font = Ui.FontOf(true);
        float high = font.GetHeight(Ui.Size(big)) + font.GetHeight(Ui.Size(small)) + 2;
        var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", front ? 12 : 8);
        row.AddChild(new QuickRank
        {
            Level = o.Level, High = high, Lit = Ui.Accent, Dim = new Color(Ui.Muted, 0.6f),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
        });
        row.AddChild(box);
        return row;
    }

    /// <summary>An icon cut to what it draws, its clear margin gone: the class icons'
    /// octagon fills <paramref name="px"/> rather than two thirds of it. Kept per name and size.</summary>
    private static Texture2D? QuickIconTight(string name, int px)
    {
        string key = $"{name}@{px}~tight";
        if (QhTight.TryGetValue(key, out Texture2D? had))
            return had;
        // rastered as large as the box drawn, before the cut: the octagon is then px across
        Texture2D? tex = QIcon(name, px * 3 / 2);
        Texture2D? cut = null;
        if (tex?.GetImage() is { } image && image.GetUsedRect() is { Size.X: > 0 } used)
            cut = new AtlasTexture { Atlas = tex, Region = used };
        return QhTight[key] = cut ?? tex;
    }

    private static readonly Dictionary<string, Texture2D?> QhTight = new();

    /// <summary>A class's icon in <c>Icons/</c>: <c>class_LT</c> and so on.</summary>
    private static string QuickClassIcon(QuickSquad.Class c) => $"class_{c.Short}";
}
