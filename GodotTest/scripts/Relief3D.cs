using System;
using System.Globalization;
using System.IO;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The same hex board with height on it, drawn by an orthographic 3D camera
/// instead of by paint order - so the depth buffer answers what hides what.
///
/// Separate from <see cref="ReliefBench"/> for the reason that one is separate
/// from the harness: the thing being judged is a camera and a handful of
/// polygons, and judging them inside a bench that loads three atlases, a panel
/// and eighteen layers means waiting on everything that is not the question.
///
/// <b>What this is here to settle, and it is not what it was expected to
/// be.</b> The case this was built to win - a tank at the foot of a cliff, hull
/// behind the face and turret over its top - the 2D board already draws
/// correctly. Measured rather than argued: the harness at <c>--grade 0.6</c>
/// with a tank on (4,2) cuts it at the plateau's back edge, and so does this.
/// The claim that a per-cell repaint cannot express partial cover was wrong,
/// because a cell repainted over a tank is repainted <i>as its own hexagon</i>,
/// and that hexagon is the right occluder.
///
/// So the case for a depth buffer is not that it draws a picture the other one
/// cannot. It is what the other one costs to keep right:
/// <see cref="ReliefCap"/> is 140 lines that exist for nothing else, and beside
/// it sit <see cref="HexField.Occluders"/>, <see cref="HexField.VisibleSide"/>,
/// <see cref="HexField.Column"/>, <see cref="HexField.OnCell"/> and
/// <c>TrackMarks.PaintOn</c>, four of which arrived one reported picture at a
/// time. Here the count is <b>zero</b>: there is no occlusion code in this file,
/// because there is nothing to write.
///
/// And the ceiling that is real is the one <see cref="ReliefCap"/> already
/// records - it is per observer, so a wooded board repaints one cell dozens of
/// times and saturates its rim. A billboard has no such number: a tree is
/// another quad.
///
/// <b>The arithmetic already in the project is the 3D arithmetic.</b> That is
/// what makes this cheap rather than a rewrite, and it is worth writing down
/// because it reads like a coincidence and is not. <see cref="HexField.Depth"/>
/// keys on <c>row + lift*tan^2(e)</c> with both terms in screen px; substitute
/// <c>row = z*sin(e)</c> and <c>lift = y*cos(e)</c> and multiply by
/// <c>cot(e)</c>, and it is <c>z*cos(e) + y*sin(e)</c> - distance along the view
/// direction, which is exactly what the depth buffer stores. The 2D key is the
/// 3D depth sampled once per object. So the model, the levels, the grade and the
/// lift all carry over untouched; what is replaced is where the comparison
/// happens.
///
/// <b>One world unit is one screen pixel</b>, and the mapping is
/// <c>world = (screen x, height, ground row / sin(e))</c>. The ground row is
/// unsquashed on the way in and the camera squashes it back on the way out, so
/// the hexagon here is a true flat-top hexagon - <c>(+/-R, 0)</c> and
/// <c>(+/-R/2, +/-sqrt(3)R/2)</c>, no camera angle in it - where the 2D board
/// carries the squash in its corner table. Proven rather than asserted: see
/// <see cref="Check"/>, which unprojects every corner of every cell through the
/// real camera and compares it against the 2D formula.
///
/// Run it with the scene as the positional argument, leaving project.godot
/// alone:
///
///     Godot_v4.7.2-stable_mono_win64.exe --path GodotTest res://Relief3D.tscn
///
/// Keys: A/D column, W/S row, Q/E turn, [ / ] grade, T flat board, C the check
/// overlay, F12 a shot.
/// </summary>
public sealed partial class Relief3D : Node3D
{
    private static readonly string SpritesRoot = AssetRoot.Sprites;
    private static readonly string TerrainsRoot = AssetRoot.Terrains;

    /// <summary>The light one, <see cref="ReliefBench"/>'s reason: smallest of
    /// the three, so it is the one a step has the best chance of dwarfing.</summary>
    private const string TankTag = "LTP";

    /// <summary>The plate if no atlas loads, so a run with no art on disk still
    /// has the right board.</summary>
    private static readonly Vector2 FallbackTile = new(248.0f, 109.0f);

