using System;
using System.Globalization;
using System.Collections.Generic;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// A board with nothing on it but the effect being judged and the four things a
/// burst can happen against.
///
/// Separate from the harness for <see cref="WoodBench"/>'s reason, and the same
/// reason again: what is judged here is what a burst looks like, and judging it
/// inside <see cref="Main"/> means three tanks, eighteen layers, sound, a panel
/// and fifty-four cells of terrain - on which the burst is one small bright
/// thing and most of the picture is something else.
///
/// Run it with the scene as the positional argument, leaving project.godot
/// alone:
///
///     Godot_v4.7.2-stable_mono_win64.exe --path GodotTest res://Effects.tscn
///
/// <b>The board is <see cref="BoardMap.Effects"/>: five by five standing a level
/// above the datum, a wood in one corner, a pond in the other and a ring of brick
/// in the middle.</b> It used to be flat plain ground everywhere, and that had a
/// stated reason - one plate under every burst, so an A/B moves nothing but the
/// burst. What it also had was every cell drawn as a flat hexagon with no side
/// faces at all, because the stage skips a prism's walls when the top and the
/// floor are the same height: a 3D stage looking exactly like the flat one. The
/// plinth fixes that, and the three features are there because a burst is judged
/// partly on what hides it and what it hides - and only brick, trees and water can
/// answer that. The cost is named in <see cref="BoardMap.Effects"/>: the mix is
/// forced by the wood, so the backdrop varies cell to cell and an A/B is worth
/// taking on one cell.
///
/// <b>It opens on the stage, like the wood bench, and for the harder half of
/// that reason.</b> A burst is a thing in the world at a height above a cell:
/// what hides it, what it stands in front of and how far it clears the ground
/// are all questions the depth buffer answers and a canvas cannot be asked. The
/// flat harness remains where the tank's own layers live - see
/// <see cref="ProcSmoke"/> on why the column had to be a canvas shader - so a
/// burst welded to a tank (an armour hit) belongs there and a burst standing on
/// the board belongs here. This bench is for the second kind.
///
/// <b>One burst is built: <see cref="ProcBlast"/>, the HE round in the
/// ground.</b> <c>SPACE</c> sets it off on the cell under the reticle, through
/// <see cref="Stage3D.Burst"/> - the same call the harness makes when a shell
/// lands in the field, so what this key shows is what a shot does. It is the
/// first of the family on purpose - the taxonomy in <c>docs/blast.md</c> has
/// three more bursts that are this one with other numbers and one addition each -
/// so what is being judged here is the vocabulary, not one effect.
/// </summary>
public sealed partial class EffectsBench : SceneRoot
{
    /// <summary>Eight frames: a burst is an event with a first frame, so the
    /// shot wanted of it is an early one - unlike the wood, where thirty frames
    /// are what it takes for a tree to be alight at all.</summary>
    public EffectsBench() : base(8) { }

    /// <summary>The atlas the tile comes off. The medium's, for the wood bench's
    /// reason: no tank is drawn here, and the atlas is loaded for
    /// <see cref="AtlasSet.HexRect"/>, which every distance on the board is
    /// measured in.</summary>
    private const string TileTag = "MTP";

    /// <summary>The cell the next burst goes on, unless a flag, a double click or
    /// the middle button says otherwise. The middle of the board, which is where
    /// the ring of brick stands - see <see cref="BoardMap.Effects"/>, which is now
    /// the only statement of how big this board is.</summary>
    public static readonly Vector2I Middle =
        new(BoardMap.Effects.Columns / 2, BoardMap.Effects.Rows / 2);

    private HexField _field = null!;
    private Stage3D? _stage;

    /// <summary>
    /// The board this bench stands on - <see cref="BoardMap.Effects"/>.
    ///
    /// <b>A map rather than a plot of plain cells, and the plinth in it is the
    /// point.</b> The stage skips a prism's walls when the top face and the floor
    /// are the same height, so a board flat on the datum draws as flat hexagons of
    /// ground: a 3D stage looking exactly like the 2D one. A level of plinth gives
    /// every cell its sides.
    /// </summary>
    private readonly BoardMap _map = BoardMap.Effects;

