using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// What stands on the board: trees, and whatever else is drawn beside them,
/// sown across it and sorted in among the tanks.
///
/// <b>Tiers, not one wood.</b> A prop plays by its tier's rules - see
/// <see cref="PropTier"/> - and the tiers are sown one after another, tallest
/// first, over the whole board rather than cell by cell. Whole board because
/// the order has to be global: sowing a cell's undergrowth before the next
/// cell's trees lets a trunk come down on a bush already standing, and nothing
/// planted later can see what was planted earlier if "earlier" means "in
/// another cell".
///
/// <b>Inside a tier the lattice is the spacing, and between tiers nothing is.</b>
/// That asymmetry is the whole of the collision rule. Two trees cannot meet
/// because the jittered lattice they both came off will not put them nearer
/// than <c>step*(1-2*jitter)</c>; a tree and a bush came off different lattices
/// with no relationship at all, so the only thing left to ask is whether their
/// measured bases overlap. So: no test within a tier - adding one would refuse
/// trees the lattice already allows, and every board rendered so far is those
/// trees - and a disc test against everything already standing, between them.
///
/// <b>How many of each is the terrain paint, not a second dial.</b>
/// <see cref="Growth"/> is a table by kind of ground, for
/// <see cref="IsForest"/>'s reason restated: "how much forest" is chosen where
/// the kinds are chosen, and a slider arguing with the paint is two answers to
/// one question.
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
/// own node at <c>ZIndex = round(</c><see cref="HexField.Depth"/><c>)</c> of its
/// foot, which is exactly <see cref="Main"/>'s depth for a vehicle. So a tree in
/// front of a tank draws over it and a tree behind it does not, on its own cell
/// and on every neighbour, with no bands and no special case.
///
/// That was the foot's screen row until the board had height in it, and the two
/// are still the same number wherever nothing is raised. Where something is, a
/// tree on a hill is drawn further up the screen and is not thereby further
/// away - read off the drawn row a wood on a rise sorts behind the whole plain
/// and disappears under the tanks in front of it.
/// </summary>
public sealed partial class Grove : Node2D
{
    /// <summary>
    /// Ground px between lattice nodes before jitter, for the tree tier.
    ///
    /// Thirty rather than something rounder because the band a tree may stand
    /// in is thin and measured: the keep-out ends at 83 and the hexagon's near
    /// edge is 107 away, so a tree that needs 11 of clearance has 13 ground px
    /// of room in the narrow direction. At 46 the emptiest wooded cell came out
    /// with one tree on it and the minimum could not fill it, because the
    /// minimum draws from candidates and there were none to draw.
    ///
    /// Every other tier carries its own in <see cref="PropTier.Step"/>. This one
    /// stays a field because it is the one that was tuned by looking and the one
    /// the panel and the checks reach for; <see cref="StepOf"/> is where the two
    /// meet, so there is one place that decides and not two.
    /// </summary>
    public double Spacing = 30.0;

    /// <summary>The lattice step a tier is sown on. The tree tier answers
    /// <see cref="Spacing"/> so that dragging that setting still moves the wood;
    /// everything else answers its own.</summary>
    public double StepOf(PropTier tier) =>
        tier.Name == PropTier.Trees ? Spacing : tier.Step;

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
    /// The tree tier's floor specifically, and it stays a field rather than
    /// moving into <see cref="Budget"/> because it is the one both the checks
    /// and any future slider reach for. <see cref="Growth"/> substitutes it in.
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

    /// <summary>Which cells are alight, if anything is. Handed in rather than
    /// owned, for the reason <see cref="Wildfire"/> is a class of its own: what
    /// burns is a cell, and the wood's business is which trees are standing on
    /// it. Null is a board where nothing can catch, which is every board drawn
    /// before this.</summary>
    public Wildfire? Fire;

    /// <summary>Where the field is, so a tree's node lands in the same space a
    /// tank's does. Its depth is still measured from the field's own origin,
    /// which is what makes the two comparable.</summary>
    public Vector2 Origin;

    /// <summary>
    /// Where a tier that gave up depth sorting draws - see
    /// <see cref="PropTier.UnderTanks"/> for why one does.
    ///
    /// A band rather than a depth, and that is the whole of it: everything that
    /// stands on the board sorts by its foot and lands at a positive
    /// <see cref="HexField.Depth"/>, so one number below all of them puts the
    /// dressing under every tank and every tree at once, with nothing to keep in
    /// step as either moves. Above the marks pressed into the ground
    /// (<see cref="TrackMarks.MarksZ"/> at -101, <see cref="SelectionRing.GroundZ"/>
    /// at -99, <see cref="TankSprite.ShadowZ"/> at -98), because a stone stands on
    /// the dirt those are painted into.
    ///
    /// Fifty rather than -97, so the gap either side is wide enough to read as a
    /// band and to put something else in later. The checks assert both edges of
    /// it rather than the number.
    ///
    /// <b>Within the band the props still settle each other by depth</b>, and
    /// for free: <see cref="Plant"/> leaves the planted list in depth order and
    /// moves the children to match, so siblings sharing a z-index fall back to
    /// tree order, which is that order.
    /// </summary>
    public const int DressZ = -50;

    private readonly List<PropNode> _planted = new();

