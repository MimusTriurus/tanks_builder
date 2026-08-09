using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The forest: trees sown across the board and sorted in among the tanks.
///
/// <b>Sown globally, harvested per cell.</b> Candidates are the nodes of a
/// jittered lattice laid over the whole board in ground space, and a tree
/// belongs to the cell its <i>foot</i> lands in. Generating inside each hexagon
/// instead would let two neighbours plant two trees a pixel apart across their
/// shared edge, and no per-cell rule can see that. This way the spacing holds
/// across every border for free, and the forest has no seam at the cell
/// boundary - which is the thing that would give the whole scheme away.
///
/// <b>Hashed, not random.</b> The same argument as
/// <see cref="TerrainSet.MixedAt"/>: --capture and --trace fix the time step so
/// two runs can be diffed, and a board that reshuffled itself would be
/// measuring itself. It also means no map is stored to have one.
///
/// <b>Ground space, not screen space.</b> The lattice is square on the ground,
/// which the isometry squashes to sin(elevation) vertically - so spacing is
/// even on the ground rather than even on screen, and the keep-out is a circle
/// rather than something that has to be an ellipse in two places.
///
/// <b>Nothing grows where a tank stands.</b> The keep-out is the contact patch
/// swept over every heading: belt length and gauge give a radius of about 101
/// ground px at the widest class, which is over half a cell. That is not a
/// tuning constant, it is the measured fact that makes a forest hex what it is -
/// a clearing with trees round its rim, which every tank can drive into.
///
/// It also removes the "middle layer the tank replaces" case entirely: there is
/// no middle to replace, because nothing was planted there.
///
/// <b>And a wooded cell is wooded, so it gets at least
/// <see cref="Minimum"/> trees</b> however the edge fade rolls. The fade
/// chooses among legal spots; it does not get to decide that a forest cell is a
/// field.
///
/// <b>Depth is the foot, by the same rule as the tanks.</b> Each tree is its
/// own node at <c>ZIndex = round(footY)</c> in field-local pixels, which is
/// exactly <see cref="Main"/>'s depth for a vehicle. So a tree in front of a
/// tank draws over it and a tree behind it does not, on its own cell and on
/// every neighbour, with no bands and no special case.
/// </summary>
public sealed partial class Grove : Node2D
{
    /// <summary>
    /// Ground px between lattice nodes before jitter.
    ///
    /// Thirty rather than something rounder because the band a tree may stand
    /// in is thin and measured: the keep-out ends at 83 and the hexagon's near
    /// edge is 107 away, so a tree that needs 11 of clearance has 13 ground px
    /// of room in the narrow direction. At 46 the emptiest wooded cell came out
    /// with one tree on it and the minimum could not fill it, because the
    /// minimum draws from candidates and there were none to draw.
    /// </summary>
    public double Spacing = 30.0;

    /// <summary>How far a node may wander from its lattice point, as a fraction
    /// of the spacing. Under 0.5 by construction: at 0.5 two neighbours can
    /// meet, and the lattice stops guaranteeing any spacing at all.</summary>
    public double Jitter = 0.38;

    /// <summary>Ground radius nothing may grow inside, centred on the cell.
    /// Set from the tanks - see the class remarks - rather than chosen.</summary>
    public double KeepOut = 83.0;

    /// <summary>Ground px a base must stand clear of the cell's border, on top
    /// of its own measured spread. The drawn seam on the plate measures six
    /// image pixels at x4 - a pixel and a half on screen - and this is that plus
    /// enough that the line is not touched rather than merely not crossed.
    /// </summary>
    public double Clearance = 3.0;

    /// <summary>How far the tallest tank reaches below its own contact point, in
    /// screen px. The clearance rule's constant, and it is a measurement: the
    /// belts hang in front of the axis, so a tank occupies ground it is not
    /// standing on.</summary>
    public double TankBelow = 31.0;

    /// <summary>
    /// Trees a wooded cell gets whatever the edge fade thinks.
    ///
    /// Four, because three read as a cell with some trees on it and four read as
    /// woods - and because the fade is a preference among legal spots rather
    /// than a rule about them, so overriding it costs nothing that matters. It
    /// cannot override the keep-out or the border: those two are what make the
    /// cell drivable and the tree one cell's tree, and a minimum that could
    /// break them would be a minimum that breaks the hex.
    /// </summary>
    public int Minimum = 4;

    /// <summary>
    /// Trees a wooded cell may have at most, or 0 for no ceiling.
    ///
    /// The other end of the same statement, and it trims by the same measure the
    /// fade prefers by: what goes first is what stood nearest open ground. So
    /// thinning a wood pulls it back from its edges rather than punching holes
    /// in it, which is the difference between a thinner wood and a patchy one.
    /// </summary>
    public int Maximum = 12;

