using System;
using System.Collections.Generic;

namespace TankSpriteTest;

/// <summary>
/// What a standing thing is made of, and therefore what a board can see it do:
/// bend in the wind, catch fire, and take the fire's mark.
///
/// <b>A family is not a tier.</b> A tier is which rules - spacing, clearing,
/// how much of the sprite behaves as height. A family is what the material
/// answers, and it answers it for everything filed under it, including art
/// nobody has written a tier row for. That is the whole reason it exists: <see cref="PropTier"/>'s
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
/// inside one of these two.</b> The pair of flags is a 2x2 with two corners
/// occupied; the families are named for their material and the bits follow from
/// it, so the missing corner has to be named rather than derived. Naming it is
/// one line.
///
/// <b>And <see cref="Soot"/> is why the two bits are not the whole of it.</b>
/// <see cref="Burns"/> was doing two jobs at once - whether the thing is fuel,
/// and whether a fire marks it - and the second is true of everything standing
/// in one. A stone does not burn and it does not come out of a burnt cell clean:
/// the ground under it is permanently black, so a bright boulder in the ash is
/// the same announcement from the other end that a charring boulder would be.
/// One number rather than a third flag, because how black a fire leaves a
/// surface is a matter of degree and it is a matter of what the surface is.
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
/// <param name="Burns">Whether it is fuel: whether it carries flame and smoke,
/// is used up, and hands over to its burnt picture. Not whether a fire marks it
/// - that is <paramref name="Soot"/>, and it is true of everything.</param>
/// <param name="Soot">How black a fire leaves this material, as a share of the
/// full char. One is charcoal: the luminance taken down to
/// <c>BarkShader</c>'s <c>coal</c> and the colour gone with it. Applied to fuel
/// and non-fuel alike, so a family at one behaves exactly as it did before this
/// number existed - which is what keeps every board rendered so far where it
/// was.</param>
public sealed record PropFamily(string Name, bool Sways, bool Burns, float Soot)
{
    public const string Vegetation = "vegetation";
    public const string Solid = "solid";

    /// <summary>
    /// The families whose rules are written down, by folder name.
    ///
    /// Two rows and three answers each, and that is deliberately all there is:
    /// everything a family could plausibly also answer - how tall, how dense,
    /// how far from the seam - differs between the tiers inside it. A wall is
    /// tall and a stone is low, and both are solid.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, PropFamily> Known =
        new Dictionary<string, PropFamily>(StringComparer.OrdinalIgnoreCase)
        {
            // Fuel, and it chars all the way: the whole number is what every
            // board burnt so far was rendered with, so anything short of it here
            // would re-time every fire ever measured on this bench.
            [Vegetation] = new(Vegetation, Sways: true, Burns: true, Soot: 1.0f),

            // Not fuel, and marked all the same. Judged against the ash it will
            // be standing in rather than against the charcoal beside it: a mix
            // of s toward coal leaves a surface at 1 - (1 - coal)*s of its own
            // luminance, so 0.55 lands stone at 0.615 of itself - about as dark
            // as the ground the same fire blackens, which is the criterion the
            // ash's own 0.55 was picked on.
            [Solid] = new(Solid, Sways: false, Burns: false, Soot: 0.55f),
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