    public int Planted => _planted.Count;

    /// <summary>Every planted prop, whatever tier, for the checks. Ground point
    /// is field-local, which is the space the depth rule is written in.
    ///
    /// Named for what it holds rather than for what it used to hold: this list
    /// carries rocks now, and a property called <c>Trees</c> that returns them
    /// is the kind of name a check reads past.</summary>
    public IReadOnlyList<PropNode> Standing => _planted;

    /// <summary>
    /// How many times the board has been sown, so somebody drawing this wood
    /// somewhere else knows when it is a different wood.
    ///
    /// A counter rather than the tree count, and that is the whole reason it
    /// exists: sowing runs on any slider drag, and the spacing, the fade and the
    /// species can all be re-rolled without the total moving a tree. Read off
    /// the count, <see cref="Stage3D"/> would carry on drawing billboards for
    /// trees that had been freed - which is the quiet half, because a freed node
    /// still answers where it stood.
    /// </summary>
    public int Sown { get; private set; }

    /// <summary>Whether a cell carries trees. Asked of the field rather than
    /// hashed here: forest is a kind of ground, chosen where the other kinds are
    /// chosen, so "how much forest" is the terrain paint and not a second dial
    /// arguing with it.</summary>
    /// <remarks><b>Never on a ramp</b>, whatever the paint says. A tree stands at
    /// one height, read off the cell's level - and a ramp's surface is at its
    /// level on one edge only, so a trunk planted on one sinks into the slope or
    /// floats over it depending which end it landed at. A ramp is the way up,
    /// which is the one cell on the board that has a reason to be clear.
    ///
    /// <b>And never in water</b>, for a reason that is not the ramp's. A ramp is
    /// refused because a trunk there has no one height to stand at; a flooded cell
    /// has one perfectly good height and the trees stand on it - which is the
    /// picture the first pond came back with, a wood growing out of a pond with the
    /// water drawn across its trunks. Reeds are a kind of ground somebody could
    /// draw; a mature wood is not, and the paint has no way to know the cell is
    /// under water because the water is not paint.</remarks>
    public bool IsForest(Vector2I cell) =>
        Grows(cell) && Field!.KindAt(cell) == TerrainSet.Forest;

    /// <summary>
    /// Whether a cell can carry anything standing at all.
    ///
    /// The two refusals in <see cref="IsForest"/>'s remarks are about a prop
    /// having one height to stand at and not about it being a tree, so they
    /// belong here, above the tiers: a boulder on a ramp sinks into the slope
    /// exactly the way a trunk does, and a bush in the pond is the wood growing
    /// out of the water again at knee height.
    /// </summary>
    public bool Grows(Vector2I cell) =>
        Enabled && Field is not null && !Field.IsRamp(cell) && !Field.IsWater(cell);

    /// <summary>The key in <see cref="Budget"/> for any drawn ground that is not
    /// named in it. A fallback rather than a wildcard to union in: a kind gets
    /// one row, so what it carries can be read off one line.</summary>
    public const string AnyGround = "*";

    /// <summary>
    /// What each kind of ground carries, by tier.
    ///
    /// <b>Entries for tiers nobody drew art for cost nothing</b> -
    /// <see cref="Growth"/> intersects this with what loaded - so the table can
    /// say what a forest floor is before there is a bush to put on it, and the
    /// day the folder appears the board answers without a code change. That is
    /// the same arrangement <c>classes.json</c> has and the opposite of the
    /// failure it guards: a row with no code behind it is a control that does
    /// nothing, and here the row <i>is</i> the code.
    ///
    /// <b>Floors are zero outside the wood, and that is a statement.</b> A
    /// forest cell is wooded or it is not a forest cell, so its trees have a
    /// floor; scatter on open ground is variety, and a floor on variety is a
    /// cell where four rocks were placed because four was written down. The
    /// ceiling is what does the work out there.
    ///
    /// <b>Grass is not in it</b>, for <see cref="PropTier"/>'s reason: it is
    /// paint. A folder of it would load and sit at nothing until somebody adds
    /// the row, which is the right amount of friction for a decision this
    /// document argues against.
    /// </summary>
    public readonly Dictionary<string, List<Sprouts>> Budget = new()
    {
        // The tree numbers here are placeholders that Growth overwrites from
        // Minimum and Maximum. Written out anyway rather than left implicit,
        // because a reader asking "what is on a forest cell" should not have to
        // know that one of the three rows comes from somewhere else.
        [TerrainSet.Forest] = new()
        {
            new Sprouts(PropTier.Trees, 4, 12),
            new Sprouts(PropTier.Bushes, 2, 6),
            new Sprouts(PropTier.Rocks, 0, 1),
        },
        // The bare template. Nothing: it is the frame every other kind is
        // aligned to, and scatter on it would be scatter on the thing the
        // scatter is measured against.
        [TerrainSet.Plain] = new(),
        [AnyGround] = new()
        {
            new Sprouts(PropTier.Bushes, 0, 2),
            new Sprouts(PropTier.Rocks, 0, 2),
        },
    };

