using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The generator's tree model (<c>pipeline/tree_gen.py</c>, <c>tree.glb</c> +
/// <c>tree.json</c>) as any volumetric board stands it: read from disk, dressed
/// by <see cref="Toon"/> with its leaves as foliage, the burn contract on its
/// materials, its scale off its sprite's, and the sprite wood's wind
/// (docs/props.md, "Tree3D"). Shared by <c>Tree3DBench</c>, which looks at the
/// models, and <c>Tank3DBench</c>, whose board wears them on its wooded cells;
/// neither is known here. The crown's line is <see cref="CrownOutline"/>.
/// </summary>
public static class TreeModel
{
    /// <summary>The scale tree art is drawn at on the board (<see cref="PropTier"/>,
    /// <c>Detail</c> for the trees): the sprites are rendered eight times over.</summary>
    public const float PropDetail = 8.0f;

    /// <summary>What the trees' own surfaces leave in the stencil (not their
    /// ink): the outline is drawn on these pixels and on no others. Not 0
    /// (everything), not 7 (the turret's), not 1 (the wall's).</summary>
    public const int Stencil = 3;

    /// <summary>Board px to a model metre (docs/tree-species-plan.md, T3):
    /// one for every tree, so a pine stands as much taller than an oak as it
    /// is. The oaks' own - the mean of their sprites' <c>px_per_m</c>,
    /// 138-147 - at <see cref="PropDetail"/>: an oak keeps the height it had
    /// when each was fitted to its sprite, within 4%.</summary>
    public const float Metre = 140.75f / PropDetail;

    /// <summary>A tree's folder: published under <c>assets/Models/Trees/</c>,
    /// or - only when it is not - the pipeline's draft of a kind,
    /// <c>pipeline/out/trees/species/</c> (<c>tree_species.make</c>), so a kind
    /// is looked at on the board before it goes into LFS (T5). A published
    /// tree always wins.</summary>
    public static string Dir(string name)
    {
        string published = AssetRoot.Trees + "/" + name;
        string draft = AssetRoot.Repo + "/pipeline/out/trees/species/" + name;
        return !Directory.Exists(published) && File.Exists(draft + "/tree.json") ? draft : published;
    }

    /// <summary>A model read and dressed: its scene (not yet in the tree), its
    /// sidecar's root, board px to its metre, its size in metres, and the
    /// materials its burn and its leaves' flutter go into.</summary>
    public sealed class Loaded
    {
        public required Node Scene;
        public required JsonElement Json;
        public required float Ppm, Height, Width, Depth;
        /// <summary>Where its fire sits: the sidecar's <c>seats</c>, a kind's
        /// own crown, else the oak's <see cref="Seats"/>.</summary>
        public required Vector3[] Seats;
        public readonly List<ShaderMaterial> Burn = new();
        public readonly List<ShaderMaterial> Leaves = new();
    }

