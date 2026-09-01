using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// What a map says about the masonry on one cell: which of its six edges carry
/// wall, and the four numbers that are the wall's shape.
///
/// <b>The edges are the truth and the run is derived, not the other way
/// round.</b> <see cref="HexField.SidesAt"/> is a mask and every rule that asks
/// about a wall asks it per edge - what a shot crosses, what a tank drives
/// through - so the mask is what the board has to carry. <see cref="WallKit"/>
/// takes something narrower: a <i>bearing</i> and a count of sides, because a
/// wall is one folded chain of bricks rather than N walls placed side by side.
/// So a mask that is two runs is a cell nobody can build one wall on, and
/// <see cref="Run"/> is the conversion that says which masks are buildable.
///
/// <b>Nothing stands props from a map yet, and the even case is where that
/// shows.</b> <c>TankBench.Walls</c> builds its walls off its own list of
/// recipes; the day a board's own masonry is laid instead, the convention below
/// - odd counts centre on an edge, even counts centre on a corner - is what will
/// be held against the bricks. It is self-consistent (<see cref="Fan"/> and
/// <see cref="Run"/> invert each other, and the self test says so) and it is
/// untested against a standing wall. Named so that a wall that comes up rotated
/// sixty degrees is one sentence away from being explained rather than a day.
///
/// <b>The four numbers are nullable, and null is silence rather than a
/// default.</b> <see cref="WallConfig"/> holds the same rule over
/// <c>walls.json</c> and holds it for the reason a shared recipe exists at all:
/// a cell that says nothing about its courses is a cell that takes whatever the
/// wall being laid uses, and writing today's default into the file would freeze
/// it there the first time anybody saved a map.
/// </summary>
public sealed class Masonry
{
    /// <summary>Every side, which is what a cell carrying the wall letter has
    /// until somebody says otherwise - see <see cref="HexField.SetCover"/>,
    /// which lays the same ring for the same reason.</summary>
    public const int Ring = 0b111111;

    public int Edges = Ring;

    public int? Seed;
    public int? Columns;
    public int? Courses;
    public int? Leaves;

    /// <summary>The ranges a dial may reach, the panel sliders' own - see
    /// <see cref="WallConfig.Apply"/>, which refuses the same figures out of a
    /// file. Here so that the editor, the file and the bench cannot drift.
    /// </summary>
    public static (int Low, int High) Range(Dial dial) => dial switch
    {
        Dial.Columns => (2, 16),
        Dial.Courses => (1, WallBench.MostCourses),
        Dial.Leaves => (1, WallBench.MostLeaves),
        // A seed is a name for a wall rather than a size of one, so every
        // number is one. Kept in the table anyway: a caller asking the range of
        // a dial must not have to know which dial it is.
        _ => (int.MinValue, int.MaxValue),
    };

    public enum Dial
    {
        Seed,
        Columns,
        Courses,
        Leaves,
    }

    /// <summary>The four, in the order the file writes them and the panel shows
    /// them. One list, because three readers walking the same four dials in
    /// three hand-written orders is three places to forget the fourth.
    /// </summary>
    public static readonly Dial[] Dials =
    {
        Dial.Seed, Dial.Columns, Dial.Courses, Dial.Leaves,
    };

    public int? Of(Dial dial) => dial switch
    {
        Dial.Seed => Seed,
        Dial.Columns => Columns,
        Dial.Courses => Courses,
        _ => Leaves,
    };

    public void Set(Dial dial, int? value)
    {
        switch (dial)
        {
            case Dial.Seed: Seed = value; break;
            case Dial.Columns: Columns = value; break;
            case Dial.Courses: Courses = value; break;
            default: Leaves = value; break;
        }
    }

    public Masonry Copy() => new()
    {
        Edges = Edges, Seed = Seed, Columns = Columns, Courses = Courses,
        Leaves = Leaves,
    };

    /// <summary>Whether this says anything a cell carrying the wall letter did
    /// not already say. A plain entry is not written to the file: it would be a
    /// line per walled cell on every board, saying the ring that the letter
    /// says.</summary>
    public bool Plain =>
        Edges == Ring && Seed is null && Columns is null && Courses is null
        && Leaves is null;

    // --- the six edges -------------------------------------------------------

