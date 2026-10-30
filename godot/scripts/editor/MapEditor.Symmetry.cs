using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The symmetric brush (stage 3 of docs/editor-ux-plan.md): every stroke, fill
/// and tank also lands on its twin - half a turn about the board's middle, or a
/// mirror left to right or top to bottom (<see cref="MapEdit.Twin"/>).
///
/// <b>The twin of a tank is the other side's.</b> A green tank put on the
/// Units tab puts a brown one of the same class on the twin cell, facing the
/// turned way; erasing one erases both. A bunker's slit turns with it.
///
/// <b>Every board opens with it off</b> - new, opened or restored - and
/// <c>P</c> or the panel's switch turns it on (2026-10-26, the author's call:
/// it was read off the board, so a fresh one opened on half a turn and the
/// brush drew twice before anyone asked it to).
///
/// <b>The audit is a reading, not a rule.</b> With a symmetry up, the problems
/// panel says in how many cells the board breaks it, which cells have no twin
/// on a board of this size, and - once both sides have tanks - which homes have
/// none of the other side on their twin. None of it stops a save or a run.
/// </summary>
public sealed partial class MapEditor
{
    private MapEdit.Symmetry _symmetry;

    private static readonly Color AxisInk = new(1.0f, 0.88f, 0.35f, 0.55f);

    private static string Named(MapEdit.Symmetry how) => how switch
    {
        MapEdit.Symmetry.Point => "half a turn",
        MapEdit.Symmetry.Across => "mirror left-right",
        MapEdit.Symmetry.Down => "mirror top-bottom",
        _ => "off",
    };

    /// <summary>The twins of <paramref name="own"/> that are not among them -
    /// the cells a stroke writes the second time.</summary>
    private List<Vector2I> Twins(IReadOnlyCollection<Vector2I> own)
    {
        var twins = new List<Vector2I>();
        if (_symmetry == MapEdit.Symmetry.None)
            return twins;
        var mine = new HashSet<Vector2I>(own);
        foreach (Vector2I cell in own)
            if (_edit.Twin(cell, _symmetry) is Vector2I twin && mine.Add(twin))
                twins.Add(twin);
        return twins;
    }

    /// <summary>The brush on a twin: what <see cref="Write"/> does, with the
    /// tank of the other side and the slit turned.</summary>
    private (bool, string) WriteTwin(Vector2I twin)
    {
        string why;
        if (_tab == MapEdit.Tab.Units)
        {
            Vector2I of = _edit.Twin(twin, _symmetry) ?? twin;
            return (_edit.Pair(of, twin, _symmetry, out why), why);
        }
        if (_tab == MapEdit.Tab.Cover && _bunker)
            return (_edit.Over(twin, Cover.Walls, out why)
                    && _edit.Shelter(twin, true, MapEdit.Turned(_facing, _symmetry), out why), why);
        return Write(twin);
    }

    /// <summary>A tank made the brush's by a click on it (MapEditor.Tanks) -
    /// and its twin made the other side's.</summary>
    private void PairTwin(Vector2I of)
    {
        if (_symmetry == MapEdit.Symmetry.None || _edit.Twin(of, _symmetry) is not Vector2I twin || twin == of)
            return;
        if (!_edit.Pair(of, twin, _symmetry, out string why))
            _said = why;
    }

    /// <summary>P and the panel's switch: the next symmetry, off last.</summary>
    private void NextSymmetry() =>
        SetSymmetry((MapEdit.Symmetry)(((int)_symmetry + 1) % Enum.GetValues<MapEdit.Symmetry>().Length));

    private void SetSymmetry(MapEdit.Symmetry how)
    {
        _symmetry = how;
        _said = $"symmetry: {Named(how)}";
        Settle();
    }

