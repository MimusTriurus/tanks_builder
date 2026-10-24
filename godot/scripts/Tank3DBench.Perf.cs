using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The bench's cost, measured over a scripted run (2026-10-24):
/// <c>--perf DIR [FRAMES]</c> plays the <c>--do</c> script with the vsync and
/// the frame cap off, keeps every frame (<see cref="Prof"/>) and writes
/// <list type="bullet">
/// <item><c>frames.csv</c> - a row a frame: wall, render CPU, GPU,
/// draw calls, nodes, allocated bytes, GC, then every section's ms;</item>
/// <item><c>summary.json</c> - avg, p50, p95, p99, max of each past the
/// warm-up (<see cref="PerfWarm"/>), the sections the same, and the worst
/// frames with the section that cost most in each;</item>
/// <item><c>compare.txt</c> - against a baseline, when there is one: a line a
/// figure, the ones worse by more than <see cref="PerfWorse"/> marked. The
/// baseline is <c>--perf-against FILE</c>, else <c>DIR/baseline.json</c>,
/// else the scenario's own in git (<c>perf/NAME.json</c>, BOT-6).
/// <c>--perf-baseline</c> makes this run <c>DIR/baseline.json</c>. The
/// process exits 1 when anything got worse.</item>
/// </list>
///
/// <b>Run it with <c>--fixed-fps 60</c>.</b> The script is in frames and the
/// game in seconds: at a real 300 fps the shot at frame 100 lands while the
/// pass at 560 is already due, and two runs do not play the same game. With
/// the fixed step every frame does the same work run after run, and the
/// engine, the vsync off, draws them as fast as it can - a frame's wall time
/// is then what it cost.
///
/// <c>--perf-scenario blast</c> is the standard run (<see cref="PerfScenarios"/>).
/// </summary>
public sealed partial class Tank3DBench
{
    /// <summary>Frames left out of the figures at the start: loading,
    /// the first shaders.</summary>
    private const int PerfWarm = 60;

    /// <summary>Worse by this share, and by more than the figure's noise
    /// (<see cref="PerfFloor"/>), is a regression.</summary>
    private const double PerfWorse = 0.10;

    /// <summary>The runs a baseline is kept for: the frames, the board (null:
    /// the match's own), the cell the pointer is held on (<c>--hover</c>: the
    /// mouse over the window moves the way and the ghosts, BOT-1) and the
    /// <c>--do</c> script. Each one's baseline is <c>perf/NAME.json</c>
    /// (<see cref="PerfBaseFile"/>).</summary>
    private static readonly Dictionary<string, (int Frames, string? Map, Vector2I Hover, string[] Script)> PerfScenarios = new()
    {
        // drill10: the HM kills the TD in the heap - the blast, three set
        // alight, the burn-out chain at the end of brown's turn, J; ricochet
        // and the water left out
        ["blast"] = (1500, null, new Vector2I(5, 5), new[]
        {
            "take=6,7@40", "aim-cell=6,3@100", "pass@560", "take=6,7@620", "aim-cell=6,3@660",
            "pass@1100", "take=7,2@1160", "douse@1200",
        }),
        // stress10 (drill10's homes, a wood and a pond added): all green and
        // three brown set alight, then blast's opening - the HM's round knocks
        // the TD in the burning heap and hands the turn over; brown passes,
        // and its burning go up one by one, the TD's blast among them; green's
        // LT drives into the wood, green passes, and its five go up
        ["stress"] = (1600, "stress10", new Vector2I(5, 5), new[]
        {
            "ignite=1,7@20", "ignite=3,4@22", "ignite=7,2@24", "ignite=8,7@26", "ignite=6,7@28",
            "ignite=3,2@30", "ignite=6,2@32", "ignite=5,3@34",
            "take=6,7@40", "aim-cell=6,3@100", "pass@560", "take=1,7@620", "go=3,7@660",
            "pass@1100",
        }),
    };

    /// <summary>The baselines kept in git: <c>res://perf/NAME.json</c>, a
    /// run's <c>summary.json</c> copied there by hand (docs/perf.md).</summary>
    private static string PerfBaseFile(string scenario) =>
        ProjectSettings.GlobalizePath($"res://perf/{scenario}.json");

