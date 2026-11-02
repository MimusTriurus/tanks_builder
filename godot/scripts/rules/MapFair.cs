using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// Is a duel map fair - a reading for its author, not a rule
/// (<c>--map-fair MAP</c> on <c>Match3D.tscn</c>, as <see cref="BattleRun"/>;
/// docs/board.md, «Честная карта»). The board is the core's
/// (<see cref="BattleRun.Start"/>), the moves and the shots the rules' own
/// (<see cref="Steps.Plan"/>, <see cref="Gunnery.Solve"/>):
/// <list type="bullet">
/// <item><b>Half a turn</b>: each cell against its twin about the board's
/// middle - level, floor, cover, water, ramp, walls turned with it; the zones
/// and the homes against the other side's. On a flat-topped board of an even
/// width this is the one exact symmetry: a mirror moves the odd columns'
/// half-row.</item>
/// <item><b>The zones apart</b>: the fewest cells between them, against a
/// mortar's three steps and five cells.</item>
/// <item><b>Zone to zone</b>: the shots a gun has from one zone into the other
/// without moving.</item>
/// <item><b>The first turn</b>: each class from each cell of its zone, every
/// cell it reaches in one turn, and from there each cell of the other zone a
/// gun sees or a mortar reaches - the cells player 1 can hit before player 2
/// has moved. The rest are where to stand. An upper bound: a casemate is let
/// fire whichever way it ends up, armour is not asked.</item>
/// </list>
/// Quits with 0, 1 for a map that would not load.
/// </summary>
public static class MapFair
{
    public static bool Asked(string[] args) => Array.IndexOf(args, "--map-fair") >= 0;

    public static int Main(string[] args)
    {
        int at = Array.IndexOf(args, "--map-fair");
        string? name = at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
        try
        {
            if (name is null || !BoardMap.Names.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                Log.Print($"map-fair: no such board {name} - known: {string.Join(", ", BoardMap.Names)}");
                return 1;
            }
            Run(BoardMap.ByName(name), Array.IndexOf(args, "--ramps") < 0);
            return 0;
        }
        catch (Exception e)
        {
            GD.PrintErr($"map-fair: threw: {e}");
            return 1;
        }
        finally
        {
            Log.Drain();
        }
    }

    /// <summary>The twin of <paramref name="cell"/> half a turn about the
    /// board's middle, odd columns shifted down (<c>MapEdit.Twin</c>).</summary>
    private static Vector2I Twin(BoardMap map, Vector2I cell)
    {
        int y2 = 2 * cell.Y + (cell.X & 1);
        int q = map.Columns - 1 - cell.X;
        int y = ((map.Columns - 1) & 1) == 1 ? 2 * map.Rows - 1 - y2 : 2 * map.Rows - 2 - y2;
        return new Vector2I(q, (y - (q & 1)) / 2);
    }

    /// <summary>Cells apart on the hex grid, odd columns shifted down.</summary>
    private static int Apart(Vector2I a, Vector2I b)
    {
        int az = a.Y - (a.X - (a.X & 1)) / 2, bz = b.Y - (b.X - (b.X & 1)) / 2;
        int dx = a.X - b.X, dz = az - bz;
        return Math.Max(Math.Abs(dx), Math.Max(Math.Abs(dz), Math.Abs(dx + dz)));
    }

    private static int Turned(int edges)
    {
        int turned = 0;
        foreach (int h in Masonry.Headings)
            if ((edges & (1 << Masonry.Bit(h))) != 0)
                turned |= 1 << Masonry.Bit((h + 180) % 360);
        return turned;
    }

