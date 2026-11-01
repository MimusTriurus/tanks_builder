using System.Collections.Generic;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The action panel, bottom centre (docs/hud3d-plan.md, «Панель действий»;
/// by the unit's card until the roster, 2026-10-30): what is left of the chosen unit's turn, in
/// a match. GDD match.md «Структура хода» (U-22): the move, then two slots,
/// one of each - the <b>attack</b> and the <b>bonus act</b>.
/// <list type="bullet">
/// <item><b>Three groups, each in its own frame</b> (the author's layout,
/// 2026-10-31; the move's first since 2026-11-01): the move's - <b>Ход</b> 1 -,
/// the attack's - <b>Атака</b> 2, <b>Засада</b> 3 and, for a unit with the
/// rounds, <b>Зажиг.</b> - and the bonus act's, numbered on from 4 in the
/// order they stand. A spent slot's frame dims and its buttons stay, grey
/// and off (2026-11-01; a tick and the slot's name stood in their place
/// before).</item>
/// <item><b>Ход</b> - the move's mode, the one with no other on: pressed,
/// every mode that takes the left button is put off (<see cref="MoveOn"/>),
/// and the zone and the click are the move's again. Lit while it is so;
/// grey once the unit may not move this turn (<c>MatchMayMove</c>).</item>
/// <item><b>Атака</b> - a mode: the left button on a tank then fires as the
/// right one does (<c>AimAt</c>), and an own unit is a target only so - by
/// default ours are not fired on (<c>MatchAims</c>, <c>FriendOn</c>). The
/// right button on an enemy fires with the mode off as ever. Spent with the
/// round (<c>Launch</c>). <b>Засада</b> (Д7) - a mode, the left button on a
/// hex picks the axis (Tank3DBench.Ambush). <b>Зажиг.</b> (Д8) - the next
/// shot is incendiary (Tank3DBench.Incendiary), no key.</item>
/// <item>The bonus acts: <b>Таран</b> - a mode (Tab too), the left button on
/// a tank then rams it, own ones too, and on the hex past the wall the hull
/// faces rams the wall (Д5; one act, one button since 2026-11-01);
/// <b>Тушить</b> - at once (J too, <see cref="Douse"/>); the kit's
/// <b>Дым</b>, <b>Мина</b>, <b>Ремонт</b>, <b>Лебёдка</b> (Д8).</item>
/// <item><b>A button is an icon</b> (2026-11-01): the act's picture from
/// <c>Icons/</c> and its key's number beside it, the name in the tooltip;
/// without the picture on disk the button says the name, as before.</item>
/// <item><b>What may not be done is not there</b> (the author's choice, as
/// with the roses): no <b>Тушить</b> on a hull that is not burning, no kit's
/// button without the kit.</item>
/// <item>Keys 1 to 9 press the buttons by their numbers (1 the move's since
/// 2026-11-01, the rest one on from before); the roster's units
/// are F1 to F8 (the digits were theirs from 2026-10-30 to 2026-10-31).</item>
/// </list>
/// </summary>
public sealed partial class Tank3DBench
{
    private HBoxContainer? _acts;
    private PanelContainer? _groupMove, _groupAttack, _groupBonus;
    private Button? _actMove, _actAttack, _actRam, _actDouse;
    /// <summary>«Атака» on: the left button on a tank fires, and ours may be fired on.</summary>
    private bool _attackMode;
    /// <summary>The bonus group's buttons in their order, and what each does
    /// - the numbers run over the ones shown.</summary>
    private readonly List<(Button Button, string Text, System.Action Act)> _bonusActs = new();
    private System.Action? _attackAct, _ambushAct;
    /// <summary>The slots and the bonus buttons shown, as last drawn: the
    /// frames and the numbers are redone when they change, not every frame.</summary>
    private int _slotsShown = -1, _bonusShown = -1;
    /// <summary>The first bonus button's number: after «Ход», «Атака», «Засада».</summary>
    private const int BonusKeyFirst = 4;
    /// <summary>A button's icon twice: as drawn, and grey for a spent slot.</summary>
    private sealed class ActArt
    {
        public required Button Button;
        public required string Name;
        public Texture2D? On, Off;
    }

    /// <summary>Each button with an icon, and the pixels the icons are
    /// rasterised at now: the stretch of the window, redone when it changes.</summary>
    private readonly List<ActArt> _actIcons = new();
    private int _actIconRaster;

    private static readonly Color SlotDim = new(0.42f, 0.43f, 0.40f);
    /// <summary>The icon's side on a button in the 1600 × 900 layout, px; on
    /// screen it is this times the window's stretch (<see cref="ActIconsFit"/>).</summary>
    private const int ActIconPx = 36;

    private void BuildActs(CanvasLayer layer)
    {
        // bottom centre (the author's layout, 2026-10-31): the move's frame,
        // the attack's, the bonus act's, a gap between
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
        HBoxContainer move;
        (_groupMove, move) = Group();
        // the move (2026-11-01): the other modes off, the click the move's
        _actMove = ActButton(move, "move", "Ход", "1", toggle: true, MoveOn);
        HBoxContainer attack;
        (_groupAttack, attack) = Group();
        _attackAct = AttackToggle;
        _actAttack = ActButton(attack, "attack", "Атака", "2", toggle: true, _attackAct);
        // the attack held back (Д7, Tank3DBench.Ambush): a mode
        _ambushAct = AmbushToggle;
        _actAmbush = ActButton(attack, "ambush", AmbushText, "3", toggle: true, _ambushAct);
        // the incendiary round (Д8, Tank3DBench.Incendiary): the next shot's, no key
        _actFiery = ActButton(attack, "incendiary", "Зажиг.", "", toggle: true, FieryToggle);
        HBoxContainer bonus;
        (_groupBonus, bonus) = Group();
        // a tank or the wall ahead (Д5): a mode, the click says which
        _actRam = BonusButton(bonus, "ram", "Таран", toggle: true, RamToggle);
        // the grenade (Д8, Tank3DBench.Smoke): a mode
        _actSmoke = BonusButton(bonus, "smoke", "Дым", toggle: true, SmokeToggle);
        // the kit's acts on a hex (Д8, Tank3DBench.Gear): modes
        _actMine = BonusButton(bonus, "mine", "Мина", toggle: true, () => GearToggle(Gear.Mine));
        _actRepair = BonusButton(bonus, "repair", "Ремонт", toggle: true, () => GearToggle(Gear.Repair));
        _actWinch = BonusButton(bonus, "winch", "Лебёдка", toggle: true, () => GearToggle(Gear.Winch));
        _actDouse = BonusButton(bonus, "douse", "Тушить", toggle: false, () => Execute(new Command(CommandKind.Douse)));
        layer.AddChild(_acts);
        ActIconsFit();
        GetTree().Root.SizeChanged += ActIconsFit;
    }

    /// <summary>The icons rasterised at the pixels they take on screen: the
    /// panel is laid out at 1600 × 900 and stretched with the window
    /// (<c>canvas_items</c>), so a raster of the layout's 36 would be blown up
    /// soft in a bigger window. Redone only when the size in pixels changes.</summary>
    private void ActIconsFit()
    {
        Window w = GetWindow();
        Vector2I size = w.Size, layout = w.ContentScaleSize;
        float stretch = layout.X > 0 && layout.Y > 0
            ? Mathf.Min(size.X / (float)layout.X, size.Y / (float)layout.Y) : 1.0f;
        int px = Mathf.Max(16, Mathf.RoundToInt(ActIconPx * stretch * w.ContentScaleFactor));
        if (px == _actIconRaster)
            return;
        _actIconRaster = px;
        Log.Print($"tank3d: acts: icons at {px} px, window {size.X}x{size.Y}");
        foreach (ActArt art in _actIcons)
            ActPaint(art, px);
    }

    /// <summary>The icon of <paramref name="art"/> at <paramref name="px"/>,
    /// both ways, and the one its button's state wants put on it.</summary>
    private static void ActPaint(ActArt art, int px)
    {
        if (ActImage(art.Name, px) is not { } image)
            return;
        art.On = ImageTexture.CreateFromImage(image);
        // grey: the brightness kept low and flat, a little see-through - the
        // shape still reads, the colour that says "to be had" is gone
        for (int y = 0; y < image.GetHeight(); y++)
            for (int x = 0; x < image.GetWidth(); x++)
            {
                Color c = image.GetPixel(x, y);
                float l = 0.22f + 0.45f * (0.3f * c.R + 0.59f * c.G + 0.11f * c.B);
                image.SetPixel(x, y, new Color(l, l, l, c.A * 0.8f));
            }
        art.Off = ImageTexture.CreateFromImage(image);
        art.Button.Icon = art.Button.Disabled ? art.Off : art.On;
    }

    /// <summary>A slot's frame: its buttons in a row.</summary>
    private (PanelContainer, HBoxContainer) Group()
    {
        var frame = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 6);
        frame.AddChild(row);
        _acts!.AddChild(frame);
        return (frame, row);
    }

    /// <summary>A button on or off: off - its slot spent - it is grey and
    /// does nothing. Changes it only when its state does.</summary>
    private void ActOn(Button b, bool on)
    {
        if (b.Disabled != on)
            return;
        b.Disabled = !on;
        if (!on)
            b.SetPressedNoSignal(false);
        for (int i = 0; i < _actIcons.Count; i++)
            if (_actIcons[i].Button == b)
                b.Icon = on ? _actIcons[i].On : _actIcons[i].Off;
    }

    private Button BonusButton(HBoxContainer row, string icon, string name, bool toggle, System.Action pressed)
    {
        Button b = ActButton(row, icon, name, "", toggle, pressed);
        _bonusActs.Add((b, name, pressed));
        return b;
    }

    /// <summary>A button: the act's icon and its key, or its name and key
    /// when the icon is not on disk.</summary>
    private Button ActButton(HBoxContainer row, string icon, string name, string key, bool toggle, System.Action pressed)
    {
        var b = new Button
        {
            ToggleMode = toggle,
            FocusMode = Control.FocusModeEnum.None,
            CustomMinimumSize = new Vector2(0.0f, 48.0f),
        };
        var art = new ActArt { Button = b, Name = icon };
        ActPaint(art, ActIconPx);
        ActLabel(b, name, key);
        b.AddThemeConstantOverride("icon_max_width", ActIconPx);
        b.AddThemeFontSizeOverride("font_size", b.Icon is null ? 16 : 14);
        b.AddThemeStyleboxOverride("normal", ActBox(new Color(0.46f, 0.48f, 0.41f), 1));
        b.AddThemeStyleboxOverride("hover", ActBox(CardPale, 1));
        b.AddThemeStyleboxOverride("pressed", ActBox(CardSpend, 2));
        b.AddThemeStyleboxOverride("hover_pressed", ActBox(CardSpend, 2));
        // spent: the box darker and its rim dim, the key's number grey
        StyleBoxFlat off = ActBox(SlotDim, 1);
        off.BgColor = new Color(0.17f, 0.18f, 0.15f, 0.9f);
        b.AddThemeStyleboxOverride("disabled", off);
        b.AddThemeColorOverride("font_pressed_color", CardSpend);
        b.AddThemeColorOverride("font_hover_pressed_color", CardSpend);
        b.AddThemeColorOverride("font_disabled_color", SlotDim);
        b.Pressed += pressed;
        row.AddChild(b);
        if (b.Icon is not null)
            _actIcons.Add(art);
        return b;
    }

    /// <summary>What a button says: the key beside the icon and the name in
    /// the tooltip, or the name and the key without the icon.</summary>
    private static void ActLabel(Button b, string name, string key)
    {
        b.Text = b.Icon is not null ? key : key.Length > 0 ? $"{name}   {key}" : name;
        b.TooltipText = key.Length > 0 ? $"{name} ({key})" : name;
    }

    /// <summary>The act's icon, <c>Icons/&lt;name&gt;.svg</c> rasterised
    /// <paramref name="px"/> on a side; null with no file.</summary>
    private static Image? ActImage(string name, int px)
    {
        string path = $"{AssetRoot.Icons}/{name}.svg";
        if (!System.IO.File.Exists(path))
        {
            Log.Print($"tank3d: acts: no {path}, the button says its name");
            return null;
        }
        var image = new Image();
        return image.LoadSvgFromString(System.IO.File.ReadAllText(path), px / 64.0f) == Error.Ok ? image : null;
    }

    private static StyleBoxFlat ActBox(Color rim, int width)
    {
        // a mid olive, not the black of the frames: the icons' ink and shadow
        // are near-black and vanished on it (2026-11-01)
        var box = new StyleBoxFlat { BgColor = new Color(0.27f, 0.29f, 0.23f, 0.95f), BorderColor = rim };
        box.SetBorderWidthAll(width);
        box.SetCornerRadiusAll(8);
        box.ContentMarginLeft = box.ContentMarginRight = 8.0f;
        return box;
    }

    /// <summary>A group's frame: pale while its slot is to be had, dim once spent.</summary>
    private static void GroupStyle(PanelContainer frame, bool open)
    {
        StyleBoxFlat box = ActBox(open ? CardPale : SlotDim, open ? 2 : 1);
        box.BgColor = new Color(0.08f, 0.085f, 0.075f, 0.55f);
        box.SetCornerRadiusAll(12);
        box.ContentMarginLeft = box.ContentMarginRight = box.ContentMarginTop = box.ContentMarginBottom = 6.0f;
        frame.AddThemeStyleboxOverride("panel", box);
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
        // the zone searched afresh each time the mode comes on
        _attackFound = false;
        if (_attackMode)
        {
            _ramMode = _smokeMode = _ambushMode = false;
            _ambushPick = null;
            _gearMode = Gear.None;
            // a cell the player fixed for the move is let go: the click is the
            // attack's now (the bench's firing post is its own, Tank3DBench.Post)
            if (!_postLock)
                Unlock();
        }
        _orderNote = _attackMode ? "атака: щёлкни по танку, свой тоже" : "атака выключена";
    }

    // --- the attack's zone ----------------------------------------------------------

    /// <summary>The attack's zone ink: orange - between the ambush's red and the bonus acts' amber.</summary>
    private static readonly Color AttackInk = new(1.0f, 0.47f, 0.12f);
    private readonly List<Vector2I> _attackCells = new();
    private (Vector2I Here, int Reach, int Smoke, int Unit) _attackKey;
    private bool _attackFound;

    /// <summary>«Атака» on in a match: the move's zone gives way to this one
    /// (the author's note, 2026-11-01). The firing post stays - a target off
    /// the axes still shows its way and ghost (Tank3DBench.Post).</summary>
    private bool AttackZoneOn => _attackMode && Match && !MatchShotSpent;

    /// <summary>
    /// With «Атака» on, in place of the move's zone, drawn as it is but
    /// orange: the hexes the gun may fire at from where it stands - every axis
    /// out of the own hex, each hex asked of the rules the shot is
    /// (<see cref="ShotWhy"/>'s: the distance's gates, <see cref="LaneShut"/>,
    /// a wall on the axis), an axis given up past the first hex its lane is
    /// shut before. Searched again when the hex, the board's tanks, the smoke
    /// or the unit change, not every frame.
    /// </summary>
    private void AttackZone(ImmediateMesh mesh)
    {
        var key = (CellHere, _reachVersion, _smokeLeft.Count, _unitId);
        if (!_attackFound || key != _attackKey)
        {
            _attackKey = key;
            _attackFound = true;
            AttackCells();
        }
        _zoneFill.Clear();
        _zoneFill.AddRange(_attackCells);
        // on the relief as the move's zone is: off the hexes' edges
        if (_relief is not null)
        {
            ReliefZoneShow(AttackInk);
            return;
        }
        if (_attackCells.Count == 0)
            return;
        Vector2[] corner = Corners();
        mesh.SurfaceBegin(Mesh.PrimitiveType.Triangles);
        var tint = new Color(AttackInk, ZoneTone);
        foreach (Vector2I cell in _zoneFill)
            ZoneCell(mesh, tint, cell, corner);
        ZoneBand(corner, 1);
        var line = new Color(AttackInk, 0.95f);
        for (int i = 0; i < _band.Count; i++)
        {
            mesh.SurfaceSetColor(line);
            mesh.SurfaceAddVertex(_band[i]);
        }
        mesh.SurfaceEnd();
    }

    private void AttackCells()
    {
        _attackCells.Clear();
        if (_field is null)
            return;
        Vector2I here = CellHere;
        foreach (int axis in HexField.EdgeHeadings)
        {
            Vector2I at = here;
            for (int k = 1; k <= 32; k++)
            {
                Vector2I next = HexField.Step(at, axis);
                if (next == at || !_field.InBounds(next))
                    break;
                // a gun's round goes into a standing wall on the axis (a
                // destroyer's through its first brick one); the bomb goes over
                if (!_profile.Lobs && WallOnAxis(here, axis, k, passOne: WallPass(here, next)) is not null)
                    break;
                bool tank = UnitOn(next) is not null;
                string shut = LaneShut(here, next, out Vector2I? stop);
                if (shut.Length > 0 && stop != next)
                    break;
                if (shut.Length == 0 && Gunnery.MayTarget(_profile.Lobs, k, tank) == Gunnery.Distance.Fits)
                    _attackCells.Add(next);
                at = next;
            }
        }
        Log.Print($"tank3d: attack zone from {MapAt(here)}: {_attackCells.Count} hexes");
    }

    /// <summary>«Ход»: every mode that takes the left button put off - the
    /// attack's, the ambush's, the ram's, a bonus act's hex - so the zone and
    /// the click are the move's. The incendiary round stays: it is the next
    /// shot's, not the click's.</summary>
    private void MoveOn()
    {
        if (MatchMoveWhy() is { Length: > 0 } why)
        {
            _orderNote = why;
            return;
        }
        _attackMode = _ambushMode = _ramMode = _smokeMode = false;
        _ambushPick = null;
        _ramKey = "";
        _gearMode = Gear.None;
        _orderNote = "ход: щёлкни по гексу в зоне";
    }

    /// <summary>No mode that takes the left button is on: the click is the move's.</summary>
    private bool MoveModeOn => !_attackMode && !_ambushMode && !_ramMode && !PickMode;

    /// <summary>Keys 1 to 9 in a match: the panel's buttons by their numbers -
    /// 1 «Ход», 2 «Атака», 3 «Засада», 4 on the bonus acts shown. F1 to F8 take the
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
        // a grey button's key does nothing, as its click does not
        if (k == 0 && !_actMove!.Disabled)
            MoveOn();
        else if (k == 1 && _actAttack!.Visible && !_actAttack.Disabled)
            _attackAct!();
        else if (k == 2 && _actAmbush!.Visible && !_actAmbush.Disabled)
            _ambushAct!();
        for (int i = 0, shown = BonusKeyFirst - 1; k >= BonusKeyFirst - 1 && i < _bonusActs.Count; i++)
            if (_bonusActs[i].Button.Visible && shown++ == k)
            {
                if (!_bonusActs[i].Button.Disabled)
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
        // the ram on the hex past the wall the hull faces: the wall's (Д5)
        if (_ramMode && WallAhead && PickAt(screen) is (var point, false, false)
            && _field!.FlatCellAt(Board(point)) == HexField.Step(CellHere, HullSide()))
        {
            _ramMode = false;
            Execute(new Command(CommandKind.RamWall, Side: HullSide()));
            return true;
        }
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
        bool attack = !MatchShotSpent, bonus = !MatchBonusSpent, move = MatchMayMove;
        int slots = (attack ? 1 : 0) | (bonus ? 2 : 0) | (move ? 4 : 0);
        if (slots != _slotsShown)
        {
            _slotsShown = slots;
            GroupStyle(_groupMove!, move);
            GroupStyle(_groupAttack!, attack);
            GroupStyle(_groupBonus!, bonus);
        }
        // a spent slot keeps its buttons, grey and off (the author's note,
        // 2026-11-01): what the unit has stays in view, what it did is told
        // by the grey
        ActOn(_actMove!, move);
        _actMove!.SetPressedNoSignal(move && MoveModeOn);
        ActOn(_actAttack!, attack);
        ActOn(_actAmbush!, attack);
        ActOn(_actFiery!, attack);
        for (int i = 0; i < _bonusActs.Count; i++)
            ActOn(_bonusActs[i].Button, bonus);
        _actAttack!.SetPressedNoSignal(attack && _attackMode);
        _actAmbush!.SetPressedNoSignal(attack && _ambushMode);
        _actFiery!.Visible = RigBurns;
        _actFiery.SetPressedNoSignal(attack && _fieryMode);
        _actRam!.SetPressedNoSignal(bonus && _ramMode);
        _actSmoke!.Visible = RigSmokes;
        _actSmoke.SetPressedNoSignal(bonus && _smokeMode);
        _actMine!.Visible = RigHas(Kit.MineLayer);
        _actMine.SetPressedNoSignal(bonus && _gearMode == Gear.Mine);
        _actRepair!.Visible = RigHas(Kit.RepairKit);
        _actRepair.SetPressedNoSignal(bonus && _gearMode == Gear.Repair);
        _actWinch!.Visible = RigHas(Kit.Winch);
        _actWinch.SetPressedNoSignal(bonus && _gearMode == Gear.Winch);
        _actDouse!.Visible = Burning(_unitId);
        // the numbers run over the bonus buttons shown, from 4
        int shownMask = 0;
        for (int i = 0; i < _bonusActs.Count; i++)
            if (_bonusActs[i].Button.Visible)
                shownMask |= 1 << i;
        if (shownMask != _bonusShown)
        {
            _bonusShown = shownMask;
            for (int i = 0, n = BonusKeyFirst; i < _bonusActs.Count; i++)
                if (_bonusActs[i].Button.Visible)
                    ActLabel(_bonusActs[i].Button, _bonusActs[i].Text, n <= 9 ? $"{n++}" : "");
        }
    }
}
