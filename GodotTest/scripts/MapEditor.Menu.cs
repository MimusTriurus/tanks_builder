using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The editor's menu: new, open, save, save as - and the one guard between them.
///
/// <b>Dialogs rather than more keys, because these three are the operations that
/// need a word from the author.</b> Everything else the editor does is a brush,
/// and a brush wants the hand on the board; a new board needs two numbers and a
/// name, opening one needs to be told which, and saving over somebody's work
/// needs to be asked about. Those are the whole of what is on a menu here.
///
/// <b>Nothing on it can be reached from the board.</b> The dialogs are
/// <see cref="Window"/>s and the bar is a <see cref="Control"/>, so a click that
/// lands on either never reaches <see cref="MapEditor._UnhandledInput"/> - which
/// is what keeps opening the menu from painting the cell behind it.
/// </summary>
public sealed partial class MapEditor
{
    private CanvasLayer _layer = null!;
    /// <summary>The bar itself, for --menu poke.</summary>
    internal Control _bar = null!;
    private Label _title = null!;

    /// <summary>Whether anything has been painted since the last save. Not
    /// <see cref="MapEdit.Depth"/>: the stack is what can be taken back, and a
    /// board saved and then undone twice is a board that differs from its
    /// file.</summary>
    private bool _dirty;

    /// <summary>Whether the dialog is being preset rather than typed into, so
    /// the rows-follow-columns suggestion holds off.</summary>
    private bool _quiet;

    private ConfirmationDialog _newDialog = null!;
    private LineEdit _newName = null!;
    private SpinBox _newColumns = null!;
    private SpinBox _newRows = null!;
    private bool _rowsTouched;

    private ConfirmationDialog _openDialog = null!;
    private ItemList _openList = null!;

    private ConfirmationDialog _saveDialog = null!;
    private LineEdit _saveName = null!;

    private ConfirmationDialog _dropDialog = null!;
    private Action? _pending;

    private AcceptDialog _tellDialog = null!;

    private enum Item
    {
        New,
        Open,
        Save,
        SaveAs,
    }

