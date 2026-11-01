using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// Fire from the high ground, two rules side by side - a reading for the
/// author, not a rule (<c>--fire-study MAP[,MAP...] [N]</c> on
/// <c>Match3D.tscn</c>, as <see cref="MapFair"/>):
/// <list type="bullet">
/// <item><b>roof</b> - the GDD's (field.md, «кромка»; classes.md, «Числа»):
/// a hull on the floor's level stops the round, a round from above reads the
/// roof.</item>
/// <item><b>over</b> - the proposal of 2026-11-01: on a shot between levels
/// no hull stops the round, either way, any of them may be the target; no
/// roof, the plates by the faces on every level.</item>
/// <item><b>hybrid</b> - the roof kept, and over the hulls only from above:
/// a round going down a level is stopped by no hull, one going up is, as in
/// the roof's rule.</item>
/// </list>
/// N line-ups at random on the relief's board (<see cref="BattleRun.Board"/>):
/// each side one of each class, on drivable dry cells, headings at random.
/// Each gun against each enemy: a shot clear by the rule and a plate it gets
/// through. The mortar is left out as a shooter - it lobs, and both rules are
/// the same to it. An upper bound, as <see cref="MapFair"/>: a turret is laid
/// for free, a casemate is let fire whichever way. <c>--facing</c>: every
/// target turned to its shooter, the front or the roof - the case the roof was
/// written for (the mirror in the face).
/// </summary>
public static class FireStudy
{
    public static bool Asked(string[] args) => Array.IndexOf(args, "--fire-study") >= 0;

    private static readonly MovementProfile[] Classes =
    {
        MovementProfile.Light, MovementProfile.Medium, MovementProfile.Heavy,
        MovementProfile.Destroyer, MovementProfile.Mortar,
    };

    private sealed class Tally
    {
        public int Same, Above, Below, MirrorAbove, MirrorSame;
        public int Total => Same + Above + Below;
    }

    public static int Main(string[] args)
    {
        int at = Array.IndexOf(args, "--fire-study");
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
            GD.PrintErr($"fire-study: threw: {e}");
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
        Tally roof = new(), over = new(), hybrid = new();
        int hybridOver = 0;
        int pairs = 0, interLevel = 0, roofOnly = 0, overOnly = 0, screened = 0;
        var roofOnlyBy = new Dictionary<string, int>();
        var overOnlyBy = new Dictionary<string, int>();
        for (int sample = 0; sample < n; sample++)
        {
            // ten distinct cells, five a side
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
                    pairs++;
                    int ls = board.LevelAt(s.Cell), lt = board.LevelAt(t.Cell);
                    bool between = ls != lt;
                    if (between)
                        interLevel++;
                    Shot a = Gunnery.Solve(board, s.Cell, t.Cell, false, c => taken.Contains(c));
                    Shot b = between
                        ? Gunnery.Solve(board, s.Cell, t.Cell, false, _ => false)
                        : a;
                    if (between && !a.Clear && b.Clear)
                        screened++;
                    bool killA = a.Clear && Gunnery.Penetration(s.Cls.Might, t.Cls,
                        Battle.PlateOf(heading, facing ? HexField.Reverse(heading) : t.Heading, ls > lt)) > 0;
                    bool killB = b.Clear && Gunnery.Penetration(s.Cls.Might, t.Cls,
                        Battle.PlateOf(heading, facing ? HexField.Reverse(heading) : t.Heading, false)) > 0;
                    string key = $"{s.Cls.Tag}>{t.Cls.Tag}";
                    // the hybrid: the roof, and over the hulls from above only
                    bool killH = (ls > lt ? b : a).Clear && Gunnery.Penetration(s.Cls.Might, t.Cls,
                        Battle.PlateOf(heading, facing ? HexField.Reverse(heading) : t.Heading, ls > lt)) > 0;
                    if (killH && !killA) hybridOver++;
                    Count(roof, killA, ls, lt, s.Cls == t.Cls);
                    Count(over, killB, ls, lt, s.Cls == t.Cls);
                    Count(hybrid, killH, ls, lt, s.Cls == t.Cls);
                    if (killA && !killB) { roofOnly++; roofOnlyBy[key] = roofOnlyBy.GetValueOrDefault(key) + 1; }
                    if (killB && !killA) { overOnly++; overOnlyBy[key] = overOnlyBy.GetValueOrDefault(key) + 1; }
                }
            }
        }
        string Levels() => string.Join(", ", land.GroupBy(c => board.LevelAt(c)).OrderBy(g => g.Key).Select(g => $"{g.Key}: {g.Count()}"));
        string Top(Dictionary<string, int> d) => string.Join(", ", d.OrderByDescending(kv => kv.Value).Take(6).Select(kv => $"{kv.Key} {kv.Value}"));
        double per = 1.0 / n;
        Log.Print($"fire-study: {map.Name} {map.Columns}x{map.Rows}{(facing ? ", every target facing its shooter" : "")}, land levels {Levels()}; {n} line-ups, "
                  + $"{pairs * per:F1} gun-enemy pairs at range a line-up, {interLevel * 100.0 / Math.Max(1, pairs):F1}% between levels");
        foreach ((string rule, Tally t) in new[] { ("roof", roof), ("over", over), ("hybrid", hybrid) })
            Log.Print($"fire-study: {map.Name} {rule}: kills a line-up {t.Total * per:F2} "
                      + $"(same level {t.Same * per:F2}, from above {t.Above * per:F2}, from below {t.Below * per:F2}); "
                      + $"mirror kills {(t.MirrorSame + t.MirrorAbove) * per:F3} (from above {t.MirrorAbove * per:F3})");
        Log.Print($"fire-study: {map.Name} only by the roof {roofOnly * per:F3} a line-up ({Top(roofOnlyBy)}); "
                  + $"only over a hull {overOnly * per:F3} ({Top(overOnlyBy)}); lanes between levels a hull screened {screened * per:F3}; "
                  + $"the hybrid's over the roof's {hybridOver * per:F3}");
    }

    private static void Count(Tally t, bool kill, int ls, int lt, bool mirror)
    {
        if (!kill)
            return;
        if (ls == lt) { t.Same++; if (mirror) t.MirrorSame++; }
        else if (ls > lt) { t.Above++; if (mirror) t.MirrorAbove++; }
        else t.Below++;
    }
}
