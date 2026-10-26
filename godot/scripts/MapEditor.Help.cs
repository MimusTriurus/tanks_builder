using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The editor's second ring of comfort (docs/editor3d-plan.md, stage 3): the
/// Edit, View and Help menus, the line under the board that reads the cell
/// under the pointer, the F1 sheet, the brush's ghost and Escape.
///
/// <b>One table of keys, three readers.</b> <see cref="Keys"/> is what the F1
/// sheet lists, what the left panel's legend shows and what the menus print
/// beside their items - the 3D editor in ratel keeps one <c>HELP</c> table for
/// its F1 for the same reason: a key added in one place and described in
/// another is a key described wrong the day it moves.
///
/// <b>The menus still only print their keys</b> - <see cref="MapEditor.Menu"/>
/// argues it: a <see cref="PopupMenu"/> accelerator takes the key before
/// <see cref="MapEditor._UnhandledInput"/> sees it, so the key switch stays the
/// only thing listening and a menu item is the same call by another road.
/// </summary>
public sealed partial class MapEditor
{
    /// <summary>A row of the sheet: where it applies, what to press, what it
    /// does. <c>Section</c> is a tab's name for a brush's keys, else
    /// <c>Board</c>, <c>File</c> or <c>View</c>.</summary>
    /// <summary>A row of the sheet: where it applies, what to press, what it
    /// does, and - for the rows the left panel's legend shows - the few words
    /// that fit there.</summary>
    private readonly record struct KeyRow(string Section, string Press, string Does, string? Brief = null);

    private static readonly KeyRow[] Keys =
    {
        new("Board", "left drag", "paint with the brush", "paint"),
        new("Board", "right drag", "erase: plain ground, no cover, level 0", "erase"),
        new("Board", "1  2  3  4", "Foundation, Cover, Level, Units tab"),
        new("Board", "Q", "next brush on the tab"),
        new("Board", "[  ]", "brush radius, 0 to 6"),
        new("Board", "F", "fill the cell's region with the brush", "fill"),
        new("Board", "H", "put a home (parking) on the cell", "home"),
        new("Board", "alt+drag", "turn a home's tank towards the pointer, on the edges"),
        new("Board", "ctrl+wheel", "turn a home's tank an edge"),
        new("Board", "Z  Y", "undo, redo - a stroke at a time", "undo, redo"),
        new("Board", "wheel, middle drag", "zoom, pan; middle tap or Home recentres"),
        new("Board", "Esc", "close the sheet, then drop the picked cell"),
        new("Foundation", "W", "the brush is water"),
        new("Foundation", "E", "the brush puts a cell on or off the board"),
        new("Cover", "W", "the bunker brush; again, its slit turns a side"),
        new("Level", "W", "the brush is a ramp - its tilt is worked out from the heights"),
        new("Units", "left click", "put the brush's tank here, or move it here if its side has it"),
        new("Units", "right click", "take the tank off this cell"),
        new("File", "N  O", "new board, open one"),
        new("File", "S  shift+S", "save, save as"),
        new("File", "F5", "run the board on the 3D bench (Edit F5 there comes back)"),
        new("File", "ctrl+F5", "run it as a match: two sides off the homes"),
        new("View", "L  R  G  B", "levels, ramps, ramp sites, flood"),
        new("View", "T  M  K", "trees, grass, walls - shown or hidden"),
        new("View", ",  .", "turn the board a hex side either way; Home turns it back"),
        new("View", "F1", "this sheet", "every key"),
    };

    /// <summary>What of the dressing is shown - hidden to see the ground under
    /// a wood, never removed. The bench's switches: there an unshown layer is
    /// never built, here it is built and hidden, so View brings it back.</summary>
    private readonly BoardDress.Show _show = new();

    private PanelContainer? _sheet;
    private Label _foot = null!;

    private enum More
    {
        Undo = 100, Redo, Fill, PlaceHome, Recentre,
        Levels, Ramps, Sites, Flood, Homes, Faults,
        Trees, Grass, Walls, Bunkers, Mines,
        Sheet,
    }

