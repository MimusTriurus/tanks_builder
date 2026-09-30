using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// <c>res://Tree3D.tscn</c>: the generator's tree models (<c>pipeline/tree_gen.py</c>,
/// <c>tree.glb</c> + <c>tree.json</c>) on the volumetric board, under
/// <see cref="Toon"/>, and nothing else on it: no sprite wood, bushes or stones -
/// the board's undergrowth is sprite art, and only the models are looked at here.
///
/// <b>Tank3D's board, camera, sun and shadow skin, and nothing of its tank</b>:
/// the tree models are what is being looked at, and Tank3D cannot stand up
/// without a tank in <c>Models/</c>. Felling the model is not played yet.
///
/// <b>The wood burns</b> (docs/props.md, "Tree3D: лесной пожар"): the board's
/// own <see cref="Wildfire"/> - cells, spread, each tree's stagger and its
/// <see cref="Wildfire.Coat"/> - with the model burning itself by its burn
/// contract (<see cref="Toon.BurnCode"/>) and a 3D tank's fire and smoke
/// (<see cref="CelBurn"/>) in its crown. LMB lights the cell under the cursor,
/// <c>R</c> puts the wood back.
/// </summary>
public sealed partial class Tree3DBench : Node3D
{
    // --- flags -----------------------------------------------------------

    /// <summary><c>--trees A,B,..</c>: which folders under
    /// <c>pipeline/out/trees/</c> to stand, in the order of <see cref="Spots"/>.</summary>
    private string[] _trees = { "Oak_001", "Oak_002", "Oak_003", "Oak_004", "Oak_005", "Oak_006" };
    private float _zoom = 2.2f;
    private bool _pbr;
    /// <summary><c>--no-outline</c>: the crowns without their line, to hold
    /// against it.</summary>
    private bool _noOutline;
    private bool _row;
    private string? _capturePath;
    private int _captureAt = 30;
    /// <summary><c>--burn q,r</c>: the cell lit <see cref="LightAfter"/> s in,
    /// so a capture has a start to count from.</summary>
    private Vector2I? _burnAt;
    /// <summary><c>--record dir</c>: every <c>--record-every</c> th frame to
    /// <c>dir/NNNN.png</c> for <c>--record-for</c> s, then quit - a fire to
    /// be looked at as a film (with <c>--fixed-fps</c>).</summary>
    private string? _recordDir;
    private int _recordEvery = 6;
    private float _recordFor = 18.0f;
    private const float LightAfter = 0.5f;
    private float _clock;

    // --- scene -----------------------------------------------------------

    private Camera3D _camera = null!;
    private DirectionalLight3D _sun = null!;
    private AtlasSet? _tile;
    private HexField? _field;
    private Stage3D? _stage;
    private readonly List<Node3D> _models = new();
    private int _frame;

    private float Squash => _tile is { } a && a.HexRect.Size.X > 0
        ? 2.0f * a.HexRect.Size.Y / (Mathf.Sqrt(3.0f) * a.HexRect.Size.X)
        : 0.5f;

    private float RiseFactor => Mathf.Sqrt(Mathf.Max(0.0f, 1.0f - Squash * Squash));

    /// <summary>The camera's stand-off, Tank3D's and for its reason: an
    /// orthographic depth is linear, and at 4000 the board's rims drowned in it.</summary>
    private const float Back = 1500.0f;

    /// <summary>The board: five by three of plain ground, flat - its own map,
    /// because every named board carries water, relief or a wood.</summary>
    private static readonly string[] Ground =
    {
        // 01234
        ".....", // r0
        ".....", // r1
        ".....", // r2
    };

    /// <summary>Where each model stands: a cell and an offset from its centre in
    /// ground px (x right, y down the screen). One alone on each side of the
    /// middle, one in the middle, three on one cell as a wood carries several,
    /// one on the far row.</summary>
    private static readonly (Vector2I Cell, Vector2 Off)[] Spots =
    {
        (new Vector2I(1, 1), Vector2.Zero),
        (new Vector2I(3, 1), new Vector2(-40.0f, -22.0f)),
        (new Vector2I(3, 1), new Vector2(42.0f, -12.0f)),
        (new Vector2I(3, 1), new Vector2(0.0f, 30.0f)),
        (new Vector2I(2, 0), Vector2.Zero),
        (new Vector2I(2, 1), Vector2.Zero),
    };

