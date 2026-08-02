using System;
using System.Collections.Generic;
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
        public Action Refresh = null!;
    }

    private readonly List<Row> _rows = new();
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

    public void Heading(string text)
    {
        if (_list.GetChildCount() > 0)
            _list.AddChild(new Control { CustomMinimumSize = new Vector2(0, 8) });
        var label = new Label { Text = text.ToUpperInvariant() };
        label.AddThemeColorOverride("font_color", new Color(0.55f, 0.72f, 0.90f));
        label.AddThemeFontSizeOverride("font_size", 12);
        _list.AddChild(label);
    }

    /// <summary>A switch: reads <paramref name="get"/>, and flipping it calls
    /// <paramref name="set"/> with what it should become.</summary>
    public void Toggle(string text, Func<bool> get, Action<bool> set)
    {
        var box = new CheckBox
        {
            Text = text,
            FocusMode = FocusModeEnum.None,
            ButtonPressed = get(),
        };
        box.Toggled += on => { if (!_syncing) set(on); };
        _list.AddChild(box);
        _rows.Add(new Row { Refresh = () => box.ButtonPressed = get() });
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
    public void Slide(string text, double lo, double hi, double step,
                      Func<double> get, Action<double> set, string unit = "",
                      Func<string>? note = null)
    {
        var caption = new Label();
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
        _list.AddChild(bar);
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
    public void Choice(string text, IReadOnlyList<string> options,
                       Func<int> get, Action<int> set)
    {
        var caption = new Label { Text = text };
        caption.AddThemeFontSizeOverride("font_size", 12);
        _list.AddChild(caption);
        var drop = new OptionButton { FocusMode = FocusModeEnum.None };
        foreach (string option in options)
            drop.AddItem(option);
        drop.Selected = Math.Clamp(get(), 0, options.Count - 1);
        drop.ItemSelected += i => { if (!_syncing) set((int)i); };
        _list.AddChild(drop);
        _rows.Add(new Row
        {
            Refresh = () => drop.Selected = Math.Clamp(get(), 0, options.Count - 1),
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
    public void Radio(Func<string> caption, IReadOnlyList<string> labels,
                      Func<int> get, Action<int> set)
    {
        var text = new Label();
        text.AddThemeFontSizeOverride("font_size", 12);
        _list.AddChild(text);
        var row = new HBoxContainer();
        _list.AddChild(row);
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
    public void Press(string text, Action go)
    {
        var button = new Button { Text = text, FocusMode = FocusModeEnum.None };
        button.Pressed += go;
        _list.AddChild(button);
    }

    /// <summary>Two of those side by side, for the pairs that read as a pair -
    /// fire and take a hit, reset and repair.</summary>
    public void PressPair(string left, Action goLeft, string right, Action goRight)
    {
        var row = new HBoxContainer();
        _list.AddChild(row);
        foreach ((string text, Action go) in new[] { (left, goLeft), (right, goRight) })
        {
            var button = new Button
            {
                Text = text,
                FocusMode = FocusModeEnum.None,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
            };
            button.Pressed += go;
            row.AddChild(button);
        }
    }

    /// <summary>A line of state with no control on it - what a plate is
    /// carrying, what the loaded set is.</summary>
    public void Readout(Func<string> get)
    {
        var label = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        label.AddThemeFontSizeOverride("font_size", 12);
        label.AddThemeColorOverride("font_color", new Color(0.72f, 0.76f, 0.80f));
        _list.AddChild(label);
        _rows.Add(new Row { Refresh = () => label.Text = get() });
    }
}
