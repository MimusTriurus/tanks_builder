using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The things that stand on the ground: trees for now, read off an absolute
/// path the way the atlases and the terrain are.
///
/// A prop is not a hex. That is the whole reason this exists next to
/// <see cref="TerrainSet"/> rather than inside it: a band of hex-sized art
/// cannot be moved out of a tank's way, and a prop can. Everything below
/// follows from a prop being one object with one place on the ground.
///
/// <b>The foot is measured, not declared.</b> It is the centre of the bottom
/// opaque row - where the sprite touches the ground - not the centre of the
/// bounding box. On Tree_4 those are 31px apart (58 against 89), because the
/// canopy leans and the trunk does not, and it is the trunk that stands
/// somewhere. Sorting off the box would put a leaning tree in front of things
/// it grows behind.
///
/// <b>Rise is height above that foot</b>, and it is the number the clearance
/// rule is written in: a prop whose foot is <c>d</c> px in front of a tank
/// clears it only while <c>rise &lt; d - below</c>, where <c>below</c> is how far
/// the tank hangs under its own contact point. See <see cref="Grove"/>.
///
/// <b>Mirrors are baked, not flipped at draw time.</b> An isometric sprite
/// cannot be rotated, so mirroring is the only free variation there is, and
/// baking it keeps the foot honest - a flip about the sprite's origin would
/// move the trunk, which is the one point that must not move. Two textures of
/// 179x446 cost 0.6MB for the pair; the arithmetic it removes would have been
/// wrong in exactly the way that reads as trees sliding sideways.
///
/// <b>A prop belongs to a tier, and the tier is the folder it was found in.</b>
/// See <see cref="PropTier"/>: a species is which picture, a tier is which
/// rules. PNGs loose in the root are trees - which is what every prop was
/// before tiers existed, so a props folder nobody has reorganised loads exactly
/// as it did and every board rendered from it is unchanged.
///
/// <b>The index stays flat across tiers.</b> <c>Species</c> is a position in one
/// list rather than a pair, because that index is a key elsewhere -
/// <see cref="Stage3D"/> caches one billboard mesh per <c>(species, mirrored)</c> -
/// and a two-part key is a second thing to hold in agreement for nothing.
///
/// <b>A state is not a species either, and it is a suffix rather than a
/// folder.</b> <c>Tree_1_burnt.png</c> is what <c>Tree_1.png</c> becomes, not a
/// sixth kind of tree - loaded as a species it would be sown beside the living
/// wood instead of replacing it, which is a forest that grows already burnt. The
/// folder cannot say so because the folder is the tier and a burnt tree plays by
/// a tree's rules; so it is named in the file, with a word and not a number, the
/// way <c>L.Caterpillar.Rebuilt</c> and <c>*.Preview</c> name a state in the
/// pipeline. A suffix with no living twin is refused and said out loud - art that
/// never appears is indistinguishable from art that failed to load.
///
/// <b>And the state cannot reach the placement numbers, by construction.</b>
/// <see cref="RiseOf"/>, <see cref="RootOf"/> and <see cref="SpreadOf"/> have no
/// state argument at all: what a tree keeps clear, how far it stands from the
/// seam and how it sorts is settled by the living art, once, before it ever
/// catches. The burnt art only draws. That is not tidiness - a burnt trunk with
/// ash round its feet measures a base twice the wood's (16.7px against 8.3 on
/// <c>Tree_5</c>), and a prop already standing cannot be given room it was not
/// sown with.
///
/// <b>Tiers are ordered by measurement, not by the order they are declared
/// in.</b> Tallest first, off the tallest prop each one loaded, because that is
/// the order they have to be sown in: undergrowth tests against what is already
/// standing (see <see cref="Grove"/>), and a list that said otherwise would put
/// a bush down and a tree on top of it.
/// </summary>
public sealed class PropSet
{
    /// <summary>
    /// How many times the on-screen scale art is painted at when its tier does
    /// not say otherwise - see <see cref="PropTier.Detail"/>, which is where the
    /// number lives now that two folders disagree about it.
    ///
    /// Declared rather than measured, because a prop has no frame to compare
    /// against - it is not painted on the template the way a terrain kind is.
    /// Four, to match the hand-drawn ground: <c>soil.png</c> is a 4x kind, so
    /// art drawn beside it in the same document is at the same scale.
    /// </summary>
    public const double Detail = 4.0;

