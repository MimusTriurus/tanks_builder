using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace TankSpriteTest;

public sealed partial class BoardMap
{
    /// <summary>Which cells are the sea round the board (<see cref="Moated"/>),
    /// or null on a board without one. Handed to the field
    /// (<see cref="HexField.Sea"/>): the one-surface guard does not ask it.</summary>
    public IReadOnlySet<Vector2I>? Sea { get; private init; }

    /// <summary>How far every cell of the authored map moved in this one:
    /// (0, 0) on a board without a sea.</summary>
    public Vector2I Shift { get; private init; }

    /// <summary>
    /// This board in a sea (BOT-11, <c>--relief</c>): a ring of deep water
    /// cells round it and its holes (<see cref="Off"/>) flooded too, so that
    /// between the land and the board's frame there is at least one hex a
    /// tank can be rammed into (the user's call, 2026-10-25).
    ///
    /// <b>Real cells, for the rules too</b>: deep water, entered as a fall
    /// (<see cref="Ramming"/>), swum or drowned in. A level under the
    /// commonest level of the land on their shore, so a bank is a step down
    /// into them from almost everywhere.
    ///
    /// <b>Two columns on the left and one on the right, and the outer left
    /// one off the board.</b> A flat-top odd-q grid shifts its odd columns
    /// down; moved by one column, every cell would swap parity and its
    /// neighbours with it. So the board moves by two, and the first column,
    /// which would make the left margin two hexes wide, is left off the plot
    /// - the frame stands over it (<c>Relief.Frame</c>).
    ///
    /// <b>The sea is exempt from the one-surface guard</b> (<see cref="Sea"/>):
    /// a river running off the board is a ford, and the whole of it beside a
    /// deep ring is a waterfall by the rule - and a ring of ford is a lane round
    /// the board to drive in. The surfaces meet with a step; the map keeps its
    /// river.
    /// </summary>
    public BoardMap Moated()
    {
        // A side whose whole edge is off the board already has its sea - the
        // holes flood - and gets no second ring: ABBEY is ringed by its own.
        bool OffColumn(int q) => Enumerable.Range(0, Rows).All(r => !OnBoard(new Vector2I(q, r)));
        bool OffRow(int r) => Enumerable.Range(0, Columns).All(q => !OnBoard(new Vector2I(q, r)));
        int left = OffColumn(0) ? 0 : 2, right = OffColumn(Columns - 1) ? 0 : 1;
        int up = OffRow(0) ? 0 : 1, down = OffRow(Rows - 1) ? 0 : 1;
        var shift = new Vector2I(left, up);
        int columns = Columns + left + right, rows = Rows + up + down, n = columns * rows;
        var levels = new int[n];
        var ramps = new bool[n];
        var water = new bool[n];
        var kinds = new string?[n];
        var cliffs = new bool[n];
        var walls = new bool[n];
        var ground = new Foundation[n];
        var over = new Cover[n];
        var plot = new HashSet<Vector2I>();
        var sea = new HashSet<Vector2I>();
        for (int r = 0; r < rows; r++)
        for (int q = left > 0 ? 1 : 0; q < columns; q++)
        {
            var cell = new Vector2I(q, r);
            plot.Add(cell);
            Vector2I was = cell - shift;
            if (!OnBoard(was))
            {
                sea.Add(cell);
                continue;
            }
            int to = r * columns + q, from = At(was);
            levels[to] = Levels[from];
            ramps[to] = Ramps[from];
            water[to] = Water[from];
            kinds[to] = Kinds[from];
            cliffs[to] = Cliffs[from];
            walls[to] = Walls[from];
            ground[to] = Ground[from];
            over[to] = Over[from];
        }

        // the sea's level: a level under the commonest dry level on its shore
        var count = new Dictionary<int, int>();
        foreach (Vector2I cell in plot)
        {
            if (sea.Contains(cell) || water[cell.Y * columns + cell.X])
                continue;
            if (!HexField.EdgeHeadings.Any(h => sea.Contains(HexField.Step(cell, h))))
                continue;
            int l = levels[cell.Y * columns + cell.X];
            count[l] = count.GetValueOrDefault(l) + 1;
        }
        int shore = count.Count == 0 ? Plinth : count.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).First().Key;
        foreach (Vector2I cell in sea)
        {
            int at = cell.Y * columns + cell.X;
            levels[at] = shore - 1;
            water[at] = true;
            ground[at] = Foundation.Deep;
        }

        var homes = Parked.Select(p => p with { Cell = p.Cell + shift }).ToList();
        var walling = Walling.ToDictionary(kv => kv.Key + shift, kv => kv.Value);
        return new BoardMap(Name, columns, rows, homes, levels, ramps, water, kinds,
                            cliffs, walls, ground, over, plot, Paint, Height, Plinth, walling)
        {
            Sea = sea,
            Shift = shift,
        };
    }
}
