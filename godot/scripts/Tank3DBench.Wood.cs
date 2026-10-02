using System;
using System.Collections.Generic;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The board's own wear for the 3D tank (docs/tank3d.md, "Трава и лес"): the
/// sprite benches' board under it is their art - a soil tile, the wood drawn
/// by <c>Stage3D.Place</c>, which this scene never calls - so the cells are
/// dressed here in what <c>Tree3D</c> puts on its board:
///
/// <list type="bullet">
/// <item><b>Grass, the cells' ground</b> (<see cref="Grass3D"/>, the tufts on
/// the cel ramp, <c>Tree3D</c>'s board grass): on every level cell of plain
/// ground, bare or wooded - not on sand, water, a ramp, a mine or brick. The
/// hulls press it flat along their belts and the craters tear it out.</item>
/// <item><b>The wood</b> - the generator's models (<see cref="TreeModel"/>) on
/// every wooded cell, <c>Tree3D</c>'s <c>--wood</c> ring, their crowns'
/// line (<see cref="CrownOutline"/>) and the sprite wood's wind.</item>
/// <item><b>It burns</b> - the board's <see cref="Wildfire"/> (cells, spread,
/// each tree's <see cref="Wildfire.Coat"/>), the model burning itself and a
/// <see cref="CelBurn"/> in its crown, as on <c>Tree3D</c>, and the grass of a
/// burning wood with it. What lights it is the rules' (GDD states.md,
/// "Уничтожен"): a tank blowing up lights the wood it stands on, and its
/// blast the wooded cells round it on its own level - each on the frame the
/// fire wave (<see cref="CelDeath.WaveArrives"/>) gets to its edge.</item>
/// <item><b>It goes down under a heavy</b> (<see cref="Bulldoze"/>): the
/// 2D bench's rule (<c>TankTick.UpdateWood</c>, GDD classes.md "Бульдозер
/// HT") - the cell is ground once the heavy drives in, and each trunk goes
/// over as the hull gets to it, the way it is going (<see cref="TreeFall"/>,
/// <c>Tree3D</c>'s fall).</item>
/// <item><b>It gives way to a hull</b> (<see cref="Brush"/>): any other
/// class pushes through it - the trunks it touches bent off its sides and on
/// the way it goes, shaking while it moves, swinging back once it is
/// past.</item>
/// <item><b>It feels a blast</b> - a round's or a tank's: the crowns are
/// thrown back from it and swing back on a spring
/// (<see cref="WoodBlast"/>), each as the blast gets to it, the leaves
/// shivering; and a gun fired in it or by it (<see cref="WoodMuzzle"/>).</item>
/// </list>
/// <c>--no-grass</c>, <c>--no-trees</c>; <c>--trees A,B,..</c> which models,
/// <c>--wood N</c> how many to a cell, <c>--wind x</c> how hard it blows.
/// </summary>
public sealed partial class Tank3DBench
{
    private bool _noGrass, _noTrees;
    private string[] _treeNames = { "Oak_001", "Oak_002", "Oak_003", "Oak_004", "Oak_005", "Oak_006" };
    private int _woodCount = 7;
    private float _wind = 1.0f, _weather;

    private Grass3D? _grass;
    private CrownOutline? _crowns;
    /// <summary>The wood's middle: the crowns' mask holds the depth round it
    /// (<see cref="CrownOutline.MaskReach"/>), not round the tank, which may
    /// be a board away.</summary>
    private Vector3 _woodMiddle;

    private sealed class Planted
    {
        public required Node3D Holder;
        public required TreeModel.Loaded Model;
        public required Vector2I Cell;
        public required CelBurn Fire;
        public required TreeFall Fall;
        public bool Burning, Burnt;
        public float Phase, Rate;
        /// <summary>When in its cell's fire it catches (<see cref="Wildfire.Of"/>),
        /// and the hash of where it stands, to go back to.</summary>
        public float Stagger, Hashed;
        public readonly List<Vector3> Seats = new();
        public readonly List<Vector3> Ports = new();
        /// <summary>The blast's lean on it (world x, z, a share of its height),
        /// its pace, and the leaves' shiver; the blasts still on their way.</summary>
        public Vector2 Push, PushV;
        public float Shiver, Spent;
        /// <summary>Where a hull holds it bent this frame (world x, z, a share
        /// of its height) - the spring's rest; zero once nothing touches it.</summary>
        public Vector2 Bend;
        public readonly List<(float Due, Vector2 Kick, float Shiver)> Kicks = new();
    }

