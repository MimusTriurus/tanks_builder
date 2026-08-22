using System;
using System.Collections.Generic;
using System.Linq;

namespace TankSpriteTest;

/// <summary>
/// What kind of standing thing a prop is: a tree, a bush, a rock.
///
/// <b>A tier is not a species.</b> A species is which picture; a tier is which
/// rules. Four trees differ only in what they look like, so the sower picks
/// among them freely - see <see cref="Grove"/>'s spot-then-species order. A
/// rock differs in what it <i>does</i>: it does not bend in the wind, it does
/// not fade when a tank drives onto its cell, and it wants a lattice a third of
/// the spacing. None of that is expressible as a fifth tree.
///
/// <b>Read off the path, not declared in a list, and the path is exactly two
/// deep.</b> <c>Images/Environment/Solid/Rock/*.png</c> is the rock tier of the
/// solid family; the first element is a <see cref="PropFamily"/> and the second
/// is the tier, and the pair is the key. A tier nobody drew art for costs
/// nothing and is never mentioned - the counts in <see cref="Grove.Note"/> come
/// off what loaded.
///
/// <b>Two and no deeper, because there is nothing a third level could be.</b>
/// Below a tier is either a species, which is a file, or a state, which this
/// project settled long ago as a word in the filename rather than a folder -
/// <c>Tree_1_burnt.png</c>. And art loose in a family's root is not a tier at
/// all and is not scanned: the family root holds folders, so the source
/// documents can live there without becoming props.
///
/// <b>The two levels are siblings in the sowing, not a hierarchy.</b> Sowing
/// order is measured height across every tier of every family, flat - the
/// family takes no part in it. That is the sign it is a behaviour table and not
/// a structure.
///
/// <b>And an unknown subdirectory is named rather than refused or absorbed.</b>
/// Absorbing it into the trees would put boulders in the canopy; refusing it
/// silently is a folder of art that does not appear. It gets
/// <see cref="Unknown"/>'s numbers and says so in the note. An unknown
/// <i>family</i> is refused instead, and <see cref="PropFamily.For"/> carries
/// why the two differ.
///
/// <b>Grass is missing from the defaults on purpose.</b> It is paint, not props:
/// it sits under the tank and never in front of it, so the clearance rule has
/// one answer for it, and a node per tuft buys nothing - the count is hundreds
/// per cell, and each prop is a <c>Node2D</c> plus two <c>MeshInstance3D</c> in
/// <see cref="Stage3D"/>. Its place is a variant plate that
/// <see cref="TerrainSet.MixedAt"/> already chooses between. The tier is
/// nonetheless defined, because the argument is about grass and not about the
/// machinery, and somebody who disagrees should be able to drop a folder in and
/// look rather than edit this file.
/// </summary>
/// <param name="Name">The path, lowercased, as <c>family/tier</c>. What the
/// budget table and the note call it, and a key that cannot collide the way a
/// bare folder name could: <c>Vegetation/Bush</c> and a future
/// <c>Solid/Bush</c> are one word apart and would have overwritten each other
/// in silence.</param>
/// <param name="Family">What it is made of, and therefore whether it sways and
/// whether it burns. Those two live there and not here - see
/// <see cref="PropFamily"/> for why a tier is not allowed to contradict
/// them.</param>
/// <param name="Detail">How many times the on-screen scale this folder's art is
/// drawn at. The one number about a prop that cannot be measured - it has no
/// template frame to compare against, the way a terrain kind does - so it is
/// declared, and declared per folder because a folder is where the rules of a
/// tier already live. Per file it would be a table of filenames, which is what
/// everything else here exists to avoid.
///
/// <b>Everything that follows from it is measured, so this is the whole
/// adjustment a re-export needs.</b> Rise, foot, contact and footprint come off
/// the pixels; step, clearing and keep-out are in ground px. Aim the drawn
/// height at 110-140 screen px against a 109px hexagon and a 131px tank, and
/// keep the widest base under about 18 - the legal ring for a trunk is some 19
/// deep between the keep-out at 88 and the near edge at 107, and a prop too wide
/// for every spot on the board goes missing rather than wrong.
///
/// Eight for the trees, which are exported at that scale from one document at
/// 1009-1041px tall: 125-130px on screen, bases 9.2 to 16.8. Four for
/// everything drawn beside <c>soil.png</c>, which is a 4x kind.</param>
/// <param name="Step">Ground px between lattice nodes before jitter. Its own,
/// because a tier is a spacing before it is anything else: 30 for trees is the
/// measured width of the band a trunk may stand in, and undergrowth that
/// honoured it would come out as four bushes on a cell.</param>
/// <param name="Clearing">How much of <see cref="Grove.KeepOut"/> - the swept
/// contact patch - this tier keeps free, as a share of it. One for a tree,
/// because the clearing is what makes a wooded cell drivable and the ring of
/// trunks round it is what a forest hex looks like. Zero for anything a tank
/// drives over or through, and zero is what spreads a tier across its hexagon:
/// at one, the legal band is the 24 ground px between the keep-out at 83 and
/// the near edge at 107, so every prop in the tier stands on the rim by
/// construction.</param>
/// <param name="MindsBorder">Whether the base must stand clear of the cell's
/// edge. True for anything with a footprint worth speaking of; false lets low
/// scatter cross a seam, which hides the grid rather than drawing it.</param>
/// <param name="Inset">Ground kept clear inside the border on top of the prop's
/// own measured base, as a share of the cell's inradius. A share rather than a
/// distance so it survives a different tile size, and on top of the base rather
/// than instead of it because the base is what must not touch the seam and this
/// is how far from it the tier should sit back. Zero for a tree: a wood that
/// stops short of its own edge reads as a hedge round a lawn, and the edge fade
/// is what softens that boundary instead.</param>
/// <param name="UnderTanks">Whether this tier gives up depth sorting and draws
/// under everything that stands on the ground.
///
/// It follows from <paramref name="Clearing"/> and is written out anyway. A tier
/// that keeps the tank's patch free never stands where a tank stands, so sorting
/// the two by their feet always has an answer; a tier that keeps none of it
/// stands in the middle of the cell the tank parks in, and there the answer is a
/// coin toss that changes with the hull's heading - half the scatter on the cell
/// drawn over the tank and half under it, reshuffling as it turns.
///
/// So the tier stops being something that stands and becomes ground dressing:
/// under every tank and every tree, over the ruts, the ring and the shadows.
/// What that costs is the true half of the old behaviour - a bush on the cell in
/// front of a tank no longer laps over its tracks, and a bush in front of a
/// trunk is cut by it. On art this low (bushes 29-37px, stones 10-22px against a
/// 131px tank) that is a few dozen pixels behind a narrow trunk, against a tank
/// with shrubbery growing out of its hull.</param>
/// <param name="Stands">What share of the sprite's drawn vertical extent
/// behaves as height when the sun is laid through it.
///
/// <b>The one number here that cannot be measured, and the reason is written
/// into <see cref="PropSet"/> already:</b> a sprite's vertical axis mixes height
/// above the ground with depth into it, and nothing in a flat image separates
/// the two. <see cref="PropSet"/> answers by refusing to guess - its contact
/// extent is horizontal only. The shadow cannot refuse: it has to throw
/// something, and throwing all of it treats a boulder lying on the ground as a
/// boulder standing on end.
///
/// Pick it as <c>H*cos(e) / (H*cos(e) + D*sin(e))</c> for a thing of true height
/// H and footprint depth D. A tree is nearly all H; a bush is about as deep as
/// it is tall; a stone is wider than it is high, and most of what is drawn above
/// its near edge is its own far edge lying on the same ground.
///
/// <b>What the remaining share is treated as is nothing - not depth.</b>
/// Modelling it as depth is more nearly true and comes out worse: a boulder's
/// far edge would throw its shadow further from the camera, which on this
/// projection is <i>up</i> the screen and therefore behind the boulder, so a
/// stone would lose its shadow entirely while its own sprite still stands bolt
/// upright. The billboard has no depth; a shadow that has depth on its own is
/// two models of the same object.</param>
/// <param name="Fades">Whether it ghosts while a tank is on its cell. A tree
/// hides a tank and a kerbstone does not, and fading what cannot hide anything
/// reads as the ground going translucent.</param>
/// <param name="Carries">Whether having one of these on a cell is what makes the
/// cell able to catch and to pass the fire on.
///
/// <b>Two flags and not one, and the pair is the rule rather than caution.</b>
/// Scrub burns - it is dry brush - but a bush is not a way across a hexagon: made
/// one, the fire walks the scatter that is on every cell of a mixed board and the
/// whole map goes up, which is a grass fire wearing a wood's name. So the wood
/// carries the front and the undergrowth burns where the front already is.
/// </param>
/// <param name="Salt">Base for every hash this tier takes, so no two tiers share
/// a decision. Sharing them puts a bush at the foot of every trunk - the same
/// failure the per-decision salts inside a tier already avoid, one level up.
/// Zero for trees, which is what pins the existing boards.</param>
public sealed record PropTier(
    string Name,
    double Detail,
    double Step,
    double Clearing,
    bool MindsBorder,
    double Inset,
    bool UnderTanks,
    double Stands,
    bool Fades,
    bool Carries,
    int Salt,
    PropFamily Family)
{
    /// <summary>The tier a PNG in the root of the props folder belongs to. Not a
    /// default so much as the arrangement that was there before tiers existed:
    /// every board rendered so far is a board of these.</summary>
    public const string Trees = PropFamily.Vegetation + "/tree";

    public const string Bushes = PropFamily.Vegetation + "/bush";
    public const string Rocks = PropFamily.Solid + "/rock";
    public const string Grass = PropFamily.Vegetation + "/grass";

    // Resolved once and named here so the rows below read as a table. Declared
    // above Known on purpose: static initialisers run in textual order, and a
    // row asking for a family that has not been resolved yet gets null.
    private static readonly PropFamily Vegetation =
        PropFamily.Known[PropFamily.Vegetation];

    private static readonly PropFamily Solid = PropFamily.Known[PropFamily.Solid];

    /// <summary>
    /// The tiers whose rules are written down, by <c>family/tier</c> path.
    ///
    /// Written with named arguments rather than a row of bare literals: there
    /// are eleven of them and four are booleans, so a positional row is a place
    /// where two flags swap without a compiler ever noticing.
    ///
    /// Steps are the one number here that is a guess rather than a measurement,
    /// and they are guesses in a direction that is safe: a step too coarse comes
    /// out as a cell with two bushes on it, which the budget's floor then goes
    /// over again at half - see <see cref="Grove"/>. A step too fine costs
    /// candidates that get thrown away.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, PropTier> Known =
        new Dictionary<string, PropTier>(StringComparer.OrdinalIgnoreCase)
        {
            // Salt 0, and it has to be: every existing board is this tier's
            // sowing, and any other value re-rolls all of them. The full
            // clearing and no inset for the same reason - this row is the
            // arrangement that was there before tiers, written out. Standing
            // whole for the same reason and one more: a trunk's flare is a few
            // px of depth in 139 of drawn height, so the share a measurement
            // would give is within rounding of the one that leaves every board
            // rendered so far byte for byte where it was.
            [Trees] = new(Trees, Detail: 8.0, Step: 30.0, Clearing: 1.0,
                          MindsBorder: true, Inset: 0.0, UnderTanks: false,
                          Stands: 1.00, Fades: true, Carries: true, Salt: 0,
                          Family: Vegetation),

            // Waist-high: it hides a tank's belt and not its turret, so it sorts
            // and it fades. No clearing, because a hull pushes through scrub
            // rather than parking around it - and because a tier that honours
            // the clearing cannot be spread over its cell, it can only ring it.
            // Eighteen across the whole hexagon rather than round its rim.
            // Two thirds of it stands: a bush is about as deep as it is tall,
            // so H*cos(e) is 0.63 of what is drawn.
            // Vegetation, so it sways and it burns - and it still does not
            // carry, which is the one thing about a bush the family cannot say.
            [Bushes] = new(Bushes, Detail: PropSet.Detail, Step: 18.0,
                           Clearing: 0.0, MindsBorder: true, Inset: 0.12,
                           UnderTanks: true, Stands: 0.65, Fades: true,
                           Carries: false, Salt: 100_003, Family: Vegetation),

            // Neither sways nor burns, and that is now the family saying so
            // rather than this row - the whole difference between a rock and a
            // bush the board can see, said once for everything solid. No
            // clearing, and that is a trade rather than a free choice: a tank
            // can now park over rubble, which on art this low reads as rubble at
            // its feet, and the alternative is every rock on the board standing
            // in a ring. And the least of any tier that stands at all - a stone
            // is roughly twice as wide as it is high, and the far half of it is
            // lying on the same ground as the near.
            [Rocks] = new(Rocks, Detail: PropSet.Detail, Step: 22.0,
                          Clearing: 0.0, MindsBorder: true, Inset: 0.12,
                          UnderTanks: true, Stands: 0.45, Fades: false,
                          Carries: false, Salt: 200_003, Family: Solid),

            // The one tier that lets itself cross a seam, because that is what a
            // tuft does. See the class remarks for why there is no art here and
            // should not be.
            [Grass] = new(Grass, Detail: PropSet.Detail, Step: 10.0,
                          Clearing: 0.0, MindsBorder: false, Inset: 0.0,
                          UnderTanks: true, Stands: 0.35, Fades: false,
                          Carries: false, Salt: 300_003, Family: Vegetation),

            // And there is no wall row, though Solid/Wall is where wall art
            // goes. Grass has a row without art in order to make an argument
            // about grass; a wall row would be eleven numbers nobody has seen a
            // picture for, and Declared would then claim somebody chose them.
            // Undeclared, it arrives as a ring round each cell and says so in
            // the note, which is the prompt to write the row - and it arrives
            // already not swaying and not burning, because of the folder it is
            // in. That is the second level earning its keep.
        };

    /// <summary>
    /// What a folder nobody wrote rules for gets.
    ///
    /// Middling and cautious: it clears the tank and minds its border, because
    /// those two are what keep a cell drivable and a prop one cell's prop, and
    /// a tier that quietly opted out of them would break the board rather than
    /// look wrong on it. It will therefore come out as a ring round each cell,
    /// which is the visible sign that nobody has written its row yet.
    ///
    /// <b>It no longer guesses whether the thing sways or burns, and that is
    /// what the family bought.</b> Both used to be true here, as the cautious
    /// middle - and both are exactly wrong for a stone, so the caution was a
    /// coin toss dressed as prudence. Now the folder it was dropped into
    /// answers those two and only the numbers are guesses.
    ///
    /// <b>Carrying stays false, which is the safe direction.</b> A tier nobody
    /// described does not get to spread a fire across the board.
    /// </summary>
    public static PropTier Unknown(PropFamily family, string name, int ordinal) =>
        new(name, PropSet.Detail, 20.0, 1.0, true, 0.10, false, 0.85, true,
            false, 400_003 + ordinal * 7_919, family);

    public static PropTier For(PropFamily family, string name, int ordinal) =>
        Known.TryGetValue(name, out PropTier? tier)
            ? tier
            : Unknown(family, name, ordinal);

    /// <summary>Whether this tier's rules were written down or inferred. The
    /// note says so: a folder running on <see cref="Unknown"/> is art that
    /// appeared without anybody deciding how it behaves.</summary>
    public bool Declared => Known.ContainsKey(Name);
}

/// <summary>How many of one tier a cell of one kind of ground carries. Both
/// ends, because a floor and a ceiling answer different questions: the floor
/// says a wooded cell is wooded whatever the edge fade rolls, and the ceiling
/// says a cell is not a thicket.</summary>
/// <param name="Tier">The folder name.</param>
/// <param name="Least">Planted whatever the fade prefers, so long as there are
/// legal spots to draw from.</param>
/// <param name="Most">0 for no ceiling.</param>
public sealed record Sprouts(string Tier, int Least, int Most);
