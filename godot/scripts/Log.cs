using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The 3D line's log (2026-10-24, BOT-8): <see cref="Print"/> instead of
/// <c>GD.Print</c>.
///
/// <b>A print in a frame cost up to 300 ms.</b> A debug build flushes stdout
/// after every line, and the console build's stdout is a pipe: the flush waits
/// for whoever reads it, and how long depends on the reader - a hand-over, a
/// shell landing, a blast stood 10 ms in one run and 100-300 in the next
/// (docs/perf.md, BOT-7 and BOT-8). Here the line goes on a queue and one
/// thread of its own prints the queue in order, so the wait is that thread's.
///
/// <see cref="Drain"/> waits for the queue to empty: the bench calls it as it
/// leaves the tree, which a quit does, so a run's last lines and its
/// <c>perf:</c> summary are printed before the process goes. A main thread
/// that hangs leaves the writer printing, which is why the flush stays on.
/// </summary>
public static class Log
{
    private static readonly BlockingCollection<string> Lines = new();
    private static readonly object Gate = new();
    private static Thread? _writer;

    /// <summary>Lines queued and not yet printed.</summary>
    private static int _pending;

    /// <summary>Print <paramref name="line"/> on the log's thread, in the
    /// order lines were given; returns at once.</summary>
    public static void Print(string line)
    {
        Start();
        Interlocked.Increment(ref _pending);
        Lines.Add(line);
    }

    /// <summary>Wait until every queued line is printed, at most
    /// <paramref name="timeoutMs"/>.</summary>
    public static void Drain(int timeoutMs = 2000)
    {
        var watch = Stopwatch.StartNew();
        while (Volatile.Read(ref _pending) > 0 && watch.ElapsedMilliseconds < timeoutMs)
            Thread.Sleep(1);
    }

    private static void Start()
    {
        if (_writer is not null)
            return;
        lock (Gate)
        {
            if (_writer is not null)
                return;
            _writer = new Thread(Write) { IsBackground = true, Name = "Log" };
            _writer.Start();
        }
    }

    private static void Write()
    {
        foreach (string line in Lines.GetConsumingEnumerable())
        {
            GD.Print(line);
            Interlocked.Decrement(ref _pending);
        }
    }
}
