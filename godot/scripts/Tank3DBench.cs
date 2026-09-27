using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// <c>Tank3D.tscn</c>: one 3D tank (<see cref="TankModel"/>) on open ground,
/// with the bench's own effects hung off its joints - the pilot for moving the
/// board from sprite atlases to 3D units.
///
/// <b>The space is <see cref="Stage3D"/>'s, on purpose.</b> An orthographic
/// camera pitched by the elevation the hex tile declares, <c>Size</c> the
/// viewport height over the zoom, so one world unit is one screen pixel at zoom
/// 1: +X right, +Y up, +Z toward the camera. The Node3D effects
/// (<see cref="ProcKick"/>, <see cref="ProcSpall"/>, <see cref="ProcSlam"/>,
/// <see cref="SheetBlast"/>) were written for exactly that space, so they are
/// raised here with the numbers the board raises them with, not re-tuned.
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
    private string _spriteTag = "LTP";
    private float _zoom = 2.5f;
    private float _heading = 215.0f;
    private string? _capturePath;
    private bool _noUi;
    private int _captureAt = 60;
    private string? _sequenceDir;
    private int _sequenceFrom, _sequenceTo, _sequenceStep = 1;
    private readonly List<(int Frame, string What)> _script = new();

    // --- scene -----------------------------------------------------------

    private AtlasSet? _atlas;
    private TankModel _model = null!;
    private Node3D _rig = null!;
    private Camera3D _camera = null!;
    private Label _hud = null!;
    private int _frame;
    private string _note = "";

    /// <summary>Hull heading, degrees about +Y: 0 faces the camera (+Z).</summary>
    private float Heading
    {
        get => _heading;
        set { _heading = Mathf.PosMod(value, 360.0f); _rig.RotationDegrees = new Vector3(0, _heading, 0); }
    }

    private float Squash => _atlas is { } a && a.HexRect.Size.X > 0
        ? 2.0f * a.HexRect.Size.Y / (Mathf.Sqrt(3.0f) * a.HexRect.Size.X)
        : 0.5f;

    private float RiseFactor => Mathf.Sqrt(Mathf.Max(0.0f, 1.0f - Squash * Squash));

    private float HexWidth => _atlas?.HexRect.Size.X ?? 248.0f;

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
        try
        {
            _atlas = AtlasSet.Load(AssetRoot.Sprites, _spriteTag);
        }
        catch (Exception e)
        {
            GD.Print($"tank3d: no {_spriteTag} sprites ({e.Message}) - scale and effects from defaults");
        }
        float perUnit = PixelsPerUnit();
        _rig = new Node3D { Name = "Rig" };
        AddChild(_rig);
        _model = TankModel.Load(_modelTag, perUnit);
        _rig.AddChild(_model);
        Heading = _heading;

        GetViewport().Msaa3D = Viewport.Msaa.Msaa4X;
        BuildCamera();
        BuildWorld();
        BuildGround();
        BuildEffects();

        var layer = new CanvasLayer();
        AddChild(layer);
        _hud = new Label { Position = new Vector2(12, 8), Modulate = new Color(1, 1, 1, 0.85f), Visible = !_noUi };
        layer.AddChild(_hud);

        GD.Print($"tank3d: {_modelTag} at {perUnit:F2} px/unit (from {_spriteTag}), "
                 + $"squash {Squash:F4} = {Mathf.RadToDeg(Mathf.Asin(Squash)):F2} deg, hex {HexWidth:F0} px");
    }

    /// <summary>The model's units against the board's: the sprite set it copies
    /// was rendered at <c>units_per_pixel</c>, so the 3D tank stands exactly as
    /// big as its sprite did.</summary>
    private float PixelsPerUnit()
    {
        string path = $"{AssetRoot.Sprites}/{_spriteTag}/hull_atlas.json";
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            return 1.0f / doc.RootElement.GetProperty("units_per_pixel").GetSingle();
        }
        catch (Exception e)
        {
            GD.Print($"tank3d: {path}: {e.Message} - 160 px/unit");
            return 160.0f;
        }
    }

    private void BuildCamera()
    {
        const float back = 4000.0f;
        _camera = new Camera3D
        {
            Projection = Camera3D.ProjectionType.Orthogonal,
            KeepAspect = Camera3D.KeepAspectEnum.Height,
            Near = 1.0f,
            Far = back * 2.0f,
            RotationDegrees = new Vector3(-Mathf.RadToDeg(Mathf.Asin(Squash)), 0.0f, 0.0f),
            Current = true,
        };
        AddChild(_camera);
        FrameCamera();
    }

    /// <summary>Size the camera to the zoom and keep the tank a little below
    /// the middle, so a plume or a thrown turret has sky to go into.</summary>
    private void FrameCamera()
    {
        const float back = 4000.0f;
        float height = GetViewport().GetVisibleRect().Size.Y;
        _camera.Size = height / _zoom;
        Vector3 pivot = new(_rig.Position.X, 0.0f, _rig.Position.Z);
        // Up the screen by a sixth of the view: screen up is -Z on the ground.
        pivot.Z -= _camera.Size / 6.0f / Squash;
        _camera.Position = pivot + new Vector3(0.0f, back * Squash, back * RiseFactor);
        // Camera2D.Offset's sense: +y moves the view down the screen.
        Vector2 shake = ShakeOffset();
        _camera.Position += _camera.Basis.X * shake.X - _camera.Basis.Y * shake.Y;
    }

    private void BuildWorld()
    {
        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            // The soil art's mean, lit about as the cells are, so the seams
            // between their soft rims read as more ground.
            BackgroundColor = new Color(0.60f, 0.50f, 0.33f),
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = new Color(0.62f, 0.64f, 0.70f),
            AmbientLightEnergy = 0.55f,
        };
        AddChild(new WorldEnvironment { Environment = env });
        // From over the camera's left shoulder, the side the sprites are lit
        // from, high enough that the shadow stays short and on the ground.
        var sun = new DirectionalLight3D
        {
            RotationDegrees = new Vector3(-52.0f, -35.0f, 0.0f),
            LightEnergy = 1.25f,
            ShadowEnabled = true,
            DirectionalShadowMaxDistance = 3000.0f,
        };
        AddChild(sun);
    }

    /// <summary>
    /// The ground: the board's cells if there is art for them, a plain plane if
    /// not - never both, because neither may write depth (see
    /// <see cref="BuildCells"/>) and two opaque layers that do not are drawn in
    /// whatever order the renderer likes.
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
        if (!terrain.Any || _atlas is null)
            return false;
        var hexRect = new Rect2I((Vector2I)_atlas.HexRect.Position, (Vector2I)_atlas.HexRect.Size);
        float scale = terrain.ScaleTo(hexRect);
        float w = _atlas.HexRect.Size.X, h = _atlas.HexRect.Size.Y;
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
            if (a == "--model" && more) _modelTag = args[++i];
            else if (a == "--sprites" && more) _spriteTag = args[++i];
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
            case "drive": _driveScripted = 1.0f; break;
            case "stop": _driveScripted = 0.0f; break;
            case "left": _turnScripted = 1.0f; break;
            case "right": _turnScripted = -1.0f; break;
            case "straight": _turnScripted = 0.0f; break;
            default:
                if (what.StartsWith("turret=", StringComparison.Ordinal))
                    _model.Yaw = Mathf.DegToRad(F(what[7..], 0));
                else if (what.StartsWith("elev=", StringComparison.Ordinal))
                    _model.Elevation = Mathf.DegToRad(F(what[5..], 0));
                else if (what.StartsWith("heading=", StringComparison.Ordinal))
                    Heading = F(what[8..], _heading);
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

        float yawDeg = Mathf.RadToDeg(_model.Yaw);
        TankModel.Landing land = _model.LandingNear(yawDeg + 150.0f);
        _tossFrom = _model.TurretRest;
        _tossTo = land.At;
        _tossYaw0 = yawDeg;
        _tossYawEnd = land.YawDeg + 360.0f * Mathf.Ceil((yawDeg + 300.0f - land.YawDeg) / 360.0f);
        // The landing's rotation with its own yaw taken out, so the spin is
        // ours and the settle is the deck's tilt alone.
        _tossTilt = land.Rest * new Quaternion(Vector3.Up, -Mathf.DegToRad(land.YawDeg));
        _model.TurretOverride = _model.Turret.Transform;

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
        float target = Mathf.Clamp(driveIn, -1.0f, 1.0f) * MaxSpeed;
        float accel = Mathf.MoveToward(_speed, target, Accel * dt) - _speed;
        _speed += accel;
        float a = accel / dt / Accel;
        float turn = Mathf.Clamp(turnIn, -1.0f, 1.0f) * TurnRate * 0.5f;
        Heading += turn * dt;
        Vector3 forward = _rig.Basis.Z;
        _rig.Position += forward * _speed * dt;
        float step = _speed * dt / _model.PixelsPerUnit;
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
        _model.Apply();
        FxProcess(dt, _speed, a);
        FrameCamera();

        _hud.Text = $"{_modelTag} 3D ({_spriteTag} scale, effects)  {_fate}  {_note}\n"
                    + "Space shot   1-4 ricochet front/right/rear/left   Shift+1-4 pierce   Ctrl+1-4 HE   5 round in the ground\n"
                    + "J burning   K knocked out   X destroyed   Backspace reset   WASD drive   Q/E turret   R/F gun   -/= zoom   F12 shot";
        Shots();
        _frame++;
    }

    private void AdvanceFate(float dt)
    {
        float t = _sinceFate;
        if (_fate == Fate.Knocked || _model.TurretOverride is null)
        {
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

        foreach (Flying f in _flying)
        {
            if (f.Rest)
                continue;
            f.V.Y -= grav * dt;
            Transform3D x = f.Node.GlobalTransform;
            x.Origin += f.V * dt;
            x.Basis = new Basis(f.Axis, f.W * dt) * x.Basis;
            if (x.Origin.Y < f.Half)
            {
                x.Origin.Y = f.Half;
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
        if (!single && !seq)
            return;
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
        bool done = (_capturePath is null || _frame >= _captureAt)
                    && (_sequenceDir is null || _frame >= _sequenceTo);
        if (done)
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
            case Key.Backspace: Do("reset"); break;
            case Key.Minus: _zoom = Mathf.Max(0.5f, _zoom / 1.25f); break;
            case Key.Equal: _zoom = Mathf.Min(8.0f, _zoom * 1.25f); break;
            case Key.F12:
                Directory.CreateDirectory(AssetRoot.Out);
                GetViewport().GetTexture().GetImage().SavePng(AssetRoot.Out + "/tank3d.png");
                break;
        }
    }
}
