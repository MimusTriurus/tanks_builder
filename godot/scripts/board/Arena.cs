using System;
using System.Collections.Generic;
using System.Linq;

using Godot;

namespace TankSpriteTest;

/// <summary>
/// The control point a map marks (GDD match.md, «Удержание точки»): one hex, or
/// the hex with its six neighbours (<see cref="Ring"/>), and the points each
/// side starts with (<see cref="Score"/>, N - the map's to say, U-01 names no
/// number).
/// </summary>
public readonly record struct ControlPoint(Vector2I Cell, bool Ring, int Score)
{
    /// <summary>The least a side may start with: nought would be a match lost
    /// before it began.</summary>
    public const int Least = 1;

    /// <summary>The point's hexes: the centre, then its neighbours with a ring.</summary>
    public IEnumerable<Vector2I> Cells()
    {
        yield return Cell;
        if (!Ring)
            yield break;
        foreach (int h in HexField.EdgeHeadings)
            yield return HexField.Step(Cell, h);
    }

    /// <summary>Whether <paramref name="cell"/> is one of the point's hexes.</summary>
    public bool Holds(Vector2I cell)
    {
        if (cell == Cell)
            return true;
        if (Ring)
            foreach (int h in HexField.EdgeHeadings)
                if (HexField.Step(Cell, h) == cell)
                    return true;
        return false;
    }

    public override string ToString() =>
        $"({Cell.X},{Cell.Y}){(Ring ? " with its ring" : "")}, {Score} points a side";
}

/// <summary>
/// What a map says of a match beyond its homes (GDD match.md): each side's
/// deployment zone (<see cref="Zone"/>, «Расстановка и первый ход») and the
/// control point (<see cref="Point"/>, «Условия победы»). Format 3's
/// <c>zones</c> and <c>point</c> (docs/battle-core-plan.md, Д9).
///
/// <b>Silence is a rule, not a gap.</b> A side with no zone deploys on its
/// homes and nowhere else - the match as it was before zones; a map with no
/// point is played to the last tank (match.md, «Уничтожение»).
///
/// <b>Immutable, and compared by <see cref="Say"/>.</b> The editor keeps one
/// in its undo stack, and a stroke that changed nothing is told by the text.
/// </summary>
public sealed class Arena
{
    public static readonly Arena Empty = new(null, null, null);

    private readonly Vector2I[] _green, _brown;

    public ControlPoint? Point { get; }

    public Arena(IEnumerable<Vector2I>? green, IEnumerable<Vector2I>? brown, ControlPoint? point)
    {
        _green = Ordered(green);
        _brown = Ordered(brown);
        Point = point;
    }

    /// <summary>Reading order, each once: the file writes it so and the
    /// comparison needs it so.</summary>
    private static Vector2I[] Ordered(IEnumerable<Vector2I>? cells) =>
        cells is null ? Array.Empty<Vector2I>()
            : cells.Distinct().OrderBy(c => c.Y).ThenBy(c => c.X).ToArray();

    /// <summary>A side's deployment zone, by <see cref="Parking.Sides"/>' name;
    /// empty when the map gave it none.</summary>
    public IReadOnlyList<Vector2I> Zone(string side) =>
        string.Equals(side, Parking.Brown, StringComparison.OrdinalIgnoreCase) ? _brown : _green;

    /// <summary>The side whose zone holds <paramref name="cell"/>, or null.</summary>
    public string? ZoneAt(Vector2I cell) =>
        Array.IndexOf(_green, cell) >= 0 ? Parking.Green
        : Array.IndexOf(_brown, cell) >= 0 ? Parking.Brown
        : null;

    /// <summary>Nothing said: no zone, no point.</summary>
    public bool Plain => _green.Length == 0 && _brown.Length == 0 && Point is null;

    public Arena WithZone(string side, IEnumerable<Vector2I> cells) =>
        string.Equals(side, Parking.Brown, StringComparison.OrdinalIgnoreCase)
            ? new Arena(_green, cells, Point)
            : new Arena(cells, _brown, Point);

    public Arena WithPoint(ControlPoint? point) => new(_green, _brown, point);

    /// <summary>The same ground on a board shifted by <paramref name="shift"/>
    /// (<see cref="BoardMap.Moated"/>).</summary>
    public Arena Moved(Vector2I shift) =>
        Plain ? this
            : new Arena(_green.Select(c => c + shift), _brown.Select(c => c + shift),
                        Point is { } p ? p with { Cell = p.Cell + shift } : null);

    /// <summary>Every cell it names, for a guard that asks whether each is on
    /// the board.</summary>
    public IEnumerable<(Vector2I Cell, string What)> Named()
    {
        foreach (Vector2I c in _green)
            yield return (c, "the green zone");
        foreach (Vector2I c in _brown)
            yield return (c, "the brown zone");
        if (Point is { } p)
            foreach (Vector2I c in p.Cells())
                yield return (c, "the control point");
    }

    /// <summary>The whole of it as one line: the comparison, the log.</summary>
    public string Say() =>
        $"green [{string.Join(" ", _green.Select(c => $"{c.X},{c.Y}"))}]"
        + $" brown [{string.Join(" ", _brown.Select(c => $"{c.X},{c.Y}"))}]"
        + $" point {(Point is { } p ? p.ToString() : "none")}";

    public override string ToString() => Say();
}
