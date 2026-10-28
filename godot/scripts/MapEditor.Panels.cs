using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The two panels: the brush on the left, the cell under the cursor on the
/// right.
///
/// <b>They are two different questions and that is why they are two panels.</b>
/// The left one is "what am I painting with", which is one answer for the whole
/// board and belongs beside the tabs; the right one is "what is this cell", which
/// is a different answer for every hex and only exists once one is picked. A
/// single panel holding both would have to redraw the tool every time the pointer
/// moved.
///
/// <b>Neither panel is a second model.</b> Every control reads
/// <see cref="MapEdit"/> when it is built and writes through
/// <see cref="MapEdit"/>'s own guarded methods when it is used - so a refusal
/// reaches the readout the way a brush stroke's does, and a panel cannot put the
/// board into a state a key could not. The keys are still there and still the
/// only thing <see cref="MapEditor._UnhandledInput"/> listens to; the buttons
/// call the same code, and <see cref="Refresh"/> is what keeps the two agreeing.
///
/// <b>The right panel is rebuilt rather than updated, and only when the cell
/// changes shape.</b> What a cell offers depends on what it is - six edge boxes
/// and four dials on masonry, none of them on a wood - so keeping one set of
/// controls and hiding the wrong ones would be the panel holding a second copy of
/// the alphabet. The signature in <see cref="Shape"/> is what a rebuild is worth:
/// the cell, its letter, its ramp and whether it is a home. Turning one edge off
/// deliberately is not in it, because a rebuild under a checkbox being clicked is
/// a checkbox that flickers.
/// </summary>
public sealed partial class MapEditor
{
    // --- the left panel ------------------------------------------------------

    private GridContainer _tabs = null!;
    /// <summary>The bar's buttons, in <see cref="TabOrder"/>.</summary>
    private readonly List<Button> _tabButtons = new();
    private VBoxContainer _choices = null!;
    private Label _brushSays = null!;
    private readonly Dictionary<Instrument, Button> _toolButtons = new();
    private OptionButton _symmetryPick = null!;
    private readonly List<Button> _picks = new();
    private readonly Dictionary<Layers, CheckBox> _switches = new();

    /// <summary>Whether the panels are being filled in from the board rather
    /// than typed into, so that setting a control's value does not read as the
    /// author having set it. <see cref="MapEditor.Menu"/>'s <c>_quiet</c>, and
    /// the same trap: a value written into a control emits the same signal a
    /// click does.</summary>
    private bool _loading;

    /// <summary>The cell the right panel is showing. Set by a click rather than
    /// by the pointer: an inspector that followed the mouse could not be reached
    /// with the mouse.</summary>
    private Vector2I _picked = new(-1, -1);

    // the four tabs' width, unclipped: what the report and the readout start past
    private const float LeftWide = 282.0f;
    private const float RightWide = 254.0f;
    private const float PanelTop = 44.0f;

