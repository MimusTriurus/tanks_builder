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
/// <item><b>A round stops on it</b> (<see cref="WallMeets"/>, <see cref="WallStruck"/>):
/// a gun's round crossing a standing edge below the wall's top is spent on
/// it and holes it (<see cref="WallRig.Strike.Ap"/>, <see cref="WallField.Struck"/>'s
/// call), with a burst of brick dust on the face it came in by; a ricochet
/// too. A destroyer's round goes through the first wall and flies on - its
/// one pass (GDD classes.md, "TD"). A bomb goes over the walls, and on a
/// walled hex brings down every wall on its edges; so does a tank's blast
/// (<see cref="WallsBlown"/>).</item>
/// <item><b>The concrete capon is masonry that holds</b> (<see cref="WallProp.Concrete"/>,
/// <see cref="CaponKit"/>; docs/wall.md, "Капонир"): no ram and no heavy goes
/// through it, a gun's round is spent on it and leaves it standing - the
/// destroyer's too, its pass is for a wall it breaks - and the slit lets a
/// round through at its own height, out and in. Only the mortar's bomb on the
/// roof breaks it (<see cref="WallRig.Strike.Cp"/>, <see cref="CaponRoof"/>).
/// It is entered by the gate along its axis, and inside the hull keeps to the
/// axis and does not turn (<see cref="ShelterHolds"/>).</item>
/// <item><b>On this board the capon is the hex bunker</b> (<see cref="BunkerProp"/>,
/// <c>assets/Models/Bunker/</c>): the model stands where the capon's prop
/// stands, the prop's own pieces unseen, and the capon's recipe is made the
/// model's - roof and slit - so the bomb's roof and the round's slit are the
/// ones drawn. The bomb on the roof breaks the model too: it falls in on its
/// own hex, on the tank in it, and goes under in its own dust to its pad.</item>
/// <item>An edge goes off the board's record when its masonry falls, not when
/// it is struck (<see cref="WallTick"/>); <c>Backspace</c> lays every wall
/// again.</item>
/// </list>
/// <c>--no-walls</c> leaves the cells bare; <c>--no-bunker</c> draws the capon
/// as the 2D bench lays it.
/// </summary>
public sealed partial class Tank3DBench
{
    private bool _noWalls;
    private bool _noBunker;
    private readonly List<WallProp> _walls = new();

    /// <summary>The bunker standing on each capon's cell.</summary>
    private readonly Dictionary<Vector2I, BunkerProp> _bunkers = new();
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

    /// <summary>The walls a round goes through on its way - a destroyer's one
    /// pass - struck when it gets to them: the round, how far along its path,
    /// and what the wall gets.</summary>
    private readonly List<(CelShell.Round Round, float Along, Action Hit)> _wallsAhead = new();

    /// <summary>The burst a round makes on brick - the plate's HE burst
    /// (<see cref="CelBlast"/>) in the masonry's colours: a short flash, brick
    /// dust instead of soot, the dust off the ground the wall's own.</summary>
    private CelBlast? _wallBurst;

    /// <summary>The one gun whose round goes through what it meets first - the
    /// destroyer's: no turret, and no lob (GDD classes.md, "TD - Тяжёлый
    /// снаряд"; the mortar is the other casemate).</summary>
    private bool ShootsThrough => !_profile.Turreted && !_profile.Lobs;

    /// <summary>How hard a round pushes the masonry: the board's round
    /// (<see cref="Ordnance"/>, the 2D bench's force at its default) by the
    /// gun's might, 0.8 for a light's to 1.2 for the mortar's.</summary>
    /// <summary>A bomb on a walled hex against a round, for each of its
    /// leaves: at the round's own push all six went a cell and more out over
    /// the neighbours - a bomb in a ring is six pushes from one point.</summary>
    private const float BlownShare = 0.55f;

    /// <summary>A tank's blast on each leaf round its hex - its own, not a
    /// gun's: at a medium's bomb share (0.5) the leaves were let go of and
    /// stood where they were, the ring still up round the wreck.</summary>
    private const float TankBlast = 0.85f;

    private float WallForce => Ordnance.At(1) * (0.8f + 0.1f * (Mathf.Clamp(_profile.Might, 1, 5) - 1));

    /// <summary>The burst a round makes on the capon's concrete - the brick
    /// one's numbers in grey: concrete dust, no brick in it.</summary>
    private CelBlast? _concreteBurst;

