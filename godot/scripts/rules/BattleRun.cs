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
/// <item><c>--ramps</c>: the old board, the map's ramps and walls - the
/// relief's slopes without it (<see cref="Board"/>).</item>
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
        bool slopes = true;
        string? ai = null;
        int aiSeed = 1, aiTurns = 300;
        bool probeCheck = false;
        Handicap handicap = Handicap.None;
        bool swap = false, rotCheck = false;
        string? deploy = null, doctrine = null;
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
            else if (a == "--ramps") slopes = false;
            else if (a == "--ai" && more) ai = args[++i];
            else if (a == "--ai-seed" && more) aiSeed = int.TryParse(args[++i], out int seed) ? seed : 1;
            else if (a == "--ai-turns" && more) aiTurns = int.TryParse(args[++i], out int turns) ? turns : 300;
            else if (a == "--probe-check") probeCheck = true;
            else if (a == "--swap-sides") swap = true;
            else if (a == "--rot-check") rotCheck = true;
            else if (a == "--deploy" && more) deploy = args[++i].ToLowerInvariant();
            else if (a == "--doctrine" && more) doctrine = args[++i];
            else if (a == "--budget" && more) _budget = int.TryParse(args[++i], out int b) ? b : -1;
            else if (a == "--deploy-lead" && more) AiDeploy.Lead = float.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
            else if (a == "--handicap" && more && Enum.TryParse(args[++i], true, out Handicap h)) handicap = h;
        }
        try
        {
            if (ai is not null)
                return RunAi(mapName, ai, aiSeed, aiTurns, outDir, first, slopes, probeCheck, handicap, swap, rotCheck, deploy, doctrine);
            return Run(mapName, scriptPath, commands, outDir, first, measure, slopes);
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

    private static int Run(string? mapName, string? scriptPath, string? commands, string? outDir, Camp first, int measure,
                           bool slopes)
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

        var place = new Placing(Played(map, slopes), first);
        BattleState start = Start(map, first, slopes);
        Log.Print($"battle-run: board {map.Name} {map.Columns}x{map.Rows} {(slopes ? "on the relief" : "with ramps and walls")}, {start.Units.Count} units, player 1 {first}, "
                  + $"{orders.Count} orders{(place.Side is Camp p ? $", {p} place first" : "")}");
        foreach (BattleUnit u in start.Units)
            Log.Print($"battle-run: {u.Say(start.Shift)} kit {Kits.Names(u.Kit)}");

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

    /// <summary>
    /// <c>--ai SIDES[:LEVEL]</c> (docs/ai-plan.md, И1): the match played by the
    /// AI (<see cref="AiBrain"/>) - <c>both</c>, or <c>green</c> or
    /// <c>brown</c> against a side that only passes; LEVEL <c>recruit</c>,
    /// <c>fighter</c> (default) or <c>veteran</c>, <c>both:a,b</c> for green's
    /// and brown's apart. A map with zones is readied as it stands. Each turn
    /// goes to the log with the play, its events and what choosing it cost;
    /// the match as it ends to <c>out/battle-run/MAP-ai.txt</c>.
    /// <c>--ai-seed N</c>, <c>--ai-turns N</c> (300) the most turns. Exit
    /// code 2 when an order of the AI's own was refused.
    /// </summary>
    /// <summary><c>--budget N</c>: each side's units upgraded on N points as
    /// the quick battle's squad (<see cref="QuickSquad"/>); -1 - the map's kits.</summary>
    private static int _budget = -1;

    /// <summary>
    /// The upgrades of <c>--budget</c>: each unit a branch of its class drawn
    /// at random, the points dealt a level at a time round the side, up to
    /// level 3 each (<see cref="QuickSquad.ForAi"/>); its kit then the flags
    /// of the levels it has (<see cref="QuickSquad.KitOf"/>), the starter's
    /// gone. A die of its own off the seed: the AI's draws are not moved.
    /// </summary>
    private static void Upgrade(BattleState s, int budget, int seed)
    {
        for (int side = 0; side < 2; side++)
        {
            var rng = new Random(seed * 7919 + side * 104729 + 1);
            var units = new List<BattleUnit>();
            foreach (BattleUnit u in s.Units)
                if ((int)u.Side == side)
                    units.Add(u);
            var slots = new QuickSquad.Slot[units.Count];
            for (int i = 0; i < units.Count; i++)
            {
                QuickSquad.Class cls = QuickSquad.Classes[Math.Clamp(units[i].Class.Might, 1, 5) - 1];
                slots[i] = new QuickSquad.Slot(cls.Tag, cls.Branches[rng.Next(cls.Branches.Length)].Id, 0);
            }
            int left = budget;
            for (int k = 0; left > 0 && k < 3 * units.Count; k++)
            {
                ref QuickSquad.Slot slot = ref slots[k % units.Count];
                if (slot.Level < 3)
                {
                    slot.Level++;
                    left--;
                }
            }
            for (int i = 0; i < units.Count; i++)
                units[i].Kit = QuickSquad.KitOf(slots[i]);
        }
    }

    private static int RunAi(string? mapName, string spec, int seed, int maxTurns, string? outDir, Camp first, bool slopes,
                             bool probeCheck = false, Handicap handicap = Handicap.None,
                             bool swap = false, bool rotCheck = false, string? deploy = null,
                             string? doctrine = null)
    {
        if (mapName is null)
            return Refuse("--battle-run wants a map");
        if (!BoardMap.Names.Contains(mapName, StringComparer.OrdinalIgnoreCase))
            return Refuse($"no such board {mapName} - known: {string.Join(", ", BoardMap.Names)}");
        BoardMap map = BoardMap.ByName(mapName);
        string[] parts = spec.Split(':');
        string sides = parts[0].ToLowerInvariant();
        if (sides is not ("both" or "green" or "brown"))
            return Refuse($"--ai {spec}: both, green or brown");
        string[] words = parts.Length > 1 ? parts[1].Split(',') : new[] { "fighter" };
        var levels = new AiLevel[2];
        var shallow = new bool[2];
        for (int i = 0; i < 2; i++)
        {
            string w = words[Math.Min(i, words.Length - 1)];
            // veteran@2: the veteran without its third activation (AiBrain.Choose)
            shallow[i] = w.EndsWith("@2");
            w = shallow[i] ? w[..^2] : w;
            if (AiBrain.LevelOf(w) is not AiLevel level)
                return Refuse($"--ai {spec}: no level {w} - recruit, fighter, veteran");
            levels[i] = level;
        }
        // --doctrine GREEN[,BROWN]: the sides' styles (AiStyle), plain by default
        string[] styleWords = (doctrine ?? "plain").Split(',');
        var styles = new AiStyle[2];
        for (int i = 0; i < 2; i++)
            styles[i] = AiStyle.Of(styleWords[Math.Min(i, styleWords.Length - 1)]);
        bool Plays(Camp c) => sides == "both" || (sides == "green") == (c == Camp.Green);

        var place = new Placing(Played(map, slopes), first);
        BattleState s = Start(map, first, slopes);
        s.Handicap = handicap;
        if (_budget >= 0)
            Upgrade(s, _budget, seed);
        Log.Print($"battle-run: ai {spec} on {map.Name}, seed {seed}, player 1 {first}, {s.Units.Count} units, handicap {handicap}, styles {styles[0].Id}/{styles[1].Id}"
                  + (_budget >= 0 ? $", budget {_budget}" : ""));
        foreach (BattleUnit u in s.Units)
            Log.Print($"battle-run: {u.Say(s.Shift)} kit {Kits.Names(u.Kit)}");
        var said = new List<string>();
        // the placement as the homes stand
        if (place.Side is not null)
            Play(s, new List<Order> { new(OrderKind.Ready), new(OrderKind.Ready) }, place, said);
        // --deploy green|brown|both: the AI places those sides in their zones
        // (AiDeploy), blind to the other's; the rest stand on their homes
        if (deploy is not null)
        {
            var dice = new Random(seed * 7919 + 17);
            foreach (Camp c in new[] { first, first == Camp.Green ? Camp.Brown : Camp.Green })
            {
                if (deploy != "both" && deploy != (c == Camp.Green ? "green" : "brown"))
                    continue;
                List<Vector2I> zone = ZoneOf(map, c, s.Shift), across = ZoneOf(map, c == Camp.Green ? Camp.Brown : Camp.Green, s.Shift);
                List<AiDeploy.Spot> spots = AiDeploy.Place(s, c, zone, across, dice);
                AiDeploy.Apply(s, spots);
                Log.Print($"battle-run: {c} placed by the AI - {string.Join(", ", spots.Select(p => $"#{p.Unit} ({(p.Cell - s.Shift).X},{(p.Cell - s.Shift).Y}) h{p.Heading}"))}");
            }
            s.Active = Battle.FirstReady(s, s.Turn)?.Id;
        }
        // --swap-sides: the colours change places, the units stay - a side's
        // edge from the map stays with the place, one from the code with the colour
        if (swap)
        {
            foreach (BattleUnit u in s.Units)
                u.Side = u.Side == Camp.Green ? Camp.Brown : Camp.Green;
            s.Active = Battle.FirstReady(s, s.Turn)?.Id;
            Log.Print("battle-run: sides swapped - green on brown's places, brown on green's");
        }
        if (handicap == Handicap.Ambush)
        {
            Battle.AmbushAtStart(s);
            foreach (BattleUnit u in s.Units)
                if (u.Ambushing)
                    Log.Print($"battle-run: at the start {u.Say(s.Shift)} watching {u.Watch}");
        }

        var rng = new Random(seed);
        int refused = 0, turns = 0, plays = 0;
        double ms = 0.0, worst = 0.0;
        int probes = 0, probesWrong = 0;
        while (!s.Over && turns < maxTurns)
        {
            turns++;
            if (probeCheck)
            {
                (int n, int wrong) = ProbeCheck(s);
                probes += n;
                probesWrong += wrong;
            }
            Camp side = s.Turn;
            if (rotCheck)
                RotCheck(s, side, said, map);
            List<Order> orders;
            if (Plays(side))
            {
                AiChoice c = AiBrain.Choose(s, levels[side == Camp.Green ? 0 : 1], rng, shallow[side == Camp.Green ? 0 : 1],
                                            styles[side == Camp.Green ? 0 : 1]);
                plays += c.Plays;
                ms += c.Ms;
                worst = Math.Max(worst, c.Ms);
                string line = FormattableString.Invariant(
                    $"battle-run: round {s.Round} {side}: {c.Play.Say(s.Shift)} - score {c.Play.Score:F2}, {c.Plays} plays, {c.Replies} replies, depth {c.Depth} ({c.Deep} deep), {c.Ms:F1} ms");
                said.Add(line);
                Log.Print(line);
                orders = c.Play.Orders.Select(o => o.Moved(-s.Shift)).ToList();
            }
            else
                orders = new List<Order> { new(OrderKind.Pass) };
            int turnNo = s.TurnNo;
            int bad = Play(s, orders, place, said);
            refused += bad;
            // a play refused part-way leaves the turn open: handed over
            if (!s.Over && s.TurnNo == turnNo)
                Play(s, new List<Order> { new(OrderKind.Pass) }, place, said);
        }
        string verdict = s.Drawn ? "drawn" : s.Won is Camp won ? $"{won} won" : "not over";
        int aiTurns = Math.Max(1, sides == "both" ? turns : (turns + 1) / 2);
        Log.Print(FormattableString.Invariant(
            $"battle-run: ai over - {verdict}, round {s.Round}, {turns} turns, {refused} refused; a turn {ms / aiTurns:F1} ms, {plays / (double)aiTurns:F0} plays, the slowest {worst:F1} ms"));
        string dir = outDir ?? ProjectSettings.GlobalizePath("res://out/battle-run");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, $"{map.Name}-ai.txt");
        File.WriteAllText(file, s.Say());
        File.WriteAllText(Path.Combine(dir, $"{map.Name}-ai.log"), string.Join("\n", said) + "\n");
        Log.Print($"battle-run: the match as it ends - {file}");
        if (probeCheck)
            Log.Print($"battle-run: probe-check - {probes} shots probed, {probesWrong} not as fired");
        return refused > 0 || probesWrong > 0 ? 2 : 0;
    }

    /// <summary>The cell half a turn about the board's middle, odd columns
    /// shifted down (<c>MapFair.Twin</c>).</summary>
    private static Vector2I Twin(BoardMap map, Vector2I shift, Vector2I board)
    {
        Vector2I cell = board - shift;
        int y2 = 2 * cell.Y + (cell.X & 1);
        int q = map.Columns - 1 - cell.X;
        int y = ((map.Columns - 1) & 1) == 1 ? 2 * map.Rows - 1 - y2 : 2 * map.Rows - 2 - y2;
        return new Vector2I(q, (y - (q & 1)) / 2) + shift;
    }

    /// <summary>
    /// <c>--rot-check</c>, on a map with its half-turn twin (<c>--map-fair</c>):
    /// the match as it stands and turned half round must read alike to the
    /// AI - the score, the threats, the plays, the best play's score. A
    /// difference is the code taking one way of the board for another.
    /// </summary>
    private static void RotCheck(BattleState s, Camp side, List<string> said, BoardMap map)
    {
        BattleState r = AiMoves.Copy(s);
        Vector2I Twin(Vector2I c) => BattleRun.Twin(map, s.Shift, c);
        int Turned(int h) => h < 0 ? h : (h + 180) % 360;
        foreach (BattleUnit u in r.Units)
        {
            u.Cell = Twin(u.Cell);
            u.Heading = Turned(u.Heading);
            u.Watch = Turned(u.Watch);
            u.Watch2 = Turned(u.Watch2);
            u.Watch3 = Turned(u.Watch3);
        }
        r.Fires = r.Fires.ToDictionary(kv => Twin(kv.Key), kv => kv.Value);
        r.Smoke = r.Smoke.ToDictionary(kv => Twin(kv.Key), kv => kv.Value);
        if (r.Point is { } pt)
            r.Point = pt.Select(c => Twin(c)).ToArray();
        var notes = new List<string>();
        void Same(string what, double a, double b)
        {
            if (Math.Abs(a - b) > 1e-6)
                notes.Add(FormattableString.Invariant($"{what} {a:F3} turned {b:F3}"));
        }
        Same("quick", AiEval.Quick(s, side), AiEval.Quick(r, side));
        foreach (Camp c in new[] { Camp.Green, Camp.Brown })
        {
            (double w1, double o1) = new AiThreat(true).Of(s, c);
            (double w2, double o2) = new AiThreat(true).Of(r, c);
            Same($"threat {c} worst", w1, w2);
            Same($"threat {c} others", o1, o2);
        }
        var plays1 = new List<double>();
        var plays2 = new List<double>();
        AiMoves.Each(s, (p, after) => plays1.Add(AiEval.Quick(after, side)));
        AiMoves.Each(r, (p, after) => plays2.Add(AiEval.Quick(after, side)));
        plays1.Sort();
        plays2.Sort();
        Same("plays", plays1.Count, plays2.Count);
        if (plays1.Count > 0 && plays2.Count > 0)
            Same("best quick play", plays1[^1], plays2[^1]);
        AiChoice c1 = AiBrain.Choose(s, AiLevel.Veteran, new Random(1));
        AiChoice c2 = AiBrain.Choose(r, AiLevel.Veteran, new Random(1));
        Same("veteran's play", c1.Play.Score, c2.Play.Score);
        if (notes.Count > 0)
        {
            string line = $"battle-run: rot-check round {s.Round} {side}: {string.Join("; ", notes)}"
                          + $" | {c1.Play.Say(s.Shift)} | turned {c2.Play.Say(r.Shift)}";
            said.Add(line);
            Log.Print(line);
        }
    }

    /// <summary>
    /// <c>--probe-check</c>: every unit able to act, from where it stands
    /// (a casemate turned to the axis), at every standing unit on an axis in
    /// range - the probe (<see cref="Battle.Probe"/>) against the shot itself
    /// on a copy (<see cref="Battle.Fire"/>), unit by unit: out, destroyed,
    /// set alight. Returns how many were asked and how many differ; the first
    /// differences go to the log.
    /// </summary>
    private static (int Asked, int Wrong) ProbeCheck(BattleState s)
    {
        int asked = 0, wrong = 0;
        var marks = new int[s.Units.Count];
        foreach (BattleUnit shooter in s.Units)
        {
            if (shooter.Fate != UnitFate.Alive)
                continue;
            foreach (BattleUnit target in s.Units)
            {
                if (ReferenceEquals(target, shooter) || !target.Standing)
                    continue;
                (int heading, int range) = s.Board.LaneTo(shooter.Cell, target.Cell);
                if (heading < 0 || Gunnery.MayTarget(shooter.Class.Lobs, range, true, Gunnery.LobReachOf(shooter.Kit)) != Gunnery.Distance.Fits)
                    continue;
                foreach (bool full in new[] { true })
                {
                    Battle.Probe(s, shooter, shooter.Cell, heading, target.Cell, marks, full);
                    BattleState t = AiMoves.Copy(s);
                    t.Turn = shooter.Side;
                    t.Active = shooter.Id;
                    t.Drove = t.Fired = t.Bonus = t.Overdrive = false;
                    t.OnWay = null;
                    if (!t.Units[s.Units.IndexOf(shooter)].Class.Turreted)
                        t.Units[s.Units.IndexOf(shooter)].Heading = heading;
                    if (Battle.Fire(t, target.Cell) is { } why)
                    {
                        Log.Print($"battle-run: probe-check: {shooter.Say(s.Shift)} at {target.Say(s.Shift)} - the shot refused: {why}");
                        continue;
                    }
                    asked++;
                    var said = new List<string>();
                    for (int i = 0; i < s.Units.Count; i++)
                    {
                        BattleUnit was = s.Units[i], now = t.Units[i];
                        int fired = now.Fate == was.Fate ? (now.Burning && !was.Burning ? Battle.ProbeAlight : Battle.ProbeNone)
                            : now.Fate == UnitFate.Destroyed ? Battle.ProbeGone
                            : now.Out && !was.Out ? Battle.ProbeOut : Battle.ProbeNone;
                        if (fired != marks[i])
                            said.Add($"#{was.Id} probe {marks[i]} fire {fired}");
                    }
                    if (said.Count == 0)
                        continue;
                    wrong++;
                    if (wrong <= 10)
                        Log.Print($"battle-run: probe-check: {shooter.Say(s.Shift)} at {target.Say(s.Shift)}: {string.Join(", ", said)}");
                }
            }
        }
        return (asked, wrong);
    }

    private static int Refuse(string why)
    {
        Log.Print($"battle-run: REFUSED {why}");
        return 1;
    }

    /// <summary>
    /// The board a match is played on (<see cref="BattleBoard.Of"/> a field
    /// built from the map as the bench builds its own): the relief's - the
    /// levels joined by slopes, a level up or down a step from any side (the
    /// bench's <c>BuildBoard</c> with <c>--relief</c>; the ramps were given up,
    /// 2026-11-01), and the map's walls in the cover and on the edges, each
    /// cell a pad on the relief (BOT-12). With <paramref name="slopes"/> off
    /// (<c>--ramps</c>), the old board: the map's ramps.
    ///
    /// The map as given, in its own cells: the sea round it is
    /// <see cref="Played"/>'s, and <see cref="Start"/> asks for it.
    /// </summary>
    public static BattleBoard Board(BoardMap map, bool slopes = true)
    {
        var field = new HexField
        {
            Terrain = TerrainSet.Load(AssetRoot.Terrains),
            Trees = false,
            Columns = map.Columns, Rows = map.Rows, Plot = map.Plot, Sea = map.Sea,
        };
        try
        {
            field.SetKinds(map.Kinds);
            field.SetGround(map.Ground);
            field.SetCover(map.Over);
            field.SetRelief(map.Levels, slopes ? null : map.Ramps);
            field.Slopes = slopes;
            field.SetWater(map.Water);
            foreach ((Vector2I cell, Masonry wall) in map.Walling)
                field.SetSides(cell, wall.Edges);
            return BattleBoard.Of(field);
        }
        finally
        {
            field.Free();
        }
    }

    /// <summary>The map as the match is played on it: on the relief in a sea
    /// of deep water (<see cref="BoardMap.Moated"/>), as the bench's
    /// <c>BuildBoard</c> rings it - the user's call, 2026-11-01: a ram at the
    /// board's edge throws the tank into the sea, not refused. With
    /// <paramref name="slopes"/> off, the map itself.</summary>
    public static BoardMap Played(BoardMap map, bool slopes) => slopes ? map.Moated() : map;

    /// <summary>The match at its start as the bench lines it up: the board
    /// (<see cref="Board"/> of the <see cref="Played"/> map, in its sea), a
    /// unit on each home in bounds, the control point. The state's cells are
    /// the board's; <see cref="BattleState.Shift"/> takes them back to the
    /// map's, the orders' and the printout's.</summary>
    public static BattleState Start(BoardMap map, Camp first, bool slopes = true)
    {
        map = Played(map, slopes);
        BattleBoard board = Board(map, slopes);
        var s = new BattleState { Board = board, Shift = map.Shift, First = first, Turn = first };
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
        Play(s, orders, new Placing(Played(map, s.Board.Slopes), s.First), null);

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
            // the script's cells are the map's, the board's are in the sea
            Order on = o.Moved(s.Shift);
            string? why = o.Kind is OrderKind.Place or OrderKind.Ready || place.Side is not null
                ? place.Apply(s, on)
                : Battle.Apply(s, on);
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
                ? $"battle-run: {o}: refused - {Said(why, s.Shift)}"
                : $"battle-run: {o}: {(s.Events.Count > from ? string.Join(", ", s.Events.Skip(from).Select(e => Said(e, s.Shift))) : "nothing")}"
                  + (passed ? " - the turn goes by itself" : "");
            if (why is not null)
                refused++;
            said.Add(line);
            Log.Print(line);
        }
        return refused;
    }

    /// <summary>An event in the map's cells. (0, 0) is the cell of an event
    /// that has none (a turn ended, the field's tick) and is left as it is: on
    /// a board in a sea it is off the plot, never a cell anything happens on.</summary>
    private static string Said(BattleEvent e, Vector2I shift) =>
        (shift == Vector2I.Zero || e.Cell == Vector2I.Zero ? e : e with { Cell = e.Cell - shift }).ToString();

    /// <summary>A refusal in the map's cells: the rules word it in the
    /// board's, each <c>(q,r)</c> in it moved back.</summary>
    private static string Said(string why, Vector2I shift) =>
        shift == Vector2I.Zero
            ? why
            : System.Text.RegularExpressions.Regex.Replace(why, @"\((-?\d+),(-?\d+)\)",
                m => $"({int.Parse(m.Groups[1].Value) - shift.X},{int.Parse(m.Groups[2].Value) - shift.Y})");

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
    /// <summary>A side's zone on the core's board: the map's, else its homes'.</summary>
    private static List<Vector2I> ZoneOf(BoardMap map, Camp side, Vector2I shift)
    {
        IReadOnlyList<Vector2I> zone = map.Arena.Zone(side == Camp.Brown ? Parking.Brown : Parking.Green);
        if (zone.Count == 0)
            zone = Lineup.Of(map.Parked).Where(u => (u.Side == Parking.Brown) == (side == Camp.Brown)).Select(u => u.Cell).ToList();
        return zone.Select(c => c + shift).ToList();
    }

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
