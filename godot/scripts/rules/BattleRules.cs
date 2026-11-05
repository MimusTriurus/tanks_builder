using System;
using System.Collections.Generic;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The match's rules applied to <see cref="BattleState"/> (docs/battle-core-plan.md,
/// Д3), by the GDD (docs/gdd: match.md, field.md, units.md, classes.md,
/// states.md). Each action checks itself and answers why not, or null, and
/// writes what happened into <see cref="BattleState.Events"/>. Deterministic:
/// one state and one action, one outcome - the property the AI searches by.
///
/// <b>What the bench has and this has too</b>: the move by steps with what the
/// path crosses (mine, deep water, a wood alight), the turn on the spot, the
/// shot (the plate by the edge it comes in through, the roof from above, the
/// ricochet off a side, the destroyer's pass, the mortar's lob, walls and woods
/// on the axis), the ram with what the pushed lands on, the wall rammed and the
/// heavy's bulldozing, fire on a unit, putting it out, burning out at the end
/// of its side's turn, the blast of a destroyed unit and its chain, the wood's
/// fire by rounds, the win by destruction, the stalemate (Д6), the ambush and
/// the medium's sector (Д7), the smoke grenade and its smoke, the incendiary
/// round, the drive's upgrades (in <see cref="Steps"/>), the mine guard, the
/// spall liner, the auto-extinguisher and its price, the thermal imager, the
/// mine layer, the repair kit, the winch, the double turn, the ambush
/// stabilizer (Д8; upgrades.md as revised 2026-11-02), the control point - holding, points on
/// the field's tick, the win on points (Д9).
///
/// <b>Not yet</b>: an ambush that holds an incendiary round back - it fires a
/// plain one; placement, which is before the first turn - the bench's and
/// <see cref="BattleRun"/>'s. Orders and the turn going over by itself: BattleOrder.cs (Д10).
/// </summary>
public static partial class Battle
{
    private const int FireRounds = 2;

    // --- the turn ------------------------------------------------------------------

    /// <summary>Unit <paramref name="id"/> takes the turn (match.md п. 1).</summary>
    public static string? Take(BattleState s, int id)
    {
        if (s.Over)
            return "the match is over";
        if (s.Unit(id) is not { } u)
            return $"no unit #{id}";
        if (u.Side != s.Turn)
            return $"#{id} is {u.Side}, {s.Turn} to move";
        if (u.Fate != UnitFate.Alive)
            return $"#{id} is {u.Fate}";
        if (s.Active is int a && a != id && (s.Drove || s.Fired || s.Bonus))
            return $"#{a} already acts this turn";
        s.Active = id;
        return null;
    }

    private static BattleUnit? Acting(BattleState s, out string? why)
    {
        why = s.Over ? "the match is over"
            : s.Active is not int id || s.Unit(id) is not { } u ? "no unit taken"
            : u.Fate != UnitFate.Alive ? $"#{id} is {u.Fate}"
            : null;
        return why is null ? s.Unit(s.Active!.Value) : null;
    }

    /// <summary>
    /// The move (п. 2-4): along <paramref name="path"/>, cell after cell - the
    /// start may lead it or not - then turned to <paramref name="face"/>. What
    /// each cell does is done as it is entered (п. 4): a mine knocks out - or,
    /// with the mine guard, sets alight - and the path ends there, deep water drowns without the gear, a wood alight sets
    /// the hull alight, water puts a fire out, a burning hull sets a wood alight,
    /// the heavy knocks down a wall it crosses. The cost is the route planner's
    /// (<see cref="Steps"/>) and not checked again here. <paramref name="back"/>:
    /// the move is astern (<see cref="Steps.Route.Back"/>), each cell entered
    /// tail first - the reverse gear's bend between two of them a turn; null
    /// reads each step against the hull, the one cell back as before.
    /// The enemy's ambushes fire each at its own moment of the move
    /// (<see cref="AmbushPicks"/>), not as each hex is entered.
    /// </summary>
    public static string? Move(BattleState s, IReadOnlyList<Vector2I> path, int face, bool? back = null)
    {
        if (Acting(s, out string? why) is not { } u)
            return why;
        if (s.Drove)
            return "already moved";
        if (u.Pinned)
            return "pinned by a ram blade";
        // the forced engine is a bonus act said before the move, not after it
        if ((s.Fired || (s.Bonus && !s.Overdrive)) && !u.Class.FreeOrder)
            return "only LT moves after acting";
        if (s.OnWay is { } way && OnWayWhy(s, u, path, face, back, way) is { } no)
            return no;
        s.Drove = true;
        Lift(s, u, "moved");
        Dictionary<int, Queue<int>>? picks = AmbushPicks(s, u, path, face, back, stopOnly: s.Overdrive);
        bool onWayShot = false;
        string? said = Walk(s, u, path, face, back,
                            pose => (picks is null || AmbushesAt(s, u, pose, picks)) && FireOnWay(s, u, ref onWayShot));
        // knocked out or stopped short of its hex, it does not fire (upgrades.md)
        if (s.OnWay is not null && !onWayShot)
            s.Fired = true;
        s.OnWay = null;
        return said;
    }

    /// <summary>
    /// The shot on the move said before it (<see cref="OnTheWay"/>): fired once,
    /// at the first pose the hull is on its hex - after what the hex did to it
    /// and after the ambushes whose moment that pose is. False once the match
    /// is over. <paramref name="shot"/>: whether it is fired yet.
    /// </summary>
    private static bool FireOnWay(BattleState s, BattleUnit u, ref bool shot)
    {
        if (shot || s.OnWay is not { } way || u.Cell != way.At || u.Fate != UnitFate.Alive)
            return true;
        shot = true;
        Fire(s, way.Aim);
        Over(s);
        return !s.Over;
    }

    /// <summary>Why the shot said for the move may not be had on
    /// <paramref name="path"/>: its hex not on the way, or the round refused
    /// from it - tried on a copy, walked there with nothing firing at it. A way
    /// that stops short of the hex on its own (a mine, the deep) is let go: it
    /// just does not fire.</summary>
    private static string? OnWayWhy(BattleState s, BattleUnit u, IReadOnlyList<Vector2I> path, int face, bool? back,
                                    (Vector2I At, Vector2I Aim) way)
    {
        bool on = u.Cell == way.At;
        foreach (Vector2I c in path)
            on |= c == way.At;
        if (!on)
            return $"({way.At.X - s.Shift.X},{way.At.Y - s.Shift.Y}) is not on the way";
        BattleState t = s.Clone();
        t.OnWay = null;
        BattleUnit tu = t.Unit(u.Id)!;
        string? why = null;
        Walk(t, tu, path, face, back, pose =>
        {
            if (tu.Cell != way.At)
                return true;
            why = Fire(t, way.Aim);
            return false;
        });
        return why is null ? null : $"on the way: {why}";
    }

    /// <summary>
    /// The shot on the move (upgrades.md, «Выстрел на ходу»; LT, «Рейд» 3):
    /// the turn's attack fired at <paramref name="aim"/> as the hull comes on
    /// to <paramref name="at"/>, a hex of the move that follows - said before
    /// it, as the forced engine is. What the hexes before it do comes first; a
    /// hull knocked out or stopped short of it does not fire, and the move
    /// goes on past it. Not an ambush: that stays the last thing.
    /// </summary>
    public static string? OnTheWay(BattleState s, Vector2I at, Vector2I aim)
    {
        if (Acting(s, out string? why) is not { } u)
            return why;
        if ((u.Kit & Kit.FireOnMove) == 0)
            return "no fire on the move";
        if (Held(s))
            return "the first turn holds fire";
        if (s.Fired)
            return "already fired";
        if (s.Drove)
            return "said before the move";
        if (u.Pinned)
            return "pinned by a ram blade";
        s.OnWay = (at, aim);
        s.Events.Add(new BattleEvent(BattleEventKind.OnWay, u.Id, at, 0, $"at ({aim.X - s.Shift.X},{aim.Y - s.Shift.Y})"));
        return null;
    }

