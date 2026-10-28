using System.Collections.Generic;

namespace TankSpriteTest;

/// <summary>
/// Which trees a wood grows: format 3's <c>wood</c> and <c>trees</c> blocks
/// (docs/map-format3-plan.md, Ф5; pipeline/docs/tree-species-plan.md, Т4).
/// A letter a species, each with its models - <c>&lt;Name&gt;_00N</c> under
/// <c>assets/Models/Trees/</c> (<see cref="TreeModel.Dir"/>).
///
/// <b>Look only.</b> A pine wood is <see cref="Cover.Forest"/> as an oak wood
/// is: the rules read the cover, and a palm hides, slows and burns as an oak.
///
/// <b>Three layers, each silent when it says nothing.</b> A cell's own letter
/// (<c>trees</c>), else the board's mix (<c>wood</c>) by a hash of where the
/// tree stands, else oaks - the wood as it always was.
/// </summary>
public static class Species
{
    /// <summary>A cell that says nothing: the board's mix.</summary>
    public const char As = '.';

    /// <summary>Every species: its letter, the key <c>wood</c> writes it
    /// under, the models' name and how many seeds of it there are - in the
    /// order the editor lists them.</summary>
    public static readonly (char Letter, string Key, string Model, int Seeds)[] All =
    {
        ('o', "oak", "Oak", 6), ('p', "pine", "Pine", 3), ('e', "spruce", "Spruce", 3),
        ('b', "birch", "Birch", 3), ('y', "yucca", "Yucca", 3), ('l', "palm", "Palm", 3),
        ('a', "acacia", "Acacia", 3),
    };

    /// <summary>The oak: what a wood grows when the board says nothing.</summary>
    public const char Oak = 'o';

    /// <summary>A species' index in <see cref="All"/> by its letter or key, -1
    /// for neither.</summary>
    public static int Of(char letter) => System.Array.FindIndex(All, s => s.Letter == letter);
    public static int Of(string key) => System.Array.FindIndex(All, s => s.Key == key);

    public static string Name(char letter) => Of(letter) is var k and >= 0 ? All[k].Key : "as the board";

    /// <summary>Every key, for a refusal to list.</summary>
    public static IEnumerable<string> Keys
    {
        get
        {
            foreach (var s in All)
                yield return s.Key;
        }
    }

    /// <summary>One model of a species, its seed by <paramref name="salt"/>.</summary>
    public static string Model(char letter, int salt)
    {
        var s = All[Of(letter) is var k and >= 0 ? k : 0];
        return $"{s.Model}_{1 + (int)((uint)salt % (uint)s.Seeds):000}";
    }

    /// <summary>The species a tree of a cell grows: the cell's own, else
    /// drawn from the mix by <paramref name="salt"/> (a share its weight),
    /// else oak.</summary>
    public static char Pick(char own, IReadOnlyDictionary<char, int>? mix, int salt)
    {
        if (Of(own) >= 0)
            return own;
        if (mix is null || mix.Count == 0)
            return Oak;
        int total = 0;
        foreach (var s in All)
            if (mix.TryGetValue(s.Letter, out int w))
                total += w;
        if (total <= 0)
            return Oak;
        int roll = (int)(Hash((uint)salt) % (uint)total);
        foreach (var s in All)
            if (mix.TryGetValue(s.Letter, out int w) && (roll -= w) < 0)
                return s.Letter;
        return Oak;
    }

    /// <summary>A wood's tree on one cell: species by <see cref="Pick"/>, seed
    /// by the same salt - the oaks of a board with no blocks are the oaks it
    /// always grew (<c>Oaks[salt % 6]</c>).</summary>
    public static string Tree(char own, IReadOnlyDictionary<char, int>? mix, int salt) =>
        Model(Pick(own, mix, salt), salt);

    private static uint Hash(uint x)
    {
        x ^= x >> 16;
        x *= 0x7feb352d;
        x ^= x >> 15;
        x *= 0x846ca68b;
        x ^= x >> 16;
        return x;
    }
}