    /// <summary>The bomb that breaks a capon: the board's HE burst at
    /// <see cref="CaponBlastSize"/> of its size, its smoke concrete-grey - the
    /// one charge here that levels a box reads louder than any (docs/wall.md,
    /// "Картинка удара": the 2D board's is three calibres).</summary>
    private CelBlast? _caponBlast;
    private const float CaponBlastSize = 2.2f;

    /// <summary>How far off the capon's axis a hull may come in by its gate,
    /// degrees.</summary>
    private const float ShelterSlackDeg = 25.0f;

    /// <summary>How fast the hull is drawn on to the capon's axis inside it,
    /// per second.</summary>
    private const float ShelterPull = 6.0f;

    /// <summary>The capon standing on <paramref name="cell"/>: concrete the
    /// board still holds a side of - after the bomb it holds none.</summary>
    private WallProp? Capon(Vector2I cell)
    {
        if (_field is null)
            return null;
        foreach (WallProp prop in _walls)
            if (prop.Concrete && prop.Cell == cell && _field.SidesAt(cell) != 0)
                return prop;
        return null;
    }

    /// <summary>Whether the side across <paramref name="heading"/> out of
    /// <paramref name="here"/> is a capon's standing concrete.</summary>
    private bool ConcreteEdge(Vector2I here, int heading)
    {
        Vector2I next = HexField.Step(here, heading);
        foreach (WallProp prop in _walls)
            if (prop.Concrete
                && ((prop.Cell == here && _field!.SideStands(here, heading))
                    || (prop.Cell == next && _field!.SideStands(next, HexField.Reverse(heading)))))
                return true;
        return false;
    }

    /// <summary>The capon's axis in the world, gate to slit: the slit faces
    /// the prop's bearing.</summary>
    private Vector3 ShelterAxis(WallProp prop) => HexWay(prop.Cell, Mathf.RoundToInt(prop.Bearing));

    /// <summary>A step on to a capon's cell is refused unless the hull goes
    /// in along its axis - the only way it fits (docs/wall.md, "разворота
    /// внутри нет"); <see cref="CanDrive"/>'s half about the gate.</summary>
    private bool ShelterRefuses(Vector2I there, Vector3 way)
    {
        if (Capon(there) is not { } prop || way.LengthSquared() < 1e-8f)
            return false;
        return way.Normalized().Dot(ShelterAxis(prop)) < Mathf.Cos(Mathf.DegToRad(ShelterSlackDeg));
    }

    /// <summary>
    /// The hull in a capon: drawn on to its axis - the heading on the nearer
    /// way along it, the middle on the line gate to slit - and no turn. True
    /// while it is in, so the keys' turn is dropped.
    /// </summary>
    private bool ShelterHolds(float dt)
    {
        if (_field is null || _walls.Count == 0)
            return false;
        Vector2I here = _field.FlatCellAt(Board(_rig.Position));
        if (Capon(here) is not { } prop)
            return false;
        Vector3 axis = ShelterAxis(prop);
        float k = 1.0f - Mathf.Exp(-ShelterPull * dt);
        float want = HeadingOf(axis);
        if (Mathf.Abs(Mathf.AngleDifference(Mathf.DegToRad(_heading), Mathf.DegToRad(want))) > Mathf.Pi * 0.5f)
            want += 180.0f;
        Heading = Mathf.RadToDeg(Mathf.LerpAngle(Mathf.DegToRad(_heading), Mathf.DegToRad(want), k));
        Vector3 off = _rig.Position - CellWorld(prop.Cell);
        off.Y = 0.0f;
        Vector3 aside = off - axis * off.Dot(axis);
        _rig.Position -= aside * k;
        return true;
    }

