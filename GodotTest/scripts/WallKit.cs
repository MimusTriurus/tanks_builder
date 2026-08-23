using System;
using System.Collections.Generic;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// A brick wall ruin, laid out from a seed, and the collapse that takes it down.
///
/// The Blender prop this replaces (<c>brick_wall.py</c>) is already procedural -
/// mesh, chips, jitter, rubble and materials all come off one seed. What was
/// hand-authored there is fourteen lines: seven courses of a profile, four fallen
/// bricks and three boulders. This is those fourteen lines turned into a rule, so
/// that a wall is a seed rather than a file.
///
/// <b>Nothing here is a length in world units.</b> Every number is a fraction of
/// the cell's circumradius, which is what <c>HEX_TILES.md</c> asks of anything
/// that stands on a cell, and what makes <see cref="Fit"/> exact: the layout does
/// not change with scale, so cutting the wall to its cell is one multiply and not
/// a rebuild. The Blender version could not say that - one absolute length
/// survived in it, the depth of a corner chip, and it made a smaller wall a
/// *different* wall: a different vertex count in the cut, a different number of
/// draws from the generator, and 38 bricks where there had been 37.
///
/// <b>It stands on the sides of the cell and is as long as they are.</b> A wall is
/// a boundary, so the cell decides its length rather than merely bounding it - the
/// run is the hexagon's side and the outer face is its apothem, both out of the
/// one <c>coverage</c>, and what the recipe still decides is how many pieces that
/// length is cut into. <see cref="Recipe.Sides"/> says how many sides it runs
/// along: the courses are always laid as one straight chain in arc length and
/// <see cref="Fit"/> folds them onto the boundary, so a three-sided wall is one
/// wall that turns two corners rather than three walls standing near each other.
/// Three passes in order, and the order is the point:
/// <see cref="Lay"/> builds the masonry about the origin, <see cref="Fit"/> cuts
/// and seats it, <see cref="Scatter"/> drops what fell off into the room that is
/// left. The apron cannot come first, because until the wall is seated there is no
/// answer to which side of it is still on the board.
///
/// <b>Y is up and the space is the board's flat plane, not the world.</b>
/// <see cref="Stage3D.World"/> maps a flat board point and a lift into the
/// stretched space the tilted orthographic camera undoes, so a wall built in
/// world units would have to be built pre-distorted. Instead the whole stack is
/// laid out square in (x, lift, y) and the distortion goes on the node - see
/// <see cref="WallStack"/>. That keeps rotations honest, which matters twice:
/// the overlap guard is a box test, and the collapse is ballistics.
///
/// <b>The collapse picks where each brick lands before it works out how it flies
/// there</b>, which is the one real advantage this has over the simulated
/// version. Over there the wall was knocked down and then fenced in, and the
/// fence did visible work - eleven of thirty-eight bricks would otherwise have
/// finished on a neighbouring cell. Here the destination is drawn inside the
/// hexagon and the flight is solved to reach it, so staying on the cell is not a
/// constraint that has to be enforced, it is a property of the construction.
/// </summary>
public static class WallKit
{
    // --- the brick, in fractions of the cell's circumradius --------------------

    /// <summary>Length, depth and height of one brick. Carried over from the
    /// Blender prop at its shipped scale, so the two look like the same wall:
    /// 0.26 x 0.13 x 0.12 of a cell before its 0.82275 fit.</summary>
    public const float BrickLong = 0.214f;
    public const float BrickDeep = 0.107f;
    public const float BrickTall = 0.099f;

    /// <summary>The cell's two lengths, and they are the wall's brief.
    ///
    /// A hexagon's side equals its circumradius, so <see cref="Edge"/> is 1 -
    /// the same number the whole file is quoted in - and <see cref="Apothem"/>
    /// is how far out that side sits. A wall standing on a boundary is
    /// therefore not fitted to the cell, it is <i>cut</i> to it: the run is the
    /// side, the seat is the apothem, and <see cref="Recipe.Columns"/> stops
    /// deciding how long a wall is and only says how many pieces that length is
    /// cut into.</summary>
    public const float Edge = 1.0f;
    public static readonly float Apothem = Mathf.Sqrt(3.0f) * 0.5f;

    /// <summary>The joint across a course, and the one up it.
    ///
    /// <b>They are different, and the second is zero.</b> A gap on all three axes
    /// was the Blender version's worst bug: every brick hung a few millimetres in
    /// the air, and forty-one of those released at once is a heap rather than a
    /// wall. Nothing bears on the perpend, so a gap there is free; everything
    /// bears on the bed.
    ///
    /// <b>And free is not the same as wanted: this is 10 mm, where it was 60.</b>
    /// It was a budget the jitter spent - the joint had to be wide enough that
    /// two bricks wandering and yawing at each other still came apart - so the
    /// number quoted here was never the joint you saw, it was the worst case
    /// plus the wobble. Now every irregularity is taken out of the brick instead
    /// (see <see cref="HalfBrick"/>), the joint is the joint, and it can be the
    /// real one: 0.0025 of a cell is 10 mm at <c>WallRig.MetresPerCell</c>, which
    /// is what a bricklayer leaves. <b>The dark line is not this.</b> The seam is
    /// painted round every face by the shader, in cell units, so it survives a
    /// joint too thin to see - which is the whole reason this could shrink at
    /// all.</summary>
    public const float Perpend = 0.0025f;
    public const float Bed = 0.0f;

    public const float PitchX = BrickLong + Perpend;
    public const float PitchY = BrickTall + Bed;

    /// <summary>How far the bottom course is buried.
    ///
    /// A footing sits in the ground rather than on it, which is the excuse; the
    /// reason is that a course laid exactly on y=0 has its underside coplanar
    /// with the ground plane, and coplanar is a coin toss over which surface the
    /// depth test keeps. It draws as a band of stripes along the bottom of every
    /// brick - <c>hit_scar</c>'s lift, arrived at from underneath.</summary>
    public const float Footing = 0.006f;

    // --- what a wall is --------------------------------------------------------

