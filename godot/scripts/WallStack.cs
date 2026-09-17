using System;
using System.Collections.Generic;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The wall on the board: one multimesh of bricks, and the collapse played over
/// it.
///
/// <b>The whole stack is laid out in fractions of a cell and the node only
/// scales.</b> Uniformly, and that word is load bearing: the board's squash
/// belongs to the camera, which is tilted by <c>asin(squash)</c> precisely so
/// that <see cref="Stage3D.World"/>'s division by it comes back out. A node that
/// divides as well projects the depth axis twice and leaves every solid stretched
/// along z by about two - invisible on a wall whose leaves are thin and whose
/// bricks are axis aligned, and impossible to miss the moment the rubble spreads
/// across the cell. Because the scale is uniform, a rotation stays a rotation and
/// a normal needs no correction, which is the other half of the same choice.
///
/// <b>There is no light on this board and the shading is written, not lit.</b>
/// Measured over in the harness: a <c>DirectionalLight3D</c> changes zero pixels,
/// because every material is <c>unshaded</c>. So the ground gets its sun from a
/// march over a height map and its sides from a bearing, and the wall gets its
/// own the same way - one Lambert term against the beauty rig's key, worked out
/// per fragment from the face normal. The key and not <see cref="Stage3D.Sun"/>:
/// the shadow's sun is behind the board, and the tanks standing beside this wall
/// are rendered under the key.
///
/// <b>What replaces the ray-traced ambient occlusion is the thing the occlusion
/// was standing in for.</b> The Blender prop calls tracing "the one render
/// setting the model needs rather than prefers", because the joints between
/// bricks are gaps between separate objects and nothing in a material can darken
/// them. There is no equivalent in <c>gl_compatibility</c>, and none is wanted:
/// the wall is being *generated*, so where every joint is, is known. The dark
/// line goes on by construction, as a band round each face measured in board
/// units - which is also, exactly, what the reference draws. 42.9% of that PNG's
/// opaque pixels are its near-black outline.
/// </summary>
public sealed partial class WallStack : Node3D
{
    /// <summary>The cell's circumradius in board px. Everything in
    /// <see cref="WallKit"/> is a fraction of this.</summary>
    public required float Radius { get; init; }

    /// <summary>The camera's two terms, off the field. The node's distortion.
    /// </summary>
    public required float Squash { get; init; }
    public required float Rise { get; init; }

    /// <summary>Where the cell's top face centre is, in world units.</summary>
    public required Vector3 Anchor { get; init; }

    /// <summary>Drawn after the ground and before anything that does not write
    /// depth. It is a solid object on the board, like a prism and unlike a
    /// billboard, so it takes the depth buffer rather than a sorting slot - which
    /// is what lets a tank behind it be occluded by it.</summary>
    public int Order { get; init; }

    /// <summary>The atlas the ramming tank is drawn out of, or null for the box
    /// it used to be.
    ///
    /// <b>The board's own tank, not a model made for this bench.</b> How a tank
    /// looks on this board is a question that has one answer already - the
    /// rendered atlas, composited by <see cref="TankSprite"/> - and a second one
    /// modelled here would be a tank of some other size next to a wall whose
    /// whole purpose is to be judged against tanks. It costs nothing either: the
    /// bench loads this atlas anyway, for <see cref="AtlasSet.HexRect"/>.
    ///
    /// Optional because the bench survives a missing atlas, and without one
    /// there is no board at all - see the warning in <c>WallBench._Ready</c>.
    /// </summary>
    public AtlasSet? Tank;

    /// <summary>Which way the ramming tank drives, in board degrees.
    ///
    /// Board degrees and never the prop's, though the bodies are in the prop's
    /// frame: the sprite is a billboard drawn for the board's one camera, so the
    /// picture of a tank driving north is the same picture whatever the
    /// turntable is doing. The turntable moves where it is, not which way it
    /// faces.</summary>
    public double TankFacing = 270.0;

    private WallKit.Plan _plan = null!;
    private List<WallFall.Flight>? _fall;
    private MultiMeshInstance3D _bricks = null!;
    private MultiMesh _mesh = null!;
    private MultiMeshInstance3D _ghosts = null!;
    private MultiMesh _ghostMesh = null!;
    private MeshInstance3D? _ram;
    private SubViewport? _paint;
    private Node2D? _holder;
    private TankSprite? _crew;
    private MeshInstance3D? _core;
    private MeshInstance3D? _sleeve;
    private MultiMeshInstance3D _shade = null!;
    private MultiMesh _shadeMesh = null!;
    private float _clock;
    private float _length;

    /// <summary>How far into the collapse it is, in seconds. Negative means the
    /// wall is standing and nothing has been fired.</summary>
    public float Clock => _fall is null ? -1.0f : _clock;
    public float Length => _length;
    public WallKit.Plan Plan => _plan;

    public void Raise(WallKit.Plan plan)
    {
        _plan = plan;
        _fall = null;
        _clock = 0.0f;
        Rebuild();
    }

    /// <summary>Fire the charge. Solved here and now, once - see
    /// <see cref="WallFall"/> for why there is no solver running behind it.
    /// </summary>
    public void Fell(int seed, WallFall.Shot shot, float coverage)
    {
        _fall = WallFall.Solve(_plan, seed, shot, coverage);
        _length = WallFall.Length(_fall);
        _clock = 0.0f;
    }

    public bool Falling => _fall is not null;

    /// <summary>Turntable, in radians about the cell's own vertical.
    ///
    /// <b>The prop turns and the camera does not, and that is not a shortcut.</b>
    /// This board's projection *is* its camera - <see cref="Stage3D.World"/>
    /// divides a flat row by squash exactly so a camera tilted by
    /// <c>asin(squash)</c> undoes it, and the ground art is painted for that one
    /// view. Move the camera and the tile stops being a tile. Turning the prop
    /// under a fixed sun is also the more useful of the two: every side comes
    /// round lit differently, which is the thing a still of one side cannot show.
    ///
    /// Nothing about the wall changes - the plan, the guard and the collapse are
    /// all in the prop's own frame and never see this.</summary>
    public float Spin
    {
        get => _spin;
        set
        {
            _spin = value;
            if (_mesh is null)
                return;
            Transform = Frame();
            // The shadow has to be redone even when nothing has moved: its run is
            // the sun's, and the sun does not turn with the wall.
            Pose();
        }
    }

