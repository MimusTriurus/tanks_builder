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

    /// <summary>
    /// Effect layers, if the scene had them: smoke first, then fire.
    ///
    /// Optional because they need a `Barrel` separated by hand in the .blend and
    /// only MT has one so far. A tank without them still loads and still drives;
    /// the harness falls back to the painted sheet, which is also what makes the
    /// two comparable side by side.
    ///
    /// The order is the draw order, and it is not arbitrary: smoke occludes and
    /// goes on with normal alpha, fire emits and goes on additively, so fire is
    /// last and on top.
    /// </summary>
    public static readonly string[] EffectNames = { "smoke", "flash" };

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
    /// Per-layer frame size and anchor.
    ///
    /// These used to be one pair for the whole set, taken off the hull, which
    /// was true until the effect layers arrived: they are rendered at a wider
    /// tile so a flash longer than the tank's half-tile is not cut off at the
    /// frame edge. The camera widens with the tile, so `units_per_pixel` is
    /// identical and the layers still composite - but only if each is drawn at
    /// *its own* anchor. Drawing a 384 tile at the hull's 256 anchor puts the
    /// flash 64px off in both directions.
    /// </summary>
    private readonly Dictionary<string, Vector2I> _tiles = new();
    private readonly Dictionary<string, Vector2> _anchors = new();
    private readonly Dictionary<string, int> _phases = new();

    /// <summary>Frame size of a layer. Falls back to the tank's tile for a
    /// layer that is not present.</summary>
    public Vector2I TileOf(string layer) =>
        _tiles.TryGetValue(layer, out Vector2I tile) ? tile : Tile;

    /// <summary>Anchor of a layer, in that layer's own pixels.</summary>
    public Vector2 AnchorOf(string layer) =>
        _anchors.TryGetValue(layer, out Vector2 anchor) ? anchor : Anchor;

    /// <summary>How many time steps an effect layer holds, 0 if absent.</summary>
    public int PhasesOf(string layer) =>
        _phases.TryGetValue(layer, out int phases) ? phases : 0;

    public bool Has(string layer) => _textures.ContainsKey(layer);

    /// <summary>True when this tank was rendered with the effect layers.</summary>
    public bool HasEffects
    {
        get
        {
            foreach (string layer in EffectNames)
                if (!Has(layer))
                    return false;
            return true;
        }
    }

    /// <summary>Phases shared by the effect layers, 0 if they are missing.</summary>
    public int EffectPhases => HasEffects ? PhasesOf(EffectNames[0]) : 0;

    /// <summary>Muzzle point per turret frame, in tile pixels.</summary>
    private Vector2[] _muzzle = Array.Empty<Vector2>();

    /// <summary>
    /// Where the gun ends, for the frame showing that heading, in tile pixels.
    ///
    /// Measured off the turret layer rather than tuned by hand, which buys two
    /// things a constant cannot. It survives a re-render at another size or with
    /// another model; and it foreshortens by itself - the same gun measures 68px
    /// from the axis across the screen and 35px pointing away from the camera,
    /// because that is what the sprite actually shows.
    /// </summary>
    public Vector2 Muzzle(int frameIndex) =>
        frameIndex >= 0 && frameIndex < _muzzle.Length ? _muzzle[frameIndex] : Anchor;

    /// <summary>
    /// Heading in degrees to frame index, taken from the per-frame labels the
    /// renderer wrote ("side @ 270 deg"). Reading it from the file rather than
    /// assuming front_dir = 270 means a model imported at another heading still
    /// lines up here.
    /// </summary>
    private readonly Dictionary<int, int> _facings = new();

    private AtlasSet() { }

    private void Take(string layer, Image image, LayerMeta meta)
    {
        _textures[layer] = ImageTexture.CreateFromImage(image);
        _columns[layer] = Math.Max(1, meta.Grid.Columns);
        _tiles[layer] = new Vector2I(meta.Tile[0], meta.Tile[1]);
        _anchors[layer] = new Vector2((float)meta.AnchorPx[0], (float)meta.AnchorPx[1]);
        _phases[layer] = Math.Max(1, meta.Phases);
    }

    public static AtlasSet Load(string root, string tag)
    {
        var atlas = new AtlasSet { Tag = tag };
        LayerMeta? hull = null;
        Image? turretImage = null;

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

            atlas.Take(layer, image, meta);
            if (layer == "hex")
                atlas.HexRect = image.GetUsedRect();
            if (layer == "turret")
                turretImage = image;
            if (layer == "hull")
                hull = meta;
        }

        // Effects are optional and silent about it. They need a Barrel split
        // out by hand in the .blend, which only some scenes have; a tank
        // without them still loads, and the harness draws the painted sheet
        // instead. A missing file here is a fact about the scene, not an error.
        foreach (string layer in EffectNames)
        {
            string basePath = $"{root}/{tag}/{layer}_atlas";
            if (!File.Exists(basePath + ".json") || !File.Exists(basePath + ".png"))
                continue;
            LayerMeta? meta;
            try
            {
                meta = JsonSerializer.Deserialize<LayerMeta>(
                    File.ReadAllText(basePath + ".json"));
            }
            catch (JsonException)
            {
                continue;
            }
            Image? image = meta is null ? null : Image.LoadFromFile(basePath + ".png");
            if (meta is null || image is null)
                continue;
            atlas.Take(layer, image, meta);
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
        if (turretImage is not null)
            atlas.FindMuzzles(turretImage, atlas._columns["turret"]);
        return atlas;
    }

    /// <summary>
    /// Furthest *solid* pixel of each turret frame along the heading it shows.
    ///
    /// Solid, not merely opaque, and that word is the whole of it. Taking the
    /// furthest opaque pixel puts the muzzle on the wireless aerial: it stands
    /// higher than anything else on the tank, so the moment the gun points away
    /// from the camera and foreshortens, the aerial wins and the flash comes out
    /// of the radio. Requiring the neighbours a few pixels out to be opaque too
    /// throws away anything thinner than the barrel, which is exactly the class
    /// of thing that can beat it - aerials, hatch handles, tow cables.
    /// </summary>
    private const int MuzzleErode = 3;

    private void FindMuzzles(Image image, int columns)
    {
        Image rgba = image;
        if (rgba.GetFormat() != Image.Format.Rgba8)
        {
            rgba = (Image)image.Duplicate();
            rgba.Convert(Image.Format.Rgba8);
        }
        byte[] data = rgba.GetData();
        int width = rgba.GetWidth();

        _muzzle = new Vector2[Count];
        for (int i = 0; i < _muzzle.Length; i++)
            _muzzle[i] = Anchor;

        foreach ((int facing, int index) in _facings)
        {
            if (index < 0 || index >= Count)
                continue;
            Vector2 dir = GroundDirection(facing).Normalized();
            int ox = index % columns * Tile.X, oy = index / columns * Tile.Y;
            float best = float.MinValue;
            for (int y = 0; y < Tile.Y; y++)
            for (int x = 0; x < Tile.X; x++)
            {
                if (!Solid(data, width, ox, oy, x, y))
                    continue;
                float d = (new Vector2(x, y) - Anchor).Dot(dir);
                if (d <= best)
                    continue;
                best = d;
                _muzzle[index] = new Vector2(x, y);
            }
        }
    }

    private bool Solid(byte[] data, int width, int ox, int oy, int x, int y)
    {
        if (x < MuzzleErode || y < MuzzleErode
            || x >= Tile.X - MuzzleErode || y >= Tile.Y - MuzzleErode)
            return false;
        return Opaque(data, width, ox + x, oy + y)
               && Opaque(data, width, ox + x - MuzzleErode, oy + y)
               && Opaque(data, width, ox + x + MuzzleErode, oy + y)
               && Opaque(data, width, ox + x, oy + y - MuzzleErode)
               && Opaque(data, width, ox + x, oy + y + MuzzleErode);
    }

    private static bool Opaque(byte[] data, int width, int x, int y) =>
        data[(y * width + x) * 4 + 3] >= 32;

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
        Vector2I tile = TileOf(layer);
        return new Rect2(
            new Vector2(index % columns * tile.X, index / columns * tile.Y),
            tile);
    }

    /// <summary>Frame of an effect layer showing <paramref name="phase"/> of the
    /// shot at <paramref name="facing"/> degrees. The grid is one row per phase
    /// and one column per heading, so the index is the obvious product - and
    /// `Count` is angles per phase, not tiles in the file, exactly so that it
    /// stays the number a heading is resolved against.</summary>
    public int EffectFrame(string layer, int phase, double facing) =>
        Math.Clamp(phase, 0, PhasesOf(layer) - 1) * Count + FrameFor(facing);

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
        [JsonPropertyName("phases")] public int Phases { get; set; } = 1;
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