    /// <summary>How a wall is laid out. Fractions throughout; see the class
    /// remarks.</summary>
    public sealed class Recipe
    {
        /// <summary>The one number that changes the wall.</summary>
        public int Seed = 11;

        /// <summary>Columns in the bottom course, and how many courses to try.
        /// The profile eats into the run as it climbs, so the wall usually runs
        /// out of bricks before it runs out of courses.</summary>
        public int Columns = 6;
        public int Courses = 8;

        /// <summary>How many sides of the cell the wall runs along, 1 to 6.
        ///
        /// <b>One run, folded, and never N walls placed side by side.</b> A
        /// course is a one-dimensional thing - a row of slots at a pitch - so the
        /// chain is laid straight, in arc length, and <see cref="Fit"/> bends it
        /// onto the boundary. Laid as separate walls each side would carry its
        /// own profile, and a ruin that steps down to one brick six times over
        /// reads as six ruins rather than as one wall that lost its ends.
        ///
        /// <b>The fan is centred on the side the shot comes from</b>, so an odd
        /// count puts a face square to the shooter and an even one puts a corner
        /// there. Symmetric on purpose: the turntable then shows the same wall
        /// from either hand, which a chain growing one way would not.
        ///
        /// Six closes the ring, and the two ends of the chain meet behind the
        /// shooter - so the taper's breach lands at the back, which is the one
        /// place a closed ring can afford one.</summary>
        public int Sides = 1;

        /// <summary>How thick, in leaves. <b>Thickness is a count and never a
        /// length</b>, for the reason nothing here is a length: a leaf is a brick
        /// on its bed, so a wall is one brick thick or two or three, and a number
        /// between them is not a wall anybody laid.
        ///
        /// One leaf reads as a screen and has nothing for the stretcher bond to
        /// tie into; two is the default the whole file was measured on. Every
        /// leaf past the first is offset half a pitch from the one before it, so
        /// each backs its neighbour's perpends - the bond that stopped this wall
        /// being see-through when there were only two.
        ///
        /// <b><see cref="BackKeep"/> is 1, because a gap in a back leaf is a
        /// hole in the wall and not a weathered one.</b> It was 0.78, on the idea
        /// that a ruin has lost pieces from the inside too - which is true of a
        /// ruin and false of this picture: the camera sees the front, so a missing
        /// back brick is never read as a missing brick, only as light coming
        /// through the front leaf's joints. What a ruin loses where the eye can
        /// tell is on the outside, and that is the profile's job.</summary>
        public int Leaves = 2;
        public float BackKeep = 1.0f;

        /// <summary>How much of a course the profile takes off per step, as a
        /// weight over 0, 1 and 2 columns. Biased to the right so the ruin steps
        /// down one way and keeps a tall end, which is what the reference is.
        /// </summary>
        public float StepLeft = 0.55f;
        public float StepRight = 1.10f;

        /// <summary>Chance a course loses a brick out of its middle.
        ///
        /// <b>Zero, and the knob stays.</b> A notch is a hole clean through a
        /// wall two bricks thick, so it does not read as a missing brick - it
        /// reads as a wall you can see the sky through. What makes this a ruin is
        /// the stepped profile and the bricks lying in front of it, both of which
        /// take pieces off the outside where the eye can tell what happened.
        /// </summary>
        public float NotchChance = 0.0f;

        /// <summary>Bricks that never made it into a course, lying against it on
        /// the cell's side, and chips of them in the apron. Counts, not positions:
        /// where they go is worked out against the wall that got built, and
        /// against the cell it has to stay inside - see <see cref="Scatter"/>.
        ///
        /// <b>That is the fix for the one bug the Blender layout kept.</b> Its
        /// four fallen bricks were hand-placed against a single-leaf wall and
        /// stayed where they were when the back leaf arrived, which put two of
        /// them inside it - 63 mm, the worst overlap in that build.</summary>
        public int Fallen = 5;

        /// <summary>Chips of brick in the apron. <b>Zero: they read as gravel,
        /// not as a wall that lost pieces</b>, and a wall loses bricks. The
        /// machinery stays because the ground under a real ruin does have grit
        /// on it - it just wants to be part of the tile's art, where every other
        /// scatter on this board lives, rather than fifty more solids in the
        /// collapse.</summary>
        public int Chips = 0;

        /// <summary>Jitter, as fractions of a brick. Position and yaw only: a
        /// leaning brick in a course lifts its own far corner into the course
        /// above, which is 9.3 mm of interpenetration in the Blender build and
        /// the reason only its crest brick leans.
        ///
        /// <b>The size jitter is what pays for the other two now, so it is the
        /// one that grew.</b> A brick is only ever cut short, never long, and
        /// what it gives up is the room it is then allowed to wander and turn in
        /// - so the wobble is bounded by the brick and not by the joint. Ask for
        /// none of it and you get a wall of identical bricks in dead straight
        /// courses, which is the honest price of a 10 mm perpend.</summary>
        public float Jitter = 0.035f;
        public float Yaw = 3.0f;
        public float SizeJitter = 0.060f;
    }

    /// <summary>One piece. An oriented box with a shade, in the flat plane's
    /// (x, lift, y) with Y up.</summary>
    public struct Block
    {
        public Vector3 Seat;      // centre
        public Vector3 Half;      // half extents, before the basis
        public Basis Turn;        // orientation
        public float Tone;        // 0..1, picked along the brick ramp
        public float Moss;        // 0..1, how green the bottom courses go
        public int Course;        // -1 for a fallen brick or a chip
        public bool Chip;

        public readonly Transform3D Frame => new(Turn, Seat);
    }

    /// <summary>The laid-out wall and what can be said about it without drawing
    /// it.</summary>
    public sealed class Plan
    {
        public List<Block> Blocks = new();
        public int Courses;
        public int Sides = 1;
        public int Leaves = 2;
        /// <summary>End bricks cut back at a shared corner, and how deep the
        /// worst cut went. Reported because a mitre is the one place this layout
        /// removes material the recipe asked for: a corner that eats whole bricks
        /// is a wall thicker than the cell can turn.</summary>
        public int Mitred;
        public float MitreCut;
        public float Overlap;         // worst interpenetration, fractions of R
        public string OverlapPair = "";
        public float Reach;           // worst hex factor; 1.0 is exactly the edge
        public Vector3 Size;
        public float Top;