    /// <summary>
    /// The move itself, pose by pose: <paramref name="at"/> is told each pose
    /// of the hull while it goes, numbered from 0 - the one it stands in as it
    /// sets out, each 60 degrees of a turn on a hex (the reverse gear's bend,
    /// the turn to <paramref name="face"/> at the end), each hex as it is
    /// entered - and a false from it stops the move in that pose. A move that
    /// neither leaves the hex nor turns has no poses.
    /// </summary>
    private static string? Walk(BattleState s, BattleUnit u, IReadOnlyList<Vector2I> path, int face, bool? back,
                                Func<int, bool> at)
    {
        bool goes = face >= 0 && face != u.Heading;
        foreach (Vector2I next in path)
            goes |= next != u.Cell;
        if (!goes)
            return null;
        int pose = 0;
        if (!at(pose++))
            return null;
        foreach (Vector2I next in path)
        {
            if (next == u.Cell)
                continue;
            int way = HexField.HeadingTo(u.Cell, next);
            if (way < 0)
                return $"({next.X},{next.Y}) is not beside ({u.Cell.X},{u.Cell.Y})";
            bool astern = back ?? way == HexField.Reverse(u.Heading);
            if (s.Board.Walled(u.Cell, way))
            {
                if (!u.Class.Bulldozes)
                    return $"a wall across ({u.Cell.X},{u.Cell.Y})>{way}";
                WallDown(s, u.Cell, way);
            }
            // turned on the hex to set off, sixty degrees at a time
            int want = astern ? HexField.Reverse(way) : way;
            while (u.Heading != want)
            {
                u.Heading = TurnStep(u.Heading, want);
                if (!at(pose++))
                    return Stopped(s, u);
            }
            u.Cell = next;
            s.Events.Add(new BattleEvent(BattleEventKind.Moved, u.Id, next, way));
            // what the hex does first: a mine or the deep stops it there
            bool stops = Entered(s, u, way, forward: !astern, ambushes: false);
            if (u.Fate != UnitFate.Alive)
                return null;
            if (!at(pose++))
                return null;
            // the mine guard's mine: alight, and the way ends on its hex
            if (stops)
                return null;
        }
        if (face >= 0 && face != u.Heading)
        {
            while (u.Heading != face)
            {
                u.Heading = TurnStep(u.Heading, face);
                if (u.Heading != face && !at(pose++))
                    return Stopped(s, u);
            }
            s.Events.Add(new BattleEvent(BattleEventKind.Turned, u.Id, u.Cell, face));
            at(pose++);
        }
        return null;
    }

    /// <summary>Stopped in the middle of a turn: the heading it was caught in.</summary>
    private static string? Stopped(BattleState s, BattleUnit u)
    {
        s.Events.Add(new BattleEvent(BattleEventKind.Turned, u.Id, u.Cell, u.Heading));
        return null;
    }

    /// <summary>Sixty degrees from <paramref name="from"/> towards
    /// <paramref name="to"/>, the short way; about, clockwise.</summary>
    public static int TurnStep(int from, int to)
    {
        int diff = ((to - from) % 360 + 360) % 360;
        if (diff == 0)
            return from;
        return ((from + (diff <= 180 ? 60 : -60)) % 360 + 360) % 360;
    }

    /// <summary>
    /// When each enemy ambush fires at the moving hull (units.md, «Режим
    /// засады», 2026-10-31): the move is walked once on a copy, the ambushes
    /// held, and at each pose an ambush sees the hull in, its round is tried on
    /// a copy of that. It fires at the first pose where the round knocks the
    /// hull out; where none does, at the first it sees it in - the
    /// stabilizer's second round the same among the poses after its first.
    /// Poses past where the move stops on its own (a mine, the deep) are not
    /// had: the walk on the copy stops there too. Null with no ambush to fire.
    /// <paramref name="stopOnly"/> - the forced engine (upgrades.md): only the
    /// pose the move ends in is had; an ambush whose axis is only crossed does
    /// not fire, and is not spent.
    /// </summary>
    public static Dictionary<int, Queue<int>>? AmbushPicks(BattleState s, BattleUnit u, IReadOnlyList<Vector2I> path,
                                                          int face, bool? back, bool stopOnly = false)
    {
        bool any = false;
        foreach (BattleUnit w in s.Units)
            any |= w.Side != u.Side && w.Fate == UnitFate.Alive && w.Ambushing;
        if (!any)
            return null;
        BattleState t = s.Clone();
        BattleUnit tu = t.Unit(u.Id)!;
        var seen = new Dictionary<int, List<(int Pose, bool Knocks, int Axis)>>();
        int last = -1;
        // the shot on the move is fired on the copy too: an ambusher it knocks
        // out picks its moment only among the poses before it (units.md)
        bool onWayShot = false;
        Walk(t, tu, path, face, back, pose =>
        {
            last = Math.Max(last, pose);
            foreach (BattleUnit w in t.Units)
            {
                if (w.Side == tu.Side || w.Fate != UnitFate.Alive || !w.Ambushing)
                    continue;
                int axis = Sees(t, w, tu);
                if (axis < 0)
                    continue;
                BattleState tried = t.Clone();
                BattleUnit target = tried.Unit(tu.Id)!;
                Shoot(tried, tried.Unit(w.Id)!, target, axis);
                if (!seen.TryGetValue(w.Id, out var poses))
                    seen[w.Id] = poses = new List<(int, bool, int)>();
                poses.Add((pose, target.Fate != UnitFate.Alive, axis));
            }
            return FireOnWay(t, tu, ref onWayShot) || true;
        });
        if (stopOnly)
            foreach (var poses in seen.Values)
                poses.RemoveAll(p => p.Pose != last);
        if (seen.Count == 0)
            return null;
        var picks = new Dictionary<int, Queue<int>>();
        foreach (var (id, poses) in seen)
        {
            var queue = new Queue<int>();
            int after = -1;
            BattleUnit watcher = s.Unit(id)!;
            // nought reads as one (BattleUnit.WatchShots), as Ambushes spends it
            int shots = Math.Max(1, watcher.WatchShots);
            // a sector's axis fires once (units/mt.md): its next round is another's
            bool sector = watcher.Watch2 >= 0;
            var spent = new List<int>();
            for (int shot = 0; shot < shots; shot++)
            {
                int pick = -1, pickAxis = -1;
                foreach ((int pose, bool knocks, int axis) in poses)
                {
                    if (pose <= after || (sector && spent.Contains(axis)))
                        continue;
                    if (pick < 0)
                    {
                        pick = pose;
                        pickAxis = axis;
                    }
                    if (knocks)
                    {
                        pick = pose;
                        pickAxis = axis;
                        break;
                    }
                }
                if (pick < 0)
                    break;
                queue.Enqueue(pick);
                spent.Add(pickAxis);
                after = pick;
            }
            picks[id] = queue;
        }
        return picks;
    }

    /// <summary>The ambushes whose moment <paramref name="pose"/> is (<see
    /// cref="AmbushPicks"/>) fire at <paramref name="u"/>, in the units'
    /// order; false once it is knocked out or the match is over.</summary>
    private static bool AmbushesAt(BattleState s, BattleUnit u, int pose, Dictionary<int, Queue<int>> picks)
    {
        foreach (BattleUnit w in s.Units)
        {
            if (!picks.TryGetValue(w.Id, out Queue<int>? queue) || queue.Count == 0 || queue.Peek() != pose)
                continue;
            queue.Dequeue();
            if (w.Fate != UnitFate.Alive || !w.Ambushing || Sees(s, w, u) is not (>= 0 and int axis))
                continue;
            Shoot(s, w, u, axis);
            Over(s);
            if (u.Fate != UnitFate.Alive || s.Over)
                return false;
        }
        return true;
    }

    /// <summary>A turn on the spot: the move of the turn spent on it.</summary>
    public static string? Turn(BattleState s, int face)
    {
        if (Acting(s, out string? why) is not { } u)
            return why;
        Steps.Mover mover = MoverOf(s, u);
        int cost = Steps.TurnCost(u.Heading, face, mover);
        if (cost > Steps.BudgetAt(s.Board, u.Cell, mover))
            return $"no steps to turn {cost}";
        return Move(s, new[] { u.Cell }, face);
    }

    /// <summary>
    /// The move as a command gives it (Д5) - to <paramref name="cell"/>, turned
    /// to <paramref name="face"/> at the end if the budget has it, -1 to stay
    /// as it arrives: the route is the planner's (<see cref="Steps.Plan"/>, the
    /// straightest of the cheapest, past the standing units), the budget its,
    /// then <see cref="Move"/> along it. What the AI orders.
    /// </summary>
    public static string? Go(BattleState s, Vector2I cell, int face = -1)
    {
        if (Acting(s, out string? why) is not { } u)
            return why;
        Steps.Reach reach = Steps.Plan(s.Board, new Steps.Pose(u.Cell, u.Heading), MoverOf(s, u));
        if (reach.RouteTo(cell) is not { } route)
            return $"({cell.X},{cell.Y}) is out of reach";
        if (face >= 0 && face != route.End.Heading && reach.TurnTo(route, face) is null)
            return $"no steps left to turn to {face}";
        return Move(s, route.Cells(), face >= 0 ? face : route.End.Heading, route.Back);
    }

    /// <summary>How unit <paramref name="u"/> moves on this state: its class,
    /// its kit, the standing units in its way.</summary>
    public static Steps.Mover MoverOf(BattleState s, BattleUnit u)
    {
        var barred = new HashSet<Vector2I>();
        foreach (BattleUnit o in s.Units)
            if (o.Standing && o.Id != u.Id)
                barred.Add(o.Cell);
        Steps.Mover mover = Steps.Mover.Of(u.Class, (u.Kit & Kit.Snorkel) != 0, barred);
        Steps.Kitted(mover, u.Kit);
        return mover;
    }

