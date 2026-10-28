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
        Ring ring = RingOf(Columns, Rows, OnBoard);
        int[] levels = ring.Spread(Levels, Plinth - 1);
        bool[] water = ring.Spread(Water, true);
        Foundation[] ground = ring.Spread(Ground, Foundation.Deep);
        var homes = Parked.Select(p => p with { Cell = p.Cell + ring.Shift }).ToList();
        var walling = Walling.ToDictionary(kv => kv.Key + ring.Shift, kv => kv.Value);
        var rocking = Rocking.ToDictionary(kv => kv.Key + ring.Shift, kv => kv.Value);
        return new BoardMap(Name, ring.Columns, ring.Rows, homes, levels,
                            ring.Spread(Ramps, false), water, ring.Spread(Kinds, null),
                            ring.Spread(Cliffs, false), ring.Spread(Walls, false), ground,
                            ring.Spread(Over, Cover.None), ring.Plot, Paint, Height, Plinth,
                            walling, rocking, Climate, SoilBase,
                            SoilCells is null ? null : ring.Spread(SoilCells, Soil.As),
                            Roads.Select(r => r.Moved(ring.Shift)).ToList(),
                            Unpaved is null ? null : ring.Spread(Unpaved, Foundation.Deep),
                            WoodMix, TreeCells is null ? null : ring.Spread(TreeCells, Species.As))
        {
            Sea = ring.Sea,
            Shift = ring.Shift,
        };
    }

    /// <summary>
    /// Where a board of <paramref name="columns"/> by <paramref name="rows"/>
    /// lands in its sea (<see cref="Moated"/>), asked of the cells alone - the
    /// editor rings a draft that need not build (MapEditor.Settle).
    ///
    /// A side whose whole edge is off the board already has its sea - the
    /// holes flood - and gets no second ring: ABBEY is ringed by its own.
    /// </summary>
    public static Ring RingOf(int columns, int rows, Func<Vector2I, bool> onBoard)
    {
        bool OffColumn(int q) => Enumerable.Range(0, rows).All(r => !onBoard(new Vector2I(q, r)));
        bool OffRow(int r) => Enumerable.Range(0, columns).All(q => !onBoard(new Vector2I(q, r)));
        int left = OffColumn(0) ? 0 : 2, right = OffColumn(columns - 1) ? 0 : 1;
        int up = OffRow(0) ? 0 : 1, down = OffRow(rows - 1) ? 0 : 1;
        var shift = new Vector2I(left, up);
        int wide = columns + left + right, high = rows + up + down;
        var plot = new HashSet<Vector2I>();
        var sea = new HashSet<Vector2I>();
        var from = new int[wide * high];
        for (int r = 0; r < high; r++)
        for (int q = 0; q < wide; q++)
        {
            var cell = new Vector2I(q, r);
            Vector2I was = cell - shift;
            from[r * wide + q] = -1;
            // the outer left column is off the plot, the frame stands over it
            if (left > 0 && q == 0)
                continue;
            plot.Add(cell);
            if (was.X < 0 || was.X >= columns || was.Y < 0 || was.Y >= rows || !onBoard(was))
            {
                sea.Add(cell);
                continue;
            }
            from[r * wide + q] = was.Y * columns + was.X;
        }
        return new Ring(wide, high, shift, plot, sea, from);
    }

    /// <summary>A board in its sea: the size, how far the map's cells moved,
    /// the plot and the sea, and which map cell each ringed one came from.
    /// </summary>
    public sealed record Ring(int Columns, int Rows, Vector2I Shift,
                              HashSet<Vector2I> Plot, HashSet<Vector2I> Sea, int[] From)
    {
        /// <summary>
        /// A per-cell array of the map laid out on the ring, the sea's cells
        /// given <paramref name="sea"/>.
        ///
        /// The sea's level is deep water's: the level the letter d stands at (a
        /// level under the plinth), so one sea and every deep pond of the map
        /// are one water, and a pond on the edge runs into it (the user's call,
        /// 2026-10-25). What on the edge stands lower than its surface - a dry
        /// pit, a ford - is dammed off by the relief (Relief.Weir).
        /// </summary>
        public T[] Spread<T>(T[] map, T sea)
        {
            var laid = new T[From.Length];
            for (int i = 0; i < From.Length; i++)
            {
                int at = From[i];
                if (at >= 0)
                    laid[i] = at < map.Length ? map[at] : default!;
                else if (Sea.Contains(new Vector2I(i % Columns, i / Columns)))
                    laid[i] = sea;
            }
            return laid;
        }
    }
}