    /// <summary>How steep one level is, as a fraction of the distance to a
    /// neighbour - the harness's own default, so the two boards are the same
    /// board.</summary>
    public double StepGrade = 0.35;

    private const int Columns = 9;
    private const int Rows = 6;

    private int[] _levels = Array.Empty<int>();
    private AtlasSet? _atlas;
    private TerrainSet? _terrain;

    private Camera3D _camera = null!;
    private MeshInstance3D _tops = null!;
    private MeshInstance3D _sides = null!;
    private SubViewport? _paint;
    private TankPaint? _painter;
    private MeshInstance3D? _billboard;
    private Label _hud = null!;
    private CheckOverlay? _overlay;

    private Vector3 _pivot;
    private Vector2I _cell = new(4, 2);
    private double _facing = 270.0;
    private bool _flat;
    private bool _bare;
    private bool _art;
    private bool _showCheck;
    private string? _shotPath;
    private int _shotAfter = 3;
    private int _frames;
    private string _checkNote = "";

    // --- the numbers, all of them read off the tile ---------------------------

    private Vector2 Tile => _atlas is null ? FallbackTile : _atlas.HexRect.Size;

    /// <summary>sin(elevation), off the plate: a flat-top hexagon is
    /// (sqrt(3)/2)*sin(e) as tall as it is wide, so the art carries the camera.
    /// Same idiom as <see cref="HexField.Squash"/> and for its reason - a second
    /// copy of the elevation is a second thing to keep in agreement.</summary>
    private float Squash => 2.0f * Tile.Y / (Mathf.Sqrt(3.0f) * Tile.X);

    /// <summary>cos(elevation).</summary>
    private float RiseFactor => Mathf.Sqrt(Mathf.Max(0.0f, 1.0f - Squash * Squash));

    /// <summary>Ground px to any of the six neighbours: sqrt(3)*R, and equally
    /// the row pitch, which is why one grade serves every edge.</summary>
    private float Reach => Mathf.Sqrt(3.0f) * Tile.X * 0.5f;

    /// <summary>One level in world units - ground px, unsquashed. The screen
    /// lift the 2D board carries is this times <see cref="RiseFactor"/>, and
    /// nothing here needs that number: the camera applies it.</summary>
    private float StepUp => _flat ? 0.0f : (float)StepGrade * Reach;

    private static bool InBounds(Vector2I cell) =>
        cell.X >= 0 && cell.X < Columns && cell.Y >= 0 && cell.Y < Rows;

    private int LevelAt(Vector2I cell) =>
        !InBounds(cell) || _levels.Length == 0 ? 0 : _levels[cell.Y * Columns + cell.X];

    private (int Low, int High) LevelRange
    {
        get
        {
            int low = 0, high = 0;
            foreach (int level in _levels)
            {
                low = Math.Min(low, level);
                high = Math.Max(high, level);
            }
            return (low, high);
        }
    }

    /// <summary>Where a cell's top face sits in the world. Odd columns pushed
    /// down half a row, as everywhere else in the project.</summary>
    private Vector3 CellTop(Vector2I cell) => new(
        cell.X * Tile.X * 0.75f,
        LevelAt(cell) * StepUp,
        (cell.Y + (cell.X % 2 != 0 ? 0.5f : 0.0f)) * Reach);

    /// <summary>The hexagon's corners about a cell centre, in the ground plane.
    /// A true flat-top hexagon, with no squash and no sine in it - the camera
    /// does that. Right first and clockwise on screen, so corner i and i+1 span
    /// the edge facing <c>330 - 60i</c>, matching
    /// <see cref="HexField.Corners"/>.</summary>
    private Vector3[] Corners()
    {
        float r = Tile.X * 0.5f, s = Reach * 0.5f;
        return new[]
        {
            new Vector3(r, 0.0f, 0.0f), new Vector3(r * 0.5f, 0.0f, s),
            new Vector3(-r * 0.5f, 0.0f, s), new Vector3(-r, 0.0f, 0.0f),
            new Vector3(-r * 0.5f, 0.0f, -s), new Vector3(r * 0.5f, 0.0f, -s),
        };
    }

    // --- setup ---------------------------------------------------------------

