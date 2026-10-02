using System;
using System.Collections.Generic;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The board's brick walls under the 3D tank (docs/tank3d.md, "Кирпичные
/// стены"): the sprite benches' masonry, stood here as it is on theirs, and
/// the rules' ram through it (GDD contradictions.md, "таран стен"; classes.md,
/// "Бульдозер HT").
///
/// <list type="bullet">
/// <item><b>The walls are the events bench's</b> - a <see cref="WallProp"/>
/// on every walled cell of the map, by the map's own recipe
/// (<see cref="Masonry.Laying"/>: a bare <c>W</c> is the ring on six sides),
/// each on its own collision bit. The bricks, the fit, the solver and the
/// heap are the prop's; this scene stands them on its <see cref="Stage3D"/>,
/// whose world is the one the tank drives in.</item>
/// <item><b>A standing wall stops a drive</b>: the board's record of the edge
/// (<see cref="HexField.Blocked"/>, written off the bricks the way
/// <see cref="WallField.Restate"/> writes it) refuses the step, and the hull
/// stops with its nose at the rim - as the cliff and the board's edge stop
/// it (<see cref="CanDrive"/>).</item>
/// <item><b>A ram is an order</b> (<c>T</c> with a wall ahead,
/// <c>--do ram-wall</c>, the panel): full ahead through the leaf to the cell
/// past it. Any class; the rules' answer - the leaf is down, the tank is
/// whole. The section goes when the nose crosses the rim
/// (<see cref="WallProp.Rammed"/>, <see cref="WallField"/>'s watcher on this
/// scene's hull), and the driven box rides the hull all along, so what is
/// loose is shoved off the way the hull goes.</item>
/// <item><b>A heavy needs no order</b>: for it a walled edge is a step, and
/// driving through it breaks the wall (<see cref="MovementProfile.Bulldozes"/>).</item>
/// <item>An edge goes off the board's record when its masonry falls, not when
/// it is struck (<see cref="WallTick"/>); <c>Backspace</c> lays every wall
/// again.</item>
/// </list>
/// <c>--no-walls</c> leaves the cells bare.
/// </summary>
public sealed partial class Tank3DBench
{
    private bool _noWalls;
    private readonly List<WallProp> _walls = new();
    private BoardMap? _wallMap;

    /// <summary>What each wall had let go of and broken when the board was last
    /// told about it - <see cref="WallField"/>'s two ints.</summary>
    private readonly Dictionary<Vector2I, (int Loose, int Broken)> _wallStated = new();

    /// <summary>A ram through a wall under way: the cell the hull drives to,
    /// the one it set off from, and how long it has made no way.</summary>
    private bool _wallRam;
    private Vector2I _wallRamFrom, _wallRamTo;
    private float _wallRamStuck;
    private string _wallNote = "";

    /// <summary>The nose's cell as of the last frame, so a crossing is a
    /// difference - frozen while the hull turns on the spot, the watcher's
    /// pivot rule (<see cref="WallField"/>, <c>_noseCell</c>).</summary>
    private Vector2I? _wallNose;

    /// <summary>The wall whose rig carries the driven box.</summary>
    private WallProp? _wallBoxed;

    /// <summary>A walled side counts as ahead of the hull within this many
    /// degrees of its heading.</summary>
    private const float WallAheadDeg = 35.0f;

    private float WallRadius => _tile is null ? 1.0f : _tile.HexRect.Size.X * 0.5f;

    /// <summary>A wall on every walled cell of the map, after the stage.</summary>
    private void BuildWalls(BoardMap map)
    {
        _wallMap = map;
        if (_noWalls || _field is null || _stage is null)
            return;
        foreach (Vector2I cell in map.Walled())
        {
            (WallKit.Recipe recipe, int bearing) = map.MasonryAt(cell).Laying()
                ?? (new WallKit.Recipe { Sides = TankBench.RingSides }, HexField.EdgeHeadings[0]);
            (CaponKit.Recipe Recipe, int Bearing)? shelter = map.MasonryAt(cell)?.Sheltering();
            if (shelter is { } s)
                bearing = s.Bearing;
            var prop = new WallProp
            {
                Field = _field, Stage = _stage, Cell = cell,
                Recipe = recipe, Capon = shelter?.Recipe, Borrow = null,
                Channel = _walls.Count,
            };
            AddChild(prop);
            prop.Bearing = bearing;
            prop.Build();
            _walls.Add(prop);
            WallRestate(prop, laying: true);
            GD.Print($"tank3d: wall on {cell}, sides {Convert.ToString(_field.SidesAt(cell), 2).PadLeft(6, '0')}, "
                     + $"clear {prop.Clearance():F2} m inside");
        }
    }