    /// <summary>The wood's fire (<see cref="Wildfire"/>), the cells it may
    /// take, and a blast's lights on their way out (when, which cell, and the
    /// point on its edge the fire comes in over).</summary>
    private Wildfire? _wildfire;
    private readonly HashSet<Vector2I> _woodedCells = new();
    private readonly List<(float Due, Vector2I Cell, Vector2 From)> _lights = new();
    /// <summary>Each burning wood's grass: its meadow, where its front starts
    /// once the cell is lit, and where a blast or a neighbour sent it in from.</summary>
    private readonly Dictionary<Vector2I, int> _meadowOf = new();
    private readonly Dictionary<Vector2I, Vector2> _frontOf = new();
    private readonly Dictionary<Vector2I, Vector2> _entry = new();
    /// <summary>Every cell's grass, and the bare cells' a blast has burnt off:
    /// when, from where, how far (<see cref="WoodSinge"/>).</summary>
    private readonly Dictionary<Vector2I, int> _lawnOf = new();
    private readonly Dictionary<Vector2I, (float Born, Vector2 From, float Stop)> _singed = new();

    /// <summary>How long the grass's flame line takes to roll out to the fire
    /// wave's reach, s: about as long as the wave is out (<see cref="CelDeath.WaveLife"/>
    /// 0.7, most of the way in its first 0.4).</summary>
    private const float SingeWithin = 0.45f;
    private float _woodClock;

    /// <summary>The wooded cells a heavy has made ground (put back by the
    /// reset), and the dust of the crowns coming down.</summary>
    private readonly HashSet<Vector2I> _razed = new();
    private FallDust? _fallDust;

    /// <summary>How far from the heavy's middle the wood goes down, a share of
    /// the cell's width - the board's <c>Grove.FellReach</c>, 140 of its 248.</summary>
    private const float FellReach = 0.56f;

    /// <summary>How fast a blast runs out over the board to the trees past
    /// the fire wave, px/s; the crowns' swing, Hz, and how much of it each
    /// cycle keeps.</summary>
    private const float BlastSpeed = 1100.0f, SwayHz = 0.8f, SwayDamp = 0.18f;
    /// <summary>How long after the grass fire gets to a trunk its tree is
    /// alight, s (<c>Tree3D</c>'s).</summary>
    private const float CatchLag = 0.2f;

    private readonly List<Planted> _trees = new();

    /// <summary>A hull's belt, a share of its width - the stand-in hull's 13 px
    /// of 58 in <c>Tree3D</c>; the belly between them presses it a third.</summary>
    private const float BeltShare = 0.22f, BellyPress = 0.35f;

    /// <summary>A hull pushing through the wood (<see cref="Brush"/>): how far
    /// past its sides the crowns' boughs reach it, a share of the crown's width;
    /// the lean of a trunk it is on, a share of the height; the leaves' shiver
    /// while it moves; and its pace (px/s) at which it is all there.</summary>
    private const float BrushMargin = 0.4f, BrushLean = 0.2f, BrushShiver = 1.6f, BrushPace = 40.0f;
    /// <summary>The cells a hull is brushing the wood on - the log's, once a
    /// cell on the way in.</summary>
    private readonly HashSet<Vector2I> _brushing = new(), _brushed = new();

    /// <summary>The cell's middle on its ground (world).</summary>
    private Vector3 CellMiddle(Vector2I cell)
    {
        Vector2 flat = _field!.FlatAnchor(cell) + _field.CentreOffset;
        return Foot(new Vector3(flat.X, 0.0f, flat.Y / Squash));
    }

    /// <summary>The trees and then the grass round their trunks; after the
    /// camera, which carries the crowns' line.</summary>
    private void BuildWood()
    {
        if (_field is null || _tile is null)
            return;
        if (!_noTrees)
            Plant();
        if (!_noGrass)
            Lawn();
    }

    /// <summary>
    /// <see cref="_woodCount"/> models on each wooded cell, <c>Tree3D</c>'s
    /// <c>--wood</c>: one in the middle, the rest on a ring 60-80 px out (y
    /// squashed as the screen has it), each turned its own way by a hash of
    /// where it stands - the crowns overlap as a wood's do.
    /// </summary>
    private void Plant()
    {
        var wooded = new List<Vector2I>();
        for (int q = 0; q < _field!.Columns; q++)
        for (int r = 0; r < _field.Rows; r++)
        {
            var cell = new Vector2I(q, r);
            if (_field.InBounds(cell) && _field.CoverAt(cell) == Cover.Forest)
                wooded.Add(cell);
        }
        if (wooded.Count == 0)
            return;
        foreach (Vector2I cell in wooded)
            _woodedCells.Add(cell);
        _wildfire = new Wildfire { Field = _field, Enabled = true, Wooded = _woodedCells.Contains };
        if (_stage is not null)
            _stage.Blaze = _wildfire;
        if (!_pbr)
        {
            _crowns = new CrownOutline { Name = "CrownOutline" };
            AddChild(_crowns);
            _crowns.Build(_camera);
        }
        ulong t0 = Time.GetTicksMsec();
        foreach (Vector2I cell in wooded)
        {
            Vector2 flat = _field.FlatAnchor(cell) + _field.CentreOffset;
            for (int i = 0; i < _woodCount; i++)
            {
                Vector2 off = Vector2.Zero;
                if (i > 0)
                {
                    float a = Mathf.Tau * (i - 1) / Mathf.Max(_woodCount - 1, 1) + 0.4f + 0.35f * CelPuff.Hash(i, 613);
                    float d = 60.0f + 20.0f * CelPuff.Hash(i, 617);
                    off = new Vector2(Mathf.Cos(a) * d, Mathf.Sin(a) * d * Squash);
                }
                Vector2 at = flat + off;
                int salt = cell.X * 97 + cell.Y * 31 + i;
                Stand(_treeNames[(salt + i) % _treeNames.Length], new Vector3(at.X, 0.0f, at.Y / Squash),
                      Mathf.Tau * CelPuff.Hash(salt, 611), at, cell);
            }
        }
        foreach (Vector2I cell in wooded)
            _woodMiddle += CellMiddle(cell) / wooded.Count;
        GD.Print($"tank3d: wood on {wooded.Count} cells, {_trees.Count} trees in {Time.GetTicksMsec() - t0} ms");
    }

