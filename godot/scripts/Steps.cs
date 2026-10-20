using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// A turn's driving by the GDD's steps (match.md, «Структура хода»; field.md,
/// «Покрытия» and «Препятствия»): where a tank can get to this turn, what each
/// way there costs, and every way of equal cost - what the HUD shows before an
/// order and what the order then drives (docs/hud3d-plan.md, stage 1).
///
/// <b>The state is a pose, a cell and the way the hull points</b>, because a
/// turn of sixty degrees costs a step like a cell does: the same cell reached
/// facing two ways is two answers, and the cheaper way in may leave the hull
/// pointing wrong. So the search is over poses, by cost (Dijkstra), and keeps
/// every predecessor of equal cost - the player picks among equal ways, since
/// the cells on the way decide the mine, the fire and the ambush.
///
/// What a step costs:
/// <list type="bullet">
/// <item>a cell forward - 1, into sand 2 (U-21); a turn of 60° on the spot - 1;</item>
/// <item>a cell back - 1 (2 into sand), and only as the whole move: one cell, no
/// turn before or after (match.md «назад на 1, не сочетается с поворотом»;
/// field.md, the ramp: «назад можно на одну клетку»);</item>
/// <item>mud and wood end the move on entry; a turn begun in either has one step
/// (the budget is 1);</item>
/// <item>a mine fires on entry and the move ends there (a trawl driving forward
/// passes it); deep water without the snorkel drowns the tank, and that ends
/// it too; a burning wood sets the tank alight - a <see cref="Risk"/> on the
/// way, the path itself goes on (it is a wood, so it stops anyway);</item>
/// <item>a ramp is crossed along its axis, never stopped on by choice and never
/// turned on: a path with one step left does not go on to one;</item>
/// <item>the board's own rule for a step (<see cref="HexField.Passable"/>:
/// rock, cliffs, the ramp's axis, the bank of a pond) and the masonry
/// (<see cref="HexField.Blocked"/>), which the heavy crosses at a step's cost
/// (classes.md, «Бульдозер HT»).</item>
/// </list>
///
/// Not yet: the road's extra step (there is no road floor), the ambush axes
/// (no ambush on the 3D bench). Like every shared module here it knows nothing
/// of the scene that asks.
/// </summary>
public static class Steps
{
    /// <summary>A cell and the flat side the hull points at
    /// (<see cref="HexField.EdgeHeadings"/>).</summary>
    public readonly record struct Pose(Vector2I Cell, int Heading);

    public enum Act { Start, Forward, Back, Turn }

    /// <summary>What a way does to the tank on it, beyond what it costs.</summary>
    [Flags]
    public enum Risk { None = 0, Mine = 1, Fire = 2, Drown = 4 }

    /// <summary>Who drives: the budget and the licences.</summary>
    public sealed class Mover
    {
        /// <summary>Steps a turn - the class's speed (classes.md).</summary>
        public int Speed = 4;
        /// <summary>Masonry is a step, not a wall (HT).</summary>
        public bool Bulldozer;
        /// <summary>The snorkel: deep water is swum, not drowned in.</summary>
        public bool Amphibious;
        /// <summary>The mine trawl: a mine driven over forward is cleared.</summary>
        public bool Trawl;
        /// <summary>Cells no step may enter - the other tanks.</summary>
        public IReadOnlySet<Vector2I>? Barred;

        public static Mover Of(MovementProfile profile, bool amphibious = false,
                               IReadOnlySet<Vector2I>? barred = null) => new()
        {
            Speed = profile.Steps,
            Bulldozer = profile.Bulldozes,
            Amphibious = amphibious,
            Barred = barred,
        };
    }

    /// <summary>One step of a way: where it leaves the tank, how, and the steps
    /// spent by then.</summary>
    public readonly record struct Leg(Pose At, Act Act, int Spent);

    /// <summary>A way from the start to a cell, step by step.</summary>
    public sealed class Route
    {
        public required IReadOnlyList<Leg> Legs { get; init; }
        /// <summary>Nothing may follow it: it entered a cell that ends the
        /// move, or it was the step back.</summary>
        public required bool Final { get; init; }
        public required Risk Risk { get; init; }

        public Pose End => Legs[^1].At;
        public int Spent => Legs[^1].Spent;
        public bool Back => Legs.Any(l => l.Act == Act.Back);
        public int Turns => Legs.Count(l => l.Act == Act.Turn);

        /// <summary>The cells driven into, in order, the start not among them.</summary>
        public List<Vector2I> Cells() =>
            Legs.Where(l => l.Act is Act.Forward or Act.Back).Select(l => l.At.Cell).ToList();

