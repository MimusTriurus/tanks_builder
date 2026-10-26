using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The toolbar and the preset brushes (stage 5 of docs/editor-ux-plan.md).
///
/// <b>The tabs say what with, the toolbar says how.</b> Six tools over every
/// tab: the brush (a drag paints), the line (press, drag, let go - the cells
/// between, a hex line), the box (the same, a filled box of columns and rows),
/// fill (a click fills the region of one letter, or the marked cells), select
/// (MapEditor.Select) and pick, the eyedropper: a click takes the cell's floor,
/// cover, level or tank as the tab's brush and goes back to the brush. Keys
/// where one is free: U box, G fill, R select, I pick; the line has none left,
/// and shift+click with the brush draws one from the last cell painted. The
/// right button on the line or the box erases along it. The radius widens a
/// line or a box as it widens the brush; a symmetry draws the twin.
///
/// <b>The Presets tab is a fifth palette, not a replacement for the four</b>
/// (<see cref="MapPresets"/>, data in <c>editor_presets.json</c>). Its ghost
/// is the stroke's result: the stroke is laid on a copy of the board
/// (<see cref="MapEdit.Fork"/>) and every cell it would change is drawn in the
/// colour it would become, edge ring and twins included.
/// </summary>
public sealed partial class MapEditor
{
    private enum Instrument { Brush, Line, Box, Fill, Select, Pick }

    private Instrument _instrument = Instrument.Brush;

    /// <summary>The select tool, as MapEditor.Select reads it.</summary>
    private bool _selectTool
    {
        get => _instrument == Instrument.Select;
        set => _instrument = value ? Instrument.Select : _instrument == Instrument.Select ? Instrument.Brush : _instrument;
    }

    private static string Named(Instrument tool) => tool switch
    {
        Instrument.Line => "line",
        Instrument.Box => "box",
        Instrument.Fill => "fill",
        Instrument.Select => "select",
        Instrument.Pick => "pick",
        _ => "brush",
    };

    /// <summary>A tool taken up, by key or toolbar.</summary>
    private void Use(Instrument tool)
    {
        if (tool == Instrument.Select)
        {
            SelectTool(true);
            return;
        }
        _instrument = tool;
        _boxFrom = null;
        _dragFrom = null;
        _said = tool switch
        {
            Instrument.Line => "line: press, drag, let go; right button erases along it",
            Instrument.Box => "box: press, drag, let go; right button erases it",
            Instrument.Fill => "fill: a click fills the cell's region, or the marked cells",
            Instrument.Pick => "pick: click a cell to take what it is as the brush",
            _ => "brush",
        };
    }

    /// <summary>Where a line or a box was pressed, while it is dragged.</summary>
    private Vector2I? _dragFrom;
    private bool _dragErase;

    /// <summary>The last cell a stroke started on: shift+click draws a line
    /// from it.</summary>
    private Vector2I _lastPainted = new(-1, -1);

    // --- input ---------------------------------------------------------------

    /// <summary>The tools' share of the pointer. Answers whether the event was
    /// theirs; the brush's ordinary stroke is not.</summary>
    private bool ToolInput(InputEvent what)
    {
        if (_held is not null || _selectTool)
            return false;
        if (what is InputEventMouseMotion && _dragFrom is not null)
        {
            _under = Pointed();
            _marks.QueueRedraw();
            Say();
            return true;
        }
        if (what is not InputEventMouseButton click
            || click.ButtonIndex is not (MouseButton.Left or MouseButton.Right))
            return false;
        bool left = click.ButtonIndex == MouseButton.Left;
        switch (_instrument)
        {
            case Instrument.Brush:
                if (!left || !click.Pressed || !click.ShiftPressed || !_edit.Inside(_lastPainted))
                    return false;
                _under = Pointed();
                if (_edit.Inside(_under))
                {
                    Shape(MapEdit.Line(_lastPainted, _under), erase: false, "line");
                    _lastPainted = _under;
                }
                return true;
            case Instrument.Fill:
                if (left && click.Pressed)
                {
                    _under = Pointed();
                    Fill();
                }
                return true;
            case Instrument.Pick:
                if (left && click.Pressed)
                {
                    _under = Pointed();
                    Pick(_under);
                }
                return true;
            case Instrument.Line or Instrument.Box:
                _under = Pointed();
                if (click.Pressed)
                {
                    if (_edit.Inside(_under))
                    {
                        _dragFrom = _under;
                        _dragErase = !left;
                    }
                    return true;
                }
                if (_dragFrom is Vector2I from)
                {
                    _dragFrom = null;
                    Shape(Shaped(from, _edit.Inside(_under) ? _under : from), _dragErase, Named(_instrument));
                }
                return true;
        }
        return false;
    }

