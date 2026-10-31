using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// A match on the core alone, no scene (docs/battle-core-plan.md, Д10):
/// <c>--battle-run MAP --script FILE</c> (or <c>--commands "a;b"</c>, the
/// bench's words, <see cref="Order"/>). The board and the line-up are the
/// bench's (<see cref="Lineup.Of"/>, the home's kit or the starter's), the
/// turn goes as the bench's does (<see cref="Battle.Pass"/>,
/// <see cref="Battle.TurnDone"/>), each order's events go to the log, and the
/// match as it ends to <c>out/battle-run/MAP.txt</c> in
/// <see cref="BattleState.Say"/>'s words - what the bench writes with
/// <c>--state-out</c> after the same commands, so the two compare by
/// <c>diff</c>.
/// <list type="bullet">
/// <item><c>--first green|brown</c>: player 1; green without it, as a
/// scripted bench run.</item>
/// <item><c>--measure N</c>: the script played N times more from the start,
/// quiet - orders a second and bytes an order, and the state's copy
/// (<see cref="BattleState.Clone"/>) timed and weighed on its own.</item>
/// <item><c>--out DIR</c>: where the result goes.</item>
/// </list>
/// A map with zones is placed first (<c>place</c>, <c>ready</c>) as the
/// bench places it: player 1, then the other side, each in its zone or on its
/// homes. The core keeps no undo: <c>undo</c> is refused. Started from
/// <c>Match3D.tscn</c> (Tank3DBench.Build), which then quits with the code
/// this returns: 0, 1 for a run that could not start, 2 for an order refused.
/// </summary>
public static class BattleRun
{
    public static bool Asked(string[] args) => Array.IndexOf(args, "--battle-run") >= 0;

    public static int Main(string[] args)
    {
        string? mapName = null, scriptPath = null, commands = null, outDir = null;
        Camp first = Camp.Green;
        int measure = 0;
        for (int i = 0; i < args.Length; i++)
        {
            bool more = i + 1 < args.Length;
            string a = args[i];
            if (a == "--battle-run" && more) mapName = args[++i];
            else if (a == "--script" && more) scriptPath = args[++i];
            else if (a == "--commands" && more) commands = args[++i];
            else if (a == "--out" && more) outDir = args[++i];
            else if (a == "--first" && more) first = args[++i] == "brown" ? Camp.Brown : Camp.Green;
            else if (a == "--measure" && more) measure = int.TryParse(args[++i], out int n) ? n : 0;
        }
        try
        {
            return Run(mapName, scriptPath, commands, outDir, first, measure);
        }
        catch (Exception e)
        {
            GD.PrintErr($"battle-run: threw: {e}");
            return 1;
        }
        finally
        {
            Log.Drain();
        }
    }

    private static int Run(string? mapName, string? scriptPath, string? commands, string? outDir, Camp first, int measure)
    {
        if (mapName is null)
            return Refuse("--battle-run wants a map");
        BoardMap map;
        if (string.Equals(mapName, "events", StringComparison.OrdinalIgnoreCase))
            map = BoardMap.Events;
        else if (BoardMap.Names.Contains(mapName, StringComparer.OrdinalIgnoreCase))
            map = BoardMap.ByName(mapName);
        else
            return Refuse($"no such board {mapName} - known: events, {string.Join(", ", BoardMap.Names)}");
        string text;
        if (scriptPath is not null)
        {
            string path = scriptPath.StartsWith("res://") ? ProjectSettings.GlobalizePath(scriptPath) : Path.GetFullPath(scriptPath);
            if (!File.Exists(path))
                return Refuse($"no script {path}");
            text = File.ReadAllText(path);
        }
        else if (commands is not null)
            text = commands;
        else
            return Refuse("--battle-run wants --script FILE or --commands \"a;b\"");
        var wrong = new List<string>();
        List<Order> orders = Order.ParseAll(text, wrong);
        if (wrong.Count > 0)
            return Refuse($"not orders: {string.Join(", ", wrong)}");

        var place = new Placing(map, first);
        BattleState start = Start(map, first);
        Log.Print($"battle-run: board {map.Name} {map.Columns}x{map.Rows}, {start.Units.Count} units, player 1 {first}, "
                  + $"{orders.Count} orders{(place.Side is Camp p ? $", {p} place first" : "")}");
        foreach (BattleUnit u in start.Units)
            Log.Print($"battle-run: {u} kit {Kits.Names(u.Kit)}");

        var said = new List<string>();
        BattleState s = start.Clone();
        int refused = Play(s, orders, place.Clone(), said);
        string verdict = s.Drawn ? "drawn" : s.Won is Camp w ? $"{w} won" : "not over";
        Log.Print($"battle-run: {orders.Count} orders, {refused} refused, round {s.Round}, turn {s.TurnNo} - {verdict}");

        string dir = outDir ?? ProjectSettings.GlobalizePath("res://out/battle-run");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, $"{map.Name}.txt");
        File.WriteAllText(file, s.Say());
        File.WriteAllText(Path.Combine(dir, $"{map.Name}.log"), string.Join("\n", said) + "\n");
        Log.Print($"battle-run: the match as it ends - {file}");

