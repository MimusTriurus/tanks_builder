using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// Test harness for the tank sprite atlases: a hex field, one tank driving
/// around it, and independent control of hull and turret headings.
///
/// What it is actually for, in order:
///   1. does the turret turn independently of the hull, with no seam gap
///   2. does the turret stay pinned to its axis instead of orbiting (SPACE)
///   3. does the tank sit centred in its cell at all twelve headings
///   4. does the field tessellate without gaps or overlap
/// </summary>
public sealed partial class Main : Node2D
{
    private const string SpritesRoot = "D:/Projects/AgentCoding/BlenderMCP/Sprites";
    private static readonly string[] Tags = { "HT", "MT", "LT" };

    private const double RotateSpeed = 200.0;   // deg/sec of hull yaw
    private const double MoveSpeed = 240.0;     // px/sec along the ground
    private const double SpinSpeed = 90.0;      // deg/sec, for the wobble check

    private readonly Dictionary<string, AtlasSet> _atlases = new();
    private readonly List<string> _loaded = new();

    private TankSprite _tank = null!;
    private HexField _field = null!;
    private Label _hud = null!;
    private Camera2D _camera = null!;

    private readonly Vector2 _origin = new(220, 200);
    private int _tagIndex = 1;                  // MT - the largest measured axis error
    private Vector2I _cell = new(4, 2);

    private List<Vector2I> _path = new();
    private int _pathStep;
    private bool _rotatingIntoStep;

    private bool _spinning;
    private bool _aimWithMouse;

    // `godot --path <dir> -- --capture <file>` renders a few frames, saves a
    // screenshot and quits, so the harness can be checked without a human at
    // the keyboard. F12 does the same thing interactively.
    private string? _capturePath;
    private int _captureAfter = 4;
    private int _frames;
    private bool _selfTest;
    private Vector2I? _driveTo;

    private bool Moving => _pathStep < _path.Count;

