using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The map editor: a board, three tabs of brushes, and the audit running on
/// every stroke.
///
/// <code>
///     Godot_v4.7.2-stable_mono_win64.exe --path GodotTest res://Editor.tscn
///     ... res://Editor.tscn -- --map abbey
///     ... res://Editor.tscn -- --new 20x17
/// </code>
///
/// <b>A root of its own, and the argument that says otherwise does not
/// carry.</b> <see cref="BoardMap"/> refuses a second copy of board
/// <i>authoring</i>, not a second root - <see cref="TankBench"/>,
/// <see cref="EffectsBench"/> and <see cref="WoodBench"/> each build their own
/// <see cref="HexField"/> because those are shared modules rather than the
/// harness's property. This is the single copy of authoring, and with a menu and
/// a new-board dialog on it there was nowhere else for it to be.
///
/// <b>It is <c>--map-report</c> made interactive, and that is most of what it
/// is.</b> Everything the author has to be told, <see cref="BoardMap.Audit"/>
/// already said: where a ramp is legal and which way it would tilt, which cells
/// the water takes, what is stranded from each home. What was missing was a
/// brush and having it recomputed on the stroke rather than on a command line.
///
/// <b>The draft always builds, because what is illegal is never the height.</b>
/// Levels are numbers and refuse nothing; ramps and water do. So an illegal ramp
/// is lifted off the field for the frame and painted as a fault, and the cells
/// the flood would take are shown flooded and marked - the board never freezes
/// mid-stroke, which was the first design for this and solved a problem that
/// does not exist.
///
/// <b>Nothing here knows how to draw a board.</b> The field is
/// <see cref="HexField"/>, the marks go through its <see cref="HexField.Ink"/>
/// hook, and the board that comes out is <see cref="BoardMap.FromGround"/>'s. A
/// board that looked different in the editor from in the game would be an editor
/// nobody could trust, and the only way to guarantee that is to not have a
/// second renderer.
///
/// <b>It opens on the stage, and that is not a preference.</b> The 2D board is
/// the old answer - <see cref="WoodBench"/> and <see cref="EffectsBench"/> both
/// put its canvas away for the same reason - and an editor that drew the flat
/// field would be an editor in which a slope, a pond and a wood cannot be judged
/// at all. So the field keeps the arithmetic, <see cref="Stage3D"/> draws it, and
/// <see cref="HexField.ShowField"/> goes off.
///
/// <b>And the marks needed nothing for it.</b> <see cref="Stage3D.Signature"/>
/// already folds <see cref="HexField.InkFor"/> into what it rebuilds on - a cell
/// that changes colour without changing level reads as a board that was not
/// rebuilt - so the editor's faults, sites and brush reach the 3D ground through
/// the same hook that paints the harness's routes and arcs, and the stage
/// notices a stroke by itself.
/// </summary>
public sealed partial class MapEditor : SceneRoot
{
    public MapEditor() : base(0) { }

    /// <summary>Which board to open, when a scene wants one without a flag.
    /// </summary>
    [Export] public string Map = "";

    private const string TileTag = "MTP";

    private MapEdit _edit = null!;
    private HexField _field = null!;
    private Camera2D _camera = null!;
    private Node2D _marks = null!;
    private Grove? _grove;
    private Wildfire? _fire;
    private Swell? _sea;
    private WaterArt? _surf;
    private Stage3D? _stage;
    private string?[] _sown = Array.Empty<string?>();

    private MapEdit.Tab _tab = MapEdit.Tab.Foundation;
    private int _radius;
    private Foundation _floor = Foundation.Solid;
    private Cover _cover = Cover.Forest;
    private bool _water;
    private bool _plotBrush;
    private int _level = 1;
    private bool _rampBrush;
    private int _home = -1;

    private Vector2I _under = new(-1, -1);
    private bool _painting;
    private bool _erasing;

    private IReadOnlyList<MapRules.Fault> _faults = Array.Empty<MapRules.Fault>();
    private IReadOnlyDictionary<Vector2I, int> _sites =
        new Dictionary<Vector2I, int>();
    private IReadOnlyList<Vector2I> _closure = Array.Empty<Vector2I>();
    private HashSet<Vector2I> _dropped = new();
    private string _said = "";

    private Label _hud = null!;
    private RichTextLabel _report = null!;

    // --- overlays ------------------------------------------------------------

    /// <summary>Which of the drawn-on-top layers are up. Flags rather than one
    /// mode, because reading a ramp fault means seeing the levels and the ramp
    /// arrows at once.</summary>
    [Flags]
    private enum Layers
    {
        None = 0,
        Levels = 1,
        Ramps = 2,
        Sites = 4,
        Faults = 8,
        Flood = 16,
        Homes = 32,
    }

    private Layers _layers = Layers.Ramps | Layers.Faults | Layers.Homes;

    // --- setup ---------------------------------------------------------------