    /// <summary>
    /// What this cell grows, in the order it has to be sown: tallest tier
    /// first, off <see cref="PropSet.Tiers"/>, which measured it.
    ///
    /// Skips tiers with no art, so a budget may name more than a board can
    /// grow and the note reports what actually stood up. Skips empty rows too -
    /// a ceiling of zero is a tier this ground does not carry, and walking it
    /// would cost a pass over the board to plant nothing.
    /// </summary>
    public IEnumerable<(PropTier Tier, int Least, int Most)> Growth(Vector2I cell)
    {
        if (Props is null || !Grows(cell) || Field is null)
            yield break;
        string kind = Field.KindAt(cell);
        if (!Budget.TryGetValue(kind, out List<Sprouts>? rows)
            && !Budget.TryGetValue(AnyGround, out rows))
            yield break;
        foreach (PropTier tier in Props.Tiers)
        {
            Sprouts? row = rows.FirstOrDefault(s => string.Equals(
                s.Tier, tier.Name, StringComparison.OrdinalIgnoreCase));
            if (row is null)
                continue;
            // The wood's two ends stay where they were tuned and where the
            // checks read them, whatever the table happens to say.
            int least = tier.Name == PropTier.Trees ? Minimum : row.Least;
            int most = tier.Name == PropTier.Trees ? Maximum : row.Most;
            if (least <= 0 && most == 0)
                continue;
            yield return (tier, least, most);
        }
    }

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

    /// <summary>How much of the plate's own scale survives to the screen. The
    /// half of a prop's scale that belongs to the board rather than to the art:
    /// what the terrain kind is painted at against the tile it is drawn on.
    /// </summary>
    private float Plate
    {
        get
        {
            if (Field?.Atlas is null)
                return 1.0f;
            return Field.Terrain is { Any: true }
                ? Field.Terrain.ScaleTo(Field.Atlas.HexRect)
                : 1.0f;
        }
    }

    /// <summary>
    /// Image px to screen px for one tier's art.
    ///
    /// Per tier and not per board, because the scale art is exported at is a
    /// property of the folder it came from - see <see cref="PropTier.Detail"/>.
    /// Trees at 8x and undergrowth at 4x is not an inconsistency to be tidied
    /// away: they are different documents, and the alternative is re-exporting
    /// art nobody asked to change.
    /// </summary>
    public float DrawScale(PropTier tier) => Plate / (float)tier.Detail;

    /// <summary>The same for one kind of prop, which is the form every readout
    /// and check wants: they hold a species index, and the tier is a lookup they
    /// would all have to remember to do.</summary>
    public float ScaleOf(int species) =>
        Props is null ? 1.0f : DrawScale(Props.TierOf(species));

    /// <summary>What grew, for the log. The counts by kind rather than the
    /// total, because the total cannot say that a prop is too wide for every
    /// legal spot on the board - which is how a prop goes missing now that the
    /// spot picks among the trees that fit it. Grouped by tier for the same
    /// reason one level up: a tier that planted nothing is art on disk that
    /// never reaches the board, and a total says it grew.</summary>
    public string Note()
    {
        if (Props is null || !Props.Any)
            return "no prop art";
        var grown = new int[Props.Count];
        foreach (PropNode prop in _planted)
            grown[prop.Species]++;
        return $"{_planted.Count} props, keep-out {KeepOut:F0}px, "
               + $"clearance {Clearance:F0}px: "
               + string.Join("; ", Props.Tiers.Select(t =>
                   $"{t.Name} x{Props.Species(t).Sum(k => grown[k])} "
                   + $"@{StepOf(t):F0}px /{t.Detail:F0} ["
                   + string.Join(", ", Props.Species(t).Select(
                       k => $"{Props.NameOf(k)} {grown[k]}"
                            + $" ({Props.RiseOf(k) * ScaleOf(k):F0}px tall,"
                            + $" base {Props.RootOf(k) * ScaleOf(k):F1})"))
                   + "]"));
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
        // Counted even when nothing is sown: "the wood was cleared" is a change
        // to the wood, and a stage that missed it would keep the last one
        // standing.
        Sown++;
        if (!Enabled || Field?.Atlas is null || Props is null || !Props.Any)
            return;

        float squash = Squash;
        _taken.Clear();

        // Tier by tier over the whole board, tallest first, and the loop nesting
        // is the rule rather than an implementation of it: the undergrowth tests
        // against what is already standing, so every tree on the board has to be
        // down before the first bush is. Cell-major - the obvious nesting - puts
        // one cell's bush under the next cell's trunk, and nothing planted later
        // can see it, because "later" there means "in another cell".
        foreach (PropTier tier in Props.Tiers)
        {
            float scale = DrawScale(tier);
            int first = _planted.Count;
            for (int q = 0; q < Field.Columns; q++)
            for (int r = 0; r < Field.Rows; r++)
            {
                var cell = new Vector2I(q, r);
                foreach ((PropTier t, int least, int most) in Growth(cell))
                    if (t == tier)
                        SowTier(cell, tier, least, most, squash, scale);
            }
            // Only now: within a tier the lattice is the spacing statement, and
            // adding the disc test to it would refuse trees the lattice allows.
            // See the class remarks - this line is that asymmetry.
            for (int i = first; i < _planted.Count; i++)
                Occupy(_planted[i], squash, scale);
        }

        // Depth decides overlap, so the tree list is kept in that order too. Not
        // for correctness - ZIndex already settles it - but so that a tie
        // between two trees at the same foot row resolves back to front rather
        // than to whichever cell was walked first.
        _planted.Sort((a, b) => a.Depth.CompareTo(b.Depth));
        foreach (PropNode node in _planted)
            MoveChild(node, _planted.Count - 1);
    }

