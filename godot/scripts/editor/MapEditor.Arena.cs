using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The Match tab (docs/battle-core-plan.md, Д9; the author's Р-5): the sides'
/// deployment zones and the control point (<see cref="Arena"/>, format 3's
/// <c>zones</c> and <c>point</c>).
/// <list type="bullet">
/// <item><b>A zone is painted</b>, a cell at a time, with the radius, the
/// line, the box and the fill like any brush; a cell in the other side's zone
/// moves over. Under a symmetry the twin goes to the other side's zone.</item>
/// <item><b>The point is put by a click</b>, as a tank is: one hex, or the hex
/// with its ring. It keeps its points; the first one starts at
/// <see cref="MapEdit.PointScore"/>.</item>
/// <item><b>The eraser</b> takes a cell out of its zone, else the point off
/// when the cell is one of its hexes.</item>
/// <item><b>The right panel</b> says the picked cell's zone and, on the point,
/// its ring and its points (N).</item>
/// </list>
/// Drawn over the board with the homes (the <c>homes</c> layer): a zone's
/// rim in its side's ink over a faint fill, the point's rim in gold with N in
/// its middle.
/// </summary>
public sealed partial class MapEditor
{
    private enum ArenaBrush { GreenZone, BrownZone, Point, Ring }

    private ArenaBrush _arenaBrush = ArenaBrush.GreenZone;

    private static readonly Color PointInk = new(1.0f, 0.86f, 0.35f);

    /// <summary>The point brush is a click, not a stroke: a drag would carry
    /// the point along every cell it crossed.</summary>
    private bool ArenaClicks => _tab == MapEdit.Tab.Match && _arenaBrush is ArenaBrush.Point or ArenaBrush.Ring;

    private IReadOnlyList<(string Name, Action Pick, Func<bool> On)> ArenaBrushes() =>
        new (string, Action, Func<bool>)[]
        {
            ("green zone", () => _arenaBrush = ArenaBrush.GreenZone, () => _arenaBrush == ArenaBrush.GreenZone),
            ("brown zone", () => _arenaBrush = ArenaBrush.BrownZone, () => _arenaBrush == ArenaBrush.BrownZone),
            ("control point  one hex", () => _arenaBrush = ArenaBrush.Point, () => _arenaBrush == ArenaBrush.Point),
            ("control point  with its ring", () => _arenaBrush = ArenaBrush.Ring, () => _arenaBrush == ArenaBrush.Ring),
        };

    /// <summary>The brush on one cell.</summary>
    private bool ArenaWrite(Vector2I cell, bool twin, out string why) => _arenaBrush switch
    {
        ArenaBrush.GreenZone => _edit.Zone(cell, twin ? Parking.Brown : Parking.Green, out why),
        ArenaBrush.BrownZone => _edit.Zone(cell, twin ? Parking.Green : Parking.Brown, out why),
        // the point is one: its twin is the point itself, and nothing to write
        _ when twin => !Refuse("", out why),
        _ => _edit.Point(cell, _arenaBrush == ArenaBrush.Ring, out why),
    };

    private string ArenaLaid() => _arenaBrush switch
    {
        ArenaBrush.GreenZone => "zone: green",
        ArenaBrush.BrownZone => "zone: brown",
        ArenaBrush.Point => "control point: one hex - click",
        _ => "control point: with its ring - click",
    };

    /// <summary>The right panel's word on the picked cell: its zone, and the
    /// point - put here, or its ring and points when the cell is one of its hexes.</summary>
    private void Staked(VBoxContainer into)
    {
        into.AddChild(Heading("Match"));
        string[] zones = { "none", Parking.Green, Parking.Brown };
        var pick = new OptionButton();
        foreach (string z in zones)
            pick.AddItem(z);
        pick.Selected = Math.Max(0, Array.IndexOf(zones, _edit.Arena.ZoneAt(_picked) ?? "none"));
        Pick(pick, index => _edit.Zone(_picked, index == 0 ? null : zones[index], out string why) ? "" : why);
        into.AddChild(Field("zone", pick));
        if (_edit.Arena.Point is { } p && p.Holds(_picked))
        {
            into.AddChild(Note(p.Cell == _picked ? "the control point" : $"the control point's ring, its middle ({p.Cell.X},{p.Cell.Y})"));
            into.AddChild(Ticked("with its ring", p.Ring,
                on => _edit.Point(p.Cell, on, out string why) ? "" : why));
            var score = new SpinBox { MinValue = ControlPoint.Least, MaxValue = 99, Step = 1, Value = p.Score };
            score.ValueChanged += value =>
            {
                if (!_loading)
                    Wrote(() => _edit.Score((int)value, out string why) ? "" : why);
            };
            into.AddChild(Field("points", score));
            into.AddChild(Does("take the point off", () =>
            {
                _edit.Erase(p.Cell, MapEdit.Tab.Match, out string why);
                return "";
            }));
        }
        else
            into.AddChild(Does(_edit.Arena.Point is null ? "put the control point here" : "move the control point here",
                () => _edit.Point(_picked, _edit.Arena.Point?.Ring ?? false, out string why) ? "" : why));
    }

    /// <summary>The zones and the point over the board (the marks' layer).</summary>
    private void DrawArena(CanvasItem on)
    {
        Arena arena = _edit.Arena;
        if (arena.Plain || !_layers.HasFlag(Layers.Homes))
            return;
        foreach (string side in Parking.Sides)
        {
            IReadOnlyList<Vector2I> zone = arena.Zone(side);
            Color ink = side == Parking.Green ? GreenInk : BrownInk;
            foreach (Vector2I cell in zone)
            {
                if (!_edit.OnBoard(cell))
                    continue;
                on.DrawColoredPolygon(TopHex(cell), new Color(ink, 0.18f));
                Rim(on, cell, c => arena.ZoneAt(c) == side, ink, 3.0f);
            }
        }
        if (arena.Point is not { } p)
            return;
        foreach (Vector2I cell in p.Cells())
            if (_edit.OnBoard(cell))
            {
                on.DrawColoredPolygon(TopHex(cell), new Color(PointInk, 0.16f));
                Rim(on, cell, p.Holds, PointInk, 4.0f);
            }
        if (_edit.OnBoard(p.Cell))
            on.DrawString(ThemeDB.FallbackFont, TopOnScreen(p.Cell) + new Vector2(-10.0f, 6.0f),
                p.Score.ToString(), HorizontalAlignment.Left, -1, 18, PointInk);
    }

    /// <summary><c>--menu arena</c>: key 8, a green zone and a brown one laid
    /// by a click with the radius, the point with its ring by a click, a cell
    /// erased, an undo - each said, the faults with it; the capture shows the
    /// board as it ends.</summary>
    private void ArenaProbe()
    {
        if (_open != "arena")
            return;
        void Click(Vector2I at)
        {
            _under = at;
            foreach (bool down in new[] { true, false })
                Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = down });
        }
        string Faults() => string.Join("; ", MapRules.ArenaFaults(_edit.Draft()).Select(f => $"{f.Rule} {f.Brief}"));
        switch (_frame)
        {
            case 4:
                Input.ParseInputEvent(new InputEventKey { Keycode = Key.Key8, Pressed = true });
                Input.ParseInputEvent(new InputEventKey { Keycode = Key.Key8, Pressed = false });
                break;
            case 6:
                GD.Print($"arena: tab {_tab}, {_picks.Count} brushes, says '{Laid()}'; green zone r1 at (2,8)");
                _radius = 1;
                _arenaBrush = ArenaBrush.GreenZone;
                Click(new Vector2I(2, 8));
                break;
            case 8:
                GD.Print($"arena: {_edit.Arena}; brown zone r1 at (6,2)");
                _arenaBrush = ArenaBrush.BrownZone;
                Click(new Vector2I(6, 2));
                break;
            case 10:
                GD.Print($"arena: {_edit.Arena}; the point with its ring at (4,5)");
                _arenaBrush = ArenaBrush.Ring;
                Click(new Vector2I(4, 5));
                break;
            case 12:
                GD.Print($"arena: {_edit.Arena}; faults {Faults()}; erase (2,9)");
                _under = new Vector2I(2, 9);
                Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Right, Pressed = true });
                Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Right, Pressed = false });
                break;
            case 14:
                GD.Print($"arena: {_edit.Arena}; undo");
                _edit.Undo();
                Settle();
                break;
            case 16:
                GD.Print($"arena: {_edit.Arena}; undo depth {_edit.Depth}");
                GD.Print("arena: the draft writes " + string.Join(" ", MapFile.WriteDraft(_edit)
                    .Split('\n').Where(l => l.Contains("zones") || l.Contains("green") || l.Contains("brown") || l.Contains("point"))
                    .Select(l => l.Trim())));
                break;
        }
    }

    /// <summary>A cell's edges toward cells outside the region.</summary>
    private void Rim(CanvasItem on, Vector2I cell, Func<Vector2I, bool> inside, Color ink, float width)
    {
        foreach (int h in HexField.EdgeHeadings)
            if (!inside(HexField.Step(cell, h)))
            {
                (Vector2 a, Vector2 b) = EdgeOnScreen(cell, h);
                on.DrawLine(a, b, ink, width);
            }
    }

    /// <summary>A cell's top hexagon on the screen, its corners in order.</summary>
    private Vector2[] TopHex(Vector2I cell)
    {
        var corners = new Vector2[6];
        foreach (int h in HexField.EdgeHeadings)
            corners[HexField.EdgeIndex(h)] = EdgeOnScreen(cell, h).A;
        return corners;
    }
}
