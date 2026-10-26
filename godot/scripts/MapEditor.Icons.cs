using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The brushes' icons (docs/editor3d-plan.md, stage 3): what each brush lays,
/// small, beside its name.
///
/// <b>Drawn where a picture says it, rendered where a model does.</b> A floor,
/// a level, a trench, a wall and a bunker are a hex prism at the board's angle
/// in the board's own tones - drawn into an image here (<see cref="Prism"/>),
/// cheap and the same every run. A wood and a minefield are their models: the
/// oak the board plants and the TM-62 it lays, rendered once each in a
/// viewport of their own (<see cref="Model"/>), so the icon is the thing the
/// brush puts down rather than a sign for it.
/// </summary>
public sealed partial class MapEditor
{
    /// <summary>An icon's side, px on the panel, and how much larger it is
    /// drawn before it is scaled down - the edges' smoothing.</summary>
    private const int IconSide = 30, IconOver = 3;

    private readonly Dictionary<string, Texture2D?> _icons = new();

    // The board's tones, near enough: the cells' art averaged, the mud's
    // shader colours (Stage3D's mire_mud, mire_pool), the walls' brick.
    private static readonly Color Grass = new(0.43f, 0.55f, 0.24f);
    private static readonly Color Soil = new(0.48f, 0.34f, 0.22f);
    private static readonly Color SandTone = new(0.80f, 0.69f, 0.47f);
    private static readonly Color MudTone = new(0.36f, 0.27f, 0.18f);
    private static readonly Color Pool = new(0.40f, 0.44f, 0.45f);
    private static readonly Color RockTone = new(0.55f, 0.53f, 0.50f);
    private static readonly Color Shallow = new(0.33f, 0.46f, 0.45f);
    private static readonly Color DeepTone = new(0.06f, 0.20f, 0.20f);
    private static readonly Color Brick = new(0.70f, 0.38f, 0.26f);
    private static readonly Color Concrete = new(0.68f, 0.68f, 0.66f);
    private static readonly Color IconInk = new(0.08f, 0.07f, 0.06f);

    /// <summary>The icon for a brush of the tab, by its name; null for a
    /// brush that has none.</summary>
    private Texture2D? IconFor(MapEdit.Tab tab, string name)
    {
        string key = tab + ":" + name.Split("  ")[0];
        if (_icons.TryGetValue(key, out Texture2D? had))
            return had;
        Texture2D? made = (tab, name.Split("  ")[0]) switch
        {
            (MapEdit.Tab.Foundation, "solid") => Prism(Grass, 0),
            (MapEdit.Tab.Foundation, "sand") => Prism(SandTone, 0),
            (MapEdit.Tab.Foundation, "mud") => Prism(MudTone, 0, puddle: true),
            (MapEdit.Tab.Foundation, "rock") => Prism(RockTone, 2),
            (MapEdit.Tab.Foundation, "water") => Prism(Shallow, -1),
            (MapEdit.Tab.Foundation, "deep water") => Prism(DeepTone, -1),
            (MapEdit.Tab.Foundation, "put on the board") => Prism(Grass, 0),
            (MapEdit.Tab.Foundation, "take off the board") => Prism(new Color(0, 0, 0, 0), 0),
            (MapEdit.Tab.Cover, "none") => Prism(Grass, 0),
            (MapEdit.Tab.Cover, "trench") => Prism(Grass, 0, trench: true),
            (MapEdit.Tab.Cover, "walls") => Prism(Grass, 0, ring: Brick),
            (MapEdit.Tab.Cover, "bunker") => Prism(Concrete, 1, slit: true),
            (MapEdit.Tab.Cover, "forest") => Model(key, tree: true),
            (MapEdit.Tab.Cover, "minefield") => Model(key, tree: false),
            (MapEdit.Tab.Level, "level +1") => Prism(Grass, 1),
            (MapEdit.Tab.Level, "level 0") => Prism(Grass, 0),
            (MapEdit.Tab.Level, "level -1") => Prism(Grass, -1),
            (MapEdit.Tab.Level, "ramp") => Prism(Grass, 0, ramp: true),
            (MapEdit.Tab.Units, string unit) => TankIcon(unit),
            (MapEdit.Tab.Presets, string preset) => PresetIcon(preset),
            _ => null,
        };
        _icons[key] = made;
        return made;
    }

