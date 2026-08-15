using System.Collections.Generic;
using System.Globalization;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The board and the things standing on it, drawn by an orthographic 3D camera
/// so the depth buffer answers what hides what.
///
/// <b>This replaces a rule, not a picture.</b> The 2D board is correct today and
/// the proof it is correct is <see cref="ReliefCap"/> plus four members of
/// <see cref="HexField"/>, four of which arrived one reported picture at a time.
/// None of that exists here. A cell is a solid prism, a tank is an upright
/// billboard, and which of them the viewer sees at a pixel is a comparison the
/// hardware does - so there is no admission rule to get wrong, no per-observer
/// repaint to saturate a rim, and nothing new to plumb through when the next
/// thing gets drawn on the ground.
///
/// <b>Nothing measured in the project moves.</b> One world unit is one screen
/// pixel and the mapping is <c>world = (x, lift/cos(e), flat row/sin(e))</c>, so
/// the camera projects every point back to the pixel the 2D bench drew it at -
/// see <see cref="Relief3D"/>, where that is proven to 0.0002px against the same
/// formula. The grade, the lift, the levels, the anchors and the atlas are
/// untouched.
///
/// <b>The tank's own layers stay in 2D, and that is what keeps this small.</b>
/// Sixteen layers at one anchor with blend modes between them are sixteen
/// coplanar quads in a 3D scene, which is one depth and no order at all.
/// Composited into a render target first, <see cref="TankSprite"/> and every
/// clock under it carry on being exactly what they are, and the scene is handed
/// a picture.
///
/// <b>And the sprite keeps its own position.</b> Inside each render target sits
/// a holder offset by <c>centre - sprite.Position</c>, so <c>Sprite.Position</c>
/// still means what it meant in the harness's space and
/// <see cref="Vehicle.GroundPoint"/>, the ring, the depth and the thirteen call
/// sites that read it did not have to be told about any of this.
/// </summary>
public sealed partial class Stage3D : Node3D
{
    public HexField Field = null!;

    /// <summary>Where the harness's own nodes sit, because the tank's position
    /// is in that space and the board must be built in the same one.</summary>
    public Vector2 Origin;

    /// <summary>The 2D camera this mirrors. Mirrored rather than replaced so the
    /// two modes are the same picture: pan and zoom stay where the harness
    /// already keeps them, and <c>--3d</c> changes what draws, not what is
    /// looked at.</summary>
    public Camera2D Eye = null!;

    /// <summary>Which tank the ring is under, or null. Read rather than pushed,
    /// for <see cref="Vehicle.LastHullFacing"/>'s reason - the selection moves
    /// from three places.</summary>
    public Vehicle? Selected;

    /// <summary>
    /// Whether the board is drawn - the same question <see cref="HexField.ShowField"/>
    /// answers, asked of the board that is actually drawing.
    ///
    /// <b>It exists because the alternative is one click away, not one edit
    /// away.</b> While the stage owns the board the field's own flag does not
    /// hide anything: it puts a canvas item back over the entire 3D world, tanks
    /// and all. That is the trap <see cref="Main.FlagRows"/> was extended for,
    /// and the panel row and the G key reach it live.
    /// </summary>
    public bool ShowBoard
    {
        get => _tops.Visible;
        set
        {
            _tops.Visible = value;
            _sides.Visible = value;
            _pond.Visible = value;
            _edges.Visible = value && ShowEdges;
        }
    }

    /// <summary>
    /// Whether each cell wears an outline at its own rim.
    ///
    /// <b>It is drawn because <see cref="Bleed"/> exists to make sure it is
    /// not.</b> That constant stops a top face sampling the art's own
    /// antialiased surround, and the note on it records the result as the board
    /// reading "as continuous ground with no cell boundary visible at all,
    /// better than the 2D board manages" - which is right when the question is
    /// whether the ground looks like ground, and wrong when it is which cell a
    /// tank is standing in. Both are asked of this board, so the boundary is put
    /// back as a mark rather than taken back out of the seam.
    ///
    /// Kept apart from <see cref="ShowBoard"/> and answered by it: no board, no
    /// outline, because an outline of nothing is a wireframe of a board that is
    /// not there.
    /// </summary>
    public bool ShowEdges
    {
        get => _showEdges;
        set
        {
            _showEdges = value;
            _edges.Visible = value && ShowBoard;
        }
    }

    /// <summary>Margin round a sprite inside its own render target. Generous:
    /// the burn column is a 384px frame and the class scale takes it to 442, and
    /// a frame clipped by its target reads as a layer that stopped being
    /// rendered.</summary>
    public const int PaintSize = 512;

    private Camera3D _camera = null!;
    private MeshInstance3D _tops = null!;
    private MeshInstance3D _sides = null!;
    private MeshInstance3D _ring = null!;
    private MeshInstance3D _edges = null!;
    private MeshInstance3D _ruts = null!;
    private MeshInstance3D _pond = null!;

    /// <summary>The belt marks, redrawn as geometry lying on the board. Null
    /// leaves the ground bare, which is how the stage behaved while the ruts were
    /// still only a canvas item - see <see cref="BuildRuts"/>.</summary>
    public TrackMarks? Marks;

    /// <summary>Where the marks' own space sits in the space <see cref="World"/>
    /// reads, which is the one a tank's <see cref="Vehicle.GroundPoint"/> is in.
    /// Passed rather than worked out here, because only the harness holds both
    /// nodes - the same reason <see cref="ReliefCap"/> is handed its shift.</summary>
    public Vector2 MarksAt;

    /// <summary>The forest, redrawn as billboards standing on the board. Null
    /// leaves the ground bare, which is how the stage behaved while the trees
    /// were still only canvas items - see <see cref="BuildTrees"/>.</summary>
    public Grove? Wood;

    /// <summary>The drawn water surface. Null leaves the pond the flat colour
    /// it was before the art arrived, which is the A/B the toggle exists for -
    /// see <see cref="Pond"/>.</summary>
    public WaterArt? Surf;

    /// <summary>
    /// Where the pond's loop has got to, 0..1 over one turn of the four frames.
    ///
    /// <b>Integrated, not clock times rate</b>, for the reason
    /// <see cref="ExhaustLoop"/> gives: a rate that changes moves the whole wave,
    /// and the wave is the only thing here that a viewer can see. Nothing changes
    /// this rate today, and the day something does - a wind, a wake - is the day
    /// the other spelling would jump the pond by however long the board had been
    /// open.
    /// </summary>
    private float _swell;

    private bool _showEdges = true;
    private readonly Dictionary<Vehicle, Stand> _stands = new();
    private string _built = "";

    /// <summary>One tank's render target, the holder that keeps its position
    /// honest, and the quad that carries the result into the world.</summary>
    private sealed class Stand
    {
        public required SubViewport Paint;
        public required Node2D Holder;
        public required MeshInstance3D Quad;

        /// <summary>Where the sprite was taken from, so it can be put back.
        /// Remembered rather than assumed to be the harness: the one thing a
        /// live switch must not do is guess where somebody else's node
        /// lives.</summary>
        public required Node Home;

        /// <summary>The anchor-to-contact offset the quad was last cut for. Held
        /// because the size slider moves it, and a body cut for one scale bends
        /// at the wrong line at another.</summary>
        public Vector2 Lead = new(float.NaN, float.NaN);

        /// <summary>The climb split the quad was last cut for: how much lower
        /// the trailing side stands and the seam line in the quad's own frame.
        /// Held for Lead's reason - both move while a tank is on a wall, and a
        /// mesh cut for one frame of a climb carries the wrong depth a few
        /// frames later.</summary>
        public float Drop;
        public Vector3 Line;

        /// <summary>Whether the quad is currently wearing the material that
        /// ignores depth. Held so the swap happens on a change rather than every
        /// frame: a new material per frame is a new shader binding per frame.
        /// </summary>
        public bool Over;
    }

    // --- the mapping ---------------------------------------------------------

    private float Squash => Field.Squash;
    private float RiseFactor => Mathf.Max(Field.RiseFactor, 0.0001f);

    /// <summary>
    /// A point of the harness's 2D space, with its lift told apart from its row,
    /// as a point of the world.
    ///
    /// <b>Both arguments are screen px and the lift has to come in separately</b>,
    /// which is the one thing a caller can get wrong here. A drawn position
    /// already has the lift subtracted out of its row - <see cref="HexField.CellAnchor"/>
    /// lifts, which is what carries height into a tank's position for free - so
    /// the flat row is the drawn row plus the lift back on.
    /// </summary>
    public Vector3 World(Vector2 flat, float lift) =>
        World(flat, lift, Squash, RiseFactor);

    /// <summary>The same mapping given its two camera terms instead of reading
    /// them off the field, so the geometry built from it can stay static and be
    /// asserted without a board - the reason <see cref="Clear"/> and
    /// <see cref="Body"/> are shaped this way too.</summary>
    public static Vector3 World(Vector2 flat, float lift, float squash, float rise) =>
        new(flat.X, lift / Mathf.Max(rise, 0.0001f),
            flat.Y / Mathf.Max(squash, 0.0001f));

    public override void _Ready()
    {
        float height = GetViewport().GetVisibleRect().Size.Y;
        _camera = new Camera3D
        {
            Projection = Camera3D.ProjectionType.Orthogonal,
            KeepAspect = Camera3D.KeepAspectEnum.Height,
            Size = height,
            Near = 1.0f,
            Far = Back * 2.0f,
            RotationDegrees = new Vector3(-Mathf.RadToDeg(Mathf.Asin(Squash)),
                                          0.0f, 0.0f),
            Current = true,
        };
        AddChild(_camera);
        // The rim of a hexagon is geometry here, where in 2D it was the art's
        // own antialiased edge. Without this the board comes back stepped and it
        // reads as the art having got worse.
        GetViewport().Msaa3D = Viewport.Msaa.Msaa4X;

        _tops = new MeshInstance3D();
        _sides = new MeshInstance3D();
        _ring = new MeshInstance3D();
        _edges = new MeshInstance3D { Visible = _showEdges };
        // An ImmediateMesh rather than an ArrayMesh rebuilt through a SurfaceTool:
        // the trail grows, fades and is forgotten from the front, so this is the
        // one piece of the board's geometry that genuinely changes every frame,
        // and that is what the type is for. The 2D layer it replaces rebuilds the
        // same polylines every frame too, so the cost is not new.
        _ruts = new MeshInstance3D { Mesh = new ImmediateMesh() };
        _pond = new MeshInstance3D();
        AddChild(_tops);
        AddChild(_sides);
        AddChild(_ruts);
        AddChild(_ring);
        AddChild(_pond);
        AddChild(_edges);
        _ruts.MaterialOverride = Decal(RutOrder);
        _pond.MaterialOverride = Decal(WaterOrder);
    }

    /// <summary>
    /// How far the camera stands back along its own view direction.
    ///
    /// An orthographic projection does not care where the camera is - the
    /// picture is the same at any distance - so this looks like a free number
    /// and is not: it sets the near and far planes, and <b>an orthographic depth
    /// buffer is linear</b>, so every unit of range between them is precision
    /// spent. At 4000 back and 8000 of range a ground mark lifted less than a
    /// pixel was inside the noise and did not draw at all.
    ///
    /// 1500 clears the board with room for a pan - the whole scene is about 1200
    /// units deep - and leaves the range at 3000, which is where the ring holds.
    /// </summary>
    private const float Back = 1500.0f;

    public override void _Process(double delta)
    {
        // The bench's step when it has pinned one. This node is a sibling of
        // Main, not a child of its process, so a pin kept local over there left
        // the pond running on the wall clock through every capture ever taken -
        // see Main.FixedStep for what that cost.
        delta = Main.FixedStep ?? delta;
        if (Field.Atlas is null)
            return;
        Aim();
        // Rebuilt only when what a cell wears changes, which is a route being
        // given or a lane going up. Fifty-four prisms is nothing to build; sixty
        // times a second for a board that has not changed is still nothing worth
        // spending.
        string want = Signature();
        if (want != _built)
        {
            Build();
            _built = want;
        }
        // Outside the rebuild gate on purpose: the swell moves every frame and
        // the board does not, which is exactly the split the gate is there to
        // make. Wrapped rather than left to grow, so a bench left open all
        // afternoon keeps the same precision in the fraction as one just started.
        if (SwellPeriod > 0.0f)
            _swell = Mathf.PosMod(_swell + (float)delta / SwellPeriod, 1.0f);
        _swellInk?.SetShaderParameter("phase", _swell);
        _swellInk?.SetShaderParameter("blend", SwellBlend ? 1.0f : 0.0f);
        _swellInk?.SetShaderParameter("foam", Mathf.Max(0.0f, Foam));
        // Wrapped at a turn for the swell phase's reason: a bench left open all
        // afternoon keeps the same precision in it as one just started. A turn
        // is the true period here - the drift is a sine of this angle.
        _foamClock = Mathf.PosMod(_foamClock + (float)delta * FoamDrift,
                                  Mathf.Tau);
        _swellInk?.SetShaderParameter("foam_time", _foamClock);
        // The computed pond's own clock, on the bench's step for the same
        // reason. Not wrapped at a turn - the surface is a drifting noise field
        // rather than a sine, so it has no period to wrap at; kept small by the
        // hour instead, which is all the precision it needs.
        _deepClock = Mathf.PosMod(_deepClock + (float)delta, 3600.0f);
        _deepInk?.SetShaderParameter("time", _deepClock);
        _deepInk?.SetShaderParameter("foam", Mathf.Max(0.0f, Foam));
        PushSwell();
    }

    private void Aim()
    {
        float zoom = Mathf.Max(Eye.Zoom.X, 0.0001f);
        _camera.Size = GetViewport().GetVisibleRect().Size.Y / zoom;
        Vector3 pivot = World(Eye.Position, 0.0f);
        _camera.Position = pivot
                           + new Vector3(0.0f, Back * Squash, Back * RiseFactor);
    }

    // --- the tanks -----------------------------------------------------------

