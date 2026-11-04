using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The quick battle against the AI (GDD meta.md, «Быстрый бой: PvP и PvAI»;
/// the author's М-5, 2026-11-02): <c>--quick</c> on <c>Match3D.tscn</c>
/// (<see cref="Quick"/>). Two screens before the board is built - the
/// lobby's parameters and the squad's build - then the placement, then the
/// squads revealed and the toss.
///
/// <b>The prototype's layout, piece for piece</b> (the artifact «Сборка
/// отряда: быстрый бой», PvAI; its dark palette, <see cref="Ui.Bg"/>): the
/// lobby a sheet of boxes; the build a header with the battle's facts and
/// the budget's pips, the battlefield down the left, three machine cards -
/// the machine's name and cost, its picture, the five classes as hexes, its
/// two branches as hex nodes linked down, what it carries - and the bar of
/// the quick start and the hands; a floating tooltip; the parameters and the
/// map in a dialog over it. The pieces are drawn (<see cref="QuickPad"/>).
/// <list type="bullet">
/// <item><b>Before the board.</b> The screens run in <c>Building</c> after
/// the tile, before the world: the map is read off its file for the minimap,
/// and changing it costs nothing. «К сборке» from the placement reloads the
/// scene with the setup kept (<see cref="_quickBack"/>).</item>
/// <item><b>Parameters</b> - the map (those with a zone for each side), the
/// player's side (south or north: the zone's half of the board), the two
/// budgets, the AI's doctrine or a random one. Changeable until «В бой», on
/// the build screen too; a budget cut below what is spent takes the top
/// levels off (<see cref="QuickSquad.Trim"/>).</item>
/// <item><b>The build</b> (<see cref="QuickSquad"/>): a class free, a
/// level's node buys the branch up to it for its number in points, the top
/// open one clicked gives it back, a branch's head moves the level over.
/// An upgrade the bench has no rule for is bought and marked «*»; it is not
/// in the machine's kit.</item>
/// <item><b>The AI</b> builds blind as «К расстановке» is pressed, by its
/// doctrine and budget (<see cref="QuickSquad.ForAi"/>), and stands on its
/// homes - its own placement is the AI plan's (docs/ai-plan.md). Its side is
/// still played by hand.</item>
/// <item><b>The placement</b> is the player's side's alone; its «В бой»
/// reveals both squads and says the toss (<see cref="QuickReveal"/>).</item>
/// </list>
/// <c>--quick-do "build;company:2;shot:PATH;go"</c> drives the screens for a
/// capture (<see cref="QuickScript"/>).
/// </summary>
public sealed partial class Tank3DBench
{
    /// <summary>The match is a quick battle against the AI - <c>--quick</c>.</summary>
    [Export] public bool Quick { get; set; }

    private sealed class QuickSetup
    {
        public string Map = "duel12";
        public Side Player = Side.Green;
        public int Budget = QuickSquad.BudgetDefault, AiBudget = QuickSquad.BudgetDefault;
        /// <summary>The AI's doctrine, an index into <see cref="QuickSquad.Doctrines"/>; null, random.</summary>
        public int? Doctrine;
        public QuickSquad.Slot[] Squad = QuickSquad.Empty();
        /// <summary>The ready squad the squad was last taken from; null, none since it was cleared.</summary>
        public QuickSquad.Company? Preset;
        /// <summary>The name the player gave the squad; null, the preset's name or the default.</summary>
        public string? Name;
        /// <summary>The AI's, made at «К расстановке».</summary>
        public QuickSquad.Slot[] Ai = QuickSquad.Empty();
        public QuickSquad.Doctrine? AiDoctrine;
    }

    /// <summary>The setup carried over the scene's reload, «К сборке» from the placement.</summary>
    private static QuickSetup? _quickBack;

    /// <summary>The battle's setup once the screens are done; null off a quick battle.</summary>
    private QuickSetup? _quick;

    private Side QuickAi => Across(_quick!.Player);

    // --- the screens -------------------------------------------------------------

    private enum QuickPage { Lobby, Build }
    private enum QuickModal { None, Params, Map, Ask }

    /// <summary>The question the <see cref="QuickModal.Ask"/> dialog puts (<see cref="QuickAsk"/>).</summary>
    private (string Head, string Text, string Yes, string No, Action Act)? _qAsk;

    private QuickSetup _qs = new();
    private QuickPage _qPage;
    private QuickModal _qModal;
    private CanvasLayer? _qLayer;
    private Control? _qRoot, _qOver;
    private bool _qGo;
    private readonly Dictionary<string, BoardMap> _qMaps = new();
    private int _qArtLeft;

    /// <summary>In <c>Building</c>, after the tile: the lobby and the build,
    /// until «К расстановке»; then the AI's squad and the map to build.</summary>
    private async Task QuickScreens()
    {
        QuickSetup q = _quickBack ?? new QuickSetup();
        bool back = _quickBack is not null;
        _quickBack = null;
        List<string> pool = QuickPool();
        if (!back && _mapNamed)
            q.Map = _mapName;
        if (!pool.Contains(q.Map) && pool.Count > 0)
            q.Map = pool[0];
        // the hangar opens on the first ready squad, taken (author 2026-11-03), not empty
        if (_qHangar && !back && q.Budget >= QuickSquad.Size * QuickSquad.CompanyLevel)
        {
            q.Preset = QuickSquad.Companies[0];
            q.Squad = QuickSquad.OfCompany(q.Preset);
        }
        _qs = q;
        _qPage = back ? QuickPage.Build : QuickPage.Lobby;
        _qLayer = new CanvasLayer { Name = "QuickScreens", Layer = 20 };
        AddChild(_qLayer);
        var ground = new ColorRect { Color = Ui.Bg, MouseFilter = Control.MouseFilterEnum.Ignore };
        ground.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _qLayer.AddChild(ground);
        _qRoot = new MarginContainer { Theme = new Theme { DefaultFontSize = 15 } };
        _qRoot.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        foreach (string side in new[] { "left", "right", "top", "bottom" })
            _qRoot.AddThemeConstantOverride($"margin_{side}", 22);
        _qLayer.AddChild(_qRoot);
        _qTips = new UiTip();
        _qLayer.AddChild(_qTips);
        // the hangar loads all it will show behind a loading screen first
        if (_qHangar)
            await QuickHangarLoading();
        QuickShow();
        QuickArt();
        QuickScript();
        Log.Print($"tank3d: quick: the screens - {(back ? "back to the build" : "the lobby")}, maps {string.Join(", ", pool)}");
        while (!_qGo)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            QuickStageTurn();
            QuickHangarSpin();
            // the podium's views are drawn in screen pixels: a window resized draws them
            // anew, once it has held its size a moment - a drag would reload them every frame
            Vector2I window = GetWindow().Size;
            if (_qHangar && window != _qhWindow)
            {
                _qhResized = _qhWindow == Vector2I.Zero ? 0 : Time.GetTicksMsec();
                _qhWindow = window;
            }
            if (_qhResized > 0 && Time.GetTicksMsec() - _qhResized > 300)
            {
                _qhResized = 0;
                if (_qPage != QuickPage.Lobby)
                    QuickShow();
                // the spares were drawn for the old size: warmed anew for this one
                QuickSpareFree();
                await QuickStageWarm();
            }
        }
        foreach (int at in _qStage.Keys.ToArray())
            QuickStageDrop(at);
        QuickSpareFree();
        _qLayer.QueueFree();
        _qLayer = null;
        _qRoot = _qOver = null;
        _qTips = null;

        // the AI builds now, blind to the player's squad (meta.md)
        q.AiDoctrine = q.Doctrine is int d ? QuickSquad.Doctrines[d]
                       : QuickSquad.Doctrines[Random.Shared.Next(QuickSquad.Doctrines.Length)];
        q.Ai = QuickSquad.ForAi(q.AiDoctrine, q.AiBudget, Random.Shared);
        _quick = q;
        _mapName = q.Map;
        _mapNamed = true;
        Log.Print($"tank3d: quick: {q.Map}, the player {SideName(q.Player)} on {q.Budget} - {QuickSquad.Said(q.Squad)}; "
                 + $"the AI {q.AiDoctrine.Name} on {q.AiBudget} - {QuickSquad.Said(q.Ai)}");
    }

    /// <summary>The maps a quick battle plays: a zone for each side and a home on each.</summary>
    private List<string> QuickPool()
    {
        var pool = new List<string>();
        foreach (string name in MapFile.Names())
            if (QuickMap(name) is { } m && m.Arena.Zone(Parking.Green).Count > 0 && m.Arena.Zone(Parking.Brown).Count > 0)
            {
                string[] sides = Parking.SidesOf(m.Parked);
                if (sides.Contains(Parking.Green) && sides.Contains(Parking.Brown))
                    pool.Add(name);
            }
        return pool;
    }

    private BoardMap? QuickMap(string name)
    {
        if (_qMaps.TryGetValue(name, out BoardMap? had))
            return had;
        try
        {
            return _qMaps[name] = MapFile.Load(name);
        }
        catch (Exception e)
        {
            Log.Print($"tank3d: quick: {name}: {e.Message}");
            return null;
        }
    }

    /// <summary>The five classes' pictures in both paints, the roster's own
    /// (<see cref="RosterArtOf"/>); the page drawn again as each comes in.</summary>
    private async void QuickArt()
    {
        foreach (Side side in new[] { _qs.Player, Across(_qs.Player) })
            foreach ((string model, string _, string _) in Lineup.Pairs)
            {
                if (_rosterArt.ContainsKey((model, side)))
                    continue;
                _qArtLeft++;
                await RosterArtOf(model, side);
                _qArtLeft--;
                if (_qRoot is null)
                    return;
                if (side == _qs.Player)
                    QuickShow();
            }
    }

    // --- the live models on the cards ----------------------------------------------

    /// <summary>A card's stage, px high: the model at a size the tree under it can spare.</summary>
    private const float QStageHigh = 150.0f;

    /// <summary>Each card's live model, kept over the page's rebuilds: a click
    /// redraws the page, the model is loaded again only for a new class or paint.</summary>
    private readonly Dictionary<int, (string Class, Side Side, Vector2I Px, SubViewportContainer Box, Node3D Rig)> _qStage = new();

    /// <summary>A full turn of the stages, seconds.</summary>
    private const double QTurnSeconds = 16.0;

    /// <summary>Degrees the drags have added to the stages' common heading.</summary>
    private float _qTurnBy;

    /// <summary>When the stages' turn started, ms; the hangar's front machine
    /// and its slot that started it.</summary>
    private ulong _qTurnFrom;
    private int _qhFrontAt = -1;
    private string? _qhFront;

    /// <summary>Every card's model at one heading - the clock's, plus the drags' -
    /// so the squad turns as one and a model brought in joins it in step. The
    /// hangar turns only the machine in front; the two behind stand at the
    /// roster's heading. Each frame of the screens; allocates nothing.</summary>
    private void QuickStageTurn()
    {
        // the hangar's front machine, new there - another class, another slot - starts
        // its turn from the roster's three-quarters front, not wherever the clock stood
        // (author 2026-11-03: a class changed came up stern first)
        if (_qHangar)
        {
            string? front = _qs.Squad[_qhAt].Class;
            if (_qhAt != _qhFrontAt || front != _qhFront)
            {
                _qhFrontAt = _qhAt;
                _qhFront = front;
                _qTurnFrom = Time.GetTicksMsec();
                _qTurnBy = 0.0f;
            }
        }
        // a release the stage never heard - its view rebuilt or gone under the press -
        // must not stop the clock for good
        if (_qTurnHeld && !Input.IsMouseButtonPressed(MouseButton.Left))
        {
            _qTurnHeld = false;
            _qTurnFrom = Time.GetTicksMsec();
        }
        float yaw = RosterArtYaw + _qTurnBy + (_qTurnHeld ? 0.0f : QuickClockTurn());
        foreach (var stage in _qStage)
            if (IsInstanceValid(stage.Value.Rig))
                stage.Value.Rig.RotationDegrees = new Vector3(0.0f, _qHangar && stage.Key != _qhAt ? RosterArtYaw : yaw, 0.0f);
    }

    /// <summary>The left button is down on a stage: the models turn by the mouse alone,
    /// the clock stopped (author 2026-11-04), and go on from there once it is up.</summary>
    private bool _qTurnHeld;

    /// <summary>Degrees the clock has turned the stages since <see cref="_qTurnFrom"/>.</summary>
    private float QuickClockTurn() =>
        (float)((Time.GetTicksMsec() - _qTurnFrom) / 1000.0 % QTurnSeconds / QTurnSeconds * 360.0);

    /// <summary>Slot <paramref name="at"/>'s model turning on its stage, the
    /// roster's own scene live (<see cref="RosterScene"/>): it spins slowly, a
    /// drag turns it by hand. Null when the model does not load - the card
    /// falls back on the picture. With <paramref name="fixedPx"/> the view is
    /// that size and its box does not stretch: the hangar scales the box
    /// (<c>Tank3DBench.Hangar</c>).</summary>
    private SubViewportContainer? QuickStage(int at, string cls, Side side, Vector2I? fixedPx = null)
    {
        // a fixed view - the hangar's podium - drawn in screen pixels, not the layout's:
        // stretched with the window a layout-sized one went soft (1.6× on a 2560 × 1440
        // screen); the box is scaled back by as much (meta q_px, QuickHangarRing)
        float px = fixedPx is null ? 1.0f : Mathf.Max(1.0f, ScreenScale());
        var size = fixedPx is { } f ? new Vector2I(Mathf.RoundToInt(f.X * px), Mathf.RoundToInt(f.Y * px))
            : new Vector2I(340, (int)QStageHigh);
        if (_qStage.TryGetValue(at, out var had) && had.Class == cls && had.Side == side && had.Px == size && IsInstanceValid(had.Box))
        {
            had.Box.GetParent()?.RemoveChild(had.Box);
            return had.Box;
        }
        QuickStageDrop(at);
        // a view the hangar keeps spare for the class: no model loaded, no hitch
        for (int i = 0; i < _qSpare.Count; i++)
        {
            var spare = _qSpare[i];
            if (spare.Class != cls || spare.Side != side || spare.Px != size || !IsInstanceValid(spare.Box))
                continue;
            _qSpare.RemoveAt(i);
            QuickStageLive(spare.Box, true);
            _qStage[at] = spare;
            QuickStageTurn();
            return spare.Box;
        }
        string? model = Lineup.Pairs.FirstOrDefault(p => p.Class == cls).Model;
        if (_qLayer is null || model is null)
            return null;
        var box = new SubViewportContainer
        {
            Stretch = fixedPx is null, MouseFilter = Control.MouseFilterEnum.Pass, MouseDefaultCursorShape = Control.CursorShape.Drag,
        };
        if (fixedPx is null)
            box.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        // into the tree for the framing, which reads the placed meshes; the card takes it after
        _qLayer.AddChild(box);
        if (RosterScene(model, side, size, box, live: true, even: fixedPx is not null) is not var (_, rig))
        {
            box.QueueFree();
            return null;
        }
        // a drag turns every model at once: they keep one heading (QuickStageTurn)
        box.GuiInput += e =>
        {
            if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left } b && b.Pressed != _qTurnHeld)
            {
                // the clock's share folded into the drags' on the press, restarted on the
                // release: the model neither jumps nor turns while held
                if (b.Pressed)
                    _qTurnBy += QuickClockTurn();
                else
                    _qTurnFrom = Time.GetTicksMsec();
                _qTurnHeld = b.Pressed;
            }
            else if (e is InputEventMouseMotion m && m.ButtonMask.HasFlag(MouseButtonMask.Left))
                _qTurnBy += m.Relative.X / px * 0.7f;
        };
        _qLayer.RemoveChild(box);
        if (fixedPx is not null)
        {
            box.Size = size;
            box.SetMeta("q_px", px);
        }
        _qStage[at] = (cls, side, size, box, rig);
        QuickStageTurn();
        return box;
    }

    /// <summary>The hangar's views a slot let go, kept by class - up to as many as a
    /// squad may have of one - and drawing nothing until a slot takes one back: a model
    /// loads in 220-400 ms, and a ready squad changed reloaded two or three, a hitch of
    /// half a second to a second (author 2026-11-03). Warmed for every class at the
    /// hangar's start (<see cref="QuickStageWarm"/>), freed with the screens or when the
    /// window's size, and with it the views', changes.</summary>
    private readonly List<(string Class, Side Side, Vector2I Px, SubViewportContainer Box, Node3D Rig)> _qSpare = new();

    /// <summary>A view drawing every frame, or parked drawing nothing.</summary>
    private static void QuickStageLive(SubViewportContainer box, bool on)
    {
        foreach (Node n in box.GetChildren())
            if (n is SubViewport v)
                v.RenderTargetUpdateMode = on ? SubViewport.UpdateMode.Always : SubViewport.UpdateMode.Disabled;
    }

    /// <summary>The views <see cref="QuickStageWarm"/> has yet to make: every class, as
    /// many as a squad may have of it (<see cref="QuickSquad.SameClass"/> - the player may
    /// take two of any), less those held or parked.</summary>
    private List<QuickSquad.Class> QuickStageWanted()
    {
        var wanted = new List<QuickSquad.Class>();
        foreach (QuickSquad.Class c in QuickSquad.Classes)
        {
            int want = QuickSquad.SameClass;
            int have = _qStage.Values.Count(s => s.Class == c.Tag && s.Side == _qs.Player)
                       + _qSpare.Count(s => s.Class == c.Tag && s.Side == _qs.Player);
            for (; have < want; have++)
                wanted.Add(c);
        }
        return wanted;
    }

    /// <summary>The views <see cref="QuickStageWanted"/> names, loaded and parked: the
    /// hangar's start pays for the loads once, a change of class or of ready squad
    /// later takes them. Each is drawn for <see cref="QWarmFrames"/> frames before it
    /// is parked - a view parked undrawn compiled its shaders the frame it was first
    /// shown, the hitch the spares were meant to take away (author 2026-11-03).
    /// <paramref name="said"/> hears each step: the loading screen's line and bar.</summary>
    private async Task QuickStageWarm(Action<string, bool>? said = null)
    {
        if (_qLayer is null)
            return;
        ulong from = Time.GetTicksMsec();
        List<QuickSquad.Class> wanted = QuickStageWanted();
        // each held on a slot of its own till all are made: parked at once, the
        // second of a class would take the first back out of the spares
        int made = 0;
        foreach (QuickSquad.Class c in wanted)
        {
            said?.Invoke(c.Name, false);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (_qLayer is null || QuickStage(-1 - made, c.Tag, _qs.Player, QhView) is not { } box)
                continue;
            said?.Invoke(c.Name, true);
            // drawn where the screens are, under the loading screen's black
            _qLayer.AddChild(box);
            _qLayer.MoveChild(box, 0);
            for (int f = 0; f < QWarmFrames; f++)
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            box.GetParent()?.RemoveChild(box);
            made++;
        }
        for (int k = 0; k < made; k++)
            QuickStageDrop(-1 - k);
        Log.Print($"tank3d: hangar: {made} spare views warmed in {Time.GetTicksMsec() - from} ms");
    }

    /// <summary>Frames a view is drawn for before it is parked: the first compiles its
    /// shaders, the next shows it is done.</summary>
    private const int QWarmFrames = 3;

    private void QuickSpareFree()
    {
        foreach (var spare in _qSpare)
            if (IsInstanceValid(spare.Box))
                spare.Box.Free();
        _qSpare.Clear();
    }

    private void QuickStageDrop(int at)
    {
        if (!_qStage.Remove(at, out var had) || !IsInstanceValid(had.Box))
            return;
        // the hangar's view parked for the next slot of its class, not thrown away
        if (_qHangar && _qLayer is not null && had.Box.HasMeta("q_px")
            && _qSpare.Count(s => s.Class == had.Class && s.Side == had.Side) < QuickSquad.SameClass)
        {
            had.Box.GetParent()?.RemoveChild(had.Box);
            QuickStageLive(had.Box, false);
            _qSpare.Add(had);
            return;
        }
        if (had.Box.IsInsideTree())
            had.Box.QueueFree();
        else
            had.Box.Free();
    }

    private Texture2D? QuickArtOf(string cls, Side side)
    {
        string model = Lineup.Pairs.FirstOrDefault(p => p.Class == cls).Model ?? "";
        return _rosterArt.TryGetValue((model, side), out Texture2D? t) ? t : null;
    }

    /// <summary>The page and its dialog drawn afresh - on every change, never per frame.</summary>
    private void QuickShow()
    {
        if (_qRoot is null || _qLayer is null)
            return;
        _qTips?.Hide(null);
        // the live models out of the old page before it goes, for the new one to take
        foreach (var stage in _qStage.Values)
            if (IsInstanceValid(stage.Box))
                stage.Box.GetParent()?.RemoveChild(stage.Box);
        foreach (Node n in _qRoot.GetChildren())
        {
            _qRoot.RemoveChild(n);
            n.QueueFree();
        }
        Ui.Large = _qHangar && _qPage != QuickPage.Lobby;
        _qRoot.AddChild(_qPage == QuickPage.Lobby ? QuickLobby() : _qHangar ? QuickHangar() : QuickBuild());
        _qOver?.QueueFree();
        _qOver = _qModal == QuickModal.None ? null : QuickDialog();
        if (_qOver is not null)
            _qLayer.AddChild(_qOver);
    }

    // --- the side as the board has it ----------------------------------------------

    /// <summary>Whether <paramref name="side"/>'s zone lies in the board's
    /// lower half - the south - on <paramref name="map"/>.</summary>
    private static bool QuickSouth(BoardMap? map, Side side)
    {
        if (map is null)
            return side == Side.Green;
        IReadOnlyList<Vector2I> zone = map.Arena.Zone(side == Side.Green ? Parking.Green : Parking.Brown);
        return zone.Count == 0 ? side == Side.Green : zone.Average(v => v.Y) >= map.Rows * 0.5f;
    }

    private string QuickSideSaid(Side side) => QuickSouth(QuickMap(_qs.Map), side) ? L.T("quick.south_cap") : L.T("quick.north_cap");

    private string QuickMode(string name) => QuickMap(name)?.Arena.Point is not null ? L.T("quick.point") : L.T("quick.annihilation");

    private string QuickSize(string name) => QuickMap(name) is { } m ? $"{m.Columns} × {m.Rows}" : "";

    private static string Pts(int n) => L.N("quick.pts", n);

    // --- the lobby -----------------------------------------------------------------

    private Control QuickLobby()
    {
        var sheet = new VBoxContainer();
        sheet.AddThemeConstantOverride("separation", 16);
        sheet.AddChild(QEyebrow(L.T("quick.quick_battle")));
        sheet.AddChild(Ui.Text(L.T("quick.vs_ai"), 30, Ui.Ink, bold: true));
        sheet.AddChild(QuickParams(thumb: 160));
        var foot = new HBoxContainer();
        foot.AddThemeConstantOverride("separation", 12);
        foot.AddChild(QBtn(L.T("quick.to_build"), () => { _qPage = QuickPage.Build; QuickShow(); }, primary: true));
        foot.AddChild(QHint(L.T("quick.lobby_hint")));
        sheet.AddChild(foot);
        var page = new VBoxContainer();
        page.AddChild(Ui.Box(sheet, Ui.Panel, 28));
        return page;
    }

    /// <summary>The battle's parameters as the prototype's boxes, two to a
    /// row: in the lobby and in the build's dialog.</summary>
    private Control QuickParams(float thumb)
    {
        QuickSetup q = _qs;
        var grid = new GridContainer { Columns = 2, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        grid.AddThemeConstantOverride("h_separation", 14);
        grid.AddThemeConstantOverride("v_separation", 14);

        // the map and its mode
        var card = new HBoxContainer();
        card.AddThemeConstantOverride("separation", 14);
        card.AddChild(QuickMinimap(q.Map, q.Player, thumb, labels: false));
        var about = new VBoxContainer();
        about.AddThemeConstantOverride("separation", 6);
        about.AddChild(Ui.Text(q.Map, 20, Ui.Ink, bold: true));
        about.AddChild(QHint(L.T("quick.map_about", QuickMode(q.Map), QuickSize(q.Map))));
        var maps = new HFlowContainer();
        maps.AddThemeConstantOverride("h_separation", 6);
        foreach (string name in QuickPool())
        {
            string one = name;
            maps.AddChild(QBtn(name, () => { q.Map = one; QuickShow(); }, pressed: name == q.Map, small: true));
        }
        about.AddChild(maps);
        card.AddChild(about);
        grid.AddChild(QParam(L.T("quick.mode_and_map"), card));

        // the sides
        var sides = new VBoxContainer();
        sides.AddThemeConstantOverride("separation", 10);
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        row.AddChild(QChip(L.T("quick.you_side", QuickSideSaid(q.Player))));
        row.AddChild(QChip(L.T("quick.ai_side", QuickSideSaid(Across(q.Player)))));
        row.AddChild(QBtn(L.T("quick.swap"), () => { q.Player = Across(q.Player); QuickArt(); QuickShow(); }));
        sides.AddChild(row);
        sides.AddChild(QHint(L.T("quick.sides_hint"), wrap: true));
        grid.AddChild(QParam(L.T("quick.sides"), sides));

        grid.AddChild(QParam(L.T("quick.your_budget"), QStepper(q.Budget, q.Budget == 0 ? L.T("quick.no_upgrades_battle") : L.T("quick.points_word"), v =>
        {
            q.Budget = v;
            QuickSquad.Trim(q.Squad, v);
        })));
        int diff = q.Budget - q.AiBudget;
        var ai = new VBoxContainer();
        ai.AddThemeConstantOverride("separation", 10);
        ai.AddChild(QStepper(q.AiBudget, q.AiBudget == 0 ? L.T("quick.no_upgrades") : L.T("quick.points_word"), v => q.AiBudget = v));
        ai.AddChild(diff != 0 ? Ui.Text(L.T("quick.handicap", diff > 0 ? "+" : "−", Math.Abs(diff)), 14, Ui.Extra, bold: true)
                              : QHint(L.T("quick.budgets_equal")));
        grid.AddChild(QParam(L.T("quick.ai_budget_difficulty"), ai));

        var doctrine = new VBoxContainer();
        doctrine.AddThemeConstantOverride("separation", 10);
        var picks = new HFlowContainer();
        picks.AddThemeConstantOverride("h_separation", 6);
        picks.AddThemeConstantOverride("v_separation", 6);
        picks.AddChild(QBtn(L.T("quick.random_cap"), () => { q.Doctrine = null; QuickShow(); }, pressed: q.Doctrine is null, small: true));
        for (int i = 0; i < QuickSquad.Doctrines.Length; i++)
        {
            int at = i;
            picks.AddChild(QBtn(QuickSquad.Doctrines[i].Name, () => { q.Doctrine = at; QuickShow(); }, pressed: q.Doctrine == i, small: true));
        }
        doctrine.AddChild(picks);
        doctrine.AddChild(QHint(q.Doctrine is int d
            ? $"{QuickSquad.Doctrines[d].Play}: {string.Join(", ", QuickSquad.Doctrines[d].Pool.Select(c => QuickSquad.ClassOf(c).Short))}."
            : L.T("quick.doctrine_hidden"), wrap: true));
        grid.AddChild(QParam(L.T("quick.ai_doctrine"), doctrine));
        return grid;
    }

    private Control QParam(string title, Control inner)
    {
        var col = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        col.AddThemeConstantOverride("separation", 10);
        col.AddChild(QEyebrow(title));
        col.AddChild(inner);
        PanelContainer box = Ui.Box(col, Ui.Inset, 14);
        // the grid's two columns share the width, as the prototype's do
        box.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        return box;
    }

    private Control QStepper(int value, string label, Action<int> set)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 10);
        var less = QBtn("−", () => { set(Math.Max(0, value - 1)); QuickShow(); });
        less.CustomMinimumSize = new Vector2(36, 34);
        less.Disabled = value <= 0;
        row.AddChild(less);
        var said = Ui.Text(value.ToString(), 22, Ui.Ink, bold: true);
        said.CustomMinimumSize = new Vector2(30, 0);
        said.HorizontalAlignment = HorizontalAlignment.Center;
        row.AddChild(said);
        var more = QBtn("+", () => { set(Math.Min(QuickSquad.BudgetMax, value + 1)); QuickShow(); });
        more.CustomMinimumSize = new Vector2(36, 34);
        more.Disabled = value >= QuickSquad.BudgetMax;
        row.AddChild(more);
        var hint = QHint(label);
        hint.VerticalAlignment = VerticalAlignment.Center;
        row.AddChild(hint);
        return row;
    }

    // --- the build -----------------------------------------------------------------
    //
    // The colours say one thing each: olive is bought (a node, a spent pip, the
    // chosen class), amber is what the next click buys or a warning, red is what
    // cannot be had. A cost is said once - a level costs its number - and then
    // only where it is paid: the tip, the card's badge, the machine's line.

    private Control QuickBuild()
    {
        QuickSetup q = _qs;
        int used = QuickSquad.Spent(q.Squad), left = q.Budget - used;
        var page = new VBoxContainer();
        page.AddThemeConstantOverride("separation", 14);

        // the header: who, the facts, the budget
        var top = new HBoxContainer();
        top.AddThemeConstantOverride("separation", 24);
        var who = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        who.AddThemeConstantOverride("separation", 6);
        who.AddChild(QEyebrow(L.T("quick.build_eyebrow")));
        who.AddChild(Ui.Text(L.T("quick.your_squad"), 28, Ui.Ink, bold: true));
        var facts = new HFlowContainer();
        facts.AddThemeConstantOverride("h_separation", 8);
        foreach ((string label, string value) in new[]
                 {
                     (L.T("quick.map"), q.Map), (L.T("quick.mode"), QuickMode(q.Map)), (L.T("quick.your_side"), QuickSideSaid(q.Player)),
                     (L.T("quick.ai_budget"), q.AiBudget.ToString()),
                     (L.T("quick.ai_doctrine"), q.Doctrine is int d ? QuickSquad.Doctrines[d].Name : L.T("quick.random")),
                 })
            facts.AddChild(QChip($"{label}: {value}", () => { _qModal = QuickModal.Params; QuickShow(); }));
        who.AddChild(facts);
        top.AddChild(who);
        var budget = new VBoxContainer { SizeFlagsVertical = Control.SizeFlags.ShrinkEnd };
        budget.AddThemeConstantOverride("separation", 6);
        var eb = QEyebrow(L.T("quick.upgrade_points"));
        eb.HorizontalAlignment = HorizontalAlignment.Right;
        budget.AddChild(eb);
        if (q.Budget > 0)
            budget.AddChild(QuickPips(used, q.Budget, 24));
        var line = QHint(q.Budget > 0 ? L.T("quick.budget_line", used, q.Budget, left) : L.T("quick.budget_zero"));
        line.HorizontalAlignment = HorizontalAlignment.Right;
        budget.AddChild(line);
        top.AddChild(budget);
        page.AddChild(top);
        page.AddChild(QuickRules());

        // the battlefield and the squad down the left, the three machines
        var work = new HBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        work.AddThemeConstantOverride("separation", 14);
        work.AddChild(QuickAside());
        for (int i = 0; i < q.Squad.Length; i++)
            work.AddChild(QuickSlot(i, left));
        page.AddChild(work);

        // the bar: the ready squads; the budget by the button that spends it;
        // the clearing apart from the ways on
        var bar = new HBoxContainer();
        bar.AddThemeConstantOverride("separation", 8);
        int start = QuickSquad.Size * QuickSquad.CompanyLevel;
        var qs = QEyebrow(L.T("quick.presets_bar", Pts(start)));
        qs.VerticalAlignment = VerticalAlignment.Center;
        bar.AddChild(qs);
        foreach (QuickSquad.Company c in QuickSquad.Companies)
        {
            QuickSquad.Company company = c;
            QuickPad b = QBtn(c.Name, () => QuickAsk(q.Squad.Any(s => !s.Empty), L.T("quick.replace_ask"),
                L.T("quick.replace_text", company.Name, QuickSquad.CompanyLevel),
                L.T("quick.replace"), () => q.Squad = QuickSquad.OfCompany(company)));
            b.Disabled = q.Budget < start;
            QTip(b, () => UiTip.Box(L.T("quick.preset_tip", Pts(start)), company.Name,
                string.Join("\n", company.Slots.Select(s => L.T("quick.preset_slot", QuickSquad.ClassOf(s.Class).Short, QuickSquad.ClassOf(s.Class).BranchOf(s.Branch)!.Name, QuickSquad.CompanyLevel))),
                null, L.T("quick.preset_note", $"{char.ToUpper(company.Play[0])}{company.Play[1..]}",
                      q.Budget > start ? L.T("quick.preset_left", Pts(q.Budget - start)) : L.T("quick.preset_all"))));
            bar.AddChild(b);
        }
        bar.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        int missing = q.Squad.Count(s => s.Empty);
        // the points are the header's alone; the bar says only what holds the button
        if (missing > 0)
        {
            Label hint = Ui.Text(L.N("quick.no_class", missing), 14, Ui.Danger, bold: true);
            hint.VerticalAlignment = VerticalAlignment.Center;
            bar.AddChild(hint);
        }
        QuickPad clear = QBtn(L.T("quick.clear_squad"), () => QuickAsk(true, L.T("quick.clear_ask"),
            L.T("quick.clear_text"), L.T("quick.clear"), () => q.Squad = QuickSquad.Empty()));
        clear.Ink = Ui.Danger;
        clear.Rim = clear.Fill = new Color(0, 0, 0, 0);
        clear.HoverRim = new Color(Ui.Danger, 0.6f);
        clear.Disabled = q.Squad.All(s => s.Empty);
        bar.AddChild(clear);
        bar.AddChild(new ColorRect { Color = Ui.Line, CustomMinimumSize = new Vector2(1, 0) });
        bar.AddChild(QBtn(L.T("quick.to_params"), () => { _qPage = QuickPage.Lobby; QuickShow(); }));
        QuickPad go = QBtn(L.T("quick.to_deploy"), QuickTryGo, primary: true);
        go.Disabled = missing > 0;
        bar.AddChild(go);
        page.AddChild(Ui.Box(bar, Ui.Panel, 12));
        return page;
    }

    /// <summary>The budget as hexes: the spent ones olive, the free ones in the line's colour.</summary>
    private static Control QuickPips(int used, int budget, float size)
    {
        var pips = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        pips.AddThemeConstantOverride("separation", size > 16 ? 3 : 2);
        for (int k = 0; k < budget; k++)
        {
            Color c = k < used ? Ui.Accent : Ui.Line;
            pips.AddChild(new QuickPad
            {
                Form = QuickPad.Shape.Hex, Rim = c, Fill = c, CustomMinimumSize = new Vector2(size, size * 0.875f),
                MouseFilter = Control.MouseFilterEnum.Ignore,
            });
        }
        return pips;
    }

    /// <summary>«К расстановке»: straight on with the budget spent, else asked first.</summary>
    private void QuickTryGo()
    {
        QuickSetup q = _qs;
        int left = q.Budget - QuickSquad.Spent(q.Squad);
        QuickAsk(left > 0, L.T("quick.unspent_ask"),
            L.T("quick.unspent_text", Pts(left), q.Budget),
            L.T("quick.to_deploy_btn"), () => _qGo = true, no: L.T("quick.spend"));
    }

    /// <summary>The battlefield, what the map asks of a squad, and what the squad comes to.</summary>
    private Control QuickAside()
    {
        QuickSetup q = _qs;
        var aside = new VBoxContainer { CustomMinimumSize = new Vector2(236, 0) };
        aside.AddThemeConstantOverride("separation", 8);
        var head = new HBoxContainer();
        var h2 = Ui.Text(L.T("quick.battlefield"), 17, Ui.Ink, bold: true);
        h2.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        head.AddChild(h2);
        head.AddChild(QBtn(L.T("quick.expand"), () => { _qModal = QuickModal.Map; QuickShow(); }, small: true));
        aside.AddChild(head);
        aside.AddChild(QuickMinimap(q.Map, q.Player, 190, labels: true));
        aside.AddChild(QuickLegend(3));
        if (QuickMapSaid(q.Map) is { Length: > 0 } said)
            aside.AddChild(Ui.Text(said, 12, Ui.Ink, wrap: true));

        aside.AddChild(new ColorRect { Color = Ui.Line, CustomMinimumSize = new Vector2(0, 1) });
        aside.AddChild(Ui.Text(L.T("quick.squad_together"), 15, Ui.Ink, bold: true));
        aside.AddChild(QuickSquadTable());
        foreach (string fact in QuickSquadSaid())
            aside.AddChild(Ui.Text(fact, 12, Ui.Ink, wrap: true));
        // a random doctrine is said in the header's chip; a chosen one, what it does
        if (q.Doctrine is int d)
            aside.AddChild(QHint(L.T("quick.ai_doctrine_said", QuickSquad.Doctrines[d].Name, QuickSquad.Doctrines[d].Play.ToLowerInvariant(),
                                 string.Join(", ", QuickSquad.Doctrines[d].Pool.Select(c => QuickSquad.ClassOf(c).Short))), wrap: true));
        return Ui.Box(aside, Ui.Panel, 12);
    }

    /// <summary>The build's rules in one line over the cards: what a level costs, what the dashes mean.</summary>
    private static Control QuickRules()
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        row.AddChild(QHint(L.T("quick.rules_cost")));
        row.AddChild(new QuickPad
        {
            Form = QuickPad.Shape.Hex, Dashed = true, Rim = Ui.Muted, Fill = new Color(0, 0, 0, 0), RimWidth = 1,
            CustomMinimumSize = new Vector2(18, 16), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        });
        row.AddChild(QHint(L.T("quick.rules_dash")));
        return row;
    }

    /// <summary>What the map's ground asks of a squad, in a line or two; empty on plain ground.</summary>
    private string QuickMapSaid(string name)
    {
        if (QuickMap(name) is not { } map)
            return "";
        int water = 0, wood = 0, walls = 0;
        for (int r = 0; r < map.Rows; r++)
            for (int c = 0; c < map.Columns; c++)
            {
                var cell = new Vector2I(c, r);
                if (map.Plot is { } plot && !plot.Contains(cell))
                    continue;
                int i = map.At(cell);
                if (map.Water[i])
                    water++;
                else if (map.Walls[i])
                    walls++;
                else if (map.Kinds[i] == TerrainSet.Forest)
                    wood++;
            }
        var said = new List<string>();
        if (water > 0)
            said.Add(L.T("quick.map_water", L.N("quick.hexes", water), L.T("squad.branch.raid.name")));
        if (walls > 0)
            said.Add(L.T("quick.map_walls", L.N("quick.hexes", walls)));
        if (wood > 0)
            said.Add(L.T("quick.map_wood", L.N("quick.hexes", wood)));
        return string.Join("\n", said);
    }

    /// <summary>The squad side by side: steps, might, the front plate, the turret.</summary>
    private Control QuickSquadTable()
    {
        var grid = new GridContainer { Columns = 5, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        grid.AddThemeConstantOverride("h_separation", 10);
        grid.AddThemeConstantOverride("v_separation", 2);
        if (_qs.Squad.All(s => s.Empty))
            return QHint(L.T("quick.squad_empty"), wrap: true);
        foreach (string h in new[] { "", L.T("quick.col_steps"), L.T("quick.col_power"), L.T("quick.col_front"), L.T("quick.turret") })
            grid.AddChild(Ui.Text(h, 11, Ui.Muted, bold: true));
        foreach (QuickSquad.Slot s in _qs.Squad)
        {
            if (s.Empty)
            {
                grid.AddChild(Ui.Text("—", 12, Ui.Muted));
                for (int k = 0; k < 4; k++)
                    grid.AddChild(Ui.Text("", 12, Ui.Muted));
                continue;
            }
            MovementProfile p = QuickSquad.ClassOf(s.Class!).Profile;
            grid.AddChild(Ui.Text(QuickSquad.ClassOf(s.Class!).Short, 12, Ui.Ink, bold: true));
            grid.AddChild(Ui.Text(p.Steps.ToString(), 12, Ui.Ink));
            grid.AddChild(Ui.Text(QRoman(p.Might), 12, Ui.Ink));
            grid.AddChild(Ui.Text(QRoman(Gunnery.ArmourAt(p, Gunnery.Plate.Front)), 12, Ui.Ink));
            grid.AddChild(Ui.Text(p.Turreted ? L.T("quick.yes") : L.T("quick.no"), 12, Ui.Ink));
        }
        return grid;
    }

    /// <summary>What the squad can and cannot: a heavy's front, its actions.</summary>
    private IEnumerable<string> QuickSquadSaid()
    {
        QuickSquad.Slot[] squad = _qs.Squad;
        if (squad.All(s => s.Empty))
            yield break;
        string[] front = squad.Where(s => s.Class is "TDP" or "HMP").Select(s => QuickSquad.ClassOf(s.Class!).Short).Distinct().ToArray();
        yield return front.Length > 0 ? L.T("quick.ht_front_yes", string.Join(", ", front)) : L.T("quick.ht_front_no");
        string[] acts = squad.SelectMany(QuickSquad.UpgradesOf).Where(u => u.Ability)
                             .Select(u => u.Name).Distinct().ToArray();
        yield return acts.Length > 0 ? L.T("quick.actions", string.Join(", ", acts)) : L.T("quick.no_actions");
    }

    /// <summary>A machine's card: the class and its duty, the model with the
    /// arrows that turn the class over, the tree. An empty card lists the
    /// classes to pick from. The points are said once, in the header.</summary>
    private Control QuickSlot(int at, int left)
    {
        QuickSetup q = _qs;
        QuickSquad.Slot s = q.Squad[at];
        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 10);
        var head = new HBoxContainer { CustomMinimumSize = new Vector2(0, 26) };
        head.AddThemeConstantOverride("separation", 8);
        if (s.Empty)
            head.AddChild(QEyebrow(L.T("quick.slot_pick", at + 1)));
        else
        {
            QuickSquad.Class c = QuickSquad.ClassOf(s.Class!);
            head.AddChild(Ui.Text(c.Name, 16, Ui.Ink, bold: true));
            var duty = Ui.Text(c.Duty, 13, Ui.Muted);
            duty.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            duty.VerticalAlignment = VerticalAlignment.Center;
            duty.ClipText = true;
            head.AddChild(duty);
        }
        col.AddChild(head);

        if (s.Empty)
        {
            // the classes as rows, each with its duty; a class there are two of is shut
            QuickStageDrop(at);
            var list = new VBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
            list.AddThemeConstantOverride("separation", 6);
            foreach (QuickSquad.Class c in QuickSquad.Classes)
            {
                bool full = QuickSquad.ClassWhy(q.Squad, at, c.Tag).Length > 0;
                QuickSquad.Class one = c;
                QuickPad row = QBtn($"{c.Short}   {c.Name} — {(full ? L.T("quick.two_already") : c.Duty)}", () =>
                {
                    q.Squad[at] = new QuickSquad.Slot(one.Tag, null, 0);
                    QuickShow();
                });
                row.Align = HorizontalAlignment.Left;
                row.CustomMinimumSize = new Vector2(0, 40);
                row.Disabled = full;
                QTip(row, () => QuickClassTip(one, full));
                list.AddChild(row);
            }
            col.AddChild(list);
        }
        else
        {
            // the model between the arrows; the stage takes what height the card has spare
            var stageRow = new HBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
            stageRow.AddThemeConstantOverride("separation", 0);
            stageRow.AddChild(QuickTurn(at, -1));
            var model = new QuickPad
            {
                Rim = Ui.Line, Fill = Ui.Inset, Ink = Ui.Muted, CustomMinimumSize = new Vector2(0, QStageHigh),
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, Glow = Ui.AccentFill.Lightened(0.15f),
            };
            if (QuickStage(at, s.Class!, q.Player) is { } stage)
                model.AddChild(stage);
            else
            {
                model.Picture = QuickArtOf(s.Class!, q.Player);
                model.PictureSize = new Vector2(150, 110);
            }
            QuickSquad.Class c = QuickSquad.ClassOf(s.Class!);
            QTip(model, () => QuickClassTip(c, false));
            stageRow.AddChild(model);
            stageRow.AddChild(QuickTurn(at, +1));
            col.AddChild(stageRow);
            col.AddChild(QuickClassDots(at));

            var tree = new HBoxContainer();
            tree.AddThemeConstantOverride("separation", 8);
            foreach (QuickSquad.Branch b in c.Branches)
                tree.AddChild(QuickBranch(at, s, b, left));
            QuickTreeCenter(tree);
            // what the machine carries is the tree itself: nothing said again under it
            col.AddChild(tree);
        }
        var box = Ui.Box(col, Ui.Panel, 12);
        box.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        box.SizeFlagsStretchRatio = 1.0f;
        box.SetMeta(UiTip.Beside, true);
        return box;
    }

    /// <summary>The class <paramref name="dir"/> steps from slot <paramref name="at"/>'s,
    /// round the five, past those there are two of already; null when none is free.</summary>
    private QuickSquad.Class? QuickClassStep(int at, int dir)
    {
        QuickSquad.Class[] all = QuickSquad.Classes;
        int from = Array.FindIndex(all, c => c.Tag == _qs.Squad[at].Class);
        for (int k = 1; k < all.Length; k++)
        {
            QuickSquad.Class c = all[((from + dir * k) % all.Length + all.Length) % all.Length];
            if (QuickSquad.ClassWhy(_qs.Squad, at, c.Tag).Length == 0)
                return c;
        }
        return null;
    }

    /// <summary>An arrow beside the model: the next or the last class that may
    /// be had; the level goes with the old class, its tip says so.</summary>
    private QuickPad QuickTurn(int at, int dir)
    {
        QuickSquad.Class? to = QuickClassStep(at, dir);
        var arrow = new QuickPad
        {
            Text = dir < 0 ? "‹" : "›", FontSize = 30, Bold = true, Rim = Ui.Line, Fill = Ui.Inset, Ink = Ui.Ink,
            HoverFill = Ui.AccentFill, HoverRim = Ui.Accent, Disabled = to is null, CustomMinimumSize = new Vector2(34, 0),
        };
        arrow.Clicked += () =>
        {
            if (QuickClassStep(at, dir) is { } c)
                _qs.Squad[at] = new QuickSquad.Slot(c.Tag, null, 0);
            QuickShow();
        };
        int level = _qs.Squad[at].Level;
        if (to is not null)
            QTip(arrow, () => QuickClassTip(to, false,
                level > 0 ? L.T("quick.class_change", Pts(level)) : null));
        return arrow;
    }

    /// <summary>Where the class is among the five: a dot each, the slot's olive,
    /// a class there are two of already dimmed - the arrows step past it.</summary>
    private Control QuickClassDots(int at)
    {
        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        row.AddThemeConstantOverride("separation", 10);
        foreach (QuickSquad.Class c in QuickSquad.Classes)
        {
            bool mine = _qs.Squad[at].Class == c.Tag;
            bool full = !mine && QuickSquad.ClassWhy(_qs.Squad, at, c.Tag).Length > 0;
            row.AddChild(Ui.Text(c.Short, 12, mine ? Ui.Accent : full ? new Color(Ui.Muted, 0.45f) : Ui.Muted, bold: mine));
        }
        return row;
    }

    /// <summary>A branch: its name, its three levels as hex nodes linked down,
    /// each with what kind of thing it is. Both branches are drawn alike and
    /// both can be clicked; the chosen one is told by its rim and its fill.</summary>
    private Control QuickBranch(int at, QuickSquad.Slot s, QuickSquad.Branch b, int left)
    {
        QuickSetup q = _qs;
        bool chosen = s.Branch == b.Id && s.Level > 0, other = s.Level > 0 && !chosen;
        int mine = chosen ? s.Level : 0;
        var col = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, Alignment = BoxContainer.AlignmentMode.Begin };
        col.AddThemeConstantOverride("separation", 0);
        // the branch's name on a band across the column's top, a heading's size (author
        // 2026-11-03: in the nodes' ink and a step over their names it read as one more
        // of them): olive with dark ink on the chosen one, as a bought node; dark with a
        // line under it on the other, lit under the pointer where a click moves the level
        var head = new QuickPad
        {
            Text = b.Name, FontSize = Ui.Size(18), Bold = true, RimWidth = 0, Fill = new Color(0, 0, 0, 0),
            Ink = chosen ? Ui.OnAccent : Ui.Ink, CustomMinimumSize = new Vector2(0, Ui.Size(14) * 2),
        };
        float bandHigh = head.CustomMinimumSize.Y;
        bool lit = false;
        head.SetMeta("q_branch", b.Id);
        head.Clicked += () =>
        {
            // the same level on the other branch costs nothing
            if (q.Squad[at].Level > 0)
                q.Squad[at] = q.Squad[at] with { Branch = b.Id };
            QuickShow();
        };
        string ups = string.Join("\n", b.Levels.Select((u, i) => u.Flag is null
            ? L.T("quick.branch_tip_missing", i + 1, u.Name) : $"{i + 1}. {u.Name}"));
        QTip(head, () => UiTip.Box(L.T("quick.branch_tip", b.Tag), b.Name, ups, null,
            chosen ? L.T("quick.branch_chosen") : other ? L.T("quick.branch_move", s.Level) : L.T("quick.branch_first")));
        col.AddChild(head);
        // the box's own top margin, given up to the band, under it instead
        var top = new Control { CustomMinimumSize = new Vector2(0, 6), MouseFilter = Control.MouseFilterEnum.Ignore };
        col.AddChild(top);
        float footHigh = QFootHigh;
        var helds = new Godot.Collections.Array();
        Label lastName = null!;
        for (int l = 1; l <= b.Levels.Length; l++)
        {
            QuickSquad.Upgrade u = b.Levels[l - 1];
            bool open = l <= mine;
            // this machine to (b, l) costs l less the level it holds
            int need = l - s.Level;
            bool shortOf = !open && need > left;
            // amber is buying and nothing else: the level after the one held, on either
            // branch (on the other it moves the machine there too); the other branch's
            // node at the level held is a free move, ringed olive (author 2026-11-03)
            bool next = !open && !shortOf && need > 0 && l == s.Level + 1;
            bool swap = other && l == s.Level;
            int level = l;
            // the upgrade's icon in the node, its number only where there is none; full
            // colour where a click does something now, grey (not faded: a dark icon faded
            // on the dark panel was lost) where it does not yet
            bool bright = open || next || swap;
            Texture2D? icon = QIcon(u.Icon, 32, grey: !bright);
            var hex = new QuickPad
            {
                Form = QuickPad.Shape.Hex, Text = icon is null ? l.ToString() : "", FontSize = 15, Bold = true, RimWidth = next || swap ? 2 : 1,
                Rim = open || swap ? Ui.Accent : next ? Ui.Extra : shortOf ? new Color(Ui.Danger, 0.8f) : Ui.Line.Lightened(0.15f),
                Fill = open ? Ui.Accent : Ui.Panel, Ink = open ? Ui.OnAccent : Ui.Ink, Dashed = u.Flag is null && !open,
                HoverFill = open ? null : Ui.AccentFill, Disabled = shortOf, CustomMinimumSize = new Vector2(46, 40),
                SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter,
                Picture = icon, PictureSize = new Vector2(30, 30),
                TextureFilter = QIconFilter,
                // the price on the node, what this click costs: none on what is held or free
                Badge = open || need <= 0 ? "" : need.ToString(), BadgeSize = Ui.Size(12),
                BadgeRim = next ? Ui.Extra : shortOf ? new Color(Ui.Danger, 0.8f) : Ui.Line.Lightened(0.15f),
                BadgeFill = next ? Ui.Extra : Ui.Inset, BadgeInk = next ? Ui.OnAccent : Ui.Ink,
            };
            hex.SetMeta("q_level", l);
            // the left button buys or moves, never takes back: an open node holds still
            if (!open)
                hex.Clicked += () =>
                {
                    QuickSquad.Slot now = q.Squad[at];
                    if (level - now.Level <= q.Budget - QuickSquad.Spent(q.Squad))
                        q.Squad[at] = now with { Branch = b.Id, Level = level };
                    QuickShow();
                };
            // the right button (or Delete) drops the top open level, and only it
            else if (l == mine)
                hex.Dropped += () =>
                {
                    q.Squad[at] = q.Squad[at] with { Level = level - 1 };
                    QuickShow();
                };
            string state = open ? (l == mine ? L.T("quick.lvl_open_drop") : L.T("quick.lvl_open"))
                : shortOf ? L.T("quick.lvl_short", Pts(need - left))
                : other ? L.T("quick.lvl_switch", b.Name, l)
                : need > 0 ? L.T("quick.lvl_buy", l, Pts(need)) : L.T("quick.lvl_back", l);
            QTip(hex, () => UiTip.Box(L.T("quick.level_tip", b.Name, level), u.Name, u.Text, (u.Kind, Pts(level)),
                                    state + (u.Flag is null ? L.T("quick.lvl_dash_note") : "")));
            col.AddChild(hex);
            // under the node, close to it: its name, then the link down to the next node
            // filling what is left - the name reads as the node's, not the next's (author
            // 2026-11-03: equal gaps above and below left it floating between two). Its kind
            // is the tip's, not said here too (author 2026-11-03). Fixed high and clipped,
            // whatever the name wraps to: every class's tree one height; the last has no link
            bool last = l == b.Levels.Length;
            var held = new Control { CustomMinimumSize = new Vector2(0, last ? footHigh - QLinkLeast : footHigh), ClipContents = true, MouseFilter = Control.MouseFilterEnum.Ignore };
            var foot = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
            foot.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            held.AddChild(foot);
            foot.AddThemeConstantOverride("separation", 0);
            foot.AddChild(new Control { CustomMinimumSize = new Vector2(0, 2), MouseFilter = Control.MouseFilterEnum.Ignore });
            // two lines at most: a longer name must not make this tree taller than the
            // next card's (the foot is fixed high, the link takes what the name leaves)
            var nm = Ui.Text(u.Name, 13, Ui.Ink, bold: open, wrap: true);
            nm.HorizontalAlignment = HorizontalAlignment.Center;
            nm.MaxLinesVisible = 2;
            nm.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            foot.AddChild(nm);
            if (!last)
            {
                Control link = QLink(l < mine ? Ui.Accent : Ui.Line);
                link.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
                foot.AddChild(link);
            }
            col.AddChild(held);
            helds.Add(held);
            lastName = nm;
        }
        var bottom = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        col.AddChild(bottom);
        var box = Ui.Box(col, chosen ? Ui.AccentFill : Ui.Inset, 6, chosen ? Ui.Accent : Ui.Line,
                       () => (chosen ? Ui.Accent : lit ? Ui.AccentFill : Ui.Panel, chosen ? null : Ui.Line, bandHigh));
        if (other)
            head.Over += (_, on) => { lit = on; box.QueueRedraw(); };
        box.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        box.SetMeta(QCenterMeta, new Godot.Collections.Array { top, helds, lastName, bottom });
        return box;
    }

    private static readonly StringName QCenterMeta = "q_center";

    /// <summary>The feet of the branches of <paramref name="tree"/> sized off their own
    /// labels, and the branches centred top to bottom in their boxes, all by one amount
    /// (author 2026-11-03: a last name on one line left a line's gap under it and none over
    /// the first node). The amount is the line the longest last name does not use, half
    /// moved over the first node; one for both, so their rows stay level, and the tree one
    /// height whatever the names. Done whenever the tree is sorted: by then the boxes have
    /// their widths and the labels their theme - a label measured off the tree took another
    /// font, 23 px a line for 26, and a two-line name came out cut at the foot.</summary>
    private static void QuickTreeCenter(Container tree)
    {
        tree.SortChildren += () =>
        {
            int lines = 1;
            float line = 0, two = 0;
            foreach (Node box in tree.GetChildren())
                if (box.HasMeta(QCenterMeta) && box is Control c)
                {
                    var name = (Label)box.GetMeta(QCenterMeta).AsGodotArray()[2];
                    Font font = name.GetThemeFont("font");
                    int size = name.GetThemeFontSize("font_size");
                    float h = font.GetHeight(size), gap = name.GetThemeConstant("line_spacing");
                    line = Mathf.Max(line, h + gap);
                    two = Mathf.Max(two, 2 * h + gap);
                    // the lines counted here, off the box's width: the label's own count
                    // is its last layout's, from before it knew its width
                    float wide = font.GetMultilineStringSize(name.Text, HorizontalAlignment.Center, c.Size.X - 12, size).Y;
                    lines = Math.Max(lines, Mathf.RoundToInt(wide / h));
                }
            float spare = (2 - Math.Min(lines, 2)) * line;
            foreach (Node box in tree.GetChildren())
                if (box.HasMeta(QCenterMeta))
                {
                    Godot.Collections.Array c = box.GetMeta(QCenterMeta).AsGodotArray();
                    Godot.Collections.Array helds = c[1].AsGodotArray();
                    for (int i = 0; i < helds.Count; i++)
                        ((Control)helds[i]).CustomMinimumSize = new Vector2(0, Mathf.Ceil(two + 2 + (i < helds.Count - 1 ? QLinkLeast : -spare)));
                    ((Control)c[0]).CustomMinimumSize = new Vector2(0, 6 + Mathf.Floor(spare * 0.5f));
                    ((Control)c[3]).CustomMinimumSize = new Vector2(0, Mathf.Ceil(spare * 0.5f));
                }
        };
    }

    /// <summary>An icon of <c>Icons/</c> - an upgrade's, a class's - rasterised from its
    /// vector for <paramref name="px"/> on screen, read once a size (<see cref="ActImage"/>);
    /// null with no file. Twice the size it is drawn at: drawn down it stays sharp, and
    /// on a window larger than the layout too (author 2026-11-03: a 64 px raster drawn
    /// at 112 was blurred).</summary>
    private static Texture2D? QIcon(string name, int px, bool grey = false)
    {
        if (name.Length == 0)
            return null;
        string key = grey ? $"{name}@{px}~grey" : $"{name}@{px}";
        if (!QIcons.TryGetValue(key, out Texture2D? tex))
        {
            Image? image = ActImage(name, px * 2);
            if (image is not null && grey)
                QGrey(image);
            image?.GenerateMipmaps();
            QIcons[key] = tex = image is null ? null : ImageTexture.CreateFromImage(image);
        }
        return tex;
    }

    /// <summary>How an icon is drawn down: smooth, from its mipmaps - the project's
    /// default filter is nearest (the board's pixels), which steps an icon's edges.</summary>
    private const CanvasItem.TextureFilterEnum QIconFilter = CanvasItem.TextureFilterEnum.LinearWithMipmaps;

    private static readonly Dictionary<string, Texture2D?> QIcons = new();

    /// <summary>An icon's colours to grey by their light, lifted into the upper half: a
    /// node not yet in reach says so without its colour, yet stays as legible as a lit one
    /// on the dark panel (a dark red one faded to 0.55 was lost, author 2026-11-03).</summary>
    private static void QGrey(Image image)
    {
        image.Convert(Image.Format.Rgba8);
        for (int y = 0; y < image.GetHeight(); y++)
            for (int x = 0; x < image.GetWidth(); x++)
            {
                Color c = image.GetPixel(x, y);
                float g = 0.42f + 0.5f * c.Luminance;
                image.SetPixel(x, y, new Color(g, g, g, c.A));
            }
    }

    /// <summary>An upgrade's name under its node and the link down from it, px high, till
    /// the tree is first sorted (<see cref="QuickTreeCenter"/> sizes it then, off the label):
    /// the name's two lines, the gap over them and the link's least.</summary>
    private static float QFootHigh => Mathf.Ceil(Ui.BoldFont.GetHeight(Ui.Size(13)) * 2 + 3 + 2 + QLinkLeast);

    /// <summary>The link between two nodes at its shortest, px: under a name on two lines.</summary>
    private const float QLinkLeast = 12;

    /// <summary>The line from one node down to the next, through the middle of the
    /// column: olive where both ends are bought.</summary>
    private static Control QLink(Color ink)
    {
        var link = new Control { CustomMinimumSize = new Vector2(0, QLinkLeast), MouseFilter = Control.MouseFilterEnum.Ignore };
        link.Draw += () =>
        {
            float w = Ui.RimLayout(link, 2), x = Mathf.Round(link.Size.X * 0.5f - w * 0.5f);
            link.DrawRect(new Rect2(x, 1, w, link.Size.Y - 1), ink);
        };
        return link;
    }

    private static string QRoman(int n) => n switch { 0 => "0", 1 => "I", 2 => "II", 3 => "III", 4 => "IV", _ => "V" };

    private Control QuickClassTip(QuickSquad.Class c, bool full, string? note = null) =>
        UiTip.Box(L.T("quick.class_tip", c.Short, c.Duty), c.Name, c.Role, null, full ? L.T("quick.class_full") : note,
                QuickClassRows(c), L.T("quick.trait", c.Trait));

    /// <summary>A class's numbers as label and value: the tip's table, the hangar's sheet.</summary>
    private static (string, string)[] QuickClassRows(QuickSquad.Class c)
    {
        MovementProfile p = c.Profile;
        string armour = string.Join(" / ", new[] { Gunnery.Plate.Front, Gunnery.Plate.Side, Gunnery.Plate.Rear, Gunnery.Plate.Roof }
                                           .Select(pl => QRoman(Gunnery.ArmourAt(p, pl))));
        return new[]
        {
            (L.T("quick.speed"), L.N("quick.steps", p.Steps)), (L.T("quick.firepower"), QRoman(p.Might)),
            (L.T("quick.armour"), armour), (L.T("quick.mass"), QRoman(p.Mass)), (L.T("quick.turret"), p.Turreted ? L.T("quick.yes") : L.T("quick.no")),
        };
    }

    // --- the dialogs ---------------------------------------------------------------

    private Control QuickDialog()
    {
        var over = new Control { MouseFilter = Control.MouseFilterEnum.Stop };
        over.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        var dim = new ColorRect { Color = new Color(10 / 255f, 14 / 255f, 10 / 255f, 0.72f) };
        dim.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        dim.GuiInput += e =>
        {
            if (e is InputEventMouseButton { Pressed: true })
                QuickClose();
        };
        over.AddChild(dim);
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 12);
        if (_qModal == QuickModal.Ask && _qAsk is var (askHead, askText, yes, no, act))
        {
            box.AddChild(Ui.Text(askHead, 18, Ui.Ink, bold: true));
            box.AddChild(Ui.Text(askText, 14, Ui.Ink, wrap: true));
            var hands = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
            hands.AddThemeConstantOverride("separation", 8);
            hands.AddChild(QBtn(no, QuickClose));
            hands.AddChild(QBtn(yes, () =>
            {
                _qModal = QuickModal.None;
                _qAsk = null;
                act();
                QuickShow();
            }, primary: true));
            box.AddChild(hands);
            var ask = Ui.Box(box, Ui.Panel, 18);
            ask.CustomMinimumSize = new Vector2(460, 0);
            var middle = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
            middle.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            middle.AddChild(ask);
            over.AddChild(middle);
            return over;
        }
        var head = new HBoxContainer();
        bool map = _qModal == QuickModal.Map;
        var h2 = Ui.Text(map ? $"{_qs.Map} · {QuickMode(_qs.Map)}" : L.T("quick.params_title"), 18, Ui.Ink, bold: true);
        h2.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        head.AddChild(h2);
        head.AddChild(QBtn(map ? L.T("quick.close") : L.T("quick.done"), QuickClose, small: true));
        box.AddChild(head);
        if (map)
        {
            box.AddChild(QuickMinimap(_qs.Map, _qs.Player, 560, labels: true));
            box.AddChild(QuickLegend());
        }
        else
        {
            box.AddChild(QuickParams(thumb: 120));
            box.AddChild(QHint(L.T("quick.params_hint")));
        }
        var panel = Ui.Box(box, Ui.Panel, 14);
        var center = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        center.AddChild(panel);
        if (!map)
            panel.CustomMinimumSize = new Vector2(760, 0);
        over.AddChild(center);
        return over;
    }

    private void QuickClose()
    {
        _qModal = QuickModal.None;
        _qAsk = null;
        QuickShow();
    }

    /// <summary>A question before a step that throws work away or burns points:
    /// <paramref name="act"/> on «<paramref name="yes"/>»; straight away when
    /// <paramref name="ask"/> is false, nothing to lose.</summary>
    private void QuickAsk(bool ask, string head, string text, string yes, Action act, string? no = null)
    {
        if (ask)
        {
            _qAsk = (head, text, yes, no ?? L.T("quick.cancel"), act);
            _qModal = QuickModal.Ask;
        }
        else
            act();
        QuickShow();
    }

    // --- the tooltip ---------------------------------------------------------------

    /// <summary>The screens' tips: the game's one (<see cref="UiTip"/>), on their layer.</summary>
    private UiTip? _qTips;

    /// <summary>The tip on <paramref name="pad"/>, built when it shows. Beside the pad -
    /// or beside its card, level with the pad (<see cref="UiTip.Beside"/>); the pad
    /// ringed by its own shape, unless it has none of its own: a branch's head is its
    /// band, and a ring round the pad inside it stood off the band's edges.</summary>
    private void QTip(QuickPad pad, Func<Control> build) =>
        pad.Over += (p, on) =>
        {
            if (_qTips is null)
                return;
            if (!on)
            {
                _qTips.Hide(p);
                return;
            }
            bool shaped = p.Form != QuickPad.Shape.Box || p.RimWidth > 0;
            _qTips.Show(p, p.GetGlobalRect, build, UiTip.Prefer.Beside, () => QuickTipNear(p),
                        shaped ? on => { p.Mark = on ? Ui.TipRim : null; p.QueueRedraw(); } : null, ring: false);
        };

    /// <summary>The rect a pad's tip stands by: the pad's, or its card's across at the
    /// pad's height.</summary>
    private static Rect2 QuickTipNear(QuickPad pad)
    {
        Rect2 own = pad.GetGlobalRect();
        Node? card = pad;
        while (card is not null && !card.HasMeta(UiTip.Beside))
            card = card.GetParent();
        return card is Control box ? new Rect2(box.GetGlobalRect().Position.X, own.Position.Y, box.Size.X, own.Size.Y) : own;
    }

    // --- the minimap ---------------------------------------------------------------

    /// <summary>The map from above as hexes, turned so the player's zone is at
    /// the bottom, as the placement looks at it. The zones are what the map is
    /// read for, so the ground round them is drawn faint and they in full
    /// colour, each ringed on its outer edges - the own one in light olive, the
    /// enemy's in red and hatched; the point in amber. With
    /// <paramref name="labels"/> the zones' names stand in strips above and
    /// below the map, not on the zones.</summary>
    private Control QuickMinimap(string name, Side player, float width, bool labels, bool ground = true)
    {
        BoardMap? map = QuickMap(name);
        float aspect = map is null ? 1.0f
            : (map.Rows + 0.5f) * 0.866f / ((map.Columns - 1) * 0.75f + 1.0f);
        var c = new Control
        {
            CustomMinimumSize = new Vector2(width, width * aspect + (labels ? 2 * QMapStrip : 0)),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        if (map is not null)
            c.Draw += () => QuickMapDraw(c, map, player, labels, ground);
        return c;
    }

    private static readonly Color QField = new("5F8A3A"), QWood = new("3F5E2A"), QWater = new("3E6E8E"), QWall = new("8B5A3C");

    /// <summary>The strip a zone's name stands in, above or below the map, px.</summary>
    private static float QMapStrip => Ui.Large ? 28.0f : 22.0f;

    /// <summary>What the ground outside the zones fades to.</summary>
    private static readonly Color QFaint = new("2E352B");

    private static void QuickMapDraw(Control c, BoardMap map, Side player, bool labels, bool ground)
    {
        float strip = labels ? QMapStrip : 0.0f;
        // no frame round the map: the hexes and the zones' rings keep a margin of their own inside the box
        var area = new Rect2(0, strip, c.Size.X, c.Size.Y - 2 * strip);
        float Fit(Rect2 r) => Mathf.Min(r.Size.X / ((map.Columns - 1) * 0.75f + 1.0f), r.Size.Y / ((map.Rows + 0.5f) * 0.866f));
        // the ring's half width and a little more, so it stops short of the box and the names
        area = area.Grow(-(QuickRingWidth(Fit(area)) * 0.5f + 3.0f));
        float w = Fit(area);
        float h = w * 0.866f;
        var extent = new Vector2((map.Columns - 1) * 0.75f * w + w, (map.Rows + 0.5f) * h);
        Vector2 off = area.Position + (area.Size - extent) * 0.5f;
        IReadOnlyList<Vector2I> own = map.Arena.Zone(player == Side.Green ? Parking.Green : Parking.Brown);
        IReadOnlyList<Vector2I> foe = map.Arena.Zone(player == Side.Green ? Parking.Brown : Parking.Green);
        bool flip = !QuickSouth(map, player);
        Vector2 Mid(Vector2I cell)
        {
            var at = new Vector2(cell.X * 0.75f * w + w * 0.5f, cell.Y * h + (cell.X % 2 != 0 ? h * 0.5f : 0.0f) + h * 0.5f);
            return off + (flip ? extent - at : at);
        }
        var hex = new Vector2[6];
        Vector2[] Hex(Vector2I cell, float scale)
        {
            Vector2 m = Mid(cell);
            for (int k = 0; k < 6; k++)
            {
                float a = Mathf.DegToRad(60.0f * k);
                hex[k] = m + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * w * 0.5f * scale;
            }
            return hex;
        }
        var zoned = new HashSet<Vector2I>(own);
        zoned.UnionWith(foe);
        // its own dark ground, or none where it stands straight on a panel (the hangar's)
        if (ground)
            c.DrawRect(new Rect2(Vector2.Zero, c.Size), Ui.Inset);
        for (int r = 0; r < map.Rows; r++)
            for (int q = 0; q < map.Columns; q++)
            {
                var cell = new Vector2I(q, r);
                if (map.Plot is { } plot && !plot.Contains(cell))
                    continue;
                int i = map.At(cell);
                Color fill = map.Water[i] ? QWater
                    : map.Walls[i] ? QWall
                    : map.Kinds[i] == TerrainSet.Forest ? QWood
                    : QField.Lightened(Mathf.Clamp(map.Levels[i] * 0.16f, -0.3f, 0.5f));
                // the ground outside the zones faint: still read for water and wood, not a colour of its own
                if (!zoned.Contains(cell))
                    fill = fill.Lerp(QFaint, 0.62f);
                c.DrawColoredPolygon(Hex(cell, 1.0f), fill);
                c.DrawPolyline(new[] { hex[0], hex[1], hex[2], hex[3], hex[4], hex[5], hex[0] }, new Color(0, 0, 0, 0.25f), 1.0f);
            }
        foreach (Vector2I cell in foe)
        {
            c.DrawColoredPolygon(Hex(cell, 1.0f), new Color(Ui.Danger, 0.22f));
            // the hatching, a stroke across each hex
            c.DrawLine(hex[1].Lerp(hex[2], 0.5f), hex[4].Lerp(hex[5], 0.5f), new Color(Ui.Danger, 0.55f), 1.5f);
        }
        foreach (Vector2I cell in own)
            c.DrawColoredPolygon(Hex(cell, 1.0f), new Color(Ui.Accent, 0.18f));
        QuickMapRing(c, own, Ui.Accent.Lightened(0.35f), w, Mid, Hex);
        QuickMapRing(c, foe, Ui.Danger, w, Mid, Hex);
        if (map.Arena.Point is { } point)
        {
            c.DrawCircle(Mid(point.Cell), w * 0.3f, Ui.Extra);
            c.DrawArc(Mid(point.Cell), w * 0.46f, 0, Mathf.Tau, 24, Ui.Extra, 2.0f);
        }
        if (!labels)
            return;
        Font font = Ui.BoldFont;
        void Chip(string text, IReadOnlyList<Vector2I> zone, Color ink, bool top)
        {
            if (zone.Count == 0)
                return;
            float x = 0.0f;
            foreach (Vector2I v in zone)
                x += Mid(v).X;
            x /= zone.Count;
            Vector2 size = font.GetStringSize(text, HorizontalAlignment.Left, -1, Ui.Size(12)) + new Vector2(16, 4);
            float y = top ? (strip - size.Y) * 0.5f : c.Size.Y - strip + (strip - size.Y) * 0.5f;
            // kept inside the map's box; a name wider than the box stands centred over it
            float left = size.X >= c.Size.X ? (c.Size.X - size.X) * 0.5f : Mathf.Clamp(x - size.X * 0.5f, 0, c.Size.X - size.X);
            var rect = new Rect2(new Vector2(left, y), size);
            c.DrawRect(rect, Ui.Panel);
            c.DrawRect(rect, ink, false, 1.0f);
            c.DrawString(font, rect.Position + new Vector2(8, font.GetAscent(Ui.Size(12)) + 2), text, HorizontalAlignment.Left, -1, Ui.Size(12), ink);
        }
        // the own zone is the bottom one, the map being turned so
        Chip(L.T("quick.zone_own"), own, Ui.Accent.Lightened(0.35f), top: false);
        Chip(L.T("quick.zone_foe"), foe, Ui.Danger, top: true);
    }

    /// <summary>A zone's outer edges, the ones its hexes share with a hex not
    /// in it: each neighbour's edge is the one whose middle lies nearest the
    /// midpoint between the two centres, which holds however the map is turned.</summary>
    private static void QuickMapRing(Control c, IReadOnlyList<Vector2I> zone, Color ink, float w,
                                     Func<Vector2I, Vector2> mid, Func<Vector2I, float, Vector2[]> hexOf)
    {
        var inside = new HashSet<Vector2I>(zone);
        foreach (Vector2I cell in zone)
        {
            Vector2[] hex = hexOf(cell, 1.0f);
            Vector2 m = mid(cell);
            bool odd = cell.X % 2 != 0;
            // odd-q: an odd column sits half a hex lower
            foreach (Vector2I d in odd ? QOddSteps : QEvenSteps)
            {
                Vector2I n = cell + d;
                if (inside.Contains(n))
                    continue;
                Vector2 half = (m + mid(n)) * 0.5f;
                int best = 0;
                float near = float.MaxValue;
                for (int k = 0; k < 6; k++)
                {
                    float dist = ((hex[k] + hex[(k + 1) % 6]) * 0.5f).DistanceSquaredTo(half);
                    if (dist < near)
                    {
                        near = dist;
                        best = k;
                    }
                }
                c.DrawLine(hex[best], hex[(best + 1) % 6], ink, QuickRingWidth(w));
            }
        }
    }

    /// <summary>A zone's ring, px, for hexes <paramref name="w"/> across.</summary>
    private static float QuickRingWidth(float w) => Mathf.Max(2.0f, w * 0.16f);

    private static readonly Vector2I[] QEvenSteps =
        { new(1, 0), new(0, 1), new(-1, 0), new(-1, -1), new(0, -1), new(1, -1) };
    private static readonly Vector2I[] QOddSteps =
        { new(1, 1), new(0, 1), new(-1, 1), new(-1, 0), new(0, -1), new(1, 0) };

    private static Control QuickLegend(int columns = 2)
    {
        var grid = new GridContainer { Columns = columns, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        grid.AddThemeConstantOverride("h_separation", 14);
        grid.AddThemeConstantOverride("v_separation", 4);
        foreach ((Color c, string name) in new[]
                 {
                     (QField, L.T("quick.legend_field")), (QField.Lightened(0.32f), L.T("quick.legend_height")), (QWood, L.T("quick.legend_forest")),
                     (QWater, L.T("quick.legend_water")), (QWall, L.T("quick.legend_wall")), (Ui.Extra, L.T("quick.point")),
                 })
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 6);
            row.AddChild(new QuickPad
            {
                Form = QuickPad.Shape.Hex, Rim = c, Fill = c, CustomMinimumSize = new Vector2(13, 11),
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter, MouseFilter = Control.MouseFilterEnum.Ignore,
            });
            row.AddChild(Ui.Text(name, 12, Ui.Muted));
            grid.AddChild(row);
        }
        return grid;
    }

    // --- the bits ------------------------------------------------------------------

    private static Label QEyebrow(string text) => Ui.Text(text.ToUpperInvariant(), 11, Ui.Muted, bold: true);

    private static Label QHint(string text, bool wrap = false) => Ui.Text(text, 13, Ui.Muted, wrap: wrap);

    /// <summary>The prototype's buttons: plain, picked (a choice made), primary.</summary>
    private static QuickPad QBtn(string text, Action clicked, bool primary = false, bool small = false, bool pressed = false)
    {
        bool picked = pressed;
        var b = new QuickPad
        {
            Text = text, FontSize = Ui.Size(primary ? 16 : small ? 13 : 14), Bold = primary,
            // the primary one framed by a light rim and lit plainly under the pointer: an
            // olive rim on an olive face showed no edge, and 8% lighter showed no hover
            Rim = primary ? Ui.Ink.Darkened(0.25f) : picked ? Ui.Accent : Ui.Line,
            Fill = primary ? Ui.Accent : picked ? Ui.AccentFill : Ui.Inset,
            Ink = primary ? Ui.OnAccent : Ui.Ink,
            HoverRim = primary ? Colors.White : Ui.Accent,
            HoverFill = primary ? Ui.Accent.Lightened(0.25f) : null,
            RimWidth = primary ? 2.0f : 1.0f,
        };
        b.Fit(primary ? new Vector2(22, 11) : small ? new Vector2(10, 5) : new Vector2(14, 8));
        b.Clicked += clicked;
        return b;
    }

    /// <summary>A fact of the battle: plain, or a door to the parameters.</summary>
    private static QuickPad QChip(string text, Action? clicked = null)
    {
        var b = new QuickPad
        {
            Text = text, FontSize = Ui.Size(13), Rim = Ui.Line, Fill = Ui.Panel, Ink = Ui.Ink,
            HoverRim = clicked is null ? null : Ui.Accent,
            MouseFilter = clicked is null ? Control.MouseFilterEnum.Ignore : Control.MouseFilterEnum.Stop,
        }.Fit(new Vector2(10, 4));
        if (clicked is not null)
            b.Clicked += clicked;
        return b;
    }


    // --- the squads on the board ---------------------------------------------------

    /// <summary>
    /// In <c>LineUpSides</c>: the map's homes for the two squads - each side's
    /// first three, then its zone's free cells if it has fewer - each with its
    /// machine's class and kit. The map's other homes stand empty.
    /// </summary>
    private void QuickHomes()
    {
        if (_quick is not { } q)
            return;
        string[] sides = Parking.SidesOf(_parkings);
        var homes = new List<Parking>();
        foreach (Side side in new[] { Side.Green, Side.Brown })
        {
            string name = side == Side.Green ? Parking.Green : Parking.Brown;
            QuickSquad.Slot[] squad = side == q.Player ? q.Squad : q.Ai;
            var cells = new List<(Vector2I Cell, int? Facing)>();
            for (int i = 0; i < _parkings.Count; i++)
                if (sides[i] == name)
                    cells.Add((_parkings[i].Cell, _parkings[i].Facing));
            foreach (Vector2I cell in _arena.Zone(name))
                if (cells.All(c => c.Cell != cell))
                    cells.Add((cell, null));
            for (int i = 0; i < squad.Length && i < cells.Count; i++)
                homes.Add(new Parking(cells[i].Cell, squad[i].Class, name, cells[i].Facing, QuickSquad.KitOf(squad[i])));
        }
        _parkings = homes;
    }

    // --- back to the build, and the reveal -------------------------------------------

    /// <summary>«К сборке» in the placement: the scene again, the setup kept.</summary>
    private void QuickBackToBuild()
    {
        if (_quick is null)
            return;
        _quickBack = _quick;
        Log.Print("tank3d: quick: back to the build");
        GetTree().ReloadCurrentScene();
    }

    private Control? _qReveal;

    /// <summary>The player's «В бой» (<c>DeployReady</c>): both squads open,
    /// the toss said - the prototype's reveal, over the board.</summary>
    private void QuickReveal()
    {
        if (_quick is not { } q || _noUi || _banner?.GetParent() is not CanvasLayer layer)
            return;
        bool mineFirst = _mt.First == q.Player;
        var sheet = new VBoxContainer();
        sheet.AddThemeConstantOverride("separation", 10);
        sheet.AddChild(QEyebrow(L.T("quick.reveal_eyebrow")));
        sheet.AddChild(Ui.Text(L.T("quick.toss", mineFirst ? L.T("quick.toss_you") : L.T("quick.toss_ai")), 22, Ui.Ink, bold: true));
        sheet.AddChild(QHint(L.T("quick.reveal_next", q.Map, QuickMode(q.Map))));
        var cols = new HBoxContainer();
        cols.AddThemeConstantOverride("separation", 14);
        cols.AddChild(QuickRevealSide(L.T("quick.reveal_you", QuickSouthSaid(q.Player)), q.Squad, q.Budget, q.Player, mineFirst, ""));
        cols.AddChild(QuickRevealSide(L.T("quick.reveal_ai", QuickSouthSaid(QuickAi)), q.Ai, q.AiBudget, QuickAi, !mineFirst,
                                      q.AiDoctrine is { } d ? L.T("quick.reveal_doctrine", d.Name) : ""));
        sheet.AddChild(cols);
        var go = QBtn(L.T("quick.to_battle"), () => { _qReveal?.QueueFree(); _qReveal = null; }, primary: true);
        go.SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd;
        sheet.AddChild(go);
        var over = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Stop };
        over.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        over.AddChild(Ui.Box(sheet, Ui.Panel, 18));
        _qReveal = over;
        layer.AddChild(over);
        Log.Print($"tank3d: quick: revealed - the AI {q.AiDoctrine?.Name}: {QuickSquad.Said(q.Ai)}");
    }

    private string QuickSouthSaid(Side side) => QuickSouth(QuickMap(_quick!.Map), side) ? L.T("quick.south") : L.T("quick.north");

    private Control QuickRevealSide(string title, QuickSquad.Slot[] squad, int budget, Side side, bool first, string extra)
    {
        var col = new VBoxContainer { CustomMinimumSize = new Vector2(380, 0) };
        col.AddThemeConstantOverride("separation", 8);
        col.AddChild(QEyebrow(first ? L.T("quick.moves_first") : L.T("quick.moves_second")));
        col.AddChild(Ui.Text(title, 18, SideInk(side), bold: true));
        col.AddChild(QHint(L.T("quick.reveal_spent", QuickSquad.Spent(squad), budget, extra)));
        foreach (QuickSquad.Slot s in squad)
        {
            if (s.Empty)
                continue;
            QuickSquad.Class c = QuickSquad.ClassOf(s.Class!);
            col.AddChild(new ColorRect { Color = Ui.Line, CustomMinimumSize = new Vector2(0, 1) });
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 12);
            row.AddChild(new TextureRect
            {
                Texture = QuickArtOf(c.Tag, side),
                CustomMinimumSize = new Vector2(96, 72),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            });
            var text = new VBoxContainer();
            text.AddChild(Ui.Text($"{c.Name} · {(s.Level > 0 ? L.T("quick.reveal_branch", c.BranchOf(s.Branch)?.Name ?? "", s.Level) : L.T("quick.neutral"))}", 14, Ui.Ink, bold: true));
            foreach (QuickSquad.Upgrade u in QuickSquad.UpgradesOf(s))
                text.AddChild(Ui.Text($"•  {u.Name}{(u.Flag is null ? " *" : "")}", 13, Ui.Muted));
            row.AddChild(text);
            col.AddChild(row);
        }
        return Ui.Box(col, Ui.Panel, 14, first ? Ui.Accent : Ui.Line);
    }

    // --- the script ----------------------------------------------------------------

    /// <summary>
    /// <c>--quick-do "STEP;STEP"</c>: the screens driven for a capture, a step
    /// every few frames - <c>lobby</c>, <c>build</c>, <c>params</c>,
    /// <c>mapview</c>, <c>close</c>, <c>map:NAME</c>, <c>swap</c>,
    /// <c>budget:N</c>, <c>ai-budget:N</c>, <c>doctrine:N|random</c>,
    /// <c>company:N</c> (from 1), <c>class:SLOT:TAG</c>,
    /// <c>level:SLOT:BRANCH:L</c>, <c>tip:SLOT:BRANCH:L</c> (the pointer on a
    /// node), <c>shot:PATH</c> (once the pictures are in) and <c>go</c>.
    /// </summary>
    private async void QuickScript()
    {
        foreach (string step in _quickDo)
        {
            for (int f = 0; f < 4; f++)
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (_qRoot is null)
                return;
            string[] p = step.Split(':');
            QuickSetup q = _qs;
            int Slot(string s) => Math.Clamp((int)F(s, 1) - 1, 0, QuickSquad.Size - 1);
            switch (p[0])
            {
                case "lobby": _qPage = QuickPage.Lobby; break;
                case "build": _qPage = QuickPage.Build; break;
                // the hangar's (Tank3DBench.Hangar): the slot on the podium
                case "slot" when p.Length > 1: _qhAt = Slot(p[1]); _qhSpin = _qhAt; break;
                // the ring caught mid-turn: the slot set, the spin left where it was
                case "turn-to" when p.Length > 1: _qhAt = Slot(p[1]); break;
                case "params": _qModal = QuickModal.Params; break;
                case "mapview": _qModal = QuickModal.Map; break;
                case "close": _qModal = QuickModal.None; _qAsk = null; break;
                case "try-go": QuickTryGo(); continue;
                case "clear": QuickAsk(true, L.T("quick.clear_ask"), L.T("quick.clear_text"), L.T("quick.clear"),
                                       () => { q.Squad = QuickSquad.Empty(); q.Preset = null; q.Name = null; }); continue;
                case "focus" when p.Length > 3:
                    // the keyboard's way to a node: its tip comes up with its focus
                    QuickShow();
                    for (int f = 0; f < 2; f++)
                        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                    if (_qTips is not null)
                        _qTips.Hold = true;
                    QuickPadAt(Slot(p[1]), p[2], (int)F(p[3], 1))?.GrabFocus();
                    continue;
                case "drop" when p.Length > 3:
                    // the node's right button, as the pointer would give it: a bought level taken back
                    QuickShow();
                    for (int f = 0; f < 2; f++)
                        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                    QuickPadAt(Slot(p[1]), p[2], (int)F(p[3], 1))?._GuiInput(
                        new InputEventMouseButton { ButtonIndex = MouseButton.Right, Pressed = true });
                    continue;
                case "map" when p.Length > 1: q.Map = p[1]; break;
                case "swap": q.Player = Across(q.Player); QuickArt(); break;
                case "budget" when p.Length > 1: q.Budget = (int)F(p[1], q.Budget); QuickSquad.Trim(q.Squad, q.Budget); break;
                case "ai-budget" when p.Length > 1: q.AiBudget = (int)F(p[1], q.AiBudget); break;
                case "doctrine" when p.Length > 1: q.Doctrine = p[1] == "random" ? null : (int)F(p[1], 1) - 1; break;
                case "company" when p.Length > 1:
                    q.Preset = QuickSquad.Companies[Math.Clamp((int)F(p[1], 1) - 1, 0, 3)];
                    q.Squad = QuickSquad.OfCompany(q.Preset);
                    q.Name = null;
                    break;
                case "preset" when p.Length > 1:
                    // as a click on the ready squad: asked first only when the squad was changed
                    QuickHangarPick(QuickSquad.Companies[Math.Clamp((int)F(p[1], 1) - 1, 0, 3)]); continue;
                case "name" when p.Length > 1: q.Name = p[1]; break;
                case "class" when p.Length > 2:
                    if (QuickSquad.ClassWhy(q.Squad, Slot(p[1]), p[2].ToUpperInvariant()) is { Length: > 0 } no)
                        Log.Print($"tank3d: quick: {step} - {no}");
                    else
                        q.Squad[Slot(p[1])] = new QuickSquad.Slot(p[2].ToUpperInvariant(), null, 0);
                    break;
                case "level" when p.Length > 3:
                    int at = Slot(p[1]), l = (int)F(p[3], 0);
                    if (QuickSquad.LevelWhy(q.Squad, at, p[2], l, q.Budget) is { Length: > 0 } why)
                        Log.Print($"tank3d: quick: {step} - {why}");
                    else
                        q.Squad[at] = q.Squad[at] with { Branch = l > 0 ? p[2] : null, Level = l };
                    break;
                case "tip" when p.Length > 3:
                    QuickShow();
                    for (int f = 0; f < 2; f++)
                        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                    // a script's tip at once: the shot after it does not wait out the rest
                    if (_qTips is not null)
                        _qTips.Hold = true;
                    QuickPointAt(Slot(p[1]), p[2], (int)F(p[3], 1));
                    continue;
                case "shot" when p.Length > 1:
                    for (int f = 0; f < 600 && _qArtLeft > 0; f++)
                        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                    if (_qTips is not { Showing: true })
                        QuickShow();
                    for (int f = 0; f < 6; f++)
                        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                    string path = string.Join(":", p.Skip(1));
                    Error err = GetViewport().GetTexture().GetImage().SavePng(path);
                    Log.Print(err == Error.Ok ? $"capture: {path}" : $"capture to {path} failed: {err}");
                    continue;
                case "go":
                    // the button waits for three; a script's empty slot is filled as the timer's would be
                    if (q.Squad.Any(s => s.Empty))
                    {
                        QuickSquad.Fill(q.Squad);
                        Log.Print($"tank3d: quick: go - the empty slots filled by «Сводная»: {QuickSquad.Said(q.Squad)}");
                    }
                    _qGo = true;
                    return;
                default: Log.Print($"tank3d: quick: no step {step}"); continue;
            }
            QuickShow();
        }
    }

    /// <summary><c>tip:</c> - the tooltip of slot <paramref name="at"/>'s node
    /// <paramref name="level"/> of <paramref name="branch"/>, as the pointer would raise it.</summary>
    private void QuickPointAt(int at, string branch, int level) =>
        QuickPadAt(at, branch, level)?.EmitSignal(Control.SignalName.MouseEntered);

    /// <summary>Slot <paramref name="at"/>'s node <paramref name="level"/> of
    /// <paramref name="branch"/>, or level 0: the branch's head.</summary>
    private QuickPad? QuickPadAt(int at, string branch, int level)
    {
        if (_qRoot is null || _qs.Squad[at].Class is not { } cls || QuickSquad.ClassOf(cls).BranchOf(branch) is not { } b)
            return null;
        // the nodes of one level in the page's order: two a slot, a branch each
        var pads = _qRoot.FindChildren("*", "", true, false).OfType<QuickPad>()
                         .Where(x => level > 0 ? x.HasMeta("q_level") && (int)x.GetMeta("q_level") == level
                                               : x.HasMeta("q_branch")).ToList();
        // the hangar draws the podium's slot's tree alone
        if (_qHangar && at != _qhAt)
            return null;
        int index = (_qHangar ? 0 : at * 2) + Array.IndexOf(QuickSquad.ClassOf(cls).Branches, b);
        return index >= 0 && index < pads.Count ? pads[index] : null;
    }

    /// <summary><c>--quick-do</c>'s steps, in order.</summary>
    private readonly List<string> _quickDo = new();
}
