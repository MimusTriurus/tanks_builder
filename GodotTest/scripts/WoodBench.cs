using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// One hex with one of every tree on it, and a middle mouse button.
///
/// Separate from the harness for <see cref="ReliefBench"/>'s reason, which is
/// <c>flash_bench.sheet()</c>'s: what is being judged here is what a tree does
/// while it burns, and judging it inside <see cref="Main"/> means three atlases,
/// eighteen layers, sound, a panel and a board of fifty-four cells - on which the
/// tree in question is one of four hundred and most of the picture is something
/// else. Here the whole subject is four trunks.
///
/// Run it with the scene as the positional argument, leaving project.godot
/// alone:
///
///     Godot_v4.7.2-stable_mono_win64.exe --path GodotTest res://Wood.tscn
///
/// <b>Every moving part of it is the harness's own.</b> <see cref="Wildfire"/>
/// holds the clocks, <see cref="Grove"/> reads them onto the trees,
/// <see cref="Stage3D"/> draws the flame, the smoke and the ash. A bench that
/// reimplemented any of the three would be a bench measuring itself - the
/// argument the panel is a view of the harness for, and not a second copy of it.
/// What is written here is the board, the button and the readout.
///
/// <b>It opens on the stage, and that is not a preference.</b> The fire is only
/// ever drawn there: in 2D a <see cref="PropNode"/> paints its art and nothing
/// else, so a <see cref="Wildfire.Coat"/> reaches the screen through
/// <see cref="Stage3D"/>'s billboards, their two quads of flame and smoke, and
/// the ash march in the ground shader. A 2D wood bench would be a wood that
/// cannot burn.
///
/// <b>One cell, which a grid of columns and rows cannot be.</b> The board is a
/// 1x1 rectangle, and the single cell is named through <see cref="HexField.Plot"/>
/// all the same rather than left implicit: the plot is what says where the board
/// stops, and a bench that agreed with it by accident would stop agreeing the day
/// the board grew. Nothing borders it, so the fire has nowhere to spread - what is
/// on show is a tree catching, charring, swapping to its burnt art and standing in
/// its own ash, which is the half of the fire that belongs to a tree rather than
/// to the grid.
///
/// <b>One of each kind, not a wood</b> - see <see cref="Grove.Specimen"/>. In a
/// wood the species come off a hash, so which of the four is standing anywhere is
/// a fact about that cell and not about the folder, and the one that got no roll
/// is the one nobody looks at. Here all four are up at once and the undergrowth
/// is not, because bushes and stones on a cell about trunks are trunks harder to
/// see.
///
/// <b>No tanks, and that costs one measurement and one effect.</b>
/// <see cref="Grove.KeepOut"/> is set from the widest hull in the harness and
/// keeps its tuned 83 here, which is the clearing every board renders - so a tree
/// stands where a tree on a harness cell stands, in the same 24px ring round the
/// same clearing. What is genuinely absent is the fade,
/// <see cref="Grove.Reveal"/>: nothing stands on the cell, so nothing ghosts.
/// </summary>
public sealed partial class WoodBench : Node2D
{
    private static readonly string TerrainsRoot = AssetRoot.Terrains;
    private static readonly string PropsRoot = AssetRoot.Props;
    private static readonly string SpritesRoot = AssetRoot.Sprites;

    /// <summary>The atlas the tile comes off. The medium's, for the harness's
    /// reason: the grid is one grid, so it cannot follow a selection, and the
    /// reference tank is the one drawn as rendered. No tank is drawn here - the
    /// atlas is loaded for <see cref="AtlasSet.HexRect"/>, which every distance
    /// on the board is measured in.</summary>
    private const string TileTag = "MTP";

    /// <summary>The one cell on the board. An even column, so the stagger that
    /// pushes the odd files down half a row is not in the picture - on a board of
    /// one cell it would only mean the hexagon sat half a row off the view's
    /// centre.</summary>
    public static readonly Vector2I Middle = new(0, 0);

