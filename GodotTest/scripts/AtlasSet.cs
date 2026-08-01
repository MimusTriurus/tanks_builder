using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// One tank's rendered output - the hull, turret and hex layers that
/// sprite_atlas.py wrote into Sprites/&lt;TAG&gt;/.
///
/// Loaded from an absolute path at run time instead of being imported as
/// project resources. Re-render the atlases in Blender and the next run picks
/// them up, with no copying and no re-import step; for a harness whose whole
/// job is to look at freshly rendered sprites that matters more than export
/// portability (Image.LoadFromFile does not work from a packed export).
/// </summary>
public sealed class AtlasSet
{
    public static readonly string[] LayerNames = { "hull", "turret", "hex" };

    public string Tag { get; private set; } = "";
    public string Error { get; private set; } = "";

    public Vector2I Tile { get; private set; } = new(256, 256);
    public int Count { get; private set; } = 12;
    public double UnitsPerPixel { get; private set; }

    /// <summary>
    /// Where spin_pivot lands inside a tile, in pixels from its top-left
    /// corner. Every layer shares it - that is what lets the layers be drawn
    /// straight on top of each other - so drawing a tile at
    /// (position - Anchor) puts the tank's rotation axis exactly on
    /// <c>position</c>.
    /// </summary>
    public Vector2 Anchor { get; private set; } = new(128, 128);

    /// <summary>
    /// Opaque bounds of the hex tile. Grid spacing is derived from this rather
    /// than read from metadata, so the field re-tessellates correctly if the
    /// tile is ever re-rendered at a different size. See <see cref="HexField"/>.
    /// </summary>
    public Rect2I HexRect { get; private set; }

    /// <summary>Camera elevation the atlas was rendered at, degrees. Needed to
    /// turn a world heading into a screen direction, and read from the file
    /// rather than assumed so a re-render at another angle still works.</summary>
    public double Elevation { get; private set; } = 30.0;

    /// <summary>Offset from the shared anchor down to the ground plane, in
    /// pixels. The anchor is the turret's rotation axis, which the renderer
    /// places at the mid-height of the fitted bounds, so it floats well above
    /// the ground the tank stands on - about 61px on these atlases. Anything
    /// that needs the contact point rather than the pivot goes through here.</summary>
    public Vector2 GroundOffset => HexRect.Position + (Vector2)HexRect.Size * 0.5f - Anchor;

    /// <summary>Screen-space direction of a world heading on the ground plane.
    /// Not normalised: the isometric view squashes the ground by
    /// sin(elevation), and anything moving along it should shrink to match.</summary>
    public Vector2 GroundDirection(double headingDegrees)
    {
        double rad = Mathf.DegToRad(headingDegrees);
        double squash = Math.Sin(Mathf.DegToRad(Elevation));
        return new Vector2((float)Math.Cos(rad), (float)(-Math.Sin(rad) * squash));
    }

    private readonly Dictionary<string, ImageTexture> _textures = new();
    private readonly Dictionary<string, int> _columns = new();

    /// <summary>
    /// Heading in degrees to frame index, taken from the per-frame labels the
    /// renderer wrote ("side @ 270 deg"). Reading it from the file rather than
    /// assuming front_dir = 270 means a model imported at another heading still
    /// lines up here.
    /// </summary>
    private readonly Dictionary<int, int> _facings = new();

    private AtlasSet() { }

    public static AtlasSet Load(string root, string tag)
    {
        var atlas = new AtlasSet { Tag = tag };
        LayerMeta? hull = null;

        foreach (string layer in LayerNames)
        {
            string basePath = $"{root}/{tag}/{layer}_atlas";
            string jsonPath = basePath + ".json";
            string pngPath = basePath + ".png";

            if (!File.Exists(jsonPath) || !File.Exists(pngPath))
            {
                atlas.Error = $"missing {basePath}.png/.json";
                return atlas;
            }

            LayerMeta? meta;
            try
            {
                meta = JsonSerializer.Deserialize<LayerMeta>(File.ReadAllText(jsonPath));
            }
            catch (JsonException e)
            {
                atlas.Error = $"unreadable json {jsonPath}: {e.Message}";
                return atlas;
            }

            if (meta is null)
            {
                atlas.Error = $"empty json {jsonPath}";
                return atlas;
            }

            Image? image = Image.LoadFromFile(pngPath);
            if (image is null)
            {
                atlas.Error = $"unreadable png {pngPath}";
                return atlas;
            }

            atlas._textures[layer] = ImageTexture.CreateFromImage(image);
            atlas._columns[layer] = Math.Max(1, meta.Grid.Columns);
            if (layer == "hex")
                atlas.HexRect = image.GetUsedRect();
            if (layer == "hull")
                hull = meta;
        }

        if (hull is null)
        {
            atlas.Error = "no hull layer";
            return atlas;
        }

        atlas.Tile = new Vector2I(hull.Tile[0], hull.Tile[1]);
        atlas.Count = Math.Max(1, hull.Count);
        atlas.Anchor = new Vector2((float)hull.AnchorPx[0], (float)hull.AnchorPx[1]);
        atlas.UnitsPerPixel = hull.UnitsPerPixel;
        atlas.Elevation = hull.View.Elevation;
        atlas.ReadFacings(hull);
        return atlas;
    }