    /// <summary>
    /// What a tree fades to while a tank is on its cell.
    ///
    /// Fading rather than hiding: the tank has to be visible through the wood,
    /// but a wood that vanishes is a tank standing in a field. Which of those
    /// two failures is nearer is what the dial is for - the setting is judged by
    /// looking, and both ends of it are wrong in different ways.
    ///
    /// The tuned value is named rather than left in the field's initialiser, the
    /// argument <see cref="Recoil.ShearOnByDefault"/> makes: a check that
    /// asserts the live value would fail on a session that dragged the slider,
    /// and one that asserts nothing would not notice the default going to zero.
    /// </summary>
    public const float GhostByDefault = 0.35f;

    public float Ghost = GhostByDefault;

    /// <summary>How long the fade takes. Short enough to keep up with a tank
    /// crossing a cell, long enough not to read as a switch.</summary>
    public double FadeTime = 0.18;

    /// <summary>Refuse to plant anything that would cross a tank standing on its
    /// own cell. Off by default, and that is the honest default: a forest that
    /// cannot hide a tank is not a forest, and the clearing already keeps the
    /// tank's own footprint clear. On, it is the A/B that shows what the rule
    /// costs - and what it costs is the whole front of every cell, because a
    /// 111px tree needs 142px of clearance and a hexagon offers 54.</summary>
    public bool ClearFront;

    public bool Enabled = true;

    public HexField? Field;
    public PropSet? Props;

    /// <summary>Where the field is, so a tree's node lands in the same space a
    /// tank's does. Its depth is still measured from the field's own origin,
    /// which is what makes the two comparable.</summary>
    public Vector2 Origin;

    private readonly List<PropNode> _planted = new();

    public int Planted => _planted.Count;

    /// <summary>Every planted tree, for the checks. Ground point is field-local,
    /// which is the space the depth rule is written in.</summary>
    public IReadOnlyList<PropNode> Trees => _planted;

    /// <summary>Whether a cell carries trees. Asked of the field rather than
    /// hashed here: forest is a kind of ground, chosen where the other kinds are
    /// chosen, so "how much forest" is the terrain paint and not a second dial
    /// arguing with it.</summary>
    public bool IsForest(Vector2I cell) =>
        Enabled && Field is not null
        && Field.KindAt(cell) == TerrainSet.Forest;

    /// <summary>Ground-plane squash, measured off the rendered tile rather than
    /// taken from the elevation: a flat-top hexagon is (sqrt(3)/2)*sin(e) as
    /// tall as it is wide, so the tile carries the camera and there is no second
    /// copy of the angle to keep in agreement.</summary>
    public float Squash
    {
        get
        {
            if (Field?.Atlas is null)
                return 0.5f;
            Vector2I hex = Field.Atlas.HexRect.Size;
            return hex.X <= 0 ? 0.5f : 2.0f * hex.Y / (Mathf.Sqrt(3.0f) * hex.X);
        }
    }

    /// <summary>Image px to screen px for a prop.</summary>
    public float DrawScale
    {
        get
        {
            if (Field?.Atlas is null)
                return 1.0f / PropSet.Detail;
            float plate = Field.Terrain is { Any: true }
                ? Field.Terrain.ScaleTo(Field.Atlas.HexRect)
                : 1.0f;
            return plate / PropSet.Detail;
        }
    }

    /// <summary>What grew, for the log. The counts by kind rather than the
    /// total, because the total cannot say that a prop is too wide for every
    /// legal spot on the board - which is how a prop goes missing now that the
    /// spot picks among the trees that fit it.</summary>
    public string Note()
    {
        if (Props is null || !Props.Any)
            return "no tree art";
        var grown = new int[Props.Count];
        foreach (PropNode tree in _planted)
            grown[tree.Species]++;
        return $"{_planted.Count} trees, keep-out {KeepOut:F0}px, "
               + $"clearance {Clearance:F0}px: "
               + string.Join(", ", Enumerable.Range(0, Props.Count).Select(
                   k => $"{Props.NameOf(k)} {grown[k]}"
                        + $" ({Props.RiseOf(k) * DrawScale:F0}px tall,"
                        + $" base {Props.RootOf(k) * DrawScale:F1})"));
    }

    public void Clear()
    {
        foreach (PropNode node in _planted)
        {
            RemoveChild(node);
            node.QueueFree();
        }
        _planted.Clear();
    }

