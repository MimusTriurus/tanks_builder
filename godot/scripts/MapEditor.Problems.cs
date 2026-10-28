using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The audit as a list beside the board, not text on it (stage 0 of
/// docs/editor-ux-plan.md).
///
/// <b>It used to be a 520 x 420 readout over the board's top left</b>, every
/// fault and a reach line per home, and on a board with ten homes the lines
/// covered the cells they talked about. Now the faults are rows in a panel in
/// the bottom right, under the cell panel: red for what the harness would
/// refuse, amber for the rest, and a click on a row picks its cell and brings
/// the camera to it. On the board stays what was always there - the faults'
/// colour on their cells (<see cref="Overlay"/>).
///
/// <b>Reach is a summary unless something is stranded</b>: "every home reaches
/// all 100 cells" on one line, and a row per home only for a home that does
/// not. Still asked only while nothing is fatal, the old readout's reason - a
/// field carrying dropped ramps is a board the author is not drawing.
/// </summary>
public sealed partial class MapEditor
{
    private PanelContainer _problems = null!;
    private Button _problemsHead = null!;
    private ScrollContainer _problemsScroll = null!;
    private VBoxContainer _problemsList = null!;
    private bool _problemsOpen = true;

    /// <summary>The fault list the panel was last built for: <see cref="Settle"/>
    /// makes a new one on every change, and <see cref="Say"/> runs on every
    /// pointer move, which must not rebuild the rows or ask reach again.</summary>
    private IReadOnlyList<MapRules.Fault>? _problemsFor;
    private bool _problemsWasOpen;

    /// <summary>The list's height before it scrolls.</summary>
    private const float ProblemsHigh = 230.0f;

    private static readonly Color Fatal = new(1.0f, 0.5f, 0.45f);
    private static readonly Color Warning = new(1.0f, 0.82f, 0.45f);

    private void Problems(CanvasLayer layer)
    {
        _problems = new PanelContainer
        {
            CustomMinimumSize = new Vector2(RightWide, 0.0f),
        };
        var column = new VBoxContainer();
        _problemsHead = new Button
        {
            Flat = true,
            Alignment = HorizontalAlignment.Left,
            FocusMode = Control.FocusModeEnum.None,
        };
        _problemsHead.AddThemeFontSizeOverride("font_size", 14);
        _problemsHead.Pressed += () =>
        {
            _problemsOpen = !_problemsOpen;
            Problems();
        };
        _problemsScroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        _problemsList = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        _problemsList.AddThemeConstantOverride("separation", 2);
        _problemsScroll.AddChild(_problemsList);
        column.AddChild(_problemsHead);
        column.AddChild(_problemsScroll);
        _problems.AddChild(column);
        layer.AddChild(_problems);
    }

    /// <summary>The panel brought up to the board's faults: rebuilt when they
    /// or the fold changed, and stood in the bottom right either way.</summary>
    private void Problems()
    {
        if (!ReferenceEquals(_faults, _problemsFor) || _problemsOpen != _problemsWasOpen)
        {
            _problemsFor = _faults;
            _problemsWasOpen = _problemsOpen;
            Rows();
        }
        // As tall as the rows, up to a height, then a scroll: a short list
        // does not stand a tall empty box over the board. Asked every time
        // rather than once after Rows: a wrapped row knows its height only
        // after a layout pass, and asked at once it gave one line.
        float tall = Math.Min(_problemsList.GetCombinedMinimumSize().Y, ProblemsHigh);
        if (!Mathf.IsEqualApprox(_problemsScroll.CustomMinimumSize.Y, tall))
            _problemsScroll.CustomMinimumSize = new Vector2(RightWide - 8.0f, tall);
        Vector2 window = GetViewportRect().Size;
        Vector2 size = _problems.GetCombinedMinimumSize();
        _problems.Size = size;
        _problems.Position = new Vector2(window.X - RightWide - 12.0f,
                                         window.Y - size.Y - 12.0f);
    }

    private void Rows()
    {
        foreach (Node was in _problemsList.GetChildren())
        {
            _problemsList.RemoveChild(was);
            was.QueueFree();
        }
        var rows = new List<(string Text, Vector2I Cell, Color Ink)>();
        foreach (MapRules.Fault fault in _faults.Where(f => f.Fatal))
            rows.Add((fault.Text, fault.Cell, Fatal));
        foreach (MapRules.Fault fault in _faults.Where(f => !f.Fatal))
            rows.Add((fault.Text, fault.Cell, Warning));
        int fatal = _faults.Count(f => f.Fatal), warned = _faults.Count - fatal;

        string reach = "";
        if (fatal == 0)
        {
            MapRules.Draft draft = _edit.Draft();
            int cells = draft.Cells().Count(c => !draft.IsCliff(c));
            int alone = 0;
            foreach (Parking parked in _edit.Homes)
            {
                Vector2I home = parked.Cell;
                HashSet<Vector2I> seen = MapRules.Seen(_field, home);
                int stranded = draft.Cells().Count(c => !draft.IsCliff(c) && !seen.Contains(c));
                if (stranded == 0)
                    continue;
                alone++;
                rows.Add(($"from the home at ({home.X},{home.Y}) {stranded} cell(s) "
                          + "can't be reached", home, Warning));
            }
            reach = alone == 0 && _edit.Homes.Count > 0
                ? $"every home reaches all {cells} cells"
                : "";
        }
        else
        {
            reach = "reach is checked once nothing is red";
        }

        string head = fatal == 0 && warned == 0 && rows.Count == 0
            ? "No problems"
            : "Problems: " + string.Join(", ", new[] { Count(fatal, "error"), Count(rows.Count - fatal, "warning") }.Where(s => s.Length > 0));
        _problemsHead.Text = (_problemsOpen ? "v  " : ">  ") + head;
        _problemsHead.AddThemeColorOverride("font_color",
            fatal > 0 ? Fatal : rows.Count > 0 ? Warning : new Color(0.75f, 0.95f, 0.75f));

        _problemsScroll.Visible = _problemsOpen;
        if (!_problemsOpen)
            return;
        foreach ((string text, Vector2I cell, Color ink) in rows)
        {
            var row = new Button
            {
                Text = text,
                Flat = true,
                Alignment = HorizontalAlignment.Left,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                CustomMinimumSize = new Vector2(RightWide - 28.0f, 0.0f),
                FocusMode = Control.FocusModeEnum.None,
                TooltipText = "show this cell",
            };
            row.AddThemeFontSizeOverride("font_size", 12);
            row.AddThemeColorOverride("font_color", ink);
            row.AddThemeColorOverride("font_hover_color", ink.Lightened(0.3f));
            Vector2I at = cell;
            row.Pressed += () => ShowCell(at);
            _problemsList.AddChild(row);
        }
        if (reach.Length > 0)
        {
            var note = new Label
            {
                Text = reach,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                CustomMinimumSize = new Vector2(RightWide - 28.0f, 0.0f),
                Modulate = new Color(1, 1, 1, 0.7f),
            };
            note.AddThemeFontSizeOverride("font_size", 12);
            _problemsList.AddChild(note);
        }
    }

    /// <summary><c>--menu problem</c>: the first row pressed on frame 6, and
    /// on frame 8 where its cell landed against the band's middle - the click
    /// a capture run has nobody to make.</summary>
    private void ProbeProblems()
    {
        if (_open != "problem")
            return;
        if (_frame == 6 && _problemsList.GetChildCount() > 0
            && _problemsList.GetChild(0) is Button first)
        {
            GD.Print($"problem: pressing \"{first.Text}\"");
            first.EmitSignal(BaseButton.SignalName.Pressed);
        }
        if (_frame == 8)
        {
            Vector2 window = GetViewportRect().Size;
            float left = LeftWide + 24.0f, right = RightWide + 24.0f;
            var middle = new Vector2(left + (window.X - left - right) * 0.5f,
                                     PanelTop + (window.Y - PanelTop) * 0.5f);
            GD.Print($"problem: picked ({_picked.X},{_picked.Y}), on screen at "
                     + $"{TopOnScreen(_picked)}, the band's middle {middle}");
        }
    }

    private static string Count(int n, string what) =>
        n == 0 ? "" : n == 1 ? $"1 {what}" : $"{n} {what}s";

    /// <summary>A problem's cell picked and brought to the middle of the band
    /// between the panels, at the zoom and the turn the author has.</summary>
    private void ShowCell(Vector2I cell)
    {
        if (!_edit.Inside(cell))
            return;
        _picked = cell;
        Vector2 window = GetViewportRect().Size;
        float left = LeftWide + 24.0f, right = RightWide + 24.0f;
        var middle = new Vector2(left + (window.X - left - right) * 0.5f,
                                 PanelTop + (window.Y - PanelTop) * 0.5f);
        // A drag moves the board with the pointer (camera -= Panned), so the
        // cell comes to the middle by the camera moving the other way.
        _camera.Position += Panned(TopOnScreen(cell) - middle);
        _said = $"({cell.X},{cell.Y}) picked from the problems";
        Say();
    }
}