    /// <summary><c>--row</c>: three models in a row across the middle cell, left
    /// to right, a crown and a little apart - the looks of one seed held side
    /// by side (<c>tree_gen.remodel</c>).</summary>
    private static readonly (Vector2I Cell, Vector2 Off)[] Row =
    {
        (new Vector2I(2, 1), new Vector2(-125.0f, 0.0f)),
        (new Vector2I(2, 1), Vector2.Zero),
        (new Vector2I(2, 1), new Vector2(125.0f, 0.0f)),
    };

    public override void _Ready()
    {
        ReadFlags();
        _tile = LoadTile("MTP");
        GetViewport().Msaa3D = Viewport.Msaa.Msaa4X;
        BuildWorld();
        BuildBoard();
        BuildCamera();
        if (!_pbr && !_noOutline)
            BuildOutline();
        (Vector2I Cell, Vector2 Off)[] spots = _row ? Row : Spots;
        for (int i = 0; i < Math.Min(_trees.Length, spots.Length); i++)
            Stand(_trees[i], spots[i].Cell, spots[i].Off);
        FrameCamera();
        if (_stage is not null && _fire is not null)
            _stage.Blaze = _fire;
    }

    public override void _Process(double delta)
    {
        // The stage's per-frame pass (Tank3D never calls it): nothing of it is
        // needed while the board has no wood and no vehicles, but a board is
        // left as the stage expects to run.
        _stage?.Place(Array.Empty<Vehicle>());
        FrameCamera();
        Burn((float)delta);
        _frame++;
        if (_recordDir is not null && _frame % _recordEvery == 0)
        {
            GetViewport().GetTexture().GetImage().SavePng($"{_recordDir}/{_frame / _recordEvery:D4}.png");
            if (_clock >= _recordFor)
                GetTree().Quit();
        }
        if (_capturePath is not null && _frame == _captureAt)
        {
            Error err = GetViewport().GetTexture().GetImage().SavePng(_capturePath);
            GD.Print(err == Error.Ok ? $"capture: {_capturePath}" : $"capture to {_capturePath} failed: {err}");
            GetTree().Quit();
        }
    }

    // --- the trees ---------------------------------------------------------

    private static string TreeDir(string name) => AssetRoot.Repo + "/pipeline/out/trees/" + name;

