using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// Which tank is which, and what a shot would do, before it is fired
/// (docs/hud3d-plan.md, stage 4).
///
/// <list type="bullet">
/// <item><b>The outline</b> - a rim of colour round the model, outside its
/// ink (<see cref="Toon.Halo"/>): wide in the own side's colour on the
/// selected tank, thin on a tank under the pointer - the own one unselected, a
/// standing one to take over, or the target in the hostile red. The ring on
/// the ground stays (<c>Tank3DBench.Select</c>); the violet band on the
/// order's cell gives way to the ghost, which now stands there for the whole
/// drive.</item>
/// <item><b>The line of fire</b> - with the pointer on the target: from the
/// muzzle along the axis to the hull, the mortar's as an arc; where the lane is
/// shut, grey and still, ended with a bar across it. Seen faint through a
/// wood's crowns (<see cref="BeadGhostShader"/>). A ricochet goes
/// on from the flank down the rules' axis to what stops it
/// (<c>Tank3DBench.Glance</c>), in the outcome's colour there.</item>
/// <item><b>The plate</b> - the edge of the target's hex the round comes in
/// through, in the outcome's colour, and a label over the target: лоб, борт,
/// корма or крыша, and пробьёт, рикошет or не пробьёт - off the same
/// <see cref="Gunnery"/> sums the round in flight asks
/// (<c>Tank3DBench.Shell</c>, <c>LevelOn</c>).</item>
/// <item><b>From where.</b> The shot is judged from where it will be fired:
/// the cell the drive ends on while driving, the fixed cell once one is fixed
/// - every class drives first. LT alone may shoot first (classes.md,
/// «Свободный порядок LT»): it is judged from where it stands, and the label
/// adds what the shot would be after the move.</item>
/// </list>
///
/// The right button follows the same reading: with a cell fixed it orders the
/// move and the shot on arrival - LT's fires now and keeps the cell fixed.
/// <c>--hover Q,R</c> on the target's cell holds the pointer on it for a
/// capture; <c>--no-ui</c> shows none of this.
/// </summary>
public sealed partial class Tank3DBench
{
    /// <summary>The rim's width past the ink, screen px: selected, under the
    /// pointer - twice what they were, the hover's too thin to read (the
    /// author's note, 2026-11-01).</summary>
    private const float HaloChosen = 4.5f, HaloHover = 3.0f;

    // red where it gets in: the target under fire, and not the green of the tanks
    private static readonly Color AimPierce = new(0.93f, 0.24f, 0.20f);
    private static readonly Color AimGlance = new(0.97f, 0.74f, 0.24f);
    private static readonly Color AimHeld = new(0.62f, 0.68f, 0.76f);
    private static readonly Color AimOff = new(0.66f, 0.66f, 0.63f);

    private readonly Dictionary<TankModel, Toon.Rim?> _halos = new();
    private bool _overOwn, _overTarget;
    private Parked? _overParked;

    private MeshInstance3D? _aimMesh;
    private Control? _aimInk;
    private Judged? _judged;
    private string _judgedNote = "";

    /// <summary>A shot judged before it is fired: from where, through which
    /// plate, to what end - or why not.</summary>
    private sealed record Judged(Vector2I From, Vector2I Cell, Vector3 Muzzle, Vector3 End, int Axis,
                                 string Face, Gunnery.Plate Plate, int Level, string Why, bool Lob,
                                 int Cells, Vector2I? Stop, Vector3 Tip)
    {
        public bool Fires => Why.Length == 0;
        public bool Pierces => Fires && Level > 0;
        public bool Glances => Fires && Level == 0 && Gunnery.Ricochets(Plate);

        /// <summary>Where the round goes on to past the target: off its flank
        /// (<see cref="GlanceLeg"/>) or, a destroyer's, through it
        /// (<see cref="PassLeg"/>).</summary>
        public Leg? Onward { get; init; }

        /// <summary>A destroyer's pass spent on a brick wall short of the
        /// target: where the line crosses it.</summary>
        public Vector3? Breach { get; init; }

        /// <summary>A destroyer's pass spent on a hull short of the target
        /// (<see cref="HoledLeg"/>): the hull, its plate, where the line
        /// crosses it.</summary>
        public Leg? Holed { get; init; }

        /// <summary>A standing tank shuts the lane: the line ends on its
        /// plate, drawn without the cross and the word.</summary>
        public bool Screened { get; init; }

        public Color Ink => !Fires ? AimOff : Pierces ? AimPierce : Glances ? AimGlance : AimHeld;

        public string Says
        {
            get
            {
                if (!Fires)
                    return Why;
                string what = Pierces ? $"пробьёт ({Level})" : Glances ? "рикошет" : "не пробьёт";
                if (Onward is { } leg)
                    what += leg.Through ? $" → насквозь: {leg.Says}" : $" → {leg.Says}";
                string through = Holed is { } h ? $"сквозь {h.Hull!.Tag} ({FaceWord(h.Face)}) → "
                    : Breach is null ? "" : "сквозь стену → ";
                return $"{through}{FaceWord(Face)} · {what}";
            }
        }
    }

