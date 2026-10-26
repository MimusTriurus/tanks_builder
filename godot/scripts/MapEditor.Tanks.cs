using System;
using System.Collections.Generic;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The tanks on the homes: the model the match will stand on each, in its
/// side's paint, facing the way the home says - and turned with the mouse.
///
/// <b>The match's line-up, not a guess at it.</b> Who stands where is
/// <see cref="Lineup.Of"/>, the call <c>Tank3DBench.Match</c> makes, so the
/// board drawn is the board the match opens on: a home past five on its side
/// stands empty here too.
///
/// <b>Loaded once per side and model, one a frame.</b> A side fields each
/// model once, so a board needs ten loads at most, and a load is a good part of
/// a second; each loaded model is kept in <see cref="_pool"/> and moved when its
/// home moves or turns, never loaded again. The loads are spread one a frame so
/// the board opens at once and the tanks come up after it.
///
/// <b>Turned like Blender's R, on the edges.</b> Alt and a left drag from a
/// home turn its tank towards the pointer; it lands on the nearest of the six
/// edges, since a tank on a hex faces one (<see cref="HexField.EdgeHeadings"/>).
/// Ctrl and the wheel over a home turn it an edge either way. Each turn is one
/// undo, through <see cref="MapEdit.Face"/> like the panel's dropdown.
/// </summary>
public sealed partial class MapEditor
{
    private bool _tanksStale = true, _tanksShown = true;

    /// <summary>The Units tab's brush: which side and class a click puts.</summary>
    private string _unitSide = Parking.Green, _unitClass = "LTP";

    /// <summary>The frame the tanks last stood - a capture waits a few past
    /// it, as it does past the dressing.</summary>
    private int _tankedFrame = int.MaxValue / 2;

    /// <summary>Every model loaded, by side and tag; hidden while no home wants it.</summary>
    private readonly Dictionary<string, (Node3D Rig, TankModel Model)> _pool = new();

    /// <summary>Tags that would not load, so they are not tried every frame.</summary>
    private readonly HashSet<string> _unloadable = new();

    /// <summary>The home being turned by an Alt drag, or -1.</summary>
    private int _turning = -1;

    private static string PoolKey(Lineup.Unit u) => u.Side + "/" + u.Model;

    /// <summary>Stand the line-up when the board moved: each unit's pooled
    /// model on its home, the rest hidden. A model not loaded yet is loaded -
    /// one a frame - and the frame after stands it.</summary>
    private void Tanks()
    {
        bool shown = _layers.HasFlag(Layers.Homes);
        // the homes layer hides them with the rings, and its toggle settles nothing
        _tanksStale |= shown != _tanksShown;
        if (!_tanksStale || _stage is null || _field.Atlas is null)
            return;
        _tanksStale = false;
        _tanksShown = shown;
        _tankedFrame = _frame;
        float squash = _field.Squash, rise = _field.RiseFactor;
        float hex = _field.Atlas.HexRect.Size.X;
        var wanted = new HashSet<string>();
        bool loaded = false;
        foreach (Lineup.Unit u in Lineup.Of(_edit.Homes))
        {
            string key = PoolKey(u);
            if (!_pool.TryGetValue(key, out (Node3D Rig, TankModel Model) tank))
            {
                if (loaded || _unloadable.Contains(key))
                {
                    _tanksStale |= !_unloadable.Contains(key);
                    continue;
                }
                loaded = true;
                if (Stood(u, hex) is not { } made)
                {
                    _unloadable.Add(key);
                    continue;
                }
                tank = made;
                _pool[key] = tank;
            }
            wanted.Add(key);
            tank.Rig.Position = BoardDress.Middle(_field, u.Cell, squash, rise);
            tank.Rig.Rotation = new Vector3(0.0f, Mathf.DegToRad(Lineup.Yaw(_field, u.Cell, u.Facing, squash)), 0.0f);
            tank.Rig.Visible = shown && _field.InBounds(u.Cell);
        }
        foreach ((string key, (Node3D Rig, TankModel Model) tank) in _pool)
            if (!wanted.Contains(key))
                tank.Rig.Visible = false;
    }

    /// <summary>A model of the unit's tag, in its side's paint, sized to the
    /// hex by its class - or null, said why.</summary>
    private (Node3D Rig, TankModel Model)? Stood(Lineup.Unit u, float hex)
    {
        ulong t0 = Time.GetTicksMsec();
        TankModel model;
        try
        {
            model = TankModel.Load(u.Model);
        }
        catch (Exception e)
        {
            GD.Print($"editor: no {u.Model} for the homes: {e.Message}");
            return null;
        }
        model.ScaleTo(Lineup.PixelsFor(model, Lineup.ClassFor(u.Model), hex));
        Lineup.Paint(model, u.Side);
        var rig = new Node3D { Name = $"Home_{u.Side}_{u.Model}" };
        AddChild(rig);
        rig.AddChild(model);
        model.Apply();
        GD.Print($"editor: {u.Side} {u.Model} loaded for the homes in {Time.GetTicksMsec() - t0} ms");
        return (rig, model);
    }