    /// <summary>
    /// How much of the sprite's height counts as touching the ground. Three
    /// percent is 13px on Tree_4, which reaches the second trunk's foot six
    /// rows up and stops well below where the stems start splaying.
    ///
    /// It is a band and not the bottom row because a prop can stand on more than
    /// one point: Tree_4 is two trunks, the right one set slightly back, and its
    /// foot never appears in the bottom row at all. Reading the row alone put
    /// the prop's position on the left trunk and called the right one an
    /// overhang 61px wide - which is how a 16px-wide base came to demand 40px of
    /// room and a wooded cell came out with one tree in it.
    /// </summary>
    private const float RootBand = 0.03f;

    /// <summary>What marks a file as a state of another rather than a kind of its
    /// own. One reserved word, so that a second state is a second word and never
    /// a number that needs a table to read.</summary>
    public const string BurntSuffix = "_burnt";

    /// <summary>The half of a prop that is only ever drawn: the picture, its
    /// mirror and where each of them touches the ground. No rise, no contact, no
    /// footprint - see the class remarks on why the state cannot reach those.
    /// The two measured numbers are kept for the note and the checks alone, and
    /// deliberately not exposed the way the living ones are.</summary>
    private sealed class Coat
    {
        public Texture2D Art = null!;
        public Texture2D Mirror = null!;
        public Vector2 Foot;
        public Vector2 MirrorFoot;
        public Vector2 Size;
        public float Rise;      // for the note and the check, never for placement
        public float Root;
    }

    private sealed class Prop
    {
        public string Name = "";
        public PropTier Tier = null!;
        public Texture2D Art = null!;
        public Texture2D Mirror = null!;
        public Vector2 Foot;        // image px, in the unmirrored art
        public Vector2 MirrorFoot;
        public Vector2 Size;        // image px
        public float Rise;          // image px above the foot
        public float Root;          // image px, half the ground contact's width
        public float Spread;        // image px, half the footprint's width

        /// <summary>What it looks like once it has finished burning, or nothing.
        /// </summary>
        public Coat? Burnt;
    }

    private readonly List<Prop> _props = new();
    private readonly List<PropTier> _tiers = new();

    public IReadOnlyList<string> Names => _props.Select(p => p.Name).ToList();
    public int Count => _props.Count;
    public bool Any => _props.Count > 0;

    /// <summary>The tiers that loaded art, tallest first - the order they have
    /// to be sown in. See the class remarks.</summary>
    public IReadOnlyList<PropTier> Tiers => _tiers;

    public string Note { get; private set; } = "no props";

    private PropSet() { }

    public static PropSet Load(string root)
    {
        var set = new PropSet();
        if (!Directory.Exists(root))
        {
            set.Note = $"no props at {root}";
            return set;
        }

        var refused = new List<string>();

        // The root first and by its own name, so a folder that predates tiers
        // loads as the trees it has always been. Subdirectories after, in name
        // order, so which ordinal an undeclared tier gets - and therefore its
        // salt - does not depend on the filesystem's enumeration order.
        set.Sow(root, PropTier.Trees, 0, refused);
        var folders = Directory.GetDirectories(root)
                               .Select(Path.GetFileName)
                               .Where(n => !string.IsNullOrEmpty(n))
                               .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                               .ToList();
        for (int i = 0; i < folders.Count; i++)
            set.Sow(Path.Combine(root, folders[i]!), folders[i]!.ToLowerInvariant(),
                    i + 1, refused);

        // Tallest first, measured. Ties go to the tier that loaded first, which
        // is the root's, so nothing about the existing wood depends on a
        // comparison between two numbers that happened to be equal.
        set._tiers.Sort((a, b) =>
        {
            float ta = set.Tallest(a), tb = set.Tallest(b);
            return ta == tb ? 0 : tb.CompareTo(ta);
        });

        set.Note = set._props.Count == 0
            ? $"no props in {root}"
            : $"{set._props.Count} props in "
              + $"{set._tiers.Count} tier{(set._tiers.Count == 1 ? "" : "s")}: "
              + string.Join("; ", set._tiers.Select(t =>
                    $"{t.Name}{(t.Declared ? "" : " (undeclared)")} x{t.Detail:F0} "
                    + $"step {t.Step:F0} ["
                    + string.Join(", ", set._props.Where(p => p.Tier == t).Select(
                          p => $"{p.Name} {p.Size.X}x{p.Size.Y} rise {p.Rise:F0}"
                               + (p.Burnt is null ? ""
                                  : $" +burnt {p.Burnt.Size.X}x{p.Burnt.Size.Y}"
                                    + $" rise {p.Burnt.Rise:F0}"
                                    + $" base {p.Burnt.Root:F0}/{p.Root:F0}")))
                    + "]"))
              + (refused.Count == 0 ? "" : "; refused " + string.Join("; ", refused));
        return set;
    }

