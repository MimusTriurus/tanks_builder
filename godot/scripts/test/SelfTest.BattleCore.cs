using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// Д3: the rules core (<see cref="Battle"/>) on positions out of the GDD whose
/// outcome the GDD states - on a probe board of its own, no scene, no bench.
/// The lanes run up and down a column (heading 90 is one row up), where the
/// hex grid's offset rows do not come into it.
/// </summary>
public static partial class SelfTest
{
    private static void BattleCore(Action<string, bool, string> check)
    {
        Theme("the battle core plays the GDD");

        // units.md, «Рикошет»: a hull facing N (90) sends a round from each of
        // the four side faces out by the other face of the same side.
        check("a side mirrors the round along the hull: NE->SE, SE->NE, NW->SW, SW->NW",
              Onward(90, 210) == 330 && Onward(90, 150) == 30 && Onward(90, 330) == 210 && Onward(90, 30) == 150,
              $"{Onward(90, 210)} {Onward(90, 150)} {Onward(90, 330)} {Onward(90, 30)}");
        check("the plate is the face the round comes in by, the roof from above",
              Battle.PlateOf(270, 90, false) == Gunnery.Plate.Front
              && Battle.PlateOf(90, 90, false) == Gunnery.Plate.Rear
              && Battle.PlateOf(210, 90, false) == Gunnery.Plate.Side
              && Battle.PlateOf(270, 90, true) == Gunnery.Plate.Roof, "");

        // MT into HT's side: II is not over II - the round glances, mirrored
        // into the LT beside, whose side 0 it goes through.
        {
            BattleState s = Probe(9, 10);
            Put(s, 0, "MTR", Camp.Green, 4, 8, 90);
            Put(s, 1, "HTR", Camp.Brown, 4, 5, 150);
            Vector2I beside = HexField.Step(new Vector2I(4, 5), Onward(150, 90));
            Put(s, 2, "LTR", Camp.Brown, beside.X, beside.Y, 90);
            Act(s, 0, st => Battle.Fire(st, new Vector2I(4, 5)));
            check("a glance off a side takes the hull beside, no gap needed",
                  s.Unit(1)!.Fate == UnitFate.Alive && s.Unit(2)!.Fate == UnitFate.Knocked,
                  Said(s));
        }

        // classes.md, «TD — Тяжёлый снаряд»: through the wall at IV, on at III
        // into the medium's side (I) behind it.
        {
            BattleState s = Probe(9, 10, walls: new[] { (new Vector2I(4, 6), 90) });
            Put(s, 0, "TDR", Camp.Green, 4, 8, 90);
            Put(s, 1, "MTR", Camp.Brown, 4, 4, 30);
            Act(s, 0, st => Battle.Fire(st, new Vector2I(4, 4)));
            check("the destroyer's round goes through a wall and takes the tank behind it",
                  !s.Board.Walled(new Vector2I(4, 6), 90) && s.Unit(1)!.Fate == UnitFate.Knocked
                  && s.Events.Any(e => e.Kind == BattleEventKind.Passed && e.Value == 3),
                  Said(s));
        }

        // A gun's round stops at a wood; the mortar's goes over it, 3 to 5 out.
        {
            BattleState s = Probe(9, 10, woods: new[] { new Vector2I(4, 6) });
            Put(s, 0, "MTR", Camp.Green, 4, 8, 90);
            Put(s, 1, "LTR", Camp.Brown, 4, 4, 90);
            Act(s, 0, st => Battle.Fire(st, new Vector2I(4, 4)));
            bool gun = s.Unit(1)!.Fate == UnitFate.Alive;
            BattleState m = Probe(9, 10, woods: new[] { new Vector2I(4, 6), new Vector2I(4, 7) });
            Put(m, 0, "HMR", Camp.Green, 4, 8, 90);
            Put(m, 1, "HTR", Camp.Brown, 4, 4, 90);
            Act(m, 0, st => Battle.Fire(st, new Vector2I(4, 4)));
            string? close = Battle.Fire(Started(Probe(9, 10), "HMR", 4, 8, 90), new Vector2I(4, 6));
            check("a wood stops a gun's round and not the mortar's, which wants 3 to 5",
                  gun && m.Unit(1)!.Fate == UnitFate.Knocked && close is not null,
                  $"gun {gun}; mortar {Said(m)}; at 2: {close}");
        }

        // field.md, «кромка»: from a level up the round meets the roof - the
        // medium's I under its II - where level it meets the front, II.
        {
            var levels = new int[9 * 10];
            levels[8 * 9 + 4] = 1;
            BattleState s = Probe(9, 10, levels: levels);
            Put(s, 0, "MTR", Camp.Green, 4, 8, 90);
            Put(s, 1, "MTR", Camp.Brown, 4, 6, 270);
            Act(s, 0, st => Battle.Fire(st, new Vector2I(4, 6)));
            BattleState flat = Probe(9, 10);
            Put(flat, 0, "MTR", Camp.Green, 4, 8, 90);
            Put(flat, 1, "MTR", Camp.Brown, 4, 6, 270);
            Act(flat, 0, st => Battle.Fire(st, new Vector2I(4, 6)));
            check("from above a medium takes a medium through the roof, level it does not",
                  s.Unit(1)!.Fate == UnitFate.Knocked && flat.Unit(1)!.Fate == UnitFate.Alive,
                  $"{Said(s)} | {Said(flat)}");
        }

        // units.md, «Таран»: pushed on to a mine it is knocked out; into deep
        // water it swims with the gear and drowns without.
        {
            BattleState s = Probe(9, 10, mines: new[] { new Vector2I(4, 4) });
            Put(s, 0, "MTR", Camp.Green, 4, 6, 90);
            Put(s, 1, "LTR", Camp.Brown, 4, 5, 270);
            Act(s, 0, st => Battle.Ram(st, new Vector2I(4, 5)));
            BattleState w = Probe(9, 10, deep: new[] { new Vector2I(4, 4) });
            Put(w, 0, "HTR", Camp.Green, 4, 6, 90);
            Put(w, 1, "MTR", Camp.Brown, 4, 5, 270);
            Put(w, 2, "LTR", Camp.Brown, 6, 5, 270, Kit.Snorkel);
            Act(w, 0, st => Battle.Ram(st, new Vector2I(4, 5)));
            check("rammed on to a mine is knocked out, into deep water without the gear drowned",
                  s.Unit(1)!.Fate == UnitFate.Knocked && s.Unit(1)!.Cell == new Vector2I(4, 4)
                  && s.Unit(0)!.Cell == new Vector2I(4, 5) && w.Unit(1)!.Fate == UnitFate.Drowned,
                  $"{Said(s)} | {Said(w)}");
            string? heavy = Battle.Ram(Started(Probe(9, 10), "LTR", 4, 6, 90, extra: ("HTR", 4, 5)), new Vector2I(4, 5));
            check("a light does not shove a heavy", heavy is not null, heavy ?? "");
        }

        // states.md, «Уничтожен»: the blast goes through the knocked out beside
        // it, one after another, and sets the one still fighting alight.
        {
            BattleState s = Probe(9, 10);
            Put(s, 0, "HMR", Camp.Green, 4, 9, 90);
            Put(s, 1, "MTR", Camp.Brown, 4, 5, 90, fate: UnitFate.Knocked);
            Put(s, 2, "MTR", Camp.Brown, 4, 4, 90, fate: UnitFate.Knocked);
            Put(s, 3, "HTR", Camp.Brown, 4, 3, 90);
            Put(s, 4, "LTR", Camp.Brown, 0, 0, 90);
            Act(s, 0, st => Battle.Fire(st, new Vector2I(4, 5)));
            check("a blast goes up the chain of the knocked out and sets the living alight",
                  s.Unit(1)!.Fate == UnitFate.Destroyed && s.Unit(2)!.Fate == UnitFate.Destroyed
                  && s.Unit(3)!.Fate == UnitFate.Alive && s.Unit(3)!.Burning, Said(s));

            // the owner's turn over with it still alight: gone, with a blast
            Battle.EndTurn(s);
            BattleState kept = s.Clone();
            Battle.Take(kept, 3);
            Battle.Douse(kept);
            Battle.EndTurn(kept);
            Battle.Take(s, 4);
            Battle.EndTurn(s);
            check("burning through its owner's turn it goes up; put out, it does not",
                  s.Unit(3)!.Fate == UnitFate.Destroyed && kept.Unit(3)!.Fate == UnitFate.Alive,
                  $"{s.Unit(3)} | {kept.Unit(3)}");
        }

        // match.md, «Ход и раунд»: a wood alight burns two rounds, then burnt;
        // the fire goes on to the wood beside at the round's end.
        {
            var row = new[] { new Vector2I(2, 2), new Vector2I(2, 3), new Vector2I(2, 4) };
            BattleState s = Probe(9, 10, woods: row);
            Put(s, 0, "LTR", Camp.Green, 6, 8, 90);
            Put(s, 1, "LTR", Camp.Brown, 6, 1, 90);
            s.Board.SetCoverState(row[1], CoverState.Burning);
            s.Fires[row[1]] = 2;
            Battle.EndTurn(s);
            Battle.EndTurn(s);
            bool spread = s.Board.CoverStateAt(row[0]) == CoverState.Burning;
            Battle.EndTurn(s);
            Battle.EndTurn(s);
            check("a wood burns two rounds, then stands burnt, and lights the one beside",
                  spread && s.Board.CoverStateAt(row[1]) == CoverState.Burnt, Said(s));
        }

        // classes.md, «Бульдозер HT»: the wood a heavy ends its turn on is ground.
        {
            BattleState s = Probe(9, 10, woods: new[] { new Vector2I(4, 5) });
            Put(s, 0, "HTR", Camp.Green, 4, 6, 90);
            Put(s, 1, "LTR", Camp.Brown, 0, 0, 90);
            Act(s, 0, st => Battle.Move(st, new[] { new Vector2I(4, 5) }, 90));
            Battle.EndTurn(s);
            check("the wood a heavy stops on is cleared at its turn's end",
                  s.Board.CoverStateAt(new Vector2I(4, 5)) == CoverState.Cleared, Said(s));
        }

        // field.md, «вода»: off the bank into the deep - the gear swims, none drowns.
        {
            BattleState s = Probe(9, 10, deep: new[] { new Vector2I(4, 5), new Vector2I(6, 5) });
            Put(s, 0, "LTR", Camp.Green, 4, 6, 90, Kit.Snorkel);
            Put(s, 1, "MTR", Camp.Green, 6, 6, 90);
            Put(s, 2, "LTR", Camp.Brown, 0, 0, 90);
            Act(s, 0, st => Battle.Move(st, new[] { new Vector2I(4, 5) }, 90));
            s.Drove = false;
            Act(s, 1, st => Battle.Move(st, new[] { new Vector2I(6, 5) }, 90));
            check("into the deep the wading gear swims and the rest drown",
                  s.Unit(0)!.Fate == UnitFate.Alive && s.Unit(1)!.Fate == UnitFate.Drowned, Said(s));
        }

        // Д5: the move as a command gives it - the planner's route, the class's
        // budget (classes.md: LT 5, MT 4), the side at the end out of the same.
        {
            BattleState s = Probe(9, 10);
            Put(s, 0, "LTR", Camp.Green, 2, 8, 90);
            Put(s, 1, "MTR", Camp.Green, 6, 8, 90);
            Put(s, 2, "LTR", Camp.Brown, 0, 0, 90);
            string? light = null, medium = null, late = null;
            Act(s, 0, st => light = Battle.Go(st, new Vector2I(2, 3)));
            s.Drove = false;
            Act(s, 1, st => medium = Battle.Go(st, new Vector2I(6, 3)));
            BattleState t = Probe(9, 10);
            Put(t, 0, "MTR", Camp.Green, 6, 8, 90);
            Put(t, 1, "LTR", Camp.Brown, 0, 0, 90);
            Act(t, 0, st => late = Battle.Go(st, new Vector2I(6, 4), face: 270));
            check("a command's move goes by the budget: LT five up, MT not, nor four and a half-turn",
                  light is null && s.Unit(0)!.Cell == new Vector2I(2, 3)
                  && medium is not null && s.Unit(1)!.Cell == new Vector2I(6, 8) && late is not null,
                  $"LT {light ?? "ok"}; MT {medium}; MT turned {late}");
        }

        // Д6, match.md «Пат — ничья»: so many whole rounds without a shot at a
        // tank and the match is drawn; a round into a wall is no such shot, a
        // round on a tank - glanced off the front or not - puts the clock back.
        {
            int quiet = MatchRules.QuietRoundsDraw;
            BattleState Facing(bool wall)
            {
                BattleState s = Probe(9, 10, walls: wall ? new[] { (new Vector2I(4, 6), 90) } : null);
                Put(s, 0, wall ? "MTR" : "LTR", Camp.Green, 4, 8, 90);
                Put(s, 1, "HTR", Camp.Brown, 4, wall ? 3 : 5, 270);
                return s;
            }
            BattleState passes = Facing(false);
            Turns(passes, 2 * quiet - 1);
            bool early = passes.Drawn;
            Turns(passes, 1);
            BattleState walled = Facing(true);
            Turns(walled, 2);
            Act(walled, 0, st => Battle.Fire(st, new Vector2I(4, 3)));
            bool wallHit = walled.Unit(1)!.Fate == UnitFate.Alive && walled.LastStrike == 0;
            Turns(walled, 2 * quiet - 2);
            BattleState struck = Facing(false);
            Turns(struck, 4);
            Act(struck, 0, st => Battle.Fire(st, new Vector2I(4, 5)));
            int strikeRound = struck.LastStrike;
            Turns(struck, 2 * quiet + 1);
            bool held = !struck.Drawn;
            Turns(struck, 1);
            check($"{quiet} rounds without a shot at a tank draw the match; a wall's shot does not hold it off, a tank's does",
                  !early && passes.Drawn && passes.Events.Any(e => e.Kind == BattleEventKind.Drawn)
                  && wallHit && walled.Drawn && strikeRound == 3 && held && struck.Drawn,
                  $"passes: early {early}, at {passes.Round} {passes.Drawn}; wall: {wallHit} drawn {walled.Drawn}; "
                  + $"struck in round {strikeRound}, held {held}, then {struck.Drawn}: {Said(struck)}");
        }

        // Д7, units.md «Режим засады»: a green medium at (4,8) watching up (90).
        {
            var mid = new Vector2I(4, 5);
            BattleState Watched(int axis2 = -1, bool ownBetween = false, bool wall = false)
            {
                BattleState s = Probe(9, 10, walls: wall ? new[] { (new Vector2I(4, 6), 90) } : null);
                Put(s, 0, "MTR", Camp.Green, 4, 8, 90);
                s.Unit(0)!.Watch = 90;
                s.Unit(0)!.Watch2 = axis2;
                if (ownBetween)
                    Put(s, 2, "LTR", Camp.Green, 4, 6, 90);
                return s;
            }
            // across the axis, three out: the round on its side, knocked, the path ends there
            Vector2I[] Across(Vector2I c) => new[] { HexField.Step(c, 30), c, HexField.Step(c, 210) };
            BattleState crossed = Watched();
            Put(crossed, 1, "LTR", Camp.Brown, Across(mid)[0].X, Across(mid)[0].Y, 210);
            Act(crossed, 1, st => Battle.Move(st, Across(mid), -1));
            check("an enemy crossing the axis is fired on as it comes in, and a knocked one stops there",
                  crossed.Unit(1)!.Fate == UnitFate.Knocked && crossed.Unit(1)!.Cell == mid
                  && !crossed.Unit(0)!.Ambushing && crossed.Events.Any(e => e.Kind == BattleEventKind.AmbushFired),
                  Said(crossed));
            // beside it: no clear hex between; behind a hull of its own; behind a wall
            Vector2I near = new(4, 7);
            BattleState close = Watched();
            Put(close, 1, "LTR", Camp.Brown, Across(near)[0].X, Across(near)[0].Y, 210);
            Act(close, 1, st => Battle.Move(st, Across(near), -1));
            BattleState screened = Watched(ownBetween: true);
            Put(screened, 1, "LTR", Camp.Brown, Across(mid)[0].X, Across(mid)[0].Y, 210);
            Act(screened, 1, st => Battle.Move(st, Across(mid), -1));
            BattleState walled = Watched(wall: true);
            Put(walled, 1, "LTR", Camp.Brown, Across(mid)[0].X, Across(mid)[0].Y, 210);
            Act(walled, 1, st => Battle.Move(st, Across(mid), -1));
            check("the axis wants a clear hex between, and stops at its own hull and at a wall",
                  close.Unit(0)!.Ambushing && close.Unit(1)!.Fate == UnitFate.Alive
                  && screened.Unit(0)!.Ambushing && screened.Unit(1)!.Fate == UnitFate.Alive
                  && walled.Unit(0)!.Ambushing && walled.Unit(1)!.Fate == UnitFate.Alive,
                  $"beside: {Said(close)}; own: {Said(screened)}; wall: {Said(walled)}");
            // shoved on to the axis by a green heavy's ram: crossing all the same
            BattleState shoved = Watched();
            Vector2I from = HexField.Step(mid, 30), rammer = HexField.Step(from, 30);
            Put(shoved, 1, "LTR", Camp.Brown, from.X, from.Y, 90);
            Put(shoved, 3, "HTR", Camp.Green, rammer.X, rammer.Y, 210);
            Act(shoved, 3, st => Battle.Ram(st, from));
            check("an enemy shoved on to the axis by a ram is fired on",
                  shoved.Unit(1)!.Cell == mid && shoved.Unit(1)!.Fate == UnitFate.Knocked && !shoved.Unit(0)!.Ambushing,
                  Said(shoved));
            // the medium's sector: one round - an HT taking it on the front
            // drives on, and the next hex of an axis has nothing to answer it
            BattleState sector = Watched(axis2: 30);
            Vector2I top = HexField.Step(mid, 90);
            Put(sector, 1, "HTR", Camp.Brown, top.X, top.Y, 270);
            Act(sector, 1, st => Battle.Move(st, new[] { top, mid, new Vector2I(4, 6) }, -1));
            Vector2I side = HexField.Step(HexField.Step(new Vector2I(4, 8), 30), 30);
            BattleState second = Watched(axis2: 30);
            Put(second, 1, "LTR", Camp.Brown, HexField.Step(side, 90).X, HexField.Step(side, 90).Y, 270);
            Act(second, 1, st => Battle.Move(st, new[] { HexField.Step(side, 90), side }, -1));
            check("the medium watches two axes beside each other with one round",
                  sector.Events.Count(e => e.Kind == BattleEventKind.AmbushFired) == 1
                  && sector.Unit(1)!.Fate == UnitFate.Alive && sector.Unit(1)!.Cell == new Vector2I(4, 6)
                  && second.Events.Any(e => e.Kind == BattleEventKind.AmbushFired && e.Value == 30),
                  $"sector: {Said(sector)}; second axis: {Said(second)}");
            // set: the attack of the turn, the turn spent; any order lifts it
            BattleState set = Probe(9, 10);
            Put(set, 0, "MTR", Camp.Green, 4, 8, 90);
            Put(set, 1, "TDR", Camp.Green, 2, 8, 90);
            Put(set, 2, "LTR", Camp.Brown, 0, 0, 90);
            string? sectorSet = null, apart = null, hull = null, after = null;
            Act(set, 0, st => sectorSet = Battle.Ambush(st, 90, 30));
            bool spent = set.Fired && set.Drove && set.Bonus;
            set.Fired = set.Drove = set.Bonus = false;
            Act(set, 1, st => hull = Battle.Ambush(st, 30));
            set.Fired = false;
            Act(set, 0, st => apart = Battle.Ambush(st, 90, 210));
            Act(set, 0, st => Battle.Turn(st, 30));
            bool lifted = !set.Unit(0)!.Ambushing && set.Events.Any(e => e.Kind == BattleEventKind.AmbushLifted);
            set.Fired = true;
            Act(set, 0, st => after = Battle.Ambush(st, 90));
            check("an ambush is the turn's attack, the casemate's along its hull, the sector's of neighbours; an order lifts it",
                  sectorSet is null && spent && hull is not null && apart is not null && lifted && after is not null,
                  $"MT 90+30 {sectorSet ?? "ok"}, spent {spent}; TD across {hull}; MT 90+210 {apart}; lifted {lifted}; after the shot {after}");
        }

        // A copy is a copy.
        {
            BattleState s = Probe(9, 10);
            Put(s, 0, "LTR", Camp.Green, 4, 6, 90);
            BattleState c = s.Clone();
            c.Unit(0)!.Cell = new Vector2I(1, 1);
            check("a cloned state moves apart from the original", s.Unit(0)!.Cell == new Vector2I(4, 6), "");
        }
    }