    private void Tools(CanvasLayer layer)
    {
        var frame = new PanelContainer
        {
            Position = new Vector2(12.0f, PanelTop),
            CustomMinimumSize = new Vector2(LeftWide, 0.0f),
        };
        layer.AddChild(frame);

        var box = new VBoxContainer();
        frame.AddChild(box);

        // The toolbar first: how, over every tab; the tabs under it say with
        // what (MapEditor.Tools).
        var tools = new GridContainer { Columns = 3 };
        var toolGroup = new ButtonGroup();
        foreach ((Instrument tool, string face) in new[]
                 {
                     (Instrument.Brush, "brush"), (Instrument.Line, "line"), (Instrument.Box, "box  U"),
                     (Instrument.Fill, "fill  G"), (Instrument.Select, "select  R"), (Instrument.Pick, "pick  I"),
                 })
        {
            Instrument which = tool;
            var button = new Button
            {
                Text = face, ToggleMode = true, ButtonGroup = toolGroup,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            };
            button.AddThemeFontSizeOverride("font_size", 13);
            button.Pressed += () =>
            {
                if (_loading)
                    return;
                Use(which);
                Say();
            };
            _toolButtons[which] = button;
            tools.AddChild(button);
        }
        box.AddChild(tools);

        // Seven tabs in two rows of toggles, none scrolled off: a TabBar is one
        // row, and seven names do not fit the panel's width in one (Look and
        // Roads, 2026-10-28). Foundation as Floor - the readout's own word.
        _tabs = new GridContainer { Columns = 4 };
        _tabs.AddThemeConstantOverride("h_separation", 2);
        _tabs.AddThemeConstantOverride("v_separation", 2);
        var group = new ButtonGroup();
        foreach (MapEdit.Tab tab in TabOrder)
        {
            MapEdit.Tab mine = tab;
            var button = new Button
            {
                Text = tab == MapEdit.Tab.Foundation ? "Floor" : tab.ToString(),
                ToggleMode = true, ButtonGroup = group, ButtonPressed = tab == _tab,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                FocusMode = Control.FocusModeEnum.None,
            };
            button.AddThemeFontSizeOverride("font_size", 12);
            button.Pressed += () =>
            {
                if (_loading)
                    return;
                _tab = mine;
                Refresh();
                Say();
            };
            _tabButtons.Add(button);
            _tabs.AddChild(button);
        }
        box.AddChild(_tabs);

        _choices = new VBoxContainer();
        box.AddChild(_choices);

        box.AddChild(new HSeparator());

        var brush = new HBoxContainer();
        brush.AddChild(Step("-", () => _radius = Math.Max(0, _radius - 1)));
        _brushSays = new Label
        {
            Text = "brush 0",
            HorizontalAlignment = HorizontalAlignment.Center,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        brush.AddChild(_brushSays);
        brush.AddChild(Step("+", () => _radius = Math.Min(6, _radius + 1)));
        box.AddChild(brush);
        // the symmetric brush, P on the keys (MapEditor.Symmetry)
        // not as wide as its longest item, which pushed the panel over the board
        _symmetryPick = new OptionButton { FitToLongestItem = false, ClipText = true };
        _symmetryPick.AddThemeFontSizeOverride("font_size", 14);
        foreach (MapEdit.Symmetry how in Enum.GetValues<MapEdit.Symmetry>())
            _symmetryPick.AddItem("symmetry: " + Named(how) + (how == MapEdit.Symmetry.None ? "   P" : ""), (int)how);
        _symmetryPick.ItemSelected += index =>
        {
            if (_loading)
                return;
            SetSymmetry((MapEdit.Symmetry)_symmetryPick.GetItemId((int)index));
        };
        box.AddChild(_symmetryPick);

        box.AddChild(new HSeparator());

        // The overlays, as the flags they are - see MapEditor.Layers. No ramps
        // and no ramp sites: the relief builds neither.
        foreach ((Layers flag, string name) in new[]
                 {
                     (Layers.Levels, "levels  L"),
                     (Layers.Flood, "flood  B"),
                     (Layers.Homes, "homes"),
                 })
        {
            Layers which = flag;
            var check = new CheckBox
            {
                Text = name, ButtonPressed = _layers.HasFlag(which),
            };
            check.Toggled += on =>
            {
                if (_loading)
                    return;
                _layers = on ? _layers | which : _layers & ~which;
                _field.QueueRedraw();
                _marks.QueueRedraw();
                Say();
            };
            check.AddThemeFontSizeOverride("font_size", 13);
            _switches[which] = check;
            box.AddChild(check);
        }

        box.AddChild(new HSeparator());

        var keys = new Label
        {
            // Off the one table of keys (MapEditor.Help), as the F1 sheet is.
            Text = Legend(),
        };
        keys.AddThemeFontSizeOverride("font_size", 12);
        box.AddChild(keys);

        Choices();
    }

    private static Button Step(string face, Action did)
    {
        var button = new Button
        {
            Text = face, CustomMinimumSize = new Vector2(28.0f, 0.0f),
        };
        button.Pressed += () => did();
        return button;
    }

    /// <summary>
    /// What the active tab paints with.
    ///
    /// <b>One list, read by the buttons and by <see cref="Cycle"/>'s key.</b>
    /// The palette and the Q key were two hand-written orders of the same
    /// brushes for one commit, and the two disagreed about where water was.
    /// </summary>
    private IReadOnlyList<(string Name, Action Pick, Func<bool> On)> Brushes()
    {
        switch (_tab)
        {
            case MapEdit.Tab.Foundation:
                return new (string, Action, Func<bool>)[]
                {
                    ("solid", () => Floor(Foundation.Solid),
                        () => Plain() && _floor == Foundation.Solid),
                    ("sand", () => Floor(Foundation.Sand),
                        () => Plain() && _floor == Foundation.Sand),
                    ("mud", () => Floor(Foundation.Mud),
                        () => Plain() && _floor == Foundation.Mud),
                    // The height is in the name because the cell it paints is
                    // two levels up and declared unclimbable, and the Level tab
                    // refuses to raise anything that far on purpose - a rock is
                    // a floor, a height and a declaration at once, so it is
                    // painted rather than raised into. See MapEdit.Level, whose
                    // refusal says the same thing to whoever tries the other
                    // tab first.
                    ("rock  +2, a cliff", () => Floor(Foundation.Rock),
                        () => Plain() && _floor == Foundation.Rock),
                    // Water is a switch rather than a floor - the alphabet's
                    // water is low ground with water standing in it, and a floor
                    // made of water has no letter. See MapEdit.Water. The level
                    // is in the name for the reason the rock's is.
                    ("water  -1", () => { _water = true; _plotBrush = false; },
                        () => _water),
                    // Deep water is a floor, unlike the ford: the letter names
                    // Foundation.Deep, and carries the level for the ford's
                    // reason. See BoardMap.DeepWet.
                    ("deep water  -1, to the brim", () => Floor(Foundation.Deep),
                        () => Plain() && _floor == Foundation.Deep),
                    // The edge of the world, both ways. Off the board was
                    // reachable only from the right panel: the eraser means
                    // plain ground on this tab, so there was no brush for it.
                    ("put on the board",
                        () => { _plotBrush = true; _plotOn = true; _water = false; },
                        () => _plotBrush && _plotOn),
                    ("take off the board",
                        () => { _plotBrush = true; _plotOn = false; _water = false; },
                        () => _plotBrush && !_plotOn),
                };
            case MapEdit.Tab.Look:
                // the looks (format 3): the ground's, each only on the floor it
                // is for and saying so on any other; then the woods' species,
                // which a cell with no wood keeps until it is planted
                return TankSpriteTest.Soil.Looks.Select(l =>
                    ($"{l.Name}  ({TankSpriteTest.Soil.FloorOf(l.Letter)!.Value.ToString().ToLowerInvariant()})",
                     (Action)(() => { _look = l.Letter; _speciesBrush = null; }),
                     (Func<bool>)(() => _speciesBrush is null && _look == l.Letter)))
                    .Concat(Species.All.Select(sp =>
                        ($"trees: {sp.Key}",
                         (Action)(() => _speciesBrush = sp.Letter),
                         (Func<bool>)(() => _speciesBrush == sp.Letter))))
                    .ToList();
            case MapEdit.Tab.Roads:
                // the roads (format 3): points on hexes, the road laid between
                // them; a right click cuts
                return Enum.GetValues<RoadType>().Select(t =>
                    (Road.Say(t),
                     (Action)(() => _roadBrush = t),
                     (Func<bool>)(() => _roadBrush == t))).ToList();
            case MapEdit.Tab.Cover:
                var covers = Enum.GetValues<Cover>().Select(over =>
                    (over.ToString().ToLowerInvariant(),
                     (Action)(() => { _cover = over; _bunker = false; }),
                     (Func<bool>)(() => _cover == over && !_bunker))).ToList();
                // The capon, a brush of its own rather than a dial on the
                // walls: it is walls on the cell and the block saying concrete,
                // and the author means one thing by it. W turns the slit.
                covers.Add(($"bunker  slit {_facing}  (W turns)",
                    () => { _cover = Cover.Walls; _bunker = true; },
                    () => _bunker));
                return covers;
            case MapEdit.Tab.Presets:
                // off editor_presets.json (MapEditor.Tools); its own words beside each
                if (_presets.Count == 0)
                    return new (string, Action, Func<bool>)[]
                    {
                        ("(no presets - " + (_presetsSay.Length > 0 ? _presetsSay : "the file is empty") + ")",
                            () => { }, () => false),
                    };
                return _presets.Select((p, i) => (p.Name,
                    (Action)(() => _preset = i), (Func<bool>)(() => _preset == i))).ToList();
            case MapEdit.Tab.Units:
                // A side's five, then the other's: Lineup.Pairs' order, the
                // roster's, so Q walks green LT to brown HM
                var units = new List<(string, Action, Func<bool>)>();
                foreach (string side in Parking.Sides)
                    foreach ((string _, string cls, string name) in Lineup.Pairs)
                    {
                        string s = side, c = cls;
                        units.Add(($"{s} {c[..2]}  {name}",
                            () => { _unitSide = s; _unitClass = c; },
                            () => _unitSide == s && _unitClass == c));
                    }
                return units;
            default:
                var levels = new List<(string, Action, Func<bool>)>();
                foreach (int level in new[] { 1, 0, -1 })
                {
                    int want = level;
                    levels.Add(($"level {want:+0;-0;0}",
                        () => { _level = want; _rampBrush = false; },
                        () => !_rampBrush && _level == want));
                }
                // No ramp brush: the board is a relief, and its levels are
                // joined by slopes from any side (MapEditor.Settle).
                return levels;
        }
    }

    private bool Plain() => !_water && !_plotBrush;

    private void Floor(Foundation want)
    {
        _floor = want;
        _water = false;
        _plotBrush = false;
    }

    /// <summary>Which tab the palette was laid out for, so that
    /// <see cref="Refresh"/> can ask the question it means. Null before the
    /// first lay-out.</summary>
    private MapEdit.Tab? _laid;

    /// <summary>Lay out the buttons for the tab that is up.</summary>
    private void Choices()
    {
        // the presets file read again each time its tab is laid out, so an
        // edit to it needs no build (MapEditor.Tools)
        if (_tab == MapEdit.Tab.Presets)
            ReadPresets();
        _laid = _tab;
        foreach (Node was in _choices.GetChildren())
        {
            _choices.RemoveChild(was);
            was.QueueFree();
        }
        _picks.Clear();
        var group = new ButtonGroup();
        foreach ((string name, Action pick, Func<bool> on) in Brushes())
        {
            var button = new Button
            {
                Text = name,
                ToggleMode = true,
                ButtonGroup = group,
                ButtonPressed = on(),
                Alignment = HorizontalAlignment.Left,
                // What the brush lays, small - MapEditor.Icons.
                Icon = IconFor(_tab, name),
                ExpandIcon = false,
            };
            button.AddThemeFontSizeOverride("font_size", 14);
            // The models' icons are rendered larger than the drawn ones; all of
            // them stand the same width on the panel.
            button.AddThemeConstantOverride("icon_max_width", IconSide);
            Action chose = pick;
            button.Pressed += () =>
            {
                if (_loading)
                    return;
                chose();
                // a brush picked is in hand: the select tool and the
                // eyedropper give way, the line, box and fill keep it
                if (_instrument is Instrument.Select or Instrument.Pick)
                    _instrument = Instrument.Brush;
                Say();
            };
            _picks.Add(button);
            _choices.AddChild(button);
        }
    }

    /// <summary>
    /// Put the panels back in step with the board.
    ///
    /// Called from <see cref="Say"/>, which every path ends in - a key, a click,
    /// an undo, a board being opened - so there is no path that changes the
    /// brush and leaves the panel showing the old one.
    /// </summary>
    private void Refresh()
    {
        _loading = true;
        // The bar is told where the tab is either way; setting it to what it
        // already says emits nothing (and _loading holds the press off).
        for (int i = 0; i < _tabButtons.Count; i++)
            _tabButtons[i].SetPressedNoSignal(TabOrder[i] == _tab);
        IReadOnlyList<(string Name, Action Pick, Func<bool> On)> brushes =
            Brushes();
        // Rebuilt when the palette was laid out for another tab - the question
        // this means - and not when the BAR disagrees with _tab, which is what
        // it used to ask.
        //
        // <b>The two differ exactly on the path the author takes.</b> A click on
        // the tab moves the bar first and the signal arrives with the two
        // already agreeing, so nothing was rebuilt; a key moves _tab first, so
        // it was. And the count fallback hid it: Foundation and Cover both hold
        // five brushes, so the palette kept the old five with the new tab's name
        // over them. Level holds four and rebuilt by luck, which is how this
        // survived a capture of all three.
        if (_laid != _tab || _picks.Count != brushes.Count)
            Choices();
        else
            for (int i = 0; i < _picks.Count; i++)
                _picks[i].ButtonPressed = brushes[i].On();
        _brushSays.Text = $"brush {_radius}";
        foreach ((Instrument tool, Button button) in _toolButtons)
            button.ButtonPressed = tool == _instrument;
        _symmetryPick.Selected = _symmetryPick.GetItemIndex((int)_symmetry);
        foreach ((Layers which, CheckBox check) in _switches)
            check.ButtonPressed = _layers.HasFlag(which);
        _loading = false;
        Fit();
    }

    // --- the right panel -----------------------------------------------------

    private PanelContainer _inspector = null!;
    private VBoxContainer _inside = null!;
    private string _shape = "";

    private void Inspect(CanvasLayer layer)
    {
        _inspector = new PanelContainer
        {
            CustomMinimumSize = new Vector2(RightWide, 0.0f),
        };
        _inspector.Position = new Vector2(
            GetViewportRect().Size.X - RightWide - 12.0f, PanelTop);
        layer.AddChild(_inspector);

        _inside = new VBoxContainer();
        _inspector.AddChild(_inside);
    }

    /// <summary>What the right panel is built for. A rebuild happens when this
    /// changes and not otherwise - see the class remarks.</summary>
    private string Shape() =>
        !_edit.Inside(_picked)
            ? "none"
            : $"{_picked.X},{_picked.Y} {_edit.LetterAt(_picked)} "
              + $"{(_edit.IsRamp(_picked) ? "r" : "-")} "
              + $"{_edit.SlotAt(_picked)}";

    private void Fit()
    {
        string want = Shape();
        if (want == _shape)
            return;
        _shape = want;
        foreach (Node was in _inside.GetChildren())
        {
            _inside.RemoveChild(was);
            was.QueueFree();
        }
        Build(_inside);
    }

    private void Build(VBoxContainer into)
    {
        if (!_edit.Inside(_picked))
        {
            into.AddChild(Heading("no cell picked"));
            var how = new Label
            {
                Text = "click a hex to paint it\nand to read it here",
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                CustomMinimumSize = new Vector2(RightWide - 24.0f, 0.0f),
            };
            how.AddThemeFontSizeOverride("font_size", 12);
            into.AddChild(how);
            return;
        }

        MapFile.Glyph glyph = _edit.GlyphAt(_picked);
        into.AddChild(Heading($"({_picked.X},{_picked.Y})   '{glyph.Letter}'"));

        // --- foundation ------------------------------------------------------
        into.AddChild(Heading("Foundation"));
        if (!_edit.OnBoard(_picked))
        {
            into.AddChild(new Label { Text = "off the board" });
            into.AddChild(Does("put it on the board",
                () => _edit.Plot(_picked, true, out string why) ? "" : why));
            return;
        }
        var floors = new OptionButton();
        Foundation[] laying =
            { Foundation.Solid, Foundation.Sand, Foundation.Mud, Foundation.Deep, Foundation.Rock };
        foreach (Foundation floor in laying)
            floors.AddItem(floor.ToString().ToLowerInvariant());
        floors.Selected = Math.Max(0, Array.IndexOf(laying, glyph.Floor));
        floors.ItemSelected += index => Wrote(() =>
            _edit.Floor(_picked, laying[(int)index], out string why) ? "" : why);
        into.AddChild(Field("floor", floors));

        into.AddChild(Ticked("water", glyph.Water,
            wet => _edit.Water(_picked, wet, out string why) ? "" : why));
        if (glyph.Floor == Foundation.Rock)
            Stone(into);
        else if (!glyph.Water && TankSpriteTest.Soil.Today(glyph.Floor) != TankSpriteTest.Soil.As)
            Worn(into, glyph.Floor);
        Grown(into);
        Paved(into);
        into.AddChild(Does("take it off the board",
            () => _edit.Plot(_picked, false, out string why) ? "" : why));

        // --- cover -----------------------------------------------------------
        into.AddChild(Heading("Cover"));
        var covers = new OptionButton();
        Cover[] over = Enum.GetValues<Cover>();
        foreach (Cover kind in over)
            covers.AddItem(kind.ToString().ToLowerInvariant());
        covers.Selected = Math.Max(0, Array.IndexOf(over, glyph.Over));
        covers.ItemSelected += index => Wrote(() =>
            _edit.Over(_picked, over[(int)index], out string why) ? "" : why);
        into.AddChild(Field("stands", covers));

        if (_edit.MasonryAt(_picked) is { } wall)
            Bricks(into, wall);

        // --- level -----------------------------------------------------------
        into.AddChild(Heading("Level"));
        var level = new SpinBox { MinValue = -1, MaxValue = 1, Step = 1 };
        level.Value = glyph.Level;
        level.ValueChanged += value => Wrote(() =>
            _edit.Level(_picked, (int)value, out string why) ? "" : why);
        into.AddChild(Field("level", level));
        // A ramp the letters still carry is kept and not built (Settle).
        if (_edit.IsRamp(_picked))
            into.AddChild(Note("ramp in the file - the relief does not build it"));

        // --- home ------------------------------------------------------------
        into.AddChild(Heading("Home"));
        int slot = _edit.SlotAt(_picked);
        if (slot >= 0)
        {
            into.AddChild(Note($"home {slot + 1} of {_edit.Homes.Count}"));
            Crew(into, slot);
            Team(into, slot);
            Face(into, slot);
            into.AddChild(Does("drop this home",
                () => _edit.DropHome(slot, out string why) ? "" : why));
        }
        else
        {
            into.AddChild(Does("make this home "
                               + (_edit.Homes.Count + 1),
                () => _edit.Home(_picked, _edit.Homes.Count, out string why)
                    ? "" : why));
        }
        Board(into);
    }

    /// <summary>The roads through the cell (format 3's <c>roads</c>): each its
    /// look, and a button that cuts them all here (<see cref="MapEdit.Cut"/>).</summary>
    private void Paved(VBoxContainer into)
    {
        List<int> roads = _edit.RoadsAt(_picked);
        if (roads.Count == 0)
            return;
        RoadType[] types = Enum.GetValues<RoadType>();
        foreach (int k in roads)
        {
            int road = k;
            RoadPath path = _edit.Roads[road];
            var pick = new OptionButton();
            foreach (RoadType t in types)
                pick.AddItem(Road.Say(t));
            pick.Selected = Array.IndexOf(types, path.Type);
            pick.ItemSelected += index =>
            {
                if (!_loading)
                    Wrote(() =>
                    {
                        _edit.Retype(road, types[(int)index]);
                        return "";
                    });
            };
            into.AddChild(Field($"road {road + 1}", pick));
            into.AddChild(Note($"{path.Cells.Count} cells, ({path.Cells[0].X},{path.Cells[0].Y}) to "
                               + $"({path.Cells[^1].X},{path.Cells[^1].Y})"));
        }
        into.AddChild(Does("cut the roads here", () =>
        {
            _edit.Cut(_picked);
            return "";
        }));
    }

    /// <summary>The cell's own look (format 3's <c>soil.cells</c>): "as the
    /// board" first, which is silence, then the looks of its floor.</summary>
    private void Worn(VBoxContainer into, Foundation floor)
    {
        char[] looks = TankSpriteTest.Soil.Looks.Where(l => TankSpriteTest.Soil.FloorOf(l.Letter) == floor)
            .Select(l => l.Letter).ToArray();
        var pick = new OptionButton();
        pick.AddItem($"as the board ({TankSpriteTest.Soil.Name(_edit.LookAt(_picked))})");
        foreach (char l in looks)
            pick.AddItem(TankSpriteTest.Soil.Name(l));
        char own = _edit.SoilAt(_picked);
        pick.Selected = own == TankSpriteTest.Soil.As ? 0 : Array.IndexOf(looks, own) + 1;
        pick.ItemSelected += index =>
        {
            if (!_loading)
                Wrote(() => _edit.Wear(_picked, index == 0 ? TankSpriteTest.Soil.As : looks[(int)index - 1],
                                       out string why) ? "" : why);
        };
        into.AddChild(Field("look", pick));
    }

    /// <summary>The cell's own species (format 3's <c>trees</c>): "as the
    /// board" first, which is the mix, then the species. Offered on any cell -
    /// one with no wood keeps it until it is planted.</summary>
    private void Grown(VBoxContainer into)
    {
        var pick = new OptionButton();
        pick.AddItem("as the board");
        foreach ((_, string key, _, _) in Species.All)
            pick.AddItem(key);
        char own = _edit.TreeAt(_picked);
        pick.Selected = own == Species.As ? 0 : Species.Of(own) + 1;
        pick.ItemSelected += index =>
        {
            if (!_loading)
                Wrote(() => _edit.Plant(_picked, index == 0 ? Species.As : Species.All[(int)index - 1].Letter,
                                        out string why) ? "" : why);
        };
        into.AddChild(Field("trees", pick));
    }

    /// <summary>
    /// The masonry group: the six edges, then the four numbers.
    ///
    /// <b>The dials are a tick and a number, because null is silence.</b> A
    /// wall that did not name its courses takes whatever the wall being laid
    /// uses - see <see cref="TankSpriteTest.Masonry"/> - and a spin box alone
    /// cannot say that, so it would write today's default into the file the first
    /// time anybody saved a board with a wall on it.
    /// </summary>
    private void Bricks(VBoxContainer into, Masonry wall)
    {
        // Brick or the concrete capon, and the capon's slit - MapEdit.Shelter.
        var kind = new OptionButton();
        kind.AddItem("brick wall");
        kind.AddItem("bunker (capon)");
        kind.Selected = wall.Kind == Masonry.Sort.Capon ? 1 : 0;
        kind.ItemSelected += index => Wrote(() =>
            _edit.Shelter(_picked, index == 1, wall.Facing ?? _facing, out string why) ? "" : why);
        into.AddChild(Field("kind", kind));
        if (wall.Kind == Masonry.Sort.Capon)
        {
            var slit = new OptionButton();
            foreach (int heading in Masonry.Headings)
                slit.AddItem($"{heading}");
            slit.Selected = Math.Max(0, Array.IndexOf(Masonry.Headings, wall.Facing ?? 270));
            slit.ItemSelected += index => Wrote(() =>
                _edit.Shelter(_picked, true, Masonry.Headings[(int)index], out string why) ? "" : why);
            into.AddChild(Field("slit faces", slit));
            return;
        }
        into.AddChild(Note("edges the wall covers"));
        var grid = new GridContainer { Columns = 3 };
        foreach (int heading in Masonry.Headings)
        {
            int side = heading;
            var check = new CheckBox
            {
                Text = $"{side}", ButtonPressed = wall.Stands(side),
            };
            check.AddThemeFontSizeOverride("font_size", 12);
            check.Toggled += on =>
            {
                if (_loading)
                    return;
                _edit.Begin();
                bool ok = _edit.Edge(_picked, side, on, out string why);
                if (!ok)
                {
                    // Put back rather than left showing what did not happen.
                    _loading = true;
                    check.ButtonPressed = !on;
                    _loading = false;
                }
                Wrote(() => ok ? "" : why, began: true);
            };
            grid.AddChild(check);
        }
        into.AddChild(grid);
        into.AddChild(Note(wall.Say()));

        foreach (Masonry.Dial dial in Masonry.Dials)
        {
            Masonry.Dial which = dial;
            (int low, int high) = Masonry.Range(which);
            // A seed is a name rather than a size, so its box is given the range
            // a spin box can actually be dragged through instead of the whole of
            // int - which is what Range says about it in words.
            if (which == Masonry.Dial.Seed)
                (low, high) = (0, 9999);
            var row = new HBoxContainer();
            var own = new CheckBox
            {
                Text = which.ToString().ToLowerInvariant(),
                ButtonPressed = wall.Of(which) is not null,
                CustomMinimumSize = new Vector2(94.0f, 0.0f),
            };
            own.AddThemeFontSizeOverride("font_size", 12);
            var box = new SpinBox
            {
                MinValue = low, MaxValue = high, Step = 1,
                // Silence's own figure and not the floor of the range - see
                // Masonry.Silence, which carries what the floor cost.
                Value = wall.Of(which) ?? Masonry.Silence(which),
                Editable = wall.Of(which) is not null,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            };
            own.Toggled += on =>
            {
                if (_loading)
                    return;
                box.Editable = on;
                Wrote(() => _edit.Shape(_picked, which,
                    on ? (int)box.Value : null, out string why) ? "" : why);
            };
            box.ValueChanged += value =>
            {
                if (_loading || !own.ButtonPressed)
                    return;
                Wrote(() => _edit.Shape(_picked, which, (int)value,
                    out string why) ? "" : why);
            };
            row.AddChild(own);
            row.AddChild(box);
            into.AddChild(row);
        }
    }

    /// <summary>
    /// The rock group: type, palette, preset and seed of the whole rock the
    /// cell is part of (format 3's <c>rocks</c>, docs/rocks-plan.md С5).
    ///
    /// <b>"crag" and "the type's" are the first entries and they are
    /// silence</b>, as "anybody" is for a home's class: a rock nobody named is
    /// the crag a bare letter always was, and naming one is the thing you go
    /// and pick. The preset and the seed are a tick and a number, as a wall's
    /// dials are, for the same reason.
    /// </summary>
    private void Stone(VBoxContainer into)
    {
        RockNote note = _edit.RockAt(_picked) ?? new RockNote();
        IReadOnlyList<Vector2I> rock = _edit.RockOf(_picked);
        into.AddChild(Note($"rock of {rock.Count} cell{(rock.Count == 1 ? "" : "s")}, "
                           + $"from ({rock[0].X},{rock[0].Y}) - {note.Look}"));
        void Put(RockNote next) =>
            Wrote(() => _edit.Note(_picked, next, out string why) ? "" : why);

        RockType[] types = Enum.GetValues<RockType>();
        var type = new OptionButton();
        foreach (RockType t in types)
            type.AddItem(RockNote.Say(t));
        type.Selected = Array.IndexOf(types, note.Type ?? RockType.Crag);
        type.ItemSelected += index =>
        {
            if (!_loading)
                Put(note with { Type = index == 0 ? null : types[(int)index] });
        };
        into.AddChild(Field("rock", type));

        string[] names = RockPalette.Names.ToArray();
        var palette = new OptionButton();
        palette.AddItem($"the type's ({RockPalette.For(note.Type ?? RockType.Crag).Name})");
        foreach (string name in names)
            palette.AddItem(name);
        palette.Selected = note.Palette is { } p ? Array.IndexOf(names, p) + 1 : 0;
        palette.ItemSelected += index =>
        {
            if (!_loading)
                Put(note with { Palette = index == 0 ? null : names[(int)index - 1] });
        };
        into.AddChild(Field("palette", palette));

        foreach (bool seed in new[] { false, true })
        {
            bool isSeed = seed;
            int? said = isSeed ? note.Seed : note.Preset;
            var row = new HBoxContainer();
            var own = new CheckBox
            {
                Text = isSeed ? "seed" : "preset",
                ButtonPressed = said is not null,
                CustomMinimumSize = new Vector2(94.0f, 0.0f),
            };
            own.AddThemeFontSizeOverride("font_size", 12);
            var box = new SpinBox
            {
                MinValue = isSeed ? 0 : 1, MaxValue = isSeed ? 9999 : RockNote.Presets, Step = 1,
                Value = said ?? (isSeed ? 0 : 1),
                Editable = said is not null,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            };
            RockNote With(int? v) => isSeed ? note with { Seed = v } : note with { Preset = v };
            own.Toggled += on =>
            {
                if (_loading)
                    return;
                box.Editable = on;
                Put(With(on ? (int)box.Value : null));
            };
            box.ValueChanged += value =>
            {
                if (!_loading && own.ButtonPressed)
                    Put(With((int)value));
            };
            row.AddChild(own);
            row.AddChild(box);
            into.AddChild(row);
        }
        if (note.Type == RockType.Mesa && note.Look.Preset == 2 && rock.Count < RockGroup.BenchCells)
            into.AddChild(Note($"preset 2 has a ledge, and a rock under {RockGroup.BenchCells} "
                               + "cells is laid without it"));
    }

    /// <summary>
    /// The board group, under every cell's: what is the whole board's and not
    /// the cell's - the look each floor wears (format 3's <c>soil.base</c>) and
    /// the climate (docs/map-format3-plan.md, Ф2 and Ф4). "today's" is the
    /// first entry and it is silence: grass, rippled sand, puddled mud.
    /// </summary>
    private void Board(VBoxContainer into)
    {
        into.AddChild(Heading("Board"));
        foreach ((Foundation floor, string key) in TankSpriteTest.Soil.Floors)
        {
            Foundation laid = floor;
            char[] looks = TankSpriteTest.Soil.Looks.Where(l => TankSpriteTest.Soil.FloorOf(l.Letter) == laid)
                .Select(l => l.Letter).ToArray();
            var pick = new OptionButton();
            foreach (char l in looks)
                pick.AddItem(l == TankSpriteTest.Soil.Today(laid) ? $"{TankSpriteTest.Soil.Name(l)} (today's)"
                                                                 : TankSpriteTest.Soil.Name(l));
            pick.Selected = Math.Max(0, Array.IndexOf(looks,
                _edit.SoilBase.TryGetValue(laid, out char now) ? now : TankSpriteTest.Soil.Today(laid)));
            pick.ItemSelected += index =>
            {
                if (_loading)
                    return;
                Wrote(() =>
                {
                    _edit.Lay(laid, looks[(int)index]);
                    return "";
                });
            };
            into.AddChild(Field(key, pick));
        }
        // the wood (format 3): a weight a species, nought for none; all
        // nought is oaks, as a board that never said
        into.AddChild(Note("wood: a weight a species, 0 for none - all 0 is oaks"));
        foreach ((char letter, string key, _, _) in Species.All)
        {
            char sp = letter;
            var weight = new SpinBox
            {
                MinValue = 0, MaxValue = 9, Step = 1,
                Value = _edit.Wood.TryGetValue(sp, out int w) ? w : 0,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            };
            weight.ValueChanged += value =>
            {
                if (!_loading)
                    Wrote(() =>
                    {
                        _edit.Weigh(sp, (int)value);
                        return "";
                    });
            };
            into.AddChild(Field(key, weight));
        }
        // the climate: one wind and one ice a board, the yardangs' and the
        // dunes' wind and the moutonnees' ice
        into.AddChild(Note("climate: degrees on the ground, 0 along +X"));
        foreach (bool ice in new[] { false, true })
        {
            bool isIce = ice;
            float? said = isIce ? _edit.Climate.SaidIce : _edit.Climate.SaidWind;
            var row = new HBoxContainer();
            var own = new CheckBox
            {
                Text = isIce ? "ice" : "wind",
                ButtonPressed = said is not null,
                CustomMinimumSize = new Vector2(94.0f, 0.0f),
            };
            own.AddThemeFontSizeOverride("font_size", 12);
            var box = new SpinBox
            {
                MinValue = -180, MaxValue = 180, Step = 5,
                Value = said ?? (isIce ? RockClimate.DefaultIce : RockClimate.DefaultWind),
                Editable = said is not null,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            };
            void Blow(float? v) => Wrote(() =>
            {
                _edit.Climate = isIce ? _edit.Climate with { SaidIce = v }
                                      : _edit.Climate with { SaidWind = v };
                return "";
            });
            own.Toggled += on =>
            {
                if (_loading)
                    return;
                box.Editable = on;
                Blow(on ? (float)box.Value : null);
            };
            box.ValueChanged += value =>
            {
                if (!_loading && own.ButtonPressed)
                    Blow((float)value);
            };
            row.AddChild(own);
            row.AddChild(box);
            into.AddChild(row);
        }
    }

    // --- the small parts -----------------------------------------------------

    private static Label Heading(string text)
    {
        var label = new Label { Text = text };
        label.AddThemeFontSizeOverride("font_size", 15);
        label.AddThemeColorOverride("font_color", new Color(0.66f, 0.86f, 1.0f));
        return label;
    }

    private static Label Note(string text)
    {
        var label = new Label
        {
            Text = text,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(RightWide - 28.0f, 0.0f),
        };
        label.AddThemeFontSizeOverride("font_size", 12);
        return label;
    }

    /// <summary>
    /// Which class a parking asks for.
    ///
    /// <b>"anybody" is the first entry and it is not a class.</b> A parking that
    /// names nobody takes whoever the load order has left - see
    /// <see cref="Parking"/> - and that is what almost every home on almost
    /// every board wants; naming one is the exception, so the exception is the
    /// thing you have to go and pick.
    ///
    /// The refusal comes back from <see cref="MapEdit.Crew"/> - a class this
    /// build has not got, or one another home already asked for - and the
    /// dropdown is put back to what it was, the way the edge boxes are.
    /// </summary>
    private void Crew(VBoxContainer into, int slot)
    {
        string? asked = _edit.Homes[slot].Class;
        var pick = new OptionButton();
        pick.AddItem("anybody");
        foreach (string tag in MovementProfile.Tags)
            pick.AddItem(tag);
        int at = asked is null
            ? 0 : Array.FindIndex(MovementProfile.Tags,
                      t => string.Equals(t, asked,
                          StringComparison.OrdinalIgnoreCase)) + 1;
        pick.Selected = Math.Max(0, at);
        int was = pick.Selected;
        pick.ItemSelected += index =>
        {
            if (_loading)
                return;
            string? tag = index == 0
                ? null : MovementProfile.Tags[(int)index - 1];
            bool ok = false;
            Wrote(() =>
            {
                ok = _edit.Crew(slot, tag, out string why);
                return ok ? "" : why;
            });
            if (ok)
            {
                was = (int)index;
                return;
            }
            _loading = true;
            pick.Selected = was;
            _loading = false;
        };
        into.AddChild(Field("class", pick));
    }

    /// <summary>The edges a home may face, in the panel's words: the board as
    /// it opens, before a turn.</summary>
    private static readonly (int Heading, string Name)[] Facings =
    {
        (90, "90 up"), (30, "30 up-right"), (330, "330 down-right"),
        (270, "270 down"), (210, "210 down-left"), (150, "150 up-left"),
    };

    /// <summary>
    /// Which side a parking is on in a match. "by order" first, Crew's reason:
    /// a parking that says nothing takes the side its place in the list gives
    /// it, which is what every board did before sides.
    /// </summary>
    private void Team(VBoxContainer into, int slot)
    {
        string? side = _edit.Homes[slot].Side;
        var pick = new OptionButton();
        // what its order would give it, the others as they are
        var probe = _edit.Homes.ToList();
        probe[slot] = probe[slot] with { Side = null };
        pick.AddItem($"by order ({Parking.SidesOf(probe)[slot]})");
        foreach (string s in Parking.Sides)
            pick.AddItem(s);
        pick.Selected = side is null ? 0 : Array.IndexOf(Parking.Sides, side) + 1;
        Pick(pick, index => _edit.Team(slot, index == 0 ? null : Parking.Sides[index - 1], out string why) ? "" : why);
        into.AddChild(Field("side", pick));
    }

    /// <summary>The edge a parking's tank starts facing; "across" is its side's
    /// way over the board.</summary>
    private void Face(VBoxContainer into, int slot)
    {
        int? facing = _edit.Homes[slot].Facing;
        var pick = new OptionButton();
        pick.AddItem("across the board");
        foreach ((int _, string name) in Facings)
            pick.AddItem(name);
        pick.Selected = facing is int f ? Array.FindIndex(Facings, x => x.Heading == f) + 1 : 0;
        Pick(pick, index => _edit.Face(slot, index == 0 ? null : Facings[index - 1].Heading, out string why) ? "" : why);
        into.AddChild(Field("facing", pick));
    }

    /// <summary>A dropdown that writes through <paramref name="write"/> as one
    /// stroke and goes back to what it was on a refusal - Crew's way.</summary>
    private void Pick(OptionButton pick, Func<int, string> write)
    {
        int was = pick.Selected;
        pick.ItemSelected += index =>
        {
            if (_loading)
                return;
            bool ok = false;
            Wrote(() =>
            {
                string why = write((int)index);
                ok = why.Length == 0;
                return why;
            });
            if (ok)
            {
                was = (int)index;
                return;
            }
            _loading = true;
            pick.Selected = was;
            _loading = false;
        };
    }

    /// <summary>A label and a control, the right panel's own - narrower
    /// than MapEditor.Menu's Row, which is a dialog's.</summary>
    private static HBoxContainer Field(string label, Control field)
    {
        var row = new HBoxContainer();
        var name = new Label
        {
            Text = label, CustomMinimumSize = new Vector2(58.0f, 0.0f),
        };
        name.AddThemeFontSizeOverride("font_size", 13);
        row.AddChild(name);
        field.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        row.AddChild(field);
        return row;
    }

    private CheckBox Ticked(string label, bool on, Func<bool, string> wrote)
    {
        var check = new CheckBox { Text = label, ButtonPressed = on };
        check.AddThemeFontSizeOverride("font_size", 13);
        check.Toggled += now =>
        {
            if (_loading)
                return;
            Wrote(() => wrote(now));
        };
        return check;
    }

    private Button Does(string label, Func<string> wrote)
    {
        var button = new Button { Text = label };
        button.AddThemeFontSizeOverride("font_size", 12);
        button.Pressed += () =>
        {
            if (_loading)
                return;
            Wrote(wrote);
        };
        return button;
    }

    /// <summary>
    /// One write from the panel: an undo entry, the write, the refusal on the
    /// readout, and the board settled.
    ///
    /// <b>One control is one stroke.</b> A brush dragged over forty cells is one
    /// entry on purpose - see <see cref="MapEdit.Begin"/> - and a checkbox is the
    /// other end of the same rule: one click, one thing changed, one undo.
    /// </summary>
    private void Wrote(Func<string> write, bool began = false)
    {
        if (!began)
            _edit.Begin();
        string why = write();
        _dirty = true;
        _said = why.Length > 0 ? why : "";
        // The shape is dropped rather than compared, so that a write which put
        // the cell into something else - a rock that took the wood off, a cover
        // that became masonry - rebuilds the group that is now wrong.
        _shape = "";
        Settle();
    }
}
