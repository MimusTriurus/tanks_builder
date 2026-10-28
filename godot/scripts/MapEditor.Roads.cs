using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The road tool (docs/editor.md, «Дорога по точкам»): the author puts points
/// on hexes, and the road between each two is laid by itself - the cheapest
/// chain of cells a road may cross, straight where it can be and bending
/// gently (<see cref="Road.Route"/>), as Cities: Skylines and the hex map
/// tools lay theirs. The cells a hand drags across were the first way, and a
/// shaking mouse made every one of them a bend.
///
/// <b>What the author sees is the road it will be.</b> The road being drawn
/// is laid on the board as a ghost of the real thing (<see cref="Stage3D.RedrawRoads"/>),
/// with the points numbered over it, its axis, a 120 degree bend marked - a
/// tank pays a turn there - and, where the next point cannot be reached, the
/// cell in the way and why.
///
/// <b>A laid road keeps handles</b>: its ends and the cells it turns in.
/// Dragging one lays the road again through the new place; pressing on the
/// road between handles and dragging puts a new one there. A press that does
/// not move starts a new road from that cell - a fork.
/// </summary>
public sealed partial class MapEditor
{
    /// <summary>The points of the road being drawn or laid again.</summary>
    private readonly List<Vector2I> _roadPoints = new();

    /// <summary>The road being laid again by its handles, or -1 for a new one.</summary>
    private int _roadEditing = -1;

    /// <summary>The point the pointer holds, or -1.</summary>
    private int _roadHolding = -1;

    /// <summary>Where the press was and whether the pointer has moved since.</summary>
    private Vector2I _roadPress;
    private bool _roadPressed, _roadMoved, _roadOnRoad;

    /// <summary>The road the points lay, and where and why it stops short.</summary>
    private List<Vector2I> _roadPath = new();
    private Vector2I? _roadBlock;
    private string _roadWhy = "";

    private bool RoadTool =>
        _tab == MapEdit.Tab.Roads && !Selecting && _held is null;

