using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The hand-drawn ground: one PNG per kind of hex, read off an absolute path
/// the way the atlases are, so redrawing one and restarting shows it.
///
/// Everything here rests on one arrangement, and it is the artist's rather than
/// this code's: <b>every terrain is painted on the same frame</b>. One file is
/// the template - the bare plate, nothing on it - and each kind is that file
/// with something drawn over it. So a kind does not carry its own geometry;
/// they all share the template's, and the only measurement taken is of the
/// template.
///
/// That is what makes overhang free. Grass that pokes above the plate's top
/// edge, a tuft that hangs off the front, a boulder wider than the hexagon: all
/// of it just extends past the plate and lands wherever the frame puts it,
/// because the frame is what is aligned, not the silhouette. Measuring each
/// kind's own opaque box instead would move every cell by however much its
/// grass grew.
///
/// Two guards, and both reject rather than repair:
///
/// <list type="bullet">
/// <item><b>A kind must be the template's frame, or a whole multiple of it.</b>
/// A file of any other size was drawn against another template, and there is no
/// way to line it up that is not a guess. This is what keeps
/// <c>HexTerrain.png</c> out - 1254 px against 256, and see below for the other
/// half of why. A whole multiple is the one size that is not a guess: 1024
/// against 256 is the same drawing at four times the detail, so every point of
/// it - plate, overhang, centre - is four times the template's, and dividing the
/// draw scale by four puts all of them back where the template said. Anything
/// between (300, 512x480) is a different frame wearing a plausible number.</item>
/// <item><b>The template must agree with the rendered tile's camera.</b> The
/// hexagon's flat-to-flat over vertex-to-vertex is <c>(sqrt(3)/2)*sin(elevation)</c>,
/// so the ratio *is* the camera angle. The project renders at 30 degrees, giving
/// 0.433; art drawn at 36 gives 0.513. <see cref="HexField.CellAt"/> inverts the
/// rendered tile's size, so ground at the wrong angle does not merely look
/// wrong - the hexagon you see stops being the cell you click, and the miss
/// grows toward the board's edge. <c>HexTerrain.png</c> measured 1151 x 590,
/// which is 36.3 degrees. Cheaper to squash the art by 0.845 than to re-render
/// sixteen layers of three tanks.</item>
/// </list>
///
/// Nothing here is gameplay. A kind is paint: it does not cost movement, block
/// a lane or stop a shell. When it does, this is where the table goes.
/// </summary>
public sealed class TerrainSet
{
    /// <summary>The file every other one is painted over. Doubles as a kind in
    /// its own right - bare ground - because that is honestly what it is, and a
    /// board painted with it is the plain plate the bench had before.</summary>
    public const string TemplateFile = "hex_atlas";

    /// <summary>What the template is called when it is being a kind.</summary>
    public const string Plain = "plain";

    /// <summary>The paint that is not one kind: every cell picks its own.</summary>
    public const string Mixed = "mixed";

    /// <summary>The kind that is not a picture: soil with trees standing on it.
    /// Named here because this is where kinds are named, but it has no file and
    /// <see cref="Has"/> is false for it - see <see cref="HexField.KindAt"/>.
    /// </summary>
    public const string Forest = "forest";

    /// <summary>
    /// What a rise is drawn on, when there is art for it.
    ///
    /// <b>Two independent statements make a hill, and this is the second
    /// one.</b> A cell being a level up is the whole of what a hill does - the
    /// walls, the ramp, the climb - and it needs no art at all. Rougher ground on
    /// top is what a hill looks like, and it comes free with the file: the ride
    /// is measured off the picture's own grain (<see cref="RideOf"/>), so
    /// exporting this plate makes a rise ride rough without a line of code.
    ///
    /// <b>It is not on disk today</b> - only <c>soil_hill.psd</c> is - and that
    /// is why <see cref="HexField.KindAt"/> falls through when a named kind did
    /// not load rather than drawing a blank. A map that asked for a plate nobody
    /// exported has to come up as ground, not as a hole.
    /// </summary>
    public const string Rise = "soil_hill";

