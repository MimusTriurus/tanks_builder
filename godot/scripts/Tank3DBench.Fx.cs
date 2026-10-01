using System;
using System.Collections.Generic;
using System.IO;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The bench's effects on the 3D tank - the same classes, raised with the
/// arguments <see cref="TankBench"/> and <see cref="Stage3D"/> raise them with,
/// and read off the model: no sprite set is loaded for the tank.
///
/// <b>Two kinds, two hosts.</b>
/// <list type="bullet">
/// <item><b>Node3D effects</b> (<see cref="ProcKick"/>, <see cref="ProcSpall"/>,
/// <see cref="ProcSlam"/>, <see cref="ProcBall"/>, <see cref="SheetBlast"/>) live
/// in this scene's world, which is the space they were written for. Each is
/// pooled, built once, and ticked here because none of them ticks itself.</item>
/// <item><b>Node2D effects</b> (<see cref="ProcFire"/>, both
/// <see cref="ProcSmoke"/>s, <see cref="ProcFume"/>, <see cref="ProcFlash"/>,
/// <see cref="ProcPierce"/>) are children of a <see cref="TankSprite"/> and read
/// everything off it. So there is one, in a render target, with no atlas: it
/// draws nothing of its own, keeps the clocks, and hands the effects a
/// <see cref="ModelShape"/> - the model's exhausts, muzzle and bore this frame,
/// and a height map rendered live off the model (<see cref="BuildHeights"/>).
/// </item>
/// </list>
///
/// <b>Where a card stands is the one new rule.</b> An effect card is upright
/// and depth-tested against an opaque model, so a card seated at the tank's
/// centre would be hidden by its near half - a spray off a front plate turned to
/// the camera would go behind that plate. Every card is therefore slid along the
/// view ray, which moves nothing on screen, until its plane is just in front of
/// what makes the effect: the muzzle, the turret ring. The model's own depth
/// then hides exactly what stands in front of that. The fire and the smoke are
/// the exception - a plane cannot follow a sloped grille - and stand in front
/// of the whole tank, hidden element by element by the height map instead.
/// </summary>
public sealed partial class Tank3DBench
{
    // --- 3D effects -------------------------------------------------------

    private readonly List<ProcKick> _kicks = new();
    private readonly List<ProcKick> _drifts = new();
    private readonly List<ProcKick> _bursts = new();
    private readonly List<ProcSpall> _spalls = new();
    private readonly List<ProcSlam> _slams = new();
    private readonly List<ProcBall> _balls = new();
    private readonly List<SheetBlast> _booms = new();
    private readonly List<PitArt> _pits = new();
    private int _nextKick, _nextDrift, _nextBurst, _nextSpall, _nextSlam, _nextBall, _nextBoom, _nextPit;
    private const int Pool = 6;
    private const int DriftPool = 48;

    /// <summary>Calibre of the rounds this bench throws - the bench's default,
    /// <see cref="TankTick.Calibre"/> 1.</summary>
    private static float Might => Ordnance.At(1);

    /// <summary>World units in front of its source a card stands.</summary>
    private const float Margin = 4.0f;

    /// <summary>
    /// Phases a lap of the exhaust's and the fire's loops, which is what their
    /// clocks count in (<see cref="ExhaustLoop"/>, <see cref="BurnLoop"/>).
    ///
    /// <b>The sprite sets' own number, and the same in all five</b>: every
    /// exhaust, fire and burn layer shipped holds twelve. The built plume and
    /// flame are continuous, so it is a tempo here and not a frame count - the
    /// loop the render was made on, kept so both kinds of tank breathe alike.
    /// </summary>
    private const int LoopPhases = 12;

    // --- the sprite that carries the 2D effects ----------------------------

    private sealed class Card
    {
        public required SubViewport Paint;
        public required Node2D Holder;
        public required MeshInstance3D Quad;
    }

    /// <summary>Render-target side in board px, and how many target pixels to
    /// a board px - two, so the card is not magnified soft at the default zoom.
    /// </summary>
    private const int CardSize = 512;
    private const int CardZoom = 2;

    private TankSprite? _sprite;
    private ModelShape? _shape;
    private Card? _rear, _front, _glow;
    private readonly List<CanvasItem> _painted = new();

    private readonly ExhaustLoop _exhaust = new();
    private readonly BurnLoop _burn = new();
    /// <summary>The fire and the column in the model's look, in the world -
    /// null under <c>--fx2d</c>, which keeps the sprites' on the card.</summary>
    private CelBurn? _celBurn;
    private CelExhaust? _celExhaust;
    private CelShot? _celShot;
    private CelHit? _celHit;
    private CelBlast? _celBlast;
    private CelDeath? _celDeath;
    private CelDust? _celDust;
    private readonly List<CelDust.Belt> _belts = new();
    /// <summary>Each belt's run as it stood last frame, model units - the
    /// belts' own travel, pivots included (<see cref="TankModel.Skid"/>).</summary>
    private readonly List<float> _beltWas = new();
    private readonly List<float> _beltRun = new();
    private Vector3 _hullAhead = Vector3.Back, _hullLeft = Vector3.Right;
    /// <summary>The belts' ruts - the model's, under <c>--fx2d</c> too: the
    /// sprites' <see cref="TrackMarks"/> needs a <see cref="Vehicle"/>, which
    /// this scene has not got. The stage's node, like the pools; the reset
    /// that comes with a new model wipes them as Backspace does.</summary>
    private CelRuts? _ruts;
    /// <summary>The hull going into the pond, in the model's look - null under
    /// <c>--fx2d</c>, which keeps the board's <see cref="Plunge"/>. The stage's,
    /// like the ruts: made once.</summary>
    private CelSplash? _splash;
    /// <summary>The wake and the bow wave in the water - the model's, under
    /// <c>--fx2d</c> too (the board's <see cref="Wake"/> needs a <see cref="Vehicle"/>).</summary>
    private CelWake? _wake;
    private readonly List<CelRuts.Belt> _rutBelts = new();
    private readonly List<(Vector3 At, Vector3 Out)> _exhaustPorts = new();
    private readonly List<Vector3> _ports = new();
    private readonly Wreck _wreck = new();
    private readonly HitLoop _hitLoop = new();
    private readonly CameraShake _shake = new();
    private int _shotFrame = -1;
    private bool _burning;

    /// <summary>The struck point in the hull's own frame, so the light through
    /// the hole follows the hull as it rocks.</summary>
    private Vector3 _hitLocal;
    /// <summary>The part the last penetration went into - <see cref="_hitLocal"/>
    /// is in its frame; the hull when none is named.</summary>
    private Node3D? _hitNode;
    private TriangleMesh? _hullHits;
    private MovementProfile _profile = MovementProfile.Light;

    /// <summary>The model's paint as it was loaded, per material - cel or the
    /// glTF's own under <c>--pbr</c> - for the wreck's char to dim and a reset
    /// to put back.</summary>
    private readonly List<(Material Mat, Color Albedo)> _paint = new();

    private static Color PaintOf(Material m) =>
        m is ShaderMaterial cel ? Toon.PaintOf(cel) : ((BaseMaterial3D)m).AlbedoColor;

    private static void Repaint(Material m, Color albedo)
    {
        if (m is ShaderMaterial cel)
            Toon.Paint(cel, albedo);
        else
            ((BaseMaterial3D)m).AlbedoColor = albedo;
    }