    /// <summary>
    /// Whether a bomb's step from <paramref name="p"/> to <paramref name="next"/>
    /// comes down on a standing capon, and where: on to its roof, or - the
    /// bomb's fall is shallower than the box is tall a cell out, and it comes
    /// into the cell under the roof's height - on the side it came in by. The
    /// box is one target to the bomb either way. Not a bomb from in it.
    /// </summary>
    private (WallProp Prop, Vector3 At)? CaponRoof(Vector3 p, Vector3 next)
    {
        if (_field is null)
            return null;
        Vector2I cell = _field.FlatCellAt(Board(next));
        if (Capon(cell) is not { Stack: not null } prop)
            return null;
        float top = prop.Stack.Anchor.Y + prop.Pile().Top * WallRadius;
        if (next.Y > top)
            return null;
        Vector3 at;
        if (_field.FlatCellAt(Board(p)) == cell)
        {
            if (p.Y <= top)
                return null;
            at = p;
        }
        else
        {
            // The rim, between the two (WallMeets' bisection).
            float lo = 0.0f, hi = 1.0f;
            for (int i = 0; i < 10; i++)
            {
                float mid = 0.5f * (lo + hi);
                if (_field.FlatCellAt(Board(p.Lerp(next, mid))) != cell)
                    lo = mid;
                else
                    hi = mid;
            }
            at = p.Lerp(next, hi);
            if (at.Y <= top)
                return (prop, at);
        }
        // Over the rim and down on to the roof.
        at = at.Lerp(next, Mathf.Clamp((at.Y - top) / Mathf.Max(at.Y - next.Y, 1e-4f), 0.0f, 1.0f));
        at.Y = top;
        return (prop, at);
    }

    /// <summary>
    /// The mortar's bomb on a capon's roof - the one round that breaks it
    /// (<see cref="WallRig.Strike.Cp"/>, the rig's <c>Shatter</c>: every slab
    /// out along its own normal, the roof down on to what was under it, the
    /// six sections broken). The bomb's burst on the roof with the concrete's
    /// dust over it; the board takes the sides off as the slabs go
    /// (<see cref="WallTick"/>).
    /// </summary>
    private Action CaponRoofed(WallProp prop, Vector3 at, Vector3 way) => () =>
    {
        if (!IsInstanceValid(prop))
            return;
        var flat = new Vector2(way.X, way.Z * Squash);
        prop.Fire(WallRig.Strike.Cp, prop.Into(flat.LengthSquared() > 1e-8f ? flat.Normalized() : Vector2.Down),
                  WallForce, false, float.NegativeInfinity);
        if (_bunkers.TryGetValue(prop.Cell, out BunkerProp? bunker))
            bunker.Break(way, BunkerOccupants(prop.Cell));
        if (_caponBlast is null)
        {
            _caponBlast = new CelBlast
            {
                Name = "CaponBlast",
                SootPuffs = 34, SootLife = 2.2f, SootTint = new Color(0.82f, 0.81f, 0.78f),
                SootDark = 0.42f, SootGrey = 0.66f,
                Fragments = 26, FragmentSpeed = 1.8f, FragmentCone = 110.0f,
                DustTint = new Color(0.74f, 0.74f, 0.72f), DustTone = 0.68f,
            };
            AddChild(_caponBlast);
            _caponBlast.Build(CaponBlastSize * _model.HullLength * _model.PixelsPerUnit);
        }
        _caponBlast.Burst(at, Vector3.Up, Foot(at));
        WallBurst(at, Vector3.Up, concrete: true);
        _shake.Blast(_profile.ShotShake * 0.8);
        WoodBlast(Foot(at), 0.12f, 150.0f);
        GD.Print($"tank3d: the bomb on the capon's roof on {prop.Cell}: the concrete comes down");
    };

    /// <summary>
    /// Whether a round's step from <paramref name="p"/> to
    /// <paramref name="next"/> meets standing masonry: an edge on the way that
    /// the board still calls walled (<see cref="HexField.Walled"/>, the record
    /// the bricks write), crossed no higher than the wall's top - a gun laid
    /// level over a wall lower than its muzzle sends the round over it. Where
    /// it crosses the rim, and whose wall it is. Not asked for a bomb: it
    /// comes down from above past everything between (GDD classes.md, "HM").
    /// </summary>
    private (WallProp Prop, Vector3 At)? WallMeets(Vector3 p, Vector3 next)
    {
        if (_walls.Count == 0 || _field is null)
            return null;
        Vector2I a = _field.FlatCellAt(Board(p)), b = _field.FlatCellAt(Board(next));
        if (a == b)
            return null;
        List<Vector2I> chain = HexField.Chain(a, b);
        for (int leg = 1; leg < chain.Count; leg++)
        {
            Vector2I from = chain[leg - 1], to = chain[leg];
            int heading = HexField.HeadingTo(from, to);
            if (heading < 0 || !_field.Walled(from, heading))
                continue;
            WallProp? prop = null;
            foreach (WallProp w in _walls)
                if ((w.Cell == from && _field.SideStands(from, heading))
                    || (w.Cell == to && _field.SideStands(to, HexField.Reverse(heading))))
                    prop = w;
            if (prop?.Stack is null)
                continue;
            // The rim, between the two: the first point of the step past the
            // cell it left.
            float lo = 0.0f, hi = 1.0f;
            for (int i = 0; i < 10; i++)
            {
                float mid = 0.5f * (lo + hi);
                if (_field.FlatCellAt(Board(p.Lerp(next, mid))) == from)
                    lo = mid;
                else
                    hi = mid;
            }
            Vector3 at = p.Lerp(next, hi);
            float top = prop.Stack.Anchor.Y + prop.Pile().Top * WallRadius;
            if (at.Y > top)
                continue;
            // The capon's slit: a side the hull is barred by and a round is
            // not (WallProp.Bars, crossing), between its sill and its lintel.
            if (prop.Capon is { } capon)
            {
                Vector2I other = prop.Cell == from ? to : from;
                float floor = prop.Stack.Anchor.Y;
                if (!prop.Bars(_field.FlatAnchor(other) - _field.FlatAnchor(prop.Cell))
                    && at.Y >= floor + capon.SlitLow * WallRadius && at.Y <= floor + capon.SlitHigh * WallRadius)
                    continue;
            }
            return (prop, at);
        }
        return null;
    }