    /// <summary>
    /// One model: <c>tree.glb</c> read from disk (<c>AssetRoot</c>'s reason, as
    /// <see cref="TankModel"/> reads <c>tank.glb</c>), dressed by
    /// <see cref="Toon"/> unless <paramref name="pbr"/>, its scale the
    /// sidecar's <c>sprites.px_per_m</c> at the eighth the board draws tree art
    /// at (<see cref="PropTier"/>) - so it stands as tall as its own sprite.
    /// glTF's +Z is the tree's front and the board camera's too. Null, and why
    /// in <paramref name="error"/>, when the files are not there.
    /// </summary>
    public static Loaded? Load(string name, bool pbr, out string? error)
    {
        string dir = Dir(name);
        // <b>Each species read once, each tree its own meshes.</b> Reading the
        // glb was most of a tree's 85 ms, and a wood is seven of a few species.
        // The scenes a state generates share their meshes, and Toon.Dress puts
        // the cel materials on the mesh - so a tree's burn would be every tree's
        // of its kind; each mesh is copied before the dressing, which is what
        // keeps one oak burning while the next stands.
        if (!Read.TryGetValue(name, out (GltfDocument Doc, GltfState State, JsonElement Json) read))
        {
            var doc = new GltfDocument();
            var state = new GltfState();
            Error err = doc.AppendFromFile(dir + "/tree.glb", state);
            if (err != Error.Ok)
            {
                error = $"{dir}/tree.glb: {err}";
                return null;
            }
            using JsonDocument file = JsonDocument.Parse(File.ReadAllText(dir + "/tree.json"));
            read = (doc, state, file.RootElement.Clone());
            Read[name] = read;
        }
        Node scene = read.Doc.GenerateScene(read.State);
        Own(scene);
        JsonElement j = read.Json;
        var tree = new Loaded
        {
            Scene = scene, Json = j,
            Ppm = Metre,
            Seats = SeatsOf(j),
            Height = j.GetProperty("height").GetSingle(),
            Width = j.GetProperty("width").GetSingle(),
            Depth = j.GetProperty("depth").GetSingle(),
        };
        // the plate's surfaces by their glTF names, before Toon swaps the materials
        var named = new List<(MeshInstance3D Mesh, int Surface, string Name)>();
        Surfaces(scene, named);
        var plated = new Dictionary<MeshInstance3D, HashSet<int>>();
        foreach ((MeshInstance3D m, int s, string mat) in named)
            if (IsPlate(mat))
                (plated.TryGetValue(m, out HashSet<int>? set) ? set : plated[m] = new HashSet<int>()).Add(s);
        if (!pbr)
        {
            var roles = new List<(MeshInstance3D Mesh, int Surface, string Name)>();
            Surfaces(scene, roles);
            Toon.Dress(scene, KeepsNormals, IsFoliage, Stencil);
            foreach ((MeshInstance3D m, int s, string mat) in roles)
                if (m.Mesh.SurfaceGetMaterial(s) is ShaderMaterial cel)
                {
                    Contract(cel, mat, tree.Burn);
                    if (mat == "TreeGame.Leaf" && !tree.Leaves.Contains(cel))
                        tree.Leaves.Add(cel);
                }
        }
        SplitPlate(plated);
        error = null;
        return tree;
    }

    /// <summary>The node that holds a model's root plate (<see cref="SplitPlate"/>).</summary>
    public const string PlateName = "RootPlate";

    /// <summary>The root plate's materials (<c>tree.json</c> <c>model.burn</c>):
    /// under the ground from the start, they never burn.</summary>
    private static bool IsPlate(string mat) => mat is "TreeGame.Soil" or "TreeGame.Root";

    /// <summary>
    /// The root plate (<c>TreeGame.Soil</c>, <c>TreeGame.Root</c>) moved off
    /// each mesh into one of its own beside it, hidden: standing, the ground
    /// hides it anyway, and drawn it was ~10 of a tree's 28 draw calls - the
    /// cel pass, the ink, the crown's mask and the shadow splits; 610 of the
    /// stress run's 3 663 (BOT-4). <see cref="TreeFall.Start"/> shows it
    /// (<see cref="Plate"/>), as the tree goes over and the plate comes out of
    /// the ground. Its surfaces keep their materials, so the fall's pose and
    /// the wind reach it as before.
    /// </summary>
    private static void SplitPlate(Dictionary<MeshInstance3D, HashSet<int>> plated)
    {
        foreach ((MeshInstance3D m, HashSet<int> plates) in plated)
            if (m.Mesh is ArrayMesh mesh)
                SplitPlate(m, mesh, plates);
    }