    /// <summary>One candidate that survived every rule that cannot bend, held
    /// with the roll that decides whether it takes.</summary>
    private readonly record struct Seedling(
        Vector2 At, int Species, bool Mirrored, double Roll, double Fade,
        double Shuffle);

    private void SowTier(Vector2I cell, PropTier tier, int least, int most,
                         float squash, float scale)
    {
        Vector2 centre = Field!.CellCentre(cell);
        Vector2 ground = new(centre.X, centre.Y / squash);
        Vector2I hex = Field.Atlas!.HexRect.Size;
        double halfX = hex.X * 0.5;
        double halfY = hex.Y * 0.5 / squash;
        double step0 = StepOf(tier);
        IReadOnlyList<int> species = Props!.Species(tier);
        if (species.Count == 0)
            return;
        // The tier's own two distances, resolved once: how much of the tank's
        // patch it keeps free, and how far inside the border it sits back. Both
        // in ground px, which is what EdgeRoom answers in - the inradius is
        // sqrt(3)/2 of the circumradius the tile's width gives.
        double clearing = KeepOut * tier.Clearing;
        double inset = tier.Inset * Math.Sqrt(3.0) * 0.5 * (hex.X * 0.5);

        List<Seedling> Gather(double step, int salt)
        {
            salt += tier.Salt;
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

                // The keep-out is about a tank being able to park here, so a
                // tier a tank drives over or through keeps none of it. See
                // PropTier.Clearing - and note that this test is what decides
                // whether a tier rings its cell or fills it, because the band
                // outside the keep-out is 24 ground px of a 107px radius.
                if (clearing > 0.0 && seed.DistanceTo(ground) < clearing)
                    continue;

                // The spot says which props can stand in it, and only then does
                // the hash say which of those does. The other order - hash a
                // species, then refuse the spot if it does not fit - throws away
                // a place a smaller tree could have taken, and quietly thins the
                // big ones twice over: once by refusing them and once by not
                // planting anything in their place. Measured on the four trees
                // in hand: the base wants 11.3px of room at the smallest and
                // 18.3 at the largest, against a ring 24 deep at its narrowest.
                //
                // Among this tier's species and never across tiers: a spot no
                // bush fits is a spot with no bush in it, not a spot that gets a
                // pebble instead.
                double room = tier.MindsBorder
                    ? EdgeRoom(cell, seed) - inset : double.PositiveInfinity;
                int fits = 0;
                foreach (int k in species)
                    if (Props.RootOf(k) * scale + Clearance <= room)
                        fits++;
                if (fits == 0)
                    continue;

                int wanted = (int)(Hash01(i, j, 4051 + salt) * fits);
                int chosen = species[0];
                foreach (int k in species)
                {
                    if (Props.RootOf(k) * scale + Clearance > room)
                        continue;
                    if (wanted-- == 0)
                    {
                        chosen = k;
                        break;
                    }
                }
                bool mirrored = Hash01(i, j, 5077 + salt) < 0.5;

                if (ClearFront)
                {
                    double d = at.Y - centre.Y;
                    if (d > 0.0 && Props.RiseOf(chosen) * scale >= d - TankBelow)
                        continue;
                }

                // And the one test that has no lattice behind it: this tier came
                // off its own grid, so whether it lands on something a taller
                // tier already planted is a question only the measured bases can
                // answer. Empty until the second tier, which is why the trees are
                // untouched by any of this.
                if (Crowded(seed, Props.RootOf(chosen) * scale))
                    continue;

                found.Add(new Seedling(at, chosen, mirrored,
                                       Hash01(i, j, 3037 + salt),
                                       EdgeFade(cell, seed, squash, tier),
                                       Hash01(i, j, 6091 + salt)));
            }
            return found;
        }

        var pool = Gather(step0, 0);

        // The edge fade is a preference, not a rule, and this is where the
        // difference shows. Everything above rejects outright: a tree inside the
        // keep-out or across a border is wrong wherever it stands. The fade only
        // says which of the legal spots is the better one - so when it has taken
        // too many, the best of what it refused comes back rather than the cell
        // coming out as a field with four trees short of being woods.
        var taking = pool.Where(s => s.Roll < s.Fade).ToList();
        if (taking.Count < least)
            taking.AddRange(pool.Where(s => s.Roll >= s.Fade)
                                .OrderByDescending(s => s.Fade)
                                .ThenBy(s => s.Shuffle)
                                .Take(least - taking.Count));