    public override void _Ready()
    {
        ReadFlags();
        _levels = BoardMap.Bench.LevelsFor(Columns, Rows);
        _terrain = TerrainSet.Load(TerrainsRoot);
        GD.Print($"relief3d: {_terrain.Note}");

        try
        {
            _atlas = AtlasSet.Load(SpritesRoot, TankTag);
            GD.Print($"relief3d: {TankTag} tile {_atlas.HexRect.Size.X}"
                     + $"x{_atlas.HexRect.Size.Y}, {_atlas.Count} headings");
        }
        catch (Exception e)
        {
            GD.Print($"relief3d: no {TankTag} sprites ({e.Message}) - board only");
            _atlas = null;
        }

        Vector2 span = new(
            (Columns - 1) * Tile.X * 0.75f,
            (Rows - 1 + 0.5f) * Reach);
        _pivot = new Vector3(span.X * 0.5f, 0.0f, span.Y * 0.5f);

        BuildCamera();
        // The rim of a hexagon is geometry here, where in 2D it was the art's
        // own antialiased edge. Without this the board comes back with stepped
        // edges and it reads as the art having got worse.
        GetViewport().Msaa3D = Viewport.Msaa.Msaa4X;
        _tops = new MeshInstance3D();
        _sides = new MeshInstance3D();
        AddChild(_tops);
        AddChild(_sides);
        BuildBoard();
        BuildTank();

        var layer = new CanvasLayer();
        AddChild(layer);
        _overlay = new CheckOverlay { Bench = this, Visible = false };
        layer.AddChild(_overlay);
        _hud = new Label
        {
            Position = new Vector2(12, 8),
            Modulate = new Color(1.0f, 1.0f, 1.0f, 0.85f),
        };
        layer.AddChild(_hud);

        GD.Print($"relief3d: tile {Tile.X}x{Tile.Y}, squash {Squash:F4} "
                 + $"= {Mathf.RadToDeg(Mathf.Asin(Squash)):F2} deg, "
                 + $"rise {RiseFactor:F4}, reach {Reach:F1}, "
                 + $"step {StepUp:F1} world = {StepUp * RiseFactor:F1} screen px");
    }

    /// <summary>
    /// The camera, and every line of it is forced rather than chosen.
    ///
    /// Orthographic because the board's whole geometry - one lift for every
    /// level, one hexagon at every distance - is what a parallel projection
    /// does. <c>Size</c> is the viewport's height in pixels and
    /// <c>KeepAspect</c> is Height, which is what makes one world unit one
    /// screen pixel and lets every measurement already in the project stand.
    ///
    /// Pitched by the elevation the <i>tile</i> declares, not by 30 degrees:
    /// the plate measures 248x109, which is 30.50 degrees, and a camera set to
    /// the round number would disagree with the art by a pixel and a half across
    /// the board.
    ///
    /// <b>Pulled back along its own view direction, which an orthographic
    /// projection does not care about at all.</b> It only sets what is inside
    /// the near and far planes, so the distance is chosen to clear the board and
    /// nothing hangs on it - and the pivot, which is what the projection does
    /// care about, is left exactly on the board's centre.
    /// </summary>
    private void BuildCamera()
    {
        float height = GetViewport().GetVisibleRect().Size.Y;
        const float back = 4000.0f;
        _camera = new Camera3D
        {
            Projection = Camera3D.ProjectionType.Orthogonal,
            KeepAspect = Camera3D.KeepAspectEnum.Height,
            Size = height,
            Near = 1.0f,
            Far = back * 2.0f,
            Position = _pivot + new Vector3(0.0f, back * Squash, back * RiseFactor),
            RotationDegrees = new Vector3(-Mathf.RadToDeg(Mathf.Asin(Squash)),
                                          0.0f, 0.0f),
            Current = true,
        };
        AddChild(_camera);
    }

    /// <summary>
    /// Where the 2D board would draw a world point, as the reference the camera
    /// is judged against - not as something anything here uses to place
    /// anything.
    /// </summary>
    public Vector2 Screen2D(Vector3 world)
    {
        Vector2 centre = GetViewport().GetVisibleRect().Size * 0.5f;
        Vector3 d = world - _pivot;
        return centre + new Vector2(d.X, d.Z * Squash - d.Y * RiseFactor);
    }