    public override void _Ready()
    {
        int newColumns = 0, newRows = 0;
        string[] args = OS.GetCmdlineUserArgs();
        for (int i = 0; i < args.Length; i++)
        {
            if (ReadCommonFlag(args, ref i))
                continue;
            if (args[i] == "--map" && i + 1 < args.Length)
                Map = args[++i];
            else if (args[i] == "--menu" && i + 1 < args.Length)
                _open = args[++i].ToLowerInvariant();
            else if (args[i] == "--pick" && i + 1 < args.Length)
            {
                // A cell picked from the command line, --layers' reason word for
                // word: the right panel only exists once a hex has been clicked,
                // and a capture run has nobody to click one.
                string[] at = args[++i].Split(',');
                if (at.Length == 2 && int.TryParse(at[0], out int q)
                    && int.TryParse(at[1], out int r))
                    _picked = new Vector2I(q, r);
                else
                    GD.PushWarning("--pick wants Q,R, as in --pick 3,2");
            }
            else if (args[i] == "--layers" && i + 1 < args.Length)
            {
                // Named rather than left to the keys, for --capture's sake: a
                // shot of the ramp sites has to be takeable, and a capture run
                // has nobody to press G.
                _layers = Layers.None;
                foreach (string want in args[++i].ToLowerInvariant()
                             .Split(',', StringSplitOptions.RemoveEmptyEntries))
                    _layers |= want switch
                    {
                        "levels" => Layers.Levels,
                        "ramps" => Layers.Ramps,
                        "sites" => Layers.Sites,
                        "faults" => Layers.Faults,
                        "flood" => Layers.Flood,
                        "homes" => Layers.Homes,
                        _ => Layers.None,
                    };
            }
            else if (args[i] == "--new" && i + 1 < args.Length)
            {
                string[] parts = args[++i].ToLowerInvariant().Split('x');
                if (parts.Length == 2 && int.TryParse(parts[0], out newColumns)
                    && int.TryParse(parts[1], out newRows))
                    continue;
                GD.PushWarning("--new wants WxH, as in --new 20x17");
                newColumns = newRows = 0;
            }
        }

        TerrainSet terrain = TerrainSet.Load(AssetRoot.Terrains);
        AtlasSet? tile = null;
        try
        {
            tile = AtlasSet.Load(AssetRoot.Sprites, TileTag);
        }
        catch (Exception e)
        {
            // Said out loud rather than left as a blank window: with no tile the
            // field has no distance on the board at all and draws nothing.
            GD.PushWarning($"editor: no {TileTag} atlas ({e.Message}) - no board");
        }

        if (newColumns > 0)
        {
            _edit = MapEdit.Blank("untitled", newColumns, newRows);
        }
        else if (Map.Length > 0)
        {
            try
            {
                _edit = MapEdit.Of(BoardMap.ByName(Map));
            }
            catch (Exception e)
            {
                // The editor is the one tool that must survive a board it cannot
                // load - the same exemption --map-report has, and for the same
                // reason: it is what the author fixes it with. It opens blank and
                // says what happened.
                GD.Print($"editor: {Map} REFUSED {e.Message}");
                _edit = MapEdit.Blank("untitled", MapEdit.DefaultColumns,
                                      MapEdit.DefaultRows);
                _said = $"{Map} refused: {e.Message}";
            }
        }
        else
        {
            _edit = MapEdit.Blank("untitled", MapEdit.DefaultColumns,
                                      MapEdit.DefaultRows);
        }

        PropSet props = PropSet.Load(AssetRoot.Props);
        GD.Print("editor: props " + props.Note);

        _field = new HexField
        {
            Atlas = tile,
            Terrain = terrain,
            Trees = props.Any,
            Columns = _edit.Columns,
            Rows = _edit.Rows,
            Ink = Painted,
        };
        AddChild(_field);

        _grove = new Grove
        {
            Field = _field, Props = props, Origin = Vector2.Zero,
            Enabled = props.Any,
        };
        AddChild(_grove);
        // After the wood, because what can catch is what is standing - see
        // Grove.Carrying. Nothing lights it here; the editor is not a bench.
        _fire = new Wildfire
        {
            Field = _field, Enabled = false, Wooded = _grove.Carrying,
        };
        _grove.Fire = _fire;

        _camera = new Camera2D { Enabled = true, Zoom = new Vector2(1, 1) };
        AddChild(_camera);

        if (tile is not null)
        {
            _surf = WaterArt.Load(AssetRoot.Water, tile.HexRect);
            GD.Print("editor: water " + _surf.Note);
        }
        _sea = new Swell { Field = _field };

        _stage = new Stage3D
        {
            Field = _field, Origin = Vector2.Zero, Eye = _camera,
            Wood = _grove, Blaze = _fire, Surf = _surf, Sea = _sea,
        };
        AddChild(_stage);

        // Both canvas answers put away, WoodBench's pair and for its reasons: the
        // field's own hexagons would be a canvas item over the whole 3D world,
        // and the grove's nodes would draw every tree twice.
        _field.ShowField = false;
        _grove.Visible = false;

        // The marks go over the stage rather than into the field's pass - see
        // Overlay.
        _marks = new Overlay { Editor = this, ZIndex = 4096 };
        AddChild(_marks);

        SettleForProof(CapturePath is not null);
        Ui();
        Settle();
        Home();

        // A dialog opened from the command line, --layers' reason word for word:
        // a capture of the menu has to be takeable, and a capture run has nobody
        // to click it.
        switch (_open)
        {
            case "new": Chose(Item.New); break;
            case "open": Chose(Item.Open); break;
            case "saveas": Chose(Item.SaveAs); break;
        }
    }

    private string _open = "";

