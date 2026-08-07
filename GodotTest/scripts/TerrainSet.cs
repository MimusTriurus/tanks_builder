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
/// <item><b>A kind must be the template's frame size.</b> A file of another size
/// was drawn against another template, and there is no way to line it up that is
/// not a guess. This is what keeps <c>HexTerrain.png</c> out - 1254 px against
/// 256, and see below for the other half of why.</item>
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

    /// <summary>How far the art's hexagon may be off the rendered tile's before
    /// it is refused. Two percent is well inside drawing slop and nowhere near
    /// the 17% a six-degree camera error produces, so it separates the two cases
    /// without having to be tuned.</summary>
    private const float CameraTolerance = 0.02f;

    private readonly Dictionary<string, Texture2D> _art = new();
    private readonly List<string> _names = new();

    /// <summary>The kinds that loaded, template first.</summary>
    public IReadOnlyList<string> Names => _names;

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
        set.Add(Plain, template);

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
            if (frame != set.Frame)
            {
                refused.Add($"{name} is {frame.X}x{frame.Y}, not {set.Frame.X}"
                            + $"x{set.Frame.Y}");
                continue;
            }
            set.Add(name, art);
        }

        set.Note = $"{set._names.Count} kinds ({string.Join(", ", set._names)})"
                   + $" on a {set.Frame.X}px frame, plate {set.Plate.Size.X}"
                   + $"x{set.Plate.Size.Y}"
                   + (refused.Count == 0 ? "" : "; refused " + string.Join("; ", refused));
        return set;
    }

    private void Add(string name, Image image)
    {
        _art[name] = ImageTexture.CreateFromImage(image);
        _names.Add(name);
    }

    public bool Has(string name) => _art.ContainsKey(name);

    public Texture2D? Texture(string name) =>
        _art.TryGetValue(name, out Texture2D? art) ? art : null;

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

    /// <summary>Where to draw a kind so its plate is centred on a cell.</summary>
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
    public string MixedAt(Vector2I cell)
    {
        if (_names.Count == 0)
            return Plain;
        int h = cell.X * 73856093 ^ cell.Y * 19349663;
        h = (h ^ (h >> 13)) * 1274126177;
        h ^= h >> 16;
        return _names[(int)((uint)h % (uint)_names.Count)];
    }
}