    private void Stand(string name, Vector3 where, float yaw, Vector2 flat, Vector2I cell)
    {
        if (TreeModel.Load(name, _pbr, out string? error) is not { } model)
        {
            GD.Print($"tank3d: {error}");
            return;
        }
        _crowns?.Ghost(model.Scene, model.Burn, model.Leaves);
        var holder = new Node3D
        {
            Name = $"{name}.{_trees.Count}", Scale = Vector3.One * model.Ppm,
            Rotation = new Vector3(0.0f, yaw, 0.0f),
        };
        holder.AddChild(model.Scene);
        AddChild(holder);
        holder.Position = Foot(where);
        TreeModel.Margin(holder);
        (float phase, float rate) = TreeModel.SwayOf(flat);
        var tree = new Planted
        {
            Holder = holder, Model = model, Cell = cell, Fire = TreeModel.Fire(this, name, model),
            Fall = TreeFall.For(model, holder, model.Burn, this), Phase = phase, Rate = rate,
        };
        tree.Stagger = tree.Hashed = TreeModel.StaggerOf(flat);
        foreach (Vector3 seat in TreeModel.Seats)
            tree.Seats.Add(new Vector3(seat.X * model.Width, seat.Y * model.Height, seat.Z * model.Depth));
        _trees.Add(tree);
    }

    /// <summary>
    /// The cells' grass: <see cref="Grass3D.Kind.CelTufts"/> on every level
    /// cell of plain ground with nothing on it but a wood - the tufts keeping
    /// off the trunks - at the trees' metre, and one press map over the board.
    /// </summary>
    private void Lawn()
    {
        float r = _tile!.HexRect.Size.X * 0.5f;
        var span = new Rect2();
        bool first = true;
        var lay = new List<Vector2I>();
        for (int q = 0; q < _field!.Columns; q++)
        for (int w = 0; w < _field.Rows; w++)
        {
            var cell = new Vector2I(q, w);
            if (!_field.InBounds(cell))
                continue;
            Vector3 m = CellMiddle(cell);
            var box = new Rect2(m.X - r, m.Z - r, 2.0f * r, 2.0f * r);
            span = first ? box : span.Merge(box);
            first = false;
            if (_field.FoundationAt(cell) == Foundation.Solid && !_field.IsRamp(cell) && !_field.IsWater(cell)
                && _field.CoverAt(cell) is Cover.None or Cover.Forest)
                lay.Add(cell);
        }
        _grass = new Grass3D
        {
            Name = "Grass", Ppm = _trees.Count > 0 ? _trees[0].Model.Ppm : 17.0f, Wind = _wind,
            GustRate = TreeModel.GustRate, GustTravel = TreeModel.GustTravel, ShadowInk = Stage3D.ShadowInk.A,
        };
        AddChild(_grass);
        _grass.Map(span);
        ulong t0 = Time.GetTicksMsec();
        for (int i = 0; i < lay.Count; i++)
        {
            Vector3 mid = CellMiddle(lay[i]);
            var trunks = new List<Vector3>();
            foreach (Planted t in _trees)
                if (_field.FlatCellAt(Board(t.Holder.Position)) == lay[i])
                    trunks.Add(t.Holder.Position);
            int meadow = _grass.Lay(Grass3D.Kind.CelTufts, mid, r, 101 + i, trunks);
            // grass burns where a wood does (Tree3D's rule), and is burnt
            // off where a tank's blast rolled over it (WoodSinge)
            _lawnOf[lay[i]] = meadow;
            if (_woodedCells.Contains(lay[i]))
                _meadowOf[lay[i]] = meadow;
        }
        GD.Print($"tank3d: grass on {lay.Count} cells in {Time.GetTicksMsec() - t0} ms");
    }

