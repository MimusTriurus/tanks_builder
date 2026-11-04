using System;
using System.Collections.Generic;
using Godot;

namespace TankSpriteTest;

public enum OrderKind { Take, Go, Turn, Fire, Burn, Ram, RamWall, Ambush, Smoke, Mine, Repair, Winch, Douse, Pass, Undo, Place, Ready, Pivot }

/// <summary>
/// An order to the core (docs/battle-core-plan.md, Д10): the bench's command
/// (Tank3DBench.Commands) as data, in the board's cells, written and read in
/// the same words as <c>--commands</c> - <c>take:ID</c>, <c>go:Q,R[:SIDE]</c>,
/// <c>turn:SIDE</c>, <c>fire:Q,R</c>, <c>burn:Q,R</c>, <c>ram:Q,R</c>,
/// <c>ramwall:SIDE</c>, <c>ambush:SIDE[:SIDE2]</c>, <c>smoke:Q,R</c>,
/// <c>mine:Q,R</c>, <c>repair:Q,R</c>, <c>winch:Q,R</c>, <c>douse</c>, <c>pivot:SIDE</c>,
/// <c>pass</c>, <c>undo</c>, <c>place:ID:Q,R[:SIDE]</c>, <c>ready</c>. What a
/// script gives <see cref="BattleRun"/> and, next, what the AI gives.
/// </summary>
public readonly record struct Order(OrderKind Kind, Vector2I Cell = default, int Side = -1, int Unit = -1, int Side2 = -1)
{
    public override string ToString() => Kind switch
    {
        OrderKind.Take => $"take:{Unit}",
        OrderKind.Go => $"go:{Cell.X},{Cell.Y}{(Side >= 0 ? $":{Side}" : "")}",
        OrderKind.Turn or OrderKind.RamWall or OrderKind.Pivot => $"{Kind.ToString().ToLowerInvariant()}:{Side}",
        OrderKind.Ambush => $"ambush:{Side}{(Side2 >= 0 ? $":{Side2}" : "")}",
        OrderKind.Place => $"place:{Unit}:{Cell.X},{Cell.Y}{(Side >= 0 ? $":{Side}" : "")}",
        OrderKind.Fire or OrderKind.Burn or OrderKind.Ram or OrderKind.Smoke
            or OrderKind.Mine or OrderKind.Repair or OrderKind.Winch => $"{Kind.ToString().ToLowerInvariant()}:{Cell.X},{Cell.Y}",
        _ => Kind.ToString().ToLowerInvariant(),
    };

    /// <summary>The order with its cell moved by <paramref name="by"/> - a
    /// script's map cell on a board in a sea (<see cref="BattleState.Shift"/>),
    /// as the bench's <c>MapCell</c> moves its commands'.</summary>
    public Order Moved(Vector2I by) => Kind switch
    {
        OrderKind.Go or OrderKind.Place or OrderKind.Fire or OrderKind.Burn or OrderKind.Ram or OrderKind.Smoke
            or OrderKind.Mine or OrderKind.Repair or OrderKind.Winch => this with { Cell = Cell + by },
        _ => this,
    };

    /// <summary>One order in <c>--commands</c>' words, or null.</summary>
    public static Order? Parse(string text)
    {
        string[] parts = text.Trim().Split(':');
        static Vector2I Cell(string s)
        {
            string[] qr = s.Split(',');
            return new Vector2I(int.Parse(qr[0]), int.Parse(qr[1]));
        }
        int Arg(int i) => parts.Length > i ? int.Parse(parts[i]) : -1;
        try
        {
            return parts[0] switch
            {
                "take" => new Order(OrderKind.Take, Unit: int.Parse(parts[1])),
                "go" => new Order(OrderKind.Go, Cell(parts[1]), Arg(2)),
                "turn" => new Order(OrderKind.Turn, Side: int.Parse(parts[1])),
                "fire" => new Order(OrderKind.Fire, Cell(parts[1])),
                "burn" => new Order(OrderKind.Burn, Cell(parts[1])),
                "ram" => new Order(OrderKind.Ram, Cell(parts[1])),
                "ramwall" => new Order(OrderKind.RamWall, Side: int.Parse(parts[1])),
                "ambush" => new Order(OrderKind.Ambush, Side: int.Parse(parts[1]), Side2: Arg(2)),
                "smoke" => new Order(OrderKind.Smoke, Cell(parts[1])),
                "mine" => new Order(OrderKind.Mine, Cell(parts[1])),
                "repair" => new Order(OrderKind.Repair, Cell(parts[1])),
                "winch" => new Order(OrderKind.Winch, Cell(parts[1])),
                "douse" => new Order(OrderKind.Douse),
                "pivot" => new Order(OrderKind.Pivot, Side: int.Parse(parts[1])),
                "pass" => new Order(OrderKind.Pass),
                "undo" => new Order(OrderKind.Undo),
                "place" => new Order(OrderKind.Place, Cell(parts[2]), Arg(3), int.Parse(parts[1])),
                "ready" => new Order(OrderKind.Ready),
                _ => null,
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>A script: orders split by <c>;</c> or lines, <c>#</c> to the
    /// line's end a comment. The words that are no order come back in
    /// <paramref name="wrong"/>.</summary>
    public static List<Order> ParseAll(string text, List<string> wrong)
    {
        var orders = new List<Order>();
        foreach (string line in text.Split('\n'))
        {
            int hash = line.IndexOf('#');
            foreach (string word in (hash >= 0 ? line[..hash] : line).Split(';'))
            {
                if (word.Trim().Length == 0)
                    continue;
                if (Parse(word) is { } o)
                    orders.Add(o);
                else
                    wrong.Add(word.Trim());
            }
        }
        return orders;
    }
}

/// <summary>
/// The turn as the bench plays it, on the core (Д10): an order applied, the
/// turn going over by itself once nothing is left in it, the next side's
/// first unit able to act taking it. What a match without the scene needs
/// beyond the rules' own actions - the bench's <c>MatchTick</c>,
/// <c>HandOver</c> and <c>FindRam</c> in the core's terms.
/// </summary>
public static partial class Battle
{
    /// <summary>
    /// One order applied: why not, or null. The ram goes as the bench's
    /// command has it (<see cref="RamTo"/>), the move first if the target is
    /// not beside; the pass ends the turn (<see cref="Pass"/>). The core keeps
    /// no undo, and the placement is the runner's (<see cref="BattleRun"/>).
    /// </summary>
    public static string? Apply(BattleState s, Order o) => o.Kind switch
    {
        OrderKind.Take => Take(s, o.Unit),
        OrderKind.Go => Go(s, o.Cell, o.Side),
        OrderKind.Turn => Turn(s, o.Side),
        OrderKind.Fire => Fire(s, o.Cell),
        OrderKind.Burn => Fire(s, o.Cell, incendiary: true),
        OrderKind.Ram => RamTo(s, o.Cell),
        OrderKind.RamWall => RamWall(s, o.Side),
        OrderKind.Ambush => Ambush(s, o.Side, o.Side2),
        OrderKind.Smoke => Smoke(s, o.Cell),
        OrderKind.Mine => LayMine(s, o.Cell),
        OrderKind.Repair => Repair(s, o.Cell),
        OrderKind.Winch => Winch(s, o.Cell),
        OrderKind.Douse => Douse(s),
        OrderKind.Pivot => Pivot(s, o.Side),
        OrderKind.Pass => s.Over ? "the match is over" : PassNow(s),
        OrderKind.Undo => "the core keeps no undo",
        _ => "the placement is before the first turn",
    };

    private static string? PassNow(BattleState s)
    {
        Pass(s);
        return null;
    }

    /// <summary>
    /// The ram of the unit on <paramref name="target"/> the way the bench's
    /// command finds it (Tank3DBench.RamPlan, <c>FindRam</c>): from where the
    /// hull stands, turned to it if the move is still to be had; else from a
    /// hex beside it the move reaches, turned on to it out of the same steps.
    /// The best is the one the ram is allowed from, then safe, then the
    /// fewest steps, then the planner's first way.
    /// </summary>
    public static string? RamTo(BattleState s, Vector2I target)
    {
        if (Acting(s, out string? why) is not { } u)
            return why;
        if (s.At(target) is not { } victim || victim.Id == u.Id)
            return "nobody to ram";
        Steps.Mover mover = MoverOf(s, u);
        bool mayMove = !s.Drove && (!(s.Fired || s.Bonus) || u.Class.FreeOrder);
        Steps.Reach? reach = mayMove ? Steps.Plan(s.Board, new Steps.Pose(u.Cell, u.Heading), mover) : null;
        Steps.Route? bestRoute = null;
        int bestSide = -1;
        bool found = false;
        Ramming.Shove bestPush = default;
        (int, int, int, int) bestRank = (int.MaxValue, 0, 0, 0);
        void Offer(Steps.Route? route, int side, int way, int steps, bool risky)
        {
            Ramming.Shove push = Ramming.Of(s.Board, c => c != target && s.At(c) is not null, u.Class, victim.Class, target, side);
            var rank = (push.Allowed ? 0 : 1, risky ? 1 : 0, steps, way);
            if (found && rank.CompareTo(bestRank) >= 0)
                return;
            found = true;
            bestRank = rank;
            bestRoute = route;
            bestSide = side;
            bestPush = push;
        }
        int here = HexField.HeadingTo(u.Cell, target);
        if (here >= 0)
        {
            int turns = reach is null ? Steps.Turning(u.Heading, here) : Steps.TurnCost(u.Heading, here, mover);
            if (Steps.Turning(u.Heading, here) == 0 || (reach is not null && turns <= reach.Budget))
                Offer(null, here, -1, turns, false);
        }
        if (reach is not null)
            foreach (int side in HexField.EdgeHeadings)
            {
                Vector2I from = HexField.Step(target, (side + 180) % 360);
                if (!reach.Reaches(from))
                    continue;
                List<Steps.Route> ways = reach.RoutesTo(from);
                for (int w = 0; w < ways.Count; w++)
                    if (reach.TurnTo(ways[w], side) is int t)
                        Offer(ways[w], side, w, ways[w].Spent + t, ways[w].Risk != Steps.Risk.None);
            }
        if (!found)
            return here >= 0 ? "no steps to turn on to it" : "no way to a hex beside it";
        // refused before the move, as the bench refuses the plan
        if (!bestPush.Allowed)
            return Ramming.Because(bestPush.Why);
        if (bestRoute is { } way2)
        {
            if (Move(s, way2.Cells(), bestSide, way2.Back) is { } refused)
                return refused;
            if (u.Fate != UnitFate.Alive || u.Cell != HexField.Step(target, (bestSide + 180) % 360))
                return null;
        }
        return Ram(s, target);
    }

    /// <summary>
    /// Nothing is left in the turn, and the bench hands it over by itself
    /// (Tank3DBench.Match, <c>MatchTick</c>): the unit is out of the fight, or
    /// it has fired, moved (or may not - only LT moves after the shot) and has
    /// no bonus act to be had - a fire to put out, a unit beside to ram, the
/// double turn; none at all once the bonus act is spent or owed.
    /// </summary>
    public static bool TurnDone(BattleState s)
    {
        if (s.Over || s.Active is not int id || s.Unit(id) is not { } u)
            return false;
        if (u.Fate != UnitFate.Alive)
            return true;
        if (!s.Fired || !(s.Drove || !u.Class.FreeOrder))
            return false;
        if (!BonusLeft(s, u))
            return true;
        if (u.Burning)
            return false;
        // the double turn is a bonus act to be had anywhere but on a ramp
        if ((u.Kit & Kit.DoubleTurn) != 0 && !s.Board.IsRamp(u.Cell))
            return false;
        foreach (int h in HexField.EdgeHeadings)
            if (s.At(HexField.Step(u.Cell, h)) is not null)
                return false;
        return true;
    }

    /// <summary>The side's first unit able to act, as the bench mounts it on
    /// the rig: by the roster's order (LT, MT, HT, TD, HM), then by id.</summary>
    public static BattleUnit? FirstReady(BattleState s, Camp side)
    {
        BattleUnit? best = null;
        foreach (BattleUnit u in s.Units)
            if (u.Side == side && u.Fate == UnitFate.Alive
                && (best is null || RosterAt(u.Tag) < RosterAt(best.Tag)
                    || (RosterAt(u.Tag) == RosterAt(best.Tag) && u.Id < best.Id)))
                best = u;
        return best;
    }

    private static int RosterAt(string tag)
    {
        int i = Array.IndexOf(Lineup.Roster, tag);
        return i < 0 ? int.MaxValue : i;
    }

    /// <summary>
    /// The turn over as the bench hands it over (<c>HandOver</c>): the core's
    /// end of it (<see cref="EndTurn"/>), then the next side's first unit able
    /// to act takes it. A side with none passes on - no turn of its own, the
    /// round's end still ticking - and with nobody on either side the match is
    /// a draw.
    /// </summary>
    public static void Pass(BattleState s)
    {
        EndTurn(s);
        for (int tries = 0; tries < 2 && !s.Over; tries++)
        {
            if (FirstReady(s, s.Turn) is { } next)
            {
                s.Active = next.Id;
                return;
            }
            if (tries == 1)
                break;
            s.Events.Add(new BattleEvent(BattleEventKind.TurnEnded, -1, default, s.TurnNo, $"{s.Turn} have nobody able to act"));
            s.Turn = s.Turn == Camp.Green ? Camp.Brown : Camp.Green;
            if (s.Turn == s.First)
            {
                FieldTick(s);
                s.Round++;
            }
            Over(s);
            Stalemate(s);
        }
        if (s.Over)
            return;
        s.Drawn = true;
        s.Events.Add(new BattleEvent(BattleEventKind.Drawn, -1, default, 0, "nobody able to act"));
    }
}