    /// <summary>
    /// The headings of a cell's six edges, in the order the mask's bits run.
    ///
    /// <b>Not <see cref="HexField.EdgeHeadings"/>, and the difference is a bug
    /// waiting to be written.</b> That array is 30, 90 ... 330 ascending, and
    /// the bit a side lives in is <see cref="HexField.EdgeIndex"/>, which counts
    /// the other way from 330. Bit order is the one that matters here, because
    /// two neighbouring bits are two neighbouring edges - which is what makes a
    /// contiguous run a contiguous wall.
    /// </summary>
    public static readonly int[] Headings = { 330, 270, 210, 150, 90, 30 };

    public static int Bit(int heading) => HexField.EdgeIndex(heading);

    public bool Stands(int heading) => (Edges & (1 << Bit(heading))) != 0;

    /// <summary>The headings this cell carries wall on, in bit order.</summary>
    public IEnumerable<int> Standing() =>
        Headings.Where(Stands);

    // --- the run -------------------------------------------------------------

    /// <summary>
    /// The mask a wall of <paramref name="sides"/> sides centred on
    /// <paramref name="bearing"/> covers.
    ///
    /// Odd counts put an edge square to the bearing and even counts put a corner
    /// there - <see cref="WallKit.Fit"/>'s own words, and the reason the fan is
    /// centred at all is that the turntable then shows the same wall from either
    /// hand. So an odd count wants a bearing on an edge (30, 90 ... 330) and an
    /// even one wants a bearing on a corner (0, 60 ... 300); a bearing on the
    /// wrong one of those is rounded to the nearer legal one rather than
    /// refused, because the caller that has both is the panel and it has an edge
    /// in its hand either way.
    /// </summary>
    public static int Fan(int bearing, int sides)
    {
        sides = Math.Clamp(sides, 1, 6);
        if (sides == 6)
            return Ring;
        int at = ((bearing % 360) + 360) % 360;
        if (sides % 2 == 1)
        {
            // Centred on an edge: the bearing names it.
            int centre = Bit(at);
            int half = (sides - 1) / 2;
            int mask = 0;
            for (int i = -half; i <= half; i++)
                mask |= 1 << (((centre + i) % 6 + 6) % 6);
            return mask;
        }
        // Centred on a corner. The corner between bit i and bit i+1 faces
        // 300 - 60i, which is the two edges' headings averaged.
        int corner = ((300 - at) / 60 % 6 + 6) % 6;
        // A bearing that named an edge instead lands between two corners; it is
        // taken as the corner clockwise of it, which is what the integer divide
        // above already did.
        int left = sides / 2 - 1;
        int made = 0;
        for (int i = -left; i <= sides / 2; i++)
            made |= 1 << (((corner + i) % 6 + 6) % 6);
        return made;
    }

    /// <summary>
    /// The wall this mask is, or null when it is not one wall.
    ///
    /// Null means two things and the caller cares about both: no edges at all -
    /// which is a breach rather than a wall - and edges in two runs, which is
    /// two walls on one cell and something <see cref="WallKit"/> cannot lay. The
    /// editor reports it; it does not refuse the stroke, because a run being
    /// drawn passes through being two runs.
    /// </summary>
    public static (int Bearing, int Sides)? Run(int edges)
    {
        int mask = edges & Ring;
        if (mask == 0)
            return null;
        if (mask == Ring)
            // Any bearing closes the same ring; the front is the one every
            // other bearing on this project defaults to.
            return (270, 6);

        int count = 0, starts = 0, start = 0;
        for (int i = 0; i < 6; i++)
        {
            bool here = (mask & (1 << i)) != 0;
            bool before = (mask & (1 << ((i + 5) % 6))) != 0;
            if (here)
                count++;
            if (here && !before)
            {
                starts++;
                start = i;
            }
        }
        if (starts != 1)
            return null;

        if (count % 2 == 1)
            return (Headings[(start + (count - 1) / 2) % 6], count);
        int corner = (start + count / 2 - 1) % 6;
        return ((300 - 60 * corner + 360) % 360, count);
    }

    public (int Bearing, int Sides)? Run() => Run(Edges);

    /// <summary>What the entry says, for the readout and the report.</summary>
    public string Say()
    {
        var parts = new List<string>();
        (int Bearing, int Sides)? run = Run();
        parts.Add(Edges == Ring ? "a closed ring"
            : run is (int bearing, int sides)
                ? $"{sides} side{(sides == 1 ? "" : "s")} facing {bearing}"
            : Edges == 0 ? "no sides standing"
            : "two runs, which is two walls on one cell");
        if (Seed is int s) parts.Add($"seed {s}");
        if (Columns is int c) parts.Add($"{c} columns");
        if (Courses is int r) parts.Add($"{r} courses");
        if (Leaves is int l) parts.Add($"{l} leaves");
        return string.Join(", ", parts);
    }
}