    /// <summary>A frame of the wood: the wind on the crowns, and the grass
    /// pressed under the hulls.</summary>
    private void WoodTick(float dt)
    {
        _weather += dt;
        _woodClock += dt;
        WoodFire(dt);
        foreach (Planted t in _trees)
            t.Bend = Vector2.Zero;
        _brushed.Clear();
        if (_fate == Fate.Alive && !DeepHere && !_falling)
            Brush(BoxOf(_rig.Position, _heading, _foot), _speed, _modelTag);
        if (_other is { Falling: false, Wet: false } o)
            Brush(BoxOf(o.Rig.Position, o.Heading, o.Foot), dt > 0.0f ? o.Slid / dt : 0.0f, o.Tag);
        _brushing.IntersectWith(_brushed);
        float w = Mathf.Tau * SwayHz;
        foreach (Planted t in _trees)
        {
            // The blasts that have got to it, and its crown's spring.
            for (int k = t.Kicks.Count - 1; k >= 0; k--)
                if (t.Kicks[k].Due <= _woodClock)
                {
                    t.PushV += t.Kicks[k].Kick * w;
                    t.Shiver = Mathf.Max(t.Shiver, t.Kicks[k].Shiver);
                    t.Kicks.RemoveAt(k);
                }
            t.PushV += (-w * w * (t.Push - t.Bend) - 2.0f * SwayDamp * w * t.PushV) * dt;
            t.Push += t.PushV * dt;
            t.Shiver *= Mathf.Exp(-dt / 0.6f);
            if (t.Push.LengthSquared() < 1e-10f && t.PushV.LengthSquared() < 1e-10f)
                t.Push = t.PushV = Vector2.Zero;
            // the wind goes out as the trunk goes over (1 - down², the sprite's)
            float down = t.Fall.Down;
            TreeModel.Sway(t.Holder, t.Model.Burn, t.Model.Leaves, _weather, t.Phase, t.Rate, _wind,
                           1.0f - down * down, t.Spent, t.Push, t.Shiver < 1e-3f ? 0.0f : t.Shiver);
        }
        Bulldoze(dt);
        if (_grass is null)
            return;
        // On its belts: not afloat, not in the air, not once it is going under.
        if (!_falling && !DeepHere && Gone < 0.5f)
        {
            float h = Mathf.DegToRad(_heading);
            var ahead = new Vector3(Mathf.Sin(h), 0.0f, Mathf.Cos(h));
            Trample(_rig.Position, ahead, _foot);
        }
        if (_other is { Falling: false, Wet: false } t2)
            Trample(t2.Rig.Position, t2.Ahead, t2.Foot);
    }

    /// <summary>
    /// A hull (<paramref name="hull"/>, its footprint) in the wood at
    /// <paramref name="speed"/> px/s, signed: every standing trunk whose crown
    /// reaches it - within <see cref="BrushMargin"/> of the crown's width of
    /// its sides - is held bent, off the side it is on and on the way the hull
    /// goes; one the hull is over goes mostly the hull's way. Less a trunk at
    /// the boughs' tips, less a charred one; half held at a standstill, all at
    /// <see cref="BrushPace"/>, and the leaves shaking with the pace - the hull
    /// pushing through, which the spring (<see cref="WoodTick"/>) follows and
    /// rings back from once the hull is past. Any class: a heavy fells the
    /// wood it drives into (<see cref="Raze"/>), and the trunks it gets to
    /// before they go - or a cell off - give way as to any other.
    /// </summary>
    private void Brush(Box hull, float speed, string tag)
    {
        float pace = Mathf.SmoothStep(3.0f, BrushPace, Mathf.Abs(speed));
        Vector2 way = speed < 0.0f ? -hull.A : hull.A;
        foreach (Planted t in _trees)
        {
            if (t.Fall.Going)
                continue;
            Vector3 foot = t.Holder.GlobalPosition;
            Vector2 d = new Vector2(foot.X, foot.Z) - hull.C;
            float along = d.Dot(hull.A), side = d.Dot(hull.L);
            float outLen = Mathf.Max(Mathf.Abs(along) - hull.HalfLen, 0.0f);
            float outWide = Mathf.Max(Mathf.Abs(side) - hull.HalfWide, 0.0f);
            float gap = Mathf.Sqrt(outLen * outLen + outWide * outWide);
            float margin = BrushMargin * t.Model.Width * t.Model.Ppm;
            if (gap >= margin)
                continue;
            float touch = 1.0f - Mathf.SmoothStep(0.0f, margin, gap);
            // off its side, and the hull's way the more it is over the trunk
            float over = 1.0f - Mathf.Clamp(outWide / margin, 0.0f, 1.0f);
            Vector2 off = hull.L * (side < 0.0f ? -1.0f : 1.0f);
            Vector2 dir = (off * (1.0f - 0.6f * over) + way * (0.4f + 0.6f * over)).Normalized();
            // the boughs catching on the hull as it goes: a quick shake across
            float rattle = 0.18f * pace * Mathf.Sin(_woodClock * 11.0f + t.Phase * 5.0f);
            Vector2 across = new Vector2(dir.Y, -dir.X);
            float lean = BrushLean * touch * (0.5f + 0.5f * pace) * Mathf.Lerp(1.0f, 0.35f, t.Spent);
            Vector2 bend = (dir + across * rattle) * lean;
            if (bend.LengthSquared() > t.Bend.LengthSquared())
                t.Bend = bend;
            t.Shiver = Mathf.Max(t.Shiver, BrushShiver * touch * pace);
            if (pace > 0.05f && _brushed.Add(t.Cell) && _brushing.Add(t.Cell))
                GD.Print($"tank3d: {tag} pushes through the wood on {t.Cell}");
        }
    }

