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
/// </summary>
public sealed class PropSet
{
    /// <summary>
    /// How many times the on-screen scale the props are painted at.
    ///
    /// Declared rather than measured, because a prop has no frame to compare
    /// against - it is not painted on the template the way a terrain kind is.
    /// Four, to match the hand-drawn ground: <c>soil.png</c> is a 4x kind, so
    /// art drawn beside it in the same document is at the same scale. Tree_4 is
    /// 446px tall, which is 111px on screen - against a 109px hexagon and a
    /// 131px tank, that is a tree.
    /// </summary>
    public const int Detail = 4;

    /// <summary>How much of the sprite's height counts as the base. Six percent
    /// is 27px on Tree_4, which is where the roots have finished splaying and
    /// the trunk is still a trunk.</summary>
    private const float RootBand = 0.06f;

    private sealed class Prop
    {
        public string Name = "";
        public Texture2D Art = null!;
        public Texture2D Mirror = null!;
        public Vector2 Foot;        // image px, in the unmirrored art
        public Vector2 MirrorFoot;
        public Vector2 Size;        // image px
        public float Rise;          // image px above the foot
        public float RootLeft;      // image px the base reaches left of the foot
        public float RootRight;
    }

    private readonly List<Prop> _props = new();

    public IReadOnlyList<string> Names => _props.Select(p => p.Name).ToList();
    public int Count => _props.Count;
    public bool Any => _props.Count > 0;

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
        foreach (string path in Directory.GetFiles(root, "*.png").OrderBy(p => p))
        {
            string name = Path.GetFileNameWithoutExtension(path);
            Image? art = Image.LoadFromFile(path);
            if (art is null)
            {
                refused.Add($"{name} unreadable");
                continue;
            }
            Rect2I box = art.GetUsedRect();
            if (box.Size.X <= 0 || box.Size.Y <= 0)
            {
                refused.Add($"{name} is empty");
                continue;
            }
            set.Add(name, art, box);
        }

        set.Note = set._props.Count == 0
            ? $"no props in {root}"
            : $"{set._props.Count} props ({string.Join(", ", set._props.Select(
                p => $"{p.Name} {p.Size.X}x{p.Size.Y} rise {p.Rise:F0}"))})"
              + $" at x{Detail}"
              + (refused.Count == 0 ? "" : "; refused " + string.Join("; ", refused));
        return set;
    }

    private void Add(string name, Image art, Rect2I box)
    {
        Vector2 foot = FootOf(art, box);
        var mirror = (Image)art.Duplicate();
        mirror.FlipX();

        // Mirroring is about the image's own centre line, so the foot goes with
        // it: a trunk 58px from the left is 58px from the right afterwards.
        var mirrorFoot = new Vector2(art.GetWidth() - 1 - foot.X, foot.Y);

        (float left, float right) = RootReach(art, box, foot);

        art.GenerateMipmaps();
        mirror.GenerateMipmaps();
        _props.Add(new Prop
        {
            Name = name,
            Art = ImageTexture.CreateFromImage(art),
            Mirror = ImageTexture.CreateFromImage(mirror),
            Foot = foot,
            MirrorFoot = mirrorFoot,
            Size = new Vector2(art.GetWidth(), art.GetHeight()),
            Rise = foot.Y - box.Position.Y + 1,
            RootLeft = left,
            RootRight = right,
        });
    }

    /// <summary>
    /// How far the base reaches either side of the foot, measured over the
    /// bottom <see cref="RootBand"/> of the sprite.
    ///
    /// Two numbers rather than a radius, because the flare is not centred on
    /// the trunk: Tree_4 reaches 12px left of its foot and 73px right, six
    /// times as far. A radius would have to be the larger one, and a tree kept
    /// 73px clear on both sides stands further into the cell than it needs on
    /// the side where it has no roots.
    ///
    /// Horizontal only. The sprite's vertical axis mixes height above the
    /// ground with depth into it, and nothing in a flat image can separate the
    /// two - so the reach that can be measured is measured, and the one that
    /// cannot is not guessed at.
    /// </summary>
    private static (float Left, float Right) RootReach(Image art, Rect2I box,
                                                       Vector2 foot)
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
            return (0.0f, 0.0f);
        return (Math.Max(0.0f, foot.X - least), Math.Max(0.0f, most - foot.X));
    }

    /// <summary>The centre of the bottom opaque row. See the class remarks for
    /// why not the box centre.</summary>
    private static Vector2 FootOf(Image art, Rect2I box)
    {
        int bottom = box.Position.Y + box.Size.Y - 1;
        int first = -1, last = -1;
        for (int x = box.Position.X; x < box.Position.X + box.Size.X; x++)
        {
            if (art.GetPixel(x, bottom).A <= 0.03f)
                continue;
            if (first < 0)
                first = x;
            last = x;
        }
        if (first < 0)
            return new Vector2(box.Position.X + box.Size.X * 0.5f, bottom);
        return new Vector2((first + last) * 0.5f, bottom);
    }

    public string NameOf(int i) => _props[Wrap(i)].Name;

    public Texture2D ArtOf(int i, bool mirrored) =>
        mirrored ? _props[Wrap(i)].Mirror : _props[Wrap(i)].Art;

    public Vector2 FootOf(int i, bool mirrored) =>
        mirrored ? _props[Wrap(i)].MirrorFoot : _props[Wrap(i)].Foot;

    public Vector2 SizeOf(int i) => _props[Wrap(i)].Size;

    /// <summary>Height above the foot, in image px. Multiply by the draw scale
    /// to get what the clearance rule is written in.</summary>
    public float RiseOf(int i) => _props[Wrap(i)].Rise;

    /// <summary>How far the base reaches either side of the foot, in image px.
    /// Mirroring swaps them, for the reason the foot moves: the picture is
    /// reversed, so left and right are the other way round.</summary>
    public (float Left, float Right) RootOf(int i, bool mirrored) =>
        mirrored ? (_props[Wrap(i)].RootRight, _props[Wrap(i)].RootLeft)
                 : (_props[Wrap(i)].RootLeft, _props[Wrap(i)].RootRight);

    private int Wrap(int i) => _props.Count == 0 ? 0 : ((i % _props.Count) + _props.Count) % _props.Count;
}