    /// <summary>The pointer, the buttons and the keys while the road tool is
    /// up; false when it is not, or for what it leaves to the rest.</summary>
    private bool RoadInput(InputEvent what)
    {
        if (!RoadTool)
        {
            if (_roadPoints.Count > 0)
                RoadCancel();
            return false;
        }
        switch (what)
        {
            case InputEventMouseMotion:
            {
                Vector2I was = _under;
                _under = Pointed();
                if (_roadPressed && _under != _roadPress)
                    _roadMoved = true;
                if (_roadHolding >= 0 && _roadMoved && _edit.Inside(_under))
                    _roadPoints[_roadHolding] = _under;
                if (was != _under)
                    RoadLay();
                return true;
            }
            case InputEventMouseButton { ButtonIndex: MouseButton.Left } click:
                if (click.Pressed)
                    RoadPress(click.DoubleClick);
                else
                    RoadRelease();
                return true;
            case InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: true }:
                if (_roadPoints.Count > 0)
                    RoadBack();
                else if (_edit.Inside(_under) && _edit.RoadsAt(_under).Count > 0)
                {
                    _edit.Begin();
                    _edit.Cut(_under);
                    _edit.End();
                    _dirty = true;
                    _said = $"roads cut at ({_under.X},{_under.Y})";
                    Settle();
                }
                return true;
            case InputEventKey { Pressed: true, Echo: false } key:
                switch (key.Keycode)
                {
                    case Key.Enter or Key.KpEnter when _roadPoints.Count > 0:
                        RoadCommit();
                        return true;
                    case Key.Backspace when _roadPoints.Count > 0:
                        RoadBack();
                        return true;
                    case Key.Escape when _roadPoints.Count > 0:
                        RoadCancel();
                        _said = "road dropped";
                        Say();
                        return true;
                }
                return false;
        }
        return false;
    }

    private void RoadPress(bool twice)
    {
        if (!_edit.Inside(_under))
            return;
        _roadPress = _under;
        _roadPressed = true;
        _roadMoved = false;
        _roadOnRoad = false;
        if (twice && _roadPoints.Count > 0)
        {
            // the second click of a double one ends the road on its cell
            if (_roadPoints[^1] != _under)
                _roadPoints.Add(_under);
            RoadLay();
            RoadCommit();
            _roadPressed = false;
            return;
        }
        if (_roadPoints.Count > 0)
            return;
        // a press on a laid road: its handle, or a new handle between two -
        // which a press that does not move takes back for a fork (RoadRelease)
        List<int> here = _edit.RoadsAt(_under);
        if (here.Count == 0)
            return;
        _roadOnRoad = true;
        int road = here[0];
        List<Vector2I> handles = Handles(_edit.Roads[road].Cells);
        _roadEditing = road;
        _roadPoints.AddRange(handles);
        _roadHolding = handles.IndexOf(_under);
        if (_roadHolding < 0)
        {
            // between which two handles the cell lies, along the road
            IReadOnlyList<Vector2I> cells = _edit.Roads[road].Cells;
            int at = cells.ToList().IndexOf(_under), after = 0;
            for (int h = 0; h < handles.Count; h++)
                if (cells.ToList().IndexOf(handles[h]) < at)
                    after = h;
            _roadPoints.Insert(after + 1, _under);
            _roadHolding = after + 1;
        }
        _roadType = _edit.Roads[road].Type;
    }

    /// <summary>The type the road being laid again keeps, the brush's
    /// otherwise.</summary>
    private RoadType? _roadType;

    private void RoadRelease()
    {
        if (!_roadPressed)
            return;
        _roadPressed = false;
        if (_roadHolding >= 0)
        {
            int held = _roadHolding;
            _roadHolding = -1;
            if (_roadMoved)
            {
                RoadLay();
                RoadCommit();
                return;
            }
            // a press on a road that did not move: a new road from there
            Vector2I from = _roadPoints[held];
            RoadCancel();
            if (_roadOnRoad)
            {
                _roadPoints.Add(from);
                _said = $"a fork from ({from.X},{from.Y}) - click where it goes, Enter to lay";
                RoadLay();
            }
            return;
        }
        if (!_edit.Inside(_roadPress))
            return;
        if (_roadPoints.Count == 0 || _roadPoints[^1] != _roadPress)
            _roadPoints.Add(_roadPress);
        _said = _roadPoints.Count == 1
            ? "click the next point; double click or Enter lays the road, Backspace takes a point back, Esc drops it"
            : "";
        RoadLay();
    }

    /// <summary>The last point taken back; the road dropped with its first.</summary>
    private void RoadBack()
    {
        _roadPoints.RemoveAt(_roadPoints.Count - 1);
        if (_roadPoints.Count == 0 || (_roadEditing >= 0 && _roadPoints.Count < 2))
            RoadCancel();
        else
            RoadLay();
    }

    private void RoadCancel()
    {
        _roadPoints.Clear();
        _roadEditing = -1;
        _roadHolding = -1;
        _roadType = null;
        _roadPath = new List<Vector2I>();
        _roadBlock = null;
        _roadWhy = "";
        RoadShow(null);
        _marks.QueueRedraw();
    }

    /// <summary>A road's handles: its ends and the cells it turns in.</summary>
    private static List<Vector2I> Handles(IReadOnlyList<Vector2I> cells)
    {
        var handles = new List<Vector2I> { cells[0] };
        for (int i = 1; i + 1 < cells.Count; i++)
            if (HexField.HeadingTo(cells[i - 1], cells[i]) != HexField.HeadingTo(cells[i], cells[i + 1]))
                handles.Add(cells[i]);
        handles.Add(cells[^1]);
        return handles;
    }

    /// <summary>
    /// The road the points lay - and, while new points are being put, on to
    /// the cell under the pointer - routed leg by leg, each leg leaving its
    /// point the way the last one came in. Where a leg cannot be laid the road
    /// stops at its start, and the cell in the way and the reason are kept.
    /// Shown on the board as the ghost.
    /// </summary>
    private void RoadLay()
    {
        var points = new List<Vector2I>(_roadPoints);
        if (_roadHolding < 0 && points.Count > 0 && _edit.Inside(_under) && points[^1] != _under
            && !_roadPressed)
            points.Add(_under);
        var others = new HashSet<(Vector2I, Vector2I)>();
        for (int k = 0; k < _edit.Roads.Count; k++)
        {
            if (k == _roadEditing)
                continue;
            IReadOnlyList<Vector2I> c = _edit.Roads[k].Cells;
            for (int i = 1; i < c.Count; i++)
            {
                others.Add((c[i - 1], c[i]));
                others.Add((c[i], c[i - 1]));
            }
        }
        var path = new List<Vector2I>();
        _roadBlock = null;
        _roadWhy = "";
        if (points.Count > 0)
            path.Add(points[0]);
        if (points.Count > 0 && _edit.RoadRefuses(points[0]) is { } first)
        {
            _roadBlock = points[0];
            _roadWhy = $"({points[0].X},{points[0].Y}) is {first}";
            path.Clear();
        }
        for (int p = 1; p < points.Count && _roadBlock is null; p++)
        {
            int? heading = path.Count >= 2 ? HexField.HeadingTo(path[^2], path[^1]) : null;
            List<Vector2I>? leg = Road.Route(path[^1], heading, points[p],
                c => _edit.RoadRefuses(c) is null, (a, b) => others.Contains((a, b)));
            if (leg is null)
            {
                // the first cell on the straight line that no road may cross
                Vector2I block = points[p];
                foreach (Vector2I c in MapEdit.Line(path[^1], points[p]))
                    if (_edit.RoadRefuses(c) is not null)
                    {
                        block = c;
                        break;
                    }
                _roadBlock = block;
                _roadWhy = _edit.RoadRefuses(block) is { } why
                    ? $"no road to ({points[p].X},{points[p].Y}): ({block.X},{block.Y}) is {why}"
                    : $"no road to ({points[p].X},{points[p].Y}) that keeps off the other roads";
                break;
            }
            for (int i = 1; i < leg.Count; i++)
                path.Add(leg[i]);
        }
        _roadPath = path;
        // a road over itself is the map's refusal too - said, not laid
        if (_roadBlock is null && path.Count >= 2)
        {
            var seen = new HashSet<Vector2I>();
            foreach (Vector2I c in path)
                if (!seen.Add(c))
                {
                    _roadBlock = c;
                    _roadWhy = $"the road crosses ({c.X},{c.Y}) twice";
                    break;
                }
        }
        RoadShow(path.Count >= 2 ? path : null);
        _marks.QueueRedraw();
        Say();
    }

    /// <summary>The board's roads with the one being drawn as the ghost - the
    /// road being laid again out of the list while it is - or the board's own.</summary>
    private void RoadShow(List<Vector2I>? ghost)
    {
        if (_stage is null || _ring is not { } ring)
            return;
        var roads = new List<RoadPath>();
        for (int k = 0; k < _edit.Roads.Count; k++)
            if (ghost is null || k != _roadEditing)
                roads.Add(_edit.Roads[k].Moved(ring.Shift));
        int from = roads.Count;
        if (ghost is not null)
            roads.Add(new RoadPath(_roadType ?? _roadBrush, ghost).Moved(ring.Shift));
        _board.Roads = roads;
        _stage.RedrawRoads(ghost is null ? int.MaxValue : from);
    }

    /// <summary>The road laid as drawn, in place of the one laid again - or
    /// the reason it is not.</summary>
    private void RoadCommit()
    {
        _roadHolding = -1;
        var points = new List<Vector2I>(_roadPoints);
        RoadLayPointsOnly();
        if (_roadBlock is not null || _roadPath.Count < 2)
        {
            _said = _roadWhy.Length > 0 ? _roadWhy : "a road joins two cells - put a second point";
            Say();
            return;
        }
        _edit.Begin();
        int laid = _edit.Pave(_roadPath, _roadType ?? _roadBrush, _roadEditing, out string why);
        _edit.End();
        if (laid < 0)
        {
            _said = why;
            Say();
            return;
        }
        _said = $"road {laid + 1} laid, {_roadPath.Count} cells through {points.Count} points";
        _dirty = true;
        RoadCancel();
        Settle();
    }

    /// <summary><see cref="RoadLay"/> for the points alone, not on to the
    /// pointer: what Enter and a release lay.</summary>
    private void RoadLayPointsOnly()
    {
        bool pressed = _roadPressed;
        _roadPressed = true;
        RoadLay();
        _roadPressed = pressed;
    }

    // --- what is drawn over the board ------------------------------------------

    private static readonly Color RoadInk = new(1.0f, 1.0f, 1.0f, 0.9f);
    private static readonly Color RoadHandle = new(0.55f, 0.85f, 1.0f, 0.95f);
    private static readonly Color RoadBend = new(1.0f, 0.75f, 0.25f, 0.95f);
    private static readonly Color RoadNo = new(1.0f, 0.35f, 0.3f, 0.95f);

    /// <summary>
    /// The road tool over the board: the road being drawn - its axis, its
    /// points numbered, its 120 degree bends, the cell in the way - or, with
    /// no road being drawn, the laid roads under the pointer with their
    /// handles. True while the tool is up, so the brush's ghost is not drawn.
    /// </summary>
    private bool RoadGhost(Overlay on)
    {
        if (!RoadTool)
            return false;
        Font font = ThemeDB.FallbackFont;
        if (_roadPoints.Count == 0)
        {
            foreach (int k in _edit.RoadsAt(_under))
            {
                IReadOnlyList<Vector2I> cells = _edit.Roads[k].Cells;
                Axis(on, cells, RoadHandle, 3.0f);
                foreach (Vector2I h in Handles(cells))
                {
                    Vector2 at = TopOnScreen(h);
                    on.DrawRect(new Rect2(at - new Vector2(6, 6), new Vector2(12, 12)), RoadHandle);
                }
                on.DrawString(font, TopOnScreen(_under) + new Vector2(16.0f, -16.0f),
                              $"road {k + 1}: {Road.Say(_edit.Roads[k].Type)}, {cells.Count} cells - drag a handle, click for a fork",
                              HorizontalAlignment.Left, -1, 14, RoadHandle);
            }
            if (_edit.Inside(_under))
                on.DrawArc(TopOnScreen(_under), 9.0f, 0.0f, Mathf.Tau, 24, RoadInk, 2.0f);
            return true;
        }
        Axis(on, _roadPath, RoadInk, 2.5f);
        // a 120 degree bend: a turn a tank pays for
        for (int i = 1; i + 1 < _roadPath.Count; i++)
        {
            int a = HexField.HeadingTo(_roadPath[i - 1], _roadPath[i]);
            int b = HexField.HeadingTo(_roadPath[i], _roadPath[i + 1]);
            if (Mathf.Abs(Mathf.PosMod(b - a + 180, 360) - 180) == 120)
                on.DrawCircle(TopOnScreen(_roadPath[i]), 6.0f, RoadBend);
        }
        if (_roadBlock is Vector2I block)
        {
            Vector2 at = TopOnScreen(block);
            if (_roadPath.Count > 0)
                Dashed(on, TopOnScreen(_roadPath[^1]), at, RoadNo);
            on.DrawArc(at, 13.0f, 0.0f, Mathf.Tau, 28, RoadNo, 3.0f);
            on.DrawLine(at + new Vector2(-8, -8), at + new Vector2(8, 8), RoadNo, 3.0f);
            on.DrawLine(at + new Vector2(-8, 8), at + new Vector2(8, -8), RoadNo, 3.0f);
            on.DrawString(font, at + new Vector2(16.0f, 20.0f), _roadWhy, HorizontalAlignment.Left, -1, 14, RoadNo);
        }
        for (int p = 0; p < _roadPoints.Count; p++)
        {
            Vector2 at = TopOnScreen(_roadPoints[p]);
            bool end = p == 0 || p == _roadPoints.Count - 1;
            float radius = end ? 12.0f : 9.0f;
            on.DrawCircle(at, radius, new Color(0.1f, 0.1f, 0.12f, 0.85f));
            on.DrawArc(at, radius, 0.0f, Mathf.Tau, 28, p == _roadHolding ? RoadBend : RoadInk, 2.0f);
            on.DrawString(font, at + new Vector2(-4.0f, 5.0f), $"{p + 1}", HorizontalAlignment.Left, -1, 13, RoadInk);
        }
        return true;
    }

    /// <summary>A road's axis on the screen, through the middles of the sides
    /// it crosses and of its end cells.</summary>
    private void Axis(Overlay on, IReadOnlyList<Vector2I> cells, Color ink, float width)
    {
        if (cells.Count < 2)
            return;
        var line = new List<Vector2> { TopOnScreen(cells[0]) };
        for (int i = 1; i < cells.Count; i++)
        {
            Vector2 a = TopOnScreen(cells[i - 1]), b = TopOnScreen(cells[i]);
            line.Add((a + b) * 0.5f);
        }
        line.Add(TopOnScreen(cells[^1]));
        on.DrawPolyline(line.ToArray(), ink, width, true);
    }

    private static void Dashed(Overlay on, Vector2 a, Vector2 b, Color ink)
    {
        float length = a.DistanceTo(b);
        for (float t = 0.0f; t < length; t += 12.0f)
            on.DrawLine(a.Lerp(b, t / length), a.Lerp(b, Mathf.Min(t + 6.0f, length) / length), ink, 2.0f);
    }
}