    /// <summary>
    /// Sow the board. Cheap enough to call on any change - a 9x6 board walks
    /// about a thousand lattice nodes - and it has to be, because coverage and
    /// the clearance rule are both things you judge by looking.
    /// </summary>
    public void Plant()
    {
        Clear();
        if (!Enabled || Field?.Atlas is null || Props is null || !Props.Any)
            return;

        float squash = Squash;
        float scale = DrawScale;

        for (int q = 0; q < Field.Columns; q++)
        for (int r = 0; r < Field.Rows; r++)
        {
            var cell = new Vector2I(q, r);
            if (!IsForest(cell))
                continue;
            SowCell(cell, squash, scale);
        }

        // Depth decides overlap, so the tree list is kept in that order too. Not
        // for correctness - ZIndex already settles it - but so that a tie
        // between two trees at the same foot row resolves back to front rather
        // than to whichever cell was walked first.
        _planted.Sort((a, b) => a.Ground.Y.CompareTo(b.Ground.Y));
        foreach (PropNode node in _planted)
            MoveChild(node, _planted.Count - 1);
    }

    /// <summary>One candidate that survived every rule that cannot bend, held
    /// with the roll that decides whether it takes.</summary>
    private readonly record struct Seedling(
        Vector2 At, int Species, bool Mirrored, double Roll, double Fade);

    private void SowCell(Vector2I cell, float squash, float scale)
    {
        Vector2 centre = Field!.CellCentre(cell);
        Vector2 ground = new(centre.X, centre.Y / squash);
        Vector2I hex = Field.Atlas!.HexRect.Size;
        double halfX = hex.X * 0.5;
        double halfY = hex.Y * 0.5 / squash;

        List<Seedling> Gather(double step, int salt)
        {
            var found = new List<Seedling>();
            int i0 = (int)Math.Floor((ground.X - halfX) / step) - 1;
            int i1 = (int)Math.Ceiling((ground.X + halfX) / step) + 1;
            int j0 = (int)Math.Floor((ground.Y - halfY) / step) - 1;
            int j1 = (int)Math.Ceiling((ground.Y + halfY) / step) + 1;

            for (int i = i0; i <= i1; i++)
            for (int j = j0; j <= j1; j++)
            {
                var seed = new Vector2(
                    (float)((i + (Hash01(i, j, 1013 + salt) - 0.5) * 2.0 * Jitter) * step),
                    (float)((j + (Hash01(i, j, 2027 + salt) - 0.5) * 2.0 * Jitter) * step));
                var at = new Vector2(seed.X, seed.Y * squash);

                // The foot decides the cell, so a node near a border is planted
                // by whichever cell it actually stands in - once, by that cell.
                if (Field.CellAt(at) != cell)
                    continue;

                if (seed.DistanceTo(ground) < KeepOut)
                    continue;

                // The spot says which trees can stand in it, and only then does
                // the hash say which of those does. The other order - hash a
                // species, then refuse the spot if it does not fit - throws away
                // a place a smaller tree could have taken, and quietly thins the
                // big ones twice over: once by refusing them and once by not
                // planting anything in their place. Measured on the four trees
                // in hand: the base wants 11.3px of room at the smallest and
                // 18.3 at the largest, against a ring 24 deep at its narrowest.
                double room = EdgeRoom(cell, seed);
                int fits = 0;
                for (int k = 0; k < Props!.Count; k++)
                    if (Props.RootOf(k) * scale + Clearance <= room)
                        fits++;
                if (fits == 0)
                    continue;

                int wanted = (int)(Hash01(i, j, 4051 + salt) * fits);
                int species = 0;
                for (int k = 0; k < Props.Count; k++)
                {
                    if (Props.RootOf(k) * scale + Clearance > room)
                        continue;
                    if (wanted-- == 0)
                    {
                        species = k;
                        break;
                    }
                }
                bool mirrored = Hash01(i, j, 5077 + salt) < 0.5;

                if (ClearFront)
                {
                    double d = at.Y - centre.Y;
                    if (d > 0.0 && Props.RiseOf(species) * scale >= d - TankBelow)
                        continue;
                }

                found.Add(new Seedling(at, species, mirrored,
                                       Hash01(i, j, 3037 + salt),
                                       EdgeFade(cell, seed, squash)));
            }
            return found;
        }

        var pool = Gather(Spacing, 0);

        // The edge fade is a preference, not a rule, and this is where the
        // difference shows. Everything above rejects outright: a tree inside the
        // keep-out or across a border is wrong wherever it stands. The fade only
        // says which of the legal spots is the better one - so when it has taken
        // too many, the best of what it refused comes back rather than the cell
        // coming out as a field with four trees short of being woods.
        var taking = pool.Where(s => s.Roll < s.Fade).ToList();
        if (taking.Count < Minimum)
            taking.AddRange(pool.Where(s => s.Roll >= s.Fade)
                                .OrderByDescending(s => s.Fade)
                                .ThenBy(s => s.At.Y).ThenBy(s => s.At.X)
                                .Take(Minimum - taking.Count));

        // And if the whole cell had fewer legal spots than the floor asks for -
        // which happens, the ring is thin - go over it again at half the step.
        // The minimum draws from candidates, so a floor that cannot be filled
        // is not a floor at all: it was a wish, and a wooded cell came out with
        // two trees on it while the setting said four.
        //
        // Only where it is short, so the spacing everywhere else is the spacing
        // that was set - and only ever finer, so nothing that was legal at the
        // coarse step stops being legal.
        if (taking.Count < Minimum)
        {
            var seen = new HashSet<Vector2>(taking.Select(s => s.At));
            taking.AddRange(Gather(Spacing * 0.5, 617)
                            .Where(s => !seen.Contains(s.At)
                                        && taking.All(t => t.At.DistanceTo(s.At)
                                                           > Spacing * 0.25))
                            .OrderByDescending(s => s.Fade)
                            .ThenBy(s => s.At.Y).ThenBy(s => s.At.X)
                            .Take(Minimum - taking.Count));
        }
        if (Maximum > 0 && taking.Count > Maximum)
            taking = taking.OrderByDescending(s => s.Fade)
                           .ThenBy(s => s.At.Y).ThenBy(s => s.At.X)
                           .Take(Maximum).ToList();

        foreach (Seedling s in taking)
        {
            var node = new PropNode
            {
                Art = Props!.ArtOf(s.Species, s.Mirrored),
                Foot = Props.FootOf(s.Species, s.Mirrored),
                Size = Props.SizeOf(s.Species),
                RiseImage = Props.RiseOf(s.Species),
                Pixels = scale,
                Ground = s.At,
                Species = s.Species,
                Mirrored = s.Mirrored,
                Cell = cell,
                Position = Origin + s.At,
                ZIndex = Mathf.RoundToInt(s.At.Y),
                // Hashed off where the tree stands rather than off its place in
                // any list, so the wood sways the same way twice and a tree
                // keeps its phase when the sowing around it changes. Its own
                // salts, so phase is not a function of species or jitter -
                // shared bits there would put rows of trees in step.
                SwayPhase = Hash01(Mathf.RoundToInt(s.At.X),
                                   Mathf.RoundToInt(s.At.Y), 7919) * Math.Tau,
                SwayRate = 0.8 + 0.5 * Hash01(Mathf.RoundToInt(s.At.X),
                                              Mathf.RoundToInt(s.At.Y), 8837),
            };
            AddChild(node);
            _planted.Add(node);
        }
    }