    /// <summary>
    /// What a round does to the wall it met at <paramref name="at"/> going
    /// <paramref name="way"/>: the solver's burst into the masonry
    /// (<see cref="WallRig.Strike.He"/>), which brings the section down - the
    /// rules' "стена разрушена" for every round; the 2D bench's
    /// <see cref="WallRig.Strike.Ap"/> only holes it and leaves the edge
    /// standing (<see cref="WallRig.Breaching"/>) - from inside when it was fired from the wall's own cell
    /// (<see cref="WallField.Struck"/>'s <c>from</c>), and the burst on the
    /// face it came in by. <paramref name="through"/>: the destroyer's round,
    /// which flies on.
    /// </summary>
    private Action WallStruck(WallProp prop, Vector3 at, Vector3 way, bool inside, bool lob, bool through) => () =>
    {
        if (!IsInstanceValid(prop))
            return;
        var flat = new Vector2(way.X, way.Z * Squash);
        // Concrete takes no gun's round (WallRig.Cracks): nothing to push.
        if (flat.LengthSquared() > 1e-8f && !prop.Concrete)
            prop.Fire(WallRig.Strike.He, prop.Into(flat.Normalized()), WallForce,
                      false, inside ? 0.0f : float.NegativeInfinity);
        WallBurst(at, -new Vector3(way.X, 0.0f, way.Z), prop.Concrete);
        _shake.Blast(_profile.ShotShake * 0.35);
        WoodBlast(Foot(at), 0.08f, 110.0f);
        GD.Print($"tank3d: round on the {(prop.Concrete ? "capon" : "wall")} {prop.Cell} {(inside ? "from inside" : "from outside")}"
                 + (through ? ", through it" : prop.Concrete ? ", spent on the concrete" : ", spent on it"));
    };

    /// <summary>The brick dust and the flash on a face looking
    /// <paramref name="n"/> - the masonry's <see cref="CelBlast"/>; grey for
    /// <paramref name="concrete"/>.</summary>
    private void WallBurst(Vector3 at, Vector3 n, bool concrete = false)
    {
        ref CelBlast? burst = ref concrete ? ref _concreteBurst : ref _wallBurst;
        if (burst is null)
        {
            burst = new CelBlast
            {
                Name = concrete ? "ConcreteBurst" : "WallBurst",
                FireSize = 0.13f, FireTime = 5.0f / 60.0f, GlowEnergy = 0.35f,
                SootPuffs = 30, SootLife = 1.8f, SootReach = 0.36f, SootGrown = 0.13f,
                SootDark = 0.50f, SootGrey = 0.70f,
                SootTint = concrete ? new Color(0.80f, 0.80f, 0.78f) : new Color(0.86f, 0.62f, 0.44f),
                Fragments = 18, FragmentSpeed = 1.6f, FragmentCone = 80.0f,
                DustTint = concrete ? new Color(0.74f, 0.74f, 0.72f) : new Color(0.84f, 0.68f, 0.52f),
                DustTone = 0.68f,
            };
            AddChild(burst);
            burst.Build(_model.HullLength * _model.PixelsPerUnit);
        }
        if (n.Y < 0.5f)
            n.Y = 0.25f;
        burst.Burst(at, n.Normalized(), Foot(at));
    }

