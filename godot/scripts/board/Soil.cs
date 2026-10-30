using System.Collections.Generic;

namespace TankSpriteTest;

/// <summary>
/// The look of the ground: format 3's <c>soil</c> block (docs/map-format3-plan.md,
/// Ф4). A letter a look, each for one floor - grass, bare earth or stones on
/// solid ground, rippled sand or dunes on sand, puddled mud or swamp on mud.
///
/// <b>Look only.</b> A dune is the sand it lies on and a swamp the mud: the
/// rules read the floor, and nothing here moves where a tank may drive or
/// what stops a round.
///
/// <b>Three layers, each silent when it says nothing.</b> A cell's own letter
/// (<c>cells</c>), else the board's look for its floor (<c>base</c>), else the
/// ground as it always was (<see cref="Today"/>).
/// </summary>
public static class Soil
{
    /// <summary>A cell that says nothing: the board's look for its floor.</summary>
    public const char As = '.';

    public const char Grass = 'g', Earth = 'e', Stony = 'k';
    public const char Ripple = 'r', Dunes = 'n';
    public const char Puddles = 'p', Swamp = 'b';

    /// <summary>The floor a look lies on, or null for a letter that is none.</summary>
    public static Foundation? FloorOf(char look) => look switch
    {
        Grass or Earth or Stony => Foundation.Solid,
        Ripple or Dunes => Foundation.Sand,
        Puddles or Swamp => Foundation.Mud,
        _ => null,
    };

    /// <summary>The look a floor always had - grass, rippled sand, puddled
    /// mud - and <see cref="As"/> for a floor with no looks.</summary>
    public static char Today(Foundation floor) => floor switch
    {
        Foundation.Solid => Grass,
        Foundation.Sand => Ripple,
        Foundation.Mud => Puddles,
        _ => As,
    };

    /// <summary>The floors that take a look, and the key <c>base</c> writes
    /// each under.</summary>
    public static readonly (Foundation Floor, string Key)[] Floors =
    {
        (Foundation.Solid, "solid"), (Foundation.Sand, "sand"), (Foundation.Mud, "mud"),
    };

    /// <summary>Every look's letter and name, in the order the editor lists
    /// them.</summary>
    public static readonly (char Letter, string Name)[] Looks =
    {
        (Grass, "grass"), (Earth, "bare earth"), (Stony, "stones"),
        (Ripple, "rippled sand"), (Dunes, "dunes"),
        (Puddles, "puddled mud"), (Swamp, "swamp"),
    };

    public static string Name(char look)
    {
        foreach ((char l, string name) in Looks)
            if (l == look)
                return name;
        return "as the board";
    }

    /// <summary>
    /// The look a cell wears: its own, else the board's for its floor, else
    /// today's. Water and a rock wear none (<see cref="As"/>) - the water and
    /// the rock paint themselves.
    /// </summary>
    public static char Resolve(char own, Foundation floor, bool water,
                               IReadOnlyDictionary<Foundation, char> laid)
    {
        if (water || Today(floor) == As)
            return As;
        if (FloorOf(own) == floor)
            return own;
        return laid.TryGetValue(floor, out char look) ? look : Today(floor);
    }

    /// <summary>Whether a look is one the ground draws apart from today's -
    /// what the relief and the stage are handed.</summary>
    public static bool Drawn(char look) => look is Earth or Stony or Dunes or Swamp;
}
