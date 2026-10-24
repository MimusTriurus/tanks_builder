using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// A tank on every parking the board declares, and a click on one of them to
/// take it (docs/editor3d-plan.md, stage 0b).
///
/// <b>One tank drives; the rest stand.</b> The bench's own tank is the bench's
/// fields - its rig, model, effects, fate, route - and making each of them a
/// value per tank is the bench rewritten. So the first parking gets the own
/// tank, as before, and every other parking a model of its class standing on
/// its cell (<see cref="Parked"/>): the board as it was drawn, every home
/// manned. Clicking a standing tank swaps them - the one being driven parks
/// where it stopped, heading and all, and the one clicked is mounted on the rig
/// in its place (<see cref="TakeOver"/>). The own tank's state does not travel:
/// a burning tank parked comes back whole, which is the price of one rig.
///
/// <b>Who stands where.</b> A parking's class picks its model
/// (<see cref="Pairs"/>); one that names no class takes the first model on disk
/// nobody on the board is already using, the harness's "whoever is left".
///
/// <b>What they are to the rest.</b> A route will not go through a standing
/// tank's cell (<see cref="OrderTo"/>) - but the free drive does, the ram's
/// target is still <c>Tank3DBench.Ram</c>'s own, and rounds pass through them.
/// Those are the bench rewritten again, and they wait for it.
///
/// <b>Only for a board the editor handed over, or with <c>--parked</c>.</b> The
/// events board parks five, and every capture recipe written against the bench
/// is a recipe with one tank on it.
/// </summary>
public sealed partial class Tank3DBench
{
    private sealed class Parked
    {
        public required TankModel Model;
        public required Node3D Rig;
        public required string Tag;
        public required Vector2I Cell;
        public float Heading;
        /// <summary>Its side and its fate in a match (Tank3DBench.Match).</summary>
        public Side Side;
        public Fate Fate = Fate.Alive;
        /// <summary>The turret and the gun as the unit left them, radians: a
        /// unit handed back after its shot keeps them laid where they were.</summary>
        public float Yaw, Elevation;
    }

    private readonly List<Parked> _parked = new();

    /// <summary>The board's parkings, the first of them the own tank's.</summary>
    private IReadOnlyList<Parking> _parkings = Array.Empty<Parking>();

    /// <summary><c>--parked</c>: stand them on a board nobody handed over.</summary>
    private bool _parkedWanted;

    private bool ParkedOn => _handed || _parkedWanted;

    /// <summary>A model on every parking past the first.</summary>
    private void BuildParked()
    {
        if (!ParkedOn || _field is null || _parkings.Count < 2)
            return;
        if (Match)
        {
            BuildSides();
            return;
        }
        var used = new List<string> { _modelTag };
        foreach (Parking home in _parkings.Skip(1))
        {
            if (!_field.InBounds(home.Cell) || home.Cell == CellHere)
                continue;
            string tag = ModelFor(home.Class, used);
            used.Add(tag);
            Stand(tag, home.Cell, _heading);
        }
        GD.Print($"tank3d: {_parked.Count} tank(s) standing on the other parkings: "
                 + string.Join(", ", _parked.Select(p => $"{p.Tag} {p.Cell}")));
    }

    /// <summary>The model a parking asks for, or the first one on disk nobody
    /// has taken.</summary>
    private string ModelFor(string? wanted, List<string> used)
    {
        if (wanted is { Length: > 0 })
        {
            int i = Array.FindIndex(Pairs, p => string.Equals(p.Class, wanted, StringComparison.OrdinalIgnoreCase));
            if (i >= 0)
                return Pairs[i].Model;
        }
        return _models.FirstOrDefault(m => !used.Contains(m, StringComparer.OrdinalIgnoreCase))
               ?? _models.FirstOrDefault() ?? _modelTag;
    }