    /// <summary>What a cell does to a hull that comes on to it, driven or pushed
    /// (field.md, «мины», «вода», «огонь\пожар»; states.md, «Горит»). True
    /// when the hex ends a move that is still alive on it: the mine guard's
    /// mine (upgrades.md, «Противоминная защита», 2026-11-02) - gone off and
    /// spent under the hull however it came, driven nose or tail first or
    /// shoved, the hull alight rather than knocked out.</summary>
    private static bool Arrive(BattleState s, BattleUnit u, int way, bool forward)
    {
        Vector2I c = u.Cell;
        bool stops = false;
        if (s.Board.CoverAt(c) == Cover.Minefield && s.Board.CoverStateAt(c) == CoverState.Intact)
        {
            s.Board.SetCoverState(c, CoverState.Cleared);
            s.Events.Add(new BattleEvent(BattleEventKind.MineBlown, u.Id, c));
            if ((u.Kit & Kit.MineGuard) == 0 || u.Fate != UnitFate.Alive)
            {
                Knock(s, u);
                return false;
            }
            Ignite(s, u, "mine");
            stops = true;
        }
        if (s.Board.IsWater(c))
        {
            if (u.Burning)
                Douse(s, u);
            if (s.Board.IsDeep(c) && u.Fate == UnitFate.Alive)
            {
                if ((u.Kit & Kit.Snorkel) != 0)
                    s.Events.Add(new BattleEvent(BattleEventKind.Afloat, u.Id, c));
                else
                {
                    u.Fate = UnitFate.Drowned;
                    s.Harmed(u);
                    s.Events.Add(new BattleEvent(BattleEventKind.Drowned, u.Id, c));
                    Lift(s, u, "drowned");
                }
            }
            return stops;
        }
        if (s.Board.CoverAt(c) == Cover.Forest)
        {
            if (s.Board.CoverStateAt(c) == CoverState.Burning)
                Ignite(s, u);
            else if (u.Burning)
                WoodAlight(s, c);
        }
        return stops;
    }

    // --- the shot -------------------------------------------------------------------

    /// <summary>
    /// The attack (п. 5а): a round at <paramref name="aim"/>, any hex on one of
    /// the axes (units.md, «Цель выстрела»); a casemate's axis is its hull's, a
    /// mortar's hex is 3 to 5 along it. The round is spent on the first thing
    /// it meets (<see cref="Fly"/>); one aimed at empty ground flies on past it.
    /// <paramref name="incendiary"/> - the incendiary round (upgrades.md,
    /// «Зажигательный снаряд») in place of the plain one, by the same rules of
    /// aim and axis: a hull it does not pierce catches fire, a living wood it is
    /// spent in catches; the mortar's bomb the same. <paramref name="smoke"/> -
    /// the smoke round (upgrades.md, «Дымовой снаряд»): smoke on the hex aimed
    /// at, an empty one - no tank, wood, walls, minefield or smoke on it - that
    /// the line of fire reaches, short of the first thing on the axis; the
    /// mortar's smoke bomb goes over everything on to a hex 3 to 5 out (3 to 6
    /// with the bracket).
    /// </summary>
    public static string? Fire(BattleState s, Vector2I aim, bool incendiary = false, bool smoke = false)
    {
        if (Acting(s, out string? why) is not { } u)
            return why;
        if (s.Fired)
            return "already fired";
        if (Held(s))
            return "the first turn holds fire";
        if (incendiary && (u.Kit & Kit.Incendiary) == 0)
            return "no incendiary rounds";
        if (smoke && (u.Kit & Kit.SmokeShell) == 0)
            return "no smoke rounds";
        if (smoke && incendiary)
            return "one round at a time";
        (int heading, int range) = s.Board.LaneTo(u.Cell, aim);
        if (heading < 0)
            return "not on an axis";
        if (!u.Class.Turreted && heading != u.Heading)
            return "a casemate fires along its hull";
        BattleUnit? there = s.At(aim);
        int reach = Gunnery.LobReachOf(u.Kit);
        switch (Gunnery.MayTarget(u.Class.Lobs, range, there is not null, reach))
        {
            case Gunnery.Distance.Own: return "its own hex";
            case Gunnery.Distance.MortarBand: return $"the mortar throws {Gunnery.LobShortest} to {reach}, this is {range}";
            case Gunnery.Distance.PointBlank: return "a tank point-blank";
        }
        if (smoke)
            return SmokeRound(s, u, aim);
        s.Fired = true;
        Lift(s, u, "fired");
        if (u.Class.Lobs)
            Lob(s, aim, incendiary);
        else
            Fly(s, u.Cell, heading, u.Class.Might, Level(s, u), passes: u.Class.Might >= 4 && !u.Class.Turreted ? 1 : 0,
                glance: true, aim: aim, fiery: incendiary, thermal: (u.Kit & Kit.Thermal) != 0,
                woodPass: (u.Kit & Kit.ThroughWood) != 0);
        return null;
    }

    /// <summary>The smoke round on <paramref name="aim"/>, the axis and the
    /// distance already the rules' (<see cref="Fire"/>): the hex empty, the
    /// gun's lane to it open - a hull, a wood, smoke (unless the imager sees
    /// through it), the rising ground or a wall stop it short; the mortar's goes
    /// over them. The shot spent, the smoke laid; nothing else is touched.</summary>
    private static string? SmokeRound(BattleState s, BattleUnit u, Vector2I aim)
    {
        if (s.At(aim) is not null)
            return "a tank stands there";
        if (s.Board.CoverAt(aim) != Cover.None)
            return $"{s.Board.CoverAt(aim)} there";
        if (s.Board.Smoked(aim))
            return "smoke there already";
        if (!u.Class.Lobs
            && Gunnery.Solve(s.Board, u.Cell, aim, false, c => s.At(c) is not null,
                             standing: c => s.At(c) is { } t ? Level(s, t) : s.Board.LevelAt(c),
                             thermal: (u.Kit & Kit.Thermal) != 0).BlockedAt is Vector2I stop)
            return $"the line of fire is shut at ({stop.X - s.Shift.X},{stop.Y - s.Shift.Y})";
        s.Fired = true;
        Lift(s, u, "fired");
        LaySmoke(s, u, aim, "round");
        return null;
    }

    /// <summary>The level a hull fires from and is shot at: its cell's, but
    /// afloat on the bank's (field.md, 2026-10-29).</summary>
    internal static int Level(BattleState s, BattleUnit u) =>
        s.Board.LevelAt(u.Cell) + (u.Fate == UnitFate.Alive && s.Board.IsDeep(u.Cell) && s.Board.RampHeading(u.Cell) < 0 ? 1 : 0);

    /// <summary>
    /// A round along an axis from <paramref name="from"/>: the edge into each
    /// cell, then what stands on it - a wall on the edge, a hull at or above
    /// the floor, the ground rising over the floor, a wood. A wall is knocked
    /// down; a hull is struck by its plate; ground and wood spend it. The
    /// destroyer's round (<paramref name="passes"/> 1) goes through the first
    /// wall or hull it meets at one less might. A side that holds sends it on
    /// mirrored across the hull (<paramref name="glance"/>, once). A
    /// <paramref name="fiery"/> round sets alight what it meets and does not
    /// pierce: a hull, a living wood - on every leg, the ricochet's too.
    /// </summary>
    private static void Fly(BattleState s, Vector2I from, int heading, int might, int fromLevel,
                            int passes, bool glance, Vector2I? aim, bool fiery = false, bool thermal = false,
                            bool woodPass = false)
    {
        int ontoLevel = aim is Vector2I a && s.Board.InBounds(a)
            ? (s.At(a) is { } t ? Level(s, t) : s.Board.LevelAt(a)) : fromLevel;
        Vector2I at = from;
        for (int i = 0; i < 64; i++)
        {
            Vector2I c = HexField.Step(at, heading);
            if (c == at || !s.Board.InBounds(c))
            {
                s.Events.Add(new BattleEvent(BattleEventKind.Spent, -1, at, heading, "edge"));
                return;
            }
            if (s.Board.Walled(at, heading))
            {
                WallDown(s, at, heading);
                if (passes > 0)
                {
                    passes--;
                    might--;
                    s.Events.Add(new BattleEvent(BattleEventKind.Passed, -1, c, might, "wall"));
                }
                else
                {
                    s.Events.Add(new BattleEvent(BattleEventKind.Spent, -1, c, heading, "wall"));
                    return;
                }
            }
            at = c;
            int floor = Math.Min(fromLevel, ontoLevel);
            if (s.At(c) is { } hit && Level(s, hit) >= floor)
            {
                bool through = passes > 0;
                Strike(s, hit, heading, might, fromLevel, glance && !through, through, fiery);
                if (!through)
                    return;
                passes--;
                might--;
                s.Events.Add(new BattleEvent(BattleEventKind.Passed, hit.Id, c, might, "hull"));
                continue;
            }
            if (Gunnery.Overtops(s.Board, c, fromLevel, ontoLevel))
            {
                s.Events.Add(new BattleEvent(BattleEventKind.Spent, -1, c, heading, "ground"));
                return;
            }
            // a wood screens its hex; masonry its edges (above, BoardRules.Screened)
            if (s.Board.LevelAt(c) >= floor && BoardRules.Screened(s.Board, c, thermal))
            {
                // through the wood (upgrades.md, «Сквозь лес»): the destroyer's
                // one pass on a wood in any state, the round on at one less, the
                // wood untouched. Not an incendiary round: what it meets burns.
                if (woodPass && passes > 0 && !fiery && s.Board.CoverAt(c) == Cover.Forest)
                {
                    passes--;
                    might--;
                    s.Events.Add(new BattleEvent(BattleEventKind.Passed, -1, c, might, "wood"));
                    continue;
                }
                s.Events.Add(new BattleEvent(BattleEventKind.Spent, -1, c, heading, "wood"));
                // the one way to set a wood alight with a shot (upgrades.md)
                if (fiery)
                    WoodAlight(s, c);
                return;
            }
        }
    }