    /// <summary>
    /// A blast on the hex under <paramref name="at"/> - a bomb come down on
    /// it, a tank blown up on it: every standing wall on its edges is brought
    /// down (GDD classes.md, "стены без танка - сносятся все стены на гранях
    /// гекса"; states.md, "Уничтожен"). The hex's own walls pushed out from its
    /// middle, a neighbour's wall on the shared edge pushed in from this side -
    /// each a bomb's push on that leaf.
    /// </summary>
    private void WallsBlown(Vector3 at, string what, float force)
    {
        if (_walls.Count == 0 || _field is null)
            return;
        Vector2I cell = _field.FlatCellAt(Board(at));
        Vector2 middle = _field.FlatAnchor(cell);
        int blown = 0;
        foreach (int heading in HexField.EdgeHeadings)
        {
            Vector2I next = HexField.Step(cell, heading);
            Vector2 out_ = (_field.FlatAnchor(next) - middle).Normalized();
            foreach (WallProp prop in _walls)
            {
                // Concrete only to the bomb on its roof (CaponRoofed).
                if (prop.Concrete)
                    continue;
                if (prop.Cell == cell && _field.SideStands(cell, heading))
                {
                    prop.Fire(WallRig.Strike.He, prop.Into(out_), force, false, 0.0f);
                    blown++;
                }
                else if (prop.Cell == next && _field.SideStands(next, HexField.Reverse(heading)))
                {
                    prop.Fire(WallRig.Strike.He, prop.Into(out_), force, false, float.NegativeInfinity);
                    blown++;
                }
            }
        }
        if (blown > 0)
            GD.Print($"tank3d: {what} on {cell} brings down {blown} wall side(s) round it");
    }

    /// <summary>The first standing wall on the axis <paramref name="axis"/> out
    /// of <paramref name="own"/>, within <paramref name="cells"/>: the middle of
    /// its edge, halfway up the wall - what a gun laid on a hex past it is laid
    /// on.</summary>
    private (Vector2I Cell, Vector3 At)? WallOnAxis(Vector2I own, int axis, int cells)
    {
        if (_walls.Count == 0 || _field is null || axis < 0)
            return null;
        Vector2I here = own;
        for (int k = 0; k < cells; k++)
        {
            Vector2I next = HexField.Step(here, axis);
            if (_field.Walled(here, axis))
                foreach (WallProp prop in _walls)
                {
                    if ((prop.Cell != here || !_field.SideStands(here, axis))
                        && (prop.Cell != next || !_field.SideStands(next, HexField.Reverse(axis))))
                        continue;
                    if (prop.Stack is null)
                        continue;
                    Vector3 rim = 0.5f * (CellWorld(here) + CellWorld(next));
                    rim.Y = prop.Stack.Anchor.Y + 0.5f * prop.Pile().Top * WallRadius;
                    return (prop.Cell, rim);
                }
            here = next;
        }
        return null;
    }

    /// <summary>The walls the rounds in the air have got to (a destroyer's
    /// pass), struck on that frame.</summary>
    private void WallsAheadTick()
    {
        for (int i = _wallsAhead.Count - 1; i >= 0; i--)
        {
            (CelShell.Round round, float along, Action hit) = _wallsAhead[i];
            if (round.Flown < along && !round.Arrived)
                continue;
            _wallsAhead.RemoveAt(i);
            hit();
        }
    }