    private float _spin;

    private Transform3D Frame() => new(
        new Basis(Vector3.Up, _spin) * Basis.FromScale(Vector3.One * Radius), Anchor);

    /// <summary>Where the sun's ground run points in the prop's own frame.
    ///
    /// <b>The sun is fixed and the wall is what moves</b>, so the run has to come
    /// back through the turntable before the shadow is built in local space - the
    /// node's own rotation then puts it back where the sun wanted it. Left in
    /// world terms it would turn with the wall, which is a wall that carries its
    /// own sun round with it.</summary>
    private Vector2 LocalRun()
    {
        Vector3 run = new Basis(Vector3.Up, -_spin)
                      * new Vector3(Stage3D.SunCast.X, 0.0f, Stage3D.SunCast.Y);
        return new Vector2(run.X, run.Z);
    }

    public override void _Ready()
    {
        // Uniform, and that is the whole of it: the stack is in fractions of a
        // cell and this turns them into world units.
        //
        // **The board's squash is the camera's, and applying it here as well
        // projects the depth axis twice.** Stage3D.World divides a flat row by
        // squash precisely so the camera's own tilt can multiply it back, so a
        // node that also divides leaves every solid stretched along z by 1/squash
        // - about two. It hid in the standing wall, whose leaves are thin in z
        // and whose bricks are all axis-aligned, and showed the moment the rubble
        // spread across the cell: the reported reach said 1.06 while the picture
        // put half the pile off the tile. Checked against the drawn cell rather
        // than argued: local z = sqrt(3)/2 has to land on the near vertex, and at
        // R = 124 with squash 0.5075 that is 54.5px, which is the plate's own
        // half-depth to the pixel.
        //
        // The same goes for lift. A cube has to come out a cube; dividing y by
        // rise makes one 1/cos(elevation) taller than it is deep, which is the
        // billboard convention and belongs to the props, not to geometry.
        Transform = Frame();

        _mesh = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true,
            UseCustomData = true,
            Mesh = Brick(),
        };
        _bricks = new MultiMeshInstance3D
        {
            Multimesh = _mesh,
            MaterialOverride = Mortar(),
            // Sorted by the depth buffer, not by where the node is: this is a
            // solid, and its own far end is behind its near end.
            SortingUseAabbCenter = true,
        };
        AddChild(_bricks);

