using System.Collections.Generic;
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
        new(flat.X, lift / RiseFactor, flat.Y / Mathf.Max(Squash, 0.0001f));

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
        AddChild(_tops);
        AddChild(_sides);
        AddChild(_ring);
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
            // Unshaded because the sprite is already lit - it was rendered with
            // its own three-point rig - and nearest because at 1:1 a resampled
            // atlas is the one thing that would make these look worse than the
            // 2D bench draws them.
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                AlbedoTexture = paint.GetTexture(),
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            },
        };
        AddChild(quad);
        _stands[vehicle] = new Stand
        {
            Paint = paint, Holder = holder, Quad = quad, Home = home,
        };
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
        _tops.MaterialOverride = null;
        _sides.MaterialOverride = null;
        _ring.MaterialOverride = null;
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
    /// </summary>
    private static ArrayMesh Body(Vector2 lead, float squash, float rise)
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

        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        float top = ay + half / rise;
        Face(st, Vector3.Back, 0.0f, seam,
             new Vector3(ax - half, top, 0.0f), new Vector3(ax + half, top, 0.0f),
             new Vector3(ax + half, 0.0f, 0.0f), new Vector3(ax - half, 0.0f, 0.0f));
        var along = new Vector3(0.0f, 0.0f, reach);
        Face(st, Vector3.Up, seam, 1.0f,
             lie + new Vector3(ax - half, 0.0f, 0.0f),
             lie + new Vector3(ax + half, 0.0f, 0.0f),
             lie + new Vector3(ax + half, 0.0f, 0.0f) + along,
             lie + new Vector3(ax - half, 0.0f, 0.0f) + along);
        return st.Commit();
    }

    /// <summary>Four corners as two triangles, the image running from
    /// <paramref name="top"/> to <paramref name="bottom"/> down the pair of edges
    /// they are given in. Written out rather than <c>QuadMesh</c> because four
    /// vertices can be read, and a primitive whose orientation rules have to be
    /// recalled is one more thing to suspect the next time a quad comes back the
    /// wrong shape.</summary>
    private static void Face(SurfaceTool st, Vector3 normal, float top,
                             float bottom, params Vector3[] corner)
    {
        Vector2[] uv = { new(0, top), new(1, top), new(1, bottom), new(0, bottom) };
        foreach (int i in new[] { 0, 1, 2, 0, 2, 3 })
        {
            st.SetUV(uv[i]);
            st.SetNormal(normal);
            st.AddVertex(corner[i]);
        }
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
            if (lead != stand.Lead)
            {
                stand.Quad.Mesh = Body(lead, Squash, RiseFactor);
                stand.Lead = lead;
            }
            stand.Quad.Position = Contact(vehicle);
        }
        // The mark goes where the tank touches the ground, every frame, for the
        // reason the 2D ring follows the contact patch: a tank spends most of an
        // order between two cells, and a mark on the cell behind it reads as the
        // selection lagging.
        if (Selected is not null)
            _ring.Position = Contact(Selected);
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
            .Append('@').Append(Field.Lift.ToString("F3"));
        for (int q = 0; q < Field.Columns; q++)
        for (int r = 0; r < Field.Rows; r++)
        {
            var cell = new Vector2I(q, r);
            note.Append(Field.LevelAt(cell)).Append(Field.InkFor(cell).ToRgba32())
                .Append(';');
        }
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

    private Vector3 CellTop(Vector2I cell)
    {
        float lift = Field.LevelAt(cell) * Field.Lift;
        Vector2 flat = Origin + Field.FlatAnchor(cell) + Field.CentreOffset;
        return World(flat, lift);
    }

    private static readonly Color Earth = new(0.42f, 0.30f, 0.20f);

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
        tops.Begin(Mesh.PrimitiveType.Triangles);
        sides.Begin(Mesh.PrimitiveType.Triangles);

        Texture2D? art = GroundArt();
        for (int q = 0; q < Field.Columns; q++)
        for (int r = 0; r < Field.Rows; r++)
        {
            var cell = new Vector2I(q, r);
            Vector3 top = CellTop(cell);
            Color ink = Field.InkFor(cell);
            if (art is null)
                ink = new Color(SoilInk.R * ink.R, SoilInk.G * ink.G,
                                SoilInk.B * ink.B);

            for (int i = 1; i + 1 < 6; i++)
            foreach (int k in new[] { 0, i, i + 1 })
            {
                tops.SetColor(ink);
                tops.SetUV(GroundUV(corner[k]));
                tops.AddVertex(top + corner[k]);
            }

            float drop = top.Y - floor;
            if (drop <= 0.0f)
                continue;
            var down = new Vector3(0.0f, -drop, 0.0f);
            for (int i = 0; i < 6; i++)
            {
                Vector3 a = top + corner[i], b = top + corner[(i + 1) % 6];
                Color side = WallInk((330 - 60 * i + 360) % 360);
                foreach (Vector3 v in new[] { a, b, b + down, a, b + down, a + down })
                {
                    sides.SetColor(side);
                    sides.AddVertex(v);
                }
            }
        }

        tops.GenerateNormals();
        sides.GenerateNormals();
        _tops.Mesh = tops.Commit();
        _sides.Mesh = sides.Commit();
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
    }

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
        _ring.MaterialOverride = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            VertexColorUseAsAlbedo = true,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            // Before the tanks, and it has to be said rather than left to the
            // depth buffer: a tank tests depth but does not write it - which is
            // what keeps its silhouette smooth - so two transparent surfaces
            // cannot settle each other and the order is whatever the sort says.
            // It said the ring, and the far arm was drawn straight across the
            // hull. This is the same statement the 2D ring makes with its z of
            // -99: the ground goes under everything standing on it.
            RenderPriority = -1,
        };
    }

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
}