    /// <summary>A hull's two belts pressing the grass flat, the way it is
    /// going, and its belly between them less (<see cref="BellyPress"/>).</summary>
    private void Trample(Vector3 rig, Vector3 ahead, (float Along, float Across, float HalfLen, float HalfWide) foot)
    {
        var left = new Vector3(ahead.Z, 0.0f, -ahead.X);
        Vector3 middle = rig + ahead * foot.Along + left * foot.Across;
        var way = new Vector2(ahead.X, ahead.Z);
        float belt = BeltShare * 2.0f * foot.HalfWide;
        foreach (float side in new[] { -1.0f, 1.0f })
            _grass!.Press(middle + left * (side * (foot.HalfWide - belt * 0.5f)), way, 2.0f * foot.HalfLen, belt, 1.0f);
        _grass!.Press(middle, way, 1.8f * foot.HalfLen, 2.0f * (foot.HalfWide - belt), BellyPress);
    }

    /// <summary>
    /// The heavies in the wood this frame - the bench's own and the target -
    /// and every falling tree's frame: its pose, the dust where its crown
    /// comes down, and the grass its root plate tears out.
    /// </summary>
    private void Bulldoze(float dt)
    {
        if (_profile.Bulldozes && _fate == Fate.Alive && !DeepHere && !_falling)
        {
            float h = Mathf.DegToRad(_heading);
            var ahead = new Vector3(Mathf.Sin(h), 0.0f, Mathf.Cos(h));
            Raze(_rig.Position, _speed < 0.0f ? -ahead : ahead, _speed);
        }
        if (_other is { Falling: false, Wet: false } o && o.Profile.Bulldozes)
            Raze(o.Rig.Position, o.Way, o.Speed);
        foreach (Planted t in _trees)
        {
            if (!t.Fall.Step(dt))
                continue;
            _fallDust ??= new FallDust(this);
            _fallDust.Add(t.Fall, t.Burnt);
        }
        _fallDust?.Settle(dt, _camera.GlobalBasis, Foot);
        if (_grass is null)
            return;
        foreach (Planted t in _trees)
        {
            if (!t.Fall.Going)
                continue;
            TreeFall f = t.Fall;
            float plate = f.Of.Plate > 0.0f ? f.Of.Plate : 1.4f;
            float grow = Mathf.SmoothStep(0.02f, 0.30f, (float)f.Angle);
            _grass.Cut(t.Holder.GlobalPosition, plate * f.Ppm * grow * 0.85f, f.WorldDir, f.Of.Hinge * f.Ppm);
        }
    }