    /// <summary>What a board comes up as. A kind rather than the mix, because
    /// the mix is two kinds until somebody draws a third and it reads as two
    /// biomes rather than as one ground with variety in it. Falls back to the
    /// mix with a warning if that file is not on disk - see
    /// <see cref="Main"/> - so naming a kind here cannot leave a blank board.
    /// </summary>
    public const string Default = "soil";

    /// <summary>How far the art's hexagon may be off the rendered tile's before
    /// it is refused. Two percent is well inside drawing slop and nowhere near
    /// the 17% a six-degree camera error produces, so it separates the two cases
    /// without having to be tuned.</summary>
    private const float CameraTolerance = 0.02f;

    private readonly Dictionary<string, Texture2D> _art = new();
    private readonly Dictionary<string, int> _detail = new();
    private readonly Dictionary<string, float> _grain = new();
    private readonly List<string> _names = new();

    /// <summary>The kinds that loaded, template first.</summary>
    public IReadOnlyList<string> Names => _names;

    /// <summary>
    /// The kinds a mixed board draws from: everything but the bare template,
    /// unless the template is all there is.
    ///
    /// It is a kind - a board painted with it is the plain plate the bench had
    /// before drawn ground existed - but it is not one of the kinds a *map* is
    /// made of. Its job is to be the frame every other kind is aligned to, and
    /// a mix that scattered it among drawn ground would read as ground that
    /// failed to draw rather than as a kind of terrain. Still selectable by
    /// name, which is where it is actually wanted: judging one kind against the
    /// plate it was painted over.
    /// </summary>
    public IReadOnlyList<string> Mixable =>
        _names.Count > 1 ? _names.Where(n => n != Plain).ToList() : _names;

    public bool Any => _names.Count > 0;

    /// <summary>The template's opaque box - the plate, in image pixels. Its
    /// centre is the point that lands on a cell's centre.</summary>
    public Rect2I Plate { get; private set; }

    /// <summary>The frame every kind is painted on.</summary>
    public Vector2I Frame { get; private set; }

    /// <summary>What happened while loading, for the log and the panel. Files
    /// that were refused say so by name: a terrain that silently does not appear
    /// reads as a bug in the field.</summary>
    public string Note { get; private set; } = "no terrain art";

    private TerrainSet() { }

    public static TerrainSet Load(string root)
    {
        var set = new TerrainSet();
        if (!Directory.Exists(root))
        {
            set.Note = $"no terrain at {root}";
            return set;
        }

        string templatePath = Path.Combine(root, TemplateFile + ".png");
        Image? template = File.Exists(templatePath)
            ? Image.LoadFromFile(templatePath) : null;
        if (template is null)
        {
            set.Note = $"no template {TemplateFile}.png in {root}";
            return set;
        }

        set.Frame = new Vector2I(template.GetWidth(), template.GetHeight());
        set.Plate = template.GetUsedRect();
        set.Add(Plain, template, 1);

        var refused = new List<string>();
        foreach (string path in Directory.GetFiles(root, "*.png").OrderBy(p => p))
        {
            string name = Path.GetFileNameWithoutExtension(path);
            if (name == TemplateFile)
                continue;
            Image? art = Image.LoadFromFile(path);
            if (art is null)
            {
                refused.Add($"{name} unreadable");
                continue;
            }
            var frame = new Vector2I(art.GetWidth(), art.GetHeight());
            int detail = DetailOf(frame, set.Frame);
            if (detail == 0)
            {
                refused.Add($"{name} is {frame.X}x{frame.Y}, not {set.Frame.X}"
                            + $"x{set.Frame.Y} or a whole multiple of it");
                continue;
            }
            set.Add(name, art, detail);
        }

        set.Note = $"{set._names.Count} kinds ({string.Join(", ", set.Listed())})"
                   + $" on a {set.Frame.X}px frame, plate {set.Plate.Size.X}"
                   + $"x{set.Plate.Size.Y}"
                   + (refused.Count == 0 ? "" : "; refused " + string.Join("; ", refused));
        return set;
    }