    /// <summary>
    /// Hand a tank to the stage: its sprite moves into a render target and a
    /// quad appears in the world carrying it.
    ///
    /// Called once, by the harness, while it is building its vehicles - so the
    /// sprite is reparented before anything has read it and the flag decides the
    /// tree rather than a switch inside every frame.
    /// </summary>
    public void Take(Vehicle vehicle)
    {
        var paint = new SubViewport
        {
            Size = new Vector2I(PaintSize, PaintSize),
            TransparentBg = true,
            Disable3D = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
            RenderTargetClearMode = SubViewport.ClearMode.Always,
        };
        AddChild(paint);
        var holder = new Node2D();
        paint.AddChild(holder);
        Node home = vehicle.Sprite.GetParent()
                    ?? throw new System.InvalidOperationException(
                        "the sprite has no parent to be given back to");
        home.RemoveChild(vehicle.Sprite);
        holder.AddChild(vehicle.Sprite);

        var quad = new MeshInstance3D
        {
            // Sorted by where the node is rather than by the middle of its own
            // box, because the node is the contact point and that is the rule the
            // 2D bench already sorts three tanks by. By the box it would be the
            // middle of the footprint, which is longer on some headings than on
            // others, and two tanks abreast would swap on a turn.
            SortingUseAabbCenter = false,
            MaterialOverride = Paint(paint.GetTexture(), false),
        };
        AddChild(quad);
        _stands[vehicle] = new Stand
        {
            Paint = paint, Holder = holder, Quad = quad, Home = home,
        };
    }

    /// <summary>
    /// What a tank's picture is drawn with, and the blend mode is the whole
    /// reason it is a shader rather than <see cref="StandardMaterial3D"/>.
    ///
    /// <b>A render target holds premultiplied colour, and the additive layers are
    /// what makes that matter.</b> Drawing into a transparent target with ordinary
    /// alpha leaves <c>rgb*a</c> in it - that is what the blend equation puts
    /// there - so displaying it with straight alpha blending multiplies by the
    /// alpha a second time. On the body that is a dark fringe round the
    /// silhouette; on the flame it is the effect disappearing, because an additive
    /// layer adds rgb to a target whose alpha stays near nought and the quad then
    /// scales that rgb by almost nought. Measured against the 2D bench, the same
    /// burning tank: the stage took 9 levels of red out of the picture where the
    /// canvas took 1, and the flame read visibly duller.
    ///
    /// <c>blend_premul_alpha</c> is <c>rgb + dst*(1-a)</c>, which is exactly what
    /// the target holds: the ordinary layers composite as they always did and the
    /// additive ones land on the ground behind the tank, which is where they were
    /// always meant to land. <b>And it keeps every ordering inside the sprite</b> -
    /// the per-heading turret order, the holdouts, the burst that goes behind the
    /// hull - which the obvious answer, a second render target for the additive
    /// layers, would have thrown away for the same result.
    ///
    /// Unshaded because the sprite is already lit - it was rendered with its own
    /// three-point rig - nearest because at 1:1 a resampled atlas is the one thing
    /// that would make these look worse than the 2D bench draws them, and no depth
    /// written because a tank that wrote depth would settle its own transparent
    /// fringe against the ground.
    /// </summary>
    /// <remarks>Internal so the check can read the render mode: this shader and
    /// <see cref="EffectLayer.Glow"/> are one statement in two places, and either
    /// of them back on straight alpha kills the flame.</remarks>
    /// <remarks>
    /// <b>The waterline is a cut this shader has to make, and the geometry cannot
    /// make it.</b> A horizontal surface crossing an upright billboard has half of
    /// itself in front of the card and half behind, and a transparent surface has
    /// one sort key for the whole of it - so the pond drawn over the tank tints
    /// the dry hull as well, and drawn under it tints nothing. The depth buffer
    /// would answer honestly and cannot be asked: a tank writes no depth, which is
    /// what keeps its silhouette smooth, and water that wrote depth would cut the
    /// submerged half away rather than tint it. A tank with its running gear
    /// missing reads as a bug; a tank seen dimly through water reads as a ford.
    ///
    /// <b>And the cut is not one row, which is the second half and cost a
    /// picture.</b> A sprite pixel's screen row is <c>height*cos(e) +
    /// depth*sin(e)</c>, and a billboard knows only the sum, so the far end of the
    /// hull is drawn 93px higher than the near one on MTP without being higher at
    /// all - against 36px of water. Cut at one row, the tail stands dry in the
    /// pond the nose is under, which is what the first pond looked like. So the
    /// threshold is per column, taken off
    /// <see cref="AtlasSet.Groundline"/>: the visible surface at a column is the
    /// near one, and where that meets the ground is the bottom of the silhouette
    /// there. Where the table has no answer - the gun swung out past the tracks -
    /// it falls back to the flat cut, which is far below the tube.
    ///
    /// The colour is the pond's own, handed in rather than written here, because
    /// the plane and the tint are one statement and two copies of a colour part
    /// company. <c>c.rgb</c> is premultiplied, so mixing toward
    /// <c>pond.rgb * c.a</c> is the same blend the plane would have done.
    ///
    /// What it does not carry is the shear: pitch, roll and tremble move the drawn
    /// sprite and not this mapping, so the line is off by however far the body is
    /// leaning - a couple of pixels at the amplitudes in use.
    /// </remarks>
    internal const string PaintShader = @"
shader_type spatial;
render_mode unshaded, blend_premul_alpha, depth_draw_never, cull_disabled{0};
uniform sampler2D picture : source_color, filter_nearest;
uniform sampler2D shore : filter_nearest, repeat_disable;
uniform vec4 pond = vec4(0.0);
// anchor.x, anchor.y, class scale, the render target's size
uniform vec4 shore_map = vec4(0.0);
// water over the sprite's own ground in board px, this heading's row of the
// table, the tile row the ground sits on, and the tile's last row
uniform vec4 shore_cut = vec4(-1.0);
void fragment() {{
    vec4 c = texture(picture, UV);
    ALPHA = c.a;
    ALBEDO = c.rgb;
    if (shore_cut.x >= 0.0) {{
        float scale = max(shore_map.z, 0.0001);
        vec2 tile = (UV * shore_map.w - shore_map.w * 0.5) / scale + shore_map.xy;
        float bottom = shore_cut.z;
        if (shore_cut.y >= 0.0) {{
            vec4 s = texture(shore, vec2((tile.x + 0.5)
                                         / float(textureSize(shore, 0).x),
                                         shore_cut.y));
            if (s.a > 0.5)
                bottom = s.r * shore_cut.w;
        }}
        if (tile.y > bottom - shore_cut.x / scale)
            ALBEDO = mix(c.rgb, pond.rgb * c.a, pond.a);
    }}
}}
";

    /// <summary>The same shader with the depth test off, for the legs where the
    /// tank is drawn over the ground - see <see cref="Place"/>. Two shaders rather
    /// than a uniform because the test is a render mode: it is compiled in, not
    /// set.</summary>
    private static readonly Shader Tested = new()
    {
        Code = string.Format(PaintShader, ""),
    };

    private static readonly Shader Free = new()
    {
        Code = string.Format(PaintShader, ", depth_test_disabled"),
    };

    private static ShaderMaterial Paint(Texture2D picture, bool overGround)
    {
        var ink = new ShaderMaterial { Shader = overGround ? Free : Tested };
        ink.SetShaderParameter("picture", picture);
        // Nothing about the water is set here, and the tint used to be: it was
        // one constant and looked like it never changed. It does now - the art
        // carries its own average, and the toggle swaps between that and the flat
        // colour - so it goes where the waterline already goes, in Place, every
        // frame. Set here instead, a tank that crossed a slope mid-ford would come
        // back from the material swap wearing whichever water was current when it
        // was built.
        return ink;
    }

    /// <summary>
    /// Hand every tank back to where it came from and drop what carried it.
    ///
    /// <b>The sprite's own position survives the round trip untouched</b>, which
    /// is the holder's whole reason for being: nothing inside the render target
    /// ever wrote <c>Sprite.Position</c>, so putting it back is a reparent and
    /// not a restore. Called by the panel row, and by the harness on the way
    /// down - a render target holding a live sprite is a sprite that leaks.
    /// </summary>
    /// <summary>The meshes and their materials are made here and referenced from
    /// C#, and a run that came down with the panel attached reported leaked
    /// instances at exit while the same run without it did not - the difference
    /// being the ring, which is only built when there is a selection to mark.
    /// Dropped on the way out rather than left to the engine's teardown.</summary>
    public override void _ExitTree()
    {
        _tops.Mesh = null;
        _sides.Mesh = null;
        _ring.Mesh = null;
        _edges.Mesh = null;
        _ruts.Mesh = null;
        _pond.Mesh = null;
        _tops.MaterialOverride = null;
        _sides.MaterialOverride = null;
        _ring.MaterialOverride = null;
        _edges.MaterialOverride = null;
        _ruts.MaterialOverride = null;
        _pond.MaterialOverride = null;
        // The wood is 464 meshes and 464 materials made here, which is by some way
        // the largest thing this node owns.
        Fell();
    }

    public void Give()
    {
        foreach ((Vehicle vehicle, Stand stand) in _stands)
        {
            stand.Holder.RemoveChild(vehicle.Sprite);
            stand.Home.AddChild(vehicle.Sprite);
            stand.Quad.QueueFree();
            stand.Paint.QueueFree();
        }
        _stands.Clear();
    }

    /// <summary>
    /// How far anything lying on the ground is nudged clear of it, and then slid
    /// back along the view direction so the nudge costs no screen pixels.
    ///
    /// Coplanar with the top face is <c>hit_scar</c>'s coin toss: which of the
    /// two surfaces the depth test keeps is undecided, and it comes back as a
    /// mark eaten in patches that move with the heading.
    ///
    /// <b>The number that matters is depth, not height, and it is smaller than
    /// the height by <c>sin(e)</c>.</b> At 0.9 units up - the sub-pixel a decal
    /// wants - the ring did not appear at all: 0.9 up is only 0.46 nearer, and
    /// against a depth range of 8000 that was a handful of steps of the buffer.
    /// At 6 it appeared whole, which is what named the cause. Two units up and
    /// <c>2*cot(e)</c> toward the camera leaves the picture exactly where it was
    /// and puts the mark four units of depth in front of the cell it lies on,
    /// which is what a decal wants and all it wants; it holds now that
    /// <see cref="Back"/> is not throwing precision away.
    ///
    /// <b>World units, not a fraction of a level.</b> The ring was lifted by a
    /// fraction of <see cref="HexField.Lift"/>, which is nothing at all when the
    /// grade is zero - and the grade slider goes there. Measured: at grade 0 the
    /// stage kept six pixels of a ring the 2D board drew 1030 of, the rest lost
    /// to the coin toss. A clearance is a clearance whatever the board is doing.
    /// </summary>
    private const float Foot = 2.0f;

    /// <summary>That nudge as an offset: up, and back along the view direction by
    /// as much as the lift moved the picture. Given the two camera terms rather
    /// than read off the field so <see cref="Body"/> can stay static.</summary>
    private static Vector3 Clear(float squash, float rise) =>
        new(0.0f, Foot, Foot * rise / squash);