    /// <summary>A round of <paramref name="might"/> travelling
    /// <paramref name="heading"/> into a hull (units.md, «Броня», «Рикошет»).</summary>
    private static void Strike(BattleState s, BattleUnit hit, int heading, int might, int fromLevel,
                               bool glance, bool through, bool fiery = false)
    {
        s.LastStrike = s.Round;
        if (hit.Out)
        {
            s.Events.Add(new BattleEvent(BattleEventKind.Hit, hit.Id, hit.Cell, might, "out"));
            Explode(s, hit);
            return;
        }
        Gunnery.Plate plate = PlateOf(heading, hit.Heading, fromLevel > Level(s, hit));
        if (Gunnery.Penetration(might, hit.Class, plate) > 0)
        {
            s.Events.Add(new BattleEvent(BattleEventKind.Hit, hit.Id, hit.Cell, might, plate.ToString()));
            Knock(s, hit);
            return;
        }
        s.Events.Add(new BattleEvent(BattleEventKind.Glanced, hit.Id, hit.Cell, might, plate.ToString()));
        // not pierced, set alight (upgrades.md, «Зажигательный снаряд»)
        if (fiery)
            Ignite(s, hit, "incendiary");
        if (through || !glance || !Gunnery.Ricochets(plate))
            return;
        // the side plate a mirror along the hull: «2·hull − arrival»
        int onward = (((2 * hit.Heading - heading) % 360) + 360) % 360;
        s.Events.Add(new BattleEvent(BattleEventKind.Ricochet, hit.Id, hit.Cell, onward));
        // the onward round: no floor's gap to keep, no second glance (units.md)
        Fly(s, hit.Cell, onward, might, Level(s, hit), passes: 0, glance: false, aim: null, fiery: fiery);
    }

    /// <summary>The plate a round meets: the roof from above, else the face of
    /// the hex it comes in through, sorted by the hull (units.md, «Броня»).</summary>
    public static Gunnery.Plate PlateOf(int travelling, int hull, bool fromAbove) =>
        fromAbove ? Gunnery.Plate.Roof
        : HexField.Reverse(travelling) == hull ? Gunnery.Plate.Front
        : travelling == hull ? Gunnery.Plate.Rear
        : Gunnery.Plate.Side;

    /// <summary>The mortar's bomb on a hex (classes.md, «HM»): a hull takes it
    /// whole - knocked out, or gone up if it was out already; with none, every
    /// wall on the hex comes down and a mine there goes off. Woods and water
    /// are a miss - but a <paramref name="fiery"/> bomb sets a living wood
    /// alight (upgrades.md: «у HM - мина миномёта с теми же свойствами»); on a
    /// hull it knocks out as any bomb, there is nothing it does not pierce.</summary>
    private static void Lob(BattleState s, Vector2I aim, bool fiery = false)
    {
        if (s.At(aim) is { } hit)
        {
            s.LastStrike = s.Round;
            s.Events.Add(new BattleEvent(BattleEventKind.Hit, hit.Id, aim, 5, "Roof"));
            if (hit.Out)
                Explode(s, hit);
            else
                Knock(s, hit);
            return;
        }
        bool any = false;
        foreach (int h in HexField.EdgeHeadings)
            if (s.Board.Walled(aim, h))
            {
                WallDown(s, aim, h);
                any = true;
            }
        if (s.Board.CoverAt(aim) == Cover.Minefield && s.Board.CoverStateAt(aim) == CoverState.Intact)
        {
            s.Board.SetCoverState(aim, CoverState.Cleared);
            s.Events.Add(new BattleEvent(BattleEventKind.MineBlown, -1, aim));
            any = true;
        }
        if (fiery && s.Board.CoverAt(aim) == Cover.Forest && s.Board.CoverStateAt(aim) == CoverState.Intact)
        {
            WoodAlight(s, aim);
            any = true;
        }
        if (!any)
            s.Events.Add(new BattleEvent(BattleEventKind.Spent, -1, aim, 0, "lob"));
    }

    // --- the bonus act --------------------------------------------------------------

    /// <summary>
    /// The ram (units.md, «Таран»; п. 5б) of the unit standing beside
    /// <paramref name="target"/>, nose to it: the target is pushed a hex along
    /// the ram (<see cref="Ramming.Of"/> says whether it may be and where it
    /// lands), the rammer takes its hex, and both get what their new hexes
    /// do. The heavy brings down the walls on both hexes. The hull is turned to
    /// the target if it is not - the bench turns it as part of the order.
    /// </summary>
    public static string? Ram(BattleState s, Vector2I target)
    {
        if (Acting(s, out string? why) is not { } u)
            return why;
        if (BonusGone(s, u) is { } spent)
            return spent;
        if (s.At(target) is not { } victim)
            return "nobody to ram";
        int way = HexField.HeadingTo(u.Cell, target);
        if (way < 0)
            return "not beside it";
        if (u.Class.Bulldozes)
        {
            WallDownAll(s, target);
        }
        Ramming.Shove push = Ramming.Of(s.Board, c => c != target && s.At(c) is not null, u.Class, victim.Class, target, way,
                                        two: (u.Kit & Kit.PushTwo) != 0);
        if (!push.Allowed)
        {
            s.Events.Add(new BattleEvent(BattleEventKind.RamRefused, u.Id, target, way, Ramming.Because(push.Why)));
            return Ramming.Because(push.Why);
        }
        s.Bonus = true;
        // a ram of a tank puts the stalemate's clock back, as a hit does
        // (GameModes.md, «Пат — ничья», decided 2026-11-05)
        s.LastStrike = s.Round;
        if (!(u.Class.FreeOrder && !s.Drove))
            s.Drove = true;
        // an order to the rammer, a ram to the rammed: both ambushes off
        Lift(s, u, "rammed");
        // the stabilizer holds it through a ram (upgrades.md)
        if ((victim.Kit & Kit.AmbushStabilizer) == 0)
            Lift(s, victim, "was rammed");
        u.Heading = way;
        // the hexes the target is thrown on to and stops on: one - the ramp's
        // slide lands at its foot - or, with the push of two (upgrades.md,
        // «Толчок на два гекса»), two in turn, as a path: each one's effects
        // had, and a hull knocked out, drowned or stopped by a mine on the
        // first goes no further. The bulldozer's walls come down on all three.
        IReadOnlyList<Vector2I> hexes = push.Two ? push.Legs : new[] { push.Legs[^1] };
        if (u.Class.Bulldozes)
            foreach (Vector2I h in hexes)
                WallDownAll(s, h);
        bool pin = (u.Kit & Kit.RamBlade) != 0 && victim.Fate == UnitFate.Alive && !victim.Pinned;
        for (int i = 0; i < hexes.Count; i++)
        {
            Vector2I at = hexes[i];
            victim.Cell = at;
            s.Events.Add(new BattleEvent(BattleEventKind.Shoved, victim.Id, at, way));
            if (i == 0)
            {
                u.Cell = target;
                s.Events.Add(new BattleEvent(BattleEventKind.Moved, u.Id, target, way));
                // the ram blade (upgrades.md, «Таранный отвал»): no move in its next activation
                if (pin)
                {
                    victim.Pinned = true;
                    s.Events.Add(new BattleEvent(BattleEventKind.Pinned, victim.Id, at, u.Id));
                }
            }
            if (victim.Fate == UnitFate.Alive)
            {
                bool stops = Entered(s, victim, way, forward: way == victim.Heading);
                if (victim.Fate != UnitFate.Alive || stops)
                    break;
                continue;
            }
            if (s.Board.IsDeep(at) && victim.Fate == UnitFate.Knocked)
            {
                victim.Fate = UnitFate.Drowned;
                s.Harmed(victim);
                s.Events.Add(new BattleEvent(BattleEventKind.Drowned, victim.Id, at));
            }
            // a hull out of the fight shoved on to an axis is crossing it too
            // (units.md: «Отброшенный тараном вражеский танк»; states.md: the
            // ambush's round on it is the one that destroys it)
            Disperse(s, victim);
            Ambushes(s, victim);
            if (victim.Fate != UnitFate.Knocked)
                break;
        }
        Entered(s, u, way, forward: true);
        Over(s);
        return null;
    }