    /// <summary>
    /// The frame: the stage's own, and the capture.
    ///
    /// <b><see cref="Stage3D.Place"/> is called every frame with nothing to
    /// place</b>, which is <see cref="WoodBench"/>'s note word for word: Place is
    /// also where the stage rebuilds the wood's billboards, so a frame that
    /// skipped it is a frame the wind stopped in.
    ///
    /// <b>The editor has no clocks of its own and that is deliberate.</b> There
    /// is no fire to tick and no tank to move - a board being drawn is a board
    /// standing still - so the only thing here that moves is the water, and it
    /// moves inside the stage.
    /// </summary>
    public override void _Process(double delta)
    {
        _stage?.Place(Array.Empty<Vehicle>());
        _frame++;
        // The path the author takes, and the one that was broken: type into the
        // spin box and press OK without committing it. Value still says the old
        // number - see MapEditor.Menu's Typed, which is the fix - so the board
        // was rebuilt at the size it already was, which reads as the size not
        // changing at all.
        if (_open == "resize" && _frame == 4)
        {
            Chose(Item.New);
            _newColumns.GetLineEdit().Text = "6";
            _newRows.GetLineEdit().Text = "5";
            GD.Print($"resize: typed 6x5, the boxes still say "
                     + $"{(int)_newColumns.Value}x{(int)_newRows.Value}, "
                     + $"board {_edit.Columns}x{_edit.Rows}");
            _newDialog.EmitSignal(ConfirmationDialog.SignalName.Confirmed);
            GD.Print($"resize: after confirm the board is {_edit.Columns}x"
                     + $"{_edit.Rows}, field {_field.Columns}x{_field.Rows}");
        }
        if (_open == "poke" && _frame == 4)
        {
            Vector2 at = _bar.GetGlobalRect().GetCenter();
            GD.Print($"poke: the Map button is at {at}, "
                     + $"rect {_bar.GetGlobalRect()}");
            foreach (bool down in new[] { true, false })
                Input.ParseInputEvent(new InputEventMouseButton
                {
                    ButtonIndex = MouseButton.Left,
                    Pressed = down,
                    Position = at,
                    GlobalPosition = at,
                });
        }
        // The readout eating the board, A against B in one run.
        //
        // <b>It counts clicks arriving rather than cells picked, and the first
        // draft of it counted cells and proved nothing.</b> Pointed() asks
        // GetGlobalMousePosition - the real pointer - so a synthesised event
        // does not move what the editor thinks is under the cursor, and a
        // capture run has the mouse wherever the desktop left it. What is
        // actually in question is whether the click reaches
        // _UnhandledInput at all: a Control with mouse_filter Stop marks the
        // event handled and nothing downstream sees it.
        //
        // Godot gives Label that filter as Ignore in its own constructor and
        // leaves every other Control on the inherited Stop, so the report was
        // an invisible 520x420 button over the top left of the board.
        if (_open == "overlay" && _frame is 4 or 8)
        {
            bool stop = _frame == 4;
            _report.MouseFilter = stop
                ? Control.MouseFilterEnum.Stop : Control.MouseFilterEnum.Ignore;
            Vector2 at = _report.GetGlobalRect().GetCenter();
            _clicks = 0;
            foreach (bool down in new[] { true, false })
                Input.ParseInputEvent(new InputEventMouseButton
                {
                    ButtonIndex = MouseButton.Left,
                    Pressed = down,
                    Position = at,
                    GlobalPosition = at,
                });
            _probeStop = stop;
        }
        if (_open == "overlay" && _frame is 5 or 9)
            GD.Print($"overlay: the report is {_report.GetGlobalRect()} on "
                     + $"filter {(_probeStop ? "Stop" : "Ignore")}, and a click at "
                     + $"its middle reached the board {_clicks} time(s)");
        // Taking up the ramp brush shows where a ramp can go. Keys rather than
        // the palette, because a key goes in through ParseInputEvent whole - it
        // carries no position, so unlike the mouse it does not have to agree
        // with where the real pointer is (see --menu overlay, which learnt that
        // the hard way).
        if (_open == "ramp" && _frame is 4 or 6)
        {
            Key key = _frame == 4 ? Key.Key3 : Key.W;
            GD.Print($"ramp: before {key}, tab {_tab}, ramp brush {_rampBrush}, "
                     + $"sites {_layers.HasFlag(Layers.Sites)}");
            foreach (bool down in new[] { true, false })
                Input.ParseInputEvent(new InputEventKey
                {
                    Keycode = key, Pressed = down,
                });
        }
        if (_open == "ramp" && _frame == 8)
            GD.Print($"ramp: tab {_tab}, ramp brush {_rampBrush}, sites "
                     + $"{_layers.HasFlag(Layers.Sites)}, "
                     + $"{_sites.Count(kv => !_edit.IsRamp(kv.Key))} free site(s) "
                     + "drawn");
        if (CapturePath is not null && _frame >= CaptureAt)
        {
            Capture(CapturePath);
            GetTree().Quit();
        }
    }

    private int _frame;

    /// <summary>Mouse buttons that reached <see cref="_UnhandledInput"/> since
    /// the counter was last cleared, for --menu overlay and nothing else.
    /// </summary>
    private int _clicks;

    private bool _probeStop;

    /// <summary>
    /// Where a cell's top face is drawn, in world pixels.
    ///
    /// <b>The lift, not the flat anchor.</b> On the stage a cell stands as high
    /// as its level says, so a number drawn at the anchor sits on the ground the
    /// column was extruded from - a level below the face it is labelling, and
    /// two on a rise.
    /// </summary>
    private Vector2 Top(Vector2I cell) =>
        _field.CellCentre(cell) - new Vector2(0.0f, _field.TopAt(cell));

