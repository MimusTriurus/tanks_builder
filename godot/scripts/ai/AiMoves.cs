using System;
using System.Collections.Generic;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// One turn of one unit, as the AI gives it (docs/ai-plan.md, «Ход как
/// макро-приказ»): the core's orders from the take to the end of the turn,
/// in the board's cells - the last a <c>pass</c> only when the turn would
/// not go by itself (<see cref="Battle.TurnDone"/>), as on the bench.
/// </summary>
public sealed class AiPlay
{
    public int Unit;
    public readonly List<Order> Orders = new();
    public double Score;

    public AiPlay(int unit, IEnumerable<Order> orders)
    {
        Unit = unit;
        Orders.AddRange(orders);
    }

    /// <summary>In <c>--commands</c>' words, in the map's cells
    /// (<paramref name="shift"/> the sea's, <see cref="BattleState.Shift"/>).</summary>
    public string Say(Vector2I shift)
    {
        var parts = new List<string>(Orders.Count);
        foreach (Order o in Orders)
            parts.Add(o.Moved(-shift).ToString());
        return string.Join(";", parts);
    }

    public override string ToString() => Say(Vector2I.Zero);
}

/// <summary>
/// The plays a side has (docs/ai-plan.md, «Ход как макро-приказ», И1), each
/// tried on a copy of the match by the rules themselves (<see cref="Battle"/>)
/// and handed with the match it leaves to a visitor. Per unit: the route plan
/// once (<see cref="Steps.Plan"/>) and the move along the planner's own
/// straightest way - the one the bench's <c>go</c> drives, so a play the AI
/// picks is the play the bench shows; the end facing (all the sides a
/// casemate may take, else as it arrives or to the nearest enemy); the
/// attack - a shot or an incendiary at each enemy on an axis in range, or an
/// ambush toward the nearest enemy; the bonus act - putting a fire out,
/// ramming an enemy beside, the repair kit on an own unit out of the fight.
/// LT may shoot first and move after. Doing nothing is always a play.
/// Not yet: the risk-wise way of equal cost, mines, the winch,
/// the double turn, the forced engine, the shot on the move, the wall's ram.
/// </summary>
public static class AiMoves
{
    public delegate void Visit(AiPlay play, BattleState after);

    /// <summary>Every play of the side to move on <paramref name="root"/>,
    /// which is left as it is.</summary>
    public static int Each(BattleState root, Visit visit)
    {
        int n = 0;
        if (root.Over)
            return 0;
        foreach (BattleUnit u in root.Units)
        {
            if (u.Side != root.Turn || u.Fate != UnitFate.Alive)
                continue;
            BattleState s0 = Copy(root);
            if (Battle.Take(s0, u.Id) is not null)
                continue;
            var head = new List<Order> { new(OrderKind.Take, Unit: u.Id) };
            var gen = new Gen(u.Id, visit);
            // LT's free order: the shot first, then the move
            if (u.Class.FreeOrder && !s0.Fired)
                foreach (Order a in Shots(s0, s0.Unit(u.Id)!))
                {
                    BattleState s1 = Copy(s0);
                    if (Battle.Apply(s1, a) is not null)
                        continue;
                    head.Add(a);
                    gen.Moves(s1, head, attacked: true);
                    head.RemoveAt(head.Count - 1);
                }
            // the forced engine, said before the move, where an enemy watches an axis
            if ((u.Kit & Kit.Overdrive) != 0 && EnemyWatching(root, u.Side))
            {
                BattleState so = Copy(s0);
                if (Battle.Overdrive(so) is null)
                {
                    head.Add(new Order(OrderKind.Overdrive));
                    gen.Moves(so, head, attacked: false);
                    head.RemoveAt(head.Count - 1);
                }
            }
            gen.Moves(s0, head, attacked: false);
            n += gen.Count;
        }
        return n;
    }

    /// <summary>The match copied for a play, without its log of events: the
    /// rules write it and never read it back.</summary>
    /// <summary>The tide (trial, <see cref="BattleState.Tide"/>) rises at this
    /// round's end unless a tank is hit first, and <paramref name="u"/>
    /// stands on the ring it floods without the snorkel.</summary>
    public static bool Drowns(BattleState s, BattleUnit u) =>
        s.Tide && (u.Kit & Kit.Snorkel) == 0 && !s.Board.IsDeep(u.Cell)
        && s.TideQuiet + 1 >= s.TideStep && s.Ring(u.Cell) == s.TideLevel + 1;

