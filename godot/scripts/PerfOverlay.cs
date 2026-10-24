using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The frame's cost in the top right corner (2026-10-24), F3 on the bench or
/// <c>--perf-overlay</c> from the start: the wall time with its p99 and worst
/// over the graph's frames, the render's CPU and GPU, what is drawn, the nodes
/// and the orphans, the bytes C# allocated, and the bench's sections
/// (<see cref="Prof"/>) heaviest first.
///
/// The graph is <see cref="Prof.History"/> frames, a bar each, against the
/// 60 and 30 fps lines; a bar over 60's is amber, over 30's red - a hitch is a
/// bar, where an average would have hidden it.
/// </summary>
public sealed partial class PerfOverlay : Control
{
    private const float Width = 380.0f;
    private const float GraphH = 64.0f;
    private const float Pad = 8.0f;
    private const int Line = 16;

    private static readonly Color Back = new(0.05f, 0.06f, 0.07f, 0.78f);
    private static readonly Color Ink = new(0.92f, 0.94f, 0.95f);
    private static readonly Color Dim = new(0.62f, 0.66f, 0.70f);
    private static readonly Color Good = new(0.40f, 0.78f, 0.45f);
    private static readonly Color Warn = new(0.95f, 0.70f, 0.25f);
    private static readonly Color Bad = new(0.95f, 0.32f, 0.28f);

    private readonly List<double> _sorted = new(Prof.History);
    private readonly List<string> _lines = new();

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
    }

    public override void _Process(double delta)
    {
        if (Visible)
            QueueRedraw();
    }

    public override void _Draw()
    {
        Font font = ThemeDB.FallbackFont;
        Prof.Sample s = Prof.Last;

        _sorted.Clear();
        for (int i = 0; i < Prof.HistoryCount; i++)
            _sorted.Add(Prof.FrameHistory[i]);
        _sorted.Sort();
        double avg = _sorted.Count > 0 ? _sorted.Average() : 0.0;
        double p99 = Prof.Percentile(_sorted, 0.99);
        double max = _sorted.Count > 0 ? _sorted[^1] : 0.0;

        _lines.Clear();
        _lines.Add($"{(avg > 0 ? 1000.0 / avg : 0):F0} fps   frame {avg:F1} ms   p99 {p99:F1}   max {max:F1}");
        _lines.Add($"render CPU {s.RenderCpuMs:F1}   GPU {s.GpuMs:F1} ms");
        _lines.Add($"draw {s.DrawCalls}   prims {Short(s.Primitives)}   objects {s.Objects}");
        _lines.Add($"nodes {s.Nodes}   orphans {s.Orphans}   VRAM {s.VideoMb:F0} MB");
        _lines.Add($"alloc {s.AllocBytes / 1024.0:F1} KB/frame   GC0 +{s.Gc0}  ({GC.CollectionCount(0)})");

        var heavy = Prof.Sections.Where(x => x.Name != "bench").OrderByDescending(x => x.Avg).Take(8).ToList();
        Prof.Section? bench = Prof.Sections.Find(x => x.Name == "bench");
        float height = Pad * 2 + Line * (_lines.Count + 1 + heavy.Count) + GraphH + Pad;
        // the bottom right: the top right is the match's button
        float x0 = Size.X - Width - 12.0f;
        float top = Size.Y - height - 12.0f;
        DrawRect(new Rect2(x0, top, Width, height), Back);

        float y = top + Pad;
        foreach (string l in _lines)
        {
            y += Line;
            DrawString(font, new Vector2(x0 + Pad, y - 3), l, HorizontalAlignment.Left, -1, 13, Ink);
        }

        // the graph: a bar a frame, the 60 and 30 fps lines across
        y += Pad;
        float gw = Width - Pad * 2;
        // by the p99, not the worst: one hand-over's 600 ms would flatten every
        // other bar to nothing - a bar over the top is cut, and red anyway
        double scale = Math.Max(40.0, p99 * 1.25);
        var graph = new Rect2(x0 + Pad, y, gw, GraphH);
        DrawRect(graph, new Color(0, 0, 0, 0.35f));
        float bw = gw / Prof.History;
        for (int i = 0; i < Prof.HistoryCount; i++)
        {
            int at = (Prof.HistoryAt - Prof.HistoryCount + i + Prof.History) % Prof.History;
            double ms = Prof.FrameHistory[at];
            float h = Mathf.Min((float)(ms / scale), 1.0f) * GraphH;
            Color c = ms > 33.4 ? Bad : ms > 16.8 ? Warn : Good;
            DrawRect(new Rect2(graph.Position.X + (Prof.History - Prof.HistoryCount + i) * bw,
                               y + GraphH - h, Math.Max(bw, 1.0f), h), c);
        }
        foreach (double mark in new[] { 1000.0 / 60.0, 1000.0 / 30.0 })
        {
            float my = y + GraphH - (float)(mark / scale) * GraphH;
            DrawLine(new Vector2(graph.Position.X, my), new Vector2(graph.End.X, my), Dim, 1.0f);
        }
        DrawString(font, new Vector2(graph.Position.X + 2, y + 11), $"{scale:F0} ms", HorizontalAlignment.Left, -1, 11, Dim);
        y += GraphH;

        // the bench's sections, heaviest first
        y += Line;
        DrawString(font, new Vector2(x0 + Pad, y - 3),
                   $"bench {bench?.Avg ?? 0:F2} ms   (avg / max of {Prof.Window})", HorizontalAlignment.Left, -1, 13, Dim);
        foreach (Prof.Section sec in heavy)
        {
            y += Line;
            Color c = sec.Max > 4.0 ? Bad : sec.Max > 1.0 ? Warn : Ink;
            DrawString(font, new Vector2(x0 + Pad, y - 3), sec.Name, HorizontalAlignment.Left, -1, 13, c);
            DrawString(font, new Vector2(x0 + Pad + 170, y - 3), $"{sec.Avg:F2}  /  {sec.Max:F2}",
                       HorizontalAlignment.Left, -1, 13, c);
        }
    }

    private static string Short(long n) =>
        n >= 1_000_000 ? $"{n / 1e6:F1}M" : n >= 1000 ? $"{n / 1e3:F0}k" : n.ToString();
}