    /// <summary>Which of a wall's cell's six edges still carry masonry, onto
    /// the board - <see cref="WallField.Restate"/>, measured off the bricks:
    /// the mask whole when the wall is laid, an edge at a time after.</summary>
    private void WallRestate(WallProp prop, bool laying = false)
    {
        int mask = 0;
        Vector2 middle = _field!.FlatAnchor(prop.Cell);
        for (int bit = 0; bit < Masonry.Headings.Length; bit++)
        {
            Vector2I next = HexField.Step(prop.Cell, Masonry.Headings[bit]);
            if (prop.Bars(_field.FlatAnchor(next) - middle, crossing: true))
                mask |= 1 << bit;
        }
        _wallStated[prop.Cell] = (prop.Rig?.Loose ?? 0, prop.Rig?.Broken ?? 0);
        if (laying)
        {
            _field.SetSides(prop.Cell, mask);
            return;
        }
        int was = _field.SidesAt(prop.Cell);
        for (int bit = 0; bit < Masonry.Headings.Length; bit++)
        {
            if ((was & (1 << bit)) == 0 || (mask & (1 << bit)) != 0)
                continue;
            if (_field.Breach(prop.Cell, Masonry.Headings[bit]))
                GD.Print($"tank3d: wall {prop.Cell} side {Masonry.Headings[bit]} is down");
        }
    }

    /// <summary>The hull is breaking masonry by driving: it is moving, and
    /// either a ram order has it or it is a heavy (<see cref="WallField.Sweeps"/>).</summary>
    private bool WallSweeping => Mathf.Abs(_speed) >= 1.0f && (_wallRam || _profile.Bulldozes);

    /// <summary>Whether the step across an edge is refused by its masonry -
    /// <see cref="CanDrive"/>'s half about walls; never while the hull is
    /// breaking them.</summary>
    private bool WallStops(Vector2I here, int heading) =>
        _walls.Count > 0 && !WallSweeping && _field!.Blocked(here, heading);

    /// <summary>The way the hull goes, signed by its speed.</summary>
    private Vector3 WallWay()
    {
        float h = Mathf.DegToRad(_heading);
        var ahead = new Vector3(Mathf.Sin(h), 0.0f, Mathf.Cos(h));
        return _speed < 0.0f ? -ahead : ahead;
    }

    /// <summary>
    /// A frame of the masonry: the driven box onto the wall the hull is about
    /// and posed to it, a crossed rim answered by the wall on it, what fell
    /// taken off the board's record, and the ram order to its end.
    /// </summary>
    private void WallTick(float dt)
    {
        if (_walls.Count == 0 || _field is null || _stage is null)
            return;
        bool can = _fate == Fate.Alive && !DeepHere && !_falling;
        Vector3 way = WallWay();
        var flatWay = new Vector2(way.X, way.Z * Squash);
        Vector3 foot = Foot(_rig.Position);
        Vector2I here = _field.FlatCellAt(Board(_rig.Position));
        Vector2I nose = _field.FlatCellAt(Board(_rig.Position + way * _foot.HalfLen));
        Vector3 box = WallBox();
        // The box rides always, order or none (WallField.Ram's reason: a hull
        // that ghosts through the heap its own ram made is the seam).
        WallRebox(can ? WallBoxable(here, nose) : null);
        bool sweeping = can && WallSweeping;
        if (!sweeping)
            _wallBoxed?.Disarm();
        if (can)
            _wallBoxed?.Drive(foot, flatWay, Mathf.Abs(_speed), sweeping ? WallGate(here, way) : null);
        if (!sweeping)
            _wallNose = nose;
        else if (Mathf.Abs(_speed) < 1.0f)
            _wallNose ??= nose;
        else
        {
            Vector2I was = _wallNose ?? nose;
            _wallNose = nose;
            if (was != nose)
            {
                List<Vector2I> chain = HexField.Chain(was, nose);
                for (int leg = 1; leg < chain.Count; leg++)
                {
                    if (HexField.HeadingTo(chain[leg - 1], chain[leg]) < 0)
                        continue;
                    Vector2 dir = _field.FlatAnchor(chain[leg]) - _field.FlatAnchor(chain[leg - 1]);
                    foreach (WallProp prop in _walls)
                    {
                        if (prop.Cell != chain[leg - 1] && prop.Cell != chain[leg])
                            continue;
                        int face = prop.Rammed(foot, dir, Mathf.Abs(_speed), box);
                        if (face >= 0)
                            GD.Print($"tank3d: {_modelTag} rams the wall on {prop.Cell} from {chain[leg - 1]} "
                                     + $"at {Mathf.Abs(_speed):F0} px/s: section {face}");
                    }
                }
            }
        }
        foreach (WallProp prop in _walls)
        {
            var now = (prop.Rig?.Loose ?? 0, prop.Rig?.Broken ?? 0);
            if (_wallStated.TryGetValue(prop.Cell, out var was) && was == now)
                continue;
            WallRestate(prop);
        }
        WallRamFrame(dt, here);
    }