    /// <summary>
    /// How much room a spot has before the cell's border, in ground px - so a
    /// prop stands there only if its base plus <see cref="Clearance"/> fits.
    ///
    /// The first version of this asked <see cref="HexField.CellAt"/> about the
    /// base's two horizontal extremes, which was right about what it tested and
    /// blind to the direction that actually fails: a trunk a pixel above the
    /// bottom edge passes a horizontal probe and is drawn standing on the seam.
    /// So the base is a disc on the ground and the question is its distance to
    /// the boundary.
    ///
    /// A disc of the contact's own half-width, measured about the point the
    /// prop actually stands on - see <see cref="PropSet"/>, where the foot moved
    /// to the middle of the contact for the same reason this is a radius: a
    /// border runs in any direction, so the extent has to answer in any
    /// direction, and depth cannot be measured from a flat sprite at all.
    ///
    ///
    /// Analytic rather than probing, because "how far inside" has no answer in
    /// terms of which cell a point is in. For a flat-top hexagon of circumradius
    /// R the two horizontal edges sit at |dy| = sqrt(3)R/2 and the four slanted
    /// ones at sqrt(3)|dx| + |dy| = sqrt(3)R, whose normal has length 2 - so the
    /// distances are the two expressions below and the boundary is the nearer.
    /// </summary>
    public double EdgeRoom(Vector2I cell, Vector2 ground)
    {
        Vector2 centre = Field!.CellCentre(cell);
        double r = Field.Atlas!.HexRect.Size.X * 0.5;
        double dx = Math.Abs(ground.X - centre.X);
        double dy = Math.Abs(ground.Y - centre.Y / Squash);
        double root3 = Math.Sqrt(3.0);
        return Math.Min(root3 * r * 0.5 - dy,
                        (root3 * r - root3 * dx - dy) * 0.5);
    }

