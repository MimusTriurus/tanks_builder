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
/// not change with scale, so shrinking the wall to its cell is one multiply and
/// not a rebuild. The Blender version could not say that - one absolute length
/// survived in it, the depth of a corner chip, and it made a smaller wall a
/// *different* wall: a different vertex count in the cut, a different number of
/// draws from the generator, and 38 bricks where there had been 37.
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

    /// <summary>The joint across a course, and the one up it.
    ///
    /// <b>They are different, and the second is zero.</b> A gap on all three axes
    /// was the Blender version's worst bug: every brick hung a few millimetres in
    /// the air, and forty-one of those released at once is a heap rather than a
    /// wall. Nothing bears on the perpend, so a gap there is free and draws the
    /// dark line the reference draws; everything bears on the bed.</summary>
    public const float Perpend = 0.0148f;
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

        /// <summary>Two leaves. A single-leaf wall reads as a screen, and the
        /// stretcher bond has nothing to tie into.
        ///
        /// <b><see cref="BackKeep"/> is 1, because a gap in the back leaf is a
        /// hole in the wall and not a weathered one.</b> It was 0.78, on the idea
        /// that a ruin has lost pieces from the inside too - which is true of a
        /// ruin and false of this picture: the camera sees the front, so a missing
        /// back brick is never read as a missing brick, only as light coming
        /// through the front leaf's joints. What a ruin loses where the eye can
        /// tell is on the outside, and that is the profile's job.</summary>
        public bool BackLeaf = true;
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

        /// <summary>Bricks that never made it into a course, lying in front, and
        /// chips of them in the apron. Counts, not positions: where they go is
        /// worked out against the wall that got built.
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
        /// the reason only its crest brick leans.</summary>
        public float Jitter = 0.035f;
        public float Yaw = 3.0f;
        public float SizeJitter = 0.022f;
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
        int left = 0, right = r.Columns - 1;
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
            right = Mathf.Min(right, r.Columns - 1 - cutRight);
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
                float x = (col - (r.Columns - 1) * 0.5f) * PitchX + shift;
                plan.Blocks.Add(Course(seed, id++, r, x, c, 0.0f));
                // Half a brick across as well as behind, and this is the whole of
                // why the wall was see-through. Laid at the same x, the two leaves
                // put their perpends in the same place, so every joint was a slot
                // through the full thickness with the cavity between the leaves
                // behind it - daylight, at every joint, on a wall that is two
                // bricks thick. Offset, each leaf backs the other's joints, which
                // is the bond a real two-leaf wall is built to and costs nothing.
                if (r.BackLeaf && Roll(seed, id, 44) < r.BackKeep)
                    plan.Blocks.Add(Course(seed, id++, r, x + PitchX * 0.5f, c,
                                           -LeafGap(r)));
            }
        }

        // --- what fell off it --------------------------------------------------
        // Placed against the wall that got built, never against a table.
        //
        // **+z is towards the camera, and this is the one place it matters.**
        // Stage3D maps a flat row to world z and the camera looks down -z, so the
        // near side of the board is +z - which means the back leaf goes to -z and
        // the rubble to +z. The first pass had both negative and put the whole
        // apron behind the wall, where it read as debris floating above it.
        float front = BrickDeep * 0.5f;
        float reachX = (r.Columns * 0.5f + 0.6f) * PitchX;
        // The apron sits against the wall. Spread over the whole cell it reads as
        // litter someone dropped rather than as a wall that lost pieces, and the
        // Blender prop's own numbers say the same - its rubble is banded along the
        // face, not scattered.
        reachX *= 0.82f;
        int laid = plan.Blocks.Count;
        for (int i = 0; i < r.Fallen; i++)
        {
            var turn = new Basis(Vector3.Up, Mathf.DegToRad(Span(seed, i, 53, -95.0f, 95.0f)))
                       * new Basis(Vector3.Right, Mathf.DegToRad(Span(seed, i, 54, -8.0f, 8.0f)));
            var piece = new Block
            {
                Half = HalfBrick(seed, i, 55, r),
                Turn = turn,
                Tone = Roll(seed, i, 56),
                Moss = Span(seed, i, 57, 0.15f, 0.55f),
                Course = -1,
            };
            piece.Seat = new Vector3(0.0f, piece.Half.Y, 0.0f);
            if (Drop(plan, ref piece, seed, i, 50, reachX, front,
                     front + BrickLong * 0.75f, laid))
                plan.Blocks.Add(piece);
        }

        for (int i = 0; i < r.Chips; i++)
        {
            float k = Span(seed, i, 61, 0.20f, 0.38f);
            var piece = new Block
            {
                Half = new Vector3(BrickLong * k, BrickTall * k, BrickDeep * k) * 0.5f,
                Turn = new Basis(Vector3.Up, Mathf.DegToRad(Span(seed, i, 64, -180.0f, 180.0f)))
                       * new Basis(Vector3.Right, Mathf.DegToRad(Span(seed, i, 65, -25.0f, 25.0f))),
                Tone = Roll(seed, i, 66),
                Moss = Span(seed, i, 67, 0.0f, 0.4f),
                Course = -1,
                Chip = true,
            };
            piece.Seat = new Vector3(0.0f, piece.Half.Y, 0.0f);
            if (Drop(plan, ref piece, seed, i, 60, reachX * 1.1f, front - BrickDeep,
                     front + BrickLong * 1.15f, laid))
                plan.Blocks.Add(piece);
        }

        Measure(plan);
        return plan;
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
                             float reachX, float near, float far, int against)
    {
        const int Tries = 24;
        float clear = Perpend * 0.5f;
        for (int t = 0; t < Tries; t++)
        {
            piece.Seat = new Vector3(
                Span(seed, i * 31 + t, salt + 1, -reachX, reachX),
                piece.Half.Y,
                Span(seed, i * 31 + t, salt + 2, near, far));
            bool clean = true;
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
    /// <b>Derived from the jitter rather than set beside it, because the jitter is
    /// what closes it.</b> A perpend of 0.0148 looks like plenty until two bricks
    /// three degrees apart swing their corners at each other: 0.107 of half-length
    /// times sin(3) is 0.0056 each, and the depth jitter adds 0.0037 each on top.
    /// The first version used the bare perpend and the guard caught it on the
    /// first seed ever generated - 4.95px, front leaf against back.</summary>
    private static float LeafGap(Recipe r) =>
        BrickDeep + Perpend
        + 2.0f * (r.Jitter * BrickDeep
                  + BrickLong * 0.5f * Mathf.Sin(Mathf.DegToRad(r.Yaw)));

    /// <summary>A brick, near enough its nominal size to be one.
    ///
    /// <b>Length and depth only: the height is never jittered.</b> The bed joint
    /// is zero, so a brick 2.2% taller than nominal is 2.2% of interpenetration
    /// with the course above it and nothing absorbs it. Measured before this: a
    /// steady +0.0016 of a cell on every seed, always between two bricks in the
    /// same column - which is small enough to look like noise in the guard and is
    /// not noise, it is the jitter.</summary>
    private static Vector3 HalfBrick(int seed, int id, int salt, Recipe r)
    {
        float gx = 1.0f + (Roll(seed, id, salt) - 0.5f) * 2.0f * r.SizeJitter;
        float gz = 1.0f + (Roll(seed, id, salt + 7) - 0.5f) * 2.0f * r.SizeJitter;
        return new Vector3(BrickLong * gx, BrickTall, BrickDeep * gz) * 0.5f;
    }

    private static Block Course(int seed, int id, Recipe r, float x, int c, float depth)
    {
        float yawRad = Mathf.DegToRad(r.Yaw);
        // **The perpend is a budget and the jitter spends it.** Two neighbours in
        // a course close on twice their own wander plus twice the swing their yaw
        // gives the near corner, so a jitter quoted as a fraction of a brick can
        // spend more than the joint has: 0.035 of a 0.214 brick is 0.0075 each
        // against a 0.0148 joint, and the guard reported exactly that - a steady
        // 0.0016 between two bricks in the same course, on the seeds where both
        // wandered the same way. Capped against the slack that is actually there.
        float swing = BrickDeep * 0.5f * Mathf.Sin(yawRad);
        float slack = Mathf.Max(0.0f, Perpend * 0.5f - swing);
        float jx = (Roll(seed, id, 11) - 0.5f) * 2.0f
                   * Mathf.Min(r.Jitter * BrickLong, slack);
        float jz = (Roll(seed, id, 12) - 0.5f) * 2.0f * r.Jitter * BrickDeep;
        // No lift jitter and no lean: both raise a brick into the course above,
        // and the bed joint is zero so there is nowhere for it to go.
        float yaw = (Roll(seed, id, 13) - 0.5f) * 2.0f * yawRad;
        return new Block
        {
            Seat = new Vector3(x + jx, (c + 0.5f) * PitchY - Footing, depth + jz),
            Half = HalfBrick(seed, id, 14, r),
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

    // --- fitting the cell ------------------------------------------------------

    /// <summary>Scale the wall so it sits inside its cell, and centre it first.
    ///
    /// One pass, where the Blender version needed two. Over there the footprint
    /// was not a closed form - the apron was scattered by rejection and the
    /// boulders were hulls of random points - so what the prop spanned had to be
    /// measured by building it. Here nothing about the layout depends on the
    /// scale, so the fit is exactly <c>coverage / reach</c>.
    ///
    /// Centred before fitting, and that is not cosmetic: the layout is
    /// deliberately lopsided, so an off-centre prop touches one edge and wastes
    /// the clearance at the other. On the Blender build that was 0.778 against
    /// 0.819 of the cell.</summary>
    public static float Fit(Plan plan, float coverage = 0.94f)
    {
        var lo = new Vector3(float.MaxValue, 0.0f, float.MaxValue);
        var hi = new Vector3(float.MinValue, 0.0f, float.MinValue);
        foreach (Block b in plan.Blocks)
        foreach (Vector3 c in Corners(b))
        {
            lo.X = Mathf.Min(lo.X, c.X); lo.Z = Mathf.Min(lo.Z, c.Z);
            hi.X = Mathf.Max(hi.X, c.X); hi.Z = Mathf.Max(hi.Z, c.Z);
        }
        var mid = new Vector3((lo.X + hi.X) * 0.5f, 0.0f, (lo.Z + hi.Z) * 0.5f);
        for (int i = 0; i < plan.Blocks.Count; i++)
        {
            Block b = plan.Blocks[i];
            b.Seat -= mid;
            plan.Blocks[i] = b;
        }

        // **A ceiling, never a target.** The layout is already stated in cell
        // fractions, so its natural size is the size that was asked for; scaling
        // it *up* to fill the coverage means the cell decides how big a wall is,
        // and a narrower apron then makes a taller wall. Measured: trimming the
        // rubble band moved the fit from 0.87 to 1.11 and pushed the crest out of
        // frame, which is a wall that grew because its debris shrank.
        float reach = Reach(plan.Blocks);
        float scale = Mathf.Min(1.0f, reach > 1e-4f ? coverage / reach : 1.0f);
        if (Mathf.Abs(scale - 1.0f) > 1e-6f)
            for (int i = 0; i < plan.Blocks.Count; i++)
            {
                Block b = plan.Blocks[i];
                b.Seat *= scale;
                b.Half *= scale;
                plan.Blocks[i] = b;
            }
        Measure(plan);
        return scale;
    }
}