    /// <summary>
    /// One model on the board: <c>tree.glb</c> read from disk (<c>AssetRoot</c>'s
    /// reason, as <see cref="TankModel"/> reads <c>tank.glb</c>), dressed by
    /// <see cref="Toon"/>, scaled so it stands as tall on the board as its own
    /// sprite does - the sidecar's <c>sprites.px_per_m</c> at the eighth the
    /// board draws tree art at (<see cref="PropTier"/>) - and set down on the
    /// ground at its foot. glTF's +Z is the tree's front and this camera's too.
    /// </summary>
    private void Stand(string name, Vector2I cell, Vector2 off)
    {
        string dir = TreeDir(name);
        var doc = new GltfDocument();
        var state = new GltfState();
        Error err = doc.AppendFromFile(dir + "/tree.glb", state);
        if (err != Error.Ok)
        {
            GD.Print($"tree3d: {dir}/tree.glb: {err}");
            return;
        }
        Node scene = doc.GenerateScene(state);
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(dir + "/tree.json"));
        JsonElement j = json.RootElement;
        float ppm = j.GetProperty("sprites").GetProperty("px_per_m").GetSingle() / PropDetail;
        ulong t0 = Time.GetTicksMsec();
        var tree = new TreeFire { Cell = cell };
        if (!_pbr)
        {
            var roles = new List<(MeshInstance3D Mesh, int Surface, string Name)>();
            Surfaces(scene, roles);
            Toon.Dress(scene, KeepsNormals, IsFoliage, TreeStencil);
            foreach ((MeshInstance3D m, int s, string mat) in roles)
                if (m.Mesh.SurfaceGetMaterial(s) is ShaderMaterial cel)
                    Contract(cel, mat, tree.Burn);
        }
        if (_mask is not null)
            Ghost(scene, MasksFor(_models.Count, tree.Burn));
        var holder = new Node3D { Name = name, Scale = Vector3.One * ppm };
        holder.AddChild(scene);
        AddChild(holder);
        if (_field is not null)
        {
            Vector2 flat = _field.FlatAnchor(cell) + _field.CentreOffset + off;
            holder.Position = Foot(new Vector3(flat.X, 0.0f, flat.Y / Squash));
        }
        _models.Add(holder);
        tree.Holder = holder;
        float h = j.GetProperty("height").GetSingle(), w = j.GetProperty("width").GetSingle();
        float d = j.GetProperty("depth").GetSingle();
        foreach (Vector3 p in Seats)
            tree.Seats.Add(new Vector3(p.X * w, p.Y * h, p.Z * d));
        Vector2 at = _field is null ? Vector2.Zero : _field.FlatAnchor(cell) + _field.CentreOffset + off;
        tree.Stagger = (float)Grove.Hash01(Mathf.RoundToInt(at.X), Mathf.RoundToInt(at.Y), StaggerSalt);
        // Its smoke thins by being eaten, not by coming apart: see CelBurn.Sparse.
        tree.Fire = new CelBurn
        {
            Name = name + "Fire", Clears = true, RoundFoot = true,
            Sparse = 0.15f, Shrink = 0.15f, SmoulderWidth = 0.95f, ToneEase = 2.5f,
        };
        AddChild(tree.Fire);
        // A tank's hull lengths, the crown's width here: the tongues, the
        // column and the light are all shares of it.
        tree.Fire.Build(w * ppm);
        _burning.Add(tree);
        _wooded.Add(cell);
        GD.Print($"tree3d: {name} on {cell} +{off}, {ppm:F2} px/m, "
                 + $"{j.GetProperty("height").GetSingle() * ppm:F0} px tall, "
                 + $"{j.GetProperty("model").GetProperty("tris").GetInt32()} tris, "
                 + $"dressed in {Time.GetTicksMsec() - t0} ms");
    }

    // --- the fire ------------------------------------------------------------

    /// <summary>One model on fire: its cell, how late it catches, the
    /// materials its burn goes into, and its flame and smoke.</summary>
    private sealed class TreeFire
    {
        public Node3D Holder = null!;
        public Vector2I Cell;
        public float Stagger;
        public readonly List<ShaderMaterial> Burn = new();
        public readonly List<Vector3> Seats = new();
        public CelBurn Fire = null!;
        public readonly List<Vector3> Ports = new();
    }

    private readonly List<TreeFire> _burning = new();
    private readonly HashSet<Vector2I> _wooded = new();
    private Wildfire? _fire;
    private const int StaggerSalt = 533_011;

    /// <summary>Where the fire sits in a crown, as shares of the model's width,
    /// height and depth from its foot (glTF: +Z the front, toward the eye):
    /// four ports, <see cref="CelBurn.MaxPorts"/>, on the crown's front half
    /// so the leaves in front hide only the flame's foot - two at the sides,
    /// one high, one low in the middle.</summary>
    private static readonly Vector3[] Seats =
    {
        new(-0.24f, 0.60f, 0.18f),
        new(0.22f, 0.66f, 0.14f),
        new(0.02f, 0.84f, 0.04f),
        new(0.04f, 0.50f, 0.26f),
    };

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
        if (role == 0 || into.Contains(cel))
            return;
        foreach (ShaderMaterial m in cel.NextPass is ShaderMaterial ink ? new[] { cel, ink } : new[] { cel })
        {
            m.SetShaderParameter("burn_role", role);
            m.SetShaderParameter("burn_window", window);
            m.SetShaderParameter("burn_eat", eat);
            into.Add(m);
        }
        cel.SetShaderParameter("burn_ember", ember);
        // The crown shades itself by its normals, not by its shadow on itself
        // (Toon's sun_shadow): the leaves' shadows on the leaves under them
        // were dark triangles all over the lit side.
        if (role == 1)
            cel.SetShaderParameter("sun_shadow", false);
    }

    private void Burn(float dt)
    {
        _clock += dt;
        if (_fire is null)
            return;
        if (_burnAt is Vector2I lit && _clock >= LightAfter)
        {
            _fire.Light(lit);
            _burnAt = null;
        }
        _fire.Tick(dt);
        Basis eye = _camera.GlobalBasis;
        foreach (TreeFire t in _burning)
        {
            Wildfire.Coat coat = _fire.Of(t.Cell, t.Stagger);
            // The crown goes with the fuel (Coat.Spent: between the flame
            // coming up and the sprite's handover shutting), the paint with the
            // char - the two clocks the sprite's pair has.
            foreach (ShaderMaterial m in t.Burn)
            {
                m.SetShaderParameter("burn", coat.Spent);
                m.SetShaderParameter("charred", coat.Char);
            }
            t.Fire.Fire = coat.Flame;
            t.Fire.Smoke = coat.Smoke;
            t.Fire.Smoulder = coat.Burnt;
            // The flame goes down with the crown: from the crown's front onto
            // the limbs and the fork as the fuel goes - left where the crown
            // was, it hung in the air round the bare twigs.
            float down = coat.Spent * coat.Spent;
            t.Ports.Clear();
            foreach (Vector3 p in t.Seats)
                t.Ports.Add(t.Holder.ToGlobal(p.Lerp(new Vector3(p.X * 0.4f, p.Y * 0.62f, p.Z * 0.3f), down)));
            t.Fire.Tick(dt, t.Ports, eye);
        }
    }

    private void Douse()
    {
        _fire?.Douse();
        _clock = 0.0f;
        foreach (TreeFire t in _burning)
            t.Fire.Reset();
        Burn(0.0f);
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventKey { Pressed: true, Echo: false, Keycode: Key.R })
            Douse();
        else if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } b
                 && _field is not null && _fire is not null)
        {
            Vector3 from = _camera.ProjectRayOrigin(b.Position), way = _camera.ProjectRayNormal(b.Position);
            if (Mathf.Abs(way.Y) < 1e-4f)
                return;
            Vector3 g = from + way * (-from.Y / way.Y);
            Vector2I cell = _field.CellUnder(new Vector2(g.X, g.Z * Squash));
            GD.Print(_fire.Light(cell) ? $"tree3d: {cell} lit" : $"tree3d: {cell} has nothing to burn");
        }
    }

    /// <summary>Leaves and puff cores keep the normals the file gives them -
    /// their puff's, not their own face's (<c>tree_gen._puff_normals</c>).</summary>
    private static bool KeepsNormals(Material m) => m.ResourceName == "TreeGame.Core";

    /// <summary>Leaves are open sheets: both sides, their puff's normal, no ink
    /// (<see cref="Toon.Dress"/>).</summary>
    private static bool IsFoliage(Material m) => m.ResourceName == "TreeGame.Leaf";

    /// <summary>The scale tree art is drawn at on the board (<see cref="PropTier"/>,
    /// <c>Detail</c> for the trees): the sprites are rendered eight times over.</summary>
    private const float PropDetail = 8.0f;

    // --- the crown's outline -------------------------------------------------

    /// <summary>What the trees' own surfaces leave in the stencil (not their
    /// ink): the outline is drawn on these pixels and on no others. Not 0
    /// (everything), not 7 (the turret's), not 1 (the wall's).</summary>
    private const int TreeStencil = 3;

    /// <summary>The layer the trees' stand-ins are on: the mask sees them, the
    /// main camera does not.</summary>
    private const uint MaskLayer = 1u << 18;

    private SubViewport? _mask;
    private Camera3D? _maskEye;

    /// <summary>
    /// The crown's line, drawn in the frame and not on the mesh.
    ///
    /// <b>Why not the ink.</b> Toon's ink is an inside-out hull and needs a
    /// closed volume; a leaf is an open sheet (<see cref="Toon.Dress"/>,
    /// <c>foliage</c>), and a shell at the leaves' tips (tried) showed its
    /// back through the thin leaves at every puff's rim as a broad dark band -
    /// the rim is where the leaves are seen edge on. <b>Why not the depth.</b>
    /// gl_compatibility gives a shader no depth texture (<see cref="SheetBlast"/>).
    ///
    /// So the trees are drawn a second time, white on nothing, into a mask of
    /// their own (stand-ins on <see cref="MaskLayer"/>, a camera that follows
    /// this one), and a pass over the whole frame puts ink on every tree pixel
    /// (<see cref="TreeStencil"/>) that has empty mask within the line's width:
    /// the inside of the crown's silhouette, notch for notch. The stencil keeps
    /// it off whatever stands in front of a tree - a tank, a bush - where the
    /// mask alone would run the line across it. The colour is the pixel's own
    /// darkened, Toon's ink rule (<see cref="Toon.InkDark"/>).
    /// </summary>
    private void BuildOutline()
    {
        _mask = new SubViewport
        {
            Name = "CrownMask",
            TransparentBg = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
            Msaa3D = Viewport.Msaa.Disabled,
            Size = (Vector2I)GetViewport().GetVisibleRect().Size,
        };
        AddChild(_mask);
        _maskEye = new Camera3D
        {
            Projection = Camera3D.ProjectionType.Orthogonal,
            KeepAspect = Camera3D.KeepAspectEnum.Height,
            CullMask = MaskLayer,
            Current = true,
        };
        _mask.AddChild(_maskEye);
        _camera.CullMask &= ~MaskLayer;

        var line = new ShaderMaterial { Shader = OutlineShader };
        line.SetShaderParameter("mask", _mask.GetTexture());
        line.SetShaderParameter("dark", Toon.InkDark);
        _camera.AddChild(new MeshInstance3D
        {
            Name = "CrownLine",
            Mesh = new QuadMesh { Size = new Vector2(2.0f, 2.0f) },
            MaterialOverride = line,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            ExtraCullMargin = 16384.0f,
        });
        _line = line;
    }

    private ShaderMaterial? _line;

    /// <summary>A stand-in for every mesh of the tree, on the mask's layer, each
    /// surface on the mask of its burn role - what has burnt away is gone from
    /// the mask on the frame it goes from the picture.</summary>
    private static void Ghost(Node node, ShaderMaterial[] masks)
    {
        foreach (Node child in node.GetChildren())
            Ghost(child, masks);
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

    /// <summary>The <paramref name="i"/>-th tree's masks, one per burn role and
    /// one more for the eaten cores (4: role 1, <c>burn_eat</c>), listed with
    /// the materials its burn goes into. The windows are the cel's: the mask
    /// cuts the core's holes where the paint does.</summary>
    private ShaderMaterial[] MasksFor(int i, List<ShaderMaterial> burn)
    {
        var masks = new ShaderMaterial[5];
        for (int k = 0; k < 5; k++)
        {
            masks[k] = MaskFor(i);
            masks[k].SetShaderParameter("burn_role", k == 4 ? 1 : k);
            masks[k].SetShaderParameter("burn_window", k == 4 ? 0.45f : 0.12f);
            masks[k].SetShaderParameter("burn_eat", k == 4);
            if (k != 0)
                burn.Add(masks[k]);
        }
        return masks;
    }

    private readonly List<ShaderMaterial> _masks = new();

    /// <summary>The mask of the <paramref name="i"/>-th tree: its number in R
    /// (1..7 of 8, round again past seven - two trees a number apart are never
    /// the two that overlap here) and its depth in G, over the slab of depth
    /// the trees stand in (<see cref="FrameCamera"/>).</summary>
    private ShaderMaterial MaskFor(int i)
    {
        var m = new ShaderMaterial { Shader = MaskShader };
        m.SetShaderParameter("id", (i % 7 + 1) / 8.0f);
        _masks.Add(m);
        return m;
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
" + Toon.NoiseCode + Toon.BurnCode + @"
void vertex() {
    world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}
void fragment() {
    if (burn_role != 0 && burn_gone(UV2.x, world)) {
        discard;
    }
    ALBEDO = vec3(id, clamp((-VERTEX.z - near) / span, 0.0, 1.0), 0.0);
}",
    };

    /// <summary>The slab of view depth the mask's G spans, board px either side
    /// of the board's middle: 8 bits over it are ~5 px of depth, and trees
    /// that overlap stand tens of px apart.</summary>
    private const float MaskReach = 600.0f;

    private static readonly Shader OutlineShader = new()
    {
        Code = @"
shader_type spatial;
render_mode unshaded, cull_disabled, depth_test_disabled, depth_draw_never, shadows_disabled, fog_disabled;
stencil_mode read, compare_equal, " + TreeStencil + @";
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

    // --- the board ---------------------------------------------------------

    private static AtlasSet? LoadTile(string tag)
    {
        try
        {
            return AtlasSet.Load(AssetRoot.Sprites, tag);
        }
        catch (Exception e)
        {
            GD.Print($"tree3d: no {tag} tile ({e.Message})");
            return null;
        }
    }

    private void BuildWorld()
    {
        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = new Color(0.3f, 0.3f, 0.3f),
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = new Color(0.62f, 0.64f, 0.70f),
            AmbientLightEnergy = 0.55f,
        };
        AddChild(new WorldEnvironment { Environment = env });
        // Tank3D's sun: over the camera's left shoulder, the side the sprites
        // are lit from, so the models' light agrees with the wood beside them.
        _sun = new DirectionalLight3D
        {
            RotationDegrees = new Vector3(-52.0f, -35.0f, 0.0f),
            LightEnergy = 1.25f,
            ShadowEnabled = true,
            DirectionalShadowMode = DirectionalLight3D.ShadowMode.Parallel2Splits,
            DirectionalShadowMaxDistance = 3000.0f,
            ShadowNormalBias = 4.0f,
        };
        AddChild(_sun);
    }

    private void BuildBoard()
    {
        if (_tile is null)
            return;
        BoardMap map = BoardMap.FromGround("tree3d", Ground, new[] { ".....", ".....", "....." },
                                           Array.Empty<Parking>(), TerrainSet.Mixed, true);
        TerrainSet terrain = TerrainSet.Load(AssetRoot.Terrains);
        _field = new HexField
        {
            Terrain = terrain,
            Paint = map.Paint,
            Trees = false,
            Columns = map.Columns, Rows = map.Rows, Plot = map.Plot,
        };
        _field.SetKinds(map.Kinds);
        _field.SetGround(map.Ground);
        _field.SetCover(map.Over);
        _field.SetRelief(map.Levels, map.Ramps);
        _field.SetWater(map.Water);
        AddChild(_field);
        _field.Atlas = _tile;

        var eye = new Camera2D { Enabled = false };
        AddChild(eye);
        _stage = new Stage3D
        {
            Field = _field, Origin = Vector2.Zero, Eye = eye,
            Surf = WaterArt.Load(AssetRoot.Water, _tile.HexRect),
        };
        AddChild(_stage);
        _field.ShowField = false;
        BuildShadows();
        _fire = new Wildfire { Field = _field, Enabled = true, Wooded = _wooded.Contains };
    }

    private float LiftAt(Vector3 w) => _field?.TopAtPoint(new Vector2(w.X, w.Z * Squash)) ?? 0.0f;

    private Vector3 Foot(Vector3 w) => new(w.X, LiftAt(w) / RiseFactor, w.Z);

    /// <summary>Tank3D's shadow skin (see its <c>BuildShadows</c>): the board is
    /// unshaded, so the sun has nothing to lay the models' shadow on but this.</summary>
    private void BuildShadows()
    {
        if (_stage is null)
            return;
        List<Vector3> tris = _stage.GroundTriangles();
        if (tris.Count == 0)
            return;
        Vector3 lie = Stage3D.Clear(Squash, RiseFactor);
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        foreach (Vector3 v in tris)
        {
            st.SetNormal(Vector3.Up);
            st.AddVertex(v + lie);
        }
        var ink = new ShaderMaterial { Shader = ShadeShader, RenderPriority = Stage3D.ShadowOrder };
        ink.SetShaderParameter("ink", Stage3D.ShadowInk.A);
        AddChild(new MeshInstance3D
        {
            Name = "Shadows",
            Mesh = st.Commit(),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            MaterialOverride = ink,
        });
    }

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

    // --- the camera --------------------------------------------------------

    private void BuildCamera()
    {
        _camera = new Camera3D
        {
            Projection = Camera3D.ProjectionType.Orthogonal,
            KeepAspect = Camera3D.KeepAspectEnum.Height,
            Near = 1.0f,
            Far = Back * 2.0f,
            RotationDegrees = new Vector3(-Mathf.RadToDeg(Mathf.Asin(Squash)), 0.0f, 0.0f),
            Current = true,
        };
        AddChild(_camera);
        _camera.MakeCurrent();
    }

    /// <summary>The board's middle a little below the view's, so the crowns have
    /// room; the sun's map on the slab of depth the trees stand in (Tank3D's
    /// <c>FrameShadow</c>, for the same acne).</summary>
    private void FrameCamera()
    {
        float height = GetViewport().GetVisibleRect().Size.Y;
        _camera.Size = height / _zoom;
        Vector3 pivot = Vector3.Zero;
        if (_field is not null)
        {
            Vector2 flat = _field.FlatAnchor(new Vector2I(2, 1)) + _field.CentreOffset;
            pivot = new Vector3(flat.X, 0.0f, flat.Y / Squash);
        }
        pivot.Z -= _camera.Size / 8.0f / Squash;
        _camera.Position = pivot + new Vector3(0.0f, Back * Squash, Back * RiseFactor);
        if (_mask is not null && _maskEye is not null)
        {
            _mask.Size = (Vector2I)GetViewport().GetVisibleRect().Size;
            _maskEye.GlobalTransform = _camera.GlobalTransform;
            _maskEye.Size = _camera.Size;
            _maskEye.Near = _camera.Near;
            _maskEye.Far = _camera.Far;
            // Toon's ink width, in board px as the ink is (Toon.InkWidth)
            _line?.SetShaderParameter("width", Mathf.Max(1.0f, Toon.InkWidth * _zoom));
        }
        float depth = (pivot - _camera.Position).Dot(-_camera.Basis.Z);
        foreach (ShaderMaterial m in _masks)
        {
            m.SetShaderParameter("near", depth - MaskReach);
            m.SetShaderParameter("span", 2.0f * MaskReach);
        }
        const float reach = 600.0f;
        _sun.DirectionalShadowMaxDistance = depth + reach;
        _sun.DirectionalShadowSplit1 = Mathf.Clamp((depth - reach) / (depth + reach), 0.05f, 0.95f);
    }

    // --- flags -------------------------------------------------------------

    private void ReadFlags()
    {
        string[] args = OS.GetCmdlineUserArgs();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            bool more = i + 1 < args.Length;
            if (a == "--trees" && more) _trees = args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries);
            else if (a == "--zoom" && more) _zoom = F(args[++i], _zoom);
            else if (a == "--pbr") _pbr = true;
            else if (a == "--no-outline") _noOutline = true;
            else if (a == "--row") _row = true;
            else if (a == "--record" && more) _recordDir = args[++i];
            else if (a == "--record-every" && more) _recordEvery = Math.Max(1, (int)F(args[++i], _recordEvery));
            else if (a == "--record-for" && more) _recordFor = F(args[++i], _recordFor);
            else if (a == "--burn" && more && args[++i].Split(',') is { Length: 2 } qr
                     && int.TryParse(qr[0], out int q) && int.TryParse(qr[1], out int r))
                _burnAt = new Vector2I(q, r);
            else if (a == "--capture" && more) _capturePath = args[++i];
            else if (a == "--capture-at" && more) _captureAt = (int)F(args[++i], _captureAt);
        }
    }

    private static float F(string s, float fallback) =>
        float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : fallback;
}