    /// <summary>
    /// Frame the whole board.
    ///
    /// <b>Fitted rather than parked at 1:1, and the difference is the whole
    /// board against a tenth of it.</b> Every bench here opens at a zoom chosen
    /// for its subject - one cell, four trunks, one wall - and an editor's
    /// subject is the map, which is 340 cells and about six windows wide at the
    /// atlas's own scale. A view that has to be panned before anything can be
    /// judged is a view that starts by hiding what the tool is for.
    ///
    /// Measured off the drawn tops rather than off columns and rows, so it fits
    /// the board's relief too: a rise stands a lift higher than its row, and a
    /// frame computed flat cuts the top off the highest cell on the board.
    /// </summary>
    private void Home()
    {
        Rect2 box = Extent();
        // The band between the panels rather than the whole window, and the
        // camera moved off centre by half the difference - otherwise the board
        // is fitted to a window a third of which it is drawn underneath.
        Vector2 window = GetViewportRect().Size;
        float left = LeftWide + 24.0f, right = RightWide + 24.0f;
        var band = new Vector2(Math.Max(window.X - left - right, 100.0f),
                               Math.Max(window.Y - PanelTop - 12.0f, 100.0f));
        float fit = Math.Min(band.X / Math.Max(box.Size.X, 1.0f),
                             band.Y / Math.Max(box.Size.Y, 1.0f));
        // A margin, because the outermost cell's own art reaches past its centre
        // and a board fitted exactly touches the window's edge with it.
        float zoom = ZoomAt ?? Math.Clamp(fit * 0.92f, 0.05f, 4.0f);
        _camera.Zoom = new Vector2(zoom, zoom);
        // Minus, because moving the camera left moves the board right: the
        // band's middle is (left - right) / 2 off the window's, and the camera
        // has to be the same distance the other way for the board to land on it.
        _camera.Position = box.GetCenter()
            - new Vector2((left - right) * 0.5f, (PanelTop - 12.0f) * 0.5f)
              / zoom;
    }

    /// <summary>The box the drawn board stands in, tops and all.</summary>
    private Rect2 Extent()
    {
        Vector2 pad = _field.Atlas?.HexRect.Size ?? new Vector2(128.0f, 96.0f);
        Vector2 low = Vector2.Inf, high = -Vector2.Inf;
        for (int r = 0; r < _edit.Rows; r++)
        for (int q = 0; q < _edit.Columns; q++)
        {
            // Every cell of the rectangle, not only the ones on the plot: the
            // board is what is being drawn, and a hole punched in its middle
            // must not move the view.
            Vector2 at = Top(new Vector2I(q, r));
            low = new Vector2(Math.Min(low.X, at.X), Math.Min(low.Y, at.Y));
            high = new Vector2(Math.Max(high.X, at.X), Math.Max(high.Y, at.Y));
        }
        if (low.X > high.X)
            return new Rect2(Vector2.Zero, pad);
        return new Rect2(low - pad, high - low + pad * 2.0f);
    }

    // --- the draft on to the field -------------------------------------------

    /// <summary>
    /// Push the draft on to the field and re-run the rules.
    ///
    /// <b>Every illegal ramp is lifted off rather than the board refusing.</b>
    /// <see cref="HexField.SetRelief"/> throws on the first ramp the map does not
    /// decide, which is right for a declared board and useless for one under a
    /// brush - so the ramps that <see cref="MapRules"/> called faulty are dropped
    /// for the frame and painted. The letters are untouched; what is dropped is
    /// what the field is shown, and the fault says so on the report.
    ///
    /// <b>Water is dropped the same way and for the same reason</b>, on the pairs
    /// the waterfall guard names.
    ///
    /// Called at the end of a stroke rather than per cell: a drag across forty
    /// cells is one rebuild, one entry on the undo stack and one audit.
    /// </summary>
    private void Settle()
    {
        MapRules.Draft draft = _edit.Draft();
        _faults = MapRules.Faults(draft);
        _sites = MapRules.Sites(draft);
        _closure = MapRules.Flood(draft);

        _dropped = _faults
            .Where(f => f.Rule is "ramp.bridge" or "ramp.rock")
            .Select(f => f.Cell).ToHashSet();
        var ramps = (bool[])draft.Ramps.Clone();
        foreach (Vector2I cell in _dropped)
            if (draft.On(cell))
                ramps[draft.At(cell)] = false;

        var water = (bool[])draft.Water.Clone();
        foreach (MapRules.Fault fault in _faults.Where(f => f.Rule == "water.step"))
            if (draft.On(fault.Cell))
                water[draft.At(fault.Cell)] = false;

        _field.Columns = _edit.Columns;
        _field.Rows = _edit.Rows;
        _field.Plot = draft.Plot;
        _field.SetRelief(draft.Levels, ramps);
        _field.SetWater(water.Any(w => w) ? water : null);
        _field.SetGround(draft.Ground);
        string?[] kinds = Kinds(draft);
        _field.SetKinds(kinds);
        _field.Paint = _edit.Paint ?? MapFile.PaintFor(_edit.Ground());

        // The wood is sown again only when the cells it grows on moved.
        // Grove.Plant walks every cell and rolls a species per prop, which is
        // two thousand of them on a board the size of ABBEY - paid once per
        // stroke that changes a wood, and never for a stroke that raises a hill.
        if (_grove is not null && _field.Atlas is not null
            && !kinds.SequenceEqual(_sown))
        {
            _sown = kinds;
            _grove.Plant();
        }
        _field.QueueRedraw();
        _marks.QueueRedraw();
        Say();
    }

