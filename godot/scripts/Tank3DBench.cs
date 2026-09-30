using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// <c>Tank3D.tscn</c>: one 3D tank (<see cref="TankModel"/>) on the tank
/// bench's board, with the bench's own effects hung off its joints - the pilot
/// for moving the board from sprite atlases to 3D units.
///
/// <b>The space is <see cref="Stage3D"/>'s, on purpose.</b> An orthographic
/// camera pitched by the elevation the hex tile declares, <c>Size</c> the
/// viewport height over the zoom, so one world unit is one screen pixel at zoom
/// 1: +X right, +Y up, +Z toward the camera. The Node3D effects
/// (<see cref="ProcKick"/>, <see cref="ProcSpall"/>, <see cref="ProcSlam"/>,
/// <see cref="SheetBlast"/>) were written for exactly that space, so they are
/// raised here with the numbers the board raises them with, not re-tuned.
///
/// <b>And so is the ground: the board is <see cref="Stage3D"/>'s own.</b> A
/// <see cref="HexField"/> laid from a <see cref="BoardMap"/> (<c>test</c>, the
/// tank bench's strip, unless <c>--map</c> names another) and handed to a stage
/// that draws it as prisms - levels, ramps, the pond - in the same world the
/// model stands in. The tank drives on it: its height and tilt are the face
/// under it, and <see cref="HexField.Passable"/> stops it at a cliff, deep water
/// or the edge. <c>--flat</c> puts back the ground this scene had before, the
/// art laid flat with no depth.
///
/// <b>The motion is the Blender previews' (<c>repro_kit.py</c> clip_*), live.</b>
/// Same springs - <c>KICK</c> is <see cref="Recoil"/>'s, <c>SWAY</c> is
/// <see cref="BodyPitch"/>'s - and the same recoil stroke, wreck pose and toss,
/// so what the GIFs showed is what this plays.
/// </summary>
public sealed partial class Tank3DBench : Node3D
{
    /// <summary>One spring, semi-implicit Euler in substeps - the bench's
    /// integrator, and the previews'.</summary>
    private sealed class Spring
    {
        private readonly float _k, _c;
        public float X, V;
        public Spring(float k, float c) { _k = k; _c = c; }
        public void Kick(float dv) => V += dv;
        public float Step(float dt, float target = 0.0f, int n = 8)
        {
            float h = dt / n;
            for (int i = 0; i < n; i++)
            {
                V += (-_k * (X - target) - _c * V) * h;
                X += V * h;
            }
            return X;
        }
        public void Reset() { X = 0.0f; V = 0.0f; }
    }

    private enum Fate { Alive, Knocked, Destroyed }

    // --- flags -----------------------------------------------------------

    private string _modelTag = "LTR";
    private string _mapName = "events";
    private bool _flat;
    /// <summary><c>--pbr</c>: the glTF's own materials, no cel shading and no
    /// ink - the look before <see cref="Toon"/>, to hold against it.</summary>
    private bool _pbr;
    /// <summary><c>--fx2d</c>: the sprites' fire and column on the card, as
    /// before <see cref="CelBurn"/>, to hold against it.</summary>
    private bool _fx2d;
    /// <summary><c>--soft-water</c>: the board's water as the sprite benches draw
    /// it - foam and glints faded in rather than stepped (<see cref="Stage3D.CelWater"/>).</summary>
    private bool _softWater;
    private float _zoom = 2.5f;
    private float _heading = 215.0f;
    private string? _capturePath;
    private bool _noUi;
    private int _captureAt = 60;
    private string? _sequenceDir;
    private int _sequenceFrom, _sequenceTo, _sequenceStep = 1;
    private readonly List<(int Frame, string What)> _script = new();

    // --- scene -----------------------------------------------------------

    private TankModel _model = null!;
    private Node3D _rig = null!;
    private Camera3D _camera = null!;
    private DirectionalLight3D _sun = null!;
    private Label _hud = null!;
    private ControlPanel? _panel;
    private int _frame;
    private string _note = "";

    /// <summary>The board's tile, and so the camera's angle, the effects' size
    /// and the tanks' scale: the medium's hexagon, as <see cref="TankBench"/>
    /// takes it - one tile for every tank, so switching the tank does not resize
    /// the board. The board's, not the tank's: nothing of the tank on it is read
    /// off a sprite set.</summary>
    private AtlasSet? _tile;

    // --- the board ---------------------------------------------------------

    private HexField? _field;
    private Stage3D? _stage;

    /// <summary>The ground's up under the tank, eased toward the face it is on
    /// so crossing onto a ramp tips the hull rather than snapping it.</summary>
    private Vector3 _groundUp = Vector3.Up;

    /// <summary>Hull heading, degrees about +Y: 0 faces the camera (+Z).</summary>
    private float Heading
    {
        get => _heading;
        set { _heading = Mathf.PosMod(value, 360.0f); ApplyRig(); }
    }

    private float Squash => _tile is { } a && a.HexRect.Size.X > 0
        ? 2.0f * a.HexRect.Size.Y / (Mathf.Sqrt(3.0f) * a.HexRect.Size.X)
        : 0.5f;

    private float RiseFactor => Mathf.Sqrt(Mathf.Max(0.0f, 1.0f - Squash * Squash));

    private float HexWidth => _tile?.HexRect.Size.X ?? 248.0f;

    // --- the tanks ---------------------------------------------------------

    /// <summary>
    /// Which class each model drives as, and what the panel calls it.
    ///
    /// <b>Written down, because the sidecar does not say it</b> (see
    /// docs/tank3d.md). The class gives the tank its speed and its size - no
    /// sprite set is read for either.
    /// </summary>
    private static readonly (string Model, string Class, string Name)[] Pairs =
    {
        ("LTR", "LTP", "лёгкий"),
        ("MTR", "MTP", "средний"),
        ("HTR", "HTP", "тяжёлый"),
        ("TDR", "TDP", "ПТ-САУ"),
        ("HMR", "HMP", "мортира"),
    };

    private static int PairAt(string model) =>
        Array.FindIndex(Pairs, p => string.Equals(p.Model, model, StringComparison.OrdinalIgnoreCase));

    /// <summary>The pair's class, or the medium's for a model not in the table:
    /// the medium is the class every other figure is read against.</summary>
    private static MovementProfile ClassFor(string model)
    {
        string want = PairAt(model) is int i and >= 0 ? Pairs[i].Class : "MTP";
        MovementProfile? medium = null;
        foreach (MovementProfile p in MovementProfile.All)
        {
            if (string.Equals(p.Tag, want, StringComparison.OrdinalIgnoreCase))
                return p;
            if (p.Tag == "MTP")
                medium = p;
        }
        return medium ?? MovementProfile.Light;
    }

    /// <summary>
    /// How long the medium's hull is against the hex it stands on.
    ///
    /// <b>The sprite bench's own proportion, taken once and then left to the
    /// hex</b>: MTP draws its 0.883-unit hull at 167.7 px a unit, 148 px on a
    /// 248 px hex - 0.60. Every model's hull is brought to this, then scaled by
    /// its class's <see cref="MovementProfile.Size"/>, which is how the 2D
    /// benches size theirs (<see cref="Fleet.Resize"/>): the generator makes
    /// every tank about one unit long, so its own length says nothing about
    /// which is the heavy.
    /// </summary>
    private const float HullOfHex = 0.60f;