    public static BattleState Copy(BattleState s)
    {
        List<BattleEvent> events = s.Events;
        s.Events = new List<BattleEvent>(0);
        BattleState c = s.Clone();
        s.Events = events;
        return c;
    }

    private sealed class Gen
    {
        private readonly int _id;
        private readonly Visit _visit;
        public int Count;

        public Gen(int id, Visit visit)
        {
            _id = id;
            _visit = visit;
        }

        /// <summary>Every move from <paramref name="s"/> (owned: used up), then what follows it.</summary>
        public void Moves(BattleState s, List<Order> head, bool attacked)
        {
            BattleUnit u = s.Unit(_id)!;
            if (!s.Drove && !u.Pinned && u.Fate == UnitFate.Alive && !s.Over)
            {
                Steps.Reach reach = Steps.Plan(s.Board, new Steps.Pose(u.Cell, u.Heading), Battle.MoverOf(s, u));
                foreach (Vector2I cell in reach.Cells.Keys)
                {
                    if (reach.RouteTo(cell) is not { } route)
                        continue;
                    foreach (int face in Faces(s, u, route, reach))
                    {
                        BattleState s1 = Copy(s);
                        if (Battle.Move(s1, route.Cells(), face, route.Back) is not null)
                            continue;
                        // no drive that takes its own hull out - into deep water,
                        // on to a mine: the threat map counts a hull under several
                        // guns once per gun, so drowning the most exposed one
                        // scored above keeping it - on duel12 nearly every match
                        // lost a tank to the sea by round 3 (2026-11-06)
                        if (s1.Unit(_id)!.Fate != UnitFate.Alive)
                            continue;
                        // nor on to the ring the tide (a trial) floods at this
                        // round's end: the hull gets no activation to leave
                        if (Drowns(s1, s1.Unit(_id)!))
                            continue;
                        head.Add(new Order(OrderKind.Go, cell, face == route.End.Heading ? -1 : face));
                        After(s1, head, attacked);
                        head.RemoveAt(head.Count - 1);
                    }
                    if (!attacked && !s.Fired && (u.Kit & Kit.FireOnMove) != 0)
                        OnTheWay(s, u, route, cell, head);
                }
                // a turn on the spot: a casemate to any side, a turret to the enemy
                foreach (int face in HexField.EdgeHeadings)
                {
                    if (face == u.Heading || (u.Class.Turreted && face != Toward(s, u, u.Cell)))
                        continue;
                    BattleState s1 = Copy(s);
                    if (Battle.Turn(s1, face) is not null)
                        continue;
                    head.Add(new Order(OrderKind.Turn, Side: face));
                    After(s1, head, attacked);
                    head.RemoveAt(head.Count - 1);
                }
            }
            After(s, head, attacked);
        }

        /// <summary>
        /// The shot on the move (upgrades.md, «Выстрел на ходу»): for each
        /// enemy, the first hex of the route it can be shot from - said before
        /// the move, the move as the bench's <c>go</c> drives it.
        /// </summary>
        private void OnTheWay(BattleState s, BattleUnit u, Steps.Route route, Vector2I end, List<Order> head)
        {
            List<Vector2I> cells = route.Cells();
            int lobReach = Gunnery.LobReachOf(u.Kit);
            foreach (BattleUnit e in s.Units)
            {
                if (e.Side == u.Side || !e.Standing)
                    continue;
                foreach (Vector2I at in cells)
                {
                    if (at == u.Cell || !HexField.OnAxis(at, e.Cell))
                        continue;
                    (int heading, int range) = s.Board.LaneTo(at, e.Cell);
                    if (heading < 0 || (!u.Class.Turreted && heading != u.Heading)
                        || Gunnery.MayTarget(u.Class.Lobs, range, true, lobReach) != Gunnery.Distance.Fits
                        || !LaneOpen(s, u, e.Cell, at))
                        continue;
                    BattleState s1 = Copy(s);
                    if (Battle.OnTheWay(s1, at, e.Cell) is not null
                        || Battle.Move(s1, cells, route.End.Heading, route.Back) is not null)
                        break;
                    head.Add(new Order(OrderKind.OnWay, at, Aim: e.Cell));
                    head.Add(new Order(OrderKind.Go, end));
                    After(s1, head, attacked: true);
                    head.RemoveRange(head.Count - 2, 2);
                    break;
                }
            }
        }