        /// <summary>Pieces of apron that found nowhere clear to lie. Reported
        /// rather than hidden: a seed that crowds is a seed with rubble inside
        /// its own wall, and the picture does not show it.</summary>
        public int Crowded;

        public int Bricks
        {
            get
            {
                int n = 0;
                foreach (Block b in Blocks)
                    if (!b.Chip)
                        n++;
                return n;
            }
        }

        public string Note() =>
            $"{Blocks.Count} pieces, {Courses} courses, "
            + $"{Sides} sides, {Leaves} leaves, "
            + (Mitred > 0 ? $"mitred {Mitred} (worst {MitreCut:F3}), " : "")
            + $"{Size.X:F2}x{Size.Z:F2}x{Size.Y:F2} of a cell, "
            + $"reach {Reach:F3}, crowded {Crowded}, "
            + $"worst overlap {Overlap:+0.0000;-0.0000;0.0000}"
            + (Overlap > 0.0f ? $" ({OverlapPair})" : "");
    }

    // --- the layout ------------------------------------------------------------

    /// <summary>A hash in [0,1) off a piece index and a salt.
    ///
    /// Salted rather than advanced, which is <see cref="Grove.Hash01"/>'s reason
    /// and it matters more here: a stream would make every draw depend on how many
    /// were taken before it, so adding one chip would reshuffle the whole wall.
    /// That is exactly what the chip depth did to the Blender build, and the point
    /// of doing it this way is that a seed keeps meaning the same wall while the
    /// generator is still being written.</summary>
    public static float Roll(int seed, int index, int salt) =>
        (float)Grove.Hash01(seed * 6151 + index, salt, 0x5bd1);

    private static float Span(int seed, int index, int salt, float lo, float hi) =>
        lo + (hi - lo) * Roll(seed, index, salt);

    /// <summary>Lay a wall out. Fractions of the cell throughout; nothing here
    /// knows how big a cell is.</summary>
    public static Plan Lay(Recipe? recipe = null)
    {
        Recipe r = recipe ?? new Recipe();
        var plan = new Plan();
        int seed = r.Seed;
        int sides = Mathf.Clamp(r.Sides, 1, 6);
        int leaves = Mathf.Clamp(r.Leaves, 1, 6);
        plan.Sides = sides;
        plan.Leaves = leaves;
        // The whole chain, in columns. <b>The wall gets longer with the sides it
        // runs along and the taper does not.</b> `Columns` is what one side is cut
        // into, so a two-sided wall is twice as many slots - but the profile still
        // eats `Columns - 1` of them off each end, which is what keeps the middle
        // of a long wall at full height instead of grinding the whole thing down
        // to a wedge. At one side this is the arithmetic that was already here.
        int span = r.Columns * sides;

        // --- the profile -------------------------------------------------------
        // A stepped ruin is a run per course, and the run only ever shrinks. This
        // is the whole of what the seven hand-written lines said.
        //
        // **The taper is set by the height that was asked for, and that is the
        // whole of the fix over a per-course chance.** The first version rolled a
        // step of 0, 1 or 2 columns off each end every course, which averages 1.9
        // columns of a six-column wall and left it three courses tall whatever
        // `Courses` said - a low broad heap, not a stepped ruin. Here the run has
        // to reach one column by the last course, so what is rolled is *where
        // along that taper* a course sits, and the totals land on the crest by
        // construction.
        int total = Mathf.Max(0, r.Columns - 1);
        int steps = Mathf.Max(1, r.Courses - 1);
        float lean = r.StepRight / Mathf.Max(r.StepLeft + r.StepRight, 1e-4f);
        int left = 0, right = span - 1;
        var runs = new List<(int Left, int Right, int Notch)>();
        for (int c = 0; c < r.Courses && left <= right; c++)
        {
            int notch = -1;
            if (c > 0 && right - left >= 2 && Roll(seed, c, 21) < r.NotchChance)
                notch = left + 1 + (int)(Roll(seed, c, 22) * (right - left - 1));
            runs.Add((left, right, notch));

            // Jitter the progress and never the step: a jittered step is a random
            // walk and does not arrive anywhere, while a jittered position along a
            // known taper is uneven and still ends at the crest.
            float along = Mathf.Clamp((c + 1) / (float)steps
                                      + (Roll(seed, c, 33) - 0.5f) * 0.24f,
                                      0.0f, 1.0f);
            int gone = Mathf.RoundToInt(total * along);
            int cutRight = Mathf.RoundToInt(gone * lean);
            // Monotone, because a course cannot be wider than the one below it.
            left = Mathf.Max(left, gone - cutRight);
            right = Mathf.Min(right, span - 1 - cutRight);
        }
        plan.Courses = runs.Count;

        // --- the courses -------------------------------------------------------
        int id = 0;
        for (int c = 0; c < runs.Count; c++)
        {
            (int lo, int hi, int notch) = runs[c];
            // Stretcher bond: every other course slides half a brick, which is
            // what stops the perpends lining up into a crack.
            float shift = (c % 2 == 0) ? 0.0f : PitchX * 0.5f;
            for (int col = lo; col <= hi; col++)
            {
                if (col == notch)
                    continue;
                float x = (col - (span - 1) * 0.5f) * PitchX + shift;
                // Half a brick across as well as behind, and this is the whole of
                // why the wall was see-through. Laid at the same x, two leaves
                // put their perpends in the same place, so every joint was a slot
                // through the full thickness with the cavity between the leaves
                // behind it - daylight, at every joint, on a wall that is two
                // bricks thick. Offset, each leaf backs the one in front of it,
                // which is the bond a real wall is built to and costs nothing.
                // Alternating rather than cumulative: three leaves want the
                // middle one staggered and the back one square behind the front,
                // which is the bond and not a staircase.
                for (int k = 0; k < leaves; k++)
                {
                    if (k > 0 && Roll(seed, id, 44) >= r.BackKeep)
                        continue;
                    float bond = (k % 2 == 1) ? PitchX * 0.5f : 0.0f;
                    plan.Blocks.Add(Course(seed, id++, r, x + bond, c,
                                           -LeafGap(r) * k));
                }
            }
        }
        Measure(plan);
        return plan;
    }