    /// <summary>
    /// A heavy at <paramref name="at"/> going <paramref name="way"/> at
    /// <paramref name="speed"/> (px/s, signed): the 2D bench's
    /// <c>TankTick.UpdateWood</c>. The cell under its middle - the nose's
    /// too spent the cell ahead while the nose only grazed it, and left its
    /// trees standing on ground; a wood burning there does not go (the rules' "горящий гекс землёй не
    /// становится" - the fire finishes first), alive or burnt out it does.
    /// Driving in spends the cell - it is ground from then, and burns no more
    /// - and standing on a spent one goes on felling: each trunk within
    /// <see cref="FellReach"/> of the hull goes over as the hull gets to it,
    /// the way it is going, a trunk off its line some degrees out to its own
    /// side (<c>Tree3D</c>'s wood: a hull lays the wood both ways).
    /// </summary>
    private void Raze(Vector3 at, Vector3 way, float speed)
    {
        if (_field is null || way.LengthSquared() < 1e-6f)
            return;
        way = way.Normalized();
        var cells = new HashSet<Vector2I> { _field.FlatCellAt(Board(at)) };
        float reach = FellReach * HexWidth;
        var across = new Vector3(way.Z, 0.0f, -way.X);
        foreach (Vector2I cell in cells)
        {
            bool spent = _razed.Contains(cell);
            if (!spent && !_woodedCells.Contains(cell))
                continue;
            bool burning = false;
            foreach (Planted t in _trees)
                if (t.Cell == cell && (t.Burning || (_wildfire?.LitAt(cell) ?? false) && !t.Burnt))
                    burning = true;
            if (burning)
                continue;
            if (!spent)
            {
                if (Mathf.Abs(speed) < 1.0f)
                    continue;
                // Ground from the drive in: the rules' answer about the hex,
                // given once; the trees go down as the hull gets to them.
                _woodedCells.Remove(cell);
                _razed.Add(cell);
                GD.Print($"tank3d: the heavy is in the wood on {cell}: it is ground now");
            }
            foreach (Planted t in _trees)
            {
                if (t.Cell != cell || t.Fall.Going)
                    continue;
                Vector3 foot = t.Holder.GlobalPosition;
                var rel = new Vector3(foot.X - at.X, 0.0f, foot.Z - at.Z);
                if (rel.Length() >= reach)
                    continue;
                // off the hull's line, out to its own side: 6-30 degrees
                float side = rel.Dot(across);
                float off = Mathf.DegToRad(6.0f + 24.0f * Mathf.Clamp(Mathf.Abs(side) / reach, 0.0f, 1.0f))
                            * Mathf.Sign(side == 0.0f ? 1.0f : side);
                Vector3 over = way.Rotated(Vector3.Up, off);
                t.Fall.Start(new Vector2(over.X, over.Z), Mathf.Abs(speed) / 120.0f, t.Burnt, "tank3d");
            }
        }
    }

    /// <summary>
    /// A blast at <paramref name="at"/> (world, on the ground) as the wood
    /// feels it: each crown thrown back from it by <paramref name="might"/>
    /// of its height at the blast, less by <c>reach² / (reach² + d²)</c> out
    /// to it, on the frame the blast gets there (<see cref="BlastSpeed"/>),
    /// and its leaves shivering. A round's (<c>might</c> 0.08-0.2 by the
    /// gun's firepower, a reach of 90-210 px) stirs the wood of its cell
    /// and the next; a tank's (0.32 over 300 px) swings the cells round it. The spring keeps
    /// some three quarters of the kick for its first swing: at half these the
    /// first swing was 0.05 of the height, 6 px at the top, and did not read;
    /// over a reach of 90 px a crown a cell off moved 1-3 px.
    /// </summary>
    private void WoodBlast(Vector3 at, float might, float reach)
    {
        foreach (Planted t in _trees)
        {
            Vector3 foot = t.Holder.GlobalPosition;
            var away = new Vector2(foot.X - at.X, foot.Z - at.Z);
            float d = away.Length();
            Vector2 dir = d > 1.0f ? away / d : Vector2.Right;
            float a = Mathf.Min(might * reach * reach / (reach * reach + d * d), 0.4f);
            if (a < 0.004f)
                continue;
            t.Kicks.Add((_woodClock + d / BlastSpeed, dir * a, a / 0.32f * 3.0f));
        }
    }

    /// <summary>
    /// A gun fired at <paramref name="muzzle"/> along <paramref name="bore"/>
    /// as the wood feels it (<see cref="WoodBlast"/>'s kick): the muzzle's
    /// blast throws the crowns off it, hardest down the line of fire and out
    /// to half as far again there, a third as hard behind the gun - the wood a
    /// tank fires from heaves round it, and the trees it fires through or past.
    /// By the gun's firepower, a crater's share (<see cref="CraterShare"/>):
    /// 0.06-0.14 of the height at the muzzle over 70-170 px.
    /// </summary>
    private void WoodMuzzle(Vector3 muzzle, Vector3 bore, int might)
    {
        float share = CraterShare(might);
        float a0 = 0.1f * share, r0 = 120.0f * share;
        var line = new Vector2(bore.X, bore.Z);
        float flat = line.Length();
        foreach (Planted t in _trees)
        {
            Vector3 foot = t.Holder.GlobalPosition;
            var away = new Vector2(foot.X - muzzle.X, foot.Z - muzzle.Z);
            float d = away.Length();
            Vector2 dir = d > 1.0f ? away / d : (flat > 0.05f ? line / flat : Vector2.Right);
            // a mortar's bore stands up: the blast goes all round
            float ahead = flat > 0.05f ? Mathf.Lerp(0.5f, dir.Dot(line / flat), Mathf.Clamp(flat * 1.5f, 0.0f, 1.0f)) : 0.5f;
            float front = Mathf.SmoothStep(-0.3f, 0.9f, ahead);
            float reach = r0 * (0.6f + 0.9f * front);
            float a = Mathf.Min(a0 * (0.33f + 0.67f * front) * reach * reach / (reach * reach + d * d), 0.3f);
            if (a < 0.004f)
                continue;
            t.Kicks.Add((_woodClock + d / BlastSpeed, dir * a, a / 0.32f * 4.0f));
        }
    }

