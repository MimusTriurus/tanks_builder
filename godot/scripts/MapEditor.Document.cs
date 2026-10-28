using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The board as a document (stage 4 of docs/editor-ux-plan.md): its name and
/// size, the recent maps, the autosave and the question on closing.
///
/// <b>File → Board... is the board's own panel.</b> Rename it, give it a new
/// size kept to one of nine corners and edges, shift what is on it, or crop it
/// to the marked cells - each one undo (<see cref="MapEdit.Reframe"/>). The
/// columns move by an even number only: an odd one would move every odd column
/// half a row against the even ones, so the dialog rounds and says so.
///
/// <b>The autosave goes to the side, never into <c>maps/</c></b>: a board
/// halfway through being drawn is usually illegal, and in the folder it would
/// break every scene that reads it (<see cref="Guarded"/>'s reason). Once a
/// minute while there is unsaved work, and before every Run, the draft is
/// written as it stands to <c>user://autosave/&lt;name&gt;.json</c>
/// (<see cref="MapFile.WriteDraft"/>); a save takes it away. Opening a board
/// whose autosave is newer than its file asks whether to restore it.
///
/// <b>Closing the window with unsaved work asks</b>, and writes the autosave
/// first, so even "quit" keeps the work somewhere.
/// </summary>
public sealed partial class MapEditor
{
    // --- the board dialog ------------------------------------------------------

    private AcceptDialog _boardDialog = null!;
    private LineEdit _boardName = null!;
    private SpinBox _boardColumns = null!, _boardRows = null!, _shiftColumns = null!, _shiftRows = null!;
    private OptionButton _anchor = null!;
    private Label _boardSays = null!;

    /// <summary>The nine places a resized board keeps, columns then rows:
    /// 0 start, 1 middle, 2 end.</summary>
    private static readonly (string Name, int Q, int R)[] Anchors =
    {
        ("top left", 0, 0), ("top", 1, 0), ("top right", 2, 0),
        ("left", 0, 1), ("middle", 1, 1), ("right", 2, 1),
        ("bottom left", 0, 2), ("bottom", 1, 2), ("bottom right", 2, 2),
    };

    private void BoardDialog()
    {
        _boardDialog = new AcceptDialog { Title = "Board", OkButtonText = "Close" };
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 8);

        _boardName = new LineEdit();
        var rename = new HBoxContainer();
        rename.AddChild(Row("name", _boardName));
        rename.AddChild(Act("Rename", () => Rename(Clean(_boardName.Text))));
        box.AddChild(rename);
        box.AddChild(new HSeparator());
        box.AddChild(Header("size, and which part stays put"));

        _boardColumns = Number(MapEdit.Smallest, MapEdit.Largest, MapEdit.DefaultColumns);
        _boardRows = Number(MapEdit.Smallest, MapEdit.Largest, MapEdit.DefaultRows);
        box.AddChild(Row("columns", _boardColumns));
        box.AddChild(Row("rows", _boardRows));
        _anchor = new OptionButton();
        foreach ((string name, int _, int _) in Anchors)
            _anchor.AddItem("keep the " + name);
        _anchor.Selected = 4;
        box.AddChild(Row("anchor", _anchor));
        box.AddChild(Act("Resize", () => Resize(Typed(_boardColumns), Typed(_boardRows), _anchor.Selected)));
        box.AddChild(new HSeparator());
        box.AddChild(Header("shift what is on it - columns by twos"));

        _shiftColumns = Number(-MapEdit.Largest, MapEdit.Largest, 0);
        _shiftColumns.Step = 2;
        _shiftRows = Number(-MapEdit.Largest, MapEdit.Largest, 0);
        box.AddChild(Row("columns", _shiftColumns));
        box.AddChild(Row("rows", _shiftRows));
        box.AddChild(Act("Shift", () => Slide(Typed(_shiftColumns), Typed(_shiftRows))));
        box.AddChild(new HSeparator());

        box.AddChild(Act("Crop to the marked cells", Crop));
        _boardSays = new Label
        {
            Text = "each is one undo; columns move by an even number, so odd columns stay odd",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(320.0f, 0.0f),
        };
        _boardSays.AddThemeFontSizeOverride("font_size", 12);
        box.AddChild(_boardSays);