        // What is leaving. Its own mesh rather than its own alpha on the one
        // above, because a material that blends is a material in the transparent
        // pass, and putting the whole wall there costs a thousand pixels along
        // its own foot for nothing - measured. See Pose: a piece moves from one
        // mesh to the other on the frame it starts to go.
        _ghostMesh = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true,
            UseCustomData = true,
            Mesh = Brick(),
        };
        _ghosts = new MultiMeshInstance3D
        {
            Multimesh = _ghostMesh,
            MaterialOverride = Mortar(going: true),
            SortingUseAabbCenter = true,
        };
        AddChild(_ghosts);

        _shadeMesh = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            // Both, and only so the shadow can be eaten away in the same places
            // as the piece throwing it: the colour carries how much is left and
            // the custom data the half extents the crumbs are measured in. See
            // Erosion.
            UseColors = true,
            UseCustomData = true,
            Mesh = Brick(),
        };
        _shade = new MultiMeshInstance3D
        {
            Multimesh = _shadeMesh,
            MaterialOverride = Shade(),
        };
        AddChild(_shade);

        // Last, so its lines are laid over the solids rather than under them.
        // It draws nothing at all while it is switched off - see WallHulls.
        _hulls = new WallHulls();
        AddChild(_hulls);

        if (_plan is not null)
            Rebuild();
    }

    /// <summary>The bodies, when the wall is being knocked down honestly.
    ///
    /// Set and the pieces come off it instead of off <see cref="WallFall"/>; the
    /// drawing is the same either way, which is the point of asking a transform
    /// for a pose rather than asking who computed it.</summary>
    public WallRig? Rig { get; set; }

    public override void _Process(double delta)
    {
        if (_bricks is null)
            return;
        // Every frame and outside the gates below, because the ram box moves
        // while the wall is still untouched: Pose only runs once something has
        // been let go of, and an overlay that froze until the first brick fell
        // would draw the box where the tank used to be.
        _hulls?.Draw(Rig, _plan);
        if (Rig is not null)
        {
            // Every frame while anything is still moving, and one more after it
            // stops: a pose written on the frame the last body slept is a pile
            // drawn one step short of where it settled.
            if (Rig.Awake > 0 || Rig.Struck)
                Pose();
            return;
        }
        if (_fall is null)
            return;
        _clock += (float)(FrameClock.FixedStep ?? delta);
        Pose();
    }

    private void Rebuild()
    {
        if (_mesh is null)
            return;
        _mesh.InstanceCount = _plan.Blocks.Count;
        _ghostMesh!.InstanceCount = _plan.Blocks.Count;
        _shadeMesh!.InstanceCount = _plan.Blocks.Count;
        // Colour and custom data are Pose's, every slot of them: which slot a
        // piece sits in is decided by the draw order, so writing them by plan
        // index here would put a brick's tone and size on whichever piece later
        // took its place.
        Pose();
    }

    /// <summary>
    /// Where the pieces have actually come to rest, in the cell's own units.
    ///
    /// The same hexagon test the standing prop is fitted by, so "the wall fits"
    /// and "the rubble fits" are one measurement asked twice and cannot drift
    /// apart. Everything else here is what a still picture cannot answer: a heap
    /// of sixty bricks on a cell 109px tall reads as a mass whichever way it
    /// went, so whether it is lying down is a number and not a look.
    /// </summary>
    public (float Reach, float Top, int Aloft, int Layers) Pile()
    {
        float reach = 0.0f, top = 0.0f;
        int aloft = 0, deepest = 0;
        for (int i = 0; i < _plan.Blocks.Count; i++)
        {
            WallKit.Block b = _plan.Blocks[i];
            Transform3D f = Rig is not null ? Rig.At(i)
                            : _fall is null ? b.Frame
                            : WallFall.At(_fall[i], b, _clock);
            float lift = f.Origin.Y;
            top = Mathf.Max(top, lift);
            float thick = b.Half.Y * 2.0f;
            if (lift > thick * 1.5f)
                aloft++;
            deepest = Mathf.Max(deepest, Mathf.FloorToInt(lift / Mathf.Max(thick, 1e-4f)));
            for (int c = 0; c < 8; c++)
            {
                var corner = new Vector3(b.Half.X * ((c & 1) == 0 ? -1 : 1),
                                         b.Half.Y * ((c & 2) == 0 ? -1 : 1),
                                         b.Half.Z * ((c & 4) == 0 ? -1 : 1));
                // Through the turntable, because the cell does not turn with the
                // wall: spun, the prop covers a different part of it, and a reach
                // measured in the prop's own frame would go on reporting the pose
                // it was fitted in. Identity at rest, so the settled numbers are
                // the same to the bit.
                Vector3 w = new Basis(Vector3.Up, _spin) * (f * corner);
                float x = Mathf.Abs(w.X), z = Mathf.Abs(w.Z);
                reach = Mathf.Max(reach, Mathf.Max(z / (Mathf.Sqrt(3.0f) * 0.5f),
                                                   x + z / Mathf.Sqrt(3.0f)));
            }
        }
        return (reach, top, aloft, deepest + 1);
    }

    /// <summary>The thing that is doing the ramming, drawn in the prop's frame.
    ///
    /// It is here rather than on the body for the reason the rig's docstring
    /// gives - the bodies are in cell units and only this node knows how to put
    /// those on screen.
    ///
    /// <b>A real tank now, and the box is what is left when there is no
    /// atlas.</b> The box was a stand-in, and see-through so as not to hide the
    /// collapse it caused; a tank that is the board's own tank answers a question
    /// the box could only dodge, which is whether this wall reads as a wall
    /// beside the thing that drives at it. What it costs is honest and worth
    /// saying: from 270 the tank arrives on the camera's side and stands over the
    /// cell it has taken, so the middle of the rubble is behind it. The turntable
    /// is how you look at the rest.</summary>
    private void PoseRam()
    {
        if (Rig?.Ram() is not { } ram)
        {
            if (_ram is not null)
            {
                _ram.QueueFree();
                _ram = null;
            }
            if (_paint is not null)
            {
                _paint.QueueFree();
                _paint = null;
                _holder = null;
                _crew = null;
            }
            return;
        }
        // The contact point, not the middle of the box: a tank stands on the
        // ground, and every other thing on this board that is put somewhere is
        // put by where it touches it.
        Vector3 foot = ram.At.Origin - Vector3.Up * ram.Size.Y * 0.5f;
        if (Tank is null)
        {
            _ram ??= Ghost(ram.Size);
            _ram.Transform = ram.At;
            return;
        }
        if (_paint is null)
            Crew();
        _crew!.HullFacing = TankFacing;
        _crew.TurretFacing = TankFacing;
        _crew.QueueRedraw();
        // What brings the sprite's contact point to the middle of the target.
        // Undone by the holder rather than by moving the sprite, exactly as the
        // stage does it: the sprite draws every layer at -Anchor in its own
        // space, so its origin is the turret axis and moving it would move the
        // one point every layer is hung off.
        _holder!.Position = new Vector2(Stage3D.PaintSize, Stage3D.PaintSize) * 0.5f
                            - Tank.GroundOffset;
        // <b>The quad hangs off this node for its position and undoes it for its
        // orientation.</b> The tank is where the body is, which is a fact in the
        // prop's frame; the picture of it is a billboard for the board's camera,
        // which must not turn with the turntable. This node's basis is
        // <c>R(spin) * S(radius)</c> and a uniform scale commutes with a
        // rotation, so a child carrying <c>S(1/radius) * R(-spin)</c> comes out
        // with the identity for its basis and the right place for its origin -
        // and the mesh, being scaled back down by the same radius, is drawn in
        // board pixels, which is what the render target is measured in.
        _ram!.Transform = new Transform3D(
            Basis.FromScale(Vector3.One / Radius) * new Basis(Vector3.Up, -_spin),
            foot);
    }

    /// <summary>The box the ram used to be, kept for a bench with no atlas.
    /// See-through, because a stand-in that hides the collapse it caused is
    /// worse than no stand-in.</summary>
    private MeshInstance3D Ghost(Vector3 size)
    {
        var box = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = size },
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.17f, 0.19f, 0.15f, 0.45f),
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            },
        };
        AddChild(box);
        return box;
    }

    /// <summary>A tank in a render target and a quad to carry it, built the
    /// frame the ram starts and freed the frame it is cleared.
    ///
    /// <b>The target holds premultiplied colour, and that is why the blend mode
    /// is not the default.</b> Drawing into a transparent target with ordinary
    /// alpha leaves <c>rgb*a</c> in it, so displaying it with straight alpha
    /// multiplies by alpha a second time - a dark fringe round the silhouette.
    /// The stage says the same thing at greater length, and it is the same
    /// target.</summary>
    private void Crew()
    {
        _paint = new SubViewport
        {
            Size = new Vector2I(Stage3D.PaintSize, Stage3D.PaintSize),
            TransparentBg = true,
            Disable3D = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
            RenderTargetClearMode = SubViewport.ClearMode.Always,
        };
        AddChild(_paint);
        _holder = new Node2D();
        _paint.AddChild(_holder);
        _crew = new TankSprite { Atlas = Tank };
        _holder.AddChild(_crew);

        _ram?.QueueFree();
        _ram = new MeshInstance3D
        {
            Mesh = new QuadMesh
            {
                Size = new Vector2(Stage3D.PaintSize, Stage3D.PaintSize),
            },
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoTexture = _paint.GetTexture(),
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                BlendMode = BaseMaterial3D.BlendModeEnum.PremultAlpha,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest,
                // A tank stands on the board, so it goes on the board's own rung
                // for that - above the rubble it is standing in, which gave that
                // sort up. Said rather than left at the default, which is the
                // rung the rubble is on: a tie is settled by distance, and the
                // pile in front of a tank is nearer than the tank.
                RenderPriority = Stage3D.StandOrder,
            },
        };
        AddChild(_ram);
    }

    /// <summary>The round's own line, drawn in the prop's frame.
    ///
    /// <b>A hot core inside a dark sleeve, never additive.</b> The board's
    /// ground is pale - about 0.72 - and an additive line a few pixels across
    /// composites into slightly warmer grass, which is the lesson the harness's
    /// tracer is written from: a thin bright thing has to bring its own contrast
    /// rather than borrow the ground's. Two boxes rather than one shader,
    /// because two boxes is what it takes; the core is given the later render
    /// priority so the pair is ordered rather than left to whichever transparent
    /// surface the sorter picks, the two being concentric.
    ///
    /// <b>The length is the shot's, not a length chosen to look right.</b> It
    /// comes back from <see cref="WallRig.Beam"/>, which is the segment the
    /// solver actually used - so a round that stopped short cannot be drawn
    /// going through.</summary>
    private void PoseShot()
    {
        if (Rig?.Beam() is not { } beam)
        {
            _core?.QueueFree();
            _sleeve?.QueueFree();
            _core = null;
            _sleeve = null;
            return;
        }
        Vector3 span = beam.To - beam.From;
        float len = span.Length();
        if (len < 1e-5f)
            return;
        _core ??= Tracer(Core, CoreWide, 1);
        _sleeve ??= Tracer(Sleeve, SleeveWide, 0);
        var frame = new Transform3D(
            Basis.LookingAt(span / len, Vector3.Up),
            beam.From + span * 0.5f);
        Lay(_core, frame, len, CoreWide, Core, beam.Fade);
        Lay(_sleeve, frame, len, SleeveWide, Sleeve, beam.Fade * 0.8f);
    }

    /// <summary>Beam colours and thicknesses, the latter in fractions of a cell
    /// so they scale with the tile the way every other size on this bench does.
    /// A course piece is 0.099 of a cell tall, so the core is a fifth of a brick
    /// and the sleeve a little under half of one.</summary>
    private static readonly Color Core = new(1.0f, 0.93f, 0.62f);
    private static readonly Color Sleeve = new(0.10f, 0.07f, 0.05f);
    private const float CoreWide = 0.018f;
    private const float SleeveWide = 0.042f;

    private MeshInstance3D Tracer(Color tint, float wide, int priority)
    {
        var node = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(wide, wide, 1.0f) },
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = tint,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                RenderPriority = priority,
            },
        };
        AddChild(node);
        return node;
    }

    private static void Lay(MeshInstance3D node, in Transform3D frame, float len,
                            float wide, Color tint, float fade)
    {
        ((BoxMesh)node.Mesh).Size = new Vector3(wide, wide, len);
        node.Transform = frame;
        ((StandardMaterial3D)node.MaterialOverride).AlbedoColor =
            new Color(tint.R, tint.G, tint.B, Mathf.Clamp(fade, 0.0f, 1.0f));
    }

    /// <summary>
    /// Where each piece is, what colour it is and how much of it is left, written
    /// every frame.
    ///
    /// <b>Whole pieces go in the opaque mesh in plan order, and that is what
    /// keeps a standing wall the picture it was.</b> Measured: moving the whole
    /// wall into the transparent pass shifts 1 151 pixels along its own foot -
    /// the ground shadow's depth test against it changes the moment it leaves the
    /// opaque pass - so nothing that is not going pays for the going.
    ///
    /// <b>What is leaving goes in the blended mesh, farthest first.</b> Multimesh
    /// instances are drawn in index order and nothing sorts them, so an
    /// alpha-blended heap handed over in plan order blends its boxes in whatever
    /// order they were built: the failure the shadow's stencil exists to avoid,
    /// one buffer over. Sorted back to front the blend is right by construction,
    /// and it costs a sort of however many pieces are actually going.
    ///
    /// <b>The shadow stays in plan order and carries every piece</b>, whole, going
    /// or gone. It is a union under a stencil, so its order changes nothing, and a
    /// gone piece discards itself - keeping the order is what makes a standing
    /// wall's shadow the same pixels it always was.
    /// </summary>
    private void Pose()
    {
        PoseRam();
        PoseShot();
        Vector2 run = LocalRun();
        int n = _plan.Blocks.Count;
        if (_order.Length != n)
        {
            _order = new int[n];
            _depth = new float[n];
            _ghost = new Transform3D[n];
            _tone = new Color[n];
            _size = new Color[n];
        }
        int solid = 0, going = 0;
        for (int i = 0; i < n; i++)
        {
            WallKit.Block b = _plan.Blocks[i];
            Transform3D frame = Rig is not null ? Rig.At(i)
                                : _fall is null ? b.Frame
                                : WallFall.At(_fall[i], b, _clock);
            // The unit cube is scaled to the brick here rather than in the mesh,
            // so one mesh serves every piece. Scaling along a box's own axes
            // leaves its face normals pointing the same way, which is what lets
            // the shader normalise its way back to an honest normal.
            Basis box = frame.Basis * Basis.FromScale(b.Half * 2.0f);
            var pose = new Transform3D(box, frame.Origin);
            // The half extents travel with the instance because the joint band
            // has to be a width in board units and not in UV: a brick is twice as
            // long as it is deep, so a band measured in its own texture space
            // would be twice as wide down its side as across its end.
            var size = new Color(b.Half.X, b.Half.Y, b.Half.Z,
                                 b.Chip ? 1.0f : 0.0f);
            // How much of it is left, read every frame because it is the only
            // thing here that changes while nothing moves - see WallRig.Left. The
            // brick spends it as opacity and its shadow as coverage; a solved
            // flight has no rig and never goes.
            float left = Rig?.Left(i) ?? 1.0f;
            Color tone = Paint(b);

            _shadeMesh!.SetInstanceTransform(i, Flatten(box, frame.Origin, run));
            _shadeMesh.SetInstanceColor(i, new Color(1.0f, 1.0f, 1.0f, left));
            _shadeMesh.SetInstanceCustomData(i, size);

            if (left >= 1.0f && !Rubble(b.Course))
            {
                _mesh.SetInstanceTransform(solid, pose);
                _mesh.SetInstanceColor(solid, tone);
                _mesh.SetInstanceCustomData(solid, size);
                solid++;
                continue;
            }
            if (left <= 0.0f)
                continue;
            // Held rather than written straight through, because which slot it
            // ends up in is the depth order and that is not known until every
            // piece has been looked at.
            _order[going] = going;
            _ghost[going] = pose;
            _tone[going] = new Color(tone.R, tone.G, tone.B, left);
            _size[going] = size;
            going++;
        }
        Backwards(going);
        for (int k = 0; k < going; k++)
        {
            int was = _order[k];
            _ghostMesh!.SetInstanceTransform(k, _ghost[was]);
            _ghostMesh.SetInstanceColor(k, _tone[was]);
            _ghostMesh.SetInstanceCustomData(k, _size[was]);
        }
        // Only the slots written this frame are drawn, so a piece that has gone
        // leaves no stale pose behind it in either mesh.
        _mesh.VisibleInstanceCount = solid;
        _ghostMesh!.VisibleInstanceCount = going;
    }

    /// <summary>Whether a piece is rubble rather than masonry: the apron
    /// (<paramref name="course"/> below zero), and nothing else.
    ///
    /// <b>A piece let go of used to be rubble too, and that clause died with
    /// the burial it excused.</b> When the ram's box still swallowed what it
    /// overran, the heap lived inside the hull's own footprint, and honest
    /// depth against it was a coin toss reshuffled at every heading -
    /// measured, a heap the tank had just made was painted across its hull up
    /// to the turret ring. So everything loose was demoted to dressing and
    /// drawn under the tank wholesale. The shepherd ended the burial: the
    /// heap now stands before and beside the prow, which is exactly where a
    /// brick must occlude the hull that pushes it - and drawn as dressing it
    /// visibly sank behind the sprite instead, "the tank drives over the wall
    /// it is ploughing". A whole piece is masonry now, standing or shoved,
    /// and takes the depth buffer; what fades has to blend and stays in the
    /// dressing pass, which by then is a heap lying still on the ground.
    ///
    /// The apron keeps the old sentence: it lies inside the cell a tank
    /// parks on from the moment it is laid, which is the one place the
    /// billboard's single depth genuinely loses.</summary>
    public static bool Rubble(int course) => Dressed && course < 0;

    /// <summary>Whether rubble is dressing on the board rather than a solid on
    /// it. On, and named here rather than left in the field's initialiser for
    /// <c>Recoil.ShearOnByDefault</c>'s reason.</summary>
    public const bool DressedByDefault = true;

    /// <summary>Whether rubble is dressing - <c>--solid-rubble</c> on either
    /// bench puts back the wall whose every piece took the depth buffer, which is
    /// the A/B <see cref="Rubble"/> is judged by. Static for
    /// <see cref="WallRig.Crumbles"/>'s reason, and read while the materials are
    /// built rather than every frame: whether a shader writes depth is a render
    /// mode, compiled in and not set, which is <c>Stage3D.Tested</c>'s reason for
    /// keeping two of them.</summary>
    public static bool Dressed = DressedByDefault;

    /// <summary>Order the blended pieces back to front.
    ///
    /// <b>Which way is back comes from the camera and never from the board.</b>
    /// The prop turns on a turntable and the board's camera is tilted, so a
    /// direction worked out from the squash would be a second opinion about where
    /// the viewer is. No camera - the first frame of a scene - is not a reason to
    /// guess: nothing is going on that frame.</summary>
    private void Backwards(int going)
    {
        Camera3D? eye = going > 1 && IsInsideTree()
            ? GetViewport()?.GetCamera3D() : null;
        if (eye is null)
            return;
        Transform3D lens = eye.GlobalTransform;
        // Along the way the camera looks, so a bigger number is farther off. A
        // dot product rather than a distance because this board's camera is
        // orthographic: every fragment shares one view direction, and how far a
        // piece sits to the side of the lens is not how far away it is.
        Vector3 into = -lens.Basis.Z;
        Transform3D mine = GlobalTransform;
        for (int k = 0; k < going; k++)
            _depth[k] = (mine * _ghost[k].Origin - lens.Origin).Dot(into);
        Array.Sort(_depth, _order, 0, going);
        Array.Reverse(_order, 0, going);
    }

    /// <summary>Scratch for <see cref="Pose"/>: the blended pieces held aside
    /// until their order is known. Fields rather than locals because this runs
    /// every frame while a heap settles.</summary>
    private WallHulls? _hulls;
    private int[] _order = Array.Empty<int>();
    private float[] _depth = Array.Empty<float>();
    private Transform3D[] _ghost = Array.Empty<Transform3D>();
    private Color[] _tone = Array.Empty<Color>();
    private Color[] _size = Array.Empty<Color>();

    /// <summary>The same box, run down the sun onto the ground.
    ///
    /// <b>The shadow is the brick itself, flattened - not a shape drawn to look
    /// like one.</b> Squashing the y row to nothing collapses all six faces into
    /// the ground plane, and their union is exactly the silhouette the sun sees,
    /// so a turned brick throws a turned shadow and a brick on edge throws a thin
    /// one without any of that being written down. It is the trick the board
    /// already plays on a tree, where the basis column that carried height
    /// carries <see cref="Stage3D.SunCast"/> instead; the only difference is that
    /// here there is a solid to flatten rather than a billboard to shear.
    ///
    /// <b>No contact blot underneath, and that is a saving rather than an
    /// omission.</b> A prop gets one because its cast shadow is thrown by a
    /// picture standing on the ground, so it starts a crown's height away and
    /// leaves the roots bare - measured at 46px on <c>Tree_1</c>. A wall is
    /// geometry: its shadow starts at the course that is touching.</summary>
    private static Transform3D Flatten(in Basis solid, in Vector3 at, Vector2 run)
    {
        Vector3 Lay(Vector3 c) =>
            new(c.X + run.X * c.Y, 0.0f, c.Z + run.Y * c.Y);
        return new Transform3D(
            new Basis(Lay(solid.X), Lay(solid.Y), Lay(solid.Z)),
            new Vector3(at.X + run.X * at.Y, Lift, at.Z + run.Y * at.Y));
    }

    // --- the look --------------------------------------------------------------

    /// <summary>Brick colours, in the space the board composites in.
    ///
    /// Not the Blender material's numbers. Those are linear and feed a tone
    /// mapper; these are what that pipeline actually *produced*, measured off the
    /// render: body (127, 91, 62) against the reference photograph's (125, 91,
    /// 60). Copying the config across instead would be copying the input to a
    /// different machine and hoping for the same output.
    ///
    /// <b>Measured on the face that looks at the camera, and re-measured when the
    /// winding was fixed.</b> Wound inside out, the face on screen was the box's
    /// far interior wall - normal away from the sun, so ambient and nothing else.
    /// Turning the bricks the right way round lit that face and took the body to
    /// (140, 103, 66); these numbers are the palette scaled back onto the
    /// reference. A colour matched against a lighting bug is a colour that moves
    /// when the bug is fixed, which is the whole reason it is measured off the
    /// render each time rather than reasoned about.</summary>
    private static readonly Color Dark = new(0.526f, 0.353f, 0.244f);
    private static readonly Color Light = new(0.798f, 0.565f, 0.404f);
    private static readonly Color Pale = new(0.726f, 0.592f, 0.498f);
    private static readonly Color Moss = new(0.273f, 0.346f, 0.146f);

    private static Color Paint(in WallKit.Block b)
    {
        // A brick is one of a range, with the occasional pale one. Picked on the
        // CPU and carried per instance, because it never changes and a ramp
        // evaluated per fragment would be the same answer at a cost.
        Color body = Dark.Lerp(Light, b.Tone);
        if (b.Tone > 0.88f)
            body = body.Lerp(Pale, 0.7f);
        return body.Lerp(Moss, b.Moss * 0.45f);
    }

    /// <summary>One box, as a unit cube with hard normals. Hard because the
    /// shading is per face and a smoothed corner would blur the one cue that says
    /// a brick is a box.</summary>
    private static ArrayMesh Brick()
    {
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        Vector3[] n =
        {
            Vector3.Right, Vector3.Left, Vector3.Up,
            Vector3.Down, Vector3.Back, Vector3.Forward,
        };
        foreach (Vector3 face in n)
        {
            // Two in-plane axes for this face, in a right-handed order.
            Vector3 u = Mathf.Abs(face.Y) > 0.5f ? Vector3.Right : Vector3.Up;
            Vector3 v = face.Cross(u);
            Vector3 c = face * 0.5f;
            Vector3[] quad =
            {
                c - u * 0.5f - v * 0.5f, c + u * 0.5f - v * 0.5f,
                c + u * 0.5f + v * 0.5f, c - u * 0.5f + v * 0.5f,
            };
            // Clockwise, because that is the winding Godot calls front facing.
            // Wound the other way every brick renders inside out: the near face
            // and the top are culled and what is left is the far interior wall,
            // whose normal points away from the sun, so a solid box comes back a
            // flat card lit at nothing but ambient. The silhouette is identical
            // either way - a box hides its own inside - which is why this survived
            // a colour match against the reference. Caught by writing the normal
            // out as colour: 63 423 pixels of (0, 0, -1) and not one of (0, 1, 0).
            foreach (int k in new[] { 0, 2, 1, 0, 3, 2 })
            {
                st.SetNormal(face);
                st.AddVertex(quad[k]);
            }
        }
        return st.Commit();
    }

    /// <summary>
    /// The key the wall is lit by, in the flat plane's own space.
    ///
    /// <b>Not <see cref="Stage3D.Sun"/>, and that is not an inconsistency.</b>
    /// The board's sun sits <i>behind</i> the board on purpose, so that shadows
    /// come towards the viewer where they can be seen - its own docstring says so
    /// and names what is given up for it. The consequence for a solid is that
    /// every face pointing at the camera is the one face the sun never reaches:
    /// measured, the camera-facing side of a brick came back at pure ambient,
    /// which is most of the wall, unlit.
    ///
    /// The tanks are not lit by that sun either. They are rendered under
    /// <c>sprite_atlas</c>'s three-point rig, whose key sits on the camera's
    /// side, and <see cref="Stage3D.Sun"/> already states that the shadow's sun
    /// and the beauty rig's key "have always been separate objects". This is the
    /// key, in the same place the atlas puts it - the same height, the same side,
    /// and the opposite sign in Z - so the wall is lit like the tanks that stand
    /// beside it, and casts like the trees.
    /// </summary>
    private static readonly Vector3 Key =
        new Vector3(-0.329f, 0.819f, 0.470f).Normalized();

    /// <summary>How wide the dark line round a brick is, as a fraction of the
    /// cell - about 1.5px at a 248px cell.
    ///
    /// <b>Nearly all of it is hard and the feather is a third of a pixel.</b> The
    /// reference's outline is a line; the first pass eased the whole width and
    /// every brick came back a pillow, because at this size the gradient is most
    /// of the face rather than an edge on it.</summary>
    private const float Joint = 0.0125f;

    private ShaderMaterial Mortar(bool going = false)
    {
        var ink = new ShaderMaterial
        {
            Shader = new Shader
            {
                Code = !going ? MortarCode
                     : Dressed ? GhostCode : SolidCode,
            },
        };
        ink.SetShaderParameter("sun", Key);
        ink.SetShaderParameter("joint", Joint);
        ink.SetShaderParameter("radius", Radius);
        ink.RenderPriority = Order;
        return ink;
    }

    /// <summary>How far the shadow floats over the ground, as a fraction of the
    /// cell - a quarter of a pixel. Coplanar with the tile is a coin toss over
    /// which surface the depth test keeps, and it shows as a shadow eaten away in
    /// patches that move with the view: <c>hit_scar</c>'s finding, and the same
    /// answer.</summary>
    private const float Lift = 0.002f;

    /// <summary>Black at the board's own density, and inked once per pixel.
    ///
    /// <b>Ordinary blending is not a maximum, and here that is most of the
    /// picture.</b> Fifty-seven bricks throw overlapping shadows and a flattened
    /// box overlaps itself six times over, so at
    /// <see cref="Stage3D.ShadowInk"/>'s 0.45 two layers make 0.70 and three make
    /// 0.83 - measured on the first build at a median of 0.70, which is a pile
    /// sitting in a black hole of its own making. It is the same trap that stops
    /// <c>ground_shadow</c> drawing hull and turret separately, and the same one
    /// the board's own tree shadows are still living with on 3.6% of their
    /// ground.
    ///
    /// <b>The depth buffer cannot fix it, and that was the first answer.</b>
    /// Every shadow fragment lies in one plane, so a screen pixel has exactly one
    /// depth on it and it is tempting to let the depth test throw the duplicates
    /// away. It does not: depth rejects the *second* fragment only if the first
    /// was nearer, and a rejected fragment has already been blended when it is
    /// the other way round. Multimesh instances are not sorted, so half the
    /// overlaps land in the wrong order. Measured with <c>depth_draw_always</c>:
    /// still 0.70.
    ///
    /// <b>The stencil buffer is what answers "has this pixel been inked".</b>
    /// Draw where the stencil is not 1 and write 1 - one layer, whatever the
    /// order, and the union of the boxes comes out with no seams inside it. It
    /// costs nothing and needs no sorting. Measured: 0.451 against the tree
    /// shadows' 0.449, on the same 18 973 pixels the compounding version
    /// darkened.
    ///
    /// <b>Culling off, because a flattened box has no inside.</b> Its faces are
    /// degenerate and half of them wind backwards; the union of all six is the
    /// silhouette, so all six have to be drawn.</summary>
    private ShaderMaterial Shade()
    {
        var ink = new ShaderMaterial
        {
            Shader = new Shader { Code = ShadeCode },
        };
        // On the board's own rung for a cast shadow, which is under everything
        // that lies on the ground. Said rather than left at the default, for the
        // ram quad's reason: the rubble is on that default now, and a shadow that
        // settles against it by distance is a shadow that flickers as a piece
        // rolls past its own.
        ink.RenderPriority = Stage3D.ShadowOrder;
        ink.SetShaderParameter("ink", Stage3D.ShadowInk.A);
        return ink;
    }

    /// <summary>The brick as it is actually compiled while it is whole: opaque,
    /// writing depth, exactly the wall this board has always drawn.</summary>
    internal static string MortarCode => string.Format(MortarShader, "", "");

    /// <summary>
    /// The same text, blended.
    ///
    /// <b>A second material and never a second shader.</b> What is whole has to
    /// go on being opaque - see <see cref="Pose"/> - and what is leaving has to
    /// blend; every other line of the brick, the seam, the grain and the chip is
    /// the same statement, and two copies of it would drift exactly where nobody
    /// compares them: the tone of a fading brick against the one beside it.
    ///
    /// <b>And it writes no depth, which is what this mesh is now for.</b> It
    /// used to, on the argument that a fading brick still had to hide a tank
    /// behind it; that was written when the only thing in here was a piece part
    /// way through clearing itself, still standing in the wall. What is in here
    /// now is <see cref="Rubble"/> - the apron and everything let go - and rubble
    /// occluding a hull is the defect rather than the feature. It still <i>tests</i>
    /// depth, so masonry standing in front of a brick still hides it, and the
    /// pieces sort against each other by <see cref="Backwards"/> rather than by
    /// the buffer.
    /// </summary>
    internal static string GhostCode =>
        string.Format(MortarShader, ", depth_draw_never", "ALPHA = left;");

    /// <summary>The same again, taking the depth buffer - the wall as it was
    /// before rubble gave that up. <c>--solid-rubble</c>; see
    /// <see cref="Dressed"/>.</summary>
    internal static string SolidCode =>
        string.Format(MortarShader, ", depth_draw_always", "ALPHA = left;");

    /// <summary>The shadow, which takes no parameter: it was always blended.
    /// </summary>
    internal static string ShadeCode => ShadeShader;

    internal const string ShadeShader = @"
shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_never;
stencil_mode read, write, compare_not_equal, 1;

uniform float ink = 0.45;

varying float left;

void vertex() {
    left = COLOR.a;
}

void fragment() {
    // Gone at nothing, so the ground under it is the ground.
    if (left <= 0.0)
        discard;
    ALBEDO = vec3(0.0);
    // <b>Thinner with its brick, not smaller.</b> The piece spends its going as
    // opacity, so a shadow that spent it as coverage stayed full black in patches
    // under a brick that had almost gone - measured, and it read as the rubble
    // being a wireframe over its own shadow.
    //
    // <b>The stencil is what this leans on and what it costs.</b> The union takes
    // the first fragment at a pixel, so where two pieces overlap the ink is
    // whichever of them was laid first - exact while a heap goes together, which
    // is what a heap does, and a lighter patch inside a solid shadow when one
    // piece goes alone. Blending them instead compounds: two at 0.45 make 0.70,
    // which is the whole reason this pass is a stencil.
    ALPHA = ink * left;
}
";

    internal const string MortarShader = @"
shader_type spatial;
// Unshaded like everything else on this board, and opaque: it writes depth, so a
// tank behind it is occluded by it. That is the one thing a billboard prop on
// this board cannot do.
// Unshaded like everything else on this board. Opaque or alpha-blended by the
// same text - see WallStack.Mortar: what is whole writes depth and nothing else,
// which is what lets a tank behind it be occluded by it, and what is leaving
// blends and writes depth as well, so it keeps occluding while it goes.
render_mode unshaded, cull_back{0};

