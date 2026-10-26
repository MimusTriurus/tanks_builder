using System;
using System.Collections.Generic;

using Godot;

namespace TankSpriteTest;

/// <summary>
/// One parking a board declares: the cell, and optionally which class the board
/// wants standing on it.
///
/// <b>The class is a preference, not a roster.</b> Which tanks exist is the
/// harness's - one per atlas that loaded, see <c>Main.Park</c> - so a class named
/// here says where that one goes if it is on the board, and a parking that names
/// nobody takes whoever is left. A board naming a class no build has, or naming
/// one twice, is refused by <c>BoardMap.MustCrew</c>: both are statements the
/// board cannot honour.
///
/// <b>Null is silence, the rule <see cref="Masonry"/> holds over its four
/// numbers.</b> A parking that said nothing is not a parking that asked for the
/// first class; it takes what the pairing has left over, and the file writes it
/// as the bare pair it always was.
///
/// <b>Side and facing are a match's, and silent the same way.</b>
/// <see cref="Side"/> - <c>green</c> or <c>brown</c> - says which team stands
/// here; a parking without one takes the side its order gives it (the first
/// five green, the next brown, <c>Tank3DBench.Match</c>). <see cref="Facing"/>
/// is an edge heading (<see cref="HexField.EdgeHeadings"/>) the tank starts
/// facing; without one a green tank looks up the board, a brown one down it.
/// A class is asked for once per side, not once per board: each team has its
/// five.
///
/// <b>A home is two jobs and only one of them is the tank.</b> The other is that
/// <see cref="MapRules.ReachFaults"/> measures the board from every parking -
/// that is the check which catches a valley cut off by mistake - and
/// <see cref="MapFile.MustHaveHomes"/> refuses a board with none, because there
/// is nothing to measure from. So a parking is worth declaring even where no
/// tank will ever stand.
/// </summary>
public readonly record struct Parking(Vector2I Cell, string? Class = null,
                                     string? Side = null, int? Facing = null)
{
    public const string Green = "green";
    public const string Brown = "brown";

    /// <summary>The two sides a parking may name.</summary>
    public static readonly string[] Sides = { Green, Brown };

    /// <summary>A parking that names nobody, which is what every board wrote
    /// before classes existed. Implicit so that a board's list of cells stays a
    /// list of cells in the source.</summary>
    public static implicit operator Parking(Vector2I cell) => new(cell);

    /// <summary>Nothing but the cell said: the file writes the bare pair.</summary>
    public bool Plain => Class is null && Side is null && Facing is null;

    public override string ToString()
    {
        string said = $"({Cell.X},{Cell.Y})";
        if (Class is { Length: > 0 } tag)
            said += $" for {tag}";
        if (Side is { Length: > 0 } side)
            said += $" {side}";
        if (Facing is int facing)
            said += $" facing {facing}";
        return said;
    }

    /// <summary>How many a side fields in a match - LT, MT, HT, TD, HM.</summary>
    public const int SideSize = 5;

    /// <summary>
    /// The side each parking plays in a match: its own when it names one, else
    /// by order - the first <see cref="SideSize"/> unsided parkings green, the
    /// rest brown. A board that names no side splits as it always did. One
    /// rule for the editor's panel and the match (<c>Tank3DBench.Match</c>), so
    /// the panel says what the match does.
    /// </summary>
    public static string[] SidesOf(IReadOnlyList<Parking> parked)
    {
        var sides = new string[parked.Count];
        int unsided = 0;
        for (int i = 0; i < parked.Count; i++)
            sides[i] = parked[i].Side is { Length: > 0 } side
                ? side.ToLowerInvariant()
                : unsided++ < SideSize ? Green : Brown;
        return sides;
    }

    /// <summary>Two parkings on the same side: both unsided count as one side,
    /// the board's only one before sides existed.</summary>
    public bool SameSide(Parking other) =>
        string.Equals(Side, other.Side, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Which parking each tank claims: the slot per entry of
    /// <paramref name="loaded"/>, or -1 for a tank the board declared no
    /// parking for.
    ///
    /// <b>Named first, then in order, and the two passes are the whole rule.</b>
    /// A parking that asked for a class takes that tank wherever it is in the
    /// load order; one that asked for nobody takes whoever is left, in order. So
    /// a board that names nothing pairs exactly as it always did, and a board
    /// that names one moves that one without disturbing the rest.
    ///
    /// <b>Pure, so that it can be judged without a board.</b> Where an unpaired
    /// tank actually stands needs the ground - see <c>Main.Beside</c> - and that
    /// is the half this deliberately does not answer.
    /// </summary>
    public static int[] Pair(IReadOnlyList<Parking> parked,
                             IReadOnlyList<string> loaded)
    {
        var slots = new int[loaded.Count];
        var claimed = new bool[parked.Count];
        for (int i = 0; i < loaded.Count; i++)
        {
            slots[i] = -1;
            for (int s = 0; s < parked.Count; s++)
                if (!claimed[s] && string.Equals(parked[s].Class, loaded[i],
                        StringComparison.OrdinalIgnoreCase))
                {
                    claimed[s] = true;
                    slots[i] = s;
                    break;
                }
        }
        for (int i = 0; i < loaded.Count; i++)
        {
            if (slots[i] >= 0)
                continue;
            for (int s = 0; s < parked.Count; s++)
                if (!claimed[s] && parked[s].Class is null)
                {
                    claimed[s] = true;
                    slots[i] = s;
                    break;
                }
        }
        return slots;
    }
}