    /// <summary>The board, as the set the field is told to keep. One entry, and
    /// it is still said rather than assumed: <see cref="HexField.Plot"/> is what
    /// the fire asks before it spreads and what the wood asks before it sows, so
    /// the bench and the field have one answer between them and not two.</summary>
    public static HashSet<Vector2I> Board(Vector2I mid) => new() { mid };

    private PropSet? _props;
    private HexField _field = null!;
    private Grove? _grove;
    private Wildfire? _fire;
    private Stage3D? _stage;
    private Camera2D _camera = null!;
    private Label? _hud;

    /// <summary>What the view opens at. Not one, which is what the harness opens
    /// at and right for a board fourteen columns wide: one cell is 248 screen px
    /// of board in a 1600px window, so at 1:1 the subject is a sixth of the
    /// picture and the rest is backdrop. Three, because a tree is 126px tall and
    /// its burnt art is drawn line by line - the state this bench is for is the
    /// one that wants looking at closely. The atlas is still sampled 1:1 - the
    /// zoom moves the view and never the sprites - and the wheel and --zoom reach
    /// the rest.</summary>
    private float _zoomAt = 3.0f;
    private string? _capturePath;
    private int _captureAt = 30;
    private int _frame;
    private Vector2I? _lightAt;

    /// <summary>Hand the burnt picture over on one frame, the way it used to be
    /// done. The A/B the crossfade is judged by, and the whole case for it -
    /// <c>--hard-swell</c>'s reason, and it is the same shape of switch: not a
    /// third thing the tree could look like, just the window closed. Zero rather
    /// than a short window, so the frame the state arrives on is the frame the
    /// picture arrives on and nothing is left to round.</summary>
    private bool _hardSwap;

    /// <summary>Wear the tank's own rendered fire instead of the procedural one -
    /// <see cref="Stage3D.TankFire"/>. Off by default because it is the probe and
    /// the procedural one is the baseline it is measured against; a comparison whose
    /// two halves both need a flag is a comparison nobody makes.</summary>
    private bool _tankFire;

    /// <summary>Whether the wood can catch at all - <c>--no-tree-fire</c>, the
    /// harness's own flag and here for the harness's reason. On this bench it is
    /// also the only honest baseline: every number the fire is judged by is a
    /// per-pixel difference against the same board not burning, and without it the
    /// nearest thing available is a green wood, whose trees differ by their char as
    /// well.</summary>
    private bool _woodFire = true;

    /// <summary>The two shadows the board throws - <c>--no-cast-shadows</c> and
    /// <c>--no-prop-contact</c>, the harness's own flags and its own reasons.
    ///
    /// Here because the shadow is part of what a burning tree does now: it hands
    /// over from the living picture to the burnt one along with the crown, so the
    /// A/B that judges the handover needs the cast on its own. Without them the
    /// footprint cannot be masked off at all, which is what an empty mask of 0px
    /// said the first time it was asked for.</summary>
    private bool _cast = true;
    private bool _contact = true;

    /// <summary>How much fire there is, in tree heights - <c>--flame</c> and
    /// <see cref="Stage3D.FlameRise"/>. Null leaves the tuned value alone.
    ///
    /// A flag rather than a rebuild for the reason every other one here is: this is
    /// the number the picture is judged on, and taking the same shot twice at two
    /// values should not need a hand on the mouse. It is also the only size in the
    /// flame - every other number about its shape is a ratio to it - so a sweep of
    /// this is a sweep of the whole effect rather than of one of its parts.</summary>
    private float? _flameRise;

    private bool _noUi;
    private bool _showUi;