    public override void _Ready()
    {
        string[] userArgs = OS.GetCmdlineUserArgs();
        for (int i = 0; i < userArgs.Length; i++)
        {
            if (userArgs[i] == "--capture" && i + 1 < userArgs.Length)
                _capturePath = userArgs[i + 1];
            else if (userArgs[i].StartsWith("--capture="))
                _capturePath = userArgs[i]["--capture=".Length..];
            else if (userArgs[i] == "--selftest")
                _selfTest = true;
            else if (userArgs[i] == "--drive" && i + 1 < userArgs.Length)
            {
                string[] parts = userArgs[i + 1].Split(',');
                if (parts.Length == 2
                    && int.TryParse(parts[0], out int col)
                    && int.TryParse(parts[1], out int row))
                {
                    _driveTo = new Vector2I(col, row);
                    _captureAfter = 90;   // long enough to be mid-path
                }
            }
        }

        var failures = new List<string>();
        foreach (string tag in Tags)
        {
            AtlasSet atlas = AtlasSet.Load(SpritesRoot, tag);
            if (atlas.Error.Length > 0)
                failures.Add($"{tag}: {atlas.Error}");
            else
            {
                _atlases[tag] = atlas;
                _loaded.Add(tag);
            }
        }

        _field = new HexField();
        AddChild(_field);
        _tank = new TankSprite();
        AddChild(_tank);

        // Judging whether two layers line up is a pixel-level question, so the
        // harness has to be able to get close. Sprites are drawn at 1:1 by
        // default; zoom only changes the view, never the atlas sampling.
        _camera = new Camera2D { Position = new Vector2(760, 500), Enabled = true };
        AddChild(_camera);

        var layer = new CanvasLayer();
        AddChild(layer);
        _hud = new Label { Position = new Vector2(16, 12) };
        _hud.AddThemeColorOverride("font_color", new Color(0.92f, 0.95f, 1.0f));
        _hud.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.85f));
        _hud.AddThemeConstantOverride("outline_size", 5);
        layer.AddChild(_hud);

        if (_loaded.Count == 0)
        {
            _hud.Text = $"No atlases loaded from {SpritesRoot}\n\n"
                        + string.Join("\n", failures);
            return;
        }
        if (failures.Count > 0)
            GD.PushWarning("some atlases failed: " + string.Join(", ", failures));

        UseTag(CurrentTag());

        if (_selfTest)
        {
            int failed = SelfTest.Run(_field, _tank);
            GetTree().Quit(failed == 0 ? 0 : 1);
            return;
        }
        if (_driveTo is not null)
            OrderMoveTo(_field.ClampCell(_driveTo.Value));
    }

    private string CurrentTag() =>
        _loaded[((_tagIndex % _loaded.Count) + _loaded.Count) % _loaded.Count];

    private void UseTag(string tag)
    {
        AtlasSet atlas = _atlases[tag];
        _tank.Atlas = atlas;
        _field.Atlas = atlas;
        CancelOrder();
        _field.QueueRedraw();
        SnapToCell();
    }

    private void SnapToCell()
    {
        _cell = _field.ClampCell(_cell);
        _field.Position = _origin;
        _tank.Position = _origin + _field.CellAnchor(_cell);
        _tank.QueueRedraw();
    }

    // --- orders ------------------------------------------------------------

    private void OrderMoveTo(Vector2I target)
    {
        if (!_field.InBounds(target) || target == _cell)
            return;
        _path = _field.FindPath(_cell, target);
        _pathStep = 0;
        _rotatingIntoStep = true;
        // Mouse aim would override both turret modes and make the feature look
        // broken while the tank drives, so an order takes the turret back.
        _aimWithMouse = false;
        _spinning = false;
        _field.Highlight = _path;
        _field.QueueRedraw();
    }

    private void CancelOrder()
    {
        _path = new List<Vector2I>();
        _pathStep = 0;
        _field.Highlight = Array.Empty<Vector2I>();
        _field.QueueRedraw();
    }

    private void AdvanceOrder(double delta)
    {
        Vector2I next = _path[_pathStep];
        int heading = HexField.HeadingTo(_cell, next);
        if (heading < 0)
        {
            CancelOrder();      // path went stale - do not drive off the grid
            return;
        }

        if (_rotatingIntoStep)
        {
            double diff = WrapAngle(heading - _tank.HullFacing);
            double budget = RotateSpeed * delta;
            if (Math.Abs(diff) <= budget)
            {
                _tank.TurnHull(diff);
                _rotatingIntoStep = false;
            }
            else
            {
                _tank.TurnHull(Math.Sign(diff) * budget);
                return;         // turn on the spot first, then drive
            }
        }

        Vector2 goal = _origin + _field.CellAnchor(next);
        Vector2 to = goal - _tank.Position;
        float budgetPx = (float)(MoveSpeed * delta);
        if (to.Length() <= budgetPx)
        {
            _tank.Position = goal;
            _cell = next;
            _pathStep++;
            _rotatingIntoStep = true;
            if (!Moving)
            {
                _field.Highlight = Array.Empty<Vector2I>();
                _field.QueueRedraw();
            }
        }
        else
        {
            _tank.Position += to.Normalized() * budgetPx;
        }
    }

    // --- frame -------------------------------------------------------------

    public override void _Process(double delta)
    {
        // give the HUD and both layers a couple of frames to settle first
        if (_capturePath is not null && ++_frames > _captureAfter)
        {
            Capture(_capturePath);
            GetTree().Quit();
            return;
        }

        if (_tank.Atlas is null)
            return;

        if (Moving)
            AdvanceOrder(delta);
        else if (_spinning)
        {
            _tank.TurretFacing = Mod(_tank.TurretFacing + SpinSpeed * delta, 360.0);
            _tank.QueueRedraw();
        }
        else if (_aimWithMouse)
        {
            Vector2 toMouse = GetGlobalMousePosition() - _tank.GlobalPosition;
            if (toMouse.Length() > 8.0f)
            {
                // Screen y grows downward and the isometric view squashes the
                // ground plane vertically by sin(elevation); undo both before
                // reading a world heading off the mouse.
                double worldY = -toMouse.Y / Math.Sin(Mathf.DegToRad(30.0));
                double facing = Mod(Mathf.RadToDeg(Math.Atan2(worldY, toMouse.X)), 360.0);
                if (Math.Abs(facing - _tank.TurretFacing) > 0.01)
                {
                    _tank.TurretFacing = facing;
                    _tank.QueueRedraw();
                }
            }
        }

        UpdateHud();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton { Pressed: true } mouse)
        {
            if (mouse.ButtonIndex == MouseButton.Left && _tank.Atlas is not null)
            {
                Vector2 local = _field.ToLocal(GetGlobalMousePosition());
                OrderMoveTo(_field.ClampCell(_field.CellAt(local)));
                UpdateHud();
                return;
            }
            float factor = mouse.ButtonIndex switch
            {
                MouseButton.WheelUp => 1.25f,
                MouseButton.WheelDown => 0.8f,
                _ => 1.0f,
            };
            if (factor != 1.0f)
            {
                float zoom = Mathf.Clamp(_camera.Zoom.X * factor, 0.25f, 8.0f);
                _camera.Zoom = new Vector2(zoom, zoom);
                UpdateHud();
            }
            return;
        }
        if (@event is InputEventMouseMotion motion
            && (motion.ButtonMask & MouseButtonMask.Middle) != 0)
        {
            _camera.Position -= motion.Relative / _camera.Zoom;
            return;
        }
        if (@event is not InputEventKey { Pressed: true, Echo: false } key)
            return;
        if (_tank.Atlas is null)
            return;

        double step = 360.0 / _tank.Atlas.Count;
        switch (key.Keycode)
        {
            case Key.A: _tank.TurnHull(step); break;
            case Key.D: _tank.TurnHull(-step); break;
            case Key.Q:
                _aimWithMouse = false;
                _tank.TurretFacing = Mod(_tank.TurretFacing + step, 360.0);
                break;
            case Key.E:
                _aimWithMouse = false;
                _tank.TurretFacing = Mod(_tank.TurretFacing - step, 360.0);
                break;
            case Key.W:
                OrderMoveTo(_field.Neighbour(_cell, _tank.HullFacing));
                break;
            case Key.S:
                OrderMoveTo(_field.Neighbour(_cell, Mod(_tank.HullFacing + 180.0, 360.0)));
                break;
            case Key.F: _tank.TurretLocked = !_tank.TurretLocked; break;
            case Key.Escape: CancelOrder(); break;
            case Key.Key1 or Key.Key2 or Key.Key3:
                // Godot's Key enum is backed by long, so this needs the cast
                _tagIndex = (int)(key.Keycode - Key.Key1);
                UseTag(CurrentTag());
                break;
            case Key.Space: _spinning = !_spinning; break;
            case Key.M: _aimWithMouse = !_aimWithMouse; break;
            case Key.H: _tank.ShowHull = !_tank.ShowHull; break;
            case Key.T: _tank.ShowTurret = !_tank.ShowTurret; break;
            case Key.G:
                _field.ShowField = !_field.ShowField;
                _field.QueueRedraw();
                break;
            case Key.X: _tank.ShowAxis = !_tank.ShowAxis; break;
            case Key.F12:
                Capture($"{ProjectSettings.GlobalizePath("res://")}shot_{Time.GetTicksMsec()}.png");
                return;
            case Key.R:
                CancelOrder();
                _tank.HullFacing = 270.0;
                _tank.TurretFacing = 270.0;
                _spinning = false;
                _aimWithMouse = false;
                _camera.Zoom = Vector2.One;
                _camera.Position = new Vector2(760, 500);
                SnapToCell();
                break;
            default: return;
        }

        _tank.QueueRedraw();
        UpdateHud();
    }

    private void UpdateHud()
    {
        AtlasSet atlas = _tank.Atlas!;
        Vector2I frames = _tank.FrameIndices();
        string On(bool v) => v ? "on" : "off";
        string order = Moving
            ? $"-> ({_path[^1].X},{_path[^1].Y}), {_path.Count - _pathStep} left"
            : "idle";

        _hud.Text = string.Join("\n", new[]
        {
            $"{atlas.Tag}   cell ({_cell.X},{_cell.Y})   {order}"
                + $"   {atlas.UnitsPerPixel:F5} units/px   zoom {_camera.Zoom.X:F2}x",
            $"hull   {_tank.HullFacing,6:F1} deg -> frame {frames.X,2}"
                + $"      turret {_tank.TurretFacing,6:F1} deg -> frame {frames.Y,2}",
            $"turret mode: {(_tank.TurretLocked ? "LOCKED - holds world heading" : "FREE - swings with the hull")}"
                + (_aimWithMouse ? "   (mouse aim overrides)" : ""),
            "",
            "left click: drive there    F turret lock    ESC cancel    A/D hull    Q/E turret",
            "W/S step fwd/back    1/2/3 tank HT/MT/LT    wheel zoom, MMB pan",
            $"SPACE spin turret (axis check)    M mouse aim: {On(_aimWithMouse)}"
                + $"    X axis cross: {On(_tank.ShowAxis)}",
            $"H hull layer: {On(_tank.ShowHull)}    T turret layer: {On(_tank.ShowTurret)}"
                + $"    G field: {On(_field.ShowField)}    R reset",
        });
    }

    private void Capture(string path)
    {
        Image image = GetViewport().GetTexture().GetImage();
        Error err = image.SavePng(path);
        if (err != Error.Ok)
            GD.PushError($"screenshot to {path} failed: {err}");
        else
            GD.Print($"screenshot: {path}");
    }

    private static double Mod(double a, double n) => (a % n + n) % n;

    private static double WrapAngle(double degrees) => Mod(degrees + 180.0, 360.0) - 180.0;
}