    /// <summary>The wood in the corner, the fire that can take it, the pond's
    /// drawn surface and its swell. Each is the same object the harness uses -
    /// a bench that reimplements what it stands on is measuring itself.</summary>
    private Grove? _grove;
    private Wildfire? _fire;
    private WaterArt? _surf;
    private Swell? _sea;
    private readonly List<WallProp> _walls = new();

    /// <summary>The marks bursts have left. Owned here and handed to the stage,
    /// the way the harness owns them: what has been dug is a fact about the
    /// board, and the stage is the thing that draws it.</summary>
    private readonly Craters _pits = new();

    private Camera2D _camera = null!;
    private Label? _hud;

    /// <summary>Where the burst will be. Kept as a cell rather than a point
    /// because that is what every board-level effect in this project is indexed
    /// by - the ash map, the fire, the wall - and a point would have to be
    /// rounded to one anyway.</summary>
    private Vector2I _at = Middle;

    private int _frame;

    /// <summary>Set the burst off on the first frame - <c>--fire</c>. A capture is
    /// evidence, and taking one of a burst must not need a hand on the keyboard:
    /// with the step pinned, <c>--capture-at</c> then names the age exactly, at
    /// 1/60s a frame.</summary>
    private bool _shoot;

    /// <summary>
    /// Put the CC0 pack's own ground explosion on the board instead of
    /// <see cref="ProcBlast"/>, at this many world units per pack unit -
    /// <c>--binbun &lt;scale&gt;</c>, and <c>--binbun-local</c> to simulate its
    /// particles in the emitter's own space so the scale reaches their speeds.
    ///
    /// <b>A flag on the bench and nothing else, which is what an A/B is.</b> The
    /// pack is authored in metres for a perspective camera; this board is one
    /// world unit to one screen pixel with a hex 248 of them wide, so what is
    /// being asked is not "is it good" but "does it survive the change of units,
    /// the orthographic camera and a board with no lights on it". That question
    /// is answered by two contact sheets at the same frames, not by reading the
    /// shaders.
    /// </summary>
    private float? _binbun;
    private bool _binbunLocal;
    private Node3D? _pack;
    private WorldEnvironment? _sky;

    /// <summary>What the view opens at. One, which is what the whole board
    /// costs: a cell is 248px wide, so five of them are 1240 in a 1600px window
    /// and the ground reaches the edges without any of the board being cropped.
    /// The wheel and <c>--zoom</c> go closer when the subject wants it - see
    /// <see cref="SceneRoot.ZoomAt"/>.</summary>
    private float HomeZoom => ZoomAt ?? 1.0f;

    private void ReadFlags()
    {
        string[] args = OS.GetCmdlineUserArgs();
        for (int i = 0; i < args.Length; i++)
        {
            // The five every root here takes - see SceneRoot.
            if (ReadCommonFlag(args, ref i))
                continue;
            if (args[i] == "--fire")
                _shoot = true;
            else if (args[i] == "--sheet")
                Sheet = true;
            else if (args[i] == "--might" && i + 1 < args.Length
                     && float.TryParse(args[i + 1], NumberStyles.Float,
                                       CultureInfo.InvariantCulture, out float sized))
            {
                // How big the burst is, for a capture of three calibres side by
                // side - the rule --fire and --at exist under: evidence must not
                // need a hand on the mouse.
                _might = Mathf.Clamp(sized, 0.25f, 3.0f);
                i++;
            }
            else if (args[i] == "--binbun" && i + 1 < args.Length
                     && float.TryParse(args[i + 1], NumberStyles.Float,
                                       CultureInfo.InvariantCulture, out float big))
            {
                _binbun = Mathf.Clamp(big, 1.0f, 400.0f);
                i++;
            }
            else if (args[i] == "--panel" && i + 1 < args.Length)
                _openGroup = args[++i];
            else if (args[i] == "--binbun-local")
                _binbunLocal = true;
            else if (args[i] == "--at" && i + 1 < args.Length)
            {
                // q,r, spelled the way the harness and the wood bench spell it:
                // a screenshot of a burst must not need a hand on the mouse.
                string spot = args[++i];
                string[] parts = spot.Split(',');
                if (parts.Length == 2
                    && int.TryParse(parts[0], out int q)
                    && int.TryParse(parts[1], out int r)
                    && OnBoard(q, r))
                    _at = new Vector2I(q, r);
                else
                    GD.PushWarning($"effects: --at {spot} is not a cell of this board");
            }
        }
        // A capture is evidence, so the step is pinned and the interface goes
        // away - the rule every root in this project applies.
        SettleForProof(CapturePath is not null);
    }