    private void ReadFlags()
    {
        string[] args = OS.GetCmdlineUserArgs();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--capture" && i + 1 < args.Length)
                _capturePath = args[++i];
            else if (args[i] == "--capture-at" && i + 1 < args.Length
                     && int.TryParse(args[i + 1], out int at))
            {
                _captureAt = at;
                i++;
            }
            else if (args[i] == "--burn-wood" && i + 1 < args.Length)
            {
                // q,r, spelled the way the harness spells it: a screenshot of a
                // burning wood must not need a hand on the mouse.
                string spot = args[++i];
                string[] parts = spot.Split(',');
                if (parts.Length == 2
                    && int.TryParse(parts[0], out int q)
                    && int.TryParse(parts[1], out int r))
                    _lightAt = new Vector2I(q, r);
                else
                    GD.PushWarning($"--burn-wood {spot} is not q,r");
            }
            else if (args[i] == "--zoom" && i + 1 < args.Length
                     && float.TryParse(args[i + 1], NumberStyles.Float,
                                       CultureInfo.InvariantCulture, out float zoom))
            {
                // Invariant and never the machine's: on a comma locale the plain
                // parse fails, the default stands, and two runs at two zooms come
                // back byte for byte identical - which reads as a flag that does
                // nothing rather than one that was not understood.
                _zoomAt = Mathf.Clamp(zoom, 0.25f, 8.0f);
                i++;
            }
            else if (args[i] == "--hard-swap")
                _hardSwap = true;
            else if (args[i] == "--tank-fire")
                _tankFire = true;
            else if (args[i] == "--no-tree-fire")
                _woodFire = false;
            else if (args[i] == "--no-cast-shadows")
                _cast = false;
            else if (args[i] == "--no-prop-contact")
                _contact = false;
            else if (args[i] == "--flame" && i + 1 < args.Length
                     && float.TryParse(args[i + 1], NumberStyles.Float,
                                       CultureInfo.InvariantCulture, out float lit))
            {
                _flameRise = Mathf.Clamp(lit, 0.05f, 2.0f);
                i++;
            }
            else if (args[i] == "--no-ui")
                _noUi = true;
            else if (args[i] == "--ui")
                _showUi = true;
        }
        // A capture is evidence, so the step is pinned for the reason it is
        // pinned over there: two runs of the same flags have to be diffable, and
        // the pond taught this file's neighbour that a step kept local is a step
        // the siblings never see. Stage3D reads this very field.
        if (_capturePath is not null)
        {
            Main.FixedStep = 1.0 / 60.0;
            // A capture is evidence, and an A/B of two renders must not differ by
            // a readout that happened to be up - the harness's rule for its panel.
            // --ui brings it back, for the shot of the bench itself.
            _noUi = !_showUi;
        }
    }

    public override void _Ready()
    {
        ReadFlags();

        TerrainSet terrain = TerrainSet.Load(TerrainsRoot);
        GD.Print("wood: terrain " + terrain.Note);
        PropSet props = PropSet.Load(PropsRoot);
        GD.Print("wood: props " + props.Note);
        _props = props;

        AtlasSet? tile = null;
        try
        {
            tile = AtlasSet.Load(SpritesRoot, TileTag);
            GD.Print($"wood: tile off {TileTag}, "
                     + $"{tile.HexRect.Size.X}x{tile.HexRect.Size.Y}");
        }
        catch (Exception e)
        {
            // Said out loud rather than left to be a blank window: without a
            // tile there is no distance on the board at all, so the field draws
            // nothing and the stage returns before it builds - see its _Process.
            GD.PushWarning($"wood: no {TileTag} atlas ({e.Message}) - no board");
        }

        _field = new HexField
        {
            Atlas = tile,
            Terrain = terrain,
            // Forest everywhere, because every cell here is the subject. The mix
            // would make some of them soil, and a bench whose board is partly not
            // what it is about has fewer cells than it says it has.
            Paint = TerrainSet.Forest,
            Trees = props.Any,
            Columns = 1,
            Rows = 1,
            Plot = Board(Middle),
        };
        AddChild(_field);

        _grove = new Grove
        {
            Field = _field, Props = props, Origin = Vector2.Zero,
            Enabled = props.Any,
            // One of each tree instead of a wood - the whole point of the cell.
            Specimen = true,
        };
        AddChild(_grove);
        // After the wood, because what can catch is what is standing - see
        // Grove.Carrying. A cell whose trees all fell to the budget's ceiling has
        // nothing to burn, and a fire on it would blacken a field.
        _fire = new Wildfire
        {
            Field = _field, Enabled = _woodFire, Wooded = _grove.Carrying,
        };
        _grove.Fire = _fire;

        _camera = new Camera2D
        {
            Zoom = new Vector2(_zoomAt, _zoomAt),
            Enabled = true,
            // The cell's own hexagon, not its anchor: the anchor is the turret
            // axis floating some sixty pixels above the ground plane, and a view
            // parked on it sits the board low in the window.
            Position = ViewHome,
        };
        AddChild(_camera);

        if (tile is not null)
        {
            _grove.Plant();
            GD.Print("wood: grove " + _grove.Note());
            // and again, now that there is something standing to measure - see
            // ViewHome. Written rather than deferred to the next frame, because a
            // capture may be taken on this one.
            _camera.Position = ViewHome;
        }

        // The stage owns the board from here, so both canvas answers are put
        // away - the pair Main.Suppress2D puts away, and for its reasons: the
        // field's own hexagons would be a canvas item over the entire 3D world,
        // and the grove's nodes would draw every tree twice, once lying flat and
        // once standing up.
        _stage = new Stage3D
        {
            Field = _field, Origin = Vector2.Zero, Eye = _camera,
            Wood = _grove, Blaze = _fire, TankFire = _tankFire,
            CastShadows = _cast, ContactShadows = _contact,
        };
        if (_flameRise is float high)
            _stage.FlameRise = high;
        AddChild(_stage);
        _field.ShowField = false;
        _grove.Visible = false;

        if (!_noUi)
        {
            var layer = new CanvasLayer();
            AddChild(layer);
            _hud = new Label { Position = new Vector2(16.0f, 12.0f) };
            _hud.AddThemeColorOverride("font_color", new Color(0.92f, 0.95f, 1.0f));
            _hud.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.85f));
            _hud.AddThemeConstantOverride("outline_size", 5);
            layer.AddChild(_hud);
        }

        if (_hardSwap)
            _fire.SwapFor = 0.0f;
        if (_lightAt is Vector2I spark && !_fire.Light(spark))
            GD.PushWarning($"--burn-wood {spark.X},{spark.Y} has no wood on it");
    }

    public override void _Process(double delta)
    {
        delta = Main.FixedStep ?? delta;
        // The board's state first and the trees' reading of it second, which is
        // the order the harness runs these three in: the fire is a state of the
        // board, and the coat on a trunk is a view of that state.
        _fire?.Tick(delta);
        _grove?.Smoulder();
        _grove?.Blow(delta);
        // Every frame, and with nothing to place: Place is also where the stage
        // rebuilds the wood's billboards, so a frame that skipped it is a frame
        // the wind stopped in and the flame did not move.
        _stage?.Place(Array.Empty<Vehicle>());

        if (_hud is not null)
            _hud.Text = Note();

        _frame++;
        if (_capturePath is not null && _frame >= _captureAt)
        {
            Capture(_capturePath);
            GetTree().Quit();
        }
    }

    /// <summary>
    /// What the fire is doing, in four numbers rather than one.
    ///
    /// The harness prints the same four, and for the same reason: a wood nobody
    /// lit, a fire that took one cell and stopped, a front that has passed
    /// leaving nothing alight, and a burn that never ends are one still picture
    /// apiece.
    /// </summary>
    private string Note()
    {
        if (_fire is null || _grove is null)
            return "no board";
        (int burning, int burnt) = _grove.Ablaze();
        // Which species are standing, and not only how many props: the cell is
        // one of each, so a kind that found no spot it fits is the failure this
        // bench is most likely to have - and a count of four says nothing about
        // which four.
        string kinds = _props is null || _grove.Planted == 0
            ? "nothing planted"
            : string.Join(", ", _grove.Standing
                .OrderBy(t => t.Species)
                .Select(t => _props.NameOf(t.Species)));
        return $"{kinds}\n"
               + $"fire {_fire.Scorched}c/{burning}t/{burnt}ash\n"
               + "middle click the cell to set it alight\n"
               + "middle drag pans, wheel zooms, R resets, F12 shoots";
    }

    /// <summary>
    /// Where the view sits: the cell, raised by half of whatever is standing on
    /// it.
    ///
    /// Measured off the planted props rather than set to a number, and that is
    /// the whole of it: a hexagon is 109px tall and a tree is 126, so a view
    /// centred on the ground puts the cell in the middle of the window and the
    /// crowns off the top of it. Half the tallest rise centres what is actually
    /// on screen, and it follows the art - the day a taller tree is drawn, the
    /// view moves with it instead of cropping it.
    ///
    /// Answers the bare cell before anything is sown, which is what a board with
    /// no prop art shows anyway.
    /// </summary>
    private Vector2 ViewHome
    {
        get
        {
            Vector2 centre = _field.CellCentre(Middle);
            float rise = 0.0f;
            foreach (PropNode prop in _grove?.Standing
                                      ?? (IReadOnlyList<PropNode>)Array.Empty<PropNode>())
                rise = Mathf.Max(rise, prop.Rise);
            return centre - new Vector2(0.0f, rise * 0.5f);
        }
    }

    /// <summary>Where the middle button went down, or null if it is up.</summary>
    private Vector2? _middleFrom;

    public override void _UnhandledInput(InputEvent @event)
    {
        // One button doing two things with the gesture deciding which, resolved
        // on release because that is the earliest moment the two are
        // distinguishable - Main.MiddleTap's arrangement, and its own slack, so
        // that a pan across the wood does not leave a line of fires behind it.
        if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Middle } middle)
        {
            if (middle.Pressed)
            {
                _middleFrom = GetGlobalMousePosition();
                return;
            }
            Vector2? from = _middleFrom;
            _middleFrom = null;
            if (from is not Vector2 down
                || !Main.MiddleTap(down, GetGlobalMousePosition()))
                return;
            // By cell, like every button on the harness's board: a crown
            // overhangs its own hexagon, so picking by pixels would light a cell
            // by clicking a tree standing on the one behind it.
            Vector2I at = _field.CellAt(_field.ToLocal(GetGlobalMousePosition()));
            // Not clamped: on a board that is a hole in a rectangle a click off
            // the plot is a click off the board, and clamping it would light the
            // cell beside the one that was aimed at.
            if (!_field.InBounds(at))
                return;
            if (_fire?.Light(at) != true)
                GD.Print($"wood: {at.X},{at.Y} has nothing on it to burn");
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
            case Key.R:
                // The bench as it opened: the fire out and the wood green again.
                // Doused rather than left to finish, because a reset whose job is
                // "back to the baseline" and which leaves a cell smouldering is
                // lying about what it did. Smoulder immediately after, so the
                // trees drop their coats on this frame rather than on the next.
                _fire?.Douse();
                _grove?.Smoulder();
                _camera.Zoom = new Vector2(_zoomAt, _zoomAt);
                _camera.Position = ViewHome;
                break;
            case Key.F12:
                Capture($"{AssetRoot.Out}/wood_{Time.GetTicksMsec()}.png");
                break;
        }
    }

    private void Capture(string path)
    {
        Image image = GetViewport().GetTexture().GetImage();
        DirAccess.MakeDirRecursiveAbsolute(path.GetBaseDir());
        Error err = image.SavePng(path);
        GD.Print(err == Error.Ok ? $"wood: wrote {path}"
                                 : $"wood: could not write {path}: {err}");
    }
}
