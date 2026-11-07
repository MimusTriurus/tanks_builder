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

        // Д6, GameModes.md «Пат — ничья»: so many whole rounds without a hit on
        // a tank and the match is drawn - counted from the first contact
        // (decided 2026-11-05): before it, passes never draw; a round into a
        // wall is no contact; a round on a tank - glanced off the front or
        // not - starts the clock and puts it back.
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
            Turns(passes, 4 * quiet);
            BattleState walled = Facing(true);
            Turns(walled, 2);
            Act(walled, 0, st => Battle.Fire(st, new Vector2I(4, 3)));
            bool wallHit = walled.Unit(1)!.Fate == UnitFate.Alive && walled.LastStrike == 0;
            Turns(walled, 4 * quiet);
            BattleState struck = Facing(false);
            Turns(struck, 4);
            Act(struck, 0, st => Battle.Fire(st, new Vector2I(4, 5)));
            int strikeRound = struck.LastStrike;
            Turns(struck, 2 * quiet + 1);
            bool held = !struck.Drawn;
            Turns(struck, 1);
            check($"{quiet} rounds without a hit on a tank draw the match, from the first contact: passes and a wall's shot "
                  + "never draw, a tank's hit starts the clock",
                  !passes.Drawn && passes.QuietRounds == 0
                  && wallHit && !walled.Drawn && strikeRound == 3 && held && struck.Drawn
                  && struck.Events.Any(e => e.Kind == BattleEventKind.Drawn),
                  $"passes: at {passes.Round} drawn {passes.Drawn}; wall: {wallHit} drawn {walled.Drawn}; "
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

        // Д8, upgrades.md «Дымовая шашка», field.md «дым»: an HM at (4,7)
        // smokes (4,6); the medium behind it at (4,8) fires up the column.
        {
            var at = new Vector2I(4, 7);
            Vector2I wood = HexField.Step(at, 150), below = HexField.Step(at, 270);
            BattleState s = Probe(9, 10, woods: new[] { wood });
            Put(s, 0, "HMR", Camp.Green, at.X, at.Y, 90, kit: Kit.Smoke);
            Put(s, 1, "LTR", Camp.Brown, 4, 2, 90);
            Put(s, 2, "MTR", Camp.Green, below.X, below.Y, 90);
            string? onWood = null, onTank = null, far = null, laid = null, noKit = null;
            Act(s, 0, st => onWood = Battle.Smoke(st, wood));
            Act(s, 0, st => onTank = Battle.Smoke(st, below));
            Act(s, 0, st => far = Battle.Smoke(st, new Vector2I(4, 5)));
            Act(s, 0, st => laid = Battle.Smoke(st, new Vector2I(4, 6)));
            s.Bonus = false;
            Act(s, 2, st => noKit = Battle.Smoke(st, HexField.Step(below, 330)));
            check("the grenade lays smoke on a free hex beside, not on a wood, a tank, further off, or without the kit",
                  laid is null && s.Board.Smoked(new Vector2I(4, 6)) && onWood is not null && onTank is not null
                  && far is not null && noKit is not null,
                  $"wood {onWood}; on a tank {onTank}; (4,5) {far}; laid {laid ?? "ok"}; no kit {noKit}");

            // a round up the column stops in it; the mortar's goes over
            BattleState shot = Probe(9, 10);
            Put(shot, 0, "MTR", Camp.Green, 4, 8, 90);
            Put(shot, 1, "LTR", Camp.Brown, 4, 4, 90);
            shot.Board.SetSmoke(new Vector2I(4, 6), true);
            shot.Smoke[new Vector2I(4, 6)] = 2;
            Act(shot, 0, st => Battle.Fire(st, new Vector2I(4, 4)));
            bool stopped = shot.Unit(1)!.Fate == UnitFate.Alive
                           && shot.Events.Any(e => e.Kind == BattleEventKind.Spent && e.Cell == new Vector2I(4, 6));
            BattleState lob = Probe(9, 10);
            Put(lob, 0, "HMR", Camp.Green, 4, 8, 90);
            Put(lob, 1, "LTR", Camp.Brown, 4, 4, 90);
            lob.Board.SetSmoke(new Vector2I(4, 6), true);
            lob.Smoke[new Vector2I(4, 6)] = 2;
            Act(lob, 0, st => Battle.Fire(st, new Vector2I(4, 4)));
            // and an ambush's axis through it sees nothing
            BattleState watch = Probe(9, 10);
            Put(watch, 0, "MTR", Camp.Green, 4, 8, 90);
            watch.Unit(0)!.Watch = 90;
            watch.Board.SetSmoke(new Vector2I(4, 6), true);
            watch.Smoke[new Vector2I(4, 6)] = 2;
            var mid = new Vector2I(4, 5);
            Put(watch, 1, "LTR", Camp.Brown, HexField.Step(mid, 30).X, HexField.Step(mid, 30).Y, 210);
            Act(watch, 1, st => Battle.Move(st, new[] { HexField.Step(mid, 30), mid, HexField.Step(mid, 210) }, -1));
            check("smoke stops a gun's round and an ambush's axis; the mortar's bomb goes over it",
                  stopped && lob.Unit(1)!.Fate == UnitFate.Knocked && watch.Unit(0)!.Ambushing
                  && watch.Unit(1)!.Fate == UnitFate.Alive,
                  $"gun: {Said(shot)}; mortar: {Said(lob)}; ambush: {Said(watch)}");

            // a unit coming on to it disperses it; left alone it goes at the second round's end
            BattleState driven = Probe(9, 10);
            Put(driven, 0, "LTR", Camp.Green, 4, 8, 90);
            Put(driven, 1, "LTR", Camp.Brown, 0, 0, 90);
            driven.Board.SetSmoke(new Vector2I(4, 7), true);
            driven.Smoke[new Vector2I(4, 7)] = 2;
            Act(driven, 0, st => Battle.Move(st, new[] { new Vector2I(4, 7), new Vector2I(4, 6) }, -1));
            BattleState left = Probe(9, 10);
            Put(left, 0, "HMR", Camp.Green, 4, 8, 90, kit: Kit.Smoke);
            Put(left, 1, "LTR", Camp.Brown, 0, 0, 90);
            Act(left, 0, st => Battle.Smoke(st, new Vector2I(4, 7)));
            Turns(left, 2);
            bool afterOne = left.Board.Smoked(new Vector2I(4, 7));
            Turns(left, 2);
            check("smoke is dispersed by a unit driving through it, and blown away at the second round's end",
                  !driven.Board.Smoked(new Vector2I(4, 7)) && driven.Unit(0)!.Cell == new Vector2I(4, 6)
                  && afterOne && !left.Board.Smoked(new Vector2I(4, 7)),
                  $"driven: {Said(driven)}; after a round {afterOne}, after two {left.Board.Smoked(new Vector2I(4, 7))}");
        }

        // upgrades.md «Дымовой снаряд» (2026-11-02): a green medium at (4,8)
        // fires up the column. The round lays smoke on an empty hex its line of
        // fire reaches, and it is the shot of the turn; not past a wood or a
        // hull, not on a wood or a tank, not without the kit. The mortar's goes
        // over the wood, 3 to 5 out only.
        {
            BattleState Shot(string tag, Kit kit, Vector2I aim, out string? why, Vector2I[]? woods = null, bool hull = false)
            {
                BattleState t = Probe(9, 10, woods: woods);
                Put(t, 0, tag, Camp.Green, 4, 8, 90, kit: kit);
                Put(t, 1, "LTR", Camp.Brown, 0, 0, 90);
                if (hull)
                    Put(t, 2, "LTR", Camp.Brown, 4, 6, 270);
                string? said = null;
                Act(t, 0, st => said = Battle.Fire(st, aim, smoke: true));
                why = said;
                return t;
            }
            Vector2I near = new(4, 5), wood = new(4, 6);
            BattleState laid = Shot("MTR", Kit.SmokeShell, near, out string? laidWhy);
            string? again = null;
            Act(laid, 0, st => again = Battle.Fire(st, new Vector2I(4, 4)));
            Shot("MTR", Kit.None, near, out string? noKit);
            Shot("MTR", Kit.SmokeShell, near, out string? pastWood, woods: new[] { wood });
            Shot("MTR", Kit.SmokeShell, wood, out string? onWood, woods: new[] { wood });
            Shot("MTR", Kit.SmokeShell, near, out string? pastHull, hull: true);
            Shot("MTR", Kit.SmokeShell, wood, out string? onHull, hull: true);
            BattleState lob = Shot("HMR", Kit.SmokeShell, new Vector2I(4, 4), out string? lobWhy, woods: new[] { wood });
            Shot("HMR", Kit.SmokeShell, wood, out string? lobNear);
            check("the smoke round: smoke on an empty hex the line of fire reaches, the turn's shot; not past or on a wood or a hull, not without the kit; the mortar's over the wood, 3 to 5 out",
                  laidWhy is null && laid.Board.Smoked(near) && laid.Fired && again is not null
                  && laid.Events.Any(e => e.Kind == BattleEventKind.SmokeLaid && e.Note == "round")
                  && noKit is not null && pastWood is not null && onWood is not null && pastHull is not null && onHull is not null
                  && lobWhy is null && lob.Board.Smoked(new Vector2I(4, 4)) && lobNear is not null,
                  $"laid {laidWhy ?? "ok"}, again {again}; no kit {noKit}; past the wood {pastWood}; on it {onWood}; "
                  + $"past a hull {pastHull}; on it {onHull}; mortar {lobWhy ?? "ok"}, 2 out {lobNear}; {Said(laid)}");
        }

        // upgrades.md «Противоминная защита» (2026-11-02): driven on nose first
        // or backed on, the mine goes off and is spent, the hull is alight and
        // not knocked out, and the way ends on the mine's hex; shoved on by a
        // ram, the same; without it, knocked out as ever.
        {
            var mine = new Vector2I(4, 7);
            BattleState ahead = Probe(9, 10, mines: new[] { mine });
            Put(ahead, 0, "MTR", Camp.Green, 4, 8, 90, kit: Kit.MineGuard);
            Put(ahead, 1, "LTR", Camp.Brown, 0, 0, 90);
            Act(ahead, 0, st => Battle.Move(st, new[] { mine, new Vector2I(4, 6) }, -1));
            BattleState back = Probe(9, 10, mines: new[] { mine });
            Put(back, 0, "MTR", Camp.Green, 4, 8, 270, kit: Kit.MineGuard);
            Put(back, 1, "LTR", Camp.Brown, 0, 0, 90);
            Act(back, 0, st => Battle.Move(st, new[] { mine }, -1));
            BattleState shoved = Probe(9, 10, mines: new[] { mine });
            Put(shoved, 0, "HTR", Camp.Brown, 4, 9, 90);
            Put(shoved, 1, "MTR", Camp.Green, 4, 8, 270, kit: Kit.MineGuard);
            Act(shoved, 0, st => Battle.Ram(st, new Vector2I(4, 8)));
            BattleState bare = Probe(9, 10, mines: new[] { mine });
            Put(bare, 0, "MTR", Camp.Green, 4, 8, 90);
            Put(bare, 1, "LTR", Camp.Brown, 0, 0, 90);
            Act(bare, 0, st => Battle.Move(st, new[] { mine, new Vector2I(4, 6) }, -1));
            static bool Guarded(BattleState s, int id, Vector2I at) =>
                s.Unit(id)! is { Fate: UnitFate.Alive, Burning: true } u && u.Cell == at
                && s.Board.CoverStateAt(at) == CoverState.Cleared
                && s.Events.Any(e => e.Kind == BattleEventKind.MineBlown) && !s.Events.Any(e => e.Note == "REFUSED");
            check("the mine guard: the mine goes off under the hull nose or tail first or shoved - alight, not knocked out, the way ends on it",
                  Guarded(ahead, 0, mine) && Guarded(back, 0, mine) && Guarded(shoved, 1, mine)
                  && bare.Unit(0)!.Fate == UnitFate.Knocked && bare.Unit(0)!.Cell == mine,
                  $"ahead: {Said(ahead)}; back: {Said(back)}; shoved: {Said(shoved)}; bare: {Said(bare)}");
        }

        // Д8, upgrades.md «Зажигательный снаряд»: the medium at (4,8) fires up
        // the column - into a heavy's front it holds, a light's rear it does not;
        // into a wood, and the mortar's bomb on one.
        {
            BattleState held = Probe(9, 10);
            Put(held, 0, "MTR", Camp.Green, 4, 8, 90, kit: Kit.Incendiary);
            Put(held, 1, "HTR", Camp.Brown, 4, 5, 270);
            Act(held, 0, st => Battle.Fire(st, new Vector2I(4, 5), incendiary: true));
            BattleState plain = Probe(9, 10);
            Put(plain, 0, "MTR", Camp.Green, 4, 8, 90, kit: Kit.Incendiary);
            Put(plain, 1, "HTR", Camp.Brown, 4, 5, 270);
            Act(plain, 0, st => Battle.Fire(st, new Vector2I(4, 5)));
            BattleState holed = Probe(9, 10);
            Put(holed, 0, "MTR", Camp.Green, 4, 8, 90, kit: Kit.Incendiary);
            Put(holed, 1, "LTR", Camp.Brown, 4, 4, 90);
            Act(holed, 0, st => Battle.Fire(st, new Vector2I(4, 4), incendiary: true));
            string? noKit = Battle.Fire(Started(Probe(9, 10), "MTR", 4, 8, 90, extra: ("HTR", 4, 5)),
                                        new Vector2I(4, 5), incendiary: true);
            check("an incendiary round sets alight a hull it does not pierce; one it pierces is knocked out; none without the kit",
                  held.Unit(1)!.Fate == UnitFate.Alive && held.Unit(1)!.Burning && !plain.Unit(1)!.Burning
                  && holed.Unit(1)!.Fate == UnitFate.Knocked && !holed.Unit(1)!.Burning && noKit is not null,
                  $"held: {Said(held)}; plain: {Said(plain)}; holed: {Said(holed)}; no kit: {noKit}");

            var wood = new Vector2I(4, 6);
            BattleState gun = Probe(9, 10, woods: new[] { wood });
            Put(gun, 0, "MTR", Camp.Green, 4, 8, 90, kit: Kit.Incendiary);
            Put(gun, 1, "LTR", Camp.Brown, 0, 0, 90);
            Act(gun, 0, st => Battle.Fire(st, wood, incendiary: true));
            BattleState cold = Probe(9, 10, woods: new[] { wood });
            Put(cold, 0, "MTR", Camp.Green, 4, 8, 90);
            Put(cold, 1, "LTR", Camp.Brown, 0, 0, 90);
            Act(cold, 0, st => Battle.Fire(st, wood));
            var far = new Vector2I(4, 5);
            BattleState bomb = Probe(9, 10, woods: new[] { far });
            Put(bomb, 0, "HMR", Camp.Green, 4, 8, 90, kit: Kit.Incendiary);
            Put(bomb, 1, "LTR", Camp.Brown, 0, 0, 90);
            Act(bomb, 0, st => Battle.Fire(st, far, incendiary: true));
            check("an incendiary round sets the wood it is spent in alight, the mortar's bomb too; a plain one does not",
                  gun.Board.CoverStateAt(wood) == CoverState.Burning && gun.Fires.ContainsKey(wood)
                  && cold.Board.CoverStateAt(wood) == CoverState.Intact
                  && bomb.Board.CoverStateAt(far) == CoverState.Burning,
                  $"gun: {Said(gun)}; plain: {Said(cold)}; bomb: {Said(bomb)}");
        }

        // Д8, upgrades.md «Широкие гусеницы»: the medium at (4,8) up the column
        // through mud on (4,7) - stopped in it without, on past it with; begun
        // in it, one step without and the speed with.
        {
            var mudCell = new Vector2I(4, 7);
            BattleState field = Probe(9, 10, mud: new[] { mudCell });
            Steps.Mover plain = Steps.Mover.Of(MovementProfile.Medium);
            Steps.Mover wide = Steps.Kitted(Steps.Mover.Of(MovementProfile.Medium), Kit.WideTracks);
            Steps.Reach without = Steps.Plan(field.Board, new Steps.Pose(new Vector2I(4, 8), 90), plain);
            Steps.Reach with = Steps.Plan(field.Board, new Steps.Pose(new Vector2I(4, 8), 90), wide);
            int inMud = Steps.BudgetAt(field.Board, mudCell, plain), inMudWide = Steps.BudgetAt(field.Board, mudCell, wide);
            check("wide tracks drive through mud as ground, and a move begun in it has the full budget",
                  without.Reaches(mudCell) && !without.Reaches(new Vector2I(4, 6))
                  && with.Reaches(new Vector2I(4, 5)) && inMud == 1 && inMudWide == MovementProfile.Medium.Steps,
                  $"plain to (4,6) {without.Reaches(new Vector2I(4, 6))}, wide to (4,5) {with.Reaches(new Vector2I(4, 5))}; "
                  + $"budget in mud {inMud} / {inMudWide}");
        }

        // Д8, upgrades.md «Задний ход»: the medium at (4,4) facing up backs two
        // cells, and bends between them; without the gear one cell, nothing more.
        {
            var start = new Vector2I(4, 4);
            Vector2I one = HexField.Step(start, 270), two = HexField.Step(one, 270);
            // backed one, turned to 30, backed again: tail first along 210
            Vector2I bend = HexField.Step(one, 210);
            BattleState field = Probe(9, 10);
            Steps.Mover plain = Steps.Mover.Of(MovementProfile.Medium);
            Steps.Mover gear = Steps.Kitted(Steps.Mover.Of(MovementProfile.Medium), Kit.Reverse);
            Steps.Reach without = Steps.Plan(field.Board, new Steps.Pose(start, 90), plain);
            Steps.Reach with = Steps.Plan(field.Board, new Steps.Pose(start, 90), gear);
            Steps.Route? straight = with.RouteTo(two), bent = with.RouteTo(bend);
            BattleState go = Probe(9, 10);
            Put(go, 0, "MTR", Camp.Green, start.X, start.Y, 90, kit: Kit.Reverse);
            Put(go, 1, "LTR", Camp.Brown, 0, 0, 90);
            Act(go, 0, st => Battle.Go(st, bend));
            BattleUnit mt = go.Unit(0)!;
            check("the reverse gear backs two cells and turns between them; without it one cell only",
                  without.Reaches(one) && without.RouteTo(one)!.Back && without.RouteTo(one)!.Final
                  && (without.RouteTo(two) is null || !without.RouteTo(two)!.Back)
                  && straight is { Back: true, Spent: 2 } && bent is { Back: true, Spent: 3 }
                  && mt.Cell == bend && mt.Heading == 30,
                  $"plain to {two}: {without.RouteTo(two)}; gear: {straight}; bent: {bent}; went {Said(go)}");
        }

        // Д8, upgrades.md «Двойной поворот»: the first 60° on the spot is free -
        // the medium (4 steps) reaches four cells out along 30, not three.
        // upgrades.md «Двойной поворот» (2026-11-02): a bonus act - 60° with no
        // step, after the move (in mud too, its one step spent on the cell),
        // the attack still to be had; the move is over with it, a second is
        // the bonus act spent, 120° is no double turn, and without the kit
        // there is none. The move's own turns cost a step each, kit or not.
        {
            var start = new Vector2I(4, 6);
            var mud = new Vector2I(4, 5);
            BattleState s = Probe(9, 10, mud: new[] { mud });
            Put(s, 0, "TDR", Camp.Green, start.X, start.Y, 90, kit: Kit.DoubleTurn);
            Put(s, 1, "LTR", Camp.Brown, 0, 0, 90);
            string? wide = null, pivot = null, again = null, after = null;
            Act(s, 0, st => Battle.Move(st, new[] { mud }, -1));
            wide = Battle.Pivot(s, 210);
            pivot = Battle.Pivot(s, 30);
            int heading = s.Unit(0)!.Heading;
            bool fire = !s.Fired && s.Drove && s.Bonus;
            again = Battle.Pivot(s, 330);
            after = Battle.Go(s, new Vector2I(4, 4));
            BattleState none = Probe(9, 10);
            Put(none, 0, "TDR", Camp.Green, start.X, start.Y, 90);
            Put(none, 1, "LTR", Camp.Brown, 0, 0, 90);
            string? bare = null;
            Act(none, 0, st => bare = Battle.Pivot(st, 30));
            Steps.Mover twice = Steps.Kitted(Steps.Mover.Of(MovementProfile.Medium), Kit.DoubleTurn);
            check("the double turn: a bonus act of 60° with no step after the move; one a turn, not 120°, not without the kit; the move's turns cost as ever",
                  wide is not null && pivot is null && heading == 30 && fire && again is not null && after is not null
                  && bare is not null && Steps.TurnCost(90, 30, twice) == 1,
                  $"120°: {wide}; pivot {pivot ?? "ok"} to h{heading}; again {again}; move after {after}; without the kit {bare}; {Said(s)}");
        }

        // upgrades.md «Вилка 3–6» (2026-11-04): the mortar at (4,8) on a light
        // six up the column - out of its band without the bracket, a hit with it.
        {
            string? plain = null, bracket = null;
            BattleState far = Probe(9, 10);
            Put(far, 0, "HMR", Camp.Green, 4, 8, 90);
            Put(far, 1, "LTR", Camp.Brown, 4, 2, 90);
            Act(far, 0, st => plain = Battle.Fire(st, new Vector2I(4, 2)));
            BattleState forked = Probe(9, 10);
            Put(forked, 0, "HMR", Camp.Green, 4, 8, 90, kit: Kit.Bracket);
            Put(forked, 1, "LTR", Camp.Brown, 4, 2, 90);
            Act(forked, 0, st => bracket = Battle.Fire(st, new Vector2I(4, 2)));
            string? near = null;
            BattleState close = Probe(9, 10);
            Put(close, 0, "HMR", Camp.Green, 4, 8, 90, kit: Kit.Bracket);
            Put(close, 1, "LTR", Camp.Brown, 4, 6, 90);
            Act(close, 0, st => near = Battle.Fire(st, new Vector2I(4, 6)));
            check("the 3-6 bracket: the mortar throws six with it, not without; two is still too near",
                  plain is not null && bracket is null && forked.Unit(1)!.Fate == UnitFate.Knocked && near is not null,
                  $"without {plain}; with {bracket ?? "ok"}: {Said(forked)}; at two {near}");
        }

        // upgrades.md «Сквозь лес» (2026-11-04): the destroyer at (4,8) on a
        // light at (4,4) behind a wood at (4,6) - spent in the wood without it;
        // with it through, the wood whole; two woods in a row stop it.
        {
            var wood = new Vector2I(4, 6);
            BattleState Shot(Kit kit, params Vector2I[] woods)
            {
                BattleState t = Probe(9, 10, woods: woods);
                Put(t, 0, "TDR", Camp.Green, 4, 8, 90, kit: kit);
                Put(t, 1, "LTR", Camp.Brown, 4, 4, 90);
                Act(t, 0, st => Battle.Fire(st, new Vector2I(4, 4)));
                return t;
            }
            BattleState bare = Shot(Kit.None, wood), through = Shot(Kit.ThroughWood, wood),
                        twice = Shot(Kit.ThroughWood, wood, new Vector2I(4, 5));
            check("through the wood: the destroyer's pass on a wood, the wood whole, the round on; a second wood stops it",
                  bare.Unit(1)!.Fate == UnitFate.Alive
                  && through.Unit(1)!.Fate == UnitFate.Knocked && through.Board.CoverStateAt(wood) == CoverState.Intact
                  && through.Events.Any(e => e.Kind == BattleEventKind.Passed && e.Note == "wood")
                  && twice.Unit(1)!.Fate == UnitFate.Alive,
                  $"bare: {Said(bare)}; through: {Said(through)}; two woods: {Said(twice)}");
        }

        // upgrades.md «Таранный отвал» (2026-11-04): a green heavy with it rams
        // a brown medium - in brown's next turn the medium may not move, but
        // fires; its activation over, it moves again.
        {
            BattleState s = Probe(9, 10);
            Put(s, 0, "HTR", Camp.Green, 4, 8, 90, kit: Kit.RamBlade);
            Put(s, 1, "MTR", Camp.Brown, 4, 7, 270);
            Act(s, 0, st => Battle.Ram(st, new Vector2I(4, 7)));
            bool pinned = s.Unit(1)!.Pinned && s.Events.Any(e => e.Kind == BattleEventKind.Pinned);
            Battle.EndTurn(s);
            string? stuck = null, fired = null, later = null;
            Act(s, 1, st => stuck = Battle.Go(st, new Vector2I(4, 5)));
            Act(s, 1, st => fired = Battle.Fire(st, new Vector2I(4, 3)));
            Battle.EndTurn(s);
            bool freed = !s.Unit(1)!.Pinned;
            Battle.EndTurn(s);
            Act(s, 1, st => later = Battle.Go(st, new Vector2I(4, 5)));
            check("the ram blade: the rammed hull has no move in its next activation, fires in it, and moves after",
                  pinned && stuck is not null && stuck.Contains("pinned") && fired is null && freed && later is null,
                  $"pinned {pinned}; move {stuck}; fire {fired ?? "ok"}; freed {freed}; later {later ?? "ok"}; {Said(s)}");
        }

        // upgrades.md «Толчок на два гекса» (2026-11-04): a green heavy at (4,9)
        // rams a brown medium at (4,8) - with it the medium lands on (4,6), the
        // heavy on (4,8); without it on (4,7); a tank on (4,6) stops it on
        // (4,7); a mine on (4,7) knocks it out there and it goes no further.
        {
            BattleState Rammed(Kit kit, Vector2I[]? mines = null, bool blocker = false)
            {
                BattleState t = Probe(9, 10, mines: mines);
                Put(t, 0, "HTR", Camp.Green, 4, 9, 90, kit: kit);
                Put(t, 1, "MTR", Camp.Brown, 4, 8, 270);
                if (blocker)
                    Put(t, 2, "LTR", Camp.Brown, 4, 6, 270);
                Act(t, 0, st => Battle.Ram(st, new Vector2I(4, 8)));
                return t;
            }
            BattleState two = Rammed(Kit.PushTwo), one = Rammed(Kit.None), held = Rammed(Kit.PushTwo, blocker: true),
                        mined = Rammed(Kit.PushTwo, mines: new[] { new Vector2I(4, 7) });
            check("the push of two: thrown two hexes, the heavy on the first; a tank behind or a mine on the first stops it there",
                  two.Unit(1)!.Cell == new Vector2I(4, 6) && two.Unit(0)!.Cell == new Vector2I(4, 8)
                  && one.Unit(1)!.Cell == new Vector2I(4, 7)
                  && held.Unit(1)!.Cell == new Vector2I(4, 7)
                  && mined.Unit(1)!.Cell == new Vector2I(4, 7) && mined.Unit(1)!.Fate == UnitFate.Knocked,
                  $"two: {Said(two)}; one: {Said(one)}; held: {Said(held)}; mined: {Said(mined)}");
        }

        // upgrades.md «Форсированный двигатель» (2026-11-04): a green medium at
        // (4,8) watches up; a brown medium crosses the axis at (4,5) - fired on
        // without it, not with it (the ambush kept); with it, stopping on the
        // axis is fired on; said after the move it is refused; the medium may
        // still move after saying it.
        {
            var mid = new Vector2I(4, 5);
            Vector2I[] across = { HexField.Step(mid, 30), mid, HexField.Step(mid, 210) };
            BattleState Crossed(Kit kit, bool say, Vector2I[] path)
            {
                BattleState t = Probe(9, 10);
                Put(t, 0, "MTR", Camp.Green, 4, 8, 90);
                t.Unit(0)!.Watch = 90;
                t.Unit(0)!.WatchShots = 1;
                Put(t, 1, "MTR", Camp.Brown, across[0].X, across[0].Y, 210, kit: kit);
                Act(t, 1, st => (say ? Battle.Overdrive(st) : null) ?? Battle.Move(st, path, -1));
                return t;
            }
            static bool Fired(BattleState t) => t.Events.Any(e => e.Kind == BattleEventKind.AmbushFired);
            static bool Refused(BattleState t) => t.Events.Any(e => e.Note == "REFUSED");
            BattleState plain = Crossed(Kit.None, false, across), forced = Crossed(Kit.Overdrive, true, across),
                        stopped = Crossed(Kit.Overdrive, true, new[] { across[1] });
            string? late = null;
            BattleState after = Probe(9, 10);
            Put(after, 0, "MTR", Camp.Brown, 4, 5, 90, kit: Kit.Overdrive);
            Put(after, 1, "LTR", Camp.Green, 0, 0, 90);
            Act(after, 0, st => Battle.Move(st, new[] { new Vector2I(4, 4) }, -1));
            late = Battle.Overdrive(after);
            check("the forced engine: an ambush's axis crossed draws no fire and the ambush stays; stopped on it, fired on; said before the move only",
                  Fired(plain) && !Fired(forced) && !Refused(forced) && forced.Unit(0)!.Ambushing
                  && forced.Unit(1)!.Cell == across[2] && Fired(stopped) && late is not null,
                  $"plain: {Said(plain)}; forced: {Said(forced)}; stopped: {Said(stopped)}; after the move: {late}");
        }

        // match.md «Структура хода», п. 2 (2026-11-07): the dash. Said before
        // the move, it gives up the attack for speed × 1.5 up - LT 8, MT and
        // TD 6, HT and HM 5. A green medium at (4,9) dashes six hexes up the
        // column to (4,3) and rams the brown light at (4,2): no shot, no
        // ambush after it, the turn kept for the move; not without the rule,
        // not after the move, not after the shot; with the forced engine yes,
        // with the shot on the move no.
        {
            static BattleState Dashing(string tag, Kit kit = Kit.None, bool on = true)
            {
                BattleState t = Probe(9, 10);
                t.DashOn = on;
                Put(t, 0, tag, Camp.Green, 4, 9, 90, kit: kit);
                Put(t, 1, "LTR", Camp.Brown, 4, 2, 270);
                Battle.Take(t, 0);
                return t;
            }
            static int Budget(BattleState t) => Battle.MoverOf(t, t.Unit(0)!).Speed;
            var budgets = new List<string>();
            bool speeds = true;
            foreach ((string tag, int plain, int dash) in new[] { ("LTR", 5, 8), ("MTR", 4, 6), ("TDR", 4, 6), ("HTR", 3, 5), ("HMR", 3, 5) })
            {
                BattleState t = Dashing(tag);
                int before = Budget(t);
                string? said = Battle.Dash(t);
                int after = Budget(t);
                budgets.Add($"{tag} {before}->{after}{(said is null ? "" : $" ({said})")}");
                speeds &= said is null && before == plain && after == dash;
            }
            BattleState m = Dashing("MTR");
            string? dashed = Battle.Dash(m);
            bool kept = !Battle.TurnDone(m);
            string? shot = Battle.Fire(m, new Vector2I(4, 2));
            string? ambush = Battle.Ambush(m, 90);
            var up = new List<Vector2I>();
            for (int r = 8; r >= 3; r--)
                up.Add(new Vector2I(4, r));
            string? went = Battle.Move(m, up, -1);
            Vector2I reached = m.Unit(0)!.Cell;
            string? rammed = Battle.Ram(m, new Vector2I(4, 2));
            BattleState off = Dashing("MTR", on: false), moved = Dashing("MTR"), lt = Dashing("LTR"),
                        forced = Dashing("MTR", Kit.Overdrive), onWay = Dashing("LTR", Kit.FireOnMove);
            string? noRule = Battle.Dash(off);
            Battle.Move(moved, new[] { new Vector2I(4, 8) }, -1);
            string? late = Battle.Dash(moved);
            lt.Fired = true;  // LT's free order: its shot already had before the move
            string? afterShot = Battle.Dash(lt);
            string? both = Battle.Overdrive(forced) ?? Battle.Dash(forced);
            string? noOnWay = Battle.Dash(onWay) is { } d ? $"dash: {d}" : Battle.OnTheWay(onWay, new Vector2I(4, 6), new Vector2I(4, 2));
            check("the dash: speed × 1.5 up, said before the move; no shot or ambush after it, the ram yes; not without the rule, after the move or the shot; with the forced engine, not with the shot on the move",
                  speeds && dashed is null && kept && shot is not null && ambush is not null && went is null && reached == new Vector2I(4, 3)
                  && rammed is null && m.Unit(0)!.Cell == new Vector2I(4, 2) && m.Unit(1)!.Cell == new Vector2I(4, 1)
                  && noRule is not null && late is not null && afterShot is not null
                  && both is null && Budget(forced) == 6 && noOnWay is not null && !noOnWay.StartsWith("dash:"),
                  $"budgets {string.Join(", ", budgets)}; dash {dashed}, kept {kept}, shot {shot}, ambush {ambush}, move {went}, ram {rammed} ({Said(m)}); "
                  + $"no rule {noRule}; after the move {late}; LT after its shot {afterShot}; with the forced engine {both}; the shot on the move {noOnWay}");
        }

        // upgrades.md «Выстрел на ходу» (2026-11-02): a green light drives up
        // the column from (4,8) to (4,4) and fires at a brown medium three hexes
        // off (4,6) on its 30 axis - on no axis from the start or the end. The
        // round flies as it stands on (4,6), between the steps, and the move
        // goes on; not with its hex off the way, not from a hex the target is off
        // the axis of, not without the kit, not said after the move; a mine
        // short of the hex stops it before it fires; the medium in ambush on that
        // axis fires first, as the light comes on to the hex.
        {
            var mid = new Vector2I(4, 6);
            Vector2I target = HexField.Step(HexField.Step(HexField.Step(mid, 30), 30), 30);
            Vector2I[] up = { new(4, 7), mid, new(4, 5), new(4, 4) };
            BattleState Run(Kit kit, Vector2I at, Vector2I[]? mines = null, bool watch = false)
            {
                BattleState t = Probe(9, 10, mines: mines);
                Put(t, 0, "LTR", Camp.Green, 4, 8, 90, kit: kit);
                Put(t, 1, "MTR", Camp.Brown, target.X, target.Y, 210);
                if (watch)
                {
                    t.Unit(1)!.Watch = 210;
                    t.Unit(1)!.WatchShots = 1;
                }
                Act(t, 0, st => Battle.OnTheWay(st, at, target) ?? Battle.Move(st, up, -1));
                return t;
            }
            static bool Refused(BattleState t) => t.Events.Any(e => e.Note == "REFUSED");
            static int First(BattleState t, Func<BattleEvent, bool> what) => t.Events.FindIndex(e => what(e));
            static bool OnTarget(BattleEvent e) => e.Unit == 1 && e.Kind is BattleEventKind.Hit or BattleEventKind.Glanced
                                                   or BattleEventKind.Ricochet or BattleEventKind.Knocked;
            BattleState shot = Run(Kit.FireOnMove, mid);
            int onMid = First(shot, e => e.Kind == BattleEventKind.Moved && e.Cell == mid);
            int past = First(shot, e => e.Kind == BattleEventKind.Moved && e.Cell == up[2]);
            int hit = First(shot, OnTarget);
            BattleState offWay = Run(Kit.FireOnMove, new Vector2I(2, 2)), offAxis = Run(Kit.FireOnMove, up[0]),
                        noKit = Run(Kit.None, mid), mined = Run(Kit.FireOnMove, mid, mines: new[] { up[0] }),
                        watched = Run(Kit.FireOnMove, mid, watch: true);
            BattleState late = Probe(9, 10);
            Put(late, 0, "LTR", Camp.Green, 4, 8, 90, kit: Kit.FireOnMove);
            Put(late, 1, "MTR", Camp.Brown, target.X, target.Y, 210);
            string? lateWhy = null;
            Act(late, 0, st => Battle.Move(st, new[] { up[0] }, -1));
            lateWhy = Battle.OnTheWay(late, mid, target);
            int ambushed = First(watched, e => e.Kind == BattleEventKind.AmbushFired);
            int answered = First(watched, OnTarget);
            check("the shot on the move: fired on its hex between the steps, the move goes on; not off the way, not off the axis, not without the kit, not after the move; a mine short of it stops it; an ambush on the axis fires first",
                  !Refused(shot) && shot.Fired && onMid >= 0 && hit > onMid && past > hit && shot.Unit(0)!.Cell == up[3]
                  && Refused(offWay) && offWay.Unit(0)!.Cell == new Vector2I(4, 8) && Refused(offAxis) && Refused(noKit)
                  && lateWhy is not null
                  && mined.Unit(0)!.Fate != UnitFate.Alive && First(mined, OnTarget) < 0
                  && ambushed >= 0 && (answered < 0 || answered > ambushed),
                  $"shot: {Said(shot)}; off the way: {Said(offWay)}; off the axis: {Said(offAxis)}; no kit: {Said(noKit)}; "
                  + $"after the move: {lateWhy}; mined: {Said(mined)}; watched: {Said(watched)}");
        }

        // units/mt.md «Секторная засада» (2026-11-02) and upgrades.md «Широкий
        // сектор»: a green medium at (4,5) set by the rules; brown lights step
        // on to a hex three out on an axis, from off every axis. The sector
        // fires once an axis: the axis spent is struck off, the other watches
        // on. The wide sector watches three side by side and fires any two,
        // then is off; not without the kit, not three apart.
        {
            BattleState Watching(Kit kit, int a, int b, int c, out string? why)
            {
                BattleState t = Probe(9, 10);
                Put(t, 0, "MTR", Camp.Green, 4, 5, 90, kit: kit);
                string? said = null;
                Act(t, 0, st => said = Battle.Ambush(st, a, b, c));
                why = said;
                return t;
            }
            static void Come(BattleState t, int id, int axis, int out3 = 3)
            {
                Vector2I c = new(4, 5);
                for (int k = 0; k < out3; k++)
                    c = HexField.Step(c, axis);
                Vector2I n = HexField.Step(c, (axis + 120) % 360);
                Put(t, id, "LTR", Camp.Brown, n.X, n.Y, (axis + 300) % 360);
                // each light its own turn
                t.Drove = t.Fired = t.Bonus = false;
                Act(t, id, st => Battle.Move(st, new[] { c }, -1));
            }
            static int Rounds(BattleState t) => t.Events.Count(e => e.Kind == BattleEventKind.AmbushFired);
            BattleState sector = Watching(Kit.None, 90, 30, -1, out string? sectorWhy);
            Come(sector, 1, 90);
            bool struck = sector.Unit(0)!.Ambushing && sector.Unit(0)!.Watch == 30 && sector.Unit(0)!.Watch2 < 0;
            Come(sector, 2, 90, 2);
            int again = Rounds(sector);
            Come(sector, 3, 30);
            BattleState wide = Watching(Kit.WideSector, 90, 30, 150, out string? wideWhy);
            Come(wide, 1, 150);
            Come(wide, 2, 30);
            Come(wide, 3, 90);
            Watching(Kit.None, 90, 30, 150, out string? noKit);
            Watching(Kit.WideSector, 90, 30, 210, out string? apart);
            check("the sector fires once an axis, the other watching on; the wide sector watches three side by side and fires any two; not without the kit, not apart",
                  sectorWhy is null && struck && again == 1 && Rounds(sector) == 2 && !sector.Unit(0)!.Ambushing
                  && wideWhy is null && Rounds(wide) == 2 && !wide.Unit(0)!.Ambushing
                  && noKit is not null && apart is not null,
                  $"sector: {Said(sector)}; wide {wideWhy ?? "ok"}: {Said(wide)}; no kit {noKit}; apart {apart}");
        }

        // Д8, upgrades.md «Противоосколочный подбой»: the mortar's bomb sets off
        // a knocked medium at (4,5); of the two lights beside it the lined one
        // does not catch.
        {
            BattleState s = Probe(9, 10);
            Put(s, 0, "HMR", Camp.Green, 4, 8, 90);
            Put(s, 1, "MTR", Camp.Brown, 4, 5, 90, fate: UnitFate.Knocked);
            Put(s, 2, "LTR", Camp.Brown, 4, 4, 90, kit: Kit.SpallLiner);
            Put(s, 3, "LTR", Camp.Brown, 4, 6, 90);
            Act(s, 0, st => Battle.Fire(st, new Vector2I(4, 5)));
            check("a blast beside does not set a hull with the spall liner alight; one without catches",
                  s.Unit(1)!.Fate == UnitFate.Destroyed && s.Unit(2)!.Fate == UnitFate.Alive && !s.Unit(2)!.Burning
                  && s.Unit(3)!.Burning,
                  Said(s));
        }

        // upgrades.md «Автоматическое тушение» (2026-11-02): three greens
        // burning from the last turn, all with it, one without - each with it
        // goes out at the turn's end. The one that moved pays with this turn's
        // bonus act; the one left alone (in ambush) keeps its watch and owes
        // it - its next activation has none, and pays the debt; the third, in
        // a burning wood, goes up; the one without, as ever.
        {
            var wood = new Vector2I(4, 8);
            BattleState s = Probe(9, 10, woods: new[] { wood });
            s.Board.SetCoverState(wood, CoverState.Burning);
            s.Fires[wood] = 2;
            Put(s, 0, "MTR", Camp.Green, 1, 8, 90, kit: Kit.Extinguisher);
            Put(s, 1, "MTR", Camp.Green, 7, 8, 90, kit: Kit.Extinguisher);
            Put(s, 2, "MTR", Camp.Green, wood.X, wood.Y, 90, kit: Kit.Extinguisher);
            Put(s, 3, "MTR", Camp.Green, 7, 3, 90);
            Put(s, 4, "LTR", Camp.Brown, 4, 0, 270);
            s.TurnNo = 1;
            foreach (int id in new[] { 0, 1, 2, 3 })
            {
                s.Unit(id)!.Burning = true;
                s.Unit(id)!.BurnSince = 0;
            }
            s.Unit(0)!.Watch = 90;
            Act(s, 1, st => Battle.Move(st, new[] { new Vector2I(7, 7) }, -1));
            Battle.EndTurn(s);
            bool outs = s.Unit(0)! is { Fate: UnitFate.Alive, Burning: false, Ambushing: true, OwesBonus: true }
                        && s.Unit(1)! is { Fate: UnitFate.Alive, Burning: false, OwesBonus: false }
                        && s.Unit(2)!.Fate == UnitFate.Destroyed && s.Unit(3)!.Fate == UnitFate.Destroyed;
            // the brown's turn, then green's again: the debtor taken, no bonus act for it
            Battle.EndTurn(s);
            string? owed = null;
            Act(s, 0, st => owed = Battle.Ram(st, new Vector2I(1, 7)));
            bool kept = !Battle.BonusLeft(s, s.Unit(0)!);
            Act(s, 0, st => Battle.Move(st, new[] { new Vector2I(1, 7) }, -1));
            Battle.EndTurn(s);
            check("the auto-extinguisher puts out every unit with it but in a burning wood; this turn's bonus act or the next activation's pays",
                  outs && owed is not null && owed.Contains("owed") && kept && !s.Unit(0)!.OwesBonus,
                  $"{s.Say()}; refused {owed}; {Said(s)}");
        }

        // Д8, upgrades.md «Тепловизор»: smoke on (4,6) between the medium at
        // (4,8) and a light at (4,4) - its round and its ambush's axis go
        // through; the round at it does not.
        {
            var smoke = new Vector2I(4, 6);
            BattleState shot = Probe(9, 10);
            Put(shot, 0, "MTR", Camp.Green, 4, 8, 90, kit: Kit.Thermal);
            Put(shot, 1, "LTR", Camp.Brown, 4, 4, 90);
            shot.Board.SetSmoke(smoke, true);
            shot.Smoke[smoke] = 2;
            Act(shot, 0, st => Battle.Fire(st, new Vector2I(4, 4)));
            BattleState back = Probe(9, 10);
            Put(back, 0, "MTR", Camp.Green, 4, 8, 90, kit: Kit.Thermal);
            Put(back, 1, "MTR", Camp.Brown, 4, 4, 270);
            back.Board.SetSmoke(smoke, true);
            back.Smoke[smoke] = 2;
            Act(back, 1, st => Battle.Fire(st, new Vector2I(4, 8)));
            BattleState watch = Probe(9, 10);
            Put(watch, 0, "MTR", Camp.Green, 4, 8, 90, kit: Kit.Thermal);
            watch.Unit(0)!.Watch = 90;
            watch.Board.SetSmoke(smoke, true);
            watch.Smoke[smoke] = 2;
            var mid = new Vector2I(4, 5);
            Put(watch, 1, "LTR", Camp.Brown, HexField.Step(mid, 30).X, HexField.Step(mid, 30).Y, 210);
            Act(watch, 1, st => Battle.Move(st, new[] { HexField.Step(mid, 30), mid, HexField.Step(mid, 210) }, -1));
            check("the thermal imager fires and watches through smoke; a round at it is stopped as ever",
                  shot.Unit(1)!.Fate == UnitFate.Knocked && back.Unit(0)!.Fate == UnitFate.Alive
                  && back.Events.Any(e => e.Kind == BattleEventKind.Spent && e.Cell == smoke)
                  && watch.Events.Any(e => e.Kind == BattleEventKind.AmbushFired) && !watch.Unit(0)!.Ambushing,
                  $"its shot: {Said(shot)}; at it: {Said(back)}; its ambush: {Said(watch)}");
        }

        // Д8, upgrades.md «Минный заградитель»: the medium at (4,8) mines (4,7)
        // - not a tank's hex, water, a wood, a mine, nor without the kit - and the
        // light that drives on to it goes up.
        {
            var beside = new Vector2I(4, 7);
            Vector2I wood = HexField.Step(new Vector2I(4, 8), 30), water = HexField.Step(new Vector2I(4, 8), 150);
            BattleState s = Probe(9, 10, woods: new[] { wood }, deep: new[] { water });
            Put(s, 0, "MTR", Camp.Green, 4, 8, 90, kit: Kit.MineLayer);
            Put(s, 1, "LTR", Camp.Brown, 4, 5, 270);
            Put(s, 2, "LTR", Camp.Green, 4, 9, 90);
            string? onTank = null, onWood = null, onWater = null, laid = null, again = null, noKit = null;
            Act(s, 0, st => onTank = Battle.LayMine(st, new Vector2I(4, 9)));
            Act(s, 0, st => onWood = Battle.LayMine(st, wood));
            Act(s, 0, st => onWater = Battle.LayMine(st, water));
            Act(s, 0, st => laid = Battle.LayMine(st, beside));
            s.Bonus = false;
            Act(s, 0, st => again = Battle.LayMine(st, beside));
            s.Bonus = false;
            Act(s, 2, st => noKit = Battle.LayMine(st, HexField.Step(new Vector2I(4, 9), 30)));
            Act(s, 1, st => Battle.Move(st, new[] { new Vector2I(4, 6), beside }, -1));
            check("the mine layer mines a free hex beside - not a tank's, water, a wood, a mine, nor without it - and it goes off",
                  laid is null && onTank is not null && onWood is not null && onWater is not null && again is not null
                  && noKit is not null && s.Unit(1)!.Fate == UnitFate.Knocked && s.Unit(1)!.Cell == beside,
                  $"tank {onTank}; wood {onWood}; water {onWater}; laid {laid ?? "ok"}; again {again}; no kit {noKit}; {Said(s)}");
        }

        // Д8, upgrades.md «Ремкомплект»: the medium at (4,8) puts its knocked
        // light beside back in the fight - not a drowned one, an enemy's, one
        // further off or one still fighting.
        {
            Vector2I drownedAt = HexField.Step(new Vector2I(4, 8), 30);
            BattleState s = Probe(9, 10, deep: new[] { drownedAt });
            Put(s, 0, "MTR", Camp.Green, 4, 8, 90, kit: Kit.RepairKit);
            Put(s, 1, "LTR", Camp.Green, 4, 7, 90, fate: UnitFate.Knocked);
            Put(s, 2, "LTR", Camp.Green, drownedAt.X, drownedAt.Y, 90, fate: UnitFate.Drowned);
            Put(s, 3, "LTR", Camp.Brown, 4, 9, 90, fate: UnitFate.Knocked);
            Put(s, 4, "LTR", Camp.Green, 4, 5, 90, fate: UnitFate.Knocked);
            Put(s, 5, "LTR", Camp.Brown, 0, 0, 90);
            string? drowned = null, enemy = null, far = null, fixedOne = null, alive = null;
            Act(s, 0, st => drowned = Battle.Repair(st, drownedAt));
            Act(s, 0, st => enemy = Battle.Repair(st, new Vector2I(4, 9)));
            Act(s, 0, st => far = Battle.Repair(st, new Vector2I(4, 5)));
            Act(s, 0, st => fixedOne = Battle.Repair(st, new Vector2I(4, 7)));
            s.Bonus = false;
            Act(s, 0, st => alive = Battle.Repair(st, new Vector2I(4, 7)));
            check("the repair kit raises an own knocked hull beside - not a drowned one, an enemy's, one further off, one fighting",
                  fixedOne is null && s.Unit(1)!.Fate == UnitFate.Alive && drowned is not null && enemy is not null
                  && far is not null && alive is not null,
                  $"drowned {drowned}; enemy {enemy}; far {far}; repaired {fixedOne ?? "ok"}; again {alive}");
        }

        // Д8, upgrades.md «Лебёдка»: the medium at (4,8) pulls the light 2 up
        // the column on to (4,7) - a mine there takes it; not a heavy, not 3
        // out, not past a hull between.
        {
            var mid = new Vector2I(4, 7);
            BattleState s = Probe(9, 10, mines: new[] { mid });
            Put(s, 0, "MTR", Camp.Green, 4, 8, 90, kit: Kit.Winch);
            Put(s, 1, "LTR", Camp.Brown, 4, 6, 90);
            Act(s, 0, st => Battle.Winch(st, new Vector2I(4, 6)));
            BattleState heavy = Probe(9, 10);
            Put(heavy, 0, "MTR", Camp.Green, 4, 8, 90, kit: Kit.Winch);
            Put(heavy, 1, "HTR", Camp.Brown, 4, 6, 90);
            Put(heavy, 2, "LTR", Camp.Brown, 4, 5, 90);
            string? tooHeavy = null, tooFar = null, past = null;
            Act(heavy, 0, st => tooHeavy = Battle.Winch(st, new Vector2I(4, 6)));
            Act(heavy, 0, st => tooFar = Battle.Winch(st, new Vector2I(4, 5)));
            BattleState between = Probe(9, 10);
            Put(between, 0, "MTR", Camp.Green, 4, 8, 90, kit: Kit.Winch);
            Put(between, 1, "LTR", Camp.Brown, 4, 6, 90);
            Put(between, 2, "LTR", Camp.Green, 4, 7, 90);
            Act(between, 0, st => past = Battle.Winch(st, new Vector2I(4, 6)));
            check("the winch pulls a hull 2 out on to the hex between, which does to it what it does; not heavier, further, or past a hull",
                  s.Unit(1)!.Cell == mid && s.Unit(1)!.Fate == UnitFate.Knocked
                  && s.Events.Any(e => e.Kind == BattleEventKind.Winched) && s.Events.Any(e => e.Kind == BattleEventKind.MineBlown)
                  && tooHeavy is not null && tooFar is not null && past is not null,
                  $"{Said(s)}; heavy {tooHeavy}; 3 out {tooFar}; past a hull {past}");
        }

        // Д8, upgrades.md «Стабилизатор засады»: the medium at (4,8) watches up
        // the column; a heavy crosses (4,5) and holds the round on its side,
        // the watch goes on; a light crosses next and takes the second, and the
        // watch is spent. Rammed, the stabilizer's watch stays.
        {
            var mid = new Vector2I(4, 5);
            BattleState s = Probe(9, 10);
            Put(s, 0, "MTR", Camp.Green, 4, 8, 90, kit: Kit.AmbushStabilizer);
            Put(s, 1, "HTR", Camp.Brown, HexField.Step(mid, 30).X, HexField.Step(mid, 30).Y, 210);
            Put(s, 2, "LTR", Camp.Brown, 0, 0, 330);
            Act(s, 0, st => Battle.Ambush(st, 90));
            s.Drove = s.Fired = s.Bonus = false;
            Act(s, 1, st => Battle.Move(st, new[] { mid, HexField.Step(mid, 210) }, -1));
            bool after1 = s.Unit(0)!.Ambushing;
            s.Drove = s.Fired = s.Bonus = false;
            s.Unit(2)!.Cell = HexField.Step(mid, 150);
            Act(s, 2, st => Battle.Move(st, new[] { mid, HexField.Step(mid, 330) }, -1));
            BattleState rammed = Probe(9, 10);
            Put(rammed, 0, "MTR", Camp.Green, 4, 5, 90, kit: Kit.AmbushStabilizer);
            Put(rammed, 1, "HTR", Camp.Brown, 4, 6, 90);
            rammed.Unit(0)!.Watch = 90;
            rammed.Unit(0)!.WatchShots = 2;
            Act(rammed, 1, st => Battle.Ram(st, new Vector2I(4, 5)));
            check("the ambush stabilizer watches on past the first round, spent at the second, and holds through a ram",
                  after1 && s.Unit(1)!.Fate == UnitFate.Alive && s.Unit(2)!.Fate == UnitFate.Knocked
                  && !s.Unit(0)!.Ambushing && s.Events.Count(e => e.Kind == BattleEventKind.AmbushFired) == 2
                  && rammed.Unit(0)!.Cell == new Vector2I(4, 4) && rammed.Unit(0)!.Ambushing,
                  $"after the heavy {after1}: {Said(s)}; rammed: {Said(rammed)}");
        }

        // Д9, match.md «Удержание точки»: the point a ring at (4,5), three points a
        // side. Round 1 the green light alone on it - green hold, brown lose one;
        // round 2 a brown medium on the ring too - one each, nobody holds, both
        // lose one, and the holding changed hands twice (the stalemate's clock);
        // round 3 the medium knocked out - it does not count, green hold, brown
        // are out of points and green win on them. Both out on one tick: a draw.
        {
            ControlPoint ring = new(new Vector2I(4, 5), true, 3);
            BattleState s = Probe(9, 10);
            s.Point = ring.Cells().ToArray();
            s.Score = new[] { ring.Score, ring.Score };
            Put(s, 0, "LTR", Camp.Green, 4, 5, 90);
            Put(s, 1, "LTR", Camp.Brown, 0, 0, 270);
            Turns(s, 2);
            (int G, int B, Camp? Holder, int Shift) round1 = (s.Score[0], s.Score[1], s.Holder, s.LastShift);
            Put(s, 2, "MTR", Camp.Brown, 4, 4, 270);
            Turns(s, 2);
            (int G, int B, Camp? Holder, int Shift, int Quiet) round2 = (s.Score[0], s.Score[1], s.Holder, s.LastShift, s.QuietRounds);
            s.Unit(2)!.Fate = UnitFate.Knocked;
            Turns(s, 2);
            BattleState both = Probe(9, 10);
            both.Point = new[] { new Vector2I(4, 5) };
            both.Score = new[] { 1, 1 };
            Put(both, 0, "LTR", Camp.Green, 0, 9, 90);
            Put(both, 1, "LTR", Camp.Brown, 0, 0, 270);
            Turns(both, 2);
            check("the control point: more active units hold it, the other side loses a point a round, both when nobody does; "
                  + "out of points loses, both out draws",
                  round1 == (3, 2, Camp.Green, 1) && round2 == (2, 1, null, 2, 0)
                  && s.Won == Camp.Green && s.Score[1] == 0 && s.Holder == Camp.Green && s.LastShift == 3
                  && s.Events.Any(e => e.Kind == BattleEventKind.Won && e.Note == "on points")
                  && both.Drawn && both.Won is null,
                  $"round 1 {round1}, round 2 {round2}; {Said(s)}; both: {Said(both)}");
        }

        // Д9, match.md «Расстановка и первый ход»: brown won the toss - brown move
        // first, the round and the field's tick end after green's turn.
        {
            BattleState s = Probe(9, 10);
            s.First = s.Turn = Camp.Brown;
            Put(s, 0, "LTR", Camp.Green, 4, 8, 90);
            Put(s, 1, "LTR", Camp.Brown, 4, 1, 270);
            Battle.EndTurn(s);
            (Camp Turn, int Round, bool Ticked) half = (s.Turn, s.Round, s.Events.Any(e => e.Kind == BattleEventKind.FieldTick));
            Battle.EndTurn(s);
            check("brown first: the round and the field's tick come after green's turn, and brown open the next",
                  half == (Camp.Green, 1, false) && s.Turn == Camp.Brown && s.Round == 2
                  && s.Events.Count(e => e.Kind == BattleEventKind.FieldTick) == 1,
                  $"after brown {half}; after green {s.Turn} round {s.Round}: {Said(s)}");
        }

        // Д9: a map's zones and point are read, written back the same, refused
        // where they cannot mean anything, and judged by the rules - a home
        // outside its side's zone, a cell in both zones.
        {
            string drill = System.IO.File.ReadAllText(MapFile.PathOf("drill10")).Replace("\"format\": 2", "\"format\": 3");
            string zoned = drill.Replace("\"ground\": [", "\"zones\": { \"green\": [[1, 7], [3, 4], [7, 2], [8, 7], [6, 7], [2, 8]], "
                                                          + "\"brown\": [[3, 2], [6, 2], [5, 3], [6, 3], [7, 3]] },\n"
                                                          + "  \"point\": { \"at\": [4, 5], \"ring\": true, \"score\": 6 },\n  \"ground\": [");
            BoardMap map = MapFile.Parse(zoned, "drill10");
            BoardMap again = MapFile.Parse(MapFile.Write(map), "drill10");
            List<MapRules.Fault> clean = MapRules.ArenaFaults(MapRules.Draft.Of(map));
            string? Refused(string text)
            {
                try
                {
                    MapFile.Parse(text, "drill10");
                    return null;
                }
                catch (InvalidOperationException e)
                {
                    return e.Message;
                }
            }
            string? noScore = Refused(zoned.Replace(", \"score\": 6", ""));
            string? red = Refused(zoned.Replace("\"brown\": [[3, 2]", "\"red\": [[3, 2]"));
            string? off = Refused(zoned.Replace("[2, 8]]", "[12, 8]]"));
            string? old = Refused(zoned.Replace("\"format\": 3", "\"format\": 2"));
            var homeOut = MapRules.ArenaFaults(MapRules.Draft.Of(MapFile.Parse(zoned.Replace("\"brown\": [[3, 2], ", "\"brown\": ["), "drill10")));
            var twice = MapRules.ArenaFaults(MapRules.Draft.Of(MapFile.Parse(zoned.Replace("[7, 3]] }", "[7, 3], [2, 8]] }"), "drill10")));
            check("a map's zones and control point read, write back the same, refuse a point without points, a side "
                  + "nobody is and a cell off the board, and the rules name a home outside its zone and a cell in both",
                  map.Arena.Point == ring() && map.Arena.Zone(Parking.Green).Count == 6 && map.Arena.Zone(Parking.Brown).Count == 5
                  && again.Arena.Say() == map.Arena.Say() && clean.Count(f => f.Fatal) == 0
                  && noScore is not null && red is not null && off is not null && old is not null
                  && homeOut.Any(f => f.Rule == "zone.home" && f.Cell == new Vector2I(3, 2))
                  && twice.Any(f => f.Rule == "zone.both" && f.Cell == new Vector2I(2, 8)),
                  $"{map.Arena}; again {again.Arena}; clean {string.Join("; ", clean)}; no score: {noScore}; red: {red}; "
                  + $"off: {off}; format 2: {old}; home out: {string.Join("; ", homeOut)}; twice: {string.Join("; ", twice)}");
            static ControlPoint ring() => new(new Vector2I(4, 5), true, 6);
        }

        // Д8, Р-4: a home names its tank's kit; silence is the stub, an empty
        // list is nothing, a name no upgrade has is refused; written back as read.
        {
            string drill = System.IO.File.ReadAllText(MapFile.PathOf("drill10"));
            string kitted = drill.Replace("\"homes\": [[1, 7], [3, 4]",
                "\"homes\": [{ \"at\": [1, 7], \"kit\": [\"smoke\", \"trawl\"] }, { \"at\": [3, 4], \"kit\": [] }");
            // «trawl» is the mine guard's name from before 2026-11-02, still read
            BoardMap map = MapFile.Parse(kitted, "drill10");
            BoardMap again = MapFile.Parse(MapFile.Write(map), "drill10");
            string? refused = null;
            try
            {
                MapFile.Parse(kitted.Replace("\"trawl\"", "\"jetpack\""), "drill10");
            }
            catch (InvalidOperationException e)
            {
                refused = e.Message;
            }
            check("a home's kit is read, written back the same, and an unknown upgrade refused",
                  kitted != drill && map.Parked[0].Kit == (Kit.Smoke | Kit.MineGuard) && map.Parked[1].Kit == Kit.None
                  && map.Parked[2].Kit is null && again.Parked[0].Kit == map.Parked[0].Kit
                  && again.Parked[1].Kit == Kit.None && again.Parked[2].Kit is null
                  && refused is not null && refused.Contains("jetpack"),
                  $"read {map.Parked[0]}, {map.Parked[1]}, {map.Parked[2]}; again {again.Parked[0]}, {again.Parked[1]}; refused: {refused}");
        }

        // Д10: the match without the scene - drill10 by Д5's commands on the
        // core alone ends as the bench ended it (the ricochet's knock-out, HM
        // on the mine, the ram into the pond, TD and HT gone up, two fires put
        // out), every order taken and the turn going by itself where it went.
        {
            BoardMap map = BoardMap.ByName("drill10");
            BattleState s = BattleRun.Start(map, Camp.Green);
            var wrong = new List<string>();
            List<Order> orders = Order.ParseAll("take:1;fire:5,3;take:9;go:7,4;take:4;fire:6,3;take:6;pass;take:1;ram:3,2;pass;"
                                                + "take:6;pass;take:4;fire:6,3;take:6;douse;pass;take:2;douse;pass", wrong);
            int refused = BattleRun.Play(map, s, orders);
            string[] said = s.Say().Split('\n');
            check("Д5's match on drill10 by the core alone ends as on the bench - round 5, brown to move, LT drowned, "
                  + "TD and HT destroyed, HM knocked out on the mine, MT on LT's hex",
                  wrong.Count == 0 && orders.Count == 21 && refused == 0
                  && said[0] == "round 5, turn 9, Brown to move, player 1 Green"
                  && said.Contains("Brown LTR#5 (3,1) h270 Drowned") && said.Contains("Brown HTR#7 (5,3) h270 Destroyed")
                  && said.Contains("Brown TDR#8 (6,3) h270 Destroyed") && said.Contains("Brown HMR#9 (7,4) h270 Knocked")
                  && said.Contains("Green MTR#1 (3,2) h90 Alive") && said.Contains("Brown MTR#6 (6,2) h270 Alive")
                  && said.Contains("Green HTR#2 (7,2) h90 Alive"),
                  $"wrong {string.Join(", ", wrong)}, {refused} refused: {s.Say().Replace('\n', ';')}");
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
                                     int[]? levels = null, Vector2I[]? mud = null)
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
        foreach (Vector2I m in mud ?? Array.Empty<Vector2I>())
            ground[m.Y * columns + m.X] = Foundation.Mud;
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
