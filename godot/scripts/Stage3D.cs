using System;
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

    /// <summary>The tank that one is acting on, or null - the red mark. A field
    /// beside <see cref="Selected"/> rather than a colour on it, because both
    /// statements are true at once and about different tanks; the flat board
    /// says the same thing with a second <see cref="SelectionRing"/>.</summary>
    public Vehicle? Quarry;

    /// <summary>The cell the interface has picked, or null - the violet mark.
    /// A cell and not a tank, because what is picked is ground: the events
    /// bench shells a hex, and on a hex there may be nothing standing.</summary>
    public Vector2I? Chosen;

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
    private MeshInstance3D _flank = null!;
    private MeshInstance3D _glaze = null!;

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

    /// <summary>Which cells are burning, for the ash on the ground. The trees'
    /// own share of the fire is read off the nodes, the way the fade and the lean
    /// are - this is here because the ground is the stage's and the ash is the
    /// ground's.</summary>
    public Wildfire? Blaze;

    /// <summary>What the ground has been blown open by, or null for a board that
    /// keeps no marks. Read rather than owned, for <see cref="Marks"/>'s reason:
    /// what has been dug is a fact about the board and this is the thing that
    /// draws it.</summary>
    public Craters? Pits;

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

        /// <summary>How far under this tank has gone, nought to one - see
        /// <see cref="TankTick.SunkAt"/> - and what that does to the billboard,
        /// see <see cref="Shrunk"/>. Held for two reasons: the quad's scale is
        /// only written when it changes, and <see cref="Screen"/> has to
        /// unproject through the same shrunk billboard the eye is looking
        /// at.</summary>
        public float Sink;
        public float Small = 1.0f;

        /// <summary>The air this tank lets go of while it drowns - made on the
        /// first frame it is under and kept, because a bench raises and sinks
        /// the same hull more than once. Null for a tank that has never
        /// drowned, which is every tank on a dry board.</summary>
        public Bubbles? Air;

        /// <summary>Whether the settled seat of the air has been printed once -
        /// one line per drowning, for the captures to be read against.</summary>
        public bool AirTold;
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
        // An ImmediateMesh for the ruts' reason, arrived at from the other side:
        // the ring is cut about the tank rather than about a cell, and now that
        // each of its corners takes the height of the ground under itself, the
        // shape changes as the tank crosses a slope and not only its position.
        _ring = new MeshInstance3D { Mesh = new ImmediateMesh() };
        _edges = new MeshInstance3D { Visible = _showEdges };
        // An ImmediateMesh rather than an ArrayMesh rebuilt through a SurfaceTool:
        // the trail grows, fades and is forgotten from the front, so this is the
        // one piece of the board's geometry that genuinely changes every frame,
        // and that is what the type is for. The 2D layer it replaces rebuilds the
        // same polylines every frame too, so the cost is not new.
        _ruts = new MeshInstance3D { Mesh = new ImmediateMesh() };
        _pond = new MeshInstance3D();
        _flank = new MeshInstance3D();
        // Over the board and over the tanks - see Glaze. Added last of the board's
        // own nodes for the same reason its material carries a rung: a transparent
        // surface is sorted, and the sort is the thing being decided here.
        // An ImmediateMesh for the ruts' reason: the cut follows the tank's own
        // frame, so it moves every frame a tank does, and an ArrayMesh rebuilt
        // through a SurfaceTool at that rate is a mesh allocated every frame.
        _glaze = new MeshInstance3D { Mesh = new ImmediateMesh() };
        AddChild(_tops);
        AddChild(_sides);
        AddChild(_flank);
        AddChild(_glaze);
        AddChild(_ruts);
        AddChild(_ring);
        AddChild(_pond);
        AddChild(_edges);
        _ruts.MaterialOverride = Decal(RutOrder);
        _ring.MaterialOverride = RingDecal();
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
        // see FrameClock.FixedStep for what that cost.
        delta = FrameClock.FixedStep ?? delta;
        if (Field.Atlas is null)
            return;
        Aim();
        // Rebuilt only when what a cell wears changes, which is a route being
        // given or a lane going up. Fifty-four prisms is nothing to build; sixty
        // times a second for a board that has not changed is still nothing worth
        // spending.
        Standing();
        // Outside the gate too, and for the swell's reason: the switch is not
        // part of what the board is made of, so folding it into the signature
        // would rebuild every prism to answer a checkbox - and leaving it in
        // Build alone would leave the ground lit while the wood's shadows had
        // already gone.
        Tell("shade", CastShadows ? ShadowInk.A : 0.0f);
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
        // The wood's fire, on the bench's step for the pond's reason: a flame
        // running on wall-clock time makes every screenshot of a burning wood a
        // different picture, and every A/B across one measures the difference.
        _fireClock = Mathf.PosMod(_fireClock + (float)delta, 3600.0f);
        // The bursts, on the same step as everything else here - delta was
        // pinned at the top of this method, so a capture of one is repeatable
        // for the pond's reason.
        foreach (SheetBlast blast in _booming)
            blast.Tick(delta);
        foreach (ProcKick kick in _kicking)
            kick.Tick(delta);
        // And the dust behind the belts, which is the same cloud out of the
        // other ring - see Drift. A ring that is filled has to be ticked.
        foreach (ProcKick drift in _drifting)
            drift.Tick(delta);
        foreach (ProcSlam slam in _slamming)
            slam.Tick(delta);
        foreach (ProcSpall spall in _spalling)
            spall.Tick(delta);
        // And the collisions, which are the same effect out of the other ring -
        // see Emit. A pool that is filled and never ticked is an effect that is
        // fired and never drawn, and the two lines have to be added together.
        foreach (ProcSpall spall in _scraping)
            spall.Tick(delta);
        foreach (ProcRack rack in _racking)
            rack.Tick(delta);
        foreach (ProcWave wave in _waving)
            wave.Tick(delta);
        // The screens with them, though a screen is not a burst: what it shares
        // with the eight above is that its picture must be the same picture on two
        // runs of the same flags, and that is what the pinned step buys.
        foreach (ProcScreen veil in _screening)
            veil.Tick(delta);
        foreach (ToonBlast toon in _tooning)
            toon.Tick(delta);
        foreach (ProcBall ball in _balling)
            ball.Tick(delta);
        foreach (SheetBlast spout in _spouting)
            spout.Tick(delta);
        foreach (Plunge spray in _plunging)
            spray.Tick(delta);
        foreach (Stand stand in _stands.Values)
            stand.Air?.Tick(delta);
        foreach (SheetBlast shard in _splintering)
            shard.Tick(delta);
        Etchings();
        Markers();
        Soot();
        _deepInk?.SetShaderParameter("foam", Mathf.Max(0.0f, Foam));
        PushSwell();
    }

    private void Aim()
    {
        float zoom = Mathf.Max(Eye.Zoom.X, 0.0001f);
        _camera.Size = GetViewport().GetVisibleRect().Size.Y / zoom;
        // Position AND offset: the offset is where the camera shake lives
        // (CameraShake -> Camera2D.Offset), and a stage aimed off the position
        // alone stood still while the 2D camera shook - which on a board where
        // the stage draws everything meant no shake at all. Found when a
        // detonation's shake, on and measured in the spring, was not on screen.
        Vector3 pivot = World(Eye.Position + Eye.Offset, 0.0f);
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
    /// <b>The depth buffer still takes its cut, and it is not the surface that
    /// takes it.</b> The plane writes no depth, as this argument requires - but
    /// the bed of the cell in front does, and a hull drawn lower than it counts
    /// as standing loses to it. Nothing draws a hull lower than its own cell any
    /// more - a drowning is size and not position, see
    /// <see cref="TankTick.SunkAt"/> - and the reading it used to cost, a
    /// submerged half missing rather than dim, is that fault and not this cut
    /// going wrong.
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
    /// <summary>
    /// The crumple every foam edge is displaced by, as GLSL shared verbatim
    /// between the surface and the sprite.
    ///
    /// <b>It displaces the edge rather than dimming it, and that is the whole of
    /// it.</b> The surface already multiplied its foam by the swell's own ridge
    /// field, which breaks a band up into patches and leaves the band exactly
    /// where it was - so a shore that runs along a hexagon's edge stays a
    /// straight line with holes in it, and reads as the cell boundary showing
    /// through the water. Pushing the distance instead moves the line itself, and
    /// a wandering line says nothing about the grid underneath it.
    ///
    /// <b>Displacement is a fraction of the band, never a length.</b> The three
    /// edges are measured in two different spaces - the shore in world units, the
    /// collar and the lap in the sprite's own pixels - so a crumple in either unit
    /// would have to be converted at two of the three call sites, and the
    /// conversion is exactly the kind of second copy that parts company. As a
    /// fraction it needs no units at all: one dial sets how ragged every edge is,
    /// and each edge stays as ragged as it is wide.
    ///
    /// <b>Sampled in world XZ at all three, including on the armour.</b> The
    /// sprite is a billboard and knows only its own pixels, but the line it draws
    /// lies on the water plane by definition - that is what a waterline is - so
    /// the plane's own height closes the projection and the fragment can be put
    /// back where it is in the world. Without that the tank's half of the line
    /// would crumple to a different rhythm than the water's half against it, and
    /// the two would part at the silhouette, which is the one place they are seen
    /// touching.
    ///
    /// Its own clock, handed in, for the reason everything here has one: TIME is
    /// the wall clock and --capture pins the step so two runs agree.
    /// </summary>
    internal const string FoamEdge = @"
float hash12(vec2 p) {
    uvec2 q = uvec2(ivec2(p)) * uvec2(1597334677u, 3812015801u);
    uint n = (q.x ^ q.y) * 1597334677u;
    return float(n) * (1.0 / 4294967295.0);
}

float vnoise(vec2 p) {
    vec2 i = floor(p);
    vec2 f = fract(p);
    vec2 u = f * f * (3.0 - 2.0 * f);
    return mix(mix(hash12(i), hash12(i + vec2(1.0, 0.0)), u.x),
               mix(hash12(i + vec2(0.0, 1.0)), hash12(i + vec2(1.0, 1.0)), u.x),
               u.y);
}

// Two octaves drifting apart, signed, roughly in -1..1. Not folded like the
// swell: a fold makes creases, and a crease in an edge is a spike. What an edge
// wants is a lobe - a tongue of foam reaching further in one place than the
// next - which is what plain noise gives.
float crumple(vec2 p, float t) {
    vec2 drift = vec2(0.21, -0.13) * t;
    return (vnoise(p + drift) - 0.5) * 1.30
         + (vnoise(p * 2.17 - drift * 1.40) - 0.5) * 0.60;
}

// The swell, and only the swell: two folded octaves, drifting apart. The fold
// is what separates water from cloud - smooth noise has round hills, a surface
// held by its own tension has creases between them - and the per-octave drift
// is what stops the field sliding as one sheet.
//
// Its clock and speed come in as arguments rather than off a uniform, which is
// the whole reason it lives here: the sprite has to evaluate this same field to
// tear its own half of the waterline, and it is a different material with a
// different set of uniforms.
float height(vec2 p, float t, float speed) {
    float h = 0.0;
    float a = 1.0;
    float f = 1.0;
    vec2 drift = vec2(0.35, 0.20) * t * speed;
    for (int i = 0; i < 2; i++) {
        float n = vnoise(p * f + drift);
        h += a * (1.0 - abs(2.0 * n - 1.0));
        a *= 0.5;
        f *= 2.03;
        drift = vec2(-drift.y, drift.x) * 1.7;
    }
    return h;
}

// And what that field does to foam: tears it into patches. Every foam edge in
// the bench is multiplied by this, so a band is never a solid stripe.
//
// <b>Displacement and tearing are two jobs and this is the second.</b> Tearing
// alone leaves the band where it was and only punches holes in it - which is
// what drew hexagons along the shore. Displacement alone gives a wandering line
// of even weight, which reads as piping. Foam is both, and it has to be the same
// two everywhere or the halves of one line do not match.
//
// Sampled at the fragment, unlike the crumple: a multiplier cannot fold a band,
// and taken on the line instead it would come out as streaks combed down the
// column.
float shred(vec2 xz, float t, float scale, float speed) {
    return smoothstep(0.30, 0.80, height(xz * scale, t, speed) / 1.75);
}
";

    internal const string PaintShader = @"
shader_type spatial;
render_mode unshaded, blend_premul_alpha, depth_draw_never, cull_disabled{0};
uniform sampler2D picture : source_color, filter_nearest;
// The waterline table where the set carries a height map, the groundline
// where it does not - see AtlasSet.Waterline. The shader cannot tell which,
// and does not have to: whichever it is, shore_cut.x is the lift that turns
// its row into the line, and a table that is already the line hands over
// nought.
uniform sampler2D shore : filter_nearest, repeat_disable;
uniform vec4 pond = vec4(0.0);
// anchor.x, anchor.y, class scale, the render target's size
uniform vec4 shore_map = vec4(0.0);
// how far the water stands over the table's own row in board px, this
// heading's row of the table, the tile row the ground sits on, and the tile's
// last row
uniform vec4 shore_cut = vec4(-1.0);
// The lift for a column the table has no answer for, or negative where such a
// column is simply dry.
//
// <b>Two tables fail for two reasons, and one fallback served both wrongly.</b>
// The groundline has no answer where the column has no silhouette bottom - the
// gun swung out past the tracks - and the flat cut leaves that dry for free,
// because the gun is drawn far above it. The waterline has no answer where the
// column's own surface stands above the water plane, which is a hull column
// like any other: the flat cut runs straight through it and tints and foams a
// column with no water in it. That is not a rare fallback either - it is nearly
// every column in shallow water. Measured on MTP's map: of 103 inked columns
// on the side heading, 0 answer at 2px of water, 16 at 5px, 42 at 10px, 98 at
// 15px, all at 20px. So a tank putting its nose in read as a dead straight bar
// across the nose plate at the cell centre's own row, ending hard at the plate's
// edges - which is what the cut being one row does, said in the one place the
// per-column table was still allowed to do it.
//
// The water's own half already refuses it: shoreline() returns -1 for a column
// with no entry rather than reaching for a flat line. This is the sprite's half
// saying the same thing, which is what it means for the two halves not to be two
// answers.
uniform float shore_flat = 0.0;
// The turret's own foot and crown, one row each per column per turret frame
// (r the foot, g the crown), and which row of the table to read - negative on
// a hull that has no turret layer.
//
// <b>The line may never stand above the foot until the water does.</b> The
// table above is the hull's, and a column the water covers whole answers row
// nought - the top of the tile, which on an isometric board is above the near,
// lower edge of a turret standing in front of the deck's far rim. Cut there,
// the water crossed the middle of the turret. So the line is held at the foot,
// less however many rows the surface stands over the deck (turret_over): a
// swimmer's water never passes its deck and its turret is dry to the foot; a
// drowning hull's surface climbs the turret's near face from the foot up, and
// once the line would stand above the column's crown the column is under
// entire and the table's nought stands. Clamping here rather than in the table
// is what lets the foot be this turret's at the angle it is actually pointing:
// the table is built per hull frame and the turret does not turn with it.
//
// A max and not a swap, so it costs nothing anywhere else: in a ford the line
// is far below the foot and keeps its own answer, and a column with no turret
// in it keeps the row the hull gave it - which is the column the gun swings
// out over, and why the gun needs no rule of its own.
uniform sampler2D turret_foot : filter_nearest, repeat_disable;
uniform float turret_row = -1.0;
// Where the gun is drawn, one bit per pixel of the tile, and which heading of
// it to read - negative on a hull with no gun layer. See AtlasSet.GunMask.
//
// <b>The one thing here that is a shape and not a row, because the gun cannot
// be a row.</b> A tube pointing at the camera runs down the screen at one
// height above the water, so in its columns wet-below-the-line is true about
// the hull and false about the tube at the same moment. Said with the foot it
// is wrong either way round: left out, the tube is tinted where it crosses the
// hull; unioned in, the column goes dry to the tracks and the tank wears a pale
// stripe. Said as a shape it is simply exact - these fragments are not water's
// to touch, and neither is the foam that sits on the line.
uniform sampler2D gun_layer : filter_nearest, repeat_disable;
// Where that frame sits in the packed atlas - xy its corner, zw its size - and
// where its corner sits in the tile. Negative width says there is no gun on
// screen: a turret blown off takes the tube with it, and a tube nobody is
// drawing must not exempt the armour it would have covered.
uniform vec4 gun_box = vec4(0.0, 0.0, -1.0, -1.0);
uniform vec2 gun_off = vec2(0.0);
// How far the water stands over the deck, in tile rows as the tile is drawn -
// nought until the surface passes the deck, which a hull afloat or in a ford
// never sees. The turret and nothing else reads it: see turret_foot.
//
// <b>Rows as drawn, and that is the whole of the drowning's arithmetic.</b> A
// drowning hull is drawn smaller about its contact point (Stage3D.Shrunk) and
// the water is not, so a tile row is worth less and less board and the surface
// climbs the tile on its own as the tank recedes. The table above is asked at
// that same board-per-row (see its caller), so every column of the hull floods
// by height at the size it is drawn, and there is no second line laid over the
// measurement: going under and being under are one number.
uniform float turret_over = 0.0;
// How high the turret's roof stands over the deck, in the same rows - see
// AtlasSet.TurretRisePx - or huge where nobody measured it. The turret is
// under once turret_over passes this: its roof is flat and at one height, so
// the water reaches all of it at once, and the row test that floods its near
// face from the foot up would otherwise leave the far rim of the roof - drawn
// higher than it stands, by its distance behind the centre - dry in water
// that is over it.
uniform float turret_rise = 1e9;
// How far under it has got, nought to one - see TankTick.SunkAt. The gun and
// nothing else reads it.
//
// <b>The one term here that is a fade rather than a measurement, and the tube
// is why.</b> Everything else in this shader is answered per row because a row
// is a height; a tube pointing at the camera runs down the screen at one
// height, so it has no row and the water arrives at all of it at one moment.
// Popping the whole tube from dry to wet on one frame is the worst of the three
// pictures, and a row test is the very thing it cannot take - so its tint comes
// in over the drowning's own clock. Nought while the hull is holding itself up,
// which is the exemption the tube is owed and gets.
uniform float sink = 0.0;
// How much of the water's colour a wet fragment takes: the pond's own tint at
// the surface, deeper as the hull goes down - see Stage3D.Fathom. One number
// rather than pond.a so that the surface's tint stays what every other reader
// of it thinks it is.
uniform float murk = 0.66;
// The foam lapping at the hull: rgb the water's own foam colour, a its level,
// so the pond's dial and --no-foam take this with them rather than leaving a
// white line on a tank standing in water that has stopped foaming.
uniform vec4 foam_lap = vec4(0.0);
uniform float lap_band = 2.5;
// The pond's own foam clock, handed in rather than taken from TIME - the rule
// the water already obeys, and for the same reason: --capture pins the step so
// that two runs are comparable, and a shader reading the wall clock makes every
// A/B near a wading tank measure the hour it was taken at.
uniform float lap_time = 0.0;
// What it takes to put a fragment of the armour back in the world, so that the
// crumple on this half of the line is the same field as the crumple on the
// water's half: the sprite's own position in the harness's 2D px, the camera's
// two terms, and the height of the water plane. The plane's height is what makes
// it solvable at all - a billboard fragment is a ray, and the waterline is by
// definition the one place that ray is known to meet the water.
uniform vec4 lap_world = vec4(0.0);
uniform float lap_plane = 0.0;
// How fine the crumple is in world units, how far it pushes the line as a
// fraction of the band, and the swell's own scale and speed for the tearing.
// All four shared with the surface: a second set would be a second answer about
// how ragged one line is.
uniform vec4 lap_crumple = vec4(0.05, 0.45, 0.02, 1.0);
// <b>Refraction: what is under the water is not the same picture, tinted.</b>
// Below the line the armour is read through a moving surface, so its picture
// is sampled displaced - by the same crumple field the foam and the shore
// wander by, so the armour swims to the pond's own rhythm - and squeezed
// toward the line, which is the fold a leg makes at the surface of a pool.
// The ripple field (wash, the strikes and the wake) adds its own slope on
// top, so a ring crossing a sunk hull visibly bends it. docs/swim-plan.md,
// step 12.
//
// refract.x is the switch and strength (nought on land and in a ford: the
// ford's picture is pinned pixel for pixel by the bench), .y the rows under
// the line over which the effect grows from nothing - it must be nought at
// the line, or the collar tears from the armour - .z the crumple's push in
// tile px per unit of the swell's height (about ±0.6 about its mean), .w the
// squeeze as a fraction of the depth.
uniform vec4 refract = vec4(0.0, 12.0, 3.0, 0.12);
uniform float refract_wash = 0.0;
// The swell as the refraction reads it, against the glints' own: .x scales
// its features up (a half is waves twice as long), .y slows its clock. Both
// below one because the user asked for a slower shiver twice: a picture under
// water should sway with the long swell, not tick with every glint.
uniform vec2 refract_pace = vec2(0.5, 0.6);
uniform sampler2D wash : filter_linear, repeat_disable, hint_default_black;
uniform vec4 wash_box = vec4(0.0, 0.0, 1.0, 1.0);
{1}
// The pond's displacement at a point on it, as the water's own shader reads
// it (see washed there) - nought off the field's box.
// The picture read between its texels: a fragment moved by a fraction of a
// pixel through the nearest sampler flickers between two texels a frame, and
// under water that read as static, not as water. Done by hand from four
// nearest reads rather than through a second, linear sampler on the same
// texture: on the compatibility renderer the filter is the texture's, not
// the sampler's, and the linear one leaked into the dry half of every tank
// (the ford's pinned picture moved by 3.6k px, out/swim/32-ford-diff.png).
vec4 soft(vec2 uv) {{
    vec2 size = vec2(textureSize(picture, 0));
    vec2 at = uv * size - 0.5;
    vec2 f = fract(at);
    vec2 base = (floor(at) + 0.5) / size;
    vec2 step = 1.0 / size;
    vec4 a = texture(picture, base);
    vec4 b = texture(picture, base + vec2(step.x, 0.0));
    vec4 c = texture(picture, base + vec2(0.0, step.y));
    vec4 d = texture(picture, base + step);
    return mix(mix(a, b, f.x), mix(c, d, f.x), f.y);
}}
float washed_at(vec2 p) {{
    vec2 uv = (p - wash_box.xy) / max(wash_box.zw, vec2(1e-4));
    if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0)
        return 0.0;
    return texture(wash, uv).x;
}}
void fragment() {{
    vec4 c = texture(picture, UV);
    ALPHA = c.a;
    ALBEDO = c.rgb;
    if (shore_cut.x >= 0.0) {{
        float scale = max(shore_map.z, 0.0001);
        vec2 tile = (UV * shore_map.w - shore_map.w * 0.5) / scale + shore_map.xy;
        // Below every row of the tile, so that a column the table cannot
        // answer is dry: no tint, no foam. Not a flag, because the file's own
        // convention for an absent number is a negative one - see shore_cut.
        float bottom = shore_flat < 0.0 ? 1e9 : shore_cut.z - shore_flat / scale;
        if (shore_cut.y >= 0.0) {{
            vec4 s = texture(shore, vec2((tile.x + 0.5)
                                         / float(textureSize(shore, 0).x),
                                         shore_cut.y));
            if (s.a > 0.5)
                bottom = s.r * shore_cut.w - shore_cut.x / scale;
        }}
        // The line as measured and nothing laid over it: the table was asked
        // at the lift a tile row is worth as drawn (see turret_over), so a
        // drowning hull floods by height, column by column, as a fording one.
        float line = bottom;
        // The turret out of the water as far as the water has not got - see
        // turret_foot: held at the foot less what stands over the deck while
        // the water is below its roof (turret_rise), and let go - the table's
        // own nought stands - once the water is over the roof, or once the
        // held line would stand above the column's own crown.
        if (turret_row >= 0.0 && turret_over < turret_rise) {{
            vec4 f = texture(turret_foot,
                             vec2((tile.x + 0.5)
                                  / float(textureSize(turret_foot, 0).x),
                                  turret_row));
            if (f.a > 0.5) {{
                float rim = f.r * shore_cut.w - turret_over;
                if (rim >= f.g * shore_cut.w)
                    line = max(line, rim);
            }}
        }}
        // The gun, out of the water whatever the line says - until the hull it
        // is bolted to goes under with it. The foam is skipped for it either
        // way: a collar painted across the tube would be foam standing on
        // nothing.
        bool gun = false;
        if (gun_box.z > 0.0) {{
            vec2 g = floor(tile) - gun_off;
            if (g.x >= 0.0 && g.y >= 0.0
                && g.x < gun_box.z && g.y < gun_box.w)
                gun = texture(gun_layer,
                              (gun_box.xy + g + 0.5)
                              / vec2(textureSize(gun_layer, 0))).a > 0.03;
        }}
        float wet = tile.y > line ? 1.0 : 0.0;
        if (gun)
            wet *= sink;
        // Under the line the picture is read through the water - see refract.
        // The line itself is not moved: it was measured on the picture as
        // drawn, and it is the sample that is displaced, growing from nothing
        // at the line so that the foam collar still sits on armour.
        if (wet > 0.0 && refract.x > 0.0) {{
            float under = tile.y - line;
            float deep = smoothstep(0.0, refract.y, under) * refract.x;
            vec2 here = (tile - shore_map.xy) * scale + lap_world.xy;
            vec2 sea = vec2(here.x, (here.y + lap_plane * lap_world.w)
                                    / max(lap_world.z, 0.0001));
            // <b>The swell, and not the crumple</b>: the glints on the pond are
            // the swell's (height, at the pond's own scale and speed), and a
            // picture under the water has to move with the thing the eye sees
            // moving on top of it. The first cut used the crumple - the foam
            // edge's field, a different rhythm - and the user saw the armour
            // shiver out of step with the glints. Read twice, offset, for the
            // two directions; centred on the swell's mean so the rest is push.
            float pace = lap_crumple.w * refract_pace.y;
            float zoom = lap_crumple.z * refract_pace.x;
            vec2 push = vec2(height((sea + vec2(11.0, 0.0)) * zoom, lap_time, pace) - 0.9,
                             (height((sea + vec2(0.0, 17.0)) * zoom, lap_time, pace) - 0.9) * 0.6)
                        * refract.z;
            // And the ripple field's slope: a ring passing over bends what is
            // under it the way a lens does. Read over a wide step, because the
            // field carries the pops' own small fast rings as well, and a
            // narrow gradient of those was the second half of the shiver.
            if (refract_wash > 0.0) {{
                float e = 7.0;
                push += vec2(washed_at(sea + vec2(e, 0.0)) - washed_at(sea - vec2(e, 0.0)),
                             washed_at(sea + vec2(0.0, e)) - washed_at(sea - vec2(0.0, e)))
                        * refract_wash;
            }}
            // Squeezed toward the line: a row this deep shows a row deeper.
            vec2 moved = vec2(tile.x + push.x * deep,
                              line + under * (1.0 + refract.w * deep) + push.y * deep);
            vec2 uv2 = ((moved - shore_map.xy) * scale + shore_map.w * 0.5) / shore_map.w;
            c = soft(uv2);
            ALPHA = c.a;
            ALBEDO = c.rgb;
        }}
        if (wet > 0.0)
            ALBEDO = mix(c.rgb, pond.rgb * c.a, murk * wet);
        // And the foam at that line, drawn here because these pixels belong to
        // the sprite and to nothing else. The pond cannot put it here: it is
        // behind the tank, and a surface drawn before an opaque one cannot show
        // through it. Asymmetric on purpose - foam sits on the water and licks a
        // little way up the paint, so most of the band is on the wet side.
        //
        // It ripples along the hull, and that is what stops it reading as a
        // stripe painted on the tank: the tank stands still and the water does
        // not, so a line that is perfectly straight and perfectly still is the
        // one thing this cannot be.
        //
        // <b>The water's own crumple and not a pair of sines.</b> Two sines along
        // tile.x were the first cut and they are wrong twice over. They run along
        // the sprite, so the ripple is pinned to the hull and swims with it
        // instead of staying in the water; and they are a different field from
        // the one the surface crumples its shore and its collar by, so the half
        // of this line on the paint and the half on the water beside it wandered
        // to two different rhythms and parted at the silhouette - the one place
        // the two halves are seen touching. Put back in the world, sampled from
        // the shared field, they are one line.
        //
        // At the line's own row and not at this fragment's, which is the same
        // rule the surface follows and for the same reason: read at the fragment
        // the push varies across the band as well as along it, the band's width
        // becomes a function of the field, and where the gradient reaches one the
        // whole thing folds into hard teeth. On the line it is constant across
        // the band by construction.
        vec2 flat_px = (vec2(tile.x, line) - shore_map.xy) * scale
                       + lap_world.xy;
        vec2 sea = vec2(flat_px.x, (flat_px.y + lap_plane * lap_world.w)
                                   / max(lap_world.z, 0.0001));
        float wob = crumple(sea * lap_crumple.x, lap_time) * lap_band
                    * lap_crumple.y;
        float d = tile.y - (line + wob);
        float lap = d >= 0.0 ? 1.0 - smoothstep(0.0, lap_band, d)
                             : 1.0 - smoothstep(0.0, lap_band * 0.4, -d);
        // And torn by the swell, exactly as every foam edge on the water is. Left
        // out, this half alone was solid while the half beside it was in patches,
        // and no width or colour would have made them one line: the surface dims
        // its foam by this field everywhere, so a band that skips it is simply
        // the brightest thing in the pond. Read at the fragment's own place, not
        // at the line's - see shred.
        vec2 spot = (tile - shore_map.xy) * scale + lap_world.xy;
        lap *= shred(vec2(spot.x, (spot.y + lap_plane * lap_world.w)
                                  / max(lap_world.z, 0.0001)),
                     lap_time, lap_crumple.z, lap_crumple.w);
        // Premultiplied, like the tint above it: this pass blends that way, and
        // an unmultiplied colour here would glow through the sprite's own edge.
        ALBEDO = mix(ALBEDO, foam_lap.rgb * c.a,
                     gun ? 0.0 : clamp(lap, 0.0, 1.0) * foam_lap.a);
    }}
}}
";

    /// <summary>The same shader with the depth test off, for the legs where the
    /// tank is drawn over the ground - see <see cref="Place"/>. Two shaders rather
    /// than a uniform because the test is a render mode: it is compiled in, not
    /// set.</summary>
    private static readonly Shader Tested = new()
    {
        Code = string.Format(PaintShader, "", FoamEdge),
    };

    private static readonly Shader Free = new()
    {
        Code = string.Format(PaintShader, ", depth_test_disabled", FoamEdge),
    };

    private static ShaderMaterial Paint(Texture2D picture, bool overGround)
    {
        var ink = new ShaderMaterial { Shader = overGround ? Free : Tested };
        // A tank stands on the board, so it shares the trees' rung and settles
        // against them by depth. Said rather than left at the default, because
        // the rung below it now has a tier on it.
        ink.RenderPriority = StandOrder;
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
        _flank.Mesh = null;
        _flank.MaterialOverride = null;
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
            stand.Air?.QueueFree();
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
    internal static Vector3 Clear(float squash, float rise) =>
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
    /// <c>Footing.SeamLine</c> in the quad's own screen frame, and the trailing
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
    /// <summary>
    /// Where a pixel of a tank's picture is drawn on screen.
    ///
    /// <b>What the stage owes any overlay that wants to point at part of a
    /// tank.</b> Staged, a sprite is reparented into a SubViewport of its own and
    /// shown on a quad, so <c>Sprite.ToGlobal</c> - which is what every
    /// measurement over a tank's pixels comes off, the muzzle and the plate
    /// included - answers in that viewport's own 512px space. Read as screen space
    /// those numbers put a mark in the corner of the window: present, confident
    /// and about nothing.
    ///
    /// The answer is not an approximation, because the billboard is not a mystery:
    /// <see cref="Body"/> builds it out of the picture's own rows, so there is an
    /// exact point of the mesh under every pixel and <see cref="OnBody"/> is that
    /// map read backwards. The camera then says where that point lands.
    ///
    /// Null when the stage cannot say, which is two cases and one answer. The
    /// tank may have no stand at all - mid-switch, or before the stage has taken
    /// it - and a caller that drew anyway would be drawing out of a viewport
    /// space that is not there yet. Or the stand may exist while the pixel handed
    /// in still belongs to the harness's canvas: <see cref="Place"/> runs in the
    /// stage's own _Process, so on the frame a tank is taken the sprite has not
    /// been moved into its viewport yet and <c>ToGlobal</c> answers a thousand
    /// pixels off the 512 the billboard covers. <b>That case has to be refused
    /// here rather than clamped or extrapolated</b>: the camera hands back NaN
    /// for it, and a NaN travels - it failed every comparison a caller could
    /// write, reached Godot's own dashed-line drawing as a negative dash count
    /// and took the process down with an illegal instruction. One frame of no
    /// answer is the whole cost.
    ///
    /// <paramref name="run"/> walks the answer along the ground from that pixel,
    /// in the harness's own flat px. <b>It has to go through the camera rather
    /// than be added to the screen point afterwards</b>, and that is the whole
    /// reason it is a parameter here instead of the caller's business: the
    /// stage's orthographic size is the viewport height over the zoom (see
    /// <see cref="Aim"/>), so one world unit is one screen pixel at 1.00x and
    /// nowhere else. Added on afterwards, a run is a fixed screen length over a
    /// board that scales - which reads as a line whose length depends on the
    /// zoom rather than on the ground it covers, and that is exactly what it was
    /// reported as. No lift on it, because the line it belongs to is level.
    /// </summary>
    public Vector2? Screen(Vehicle vehicle, Vector2 paint, Vector2 run = default)
    {
        if (!_stands.TryGetValue(vehicle, out Stand? stand))
            return null;
        if (paint.X < 0.0f || paint.Y < 0.0f
            || paint.X > PaintSize || paint.Y > PaintSize)
            return null;
        // The cached cut rather than a fresh one, and that is what caching it is
        // for: the inverse has to invert the mesh that is on screen this frame,
        // not one recomputed from figures that may since have moved. Lead, Drop
        // and Line are exactly what Body was last called with.
        // Through the billboard as it is drawn, which on a drowning hull is
        // a shrunk one - see Sunken. The run is not on the billboard at all: it
        // is a length on the ground, and the ground does not sink.
        Vector3 local = OnBody(paint, stand.Lead, Squash, RiseFactor,
                               stand.Drop, stand.Line)
                        * stand.Small
                        + World(run, 0.0f);
        Vector2 at = _camera.UnprojectPosition(stand.Quad.Position + local);
        return float.IsFinite(at.X) && float.IsFinite(at.Y) ? at : null;
    }

    /// <summary>
    /// Which point of the billboard a pixel of the picture is drawn at, in the
    /// quad's own frame - <see cref="Body"/> read backwards.
    ///
    /// Every line of it is one of Body's own lines solved the other way, and the
    /// two sit next to each other for that reason: an inverse kept anywhere else
    /// is a second description of the same surface, and the day the billboard
    /// gains a fold the copy goes on answering about the surface before it.
    /// <b>That is not left to be believed</b> - the check reads the mesh Body
    /// actually built, feeds each vertex's own UV back through here and requires
    /// the vertex back.
    ///
    /// The climb slide is the one branch: a point is slid only if it fell on the
    /// trailing side of the seam, and the test for that is the very expression
    /// Body split the polygon with rather than a restatement of it.
    /// </summary>
    internal static Vector3 OnBody(Vector2 paint, Vector2 lead, float squash,
                                   float rise, float drop, Vector3 line)
    {
        float half = PaintSize * 0.5f;
        float ax = -lead.X, ay = lead.Y / rise;
        float seam = (half + lead.Y) / PaintSize;
        float reach = (half - lead.Y) / squash;
        float top = ay + half / rise;

        float v = paint.Y / PaintSize;
        bool standing = v <= seam;
        // u runs across both faces the same way, so x is the same solve on either
        // side of the seam; only the second coordinate changes meaning - height up
        // the standing half, ground run out along the lying one.
        var p = new Vector2(
            ax - half + paint.X,
            standing ? top * (1.0f - v / Mathf.Max(seam, 1e-6f))
                     : reach * (v - seam) / Mathf.Max(1.0f - seam, 1e-6f));
        float f = standing
            ? line.X * p.X - line.Y * p.Y * rise - line.Z
            : line.X * p.X + line.Y * p.Y * squash - line.Z;
        float d = Mathf.Abs(drop) < 0.25f || f >= 0.0f ? 0.0f : drop;
        var slide = new Vector3(0.0f, d / rise, d / squash);
        return standing
            ? slide + new Vector3(p.X, p.Y, 0.0f)
            : slide + Clear(squash, rise) + new Vector3(p.X, 0.0f, p.Y);
    }

    internal static ArrayMesh Body(Vector2 lead, float squash, float rise,
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

    /// <summary>
    /// Whether the stage draws this tank over the ground instead of depth-testing
    /// it against it - see the rule and its measurements in <see cref="Place"/>.
    ///
    /// <b>Here rather than at its one use, because the harness prints it.</b> The
    /// readout beside the lean says "over" or "depth" and is read off a capture to
    /// judge the picture; spelled a second time there it would be a copy of this
    /// condition that nothing makes agree with it.
    /// </summary>
    public static bool DrawnOver(Vehicle vehicle) =>
        vehicle.OnSlope;

    /// <summary>Every tank's holder and quad, from where it stands this
    /// frame.</summary>
    public void Place(IReadOnlyList<Vehicle> vehicles)
    {
        var centre = new Vector2(PaintSize * 0.5f, PaintSize * 0.5f);
        _wearing.Clear();
        foreach (Vehicle vehicle in vehicles)
        {
            // <b>Before the stand, because a hull without one still burnt the
            // ground.</b> Asked here rather than off a hook - see Scald - and
            // asked of the wreck rather than of the frame it died on, so it is
            // right on a board that was resumed or reset.
            if (vehicle.Wreck.Dead)
                Scald(vehicle);
            else
                _scalded.Remove(vehicle);
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
            // disagree the most: the sprite stands vertically at one point of a
            // face that is rising through it, so everything drawn below that point
            // sits at the contact's own depth, and any surface nearer the camera
            // than the contact takes the tank's lower body - the near half of the
            // ramp it is on, or the flat cell it is about to drive down onto.
            //
            // <b>It was narrowed to "parked on a slope" and put back, and the
            // measurement is why.</b> OnSlope && !Moving was tried on the strength
            // of a leg on the events board - LTP driving (7,4) -> (7,6) into the
            // ravine - where the old condition laid 1549, then 3696, then 5173 px
            // of hull and track over the ground in front and let go of all of it in
            // one frame at parking. Narrowed, the leg came back pixel-for-pixel
            // honest, and honest was the wrong picture: the ground doing the cutting
            // was the ravine floor at level -1, a level *below* the tank standing on
            // the ramp above it. The board's own rule says that cannot happen - the
            // self test asserts "ground below a tank never covers it" over every
            // cell of every board - so those 5173 px were the stage keeping the
            // rule, not breaking it, and the narrowed build was reported broken on
            // the first ramp it was driven down.
            //
            // <b>So the depth buffer is not the authority here, and this switch is
            // not a workaround for it.</b> Occluders knows that a cell below a tank
            // never hides it; a depth buffer cannot know it, because a billboard has
            // no thickness and its lower pixels are simply at the contact's depth.
            // Turning the test off for a tank on a slope is how the stage says the
            // thing the field already says.
            //
            // <b>A flat leg at one level keeps the test</b>, which is the other half
            // of the same rule and the thing the stage exists to show: a tank
            // driving behind a ridge on its own level stays behind it. So this is
            // not "moving", it is "on a slope" - the two coincided on the board's
            // first three ramps and come apart everywhere else.
            //
            // <b>Levelling came out of the condition and nothing moved</b>, which is
            // the one part of that attempt worth keeping. Every change of level goes
            // through a ramp and OnSlope asks IsRamp of both ends of the leg, so
            // Levelling implied it and the || carried no term of its own; the probe
            // agrees, reading lev=False slope=True through the whole of the leg
            // above. What the old condition costs is therefore still on the table
            // and still unpaid: a tank on a leg that changes levels is drawn over
            // everything, the honest mid-leg occlusion there is a billboard crossing
            // a volume, the crown promotion snapped at parking, and the descent wipe
            // shed a couple of hundred pixels a frame at cruise.
            //
            // Swapped rather than switched, because the depth test is a render
            // mode and a render mode is compiled in - see Paint.
            //
            // <b>Not a hull afloat in deep water, and it was tried.</b> The pond
            // is a pit a level deep, and a card standing in a pit loses to every
            // bank that is higher than the pixel being drawn; the drowned hull in
            // the near row was hidden to its turret, and for one round swimmers
            // and drowned were drawn free of the test like a hull on a leg. The
            // user read the result as a bug straight away (docs/swim-plan.md
            // step 10): the hull's near flank and its foam collar lay on the
            // bank's ground where the bank should have cut them. The bank
            // *is* higher than the waterline, so the cut is the honest picture -
            // a hull in a pit behind a bank - and the drowned one hiding behind
            // the near bank is the same honesty. Swimmers keep the test.
            // What is standing in front of this tank, asked once and used twice:
            // the hull is drawn over it, and it is laid back over the hull at half
            // strength - see Glaze, which is the other half of this line.
            List<Vector2I> covering =
                Field.Occluders(vehicle.FlatRow, vehicle.Height, vehicle.Box);
            // Once per cell however many tanks are behind it: laid twice, a half
            // over a half is three quarters, and the whole of this is that a half
            // is a half.
            // One cell, and the one that is actually standing in front of this
            // hull - see Nearest.
            if (Nearest(vehicle, covering) is Vector2I front
                && !_wearing.Contains(front))
                _wearing.Add(front);
            bool over = DrawnOver(vehicle) || covering.Count > 0;
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
            Vector2I wet = Field.CellAt(vehicle.GroundPoint - Origin);
            // How far under a drowning hull has got, and the billboard shrunk
            // about its own contact point to say so - see Shrunk. The descent
            // to the drawn bed is the tick's (TankTick.Afloat); the size says
            // the rest of the depth, because a screen row here is a place on
            // the ground and rows spent past the bed walk the tank off its
            // hexagon.
            //
            // Written on a change rather than every frame, for the mesh's own
            // reason above - and nought is the whole board's usual answer, so
            // this costs a comparison per tank and nothing else.
            float sink = TankTick.SunkAt(vehicle.Wreck.Disabled,
                                         vehicle.Wreck.Dead, Field.IsDeep(wet),
                                         vehicle.Wreck.OutAge);
            bool turreted = vehicle.Sprite.ShowTurret && !vehicle.Sprite.Turretless;
            float contact = atlas.HexRect.Position.Y + atlas.HexRect.Size.Y * 0.5f;
            if (Mathf.Abs(sink - stand.Sink) > 1e-4f)
            {
                stand.Sink = sink;
                // The tank's own height against the water over the drawn bed:
                // the turret's roof or the hull's crest, whichever stands
                // higher (AtlasSet.RoofRisePx) - measured heights, the aerial
                // and the gun's tip left out, and not the drawn crown: the
                // drawn top is the far rim's, higher than any height by its
                // distance behind the centre, and measured against that a
                // medium ended at half its size. A drowning hull goes down to
                // the drawn bed at its own size (TankTick.Afloat), and where the
                // water there is as deep as the tank is tall this comes out at
                // one and nothing shrinks - the events pond at its default bed
                // against all five classes (HexField.DeepBed). What is left to
                // shrink is a tank taller than the water is deep, by exactly the
                // excess, so that the water is over the last of it by
                // measurement. docs/swim-plan.md, step 9.
                double roof = turreted ? atlas.RoofRisePx : atlas.HullCrestPx;
                stand.Small = Shrunk(sink,
                                     (roof > 0.0 ? (float)roof : atlas.DrawnHeight)
                                     * scale,
                                     Field.WaterTop(wet) - Field.BedAt(wet));
                stand.Quad.Scale = Vector3.One * stand.Small;
            }
            // The air coming up off it - Bubbles, docs/swim-plan.md step 11.
            // Seated every frame while it drowns, because the hull is still
            // going down to the bed; ticked with the bursts in Tick. Made once
            // and kept: the bench raises and sinks the same hull again.
            if (sink > 0.0f || stand.Air is { Busy: true })
            {
                if (stand.Air is null)
                {
                    stand.Air = new Bubbles();
                    AddChild(stand.Air);
                    stand.Air.Build((ulong)vehicle.GetHashCode());
                    stand.Air.Struck = Wash is null ? null
                        : (at, might) => Wash.Strike(at, might);
                }
                // <b>Over the deck as drawn, not over the footprint.</b> The
                // surface over the hull's ground footprint is the physical
                // answer, and it was tried twice: on GroundPoint, which the
                // drowning descent (TankTick.Settle) carries down on to the hex
                // in front, and on the cell's centre, which is right on the
                // ground and still 25-30 px under the picture of the deck -
                // read off the screenshot as pops in front of the tank, twice.
                // In isometry the eye puts a thing over the tank where the
                // tank's picture is, so the seat is the hull's drawn deck: its
                // ground point less most of its crest, and it follows the
                // sprite down as the hull settles. The x is the cell's, so a
                // hull leaning off its anchor does not carry its air with it.
                Vector2 midCell = Origin + Field.CellCentre(wet);
                float deck = midCell.Y - (float)atlas.HullCrestPx * scale * DeckSeat;
                if (sink >= 1.0f && !stand.AirTold)
                {
                    stand.AirTold = true;
                    GD.Print($"stage: air off {vehicle.Tag} settled: crest "
                             + $"{atlas.HullCrestPx:F0}px x{scale:F2}, ground row "
                             + $"{vehicle.GroundPoint.Y:F0}, cell row "
                             + $"{Origin.Y + Field.CellCentre(wet).Y:F0}, seat row {deck:F0}");
                }
                stand.Air.Sit(new Vector2(midCell.X, deck),
                              Field.WaterTop(wet),
                              atlas.GroundDirection(vehicle.Sprite.HullFacing),
                              atlas.HullSpan * scale * 0.5f,
                              atlas.HullSpan * scale * 0.5f * 0.55f,
                              Squash, RiseFactor);
                stand.Air.Feed(sink);
            }
            Color tint = WaterTintAt(wet);
            ink.SetShaderParameter("pond", tint);
            ink.SetShaderParameter("sink", sink);
            // And how much of that colour the armour takes, which is the second
            // thing the drowning says with one number: at the surface the
            // water's own tint, and by the bottom of it Fathom - a hull under
            // water is behind water, and the size alone was read off the
            // snapshot as a small tank standing on the pond rather than a tank
            // in it.
            ink.SetShaderParameter("murk", Mathf.Lerp(tint.A, Fathom, sink));
            // What is under the water is read through it - refract in the
            // shader. Deep water only: a ford is two or three rows of water
            // over the tracks and its picture is the bench's pixel-pinned
            // invariant; and the effect wants depth to grow into anyway.
            bool refracting = Refraction > 0.0f && vehicle.Wading && Field.IsDeep(wet);
            ink.SetShaderParameter("refract", new Godot.Vector4(
                refracting ? Refraction : 0.0f, RefractDepth, RefractPush, RefractSqueeze));
            ink.SetShaderParameter("refract_pace", RefractPace);
            if (refracting && Wash is { Enabled: true, Sheet: not null })
            {
                ink.SetShaderParameter("wash", Wash.Sheet);
                ink.SetShaderParameter("wash_box", new Godot.Vector4(
                    Wash.Box.Position.X, Wash.Box.Position.Y,
                    Wash.Box.Size.X, Wash.Box.Size.Y));
                ink.SetShaderParameter("refract_wash", RefractWash);
            }
            else
                ink.SetShaderParameter("refract_wash", 0.0f);
            // Which table, and what it still owes: both of the height map's
            // are already the waterline and are handed nought to lift them by,
            // the groundline's is where the tank meets the bottom and is handed
            // the depth. One call site for all three, so a set with a map and a
            // set without differ in the table they carry and in nothing else.
            //
            // <b>The scan, in deep water as in a ford, live or drowned, at the
            // size the tile is drawn.</b> Waterline - Height is the water over
            // the contact point in board px; a tile row is worth BodyScale of
            // those at full size and BodyScale times Small on a drowning hull,
            // so the dip the map is read at grows as the tank recedes and every
            // column floods by height, exactly as it does in a ford. There is
            // no second line for the drowning: the single row that used to be
            // laid over an unshrunk measurement flooded a casemate's roof for
            // being drawn high rather than for being low. A set without a map
            // is handed the same lift per row for its groundline's sake. One
            // table, one measurement, for every hull in any water at any size.
            // docs/swim-plan.md, steps 2, 5 and 9.
            float lift = vehicle.Wading ? vehicle.Waterline - vehicle.Height : 0.0f;
            float small = Mathf.Max(stand.Small, 1e-4f);
            float dip = lift / Mathf.Max(scale * small, 1e-4f);
            Texture2D? table = !vehicle.Wading ? null : atlas.Waterline(dip);
            ink.SetShaderParameter("shore", table ?? atlas.Groundline);
            ink.SetShaderParameter("shore_map", new Godot.Vector4(
                atlas.Anchor.X, atlas.Anchor.Y, scale, PaintSize));
            ink.SetShaderParameter("shore_cut", new Godot.Vector4(
                !vehicle.Wading ? -1.0f : table is not null ? 0.0f : lift / small,
                (table ?? atlas.Groundline) is null ? -1.0f
                    : (atlas.FrameFor(vehicle.Sprite.HullFacing) + 0.5f)
                      / Mathf.Max(atlas.Count, 1),
                contact,
                Mathf.Max(atlas.Tile.Y - 1, 1)));
            // The fallback for a column with no answer, or -1 where there is
            // none to be had: with the height map, no answer means the water
            // never reaches that column - see the uniform's own note.
            ink.SetShaderParameter("shore_flat",
                                   table is not null ? -1.0f : lift / small);
            // And how far that water stands over the deck, in the same rows,
            // which is the turret's whole question - see the uniform. Nought
            // without a height map, which is a set with no deck to count from:
            // its turret is held dry to the foot as it always was.
            ink.SetShaderParameter("turret_over",
                atlas.DeckHeightPx > 0.0
                    ? Mathf.Max(0.0f, dip - (float)atlas.DeckHeightPx) : 0.0f);
            // And how high that turret's roof stands over the deck - see the
            // uniform - or huge where the set cannot say, so the foot holds.
            ink.SetShaderParameter("turret_rise",
                atlas.TurretRisePx > 0.0 && atlas.DeckHeightPx > 0.0
                    ? (float)Math.Max(0.0, atlas.TurretRisePx - atlas.DeckHeightPx)
                    : 1e9f);
            // And the turret's foot, at the angle the turret is actually
            // pointing - see the uniform. Set beside the table rather than with
            // the material, for the table's own reason: the row moves whenever
            // the turret does.
            ink.SetShaderParameter("turret_foot", atlas.TurretFoot);
            ink.SetShaderParameter("turret_row",
                atlas.TurretFoot is null || atlas.Count <= 0 ? -1.0f
                    : (atlas.FrameFor(vehicle.Sprite.TurretFacing) + 0.5f)
                      / atlas.Count);
            // And the gun's own silhouette, which is the one thing here that
            // is a shape rather than a row - see the uniform. The frame is the
            // very one the sprite drew, asked for by name
            // (AtlasSet.GunFrame) rather than composed a second time here: a
            // gun exempted at the wrong elevation is a tube-shaped dry patch
            // beside the tube.
            int shown = vehicle.Sprite.ShowTurret && !vehicle.Sprite.Turretless
                ? atlas.GunFrame(vehicle.Sprite.TurretFacing,
                                 vehicle.Sprite.RecoilPhase,
                                 vehicle.Sprite.BarrelRung)
                : -1;
            Rect2 tube = shown < 0 ? new Rect2()
                : atlas.Region(AtlasSet.BarrelName, shown);
            ink.SetShaderParameter("gun_layer",
                shown < 0 ? null : atlas.Texture(AtlasSet.BarrelName));
            ink.SetShaderParameter("gun_box", shown < 0
                ? new Godot.Vector4(0.0f, 0.0f, -1.0f, -1.0f)
                : new Godot.Vector4(tube.Position.X, tube.Position.Y,
                                    tube.Size.X, tube.Size.Y));
            ink.SetShaderParameter("gun_off", shown < 0 ? Vector2.Zero
                : atlas.OffsetOf(AtlasSet.BarrelName, shown));
            // The water's own foam colour and level, so this line and the pond's
            // are one dial: a tank still wearing a white collar in water that
            // has stopped foaming would be the reading nobody could explain.
            // And out with the drowning, because there is no line left to
            // lap at: the collar sits where the water meets the armour, and by
            // the end of a sink the armour is under all of it. The water's own
            // half goes with it - see the rows below, which fade to the
            // refusal shoreline() already reads.
            ink.SetShaderParameter("foam_lap", new Godot.Vector4(
                FoamInk.R, FoamInk.G, FoamInk.B,
                vehicle.Wading ? Mathf.Max(0.0f, Foam) * (1.0f - sink) : 0.0f));
            ink.SetShaderParameter("lap_band", LapBand);
            // <b>The surface's clock and not the strip's.</b> This half of the
            // line is displaced and torn by the same two fields the water's half
            // is, and a field is only the same field if it is read at the same
            // moment: on _foamClock the armour crumpled to one rhythm and the
            // collar an inch away to another, which is the parting this whole
            // arrangement exists to prevent. _foamClock stays what it was for -
            // the sheet water, which has no computed surface to agree with.
            ink.SetShaderParameter("lap_time", _deepClock);
            // What the sprite needs to put a fragment of its own armour back in
            // the world, so that its half of the waterline crumples to the same
            // field as the water's half beside it. The sprite's position and not
            // GroundPoint, for the reason the collar takes the same: the contact
            // patch is the anchor already moved by the offset these rows are
            // counted from.
            ink.SetShaderParameter("lap_world", new Godot.Vector4(
                vehicle.Sprite.Position.X, vehicle.Sprite.Position.Y,
                Squash, RiseFactor));
            // The water plane's own height in world units - the third equation,
            // and the only reason a billboard fragment can be placed at all.
            ink.SetShaderParameter("lap_plane", vehicle.Wading
                ? Field.WaterTop(wet) / RiseFactor : 0.0f);
            ink.SetShaderParameter("lap_crumple", new Godot.Vector4(
                EdgeCrumple.X, EdgeCrumple.Y, Swell2D.X, Swell2D.Y));
        }
        // And the waterline round each hull that is in the water. Gathered here
        // because this is where the tanks are, and pushed even when none of them
        // is wading - a radius left standing is a ring of foam round a patch of
        // water nobody is in.
        if (_deepInk is not null)
        {
            var at = new Vector2[Collars];
            // Two vec2 and not one vec4: a vec4 uniform array is quietly not
            // accepted here, the trap that once left the wake never arriving.
            var pivot = new Vector2[Collars];
            var size = new Vector2[Collars];
            var line = new float[Collars * HullLine];
            var hem = new float[Collars * HullLine];
            var dip = new float[Collars];
            var run = new Vector2[Collars];
            var push = new float[Collars];
            for (int i = 0; i < line.Length; i++)
            {
                line[i] = -1.0f;
                hem[i] = -1.0f;
            }
            int worn = 0;
            foreach (Vehicle vehicle in vehicles)
            {
                if (worn >= Collars || !vehicle.Wading)
                    continue;
                AtlasSet set = vehicle.Atlas;
                float wetPx = (vehicle.Waterline - vehicle.Height)
                              / Mathf.Max(vehicle.Sprite.BodyScale, 1e-4f);
                // Deep water is ruled to the deck and a ford is scanned for,
                // exactly as the sprite's own half chooses - the two halves of
                // one line have to be one measurement, so they read the same
                // table off the same question. Asked of the cell, so a tank
                // half out of a pond is still the pond's.
                bool deep = Field.IsDeep(
                    Field.CellAt(vehicle.GroundPoint - Origin));
                // How far under it has got, asked exactly as the sprite's half
                // asks it - one expression, so the two halves of one line cannot
                // part on the way down either. The table is the scan for every
                // hull in any water, as the sprite's half chooses - see the
                // shore table above.
                float under = TankTick.SunkAt(
                    vehicle.Wreck.Disabled, vehicle.Wreck.Dead, deep,
                    vehicle.Wreck.OutAge);
                // And how small it is drawn because of it, which this half has
                // to know as well: the collar is mapped from the world into the
                // tile by the tank's own scale, and a collar drawn at the size
                // the hull is not any more is a ring standing off it.
                float small = under <= 0.0f
                              || !_stands.TryGetValue(vehicle, out Stand? shrunk)
                    ? 1.0f : shrunk.Small;
                // Asked as drawn, exactly as the sprite's half asks it: the
                // table's dip is the water over the contact point per tile
                // row, and a row of a drowning hull is worth Small of what it
                // was - see the shore table above.
                float dipPx = wetPx / Mathf.Max(small, 1e-4f);
                bool mapped = set.Waterline(dipPx) is not null;
                if (!mapped && set.Groundline is null)
                    continue;
                // The sprite's own anchor, in the harness's 2D px - the space the
                // groundline is measured in, and the one the shader puts a
                // fragment of the water back into. Not GroundPoint: that is the
                // contact patch, which is the anchor already moved by the very
                // offset the table's rows are counted from.
                // The anchor as it is *drawn*, which parts from the sprite's
                // own position on a drowning hull and on nothing else: the
                // billboard shrinks about the contact point, so the anchor
                // comes down toward it by whatever the drowning has taken.
                at[worn] = vehicle.GroundPoint
                           - set.GroundOffset * vehicle.Sprite.BodyScale * small;
                pivot[worn] = set.Anchor;
                size[worn] = new Vector2(vehicle.Sprite.BodyScale * small,
                                         set.Tile.X);
                int frame = set.FrameFor(vehicle.Sprite.HullFacing);
                // Which way the turret is pointing, which is not the hull's
                // frame and is what the foot has to be read at.
                int spun = set.FrameFor(vehicle.Sprite.TurretFacing);
                // The same table and the same lift the sprite cuts its own tint
                // at, so the half of the line that lies on the armour and the
                // half that lies on the water are one measurement and cannot
                // part. Taking the groundline's own row was drawing this half
                // where the tank meets the bottom, a whole water below the
                // other; most of that hid behind the hull, which is why it read
                // as stray white beside the tank rather than as a line in the
                // wrong place.
                dip[worn] = mapped ? 0.0f : dipPx;
                // How far the water stands over the deck, in tile rows as
                // drawn - the turret's question, asked once per hull and
                // exactly as the sprite's half asks it (turret_over).
                float over = set.DeckHeightPx > 0.0
                    ? Mathf.Max(0.0f, dipPx - (float)set.DeckHeightPx) : 0.0f;
                float rise = set.TurretRisePx > 0.0 && set.DeckHeightPx > 0.0
                    ? (float)Math.Max(0.0, set.TurretRisePx - set.DeckHeightPx)
                    : 1e9f;
                // How hard this one is pushing, against the ford's own ceiling
                // and not against its top speed: what a tank can do in water is
                // what full push through water means, and normalising by a speed
                // it cannot reach here would leave every bow wave at two fifths
                // of itself for a reason nothing on screen could show.
                float shove = Mathf.Clamp(
                    (float)(vehicle.Speed
                            / Mathf.Max(vehicle.Profile.WaterSpeed, 1e-4)),
                    0.0f, 1.0f);
                push[worn] = shove;
                run[worn] = set.GroundDirection(vehicle.Sprite.HullFacing)
                            * shove;
                for (int i = 0; i < HullLine; i++)
                {
                    int column = Mathf.RoundToInt(
                        i * (set.Tile.X - 1) / (float)(HullLine - 1));
                    int row = !mapped ? set.GroundlineAt(frame, column)
                        : set.WaterlineAt(frame, column);
                    // Clamped out of the turret exactly as the sprite's half
                    // is - see PaintShader's turret_foot, and TurretLine for
                    // that block as one expression - so that the two halves
                    // of one line stay one line all the way under.
                    //
                    // <b>With one thing this half has to say and that one
                    // does not: where the armour ends.</b> The sprite tints
                    // fragments and there are none above the silhouette, so a
                    // line run off the top of it costs nothing there; this
                    // line is drawn on the water beside the tank, and carried
                    // above the turret's crown it is a bar of foam laid across
                    // open water - which is what it drew, straight and the
                    // width of the hull, the first time the two were the same
                    // expression. So a column the water has passed the top of
                    // answers nought, which is this table's own word for a
                    // column under water entire and is what shoreline()
                    // already refuses on.
                    float ruled = row < 0 ? row
                        : TurretLine(row, set.TurretFootAt(spun, column),
                                     set.TurretCrownAt(spun, column), over, rise);
                    line[worn * HullLine + i] = ruled;
                    hem[worn * HullLine + i] = set.GroundlineAt(frame, column);
                }
                worn++;
            }
            _deepInk.SetShaderParameter("hull_at", at);
            _deepInk.SetShaderParameter("hull_pivot", pivot);
            _deepInk.SetShaderParameter("hull_size", size);
            _deepInk.SetShaderParameter("hull_line", line);
            _deepInk.SetShaderParameter("hull_hem", hem);
            _deepInk.SetShaderParameter("hull_dip", dip);
            _deepInk.SetShaderParameter("hull_band", HullBand);
            _deepInk.SetShaderParameter("hull_run", run);
            _deepInk.SetShaderParameter("hull_push", push);
            _deepInk.SetShaderParameter("bow_on", Bows ? 1.0f : 0.0f);
        }
        // The mark goes where the tank touches the ground, every frame, for the
        // reason the 2D ring follows the contact patch: a tank spends most of an
        // order between two cells, and a mark on the cell behind it reads as the
        // selection lagging.
        BuildRing();
        // Every frame, and not only when a tank moves: the trail fades by age and
        // is forgotten from the front, so a mesh left standing is a trail that
        // stopped weathering the moment the tank parked.
        BuildRuts(vehicles);
        // The same, and for a nearer reason: the wood leans in a gust that is
        // always moving, so a frame that skipped this is a frame the wind stopped
        // in.
        BuildTrees();
        // And what the tanks have in the air, which is here rather than at a
        // call of its own because a round belongs to the tank that fired it -
        // Vehicle.Rounds - and this is already the pass that puts everything
        // those tanks are on the board.
        Shades(vehicles);
        Glaze();
    }

    /// <summary>
    /// The cells standing in front of a tank, laid a second time and see-through.
    ///
    /// <b>The tank is already drawn over them, and that is what makes this one
    /// node instead of a rebuild of the board.</b> On a slope the stage turns the
    /// depth test off - see <see cref="DrawnOver"/> - so the hull is painted over
    /// the ground that ought to hide it, which is the complaint. Laying the same
    /// hexagon again on the rung above the tanks puts the ground back on top of it
    /// at half strength: the cell reads as a cell, the hull reads through it.
    ///
    /// <b>Nothing is taken out of the board, and that is the trick.</b> Where the
    /// glaze covers no tank it is a cell blended over itself, which comes out as
    /// the cell at any alpha - so there is no hole to see the void through, no
    /// seam against the neighbours, and no mesh to rebuild with a cell missing.
    /// Its material is the board's own, off the same shader source, for exactly
    /// this reason - see <see cref="Glass"/>.
    ///
    /// <b>Which cells is the field's answer, not one worked out here.</b>
    /// <see cref="HexField.Occluders"/> is what the flat mode has always painted
    /// from, it is asked with the tank's own row and box - see Vehicle.FlatRow -
    /// and it already keeps the one rule a depth buffer cannot: ground below a
    /// tank never covers it, so a pit in front of a tank never wears this.
    /// </summary>
    /// <summary>
    /// Whether these two cells share an edge - the glaze's own reach.
    ///
    /// <b>Occluders is asked with the tank's frame, and the frame is square.</b>
    /// It is centred on the sprite's anchor, about the middle of the hull, so
    /// half of it hangs below the tracks - a cell and a half of board, drawn
    /// lower down the screen than the tank stands. A cell that touches the frame
    /// at all is admitted, so a rise a row or two in front came back as covering
    /// a tank it is nowhere near, and the glaze went on with it.
    ///
    /// <b>Trimming the frame at the tracks was tried and cut too deep.</b> It
    /// dropped the cell standing straight in front as well - that one's drawn top
    /// edge falls a few pixels below the tracks, so it grazes the hull rather
    /// than covering it, and it is the cell anybody pointing at "the hex in front
    /// of the tank" is pointing at. A bound in pixels cannot tell those two apart
    /// without a number nobody can argue for.
    ///
    /// So the reach is stated in cells instead, which is the sentence the whole
    /// feature is written in: <b>the hex in front of the tank</b>. A neighbour
    /// can be in front of a tank; a cell two rows away is in front of something
    /// else. The flat mode keeps the frame alone and is right to - repainting a
    /// cell that covers nothing costs it nothing there, which is why the square
    /// was never wrong for it.
    /// </summary>
    private static bool Beside(Vector2I here, Vector2I cell)
    {
        foreach (int heading in HexField.EdgeHeadings)
            if (HexField.Step(here, heading) == cell)
                return true;
        return false;
    }

    /// <summary>
    /// Which single cell is standing in front of this tank: of the ones Occluders
    /// admits, the neighbour whose drawn face covers the most of the hull.
    ///
    /// <b>One cell rather than the set, because the set was never the question.</b>
    /// A tank in a pit has three higher neighbours in front of it and all three
    /// are, strictly, in front - but only one of them is the hexagon anybody means
    /// by "the hex covering the tank", and glazing the other two changes the
    /// picture for nothing. What they have in common is the frame Occluders is
    /// asked with: it is the square the tank's layers are drawn from, wider than
    /// the hull and half a cell taller, so three hexagons touch it.
    ///
    /// <b>Measured rather than guessed at</b>, and that is what makes this a rule
    /// instead of a heading. The cell's own drawn hexagon is cut to the tank's
    /// frame - <see cref="Split"/>, four half-planes, the helper the hull's seam
    /// is cut with - and what is left is measured by the shoelace. Picking the
    /// neighbour straight ahead instead would have been a guess, and on this
    /// board a wrong one: that cell's drawn top edge falls a few pixels below the
    /// tracks, so it grazes the hull, while the two diagonals are the ones the
    /// depth test actually cuts it with.
    ///
    /// Ties do not need breaking: two cells covering the hull equally is a tank
    /// squarely on the seam between them, and either answer is the same picture.
    /// </summary>
    private Vector2I? Nearest(Vehicle vehicle, IReadOnlyList<Vector2I> covering)
    {
        Vector2I? best = null;
        float most = 0.0f;
        foreach (Vector2I cell in covering)
        {
            if (!Beside(vehicle.Cell, cell))
                continue;
            float much = Under(cell, vehicle.Box);
            if (much <= most)
                continue;
            most = much;
            best = cell;
        }
        return best;
    }

    /// <summary>How much of this cell's drawn face falls inside that box, in
    /// square units of the flat space both are measured in. Nought when they do
    /// not meet.</summary>
    private float Under(Vector2I cell, Rect2 box)
    {
        // <b>In the field's space, which is the box's, and not the stage's.</b>
        // The face laid by GlazeFace is a mesh under this node, so its hexagon
        // carries Origin and is built off FlatAnchor - the sorting row, with the
        // lift held apart and put back per vertex. What is being measured here is
        // neither: it is where the cell is *drawn*, against a box measured from a
        // sprite's drawn position. That is CellCentre, lift and all, with no
        // Origin on it. Handed the mesh's hexagon instead, the two never met and
        // the answer was nought for every cell - which is a tank drawn over the
        // ground with nothing laid back over it.
        List<Vector2> kept = Hexagon(cell, Field.CellCentre(cell));
        (kept, _) = Split(kept, p => p.X - box.Position.X);
        (kept, _) = Split(kept, p => box.End.X - p.X);
        (kept, _) = Split(kept, p => p.Y - box.Position.Y);
        (kept, _) = Split(kept, p => box.End.Y - p.Y);
        if (kept.Count < 3)
            return 0.0f;
        float twice = 0.0f;
        for (int k = 0; k < kept.Count; k++)
        {
            Vector2 a = kept[k], b = kept[(k + 1) % kept.Count];
            twice += a.X * b.Y - b.X * a.Y;
        }
        return Mathf.Abs(twice) * 0.5f;
    }

    /// <summary>The cell's drawn hexagon in flat space, stopped short of its own
    /// outline. One writer, because the face that is measured has to be the face
    /// that is laid - see <see cref="GlazeFace"/> and <see cref="Under"/>.
    /// </summary>
    private List<Vector2> Hexagon(Vector2I cell, Vector2 hub)
    {
        float inset = Field.LevelAt(cell) > 0 || Field.IsRamp(cell)
            ? RaisedEdge : Edge;
        Vector3[] corner = Corners();
        var ring = new List<Vector2>(6);
        for (int k = 0; k < 6; k++)
            ring.Add(hub + new Vector2(corner[k].X, corner[k].Z * Squash) * inset);
        return ring;
    }

    private void Glaze()
    {
        // Framewise rather than a lerp on a stored target, which is the wood's
        // reason word for word: the same integration the rest of the bench uses,
        // so a paused frame does not step the fade. Read here rather than handed
        // down because Place takes no step and the root's process and this node's
        // are not ordered against each other - a step read a frame late would put
        // the fade one frame behind the tank that moved it.
        double delta = FrameClock.FixedStep ?? GetProcessDeltaTime();
        double k = GlazeTime <= 0.0 ? 1.0 : 1.0 - Math.Exp(-delta / GlazeTime);
        foreach (Vector2I cell in _wearing)
            if (!_glazing.ContainsKey(cell))
                _glazing[cell] = 0.0f;
        _fading.Clear();
        foreach ((Vector2I cell, float now) in _glazing)
        {
            float want = _wearing.Contains(cell) ? 1.0f : 0.0f;
            float next = (float)(now + (want - now) * k);
            // Snapped a percent out rather than a thousandth: a cell at one
            // percent of the glaze is a cell, and the exponential tail below that
            // is two dozen frames of a mesh built for nothing.
            if (Mathf.Abs(next - want) < 0.01f)
                next = want;
            if (next > 0.0f)
                _fading[cell] = next;
        }
        _glazing.Clear();
        foreach ((Vector2I cell, float much) in _fading)
            _glazing[cell] = much;

        var mesh = (ImmediateMesh)_glaze.Mesh;
        mesh.ClearSurfaces();
        if (_glazing.Count == 0)
            return;
        Vector3[] corner = Corners();
        Texture2D? art = GroundArt();
        var pane = new List<(Vector3 At, Vector2 Uv, Color Ink)>();
        foreach ((Vector2I cell, float much) in _glazing)
            GlazeFace(pane, cell, corner, art, much);
        if (pane.Count == 0)
            return;
        mesh.SurfaceBegin(Mesh.PrimitiveType.Triangles);
        foreach ((Vector3 at, Vector2 uv, Color ink) in pane)
        {
            mesh.SurfaceSetColor(ink);
            mesh.SurfaceSetUV(uv);
            mesh.SurfaceAddVertex(at);
        }
        mesh.SurfaceEnd();
    }

    /// <summary>
    /// One cell's face for the glaze: the whole hexagon, stopped short of its own
    /// outline and at nothing else.
    ///
    /// <b>It was cut to the tank's frame for one round and that was wrong.</b>
    /// The cut was there to keep a bush two cells away from going pale, and what
    /// it produced was a band of see-through ground under the hull instead of a
    /// see-through cell - the thing being asked for is the hexagon in front, whole.
    /// A bush on it paling with it is the cell going see-through, not a defect of
    /// it.
    ///
    /// <b>Stopped at the outline's inner edge</b>, which is the same fraction the
    /// band's own inner ring is built at - see <see cref="Edge"/>. The band is its
    /// own mesh with its own material, so a glaze reaching under it would not be
    /// blending the cell over itself there: it would be laying ground over the
    /// outline and washing it out.
    ///
    /// The lift is affine over a face - a ramp's top is one plane - so a point
    /// inside the hexagon is fitted from the centre and two corners rather than
    /// looked up. Nothing needs that while the polygon is the hexagon itself; it
    /// is what lets the polygon be anything else.
    /// </summary>
    private void GlazeFace(List<(Vector3, Vector2, Color)> into, Vector2I cell,
                           Vector3[] corner, Texture2D? art, float much)
    {
        Vector2 hub = Origin + Field.FlatAnchor(cell) + Field.CentreOffset;
        Vector2 Flat(Vector3 off) => new(off.X, off.Z * Squash);
        List<Vector2> kept = Hexagon(cell, hub);

        Vector3 top = CellTop(cell);
        Vector3[] up = CornerLift(cell);
        Vector3 mid = Vector3.Zero;
        foreach (Vector3 v in up)
            mid += v / 6.0f;
        Vector2 e0 = Flat(corner[0]), e1 = Flat(corner[1]);
        Vector3 v0 = up[0] - mid, v1 = up[1] - mid;
        float det = e0.X * e1.Y - e0.Y * e1.X;
        // How far into the glaze this cell is, carried in the face's own alpha
        // rather than in the material's: one node draws every glazed cell, and a
        // uniform would give them all one clock - so a cell coming in would drag
        // the ones already there back with it.
        Color ink = CellInk(cell, art);
        ink.A = much;
        for (int i = 1; i + 1 < kept.Count; i++)
        foreach (int k in new[] { 0, i, i + 1 })
        {
            Vector2 d = kept[k] - hub;
            var plan = new Vector3(d.X, 0.0f, d.Y / Mathf.Max(Squash, 1e-4f));
            Vector3 lift = mid;
            if (Mathf.Abs(det) > 1e-6f)
                lift += v0 * ((d.X * e1.Y - d.Y * e1.X) / det)
                        + v1 * ((e0.X * d.Y - e0.Y * d.X) / det);
            into.Add((top + plan + lift, GroundUV(plan), ink));
        }
    }

    /// <summary><see cref="Glaze"/>'s working set. A field rather than a local so
    /// the pass that fills it every frame allocates nothing.</summary>
    private readonly List<Vector2I> _wearing = new();

    /// <summary>How far into the glaze each cell is, nought to one. A cell is in
    /// here while it is still worth drawing, which outlives the frame it stopped
    /// standing in front of a tank - that is the fade out.</summary>
    private readonly Dictionary<Vector2I, float> _glazing = new();

    /// <summary><see cref="_glazing"/>'s next state, because a dictionary may not
    /// be written while it is walked.</summary>
    private readonly Dictionary<Vector2I, float> _fading = new();

    /// <summary>How long a cell takes to come into the glaze and to leave it.
    /// The wood's number, and for the wood's reason: short enough to keep up with
    /// a tank crossing a cell, long enough not to read as a switch. See
    /// <c>Grove.FadeTime</c>, which is the same fade on the trees.</summary>
    public double GlazeTime = 0.18;

    private readonly List<ShellShade> _shades = new();

    /// <summary>
    /// The shadow under every round that is off the ground - see
    /// <see cref="ShellShade"/>, which is only ever a mortar's bomb.
    ///
    /// <b>A pool walked in order and hidden from where it runs out</b>, the
    /// arrangement <see cref="Etchings"/> uses - and unlike the craters there is
    /// no stamp to skip on, because a round in the air has moved by definition.
    ///
    /// <b>The ground's own rise is added here and nowhere else.</b>
    /// <see cref="Shell.Under"/> hands over a point on the datum, because that is
    /// all a round can know: the shell is a flat drawing of a line between two
    /// tanks and has no idea what it is passing over. The board does, and it is
    /// the board that has to answer - a bomb crossing a hill throws its shadow on
    /// the hilltop, not on the plain the hill stands out of.
    /// </summary>
    private void Shades(IReadOnlyList<Vehicle> vehicles)
    {
        int k = 0;
        foreach (Vehicle vehicle in vehicles)
            foreach (Shell round in vehicle.Rounds)
            {
                if (!round.Lofted || round.Arrived)
                    continue;
                while (_shades.Count <= k)
                {
                    var made = new ShellShade();
                    AddChild(made);
                    made.Build(Squash, RiseFactor);
                    _shades.Add(made);
                }
                Vector2 under = round.Under;
                Vector2I cell = Field.CellAt(under - Origin);
                _shades[k].Show(under,
                                Field.InBounds(cell)
                                    ? Field.LevelAt(cell) * Field.Lift : 0.0f,
                                round.High, Squash, RiseFactor);
                k++;
            }
        for (int i = k; i < _shades.Count; i++)
            _shades[i].Hide();
    }

    /// <summary>The shadows as they stand, for a check that cannot read a frame.
    /// Read-only: they are put where the rounds are and nowhere else.</summary>
    public IReadOnlyList<ShellShade> Shading => _shades;


    /// <summary>Where a tank touches the ground, in the world.
    ///
    /// <b>Standing rather than Height</b>, and the two differ only mid-step: the
    /// row drawn is the same for either, so this buys depth and nothing else. See
    /// <see cref="Vehicle.Standing"/> for what it buys it against.</summary>
    public Vector3 Contact(Vehicle vehicle) =>
        Ground(vehicle.GroundPoint, vehicle.Standing);

    /// <summary>
    /// Where a point drawn on the ground stands in the world, given how high off
    /// the datum the ground under it is.
    ///
    /// <b>A drawn row is not an earth row, and the water was the fourth thing on
    /// this board to pay for it.</b> The ring, the pond and the wood each had to
    /// put the lift back before they could name a cell (see <c>HexField.Bare</c>);
    /// the wake, the foam blot and the ripple push each carried a drawn point
    /// straight into <see cref="World"/> with a lift of zero, which is the same
    /// mistake read the other way round - a drawn row handed to a mapping that
    /// wants an earth row and a lift beside it.
    ///
    /// <b>And the row on screen comes out right either way, which is why nothing
    /// said so.</b> <c>World</c> draws a point at <c>flat - lift</c>, and a drawn
    /// row has the lift taken out of it already, so both spellings land on the
    /// same screen row. What comes out wrong is the <b>depth</b>, by a whole level
    /// of it - and the surface the stamp is compared against is a horizontal
    /// plane, so a level of depth is a level of screen row. Measured on the pond:
    /// the trail sat one <c>HexField.Lift</c> below the tank that laid it instead
    /// of the water's own depth above it.
    ///
    /// <b>Which is also why it showed on a diagonal and not on a column.</b> The
    /// error is purely vertical, and four of the six edge headings run
    /// diagonally: a vertical shift of a diagonal trail reads as the trail having
    /// moved sideways off the line of cell centres, while on the two vertical
    /// headings it slides along the trail and is invisible.
    ///
    /// <b>And the lift to hand it is the plane's, not the ground's.</b> A mark on
    /// the water is drawn on a plane the water's own depth above the ground, so
    /// carrying the ground's lift puts it that depth up the screen - which is not
    /// a rounding: on the water board the depth is 14.7px and half the track
    /// gauge projects to 17.1px, so the blot lands on the <b>far</b> track's own
    /// ground line and reads as clinging to it. Handing it the surface's lift
    /// brings it back onto the tank's own row, where the board draws every other
    /// ground mark - the rut, the ring in a ford, the pond's splash - so a trail
    /// crossing a beach joins its ruts with no step. What is given up is the plan
    /// position: the mark sits one water depth further from the camera than the
    /// tank does. <see cref="Contact"/> is the other case and keeps the ground's
    /// lift, because a tank stands on the ground rather than on the water.
    /// </summary>
    public Vector3 Ground(Vector2 spot, float lift) =>
        Ground(spot, lift, Squash, RiseFactor);

    /// <summary>The same conversion given its two camera terms instead of reading
    /// them off the field, so it can be asserted without a board - the reason
    /// <see cref="World"/> is shaped this way too, and it earns it here: what a
    /// check of this has to pin down is the one expression the stage actually
    /// uses, not a second copy of it written out in the check.</summary>
    public static Vector3 Ground(Vector2 spot, float lift, float squash,
                                 float rise) =>
        World(new Vector2(spot.X, spot.Y + lift), lift, squash, rise);

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
                // Deep water is a different surface height on the same mask, so
                // it is its own mark for the same reason.
                .Append(Field.IsDeep(cell) ? 'D' : Field.IsWater(cell) ? '~' : '.')
                .Append(Field.InkFor(cell).ToRgba32()).Append(';');
        }
        // The depth as well, and not only where the water is: it is a slider, and
        // moving it moves the surface without moving a single cell of the mask.
        note.Append('@').Append(Field.WaterRise.ToString("F3"));
        // And the deep water's own two: how far under the bank its surface stands
        // and how far under its level its bed is drawn. Both are sliders, both
        // move mesh - the surface and the flank, the plate and the walls - and
        // neither moves a cell of the mask.
        note.Append('@').Append(Field.DeepRise.ToString("F3"))
            .Append('v').Append(Field.DeepBed.ToString("F3"));
        // And whether it is painted, because that swaps the surface's material
        // and the mesh's UVs at once - a toggle that only changed the shader
        // would leave the drawn pond wearing no texture coordinates at all.
        note.Append(Surf is { Any: true } ? '#' : '=');
        // Nothing about the ring: it is cut fresh every frame about the tank it
        // marks, so neither whether there is one nor where it stands is anything
        // the board has to be rebuilt for.
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

    /// <summary>The board's top faces as triangles, in world units.
    ///
    /// <b>For anything that has to stand on the ground rather than draw it</b> -
    /// today the wall's rigid bodies. Built from the same <see cref="CellTop"/>
    /// and <see cref="CornerLift"/> the mesh is built from, and fanned the same
    /// way, for the reason the shadow map is rasterised off them too: a second
    /// description of the ground disagrees exactly where it is hardest to check,
    /// and the failure is rubble resting on nothing.
    ///
    /// Tops only. A collider for the walls of the prisms would be a second
    /// question - what happens to a brick that goes over the rim - and this board
    /// answers it by letting the brick fall to whatever top is under it.</summary>
    public List<Vector3> GroundTriangles()
    {
        var outp = new List<Vector3>();
        if (Field.Atlas is null)
            return outp;
        Vector3[] corner = Corners();
        for (int q = 0; q < Field.Columns; q++)
        for (int r = 0; r < Field.Rows; r++)
        {
            var cell = new Vector2I(q, r);
            if (!Field.InBounds(cell))
                continue;
            Vector3 top = CellTop(cell);
            Vector3[] up = CornerLift(cell);
            for (int i = 1; i + 1 < 6; i++)
            foreach (int k in new[] { 0, i, i + 1 })
                outp.Add(top + corner[k] + up[k]);
        }
        return outp;
    }

    /// <summary>A cell's centre at its own base level - the lowest its top face
    /// reaches. A flat cell's whole top sits here; a ramp's rises off it, which
    /// <see cref="Field"/>'s <c>TopCorners</c> carries per corner.</summary>
    private Vector3 CellTop(Vector2I cell)
    {
        // The drawn floor and not the level: the two part on flat deep water,
        // whose bed is drawn under the level it counts as - see HexField.BedAt.
        // CornerLift below still measures off the level, so the corners of such
        // a cell come out at nought and the whole face lands on the bed.
        float lift = Field.BedAt(cell);
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
    /// The water at its own surface, which is what the flank of the pond is
    /// graded down from - see <see cref="BuildWater"/>.
    ///
    /// <b>Measured off the rendered surface, not picked beside it.</b> The flank
    /// stands directly under the surface's near edge and the two share that edge,
    /// so any disagreement about the colour draws itself as a line there. The
    /// surface's own pixels along that edge measure (82, 101, 83); this is a
    /// quarter under them, which is what says the flank is the side of the water
    /// and not more of the top of it. Matched exactly the pond would go back to
    /// reading as a sheet, only a taller one.
    ///
    /// On screen as written: a vertex colour on an unshaded material comes out
    /// encoded, so this is the value a screenshot reports and not a linear one.
    /// </summary>
    public static readonly Color Brim = new(0.243f, 0.329f, 0.306f);

    /// <summary>
    /// The same for the drawn strip, which is a different water and measures
    /// (48, 67, 59) at the same edge.
    ///
    /// <b>A second measurement rather than <see cref="WaterTint"/>, and the first
    /// try was WaterTint.</b> That is the mean of the <i>art</i>, and the art is
    /// laid two thirds opaque over pale ground - so the number is far under what
    /// the strip actually comes out at, and the flank went nearly black. A hole,
    /// which is the one thing the flank exists to stop being.
    ///
    /// Two constants and not one because there are two surfaces. A single colour
    /// would be a quarter under one of them and a third over the other, and which
    /// one it flattered would depend on a flag.
    /// </summary>
    public static readonly Color BrimDrawn = new(0.150f, 0.211f, 0.185f);

    /// <summary>
    /// And the same again for the side of deep water, because the surface over it
    /// is a different colour now - see the shader's <c>murk</c>.
    ///
    /// <b>Measured the same way and it had to be.</b> The flank stands under the
    /// surface's near edge and shares it, so a disagreement draws itself as a line
    /// there - which is exactly what the first cut of the deep water did: a dark
    /// green pond with a pale blue rim under it, and the rim was the old constant
    /// still describing a ford. The deep surface's own pixels along that edge
    /// measure (33, 64, 48); this is a quarter under them, the relation
    /// <see cref="Brim"/> already states.
    ///
    /// Three constants now, and the reason is the reason there were two: one
    /// colour would be a quarter under one water and half over another, and which
    /// it flattered would depend on the cell.
    /// </summary>
    public static readonly Color BrimDeep = new(0.098f, 0.188f, 0.141f);

    /// <summary>
    /// How much darker the water is at the bottom of the flank than at its top.
    ///
    /// <b>A fraction of the surface's colour rather than a second colour</b>, for
    /// the reason every size in this project is a fraction: the flank has to
    /// follow whichever water is switched on, and a pair of absolutes would be
    /// right under one of them. What the gradient says is the only thing a
    /// cross-section of water can say without a texture - that there is less light
    /// further down - and that is the whole of it being a volume rather than a
    /// sheet.
    /// </summary>
    public const float Bed = 0.45f;

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
    /// <remarks>
    /// <b>Deep water is not the strip, and forgetting that made the hull the
    /// same colour as the pond.</b> The mean above is the mean of the art, and
    /// the art is what a ford is drawn with; a deep cell is that art over the
    /// murk <c>DeepShader</c> puts under it, which is darker by design - that is
    /// what F7 was for. Tinting toward the strip's mean in a cell drawn far
    /// darker than the strip does not stop short of the water, it goes through
    /// it and out the other side: measured on the drowned LTP, hull (24,44,33)
    /// against a pond of (20,48,35), four levels of 255 apart, which is the
    /// second half of "the tank is not drawn".
    ///
    /// <see cref="BrimDeep"/> is that cell's own surface colour and not a second
    /// opinion about it - it is what the flank's lip at a deep cell is painted,
    /// chosen so the lip matches the water it stands in - so the tint lands 34%
    /// short of the water on the hull's side of it, as it does on a ford.
    /// </remarks>
    public Color WaterTintAt(Vector2I cell)
    {
        if (Field is not null && Field.IsDeep(cell))
            return new Color(BrimDeep.R, BrimDeep.G, BrimDeep.B, Pond.A);
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
    /// <summary>
    /// Build the board if what a cell wears has changed since it was last built.
    ///
    /// <b>Lifted out of the frame so that something raised before the first frame
    /// can ask for it, which is a bug a shot into water found.</b> Laying the
    /// ripple field is part of building the pond - the field is state, so it is
    /// laid exactly when the pond's shape is - and a bench that fires at
    /// <c>_Ready</c> fired into a field of nought texels. The hole was punched
    /// into nothing and lost, silently, because a strike outside the field's box
    /// is exactly what a shot that lands off the pond looks like.
    ///
    /// Idempotent by <see cref="Signature"/>, which is what makes it safe to ask
    /// for twice in a frame: fifty-four prisms is nothing to build, and sixty
    /// times a second for a board that has not changed is still nothing worth
    /// spending.
    /// </summary>
    private void Standing()
    {
        string want = Signature();
        if (want != _built)
        {
            Build();
            _built = want;
        }
    }

    private void Build()
    {
        if (Field.Atlas is null)
            return;
        (int low, _) = Field.LevelRange;
        // The lowest drawn floor rather than the lowest level: deep water's bed
        // is drawn under its level (HexField.BedAt), and a bank wall that stopped
        // at the level would hang over that bed with a slot of nothing under it.
        float lowest = low * Field.Lift;
        for (int q = 0; q < Field.Columns; q++)
        for (int r = 0; r < Field.Rows; r++)
            if (Field.InBounds(new Vector2I(q, r)))
                lowest = Mathf.Min(lowest, Field.BedAt(new Vector2I(q, r)));
        float floor = World(Vector2.Zero, lowest).Y;
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
            // A hole in the rectangle is not board - see HexField.Plot. Asked
            // here rather than by walking Ordered(), because every loop over the
            // board in this file is a raster and the order it runs in is its own.
            if (!Field.InBounds(cell))
                continue;
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

            TopFace(tops, cell, corner, art, 1.0f);

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
        // Before the materials, because they carry it: the map is baked off the
        // same corners the faces above were built from, so it cannot describe a
        // different board than the one it shades.
        BakeHeights(corner);
        _tops.MaterialOverride = Turf(art);
        // No art: the sides never had a texture, and the shader's default-white
        // sampler is what lets one material serve both.
        _sides.MaterialOverride = Turf(null);
        _glaze.MaterialOverride = Turf(art, glass: true);
        ((ImmediateMesh)_glaze.Mesh).ClearSurfaces();
        BuildWater(corner);
    }

    // --- what the sun cannot see ---------------------------------------------

    /// <summary>How many samples a fragment takes along the sun.
    ///
    /// <b>Fixed, with the stride adapting instead</b>, and that is the trade
    /// rather than the obvious one. The march has to reach as far as the tallest
    /// thing on the board can throw - <c>(highest-lowest)*cot(elevation)</c> -
    /// so a taller board wants either more samples or longer ones. More samples
    /// means a loop bound that changes, and this bench drags a grade slider, so
    /// that is a shader recompiled mid-drag. Longer samples means a shadow's edge
    /// gets coarser on a tall board, which is a picture getting slightly worse
    /// rather than a frame getting lost.
    ///
    /// 24 across the whole reach is 4.7 world units a step on a three-level
    /// board, against a cell 215 across - so the step is finer than a shadow's
    /// own soft edge and nothing thinner than a fifth of a cell can be stepped
    /// over.</summary>
    private const int Marches = 24;

    /// <summary>How far a shadow's edge is smeared, in world units.
    ///
    /// A penumbra rather than a hard line, and it is nearly free: the march
    /// already knows how far under the ray the blocker stands, so the edge is a
    /// smoothstep on a number it has. Named in world units because that is what
    /// the march is in - in pixels it would sharpen as the board is zoomed, and
    /// a shadow that sharpens with the zoom is a shadow drawn on the lens.
    /// </summary>
    private const float Penumbra = 7.0f;

    /// <summary>How coarse the height map is, in world units to a texel.
    ///
    /// <b>This is what a shadow's edge is made of, and six was visibly too
    /// coarse.</b> The reasoning that picked it - finer than
    /// <see cref="Penumbra"/>, so invisible - was about the height a shadow
    /// falls from, and the eye does not look at that. It looks at where the
    /// shadow stops, and that edge is a contour of this grid: at six units the
    /// hem beside a wall came out in stair steps about nine world units across,
    /// which at this zoom is five screen pixels of visible staircase.
    ///
    /// Two is about a screen pixel at the bench's own zoom, and the whole board
    /// is then a couple of megabytes of one-byte texels - which is only
    /// affordable because the bake writes a byte array rather than calling
    /// <c>SetPixel</c> a million and a half times.</summary>
    private const float Grain = 2.0f;

    private ImageTexture? _heights;
    private Vector4 _mapAt;
    private Vector2 _mapSpan;
    private float _mapReach;
    private byte[]? _map;
    private int _mapWide, _mapTall;

    /// <summary>How far the tallest thing on this board can throw, in world
    /// units on the ground. Nought on a board with no relief, which is also the
    /// board that bakes no map and marches no fragment.</summary>
    internal float ShadowReach => _mapReach;

    /// <summary>
    /// What the height map says the surface is at a point of the world, or null
    /// where there is no map.
    ///
    /// Kept so the map can be asked the one question that matters about it -
    /// whether it is the ground - rather than only inspected as pixels. A map
    /// that describes a board slightly other than the one being drawn shades
    /// perfectly convincingly and is wrong everywhere, which is the failure this
    /// bench keeps refusing to leave to the eye.
    /// </summary>
    internal float? MapHeight(float x, float z)
    {
        if (_map is null)
            return null;
        int at = Mathf.Clamp(Mathf.FloorToInt((x - _mapAt.X) * _mapAt.Z * _mapWide),
                             0, _mapWide - 1);
        int down = Mathf.Clamp(Mathf.FloorToInt((z - _mapAt.Y) * _mapAt.W * _mapTall),
                               0, _mapTall - 1);
        return _mapSpan.X + _map[down * _mapWide + at] / 255.0f * _mapSpan.Y;
    }

    /// <summary>
    /// The board's own top surface as a height map, baked off the very triangles
    /// the ground is built from.
    ///
    /// <b>Rasterised from the mesh's own corners rather than asked of the field
    /// again.</b> A second description of where the ground is would be a second
    /// thing to keep in agreement, and it would go wrong exactly where the two
    /// are hardest to compare - on a ramp, whose top is a tilted plane the field
    /// states per corner. Taken off <see cref="CellTop"/> and
    /// <see cref="CornerLift"/>, the map <i>is</i> the ground the shader shades,
    /// slopes and all, by construction.
    ///
    /// <b>A byte a texel, with the range carried beside it.</b> The whole board's
    /// relief is a few levels, so 256 codes over it is a quarter of a world unit
    /// - far under <see cref="Penumbra"/>, and therefore under anything the eye
    /// could find. Outside the board it reads as the lowest ground there is,
    /// which is the right answer: nothing out there casts anything.
    /// </summary>
    private void BakeHeights(Vector3[] corner)
    {
        _heights = null;
        _map = null;
        _mapReach = 0.0f;
        if (Field.Atlas is null)
            return;

        float loX = float.MaxValue, hiX = float.MinValue;
        float loZ = float.MaxValue, hiZ = float.MinValue;
        float loY = float.MaxValue, hiY = float.MinValue;
        for (int q = 0; q < Field.Columns; q++)
        for (int r = 0; r < Field.Rows; r++)
        {
            if (!Field.InBounds(new Vector2I(q, r)))
                continue;
            Vector3 top = CellTop(new Vector2I(q, r));
            Vector3[] up = CornerLift(new Vector2I(q, r));
            for (int i = 0; i < 6; i++)
            {
                float x = top.X + corner[i].X, z = top.Z + corner[i].Z;
                float y = top.Y + up[i].Y;
                loX = Mathf.Min(loX, x); hiX = Mathf.Max(hiX, x);
                loZ = Mathf.Min(loZ, z); hiZ = Mathf.Max(hiZ, z);
                loY = Mathf.Min(loY, y); hiY = Mathf.Max(hiY, y);
            }
        }
        if (loX > hiX)
            return;

        // A flat board casts nothing, and saying so here is what keeps the
        // march off every fragment of it rather than having it run 24 samples
        // to discover the ground is where it started.
        float relief = hiY - loY;
        if (relief < 0.001f)
            return;

        int wide = Mathf.Clamp(Mathf.CeilToInt((hiX - loX) / Grain), 8, 4096);
        int tall = Mathf.Clamp(Mathf.CeilToInt((hiZ - loZ) / Grain), 8, 4096);
        var map = new byte[wide * tall];

        float perX = wide / (hiX - loX), perZ = tall / (hiZ - loZ);
        for (int q = 0; q < Field.Columns; q++)
        for (int r = 0; r < Field.Rows; r++)
        {
            var cell = new Vector2I(q, r);
            if (!Field.InBounds(cell))
                continue;
            Vector3 top = CellTop(cell);
            Vector3[] up = CornerLift(cell);
            // The same fan the top face is built from, so what is rasterised is
            // the face and not a hexagon that happens to sit near it.
            for (int i = 1; i + 1 < 6; i++)
                Etch(map, wide, tall, loX, loZ, perX, perZ, loY, relief,
                     Peak(top, corner[0], up[0]),
                     Peak(top, corner[i], up[i]),
                     Peak(top, corner[i + 1], up[i + 1]));
        }

        _heights = ImageTexture.CreateFromImage(
            Image.CreateFromData(wide, tall, false, Image.Format.R8, map));
        _map = map;
        _mapWide = wide;
        _mapTall = tall;
        _mapAt = new Vector4(loX, loZ, 1.0f / (hiX - loX), 1.0f / (hiZ - loZ));
        _mapSpan = new Vector2(loY, relief);
        _mapReach = relief * SunCast.Length();
    }

    /// <summary>Bake the map for the board as it stands now. The same call
    /// <see cref="Build"/> makes with the same argument, exposed so the map can
    /// be asserted against the field without a built stage under it - a
    /// <c>--selftest</c> run quits before this node's first frame.</summary>
    internal void Survey()
    {
        if (Field.Atlas is not null)
            BakeHeights(Corners());
    }

    /// <summary>One corner of a top face, as the map wants it: where it is on the
    /// ground and how high it stands.</summary>
    private static Vector3 Peak(Vector3 top, Vector3 corner, Vector3 up) =>
        new(top.X + corner.X, top.Y + up.Y, top.Z + corner.Z);

    /// <summary>One triangle of the top surface into the map, keeping the higher
    /// answer where two overlap - which along a shared edge is the same answer
    /// twice, and at a corner is the one that matters.</summary>
    private static void Etch(byte[] map, int wide, int tall, float loX, float loZ,
                             float perX, float perZ, float loY, float relief,
                             Vector3 a, Vector3 b, Vector3 c)
    {
        Vector2 pa = new((a.X - loX) * perX, (a.Z - loZ) * perZ);
        Vector2 pb = new((b.X - loX) * perX, (b.Z - loZ) * perZ);
        Vector2 pc = new((c.X - loX) * perX, (c.Z - loZ) * perZ);
        float area = (pb.X - pa.X) * (pc.Y - pa.Y) - (pb.Y - pa.Y) * (pc.X - pa.X);
        if (Mathf.Abs(area) < 1e-6f)
            return;
        int x0 = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(pa.X, Mathf.Min(pb.X, pc.X))),
                             0, wide - 1);
        int x1 = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(pa.X, Mathf.Max(pb.X, pc.X))),
                             0, wide - 1);
        int y0 = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(pa.Y, Mathf.Min(pb.Y, pc.Y))),
                             0, tall - 1);
        int y1 = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(pa.Y, Mathf.Max(pb.Y, pc.Y))),
                             0, tall - 1);
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
            // The texel's middle, because that is the point the shader's
            // bilinear fetch is centred on.
            float px = x + 0.5f, py = y + 0.5f;
            float wa = ((pb.X - px) * (pc.Y - py) - (pb.Y - py) * (pc.X - px)) / area;
            float wb = ((pc.X - px) * (pa.Y - py) - (pc.Y - py) * (pa.X - px)) / area;
            float wc = 1.0f - wa - wb;
            // A hair of slack, so that a texel centre landing exactly on a
            // shared edge is claimed by both triangles rather than by neither.
            if (wa < -0.001f || wb < -0.001f || wc < -0.001f)
                continue;
            float high = wa * a.Y + wb * b.Y + wc * c.Y;
            var code = (byte)Mathf.RoundToInt(
                Mathf.Clamp((high - loY) / relief, 0.0f, 1.0f) * 255.0f);
            int at = y * wide + x;
            if (code > map[at])
                map[at] = code;
        }
    }

    /// <summary>
    /// What the ground is painted with: the art and the vertex colour it always
    /// wore, times how much of the sun reaches it.
    ///
    /// <b>The march is the whole of it, and it answers for the walls too without
    /// being told about them.</b> A fragment steps toward the sun and asks the
    /// height map whether the ground there has got above the ray; a point on a
    /// wall facing away from the sun leaves its own cell's top standing over the
    /// ray on the first step, so it comes out dark, and one on a wall facing the
    /// sun steps out over the lower neighbour and comes out lit. Neither needed a
    /// normal, which is as well: the sides carry no UV and their facing is a
    /// thing <see cref="WallInk"/> states per heading.
    ///
    /// <b>The art is sampled through a default-white texture rather than through
    /// a branch.</b> A board with no terrain art on disk hands over nothing, and
    /// nothing samples white, so the vertex colour comes through untouched -
    /// which is exactly what the <see cref="StandardMaterial3D"/> this replaces
    /// did with a null albedo.
    ///
    /// The darkening is <see cref="ShadowInk"/>'s own alpha, and it has to be:
    /// a hill's shadow and a tree's shadow are the same shadow, and two numbers
    /// for it would be the board lit by two suns of different strengths.
    /// </summary>
    internal const string SoilShader = @"
shader_type spatial;
render_mode unshaded, cull_disabled;

uniform sampler2D art : source_color, filter_linear_mipmap, hint_default_white;
uniform sampler2D height : filter_linear, hint_default_black;
// Where the wood has burnt, in the same world XZ the march is walked in, and how
// black a fully burnt cell goes. Nothing when the board has never been lit, which
// is what keeps this off every fragment of every board that has not.
uniform sampler2D ash : filter_linear, hint_default_black;
uniform vec4 ash_map = vec4(0.0, 0.0, 1.0, 1.0);
uniform float ash_ink = 0.0;
// How much of its own light burnt ground keeps. A uniform rather than a literal
// in the mix below because it is a bound on other things too - see
// PropFamily.Soot, which has to come out under it - and a bound copied into the
// thing it bounds is the one that goes stale.
uniform float ash_keep = 0.55;
// How much of the glaze's own colour it keeps over what it covers. Read by the
// glass build of this shader and by nothing else - see Stage3D.Glass, which is
// this source with one line put in.
uniform float glaze = 0.5;
// The map's own corner in world XZ, and one over its extent there.
uniform vec4 map = vec4(0.0, 0.0, 1.0, 1.0);
// The lowest ground on the board and how far the relief reaches above it.
uniform vec2 span = vec2(0.0, 1.0);
// Where the sun is, as a direction to it.
uniform vec3 sun = vec3(0.0, 1.0, 0.0);
// How far along the ground one sample steps, how far a shadow's edge is
// smeared, how dark a blocked fragment goes, and how many samples to take.
// The count is a uniform and not a constant so that a board growing taller
// does not recompile this in the middle of a slider drag.
uniform float step_run = 0.0;
uniform float soft = 1.0;
uniform float shade = 0.0;
uniform int steps = 0;

varying vec3 ground;

void vertex() {
    ground = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
    vec4 paint = texture(art, UV) * COLOR;
    float blocked = 0.0;
    if (shade > 0.0 && steps > 0) {
        vec2 flat_run = normalize(sun.xz) * step_run;
        float climb = step_run * sun.y / length(sun.xz);
        for (int k = 1; k <= steps; k++) {
            vec2 at = ground.xz + flat_run * float(k);
            vec2 uv = (at - map.xy) * map.zw;
            // Off the board is the lowest ground there is - nothing out there
            // stands over anything.
            float over = span.x;
            if (uv.x >= 0.0 && uv.x <= 1.0 && uv.y >= 0.0 && uv.y <= 1.0)
                over = span.x + texture(height, uv).r * span.y;
            blocked = max(blocked,
                          smoothstep(0.0, soft, over - (ground.y + climb * float(k))));
        }
    }
    vec3 lit = paint.rgb * (1.0 - shade * blocked);
    if (ash_ink > 0.0) {
        vec2 av = (ground.xz - ash_map.xy) * ash_map.zw;
        if (av.x >= 0.0 && av.x <= 1.0 && av.y >= 0.0 && av.y <= 1.0) {
            float soot = texture(ash, av).r * ash_ink;
            // Half the colour out and near half the light: ash is grey, and the
            // ground under it keeps its own grain, which is what keeps a burnt
            // cell reading as ground rather than as a hole.
            //
            // How far down is set by what stands on it rather than by taste: a
            // burnt trunk composites to about 86 of 255 over this ground, so at
            // 0.30 the ash went to 45 and the trees left standing in it read as
            // silver skeletons on tarmac - brighter than anything near them.
            // 0.55 puts the ash at 82, which is the charcoal standing in it.
            float lum = dot(lit, vec3(0.299, 0.587, 0.114));
            lit = mix(lit, mix(lit, vec3(lum), 0.5) * ash_keep, soot);
        }
    }
    ALBEDO = lit;
    // GLAZE
    // ALPHA is deliberately not written. The ground is opaque and the material
    // this replaced said so by leaving transparency off; a spatial shader that
    // writes ALPHA at all is taken for a transparent surface, drops out of the
    // opaque pass and stops settling the things standing on it. Measured, and
    // it does not look like a depth bug: the ground itself came out identical
    // to the pixel, and what moved was every transparent thing over it - trees,
    // tanks, the pond and the cell outlines, 215227px of them.
}
";

    private static readonly Shader Soil = new() { Code = SoilShader };

    /// <summary>
    /// The soil shader with one line put in: the glaze writes ALPHA.
    ///
    /// <b>Two shaders off one source, for the reason the tank has two</b> - see
    /// Paint. Transparency is a render mode and a render mode is compiled in, and
    /// the paragraph at the foot of SoilShader is what happens if the board's own
    /// material takes it: the ground leaves the opaque pass and every transparent
    /// thing standing on it moves, 215227px of it. So the board keeps the opaque
    /// build and the glaze gets its own.
    ///
    /// <b>Off the same source rather than a second shader beside it</b>, because
    /// the copy has to come out the same colour as the face under it to the pixel.
    /// Where the glaze covers no tank it blends a cell over itself, which is a
    /// no-op at any alpha - but only while the two agree about the art, the ramp's
    /// shade, the sun's march and the ash. A shader written twice would agree
    /// until one of them was edited.
    /// </summary>
    private static readonly Shader Glass = new()
    {
        Code = SoilShader
            // Coplanar with the face it is a copy of, so a depth test here is a
            // coin toss per fragment and comes back as the cell crawling with
            // noise. It is sorted by its rung instead - see Turf.
            .Replace("render_mode unshaded, cull_disabled;",
                     "render_mode unshaded, cull_disabled, depth_draw_never, "
                     + "depth_test_disabled;")
            // <b>The colour is untouched, and that is the whole contract.</b>
            // A tint here would be a marker and not a glass: the cell would
            // read as something other than ground wherever it is worn. What
            // the copy adds is an alpha and nothing else, so where it covers
            // no hull it is the cell blended over the cell, which is the cell.
            .Replace("// GLAZE", "ALPHA = glaze * COLOR.a;"),
    };

    /// <summary>How much of the cell in front of a tank is still the cell. Half:
    /// the tank has to read through it, and the ground has to stay ground.
    /// </summary>
    public const float GlazeInk = 0.5f;

    /// <summary>The ground's material, with the map this board baked in it.
    /// Handed the art or nothing; nothing samples white.</summary>
    /// <summary>
    /// One cell's top face, into whatever surface is being built.
    ///
    /// <b>Its own method because the glaze lays the same hexagon a second
    /// time</b> - see <see cref="Glaze"/>. A copy that is a shade off the face
    /// under it stops being invisible where it does not cover a tank, and a
    /// second spelling of the fan, the art's keying and the ramp's shade is
    /// exactly how it would get there.
    /// </summary>
    /// <summary>What a cell's face is painted: its own ink, darkened for a ramp,
    /// and stood in for the art when the board has none. Its own method because
    /// the glaze paints the same cell a second time and a shade off is a patch.
    /// </summary>
    private Color CellInk(Vector2I cell, Texture2D? art)
    {
        Color ink = Field.InkFor(cell);
        if (art is null)
            ink = new Color(SoilInk.R * ink.R, SoilInk.G * ink.G,
                            SoilInk.B * ink.B);
        if (Field.IsRamp(cell))
            ink = new Color(ink.R * RampShade, ink.G * RampShade,
                            ink.B * RampShade);
        return ink;
    }

    private void TopFace(SurfaceTool into, Vector2I cell, Vector3[] corner,
                         Texture2D? art, float inset)
    {
        Vector3 top = CellTop(cell);
        Vector3[] up = CornerLift(cell);
        Vector3 mid = Vector3.Zero;
        foreach (Vector3 v in up)
            mid += v / 6.0f;
        Color ink = CellInk(cell, art);
        for (int i = 1; i + 1 < 6; i++)
        foreach (int k in new[] { 0, i, i + 1 })
        {
            into.SetColor(ink);
            // The art is keyed to the footprint, not to where the point is
            // drawn - so a slope wears the same hexagon of ground stretched
            // over a face that projects taller, which is what a slope does.
            Vector3 plan = corner[k] * inset;
            into.SetUV(GroundUV(plan));
            into.AddVertex(top + plan + mid + (up[k] - mid) * inset);
        }
    }

    /// <summary>Hand a uniform to every material the board's faces wear - the
    /// tops, the sides and the glaze.
    ///
    /// <b>Written once because the glaze is the same shader on its own node.</b>
    /// The whole of <see cref="Glaze"/> is that a cell blended over itself comes
    /// out as itself, and that holds only while the copy is told everything the
    /// face under it was told. Pushed to two of the three, the sun moving or a
    /// wood burning would draw the glazed cells as a patch.</summary>
    private void Tell(string name, Variant value)
    {
        foreach (MeshInstance3D face in new[] { _tops, _sides, _glaze })
            if (face?.MaterialOverride is ShaderMaterial ink)
                ink.SetShaderParameter(name, value);
    }

    private ShaderMaterial Turf(Texture2D? art, bool glass = false)
    {
        var ink = new ShaderMaterial { Shader = glass ? Glass : Soil };
        if (art is not null)
            ink.SetShaderParameter("art", art);
        if (_heights is not null)
        {
            ink.SetShaderParameter("height", _heights);
            ink.SetShaderParameter("map", _mapAt);
            ink.SetShaderParameter("span", _mapSpan);
            ink.SetShaderParameter("sun", Sun);
            ink.SetShaderParameter("soft", Penumbra);
            ink.SetShaderParameter("steps", Marches);
            ink.SetShaderParameter("step_run", _mapReach / Marches);
        }
        ink.SetShaderParameter("shade", CastShadows ? ShadowInk.A : 0.0f);
        if (glass)
        {
            ink.SetShaderParameter("glaze", GlazeInk);
            ink.RenderPriority = GlazeOrder;
        }
        return ink;
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
            _flank.Mesh = null;
            Wash?.Drain();
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
        var flanks = new SurfaceTool();
        flanks.Begin(Mesh.PrimitiveType.Triangles);
        (Vector2[] normal, float[] stand, float inradius) = Rim(corner);
        // Which way the eye is - see the flanks below. How far the surface stands
        // over its own bottom is per cell now (deep water is a whole level of it)
        // and is read where the cell is.
        var eye = new Vector3(0.0f, Squash, RiseFactor);
        Color brim = Deep ? Brim : BrimDrawn;
        var bed = new Color(brim.R * Bed, brim.G * Bed, brim.B * Bed, 1.0f);
        // And the pair the deep cells get instead. Only on the computed surface:
        // the drawn strip is one sheet of art at one opacity and has no second
        // water to be the side of.
        Color brimDeep = Deep ? BrimDeep : BrimDrawn;
        var bedDeep = new Color(brimDeep.R * Bed, brimDeep.G * Bed,
                                brimDeep.B * Bed, 1.0f);
        bool walled = false;
        bool any = false;
        int over = 0;
        var hub = new Vector2[MaxWaterCells];
        var mask = new float[MaxWaterCells];
        // And which of them are deep, which is the one thing about a water cell
        // the surface could not see. See the shader's own murk.
        var sink = new float[MaxWaterCells];
        // The pond's own bounding box in world XZ, which is what the ripple field
        // is laid over - see Ripples.Fit. Taken in this loop because it is the one
        // that already knows which cells are wet and how far a hexagon reaches,
        // and a second pass over them would be a second answer to where the water
        // is.
        var low = new Vector2(float.MaxValue, float.MaxValue);
        var high = new Vector2(float.MinValue, float.MinValue);
        // And a signature of which cells came out wet, so the field can tell this
        // pond from another one that happens to have the same bounding box - see
        // Ripples.Fit, which keeps its state when both agree.
        long wetStamp = 1469598103934665603L;
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
            Vector2 flat = Origin + Field.FlatAnchor(cell) + Field.CentreOffset;
            Vector3 top = World(flat, Field.WaterTop(cell));
            // Which of the six edges leave the water, and where the middle of
            // this cell is - the two things the surface needs to work out how far
            // any fragment of it is from a shore. Both go to the shader by cell
            // rather than by vertex: see the fragment, where trying to carry the
            // answer on the corners is what put foam round the whole rim.
            hub[ord] = new Vector2(top.X, top.Z);
            mask[ord] = Edges(cell, corner);
            sink[ord] = Field.IsDeep(cell) ? 1.0f : 0.0f;
            wetStamp = (wetStamp ^ (q * 1013 + r)) * 1099511628211L;
            low = new Vector2(Mathf.Min(low.X, top.X - halfX),
                              Mathf.Min(low.Y, top.Z - halfZ));
            high = new Vector2(Mathf.Max(high.X, top.X + halfX),
                               Mathf.Max(high.Y, top.Z + halfZ));
            // A fan from the middle rather than from a corner. Nothing in the
            // shading needs it any more, but it is the shape that keeps a
            // triangle inside one edge's business, and rebuilding it the other
            // way round would be churn for a mesh that is correct.
            for (int k = 0; k < 6; k++)
            {
                Lay(st, top, Vector3.Zero, ord, halfX, halfZ, inset);
                Lay(st, top, corner[k], ord, halfX, halfZ, inset);
                Lay(st, top, corner[(k + 1) % 6], ord, halfX, halfZ, inset);
            }

            // And the flank under the free edges: the water's own cross-section,
            // from the surface down to the bottom it stands on. Without it the
            // pond is a sheet held up over its floor, and at the board's cut you
            // see the floor in the gap - which is what the near shore was.
            //
            // <b>Only the edges facing the eye, and the cull is needed rather than
            // saved.</b> A flank on a far edge is behind the surface's own hexagon
            // and the surface would draw over it - but it is opaque and it writes
            // depth, so the deep shader would read it as the bottom, find the water
            // paper-thin there, and lay foam along the whole of that edge. The
            // three that face away are the ground prisms' three, for the same
            // reason and by the same arithmetic.
            bool[] open = Freeboard(cell, corner);
            // Down to the drawn bed, not to the level - the flank is the water's
            // cross-section, and a bed drawn deeper (HexField.DeepBed) is a
            // deeper cross-section at the board's cut. On every other cell the two
            // are the same number.
            var sole = new Vector3(
                0.0f, (Field.WaterTop(cell) - Field.BedAt(cell)) / RiseFactor, 0.0f);
            bool sunk = Field.IsDeep(cell);
            Color lip = sunk ? brimDeep : brim, floorInk = sunk ? bedDeep : bed;
            for (int k = 0; k < 6; k++)
            {
                if (!open[k]
                    || new Vector3(normal[k].X, 0.0f, normal[k].Y).Dot(eye) <= 0.0f)
                    continue;
                walled = true;
                Vector3 a = top + corner[k], b = top + corner[(k + 1) % 6];
                Vector3 af = a - sole, bf = b - sole;
                foreach ((Vector3 v, Color ink) in new[]
                         {
                             (a, lip), (b, lip), (bf, floorInk),
                             (a, lip), (bf, floorInk), (af, floorInk),
                         })
                {
                    flanks.SetColor(ink);
                    flanks.AddVertex(v);
                }
            }
        }
        if (!any)
        {
            _pond.Mesh = null;
            _flank.Mesh = null;
            Wash?.Drain();
            return;
        }
        if (walled)
        {
            flanks.GenerateNormals();
            _flank.Mesh = flanks.Commit();
            // Opaque, where the surface over it need not be. What a see-through
            // surface buys is the ford's floor showing; behind a flank there is
            // the void the board is cut against, or the pond's own bottom, and
            // neither is worth showing through water.
            _flank.MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                VertexColorUseAsAlbedo = true,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            };
        }
        else
        {
            _flank.Mesh = null;
        }
        if (over > 0)
            GD.PushWarning($"{over} flooded cells past the {MaxWaterCells} the "
                           + "surface can carry a state for - they share the last "
                           + "slot, so they will churn when it does");
        st.GenerateNormals();
        _pond.Mesh = st.Commit();
        // And the field the tanks push about, over the box those cells came to.
        // Here rather than per frame because it is state: re-laying it empties the
        // pond, so it is laid exactly when the pond's shape is - which is what
        // Build is gated on. The wet test is asked in world XZ and answered by the
        // field, through the same mapping the vertices went through, so the two
        // cannot disagree about which texel is water.
        Wash?.Fit(new Rect2(low, high - low), Squash, wetStamp,
                  p => Field.IsWater(Field.FlatCellAt(
                           new Vector2(p.X, p.Y * Squash) - Origin)));
        // Where each cell is and which of its edges are shore. With the board,
        // not with the frame: this is the pond's shape, and the shape is exactly
        // what Build is gated on changing.
        if (_deepInk is not null)
        {
            _deepInk.SetShaderParameter("hub", hub);
            _deepInk.SetShaderParameter("rim_mask", mask);
            _deepInk.SetShaderParameter("sink", sink);
            _deepInk.SetShaderParameter("rim_n", normal);
            _deepInk.SetShaderParameter("rim_d", stand);
            _deepInk.SetShaderParameter("rim_span", inradius);
            _deepInk.SetShaderParameter("squash", Squash);
            _deepInk.SetShaderParameter("rise", RiseFactor);
        }
    }

    /// <summary>One vertex of the surface, with the ordinal of the cell it
    /// belongs to written into UV2.</summary>
    private static void Lay(SurfaceTool st, Vector3 top, Vector3 off, int ord,
                            float halfX, float halfZ, Vector2 inset)
    {
        st.SetColor(Pond);
        // +Z reads as +v, the same way the ground art is laid on a top face:
        // two conventions in one scene is how a texture comes out mirrored on
        // one surface and nobody can say against what.
        st.SetUV(new Vector2(0.5f + inset.X * off.X / (2.0f * halfX),
                             0.5f + inset.Y * off.Z / (2.0f * halfZ)));
        st.SetUV2(new Vector2(ord, 0.0f));
        st.AddVertex(top + off);
    }

    /// <summary>Which edges of a flooded cell leave the water, as the six-bit
    /// number the shader carries per cell. Packed rather than passed as six
    /// arrays for the reason a shader uniform array is capped at all: one float
    /// per cell says the same thing.</summary>
    private float Edges(Vector2I cell, Vector3[] corner)
    {
        bool[] shore = Shoreline(cell, corner);
        float mask = 0.0f;
        for (int k = 0; k < 6; k++)
            if (shore[k])
                mask += 1 << k;
        return mask;
    }

    /// <summary>
    /// Which edges of a flooded cell leave the water.
    ///
    /// <b>The edges are matched to their neighbours by direction, not by index.</b>
    /// Corner order comes from whoever built the hexagon and the headings come
    /// from the field; assuming the two lists line up would be right on this
    /// board and wrong on the first one that numbers its corners the other way,
    /// and the failure - foam along the wrong edges - looks like a tuning problem
    /// rather than a wiring one.
    /// </summary>
    private bool[] Shoreline(Vector2I cell, Vector3[] corner) =>
        Shoreline(Field, cell, corner, Squash, RiseFactor);

    private bool[] Freeboard(Vector2I cell, Vector3[] corner) =>
        Freeboard(Field, cell, corner, Squash, RiseFactor);

    /// <summary>The same, given its camera terms instead of reading them off the
    /// field - so it can be asserted without a board, the shape
    /// <see cref="World(Vector2, float, float, float)"/> is in and for the same
    /// reason.</summary>
    internal static bool[] Shoreline(HexField field, Vector2I cell,
                                     Vector3[] corner, float squash, float rise)
    {
        Vector2I[] next = Beside(field, cell, corner, squash, rise);
        var beach = new bool[6];
        for (int k = 0; k < 6; k++)
            beach[k] = !field.IsWater(next[k]);
        return beach;
    }

    /// <summary>
    /// Which cell lies over each of the hexagon's six edges.
    ///
    /// The matching by direction that <see cref="Shoreline"/>'s note argues for,
    /// pulled out because two questions are asked of it now - which edges leave
    /// the water, and which of those have nothing holding the water in. One
    /// answer, so the two cannot disagree about which edge is which.
    ///
    /// An edge no heading claims keeps the cell itself, which is the identity
    /// every caller wants: it is water if this cell is, and it stands at this
    /// cell's own height.
    /// </summary>
    internal static Vector2I[] Beside(HexField field, Vector2I cell,
                                      Vector3[] corner, float squash, float rise)
    {
        var beside = new Vector2I[6];
        for (int k = 0; k < 6; k++)
            beside[k] = cell;
        Vector2 here = field.FlatAnchor(cell);
        foreach (int heading in HexField.EdgeHeadings)
        {
            Vector2I next = HexField.Step(cell, heading);
            // Where that neighbour lies, in the surface's own space, so it can
            // be compared against the corners it was built from.
            Vector2 away = field.FlatAnchor(next) - here;
            Vector3 w = World(away, 0.0f, squash, rise);
            var want = new Vector2(w.X, w.Z).Normalized();
            int best = 0;
            float top = -2.0f;
            for (int k = 0; k < 6; k++)
            {
                Vector3 mid = corner[k] + corner[(k + 1) % 6];
                float dot = new Vector2(mid.X, mid.Z).Normalized().Dot(want);
                if (dot > top)
                {
                    top = dot;
                    best = k;
                }
            }
            beside[best] = next;
        }
        return beside;
    }

    /// <summary>
    /// Which edges of a flooded cell have the water standing free above the
    /// ground beside them - the edges that need a flank.
    ///
    /// <b>Not the same question as <see cref="Shoreline"/>, and the far bank is
    /// what tells them apart.</b> Every dry neighbour is a shore, but at the far
    /// bank the ground stands a whole level over the surface and it is the bank's
    /// own wall that the water meets: a flank there would be a sheet of water
    /// drawn in the same plane as the sand, fighting it for the depth test. What
    /// earns a flank is a neighbour whose top is <i>below</i> the surface - the
    /// pit's mouth, where the pond stands half a level over the beach, and the
    /// board's own outer edge, where the world is cut through and there is no
    /// neighbour at all.
    /// </summary>
    internal static bool[] Freeboard(HexField field, Vector2I cell,
                                     Vector3[] corner, float squash, float rise)
    {
        Vector2I[] next = Beside(field, cell, corner, squash, rise);
        var open = new bool[6];
        float top = field.WaterTop(cell);
        for (int k = 0; k < 6; k++)
            open[k] = !field.IsWater(next[k])
                      && (!field.InBounds(next[k]) || field.TopAt(next[k]) < top);
        return open;
    }

    /// <summary>
    /// The hexagon a cell's edges belong to: the outward unit normal of each
    /// edge and how far it stands off the middle, in world units, plus the
    /// inradius the foam band is a fraction of.
    ///
    /// <b>Off the corners the mesh was built from, not from an angle.</b> Corner
    /// order is whoever built the hexagon's business, and the camera squashes it
    /// in z besides - so a normal reasoned from "every sixty degrees" would be
    /// right on this board and quietly wrong on the first one that numbers its
    /// corners the other way or looks at it from a different height.
    /// </summary>
    internal static (Vector2[], float[], float) Rim(Vector3[] corner)
    {
        var normal = new Vector2[6];
        var off = new float[6];
        float inradius = float.MaxValue;
        for (int k = 0; k < 6; k++)
        {
            var a = new Vector2(corner[k].X, corner[k].Z);
            var b = new Vector2(corner[(k + 1) % 6].X, corner[(k + 1) % 6].Z);
            Vector2 along = b - a;
            var out_ = new Vector2(along.Y, -along.X).Normalized();
            // Oriented by the midpoint, because which way the perpendicular of a
            // segment points depends on which way round the segment was given.
            if (out_.Dot((a + b) * 0.5f) < 0.0f)
                out_ = -out_;
            normal[k] = out_;
            off[k] = a.Dot(out_);
            inradius = Mathf.Min(inradius, Mathf.Abs(off[k]));
        }
        return (normal, off, inradius);
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
    /// says so instead. The harness's pond is five and the tank bench's is
    /// fourteen.
    ///
    /// <b>A hundred and sixty because a board asked for it, and the warning is
    /// what asked.</b> <see cref="BoardMap.WaterMap"/> is twelve by twelve with
    /// seven cells of land in it, so a hundred and forty-three are flooded - and
    /// under the old sixty-four, seventy-nine of them shared the last slot, which
    /// on a board whose whole subject is water means the swell answers the wrong
    /// cell over more than half the pond. Not a silent failure, because the cap
    /// was named for exactly this; raising it is the answer the name was left
    /// there to get.
    ///
    /// <b>What it costs is a uniform block, and it is measured rather than
    /// guessed.</b> Four arrays index by this - <c>state</c>, <c>mark</c>,
    /// <c>hub</c> and <c>rim_mask</c>, with <c>span</c> on the drawn strip in
    /// place of the last two - and std140 gives every element of an array its own
    /// sixteen bytes whatever it holds, so a hundred and sixty is ten kilobytes
    /// against the sixteen a GL ES 3 block is guaranteed. Per frame it is a
    /// hundred and sixty floats pushed instead of sixty-four.
    ///
    /// <b>No board that fitted under the old cap moves</b>, and that is measured
    /// rather than argued: past the pond's own cell count every slot is dead
    /// ground the shader never indexes. The tank bench comes back bit for bit
    /// identical, and the harness by one pixel at one level - against a run to
    /// run noise floor of zero on this frame, which makes that the rounding this
    /// project already names and not the cap.
    /// </summary>
    public const int MaxWaterCells = 160;

    /// <summary>Whether the pond is computed or drawn. On by default because it
    /// is what the water is now; the strip stays reachable so the two can be put
    /// side by side, which is the only way either was ever judged here.</summary>
    public const bool DeepDefault = true;

    public bool Deep = DeepDefault;

    /// <summary>
    /// Where the sun is, as a direction to it. <b>The board's one sun</b>: the
    /// glint on the water, every shadow cast on the ground and the pass the
    /// tank's own shadow is baked under all read this and nothing else.
    ///
    /// <b>55 degrees up, which is the rig's key light - and the azimuth is the
    /// board's own, which the rig's is not.</b> The docstring used to claim both
    /// were matched. Worked back out of the code rather than remembered: a sun
    /// at rest points down -Z and <c>build_lighting</c> orients it by
    /// <c>Rz(az)*Rx(90-elev)</c>, so the key at <c>az -35, elev 55</c> points to
    /// <c>(-0.329, -0.470, 0.819)</c> in Blender; <c>camera_matrix(0, 30)</c>
    /// stands the camera at <c>(0, -0.866d, +0.5d)</c>, so Blender's -Y is
    /// toward the camera and this board's +Z is too. That makes the rig's key
    /// <c>(-0.329, 0.819, +0.470)</c> here - the same height, the same side, and
    /// <b>the opposite sign in Z</b>.
    ///
    /// The rig's key sits on the camera's side, as a three-point key does, and a
    /// sun there throws every shadow <i>up</i> the screen, behind the thing
    /// casting it: a tree 100px tall would put its shadow 33px up, which is
    /// under its own crown. So the board keeps the sun behind it and the shadows
    /// come toward the viewer, where they can be seen. What is given up is the
    /// azimuth of the key highlight on 183px of armour, which is not a thing the
    /// eye reads; what is bought is the direction of every shadow, which it is.
    ///
    /// This is not two suns quietly disagreeing: <c>ground_shadow</c> replaces
    /// the rig wholesale for its own pass - one sun, no fill - so the shadow's
    /// sun and the beauty rig's key have always been separate objects. This is
    /// the shadow's, and it is declared here.
    /// </summary>
    public static readonly Vector3 Sun =
        new Vector3(-0.40f, 0.82f, -0.41f).Normalized();

    /// <summary>
    /// Where a point one world unit above the ground throws its shadow, as an
    /// offset in the ground's own XZ.
    ///
    /// The ray from a point at height <c>h</c> reaches <c>y=0</c> after
    /// <c>h/Sun.y</c>, so the offset is <c>-h*Sun.xz/Sun.y</c> and this is that
    /// per unit. Its length is <c>cot(elevation)</c> - 0.70 at 55 degrees - and
    /// <b>that number is the whole reason terrain shadows on this board are a
    /// hem rather than a shadow</b>: a level stands 53.7 world units at the
    /// default grade, so a one-level wall throws 37.6 units, which is 0.18 of a
    /// cell. Named here so the next person to find the relief unconvincing
    /// lowers the sun on purpose rather than by accident - and pays for it in
    /// the water's glint and in a re-render of every tank's shadow, because
    /// those read this too.
    ///
    /// A tree is the case this does work for: 100px of screen height is 115
    /// world units, which throws 80 - 0.37 of a cell, and clear of the crown.
    /// </summary>
    public static readonly Vector2 SunCast =
        new(-Sun.X / Sun.Y, -Sun.Z / Sun.Y);

    /// <summary>How many hulls the surface can draw a waterline round at once.
    /// Three tanks and a slot spare; a cap because a shader array needs one.
    /// </summary>
    public const int Collars = 4;

    /// <summary>How many columns of the groundline the waterline round a hull is
    /// drawn from. Not the table's own 256: what goes to the shader is one
    /// heading's row, resampled, and 64 of them across a 183px hull is a sample
    /// every three pixels of a curve the band is three pixels wide - interpolated
    /// between, the quantisation lands under the line's own thickness. The whole
    /// board's worth is <see cref="Collars"/> times this, which is what fits in a
    /// uniform array without asking what the driver's limit is.</summary>
    public const int HullLine = 64;

    /// <summary>How wide the waterline round a hull is, in the sprite's own
    /// pixels - so it grows with the tank the size dial made bigger rather than
    /// staying a fixed smear on screen.</summary>
    public const float HullBand = 6.0f;

    /// <summary>
    /// How much of the water's own colour a hull takes once it is all the way
    /// under - against <see cref="Pond"/>'s own alpha, which is what the water
    /// does to a hull standing at the surface.
    ///
    /// <b>Depth is said twice, and it has to be.</b> Nothing here writes depth
    /// but the ground, so a submerged hull is still drawn over the pond and
    /// keeps every one of its own pixels: at the surface tint it came out as a
    /// small bright tank standing on the water. Behind a metre of water it is
    /// mostly water, and that is this. Not one, because the hull is still a
    /// target that has to be found and finished off - see
    /// <see cref="Shrunk"/>.
    /// </summary>
    // 0.92 while the drowned hull stood on the level's bed under 39px of water
    // and was shrunk to a third; it goes down to the drawn bed at its own size
    // now and the pond over it is darker for the depth, so at 0.92 it was gone -
    // a faint rectangle on the sunk board, docs/swim-plan.md step 5. Four
    // fifths keeps it a dark shape under the water, which is what the rules
    // need seen.
    public const float Fathom = 0.8f;

    /// <summary>How far under the surface the drawn top of a drowned tank ends
    /// up, as a share of the water's own depth over the bed. Under one, so the
    /// last of it is inside the water rather than touching the line.</summary>
    // 0.85 while the drowned hull stood on the level's bed and the shrink alone
    // had to put its silhouette under the drawn surface; 1.0 while the shrink
    // was measured against the deck and the turret was meant to stay out; 0.9
    // for one round against the drawn crown, far rims and all, which put a
    // medium at half its size and read as a toy. The whole tank goes under now
    // (docs/swim-plan.md, step 9), measured against its own height
    // (AtlasSet.RoofRisePx), and this is only the rounding: the water three
    // hundredths over the roof, so that the top code of the map is under it
    // and not on it.
    public const float SunkenMargin = 0.97f;

    /// <summary>
    /// How small a tank is drawn as it drowns: one while it is holding itself
    /// up, and small enough by the end that its drawn crown is under the
    /// surface.
    ///
    /// <b>This is how the board says the depth the bed cannot, and it exists
    /// because position cannot say it.</b> A drowning hull goes down to the
    /// drawn bed (<see cref="TankTick.Afloat"/>) and no further: a screen row
    /// here is a place on the ground, so a hull sent on down the water column
    /// walks off its own hexagon - which is what the settle that came before
    /// this did, and it was read off the board as "the unit is not on its hex".
    /// Size costs no rows: the tank stands on its cell's anchor to the pixel
    /// and recedes.
    ///
    /// <b>And the end of it is measured rather than chosen, because the tank
    /// is drawn over the water and not behind it.</b> Nothing writes depth here
    /// but the ground, so the pond can never cover a hull - the sprite tints
    /// its own submerged half instead (see <c>PaintShader</c>). A tank tinted
    /// blue while it still stands a turret above the surface reads as a blue
    /// tank in shallow water, not as a wreck under it, so what makes the picture
    /// is every pixel of the armour measured under: this brings the tank's own
    /// height (<see cref="AtlasSet.RoofRisePx"/> - the turret's roof or the
    /// casemate's crest, aerial and gun tip left out) down to
    /// <see cref="SunkenMargin"/> of the water's depth, and no further. Water
    /// as deep as the tank is tall shrinks nothing, which is the events pond at
    /// its default bed (<see cref="HexField.DeepBed"/>); a tall tank in shallow
    /// water shrinks by exactly what overtops it. Not the drawn crown: the far
    /// rim of a roof is drawn higher than it stands by its distance behind the
    /// centre, and measured against that a medium ended at half its size and
    /// read as a toy.
    ///
    /// <b>Not nought, because the hull is still on the board.</b> GDD match.md
    /// keeps a drowned tank as a target that has to be finished off, and
    /// field.md has it blocking movement and the line of fire. A cell that
    /// stops a tank and holds a target may not look like open water - so what
    /// is left is a small shape under the pond's own tint, which is what
    /// "under water" looks like on a board whose water is translucent.
    ///
    /// <b>Applied to the billboard about its contact point and to nothing
    /// else</b> - see <see cref="Place"/>. Never to
    /// <see cref="TankSprite.BodyScale"/>: that is the class's own size, and
    /// the hull span, the track gauge, the belt's pitch, a mine's footprint and
    /// the width of the selection ring are all measured off it. A hull that
    /// shrank through it would not be a tank going under, it would be a tank
    /// turning into a smaller class on the way down.
    /// </summary>
    /// <param name="tall">How tall the tank is drawn, in board px - the drawn
    /// crown over the contact row, the class's size already in it.</param>
    /// <param name="deep">How far the water it is in stands over the bed it is
    /// standing on, in the same px.</param>
    public static float Shrunk(float sink, float tall, float deep) =>
        Mathf.Lerp(1.0f, tall <= 1e-4f || deep <= 0.0f ? 1.0f
                         : Mathf.Clamp(deep * SunkenMargin / tall, 0.05f, 1.0f),
                   Mathf.Clamp(sink, 0.0f, 1.0f));

    /// <summary>
    /// Where the water's half of the line is drawn in one column of the hull's
    /// table, given the turret standing in it - <c>PaintShader</c>'s turret
    /// block as one expression, so the two halves of the line cannot part.
    /// <paramref name="row"/> is the table's own answer (nought for a column
    /// under entire), <paramref name="foot"/> and <paramref name="crown"/> the
    /// turret's bottom and top rows in the column (-1 for no turret), and
    /// <paramref name="over"/> how many rows the water stands above the deck
    /// and <paramref name="rise"/> how many the turret's roof does. The line
    /// is held at the foot less the water while the water is below the roof,
    /// and let go once it is over it - the roof is flat and goes under at once,
    /// so the table's own answer stands - or, for the water's half alone,
    /// turned to nought (the table's word for under entire) once the held line
    /// would stand above the column's crown.
    /// </summary>
    public static float TurretLine(int row, int foot, int crown, float over,
                                   float rise)
    {
        if (foot < 0 || over >= rise)
            return row;
        float rim = foot - over;
        return rim >= crown ? Mathf.Max(row, rim) : 0.0f;
    }

    /// <summary>
    /// Whether the crest ahead of a wading hull is painted as a band of foam
    /// dilated off its own waterline.
    ///
    /// <b>Off, because the field pushes the water instead now.</b> A hull under
    /// way puts a dipole into <see cref="Ripples"/> and the crest is what the
    /// surface does about it: a real ridge with a real slope, shedding real arms,
    /// which is a different thing from a band of white paint held at a fixed
    /// stand-off. The band read as a thick collar in front of the tracks, and that
    /// is what it was.
    ///
    /// <b>Kept rather than deleted, on the bought flash sheet's precedent:</b> "the
    /// pushed one is better" stays an assertion until the two can be put side by
    /// side. See --bow, and note that the two are not exclusive - the band is
    /// drawn over the field, so asking for both is a fair way to see how much of
    /// the old one was the field's job all along.
    /// </summary>
    public bool Bows = BowBandOnByDefault;

    /// <summary>Whether the painted bow band starts on. Named rather than left in
    /// the field's initialiser, for <see cref="Recoil.ShearOnByDefault"/>'s reason:
    /// a default nobody can point at is a default nobody notices being flipped.
    /// </summary>
    public const bool BowBandOnByDefault = false;

    /// <summary>
    /// How far ahead of its own waterline the bow crest stands, in the sprite's
    /// own pixels, at full push.
    ///
    /// <b>The crest is that line pushed along the way the tank is going, and not
    /// a shape drawn in front of it.</b> A prow arc hung off the hull would be
    /// right about where the tank is and wrong about its shape everywhere - the
    /// same objection an ellipse round the hull already lost to, and the same
    /// answer: the silhouette is already measured, one row per column per
    /// heading, so the crest is a directional dilation of it and cannot disagree
    /// with the collar an inch away.
    ///
    /// <b>Which side is the bow needs no test, and that is what the offset buys.</b>
    /// Stepping <i>back</i> along the travel direction and asking the table there
    /// puts the band outside the silhouette at the leading end and inside it at
    /// the trailing one - and inside is behind the sprite, which is drawn over
    /// the water. So the stern's half hides itself, exactly as a hull does.
    ///
    /// <b>One number and not two, and that is a derivation rather than a
    /// tidy-up.</b> The dilation offsets by so much and spreads by so much, and
    /// the crest is filled from the hull outward exactly when those two are
    /// equal: any less and it opens a gap of clean water between the foam and
    /// the tank, any more and its inner half is drawn under the sprite, where
    /// nothing is ever seen. So the pair is one dial, and this is what it
    /// measures - how far past the silhouette the water is thrown.
    ///
    /// <b>Judged against the wake and not against the collar.</b> A waterline is
    /// 6px because it is a line; a bow wave is a body of churned water and its
    /// neighbour on the far side of the hull is a wake stamp 40 world units
    /// across. At 36 sprite px of a hull drawn some 180 wide the crest is a fifth
    /// of the tank, which is where it stops reading as a thick collar.
    /// </summary>
    public const float BowSwell = 36.0f;

    /// <summary>Where the crest's peak stands, in the sprite's own px, for a hull
    /// whose travel is <paramref name="run"/> - GroundDirection times the share
    /// of the ford's ceiling it is doing. The shader's own expression, named here
    /// because this is the half of the pair the projection belongs to and the
    /// check has to be able to ask for it: a bow that foreshortens its opacity
    /// instead of its reach looks identical in the source and is invisible
    /// head-on, which is the heading that shows a bow best.</summary>
    public static float BowStandoff(Vector2 run) => 0.5f * BowSwell * run.Length();

    /// <summary>How far the foam licks up the paint at the waterline, in the
    /// sprite's own pixels.
    ///
    /// <b>It is not the whole band and never was, which is what makes this
    /// number readable next to <see cref="HullBand"/>.</b> The profile is
    /// asymmetric - foam sits on the water and only licks a little way up the
    /// paint - so of this figure the wet side gets all of it and the dry side
    /// four tenths. At 5.0 that is 7.0px across the silhouette against the
    /// collar's 6.0 beside it, which is the point: the two halves of one line
    /// now read as one thickness. It was 2.5, giving 3.5 against 6.0, and the
    /// line visibly thinned as it crossed onto the armour.
    ///
    /// Wider than this and the old warning holds - a wide band here is not foam,
    /// it is a repaint of the hull - but the ceiling is the dry side's 0.4 of it,
    /// and 2.0px up a hull drawn 49px tall is a lick.</summary>
    public const float LapBand = 5.0f;

    /// <summary>The swell's own scale and speed, as the foam's tearing reads
    /// them. Named here because two materials evaluate that field now - the
    /// surface for its normals and its foam, the sprite for the half of the
    /// waterline that lies on the armour - and a second copy of either number is
    /// two rhythms in one line.</summary>
    public static readonly Vector2 Swell2D = new(0.02f, 1.0f);

    /// <summary>How fine the crumple that displaces every foam edge is, in world
    /// units, and how far it pushes as a fraction of that edge's own band.
    ///
    /// <b>One pair for all three edges, which is the whole of the unification.</b>
    /// The shore is measured in world units and the collar and the lap in sprite
    /// pixels; as a fraction the dial needs no conversion at either, so there is
    /// one answer to "how ragged is the water's edge" rather than three that
    /// drift apart.
    ///
    /// The scale is a feature every ~20 world units against a hex some 93 across,
    /// so a cell's edge carries several lobes and never reads as one straight
    /// line. Finer and the edge frays into noise at this zoom; coarser and a
    /// whole edge bows one way, which reads as the hexagon again with a bend in
    /// it.
    ///
    /// The push is under a half deliberately, and the pair moved together. At
    /// 0.6 over a 12-unit cell the hull's line zigzagged three pixels every six -
    /// not a shore but a row of teeth - and the answer to that is a longer
    /// wavelength <i>and</i> a shorter push, because either alone just trades one
    /// wrong reading for the other.</summary>
    public static readonly Vector2 EdgeCrumple = new(0.05f, 0.45f);

    /// <summary>How wide the shore's own band is, as a fraction of a tile's
    /// inradius.
    ///
    /// <b>Halved, and paid for by the crumple rather than given up.</b> It was
    /// 0.10, and at that width the bank read as a haze standing off the water's
    /// edge rather than as foam on it - wide enough that the eye took the whole
    /// gradient for the edge, and straight enough along a hexagon's side that
    /// what it drew was the cell. Displacing the distance buys back the raggedness
    /// that width was standing in for, so the band can be the thing it is:
    /// narrow, and where the water actually stops.</summary>
    public const float Beach = 0.05f;

    /// <summary>How deep the water has to be before the view-ray half of the
    /// shore stops foaming, in world units. Halved with <see cref="Beach"/> and
    /// for the same reason - the two are the same edge asked two ways, and
    /// narrowing one alone would leave the far walls wearing the old haze while
    /// the near banks wore the new line.</summary>
    public const float ShoreDepth = 5.0f;

    /// <summary>How fine the lapping of the shore is in world units, and how far
    /// it runs the water up a slope and back down, in world units of depth along
    /// the view ray.
    ///
    /// <b>A pair and not a share of the foam band, because it is bounded by
    /// something else entirely.</b> The band is a width and can be any width; this
    /// is the displacement of a contour on a slope, and past a point it stops
    /// moving a line and starts folding one. Along the shore the bed does not
    /// change and across it it changes at the ramp's own gradient - StepGrade times
    /// the squash, 0.125 of depth per world unit at grade 0.25 - so a field whose
    /// own gradient beats that crosses zero more than once, and the water comes
    /// apart into islands standing on dry sand. The crumple's two octaves put that
    /// bound at roughly <c>0.05 x</c> the feature size.
    ///
    /// <b>And the film is what lets this sit past it.</b> Swept: a fifth under the
    /// bound (0.032, 1.2) reads as one bow per ramp and, on the flatter ramps, as
    /// the straight segment it replaced; the foam's own scale at a beach amplitude
    /// (0.05, 2.25) is twice over and drew a row of hard teeth. This pair is over
    /// it, and with <see cref="LapEdge"/> feathering the tip the fold arrives as a
    /// tongue of water on wet sand instead. So the bound is the bound of a
    /// <i>cut</i>, and what bought the scallops was the softness and not the size.
    ///
    /// <b>The bound is the board's, not this file's, and by more than a little.</b>
    /// The grade is a dial, so how far over this sits depends on which board is
    /// asking: 1.48 on the tank bench, which keeps HexField's own 0.25 and is where
    /// the picture was judged, and 1.05 on the harness, whose panel opens at 0.35.
    /// That is why it is written down as a ratio to the ground's gradient and
    /// asserted as a band rather than set as a distance in pixels - a flatter board
    /// tightens the bound, and it is the one way this pair can go wrong later.</summary>
    public static readonly Vector2 LapWave = new(0.04f, 1.8f);

    /// <summary>How soft the tip of that run is, in the same units.
    ///
    /// <b>A film, not a cut.</b> The last inch of a wave up a beach is a sheet a
    /// few millimetres deep over wet sand, so the edge wants a ramp rather than a
    /// boundary - and the same ramp is what keeps a fold from reading as a tooth
    /// where the field's gradient does clip the bound. Half a unit of depth is
    /// about four world units of beach, the narrowest strip that is more than one
    /// pixel of itself at play zoom.
    ///
    /// <b>Spent on what the water adds, never on its alpha.</b> Feathering ALPHA
    /// is the obvious build and the self test refuses it by name: owning every
    /// pixel it covers is the whole difference in kind between this surface and the
    /// strip. It is not needed anyway - the colour converges on the ground by
    /// itself as the thickness goes to nothing. What stops dead at a cut is what
    /// gets added regardless of depth: the foam and the two highlights.</summary>
    public const float LapEdge = 0.5f;

    /// <summary>The colour of foam, wherever it is drawn. One constant because
    /// the line at the hull and the line at the bank are the same water, and two
    /// would be two things to keep in agreement.</summary>
    public static readonly Color FoamInk = new(1.0f, 1.0f, 1.0f);

    private ShaderMaterial? _deepInk;
    private float _deepClock;

    private ShaderMaterial DeepWater()
    {
        _deepInk ??= new ShaderMaterial
        {
            Shader = new Shader
            {
                Code = string.Format(CultureInfo.InvariantCulture, DeepShader,
                                     MaxWaterCells, FoamCut, FoamLift, FoamRung,
                                     Wake.Max, HullLine, Collars * HullLine,
                                     FoamEdge),
            },
            RenderPriority = WaterOrder,
        };
        _deepInk.SetShaderParameter("sun", Sun);
        _deepInk.SetShaderParameter("wake_seed", Wake.Seed);
        _deepInk.SetShaderParameter("wake_spread", Wake.Spread);
        _deepInk.SetShaderParameter("bow_swell", BowSwell);
        _deepInk.SetShaderParameter("bands", (float)WaterArt.Bands);
        // Named here as well as defaulted in the shader, because the sprite is
        // handed the same two numbers from the same constants: a default living
        // only in the GLSL would be one of the pair set from C# and the other
        // not, which is how the two halves of this line would part again.
        _deepInk.SetShaderParameter("edge_crumple", EdgeCrumple);
        _deepInk.SetShaderParameter("beach", Beach);
        _deepInk.SetShaderParameter("shore", ShoreDepth);
        _deepInk.SetShaderParameter("lap_wave", LapWave);
        _deepInk.SetShaderParameter("lap_edge", LapEdge);
        _deepInk.SetShaderParameter("wave_scale", Swell2D.X);
        _deepInk.SetShaderParameter("wave_speed", Swell2D.Y);
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
            // and which camera is looking at them is not its business - which is
            // also why it carries the lift beside the point rather than applying
            // it, see Ground.
            Vector2 at = Sea?.Mark.Length > i ? Sea.Mark[i] : Vector2.Zero;
            float rest = Sea?.Rest.Length > i ? Sea.Rest[i] : 0.0f;
            Vector3 w = Ground(at, rest);
            mark[i] = new Vector2(w.X, w.Z);
        }
        ink.SetShaderParameter("state", state);
        ink.SetShaderParameter("mark", mark);
        ink.SetShaderParameter("foam_reach", _pondReach);
        // Only the drawn strip has a loop to run part of; the computed surface
        // has no frames at all, so a span means nothing to it.
        _swellInk?.SetShaderParameter("span", span);
        if (_deepInk is null)
            return;
        // Through the same mapping the surface's own vertices went through, for
        // the mark's reason: the shader compares these against world.xz, and the
        // trail lives in the tanks' space until it gets here.
        var spot = new Vector2[Wake.Max];
        var age = new float[Wake.Max];
        // Past its life is the empty slot, so there is no second way of saying
        // dead that could disagree with the first.
        for (int i = 0; i < Wake.Max; i++)
            age[i] = 2.0f;
        int live = Mathf.Min(Trail?.Count ?? 0, Wake.Max);
        for (int i = 0; i < live; i++)
        {
            Vector3 w = Ground(Trail!.At[i], Trail.Lift[i]);
            spot[i] = new Vector2(w.X, w.Z);
            age[i] = Trail.Age[i];
        }
        _deepInk.SetShaderParameter("wake_at", spot);
        _deepInk.SetShaderParameter("wake_age", age);
        // And the ripple field, which is one texture and its box rather than an
        // array of anything: what it holds is a surface, so the shader looks it up
        // where the fragment is instead of being told about every source that made
        // it. Left unset while there is no field, and that is the whole of how a
        // board without one costs nothing - an unset sampler reads zero, so all
        // three readers of it are no-ops.
        if (Wash is { Wide: > 0, Sheet: not null })
        {
            _deepInk.SetShaderParameter("wash", Wash.Sheet);
            _deepInk.SetShaderParameter(
                "wash_box", new Vector4(Wash.Box.Position.X, Wash.Box.Position.Y,
                                        Wash.Box.Size.X, Wash.Box.Size.Y));
            _deepInk.SetShaderParameter("wash_step", Wash.Cell);
            _deepInk.SetShaderParameter("wash_relief", WashRelief);
            _deepInk.SetShaderParameter("wash_crest", WashCrest);
            _deepInk.SetShaderParameter("wash_band", WashBand);
            _deepInk.SetShaderParameter("wash_on", Wash.Enabled ? 1.0f : 0.0f);
        }
        else
        {
            _deepInk.SetShaderParameter("wash_on", 0.0f);
        }
    }

    /// <summary>The field of ripples on the pond, or null to leave it flat. What
    /// the surface reads it for is its normals, its foam and how far the water runs
    /// up a beach - never anything that decides. See Ripples.</summary>
    public Ripples? Wash;

    /// <summary>How much of the ripple field's own slope goes into the surface
    /// normal.
    ///
    /// <b>One, and that is a statement rather than a default.</b> The field is a
    /// height in world units and the normal is built in world units, so the honest
    /// gain is unity and there is nothing to fit: a ring is as steep as the water
    /// it is made of. What it costs is that the amplitude dial is
    /// <see cref="Ripples.Push"/> and only that, which is where a dial about how
    /// hard a hull shoves water belongs.</summary>
    public const float WashRelief = 1.0f;

    /// <summary>How tall a ripple has to be before it goes white, in world units,
    /// and over how much more of it the foam comes fully in.
    ///
    /// <b>This is the bow wave, and the number is what makes it one.</b> Foam on
    /// every crest is what the first cut of the surface's own swell did and it read
    /// as scratches; foam only on the tall crests picks out the one thing on this
    /// pond that is genuinely taller than the chop, which is the water a hull is
    /// pushing. Measured against <see cref="Ripples.Push"/>: a hull at full shove
    /// stands about four units of water in front of itself, so a cut at two fifths
    /// of that whitens the crest and leaves the rings it sheds grey.</summary>
    public const float WashCrest = 3.5f;

    /// <summary>How much taller than <see cref="WashCrest"/> a crest is when its
    /// foam is fully in - see there.</summary>
    public const float WashBand = 2.5f;

    /// <summary>What each flooded cell is doing. Null leaves the pond calm, which
    /// is how it behaved before the water answered anybody - see --still-water.
    /// </summary>
    public Swell? Sea;

    /// <summary>The trail the tanks have left on the water. Null draws none,
    /// which is the pond before this - see --no-wake.</summary>
    public Wake? Trail;

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
// <b>The depth test is off and the clip is done here instead</b> - see the lap
// in fragment(). Off costs nothing and gives up nothing: what clips this pass is
// the opaque ground, which is exactly what zbuf holds, so the same comparison
// made by hand is the same answer - and the props and the tanks were never
// clipped by depth anyway, they are drawn after it (WaterOrder is below both).
// What it buys is the freedom to reach *past* the geometric line, which the
// hardware test cannot give at any setting.
render_mode unshaded, cull_disabled, depth_draw_never, depth_test_disabled;

uniform sampler2D screen : hint_screen_texture, filter_linear_mipmap;
uniform sampler2D zbuf : hint_depth_texture, filter_linear;

// Our own clock, wrapped, for --capture's reason: TIME is the wall clock and
// two runs have to land on the same picture. See FrameClock.FixedStep.
uniform float time = 0.0;
uniform vec3 sun = vec3(0.0, 1.0, 0.0);
uniform float wave_scale = 0.02;
uniform float wave_speed = 1.0;
uniform float relief = 0.30;
uniform vec3 shallow : source_color = vec3(0.24, 0.52, 0.50);
uniform vec3 deep : source_color = vec3(0.06, 0.22, 0.28);
uniform vec3 absorb : source_color = vec3(0.34, 0.10, 0.06);
uniform float clarity = 110.0;
// <b>And the same length for a hollow filled to the brim, which is a different
// body of water.</b> Beer-Lambert is honest here - the thickness comes off the
// depth buffer and a deep cell really does stand nearly twice a ford's - but at
// one clarity for the board that difference arrives as a fifth of a shade, and
// the picture said the two waters were the same water at two heights. They are
// not: a ford is a run over a bed, and deep water on this board is a hollow that
// filled and has been standing. Standing water carries what settled in it.
//
// So the fork is on the length light gets through and not on a tint, for the
// reason the pond's own ramp swap was: the physics stays the physics, and what
// changes is the water. Judged by whether the bed is legible - a ford shows the
// stones it runs over, and a hollow you would drown in does not show its floor.
uniform float murk = 26.0;
// The body colour at the far end of that, and it is not <c>deep</c> pushed
// darker: standing water goes green-brown as it goes opaque, where clear water
// goes blue. Same argument as the burst's litter ramp - which way the colour
// walks is what says what the substance is.
uniform vec3 silt : source_color = vec3(0.055, 0.132, 0.115);
// Which cells of the pond are that, one per cell, written by BuildWater.
uniform float sink[{0}];
uniform float refraction = 22.0;
uniform float gloss = 210.0;
uniform float sheen_gloss = 14.0;
uniform float sheen = 0.07;
uniform float fine = 3.4;
uniform float sparkle = 0.55;
uniform float glint = 0.85;
uniform float shore = 5.0;
uniform float foam = 0.0;
uniform float foam_cut = {1};
uniform float foam_rung = {3};
uniform float foam_reach = 1.0;
uniform vec3 foam_ink : source_color = vec3(1.0, 1.0, 1.0);
uniform float state[{0}];
uniform vec2 mark[{0}];
uniform float bands = 1.0;
// The trail: where each stamp was laid, and how far through its life it is.
// Two arrays of the plainest types there are, and not one of vec4, because a
// vec4 array is quietly not accepted here - the uniform stayed at its default
// and the trail simply never arrived. Nothing said so: the water rendered, the
// bench counted its stamps, and the surface drew none of them.
uniform vec2 wake_at[{4}];
uniform float wake_age[{4}];
uniform float wake_seed = 9.0;
uniform float wake_spread = 24.0;
uniform float wake_on = 1.0;
// Where the wading tanks are drawn, and the shape of the line each one makes
// where it meets the ground. It cannot see them any other way: nothing writes
// depth but the ground, so to the water a tank is not there at all - but the
// atlas already measured that line, one row per column per heading, and that is
// what these carry. hull_at is the sprite's anchor in the harness's own 2D px,
// hull_pivot is the atlas anchor inside the tile, hull_size is (body scale, tile
// width) and hull_line is the groundline resampled to {5} columns across the
// tile, negative where the tank has no pixel in that column at all.
//
// hull_dip is how far up that line the water stands, in the same sprite px, and
// it is the whole difference between a line and a staircase: the surface meets
// a tank at its waterline, not where it touches the bottom, so a collar drawn
// on the table itself sits a whole depth below the one the sprite cuts at and
// the two halves of one line are drawn a plane apart. It is the sprite's own
// expression, so they cannot part.
uniform vec2 hull_at[4];
uniform vec2 hull_pivot[4];
uniform vec2 hull_size[4];
uniform float hull_line[{6}];
// And the bottom of that same silhouette, which is a different line and is here
// for a reason worth stating. hull_line is where the water meets the hull *in
// the world*, and it runs up the armour - so the crest dilated off it lands
// inside the sprite, which is drawn over the water, and is never seen. What the
// eye takes for the water in front of a tank starts where the sprite stops, and
// that is this. Same table, same scan, one row lower down the same column: the
// collar is a line with a half on the armour, the crest is foam on water the eye
// can actually see, and they want different halves of one measurement.
uniform float hull_hem[{6}];
uniform float hull_dip[4];
uniform float hull_band = 3.0;
// Which way each hull is going, in the harness's own 2D px, and how hard it is
// pushing. Two things and not one, and the first cut of it folded them into a
// single unnormalised vector on the flash's precedent - wrongly. That precedent
// is about *lengths on the ground*: GroundDirection's own length is the share of
// a ground movement the projection left, so it belongs to how far the crest
// stands off, which is a ground length, and speed belongs there too. It does not
// belong to how white the foam is. Folded, a tank driving into the camera lost
// half its reach and half its opacity for one reason counted twice, and head-on
// - the heading that shows the bow best - the crest all but vanished.
uniform vec2 hull_run[4];
uniform float hull_push[4];
uniform float bow_swell = 36.0;
uniform float bow_on = 1.0;
// The two camera terms, so a fragment of the water can be put back into the 2D
// space the sprites are placed in. Uniforms rather than constants because the
// field owns them and a second copy is a second thing to keep in agreement.
uniform float squash = 0.5;
uniform float rise = 1.0;
// Where each flooded cell's middle is, which of its six edges leave the water,
// and the hexagon those edges belong to: outward normals and how far each edge
// stands off the middle, in world units.
uniform vec2 hub[{0}];
uniform float rim_mask[{0}];
uniform vec2 rim_n[6];
uniform float rim_d[6];
uniform float rim_span = 1.0;
// Half what it was, and the halving is the shore meeting the hull's line rather
// than the hull's line being let out to meet a haze. See Stage3D.Beach.
uniform float beach = 0.05;
// How fine the foam's crumple is in world units, and how far it pushes an edge
// as a fraction of that edge's own band. Shared verbatim with the sprite, which
// draws the half of the waterline that lies on the armour.
uniform vec2 edge_crumple = vec2(0.08, 0.6);
// How fine the lapping of the shore is in world units, and how far it runs the
// water up a slope and back down, in depth along the ray. A pair on the shape of
// edge_crumple above and for the same reason: a scale means nothing without the
// amplitude read at it. See Stage3D.LapWave.
uniform vec2 lap_wave = vec2(0.04, 1.8);
// And how soft the tip of that run is, in the same units. See Stage3D.LapEdge.
uniform float lap_edge = 0.5;
// The pond's own displacement, a float of world height per texel, over the box in
// world XZ it is laid on and at the grain it is laid at - see Ripples. A texture
// and not an array of sources, because what is being handed over is the *surface*:
// the sum of everything that ever pushed it, which is the whole of what a field
// buys over a stamp. hint_default_black is how a board without one costs nothing -
// an unset sampler reads zero, so all three readers below are no-ops.
uniform sampler2D wash : filter_linear, repeat_disable, hint_default_black;
uniform vec4 wash_box = vec4(0.0, 0.0, 1.0, 1.0);
uniform vec2 wash_step = vec2(1.0);
uniform float wash_relief = 1.0;
uniform float wash_crest = 3.5;
uniform float wash_band = 2.5;
uniform float wash_on = 0.0;

varying vec3 world;

{7}

// The chop on top of it: finer, quicker, and <b>not</b> folded. This is what
// the sun is actually catching, and it is a separate field rather than two
// more octaves for a reason the measurement gave. A highlight run was 7.93 px
// across against 3.68 down - a ratio of 2.15, which is exactly the camera's
// own squash - so the streaks were never anisotropic at all: they were round
// on the water and flattened by the projection. What made them read as brush
// strokes was their size, so what fixes it is a smaller feature and a tighter
// lobe, not a different shape.
float chop(vec2 p) {{
    float h = 0.0;
    float a = 1.0;
    float f = 1.0;
    vec2 drift = vec2(-0.22, 0.31) * time * wave_speed;
    for (int i = 0; i < 2; i++) {{
        h += a * (vnoise(p * f + drift) - 0.5);
        a *= 0.5;
        f *= 2.7;
        drift = vec2(drift.y, -drift.x) * 2.1;
    }}
    return h;
}}

// Where tank `who` meets the water in the column a point of its tile falls in,
// in the sprite's own px, or -1 where it has no pixel in that column at all.
//
// A function because two things ask it now - the collar at the fragment, and
// the bow crest at the fragment stepped back along the way the tank is going -
// and a second copy of this lookup would be a second answer to where the
// waterline is, which is the one thing the whole arrangement exists to prevent.
// The pond's displacement at a point on it, in world units. Zero off the field's
// own box rather than clamped to its rim: a pond reaching past the field fades out
// of the simulation there, where clamping would smear the edge texel across the
// rest of it.
float washed(vec2 p) {{
    vec2 uv = (p - wash_box.xy) / max(wash_box.zw, vec2(1e-4));
    if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0)
        return 0.0;
    return texture(wash, uv).x * wash_on;
}}

float shoreline(int who, vec2 px, bool hem) {{
    float u = px.x / max(hull_size[who].y, 1.0);
    if (u < 0.0 || u > 1.0)
        return -1.0;
    float f = u * float({5} - 1);
    int k = int(floor(f));
    int lo = who * {5} + k;
    int hi = who * {5} + min(k + 1, {5} - 1);
    float a = hem ? hull_hem[lo] : hull_line[lo];
    float b = hem ? hull_hem[hi] : hull_line[hi];
    // No entry means no tank in that column - the gun swung out past the tracks
    // is the case it is there for - and a line through it would be drawn from
    // the two columns either side of a gap it is not in.
    if (a < 0.0 || b < 0.0)
        return -1.0;
    // Nought is that table's other refusal and it means the opposite of a gap:
    // the column is under water entire, so the armour is wet all the way up and
    // there is no line on it for this half to foam against. The sprite's half
    // does need a row there - it has a column to tint - which is why the one
    // table says it with a row nothing can be above rather than with a second
    // sentinel, and why this half has to read that row as a refusal. Left out,
    // the collar ramped from the turret's foot to the top of the tile and drew
    // two white threads standing out of the water beside a drowned tank.
    if (!hem && (a < 0.5 || b < 0.5))
        return -1.0;
    return mix(a, b, fract(f)) - (hem ? 0.0 : hull_dip[who]);
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
    float h = height(p, time, wave_speed);
    float hx = height(p + vec2(e, 0.0), time, wave_speed) - h;
    float hz = height(p + vec2(0.0, e), time, wave_speed) - h;
    // And the chop's own slope, at its own step. Sampled apart from the swell
    // because it is finer than the swell's step can resolve: differenced over
    // e it would be averaged away, which is how the sparkle got lost into the
    // creases in the first place.
    vec2 ce = vec2(0.12, 0.0);
    vec2 cp = p * fine;
    float c = chop(cp);
    float cx = chop(cp + ce) - c;
    float cz = chop(cp + ce.yx) - c;
    // And the sheet the tanks are actually pushing about, differenced at its own
    // grain. <b>A real slope, so it is the one term here with a unit</b>: the
    // swell's two are height differences taken over the e-step, which is the
    // convention this normal is built in - (-slope*e, e, -slope*e) - so a slope in
    // world units has to be multiplied by e to join them. Sampled at the grain and
    // not at e, because differencing a linearly filtered texture over less than a
    // texel returns the filter's own ramp rather than the field's.
    float wsh = washed(world.xz);
    vec2 wslope = (vec2(washed(world.xz + vec2(wash_step.x, 0.0)),
                        washed(world.xz + vec2(0.0, wash_step.y))) - wsh)
                  / max(wash_step, vec2(1e-4)) * wash_relief * e;
    vec3 n = normalize(vec3(-hx * relief - cx * sparkle - wslope.x,
                            e,
                            -hz * relief - cz * sparkle - wslope.y));

    // How much water is in front of the bottom. The prisms are opaque, so this
    // is the floor of the pit and the wall it climbs at the shore.
    float own = FRAGCOORD.z;
    float raw = texture(zbuf, SCREEN_UV).x;
    vec3 ndc = vec3(SCREEN_UV * 2.0 - 1.0, raw * 2.0 - 1.0);
    vec4 view = INV_PROJECTION_MATRIX * vec4(ndc, 1.0);
    float floor_z = -(view.z / view.w);
    float here_z = -(INV_PROJECTION_MATRIX * vec4(vec3(SCREEN_UV * 2.0 - 1.0,
                     own * 2.0 - 1.0), 1.0)).z;

    // <b>And the swell runs the edge up the beach and back down.</b> Where the
    // pond meets a slope the water ends on the intersection of two planes, which
    // is a straight segment - dead straight along the whole of a ramp, and read
    // as one because it is one.
    //
    // Pushed by the <b>same</b> crumple that pushes the foam, in the same coin,
    // and that is the point rather than a saving. The band was already displaced
    // by this field while the cut it faded into was not, so the foam wandered
    // *inside* a straight edge - the two halves of one line parting exactly as
    // the hull's collar and the shore are written not to. One push, read once,
    // and the band rides on the edge by construction.
    //
    // In depth along the ray, because that is the space the comparison is in: a
    // world height of w moves this fragment w * squash nearer, so a threshold
    // said in thickness is a wave height said in the only units the depth buffer
    // can be asked about. Signed, so the water reaches past the geometric line
    // as often as it falls short of it - the crest running up dry sand is the
    // half the hardware test could never draw.
    // <b>Its own scale, and the bound on it belongs to the ground.</b> This edge
    // is a contour on a slope: along it the bed does not change, and across it it
    // changes at the ramp's own gradient - StepGrade times the squash, 0.125 of
    // depth per world unit on this board. A field whose own gradient beats that
    // crosses zero more than once, and where it does the water comes apart into
    // islands sitting on dry sand. Measured: the foam's own crumple, at the
    // amplitude a beach needs, is a gradient of 0.29 - twice over - and it drew
    // exactly that, a row of hard teeth.
    //
    // <b>What buys the rest is the film, not a bigger number.</b> Held under the
    // bound the line comes back nearly straight - swept it, and a fifth under read
    // as one bow per ramp and nothing else. Past it, with the tip feathered, the
    // same fold arrives as a tongue of water on wet sand, which is what a wave up
    // a beach is. So the bound is the bound of a <i>cut</i>; a soft edge is
    // allowed past it, and that is the whole of why this pair sits where it does.
    // <b>And the ripples run up the beach on the same term, with no dial at all.</b>
    // A crest of h world units stands the surface h higher, which moves this
    // fragment h*squash nearer - the conversion this comment already states two
    // paragraphs up - so the honest amount is exactly that and a gain here would be
    // a second opinion about the projection. It is also what makes the shore the
    // one place the simulation is unmistakable: a ring arriving does not brighten
    // the bank, it moves it.
    float lap = crumple(world.xz * lap_wave.x, time * wave_speed) * lap_wave.y
                + wsh * squash;
    float bed = floor_z - here_z;
    if (bed + lap <= 0.0)
        discard;
    float thick = max(0.0, bed + lap);
    // Which cell this fragment is on, and therefore which of the two waters. Read
    // here rather than where the shore is worked out, because the absorption
    // below is the first thing that needs it; the shore reads the same expression
    // again a few dozen lines down.
    float murky = mix(clarity, murk, sink[int(UV2.x + 0.5)]);
    // How much of the last inch of the run is water at all - a film over wet sand
    // rather than a cut. It is also what keeps the fold from reading as a tooth
    // where this field's gradient beats the ground's: a hard edge shows a fold, a
    // soft one shows a tongue.
    float film = smoothstep(0.0, lap_edge, thick);

    // Bent by the surface, and the amount falls off in the shallows so the
    // shoreline does not tear. The tanks are drawn after this pass, so what is
    // being bent is the ground and nothing else.
    vec2 bend = n.xz * refraction * clamp(thick / max(murky, 0.001), 0.0, 1.0);
    vec2 uv = SCREEN_UV + bend / vec2(textureSize(screen, 0));
    vec3 bottom = texture(screen, uv).rgb;

    // Beer-Lambert on the way down and back: red goes first, which is the whole
    // reason open water is blue. Then the volume's own colour over the top of
    // what is left.
    float t = clamp(thick / max(murky, 0.001), 0.0, 1.0);
    vec3 through = bottom * exp(-thick * absorb / max(murky, 0.001));
    vec3 body = mix(shallow, mix(deep, silt, sink[int(UV2.x + 0.5)]), t);
    vec3 col = mix(through, body, t * 0.55);

    // One sun, worked out here. The camera is orthographic, so the view vector
    // is the same for every fragment and the highlight is a single dot product
    // - the one place this projection makes something cheaper instead of harder.
    vec3 eye = normalize((INV_VIEW_MATRIX * vec4(0.0, 0.0, 1.0, 0.0)).xyz);
    vec3 half_v = normalize(normalize(sun) + eye);
    // Two lobes on two normals, and the second normal is the point. Measured
    // one lobe at a time: the tight spark runs 4.26 px across and reaches 28,
    // the broad sheen runs 5.47 and reaches 39 - so what read as brush strokes
    // was the sheen, tracking the same ridge creases the swell is made of. It
    // gets a flattened normal that the creases barely reach, and stays what it
    // is for: the surface saying it is wet. The spark keeps the whole normal,
    // chop and all, because being small and moving is the whole of its job.
    // The ripples go into this one at full weight, where the swell is flattened to
    // a quarter. Not an oversight: what the sheen is for is the surface saying it
    // is wet, and it was flattened because the swell's creases are what made it
    // read as brush strokes. A ring is not a crease - it is the largest feature on
    // the water and the one the eye is following - so a sheen that ignored it would
    // be a wet surface with a dry ring in it.
    vec3 soft = normalize(vec3(-hx * relief * 0.25 - wslope.x, e,
                               -hz * relief * 0.25 - wslope.y));
    col += vec3(pow(max(dot(n, half_v), 0.0), gloss)) * glint
         + vec3(pow(max(dot(soft, half_v), 0.0), sheen_gloss)) * sheen;

    // Foam where the water meets something, and it is asked twice because the
    // two ways of asking fail in opposite places.
    //
    // Along the view ray: how soon the ray hits the bottom. Real foam under a
    // wall standing behind the water, and <b>nothing at all</b> where the shore
    // is on the near side, because there the ray carries on down to the floor.
    // That asymmetry is what left the ramp and the near bank bare.
    //
    // <b>Every edge below is pushed by the crumple rather than dimmed by it.</b>
    // The ridge field still breaks the foam into patches further down, and that
    // was all there was: a band multiplied by a mask keeps its own shape, so a
    // shore lying along a hexagon's edge stayed a straight line with holes in it
    // and read as the grid showing through the water.
    //
    // <b>And each is sampled on its own edge, never at the fragment.</b> Sampled
    // at the fragment the displacement varies across the band as well as along
    // it, so the band's width becomes a function of the field: where the gradient
    // approaches one the mapping folds and the line comes back as hard teeth.
    // Measured before it was believed - band 5px, field pushing 3px over a 12px
    // cell, which puts the gradient at 0.9 and the fold one step away. Taken at
    // the point on the edge itself the displacement is constant across the band
    // by construction, so no setting of the two dials can fold it.
    // Off the thickness the lap already moved, so the band starts at the water's
    // own edge. It carried this push itself while the edge stood still; both at
    // once would push it twice.
    float lip = 1.0 - smoothstep(0.0, shore, thick);
    // And in the plane of the water: how far this fragment is from an edge the
    // pond actually ends at. Independent of where the camera is by construction,
    // which is the whole point - a shore is where the water stops, not where the
    // eye happens to see the bottom rise.
    //
    // <b>Worked out here from the cell's edge mask, not carried on the vertices.</b>
    // Carried, the answer had to live on the corners so the two triangles sharing
    // one agreed about it - and a corner belongs to two edges, so one dry edge
    // wet the ends of both its neighbours. On a pond whose cells have two water
    // neighbours apiece that marks all six corners of nearly every cell, which is
    // foam round the whole rim: exactly the failure, and it was in the resolution
    // rather than in the wiring. A distance to a line is exact, seamless and says
    // nothing about the edges next to it.
    int cell = int(UV2.x + 0.5);
    vec2 off = world.xz - hub[cell];
    float near = 1e9;
    // And where on that edge the nearest point is, which is where its crumple is
    // read - see above. Costs one vec2 in a loop of six.
    vec2 foot = world.xz;
    for (int k = 0; k < 6; k++) {{
        if (mod(floor(rim_mask[cell] / exp2(float(k))), 2.0) < 0.5)
            continue;
        float span = rim_d[k] - dot(off, rim_n[k]);
        if (span < near) {{
            near = span;
            foot = world.xz + rim_n[k] * span;
        }}
    }}
    float bank_band = beach * rim_span;
    float bank = 1.0 - smoothstep(0.0, bank_band, near
                  + crumple(foot * edge_crumple.x, time) * edge_crumple.y
                    * bank_band);
    // And foam where a tank went through, off the band and the point it was
    // standing at - see Swell. Unchanged by any of this: it never needed depth.
    float lv = clamp(state[cell] / max(bands - 1.0, 1.0), 0.0, 1.0);
    lv *= 1.0 - smoothstep(0.45 * foam_reach, foam_reach,
                           distance(world.xz, mark[cell]));
    // Shore and tank, and nothing else. Crest foam in open water was the first
    // cut and it read as scratches on the surface: the ridge fold makes thin
    // creases by design, so anything keyed straight off the height field picks
    // out lines rather than patches. The field is used to break the two real
    // sources up instead.
    // And the trail behind whoever drove through. Combined by max and never by
    // sum: stamps overlap along the path by construction - that is what makes
    // the line continuous - so adding them would put a bright bead at every
    // spacing and read as the trail being made of dots after all.
    float trail = 0.0;
    for (int i = 0; i < {4}; i++) {{
        float age = wake_age[i];
        if (age >= 1.0)
            continue;
        float r = mix(wake_seed, wake_spread, age);
        // Fades as it spreads, and the two together are what a wake does: the
        // same water spread over more of the surface.
        // Held longer than linear, the tracer smoke's argument in the same
        // words: the stamp grows as it ages, so taking its opacity away in step
        // with time removes it exactly as its width arrives, and the wide half
        // of the trail is never seen.
        trail = max(trail, pow(1.0 - age, 0.7)
                          * (1.0 - smoothstep(r * 0.35, r,
                                              distance(world.xz, wake_at[i]))));
    }}
    trail *= wake_on;

    float broken = shred(world.xz, time, wave_scale, wave_speed);
    // The shore and the tank's own patch are broken up hard - they are foam
    // sitting on moving water. The trail is not: a wake is water that has been
    // churned through, so it holds together as a lane and only takes the
    // surface's texture on top.
    // And round every hull that is in the water, along the line the tank
    // actually meets the ground at.
    //
    // <b>The tank's own silhouette, not an ellipse drawn round it.</b> An oval
    // sized off the hull is right about where the tank is and wrong about its
    // shape everywhere - it stands off the nose and cuts through the track guards
    // - and no amount of fitting makes an ellipse into a tank. What it should
    // follow is already measured: AtlasSet.Groundline is the bottom of the
    // silhouette per column per heading, which is where the near surface of the
    // tank meets the ground, and it is the same table the sprite's own waterline
    // is cut by. So the two lines are one measurement and cannot disagree.
    //
    // Done in the harness's 2D px rather than in world units, because that is the
    // space the table is in and the mapping back is exact: this projection puts a
    // world point at (x, z*squash - y*rise) and nothing else. The band is in the
    // sprite's own pixels, so it grows with the tank the size dial made bigger.
    vec2 plane = vec2(world.x, world.z * squash - world.y * rise);
    float collar = 0.0;
    float prow = 0.0;
    for (int i = 0; i < 4; i++) {{
        if (hull_size[i].x <= 0.0)
            continue;
        vec2 px = (plane - hull_at[i]) / hull_size[i].x + hull_pivot[i];
        float line = shoreline(i, px, false);
        if (line >= 0.0) {{
            // Pushed by the same field as the shore and in the same coin - a
            // fraction of its own band - and read at the line rather than at the
            // fragment, for the reason given up there: read at the fragment this
            // one folds first, being the narrowest of the three.
            //
            // The line's own point is put back in the world by inverting the
            // mapping that brought the fragment here, and the height it needs is
            // this fragment's own: the collar is drawn on the water, so world.y
            // is already the plane the waterline lies in. The sprite's half does
            // the same inversion from the other side, which is what makes the
            // two one line.
            vec2 seat = (vec2(px.x, line) - hull_pivot[i]) * hull_size[i].x
                        + hull_at[i];
            vec2 sea = vec2(seat.x,
                            (seat.y + world.y * rise) / max(squash, 0.0001));
            line += crumple(sea * edge_crumple.x, time) * edge_crumple.y
                    * hull_band;
            collar = max(collar,
                         1.0 - smoothstep(0.0, hull_band, abs(px.y - line)));
        }}
        // And the water piled up ahead of it, which is that same line dilated
        // along the way the tank is going. Stepping the sample point *back* down
        // the travel direction is the whole of it: where this fragment is ahead
        // of the hull the stepped point lands on the line and the crest is drawn,
        // where it is behind it lands inside the silhouette - and inside is under
        // the sprite, which is drawn over the water. No side test, and the stern
        // hides its own half exactly as a hull does.
        //
        // A direction in the plane is the same direction in tile px, because the
        // mapping between them is one uniform scale; only the length has to be
        // said in the space it is measured in, and reach is in the sprite's px
        // like the band it is judged against.
        float push = hull_push[i] * bow_on;
        if (push <= 0.0 || length(hull_run[i]) <= 0.0)
            continue;
        // Half the swell each way: offset by it, spread by it. See BowSwell -
        // equal is the one setting that fills the crest from the hull outward
        // with nothing wasted under the sprite and no clean gap beside it.
        float half_swell = 0.5 * bow_swell * length(hull_run[i]);
        vec2 back = px - normalize(hull_run[i]) * half_swell;
        float ahead = shoreline(i, back, true);
        if (ahead < 0.0)
            continue;
        vec2 rest = (vec2(back.x, ahead) - hull_pivot[i]) * hull_size[i].x
                    + hull_at[i];
        vec2 brine = vec2(rest.x,
                          (rest.y + world.y * rise) / max(squash, 0.0001));
        ahead += crumple(brine * edge_crumple.x, time) * edge_crumple.y
                 * half_swell;
        prow = max(prow, push
                         * (1.0 - smoothstep(0.0, half_swell,
                                             abs(back.y - ahead))));
    }}

    float edge = clamp(max(max(lip, bank), max(lv, collar)), 0.0, 1.0) * broken;
    // The trail and the crest take the surface's texture only on top, and for
    // one reason: both are water that is being churned through right now, so
    // they hold together as a body. Broken as hard as the shore is, a crest that
    // is a few pixels wide comes apart into speckle and reads as noise rather
    // than as a bow.
    // And the crest of the ripple field itself, which is where the bow wave comes
    // from now. It goes in the lane rather than the edge for the lane's own reason:
    // this is water being churned through right now, so it holds together as a body
    // and only takes the surface's texture on top. Thresholded high, because foam on
    // every crest is the scratches this shader already refused once - what is over
    // the cut is the water a hull is standing in front of itself.
    float crest = smoothstep(wash_crest, wash_crest + wash_band, wsh);
    float lane = max(max(trail, prow), crest) * mix(broken, 1.0, 0.55);
    col = mix(col, foam_ink, clamp(max(edge, lane), 0.0, 1.0) * foam);

    // And the film, spent on what the water <b>adds</b> rather than on ALPHA.
    // Translucency was the obvious way to feather a tip and it is the one thing
    // this surface must not do: owning every pixel it covers is the whole
    // difference in kind from the strip, and an alpha ramp hands the last of them
    // back to the blender. It is not needed either - the colour already arrives
    // at the ground on its own, because t goes to nothing with the thickness and
    // mix(through, body, 0) is the bottom exactly. What ends abruptly at a cut is
    // the foam and the two highlights, which are added whatever the depth: so it
    // is those that fade, and `bottom` is where they fade to.
    col = mix(bottom, col, film);

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
    /// <b>Cut about the contact patch every frame, not built on a cell.</b> Built
    /// on <c>Selected.Cell</c> it sat on the last cell reached and a tank spends
    /// most of an order between two - so it trailed a whole cell behind the tank
    /// it was marking, which reads as the selection lagging. That is the same
    /// thing <see cref="SelectionRing"/> says in 2D, and it says it about the
    /// contact patch, which is what this is cut about now.
    ///
    /// <b>It lies in the face, and one height for the whole band cannot.</b>
    /// <c>World(row + L, L)</c> lands on the same pixel for every L, so six corners
    /// sharing an L are on screen the flat hexagon the 2D ring draws - and on a
    /// face that tilts a level across itself that is the wrong shape twice over: it
    /// reads as a decal hovering over the slope rather than painted on it, and at
    /// the tank's own height it sank into the uphill half and the face hid it.
    /// Measured, parked on the ramp at (3,4): 922 px of ring in a 218x53 box where
    /// a flat board draws 1482 in 218x96. The band now takes the plane of the face
    /// the tank is on - <see cref="HexField.TopOn"/>, one named cell - so it
    /// comes out congruent to the hexagon of that face, which is what the tilted
    /// cell outline beside it is drawn as. The cell is named rather than asked per
    /// corner because a band a cell wide overhangs its own cell the moment the tank
    /// is between two, and both ways of letting the corners answer for themselves
    /// were built and measured worse: see <see cref="HexField.TopOn"/>.
    ///
    /// <b>What that costs is the uphill arc while climbing, and it is the honest
    /// cost.</b> A cell-sized ring on a ramp has its far side a level above the
    /// tank's feet, which on an isometric board is behind the hull - measured
    /// mid-climb, 750 px of ring against 1341 drawn flat. Behind the hull is where
    /// the far side of this mark has always gone; on a slope there is more of it.
    ///
    /// <b>The face, and in a ford that means the bed rather than the water.</b> A
    /// wading tank stands on the bottom, and what the mark says is which cell it
    /// is standing on - so it lies on the ground there like everywhere else, and
    /// the water is simply drawn under it. That is the whole of the water fix:
    /// under the surface the band was not dimmed but painted out, the pond being
    /// sorted after it - measured, parked in the ford, <b>0 px</b> of it survived
    /// against 1482 on the same cell with <c>--no-water</c> - and
    /// <see cref="RingOrder"/> now puts the mark over the water, where a mark
    /// belongs. Floating it on the surface instead was tried and is a second
    /// statement about where the tank is: the hull is drawn standing on the bed
    /// and the ring would be drawn round its waterline.
    /// </summary>
    private void BuildRing()
    {
        var mesh = (ImmediateMesh)_ring.Mesh;
        mesh.ClearSurfaces();
        if (Field.Atlas is null)
            return;
        List<(Vehicle? Who, Vector2I Cell, Color Ink, float Ring)> marks = Marked();
        if (marks.Count == 0)
            return;
        mesh.SurfaceBegin(Mesh.PrimitiveType.Triangles);
        foreach ((Vehicle? who, Vector2I cell, Color ink, float ring) in marks)
            Band(mesh, who, cell, ink, ring);
        mesh.SurfaceEnd();
    }

    /// <summary>
    /// What is marked on the ground and how wide each band is, outermost first.
    ///
    /// <b>Three marks and one mesh, because they are one statement made about
    /// three things</b> - which cell this is about. That is
    /// <see cref="SelectionRing"/>'s own argument for one class in two colours,
    /// arriving here as one band builder called three times: a second builder is
    /// a second chance for a mark to stop lying on the ground.
    ///
    /// <b>Nested only where they land on top of each other, and that is what
    /// keeps three marks from becoming three sizes.</b> A mark's size is not a
    /// statement - the band is off the cell, never off the tank, for the reason
    /// named at <see cref="Ring"/> - so two marks about two cells are both drawn
    /// at <see cref="Ring"/>, and a picture with one mark in it is the picture it
    /// always was. Stacked on one cell they cannot all be that, and stepping the
    /// later one in is the flat board's own answer: <c>Main</c> draws its red
    /// ring at a tighter inset than its cyan one so that the day a tank is both
    /// yours and somebody's target is two rings rather than one flickering.
    ///
    /// <b>The cell first, and that is the nesting order rather than a taste.</b>
    /// A cell is the widest thing of the three - a tank stands on it, and the
    /// tank being shot at is one particular tank - so where two of them stack,
    /// the one that steps inward is the more specific one.
    ///
    /// Internal and separate from the mesh it feeds because the order and the
    /// nesting are the claim being made here, and a claim read off a picture of
    /// three coloured hexagons is a claim nobody can check.
    /// </summary>
    internal List<(Vehicle? Who, Vector2I Cell, Color Ink, float Ring)> Marked()
    {
        var want = new List<(Vehicle? Who, Vector2I Cell, Color Ink)>();
        if (Chosen is Vector2I cell)
            want.Add((null, cell, SelectionRing.Chosen));
        if (Selected is not null)
            want.Add((Selected, Selected.Cell, SelectionRing.Friendly));
        if (Quarry is not null)
            want.Add((Quarry, Quarry.Cell, SelectionRing.Hostile));
        var marks = new List<(Vehicle?, Vector2I, Color, float)>();
        for (int i = 0; i < want.Count; i++)
        {
            int over = 0;
            for (int k = 0; k < i; k++)
                if (want[k].Cell == want[i].Cell)
                    over++;
            marks.Add((want[i].Who, want[i].Cell, want[i].Ink, Ring - Nest * over));
        }
        return marks;
    }

    /// <summary>
    /// One mark: the band of six quads, in the plane of the face it is about.
    ///
    /// <b>A tank or a cell, and the difference is one line of it.</b> A tank is
    /// marked about its contact patch - the reason is above - and a cell about
    /// its own middle, which is the same point <see cref="Sunk"/> already
    /// measures its corners from. Everything after that is the same arithmetic,
    /// and it has to be: a cell mark drawn by a second routine is a second answer
    /// to "where is the ground here", and the two would disagree exactly on the
    /// slopes and in the water where this one was hard to get right.
    ///
    /// <b>A cell in water is marked on its bed</b>, like a wading tank and for
    /// the same reason - the bed is the ground and the water is drawn over it.
    /// Asked of the cell rather than of a hull, because a cell with no tank on it
    /// has nothing that could be wading.
    /// </summary>
    private void Band(ImmediateMesh mesh, Vehicle? who, Vector2I cell,
                      Color ink, float ring)
    {
        Vector3[] corner = Corners();
        Vector3 lie = Clear(Squash, RiseFactor);
        // Where the mark touches the ground, in flat space - the drawn row with
        // its lift put back on, which is the space World and HexField read.
        //
        // <b>The ground's own height, and Standing is the one number here that
        // must not be used.</b> Standing is a depth promoted to the crown while
        // the tank is on a wall: it costs nothing to a shape that carries its own
        // height, and a whole level to one that looks its height up, because the
        // row it hands the board is a level out and every corner is then answered
        // by the ground a cell away. Ground is the number laid under a tank, bare
        // because the clearance in it is for things laid at a height rather than
        // for things asking for one. A cell carries no clearance at all, so its
        // own flat middle is already that space.
        Vector2 at = who is null
            ? Field.FlatAnchor(cell) + Field.CentreOffset + Origin
            : new Vector2(who.GroundPoint.X,
                          who.GroundPoint.Y + Field.Bare(who.Ground));
        // Which face it is lying in, and it is two of them while a hull is over
        // the seam. Mixed by the same LegBlend the body's own lean is mixed by -
        // see Main.SurfaceSlope - so the mark tilts with the tank rather than
        // snapping a frame before or after it. Named as one cell instead, the
        // band jumped 32px at the moment the near cell changed; the lerp of two
        // planes is a plane, so the shape stays a hexagon all the way across. A
        // cell mark is over no seam, and its two are the one cell.
        Vector2I near = who?.Cell ?? cell, onto = who?.Onto ?? cell;
        float cross = who?.LegBlend ?? 0.0f;
        // How high the cell it is standing on stands, for the rule in Lie.
        float seat = Mathf.Lerp(Field.TopAt(near), Field.TopAt(onto), cross);
        // <b>And how far forward the band has to stand to be in a ford at all.</b>
        // A wading tank's mark lies on the bed, and what stands between the bed
        // and the eye is the pond's own side - opaque, depth-writing, and exactly
        // as tall as the water is deep. Measured, parked in the ford: 476 px of
        // ring, and 1479 with that side taken away. So the band is pushed toward
        // the camera by that same depth, which is a nudge and not a lift:
        // World(row + L, L) draws at the same pixel, so this moves nothing on
        // screen and buys depth alone - the trick Clear is already made of.
        //
        // <b>By the water's depth and not by more</b>, because the next thing in
        // front of a mark is a hex standing over it, and a level is nearly twice
        // this. That occlusion is wanted; this one is the water occluding its own
        // floor.
        //
        // <b>And not on the corners a rising neighbour stands in front of, which
        // is the same occlusion arriving by the other door.</b> Half a level is
        // plenty to clear the low end of a ramp: the pit's mouth comes down to
        // the bed at the edge it shares with the water, so the nudge carried the
        // band out in front of the slope, and the same tank parked one level up
        // on dry ground - no nudge to spend - had that half properly cut. Two
        // pictures of one hex, and the water was the only difference.
        //
        // Spent per corner, so what it costs is the corners facing that
        // neighbour and nothing else. Free in shape: the nudge moves no pixel by
        // construction, so corners carrying different ones still draw one
        // hexagon.
        float wade = who is null
            ? Field.IsWater(cell) ? Field.WaterRiseAt(cell) : 0.0f
            : who.Wading ? Field.WaterRiseAt(near) : 0.0f;
        // The hexagon's own edges and what stands over the bed across each of
        // them. Read once - the six are the same for every corner.
        (Vector2[] side, float[] reach, _) = Rim(corner);
        float[]? overNear = wade > 0.0f ? Overlook(near, corner) : null;
        float[]? overOnto = wade > 0.0f && onto != near
            ? Overlook(onto, corner) : overNear;
        // Dark, ink, dark - the 2D ring's five-pixel backing under its two-pixel
        // line, as three strips of one mesh rather than two coplanar transparent
        // bands with nothing to order them. The lesson is the additive effect
        // layers': a colour chosen on the pale tile disappears on the amber one,
        // so the mark carries its own contrast rather than borrowing the
        // ground's - and the ground here is the route.
        foreach ((float from, float to, Color paint) in new[]
                 {
                     (ring - Fringe, ring - Line, SelectionRing.Under),
                     (ring - Line, ring + Line, ink),
                     (ring + Line, ring + Fringe, SelectionRing.Under),
                 })
        for (int i = 0; i < 6; i++)
        {
            Vector3 a = corner[i], b = corner[(i + 1) % 6];
            foreach (Vector3 v in new[]
                     {
                         a * to, b * to, b * from,
                         a * to, b * from, a * from,
                     })
            {
                mesh.SurfaceSetColor(paint);
                Vector2 on = Flat(at, v);
                float high = Lie(on, near, onto, cross, seat);
                float sunk = Sunk(on, near, onto, wade, side, reach,
                                  overNear, overOnto);
                mesh.SurfaceAddVertex(
                    World(new Vector2(on.X, on.Y + sunk), high + sunk) + lie);
            }
        }
    }

    /// <summary>How far the ground across each of a cell's six edges stands over
    /// that cell's own top - nought where it is level with it or lower, which is
    /// the answer for the board's cut too. What <see cref="Sunk"/> asks to find
    /// out whether a corner has a neighbour in front of it or only water.</summary>
    private float[] Overlook(Vector2I cell, Vector3[] corner) =>
        Overlook(Field, cell, corner, Squash, RiseFactor);

    /// <summary>The same, given its camera terms instead of reading them off the
    /// field - so it can be asserted without a board, for
    /// <see cref="Shoreline"/>'s reason.</summary>
    internal static float[] Overlook(HexField field, Vector2I cell,
                                     Vector3[] corner, float squash, float rise)
    {
        Vector2I[] next = Beside(field, cell, corner, squash, rise);
        var over = new float[6];
        float bed = field.TopAt(cell);
        for (int k = 0; k < 6; k++)
            over[k] = field.InBounds(next[k]) && next[k] != cell
                ? Mathf.Max(0.0f, field.TopAt(next[k]) - bed) : 0.0f;
        return over;
    }

    /// <summary>
    /// How far toward the eye one corner of the ring stands: the water's own
    /// depth where the pond's side is all that hides the bed, and nothing where
    /// the ground next door is what hides it.
    ///
    /// <b>Capped by the distance to the edge, not switched off at it.</b> The
    /// nudge is depth and nothing else, so what it has to stay behind is the
    /// wall standing at that edge: a corner <c>d</c> in from it may come forward
    /// by <c>d</c> and no further, and one far enough in is behind nothing and
    /// takes the lot. Written as a switch instead it took the whole of the ring
    /// on any side with a level over it - measured on (8,5), 507 px down to 199,
    /// because a cell of the pit's floor has dry ground a full level up along
    /// half of it and one level is wider than the ring.
    ///
    /// A neighbour level with the bed hides nothing, caps nothing, and the
    /// board's cut is that case too.
    /// </summary>
    private float Sunk(Vector2 on, Vector2I near, Vector2I onto, float wade,
                      Vector2[] side, float[] reach, float[]? overNear,
                      float[]? overOnto)
    {
        if (wade <= 0.0f || overNear is null)
            return 0.0f;
        Vector2 flat = on - Origin;
        Vector2I under = Field.CellUnder(flat);
        float[]? over = under == near ? overNear
            : under == onto ? overOnto : null;
        // Out of the pond the nudge buys nothing it is entitled to: what stands
        // between such a corner and the eye is ground, not this tank's water.
        if (over is null || !Field.IsWater(under))
            return 0.0f;
        // The corner about its own cell's middle, unsquashed - the space the
        // hexagon's edges were measured in.
        Vector2 mid = Field.FlatAnchor(under) + Field.CentreOffset + Origin;
        var point = new Vector2(on.X - mid.X, (on.Y - mid.Y) / Squash);
        float sunk = wade;
        for (int k = 0; k < 6; k++)
            if (over[k] > 0.0f)
                sunk = Mathf.Min(
                    sunk, (reach[k] - point.Dot(side[k])) * Squash);
        return Mathf.Max(0.0f, sunk);
    }

    /// <summary>
    /// How high one point of the ring lies: the face's own plane, and never under
    /// ground that is not standing over the tank.
    ///
    /// <b>The plane is the shape and the clamp is what the ground is allowed to do
    /// about it.</b> A band a cell wide overhangs its own cell, and the plane it
    /// lies in carries on going: downhill it passes under whatever is out there,
    /// so the ground swallowed it - measured on the descent into the pit, 262 px
    /// of ring left of 1482, and 476 parked in a ford where the pond's own opaque
    /// side stands in front of the bed. That is not occlusion, it is the mark
    /// falling out of the world at the edges.
    ///
    /// <b>Occlusion by higher cells is a different thing and is wanted.</b> A hex
    /// standing above the one the tank is on really is in front of the ring, and
    /// the ring drawn over its wall reads as painted up the side of the hill.
    /// So the clamp asks the level: ground on cells no higher than the tank's own
    /// lifts the band up onto itself and cannot bury it, and ground on cells
    /// standing over it does not - it keeps its depth and takes the band, which is
    /// the picture that was asked for.
    ///
    /// Not a fraction of a level, not a tolerance: whether the cell under this
    /// corner stands over the cell under the tank is a question with an answer,
    /// and the answer is <see cref="HexField.TopAt"/> either side. Blended across
    /// the seam by the same LegBlend as the plane, so the test does not flip a
    /// frame before the shape does.
    ///
    /// <b>Except the two cells the tank is standing across, which are never in
    /// front of it: it is on them.</b> A step onto a ramp is a step onto ground
    /// that stands over the ground left behind - by the whole of a level by the
    /// end of it - so the plain test called the cell being climbed onto a wall and
    /// let it take the half of the ring lying on it. Measured on the climb up
    /// column 3, far half gone: 1084 px of ring in a 218x69 box against the 1482
    /// in 218x96 that a whole one draws, and 1409 in 216x106 coming off the ramp
    /// onto the plateau, where it is the near half that goes instead.
    ///
    /// The clamp is what makes that half right rather than merely present: lifted
    /// onto the ground it overhangs, the band bends over the ramp's edge, which is
    /// the shape the surface has there. And exempting these two cannot cost the
    /// occlusion this rule is for - a hex to the side is neither of them.
    /// </summary>
    private float Lie(Vector2 on, Vector2I near, Vector2I onto, float cross,
                     float seat)
    {
        Vector2 flat = on - Origin;
        float plane = Mathf.Lerp(Field.TopOn(near, flat),
                                 Field.TopOn(onto, flat), cross);
        Vector2I under = Field.CellUnder(flat);
        return Covers(under, near, onto, Field.TopAt(under), seat)
            ? plane
            : Mathf.Max(plane, Field.TopAtPoint(flat));
    }

    /// <summary>Whether the cell under one point of the ring is allowed to stand
    /// in front of it: it is, when it stands over the tank and is not one of the
    /// two the tank is standing across. Named and static so the rule can be
    /// asserted about a board rather than read off a picture - the half a ramp
    /// took is a half that is simply absent, which no number about the ring
    /// reports.</summary>
    public static bool Covers(Vector2I under, Vector2I near, Vector2I onto,
                             float top, float seat) =>
        under != near && under != onto && top > seat + 0.01f;

    /// <summary>A point of the ground plane offset from another, in flat space.
    /// The offset arrives as a world XZ vector and flat space is that squashed, so
    /// the two terms of the row are told apart here rather than at every call -
    /// the same distinction <see cref="World"/> exists to keep.</summary>
    private Vector2 Flat(Vector2 at, Vector3 offset) =>
        new(at.X + offset.X, at.Y + offset.Z * Squash);

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
    /// <summary>
    /// The ring's own, and it tests depth like everything else on the ground.
    ///
    /// <b>Standing it off the depth buffer was tried and it takes away the half of
    /// the picture that was right.</b> It cures the mark falling under the ground
    /// at the edges - but a hex standing over the one the tank is on really is in
    /// front of the ring, and the band drawn over its wall reads as painted up the
    /// side of the hill. The falling-under is cured where it is caused instead, in
    /// <see cref="Lie"/>, and this stays a decal.
    /// </summary>
    private static StandardMaterial3D RingDecal() => Decal(RingOrder);

    /// <summary>Whether the ring's material takes the terrain's depth test, so the
    /// rule above is asserted rather than read off a picture: cells standing over
    /// the tank's own are what may cover the mark.</summary>
    public static bool RingTakesDepth => !RingDecal().NoDepthTest;

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
    /// <b>The water goes over the ruts and under the ring, and the ring is the
    /// rung that moved.</b> A rut is terrain, so water lying on it is water lying
    /// on it. The ring is not terrain at all - it is a marker saying which tank
    /// the keys drive - and under the water it was not dimmed but <b>gone</b>: the
    /// surface is drawn after it and paints over it whole. Measured, parked in the
    /// ford, <b>0 px</b> of ring against 1482 on the same cell with
    /// <c>--no-water</c>, so a tank driven into the water stopped being the tank
    /// that was selected. Painted on the surface instead, it says what it has
    /// always said, and there is nothing it can be wrong about: it only ever meets
    /// water at a shore or in a ford, and it is a mark in both.
    ///
    /// <b>The water is still under every tank</b>, which is the half of the
    /// statement that has not changed: a tank standing in it is drawn over the
    /// surface and tints its own submerged half - see <see cref="PaintShader"/>.
    /// Drawn over the tanks instead it would be right for the near half of its own
    /// cell and wrong for every tank in front of that cell, because one sort key is
    /// one answer for a whole surface.</summary>
    public const int RingOrder = -1;
    public const int WaterOrder = -2;

    /// <summary>Where a cast shadow sorts: over the ruts and under the ring.
    ///
    /// <b>Over the ruts because a shadow is light not arriving, and it does not
    /// arrive at the churned soil either.</b> A rut drawn over its own shadow is
    /// a belt mark that glows out of the dark, which is the one thing a mark
    /// pressed into dirt cannot do. Under the ring for the ring's own reason -
    /// it is a marker laid over the terrain, and terrain is what a shadow is
    /// painted on.</summary>
    public const int ShadowOrder = -3;
    public const int RutOrder = -4;

    /// <summary>
    /// The two rungs above the ground, and they are a rung apart rather than
    /// both at the default because the difference is the whole statement.
    ///
    /// <see cref="StandOrder"/> is what stands on the board - the tanks and the
    /// trees - and inside it the camera sorts by each thing's own foot, which is
    /// the rule the 2D layer states as a z-index. <see cref="DressOrder"/> is the
    /// tiers that gave up that sort because they stand where a tank parks; see
    /// <see cref="PropTier.UnderTanks"/>.
    ///
    /// <b>Which rung a prop is on is a state, not a tier constant.</b> It gave up
    /// the sort against a <i>hull</i>, so it gives it up while a hull is on its
    /// ground and not otherwise - <see cref="Grove.Reveal"/> carries the whole
    /// argument and what the change costs. Priority beats depth, so a constant
    /// here is a bush under every trunk on the board for good.
    ///
    /// <b>Added above rather than pushing the ground down.</b> Shifting the four
    /// rungs below would restate every one of their arguments in new numbers for
    /// no gain; a tank and a tree were never at a priority anybody chose, they
    /// were at the default, so naming that rung is a gain on its own.
    /// </summary>
    public const int DressOrder = 0;

    public const int StandOrder = 1;

    /// <summary>The rung the glaze stands on: over the tanks, which is the whole
    /// of what it is for - see <see cref="Glaze"/>.</summary>
    public const int GlazeOrder = StandOrder + 1;

    /// <summary>Which of the two rungs a prop is on this frame. One place,
    /// because the rung is set twice - once as the billboard is built and again
    /// every frame as the tanks move - and two copies of it is a board where a
    /// prop is banded in one of them and not the other.</summary>
    public static int RungFor(PropNode tree) =>
        tree.Dressed ? DressOrder : StandOrder;

    /// <summary>
    /// The rung a prop is drawn on, band and glaze together.
    ///
    /// <b>A prop standing on a glazed cell climbs over the glaze</b> rather than
    /// being washed by it. The glaze is the colour of ground, so laid over a bush
    /// it is half a bush and half dirt - and that was the only thing by which a
    /// glazed cell could be told from an untouched one at all, half the pixels
    /// the whole effect moves. Over it, the bush stays a bush and covers the hull
    /// honestly: it is standing in front of the tank, which is the same sentence
    /// the glaze itself is built on.
    ///
    /// <b>The band still wins.</b> A prop in the dressing band is under the tank
    /// because a hull is on its ground, and a cell a hull is standing on is not a
    /// cell in front of it.
    /// </summary>
    private int RungOn(PropNode tree) =>
        tree.Dressed ? DressOrder
        : _glazing.ContainsKey(tree.Cell) ? GlazeOrder + 1
        : StandOrder;

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
        // Ground and not a fourth spelling of it: a rut is a mark on the ground
        // drawn on the tank's own row, which is the same sentence the water marks
        // and the tank's own quad are, and the arithmetic is bit for bit the same.
        return Ground(at + shift, lift, squash, rise) + Clear(squash, rise);
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
    /// <summary>One quad per kind, per mirror, and <b>that is all</b> - see
    /// <see cref="Shape"/>. It holds the living picture and the burnt one at
    /// once, so nothing about it changes when a tree burns: the handover is the
    /// material's, per fragment.</summary>
    private readonly Dictionary<(int Species, bool Mirrored), ArrayMesh>
        _stems = new();

    /// <summary>The flame and the smoke over one tree, made when it first
    /// catches and kept afterwards. Lazily, because a board of 672 trees would
    /// otherwise carry 1344 hidden quads for a fire nobody has lit.</summary>
    private readonly Dictionary<PropNode, MeshInstance3D> _burning = new();
    private readonly Dictionary<PropNode, MeshInstance3D> _smoking = new();
    private readonly Dictionary<(int Species, bool Mirrored), ArrayMesh> _pyres = new();
    private readonly Dictionary<(int Species, bool Mirrored), ArrayMesh> _plumes = new();
    private readonly Dictionary<string, Borrowed?> _loans = new();
    private double? _upright;
    private float _fireClock;
    private int _sown = -1;

    /// <summary>The same trees again, laid down the sun onto the ground.
    ///
    /// <b>The mesh is the standing tree's own</b>, not a second one: the whole of
    /// the flattening is in the transform, so a wood of 203 trees adds 203 nodes
    /// and not one vertex. The material is per tree for the standing tree's
    /// reason - the fade is per tree and this renderer ignores the per-instance
    /// answer.</summary>
    private readonly Dictionary<PropNode, MeshInstance3D> _shading = new();

    /// <summary>And a patch of darkened ground under each one's foot. One shared
    /// quad and one shared blot for the whole board - the size is in the
    /// transform, so a wood of 203 props adds 203 nodes and one 64px texture.
    /// The material is per prop for the fade's reason, the same as the other
    /// two.</summary>
    private readonly Dictionary<PropNode, MeshInstance3D> _seating = new();
    private ArrayMesh? _sole;

    /// <summary>Whether anything on this board casts a shadow along
    /// <see cref="Sun"/>. On by default, on the contact shadow's argument: the
    /// A/B is the whole case for the layer, and a switch that has to be found
    /// before the thing can be seen is a switch nobody reaches for.</summary>
    public bool CastShadows = true;

    /// <summary>
    /// How dark a cast shadow is where the thing casting it is opaque.
    ///
    /// <b>Multiplied into the art's own alpha, which is what makes a crown read
    /// as a crown.</b> The mask is the tree's texture, so gaps between the leaves
    /// come through as gaps in the shadow for nothing - the dapple is the art's,
    /// not a pattern invented here.
    ///
    /// Black rather than a tinted dark: this lies on grass, on soil and on the
    /// plain <see cref="SoilInk"/> of a run with no terrain art at all, and a
    /// shadow that carried a hue would be right against one of those three.
    ///
    /// <b>Two shadows do compound here, and that is the thing
    /// <c>ground_shadow</c> refused for the tank - so why it is allowed is worth
    /// saying.</b> Ordinary blending stacks: two of these over one another give
    /// 0.70 and three give 0.83, and the wood is dense enough to do it. Measured
    /// on <c>--terrain mixed</c> against the same frame with the switch off:
    /// 70199px of the picture move, 12162 of them by 60 levels or more - which
    /// is one full-strength shadow on this ground - and only 2513 past that,
    /// 362 past 100. So 3.6% of the shadowed ground is stacked and the deepest
    /// is about two and a half layers.
    ///
    /// What made it unacceptable on the tank was not the darkness but that the
    /// dark patch <i>moved</i>: hull and turret shadows overlapped differently
    /// at every turret bearing, so a blot slid about the tank as the gun came
    /// round. Two trees do not move relative to each other, so what this makes
    /// is a fixed deeper shade under a clump, which is what a clump has. Named
    /// rather than fixed, because the fix is a shadow mask taken as a maximum,
    /// and that is the terrain pass's machinery - worth building once, for both.
    /// </summary>
    public static readonly Color ShadowInk = new(0.0f, 0.0f, 0.0f, 0.45f);

    /// <summary>
    /// Whether a prop also darkens the ground it is standing on, right at its
    /// foot.
    ///
    /// <b>Its own switch and not <see cref="CastShadows"/>'s, because the two
    /// say different things.</b> The cast declares where the sun is; this
    /// declares that the thing is touching the ground - the split
    /// <c>ground_shadow</c> already draws for the tank, arriving late for the
    /// wood. Separate also because the A/B of either is a pair of frames in
    /// which the other must not move.
    ///
    /// On by default, on the cast's own argument: a layer whose whole case is
    /// the A/B is a layer that has to be there to be turned off.
    /// </summary>
    public bool ContactShadows = true;

    /// <summary>
    /// How dark the patch under a foot is: <see cref="ShadowInk"/>, and the
    /// same object rather than a number equal to it.
    ///
    /// <b>A second constant was wrong, and wrong in the one direction that
    /// reads as a fault.</b> It was set lighter than the cast on the argument
    /// that contact is ambient occlusion and a softer thing - which put a patch
    /// of 0.35 grey against a streak of 0.45 leaving the same foot, so the
    /// ground got <i>lighter</i> as it approached the thing casting it. A
    /// shadow that fades toward its own object is backwards however soft it is.
    ///
    /// One ink is not the same as two numbers kept equal, and this is a project
    /// with a graveyard of the second: a hill's shadow, a tree's shadow and the
    /// ground a prop stands on are one statement about one sun, and the density
    /// belongs to the sun.
    ///
    /// <b>What the patch still adds is where the two overlap</b>, which is most
    /// of the down-sun half of it: ordinary blending compounds them to
    /// <c>1-(1-0.45)^2 = 0.70</c>. That is the right shape and it is not chosen
    /// - the contact is the darkest part of any shadow, and here it comes out
    /// darker without a third number for it. Which of the two draws first still
    /// does not matter: <c>a + b - ab</c> is symmetric, and both are black.
    /// </summary>
    public static Color SeatInk => ShadowInk;

    /// <summary>
    /// How far past the measured ground contact the patch reaches, as a share of
    /// the prop's drawn height.
    ///
    /// <b>Two terms, because the patch answers two things at once.</b> Where it
    /// sits and how much actually touches is <see cref="PropSet.RootOf"/> - the
    /// half-width of the bottom contact band, the one thing about a prop's
    /// footprint that was measured rather than declared, and the same pair the
    /// sower keeps props off each other by. How far the darkening reaches out
    /// from there is not that: it is set by how much of the thing is standing
    /// over the ground, which is its height.
    ///
    /// The second term is what makes one number serve both ends of the tier
    /// list. A boulder's drawn height is mostly its own footprint lying on the
    /// same ground - <see cref="PropTier.Stands"/>'s whole point - so a share of
    /// it comes out at roughly the footprint, which is what a boulder shades. A
    /// tree's is height, and a share of it comes out at roughly how far the crown
    /// reaches, which is what a tree shades.
    ///
    /// Measured at 0.14: <c>Tree_1</c> gets 13.2 of contact plus 19.5 of reach,
    /// so 33px of radius - an ellipse 65 by 33 on screen, of which the camera
    /// sees the near half, and that near half is what closes the 26px the canopy
    /// shadow lands away from the trunk. A bush gets 12, a stone gets 4, and a
    /// stone needs no more: its cast already touches it.
    /// </summary>
    public const float SeatReach = 0.14f;

    /// <summary>Where the sprite's own alpha stops mattering to the patch. The
    /// core is flat and the rim is a smooth run out to nothing - a hard-edged
    /// disc under a tree reads as a plate, and a Gaussian with no core reads as
    /// dirt.</summary>
    private const float SeatCore = 0.34f;

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
            // Nothing about the pictures on the ground changes any more either -
            // the shadow and the patch carry both and mix them per fragment, the
            // same as the crown. All three are told how far along it is below.
            Kindle(tree);
            // How much of it is on screen: the fade the wood set this frame
            // times whatever the fire has left of it. Read off the node rather
            // than pushed by whoever set it, for Selected's reason - the reveal,
            // the slider and the reset all write the first, and Smoulder writes
            // the second. See PropNode.Shown for why all three readers here take
            // the product and not one of the factors.
            if (stem.MaterialOverride is ShaderMaterial bark)
            {
                bark.SetShaderParameter("fade", tree.Shown);
                bark.SetShaderParameter("scorch", tree.Scorch);
                // How far between its two pictures it is. The one thing about the
                // pair that moves, which is why the maps beside it are set once.
                bark.SetShaderParameter("swap", tree.Swap);
                // Which rung, off the same flag the 2D z-index reads. A prop
                // only sits in the dressing band while a hull is on its ground -
                // see Grove.Reveal, and DressOrder for what the band is.
                bark.RenderPriority = RungOn(tree);
            }
        }
        foreach ((PropNode tree, MeshInstance3D seat) in _seating)
        {
            seat.Visible = ContactShadows;
            if (!ContactShadows)
                continue;
            // The patch neither leans nor knows where the sun is, so its
            // transform only moves when the board does - written every frame
            // anyway, for the standing quad's reason: a prop moves when the
            // grade slider does, and a patch left behind reads as a tree that
            // has stepped off its own shadow.
            seat.Transform = Seated(tree);
            // The fade, and the lean the inverse map needs - the second is why
            // this cannot be set once at sowing: a gust moves the cast, so the
            // ground the patch is allowed to paint moves with it.
            if (seat.MaterialOverride is ShaderMaterial sole)
            {
                sole.SetShaderParameter("ink", SeatInk.A * tree.Shown);
                sole.SetShaderParameter("clip", CastShadows ? 1.0f : 0.0f);
                // The same number the cast and the crown get, and it has to be:
                // what this keeps off is what the cast is laying down this frame.
                sole.SetShaderParameter("swap", tree.Swap);
                // Times what height is left, exactly as the cast is scaled -
                // see Stage3D.Cast and PropNode.Squat. The patch keeps clear
                // what the cast is laying down, and a trunk on the ground lays
                // down almost nothing.
                sole.SetShaderParameter("run", new Godot.Vector2(
                    (tree.Lean * RiseFactor
                     + SunCast.X * (float)tree.Tier.Stands) * tree.Squat,
                    SunCast.Y * (float)tree.Tier.Stands * tree.Squat));
            }
        }
        foreach ((PropNode tree, MeshInstance3D cast) in _shading)
        {
            cast.Visible = CastShadows;
            if (!CastShadows)
                continue;
            cast.Transform = Shaded(tree);
            // The same fade, and it has to be the same one: a tree that has gone
            // half transparent for the tank on its cell keeping a full-strength
            // shadow reads as the shadow of a tree that is not there. Which is
            // also why it is Shown and not the reveal alone - a bush burnt away
            // leaving its shadow on the ground is that sentence exactly. And the
            // same handover, for the plainer reason: it is the shadow of that
            // crown.
            if (cast.MaterialOverride is ShaderMaterial ink)
            {
                ink.SetShaderParameter("ink", ShadowInk.A * tree.Shown);
                ink.SetShaderParameter("swap", tree.Swap);
            }
        }
    }

    private void Sow()
    {
        foreach (PropNode tree in Wood!.Standing)
        {
            var stem = new MeshInstance3D
            {
                Mesh = Shape(tree),
                // Sorted by where the node is, which is the trunk's foot: the
                // same rule the tanks are sorted by and the same one the 2D
                // ZIndex stated. By its box it would be the middle of the crown,
                // and a crown leans.
                SortingUseAabbCenter = false,
                // Whichever rung it is on as it is built; the band moves with
                // the tanks, so the loop above re-states it every frame.
                MaterialOverride = Bark(tree, RungOn(tree)),
            };
            AddChild(stem);
            _standing[tree] = stem;

            // The patch first of the three, on the same argument as the cast
            // below it: between two props whose feet sort equal, what is painted
            // on the ground is what goes under.
            _sole ??= Sole();
            ShaderMaterial blot = Blot();
            blot.SetShaderParameter("art", tree.Art);
            blot.SetShaderParameter("foot", tree.Foot);
            blot.SetShaderParameter("span", tree.Size);
            // The pair of rectangles is a fact about the kind, so it goes in once
            // and only the handover moves - the crown's own arrangement.
            if (tree.BurntArt is not null)
                blot.SetShaderParameter("burnt", tree.BurntArt);
            blot.SetShaderParameter("burnt_foot", tree.BurntFoot);
            blot.SetShaderParameter("burnt_span",
                tree.BurntArt is null ? tree.Size : tree.BurntSize);
            blot.SetShaderParameter("swap", 0.0f);
            blot.SetShaderParameter("firm", Firm);
            blot.SetShaderParameter("pixels", tree.Pixels);
            blot.SetShaderParameter("rise", RiseFactor);
            blot.SetShaderParameter("radius",
                Seating(tree.Root, tree.Rise, tree.Spread));
            blot.SetShaderParameter("core", SeatCore);
            var seat = new MeshInstance3D
            {
                Mesh = _sole,
                SortingUseAabbCenter = false,
                MaterialOverride = blot,
                Visible = ContactShadows,
            };
            AddChild(seat);
            _seating[tree] = seat;

            // The shadow goes in first, so that between two trees whose feet
            // sort equal the ground mark is the one underneath. The priority
            // already says so; the order says it again for free.
            //
            // On the crown's own quad, and that is what the cross-fade bought: it
            // held one picture's rectangle while it showed one picture, and now it
            // shows both. The union is the same mesh the trunk stands on, so a kind
            // with nothing to swap to gets the quad it always had, to the bit.
            var cast = new MeshInstance3D
            {
                Mesh = Shape(tree),
                SortingUseAabbCenter = false,
                MaterialOverride = Shade(tree),
                Visible = CastShadows,
            };
            AddChild(cast);
            _shading[tree] = cast;
        }
    }


    /// <summary>
    /// How black a fully burnt cell's ground goes, and how coarse the map that
    /// says where it is.
    ///
    /// <b>Coarse on purpose, and it is the hexagon it is blurring away.</b> A mask
    /// at cell resolution with a soft edge spills over the seams, which is what
    /// ash does and what a per-cell tint cannot do: the pond's foam had exactly
    /// this failure, a mask made from a per-cell number, and it drew the grid.
    /// Eight world units is about three screen px.
    /// </summary>
    private const float AshInk = 1.0f;
    private const float AshGrain = 8.0f;

    private byte[]? _soot;
    private ImageTexture? _sootMap;
    private int _sootWide, _sootTall;
    private Vector4 _sootAt;


    /// <summary>
    /// One dark mark on the ground, into the ash map.
    ///
    /// <b>A disc rather than the cell's own hexagon, and it is the same choice
    /// the contact shadow makes</b>: a round mark on the ground is one that does
    /// not announce which cell it belongs to. Two burnt neighbours overlap and
    /// read as one patch, which is what a fire leaves behind; two craters do the
    /// same, which is what a shelled cell looks like.
    ///
    /// Written once because it is now used twice - by the fire, a cell at a time,
    /// and by a burst, a landing point at a time. The two differ in where the
    /// mark is and how wide, and in nothing else, which is exactly the shape that
    /// wants one function rather than two loops that agree until one is edited.
    ///
    /// Keeps the darkest of whatever is already there, so a crater inside a burnt
    /// patch does not lighten it.
    ///
    /// <b>How soft the rim is belongs to the caller.</b> A fire's ash spills over
    /// the seams and wants most of its radius spent fading - that is the whole of
    /// what stops it drawing the hexagon it came from. A crater is the opposite:
    /// it is a small mark that has to read as a mark rather than as a shadow, so
    /// nearly all of it is at full strength and only the last quarter falls off.
    /// One number, two answers, and read at the fire's value the crater came out
    /// a soft grey smudge indistinguishable from the tank's own contact shadow.
    /// </summary>
    private void Splat(Vector3 at, float wide, float much, float perX, float perZ,
                       float rim = FireRim)
    {
        if (_soot is null || much <= 0.0f || wide <= 0.0f)
            return;
        float x0f = (at.X - _sootAt.X) * perX, z0f = (at.Z - _sootAt.Y) * perZ;
        float reachX = wide * perX, reachZ = wide * perZ;
        int x0 = Mathf.Max(0, Mathf.FloorToInt(x0f - reachX));
        int x1 = Mathf.Min(_sootWide - 1, Mathf.CeilToInt(x0f + reachX));
        int z0 = Mathf.Max(0, Mathf.FloorToInt(z0f - reachZ));
        int z1 = Mathf.Min(_sootTall - 1, Mathf.CeilToInt(z0f + reachZ));
        for (int z = z0; z <= z1; z++)
        for (int x = x0; x <= x1; x++)
        {
            float dx = (x - x0f) / Mathf.Max(reachX, 0.0001f);
            float dz = (z - z0f) / Mathf.Max(reachZ, 0.0001f);
            float d = Mathf.Sqrt(dx * dx + dz * dz);
            if (d >= 1.0f)
                continue;
            float fall = Mathf.Clamp((1.0f - d) / Mathf.Max(rim, 0.001f), 0.0f, 1.0f);
            byte want = (byte)Mathf.RoundToInt(255.0f * much * fall);
            int i = z * _sootWide + x;
            if (want > _soot[i])
                _soot[i] = want;
        }
    }

    /// <summary>
    /// How many bursts the board can have going at once.
    ///
    /// Six, and they are reused oldest-first: a burst lasts 1.15s, so six of them
    /// is more than three tanks can start inside one burst's life at any
    /// reload this game has. Past that the oldest is cut short rather than a
    /// seventh node being built, which is the failure to prefer - a board under
    /// heavy fire drops the tail of the burst nobody is looking at any more,
    /// instead of allocating a quad and a material mid-frame.
    /// </summary>
    public const int Bursts = 6;

    /// <summary>
    /// How many clouds of the second kind the board keeps - the dust behind the
    /// belts, <see cref="Drift"/>.
    ///
    /// Counted off the cadence rather than picked, and re-counted every time
    /// the cadence has moved: a puff lives <see cref="TrackDust.Hang"/> - six
    /// tenths of a second - and a light at top speed lays one every nineteenth
    /// of one (<see cref="TrackDust.Step"/>), so a tank driving flat out holds
    /// eleven or twelve. Twenty-six is that tank and another beside it, with a
    /// slot or two in hand.
    ///
    /// <b>And running out shows differently here than it does for a burst.</b>
    /// A board under heavy fire drops the tail of a burst nobody is watching;
    /// this ring wraps on to a cloud that is still being looked at, in the
    /// middle of a trail, and a cloud cut short is exactly the pop the overlap
    /// exists to get rid of. So the margin is in the pool rather than in the
    /// life, which would cost every tank a shorter trail to pay for the rare
    /// frame where three of them drive at once.
    /// </summary>
    public const int Drifts = 96;

    /// <summary>How much of a mark's radius is spent fading out, for the fire's
    /// ash and for a crater - see <see cref="Splat"/>.</summary>
    private const float FireRim = 0.45f;

    /// <summary>How much of a scorch's radius is its own soft edge. Harder than
    /// the fire's: ash blown off a burning wood thins out over its whole width,
    /// and a hull burns where it stands - what it leaves has an edge.</summary>
    private const float ScaldRim = 0.30f;
    private const float PitRim = 0.22f;

    private readonly List<SheetBlast> _booming = new();
    private int _nextBoom;

    /// <summary>
    /// What to do to a burst before it goes off, or null to leave it as built.
    ///
    /// <b>A hook rather than an exposed pool</b>, by <see cref="TankTick.Launch"/>'s
    /// argument: the pool is built on the first shot, so a bench holding a panel of
    /// settings has nothing to write them into until then - and it must not have to
    /// wait for a shot to be able to set one. Answered, every burst is dressed on
    /// its way out, whichever of the six it turns out to be.
    /// </summary>
    public Action<SheetBlast>? Attire;

    /// <summary>
    /// The imported burst on the board: the reference pack's flipbook cloud, from
    /// its own pool.
    ///
    /// <b>A second pool rather than a switch inside <see cref="Burst"/>, and the
    /// two are meant to stand side by side.</b> They are made of different things -
    /// one computes its shape, the other plays sixty-four rendered frames of one -
    /// so the only honest comparison is the two on the same board, on the same
    /// cell, at the same frame. A flag that replaced one with the other would make
    /// that comparison two runs and a memory of the first.
    ///
    /// <paramref name="spot"/> is board space, like <see cref="Burst"/>'s, and it
    /// digs the same mark: a hole in the ground is a fact about the event, not
    /// about which effect drew it.
    /// </summary>
    /// <param name="dig">Whether it leaves a crater. False for a burst seated
    /// on something rather than on the ground - a mortar bomb on a concrete
    /// roof, see <c>WallField.Roofed</c> - because the pit is a mark on the
    /// ground at the seat's lift, and a seat lifted onto a roof leaves it
    /// hanging where the roof was once the roof has gone.</param>
    public void Boom(Vector2 spot, float lift, float might = 1.0f, bool dig = true)
    {
        if (Field.Atlas is null)
            return;
        while (_booming.Count < Bursts)
        {
            var made = new SheetBlast();
            AddChild(made);
            made.Build(Field.Atlas.HexRect.Size.X, Squash, RiseFactor);
            _booming.Add(made);
        }
        SheetBlast blast = _booming[_nextBoom % Bursts];
        // Reset before the hook - see Burst on why the multiply alone compounds.
        blast.Might = 1.0f;
        Attire?.Invoke(blast);
        _nextBoom = (_nextBoom + 1) % Bursts;
        blast.Might *= might;
        blast.Sit(spot, lift, Squash, RiseFactor);
        blast.Fire();
        if (dig)
            Pits?.Dig(spot, lift, Pits.Ink, Pits.Wide * might);
    }

    /// <summary>The bursts as they stand, for a bench that shows what one is
    /// doing. Read-only: they are set off through <see cref="Boom"/> and dressed
    /// through <see cref="Attire"/>, never handed out to be driven.</summary>
    public IReadOnlyList<SheetBlast> Booming => _booming;

    /// <summary>
    /// A round that went into the board: earth or water, whichever is under it.
    ///
    /// <b>One method both roots call, and that is the whole of why it exists.</b>
    /// The harness and the tank bench each answered <c>TankTick.Landed</c> with
    /// their own burst call - two copies of one sentence, which is exactly the
    /// arrangement <see cref="Boom"/>'s own note warns about for the crater. A
    /// choice made in two places is a choice that agrees until one of them is
    /// edited.
    ///
    /// <b>There used to be a second earth burst here and a setting to pick it.</b>
    /// Two implementations of one picture - one computing its shape per fragment,
    /// one playing sixty-four rendered frames of a simulated cloud - and the
    /// choice shipped as <c>BlastSource</c> with <c>--burst built</c> to ask for
    /// the computed one. The comparison is over: the flipbook won it on earth and
    /// then on water, so what is left is one burst and no setting. See
    /// <c>docs/blast.md</c> for what came off with it.
    /// </summary>
    public void Land(Vector2 spot, float lift, float might = 1.0f)
    {
        // <b>Wet first.</b> The burst below is earth - a cloud the colour of
        // thrown ground, with a crater under it - so on a wet cell it is simply
        // the wrong answer. See Wet, and Spout for the A/B.
        if (Spout && Wet(spot, out float top))
        {
            Splash(spot, top, might);
            return;
        }
        // <b>And a wood next</b>, for the same reason and one step further out: a
        // round that goes off among the crowns never reaches the ground, so the
        // earth burst is wrong twice over - the colour, and the hole it leaves.
        // See Splinter.
        if (Leafy(spot))
        {
            Splinter(spot, lift, might);
            return;
        }
        Boom(spot, lift, might);
    }

    /// <summary>Whether the point stands on a cell of wood, live or burnt. The
    /// cover and not the props: a lone bush on open ground is decoration, and a
    /// board where it turned a shell's earth cone into a shower of splinters would
    /// be one where the picture and the rules disagreed about what a wood is.
    /// Burnt counts, because a stand of charcoal still shatters.</summary>
    private bool Leafy(Vector2 spot) =>
        Field.CoverAt(Field.CellAt(spot - Origin)) == Cover.Forest;

    private readonly List<ProcKick> _kicking = new();
    private int _nextKick;
    private readonly List<ProcKick> _drifting = new();
    private int _nextDrift;

    /// <summary>What to do to a kick before it goes off - <see cref="Dress"/> for
    /// the burst, and the same argument: the pool is built on the first shot, so a
    /// bench holding a panel of settings has nothing to write them into until
    /// then, and it must not have to wait for a shot to be able to set one.
    /// </summary>
    public Action<ProcKick>? Fit;

    /// <summary>
    /// The dust a gun blows off the ground, on the board - <see cref="ProcKick"/>.
    ///
    /// <b>A third pool rather than a case inside <see cref="Burst"/>, because it is
    /// a different event on a different trigger.</b> A burst is a round arriving; a
    /// kick is a round leaving, it happens to every shot whether or not anything is
    /// hit, and it digs nothing - there is no crater under a muzzle. Folded in, the
    /// two would have shared a clock and the shot's dust would have outlived or
    /// pre-empted the impact's by whichever of them fired last.
    ///
    /// <b><paramref name="spot"/> is board space</b>, with <paramref name="lift"/>
    /// the ground's height there - <see cref="Burst"/>'s pair, and worth repeating
    /// because getting it wrong is silent and has been: written to take the field's
    /// own space, a quad gets <see cref="Origin"/> added to a point that already
    /// carries it and stands hundreds of pixels off the thing it belongs to.
    ///
    /// <paramref name="along"/> is the gun's ground direction as
    /// <see cref="AtlasSet.GroundDirection"/> gives it - unnormalised, so its
    /// length is the share of a ground length that survived the projection. See
    /// <see cref="ProcKick.Aim"/>: both halves of it are used.
    ///
    /// <paramref name="snout"/> is the muzzle's offset from
    /// <paramref name="spot"/> in screen px. Handed over rather than worked out
    /// here for <see cref="Vehicle.Bore"/>'s reason: where a gun's mouth is is one
    /// measurement, and this would be a fourth projection of it.
    ///
    /// <paramref name="cloud"/> says what it is made of - see
    /// <see cref="ProcKick.Dress"/>. <b>One model, two events: propellant over
    /// the ground a gun stands on, and the ground itself thrown up by something
    /// landing on it.</b> They differ in colour, in whether any of it is burning
    /// and in how much haze is left hanging, and in nothing else - which is why
    /// this is one parameter and not a second effect.
    /// </summary>
    public void Kick(Vector2 spot, float lift, Vector2 along, Vector2 snout,
                     float might = 1.0f, int order = StandOrder,
                     ProcKick.Cloud cloud = ProcKick.Cloud.Muzzle) =>
        Raise(_kicking, ref _nextKick, Bursts, dressed: true,
              spot, lift, along, snout, might, order, cloud);

    /// <summary>
    /// The dust a belt grinds out from under itself while a tank is driving -
    /// <c>TankTick.Dusted</c>, and the cadence behind it is
    /// <see cref="TrackDust"/>.
    ///
    /// <b>A pool of its own rather than a seventh argument to
    /// <see cref="Kick"/>, and the starvation is only the first half of why.</b>
    /// Six clouds are shared by every gun on the board; a tank driving across it
    /// lays one every third of a second and would hold four of the six on its
    /// own, so the next shot fired would cut short a puff and the puff after
    /// that would cut short the shot. That much is arithmetic. The other half is
    /// <see cref="ProcKick.Might"/>'s own finding said about the wardrobe: the
    /// ring hands one cloud round, a throw that sets only what it wants inherits
    /// the rest from whoever had the slot last, and these two throws do not want
    /// the same numbers. A pool that only ever wears one kind cannot inherit the
    /// other's.
    ///
    /// <b>And the panel's hook is not offered it.</b> <see cref="Fit"/> is the
    /// bench's dial on the <em>shot's</em> dust - that is what the rows beside it
    /// read back - so a slider dragged there must not silently resize every puff
    /// behind every tank on the board.
    ///
    /// <b>On <see cref="DressOrder"/>, both rows, and the exception was tried
    /// and taken out.</b> <c>TankTick.Bumped</c>'s finding is that under the
    /// middle of a hull dust cannot be seen and over the hull it is a veil, and
    /// a drive lays two rows of it - so for one commit the row facing the camera
    /// went on <see cref="StandOrder"/>, the way <see cref="Scrape"/> splits its
    /// sparks, to keep a diagonal from showing dust off one belt only. What it
    /// showed instead was dust on the armour, and that is the wrong half of the
    /// trade: a tank on this board is a billboard, so anything drawn over it
    /// reads as a fault in the picture rather than as dust passing in front. The
    /// row that is behind the hull stays behind it, and what makes either of
    /// them visible is the tank driving off them - see
    /// <see cref="TrackDust.Trail"/>.
    /// </summary>
    /// <param name="spot">The belt's contact patch, in board px.</param>
    /// <param name="away">Which way the mass leaves, unnormalised - see
    /// <see cref="TrackDust.Lay"/>, which throws it astern and a little
    /// outward.</param>
    public void Drift(Vector2 spot, float lift, Vector2 away, float might) =>
        Raise(_drifting, ref _nextDrift, Drifts, dressed: false,
              spot, lift, away, Vector2.Zero, might, DressOrder,
              // Earth, not propellant: a tank driving is not a tank firing.
              ProcKick.Cloud.Ground,
              // And shorter-lived and shorter-thrown than a shot's - see
              // TrackDust.Hang and TrackDust.Carry, which say how this throw
              // differs from a gun's without touching what the dust is made of.
              TrackDust.Hang, TrackDust.Carry, TrackDust.Ink,
              // And it lies on the board instead of standing on it, which is
              // what puts it on the rut that made it - ProcKick.Lying, and
              // TrackDust.Bed for how far in front of the belt it reaches.
              TrackDust.Bed, TrackDust.Creep, TrackDust.Born,
              lying: true);

    /// <summary>One cloud out of one of the two rings - see <see cref="Drift"/>
    /// for why there are two, and <see cref="Emit"/> for the same arrangement
    /// one effect along.
    ///
    /// <b>A ring that is filled has to be ticked</b>, which is a line each in
    /// <c>_Process</c>: a pool added here without its line there is an effect
    /// that is fired and never drawn.</summary>
    private void Raise(List<ProcKick> pool, ref int next, int size, bool dressed,
                       Vector2 spot, float lift, Vector2 along, Vector2 snout,
                       float might, int order, ProcKick.Cloud cloud,
                       float? life = null, float? reach = null,
                       float? ink = null, float? bed = null,
                       float? creep = null, float? swell = null,
                       bool lying = false)
    {
        if (Field.Atlas is null)
            return;
        while (pool.Count < size)
        {
            var made = new ProcKick();
            // Before Build, because it chooses the mesh - ProcKick.Lying.
            made.Lying = lying;
            // What this ring's clouds are shaped like, written before Build
            // and not after it: the shape uniforms are set when the material is
            // made, so a reach written afterwards is a number nothing reads -
            // and it looks exactly like a reach that was applied. Per ring
            // rather than per throw for Drift's reason: a pool that only ever
            // carries one kind of throw is the only one that may state this.
            if (reach is float far)
                made.Reach = far;
            // And how far in front of the seat it reaches - before Build with
            // the rest of the shape, and for a second reason on top of theirs:
            // lying, this moves the mesh as well as the uniform. Standing, root
            // is a hair, because below the contact line and behind the ground
            // are the same test. See TrackDust.Bed.
            // How small it is born - a trail is a wedge and a shot is not, see
            // ProcKick.Swell. Per ring with the rest of the shape.
            if (swell is float small)
                made.Swell = small;
            if (bed is float deep)
            {
                made.Root = deep;
                // And the sheet is twice that deep, so the seat is in the
                // middle of it. Standing, the quad is all on one side because
                // there is nothing on the other; lying, there is ordinary
                // ground both ways and a quad that keeps the standing shape
                // clips the near half off. Measured: it cost 42px of offset,
                // which was the whole of what was left.
                if (lying)
                    made.Tall = deep * 2.0f;
            }
            AddChild(made);
            made.Build(Field.Atlas.HexRect.Size.X, Squash, RiseFactor);
            // And how long, which is the other way round: it writes to the
            // materials, so it has to come after they exist - and it is Hasten
            // rather than Life, because a life on its own cuts the cloud off
            // where it had got to instead of shortening it. See ProcKick.Hasten.
            if (life is float span)
                made.Hasten(span);
            // And a lying cloud does not climb: its quad's up is the ground
            // going away from the camera, so a rise there is a drift nothing is
            // pushing. See ProcKick.Settle and TrackDust.Creep.
            if (creep is float keep)
                made.Settle(keep);
            // And how dense, on the model's own figure rather than on one
            // written here - Hasten's arrangement and its reason: the shader is
            // the only honest baseline, and a second copy of a default agrees
            // with it until the first edit.
            if (ink is float thick)
                made.Dial(ProcKick.Part.Dust, "dust_ink",
                          made.Dial(ProcKick.Part.Dust, "dust_ink") * thick);
            pool.Add(made);
        }
        ProcKick kick = pool[next % size];
        // Reset before the hook, multiplied after it - see Burst on why the
        // multiply alone compounds every shot until the cloud fills the screen.
        kick.Might = 1.0f;
        if (dressed)
            Fit?.Invoke(kick);
        next = (next + 1) % size;
        kick.Might *= might;
        // Every throw, because the pool hands the same cloud round - see
        // ProcKick.Order.
        kick.Order = order;
        // After Fit, so a bench panel setting these still lands - Dress writes
        // nothing while the kind has not changed hands.
        kick.Dress(cloud);
        // And the footing last, because it is the one shape number that cannot
        // be written at the pool: it is quoted on the board and the quad it has
        // to be measured in is scaled by this throw's own might. Divided rather
        // than multiplied for that reason - a band that shrank with the puff is
        // exactly the fault it is here to fix. See TrackDust.Foot.
        kick.Sit(spot, lift, Squash, RiseFactor);
        // Aimed after it is seated and sized, because Aim writes to the materials
        // and Sit does not touch them - the order is not load-bearing, but the
        // pair has to be complete before Fire or the first frame draws the last
        // shot's heading.
        kick.Aim(along, snout);
        kick.Fire();
    }

    /// <summary>The kicks as they stand, <see cref="Bursting"/>'s twin and
    /// read-only for its reason - and beside it the drives' dust, which is the
    /// same effect out of the other ring. Two lists rather than one for
    /// <see cref="Drift"/>'s reason, and they are two different sizes for
    /// it.</summary>
    public IReadOnlyList<ProcKick> Kicking => _kicking;
    public IReadOnlyList<ProcKick> Drifting => _drifting;

    /// <summary>
    /// A charge going off under a tank - <c>TankTick.Mined</c>.
    ///
    /// <b><see cref="Slam"/>, and the surface is the ground.</b> Three goes at
    /// this one, and every one of them was wrong about the same thing - where the
    /// flash is. <see cref="Land"/> first, a round's own earth column: the charge
    /// is under a hull, so all a mushroom shows above the roof is its top, and
    /// the eye gets a brown cloud sitting on the turret. Then two low dust throws
    /// (<see cref="Kick"/>) out from under the tracks, which is what a confined
    /// charge really does - and they drew <em>through</em> the hull, because a
    /// cloud on the board is a quad in the world and a tank is a billboard in
    /// front of it. Then the flash on the tank's own canvas, through the hit loop
    /// - and a burst on a plate is a burst on a plate whatever set it off: what
    /// it said was that a shell had struck the front of the tank.
    ///
    /// A mine goes off <em>on the ground</em>, so the burst stands on the ground.
    /// A round bursting against masonry (<see cref="ProcSlam.Surface.Masonry"/>)
    /// is the nearest event this board already draws and it is the right one:
    /// dust and grit off a hard surface, no armour spark, and nothing that
    /// climbs. What keeps it out of the hull is <see cref="Slam"/>'s own depth
    /// side, which is the machinery both board attempts did without - the seat is
    /// the tank's foot, the charge is an offset over that seat, and the quads
    /// stand on the camera side of the hull because the gases that can be seen at
    /// all are the ones that got out on this side.
    ///
    /// <b>And the crater is dug here rather than left to <see cref="Land"/>.</b>
    /// A mine takes the ground with it; the hole is what says a charge was buried
    /// there, and it is the same <see cref="Pits"/> call <see cref="Boom"/> makes,
    /// so a given might cannot dig two different holes.
    ///
    /// <b>Seated on the charge's own point, and the impact point handed over is
    /// nought.</b> The three hooks that draw a shell arriving seat their quad on
    /// the tank's foot and hand the impact over as an offset in screen px -
    /// because it is a point on a <em>plate</em>, where up the screen is up the
    /// hull. A charge on the ground has no such offset: hand a ground offset
    /// there and the far half of the hex becomes height, which is a burst
    /// climbing the back of the tank. So the seat is the charge and the burst
    /// stands at its own foot; which side of the hull it comes out on is the
    /// depth of that seat, which the board already knows because
    /// <see cref="Trunk"/> puts a point behind the tank behind the tank.
    /// </summary>
    /// <param name="spot">Where the charge was, in board px.</param>
    /// <param name="away">Which way the gases leave, unnormalised - see
    /// <c>TankTick.MineAway</c>.</param>
    /// <param name="ahead">Whether the charge lies on the camera's side of the
    /// hull's contact line - <c>Mines.Ahead</c>. <b>The flash goes in front of the
    /// tank when it is in front of the tank</b>, which for a hull driving at the
    /// viewer is where a mine under its leading end actually is; behind it when the
    /// tank drove up the board instead, where a flash over the hull would be light
    /// coming through the tank. The dust does not get the choice - see
    /// <see cref="ProcSlam.Lit"/>, and the report it names.</param>
    public void Mine(Vector2 spot, float lift, Vector2 away, float might = 1.0f,
                     bool ahead = false)
    {
        Slam(spot, lift, away, Vector2.Zero, behind: false, might,
             ProcSlam.Surface.Masonry, DressOrder,
             lit: ahead ? StandOrder : DressOrder);
        Pits?.Dig(spot, lift, Pits.Ink, Pits.Wide * might);
    }

    private readonly List<ProcSpall> _spalling = new();
    private int _nextSpall;
    private readonly List<ProcSpall> _scraping = new();
    private int _nextScrape;

    /// <summary>What to do to a ricochet before it goes off - <see cref="Dress"/>
    /// for the burst, fourth time, and the same argument: the pool is built on the
    /// first bounce, so a bench holding a panel of settings has nothing to write
    /// them into until then and must not have to wait for one.</summary>
    public Action<ProcSpall>? Hone;

    /// <summary>
    /// A round that struck armour and bounced - <see cref="ProcSpall"/>.
    ///
    /// <b>A fourth pool rather than a case inside <see cref="Burst"/> or
    /// <see cref="Kick"/></b>, for the kick's reason: it is a different event on a
    /// different trigger, it happens to the tank that was <em>hit</em> rather than
    /// to the one that fired, and it digs nothing - a round that failed to get
    /// through a plate did not dig anything either. Folded in, the pools would
    /// have shared a clock and one shell's spall would have cut short another
    /// shell's burst.
    ///
    /// <b><paramref name="spot"/> is board space</b>, with <paramref name="lift"/>
    /// the ground's height there - <see cref="Burst"/>'s pair, and worth
    /// repeating because getting it wrong is silent and has been.
    ///
    /// <paramref name="away"/> is the reflected direction as
    /// <see cref="AtlasSet.GroundDirection"/> gives it - unnormalised, so its
    /// length is the share of a ground length that survived the projection. See
    /// <see cref="ProcSpall.Aim"/>: both halves of it are used.
    ///
    /// <paramref name="plate"/> is the point of impact's offset from
    /// <paramref name="spot"/> in screen px, and <paramref name="behind"/> whether
    /// that plate is turned away from the camera. Both handed over rather than
    /// worked out here for <see cref="Vehicle.Bore"/>'s reason: where a shell hit
    /// a hull is one measurement, and this would be another projection of it.
    /// </summary>
    /// <param name="order">Which rung it sorts on - <see cref="StandOrder"/> for
    /// a fan that stands clear of the hull it came off, and see
    /// <see cref="ProcSpall.Order"/> for the one that cannot use it.</param>
    public void Spall(Vector2 spot, float lift, Vector2 away, Vector2 plate,
                      bool behind, float might = 1.0f, int order = StandOrder) =>
        Emit(_spalling, ref _nextSpall, ProcSpall.Cause.Round,
             spot, lift, away, plate, behind, might, order);

    /// <summary>
    /// One fan out of one of the two pools - the whole of what <see cref="Spall"/>
    /// and <see cref="Scrape"/> differ in, which is a cause and a pool.
    ///
    /// <b>A ring per cause, and it is this class's own argument applied once
    /// more.</b> <see cref="Spall"/> has a pool rather than a case inside
    /// <see cref="Burst"/> because one shell's spall must not cut another shell's
    /// burst short - <see cref="ProcSpall.Fire"/> is a clock set to nought, so a
    /// pooled effect handed out again is an effect cut short. A collision is as
    /// different from a ricochet as a ricochet is from a burst: different
    /// trigger, different hull, no round. Sharing the ring, a ram would cut a
    /// ricochet fired in the same second, and the deeper it was in the ring the
    /// less of either was left.
    ///
    /// <b>Both are one throw each, and that is the state this settled into.</b> A
    /// push used to grind for as long as it lasted, two fans every 0.09s, which
    /// owned the ring outright: it came round every 0.27s against a
    /// <see cref="ProcSpall.Life"/> of 0.95s and no fan of either kind reached a
    /// third of itself. That stream is gone - see <c>TankTick.Pushes</c> - so the
    /// rings are no longer about rate; they are about two events not being one.
    ///
    /// <b>A ring that is filled has to be ticked.</b> Both are walked in
    /// <c>_Process</c>, by a line each, and a ring added here without its line there
    /// is an effect fired and never drawn: it happened, and nothing about the picture
    /// said so.
    /// </summary>
    private void Emit(List<ProcSpall> pool, ref int next, ProcSpall.Cause cause,
                      Vector2 spot, float lift, Vector2 away, Vector2 plate,
                      bool behind, float might, int order)
    {
        if (Field.Atlas is null)
            return;
        while (pool.Count < Bursts)
        {
            var made = new ProcSpall();
            AddChild(made);
            made.Build(Field.Atlas.HexRect.Size.X, Squash, RiseFactor);
            pool.Add(made);
        }
        ProcSpall spall = pool[next % Bursts];
        // Reset before the hook, multiplied after it - see Burst on why the
        // multiply alone compounds every shot until the fan fills the screen.
        spall.Might = 1.0f;
        // And what struck the plate, on Might's own arrangement and for its
        // reason: stated before the hook, so a bench's own value still wins over
        // it, and stated whole rather than one family at a time - see
        // ProcSpall.Blame.
        spall.Blame(cause);
        Hone?.Invoke(spall);
        next = (next + 1) % Bursts;
        spall.Might *= might;
        spall.Order = order;
        spall.Sit(spot, lift, Squash, RiseFactor, behind);
        // Aimed after it is seated and sized, because Aim writes to the materials
        // and Sit does not touch them - the kick's order, and for its reason: the
        // pair has to be complete before Fire or the first frame draws the last
        // round's plate.
        spall.Aim(away, plate);
        spall.Fire();
    }

    /// <summary>
    /// Two hulls meeting in a ram: the scale struck off the plate they met on.
    ///
    /// <b><see cref="Spall"/> with the round taken out of it, and that is the
    /// whole difference</b> - which is why it is this pool rather than a sixth
    /// one. A ricochet is a fan of hot metal off a plate with a lump of shell
    /// carrying on past it; a ram is that fan with nothing carrying on, because
    /// nothing arrived. The fan, the dust and the sparks that reach the ground
    /// are the same three pictures of the same thing happening to armour, and a
    /// second copy of them would be two statements of one event - the argument
    /// <see cref="Mine"/> is written under, one method along.
    ///
    /// <b>Seated wherever the caller says, and the ram says the seam.</b> The
    /// dust of that same collision could not be seated between the hulls
    /// (<see cref="TankTick.Bumped"/>, at length): a quad standing between two
    /// billboards sorts by nothing at all. This one can, because what decides
    /// whether a hull hides its own sparks is the rung rather than the seat -
    /// see below. It was seated on each tank's own foot at first, with the point
    /// of contact carried in <paramref name="plate"/>, and that offset turns out
    /// to move the fan by a quarter of what it is handed: fine for one frame of
    /// contact, wrong for a push that lasts a hex - TankTick.Sparked has the
    /// measurement.
    ///
    /// <b>Which side of that hull it is on is said with the rung, not with the
    /// clearance.</b> <paramref name="behind"/> is still passed - it holds the two
    /// halves of the effect apart - but what decides whether a hull hides its own
    /// sparks is <see cref="ProcSpall.Order"/>: measured, the nudge alone moves 36
    /// px of a frame, and a ram driven away from the camera drew its own nose fan
    /// across its own engine deck. On <see cref="DressOrder"/> a fan is under
    /// every hull on the board and shows only where it reaches past the silhouette
    /// that made it, which is what a shower of sparks off the far side of a tank
    /// looks like.
    /// </summary>
    public void Scrape(Vector2 spot, float lift, Vector2 outward, Vector2 plate,
                       bool behind, float might = 1.0f) =>
        Emit(_scraping, ref _nextScrape, ProcSpall.Cause.Contact,
             spot, lift, outward, plate, behind, might,
             behind ? DressOrder : StandOrder);

    /// <summary>The ricochets as they stand, <see cref="Bursting"/>'s twin and
    /// read-only for its reason - and beside it the collisions, which are the
    /// same effect out of the other ring. Two lists rather than one for
    /// <see cref="Emit"/>'s reason, and they are asserted to be two.</summary>
    public IReadOnlyList<ProcSpall> Spalling => _spalling;
    public IReadOnlyList<ProcSpall> Scraping => _scraping;

    private readonly List<ProcSlam> _slamming = new();
    private int _nextSlam;

    /// <summary>What to do to a burst on armour before it goes off -
    /// <see cref="Dress"/> for the burst, fifth time, and the same argument: the
    /// pool is built on the first HE round, so a bench holding a panel of
    /// settings has nothing to write them into until then and must not have to
    /// wait for one.</summary>
    public Action<ProcSlam>? Temper;

    /// <summary>
    /// A high-explosive round bursting on the face of a plate -
    /// <see cref="ProcSlam"/>.
    ///
    /// <b>A fifth pool rather than a case inside <see cref="Spall"/></b>, which
    /// is the one place the argument needed making again: the two events are on
    /// the same trigger, at the same measured point, on the same tank. What
    /// separates them is that they are two <em>pictures</em> with two clocks, and
    /// a bounce and a burst can land on one hull inside a second of each other -
    /// two guns, or one gun and a hand-dealt hit. Folded in, the second would
    /// have restarted the first's clock and one shell's soot would have cut short
    /// another shell's fan.
    ///
    /// <b><paramref name="spot"/> is board space</b>, with <paramref name="lift"/>
    /// the ground's height there - <see cref="Burst"/>'s pair, and worth
    /// repeating because getting it wrong is silent and has been.
    ///
    /// <paramref name="outward"/> is the plate's own normal as
    /// <see cref="AtlasSet.GroundDirection"/> gives it - unnormalised, so its
    /// length is the share of a ground length that survived the projection.
    /// <paramref name="plate"/> is the point of impact's offset from
    /// <paramref name="spot"/> in screen px, and <paramref name="behind"/> whether
    /// that plate is turned away from the camera. All three handed over rather
    /// than worked out here for <see cref="Spall"/>'s reason, and out of the same
    /// two measurements.
    ///
    /// <b>And it digs nothing</b>, which the other four say too and which this one
    /// has to say hardest: a shell that went off against a hull did not touch the
    /// ground it is standing over. The crater under a tank is what
    /// <c>ammo_rack</c> will be, and that is a different event.
    ///
    /// <paramref name="face"/> is what the round burst against - armour or
    /// masonry, see <see cref="ProcSlam.Face"/>. A parameter rather than a second
    /// method because the two are one picture with four terms flipped, and a fact
    /// of the shot rather than a setting: only the caller that watched the round
    /// arrive knows what it arrived at.
    /// </summary>
    /// <param name="order">Which rung it sorts on - <see cref="StandOrder"/> for
    /// anything that burst outside a hull, and see <see cref="ProcSlam.Order"/>
    /// for the one event that cannot use it.</param>
    /// <param name="lit">The burning half's own rung when it differs from the
    /// dust's - <see cref="ProcSlam.Lit"/>. Null keeps them together, which is
    /// every event but the mine.</param>
    public void Slam(Vector2 spot, float lift, Vector2 outward, Vector2 plate,
                     bool behind, float might = 1.0f,
                     ProcSlam.Surface face = ProcSlam.Surface.Armour,
                     int order = StandOrder, int? lit = null)
    {
        if (Field.Atlas is null)
            return;
        while (_slamming.Count < Bursts)
        {
            var made = new ProcSlam();
            AddChild(made);
            made.Build(Field.Atlas.HexRect.Size.X, Squash, RiseFactor);
            _slamming.Add(made);
        }
        ProcSlam slam = _slamming[_nextSlam % Bursts];
        // Reset before the hook, multiplied after it - see Burst on why the
        // multiply alone compounds every shot until the mass fills the screen.
        slam.Might = 1.0f;
        Temper?.Invoke(slam);
        _nextSlam = (_nextSlam + 1) % Bursts;
        slam.Might *= might;
        // <b>The surface after the hook, like the size and for its reason.</b> A
        // bench dressing a pool has no business deciding what the last round hit,
        // and the caller does: this is the one thing about the burst that is a
        // fact of the shot rather than a setting.
        slam.Face = face;
        // And the rung, beside the surface and for its reason: both are facts of
        // this shot rather than settings, and both have to be stated on every
        // throw because the pool hands the same burst round - ProcSlam.Order.
        slam.Order = order;
        slam.Lit = lit ?? order;
        slam.Sit(spot, lift, Squash, RiseFactor, behind);
        // Aimed after it is seated and sized, because Aim writes to the materials
        // and Sit does not touch them - the kick's order, and for its reason: the
        // pair has to be complete before Fire or the first frame draws the last
        // round's plate.
        slam.Aim(outward, plate);
        slam.Fire();
    }

    /// <summary>The bursts on armour as they stand, <see cref="Bursting"/>'s twin
    /// and read-only for its reason.</summary>
    public IReadOnlyList<ProcSlam> Slamming => _slamming;

    private readonly List<ProcRack> _racking = new();
    private int _nextRack;

    /// <summary>What to do to a detonation before it goes off -
    /// <see cref="Dress"/> for the burst, sixth time and last, with the same
    /// argument every time: the pool is built on the first one, so a bench
    /// holding a panel of settings has nothing to write them into until then and
    /// must not have to wait for a tank to die.</summary>
    public Action<ProcRack>? Charge;

    /// <summary>
    /// A tank's ammunition going off - <see cref="ProcRack"/>.
    ///
    /// <b>A sixth pool, and here the argument for a pool at all is the weakest it
    /// has been</b>: a hull dies once, and <see cref="Wreck.Kill"/> already
    /// refuses to kill it twice. What the pool buys is two tanks dying inside one
    /// event's length of each other, which on a board where three of them shoot
    /// at each other is not a corner case, and it buys it for the price of the
    /// five that already exist.
    ///
    /// <b><paramref name="spot"/> is board space</b>, with <paramref name="lift"/>
    /// the ground's height there - <see cref="Burst"/>'s pair, and worth
    /// repeating because getting it wrong is silent and has been.
    ///
    /// <paramref name="deck"/> is where the turret ring sits above
    /// <paramref name="spot"/>, in screen px. <b>Handed over rather than worked
    /// out here, and it is the one measurement this effect takes</b> - see
    /// <see cref="ProcRack.Aim"/> on why the horizontal half of it is nought by
    /// construction.
    ///
    /// <b>And it digs nothing, which is the fifth time that sentence is written
    /// on this page and the first time it is arguable.</b>
    /// <see cref="Slam"/>'s own note says the crater under a tank is what this
    /// event would be - and standing on the board it is still wrong: the hull is
    /// its own lid, and what a mark under a wreck would do is sit under a wreck,
    /// unseen, for the rest of the battle. A scorch reaching out past the tracks
    /// is a different claim and one worth making later; a hole is not.
    /// </summary>
    public void Rack(Vector2 spot, float lift, Vector2 deck, float might = 1.0f)
    {
        if (Field.Atlas is null)
            return;
        while (_racking.Count < Bursts)
        {
            var made = new ProcRack();
            AddChild(made);
            made.Build(Field.Atlas.HexRect.Size.X, Squash, RiseFactor);
            _racking.Add(made);
        }
        ProcRack rack = _racking[_nextRack % Bursts];
        // Reset before the hook, multiplied after it - see Burst on why the
        // multiply alone compounds until the column fills the screen.
        rack.Might = 1.0f;
        Charge?.Invoke(rack);
        _nextRack = (_nextRack + 1) % Bursts;
        rack.Might *= might;
        rack.Sit(spot, lift, Squash, RiseFactor);
        // Aimed after it is seated and sized, because Aim writes to the materials
        // and Sit does not touch them - the kick's order, and for its reason.
        rack.Aim(deck / Mathf.Max(Field.Atlas.HexRect.Size.X, 1.0f));
        rack.Fire();
    }

    /// <summary>The detonations as they stand, <see cref="Bursting"/>'s twin and
    /// read-only for its reason.</summary>
    public IReadOnlyList<ProcRack> Racking => _racking;

    private readonly List<ProcWave> _waving = new();
    private int _nextWave;

    /// <summary>What to do to a wave before it goes out - <see cref="Charge"/>
    /// for the rack, same argument: the pool is built on the first death.</summary>
    public Action<ProcWave>? Surge;

    /// <summary>
    /// The blast wave of a destroyed tank on <paramref name="cell"/>, crossing on
    /// to the neighbours the rules say it reaches - <see cref="ProcWave"/>.
    ///
    /// <b>The stage decides the mask, because the stage is what knows the
    /// levels.</b> GDD states.md: the six neighbours of the wreck's own level
    /// and no other; a neighbour off the board is not reached either. One call
    /// for the event bench and the effects bench both, so the two cannot answer
    /// the level question differently.
    /// </summary>
    public void Wave(Vector2I cell, bool hushed = false)
    {
        if (Field.Atlas is null)
            return;
        while (_waving.Count < Bursts)
        {
            var made = new ProcWave();
            AddChild(made);
            made.Build(Field.Atlas.HexRect.Size.X, Squash, RiseFactor);
            _waving.Add(made);
        }
        ProcWave wave = _waving[_nextWave % Bursts];
        _nextWave = (_nextWave + 1) % Bursts;
        int level = Field.LevelAt(cell);
        var reached = new bool[HexField.EdgeHeadings.Length];
        for (int i = 0; i < reached.Length; i++)
        {
            Vector2I next = HexField.Step(cell, HexField.EdgeHeadings[i]);
            reached[i] = Field.InBounds(next) && Field.LevelAt(next) == level;
        }
        wave.Sit(Origin + Field.CellCentre(cell), level * Field.Lift, Squash,
                 RiseFactor);
        wave.Reach(reached);
        Surge?.Invoke(wave);
        // After the hook, so a board's hush is not undone by a bench's dials -
        // and after it rather than instead of it, because the hush is three
        // numbers and the hook is all of them.
        if (hushed)
            wave.Hush();
        wave.Fire();
    }

    /// <summary>The waves as they stand, <see cref="Racking"/>'s twin.</summary>
    public IReadOnlyList<ProcWave> Waving => _waving;

    /// <summary>
    /// How many smoke screens may stand on the board at once.
    ///
    /// <b>Four rather than <see cref="Bursts"/>' six, and it is a different kind
    /// of number.</b> The burst pools are six because two tanks can die inside one
    /// second and neither death may cut the other's picture short; nothing about
    /// six is a rule. A screen is not an event but a thing occupying a cell, and
    /// the rules put a pot on a tank rather than on the board - so the ceiling is
    /// how many pots a side can have in the air, and four is what M3 asks for.
    /// Laid on a fifth cell, the oldest goes: named here rather than discovered as
    /// a screen that quietly failed to appear.
    /// </summary>
    public const int Screens = 4;

    private readonly List<ProcScreen> _screening = new();
    private int _nextScreen;

    /// <summary>What to do to a screen before it is laid - <see cref="Attire"/> for
    /// the smoke, same argument: the pool is built on the first pot, so a bench
    /// holding a panel of dials cannot hold a reference to one.</summary>
    public Action<ProcScreen>? Veil;

    /// <summary>
    /// Lay a smoke screen on <paramref name="cell"/> - <see cref="ProcScreen"/>,
    /// from its own pool.
    ///
    /// <b>One cell, one screen.</b> A pot on a cell that already has one restarts
    /// that screen's rise rather than standing a second cloud inside the first,
    /// which is what the rules mean by a hex being screened - and it is also the
    /// only way the pool's ceiling reads as a ceiling rather than as flicker.
    ///
    /// <b>A free screen before an old one.</b> Round-robin is right for bursts,
    /// where every entry is dying anyway; here an entry is standing, and taking the
    /// next in line would put out a live screen while a dead one sat unused.
    /// </summary>
    public ProcScreen? Screen(Vector2I cell)
    {
        if (Field.Atlas is null)
            return null;
        while (_screening.Count < Screens)
        {
            var made = new ProcScreen();
            AddChild(made);
            made.Build(Field.Atlas.HexRect.Size.X, Squash, RiseFactor);
            _screening.Add(made);
        }
        ProcScreen? veil = null;
        foreach (ProcScreen one in _screening)
            if (one.Alive && one.Cell == cell)
            {
                veil = one;
                break;
            }
        if (veil is null)
            foreach (ProcScreen one in _screening)
                if (!one.Alive)
                {
                    veil = one;
                    break;
                }
        if (veil is null)
        {
            veil = _screening[_nextScreen % Screens];
            _nextScreen = (_nextScreen + 1) % Screens;
        }
        veil.Cell = cell;
        veil.Sit(Origin + Field.CellCentre(cell), Field.LevelAt(cell) * Field.Lift,
                 Squash, RiseFactor);
        Veil?.Invoke(veil);
        veil.Lay();
        return veil;
    }

    /// <summary>
    /// Take the screen off <paramref name="cell"/>: the rules' two rounds are up,
    /// or - with <paramref name="gust"/> - a hull drove through it.
    ///
    /// Named for the event rather than for the picture, because that is what
    /// arrives: <c>SmokeDissipated</c> is a fact about the board, and how long the
    /// cloud takes to go is <see cref="ProcScreen"/>'s business.
    /// </summary>
    public void Unscreen(Vector2I cell, bool gust = false)
    {
        foreach (ProcScreen one in _screening)
            if (one.Alive && one.Cell == cell)
                one.Lift(gust);
    }

    /// <summary>Whether the rules still have a screen standing on this cell - what
    /// a line of fire would ask. <see cref="ProcScreen.Standing"/> rather than
    /// <c>Alive</c>: a cloud still thinning out no longer blinds anybody.</summary>
    public bool Screened(Vector2I cell)
    {
        foreach (ProcScreen one in _screening)
            if (one.Standing && one.Cell == cell)
                return true;
        return false;
    }

    /// <summary>The screens as they stand, <see cref="Waving"/>'s twin.</summary>
    public IReadOnlyList<ProcScreen> Screening => _screening;

    private readonly List<ToonBlast> _tooning = new();
    private int _nextToon;

    /// <summary>What to do to a drawn explosion before it goes off -
    /// <see cref="Attire"/> for the sheet's, same argument.</summary>
    public Action<ToonBlast>? Style;

    /// <summary>
    /// The drawn explosion on a point of the board - <see cref="ToonBlast"/>,
    /// from its own pool, <see cref="Boom"/>'s twin: a third picture of one
    /// event, and the three are meant to stand side by side on one board.
    /// </summary>
    public void Toon(Vector2 spot, float lift)
    {
        if (Field.Atlas is null)
            return;
        while (_tooning.Count < Bursts)
        {
            var made = new ToonBlast();
            AddChild(made);
            made.Build(Field.Atlas.HexRect.Size.X, Squash, RiseFactor);
            _tooning.Add(made);
        }
        ToonBlast blast = _tooning[_nextToon % Bursts];
        _nextToon = (_nextToon + 1) % Bursts;
        Style?.Invoke(blast);
        blast.Sit(spot, lift, Squash, RiseFactor);
        blast.Fire();
    }

    /// <summary>The drawn explosions as they stand, <see cref="Booming"/>'s twin.</summary>
    public IReadOnlyList<ToonBlast> Tooning => _tooning;

    private readonly List<ProcBall> _balling = new();
    private int _nextBall;

    /// <summary>What to do to a fireball before it goes off -
    /// <see cref="Charge"/> for the rack, same argument.</summary>
    public Action<ProcBall>? Fuse;

    /// <summary>
    /// The fireball of a destroyed tank on a point of the board -
    /// <see cref="ProcBall"/>, from its own pool, <see cref="Rack"/>'s twin at
    /// the scale of a cell. <paramref name="heart"/> is where the charge went
    /// off over <paramref name="spot"/>, in tile widths - the rack's deck.
    /// </summary>
    public void Ball(Vector2 spot, float lift, Vector2 heart, float might = 1.0f,
                     bool grounded = true)
    {
        if (Field.Atlas is null)
            return;
        while (_balling.Count < Bursts)
        {
            var made = new ProcBall();
            AddChild(made);
            made.Build(Field.Atlas.HexRect.Size.X, Squash, RiseFactor);
            _balling.Add(made);
        }
        ProcBall ball = _balling[_nextBall % Bursts];
        _nextBall = (_nextBall + 1) % Bursts;
        ball.Might = 1.0f;
        ball.Grounded = grounded;
        Fuse?.Invoke(ball);
        ball.Might *= might;
        ball.Sit(spot, lift, Squash, RiseFactor);
        ball.Aim(heart);
        ball.Fire();
    }

    /// <summary>The fireballs as they stand, <see cref="Racking"/>'s twin.</summary>
    public IReadOnlyList<ProcBall> Balling => _balling;

    /// <summary>Which picture a tank's death plays: the fireball
    /// (<see cref="Ball"/>) by default, the rack (<see cref="Rack"/>) when off.
    /// One switch for every board, so the harness, the tank bench and the event
    /// bench cannot answer it differently; the effects bench keeps both keys.</summary>
    public static bool BallDeaths = true;

    /// <summary>
    /// How much bigger the plume off a tank dying in water is than the one a
    /// round raises there.
    ///
    /// <b>A multiplier on the shell rather than a second set of numbers</b>, the
    /// argument <see cref="Waves"/> already makes one level down: what is going
    /// off is larger, and every proportion of the event is the same event's. Two
    /// tuned plumes would be two things to keep in step for one picture.
    ///
    /// The ring comes with it and needs no line of its own -
    /// <see cref="Splash"/> strikes the ripple field with what it is given, so a
    /// death throws a wave nearly twice a shell's and it spreads at the field's
    /// own speed.
    /// </summary>
    public const float Drowned = 1.80f;

    /// <summary>
    /// A tank's death, as the boards play it - the one call the three
    /// <c>Detonated</c> hooks make. <paramref name="deck"/> is the turret ring
    /// over <paramref name="spot"/> in screen px, <see cref="Rack"/>'s argument;
    /// the fireball takes it in tile widths, and that division is the whole of
    /// what this method does besides choosing.
    /// </summary>
    public void Detonate(Vector2 spot, float lift, Vector2 deck, float might = 1.0f)
    {
        // <b>Wet before either of them, which is Land's own first line and for
        // its reason.</b> Both pictures below are made of thrown earth and lit
        // dust, and a hull that lets go while it is sitting in a pond throws
        // neither: what comes up is the water it was in. So the board asks what
        // is under the point before it asks which death picture is switched on -
        // the substance decides first and the setting second, exactly as it does
        // for a round arriving.
        //
        // <b>Bigger than a shell's plume, and the factor is named.</b> A round
        // going into water is a round; this is a tank's whole rack, and the same
        // multiplier carries the ring the plume already strikes into the ripple
        // field - see Splash, where Waves is applied on top of it.
        if (Spout && Wet(spot, out float sea))
        {
            Splash(spot, sea, might * Drowned);
            return;
        }
        if (!BallDeaths)
        {
            Rack(spot, lift, deck, might);
            return;
        }
        if (Field.Atlas is null)
            return;
        Ball(spot, lift, deck / Mathf.Max(Field.Atlas.HexRect.Size.X, 1.0f), might);
    }

    /// <summary>The knock-out flash: the death's fireball at the ring, small,
    /// and without its ground - <see cref="Detonate"/>'s twin for
    /// <c>TankTick.Flashed</c>. Always the ball, whichever picture deaths play:
    /// the rack model is a detonation of the whole rack and has no small setting
    /// that reads as anything. No rings and no dust carpet (<see cref="ProcBall.Grounded"/>):
    /// the round went off inside the hull, and a shock running out across the
    /// ground from it was the one thing seen at this size.</summary>
    public void Flash(Vector2 spot, float lift, Vector2 deck, float might)
    {
        if (Field.Atlas is null)
            return;
        Ball(spot, lift, deck / Mathf.Max(Field.Atlas.HexRect.Size.X, 1.0f), might,
             grounded: false);
    }

    private readonly List<SheetBlast> _spouting = new();
    private int _nextSpout;

    /// <summary>
    /// A hull going into the pond off the bank: the bow wave and the two fans
    /// of spray, not the shell's plume - see <see cref="Plunge"/> for why the
    /// two are different events, and docs/swim-plan.md step 10. The ripple
    /// field is struck as well, at the nose and at both flanks, so the ring the
    /// pond carries to the bank is the hull's shape and not a point's.
    /// </summary>
    /// <param name="heading">The hull's screen-space ground direction -
    /// <see cref="AtlasSet.GroundDirection"/> at its facing.</param>
    /// <param name="halfLen">Half the hull's length on the ground, in ground px,
    /// the class's size already in it; the fans stand at 0.55 of it.</param>
    /// <param name="look">Which of the two looks - the root's setting, handed in
    /// with the call rather than held here, so a root with no stage still has
    /// the setting to hand.</param>
    public void Plunge(Vector2 spot, float top, Vector2 heading, float halfLen,
                       float might, Plunge.Style look)
    {
        if (Field.Atlas is null)
            return;
        Standing();
        while (_plunging.Count < Bursts)
        {
            var made = new Plunge();
            AddChild(made);
            made.Build(Squash, RiseFactor);
            _plunging.Add(made);
        }
        Plunge spray = _plunging[_nextPlunge % Bursts];
        _nextPlunge = (_nextPlunge + 1) % Bursts;
        GD.Print($"stage: plunge at ({spot.X:F0},{spot.Y:F0}) heading "
                 + $"({heading.X:F2},{heading.Y:F2}) half {halfLen:F0}px might "
                 + $"{might:F2} {look}");
        spray.Look = look;
        spray.Might = might;
        spray.Sit(spot, top, heading, halfLen, halfLen * 0.55f, Squash, RiseFactor);
        spray.Fire();
        // The ring, in the field's own space - see Splash for the conversion and
        // for Waves. Three strikes along the hull rather than one at its middle:
        // the nose goes in first and hardest, the flanks after it, and a single
        // hole under the middle of the deck is a ring from the wrong place.
        if (Wash is null)
            return;
        Vector3 nose = Ground(spot, top);
        Vector2 at = new(nose.X, nose.Z);
        // The screen heading back on the field's ground plane, as Plunge.Sit
        // does it: the row divided by the squash - see World.
        Vector2 along = new(heading.X, heading.Y / Mathf.Max(Squash, 0.0001f));
        along = along.LengthSquared() > 1e-6f ? along.Normalized() : Vector2.Right;
        Vector2 across = new(-along.Y, along.X);
        Wash.Strike(at + along * halfLen * 0.8f, might * Waves);
        Wash.Strike(at + across * halfLen * 0.55f, might * Waves * 0.5f);
        Wash.Strike(at - across * halfLen * 0.55f, might * Waves * 0.5f);
    }

    private readonly List<Plunge> _plunging = new();
    private int _nextPlunge;

    /// <summary>Where a drowning tank's air comes up on screen, as a fraction
    /// of the hull's crest above the cell's centre: 0 is the cell's own row,
    /// 1 the hull's roof. At 0.8 the rings stood over and above the turret; a
    /// little over half puts them on the deck and the turret's foot, where
    /// the hatches are and where the eye looks for them.</summary>
    public const float DeckSeat = 0.55f;

    /// <summary>The refraction of a hull's underwater picture - see
    /// <c>refract</c> in the paint shader and docs/swim-plan.md step 12.
    /// <see cref="Refraction"/> is the switch and strength (nought turns it
    /// off; a field rather than a const so a panel row can hold it),
    /// <see cref="RefractDepth"/> the tile rows under the line over which it
    /// grows from nothing, <see cref="RefractPush"/> the crumple's push in
    /// tile px at full depth, <see cref="RefractSqueeze"/> the fraction by which
    /// a row under the line shows a deeper row, <see cref="RefractWash"/> the
    /// ripple field's slope turned into px.</summary>
    public static float Refraction = 1.0f;
    public const float RefractDepth = 12.0f;
    public const float RefractPush = 3.0f;
    public const float RefractSqueeze = 0.12f;
    public const float RefractWash = 3.0f;

    /// <summary>How the refraction reads the swell against the glints' own
    /// field: x scales its features (0.5 is waves twice as long), y slows its
    /// clock. Asked for twice by the user - the shiver was too quick both
    /// times - so a knob and not a number in the shader.</summary>
    public static Godot.Vector2 RefractPace = new(0.5f, 0.6f);

    /// <summary>What to do to a plume before it goes up - <see cref="Dress"/> for
    /// the burst, seventh time, same argument: the pool is built on the first
    /// round into the water, and a panel must not have to wait for one.</summary>
    public Action<SheetBlast>? Drench;

    /// <summary>
    /// Whether a round landing in water raises a plume or the burst it used to.
    ///
    /// <b>On the stage rather than on the tick, which is where the other three
    /// switches of this shape live.</b> <c>--spall</c>, <c>--slam</c> and
    /// <c>--rack</c> each gate a <em>fact about a shot</em> and belong with the
    /// shot; this gates what the <em>board</em> makes of a point on itself, and
    /// the board is the only thing that knows the point is wet. It sits beside
    /// <see cref="Blast"/> for that reason and is read in the same method.
    ///
    /// Off, a round into the pond throws the earth burst again - which is what it
    /// did until this was written, so the flag is the A/B against the picture
    /// that was reported as wrong: same cloud, brown, with a crater under it.
    /// </summary>
    public bool Spout = true;

    /// <summary>
    /// Whether this point of the board is water, and how high that water stands.
    ///
    /// <b>The one question the board was never asked, and the whole of what made
    /// <c>water_he</c> a bug rather than a gap.</b> A landed round carries a point
    /// and the lift it was fired from - see <c>TankTick.Loose</c>, which states
    /// the level-gun assumption it is made under - and every burst on this board
    /// took that pair without asking what was underneath. So a shell into the
    /// pond raised a cone of thrown earth, and nothing errored, because a wet
    /// cell is a perfectly good cell.
    ///
    /// <b>The surface and not the bed</b>, which is the second half of the same
    /// answer: <see cref="HexField.WaterTop"/> stands
    /// <see cref="HexField.WaterRise"/> over the floor of the cell it fills, and
    /// seated on the lift that travelled with the round the plume would come up
    /// out of the bottom of the pond. Asked here rather than at the two call
    /// sites, so the picture and the ring cannot disagree about where the surface
    /// is.
    /// </summary>
    public bool Wet(Vector2 spot, out float top)
    {
        top = 0.0f;
        if (Field.Atlas is null || !Field.HasWater)
            return false;
        Vector2I cell = Field.CellAt(spot - Origin);
        if (!Field.IsWater(cell))
            return false;
        top = Field.WaterTop(cell);
        return true;
    }

    /// <summary>
    /// A round that went into water: the imported burst, in white.
    ///
    /// <b>The same <see cref="SheetBlast"/> the board raises on earth, from a
    /// pool of its own, with one ramp swapped - and that swap is the finding
    /// rather than a shortcut.</b> A shell into a pond throws the event this class
    /// already draws: a mass of many puffs going up, curling, sagging and
    /// spreading, with the round's own flash under it. What the substance decides
    /// is the colour; how the mass moves it does not decide at all. See
    /// <see cref="SheetBlast.Wet"/>, which is the whole of the difference, and
    /// what it cost to learn.
    ///
    /// <b>A third pool rather than a flag on <see cref="Boom"/>, for the reason
    /// the second one exists.</b> A pool is a set of built nodes, and what is
    /// built into these is the white ramp: <see cref="SheetBlast.Build"/> reads
    /// <see cref="SheetBlast.Wet"/> once. Sharing the earth pool would mean
    /// rebuilding a material per shot, on the pool a bench compares against.
    ///
    /// <b><paramref name="top"/> is the water's own height, not the cell's</b> -
    /// <see cref="Wet"/> is what hands it over, and it is the whole of what this
    /// effect needs the field for.
    ///
    /// <b>And it digs nothing.</b> The one burst on this board that needs no
    /// argument for that: a hole in water is not a mark, it is the wave, and the
    /// wave is <see cref="Ripples"/>'s. <see cref="Pits"/> is not touched, so a
    /// shot into the pond leaves the cell exactly as it found it - which is the
    /// second half of what was asked for, the first being the colour.
    /// </summary>
    public void Splash(Vector2 spot, float top, float might = 1.0f)
    {
        if (Field.Atlas is null)
            return;
        // <b>The pond before the shell that goes into it.</b> Every other burst
        // needs nothing of the board but a point; this one needs the ripple field,
        // and the field is laid when the pond is built. See Standing.
        Standing();
        while (_spouting.Count < Bursts)
        {
            // Wet before Build, because the ramp is baked into the material there.
            var made = new SheetBlast { Wet = true };
            AddChild(made);
            made.Build(Field.Atlas.HexRect.Size.X, Squash, RiseFactor);
            _spouting.Add(made);
        }
        SheetBlast spout = _spouting[_nextSpout % Bursts];
        // Reset before the hook, multiplied after it - see Burst on why the
        // multiply alone compounds until the plume fills the screen.
        spout.Might = 1.0f;
        Drench?.Invoke(spout);
        _nextSpout = (_nextSpout + 1) % Bursts;
        spout.Might *= might;
        spout.Sit(spot, top, Squash, RiseFactor);
        spout.Fire();
        // And the waves, in the field's own space. The one expression that puts a
        // board point on the pond's surface is this class's - see Ground - and a
        // second copy of it in a caller is how a push comes to land a level away
        // from the splash it belongs to.
        //
        // <b>Hard, and by a named factor rather than by folding it into the
        // field's own numbers.</b> Ripples.Dent is what one shell does to still
        // water; this is the shell a HE round is, and it is asked for as waves
        // rather than a ripple. Kept here because it is a fact about this
        // effect - a hull's wake, a ford and a drop all go through the same field
        // and none of them wants it.
        Vector3 at = Ground(spot, top);
        Wash?.Strike(new Vector2(at.X, at.Z), might * Waves);
    }

    /// <summary>
    /// How much harder a bursting shell hits the water than
    /// <see cref="Ripples.Dent"/> alone says.
    ///
    /// <b>Three, and the number is the difference between a ripple and a wave.</b>
    /// The field's own dent is tuned to a thing entering water - it is what a
    /// solid going in displaces - and a round detonating there does not enter it,
    /// it throws a column of it out. What that leaves is a hole several times as
    /// deep, and a hole several times as deep is a ring several times as tall,
    /// which is what carries to the bank and back.
    ///
    /// <b>It scales the displacement and nothing else, which is why it is safe to
    /// turn.</b> The five-point stencil's stability bound is on the coupling, not
    /// on the amplitude - see <see cref="Ripples.Bound"/> - so a deeper hole
    /// spreads at exactly the same speed, with the same reflections, and only
    /// stands taller. Every aspect ratio of the wave is untouched.
    /// </summary>
    public const float Waves = 3.0f;

    private readonly List<SheetBlast> _splintering = new();
    private int _nextShard;

    /// <summary>What to do to a wood burst before it goes off -
    /// <see cref="Attire"/> for the sheet's, <see cref="Drench"/> for the plume's,
    /// same argument.</summary>
    public Action<SheetBlast>? Shiver;

    /// <summary>
    /// A round that went off in a wood: the imported burst, in leaf and splinter.
    ///
    /// <b>The third pool, and it exists for the reason the second one does.</b>
    /// The ramp is baked into the material by <see cref="SheetBlast.Build"/>, which
    /// reads <see cref="SheetBlast.Leaf"/> once; sharing the earth pool would mean
    /// rebuilding a material per shot, on the pool a bench compares against.
    ///
    /// <b>And it digs nothing.</b> The pond's argument, arriving at the wood by a
    /// different road: a shell into water leaves no mark because the mark is the
    /// wave, and a shell into a canopy leaves none because it never got to the
    /// ground. What it marks instead is the trees - see below - and a crater under
    /// an airburst would be the board saying the round landed twice.
    ///
    /// <b>The wood is shoved by this and not by the caller</b>, which is
    /// <see cref="Splash"/>'s waves again: the one thing every burst on a wooded
    /// cell does is throw the crowns, and left to the caller it would be done by
    /// whoever remembered. <see cref="Grove.TreeBlast"/> says how hard, and that it
    /// is harder than a hit on a plate at the tree line.
    ///
    /// <b>What it does not do is set the wood alight.</b> Burning is a noun the
    /// cell carries and the rules put there - <see cref="Playback.Blast"/> is where
    /// that happens, on the same event. This class draws.
    /// </summary>
    public void Splinter(Vector2 spot, float lift, float might = 1.0f)
    {
        if (Field.Atlas is null)
            return;
        while (_splintering.Count < Bursts)
        {
            // Timber before Build, because the ramp is baked into the material
            // there - and it sets the shape as well as the colour, which water did
            // not need. See SheetBlast.Timber.
            var made = new SheetBlast();
            made.Timber();
            AddChild(made);
            made.Build(Field.Atlas.HexRect.Size.X, Squash, RiseFactor);
            _splintering.Add(made);
        }
        SheetBlast shard = _splintering[_nextShard % Bursts];
        // Reset before the hook, multiplied after it - see Burst on why the
        // multiply alone compounds.
        shard.Might = 1.0f;
        Shiver?.Invoke(shard);
        _nextShard = (_nextShard + 1) % Bursts;
        shard.Might *= might;
        shard.Sit(spot, lift, Squash, RiseFactor);
        shard.Fire();
        Wood?.Shock(spot - Origin, Wood.TreeBlast * might);
    }

    /// <summary>The wood bursts as they stand, for a bench -
    /// <see cref="Booming"/>'s twin.</summary>
    public IReadOnlyList<SheetBlast> Splintering => _splintering;

    /// <summary>The plumes as they stand, <see cref="Bursting"/>'s twin and
    /// read-only for its reason.</summary>
    public IReadOnlyList<SheetBlast> Spouting => _spouting;

    /// <summary>
    /// Where a hull has burnt the ground under it: what is left after the
    /// detonation and the eighteen seconds of fire behind it.
    ///
    /// <b>An ash mark and not a crater, which is the fifth time this page says
    /// "it digs nothing" and the first time the sentence has an exception.</b>
    /// <see cref="Craters"/>'s own note is what decides it, read forwards: that
    /// map is 8 world units to the texel and cannot say <em>lighter</em> at all,
    /// so a crater - a dark hole in a pale splash of thrown soil - could only ever
    /// be drawn half right there, and it left for a plane of its own. A scorch is
    /// the other thing: it is only dark, and it is a cell wide rather than five
    /// texels. That is the exact shape this map was built for - a fire covering
    /// whole cells - and it is why the burnt forest is still rasterised into it.
    ///
    /// <b>And there is no hole to draw anyway.</b> A round that goes off inside a
    /// hull is under its own lid: the ground takes the heat and none of the
    /// blast, so what a wreck leaves is scorched earth rather than an excavation.
    ///
    /// <b>A point rather than a cell</b>, for <see cref="Craters"/>'s reason -
    /// a mask built from a per-cell number draws the grid - even though a tank
    /// dies standing on one. What it is splatted at is its contact point, so a
    /// hull that died half over a rim scorches half over the rim.
    ///
    /// <b>It does not fade</b>, again the crater's argument: burnt ground does
    /// not recover, and the wreck is sitting on it for the rest of the match in
    /// any case. Bounded like the pits are, oldest forgotten - a bound on cost
    /// and not a claim that the earth healed.
    /// </summary>
    private readonly List<(Vector2 Spot, float Lift)> _scalds = new();

    /// <summary>Which hulls have already scorched their ground, so a wreck
    /// burning for the rest of the match marks it once. Keyed by the tank rather
    /// than counted, because a board can lose three of them.</summary>
    private readonly HashSet<Vehicle> _scalded = new();

    /// <summary>How many the board remembers - <see cref="Craters.Capacity"/>'s
    /// argument at a tenth of the number, because a tank dies once and there are
    /// three of them.</summary>
    public const int Scalds = 8;

    /// <summary>How wide a wreck's scorch is, in tile widths, and how dark.
    ///
    /// <b>Wider than the tank, which is the whole of whether it can be seen.</b>
    /// The hull is sitting on the middle of it for the rest of the match, so what
    /// shows is the ring outside the tracks - and a mark cut to the silhouette
    /// would be a mark nobody ever sees. Darker than the forest's ash for the
    /// opposite reason: a burnt tree is a scatter of ash over grass and this is
    /// where a fuel fire stood.
    /// </summary>
    /// <b>Cut back from 1.24 of a tile across after one look.</b> Read that wide
    /// it reached well onto the next cell and stopped saying burnt ground at all -
    /// it said vignette, because a mark bigger than the thing that made it has no
    /// cause on the board. Slightly wider than the hull is the whole requirement.
    public float ScaldWide = 0.45f;
    public float ScaldInk = 0.88f;

    /// <summary>The scorches as they stand, for the checks.</summary>
    public IReadOnlyList<(Vector2 Spot, float Lift)> Scalding => _scalds;

    /// <summary>
    /// Mark the ground under a hull that has died.
    ///
    /// <b>Raised by the stage off board state rather than handed over by a
    /// hook</b>, which is the split <see cref="Craters"/> and <c>Wildfire</c>
    /// both live under: that a hull burnt here is a fact, and which map it is
    /// rasterised into is a question about the picture. <see cref="Place"/>
    /// already walks every tank every frame and already knows where its feet are,
    /// so a hook would be a second path to a thing this one cannot miss.
    ///
    /// <b>Every death and not only a detonation</b>, unlike the burst: a hull
    /// that merely caught fire burns for eighteen seconds in the same place, and
    /// the ground does not ask which of the two deaths it was.
    /// </summary>
    private void Scald(Vehicle vehicle)
    {
        if (!_scalded.Add(vehicle))
            return;
        if (_scalds.Count >= Scalds)
            _scalds.RemoveAt(0);
        // <b>Board space, and NOT the wood solver's space.</b> Origin is what
        // TankTick takes off a point before handing it to the forest, and taking
        // it off here as well put the mark a tank's height up the screen of the
        // hull it belongs to - Burst's own warning, which says getting this wrong
        // is silent and has been. What Slam, Spall and Rack are all handed is
        // GroundPoint untouched, and this is the same point.
        _scalds.Add((vehicle.GroundPoint,
                     vehicle.LiftOf(vehicle.GroundPoint)));
        // The map is rebuilt from the list, so dropping it is the whole of asking
        // for a redraw - Soot's own arrangement for the fire.
        _soot = null;
    }

    private readonly List<PitArt> _etched = new();
    private int _etchedAt = -1;

    private readonly List<MineArt> _marking = new();
    private int _markedAt = -1;

    /// <summary>The markers as they stand, <see cref="Etched"/>'s twin and
    /// read-only for its reason.</summary>
    public IReadOnlyList<MineArt> Marking => _marking;

    /// <summary>
    /// Put the mines on the board - <see cref="MineArt"/>, and the rules' own
    /// "мины видны обоим игрокам всегда".
    ///
    /// <b>On the cover stamp, exactly as the craters are on theirs</b>
    /// (<see cref="Craters.Stamp"/>): what is laid on a cell is a fact until
    /// somebody changes it, and a mine that has gone off changes it - the same
    /// <see cref="CoverState.Cleared"/> that spends the charge takes the marker
    /// off, and <c>R</c> laying the covers again brings it back. Which cells are
    /// due one is <see cref="Mines.Marked"/>'s answer and not this method's, so a
    /// check can ask the rule without a board.
    ///
    /// The point is <see cref="Mines.At"/>, which is the whole of F5's
    /// requirement: the marker and the burst cannot disagree about where the mine
    /// is, because there is one place that says.
    /// </summary>
    private void Markers()
    {
        if (Field.Atlas is null || !MineArt.Loaded)
            return;
        if (Field.CoverStamp == _markedAt)
            return;
        _markedAt = Field.CoverStamp;
        List<Vector2I> due = Mines.Marked(Field);
        float tile = Field.Atlas.HexRect.Size.X;
        for (int k = 0; k < due.Count; k++)
        {
            while (_marking.Count <= k)
            {
                var made = new MineArt();
                AddChild(made);
                made.Build(tile, RiseFactor);
                _marking.Add(made);
            }
            _marking[k].Show(Origin + Mines.At(Field, due[k]),
                             Field.LevelAt(due[k]) * Field.Lift, Squash,
                             RiseFactor);
        }
        for (int k = due.Count; k < _marking.Count; k++)
            _marking[k].Hide();
    }

    /// <summary>What to do to a crater's art before it is shown - Dress's
    /// argument, third time: the planes are built as craters are dug, so a panel of
    /// numbers has nothing to write into until the first shot and must not have to
    /// wait for one.</summary>
    public Action<PitArt>? Scar;

    /// <summary>The crater art as it stands, for a bench that shows what one is
    /// made of. Read-only, like the two burst pools.</summary>
    public IReadOnlyList<PitArt> Etched => _etched;

    /// <summary>
    /// Put the craters on the board.
    ///
    /// <b>Only when the list has changed</b>, which is what
    /// <see cref="Craters.Stamp"/> is for: a crater is dug once and is then a fact,
    /// unlike the fire the ash map draws, so re-sending ten uniforms per plane per
    /// frame would be work done to say nothing happened. A panel turning a dial
    /// bumps the stamp itself - see <see cref="Restamp"/>.
    /// </summary>
    private void Etchings()
    {
        if (Pits is null || Field.Atlas is null)
            return;
        if (Pits.Stamp == _etchedAt)
            return;
        _etchedAt = Pits.Stamp;
        float tile = Field.Atlas.HexRect.Size.X;
        for (int k = 0; k < Pits.Pits.Count; k++)
        {
            while (_etched.Count <= k)
            {
                var made = new PitArt();
                AddChild(made);
                made.Build(Squash, RiseFactor);
                Scar?.Invoke(made);
                _etched.Add(made);
            }
            Craters.Pit pit = Pits.Pits[k];
            // A seed off the point it was dug at, so two craters are two craters
            // and the same crater is the same one every frame - and so a reload of
            // the board draws what it drew before.
            float seed = Mathf.Abs(pit.Spot.X * 0.37f + pit.Spot.Y * 0.71f) % 64.0f;
            _etched[k].Show(pit.Spot, pit.Lift, pit.Wide * tile, pit.Much, seed,
                            Squash, RiseFactor);
        }
        for (int k = Pits.Pits.Count; k < _etched.Count; k++)
            _etched[k].Hide();
    }

    /// <summary>Draw the craters again on the next frame though the list has not
    /// changed - what a panel needs after turning one of their numbers.</summary>
    public void Restamp() => _etchedAt = -1;

    /// <summary>Every burst out and nothing drawn - the reset. The craters are the
    /// board's and are filled by whoever owns them.</summary>
    public void Quench()
    {
        foreach (SheetBlast blast in _booming)
            blast.Douse();
        // The plume too, and it belongs with these two rather than with the three
        // that hang off armour: all three of these are what the board makes of a
        // round landing on it, and Land is the one method that picks between them.
        foreach (SheetBlast spout in _spouting)
            spout.Douse();
        foreach (SheetBlast shard in _splintering)
            shard.Douse();
        foreach (ProcWave wave in _waving)
            wave.Douse();
        // Both rings of dust. The shot's was missing here and it did not show: a
        // muzzle cloud is 1.6s long and R is pressed between shots, so the one
        // left standing was one nobody had raised in the last second and a half.
        // A drive's is another matter - the board can be holding a dozen of them
        // in a line behind a tank that is about to be put back where it started,
        // and a trail left hanging is a reset lying about what has happened.
        foreach (ProcKick kick in _kicking)
            kick.Douse();
        foreach (ProcKick drift in _drifting)
            drift.Douse();
        foreach (ToonBlast toon in _tooning)
            toon.Douse();
        foreach (ProcBall ball in _balling)
            ball.Douse();
        // The screens too, and this is the one entry here that is not a burst
        // going out: R puts the board back, and a cell left screened after it
        // would be the reset lying about what is on the board.
        foreach (ProcScreen veil in _screening)
            veil.Douse();
    }

    /// <summary>
    /// Blacken the ground the wood burnt on.
    ///
    /// <b>Rasterised into the map the ground shader already reads world XZ in</b>,
    /// rather than tinting each cell's own face: the face is a prism top built at
    /// board time, so a tint on it would be a mesh rebuild per frame while the ash
    /// was still coming down - and it would be exactly hexagon-shaped.
    ///
    /// <b>Rebuilt whole each frame while anything has burnt.</b> The board is a
    /// couple of hundred cells and the map is some 20k texels, so clearing and
    /// splatting it is cheaper than working out which cell's ash moved - and it
    /// cannot get out of step with the fire, which the cheaper thing could.
    /// </summary>
    private void Soot()
    {
        if (_tops?.MaterialOverride is not ShaderMaterial turf)
            return;
        bool burnt = Blaze is not null && Blaze.Scorched > 0;
        bool scalded = _scalds.Count > 0;
        if ((!burnt && !scalded) || Field.Atlas is null)
        {
            Tell("ash_ink", 0.0f);
            return;
        }
        float circum = Field.Atlas.HexRect.Size.X * 0.5f;
        if (_soot is null)
        {
            float loX = float.MaxValue, hiX = float.MinValue;
            float loZ = float.MaxValue, hiZ = float.MinValue;
            for (int q = 0; q < Field.Columns; q++)
            for (int r = 0; r < Field.Rows; r++)
            {
                if (!Field.InBounds(new Vector2I(q, r)))
                    continue;
                Vector3 top = CellTop(new Vector2I(q, r));
                loX = Mathf.Min(loX, top.X - circum); hiX = Mathf.Max(hiX, top.X + circum);
                loZ = Mathf.Min(loZ, top.Z - circum); hiZ = Mathf.Max(hiZ, top.Z + circum);
            }
            if (loX > hiX)
                return;
            _sootWide = Mathf.Clamp(Mathf.CeilToInt((hiX - loX) / AshGrain), 8, 2048);
            _sootTall = Mathf.Clamp(Mathf.CeilToInt((hiZ - loZ) / AshGrain), 8, 2048);
            _soot = new byte[_sootWide * _sootTall];
            _sootAt = new Vector4(loX, loZ, 1.0f / (hiX - loX), 1.0f / (hiZ - loZ));
        }

        Array.Clear(_soot);
        float perX = _sootWide * _sootAt.Z, perZ = _sootTall * _sootAt.W;
        if (burnt)
            for (int q = 0; q < Field.Columns; q++)
            for (int r = 0; r < Field.Rows; r++)
            {
                var cell = new Vector2I(q, r);
                float much = Blaze!.AshAt(cell);
                if (much > 0.0f)
                    Splat(CellTop(cell), circum, much, perX, perZ);
            }

        // <b>And what a burnt-out hull left, which is the one thing that has ever
        // come back into this map.</b> Same splat as a burnt cell and for the same
        // reason it is allowed: a scorch is only dark and it is a cell wide. See
        // Scald, and see below for what a crater is instead.
        foreach ((Vector2 spot, float lift) in _scalds)
            Splat(Ground(spot, lift), ScaldWide * circum * 2.0f, ScaldInk,
                  perX, perZ, ScaldRim);

        // <b>Craters used to be splatted in here too, and are not any more.</b> The
        // map is 8 units to the texel and a crater is five texels across, and the
        // soil a crater throws out is lighter than the ground rather than darker,
        // which this map cannot say at all - see PitArt. The fire is what is left,
        // which is what this map was built for.

        Image img = Image.CreateFromData(_sootWide, _sootTall, false,
                                         Image.Format.R8, _soot);
        if (_sootMap is null)
            _sootMap = ImageTexture.CreateFromImage(img);
        else
            _sootMap.Update(img);
        Tell("ash", _sootMap);
        Tell("ash_map", _sootAt);
        Tell("ash_ink", AshInk);
    }

    /// <summary>
    /// The noise the flame and the smoke are both broken up with.
    ///
    /// Shared text handed to both shaders rather than copied into each, for
    /// <c>FoamEdge</c>'s reason: a fire and its own smoke torn by two different
    /// fields is two effects that happen to be in the same place, and the copy is
    /// the whole of the failure.
    /// </summary>
    /// <summary>The noise the flame and its smoke are both broken up with,
    /// and now the tank's flame too - it is what <see cref="FlameInk"/>'s
    /// lumping reads, so the two travel together or neither compiles.</summary>
    internal const string EmberNoiseCode = @"
float ember_hash(vec2 p) {
    return fract(sin(dot(p, vec2(41.3, 289.1))) * 43758.5453);
}

float ember_noise(vec2 p) {
    vec2 c = floor(p);
    vec2 f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    return mix(mix(ember_hash(c), ember_hash(c + vec2(1.0, 0.0)), f.x),
               mix(ember_hash(c + vec2(0.0, 1.0)), ember_hash(c + vec2(1.0)), f.x),
               f.y);
}

float ember_fbm(vec2 p) {
    float sum = 0.0;
    float amp = 0.5;
    for (int i = 0; i < 4; i++) {
        sum += amp * ember_noise(p);
        p *= 2.03;
        amp *= 0.5;
    }
    return sum;
}
";

    /// <summary>
    /// The flame on one tree: <c>engine_fire</c>'s model, in a fragment.
    ///
    /// <b>Not the tank's pixels - the tank's construction.</b> Borrowing the
    /// rendered atlas was tried first and measured: worn at a tree's size it covers
    /// 3.3x the area at three quarters the redness, because that flame is
    /// proportioned to be backed by its own column and by dark armour. What carries
    /// over is the way it is built, and every line below is one of the things
    /// <c>engine_fire</c> says makes a fire rather than an orange bar:
    ///
    /// <b>Elements with an age, not analytic tongues.</b> Each one's age advances by
    /// the same step and wraps, and where it is, how big, how solid and what colour
    /// are all functions of that one age - so the loop closes by construction, the
    /// way the plume's does. The three smooth ribbons this replaced had their
    /// variation inside a silhouette that stood still; the eye reads the silhouette.
    ///
    /// <b>Every element has a cross-section.</b> The old licks were the distance to
    /// a vertical line: flat inside, blurred at the sides, a ribbon. These are
    /// ellipses shaded from the middle out, which is what the tank's spheres get
    /// from their own curvature and the single biggest difference in the picture.
    ///
    /// <b>They are stretched along the flow, not round</b> - <c>stretch</c>, and
    /// engine_fire calls it most of what stops a fire reading as a stack of orange
    /// balls.
    ///
    /// <b>They crowd at the seat.</b> Speed along the path is <c>exp(gather * age)</c>,
    /// so elements bunch at the bottom and pull apart toward the tips: the dense
    /// base and the thin licks come out of the path rather than out of a size table.
    /// For a tree the path is a straight line up, so that integral has a closed form
    /// and costs one exp - the tank has to march it because its port points
    /// somewhere else.
    ///
    /// <b>They end by shrinking, never by fading</b> - <c>taper</c>. Forced by the
    /// blend mode rather than chosen: this layer is added, so a low-alpha pixel over
    /// the pale hex is that hex very slightly tinted, whatever colour it was meant
    /// to be. Fading a tip out turns it into steam; shrinking removes it while every
    /// pixel left is still saturated.
    ///
    /// <b>The elements composite over each other; only the layer is added.</b> This
    /// is the one that had to be found rather than read, and it is what the dark rim
    /// is for. In Blender the blobs are surfaces stacked along a ray and they blend
    /// with alpha, so an element in front puts its dark lip over the bright middle of
    /// the one behind - which is the edge that keeps the licks apart. Summing them
    /// instead, which is what the game does to the finished layer, makes the lip
    /// *add* a little dark red: additive light cannot darken anything, so every
    /// overlap brightened and twenty-two elements melted into one bar. Composited in
    /// the shader and handed over premultiplied, the layer still adds exactly once.
    ///
    /// <b>No stop in the ramp is near white</b>: red saturating is not the failure,
    /// green and blue coming up with it is, and a yellow stop anywhere near the mass
    /// guarantees it.
    ///
    /// <b>But the stops are the tank's rendered sprite, not engine_fire's config -
    /// and copying the config was a whole factor of six wrong.</b> Those numbers are
    /// linear, because Blender works in linear and the atlas is that ramp after the
    /// view transform; this shader writes its ALBEDO into an sRGB framebuffer, where
    /// blending happens in the encoded space. Measured, by grey-probing the flame at
    /// 0.25 and 0.50: a grey albedo comes back grey to within 0.05%, so the add is in
    /// the encoded space, and the same numbers read there are far darker in green and
    /// blue than the sprite the eye is asked to match. Own contribution, integrated
    /// in sRGB against a black-flame probe of the same frame: <b>(1, 0.061, 0.0002)
    /// against the sprite's (1, 0.393, 0.110)</b> - a sixth of its green and none of
    /// its blue, which is why the forest burned blood-red where the tank burns amber.
    ///
    /// So the stops carry the sprite's own hue instead, measured off
    /// <c>fire_atlas.png</c>: the view transform flattens the whole tank ramp into
    /// very nearly one colour - <b>(179, 69, 15)</b>, its hue moving only 0.373 to
    /// 0.394 in green across the entire intensity range - so the ramp spans that
    /// colour's own 2nd-to-98th percentile spread, G/R 0.50 down to 0.31 and B/R 0.25
    /// down to 0.045, hot end to cool. Red is untouched stop for stop, so this is a
    /// change of hue and nothing else: A/B over the burning bench moved 135518 px of
    /// green and 31686 of blue and <b>zero pixels of red</b>. On screen at matched
    /// red the core went from (148, 68, 52) to (148, 91, 53) against the burning
    /// tank's own (191, 119, 72) - hue 0.459 to 0.615 against its 0.623.
    ///
    /// <b>What it costs is redness against pale ground, and it stays inside the
    /// tank's own margin.</b> Median redness over the flame body 64.5 to 56.5, where
    /// engine_fire's check calls 30 the floor and the tank's own atlas reports 60.3;
    /// near-white pixels stay at zero. The guard turns out to have been cut for this
    /// all along - it rejects G/R past 0.55 and B/R past 0.30, and the sprite's own
    /// extremes are 0.506 and 0.247 - it had simply never been walked up to.
    ///
    /// <b>engine_fire's config is not the thing to edit here.</b> Its numbers are
    /// right for Blender and they are what produces the sprite these stops are
    /// matched to; moving them would move the target.
    ///
    /// <b>Sizes in tree heights, never in pixels</b>, the way engine_fire quotes
    /// everything in hull lengths - so one config fits four kinds of tree and
    /// whatever a slider does to their size. <c>span</c> and <c>wide</c> are what
    /// carry the quad into those units.
    ///
    /// <b>Time is a uniform, not TIME.</b> The bench fixes its step so two runs can
    /// be compared.
    /// </summary>
    internal const string FlameShader = @"
shader_type spatial;
render_mode unshaded, cull_disabled, blend_add, depth_draw_never;

// Where the foot of the tree sits down the quad, and this tree's own place in the
// flicker - see Pitch. Both per tree, because the mesh is shared per kind.
uniform float foot_v = 0.9;
uniform float seed = 0.0;
uniform float level = 0.0;
uniform float time = 0.0;
// The quad in tree heights - its own height and width, and how far of it hangs
// below the foot. Everything below is then quoted the way engine_fire quotes against
// the hull. Measured rather than derived from a span, because the quad is no longer
// the tree's own rectangle: it reaches under the ground line so the seat and the
// youngest licks are not sliced off by its edge.
uniform float tall = 1.42;
uniform float wideq = 1.12;
// How far above the contact point the fire's own seat sits, and the band it fades
// out over on the way down to that point - both in tree heights, see PyreRoot.
uniform float root = 0.18;
uniform float floor_fade = 0.14;

// How many of each. Fewer than the tank's thirty licks, because there each one is
// geometry the rasteriser skips and here each one is work every fragment does; the
// count is what to cut first if a burning forest costs too much.
uniform int licks = 22;
uniform int seat_blobs = 4;
uniform int embers = 6;

// How much fire there is, in tree heights - the one size knob, and the only
// number here that is a size at all.
uniform float rise = 0.44;
uniform float gather = 0.70;
// The three widths, as ratios to the rise. That is the only form engine_fire's
// numbers port in - it quotes all four against the hull, so on MT seat/rise is
// 0.167, sway/rise 0.207 and ember sway/rise 0.714 - and it is also what keeps
// rise a single knob: written as absolutes they have to be moved by hand with it,
// and a fire scaled in one direction stops being the same fire.
//
// Reading engine_fire's 0.032 and 0.058 as fractions of a tree instead, which is
// what this did first, made the sway a third of what it should be: elements that
// cannot get out of each other's way merge into one bar however many there are.
uniform float seat_ratio = 0.167;
uniform float sway_ratio = 0.207;
uniform float ember_sway_ratio = 0.714;
uniform float taper = 0.26;
uniform float taper_power = 0.90;
uniform float stretch = 2.30;
uniform float vary = 0.40;
uniform float stagger = 0.42;
uniform float alpha = 0.70;
uniform float fade = 0.55;
uniform float born_scatter = 1.05;
uniform float rate = 0.85;

uniform float seat_size = 1.15;
uniform float seat_stretch = 1.15;
uniform float seat_scatter = 0.60;
uniform float seat_flicker = 0.45;
uniform float seat_gain = 0.95;

uniform float ember_reach = 1.15;
uniform float ember_size = 0.26;
uniform float ember_gain = 0.85;
uniform float ember_fade = 1.10;

FLAME_NOISE

FLAME_INK

// Stays with the tree rather than going into FlameInk, and it is the one piece
// that could not be shared: a tree's axis is vertical, so the direction never
// needs renormalising and the integral closes. A tank's vent points back and
// up, so its path is marched in Plumes.Path and its elements arrive already
// placed.
// How far along the path an element of this age has got, as a fraction of the
// reach. The integral of exp(gather * a), normalised on the end point - see the
// summary for why it is in closed form here and marched on the tank.
float flame_climb(float age) {
    float g = max(gather, 1e-3);
    return (exp(g * age) - 1.0) / (exp(g) - 1.0);
}


void fragment() {
    if (level <= 0.0) {
        ALBEDO = vec3(0.0);
        ALPHA = 0.0;
    } else {
        // Tree heights across, and up from the fire's own seat - which stands root
        // above the point the tree touches the ground. Below that point nothing can
        // be drawn at all: under this camera below the contact point and behind the
        // ground are the same test, so a fire reaching down there is not faded by
        // anything, it is sliced off flat by the cell it is standing on.
        vec2 at = vec2((UV.x - 0.5) * wideq, (foot_v - UV.y) * tall - root);
        float t = time * rate + seed;
        float seat = rise * seat_ratio;
        float spread = rise * sway_ratio;
        float ember_spread = rise * ember_sway_ratio;
        vec4 fire = vec4(0.0);

        // The seat: a squat bright body on the ground in every phase. Without it the
        // elements fade in from nothing at age 0 and a flame with no hot seat reads
        // as fog - the muzzle flash's lesson.
        for (int k = 0; k < seat_blobs; k++) {
            float fk = float(k);
            float off = seat * seat_scatter * (2.0 * ember_hash(vec2(fk, 21.0)) - 1.0);
            float lift = seat * 0.35 * ember_hash(vec2(fk, 22.0));
            float size = seat * seat_size * (0.7 + 0.6 * ember_hash(vec2(fk, 23.0)));
            // Flickers in place rather than moving, on a whole number of cycles per
            // lap so it cannot jump at the wrap.
            float turns = 1.0 + mod(fk, 3.0);
            float wave = 0.5 + 0.5 * cos(6.283185 * (turns * t
                                                     + ember_hash(vec2(fk, 24.0))));
            float gain = 1.0 - seat_flicker + seat_flicker * wave;
            fire = flame_over(fire,
                              flame_blob(at, vec2(off, lift), size,
                                         size * seat_stretch, vec2(0.0, 1.0), fk + 3.0, 0.0,
                                         seat_gain * gain * alpha));
        }

        // The licks.
        for (int k = 0; k < licks; k++) {
            float fk = float(k);
            float n = float(licks);
            float age = fract((fk + 0.5) / n
                              + stagger * (2.0 * ember_hash(vec2(fk, 11.0)) - 1.0) / n
                              + t);
            float life = flame_life(age, fade);
            float up = rise * flame_climb(age);
            float r = seat * (1.0 - (1.0 - taper) * pow(age, taper_power))
                      * (1.0 - vary + 2.0 * vary * ember_hash(vec2(fk, 12.0)));
            // Sway, and born off the middle of the trunk: every lick leaving through
            // the exact centre makes the base of the column a needle.
            float sway = spread * pow(age, 0.8) * ember_hash(vec2(fk, 13.0))
                         * cos(6.283185 * ember_hash(vec2(fk, 14.0)));
            float born = seat * born_scatter
                         * (2.0 * ember_hash(vec2(fk, 15.0)) - 1.0);
            fire = flame_over(fire,
                              flame_blob(at, vec2(born + sway, up), r, r * stretch, vec2(0.0, 1.0),
                                         fk + 20.0, age, alpha * life));
        }

        // The embers: small, red, and further out than the flame. Named in practice
        // as one of the things separating a mediocre fire from a good one, and here
        // they are the same queue with a longer reach.
        for (int k = 0; k < embers; k++) {
            float fk = float(k);
            float n = float(embers);
            float age = fract((fk + 0.5) / n
                              + stagger * (2.0 * ember_hash(vec2(fk, 31.0)) - 1.0) / n
                              + t);
            float life = flame_life(age, ember_fade);
            float up = rise * ember_reach * flame_climb(age);
            float r = seat * ember_size * (0.5 + ember_hash(vec2(fk, 32.0)));
            float sway = ember_spread * pow(age, 0.9)
                         * ember_hash(vec2(fk, 33.0))
                         * cos(6.283185 * ember_hash(vec2(fk, 34.0)));
            fire = flame_over(fire,
                              flame_spark(at, vec2(sway, up), r, r * 1.6, vec2(0.0, 1.0), fk + 40.0,
                                          ember_colour, ember_gain * life));
        }

        // Out before the ground line rather than at it, and the band is above the
        // line because there is no below - see the note on at. The seat is lifted
        // clear of the band so the fire's bright base is at full strength.
        float footing = smoothstep(0.0, max(floor_fade, 1e-4), at.y + root);
        // Already premultiplied by the compositing above, so the alpha is spent and
        // a solid one is handed over: blend_add lays down colour times alpha, which
        // adds this layer exactly once - the way the game adds the tank's.
        ALBEDO = fire.rgb * (level * footing);
        ALPHA = 1.0;
    }
}
";

    /// <summary>
    /// The smoke over one burning tree: a queue of puffs born behind the flame,
    /// rising, spreading and thinning out.
    ///
    /// <b>Puffs and not a veil, and that is the whole of this shader.</b> What it
    /// replaced was a soft-edged band with noise torn out of it, which has no
    /// scale in it at all: nothing in the picture says whether the column is ten
    /// metres or a hundred, and eight of them on one cell composite into a flat
    /// slab that greys out the wood standing behind. A queue of bodies has a scale
    /// because each body has an edge - the flame's own finding, where licks that
    /// were flat inside and blurred at the sides read as ribbons until they became
    /// ellipses.
    ///
    /// <b>It is <c>engine_fire.SMOKE</c> - the tank's own column - ported the way
    /// the flame ported the fire.</b> The numbers are <see cref="ProcSmoke"/>'s;
    /// there they are quoted against the hull's length, here against the column's
    /// own reach, because that is the only form they port in and reading them as
    /// fractions of anything else is the mistake both ports have already paid for
    /// once. So the board has one smoke: a tank's column is these puffs marched
    /// along a vent's path in 2D, a tree's is the same puffs up a vertical axis in
    /// a billboard.
    ///
    /// <b>The one number that tells a column from a flame is the sign of the
    /// climb.</b> A flame gathers - elements bunching toward the tip - and smoke
    /// slows, which is <c>ProcSmoke.Slowing</c> handed to the same path as
    /// <c>-1/slowing</c>. Same closed form as <c>flame_climb</c>, for a tree's
    /// reason: the axis is vertical, so the integral closes rather than being
    /// marched.
    ///
    /// <b>Ordinary alpha and dark, and that is what makes the flame work.</b> The
    /// fire adds light, so what is behind it decides how much of it arrives; on
    /// pale ground almost none does. Black smoke under the flame is what the tank's
    /// burning column is for, in those words, and drawing it the other way round
    /// would wash out the fire it is supposed to hold up. So the queue is seated
    /// inside the flame rather than over the crown: above the roots, which would
    /// read as a smoke machine, and low enough that the fire has something dark to
    /// stand on.
    ///
    /// <b>Composited in index order and not sorted</b>, unlike the tank's, and
    /// that is affordable for one reason: every puff here is the same ink give or
    /// take <c>tone_vary</c>, so which of two overlapping bodies is in front
    /// cannot be seen. The tank sorts because its puffs are cut against the hull's
    /// depth map, and there the order is the depth buffer it does not have.
    /// </summary>
    private const string SmokeShader = @"
shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_never;

// Where the foot of the tree sits down the quad, this tree's own place in the
// cycle, and the quad in tree heights - the flame's arrangement, and it has to be
// the flame's or every puff comes out an ellipse: the UV is not square.
uniform float foot_v = 0.9;
uniform float seed = 0.0;
uniform float level = 0.0;
uniform float time = 0.0;
uniform float tall = 1.85;
uniform float wideq = 1.85;

// How many bodies the column is. Fewer than the tank's twenty-two, because there
// each puff is a quad the rasteriser skips and here it is work every fragment
// does - the flame's note, and this is the first number to cut if a burning
// forest costs too much.
uniform int puffs = 22;

// How far the column reaches and where it is born, both in tree heights. The
// reach is the one size knob; everything below is a ratio to it.
uniform float rise = 1.15;
uniform float seat_at = 0.30;

// engine_fire.SMOKE, quoted against that reach. The tank quotes them against its
// hull - PuffStart 0.40 of a port radius, PuffGrow 0.048 and Spread 0.103 of a
// hull, against Rise 0.45 of one - so these are those numbers divided through by
// the reach they are measured with. The port, rather than a new set of dials.
uniform float puff_seat = 0.107;
uniform float puff_grow = 0.107;
uniform float puff_power = 0.65;
uniform float spread_ratio = 0.229;
uniform float born_scatter = 0.70;
uniform float vary = 0.45;
uniform float stagger = 0.35;
uniform float wobble = 0.22;
uniform float alpha = 0.80;
uniform float onset = 0.10;
uniform float fade = 1.25;
uniform float rim_falloff = 1.15;
uniform float opacity = 0.80;
uniform float tone_vary = 0.08;
// Negative, and it is the one number that tells this from a flame: smoke slows
// (ProcSmoke.Slowing 1.10, handed to the path as -1/slowing) where a flame
// gathers.
uniform float gather = -0.909;
// One lap of the queue, and slower than the flame's 0.85: a column drifts where a
// fire flickers, which is the borrowed pair's two rates.
uniform float rate = 0.30;
// A lean with height, on top of the wander - one wind over the board, so a stand
// of columns agrees instead of eight of them standing plumb.
uniform float lean = 0.13;
// Soot, not grey: what comes off a burning tree is dark and slightly warm, and a
// neutral grey column on this ground reads as fog.
uniform vec3 ink = vec3(0.24, 0.22, 0.20);

FLAME_NOISE

// Solidity over one puff's life: zero at both ends, or a body appears and
// vanishes with a step. ProcSmoke's own expression, which is flame_life's.
float smoke_life(float age) {
    return (1.0 - exp(-age / max(onset, 1e-4)))
           * pow(max(1.0 - age, 0.0), fade);
}

// How far up the reach a puff of this age has got: the integral of exp(g*a)
// normalised on its end point - flame_climb, with g negative.
float smoke_climb(float age) {
    float g = gather;
    if (abs(g) < 1e-3) { return age; }
    return (exp(g * age) - 1.0) / (exp(g) - 1.0);
}

// One puff: an ellipse soft in the middle and thin at its rim, the rim itself
// roughened so it is not a circle. Premultiplied, like the flame's elements.
vec4 smoke_puff(vec2 at, vec2 centre, float r, float gain, float tone,
                float lump) {
    vec2 d = (at - centre) / max(r, 1e-4);
    float q = dot(d, d);
    if (q >= 2.25) { return vec4(0.0); }
    float wob = 1.0 + wobble * (2.0 * ember_fbm(d * 1.7 + vec2(lump, lump)) - 1.0);
    q = q / max(wob * wob, 1e-4);
    if (q >= 1.0) { return vec4(0.0); }
    float facing = sqrt(1.0 - q);
    float a = clamp(pow(facing, rim_falloff) * gain * opacity, 0.0, 1.0);
    return vec4(ink * tone * a, a);
}

vec4 smoke_over(vec4 acc, vec4 e) {
    return vec4(e.rgb + (1.0 - e.a) * acc.rgb, e.a + (1.0 - e.a) * acc.a);
}

void fragment() {
    if (level <= 0.0) {
        ALBEDO = vec3(0.0);
        ALPHA = 0.0;
    } else {
        vec2 at = vec2((UV.x - 0.5) * wideq, (foot_v - UV.y) * tall);
        float t = time * rate + seed;
        vec4 col = vec4(0.0);
        for (int k = 0; k < puffs; k++) {
            float fk = float(k);
            float n = float(puffs);
            float age = fract((fk + 0.5) / n
                              + stagger * (2.0 * ember_hash(vec2(fk, 51.0)) - 1.0) / n
                              + t);
            float life = smoke_life(age);
            float up = seat_at + rise * smoke_climb(age);
            float r = rise * (puff_seat + puff_grow * pow(age, puff_power))
                      * (1.0 - vary + 2.0 * vary * ember_hash(vec2(fk, 52.0)));
            // Out of the middle and on across: the wander is the tank's Spread on
            // age^0.7, and the lean is one wind, growing faster than it.
            float wander = rise * spread_ratio * pow(age, 0.7)
                           * ember_hash(vec2(fk, 53.0))
                           * cos(6.283185 * ember_hash(vec2(fk, 54.0)));
            float born = r * born_scatter
                         * (2.0 * ember_hash(vec2(fk, 55.0)) - 1.0);
            float side = lean * rise * pow(age, 1.2);
            float tone = 1.0 + tone_vary
                               * (2.0 * ember_hash(vec2(fk, 56.0)) - 1.0);
            col = smoke_over(col,
                             smoke_puff(at, vec2(born + wander + side, up), r,
                                        alpha * life, tone, fk * 7.0 + seed));
        }
        // Premultiplied out of the compositing, so the colour is divided back out
        // and the coverage handed over as it stands.
        ALBEDO = col.rgb / max(col.a, 0.0001);
        ALPHA = clamp(col.a * level, 0.0, 1.0);
    }
}
";

    /// <summary>
    /// What one element of a flame is: the ramp, the life, the ellipse,
    /// the section across it and the dark lip - and the numbers those read.
    ///
    /// <b>Shared with the tank's own fire, and that is the point rather
    /// than the saving.</b> The ramp here is the tank sprite's measured
    /// hue, fitted to <c>fire_atlas</c> because this lands in an sRGB
    /// framebuffer where <c>engine_fire</c>'s linear numbers are six times
    /// too dark in green and blue. A second copy on the tank would be a
    /// second answer to what colour the fire is, and the two would part
    /// where nobody compares them - which is the whole complaint the
    /// tank's built flame exists to answer.
    ///
    /// <see cref="FoamEdge"/>'s arrangement: handed to both shaders as
    /// a format argument, and the check requires it in both
    /// <em>compiled</em> sources, because a copy still names every
    /// function it defines.
    /// </summary>
    internal const string FlameInk = @"
uniform float wobble = 0.28;
uniform float gradient = 0.20;
uniform float onset = 0.07;
// The element's own cross-section, and both halves of it are engine_fire's
// material rather than a guess at one.
//
// <b>The exponent is on the facing, not on the radius.</b> There alpha goes as
// Layer Weight's Facing to this power, and for a sphere facing is sqrt(1 - r^2);
// writing pow(1 - r^2, 1.15) instead - which is what this did - is facing^2.3, so
// every element came out half as soft and a size smaller than it was asked for.
//
// <b>The dark lip is load-bearing.</b> engine_fire keeps one for exactly the
// failure seen here: without it the licks melt into each other instead of keeping
// a shape. The plume wants the opposite and has its rim nearly the body's colour,
// because there shapes are the problem.
//
// Its hue moved with the ramp's and for the same reason, but less: it stays redder
// and darker than the body, which is the whole of its job.
uniform float rim_falloff = 1.15;
uniform float rim_reach = 0.32;
uniform vec3 rim_colour = vec3(0.35, 0.088, 0.018);
uniform float opacity = 0.72;
// The section across one element, in three bands: a deep core, a bright shoulder,
// and the dark lip at the very edge.
//
// <b>Hue alone does not read, and that was measured.</b> Shifting the ramp across
// the element makes the shoulder yellower and leaves it *darker*, because both the
// lip and the alpha fall off monotonically from the middle - so the eye sees one
// dimming body, not a light edge. The shoulder has to be brighter as well as
// yellower, which is what shell_gain buys, and the lip has to be pulled in close to
// the rim to leave room for it - rim_reach 0.32 against the 0.60 it was.
//
// <b>The lip stays, and it still has to do its job.</b> It is what keeps the licks
// from melting into each other, measured; a bright shoulder that reached the edge
// would light its neighbour's and put the bar back.
//
// Where the shoulder sits and how wide it is, in facing: 1 is dead centre, 0 the rim.
uniform float shell_at = 0.55;
uniform float shell_gain = 1.35;
uniform float shell_width = 0.34;
// And how far the middle of an element and its shoulders sit apart on the ramp: plus
// this at dead centre, minus it at the rim.
//
// <b>A shift along the ramp, not a second set of colours.</b> The ramp already runs
// from a bright orange at 0 to a deep red at 1, so a cross section is one number on
// the same curve - which keeps the no-stop-near-white rule true by construction
// and leaves one place where the flame's colour is written.
//
// <b>It does not replace the dark lip, it sits inside it.</b> The lip is what keeps
// the licks from melting into each other and that was measured; so the section
// reads middle deep, shoulders bright, and then the lip at the very edge. Three
// bands out of two numbers.
uniform float core_hue = 0.26;
// An ember's own colour rather than a place on the ramp, because they are what
// is left rather than what is burning - and warm rather than the yellow a real
// spark is, for the ramp's reason twice over: an ember is a few pixels at low
// alpha, and low-alpha yellow added to a pale field is a grey speck. They read
// as drizzle before this was pulled toward red.
//
// Here rather than in either shader because it is one of the colours the fire
// is, and the tank's flame asks for it by name.
uniform vec3 ember_colour = vec3(1.00, 0.38, 0.08);
// The tank sprite's own hue, hot end to cool, strength in w. NOT engine_fire's
// config numbers: those are linear and this lands in an sRGB framebuffer - see the
// note on FlameShader for the measurement and the factor of six.
vec4 flame_ramp(float t) {
    vec3 c;
    float s;
    if (t < 0.08) {
        float f = t / 0.08;
        c = mix(vec3(1.00, 0.50, 0.25), vec3(1.00, 0.455, 0.19), f);
        s = mix(1.00, 0.95, f);
    } else if (t < 0.22) {
        float f = (t - 0.08) / 0.14;
        c = mix(vec3(1.00, 0.455, 0.19), vec3(1.00, 0.40, 0.10), f);
        s = mix(0.95, 0.92, f);
    } else if (t < 0.60) {
        float f = (t - 0.22) / 0.38;
        c = mix(vec3(1.00, 0.40, 0.10), vec3(0.85, 0.298, 0.051), f);
        s = mix(0.92, 0.66, f);
    } else {
        float f = (t - 0.60) / 0.40;
        c = mix(vec3(0.85, 0.298, 0.051), vec3(0.55, 0.171, 0.025), f);
        s = mix(0.66, 0.26, f);
    }
    return vec4(c, s);
}

// Up from nothing at age 0, back to nothing at age 1 - both ends exactly, or the
// lap shows a seam.
float flame_life(float age, float ends) {
    return (1.0 - exp(-age / max(onset, 1e-4)))
           * pow(max(1.0 - age, 0.0), ends);
}

// One element, premultiplied colour in rgb and its coverage in a. `at` is the
// fragment in tree heights from the foot, `centre` the element, `half_w` its
// half-width, `along` its half-length. `body` is its colour in the middle; the rim
// takes it toward rim_colour.
//
// Ellipse rather than a distance to a line, and that is the single biggest thing
// in the picture: the licks this replaced were flat inside and blurred at the
// sides, which is a ribbon. A sphere is bright in the middle and dark and thin at
// its edge, and that is what makes it read as a body.
// How far into an element this fragment is: 1 dead centre, 0 at its rim, and 0
// outside it. `down` comes back as +1 at the leading end and -1 at the trailing one.
//
// <b>`flow` is which way the element lies</b>, as a unit screen vector. A tree's
// fire climbs straight up the quad and passes vec2(0, 1), which is what this did
// before there was an argument and is bit-identical to it. A tank's leaves the
// vent at whatever the vent points at, and the stretch has to lie along that or a
// lick at the seat is a horizontal ellipse across a diagonal flow.
// <b>How far into an element this fragment is, squared</b>: 0 dead centre, 1 at
// its wobbled rim, and past 1 outside it. This is the whole of *where* a fragment
// sits inside an element - the ellipse, the flow frame it lies in and the noise
// that keeps its rim from being a perfect one - and nothing about what it looks
// like once it is in there.
//
// <b>Split out because a sphere and a cloud want the same frame and different
// profiles.</b> A lick of flame is a body: bright in the middle, dark and thin at
// its edge, and its section is <c>sqrt(1 - q)</c> exactly - see flame_facing,
// which is that and is unchanged. Earth in the air is not a body at all; measured
// off the CC0 pack's own painted puff, its alpha follows <c>(1 - q)^2.57</c> and
// never reaches full anywhere in the sprite - so the burst's dust puts that
// profile on this frame instead. Two profiles, one statement of the frame; the
// alternative was a second copy of the ellipse arithmetic, which is the thing
// this kit exists to prevent.
float flame_reach(vec2 at, vec2 centre, float half_w, float along, vec2 flow,
                  float lump_seed, out float down) {
    vec2 e = at - centre;
    vec2 d = vec2(dot(e, vec2(flow.y, -flow.x)) / max(half_w, 1e-5),
                  dot(e, flow) / max(along, 1e-5));
    down = clamp(d.y, -1.0, 1.0);
    float q = dot(d, d);
    // Cheap bail before the noise: most fragments are outside most elements.
    if (q > 2.25) {
        return 2.25;
    }
    float lump = 1.0 + wobble * (ember_noise(vec2(d.x * 1.6 + lump_seed * 7.3,
                                                  d.y * 1.6 + lump_seed * 3.1))
                                 - 0.5);
    return q / max(lump * lump, 1e-3);
}

float flame_facing(vec2 at, vec2 centre, float half_w, float along, vec2 flow,
                   float lump_seed, out float down) {
    float q = flame_reach(at, centre, half_w, along, flow, lump_seed, down);
    return q >= 1.0 ? 0.0 : sqrt(1.0 - q);
}

// One element, premultiplied colour in rgb and its coverage in a. `age` is where it
// sits on the ramp; the length of it and the section across it both shift that -
// read once per element the ramp is a flat colour per blob, which is what a stack of
// coloured balls looks like.
vec4 flame_blob(vec2 at, vec2 centre, float half_w, float along, vec2 flow,
                          float lump_seed,
                float age, float gain) {
    float down;
    float facing = flame_facing(at, centre, half_w, along, flow, lump_seed, down);
    if (facing <= 0.0) {
        return vec4(0.0);
    }
    float across = clamp((facing - 0.5) * 2.0, -1.0, 1.0);
    vec4 c = flame_ramp(clamp(age + gradient * (down + 1.0) * 0.5
                              + core_hue * across, 0.0, 1.0));
    float shell = 1.0 + ((shell_gain - 1.0)
                         * max(0.0, 1.0 - (abs(facing - shell_at)
                                           / max(shell_width, 1e-3))));
    vec3 col = mix(rim_colour, c.rgb, pow(facing, rim_reach));
    float a = clamp(pow(facing, rim_falloff) * c.w * shell * gain * opacity,
                    0.0, 1.0);
    return vec4(col * a, a);
}

// An ember: the same body, one flat colour. engine_fire gives them their own rather
// than a place on the ramp, because they are what is left rather than what is
// burning - and the lip still applies, so they keep an edge.
vec4 flame_spark(vec2 at, vec2 centre, float half_w, float along, vec2 flow,
                           float lump_seed,
                 vec3 body, float gain) {
    float down;
    float facing = flame_facing(at, centre, half_w, along, flow, lump_seed, down);
    if (facing <= 0.0) {
        return vec4(0.0);
    }
    vec3 col = mix(rim_colour, body, pow(facing, rim_reach));
    float a = clamp(pow(facing, rim_falloff) * gain * opacity, 0.0, 1.0);
    return vec4(col * a, a);
}

// One element over what is already there.
vec4 flame_over(vec4 acc, vec4 e) {
    return vec4(e.rgb + (1.0 - e.a) * acc.rgb, e.a + (1.0 - e.a) * acc.a);
}
";

    /// <summary>The forest's flame as it is compiled, so the check can ask
    /// whether it and the tank's are one text. A copy still names every
    /// function it defines, so nothing shorter than this would notice.
    /// </summary>
    internal static readonly string BlazingCode =
        FlameShader.Replace("FLAME_NOISE", EmberNoiseCode)
                   .Replace("FLAME_INK", FlameInk);

    private static readonly Shader Blazing = new() { Code = BlazingCode };

    /// <summary>The forest's column as it is compiled, so the check can ask
    /// whether its numbers are still the tank's - the flame's arrangement, and
    /// for the flame's reason: a port that is only a port in the comments comes
    /// apart the first time either side is tuned.</summary>
    internal static readonly string SmokingCode =
        SmokeShader.Replace("FLAME_NOISE", EmberNoiseCode);

    private static readonly Shader Smoking = new() { Code = SmokingCode };

    /// <summary>
    /// A borrowed layer on a tree: one column of the strip, picked by phase.
    ///
    /// <b>The blend mode is substituted rather than parameterised</b>, because it is
    /// not a parameter: the fire adds light and the column takes it away, and in
    /// Godot the mode lives on the material. Two shaders off one text, the way the
    /// flame and the smoke already share their noise.
    ///
    /// <b>The column is clamped half a texel in from its own pad.</b> The frames sit
    /// side by side with a transparent pixel between them, so linear filtering at
    /// the seam reaches the gutter rather than the next phase - which would be one
    /// phase of the fire faintly printed on the next.
    /// </summary>
    private const string LoanShader = @"
shader_type spatial;
render_mode unshaded, cull_disabled, BLEND_MODE, depth_draw_never;

uniform sampler2D art : source_color, filter_linear, hint_default_transparent;
// Where this phase's column starts in the strip and how wide it is, both as a
// fraction of the whole - so the shader never learns how many phases there are.
uniform float stride = 1.0;
uniform float pad = 0.0;
uniform float span = 1.0;
uniform float phase = 0.0;
uniform float level = 0.0;

void fragment() {
    if (level <= 0.0) {
        ALBEDO = vec3(0.0);
        ALPHA = 0.0;
    } else {
        float u = phase * stride + pad + clamp(UV.x, 0.0, 1.0) * span;
        vec4 c = texture(art, vec2(u, UV.y));
        ALBEDO = c.rgb;
        ALPHA = c.a * level;
    }
}
";

    private static readonly Shader Lending =
        new() { Code = LoanShader.Replace("BLEND_MODE", "blend_add") };

    private static readonly Shader Fuming =
        new() { Code = LoanShader.Replace("BLEND_MODE, ", string.Empty) };


    /// <summary>
    /// The quad a tree of this kind stands on: the union of its living picture
    /// and its burnt one.
    ///
    /// <b>One quad for every state, which is what the crossfade needed and then
    /// simplified away.</b> The two pictures have their own sizes and their own
    /// feet - Tree_1 is 947x1011 living against 908x989 burnt, and Tree_2 is
    /// <i>wider</i> burnt than living - so neither rectangle contains the other,
    /// and a handover drawn inside the incoming one would cut the outgoing crown
    /// off square. The union holds both, so the mesh does not change when a tree
    /// burns at all and the state lives entirely in the material.
    ///
    /// The margin it adds costs nothing: it is transparent in both pictures, the
    /// same reason the rows below a tree's foot cost nothing.
    /// </summary>
    private ArrayMesh Shape(PropNode tree)
    {
        (int, bool) kind = (tree.Species, tree.Mirrored);
        if (_stems.TryGetValue(kind, out ArrayMesh? shape))
            return shape;
        (Vector2 foot, Vector2 size, _, _) = Union(tree);
        _stems[kind] = shape = Stem(foot * tree.Pixels, size * tree.Pixels,
                                    RiseFactor);
        return shape;
    }

    /// <summary>
    /// The union quad's foot and size, and the two maps that put each picture
    /// back where it belongs inside it.
    ///
    /// Worked in foot-relative image px with y up from the foot, which is the
    /// space <see cref="Stem"/> reads: a picture with foot F and size S covers x
    /// from -F.x to S.x-F.x, and y from F.y-S.y to F.y. A map is an offset and a
    /// scale onto that picture's own UV, so it carries no pixel scale and is the
    /// same for every tree of the kind - which is why it is set once and not per
    /// frame.
    ///
    /// A kind with no burnt picture unions its own rectangle with itself, so it
    /// comes out with exactly the quad and exactly the map it had before any of
    /// this: the identity, to the bit.
    /// </summary>
    public static (Vector2 Foot, Vector2 Size, Godot.Vector4 Live,
                   Godot.Vector4 Dead) Union(PropNode tree)
    {
        Vector2 f = tree.Foot, z = tree.Size;
        Vector2 b = tree.BurntArt is null ? f : tree.BurntFoot;
        Vector2 t = tree.BurntArt is null ? z : tree.BurntSize;
        float left = Mathf.Min(-f.X, -b.X);
        float right = Mathf.Max(z.X - f.X, t.X - b.X);
        float top = Mathf.Max(f.Y, b.Y);
        float low = Mathf.Min(f.Y - z.Y, b.Y - t.Y);
        var foot = new Vector2(-left, top);
        var size = new Vector2(right - left, top - low);
        return (foot, size, Sited(f, z, left, top, size),
                Sited(b, t, left, top, size));
    }

    /// <summary>One picture's rectangle as an offset and a scale onto its own
    /// UV, given where the union starts and how big it is.</summary>
    private static Godot.Vector4 Sited(Vector2 foot, Vector2 size, float left,
                                       float top, Vector2 span) =>
        new((left + foot.X) / size.X, (foot.Y - top) / size.Y,
            span.X / size.X, span.Y / size.Y);

    /// <summary>How much bigger than the tree the flame's quad and the smoke's are,
    /// about the foot. The flame licks past the crown, the column stands well over
    /// it; both are shaped inside the quad by the shader, so these only have to be
    /// roomy enough not to clip - which is the one way they can fail visibly.
    /// </summary>
    private const float PyreSpan = 1.20f;
    private const float PlumeSpan = 1.85f;

    /// <summary>
    /// The flame's quad, as multiples of its own rise: how far it reaches over the
    /// foot, how far to either side of the trunk, how far under the ground line, and
    /// how much of that last it spends fading out.
    ///
    /// <b>Measured against the fire, not against the tree, and that is the whole of
    /// it.</b> The quad used to be the tree's own rectangle scaled by a span, which
    /// holds only while the fire is smaller than the tree: asked for a taller flame
    /// it printed its own rectangle on the board, because the embers drift 0.71 of
    /// the rise sideways and 1.15 of it up. Same lesson as the tank's smoke frame -
    /// the effect that buys room has to be the one the room is measured on.
    ///
    /// Up and flank come out of the shader's own numbers and nothing else: up is
    /// <c>ember_reach</c> plus an ember's half-length, flank is
    /// <c>ember_sway_ratio</c> plus its half-width - the licks want less, 0.62.
    /// There is a check that reads those uniforms back and asserts these still cover
    /// them, because two copies of one bound is exactly how a frame starts clipping
    /// quietly.
    /// </summary>
    private const float PyreLift = 1.30f;
    private const float PyreFlank = 0.90f;

    /// <summary>
    /// How far above the point the tree touches the ground the fire's seat sits, and
    /// the band it fades out over on the way back down to that point - both as
    /// multiples of the rise.
    ///
    /// <b>The flame was being sliced off flat along the hex, and no amount of room
    /// under the foot could ever have helped.</b> Under this camera "below the
    /// contact point" and "behind the ground" are the same test - the depth a
    /// fragment loses is <c>y/sin(e)</c> and nothing else in the expression
    /// survives, which is the finding that bent the tank's own quad flat along the
    /// ground, and moving the quad about cannot help because shifting it in z moves
    /// the ground it covers by the same amount. So the room the quad used to hang
    /// below the foot, and the fade spent inside it, bought nothing whatsoever:
    /// every one of those fragments was behind the cell the tree stands on before
    /// the fade was ever asked. Measured on the board - the flame's bottom edge sat
    /// on one row for 26 columns out of 51, and on a second row for 9 more, those
    /// being the columns hanging past the hex where there is no ground to be behind.
    ///
    /// <b>So the fire is lifted instead, and the fade band moves above the
    /// line.</b> The seat stands clear of the band and burns at full strength; what
    /// still hangs below it - the widest seat blob reaches 0.29 of a rise down, the
    /// youngest lick nearly half of one - fades out and is zero by the time it
    /// reaches the ground. Nothing is cut because nothing is left to cut.
    ///
    /// <b>What that gives up is the fire spreading on the ground at its base</b>,
    /// which reads well and was never once drawn. Getting it needs the tank's
    /// answer - the quad bent flat along the ground below the contact line - and
    /// that is its own piece of work rather than a constant.
    /// </summary>
    private const float PyreRoot = 0.18f;
    private const float PyreFade = 0.14f;

    /// <summary>
    /// How much fire there is, in tree heights - the one size knob, and the only
    /// number in the flame that is a size at all. Everything else about its shape is
    /// a ratio to this, so moving it scales the fire rather than stretching it.
    /// </summary>
    public const float FlameRiseDefault = 0.80f;

    public float FlameRise { get; set; } = FlameRiseDefault;

    /// <summary>How far over the foot a flame of this rise reaches, for the checks.
    /// </summary>
    public static float PyreReachAt(float rise) => rise * PyreLift;

    /// <summary>The four bounds, for the check that reads the shader back.</summary>
    public static (float Lift, float Flank, float Root, float Fade) PyreBounds =>
        (PyreLift, PyreFlank, PyreRoot, PyreFade);

    /// <summary>The same two, for the checks: a claim about which of them is the
    /// roomier is worth asserting, and a private constant cannot be asked.
    /// </summary>
    public static float PyreReach => PyreReachAt(FlameRiseDefault);
    public static float PlumeReach => PlumeSpan;

    /// <summary>
    /// Dress the trees in the tank's own rendered fire instead of the procedural
    /// one - the probe, not a destination.
    ///
    /// <b>It costs no render at all, and that is the whole of why it is worth
    /// having:</b> <c>fire_atlas</c> and <c>burn_atlas</c> are already on disk, so
    /// this answers the question that has to be asked before anything is built -
    /// whether the tank's fire is even what a tree wants. The tank's is a jet out
    /// of a deck port, seated and columnar; a tree burns as a crown. If those pull
    /// apart, they pull apart here, for the price of a flag.
    ///
    /// <b>What it cannot answer is proportion.</b> The frames are sized against the
    /// tank's own tile, so wearing them at a tree's size is a resample either way;
    /// they are scaled to the height the procedural flame reaches, so the A/B is
    /// about shape rather than size.
    /// </summary>
    public bool TankFire { get; set; }

    /// <summary>How tall the borrowed flame is made, as a fraction of the tree's
    /// own drawn height - the procedural <c>reach</c>, so that the two are the same
    /// size and the comparison is about everything else. The column comes with it
    /// on the same factor, never its own: the flame and the thing that holds it up
    /// are one effect, and scaling them apart is the one way to lose the reason the
    /// column exists.</summary>
    private const float LoanReach = 0.62f;

    /// <summary>
    /// One borrowed layer, laid out as a strip of its phases.
    ///
    /// <b>A strip rather than the atlas's own rects, because the frames are
    /// trimmed</b> and each one sits at its own offset in the tile. Pasting them
    /// back into a tile and cropping the union of the twelve turns twelve
    /// rectangles into one: the quad is that crop, and the shader only has to pick
    /// a column. The same trick the burnt-art swap uses, for the same reason.
    ///
    /// <b>A transparent pad between the columns</b>, so linear filtering cannot
    /// reach into the next phase - <c>atlas_pack</c>'s gutter, and its argument
    /// word for word.
    /// </summary>
    private sealed record Borrowed(ImageTexture Strip, int Phases, Vector2 Cell,
                                  Vector2 Base, float Body, float Stride, float Pad,
                                  float Width);

    /// <summary>Where a borrowed layer stops being flame and starts being embers:
    /// the alpha its dense body is measured at.
    ///
    /// <b>The crop is not the fire.</b> The union of twelve phases is 63x96 on MT
    /// and the part of it carrying real density is 58x73 - three quarters. Sizing
    /// and seating by the crop, which is what this did first, therefore drew the
    /// body a quarter small and stood it on its own sparks: measured, the seat came
    /// out ten pixels above the bottom row of the crop.</summary>
    private const float LoanBody = 0.43f;

    private readonly Dictionary<string, (byte[] Data, int Width)> _sheets = new();

    /// <summary>
    /// The heading both borrowed layers are taken at, and it has to be measured.
    ///
    /// <b>The tank's smoke leans</b>, because the port on MT looks back and up at
    /// 71.8 degrees, so the column stands upright on some headings and comes at the
    /// camera foreshortened on others - 116px over the anchor at one end of the
    /// carousel and 40 at the other. A tree is a billboard with no heading of its
    /// own, so a leaning column on it is wind that is not blowing.
    ///
    /// <b>One heading for both layers, not the best of each.</b> The flame and the
    /// column are one effect - the column is what the additive flame is composited
    /// on - so picking them apart would stand the smoke beside the fire.
    ///
    /// Width is the wrong measure and was the first one tried: the widest heading
    /// of the column is the one pointed at the camera, which is the worst of the
    /// twenty-four.
    /// </summary>
    private double Standing(AtlasSet set)
    {
        if (_upright is double had)
            return had;
        IReadOnlyList<int> facings = set.RenderedFacings();
        double best = facings.Count > 0 ? facings[0] : 0.0;
        float least = float.MaxValue;
        foreach (int facing in facings)
        {
            float lean = Lean(set, AtlasSet.FireName, facing)
                         + Lean(set, AtlasSet.BurnName, facing);
            if (lean < least)
            {
                least = lean;
                best = facing;
            }
        }
        _upright = best;
        GD.Print($"wood: tank fire borrowed at heading {best:F0} deg, lean {least:F1}px");
        return best;
    }

    /// <summary>How far a layer's mass at this heading drifts sideways from its own
    /// foot to its own tip, over all its phases folded together. Zero is upright.
    /// Read off the atlas bytes rather than through GetPixel, because this walks
    /// every phase of every heading of two layers to make one choice.</summary>
    private float Lean(AtlasSet set, string layer, double facing)
    {
        if (!set.Has(layer))
            return 0.0f;
        if (!_sheets.TryGetValue(layer, out (byte[] Data, int Width) sheet))
        {
            Image whole = set.Texture(layer).GetImage();
            if (whole.GetFormat() != Image.Format.Rgba8)
                whole.Convert(Image.Format.Rgba8);
            _sheets[layer] = sheet = (whole.GetData(), whole.GetWidth());
        }
        int phases = set.PhasesOf(layer);
        int top = int.MaxValue, low = int.MinValue;
        for (int pass = 0; pass < 2; pass++)
        {
            double tipX = 0.0, tipN = 0.0, footX = 0.0, footN = 0.0;
            int band = pass == 0 ? 0 : Math.Max(3, (low - top + 1) / 8);
            for (int phase = 0; phase < phases; phase++)
            {
                int index = set.EffectFrame(layer, phase, facing);
                Rect2 region = set.Region(layer, index);
                if (region.Size.X <= 0.0f || region.Size.Y <= 0.0f)
                    continue;
                Vector2 off = set.OffsetOf(layer, index);
                var at = (Vector2I)region.Position;
                var span = (Vector2I)region.Size;
                for (int y = 0; y < span.Y; y++)
                {
                    int row = (at.Y + y) * sheet.Width + at.X;
                    int tile = (int)off.Y + y;
                    for (int x = 0; x < span.X; x++)
                    {
                        if (sheet.Data[((row + x) * 4) + 3] <= 2)
                            continue;
                        if (pass == 0)
                        {
                            top = Math.Min(top, tile);
                            low = Math.Max(low, tile);
                            continue;
                        }

                        double col = off.X + x;
                        if (tile <= top + band)
                        {
                            tipX += col;
                            tipN += 1.0;
                        }
                        else if (tile >= low - band)
                        {
                            footX += col;
                            footN += 1.0;
                        }
                    }
                }
            }

            if (pass == 0 && low < top)
                return 0.0f;
            if (pass == 1)
                return tipN <= 0.0 || footN <= 0.0
                    ? 0.0f : Mathf.Abs((float)((tipX / tipN) - (footX / footN)));
        }

        return 0.0f;
    }

    /// <summary>
    /// One borrowed layer, pasted back into tiles and cropped to the union of its
    /// phases - see <see cref="Borrowed"/>.
    /// </summary>
    private Borrowed? Loan(string layer)
    {
        if (_loans.TryGetValue(layer, out Borrowed? had))
            return had;
        _loans[layer] = null;
        AtlasSet? set = Field?.Atlas;
        if (set is null || !set.Has(layer) || set.PhasesOf(layer) <= 0)
            return null;
        double facing = Standing(set);
        int phases = set.PhasesOf(layer);
        var tiles = new Image[phases];
        var used = new Rect2I();
        for (int phase = 0; phase < phases; phase++)
        {
            tiles[phase] = set.TileImage(layer, set.EffectFrame(layer, phase, facing));
            Rect2I box = tiles[phase].GetUsedRect();
            if (box.Size.X <= 0 || box.Size.Y <= 0)
                continue;
            used = used.Size.X <= 0 ? box : used.Merge(box);
        }

        if (used.Size.X <= 0 || used.Size.Y <= 0)
            return null;
        const int pad = 1;
        int stride = used.Size.X + (pad * 2);
        Image strip = Image.CreateEmpty(stride * phases, used.Size.Y, false,
                                        Image.Format.Rgba8);
        var seen = new float[used.Size.X * used.Size.Y];
        for (int phase = 0; phase < phases; phase++)
        {
            strip.BlitRect(tiles[phase], used, new Vector2I((phase * stride) + pad, 0));
            for (int y = 0; y < used.Size.Y; y++)
            {
                for (int x = 0; x < used.Size.X; x++)
                    seen[(y * used.Size.X) + x] = Mathf.Max(
                        seen[(y * used.Size.X) + x],
                        tiles[phase].GetPixel(used.Position.X + x,
                                              used.Position.Y + y).A);
            }
        }

        // Where its own foot is across the crop, so the flame sits on the trunk
        // rather than on the middle of a rectangle: the crop is the union of twelve
        // phases and the fire does not stand in the centre of it.
        double footX = 0.0, footN = 0.0;
        int lowest = used.Size.Y - 1;
        int reach = Math.Max(3, used.Size.Y / 8);
        for (int y = Math.Max(0, lowest - reach); y <= lowest; y++)
        {
            for (int x = 0; x < used.Size.X; x++)
            {
                if (seen[(y * used.Size.X) + x] > 0.008f)
                {
                    footX += x;
                    footN += 1.0;
                }
            }
        }

        float across = footN > 0.0 ? (float)(footX / footN) : used.Size.X * 0.5f;

        // The dense body inside the crop: what has to be made the size asked for,
        // and what has to stand on the ground. See LoanBody.
        int rise = -1, sits = -1;
        for (int y = 0; y < used.Size.Y; y++)
        {
            bool solid = false;
            for (int x = 0; x < used.Size.X && !solid; x++)
                solid = seen[(y * used.Size.X) + x] >= LoanBody;
            if (!solid)
                continue;
            if (rise < 0)
                rise = y;
            sits = y;
        }

        if (rise < 0)
        {
            rise = 0;
            sits = used.Size.Y - 1;
        }

        var loan = new Borrowed(
            ImageTexture.CreateFromImage(strip), phases,
            new Vector2(used.Size.X, used.Size.Y),
            new Vector2(across, sits + 1), sits - rise + 1,
            stride / (float)strip.GetWidth(), pad / (float)strip.GetWidth(),
            used.Size.X / (float)strip.GetWidth());
        _loans[layer] = loan;
        GD.Print($"wood: {layer} on loan, crop {used.Size.X}x{used.Size.Y} x{phases} "
                 + $"phases, body {loan.Body:F0}px seated {used.Size.Y - 1 - sits}px "
                 + $"off its bottom, foot {across:F0}px across");
        return loan;
    }

    /// <summary>
    /// The flame and the smoke over one burning tree.
    ///
    /// <b>Two quads and not one, for <c>engine_fire</c>'s reason:</b> the fire adds
    /// light and the smoke takes it away, and one surface cannot do both. The order
    /// is the effect rather than a preference - the column goes down first so the
    /// flame has something dark to sit on, which is the whole of why the tank's
    /// burning plume reads on pale ground.
    ///
    /// <b>Made when the tree first catches, not at sowing.</b> Otherwise a board of
    /// 672 trees carries 1344 hidden quads for a fire nobody has lit. Kept
    /// afterwards, because a tree burns once.
    ///
    /// <b>Sorted by a nudge and not by priority.</b> Both hang on the tree's own
    /// transform, so they sort at the tree's depth and a trunk in front of this one
    /// still covers them - which a render priority would break, putting one tree's
    /// flame over the whole wood. Between themselves the nudge decides, and it is
    /// the same <see cref="Clear"/> that keeps a quad off the face it stands on.
    /// </summary>
    private void Kindle(PropNode tree)
    {
        Wildfire.Coat coat = tree.Coat;
        if (coat.Flame <= 0.0f && coat.Smoke <= 0.0f && !_burning.ContainsKey(tree))
            return;
        (int, bool) kind = (tree.Species, tree.Mirrored);
        Borrowed? blaze = TankFire ? Loan(AtlasSet.FireName) : null;
        Borrowed? column = TankFire ? Loan(AtlasSet.BurnName) : null;
        bool lent = blaze is not null && column is not null;
        if (!_burning.TryGetValue(tree, out MeshInstance3D? fire))
        {
            if (lent)
            {
                // One factor off the flame, and the column rides it - see LoanReach.
                float k = blaze!.Body <= 0.0f ? 1.0f
                    : tree.Size.Y * tree.Pixels * LoanReach / blaze.Body;
                if (!_pyres.TryGetValue(kind, out ArrayMesh? worn))
                    _pyres[kind] = worn = Stem(blaze.Base * k, blaze.Cell * k,
                                               RiseFactor);
                if (!_plumes.TryGetValue(kind, out ArrayMesh? veil))
                    _plumes[kind] = veil = Stem(column!.Base * k, column.Cell * k,
                                                RiseFactor);
                _smoking[tree] = Lick(veil, Loaned(column!, Fuming));
                _burning[tree] = fire = Lick(worn, Loaned(blaze, Lending));
            }
            else
            {
                if (!_pyres.TryGetValue(kind, out ArrayMesh? pyre))
                {
                    (Vector2 foot, Vector2 size) = Pyre(tree);
                    _pyres[kind] = pyre = Stem(foot * tree.Pixels,
                                               size * tree.Pixels, RiseFactor);
                }
                if (!_plumes.TryGetValue(kind, out ArrayMesh? plume))
                    _plumes[kind] = plume = Stem(tree.Foot * tree.Pixels * PlumeSpan,
                                                 tree.Size * tree.Pixels * PlumeSpan,
                                                 RiseFactor);
                _smoking[tree] = Lick(plume, Fumes(tree));
                _burning[tree] = fire = Lick(_pyres[kind], Flames(tree));
            }
        }
        MeshInstance3D smoke = _smoking[tree];
        Transform3D at = Rooted(tree);
        Vector3 nudge = Clear(Squash, RiseFactor);
        smoke.Transform = at with { Origin = at.Origin + nudge };
        fire.Transform = at with { Origin = at.Origin + nudge * 2.0f };
        smoke.Visible = coat.Smoke > 0.0f;
        fire.Visible = coat.Flame > 0.0f;
        if (smoke.MaterialOverride is ShaderMaterial fumes)
        {
            fumes.SetShaderParameter("time", _fireClock);
            fumes.SetShaderParameter("level", coat.Smoke);
            if (lent)
                fumes.SetShaderParameter("phase", Turn(tree, column!, SmokeRate));
        }

        if (fire.MaterialOverride is ShaderMaterial flames)
        {
            flames.SetShaderParameter("time", _fireClock);
            flames.SetShaderParameter("level", coat.Flame);
            if (lent)
                flames.SetShaderParameter("phase", Turn(tree, blaze!, FireRate));
        }
    }

    private MeshInstance3D Lick(ArrayMesh shape, ShaderMaterial ink)
    {
        var node = new MeshInstance3D
        {
            Mesh = shape,
            SortingUseAabbCenter = false,
            MaterialOverride = ink,
            Visible = false,
        };
        AddChild(node);
        return node;
    }

    /// <summary>Where the foot sits in the quad's own UV, and this tree's own place
    /// in the flicker. Both uniforms rather than built into the mesh: the mesh is
    /// shared by every tree of a kind, and the seed is what keeps two of them from
    /// burning in step - the rule three tanks' clocks are separate for.</summary>
    private static void Pitch(ShaderMaterial ink, PropNode tree, float span)
    {
        Vector2 size = tree.Size * span;
        Vector2 foot = tree.Foot * span;
        ink.SetShaderParameter("foot_v", size.Y <= 0.0f ? 0.9f : foot.Y / size.Y);
        ink.SetShaderParameter("seed",
                               (float)Grove.Hash01(tree.Seed, tree.Species, 733_003));
    }

    /// <summary>The tank's own tempo for the borrowed pair - <c>BurnLoop</c>'s two
    /// rates, because how fast the cycle runs is part of what is being judged. The
    /// flame flickers and the column drifts, and one rate for both would pick
    /// between a smouldering fire and a column going up like a jet.</summary>
    private const float FireRate = 10.0f;
    private const float SmokeRate = 5.0f;

    /// <summary>Which phase this tree is showing. The seed offsets it, for the
    /// reason three tanks' clocks are separate: a stand of trees stepping through
    /// one cycle together is one animation played eight times.</summary>
    private float Turn(PropNode tree, Borrowed loan, float rate)
    {
        float seed = (float)Grove.Hash01(tree.Seed, tree.Species, 733_003);
        return Mathf.Floor(Mathf.PosMod((_fireClock * rate) + (seed * loan.Phases),
                                        loan.Phases));
    }

    private static ShaderMaterial Loaned(Borrowed loan, Shader how)
    {
        var ink = new ShaderMaterial { Shader = how, RenderPriority = StandOrder };
        ink.SetShaderParameter("art", loan.Strip);
        ink.SetShaderParameter("stride", loan.Stride);
        ink.SetShaderParameter("pad", loan.Pad);
        ink.SetShaderParameter("span", loan.Width);
        ink.SetShaderParameter("phase", 0.0f);
        ink.SetShaderParameter("level", 0.0f);
        return ink;
    }

    /// <summary>
    /// The flame's quad, in art pixels - sized by the fire it has to hold, in tree
    /// heights, so it scales with the kind the way every other measurement here does.
    ///
    /// <b>Centred on the trunk, not on the art's own foot.</b> The shader reads
    /// across from <c>UV.x - 0.5</c>, so the middle of the quad is where it puts the
    /// fire; built around <c>tree.Foot.X</c> instead, which is not the middle of any
    /// of the four kinds, the whole flame stood beside its own tree by however far
    /// off centre that foot was.
    /// </summary>
    public static (Vector2 Foot, Vector2 Size) PyreQuad(PropNode tree, float rise)
    {
        float high = tree.Size.Y <= 0.0f ? 1.0f : tree.Size.Y;
        float half = rise * PyreFlank * high;
        // The fire's own reach plus the lift it stands on, and not one row below the
        // foot: those rows cannot be drawn at all - see PyreRoot.
        float up = rise * (PyreLift + PyreRoot) * high;
        return (new Vector2(half, up), new Vector2(half * 2.0f, up));
    }

    private (Vector2 Foot, Vector2 Size) Pyre(PropNode tree) =>
        PyreQuad(tree, FlameRise);

    private ShaderMaterial Flames(PropNode tree)
    {
        var ink = new ShaderMaterial { Shader = Blazing, RenderPriority = StandOrder };
        (Vector2 foot, Vector2 size) = Pyre(tree);
        float high = tree.Size.Y <= 0.0f ? 1.0f : tree.Size.Y;
        // The quad in tree heights, and where the foot sits down it. Handed over as
        // its own measurements rather than derived from a span, because the quad is
        // no longer the tree's own rectangle: it is sized by the fire, and a shader
        // that assumed otherwise would put the whole fire a fifth of a tree low.
        ink.SetShaderParameter("foot_v", size.Y <= 0.0f ? 0.9f : foot.Y / size.Y);
        ink.SetShaderParameter("tall", size.Y / high);
        ink.SetShaderParameter("wideq", size.X / high);
        ink.SetShaderParameter("root", FlameRise * PyreRoot);
        ink.SetShaderParameter("floor_fade", FlameRise * PyreFade);
        ink.SetShaderParameter("rise", FlameRise);
        ink.SetShaderParameter("seed",
                               (float)Grove.Hash01(tree.Seed, tree.Species, 733_003));
        ink.SetShaderParameter("level", 0.0f);
        ink.SetShaderParameter("time", _fireClock);
        return ink;
    }

    private ShaderMaterial Fumes(PropNode tree)
    {
        var ink = new ShaderMaterial { Shader = Smoking, RenderPriority = StandOrder };
        Pitch(ink, tree, PlumeSpan);
        // The quad in tree heights, both ways - the flame's arrangement and for
        // its reason, only more so: a puff is a circle, and a circle drawn in a UV
        // that is not square is an ellipse.
        Vector2 size = tree.Size * PlumeSpan;
        float high = tree.Size.Y <= 0.0f ? 1.0f : tree.Size.Y;
        ink.SetShaderParameter("tall", size.Y / high);
        ink.SetShaderParameter("wideq", size.X / high);
        ink.SetShaderParameter("level", 0.0f);
        ink.SetShaderParameter("time", _fireClock);
        return ink;
    }

    private void Fell()
    {
        foreach (MeshInstance3D stem in _standing.Values)
        {
            stem.Mesh = null;
            stem.MaterialOverride = null;
            stem.QueueFree();
        }
        foreach (MeshInstance3D cast in _shading.Values)
        {
            cast.Mesh = null;
            cast.MaterialOverride = null;
            cast.QueueFree();
        }
        foreach (MeshInstance3D seat in _seating.Values)
        {
            seat.Mesh = null;
            seat.MaterialOverride = null;
            seat.QueueFree();
        }
        foreach (MeshInstance3D fire in _burning.Values)
        {
            fire.Mesh = null;
            fire.MaterialOverride = null;
            fire.QueueFree();
        }
        foreach (MeshInstance3D smoke in _smoking.Values)
        {
            smoke.Mesh = null;
            smoke.MaterialOverride = null;
            smoke.QueueFree();
        }
        _standing.Clear();
        _shading.Clear();
        _seating.Clear();
        _burning.Clear();
        _smoking.Clear();
        _stems.Clear();
        _pyres.Clear();
        _plumes.Clear();
    }

    /// <summary>One tree's transform, from where it stands on this board.
    ///
    /// <b>Nothing is sunk here, and a trunk that was is what that cost.</b> A
    /// felled butt was put under the ground for a while, on the argument that a
    /// standing tree's art stops flat at the ground and that flat edge faces the
    /// camera once the tree is down. It does - but the trees have a root plate
    /// drawn on them, the burial ate it, and a trunk lying without its roots is
    /// the sawn end the burial was against. The other half of that cut was the
    /// angle, and it is capped now: see <see cref="Grove.FallFlat"/>.</summary>
    private Transform3D Rooted(PropNode tree) =>
        Trunk(Origin + tree.Ground, Field.LevelAt(tree.Cell) * Field.Lift,
              tree.Lean, Squash, RiseFactor, tree.Turn);

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
    /// <remarks>
    /// <b>And <paramref name="turn"/> is the felling, which is a rotation and
    /// not a lean</b> - see <see cref="PropNode.Turn"/> for why the two are
    /// different things. The card turns about its foot by the whole of it: the
    /// crown goes over with the trunk instead of sliding off it. Stated in
    /// screen px and put back into world units column by column, so what is
    /// drawn is a rigid rotation of the art and not one the camera squeezed - a
    /// crown 100px up is 100px out once it is flat, not <c>100*cos(e)</c>.
    ///
    /// <b>The width goes over on to the ground and not under it, and that is the
    /// whole of why a felled tree is not cut off along its own foot's row.</b>
    /// Under this camera "below the ground plane" and "behind the ground" are
    /// the same test - see <see cref="Body"/>, where a tank's running gear was
    /// being eaten by exactly this. A standing card's width runs across the
    /// screen and never crosses the foot's row; a turned one points it up and
    /// down the screen, so half the crown's width ends up below the foot and is
    /// buried by the cell the tree is standing on. That half is not under the
    /// ground, it is lying on it - which is what a felled tree is - so the
    /// column's downward share is world z, the ground going away from the
    /// camera, at <c>1/squash</c> per screen px. The screen offset is the same
    /// either way, so the picture is still the rotation and only the depth
    /// moves; the other half comes out at negative z, in front of the ground,
    /// which is where the near side of a trunk lying across a hex belongs. The
    /// quad is lifted clear of the face it stands on by <see cref="Clear"/>
    /// already, so none of it is left coplanar with the ground.
    ///
    /// Zero leaves the basis it has always had, to the bit.</remarks>
    public static Transform3D Trunk(Vector2 foot, float lift, float lean,
                                    float squash, float rise,
                                    float turn = 0.0f)
    {
        Vector3 at = World(foot + new Vector2(0.0f, lift), lift, squash, rise)
                     + Clear(squash, rise);
        float cos = Mathf.Cos(turn), sin = Mathf.Sin(turn);
        var bend = new Basis(
            new Vector3(cos, 0.0f, sin / Mathf.Max(squash, 0.0001f)),
            new Vector3(lean * rise * cos + rise * sin,
                        cos - lean * rise * sin, 0.0f),
            new Vector3(0.0f, 0.0f, 1.0f));
        return new Transform3D(bend, at);
    }

    /// <summary>The patch of ground one prop is standing on, from where it
    /// stands on this board.</summary>
    private Transform3D Seated(PropNode tree) =>
        Footing(Origin + tree.Ground, Field.LevelAt(tree.Cell) * Field.Lift,
                Squash, RiseFactor,
                Seating(tree.Root, tree.Rise, tree.Spread));

    /// <summary>
    /// How wide the patch under one prop is, in screen px of ground: the contact
    /// widened by a share of the height, and never less than what the prop has
    /// lying on the ground.
    ///
    /// <b>The floor is not a safety net, it is the answer for anything that is
    /// mostly footprint.</b> The first two terms are written for a thing that
    /// stands: a narrow contact and a body up in the air that shades out from
    /// it. A stone has neither - <see cref="PropSet.SpreadOf"/>'s remarks for
    /// how badly the contact reads a boulder, and its 10 to 19px of drawn height
    /// cannot make it up, so the pair came out at 4 to 7px of radius under a
    /// stone drawn 17 to 24 wide. Two or three pixels showing below the foot,
    /// which is nothing at all, and the share it now also throws a shorter cast
    /// by made that twice over.
    ///
    /// <b>A max rather than a sum, and rather than a new term.</b> The two are
    /// answers to the same question from opposite ends - what a standing thing
    /// shades, what a lying thing covers - and a thing is one or the other, so
    /// adding them would double-count whatever is both. A max can also only make
    /// a patch bigger, so nothing that already looked right can be spoilt by it:
    /// a tree's band is empty by declaration, its spread is the bottom row, and
    /// its radius is the one it had.
    ///
    /// Measured: trees unchanged at 23 to 33, bushes 10/12/15 to 15/19/23, and
    /// stones 6.4/4.2/6.6 to 8.8/9.4/11.9 - each of the last two now the width
    /// of the thing standing on it.
    /// </summary>
    public static float Seating(float root, float rise, float spread) =>
        Mathf.Max(root + SeatReach * rise, spread);

    /// <summary>
    /// A disc of ground at a prop's foot, lying in the plane the prop stands on.
    ///
    /// <b>The origin is the standing prop's, clearance and all</b> - the same
    /// expression <see cref="Trunk"/> and <see cref="Cast"/> use, because all
    /// three are claims about one point, and a patch that worked its own out is
    /// a second answer waiting to disagree.
    ///
    /// <b>A disc on the ground and never an ellipse on the screen.</b> The
    /// squash is the camera's, so laying the quad flat and letting the
    /// projection do it keeps the patch as round as the ground is - and keeps it
    /// round the day somebody re-renders the tile the camera angle is read off.
    /// Written as an ellipse in screen px it would have to be squashed by hand,
    /// against a number this class is already holding.
    ///
    /// <b>The column carrying the quad's normal is up, and it moves no
    /// vertex.</b> <see cref="Cast"/>'s point again: the quad is planar in its
    /// own z, so that column is free, and up rather than zero keeps the engine
    /// from sorting a box with no thickness.
    /// </summary>
    public static Transform3D Footing(Vector2 foot, float lift, float squash,
                                      float rise, float radius)
    {
        Vector3 at = World(foot + new Vector2(0.0f, lift), lift, squash, rise)
                     + Clear(squash, rise);
        var lie = new Basis(new Vector3(radius, 0.0f, 0.0f),
                            new Vector3(0.0f, 0.0f, -radius),
                            new Vector3(0.0f, 1.0f, 0.0f));
        return new Transform3D(lie, at);
    }

    /// <summary>The unit square the patch is painted on, centred on the foot.
    /// One for the board: all that differs between props is a scale, and a scale
    /// is the transform's business.</summary>
    private static ArrayMesh Sole()
    {
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        (Vector3 At, Vector2 Uv)[] corner =
        {
            (new Vector3(-1.0f, 1.0f, 0.0f), new Vector2(0.0f, 0.0f)),
            (new Vector3(1.0f, 1.0f, 0.0f), new Vector2(1.0f, 0.0f)),
            (new Vector3(1.0f, -1.0f, 0.0f), new Vector2(1.0f, 1.0f)),
            (new Vector3(-1.0f, -1.0f, 0.0f), new Vector2(0.0f, 1.0f)),
        };
        foreach (int k in new[] { 0, 1, 2, 0, 2, 3 })
        {
            st.SetNormal(Vector3.Back);
            st.SetUV(corner[k].Uv);
            st.AddVertex(corner[k].At);
        }
        return st.Commit();
    }

    /// <summary>
    /// What the patch is painted with: black, at <see cref="ShadowInk"/>'s
    /// strength, over exactly the ground the cast does not already have.
    ///
    /// <b>Two shadows of one object must not compound, and ordinary blending is
    /// the only thing that was making them.</b> This board has one sun and no
    /// ambient term at all - <see cref="ShadowInk"/> measures sun that did not
    /// arrive - so a second darkening from the same prop stands for nothing.
    /// Measured before this: 14% of the shadowed ground came out at 0.68 of the
    /// ground colour instead of 0.45, a pool at every foot that nobody chose.
    /// <c>ground_shadow</c> refused to draw a hull and a turret separately for
    /// this reason, and <see cref="ShadowInk"/> names the general fix as a mask
    /// taken as a maximum.
    ///
    /// <b>This is that maximum, per fragment and without a second pass, and it
    /// is affordable because the cast's map is invertible.</b> A fragment of the
    /// patch knows its own ground offset from the foot; <see cref="Cast"/> puts
    /// a sprite pixel at <c>(x + t*run.x, t*run.y)</c>, so <c>t</c> and <c>x</c>
    /// come straight back out of it and name the texel that threw the shadow
    /// there. One extra sample of art the prop is already holding.
    ///
    /// <b>The patch then paints the difference rather than the maximum</b>,
    /// because it is composited over the cast rather than instead of it: for the
    /// pair to end at <c>max(cast, blot)</c> the second one has to lay down
    /// <c>1 - (1-max)/(1-cast)</c>. Which of the two draws first still does not
    /// matter - both are black, so the pair ends at
    /// <c>1-(1-a)(1-b)</c> either way round, and that is what this inverts.
    ///
    /// <b>Depth and stencil cannot do it, and it is worth saying why</b>, since
    /// both look like the cheap answer: they mask per fragment regardless of
    /// alpha, so the cast would be cut by the patch's whole quad - or, with an
    /// alpha scissor, by a hard disc, punching a hole in the streak exactly
    /// where the patch's own rim has faded to nothing.
    ///
    /// <b>The blot is arithmetic rather than a texture now.</b> It was a 64px
    /// image; a fragment shader that is already computing an inverse map can
    /// take a smoothstep, and one falloff written down twice is one too many.
    ///
    /// <b>Its own material per prop</b>, for the cast's reason: the fade under a
    /// tank multiplies this alpha and nothing else's - and here also because the
    /// lean, which the inverse map needs, is a different number for every prop
    /// on every frame.
    /// </summary>
    private static ShaderMaterial Blot() =>
        new()
        {
            Shader = _seatShader ??= new Shader { Code = SeatShader },
            RenderPriority = ShadowOrder,
        };

    private static Shader? _seatShader;

    /// <summary>Internal so the check can read it: that the patch samples the
    /// prop's own art through the inverse cast, and that what it lays down is
    /// the difference and not the whole blot, are the two claims that make this
    /// a maximum rather than a second shadow.</summary>
    internal const string SeatShader = @"
shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_never;

uniform sampler2D art : filter_linear_mipmap, source_color;
uniform vec2 foot = vec2(0.0);   // image px of the prop's foot within art
uniform vec2 span = vec2(1.0);   // image px, the art's size
// The picture it hands over to, its own foot and size, and how far along that is.
// Both, because what this has to keep off is what the cast is actually laying
// down - and the cast is mixing the pair, so a patch reading one of them stands
// aside from a streak that is half gone.
uniform sampler2D burnt : filter_linear_mipmap, source_color, hint_default_transparent;
uniform vec2 burnt_foot = vec2(0.0);
uniform vec2 burnt_span = vec2(1.0);
uniform float swap = 0.0;
uniform float firm = 1.0;       // the same gamma the cast puts on that alpha
uniform float pixels = 1.0;      // image px to screen px
uniform float rise = 1.0;        // cos(elevation)
uniform vec2 run = vec2(0.0);    // Cast's own column: lean*rise + sun*stands
uniform float radius = 1.0;      // the patch's own reach, in ground px
uniform float ink = 0.0;         // ShadowInk.A, times this prop's fade
uniform float core = 0.34;       // where the blot stops being flat
// Whether there is a cast to keep off. Nothing to stand aside for when the cast
// is switched off, and a patch that went on standing aside would show a bite
// out of itself taken by a shadow that is not on the board.
uniform float clip = 1.0;

// One picture's own alpha where the cast would have thrown it here, or nothing at
// all if that lands outside the picture. The gamma is the cast's, because the two
// have to agree about what the cast put down.
float thrown(sampler2D tex, vec2 f, vec2 z, float dx, float t, float gamma) {
    vec2 uv = (f + vec2(dx - t * run.x, -t * rise) / pixels) / z;
    if (t <= 0.0 || uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0)
        return 0.0;
    float a = texture(tex, uv).a;
    return gamma >= 0.999 ? a : pow(a, gamma);
}

void fragment() {
    // The quad is Sole(): local x across, local y up the ground away from the
    // camera, and Footing scales both by the radius.
    float lx = UV.x * 2.0 - 1.0;
    float ly = 1.0 - UV.y * 2.0;
    float blot = (1.0 - smoothstep(core, 1.0, length(vec2(lx, ly)))) * ink;

    // Cast, run backwards. dz is what the height became, so it names the height;
    // the height then names how much of dx was the sun and how much was the
    // sprite's own width.
    float dx = radius * lx;
    float dz = -radius * ly;
    float t = run.y == 0.0 ? -1.0 : dz / run.y;
    float cover = thrown(art, foot, span, dx, t, 1.0);
    if (swap > 0.0)
        cover = mix(cover, thrown(burnt, burnt_foot, burnt_span, dx, t, firm),
                    swap);
    float cast = cover * ink * clip;

    ALBEDO = vec3(0.0);
    ALPHA = cast >= 0.9995
        ? 0.0
        : clamp(1.0 - (1.0 - max(cast, blot)) / (1.0 - cast), 0.0, 1.0);
}
";

    /// <summary>One tree's shadow, from where it stands on this board.</summary>
    private Transform3D Shaded(PropNode tree) =>
        Cast(Origin + tree.Ground, Field.LevelAt(tree.Cell) * Field.Lift,
             tree.Lean, Squash, RiseFactor, SunCast, (float)tree.Tier.Stands,
             tree.Squat);

    /// <summary>
    /// The same tree laid down the sun: <see cref="Trunk"/>'s transform with the
    /// height folded into the ground.
    ///
    /// <b>One map, not a second piece of geometry.</b> A point <c>h</c> above the
    /// foot lands at <c>h*<see cref="SunCast"/></c> across the ground, so the
    /// column that carried height carries that instead and everything else is
    /// unchanged - which means the lean rides along for free. A gust bends the
    /// shadow exactly as far as it bends the tree, because it is the same number
    /// in the same column.
    ///
    /// <b>The z column is arbitrary and has to be.</b> Flattening onto the ground
    /// is a projection, so any basis that does it is singular; the quad is planar
    /// in z, so that column moves no vertex at all. Up rather than the
    /// projection's own zero, which would leave the engine sorting and culling a
    /// box with no thickness.
    ///
    /// <b>The origin is the standing tree's, clearance and all.</b> The foot is
    /// on the ground already, so the shadow needs no place of its own - and it
    /// wants exactly the same nudge off the face, for exactly the same coin toss.
    ///
    /// <b>The share of the drawn extent that is thrown is the tier's, and the
    /// lean's is not.</b> <see cref="PropTier.Stands"/> says how much of a
    /// sprite's height is height rather than footprint drawn in isometric, and
    /// only the sun's column is scaled by it: the lean is a real horizontal
    /// drift of a point the sprite has already put at that height, so it moves
    /// the shadow by exactly as much whatever the thing is made of. One means
    /// what the board did before there was a share at all, which is why it is
    /// the default and why the wood is unchanged.
    ///
    /// What this does not do is follow the ground it falls on: the shadow stays
    /// in the plane of the cell the tree stands in, so one that runs over a step
    /// lies flat across it. At 0.37 of a cell, and with nothing growing on a
    /// ramp, that is a few cells' worth of edge on the board - named rather than
    /// fixed, because fixing it means marching the same height map the terrain's
    /// own shadow marches and that is worth doing once, for both.
    /// </summary>
    public static Transform3D Cast(Vector2 foot, float lift, float lean,
                                   float squash, float rise, Vector2 sun,
                                   float stands = 1.0f, float squat = 1.0f)
    {
        Vector3 at = World(foot + new Vector2(0.0f, lift), lift, squash, rise)
                     + Clear(squash, rise);
        // Squat scales the whole column, the sun's share included: what is
        // thrown is the height the tree still has, so a trunk going over pulls
        // its own shadow in after it - see PropNode.Squat and Trunk.
        var flat = new Basis(new Vector3(1.0f, 0.0f, 0.0f),
                             new Vector3((lean * rise + sun.X * stands) * squat,
                                         0.0f, sun.Y * stands * squat),
                             new Vector3(0.0f, 1.0f, 0.0f));
        return new Transform3D(flat, at);
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
    /// <summary>
    /// The same rectangle <see cref="Stem"/> stands up, hinged at the seat: the
    /// half behind the seat stands, the half in front of it lies on the ground.
    /// For <see cref="ProcKick.Lying"/>.
    ///
    /// <b>Both halves, because dust needs both.</b> Standing alone, nothing can
    /// be drawn below the contact line - so half of every puff went missing and
    /// what was left had its centre half a cloud up the screen from the rut that
    /// made it. Lying alone, there is no height at all, and a trail that cannot
    /// rise reads as a stain on the ground. The fold is the tank's own surface,
    /// <see cref="Body"/>, one effect along: a screen px above the seat is
    /// 1/cos(e) of height, a screen px in front of it is 1/sin(e) of ground
    /// coming at the camera.
    ///
    /// <b>Two faces and never one.</b> A single quad spanning the seam would
    /// interpolate the fold into a ramp - <see cref="Body"/>'s finding, and it
    /// costs nothing to avoid here.
    ///
    /// <b>The v axis runs the same way as <see cref="Stem"/>'s</b>, across both
    /// faces, so <c>foot_v</c> means the same thing to the shader whichever side
    /// of the hinge it is painting and a model written for the flat quad works
    /// here unread.
    ///
    /// <b><paramref name="ahead"/> is how much of the rect is in front of the
    /// seat</b>, in the same screen px - the shader's <c>root</c>, which says
    /// how much of the model is on the near side. The mesh has to be moved by
    /// the same amount or the seat lands behind the point it was given.
    /// </summary>
    public static ArrayMesh Fold(Vector2 foot, Vector2 size, float ahead,
                                 float squash, float rise)
    {
        float left = -foot.X, right = size.X - foot.X;
        // The rect over its own screen px, measured up from the seat: ahead is
        // how much of it is in front of the seat, which is the half that lies.
        float top = foot.Y - ahead, bottom = -ahead;
        float span = Mathf.Max(size.Y, 1e-4f);
        Vector3 lie = Clear(squash, rise);

        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        // A screen px above the seat is 1/cos(e) of height; a screen px in front
        // of it is 1/sin(e) of ground coming at the camera. Body's two lines,
        // and the same hinge.
        Vector3 At(float x, float y) =>
            y >= 0.0f ? new Vector3(x, y / rise, 0.0f)
                      : lie + new Vector3(x, 0.0f, -y / Mathf.Max(squash, 1e-4f));
        Vector2 Uv(float x, float y) =>
            new Vector2((x - left) / Mathf.Max(right - left, 1e-4f),
                        (top - y) / span);
        void Face(float low, float high)
        {
            (float X, float Y)[] corner =
            {
                (left, high), (right, high), (right, low), (left, low),
            };
            foreach (int c in new[] { 0, 1, 2, 0, 2, 3 })
            {
                st.SetNormal(Vector3.Back);
                st.SetUV(Uv(corner[c].X, corner[c].Y));
                st.AddVertex(At(corner[c].X, corner[c].Y));
            }
        }
        // Two faces and never one, because the seam is where the surface turns:
        // a single quad spanning it would interpolate its own fold into a ramp.
        if (top > 0.0f)
            Face(0.0f, top);
        if (bottom < 0.0f)
            Face(bottom, 0.0f);
        return st.Commit();
    }

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

    /// <summary>
    /// What a tree is painted with: the set's own art, unshaded because it
    /// arrived lit, and mipmapped because it is painted at eight times the size
    /// it is drawn at - <see cref="PropNode._Ready"/>'s reason, unchanged. One per
    /// tree, because the fade is per tree; see <see cref="BuildTrees"/>.
    ///
    /// <b>A shader rather than the standard material it was, and the reason is the
    /// wreck's word for word:</b> a multiply cannot take the colour out of
    /// anything. Charring a green crown by scaling its albedo gives a dark green
    /// crown, and dark green is a tree in shadow - which is why the tank's char is
    /// a shader too. So the luminance is taken first and the tint is dropped, and
    /// then it is darkened.
    ///
    /// <b>At <c>scorch = 0</c> it is the material it replaced, and that is
    /// measured rather than reasoned:</b> the same board rendered before and after
    /// the swap came out identical to the pixel. The one thing to keep true is the
    /// pair of defaults - unshaded and writing ALPHA is what a standard material
    /// with Transparency.Alpha is, and a spatial shader that leaves ALPHA alone
    /// drops out of the transparent pass and stops sorting against the wood.
    /// </summary>
    internal const string BarkShader = @"
shader_type spatial;
render_mode unshaded, cull_disabled;

uniform sampler2D art : source_color, filter_linear_mipmap, hint_default_white;
// The picture it hands over to, and where each of the two sits inside the quad.
// Both mapped, rather than one of them being the quad, because the quad is the
// union of the pair - see Shape.
uniform sampler2D burnt : source_color, filter_linear_mipmap, hint_default_transparent;
uniform vec4 art_map = vec4(0.0, 0.0, 1.0, 1.0);
uniform vec4 burnt_map = vec4(0.0, 0.0, 1.0, 1.0);
// How far the handover has got. Zero is the living picture exactly and one the
// burnt one exactly, both to the pixel.
uniform float swap = 0.0;
// The wood's own per-tree reveal, and how far the living picture has charred.
uniform float fade = 1.0;
uniform float scorch = 0.0;
// What charcoal is worth, as a share of the luminance it replaces.
uniform float coal = 0.30;
// A gamma on the burnt picture's alpha, for art whose strokes are thinner than a
// screen pixel. One leaves it alone exactly - see Firm. Only the burnt one gets
// it, which is what it was always for: the living crown is a mass.
uniform float firm = 1.0;

LIFT_FN

void fragment() {
    vec4 live = lift(art, UV, art_map, 1.0);
    float lum = dot(live.rgb, vec3(0.299, 0.587, 0.114));
    live.rgb = mix(live.rgb, vec3(lum * coal), scorch);
    vec4 dead = lift(burnt, UV, burnt_map, firm);
    // Mixed premultiplied and divided back out, and that is the whole of why a
    // trunk cannot thin halfway through: the alphas are averaged rather than
    // composited, so where both pictures are solid the answer is solid. Two
    // quads at complementary alpha would come out at 0.75 at the midpoint - the
    // track smear's finding, and the reason it lies over its own layer instead
    // of cross-fading with it.
    vec3 pre = mix(live.rgb * live.a, dead.rgb * dead.a, swap);
    float a = mix(live.a, dead.a, swap);
    ALBEDO = a > 0.0001 ? pre / a : vec3(0.0);
    ALPHA = a * fade;
}
";

    /// <summary>
    /// One of the two pictures off the union quad, shared by the crown and by the
    /// shadow it throws.
    ///
    /// <b>One text and not two, for FoamEdge's reason:</b> the mask is a mask
    /// <i>of</i> the silhouette, so a copy that drifted would be two answers to one
    /// question - and the way it would read is a burnt trunk throwing a shadow with
    /// leaves in it. There is a check that both compiled sources contain this.
    ///
    /// The quad point comes in as an argument because a built-in is only in scope in
    /// the function it belongs to - reading UV in here is a compile error, and the
    /// material that fails to compile falls back to an opaque black one, which draws
    /// every tree as its own bare rectangle.
    ///
    /// Outside its own rectangle the picture is not there at all, and that is said
    /// rather than left to the sampler's clamp: these arts fill their image to the
    /// border, so the edge texel is not transparent and clamping would smear it
    /// across the margin the union added.
    /// </summary>
    internal const string PictureLift = @"
vec4 lift(sampler2D tex, vec2 at, vec4 map, float gamma) {
    vec2 uv = map.xy + at * map.zw;
    vec4 c = texture(tex, uv);
    float inside = float(uv.x >= 0.0 && uv.x <= 1.0
                         && uv.y >= 0.0 && uv.y <= 1.0);
    c.a = (gamma >= 0.999 ? c.a : pow(c.a, gamma)) * inside;
    return c;
}";

    private static readonly Shader Timber =
        new() { Code = BarkShader.Replace("LIFT_FN", PictureLift) };

    /// <summary>
    /// What a tree's shadow is painted with: the same pair of pictures the crown is,
    /// used as a mask and nothing else. <see cref="ShadowInk"/>'s alpha is the
    /// strength and its rgb is black, so the leaves' gaps are the shadow's gaps.
    ///
    /// <b>It cross-fades now, and the thing that used to stop it was two draws
    /// rather than the blending.</b> A stencil laid down twice at complementary
    /// strength compounds instead of averaging - two 0.55s make 0.80, which is why
    /// <c>ground_shadow</c> will not draw a hull and a turret separately - so the
    /// shadow showed one picture or the other and changed on the middle frame of the
    /// handover. Sampling both in <i>one</i> fragment and mixing the coverage is the
    /// crown's own answer, and it is exactly as legal here: one draw, one alpha,
    /// nothing to compound with.
    ///
    /// <b>And the mid-window switch is worth naming as a bug rather than a
    /// simplification, now that the handover happens under the fire.</b> The crown
    /// spends 1.2s dissolving; the shadow snapped in the middle of it, so the one
    /// frame the eye was already watching was the frame the ground jumped.
    ///
    /// <b>The burnt alpha goes through <see cref="Firm"/> here too</b>, because the
    /// crown's does: a mask thinner than the thing it is a mask of is the same two
    /// answers again, and on this art it is a whole gamma's worth of twig.
    ///
    /// <b>Its own material per prop</b>, because the fade under a tank multiplies
    /// this alpha and nothing else's, and because how far along the handover is is a
    /// different number for every tree on the cell.
    /// </summary>
    internal const string CastShader = @"
shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_never;

uniform sampler2D art : source_color, filter_linear_mipmap, hint_default_white;
uniform sampler2D burnt : source_color, filter_linear_mipmap, hint_default_transparent;
uniform vec4 art_map = vec4(0.0, 0.0, 1.0, 1.0);
uniform vec4 burnt_map = vec4(0.0, 0.0, 1.0, 1.0);
// How far the handover has got, the tree's own fade times ShadowInk's strength,
// the ink itself, and the gamma on the burnt picture's alpha.
uniform float swap = 0.0;
uniform float ink = 0.0;
uniform vec3 tint = vec3(0.0);
uniform float firm = 1.0;
LIFT_FN

void fragment() {
    // Mixed coverage in one fragment, never two draws - see CastShader. Skipped
    // outright at swap 0 so that a wood nobody has lit is the single sample it
    // always was, to the bit.
    float a = lift(art, UV, art_map, 1.0).a;
    if (swap > 0.0)
        a = mix(a, lift(burnt, UV, burnt_map, firm).a, swap);
    ALBEDO = tint;
    ALPHA = a * ink;
}
";

    private static Shader? _castShader;

    private static ShaderMaterial Shade(PropNode tree)
    {
        var ink = new ShaderMaterial
        {
            Shader = _castShader ??=
                new Shader { Code = CastShader.Replace("LIFT_FN", PictureLift) },
            RenderPriority = ShadowOrder,
        };
        (_, _, Godot.Vector4 live, Godot.Vector4 dead) = Union(tree);
        ink.SetShaderParameter("art", tree.Art);
        if (tree.BurntArt is not null)
            ink.SetShaderParameter("burnt", tree.BurntArt);
        ink.SetShaderParameter("art_map", live);
        ink.SetShaderParameter("burnt_map", dead);
        ink.SetShaderParameter("swap", 0.0f);
        ink.SetShaderParameter("ink", 0.0f);
        ink.SetShaderParameter("tint",
            new Vector3(ShadowInk.R, ShadowInk.G, ShadowInk.B));
        ink.SetShaderParameter("firm", Firm);
        return ink;
    }

    /// <summary>How dark charcoal goes, as a share of the luminance under it.
    /// The tank's number, and for the tank's reason: 0.55 reads as a grey tree
    /// and 0.22 as a silhouette, so the wreck landed on a third and a burnt
    /// crown wants the same.</summary>
    public const float Coal = 0.30f;

    /// <summary>
    /// The gamma the burnt art's alpha is drawn through.
    ///
    /// <b>Because a bare tree is all thin lines, and thin lines do not survive
    /// minification.</b> The branches are 5-8 art px, which at 8x is well under a
    /// screen pixel, so the mip that gets drawn has them at half coverage
    /// everywhere - and half coverage over pale ground is grey. Measured: the art
    /// on disk is (39,38,37) charcoal and the board drew it at about 95, which
    /// read as a silver skeleton rather than a burnt tree.
    ///
    /// <b>Coverage-correct is not the same as right here.</b> The averaging is
    /// doing its job - at that distance a real bare tree is a haze - but the art
    /// was drawn as a black skeleton and is meant to read as one, so the alpha is
    /// pushed back up. 0.6 puts a half-covered texel at 0.66 rather than 0.5.
    ///
    /// <b>The living art is left alone</b>, at one, which the shader takes as
    /// exactly no change: a canopy is a mass rather than a set of lines, so it
    /// minifies without going translucent, and thickening its edge would be a
    /// change to every board rendered so far.
    /// </summary>
    public const float Firm = 0.60f;

    private static ShaderMaterial Bark(PropNode tree, int order)
    {
        var ink = new ShaderMaterial { Shader = Timber, RenderPriority = order };
        (_, _, Godot.Vector4 live, Godot.Vector4 dead) = Union(tree);
        ink.SetShaderParameter("art", tree.Art);
        // The pair of rectangles is a fact about the kind, so it is set here and
        // never again; only how far between them the tree is moves. A kind with
        // no burnt picture leaves that sampler at its transparent default, which
        // with swap pinned at zero is never read.
        if (tree.BurntArt is not null)
            ink.SetShaderParameter("burnt", tree.BurntArt);
        ink.SetShaderParameter("art_map", live);
        ink.SetShaderParameter("burnt_map", dead);
        ink.SetShaderParameter("swap", 0.0f);
        ink.SetShaderParameter("fade", 1.0f);
        ink.SetShaderParameter("scorch", 0.0f);
        ink.SetShaderParameter("coal", Coal);
        ink.SetShaderParameter("firm", Firm);
        return ink;
    }

    /// <summary>Where the line runs, as a fraction of the cell, and the two
    /// half-widths about it - a 2px line inside a 5px backing, in the cell's own
    /// units so the whole thing is one statement of the same shape. Off the cell,
    /// never off the tank: it says where this tank stands, and a ring that grew
    /// with the class would be a second claim about size arguing with the one the
    /// tanks make.
    ///
    /// <b>Inset further than the flat board's <see cref="SelectionRing.Inset"/>,
    /// and the relief is what buys the difference.</b> At 0.86 a corner stands
    /// <c>0.86*cos30 = 0.745</c> of the circumradius off the middle against an
    /// edge at 0.866 - about 15px of a 248px tile - and on flat ground nothing is
    /// drawn in that margin, so it costs nothing there. At a pond it is exactly
    /// where the water's own side and the beach beyond it are painted, and a mark
    /// standing in it reads as climbing the slope next door even though it never
    /// leaves its own cell. Measured on (8,5): with the ford nudge taken away
    /// entirely the same arc is drawn in the same pixels, so this is the margin
    /// and not the depth. 0.75 doubles it to about 30px.
    ///
    /// <b>Those widths are on the ground, and the camera squashes the ground.</b>
    /// A band 2 units wide keeps its 2px across the edges that run up the screen
    /// and comes out at <c>sin(e)</c> of that - one pixel - on the two that run
    /// across it, which is what a stripe painted on the ground does and what the
    /// 2D ring, drawn on the screen at a flat 2px, cannot do. Measured against
    /// it on a flat board: the same colour to a level (76,216,255 against
    /// 76,217,255) in the same 218x73 box to a pixel, over 433 fully saturated
    /// pixels rather than 1030.</summary>
    public const float Ring = 0.75f;
    internal const float Line = 0.008f;
    internal const float Fringe = 0.020f;

    /// <summary>How far a mark steps inward for each mark already laid on its
    /// cell - see <see cref="BuildRing"/> for why only then. Wide enough that
    /// the two bands do not touch (a band is <c>2*Fringe</c> across, so 0.04 is
    /// the floor) and narrow enough that the innermost of three still stands
    /// clear of the hull it is about: at 0.13 the third is at 0.49 of the cell,
    /// which on a 248px tile is about 60px in from the outermost.</summary>
    internal const float Nest = 0.13f;

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