    private static void SplitPlate(MeshInstance3D m, ArrayMesh mesh, HashSet<int> plates)
    {
        var keep = new ArrayMesh();
        var plate = new ArrayMesh();
        for (int s = 0; s < mesh.GetSurfaceCount(); s++)
        {
            Material? mat = mesh.SurfaceGetMaterial(s);
            ArrayMesh to = plates.Contains(s) ? plate : keep;
            Godot.Collections.Array arrays = mesh.SurfaceGetArrays(s);
            // the custom channels' formats are the surface's own (Toon's ink normal)
            const long customs = ((1L << (int)Mesh.ArrayFormat.FormatCustomBits) - 1) << (int)Mesh.ArrayFormat.FormatCustom0Shift
                                 | ((1L << (int)Mesh.ArrayFormat.FormatCustomBits) - 1) << (int)Mesh.ArrayFormat.FormatCustom1Shift
                                 | ((1L << (int)Mesh.ArrayFormat.FormatCustomBits) - 1) << (int)Mesh.ArrayFormat.FormatCustom2Shift
                                 | ((1L << (int)Mesh.ArrayFormat.FormatCustomBits) - 1) << (int)Mesh.ArrayFormat.FormatCustom3Shift;
            to.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays, null, null,
                                    (Mesh.ArrayFormat)((long)mesh.SurfaceGetFormat(s) & customs));
            to.SurfaceSetMaterial(to.GetSurfaceCount() - 1, mat);
        }
        if (plate.GetSurfaceCount() == 0)
            return;
        m.Mesh = keep;
        m.AddChild(new MeshInstance3D { Name = PlateName, Mesh = plate, Visible = false });
    }

    /// <summary>The root plate under <paramref name="node"/> shown or hidden.</summary>
    public static void Plate(Node node, bool shown)
    {
        if (node is MeshInstance3D { Name: var n } p && n == PlateName)
            p.Visible = shown;
        foreach (Node child in node.GetChildren())
            Plate(child, shown);
    }

    /// <summary>The species read so far: the glb parsed and the sidecar.</summary>
    private static readonly Dictionary<string, (GltfDocument Doc, GltfState State, JsonElement Json)> Read = new();

    /// <summary>Every mesh under <paramref name="node"/> made this scene's own
    /// - see <see cref="Load"/>.</summary>
    private static void Own(Node node)
    {
        foreach (Node child in node.GetChildren())
            Own(child);
        if (node is MeshInstance3D { Mesh: { } mesh } m)
            m.Mesh = (Mesh)mesh.Duplicate();
    }

    /// <summary>Leaves and puff cores keep the normals the file gives them -
    /// their puff's, not their own face's (<c>tree_gen._puff_normals</c>).</summary>
    public static bool KeepsNormals(Material m) => m.ResourceName == "TreeGame.Core";

    /// <summary>Leaves are open sheets: both sides, their puff's normal, no ink
    /// (<see cref="Toon.Dress"/>).</summary>
    public static bool IsFoliage(Material m) => m.ResourceName == "TreeGame.Leaf";

    /// <summary>Every surface under <paramref name="node"/> with the name of
    /// its glTF material, before <see cref="Toon.Dress"/> swaps them.</summary>
    private static void Surfaces(Node node, List<(MeshInstance3D, int, string)> into)
    {
        foreach (Node child in node.GetChildren())
            Surfaces(child, into);
        if (node is not MeshInstance3D m || m.Mesh is null)
            return;
        for (int s = 0; s < m.Mesh.GetSurfaceCount(); s++)
            into.Add((m, s, m.Mesh.SurfaceGetMaterial(s)?.ResourceName ?? ""));
    }

    /// <summary>The burn contract of <c>tree.json</c> (<c>model.burn</c>) on one
    /// cel material and its ink: leaves and cores go, twigs show, bark chars.
    /// The windows are the Blender preview's (<c>tree_gen.BURN_WINDOW</c>).</summary>
    private static void Contract(ShaderMaterial cel, string mat, List<ShaderMaterial> into)
    {
        (int role, float window, bool ember, bool eat) = mat switch
        {
            "TreeGame.Leaf" => (1, 0.12f, true, false),
            "TreeGame.Core" => (1, 0.45f, false, true),
            "TreeGame.Twig" => (2, 0.0f, false, false),
            "TreeGame.Bark" => (3, 0.0f, false, false),
            _ => (0, 0.0f, false, false),
        };
        if (into.Contains(cel))
            return;
        foreach (ShaderMaterial m in cel.NextPass is ShaderMaterial ink ? new[] { cel, ink } : new[] { cel })
        {
            into.Add(m);
            if (role == 0)
                continue;
            m.Param("burn_role", role);
            m.Param("burn_window", window);
            m.Param("burn_eat", eat);
        }
        cel.Param("burn_ember", ember);
        // The crown shades itself by its normals, not by its shadow on itself
        // (Toon's sun_shadow): the leaves' shadows on the leaves under them
        // were dark triangles all over the lit side.
        if (role == 1)
            cel.Param("sun_shadow", false);
    }

    /// <summary>Where the fire sits in a crown, as shares of the model's width,
    /// height and depth from its foot (glTF: +Z the front, toward the eye):
    /// four ports, <see cref="CelBurn.MaxPorts"/>, on the crown's front half
    /// so the leaves in front hide only the flame's foot - two at the sides,
    /// one high, one low in the middle.</summary>
    public static readonly Vector3[] Seats =
    {
        new(-0.24f, 0.60f, 0.18f),
        new(0.22f, 0.66f, 0.14f),
        new(0.02f, 0.84f, 0.04f),
        new(0.04f, 0.50f, 0.26f),
    };

    /// <summary>A sidecar's <c>seats.at</c>, or the oak's when it has none.</summary>
    private static Vector3[] SeatsOf(JsonElement j)
    {
        if (!j.TryGetProperty("seats", out JsonElement s) || !s.TryGetProperty("at", out JsonElement at))
            return Seats;
        var seats = new Vector3[at.GetArrayLength()];
        for (int i = 0; i < seats.Length; i++)
            seats[i] = new Vector3(at[i][0].GetSingle(), at[i][1].GetSingle(), at[i][2].GetSingle());
        return seats;
    }

    /// <summary>How late in its cell's fire a tree catches, nought to one, off
    /// where it stands on the board (flat board px) - the hash
    /// <see cref="Wildfire.Of"/> is handed when nothing better is known.</summary>
    public static float StaggerOf(Vector2 at) =>
        (float)Grove.Hash01(Mathf.RoundToInt(at.X), Mathf.RoundToInt(at.Y), 533_011);

    /// <summary>A tree's fire: <c>CelBurn</c> with the numbers a crown wants
    /// (docs/props.md, "Лесной пожар"), its "hull length" the crown's width
    /// on the board. Its smoke thins by being eaten, not by coming apart
    /// (<see cref="CelBurn.Sparse"/>), and it clears the trees' stencil.</summary>
    public static CelBurn Fire(Node parent, string name, Loaded model)
    {
        var fire = new CelBurn
        {
            Name = name + "Fire", Clears = true, RoundFoot = true,
            Sparse = 0.15f, Shrink = 0.15f, SmoulderWidth = 0.95f, ToneEase = 2.5f,
        };
        parent.AddChild(fire);
        fire.Build(model.Width * model.Ppm);
        return fire;
    }

    /// <summary>The cull box's margin of a standing tree, model metres: the
    /// wind's lean and a blast's throw move the crown a metre or two.</summary>
    public const float StandMargin = 2.0f;

    /// <summary>
    /// A model lies outside the box it stands in once it is down: culled by
    /// that box, a fallen crown blinked out at the screen's edge. So a falling
    /// tree (<see cref="TreeFall.Start"/>) is never culled - and only a falling
    /// one (BOT-5): with that margin from the planting, every tree stood in
    /// every shadow split and every cull, 10 of the stress run's 23.5 M
    /// primitives. Standing (<paramref name="down"/> false) it has
    /// <see cref="StandMargin"/>.
    /// </summary>
    public static void Margin(Node node, bool down = false)
    {
        if (node is GeometryInstance3D g)
            g.ExtraCullMargin = down ? 16384.0f : StandMargin;
        foreach (Node child in node.GetChildren())
            Margin(child, down);
    }

    // --- the wind -------------------------------------------------------------

    /// <summary>The sprite wood's wind (<c>Grove</c>): a crown drift of
    /// <see cref="WindDrift"/> px at <see cref="WindHeight"/> px up at full
    /// gust, a gust wave crossing along x at <see cref="GustRate"/> rad/s,
    /// <see cref="GustTravel"/> rad a px, and a third of it for a burnt
    /// trunk. To the screen's right and back, as the sprites lean.</summary>
    public const float WindDrift = 2.6f, WindHeight = 120.0f, GustRate = 0.55f, GustTravel = 0.0035f;
    public const float CharredSway = 0.33f;
    /// <summary>What the model adds on top, m at full gust: the puffs'
    /// billow, and the leaves' flutter.</summary>
    public const float Billow = 0.10f, Flutter = 0.035f;

    /// <summary>A tree's own sway, off where it stands on the board (flat
    /// board px): the sprite wood's hash for its phase and its rate
    /// (<c>Grove</c>), so a model sways as the sprite there would.</summary>
    public static (float Phase, float Rate) SwayOf(Vector2 at)
    {
        int x = Mathf.RoundToInt(at.X), y = Mathf.RoundToInt(at.Y);
        return (Mathf.Tau * (float)Grove.Hash01(x, y, 901_001), 0.8f + 0.5f * (float)Grove.Hash01(x, y, 901_003));
    }

    /// <summary>A frame's clock and wind for every tree in the scene: one
    /// clock and one wind to a scene, as the grass's.</summary>
    public static void Weather(float weather, float wind)
    {
        RenderingServer.GlobalShaderParameterSet(Toon.WeatherName, weather);
        RenderingServer.GlobalShaderParameterSet(Toon.TreeWindName, wind);
    }

    /// <summary>
    /// A tree into the wind, once, as it is planted: <c>Grove</c>'s lean -
    /// <c>wind (0.35 + 0.65 gust) sin(weather rate + phase) / height</c>, the
    /// gust one long wave along x so neighbours rise a beat apart, the phase
    /// and rate each tree's own (<see cref="SwayOf"/>) - is worked out in
    /// <see cref="Toon.WindCode"/> off what is set here: the phase, the rate,
    /// the foot's world x, and the world's wind (along x) in the model's own
    /// frame. The leaves (<paramref name="leaves"/>) flutter as well.
    /// </summary>
    public static void Wind(Node3D holder, IEnumerable<ShaderMaterial> burn, IEnumerable<ShaderMaterial> leaves,
                            float phase, float rate)
    {
        Vector3 own = holder.GlobalBasis.Orthonormalized().Inverse() * Vector3.Right;
        var dir = new Vector2(own.X, own.Z).Normalized();
        foreach (ShaderMaterial m in burn)
        {
            m.Param("wind_rate", rate);
            m.Param("wind_phase", phase);
            m.Param("wind_foot", holder.GlobalPosition.X);
            m.Param("wind_dir", dir);
        }
        foreach (ShaderMaterial m in leaves)
            m.Param("wind_leaf", true);
    }

    /// <summary>What <see cref="Sway"/> last wrote to a tree: it writes again
    /// only what changed.</summary>
    public sealed class Swayed
    {
        public float Still = 1.0f, Keep = 1.0f, Shiver;
        public Vector2 Push;
    }

    /// <summary>
    /// A frame of what moves one tree beyond the wind (<see cref="Wind"/>):
    /// <paramref name="still"/> takes the wind out as the trunk goes over,
    /// <paramref name="spent"/> its crown gone to the fire (a burnt tree keeps
    /// <see cref="CharredSway"/>), <paramref name="push"/> a lean on top -
    /// world x and z as a share of the tree's height, a blast's (the scene's
    /// spring) - and <paramref name="shiver"/> more billow and flutter with it.
    /// Written only when it differs from <paramref name="last"/>: a tree at
    /// rest costs nothing a frame.
    /// </summary>
    public static void Sway(Node3D holder, IEnumerable<ShaderMaterial> burn, Swayed last,
                            float still = 1.0f, float spent = 0.0f, Vector2 push = default, float shiver = 0.0f)
    {
        float keep = Mathf.Lerp(1.0f, CharredSway, spent);
        Vector2 own = Vector2.Zero;
        if (push != Vector2.Zero)
        {
            Vector3 p = holder.GlobalBasis.Orthonormalized().Inverse() * new Vector3(push.X, 0.0f, push.Y);
            own = new Vector2(p.X, p.Z);
        }
        if (still == last.Still && keep == last.Keep && shiver == last.Shiver && own == last.Push)
            return;
        (last.Still, last.Keep, last.Shiver, last.Push) = (still, keep, shiver, own);
        foreach (ShaderMaterial m in burn)
        {
            m.Param("wind_still", still);
            m.Param("wind_keep", keep);
            m.Param("wind_shiver", shiver);
            m.Param("wind_push", own);
        }
    }
}

