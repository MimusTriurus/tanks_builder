using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The board being drawn, dressed as the 3D line plays it: model trees, grass,
/// mines, cel walls and the bunker, cel water and relief, the sun and the
/// shadow skin - <see cref="BoardDress"/>, the same calls
/// <c>Tank3DBench</c> makes (docs/editor3d-plan.md, stage 2).
///
/// <b>What it replaced is the sprite era's look on the same stage</b>: the
/// <see cref="Grove"/>'s billboards - trees, bushes and stones the 3D bench
/// never stands - over soil with no grass, and walls without the model's line.
/// The grove is still sown, because the probes ask it; it is no longer handed
/// to the stage, so it draws nothing.
///
/// <b>Redressed per cell, after the stage.</b> A stroke ends in
/// <see cref="Settle"/>, which only marks the dressing stale; the frame after,
/// past <see cref="Stage3D.Place"/>, every cell whose cover, height, ramp,
/// water or floor moved has its trees and mine taken up and laid again. Model
/// trees are loaded per tree, so a full replant on every stroke would be
/// seconds; a hill raised next to a wood re-stands that wood's feet and nothing
/// else. The grass has no way to lift one meadow, so it is laid again whole -
/// only when a lawned cell or a trunk moved.
/// </summary>
public sealed partial class MapEditor
{
    private CrownOutline? _crowns;
    private BoardDress.MineArt? _mineArt;
    private bool _mineTried;
    private Grass3D? _lawn;
    private MeshInstance3D? _skin;
    private bool _dressStale = true;

    /// <summary>The frame the dressing last caught up on - a capture waits a
    /// few past it.</summary>
    private int _dressedFrame = int.MaxValue / 2;

    /// <summary>What each cell was dressed for, so a stroke redresses only the
    /// cells it moved.</summary>
    private readonly Dictionary<Vector2I, string> _dressedAs = new();
    private readonly Dictionary<Vector2I, List<(Node3D Holder, Vector3 At)>> _woodOn = new();
    private readonly Dictionary<Vector2I, Node3D> _mineOn = new();
    private string _lawnAs = "", _skinAs = "";
    private readonly Dictionary<Vector2I, int> _lawnOn = new();
    private float _lawnPpm = BoardDress.GrassPpm;

    /// <summary>Each species read once (<see cref="BoardDress.Tree"/>: nothing
    /// burns on a board being drawn).</summary>
    private readonly Dictionary<string, TreeModel.Loaded> _species = new();

    /// <summary>The species' own scenes, which only their copies ever enter the
    /// tree: freed here, or every run ends on Godot's leak report. Not when
    /// Run parks the editor (<see cref="Loading.Parking"/>): the wood planted
    /// after the way back is copied out of these.</summary>
    public override void _ExitTree()
    {
        // the window closes straight away again for whatever comes in -
        // the bench has no unsaved work to ask about (MapEditor.Document)
        GetTree().AutoAcceptQuit = true;
        if (Loading.Parking)
            return;
        foreach (TreeModel.Loaded model in _species.Values)
            if (GodotObject.IsInstanceValid(model.Scene))
                model.Scene.Free();
        _species.Clear();
    }

    /// <summary>The light and the outline, once, after the stage is in the
    /// tree - the outline draws through the stage's camera.</summary>
    private void Dress()
    {
        AddChild(BoardDress.World());
        AddChild(BoardDress.Sun());
        if (_stage is not null)
        {
            _crowns = new CrownOutline { Name = "CrownOutline" };
            AddChild(_crowns);
            _crowns.Build(_stage.Camera);
        }
    }