    /// <summary>Every PNG directly in one folder, as one tier. A folder with no
    /// readable art adds no tier at all - an empty <c>Rock/</c> is not a tier
    /// with nothing in it, it is a tier nobody has drawn yet, and the budget
    /// table skips what did not load.
    ///
    /// <b>Two passes, because a state has to find its species and the folder
    /// hands them over in name order.</b> <c>Tree_1_burnt.png</c> sorts right
    /// after <c>Tree_1.png</c> here and would not on a folder named differently,
    /// so the pairing is done by name and not by adjacency.</summary>
    private void Sow(string folder, string tierName, int ordinal, List<string> refused)
    {
        if (!Directory.Exists(folder))
            return;
        PropTier tier = PropTier.For(tierName, ordinal);

        var read = new Dictionary<string, (Image Art, Rect2I Box)>();
        foreach (string path in Directory.GetFiles(folder, "*.png").OrderBy(p => p))
        {
            string name = Path.GetFileNameWithoutExtension(path);
            Image? art = Image.LoadFromFile(path);
            if (art is null)
            {
                refused.Add($"{tierName}/{name} unreadable");
                continue;
            }
            Rect2I box = art.GetUsedRect();
            if (box.Size.X <= 0 || box.Size.Y <= 0)
            {
                refused.Add($"{tierName}/{name} is empty");
                continue;
            }
            read[name] = (art, box);
        }

        bool added = false;
        foreach ((string name, (Image art, Rect2I box)) in read.OrderBy(e => e.Key))
        {
            if (name.EndsWith(BurntSuffix, StringComparison.OrdinalIgnoreCase))
            {
                // Said out loud rather than skipped: a state whose species is
                // missing loads, counts for nothing and never appears, which is
                // the one failure that looks exactly like art that did not load.
                string live = name[..^BurntSuffix.Length];
                if (!read.ContainsKey(live))
                    refused.Add($"{tierName}/{name} has no {live}");
                continue;
            }
            Coat? burnt = null;
            if (read.TryGetValue(name + BurntSuffix, out (Image Art, Rect2I Box) after))
                burnt = Dress(after.Art, after.Box, tier);
            Add(name, tier, art, box, burnt);
            added = true;
        }
        if (added)
            _tiers.Add(tier);
    }

    private float Tallest(PropTier tier) =>
        _props.Where(p => p.Tier == tier).Select(p => p.Rise).DefaultIfEmpty(0.0f).Max();

    private void Add(string name, PropTier tier, Image art, Rect2I box,
                     Coat? burnt)
    {
        (Vector2 foot, float root) = FootOf(art, box);
        float rise = foot.Y - box.Position.Y + 1;
        float spread = SpreadOf(art, box, rise * (1.0f - (float)tier.Stands));
        // Before the mirror, so both halves of the pair carry the same margin -
        // and before the mipmaps, which is the whole point. See Bleed.
        art = Bleed(art);
        var mirror = (Image)art.Duplicate();
        mirror.FlipX();

        // Mirroring is about the image's own centre line, so the foot goes with
        // it: a trunk 58px from the left is 58px from the right afterwards.
        var mirrorFoot = new Vector2(art.GetWidth() - 1 - foot.X, foot.Y);

        art.GenerateMipmaps();
        mirror.GenerateMipmaps();
        _props.Add(new Prop
        {
            Name = name,
            Tier = tier,
            Art = ImageTexture.CreateFromImage(art),
            Mirror = ImageTexture.CreateFromImage(mirror),
            Foot = foot,
            MirrorFoot = mirrorFoot,
            Size = new Vector2(art.GetWidth(), art.GetHeight()),
            Rise = rise,
            Root = root,
            Spread = spread,
            Burnt = burnt,
        });
    }

