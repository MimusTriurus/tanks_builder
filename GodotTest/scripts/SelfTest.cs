using System;
using System.Collections.Generic;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// Assertions over the grid arithmetic and the turret modes, run with
/// `godot --path &lt;dir&gt; -- --selftest`.
///
/// It exists because none of this can be checked from a screenshot: picking a
/// cell from a click, walking a path and holding a turret heading through a
/// turn are all behaviour over time, and a still frame shows none of it. The
/// click-to-cell round trip in particular already caught a real bug - the
/// anchor and the hexagon centre are different points, and treating them as one
/// selects the neighbouring cell along every slanted edge.
/// </summary>
public static class SelfTest
{
    public static int Run(HexField field, TankSprite tank)
    {
        int failed = 0;

        void Check(string name, bool ok, string detail = "")
        {
            if (ok)
                GD.Print($"  ok    {name}");
            else
            {
                GD.Print($"  FAIL  {name}  {detail}");
                failed++;
            }
        }

        GD.Print("grid: click lands on the cell that was drawn");
        int misses = 0;
        var firstMiss = "";
        for (int q = 0; q < field.Columns; q++)
        for (int r = 0; r < field.Rows; r++)
        {
            var cell = new Vector2I(q, r);
            Vector2I hit = field.CellAt(field.CellCentre(cell));
            if (hit != cell)
            {
                misses++;
                if (firstMiss.Length == 0)
                    firstMiss = $"({q},{r}) -> ({hit.X},{hit.Y})";
            }
        }
        Check("cell centre round trip over every cell", misses == 0,
            $"{misses} wrong, first {firstMiss}");

        // A click anywhere inside a cell must pick that cell, not just a click
        // dead centre. Probe at 40% of the way to each of the six neighbours.
        misses = 0;
        firstMiss = "";
        for (int q = 1; q < field.Columns - 1; q++)
        for (int r = 1; r < field.Rows - 1; r++)
        {
            var cell = new Vector2I(q, r);
            Vector2 centre = field.CellCentre(cell);
            foreach (int heading in HexField.EdgeHeadings)
            {
                Vector2 towards = field.CellCentre(HexField.Step(cell, heading));
                Vector2I hit = field.CellAt(centre + (towards - centre) * 0.4f);
                if (hit != cell)
                {
                    misses++;
                    if (firstMiss.Length == 0)
                        firstMiss = $"({q},{r}) at {heading} deg -> ({hit.X},{hit.Y})";
                }
            }
        }
        Check("clicks off-centre stay in the same cell", misses == 0,
            $"{misses} wrong, first {firstMiss}");

        GD.Print("grid: steps and headings agree");
        misses = 0;
        for (int q = 0; q < field.Columns; q++)
        for (int r = 0; r < field.Rows; r++)
        foreach (int heading in HexField.EdgeHeadings)
        {
            var cell = new Vector2I(q, r);
            if (HexField.HeadingTo(cell, HexField.Step(cell, heading)) != heading)
                misses++;
        }
        Check("HeadingTo inverts Step everywhere", misses == 0, $"{misses} wrong");

        GD.Print("paths");
        int bad = 0;
        var badDetail = "";
        for (int q = 0; q < field.Columns; q++)
        for (int r = 0; r < field.Rows; r++)
        {
            var from = new Vector2I(0, 0);
            var to = new Vector2I(q, r);
            List<Vector2I> path = field.FindPath(from, to);
            if (from == to)
                continue;
            string why = "";
            if (path.Count == 0)
                why = "no path";
            else if (path[^1] != to)
                why = "does not end at the target";
            else
            {
                Vector2I at = from;
                foreach (Vector2I next in path)
                {
                    if (!field.InBounds(next))
                    {
                        why = $"leaves the board at ({next.X},{next.Y})";
                        break;
                    }
                    if (HexField.HeadingTo(at, next) < 0)
                    {
                        why = $"({at.X},{at.Y}) and ({next.X},{next.Y}) are not neighbours";
                        break;
                    }
                    at = next;
                }
            }
            if (why.Length > 0)
            {
                bad++;
                if (badDetail.Length == 0)
                    badDetail = $"to ({q},{r}): {why}";
            }
        }
        Check("every path is contiguous, in bounds and reaches the target",
            bad == 0, $"{bad} bad, first {badDetail}");

        GD.Print("turret modes");
        tank.TurretLocked = true;
        tank.HullFacing = 270.0;
        tank.TurretFacing = 0.0;
        for (int i = 0; i < 24; i++)
            tank.TurnHull(15.0);
        Check("locked turret holds its world heading through a full hull turn",
            Math.Abs(WrapAngle(tank.TurretFacing - 0.0)) < 1e-6,
            $"turret ended at {tank.TurretFacing:F3}");
        Check("locked hull still came back round",
            Math.Abs(WrapAngle(tank.HullFacing - 270.0)) < 1e-6,
            $"hull ended at {tank.HullFacing:F3}");

        tank.TurretLocked = false;
        tank.HullFacing = 270.0;
        tank.TurretFacing = 0.0;
        double offsetBefore = WrapAngle(tank.TurretFacing - tank.HullFacing);
        for (int i = 0; i < 7; i++)
            tank.TurnHull(23.0);
        double offsetAfter = WrapAngle(tank.TurretFacing - tank.HullFacing);
        Check("free turret keeps a constant offset from the hull",
            Math.Abs(WrapAngle(offsetAfter - offsetBefore)) < 1e-6,
            $"offset {offsetBefore:F3} -> {offsetAfter:F3}");
        Check("free turret actually moved",
            Math.Abs(WrapAngle(tank.TurretFacing - 0.0)) > 1.0,
            $"turret ended at {tank.TurretFacing:F3}");

        GD.Print(failed == 0 ? "SELFTEST OK" : $"SELFTEST FAILED: {failed}");
        return failed;
    }

    private static double WrapAngle(double degrees) =>
        (degrees % 360.0 + 360.0 + 180.0) % 360.0 - 180.0;
}