    private static void Run(BoardMap map, bool slopes)
    {
        Log.Print($"map-fair: {map.Name} {map.Columns}x{map.Rows} on {(slopes ? "the relief - slopes, walls on pads" : "ramps and walls")}, levels "
                  + string.Join(", ", map.Levels.GroupBy(l => l).OrderBy(g => g.Key).Select(g => $"{g.Key}: {g.Count()}"))
                  + (map.Ramps.Any(r => r) ? $", {map.Ramps.Count(r => r)} ramp(s) in the file" : ""));

        // --- half a turn
        var off = new List<string>();
        for (int r = 0; r < map.Rows; r++)
        for (int q = 0; q < map.Columns; q++)
        {
            var cell = new Vector2I(q, r);
            Vector2I twin = Twin(map, cell);
            int i = map.At(cell), j = map.At(twin);
            if (map.Levels[i] != map.Levels[j] || map.Ground[i] != map.Ground[j] || map.Over[i] != map.Over[j]
                || map.Water[i] != map.Water[j] || map.Ramps[i] != map.Ramps[j])
                off.Add($"({q},{r})~({twin.X},{twin.Y})");
            int edges = map.Walling.TryGetValue(cell, out Masonry? wall) ? wall.Edges : 0;
            int twinEdges = map.Walling.TryGetValue(twin, out Masonry? other) ? other.Edges : 0;
            if (map.Over[i] == Cover.Walls && Turned(edges) != twinEdges)
                off.Add($"walls ({q},{r})~({twin.X},{twin.Y})");
        }
        IReadOnlyList<Vector2I> green = map.Arena.Zone(Parking.Green), brown = map.Arena.Zone(Parking.Brown);
        if (!new HashSet<Vector2I>(green.Select(c => Twin(map, c))).SetEquals(brown))
            off.Add("zones");
        List<Lineup.Unit> units = Lineup.Of(map.Parked);
        foreach (Lineup.Unit u in units)
            if (!units.Any(o => o.Side != u.Side && o.Cell == Twin(map, u.Cell) && o.Model == u.Model))
                off.Add($"home ({u.Cell.X},{u.Cell.Y}) {u.Side} {u.Model}");
        Log.Print(off.Count == 0 ? "map-fair: half a turn: every cell, wall, zone and home has its twin"
                  : $"map-fair: half a turn: {off.Count} off - {string.Join(", ", off)}");
        if (green.Count == 0 || brown.Count == 0)
        {
            Log.Print("map-fair: no zones - the rest is about them");
            return;
        }

        // --- the zones apart
        int apart = green.Min(g => brown.Min(b => Apart(g, b)));
        Log.Print($"map-fair: zones {apart} cells apart at the nearest; a mortar's first turn reaches "
                  + $"{3 + Gunnery.LobReach} (3 steps, {Gunnery.LobReach} cells) - "
                  + (apart > 3 + Gunnery.LobReach ? "out of it" : "IN it"));

        // the units (classes, kits) as the core lines them up, the board as
        // played - without its sea, in the map's own cells, as the zones are
        BattleState s = BattleRun.Start(map, Camp.Green, slopes);
        IHexBoard board = BattleRun.Board(map, slopes);
        Func<Vector2I, bool> nobody = _ => false;

        // --- zone to zone, standing
        int standing = 0;
        var lines = new List<string>();
        foreach (Vector2I g in green)
        foreach (Vector2I b in brown)
        {
            (int heading, int range) = board.LaneTo(g, b);
            if (heading < 0 || range < Gunnery.DirectNearest)
                continue;
            if (Gunnery.Solve(board, g, b, false, nobody).Clear)
            {
                standing++;
                if (lines.Count < 8)
                    lines.Add($"({g.X},{g.Y})-({b.X},{b.Y})");
            }
        }
        Log.Print($"map-fair: zone to zone, standing: {standing} clear line(s)"
                  + (lines.Count > 0 ? " - " + string.Join(", ", lines) + (standing > lines.Count ? ", ..." : "") : ""));

        // --- the first turn
        // the high ground: the highest a hull may stand (a rock is not stood on)
        int top = int.MinValue;
        for (int r = 0; r < map.Rows; r++)
        for (int q = 0; q < map.Columns; q++)
        {
            var cell = new Vector2I(q, r);
            if (board.InBounds(cell) && TerrainRules.Drivable(board.FoundationAt(cell)))
                top = Math.Max(top, board.LevelAt(cell));
        }
        bool raised = top > board.LevelAt(green[0]);
        foreach (Camp side in new[] { Camp.Green, Camp.Brown })
        {
            IReadOnlyList<Vector2I> own = side == Camp.Green ? green : brown;
            IReadOnlyList<Vector2I> theirs = side == Camp.Green ? brown : green;
            int facing = side == Camp.Green ? 90 : 270;
            var hit = new HashSet<Vector2I>();
            var says = new List<string>();
            var up = new List<string>();
            foreach (BattleUnit u in s.Units.Where(u => u.Side == side).GroupBy(u => u.Class.Tag).Select(g => g.First()))
            {
                Steps.Mover mover = Steps.Kitted(Steps.Mover.Of(u.Class, (u.Kit & Kit.Snorkel) != 0), u.Kit);
                var spots = new HashSet<Vector2I>();
                foreach (Vector2I z in own)
                    foreach (Vector2I c in Steps.Plan(board, new Steps.Pose(z, facing), mover).Cells.Keys)
                        spots.Add(c);
                if (raised && spots.Any(c => board.LevelAt(c) == top))
                    up.Add(u.Class.Tag);
                var mine = new HashSet<Vector2I>();
                foreach (Vector2I e in theirs)
                    foreach (Vector2I p in spots)
                    {
                        (int heading, int range) = board.LaneTo(p, e);
                        if (heading < 0)
                            continue;
                        bool reaches = u.Class.Lobs
                            ? range >= Gunnery.LobShortest && range <= Gunnery.LobReach
                            : range >= Gunnery.DirectNearest && Gunnery.Solve(board, p, e, false, nobody).Clear;
                        if (reaches)
                        {
                            mine.Add(e);
                            break;
                        }
                    }
                hit.UnionWith(mine);
                says.Add($"{u.Class.Tag} {mine.Count}");
            }
            var safe = theirs.Where(e => !hit.Contains(e)).OrderBy(e => e.Y).ThenBy(e => e.X).ToList();
            Log.Print($"map-fair: {side} first: hits {hit.Count} of the other zone's {theirs.Count} cells ({string.Join(", ", says)}); "
                      + $"safe {safe.Count}: {string.Join(" ", safe.Select(c => $"({c.X},{c.Y})"))}"
                      + (raised ? $"; on the high ground in it: {(up.Count > 0 ? string.Join(", ", up) : "none")}" : ""));
        }
    }
}