    /// <summary>What each cell is painted as, off its own letter. The same two
    /// statements <see cref="BoardMap.FromGround"/> makes - a wood is forest, a
    /// rise and a rock are the risen plate - said here because the editor never
    /// builds the map until it is saved.</summary>
    private string?[] Kinds(MapRules.Draft draft)
    {
        var kinds = new string?[_edit.Columns * _edit.Rows];
        for (int r = 0; r < _edit.Rows; r++)
        for (int q = 0; q < _edit.Columns; q++)
        {
            var cell = new Vector2I(q, r);
            MapFile.Glyph glyph = _edit.GlyphAt(cell);
            kinds[draft.At(cell)] =
                glyph.Wood ? TerrainSet.Forest
                : glyph.Level == 1 || glyph.Cliff ? TerrainSet.Rise
                : null;
        }
        return kinds;
    }

    // --- marks ---------------------------------------------------------------

    private static readonly Color FaultInk = new(1.0f, 0.45f, 0.42f);
    private static readonly Color WarnInk = new(1.0f, 0.82f, 0.45f);
    private static readonly Color SiteInk = new(0.62f, 0.95f, 0.66f);
    private static readonly Color BrushInk = new(0.55f, 0.90f, 1.0f);
    private static readonly Color FloodInk = new(0.55f, 0.70f, 1.0f);
    private static readonly Color BrickInk = new(0.86f, 0.55f, 0.40f);

    /// <summary>
    /// The mark a cell carries, through <see cref="HexField.Ink"/>.
    ///
    /// Strongest first, the field's own order: what is wrong beats what is
    /// possible, and both beat where the pointer is. The brush is last because
    /// it moves - a mark that came and went with the mouse over a fault would
    /// hide the fault exactly while it was being looked at.
    /// </summary>
    private Color? Painted(Vector2I cell)
    {
        if (_layers.HasFlag(Layers.Faults))
        {
            foreach (MapRules.Fault fault in _faults)
                if (fault.Cell == cell)
                    return fault.Fatal ? FaultInk : WarnInk;
        }
        if (_layers.HasFlag(Layers.Flood) && _closure.Contains(cell))
            return FloodInk;
        if (_layers.HasFlag(Layers.Sites) && _sites.ContainsKey(cell)
            && !_edit.IsRamp(cell))
            return SiteInk;
        if (Patch().Contains(cell))
            return BrushInk;
        return null;
    }

    /// <summary>
    /// The cell under the mouse.
    ///
    /// <b>Through the field's own inverse and off the world position, both of
    /// which are the fix somebody already paid for.</b>
    /// <c>InputEventMouseButton.Position</c> is the pointer in viewport pixels
    /// and matches the world only at zoom 1 with the camera on the window's
    /// middle, and <see cref="HexField.CellAt"/> is what undoes the lift a cell
    /// is drawn at - without it a board with height answers with the cell in
    /// front of the one being looked at. See <c>EffectsBench.Pointed</c>, which
    /// says both at length.
    /// </summary>
    private Vector2I Pointed() =>
        _field.CellAt(_field.ToLocal(GetGlobalMousePosition()));

    private IReadOnlyList<Vector2I> Patch() =>
        _edit.Inside(_under) ? _edit.Ring(_under, _radius)
                             : Array.Empty<Vector2I>();

    /// <summary>
    /// The layers drawn over the board rather than into it - numbers, arrows and
    /// the homes.
    ///
    /// A node of its own above the field, because the field paints whole columns
    /// in depth order and anything drawn into that pass is painted over by the
    /// next cell in front of it.
    /// </summary>
    private sealed partial class Overlay : Node2D
    {
        public MapEditor Editor = null!;

        public override void _Draw()
        {
            MapEditor editor = Editor;
            if (editor._field.Atlas is null)
                return;
            Font font = ThemeDB.FallbackFont;

            for (int r = 0; r < editor._edit.Rows; r++)
            for (int q = 0; q < editor._edit.Columns; q++)
            {
                var cell = new Vector2I(q, r);
                if (!editor._edit.OnBoard(cell))
                    continue;
                Vector2 at = editor.Top(cell);

                if (editor._layers.HasFlag(Layers.Levels))
                {
                    int level = editor._edit.GlyphAt(cell).Level;
                    DrawString(font, at + new Vector2(-6.0f, 5.0f),
                        level.ToString(), HorizontalAlignment.Left, -1, 16,
                        new Color(1, 1, 1, 0.85f));
                }

                // Which way a ramp tilts, drawn as the arrow it is. Off the
                // field's derived heading rather than off the map's flag: what
                // is worth seeing is the edge the board decided, and on a faulty
                // ramp there is none - which is the arrow's absence saying so.
                if (editor._layers.HasFlag(Layers.Ramps)
                    && editor._edit.IsRamp(cell))
                {
                    int heading = editor._field.RampHeading(cell);
                    if (heading >= 0)
                    {
                        Vector2 to = editor.Top(HexField.Step(cell, heading));
                        Vector2 dir = (to - at).Normalized() * 22.0f;
                        DrawLine(at - dir * 0.4f, at + dir,
                            new Color(1.0f, 0.95f, 0.6f, 0.9f), 3.0f);
                        DrawCircle(at + dir, 4.0f,
                            new Color(1.0f, 0.95f, 0.6f, 0.9f));
                    }
                    else
                    {
                        DrawCircle(at, 6.0f, FaultInk);
                    }
                }

                if (editor._layers.HasFlag(Layers.Sites)
                    && editor._sites.TryGetValue(cell, out int up)
                    && !editor._edit.IsRamp(cell))
                {
                    Vector2 to = editor.Top(HexField.Step(cell, up));
                    DrawLine(at, at + (to - at).Normalized() * 16.0f,
                        new Color(SiteInk, 0.8f), 2.0f);
                }

                // Which edges the masonry stands on, drawn on the edges. The
                // 3D stage has no bricks in it - a wall on the board is a prop
                // somebody stands, and nothing stands props from a map yet - so
                // without this the six checkboxes in the right panel would be
                // the only place a wall's shape exists.
                if (editor._edit.MasonryAt(cell) is { } wall)
                {
                    foreach (int heading in wall.Standing())
                    {
                        (Vector2 a, Vector2 b) =
                            editor._field.EdgeEnds(cell, heading);
                        DrawLine(a, b, BrickInk, 5.0f);
                    }
                }

                if (cell == editor._picked)
                    DrawArc(at, 20.0f, 0.0f, Mathf.Tau, 18,
                        new Color(1, 1, 1, 0.75f), 2.0f);
            }

            if (!editor._layers.HasFlag(Layers.Homes))
                return;
            for (int i = 0; i < editor._edit.Homes.Count; i++)
            {
                Vector2 at = editor.Top(editor._edit.Homes[i]);
                DrawArc(at, 26.0f, 0.0f, Mathf.Tau, 24,
                    new Color(0.6f, 1.0f, 0.9f, 0.9f), 2.5f);
                DrawString(font, at + new Vector2(-4.0f, -28.0f),
                    (i + 1).ToString(), HorizontalAlignment.Left, -1, 15,
                    new Color(0.6f, 1.0f, 0.9f));
            }
        }
    }