    /// <summary>
    /// Bend the trees, and move the gust along.
    ///
    /// A board where nothing moves reads as a photograph of a board, which is
    /// the same complaint the engine tremble answers on the tanks: the trees
    /// are the largest still thing on screen and the cheapest to give a pulse.
    ///
    /// <b>A shear about the foot, not a rotation and not a wobble.</b> The
    /// trunk is planted; what a tree does in wind is lean, so the drift of a
    /// point is proportional to its height above the ground - and that is a
    /// shear, which is the same reasoning <see cref="BodyPitch"/> uses for
    /// pitch on a sprite that has already been projected. Linear rather than
    /// bending harder near the crown, because an affine transform is what a
    /// single draw call has: a real bend would mean slicing each tree, and 464
    /// trees is not the place to spend that.
    ///
    /// <b>Out of step by construction.</b> Each tree carries a phase hashed off
    /// its own foot and a rate a little either side of the wind's, so no two
    /// agree for long. That alone would read as every tree fidgeting on its
    /// own, so the amplitude is a gust that travels: one wave crossing the
    /// board along x, which is what makes it read as weather rather than as
    /// idle animation.
    ///
    /// <b>Integrated, not clocked off the wall.</b> The same argument as the
    /// tremble and the exhaust: --capture and --trace fix the time step so two
    /// runs can be diffed, and a phase taken from the wall clock would differ
    /// between them.
    /// </summary>
    public void Blow(double delta)
    {
        if (_planted.Count == 0)
            return;
        Weather += delta;
        // The fronts move and deliver first, so a tree kicked this frame is
        // integrated this frame rather than next: at 700px/s a frame is twelve
        // ground pixels, which is a whole rank of trees getting their shove one
        // frame late for nothing.
        Rush(delta);
        foreach (PropNode tree in _planted)
        {
            float lean = 0.0f;
            if (Wind > 0.0)
            {
                // The gust: one long wave travelling along x, so neighbours
                // rise and fall a beat apart rather than together.
                double gust = 0.5 + 0.5 * Math.Sin(
                    Weather * GustRate - tree.Ground.X * GustTravel);
                double phase = Weather * tree.SwayRate + tree.SwayPhase;
                lean = (float)(Wind * (0.35 + 0.65 * gust) * Math.Sin(phase)
                               / SwayHeight);
            }
            // The flinch rings down on its own spring and adds to the wind,
            // rather than overriding it: a gun going off does not stop the
            // weather, and a tree that snapped to the blast and back would be
            // showing the wind switching off for a second and a half.
            //
            // Integrated here rather than in Rush so that Lean has exactly one
            // writer. Two of them - one for weather, one for blast - is how a
            // pose ends up depending on which ran last, and it would show up as
            // the wood stalling for the length of a shot.
            if (tree.Flinch != 0.0f || tree.FlinchRate != 0.0f)
            {
                tree.FlinchRate += (float)((-BlastStiffness * tree.Flinch
                                            - BlastDamping * tree.FlinchRate)
                                           * delta);
                tree.Flinch += (float)(tree.FlinchRate * delta);
                // Landed: under a hundredth of a pixel of crown at any sane
                // height, so going on integrating it only keeps the tree in the
                // redraw list for ever.
                if (Math.Abs(tree.Flinch) < 1e-5f && Math.Abs(tree.FlinchRate) < 1e-4f)
                {
                    tree.Flinch = 0.0f;
                    tree.FlinchRate = 0.0f;
                }
                lean += tree.Flinch;
            }
            // The lean is written every frame and the redraw is not: a tree
            // whose crown has moved a thousandth of a pixel since it was last
            // drawn does not need drawing again. Kept apart rather than folded
            // into one test, because folding them leaves Lean holding the last
            // value that happened to cross the threshold - so it stops being
            // where the tree is and starts being where it was, and every
            // measurement of the wind is then off by the threshold.
            tree.Lean = lean;
            if (Math.Abs(lean - tree.Drawn) < 0.00002f)
                continue;
            tree.Drawn = lean;
            tree.QueueRedraw();
        }
    }

    /// <summary>How far the crown of a tree this tall drifts at full gust, in
    /// screen px. Written as a drift at a height rather than as a shear so that
    /// the setting is in the units it is judged in - and the shear that comes
    /// out of it is what makes a taller tree lean further, which is the part
    /// that has to be true rather than tuned.</summary>
    public double Wind = 2.6;
    public double SwayHeight = 120.0;

    /// <summary>How fast the gust crosses, and how far apart in phase two
    /// points a pixel apart on the board are. 0.55 rad/s is a wave every
    /// eleven seconds; the travel puts a full cycle across about four cells,
    /// so a gust is visibly moving rather than the whole board breathing.
    /// </summary>
    public double GustRate = 0.55;
    public double GustTravel = 0.0035;

    /// <summary>How long the wind has been blowing, in seconds. Public because
    /// it is the weather's state rather than a private counter: everything the
    /// trees do is a function of it and their own hashed phase, so it is what
    /// says whether two runs are looking at the same gust.</summary>
    public double Weather;

    // ---- the blast wave -------------------------------------------------

    /// <summary>
    /// A multiplier over the per-event strengths, and the one number the panel
    /// puts on a slider.
    ///
    /// On by default, where <see cref="CameraShake.OnByDefault"/> is off, and
    /// the difference is exactly that constant's argument: the shake moves the
    /// whole frame, so an A/B taken near a shot measures the shake instead of
    /// what it was aimed at. This moves trees, on wooded cells, and nothing
    /// else - a diff of a tank is untouched by it.
    /// </summary>
    public double Blast = 1.0;