/// <summary>
/// The crown's line, drawn in the frame and not on the mesh.
///
/// <b>Why not the ink.</b> Toon's ink is an inside-out hull and needs a closed
/// volume; a leaf is an open sheet (<see cref="Toon.Dress"/>, <c>foliage</c>),
/// and a shell at the leaves' tips (tried) showed its back through the thin
/// leaves at every puff's rim as a broad dark band - the rim is where the
/// leaves are seen edge on. <b>Why not the depth.</b> gl_compatibility gives a
/// shader no depth texture (<see cref="SheetBlast"/>).
///
/// So the trees are drawn a second time, white on nothing, into a mask of their
/// own (stand-ins on <see cref="MaskLayer"/>, a camera that follows the scene's),
/// and a pass over the whole frame puts ink on every tree pixel
/// (<see cref="TreeModel.Stencil"/>) that has empty mask within the line's
/// width: the inside of the crown's silhouette, notch for notch. The stencil
/// keeps it off whatever stands in front of a tree - a tank, a bush - where the
/// mask alone would run the line across it. The colour is the pixel's own
/// darkened, Toon's ink rule (<see cref="Toon.InkDark"/>).
///
/// <b>Between overlapping trees the same line</b>: each tree writes its number
/// (R) and its depth (G) into the mask, and the ink goes also where the next
/// pixel is another tree that is farther - on the front one's edge only.
/// </summary>
public sealed partial class CrownOutline : Node
{
    /// <summary>The layer the trees' stand-ins are on: the mask sees them, the
    /// scene's camera does not.</summary>
    public const uint MaskLayer = 1u << 18;