    /// <summary>World px a model unit, for this model in this class.</summary>
    private float PixelsFor(TankModel model, MovementProfile profile) =>
        HullOfHex * HexWidth * (float)profile.Size / Mathf.Max(model.HullLength, 1e-3f);

    /// <summary>The models on disk - a folder under <c>Models/</c> with both
    /// files in it - in the pairs' order, then any others by name.</summary>
    private static List<string> ModelsOnDisk()
    {
        var found = new List<string>();
        string root = AssetRoot.Repo + "/Models";
        if (Directory.Exists(root))
            foreach (string dir in Directory.GetDirectories(root))
                if (File.Exists(dir + "/tank.glb") && File.Exists(dir + "/tank.json"))
                    found.Add(Path.GetFileName(dir));
        found.Sort((a, b) =>
        {
            int ia = PairAt(a), ib = PairAt(b);
            ia = ia < 0 ? int.MaxValue : ia;
            ib = ib < 0 ? int.MaxValue : ib;
            return ia != ib ? ia.CompareTo(ib) : string.CompareOrdinal(a, b);
        });
        return found;
    }

    private List<string> _models = new();

    // --- motion ----------------------------------------------------------

    private static readonly (float K, float C) KickSpring = (1150.0f, 38.0f);
    private static readonly (float K, float C) SwaySpring = (480.0f, 28.5f);

    private readonly Spring _kickPitch = new(KickSpring.K, KickSpring.C);
    private readonly Spring _kickRoll = new(KickSpring.K, KickSpring.C);
    private readonly Spring _swayPitch = new(SwaySpring.K, SwaySpring.C);
    private readonly Spring _swayRoll = new(SwaySpring.K, SwaySpring.C);
    private readonly Spring _heave = new(SwaySpring.K, SwaySpring.C);
    private readonly Spring _tip = new(260.0f, 18.0f);
    private readonly RandomNumberGenerator _rng = new() { Seed = 7 };

    /// <summary>World px per second at full speed, and the ramp and the turn -
    /// the class's own <see cref="MovementProfile"/>, as the board drives it.
    /// </summary>
    private float MaxSpeed => (float)_profile.TopSpeed;
    private float Accel => (float)_profile.Accel;
    private float TurnRate => (float)_profile.TurnRate;
    private float _speed;
    private float _nextBump;
    private float _sinceShot = 99.0f;
    private float _sinceFate;
    private Fate _fate = Fate.Alive;

    /// <summary>The light class's slew, degrees a second - the previews'.</summary>
    private const float SlewDeg = 240.0f;
    private const float ElevDeg = 20.0f;

    public override void _Ready()
    {
        ReadFlags();
        _models = ModelsOnDisk();
        _rig = new Node3D { Name = "Rig" };
        AddChild(_rig);
        // The board's tile first: the camera's angle, the board's size and the
        // effects' pools all come off it, and none of them follows the tank.
        _tile = LoadTile("MTP");

        GetViewport().Msaa3D = Viewport.Msaa.Msaa4X;
        BuildWorld();
        if (!_flat)
            BuildBoard();
        // After the board: the stage makes its own camera current as it enters
        // the tree, and this one has to be current over it.
        BuildCamera();
        if (_field is null)
            BuildGround();
        Park();
        Mount(_modelTag);
        if (_otherWanted)
        {
            if (_otherCell is Vector2I at)
                PlaceOther(at, _otherHeading);
            else
                LineUp();
        }

        var layer = new CanvasLayer();
        AddChild(layer);
        _hud = new Label { Position = new Vector2(12, 8), Modulate = new Color(1, 1, 1, 0.85f), Visible = !_noUi };
        layer.AddChild(_hud);
        if (!_noUi)
            BuildPanel(layer);
    }

    /// <summary>The board's tile: the hex of a sprite set, the one thing here
    /// read off one - null if it does not load, which leaves the flat ground and
    /// a 248 px hex.</summary>
    private static AtlasSet? LoadTile(string tag)
    {
        try
        {
            return AtlasSet.Load(AssetRoot.Sprites, tag);
        }
        catch (Exception e)
        {
            GD.Print($"tank3d: no {tag} tile ({e.Message}) - flat ground, 248 px hex");
            return null;
        }
    }

    /// <summary>
    /// Put a tank on the rig: its model, its class and the effects that read
    /// it. What happens at start and on every pick from the panel.
    ///
    /// <b>The new model is loaded before the old one is taken down</b>, so a
    /// tank that fails to load leaves the one that was standing there. The rig,
    /// its place and its heading, the board and the effect pools stay: they are
    /// the bench's, not the tank's.
    /// </summary>
    private void Mount(string model)
    {
        MovementProfile profile = ClassFor(model);
        TankModel next = TankModel.Load(model, toon: !_pbr);
        next.ScaleTo(PixelsFor(next, profile));
        Unmount();
        _modelTag = model;
        _profile = profile;
        _model = next;
        _rig.AddChild(_model);
        _deckPx = MeasureDeck();
        _beltPaint = null;
        _foot = MeasureFootprint(_model, _rig, _modelTag);
        _roofPx = MeasureRoof();
        BuildEffects();
        GD.Print($"tank3d: {_modelTag} class {_profile.Tag} x{_profile.Size:F2}, {_model.PixelsPerUnit:F2} px/unit, "
                 + $"hull {_model.HullLength:F4} x {_model.HullWidth:F4} = {_model.HullLength * _model.PixelsPerUnit:F0} px, "
                 + $"bore r {_model.BoreRadius:F4}, deck {_deckPx:F1} px, roof {_roofPx:F1} of {_model.Size.Y * _model.PixelsPerUnit * RiseFactor:F1}, squash {Squash:F4} = {Mathf.RadToDeg(Mathf.Asin(Squash)):F2} deg, "
                 + $"hex {HexWidth:F0} px");
    }

    /// <summary>Take the tank off the rig: everything <see cref="Mount"/> and
    /// <see cref="BuildEffects"/> made for it. Reset first, so the debris a
    /// destroyed tank threw is back in the model that gets freed.</summary>
    private void Unmount()
    {
        if (_model is null)
            return;
        ResetTank();
        FreeEffects();
        _paint.Clear();
        _hullHits = null;
        _hitLocal = Vector3.Zero;
        _rig.RemoveChild(_model);
        _model.QueueFree();
        _model = null!;
    }

    /// <summary>The panel's pick and <c>--do model=</c>: another tank on the same
    /// spot, heading unchanged, whole.</summary>
    private void Pick(string model)
    {
        if (string.Equals(model, _modelTag, StringComparison.OrdinalIgnoreCase))
            return;
        try
        {
            Mount(model);
        }
        catch (Exception e)
        {
            GD.Print($"tank3d: {model}: {e.Message}");
            _note = $"{model} did not load";
        }
    }

    /// <summary>How far the camera stands back along its view: the stage's own
    /// number and for its reason - an orthographic depth buffer is linear, and
    /// at 4000 back the board's rims, lifted two units off the faces, fell into
    /// its noise (<c>Stage3D.Back</c>).</summary>
    private const float Back = 1500.0f;