    /// <summary>Drop what fell off the wall, into the cell the wall stands on.
    ///
    /// <b>After the seat, never before it, and that is the whole reason this is
    /// its own pass.</b> While the wall was fitted to the middle of the cell the
    /// apron could be laid in the wall's own coordinates and scaled with it; a
    /// wall standing on a boundary has a side that is off the board, so where the
    /// rubble may lie is a question about the hexagon and cannot be answered
    /// before the wall knows where it sits.
    ///
    /// <b>Inward, because the other side of this wall is the next cell.</b> The
    /// prop belongs to one cell - a brick that rolled to a neighbour is a brick
    /// in somebody else's sprite - and the wall's outer face is the boundary
    /// itself, so there is no outward side left to fall on. What that costs is
    /// named: at the near face the apron is behind the wall from the camera, and
    /// what reads is the part of it that spreads past the wall's ends. It does
    /// spread past them, and by construction: the band lies inward of the wall,
    /// the hexagon widens as it goes in, and the pieces are sampled across the
    /// width that is actually there rather than across the wall's own.</summary>
    public static void Scatter(Plan plan, Recipe? recipe, float scale, float coverage)
    {
        Recipe r = recipe ?? new Recipe();
        int seed = r.Seed;
        int sides = Mathf.Clamp(plan.Sides, 1, 6);
        // One band per side, in that side's own frame, and the pieces are dealt
        // round the chain. A single band across a folded wall would be a band
        // across whichever side happened to face +z, which is a heap in front of
        // one third of a three-sided ruin.
        var frames = new Basis[sides];
        var faces = new float[sides];
        for (int f = 0; f < sides; f++)
        {
            frames[f] = new Basis(Vector3.Up, Mathf.DegToRad(Fan(f, sides)));
            Basis back = frames[f].Inverse();
            float inner = float.MaxValue;
            foreach (Block b in plan.Blocks)
            {
                if (b.Course < 0)
                    continue;
                foreach (Vector3 c in Corners(b))
                {
                    Vector3 q = back * c;
                    // Its own kite only. Without this a side reads its
                    // neighbours' bricks as its own inner face, which on a chain
                    // is most of the cell, and the apron is laid in the middle of
                    // the ring instead of at the foot of the wall.
                    if (sides > 1 && Mathf.Abs(q.X) > q.Z / Mathf.Sqrt(3.0f))
                        continue;
                    inner = Mathf.Min(inner, q.Z);
                }
            }
            faces[f] = inner;
        }
        if (faces[0] > 1.0f)
            return;                       // no masonry, nothing to have fallen off

        // Deep enough for a piece that is lying across it: a brick turned
        // ninety degrees spans its own length along z, so a band 0.75 of a brick
        // deep is thinner than the thing it is meant to hold and the rejection
        // has to throw pieces away for want of anywhere legal. Measured over
        // eight seeds: 0.75 of a brick left one of five unplaced on four of
        // them, 1.2 on one.
        float deep = BrickLong * 1.2f * scale;

        for (int i = 0; i < r.Fallen; i++)
        {
            int f = i % sides;
            float near = faces[f];
            float far = near - deep;
            // How wide the cell is at the shallow end of the band. The far end is
            // wider still, so this understates rather than overstates - and every
            // piece is put to the hexagon itself before it is kept.
            float reachX = Mathf.Max(0.0f,
                                     coverage - Mathf.Abs(near) / Mathf.Sqrt(3.0f));
            var turn = new Basis(Vector3.Up, Mathf.DegToRad(Span(seed, i, 53, -95.0f, 95.0f)))
                       * new Basis(Vector3.Right, Mathf.DegToRad(Span(seed, i, 54, -8.0f, 8.0f)));
            var piece = new Block
            {
                Half = LooseBrick(seed, i, 55, r) * scale,
                Turn = frames[f] * turn,
                Tone = Roll(seed, i, 56),
                Moss = Span(seed, i, 57, 0.15f, 0.55f),
                Course = -1,
            };
            piece.Seat = new Vector3(0.0f, piece.Half.Y, 0.0f);
            if (Drop(plan, ref piece, seed, i, 50, frames[f], reachX, near, far,
                     coverage))
                plan.Blocks.Add(piece);
        }

        for (int i = 0; i < r.Chips; i++)
        {
            int f = i % sides;
            float near = faces[f];
            float far = near - deep;
            float reachX = Mathf.Max(0.0f,
                                     coverage - Mathf.Abs(near) / Mathf.Sqrt(3.0f));
            float k = Span(seed, i, 61, 0.20f, 0.38f);
            var piece = new Block
            {
                Half = new Vector3(BrickLong * k, BrickTall * k, BrickDeep * k)
                       * 0.5f * scale,
                Turn = frames[f]
                       * new Basis(Vector3.Up, Mathf.DegToRad(Span(seed, i, 64, -180.0f, 180.0f)))
                       * new Basis(Vector3.Right, Mathf.DegToRad(Span(seed, i, 65, -25.0f, 25.0f))),
                Tone = Roll(seed, i, 66),
                Moss = Span(seed, i, 67, 0.0f, 0.4f),
                Course = -1,
                Chip = true,
            };
            piece.Seat = new Vector3(0.0f, piece.Half.Y, 0.0f);
            if (Drop(plan, ref piece, seed, i, 60, frames[f], reachX,
                     near + BrickDeep * scale, far, coverage))
                plan.Blocks.Add(piece);
        }

        Measure(plan);
    }