    // --- input ---------------------------------------------------------------

    public override void _UnhandledInput(InputEvent what)
    {
        if (what is InputEventMouseMotion pan
            && (pan.ButtonMask & MouseButtonMask.Middle) != 0)
        {
            _camera.Position -= pan.Relative / _camera.Zoom;
            return;
        }

        if (what is InputEventMouseMotion move)
        {
            Vector2I was = _under;
            _under = Pointed();
            if (_painting || _erasing)
                Stroke(false);
            else if (was != _under)
            {
                _field.QueueRedraw();
                Say();
            }
            return;
        }

        if (what is InputEventMouseButton click)
        {
            _clicks++;
            if (click.ButtonIndex == MouseButton.WheelUp && click.Pressed)
                _camera.Zoom *= 1.1f;
            else if (click.ButtonIndex == MouseButton.WheelDown && click.Pressed)
                _camera.Zoom /= 1.1f;
            else if (click.ButtonIndex == MouseButton.Left)
            {
                _painting = click.Pressed;
                if (click.Pressed)
                {
                    // The press picks the cell as well as painting it, and the
                    // press rather than the release: a drag paints forty cells
                    // and the one the author meant is the one they aimed at.
                    if (_edit.Inside(_under))
                        _picked = _under;
                    Stroke(true);
                }
                else
                {
                    Settle();
                }
            }
            else if (click.ButtonIndex == MouseButton.Right)
            {
                _erasing = click.Pressed;
                if (click.Pressed)
                    Stroke(true);
                else
                    Settle();
            }
            else if (click.ButtonIndex == MouseButton.Middle
                     && MiddleTapped(click, out Vector2 _))
            {
                Home();
            }
            return;
        }

        if (what is not InputEventKey { Pressed: true, Echo: false } key)
            return;
        switch (key.Keycode)
        {
            case Key.Key1: _tab = MapEdit.Tab.Foundation; break;
            case Key.Key2: _tab = MapEdit.Tab.Cover; break;
            case Key.Key3: _tab = MapEdit.Tab.Level; break;
            case Key.Q: Cycle(); break;
            case Key.W: Toggle(true); break;
            case Key.E: Toggle(false); break;
            case Key.Bracketleft: _radius = Math.Max(0, _radius - 1); break;
            case Key.Bracketright: _radius = Math.Min(6, _radius + 1); break;
            case Key.Z: Take(_edit.Undo(), "undone"); return;
            case Key.Y: Take(_edit.Redo(), "redone"); return;
            case Key.F: Fill(); return;
            case Key.H: PlaceHome(); return;
            case Key.N: Chose(Item.New); return;
            case Key.O: Chose(Item.Open); return;
            case Key.S when key.ShiftPressed: Chose(Item.SaveAs); return;
            case Key.S: Keep(); return;
            case Key.L: _layers ^= Layers.Levels; break;
            case Key.R: _layers ^= Layers.Ramps; break;
            case Key.G: _layers ^= Layers.Sites; break;
            case Key.B: _layers ^= Layers.Flood; break;
            case Key.Home: Home(); return;
            default: return;
        }
        _field.QueueRedraw();
        _marks.QueueRedraw();
        Say();
    }

    private void Take(bool did, string what)
    {
        _said = did ? $"one stroke {what}" : $"nothing to be {what}";
        Settle();
    }

    // --- the brush -----------------------------------------------------------

    /// <summary>
    /// One stroke of the current tool.
    ///
    /// <b>The undo entry is pushed on the press and not on the cell</b>, which is
    /// what makes a drag across forty cells one undo - see
    /// <see cref="MapEdit.Begin"/>.
    /// </summary>
    private void Stroke(bool started)
    {
        if (!_edit.Inside(_under))
            return;
        if (started)
            _edit.Begin();

        IReadOnlyList<Vector2I> patch = Patch();
        IReadOnlyList<string> refused = _erasing
            ? _edit.Sweep(patch, c => (_edit.Erase(c, _tab, out string why), why))
            : _edit.Sweep(patch, Write);

        if (refused.Count > 0)
            _said = refused[0];
        else if (!_erasing)
            _said = "";

        _dirty = true;

        // Painted straight away and settled on release: the letters are what the
        // author is looking at, and waiting for the mouse to come up would be a
        // brush that lags a stroke behind.
        _field.QueueRedraw();
        _marks.QueueRedraw();
    }

