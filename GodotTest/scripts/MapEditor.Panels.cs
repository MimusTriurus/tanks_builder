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

    private TabBar _tabs = null!;
    private VBoxContainer _choices = null!;
    private Label _brushSays = null!;
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

    private const float LeftWide = 224.0f;
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

        _tabs = new TabBar();
        _tabs.AddThemeFontSizeOverride("font_size", 13);
        foreach (MapEdit.Tab tab in Enum.GetValues<MapEdit.Tab>())
            _tabs.AddTab(tab.ToString());
        _tabs.CurrentTab = (int)_tab;
        _tabs.TabChanged += index =>
        {
            if (_loading)
                return;
            _tab = (MapEdit.Tab)(int)index;
            Refresh();
            Say();
        };
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

        box.AddChild(new HSeparator());

        // The overlays, as the flags they are - see MapEditor.Layers, where
        // reading a ramp fault means seeing the levels and the arrows at once.
        foreach ((Layers flag, string name) in new[]
                 {
                     (Layers.Levels, "levels  L"),
                     (Layers.Ramps, "ramps  R"),
                     (Layers.Sites, "ramp sites  G"),
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
            Text = "left paint   right erase\nF fill   H home\nZ undo   Y redo\n"
                   + "Home recentre",
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
                    ("rock", () => Floor(Foundation.Rock),
                        () => Plain() && _floor == Foundation.Rock),
                    // Water is a switch rather than a floor - the alphabet's
                    // water is low ground with water standing in it, and a floor
                    // made of water has no letter. See MapEdit.Water.
                    ("water", () => { _water = true; _plotBrush = false; },
                        () => _water),
                    ("on the board", () => { _plotBrush = true; _water = false; },
                        () => _plotBrush),
                };
            case MapEdit.Tab.Cover:
                return Enum.GetValues<Cover>().Select(over =>
                    (over.ToString().ToLowerInvariant(),
                     (Action)(() => _cover = over),
                     (Func<bool>)(() => _cover == over))).ToList();
            default:
                var levels = new List<(string, Action, Func<bool>)>();
                foreach (int level in new[] { 1, 0, -1 })
                {
                    int want = level;
                    levels.Add(($"level {want:+0;-0;0}",
                        () => { _level = want; _rampBrush = false; },
                        () => !_rampBrush && _level == want));
                }
                // Through RampBrush, which is where the sites come on - see
                // its remarks, which carry why a ramp is one click and not two.
                levels.Add(("ramp", () => RampBrush(true), () => _rampBrush));
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
            };
            button.AddThemeFontSizeOverride("font_size", 14);
            Action chose = pick;
            button.Pressed += () =>
            {
                if (_loading)
                    return;
                chose();
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
        // already says emits nothing.
        _tabs.CurrentTab = (int)_tab;
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
              + $"{Slot(_picked)}";

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
            { Foundation.Solid, Foundation.Sand, Foundation.Rock };
        foreach (Foundation floor in laying)
            floors.AddItem(floor.ToString().ToLowerInvariant());
        floors.Selected = Math.Max(0, Array.IndexOf(laying, glyph.Floor));
        floors.ItemSelected += index => Wrote(() =>
            _edit.Floor(_picked, laying[(int)index], out string why) ? "" : why);
        into.AddChild(Field("floor", floors));

        into.AddChild(Ticked("water", glyph.Water,
            wet => _edit.Water(_picked, wet, out string why) ? "" : why));
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

        into.AddChild(Ticked("ramp", _edit.IsRamp(_picked),
            on => _edit.Ramp(_picked, on, out string why) ? "" : why));
        if (_edit.IsRamp(_picked))
        {
            int heading = _field.RampHeading(_picked);
            into.AddChild(Note(heading >= 0
                ? $"climbs towards {heading}"
                : "no edge to climb - see the report"));
        }

        // --- home ------------------------------------------------------------
        into.AddChild(Heading("Home"));
        int slot = Slot(_picked);
        if (slot >= 0)
        {
            into.AddChild(Note($"home {slot + 1} of {_edit.Homes.Count}"));
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

    // --- the small parts -----------------------------------------------------

    /// <summary>Which home a cell is, or -1. A walk rather than IndexOf,
    /// which a read-only list does not have.</summary>
    private int Slot(Vector2I cell)
    {
        for (int i = 0; i < _edit.Homes.Count; i++)
            if (_edit.Homes[i] == cell)
                return i;
        return -1;
    }

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
