using System;
using System.IO;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The drawn surface of the water: one strip of frames, read off an absolute
/// path the way the atlases, the terrain and the props are.
///
/// <b>It is not a <see cref="TerrainSet"/> kind, and the line is the one the
/// water map already draws.</b> A kind is paint - picked by hash, costing
/// nothing - and water costs a speed ceiling, refuses trees and holds a tank up
/// to its belly. Putting the surface in with the ground would also put it in
/// the mix, which is a pond appearing on a hilltop.
///
/// <b>A frame is exactly the plate, and that is what makes this small.</b> The
/// terrain kinds are painted on a template with room to overhang it, so they
/// need a frame, an anchor and a plate rect; the calm frames of the water sheet
/// carry no spray at all, so the hexagon *is* the picture and a corner of the
/// mesh maps to a corner of the frame. See <c>water_sheet.py</c>, which is
/// where that was measured and where the four frames are cut.
///
/// <b>The frame count and the camera check are one question.</b> Nothing in the
/// file says how many frames it holds, and a sidecar saying so would be a
/// second copy of something the pixels answer: a frame has to be the hexagon's
/// own shape, so the count is the width over the height over the tile's aspect,
/// and art at the wrong camera does not divide into a whole number of them. It
/// is not airtight - a wrong camera could land on a whole number by luck - but
/// it refuses by name, which is the half that matters. The sheet as delivered
/// is drawn at 36.4 degrees against the board's 30, so this is not a
/// hypothetical: it is the same defect already recorded against
/// <c>HexTerrain.png</c>, and <c>water_sheet.py</c> squashes it by 0.843.
///
/// <b>The mean colour is measured here rather than declared.</b> A tank wading
/// tints its own submerged half with a flat colour, because the waterline is
/// cut inside its shader and a billboard does not know where in the world its
/// lower half is - so the tint cannot sample the surface it stands in for. What
/// it can do is be that surface's average, and taking the average off the strip
/// that is actually loaded means the tint cannot go stale against a redrawn
/// sheet. <see cref="Stage3D.Pond"/> stays as the colour for a board with no
/// art on disk, and the self-test holds the two together.
/// </summary>
public sealed class WaterArt
{
    /// <summary>The strip <c>water_sheet.py</c> writes.</summary>
    public const string File = "pond";

    /// <summary>How far the art's hexagon may be off the rendered tile's, as
    /// <see cref="TerrainSet"/> puts it. Read here as how far from whole the
    /// frame count may land.</summary>
    private const float CameraTolerance = 0.02f;

    /// <summary>Every other pixel in both axes, which is 90k samples across the
    /// shipped strip. The mean of a photograph of water does not move in the
    /// fourth decimal for want of the other three quarters.</summary>
    private const int MeanStride = 2;

    private WaterArt() { }

    public Texture2D? Texture { get; private set; }

    /// <summary>How many frames the strip holds. One would be a still, and a
    /// still is not a loop - see <see cref="Any"/>.</summary>
    public int Frames { get; private set; }

    /// <summary>One frame, in image px.</summary>
    public Vector2I Frame { get; private set; }

    /// <summary>The surface's average, linear, ready to be a shader uniform.
    /// This is what a wading tank is tinted with.</summary>
    public Color Mean { get; private set; }

    public string Note { get; private set; } = "no water art";

    /// <summary>Usable as a moving surface. Two frames is the least that can
    /// be a loop; anything fewer is a picture, and the flat colour says the
    /// same thing for less.</summary>
    public bool Any => Texture is not null && Frames > 1;

    /// <summary>
    /// Read the strip and work out how many frames it is, against the tile the
    /// tanks were rendered on.
    ///
    /// The tile is a parameter rather than something reached for, because it is
    /// the thing being agreed with: this class has no opinion about the board's
    /// camera, it only refuses art that disagrees with it.
    /// </summary>
    public static WaterArt Load(string root, Rect2I hexRect)
    {
        var set = new WaterArt();
        string path = Path.Combine(root, File + ".png");
        if (!System.IO.File.Exists(path))
        {
            set.Note = $"no {File}.png in {root}";
            return set;
        }
        Image? art = Image.LoadFromFile(path);
        if (art is null)
        {
            set.Note = $"{File}.png unreadable";
            return set;
        }
        int w = art.GetWidth(), h = art.GetHeight();
        if (w <= 0 || h <= 0 || hexRect.Size.X <= 0 || hexRect.Size.Y <= 0)
        {
            set.Note = $"{File}.png is {w}x{h} against a {hexRect.Size.X}"
                       + $"x{hexRect.Size.Y} tile";
            return set;
        }

        // A frame is as tall as the strip and as wide as that height makes a
        // hexagon of the tile's shape, so the count falls out of the two.
        float exact = w * (hexRect.Size.Y / (float)hexRect.Size.X) / h;
        int frames = Mathf.RoundToInt(exact);
        if (frames < 1 || Mathf.Abs(exact - frames) / frames > CameraTolerance)
        {
            set.Note = $"{File}.png is {w}x{h}, which is {exact:F2} frames of the"
                       + $" tile's shape - refused, see water_sheet.py";
            return set;
        }

        set.Frames = frames;
        set.Frame = new Vector2I(w / frames, h);
        set.Mean = MeanOf(art);
        art.GenerateMipmaps();
        set.Texture = ImageTexture.CreateFromImage(art);
        set.Note = $"{File} {w}x{h}, {frames} frames of {set.Frame.X}"
                   + $"x{set.Frame.Y}, mean {set.Mean.ToHtml(false)}";
        return set;
    }

    /// <summary>
    /// The average of the surface, over the solid interior only.
    ///
    /// <b>Solid only, and the soft rim is why.</b> The hexagon is keyed with an
    /// antialiased edge, so its outermost pixels are the surround fading in;
    /// averaging those pulls the mean toward whatever the surround happens to
    /// carry, which on this sheet is black. That would darken the tint under
    /// every wading tank for a reason that has nothing to do with water.
    ///
    /// Converted out of sRGB, because a Godot colour is linear and the texture
    /// is sampled as <c>source_color</c>. Left encoded, the tint would sit
    /// visibly lighter than the surface beside it - which is the exact seam
    /// this number exists to close.
    /// </summary>
    private static Color MeanOf(Image art)
    {
        double r = 0.0, g = 0.0, b = 0.0;
        long n = 0;
        for (int y = 0; y < art.GetHeight(); y += MeanStride)
        for (int x = 0; x < art.GetWidth(); x += MeanStride)
        {
            Color c = art.GetPixel(x, y);
            if (c.A <= 0.98f)
                continue;
            r += c.R;
            g += c.G;
            b += c.B;
            n++;
        }
        if (n == 0)
            return Stage3D.Pond;
        return new Color((float)(r / n), (float)(g / n), (float)(b / n))
            .SrgbToLinear();
    }
}