    private void ReadFacings(LayerMeta meta)
    {
        foreach (FrameMeta frame in meta.Frames)
        {
            int facing = -1;
            if (!string.IsNullOrEmpty(frame.Label))
            {
                // "side @ 270 deg" / "corner @ 300 deg"
                foreach (string part in frame.Label.Split(' '))
                    if (int.TryParse(part, out int parsed))
                    {
                        facing = parsed;
                        break;
                    }
            }

            if (facing < 0)
                // no labels: fall back to the stated import convention
                facing = (int)Math.Round(Mod(270.0 + frame.Angle, 360.0));

            _facings[Mod(facing, 360)] = frame.Index;
        }
    }

    public Texture2D Texture(string layer) => _textures[layer];

    /// <summary>Frame showing the part pointing at <paramref name="facing"/>
    /// degrees, snapped to the nearest heading that was actually rendered.</summary>
    public int FrameFor(double facing)
    {
        double step = 360.0 / Count;
        int snapped = Mod((int)Math.Round(Mod(facing, 360.0) / step) * (int)Math.Round(step), 360);
        if (_facings.TryGetValue(snapped, out int index))
            return index;

        // labels did not land on the step grid - take the nearest by angle
        int best = 0;
        double bestGap = double.MaxValue;
        foreach ((int key, int value) in _facings)
        {
            double gap = Math.Abs(Mod(key - facing + 180.0, 360.0) - 180.0);
            if (gap < bestGap)
            {
                bestGap = gap;
                best = value;
            }
        }
        return best;
    }

    public Rect2 Region(string layer, int index)
    {
        int columns = _columns[layer];
        return new Rect2(
            new Vector2(index % columns * Tile.X, index / columns * Tile.Y),
            Tile);
    }

    /// <summary>The headings this tank was actually rendered at, ascending.</summary>
    public IReadOnlyList<int> RenderedFacings() => _facings.Keys.OrderBy(k => k).ToList();

    private static double Mod(double a, double n) => (a % n + n) % n;
    private static int Mod(int a, int n) => (a % n + n) % n;

    // --- json shapes -------------------------------------------------------

    private sealed class LayerMeta
    {
        [JsonPropertyName("tile")] public int[] Tile { get; set; } = { 256, 256 };
        [JsonPropertyName("grid")] public GridMeta Grid { get; set; } = new();
        [JsonPropertyName("count")] public int Count { get; set; } = 1;
        [JsonPropertyName("anchor_px")] public double[] AnchorPx { get; set; } = { 128, 128 };
        [JsonPropertyName("units_per_pixel")] public double UnitsPerPixel { get; set; }
        [JsonPropertyName("frames")] public FrameMeta[] Frames { get; set; } = Array.Empty<FrameMeta>();
        [JsonPropertyName("view")] public ViewMeta View { get; set; } = new();
    }

    private sealed class ViewMeta
    {
        [JsonPropertyName("elevation")] public double Elevation { get; set; } = 30.0;
        [JsonPropertyName("azimuth")] public double Azimuth { get; set; }
    }

    private sealed class GridMeta
    {
        [JsonPropertyName("columns")] public int Columns { get; set; } = 1;
        [JsonPropertyName("rows")] public int Rows { get; set; } = 1;
    }

    private sealed class FrameMeta
    {
        [JsonPropertyName("index")] public int Index { get; set; }
        [JsonPropertyName("angle")] public double Angle { get; set; }
        [JsonPropertyName("label")] public string? Label { get; set; }
    }
}