    private string? _perfDir;
    private int _perfTo = -1;
    private bool _perfBaseline;
    private bool _perfOverlay;
    private string _perfScenario = "";
    /// <summary><c>--perf-against FILE</c>: the baseline, before
    /// <c>DIR/baseline.json</c> and the scenario's own.</summary>
    private string? _perfAgainst;
    /// <summary>The scenario's pointer cell, when no <c>--hover</c> was given.</summary>
    private Vector2I? _perfHover;
    private PerfOverlay? _perfView;

    /// <summary>Its flags; true when <paramref name="i"/>'s was one.</summary>
    private bool ReadPerfFlag(string[] args, ref int i)
    {
        string a = args[i];
        bool more = i + 1 < args.Length;
        if (a == "--perf" && more)
        {
            _perfDir = args[++i];
            if (i + 1 < args.Length && int.TryParse(args[i + 1], out int frames))
            {
                _perfTo = frames;
                i++;
            }
            return true;
        }
        if (a == "--perf-scenario" && more)
        {
            _perfScenario = args[++i];
            if (PerfScenarios.TryGetValue(_perfScenario, out var sc))
            {
                foreach (string d in sc.Script)
                {
                    string[] parts = d.Split('@');
                    _script.Add(((int)F(parts[1], 0), parts[0]));
                }
                if (_perfTo < 0)
                    _perfTo = sc.Frames;
                // the flags' own --map and --hover win, wherever they stand
                if (sc.Map is not null && !_mapNamed)
                    (_mapName, _mapNamed) = (sc.Map, true);
                _perfHover = sc.Hover;
            }
            else
                GD.PrintErr($"perf: no scenario '{_perfScenario}' - there are {string.Join(", ", PerfScenarios.Keys)}");
            return true;
        }
        if (a == "--perf-against" && more) { _perfAgainst = args[++i]; return true; }
        if (a == "--perf-baseline") { _perfBaseline = true; return true; }
        if (a == "--perf-overlay") { _perfOverlay = true; return true; }
        return false;
    }

    /// <summary>In <c>_Ready</c>: the stopwatch on the viewport, the overlay
    /// on the layer, and a run's settings.</summary>
    private void BuildPerf(CanvasLayer layer)
    {
        Prof.Watch(GetViewport());
        _perfView = new PerfOverlay { Name = "Perf", Visible = _perfOverlay };
        layer.AddChild(_perfView);
        if (_perfDir is null)
            return;
        _hoverFixed ??= _perfHover;
        if (_perfTo < 0)
            _perfTo = Math.Max(600, _script.Count > 0 ? _script.Max(s => s.Frame) + 300 : 0);
        DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
        Engine.MaxFps = 0;
        Prof.Recording = true;
        Prof.Samples.Clear();
        // sized up front: a list doubling mid-run is the recording's own
        // allocation in the frame it lands on
        Prof.Capacity = _perfTo + 2;
        Prof.Samples.Capacity = Prof.Capacity;
        foreach (Prof.Section s in Prof.Sections)
        {
            s.Trace.Clear();
            s.AllocTrace.Clear();
            s.Trace.Capacity = s.AllocTrace.Capacity = Prof.Capacity;
        }
        Log.Print($"perf: recording {_perfTo} frames to {_perfDir}");
    }

    private double? _perfStep;
    private bool _perfFixed = true;
    private bool PerfFixed => _perfFixed;

    private void TogglePerf()
    {
        if (_perfView is not null)
            _perfView.Visible = !_perfView.Visible;
    }

    /// <summary>Every frame, first: the frame before closed, and the run's end.</summary>
    private void PerfTick(double delta)
    {
        Prof.Frame(_frame - 1);
        // --fixed-fps does not reach the user's args: the step tells it, the
        // same every frame to the last bit
        _perfStep ??= delta;
        if (delta != _perfStep)
            _perfFixed = false;
        if (_perfDir is not null && _frame == _perfTo + 1)
        {
            int worse = PerfWrite();
            GetTree().Quit(worse > 0 ? 1 : 0);
        }
    }

