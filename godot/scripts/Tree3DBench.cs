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
/// without a tank in <c>Models/</c>. Burning and felling the model are not played
/// yet - it stands as it was baked (docs/props.md, "Tree3D").
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
    }

    public override void _Process(double delta)
    {
        // The stage's per-frame pass (Tank3D never calls it): nothing of it is
        // needed while the board has no wood and no vehicles, but a board is
        // left as the stage expects to run.
        _stage?.Place(Array.Empty<Vehicle>());
        FrameCamera();
        _frame++;
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
        if (!_pbr)
            Toon.Dress(scene, KeepsNormals, IsFoliage, TreeStencil);
        if (_mask is not null)
            Ghost(scene);
        var holder = new Node3D { Name = name, Scale = Vector3.One * ppm };
        holder.AddChild(scene);
        AddChild(holder);
        if (_field is not null)
        {
            Vector2 flat = _field.FlatAnchor(cell) + _field.CentreOffset + off;
            holder.Position = Foot(new Vector3(flat.X, 0.0f, flat.Y / Squash));
        }
        _models.Add(holder);
        GD.Print($"tree3d: {name} on {cell} +{off}, {ppm:F2} px/m, "
                 + $"{j.GetProperty("height").GetSingle() * ppm:F0} px tall, "
                 + $"{j.GetProperty("model").GetProperty("tris").GetInt32()} tris, "
                 + $"dressed in {Time.GetTicksMsec() - t0} ms");
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

    /// <summary>A white stand-in for every mesh of the tree, on the mask's layer.</summary>
    private static void Ghost(Node node)
    {
        foreach (Node child in node.GetChildren())
            Ghost(child);
        if (node is not MeshInstance3D m || m.Mesh is null)
            return;
        m.AddChild(new MeshInstance3D
        {
            Mesh = m.Mesh,
            Layers = MaskLayer,
            MaterialOverride = MaskMaterial,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });
    }

    private static readonly ShaderMaterial MaskMaterial = new()
    {
        Shader = new Shader
        {
            Code = @"
shader_type spatial;
render_mode unshaded, cull_disabled, shadows_disabled, fog_disabled;
void fragment() {
    ALBEDO = vec3(1.0);
}",
        },
    };

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
    float empty = 0.0;
    for (int i = 0; i < 8; i++) {
        float a = float(i) * 0.785398;
        empty = max(empty, 1.0 - texture(mask, SCREEN_UV + vec2(cos(a), sin(a)) * px).r);
    }
    if (empty < 0.5) {
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
            else if (a == "--capture" && more) _capturePath = args[++i];
            else if (a == "--capture-at" && more) _captureAt = (int)F(args[++i], _captureAt);
        }
    }

    private static float F(string s, float fallback) =>
        float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : fallback;
}