    /// <summary>The audit's rows, added to the rules' faults by
    /// <see cref="Settle"/> - all of them warnings.</summary>
    private void Symmetric(List<MapRules.Fault> faults)
    {
        if (_symmetry == MapEdit.Symmetry.None)
            return;
        MapEdit.Asymmetry a = _edit.Audit(_symmetry);
        string how = Named(_symmetry);
        if (a.First is Vector2I first)
            faults.Add(new MapRules.Fault(first, "symmetry.cells",
                $"the board breaks {how} in {a.Cells} cell(s) - ({first.X},{first.Y}) differs from its twin",
                false, $"{a.Cells} cell(s) off {how}"));
        if (a.FirstLone is Vector2I lone)
            faults.Add(new MapRules.Fault(lone, "symmetry.lone",
                $"{a.Lone} cell(s) have no twin under {how} on a {_edit.Columns}x{_edit.Rows} board, "
                + $"({lone.X},{lone.Y}) first - the brush draws them alone; take them off the board or "
                + (_symmetry == MapEdit.Symmetry.Down ? "keep them in mind" : "make the width "
                    + (_symmetry == MapEdit.Symmetry.Point ? "even" : "odd")),
                false, $"{a.Lone} cell(s) without a twin"));
        // the tanks only once both sides field some: a board for one side
        // has no twins to keep
        string[] sides = Parking.SidesOf(_edit.Homes);
        if (a.FirstHome is Vector2I home && sides.Contains(Parking.Green) && sides.Contains(Parking.Brown))
            faults.Add(new MapRules.Fault(home, "symmetry.homes",
                $"{a.Homes} tank(s) have no tank of the other side on their twin under {how}, "
                + $"({home.X},{home.Y}) first", false, $"{a.Homes} tank(s) without a twin"));
    }

    /// <summary>Where the board turns or folds: a cross at the middle for half
    /// a turn, the axis for a mirror.</summary>
    private void DrawAxis(Overlay on)
    {
        if (_symmetry == MapEdit.Symmetry.None || _field.Atlas is null)
            return;
        // the middle of a cell and its twin lies on the axis
        Vector2? Mid(Vector2I cell)
        {
            if (!_edit.Inside(cell) || _edit.Twin(cell, _symmetry) is not Vector2I twin)
                return null;
            float lift = (_field.TopAt(cell) + _field.TopAt(twin)) * 0.5f;
            return Screen((Flat(cell) + Flat(twin)) * 0.5f, lift);
        }
        Vector2? a = null, b = null;
        switch (_symmetry)
        {
            case MapEdit.Symmetry.Point:
                if (Mid(new Vector2I(0, 0)) is Vector2 middle)
                {
                    float arm = 14.0f;
                    on.DrawLine(middle - new Vector2(arm, 0), middle + new Vector2(arm, 0), AxisInk, 3.0f);
                    on.DrawLine(middle - new Vector2(0, arm), middle + new Vector2(0, arm), AxisInk, 3.0f);
                    on.DrawArc(middle, arm, 0.0f, Mathf.Tau, 20, AxisInk, 2.0f);
                }
                return;
            case MapEdit.Symmetry.Across:
                a = Mid(new Vector2I(0, 0));
                b = Mid(new Vector2I(0, _edit.Rows - 1));
                break;
            case MapEdit.Symmetry.Down:
                a = Mid(new Vector2I(0, 0));
                b = Mid(new Vector2I((_edit.Columns - 1) & ~1, 0));
                break;
        }
        if (a is not Vector2 from || b is not Vector2 to || from.DistanceTo(to) < 1.0f)
            return;
        // run past both ends, so the axis crosses the whole board
        Vector2 way = (to - from).Normalized() * 60.0f;
        on.DrawDashedLine(from - way, to + way, AxisInk, 3.0f, 12.0f);
    }

    // --- the probe -------------------------------------------------------------

