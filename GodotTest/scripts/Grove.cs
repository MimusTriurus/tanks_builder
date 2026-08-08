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
    /// <summary>Ground px between lattice nodes before jitter. 46 puts about
    /// twenty-five candidates over a hexagon, of which the keep-out leaves ten
    /// or so on the rim.</summary>
    public double Spacing = 40.0;

    /// <summary>How far a node may wander from its lattice point, as a fraction
    /// of the spacing. Under 0.5 by construction: at 0.5 two neighbours can
    /// meet, and the lattice stops guaranteeing any spacing at all.</summary>
    public double Jitter = 0.38;

    /// <summary>Ground radius nothing may grow inside, centred on the cell.
    /// Set from the tanks - see the class remarks - rather than chosen.</summary>
    public double KeepOut = 83.0;

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

    /// <summary>What a tree fades to while a tank is on its cell, and how long
    /// it takes. Fading rather than hiding: the tank has to be visible through
    /// the wood, but a wood that vanishes is a tank standing in a field. The
    /// time constant is short enough to keep up with a tank crossing a cell and
    /// long enough not to read as a switch.</summary>
    public float Ghost = 0.35f;
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

        int i0 = (int)Math.Floor((ground.X - halfX) / Spacing) - 1;
        int i1 = (int)Math.Ceiling((ground.X + halfX) / Spacing) + 1;
        int j0 = (int)Math.Floor((ground.Y - halfY) / Spacing) - 1;
        int j1 = (int)Math.Ceiling((ground.Y + halfY) / Spacing) + 1;

        var pool = new List<Seedling>();
        for (int i = i0; i <= i1; i++)
        for (int j = j0; j <= j1; j++)
        {
            var seed = new Vector2(
                (float)((i + (Hash01(i, j, 1013) - 0.5) * 2.0 * Jitter) * Spacing),
                (float)((j + (Hash01(i, j, 2027) - 0.5) * 2.0 * Jitter) * Spacing));
            var at = new Vector2(seed.X, seed.Y * squash);

            // The foot decides the cell, so a node near a border is planted by
            // whichever cell it actually stands in - once, by that cell.
            if (Field.CellAt(at) != cell)
                continue;

            if (seed.DistanceTo(ground) < KeepOut)
                continue;

            int species = (int)(Hash01(i, j, 4051) * Props!.Count);
            bool mirrored = Hash01(i, j, 5077) < 0.5;

            // The foot being inside is not enough: the base is wider than the
            // point it is measured at, and a trunk whose roots cross the border
            // reads as a tree belonging to two cells.
            if (!RootsInside(cell, at, species, mirrored, scale))
                continue;

            if (ClearFront)
            {
                double d = at.Y - centre.Y;
                if (d > 0.0 && Props.RiseOf(species) * scale >= d - TankBelow)
                    continue;
            }

            pool.Add(new Seedling(at, species, mirrored, Hash01(i, j, 3037),
                                  EdgeFade(cell, seed, squash)));
        }

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
            };
            AddChild(node);
            _planted.Add(node);
        }
    }

    /// <summary>
    /// Whether the base stands inside the hexagon, not merely the point it is
    /// measured at.
    ///
    /// Asked of the two horizontal extremes rather than of a radius, because the
    /// flare is not centred on the trunk - see <see cref="PropSet"/>. Asked of
    /// the cell rather than of an inset outline, because the cell is what
    /// <see cref="HexField.CellAt"/> already answers exactly, slanted edges and
    /// all; an inset hexagon would be a second description of the same shape.
    /// </summary>
    private bool RootsInside(Vector2I cell, Vector2 at, int species,
                             bool mirrored, float scale)
    {
        (float left, float right) = Props!.RootOf(species, mirrored);
        return Field!.CellAt(at - new Vector2(left * scale, 0.0f)) == cell
            && Field.CellAt(at + new Vector2(right * scale, 0.0f)) == cell;
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

    public override void _Ready()
    {
        // Painted at four times the size it is drawn at, so the same argument
        // as the ground: nearest minification point-samples, and a tree that
        // crawls as the board pans is worse than no tree.
        TextureFilter = TextureFilterEnum.LinearWithMipmaps;
    }

    public override void _Draw() =>
        DrawTextureRect(Art, new Rect2(-Foot * Pixels, Size * Pixels), false);
}