    /// <summary>A preset's icon: the cell its core makes of plain ground, in
    /// the ghost's colours (MapEditor.Tools) - the file names no picture.</summary>
    private Texture2D? PresetIcon(string name)
    {
        if (_presets.FirstOrDefault(p => p.Name == name) is not { } preset)
            return null;
        MapEdit cell = MapEdit.Blank("icon", 1, 1);
        MapPresets.Lay(cell, preset, new[] { Vector2I.Zero });
        MapFile.Glyph g = cell.GlyphAt(Vector2I.Zero);
        return Prism(Tint(g.Letter), g.Cliff ? 2 : g.Level, puddle: g.Floor == Foundation.Mud);
    }

    /// <summary>
    /// A hex at the board's angle: its top at <paramref name="level"/> -
    /// a step up or down, two for the rock - with the sides of the prism under
    /// it in soil, and what the brush adds drawn on the top.
    /// </summary>
    private static Texture2D Prism(Color top, int level, bool puddle = false, bool trench = false,
                                   Color? ring = null, bool slit = false, bool ramp = false)
    {
        int n = IconSide * IconOver;
        var img = Image.CreateEmpty(n, n, false, Image.Format.Rgba8);
        img.Fill(new Color(0, 0, 0, 0));
        float r = n * 0.38f, squash = 0.55f, step = n * 0.15f;
        // The datum hex's middle; a level moves its top, the base stays.
        var mid = new Vector2(n * 0.5f, n * 0.62f);
        float lift = level * step;
        Vector2 Corner(int i, float up) =>
            mid + new Vector2(Mathf.Cos(Mathf.Pi / 3 * i) * r, Mathf.Sin(Mathf.Pi / 3 * i) * r * squash - up);
        var topPoly = new Vector2[6];
        var basePoly = new Vector2[6];
        for (int i = 0; i < 6; i++)
        {
            // A ramp's top tilts: its back edge a level up, its front at the datum.
            float up = ramp ? (Mathf.Sin(Mathf.Pi / 3 * i) < 0 ? step : 0.0f) : lift;
            topPoly[i] = Corner(i, up);
            basePoly[i] = Corner(i, Math.Min(0.0f, lift) - step * 0.6f);
        }
        bool off = top.A == 0.0f;
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            var p = new Vector2(x + 0.5f, y + 0.5f);
            Color c = new(0, 0, 0, 0);
            // The sides: the band between the top's front edges and the base's.
            if (!off && Inside(p, Hull(topPoly, basePoly)))
                c = level < 0 ? Soil.Darkened(0.35f) : Soil;
            if (Inside(p, topPoly))
            {
                c = off ? new Color(0, 0, 0, 0) : top;
                float d = (p - (mid - new Vector2(0, lift))).Length() / r;
                if (puddle && d < 0.38f)
                    c = Pool;
                if (trench && Mathf.Abs(p.Y - (mid.Y - lift)) < n * 0.035f && d < 0.8f)
                    c = Soil.Darkened(0.5f);
                if (slit && Mathf.Abs(p.Y - (mid.Y - lift + r * squash * 0.55f)) < n * 0.03f
                    && Mathf.Abs(p.X - mid.X) < r * 0.3f)
                    c = IconInk;
            }
            float edge = Edge(p, topPoly);
            if (edge < IconOver * 0.9f)
                c = off ? new Color(0.75f, 0.78f, 0.82f, (int)(Mathf.Atan2(p.Y - mid.Y, p.X - mid.X) * 6) % 2 == 0 ? 0.9f : 0.0f)
                    : ring is { } brick && edge < IconOver * 2.6f ? brick : IconInk;
            else if (ring is { } inner && edge < IconOver * 2.6f && Inside(p, topPoly))
                c = inner;
            // Below the datum: the datum's own outline over it, the hollow's rim.
            if (level < 0)
            {
                var datum = new Vector2[6];
                for (int i = 0; i < 6; i++)
                    datum[i] = Corner(i, 0.0f);
                if (Edge(p, datum) < IconOver * 0.8f)
                    c = new Color(0.85f, 0.86f, 0.80f, 0.85f);
            }
            img.SetPixel(x, y, c);
        }
        img.Resize(IconSide, IconSide, Image.Interpolation.Lanczos);
        return ImageTexture.CreateFromImage(img);
    }

    /// <summary>
    /// A Units brush's icon, <c>"green LT"</c>: the tank from above in its
    /// side's paint, inked round - belts, hull, turret and gun, a casemate
    /// (TD, HM) a fighting compartment instead of the turret - as big as its
    /// class (<see cref="MovementProfile.Size"/>). Drawn, not rendered: ten
    /// models loaded for the palette would stop the editor for seconds the
    /// first time the tab is opened.
    /// </summary>
    private static Texture2D? TankIcon(string unit)
    {
        string[] said = unit.Split(' ');
        if (said.Length != 2)
            return null;
        Color paint = said[0] == Parking.Brown ? BrownInk.Darkened(0.15f) : GreenInk.Darkened(0.35f);
        int pair = Array.FindIndex(Lineup.Pairs, p => p.Class.StartsWith(said[1], StringComparison.Ordinal));
        if (pair < 0)
            return null;
        string cls = Lineup.Pairs[pair].Class;
        bool casemate = cls is "TDP" or "HMP";
        // the heavy fills the icon, the rest as much smaller as their class
        float size = (float)Lineup.ClassFor(Lineup.Pairs[pair].Model).Size / 1.1f;
        int n = IconSide * IconOver;
        var img = Image.CreateEmpty(n, n, false, Image.Format.Rgba8);
        img.Fill(new Color(0, 0, 0, 0));
        var mid = new Vector2(n * 0.42f, n * 0.5f);
        float len = n * 0.62f * size, wide = n * 0.46f * size, ink = IconOver * 1.4f;
        // 0 none, 1 belts, 2 hull, 3 turret or compartment, 4 gun - grown by
        // the ink's width for the outline pass
        int Part(Vector2 p, float grow)
        {
            bool gun = p.X > 0.0f && p.X < len * 0.95f + grow && Mathf.Abs(p.Y) < n * 0.035f + grow;
            bool top = casemate
                ? p.X > -len * 0.12f - grow && p.X < len * 0.36f + grow && Mathf.Abs(p.Y) < wide * 0.30f + grow
                : p.Length() < wide * 0.30f + grow;
            bool hull = Mathf.Abs(p.X) < len * 0.5f + grow && Mathf.Abs(p.Y) < wide * 0.36f + grow;
            bool belts = Mathf.Abs(p.X) < len * 0.54f + grow && Mathf.Abs(p.Y) < wide * 0.5f + grow
                         && Mathf.Abs(p.Y) > wide * 0.36f - grow;
            return top ? 3 : gun ? 4 : hull ? 2 : belts ? 1 : 0;
        }
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            var p = new Vector2(x + 0.5f, y + 0.5f) - mid;
            int part = Part(p, 0.0f);
            if (part == 0)
            {
                // the cel line round the whole tank
                if (Part(p, ink) != 0)
                    img.SetPixel(x, y, IconInk);
                continue;
            }
            // and between the parts: the turret's rim over the hull
            bool rim = part == 3 && Part(p, -ink) != 3;
            Color c = rim ? IconInk
                : part switch
                {
                    1 => IconInk.Lightened(0.3f),
                    2 => paint,
                    3 => paint.Lightened(0.2f),
                    _ => IconInk.Lightened(0.35f),
                };
            img.SetPixel(x, y, c);
        }
        img.Resize(IconSide, IconSide, Image.Interpolation.Lanczos);
        return ImageTexture.CreateFromImage(img);
    }

    /// <summary>The top and the base as one outline, the prism's silhouette.</summary>
    private static Vector2[] Hull(Vector2[] top, Vector2[] bottom)
    {
        // The lower half of the base (its three front corners) and the top's
        // upper half are the silhouette of a prism seen from above the front.
        return new[] { top[3], top[4], top[5], top[0], bottom[0], bottom[1], bottom[2], bottom[3] };
    }

    private static bool Inside(Vector2 p, Vector2[] poly)
    {
        bool inside = false;
        for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
            if ((poly[i].Y > p.Y) != (poly[j].Y > p.Y)
                && p.X < (poly[j].X - poly[i].X) * (p.Y - poly[i].Y) / (poly[j].Y - poly[i].Y) + poly[i].X)
                inside = !inside;
        return inside;
    }

    private static float Edge(Vector2 p, Vector2[] poly)
    {
        float best = float.MaxValue;
        for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
        {
            Vector2 a = poly[j], b = poly[i], ab = b - a;
            float t = Mathf.Clamp((p - a).Dot(ab) / Mathf.Max(ab.LengthSquared(), 1e-6f), 0.0f, 1.0f);
            best = Mathf.Min(best, (a + ab * t - p).Length());
        }
        return best;
    }

    /// <summary>
    /// A model's icon: the oak the board plants, or the TM-62, on a disc of
    /// grass, rendered once in a viewport of its own world - lit by the
    /// board's sun, at the board's angle, its background clear.
    /// </summary>
    private Texture2D? Model(string key, bool tree)
    {
        Node3D? thing = null;
        float size;
        if (tree)
        {
            if (TreeModel.Load(BoardDress.Oaks[0], false, out string? error) is not { } oak)
            {
                GD.Print($"editor: icon {key}: {error}");
                return null;
            }
            thing = new Node3D { Scale = Vector3.One * oak.Ppm };
            thing.AddChild(oak.Scene);
            size = Mathf.Max(oak.Height, oak.Width) * oak.Ppm * 1.15f;
        }
        else
        {
            if (BoardDress.Mine(out string? note) is not { } art)
            {
                GD.Print($"editor: icon {key}: {note}");
                return null;
            }
            var mine = (Node3D)art.Doc.GenerateScene(art.State);
            Toon.Dress(mine);
            float scale = 40.0f / art.Across;
            thing = new Node3D { Scale = Vector3.One * scale };
            thing.AddChild(mine);
            size = 40.0f * 1.8f;
        }

        var view = new SubViewport
        {
            Name = "Icon_" + key.Replace(':', '_'),
            Size = new Vector2I(IconSide * 2, IconSide * 2),
            TransparentBg = true,
            OwnWorld3D = true,
            Msaa3D = Viewport.Msaa.Msaa4X,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Once,
        };
        AddChild(view);
        view.AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.ClearColor,
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.62f, 0.64f, 0.70f),
                AmbientLightEnergy = 0.55f,
            },
        });
        DirectionalLight3D sun = BoardDress.Sun();
        sun.ShadowEnabled = false;
        view.AddChild(sun);
        var disc = new MeshInstance3D
        {
            Mesh = new CylinderMesh
            {
                TopRadius = size * 0.45f, BottomRadius = size * 0.45f, Height = size * 0.04f,
                RadialSegments = 6, Rings = 1,
            },
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = Grass, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            },
            Position = new Vector3(0.0f, -size * 0.02f, 0.0f),
        };
        view.AddChild(disc);
        view.AddChild(thing);
        float squash = Mathf.Max(_field.Squash, 0.1f);
        var eye = new Camera3D
        {
            Projection = Camera3D.ProjectionType.Orthogonal,
            Size = size,
            Near = 1.0f,
            Far = size * 20.0f,
            RotationDegrees = new Vector3(-Mathf.RadToDeg(Mathf.Asin(squash)), 0.0f, 0.0f),
            Current = true,
        };
        float back = size * 8.0f;
        eye.Position = new Vector3(0.0f, size * (tree ? 0.42f : 0.15f), 0.0f)
                       + new Vector3(0.0f, back * squash, back * Mathf.Sqrt(1.0f - squash * squash));
        view.AddChild(eye);
        return view.GetTexture();
    }
}