    /// <summary>The cells a line or a box from <paramref name="from"/> to
    /// <paramref name="to"/> covers, before the radius.</summary>
    private IReadOnlyList<Vector2I> Shaped(Vector2I from, Vector2I to) =>
        _instrument == Instrument.Box ? _edit.Box(from, to) : MapEdit.Line(from, to).Where(_edit.Inside).ToList();

    /// <summary>The cells widened by the brush's radius.</summary>
    private List<Vector2I> Widened(IReadOnlyList<Vector2I> cells)
    {
        if (_radius == 0)
            return cells.ToList();
        var all = new HashSet<Vector2I>();
        foreach (Vector2I cell in cells)
            all.UnionWith(_edit.Ring(cell, _radius));
        return all.OrderBy(c => c.Y).ThenBy(c => c.X).ToList();
    }

    /// <summary>A line or a box written with the tab's brush - or erased -
    /// and its twins, as one undo.</summary>
    private void Shape(IReadOnlyList<Vector2I> cells, bool erase, string word)
    {
        if (_tab == MapEdit.Tab.Units)
        {
            _said = "tanks are put one by one - a click on the Units tab";
            Say();
            return;
        }
        List<Vector2I> core = Widened(cells);
        _edit.Begin();
        var refused = new List<string>();
        if (erase)
        {
            refused.AddRange(_edit.Sweep(core, c => (_edit.Erase(c, _tab, out string why), why)));
            refused.AddRange(_edit.Sweep(Twins(core), c => (_edit.Erase(c, _tab, out string why), why)));
        }
        else if (_tab == MapEdit.Tab.Presets)
            refused.AddRange(LayPreset(_edit, core));
        else
        {
            refused.AddRange(_edit.Sweep(core, Write));
            refused.AddRange(_edit.Sweep(Twins(core), WriteTwin));
        }
        _dirty |= _edit.End();
        _said = refused.Count > 0 ? refused[0] : $"{word}{(erase ? " erased" : "")}: {core.Count} cell(s)";
        Settle();
    }

    // --- the eyedropper ----------------------------------------------------------

    /// <summary>What a cell is, taken as the tab's brush; then back to the
    /// brush.</summary>
    private void Pick(Vector2I cell)
    {
        if (!_edit.Inside(cell))
            return;
        MapFile.Glyph g = _edit.GlyphAt(cell);
        string took;
        switch (_tab)
        {
            case MapEdit.Tab.Foundation:
                if (!_edit.OnBoard(cell))
                {
                    _plotBrush = true;
                    _plotOn = false;
                    _water = false;
                    took = "off the board";
                }
                else if (g.Water && g.Floor != Foundation.Deep)
                {
                    _water = true;
                    _plotBrush = false;
                    took = "water";
                }
                else
                {
                    Floor(g.Floor);
                    took = g.Floor.ToString().ToLowerInvariant();
                }
                break;
            case MapEdit.Tab.Cover:
                if (_edit.MasonryAt(cell) is { Kind: Masonry.Sort.Capon } capon)
                {
                    _bunker = true;
                    _cover = Cover.Walls;
                    _facing = capon.Facing ?? 270;
                    took = $"bunker, slit {_facing}";
                }
                else
                {
                    _bunker = false;
                    _cover = g.Over;
                    took = g.Over.ToString().ToLowerInvariant();
                }
                break;
            case MapEdit.Tab.Level:
                if (g.Cliff)
                {
                    _said = "a rock is a floor, not a level - pick it on the Foundation tab";
                    Say();
                    return;
                }
                _level = g.Level;
                _rampBrush = false;
                took = $"level {g.Level}";
                break;
            case MapEdit.Tab.Units:
                Lineup.Unit[] here = Lineup.Of(_edit.Homes).Where(u => u.Cell == cell).ToArray();
                int pair = here.Length > 0 ? Lineup.PairAt(here[0].Model) : -1;
                if (pair < 0)
                {
                    _said = $"no tank on ({cell.X},{cell.Y}) to pick";
                    Say();
                    return;
                }
                _unitSide = here[0].Side;
                _unitClass = Lineup.Pairs[pair].Class;
                took = $"{_unitSide} {_unitClass}";
                break;
            default:
                _said = "a preset is picked from the palette";
                Say();
                return;
        }
        _instrument = Instrument.Brush;
        _said = $"picked {took} off ({cell.X},{cell.Y})";
        Say();
    }

    // --- presets -----------------------------------------------------------------

    private IReadOnlyList<MapPresets.Preset> _presets = Array.Empty<MapPresets.Preset>();
    private int _preset;
    private string _presetsSay = "";

