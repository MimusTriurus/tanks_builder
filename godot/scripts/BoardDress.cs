using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// What stands on a 3D board besides the board: the light, the shadow skin, the
/// model trees, the grass, the mines and the masonry - laid off a
/// <see cref="HexField"/> the same way wherever it is asked.
///
/// <b>Two callers, one look.</b> <c>Tank3DBench</c> dresses its board with it
/// and the map editor dresses the board being drawn, so what the author paints
/// is what the 3D line plays (docs/editor3d-plan.md, stage 2). Before this the
/// placement lived in the bench's partials and the editor drew the sprite
/// era's billboards on the same <see cref="Stage3D"/>.
///
/// <b>Placement here, behaviour with the caller.</b> A tree's fire and fall, a
/// mine's blast, a wall's rig are the bench's: they need a tank to happen. This
/// file answers where each thing stands and builds it standing - nothing in it
/// knows which scene asked, the rule every shared module here keeps.
///
/// Coordinates are the bench's world: x and z the board's flat px with z
/// unsquashed (<c>flat.Y / squash</c>), y the lift over the rise
/// (<see cref="Stage3D.World"/>).
/// </summary>
public static class BoardDress
{
    // --- light ---------------------------------------------------------------

    /// <summary>The world the models are lit in: a flat background and an
    /// ambient. <paramref name="flat"/> is the bench's board-less ground, whose
    /// background is the soil's mean; on a board it is the benches' grey,
    /// because past the rim is off the board, not more of it.</summary>
    public static WorldEnvironment World(bool flat = false) => new()
    {
        Environment = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = flat ? new Color(0.60f, 0.50f, 0.33f) : new Color(0.3f, 0.3f, 0.3f),
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = new Color(0.62f, 0.64f, 0.70f),
            AmbientLightEnergy = 0.55f,
        },
    };

    /// <summary>
    /// The sun: from over the camera's left shoulder, the side the sprites are
    /// lit from, high enough that the shadow stays short and on the ground.
    /// </summary>
    public static DirectionalLight3D Sun() => new()
    {
        Name = "Sun",
        RotationDegrees = new Vector3(-52.0f, -35.0f, 0.0f),
        LightEnergy = 1.25f,
        ShadowEnabled = true,
        DirectionalShadowMode = DirectionalLight3D.ShadowMode.Parallel2Splits,
        DirectionalShadowMaxDistance = 3000.0f,
        // Twice the default: on the slab the bench's FrameShadow fits, a texel
        // is fine enough that the cel ramp's step cut the turret's sides into
        // acne stripes along its loft at 2; at 7 the contact shadows under the
        // turret and the fenders thin away.
        ShadowNormalBias = 4.0f,
        // out to the edge of the map, not faded from 0.8 of it: the tank's
        // shadow falls just past the tank
        DirectionalShadowFadeStart = 1.0f,
    };

    /// <summary>
    /// Where the models' shadows fall: a skin over the board's top faces that
    /// is clear where the sun reaches and dark where it does not.
    ///
    /// <b>The board is unshaded</b> - its light is painted - so the sun has
    /// nothing to fall on. The skin is the board's own tops
    /// (<see cref="Stage3D.GroundTriangles"/>), lifted by the stage's
    /// clearance, multiplying what is under it: by one in the sun, by one less
    /// the board's shadow ink out of it (<see cref="ShadeShader"/>). Null on a
    /// stage with no tops yet.
    /// </summary>
    public static MeshInstance3D? Shadows(Stage3D stage, float squash, float rise)
    {
        List<Vector3> tris = stage.GroundTriangles();
        if (tris.Count == 0)
            return null;
        Vector3 lie = Stage3D.Clear(squash, rise);
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        foreach (Vector3 v in tris)
        {
            st.SetNormal(Vector3.Up);
            st.AddVertex(v + lie);
        }
        var ink = new ShaderMaterial { Shader = ShadeShader, RenderPriority = Stage3D.ShadowOrder };
        ink.SetShaderParameter("ink", Stage3D.ShadowInk.A);
        return new MeshInstance3D
        {
            Name = "Shadows",
            Mesh = st.Commit(),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            MaterialOverride = ink,
        };
    }

    /// <summary>
    /// The skin's shader: white, lit only by the sun's visibility, multiplied
    /// into the frame. Emission carries the part the sun cannot take away and
    /// the light the rest, so the product is one in the sun and <c>1 - ink</c>
    /// in shadow - the board's own ink, as <see cref="Stage3D.ShadowInk"/>
    /// lays it over the ground under a tree.
    ///
    /// <b>Not <c>shadow_to_opacity</c></b>, which is the stock answer and draws
    /// nothing at all under <c>gl_compatibility</c>: measured, the skin came out
    /// clear in the model's shadow too.
    /// </summary>
    private static readonly Shader ShadeShader = new()
    {
        Code = @"
shader_type spatial;
render_mode blend_mul, depth_draw_never, cull_disabled, ambient_light_disabled;
uniform float ink = 0.45;
void fragment() {
    ALBEDO = vec3(1.0);
    EMISSION = vec3(1.0 - ink);
}
void light() {
    DIFFUSE_LIGHT += vec3(ink * ATTENUATION);
}",
    };

    // --- the ground under things ----------------------------------------------

    /// <summary>The ground point under a world point.</summary>
    public static Vector3 Foot(HexField field, Vector3 w, float squash, float rise) =>
        new(w.X, field.TopAtPoint(new Vector2(w.X, w.Z * squash)) / Mathf.Max(rise, 0.0001f), w.Z);

    /// <summary>A cell's middle on its ground.</summary>
    public static Vector3 Middle(HexField field, Vector2I cell, float squash, float rise)
    {
        Vector2 flat = field.FlatAnchor(cell) + field.CentreOffset;
        return Foot(field, new Vector3(flat.X, 0.0f, flat.Y / squash), squash, rise);
    }

    // --- the wood -------------------------------------------------------------

    /// <summary>The species a wood is planted from unless told otherwise.</summary>
    public static readonly string[] Oaks = { "Oak_001", "Oak_002", "Oak_003", "Oak_004", "Oak_005", "Oak_006" };

    /// <summary>Trees on a wooded cell unless told otherwise - <c>Tree3D</c>'s
    /// <c>--wood</c>.</summary>
    public const int WoodCount = 7;

    /// <summary>Where one tree of a wood stands: its cell, which of the cell's
    /// it is, its species, its foot on the datum (y nought - the caller stands
    /// it on the ground), its yaw, and the flat point it was hashed from.</summary>
    public readonly record struct TreeSpot(Vector2I Cell, int Index, string Name, Vector3 At, float Yaw, Vector2 Flat);

    /// <summary>The board's wooded cells.</summary>
    public static List<Vector2I> Wooded(HexField field)
    {
        var wooded = new List<Vector2I>();
        for (int q = 0; q < field.Columns; q++)
        for (int r = 0; r < field.Rows; r++)
        {
            var cell = new Vector2I(q, r);
            if (field.InBounds(cell) && field.CoverAt(cell) == Cover.Forest)
                wooded.Add(cell);
        }
        return wooded;
    }

    /// <summary>
    /// The trees of one wooded cell: one in the middle, the rest on a ring
    /// 60-80 px out (y squashed as the screen has it), each turned its own way
    /// by a hash of where it stands - the crowns overlap as a wood's do. All of
    /// it hashed off the cell, so a cell planted again plants the same wood.
    /// </summary>
    public static IEnumerable<TreeSpot> Spots(HexField field, Vector2I cell, string[] names, int count, float squash)
    {
        Vector2 flat = field.FlatAnchor(cell) + field.CentreOffset;
        for (int i = 0; i < count; i++)
        {
            Vector2 off = Vector2.Zero;
            if (i > 0)
            {
                float a = Mathf.Tau * (i - 1) / Mathf.Max(count - 1, 1) + 0.4f + 0.35f * CelPuff.Hash(i, 613);
                float d = 60.0f + 20.0f * CelPuff.Hash(i, 617);
                off = new Vector2(Mathf.Cos(a) * d, Mathf.Sin(a) * d * squash);
            }
            Vector2 at = flat + off;
            int salt = cell.X * 97 + cell.Y * 31 + i;
            yield return new TreeSpot(cell, i, names[(salt + i) % names.Length],
                                      new Vector3(at.X, 0.0f, at.Y / squash), Mathf.Tau * CelPuff.Hash(salt, 611), at);
        }
    }

    /// <summary>
    /// A model tree standing on <paramref name="foot"/>: loaded, outlined by
    /// <paramref name="crowns"/> when there is one, in a holder scaled to the
    /// board's metre and added to <paramref name="parent"/>. Null, with the
    /// reason, when the model's files are not there.
    ///
    /// <b><paramref name="shared"/> is for a board nothing burns.</b> With it a
    /// species is read once and each tree is a <see cref="Node.Duplicate"/> of
    /// it - the meshes and their cel materials shared, which is what makes it
    /// cheap (a read is about 85 ms; ABBEY's wood was 25 s without it) and also
    /// what makes it wrong for the bench, where one tree's burn would be every
    /// tree's of that species. Without it every tree is its own read.
    /// </summary>
    public static Node3D? Tree(Node parent, TreeSpot spot, Vector3 foot, bool pbr, CrownOutline? crowns,
                               string label, out TreeModel.Loaded? model, out string? error,
                               Dictionary<string, TreeModel.Loaded>? shared = null)
    {
        Node scene;
        if (shared is null)
        {
            model = TreeModel.Load(spot.Name, pbr, out error);
            if (model is null)
                return null;
            scene = model.Scene;
        }
        else
        {
            error = null;
            if (!shared.TryGetValue(spot.Name, out model))
            {
                model = TreeModel.Load(spot.Name, pbr, out error);
                if (model is null)
                    return null;
                shared[spot.Name] = model;
            }
            scene = model.Scene.Duplicate();
        }
        crowns?.Ghost(scene, model.Burn, model.Leaves);
        var holder = new Node3D
        {
            Name = label, Scale = Vector3.One * model.Ppm,
            Rotation = new Vector3(0.0f, spot.Yaw, 0.0f),
        };
        holder.AddChild(scene);
        parent.AddChild(holder);
        holder.Position = foot;
        TreeModel.Margin(holder);
        return holder;
    }

    // --- the grass ------------------------------------------------------------

    /// <summary>The grass's metre when no tree has given one.</summary>
    public const float GrassPpm = 17.0f;

    /// <summary>Whether a cell grows grass: level plain ground with nothing on
    /// it but a wood.</summary>
    public static bool Lawned(HexField field, Vector2I cell) =>
        field.InBounds(cell)
        && field.FoundationAt(cell) == Foundation.Solid && !field.IsRamp(cell) && !field.IsWater(cell)
        && field.CoverAt(cell) is Cover.None or Cover.Forest;

    /// <summary>The span the grass's press map covers - every cell's square of
    /// <paramref name="radius"/>, world XZ.</summary>
    public static Rect2 LawnSpan(HexField field, float radius, float squash, float rise)
    {
        var span = new Rect2();
        bool first = true;
        for (int q = 0; q < field.Columns; q++)
        for (int w = 0; w < field.Rows; w++)
        {
            var cell = new Vector2I(q, w);
            if (!field.InBounds(cell))
                continue;
            Vector3 m = Middle(field, cell, squash, rise);
            var box = new Rect2(m.X - radius, m.Z - radius, 2.0f * radius, 2.0f * radius);
            span = first ? box : span.Merge(box);
            first = false;
        }
        return span;
    }

    // --- mines ----------------------------------------------------------------

    /// <summary>A mine's width, a share of the hex - the model to scale would be
    /// four pixels at board zoom (the bench's <c>Tank3DBench.Mines</c> argues it).</summary>
    public const float MineWide = 0.14f;

    /// <summary>The TM-62 read once: its document, and how wide it is.</summary>
    public sealed class MineArt
    {
        public required GltfDocument Doc;
        public required GltfState State;
        public required float Across;
    }

    /// <summary>The mine model off <see cref="AssetRoot.Mine"/>, or null with
    /// the reason when it is not there.</summary>
    public static MineArt? Mine(out string? error)
    {
        string glb = AssetRoot.Mine + "/tm62.glb";
        var doc = new GltfDocument();
        var state = new GltfState();
        if (doc.AppendFromFile(glb, state) != Error.Ok)
        {
            error = $"no mine model at {glb}";
            return null;
        }
        error = null;
        float across = 0.328f;   // tm62.json's size.diameter, if it is not there
        try
        {
            using JsonDocument side = JsonDocument.Parse(File.ReadAllText(AssetRoot.Mine + "/tm62.json"));
            across = side.RootElement.GetProperty("size").GetProperty("diameter").GetSingle();
        }
        catch (Exception e)
        {
            error = $"tm62.json: {e.Message} - {across} m across";
        }
        return new MineArt { Doc = doc, State = state, Across = across };
    }

    /// <summary>A mine on <paramref name="cell"/>, set off its middle by the
    /// cell's hash (<see cref="Mines.Offset"/>) and turned by it, added to
    /// <paramref name="parent"/>. Answers the node and where it lies.</summary>
    public static Node3D LayMine(Node parent, MineArt art, HexField field, Vector2I cell, Vector2 hexSize,
                                 bool pbr, float squash, float rise, out Vector3 at)
    {
        var model = (Node3D)art.Doc.GenerateScene(art.State);
        if (!pbr)
            Toon.Dress(model);
        Vector2 flat = field.FlatAnchor(cell) + field.CentreOffset + Mines.Offset(cell, hexSize);
        at = Foot(field, new Vector3(flat.X, 0.0f, flat.Y / squash), squash, rise);
        // Turned by the cell's hash too, so a row of them is not a stamp.
        float turn = Plumes.Hash01(cell.Y * 1021 + cell.X, 271) * Mathf.Tau;
        float scale = MineWide * hexSize.X / art.Across;
        var node = new Node3D
        {
            Name = $"Mine_{cell.X}_{cell.Y}",
            Transform = new Transform3D(new Basis(Vector3.Up, turn).Scaled(Vector3.One * scale), at),
        };
        node.AddChild(model);
        parent.AddChild(node);
        return node;
    }

    // --- masonry --------------------------------------------------------------

    /// <summary>
    /// The wall or capon a walled cell describes, built and added to
    /// <paramref name="parent"/>: <see cref="Masonry.Laying"/> and
    /// <see cref="Masonry.Sheltering"/> say which recipe and which side, and a
    /// capon takes the bunker model's own box (<paramref name="bunker"/>) so a
    /// bomb lands on the roof that is drawn. <paramref name="fallback"/> is the
    /// recipe for a cell with no masonry of its own (the bench's ring); null
    /// lays nothing there. <paramref name="solved"/> skips the rig - nothing
    /// on the board can knock the wall down.
    /// </summary>
    public static WallProp? Wall(Node parent, HexField field, Stage3D stage, Vector2I cell, Masonry? masonry,
                                 int channel, bool cel, bool solved, BunkerProp.Sizes? bunker,
                                 (WallKit.Recipe Recipe, int Bearing)? fallback = null)
    {
        if ((masonry?.Laying() ?? fallback) is not ({ } recipe, int bearing))
            return null;
        (CaponKit.Recipe Recipe, int Bearing)? shelter = masonry?.Sheltering();
        if (shelter is { } s)
        {
            bearing = s.Bearing;
            if (bunker is { } size)
            {
                s.Recipe.WallHigh = size.RoofUnder;
                s.Recipe.RoofThick = size.Height - size.RoofUnder;
                s.Recipe.SlitLow = size.SlitLow;
                s.Recipe.SlitHigh = size.SlitHigh;
            }
        }
        var prop = new WallProp
        {
            Field = field, Stage = stage, Cell = cell,
            Recipe = recipe, Capon = shelter?.Recipe, Borrow = null,
            Channel = channel,
            // the model's look on the bricks, as on the tank and the trees
            Cel = cel,
        };
        parent.AddChild(prop);
        prop.Solved = solved;
        prop.Bearing = bearing;
        prop.Build();
        return prop;
    }

    /// <summary>
    /// The hex bunker on a capon's cell, where its prop stands and turned as it
    /// is turned, the prop's own pieces put out of sight. Null, with the
    /// problem, when the prop has no stack or the model does not build.
    /// </summary>
    public static BunkerProp? Bunker(Node parent, WallProp prop, float radius, bool pbr, out string? problem)
    {
        problem = null;
        if (prop.Stack is not { } stack)
            return null;
        var bunker = new BunkerProp
        {
            Name = $"Bunker{prop.Cell.X}_{prop.Cell.Y}",
            Radius = radius, Anchor = stack.Anchor, Lay = prop.Lay, Cell = prop.Cell, Pbr = pbr,
        };
        parent.AddChild(bunker);
        if (!bunker.Build())
        {
            problem = bunker.Problem;
            bunker.QueueFree();
            return null;
        }
        stack.Visible = false;
        return bunker;
    }
}