    /// <summary>A wall rammed (field.md, «кирпичные стены»; п. 5б): through the
    /// edge ahead on to the hex past it, the wall down, the hull whole.</summary>
    public static string? RamWall(BattleState s, int heading)
    {
        if (Acting(s, out string? why) is not { } u)
            return why;
        if (BonusGone(s, u) is { } spent)
            return spent;
        if (!s.Board.Walled(u.Cell, heading))
            return "no wall there";
        Vector2I past = HexField.Step(u.Cell, heading);
        if (!s.Board.Passable(u.Cell, heading) || s.At(past) is not null)
            return "nowhere to go through to";
        s.Bonus = true;
        Lift(s, u, "rammed a wall");
        WallDown(s, u.Cell, heading);
        u.Heading = heading;
        u.Cell = past;
        s.Events.Add(new BattleEvent(BattleEventKind.Moved, u.Id, past, heading));
        Entered(s, u, heading, forward: true);
        Over(s);
        return null;
    }

    /// <summary>The fire put out (states.md, «Горит»; п. 5б) - not on a burning hex.</summary>
    public static string? Douse(BattleState s)
    {
        if (Acting(s, out string? why) is not { } u)
            return why;
        if (BonusGone(s, u) is { } spent)
            return spent;
        if (!u.Burning)
            return "not burning";
        if (s.Board.CoverAt(u.Cell) == Cover.Forest && s.Board.CoverStateAt(u.Cell) == CoverState.Burning)
            return "on a burning hex";
        s.Bonus = true;
        Lift(s, u, "doused");
        Douse(s, u);
        return null;
    }

    /// <summary>
    /// The forced engine (upgrades.md, «Форсированный двигатель», 2026-11-02;
    /// п. 5б): said before the move - an exception to «the move first, then the
    /// acts», as LT's free order is. This turn the move's crossings of an enemy
    /// ambush's axis draw no fire; an ambush fires only on the hull stopped on
    /// its axis, and one crossed is not spent (<see cref="AmbushPicks"/>,
    /// <c>stopOnly</c>). It is the turn's bonus act: no ram, no putting a fire
    /// out after it.
    /// </summary>
    public static string? Overdrive(BattleState s)
    {
        if (Acting(s, out string? why) is not { } u)
            return why;
        if ((u.Kit & Kit.Overdrive) == 0)
            return "no forced engine";
        if (BonusGone(s, u) is { } spent)
            return spent;
        if (s.Drove)
            return "said before the move";
        if (u.Pinned)
            return "pinned by a ram blade";
        s.Bonus = s.Overdrive = true;
        Lift(s, u, "overdrive");
        s.Events.Add(new BattleEvent(BattleEventKind.Overdrive, u.Id, u.Cell));
        return null;
    }

    /// <summary>Why the bonus act may not be had: spent this turn, or owed to
    /// the auto-extinguisher (<see cref="BattleUnit.OwesBonus"/>); null if it may.</summary>
    private static string? BonusGone(BattleState s, BattleUnit u) =>
        s.Bonus ? "the bonus act is spent"
        : u.OwesBonus ? "the bonus act is owed to the extinguisher"
        : null;

    /// <summary>Whether the unit on turn may still have a bonus act - the
    /// turn's not spent and none owed (<see cref="BonusGone"/>).</summary>
    public static bool BonusLeft(BattleState s, BattleUnit u) => BonusGone(s, u) is null;

    /// <summary>
    /// The double turn (upgrades.md, «Двойной поворот», 2026-11-02; п. 5б): the
    /// hull 60° round to <paramref name="face"/> with no step - after the move,
    /// before or after the attack. The move is over with it, LT's too: the
    /// rule says «после движения». Not on a ramp, which nothing turns on. An
    /// enemy ambush that sees the hull answers it as on a turn in the move
    /// (<see cref="AmbushPicks"/>).
    /// </summary>
    public static string? Pivot(BattleState s, int face)
    {
        if (Acting(s, out string? why) is not { } u)
            return why;
        if ((u.Kit & Kit.DoubleTurn) == 0)
            return "no double turn";
        if (BonusGone(s, u) is { } spent)
            return spent;
        if (u.Pinned)
            return "pinned by a ram blade";
        if (Array.IndexOf(HexField.EdgeHeadings, face) < 0)
            return $"{face} is not a side";
        if (Steps.Turning(u.Heading, face) != 1)
            return "the double turn is sixty degrees";
        if (s.Board.IsRamp(u.Cell))
            return "not on a ramp";
        s.Bonus = s.Drove = true;
        Lift(s, u, "turned");
        var here = new[] { u.Cell };
        Dictionary<int, Queue<int>>? picks = AmbushPicks(s, u, here, face, null);
        return Walk(s, u, here, face, null, pose => picks is null || AmbushesAt(s, u, pose, picks));
    }

    // --- the ambush ---------------------------------------------------------------------

    /// <summary>
    /// The ambush (units.md, «Режим засады»; п. 5а): the attack of the turn
    /// held back on <paramref name="axis"/> - for the medium (classes.md,
    /// «Секторная засада») and its <paramref name="second"/> beside it too, a
    /// round on each (units/mt.md, 2026-11-02); with the wide sector
    /// (upgrades.md, «Широкий сектор») a <paramref name="third"/>, the three
    /// side by side, still two rounds. Set last: the turn is spent with it, the
    /// move and the bonus act as well, for any act of the unit after it would
    /// lift it. A casemate watches along its hull - turned to the axis first,
    /// out of the move's steps.
    /// </summary>
    public static string? Ambush(BattleState s, int axis, int second = -1, int third = -1)
    {
        if (Acting(s, out string? why) is not { } u)
            return why;
        if (s.Fired)
            return "the attack is spent";
        if (Array.IndexOf(HexField.EdgeHeadings, axis) < 0)
            return $"{axis} is not an axis";
        if (second >= 0)
        {
            if (!u.Class.WatchesSector)
                return "only the medium watches two axes";
            int apart = ((second - axis) % 360 + 360) % 360;
            if (apart != 60 && apart != 300)
                return "the two axes must be neighbours";
        }
        if (third >= 0)
        {
            if (second < 0)
                return "a third axis wants a second";
            if ((u.Kit & Kit.WideSector) == 0)
                return "no wide sector";
            if (!Wedge(axis, second, third))
                return "the three axes must be side by side";
        }
        if (!u.Class.Turreted && axis != u.Heading)
            return "a casemate watches along its hull";
        s.Fired = s.Drove = s.Bonus = true;
        u.Watch = axis;
        u.Watch2 = second;
        u.Watch3 = third;
        // a sector's round an axis, two of the wide one's three; the
        // stabilizer watches on past the first round (upgrades.md) - on the
        // sector it adds none (units/mt.md)
        u.WatchShots = second >= 0 || (u.Kit & Kit.AmbushStabilizer) != 0 ? 2 : 1;
        s.Events.Add(new BattleEvent(BattleEventKind.AmbushSet, u.Id, u.Cell, axis,
                                     second < 0 ? "" : third < 0 ? $"and {second}" : $"and {second}, {third}"));
        return null;
    }

    /// <summary>Three sides that make a wedge of 120 degrees: one of them with
    /// both the next two clockwise among them.</summary>
    public static bool Wedge(int a, int b, int c)
    {
        static int N(int h) => ((h % 360) + 360) % 360;
        int[] all = { N(a), N(b), N(c) };
        foreach (int x in all)
            if (Array.IndexOf(all, N(x + 60)) >= 0 && Array.IndexOf(all, N(x + 120)) >= 0)
                return true;
        return false;
    }

    /// <summary>Ambush <paramref name="w"/> has fired along <paramref name="axis"/>:
    /// a round spent, the watch off with the last - a sector's axis struck off
    /// with its own (units/mt.md), the rest moved up.</summary>
    public static void Spend(BattleUnit w, int axis)
    {
        if (--w.WatchShots <= 0)
        {
            w.Watch = w.Watch2 = w.Watch3 = -1;
            return;
        }
        if (w.Watch2 < 0)
            return;
        if (w.Watch == axis)
        {
            w.Watch = w.Watch2;
            w.Watch2 = w.Watch3;
        }
        else if (w.Watch2 == axis)
            w.Watch2 = w.Watch3;
        else if (w.Watch3 != axis)
            return;
        w.Watch3 = -1;
    }

    /// <summary>A cell's effects on a hull that came on to it (<see cref="Arrive"/>),
    /// then the enemy's ambushes on it - in that order (match.md п. 4). An
    /// effect of the hex that knocks it out ends the hex: the ambush answers
    /// the hull as it came in.</summary>
    private static bool Entered(BattleState s, BattleUnit u, int way, bool forward, bool ambushes = true)
    {
        UnitFate was = u.Fate;
        Disperse(s, u);
        bool stops = Arrive(s, u, way, forward);
        if (ambushes && u.Fate == was)
            Ambushes(s, u);
        return stops;
    }

    // --- smoke ------------------------------------------------------------------------

    /// <summary>Smoke a unit comes on to, or drives through, is gone (field.md, «дым»).</summary>
    private static void Disperse(BattleState s, BattleUnit u)
    {
        if (!s.Smoke.Remove(u.Cell))
            return;
        s.Board.SetSmoke(u.Cell, false);
        s.Events.Add(new BattleEvent(BattleEventKind.SmokeGone, u.Id, u.Cell, 0, "dispersed"));
    }