        /// <summary>The attack after the move (owned state).</summary>
        private void After(BattleState s, List<Order> head, bool attacked)
        {
            BattleUnit u = s.Unit(_id)!;
            if (!attacked && !s.Fired && u.Fate == UnitFate.Alive && !s.Over)
            {
                foreach (Order a in Attacks(s, u))
                {
                    BattleState s1 = Copy(s);
                    if (Battle.Apply(s1, a) is not null)
                        continue;
                    head.Add(a);
                    Bonus(s1, head);
                    head.RemoveAt(head.Count - 1);
                }
                // The acts the turn's end does not wait for (Battle.TurnDone
                // waits only for a fire, a hull beside, the double turn): once
                // the shot is fired the turn goes by itself on the bench, so
                // the smoke, the mine, the winch and the wall's ram come
                // before the attack.
                if (Battle.BonusLeft(s, u))
                    foreach (Order b in Bonuses(s, u))
                    {
                        if (b.Kind is not (OrderKind.Smoke or OrderKind.Mine or OrderKind.Winch or OrderKind.RamWall))
                            continue;
                        BattleState s1 = Copy(s);
                        if (Battle.Apply(s1, b) is not null)
                            continue;
                        head.Add(b);
                        BattleUnit u1 = s1.Unit(_id)!;
                        if (u1.Fate == UnitFate.Alive && !s1.Over && !s1.Fired)
                            foreach (Order a in Attacks(s1, u1))
                            {
                                BattleState s2 = Copy(s1);
                                if (Battle.Apply(s2, a) is not null)
                                    continue;
                                head.Add(a);
                                Finish(s2, head);
                                head.RemoveAt(head.Count - 1);
                            }
                        // without the attack it is one of Bonus' plays below
                        head.RemoveAt(head.Count - 1);
                    }
            }
            Bonus(s, head);
        }

        /// <summary>The bonus act (owned state) - only while the turn has not
        /// gone by itself (<see cref="Battle.TurnDone"/>) - then the end of the turn.</summary>
        private void Bonus(BattleState s, List<Order> head)
        {
            BattleUnit u = s.Unit(_id)!;
            if (u.Fate == UnitFate.Alive && !s.Over && Battle.BonusLeft(s, u) && !Battle.TurnDone(s))
                foreach (Order b in Bonuses(s, u))
                {
                    BattleState s1 = Copy(s);
                    if (Battle.Apply(s1, b) is not null)
                        continue;
                    head.Add(b);
                    Finish(s1, head);
                    head.RemoveAt(head.Count - 1);
                }
            Finish(s, head);
        }

        /// <summary>The turn handed over - by itself if nothing is left in it,
        /// else by a pass - and the play visited.</summary>
        private void Finish(BattleState s, List<Order> head)
        {
            var play = new AiPlay(_id, head);
            if (!s.Over)
            {
                if (!Battle.TurnDone(s))
                    play.Orders.Add(new Order(OrderKind.Pass));
                Battle.Pass(s);
            }
            Count++;
            _visit(play, s);
        }
    }

    /// <summary>The sides worth ending a route on: as it arrives; a casemate
    /// any it may turn to (its gun is its hull); a turret the one to the
    /// nearest enemy.</summary>
    private static IEnumerable<int> Faces(BattleState s, BattleUnit u, Steps.Route route, Steps.Reach reach)
    {
        int arrive = route.End.Heading;
        yield return arrive;
        if (u.Class.Turreted)
        {
            int to = Toward(s, u, route.End.Cell);
            if (to >= 0 && to != arrive && reach.TurnTo(route, to) is not null)
                yield return to;
            yield break;
        }
        foreach (int h in HexField.EdgeHeadings)
            if (h != arrive && reach.TurnTo(route, h) is not null)
                yield return h;
    }

    /// <summary>The side of <paramref name="from"/> that leads nearest the
    /// nearest enemy of <paramref name="u"/>, or -1 with none.</summary>
    public static int Toward(BattleState s, BattleUnit u, Vector2I from)
    {
        BattleUnit? best = AiEval.NearestEnemy(s, u, from);
        if (best is null)
            return -1;
        int side = -1, near = int.MaxValue;
        foreach (int h in HexField.EdgeHeadings)
        {
            int here = HexField.Distance(HexField.Step(from, h), best.Cell);
            if (here < near)
            {
                near = here;
                side = h;
            }
        }
        return side;
    }