    /// <summary>
    /// <c>--menu turn</c>: Ctrl and the wheel up over home 1, then an Alt drag
    /// from it towards the cell on its 330 edge, through
    /// <see cref="Input.ParseInputEvent"/> - the probes' reason: input, and a
    /// capture run has no hands. The drag's move is handed the point itself,
    /// since a synthesised press moves no pointer (<c>--menu overlay</c>).
    /// </summary>
    private void TurnProbe()
    {
        if (_open != "turn" || _edit.Homes.Count == 0)
            return;
        Vector2I home = _edit.Homes[0].Cell;
        switch (_frame)
        {
            case 6:
                _under = home;
                GD.Print($"turn: home 1 faces {FacingOf(0)} ({(_edit.Homes[0].Facing is null ? "its side's way" : "said")}); ctrl+wheel up");
                Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.WheelUp, Pressed = true, CtrlPressed = true });
                break;
            case 8:
                GD.Print($"turn: after ctrl+wheel up home 1 faces {FacingOf(0)}, zoom {_camera.Zoom.X:F2}; alt+drag to its 330 edge");
                _under = home;
                Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, AltPressed = true });
                break;
            case 10:
                Turning(TopOnScreen(HexField.Step(home, 330)));
                Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, AltPressed = true });
                break;
            case 12:
                GD.Print($"turn: after the drag home 1 faces {FacingOf(0)}, the cell still '{_edit.GlyphAt(home).Letter}', "
                         + $"turning {_turning}; undo");
                Take(_edit.Undo(), "undone");
                break;
            case 14:
                GD.Print($"turn: after one undo home 1 faces {FacingOf(0)}");
                break;
        }
    }

    /// <summary>
    /// <c>--menu units</c>: key 4, the brown HT brush, a click on (3,3), a
    /// click on (6,4) - the same tank moved, not a second - the green LT on top
    /// of it, then a right click there; each step prints the homes.
    /// </summary>
    private void UnitsProbe()
    {
        if (_open != "units")
            return;
        void Click(Vector2I at, MouseButton button)
        {
            _under = at;
            foreach (bool down in new[] { true, false })
                Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = button, Pressed = down });
        }
        string Homes() => string.Join(", ", _edit.Homes);
        switch (_frame)
        {
            case 4:
                GD.Print($"units: homes {Homes()}; key 4");
                Input.ParseInputEvent(new InputEventKey { Keycode = Key.Key4, Pressed = true });
                Input.ParseInputEvent(new InputEventKey { Keycode = Key.Key4, Pressed = false });
                break;
            case 6:
                _unitSide = Parking.Brown;
                _unitClass = "HTP";
                GD.Print($"units: tab {_tab}, {_picks.Count} brushes, tool '{Tool()}'; click (3,3)");
                Click(new Vector2I(3, 3), MouseButton.Left);
                break;
            case 8:
                GD.Print($"units: homes {Homes()}; click (6,4)");
                Click(new Vector2I(6, 4), MouseButton.Left);
                break;
            case 10:
                GD.Print($"units: homes {Homes()}; green LT on (6,4)");
                _unitSide = Parking.Green;
                _unitClass = "LTP";
                Click(new Vector2I(6, 4), MouseButton.Left);
                break;
            case 12:
                GD.Print($"units: homes {Homes()}; right click (6,4)");
                Click(new Vector2I(6, 4), MouseButton.Right);
                break;
            case 14:
                GD.Print($"units: homes {Homes()}, {_edit.Depth} undo(s)");
                break;
        }
    }

    /// <summary>The edge from <paramref name="cell"/> whose neighbour lies
    /// nearest the direction of <paramref name="screen"/> on the screen - so it
    /// is right on any turn of the board.</summary>
    private int EdgeTowards(Vector2I cell, Vector2 screen)
    {
        Vector2 at = TopOnScreen(cell);
        Vector2 want = screen - at;
        if (want.LengthSquared() < 1.0f)
            return -1;
        int best = -1;
        float most = float.MinValue;
        foreach (int edge in HexField.EdgeHeadings)
        {
            Vector2 way = (TopOnScreen(HexField.Step(cell, edge)) - at).Normalized();
            float dot = way.Dot(want.Normalized());
            if (dot > most)
            {
                most = dot;
                best = edge;
            }
        }
        return best;
    }

    /// <summary>The edge a home faces now: its own, else its side's way across.</summary>
    private int FacingOf(int slot)
    {
        Parking home = _edit.Homes[slot];
        return home.Facing ?? Lineup.Across(Parking.SidesOf(_edit.Homes)[slot]);
    }

    /// <summary>Alt and a left press on a home: the drag turns it, one undo.</summary>
    private bool StartTurning()
    {
        int slot = _edit.SlotAt(_under);
        if (slot < 0)
            return false;
        _turning = slot;
        _picked = _under;
        _edit.Begin();
        return true;
    }

    /// <summary>The drag's move: the home faces the edge towards the pointer.</summary>
    private void Turning(Vector2 screen)
    {
        if (_turning < 0 || _turning >= _edit.Homes.Count)
            return;
        int edge = EdgeTowards(_edit.Homes[_turning].Cell, screen);
        if (edge < 0 || edge == FacingOf(_turning))
            return;
        if (_edit.Face(_turning, edge, out string why))
        {
            _said = $"home {_turning + 1} faces {edge}";
            _dirty = true;
        }
        else
            _said = why;
        _tanksStale = true;
        _marks.QueueRedraw();
        Say();
    }

    private void StopTurning()
    {
        if (_turning < 0)
            return;
        _turning = -1;
        _edit.End();
        // the panel's facing dropdown says the new edge
        _shape = "";
        Settle();
    }

    /// <summary>Ctrl and the wheel over a home: an edge either way, one undo.</summary>
    private bool TurnHome(int by)
    {
        int slot = _edit.SlotAt(_under);
        if (slot < 0)
            return false;
        int i = Array.IndexOf(HexField.EdgeHeadings, FacingOf(slot));
        int edge = HexField.EdgeHeadings[((i + by) % 6 + 6) % 6];
        Wrote(() => _edit.Face(slot, edge, out string why) ? "" : why);
        _said = $"home {slot + 1} faces {edge}";
        Say();
        return true;
    }
}