    /// <summary>
    /// A tank blowing up at <paramref name="at"/> lights the wood (GDD
    /// states.md, "Уничтожен"): the cell it stands on, now, and every wooded
    /// neighbour on its own level - a rise's or a hollow's it does not reach
    /// - on the frame the fire wave gets to the edge between them, the fire
    /// coming in over that edge. Afloat there is no fire wave: the
    /// neighbours a beat after the blast. A burnt wood is not lit again
    /// (<see cref="Wildfire.Light"/> refuses). On the ground the wave burns
    /// off the bare cells' grass too (<see cref="WoodSinge"/>).
    /// </summary>
    private void WoodIgnite(Vector3 at, bool grounded)
    {
        if (grounded)
            WoodSinge(at);
        if (_wildfire is null || _field is null)
            return;
        Vector2I here = _field.FlatCellAt(Board(at));
        if (_woodedCells.Contains(here))
            _lights.Add((_woodClock, here, new Vector2(at.X, at.Z)));
        int level = _field.LevelAt(here);
        Vector3 mid = CellMiddle(here);
        foreach (int heading in HexField.EdgeHeadings)
        {
            Vector2I next = HexField.Step(here, heading);
            if (next == here || !_field.InBounds(next) || !_woodedCells.Contains(next) || _field.LevelAt(next) != level)
                continue;
            Vector3 edge = 0.5f * (mid + CellMiddle(next));
            float d = new Vector2(edge.X - at.X, edge.Z - at.Z).Length();
            float when = grounded && _celDeath is not null ? _celDeath.WaveArrives(d) : float.PositiveInfinity;
            if (float.IsInfinity(when))
                when = d / BlastSpeed;
            _lights.Add((_woodClock + when, next, new Vector2(edge.X, edge.Z)));
            GD.Print($"tank3d: blast on {here} lights the wood on {next} in {when:F2} s");
        }
    }

    /// <summary>
    /// A tank's fire wave burns the grass it rolls over - the cell it stands
    /// on and its neighbours on its own level, out to the wave's reach
    /// (<see cref="CelDeath.WaveReach"/> of a cell) from the blast and no
    /// further: a flame line running out with the wave, char, stubble and
    /// embers, a little smoke. Decoration, not a fire: the cell is not lit,
    /// burns nothing and hands nothing on - the rules have fire in a wood only
    /// (GDD field.md, "Вне леса пожара не бывает"); a wooded cell burns as a
    /// wood does (<see cref="WoodIgnite"/>). Burnt once: a second blast over
    /// the same grass finds stubble. None afloat - there is no wave.
    /// </summary>
    private void WoodSinge(Vector3 at)
    {
        if (_grass is null || _field is null || _celDeath is null)
            return;
        Vector2I here = _field.FlatCellAt(Board(at));
        int level = _field.LevelAt(here);
        float reach = _celDeath.WaveReach * HexWidth;
        var cells = new List<Vector2I> { here };
        foreach (int heading in HexField.EdgeHeadings)
            cells.Add(HexField.Step(here, heading));
        foreach (Vector2I cell in cells)
        {
            if (!_lawnOf.ContainsKey(cell) || _woodedCells.Contains(cell) || _singed.ContainsKey(cell)
                || _field.LevelAt(cell) != level)
                continue;
            _singed[cell] = (_woodClock, new Vector2(at.X, at.Z), reach);
        }
    }