        public override string ToString() =>
            string.Join(" ", Legs.Skip(1).Select(l => l.Act switch
            {
                Act.Turn => $"t{l.At.Heading}",
                Act.Back => $"b{l.At.Cell}",
                _ => $"{l.At.Cell}",
            })) + $" = {Spent}";
    }

    internal readonly record struct State(Pose Pose, bool Final);

    /// <summary>Everything a turn can do from one pose, searched once.</summary>
    public sealed class Reach
    {
        public required Pose Start { get; init; }
        public required int Budget { get; init; }
        internal readonly Dictionary<State, int> Cost = new();
        internal readonly Dictionary<State, List<State>> Pred = new();
        internal readonly Dictionary<State, Risk> Risks = new();
        internal HexField Field = null!;
        private Dictionary<Vector2I, int>? _cells;

        /// <summary>Every cell a move may end on, with the fewest steps that
        /// get there - the start not among them, a ramp only where a mine on it
        /// ended the move.</summary>
        public IReadOnlyDictionary<Vector2I, int> Cells
        {
            get
            {
                if (_cells is not null)
                    return _cells;
                _cells = new Dictionary<Vector2I, int>();
                foreach ((State s, int cost) in Cost)
                {
                    if (!Ends(s))
                        continue;
                    if (!_cells.TryGetValue(s.Pose.Cell, out int had) || cost < had)
                        _cells[s.Pose.Cell] = cost;
                }
                return _cells;
            }
        }

        public bool Reaches(Vector2I cell) => Cells.ContainsKey(cell);

        /// <summary>Steps left after the cheapest way to <paramref name="cell"/>,
        /// or -1 when it cannot be reached.</summary>
        public int Left(Vector2I cell) => Cells.TryGetValue(cell, out int c) ? Budget - c : -1;

        private bool Ends(State s) =>
            s.Pose.Cell != Start.Cell && (s.Final || !Field.IsRamp(s.Pose.Cell));

        /// <summary>
        /// Every way of the fewest steps to <paramref name="cell"/>, up to
        /// <paramref name="most"/>, straightest first (fewest turns, then
        /// forward before back). Two ways count as one only when they drive the
        /// same cells, turn the same way and end facing the same side.
        /// </summary>
        public List<Route> RoutesTo(Vector2I cell, int most = 8)
        {
            var routes = new List<Route>();
            if (!Cells.TryGetValue(cell, out int best))
                return routes;
            var seen = new HashSet<string>();
            foreach ((State end, int cost) in Cost)
            {
                if (end.Pose.Cell != cell || cost != best || !Ends(end))
                    continue;
                var chain = new List<State> { end };
                Walk(chain);
                if (routes.Count >= most * 4)
                    break;
            }
            return routes
                .OrderBy(r => r.Turns).ThenBy(r => r.Back ? 1 : 0).ThenBy(r => r.ToString(), StringComparer.Ordinal)
                .Take(most).ToList();

            void Walk(List<State> chain)
            {
                if (routes.Count >= most * 4)
                    return;
                State head = chain[^1];
                List<State> before = Pred[head];
                if (before.Count == 0)
                {
                    Route route = Build(chain);
                    if (seen.Add(route.ToString()))
                        routes.Add(route);
                    return;
                }
                foreach (State p in before)
                {
                    chain.Add(p);
                    Walk(chain);
                    chain.RemoveAt(chain.Count - 1);
                }
            }
        }

        /// <summary>The way to <paramref name="cell"/> the HUD shows first, or
        /// null.</summary>
        public Route? RouteTo(Vector2I cell) => RoutesTo(cell, 1).FirstOrDefault();

        /// <summary>
        /// The steps a turn on the spot to <paramref name="heading"/> costs at
        /// the end of <paramref name="route"/>, or null when it may not be had:
        /// over the budget, after a final step (mud, wood, a mine, the step
        /// back), or not a flat side at all. 0 for the side it already faces.
        /// </summary>
        public int? TurnTo(Route route, int heading)
        {
            if (!HexField.EdgeHeadings.Contains(heading))
                return null;
            int turns = Turning(route.End.Heading, heading);
            if (turns == 0)
                return 0;
            if (route.Final || route.Spent + turns > Budget)
                return null;
            return turns;
        }

        private Route Build(List<State> backwards)
        {
            var legs = new List<Leg>(backwards.Count);
            Risk risk = Risk.None;
            for (int i = backwards.Count - 1; i >= 0; i--)
            {
                State s = backwards[i];
                Act act = Act.Start;
                if (i < backwards.Count - 1)
                {
                    Pose was = backwards[i + 1].Pose;
                    act = was.Cell == s.Pose.Cell ? Act.Turn
                        : HexField.Step(was.Cell, was.Heading) == s.Pose.Cell ? Act.Forward
                        : Act.Back;
                }
                if (Risks.TryGetValue(s, out Risk r))
                    risk |= r;
                legs.Add(new Leg(s.Pose, act, Cost[s]));
            }
            return new Route { Legs = legs, Final = backwards[0].Final, Risk = risk };
        }
    }