    /// <summary>How far the wave carries, in ground px. About a cell and a half
    /// - the hexagon's circumradius is 247, so the blast reaches the trees on
    /// the cell it happened on and thins out over the ring of neighbours. Far
    /// enough that a wood answers a gun going off in it, near enough that a shot
    /// at one end of the board leaves the other end alone.</summary>
    public double BlastReach = 320.0;

    /// <summary>
    /// How fast the front travels, in ground px per second.
    ///
    /// <b>This is what keeps the wood out of step, and it is the honest reason
    /// rather than a dressed-up one.</b> The wind is desynchronised by hashing a
    /// phase per tree, because there is no event to be at a distance from. A
    /// blast has one, so the trees near it flinch first and the far ones a
    /// quarter of a second later, which is what a wave is - and it costs no
    /// per-tree state at all.
    ///
    /// Slow: a real blast wave is supersonic and would arrive everywhere on the
    /// same frame, which is exactly the picture this is written to avoid. 700
    /// puts a third of a second between the near trees and the far ones, which
    /// is the reading, and reading is what the number is for.
    /// </summary>
    public double BlastSpeed = 700.0;

    /// <summary>About 3.2Hz at a seventh of critical - slower and springier than
    /// <see cref="CameraShake"/>'s 8Hz, because a tree is a big slow thing and a
    /// camera is not. Three or four visible rebounds over a second and a half.
    /// </summary>
    public double BlastStiffness = 400.0;
    public double BlastDamping = 6.0;

    /// <summary>The gun's blast, as a multiple of the class's own
    /// <see cref="MovementProfile.ShotShake"/>. Reused rather than given its own
    /// three numbers: that field is already the measured statement that a heavy
    /// gun goes off harder than a light one, and a second table by the same key
    /// is the thing this project keeps refusing to write. 2.4 to 5.6px across
    /// the classes, which is under a hit on purpose - see below.</summary>
    public double ShotBlast = 0.8;

    /// <summary>
    /// A round bursting on armour, in crown px at the source.
    ///
    /// One number rather than per class, because the shell is the same shell
    /// whoever fired it - and the key-pressed hit has no shooter to ask.
    ///
    /// Above even the heaviest gun's, which is a claim rather than a spare
    /// pixel: a gun's blast is directed down the bore and most of it leaves,
    /// where a round breaking up against a plate is a burst going out in every
    /// direction at the tree line. The first numbers had the heavy's gun over a
    /// hit and the ordering check caught it.
    /// </summary>
    public double HitBlast = 7.0;

    /// <summary>A tank going up. The largest of the three by some way: it is the
    /// one event of the three that is the machine itself letting go, and if it
    /// did not out-shove its own gun the wood would be saying that dying is a
    /// smaller thing than shooting.</summary>
    public double DeathBlast = 16.0;

    /// <summary>One front still travelling. A class rather than a struct because
    /// the front advances in place; there are at most a handful of these.
    /// </summary>
    private sealed class Wave
    {
        public Vector2 Ground;     // ground space, field-local
        public double Pixels;      // crown drift at the source
        public double Front;       // ground px the front has reached
    }

    private readonly List<Wave> _waves = new();

    /// <summary>Fronts still crossing the board. For the trace: a blast that
    /// delivered nothing and one that was never fired are the same still
    /// picture.</summary>
    public int Waves => _waves.Count;

    /// <summary>
    /// The peak lean a unit impulse produces, run through the same integrator at
    /// the same step rather than solved on paper.
    ///
    /// <see cref="Recoil.PeakFor"/>'s lesson, restated by
    /// <see cref="CameraShake.UnitPeak"/>: the closed form and a 1/60
    /// semi-implicit Euler step disagree by enough that a caption quoting the
    /// analytic figure would be describing a different effect. Dividing by it is
    /// what lets <see cref="Shock"/> be asked for crown pixels and deliver crown
    /// pixels.
    /// </summary>
    public double UnitKick
    {
        get
        {
            const double step = 1.0 / 60.0;
            double rate = 1.0, x = 0.0, peak = 0.0;
            for (int i = 0; i < 240; i++)
            {
                rate += (-BlastStiffness * x - BlastDamping * rate) * step;
                x += rate * step;
                peak = Math.Max(peak, Math.Abs(x));
            }
            return peak;
        }
    }

    /// <summary>
    /// Something went off at <paramref name="at"/> - field-local screen px, the
    /// space a tank's contact point and a tree's foot are both in - hard enough
    /// to move a crown at the source by <paramref name="pixels"/>.
    ///
    /// Queued as a front rather than applied here, because the whole of the
    /// effect is that it arrives at different trees at different times. Added to
    /// whatever is already crossing rather than replacing it, the argument
    /// <see cref="CameraShake.Fire"/> makes: three tanks in an engagement is the
    /// case, and a wave that cancelled the one before it would read as the first
    /// shot being undone by the second.
    /// </summary>
    public void Shock(Vector2 at, double pixels)
    {
        if (!Enabled || Blast <= 0.0 || pixels <= 0.0 || _planted.Count == 0)
            return;
        float squash = Squash;
        _waves.Add(new Wave
        {
            Ground = new Vector2(at.X, at.Y / squash),
            Pixels = pixels * Blast,
            Front = 0.0,
        });
    }