    /// <summary>The shots <paramref name="u"/> has from where it stands: at
    /// each enemy hull on an axis in range (a casemate's along its hull), the
    /// incendiary as well with the kit for it. The own out of the fight are
    /// never aimed at (meta.md, «ИИ противника и мета»).</summary>
    public static List<Order> Shots(BattleState s, BattleUnit u)
    {
        var shots = new List<Order>();
        int reach = Gunnery.LobReachOf(u.Kit);
        foreach (BattleUnit e in s.Units)
        {
            if (e.Side == u.Side || !e.Standing)
                continue;
            (int heading, int range) = s.Board.LaneTo(u.Cell, e.Cell);
            if (heading < 0 || (!u.Class.Turreted && heading != u.Heading))
                continue;
            if (Gunnery.MayTarget(u.Class.Lobs, range, true, reach) != Gunnery.Distance.Fits)
                continue;
            if (!LaneOpen(s, u, e.Cell))
                continue;
            shots.Add(new Order(OrderKind.Fire, e.Cell));
            if ((u.Kit & Kit.Incendiary) != 0)
                shots.Add(new Order(OrderKind.Burn, e.Cell));
        }
        return shots;
    }

    /// <summary>The bench's gate on a direct shot (Tank3DBench.Orders,
    /// <c>LaneShut</c>): the lane to the target open - nothing on it short of
    /// the target, a destroyer's one pass through a hull let by. The core
    /// would let the round fly and be spent; the bench refuses the order, so
    /// the AI never gives it. The mortar goes over everything.</summary>
    public static bool LaneOpen(BattleState s, BattleUnit u, Vector2I aim, Vector2I? from = null)
    {
        if (u.Class.Lobs)
            return true;
        bool passes = u.Class.Might >= 4 && !u.Class.Turreted;
        Shot shot = Gunnery.Solve(s.Board, from ?? u.Cell, aim, false, c => s.At(c) is not null, masonry: false,
                                          standing: c => s.At(c) is { } t ? Battle.Level(s, t) : s.Board.LevelAt(c),
                                          thermal: (u.Kit & Kit.Thermal) != 0, passes: passes ? 1 : 0,
                                          passable: c => s.At(c) is not null, woodPass: (u.Kit & Kit.ThroughWood) != 0);
        return shot.Clear || shot.BlockedAt is null;
    }

    /// <summary>The incendiary into a wood beside an enemy standing in a
    /// wood (docs/ai-plan.md, И4): the round's end carries the fire on to
    /// the enemy's hex (field.md, «огонь\пожар»), where no round of the
    /// gun's gets - a wood or the hull's front in the way. The wood aimed at
    /// is the lane's last cell, so it does not shut it.</summary>
    private static void Kindling(BattleState s, BattleUnit u, List<Order> all)
    {
        int reach = Gunnery.LobReachOf(u.Kit);
        foreach (BattleUnit e in s.Units)
        {
            if (e.Side == u.Side || e.Fate != UnitFate.Alive || !Wood(s, e.Cell))
                continue;
            foreach (int h in HexField.EdgeHeadings)
            {
                Vector2I w = HexField.Step(e.Cell, h);
                if (w == e.Cell || !s.Board.InBounds(w) || !Wood(s, w) || s.At(w) is not null
                    || s.Board.Walled(e.Cell, h))
                    continue;
                (int heading, int range) = s.Board.LaneTo(u.Cell, w);
                if (heading < 0 || (!u.Class.Turreted && heading != u.Heading)
                    || Gunnery.MayTarget(u.Class.Lobs, range, false, reach) != Gunnery.Distance.Fits
                    || !LaneOpen(s, u, w))
                    continue;
                var burn = new Order(OrderKind.Burn, w);
                if (!all.Contains(burn))
                    all.Add(burn);
            }
        }
    }

    private static bool Wood(BattleState s, Vector2I c) =>
        s.Board.CoverAt(c) == Cover.Forest && s.Board.CoverStateAt(c) == CoverState.Intact && !s.Fires.ContainsKey(c);