    private void Menu(CanvasLayer layer)
    {
        _layer = layer;

        // A menu bar with one menu on it, the way a text editor has one.
        //
        // <b>This is the second answer to the same question, and the first one
        // is worth keeping in view.</b> The bar started as a MenuButton with a
        // popup, which a synthesised click opened every time (--menu poke) and
        // which the author could not open at all; it was replaced by four plain
        // buttons, and then asked for again in this shape. What makes the shape
        // safe is not the control, it is that <b>every item also has a key</b> -
        // N, O, S, shift+S, printed in the menu beside its item and handled in
        // MapEditor's own key switch. A menu that cannot be opened is then an
        // inconvenience rather than a tool nobody can use.
        //
        // <b>The keys are printed rather than registered as accelerators.</b> A
        // PopupMenu accelerator consumes the key before _UnhandledInput sees it,
        // so the two paths would be one path with a second place to break; the
        // label says what to press and the switch is the only thing listening.
        var bar = new MenuBar
        {
            Position = new Vector2(12.0f, 6.0f),
            // Big enough to hit. Forty-two pixels by thirty-one in a 1600x900
            // design space is a target the window's own stretch shrinks further.
            CustomMinimumSize = new Vector2(0.0f, 34.0f),
        };
        bar.AddThemeFontSizeOverride("font_size", 17);
        layer.AddChild(bar);

        var file = new PopupMenu { Name = "File" };
        file.AddThemeFontSizeOverride("font_size", 16);
        file.AddItem("New board...      N", (int)Item.New);
        file.AddItem("Open...           O", (int)Item.Open);
        file.AddSeparator();
        file.AddItem("Save              S", (int)Item.Save);
        file.AddItem("Save as...  shift+S", (int)Item.SaveAs);
        file.IdPressed += id => Chose((Item)id);
        bar.AddChild(file);
        _bar = bar;

        // Beside the bar rather than in it: a MenuBar lays out its PopupMenu
        // children as menus and stacks anything else at its own origin, which
        // put the board's name on top of the word File.
        _title = new Label
        {
            Position = new Vector2(86.0f, 12.0f),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _title.AddThemeColorOverride("font_color", new Color(0.94f, 0.96f, 1.0f));
        _title.AddThemeConstantOverride("outline_size", 5);
        _title.AddThemeColorOverride("font_outline_color",
            new Color(0, 0, 0, 0.85f));
        _title.AddThemeFontSizeOverride("font_size", 16);
        layer.AddChild(_title);

        Dialogs();
    }

    private void Chose(Item item)
    {
        switch (item)
        {
            case Item.New:
                Guarded(() =>
                {
                    _newName.Text = "untitled";
                    // Preset with the follow held off, so seeding the columns
                    // does not overwrite the rows with the suggestion - the
                    // default is ten by ten and the suggestion for ten is nine.
                    _quiet = true;
                    _newColumns.Value = MapEdit.DefaultColumns;
                    _newRows.Value = MapEdit.DefaultRows;
                    _quiet = false;
                    _rowsTouched = false;
                    _newDialog.PopupCentered();
                });
                break;
            case Item.Open:
                Guarded(() =>
                {
                    _openList.Clear();
                    foreach (string name in BoardMap.Names)
                        _openList.AddItem(
                            BoardMap.Compiled.Contains(
                                name, StringComparer.OrdinalIgnoreCase)
                                ? name + "   (built in)" : name);
                    _openDialog.PopupCentered();
                });
                break;
            case Item.Save:
                Keep();
                break;
            default:
                _saveName.Text = _edit.Name;
                _saveDialog.PopupCentered();
                break;
        }
    }

    /// <summary>
    /// Run something that throws the board away, once the author has said so.
    ///
    /// <b>Asked rather than autosaved.</b> A board halfway through being drawn is
    /// usually illegal - that is the whole reason the rules return a list - so
    /// saving it on the way out would put a file in the folder that the self test
    /// judges and every scene reads.
    /// </summary>
    private void Guarded(Action then)
    {
        if (!_dirty)
        {
            then();
            return;
        }
        _pending = then;
        _dropDialog.DialogText =
            $"{_edit.Name} has unsaved work on it. Throw it away?";
        _dropDialog.PopupCentered();
    }

    private void Dialogs()
    {
        // --- a new board -----------------------------------------------------
        _newDialog = new ConfirmationDialog { Title = "New board" };
        var fresh = new VBoxContainer();
        _newName = new LineEdit { Text = "untitled" };
        fresh.AddChild(Row("name", _newName));
        _newColumns = Number(4, 64, MapEdit.DefaultColumns);
        fresh.AddChild(Row("columns", _newColumns));
        _newRows = Number(4, 64, MapEdit.DefaultRows);
        fresh.AddChild(Row("rows", _newRows));
        // The proportion, said rather than enforced. A board square on the
        // ground has Rows = 0.866 * Columns - the column step is 1.5R and the row
        // step is sqrt(3)R, and the squash belongs to the camera - so twenty by
        // twenty is stretched where the tanks drive and looks right on screen,
        // which is exactly the mistake nothing else would catch.
        var hint = new Label
        {
            Text = "square on the ground is rows = 0.866 x columns",
        };
        hint.AddThemeFontSizeOverride("font_size", 12);
        fresh.AddChild(hint);
        _newColumns.ValueChanged += value =>
        {
            if (!_quiet && !_rowsTouched)
                _newRows.Value = MapEdit.Square((int)value);
        };
        _newRows.ValueChanged += _ =>
        {
            if (!_quiet)
                _rowsTouched = true;
        };
        _newDialog.AddChild(fresh);
        // Enter in the name field confirms, which is what anybody typing a name
        // then reaching for the keyboard expects.
        _newDialog.RegisterTextEnter(_newName);
        _newDialog.Confirmed += () =>
        {
            _edit = MapEdit.Blank(Clean(_newName.Text),
                                  Typed(_newColumns), Typed(_newRows));
            Opened($"new board {_edit.Columns}x{_edit.Rows}");
        };
        _layer.AddChild(_newDialog);

        // --- opening one -----------------------------------------------------
        _openDialog = new ConfirmationDialog { Title = "Open a board" };
        _openList = new ItemList { CustomMinimumSize = new Vector2(280, 320) };
        _openDialog.AddChild(_openList);
        _openDialog.Confirmed += () =>
        {
            int[] picked = _openList.GetSelectedItems();
            if (picked.Length == 0)
                return;
            string name = _openList.GetItemText(picked[0]).Split("   ")[0];
            try
            {
                _edit = MapEdit.Of(BoardMap.ByName(name));
                Opened("opened " + name);
            }
            catch (Exception e)
            {
                // The editor is the one tool that has to survive a board it
                // cannot load, --map-report's exemption and for its reason: it
                // is what the author fixes it with.
                Tell($"{name} refused:\n{e.Message}");
            }
        };
        _layer.AddChild(_openDialog);

        // --- saving under a name ---------------------------------------------
        _saveDialog = new ConfirmationDialog { Title = "Save as" };
        var named = new VBoxContainer();
        _saveName = new LineEdit();
        named.AddChild(Row("name", _saveName));
        var warn = new Label
        {
            Text = "a name this build already writes is refused",
        };
        warn.AddThemeFontSizeOverride("font_size", 12);
        named.AddChild(warn);
        _saveDialog.AddChild(named);
        _saveDialog.Confirmed += () =>
        {
            string was = _edit.Name;
            _edit.Name = Clean(_saveName.Text);
            if (!Keep())
                _edit.Name = was;
        };
        _layer.AddChild(_saveDialog);

        // --- throwing one away ------------------------------------------------
        _dropDialog = new ConfirmationDialog { Title = "Unsaved work" };
        _dropDialog.Confirmed += () =>
        {
            Action? then = _pending;
            _pending = null;
            then?.Invoke();
        };
        _layer.AddChild(_dropDialog);

        _tellDialog = new AcceptDialog { Title = "The map editor" };
        _layer.AddChild(_tellDialog);
    }

    private void Tell(string what)
    {
        _tellDialog.DialogText = what;
        _tellDialog.PopupCentered();
    }

    /// <summary>A board has just replaced the one being drawn: the field is the
    /// wrong size, the view is on the wrong place and the stack belongs to
    /// somebody else.</summary>
    private void Opened(string said)
    {
        _sown = Array.Empty<string?>();
        _stood = "";
        _dirty = false;
        _said = said;
        Settle();
        Home();
    }

    /// <summary>The name a file is saved under: lowercase, no spaces, and never
    /// empty. The file is looked up by it and the board answers to it, so a name
    /// with a space in it is a board that cannot be asked for.</summary>
    private static string Clean(string raw)
    {
        string name = new string(raw.Trim().ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_')
            .ToArray());
        return name.Length == 0 ? "untitled" : name;
    }

    private static HBoxContainer Row(string label, Control field)
    {
        var row = new HBoxContainer();
        row.AddChild(new Label
        {
            Text = label, CustomMinimumSize = new Vector2(80.0f, 0.0f),
        });
        field.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        row.AddChild(field);
        return row;
    }

    /// <summary>
    /// What a spin box says, including what has only been typed into it.
    ///
    /// <b>This is the bug the new-board dialog had, and it looked exactly like
    /// the board refusing to change size.</b> A <see cref="SpinBox"/> holds a
    /// <see cref="LineEdit"/>, and text typed into it reaches
    /// <see cref="Range.Value"/> only when it is committed - so clicking OK
    /// straight after typing built a board of the numbers that were there
    /// before, which is a new board of the old size and no error anywhere.
    /// <see cref="SpinBox.Apply"/> commits it first.
    /// </summary>
    private static int Typed(SpinBox box)
    {
        box.Apply();
        return (int)box.Value;
    }

    private static SpinBox Number(int low, int high, int value) => new()
    {
        MinValue = low, MaxValue = high, Step = 1, Value = value,
    };
}