    /// <summary>Put the wood back at rest: no fronts crossing and nothing
    /// leaning. For the reset, by the argument that resets the recoil spring -
    /// the flinch is the one thing here that holds a pose with nothing driving
    /// it, so leaving it would leave the board bent.</summary>
    public void Calm()
    {
        _waves.Clear();
        foreach (PropNode tree in _planted)
        {
            tree.Flinch = 0.0f;
            tree.FlinchRate = 0.0f;
        }
    }

    /// <summary>
    /// Advance every front and hand its kick to the trees it passed this frame.
    ///
    /// <b>The direction is the horizontal share of a radial shove, and the trees
    /// it barely moves are right to barely move.</b> A blast pushes a tree away
    /// from it along the ground; a shear can only draw the part of that which
    /// runs across the screen, so a tree straight up-board of the burst is being
    /// pushed away from the camera and leans almost not at all. That is the same
    /// projection that gives <see cref="CameraShake"/> four pixels broadside
    /// against two into the screen, and faking it with a floor would be drawing
    /// a lean the geometry does not have.
    ///
    /// The falloff runs linearly to nothing at <see cref="BlastReach"/> rather
    /// than stopping at it: a hard edge is a line of trees that flinch beside a
    /// line that does not, and on a lattice this dense that line is visible.
    /// </summary>
    private void Rush(double delta)
    {
        if (_waves.Count == 0)
            return;
        float squash = Squash;
        double unit = UnitKick;
        for (int w = _waves.Count - 1; w >= 0; w--)
        {
            Wave wave = _waves[w];
            double was = wave.Front;
            wave.Front += BlastSpeed * delta;
            foreach (PropNode tree in _planted)
            {
                var g = new Vector2(tree.Ground.X, tree.Ground.Y / squash);
                double d = g.DistanceTo(wave.Ground);
                // Passed this frame, and once: the front only ever advances, so
                // a tree is kicked by a given wave exactly when the band between
                // last frame's radius and this one's contains it. No per-tree
                // bookkeeping, and none that could go stale.
                if (d < was || d >= wave.Front || d >= BlastReach)
                    continue;
                double share = d <= 0.001 ? 0.0 : (g.X - wave.Ground.X) / d;
                double want = wave.Pixels * (1.0 - d / BlastReach) * share;
                if (unit > 0.0)
                    tree.FlinchRate += (float)(want / (SwayHeight * unit));
            }
            if (wave.Front >= BlastReach)
                _waves.RemoveAt(w);
        }
    }

    /// <summary>
    /// Fade the trees on cells that have a tank in them, and let the rest come
    /// back.
    ///
    /// A tank in the woods has to be visible, and every other way of arranging
    /// that is worse: drawing it over the trees breaks the depth rule that makes
    /// the forest read as ground at all, and hiding the trees leaves a tank
    /// standing in a field. Fading keeps the cell wooded and the tank readable,
    /// and it is the one place where the trees stop being ground and admit to
    /// being a view of it.
    ///
    /// Per cell rather than per tree, and every tree on the cell rather than the
    /// ones actually overlapping: which trees cross the tank changes with the
    /// hull's heading, so a per-tree test would have trees blinking as the tank
    /// turned on the spot.
    /// </summary>
    public void Reveal(IReadOnlySet<Vector2I> occupied, double delta)
    {
        if (_planted.Count == 0)
            return;
        // Framewise rather than a lerp on a stored target: the same integration
        // the rest of the bench uses, so a paused frame does not step the fade.
        double k = FadeTime <= 0.0 ? 1.0
                 : 1.0 - Math.Exp(-delta / FadeTime);
        foreach (PropNode tree in _planted)
        {
            float want = occupied.Contains(tree.Cell) ? Ghost : 1.0f;
            float now = tree.Modulate.A;
            float next = (float)(now + (want - now) * k);
            if (Math.Abs(next - now) < 0.001f)
                next = want;
            if (next == now)
                continue;
            tree.Modulate = new Color(1.0f, 1.0f, 1.0f, next);
        }
    }