    /// <summary>
    /// Every corner of every cell, unprojected through the real camera and
    /// compared against <see cref="Screen2D"/>.
    ///
    /// <b>This is the only thing in the file that can be silently wrong.</b> A
    /// camera off by a fraction of a degree draws a board that looks right and
    /// disagrees with every pixel measurement the project has taken; a camera
    /// off in its <c>Size</c> draws one that is right in the middle and wrong at
    /// the edges. Both read as the art being off, which is where the time would
    /// go.
    /// </summary>
    public float Check()
    {
        float worst = 0.0f;
        Vector3[] corner = Corners();
        for (int q = 0; q < Columns; q++)
        for (int r = 0; r < Rows; r++)
        {
            Vector3 top = CellTop(new Vector2I(q, r));
            foreach (Vector3 c in corner)
            {
                Vector3 world = top + c;
                worst = Mathf.Max(worst,
                    (_camera.UnprojectPosition(world) - Screen2D(world)).Length());
            }
        }
        return worst;
    }

    // --- the board -----------------------------------------------------------

    /// <summary>The cut earth, and the two shades that make a column read as a
    /// block rather than as a hexagon with a skirt. Lifted from
    /// <see cref="HexField"/> unchanged, because the two boards being the same
    /// board is the comparison.</summary>
    private static readonly Color Earth = new(0.42f, 0.30f, 0.20f);

    private static Color WallInk(int heading)
    {
        float k = heading is 270 or 90 ? 1.0f : 0.74f;
        return new Color(Earth.R * k, Earth.G * k, Earth.B * k);
    }

    private static Color TopInk(int level) => level switch
    {
        > 0 => new Color(0.55f, 0.62f, 0.38f),
        < 0 => new Color(0.38f, 0.45f, 0.32f),
        _ => new Color(0.47f, 0.55f, 0.35f),
    };