    /// <summary>
    /// One tank's surface: upright above where it touches the ground, flat along
    /// the ground below, hinged at the contact line. Origin at the contact point,
    /// <paramref name="lead"/> being the screen offset from there up to the
    /// sprite's own anchor.
    ///
    /// <b>The bend is the whole point, and a plain upright quad was cutting every
    /// tank's running gear off.</b> Under this camera "below the ground plane" and
    /// "behind the ground" are the same test - the depth a fragment loses by is
    /// <c>y/sin(e)</c> and nothing else in the expression survives - so every
    /// sprite pixel below the contact point was buried by the cell the tank was
    /// standing on. Moving the quad about cannot help: shifting it in z moves the
    /// ground it covers by the same amount and the terms cancel exactly.
    ///
    /// And those pixels are not below the ground, they are <b>in front of</b> it:
    /// the near track, the near skirt and the contact shadow, all of them lying on
    /// the ground nearer the camera than the point the tank is standing on. Flat
    /// is where they belong, and it is the same surface the ring and the ruts use.
    /// So the sprite is a body standing on a footprint, which is what a tank is,
    /// and the contact line is where one becomes the other.
    ///
    /// <b>On a wall the surface splits in two along the seam line too</b>,
    /// because a climbing hull has its nose and its tail at two different
    /// heights and one depth for the whole sprite answers only one of them -
    /// promoted whole to the crown, the tank spent all of (3,2)->(4,3) drawn
    /// over (3,3), a hex it had not come up to. <paramref name="drop"/> is how
    /// much lower the trailing side stands and <paramref name="line"/> is
    /// <c>Main.SeamLine</c> in the quad's own screen frame, and the trailing
    /// side slides along the view ray by the whole of the drop: up by
    /// <c>d/cos(e)</c> and toward the camera by <c>d/sin(e)</c>, the family
    /// <c>World(row + L, L)</c> in the quad's own space, so not one pixel moves
    /// and only the depth does. Zero drop is the old pair of faces exactly,
    /// which is every parked tank and every flat board.
    ///
    /// <b>The cut follows the hexes' shared edge, and both simpler cuts were
    /// built first and looked broken.</b> A ramp across the hull put columns at
    /// heights between the two, and those are cut by the crown cells' top
    /// faces along a line that is the rim of nothing drawn - measured on
    /// (3,2)->(4,3), where it came back as a diagonal wedge out of the hull
    /// with the mounted hex's ground inside it. A vertical step could not fix
    /// it, at the contact point or anywhere else: two neighbouring hexes
    /// overlap by half a width in x, so no vertical line has the mounted cell
    /// wholly on one side, and whichever part of it crossed over bit a corner
    /// of ground out of the tank riding onto it. The shared edge is the one
    /// line that separates them - a convex hex lies entirely on its own side
    /// of each of its edges - so the mounted hex never covers the tank
    /// mounting it, the passed ground covers the trailing side along the rim
    /// it actually draws, and the cover runs out exactly where that rim does.
    /// Both faces are linear in screen space over their own parameters, so the
    /// line is a half-plane in either's frame and the split is two convex
    /// polygons per face.
    /// </summary>
    private static ArrayMesh Body(Vector2 lead, float squash, float rise,
                                  float drop, Vector3 line)
    {
        float half = PaintSize * 0.5f;
        // The anchor in the contact point's frame, and the image row the two
        // halves are hinged on.
        float ax = -lead.X, ay = lead.Y / rise;
        float seam = (half + lead.Y) / PaintSize;
        Vector3 lie = Clear(squash, rise);
        // A screen pixel below the contact is 1/sin(e) of ground going away from
        // the tank, as against 1/cos(e) of height going up it.
        float reach = (half - lead.Y) / squash;
        float top = ay + half / rise;

        // The faces over their own parameters: the standing half over
        // (x, height), the lying half over (x, ground run).
        var upright = new[]
        {
            new Vector2(ax - half, top), new Vector2(ax + half, top),
            new Vector2(ax + half, 0.0f), new Vector2(ax - half, 0.0f),
        };
        var foot = new[]
        {
            new Vector2(ax - half, 0.0f), new Vector2(ax + half, 0.0f),
            new Vector2(ax + half, reach), new Vector2(ax - half, reach),
        };

        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        // One polygon of one face, fanned into triangles, the whole of it
        // slid d along the view ray. Vertices can carry their own d, but a
        // surface interpolated between two heights is the ramp again.
        void Fan(IReadOnlyList<Vector2> poly, bool standing, float d)
        {
            var slide = new Vector3(0.0f, d / rise, d / squash);
            for (int i = 1; i + 1 < poly.Count; i++)
            foreach (int k in new[] { 0, i, i + 1 })
            {
                Vector2 p = poly[k];
                float u = (p.X - (ax - half)) / PaintSize;
                st.SetUV(standing
                    ? new Vector2(u, seam * (1.0f - p.Y / top))
                    : new Vector2(u, seam + (1.0f - seam) * p.Y / reach));
                st.SetNormal(standing ? Vector3.Back : Vector3.Up);
                st.AddVertex(standing
                    ? slide + new Vector3(p.X, p.Y, 0.0f)
                    : slide + lie + new Vector3(p.X, 0.0f, p.Y));
            }
        }

        if (Mathf.Abs(drop) < 0.25f)
        {
            Fan(upright, true, 0.0f);
            Fan(foot, false, 0.0f);
        }
        else
        {
            // The screen offset from the contact of an upright point is
            // (x, -h*cos(e)) and of a lying point (x, +z*sin(e)), so the seam
            // is linear over both faces and the mounted side is f >= 0.
            (List<Vector2> crown, List<Vector2> tail) = Split(upright,
                p => line.X * p.X - line.Y * p.Y * rise - line.Z);
            Fan(crown, true, 0.0f);
            Fan(tail, true, drop);
            (crown, tail) = Split(foot,
                p => line.X * p.X + line.Y * p.Y * squash - line.Z);
            Fan(crown, false, 0.0f);
            Fan(tail, false, drop);
        }
        return st.Commit();
    }

    /// <summary>A convex polygon cut in two along f = 0, winding preserved:
    /// what f keeps, and what it rejects.</summary>
    private static (List<Vector2>, List<Vector2>) Split(
        IReadOnlyList<Vector2> poly, System.Func<Vector2, float> f)
    {
        var kept = new List<Vector2>();
        var cut = new List<Vector2>();
        for (int i = 0; i < poly.Count; i++)
        {
            Vector2 a = poly[i], b = poly[(i + 1) % poly.Count];
            float fa = f(a), fb = f(b);
            if (fa >= 0.0f)
                kept.Add(a);
            if (fa <= 0.0f)
                cut.Add(a);
            if (fa > 0.0f != fb > 0.0f && Mathf.Abs(fa - fb) > 1e-6f)
            {
                Vector2 m = a.Lerp(b, fa / (fa - fb));
                kept.Add(m);
                cut.Add(m);
            }
        }
        return (kept, cut);
    }

    /// <summary>Every tank's holder and quad, from where it stands this
    /// frame.</summary>
    public void Place(IReadOnlyList<Vehicle> vehicles)
    {
        var centre = new Vector2(PaintSize * 0.5f, PaintSize * 0.5f);
        foreach (Vehicle vehicle in vehicles)
        {
            if (!_stands.TryGetValue(vehicle, out Stand? stand))
                continue;
            // The sprite draws at the position the harness gave it, in the
            // harness's space; the holder is what brings that to the middle of
            // the target. Undone here rather than by moving the sprite, because
            // the sprite's position is read by thirteen other places and is the
            // one definition of where this tank is.
            stand.Holder.Position = centre - vehicle.Sprite.Position;

            // The quad stands at the contact point, not at the anchor: the anchor
            // floats a class-scaled fifty-odd pixels up, so three tanks abreast
            // would sort by how tall they are drawn. The anchor offset is cut into
            // the mesh instead, where it also decides where the surface bends.
            Vector2 lead = vehicle.Atlas.GroundOffset * vehicle.Sprite.BodyScale;
            // The split as the mesh carries it: how far the far side of the
            // seam stands off the nose's height, negative for a climb's tail
            // and positive for the crown a descending tank's tail still holds.
            // Whether a leg splits at all - and the climb's fade driving
            // straight up or down the screen - is Main.Climb's decision, made
            // next to the knowledge of which leg this is; here a parked tank
            // simply has Trailing == Standing. The seam line is fixed to the
            // board; it moves through the quad's frame as the tank moves past.
            float drop = vehicle.Trailing - vehicle.Standing;
            Vector3 world = vehicle.SeamLine;
            var line = new Vector3(world.X, world.Y,
                world.Z - world.X * vehicle.GroundPoint.X
                        - world.Y * vehicle.GroundPoint.Y);
            if (lead != stand.Lead || Mathf.Abs(drop - stand.Drop) > 0.25f
                || (line - stand.Line).Length() > 0.5f)
            {
                stand.Quad.Mesh = Body(lead, Squash, RiseFactor, drop, line);
                stand.Lead = lead;
                stand.Drop = drop;
                stand.Line = line;
            }
            stand.Quad.Position = Contact(vehicle);
            // <b>A tank on a slope is drawn over the ground, parked or not</b> - see
            // Vehicle.OnSlope. A slope is where a flat billboard and the geometry
            // disagree the most: the sprite stands vertically at one point of a face
            // that is rising through it, so the half of the face in front of that
            // point is nearer AND higher, and honest depth takes the tank's lower
            // body. Measured on the rosette, where the case is plain - a tank parked
            // on the ramp at (11,2) sits behind the hill it climbs, and the hill is
            // a level up against the ramp's half.
            //
            // <b>A flat leg at one level keeps the test</b>, which is the other half
            // of the same rule and the thing the stage exists to show: a tank
            // driving behind a ridge on its own level stays behind it. So this is
            // not "moving", it is "on a slope" - the two coincided on the board's
            // first three ramps and come apart everywhere else.
            //
            // A tank on a leg that changes levels is drawn over everything -
            // the user's call, made with the cost on the table. The honest
            // mid-leg occlusion there is a billboard crossing a volume, and
            // every version of it bought some stutter: the crown promotion
            // snapped at parking, the descent wipe still shed a couple of
            // hundred pixels a frame at cruise. Skipping the depth test for
            // those legs trades all of that for the one discontinuity that
            // is left - the tank drops back into honest depth the moment it
            // parks. Flat legs keep the test: a tank passing behind a ridge
            // on its own level stays honestly behind it, which is what the
            // stage exists to show. The split machinery above keeps running;
            // with the test off it just decides nothing until the tank
            // stops.
            //
            // Swapped rather than switched, because the depth test is a render
            // mode and a render mode is compiled in - see Paint.
            bool over = vehicle.Levelling || vehicle.OnSlope;
            if (over != stand.Over)
            {
                stand.Quad.MaterialOverride =
                    Paint(stand.Paint.GetTexture(), over);
                stand.Over = over;
            }
            // The waterline, in the sprite's own frame rather than the world's -
            // see PaintShader for why it cannot be one row. After the swap above,
            // never before it: a new material is a new set of uniforms.
            //
            // Off Height and not Standing, because this is measured against where
            // the sprite is *drawn* touching the ground: Standing is the depth
            // promotion and is deliberately stepped, so a leg with one end on a
            // ramp would put the line most of a level out.
            var ink = (ShaderMaterial)stand.Quad.MaterialOverride;
            AtlasSet atlas = vehicle.Atlas;
            float scale = vehicle.Sprite.BodyScale;
            // Less Origin, and that omission is what broke the swell: a ground
            // point is in the tanks' space and CellAt reads the field's own, so
            // an unshifted point resolves to a cell most of a board away. It
            // fails quietly in both directions at once - the tank wears some
            // other cell's water and its own cell is never told it is being
            // driven through.
            ink.SetShaderParameter(
                "pond", WaterTintAt(Field.CellAt(vehicle.GroundPoint - Origin)));
            ink.SetShaderParameter("shore", atlas.Groundline);
            ink.SetShaderParameter("shore_map", new Godot.Vector4(
                atlas.Anchor.X, atlas.Anchor.Y, scale, PaintSize));
            ink.SetShaderParameter("shore_cut", new Godot.Vector4(
                vehicle.Wading ? vehicle.Waterline - vehicle.Height : -1.0f,
                atlas.Groundline is null ? -1.0f
                    : (atlas.FrameFor(vehicle.Sprite.HullFacing) + 0.5f)
                      / Mathf.Max(atlas.Count, 1),
                atlas.HexRect.Position.Y + atlas.HexRect.Size.Y * 0.5f,
                Mathf.Max(atlas.Tile.Y - 1, 1)));
        }
        // The mark goes where the tank touches the ground, every frame, for the
        // reason the 2D ring follows the contact patch: a tank spends most of an
        // order between two cells, and a mark on the cell behind it reads as the
        // selection lagging.
        if (Selected is not null)
            _ring.Position = Contact(Selected);
        // Every frame, and not only when a tank moves: the trail fades by age and
        // is forgotten from the front, so a mesh left standing is a trail that
        // stopped weathering the moment the tank parked.
        BuildRuts(vehicles);
        // The same, and for a nearer reason: the wood leans in a gust that is
        // always moving, so a frame that skipped this is a frame the wind stopped
        // in.
        BuildTrees();
    }


    /// <summary>Where a tank touches the ground, in the world.
    ///
    /// <b>Standing rather than Height</b>, and the two differ only mid-step: the
    /// row drawn is the same for either, so this buys depth and nothing else. See
    /// <see cref="Vehicle.Standing"/> for what it buys it against.</summary>
    private Vector3 Contact(Vehicle vehicle)
    {
        Vector2 ground = vehicle.GroundPoint;
        return World(new Vector2(ground.X, ground.Y + vehicle.Standing),
                     vehicle.Standing);
    }

    // --- the board -----------------------------------------------------------

    /// <summary>What the board is wearing, so it is rebuilt when that changes
    /// and not otherwise. The colours rather than the lists, because the colour
    /// is what the mesh carries and two different lists that paint the same
    /// board are the same board.</summary>
    private string Signature()
    {
        var note = new System.Text.StringBuilder();
        note.Append(Field.Columns).Append('x').Append(Field.Rows)
            .Append('@').Append(Field.Lift.ToString("F3"))
            // Which pond, because the material is chosen inside BuildWater and
            // nowhere else: left out, the panel's own switch for it would move
            // a field nothing ever reads again, which is a control that does
            // nothing and says nothing.
            .Append(Deep ? "|deep" : "|strip");
        for (int q = 0; q < Field.Columns; q++)
        for (int r = 0; r < Field.Rows; r++)
        {
            var cell = new Vector2I(q, r);
            // The ramp's heading too, and it has to be said: a cell that starts
            // tilting without changing level or colour is a board that is not
            // rebuilt, which reads as the ramp not having been modelled.
            note.Append(Field.LevelAt(cell)).Append('/')
                .Append(Field.RampHeading(cell))
                // And whether it is under water, for the ramp heading's reason: a
                // cell that floods without changing level or colour is a board
                // that was not rebuilt, which reads as the water not having been
                // put on the map.
                .Append(Field.IsWater(cell) ? '~' : '.')
                .Append(Field.InkFor(cell).ToRgba32()).Append(';');
        }
        // The depth as well, and not only where the water is: it is a slider, and
        // moving it moves the surface without moving a single cell of the mask.
        note.Append('@').Append(Field.WaterRise.ToString("F3"));
        // And whether it is painted, because that swaps the surface's material
        // and the mesh's UVs at once - a toggle that only changed the shader
        // would leave the drawn pond wearing no texture coordinates at all.
        note.Append(Surf is { Any: true } ? '#' : '=');
        // Whether there is a ring, not where it is: it is cut about its own
        // origin and driven every frame, so the cell it stands on is not
        // something the board has to be rebuilt for.
        note.Append(Selected is null ? '-' : '+');
        return note.ToString();
    }

    /// <summary>The hexagon's corners about a cell centre, in the ground plane:
    /// a true flat-top hexagon with no squash and no sine in it, because the
    /// camera does that. The 2D board carries the squash in its own corner
    /// table, which is the same hexagon already projected.</summary>
    private Vector3[] Corners()
    {
        float r = Field.Atlas!.HexRect.Size.X * 0.5f;
        float s = r * Mathf.Sqrt(3.0f) * 0.5f;
        return new[]
        {
            new Vector3(r, 0.0f, 0.0f), new Vector3(r * 0.5f, 0.0f, s),
            new Vector3(-r * 0.5f, 0.0f, s), new Vector3(-r, 0.0f, 0.0f),
            new Vector3(-r * 0.5f, 0.0f, -s), new Vector3(r * 0.5f, 0.0f, -s),
        };
    }