    /// <summary>A standing tank of <paramref name="tag"/> on a cell, facing
    /// <paramref name="heading"/>, upright on its cell's ground - in a match,
    /// of <paramref name="side"/> and in its paint.</summary>
    private void Stand(string tag, Vector2I cell, float heading, Side side = Side.Green,
                       float yaw = 0.0f, float elevation = 0.0f)
    {
        TankModel model;
        try
        {
            model = TankModel.Load(tag, toon: !_pbr);
        }
        catch (Exception e)
        {
            GD.Print($"tank3d: parked {tag} on {cell}: {e.Message}");
            return;
        }
        model.ScaleTo(PixelsFor(model, ClassFor(tag)));
        SidePaint(model, side);
        var rig = new Node3D { Name = $"Parked_{tag}_{cell.X}_{cell.Y}" };
        AddChild(rig);
        rig.AddChild(model);
        model.Yaw = yaw;
        model.Elevation = elevation;
        model.Apply();
        rig.Position = CellMiddle(cell);
        rig.Rotation = new Vector3(0.0f, Mathf.DegToRad(heading), 0.0f);
        _parked.Add(new Parked
        {
            Model = model, Rig = rig, Tag = tag.ToUpperInvariant(), Cell = cell, Heading = heading, Side = side,
            Yaw = yaw, Elevation = elevation,
        });
    }

    /// <summary>The standing tank under a click, by where it is drawn: within
    /// half a hull of the pointer, the nearest.</summary>
    private Parked? ParkedAt(Vector2 screen)
    {
        Parked? best = null;
        float near = float.MaxValue;
        foreach (Parked p in _parked)
        {
            Vector3 middle = p.Rig.Position + Vector3.Up * (p.Model.Size.Y * p.Model.PixelsPerUnit * 0.4f);
            float d = _camera.UnprojectPosition(middle).DistanceTo(screen);
            float reach = 0.5f * p.Model.HullLength * p.Model.PixelsPerUnit * _zoom;
            if (d < reach && d < near)
            {
                near = d;
                best = p;
            }
        }
        return best;
    }

    /// <summary>
    /// Drive <paramref name="next"/> instead: the own tank parks where it stands
    /// and the standing one is mounted on the rig, on its cell and heading.
    /// </summary>
    private void TakeOver(Parked next)
    {
        if (_field is null || RamRunning)
            return;
        // In a match the target goes back to standing first: the swap resets
        // the rig, and the reset wipes the target's marks.
        DropTarget();
        string was = _modelTag;
        Vector2I wasCell = CellHere;
        float wasHeading = _heading;
        Side wasSide = _side;
        // the turret and the gun stay where the last shot left them, both ways
        float wasYaw = _model.Yaw, wasElevation = _model.Elevation;
        _parked.Remove(next);
        next.Rig.QueueFree();
        _side = next.Side;
        try
        {
            Mount(next.Tag);
        }
        catch (Exception e)
        {
            GD.Print($"tank3d: {next.Tag}: {e.Message}");
            _side = wasSide;
            Stand(next.Tag, next.Cell, next.Heading, next.Side, next.Yaw, next.Elevation);
            return;
        }
        // the ram is the unit's own to switch on (Tank3DBench.RamPlan)
        _ramMode = false;
        CancelOrder();
        _home = next.Cell;
        Heading = next.Heading;
        _model.Yaw = next.Yaw;
        _model.Elevation = next.Elevation;
        Park();
        Stand(was, wasCell, wasHeading, wasSide, wasYaw, wasElevation);
        Choose(true);
        _orderNote = $"ведёшь {next.Tag}";
        GD.Print($"tank3d: took {next.Tag} on {next.Cell}; {was} parks on {wasCell}");
    }

    /// <summary>The cells a route keeps off - the standing tanks', a destroyed
    /// one's not (docs/gdd/states.md, «Уничтожен»).</summary>
    private IEnumerable<Vector2I> ParkedCells => _parked.Where(p => p.Fate != Fate.Destroyed).Select(p => p.Cell);
}