    private static int Onward(int hull, int travelling) => (((2 * hull - travelling) % 360) + 360) % 360;

    /// <summary>A board of its own: flat ground, the woods, mines, deep water,
    /// walls (a cell and an edge) and levels asked for.</summary>
    private static BattleState Probe(int columns, int rows, Vector2I[]? woods = null, Vector2I[]? mines = null,
                                     Vector2I[]? deep = null, (Vector2I Cell, int Heading)[]? walls = null,
                                     int[]? levels = null)
    {
        var field = new HexField { Trees = false, Columns = columns, Rows = rows };
        var cover = new Cover[columns * rows];
        var ground = new Foundation[columns * rows];
        foreach (Vector2I w in woods ?? Array.Empty<Vector2I>())
            cover[w.Y * columns + w.X] = Cover.Forest;
        foreach (Vector2I m in mines ?? Array.Empty<Vector2I>())
            cover[m.Y * columns + m.X] = Cover.Minefield;
        foreach ((Vector2I c, int _) in walls ?? Array.Empty<(Vector2I, int)>())
            cover[c.Y * columns + c.X] = Cover.Walls;
        foreach (Vector2I d in deep ?? Array.Empty<Vector2I>())
            ground[d.Y * columns + d.X] = Foundation.Deep;
        field.SetGround(ground);
        field.SetCover(cover);
        if (levels is not null)
            field.SetRelief(levels);
        foreach ((Vector2I c, int h) in walls ?? Array.Empty<(Vector2I, int)>())
            field.SetSides(c, 1 << HexField.EdgeIndex(h));
        BattleBoard board = BattleBoard.Of(field);
        field.Free();
        return new BattleState { Board = board };
    }