    private void BuildCamera()
    {
        _camera = new Camera3D
        {
            Projection = Camera3D.ProjectionType.Orthogonal,
            KeepAspect = Camera3D.KeepAspectEnum.Height,
            Near = 1.0f,
            Far = Back * 2.0f,
            RotationDegrees = new Vector3(-Mathf.RadToDeg(Mathf.Asin(Squash)), 0.0f, 0.0f),
            // Everything but the model's stand-ins, which are the height map's.
            CullMask = 0xFFFFF & ~GhostLayer,
            Current = true,
        };
        AddChild(_camera);
        _camera.MakeCurrent();
    }

    /// <summary>Size the camera to the zoom and keep the tank a little below
    /// the middle, so a plume or a thrown turret has sky to go into.</summary>
    private void FrameCamera()
    {
        const float back = Back;
        float height = GetViewport().GetVisibleRect().Size.Y;
        _camera.Size = height / _zoom;
        Vector3 pivot = _rig.Position;
        // Both hulls in the picture while a target stands near: toward the
        // middle between them, eased off with the distance so the rammer stays
        // on the screen - a switch at one distance was a jump of the view.
        if (_other is not null)
        {
            Vector3 mid = 0.5f * (_rig.Position + _other.Rig.Position);
            float d = mid.DistanceTo(_rig.Position) / _camera.Size;
            pivot = pivot.Lerp(mid, 1.0f - Smooth(0.3f, 0.6f, d));
        }
        // Up the screen by a sixth of the view: screen up is -Z on the ground.
        pivot.Z -= _camera.Size / 6.0f / Squash;
        _camera.Position = pivot + new Vector3(0.0f, back * Squash, back * RiseFactor);
        // Camera2D.Offset's sense: +y moves the view down the screen.
        Vector2 shake = ShakeOffset();
        _camera.Position += _camera.Basis.X * shake.X - _camera.Basis.Y * shake.Y;
        FrameShadow();
    }

    /// <summary>How far round the tank, in its own reaches, the sun's shadow
    /// map has to see: the tank and its shadow on the ground.</summary>
    private const float ShadowReach = 3.0f;

    /// <summary>
    /// The sun's map on the slab of depth the tank stands in, not on the whole
    /// view: the second of two splits runs from the tank's depth less
    /// <see cref="ShadowReach"/> reaches to the same past it, and the first,
    /// which nothing here casts into, takes the rest.
    ///
    /// <b>This is what the cel ramp needed.</b> Over all 3000 of the view's
    /// depth a texel came to ~1.5 px of the board, and a side the sun grazes
    /// shadowed itself in soft diagonal stripes - acne, blurred by the filter -
    /// which the tone's threshold cut into blots across the skirts.
    /// </summary>
    private void FrameShadow()
    {
        if (_model is null)
            return;
        float depth = (_rig.Position - _camera.Position).Dot(-_camera.Basis.Z);
        float reach = Reach * ShadowReach;
        // And the target's, when it stands on the board.
        if (_other is not null)
            reach += Mathf.Abs((_other.Rig.Position - _rig.Position).Dot(-_camera.Basis.Z));
        _sun.DirectionalShadowMaxDistance = depth + reach;
        _sun.DirectionalShadowSplit1 = Mathf.Clamp((depth - reach) / (depth + reach), 0.05f, 0.95f);
    }