    /// <summary>
    /// The smoke grenade (upgrades.md, «Дымовая шашка»; п. 5б): smoke on a
    /// hex beside the unit - not its own, not one with a tank, a wood in any
    /// state, walls or a minefield (the hex's one obstacle slot, field.md) -
    /// for two rounds. It stops rounds and ambushes' axes as a wood does; the
    /// mortar's go over it.
    /// </summary>
    public static string? Smoke(BattleState s, Vector2I cell)
    {
        if (Acting(s, out string? why) is not { } u)
            return why;
        if ((u.Kit & Kit.Smoke) == 0)
            return "no smoke grenade";
        if (BonusGone(s, u) is { } spent)
            return spent;
        if (HexField.HeadingTo(u.Cell, cell) < 0 || !s.Board.InBounds(cell))
            return "only on a hex beside";
        if (s.At(cell) is not null)
            return "a tank stands there";
        if (s.Board.CoverAt(cell) != Cover.None)
            return $"{s.Board.CoverAt(cell)} there";
        if (s.Board.Smoked(cell))
            return "smoke there already";
        s.Bonus = true;
        Lift(s, u, "smoke");
        LaySmoke(s, u, cell, "grenade");
        return null;
    }

    /// <summary>Smoke on <paramref name="cell"/> for two rounds, by the
    /// grenade or the round (<paramref name="by"/>).</summary>
    private static void LaySmoke(BattleState s, BattleUnit u, Vector2I cell, string by)
    {
        s.Board.SetSmoke(cell, true);
        s.Smoke[cell] = SmokeRounds;
        s.Events.Add(new BattleEvent(BattleEventKind.SmokeLaid, u.Id, cell, SmokeRounds, by));
    }

    private const int SmokeRounds = 2;

    // --- the bonus acts of the kit ---------------------------------------------------

    /// <summary>
    /// The mine layer (upgrades.md, «Минный заградитель»; п. 5б): a mine on a
    /// hex beside - not one with a tank, a mine already, water, or another
    /// cover (the hex's one slot, field.md); a ramp will do. Seen by all, and
    /// it goes off under its own side as well.
    /// </summary>
    public static string? LayMine(BattleState s, Vector2I cell)
    {
        if (Acting(s, out string? why) is not { } u)
            return why;
        if ((u.Kit & Kit.MineLayer) == 0)
            return "no mine layer";
        if (BonusGone(s, u) is { } spent)
            return spent;
        if (HexField.HeadingTo(u.Cell, cell) < 0 || !s.Board.InBounds(cell))
            return "only on a hex beside";
        if (s.At(cell) is not null)
            return "a tank stands there";
        if (s.Board.IsWater(cell))
            return "not on water";
        if (s.Board.CoverAt(cell) == Cover.Minefield && s.Board.CoverStateAt(cell) == CoverState.Intact)
            return "a mine there already";
        if (!s.Board.LayMinefield(cell))
            return $"{s.Board.CoverAt(cell)} there";
        s.Bonus = true;
        Lift(s, u, "laid a mine");
        s.Events.Add(new BattleEvent(BattleEventKind.MineLaid, u.Id, cell));
        return null;
    }

    /// <summary>
    /// The repair kit (upgrades.md, «Ремкомплект»; п. 5б): a knocked-out hull
    /// of the unit's own side on a hex beside fights again - from its side's
    /// next turn, this one being the repairer's. A drowned one is not raised.
    /// </summary>
    public static string? Repair(BattleState s, Vector2I cell)
    {
        if (Acting(s, out string? why) is not { } u)
            return why;
        if ((u.Kit & Kit.RepairKit) == 0)
            return "no repair kit";
        if (BonusGone(s, u) is { } spent)
            return spent;
        if (HexField.HeadingTo(u.Cell, cell) < 0)
            return "only a hull beside";
        if (s.At(cell) is not { } hull)
            return "nobody there";
        if (hull.Side != u.Side)
            return "not one of ours";
        if (hull.Fate == UnitFate.Drowned)
            return "a drowned hull is not raised";
        if (hull.Fate != UnitFate.Knocked)
            return "not knocked out";
        s.Bonus = true;
        Lift(s, u, "repaired");
        hull.Fate = UnitFate.Alive;
        s.Events.Add(new BattleEvent(BattleEventKind.Repaired, hull.Id, cell, u.Id));
        return null;
    }

    /// <summary>
    /// The winch (upgrades.md, «Лебёдка»; п. 5б): the hull 2 out along an axis
    /// of the unit's own level - either side's, out of the fight too, no heavier
    /// by mass than its own - pulled on to the hex between, which must be empty
    /// with no standing wall across either edge it crosses. What the hex does
    /// is done to it as on any entering (a mine, a fire, water, smoke), and an
    /// enemy ambush's axis it lands on answers it. Its own ambush is off - it
    /// was moved, as a ram moves it (a call of this build; the GDD is silent) -
    /// unless it has the stabilizer, which a ram does not lift either.
    /// </summary>
    public static string? Winch(BattleState s, Vector2I cell)
    {
        if (Acting(s, out string? why) is not { } u)
            return why;
        if ((u.Kit & Kit.Winch) == 0)
            return "no winch";
        if (BonusGone(s, u) is { } spent)
            return spent;
        if (s.At(cell) is not { } hull)
            return "nobody there";
        (int heading, int range) = s.Board.LaneTo(u.Cell, cell);
        if (heading < 0 || range != WinchReach)
            return $"the winch pulls a hull {WinchReach} out along an axis";
        if (hull.Class.Mass > u.Class.Mass)
            return "heavier than its own hull";
        Vector2I mid = HexField.Step(u.Cell, heading);
        int level = s.Board.LevelAt(u.Cell);
        if (s.Board.LevelAt(mid) != level || s.Board.LevelAt(cell) != level)
            return "only on its own level";
        if (s.At(mid) is not null)
            return "the hex between is taken";
        if (s.Board.Walled(u.Cell, heading) || s.Board.Walled(mid, heading))
            return "a wall across the way";
        s.Bonus = true;
        Lift(s, u, "winched");
        if ((hull.Kit & Kit.AmbushStabilizer) == 0)
            Lift(s, hull, "was winched");
        int way = HexField.Reverse(heading);
        hull.Cell = mid;
        s.Events.Add(new BattleEvent(BattleEventKind.Winched, hull.Id, mid, way, $"by #{u.Id}"));
        if (hull.Fate == UnitFate.Alive)
            Entered(s, hull, way, forward: way == hull.Heading);
        else
        {
            if (s.Board.IsDeep(mid) && hull.Fate == UnitFate.Knocked)
            {
                hull.Fate = UnitFate.Drowned;
                s.Harmed(hull);
                s.Events.Add(new BattleEvent(BattleEventKind.Drowned, hull.Id, mid));
            }
            Disperse(s, hull);
            Ambushes(s, hull);
        }
        Over(s);
        return null;
    }

    /// <summary>How far out the winch takes a hull, hexes (upgrades.md).</summary>
    public const int WinchReach = 2;

    /// <summary>
    /// Unit <paramref name="entered"/> has come on to its hex not under its
    /// own move - shoved by a ram, winched, or the rammer itself - so in one
    /// pose: every ambush of the other side whose axis sees it there now
    /// (<see cref="Sees"/>) fires, by the rules of a shot, and is spent. They
    /// fire in turn while the hull is as it came in: the first that knocks it
    /// out ends it. A move picks its moments instead (<see cref="AmbushPicks"/>).
    /// Public for the bench, which plays it on a hull shoved (Tank3DBench.Ambush).
    /// </summary>
    public static void Ambushes(BattleState s, BattleUnit entered)
    {
        UnitFate was = entered.Fate;
        foreach (BattleUnit w in s.Units)
        {
            if (entered.Fate != was || s.Over)
                return;
            if (w.Side == entered.Side || w.Fate != UnitFate.Alive || !w.Ambushing)
                continue;
            int axis = Sees(s, w, entered);
            if (axis < 0)
                continue;
            Shoot(s, w, entered, axis);
            Over(s);
        }
    }

    /// <summary>Ambush <paramref name="w"/> fires along <paramref name="axis"/>
    /// at <paramref name="target"/> as it stands, and is spent - with the
    /// stabilizer, at its second round (upgrades.md).</summary>
    private static void Shoot(BattleState s, BattleUnit w, BattleUnit target, int axis)
    {
        Spend(w, axis);
        // the hull's heading as it was caught, for the bench to fire as it takes it
        s.Events.Add(new BattleEvent(BattleEventKind.AmbushFired, w.Id, target.Cell, axis,
                                     $"at #{target.Id} facing {target.Heading}" + (w.Ambushing ? ", watching on" : "")));
        if (w.Class.Lobs)
            Lob(s, target.Cell);
        else
            Fly(s, w.Cell, axis, w.Class.Might, Level(s, w),
                passes: w.Class.Might >= 4 && !w.Class.Turreted ? 1 : 0, glance: true, aim: target.Cell,
                thermal: (w.Kit & Kit.Thermal) != 0);
    }