    /// <summary>The hull's box for the solver, metres: its footprint, its deck
    /// high (<see cref="WallProp.Box"/>'s, asked of the model rather than of
    /// an atlas).</summary>
    private Vector3 WallBox()
    {
        float metres = WallRig.MetresPerCell / Mathf.Max(WallRadius, 1e-4f);
        float tall = _deckPx / RiseFactor * metres;
        return new Vector3(2.0f * _foot.HalfWide * metres, tall > 0.1f ? tall : WallRig.TankTall,
                           2.0f * _foot.HalfLen * metres);
    }

    /// <summary>The wall the hull is about: the one on its cell, on its
    /// nose's, or beside it - heaps spill a cell out (<see cref="WallField"/>,
    /// <c>Boxable</c>).</summary>
    private WallProp? WallBoxable(Vector2I here, Vector2I nose)
    {
        foreach (WallProp prop in _walls)
            if (prop.Cell == here)
                return prop;
        foreach (WallProp prop in _walls)
            if (prop.Cell == nose)
                return prop;
        foreach (WallProp prop in _walls)
            foreach (int heading in HexField.EdgeHeadings)
                if (HexField.Step(here, heading) == prop.Cell)
                    return prop;
        return null;
    }

    /// <summary>Move the driven box to <paramref name="prop"/>'s rig, mounted on
    /// the hull as it stands: footprint, glacis and turret roof.</summary>
    private void WallRebox(WallProp? prop)
    {
        if (_wallBoxed == prop)
            return;
        _wallBoxed?.Dismount();
        _wallBoxed = prop;
        if (prop is null)
            return;
        float metres = WallRig.MetresPerCell / Mathf.Max(WallRadius, 1e-4f);
        Vector3 way = WallWay();
        prop.Mount(Foot(_rig.Position), new Vector2(way.X, way.Z * Squash), WallBox(),
                   0.22f * 2.0f * _foot.HalfLen * metres, _roofPx / RiseFactor * metres);
    }

    /// <summary>The rim of the boxed wall the hull is driving at - the naming
    /// gate of <see cref="WallRig.Drive"/>, how far it may name masonry for
    /// itself: the order's rim under a ram, the one ahead of the hull for a
    /// heavy. A world point, the stage's own expression for it.</summary>
    private Vector3? WallGate(Vector2I here, Vector3 way)
    {
        if (_wallBoxed is null)
            return null;
        Vector2I from = here, to;
        if (_wallRam)
            (from, to) = (_wallRamFrom, _wallRamTo);
        else
        {
            int heading = WallHeadingNear(here, way, out float dot);
            if (heading < 0 || dot < Mathf.Cos(Mathf.DegToRad(WallAheadDeg)))
                return null;
            to = HexField.Step(here, heading);
        }
        if (from != _wallBoxed.Cell && to != _wallBoxed.Cell)
            return null;
        Vector2 mid = (_field!.FlatAnchor(from) + _field.FlatAnchor(to)) * 0.5f + _field.CentreOffset;
        return Stage3D.World(_stage!.Origin + mid, _field.LevelAt(_wallBoxed.Cell) * _field.Lift,
                             _field.Squash, _field.RiseFactor);
    }

    /// <summary>The flat side of <paramref name="cell"/> nearest the way the
    /// hull goes, and how near.</summary>
    private int WallHeadingNear(Vector2I cell, Vector3 way, out float dot)
    {
        int best = -1;
        dot = -2.0f;
        foreach (int heading in HexField.EdgeHeadings)
        {
            float d = HexWay(cell, heading).Dot(way);
            if (d > dot)
            {
                dot = d;
                best = heading;
            }
        }
        return best;
    }