    /// <summary>
    /// Find somewhere in front of the wall this piece actually fits, by trying.
    ///
    /// <b>Rejection against what got built, never a keep-out radius off a table.</b>
    /// A rotated brick is wider than a brick - at 95 degrees this one spans its own
    /// length across the depth axis rather than its depth - so a clearance measured
    /// from the number written beside it in a table is measured from the wrong
    /// number. That understatement is the Blender layout's oldest surviving bug and
    /// it was fixed there twice, once for the chips and once for the fallen bricks.
    /// Here the piece is simply asked whether it clears, by the same test the guard
    /// will judge it with, so the two cannot disagree.
    ///
    /// The tries are salted by attempt, so a piece that has to move is still the
    /// same piece on the next run - see <see cref="Roll"/> for why nothing here
    /// advances a stream.
    /// </summary>
    private static bool Drop(Plan plan, ref Block piece, int seed, int i, int salt,
                             Basis frame, float reachX, float near, float far,
                             float coverage)
    {
        const int Tries = 24;
        float clear = Perpend * 0.5f;
        for (int t = 0; t < Tries; t++)
        {
            // The depth first, then the width the cell has at that depth. The
            // band is a wedge and not a rectangle - the hexagon opens out as it
            // goes in from the side the wall stands on - so a piece sampled across
            // the wall's own width would never reach past the wall's ends, which
            // is the only part of the apron the camera gets to see from the near
            // face. Measured off the drawn cell: the wall's half-run is 0.485 and
            // the wedge is 0.57 wide at the back of the masonry.
            float z = Span(seed, i * 31 + t, salt + 2, near, far);
            float wide = Mathf.Max(0.0f,
                                   reachX - (Mathf.Abs(z) - Mathf.Abs(near))
                                            / Mathf.Sqrt(3.0f));
            // Drawn in the side's own frame and then turned into the cell's:
            // near, far and the wedge are all statements about one side of the
            // wall. Identity when the wall stands on one side, so a single-sided
            // seed lands where it always did, to the bit.
            piece.Seat = frame * new Vector3(
                Span(seed, i * 31 + t, salt + 1, -wide, wide),
                piece.Half.Y,
                z);
            // Two tests, and the second is the cell. The wall is on the boundary
            // now, so a piece that clears every brick can still be over the edge
            // - and it is asked with the very test the fit is stated in, so the
            // two cannot disagree about where the cell ends.
            bool clean = Reach(piece) <= coverage;
            for (int k = 0; k < plan.Blocks.Count && clean; k++)
                if (Penetration(piece, plan.Blocks[k]) > -clear)
                    clean = false;
            if (clean)
                return true;
        }
        // Nothing found in twenty-four tries, so this piece does not exist.
        //
        // **Left out rather than dropped somewhere clear, and counted.** A
        // fallback position is the same position every time, so every piece that
        // could not be placed stacks in it - which is how the Blender apron once
        // produced 90mm of overlap out of the routine written to prevent it. A
        // missing chip is invisible; a chip inside the wall is the one defect a
        // still picture cannot show.
        plan.Crowded++;
        return false;
    }

    /// <summary>How far apart the two leaves sit, centre to centre.
    ///
    /// <b>The bare joint, and it is exact.</b> It used to be the joint plus twice
    /// the jitter and twice the yaw's corner swing, because those were what closed
    /// it - the first version used the bare perpend and the guard caught it on the
    /// first seed ever generated, 4.95px of front leaf inside back. That allowance
    /// is gone because the thing it allowed for is gone: a course brick's
    /// footprint is now the nominal brick whatever it does inside it, so two
    /// leaves a perpend apart are a perpend apart. Measured, it took 135 mm of
    /// daylight out of the cavity.</summary>
    private static float LeafGap(Recipe r) => BrickDeep + Perpend;

    /// <summary>A brick that fits its slot however it is turned.
    ///
    /// <b>Every irregularity comes out of the brick and never out of the
    /// joint.</b> That is the whole of what lets the perpend be 10 mm. A box
    /// yawed by t has an x extent of <c>a*cos + c*sin</c> and a z extent of
    /// <c>c*cos + a*sin</c>, so asking both of those to come out at the nominal
    /// half-brick is a 2x2 solve, and the answer is a brick a little shorter and
    /// a little thinner than nominal - 2% and 10% at three degrees. Turn it, and
    /// it still occupies exactly one brick of the course.
    ///
    /// The size jitter is then one-sided: a brick is cut short, never long. Long
    /// is the same failure under another name - it has to come out of the joint,
    /// because there is nowhere else - and short leaves room the position jitter
    /// is allowed to spend, which is where the wobble comes from now.
    ///
    /// <b>Length and depth only: the height is never jittered.</b> The bed joint
    /// is zero, so a brick 2.2% taller than nominal is 2.2% of interpenetration
    /// with the course above it and nothing absorbs it. Measured before that was
    /// found: a steady +0.0016 of a cell on every seed, always between two bricks
    /// in the same column - small enough to look like noise in the guard and not
    /// noise at all.</summary>
    private static Vector3 HalfBrick(int seed, int id, int salt, Recipe r, float yaw)
    {
        float cs = Mathf.Cos(yaw), sn = Mathf.Abs(Mathf.Sin(yaw));
        // cos(2t), and it only vanishes at 45 degrees - a course brick never gets
        // near that, and the apron's loose bricks do not come through here.
        float det = Mathf.Max(cs * cs - sn * sn, 1e-3f);
        float ax = (BrickLong * cs - BrickDeep * sn) / (2.0f * det);
        float az = (BrickDeep * cs - BrickLong * sn) / (2.0f * det);
        float gx = 1.0f - Roll(seed, id, salt) * r.SizeJitter;
        float gz = 1.0f - Roll(seed, id, salt + 7) * r.SizeJitter;
        return new Vector3(ax * gx, BrickTall * 0.5f, az * gz);
    }