    /// <summary>The slab of view depth the mask's G spans, board px either side
    /// of the depth handed to <see cref="Follow"/>: 8 bits over it are ~5 px of
    /// depth, and trees that overlap stand tens of px apart.</summary>
    public const float MaskReach = 600.0f;

    private SubViewport _mask = null!;
    private Camera3D _eye = null!;
    private ShaderMaterial _line = null!;
    private readonly List<ShaderMaterial> _masks = new();
    private int _count;

    /// <summary>The mask and its camera under this, the line on
    /// <paramref name="camera"/>, which stops seeing the mask's layer.</summary>
    public void Build(Camera3D camera)
    {
        _mask = new SubViewport
        {
            Name = "CrownMask",
            TransparentBg = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
            Msaa3D = Viewport.Msaa.Disabled,
            Size = (Vector2I)camera.GetViewport().GetVisibleRect().Size,
        };
        AddChild(_mask);
        _eye = new Camera3D
        {
            Projection = Camera3D.ProjectionType.Orthogonal,
            KeepAspect = Camera3D.KeepAspectEnum.Height,
            CullMask = MaskLayer,
            Current = true,
        };
        _mask.AddChild(_eye);
        camera.CullMask &= ~MaskLayer;

        _line = new ShaderMaterial { Shader = OutlineShader };
        _line.Param("mask", _mask.GetTexture());
        _line.Param("dark", Toon.InkDark);
        camera.AddChild(new MeshInstance3D
        {
            Name = "CrownLine",
            Mesh = new QuadMesh { Size = new Vector2(2.0f, 2.0f) },
            MaterialOverride = _line,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            ExtraCullMargin = 16384.0f,
        });
    }