    /// <summary>Edit, View and Help beside File.</summary>
    private void MoreMenus(MenuBar bar)
    {
        var edit = new PopupMenu { Name = "Edit" };
        edit.AddThemeFontSizeOverride("font_size", 16);
        edit.AddItem("Undo              Z", (int)More.Undo);
        edit.AddItem("Redo              Y", (int)More.Redo);
        edit.AddSeparator();
        edit.AddItem("Fill region       F", (int)More.Fill);
        edit.AddItem("Put a home        H", (int)More.PlaceHome);
        edit.IdPressed += id => Act((More)id);
        bar.AddChild(edit);

        var view = new PopupMenu { Name = "View" };
        view.AddThemeFontSizeOverride("font_size", 16);
        view.AddCheckItem("Levels            L", (int)More.Levels);
        view.AddCheckItem("Ramps             R", (int)More.Ramps);
        view.AddCheckItem("Ramp sites        G", (int)More.Sites);
        view.AddCheckItem("Flood             B", (int)More.Flood);
        view.AddCheckItem("Homes", (int)More.Homes);
        view.AddCheckItem("Faults", (int)More.Faults);
        view.AddSeparator();
        view.AddCheckItem("Trees             T", (int)More.Trees);
        view.AddCheckItem("Grass             M", (int)More.Grass);
        view.AddCheckItem("Walls             K", (int)More.Walls);
        // No keys: none is free (ui.md), and these two are looked at seldom.
        view.AddCheckItem("Bunkers", (int)More.Bunkers);
        view.AddCheckItem("Mines", (int)More.Mines);
        view.AddSeparator();
        view.AddItem("Recentre       Home", (int)More.Recentre);
        // Ticked off the state as the menu opens, so a key pressed since is
        // what the menu shows.
        view.AboutToPopup += () =>
        {
            foreach ((More item, bool on) in new[]
                     {
                         (More.Levels, _layers.HasFlag(Layers.Levels)), (More.Ramps, _layers.HasFlag(Layers.Ramps)),
                         (More.Sites, _layers.HasFlag(Layers.Sites)), (More.Flood, _layers.HasFlag(Layers.Flood)),
                         (More.Homes, _layers.HasFlag(Layers.Homes)), (More.Faults, _layers.HasFlag(Layers.Faults)),
                         (More.Trees, _show.Trees), (More.Grass, _show.Grass), (More.Walls, _show.Walls),
                         (More.Bunkers, _show.Bunkers), (More.Mines, _show.Mines),
                     })
                view.SetItemChecked(view.GetItemIndex((int)item), on);
        };
        view.IdPressed += id => Act((More)id);
        bar.AddChild(view);

        var help = new PopupMenu { Name = "Help" };
        help.AddThemeFontSizeOverride("font_size", 16);
        help.AddItem("Keys             F1", (int)More.Sheet);
        help.IdPressed += id => Act((More)id);
        bar.AddChild(help);
    }

    /// <summary>A menu item or a key: the same call either way.</summary>
    private void Act(More what)
    {
        switch (what)
        {
            case More.Undo: Take(_edit.Undo(), "undone"); return;
            case More.Redo: Take(_edit.Redo(), "redone"); return;
            case More.Fill: Fill(); return;
            case More.PlaceHome: PlaceHome(); return;
            case More.Recentre: Home(); return;
            case More.Levels: _layers ^= Layers.Levels; break;
            case More.Ramps: _layers ^= Layers.Ramps; break;
            case More.Sites: _layers ^= Layers.Sites; break;
            case More.Flood: _layers ^= Layers.Flood; break;
            case More.Homes: _layers ^= Layers.Homes; break;
            case More.Faults: _layers ^= Layers.Faults; break;
            case More.Trees: _show.Trees = !_show.Trees; Shown(); break;
            case More.Grass: _show.Grass = !_show.Grass; Shown(); break;
            case More.Walls: _show.Walls = !_show.Walls; Shown(); break;
            case More.Bunkers: _show.Bunkers = !_show.Bunkers; Shown(); break;
            case More.Mines: _show.Mines = !_show.Mines; Shown(); break;
            case More.Sheet: Sheet(_sheet is null); return;
        }
        _field.QueueRedraw();
        _marks.QueueRedraw();
        Say();
    }