    private (bool, string) Write(Vector2I cell)
    {
        string why;
        bool ok = _tab switch
        {
            MapEdit.Tab.Foundation =>
                _plotBrush ? _edit.Plot(cell, true, out why)
                : _water ? _edit.Water(cell, true, out why)
                : _edit.Floor(cell, _floor, out why),
            MapEdit.Tab.Cover => _edit.Over(cell, _cover, out why),
            _ => _rampBrush ? _edit.Ramp(cell, true, out why)
                            : _edit.Level(cell, _level, out why),
        };
        return (ok, why);
    }

    private void Fill()
    {
        if (!_edit.Inside(_under))
            return;
        _edit.Begin();
        IReadOnlyList<string> refused =
            _edit.Sweep(_edit.Region(_under), Write);
        _dirty = true;
        _said = refused.Count > 0 ? refused[0] : "filled";
        Settle();
    }

    private void PlaceHome()
    {
        if (!_edit.Inside(_under))
            return;
        _edit.Begin();
        int slot = _home < 0 ? _edit.Homes.Count : _home;
        _dirty = true;
        _said = _edit.Home(_under, slot, out string why)
            ? $"home {slot + 1} at ({_under.X},{_under.Y})" : why;
        Settle();
    }

    /// <summary>
    /// Save, and it builds first.
    ///
    /// <b>A draft that will not build is not written.</b> The folder is judged by
    /// the self test and read by every scene, so a board that refuses in it
    /// breaks the session rather than the drawing - which is why the refusal is
    /// shown here instead of on the next run.
    /// </summary>
    private bool Keep()
    {
        bool kept;
        try
        {
            string path = _edit.Save();
            _said = "saved " + Path.GetFileName(path);
            _dirty = false;
            kept = true;
        }
        catch (Exception e)
        {
            // Shown rather than only muttered on the readout: a save that did
            // not happen is the one message the author must not miss, and the
            // refusal names what to do about it.
            _said = "NOT saved: " + e.Message;
            Tell("Not saved.\n\n" + e.Message);
            kept = false;
        }
        Say();
        return kept;
    }

    // --- the readout ---------------------------------------------------------

    private void Say()
    {
        MapFile.Glyph glyph = _edit.GlyphAt(_under);
        string cell = _edit.Inside(_under)
            ? $"({_under.X},{_under.Y}) '{glyph.Letter}' "
              + $"{glyph.Floor.ToString().ToLowerInvariant()}"
              + (glyph.Over == Cover.None
                  ? "" : "/" + glyph.Over.ToString().ToLowerInvariant())
              + $" level {glyph.Level}"
              + (_edit.IsRamp(_under) ? " ramp" : "")
            : "off the grid";

        _title.Text = $"   {_edit.Name}{(_dirty ? " *" : "")}";
        _hud.Text = $"{_edit.Name}  {_edit.Columns}x{_edit.Rows}   {cell}\n"
                    + $"{Tool()}   brush {_radius}   {_edit.Depth} undo\n"
                    + (_said.Length > 0 ? _said : "");
        _report.Text = Report();
        // Last, because the panels read the board this has just described and
        // the right one rebuilds off it - see MapEditor.Panels.
        Refresh();
    }

    private string Tool() => _tab switch
    {
        MapEdit.Tab.Foundation =>
            "floor: " + (_plotBrush ? "on the board"
                : _water ? "water" : _floor.ToString().ToLowerInvariant()),
        MapEdit.Tab.Cover => "cover: " + _cover.ToString().ToLowerInvariant(),
        _ => _rampBrush ? "level: ramp" : $"level: {_level}",
    };

    /// <summary>
    /// The audit, live.
    ///
    /// <b>Reachability is asked of the field rather than recomputed.</b> It is
    /// the one rule that needs the floor, the flood, the cliffs and the ramp axis
    /// together, and <see cref="HexField.Passable"/> is where those four already
    /// meet - see <see cref="MapRules.Seen"/>.
    /// </summary>
    private string Report()
    {
        var lines = new List<string>();
        MapRules.Draft draft = _edit.Draft();
        int cells = draft.Cells().Count();
        lines.Add($"{cells} cells on the board, {_sites.Count} could carry a "
                  + $"ramp, {_sites.Count(kv => !_edit.IsRamp(kv.Key))} free");

        foreach (MapRules.Fault fault in _faults.Where(f => f.Fatal).Take(12))
            lines.Add("BAD " + fault.Text);
        foreach (MapRules.Fault fault in _faults.Where(f => !f.Fatal).Take(6))
            lines.Add("... " + fault.Text);
        if (_faults.Count(f => f.Fatal) > 12)
            lines.Add($"... and {_faults.Count(f => f.Fatal) - 12} more");

        // Only when the board builds: reachability off a field carrying dropped
        // ramps would be reachability on a board the author is not drawing.
        if (_faults.All(f => !f.Fatal))
        {
            foreach (Vector2I home in _edit.Homes)
            {
                HashSet<Vector2I> seen = MapRules.Seen(_field, home);
                int stranded = draft.Cells()
                    .Count(c => !draft.IsCliff(c) && !seen.Contains(c));
                lines.Add($"from ({home.X},{home.Y}): {seen.Count} reachable, "
                          + $"{stranded} stranded");
            }
        }
        else
        {
            lines.Add("(reach is not asked while the board has faults)");
        }
        return string.Join("\n", lines);
    }

