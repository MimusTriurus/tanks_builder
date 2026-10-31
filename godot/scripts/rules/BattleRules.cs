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
/// round, the drive's upgrades (in <see cref="Steps"/>), the spall liner, the
/// auto-extinguisher, the thermal imager (Д8).
///
/// <b>Not yet</b> (stages Д8-Д9): the winch, the mine layer, the repair kit,
/// the ambush stabilizer; an ambush that holds an incendiary round back - it
/// fires a plain one; the control point, placement.
/// </summary>
public static class Battle
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
    /// each cell does is done as it is entered (п. 4): a mine knocks out and the
    /// path ends there, deep water drowns without the gear, a wood alight sets
    /// the hull alight, water puts a fire out, a burning hull sets a wood alight,
    /// the heavy knocks down a wall it crosses. The cost is the route planner's
    /// (<see cref="Steps"/>) and not checked again here. <paramref name="back"/>:
    /// the move is astern (<see cref="Steps.Route.Back"/>), each cell entered
    /// tail first - the reverse gear's bend between two of them a turn; null
    /// reads each step against the hull, the one cell back as before.
    /// </summary>
    public static string? Move(BattleState s, IReadOnlyList<Vector2I> path, int face, bool? back = null)
    {
        if (Acting(s, out string? why) is not { } u)
            return why;
        if (s.Drove)
            return "already moved";
        if ((s.Fired || s.Bonus) && !u.Class.FreeOrder)
            return "only LT moves after acting";
        s.Drove = true;
        Lift(s, u, "moved");
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
            u.Heading = astern ? HexField.Reverse(way) : way;
            u.Cell = next;
            s.Events.Add(new BattleEvent(BattleEventKind.Moved, u.Id, next, way));
            Entered(s, u, way, forward: !astern);
            if (u.Fate != UnitFate.Alive)
                return null;
        }
        if (face >= 0 && face != u.Heading)
        {
            u.Heading = face;
            s.Events.Add(new BattleEvent(BattleEventKind.Turned, u.Id, u.Cell, face));
        }
        return null;
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
    /// (field.md, «мины», «вода», «огонь\пожар»; states.md, «Горит»).</summary>
    private static void Arrive(BattleState s, BattleUnit u, int way, bool forward)
    {
        Vector2I c = u.Cell;
        if (s.Board.CoverAt(c) == Cover.Minefield && s.Board.CoverStateAt(c) == CoverState.Intact)
        {
            s.Board.SetCoverState(c, CoverState.Cleared);
            if ((u.Kit & Kit.Trawl) != 0 && forward && way == u.Heading)
                s.Events.Add(new BattleEvent(BattleEventKind.MineCleared, u.Id, c));
            else
            {
                s.Events.Add(new BattleEvent(BattleEventKind.MineBlown, u.Id, c));
                Knock(s, u);
                return;
            }
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
                    s.Events.Add(new BattleEvent(BattleEventKind.Drowned, u.Id, c));
                    Lift(s, u, "drowned");
                }
            }
            return;
        }
        if (s.Board.CoverAt(c) == Cover.Forest)
        {
            if (s.Board.CoverStateAt(c) == CoverState.Burning)
                Ignite(s, u);
            else if (u.Burning)
                WoodAlight(s, c);
        }
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
    /// spent in catches; the mortar's bomb the same.
    /// </summary>
    public static string? Fire(BattleState s, Vector2I aim, bool incendiary = false)
    {
        if (Acting(s, out string? why) is not { } u)
            return why;
        if (s.Fired)
            return "already fired";
        if (incendiary && (u.Kit & Kit.Incendiary) == 0)
            return "no incendiary rounds";
        (int heading, int range) = s.Board.LaneTo(u.Cell, aim);
        if (heading < 0)
            return "not on an axis";
        if (!u.Class.Turreted && heading != u.Heading)
            return "a casemate fires along its hull";
        BattleUnit? there = s.At(aim);
        switch (Gunnery.MayTarget(u.Class.Lobs, range, there is not null))
        {
            case Gunnery.Distance.Own: return "its own hex";
            case Gunnery.Distance.MortarBand: return $"the mortar throws 3 to 5, this is {range}";
            case Gunnery.Distance.PointBlank: return "a tank point-blank";
        }
        s.Fired = true;
        Lift(s, u, "fired");
        if (u.Class.Lobs)
            Lob(s, aim, incendiary);
        else
            Fly(s, u.Cell, heading, u.Class.Might, Level(s, u), passes: u.Class.Might >= 4 && !u.Class.Turreted ? 1 : 0,
                glance: true, aim: aim, fiery: incendiary, thermal: (u.Kit & Kit.Thermal) != 0);
        return null;
    }

    /// <summary>The level a hull fires from and is shot at: its cell's, but
    /// afloat on the bank's (field.md, 2026-10-29).</summary>
    private static int Level(BattleState s, BattleUnit u) =>
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
                            int passes, bool glance, Vector2I? aim, bool fiery = false, bool thermal = false)
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
        if (s.Bonus)
            return "the bonus act is spent";
        if (s.At(target) is not { } victim)
            return "nobody to ram";
        int way = HexField.HeadingTo(u.Cell, target);
        if (way < 0)
            return "not beside it";
        if (u.Class.Bulldozes)
        {
            WallDownAll(s, target);
        }
        Ramming.Shove push = Ramming.Of(s.Board, c => c != target && s.At(c) is not null, u.Class, victim.Class, target, way);
        if (!push.Allowed)
        {
            s.Events.Add(new BattleEvent(BattleEventKind.RamRefused, u.Id, target, way, Ramming.Because(push.Why)));
            return Ramming.Because(push.Why);
        }
        s.Bonus = true;
        if (!(u.Class.FreeOrder && !s.Drove))
            s.Drove = true;
        // an order to the rammer, a ram to the rammed: both ambushes off
        Lift(s, u, "rammed");
        Lift(s, victim, "was rammed");
        u.Heading = way;
        Vector2I landing = push.Legs[^1];
        if (u.Class.Bulldozes)
            WallDownAll(s, landing);
        victim.Cell = landing;
        s.Events.Add(new BattleEvent(BattleEventKind.Shoved, victim.Id, landing, way));
        u.Cell = target;
        s.Events.Add(new BattleEvent(BattleEventKind.Moved, u.Id, target, way));
        if (victim.Fate == UnitFate.Alive)
            Entered(s, victim, way, forward: way == victim.Heading);
        else
        {
            if (s.Board.IsDeep(landing) && victim.Fate == UnitFate.Knocked)
            {
                victim.Fate = UnitFate.Drowned;
                s.Events.Add(new BattleEvent(BattleEventKind.Drowned, victim.Id, landing));
            }
            // a hull out of the fight shoved on to an axis is crossing it too
            // (units.md: «Отброшенный тараном вражеский танк»; states.md: the
            // ambush's round on it is the one that destroys it)
            Disperse(s, victim);
            Ambushes(s, victim);
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
        if (s.Bonus)
            return "the bonus act is spent";
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
        if (s.Bonus)
            return "the bonus act is spent";
        if (!u.Burning)
            return "not burning";
        if (s.Board.CoverAt(u.Cell) == Cover.Forest && s.Board.CoverStateAt(u.Cell) == CoverState.Burning)
            return "on a burning hex";
        s.Bonus = true;
        Lift(s, u, "doused");
        Douse(s, u);
        return null;
    }

    // --- the ambush ---------------------------------------------------------------------

    /// <summary>
    /// The ambush (units.md, «Режим засады»; п. 5а): the attack of the turn
    /// held back on <paramref name="axis"/> - for the medium (classes.md,
    /// «Секторная засада») and its <paramref name="second"/> beside it too. Set
    /// last: the turn is spent with it, the move and the bonus act as well, for
    /// any act of the unit after it would lift it. A casemate watches along its
    /// hull - turned to the axis first, out of the move's steps.
    /// </summary>
    public static string? Ambush(BattleState s, int axis, int second = -1)
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
        if (!u.Class.Turreted && axis != u.Heading)
            return "a casemate watches along its hull";
        s.Fired = s.Drove = s.Bonus = true;
        u.Watch = axis;
        u.Watch2 = second;
        s.Events.Add(new BattleEvent(BattleEventKind.AmbushSet, u.Id, u.Cell, axis, second >= 0 ? $"and {second}" : ""));
        return null;
    }

    /// <summary>A cell's effects on a hull that came on to it (<see cref="Arrive"/>),
    /// then the enemy's ambushes on it - in that order (match.md п. 4). An
    /// effect of the hex that knocks it out ends the hex: the ambush answers
    /// the hull as it came in.</summary>
    private static void Entered(BattleState s, BattleUnit u, int way, bool forward)
    {
        UnitFate was = u.Fate;
        Disperse(s, u);
        Arrive(s, u, way, forward);
        if (u.Fate == was)
            Ambushes(s, u);
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
        if (s.Bonus)
            return "the bonus act is spent";
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
        s.Board.SetSmoke(cell, true);
        s.Smoke[cell] = SmokeRounds;
        s.Events.Add(new BattleEvent(BattleEventKind.SmokeLaid, u.Id, cell, SmokeRounds));
        return null;
    }

    private const int SmokeRounds = 2;

    /// <summary>
    /// Unit <paramref name="entered"/> has come on to its hex - driven, shoved
    /// or through a wall: every ambush of the other side whose axis sees it
    /// there now (<see cref="Sees"/>) fires, by the rules of a shot, and is
    /// spent. They fire in turn while the hull is as it came in: the first that
    /// knocks it out ends it. Public for the bench, which plays it on its own
    /// hull's entering (Tank3DBench.Ambush).
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
            w.Watch = w.Watch2 = -1;
            s.Events.Add(new BattleEvent(BattleEventKind.AmbushFired, w.Id, entered.Cell, axis, $"at #{entered.Id}"));
            if (w.Class.Lobs)
                Lob(s, entered.Cell);
            else
                Fly(s, w.Cell, axis, w.Class.Might, Level(s, w),
                    passes: w.Class.Might >= 4 && !w.Class.Turreted ? 1 : 0, glance: true, aim: entered.Cell,
                    thermal: (w.Kit & Kit.Thermal) != 0);
            Over(s);
        }
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
        if (heading < 0 || (heading != w.Watch && heading != w.Watch2))
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
        u.Watch = u.Watch2 = -1;
        s.Events.Add(new BattleEvent(BattleEventKind.AmbushLifted, u.Id, u.Cell, 0, why));
    }

    // --- the end of the turn ----------------------------------------------------------

    /// <summary>
    /// П. 6 and 7: the side's units still burning from before this turn go up
    /// (states.md, «Горит»), the wood under a heavy that ends its turn on it
    /// falls (classes.md, «Бульдозер HT»), and the turn goes over - the round,
    /// and the field's tick, after the second side's (match.md, «Ход и раунд»).
    /// A burning unit with the auto-extinguisher (upgrades.md) its side did not
    /// activate this turn - did not move it nor act with it - goes out instead,
    /// and keeps its ambush.
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
        foreach (BattleUnit b in s.Units.ToArray())
            if (b.Side == ending && b.Fate == UnitFate.Alive && b.Burning && b.BurnSince < s.TurnNo)
            {
                if ((b.Kit & Kit.Extinguisher) != 0 && b.Id != activated)
                    Douse(s, b);
                else
                    Explode(s, b);
            }
        s.Events.Add(new BattleEvent(BattleEventKind.TurnEnded, s.Active ?? -1, default, s.TurnNo));
        s.TurnNo++;
        s.Turn = ending == Camp.Green ? Camp.Brown : Camp.Green;
        s.Active = null;
        s.Drove = s.Fired = s.Bonus = false;
        if (s.Turn == Camp.Green)
        {
            FieldTick(s);
            s.Round++;
        }
        Over(s);
        Stalemate(s);
    }

    /// <summary>Пат (match.md, «Пат — ничья»): <see cref="MatchRules.QuietRoundsDraw"/>
    /// whole rounds without a shot at a tank - a round into the ground, a wall
    /// or a wood is no shot at one, nor a mine or a fire. Read after the win:
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
        s.Events.Add(new BattleEvent(BattleEventKind.FieldTick, -1, default, s.Round));
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

    /// <summary>A side with every unit destroyed has lost (match.md, «Уничтожение»).</summary>
    private static void Over(BattleState s)
    {
        if (s.Over)
            return;
        foreach (Camp side in new[] { Camp.Green, Camp.Brown })
        {
            bool any = false, alive = false;
            foreach (BattleUnit u in s.Units)
                if (u.Side == side)
                {
                    any = true;
                    alive |= u.Standing;
                }
            if (any && !alive)
            {
                s.Won = side == Camp.Green ? Camp.Brown : Camp.Green;
                s.Events.Add(new BattleEvent(BattleEventKind.Won, -1, default, (int)s.Won.Value));
                return;
            }
        }
    }
}