    private void BuildWorld()
    {
        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            // Flat: the soil art's mean, lit about as the cells are, so the
            // seams between their soft rims read as more ground. On the board:
            // the grey the other benches stand their boards on, because past
            // the rim is off the board, not more of it.
            BackgroundColor = _flat ? new Color(0.60f, 0.50f, 0.33f) : new Color(0.3f, 0.3f, 0.3f),
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = new Color(0.62f, 0.64f, 0.70f),
            AmbientLightEnergy = 0.55f,
        };
        AddChild(new WorldEnvironment { Environment = env });
        // From over the camera's left shoulder, the side the sprites are lit
        // from, high enough that the shadow stays short and on the ground.
        _sun = new DirectionalLight3D
        {
            RotationDegrees = new Vector3(-52.0f, -35.0f, 0.0f),
            LightEnergy = 1.25f,
            ShadowEnabled = true,
            DirectionalShadowMode = DirectionalLight3D.ShadowMode.Parallel2Splits,
            DirectionalShadowMaxDistance = 3000.0f,
            // Twice the default: on the slab FrameShadow fits, a texel is
            // fine enough that the cel ramp's step cut the turret's sides
            // into acne stripes along its loft at 2; at 7 the contact shadows
            // under the turret and the fenders thin away.
            ShadowNormalBias = 4.0f,
        };
        AddChild(_sun);
    }

    /// <summary>
    /// The board, as <see cref="TankBench"/> lays it: the map's kinds, ground,
    /// cover, relief and water on a <see cref="HexField"/>, and a
    /// <see cref="Stage3D"/> that draws it. The field draws nothing itself - the
    /// stage owns the board, as on every bench that has one.
    ///
    /// <b>The stage's ground writes depth, and the effect cards are why that
    /// needed an answer</b>: a card slid back along the view ray passes under
    /// the ground with its lower half. The cards' shader lifts whatever is below
    /// the ground back up the same ray (<see cref="CardShader"/>), which is the
    /// stage's own bend for its sprites (<c>Stage3D.Body</c>) done per vertex.
    /// </summary>
    private void BuildBoard()
    {
        if (_tile is null)
        {
            GD.Print("tank3d: no tile to lay a board with - the flat ground instead");
            return;
        }
        // The events bench's board is compiled apart from the named ones -
        // ByName would answer "events" with the harness's bench board.
        BoardMap map = string.Equals(_mapName, "events", StringComparison.OrdinalIgnoreCase)
            ? BoardMap.Events : BoardMap.ByName(_mapName);
        TerrainSet terrain = TerrainSet.Load(AssetRoot.Terrains);
        GD.Print($"tank3d: board {map.Name} {map.Columns}x{map.Rows}, terrain {terrain.Note}");
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
        // After the relief - the tank bench's reason: the water's guards are
        // asked about levels and ramps.
        _field.SetWater(map.Water);
        AddChild(_field);
        _field.Atlas = _tile;
        _home = _startCell ?? (map.Homes.Count > 0 ? map.Homes[0] : new Vector2I(map.Columns / 2, map.Rows / 2));

        // What the stage aims its own camera by: it mirrors a 2D camera, and
        // this scene has none - its camera is its own (BuildCamera), current
        // over the stage's, which is left idle. In the tree so it is freed with
        // the scene, disabled so it moves no canvas.
        var eye = new Camera2D { Enabled = false };
        AddChild(eye);
        _stage = new Stage3D
        {
            Field = _field, Origin = Vector2.Zero, Eye = eye,
            Surf = WaterArt.Load(AssetRoot.Water, _tile.HexRect),
            // The pond as a wave field (Ripples): the stage fits it to the water
            // and reads it for the surface's normals and foam; the hull, the
            // splash and the air strike it (Tank3DBench.Water).
            Wash = _ripples,
            CelWater = !_softWater,
        };
        AddChild(_stage);
        _field.ShowField = false;
        BuildShadows();
        _celRipples = new CelRipples { Name = "CelRipples" };
        AddChild(_celRipples);
        _celRipples.Build(_field, Squash, RiseFactor, HexWidth * 0.5f);
    }

    /// <summary>The cell the tank opens on: the map's first parking.</summary>
    private Vector2I _home;

    /// <summary>
    /// Where the model's shadow falls: a skin over the board's top faces that is
    /// clear where the sun reaches and dark where it does not.
    ///
    /// <b>The board is unshaded</b> - its light is painted - so the sun this
    /// scene lights the model with has nothing to fall on. The skin is the
    /// board's own tops (<see cref="Stage3D.GroundTriangles"/>, the faces the
    /// mesh is built from), lifted by the stage's clearance, and it multiplies
    /// what is under it: by one where the sun reaches, by one less the board's
    /// shadow ink where it does not (<see cref="ShadeShader"/>).
    /// </summary>
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
        var skin = new MeshInstance3D
        {
            Name = "Shadows",
            Mesh = st.Commit(),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            MaterialOverride = ink,
        };
        AddChild(skin);
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

    // --- standing on the board ----------------------------------------------

    /// <summary>How high the ground stands under a world point, in screen px -
    /// the board's own unit for it (<see cref="HexField.TopAtPoint"/>); nought
    /// on the flat ground.</summary>
    private float LiftAt(Vector3 w) => _field?.TopAtPoint(Board(w)) ?? 0.0f;

    /// <summary>The ground point under a world point.</summary>
    private Vector3 Foot(Vector3 w) => new(w.X, LiftAt(w) / RiseFactor, w.Z);

    /// <summary>Stand the rig on the home cell's centre, at its height.</summary>
    private void Park()
    {
        if (_field is not null)
        {
            Vector2 flat = _field.FlatAnchor(_home) + _field.CentreOffset;
            _rig.Position = Foot(new Vector3(flat.X, 0.0f, flat.Y / Squash));
        }
        Settle(0.0f, snap: true);
    }

    /// <summary>
    /// Whether the tank may go from one world point to the next, travelling
    /// <paramref name="way"/>: the board's own rule for the step between cells
    /// (<see cref="HexField.Passable"/> - no cliff without a ramp, a ramp only
    /// along its axis, not off the board; into deep water off any bank, out of
    /// it by the ramp alone). Anywhere on the flat ground.
    ///
    /// <b>Asked of the middle and of the leading end.</b> The middle alone let
    /// the front half of the hull out over the board's edge before it stopped;
    /// the end asks the same question of the step from the middle's cell to its
    /// own, which is at most a neighbour - half a hull is shorter than a cell's
    /// edge.
    /// </summary>
    private bool CanDrive(Vector3 from, Vector3 to, Vector3 way)
    {
        if (_field is null)
            return true;
        float half = _model.Size.Z * 0.5f * _model.PixelsPerUnit;
        Vector2I a = _field.FlatCellAt(Board(from)), b = _field.FlatCellAt(Board(to));
        Vector2I end = _field.FlatCellAt(Board(to + way * half));
        return Step(a, b) && Step(b, end);

        bool Step(Vector2I here, Vector2I there)
        {
            if (here == there)
                return true;
            if (!_field.InBounds(there))
                return false;
            int heading = HexField.HeadingTo(here, there);
            return heading >= 0 && _field.Passable(here, heading);
        }
    }

    /// <summary>
    /// Put the rig on the ground under it: its height, and its up eased toward
    /// the face it is on.
    ///
    /// <b>One face, the one under the middle</b>, extrapolated under all four
    /// samples (<see cref="HexField.TopOn"/>): a hull over a cell's rim then
    /// lies in its own cell's plane rather than bridging to the neighbour's,
    /// which is the board's rule for anything a cell wide.
    ///
    /// <b>The height is the ride, not always the face</b> - in deep water the
    /// hull floats, drowns down to the drawn bed and falls in off the bank
    /// (Tank3DBench.Water); afloat it lies level, whatever the face under it.
    /// </summary>
    private void Settle(float dt, bool snap = false)
    {
        Vector3 want = Vector3.Up;
        if (_field is not null)
        {
            Vector2 flat = Board(_rig.Position);
            Vector2I cell = _field.CellUnder(flat);
            float d = 0.25f * HexWidth;
            float Y(Vector2 f) => _field.TopOn(cell, f) / RiseFactor;
            float sx = (Y(flat + new Vector2(d, 0.0f)) - Y(flat - new Vector2(d, 0.0f))) / (2.0f * d);
            // A world step of d in Z is d times the squash on the flat board.
            float sz = (Y(flat + new Vector2(0.0f, d * Squash)) - Y(flat - new Vector2(0.0f, d * Squash))) / (2.0f * d);
            float ride = RideOn(cell, flat, out bool afloat);
            want = afloat || _falling ? Vector3.Up : new Vector3(-sx, 1.0f, -sz).Normalized();
            _rig.Position = new Vector3(_rig.Position.X, Ride(cell, ride, afloat, dt, snap), _rig.Position.Z);
        }
        _groundUp = snap ? want : _groundUp.Lerp(want, 1.0f - Mathf.Exp(-10.0f * dt)).Normalized();
        ApplyRig();
    }

    /// <summary>The rig's basis: the heading about the ground's up.</summary>
    private void ApplyRig()
    {
        if (_rig is null)
            return;
        float h = Mathf.DegToRad(_heading);
        var ahead = new Vector3(Mathf.Sin(h), 0.0f, Mathf.Cos(h));
        Vector3 up = _groundUp;
        Vector3 z = (ahead - up * ahead.Dot(up)).Normalized();
        _rig.Basis = new Basis(up.Cross(z), up, z);
    }

    /// <summary>
    /// The ground with <c>--flat</c>: the board's cells if there is art for
    /// them, a plain plane if not - never both, because neither may write depth
    /// (see <see cref="BuildCells"/>) and two opaque layers that do not are drawn
    /// in whatever order the renderer likes.
    /// </summary>
    private void BuildGround()
    {
        if (BuildCells())
            return;
        var mat = new StandardMaterial3D
        {
            AlbedoColor = new Color(134 / 255.0f, 110 / 255.0f, 70 / 255.0f),
            Roughness = 1.0f,
            DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Disabled,
        };
        var ground = new MeshInstance3D
        {
            Name = "Ground",
            Mesh = new PlaneMesh { Size = new Vector2(8000.0f, 8000.0f) },
            MaterialOverride = mat,
        };
        AddChild(ground);
    }

    /// <summary>
    /// The board's own ground art, one hexagon per cell, laid where the flat
    /// board draws it: the art is painted already squashed for this camera, so
    /// each cell is a lying quad whose outline on screen is the rectangle
    /// <see cref="TerrainSet.RectAt"/> gives - a screen row is depth times the
    /// squash.
    ///
    /// <b>Not in the depth buffer</b>, and that is load-bearing: an effect card
    /// is slid back along the view ray to stand behind the tank (see
    /// <see cref="Tank3DBench"/>.Fx), and its lower half then passes under the
    /// ground - a ground that wrote depth would cut it off there. Nothing stands
    /// below the ground, so it loses nothing by it. The gaps between the art's
    /// soft rims show the background, which is the soil's own mean.
    /// </summary>
    private bool BuildCells()
    {
        TerrainSet terrain = TerrainSet.Load(AssetRoot.Terrains);
        GD.Print($"tank3d: {terrain.Note}");
        if (!terrain.Any || _tile is null)
            return false;
        var hexRect = new Rect2I((Vector2I)_tile.HexRect.Position, (Vector2I)_tile.HexRect.Size);
        float scale = terrain.ScaleTo(hexRect);
        float w = _tile.HexRect.Size.X, h = _tile.HexRect.Size.Y;
        var mats = new Dictionary<string, StandardMaterial3D>();
        const int reach = 7;
        for (int q = -reach; q <= reach; q++)
            for (int r = -reach; r <= reach; r++)
            {
                var cell = new Vector2I(q, r);
                string kind = terrain.MixedAt(cell);
                if (terrain.Texture(kind) is not Texture2D tex)
                    continue;
                if (!mats.TryGetValue(kind, out StandardMaterial3D? m))
                {
                    m = new StandardMaterial3D
                    {
                        AlbedoTexture = tex,
                        Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor,
                        DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Disabled,
                        Roughness = 1.0f,
                        // Squashed 2:1 on screen, the mip chain would pick the
                        // level for the short axis and smear the art to its mean.
                        TextureFilter = BaseMaterial3D.TextureFilterEnum.Linear,
                    };
                    mats[kind] = m;
                }
                var centre = new Vector2(q * 0.75f * w, (r + 0.5f * (q & 1)) * h);
                Rect2 rect = terrain.RectAt(centre, scale);
                var tile = new MeshInstance3D
                {
                    Mesh = new PlaneMesh { Size = new Vector2(rect.Size.X, rect.Size.Y / Squash) },
                    MaterialOverride = m,
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                    Position = new Vector3(rect.Position.X + rect.Size.X * 0.5f, 0.0f,
                                           (rect.Position.Y + rect.Size.Y * 0.5f) / Squash),
                };
                AddChild(tile);
            }
        return true;
    }

    // --- flags -----------------------------------------------------------

    private void ReadFlags()
    {
        string[] args = OS.GetCmdlineUserArgs();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            bool more = i + 1 < args.Length;
            if (a == "--model" && more) _modelTag = args[++i].ToUpperInvariant();
            else if (a == "--sprites" && more)
                GD.Print($"tank3d: --sprites {args[++i]} ignored - the 3D tank reads no sprite set");
            else if (a == "--map" && more) _mapName = args[++i];
            else if (a == "--cell" && more)
            {
                string[] qr = args[++i].Split(',');
                if (qr.Length == 2 && int.TryParse(qr[0], out int q) && int.TryParse(qr[1], out int r))
                    _startCell = new Vector2I(q, r);
            }
            // The ram's target (Tank3DBench.Ram): its model, and where it stands -
            // lined up two hexes ahead of the rammer unless a cell is named.
            else if (a == "--target" && more)
            {
                _otherTag = args[++i].ToUpperInvariant();
                _otherWanted = true;
            }
            else if (a == "--target-cell" && more)
            {
                string[] qr = args[++i].Split(',');
                if (qr.Length == 2 && int.TryParse(qr[0], out int q) && int.TryParse(qr[1], out int r))
                    _otherCell = new Vector2I(q, r);
                _otherWanted = true;
            }
            else if (a == "--target-heading" && more) _otherHeading = F(args[++i], _otherHeading);
            else if (a == "--no-amphibious") _amphibious = false;
            else if (a == "--no-ripples") _ripples.Enabled = false;
            else if (a == "--soft-water") _softWater = true;
            else if (a == "--flat") _flat = true;
            else if (a == "--pbr") _pbr = true;
            else if (a == "--fx2d") _fx2d = true;
            else if (a == "--zoom" && more) _zoom = F(args[++i], _zoom);
            else if (a == "--heading" && more) _heading = F(args[++i], _heading);
            else if (a == "--capture" && more) _capturePath = args[++i];
            else if (a == "--no-ui") _noUi = true;
            else if (a == "--capture-at" && more) _captureAt = (int)F(args[++i], _captureAt);
            // --sequence DIR FROM TO [STEP]: every STEP-th frame in between, for
            // looking at motion rather than at one moment of it.
            else if (a == "--sequence" && i + 3 < args.Length)
            {
                _sequenceDir = args[++i];
                _sequenceFrom = (int)F(args[++i], 0);
                _sequenceTo = (int)F(args[++i], 0);
                if (i + 1 < args.Length && int.TryParse(args[i + 1], out int step))
                {
                    _sequenceStep = Math.Max(1, step);
                    i++;
                }
            }
            // --do WHAT@FRAME: the same thing the key does, on that frame, so a
            // capture shows an event without anybody at the keyboard.
            else if (a == "--do" && more)
            {
                string[] parts = args[++i].Split('@');
                _script.Add((parts.Length > 1 ? (int)F(parts[1], 0) : 0, parts[0]));
            }
        }
    }

    private static float F(string s, float fallback) =>
        float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : fallback;

    // --- events ----------------------------------------------------------

    /// <summary>Everything a key or <c>--do</c> can ask for, by one name.</summary>
    private void Do(string what)
    {
        switch (what)
        {
            case "shot": Shoot(); break;
            case "ricochet-front": Hit(0, false); break;
            case "ricochet-right": Hit(1, false); break;
            case "ricochet-rear": Hit(2, false); break;
            case "ricochet-left": Hit(3, false); break;
            case "pierce-front": Hit(0, true); break;
            case "pierce-right": Hit(1, true); break;
            case "pierce-rear": Hit(2, true); break;
            case "pierce-left": Hit(3, true); break;
            case "he-front": He(0); break;
            case "he-right": He(1); break;
            case "he-rear": He(2); break;
            case "he-left": He(3); break;
            case "ground": FxGround(); break;
            case "burn": FxBurn(true); break;
            case "unburn": FxBurn(false); break;
            case "knock": KnockOut(); break;
            case "destroy": Destroy(); break;
            case "reset": ResetTank(); break;
            case "pond": ToPond(); break;
            case "amphibious": _amphibious = true; break;
            case "no-amphibious": _amphibious = false; break;
            case "heights": SaveHeights(); break;
            case "drive": _driveScripted = 1.0f; break;
            case "stop": _driveScripted = 0.0f; break;
            case "left": _turnScripted = 1.0f; break;
            case "right": _turnScripted = -1.0f; break;
            case "straight": _turnScripted = 0.0f; break;
            case "ram": RamGo(); break;
            case "target": LineUp(); break;
            case "untarget": RemoveOther(); break;
            default:
                if (what.StartsWith("turret=", StringComparison.Ordinal))
                {
                    if (_model.Turreted)
                        _model.Yaw = Mathf.DegToRad(F(what[7..], 0));
                    else
                        GD.Print($"tank3d: --do {what}: {_modelTag} is a casemate, it has no turret");
                }
                else if (what.StartsWith("elev=", StringComparison.Ordinal))
                    _model.Elevation = Mathf.DegToRad(F(what[5..], 0));
                else if (what.StartsWith("heading=", StringComparison.Ordinal))
                    Heading = F(what[8..], _heading);
                else if (what.StartsWith("model=", StringComparison.Ordinal))
                    Pick(what[6..].ToUpperInvariant());
                else if (what.StartsWith("target=", StringComparison.Ordinal))
                    PickOther(what[7..].ToUpperInvariant());
                else
                    GD.Print($"tank3d: --do {what}: no such event");
                break;
        }
    }

    private float _driveScripted, _turnScripted;

    /// <summary>The gun fires: the tube strokes back along its bore and the
    /// sprung mass rocks against it - pitch and roll by where the gun points.
    /// </summary>
    private void Shoot()
    {
        if (_fate != Fate.Alive)
            return;
        _sinceShot = 0.0f;
        const float impulse = 2.3f;   // rad/s: a light gun, about two degrees at peak
        // The roof goes opposite the gun: back when it points forward (pitch
        // negative is nose up), to the tank's right when it points left (roll
        // positive is roof to the right).
        _kickPitch.Kick(-impulse * Mathf.Cos(_model.Yaw));
        _kickRoll.Kick(impulse * Mathf.Sin(_model.Yaw));
        FxShot();
    }

    /// <summary>Four sides in the order the previews name them.</summary>
    private static readonly string[] Sides = { "front", "right", "rear", "left" };

    /// <summary>A round arrives from one side: the roof goes away from it and
    /// springs back; a ricochet glances, a penetration rocks harder.</summary>
    private void Hit(int side, bool pierce)
    {
        if (_fate == Fate.Destroyed)
            return;
        Vector3 travel = Travel(side);
        float k = pierce ? 3.2f : 2.8f;
        _kickPitch.Kick(k * travel.Z);
        _kickRoll.Kick(-k * travel.X);
        FxHit(side, travel, pierce);
    }

    /// <summary>Which way a round from each side travels, in the hull's frame:
    /// from the front it goes aft (-Z), from the right toward the left (+X).
    /// </summary>
    private static Vector3 Travel(int side) => side switch
    {
        0 => new Vector3(0, 0, -1),
        1 => new Vector3(1, 0, 0),
        2 => new Vector3(0, 0, 1),
        _ => new Vector3(-1, 0, 0),
    };

    /// <summary>HE bursting on a plate: it rocks the hull like a hit.</summary>
    private void He(int side)
    {
        if (_fate == Fate.Destroyed)
            return;
        Vector3 travel = Travel(side);
        _kickPitch.Kick(3.0f * travel.Z);
        _kickRoll.Kick(-3.0f * travel.X);
        FxHe(side, travel);
    }

    /// <summary>Knocked out: a penetration from the front, then the turret sits
    /// down in its ring and tips, the gun falls to its stop, the belts go slack
    /// - and it burns.</summary>
    private void KnockOut()
    {
        if (_fate != Fate.Alive)
            return;
        Hit(0, true);
        _fate = Fate.Knocked;
        _sinceFate = 0.0f;
        _speed = 0.0f;
        FxKnocked();
    }

    // The toss, set when the tank is destroyed.
    private sealed class Flying
    {
        public required Node3D Node;
        public Vector3 V;
        public Vector3 Axis;
        public float W;
        public float Half;
        public bool Rest;
    }

    private readonly List<Flying> _flying = new();
    private Vector3 _tossFrom, _tossTo;
    private float _tossYaw0, _tossYawEnd;
    private Quaternion _tossTilt;
    private readonly List<(Node3D Node, Node Parent, Transform3D Local)> _moved = new();

    /// <summary>Destroyed: the ammunition goes - the debris is thrown out from
    /// the ring and falls bouncing, the turret goes back in a spinning arc, lands
    /// on the engine deck and slides to rest, the hull jolts twice.</summary>
    private void Destroy()
    {
        if (_fate == Fate.Destroyed)
            return;
        if (_fate == Fate.Alive)
        {
            _fate = Fate.Knocked;
            FxKnocked();
        }
        _fate = Fate.Destroyed;
        _sinceFate = 0.0f;
        _speed = 0.0f;
        _kickPitch.Kick(2.5f);
        _heave.Kick(0.35f * 0.3f);

        // A casemate keeps its casemate: nothing is thrown, only the debris.
        if (_model.Turret is { } turret)
        {
            float yawDeg = Mathf.RadToDeg(_model.Yaw);
            TankModel.Landing land = _model.LandingNear(yawDeg + 150.0f);
            _tossFrom = _model.TurretRest;
            _tossTo = land.At;
            _tossYaw0 = yawDeg;
            _tossYawEnd = land.YawDeg + 360.0f * Mathf.Ceil((yawDeg + 300.0f - land.YawDeg) / 360.0f);
            // The landing's rotation with its own yaw taken out, so the spin is
            // ours and the settle is the deck's tilt alone.
            _tossTilt = land.Rest * new Quaternion(Vector3.Up, -Mathf.DegToRad(land.YawDeg));
            _model.TurretOverride = turret.Transform;
        }

        // Debris out of the hierarchy and into the world, thrown out and up
        // from the blast point.
        var rnd = new RandomNumberGenerator { Seed = 3 };
        Vector3 blast = _model.Tank.ToGlobal(_model.BlastAt);
        foreach (TankModel.Piece piece in _model.Debris)
        {
            Node3D n = piece.Node;
            Transform3D world = n.GlobalTransform;
            _moved.Add((n, n.GetParent(), n.Transform));
            n.GetParent().RemoveChild(n);
            AddChild(n);
            n.GlobalTransform = world;
            Vector3 d = world.Origin - blast;
            d.Y = Mathf.Abs(d.Y) + 0.6f * d.Length();
            d = d.Normalized();
            var axis = new Vector3(rnd.RandfRange(-1, 1), rnd.RandfRange(-1, 1), rnd.RandfRange(-1, 1)).Normalized();
            float s = _model.PixelsPerUnit;
            _flying.Add(new Flying
            {
                Node = n,
                V = d * rnd.RandfRange(1.0f, 1.7f) * s,
                Axis = axis,
                W = rnd.RandfRange(6.0f, 14.0f),
                Half = Mathf.Min(piece.Size.X, Mathf.Min(piece.Size.Y, piece.Size.Z)) * 0.5f * s,
            });
        }
        FxDestroyed(blast);
    }

    private void ResetTank()
    {
        foreach (var (node, parent, local) in _moved)
        {
            node.GetParent().RemoveChild(node);
            parent.AddChild(node);
            node.Transform = local;
        }
        _moved.Clear();
        _flying.Clear();
        _model.TurretOverride = null;
        _model.Yaw = 0; _model.Elevation = 0; _model.Recoil = 0;
        _model.Droop = 0; _model.Cant = 0; _model.Slackness = 0;
        foreach (Spring s in new[] { _kickPitch, _kickRoll, _swayPitch, _swayRoll, _heave, _tip })
            s.Reset();
        _fate = Fate.Alive;
        _speed = 0;
        _sinceShot = 99.0f;
        FxReset();
        WaterReset();
        RamReset();
    }

    // --- frame -----------------------------------------------------------

    private static float Smooth(float a, float b, float t)
    {
        float u = Mathf.Clamp((t - a) / (b - a), 0.0f, 1.0f);
        return u * u * (3.0f - 2.0f * u);
    }

    /// <summary>The tube's stroke, 0..1, t seconds after the shot: out in one
    /// frame, held a frame, back on an exponential - <see cref="RecoilLoop"/>'s
    /// shape.</summary>
    private static float Stroke(float t)
    {
        if (t < 0.0f) return 0.0f;
        if (t < 0.04f) return t / 0.04f;
        if (t < 0.08f) return 1.0f;
        return Mathf.Exp(-(t - 0.08f) / 0.12f);
    }

    public override void _Process(double delta)
    {
        float dt = (float)Math.Min(delta, 0.1);
        foreach (var (frame, what) in _script)
            if (frame == _frame)
                Do(what);

        bool alive = _fate == Fate.Alive;
        float yawIn = (Input.IsKeyPressed(Key.Q) ? 1 : 0) - (Input.IsKeyPressed(Key.E) ? 1 : 0);
        float elevIn = (Input.IsKeyPressed(Key.R) ? 1 : 0) - (Input.IsKeyPressed(Key.F) ? 1 : 0);
        float driveIn = (Input.IsKeyPressed(Key.W) ? 1 : 0) - (Input.IsKeyPressed(Key.S) ? 1 : 0) + _driveScripted;
        float turnIn = (Input.IsKeyPressed(Key.A) ? 1 : 0) - (Input.IsKeyPressed(Key.D) ? 1 : 0) + _turnScripted;
        if (alive)
        {
            if (_model.Turreted)          // a casemate aims with its hull
                _model.Yaw += yawIn * Mathf.DegToRad(SlewDeg) * dt;
            _model.Elevation = Mathf.Clamp(_model.Elevation + elevIn * Mathf.DegToRad(ElevDeg) * dt,
                                           Mathf.DegToRad(_model.ElevMin), Mathf.DegToRad(_model.ElevMax));
        }
        else
        {
            driveIn = 0.0f;
            turnIn = 0.0f;
        }

        // Drive: a ramp to the preview's speed, the belts and wheels on the
        // distance, the mass squatting on the pull and nosing down on the stop.
        // Afloat nothing is under the tracks: the class's swimming share
        // (MovementProfile.SwimFraction, the board's cap).
        float cap = Swimming ? (float)MovementProfile.SwimFraction : 1.0f;
        float target, a, turn, step;
        if (RamRunning)
        {
            // A ram has the hull (Tank3DBench.Ram): pushing, waiting against
            // the target, backing off - the keys wait for it, as the board's
            // orders wait for a push.
            float was = _speed;
            step = RamDrive(dt);
            a = (_speed - was) / dt / Accel;
            target = _speed;
            turn = 0.0f;
            turnIn = 0.0f;
        }
        else
        {
            target = Mathf.Clamp(driveIn, -1.0f, 1.0f) * MaxSpeed * cap;
            float accel = Mathf.MoveToward(_speed, target, Accel * dt) - _speed;
            _speed += accel;
            a = accel / dt / Accel;
            turn = Mathf.Clamp(turnIn, -1.0f, 1.0f) * TurnRate * 0.5f;
            Heading += turn * dt;
            float h = Mathf.DegToRad(_heading);
            var ahead = new Vector3(Mathf.Sin(h), 0.0f, Mathf.Cos(h));
            Vector3 next = _rig.Position + ahead * _speed * dt;
            // Another hull in the way: up to it and no further, and the ram if
            // it was driven into (RamMeets). A turn into it on the spot is undone.
            if (RamMeets(ref next) && next == _rig.Position && turn != 0.0f)
                Heading -= turn * dt;
            step = (next - _rig.Position).Dot(ahead) / _model.PixelsPerUnit;
            if (CanDrive(_rig.Position, next, _speed >= 0.0f ? ahead : -ahead))
                _rig.Position = next;
            else
            {
                // The board says no - a cliff, deep water, its edge: the tank stops
                // where it is rather than being steered round it.
                _speed = 0.0f;
                step = 0.0f;
            }
        }
        Settle(dt);
        _model.Driven += step;
        _model.Skid += Mathf.DegToRad(turn) * dt * _model.Size.X * 0.5f;
        if (Mathf.Abs(_speed) > 1.0f || Mathf.Abs(turnIn) > 0.0f)
        {
            _nextBump -= Mathf.Abs(step) + Mathf.Abs(turnIn) * dt * 0.1f;
            if (_nextBump <= 0.0f)
            {
                _swayPitch.Kick(_rng.RandfRange(-0.35f, 0.35f));
                _swayRoll.Kick(_rng.RandfRange(-0.25f, 0.25f));
                _nextBump = _rng.RandfRange(0.06f, 0.12f);
            }
        }

        // BodyPitch follows the acceleration: 0.035 rad at full pull, nose up -
        // negative about +X - when pulling away forward.
        float sway = _swayPitch.Step(dt, -0.035f * a * Mathf.Sign(target == 0 ? _speed : target));
        _model.Pitch = _kickPitch.Step(dt) + sway;
        _model.Roll = _kickRoll.Step(dt) + _swayRoll.Step(dt);
        _model.Heave = _heave.Step(dt);

        _sinceShot += dt;
        _model.Recoil = Stroke(_sinceShot);

        if (_fate != Fate.Alive)
        {
            _sinceFate += dt;
            AdvanceFate(dt);
        }
        WaterTick(dt);
        _model.Apply();
        FxProcess(dt, _speed, a);
        RamFrame(dt, _camera.GlobalBasis);
        FrameCamera();

        string where = _field is null ? "flat ground"
            : $"{_mapName} {_field.FlatCellAt(Board(_rig.Position))}";
        string fate = Drowning ? "Drowned" : _fate.ToString();
        if (_fate == Fate.Alive && Swimming)
            fate = "Afloat";
        string ram = _other is null ? "" : $"  target {_other.Tag} {_other.Cell}"
                     + (_ramNote.Length > 0 ? $"  {_ramNote}" : "");
        _hud.Text = $"{_modelTag} 3D, class {_profile.Tag}  {where}  {fate}  {_note}{ram}\n"
                    + "Space shot   1-4 ricochet front/right/rear/left   Shift+1-4 pierce   Ctrl+1-4 HE   5 round in the ground\n"
                    + "J burning   K knocked out   X destroyed   T ram   Backspace reset   WASD drive   "
                    + (_model.Turreted ? "Q/E turret   " : "") + "R/F gun   -/= zoom   Tab panel   F12 shot";
        Shots();
        _frame++;
    }

    private void AdvanceFate(float dt)
    {
        float t = _sinceFate;
        // (a destroyed casemate has no toss to own its turret, and still goes
        // on below: its debris flies)
        if (_fate == Fate.Knocked || (_model.Turreted && _model.TurretOverride is null))
        {
            // Drowning breaks nothing, so the pose does not come with it: the
            // gun, the turret and the belts are as the water found them
            // (TankTick.Drowning, docs/water.md «Утоплен»).
            if (Drowning)
                return;
            // The tip settles with a small overshoot, the gun falls to its stop,
            // the belts sag.
            _model.Cant = _tip.Step(dt, t >= 0.05f ? 1.0f : 0.0f);
            float g = Mathf.Clamp((t - 0.05f) / 0.28f, 0.0f, 1.0f);
            _model.Droop = g * g;
            _model.Slackness = Smooth(0.05f, 0.5f, t);
            return;
        }

        // Destroyed. Gravity so the arc peaks at the sidecar's lift.
        float flight = _model.TossFlight, slide = _model.SlideTime;
        float s = _model.PixelsPerUnit;
        float grav = 8.0f * _model.TossLift / (flight * flight) * s;
        _model.Droop = Smooth(0.0f, 0.6f, t);
        _model.Slackness = Smooth(0.0f, 0.4f, t);
        if (_model.Turreted)
        {
            if (t >= flight && t - dt < flight)
            {
                _kickPitch.Kick(-1.6f);   // the turret lands on the deck: stern down
                FxLanded();
            }

            float tau = Mathf.Min(1.0f, t / flight);
            float u = Mathf.Clamp((t - flight) / slide, 0.0f, 1.0f);
            float e = 1.0f - (1.0f - u) * (1.0f - u);
            float prog = (1.0f - _model.SlideShare) * tau + _model.SlideShare * e;
            Vector3 pos = _tossFrom.Lerp(_tossTo, prog);
            if (tau < 1.0f)
                pos.Y = _tossFrom.Y + (_tossTo.Y - _tossFrom.Y) * tau + 4.0f * _model.TossLift * tau * (1.0f - tau);
            float yaw = _tossYaw0 + (_tossYawEnd - _model.SlideSpin - _tossYaw0) * tau + _model.SlideSpin * e;
            float w = Smooth(0.75f, 1.0f, tau);
            Quaternion rot = Quaternion.Identity.Slerp(_tossTilt, w) * new Quaternion(Vector3.Up, Mathf.DegToRad(yaw));
            _model.TurretOverride = new Transform3D(new Basis(rot), pos);
        }

        foreach (Flying f in _flying)
        {
            if (f.Rest)
                continue;
            f.V.Y -= grav * dt;
            Transform3D x = f.Node.GlobalTransform;
            x.Origin += f.V * dt;
            x.Basis = new Basis(f.Axis, f.W * dt) * x.Basis;
            float floor = BedUnder(x.Origin) + f.Half;
            if (x.Origin.Y < floor)
            {
                x.Origin.Y = floor;
                f.V = new Vector3(f.V.X * 0.5f, -0.3f * f.V.Y, f.V.Z * 0.5f);
                f.W *= 0.5f;
                if (Mathf.Abs(f.V.Y) < 0.15f * s)
                    f.Rest = true;
            }
            f.Node.GlobalTransform = x;
        }
    }

    private void Shots()
    {
        bool single = _capturePath is not null && _frame == _captureAt;
        bool seq = _sequenceDir is not null && _frame >= _sequenceFrom && _frame <= _sequenceTo
                   && (_frame - _sequenceFrom) % _sequenceStep == 0;
        if (single || seq)
        {
            Image image = GetViewport().GetTexture().GetImage();
            if (seq)
            {
                Directory.CreateDirectory(_sequenceDir!);
                image.SavePng($"{_sequenceDir}/f{_frame:D4}.png");
            }
            if (single)
            {
                Error err = image.SavePng(_capturePath!);
                GD.Print(err == Error.Ok ? $"capture: {_capturePath}" : $"capture to {_capturePath} failed: {err}");
            }
        }
        // Asked on every frame, not only on a saved one: a sequence whose end
        // is not on its step saved its last frame short of the end and ran on.
        bool done = (_capturePath is null || _frame >= _captureAt)
                    && (_sequenceDir is null || _frame >= _sequenceTo);
        if (done && (_capturePath is not null || _sequenceDir is not null))
            GetTree().Quit();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true, Echo: false } key)
            return;
        string kind = key.CtrlPressed ? "he-" : key.ShiftPressed ? "pierce-" : "ricochet-";
        switch (key.Keycode)
        {
            case Key.Space: Do("shot"); break;
            case Key.Key1: Do(kind + "front"); break;
            case Key.Key2: Do(kind + "right"); break;
            case Key.Key3: Do(kind + "rear"); break;
            case Key.Key4: Do(kind + "left"); break;
            case Key.Key5: Do("ground"); break;
            case Key.J: Do(_burning ? "unburn" : "burn"); break;
            case Key.K: Do("knock"); break;
            case Key.X: Do("destroy"); break;
            case Key.T: Do("ram"); break;
            case Key.Backspace: Do("reset"); break;
            case Key.Tab: _panel?.Flip(); break;
            case Key.Minus: _zoom = Mathf.Max(0.5f, _zoom / 1.25f); break;
            case Key.Equal: _zoom = Mathf.Min(8.0f, _zoom * 1.25f); break;
            case Key.F12:
                Directory.CreateDirectory(AssetRoot.Out);
                GetViewport().GetTexture().GetImage().SavePng(AssetRoot.Out + "/tank3d.png");
                break;
        }
    }

    // --- the panel ---------------------------------------------------------

    /// <summary>
    /// The side panel, the benches' own <see cref="ControlPanel"/>: for now one
    /// group - which tank stands on the board.
    ///
    /// The list is what is on disk (<see cref="ModelsOnDisk"/>), each named by
    /// its class; a pick is <see cref="Pick"/>, the same as <c>--do model=</c>.
    /// No file of captions behind it, as the benches' <c>bench.json</c> is:
    /// two rows do not need one, and the code's own captions stand.
    /// </summary>
    private void BuildPanel(CanvasLayer layer)
    {
        _panel = new ControlPanel();
        _panel.Prepare();
        _panel.Heading("tank3d.tank", "танк");
        var labels = new List<string>();
        foreach (string model in _models)
            labels.Add(PairAt(model) is int i and >= 0 ? $"{model}  {Pairs[i].Name}" : model);
        if (labels.Count > 0)
            _panel.Choice("tank3d.tank.model", "модель", labels,
                          () => _models.FindIndex(m => string.Equals(m, _modelTag, StringComparison.OrdinalIgnoreCase)),
                          i => Pick(_models[i]));
        _panel.Readout("tank3d.tank.note", () =>
            $"класс {_profile.Tag} x{_profile.Size:F2}, {_model.PixelsPerUnit:F1} px на единицу"
            + (_model.Turreted ? "" : ", без башни"));
        _panel.Expand("tank3d.tank", true);
        // Whether the pond is swum or drowned in - see TankTick.Amphibious.
        _panel.Heading("tank3d.water", "вода");
        _panel.Toggle("tank3d.water.amphibious", "ОПВТ: плывёт, без него тонет",
                      () => _amphibious, v => _amphibious = v);
        _panel.Toggle("tank3d.water.ripples", "рябь  (--no-ripples)", () => _ripples.Enabled, v =>
        {
            _ripples.Enabled = v;
            if (!v)
                _ripples.Settle();
        });
        _panel.Press("tank3d.water.pond", "к пруду: на берег, носом в воду", ToPond);
        _panel.Readout("tank3d.water.note", () =>
            $"палуба {_deckPx:F0} px, осадка {_deckPx * _profile.Draught:F0} px"
            + (Drowning ? $", тонет {Sink:P0}" : Swimming ? ", на плаву" : ""));
        _panel.Expand("tank3d.water", true);
        // The ram (Tank3DBench.Ram): which hull is rammed, where it stands, go.
        _panel.Heading("tank3d.ram", "таран");
        if (labels.Count > 0)
            _panel.Choice("tank3d.ram.target", "цель", labels,
                          () => _models.FindIndex(m => string.Equals(m, _otherTag, StringComparison.OrdinalIgnoreCase)),
                          i => PickOther(_models[i]));
        _panel.Press("tank3d.ram.place", "цель впереди: два гекса прямо, бортом", () => LineUp());
        _panel.Press("tank3d.ram.go", "таранить  (T)", RamGo);
        _panel.Press("tank3d.ram.remove", "убрать цель", RemoveOther);
        _panel.Readout("tank3d.ram.note", () => _other is null
            ? "цели нет"
            : $"{_other.Tag}, масса {_other.Profile.Mass} против {_profile.Mass}"
              + (_ramNote.Length > 0 ? $"\n{_ramNote}" : ""));
        _panel.Expand("tank3d.ram", true);
        layer.AddChild(_panel);
        _panel.AddHandle();
    }
}