    /// <summary>Whether q,r names a cell of this board. Asked of the map rather
    /// than of the field, because the flags are read before the field is
    /// built.</summary>
    private bool OnBoard(int q, int r) =>
        BoardMap.Effects.OnBoard(new Vector2I(q, r));

    public override void _Ready()
    {
        ReadFlags();

        TerrainSet terrain = TerrainSet.Load(AssetRoot.Terrains);
        GD.Print("effects: terrain " + terrain.Note);

        AtlasSet? tile = null;
        try
        {
            tile = AtlasSet.Load(AssetRoot.Sprites, TileTag);
            GD.Print($"effects: tile off {TileTag}, "
                     + $"{tile.HexRect.Size.X}x{tile.HexRect.Size.Y}");
        }
        catch (Exception e)
        {
            // Said out loud rather than left to be a blank window: without a
            // tile there is no distance on the board at all, so the field draws
            // nothing and the stage returns before it builds.
            GD.PushWarning($"effects: no {TileTag} atlas ({e.Message}) - no board");
        }

        PropSet props = PropSet.Load(AssetRoot.Props);
        GD.Print("effects: props " + props.Note);

        _field = new HexField
        {
            Atlas = tile,
            Terrain = terrain,
            // The map's own paint - see BoardMap.Effects on why it is the plain
            // plate and not the mix.
            Paint = _map.Paint,
            Trees = props.Any,
            Columns = _map.Columns,
            Rows = _map.Rows,
            Plot = _map.Plot,
        };
        _field.SetKinds(_map.Kinds);
        _field.SetRelief(_map.Levels, _map.Ramps);
        // After the relief and never before it, the tank bench's reason word for
        // word: the water's guards are asked about levels and ramps, so water laid
        // on a board with no heights yet is water judged against a board that does
        // not exist.
        _field.SetWater(_map.Water);
        AddChild(_field);

        _grove = new Grove
        {
            Field = _field, Props = props, Origin = Vector2.Zero,
            Enabled = props.Any,
        };
        AddChild(_grove);
        // After the wood, because what can catch is what is standing - see
        // Grove.Carrying. Nothing lights it yet; a burst that sets a wood alight is
        // the next thing this board is for.
        _fire = new Wildfire
        {
            Field = _field, Enabled = true, Wooded = _grove.Carrying,
        };
        _grove.Fire = _fire;

        _camera = new Camera2D
        {
            Zoom = new Vector2(HomeZoom, HomeZoom),
            Enabled = true,
            // On the cell the burst will be on rather than on the middle of the
            // board, which is the same thing until <c>--at</c> is given and then is
            // not: a capture of a burst on the pond framed on the brick is a
            // capture of the brick. The reset puts it back on the middle, because
            // that is what a reset means.
            Position = _field.CellCentre(_at),
        };
        AddChild(_camera);

        // The stage owns the board from here, so the canvas answer is put away:
        // the field's own hexagons would be a canvas item over the entire 3D
        // world, tanks and all - the trap Main.Suppress2D exists for.
        if (tile is not null)
        {
            _grove.Plant();
            GD.Print("effects: grove " + _grove.Note());
            _surf = WaterArt.Load(AssetRoot.Water, tile.HexRect);
            GD.Print("effects: water " + _surf.Note);
        }
        _sea = new Swell { Field = _field };

        _stage = new Stage3D
        {
            Field = _field, Origin = Vector2.Zero, Eye = _camera,
            Wood = _grove, Blaze = _fire, Surf = _surf, Sea = _sea,
        };
        AddChild(_stage);
        // Both canvas answers put away, WoodBench's pair and for its reasons: the
        // field's own hexagons would be a canvas item over the whole 3D world, and
        // the grove's nodes would draw every tree twice.
        _field.ShowField = false;
        _grove.Visible = false;

        // The bursts themselves belong to the stage - it is the 3D world, and a
        // burst is a thing standing in it. What is handed over here is the board's
        // half: where the ground has been dug.
        _stage.Pits = _pits;
        // Every burst opens on whatever the panel says, whichever of the six it
        // turns out to be - see EffectsBench.Panel. Both kinds, because both
        // pools live in the stage at once and either key may be the one pressed.
        _stage.Dress = Dress;
        _stage.Attire = Attire;
        _stage.Scar = Scar;

        if (!NoUi)
        {
            // One layer for both, and it goes in the tree before either of them:
            // a CanvasLayer that is never a child of anything is a layer nothing
            // draws and nothing processes, which is a panel that is simply absent
            // with no error to say so.
            var layer = new CanvasLayer();
            AddChild(layer);
            _hud = new Label { Position = new Vector2(16.0f, 12.0f) };
            _hud.AddThemeColorOverride("font_color", new Color(0.92f, 0.95f, 1.0f));
            _hud.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.85f));
            _hud.AddThemeConstantOverride("outline_size", 5);
            layer.AddChild(_hud);
            Panel(layer);
        }

        // After the stage, because a wall wants the ground it stands on and the
        // triangles come off the stage - the tank bench's ordering.
        Brick();

        if (_shoot)
            Burst();
    }

    /// <summary>
    /// The ring of brick in the middle.
    ///
    /// <b>Six sides on one cell</b>, which is <see cref="TankBench.RingSides"/>'s
    /// own arrangement: what the map marks with <c>W</c> is a cell, and what
    /// stands on it is one <see cref="WallProp"/> whose recipe runs the whole way
    /// round. Nothing rams it here - there is no tank on this bench - so what it is
    /// for is to be something a burst happens next to: brick is the one thing on
    /// this board with a vertical face, and a burst is judged partly on what it
    /// hides and what hides it.
    /// </summary>
    private void Brick()
    {
        if (_stage is null || _field.Atlas is null)
            return;
        foreach (Vector2I cell in _map.Walled())
        {
            var prop = new WallProp
            {
                Field = _field,
                Stage = _stage,
                Cell = cell,
                Recipe = new WallKit.Recipe { Sides = TankBench.RingSides },
                Borrow = null,
                // Its own collision channel per wall, the tank bench's reason: every
                // rig is centred on the world origin, so two on one bit share a
                // phantom space.
                Channel = _walls.Count,
            };
            AddChild(prop);
            prop.Bearing = HexField.EdgeHeadings[0];
            prop.Build();
            _walls.Add(prop);
        }
        if (_walls.Count > 0)
            GD.Print($"effects: {_walls.Count} ring(s) of brick");
    }

    /// <summary>
    /// Set a burst off on the cell under the reticle.
    ///
    /// <b>Through the stage's own pool rather than a burst of this bench's
    /// own</b>, which is the rule every bench here runs under: a bench that
    /// reimplements what it judges is a bench measuring itself, and two copies
    /// agree until the first edit lands in one of them. The harness makes the
    /// same call from <see cref="TankTick.Landed"/>, so what this key shows is
    /// what a shell does.
    ///
    /// Seated on the cell's own hexagon and the level under it, so a burst on a
    /// step stands on the step rather than at sea level.
    /// </summary>
    private void Burst()
    {
        if (_binbun is float scale)
        {
            Pack(scale);
            return;
        }
        if (_stage is null)
            return;
        // Which of the two this scene is about. The board, the crater and the
        // hook are the same either way; what differs is which pool the shot comes
        // out of - see EffectsBench.Sheet on why that is a scene and not a
        // setting.
        Vector2 spot = _stage.Origin + _field.CellCentre(_at);
        float lift = _field.LevelAt(_at) * _field.Lift;
        if (Sheet)
        {
            _stage.Boom(spot, lift);
            // <b>And the computed one too, when asked.</b> The imported cloud is
            // smoke and fire and nothing else - no ground rings, no cone of thrown
            // earth - and those are what the other burst is best at. Whether the
            // answer is one effect or the flipbook cloud wearing the other's
            // ground layer is a question about a picture, so the bench can draw
            // the picture: see docs/blast.md.
            if (_pair)
                _stage.Burst(spot, lift);
        }
        else
            _stage.Burst(spot, lift);
    }

    /// <summary>
    /// Stand the pack's explosion where the reticle is, at this board's size, and
    /// play it.
    ///
    /// <b>Its numbers are multiplied rather than its root scaled, and that is
    /// measured rather than chosen.</b> A scale on the root does not reach a GPU
    /// particle at all: with the pack at 25x its quads still came out half a world
    /// unit across - two pixels - against a magenta marker sphere at the same
    /// point that drew correctly at its full 24. <c>local_coords</c> changes
    /// nothing about it either. So the pack cannot be fitted to a board where a
    /// hex is 248 units by putting a number on its transform; every length inside
    /// it has to be walked and multiplied, which is what <see cref="Swell"/> does.
    ///
    /// Loaded with the cache ignored, so each shot gets its own copy of every
    /// process material and mesh: they are sub-resources shared by every instance
    /// of a PackedScene, and multiplying shared ones would scale the pack again on
    /// every press of the key.
    /// </summary>
    private void Pack(float scale)
    {
        if (_stage is null)
            return;
        _pack?.QueueFree();
        var made = ResourceLoader.Load<PackedScene>(
            PackPath, cacheMode: ResourceLoader.CacheMode.Ignore);
        if (made is null)
        {
            GD.PushWarning($"effects: no pack at {PackPath}");
            return;
        }
        var node = made.Instantiate<Node3D>();
        _pack = node;
        _stage.AddChild(node);
        // The pack's own y is up in metres; the board's is up in pixels through
        // the same Trunk a tree and a burst both stand by, so the transform is
        // theirs and the size is ours - see Swell.
        Vector2 spot = _stage.Origin + _field.CellCentre(_at);
        float lift = _field.LevelAt(_at) * _field.Lift;
        node.Transform = Stage3D.Trunk(spot, lift, 0.0f, _field.Squash,
                                       _field.RiseFactor);
        int touched = Swell(node, scale);

        // <b>Ambient light, because two of the pack's shaders are lit and this
        // board has no lights at all.</b> Every shader the stage draws with is
        // unshaded - the art arrived lit - so there is no sun in the 3D world and
        // no environment either. The pack's smoke and its bits' trail are
        // ordinary lit materials, and with nothing to light them they render
        // black: measured, a black sheet over half the board. An environment with
        // only ambient in it lights those two and cannot touch anything of ours,
        // which is unshaded by construction.
        if (_sky is null)
        {
            _sky = new WorldEnvironment
            {
                Environment = new Godot.Environment
                {
                    BackgroundMode = Godot.Environment.BGMode.Canvas,
                    AmbientLightSource = Godot.Environment.AmbientSource.Color,
                    AmbientLightColor = new Color(1.0f, 0.95f, 0.9f),
                    AmbientLightEnergy = 1.4f,
                },
            };
            _stage.AddChild(_sky);
            // And a sun, because ambient is not enough: the pack's smoke has its
            // own light() function, and light() is only ever called for a real
            // light - ambient never reaches it. With none in the world the smoke
            // came out a black silhouette with hard edges, which is a lit material
            // multiplied by no light. Aimed down the board's own declared sun so
            // the pack is lit from where everything else here is lit from.
            _stage.AddChild(new DirectionalLight3D
            {
                Transform = new Transform3D(
                    Basis.LookingAt(-Stage3D.Sun, Vector3.Up), Vector3.Zero),
                LightEnergy = 1.6f,
                ShadowEnabled = false,
            });
        }

        // <b>Played from here rather than by the pack's own controller.</b> Its
        // script is GDScript with a class_name, and a class_name only exists once
        // the editor has scanned it into global_script_class_cache - so in a
        // headless run the script fails to parse ("Could not find base class
        // VFXControllerBB"), nothing calls play(), every emitter stays off and the
        // board comes out empty. The AnimationPlayer under it is what the
        // controller drives anyway, so driving it directly costs nothing and drops
        // the GDScript out of the picture entirely.
        if (node.GetNodeOrNull<AnimationPlayer>("AnimationPlayer") is { } reel)
            reel.Play("main");
        else
            GD.PushWarning("effects: the pack has no AnimationPlayer to play");

        GD.Print($"effects: pack at {scale}x, {touched} number(s) multiplied");
    }

    /// <summary>
    /// Multiply every length inside an instantiated pack by
    /// <paramref name="by"/>, and say how many numbers were touched.
    ///
    /// Velocities, accelerations, gravity, damping and emission radii live in the
    /// <c>ParticleProcessMaterial</c>; sizes live in the draw meshes. Both have to
    /// move together or the burst is the right size and crawls, or the right speed
    /// and is made of dust motes. The curves are left alone on purpose: they are
    /// multipliers over a particle's life, so they scale with what they multiply.
    ///
    /// The count is printed because it is the only cheap way to notice that a pack
    /// update added an emitter this does not know about: a burst that comes out
    /// half-scaled looks like a burst that was authored small.
    /// </summary>
    private static int Swell(Node node, float by)
    {
        int hits = 0;
        if (node is GpuParticles3D puff)
        {
            // The box the system is culled by, which is in the emitter's own
            // space: left alone, a burst three cells wide is clipped away the
            // moment its first clod leaves a four-unit cube.
            Aabb box = puff.VisibilityAabb;
            puff.VisibilityAabb = new Aabb(box.Position * by, box.Size * by);
            hits++;
            if (puff.ProcessMaterial is ParticleProcessMaterial mat)
            {
                mat.Gravity *= by;
                mat.InitialVelocityMin *= by;
                mat.InitialVelocityMax *= by;
                mat.LinearAccelMin *= by;
                mat.LinearAccelMax *= by;
                mat.RadialAccelMin *= by;
                mat.RadialAccelMax *= by;
                mat.TangentialAccelMin *= by;
                mat.TangentialAccelMax *= by;
                mat.DampingMin *= by;
                mat.DampingMax *= by;
                mat.ScaleMin *= by;
                mat.ScaleMax *= by;
                mat.EmissionSphereRadius *= by;
                mat.EmissionBoxExtents *= by;
                mat.EmissionShapeOffset *= by;
                hits += 15;
            }
            for (int pass = 0; pass < puff.DrawPasses; pass++)
                if (Grow(puff.GetDrawPassMesh(pass), by))
                    hits++;
        }
        else if (node is OmniLight3D lamp)
        {
            lamp.OmniRange *= by;
            hits++;
        }
        foreach (Node child in node.GetChildren())
            hits += Swell(child, by);
        return hits;
    }

    /// <summary>One draw mesh at the board's size. Every primitive the pack draws
    /// with, and nothing else: an unknown mesh comes back false so the count says
    /// so.</summary>
    private static bool Grow(Mesh? mesh, float by)
    {
        switch (mesh)
        {
            case SphereMesh ball:
                ball.Radius *= by;
                ball.Height *= by;
                return true;
            case CylinderMesh tube:
                tube.TopRadius *= by;
                tube.BottomRadius *= by;
                tube.Height *= by;
                return true;
            case TubeTrailMesh trail:
                trail.Radius *= by;
                trail.SectionLength *= by;
                return true;
            case QuadMesh flat:
                flat.Size *= by;
                return true;
            default:
                return false;
        }
    }

    private const string PackPath =
        "res://assets/BinbunVFX_Vol2/ExplosionFX/effects/ground/"
        + "vfx_ground_explosion_01.tscn";

    public override void _Process(double delta)
    {
        delta = FrameClock.FixedStep ?? delta;
        // Every frame and with no vehicles: Place is where the stage does its
        // own per-frame work, so a frame that skipped it is a frame the board
        // stood still in.
        _stage?.Place(Array.Empty<Vehicle>());
        QueueRedraw();

        if (_hud is not null)
            _hud.Text = Note();

        _frame++;
        if (CapturePath is not null && _frame >= CaptureAt)
        {
            Capture(CapturePath);
            GetTree().Quit();
        }
    }

    /// <summary>
    /// The reticle: where the next burst goes.
    ///
    /// Drawn on the canvas, which means it lands over the whole 3D board rather
    /// than in it. That is right for a reticle and wrong for anything else this
    /// bench will draw - it is a mark on the window, not a thing on the ground,
    /// and the bursts themselves belong in the stage where the depth buffer can
    /// hide them.
    /// </summary>
    public override void _Draw()
    {
        if (_field.Atlas is null)
            return;
        Vector2 at = _field.CellCentre(_at);
        var ink = new Color(1.0f, 0.85f, 0.35f, 0.85f);
        const float arm = 14.0f, gap = 5.0f;
        DrawLine(at + new Vector2(-arm, 0.0f), at + new Vector2(-gap, 0.0f), ink, 1.0f);
        DrawLine(at + new Vector2(gap, 0.0f), at + new Vector2(arm, 0.0f), ink, 1.0f);
        DrawLine(at + new Vector2(0.0f, -arm), at + new Vector2(0.0f, -gap), ink, 1.0f);
        DrawLine(at + new Vector2(0.0f, gap), at + new Vector2(0.0f, arm), ink, 1.0f);
    }

    /// <summary>What the bench is showing, and - while nothing is built - what
    /// it is not. A bench whose readout claims a subject it does not draw is the
    /// one failure a bench cannot have.</summary>
    private string Note()
    {
        if (_field.Atlas is null)
            return "no board";
        // Named as the experiment it is, because it does not work: a bench whose
        // readout claims a subject it does not draw is the one failure a bench
        // cannot have, and what --binbun draws today is a black silhouette.
        if (_binbun is float packed)
            return $"binbun pack at {packed}x on {_at.X},{_at.Y}\n"
                   + "its smoke renders black here - see docs/blast.md\n"
                   + "SPACE re-fires, R resets, F12 shoots";
        return $"board {_map.Columns}x{_map.Rows}, wood, pond and a ring of brick, "
               + (Sheet ? "imported (flipbook) burst" : "computed burst")
               + $" at {_at.X},{_at.Y}\n"
               + $"{_pits.Pits.Count} crater(s) of {Craters.Capacity}\n"
               + "SPACE fires, double click a cell to fire on it,\n"
               + "TAB the panel, middle click a cell to aim,\n"
               + "middle drag pans, wheel zooms, R resets, F12 shoots";
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        // <b>A double click on the left sets a burst off on that cell.</b> A single
        // one would be worse than nothing here: it is the gesture every other bench
        // uses for "take this tank" and "go there", and a click that detonates on
        // the way past is a click that cannot be used to aim and look. A double
        // click cannot be made by accident, and it does both things at once - the
        // reticle moves and the shell lands.
        if (@event is InputEventMouseButton
            { ButtonIndex: MouseButton.Left, DoubleClick: true } twice)
        {
            Vector2I cell = _field.CellAt(_field.ToLocal(twice.Position));
            // Not clamped, by the middle button's argument: a click off the board
            // is a click off the board, and clamping would blow up the cell beside
            // the one that was pointed at.
            if (_field.InBounds(cell))
            {
                _at = cell;
                Burst();
            }
            return;
        }
        // One button doing two things with the gesture deciding which, resolved
        // on release because that is the earliest moment the two are
        // distinguishable - SceneRoot.MiddleTapped.
        if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Middle } middle)
        {
            if (!MiddleTapped(middle, out Vector2 tapped))
                return;
            Vector2I cell = _field.CellAt(_field.ToLocal(tapped));
            // Not clamped, for the wood bench's reason: a click off the plot is
            // a click off the board, and clamping it would aim at the cell
            // beside the one that was pointed at.
            if (_field.InBounds(cell))
                _at = cell;
            return;
        }
        if (@event is InputEventMouseMotion motion
            && (motion.ButtonMask & MouseButtonMask.Middle) != 0)
        {
            _camera.Position -= motion.Relative / _camera.Zoom;
            return;
        }
        if (@event is InputEventMouseButton { Pressed: true } wheel)
        {
            float factor = wheel.ButtonIndex switch
            {
                MouseButton.WheelUp => 1.25f,
                MouseButton.WheelDown => 0.8f,
                _ => 1.0f,
            };
            if (factor != 1.0f)
            {
                float zoom = Mathf.Clamp(_camera.Zoom.X * factor, 0.25f, 8.0f);
                _camera.Zoom = new Vector2(zoom, zoom);
            }
            return;
        }
        if (@event is not InputEventKey { Pressed: true, Echo: false } key)
            return;
        switch (key.Keycode)
        {
            case Key.Space:
                Burst();
                break;
            case Key.R:
                // The bench as it opened: the reticle back in the middle and the
                // view back home.
                _at = Middle;
                // Both halves of a burst: the ones in the air and the marks the
                // ones before them left. A reset that leaves the craters is a
                // board that was not reset.
                _stage?.Quench();
                _pits.Fill();
                _camera.Zoom = new Vector2(HomeZoom, HomeZoom);
                _camera.Position = _field.CellCentre(Middle);
                break;
            case Key.Tab:
                _panel?.Flip();
                break;
            case Key.F12:
                Capture($"{AssetRoot.Out}/effects_{Time.GetTicksMsec()}.png");
                break;
        }
    }
}