    /// <summary>A brick that fell off. No slot to keep to, so no solve: cut short
    /// like the others and then placed by rejection, which is a stronger test
    /// than any size rule.</summary>
    private static Vector3 LooseBrick(int seed, int id, int salt, Recipe r)
    {
        float gx = 1.0f - Roll(seed, id, salt) * r.SizeJitter;
        float gz = 1.0f - Roll(seed, id, salt + 7) * r.SizeJitter;
        return new Vector3(BrickLong * gx, BrickTall, BrickDeep * gz) * 0.5f;
    }

    private static Block Course(int seed, int id, Recipe r, float x, int c, float depth)
    {
        // No lift jitter and no lean: both raise a brick into the course above,
        // and the bed joint is zero so there is nowhere for it to go.
        float yaw = (Roll(seed, id, 13) - 0.5f) * 2.0f * Mathf.DegToRad(r.Yaw);
        Vector3 half = HalfBrick(seed, id, 14, r, yaw);
        // **Every brick owns one slot of the course and never leaves it.** The
        // perpend used to be a budget the jitter spent, and it overspent: 0.035
        // of a 0.214 brick is 0.0075 of wander each against a 0.0148 joint, plus
        // the swing the yaw gives the near corner, and the guard reported exactly
        // that - a steady 0.0016 of a cell between two bricks in the same course
        // on the seeds where both wandered the same way. The slot inverts it. A
        // brick's yawed footprint is already the nominal brick, so what it may
        // wander is what it gave up by being cut short, and two neighbours are a
        // full perpend apart in the worst case rather than in the average one.
        // Same-course overlap is zero by construction now, and the guard has
        // nothing left to catch there.
        float cs = Mathf.Cos(yaw), sn = Mathf.Abs(Mathf.Sin(yaw));
        float roomX = Mathf.Max(0.0f, BrickLong * 0.5f - (half.X * cs + half.Z * sn));
        float roomZ = Mathf.Max(0.0f, BrickDeep * 0.5f - (half.Z * cs + half.X * sn));
        float jx = (Roll(seed, id, 11) - 0.5f) * 2.0f
                   * Mathf.Min(r.Jitter * BrickLong, roomX);
        float jz = (Roll(seed, id, 12) - 0.5f) * 2.0f
                   * Mathf.Min(r.Jitter * BrickDeep, roomZ);
        return new Block
        {
            Seat = new Vector3(x + jx, (c + 0.5f) * PitchY - Footing, depth + jz),
            Half = half,
            Turn = new Basis(Vector3.Up, yaw),
            Tone = Roll(seed, id, 15),
            // Moss climbs out of the ground, so it is a function of the course
            // and not of world height - the Blender material's argument, which is
            // that anything derived from world height crawls when the piece moves.
            // Out of the ground and only for the first courses of it. Against
            // the courses that exist, not against a constant: on a three-course
            // ruin a fixed reach of 2.6 makes the entire wall green.
            Moss = Mathf.Clamp(1.0f - c / (0.34f * r.Courses + 0.7f), 0.0f, 1.0f)
                   * Span(seed, id, 16, 0.25f, 1.0f),
            Course = c,
        };
    }

    // --- the guards ------------------------------------------------------------

    private static void Measure(Plan plan)
    {
        var lo = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        var hi = new Vector3(float.MinValue, float.MinValue, float.MinValue);
        foreach (Block b in plan.Blocks)
        foreach (Vector3 corner in Corners(b))
        {
            lo = new Vector3(Mathf.Min(lo.X, corner.X), Mathf.Min(lo.Y, corner.Y),
                             Mathf.Min(lo.Z, corner.Z));
            hi = new Vector3(Mathf.Max(hi.X, corner.X), Mathf.Max(hi.Y, corner.Y),
                             Mathf.Max(hi.Z, corner.Z));
        }
        plan.Size = hi - lo;
        plan.Top = hi.Y;
        plan.Reach = Reach(plan.Blocks);
        (plan.Overlap, plan.OverlapPair) = Worst(plan.Blocks);
    }

    /// <summary>How far the pieces poke out of a flat-top hexagon of radius 1.
    /// 1.0 is exactly the edge.
    ///
    /// The hexagon's own two constraints, never a circle: a bounding circle is
    /// the inradius and throws away the 15% of extra room along the vertex axis,
    /// which is the axis a wall runs along.</summary>
    public static float Reach(in Block b)
    {
        float worst = 0.0f;
        foreach (Vector3 c in Corners(b))
        {
            float x = Mathf.Abs(c.X), z = Mathf.Abs(c.Z);
            worst = Mathf.Max(worst, Mathf.Max(z / (Mathf.Sqrt(3.0f) * 0.5f),
                                               x + z / Mathf.Sqrt(3.0f)));
        }
        return worst;
    }

    public static float Reach(IReadOnlyList<Block> blocks)
    {
        float worst = 0.0f;
        foreach (Block b in blocks)
        foreach (Vector3 c in Corners(b))
        {
            float x = Mathf.Abs(c.X), z = Mathf.Abs(c.Z);
            worst = Mathf.Max(worst, Mathf.Max(z / (Mathf.Sqrt(3.0f) * 0.5f),
                                               x + z / Mathf.Sqrt(3.0f)));
        }
        return worst;
    }

    private static Vector3[] Corners(in Block b)
    {
        var outp = new Vector3[8];
        int k = 0;
        for (int i = -1; i <= 1; i += 2)
        for (int j = -1; j <= 1; j += 2)
        for (int m = -1; m <= 1; m += 2)
            outp[k++] = b.Seat + b.Turn * new Vector3(b.Half.X * i, b.Half.Y * j,
                                                      b.Half.Z * m);
        return outp;
    }

