using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The rules core's own themes (docs/battle-core-plan.md). Kept out of
/// <c>SelfTest.cs</c>, whose NUL byte makes every write to it a hazard
/// (its endings are not normalised); the run calls in here.
/// </summary>
public static partial class SelfTest
{
    /// <summary>
    /// Д2: every shipped board, as the bench lays it on a <see cref="HexField"/>
    /// and as <see cref="BattleBoard"/> copies it, answers the rules the same -
    /// cell by cell, step by step, and through the modules that read a board
    /// (<see cref="Steps.Plan"/>, <see cref="Gunnery.Solve"/>). Some woods set
    /// alight, burnt and cleared and a wall's leaf knocked down first, so the
    /// states a match changes are in the comparison too.
    /// </summary>
    private static void BattleBoardTwins(Action<string, bool, string> check)
    {
        Theme("the battle board answers as the bench's board");

        // Trees stop a gun's round, burnt ones too (GDD field.md); cleared ones
        // are ground. The table and the data file agree (terrain.json).
        check("a wood screens alive, burning and burnt, and not cleared",
              TerrainRules.Screens(Cover.Forest, CoverState.Intact)
              && TerrainRules.Screens(Cover.Forest, CoverState.Burning)
              && TerrainRules.Screens(Cover.Forest, CoverState.Burnt)
              && !TerrainRules.Screens(Cover.Forest, CoverState.Cleared),
              "");

        int boards = 0;
        foreach (string name in BoardMap.Names)
        {
            BoardMap map;
            HexField field;
            try
            {
                map = BoardMap.ByName(name);
                field = new HexField
                {
                    Trees = false,
                    Columns = map.Columns, Rows = map.Rows, Plot = map.Plot, Sea = map.Sea,
                };
                field.SetKinds(map.Kinds);
                field.SetGround(map.Ground);
                field.SetCover(map.Over);
                field.SetRelief(map.Levels, map.Ramps);
                field.SetWater(map.Water);
                foreach ((Vector2I cell, Masonry wall) in map.Walling)
                    field.SetSides(cell, wall.Edges);
            }
            catch (Exception)
            {
                // a board the field refuses is the authored boards' theme's to report
                continue;
            }
            boards++;
            Stir(field);
            BattleBoard twin = BattleBoard.Of(field);
            var cells = new List<Vector2I>();
            for (int r = 0; r < field.Rows; r++)
                for (int q = 0; q < field.Columns; q++)
                    cells.Add(new Vector2I(q, r));

            var odd = new List<string>();
            foreach (Vector2I c in cells)
            {
                if (field.InBounds(c) != twin.InBounds(c) || field.LevelAt(c) != twin.LevelAt(c)
                    || field.RampHeading(c) != twin.RampHeading(c) || field.FoundationAt(c) != twin.FoundationAt(c)
                    || field.CoverAt(c) != twin.CoverAt(c) || field.CoverStateAt(c) != twin.CoverStateAt(c)
                    || field.SidesAt(c) != twin.SidesAt(c) || field.IsWater(c) != twin.IsWater(c)
                    || field.IsDeep(c) != twin.IsDeep(c) || field.Screened(c) != twin.Screened(c))
                    odd.Add($"({c.X},{c.Y})");
                foreach (int h in HexField.EdgeHeadings)
                    if (field.Passable(c, h) != twin.Passable(c, h) || field.Blocked(c, h) != twin.Blocked(c, h)
                        || field.Drops(c, HexField.Step(c, h)) != twin.Drops(c, HexField.Step(c, h)))
                        odd.Add($"({c.X},{c.Y})>{h}");
            }
            check($"{name}: every cell and every step reads the same", odd.Count == 0,
                  string.Join(" ", odd.Take(12)));

            // the moves, for the light (amphibious) and the heavy (the bulldozer)
            var moves = new List<string>();
            foreach (MovementProfile cls in new[] { MovementProfile.Light, MovementProfile.Heavy })
                foreach (Vector2I c in cells)
                {
                    if (!field.InBounds(c) || !TerrainRules.Drivable(field.FoundationAt(c)))
                        continue;
                    var pose = new Steps.Pose(c, HexField.EdgeHeadings[(c.X + c.Y) % 6]);
                    Steps.Mover mover = Steps.Mover.Of(cls, amphibious: cls == MovementProfile.Light);
                    Steps.Reach a = Steps.Plan(field, pose, mover), b = Steps.Plan(twin, pose, mover);
                    if (a.Budget != b.Budget || a.Cells.Count != b.Cells.Count
                        || a.Cells.Any(kv => !b.Cells.TryGetValue(kv.Key, out int cost) || cost != kv.Value))
                        moves.Add($"{cls.Tag} ({c.X},{c.Y})");
                }
            check($"{name}: every move plans the same", moves.Count == 0, string.Join(" ", moves.Take(8)));

            // the shots along every lane, a gun's and a mortar's
            var shots = new List<string>();
            foreach (Vector2I c in cells)
            {
                if (!field.InBounds(c))
                    continue;
                foreach (int h in HexField.EdgeHeadings)
                    foreach (Vector2I onto in field.Lane(c, h))
                        foreach (bool lobs in new[] { false, true })
                        {
                            Shot a = Gunnery.Solve(field, c, onto, lobs, _ => false);
                            Shot b = Gunnery.Solve(twin, c, onto, lobs, _ => false);
                            if (a.ToString() != b.ToString())
                                shots.Add($"({c.X},{c.Y})->({onto.X},{onto.Y}){(lobs ? " lob" : "")}");
                        }
            }
            check($"{name}: every shot solves the same", shots.Count == 0, string.Join(" ", shots.Take(8)));
        }
        check("the boards were there to compare", boards >= 5, $"{boards} board(s)");

        // A copy is a copy: what changes on it stays on it, and a wall knocked
        // flat reads breached without a second write, as on the field.
        BattleBoard? walled = null;
        Vector2I wallAt = default;
        foreach (string name in BoardMap.Names)
        {
            BoardMap map = BoardMap.ByName(name);
            if (map.Walling.Count == 0)
                continue;
            var f = new HexField { Trees = false, Columns = map.Columns, Rows = map.Rows, Plot = map.Plot };
            f.SetGround(map.Ground);
            f.SetCover(map.Over);
            foreach ((Vector2I cell, Masonry wall) in map.Walling)
                f.SetSides(cell, wall.Edges);
            walled = BattleBoard.Of(f);
            wallAt = map.Walling.Keys.First(c => f.SidesAt(c) != 0);
            break;
        }
        if (walled is not null)
        {
            BattleBoard copy = walled.Clone();
            copy.SetSides(wallAt, 0);
            check("a wall knocked flat on a copy is breached there and standing on the original",
                  copy.CoverStateAt(wallAt) == CoverState.Breached && copy.SidesAt(wallAt) == 0
                  && walled.CoverStateAt(wallAt) == CoverState.Intact && walled.SidesAt(wallAt) != 0,
                  $"({wallAt.X},{wallAt.Y})");
        }
        else
            check("a shipped board has a wall to knock down", false, "");
    }

    /// <summary>The states a match leaves on a board, laid on some of its
    /// cells: woods alight, burnt and cleared in turn, a minefield spent, the
    /// first leaf of each wall down.</summary>
    private static void Stir(HexField field)
    {
        int wood = 0;
        for (int r = 0; r < field.Rows; r++)
            for (int q = 0; q < field.Columns; q++)
            {
                var c = new Vector2I(q, r);
                switch (field.CoverAt(c))
                {
                    case Cover.Forest:
                        CoverState s = (wood++ % 4) switch
                        {
                            1 => CoverState.Burning, 2 => CoverState.Burnt, 3 => CoverState.Cleared,
                            _ => CoverState.Intact,
                        };
                        field.SetCoverState(c, s);
                        break;
                    case Cover.Minefield when (q + r) % 2 == 0:
                        field.SetCoverState(c, CoverState.Cleared);
                        break;
                    case Cover.Walls:
                        int sides = field.SidesAt(c);
                        field.SetSides(c, sides & (sides - 1));
                        break;
                }
            }
    }
}
