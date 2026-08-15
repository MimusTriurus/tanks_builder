using System;
using System.IO;
using System.Linq;
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
/// need a frame, an anchor and a plate rect; a water frame is cut to its plate,
/// so the hexagon *is* the picture and a corner of the mesh maps to a corner of
/// the frame. Airborne spray is cropped away with the rest of the overhang -
/// water standing above the surface is a different effect and does not belong to
/// the plane. See <c>water_sheet.py</c>, where that is measured and cut.
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

    /// <summary>
    /// How many sea states the strip is divided into: calm, chop, breaking.
    ///
    /// <b>Declared, because the pixels cannot say it.</b> Everything else here is
    /// measured off the file - the frame count, the camera, the mean, the edge -
    /// and this one is what the bands *mean*, which is a fact about the drawing
    /// and not about its pixels. What the pixels do say is that it is true: the
    /// spread of each frame climbs 13, 15, 21, 22 / 24, 31, 38, 40 / 38, 41, 45,
    /// 47, and the near-white share 0.0% to 5.2%, monotonically, in three groups
    /// of four.
    ///
    /// And the split is not decoration: the twelve frames <b>do not close</b> as
    /// one loop - wrap 41.5 against a worst normal step of 33.2 - while each
    /// quartet does, at 1.14, 1.11 and 1.21. So the loop is a quarter of the
    /// strip and the band is the axis across it.
    /// </summary>
    public const int Bands = 3;

    /// <summary>How many frames the strip holds. One would be a still, and a
    /// still is not a loop - see <see cref="Any"/>.</summary>
    public int Frames { get; private set; }

    /// <summary>The length of one loop: the strip divided by its bands.</summary>
    public int Loop => Frames / Bands;

    /// <summary>One frame, in image px.</summary>
    public Vector2I Frame { get; private set; }

    /// <summary>The calm band's average, linear, ready to be a shader uniform -
    /// what a wading tank in still water is tinted with. See
    /// <see cref="MeanOfBand"/> for why it is per band and not one number for the
    /// strip.</summary>
    public Color Mean => MeanOfBand(0);

    private Color[] _mean = Array.Empty<Color>();

    /// <summary>
    /// A band's average, linear.
    ///
    /// <b>Per band, because the tint has to be the water the tank is actually
    /// standing in.</b> The tint stands in for a surface the sprite's shader
    /// cannot sample, so the whole of its job is to be that surface's colour -
    /// and the bands are 0.038/0.114/0.110 calm against 0.089/0.184/0.181
    /// breaking, which is not a rounding difference. One number for the strip
    /// would leave a tank in a churning cell visibly darker than the water
    /// around it, which reads as the tank being in shadow rather than in water.
    /// </summary>
    public Color MeanOfBand(int band) =>
        _mean.Length == 0 ? Stage3D.Pond
                          : _mean[Mathf.Clamp(band, 0, _mean.Length - 1)];

    /// <summary>
    /// The least alpha the mesh can sample anywhere on its own boundary, once
    /// <see cref="Stage3D.SwellBleed"/> has pulled it in.
    ///
    /// <b>The number the pond broke on.</b> A frame is exactly the plate, so the
    /// hexagon's keyed rim lies on the frame's own edge - and where two cells meet,
    /// two of those fades land on top of each other. Shipped as cut, the outermost
    /// column was alpha 15 of 255 and the five-cell pond drew as three separate
    /// pieces with dry ground between them. Nothing in the picture said so except
    /// the picture.
    ///
    /// Measured over the hexagon's six vertices and six edge midpoints, which is
    /// exactly the boundary a neighbour is on the other side of.
    /// </summary>
    public float EdgeAlpha { get; private set; }

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
        if (frames % Bands != 0)
        {
            set.Note = $"{File}.png is {frames} frames, which is not {Bands} bands"
                       + " of a whole loop - refused, see water_sheet.py";
            return set;
        }

        set.Frames = frames;
        set.Frame = new Vector2I(w / frames, h);
        set._mean = new Color[Bands];
        for (int b = 0; b < Bands; b++)
            set._mean[b] = MeanOf(art, b * set.Loop * set.Frame.X,
                                  set.Loop * set.Frame.X);
        set.EdgeAlpha = RimOf(art, set.Frame, frames);
        art.GenerateMipmaps();
        set.Texture = ImageTexture.CreateFromImage(art);
        set.Note = $"{File} {w}x{h}, {Bands} bands of {set.Loop} frames of "
                   + $"{set.Frame.X}x{set.Frame.Y}, means "
                   + string.Join("/", set._mean.Select(c => c.ToHtml(false)))
                   + $", edge alpha {set.EdgeAlpha:F2}";
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
    /// <summary>
    /// The worst alpha on the drawn boundary of every frame, after the bleed.
    ///
    /// The twelve points are the flat-top hexagon's own: six vertices at
    /// <c>(±0.5, 0)</c> and <c>(±0.25, ±0.5)</c> of the frame, and the six edge
    /// midpoints between them. In frame-local terms that is exactly what
    /// <see cref="Stage3D"/> hands the mesh, so this asks the question the mesh
    /// asks and not a similar one.
    /// </summary>
    private static float RimOf(Image art, Vector2I frame, int frames)
    {
        // Straight out of the frame's own size, with no trigonometry in it: a
        // flat-top hexagon drawn on a plate has its vertices at (+-w/2, 0) and
        // (+-w/4, +-h/2), and the plate is already the camera's squash. The same
        // statement SelectionRing makes about the tile.
        var hex = new[]
        {
            new Vector2(0.5f, 0.0f), new Vector2(0.25f, 0.5f),
            new Vector2(-0.25f, 0.5f), new Vector2(-0.5f, 0.0f),
            new Vector2(-0.25f, -0.5f), new Vector2(0.25f, -0.5f),
        };
        var inset = new Vector2(1.0f - 2.0f * Stage3D.SwellBleed / frame.X,
                                1.0f - 2.0f * Stage3D.SwellBleed / frame.Y);
        float worst = 1.0f;
        for (int f = 0; f < frames; f++)
        for (int i = 0; i < 6; i++)
        foreach (Vector2 p in new[] { hex[i], (hex[i] + hex[(i + 1) % 6]) * 0.5f })
        {
            var at = new Vector2I(
                Mathf.Clamp(f * frame.X
                            + Mathf.RoundToInt((0.5f + inset.X * p.X) * frame.X),
                            f * frame.X, (f + 1) * frame.X - 1),
                Mathf.Clamp(Mathf.RoundToInt((0.5f + inset.Y * p.Y) * frame.Y),
                            0, frame.Y - 1));
            worst = Mathf.Min(worst, art.GetPixel(at.X, at.Y).A);
        }
        return worst;
    }

    private static Color MeanOf(Image art, int from, int wide)
    {
        double r = 0.0, g = 0.0, b = 0.0;
        long n = 0;
        for (int y = 0; y < art.GetHeight(); y += MeanStride)
        for (int x = from; x < from + wide; x += MeanStride)
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