uniform vec3 sun = vec3(-0.33, 0.82, 0.47);
uniform float joint = 0.0125;
uniform float radius = 124.0;
// How much of the brick is there with the sun off it. Not zero: there is no
// bounce on this board, so an unlit face is whatever this says and nothing else.
uniform float ambient = 0.55;
uniform float grain = 0.10;

varying vec3 tint;
varying vec3 face;
varying vec3 axis;
varying vec3 local;
varying vec3 halfsz;
varying float chip;
varying float left;

float hash13(vec3 p) {{
    p = fract(p * 0.1031);
    p += dot(p, p.yzx + 33.33);
    return fract((p.x + p.y) * p.z);
}}

// Value noise in the brick's own space. Object space and never world: the
// pattern has to travel with the brick, or every piece re-textures itself on the
// way down - the Blender material's argument, and here it is the difference
// between rubble and a swarm.
float vnoise(vec3 p) {{
    vec3 i = floor(p), f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    float n = 0.0;
    for (int k = 0; k < 8; k++) {{
        vec3 o = vec3(float(k & 1), float((k >> 1) & 1), float((k >> 2) & 1));
        float w = mix(1.0 - f.x, f.x, o.x) * mix(1.0 - f.y, f.y, o.y)
                * mix(1.0 - f.z, f.z, o.z);
        n += w * hash13(i + o);
    }}
    return n;
}}