    /// <summary>The attacks after the move: the shots, and the ambush toward
    /// the nearest enemy - a casemate along its hull, the medium a sector of
    /// two (three with the wide sector) about that side.</summary>
    private static List<Order> Attacks(BattleState s, BattleUnit u)
    {
        List<Order> all = Shots(s, u);
        Glances(s, u, all);
        if ((u.Kit & Kit.Incendiary) != 0)
            Kindling(s, u, all);
        // the mortar's smoke bomb (upgrades.md, «Дымовой снаряд»): over
        // everything on to any empty open hex of its band along the hull -
        // 3 to 5, 3 to 6 with the bracket. It went the gun's way below (one to
        // three out, stopped by the first tank) and never found a hex: 0 rounds
        // in 36 matches with the upgrade (2026-11-06, --budget 15)
        if ((u.Kit & Kit.SmokeShell) != 0 && u.Class.Lobs)
        {
            int reach = Gunnery.LobReachOf(u.Kit);
            Vector2I c = u.Cell;
            for (int k = 1; k <= reach; k++)
            {
                c = HexField.Step(c, u.Heading);
                if (!s.Board.InBounds(c))
                    break;
                if (k >= Gunnery.LobShortest && s.At(c) is null && s.Board.CoverAt(c) == Cover.None && !s.Board.Smoked(c))
                    all.Add(new Order(OrderKind.SmokeShot, c));
            }
        }
        int to = Toward(s, u, u.Cell);
        if (to < 0)
            return all;
        // the smoke round on the first empty hex toward the enemy (upgrades.md, «Дымовой снаряд»)
        if ((u.Kit & Kit.SmokeShell) != 0 && !u.Class.Lobs && (u.Class.Turreted || to == u.Heading))
        {
            Vector2I c = u.Cell;
            for (int k = 1; k <= 3; k++)
            {
                c = HexField.Step(c, to);
                if (!s.Board.InBounds(c) || s.At(c) is not null)
                    break;
                if (s.Board.CoverAt(c) == Cover.None && !s.Board.Smoked(c)
                    && Gunnery.MayTarget(u.Class.Lobs, k, false, Gunnery.LobReachOf(u.Kit)) == Gunnery.Distance.Fits)
                {
                    all.Add(new Order(OrderKind.SmokeShot, c));
                    break;
                }
            }
        }
        if (!u.Class.Turreted)
            all.Add(new Order(OrderKind.Ambush, Side: u.Heading));
        else if (u.Class.WatchesSector)
        {
            int left = (to + 60) % 360, right = (to + 300) % 360;
            if ((u.Kit & Kit.WideSector) != 0)
                all.Add(new Order(OrderKind.Ambush, Side: to, Side2: left, Side3: right));
            all.Add(new Order(OrderKind.Ambush, Side: to, Side2: left));
            all.Add(new Order(OrderKind.Ambush, Side: to, Side2: right));
        }
        else
            all.Add(new Order(OrderKind.Ambush, Side: to));
        return all;
    }

    /// <summary>
    /// The shot along an axis whose first hull is one of the unit's own able
    /// to fight, a side to it that holds: the round glances off it on to
    /// what lies past (units.md, «Рикошет»; the playtest PT-4). The rules let
    /// the round go at any hex of the axis; the bench too. Never at an own
    /// hull out of the fight (meta.md). The bench fires on the own hull as
    /// «Атака» does (<c>Tank3DBench.Execute</c>, the fire command).
    /// </summary>
    private static void Glances(BattleState s, BattleUnit u, List<Order> all)
    {
        if (u.Class.Lobs)
            return;
        foreach (int h in HexField.EdgeHeadings)
        {
            if (!u.Class.Turreted && h != u.Heading)
                continue;
            Vector2I c = u.Cell;
            for (int k = 1; k < 32; k++)
            {
                c = HexField.Step(c, h);
                if (!s.Board.InBounds(c))
                    break;
                if (s.At(c) is not { } first)
                    continue;
                if (first.Side == u.Side && first.Fate == UnitFate.Alive
                    && Gunnery.MayTarget(false, k, true) == Gunnery.Distance.Fits
                    && Gunnery.Ricochets(Battle.PlateOf(h, first.Heading, false))
                    && Gunnery.Penetration(u.Class.Might, first.Class, Gunnery.Plate.Side) == 0
                    && LaneOpen(s, u, c) && Takes(s, u, h, c))
                    all.Add(new Order(OrderKind.Fire, c));
                break;
            }
        }
    }

