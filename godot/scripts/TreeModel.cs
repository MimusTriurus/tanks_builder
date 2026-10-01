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

    public static string Dir(string name) => AssetRoot.Trees + "/" + name;

    /// <summary>A model read and dressed: its scene (not yet in the tree), its
    /// sidecar's root, board px to its metre, its size in metres, and the
    /// materials its burn and its leaves' flutter go into.</summary>
    public sealed class Loaded
    {
        public required Node Scene;
        public required JsonElement Json;
        public required float Ppm, Height, Width, Depth;
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
        var doc = new GltfDocument();
        var state = new GltfState();
        Error err = doc.AppendFromFile(dir + "/tree.glb", state);
        if (err != Error.Ok)
        {
            error = $"{dir}/tree.glb: {err}";
            return null;
        }
        Node scene = doc.GenerateScene(state);
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(dir + "/tree.json"));
        JsonElement j = json.RootElement.Clone();
        var tree = new Loaded
        {
            Scene = scene, Json = j,
            Ppm = j.GetProperty("sprites").GetProperty("px_per_m").GetSingle() / PropDetail,
            Height = j.GetProperty("height").GetSingle(),
            Width = j.GetProperty("width").GetSingle(),
            Depth = j.GetProperty("depth").GetSingle(),
        };
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
        error = null;
        return tree;
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
            m.SetShaderParameter("burn_role", role);
            m.SetShaderParameter("burn_window", window);
            m.SetShaderParameter("burn_eat", eat);
        }
        cel.SetShaderParameter("burn_ember", ember);
        // The crown shades itself by its normals, not by its shadow on itself
        // (Toon's sun_shadow): the leaves' shadows on the leaves under them
        // were dark triangles all over the lit side.
        if (role == 1)
            cel.SetShaderParameter("sun_shadow", false);
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

    /// <summary>A model lies outside the box it stands in once it is down:
    /// culled by that box, a fallen crown blinked out at the screen's edge.</summary>
    public static void Margin(Node node)
    {
        if (node is GeometryInstance3D g)
            g.ExtraCullMargin = 16384.0f;
        foreach (Node child in node.GetChildren())
            Margin(child);
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

    /// <summary>
    /// A frame of the wind on one tree: <c>Grove</c>'s lean -
    /// <c>wind (0.35 + 0.65 gust) sin(phase) / height</c>, the gust one long wave
    /// along x so neighbours rise a beat apart, the phase each tree's own - into
    /// <see cref="Toon.WindCode"/>, the billow and the flutter with the gust.
    /// <paramref name="still"/> takes it out as the trunk goes over,
    /// <paramref name="spent"/> its crown gone to the fire (a burnt tree keeps
    /// <see cref="CharredSway"/>). The world's wind is along x; a turned model
    /// gets it in its own frame.
    /// </summary>
    /// <paramref name="push"/> is a lean on top, world x and z as a share of
    /// the tree's height - a blast's (the scene's spring) - and
    /// <paramref name="shiver"/> more billow and flutter with it.
    public static void Sway(Node3D holder, IEnumerable<ShaderMaterial> burn, IEnumerable<ShaderMaterial> leaves,
                            float weather, float phase, float rate, float wind, float still = 1.0f, float spent = 0.0f,
                            Vector2 push = default, float shiver = 0.0f)
    {
        Vector3 foot = holder.GlobalPosition;
        float gust = 0.5f + 0.5f * Mathf.Sin(weather * GustRate - foot.X * GustTravel);
        float lean = wind * WindDrift * (0.35f + 0.65f * gust) * Mathf.Sin(weather * rate + phase) / WindHeight;
        float keep = Mathf.Lerp(1.0f, CharredSway, spent);
        Vector3 own = holder.GlobalBasis.Orthonormalized().Inverse() * Vector3.Right;
        var dir = new Vector2(own.X, own.Z).Normalized();
        float billow = wind * Billow * (0.3f + 0.7f * gust) * still * keep + Billow * shiver * still;
        float leaning = lean * still * keep;
        if (push != Vector2.Zero)
        {
            // The wind's lean and the push added as two leans in the model's
            // own frame - the shader has one way and one size.
            Vector3 p = holder.GlobalBasis.Orthonormalized().Inverse() * new Vector3(push.X, 0.0f, push.Y);
            Vector2 sum = dir * leaning + new Vector2(p.X, p.Z) * still;
            leaning = sum.Length();
            if (leaning > 1e-6f)
                dir = sum / leaning;
        }
        foreach (ShaderMaterial m in burn)
        {
            m.SetShaderParameter("wind_dir", dir);
            m.SetShaderParameter("wind_lean", leaning);
            m.SetShaderParameter("wind_billow", billow);
            m.SetShaderParameter("wind_time", weather * rate);
        }
        foreach (ShaderMaterial m in leaves)
            m.SetShaderParameter("wind_flutter", shiver == 0.0f ? wind * Flutter * (0.3f + 0.7f * gust) * still
                                                                : (wind * (0.3f + 0.7f * gust) + shiver) * Flutter * still);
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
        _line.SetShaderParameter("mask", _mask.GetTexture());
        _line.SetShaderParameter("dark", Toon.InkDark);
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
            masks[k].SetShaderParameter("burn_role", k == 4 ? 1 : k);
            masks[k].SetShaderParameter("burn_window", k == 4 ? 0.45f : 0.12f);
            masks[k].SetShaderParameter("burn_eat", k == 4);
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
                role = cel.GetShaderParameter("burn_role").AsInt32();
                // the eaten core's own: role 1 is the leaves' as well
                if (role == 1 && cel.GetShaderParameter("burn_eat").AsBool())
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
        m.SetShaderParameter("id", (i % 7 + 1) / 8.0f);
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
        _line.SetShaderParameter("width", Mathf.Max(1.0f, Toon.InkWidth * zoom));
        foreach (ShaderMaterial m in _masks)
        {
            m.SetShaderParameter("near", depth - MaskReach);
            m.SetShaderParameter("span", 2.0f * MaskReach);
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