    /// <summary>The file read again - when the tab is laid out, so an edit to
    /// it shows the next time the tab is opened.</summary>
    private void ReadPresets()
    {
        _presets = MapPresets.Load(out _presetsSay);
        _preset = Math.Clamp(_preset, 0, Math.Max(0, _presets.Count - 1));
        if (_presetsSay.Length > 0)
            GD.PushWarning("editor: " + _presetsSay);
    }

    private MapPresets.Preset? Preset => _preset < _presets.Count ? _presets[_preset] : null;

    /// <summary>The preset laid on <paramref name="core"/> and on its twins -
    /// on the board or on a fork of it for the ghost.</summary>
    private List<string> LayPreset(MapEdit on, IReadOnlyCollection<Vector2I> core, ISet<Vector2I>? keep = null)
    {
        if (Preset is not { } preset)
            return new List<string> { _presetsSay.Length > 0 ? _presetsSay : "no presets" };
        List<string> refused = MapPresets.Lay(on, preset, core, keep);
        List<Vector2I> twins = Twins(core);
        if (twins.Count > 0)
            refused.AddRange(MapPresets.Lay(on, preset, twins, keep));
        return refused;
    }

    /// <summary>The core the cells of this drag have laid - the next step's
    /// edge leaves them alone (<see cref="MapPresets.Lay"/>).</summary>
    private readonly HashSet<Vector2I> _strokeCore = new();

    /// <summary>One step of a preset drag (<see cref="Stroke"/>'s branch).</summary>
    private List<string> PresetStep(bool started)
    {
        if (started)
            _strokeCore.Clear();
        IReadOnlyList<Vector2I> core = Own();
        List<string> refused = LayPreset(_edit, core, _strokeCore);
        _strokeCore.UnionWith(core);
        _strokeCore.UnionWith(Twins(core));
        return refused;
    }

    // --- ghosts ------------------------------------------------------------------

    private string _ghostKey = "";
    private readonly List<(Vector2I Cell, char Letter)> _ghostCells = new();

    /// <summary>The tools' ghosts, ahead of the brush's: a line or a box being
    /// dragged, the cell the eyedropper would read, a preset's result.
    /// Answers whether it drew.</summary>
    private bool ToolGhost(Overlay on)
    {
        if (_dragFrom is Vector2I from && _edit.Inside(_under))
        {
            List<Vector2I> core = Widened(Shaped(from, _under));
            var all = new HashSet<Vector2I>(core);
            all.UnionWith(Twins(core));
            Color ink = _dragErase ? new Color(1.0f, 0.6f, 0.5f) : BrushInk;
            Outline(on, all, ink, 2.5f);
            Word(on, $"{Named(_instrument)}{(_dragErase ? " erase" : "")} {core.Count}", ink);
            return true;
        }
        if (_instrument == Instrument.Pick)
        {
            Outline(on, new HashSet<Vector2I> { _under }, SelectInk, 2.5f);
            Word(on, "pick", SelectInk);
            return true;
        }
        if (_tab != MapEdit.Tab.Presets || _erasing || Preset is not { } preset)
            return false;
        PresetGhost(on, preset);
        return true;
    }

    /// <summary>What the preset would make of the board here: laid on a fork,
    /// every changed cell drawn in its new colour. Worked out again only when
    /// the pointer, the brush or the board moved.</summary>
    private void PresetGhost(Overlay on, MapPresets.Preset preset)
    {
        string key = $"{_under}:{_radius}:{_preset}:{_symmetry}:{_edit.Depth}:{_edit.Columns}x{_edit.Rows}";
        if (key != _ghostKey)
        {
            _ghostKey = key;
            _ghostCells.Clear();
            MapEdit fork = _edit.Fork();
            LayPreset(fork, Own());
            for (int r = 0; r < _edit.Rows; r++)
            for (int q = 0; q < _edit.Columns; q++)
            {
                var cell = new Vector2I(q, r);
                if (fork.LetterAt(cell) != _edit.LetterAt(cell))
                    _ghostCells.Add((cell, fork.LetterAt(cell)));
            }
        }
        var corners = new Vector2[6];
        foreach ((Vector2I cell, char letter) in _ghostCells)
        {
            foreach (int heading in HexField.EdgeHeadings)
                corners[HexField.EdgeIndex(heading)] = EdgeOnScreen(cell, heading).A;
            on.DrawColoredPolygon(corners, new Color(Tint(letter), 0.6f));
        }
        var core = new HashSet<Vector2I>(Own());
        core.UnionWith(Twins(Own()));
        Outline(on, core, new Color(BrushInk, 0.9f), 2.0f);
        Word(on, preset.Name, BrushInk);
    }