    /// <summary>
    /// Which of <paramref name="w"/>'s axes sees <paramref name="target"/>
    /// now, -1 for none - read at the moment it fires, not when it was set
    /// (units.md): on the axis, a clear hex between (the mortar's 3 to 5), and
    /// nothing before it a round would stop at - a wall across an edge, a hull,
    /// the ground rising, a wood; a hull of its own side cuts it too. The
    /// mortar's axis is cut the same: it watches, and watching wants the sight.
    /// </summary>
    public static int Sees(BattleState s, BattleUnit w, BattleUnit target)
    {
        (int heading, int range) = s.Board.LaneTo(w.Cell, target.Cell);
        if (heading < 0 || (heading != w.Watch && heading != w.Watch2 && heading != w.Watch3))
            return -1;
        if (Gunnery.MayTarget(w.Class.Lobs, range, true) != Gunnery.Distance.Fits)
            return -1;
        int fromLevel = Level(s, w), ontoLevel = Level(s, target);
        int floor = Math.Min(fromLevel, ontoLevel);
        Vector2I at = w.Cell;
        for (int k = 0; k < range; k++)
        {
            if (s.Board.Walled(at, heading))
                return -1;
            Vector2I c = HexField.Step(at, heading);
            if (c == target.Cell)
                return heading;
            if (s.At(c) is { } hit && Level(s, hit) >= floor)
                return -1;
            if (Gunnery.Overtops(s.Board, c, fromLevel, ontoLevel))
                return -1;
            // the thermal imager's axis goes through smoke (upgrades.md)
            if (s.Board.LevelAt(c) >= floor && BoardRules.Screened(s.Board, c, (w.Kit & Kit.Thermal) != 0))
                return -1;
            at = c;
        }
        return -1;
    }

    /// <summary>The ambush off (units.md, «Длится»): any order to the unit,
    /// a ram into it, its knocking out or destruction.</summary>
    private static void Lift(BattleState s, BattleUnit u, string why)
    {
        if (!u.Ambushing)
            return;
        u.Watch = u.Watch2 = u.Watch3 = -1;
        s.Events.Add(new BattleEvent(BattleEventKind.AmbushLifted, u.Id, u.Cell, 0, why));
    }

    // --- the end of the turn ----------------------------------------------------------

    /// <summary>
    /// П. 6 and 7: the side's units still burning from before this turn go up
    /// (states.md, «Горит»), the wood under a heavy that ends its turn on it
    /// falls (classes.md, «Бульдозер HT»), and the turn goes over - the round,
    /// and the field's tick, after the second side's (match.md, «Ход и раунд»).
    /// <para>The auto-extinguisher (upgrades.md, 2026-11-02): every burning unit
    /// with it goes out instead, activated or not, and keeps its ambush - but
    /// not on a burning hex, where it would only catch again, and that one
    /// goes up as any. Its price is one bonus act: this turn's, if the unit
    /// was the one activated and has it unspent; else it owes it
    /// (<see cref="BattleUnit.OwesBonus"/>), and its next activation goes
    /// without one. The activated unit's old debt is paid by this turn first -
    /// its bonus act was not to be had in it.</para>
    /// </summary>
    public static void EndTurn(BattleState s)
    {
        if (s.Over)
            return;
        Camp ending = s.Turn;
        if (s.Active is int id && s.Unit(id) is { Fate: UnitFate.Alive } u && u.Class.Bulldozes
            && s.Board.CoverAt(u.Cell) == Cover.Forest
            && s.Board.CoverStateAt(u.Cell) is CoverState.Intact or CoverState.Burnt)
        {
            s.Board.SetCoverState(u.Cell, CoverState.Cleared);
            s.Events.Add(new BattleEvent(BattleEventKind.WoodCleared, u.Id, u.Cell));
        }
        int activated = s.Active is int a && (s.Drove || s.Fired || s.Bonus) ? a : -1;
        // the activation it was pinned for has gone without its move
        if (s.Unit(activated) is { Pinned: true } freed)
            freed.Pinned = false;
        // the activation the debt was owed to has gone without its bonus act
        bool owed = false;
        if (s.Unit(activated) is { OwesBonus: true } payer)
        {
            payer.OwesBonus = false;
            owed = true;
        }
        foreach (BattleUnit b in s.Units.ToArray())
            if (b.Side == ending && b.Fate == UnitFate.Alive && b.Burning && b.BurnSince < s.TurnNo)
            {
                bool woodAlight = s.Board.CoverAt(b.Cell) == Cover.Forest && s.Board.CoverStateAt(b.Cell) == CoverState.Burning;
                if ((b.Kit & Kit.Extinguisher) == 0 || woodAlight)
                {
                    Explode(s, b);
                    continue;
                }
                Douse(s, b);
                if (b.Id == activated && !s.Bonus && !owed)
                {
                    s.Bonus = true;
                    s.Events.Add(new BattleEvent(BattleEventKind.BonusOwed, b.Id, b.Cell, 1, "paid with this turn's bonus act"));
                }
                else
                {
                    b.OwesBonus = true;
                    s.Events.Add(new BattleEvent(BattleEventKind.BonusOwed, b.Id, b.Cell, 0, "owed to its next activation"));
                }
            }
        s.Events.Add(new BattleEvent(BattleEventKind.TurnEnded, s.Active ?? -1, default, s.TurnNo));
        s.TurnNo++;
        s.Turn = ending == Camp.Green ? Camp.Brown : Camp.Green;
        s.Active = null;
        s.Drove = s.Fired = s.Bonus = s.Overdrive = false;
        s.OnWay = null;
        // the round over once the side after its lead has moved (match.md,
        // «Ход и раунд»), and the next round's lead to move
        if (ending != Leader(s))
        {
            FieldTick(s);
            s.Round++;
            s.Turn = Leader(s);
        }
        Over(s);
        Stalemate(s);
    }

    /// <summary>The side that moves first in this round: player 1 - but in
    /// every even round player 2 with the snake (<see cref="Handicap.Snake"/>).</summary>
    public static Camp Leader(BattleState s) =>
        s.Handicap == Handicap.Snake && s.Round % 2 == 0 ? (s.First == Camp.Green ? Camp.Brown : Camp.Green) : s.First;

    /// <summary>The ambush head start (<see cref="Handicap.Ambush"/>): player
    /// 2's guns - the mortar aside - watch along their hulls from the start,
    /// a round each, two with the stabilizer, as <see cref="Ambush"/> sets it.</summary>
    public static void AmbushAtStart(BattleState s)
    {
        Camp second = s.First == Camp.Green ? Camp.Brown : Camp.Green;
        foreach (BattleUnit u in s.Units)
        {
            if (u.Side != second || u.Fate != UnitFate.Alive || u.Class.Lobs)
                continue;
            u.Watch = u.Heading;
            u.Watch2 = u.Watch3 = -1;
            u.WatchShots = (u.Kit & Kit.AmbushStabilizer) != 0 ? 2 : 1;
            s.Events.Add(new BattleEvent(BattleEventKind.AmbushSet, u.Id, u.Cell, u.Heading, "at the start"));
        }
    }

    /// <summary>The hold head start (<see cref="Handicap.Hold"/>): no round in the match's first turn.</summary>
    private static bool Held(BattleState s) => s.Handicap == Handicap.Hold && s.TurnNo == 0;

    /// <summary>Пат (match.md, «Пат — ничья»): <see cref="MatchRules.QuietRoundsDraw"/>
    /// whole rounds without a shot at a tank - a round into the ground, a wall
    /// or a wood is no shot at one, nor a mine or a fire - and without the
    /// point changing hands (<see cref="BattleState.LastShift"/>). Read after the win:
    /// the last tank burnt out on the same tick is a win, not a draw.</summary>
    private static void Stalemate(BattleState s)
    {
        if (s.Over || s.QuietRounds < MatchRules.QuietRoundsDraw)
            return;
        s.Drawn = true;
        s.Events.Add(new BattleEvent(BattleEventKind.Drawn, -1, default, s.QuietRounds, "stalemate"));
    }

    /// <summary>The round's end (match.md): each burning wood a round shorter,
    /// burnt out at nought, and the fire on to the woods beside, a wall between
    /// stopping it (field.md, «огонь\пожар»). A unit on a wood the fire reaches
    /// catches.</summary>
    private static void FieldTick(BattleState s)
    {
        var burning = new List<Vector2I>(s.Fires.Keys);
        foreach (Vector2I c in burning)
            foreach (int h in HexField.EdgeHeadings)
            {
                Vector2I n = HexField.Step(c, h);
                if (!s.Fires.ContainsKey(n) && !s.Board.Walled(c, h)
                    && s.Board.CoverAt(n) == Cover.Forest && s.Board.CoverStateAt(n) == CoverState.Intact)
                    WoodAlight(s, n, fresh: true);
            }
        foreach (Vector2I c in burning)
        {
            int left = s.Fires[c] - 1;
            if (left > 0)
            {
                s.Fires[c] = left;
                continue;
            }
            s.Fires.Remove(c);
            s.Board.SetCoverState(c, CoverState.Burnt);
            s.Events.Add(new BattleEvent(BattleEventKind.WoodBurnt, -1, c));
        }
        foreach (Vector2I c in new List<Vector2I>(s.Smoke.Keys))
        {
            if (--s.Smoke[c] > 0)
                continue;
            s.Smoke.Remove(c);
            s.Board.SetSmoke(c, false);
            s.Events.Add(new BattleEvent(BattleEventKind.SmokeGone, -1, c, 0, "blown away"));
        }
        PointTick(s);
        s.Events.Add(new BattleEvent(BattleEventKind.FieldTick, -1, default, s.Round));
    }

