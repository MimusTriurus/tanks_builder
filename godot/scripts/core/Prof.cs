using System;
using System.Collections.Generic;
using System.Diagnostics;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The bench's own stopwatch (2026-10-24): what a frame costs, and which part
/// of the bench's code it went on.
///
/// <list type="bullet">
/// <item><b>Sections</b> - <c>using (Prof.Measure("blast")) BlastTick(dt);</c>
/// adds the time spent inside to the section's frame total. Each section is
/// also a custom monitor, <c>bench/&lt;name&gt;</c>, so the editor's
/// Debugger → Monitors draws it beside the engine's own: the editor's Profiler
/// looks into GDScript only, and the bench is C#.</item>
/// <item><b>The frame</b> - <see cref="Frame"/>, once at the top of the
/// bench's <c>_Process</c>, closes the frame before it: the wall time between
/// the two, the render's CPU and GPU time, draw calls, nodes, and the bytes the
/// main thread allocated - a C# allocation per frame is a GC pause later.</item>
/// </list>
///
/// Read by the overlay (<see cref="PerfOverlay"/>, F3) and by the
/// <c>--perf</c> run (<c>Tank3DBench.Perf</c>), which turns
/// <see cref="Recording"/> on and gets every frame back.
/// </summary>
public static class Prof
{
    /// <summary>Frames the averages and maxima are taken over.</summary>
    public const int Window = 60;

    /// <summary>Frames kept for the overlay's graph.</summary>
    public const int History = 240;

    public sealed class Section
    {
        public string Name = "";
        /// <summary>This frame so far, ms.</summary>
        internal double Now;
        /// <summary>The frame just closed, ms.</summary>
        public double Last;
        public double Avg;
        public double Max;
        internal readonly double[] Ring = new double[Window];
        /// <summary>Every frame's, while <see cref="Recording"/>.</summary>
        public readonly List<double> Trace = new();
        /// <summary>Bytes the main thread allocated inside: this frame so far,
        /// the frame just closed, its average over <see cref="Window"/> and
        /// every frame's while <see cref="Recording"/>. Which section a GC
        /// pause is owed to (BOT-3): the frame's own count says only that it is.</summary>
        internal long AllocNow;
        public long AllocLast;
        public double AllocAvg;
        internal readonly long[] AllocRing = new long[Window];
        public readonly List<long> AllocTrace = new();
    }

    /// <summary>One closed frame.</summary>
    public struct Sample
    {
        public int Frame;
        public double FrameMs;
        public double RenderCpuMs;
        public double GpuMs;
        public long DrawCalls;
        public long Primitives;
        public long Objects;
        public long Nodes;
        public long Orphans;
        public long AllocBytes;
        public int Gc0;
        public double VideoMb;
    }

    private static readonly Dictionary<string, Section> _byName = new();
    public static readonly List<Section> Sections = new();

    /// <summary>The frame just closed.</summary>
    public static Sample Last;

    /// <summary>The last <see cref="History"/> frames' wall times, oldest
    /// first from <see cref="HistoryAt"/>.</summary>
    public static readonly double[] FrameHistory = new double[History];
    public static int HistoryAt;
    public static int HistoryCount;

    /// <summary>Frames since the start of the process.</summary>
    public static int Frames;

    /// <summary>Every closed frame kept (<see cref="Samples"/>, each section's
    /// <see cref="Section.Trace"/>).</summary>
    public static bool Recording;
    public static readonly List<Sample> Samples = new();
    /// <summary>Frames a recording will hold: a section's lists are made this
    /// big when it comes in, so none grows mid-run.</summary>
    public static int Capacity;

    private static long _lastTick;
    private static long _allocAt = -1;
    private static int _gcAt = -1;
    private static Rid _viewport;

    public readonly struct Scope : IDisposable
    {
        private readonly Section _section;
        private readonly long _from;
        private readonly long _bytes;

        internal Scope(Section section)
        {
            _section = section;
            _from = Stopwatch.GetTimestamp();
            _bytes = GC.GetAllocatedBytesForCurrentThread();
        }

        public void Dispose()
        {
            _section.Now += (Stopwatch.GetTimestamp() - _from) * 1000.0 / Stopwatch.Frequency;
            _section.AllocNow += GC.GetAllocatedBytesForCurrentThread() - _bytes;
        }
    }

    public static Scope Measure(string name) => new(Get(name));