    private void Word(Overlay on, string word, Color ink) =>
        on.DrawString(ThemeDB.FallbackFont, TopOnScreen(_under) + new Vector2(18.0f, -18.0f), word,
                      HorizontalAlignment.Left, -1, 15, ink);

    // --- the probe -----------------------------------------------------------------

    /// <summary>
    /// <c>--new 12x10 --menu tools</c>: stage 5 through the tools' own calls.
    /// A pond preset of radius 1 (deep core, ford rim), dragged one cell on so
    /// its rim must not wash the first core back; a wooded hill; a swamp; a
    /// hex line of sand corner to corner (no gaps: each cell touches the next);
    /// a box of forest; the eyedropper on the pond and the hill; and the
    /// presets file's own names. With --hover the pond preset's ghost is left
    /// up for the capture.
    /// </summary>
    private void ToolsProbe()
    {
        if (_open != "tools" || _frame != 10)
            return;
        ReadPresets();
        GD.Print($"tools: {_presets.Count} preset(s) - {string.Join(", ", _presets.Select(p => p.Name))}"
                 + (_presetsSay.Length > 0 ? $"; {_presetsSay}" : ""));
        int Find(string name) => _presets.ToList().FindIndex(p => p.Name == name);
        // a fresh board opens on half a turn; the tools are judged alone
        _symmetry = MapEdit.Symmetry.None;

        _tab = MapEdit.Tab.Presets;
        _radius = 1;
        _preset = Find("pond");
        _under = new Vector2I(3, 3);
        Stroke(true);
        _under = new Vector2I(4, 3);
        Stroke(false);
        _edit.End();
        Settle();
        string Row(int r) => _edit.Ground()[r];
        GD.Print($"tools: pond dragged (3,3)->(4,3): rows 2-4 '{Row(2)}' '{Row(3)}' '{Row(4)}', "
                 + $"first core still deep: {_edit.LetterAt(new Vector2I(3, 3)) == BoardMap.DeepWet}, "
                 + $"fatal {_faults.Count(f => f.Fatal)}, undo {_edit.Depth}");

        _preset = Find("wooded hill");
        _under = new Vector2I(8, 2);
        Stroke(true);
        _edit.End();
        _preset = Find("swamp");
        _under = new Vector2I(8, 7);
        Stroke(true);
        _edit.End();
        Settle();
        GD.Print($"tools: wooded hill (8,2) '{_edit.LetterAt(new Vector2I(8, 2))}', swamp round (8,7) "
                 + $"'{string.Concat(_edit.Ring(new Vector2I(8, 7), 1).Select(_edit.LetterAt))}'");

        // a hex line corner to corner, with the Foundation tab's sand
        _tab = MapEdit.Tab.Foundation;
        _radius = 0;
        Floor(Foundation.Sand);
        _instrument = Instrument.Line;
        IReadOnlyList<Vector2I> line = Shaped(new Vector2I(0, 9), new Vector2I(11, 0));
        bool joined = line.Zip(line.Skip(1)).All(p => HexField.HeadingTo(p.First, p.Second) >= 0);
        Shape(line, erase: false, "line");
        GD.Print($"tools: line (0,9)->(11,0) {line.Count} cell(s), each touching the next {joined}, "
                 + $"sand on {line.Count(c => _edit.LetterAt(c) == BoardMap.Sand)} - "
                 + $"the rest '{string.Concat(line.Where(c => _edit.LetterAt(c) != BoardMap.Sand).Select(_edit.LetterAt))}' "
                 + $"(sand lies only on dry ground at the datum); said \"{_said}\"");

        _tab = MapEdit.Tab.Cover;
        _cover = Cover.Forest;
        _bunker = false;
        _instrument = Instrument.Box;
        Shape(Shaped(new Vector2I(0, 6), new Vector2I(2, 8)), erase: false, "box");
        GD.Print($"tools: box (0,6)-(2,8) of forest: '{_edit.Ground()[6][..3]}' '{_edit.Ground()[7][..3]}' '{_edit.Ground()[8][..3]}', undo {_edit.Depth}");

        _tab = MapEdit.Tab.Foundation;
        _instrument = Instrument.Pick;
        Pick(new Vector2I(3, 3));
        string fromPond = Tool();
        _tab = MapEdit.Tab.Level;
        Pick(new Vector2I(8, 2));
        GD.Print($"tools: picked off the pond '{fromPond}', off the hill '{Tool()}', tool now {Named(_instrument)}");

        _tab = MapEdit.Tab.Presets;
        _preset = Find("pond");
        _radius = 1;
        if (_hovered is Vector2I hover)
            _under = hover;
        Say();
    }
}