    /// <summary>A cell's centre at its own base level - the lowest its top face
    /// reaches. A flat cell's whole top sits here; a ramp's rises off it, which
    /// <see cref="Field"/>'s <c>TopCorners</c> carries per corner.</summary>
    private Vector3 CellTop(Vector2I cell)
    {
        float lift = Field.LevelAt(cell) * Field.Lift;
        Vector2 flat = Origin + Field.FlatAnchor(cell) + Field.CentreOffset;
        return World(flat, lift);
    }

    /// <summary>How far above a cell's base each of its six corners stands, as a
    /// world offset. Read off the field rather than worked out here, so the ramp's
    /// shape has one statement and the picking, the pathing and the mesh cannot
    /// disagree about it.</summary>
    private Vector3[] CornerLift(Vector2I cell)
    {
        float[] top = Field.TopCorners(cell);
        float floor = Field.LevelAt(cell) * Field.Lift;
        var up = new Vector3[6];
        for (int i = 0; i < 6; i++)
            up[i] = new Vector3(0.0f, (top[i] - floor) / RiseFactor, 0.0f);
        return up;
    }

    private static readonly Color Earth = new(0.42f, 0.30f, 0.20f);

    /// <summary>
    /// What water is on a board with no art on disk: the surface plane's own
    /// vertex colour, and the tint the submerged part of a tank is composited
    /// with in <see cref="PaintShader"/>.
    ///
    /// <b>One constant because it is one statement.</b> The tint exists precisely
    /// to stand in for the plane over that fragment, so a second colour beside it
    /// would be the same water disagreeing with itself - and the way that shows is
    /// a hull that is a different water from the water it is in, which reads as the
    /// tank being lit rather than as it being wet. With art loaded that one
    /// statement moves to <see cref="WaterTint"/>, which reads it off the strip.
    ///
    /// <b>The number is the drawn surface's average, and it was a colour picked by
    /// eye.</b> This is the flat half of an A/B, so the two halves must differ by
    /// the texture and by nothing else; a fallback in its own blue would make the
    /// toggle a colour swap wearing a texture swap's name. Measured at (55, 96, 94)
    /// encoded - see <c>water_sheet.py</c> - which is darker and greener than the
    /// blue that stood here.
    ///
    /// Two thirds opaque, and that part is unchanged. The ground here is pale -
    /// that is the argument every additive layer in this project has already had
    /// with it - so water that let most of the bottom through would read as a stain
    /// on the grass, and water that let none through would read as a hole.
    /// </summary>
    public static readonly Color Pond = new(0.038f, 0.114f, 0.110f, 0.66f);

    /// <summary>
    /// What a wading tank's submerged half is tinted with: the drawn surface's
    /// own average when there is one, and <see cref="Pond"/> when there is not.
    ///
    /// <b>Measured off the loaded strip rather than typed in beside it.</b> The
    /// tint's whole job is to be the surface over a fragment the shader cannot
    /// sample - so the one thing it must never be is a second opinion about what
    /// that surface looks like. Redraw the sheet and this follows; type the
    /// number here and it follows only when somebody remembers.
    ///
    /// The opacity stays this side of it either way. How much of the bottom
    /// shows through is a statement about the ford, not about the paint.
    /// </summary>
    /// <summary>
    /// The tint for a tank standing in a given cell: the water it is actually in.
    ///
    /// <b>Per cell, because the state is per cell.</b> The tint's whole job is to
    /// be the surface over a fragment the sprite's shader cannot sample, and once
    /// a cell can be calm while its neighbour breaks, one tint for the pond is the
    /// same water disagreeing with itself again - a tank in a churning cell would
    /// come out darker than the water around it, which reads as shadow rather than
    /// as wet.
    /// </summary>
    public Color WaterTintAt(Vector2I cell)
    {
        if (Surf is not { Any: true } s)
            return Pond;
        float band = Mathf.Clamp(Sea?.StateAt(cell) ?? 0.0f, 0.0f,
                                 WaterArt.Bands - 1);
        int low = Mathf.FloorToInt(band);
        Color a = s.MeanOfBand(low), b = s.MeanOfBand(low + 1);
        float t = band - low;
        return new Color(Mathf.Lerp(a.R, b.R, t), Mathf.Lerp(a.G, b.G, t),
                         Mathf.Lerp(a.B, b.B, t), Pond.A);
    }

    public Color WaterTint =>
        Surf is { Any: true } s
            ? new Color(s.Mean.R, s.Mean.G, s.Mean.B, Pond.A) : Pond;

    /// <summary>
    /// How much a ramp's top face is darkened against the flat ground.
    ///
    /// <b>A mark, not a light, and the arithmetic is why.</b> The top faces are
    /// unshaded on purpose - the ground art arrived pre-lit - so a tilted plane
    /// comes out the identical colour as a horizontal one and the only cue left is
    /// the silhouette. Measured: at grade 0.6 the ramps moved 35352 px of the
    /// picture and still read as the plateau being one cell bigger rather than as
    /// a slope up to it.
    ///
    /// The honest lighting factor is <c>cos(slope)</c>, and it does not read: at
    /// the default grade that is 0.970, three percent. So this is the same answer
    /// <see cref="RaisedEdge"/> gives to the same problem - the mark carries its
    /// own contrast rather than borrowing the ground's - and it is lighter than
    /// <see cref="WallInk"/>'s 0.74 because a ramp is ground that is driven on and
    /// not a wall.
    /// </summary>
    private const float RampShade = 0.88f;

    private static Color WallInk(int heading)
    {
        float k = heading is 270 or 90 ? 1.0f : 0.74f;
        return new Color(Earth.R * k, Earth.G * k, Earth.B * k);
    }