    /// <summary>The frame's half of the dressing: the stale cells laid again,
    /// after the stage has rebuilt the ground they stand on.</summary>
    private void Redress()
    {
        if (!_dressStale)
            return;
        _dressStale = false;
        _dressedFrame = _frame;
        if (_stage is null || _field.Atlas is null)
            return;
        float squash = _field.Squash, rise = _field.RiseFactor;
        Vector2 hex = _field.Atlas.HexRect.Size;
        ulong t0 = Time.GetTicksMsec();

        var seen = new HashSet<Vector2I>();
        var moved = new List<Vector2I>();
        for (int q = 0; q < _field.Columns; q++)
        for (int r = 0; r < _field.Rows; r++)
        {
            var cell = new Vector2I(q, r);
            seen.Add(cell);
            string now = CellDress(cell);
            if (_dressedAs.TryGetValue(cell, out string? was) && was == now)
                continue;
            _dressedAs[cell] = now;
            moved.Add(cell);
            Unwood(cell);
            Unmine(cell);
            if (!_field.InBounds(cell))
                continue;
            if (_field.CoverAt(cell) == Cover.Forest)
                Wood(cell, squash, rise);
            if (_field.CoverAt(cell) == Cover.Minefield)
                Mine(cell, hex, squash, rise);
        }
        foreach (Vector2I gone in _dressedAs.Keys.Where(c => !seen.Contains(c)).ToList())
        {
            _dressedAs.Remove(gone);
            Unwood(gone);
            Unmine(gone);
        }
        if (moved.Count == 0)
            return;

        // A wood's feet stand on its neighbours' ground too: the ring reaches
        // 80 px out of a 124 px cell, so a hill raised beside it lifts it.
        foreach (List<(Node3D Holder, Vector3 At)> wood in _woodOn.Values)
            foreach ((Node3D holder, Vector3 at) in wood)
                holder.Position = BoardDress.Foot(_field, at, squash, rise);

        ulong tWood = Time.GetTicksMsec() - t0;
        string relief = Grounds();
        if (relief != _skinAs)
        {
            _skinAs = relief;
            _skin?.QueueFree();
            _skin = BoardDress.Shadows(_stage, squash, rise);
            if (_skin is not null)
                AddChild(_skin);
        }
        ulong tSkin = Time.GetTicksMsec() - t0;
        int laid = Lawn(moved, hex.X * 0.5f, squash, rise);
        Shown();
        GD.Print($"editor: dressed {moved.Count} cell(s) in {Time.GetTicksMsec() - t0} ms "
                 + $"(wood and mines {tWood}, shadows {tSkin - tWood}, grass on {laid} in {Time.GetTicksMsec() - t0 - tSkin})");
    }

    /// <summary>
    /// The crowns' mask onto the stage's camera, every frame, its depth slab
    /// round the wood - the bench's <c>WoodFollow</c>. Without it the mask
    /// stays where it was built and its ink fills the crowns black.
    /// </summary>
    private void CrownsFollow()
    {
        if (_crowns is null || _stage is null || _woodOn.Count == 0)
            return;
        Camera3D eye = _stage.Camera;
        Vector3 middle = Vector3.Zero;
        foreach (Vector2I cell in _woodOn.Keys)
            middle += BoardDress.Middle(_field, cell, _field.Squash, _field.RiseFactor) / _woodOn.Count;
        float depth = (middle - eye.GlobalPosition).Dot(-eye.GlobalBasis.Z);
        _crowns.Follow(eye, _camera.Zoom.X, depth);
    }

    /// <summary>Everything about a cell its dressing reads.</summary>
    private string CellDress(Vector2I cell) =>
        !_field.InBounds(cell) ? "-"
        : $"{_field.CoverAt(cell)},{_field.FoundationAt(cell)},{_field.LevelAt(cell)},"
          + $"{_field.IsRamp(cell)},{_field.IsWater(cell)}";

    private string Grounds() =>
        $"{_field.Columns}x{_field.Rows}:" + string.Join(";", _dressedAs.OrderBy(p => p.Key.X).ThenBy(p => p.Key.Y)
            .Select(p => p.Value.Substring(p.Value.IndexOf(',') + 1)));

    private void Wood(Vector2I cell, float squash, float rise)
    {
        var wood = new List<(Node3D, Vector3)>();
        foreach (BoardDress.TreeSpot spot in BoardDress.Spots(_field, cell, BoardDress.Oaks, BoardDress.WoodCount, squash))
        {
            if (BoardDress.Tree(this, spot, BoardDress.Foot(_field, spot.At, squash, rise), pbr: false, _crowns,
                                $"{spot.Name}.{cell.X}_{cell.Y}.{spot.Index}", out TreeModel.Loaded? model,
                                out string? error, _species) is not { } holder)
            {
                GD.Print($"editor: {error}");
                continue;
            }
            _lawnPpm = model!.Ppm;
            wood.Add((holder, spot.At));
        }
        _woodOn[cell] = wood;
    }

    private void Unwood(Vector2I cell)
    {
        if (!_woodOn.Remove(cell, out List<(Node3D Holder, Vector3 At)>? wood))
            return;
        foreach ((Node3D holder, Vector3 _) in wood)
            holder.QueueFree();
    }

    private void Mine(Vector2I cell, Vector2 hex, float squash, float rise)
    {
        if (!_mineTried)
        {
            _mineTried = true;
            _mineArt = BoardDress.Mine(out string? note);
            if (note is not null)
                GD.Print($"editor: {note}");
        }
        if (_mineArt is not null)
            _mineOn[cell] = BoardDress.LayMine(this, _mineArt, _field, cell, hex, pbr: false, squash, rise, out _);
    }

    private void Unmine(Vector2I cell)
    {
        if (_mineOn.Remove(cell, out Node3D? mine))
            mine.QueueFree();
    }