    /// <summary>
    /// One prism per cell, from its own top face down to the board's floor.
    ///
    /// <b>The column model survives the move and gets simpler.</b> In 2D it is
    /// what let paint order answer everything; here it is just what the ground
    /// is. And the three faces that had to be listed - <c>NearHeading</c>, and
    /// the note explaining that a side extruded from a far edge lands on the
    /// cell behind - are not listed at all: all six are built and the ones
    /// facing away are hidden by the cell's own top face, which is provably
    /// nearer by <c>(y_top - y)/sin(e)</c> at every screen point they share.
    ///
    /// One surface for the whole board, with the colour on the vertices, so it
    /// is one draw call and no material per cell.
    /// </summary>
    private void BuildBoard()
    {
        (int low, _) = LevelRange;
        float floor = low * StepUp;
        Vector3[] corner = Corners();
        Texture2D? art = GroundArt();

        var tops = new SurfaceTool();
        var sides = new SurfaceTool();
        tops.Begin(Mesh.PrimitiveType.Triangles);
        sides.Begin(Mesh.PrimitiveType.Triangles);

        for (int q = 0; q < Columns; q++)
        for (int r = 0; r < Rows; r++)
        {
            var cell = new Vector2I(q, r);
            Vector3 top = CellTop(cell);
            Color ink = art is null ? TopInk(LevelAt(cell)) : Colors.White;

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
            // Both sides, because nothing hangs on the winding: a face turned
            // away is behind its own cell's top at every point it could be seen
            // through, so culling here buys triangles and not correctness.
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
        _sides.MaterialOverride = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            VertexColorUseAsAlbedo = true,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
    }

    private Texture2D? GroundArt() =>
        !_art || _terrain is not { Any: true } t ? null
            : t.Texture(t.Has(TerrainSet.Default) ? TerrainSet.Default
                                                  : TerrainSet.Plain);

    /// <summary>
    /// Where a point of the top face samples the ground art, worked from the
    /// rectangle the 2D board draws that art in - so the two ask the same
    /// texture for the same pixel.
    ///
    /// <b>And this is where the art pays for the move.</b> The rect is
    /// <see cref="TerrainSet.Frame"/> and the hexagon only reaches
    /// <see cref="TerrainSet.Plate"/>, so everything the artist drew outside the
    /// plate - the grass over the back edge, the tuft off the front - is never
    /// sampled. In 2D that overhang is drawn and the cell in front paints over
    /// it, which is what makes the board look like ground rather than like
    /// tiles. A prism has an edge exactly where its hexagon ends.
    /// </summary>
    /// <summary>How far inside its own plate the top face stops sampling, in
    /// art pixels.
    ///
    /// <b>Measured, and it is the seam the overhang used to hide.</b> Sampled to
    /// the plate exactly, every cell came back framed in white: the art's
    /// hexagon is antialiased into a transparent surround, so the outermost
    /// texels are white at low alpha and a top face that reaches its own
    /// boundary reads them. In 2D nothing does, because the cell in front is
    /// drawn over that rim - which is the overhang doing a job nobody had
    /// written down.
    ///
    /// Six and not two: at two the frames were thinner and still there. At six
    /// the board reads as continuous ground with no cell boundary visible at
    /// all - better than the 2D board, which shows a faint outline at every
    /// seam. The art is 4x detail, so six of its pixels are one and a half of
    /// the tile's, and what is lost is a pixel and a half of grass at an edge
    /// that was never drawn sharp anyway.</summary>
    private const float Bleed = 6.0f;

    private Vector2 GroundUV(Vector3 fromCentre)
    {
        if (_terrain is not { Any: true } t || t.Plate.Size.X <= 0
            || t.Plate.Size.Y <= 0)
            return Vector2.Zero;
        // The corner offset as the 2D board would see it on screen: x straight
        // through, the ground row squashed by the camera.
        var screen = new Vector2(fromCentre.X, fromCentre.Z * Squash);
        float scale = t.ScaleTo(new Rect2I(Vector2I.Zero, (Vector2I)Tile));
        screen *= new Vector2(1.0f - 2.0f * Bleed / t.Plate.Size.X,
                              1.0f - 2.0f * Bleed / t.Plate.Size.Y);
        Vector2 plate = (Vector2)t.Plate.Position + (Vector2)t.Plate.Size * 0.5f;
        return (screen + plate * scale) / ((Vector2)t.Frame * scale);
    }

    // --- the tank ------------------------------------------------------------

    /// <summary>Margin round the sprite inside its own render target, so a
    /// frame that reaches its tile's edge is not clipped by it.</summary>
    private const int Margin = 8;

    /// <summary>
    /// The tank: its layers composited in 2D exactly as the harness composites
    /// them, into a render target, and that render target hung on one upright
    /// quad.
    ///
    /// <b>One quad and not one per layer</b>, and this is the whole of why the
    /// move is affordable. The sixteen layers sit at one anchor with blend modes
    /// between them - additive for the flash and the fire - and as sixteen
    /// coplanar quads in a 3D scene they have one depth between them and no
    /// order at all. Composited first, <see cref="TankSprite"/> and every clock
    /// under it carry on being exactly what they are and the scene sees a
    /// picture.
    ///
    /// <b>Upright, and stretched vertically by 1/cos(e).</b> A quad facing the
    /// camera square would have one depth over its whole surface, which is the
    /// scalar key again with extra steps. Standing it up in the world gives a
    /// pixel <c>dy</c> above the contact point the depth of a point <c>dy/cos(e)</c>
    /// off the ground, which is what it is a picture of; the stretch is there
    /// because the projection then squashes it back, and without it the tank
    /// lands 14% short.
    ///
    /// <b>Transparent, so it depth-tests and does not depth-write.</b> That is
    /// the line that keeps the silhouette smooth: an alpha-scissored sprite
    /// writes depth and comes back with a jagged rim, and the only thing the
    /// tank has to occlude is another tank, which is three quads sorted by
    /// distance - exactly what the 2D bench already does.
    /// </summary>
    private void BuildTank()
    {
        if (_atlas is null || _bare)
            return;
        Vector2I size = _atlas.Tile + new Vector2I(Margin * 2, Margin * 2);
        _paint = new SubViewport
        {
            Size = size,
            TransparentBg = true,
            Disable3D = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
            RenderTargetClearMode = SubViewport.ClearMode.Always,
        };
        AddChild(_paint);

        _painter = new TankPaint
        {
            Atlas = _atlas,
            Foot = new Vector2(Margin, Margin) + _atlas.Anchor + _atlas.GroundOffset,
        };
        _paint.AddChild(_painter);

        _billboard = new MeshInstance3D
        {
            Mesh = new QuadMesh { Size = new Vector2(size.X, size.Y / RiseFactor) },
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoTexture = _paint.GetTexture(),
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            },
        };
        AddChild(_billboard);
        PlaceTank();
    }