    private static void Put(BattleState s, int id, string tag, Camp side, int q, int r, int heading,
                            Kit kit = Kit.None, UnitFate fate = UnitFate.Alive) =>
        s.Units.Add(new BattleUnit
        {
            Id = id, Tag = tag, Class = Lineup.ClassFor(tag), Side = side, Cell = new Vector2I(q, r),
            Heading = heading, Kit = kit, Fate = fate,
        });

    /// <summary>A one-unit green state with that unit taken (and one more
    /// brown standing, if asked), for a refusal to be read off.</summary>
    private static BattleState Started(BattleState s, string tag, int q, int r, int heading,
                                       (string Tag, int Q, int R)? extra = null)
    {
        Put(s, 0, tag, Camp.Green, q, r, heading);
        Put(s, 1, extra?.Tag ?? "LTR", Camp.Brown, extra?.Q ?? 0, extra?.R ?? 0, 270);
        Battle.Take(s, 0);
        return s;
    }

    private static void Act(BattleState s, int id, Func<BattleState, string?> act)
    {
        s.Turn = s.Unit(id)!.Side;
        s.Active = null;
        if (Battle.Take(s, id) is { } no || act(s) is { } refused)
            s.Events.Add(new BattleEvent(BattleEventKind.Spent, id, default, 0, "REFUSED"));
    }

    private static string Said(BattleState s) => string.Join(", ", s.Events.Select(e => e.ToString()));

    /// <summary>So many turns passed with nothing done.</summary>
    private static void Turns(BattleState s, int n)
    {
        for (int i = 0; i < n; i++)
            Battle.EndTurn(s);
    }
}