    /// <summary>Worst interpenetration between any two pieces, by the separating
    /// axis test over oriented boxes.
    ///
    /// <b>Fifteen axes, and the verdict is the smallest of them.</b> Two boxes
    /// overlap only where *every* axis overlaps, so the answer is the minimum;
    /// taking the maximum - which the Blender version did for a while - reported
    /// two bricks honestly spaced along a course as 0.266 of overlap, wider than a
    /// brick. And it has to be the oriented test: a world-aligned box inflates
    /// anything turned, which invented 32 mm between a header and a stretcher and
    /// made the guard unsatisfiable, and a guard that cannot be met stops being
    /// read.</summary>
    public static (float Worst, string Pair) Worst(IReadOnlyList<Block> blocks)
    {
        float worst = float.MinValue;
        string pair = "";
        for (int i = 0; i < blocks.Count; i++)
        for (int j = i + 1; j < blocks.Count; j++)
        {
            // Cheap reject first: this is O(n^2) over sixty-odd pieces, which is
            // nothing, but the axis test is fifteen dot products a pair.
            Vector3 d = blocks[j].Seat - blocks[i].Seat;
            float span = blocks[i].Half.Length() + blocks[j].Half.Length();
            if (d.LengthSquared() > span * span)
                continue;
            float p = Penetration(blocks[i], blocks[j]);
            if (p > worst)
            {
                worst = p;
                pair = $"{i}/{j}";
            }
        }
        return (worst == float.MinValue ? -1.0f : worst, pair);
    }

    private static float Penetration(in Block a, in Block b)
    {
        Vector3 t = b.Seat - a.Seat;
        var axes = new Vector3[15];
        int n = 0;
        for (int i = 0; i < 3; i++)
            axes[n++] = Col(a.Turn, i);
        for (int i = 0; i < 3; i++)
            axes[n++] = Col(b.Turn, i);
        for (int i = 0; i < 3; i++)
        for (int j = 0; j < 3; j++)
        {
            Vector3 c = Col(a.Turn, i).Cross(Col(b.Turn, j));
            if (c.LengthSquared() > 1e-8f)
                axes[n++] = c.Normalized();
        }

        float least = float.MaxValue;
        for (int i = 0; i < n; i++)
        {
            Vector3 ax = axes[i];
            float ra = Extent(a, ax), rb = Extent(b, ax);
            float gap = ra + rb - Mathf.Abs(t.Dot(ax));
            if (gap < least)
                least = gap;
            if (least < 0.0f)
                return least;          // separated on this axis, and that settles it
        }
        return least;
    }

    /// <summary>A basis column, i.e. one of the box's own axes. Godot spells
    /// these X, Y and Z rather than by index.</summary>
    private static Vector3 Col(in Basis t, int i) =>
        i == 0 ? t.X : (i == 1 ? t.Y : t.Z);

    private static float Extent(in Block b, Vector3 axis) =>
        Mathf.Abs(Col(b.Turn, 0).Dot(axis)) * b.Half.X
        + Mathf.Abs(Col(b.Turn, 1).Dot(axis)) * b.Half.Y
        + Mathf.Abs(Col(b.Turn, 2).Dot(axis)) * b.Half.Z;

    // --- standing it on a side of the cell -------------------------------------