    /// <summary>
    /// How likely a tree is to take at this point, from how close it stands to
    /// open ground.
    ///
    /// Without it the forest ends on the hexagon's edge, and a straight edge is
    /// a drawn edge: the boundary reads as the grid rather than as woodland.
    /// Measured against the neighbouring cell's centre rather than the border
    /// itself, because the six neighbours of a flat-top hex all sit the same
    /// 214 ground px away, so one distance covers every direction.
    /// </summary>
    private double EdgeFade(Vector2I cell, Vector2 ground, float squash)
    {
        double nearest = double.MaxValue;
        foreach (int heading in HexField.EdgeHeadings)
        {
            Vector2I next = HexField.Step(cell, heading);
            if (next == cell)
                continue;
            if (Field!.InBounds(next) && IsForest(next))
                continue;                       // more forest, or off the board
            Vector2 c = Field.CellCentre(next);
            nearest = Math.Min(nearest, ground.DistanceTo(new Vector2(c.X, c.Y / squash)));
        }
        if (nearest == double.MaxValue)
            return 1.0;
        return Math.Clamp((nearest - 80.0) / 60.0, 0.0, 1.0);
    }

    /// <summary>A hash in [0,1) off two integers and a salt. Same shape as
    /// <see cref="TerrainSet.MixedAt"/>'s, and salted rather than advanced so
    /// that each decision about a node is independent of the others - jitter
    /// that shared bits with the species would tie a tree's kind to where it
    /// stands, which reads as rows of the same tree.</summary>
    public static double Hash01(int a, int b, int salt)
    {
        int h = a * 73856093 ^ b * 19349663 ^ salt * 83492791;
        h = (h ^ (h >> 13)) * 1274126177;
        h ^= h >> 16;
        return ((uint)h % 100000u) / 100000.0;
    }
}

/// <summary>
/// One standing thing. Its own node because its own <c>ZIndex</c> is the whole
/// point - it has to sort in among the tanks, and ZIndex is per CanvasItem.
/// </summary>
public sealed partial class PropNode : Node2D
{
    public Texture2D Art = null!;
    public Vector2 Foot;        // image px
    public Vector2 Size;        // image px
    public float Pixels = 1.0f;   // image px to screen px
    public Vector2 Ground;      // field-local screen px of the foot
    public int Species;
    public bool Mirrored;
    public Vector2I Cell;

    /// <summary>Height above the foot in image px, straight from the set.
    /// Carried rather than derived from <see cref="Size"/>, because the two are
    /// different numbers whenever the art has transparent margin under the
    /// trunk.</summary>
    public float RiseImage;

    /// <summary>Height above the foot, on screen. What the clearance rule and
    /// the checks are written in.</summary>
    public float Rise => RiseImage * Pixels;

    /// <summary>Where this tree is in the sway, and how fast it goes round.
    /// Hashed off its own foot at planting - see <see cref="Grove.Blow"/>.
    /// </summary>
    public double SwayPhase;
    public double SwayRate = 1.0;

    /// <summary>How far a point drifts per pixel of height above the foot. A
    /// shear rather than an angle: at these amplitudes the two are the same
    /// number, and the shear is what the draw call takes.
    ///
    /// The wind's lean plus the blast's, written once a frame by
    /// <see cref="Grove.Blow"/> and by nothing else.</summary>
    public float Lean;

    /// <summary>This tree's own blast spring: how far it is still shoved over,
    /// and how fast that is changing. Per tree rather than one spring for the
    /// wood, because the whole point is that the front reaches them at different
    /// moments - see <see cref="Grove.Rush"/>.</summary>
    public float Flinch;
    public float FlinchRate;

    /// <summary>The lean the sprite was last drawn at. See
    /// <see cref="Grove.Blow"/>: what is drawn lags what is true by less than a
    /// hundredth of a pixel, and that is the whole point of it.</summary>
    public float Drawn;

    /// <summary>Where this tree's crown is right now, in screen px - the number
    /// the setting is written in and the one a check can read.</summary>
    public float Drift => Lean * Rise;

    public override void _Ready()
    {
        // Painted at four times the size it is drawn at, so the same argument
        // as the ground: nearest minification point-samples, and a tree that
        // crawls as the board pans is worse than no tree.
        TextureFilter = TextureFilterEnum.LinearWithMipmaps;
    }

    /// <summary>
    /// The shear the sprite is drawn through.
    ///
    /// The node's origin is the foot, because the sprite is drawn at -Foot - so
    /// a shear with no translation in it pivots on the ground and there is
    /// nothing to line up. A point h above the foot sits at local y = -h, hence
    /// the minus: it has to drift the way the lean says, not against it.
    ///
    /// A property rather than a local, so a check can ask the matrix itself
    /// where the foot ends up. A shear on the wrong axis and an origin left in
    /// it both slide the whole tree, and both look like wind.
    /// </summary>
    public Transform2D Bend => new(new Vector2(1.0f, 0.0f),
                                   new Vector2(-Lean, 1.0f), Vector2.Zero);

    public override void _Draw()
    {
        if (Lean != 0.0f)
            DrawSetTransformMatrix(Bend);
        DrawTextureRect(Art, new Rect2(-Foot * Pixels, Size * Pixels), false);
    }
}
