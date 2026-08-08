using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The slide-out panel down the right-hand edge: every switch the keyboard has,
/// with a name on it.
///
/// It is a *view of the harness's state, never a second copy of it*. Each row
/// is a pair of delegates onto the field the keys already write - a getter that
/// says what the widget should read and a setter that does exactly what the key
/// does - so pressing P and clicking "body pitch" cannot disagree. The obvious
/// arrangement, where a control owns its own bool and pushes it into the
/// harness, gives two truths that drift the moment anything changes state from
/// somewhere else: a shot fired from the panel while `--fire` set it, a class
/// switch clearing the damage, R resetting the lot.
///
/// So <see cref="Sync"/> runs every frame and writes the getters into the
/// widgets. That is a feedback loop unless it is stopped, because setting a
/// widget's value emits its changed signal, which would call the setter, which
/// would... The guard is <see cref="_syncing"/>, and it is asserted in the
/// self-test rather than trusted.
///
/// Nothing here takes keyboard focus. A focused Button eats SPACE and ENTER
/// and a focused slider eats the arrow keys, so the panel would quietly break
/// the keys it is a view of - the first thing that happened, on the turret
/// spin. FocusMode.None on every widget, no exceptions.
/// </summary>
public sealed partial class ControlPanel : PanelContainer
{
    public const int Width = 300;

    /// <summary>How long the slide takes. Long enough to read as a panel
    /// rather than a popup, short enough not to be waited on.</summary>
    private const double SlideSeconds = 0.22;

    /// <summary>One control, as the two delegates that connect it to the
    /// harness. Kept as a list so <see cref="Sync"/> is one loop and adding a
    /// row cannot forget to be synced.</summary>
    private sealed class Row
    {
        public string Id = "";
        public Action Refresh = null!;

        /// <summary>Applies this row's opening value from the file, through the
        /// very setter the key and the widget use. Null on a row that is not a
        /// setting - a readout, a button - because there is nothing to open on.
        ///
        /// Going through the setter rather than at the field is the whole trick:
        /// the file cannot reach anything the panel cannot, and a default and a
        /// click do exactly the same thing by construction.</summary>
        public Action<PanelText>? Open;
    }

    private readonly List<Row> _rows = new();

    /// <summary>Names, descriptions, ranges and opening values. Never null - an
    /// unloaded one answers with the code's own fallbacks, which is what the
    /// bench looked like before there was a file.</summary>
    public PanelText Text = PanelText.Load(null);

    /// <summary>Every row id in build order, and the group each landed in.
    /// Exposed so the file can be checked against the panel in both directions
    /// rather than trusted in either.</summary>
    public IReadOnlyList<(string Id, string Group)> Ids =>
        _rows.Where(r => r.Id != "").Select(r => (r.Id, GroupOf(r.Id))).ToList();

    private readonly Dictionary<string, string> _group = new();
    private string _heading = "";

    private string GroupOf(string id) =>
        _group.TryGetValue(id, out string? g) ? g : "";

    /// <summary>Set every row that the file has an opening value for. Called
    /// once, after the scene exists - a good few of these setters reach into a
    /// tank, and the tanks are built after the flags are read.</summary>
    public void OpenDefaults(IReadOnlyCollection<string> flagged)
    {
        foreach (Row row in _rows)
            if (!flagged.Contains(row.Id))
                row.Open?.Invoke(Text);
    }
    private VBoxContainer _list = null!;
    private Button _tab = null!;

    /// <summary>True while <see cref="Sync"/> is writing widgets, so the
    /// changed signals it provokes do not run back into the harness.</summary>
    private bool _syncing;

    /// <summary>Open, closed, and where between - 0 open, 1 closed.</summary>
    private double _slide;
    private bool _open = true;

    public bool Open => _open;

    /// <summary>Exposed for the self-test: whether a signal arriving now is the
    /// harness talking to the panel rather than the other way round.</summary>
    public bool Syncing => _syncing;

    public override void _Ready() => Prepare();