    /// <summary>Cut the wall to the side it stands on, and seat it there.
    ///
    /// <b>The cell sets the length; the recipe only says how many pieces that
    /// length is cut into.</b> A wall on a boundary is not a prop that happens to
    /// fit a cell - it is a piece of that boundary, so its run is the hexagon's
    /// side and its face is the hexagon's apothem, and both come out of the same
    /// <paramref name="coverage"/>. That one number is what keeps it off the tile
    /// edge, and it is exact rather than approximate: a run of
    /// <c>Edge * coverage</c> seated at <c>Apothem * coverage</c> puts the wall's
    /// two outer top corners on the two vertices of the cell shrunk by coverage,
    /// because <c>0.5c + (sqrt(3)/2)c/sqrt(3)</c> is <c>c</c>. The wall stands on
    /// the side of a slightly smaller hexagon, and <see cref="Reach"/> comes back
    /// reading coverage to the last digit.
    ///
    /// <b>Where it used to be a ceiling it is now a cut, and that is the change.</b>
    /// The old fit scaled the prop down if it stuck out and left it alone if it
    /// did not, on the argument that a layout stated in cell fractions is already
    /// the size that was asked for. True while the wall stood in the middle of the
    /// cell with nothing to line up with; a wall standing on a side has something
    /// to line up with, and being a little short of it is a wall that missed.
    ///
    /// Centred before it is seated, and that is not cosmetic: the profile is
    /// deliberately lopsided, so the run has to be measured and centred rather
    /// than assumed from the column count - the stretcher bond alone puts half a
    /// pitch of the upper courses past the bottom one.
    ///
    /// <b>And it folds, which is the whole of running along more than one
    /// side.</b> The chain is laid straight and cut to <c>Sides</c> sides of the
    /// shrunk cell, then each brick is sent to the side its run coordinate falls
    /// in and turned by sixty degrees a side. Arc length, so the fold neither
    /// stretches a course nor gathers it, and the last brick of one side and the
    /// first of the next meet at the vertex exactly. The fan is centred, so the
    /// side the shot comes from is either square to it or split by a corner and
    /// never lopsided.
    ///
    /// What that leaves is the corner, and <see cref="Mitre"/> is the answer:
    /// two slabs meeting at 120 degrees overlap behind their vertex, so an end
    /// with a neighbour is cut back to the cell's own kite boundary. Nothing is
    /// cut where there is no neighbour, which is what keeps a one-sided wall the
    /// wall it has always been.
    ///
    /// Masonry only. The apron is dropped afterwards by <see cref="Scatter"/>,
    /// against a wall that already knows where it stands.</summary>
    public static float Fit(Plan plan, float coverage = 0.97f)
    {
        int sides = Mathf.Clamp(plan.Sides, 1, 6);
        float lo = float.MaxValue, hi = float.MinValue, face = float.MinValue;
        foreach (Block b in plan.Blocks)
        {
            if (b.Course < 0)
                continue;
            foreach (Vector3 c in Corners(b))
            {
                lo = Mathf.Min(lo, c.X);
                hi = Mathf.Max(hi, c.X);
                face = Mathf.Max(face, c.Z);
            }
        }
        if (lo > hi)
            return 1.0f;

        float run = hi - lo;
        float side = Edge * coverage;              // one side of the shrunk cell
        float scale = run > 1e-4f ? side * sides / run : 1.0f;
        float mid = (lo + hi) * 0.5f;
        // Everything moves together: x about the run's middle, y about the ground
        // it is footed in, z so that the outer face lands on the side.
        float seat = Apothem * coverage - face * scale;
        float half = side * sides * 0.5f;
        plan.Mitred = 0;
        plan.MitreCut = 0.0f;

        var kept = new List<Block>(plan.Blocks.Count);
        foreach (Block src in plan.Blocks)
        {
            Block b = src;
            float x = (b.Seat.X - mid) * scale;
            // Which side of the chain this brick belongs to, and where it sits
            // along that side. Arc length, so the fold neither stretches nor
            // gathers a course: the end of one side and the start of the next are
            // the same point on the boundary, and the two runs meet there.
            int s = Mathf.Clamp(Mathf.FloorToInt((x + half) / side), 0, sides - 1);
            // Subtract the side's own centre, and never add half a chain and
            // take it off again. Written that way it is exactly zero on a
            // one-sided wall in algebra and not in floats: (x + 0.485) - 0.485
            // drops the low bits of x, which is invisible in the render and not
            // invisible to the solver. Measured - the standing wall came back
            // inside its own one-pixel noise while the same HE round settled at
            // 1.93s instead of 2.28s, which is the whole collapse rearranged by
            // a rounding error.
            float axis = (s + 0.5f) * side - half;
            b.Seat = new Vector3(x - axis,
                                 b.Seat.Y * scale,
                                 b.Seat.Z * scale + seat);
            b.Half *= scale;

            // The mitre, and it is one rule for every corner, leaf and thickness.
            //
            // Two slabs meeting at 120 degrees cross behind their vertex - the
            // overlap runs back along each of them by the wall's own thickness -
            // so a chain of full-length sides is a wall inside itself at every
            // corner, which is the one defect the picture cannot show and the
            // solver answers with an explosion. The cell already says where the
            // join belongs: the six kites from the centre to each side tile the
            // ring with no gap and no overlap, so a brick is simply cut back to
            // its own kite at an end that has a neighbour.
            //
            // <b>Cut out of the brick, never out of the joint</b> - the same rule
            // that lets the perpend be 10 mm, arriving at a second place. And cut
            // only where somebody is on the other side: a lone wall pokes out of
            // its own kite near the ends by design, and clipping it there would
            // rebuild every seed this file was measured on.
            // Closed at six, and only then. The chain's two ends are the only
            // ends without a neighbour - except on a full ring, where they are
            // each other's. Measured before the wrap: 5.53px of one end inside
            // the other, at the one vertex behind the shooter.
            bool ring = sides == 6;
            bool cut = false;
            if (s > 0 || ring)
                cut |= Mitre(ref b, new Vector3(-Mathf.Sqrt(3.0f) * 0.5f, 0.0f, -0.5f),
                             plan);
            if (s < sides - 1 || ring)
                cut |= Mitre(ref b, new Vector3(Mathf.Sqrt(3.0f) * 0.5f, 0.0f, -0.5f),
                             plan);
            if (cut)
            {
                plan.Mitred++;
                if (b.Half.X <= 0.0f)
                    continue;                 // wholly inside the neighbour's kite
            }

            var turn = new Basis(Vector3.Up, Mathf.DegToRad(Fan(s, sides)));
            b.Seat = turn * b.Seat;
            b.Turn = turn * b.Turn;
            kept.Add(b);
        }
        plan.Blocks = kept;
        Measure(plan);
        return scale;
    }

    /// <summary>Which way side <paramref name="s"/> of a chain of
    /// <paramref name="sides"/> faces, in degrees off the one the shot comes from.
    ///
    /// <b>Whole sixties, never a centred fan.</b> The cell has six side normals
    /// and they are sixty degrees apart, so a chain of an even number of them
    /// cannot be centred on one of them - it can only be centred on a vertex, and
    /// a vertex is not somewhere a side can face. Asked for a centred fan the
    /// first version put every side of a two-sided wall thirty degrees off its
    /// own boundary, and the wall stood out of the cell: <see cref="Reach"/> 1.120
    /// against a coverage of 0.97, which is the corner of the prop hanging over
    /// the next tile.
    ///
    /// So an even count is lopsided by one side and says so, and what stays true
    /// at every count is the thing that has to: side zero faces the shooter.
    /// </summary>
    private static float Fan(int s, int sides) => (s - (sides - 1) / 2) * 60.0f;

    /// <summary>Cut a brick back behind a plane through the cell's centre, by
    /// shortening it along its own length.
    ///
    /// Exact in one step rather than iterated, and that is worth the algebra: take
    /// <c>d</c> off the half length and walk the seat <c>d</c> the same way, and
    /// both the centre's distance to the plane and the box's reach towards it fall
    /// by <c>d|Xn|</c> - so the violation goes down by twice that and the step
    /// that clears it is the violation over twice that. Iterating instead would
    /// leave a residue that depends on the yaw, which is to say a corner whose
    /// tightness is a function of the seed.</summary>
    private static bool Mitre(ref Block b, Vector3 plane, Plan plan)
    {
        float over = b.Seat.Dot(plane) + Extent(b, plane);
        if (over <= 0.0f)
            return false;
        float grip = Mathf.Abs(b.Turn.X.Dot(plane));
        if (grip < 1e-4f)
            return false;                      // length lies in the plane; nothing to take
        float d = over / (2.0f * grip);
        plan.MitreCut = Mathf.Max(plan.MitreCut, d);
        if (d >= b.Half.X)
        {
            b.Half = new Vector3(0.0f, b.Half.Y, b.Half.Z);
            return true;
        }
        b.Seat -= b.Turn.X * (d * Mathf.Sign(b.Turn.X.Dot(plane)));
        b.Half = new Vector3(b.Half.X - d, b.Half.Y, b.Half.Z);
        return true;
    }
}