    private static Section Get(string name)
    {
        if (_byName.TryGetValue(name, out Section? s))
            return s;
        s = new Section { Name = name };
        _byName[name] = s;
        Sections.Add(s);
        // a section that comes in late in a recording: nought for the frames before it
        if (Recording)
        {
            s.Trace.Capacity = s.AllocTrace.Capacity = Math.Max(Capacity, Samples.Count);
            for (int i = 0; i < Samples.Count; i++)
            {
                s.Trace.Add(0.0);
                s.AllocTrace.Add(0);
            }
        }
        string id = "bench/" + name;
        // statics outlive a scene reload (Backspace), and the monitors with them
        if (!Performance.HasCustomMonitor(id))
        {
            Section held = s;
            Performance.AddCustomMonitor(id, Callable.From(() => held.Avg));
        }
        return s;
    }

    /// <summary>The render's own times measured from here on - the viewport's
    /// CPU and GPU, which cost nothing worth counting.</summary>
    public static void Watch(Viewport viewport)
    {
        _viewport = viewport.GetViewportRid();
        RenderingServer.ViewportSetMeasureRenderTime(_viewport, true);
        if (!Performance.HasCustomMonitor("bench/frame_ms"))
        {
            Performance.AddCustomMonitor("bench/frame_ms", Callable.From(() => Last.FrameMs));
            Performance.AddCustomMonitor("bench/gpu_ms", Callable.From(() => Last.GpuMs));
            Performance.AddCustomMonitor("bench/alloc_kb", Callable.From(() => Last.AllocBytes / 1024.0));
        }
    }

    /// <summary>The frame before this one closed; <paramref name="frame"/> is
    /// its number.</summary>
    public static void Frame(int frame)
    {
        long tick = Stopwatch.GetTimestamp();
        double wall = _lastTick == 0 ? 0.0 : (tick - _lastTick) * 1000.0 / Stopwatch.Frequency;
        _lastTick = tick;
        long alloc = GC.GetAllocatedBytesForCurrentThread();
        int gc = GC.CollectionCount(0);
        var s = new Sample
        {
            Frame = frame,
            FrameMs = wall,
            DrawCalls = (long)RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TotalDrawCallsInFrame),
            Primitives = (long)RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TotalPrimitivesInFrame),
            Objects = (long)RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TotalObjectsInFrame),
            Nodes = (long)Performance.GetMonitor(Performance.Monitor.ObjectNodeCount),
            Orphans = (long)Performance.GetMonitor(Performance.Monitor.ObjectOrphanNodeCount),
            AllocBytes = _allocAt < 0 ? 0 : alloc - _allocAt,
            Gc0 = _gcAt < 0 ? 0 : gc - _gcAt,
            VideoMb = Performance.GetMonitor(Performance.Monitor.RenderVideoMemUsed) / (1024.0 * 1024.0),
        };
        if (_viewport.IsValid)
        {
            s.RenderCpuMs = RenderingServer.ViewportGetMeasuredRenderTimeCpu(_viewport);
            s.GpuMs = RenderingServer.ViewportGetMeasuredRenderTimeGpu(_viewport);
        }
        _allocAt = alloc;
        _gcAt = gc;
        Last = s;
        FrameHistory[HistoryAt] = wall;
        HistoryAt = (HistoryAt + 1) % History;
        HistoryCount = Math.Min(HistoryCount + 1, History);

        int slot = Frames % Window;
        int n = Math.Min(Frames + 1, Window);
        foreach (Section sec in Sections)
        {
            sec.Last = sec.Now;
            sec.Now = 0.0;
            sec.Ring[slot] = sec.Last;
            double sum = 0.0, max = 0.0;
            for (int i = 0; i < n; i++)
            {
                sum += sec.Ring[i];
                max = Math.Max(max, sec.Ring[i]);
            }
            sec.Avg = sum / n;
            sec.Max = max;
            sec.AllocLast = sec.AllocNow;
            sec.AllocNow = 0;
            sec.AllocRing[slot] = sec.AllocLast;
            long bytes = 0;
            for (int i = 0; i < n; i++)
                bytes += sec.AllocRing[i];
            sec.AllocAvg = (double)bytes / n;
            if (Recording)
            {
                sec.Trace.Add(sec.Last);
                sec.AllocTrace.Add(sec.AllocLast);
            }
        }
        if (Recording)
            Samples.Add(s);
        Frames++;
    }

    /// <summary>The <paramref name="p"/>-th share (0..1) of
    /// <paramref name="sorted"/>, nearest rank.</summary>
    public static double Percentile(IReadOnlyList<double> sorted, double p)
    {
        if (sorted.Count == 0)
            return 0.0;
        int i = (int)Math.Ceiling(p * sorted.Count) - 1;
        return sorted[Math.Clamp(i, 0, sorted.Count - 1)];
    }
}