        if (measure > 0)
            Measure(start, orders, place, measure);
        return refused > 0 ? 2 : 0;
    }

    private static int Refuse(string why)
    {
        Log.Print($"battle-run: REFUSED {why}");
        return 1;
    }

    /// <summary>The match at its start as the bench lines it up: the board
    /// (<see cref="BattleBoard.Of"/> a field built from the map as the bench
    /// builds its own), a unit on each home in bounds, the control point.</summary>
    public static BattleState Start(BoardMap map, Camp first)
    {
        var field = new HexField
        {
            Terrain = TerrainSet.Load(AssetRoot.Terrains),
            Trees = false,
            Columns = map.Columns, Rows = map.Rows, Plot = map.Plot, Sea = map.Sea,
        };
        BattleBoard board;
        try
        {
            field.SetKinds(map.Kinds);
            field.SetGround(map.Ground);
            field.SetCover(map.Over);
            field.SetRelief(map.Levels, map.Ramps);
            field.SetWater(map.Water);
            foreach ((Vector2I cell, Masonry wall) in map.Walling)
                field.SetSides(cell, wall.Edges);
            board = BattleBoard.Of(field);
        }
        finally
        {
            field.Free();
        }
        var s = new BattleState { Board = board, First = first, Turn = first };
        List<Lineup.Unit> lineup = Lineup.Of(map.Parked);
        for (int i = 0; i < lineup.Count; i++)
        {
            Lineup.Unit u = lineup[i];
            if (!board.InBounds(u.Cell))
                continue;
            MovementProfile cls = Lineup.ClassFor(u.Model);
            s.Units.Add(new BattleUnit
            {
                Id = i, Tag = u.Model, Class = cls, Side = u.Side == Parking.Brown ? Camp.Brown : Camp.Green,
                Cell = u.Cell, Heading = u.Facing, Kit = map.Parked[u.Slot].Kit ?? Kits.Starter(cls),
            });
        }
        if (map.Arena.Point is { } point)
        {
            s.Point = point.Cells().Where(board.InBounds).ToArray();
            s.Score[0] = s.Score[1] = point.Score;
        }
        s.Active = Battle.FirstReady(s, first)?.Id;
        return s;
    }

    /// <summary>The orders on a match <see cref="Start"/>ed off
    /// <paramref name="map"/>, the placement with them; how many were
    /// refused. What the self test plays.</summary>
    public static int Play(BoardMap map, BattleState s, List<Order> orders) =>
        Play(s, orders, new Placing(map, s.First), null);

    /// <summary>
    /// The orders on <paramref name="s"/>, each with what it did into
    /// <paramref name="said"/> (null when measuring), the turn handed over
    /// whenever nothing is left in it. Returns how many were refused.
    /// </summary>
    private static int Play(BattleState s, List<Order> orders, Placing place, List<string>? said,
                            long[]? ticks = null, int[]? counts = null)
    {
        int refused = 0;
        foreach (Order o in orders)
        {
            int from = s.Events.Count;
            long t0 = ticks is null ? 0 : Stopwatch.GetTimestamp();
            string? why = o.Kind is OrderKind.Place or OrderKind.Ready || place.Side is not null
                ? place.Apply(s, o)
                : Battle.Apply(s, o);
            // the turn goes by itself once nothing is left in it (Tank3DBench.Match)
            bool passed = why is null && o.Kind != OrderKind.Pass && Battle.TurnDone(s);
            if (passed)
                Battle.Pass(s);
            if (ticks is not null)
            {
                ticks[(int)o.Kind] += Stopwatch.GetTimestamp() - t0;
                counts![(int)o.Kind]++;
            }
            if (said is null)
            {
                if (why is not null)
                    refused++;
                continue;
            }
            string line = why is not null
                ? $"battle-run: {o}: refused - {why}"
                : $"battle-run: {o}: {(s.Events.Count > from ? string.Join(", ", s.Events.Skip(from)) : "nothing")}"
                  + (passed ? " - the turn goes by itself" : "");
            if (why is not null)
                refused++;
            said.Add(line);
            Log.Print(line);
        }
        return refused;
    }

    /// <summary>
    /// What the AI's depth rests on: orders a second and bytes an order, the
    /// script played <paramref name="times"/> times from a copy of the start;
    /// the copy alone timed and weighed as often again. Warmed up first, so
    /// the JIT is not in the count.
    /// </summary>
    private static void Measure(BattleState start, List<Order> orders, Placing place, int times)
    {
        Play(start.Clone(), orders, place.Clone(), null);
        long bytes0 = GC.GetAllocatedBytesForCurrentThread();
        var watch = Stopwatch.StartNew();
        for (int i = 0; i < times; i++)
            Play(start.Clone(), orders, place.Clone(), null);
        watch.Stop();
        long played = GC.GetAllocatedBytesForCurrentThread() - bytes0;
        double perOrder = watch.Elapsed.TotalMilliseconds * 1000.0 / (times * (double)orders.Count);

        BattleState mid = start.Clone();
        Play(mid, orders.Take(orders.Count / 2).ToList(), place.Clone(), null);
        bytes0 = GC.GetAllocatedBytesForCurrentThread();
        watch.Restart();
        for (int i = 0; i < times; i++)
            mid.Clone();
        watch.Stop();
        long copied = GC.GetAllocatedBytesForCurrentThread() - bytes0;
        double perCopy = watch.Elapsed.TotalMilliseconds * 1000.0 / times;
        // the start's copy is in every play: taken out of the order's share
        bytes0 = GC.GetAllocatedBytesForCurrentThread();
        start.Clone();
        long startCopy = GC.GetAllocatedBytesForCurrentThread() - bytes0;
        double orderBytes = (played - (double)startCopy * times) / (times * (double)orders.Count);

        Log.Print(FormattableString.Invariant(
            $"battle-run: measure x{times}: {perOrder:F1} us an order ({1e6 / perOrder:F0} a second), ")
                  + FormattableString.Invariant($"~{orderBytes / 1024.0:F1} KB an order with the start's copy taken out; ")
                  + FormattableString.Invariant($"the state's copy {perCopy:F2} us, {copied / (double)times / 1024.0:F1} KB ")
                  + $"({mid.Units.Count} units, {mid.Board.Columns}x{mid.Board.Rows}, {mid.Events.Count} events)");

        // each kind of order on its own: the move plans its route, which is
        // most of what a search pays
        int kinds = Enum.GetValues<OrderKind>().Length;
        long[] ticks = new long[kinds];
        int[] counts = new int[kinds];
        for (int i = 0; i < Math.Max(1, times / 10); i++)
            Play(start.Clone(), orders, place.Clone(), null, ticks, counts);
        var by = new List<string>();
        for (int k = 0; k < kinds; k++)
            if (counts[k] > 0)
                by.Add(FormattableString.Invariant(
                    $"{((OrderKind)k).ToString().ToLowerInvariant()} {ticks[k] * 1e6 / Stopwatch.Frequency / counts[k]:F1}"));
        Log.Print($"battle-run: measure by order, us (the pass with the turn going by itself in the order before it): {string.Join(", ", by)}");
    }

    /// <summary>
    /// The placement before the first turn (Tank3DBench.Deploy), on the core's
    /// state: player 1's side, then the other, each a unit at a time on a free
    /// hex of its zone - its homes where the map draws none - and <c>ready</c>
    /// to hand on. Only on a map with a zone, as on the bench.
    /// </summary>
    private sealed class Placing
    {
        private readonly BoardMap _map;
        private readonly Camp _first;
        public Camp? Side;

        public Placing(BoardMap map, Camp first)
        {
            _map = map;
            _first = first;
            bool zoned = map.Arena.Zone(Parking.Green).Count > 0 || map.Arena.Zone(Parking.Brown).Count > 0;
            Side = zoned ? first : null;
        }

        public Placing Clone() => (Placing)MemberwiseClone();

        private IReadOnlyList<Vector2I> Zone(BattleState s, Camp side)
        {
            IReadOnlyList<Vector2I> zone = _map.Arena.Zone(side == Camp.Brown ? Parking.Brown : Parking.Green);
            if (zone.Count > 0)
                return zone;
            return Lineup.Of(_map.Parked).Where(u => (u.Side == Parking.Brown) == (side == Camp.Brown))
                         .Select(u => u.Cell).ToList();
        }

        public string? Apply(BattleState s, Order o)
        {
            if (Side is not Camp side)
                return "the placement is over";
            switch (o.Kind)
            {
                // the pass is «В бой» while placing, as on the bench (EndTurn)
                case OrderKind.Ready:
                case OrderKind.Pass:
                    if (side == _first)
                    {
                        Side = side == Camp.Green ? Camp.Brown : Camp.Green;
                        return null;
                    }
                    Side = null;
                    s.Turn = _first;
                    s.Active = Battle.FirstReady(s, _first)?.Id;
                    return null;
                case OrderKind.Place:
                    if (s.Unit(o.Unit) is not { } u)
                        return $"no unit #{o.Unit}";
                    if (u.Side != side)
                        return $"#{o.Unit} is {u.Side}, {side} place";
                    if (o.Side >= 0 && Array.IndexOf(HexField.EdgeHeadings, o.Side) < 0)
                        return $"{o.Side} is not a hex's side";
                    if (!Zone(s, side).Contains(o.Cell))
                        return $"({o.Cell.X},{o.Cell.Y}) is not in {side}'s zone";
                    if (s.At(o.Cell) is { } there && there.Id != u.Id)
                        return $"({o.Cell.X},{o.Cell.Y}) is taken by #{there.Id}";
                    u.Cell = o.Cell;
                    if (o.Side >= 0)
                        u.Heading = o.Side;
                    return null;
                case OrderKind.Take:
                    return null;
                default:
                    return "the placement first - then ready";
            }
        }
    }
}