void vertex() {{
    tint = COLOR.rgb;
    local = VERTEX;                       // the unit box, -0.5 .. 0.5
    halfsz = INSTANCE_CUSTOM.xyz;
    chip = INSTANCE_CUSTOM.w;
    // The brick's own rotation, and nothing else to undo: the node's basis is a
    // uniform scale, so it leaves a direction pointing where it pointed. A
    // non-uniform one would not, and transforming a normal by it - rather than by
    // its inverse transpose - would be wrong on every face that is not axis
    // aligned.
    face = normalize(mat3(MODEL_MATRIX) * NORMAL);
    // And the same normal left alone, because the seam is measured in the
    // brick's own space and has to be masked in it. World and local agree while
    // the wall stands - every piece is yawed and little else - so this was
    // invisible until the solver laid one on its side: the mask then dropped the
    // wrong axis, every fragment came out a border, and the brick rendered
    // black. The comment below already names that failure; it just had the
    // second way of reaching it.
    axis = NORMAL;
    // How much of this piece is left - see Erosion. Carried in the colour''s
    // alpha because nothing else on this instance wanted it: the tone is picked
    // once on the CPU and the three half extents fill the custom data.
    left = COLOR.a;
}}

void fragment() {{
    // Gone at nothing, rather than eaten away in patches: the piece thins out.
    // Costs the depth pre-pass nothing here because the material writes depth
    // anyway, and costs the ordering nothing because the instances arrive sorted.
    if (left <= 0.0)
        discard;
    // One Lambert term, written rather than lit.
    float lam = max(dot(normalize(face), normalize(sun)), 0.0);
    float lit = ambient + (1.0 - ambient) * lam;

    // The dark line round the face. Measured out from the border in cell units,
    // so it is the same width down a brick's long side as across its end - which
    // a band in UV would not be.
    vec3 edge = (vec3(0.5) - abs(local)) * halfsz * 2.0;
    // The axis the face looks along is zero everywhere on that face, so it is the
    // one that has to be dropped - pushed out of the minimum rather than
    // branched on. Dropping the other two instead makes every fragment a border
    // and the wall comes back black, which is how this was found.
    vec3 keep = abs(axis) * 9.0;
    float border = min(min(edge.x + keep.x, edge.y + keep.y), edge.z + keep.z);
    // Asymmetric: the line is nearly hard on the outside and feathers inwards,
    // because a symmetric one at this width reads as a bevel.
    float seam = smoothstep(0.0, joint * 0.35, border);

    // Grain, in the brick's own space and scaled by the cell so it does not
    // change size when the wall is fitted to a different tile.
    float g = vnoise(local * halfsz * radius * 0.55) - 0.5;

    // <b>The dark line goes with the piece.</b> Alpha thins the body and the seam
    // alike, but a near-black line at a fifth of its opacity is still a line on
    // pale ground while the brick beside it has washed out - measured, and it read
    // as the heap turning into a wireframe of itself. Lifted by however much of
    // the piece has gone, the outline leaves before the mass does, which is the
    // way round that reads as masonry going rather than as a drawing of it.
    float show = max(seam, 1.0 - left);

    vec3 body = tint * lit * (1.0 + g * grain);
    // A chip is a broken piece: the inside of a brick is paler and rawer than
    // its weathered face, which is the one thing the procedural material in
    // Blender had to define rather than paint.
    body = mix(body, body * vec3(1.14, 1.10, 1.05), chip * 0.6);
    ALBEDO = mix(body * 0.13, body, show);
    {1}
}}
";
}
