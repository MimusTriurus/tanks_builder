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

    /// <summary>
    /// The engine plume, which is an effect layer and is not one of those.
    ///
    /// Kept out of <see cref="EffectNames"/> deliberately: that array is the
    /// shot's pair, and <see cref="HasEffects"/> - which decides whether the
    /// rendered flash can be shown at all - requires every name in it. Adding
    /// the exhaust there would switch the rendered flash off on any tank that
    /// had a flash but no separated Engine, which is a different question
    /// answered wrongly.
    ///
    /// It also differs in both of the things a layer is indexed by. It follows
    /// the *hull*, because the outlet is bolted to the engine deck, where the
    /// flash follows the turret; and it loops rather than running once, so it
    /// has its own clock in <see cref="ExhaustLoop"/> instead of a table of
    /// held frames.
    /// </summary>
    public const string ExhaustName = "exhaust";

    /// <summary>
    /// The burning wreck: a red flame at the engine deck and a black column
    /// above it.
    ///
    /// Two layers for the reason the shot is two - the flame emits and is added,
    /// the column occludes and is not - and kept out of
    /// <see cref="EffectNames"/> for the reason the exhaust is.
    ///
    /// Both follow the *hull*, like the exhaust and unlike the flash: all three
    /// are built on the same stamped ports, and those are bolted to the engine
    /// deck.
    /// </summary>
    public const string FireName = "fire";
    public const string BurnName = "burn";

    /// <summary>
    /// A shell arriving: the burst and the dust under it.
    ///
    /// Alone among the layers these are rendered with *no headings at all* -
    /// one column of phases - because a hit is not welded to the tank the way a
    /// muzzle flash is welded to a gun, and a ball of light looks the same from
    /// every azimuth. The game puts it where the hit was, off the plate table
    /// the renderer stamps into the same JSON. That is also why
    /// <see cref="CountOf"/> exists.
    ///
    /// Kept out of <see cref="EffectNames"/> for the reason the exhaust is, and
    /// required in pairs for the reason the burning halves are: the dust is not
    /// decoration, it is what the additive burst is composited against.
    /// </summary>
    public const string BurstName = "burst";
    public const string DustName = "dust";

    /// <summary>
    /// The mark a shell leaves, one layer per plate.
    ///
    /// The opposite of the burst in every way that matters, and worth stating
    /// as such. It is welded to the tank, so it is rendered at every heading
    /// and drawn at the shared anchor with no offset - it is not
    /// <see cref="EffectLayer.Placed"/>. And its phases are not time: they are
    /// how much damage the plate has taken, so nothing steps them and there is
    /// no clock. A plate holds its level until something changes it.
    ///
    /// Because it turns with the hull, the renderer could hold the tank out of
    /// it, which is why nothing here decides front-or-behind: a mark on a plate
    /// facing away is simply an empty frame.
    /// </summary>
    public static readonly string[] ScarNames =
        { "scar_front", "scar_rear", "scar_left", "scar_right" };

    /// <summary>Layers that load if they are there and are silently skipped if
    /// they are not. All but the hit layers need a piece separated by hand in
    /// the .blend; the hit is measured off the hull and so is always there.</summary>
    private static readonly string[] OptionalNames =
        new[] { "smoke", "flash", ExhaustName, FireName, BurnName, BurstName,
                DustName }.Concat(ScarNames).ToArray();

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

    /// <summary>
    /// Headings a layer was rendered at, which until the hit arrived was the
    /// same number for every layer and so was read off the hull.
    ///
    /// The hit burst breaks that: it is not welded to the tank, so it renders
    /// once with no headings at all and the game puts it where the hit was.
    /// Its count is 1, and indexing it against the hull's 12 would run straight
    /// off the end of a ten-frame atlas.
    /// </summary>
    private readonly Dictionary<string, int> _counts = new();

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

    /// <summary>True when this tank was rendered with an engine plume.</summary>
    public bool HasExhaust => Has(ExhaustName);

    /// <summary>Phases in the exhaust loop, 0 if it is missing.</summary>
    public int ExhaustPhases => PhasesOf(ExhaustName);

    /// <summary>True when this tank was rendered burning. Both halves are
    /// required, and that is not tidiness: the column is not decoration, it is
    /// what the additive flame is composited against, and the flame alone on the
    /// game's pale field washes out to nothing much.</summary>
    public bool HasBurning => Has(FireName) && Has(BurnName);

    /// <summary>Phases in the burning loop, 0 if it is missing. Both layers
    /// share the count - one event, one render job.</summary>
    public int BurnPhases => HasBurning ? PhasesOf(FireName) : 0;

    /// <summary>True when this tank was rendered with the hit pair *and* the
    /// plate table that says where to put it. Both halves, for the burning
    /// pair's reason: an additive burst with nothing dark under it is the
    /// field, faintly tinted.</summary>
    public bool HasHit => Has(BurstName) && Has(DustName) && _plates.Count > 0;

    /// <summary>Phases in a hit, 0 if it is missing.</summary>
    public int HitPhases => HasHit ? PhasesOf(BurstName) : 0;

    /// <summary>True when every plate has a mark to leave. All four, because a
    /// tank that can be holed on three sides and not the fourth is worse than
    /// one that cannot be holed at all - the clean side reads as armour that
    /// held.</summary>
    public bool HasScars
    {
        get
        {
            foreach (string layer in ScarNames)
                if (!Has(layer))
                    return false;
            return HitFaces.Count > 0;
        }
    }

    /// <summary>Levels of damage a plate can be in, counting the clean state
    /// this layer does not draw. 0 if the layers are missing.</summary>
    public int ScarLevels => HasScars ? PhasesOf(ScarNames[0]) : 0;

    /// <summary>The layer that carries a given plate's mark, or "" if this tank
    /// has none. Faces and layers are matched by name rather than by position:
    /// <see cref="HitFaces"/> comes from the render and could in principle come
    /// back in another order or short a plate.</summary>
    public string ScarLayer(string face)
    {
        string name = "scar_" + face;
        return Has(name) ? name : "";
    }

    /// <summary>The plates this tank was measured to have, in a stable order.</summary>
    public IReadOnlyList<string> HitFaces { get; private set; } = Array.Empty<string>();

    private readonly Dictionary<string, PlateMeta> _plates = new();

    /// <summary>
    /// Which plate a shell arriving from <paramref name="fromBearing"/> lands
    /// on, given where the hull is pointing.
    ///
    /// The plate whose outward normal comes closest to facing the shooter. Each
    /// plate's bearing is stamped as an offset from the hull's own heading, so
    /// this stays right as the tank turns - a world angle would have been a
    /// number that is only true at heading zero.
    /// </summary>
    public string FaceFor(double fromBearing, double hullFacing)
    {
        string best = HitFaces.Count > 0 ? HitFaces[0] : "";
        double bestGap = double.MaxValue;
        foreach (string face in HitFaces)
        {
            double outward = hullFacing + _plates[face].Bearing;
            double gap = Math.Abs(Mod(outward - fromBearing + 180.0, 360.0) - 180.0);
            if (gap < bestGap)
            {
                bestGap = gap;
                best = face;
            }
        }
        return best;
    }

    /// <summary>Where a plate looks, as an offset from the hull's own heading.
    /// Exposed so <see cref="FaceFor"/> can be asserted against the geometry
    /// rather than against itself.</summary>
    public double HitBearing(string face) =>
        _plates.TryGetValue(face, out PlateMeta? plate) ? plate.Bearing : 0.0;

    /// <summary>Where a hit on <paramref name="face"/> sits, in pixels from the
    /// layer's anchor, with the hull at <paramref name="hullFacing"/>.</summary>
    public Vector2 HitOffset(string face, double hullFacing) =>
        HitFrame(face, hullFacing) is { } row
            ? new Vector2((float)row.Offset[0], (float)row.Offset[1])
            : Vector2.Zero;

    /// <summary>How squarely the plate faces the camera: +1 straight at it, -1
    /// straight away. The sign alone decides whether the hit is drawn in front
    /// of the tank or behind it.</summary>
    public double HitFacing(string face, double hullFacing) =>
        HitFrame(face, hullFacing)?.Facing ?? 1.0;

    /// <summary>Half the plate's width, as a screen vector, so a hit can be
    /// scattered along the plate rather than off it.</summary>
    public Vector2 HitTangent(string face, double hullFacing) =>
        HitFrame(face, hullFacing) is { } row
            ? new Vector2((float)row.Tangent[0], (float)row.Tangent[1])
            : Vector2.Zero;

    private HitFrameMeta? HitFrame(string face, double hullFacing)
    {
        if (!_plates.TryGetValue(face, out PlateMeta? plate) || plate.Frames.Length == 0)
            return null;
        // the table is in frame-index order, which is what FrameFor returns
        int index = Math.Clamp(FrameFor(hullFacing), 0, plate.Frames.Length - 1);
        return plate.Frames[index];
    }

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
        _counts[layer] = Math.Max(1, meta.Count);
    }

    /// <summary>The plate table, straight out of the layer that gets placed by
    /// it. Ordered front, rear, left, right when they are all present, so the
    /// harness cycles them in a way that makes sense rather than in whatever
    /// order the JSON happened to come in.</summary>
    private void TakePlates(Dictionary<string, PlateMeta> plates)
    {
        foreach ((string face, PlateMeta plate) in plates)
            _plates[face] = plate;
        string[] preferred = { "front", "rear", "left", "right" };
        var ordered = new List<string>();
        foreach (string face in preferred)
            if (_plates.ContainsKey(face))
                ordered.Add(face);
        foreach (string face in _plates.Keys)
            if (!ordered.Contains(face))
                ordered.Add(face);
        HitFaces = ordered;
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

        // Effects are optional and silent about it. Each needs a piece split out
        // by hand in the .blend - a Barrel for the shot, an Engine for the plume
        // - which only some scenes have; a tank without them still loads, and
        // the harness draws the painted sheet instead of the rendered flash. A
        // missing file here is a fact about the scene, not an error.
        foreach (string layer in OptionalNames)
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
            if (layer == BurstName && meta.Hits is not null)
                atlas.TakePlates(meta.Hits.Faces);
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
    public int EffectFrame(string layer, int phase, double facing)
    {
        int count = CountOf(layer);
        // a layer rendered without headings has exactly one column, and asking
        // which of them faces anywhere is a question with one answer
        int frame = count <= 1 ? 0 : FrameFor(facing);
        return Math.Clamp(phase, 0, PhasesOf(layer) - 1) * count + frame;
    }

    /// <summary>Headings a layer holds; the tank's count for anything absent.</summary>
    public int CountOf(string layer) =>
        _counts.TryGetValue(layer, out int count) ? count : Count;

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
        [JsonPropertyName("hits")] public HitsMeta? Hits { get; set; }
    }

    private sealed class HitsMeta
    {
        [JsonPropertyName("faces")]
        public Dictionary<string, PlateMeta> Faces { get; set; } = new();
    }

    private sealed class PlateMeta
    {
        /// <summary>Where the plate looks, as an offset from the hull's own
        /// heading. Not a world angle: the hull turns.</summary>
        [JsonPropertyName("bearing")] public double Bearing { get; set; }

        [JsonPropertyName("frames")]
        public HitFrameMeta[] Frames { get; set; } = Array.Empty<HitFrameMeta>();
    }

    private sealed class HitFrameMeta
    {
        [JsonPropertyName("offset")] public double[] Offset { get; set; } = { 0, 0 };
        [JsonPropertyName("tangent")] public double[] Tangent { get; set; } = { 0, 0 };
        [JsonPropertyName("slope")] public double[] Slope { get; set; } = { 0, 0 };
        [JsonPropertyName("normal")] public double[] Normal { get; set; } = { 0, 0 };
        [JsonPropertyName("facing")] public double Facing { get; set; } = 1.0;
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