    /// <summary>
    /// A frame of the wood's fire: a blast's lights as they fall due, the
    /// board's fire, the grass of each burning wood - its front from where the
    /// fire came in, and with it when each tree catches: as the line gets to
    /// its foot (<see cref="Grass3D.Arrival"/>), as on <c>Tree3D</c> - and each
    /// tree's coat on its model and its fire.
    /// </summary>
    private void WoodFire(float dt)
    {
        if (_wildfire is null)
        {
            // a board with no wood still has grass for a blast to burn off
            foreach ((Vector2I cell, (float born, Vector2 from, float stop)) in _singed)
                _grass?.Burn(_lawnOf[cell], _woodClock - born, from, SingeWithin, stop);
            return;
        }
        for (int i = _lights.Count - 1; i >= 0; i--)
            if (_lights[i].Due <= _woodClock)
            {
                (_, Vector2I cell, Vector2 from) = _lights[i];
                _lights.RemoveAt(i);
                if (_wildfire.Light(cell))
                    _entry[cell] = from;
            }
        _wildfire.Tick(dt);
        foreach ((Vector2I cell, (float born, Vector2 from, float stop)) in _singed)
            _grass?.Burn(_lawnOf[cell], _woodClock - born, from, SingeWithin, stop);
        float within = _wildfire.CatchWithin - CatchLag;
        foreach ((Vector2I cell, int meadow) in _meadowOf)
        {
            float age = _wildfire.AgeAt(cell);
            bool lit = age >= 0.0f && !_frontOf.ContainsKey(cell);
            if (age < 0.0f)
                _frontOf.Remove(cell);
            else if (lit)
                _frontOf[cell] = FrontFrom(cell);
            _grass!.Burn(meadow, age, _frontOf.TryGetValue(cell, out Vector2 f) ? f : Vector2.Zero, within);
            foreach (Planted t in _trees)
            {
                if (t.Cell != cell)
                    continue;
                if (age < 0.0f)
                    t.Stagger = t.Hashed;
                else if (lit)
                {
                    Vector3 foot = t.Holder.GlobalPosition;
                    float catches = _grass.Arrival(meadow, new Vector2(foot.X, foot.Z)) + CatchLag;
                    t.Stagger = Mathf.Clamp(catches / Mathf.Max(_wildfire.CatchWithin, 1e-3f), 0.0f, 1.0f);
                }
            }
        }
        Basis eye = _camera.GlobalBasis;
        foreach (Planted t in _trees)
        {
            Wildfire.Coat coat = _wildfire.Of(t.Cell, t.Stagger);
            foreach (ShaderMaterial m in t.Model.Burn)
            {
                m.SetShaderParameter("burn", coat.Spent);
                m.SetShaderParameter("charred", coat.Char);
            }
            t.Fire.Fire = coat.Flame;
            t.Fire.Smoke = coat.Smoke;
            t.Fire.Smoulder = coat.Burnt;
            t.Spent = coat.Spent;
            t.Burning = coat.Flame > 0.01f;
            t.Burnt = coat.Burnt || coat.Spent >= 1.0f;
            // The flame goes down with the crown, onto the limbs and the fork.
            float down = coat.Spent * coat.Spent;
            t.Ports.Clear();
            foreach (Vector3 p in t.Seats)
                t.Ports.Add(t.Holder.ToGlobal(t.Fall.Pose(p.Lerp(new Vector3(p.X * 0.4f, p.Y * 0.62f, p.Z * 0.3f), down))));
            t.Fire.Tick(dt, t.Ports, eye);
        }
    }

    /// <summary>Where a lit cell's grass front starts: where a blast sent the
    /// fire in over its edge; else the edge with the neighbour that has burnt
    /// longest past its delay, a little out on the neighbour's side (the
    /// fire's own spread, <see cref="Wildfire.Delay"/>); else the middle.</summary>
    private Vector2 FrontFrom(Vector2I cell)
    {
        Vector3 mid = CellMiddle(cell);
        if (_entry.Remove(cell, out Vector2 entry))
            return entry;
        Vector2I? best = null;
        float most = float.MinValue;
        foreach (int heading in HexField.EdgeHeadings)
        {
            Vector2I next = HexField.Step(cell, heading);
            if (next == cell || !_field!.InBounds(next))
                continue;
            float past = _wildfire!.AgeAt(next);
            if (past < 0.0f)
                continue;
            past -= _wildfire.Delay(next, cell);
            if (past > most)
            {
                most = past;
                best = next;
            }
        }
        if (best is not { } from)
            return new Vector2(mid.X, mid.Z);
        Vector3 edge = mid + (CellMiddle(from) - mid) * 0.62f;
        return new Vector2(edge.X, edge.Z);
    }

    /// <summary>A crater tears the grass out over its rim: earth thrown up,
    /// not a lawn.</summary>
    private void WoodCrater(Vector3 at, float radius) =>
        _grass?.Cut(at, radius * CelCrater.FootMost, Vector2.Zero, 0.0f);

    /// <summary>The crowns' mask onto the camera, its depth slab round the wood.</summary>
    private void WoodFollow()
    {
        if (_crowns is null)
            return;
        float depth = (_woodMiddle - _camera.Position).Dot(-_camera.Basis.Z);
        _crowns.Follow(_camera, _zoom, depth);
    }

    /// <summary>The grass standing again and the wood green, still.</summary>
    private void WoodReset()
    {
        foreach (Vector2I cell in _razed)
            _woodedCells.Add(cell);
        _razed.Clear();
        _fallDust?.Clear();
        _brushing.Clear();
        _grass?.Heal();
        foreach (Vector2I cell in _singed.Keys)
            _grass?.Burn(_lawnOf[cell], -1.0f, Vector2.Zero);
        _singed.Clear();
        _wildfire?.Douse();
        _lights.Clear();
        _entry.Clear();
        foreach (Planted t in _trees)
        {
            t.Fire.Reset();
            t.Push = t.PushV = t.Bend = Vector2.Zero;
            t.Shiver = 0.0f;
            t.Kicks.Clear();
            t.Fall.Reset();
        }
    }
}