    /// <summary>
    /// <c>--new 11x10 --menu sym</c>: stage 3's "done when". First the maths:
    /// for each symmetry on three sizes, every cell's twin's twin is the cell
    /// and every neighbour's twin is the twin's neighbour across the turned
    /// edge. Then a board drawn through the brush itself - a pond, a wood, a
    /// hill and two green tanks, half a turn up - must audit at zero cells
    /// off, with the brown twins standing where they should.
    /// </summary>
    private void SymmetryProbe()
    {
        if (_open != "sym" || _frame != 10)
            return;
        foreach ((int w, int h) in new[] { (10, 10), (11, 10), (11, 9) })
        {
            MapEdit board = MapEdit.Blank("probe", w, h);
            var says = new List<string>();
            foreach (MapEdit.Symmetry how in new[] { MapEdit.Symmetry.Point, MapEdit.Symmetry.Across, MapEdit.Symmetry.Down })
            {
                int paired = 0, back = 0, parted = 0;
                for (int q = 0; q < w; q++)
                for (int r = 0; r < h; r++)
                {
                    var cell = new Vector2I(q, r);
                    if (board.Twin(cell, how) is not Vector2I twin)
                        continue;
                    paired++;
                    if (board.Twin(twin, how) != cell)
                        back++;
                    foreach (int edge in HexField.EdgeHeadings)
                    {
                        Vector2I next = HexField.Step(cell, edge);
                        if (board.Inside(next) && board.Twin(next, how) is Vector2I nextTwin
                            && HexField.Step(twin, MapEdit.Turned(edge, how)) != nextTwin)
                            parted++;
                    }
                }
                says.Add($"{how} {paired}/{w * h} paired, {back} not back, {parted} parted");
            }
            GD.Print($"sym: {w}x{h}: " + string.Join("; ", says));
        }

        GD.Print($"sym: opened {_edit.Columns}x{_edit.Rows} with symmetry {Named(_symmetry)}");
        SetSymmetry(MapEdit.Symmetry.Point);
        void Draw(MapEdit.Tab tab, Action brush, Vector2I at, int radius)
        {
            _tab = tab;
            brush();
            _radius = radius;
            _under = at;
            Stroke(true);
            _edit.End();
            Settle();
        }
        Draw(MapEdit.Tab.Foundation, () => { _water = true; _plotBrush = false; }, new Vector2I(2, 3), 1);
        Draw(MapEdit.Tab.Cover, () => { _cover = Cover.Forest; _bunker = false; }, new Vector2I(7, 2), 1);
        Draw(MapEdit.Tab.Level, () => { _level = 1; _rampBrush = false; }, new Vector2I(5, 6), 0);
        Draw(MapEdit.Tab.Units, () => { _unitSide = Parking.Green; _unitClass = "HTP"; }, new Vector2I(3, 8), 0);
        Draw(MapEdit.Tab.Units, () => { _unitSide = Parking.Green; _unitClass = "LTP"; }, new Vector2I(6, 7), 0);
        // the home a fresh board starts with has no twin: taken off, as an
        // author drawing two sides would
        _edit.Begin();
        _edit.Unhome(new Vector2I(_edit.Columns / 2, _edit.Rows - 1), out string _);
        _edit.End();
        Settle();
        MapEdit.Asymmetry a = _edit.Audit(MapEdit.Symmetry.Point);
        GD.Print($"sym: drawn: {a.Cells} cell(s) off half a turn, {a.Lone} without a twin, {a.Homes} tank(s) without one");
        GD.Print("sym: tanks " + string.Join(", ", _edit.Homes.Select(p => $"({p.Cell.X},{p.Cell.Y}) {p.Side} {p.Class} {p.Facing?.ToString() ?? "-"}")));
        GD.Print("sym: problems " + string.Join(" | ", _faults.Select(f => f.Rule + ": " + f.Text)));
        GD.Print("sym: board\n    " + string.Join("\n    ", _edit.Ground()));
        _tab = MapEdit.Tab.Foundation;
        _radius = 0;
        if (_hovered is Vector2I hover)
            _under = hover;
        Say();
    }
}