    // --- the control point ------------------------------------------------------------

    /// <summary>Who holds the point with <paramref name="green"/> and
    /// <paramref name="brown"/> active units on it: more is holding, a tie -
    /// none on it too - is nobody (match.md, «Удержание точки»). The bench asks
    /// this too.</summary>
    public static Camp? Holding(int green, int brown) =>
        green > brown ? Camp.Green : brown > green ? Camp.Brown : null;

    /// <summary>Who holds the point now: the active units on its hexes, the
    /// knocked out and drowned not counted.</summary>
    public static Camp? HeldBy(BattleState s)
    {
        if (s.Point is null)
            return null;
        int green = 0, brown = 0;
        foreach (BattleUnit u in s.Units)
            if (u.Fate == UnitFate.Alive && Array.IndexOf(s.Point, u.Cell) >= 0)
            {
                if (u.Side == Camp.Green)
                    green++;
                else
                    brown++;
            }
        return Holding(green, brown);
    }

    /// <summary>The point at the round's end (match.md): the side that does
    /// not hold it loses a point, both when nobody does; the holding changing
    /// hands puts the stalemate's clock back.</summary>
    private static void PointTick(BattleState s)
    {
        if (s.Point is null)
            return;
        Camp? held = HeldBy(s);
        if (held != s.Holder)
        {
            s.Holder = held;
            s.LastShift = s.Round;
        }
        if (held != Camp.Green)
            s.Score[(int)Camp.Green]--;
        if (held != Camp.Brown)
            s.Score[(int)Camp.Brown]--;
        s.Events.Add(new BattleEvent(BattleEventKind.Held, -1, s.Point[0], held is Camp h ? (int)h : -1,
            $"green {s.Score[(int)Camp.Green]}, brown {s.Score[(int)Camp.Brown]}"));
    }

    // --- fates and fires --------------------------------------------------------------

    /// <summary>Knocked out (states.md, «Подбит»); in deep water drowned
    /// («Утоплен»). Its fire goes with its running.</summary>
    private static void Knock(BattleState s, BattleUnit u)
    {
        if (u.Fate != UnitFate.Alive)
            return;
        u.Burning = false;
        bool deep = s.Board.IsDeep(u.Cell) && s.Board.RampHeading(u.Cell) < 0;
        u.Fate = deep ? UnitFate.Drowned : UnitFate.Knocked;
        s.Harmed(u);
        s.Events.Add(new BattleEvent(deep ? BattleEventKind.Drowned : BattleEventKind.Knocked, u.Id, u.Cell));
        Lift(s, u, "out of the fight");
        Over(s);
    }

    /// <summary>
    /// Destroyed - always a blast (states.md, «Уничтожен»): the six hexes round
    /// it on its level - a unit fighting there catches fire (not in water), one
    /// out goes up in turn, a wood catches - and the walls on its own hex come
    /// down; its own wood catches too.
    /// </summary>
    private static void Explode(BattleState s, BattleUnit u)
    {
        if (u.Fate == UnitFate.Destroyed)
            return;
        u.Fate = UnitFate.Destroyed;
        s.Harmed(u);
        u.Burning = false;
        s.Events.Add(new BattleEvent(BattleEventKind.Exploded, u.Id, u.Cell));
        Lift(s, u, "destroyed");
        WallDownAll(s, u.Cell);
        if (s.Board.CoverAt(u.Cell) == Cover.Forest)
            WoodAlight(s, u.Cell);
        int level = s.Board.LevelAt(u.Cell);
        foreach (int h in HexField.EdgeHeadings)
        {
            Vector2I n = HexField.Step(u.Cell, h);
            if (!s.Board.InBounds(n) || s.Board.LevelAt(n) != level)
                continue;
            if (s.At(n) is { } near)
            {
                if (near.Out)
                    Explode(s, near);
                // the spall liner keeps a living hull from the blast's fire
                // (upgrades.md); a knocked one goes up in the chain as any
                else if (near.Fate == UnitFate.Alive && (near.Kit & Kit.SpallLiner) == 0)
                    Ignite(s, near);
            }
            if (s.Board.CoverAt(n) == Cover.Forest)
                WoodAlight(s, n);
        }
        Over(s);
    }

    private static void Ignite(BattleState s, BattleUnit u, string why = "")
    {
        if (u.Fate != UnitFate.Alive || u.Burning || s.Board.IsWater(u.Cell))
            return;
        u.Burning = true;
        s.Harmed(u);
        u.BurnSince = s.TurnNo;
        s.Events.Add(new BattleEvent(BattleEventKind.Ignited, u.Id, u.Cell, 0, why));
    }

    private static void Douse(BattleState s, BattleUnit u)
    {
        if (!u.Burning)
            return;
        u.Burning = false;
        s.Events.Add(new BattleEvent(BattleEventKind.Doused, u.Id, u.Cell));
    }

    /// <summary>A wood catches (not a burnt one, not one burning already), and
    /// whoever stands in it.</summary>
    private static void WoodAlight(BattleState s, Vector2I c, bool fresh = false)
    {
        if (s.Board.CoverAt(c) != Cover.Forest || s.Board.CoverStateAt(c) != CoverState.Intact)
            return;
        s.Board.SetCoverState(c, CoverState.Burning);
        s.Fires[c] = FireRounds;
        s.Events.Add(new BattleEvent(BattleEventKind.WoodAlight, -1, c, fresh ? 1 : 0));
        if (s.At(c) is { } u)
            Ignite(s, u);
    }

    /// <summary>The wall across one edge down, from both sides of it.</summary>
    private static void WallDown(BattleState s, Vector2I cell, int heading)
    {
        Vector2I other = HexField.Step(cell, heading);
        int back = HexField.Reverse(heading);
        bool any = false;
        if (BoardRules.SideStands(s.Board, cell, heading))
        {
            s.Board.SetSides(cell, s.Board.SidesAt(cell) & ~(1 << HexField.EdgeIndex(heading)));
            any = true;
        }
        if (BoardRules.SideStands(s.Board, other, back))
        {
            s.Board.SetSides(other, s.Board.SidesAt(other) & ~(1 << HexField.EdgeIndex(back)));
            any = true;
        }
        if (any)
            s.Events.Add(new BattleEvent(BattleEventKind.WallDown, -1, cell, heading));
    }

    private static void WallDownAll(BattleState s, Vector2I cell)
    {
        foreach (int h in HexField.EdgeHeadings)
            if (s.Board.Walled(cell, h))
                WallDown(s, cell, h);
    }

    /// <summary>A side with no unit able to fight has lost (GameModes.md,
    /// «Общее для всех режимов», 2026-11-02) - in a match on the point too;
    /// with a point, a side whose points are spent has lost, and both spent on
    /// one tick is a draw (the GDD does not say; docs/battle-core-plan.md, Д9).</summary>
    private static void Over(BattleState s)
    {
        if (s.Over)
            return;
        Beaten(s);
        if (s.Over || s.Point is null)
            return;
        bool green = s.Score[(int)Camp.Green] <= 0, brown = s.Score[(int)Camp.Brown] <= 0;
        if (green && brown)
        {
            s.Drawn = true;
            s.Events.Add(new BattleEvent(BattleEventKind.Drawn, -1, default, 0, "both out of points"));
        }
        else if (green || brown)
        {
            s.Won = green ? Camp.Brown : Camp.Green;
            s.Events.Add(new BattleEvent(BattleEventKind.Won, -1, default, (int)s.Won.Value, "on points"));
        }
    }

    /// <summary>
    /// A side with no active unit - every one knocked out, drowned or
    /// destroyed - has lost (GameModes.md, «Сторона без активных машин
    /// проигрывает в любом режиме», 2026-11-02; until then it took every unit
    /// destroyed). A unit alight is still active: its activation may put the
    /// fire out. Both sides at once - one blast taking the last of each - is a
    /// draw: the GDD does not say (undefined.md).
    /// </summary>
    private static void Beaten(BattleState s)
    {
        bool green = Fights(s, Camp.Green), brown = Fights(s, Camp.Brown);
        if (green && brown)
            return;
        if (!green && !brown)
        {
            s.Drawn = true;
            s.Events.Add(new BattleEvent(BattleEventKind.Drawn, -1, default, 0, "nobody able to fight"));
            return;
        }
        s.Won = green ? Camp.Green : Camp.Brown;
        s.Events.Add(new BattleEvent(BattleEventKind.Won, -1, default, (int)s.Won.Value));
    }

    /// <summary><paramref name="side"/> has a unit able to fight - or no units
    /// at all, a board set up without it (the self test's).</summary>
    private static bool Fights(BattleState s, Camp side)
    {
        bool any = false;
        foreach (BattleUnit u in s.Units)
            if (u.Side == side)
            {
                if (u.Fate == UnitFate.Alive)
                    return true;
                any = true;
            }
        return !any;
    }
}