    /// <summary>One state's picture, measured by exactly the same code that
    /// measures the living one - which is the point of it being a function. A
    /// state whose foot were found some other way would stand somewhere else,
    /// and a tree that steps sideways when it burns is the one thing this pair
    /// must not do.</summary>
    private static Coat Dress(Image art, Rect2I box, PropTier tier)
    {
        (Vector2 foot, float root) = FootOf(art, box);
        // Measured first and bled second: the contact band is read off the alpha,
        // which Bleed does not touch, and reading it after would be reading it
        // through the margin.
        art = Bleed(art);
        var mirror = (Image)art.Duplicate();
        mirror.FlipX();
        var mirrorFoot = new Vector2(art.GetWidth() - 1 - foot.X, foot.Y);
        art.GenerateMipmaps();
        mirror.GenerateMipmaps();
        return new Coat
        {
            Art = ImageTexture.CreateFromImage(art),
            Mirror = ImageTexture.CreateFromImage(mirror),
            Foot = foot,
            MirrorFoot = mirrorFoot,
            Size = new Vector2(art.GetWidth(), art.GetHeight()),
            Rise = foot.Y - box.Position.Y + 1,
            Root = root,
        };
    }

    /// <summary>
    /// Where the prop meets the ground, and how wide that contact is.
    ///
    /// Both come from the same band, and that is the point: the foot is the
    /// centre of the ground contact rather than the centre of the bounding box,
    /// which on a leaning tree are 31px apart and only one of them is where the
    /// thing stands. Half the contact's width is what it needs kept clear
    /// around it - one number rather than a reach either side, because a border
    /// runs in any direction and the extent has to answer in any direction.
    ///
    /// Horizontal only. The sprite's vertical axis mixes height above the
    /// ground with depth into it, and nothing in a flat image can separate the
    /// two - so the extent that can be measured is measured and read as a
    /// radius, and the one that cannot is not guessed at.
    /// </summary>
    private static (Vector2 Foot, float Root) FootOf(Image art, Rect2I box)
    {
        int bottom = box.Position.Y + box.Size.Y - 1;
        int top = Math.Max(box.Position.Y,
                           bottom - Mathf.RoundToInt(box.Size.Y * RootBand));
        int least = int.MaxValue, most = int.MinValue;
        for (int y = top; y <= bottom; y++)
        for (int x = box.Position.X; x < box.Position.X + box.Size.X; x++)
        {
            if (art.GetPixel(x, y).A <= 0.03f)
                continue;
            least = Math.Min(least, x);
            most = Math.Max(most, x);
        }
        if (least == int.MaxValue)
            return (new Vector2(box.Position.X + box.Size.X * 0.5f, bottom), 0.0f);
        return (new Vector2((least + most) * 0.5f, bottom),
                (most - least) * 0.5f);
    }

    /// <summary>
    /// Half the width of what the prop has lying on the ground, in image px.
    ///
    /// <b>The band it is measured over is <see cref="PropTier.Stands"/>'s
    /// leftover</b>, and that is the whole idea: the tier already declares what
    /// share of the drawn extent is height, so what is left is footprint drawn
    /// in isometric, and the sprite's width across it is the footprint's width.
    /// A tree declares itself all height, so its band is empty and this is the
    /// bottom row - which is why nothing about a wood moves.
    ///
    /// <b>Why <see cref="FootOf"/>'s contact will not do the job.</b> That band
    /// is three percent of the sprite's height, so it scales with the art: 4.2
    /// screen px of band on <c>Tree_1</c>, 1.0 on a bush, <b>0.45 on a
    /// stone</b> - the bottom row and nothing else. On a rounded boulder that
    /// samples a chip: 2.1 against a widest half-width of 9.4. The contact is
    /// still the right answer to <i>what touches</i>, which is what the sower
    /// keeps props off each other by; it is the wrong answer to <i>what lies on
    /// the ground</i>, and only a prop with no height to speak of makes the two
    /// differ enough to see.
    /// </summary>
    private static float SpreadOf(Image art, Rect2I box, float band)
    {
        int bottom = box.Position.Y + box.Size.Y - 1;
        int top = Math.Max(box.Position.Y, bottom - Mathf.RoundToInt(band));
        int least = int.MaxValue, most = int.MinValue;
        for (int y = top; y <= bottom; y++)
        for (int x = box.Position.X; x < box.Position.X + box.Size.X; x++)
        {
            if (art.GetPixel(x, y).A <= 0.03f)
                continue;
            least = Math.Min(least, x);
            most = Math.Max(most, x);
        }
        return least == int.MaxValue ? 0.0f : (most - least) * 0.5f;
    }


