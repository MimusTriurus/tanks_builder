using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// How far the guns kill, and what a range would take away - a reading for
/// the author, not a rule (<c>--range-study MAP[,MAP...] [N] [--facing]</c> on
/// <c>Match3D.tscn</c>, as <see cref="FireStudy"/>). The GDD's gun reaches the
/// board's edge (units.md, «Цель выстрела»); two ranges are read against it:
/// <list type="bullet">
/// <item><b>cap</b> - no shot at a tank past R cells;</item>
/// <item><b>fall</b> - past R cells the gun's might is one less, and the
/// armour's «strictly more» does the rest.</item>
/// </list>
/// The line-ups are <see cref="FireStudy"/>'s: N at random on the relief's
/// board, each side one of each class, a turret laid for free, a casemate let
/// fire whichever way; the mortar is left out as a shooter (3-5 already).
/// <c>--facing</c> turns every target to its shooter.
/// </summary>
public static class RangeStudy
{
    public static bool Asked(string[] args) => Array.IndexOf(args, "--range-study") >= 0;

    private static readonly MovementProfile[] Classes =
    {
        MovementProfile.Light, MovementProfile.Medium, MovementProfile.Heavy,
        MovementProfile.Destroyer, MovementProfile.Mortar,
    };

    /// <summary>The ranges read, R cells.</summary>
    private static readonly int[] Ranges = { 3, 4, 5, 6, 7, 8 };

    public static int Main(string[] args)
    {
        int at = Array.IndexOf(args, "--range-study");
        string names = at + 1 < args.Length ? args[at + 1] : "duel12";
        int n = at + 2 < args.Length && int.TryParse(args[at + 2], out int k) ? k : 2000;
        try
        {
            foreach (string name in names.Split(',', StringSplitOptions.RemoveEmptyEntries))
                Run(BoardMap.ByName(name), n, Array.IndexOf(args, "--facing") >= 0);
            return 0;
        }
        catch (Exception e)
        {
            GD.PrintErr($"range-study: threw: {e}");
            return 1;
        }
        finally
        {
            Log.Drain();
        }
    }

    private static void Run(BoardMap map, int n, bool facing)
    {
        BattleBoard board = BattleRun.Board(map);
        var land = new List<Vector2I>();
        for (int r = 0; r < map.Rows; r++)
        for (int q = 0; q < map.Columns; q++)
        {
            var c = new Vector2I(q, r);
            if (board.InBounds(c) && TerrainRules.Drivable(board.FoundationAt(c)) && !board.IsWater(c))
                land.Add(c);
        }
        var rng = new Random(20261101);
        // by the cells between shooter and target: clear lanes, kills by the
        // GDD, and the kills the fall keeps, per R
        var lanes = new SortedDictionary<int, int>();
        var kills = new SortedDictionary<int, int>();
        var killsBy = new Dictionary<string, int>();
        var fallBy = new Dictionary<(string, int), int>();
        var fall = new int[Ranges.Length];
        int total = 0;
        for (int sample = 0; sample < n; sample++)
        {
            var cells = land.OrderBy(_ => rng.Next()).Take(10).ToArray();
            var units = new (MovementProfile Cls, int Side, Vector2I Cell, int Heading)[10];
            for (int i = 0; i < 10; i++)
                units[i] = (Classes[i % 5], i / 5, cells[i], HexField.EdgeHeadings[rng.Next(6)]);
            var taken = new HashSet<Vector2I>(cells);
            foreach (var s in units)
            {
                if (s.Cls.Lobs)
                    continue;
                foreach (var t in units)
                {
                    if (t.Side == s.Side)
                        continue;
                    (int heading, int range) = board.LaneTo(s.Cell, t.Cell);
                    if (heading < 0 || range < Gunnery.DirectNearest)
                        continue;
                    if (!Gunnery.Solve(board, s.Cell, t.Cell, false, c => taken.Contains(c)).Clear)
                        continue;
                    lanes[range] = lanes.GetValueOrDefault(range) + 1;
                    bool above = board.LevelAt(s.Cell) > board.LevelAt(t.Cell);
                    Gunnery.Plate plate = Battle.PlateOf(heading, facing ? HexField.Reverse(heading) : t.Heading, above);
                    if (Gunnery.Penetration(s.Cls.Might, t.Cls, plate) <= 0)
                        continue;
                    total++;
                    kills[range] = kills.GetValueOrDefault(range) + 1;
                    killsBy[s.Cls.Tag] = killsBy.GetValueOrDefault(s.Cls.Tag) + 1;
                    for (int i = 0; i < Ranges.Length; i++)
                        if (range <= Ranges[i] || Gunnery.Penetration(s.Cls.Might - 1, t.Cls, plate) > 0)
                        {
                            fall[i]++;
                            fallBy[(s.Cls.Tag, Ranges[i])] = fallBy.GetValueOrDefault((s.Cls.Tag, Ranges[i])) + 1;
                        }
                }
            }
        }

        double per = 1.0 / n;
        int lanesAll = lanes.Values.Sum();
        Log.Print($"range-study: {map.Name} {map.Columns}x{map.Rows}{(facing ? ", every target facing its shooter" : "")}; {n} line-ups, "
                  + $"{lanesAll * per:F2} clear gun-enemy lanes and {total * per:F2} kills a line-up");
        // the median and the share past each R, of the kills
        int half = 0, median = 0;
        foreach (var kv in kills)
            if ((half += kv.Value) * 2 >= total) { median = kv.Key; break; }
        Log.Print($"range-study: {map.Name} kills by range (cells: per line-up): "
                  + string.Join(", ", kills.Select(kv => $"{kv.Key}: {kv.Value * per:F3}"))
                  + $"; median {median}, longest {(kills.Count > 0 ? kills.Keys.Max() : 0)}");
        Log.Print($"range-study: {map.Name} clear lanes by range: "
                  + string.Join(", ", lanes.Select(kv => $"{kv.Key}: {kv.Value * per:F3}")));
        for (int i = 0; i < Ranges.Length; i++)
        {
            int r = Ranges[i];
            int capped = kills.Where(kv => kv.Key <= r).Sum(kv => kv.Value);
            Log.Print($"range-study: {map.Name} R={r}: cap keeps {capped * 100.0 / Math.Max(1, total):F1}% of the kills, "
                      + $"fall keeps {fall[i] * 100.0 / Math.Max(1, total):F1}%; fall by shooter "
                      + string.Join(", ", Classes.Where(c => !c.Lobs).Select(c =>
                          $"{c.Tag} {fallBy.GetValueOrDefault((c.Tag, r)) * per:F3}/{killsBy.GetValueOrDefault(c.Tag) * per:F3}")));
        }
    }
}