    /// <summary>The dressing's visibility onto what stands - after every
    /// redress and every relaying of the walls, which make new nodes.</summary>
    private void Shown()
    {
        foreach (List<(Node3D Holder, Vector3 At)> wood in _woodOn.Values)
            foreach ((Node3D holder, Vector3 _) in wood)
                holder.Visible = _show.Trees;
        foreach (Node3D mine in _mineOn.Values)
            mine.Visible = _show.Mines;
        if (_lawn is not null)
            _lawn.Visible = _show.Grass;
        // A prop is a Node; what shows is its stack - and a capon's stack stays
        // hidden under its bunker (BoardDress.Bunker). Bunkers off is the bench's
        // --no-bunker: the capon as its masonry.
        foreach (WallProp prop in _bricks)
            if (prop.Stack is { } stack)
                stack.Visible = _show.Walls && (!_show.Bunkers || _shelters.All(b => b.Cell != prop.Cell));
        foreach (BunkerProp bunker in _shelters)
            bunker.Visible = _show.Walls && _show.Bunkers;
    }

    // --- the sheet -------------------------------------------------------------

    /// <summary>The left panel's legend: the board's own keys, off the table.</summary>
    private static string Legend() =>
        string.Join("\n", Keys.Where(k => k.Brief is not null).Select(k => $"{k.Press}  {k.Brief}"));