    // --- the files -------------------------------------------------------------------

    private int PerfWrite()
    {
        string dir = _perfDir!;
        if (!PerfFixed)
            Log.Print("perf: no --fixed-fps - the script's frames drift with the frame rate, and two runs play different games");
        Directory.CreateDirectory(dir);
        List<Prof.Sample> all = Prof.Samples.Where(s => s.Frame >= 0).ToList();
        List<Prof.Section> sections = Prof.Sections.ToList();
        int offset = Prof.Samples.Count - all.Count;
        WriteCsv(Path.Combine(dir, "frames.csv"), all, sections, offset);

        // the figures past the warm-up
        int from = Math.Min(PerfWarm, all.Count);
        List<Prof.Sample> run = all.Skip(from).ToList();
        var metrics = new JsonObject
        {
            ["frame_ms"] = Stats(run.Select(s => s.FrameMs)),
            ["render_cpu_ms"] = Stats(run.Select(s => s.RenderCpuMs)),
            ["gpu_ms"] = Stats(run.Select(s => s.GpuMs)),
            ["draw_calls"] = Stats(run.Select(s => (double)s.DrawCalls)),
            ["primitives"] = Stats(run.Select(s => (double)s.Primitives)),
            ["nodes"] = Stats(run.Select(s => (double)s.Nodes)),
            ["orphans"] = Stats(run.Select(s => (double)s.Orphans)),
            ["alloc_bytes"] = Stats(run.Select(s => (double)s.AllocBytes)),
            ["gc0"] = run.Sum(s => s.Gc0),
            ["video_mb"] = run.Count > 0 ? Math.Round(run.Max(s => s.VideoMb), 1) : 0,
        };
        var secs = new JsonObject();
        // and what each allocated, KB a frame: whose bytes the GC pauses are
        var secsKb = new JsonObject();
        foreach (Prof.Section sec in sections)
        {
            secs[sec.Name] = Stats(sec.Trace.Skip(offset + from).Take(run.Count));
            secsKb[sec.Name] = Stats(sec.AllocTrace.Skip(offset + from).Take(run.Count).Select(b => b / 1024.0));
        }
        // the worst frames, and which section had them
        var worst = new JsonArray();
        foreach ((Prof.Sample s, int i) in run.Select((s, i) => (s, i)).OrderByDescending(p => p.s.FrameMs).Take(8))
        {
            int at = offset + from + i;
            // three, not one: sections nest (takeover inside match or drive), and
            // the outer one alone would hide the inner
            string heavy = string.Join(", ", sections.Where(x => x.Name != "bench" && at < x.Trace.Count)
                                                     .OrderByDescending(x => x.Trace[at]).Take(3)
                                                     .Select(x => x.Name + " " + x.Trace[at].ToString("F2", CultureInfo.InvariantCulture)));
            worst.Add(new JsonObject
            {
                ["frame"] = s.Frame,
                ["frame_ms"] = Math.Round(s.FrameMs, 2),
                ["gpu_ms"] = Math.Round(s.GpuMs, 2),
                ["alloc_bytes"] = s.AllocBytes,
                ["gc0"] = s.Gc0,
                ["heaviest_ms"] = heavy,
            });
        }
        var summary = new JsonObject
        {
            ["when"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            ["scene"] = GetTree().CurrentScene?.SceneFilePath ?? "",
            ["scenario"] = _perfScenario,
            ["map"] = _mapName,
            // what the run played - the comparison asks this, not the args,
            // which carry the output's paths and the captures' flags
            ["script"] = string.Join(" ", _script.Select(s => $"{s.What}@{s.Frame}")),
            ["args"] = string.Join(" ", OS.GetCmdlineUserArgs()),
            ["fixed_fps"] = PerfFixed,
            ["gpu"] = RenderingServer.GetVideoAdapterName(),
            ["window"] = $"{GetViewport().GetVisibleRect().Size.X}x{GetViewport().GetVisibleRect().Size.Y}",
            ["frames"] = run.Count,
            ["warm_up"] = from,
            ["metrics"] = metrics,
            ["sections"] = secs,
            ["section_alloc_kb"] = secsKb,
            ["worst"] = worst,
        };
        string json = summary.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        string path = Path.Combine(dir, "summary.json");
        File.WriteAllText(path, json);
        JsonObject m = metrics;
        Log.Print($"perf: {run.Count} frames - frame avg {Get(m, "frame_ms", "avg"):F2} ms, p99 {Get(m, "frame_ms", "p99"):F2}, "
                 + $"max {Get(m, "frame_ms", "max"):F2}; GPU avg {Get(m, "gpu_ms", "avg"):F2}; "
                 + $"alloc {Get(m, "alloc_bytes", "avg") / 1024.0:F1} KB/frame, GC0 {m["gc0"]} - {path}");

        int worseCount = 0;
        string basePath = Path.Combine(dir, "baseline.json");
        // against: the file named, the directory's own, the scenario's in git
        string? against = _perfAgainst
            ?? (File.Exists(basePath) ? basePath
                : _perfScenario != "" && File.Exists(PerfBaseFile(_perfScenario)) ? PerfBaseFile(_perfScenario)
                : null);
        if (against is not null && !File.Exists(against))
            GD.PrintErr($"perf: --perf-against {against}: no such file");
        else if (against is not null)
            worseCount = Compare(against, summary, Path.Combine(dir, "compare.txt"));
        if (_perfBaseline)
        {
            File.WriteAllText(basePath, json);
            Log.Print($"perf: this run is the baseline now - {basePath}");
        }
        return worseCount;
    }

    private static void WriteCsv(string path, List<Prof.Sample> rows, List<Prof.Section> sections, int offset)
    {
        var sb = new StringBuilder();
        sb.Append("frame,frame_ms,render_cpu_ms,gpu_ms,draw_calls,primitives,objects,nodes,orphans,alloc_bytes,gc0");
        foreach (Prof.Section s in sections)
            sb.Append(',').Append(s.Name);
        // then each section's bytes, the same order
        foreach (Prof.Section s in sections)
            sb.Append(",kb:").Append(s.Name);
        sb.Append('\n');
        var ci = CultureInfo.InvariantCulture;
        for (int i = 0; i < rows.Count; i++)
        {
            Prof.Sample r = rows[i];
            sb.Append(ci, $"{r.Frame},{r.FrameMs:F3},{r.RenderCpuMs:F3},{r.GpuMs:F3},{r.DrawCalls},{r.Primitives},{r.Objects},{r.Nodes},{r.Orphans},{r.AllocBytes},{r.Gc0}");
            foreach (Prof.Section s in sections)
            {
                int at = offset + i;
                sb.Append(',').Append((at < s.Trace.Count ? s.Trace[at] : 0.0).ToString("F3", ci));
            }
            foreach (Prof.Section s in sections)
            {
                int at = offset + i;
                sb.Append(',').Append((at < s.AllocTrace.Count ? s.AllocTrace[at] / 1024.0 : 0.0).ToString("F2", ci));
            }
            sb.Append('\n');
        }
        File.WriteAllText(path, sb.ToString());
    }

    private static JsonObject Stats(IEnumerable<double> values)
    {
        List<double> v = values.ToList();
        v.Sort();
        return new JsonObject
        {
            ["avg"] = Math.Round(v.Count > 0 ? v.Average() : 0.0, 4),
            ["p50"] = Math.Round(Prof.Percentile(v, 0.50), 4),
            ["p95"] = Math.Round(Prof.Percentile(v, 0.95), 4),
            ["p99"] = Math.Round(Prof.Percentile(v, 0.99), 4),
            ["max"] = Math.Round(v.Count > 0 ? v[^1] : 0.0, 4),
        };
    }

    private static double Get(JsonObject o, string key, string stat) =>
        o[key] is JsonObject s && s[stat] is JsonNode n ? n.GetValue<double>() : 0.0;

    // --- against the baseline ------------------------------------------------------

    /// <summary>Below this a difference is noise, whatever its share: ms for
    /// times, bytes for allocations, a count for the rest.</summary>
    private static double PerfFloor(string key) =>
        key.EndsWith("_ms") || key.StartsWith("section ") ? 0.2
        : key.StartsWith("alloc") ? 2048.0
        : 2.0;

    /// <summary>The run against the baseline, written to
    /// <paramref name="outPath"/> and printed; how many got worse.</summary>
    private static int Compare(string basePath, JsonObject now, string outPath)
    {
        JsonNode? old;
        try
        {
            old = JsonNode.Parse(File.ReadAllText(basePath));
        }
        catch (Exception e)
        {
            GD.PrintErr($"perf: baseline {basePath} does not read: {e.Message}");
            return 0;
        }
        if (old is not JsonObject was)
            return 0;
        var sb = new StringBuilder();
        sb.AppendLine($"against {basePath}");
        sb.AppendLine($"baseline {was["when"]} ({was["frames"]} frames)  vs  now {now["when"]} ({now["frames"]} frames)");
        foreach (string k in new[] { "scene", "map", "script", "frames", "fixed_fps", "window" })
            if (was[k]?.ToJsonString() != now[k]?.ToJsonString())
                sb.AppendLine($"!! another {k} - baseline: {was[k]}, now: {now[k]}");
        if (was["gpu"]?.ToString() != now["gpu"]?.ToString())
            sb.AppendLine($"!! another GPU - baseline: {was["gpu"]}, now: {now["gpu"]}");
        sb.AppendLine();
        int worse = 0;

        void Row(string name, double a, double b)
        {
            double d = b - a;
            double rel = a > 1e-9 ? d / a : (b > 1e-9 ? 1.0 : 0.0);
            bool bad = d > PerfFloor(name) && rel > PerfWorse;
            bool good = -d > PerfFloor(name) && -rel > PerfWorse;
            if (bad)
                worse++;
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0} {1,-34} {2,12:F2} -> {3,12:F2}  {4,7:+0;-0;0}%",
                                        bad ? "▲" : good ? "▼" : " ", name, a, b, rel * 100.0));
        }

        string[] stats = { "avg", "p95", "p99", "max" };
        if (was["metrics"] is JsonObject wm && now["metrics"] is JsonObject nm)
            foreach ((string key, JsonNode? node) in nm)
            {
                if (node is not JsonObject)
                    continue;
                foreach (string st in key is "frame_ms" or "gpu_ms" or "render_cpu_ms" ? stats
                                      : key == "alloc_bytes" ? new[] { "avg", "p50", "max" } : new[] { "avg", "max" })
                    if (wm[key] is JsonObject)
                        Row($"{key} {st}", Get(wm, key, st), Get(nm, key, st));
            }
        if (was["sections"] is JsonObject ws && now["sections"] is JsonObject ns)
        {
            foreach (var (key, _) in ns)
                if (ws[key] is JsonObject)
                    foreach (string st in new[] { "avg", "p99" })
                        Row($"section {key} {st}", Get(ws, key, st), Get(ns, key, st));
                else
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "+ section {0,-26} new: avg {1:F2}, p99 {2:F2}",
                                                key, Get(ns, key, "avg"), Get(ns, key, "p99")));
            foreach (var (key, _) in ws)
                if (ns[key] is null)
                    sb.AppendLine($"- section {key} not measured now");
        }
        // a section's KB a frame, where either run has some (a floor of 2 KB,
        // the counts'): a baseline from before the count has none to compare
        if (was["section_alloc_kb"] is JsonObject wk && now["section_alloc_kb"] is JsonObject nk)
            foreach (var (key, _) in nk)
                if (wk[key] is JsonObject && Math.Max(Get(wk, key, "avg"), Get(nk, key, "avg")) >= 0.1)
                    foreach (string st in new[] { "avg", "p50" })
                        Row($"kb {key} {st}", Get(wk, key, st), Get(nk, key, st));
        sb.AppendLine();
        sb.AppendLine(worse == 0 ? "nothing worse than the baseline" : $"{worse} worse than the baseline (▲)");
        string text = sb.ToString();
        File.WriteAllText(outPath, text);
        Log.Print(text);
        return worse;
    }
}