    /// <summary>
    /// Nothing of a card goes under the ground: a vertex below
    /// <c>ground</c> is slid up its own view ray (<c>toward</c>, the camera's
    /// way) until it stands on it. The ray is the one direction that moves
    /// nothing on screen, so the picture is the same picture; what changes is
    /// only its depth, which the board's depth-written ground would otherwise
    /// win below the tank's contact - the stage's own bend for its sprites
    /// (<c>Stage3D.Body</c>), per vertex. The model still hides the lifted part
    /// wherever it hid the buried one: anything above the ground on a view ray
    /// is nearer the camera than where that ray meets the ground.
    /// </summary>
    private const string Bend = @"
uniform float ground = -100000.0;
uniform vec3 toward = vec3(0.0, 0.5, 0.866);
void vertex() {
    vec3 w = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
    if (w.y < ground) {
        w += toward * ((ground - w.y) / toward.y);
        VERTEX = (inverse(MODEL_MATRIX) * vec4(w, 1.0)).xyz;
    }
}";

    /// <summary>Rows the card's quad is cut into for <see cref="Bend"/>, which
    /// bends at vertices: about nine world units a row.</summary>
    private const int CardRows = 64;

    private static readonly Shader CardShader = new()
    {
        Code = @"
shader_type spatial;
render_mode unshaded, blend_premul_alpha, depth_draw_never, cull_disabled;
uniform sampler2D picture : source_color, filter_linear;" + Bend + @"
void fragment() {
    vec4 c = texture(picture, UV);
    ALBEDO = c.rgb;
    ALPHA = c.a;
}",
    };

    /// <summary>
    /// The same, adding light and nothing else - for <see cref="ProcPierce"/>,
    /// which draws the hull's silhouette additively to use its alpha as a mask.
    /// Over an empty target that alpha is the whole silhouette, and a
    /// premultiplied card would draw it black.
    /// </summary>
    private static readonly Shader GlowShader = new()
    {
        Code = @"
shader_type spatial;
render_mode unshaded, blend_add, depth_draw_never, cull_disabled;
uniform sampler2D picture : source_color, filter_linear;" + Bend + @"
void fragment() {
    ALBEDO = texture(picture, UV).rgb;
}",
    };

    // --- the height map -------------------------------------------------------

    /// <summary>The render layer the model's stand-ins draw on, which the
    /// scene's camera does not look at and the height map's camera looks at
    /// alone.</summary>
    private const uint GhostLayer = 1u << 19;

    /// <summary>
    /// Texels of the height map a board px: the cards' own, so the map is as
    /// fine as what reads it.
    ///
    /// <b>Finer buys little, and that was measured.</b> The map is read
    /// nearest - a height blended across a silhouette invents a surface - so
    /// where the tank ends the cut steps. On a casemate's roof edge at 3.5x
    /// zoom, going from one texel a px to two changed 329 px of the picture and
    /// from two to four 119, and the steps left after four are where two left
    /// them: the cut is made once per <em>card</em> texel, and the card is
    /// <see cref="CardZoom"/> to a px - under two screen px at 3.5x.
    /// </summary>
    private const int HeightZoom = CardZoom;

    private SubViewport? _heights;
    private Camera3D? _heightEye;
    private ShaderMaterial? _heightInk;
    private readonly List<(GeometryInstance3D Ghost, GeometryInstance3D Source, bool OnTurret)> _ghosts = new();

    /// <summary>
    /// Every surface's height on the byte scale the built layers read
    /// (<see cref="Plumes.DepthCode"/>): 0 where nothing is, 1..255 over
    /// <c>low</c>..<c>high</c>. The nearest surface wins in the depth buffer,
    /// and under this camera nearest is highest - the relation the whole
    /// holdout stands on - so what is left in a pixel is the height the effects
    /// ask for.
    ///
    /// <b>Written as the value it is.</b> An unshaded colour reaches a target
    /// under <c>gl_compatibility</c> with no sRGB curve on it - measured: handed
    /// over decoded, the deck's 78 came back as 19 and the belts' lowest codes
    /// as nought, which is "no tank".
    /// </summary>
    private static readonly Shader HeightShader = new()
    {
        Code = @"
shader_type spatial;
render_mode unshaded, cull_disabled, shadows_disabled, ambient_light_disabled, fog_disabled;
uniform float low = 0.0;
uniform float high = 1.0;
void fragment() {
    float y = (INV_VIEW_MATRIX * vec4(VERTEX, 1.0)).y;
    float lift = clamp((y - low) / max(high - low, 1e-4), 0.0, 1.0);
    float code = (1.0 + 254.0 * lift) / 255.0;
    ALBEDO = vec3(code);
}",
    };