    /// <summary>
    /// One prism per cell, from its own top face down to the board's floor, and
    /// the ring under the tank being driven.
    ///
    /// <b>All six sides are built and three of them are never seen.</b> In 2D
    /// the three facing away had to be named and left out, because a side
    /// extruded from a far edge leaves the hexagon at its vertex and lands on the
    /// cell behind. Here they are simply behind their own cell's top face, which
    /// is nearer by <c>(y_top - y)/sin(e)</c> at every screen point the two
    /// share - so the list is gone and with it the chance of getting it wrong.
    ///
    /// The outline goes on here rather than in its own pass because it is a band
    /// of the cell's own top face and belongs to the loop that knows where that
    /// face is. <b>Per cell, and on a stepped board that is not a choice:</b> two
    /// neighbours' rims are at different heights and each has to be drawn at its
    /// own, so there is no shared edge to draw once. On the flat that shows as an
    /// interior seam carrying two bands to the board's outer edge's one, which is
    /// honest - a seam is a boundary twice over.
    /// </summary>
    private void Build()
    {
        if (Field.Atlas is null)
            return;
        (int low, _) = Field.LevelRange;
        float floor = World(Vector2.Zero, low * Field.Lift).Y;
        Vector3[] corner = Corners();

        var tops = new SurfaceTool();
        var sides = new SurfaceTool();
        var edges = new SurfaceTool();
        tops.Begin(Mesh.PrimitiveType.Triangles);
        sides.Begin(Mesh.PrimitiveType.Triangles);
        edges.Begin(Mesh.PrimitiveType.Triangles);
        Vector3 lie = Clear(Squash, RiseFactor);

        Texture2D? art = GroundArt();
        for (int q = 0; q < Field.Columns; q++)
        for (int r = 0; r < Field.Rows; r++)
        {
            var cell = new Vector2I(q, r);
            Vector3 top = CellTop(cell);
            // How far each corner stands off that base, and the middle of the
            // face. A ramp's top is still one plane - the height is linear in
            // position - so the fan and the rim band are unchanged in kind; they
            // just carry a lift per point now.
            Vector3[] up = CornerLift(cell);
            Vector3 mid = Vector3.Zero;
            foreach (Vector3 v in up)
                mid += v / 6.0f;
            Color ink = Field.InkFor(cell);
            if (art is null)
                ink = new Color(SoilInk.R * ink.R, SoilInk.G * ink.G,
                                SoilInk.B * ink.B);
            if (Field.IsRamp(cell))
                ink = new Color(ink.R * RampShade, ink.G * RampShade,
                                ink.B * RampShade);

            for (int i = 1; i + 1 < 6; i++)
            foreach (int k in new[] { 0, i, i + 1 })
            {
                tops.SetColor(ink);
                // The art is keyed to the footprint, not to where the point is
                // drawn - so a slope wears the same hexagon of ground stretched
                // over a face that projects taller, which is what a slope does.
                tops.SetUV(GroundUV(corner[k]));
                tops.AddVertex(top + corner[k] + up[k]);
            }

            // A raised cell wears the wider band: its top face is cut from
            // the same dirt as the floor and lit the same - unshaded, on
            // purpose - so away from the walls the two are one texture, and
            // the rim of the outline is most of what says "this ground is
            // higher" at a glance.
            // Above the datum, not above the board's low: a map with a pit
            // has low = -1, and "higher than the lowest" would put the wide
            // band on the entire floor.
            float inner = Field.LevelAt(cell) > 0 || Field.IsRamp(cell)
                ? RaisedEdge : Edge;
            Vector3 rim = top + lie;
            for (int i = 0; i < 6; i++)
            {
                int j = (i + 1) % 6;
                Vector3 a = corner[i], b = corner[j];
                // The band's inner edge is pulled toward the centre, so its lift
                // is the face's own height there - interpolated from the middle
                // rather than copied off the corner, which on a slope would hang
                // the band off the plane it is meant to lie on.
                Vector3 ai = mid + (up[i] - mid) * inner;
                Vector3 bj = mid + (up[j] - mid) * inner;
                foreach (Vector3 v in new[]
                         {
                             rim + a + up[i], rim + b + up[j], rim + b * inner + bj,
                             rim + a + up[i], rim + b * inner + bj,
                             rim + a * inner + ai,
                         })
                {
                    edges.SetColor(EdgeInk);
                    edges.AddVertex(v);
                }
            }

            // Any corner above the floor earns a wall, not just a raised cell: a
            // ramp standing on the floor still has its high edge a whole level
            // up, and skipping it on the old "is the base above the floor" test
            // left the slope hanging over nothing.
            float highest = top.Y;
            foreach (Vector3 v in up)
                highest = Mathf.Max(highest, top.Y + v.Y);
            if (highest - floor <= 0.0f)
                continue;
            for (int i = 0; i < 6; i++)
            {
                int j = (i + 1) % 6;
                Vector3 a = top + corner[i] + up[i], b = top + corner[j] + up[j];
                var af = new Vector3(a.X, floor, a.Z);
                var bf = new Vector3(b.X, floor, b.Z);
                Color side = WallInk((330 - 60 * i + 360) % 360);
                foreach (Vector3 v in new[] { a, b, bf, a, bf, af })
                {
                    sides.SetColor(side);
                    sides.AddVertex(v);
                }
            }
        }

        tops.GenerateNormals();
        sides.GenerateNormals();
        edges.GenerateNormals();
        _tops.Mesh = tops.Commit();
        _sides.Mesh = sides.Commit();
        _edges.Mesh = edges.Commit();
        _edges.MaterialOverride = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            VertexColorUseAsAlbedo = true,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            // The ring's reason, unchanged: a tank tests depth without writing
            // it, so two transparent surfaces cannot settle each other and the
            // sort decides. Said out loud, the ground goes under what stands on
            // it.
            RenderPriority = -1,
        };
        _tops.MaterialOverride = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            VertexColorUseAsAlbedo = true,
            AlbedoTexture = art,
            TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmaps,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
        _sides.MaterialOverride = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            VertexColorUseAsAlbedo = true,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
        BuildRing(corner);
        BuildWater(corner);
    }

    /// <summary>
    /// One flat hexagon per water cell, at the surface of the water over it.
    ///
    /// <b>No clearance off the ground, and it is the one thing lying on this board
    /// that needs none.</b> A rut and a ring are coplanar with the face they mark
    /// and have to be nudged off it or the depth test tosses a coin - see
    /// <see cref="Clear"/>. This stands a good part of a level above its own
    /// bottom, because that is what a depth <i>is</i>, so there is nothing for it
    /// to fight with.
    ///
    /// <b>One mesh for the whole board, where the trees are one node each.</b> The
    /// trees are sorted against the tanks individually and so must each carry
    /// their own key; this sorts under every tank by
    /// <see cref="WaterOrder"/>, so what its single key would decide has already
    /// been decided. What replaces the per-cell sort is the tint in
    /// <see cref="PaintShader"/>, which is per fragment and therefore right where
    /// no sort key could be.
    /// </summary>
    private void BuildWater(Vector3[] corner)
    {
        bool drawn = Surf is { Any: true };
        _pond.MaterialOverride = Deep ? DeepWater()
                               : drawn ? Swell() : Decal(WaterOrder);
        if (!Field.HasWater)
        {
            _pond.Mesh = null;
            return;
        }
        // Half the hexagon, so a corner maps to a corner of the frame. Not the
        // camera's squash: the frame was cut to the tile's own shape by
        // water_sheet.py, so the two agree by construction and a sine here
        // would be a second opinion about it.
        float halfX = 0.0f, halfZ = 0.0f;
        foreach (Vector3 c in corner)
        {
            halfX = Mathf.Max(halfX, Mathf.Abs(c.X));
            halfZ = Mathf.Max(halfZ, Mathf.Abs(c.Z));
        }
        // In world units, taken off the hexagon the foam has to stay inside
        // rather than written down: the tile is a dial, and a reach in pixels
        // would be right on one setting of it.
        _pondReach = Mathf.Max(halfX, halfZ) * FoamReach;
        // Pulled in off its own boundary, exactly as the ground faces are - see
        // Bleed, which is this defect solved once already.
        Vector2 inset = drawn
            ? new Vector2(1.0f - 2.0f * SwellBleed / Surf!.Frame.X,
                          1.0f - 2.0f * SwellBleed / Surf!.Frame.Y)
            : Vector2.One;
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        bool any = false;
        int over = 0;
        for (int q = 0; q < Field.Columns; q++)
        for (int r = 0; r < Field.Rows; r++)
        {
            var cell = new Vector2I(q, r);
            if (!Field.IsWater(cell))
                continue;
            any = true;
            // Its place in the field's own list, written into every vertex so the
            // surface can be told what this cell is doing without the mesh being
            // rebuilt for it. Past the cap it reads the last slot rather than off
            // the end of the array, and the note says so - a pond that quietly
            // went calm past cell 64 would be the silent failure.
            int ord = Field.WaterIndex(cell);
            if (ord >= MaxWaterCells)
            {
                over++;
                ord = MaxWaterCells - 1;
            }
            var ordinal = new Vector2(ord, 0.0f);
            Vector2 flat = Origin + Field.FlatAnchor(cell) + Field.CentreOffset;
            Vector3 top = World(flat, Field.WaterTop(cell));
            for (int i = 1; i + 1 < 6; i++)
            foreach (int k in new[] { 0, i, i + 1 })
            {
                st.SetColor(Pond);
                // +Z reads as +v, the same way the ground art is laid on a top
                // face: two conventions in one scene is how a texture comes out
                // mirrored on one surface and nobody can say against what.
                st.SetUV(new Vector2(
                    0.5f + inset.X * corner[k].X / (2.0f * halfX),
                    0.5f + inset.Y * corner[k].Z / (2.0f * halfZ)));
                st.SetUV2(ordinal);
                st.AddVertex(top + corner[k]);
            }
        }
        if (!any)
        {
            _pond.Mesh = null;
            return;
        }
        if (over > 0)
            GD.PushWarning($"{over} flooded cells past the {MaxWaterCells} the "
                           + "surface can carry a state for - they share the last "
                           + "slot, so they will churn when it does");
        st.GenerateNormals();
        _pond.Mesh = st.Commit();
    }

    /// <summary>
    /// How far inside its own frame the surface stops sampling, in art px.
    ///
    /// <b>The belt, not the braces.</b> What actually broke the pond was the art
    /// carrying a keyed rim: a frame is exactly the plate, so the hexagon faded
    /// out on the frame's own boundary, two cells meeting laid two fades on top of
    /// each other, and the five-cell pond drew as three pieces with a 5px strip of
    /// dry ground along every shared edge. That is fixed where it was made -
    /// <c>water_sheet.py</c> hardens the alpha, because <b>the mesh is already a
    /// hexagon and the texture must not be one too</b>.
    ///
    /// This stays for what a hard mask cannot help: the outermost texel of a hard
    /// edge is still half a texel from the boundary, and a linear sample there
    /// reaches across it. Eight is 2.2% of a 360px plate, the same fraction
    /// <see cref="Bleed"/> takes off the ground's 248px plate for the same reason.
    /// <see cref="WaterArt.EdgeAlpha"/> is what holds the pair honest: it measures
    /// the alpha the mesh can actually reach, after this.
    /// </summary>
    internal const float SwellBleed = 8.0f;

    /// <summary>
    /// How long one turn of the water's frames takes, in seconds.
    ///
    /// <b>Seconds and not a multiplier, because there is no triple under it.</b>
    /// The dials that are levels - tremble, exhaust, traverse, tracer - sit over a
    /// per-class trio whose spread a direct control would flatten on the first
    /// drag. The pond is one board's worth of water. Same call
    /// <c>--smoke-life</c> made.
    ///
    /// <b>The period rather than a rate, because what was measured is a period.</b>
    /// Four frames over 2.4s is one every 0.6s - 36 screen frames apiece, which is
    /// a slideshow and is exactly what "jerky" was. Speed alone cannot answer that:
    /// four frames step at any rate, and faster only trades a slideshow for a
    /// flicker. What answers it is <see cref="SwellBlendDefault"/>; this then says
    /// how fast the water flows rather than how coarsely it ticks.
    /// </summary>
    public const float SwellPeriodDefault = 2.4f;

    /// <summary>Live, off the panel row. Floored rather than validated: a period
    /// of zero is a still pond, which is a legible thing to ask for, where the
    /// division it would otherwise do is not.</summary>
    public float SwellPeriod = SwellPeriodDefault;

    /// <summary>How far one frame is carried into the next, 0..1.
    ///
    /// <b>Zero is the stepped loop exactly</b>, which is what makes this an A/B
    /// rather than a smoothing knob: the shader mixes toward the following frame
    /// by <c>fract(t) * blend</c>, so at zero the mix term vanishes and what is
    /// drawn is the floor frame and nothing else - the pond as it was.
    ///
    /// <b>On by default, because it is the answer to the complaint.</b> Four
    /// frames cannot be made smooth by choosing when to change them; they can only
    /// be made to stop changing all at once. The cost is honest and worth naming:
    /// a dissolve is not a translation, so what crossing frames buys is continuous
    /// motion and what it spends is a little of the wave's edge. On this quartet
    /// that trade is cheap - consecutive frames differ by 6-9 units of picture,
    /// which is why they were chosen as the ford's frames in the first place.
    /// </summary>
    public const bool SwellBlendDefault = true;

    public bool SwellBlend = SwellBlendDefault;

    /// <summary>
    /// How much foam a stirred cell throws, as a multiplier over none.
    ///
    /// <b>A detail on the band, never a second opinion about it.</b> What says a
    /// tank was here is the strip's own ladder - calm, chop, break - and the foam
    /// is cut by that same state, so it can only agree with it. It is worth having
    /// beside it because the ladder is quantised to three rungs and foam is not:
    /// the band cannot say "a smaller splash" at all, which is why leaving a cell
    /// had to be told from entering one by loop length rather than by strength.
    ///
    /// <b>Off is bit-identical to the pond as it was</b>, and that is the whole
    /// claim this has to earn - measured, not asserted, by capturing the same
    /// frame twice. It holds twice over: the level multiplies every term, and at
    /// state zero the noise cannot reach the cutoff in the first place.
    ///
    /// On by default, on the contact shadow's argument: an A/B against the thing
    /// itself is the only case for the layer, and a switch nobody flips is a
    /// layer nobody looks at.
    /// </summary>
    public const float FoamDefault = 1.0f;

    public float Foam = FoamDefault;

    /// <summary>Where the noise has to reach before any foam is drawn, over and
    /// above whatever the cell's own state adds to it.</summary>
    public const float FoamCut = 0.92f;

    /// <summary>
    /// What one rung of the ladder is worth to the foam.
    ///
    /// <b>Measured off the noise, and the first cut of it was not.</b> The band
    /// arrives normalised to 0, 0.5 and 1, and adding that straight to the noise
    /// moves it by more than the noise's whole spread - p5 to p95 is 0.48 - so
    /// the top rung saturated and the foam came out as a solid white slab with
    /// the hexagon's own edges on it. Which is the water drawing the cell
    /// boundary: the one failure this layer is not allowed to have.
    ///
    /// At 0.36 the rungs cover <b>2.9% and 37.9%</b> of a cell - a few flecks
    /// under a tank driving through, a broken raft where one crossed - and still
    /// water stays at nought because <see cref="FoamCeiling"/> is below the cut.
    /// </summary>
    public const float FoamRung = 0.55f;

    /// <summary>
    /// The most the three octaves of <c>clumps()</c> can sum to - 0.5 + 0.25 +
    /// 0.125, because each squared cell distance tops out at one.
    ///
    /// Named so the thing it guarantees can be asserted rather than believed:
    /// this sits below <see cref="FoamCut"/> + 0.5, which is the threshold a cell
    /// at state zero faces, so calm water takes no foam <i>by construction</i>.
    /// Raise the cut or add an octave without looking here and the pond starts
    /// growing foam where nobody has been.
    /// </summary>
    public const float FoamCeiling = 0.875f;


    /// <summary>How far foam pulls the surface towards opaque. Under one on
    /// purpose: foam hides the bottom, but taken all the way it stops reading as
    /// something floating on a ford and starts reading as a hole in it.</summary>
    public const float FoamLift = 0.55f;

    /// <summary>How fast the bubbles inside a raft drift, in radians a second.
    /// On the bench's side of the line rather than in the shader, because the
    /// shader must not read a clock of its own - see <c>foam_time</c>.</summary>
    public const float FoamDrift = 1.0f;

    /// <summary>How far from the tank foam reaches, as a fraction of the tile's
    /// own circumradius. Under one so a raft dies out before the cell edge it
    /// happens to sit in - the point of measuring it from the tank at all - and
    /// not so far under that a crossing leaves a tidy disc, which is the same
    /// grid artefact with rounder corners.</summary>
    public const float FoamReach = 1.10f;

    private float _foamClock;
    private float _pondReach = 1.0f;

    /// <summary>
    /// How many flooded cells the surface can carry a state for.
    ///
    /// <b>A cap because a shader array has to have one</b>, and named rather than
    /// left at whatever looked big: a pond past it would silently draw calm, which
    /// is the failure this project keeps refusing - so <see cref="BuildWater"/>
    /// says so instead. The board's pond is five.
    /// </summary>
    public const int MaxWaterCells = 64;

    /// <summary>Whether the pond is computed or drawn. On by default because it
    /// is what the water is now; the strip stays reachable so the two can be put
    /// side by side, which is the only way either was ever judged here.</summary>
    public const bool DeepDefault = true;

    public bool Deep = DeepDefault;

    /// <summary>Where the sun is, as a direction to it. Matched to the key light
    /// of the rig the sprites were rendered under - 55 degrees above the horizon
    /// - so the glint on the water and the shading baked into the tanks agree
    /// about which way is up-sun. Nothing else on the board reads it: every other
    /// material is unshaded, which is why a real light node was measured to cost
    /// exactly nothing and was still not used.</summary>
    public static readonly Vector3 Sun =
        new Vector3(-0.40f, 0.82f, -0.41f).Normalized();

    private ShaderMaterial? _deepInk;
    private float _deepClock;

    private ShaderMaterial DeepWater()
    {
        _deepInk ??= new ShaderMaterial
        {
            Shader = new Shader
            {
                Code = string.Format(CultureInfo.InvariantCulture, DeepShader,
                                     MaxWaterCells, FoamCut, FoamLift, FoamRung),
            },
            RenderPriority = WaterOrder,
        };
        _deepInk.SetShaderParameter("sun", Sun);
        _deepInk.SetShaderParameter("bands", (float)WaterArt.Bands);
        return _deepInk;
    }

    private ShaderMaterial? _swellInk;

    private ShaderMaterial Swell()
    {
        _swellInk ??= new ShaderMaterial
        {
            Shader = new Shader
            {
                // Invariant culture, and it is not pedantry: this machine's
                // locale writes a comma for the decimal point, and "foam_cut =
                // 0,7" is a shader that does not compile - the same trap that
                // once made two captures byte-identical because a float flag
                // never parsed.
                Code = string.Format(CultureInfo.InvariantCulture, SwellShader,
                                     MaxWaterCells, FoamCut, FoamLift, FoamRung),
            },
            RenderPriority = WaterOrder,
        };
        _swellInk.SetShaderParameter("surf", Surf!.Texture);
        _swellInk.SetShaderParameter("frames", (float)Surf!.Loop);
        _swellInk.SetShaderParameter("bands", (float)WaterArt.Bands);
        _swellInk.SetShaderParameter("tint", WaterTint);
        return _swellInk;
    }

    /// <summary>Hand the surface what each of its cells is doing. Every frame and
    /// not on a change, because it is a decay: the states are always moving, and
    /// the mesh they belong to is deliberately built only when the board does.
    /// </summary>
    private void PushSwell()
    {
        ShaderMaterial? ink = _deepInk ?? _swellInk;
        if (ink is null)
            return;
        int n = Mathf.Min(Field.WaterCells.Count, MaxWaterCells);
        var state = new float[MaxWaterCells];
        var span = new float[MaxWaterCells];
        var mark = new Vector2[MaxWaterCells];
        for (int i = 0; i < MaxWaterCells; i++)
            span[i] = 1.0f;
        for (int i = 0; i < n; i++)
        {
            state[i] = Sea?.State.Length > i ? Sea.State[i] : 0.0f;
            span[i] = Sea?.Span.Length > i ? Sea.Span[i] : 1.0f;
            // Through the same mapping the surface's own vertices went through,
            // because the shader compares it against world.xz. Squashed here
            // rather than in Swell: the swell keeps the tanks' own coordinates,
            // and which camera is looking at them is not its business.
            Vector2 at = Sea?.Mark.Length > i ? Sea.Mark[i] : Vector2.Zero;
            Vector3 w = World(at, 0.0f);
            mark[i] = new Vector2(w.X, w.Z);
        }
        ink.SetShaderParameter("state", state);
        ink.SetShaderParameter("mark", mark);
        ink.SetShaderParameter("foam_reach", _pondReach);
        // Only the drawn strip has a loop to run part of; the computed surface
        // has no frames at all, so a span means nothing to it.
        _swellInk?.SetShaderParameter("span", span);
    }

    /// <summary>What each flooded cell is doing. Null leaves the pond calm, which
    /// is how it behaved before the water answered anybody - see --still-water.
    /// </summary>
    public Swell? Sea;

    /// <summary>
    /// The pond's surface: one frame of the strip, chosen per cell and per
    /// moment, at the flat colour's own opacity.
    ///
    /// <b>One phase for the whole pond, and the per-cell one was wrong.</b> It was
    /// put in on the rule every clock in this bench obeys - three tanks tremble on
    /// their own, exhaust on their own, scan on their own - and that rule is about
    /// <i>separate objects</i>. A pond is one body of water. Desynchronised, its
    /// cells show different moments of the same swell either side of a shared edge,
    /// so the edge is the one place the picture disagrees with itself, and every
    /// cell boundary is drawn by the water rather than hidden by it.
    ///
    /// <b>The step is taken in the shader and not in the mesh.</b> Advancing the
    /// frame by rewriting the UVs would rebuild the board's geometry sixty times a
    /// second for a picture that has not changed shape - and <see cref="Build"/> is
    /// deliberately gated on the board changing at all.
    ///
    /// <b>Inset by half a texel.</b> A frame is exactly the plate, so the
    /// hexagon's left and right vertices sit on the frame's own boundary, and a
    /// linear sample there reaches into the next frame along - which is the next
    /// moment of the same water, so it does not look like corruption; it looks
    /// like nothing, until the strip is one day cut from frames that differ.
    ///
    /// <b>Two frames fetched, and the last one wraps to the first.</b> The mix is
    /// what stops four frames reading as four; wrapping is free here because the
    /// quartet closes by measurement, not by hope - its wrap step is 1.14 of the
    /// worst step inside it, where the project calls a seam anything over 4. On a
    /// strip that did not close, this blend would be the place it showed.
    ///
    /// The opacity stays the flat colour's: the bottom has to show through, which
    /// is what makes a ford a ford rather than a hole.
    /// </summary>
    internal const string SwellShader = @"
shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_never;
uniform sampler2D surf : source_color, filter_linear_mipmap;
uniform vec4 tint = vec4(0.0);
uniform float frames = 1.0;
uniform float bands = 1.0;
uniform float phase = 0.0;
uniform float blend = 1.0;
uniform float state[{0}];
uniform float span[{0}];
uniform vec2 mark[{0}];
uniform float foam_reach = 1.0;
uniform float foam = 0.0;
uniform float foam_scale = 5.0;
// The foam's own clock, in radians, handed in rather than taken from TIME.
// TIME is the wall clock, and --capture pins the step to 1/60 for the one
// reason that two runs have to be comparable: read it here and every capture
// of the pond comes out different, so any A/B taken near the water measures
// the hour it was taken at. Wrapped by the bench, the way the swell phase is.
uniform float foam_time = 0.0;
uniform float foam_cut = {1};
uniform float foam_lift = {2};
uniform float foam_rung = {3};
uniform vec3 foam_ink : source_color = vec3(1.0, 1.0, 1.0);
uniform vec3 foam_rim : source_color = vec3(0.0, 0.05, 0.08);

varying vec3 world;

float hash12(vec2 p) {{
    uvec2 q = uvec2(ivec2(p)) * uvec2(1597334677u, 3812015801u);
    uint n = (q.x ^ q.y) * 1597334677u;
    return float(n) * (1.0 / 4294967295.0);
}}

float vnoise(vec2 p) {{
    vec2 i = floor(p);
    vec2 f = fract(p);
    vec2 u = f * f * (3.0 - 2.0 * f);
    return mix(mix(hash12(i), hash12(i + vec2(1.0, 0.0)), u.x),
               mix(hash12(i + vec2(0.0, 1.0)), hash12(i + vec2(1.0, 1.0)), u.x),
               u.y);
}}

vec2 hash22(vec2 p) {{
    vec3 q = fract(vec3(p.xyx) * vec3(0.1031, 0.1030, 0.0973));
    q += dot(q, q.yzx + 33.33);
    return fract((q.xx + q.yz) * q.zy);
}}

// Squared distance to the nearest of a drifting set of points, one per grid
// cell. Cell-shaped rather than blobby, which is what a raft of bubbles is -
// and it drifts in place instead of flowing, because the pond has no current.
float cells(vec2 uv) {{
    vec2 n = floor(uv);
    vec2 f = fract(uv);
    float d = 1.0;
    for (int j = -1; j <= 1; j++) {{
        for (int i = -1; i <= 1; i++) {{
            vec2 g = vec2(float(i), float(j));
            vec2 o = hash22(n + g);
            o = 0.5 + 0.5 * sin(foam_time + 6.2831 * o);
            vec2 r = g - f + o;
            d = min(d, dot(r, r));
        }}
    }}
    return d;
}}

// Three octaves of it, so the raft has both clumps and the bubbles inside
// them. Peaks below 0.875 by construction - the sum of the three weights -
// and that ceiling is what keeps calm water clean of foam, see below.
float clumps(vec2 uv) {{
    float v = 0.0;
    float a = 0.5;
    mat2 rot = mat2(vec2(0.8776, 0.4794), vec2(-0.4794, 0.8776));
    for (int i = 0; i < 3; i++) {{
        float s = 1.0 - cells(uv);
        v += a * s * s;
        uv = rot * uv * 2.0 + vec2(100.0);
        a *= 0.5;
    }}
    return v;
}}

void vertex() {{
    world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}}

void fragment() {{
    float total = frames * bands;
    float inset = 0.5 * total / float(textureSize(surf, 0).x);
    float u = clamp(UV.x, inset, 1.0 - inset);

    int cell = int(UV2.x + 0.5);
    // How much of the band's loop this cell runs through. One for all of it;
    // a half leaves a cell that is only being left on the milder two frames of
    // the breaking band - see Swell.LeavingSpan. The floor keeps a still out of
    // it, because a still is not a loop.
    float run = max(1.0, floor(frames * clamp(span[cell], 0.0, 1.0)));
    float t = fract(phase) * run;
    float f0 = floor(t);
    float f1 = mod(f0 + 1.0, run);
    float ft = fract(t) * blend;

    float s = clamp(state[cell], 0.0, bands - 1.0);
    float b0 = floor(s);
    float b1 = min(b0 + 1.0, bands - 1.0);
    float fb = s - b0;

    vec4 lo = mix(texture(surf, vec2((u + b0 * frames + f0) / total, UV.y)),
                  texture(surf, vec2((u + b0 * frames + f1) / total, UV.y)), ft);
    vec4 hi = mix(texture(surf, vec2((u + b1 * frames + f0) / total, UV.y)),
                  texture(surf, vec2((u + b1 * frames + f1) / total, UV.y)), ft);
    vec4 c = mix(lo, hi, fb);
    vec3 rgb = c.rgb;
    float alpha = c.a * tint.a;

    // Foam, cut by the same state the band was chosen with. It is a detail on
    // that statement and never a second one: where the strip says calm, there
    // is none - the sum below cannot reach the cutoff, because clumps() tops
    // out at 0.875 and the threshold at level zero is 1.2. So still water is
    // untouched by construction rather than by a branch somebody can move.
    //
    // In world XZ, not in UV. Per-hex UVs would repeat the same raft in every
    // cell and line its edges up with the grid, which is the one thing the
    // water is on the board to hide.
    // How much the band allows, and then how near the tank that stirred it we
    // are. Both, and the second is not a refinement: a state is one number for
    // a whole hexagon, so a mask made from it alone is hexagonal - the raft
    // filled its cell to the corners while the neighbour stayed clean, which is
    // the water drawing the grid it is on the board to hide. The band says
    // whether, the mark says where.
    float level = clamp(state[cell] / max(bands - 1.0, 1.0), 0.0, 1.0);
    level *= 1.0 - smoothstep(0.45 * foam_reach, foam_reach,
                              distance(world.xz, mark[cell]));
    // The warp does not move, and that is not a saving. It is here to break the
    // clump grid up so the raft has no lattice in it; what moves is the points
    // inside the cells. A drifting warp would be a current, and a pond has none.
    vec2 flow = world.xz * 0.005;
    vec2 warp = vec2(vnoise(flow), vnoise(flow + vec2(5.2, 1.3))) - 0.5;
    vec2 fuv = world.xz * foam_scale * 0.01 + warp;
    float lit = smoothstep(foam_cut, foam_cut + 0.08,
                           clumps(fuv) + level * foam_rung);
    // The same field again, half a clump over: what it covers and the body
    // does not is the far lip of the raft, and drawing that dark is what gives
    // flat foam a thickness.
    float back = clamp(
        smoothstep(foam_cut, foam_cut + 0.08,
                   clumps(fuv + vec2(0.35)) + level * foam_rung)
        - lit, 0.0, 1.0);

    rgb = mix(rgb, foam_rim, back * foam);
    rgb = mix(rgb, foam_ink, lit * foam);
    // Foam is denser than the water it sits on, so it hides the bottom the
    // ford is otherwise showing. Partly, and named: taken all the way it would
    // read as a hole in the pond rather than as something floating on it.
    alpha = mix(alpha, 1.0, max(lit, back) * foam * foam_lift);

    ALBEDO = rgb;
    ALPHA = alpha;
}}
";

    /// <summary>
    /// Water that is computed rather than drawn: a moving surface, the bottom
    /// seen through it, and a sun on it.
    ///
    /// <b>Two facts made this possible, and both were measured, not assumed.</b>
    /// The depth texture works in GL Compatibility and already holds the pit
    /// floor - the ground prisms are opaque, so the buffer has the bottom in it
    /// without anything being re-rendered. And the tanks draw <i>after</i> the
    /// water, so they are not in the screen texture the refraction samples:
    /// bending it cannot smear the one artefact this project judges pixel by
    /// pixel. Both were the standing objections to doing this at all.
    ///
    /// <b>Unshaded with its own highlight, and not a lit material.</b> A light
    /// node was measured too and costs exactly nothing - every other material on
    /// the board is unshaded, so a sun cannot touch them - but a lit surface with
    /// no environment gets its ambient from nowhere, and then the pond's body
    /// answers to a tonemap the sprites do not. The sun arrives as a direction
    /// and the highlight is worked out here, where it can be tuned against the
    /// picture instead of against a rig.
    ///
    /// <b>Opaque, and that is what makes it water rather than tinted glass.</b>
    /// The drawn strip had to be see-through, because showing the bottom was the
    /// only way it could: a ford that hides its floor is a hole. This samples
    /// the floor itself, so it can own every pixel it covers - and then absorb,
    /// tint and refract what it found, which a blend against the ground can only
    /// approximate.
    ///
    /// <b>The surface is a height field, and the mesh never moves.</b> Vertex
    /// waves want a tessellated plane and would put the pond on two heights,
    /// which the board forbids in as many words - a body of water is one surface
    /// - and would leave the tank's waterline arguing with the picture. At 30
    /// degrees of elevation almost all of what reads as motion is the normal
    /// anyway, so the height field lives in the fragment and the hexagons stay
    /// flat.
    /// </summary>
    internal const string DeepShader = @"
shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_never;

uniform sampler2D screen : hint_screen_texture, filter_linear_mipmap;
uniform sampler2D zbuf : hint_depth_texture, filter_linear;

// Our own clock, wrapped, for --capture's reason: TIME is the wall clock and
// two runs have to land on the same picture. See Main.FixedStep.
uniform float time = 0.0;
uniform vec3 sun = vec3(0.0, 1.0, 0.0);
uniform float wave_scale = 0.02;
uniform float wave_speed = 1.0;
uniform float relief = 0.30;
uniform vec3 shallow : source_color = vec3(0.24, 0.52, 0.50);
uniform vec3 deep : source_color = vec3(0.06, 0.22, 0.28);
uniform vec3 absorb : source_color = vec3(0.34, 0.10, 0.06);
uniform float clarity = 110.0;
uniform float refraction = 22.0;
uniform float gloss = 90.0;
uniform float glint = 0.60;
uniform float shore = 10.0;
uniform float foam = 0.0;
uniform float foam_cut = {1};
uniform float foam_rung = {3};
uniform float foam_reach = 1.0;
uniform vec3 foam_ink : source_color = vec3(1.0, 1.0, 1.0);
uniform float state[{0}];
uniform vec2 mark[{0}];
uniform float bands = 1.0;

varying vec3 world;

float hash12(vec2 p) {{
    uvec2 q = uvec2(ivec2(p)) * uvec2(1597334677u, 3812015801u);
    uint n = (q.x ^ q.y) * 1597334677u;
    return float(n) * (1.0 / 4294967295.0);
}}

float vnoise(vec2 p) {{
    vec2 i = floor(p);
    vec2 f = fract(p);
    vec2 u = f * f * (3.0 - 2.0 * f);
    return mix(mix(hash12(i), hash12(i + vec2(1.0, 0.0)), u.x),
               mix(hash12(i + vec2(0.0, 1.0)), hash12(i + vec2(1.0, 1.0)), u.x),
               u.y);
}}

// Three octaves, each folded to a ridge and each drifting its own way. The
// fold is what separates water from cloud: smooth noise has round hills, and
// a surface pulled taut by its own tension has creases between them. The
// per-octave drift is what stops the whole field sliding as one sheet.
float height(vec2 p) {{
    float h = 0.0;
    float a = 1.0;
    float f = 1.0;
    vec2 drift = vec2(0.35, 0.20) * time * wave_speed;
    for (int i = 0; i < 3; i++) {{
        float n = vnoise(p * f + drift);
        h += a * (1.0 - abs(2.0 * n - 1.0));
        a *= 0.5;
        f *= 2.03;
        drift = vec2(-drift.y, drift.x) * 1.7;
    }}
    return h;
}}

void vertex() {{
    world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}}