    /// <summary>
    /// The next tree into the mask: a stand-in for every mesh of
    /// <paramref name="scene"/> on the mask's layer, each surface on the mask
    /// of its burn role - what has burnt away is gone from the mask on the
    /// frame it goes from the picture. The masks go into its
    /// <paramref name="burn"/> (they burn and sway as it does) and the
    /// leaves' into its <paramref name="leaves"/> (they flutter).
    /// </summary>
    public void Ghost(Node scene, List<ShaderMaterial> burn, List<ShaderMaterial> leaves)
    {
        var masks = new ShaderMaterial[5];
        for (int k = 0; k < 5; k++)
        {
            masks[k] = MaskFor(_count);
            masks[k].Param("burn_role", k == 4 ? 1 : k);
            masks[k].Param("burn_window", k == 4 ? 0.45f : 0.12f);
            masks[k].Param("burn_eat", k == 4);
            burn.Add(masks[k]);
        }
        leaves.Add(masks[1]);   // role 1 without the eating: the leaves
        _count++;
        Stand(scene, masks);
    }

    private static void Stand(Node node, ShaderMaterial[] masks)
    {
        foreach (Node child in node.GetChildren())
            Stand(child, masks);
        if (node is not MeshInstance3D m || m.Mesh is null)
            return;
        var ghost = new MeshInstance3D
        {
            Mesh = m.Mesh,
            Layers = MaskLayer,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        for (int s = 0; s < m.Mesh.GetSurfaceCount(); s++)
        {
            int role = 0;
            if (m.Mesh.SurfaceGetMaterial(s) is ShaderMaterial cel)
            {
                role = cel.Param("burn_role").AsInt32();
                // the eaten core's own: role 1 is the leaves' as well
                if (role == 1 && cel.Param("burn_eat").AsBool())
                    role = 4;
            }
            ghost.SetSurfaceOverrideMaterial(s, masks[Mathf.Clamp(role, 0, 4)]);
        }
        m.AddChild(ghost);
    }

    /// <summary>The mask of the <paramref name="i"/>-th tree: its number in R
    /// (1..7 of 8, round again past seven - two trees a number apart are never
    /// the two that overlap here) and its depth in G, over the slab
    /// <see cref="Follow"/> sets.</summary>
    private ShaderMaterial MaskFor(int i)
    {
        var m = new ShaderMaterial { Shader = MaskShader };
        m.Param("id", (i % 7 + 1) / 8.0f);
        _masks.Add(m);
        return m;
    }

    /// <summary>The mask's camera onto <paramref name="camera"/>, the line
    /// Toon's ink width in board px at <paramref name="zoom"/>, and the depth
    /// slab round <paramref name="depth"/> (view depth of the board's middle).</summary>
    public void Follow(Camera3D camera, float zoom, float depth)
    {
        _mask.Size = (Vector2I)camera.GetViewport().GetVisibleRect().Size;
        _eye.GlobalTransform = camera.GlobalTransform;
        _eye.Size = camera.Size;
        _eye.Near = camera.Near;
        _eye.Far = camera.Far;
        _line.Param("width", Mathf.Max(1.0f, Toon.InkWidth * zoom));
        foreach (ShaderMaterial m in _masks)
        {
            m.Param("near", depth - MaskReach);
            m.Param("span", 2.0f * MaskReach);
        }
    }

    private static readonly Shader MaskShader = new()
    {
        Code = @"
shader_type spatial;
render_mode unshaded, cull_disabled, shadows_disabled, fog_disabled;
uniform float id = 1.0;
uniform float near = 0.0;
uniform float span = 1.0;
varying vec3 world;
" + Toon.NoiseCode + Toon.BurnCode + Toon.WindCode + Toon.FallCode + @"
void vertex() {
    vec3 n = NORMAL;
    wind_pose(VERTEX);
    fall_pose(VERTEX, n);
    world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}
void fragment() {
    if (burn_role != 0 && burn_gone(UV2.x, world)) {
        discard;
    }
    ALBEDO = vec3(id, clamp((-VERTEX.z - near) / span, 0.0, 1.0), 0.0);
}",
    };

    private static readonly Shader OutlineShader = new()
    {
        Code = @"
shader_type spatial;
render_mode unshaded, cull_disabled, depth_test_disabled, depth_draw_never, shadows_disabled, fog_disabled;
stencil_mode read, compare_equal, " + TreeModel.Stencil + @";
uniform sampler2D mask : filter_nearest;
uniform sampler2D screen : hint_screen_texture, filter_nearest;
uniform float width = 2.0;
uniform float dark = 0.28;
void vertex() {
    POSITION = vec4(VERTEX.xy, 0.0, 1.0);
}
void fragment() {
    vec2 px = width / VIEWPORT_SIZE;
    vec4 me = texture(mask, SCREEN_UV);
    bool edge = false;
    for (int i = 0; i < 8; i++) {
        float a = float(i) * 0.785398;
        vec4 q = texture(mask, SCREEN_UV + vec2(cos(a), sin(a)) * px);
        // nothing there: the silhouette; another tree, and farther: this
        // tree's edge over it - so the line is drawn on the front one only
        edge = edge || q.a < 0.5
            || (me.a >= 0.5 && abs(q.r - me.r) > 0.03 && q.g > me.g + 0.004);
    }
    if (!edge) {
        discard;
    }
    ALBEDO = texture(screen, SCREEN_UV).rgb * dark;
}",
    };
}