        _boardDialog.AddChild(box);
        _layer.AddChild(_boardDialog);
    }

    private static Label Header(string text)
    {
        var label = new Label { Text = text, Modulate = new Color(1, 1, 1, 0.7f) };
        label.AddThemeFontSizeOverride("font_size", 13);
        return label;
    }

    private static Button Act(string text, Action did)
    {
        var button = new Button { Text = text };
        button.Pressed += did;
        return button;
    }

    /// <summary>File → Board...: the dialog, filled in from the board.</summary>
    private void OpenBoard()
    {
        _boardName.Text = _edit.Name;
        _boardColumns.Value = _edit.Columns;
        _boardRows.Value = _edit.Rows;
        _shiftColumns.Value = 0;
        _shiftRows.Value = 0;
        _boardDialog.PopupCentered();
    }

    private void Rename(string name)
    {
        if (name == _edit.Name)
            return;
        string was = _edit.Name;
        _edit.Name = name;
        _dirty = true;
        Tells($"renamed {was} to {name} - not saved yet");
    }

    private void Resize(int columns, int rows, int anchor)
    {
        (string _, int q, int r) = Anchors[Math.Clamp(anchor, 0, Anchors.Length - 1)];
        int dq = MapEdit.Anchored(columns - _edit.Columns, q, columns: true);
        int dr = MapEdit.Anchored(rows - _edit.Rows, r, columns: false);
        Reframed(columns, rows, dq, dr, $"resized to {columns}x{rows}, keeping the {Anchors[anchor].Name}");
    }

    private void Slide(int dq, int dr)
    {
        // the box steps by two, but a typed number need not
        int even = dq - (dq & 1);
        Reframed(_edit.Columns, _edit.Rows, even, dr,
                 $"shifted {even} column(s), {dr} row(s)" + (even != dq ? $" ({dq} rounded to even)" : ""));
    }

    /// <summary>The board cut to the box round the marked cells, its first
    /// column rounded down to an even one.</summary>
    private void Crop()
    {
        if (_selected.Count == 0)
        {
            Tells("nothing marked - R, then drag a box round what to keep");
            return;
        }
        int left = _selected.Min(c => c.X), top = _selected.Min(c => c.Y);
        left -= left & 1;
        int columns = _selected.Max(c => c.X) - left + 1, rows = _selected.Max(c => c.Y) - top + 1;
        Reframed(Math.Max(columns, MapEdit.Smallest), Math.Max(rows, MapEdit.Smallest), -left, -top,
                 $"cropped to {Math.Max(columns, MapEdit.Smallest)}x{Math.Max(rows, MapEdit.Smallest)}");
    }

    private void Reframed(int columns, int rows, int dq, int dr, string said)
    {
        _edit.Begin();
        if (!_edit.Reframe(columns, rows, dq, dr, out int lost, out string why))
        {
            _edit.End();
            Tells("NOT changed: " + why);
            return;
        }
        _dirty |= _edit.End();
        _said = said + (lost > 0 ? $" - {lost} home(s) fell off" : "");
        Reshaped();
        _boardSays.Text = _said;
    }

    /// <summary>The board changed size under the editor without being
    /// another board: what <see cref="Opened"/> resets, the history and the
    /// dirt kept.</summary>
    private void Reshaped()
    {
        _sown = Array.Empty<string>();
        _stood = "";
        Unmark();
        if (!_edit.Inside(_picked))
            _picked = new Vector2I(-1, -1);
        Settle();
        Home();
    }

    private void Tells(string what)
    {
        _said = what;
        if (_boardSays is not null)
            _boardSays.Text = what;
        Say();
    }

    // --- recent ---------------------------------------------------------------

    private const int RecentKept = 5;

    private static string RecentPath => Path.Combine(ProjectSettings.GlobalizePath("user://"), "editor_recent.json");

    /// <summary>The last boards opened or saved, newest first. A file that has
    /// gone is not listed.</summary>
    private static List<string> Recent()
    {
        try
        {
            if (File.Exists(RecentPath)
                && JsonSerializer.Deserialize<List<string>>(File.ReadAllText(RecentPath)) is { } names)
                return names.Where(n => MapFile.Has(n) || BoardMap.Compiled.Contains(n, StringComparer.OrdinalIgnoreCase))
                            .Take(RecentKept).ToList();
        }
        catch (Exception e)
        {
            GD.PushWarning($"editor: the recent list is unreadable ({e.Message})");
        }
        return new List<string>();
    }

    private static void Remember(string name)
    {
        List<string> names = Recent();
        names.RemoveAll(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
        names.Insert(0, name);
        try
        {
            File.WriteAllText(RecentPath, JsonSerializer.Serialize(names.Take(RecentKept).ToList()));
        }
        catch (Exception e)
        {
            GD.PushWarning($"editor: the recent list was not written ({e.Message})");
        }
    }

    private PopupMenu _recent = null!;

    /// <summary>File → Recent, listed as the menu opens.</summary>
    private void RecentMenu(PopupMenu file)
    {
        _recent = new PopupMenu { Name = "Recent" };
        _recent.AddThemeFontSizeOverride("font_size", 16);
        _recent.IdPressed += id =>
        {
            string name = _recent.GetItemText(_recent.GetItemIndex((int)id));
            Guarded(() => OpenNamed(name));
        };
        file.AddChild(_recent);
        file.AddSubmenuNodeItem("Recent", _recent);
        file.AboutToPopup += () =>
        {
            _recent.Clear();
            List<string> names = Recent();
            for (int i = 0; i < names.Count; i++)
                _recent.AddItem(names[i], i);
            if (names.Count == 0)
            {
                _recent.AddItem("(none yet)", 0);
                _recent.SetItemDisabled(0, true);
            }
        };
    }

    /// <summary>A board opened by name - Open's list and Recent: remembered,
    /// and its autosave offered when it is newer.</summary>
    private void OpenNamed(string name)
    {
        try
        {
            _edit = MapEdit.Of(BoardMap.ByName(name));
            Opened("opened " + name);
            Remember(name);
            OfferAutosave();
        }
        catch (Exception e)
        {
            // The editor is the one tool that has to survive a board it
            // cannot load, --map-report's exemption and for its reason: it
            // is what the author fixes it with.
            Tell($"{name} refused:\n{e.Message}");
        }
    }

    // --- the autosave -----------------------------------------------------------

    /// <summary>Seconds between autosaves while there is unsaved work.</summary>
    private const double AutosaveEvery = 60.0;

    private double _autosaveClock;

    /// <summary>The text last written, so an unchanged draft is not written
    /// again every minute.</summary>
    private string _autosaved = "";

    private static string AutosaveDir => Path.Combine(ProjectSettings.GlobalizePath("user://"), "autosave");

    private static string AutosaveOf(string name) => Path.Combine(AutosaveDir, name + MapFile.Extension);

    /// <summary>The frame's half: a minute of unsaved work is written.</summary>
    private void AutosaveTick(double delta)
    {
        _autosaveClock += delta;
        if (_autosaveClock < AutosaveEvery)
            return;
        _autosaveClock = 0.0;
        Autosave("a minute");
    }

    /// <summary>The draft to the side, when there is unsaved work and it
    /// changed since the last time. Never throws: an autosave that fails is
    /// said in the log, not in the author's way.</summary>
    private void Autosave(string why)
    {
        if (!_dirty)
            return;
        try
        {
            string text = MapFile.WriteDraft(_edit);
            if (text == _autosaved)
                return;
            Directory.CreateDirectory(AutosaveDir);
            File.WriteAllText(AutosaveOf(_edit.Name), text);
            _autosaved = text;
            GD.Print($"editor: autosaved {_edit.Name} ({why}) to {AutosaveOf(_edit.Name)}");
        }
        catch (Exception e)
        {
            GD.PushWarning($"editor: autosave of {_edit.Name} failed - {e.Message}");
        }
    }

    /// <summary>A save took the work into <c>maps/</c>: its autosave is spent.</summary>
    private void Spent(string name)
    {
        _autosaved = "";
        try
        {
            if (File.Exists(AutosaveOf(name)))
                File.Delete(AutosaveOf(name));
        }
        catch (Exception e)
        {
            GD.PushWarning($"editor: the autosave of {name} was not taken away ({e.Message})");
        }
    }

    private ConfirmationDialog _restoreDialog = null!;
    private string _restoreFrom = "";

    private void RestoreDialog()
    {
        _restoreDialog = new ConfirmationDialog
        {
            Title = "Autosave", OkButtonText = "Restore it", CancelButtonText = "Keep the file",
        };
        _restoreDialog.Confirmed += () =>
        {
            try
            {
                MapEdit draft = MapFile.ReadDraft(_restoreFrom);
                _edit = draft;
                Opened($"restored {draft.Name} from its autosave - not saved yet");
                _dirty = true;
                Say();
            }
            catch (Exception e)
            {
                Tell("The autosave is unreadable.\n\n" + e.Message);
            }
        };
        _layer.AddChild(_restoreDialog);
    }

    /// <summary>The board just opened has an autosave newer than its file -
    /// or, for a board with no file, any autosave - that says something else:
    /// ask whether to take it.</summary>
    private bool OfferAutosave()
    {
        string path = AutosaveOf(_edit.Name);
        if (!File.Exists(path))
            return false;
        DateTime saved = File.GetLastWriteTime(path);
        if (MapFile.Has(_edit.Name) && File.GetLastWriteTime(MapFile.PathOf(_edit.Name)) >= saved)
            return false;
        if (File.ReadAllText(path) == MapFile.WriteDraft(_edit))
            return false;
        _restoreFrom = path;
        _restoreDialog.DialogText =
            $"There is an autosave of \"{_edit.Name}\" from {saved:yyyy-MM-dd HH:mm}, "
            + (MapFile.Has(_edit.Name) ? "newer than its file." : "and the board has no file.")
            + "\n\nRestore the autosave, or keep the board as opened?";
        _restoreDialog.PopupCentered();
        return true;
    }

    // --- closing the window -------------------------------------------------------

    private ConfirmationDialog _quitDialog = null!;

    private void QuitDialog()
    {
        _quitDialog = new ConfirmationDialog { Title = "Unsaved work", OkButtonText = "Quit", CancelButtonText = "Stay" };
        _quitDialog.Confirmed += () => GetTree().Quit();
        _layer.AddChild(_quitDialog);
    }

    /// <summary>The window's close button: straight out with nothing unsaved,
    /// else asked - with the autosave written first.</summary>
    public override void _Notification(int what)
    {
        if (what != NotificationWMCloseRequest || !_built)
            return;
        if (!_dirty)
        {
            GetTree().Quit();
            return;
        }
        Autosave("closing");
        _quitDialog.DialogText = $"{_edit.Name} has unsaved work on it.\n\n"
                                 + $"Quit anyway? It is kept in the autosave, and opening {_edit.Name} again offers it back.";
        _quitDialog.PopupCentered();
    }

    // --- the probe -------------------------------------------------------------------

    /// <summary>
    /// <c>--map drill10 --menu board</c>: stage 4 without a mouse. A resize to
    /// 12 x 11 kept in the middle, a shift and a crop, each checked against the
    /// grid it should be and undone back to the board it was; a draft with a
    /// fault in it written and read back the same; the recent list; and an
    /// autosave that, being newer than no file, is offered.
    /// </summary>
    private void BoardProbe()
    {
        // quit: the window's close button with unsaved work - the editor is
        // told what the window tells it, and must ask rather than go. Not
        // propagated from the root: that reaches the dialogs too, and an
        // AcceptDialog shuts on a close request, so the question vanished.
        if (_open == "quit" && _frame == 10)
        {
            _dirty = true;
            Notification((int)NotificationWMCloseRequest);
            GD.Print($"quit: auto accept {GetTree().AutoAcceptQuit}, asked {_quitDialog.Visible}");
            Spent(_edit.Name);
        }
        if (_open != "board" || _frame != 10)
            return;
        string[] was = _edit.Ground();
        string Homes() => string.Join(" ", _edit.Homes.Select(h => $"{h.Cell.X},{h.Cell.Y}"));
        string homes = Homes();

        Resize(12, 11, 4);
        // the old board must sit at (0, 0) + the even column shift and half the rows
        int dq = MapEdit.Anchored(2, 1, true), dr = MapEdit.Anchored(1, 1, false);
        bool inPlace = was.Select((row, r) => _edit.Ground()[r + dr].Substring(dq, row.Length) == row).All(x => x);
        GD.Print($"board: resized to {_edit.Columns}x{_edit.Rows} (field {_field.Columns}x{_field.Rows}), "
                 + $"the old board at +({dq},{dr}): {inPlace}, homes {Homes()}");
        Take(_edit.Undo(), "undone");
        GD.Print($"board: undone - {_edit.Columns}x{_edit.Rows} (field {_field.Columns}x{_field.Rows}), "
                 + $"the same board {_edit.Ground().SequenceEqual(was)}, the same homes {Homes() == homes}");

        Slide(3, 1);
        bool moved = Enumerable.Range(0, was.Length - 1)
            .All(r => _edit.Ground()[r + 1].Substring(2) == was[r][..^2]);
        GD.Print($"board: shift 3,1 - {_said}; what was on (q,r) is on (q+2,r+1): {moved}");
        Take(_edit.Undo(), "undone");

        MarkAll();
        _selected.RemoveWhere(c => c.X < 3 || c.Y < 2);
        Crop();
        GD.Print($"board: crop - {_said}; {_edit.Columns}x{_edit.Rows}, homes {Homes()}");
        Take(_edit.Undo(), "undone");
        GD.Print($"board: after three undos the board is the one opened: {_edit.Ground().SequenceEqual(was) && Homes() == homes}");

        // a draft with a fatal fault survives the disk as it is
        _edit.Begin();
        _edit.Level(new Vector2I(2, 2), 1, out string _);
        _edit.Level(new Vector2I(3, 2), 1, out string _);
        _edit.Ramp(new Vector2I(2, 3), true, out string _);
        _edit.End();
        Settle();
        string draft = MapFile.WriteDraft(_edit);
        string tmp = Path.Combine(AutosaveDir, "zz_probe" + MapFile.Extension);
        Directory.CreateDirectory(AutosaveDir);
        File.WriteAllText(tmp, draft);
        MapEdit back = MapFile.ReadDraft(tmp);
        File.Delete(tmp);
        GD.Print($"board: a draft with {_faults.Count(f => f.Fatal)} fatal fault(s) read back the same: "
                 + $"{back.Ground().SequenceEqual(_edit.Ground()) && back.Ramps().SequenceEqual(_edit.Ramps()) && MapFile.WriteDraft(back) == draft}");

        // the autosave of a board with no file is offered on opening
        string name = _edit.Name;
        _edit.Name = "zz_autosave";
        _dirty = true;
        Autosave("probe");
        _edit.Undo();
        Settle();
        bool offered = OfferAutosave();
        _restoreDialog.Hide();
        bool there = File.Exists(AutosaveOf("zz_autosave"));
        Spent("zz_autosave");
        _edit.Name = name;
        GD.Print($"board: autosave written {there}, offered {offered}, taken away {!File.Exists(AutosaveOf("zz_autosave"))}");
        GD.Print($"board: recent {string.Join(", ", Recent())}");
        Say();
    }

    /// <summary>
    /// <c>--map rocktest --menu rocks</c>: Ф2's notes without a mouse
    /// (docs/rocks-plan.md С5). The board written back as the file it was
    /// opened from; the mesa cut in two by a stroke, both halves holding its
    /// note; a whole rock given a note and the climate a wind; a draft of that
    /// through the disk; and undo back to the file.
    /// </summary>
    private void RockProbe()
    {
        if (_open != "rocks" || _frame != 10)
            return;
        string Notes() => string.Join("; ", _edit.Rocking().OrderBy(kv => kv.Key.Y).ThenBy(kv => kv.Key.X)
            .Select(kv => $"{kv.Key.X},{kv.Key.Y} {kv.Value.Look}"));
        string file = MapFile.Has(_edit.Name)
            ? File.ReadAllText(MapFile.PathOf(_edit.Name)).Replace("\r\n", "\n") : "";
        string opened = MapFile.Write(_edit.Build());
        GD.Print($"rocks: opened - {Notes()}; writes the file back: {opened == file}");

        // (3,1) out of the mesa leaves (4,1) a rock of its own
        _edit.Begin();
        _edit.Floor(new Vector2I(3, 1), Foundation.Solid, out string why);
        _edit.End();
        Settle();
        GD.Print($"rocks: cut at 3,1 ({(why.Length > 0 ? why : "done")}) - {Notes()}");

        _edit.Begin();
        _edit.Note(new Vector2I(9, 8), new RockNote { Type = RockType.Mesa, Palette = "red", Seed = 7 }, out why);
        _edit.Climate = _edit.Climate with { SaidWind = 90.0f };
        _edit.End();
        Settle();
        GD.Print($"rocks: 9,8 noted ({(why.Length > 0 ? why : "done")}), wind 90 - {Notes()}; "
                 + $"faults {string.Join(", ", _faults.Where(f => f.Rule.StartsWith("rock.")).Select(f => f.Rule))}");

        string draft = MapFile.WriteDraft(_edit);
        string tmp = Path.Combine(AutosaveDir, "zz_rocks" + MapFile.Extension);
        Directory.CreateDirectory(AutosaveDir);
        File.WriteAllText(tmp, draft);
        MapEdit back = MapFile.ReadDraft(tmp);
        File.Delete(tmp);
        GD.Print($"rocks: the draft read back the same: {MapFile.WriteDraft(back) == draft}");

        Take(_edit.Undo(), "undone");
        Take(_edit.Undo(), "undone");
        GD.Print($"rocks: two undos - {Notes()}; writes the file back: {MapFile.Write(_edit.Build()) == file}");
        Say();
    }
}