    /// <summary>A wall on every walled cell of the map, after the stage.</summary>
    private void BuildWalls(BoardMap map)
    {
        _wallMap = map;
        if (_noWalls || _field is null || _stage is null)
            return;
        BunkerProp.Sizes? bunker = _noBunker ? null : BunkerProp.ReadSizes();
        foreach (Vector2I cell in map.Walled())
        {
            (WallKit.Recipe recipe, int bearing) = map.MasonryAt(cell).Laying()
                ?? (new WallKit.Recipe { Sides = TankBench.RingSides }, HexField.EdgeHeadings[0]);
            (CaponKit.Recipe Recipe, int Bearing)? shelter = map.MasonryAt(cell)?.Sheltering();
            if (shelter is { } s)
            {
                bearing = s.Bearing;
                // The rules' box made the model's: the bomb lands on the roof
                // that is drawn and a round passes the slit that is drawn.
                if (bunker is { } size)
                {
                    s.Recipe.WallHigh = size.RoofUnder;
                    s.Recipe.RoofThick = size.Height - size.RoofUnder;
                    s.Recipe.SlitLow = size.SlitLow;
                    s.Recipe.SlitHigh = size.SlitHigh;
                }
            }
            var prop = new WallProp
            {
                Field = _field, Stage = _stage, Cell = cell,
                Recipe = recipe, Capon = shelter?.Recipe, Borrow = null,
                Channel = _walls.Count,
                // the model's look on the bricks, as on the tank and the trees
                Cel = !_pbr,
            };
            AddChild(prop);
            prop.Bearing = bearing;
            prop.Build();
            _walls.Add(prop);
            WallRestate(prop, laying: true);
            GD.Print($"tank3d: wall on {cell}, sides {Convert.ToString(_field.SidesAt(cell), 2).PadLeft(6, '0')}, "
                     + $"clear {prop.Clearance():F2} m inside");
            if (prop.Concrete && bunker is not null)
                StandBunker(prop);
        }
    }

    /// <summary>The hex bunker on a capon's cell, where its prop stands and
    /// turned as it is turned; the prop's own pieces put out of sight.</summary>
    private void StandBunker(WallProp prop)
    {
        if (prop.Stack is not { } stack)
            return;
        var bunker = new BunkerProp
        {
            Name = $"Bunker{prop.Cell.X}_{prop.Cell.Y}",
            Radius = WallRadius, Anchor = stack.Anchor, Lay = prop.Lay, Cell = prop.Cell, Pbr = _pbr,
        };
        AddChild(bunker);
        if (!bunker.Build())
        {
            GD.PrintErr($"tank3d: bunker on {prop.Cell}: {bunker.Problem}");
            bunker.QueueFree();
            return;
        }
        stack.Visible = false;
        _bunkers[prop.Cell] = bunker;
        GD.Print($"tank3d: bunker on {prop.Cell}, {bunker.Pieces} chunks, slit to {Mathf.RoundToInt(prop.Bearing)}");
    }

    /// <summary>The tanks standing in the capon on <paramref name="cell"/> -
    /// this scene's and the ram's target - as the bunker's falling chunks
    /// should meet them.</summary>
    private List<BunkerProp.Occupant> BunkerOccupants(Vector2I cell)
    {
        var inside = new List<BunkerProp.Occupant>();
        if (_field is null)
            return inside;
        // a wreck is in the way as much as a tank
        if (_field.FlatCellAt(Board(_rig.Position)) == cell)
            inside.Add(BunkerProp.Occupy(_model));
        if (_other is { } o && _field.FlatCellAt(Board(o.Rig.Position)) == cell)
            inside.Add(BunkerProp.Occupy(o.Model));
        return inside;
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
    /// breaking them, always when it is a capon's concrete.</summary>
    private bool WallStops(Vector2I here, int heading) =>
        _walls.Count > 0 && _field!.Blocked(here, heading)
        && (!WallSweeping || ConcreteEdge(here, heading));

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
        WallsAheadTick();
        foreach (BunkerProp bunker in _bunkers.Values)
            bunker.Tick(dt, _camera.GlobalBasis);
        _wallBurst?.Tick(dt, _camera.GlobalBasis);
        _concreteBurst?.Tick(dt, _camera.GlobalBasis);
        _caponBlast?.Tick(dt, _camera.GlobalBasis);
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
        bool sweeping = can && WallSweeping && _wallBoxed is not { Concrete: true };
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
                        if ((prop.Cell != chain[leg - 1] && prop.Cell != chain[leg]) || prop.Concrete)
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
                      && _field.InBounds(HexField.Step(here, heading)) && _field.Walled(here, heading)
                      && !ConcreteEdge(here, heading);
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
                if (prop.Concrete)
                    break;
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
        _wallsAhead.Clear();
        _wallBurst?.Reset();
        _concreteBurst?.Reset();
        _caponBlast?.Reset();
        _wallBoxed?.Dismount();
        _wallBoxed = null;
        foreach (WallProp prop in _walls)
        {
            prop.Build();
            WallRestate(prop, laying: true);
            // Build makes the stack again: out of sight again under the bunker
            if (_bunkers.TryGetValue(prop.Cell, out BunkerProp? bunker) && prop.Stack is { } stack)
            {
                stack.Visible = false;
                bunker.Reset();
            }
        }
    }
}