    /// <summary>
    /// The grass on <paramref name="moved"/> taken up and laid again where it
    /// grows (<see cref="Grass3D.TakeUp"/>); the whole of it laid anew only when
    /// the board's size moved, since the press map covers the board. Answers
    /// how many cells were laid.
    /// </summary>
    private int Lawn(List<Vector2I> moved, float radius, float squash, float rise)
    {
        string size = $"{_field.Columns}x{_field.Rows}";
        IEnumerable<Vector2I> due = moved;
        if (_lawn is null || size != _lawnAs)
        {
            _lawnAs = size;
            _lawn?.QueueFree();
            _lawnOn.Clear();
            _lawn = new Grass3D
            {
                Name = "Grass", Ppm = _lawnPpm,
                GustRate = TreeModel.GustRate, GustTravel = TreeModel.GustTravel, ShadowInk = Stage3D.ShadowInk.A,
            };
            // on the relief the tufts root on the drawn ground and keep off
            // its banks, as on the bench (BoardDress.ReliefGround); the
            // relief is asked when a tuft is laid, so the one built last
            _lawn.Ground = w =>
            {
                var flat = new Vector2(w.X, w.Z * _field.Squash);
                return _relief is { } relief && _stage is not null
                    ? BoardDress.ReliefGround(relief, flat - _stage.Origin, _field.RiseFactor)
                    : _field.TopAtPoint(flat) / _field.RiseFactor;
            };
            AddChild(_lawn);
            _lawn.Map(BoardDress.LawnSpan(_field, radius, squash, rise));
            due = _dressedAs.Keys;
        }
        else
        {
            // A bank is smoothed across the cells beside the one that moved:
            // their tufts are laid again on it too.
            var near = new HashSet<Vector2I>(moved);
            foreach (Vector2I cell in moved)
                foreach (int heading in HexField.EdgeHeadings)
                    near.Add(_field.Neighbour(cell, heading));
            due = near.Where(_dressedAs.ContainsKey);
        }
        // Under the bench's veil the first lawn goes down a few cells a frame
        // (LawnMore), so the bar moves: it is a second and a half of the
        // editor's way back, and the veil's bar stood still through all of it.
        if (!_lawnLoaded && Loading.Veiled)
        {
            _lawnDue.AddRange(due);
            _lawnLaying = (radius, squash, rise);
            _lawnTotal = _lawnDue.Count;
            return 0;
        }
        int laid = 0;
        foreach (Vector2I cell in due)
            if (LawnCell(cell, radius, squash, rise))
                laid++;
        return laid;
    }

    private bool LawnCell(Vector2I cell, float radius, float squash, float rise)
    {
        if (_lawnOn.Remove(cell, out int was))
            _lawn!.TakeUp(was);
        if (!BoardDress.Lawned(_field, cell))
            return false;
        var trunks = new List<Vector3>();
        if (_woodOn.TryGetValue(cell, out List<(Node3D Holder, Vector3 At)>? wood))
            foreach ((Node3D holder, Vector3 _) in wood)
                trunks.Add(holder.Position);
        _lawnOn[cell] = _lawn!.Lay(Grass3D.Kind.CelTufts, BoardDress.Middle(_field, cell, squash, rise), radius,
                                   101 + cell.X * 31 + cell.Y, trunks);
        return true;
    }

    /// <summary>The first lawn is down, or was never waited for: the veil
    /// lifts after it, and every later lawn is laid whole, as a stroke
    /// wants it.</summary>
    private bool _lawnLoaded;

    private readonly List<Vector2I> _lawnDue = new();
    private (float Radius, float Squash, float Rise) _lawnLaying;
    private int _lawnTotal;

    /// <summary>Cells of the first lawn laid a frame under the veil.</summary>
    private const int LawnStage = 16;

    /// <summary>
    /// A frame's share of the first lawn under the veil, and the veil told
    /// once it is all down (<see cref="Loading.Built"/>). Opened straight,
    /// nothing was queued and this only says the build is over.
    /// </summary>
    private void LawnMore()
    {
        if (_lawnLoaded)
            return;
        if (_lawnDue.Count == 0)
        {
            _lawnLoaded = true;
            Loading.Built(this);
            return;
        }
        int laid = _lawnTotal - _lawnDue.Count;
        Loading.Step(this, $"grass {laid} of {_lawnTotal} cells", 0.4f + 0.6f * laid / _lawnTotal);
        int n = Math.Min(LawnStage, _lawnDue.Count);
        (float radius, float squash, float rise) = _lawnLaying;
        for (int i = 0; i < n; i++)
            LawnCell(_lawnDue[i], radius, squash, rise);
        _lawnDue.RemoveRange(0, n);
        Loading.Rest();
    }
}