    /// <summary>How many times the template's frame this one is, or 0 if it is
    /// not a whole multiple of it in both axes by the same factor. Both axes and
    /// the same factor, because a file that is 4x wide and 2x tall is not the
    /// same drawing at more detail - it is a different hexagon, and the aspect
    /// ratio is the camera angle.</summary>
    private static int DetailOf(Vector2I frame, Vector2I template)
    {
        if (template.X <= 0 || template.Y <= 0)
            return 0;
        if (frame.X % template.X != 0 || frame.Y % template.Y != 0)
            return 0;
        int x = frame.X / template.X;
        return x == frame.Y / template.Y ? x : 0;
    }

    /// <summary>
    /// How grainy a kind's art is, in levels of luminance per pixel step.
    ///
    /// <b>Measured off the art rather than declared in a table, and that is what
    /// keeps a new hex a file drop.</b> A table of names would have to be edited
    /// every time somebody paints one, and a kind missing from it rides like
    /// nothing in particular - the same quiet failure a paint list built by hand
    /// would be. This asks the picture: ground drawn coarse rides coarse.
    ///
    /// Neighbouring pixels, sampled every fourth: the differences are between
    /// adjacent pixels because that is what grain is, while which pairs get asked
    /// is a sixteenth of them because the answer is a mean over a megapixel.
    /// Transparent pixels sit out - a hexagon is a third of its own frame.
    /// </summary>
    private static float Grain(Image art)
    {
        var probe = (Image)art.Duplicate();
        if (probe.GetFormat() != Image.Format.Rgba8)
            probe.Convert(Image.Format.Rgba8);
        byte[] px = probe.GetData();
        int w = probe.GetWidth(), h = probe.GetHeight();
        double sum = 0.0;
        long pairs = 0;
        for (int y = 0; y + 1 < h; y += 4)
        for (int x = 0; x + 1 < w; x += 4)
        {
            int i = (y * w + x) * 4;
            if (px[i + 3] < 200)
                continue;
            double here = Luma(px, i);
            int right = i + 4, down = i + w * 4;
            if (px[right + 3] >= 200)
            {
                sum += Math.Abs(here - Luma(px, right));
                pairs++;
            }
            if (down + 3 < px.Length && px[down + 3] >= 200)
            {
                sum += Math.Abs(here - Luma(px, down));
                pairs++;
            }
        }
        return pairs == 0 ? 0.0f : (float)(sum / pairs);
    }

    private static double Luma(byte[] px, int i) =>
        0.2126 * px[i] + 0.7152 * px[i + 1] + 0.0722 * px[i + 2];

    /// <summary>The smoothest ride any ground gives, and how many levels of
    /// grain per pixel step buy a full one on top of it. Measured on the three
    /// kinds on disk: the rendered plain tile grains 0.3 and rides 0.76, soil
    /// grains 5.9 and rides 1.00, the hill grains 13.0 and rides 1.29. The cap is
    /// a guard against art nobody has drawn yet rather than the operating point -
    /// nothing here reaches it.</summary>
    public const float SmoothRide = 0.75f;
    public const float RideGrain = 24.0f;
    public const float RoughRide = 1.45f;

    /// <summary>How rough a ride a kind of ground gives, as a multiplier on the
    /// jolt. 1.0 for anything with no art behind it - a forest cell is soil with
    /// trees on it, and a kind nobody has painted has nothing to measure.
    /// </summary>
    /// <summary>The grain itself, for the check that the ride follows it and for
    /// the note. Nought for a kind with no art.</summary>
    public float GrainOf(string name) =>
        _grain.TryGetValue(name, out float grain) ? grain : 0.0f;

    public float RideOf(string name) =>
        _grain.TryGetValue(name, out float grain)
            ? Math.Min(SmoothRide + grain / RideGrain, RoughRide)
            : 1.0f;

    private void Add(string name, Image image, int detail)
    {
        // Before the mipmaps and before the texture: GenerateMipmaps changes the
        // image in place, and the grain of the ground is the grain of the ground
        // as drawn.
        _grain[name] = Grain(image);
        // Mipmaps for anything that will be drawn smaller than it was painted.
        // Without them a 4x kind is point-sampled down to a quarter and the
        // gravel crawls as the board pans - the ground reads as noise rather
        // than as ground, which is the one thing the hand-drawn art is for.
        if (detail > 1)
            image.GenerateMipmaps();
        _art[name] = ImageTexture.CreateFromImage(image);
        _detail[name] = detail;
        _names.Add(name);
    }

