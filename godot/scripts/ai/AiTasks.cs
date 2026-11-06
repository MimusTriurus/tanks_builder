using System;
using System.Collections.Generic;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The AI's tactical problems (docs/ai-plan.md, «Проверка»): a position on a
/// board of its own, green to move, and what its turn must come to. Each is
/// asked of the fighter and of the veteran. Run as the self test's theme
/// <c>ai</c> (<see cref="Run"/>) or on its own -
/// <c>Match3D.tscn -- --ai-tasks</c> (<see cref="Main"/>), a line a problem,
/// exit code the failures - so a change to the AI is checked without the
/// whole self test. The lanes run up and down a column: heading 90 is one
/// row up.
/// </summary>
public static class AiTasks
{
    public static bool Asked(string[] args) => Array.IndexOf(args, "--ai-tasks") >= 0;

    public static int Main(string[] args)
    {
        int failed = 0, passed = 0;
        bool verbose = Array.IndexOf(args, "--verbose") >= 0;
        try
        {
            Run((name, ok, said) =>
            {
                if (ok)
                    passed++;
                else
                    failed++;
                Log.Print($"ai-tasks: {(ok ? "PASS" : "FAIL")} {name}{((ok && !verbose) || said.Length == 0 ? "" : $" - {said}")}");
            });
            Log.Print($"ai-tasks: {passed} passed, {failed} failed");
        }
        catch (Exception e)
        {
            GD.PrintErr($"ai-tasks: threw: {e}");
            failed++;
        }
        finally
        {
            Log.Drain();
        }
        return failed;
    }

    /// <summary>Every problem at both levels, into <paramref name="check"/>;
    /// the plans over two turns (<see cref="Plans"/>) at the veteran's.</summary>
    public static void Run(Action<string, bool, string> check)
    {
        foreach (AiLevel level in new[] { AiLevel.Fighter, AiLevel.Veteran })
            foreach ((string name, Func<BattleState> setup, Func<BattleState, BattleState, bool> ok) in Problems())
            {
                BattleState start = Ready(setup());
                AiChoice c = AiBrain.Choose(start, level, new Random(7));
                (BattleState after, string? refused) = Play(start, c.Play);
                bool good = refused is null && ok(start, after);
                check($"{level}: {name}", good,
                      $"{c.Play} {(refused is null ? "" : $"refused: {refused}; ")}events: {string.Join(", ", after.Events)}");
            }
        foreach ((string name, Func<BattleState> setup, Func<BattleState, BattleState, bool> ok) in Plans())
        {
            // green the veteran, the enemy's reply the fighter's, green again
            BattleState start = Ready(setup());
            BattleState s = start;
            var said = new List<string>();
            string? refused = null;
            var rng = new Random(7);
            for (int turn = 0; turn < 3 && refused is null && !s.Over; turn++)
            {
                AiChoice c = AiBrain.Choose(s, s.Turn == Camp.Green ? AiLevel.Veteran : AiLevel.Fighter, rng);
                said.Add($"{s.Turn}: {c.Play}");
                (s, refused) = Play(s, c.Play);
            }
            check($"{AiLevel.Veteran}: {name}", refused is null && ok(start, s),
                  $"{string.Join("; ", said)} {(refused is null ? "" : $"refused: {refused}; ")}events: {string.Join(", ", s.Events)}");
        }
    }

    /// <summary>Green to move, its first unit able to act taken.</summary>
    private static BattleState Ready(BattleState s)
    {
        s.Turn = Camp.Green;
        s.Active = Battle.FirstReady(s, Camp.Green)?.Id;
        return s;
    }

    /// <summary>The play on a copy, as the bench gives it: each order, the
    /// turn handed over once nothing is left in it.</summary>
    private static (BattleState After, string? Refused) Play(BattleState start, AiPlay play)
    {
        BattleState s = AiMoves.Copy(start);
        foreach (Order o in play.Orders)
        {
            if (Battle.Apply(s, o) is { } why)
                return (s, $"{o}: {why}");
            if (o.Kind != OrderKind.Pass && !s.Over && Battle.TurnDone(s))
                Battle.Pass(s);
        }
        return (s, null);
    }