    // --- the panels ----------------------------------------------------------

    private void Ui()
    {
        var layer = new CanvasLayer();
        AddChild(layer);

        // The bar first, so what is under it is the board rather than the
        // readout - see MapEditor.Menu.
        Menu(layer);
        // Then the two panels, and the readout between them - see
        // MapEditor.Panels. The board is drawn under all three and framed
        // between them by Home.
        Tools(layer);
        Inspect(layer);

        _hud = Text(LeftWide + 26.0f, 44.0f, 17);
        layer.AddChild(_hud);

        _report = new RichTextLabel
        {
            Position = new Vector2(LeftWide + 26.0f, 106.0f),
            CustomMinimumSize = new Vector2(520.0f, 420.0f),
            ScrollActive = false,
            BbcodeEnabled = false,
            // And it does not eat the board.
            //
            // <b>Godot gives Label mouse_filter Ignore in its own constructor
            // and leaves every other Control on the inherited Stop</b>, so this
            // was an invisible 520x420 button over the top left of the board:
            // every hex under the audit was unclickable, and nothing on screen
            // said why. The panels are Stop on purpose - a click that lands on
            // one must not paint the cell behind it - and the readout is the
            // opposite case, because there is nothing on it to click.
            //
            // Proved by --menu overlay, which synthesises a click at the middle
            // of this rectangle and prints the cell it picked. It printed
            // (-1,-1).
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _report.AddThemeColorOverride("default_color",
            new Color(0.86f, 0.92f, 1.0f));
        _report.AddThemeColorOverride("font_outline_color",
            new Color(0, 0, 0, 0.85f));
        _report.AddThemeConstantOverride("outline_size", 4);
        _report.AddThemeFontSizeOverride("normal_font_size", 13);
        layer.AddChild(_report);
    }

    private static Label Text(float x, float y, int size)
    {
        // Ignore said out loud rather than left to Label's own constructor,
        // which is where it comes from: the readout that did eat the board was
        // the one Control here that is not a Label - see Ui.
        var label = new Label
        {
            Position = new Vector2(x, y),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        label.AddThemeColorOverride("font_color", new Color(0.94f, 0.96f, 1.0f));
        label.AddThemeColorOverride("font_outline_color",
            new Color(0, 0, 0, 0.85f));
        label.AddThemeConstantOverride("outline_size", 5);
        label.AddThemeFontSizeOverride("font_size", size);
        return label;
    }

    /// <summary>
    /// Step the brush within the tab.
    ///
    /// <b>Down the same list the left panel lays out</b>, which is
    /// <see cref="Brushes"/>. The key and the palette were written twice for one
    /// commit and disagreed about where water was - the key skipped it, because
    /// water had been a switch on W since before there was a panel to put it on.
    ///
    /// <b>Shallow and deep are not in the list, and that is the format rather
    /// than an omission.</b> Water on a map is the flood letter, which is low
    /// ground with water standing in it - see <see cref="MapEdit.Floor"/>, and it
    /// is <c>W</c> there.
    /// </summary>
    private void Cycle()
    {
        IReadOnlyList<(string Name, Action Pick, Func<bool> On)> brushes =
            Brushes();
        int at = 0;
        for (int i = 0; i < brushes.Count; i++)
            if (brushes[i].On())
            {
                at = i;
                break;
            }
        brushes[(at + 1) % brushes.Count].Pick();
    }

    /// <summary>The two brushes that are a switch rather than a step: water and
    /// the board's edge on the floor tab, the ramp on the height tab. Separate
    /// because each is a statement of its own about a cell rather than another
    /// value of the same axis - the edge of the world most of all.</summary>
    /// <summary>
    /// Take up the ramp brush, or put it down - and taking it up turns the ramp
    /// sites on.
    ///
    /// <b>Because a ramp has no direction to author, only a cell.</b> Which edge
    /// a ramp climbs is derived: <see cref="MapRules.Above"/> wants exactly one
    /// neighbour a level up, and <see cref="MapRules.Footed"/> wants the
    /// opposite one on its own level, so a legal ramp cell has exactly one high
    /// edge and a cell with two is <c>ramp.bridge</c> - not a choice, a fault.
    /// The tile is drawn as one plane tilted about one edge
    /// (<see cref="HexField.TopCorners"/>), so this is geometry rather than
    /// bookkeeping: a cell rising towards two neighbours is not a surface.
    ///
    /// So the question the author actually has is "where can one go", not "which
    /// way should this one face" - and <see cref="MapRules.Sites"/> has answered
    /// it all along, behind a layer that was off by default. Now the brush turns
    /// it on: every cell that can take a ramp is painted, with a stem towards
    /// the edge it would climb, before anything is clicked.
    ///
    /// Putting the brush down leaves the layer up. The author may have asked for
    /// it themselves, and a checkbox that clears itself is a checkbox arguing.
    /// </summary>
    private void RampBrush(bool up)
    {
        _rampBrush = up;
        if (up)
            _layers |= Layers.Sites;
    }

    private void Toggle(bool first)
    {
        switch (_tab)
        {
            case MapEdit.Tab.Foundation when first:
                _water = !_water;
                _plotBrush = false;
                break;
            case MapEdit.Tab.Foundation:
                _plotBrush = !_plotBrush;
                _water = false;
                break;
            case MapEdit.Tab.Level when first:
                RampBrush(!_rampBrush);
                break;
        }
    }
}