    /// <summary>
    /// The quad's centre, from where the contact point is drawn inside the
    /// render target.
    ///
    /// Worked from the contact point rather than from the anchor, for
    /// <see cref="HexField.GroundRow"/>'s reason: the anchor is the turret axis
    /// projected to screen and floats some 57px above the ground the tank stands
    /// on, so a quad hung off it is more than half a row out of place.
    /// </summary>
    private void PlaceTank()
    {
        if (_billboard is null || _paint is null || _painter is null)
            return;
        Vector3 contact = CellTop(_cell);
        Vector2 half = (Vector2)_paint.Size * 0.5f;
        Vector2 off = half - _painter.Foot;      // screen px, centre from contact
        _billboard.Position = new Vector3(contact.X + off.X,
                                          contact.Y - off.Y / RiseFactor,
                                          contact.Z);
        _painter.Facing = _facing;
        _painter.QueueRedraw();
    }

    // --- running -------------------------------------------------------------

    private void ReadFlags()
    {
        string[] args = OS.GetCmdlineUserArgs();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--shot" && i + 1 < args.Length)
                _shotPath = args[++i];
            else if (args[i] == "--shot-at" && i + 1 < args.Length
                     && int.TryParse(args[i + 1], out int at))
            {
                _shotAfter = at;
                i++;
            }
            else if (args[i] == "--grade" && i + 1 < args.Length
                     && double.TryParse(args[i + 1], NumberStyles.Float,
                                        CultureInfo.InvariantCulture, out double g))
            {
                StepGrade = g;
                i++;
            }
            else if (args[i] == "--stand" && i + 1 < args.Length)
            {
                string[] parts = args[++i].Split(',');
                if (parts.Length == 2 && int.TryParse(parts[0], out int q)
                    && int.TryParse(parts[1], out int r))
                    _cell = new Vector2I(q, r);
            }
            else if (args[i] == "--facing" && i + 1 < args.Length
                     && double.TryParse(args[i + 1], NumberStyles.Float,
                                        CultureInfo.InvariantCulture, out double f))
            {
                _facing = f;
                i++;
            }
            else if (args[i] == "--flat")
                _flat = true;
            // The board with nothing standing on it, so what the tank shows can
            // be counted by difference rather than by trying to tell a green
            // hull from green grass.
            else if (args[i] == "--no-tank")
                _bare = true;
            // The real ground on the top faces, which is the other half of the
            // comparison and the one that costs something.
            else if (args[i] == "--art")
                _art = true;
            else if (args[i] == "--check")
                _showCheck = true;
        }
    }

    public override void _Process(double delta)
    {
        if (_frames == 0)
        {
            float worst = Check();
            _checkNote = $"camera {worst:F4} px worst over "
                         + $"{Columns * Rows * 6} corners";
            GD.Print($"relief3d: {_checkNote}");
        }

        if (_overlay is not null)
            _overlay.Visible = _showCheck;
        _hud.Text = $"cell {_cell.X},{_cell.Y}  level {LevelAt(_cell)}  "
                    + $"facing {_facing:F0}\n"
                    + $"grade 1:{1.0 / Math.Max(0.001, StepGrade):F1}  "
                    + $"step {StepUp:F1} world = {StepUp * RiseFactor:F1} px\n"
                    + _checkNote
                    + (_flat ? "\nflat" : "");

        if (_shotPath is null || ++_frames <= _shotAfter)
        {
            _frames = Math.Max(_frames, 1);
            return;
        }
        Image image = GetViewport().GetTexture().GetImage();
        Error err = image.SavePng(_shotPath);
        GD.Print(err == Error.Ok ? $"shot: {_shotPath}"
                                 : $"shot to {_shotPath} failed: {err}");
        _shotPath = null;
        GetTree().Quit();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true, Echo: false } key)
            return;
        bool moved = true;
        switch (key.Keycode)
        {
            case Key.A: _cell.X = Math.Max(0, _cell.X - 1); break;
            case Key.D: _cell.X = Math.Min(Columns - 1, _cell.X + 1); break;
            case Key.W: _cell.Y = Math.Max(0, _cell.Y - 1); break;
            case Key.S: _cell.Y = Math.Min(Rows - 1, _cell.Y + 1); break;
            case Key.Q: _facing = (_facing + 345.0) % 360.0; break;
            case Key.E: _facing = (_facing + 15.0) % 360.0; break;
            case Key.Bracketleft:
                StepGrade = Math.Max(0.0, StepGrade - 0.05);
                Rebuild();
                break;
            case Key.Bracketright:
                StepGrade = Math.Min(1.0, StepGrade + 0.05);
                Rebuild();
                break;
            case Key.T:
                _flat = !_flat;
                Rebuild();
                break;
            case Key.C: _showCheck = !_showCheck; break;
            case Key.G:
                _art = !_art;
                Rebuild();
                break;
            case Key.F12:
                // out/ is gitignored scratch and may simply not be there, and
                // SavePng into a missing directory fails with a line in the log
                // that reads like a screenshot that did not work.
                Directory.CreateDirectory(AssetRoot.Out);
                GetViewport().GetTexture().GetImage()
                    .SavePng(AssetRoot.Out + "/relief3d.png");
                moved = false;
                break;
            default: moved = false; break;
        }
        if (moved)
            PlaceTank();
    }

    private void Rebuild()
    {
        BuildBoard();
        PlaceTank();
        _frames = 0;
    }

    /// <summary>The 2D formula drawn over the 3D board, one magenta outline per
    /// cell. The number from <see cref="Check"/> is the proof; this is what it
    /// looks like, and it is what catches a camera that is right at the pivot
    /// and wrong at the rim.</summary>
    private sealed partial class CheckOverlay : Node2D
    {
        public Relief3D Bench = null!;

        public override void _Process(double delta) => QueueRedraw();

        public override void _Draw()
        {
            Vector3[] corner = Bench.Corners();
            var ink = new Color(1.0f, 0.0f, 1.0f, 0.65f);
            for (int q = 0; q < Columns; q++)
            for (int r = 0; r < Rows; r++)
            {
                Vector3 top = Bench.CellTop(new Vector2I(q, r));
                var line = new Vector2[7];
                for (int i = 0; i < 6; i++)
                    line[i] = Bench.Screen2D(top + corner[i]);
                line[6] = line[0];
                DrawPolyline(line, ink, 1.0f);
            }
        }
    }

    /// <summary>
    /// The tank's layers, drawn into the render target the way
    /// <c>TankSprite.DrawLayer</c> draws them - the frame's own trimmed box
    /// moved by what came off the tile's top-left, so a packed set and an
    /// unpacked one land in the same place.
    ///
    /// Four layers and no clocks: what is being judged is where the sprite sits
    /// against the ground, and the belts, the plume and the fire all sit at the
    /// same anchor as the hull.
    /// </summary>
    private sealed partial class TankPaint : Node2D
    {
        public AtlasSet Atlas = null!;
        public Vector2 Foot;
        public double Facing = 270.0;

        public override void _Draw()
        {
            Vector2 origin = Foot - Atlas.GroundOffset;
            if (Atlas.HasShadow)
                Layer(AtlasSet.ShadowName, origin);
            Layer("hull", origin);
            foreach (string belt in AtlasSet.TrackNames)
                if (Atlas.Has(belt))
                    Layer(belt, origin);
            Layer("turret", origin);
            if (Atlas.Has(AtlasSet.BarrelName))
                Layer(AtlasSet.BarrelName, origin);
        }

        private void Layer(string layer, Vector2 origin)
        {
            int index = Atlas.FrameFor(Facing);
            Vector2 size = Atlas.SizeOf(layer, index);
            if (size.X <= 0.0f || size.Y <= 0.0f)
                return;
            DrawTextureRectRegion(Atlas.Texture(layer),
                new Rect2(origin - Atlas.Anchor + Atlas.OffsetOf(layer, index), size),
                Atlas.Region(layer, index));
        }
    }
}