    private static IEnumerable<(string, Func<BattleState>, Func<BattleState, BattleState, bool>)> Problems()
    {
        // 1. LT round to the mortar's back: I over 0
        yield return ("LT goes round to the mortar's back and knocks it out", () =>
        {
            BattleState s = Board();
            Put(s, 0, "LTR", Camp.Green, 2, 8, 90);
            Put(s, 1, "HMR", Camp.Brown, 4, 4, 90);
            return s;
        }, (_, a) => a.Unit(1)!.Fate != UnitFate.Alive);

        // 2. MT on MT head on: II is not over II - the round is not thrown at the front
        yield return ("MT does not waste its round on a medium's front", () =>
        {
            BattleState s = Board();
            Put(s, 0, "MTR", Camp.Green, 4, 8, 90);
            Put(s, 1, "MTR", Camp.Brown, 4, 4, 270);
            return s;
        }, (_, a) => !a.Events.Exists(e => e.Kind == BattleEventKind.Glanced && e.Unit == 1 && e.Note == "Front"));

        // 3. the knocked LT between two enemies: the chain lights both
        yield return ("a knocked-out enemy between two others is finished: the blast lights both", () =>
        {
            BattleState s = Board();
            Put(s, 0, "MTR", Camp.Green, 4, 8, 90);
            Put(s, 1, "LTR", Camp.Brown, 4, 4, 270, fate: UnitFate.Knocked);
            Put(s, 2, "HTR", Camp.Brown, 3, 3, 270);
            Put(s, 3, "MTR", Camp.Brown, 5, 3, 270);
            // a second green unit, pinned out of it in the corner: with the
            // medium alone, the heavy's reply knocking it out would lose the
            // match (GameModes.md, a side with none able to fight)
            Put(s, 4, "LTR", Camp.Green, 8, 9, 30);
            s.Unit(4)!.Pinned = true;
            return s;
        }, (_, a) => a.Unit(1)!.Fate == UnitFate.Destroyed);

        // 15, 16. finish a hull or knock a live one («подбить и добить»,
        // docs/ai-plan.md, И4): a win wants no hull blown (GameModes.md), so
        // a hull is worth a round only while a repair kit can raise it. The
        // same board twice, the enemy medium beyond the lane with the kit or
        // without: raised, the knocked heavy is worth more than the light. The
        // light stands on the heavy's other axis; neither the light nor the
        // medium gets through a heavy's front, so the reply knocks nothing out
        foreach (bool kit in new[] { true, false })
            yield return (kit
                ? "a knocked-out HT a repairer can reach is blown up, not left to be raised"
                : "a knocked-out HT nobody can raise is left: the round knocks out a live LT", () =>
            {
                BattleState s = Board();
                Put(s, 0, "HTR", Camp.Green, 4, 8, 90);
                Put(s, 1, "HTR", Camp.Brown, 4, 4, 270, fate: UnitFate.Knocked);
                Put(s, 2, "LTR", Camp.Brown, 1, 6, 330);
                Put(s, 3, "MTR", Camp.Brown, 6, 2, 270, kit ? Kit.RepairKit : Kit.None);
                // pinned: the choice is between its two rounds, not a drive
                s.Unit(0)!.Pinned = true;
                return s;
            }, kit
                ? (_, a) => a.Unit(1)!.Fate == UnitFate.Destroyed
                : (_, a) => a.Unit(1)!.Fate == UnitFate.Knocked && a.Unit(2)!.Fate != UnitFate.Alive);

        // 4. alight: put it out before anything
        yield return ("a burning MT puts its fire out", () =>
        {
            BattleState s = Board();
            Put(s, 0, "MTR", Camp.Green, 4, 8, 90);
            s.Unit(0)!.Burning = true;
            Put(s, 1, "LTR", Camp.Brown, 8, 0, 270);
            return s;
        }, (_, a) => a.Unit(0)!.Fate == UnitFate.Alive && !a.Unit(0)!.Burning);

        // 6. the ram on to a mine: the light in a corridor of woods, only its front open
        yield return ("LT rams the enemy LT back on to a mine", () =>
        {
            Vector2I at = new(4, 3);
            var woods = new List<Vector2I>();
            foreach (int h in new[] { 30, 150, 210, 330 })
                woods.Add(HexField.Step(at, h));
            BattleState s = Board(woods: woods.ToArray(), mines: new[] { HexField.Step(at, 90) });
            Put(s, 0, "LTR", Camp.Green, 4, 8, 90);
            Put(s, 1, "LTR", Camp.Brown, at.X, at.Y, 270);
            return s;
        }, (_, a) => a.Unit(1)!.Fate != UnitFate.Alive);

        // 7. the glance off an own medium's side on to the mortar beside it
        yield return ("LT glances its round off an own MT's side into the mortar", () =>
        {
            BattleState s = Board();
            Put(s, 0, "LTR", Camp.Green, 4, 8, 90);
            Put(s, 1, "MTR", Camp.Green, 4, 5, 150);
            Vector2I past = HexField.Step(new Vector2I(4, 5), 210);
            Put(s, 2, "HMR", Camp.Brown, past.X, past.Y, 90);
            // neither may move: the glance is the only way at the mortar
            s.Unit(0)!.Pinned = s.Unit(1)!.Pinned = true;
            return s;
        }, (_, a) => a.Unit(2)!.Fate != UnitFate.Alive && a.Unit(1)!.Fate == UnitFate.Alive);

        // 8. the destroyer through a wall into the tank behind it
        yield return ("TD fires through a wall at the tank behind it", () =>
        {
            BattleState s = Board(walls: new[] { (new Vector2I(4, 6), 90) });
            Put(s, 0, "TDR", Camp.Green, 4, 8, 90);
            Put(s, 1, "MTR", Camp.Brown, 4, 4, 30);
            return s;
        }, (_, a) => a.Unit(1)!.Fate != UnitFate.Alive);

        // 9. the mortar over a wood, four out
        yield return ("HM lobs over a wood at a tank four out", () =>
        {
            BattleState s = Board(woods: new[] { new Vector2I(4, 6) });
            Put(s, 0, "HMR", Camp.Green, 4, 8, 90);
            Put(s, 1, "MTR", Camp.Brown, 4, 4, 270);
            return s;
        }, (_, a) => a.Unit(1)!.Fate != UnitFate.Alive);

        // 10. the brink of a hill: the roof of the medium below
        yield return ("MT on the brink of a hill takes a medium below through its roof", () =>
        {
            var levels = new int[Columns * Rows];
            for (int r = 7; r < Rows; r++)
                for (int q = 0; q < Columns; q++)
                    levels[r * Columns + q] = 1;
            BattleState s = Board(levels: levels);
            Put(s, 0, "MTR", Camp.Green, 4, 7, 90);
            Put(s, 1, "MTR", Camp.Brown, 4, 4, 270);
            // pinned: no way round to a side - the roof is the only plate
            s.Unit(0)!.Pinned = true;
            return s;
        }, (_, a) => a.Unit(1)!.Fate != UnitFate.Alive);

        // 13. an own hull out of the fight beside an enemy is not blown up (meta.md)
        yield return ("an own knocked-out hull is not shot to set the enemy beside alight", () =>
        {
            BattleState s = Board();
            Put(s, 0, "MTR", Camp.Green, 4, 8, 90);
            Put(s, 1, "LTR", Camp.Green, 4, 4, 90, fate: UnitFate.Knocked);
            Put(s, 2, "HTR", Camp.Brown, 3, 3, 270);
            return s;
        }, (_, a) => a.Unit(1)!.Fate == UnitFate.Knocked);

        // 14. the winch: the enemy medium pulled on to the mine between
        yield return ("MT winches the enemy medium on to a mine", () =>
        {
            BattleState s = Board(mines: new[] { new Vector2I(4, 6) });
            Put(s, 0, "MTR", Camp.Green, 4, 7, 90, Kit.Winch);
            Put(s, 1, "MTR", Camp.Brown, 4, 5, 270);
            // pinned: no way round to a side - the winch is the only way
            s.Unit(0)!.Pinned = true;
            return s;
        }, (_, a) => a.Unit(1)!.Fate != UnitFate.Alive);

        // 19. the mortar's smoke bomb (upgrades.md, «Дымовой снаряд»): the
        // enemy destroyer six out down the column, its round through the
        // green medium and on into the mortar behind it (its one pass). The
        // destroyer is past the mortar's reach of five, the medium's round
        // does not get through its front, all are pinned: the one answer is
        // smoke on the column 3 to 5 out, over the medium. The generator
        // looked one to three out and stopped at the medium (2026-11-06).
        yield return ("HM lays a smoke bomb over its MT on the TD's axis", () =>
        {
            BattleState s = Board();
            Put(s, 0, "HMR", Camp.Green, 4, 8, 90, Kit.SmokeShell);
            Put(s, 1, "MTR", Camp.Green, 4, 7, 90);
            Put(s, 2, "TDR", Camp.Brown, 4, 1, 270);
            foreach (int id in new[] { 0, 1, 2 })
                s.Unit(id)!.Pinned = true;
            return s;
        }, (_, a) => a.Board.Smoked(new Vector2I(4, 3)) || a.Board.Smoked(new Vector2I(4, 4)) || a.Board.Smoked(new Vector2I(4, 5)));
    }