    private void BuildAim(CanvasLayer layer)
    {
        if (_noUi)
            return;
        _aimMesh = new MeshInstance3D
        {
            Name = "AimPlate",
            Mesh = new ImmediateMesh(),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                VertexColorUseAsAlbedo = true,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                RenderPriority = Stage3D.RingOrder + 1,
            },
        };
        AddChild(_aimMesh);
        BuildBeads();
        BuildAxisLines();
        _aimInk = new Control { Name = "AimInk", MouseFilter = Control.MouseFilterEnum.Ignore };
        _aimInk.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _aimInk.Draw += DrawAim;
        layer.AddChild(_aimInk);
    }

    /// <summary>Every frame, after <see cref="CourseTick"/>: what is under the
    /// pointer, the outlines, the shot judged.</summary>
    private void AimTick()
    {
        if (_noUi)
            return;
        UnderTick();
        // the axis under the pointer with «Засада» on (Tank3DBench.Ambush)
        AmbushAimTick();
        ReachTick();
        HaloTick();
        _judged = null;
        _judgedNote = "";
        if (_aimMesh?.Mesh is ImmediateMesh mesh)
            mesh.ClearSurfaces();
        // the ram, when the pointer offers it, before the shot (Tank3DBench.RamPlan);
        // out of reach from here: a post of the move fixed for it (Tank3DBench.Post)
        bool ram = RamTick();
        if (!ram)
            PostTick();
        if (!ram && _rules && _chosen && _fate == Fate.Alive && _overTarget && _other is { } o && _field is not null && !RamRunning
            && !MatchShotSpent)
        {
            bool driving = Driving;
            Vector2I from = ShotFrom;
            // the ghost's gun on the target first: Judge takes the tip of its
            // barrel, and the ghost is stood afresh each frame turret ahead
            CasemateGhost(from, o);
            GhostTrack(from, o);
            _judged = Judge(from, o);
            _judgedNote = $"цель {o.Tag} {MapAt(_judged.Cell)}{(from != CellHere ? $" из {MapAt(from)}" : "")}: {_judged.Says}"
                          + (_postLock && !driving ? "; ПКМ — ехать и стрелять" : "")
                          + (_postNone ? "; ни с одной клетки хода" : "");
            if (_lockCell is Vector2I after && _profile.FreeOrder && !driving && !_postLock)
            {
                Judged then = Judge(after, o);
                _judgedNote += $"; после хода из {MapAt(after)}: {then.Says}";
            }
            // a knocked-out target goes up: whom it takes with it (Tank3DBench.BlastPlan)
            BlastPlanTick(_judged, o);
            if (_blastAt is not null)
                _judgedNote += $"; {_blastNote}";
            if (_aimMesh?.Mesh is ImmediateMesh plate)
            {
                AimGround(plate, _judged);
                BlastGround(plate);
            }
        }
        else
            BlastPlanTick(null, null);
        BeadsTick();
        // the ambushes' axes (Tank3DBench.Ambush)
        AxisLinesTick();
        _aimInk?.QueueRedraw();
    }

    private bool Driving => RouteMoving && !_routeRams && _route is { Count: > 0 };

    /// <summary>Where the shot is judged from: the drive's end while driving,
    /// the fixed cell once one is fixed (LT's only after the move), else
    /// where the tank stands.</summary>
    private Vector2I ShotFrom => Driving ? _route![^1]
        : _lockCell is Vector2I fixedAt && (!_profile.FreeOrder || _postLock) ? fixedAt : CellHere;

    /// <summary>Where the rims of the units in reach are judged from: as
    /// <see cref="ShotFrom"/>, but never the bench's own firing post
    /// (<c>Tank3DBench.Post</c>) - that one comes up for the target under the
    /// pointer, and with it the rims of the others went.</summary>
    private Vector2I ReachFrom => Driving ? _route![^1]
        : _lockCell is Vector2I fixedAt && !_profile.FreeOrder && !_postLock ? fixedAt : CellHere;

    // --- the outlines -------------------------------------------------------

    /// <summary>
    /// The units the selected tank may fire at from
    /// <see cref="ReachFrom"/> - the right button's own gate
    /// (<see cref="ShotWhy"/>, a gun's empty hex between, a wall on the axis):
    /// each enemy among them gets the thin hostile rim the pointer gives a
    /// target, from the moment the tank is chosen, so who can be hit reads
    /// before any hover (the author's note, 2026-10-29). The own ones are in
    /// it for the pointer: a unit under it is red when in reach, grey when
    /// not (<see cref="HaloTick"/>). Judged again only when what it depends
    /// on changes (<see cref="ReachStamp"/>): the gate allocates.
    /// </summary>
    private readonly HashSet<TankModel> _inReach = new();
    private int _reachStamp;

    private void ReachTick()
    {
        bool open = Match && _rules && _chosen && _fate == Fate.Alive && _field is not null && !RamRunning;
        int stamp = open ? ReachStamp() : 0;
        if (stamp == _reachStamp)
            return;
        _reachStamp = stamp;
        _inReach.Clear();
        if (!open)
            return;
        Vector2I from = ReachFrom;
        bool after = MayMoveFirst;
        for (int i = 0; i < _parked.Count; i++)
        {
            Parked p = _parked[i];
            if (p.Fate != Fate.Destroyed && (MayHit(from, p.Cell) || (after && MayHitAfter(p.Cell))))
                _inReach.Add(p.Model);
        }
        if (_other is { Unit: true } o && o.Fate != Fate.Destroyed
            && (MayHit(from, OtherCell(o)) || (after && MayHitAfter(OtherCell(o)))))
            _inReach.Add(o.Model);
    }

    /// <summary>The move is still to be given - nothing fixed by the player,
    /// no drive under way: a target is in reach from any cell of it too
    /// (fire after the move, <c>Tank3DBench.Post</c>; the author's note,
    /// 2026-10-29).</summary>
    private bool MayMoveFirst => CourseShown && _reach is not null && (_lockCell is null || _postLock);

    /// <summary>Whether a cell of the move (<see cref="_reach"/>) has a shot at
    /// <paramref name="target"/> - <see cref="FindPost"/>'s reading, the
    /// outcome aside: a casemate turns on to the axis out of the same
    /// steps, so a cell without them for that turn does not count.</summary>
    private bool MayHitAfter(Vector2I target)
    {
        Steps.Reach reach = _reach!;
        foreach ((Vector2I cell, int _) in reach.Cells)
        {
            if (cell == reach.Start.Cell || !MayHit(cell, target))
                continue;
            if (_model.Turreted)
                return true;
            (int axis, _) = AxisTo(cell, target);
            List<Steps.Route> ways = reach.RoutesTo(cell);
            for (int w = 0; w < ways.Count; w++)
                if (reach.TurnTo(ways[w], axis) is not null)
                    return true;
        }
        return false;
    }

    /// <summary>What <see cref="ReachTick"/>'s judging reads: the cell fired
    /// from, the move's cells, the turn and the shot spent, and where every
    /// unit stands. Never 0, which is «nothing in reach».</summary>
    private int ReachStamp()
    {
        int h = HashCode.Combine(ReachFrom, MayMoveFirst, _reachVersion, _mt.Turn, _side, _mt.Fired, _passIn is null, MatchOver);
        h = HashCode.Combine(h, _mt.Bonus);
        h = HashCode.Combine(h, _parked.Count);
        for (int i = 0; i < _parked.Count; i++)
            h = HashCode.Combine(h, _parked[i].Cell, _parked[i].Fate);
        if (_other is { } o)
            h = HashCode.Combine(h, OtherCell(o), o.Fate);
        return h == 0 ? 1 : h;
    }

    /// <summary>Whether a shot from <paramref name="from"/> at a unit on
    /// <paramref name="cell"/> would be fired: <see cref="Judge"/>'s refusals
    /// without the rest of its reading.</summary>
    private bool MayHit(Vector2I from, Vector2I cell)
    {
        if (ShotWhy(from, cell, out int axis, out int cells, out _).Length > 0)
            return false;
        if (_profile.Lobs)
            return true;
        return cells >= DirectNearest && WallOnAxis(from, axis, cells, passOne: WallPass(from, cell)) is null;
    }

    /// <summary>The own tank, the target or a standing one under the pointer -
    /// or under <c>--hover</c>.</summary>
    private void UnderTick()
    {
        _overOwn = _overTarget = false;
        _overParked = null;
        // on the roster's column the board under it is not pointed at (Tank3DBench.Roster)
        if (_rosterUnder && _hoverFixed is null)
            return;
        if (_hoverFixed is Vector2I held)
        {
            // an enemy standing there is the target now (Tank3DBench.Match)
            if (_parked.Find(p => p.Cell == held) is { } fixedOn && MatchAims(fixedOn))
            {
                _overTarget = true;
                return;
            }
            _overTarget = _other is { } t && OtherCell(t) == held;
            _overOwn = held == CellHere;
            _overParked = _parked.Find(p => p.Cell == held);
            return;
        }
        Vector2 mouse = GetViewport().GetMousePosition();
        if (ParkedAt(mouse) is { } standing)
        {
            if (MatchAims(standing))
                _overTarget = true;
            else
                _overParked = standing;
            return;
        }
        if (PickAt(mouse) is not var (point, onTank, onOwn))
            return;
        _overOwn = onOwn;
        _overTarget = onTank
            || (!onOwn && _other is { } o && _field is not null && _field.FlatCellAt(Board(point)) == OtherCell(o));
    }

    private readonly List<TankModel> _haloGone = new();

    private void HaloTick()
    {
        // into a list kept for it, not Where().ToList(): every frame (BOT-3)
        _haloGone.Clear();
        foreach (TankModel m in _halos.Keys)
            if (!IsInstanceValid(m))
                _haloGone.Add(m);
        foreach (TankModel gone in _haloGone)
            _halos.Remove(gone);
        bool alive = _fate == Fate.Alive;
        SetHalo(_model, !alive ? 0.0f : _chosen ? HaloChosen : _overOwn ? HaloHover : 0.0f, SelectionRing.Friendly);
        if (_other is { } o)
        {
            // under the pointer in a match: red when the gun reaches it, grey
            // when not (the author's note, 2026-10-29); off it, the rim of an
            // enemy in reach (ReachTick)
            bool reached = _inReach.Contains(o.Model);
            bool shown = _overTarget || (reached && o.Unit && o.Side != _side);
            SetHalo(o.Model, shown ? HaloHover : 0.0f,
                    !Match || reached ? SelectionRing.Hostile : AimOff);
        }
        // in a match, the other side's in its colour (Tank3DBench.Match); the
        // enemies in reach of the selected tank's gun at all times (ReachTick)
        foreach (Parked p in _parked)
        {
            bool enemy = Match && p.Side != _side;
            bool shown = ReferenceEquals(p, _overParked) || (enemy && _inReach.Contains(p.Model));
            SetHalo(p.Model, shown && p.Fate != Fate.Destroyed ? HaloHover : 0.0f,
                    enemy ? SelectionRing.Hostile : SelectionRing.Friendly);
        }
    }

    /// <summary>The rim on <paramref name="model"/>, hung on first need.</summary>
    private void SetHalo(TankModel model, float px, Color tint)
    {
        if (!_halos.TryGetValue(model, out Toon.Rim? halo))
        {
            if (px <= 0.0f)
                return;
            _halos[model] = halo = Toon.Halo(model);
        }
        if (halo is null)
            return;
        halo.Set(px, tint);
    }

    // --- the shot -------------------------------------------------------------

    /// <summary>A shot from <paramref name="from"/> at the target: the
    /// right button's gate (<see cref="ShotWhy"/>), a wall on the axis, then
    /// the plate the round meets and what it does there.</summary>
    private Judged Judge(Vector2I from, Other o)
    {
        Vector2I cell = OtherCell(o);
        string why = ShotWhy(from, cell, out int axis, out int cells, out Vector2I? stop);
        Vector3 muzzle = MuzzleAt(from);
        float tall = o.Model.Size.Y * o.Model.PixelsPerUnit;
        Vector3 end = o.Rig.Position + o.Up * (0.45f * tall);
        // A destroyer's pass (classes.md, «Тяжёлый снаряд TD»): a brick wall
        // first on the axis takes it - the round goes on with one less - and
        // then the target cannot; WallOnAxis(passOne) finds the wall after.
        int might = _profile.Might;
        Vector3? breach = null;
        // ...or a hull first on it (Holes): the round goes through it, holing
        // it, and on to the target with one less - the near hull's leg
        Leg? holed = why.Length == 0 && !_profile.Lobs && HullPass(from, cell) is { } hull
            ? HoledLeg(from, axis, hull) : null;
        if (holed is not null)
            might = Gunnery.PassedMight(_profile);
        bool wallPass = holed is null && ShootsThrough;
        if (why.Length == 0 && !_profile.Lobs && wallPass
            && WallOnAxis(from, axis, cells) is { Prop.Concrete: false } first)
        {
            breach = first.At;
            might = Gunnery.PassedMight(_profile);
        }
        if (why.Length == 0 && !_profile.Lobs && WallOnAxis(from, axis, cells, passOne: wallPass) is { } wall)
        {
            why = $"снаряд уйдёт в стену {MapAt(wall.Cell)}";
            end = wall.At;
            stop = wall.Cell;
        }
        else if (stop is Vector2I shut)
            end = ShutAt(from, shut, end);
        // A tank in the lane: the line runs on into its plate, as the round
        // would - no cross short of it, no word over the target; the order
        // is still refused, and the HUD says why (the author's note, 2026-10-29).
        Parked? screen = null;
        if (stop is Vector2I s && !_profile.Lobs)
            for (int i = 0; i < _parked.Count; i++)
                if (_parked[i].Cell == s && _parked[i].Fate != Fate.Destroyed)
                    screen = _parked[i];
        if (screen is not null)
        {
            Vector3 tip0 = TipAt(from);
            Vector3 body = screen.Rig.Position + Vector3.Up * (0.45f * screen.Model.Size.Y * screen.Model.PixelsPerUnit);
            end = HitsOf(screen).Cast(tip0, (body - tip0).Normalized()) is { } on ? on.At : body;
        }
        string face = "roof";
        Gunnery.Plate plate = Gunnery.Plate.Roof;
        int level;
        if (_profile.Lobs)
        {
            // the bomb comes down on the roof (classes.md, «HM — Навес»)
            end = o.Rig.Position + o.Up * tall;
            level = Gunnery.Penetration(_profile.Might, o.Profile, plate);
            // on the roof itself, where the bomb comes down (AimCell's cast)
            if (why.Length == 0 && o.Hits.Cast(o.Rig.Position + Vector3.Up * (2.0f * tall + HexWidth), Vector3.Down) is { } roof)
                end = roof.At;
        }
        else
        {
            level = LevelOn(o, end - muzzle, might, out face, out plate);
            // the plate itself, where the round from the muzzle meets it -
            // the middle of the one the rules name (PlateMid)
            Vector3 tip = TipAt(from);
            if (why.Length == 0)
                end = PlateMid(o, face, tip, end);
        }
        var judged = new Judged(from, cell, muzzle, end, axis, face, plate, level, why, _profile.Lobs, cells, stop, TipAt(from));
        judged = judged with { Breach = breach, Screened = screen is not null, Holed = why.Length == 0 ? holed : null };
        // where it goes on to past the target - the round's own leg (Strike):
        // held by a flank, or a destroyer's with its pass still in hand
        // along the rules' axis, not the line's last stretch: that runs to the
        // plate's middle (PlateMid), off the axis by as much as the hull is wide
        if (judged.Glances && !judged.Lob)
            judged = judged with { Onward = GlanceLeg(o, end, HexWay(from, axis), might) };
        else if (judged.Pierces && !judged.Lob && ShootsThrough && breach is null && holed is null)
            judged = judged with { Onward = PassLeg(o, end, HexWay(from, axis), Gunnery.PassedMight(_profile)) };
        return judged;
    }

    /// <summary>
    /// Where the line of fire meets the target: the middle of the plate the
    /// rules name (<paramref name="face"/>, <see cref="LevelOn"/>'s sector -
    /// front within 30 degrees, rear from 150, the sides between), on the
    /// surface, as the line from <paramref name="tip"/> to it meets the hull.
    /// The rules' axis through the hull's middle met a side plate from 60
    /// degrees off the rear behind its middle, by the rear corner, and in
    /// the board's foreshortening the line read as into the stern while the
    /// rules and the hex's edge said the side (the author's note,
    /// 2026-10-31). The outcome is the rules' either way; this is where the
    /// line says it is. <paramref name="centre"/> when nothing is met.
    /// </summary>
    private static Vector3 PlateMid(Other o, string face, Vector3 tip, Vector3 centre)
    {
        Vector3 ahead = o.Ahead, left = new(ahead.Z, 0.0f, -ahead.X);
        Vector3 n = face switch
        {
            "front" => ahead,
            "rear" => -ahead,
            "left" => left,
            "right" => -left,
            _ => Vector3.Zero,
        };
        Vector3 mid = centre;
        // the plate's middle: in along its normal from well outside the hull
        float reach = o.Model.Size.Length() * o.Model.PixelsPerUnit;
        if (n != Vector3.Zero && o.Hits.Cast(centre + n * reach, -n) is { } plate)
            mid = plate.At;
        // and where the line to it meets the hull first, so no bead lies in it
        return o.Hits.Cast(tip, (mid - tip).Normalized()) is { } hit ? hit.At : mid;
    }

    /// <summary>
    /// Where the line of a shot the lane shuts at <paramref name="shut"/>
    /// ends: on the barrel's line, as far out as the middle of that hex - in
    /// a wood, its middle under the crowns (the author's note, 2026-10-31).
    /// It ran to a point on the datum, a third of a hull up, and out of a
    /// wood into the next it left the gun at an angle no barrel had. The
    /// barrel the shot leaves from is the ghost's or the tank's, laid on the
    /// target (the line is drawn only then, <see cref="OnTarget"/>); else
    /// the level way to <paramref name="aim"/>.
    /// </summary>
    private Vector3 ShutAt(Vector2I from, Vector2I shut, Vector3 aim)
    {
        Vector3 tip = TipAt(from);
        TankModel? tube = GhostOn(from) ?? (from == CellHere ? _model : null);
        Vector3 way = tube is not null && !_profile.Lobs ? tube.Muzzle.GlobalBasis.Z : aim - tip;
        if (tube is null || _profile.Lobs)
            way.Y = 0.0f;
        if (way.LengthSquared() < 1e-6f)
            return CellMiddle(shut);
        way = way.Normalized();
        Vector3 mid = CellMiddle(shut);
        Vector2 flat = new Vector2(way.X, way.Z);
        float run = new Vector2(mid.X - tip.X, mid.Z - tip.Z).Dot(flat) / Mathf.Max(flat.LengthSquared(), 1e-6f);
        return tip + way * run;
    }

    /// <summary>Where the line of fire starts from <paramref name="from"/>: over
    /// the hull's middle at the muzzle's height - the tank's where it stands,
    /// the ghost's where the ghost stands, else a turret's height over the
    /// cell. Not the muzzle: a line grown out of the barrel read as part of
    /// the model, and a casemate's barrel points where the hull does.</summary>
    private Vector3 MuzzleAt(Vector2I from)
    {
        if (GhostOn(from) is { } ghost)
            return OverMiddle(ghost, _ghostRig!);
        if (from == CellHere)
            return OverMiddle(_model, _rig);
        return CellMiddle(from) + Vector3.Up * (0.7f * _model.Size.Y * _model.PixelsPerUnit);
    }

    /// <summary>The ghost, if it stands on <paramref name="cell"/>.</summary>
    /// <summary>The muzzle's tip where the shot is fired from: the beads on the
    /// ground start under it.</summary>
    private Vector3 TipAt(Vector2I from) =>
        // the ghost first: on the own cell it is a casemate turned on the target
        GhostOn(from) is { } ghost ? ghost.Muzzle.GlobalPosition
        : from == CellHere ? _model.Muzzle.GlobalPosition
        : CellMiddle(from);

    private TankModel? GhostOn(Vector2I cell) =>
        _ghost is not null && _ghostRig is { Visible: true } rig && _field!.FlatCellAt(Board(rig.Position)) == cell
            ? _ghost : null;

    private static Vector3 OverMiddle(TankModel model, Node3D rig) =>
        new(rig.GlobalPosition.X, model.Muzzle.GlobalPosition.Y, rig.GlobalPosition.Z);

    /// <summary>How far the bore is turned off <paramref name="target"/>, rad,
    /// about the turret (or the hull) - <c>Laying</c>'s reading, signed so
    /// that adding it to <see cref="TankModel.Yaw"/> lays the gun.</summary>
    private static float BoreOff(TankModel model, Node3D rig, Vector3 target)
    {
        Vector3 bore = model.Muzzle.GlobalBasis.Z;
        Vector3 pivot = model.Turreted && model.Turret is not null ? model.Turret.GlobalPosition : rig.GlobalPosition;
        Vector3 want = target - pivot, now = bore;
        want.Y = 0.0f;
        now.Y = 0.0f;
        return want.LengthSquared() > 1e-4f && now.LengthSquared() > 1e-6f
            ? Mathf.Atan2(now.Z * want.X - now.X * want.Z, now.X * want.X + now.Z * want.Z) : 0.0f;
    }

    /// <summary>
    /// The chosen tank's turret onto the unit under the pointer, the rules'
    /// way (at the class's traverse, <c>SlewDeg</c>): the target - the HUD's
    /// line of fire then leaves along the barrel - and, since the author's
    /// note of 2026-10-30, any other unit too, a standing one of either side,
    /// whether a shot would be had at it or not, or from where. Only standing,
    /// with no shot being laid - the right button's laying has the turret
    /// then. Off the units, the turret stays where it was looking. A
    /// casemate's hull does not turn for a look: that is a step.
    /// </summary>
    private float TrackIn(float dt)
    {
        if (!_chosen || _aim is not null || RouteMoving || !_model.Turreted || _fate != Fate.Alive || dt <= 0.0f)
            return 0.0f;
        // In placement nothing is aimed at: the turret back over the bow, and
        // not after the own tank under the pointer - a ghost turned
        // sideways stood so when let go (the author's note, 2026-11-01).
        if (Deploying)
            return Mathf.Clamp(-_model.Yaw / (Mathf.DegToRad(SlewDeg) * dt), -1.0f, 1.0f);
        Vector3 at;
        // «Засада» on: round to the axis being picked (Tank3DBench.Ambush)
        if (_ambushAxis is int axis)
            at = AxisPoint(axis);
        else if (_overTarget && _other is { } o)
            at = Centre(o);
        else if (_overParked is { } p)
            at = p.Rig.GlobalPosition + Vector3.Up * (0.45f * p.Model.Size.Y * p.Model.PixelsPerUnit);
        // «Атака» on (the author's note, 2026-11-01): after the pointer over
        // the board, wherever it is - not over the own hull, where the bearing
        // spins
        else if (AttackZoneOn && PickAt(GetViewport().GetMousePosition()) is (var point, false, false)
                 && Flat(point - _rig.GlobalPosition).Length() > 0.5f * HexWidth)
            at = point;
        else
            return 0.0f;
        float yaw = BoreOff(_model, _rig, at);
        return Mathf.Clamp(yaw / (Mathf.DegToRad(SlewDeg) * dt), -1.0f, 1.0f);
    }

    /// <summary>The ghost's turret straight onto the target when the shot is
    /// judged from the ghost's cell - a picture, so at once. Before the shot is
    /// judged: its line starts at the tip of this barrel.</summary>
    private void GhostTrack(Vector2I from, Other o)
    {
        if (GhostOn(from) is not { Turreted: true } ghost)
            return;
        ghost.Yaw += BoreOff(ghost, _ghostRig!, Centre(o));
        ghost.Apply();
    }

    /// <summary>How far off the target a casemate's hull may point before the
    /// ghost stands in for it, rad.</summary>
    private const float CasemateOff = 0.035f;

    /// <summary>
    /// A casemate's gun points where its hull does, and the hull is not turned
    /// for a look - that is a step - so a line of fire out of its muzzle left
    /// at the hull's angle. Instead the shot is shown from the hull as it will
    /// fire: the ghost, turned on the target (the right button's laying turns
    /// the hull the same way). On the cell the shot is judged from - the
    /// ghost already standing on the fixed cell or the drive's end, or, from
    /// where the tank stands, one over it while the hull points off
    /// the target by more than <see cref="CasemateOff"/>, as firm as a fixed
    /// cell's - inside the hull a faint one did not read. Runs before
    /// <see cref="Judge"/>, which takes the muzzle off the ghost.
    /// </summary>
    private void CasemateGhost(Vector2I from, Other o)
    {
        // and only for a shot the rules allow: turned for one that will not
        // be fired, the ghost said there would be one
        if (_model.Turreted || _field is null || ShotWhy(from, OtherCell(o), out _, out _, out _).Length > 0)
            return;
        Vector3 want = Centre(o) - CellMiddle(from);
        want.Y = 0.0f;
        if (want.LengthSquared() < 1.0f)
            return;
        if (GhostOn(from) is null)
        {
            if (from != CellHere || Mathf.Abs(BoreOff(_model, _rig, Centre(o))) < CasemateOff)
                return;
            GhostAt(from, HullSide(), true, overOwn: true);
            if (_ghostRig is null)
                return;
        }
        _ghostRig!.Rotation = new Vector3(0.0f, Mathf.Atan2(want.X, want.Z), 0.0f);
        // a mortar's tube at the angle it will be laid at - at once, a picture
        if (_profile.Lobs && _ghost is { } ghost)
        {
            ghost.Elevation = Mathf.Clamp(ghost.Elevation + LobOff(ghost, _ghostRig.GlobalPosition, Centre(o)),
                                          Mathf.DegToRad(ghost.ElevMin), Mathf.DegToRad(ghost.ElevMax));
            ghost.Apply();
        }
    }

    /// <summary>How far <paramref name="tube"/> is off the angle the right
    /// button lays a mortar at for <paramref name="at"/>, rad - the range's
    /// angle (<see cref="LobLay"/>), the one the bomb flies by.</summary>
    private float LobOff(TankModel tube, Vector3 rig, Vector3 at)
    {
        float pitch = Mathf.Asin(Mathf.Clamp(tube.Muzzle.GlobalBasis.Z.Normalized().Y, -1.0f, 1.0f));
        float cells = new Vector2(at.X - rig.X, at.Z - rig.Z).Length() / Mathf.Max(CellStep, 1.0f);
        return LobLay(cells, pitch, tube) - pitch;
    }

    /// <summary>The mortar's own tube on to the angle for the target under the
    /// pointer, at the elevation's rate, as <see cref="TrackIn"/> brings a
    /// turret round: standing, nothing being laid, judged from here and no
    /// ghost standing in for the hull.</summary>
    private float LobTrackIn(float dt)
    {
        if (!_profile.Lobs || _judged is not { } shot || _other is not { } o || _aim is not null || RouteMoving
            || _fate != Fate.Alive || dt <= 0.0f || shot.From != CellHere || GhostOn(CellHere) is not null)
            return 0.0f;
        return Mathf.Clamp(LobOff(_model, _rig.GlobalPosition, Centre(o)) / (Mathf.DegToRad(ElevDeg) * dt), -1.0f, 1.0f);
    }

    private static Vector3 Centre(Other o) =>
        o.Rig.Position + o.Up * (0.45f * o.Model.Size.Y * o.Model.PixelsPerUnit);

    // --- the target on the board --------------------------------------------

    /// <summary><c>--no-target</c>: no target unless asked for.</summary>
    private bool _targetDefault = true;

    /// <summary>The target the bench starts with (HUD stage 4): along the own
    /// hull three hexes out - one of the six axes, two empty hexes between -
    /// broadside on; else the next side round, or four or two out. Dry, on
    /// the board, off the standing tanks, the mines and the woods (a tank
    /// under the crowns is hard to see to aim at).</summary>
    private void StandTarget()
    {
        if (_field is null || _other is not null)
            return;
        Vector2I here = CellHere;
        var sides = new List<int>(HexField.EdgeHeadings);
        int hull = HullSide();
        sides.Sort((a, b) => Turn(hull, a).CompareTo(Turn(hull, b)));
        HashSet<Vector2I> taken = Barred();
        // a hex the gun reaches first: behind a rise it would greet the bench
        // with a refusal; any dry one after that
        foreach (bool clear in new[] { true, false })
        foreach (int cells in new[] { 3, 4, 2 })
            foreach (int side in sides)
            {
                Vector2I c = here;
                for (int k = 0; k < cells; k++)
                    c = HexField.Step(c, side);
                if (!_field.InBounds(c) || _field.IsWater(c) || taken.Contains(c)
                    || _field.CoverAt(c) is Cover.Minefield or Cover.Forest
                    || (clear && ShotWhy(here, c, out _, out _, out _).Length > 0))
                    continue;
                PlaceOther(c, HeadingFor(c, side) + 90.0f);
                Log.Print($"tank3d: target {_otherTag} stands on {MapAt(c)}, {cells} out along {side}");
                return;
            }
    }

    private static int Turn(int from, int to) => Mathf.Min(Mathf.PosMod(to - from, 360), Mathf.PosMod(from - to, 360));

    // --- the line of fire and the plate -------------------------------------

    /// <summary>
    /// The line of fire (HUD stage 4): beads in the air from the muzzle's tip
    /// to where the round meets the hull - cast on the target's plates - or
    /// to where the lane is shut; they run toward the target with a soft wave
    /// of light, in the outcome's colour: red where the round gets in, amber
    /// off a flank, steel where it holds, grey where it stops short. Tried
    /// before it and turned down: a solid arrow (read as part of the model),
    /// a dash over the board, beads on the ground along the axis (the shot
    /// did not read as a flight). Off with <c>--no-fire-axis</c> or the panel:
    /// then only the plate.
    ///
    /// <b>Only once the gun is on the target</b> (<see cref="OnTarget"/>): while
    /// the turret is still coming round the line would leave the muzzle at an
    /// angle and swing with it.
    /// </summary>
    private bool _fireAxis = true;

    /// <summary><c>--reticle</c> or the panel: the ring round the point the
    /// round meets, and a smaller one on a second target. Off by default - the
    /// line's colour and course say it (2026-10-20).</summary>
    private bool _reticle;

    /// <summary>
    /// Whether the gun the shot leaves from points at the target: the ghost's
    /// is turned at once (<see cref="GhostTrack"/>, <see cref="CasemateGhost"/>);
    /// the tank's own, judged from where it stands, once its turret - or a
    /// casemate's hull, turned by the right button's laying - is within
    /// <see cref="CasemateOff"/> of it.
    /// </summary>
    private bool OnTarget(Judged shot) =>
        shot.From != CellHere || GhostOn(shot.From) is not null || _other is not { } o
        || (Mathf.Abs(BoreOff(_model, _rig, Centre(o))) <= CasemateOff
            // and a mortar's tube at its angle for the range (LobTrackIn)
            && (!_profile.Lobs || Mathf.Abs(LobOff(_model, _rig.GlobalPosition, Centre(o))) <= CasemateOff));

    /// <summary>Beads a hex, how many hexes a second they run, how fast the
    /// light goes along them.</summary>
    private const float BeadsAHex = 6.0f, BeadRun = 0.55f, BeadWave = 5.0f;

    private void AimGround(ImmediateMesh mesh, Judged shot)
    {
        if (_tile is null || _field is null)
            return;
        bool shut = shot.Stop is not null;
        if (!shut && !shot.Fires)
            return;
        Vector2[] corner = Corners();
        mesh.SurfaceBegin(Mesh.PrimitiveType.Triangles);
        if (shut)
            ShutHex(mesh, shot, corner);
        if (shot.Fires)
        {
            Vector2I cell = shot.Cell;
            Vector2 mid = CellFlat(cell);
            if (shot.Lob)
            {
                var tint = new Color(shot.Ink, 0.35f);
                for (int i = 0; i < 6; i++)
                    Tri(mesh, tint, cell, mid, mid + corner[i] * 0.95f, mid + corner[(i + 1) % 6] * 0.95f);
            }
            else
            {
                PlateEdge(mesh, cell, HexField.Reverse(shot.Axis), shot.Ink, corner);
                // a destroyer's pass through a hull short of it: where it went in
                if (shot.Holed is { } holed)
                    PlateEdge(mesh, holed.Cell, HexField.Reverse(holed.Axis), holed.Ink, corner);
                // the ricochet's: the edge it leaves by, and the plate it meets
                if (shot.Onward is { } leg)
                {
                    PlateEdge(mesh, cell, leg.Axis, leg.Lead, corner);
                    if (leg.Ends == LegEnd.Tank)
                        PlateEdge(mesh, leg.Cell, HexField.Reverse(leg.Axis), leg.Ink, corner);
                }
            }
        }
        mesh.SurfaceEnd();
    }

    /// <summary>The edge of <paramref name="cell"/> toward
    /// <paramref name="side"/>, a band in <paramref name="tint"/> on a dark
    /// one: where a round comes in or leaves.</summary>
    private void PlateEdge(ImmediateMesh mesh, Vector2I cell, int side, Color tint, Vector2[] corner)
    {
        Vector2 mid = CellFlat(cell);
        int e = EdgeOf(cell, side, corner);
        Vector2 a = corner[e], b = corner[(e + 1) % 6];
        Quad(mesh, SelectionRing.Under, cell, mid + a * 1.03f, mid + b * 1.03f, mid + b * 0.76f, mid + a * 0.76f);
        Quad(mesh, tint, cell, mid + a * 0.99f, mid + b * 0.99f, mid + b * 0.80f, mid + a * 0.80f);
    }

    /// <summary>The hex that shuts the lane, tinted on the ground; the beads
    /// themselves are in the air (<see cref="BeadsTick"/>).</summary>
    private void ShutHex(ImmediateMesh mesh, Judged shot, Vector2[] corner)
    {
        if (shot.Stop is not Vector2I shut)
            return;
        Vector2 mid = CellFlat(shut);
        var tint = new Color(AimOff, 0.30f);
        for (int i = 0; i < 6; i++)
            Tri(mesh, tint, shut, mid, mid + corner[i] * 0.95f, mid + corner[(i + 1) % 6] * 0.95f);
    }

    /// <summary>
    /// The round's flight as beads in the air: from the muzzle's tip to where
    /// it meets the hull (<see cref="Judged.End"/>, cast on the target's
    /// plates) - the mortar's along its arc - or to where the lane is shut.
    /// They run toward the target with a soft wave of light, coming in at the
    /// muzzle and going at the hull, in the outcome's colour. Spaced in the
    /// world, sized on the screen, so a long shot has more beads and not
    /// bigger ones.
    ///
    /// <b>In the scene, not over it</b> (<see cref="BeadShader"/>): each bead a
    /// disc facing the camera, tested against the depth, so a round that goes
    /// through a hull or a wall goes into it and comes out the far side - the
    /// part inside and behind it hidden by the model itself. Drawn on the
    /// screen's canvas, as first, it lay over the hull it went through. The
    /// crowns hide it as well: the line goes through the wood.
    /// </summary>
    private void BeadsTick()
    {
        if (_beads?.Multimesh is not { } mm)
            return;
        mm.VisibleInstanceCount = 0;
        // «Засада» on: the axis being picked instead (Tank3DBench.Ambush, AxisLinesTick)
        if (_ambushMode)
            return;
        if (_judged is not { } shot || !_fireAxis || shot.Axis < 0 || !OnTarget(shot))
            return;
        var path = shot.Lob && LobPath(shot) is { } arc ? arc : new List<Vector3> { shot.Tip, shot.End };
        float first = 0.0f;
        for (int k = 0; k + 1 < path.Count; k++)
            first += path[k].DistanceTo(path[k + 1]);
        // a ricochet: on from the plate down its axis, in its own colour
        // (Tank3DBench.Glance)
        Leg? leg = _fireAxis && shot.Fires ? shot.Onward : null;
        if (leg is not null)
            path.Add(leg.End);
        float total = first + (leg is null ? 0.0f : leg.From.DistanceTo(leg.End));
        float gap = CellStep / BeadsAHex;
        if (total <= gap * 0.5f)
            return;
        float t = (float)(Time.GetTicksMsec() / 1000.0);
        // a shot that will not be fired stands still: grey, no light running
        // along it, to where it stops (the author's note, 2026-10-31)
        float run = shot.Fires ? Mathf.PosMod(t * BeadRun * CellStep, gap) : 0.0f;
        float r = Mathf.Clamp(HexScreen() * 0.045f, 2.5f, 6.0f);
        // screen px a world unit: the camera is orthographic, one figure
        Vector3 basisX = _camera.GlobalBasis.X.Normalized();
        float px = (_camera.UnprojectPosition(shot.End + basisX) - _camera.UnprojectPosition(shot.End)).Length();
        if (px < 1e-4f)
            return;
        Basis facing = _camera.GlobalBasis.Orthonormalized();
        Color c = shot.Fires ? shot.Ink : AimOff;
        // the first bead a little out of the bore, the last on the plate - or
        // a leg off the board dying away over its last two hexes
        float start = gap * 0.3f;
        float tail = leg is { Fades: true } ? CellStep * 2.0f : gap;
        int n = 0;
        for (float s = start + run; s <= total && n < BeadsMost; s += gap)
        {
            float wave = shot.Fires ? 0.5f + 0.5f * Mathf.Sin(t * BeadWave - s / gap * 0.9f) : 0.4f;
            // the still line does not fade at its ends: shut a hex out of a
            // wood, it is little more than its ends
            float edge = !shot.Fires ? 1.0f
                : Mathf.Clamp((s - start) / gap, 0.0f, 1.0f) * Mathf.Clamp((total - s) / tail + 0.3f, 0.0f, 1.0f);
            Color bead = leg is not null && s > first ? leg.Ink : c;
            float size = r * (0.8f + 0.35f * wave) * (0.4f + 0.6f * edge);
            // the disc with its dark ring, 1.6 px past the colour
            float outer = (size + 1.6f) / px;
            mm.SetInstanceTransform(n, new Transform3D(facing.Scaled(Vector3.One * outer), Along3(path, s)));
            mm.SetInstanceColor(n, new Color(bead.Lerp(Colors.White, 0.45f * wave), (0.55f + 0.45f * wave) * edge));
            mm.SetInstanceCustomData(n, new Color(size / (size + 1.6f), 0.5f * edge, 0.0f, 0.0f));
            n++;
        }
        // every line ends in a bead twice the size, where the round stops -
        // on the plate it meets, at the end of a ricochet's or a pass's leg,
        // where the lane is shut (the author's notes, 2026-10-31): a bar
        // across the line drawn on the screen was lost on a wood's crowns. A
        // leg off the board dies away instead. Pulled toward the eye by its
        // radius, so a hull it lies on does not cut it in half.
        if (leg is not { Fades: true } && n < BeadsMost)
        {
            // a shot that will be fired flashes as the wave of light along it
            // gets to the end (EndPulse): the round arriving; the still one
            // does not (the author's note, 2026-10-31)
            float hit = shot.Fires ? Mathf.Pow(0.5f + 0.5f * Mathf.Sin(t * BeadWave - total / gap * 0.9f), 3.0f) : 0.0f;
            float size = r * EndBead * (1.0f + EndPulse * hit);
            float outer = (size + 2.0f) / px;
            // on a hull, pulled past it: the plate the rules name may face
            // away from the eye (PlateMid), and the hull hid the bead on it;
            // the camera is orthographic, so on the screen it stays on the plate
            TankModel? on = leg is not null ? leg.Hull?.Model : shot.Fires ? _other?.Model : null;
            float pull = outer + (on is not null ? on.Size.Length() * on.PixelsPerUnit : 0.0f);
            Vector3 at = (leg is not null ? leg.End : shot.End) + _camera.GlobalBasis.Z.Normalized() * pull;
            Color ink = leg is not null ? leg.Ink : c;
            mm.SetInstanceTransform(n, new Transform3D(facing.Scaled(Vector3.One * outer), at));
            mm.SetInstanceColor(n, new Color(ink.Lerp(Colors.White, 0.3f + 0.4f * hit), 1.0f));
            mm.SetInstanceCustomData(n, new Color(size / (size + 2.0f), 0.7f, 0.0f, 0.0f));
            n++;
        }
        // a line that goes on past the target - off a flank or through it -
        // marks where it meets it too, smaller, pulled past the hull as the
        // end bead is: the plate there may face away from the eye
        if (leg is not null && shot.Fires && _other is { } target && n < BeadsMost)
        {
            float size = r * HitBead;
            float outer = (size + 2.0f) / px;
            float pull = outer + target.Model.Size.Length() * target.Model.PixelsPerUnit;
            mm.SetInstanceTransform(n, new Transform3D(facing.Scaled(Vector3.One * outer),
                                                       shot.End + _camera.GlobalBasis.Z.Normalized() * pull));
            mm.SetInstanceColor(n, new Color(c.Lerp(Colors.White, 0.3f), 1.0f));
            mm.SetInstanceCustomData(n, new Color(size / (size + 2.0f), 0.7f, 0.0f, 0.0f));
            n++;
        }
        mm.VisibleInstanceCount = n;
    }

    /// <summary>Where a line that goes on past the target meets it, beads' radii.</summary>
    private const float HitBead = 1.6f;

    /// <summary>The bead every line of fire ends in, beads' radii; the
    /// ambush's axes end in one as many dash widths (<c>AxisLine</c>).</summary>
    private const float EndBead = 2.2f;

    /// <summary>How much the end bead grows at the top of its flash.</summary>
    private const float EndPulse = 0.2f;

    /// <summary>The most beads drawn at once.</summary>
    private const int BeadsMost = 600;

    private MultiMeshInstance3D? _beads;

    /// <summary>
    /// A bead: a camera-facing disc in the instance's colour, ringed in dark -
    /// <c>INSTANCE_CUSTOM.x</c> is where the colour ends across the radius,
    /// <c>.y</c> the ring's alpha. Unshaded, blended, tested against the depth
    /// and not writing it: the hull in front hides it, it hides nothing.
    /// </summary>
    private static readonly Shader BeadShader = new()
    {
        Code = @"
shader_type spatial;
render_mode unshaded, blend_mix, depth_draw_never, cull_disabled, shadows_disabled, fog_disabled;
varying vec2 ring;
void vertex() {
    ring = INSTANCE_CUSTOM.xy;
}
void fragment() {
    float d = length(UV - vec2(0.5)) * 2.0;
    float w = fwidth(d);
    float disc = 1.0 - smoothstep(1.0 - w, 1.0, d);
    float core = 1.0 - smoothstep(ring.x - w, ring.x, d);
    ALBEDO = mix(vec3(0.0), COLOR.rgb, core);
    ALPHA = disc * mix(ring.y, COLOR.a, core);
}
",
    };

    /// <summary>How much of a bead shows through the crowns
    /// (<see cref="BeadGhostShader"/>).</summary>
    private const float BeadGhost = 0.6f;

    /// <summary>
    /// The same beads once more, as a ghost: with no depth test, only on the
    /// pixels the trees' own surfaces mark (<see cref="TreeModel.Stencil"/>) -
    /// faint, through the crowns, so a shot from under them or into them
    /// reads from the muzzle to where it stops (the author's note,
    /// 2026-10-31). A hull still hides the beads: it leaves no such mark.
    /// Drawn before the beads themselves, so where a bead is seen it lies
    /// over its ghost.
    /// </summary>
    private static readonly Shader BeadGhostShader = new()
    {
        Code = @"
shader_type spatial;
render_mode unshaded, blend_mix, depth_test_disabled, depth_draw_never, cull_disabled, shadows_disabled, fog_disabled;
stencil_mode read, compare_equal, " + TreeModel.Stencil + @";
varying vec2 ring;
void vertex() {
    ring = INSTANCE_CUSTOM.xy;
}
void fragment() {
    float d = length(UV - vec2(0.5)) * 2.0;
    float w = fwidth(d);
    float disc = 1.0 - smoothstep(1.0 - w, 1.0, d);
    float core = 1.0 - smoothstep(ring.x - w, ring.x, d);
    ALBEDO = mix(vec3(0.0), mix(COLOR.rgb, vec3(1.0), 0.35), core);
    ALPHA = disc * mix(ring.y, COLOR.a, core) * " + BeadGhost.ToString(System.Globalization.CultureInfo.InvariantCulture) + @";
}
",
    };

    private void BuildBeads()
    {
        var mm = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true,
            UseCustomData = true,
            Mesh = new QuadMesh { Size = new Vector2(2.0f, 2.0f) },
        };
        mm.InstanceCount = BeadsMost;
        mm.VisibleInstanceCount = 0;
        _beads = new MultiMeshInstance3D
        {
            Name = "AimBeads",
            Multimesh = mm,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            MaterialOverride = new ShaderMaterial { Shader = BeadShader, RenderPriority = 1 },
            // the beads move every frame; nothing of them is culled
            CustomAabb = new Aabb(new Vector3(-1e5f, -1e5f, -1e5f), new Vector3(2e5f, 2e5f, 2e5f)),
        };
        AddChild(_beads);
        // their ghost through the crowns, off the same instances
        AddChild(new MultiMeshInstance3D
        {
            Name = "AimBeadsGhost",
            Multimesh = mm,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            MaterialOverride = new ShaderMaterial { Shader = BeadGhostShader, RenderPriority = 0 },
            CustomAabb = _beads.CustomAabb,
        });
    }

    /// <summary>
    /// The bomb's own flight: out of the tube along its bore - the tube the
    /// shot leaves from, laid for the range (<see cref="LobTrackIn"/>, the
    /// ghost's at once) - at the charge that brings it down on the roof
    /// (<see cref="Charge"/>), under <see cref="LobFall"/>: the curve
    /// <see cref="Launch"/> throws. Before this it was a parabola drawn over
    /// the chord, a third of it high, that left the muzzle at no angle the
    /// tube had. None when no tube stands there or it cannot carry the bomb.
    /// </summary>
    private List<Vector3>? LobPath(Judged shot)
    {
        TankModel? tube = GhostOn(shot.From) ?? (shot.From == CellHere ? _model : null);
        if (tube is null)
            return null;
        Vector3 bore = tube.Muzzle.GlobalBasis.Z.Normalized();
        if (Charge(shot.Tip, bore, shot.End, LobFall) is not float v)
            return null;
        float flat = new Vector2(bore.X, bore.Z).Length();
        float run = new Vector2(shot.End.X - shot.Tip.X, shot.End.Z - shot.Tip.Z).Length();
        if (flat < 1e-4f || v < 1e-3f)
            return null;
        float time = run / (v * flat);
        const int steps = 32;
        var path = new List<Vector3>(steps + 1);
        Vector3 At(float t) => shot.Tip + bore * (v * t) + Vector3.Down * (0.5f * LobFall * t * t);
        // the bore within a degree or two of the target: the miss spread along
        // the curve, so it lands on the roof
        Vector3 miss = shot.End - At(time);
        for (int k = 0; k <= steps; k++)
            path.Add(At(time * k / steps) + miss * (k / (float)steps));
        return path;
    }

    private static Vector3 Along3(List<Vector3> path, float s)
    {
        for (int k = 0; k + 1 < path.Count; k++)
        {
            float len = path[k].DistanceTo(path[k + 1]);
            if (s <= len && len > 0.0f)
                return path[k].Lerp(path[k + 1], s / len);
            s -= len;
        }
        return path[^1];
    }

    private void Disc(ImmediateMesh mesh, Vector2I cell, Vector2 at, float r, Color ink)
    {
        const int sides = 10;
        for (int i = 0; i < sides; i++)
        {
            float u = Mathf.Tau * i / sides, v = Mathf.Tau * (i + 1) / sides;
            Tri(mesh, ink, cell, at, at + new Vector2(Mathf.Cos(u), Mathf.Sin(u)) * r,
                at + new Vector2(Mathf.Cos(v), Mathf.Sin(v)) * r);
        }
    }

    // --- the screen: the reticle and the label ----------------------

    /// <summary>Over the board: a reticle round the target in the outcome's
    /// colour, and LT's label after the move.</summary>
    private void DrawAim()
    {
        // the target can go between the judging and the drawing: a match's
        // turn hands it back to standing in that frame (Tank3DBench.Match)
        if (_aimInk is { } ramInk)
        {
            DrawBadges(ramInk);
            DrawShotSign(ramInk);
            DrawRamSign(ramInk);
            DrawGhostSign(ramInk);
            DrawRam(ramInk);
            DrawBlast(ramInk);
        }
        if (_aimInk is not { } ink || _judged is not { } shot || _other is null)
            return;
        Font font = ThemeDB.FallbackFont;
        float hex = HexScreen();
        int size = Mathf.RoundToInt(Mathf.Clamp(hex * 0.16f, 9.0f, 16.0f) * 1.15f);
        Vector2 end = _camera.UnprojectPosition(shot.End);
        float r = size * 0.55f;
        if (shot.Fires)
        {
            if (_reticle)
            {
                Reticle(ink, end, Mathf.Max(size * 1.2f, hex * 0.32f), shot.Ink);
                // the second target past this one, smaller: a ricochet's or a pass's
                if (shot.Onward is { Ends: LegEnd.Tank } leg)
                    Reticle(ink, _camera.UnprojectPosition(leg.End), Mathf.Max(size * 0.9f, hex * 0.22f), leg.Ink);
            }
            // a destroyer's pass spent on a wall short of the target - part of
            // the line, shown with it
            if (shot.Breach is Vector3 wall && _fireAxis && OnTarget(shot))
                Burst(ink, _camera.UnprojectPosition(wall), Mathf.Max(size * 0.8f, hex * 0.18f), AimPierce);
            // ...or on a hull short of it, holed on the way
            if (shot.Holed is { } holed && _fireAxis && OnTarget(shot))
                Burst(ink, _camera.UnprojectPosition(holed.End), Mathf.Max(size * 0.8f, hex * 0.18f), AimPierce);
        }
        Vector2 over = _camera.UnprojectPosition(_other!.Rig.Position + _other.Up * (_other.Model.Size.Y * _other.Model.PixelsPerUnit * 1.2f));
        // kept under the HUD's three lines
        over.Y = Mathf.Max(over.Y, 100.0f + size * 2.0f);
        // The outcome is the line's colour and its course - no words over the
        // target for it, and none for why there is no shot either: the still
        // grey line into what shuts it says that, and the rims of the units
        // in reach (ReachTick) say who can be hit; the words are on the
        // status line (the author's notes, 2026-10-29 and 2026-10-31).
        int semi = _judgedNote.IndexOf("; после хода", System.StringComparison.Ordinal);
        if (semi >= 0)
            Pill(ink, font, over + new Vector2(0, size * 0.8f), _judgedNote[(semi + 2)..], new Color(0.92f, 0.95f, 0.98f),
                 new Color(0.05f, 0.27f, 0.49f), size);
    }

    /// <summary>A wall broken through: eight rays out of a small ring, in
    /// <paramref name="c"/> on a dark one.</summary>
    private static void Burst(Control ink, Vector2 at, float r, Color c, float a = 1.0f)
    {
        var dark = new Color(0.0f, 0.0f, 0.0f, 0.5f * a);
        foreach ((Color col, float w) in new[] { (dark, 5.0f), (new Color(c, c.A * a), 2.5f) })
        {
            ink.DrawArc(at, r * 0.35f, 0.0f, Mathf.Tau, 20, col, w, true);
            for (int i = 0; i < 8; i++)
            {
                Vector2 d = Vector2.Right.Rotated(Mathf.Tau * i / 8.0f);
                ink.DrawLine(at + d * (r * 0.55f), at + d * (i % 2 == 0 ? r : r * 0.8f), col, w, true);
            }
        }
    }

    /// <summary>A ring with four ticks pointing in, in the outcome's colour on
    /// a dark one: where the round goes in.</summary>
    private static void Reticle(Control ink, Vector2 at, float r, Color c)
    {
        var dark = new Color(0.0f, 0.0f, 0.0f, 0.5f);
        foreach ((Color col, float w) in new[] { (dark, 5.0f), (c, 2.5f) })
        {
            ink.DrawArc(at, r, 0.0f, Mathf.Tau, 40, col, w, true);
            for (int i = 0; i < 4; i++)
            {
                Vector2 d = Vector2.Right.Rotated(Mathf.Pi * 0.5f * i + Mathf.Pi * 0.25f);
                ink.DrawLine(at + d * (r - r * 0.35f), at + d * (r + r * 0.25f), col, w, true);
            }
        }
    }
}
