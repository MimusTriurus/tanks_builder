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
/// Not yet: the risk-wise way of equal cost, the smoke, mines, the winch,
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
            gen.Moves(s0, head, attacked: false);
            n += gen.Count;
        }
        return n;
    }

    /// <summary>The match copied for a play, without its log of events: the
    /// rules write it and never read it back.</summary>
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
                        head.Add(new Order(OrderKind.Go, cell, face == route.End.Heading ? -1 : face));
                        After(s1, head, attacked);
                        head.RemoveAt(head.Count - 1);
                    }
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
            }
            Bonus(s, head);
        }

        /// <summary>The bonus act (owned state), then the end of the turn.</summary>
        private void Bonus(BattleState s, List<Order> head)
        {
            BattleUnit u = s.Unit(_id)!;
            if (u.Fate == UnitFate.Alive && !s.Over && Battle.BonusLeft(s, u))
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
    public static bool LaneOpen(BattleState s, BattleUnit u, Vector2I aim)
    {
        if (u.Class.Lobs)
            return true;
        bool passes = u.Class.Might >= 4 && !u.Class.Turreted;
        Shot shot = Gunnery.Solve(s.Board, u.Cell, aim, false, c => s.At(c) is not null, masonry: false,
                                          standing: c => s.At(c) is { } t ? Battle.Level(s, t) : s.Board.LevelAt(c),
                                          thermal: (u.Kit & Kit.Thermal) != 0, passes: passes ? 1 : 0,
                                          passable: c => s.At(c) is not null, woodPass: (u.Kit & Kit.ThroughWood) != 0);
        return shot.Clear || shot.BlockedAt is null;
    }

    /// <summary>The attacks after the move: the shots, and the ambush toward
    /// the nearest enemy - a casemate along its hull, the medium a sector of
    /// two (three with the wide sector) about that side.</summary>
    private static List<Order> Attacks(BattleState s, BattleUnit u)
    {
        List<Order> all = Shots(s, u);
        int to = Toward(s, u, u.Cell);
        if (to < 0)
            return all;
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

    /// <summary>The bonus acts worth trying: the fire put out, a ram at each
    /// enemy beside, the repair kit at each own unit beside out of the fight.</summary>
    private static List<Order> Bonuses(BattleState s, BattleUnit u)
    {
        var acts = new List<Order>();
        if (u.Burning)
            acts.Add(new Order(OrderKind.Douse));
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