    /// <summary>Problems a turn does not settle: green's play, the enemy's
    /// reply, green's next play, and what they come to (docs/ai-plan.md, И3).</summary>
    private static IEnumerable<(string, Func<BattleState>, Func<BattleState, BattleState, bool>)> Plans()
    {
        // 12. the heavy stands in the destroyer's lane: it steps aside, the
        // destroyer fires through the wall next turn (the wall takes one off
        // its IV, the mortar's front is I). The mortar is pinned and six out of
        // the destroyer, past its own reach; woods at its sides shut the
        // flanks, the wall the heavy's round, deep water behind the wall the
        // heavy's ram through it (И2's veteran rammed it and shot from beside).
        yield return ("HT steps out of the TD's lane, the TD fires through the wall next turn", () =>
        {
            Vector2I at = new(4, 3);
            var woods = new List<Vector2I>();
            foreach (int h in new[] { 30, 150, 210, 330 })
                woods.Add(HexField.Step(at, h));
            BattleState s = Board(woods: woods.ToArray(), walls: new[] { (new Vector2I(4, 5), 90) },
                                  deep: new[] { new Vector2I(4, 4) });
            Put(s, 0, "TDR", Camp.Green, 4, 9, 90);
            Put(s, 1, "HTR", Camp.Green, 4, 8, 90);
            Put(s, 2, "HMR", Camp.Brown, at.X, at.Y, 270);
            s.Unit(2)!.Pinned = true;
            return s;
        }, (_, a) => a.Unit(2)!.Fate != UnitFate.Alive);

        // 17. smoke on the axis to an own mortar (docs/ai-plan.md, И4): the
        // enemy medium two out of the mortar, under its reach of three, would
        // knock it out the moment it fires at the medium four out; an own
        // medium beside the lane puts the smoke grenade between them, the
        // mortar lobs over it next turn - or the medium, free by then, takes
        // the screened one in its side: either is the smoke's gain, without
        // it the mortar is lost in the reply. All but the enemy's reply are pinned:
        // no hull drives into the lane instead of the smoke, the mortar does
        // not drive off the axis; neither green gun gets through a medium's front.
        yield return ("MT smokes the axis to its own HM: the HM lives, an enemy falls next turn", () =>
        {
            BattleState s = Board();
            Put(s, 0, "HMR", Camp.Green, 4, 8, 90);
            Put(s, 1, "MTR", Camp.Green, 5, 7, 90, Kit.Smoke);
            Put(s, 2, "MTR", Camp.Brown, 4, 6, 270);
            Put(s, 3, "MTR", Camp.Brown, 4, 4, 270);
            foreach (int id in new[] { 0, 1, 2, 3 })
                s.Unit(id)!.Pinned = true;
            return s;
        }, (_, a) => a.Unit(0)!.Fate == UnitFate.Alive
                     && (a.Unit(3)!.Fate != UnitFate.Alive || a.Unit(2)!.Fate != UnitFate.Alive));

        // 18. the incendiary into the wood before the enemy (docs/ai-plan.md,
        // И4): the enemy medium in a wood behind a wood - the green medium's
        // round is spent on the first wood and a medium's front holds it
        // anyway; the incendiary lights the first wood, the round's end
        // carries the fire on to the enemy's (field.md, «огонь\пожар»). Not a
        // heavy: one ending its turn in a wood fells it (units/ht.md, rule 3).
        // The enemy is pinned for the reply, the green medium for its first
        // turn: no drive round to the enemy's back.
        yield return ("MT fires the incendiary into the wood before the enemy MT: the fire reaches it", () =>
        {
            BattleState s = Board(woods: new[] { new Vector2I(4, 5), new Vector2I(4, 4) });
            Put(s, 0, "MTR", Camp.Green, 4, 8, 90, Kit.Incendiary);
            Put(s, 1, "MTR", Camp.Brown, 4, 4, 270);
            s.Unit(0)!.Pinned = s.Unit(1)!.Pinned = true;
            return s;
        }, (_, a) => a.Unit(1)!.Burning || a.Unit(1)!.Fate != UnitFate.Alive);
    }

    private const int Columns = 9, Rows = 10;

    /// <summary>A board of its own (the self test's probe board): flat ground,
    /// the woods, mines, walls (a cell and an edge), deep water and levels asked for.</summary>
    private static BattleState Board(Vector2I[]? woods = null, Vector2I[]? mines = null,
                                     (Vector2I Cell, int Heading)[]? walls = null, int[]? levels = null,
                                     Vector2I[]? deep = null)
    {
        var field = new HexField { Trees = false, Columns = Columns, Rows = Rows };
        var cover = new Cover[Columns * Rows];
        var ground = new Foundation[Columns * Rows];
        foreach (Vector2I w in woods ?? Array.Empty<Vector2I>())
            cover[w.Y * Columns + w.X] = Cover.Forest;
        foreach (Vector2I m in mines ?? Array.Empty<Vector2I>())
            cover[m.Y * Columns + m.X] = Cover.Minefield;
        foreach ((Vector2I c, int _) in walls ?? Array.Empty<(Vector2I, int)>())
            cover[c.Y * Columns + c.X] = Cover.Walls;
        foreach (Vector2I d in deep ?? Array.Empty<Vector2I>())
            ground[d.Y * Columns + d.X] = Foundation.Deep;
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
}