        // And if the whole cell had fewer legal spots than the floor asks for -
        // which happens, the ring is thin - go over it again at half the step.
        // The minimum draws from candidates, so a floor that cannot be filled
        // is not a floor at all: it was a wish, and a wooded cell came out with
        // two trees on it while the setting said four.
        //
        // Only where it is short, so the spacing everywhere else is the spacing
        // that was set - and only ever finer, so nothing that was legal at the
        // coarse step stops being legal.
        if (taking.Count < least)
        {
            var seen = new HashSet<Vector2>(taking.Select(s => s.At));
            taking.AddRange(Gather(step0 * 0.5, 617)
                            .Where(s => !seen.Contains(s.At)
                                        && taking.All(t => t.At.DistanceTo(s.At)
                                                           > step0 * 0.25))
                            .OrderByDescending(s => s.Fade)
                            .ThenBy(s => s.Shuffle)
                            .Take(least - taking.Count));
        }
        if (most > 0 && taking.Count > most)
            taking = taking.OrderByDescending(s => s.Fade)
                           .ThenBy(s => s.Shuffle)
                           .Take(most).ToList();

        // How high this cell stands, in screen px. The foot is already drawn
        // lifted - it came off CellCentre - so this is only ever wanted to take
        // the lift back out for the depth, which is the one place the two terms
        // have to be apart.
        float lift = Field.LevelAt(cell) * Field.Lift;