    /// <summary>Put the handle beside the panel.
    ///
    /// A sibling rather than a child, because children slide away with it and a
    /// panel that hides its own way back is one nobody opens again. Called by
    /// the harness after the panel is in the tree rather than from _Ready,
    /// where adding a sibling means going through CallDeferred and leaves a
    /// stray node behind if the run quits first.
    /// </summary>
    public void AddHandle()
    {
        _tab = new Button
        {
            Text = "›",
            FocusMode = FocusModeEnum.None,
            AnchorLeft = 1.0f,
            AnchorRight = 1.0f,
            AnchorTop = 0.0f,
            AnchorBottom = 0.0f,
            OffsetTop = 8,
            OffsetBottom = 36,
        };
        _tab.Pressed += Flip;
        GetParent().AddChild(_tab);
        Place(_slide);
    }

    /// <summary>Build the frame the rows go into.
    ///
    /// Split out of <see cref="_Ready"/> so the self-test can stand one of
    /// these up without a scene tree. What it asserts there - that syncing a
    /// widget from the harness does not run back into the harness - is a
    /// property of the rows, and the rows need real widgets to have it.
    /// </summary>
    public void Prepare()
    {
        // Idempotent: the harness calls this to build the rows before deciding
        // whether the panel goes in the tree, and _Ready calls it again if it
        // does. A second frame on top of the first would swallow every row.
        if (_list is not null)
            return;
        MouseFilter = MouseFilterEnum.Stop;   // clicks here are not orders to drive
        AnchorLeft = 1.0f;
        AnchorRight = 1.0f;
        AnchorTop = 0.0f;
        AnchorBottom = 1.0f;
        AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.09f, 0.10f, 0.12f, 0.94f),
            BorderWidthLeft = 1,
            BorderColor = new Color(0.35f, 0.40f, 0.46f),
            ContentMarginLeft = 12,
            ContentMarginRight = 12,
            ContentMarginTop = 10,
            ContentMarginBottom = 10,
        });

        var scroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            FocusMode = FocusModeEnum.None,
        };
        AddChild(scroll);
        _list = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _list.AddThemeConstantOverride("separation", 4);
        scroll.AddChild(_list);
    }

    /// <summary>Slide it out or away. Bound to the handle and to TAB.</summary>
    public void Flip()
    {
        _open = !_open;
        _tab.Text = _open ? "›" : "‹";
    }

    public override void _Process(double delta)
    {
        double want = _open ? 0.0 : 1.0;
        double step = delta / SlideSeconds;
        _slide = Math.Abs(want - _slide) <= step ? want
            : _slide + Math.Sign(want - _slide) * step;
        Place(_slide);
        Sync();
    }

    private void Place(double slide)
    {
        OffsetLeft = (float)(-Width + slide * Width);
        OffsetRight = (float)(slide * Width);
        if (_tab is not null)
            _tab.OffsetLeft = (float)(-Width - 28 + slide * Width);
        if (_tab is not null)
            _tab.OffsetRight = (float)(-Width + slide * Width);
    }

    /// <summary>Write the harness's state into the widgets. Called every frame:
    /// the keys, the command line and the game itself all change state behind
    /// the panel's back, and polling is both the smallest and the only
    /// arrangement that cannot go stale.</summary>
    public void Sync()
    {
        _syncing = true;
        try
        {
            foreach (Row row in _rows)
                row.Refresh();
        }
        finally
        {
            _syncing = false;
        }
    }

    // -----------------------------------------------------------------------
    // rows
    // -----------------------------------------------------------------------

    public void Heading(string id)
    {
        _heading = id;
        if (_list.GetChildCount() > 0)
            _list.AddChild(new Control { CustomMinimumSize = new Vector2(0, 8) });
        var label = new Label
        {
            Text = Text.GroupTitle(id).ToUpperInvariant(),
            TooltipText = Text.GroupNote(id),
            MouseFilter = MouseFilterEnum.Stop,   // a Label ignores the mouse, and an ignored label has no tooltip
        };
        label.AddThemeColorOverride("font_color", new Color(0.55f, 0.72f, 0.90f));
        label.AddThemeFontSizeOverride("font_size", 12);
        _list.AddChild(label);
    }

    /// <summary>A switch: reads <paramref name="get"/>, and flipping it calls
    /// <paramref name="set"/> with what it should become.</summary>
    public void Toggle(string id, string text, Func<bool> get, Action<bool> set)
    {
        var box = new CheckBox
        {
            Text = Text.Title(id, text),
            TooltipText = Text.Note(id),
            FocusMode = FocusModeEnum.None,
            ButtonPressed = get(),
        };
        box.Toggled += on => { if (!_syncing) set(on); };
        Add(id, box);
        _rows.Add(new Row
        {
            Id = id,
            Refresh = () => box.ButtonPressed = get(),
            Open = t => { if (t.Bool(id) is bool on) set(on); },
        });
    }

    /// <summary>A number. <paramref name="step"/> is not decoration: the atlas
    /// is quantised, and a heading slider that slides continuously through
    /// twelve rendered frames says the sprite is smoother than it is.
    ///
    /// <paramref name="note"/> is a second line under the slider saying what the
    /// value is worth in units the eye has an opinion about - pixels of travel,
    /// amplitude against the threshold it must not cross. A bare multiplier says
    /// nothing about whether the effect is visible or excessive, and those are
    /// the only two questions anyone drags one of these to answer.</summary>
    public void Slide(string id, string text, double lo, double hi, double step,
                      Func<double> get, Action<double> set, string unit = "",
                      Func<string>? note = null)
    {
        (lo, hi, step) = Text.Range(id, lo, hi, step);
        text = Text.Title(id, text);
        var caption = new Label
        {
            TooltipText = Text.Note(id),
            MouseFilter = MouseFilterEnum.Stop,
        };
        caption.AddThemeFontSizeOverride("font_size", 12);
        _list.AddChild(caption);
        var bar = new HSlider
        {
            MinValue = lo,
            MaxValue = hi,
            Step = step,
            Value = get(),
            FocusMode = FocusModeEnum.None,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        bar.ValueChanged += v => { if (!_syncing) set(v); };
        bar.TooltipText = Text.Note(id);
        Add(id, bar);
        Label? aside = null;
        if (note is not null)
        {
            aside = new Label();
            aside.AddThemeFontSizeOverride("font_size", 11);
            aside.AddThemeColorOverride("font_color", new Color(0.62f, 0.66f, 0.72f));
            _list.AddChild(aside);
        }
        _rows.Add(new Row
        {
            Id = id,
            Open = t => { if (t.Number(id) is double v) set(v); },
            Refresh = () =>
            {
                double now = get();
                bar.Value = now;
                caption.Text = step >= 1.0
                    ? $"{text}  {now:F0}{unit}"
                    : $"{text}  {now:F2}{unit}";
                if (aside is not null)
                    aside.Text = note!();
            },
        });
    }

    /// <summary>A choice. <paramref name="set"/> gets the index chosen.</summary>
    public void Choice(string id, string text, IReadOnlyList<string> options,
                       Func<int> get, Action<int> set)
    {
        var caption = new Label
        {
            Text = Text.Title(id, text),
            TooltipText = Text.Note(id),
            MouseFilter = MouseFilterEnum.Stop,
        };
        caption.AddThemeFontSizeOverride("font_size", 12);
        _list.AddChild(caption);
        var drop = new OptionButton { FocusMode = FocusModeEnum.None };
        foreach (string option in options)
            drop.AddItem(option);
        drop.Selected = Math.Clamp(get(), 0, options.Count - 1);
        drop.ItemSelected += i => { if (!_syncing) set((int)i); };
        drop.TooltipText = Text.Note(id);
        Add(id, drop);
        _rows.Add(new Row
        {
            Id = id,
            Refresh = () => drop.Selected = Math.Clamp(get(), 0, options.Count - 1),
            // Named, not numbered: an index into a list whose length is whatever
            // loaded picks a different tank the day a fourth one appears.
            Open = t =>
            {
                if (t.Choice(id) is not string want)
                    return;
                for (int i = 0; i < options.Count; i++)
                    if (string.Equals(options[i], want, StringComparison.OrdinalIgnoreCase))
                        set(i);
            },
        });
    }

    /// <summary>
    /// A short row of numbered buttons, one of which is down.
    ///
    /// A dropdown would do the same job in less room, and does not, because
    /// these are directions: which one is chosen is worth seeing without
    /// opening anything, and the six of them are a shape - the hex's six sides
    /// - rather than a list. Godot's ButtonGroup handles the "one down unpresses
    /// the rest" part; the caption carries what the number means.
    /// </summary>
    public void Radio(string id, Func<string> caption, IReadOnlyList<string> labels,
                      Func<int> get, Action<int> set)
    {
        var text = new Label
        {
            TooltipText = Text.Note(id),
            MouseFilter = MouseFilterEnum.Stop,
        };
        text.AddThemeFontSizeOverride("font_size", 12);
        _list.AddChild(text);
        var row = new HBoxContainer();
        Add(id, row);
        var group = new ButtonGroup();
        var buttons = new List<Button>();
        for (int i = 0; i < labels.Count; i++)
        {
            int index = i;
            var button = new Button
            {
                Text = labels[i],
                ToggleMode = true,
                ButtonGroup = group,
                FocusMode = FocusModeEnum.None,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                ButtonPressed = i == get(),
            };
            button.Toggled += on => { if (on && !_syncing) set(index); };
            row.AddChild(button);
            buttons.Add(button);
        }
        _rows.Add(new Row
        {
            Id = id,
            Open = t =>
            {
                if (t.Number(id) is double v)
                    set(Math.Clamp((int)v, 0, labels.Count - 1));
            },
            Refresh = () =>
            {
                // Only the chosen one is written: the group unpresses the rest
                // by itself, and writing `false` over the one that is down is
                // a state the group refuses to be in.
                int at = Math.Clamp(get(), 0, buttons.Count - 1);
                buttons[at].ButtonPressed = true;
                text.Text = caption();
            },
        });
    }

    /// <summary>Something that happens rather than something that is.</summary>
    public void Press(string id, string text, Action go)
    {
        var button = new Button
        {
            Text = Text.Title(id, text),
            TooltipText = Text.Note(id),
            FocusMode = FocusModeEnum.None,
        };
        button.Pressed += go;
        Add(id, button);
        // Registered with no Open: a button is something that happens, and a
        // file cannot have an opinion about what has already happened.
        _rows.Add(new Row { Id = id, Refresh = () => { } });
    }

    /// <summary>Two of those side by side, for the pairs that read as a pair -
    /// fire and take a hit, reset and repair.</summary>
    public void PressPair(string id, string left, Action goLeft,
                          string right, Action goRight)
    {
        var row = new HBoxContainer { TooltipText = Text.Note(id) };
        Add(id, row);
        // One id for the pair, because they read as a pair. The file names both
        // halves in one string with a slash between them, which keeps them from
        // drifting apart into two rows the panel has not got.
        string[] both = Text.Title(id, left + " / " + right).Split('/');
        int at = 0;
        foreach ((string text, Action go) in new[] { (left, goLeft), (right, goRight) })
        {
            var button = new Button
            {
                Text = (at < both.Length ? both[at] : text).Trim(),
                FocusMode = FocusModeEnum.None,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
            };
            at++;
            button.Pressed += go;
            row.AddChild(button);
        }
        _rows.Add(new Row { Id = id, Refresh = () => { } });
    }

    /// <summary>A line of state with no control on it - what a plate is
    /// carrying, what the loaded set is.</summary>
    public void Readout(string id, Func<string> get)
    {
        var label = new Label
        {
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            TooltipText = Text.Note(id),
            MouseFilter = MouseFilterEnum.Stop,
        };
        label.AddThemeFontSizeOverride("font_size", 12);
        label.AddThemeColorOverride("font_color", new Color(0.72f, 0.76f, 0.80f));
        Add(id, label);
        // A readout is not a setting and has no Open, but it is still named in
        // the file: what its numbers mean is exactly what a description is for,
        // and leaving it unnamed would make the two-way check between the file
        // and the panel impossible to state simply.
        _rows.Add(new Row { Id = id, Refresh = () => label.Text = get() });
    }

    /// <summary>Add a widget, remembering which heading it landed under so the
    /// grouping in the file can be checked against the grouping in the code
    /// rather than being a second opinion about it.</summary>
    private void Add(string id, Control control)
    {
        _group[id] = _heading;
        _list.AddChild(control);
    }
}