void fragment() {{
    vec2 p = world.xz * wave_scale;
    // Central differences, in the height field's own units. The step is the
    // one number that decides how crisp the surface reads: too small and it
    // shimmers on a still frame, too large and the creases go out of it.
    float e = 0.35;
    float h = height(p);
    float hx = height(p + vec2(e, 0.0)) - h;
    float hz = height(p + vec2(0.0, e)) - h;
    vec3 n = normalize(vec3(-hx * relief, e, -hz * relief));

    // How much water is in front of the bottom. The prisms are opaque, so this
    // is the floor of the pit and the wall it climbs at the shore.
    float own = FRAGCOORD.z;
    float raw = texture(zbuf, SCREEN_UV).x;
    vec3 ndc = vec3(SCREEN_UV * 2.0 - 1.0, raw * 2.0 - 1.0);
    vec4 view = INV_PROJECTION_MATRIX * vec4(ndc, 1.0);
    float floor_z = -(view.z / view.w);
    float here_z = -(INV_PROJECTION_MATRIX * vec4(vec3(SCREEN_UV * 2.0 - 1.0,
                     own * 2.0 - 1.0), 1.0)).z;
    float thick = max(0.0, floor_z - here_z);

    // Bent by the surface, and the amount falls off in the shallows so the
    // shoreline does not tear. The tanks are drawn after this pass, so what is
    // being bent is the ground and nothing else.
    vec2 bend = n.xz * refraction * clamp(thick / max(clarity, 0.001), 0.0, 1.0);
    vec2 uv = SCREEN_UV + bend / vec2(textureSize(screen, 0));
    vec3 bottom = texture(screen, uv).rgb;

    // Beer-Lambert on the way down and back: red goes first, which is the whole
    // reason open water is blue. Then the volume's own colour over the top of
    // what is left.
    float t = clamp(thick / max(clarity, 0.001), 0.0, 1.0);
    vec3 through = bottom * exp(-thick * absorb / max(clarity, 0.001));
    vec3 body = mix(shallow, deep, t);
    vec3 col = mix(through, body, t * 0.55);

    // One sun, worked out here. The camera is orthographic, so the view vector
    // is the same for every fragment and the highlight is a single dot product
    // - the one place this projection makes something cheaper instead of harder.
    vec3 eye = normalize((INV_VIEW_MATRIX * vec4(0.0, 0.0, 1.0, 0.0)).xyz);
    vec3 half_v = normalize(normalize(sun) + eye);
    col += vec3(pow(max(dot(n, half_v), 0.0), gloss)) * glint;

    // Foam where the floor comes up to meet the surface - the shore, and every
    // wall of the pit. This is the half that needed depth and now has it.
    float lip = 1.0 - smoothstep(0.0, shore, thick);
    // And foam where a tank went through, off the band and the point it was
    // standing at - see Swell. Unchanged by any of this: it never needed depth.
    int cell = int(UV2.x + 0.5);
    float lv = clamp(state[cell] / max(bands - 1.0, 1.0), 0.0, 1.0);
    lv *= 1.0 - smoothstep(0.45 * foam_reach, foam_reach,
                           distance(world.xz, mark[cell]));
    // Shore and tank, and nothing else. Crest foam in open water was the first
    // cut and it read as scratches on the surface: the ridge fold makes thin
    // creases by design, so anything keyed straight off the height field picks
    // out lines rather than patches. The field is used to break the two real
    // sources up instead.
    float froth = clamp(max(lip, lv), 0.0, 1.0);
    float broken = smoothstep(0.30, 0.80, h / 1.75);
    col = mix(col, foam_ink, froth * broken * foam);

    ALBEDO = col;
    ALPHA = 1.0;
}}
";

    /// <summary>The plain board when there is no art, tinted by whatever the
    /// cell is wearing. Only ever seen on a run with no terrain on disk, which
    /// is a state the bench reaches cleanly on a fresh checkout.</summary>
    private static readonly Color SoilInk = new(0.47f, 0.55f, 0.35f);

    private Texture2D? GroundArt() =>
        Field.Terrain is not { Any: true } t ? null
            : t.Texture(t.Has(TerrainSet.Default) ? TerrainSet.Default
                                                  : TerrainSet.Plain);

    /// <summary>How far inside its own plate a top face stops sampling, in art
    /// pixels.
    ///
    /// <b>Measured, and it is the seam the overhang used to hide.</b> Sampling to
    /// the plate exactly, every cell came back framed in white: the art's hexagon
    /// is antialiased into a transparent surround, so the outermost texels are
    /// white at low alpha and a face that reaches its own boundary reads them. In
    /// 2D nothing does, because the cell in front is drawn over that rim - the
    /// overhang doing a job nobody had written down. At six the board reads as
    /// continuous ground with no cell boundary visible at all, which is better
    /// than the 2D board manages.</summary>
    private const float Bleed = 6.0f;

    private Vector2 GroundUV(Vector3 fromCentre)
    {
        if (Field.Terrain is not { Any: true } t || t.Plate.Size.X <= 0
            || t.Plate.Size.Y <= 0)
            return Vector2.Zero;
        // The corner offset as the 2D board would see it on screen: x straight
        // through, the ground row squashed by the camera.
        var screen = new Vector2(fromCentre.X, fromCentre.Z * Squash);
        float scale = t.ScaleTo(Field.Atlas!.HexRect);
        screen *= new Vector2(1.0f - 2.0f * Bleed / t.Plate.Size.X,
                              1.0f - 2.0f * Bleed / t.Plate.Size.Y);
        Vector2 plate = (Vector2)t.Plate.Position + (Vector2)t.Plate.Size * 0.5f;
        return (screen + plate * scale) / ((Vector2)t.Frame * scale);
    }

    /// <summary>
    /// The ring on the cell being driven, as a band of six quads lying on that
    /// cell's top face.
    ///
    /// <b>Geometry rather than a canvas item, and that is the whole reason it is
    /// here.</b> Anything left drawing in 2D lands on top of the entire 3D world,
    /// so a ring left as it was would ride over the tanks - and a mark that
    /// covers what it is pointing at is not a mark. On the ground it goes under
    /// them, which is where it has always claimed to be.
    ///
    /// Its far side disappearing behind the hull is not a side effect: it is what
    /// makes the ring read as lying on the ground rather than hovering over it.
    ///
    /// <b>Cut about its own origin and moved by <see cref="Place"/>, not built on
    /// a cell.</b> Built on <c>Selected.Cell</c> it sat on the last cell reached
    /// and a tank spends most of an order between two - so it trailed a whole
    /// cell behind the tank it was marking, which reads as the selection lagging.
    /// That is the same thing <see cref="SelectionRing"/> says in 2D, and it says
    /// it about the contact patch, which is what this is placed on now.
    /// </summary>
    private void BuildRing(Vector3[] corner)
    {
        if (Selected is null)
        {
            _ring.Mesh = null;
            return;
        }
        Vector3 top = Clear(Squash, RiseFactor);
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        // Dark, cyan, dark - the 2D ring's five-pixel backing under its two-pixel
        // line, as three strips of one mesh rather than two coplanar transparent
        // bands with nothing to order them. The lesson is the additive effect
        // layers': a colour chosen on the pale tile disappears on the amber one,
        // so the mark carries its own contrast rather than borrowing the
        // ground's - and the ground here is the route.
        foreach ((float from, float to, Color ink) in new[]
                 {
                     (Ring - Fringe, Ring - Line, SelectionRing.Under),
                     (Ring - Line, Ring + Line, SelectionRing.Friendly),
                     (Ring + Line, Ring + Fringe, SelectionRing.Under),
                 })
        for (int i = 0; i < 6; i++)
        {
            Vector3 a = corner[i], b = corner[(i + 1) % 6];
            foreach (Vector3 v in new[]
                     {
                         top + a * to, top + b * to, top + b * from,
                         top + a * to, top + b * from, top + a * from,
                     })
            {
                st.SetColor(ink);
                st.AddVertex(v);
            }
        }
        st.GenerateNormals();
        _ring.Mesh = st.Commit();
        _ring.MaterialOverride = Decal(RingOrder);
    }

    /// <summary>
    /// What everything painted on the ground is made of: unshaded, coloured per
    /// vertex, transparent, and sorted rather than depth-sorted.
    ///
    /// <b>The order has to be said rather than left to the depth buffer.</b> A
    /// tank tests depth but does not write it - which is what keeps its silhouette
    /// smooth - so two transparent surfaces cannot settle each other and the order
    /// is whatever the sort says. It said the ring, and the far arm was drawn
    /// straight across the hull.
    ///
    /// That it cannot settle them is also what lets these share one clearance off
    /// the ground: both test against the terrain and neither against the other, so
    /// two decals at the same height are ordered by this number and never by a
    /// coin toss. It is the same statement the 2D layers make with their z
    /// indices - ruts at -101 under a ring at -99 under everything standing on
    /// the ground.
    /// </summary>
    private static StandardMaterial3D Decal(int order) => new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        VertexColorUseAsAlbedo = true,
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        RenderPriority = order,
    };

    /// <summary>Where the ground marks sort, mirroring
    /// <see cref="SelectionRing.GroundZ"/> against
    /// <see cref="TrackMarks.MarksZ"/>: a rut is terrain and the ring is a marker
    /// laid over it. All under the tanks, which sort at the default.
    ///
    /// <b>The water is the top rung of the ladder and still under them</b>, which
    /// is the whole statement: a rut and a ring lie on the bottom, so the water
    /// goes over both, and a tank standing in it is drawn over the surface and
    /// tints its own submerged half - see <see cref="PaintShader"/>. Drawn over
    /// the tanks instead it would be right for the near half of its own cell and
    /// wrong for every tank in front of that cell, because one sort key is one
    /// answer for a whole surface.</summary>
    public const int WaterOrder = -1;
    public const int RingOrder = -2;
    public const int RutOrder = -3;

    /// <summary>
    /// The belt marks, as strips lying on the board.
    ///
    /// <b>Geometry rather than the canvas item that already draws them, for the
    /// ring's reason.</b> Anything left drawing in 2D lands on top of the entire
    /// 3D world, so the ruts would ride over the tanks that made them. On the
    /// ground they go under, which is where they have always claimed to be - and
    /// they are hidden by a hill standing in front of them, which the canvas item
    /// could not do at all.
    ///
    /// <b>Where the trail goes is still the trail's answer.</b> The points come
    /// smoothed, the shoes come laid across the belt as it was, the fade comes off
    /// the imprint's age - none of that is worked out again here. What this owns is
    /// one thing: the width is taken <b>on the ground</b> and the camera squashes
    /// it, where the 2D layer had to project it onto the screen itself and got
    /// 106px across a 21px belt the one time it was wrong. The same trade the ring
    /// made, and the reason neither keeps a width in screen pixels.
    ///
    /// <b>Two passes, ribbons then shoes</b>, because these are transparent and so
    /// do not settle each other by depth - the ladder has to be submitted after
    /// the soil it is pressed into. Same statement the 2D layer makes by calling
    /// DrawTrail before DrawShoes.
    /// </summary>
    private void BuildRuts(IReadOnlyList<Vehicle> vehicles)
    {
        var mesh = (ImmediateMesh)_ruts.Mesh;
        mesh.ClearSurfaces();
        if (Marks is null || !Marks.Enabled)
            return;
        var soil = new List<(Vector3 At, Color Ink)>();
        var shoes = new List<(Vector3 At, Color Ink)>();
        foreach (Vehicle vehicle in vehicles)
        for (int side = 0; side < 2; side++)
        {
            IReadOnlyList<TrackMarks.Imprint> trail = Marks.ImprintsOf(vehicle, side);
            Ribbon(trail, MarksAt, Squash, RiseFactor, soil);
            Shoes(trail, MarksAt, Squash, RiseFactor, shoes);
        }
        if (soil.Count == 0 && shoes.Count == 0)
            return;
        mesh.SurfaceBegin(Mesh.PrimitiveType.Triangles);
        foreach ((Vector3 at, Color ink) in soil)
        {
            mesh.SurfaceSetColor(ink);
            mesh.SurfaceAddVertex(at);
        }
        foreach ((Vector3 at, Color ink) in shoes)
        {
            mesh.SurfaceSetColor(ink);
            mesh.SurfaceAddVertex(at);
        }
        mesh.SurfaceEnd();
    }

    /// <summary>Where one imprint sits in the world: the drawn row with its lift
    /// put back on to get the flat row, then lifted clear of the face it lies on.
    /// The pair is what <see cref="World"/> warns about, and the one thing a caller
    /// here can get wrong.</summary>
    private static Vector3 Sit(Vector2 at, float lift, Vector2 shift,
                               float squash, float rise)
    {
        Vector2 flat = at + shift + new Vector2(0.0f, lift);
        return World(flat, lift, squash, rise) + Clear(squash, rise);
    }

    /// <summary>Horizontal and across a step of the path, in the ground plane. The
    /// step's own rise is dropped: a rut is a strip of ground, so its width lies in
    /// the ground however steep the face is.</summary>
    private static Vector3 Across(Vector3 step)
    {
        var flat = new Vector3(step.Z, 0.0f, -step.X);
        return flat.LengthSquared() <= 1e-12f ? Vector3.Zero : flat.Normalized();
    }

    /// <summary>
    /// The soil pressed flat: one continuous strip per run.
    ///
    /// <b>Per-point normals, averaged between the two steps that meet there</b>, so
    /// the strip is continuous by construction - no gap on the outside of a corner
    /// and, which matters more here, no overlap on the inside. These are
    /// transparent, so an overlap is a bright seam at every joint, and that is the
    /// failure a quad-per-segment version would have. The mitre is left
    /// uncompensated: at the 25 degrees the trail is already smoothed to it
    /// under-fills a corner by two percent.
    /// </summary>
    private static void Ribbon(IReadOnlyList<TrackMarks.Imprint> trail,
                               Vector2 shift, float squash, float rise,
                               List<(Vector3, Color)> into)
    {
        for (int start = 0; start < trail.Count;)
        {
            int end = start + 1;
            while (end < trail.Count && !trail[end].Break)
                end++;
            int n = end - start;
            if (n < 2)
            {
                start = end;
                continue;
            }
            var at = new Vector3[n];
            var side = new Vector3[n];
            for (int i = 0; i < n; i++)
                at[i] = Sit(trail[start + i].Rolled, trail[start + i].Lift,
                            shift, squash, rise);
            for (int i = 0; i < n; i++)
            {
                Vector3 back = i > 0 ? Across(at[i] - at[i - 1]) : Vector3.Zero;
                Vector3 on = i + 1 < n ? Across(at[i + 1] - at[i]) : Vector3.Zero;
                // A path that doubles straight back on itself has no mean - the two
                // normals cancel - so one of them stands in. It happens at the
                // apex of a reversal, where the ribbon is a fold anyway.
                Vector3 mean = back + on;
                if (mean.LengthSquared() <= 1e-12f)
                    mean = back.LengthSquared() > 1e-12f ? back : on;
                side[i] = mean.Normalized() * (trail[start + i].Ground * 0.5f);
            }
            for (int i = 0; i + 1 < n; i++)
            {
                Color a = Tint(TrackMarks.Ink, trail[start + i].Fade);
                Color b = Tint(TrackMarks.Ink, trail[start + i + 1].Fade);
                Quad(into, at[i] - side[i], at[i] + side[i],
                     at[i + 1] + side[i + 1], at[i + 1] - side[i + 1], a, a, b, b);
            }
            start = end;
        }
    }

    /// <summary>
    /// The ladder: one bar across the belt per link, lying on the ground.
    ///
    /// The bar comes stored as the shoe's half-vector in the harness's squashed
    /// space, so unsquashing it is all there is to do - and it is why the shoes fan
    /// out radially on a pivot without this knowing what a pivot is.
    ///
    /// Its thickness is <see cref="TrackMarks.BarThickness"/> read as ground units
    /// rather than screen pixels, which is the ring's trade again: a bar keeps its
    /// two pixels on the edges running up the screen and comes out at sin(e) of
    /// that across them, because that is what a mark painted on the ground does.
    /// </summary>
    private static void Shoes(IReadOnlyList<TrackMarks.Imprint> trail,
                             Vector2 shift, float squash, float rise,
                             List<(Vector3, Color)> into)
    {
        float half = TrackMarks.BarThickness * 0.5f;
        for (int i = 0; i < trail.Count; i++)
        {
            // The raw point, not the rolled one: the ladder is where the shoes bit
            // and is not smoothed - see TrackMarks.Imprint.
            Vector3 at = Sit(trail[i].At, trail[i].Lift, shift, squash, rise);
            Vector2 bar = trail[i].Bar;
            var arm = new Vector3(bar.X, 0.0f, bar.Y / Mathf.Max(squash, 0.0001f));
            if (arm.LengthSquared() <= 1e-12f)
                continue;
            // Along the shoe's own bar rather than along the path: on a pivot the
            // two disagree by ninety degrees, and the shoe is what was pressed in.
            Vector3 along = Across(arm) * half;
            Color ink = Tint(TrackMarks.BarInk, trail[i].Fade);
            Quad(into, at - arm - along, at + arm - along,
                 at + arm + along, at - arm + along, ink, ink, ink, ink);
        }
    }

    private static Color Tint(Color ink, float fade)
    {
        ink.A *= fade;
        return ink;
    }

    private static void Quad(List<(Vector3, Color)> into,
                             Vector3 a, Vector3 b, Vector3 c, Vector3 d,
                             Color ia, Color ib, Color ic, Color id)
    {
        into.Add((a, ia));
        into.Add((b, ib));
        into.Add((c, ic));
        into.Add((a, ia));
        into.Add((c, ic));
        into.Add((d, id));
    }

    // --- the wood ------------------------------------------------------------

    /// <summary>One billboard per planted tree, and the quad shape shared by
    /// every tree of a kind.</summary>
    private readonly Dictionary<PropNode, MeshInstance3D> _standing = new();
    private readonly Dictionary<(int Species, bool Mirrored), ArrayMesh> _stems = new();
    private int _sown = -1;

    /// <summary>
    /// The forest, as upright quads standing on the board.
    ///
    /// <b>Geometry rather than the canvas nodes that already draw it, for the
    /// ring's and the ruts' reason:</b> anything left drawing in 2D lands on top
    /// of the entire 3D world, so a wood left as it was rode over every tank on
    /// the board - and while the stage owned the ground the trees were simply
    /// switched off, which is a wooded cell drawn as a field.
    ///
    /// <b>What the wood decides is still the wood's.</b> Where a tree stands,
    /// which species it is, how it leans in the gust, how hard a blast shoved it
    /// and how far it has faded for the tank on its cell - all of that is
    /// <see cref="Grove"/>'s, computed on nodes that are hidden rather than gone.
    /// Two things are this side of the line, and they are the two the 2D layer had
    /// to do by hand:
    ///
    /// <b>Depth is the foot, and now it is the foot rather than a number derived
    /// from it.</b> In 2D each tree carried <c>ZIndex = round(Depth(foot))</c>,
    /// which is <see cref="HexField.Depth"/> written out because a canvas item has
    /// no other way to sort - and that rounding is why a tree and a tank on the
    /// same row needed a tie-break rule. Here the quad stands where the tree
    /// stands and the camera sorts it, with <see cref="MeshInstance3D.SortingUseAabbCenter"/>
    /// off so the key is the trunk's own point and not the middle of a crown that
    /// leans.
    ///
    /// <b>The lean is the transform, and it costs nothing.</b> A shear about the
    /// foot is what <see cref="PropNode.Bend"/> already is; the same statement in
    /// the world is one column of the basis, so a gust crossing 464 trees rebuilds
    /// no geometry at all. The <c>rise</c> in it is the only conversion: the lean
    /// is quoted in screen px of drift per screen px of height, and world height
    /// is screen height over <c>cos(e)</c>.
    ///
    /// <b>Alpha-blended, and a material per tree, which is the renderer's doing
    /// rather than a choice.</b> The fade is per tree and this bench runs on GL
    /// Compatibility, where <see cref="GeometryInstance3D.Transparency"/> - the
    /// per-instance answer - does nothing at all. Scissored alpha would let the
    /// trees write depth and settle each other in the buffer, but a scissor cannot
    /// fade: a tree at 0.35 would vanish outright, and that fade is the one thing
    /// keeping a tank visible in the woods. So they are transparent objects sorted
    /// by their own foot, which is exactly the rule the 2D layer sorted them by,
    /// and the sort's usual failure - intersecting geometry - cannot arise between
    /// billboards that do not touch.
    /// </summary>
    private void BuildTrees()
    {
        if (Field.Atlas is null)
            return;
        // A different wood, or none: Grove counts its own sowings, because the
        // tree count does not move when a re-roll moves every tree.
        int sown = Wood?.Sown ?? -1;
        if (sown != _sown)
        {
            Fell();
            if (Wood is not null)
                Sow();
            _sown = sown;
        }
        foreach ((PropNode tree, MeshInstance3D stem) in _standing)
        {
            stem.Transform = Rooted(tree);
            // The fade the wood set this frame. Read off the node rather than
            // pushed by whoever set it, for Selected's reason - the reveal, the
            // slider and the reset all write it.
            if (stem.MaterialOverride is StandardMaterial3D bark)
                bark.AlbedoColor = new Color(1.0f, 1.0f, 1.0f, tree.Modulate.A);
        }
    }

    private void Sow()
    {
        foreach (PropNode tree in Wood!.Trees)
        {
            (int, bool) kind = (tree.Species, tree.Mirrored);
            if (!_stems.TryGetValue(kind, out ArrayMesh? shape))
                _stems[kind] = shape = Stem(tree.Foot * tree.Pixels,
                                            tree.Size * tree.Pixels, RiseFactor);
            var stem = new MeshInstance3D
            {
                Mesh = shape,
                // Sorted by where the node is, which is the trunk's foot: the
                // same rule the tanks are sorted by and the same one the 2D
                // ZIndex stated. By its box it would be the middle of the crown,
                // and a crown leans.
                SortingUseAabbCenter = false,
                MaterialOverride = Bark(tree.Art),
            };
            AddChild(stem);
            _standing[tree] = stem;
        }
    }

    private void Fell()
    {
        foreach (MeshInstance3D stem in _standing.Values)
        {
            stem.Mesh = null;
            stem.MaterialOverride = null;
            stem.QueueFree();
        }
        _standing.Clear();
        _stems.Clear();
    }

    /// <summary>One tree's transform, from where it stands on this board.</summary>
    private Transform3D Rooted(PropNode tree) =>
        Trunk(Origin + tree.Ground, Field.LevelAt(tree.Cell) * Field.Lift,
              tree.Lean, Squash, RiseFactor);

    /// <summary>
    /// Where a tree stands and how far it is leaning, as its transform. Given the
    /// two camera terms rather than reading them off the field, for
    /// <see cref="Body"/>'s reason - so what it claims can be asserted without a
    /// board under it.
    ///
    /// <b>The foot's row is drawn lifted</b> - it came off <c>CellCentre</c> - so
    /// the flat row is that row with the lift put back on. The pair
    /// <see cref="World"/> warns about, and a tree on a plateau is where it goes
    /// wrong: read as a flat row it would stand a level too far up the board.
    ///
    /// Lifted clear of the face it stands on by <see cref="Clear"/>, because a
    /// quad's base row is coplanar with the ground under it and that is
    /// <c>hit_scar</c>'s coin toss again - and the nudge costs no screen pixels by
    /// construction.
    ///
    /// <b>The lean is a shear, and the <c>rise</c> is the whole of the
    /// conversion.</b> <see cref="PropNode.Bend"/> quotes it as screen px of drift
    /// per screen px of height above the foot; world height is screen height over
    /// <c>cos(e)</c>, so the basis column that carries height gains exactly
    /// <c>lean*cos(e)</c> of x. Nothing else in the matrix moves, which is why a
    /// gust crossing the whole wood rebuilds no geometry.
    /// </summary>
    public static Transform3D Trunk(Vector2 foot, float lift, float lean,
                                    float squash, float rise)
    {
        Vector3 at = World(foot + new Vector2(0.0f, lift), lift, squash, rise)
                     + Clear(squash, rise);
        var bend = new Basis(new Vector3(1.0f, 0.0f, 0.0f),
                             new Vector3(lean * rise, 1.0f, 0.0f),
                             new Vector3(0.0f, 0.0f, 1.0f));
        return new Transform3D(bend, at);
    }

    /// <summary>
    /// One kind of tree as a quad, upright in the plane the tank's standing half
    /// uses, with its origin at the foot.
    ///
    /// The rect is <see cref="PropNode"/>'s own: from <c>-Foot</c> across
    /// <c>Size</c>, in screen px, with y down the screen. World height is screen
    /// height over <c>cos(e)</c>, so the art keeps the height it is drawn at
    /// exactly - the mapping is the same one that makes the board project back
    /// onto the 2D bench's pixels.
    ///
    /// <b>The bottom edge may fall below the foot, and that is not a bug to
    /// clamp.</b> Art with transparent margin under the trunk has a Size taller
    /// than its Rise, and those rows are behind the ground here for the reason a
    /// tank's running gear was - which costs nothing, because they are the rows
    /// with nothing in them.
    /// </summary>
    public static ArrayMesh Stem(Vector2 foot, Vector2 size, float rise)
    {
        float left = -foot.X, right = size.X - foot.X;
        float top = foot.Y / rise, bottom = (foot.Y - size.Y) / rise;

        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        (Vector3 At, Vector2 Uv)[] corner =
        {
            (new Vector3(left, top, 0.0f), new Vector2(0.0f, 0.0f)),
            (new Vector3(right, top, 0.0f), new Vector2(1.0f, 0.0f)),
            (new Vector3(right, bottom, 0.0f), new Vector2(1.0f, 1.0f)),
            (new Vector3(left, bottom, 0.0f), new Vector2(0.0f, 1.0f)),
        };
        foreach (int k in new[] { 0, 1, 2, 0, 2, 3 })
        {
            st.SetNormal(Vector3.Back);
            st.SetUV(corner[k].Uv);
            st.AddVertex(corner[k].At);
        }
        return st.Commit();
    }

    /// <summary>What a tree is painted with: the set's own art, unshaded because
    /// it arrived lit, and mipmapped because it is painted at four times the size
    /// it is drawn at - <see cref="PropNode._Ready"/>'s reason, unchanged. One per
    /// tree, because the fade is per tree; see <see cref="BuildTrees"/>.</summary>
    private static StandardMaterial3D Bark(Texture2D art) => new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        AlbedoTexture = art,
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmaps,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
    };

    /// <summary>Where the line runs, as a fraction of the cell, and the two
    /// half-widths about it - <see cref="SelectionRing.Inset"/>'s 0.86 and its
    /// 2px line inside a 5px backing, in the cell's own units so the whole thing
    /// is one statement of the same shape. Off the cell, never off the tank: it
    /// says where this tank stands, and a ring that grew with the class would be
    /// a second claim about size arguing with the one the tanks make.
    ///
    /// <b>Those widths are on the ground, and the camera squashes the ground.</b>
    /// A band 2 units wide keeps its 2px across the edges that run up the screen
    /// and comes out at <c>sin(e)</c> of that - one pixel - on the two that run
    /// across it, which is what a stripe painted on the ground does and what the
    /// 2D ring, drawn on the screen at a flat 2px, cannot do. Measured against
    /// it on a flat board: the same colour to a level (76,216,255 against
    /// 76,217,255) in the same 218x73 box to a pixel, over 433 fully saturated
    /// pixels rather than 1030.</summary>
    private const float Ring = 0.86f;
    private const float Line = 0.008f;
    private const float Fringe = 0.020f;

    /// <summary>
    /// Where the cell outline's inner edge runs, as a fraction of the cell - so
    /// the band is the rim itself and everything inside it is ground.
    ///
    /// <b>Inward, never outward.</b> Grown past the hexagon it would hang over
    /// whatever is beyond, which on a stepped board is the top of an earth wall
    /// or the cell below it, and a boundary drawn on the wrong cell is worse
    /// than none.
    ///
    /// <b>On the ground, so the camera thins it - and that is the point.</b>
    /// 0.02 of the circumradius is 2.5 units on the two edges that run up the
    /// screen and <c>sin(e)</c> of that, 1.3, on the four that run across it,
    /// which is what a line painted on the ground does. The same statement
    /// <see cref="Line"/> makes, and the reason neither is in screen pixels.
    /// </summary>
    private const float Edge = 0.980f;

    /// <summary>The band on a raised cell's top face - three times
    /// <see cref="Edge"/>'s. The plateau's top wears the same dirt as the
    /// floor, so on the faces the camera sees (the walls behind a plateau
    /// face away from it) the rim band is most of what marks the ground as
    /// higher; at the floor's 2px it read as just another cell.</summary>
    private const float RaisedEdge = 0.940f;

    /// <summary>
    /// The outline's ink, and it carries its own contrast rather than borrowing
    /// the ground's - the additive effect layers' lesson, and the selection
    /// ring's before it. This one lies on grass, on whatever terrain art is
    /// loaded and on the plain <see cref="SoilInk"/> of a run with no art at
    /// all, and dark is the only one of those three it is dark against.
    ///
    /// Low alpha because the mark is a boundary, not a wall: at full strength
    /// the board reads as a stack of tiles, which is the failure
    /// <see cref="Bleed"/> was measured into avoiding and is not worth undoing
    /// to answer a different question.
    /// </summary>
    private static readonly Color EdgeInk = new(0.05f, 0.04f, 0.03f, 0.30f);
}