    /// <summary>The round along <paramref name="heading"/> harms an enemy
    /// (<see cref="Battle.Probe"/>): a glance worth giving, not one that only
    /// puts the stalemate's clock back.</summary>
    private static bool Takes(BattleState s, BattleUnit u, int heading, Vector2I aim)
    {
        var marks = new int[s.Units.Count];
        Battle.Probe(s, u, u.Cell, heading, aim, marks);
        for (int i = 0; i < marks.Length; i++)
            if (marks[i] != Battle.ProbeNone && s.Units[i].Side != u.Side)
                return true;
        return false;
    }

    /// <summary>An enemy unit is watching an axis.</summary>
    private static bool EnemyWatching(BattleState s, Camp side)
    {
        foreach (BattleUnit o in s.Units)
            if (o.Side != side && o.Ambushing && o.Fate == UnitFate.Alive)
                return true;
        return false;
    }

    /// <summary>Whether <paramref name="c"/> lies on an enemy gun's axis
    /// strictly between it and an active unit of <paramref name="side"/>.</summary>
    private static bool Screens(BattleState s, Camp side, Vector2I c)
    {
        foreach (BattleUnit e in s.Units)
        {
            if (e.Side == side || e.Fate != UnitFate.Alive || e.Class.Lobs)
                continue;
            (int toC, int atC) = s.Board.LaneTo(e.Cell, c);
            if (toC < 0)
                continue;
            foreach (BattleUnit o in s.Units)
                if (o.Side == side && o.Fate == UnitFate.Alive
                    && s.Board.LaneTo(e.Cell, o.Cell) is var (toO, atO) && toO == toC && atO > atC)
                    return true;
        }
        return false;
    }

    /// <summary>
    /// The bonus acts worth trying: the fire put out; a ram at each enemy
    /// beside; the repair kit at each own unit beside out of the fight; the
    /// smoke grenade and the mine layer on the hex toward the nearest enemy,
    /// the grenade also on each hex beside that screens an own unit from an
    /// enemy gun (<see cref="Screens"/>);
    /// the winch at each hull two out; the double turn either way; the ram of
    /// the wall ahead.
    /// </summary>
    private static List<Order> Bonuses(BattleState s, BattleUnit u)
    {
        var acts = new List<Order>();
        if (u.Burning)
            acts.Add(new Order(OrderKind.Douse));
        int to = Toward(s, u, u.Cell);
        if (to >= 0)
        {
            Vector2I near = HexField.Step(u.Cell, to);
            if ((u.Kit & Kit.Smoke) != 0)
                acts.Add(new Order(OrderKind.Smoke, near));
            if ((u.Kit & Kit.MineLayer) != 0)
                acts.Add(new Order(OrderKind.Mine, near));
        }
        // the screen: smoke on a hex beside that cuts an enemy gun's axis to
        // an own unit (docs/ai-plan.md, И4, «дым на ось к своему HM»)
        if ((u.Kit & Kit.Smoke) != 0)
            foreach (int h in HexField.EdgeHeadings)
            {
                Vector2I c = HexField.Step(u.Cell, h);
                // the hex toward the enemy is tried above already
                if (c != u.Cell && !(to >= 0 && c == HexField.Step(u.Cell, to)) && Screens(s, u.Side, c))
                    acts.Add(new Order(OrderKind.Smoke, c));
            }
        if ((u.Kit & Kit.Winch) != 0)
            foreach (int h in HexField.EdgeHeadings)
            {
                Vector2I two = HexField.Step(HexField.Step(u.Cell, h), h);
                if (s.At(two) is not null)
                    acts.Add(new Order(OrderKind.Winch, two));
            }
        if ((u.Kit & Kit.DoubleTurn) != 0)
        {
            acts.Add(new Order(OrderKind.Pivot, Side: (u.Heading + 60) % 360));
            acts.Add(new Order(OrderKind.Pivot, Side: (u.Heading + 300) % 360));
        }
        if (s.Board.Walled(u.Cell, u.Heading))
            acts.Add(new Order(OrderKind.RamWall, Side: u.Heading));
        foreach (int h in HexField.EdgeHeadings)
        {
            Vector2I c = HexField.Step(u.Cell, h);
            if (s.At(c) is not { } o)
                continue;
            if (o.Side != u.Side && o.Fate == UnitFate.Alive)
                acts.Add(new Order(OrderKind.Ram, c));
            else if (o.Side == u.Side && o.Out && (u.Kit & Kit.RepairKit) != 0)
                acts.Add(new Order(OrderKind.Repair, c));
        }
        return acts;
    }
}
