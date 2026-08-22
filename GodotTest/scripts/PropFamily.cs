using System;
using System.Collections.Generic;

namespace TankSpriteTest;

/// <summary>
/// What a standing thing is made of, and therefore the two things a board can
/// see it do: bend in the wind, and catch fire.
///
/// <b>A family is not a tier.</b> A tier is which rules - spacing, clearing,
/// how much of the sprite behaves as height. A family is which two bits, and it
/// answers them for everything filed under it, including art nobody has written
/// a tier row for. That is the whole reason it exists: <see cref="PropTier"/>'s
/// old <c>Unknown</c> had to <i>guess</i> whether a new folder swayed and
/// burned, and its cautious middle - both true - is exactly backwards for a
/// stone. Under <c>Solid/</c> a folder nobody has described gets the right
/// answer to both for free, because of where it was put.
///
/// <b>A tier cannot contradict its family.</b> Let it, and the family stops
/// being a fact and becomes a suggestion, and then two places answer "does this
/// burn" and they drift apart on the row nobody re-read. So the flags live here
/// and only here; <see cref="PropTier"/> does not carry them.
///
/// <b>Which is why the day something burns without swaying - a fence, a
/// haystack, a stack of crates - it is a third family and not an exception
/// inside one of these two.</b> The pair is a 2x2 with two corners occupied; the
/// families are named for their material and the bits follow from it, so the
/// missing corner has to be named rather than derived. Naming it is one line.
///
/// <b>What does not live here: <see cref="PropTier.Carries"/>.</b> A tree passes
/// the fire across a hexagon and a bush does not, and both are vegetation. That
/// is the evidence there are really two levels rather than one - were carrying
/// derivable from the family, the tier would be a folder and nothing more.
/// </summary>
/// <param name="Name">The first path element under the props root, lowercased.
/// </param>
/// <param name="Sways">Whether the wind, the blast and a passing hull move it.
/// A rock that leaned in a gust would be the one thing on the board announcing
/// that all of this is a shear on a sprite.</param>
/// <param name="Burns">Whether it takes the fire's coat at all - soot, flame,
/// and the swap to its burnt picture. A boulder charring is the same
/// announcement from the other end.</param>
public sealed record PropFamily(string Name, bool Sways, bool Burns)
{
    public const string Vegetation = "vegetation";
    public const string Solid = "solid";

    /// <summary>
    /// The families whose rules are written down, by folder name.
    ///
    /// Two rows and two booleans each, and that is deliberately all there is:
    /// everything a family could plausibly also answer - how tall, how dense,
    /// how far from the seam - differs between the tiers inside it. A wall is
    /// tall and a stone is low, and both are solid.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, PropFamily> Known =
        new Dictionary<string, PropFamily>(StringComparer.OrdinalIgnoreCase)
        {
            [Vegetation] = new(Vegetation, Sways: true, Burns: true),
            [Solid] = new(Solid, Sways: false, Burns: false),
        };

    /// <summary>
    /// The family a folder name names, or null when nobody has written its row.
    ///
    /// <b>Null is refused by the caller, and that is the opposite of what an
    /// unknown tier gets.</b> The two are missing different things. A tier
    /// without a row is missing numbers - <see cref="PropTier.Unknown"/> picks
    /// middling ones, the art loads, and <see cref="PropTier.Declared"/> plus
    /// the ring the sowing comes out as both say so. A family without a row is
    /// missing behaviour, and there is nothing to guess it from: the folder is
    /// the declaration. Two bits are one line to write, which is cheaper than
    /// any wrong guess about them.
    /// </summary>
    public static PropFamily? For(string name) =>
        Known.TryGetValue(name, out PropFamily? family) ? family : null;
}