    /// <summary>The F1 sheet, open or shut. The tab in hand first: its brush's
    /// keys are the ones being looked for.</summary>
    private void Sheet(bool open)
    {
        _sheet?.QueueFree();
        _sheet = null;
        if (!open)
            return;
        string tab = _tab.ToString();
        IEnumerable<string> order = new[] { tab, "Board" }
            .Concat(Keys.Select(k => k.Section).Distinct().Where(s => s != tab && s != "Board"));
        var lines = new List<string>();
        foreach (string section in order)
        {
            KeyRow[] rows = Keys.Where(k => k.Section == section).ToArray();
            if (rows.Length == 0)
                continue;
            lines.Add(section.ToUpperInvariant());
            lines.AddRange(rows.Select(k => $"   {k.Press,-20}{k.Does}"));
            lines.Add("");
        }
        lines.Add("F1 or Esc closes");
        var text = new Label { Text = string.Join("\n", lines) };
        text.AddThemeFontSizeOverride("font_size", 15);
        text.AddThemeFontOverride("font", new SystemFont { FontNames = new[] { "Consolas", "monospace" } });
        _sheet = new PanelContainer
        {
            Position = new Vector2(LeftWide + 40.0f, 60.0f),
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        // Opaque: the theme's panel is see-through, and the readout under it
        // read through the sheet line for line.
        _sheet.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.10f, 0.11f, 0.13f, 0.97f),
            BorderColor = new Color(0.55f, 0.90f, 1.0f, 0.5f),
            BorderWidthLeft = 1, BorderWidthRight = 1, BorderWidthTop = 1, BorderWidthBottom = 1,
            ContentMarginLeft = 16, ContentMarginRight = 16, ContentMarginTop = 12, ContentMarginBottom = 12,
        });
        _sheet.AddChild(text);
        _layer.AddChild(_sheet);
    }

    /// <summary>Escape backs out a layer at a time: the sheet, then the picked
    /// cell. Answers whether it had anything to close.</summary>
    private bool Escape()
    {
        if (_sheet is not null)
        {
            Sheet(false);
            return true;
        }
        if (_edit.Inside(_picked))
        {
            _picked = new Vector2I(-1, -1);
            return true;
        }
        return false;
    }

    // --- the line under the board ---------------------------------------------

    private void Foot(CanvasLayer layer)
    {
        _foot = Text(LeftWide + 26.0f, 900.0f - 34.0f, 15);
        layer.AddChild(_foot);
    }

    /// <summary>
    /// The cell under the pointer, everything the board knows about it: where,
    /// its letter, floor, cover, level, ramp and its tilt, water, the home on it
    /// and its class, and the rules it breaks - the fault's own words, which the
    /// cell's colour only hints at.
    /// </summary>
    private string Under()
    {
        if (!_edit.Inside(_under))
            return "off the grid";
        Vector2I c = _under;
        MapFile.Glyph g = _edit.GlyphAt(c);
        var parts = new List<string>
        {
            $"({c.X},{c.Y})  '{g.Letter}'",
            g.Floor.ToString().ToLowerInvariant(),
        };
        if (_edit.MasonryAt(c) is { Kind: Masonry.Sort.Capon } capon)
            parts.Add($"bunker, slit {capon.Facing ?? 270}");
        else if (g.Over != Cover.None)
            parts.Add(g.Over.ToString().ToLowerInvariant());
        parts.Add($"level {g.Level}");
        if (_edit.IsRamp(c))
        {
            int heading = _field.RampHeading(c);
            parts.Add(heading >= 0 ? $"ramp up to {heading}" : "ramp, no tilt");
        }
        if (_field.InBounds(c) && _field.IsWater(c))
            parts.Add(_field.IsDeep(c) ? "deep water" : "water");
        int home = _edit.Homes.ToList().FindIndex(h => h.Cell == c);
        if (home >= 0)
            parts.Add($"home {home + 1}" + (_edit.Homes[home].Class is { Length: > 0 } cls ? $" {cls}" : ""));
        string line = string.Join("  ·  ", parts);
        MapRules.Fault[] here = _faults.Where(f => f.Cell == c).ToArray();
        if (here.Length > 0)
            line += "   —   " + string.Join("; ", here.Select(f => (f.Fatal ? "BAD " : "") + f.Text));
        return line;
    }

    // --- the brush's ghost -----------------------------------------------------

    /// <summary>
    /// What the brush would do, drawn before it does it: every cell under it
    /// outlined, and on the Level tab the top it would get - the outline lifted
    /// or dropped to that height with its corners joined down to the cell, the
    /// prism the stroke would build. The word for the brush rides by the
    /// pointer. ratel's editor has no ghost; there a placed thing is picked and
    /// follows the pointer instead, which a brush cannot do.
    /// </summary>
    private void Ghost(Overlay on)
    {
        if (!_edit.Inside(_under) || _sheet is not null)
            return;
        Color ink = _erasing ? new Color(1.0f, 0.6f, 0.5f, 0.9f) : new Color(BrushInk, 0.9f);
        foreach (Vector2I cell in Patch())
        {
            if (!_field.InBounds(cell))
                continue;
            float rise = 0.0f;
            if (_tab == MapEdit.Tab.Level && !_erasing && _field.Atlas is not null)
            {
                float want = _rampBrush
                    ? _field.LevelAt(cell) * _field.Lift + _field.Lift * 0.5f
                    : _level * _field.Lift;
                rise = want - _field.TopAt(cell);
            }
            foreach (int heading in HexField.EdgeHeadings)
            {
                (Vector2 a, Vector2 b) = EdgeOnScreen(cell, heading);
                on.DrawLine(a, b, new Color(ink, 0.55f), 2.0f);
                if (Mathf.Abs(rise) < 0.5f)
                    continue;
                (Vector2 ua, Vector2 ub) = EdgeOnScreen(cell, heading, rise);
                on.DrawLine(ua, ub, ink, 2.5f);
                on.DrawLine(a, ua, new Color(ink, 0.45f), 1.5f);
            }
        }
        // The bunker brush: which way each capon's slit would face, an arrow
        // from the cell's middle to the middle of that edge.
        if (_tab == MapEdit.Tab.Cover && _bunker && !_erasing)
            foreach (Vector2I cell in Patch())
            {
                if (!_field.InBounds(cell))
                    continue;
                (Vector2 a, Vector2 b) = EdgeOnScreen(cell, _facing);
                Vector2 from = TopOnScreen(cell), to = (a + b) * 0.5f;
                on.DrawLine(from, to, ink, 3.0f);
                Vector2 back = (from - to).Normalized() * 10.0f;
                on.DrawLine(to, to + back.Rotated(0.5f), ink, 3.0f);
                on.DrawLine(to, to + back.Rotated(-0.5f), ink, 3.0f);
            }
        string word = _erasing ? "erase" : Tool().Split(": ").Last();
        on.DrawString(ThemeDB.FallbackFont, TopOnScreen(_under) + new Vector2(18.0f, -18.0f), word,
                      HorizontalAlignment.Left, -1, 15, ink);
    }
}