    public bool Has(string name) => _art.ContainsKey(name);

    public Texture2D? Texture(string name) =>
        _art.TryGetValue(name, out Texture2D? art) ? art : null;

    /// <summary>How many times the template's frame this kind was painted at.
    /// Divides the draw scale, and is 1 for art drawn at the template's own
    /// size.</summary>
    public int Detail(string name) =>
        _detail.TryGetValue(name, out int detail) ? detail : 1;

    /// <summary>Anything drawn finer than the template, so the filter can be
    /// told and the note can say so.</summary>
    public bool AnyDetailed => _detail.Values.Any(d => d > 1);

    /// <summary>What a kind is called in the note, with the two numbers that
    /// were measured off it. In the trace rather than nowhere, for the reason
    /// every other loaded number is: a ride that came out wrong is otherwise a
    /// tank that feels odd on some cells.</summary>
    private IEnumerable<string> Listed() =>
        _names.Select(n => (Detail(n) > 1 ? $"{n} x{Detail(n)}" : n)
                           + $" ride {RideOf(n):F2}");

    /// <summary>What the art has to be multiplied by to sit on the rendered
    /// tile. Off the width, because the width is the load-bearing number: the
    /// column spacing is 0.75 of it, so a scale taken from anything else puts
    /// gaps between cells.</summary>
    public float ScaleTo(Rect2I hexRect) =>
        Plate.Size.X <= 0 ? 1.0f : hexRect.Size.X / (float)Plate.Size.X;

    /// <summary>How far the art's camera is from the rendered tile's, as a
    /// fraction of the latter. See the class remarks: this is an angle wearing
    /// the clothes of an aspect ratio.</summary>
    public float CameraError(Rect2I hexRect)
    {
        if (Plate.Size.X <= 0 || hexRect.Size.X <= 0 || hexRect.Size.Y <= 0)
            return 0.0f;
        float art = Plate.Size.Y / (float)Plate.Size.X;
        float rendered = hexRect.Size.Y / (float)hexRect.Size.X;
        return Mathf.Abs(art - rendered) / rendered;
    }

    public bool CameraAgrees(Rect2I hexRect) =>
        CameraError(hexRect) <= CameraTolerance;

    /// <summary>
    /// Where to draw a kind so its plate is centred on a cell.
    ///
    /// The same rectangle whatever the kind's detail, and that is the whole
    /// argument for allowing whole multiples: a 4x kind is 4x the frame drawn at
    /// a quarter of the scale, so the destination is the template's frame times
    /// the template's scale either way. Detail buys pixels inside the rectangle
    /// and moves nothing.
    /// </summary>
    public Rect2 RectAt(Vector2 cellCentre, float scale)
    {
        Vector2 centre = ((Vector2)Plate.Position + (Vector2)Plate.Size * 0.5f) * scale;
        return new Rect2(cellCentre - centre, (Vector2)Frame * scale);
    }

    /// <summary>
    /// The kind a cell gets when the board is mixed.
    ///
    /// Hashed off the cell rather than drawn from a generator, for the reason
    /// the shell scatter is: --capture and --trace fix the time step so two runs
    /// can be diffed, and a board that reshuffled itself would be measuring
    /// itself. It also means no map has to be stored to have one.
    /// </summary>
    public string MixedAt(Vector2I cell) => MixedAt(cell, null);

    /// <summary>The same, with one kind that has no file thrown into the hat -
    /// the forest. Passed in rather than held here, because a set of drawn
    /// ground has no business knowing what else the board can be made of.
    /// </summary>
    public string MixedAt(Vector2I cell, string? extra)
    {
        IReadOnlyList<string> pool = Mixable;
        int kinds = pool.Count + (extra is null ? 0 : 1);
        if (kinds == 0)
            return Plain;
        int h = cell.X * 73856093 ^ cell.Y * 19349663;
        h = (h ^ (h >> 13)) * 1274126177;
        h ^= h >> 16;
        int pick = (int)((uint)h % (uint)kinds);
        return pick < pool.Count ? pool[pick] : extra!;
    }
}
