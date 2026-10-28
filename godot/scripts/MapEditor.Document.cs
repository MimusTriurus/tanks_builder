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

    /// <summary>
    /// <c>--map floortest --menu floor</c>: Ф3's floor under a cover without a
    /// mouse (docs/map-format3-plan.md). The board written back as the file;
    /// the brushes that used to refuse - sand on a wood, a wood on sand, mud
    /// raised to a hill - each said with the cell's floor and letter after;
    /// what the rules' draft reads there; a draft through the disk; and undo
    /// back to the file.
    /// </summary>
    private void FloorProbe()
    {
        if (_open != "floor" || _frame != 10)
            return;
        string file = MapFile.Has(_edit.Name)
            ? File.ReadAllText(MapFile.PathOf(_edit.Name)).Replace("\r\n", "\n") : "";
        GD.Print($"floor: opened - writes the file back: {MapFile.Write(_edit.Build()) == file}");
        string At(Vector2I c)
        {
            MapFile.Glyph g = _edit.GlyphAt(c);
            return $"{c.X},{c.Y} '{g.Letter}' {g.Floor}/{g.Over} level {g.Level}";
        }
        void Try(string what, Vector2I c, Func<string> stroke)
        {
            _edit.Begin();
            string why = stroke();
            _edit.End();
            GD.Print($"floor: {what} - {(why.Length > 0 ? "refused: " + why : At(c))}");
        }
        Try("sand under the wood at 5,1", new Vector2I(5, 1), () =>
            _edit.Over(new Vector2I(5, 1), Cover.Forest, out string a) && _edit.Floor(new Vector2I(5, 1), Foundation.Sand, out a) ? "" : a);
        Try("a wood on the mud at 7,2", new Vector2I(7, 2), () =>
            _edit.Over(new Vector2I(7, 2), Cover.Forest, out string a) ? "" : a);
        Try("the mud at 8,2 raised", new Vector2I(8, 2), () =>
            _edit.Level(new Vector2I(8, 2), 1, out string a) ? "" : a);
        Try("the wood on sand at 1,1 cut", new Vector2I(1, 1), () =>
            _edit.Over(new Vector2I(1, 1), Cover.None, out string a) ? "" : a);
        Try("water on the mud hollow at 2,6", new Vector2I(2, 6), () =>
            _edit.Water(new Vector2I(2, 6), true, out string a) ? "" : a);
        Try("a wall on sand at 8,6", new Vector2I(8, 6), () =>
            _edit.Over(new Vector2I(8, 6), Cover.Walls, out string a) ? "" : a);
        Settle();
        MapRules.Draft draft = _edit.Draft();
        GD.Print("floor: the draft's floors - "
                 + string.Join(", ", new[] { new Vector2I(5, 1), new Vector2I(7, 2), new Vector2I(8, 2), new Vector2I(3, 1) }
                     .Select(c => $"{c.X},{c.Y} {draft.Ground[draft.At(c)]}")));

        string draftText = MapFile.WriteDraft(_edit);
        string tmp = Path.Combine(AutosaveDir, "zz_floor" + MapFile.Extension);
        Directory.CreateDirectory(AutosaveDir);
        File.WriteAllText(tmp, draftText);
        MapEdit back = MapFile.ReadDraft(tmp);
        File.Delete(tmp);
        GD.Print($"floor: the draft read back the same: {MapFile.WriteDraft(back) == draftText}");

        while (_edit.Undo()) { }
        Settle();
        GD.Print($"floor: undone - writes the file back: {MapFile.Write(_edit.Build()) == file}");
        Say();
    }

    /// <summary>
    /// <c>--map roadtest --menu roadtool</c> and <c>--menu roadlay</c>: the
    /// road tool without a mouse (MapEditor.Roads). The first puts two points
    /// and leaves the pointer on a third, so a capture shows the ghost, the
    /// numbers and the axis; the second lays a road by its points, drags the
    /// dirt road's end handle, starts a fork with a press that does not move,
    /// tries a point across water, and undoes it all back to the file.
    /// </summary>
    private void RoadToolProbe()
    {
        if (_open is not ("roadtool" or "roadlay") || _frame != 10)
            return;
        string file = MapFile.Has(_edit.Name)
            ? File.ReadAllText(MapFile.PathOf(_edit.Name)).Replace("\r\n", "\n") : "";
        _tab = MapEdit.Tab.Roads;
        _roadBrush = RoadType.Gravel;
        void Click(Vector2I c)
        {
            _under = c;
            RoadPress(false);
            RoadRelease();
        }
        string Path() => string.Join(" ", _roadPath.Select(c => $"{c.X},{c.Y}"));
        Click(new Vector2I(7, 2));
        Click(new Vector2I(10, 6));
        _under = new Vector2I(13, 8);
        RoadLay();
        GD.Print($"roadtool: points 7,2 10,6 and the pointer on 13,8 - {_roadPath.Count} cells: {Path()}"
                 + (_roadBlock is null ? "" : $"; blocked: {_roadWhy}"));
        if (_open == "roadtool")
            return;

        RoadCommit();
        GD.Print($"roadtool: laid - {_said}; now {_edit.Roads.Count} roads, the last {_edit.Roads[^1]}");

        // the dirt road's far end dragged up to 13,0
        _under = new Vector2I(13, 2);
        RoadPress(false);
        _roadMoved = true;
        _roadPoints[_roadHolding] = new Vector2I(13, 0);
        RoadRelease();
        GD.Print($"roadtool: the dirt road's end dragged to 13,0 - "
                 + string.Join(" | ", _edit.Roads.Where(r => r.Type == RoadType.Dirt)));

        // a press on the cobble that does not move: a fork from it
        Click(new Vector2I(11, 4));
        Click(new Vector2I(11, 6));
        RoadCommit();
        GD.Print($"roadtool: a fork from the cobble at 11,4 - {_said}; the last {_edit.Roads[^1]}");

        // a point across the water: the first cell in the way and why
        _edit.Begin();
        _edit.Water(new Vector2I(4, 1), true, out string _);
        _edit.Water(new Vector2I(4, 2), true, out string _);
        _edit.End();
        Settle();
        Click(new Vector2I(3, 1));
        _under = new Vector2I(5, 1);
        RoadLay();
        GD.Print($"roadtool: 3,1 to 5,1 round the water - {Path()}"
                 + (_roadBlock is null ? "" : $"; blocked: {_roadWhy}"));
        RoadCancel();

        while (_edit.Undo()) { }
        Settle();
        GD.Print($"roadtool: undone - writes the file back: {MapFile.Write(_edit.Build()) == file}");
        Say();
    }

    /// <summary>
    /// <c>--map roadtest --menu roads</c>: Ф6's roads without a mouse. The
    /// board written back as the file; a road laid and one refused across
    /// water; the fork cut at its cell; water painted on a road; the turn's
    /// budget from a road and off it (<see cref="Steps"/>); a draft through the
    /// disk; and undo back to the file.
    /// </summary>
    private void RoadProbe()
    {
        if (_open != "roads" || _frame != 10)
            return;
        string file = MapFile.Has(_edit.Name)
            ? File.ReadAllText(MapFile.PathOf(_edit.Name)).Replace("\r\n", "\n") : "";
        string Roads() => string.Join("; ", _edit.Roads);
        GD.Print($"roads: opened - {_edit.Roads.Count} roads; writes the file back: "
                 + $"{MapFile.Write(_edit.Build()) == file}");

        _edit.Begin();
        int laid = _edit.Pave(new[] { new Vector2I(9, 6), new Vector2I(10, 6), new Vector2I(11, 6) },
                              RoadType.Asphalt, -1, out string why);
        _edit.End();
        GD.Print($"roads: laid 9,6-11,6 as road {laid + 1} {why}");
        _edit.Begin();
        _edit.Water(new Vector2I(12, 6), true, out why);
        int across = _edit.Pave(new[] { new Vector2I(11, 6), new Vector2I(12, 6) }, RoadType.Dirt, -1, out why);
        _edit.End();
        GD.Print($"roads: across the water at 12,6 - {(across < 0 ? "refused: " + why : "laid")}");

        _edit.Begin();
        int cut = _edit.Cut(new Vector2I(3, 3));
        _edit.End();
        GD.Print($"roads: cut at the fork 3,3 - {cut} roads touched; now {Roads()}");
        _edit.Begin();
        _edit.Water(new Vector2I(10, 1), true, out why);
        _edit.End();
        GD.Print($"roads: water at 10,1 - {(why.Length > 0 ? why : "")} now {_edit.Roads.Count} roads, "
                 + $"dirt: {string.Join(" | ", _edit.Roads.Where(r => r.Type == RoadType.Dirt))}");
        while (_edit.Undo()) { }
        Settle();

        // the turn's budget: one more step where the whole way is road
        // a speed of one, straight up the gravel 6,2-6,0 and up the grass 8,2-8,0
        var mover = new Steps.Mover { Speed = 1 };
        Steps.Reach on = Steps.Plan(_field, new Steps.Pose(new Vector2I(6, 2),
            HexField.HeadingTo(new Vector2I(6, 2), new Vector2I(6, 1))), mover);
        Steps.Reach off = Steps.Plan(_field, new Steps.Pose(new Vector2I(8, 2),
            HexField.HeadingTo(new Vector2I(8, 2), new Vector2I(8, 1))), mover);
        GD.Print($"roads: speed 1 up the gravel from 6,2 - budget {on.Budget} (plain {on.Plain}), "
                 + $"reaches 6,0: {on.Reaches(new Vector2I(6, 0))}, 7,1 off it: {on.Reaches(new Vector2I(7, 1))}; "
                 + $"up the grass from 8,2 - budget {off.Budget}, reaches 8,0: {off.Reaches(new Vector2I(8, 0))}");

        string draft = MapFile.WriteDraft(_edit);
        string tmp = Path.Combine(AutosaveDir, "zz_roads" + MapFile.Extension);
        Directory.CreateDirectory(AutosaveDir);
        File.WriteAllText(tmp, draft);
        MapEdit back = MapFile.ReadDraft(tmp);
        File.Delete(tmp);
        GD.Print($"roads: the draft read back the same: {MapFile.WriteDraft(back) == draft}; "
                 + $"undone - writes the file back: {MapFile.Write(_edit.Build()) == file}");
        Say();
    }

    /// <summary>
    /// <c>--map soiltest --menu soil</c>: Ф4's looks without a mouse. The board
    /// written back as the file; a look worn, one refused on the wrong floor,
    /// one dropped by a floor brush; the board's base changed; a draft through
    /// the disk; and undo back to the file.
    /// </summary>
    private void SoilProbe()
    {
        if (_open != "soil" || _frame != 10)
            return;
        string file = MapFile.Has(_edit.Name)
            ? File.ReadAllText(MapFile.PathOf(_edit.Name)).Replace("\r\n", "\n") : "";
        GD.Print($"soil: opened - writes the file back: {MapFile.Write(_edit.Build()) == file}");
        string Look(Vector2I c) => $"{c.X},{c.Y} {TankSpriteTest.Soil.Name(_edit.LookAt(c))}";
        void Try(string what, Vector2I c, Func<string> stroke)
        {
            _edit.Begin();
            string why = stroke();
            _edit.End();
            GD.Print($"soil: {what} - {(why.Length > 0 ? "refused: " + why : Look(c))}");
        }
        Try("stones on the plain at 0,0", new Vector2I(0, 0), () =>
            _edit.Wear(new Vector2I(0, 0), TankSpriteTest.Soil.Stony, out string a) ? "" : a);
        Try("dunes on the plain at 1,0", new Vector2I(1, 0), () =>
            _edit.Wear(new Vector2I(1, 0), TankSpriteTest.Soil.Dunes, out string a) ? "" : a);
        Try("the swamp at 8,1 painted sand", new Vector2I(8, 1), () =>
            _edit.Floor(new Vector2I(8, 1), Foundation.Sand, out string a) ? "" : a);
        Try("the board's sand back to ripples", new Vector2I(2, 1), () =>
        {
            _edit.Lay(Foundation.Sand, TankSpriteTest.Soil.Ripple);
            return "";
        });
        Settle();
        int drawn = _field.Columns * _field.Rows;
        int swamp = 0, stones = 0;
        for (int q = 0; q < _field.Columns; q++)
            for (int r = 0; r < _field.Rows; r++)
            {
                char l = _field.SoilAt(new Vector2I(q, r));
                swamp += l == TankSpriteTest.Soil.Swamp ? 1 : 0;
                stones += l == TankSpriteTest.Soil.Stony ? 1 : 0;
            }
        GD.Print($"soil: the field wears {swamp} swamp and {stones} stony of {drawn} cells");

        string draft = MapFile.WriteDraft(_edit);
        string tmp = Path.Combine(AutosaveDir, "zz_soil" + MapFile.Extension);
        Directory.CreateDirectory(AutosaveDir);
        File.WriteAllText(tmp, draft);
        MapEdit back = MapFile.ReadDraft(tmp);
        File.Delete(tmp);
        GD.Print($"soil: the draft read back the same: {MapFile.WriteDraft(back) == draft}");

        while (_edit.Undo()) { }
        Settle();
        GD.Print($"soil: undone - writes the file back: {MapFile.Write(_edit.Build()) == file}");
        Say();
    }

    /// <summary>
    /// <c>--menu icons</c>: every brush icon of the Floor and Cover tabs in a
    /// row a tab, drawn three times over without smoothing, to
    /// <c>out/editor_icons.png</c> - the list is longer than the window, and a
    /// 30 px icon is judged at its pixels.
    /// </summary>
    private void IconsProbe()
    {
        // asked at 10, read at 16: a model's icon is a viewport rendered in
        // the frames after it is asked for, and empty in the frame it was
        if (_open != "icons" || _frame is not (10 or 16))
            return;
        MapEdit.Tab was = _tab;
        if (_frame == 10)
        {
            foreach (MapEdit.Tab tab in TabOrder)
            {
                _tab = tab;
                foreach (var b in Brushes())
                    IconFor(tab, b.Name);
            }
            _tab = was;
            return;
        }
        var rows = new List<List<Image>>();
        foreach (MapEdit.Tab tab in TabOrder)
        {
            _tab = tab;
            rows.Add(Brushes().Select(b => IconFor(tab, b.Name)?.GetImage())
                              .Where(i => i is not null).Select(i => i!).ToList());
        }
        _tab = was;
        const int zoom = 3, cell = (IconSide + 2) * zoom;
        int wide = rows.Max(r => r.Count);
        var sheet = Image.CreateEmpty(wide * cell, rows.Count * cell, false, Image.Format.Rgba8);
        sheet.Fill(new Color(0.13f, 0.13f, 0.14f));
        for (int r = 0; r < rows.Count; r++)
            for (int k = 0; k < rows[r].Count; k++)
            {
                Image icon = rows[r][k];
                icon.Convert(Image.Format.Rgba8);
                // a model's viewport is larger than the icon: the panel's
                // button fits it to IconSide, and so does the sheet
                if (icon.GetWidth() != IconSide)
                    icon.Resize(IconSide, IconSide * icon.GetHeight() / icon.GetWidth(), Image.Interpolation.Lanczos);
                icon.Resize(icon.GetWidth() * zoom, icon.GetHeight() * zoom, Image.Interpolation.Nearest);
                sheet.BlendRect(icon, new Rect2I(Vector2I.Zero, icon.GetSize()), new Vector2I(k * cell, r * cell));
            }
        string path = Path.Combine(AssetRoot.Out, "editor_icons.png");
        Directory.CreateDirectory(AssetRoot.Out);
        sheet.SavePng(path);
        GD.Print($"icons: {string.Join(", ", rows.Select(r => r.Count))} a tab - {path}");
    }

    /// <summary>
    /// <c>--map woodtest --menu wood</c>: the species without a mouse
    /// (format 3, Ф5). Which models the woods stand, a cell given acacia and
    /// pine taken out of the mix, the warning on a species with no wood, the
    /// draft and the undo back to the file.
    /// </summary>
    private void WoodProbe()
    {
        // three frames apart: a wood laid again stands in the next frame, the
        // trees it replaced leave at the end of this one
        if (_open != "wood" || _frame is not (10 or 14 or 18))
            return;
        string file = MapFile.Has(_edit.Name)
            ? File.ReadAllText(MapFile.PathOf(_edit.Name)).Replace("\r\n", "\n") : "";
        string Stood()
        {
            var kinds = new SortedDictionary<string, int>();
            foreach (List<(Node3D Holder, Vector3 At)> wood in _woodOn.Values)
                foreach ((Node3D holder, _) in wood)
                {
                    string kind = holder.Name.ToString().Split('_')[0];
                    kinds[kind] = kinds.GetValueOrDefault(kind) + 1;
                }
            return string.Join(", ", kinds.Select(kv => $"{kv.Value} {kv.Key}"));
        }
        string Cell(Vector2I c) => string.Join(" ", _woodOn.TryGetValue(c, out var w)
            ? w.Select(t => t.Holder.Name.ToString().Split('.')[0]) : Array.Empty<string>());
        if (_frame == 18)
        {
            GD.Print($"wood: undone - writes the file back: {MapFile.Write(_edit.Build()) == file}; stands {Stood()}");
            // for the capture: the Look tab with the pine brush up, the
            // spruce cell picked
            _speciesBrush = 'p';
            _picked = new Vector2I(5, 0);
            _tab = MapEdit.Tab.Look;
            Refresh();
            Say();
            return;
        }
        if (_frame == 10)
        {
            GD.Print($"wood: opened - writes the file back: {MapFile.Write(_edit.Build()) == file}");
            GD.Print($"wood: stands {Stood()}");
            GD.Print($"wood: 0,0 by the mix - {Cell(new Vector2I(0, 0))}; 5,0 spruce - {Cell(new Vector2I(5, 0))}");
            GD.Print($"wood: waiting - {string.Join("; ", _faults.Where(f => f.Rule == "trees.wait").Select(f => f.Text))}");
            _edit.Begin();
            bool planted = _edit.Plant(new Vector2I(0, 0), 'a', out string why);
            _edit.Weigh('p', 0);
            _edit.End();
            GD.Print($"wood: 0,0 acacia - {(planted ? "ok" : why)}; pine out of the mix");
            Settle();
            return;
        }
        GD.Print($"wood: 0,0 {Cell(new Vector2I(0, 0))}; 1,1 {Cell(new Vector2I(1, 1))}; stands {Stood()}; "
                 + $"the file says {string.Join(" ", MapFile.Write(_edit.Build()).Split('\n').Where(l => l.Contains("\"wood\"")))}");

        string draft = MapFile.WriteDraft(_edit);
        string tmp = Path.Combine(AutosaveDir, "zz_wood" + MapFile.Extension);
        Directory.CreateDirectory(AutosaveDir);
        File.WriteAllText(tmp, draft);
        MapEdit back = MapFile.ReadDraft(tmp);
        File.Delete(tmp);
        GD.Print($"wood: the draft read back the same: {MapFile.WriteDraft(back) == draft}");

        while (_edit.Undo()) { }
        Settle();
    }
}