        foreach (Seedling s in taking)
        {
            var node = new PropNode
            {
                Art = Props!.ArtOf(s.Species, s.Mirrored),
                Foot = Props.FootOf(s.Species, s.Mirrored),
                Size = Props.SizeOf(s.Species),
                BurntArt = Props.HasBurnt(s.Species)
                    ? Props.ArtOf(s.Species, s.Mirrored, true) : null,
                BurntFoot = Props.FootOf(s.Species, s.Mirrored, true),
                BurntSize = Props.SizeOf(s.Species, true),
                RiseImage = Props.RiseOf(s.Species),
                RootImage = Props.RootOf(s.Species),
                SpreadImage = Props.SpreadOf(s.Species),
                Pixels = scale,
                Ground = s.At,
                Species = s.Species,
                Mirrored = s.Mirrored,
                Cell = cell,
                Tier = tier,
                // Copied onto the node rather than reached for through the set,
                // because everything that reads them walks the planted list
                // every frame: Blow, Rush, Brush and Reveal between them touch
                // each prop four times a frame, and a dictionary lookup per
                // touch buys nothing over two bools.
                Sways = tier.Sways,
                Fades = tier.Fades,
                Position = Origin + s.At,
                Depth = Field.Depth(s.At.Y + lift, lift),
                // Ground dressing goes in the band; everything that stands sorts
                // by its foot. Depth above is filled in either way - the checks
                // read it, the list is sorted by it, and inside the band it is
                // still what settles two stones against each other.
                ZIndex = tier.UnderTanks
                    ? DressZ
                    : Mathf.RoundToInt(Field.Depth(s.At.Y + lift, lift)),
                // Hashed off where the tree stands rather than off its place in
                // any list, so the wood sways the same way twice and a tree
                // keeps its phase when the sowing around it changes. Its own
                // salts, so phase is not a function of species or jitter -
                // shared bits there would put rows of trees in step.
                SwayPhase = Hash01(Mathf.RoundToInt(s.At.X),
                                   Mathf.RoundToInt(s.At.Y), 7919) * Math.Tau,
                SwayRate = 0.8 + 0.5 * Hash01(Mathf.RoundToInt(s.At.X),
                                              Mathf.RoundToInt(s.At.Y), 8837),
                // Off where it stands, for the sway phase's reason and not the
                // planting order's: a tree keeps how late it catches when the
                // sowing around it changes, and two trees on one cell do not
                // catch together.
                Seed = Mathf.RoundToInt(s.At.X) * 1_009
                       + Mathf.RoundToInt(s.At.Y),
            };
            AddChild(node);
            _planted.Add(node);
        }
    }

    // ---- what is already standing ---------------------------------------

    /// <summary>
    /// The bases of everything planted by a taller tier, in ground space,
    /// bucketed so a candidate asks about its own neighbourhood rather than
    /// about the board.
    ///
    /// A grid rather than a list, and it is not premature: a mixed board sows
    /// six hundred props, and the shortest tier walks a lattice ten ground px
    /// across, so the pairing is the one place here that grows as the square.
    /// The bucket is <see cref="Bucket"/> wide and a lookup reads nine of them,
    /// which covers any pair that could touch as long as no base is wider than
    /// a bucket - and <see cref="Occupy"/> asserts exactly that by widening the
    /// bucket rather than by hoping.
    /// </summary>
    private readonly Dictionary<Vector2I, List<(Vector2 At, float Root)>> _taken = new();

    /// <summary>Ground px across one bucket. Sixty is over twice the widest base
    /// measured (18.3px) plus the clearance, so nine buckets always contain
    /// every neighbour that could overlap.</summary>
    private const double Bucket = 60.0;

    private static Vector2I Cellule(Vector2 ground) =>
        new((int)Math.Floor(ground.X / Bucket), (int)Math.Floor(ground.Y / Bucket));

    /// <summary>Record a planted prop's base, so shorter tiers can see it. Takes
    /// the node rather than a point and a radius, so the ground space and the
    /// scale are converted in one place and cannot disagree with the sowing's.
    /// </summary>
    private void Occupy(PropNode node, float squash, float scale)
    {
        var ground = new Vector2(node.Ground.X, node.Ground.Y / squash);
        float root = Props!.RootOf(node.Species) * scale;
        Vector2I key = Cellule(ground);
        if (!_taken.TryGetValue(key, out List<(Vector2, float)>? bin))
            _taken[key] = bin = new List<(Vector2, float)>();
        bin.Add((ground, root));
    }

    /// <summary>
    /// Whether a base of <paramref name="root"/> at <paramref name="ground"/>
    /// would overlap something a taller tier already planted.
    ///
    /// Both radii and the clearance, because the question is whether the two
    /// bases touch and a base has a width at both ends of the pair. The same
    /// <see cref="Clearance"/> the border uses: what it means there is "the
    /// drawn seam is not touched", and what it means here is the same slack
    /// against the other prop's own drawn edge.
    /// </summary>
    private bool Crowded(Vector2 ground, float root)
    {
        if (_taken.Count == 0)
            return false;
        Vector2I key = Cellule(ground);
        for (int dx = -1; dx <= 1; dx++)
        for (int dy = -1; dy <= 1; dy++)
        {
            if (!_taken.TryGetValue(key + new Vector2I(dx, dy),
                                    out List<(Vector2 At, float Root)>? bin))
                continue;
            foreach ((Vector2 at, float other) in bin)
                if (ground.DistanceTo(at) < root + other + Clearance)
                    return true;
        }
        return false;
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
            // A rock is not weather. Skipped here rather than given a zero
            // amplitude, so nothing about it enters the redraw list either -
            // and skipped before the flinch too, because a spring that is never
            // driven still costs the test that finds that out.
            if (!tree.Sways)
                continue;
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
            // A burnt trunk is stiff. Damped rather than pinned, because a bare
            // stem in a gust that stood dead still while the wood around it
            // moved would read as a sprite that had stopped being updated - the
            // rock's flag says "not a thing that sways", and this says "the same
            // thing, with its crown gone".
            if (tree.Charred)
                lean *= CharredSway;
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

    /// <summary>What share of the sway a burnt trunk keeps. A third: what is left
    /// of a tree after a fire is a stem with no crown on it, so most of the sail
    /// area the wind was pushing is gone.</summary>
    public const float CharredSway = 0.33f;

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

    /// <summary>
    /// How far a crown is shouldered over by a hull right against it at
    /// <see cref="BrushSpeed"/>, in screen px.
    ///
    /// Five rather than the hit's seven: a tank going through the trees is a
    /// bigger thing than a shell but it is leaning on them rather than bursting,
    /// and the difference between the two has to be readable or there is no
    /// point having both.
    /// </summary>
    public double Brushing = 5.0;

    /// <summary>
    /// How far from the hull the wood gives way, in ground px.
    ///
    /// <b>This is what makes it happen on the border and nowhere else, and it is
    /// the keep-out doing the work rather than any test for entering a cell.</b>
    /// Nothing grows within 83 of a cell's centre and the hexagon's near edge is
    /// 107 out, so a tank parked in a clearing is a good 83 from the nearest
    /// trunk and a tank halfway onto the next cell is standing in the ring. At 90
    /// the first is barely touched and the second is in among them - which is
    /// "driving on" and "driving off" expressed as geometry, with no moment to
    /// detect and nothing to go stale.
    /// </summary>
    public double BrushReach = 90.0;

    /// <summary>The speed <see cref="Brushing"/> is quoted at - the medium's
    /// cruise. One reference rather than a fraction of each class's own top
    /// speed, because how hard a hull shoulders a sapling aside is how fast it is
    /// going: a heavy flat out at 175 disturbs the wood less than a light flat
    /// out at 310, and saying otherwise would need a table nothing measured.
    /// </summary>
    public double BrushSpeed = 240.0;

    /// <summary>
    /// A hull passing at <paramref name="at"/>, having travelled
    /// <paramref name="travel"/> since last frame - both field-local screen px.
    ///
    /// <b>A force while it is near, where the blast is an impulse once.</b> That
    /// is the whole difference between the two and it is what they look like:
    /// a burst leaves a tree already moving and lets go, and a hull leans on it
    /// for as long as it takes to get past, so the tree bends over and springs
    /// back behind it. Driving the spring rather than kicking it also means the
    /// bend is bounded - it settles at force over stiffness however long the
    /// tank takes - where repeated kicks would pile up without limit on a tank
    /// that dawdled.
    ///
    /// <b>Shouldered aside, so the direction is away from the hull rather than
    /// along its travel.</b> A tank driving up-board would push the trees in
    /// front of it away from the camera, and a shear cannot draw that at all -
    /// the same silence the blast has in that direction. But it is also not what
    /// happens: what a hull does to a wood is part it, and parting is sideways
    /// on screen whichever way the tank is going.
    ///
    /// <b>Travel rather than a speed, and it is not tidiness.</b> The
    /// displacement says both how fast and that it is happening at all, so a tank
    /// pivoting on the spot leaves the wood alone without anything here knowing
    /// what a pivot is, and reverse works without a sign.
    /// </summary>
    public void Brush(Vector2 at, Vector2 travel, double delta)
    {
        if (!Enabled || Brushing <= 0.0 || _planted.Count == 0 || delta <= 0.0)
            return;
        float squash = Squash;
        var src = new Vector2(at.X, at.Y / squash);
        var way = new Vector2(travel.X, travel.Y / squash);
        double moved = way.Length();
        if (moved <= 0.0)
            return;
        // Capped at the reference rather than scaled past it: the fastest thing
        // on the board is already at it, and a tank hurled about by a test would
        // otherwise lay the wood flat.
        double push = Brushing * Math.Min(1.0, moved / delta / BrushSpeed);
        foreach (PropNode tree in _planted)
        {
            if (!tree.Sways)
                continue;               // a hull does not shoulder a boulder aside
            var g = new Vector2(tree.Ground.X, tree.Ground.Y / squash);
            double d = g.DistanceTo(src);
            if (d >= BrushReach || d <= 0.001)
                continue;
            double want = push * (1.0 - d / BrushReach) * (g.X - src.X) / d;
            // Force, integrated by Blow with everything else. A spring driven by
            // F rests at F over its stiffness - on paper. Stepped, it rests
            // exactly (1 - c*dt) of that, which is a tenth of it at 1/60, so the
            // setting would quietly mean nine tenths of what it says. The same
            // disagreement UnitKick and Recoil.PeakFor are both about, and the
            // same answer: ask the integrator that is actually running, not the
            // closed form. Floored in case a frame is ever long enough to make
            // the factor meaningless.
            double slack = Math.Max(0.25, 1.0 - BlastDamping * delta);
            tree.FlinchRate +=
                (float)(want / SwayHeight * BlastStiffness / slack * delta);
        }
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
                if (!tree.Sways)
                    continue;
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
    /// <summary>
    /// Whether a fire could take hold on this cell, and cross it.
    ///
    /// Off what is actually standing there, not off what the ground is called: a
    /// forest cell whose trees all fell to the budget's ceiling has nothing to
    /// burn, and a fire that spread onto it would blacken a field. See
    /// <see cref="PropTier.Carries"/> for why a cell of scrub is not a way
    /// across.
    /// </summary>
    public bool Carrying(Vector2I cell)
    {
        foreach (PropNode prop in _planted)
            if (prop.Cell == cell && prop.Tier.Carries)
                return true;
        return false;
    }

    /// <summary>
    /// Read the fire onto every tree, once a frame.
    ///
    /// <b>The cell holds the clock and the tree reads it late.</b> Every tree on
    /// one cell lighting in the same frame is one animation played eight times -
    /// the rule the three tanks' clocks are separate for. The lateness is a hash
    /// of where the trunk stands, so it is the same tree on two runs and a
    /// different one from its neighbour.
    ///
    /// <b>A rock on a burning cell does not burn</b>, and the flag says so rather
    /// than the arithmetic: a boulder charring in a fire is the one thing on the
    /// board announcing that all of this is a tint on a sprite.
    /// </summary>
    public void Smoulder()
    {
        if (_planted.Count == 0)
            return;
        foreach (PropNode prop in _planted)
        {
            Wildfire.Coat was = prop.Coat;
            prop.Coat = Fire is null || !prop.Tier.Burns
                ? Wildfire.Green
                : Fire.Of(prop.Cell, Hash01(prop.Seed, prop.Species, 511_003));
            // The swap changes the picture, and in 2D that is a redraw. The stage
            // reads the node itself, so it needs nothing from here.
            if (prop.Coat.Burnt != was.Burnt || prop.Coat.Char != was.Char
                || prop.Coat.Flame != was.Flame)
                prop.QueueRedraw();
        }
    }

    /// <summary>How many trees are in flame and how many have burnt out.
    /// Reported because a fire that is spreading and one that has gone out are
    /// the same still picture - the pond's readouts exist for this reason too.
    /// </summary>
    public (int Burning, int Burnt) Ablaze()
    {
        int burning = 0, burnt = 0;
        foreach (PropNode prop in _planted)
        {
            if (prop.Coat.Burnt)
                burnt++;
            else if (prop.Coat.Flame > 0.0f || prop.Coat.Char > 0.0f)
                burning++;
        }
        return (burning, burnt);
    }

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
            // Only what could have been in the way. A kerbstone going
            // translucent for a tank parked beside it is the ground admitting to
            // being a view of itself for no gain - nothing was hidden.
            float want = tree.Fades && occupied.Contains(tree.Cell) ? Ghost : 1.0f;
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
    ///
    /// <b>Per tier, because the edge it fades at is that tier's own.</b> A wood
    /// thins where the trees stop; the bushes on the same cells thin where the
    /// bushes stop, and the two boundaries are the same only while one table row
    /// names both. Asking <see cref="IsForest"/> for all of them would fade the
    /// scatter on open ground out at every border it has, which is all of them -
    /// so every cell that was not a forest would come out with its ceiling
    /// unreachable and nothing on it.
    /// </summary>
    private double EdgeFade(Vector2I cell, Vector2 ground, float squash, PropTier tier)
    {
        double nearest = double.MaxValue;
        foreach (int heading in HexField.EdgeHeadings)
        {
            Vector2I next = HexField.Step(cell, heading);
            if (next == cell)
                continue;
            if (Field!.InBounds(next) && Growth(next).Any(g => g.Tier == tier))
                continue;                       // the same tier next door, or off the board
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

    /// <summary>What this one looks like once it has finished burning, or
    /// nothing. Held beside the living picture rather than replacing it, which is
    /// the whole of how the pair stays honest: <see cref="Art"/>,
    /// <see cref="Foot"/> and <see cref="Size"/> keep meaning the tree that was
    /// sown, so everything measured off them - the clearance, the seam, the
    /// contact patch, the checks - is measured off the art the spot was chosen
    /// for. Only the three <c>Shown</c> members below know about the state.
    /// </summary>
    public Texture2D? BurntArt;
    public Vector2 BurntFoot;
    public Vector2 BurntSize;

    /// <summary>This tree's own place in every hash about it - how late it
    /// catches, and nothing else yet. Handed out at planting, so it is the same
    /// tree on two runs of the same board: <c>--capture</c> fixes the step
    /// precisely so two runs can be compared.</summary>
    public int Seed;

    /// <summary>How far through burning it is. Written once a frame by
    /// <see cref="Grove.Smoulder"/> and read by whoever draws.</summary>
    public Wildfire.Coat Coat = Wildfire.Green;

    /// <summary>Whether it is standing in its burnt art rather than the one it
    /// grew in. False for a kind nobody drew a burnt state for, however long it
    /// burns - <c>EffectLayer.StandIn</c>'s rule: a board whose art predates the
    /// state loses nothing, it just chars and keeps its shape.</summary>
    public bool Charred => Coat.Burnt && BurntArt is not null;

    /// <summary>
    /// How far this one is charred, as the paint wants it.
    ///
    /// <b>It does not go back to nothing when the burn ends - it goes to
    /// everything, unless there is a burnt picture to take over.</b> The char is
    /// the transition for a kind that has one, so at the swap the art is already
    /// charcoal and the multiply has to come off or it goes to black. A kind with
    /// no burnt art has nothing to swap to, so the char is all it will ever have,
    /// and dropping it left bright green scrub standing in a burnt-out cell - the
    /// one thing on the board saying the fire had not really been there.
    /// </summary>
    public float Scorch =>
        Coat.Burnt ? (BurntArt is null ? 1.0f : 0.0f) : Coat.Char;

    /// <summary>The picture, the foot and the size as drawn - the state's if it
    /// has one. Three members and not a mutated field, so that no measurement can
    /// pick them up by accident.</summary>
    public Texture2D ShownArt => Charred ? BurntArt! : Art;
    public Vector2 ShownFoot => Charred ? BurntFoot : Foot;
    public Vector2 ShownSize => Charred ? BurntSize : Size;
    public float Pixels = 1.0f;   // image px to screen px
    public Vector2 Ground;      // field-local screen px of the foot
    public int Species;
    public bool Mirrored;
    public Vector2I Cell;

    /// <summary>Which rules this one plays by. Carried for the checks and the
    /// trace - the two flags below are what the frame loops actually read.
    /// </summary>
    public PropTier Tier = null!;

    /// <summary>Whether the wind, a blast and a passing hull move it. False on a
    /// rock, and that is the difference a board can see: a boulder leaning in a
    /// gust is the one thing on screen announcing that all of this is a shear on
    /// a sprite.</summary>
    public bool Sways = true;

    /// <summary>Whether it ghosts while a tank is on its cell. False on anything
    /// that cannot hide a tank - fading what was never in the way reads as the
    /// ground going translucent rather than as a view through a wood.</summary>
    public bool Fades = true;

    /// <summary>
    /// How near the camera this tree is - <see cref="HexField.Depth"/> of its
    /// foot, and what its <c>ZIndex</c> is rounded from.
    ///
    /// Not the drawn foot row, which is what it was while the board was flat and
    /// the two were the same number. A tree standing on a hill is drawn further
    /// up the screen and is <b>not</b> thereby further away: read off the drawn
    /// row it sorts behind everything on the plain, and a wood on a rise ends up
    /// under the tanks in front of it.
    /// </summary>
    public float Depth;

    /// <summary>Height above the foot in image px, straight from the set.
    /// Carried rather than derived from <see cref="Size"/>, because the two are
    /// different numbers whenever the art has transparent margin under the
    /// trunk.</summary>
    public float RiseImage;

    /// <summary>Height above the foot, on screen. What the clearance rule and
    /// the checks are written in.</summary>
    public float Rise => RiseImage * Pixels;

    /// <summary>Half the ground contact's width in image px, straight from the
    /// set - the same number the sower keeps props off each other by, carried so
    /// the stage can size the patch of ground under the foot without asking the
    /// set again. Unchanged by mirroring: the foot moves with the picture, the
    /// extent around it does not.</summary>
    public float RootImage;

    /// <summary>That contact on screen. See <see cref="Stage3D.Seat"/>: it is
    /// what the contact shadow is sized off, because it is the only thing about
    /// a prop's footprint that was measured rather than declared.</summary>
    public float Root => RootImage * Pixels;

    /// <summary>Half the footprint's width in image px, and on screen. What the
    /// contact patch is never smaller than - see
    /// <see cref="Stage3D.Seating"/>.</summary>
    public float SpreadImage;

    public float Spread => SpreadImage * Pixels;

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
        DrawTextureRect(ShownArt,
                        new Rect2(-ShownFoot * Pixels, ShownSize * Pixels), false);
    }
}