    /// <summary>
    /// The height map, live: stand-ins of the model, drawn by a camera of their
    /// own into a target the size of a card (<see cref="HeightZoom"/> texels a
    /// px), centred on the card's anchor, looking down the scene camera's own
    /// ray.
    ///
    /// <b>This is what hides the fire and the smoke, element by element</b>, as
    /// the atlas's map does on a sprite - not the model's depth against the
    /// card. A card is one plane, and a plane at the port was cut by the half of
    /// a sloped grille that leans toward the camera: the flame came out lying
    /// under the slats. So the rear card stands in front of the whole tank and
    /// the map decides.
    ///
    /// <b>Hull, belts and turret; not the gun, and not a turret that has been
    /// thrown.</b> The sprite's map is the hull and belts only because its
    /// turret is a layer the heading sorts over or under the column; here it is
    /// geometry, and the map is where geometry can hide an element. The gun
    /// stays out for the atlas's reason - the flash's height is the bore's, and
    /// the tube's top stands a radius over it, so a gun in the map would cut its
    /// own flash. A thrown turret lies on the deck over the port, and the board
    /// draws it under the fire (<see cref="FxProcess"/>).
    /// </summary>
    private void BuildHeights()
    {
        _heights = new SubViewport
        {
            Name = "Heights",
            Size = new Vector2I(CardSize * HeightZoom, CardSize * HeightZoom),
            TransparentBg = true,
            Msaa3D = Viewport.Msaa.Disabled,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
            RenderTargetClearMode = SubViewport.ClearMode.Always,
        };
        AddChild(_heights);
        _heightEye = new Camera3D
        {
            Projection = Camera3D.ProjectionType.Orthogonal,
            KeepAspect = Camera3D.KeepAspectEnum.Height,
            Size = CardSize,
            Near = 1.0f,
            Far = Back * 2.0f,
            CullMask = GhostLayer,
            RotationDegrees = new Vector3(-Mathf.RadToDeg(Mathf.Asin(Squash)), 0.0f, 0.0f),
            // Its own, empty: the scene's environment would paint the grey
            // background into the target and light nothing that matters here.
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.ClearColor,
                AmbientLightSource = Godot.Environment.AmbientSource.Disabled,
            },
        };
        _heights.AddChild(_heightEye);
        _heightEye.MakeCurrent();
        _heightInk = new ShaderMaterial { Shader = HeightShader };
        Ghost(_model);
        _shape!.Map = _heights.GetTexture();
        _shape.MapSide = CardSize;
    }

    /// <summary>A stand-in for every mesh under <paramref name="node"/> but the
    /// gun's, on <see cref="GhostLayer"/>, drawn with the height ink.</summary>
    private void Ghost(Node node, bool onTurret = false)
    {
        if (node == _model.Mantlet)
            return;
        onTurret |= node == _model.Turret;
        GeometryInstance3D? ghost = node switch
        {
            MeshInstance3D { Mesh: not null } m => new MeshInstance3D { Mesh = m.Mesh },
            MultiMeshInstance3D { Multimesh: not null } mm => new MultiMeshInstance3D { Multimesh = mm.Multimesh },
            _ => null,
        };
        if (ghost is not null)
        {
            ghost.Layers = GhostLayer;
            ghost.MaterialOverride = _heightInk;
            ghost.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
            AddChild(ghost);
            _ghosts.Add((ghost, (GeometryInstance3D)node, onTurret));
        }
        foreach (Node child in node.GetChildren())
            Ghost(child, onTurret);
    }

    /// <summary>The stand-ins onto the model as it stands, the camera onto the
    /// anchor, and the byte range onto the shape's.</summary>
    private void SyncHeights(Vector3 anchor)
    {
        if (_heightEye is null || _heightInk is null || _shape is null)
            return;
        bool thrown = _model.TurretOverride is not null;
        foreach (var (ghost, source, onTurret) in _ghosts)
        {
            bool shown = GodotObject.IsInstanceValid(source) && source.IsVisibleInTree()
                         && !(onTurret && thrown);
            ghost.Visible = shown;
            if (shown)
                ghost.GlobalTransform = source.GlobalTransform;
        }
        _heightEye.Position = anchor + new Vector3(0.0f, Back * Squash, Back * RiseFactor);
        float ppu = _model.PixelsPerUnit;
        _heightInk.SetShaderParameter("low", anchor.Y + (float)_shape.HeightLow * ppu);
        _heightInk.SetShaderParameter("high", anchor.Y + (float)_shape.HeightHigh * ppu);
    }

    /// <summary>Everything <see cref="BuildHeights"/> made, gone.</summary>
    private void FreeHeights()
    {
        foreach (var (ghost, _, _) in _ghosts)
            ghost.QueueFree();
        _ghosts.Clear();
        _heights?.QueueFree();
        _heights = null;
        _heightEye = null;
    }

    /// <summary>The height map as it stands, to a file - <c>--do heights</c>.
    /// </summary>
    private void SaveHeights()
    {
        if (_heights is null)
            return;
        Directory.CreateDirectory(AssetRoot.Out);
        string path = AssetRoot.Out + "/tank3d_heights.png";
        Error err = _heights.GetTexture().GetImage().SavePng(path);
        GD.Print(err == Error.Ok ? $"heights: {path}" : $"heights to {path} failed: {err}");
    }

    // --- setup ------------------------------------------------------------

    private void BuildEffects()
    {
        // The ruts are the stage's: made once, told the new belts' pitch and
        // width, and the pens lifted. Unmount's reset has already wiped them.
        if (_ruts is null)
        {
            _ruts = new CelRuts { Name = "Ruts" };
            AddChild(_ruts);
        }
        if (_model.Tracks.Count > 0)
        {
            TankModel.Track belt = _model.Tracks[0];
            _ruts.Build(belt.Pitch * _model.PixelsPerUnit,
                        belt.Links.Multimesh.Mesh.GetAabb().Size.X * _model.PixelsPerUnit);
        }
        _ruts.Lift();
        if (_wake is null)
        {
            _wake = new CelWake { Name = "Wake" };
            AddChild(_wake);
            _wake.Build(Squash, RiseFactor);
        }
        if (_splash is null && !_fx2d)
        {
            _splash = new CelSplash { Name = "Splash" };
            AddChild(_splash);
            _splash.Build(Squash, RiseFactor);
        }
        if (_profile.Turreted != _model.Turreted)
            GD.Print($"tank3d: {_modelTag} is {(_model.Turreted ? "turreted" : "a casemate")} but "
                     + $"moves as the {_profile.Tag} class, which is {(_profile.Turreted ? "turreted" : "a casemate")}"
                     + " - the class comes from the model's pair");
        foreach (MeshInstance3D mesh in Meshes(_model))
            for (int s = 0; s < mesh.Mesh.GetSurfaceCount(); s++)
                if (mesh.Mesh.SurfaceGetMaterial(s) is Material m and (ShaderMaterial or BaseMaterial3D)
                    && !_paint.Exists(x => x.Mat == m))
                    _paint.Add((m, PaintOf(m)));
        if (_model.Hull is MeshInstance3D hull)
            _hullHits = hull.Mesh.GenerateTriangleMesh();

        _shape = new ModelShape(_model, Squash, RiseFactor);
        BuildHeights();
        _exhaust.Phases = LoopPhases;
        _exhaust.TopSpeed = _profile.TopSpeed;
        _burn.Phases = LoopPhases;

        _rear = MakeCard("Rear");
        _front = MakeCard("Front");
        _glow = MakeCard("Glow", GlowShader);
        // No atlas: nothing of the sprite's own is drawn, every layer of it
        // being a question to the atlas, and the built effects read the shape.
        _sprite = new TankSprite
        {
            Shape = _shape,
            Name = "Effects",
            ProceduralExhaust = true,
            ProceduralSmoke = true,
            ProceduralFire = true,
            Source = FlashSource.Built,
        };
        _rear.Holder.AddChild(_sprite);
        // Out of the sprite and into the card that stands where each one is
        // made. Each still reads the sprite through its Tank field and draws in
        // its own local frame, so a holder at the sprite's place draws it the same.
        foreach (Node child in _sprite.GetChildren())
        {
            Card? to = child switch
            {
                ProcFume or ProcFlash => _front,
                ProcPierce => _glow,
                _ => null,
            };
            if (to is null)
                continue;
            _sprite.RemoveChild(child);
            to.Holder.AddChild(child);
        }
        if (!_fx2d)
        {
            // The sprites' fire, column, plume, flash and fume stay what they
            // are, for the 2D tanks; on the model they are hidden, CelBurn
            // draws the first two, CelExhaust the plume (hidden where it is
            // ticked) and CelShot the shot - its muzzle cloud too (FxShot).
            if (_sprite.Blaze is not null)
                _sprite.Blaze.Visible = false;
            if (_sprite.Column is not null)
                _sprite.Column.Visible = false;
            _celBurn = new CelBurn { Name = "Burn" };
            AddChild(_celBurn);
            _celBurn.Build(_model.HullLength * _model.PixelsPerUnit);
            _celBurn.Paint = _model.Cel;
            _celExhaust = new CelExhaust { Name = "Exhaust" };
            AddChild(_celExhaust);
            _celExhaust.Build(_model.HullLength * _model.PixelsPerUnit);
            _celExhaust.Solids = _solids;
            if (_sprite.Flare is not null)
                _sprite.Flare.Visible = false;
            if (_sprite.Fume is not null)
                _sprite.Fume.Visible = false;
            _celShot = new CelShot { Name = "Shot" };
            AddChild(_celShot);
            _celShot.Build(_model.HullLength * _model.PixelsPerUnit);
            _celShot.Solids = _solids;
            _celHit = new CelHit { Name = "Hits" };
            AddChild(_celHit);
            _celHit.Build(_model.HullLength * _model.PixelsPerUnit);
            _celHit.Solids = _solids;
            _celHit.Targets(_model, _model.Cel);
            _celBlast = new CelBlast { Name = "Blast" };
            AddChild(_celBlast);
            _celBlast.Build(_model.HullLength * _model.PixelsPerUnit);
            _celDeath = new CelDeath { Name = "Death" };
            AddChild(_celDeath);
            _celDeath.Build(_model.HullLength * _model.PixelsPerUnit, HexWidth);
            _celDeath.Ground = w => Foot(w).Y;
            // The belts' dust too: the sprites' drift is not laid (Dust).
            _celDust = new CelDust { Name = "TrackDust" };
            AddChild(_celDust);
            _celDust.Build(_model.HullLength * _model.PixelsPerUnit);
            _celDust.Solids = _solids;
            _beltWas.Clear();
        }
        _painted.Add(_sprite);
        foreach (Card c in new[] { _rear, _front, _glow })
            foreach (Node n in c.Holder.GetChildren())
                if (n is CanvasItem item && item != _sprite)
                    _painted.Add(item);
        foreach (Node n in _sprite.GetChildren())
            if (n is CanvasItem item)
                _painted.Add(item);
    }

    private static IEnumerable<MeshInstance3D> Meshes(Node root)
    {
        foreach (Node n in root.GetChildren())
        {
            if (n is MeshInstance3D m && m.Mesh is not null)
                yield return m;
            foreach (MeshInstance3D d in Meshes(n))
                yield return d;
        }
    }

    private Card MakeCard(string name, Shader? shader = null)
    {
        var paint = new SubViewport
        {
            Name = name,
            Size = new Vector2I(CardSize * CardZoom, CardSize * CardZoom),
            TransparentBg = true,
            Disable3D = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
            RenderTargetClearMode = SubViewport.ClearMode.Always,
        };
        AddChild(paint);
        var holder = new Node2D
        {
            Position = Vector2.One * CardSize * CardZoom * 0.5f,
            Scale = Vector2.One * CardZoom,
        };
        paint.AddChild(holder);
        var mat = new ShaderMaterial { Shader = shader ?? CardShader, RenderPriority = Stage3D.StandOrder };
        mat.SetShaderParameter("picture", paint.GetTexture());
        mat.SetShaderParameter("toward", View);
        // Upright, facing +Z like every card on the stage; a target pixel
        // (u, w) lands at (u - half, (half - w) / rise) so it is drawn exactly
        // one board px from its neighbour on screen.
        var quad = new MeshInstance3D
        {
            Name = name + "Card",
            Mesh = new QuadMesh { Size = new Vector2(CardSize, CardSize / RiseFactor), SubdivideDepth = CardRows - 1 },
            MaterialOverride = mat,
            SortingUseAabbCenter = false,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(quad);
        return new Card { Paint = paint, Holder = holder, Quad = quad };
    }

    /// <summary>Everything <see cref="BuildEffects"/> made for this tank, gone.
    /// </summary>
    private void FreeEffects()
    {
        // The cards go at the end of the frame and still draw in it, while the
        // model they read is already off the rig: without a shape, an effect
        // has nothing to draw and asks nothing of the model.
        if (_sprite is not null)
            _sprite.Shape = null;
        foreach (Card? card in new[] { _rear, _front, _glow })
        {
            card?.Paint.QueueFree();
            card?.Quad.QueueFree();
        }
        _rear = _front = _glow = null;
        _sprite = null;
        _celBurn?.QueueFree();
        _celBurn = null;
        _celExhaust?.QueueFree();
        _celExhaust = null;
        _celShot?.QueueFree();
        _celShot = null;
        _celHit?.QueueFree();
        _celHit = null;
        _celBlast?.QueueFree();
        _celBlast = null;
        _celDeath?.QueueFree();
        _celDeath = null;
        _celDust?.QueueFree();
        _celDust = null;
        _painted.Clear();
        FreeHeights();
        _shape = null;
    }

    /// <summary>Where the cards hang and px (0, 0) is: the middle of the
    /// model's height over its ground point, riding the rig - the sprite's
    /// anchor was its ring raised to the middle of its bounds, for the same
    /// room above and below.</summary>
    private Vector3 CardAnchor() => _model.Tank.ToGlobal(new Vector3(0.0f, _model.Size.Y * 0.5f, 0.0f));

    // --- the board's terms --------------------------------------------------

    /// <summary>A ground point (Y = 0) as the harness would pass it: the flat
    /// board's row is world depth times the squash.</summary>
    private Vector2 Board(Vector3 w) => new(w.X, w.Z * Squash);

    /// <summary>
    /// The ground under a world point as the stage names a spot: where it is
    /// <em>drawn</em>, its flat row lifted by the ground's height - what every
    /// effect's <c>Sit</c> and the crater's <c>Show</c> take beside that height
    /// (<see cref="Stage3D.Trunk"/> and <see cref="Stage3D.Ground"/> add it back).
    ///
    /// <b>The flat point in its place put everything on a level in front of
    /// where it was</b>, by the lift over the squash: on the raised cell the
    /// belts' dust slid down the cliff and the shot's cloud fell out in front of
    /// the hull. On the ground floor the lift is nought, and the two agree.
    /// </summary>
    private Vector2 Spot(Vector3 w) => Board(w) - new Vector2(0.0f, LiftAt(w));

    /// <summary>A world offset as screen px, y down - what the board calls a
    /// snout or a plate.</summary>
    private Vector2 Drawn(Vector3 d) => new(d.X, d.Z * Squash - d.Y * RiseFactor);

    /// <summary>
    /// The screen direction of a world direction on the ground - what
    /// <see cref="AtlasSet.GroundDirection"/> answers for the heading it points
    /// at, and unnormalised the same way: its length is how much of a ground
    /// length survives the squash, which every effect that takes one expects.
    /// </summary>
    private Vector2 Along(Vector3 d)
    {
        var flat = new Vector2(d.X, d.Z);
        if (flat.LengthSquared() < 1e-12f)
            return Vector2.Zero;
        flat = flat.Normalized();
        return new Vector2(flat.X, flat.Y * Squash);
    }

    private static readonly Vector3 Up = Vector3.Up;

    /// <summary>Toward the camera along its view - the one direction a card can
    /// move without moving on screen.</summary>
    private Vector3 View => new(0.0f, Squash, RiseFactor);

    /// <summary>Slide a node along the view ray until it stands at depth
    /// <paramref name="z"/>.</summary>
    private void StandAt(Node3D node, float z)
    {
        float t = (z - node.Position.Z) / RiseFactor;
        node.Position += View * t;
    }

    private static T Next<T>(List<T> pool, ref int next, int size, Func<T> make)
    {
        while (pool.Count < size)
            pool.Add(make());
        T item = pool[next % size];
        next = (next + 1) % size;
        return item;
    }

    // --- events -------------------------------------------------------------

    private void FxShot()
    {
        _shotFrame = 0;
        Vector3 muzzle = _model.Muzzle.GlobalPosition;
        Vector3 foot = Foot(muzzle);
        Vector3 bore = _model.Muzzle.GlobalBasis.Z;
        if (_celShot is not null)
        {
            _celShot.Fire(muzzle, bore, foot);
            return;
        }
        ProcKick kick = Next(_kicks, ref _nextKick, Pool, () =>
        {
            var made = new ProcKick();
            AddChild(made);
            made.Build(HexWidth, Squash, RiseFactor);
            return made;
        });
        // Stage3D.Raise, minus the bench's panel hook.
        kick.Might = 1.0f;
        kick.Might *= Might;
        kick.Order = Stage3D.StandOrder;
        kick.Dress(ProcKick.Cloud.Muzzle);
        // Seated under the muzzle rather than at the tank's middle - see the
        // class note: the middle is behind the near half of an opaque hull.
        kick.Sit(Spot(foot), LiftAt(foot), Squash, RiseFactor);
        kick.Aim(Along(new Vector3(bore.X, 0.0f, bore.Z)), Drawn(muzzle - foot));
        kick.Fire();
    }

    /// <summary>
    /// Where a round travelling <paramref name="travel"/> (hull frame) meets the
    /// hull, and the plate's normal there - by a ray at the hull mesh, because
    /// the plates are sloped and a box would put a front-plate hit in the air.
    /// </summary>
    private (Vector3 At, Vector3 Normal) Strike(Vector3 travel)
    {
        var hull = _model.Hull;
        Aabb box = hull is MeshInstance3D mi ? mi.GetAabb() : new Aabb(-Vector3.One * 0.4f, Vector3.One * 0.8f);
        Vector3 aim = box.GetCenter() + new Vector3(0.0f, box.Size.Y * 0.08f, 0.0f);
        Vector3 from = aim - travel * 2.0f;
        if (_hullHits is not null)
        {
            var hit = _hullHits.IntersectRay(from, travel);
            if (hit.Count > 0 && hit.ContainsKey("position"))
            {
                Vector3 n = ((Vector3)hit["normal"]).Normalized();
                if (n.Dot(travel) > 0.0f)
                    n = -n;
                return ((Vector3)hit["position"], n);
            }
        }
        Vector3 half = box.Size * 0.5f;
        Vector3 at = aim - travel * new Vector3(half.X, 0, half.Z).Dot(travel.Abs());
        return (at, -travel);
    }

    private void FxHit(int side, Vector3 travel, bool pierce)
    {
        // A little off the plate's normal, so a glancing round has somewhere to
        // glance to, and a little downward, as a round arriving from range does.
        float skew = side % 2 == 0 ? 25.0f : -25.0f;
        Vector3 u = travel.Rotated(Vector3.Up, Mathf.DegToRad(skew));
        u = (u + new Vector3(0.0f, -0.12f, 0.0f)).Normalized();
        (Vector3 local, Vector3 normalLocal) = Strike(u);
        Node3D hull = _model.Hull;
        Vector3 at = hull.ToGlobal(local);
        Vector3 n = (hull.GlobalBasis * normalLocal).Normalized();
        Vector3 uw = (hull.GlobalBasis * u).Normalized();
        Node3D struck = hull;
        // The model's own: anywhere on its plates the round can see, and a
        // mark left there - see CelHit.
        if (_celHit?.Aim((hull.GlobalBasis * travel).Normalized()) is { } aimed)
        {
            (struck, at, n, uw) = (aimed.Part, aimed.At, aimed.N, aimed.Way);
            _celHit.Leave(aimed.Part, at, n, uw, pierce ? CelHit.Kind.Hole : CelHit.Kind.Gouge);
        }
        bool behind = n.Z <= 0.0f;
        Vector3 foot = Foot(at);
        if (pierce)
        {
            _hitNode = struck;
            _hitLocal = struck.ToLocal(at);
            // The model's own: the hole, its star, spall, puff and smoke. The
            // sprites' entry cloud and glow card stay the 2D tanks'.
            if (_celHit is not null && struck is MeshInstance3D part)
            {
                _celHit.Pierce(part, at, n);
                return;
            }
            _hitLoop.Strike(Sides[side], 0.0f, 0.0f, 1.0f, true);
            FxEntry(at, n, foot);
            return;
        }
        Vector3 r = uw - 2.0f * uw.Dot(n) * n;
        if (_celHit is not null)
        {
            _celHit.Ricochet(at, n, r);
            return;
        }
        ProcSpall spall = Next(_spalls, ref _nextSpall, Pool, () =>
        {
            var made = new ProcSpall();
            AddChild(made);
            made.Build(HexWidth, Squash, RiseFactor);
            return made;
        });
        spall.Might = 1.0f;
        spall.Blame(ProcSpall.Cause.Round);
        spall.Might *= Might;
        spall.Order = Stage3D.StandOrder;
        spall.Sit(Spot(foot), LiftAt(foot), Squash, RiseFactor, behind);
        Vector3 flat = new(r.X, 0.0f, r.Z);
        spall.Aim(flat.LengthSquared() < 1e-4f ? Vehicle.Spent : Along(flat), Drawn(at - foot));
        spall.Fire();
    }

    /// <summary>
    /// The outside of a penetration: a puff of earth-tan dust with a warm core
    /// on the plate, blown out along its normal.
    ///
    /// <b>A stand-in for the rendered pair, and named as one.</b> On a sprite
    /// that is <c>burst</c> and <c>dust</c> off the atlas - pictures rendered
    /// with each tank's set, which is exactly what a 3D tank does not load.
    /// What they draw is <see cref="ProcSlam"/>'s description of them, "a puff
    /// of earth-tan dust with a warm core, which is a shell going in", and the
    /// board has that cloud already: <see cref="ProcKick"/> wearing the
    /// ground's earth with the muzzle's glowing core left in. The inside of
    /// the same hit is <see cref="ProcPierce"/>, on the glow card.
    /// </summary>
    private void FxEntry(Vector3 at, Vector3 n, Vector3 foot)
    {
        ProcKick burst = Next(_bursts, ref _nextBurst, Pool, () =>
        {
            var made = new ProcKick();
            AddChild(made);
            made.Build(HexWidth, Squash, RiseFactor);
            // Worn once and never again: this ring is only ever this cloud.
            made.Dress(ProcKick.Cloud.Ground);
            made.Dial(ProcKick.Part.Glow, "core_gain", ProcKick.CoreGain);
            made.Dial(ProcKick.Part.Glow, "ember_gain", ProcKick.EmberGain);
            return made;
        });
        burst.Might = 1.0f;
        burst.Might *= Might * EntryShare;
        burst.Order = Stage3D.StandOrder;
        burst.Sit(Spot(foot), LiftAt(foot), Squash, RiseFactor);
        Vector3 flat = new(n.X, 0.0f, n.Z);
        burst.Aim(flat.LengthSquared() < 1e-4f ? Vehicle.Spent : Along(flat), Drawn(at - foot));
        burst.Fire();
    }

    /// <summary>How big the entry's cloud is against a gun's own - a round
    /// going in throws less than a charge going off.</summary>
    private const float EntryShare = 0.6f;

    /// <summary>An HE round bursting on a plate, from the side named -
    /// <see cref="CelBlast"/> on the model, <see cref="ProcSlam"/> on armour
    /// with --fx2d.</summary>
    private void FxHe(int side, Vector3 travel)
    {
        (Vector3 local, Vector3 normalLocal) = Strike(travel);
        Node3D hull = _model.Hull;
        Vector3 at = hull.ToGlobal(local);
        Vector3 n = (hull.GlobalBasis * normalLocal).Normalized();
        if (_celHit?.Aim((hull.GlobalBasis * travel).Normalized()) is { } aimed)
        {
            (at, n) = (aimed.At, aimed.N);
            _celHit.Leave(aimed.Part, at, n, aimed.Way, CelHit.Kind.Splash);
        }
        Vector3 foot = Foot(at);
        if (_celBlast is not null)
        {
            _celBlast.Burst(at, n, foot);
            return;
        }
        ProcSlam slam = Next(_slams, ref _nextSlam, Pool, () =>
        {
            var made = new ProcSlam();
            AddChild(made);
            made.Build(HexWidth, Squash, RiseFactor);
            return made;
        });
        slam.Might = 1.0f;
        slam.Might *= Might;
        slam.Face = ProcSlam.Surface.Armour;
        slam.Order = Stage3D.StandOrder;
        slam.Lit = Stage3D.StandOrder;
        slam.Sit(Spot(foot), LiftAt(foot), Squash, RiseFactor, n.Z <= 0.0f);
        slam.Aim(Along(new Vector3(n.X, 0.0f, n.Z)), Drawn(at - foot));
        slam.Fire();
    }

    /// <summary>A round landing in the ground beside the tank: the board's
    /// <see cref="Stage3D.Boom"/>, burst and crater.</summary>
    private void FxGround()
    {
        Vector3 spot = Foot(_rig.Position + new Vector3(0.55f, 0.0f, 0.45f) * HexWidth);
        float lift = LiftAt(spot);
        SheetBlast blast = Next(_booms, ref _nextBoom, Pool, () =>
        {
            var made = new SheetBlast();
            AddChild(made);
            made.Build(HexWidth, Squash, RiseFactor);
            return made;
        });
        blast.Might = 1.0f;
        blast.Might *= Might;
        blast.Sit(Spot(spot), lift, Squash, RiseFactor);
        blast.Fire();
        var pits = new Craters();
        PitArt pit = Next(_pits, ref _nextPit, Pool, () =>
        {
            var made = new PitArt();
            AddChild(made);
            made.Build(Squash, RiseFactor);
            return made;
        });
        Vector2 at = Spot(spot);
        pit.Show(at, lift, pits.Wide * Might * HexWidth, pits.Ink,
                 Mathf.Abs(at.X * 0.37f + at.Y * 0.71f) % 64.0f, Squash, RiseFactor);
        _shake.Blast(_profile.ShotShake * 0.6);
    }

    /// <summary>A fireball on the tank - the death, or (ungrounded and small)
    /// the knock-out flash: <see cref="Stage3D.Detonate"/> and
    /// <see cref="Stage3D.Flash"/>.</summary>
    private void Fireball(float might, bool grounded)
    {
        // The model's own, out of its ring - see CelDeath.
        if (_celDeath is not null)
        {
            Vector3 at = _model.Tank.ToGlobal(_model.BlastAt);
            _celDeath.Blow(at, Foot(at), might, grounded);
            return;
        }
        Vector3 foot = Foot(_rig.Position);
        ProcBall ball = Next(_balls, ref _nextBall, Pool, () =>
        {
            var made = new ProcBall();
            AddChild(made);
            made.Build(HexWidth, Squash, RiseFactor);
            return made;
        });
        ball.Might = 1.0f;
        ball.Grounded = grounded;
        ball.Might *= might;
        ball.Sit(Spot(foot), LiftAt(foot), Squash, RiseFactor);
        // How tall the tank stands on screen, the stage's HeightSpanPx - the
        // model's own height, drawn.
        float tall = _model.Size.Y * _model.PixelsPerUnit * RiseFactor;
        ball.Aim(new Vector2(0.0f, tall) / Mathf.Max(HexWidth, 1.0f));
        ball.Fire();
        // In front of the whole tank, as the board draws it over the sprite.
        StandAt(ball, _rig.Position.Z + Reach);
    }

    private void FxKnocked()
    {
        // The knock-out's own penetration has set where it went in already
        // when the model takes rounds anywhere (CelHit).
        if (_celHit is null)
        {
            _hitLocal = Strike(new Vector3(0, 0, -1)).At;
            _hitNode = null;
        }
        // In deep water the engine stops and the tank drowns rather than being
        // knocked out: no flash out of a deck that is going under, the turret
        // left on its ring (TankTick.Drowning; the round's own entry is CelHit's).
        bool deep = DeepHere;
        _wreck.Disable(seated: deep);
        if (deep)
        {
            _burning = false;
            return;
        }
        Fireball(TankTick.KnockOutFlash, grounded: false);
    }

    private void FxDestroyed(Vector3 blast)
    {
        if (!_wreck.Out)
            _wreck.Disable();
        _wreck.Kill(racked: true);
        // In the pond what comes up is the water it was in, not fire and earth -
        // Stage3D.Detonate's first question, and its plume at Drowned; the wreck
        // does not burn, the water has it.
        if (DeepHere && _stage is not null && _field is not null)
        {
            float top = _field.WaterTop(CellHere);
            _stage.Splash(Board(_rig.Position) - new Vector2(0.0f, top), top, Stage3D.Drowned);
            _burning = false;
        }
        else
        {
            _burning = true;
            Fireball(1.0f, grounded: true);
        }
        // TankTick.Quake(Death): the class's own gun shake, harder.
        _shake.Fire(new Vector2(0.0f, -1.0f), _profile.ShotShake * 2.15);
        _shake.Blast(_profile.ShotShake * 1.6);
    }

    /// <summary>The turret hit the deck: the board's turret quake.</summary>
    private void FxLanded() => _shake.Fire(new Vector2(0.0f, 1.0f), _profile.ShotShake * 0.45);

    private void FxBurn(bool on) => _burning = on;

    private void FxReset()
    {
        _wreck.Reset();
        _burn.Reset();
        _exhaust.Reset();
        _hitLoop.Reset();
        _shake.Reset();
        _burning = false;
        _shotFrame = -1;
        _celBurn?.Reset();
        _celExhaust?.Reset();
        _celShot?.Reset();
        _celHit?.Reset();
        _celBlast?.Reset();
        _celDeath?.Reset();
        _celDust?.Reset();
        _ruts?.Clear();
        _splash?.Reset();
        _wake?.Clear();
        _ripples.Settle();
        _beltWas.Clear();
        _hitNode = null;
        foreach (var (mat, albedo) in _paint)
            Repaint(mat, albedo);
        foreach (ProcKick k in _kicks) k.Douse();
        foreach (ProcKick k in _drifts) k.Douse();
        foreach (ProcKick k in _bursts) k.Douse();
        foreach (ProcSpall s in _spalls) s.Douse();
        foreach (ProcSlam s in _slams) s.Douse();
        foreach (ProcBall b in _balls) b.Douse();
        foreach (PitArt p in _pits) p.Hide();
    }

    // --- the frame ----------------------------------------------------------

    private float _dustSpent;
    private int _dustSide, _dustLaid;

    /// <summary>The tanks on the stage as boxes, this frame: what the exhaust,
    /// the dust, the shot's smoke and the hits' puffs thin away in
    /// (<see cref="CelSolids"/>). The target's pose is the last frame's - it
    /// is posed after the effects (RamFrame), a frame nobody sees.</summary>
    private readonly CelSolids _solids = new();

    private void FxProcess(float dt, float speed, float accel)
    {
        _shake.Update(dt);
        _solids.Clear();
        _solids.Take(_model);
        if (_other is not null)
            _solids.Take(_other.Model);
        foreach (ProcKick k in _kicks) k.Tick(dt);
        foreach (ProcKick k in _drifts) k.Tick(dt);
        foreach (ProcKick k in _bursts) k.Tick(dt);
        foreach (ProcSpall sp in _spalls) sp.Tick(dt);
        foreach (ProcSlam sl in _slams) sl.Tick(dt);
        foreach (ProcBall b in _balls) b.Tick(dt);
        foreach (SheetBlast b in _booms) b.Tick(dt);
        _splash?.Tick(dt, _camera.GlobalBasis);
        if (_sprite is null || _shape is null)
            return;

        TankSprite s = _sprite;
        double hull = TankSprite.Mod(_heading - 90.0, 360.0);
        s.HullFacing = hull;
        s.TurretFacing = TankSprite.Mod(hull + Mathf.RadToDeg(_model.Yaw), 360.0);
        // The model's gun lays smoothly and the shape hands the bore as it
        // stands, so there is no ladder to pick a rung off.
        s.BarrelRung = 0;

        // TankTick.UpdateExhaust
        if (_wreck.Out)
        {
            if (s.ExhaustPhase >= 0)
            {
                _exhaust.Reset();
                s.ExhaustPhase = -1;
            }
        }
        else
        {
            _exhaust.Advance(Mathf.Abs(speed), dt);
            s.ExhaustPhase = _exhaust.Frame;
            s.ExhaustDensity = (float)_exhaust.Density;
            s.ExhaustCycle = (float)(_exhaust.Phase / Math.Max(_exhaust.Phases, 1));
        }

        // TankTick.UpdateWreck, then UpdateBurn
        if (_wreck.Out)
        {
            _wreck.Update(dt);
            s.FireDensity = (float)(_burning ? _wreck.Blaze : _wreck.Flare);
            s.SmokeDensity = (float)(_wreck.Dead || _burning ? _wreck.Smoke : _wreck.Smoulder);
            Char((float)_wreck.Char);
        }
        // Nothing flares or smokes off a deck under water - TankTick's own two
        // exceptions for a drowned hull, asked of the cell.
        bool lit = (_burning || (_wreck.Flare > 0.0 && !Drowning)) && _shape.HasPorts;
        bool smoulder = !lit && _wreck.Disabled && _shape.HasPorts && !DeepHere;
        if (!lit && !smoulder)
        {
            if (s.Burning || s.Smouldering || s.FirePhase >= 0 || s.BurnPhase >= 0)
            {
                _burn.Reset();
                s.Burning = false;
                s.Smouldering = false;
                s.FirePhase = -1;
                s.BurnPhase = -1;
                s.FireCycle = 0.0f;
                s.SmokeCycle = 0.0f;
            }
        }
        else
        {
            _burn.Advance(dt);
            s.Burning = lit;
            s.Smouldering = smoulder;
            if (s.Column is ProcSmoke column)
                column.Ink = _burning ? ProcSmoke.ColumnInk : ProcSmoke.SmoulderInk;
            if (!_wreck.Out)
            {
                s.FireDensity = 1.0f;
                s.SmokeDensity = 1.0f;
            }
            s.FirePhase = _burn.FireFrame;
            s.BurnPhase = _burn.SmokeFrame;
            s.FireCycle = (float)(_burn.FirePhase / Math.Max(_burn.Phases, 1));
            s.SmokeCycle = (float)(_burn.SmokePhase / Math.Max(_burn.Phases, 1));
        }
        if (_celBurn is not null)
        {
            // What the sprite's fire and column would draw, handed to the model's.
            _celBurn.Fire = lit ? s.FireDensity : 0.0f;
            _celBurn.Smoke = lit || smoulder ? s.SmokeDensity : 0.0f;
            _celBurn.Smoulder = smoulder;
            _ports.Clear();
            foreach (Node3D ex in _model.Exhausts)
                _ports.Add(ex.GlobalPosition);
            // The turret hides a grille's fire or not, whole, by which of the
            // two is nearer the eye; a thrown turret lies on the deck over the
            // grilles and the fire is drawn over it, as the board draws the
            // sprites'.
            _celBurn.OverTurret = _model.Turret is not null && _model.TurretOverride is not null;
            _celBurn.TurretAt = _model.Turret?.GlobalPosition;
            _celBurn.Tick(dt, _ports, _camera.GlobalBasis);
            if (s.Plume is not null)
                s.Plume.Visible = false;
        }
        if (_celExhaust is not null)
        {
            // The engine runs until the tank is out; while it burns the fire's
            // column is the smoke, and the exhaust would stand in the flame.
            _celExhaust.Running = !_wreck.Out && (_celBurn is null || _celBurn.Heat <= 0.001f);
            // Pulling, not braking: off the throttle the engine idles.
            _celExhaust.Working = _exhaust.Response(Mathf.Abs(speed)) > 0.5 && accel * speed >= 0.0f;
            _exhaustPorts.Clear();
            foreach (Node3D ex in _model.Exhausts)
                _exhaustPorts.Add((ex.GlobalPosition, ex.GlobalBasis.Y.Normalized()));
            Vector3 ahead = _rig.GlobalBasis.Z;
            _celExhaust.Astern = -new Vector3(ahead.X, 0.0f, ahead.Z).Normalized();
            _celExhaust.Tick(dt, _exhaustPorts, _camera.GlobalBasis);
        }
        _celShot?.Tick(dt, _camera.GlobalBasis);
        _celHit?.Tick(dt, _camera.GlobalBasis);
        _celBlast?.Tick(dt, _camera.GlobalBasis);
        _celDeath?.Tick(dt, _camera.GlobalBasis);

        // TankTick.UpdateShot - frames, as the board counts them.
        int frame = _shotFrame < 0 ? -1 : FlashSheet.FrameAt(_shotFrame);
        int phase = _shotFrame < 0 ? -1 : EffectLayer.PhaseAt(_shotFrame);
        if (_shotFrame >= 0)
        {
            _shotFrame++;
            if (frame < 0 && phase < 0)
                _shotFrame = -1;
        }
        s.FlashFrame = frame;
        s.ShotPhase = phase;

        // The anchor, the shape read off the model about it, and the height
        // map rendered for it.
        Vector3 anchor = CardAnchor();
        _shape.Update(anchor, s.HullFacing, s.TurretFacing);
        SyncHeights(anchor);
        Vector3 muzzle = _model.Muzzle.GlobalPosition;
        Vector3 struck = (_hitNode is not null && IsInstanceValid(_hitNode) ? _hitNode : _model.Hull).ToGlobal(_hitLocal);

        // TankTick.UpdateHit, with the plate point the model's rather than the
        // atlas's table - where the light through the hole is seated.
        int hitPhase = _hitLoop.Phase;
        if (hitPhase >= 0)
        {
            s.HitOffset = Drawn(struck - anchor);
            s.HitScale = _hitLoop.Scale;
            s.HitThrough = _hitLoop.Through;
        }
        s.HitFrame = _hitLoop.Elapsed;
        s.HitPhase = hitPhase;
        _hitLoop.Advance();

        // The ground under the tank, and the stage's clearance over it, as the
        // floor no card may go below - see Bend.
        float floor = _rig.Position.Y + Stage3D.Clear(Squash, RiseFactor).Y;
        foreach (Card c in new[] { _rear!, _front!, _glow! })
        {
            c.Quad.Position = anchor;
            ((ShaderMaterial)c.Quad.MaterialOverride).SetShaderParameter("ground", floor);
        }
        // The fire, the column and the plume in front of the whole tank, and the
        // height map to say which of their elements it stands in front of - see
        // BuildHeights. In front of a thrown turret too, which the map leaves
        // out: it lies over the port, and the board draws it under the fire.
        StandAt(_rear!.Quad, _rig.Position.Z + Reach + Margin);
        StandAt(_front!.Quad, muzzle.Z + Margin);
        // The leak is light round the turret ring, drawn on the sprite under
        // the turret: at the ring's own depth the turret's near half covers it
        // as the sprite's turret layer did. A casemate has no ring: the
        // fighting compartment it would leak from is the sidecar's blast point.
        Vector3 ring = _model.Turret?.GlobalPosition ?? _model.Tank.ToGlobal(_model.BlastAt);
        StandAt(_glow!.Quad, ring.Z + Margin);

        foreach (CanvasItem item in _painted)
            item.QueueRedraw();

        BeltRuns();
        Ruts(dt);
        Dust(dt, speed);
    }

    /// <summary>How far past the rig anything of the tank can stand, world px:
    /// half its box's diagonal, turret thrown or not - the sidecar's toss lands
    /// it on the deck (<c>overhang_ok</c>).</summary>
    private float Reach => _model.Size.Length() * 0.5f * _model.PixelsPerUnit;

    /// <summary>The wreck's char on the model: the albedo dimmed toward soot
    /// by the fraction the board's char shader burns the sprite.</summary>
    private void Char(float amount)
    {
        var soot = new Color(0.16f, 0.14f, 0.12f);
        foreach (var (mat, albedo) in _paint)
            Repaint(mat, albedo.Lerp(soot * albedo, Mathf.Clamp(amount, 0.0f, 1.0f) * 0.85f));
    }

    /// <summary><see cref="TrackDust.Lay"/> on the model's belts: a puff every
    /// <see cref="TrackDust.Step"/> px of travel, belts in turn, a share of the
    /// track's length astern, blown astern.</summary>
    private void Dust(float dt, float speed)
    {
        if (_celDust is not null)
        {
            CelDustTick(dt);
            return;
        }
        if (_wreck.Out || dt <= 0.0f)
            return;
        float moved = Mathf.Abs(speed) * dt;
        if (moved / dt < TrackDust.DriveAbove)
        {
            _dustSpent = 0.0f;
            return;
        }
        _dustSpent += moved;
        if (_dustSpent < TrackDust.Step)
            return;
        _dustSpent = Mathf.Min(_dustSpent - (float)TrackDust.Step, (float)TrackDust.Step);
        int side = _dustSide;
        _dustSide ^= 1;
        int laid = _dustLaid++;
        Vector3 forward = _rig.GlobalBasis.Z;
        Vector3 left = _rig.GlobalBasis.X;
        Vector3 astern = speed >= 0.0f ? -forward : forward;
        float arm = _model.Tracks.Count > 0
            ? Mathf.Abs(_model.Tracks[0].Node.Position.X) * _model.PixelsPerUnit : 40.0f;
        float length = _model.Size.Z * _model.PixelsPerUnit;
        float wander = (2.0f * Plumes.Hash01(laid, 21391) - 1.0f) * 12.0f;
        Vector3 at = _rig.Position + left * ((side == 0 ? arm : -arm) + wander)
                     + astern * length * (float)TrackDust.Trail;
        float share = Mathf.Clamp(Mathf.Abs(speed) / (float)_profile.TopSpeed, 0.0f, 1.0f);
        float might = TrackDust.Might * share * (float)_profile.Size
                      * (1.0f + TrackDust.Ripple * (2.0f * Plumes.Hash01(laid, 60167) - 1.0f));
        ProcKick kick = Next(_drifts, ref _nextDrift, DriftPool, () =>
        {
            // Stage3D.Drift's ring: lying, and shaped before Build.
            var made = new ProcKick { Lying = true, Reach = TrackDust.Carry, Swell = TrackDust.Born };
            made.Root = TrackDust.Bed;
            made.Tall = TrackDust.Bed * 2.0f;
            AddChild(made);
            made.Build(HexWidth, Squash, RiseFactor);
            made.Hasten(TrackDust.Hang);
            made.Settle(TrackDust.Creep);
            made.Dial(ProcKick.Part.Dust, "dust_ink", made.Dial(ProcKick.Part.Dust, "dust_ink") * TrackDust.Ink);
            return made;
        });
        kick.Might = 1.0f;
        kick.Might *= might;
        kick.Order = Stage3D.DressOrder;
        kick.Dress(ProcKick.Cloud.Ground);
        kick.Sit(Spot(at), LiftAt(at), Squash, RiseFactor);
        kick.Aim(Along(astern), Vector2.Zero);
        kick.Fire();
    }

    /// <summary>
    /// Each belt's run this frame, board px, off its own travel
    /// (<see cref="TankModel.Driven"/> less its share of
    /// <see cref="TankModel.Skid"/>, so a pivot runs the belts against each
    /// other while the hull stays put) - read by the dust and the ruts alike -
    /// and the hull's flat frame on the ground.
    /// </summary>
    private void BeltRuns()
    {
        float ppu = _model.PixelsPerUnit;
        float length = _model.Size.Z * ppu;
        Vector3 forward = _rig.GlobalBasis.Z;
        _hullAhead = new Vector3(forward.X, 0.0f, forward.Z).Normalized();
        _hullLeft = new Vector3(_hullAhead.Z, 0.0f, -_hullAhead.X);
        while (_beltWas.Count < _model.Tracks.Count)
            _beltWas.Add(float.NaN);
        _beltRun.Clear();
        for (int i = 0; i < _model.Tracks.Count; i++)
        {
            float driven = _model.Driven - _model.Tracks[i].Side * _model.Skid;
            float was = _beltWas[i];
            _beltWas[i] = driven;
            // Model units to board px; a jump (a reset, a new model) is no run.
            float run = float.IsNaN(was) ? 0.0f : (driven - was) * ppu;
            _beltRun.Add(Mathf.Abs(run) > length ? 0.0f : run);
        }
    }

    /// <summary>
    /// The ruts: a stitch per link of each belt's run at the middle of its
    /// footprint on the ground, the bar across the hull as the belt lay; in a
    /// ford too (seen through the water), none in deep water, and a tank that
    /// jumped starts a new run
    /// (<see cref="CelRuts"/>).
    /// </summary>
    private void Ruts(float dt)
    {
        if (_ruts is null)
            return;
        _rutBelts.Clear();
        for (int i = 0; i < _model.Tracks.Count; i++)
        {
            Vector3 off = _model.Tracks[i].Node.GlobalPosition - _rig.GlobalPosition;
            Vector3 at = Foot(_rig.GlobalPosition + _hullLeft * off.Dot(_hullLeft)
                              + _hullAhead * off.Dot(_hullAhead));
            // A ford takes the mark - the water's surface lies over it, a rung
            // up - and deep water does not: a hull afloat has no belt on the
            // bottom. The sprites' layer lifts the pen at any water.
            Vector2I cell = _field?.FlatCellAt(Board(at)) ?? Vector2I.Zero;
            bool marks = !(_field?.IsDeep(cell) ?? false);
            bool wet = _field?.IsWater(cell) ?? false;
            // In a ford the mark is drawn on the surface, not on the bed: a point
            // on the bed failed the depth test against it and drew nothing (only
            // the ramp's, whose floor stands at its own height, showed). Moved
            // up to the surface along the eye's ray (View), not straight up:
            // straight up is a step up the screen too, and the rut ran beside
            // the belts instead of under them (the user showed it). Along the
            // ray the screen does not move - StandAt's move - and only the depth
            // changes. Handed apart from the point, so the rut's length and
            // heading stay the ground's.
            Vector3 lift = Stage3D.Clear(Squash, RiseFactor);
            if (wet && _field is not null)
            {
                float rise = _field.WaterTop(cell) / RiseFactor - at.Y;
                if (rise > 0.0f)
                    lift += View * (rise / View.Y);
            }
            _rutBelts.Add(new CelRuts.Belt(at, lift, _hullLeft, _beltRun[i], marks, wet));
        }
        _ruts.Tick(dt, _rutBelts);
    }

    /// <summary>
    /// The belts' dust in the model's look: each belt's run this frame off its
    /// own travel (<see cref="TankModel.Driven"/> less its share of
    /// <see cref="TankModel.Skid"/>), so a pivot raises dust as
    /// <see cref="TrackDust"/> has it; born at the belt's trailing end, on the
    /// ground there, and none on wet ground.
    /// </summary>
    private void CelDustTick(float dt)
    {
        _belts.Clear();
        float length = _model.Size.Z * _model.PixelsPerUnit;
        Vector3 forward = _hullAhead, left = _hullLeft;
        // Belts running against each other churn the ground rather than roll
        // over it: TrackMarks.Scrub's 1.5 on a pivot. At a pivot's pace alone
        // the puffs were small separate lumps - stones, not dust.
        float churn = _beltRun.Count == 2 && _beltRun[0] * _beltRun[1] < 0.0f ? 1.5f : 1.0f;
        for (int i = 0; i < _model.Tracks.Count; i++)
        {
            TankModel.Track t = _model.Tracks[i];
            float run = _beltRun[i];
            float off = (t.Node.GlobalPosition - _rig.GlobalPosition).Dot(left);
            Vector3 outward = off >= 0.0f ? left : -left;
            Vector3 astern = run >= 0.0f ? -forward : forward;
            Vector3 at = Foot(_rig.GlobalPosition + left * off + astern * (length * 0.42f));
            float pace = dt > 0.0f ? Mathf.Abs(run) / dt : 0.0f;
            bool dusty = !_wreck.Out && pace >= TrackDust.DriveAbove
                         && !(_field?.IsWater(_field.FlatCellAt(Board(at))) ?? false);
            _belts.Add(new CelDust.Belt(at, astern, outward, dusty ? Mathf.Abs(run) : 0.0f,
                                        churn * pace / (float)_profile.TopSpeed));
        }
        _celDust!.Tick(dt, _belts, _camera.GlobalBasis);
    }

    /// <summary>The camera's shake, in world units at this zoom.</summary>
    private Vector2 ShakeOffset() => _shake.ScreenOffset(_zoom);
}