    /// <summary>
    /// How far the colour is carried out into the transparent margin before the
    /// mipmaps are made.
    ///
    /// Eight, because that is what the draw scale asks for: art at 8x is drawn
    /// from about the third mip, where one texel is an 8x8 block of the original,
    /// so the colour has to be right that far outside the silhouette.
    /// </summary>
    private const int BleedFor = 8;

    /// <summary>
    /// Carry the edge colour out into the fully transparent texels, leaving their
    /// alpha at zero.
    ///
    /// <b>Without it the mipmaps mix the picture with whatever the exporter left
    /// in the margin, and here that was white.</b> Measured on
    /// <c>Tree_3_burnt.png</c>: the transparent texels are (255,255,255) and the
    /// branches are (39,38,37), so at the third mip a bare tree is mostly margin
    /// by area and comes out silver. On the board it read as a tree drawn in
    /// chalk, standing brighter than the wood around it - and the art on disk is
    /// charcoal.
    ///
    /// <b>The same fix <c>water_sheet.py</c> uses, in the mirror case.</b> There a
    /// soft hexagon shrunk on straight alpha picked up the black around it and
    /// came out with a dark rim; here a dark silhouette picks up the white around
    /// it. Both are the one rule: alpha-weighted averaging, or fill the margin
    /// with the colour that belongs there.
    ///
    /// Frontier by frontier rather than a pass over the whole image, so the cost
    /// is the silhouette's perimeter and not eight times its area - nine of these
    /// at a megapixel each is a load, and this is done at load.
    /// </summary>
    private static Image Bleed(Image art)
    {
        int wide = art.GetWidth(), tall = art.GetHeight();
        byte[] px = art.GetData();
        if (px.Length != wide * tall * 4)
            return art;                     // not RGBA8; nothing to carry
        var known = new bool[wide * tall];
        var edge = new List<int>();
        for (int i = 0; i < known.Length; i++)
            if (px[i * 4 + 3] > 0)
            {
                known[i] = true;
                edge.Add(i);
            }
        for (int round = 0; round < BleedFor && edge.Count > 0; round++)
        {
            var next = new List<int>();
            var painted = new List<(int At, int R, int G, int B)>();
            foreach (int at in edge)
            {
                int x = at % wide, y = at / wide;
                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if (dx == 0 && dy == 0 || nx < 0 || ny < 0
                        || nx >= wide || ny >= tall)
                        continue;
                    int to = ny * wide + nx;
                    if (known[to])
                        continue;
                    // Averaged over the known neighbours, so a texel between two
                    // branches takes both rather than whichever was walked first.
                    int r = 0, g = 0, b = 0, n = 0;
                    for (int ky = -1; ky <= 1; ky++)
                    for (int kx = -1; kx <= 1; kx++)
                    {
                        int mx = nx + kx, my = ny + ky;
                        if (mx < 0 || my < 0 || mx >= wide || my >= tall)
                            continue;
                        int from = my * wide + mx;
                        if (!known[from])
                            continue;
                        r += px[from * 4]; g += px[from * 4 + 1];
                        b += px[from * 4 + 2]; n++;
                    }
                    if (n == 0)
                        continue;
                    painted.Add((to, r / n, g / n, b / n));
                    next.Add(to);
                }
            }
            // Written after the round rather than inside it, so a texel filled
            // this round is not a source for its neighbour in the same one -
            // otherwise the colour smears along the walk order.
            foreach ((int at, int r, int g, int b) in painted)
            {
                if (known[at])
                    continue;
                px[at * 4] = (byte)r;
                px[at * 4 + 1] = (byte)g;
                px[at * 4 + 2] = (byte)b;
            }
            foreach ((int at, int _, int _, int _) in painted)
                known[at] = true;
            edge = next;
        }
        return Image.CreateFromData(wide, tall, false, Image.Format.Rgba8, px);
    }

    public string NameOf(int i) => _props[Wrap(i)].Name;

    /// <summary>Which rules this prop plays by. See <see cref="PropTier"/>.
    /// </summary>
    public PropTier TierOf(int i) => _props[Wrap(i)].Tier;

    /// <summary>The species in one tier, as indices into the flat list. The
    /// sower picks among these and never across them: a spot that fits no bush
    /// is a spot with no bush on it, not a spot that gets a tree instead.
    /// </summary>
    public IReadOnlyList<int> Species(PropTier tier) =>
        Enumerable.Range(0, _props.Count).Where(i => _props[i].Tier == tier).ToList();

    public bool Has(string tierName) =>
        _tiers.Any(t => string.Equals(t.Name, tierName,
                                      StringComparison.OrdinalIgnoreCase));


    public Texture2D ArtOf(int i, bool mirrored) =>
        mirrored ? _props[Wrap(i)].Mirror : _props[Wrap(i)].Art;

    public Vector2 FootOf(int i, bool mirrored) =>
        mirrored ? _props[Wrap(i)].MirrorFoot : _props[Wrap(i)].Foot;

    public Vector2 SizeOf(int i) => _props[Wrap(i)].Size;

    /// <summary>Whether this kind has an art for what it looks like burnt out. A
    /// kind without one does not stop burning - it chars and stays in the shape
    /// it grew in, which is <c>EffectLayer.StandIn</c>'s rule: a board whose art
    /// predates the state is a board that loses nothing.</summary>
    public bool HasBurnt(int i) => _props[Wrap(i)].Burnt is not null;

    /// <summary>The picture for one state of one kind. Falls back to the living
    /// art, so a caller that asks for a state nobody drew gets a tree rather
    /// than nothing.</summary>
    public Texture2D ArtOf(int i, bool mirrored, bool burnt)
    {
        Coat? coat = burnt ? _props[Wrap(i)].Burnt : null;
        return coat is null ? ArtOf(i, mirrored)
                            : mirrored ? coat.Mirror : coat.Art;
    }

    public Vector2 FootOf(int i, bool mirrored, bool burnt)
    {
        Coat? coat = burnt ? _props[Wrap(i)].Burnt : null;
        return coat is null ? FootOf(i, mirrored)
                            : mirrored ? coat.MirrorFoot : coat.Foot;
    }

    public Vector2 SizeOf(int i, bool burnt)
    {
        Coat? coat = burnt ? _props[Wrap(i)].Burnt : null;
        return coat is null ? SizeOf(i) : coat.Size;
    }

    /// <summary>The state's own two measurements, for the note and the checks -
    /// never for placement, which is settled by the living art before anything
    /// catches. See the class remarks.</summary>
    public (float Rise, float Root) BurntSpanOf(int i)
    {
        Coat? coat = _props[Wrap(i)].Burnt;
        return coat is null ? (0.0f, 0.0f) : (coat.Rise, coat.Root);
    }

    /// <summary>Height above the foot, in image px. Multiply by the draw scale
    /// to get what the clearance rule is written in.</summary>
    public float RiseOf(int i) => _props[Wrap(i)].Rise;

    /// <summary>Half the ground contact's width, in image px. Unchanged by
    /// mirroring - the foot moves with the picture, the extent around it does
    /// not.</summary>
    public float RootOf(int i) => _props[Wrap(i)].Root;

    /// <summary>Half the footprint's width, in image px. See
    /// <see cref="SpreadOf"/>: the contact says what touches, this says what
    /// lies on the ground, and on anything but a tree they are different
    /// numbers.</summary>
    public float SpreadOf(int i) => _props[Wrap(i)].Spread;

    private int Wrap(int i) => _props.Count == 0 ? 0 : ((i % _props.Count) + _props.Count) % _props.Count;
}