    /// <summary>The steps a turn begun on <paramref name="cell"/> has: the
    /// speed, or one in mud or a wood.</summary>
    public static int BudgetAt(HexField field, Vector2I cell, Mover mover) =>
        Stops(field, cell) ? Math.Min(1, mover.Speed) : mover.Speed;

    /// <summary>Whether entering <paramref name="cell"/> ends the move by the
    /// floor or the cover alone: mud, a wood in any state.</summary>
    public static bool Stops(HexField field, Vector2I cell) =>
        field.FoundationAt(cell) == Foundation.Mud || field.CoverAt(cell) == Cover.Forest;

    /// <summary>60° turns between two flat sides, the short way round.</summary>
    public static int Turning(int from, int to)
    {
        int d = ((to - from) % 360 + 360) % 360 / 60;
        return Math.Min(d, 6 - d);
    }

    /// <summary>
    /// Search the turn from <paramref name="start"/>: every pose within the
    /// budget, the fewest steps to each and all its equal predecessors.
    /// </summary>
    public static Reach Plan(HexField field, Pose start, Mover mover)
    {
        var reach = new Reach { Start = start, Budget = BudgetAt(field, start.Cell, mover), Field = field };
        var first = new State(start, false);
        reach.Cost[first] = 0;
        reach.Pred[first] = new List<State>();
        var open = new PriorityQueue<State, int>();
        open.Enqueue(first, 0);
        while (open.TryDequeue(out State s, out int cost))
        {
            if (cost > reach.Cost[s] || s.Final)
                continue;
            Vector2I cell = s.Pose.Cell;
            int heading = s.Pose.Heading;
            bool onRamp = field.IsRamp(cell);

            if (!onRamp)
            {
                Offer(new State(new Pose(cell, Turn(heading, 60)), false), cost + 1, s, Risk.None);
                Offer(new State(new Pose(cell, Turn(heading, -60)), false), cost + 1, s, Risk.None);
            }
            if (Enter(field, mover, cell, heading, forward: true) is { } ahead)
                Offer(new State(new Pose(ahead.To, heading), ahead.Ends), cost + ahead.Price, s, ahead.Risk);
            // The step back: from the start pose only, the whole move.
            if (s == first && !onRamp
                && Enter(field, mover, cell, HexField.Reverse(heading), forward: false) is { } behind
                && !field.IsRamp(behind.To))
                Offer(new State(new Pose(behind.To, heading), true), cost + behind.Price, s, behind.Risk);
        }
        return reach;

        void Offer(State next, int cost, State from, Risk risk)
        {
            if (cost > reach.Budget)
                return;
            if (!reach.Cost.TryGetValue(next, out int had) || cost < had)
            {
                reach.Cost[next] = cost;
                reach.Pred[next] = new List<State> { from };
                reach.Risks[next] = risk;
                open.Enqueue(next, cost);
            }
            else if (cost == had && !reach.Pred[next].Contains(from))
                reach.Pred[next].Add(from);
        }
    }

    private static int Turn(int heading, int by) => ((heading + by) % 360 + 360) % 360;

    /// <summary>The step out of <paramref name="cell"/> along
    /// <paramref name="way"/>: where it lands, its price, whether the move
    /// ends there and what it risks - or null when it may not be taken.</summary>
    private static (Vector2I To, int Price, bool Ends, Risk Risk)? Enter(
        HexField field, Mover mover, Vector2I cell, int way, bool forward)
    {
        Vector2I to = HexField.Step(cell, way);
        if (to == cell || !field.InBounds(to) || mover.Barred?.Contains(to) == true)
            return null;
        if (!field.Passable(cell, way) || (!mover.Bulldozer && field.Blocked(cell, way)))
            return null;
        int price = field.FoundationAt(to) == Foundation.Sand ? 2 : 1;
        bool ends = Stops(field, to);
        Risk risk = Risk.None;
        if (field.CoverAt(to) == Cover.Minefield && field.CoverStateAt(to) != CoverState.Cleared
            && !(mover.Trawl && forward))
        {
            risk |= Risk.Mine;
            ends = true;
        }
        if (field.CoverStateAt(to) == CoverState.Burning)
            risk |= Risk.Fire;
        if (field.IsDeep(to) && !mover.Amphibious)
        {
            risk |= Risk.Drown;
            ends = true;
        }
        return (to, price, ends, risk);
    }
}