    /// <summary>
    /// <c>T</c> with a wall ahead, <c>--do ram-wall</c>: full ahead through the
    /// standing leaf on the side of the hull's cell nearest its heading, to the
    /// cell past it. With none ahead, <c>ram-wall</c> lines the hull up first -
    /// on the free neighbour of the nearest wall, nose at its leaf - and
    /// <c>T</c> answers false, for the ram of the target.
    /// </summary>
    private bool WallRamGo(bool lineUp)
    {
        if (_walls.Count == 0 || _field is null || _wallRam || RamRunning || _fate != Fate.Alive)
            return false;
        Vector2I here = _field.FlatCellAt(Board(_rig.Position));
        float h = Mathf.DegToRad(_heading);
        var ahead = new Vector3(Mathf.Sin(h), 0.0f, Mathf.Cos(h));
        int heading = WallHeadingNear(here, ahead, out float dot);
        bool walled = heading >= 0 && dot >= Mathf.Cos(Mathf.DegToRad(WallAheadDeg))
                      && _field.InBounds(HexField.Step(here, heading)) && _field.Walled(here, heading);
        if (!walled)
        {
            if (!lineUp || !WallLineUp(out here, out heading))
            {
                if (lineUp)
                    _wallNote = "некуда встать: у стен нет свободного соседа";
                return false;
            }
        }
        _wallRam = true;
        _wallRamFrom = here;
        _wallRamTo = HexField.Step(here, heading);
        _wallRamStuck = 0.0f;
        _wallNose = null;
        // Square on to the side: the ram is a step across it.
        Heading = HeadingOf(HexWay(here, heading));
        _driveScripted = 1.0f;
        _turnScripted = 0.0f;
        _wallNote = $"таран стены: {here} -> {_wallRamTo}";
        GD.Print($"tank3d: {_modelTag} rams through the wall between {here} and {_wallRamTo}, heading {heading}");
        return true;
    }

    /// <summary>The hull on the middle of a free neighbour of the wall nearest
    /// it, facing the wall's cell across a standing leaf.</summary>
    private bool WallLineUp(out Vector2I from, out int heading)
    {
        from = default;
        heading = -1;
        float best = float.MaxValue;
        foreach (WallProp prop in _walls)
            foreach (int h in HexField.EdgeHeadings)
            {
                Vector2I next = HexField.Step(prop.Cell, h);
                int back = HexField.Reverse(h);
                if (!_field!.InBounds(next) || _field.IsWater(next) || _field.CoverAt(next) == Cover.Walls
                    || !_field.Passable(next, back) || !_field.Walled(next, back))
                    continue;
                float d = (CellWorld(next) - _rig.Position).Length();
                if (d < best)
                {
                    best = d;
                    from = next;
                    heading = back;
                }
            }
        if (heading < 0)
            return false;
        ResetTank();
        _rig.Position = Foot(CellWorld(from));
        Heading = HeadingOf(HexWay(from, heading));
        Settle(0.0f, snap: true);
        _ruts?.Lift();
        return true;
    }

    /// <summary>The ram order to its end: stopped on the middle of the cell
    /// past the wall - or given up when the hull has made no way for a second
    /// (something else is in it).</summary>
    private void WallRamFrame(float dt, Vector2I here)
    {
        if (!_wallRam)
            return;
        if (_fate != Fate.Alive)
        {
            WallRamEnd("танк выбыл");
            return;
        }
        _wallRamStuck = Mathf.Abs(_speed) < 1.0f ? _wallRamStuck + dt : 0.0f;
        if (_wallRamStuck > 1.0f)
        {
            WallRamEnd("встал");
            return;
        }
        Vector3 to = CellWorld(_wallRamTo);
        float left = (to - _rig.Position).Dot(WallWay());
        // Braking from full speed takes speed² / 2a; off with the throttle that
        // far short, and the hull rolls to the middle.
        if (here == _wallRamTo && left <= _speed * _speed / (2.0f * Accel))
            WallRamEnd($"проехал на {_wallRamTo}");
    }

    private void WallRamEnd(string why)
    {
        _wallRam = false;
        _driveScripted = 0.0f;
        _wallNote = $"таран стены: {why}";
        GD.Print($"tank3d: wall ram over: {why}");
    }

    /// <summary>The walls laid again, every edge standing - what
    /// <c>Backspace</c> does to the board.</summary>
    private void WallReset()
    {
        _wallRam = false;
        _wallNose = null;
        _wallBoxed?.Dismount();
        _wallBoxed = null;
        foreach (WallProp prop in _walls)
        {
            prop.Build();
            WallRestate(prop, laying: true);
        }
    }
}
