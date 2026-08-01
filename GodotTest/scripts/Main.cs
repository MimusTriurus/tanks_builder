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
    private double _speed;
    private MovementProfile _profile = MovementProfile.Medium;
    private readonly BodyPitch _pitch = new();

    /// <summary>Body pitch is off unless asked for - key P, or --pitch on a
    /// capture run. It is an interpretation of the sprites rather than
    /// something the atlases contain, so the plain rendered motion stays the
    /// default and the effect is something you switch on to compare against
    /// it.</summary>
    private bool _pitchEnabled;

    private readonly BodyRumble _rumble = new();
    /// <summary>Ground rumble - key B, or --rumble. Off again: the whole-pixel
    /// jolt turned out to be a stronger reading of the ground than wanted, and
    /// the engine tremble now covers moving as well as standing, so the two
    /// stack rather than divide the work.</summary>
    private bool _rumbleEnabled;

    private readonly EngineTremble _tremble = new();
    /// <summary>Engine tremble, standing or moving. The one vibration that is on
    /// by default: a tank that slides over the ground with nothing moving on it
    /// is wrong in a way the plain render is not. Key I, or --no-tremble.</summary>
    private bool _trembleEnabled = true;

    private readonly TurretScan _scan = new();
    /// <summary>Idle turret traverse - key N, or --scan.</summary>
    private bool _scanEnabled;

    private FlashSheet _flash = null!;
    private readonly Recoil _recoil = new();
    /// <summary>Screen frames since the shot went off, or -1 between shots.</summary>
    private int _shotFrame = -1;

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
    // Prints one line of state per frame and quits. Picking a frame to
    // screenshot by arithmetic does not work - the pivot length depends on
    // which heading the pathfinder chose - so read it off instead.
    private int _traceFrames;
    private string? _startTag;
    private bool _fireAtStart;
    private double? _startTurret;
    /// <summary>Which flash to start with - key V, or --flash sheet|rendered.
    /// Rendered by default: it is the one the pipeline exists to produce, and
    /// it falls back on its own for a tank that has none.</summary>
    private FlashSource _flashSource = FlashSource.Rendered;

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
            else if (userArgs[i] == "--capture-at" && i + 1 < userArgs.Length
                     && int.TryParse(userArgs[i + 1], out int at))
                _captureAfter = at;
            else if (userArgs[i] == "--pitch")
                _pitchEnabled = true;
            else if (userArgs[i] == "--rumble")
                _rumbleEnabled = true;
            else if (userArgs[i] == "--no-tremble")
                _trembleEnabled = false;
            else if (userArgs[i] == "--scan")
                _scanEnabled = true;
            else if (userArgs[i] == "--fire")
                _fireAtStart = true;
            else if (userArgs[i] == "--flash" && i + 1 < userArgs.Length)
                _flashSource = userArgs[i + 1].Equals("sheet",
                    StringComparison.OrdinalIgnoreCase)
                    ? FlashSource.Sheet
                    : FlashSource.Rendered;
            else if (userArgs[i] == "--turret" && i + 1 < userArgs.Length
                     && double.TryParse(userArgs[i + 1], out double bearing))
                _startTurret = bearing;
            else if (userArgs[i] == "--tank" && i + 1 < userArgs.Length)
                _startTag = userArgs[i + 1].ToUpperInvariant();
            else if (userArgs[i] == "--roll-only")
            {
                // Isolates the roll by silencing the heave. The two cannot be
                // told apart in a screenshot otherwise: a vertical jolt shifts
                // content between rows, and where the silhouette narrows - the
                // gun, the turret - that reads as a horizontal shift of over
                // two pixels which has nothing to do with roll.
                _rumbleEnabled = true;
                _rumble.Amplitude = 0.0;
            }
            else if (userArgs[i] == "--trace" && i + 1 < userArgs.Length
                     && int.TryParse(userArgs[i + 1], out int frames))
                _traceFrames = frames;
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

        _flash = FlashSheet.Load($"{SpritesRoot}/Fire_rgba.png");
        if (_flash.Error.Length > 0)
            failures.Add($"flash: {_flash.Error}");

        _field = new HexField();
        AddChild(_field);
        _tank = new TankSprite { Flash = _flash, Source = _flashSource };
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

        if (_startTag is not null && _loaded.IndexOf(_startTag) >= 0)
            _tagIndex = _loaded.IndexOf(_startTag);
        UseTag(CurrentTag());

        if (_selfTest)
        {
            int failed = SelfTest.Run(_field, _tank);
            GetTree().Quit(failed == 0 ? 0 : 1);
            return;
        }
        if (_startTurret is not null)
            _tank.TurretFacing = Mod(_startTurret.Value, 360.0);
        if (_driveTo is not null)
            OrderMoveTo(_field.ClampCell(_driveTo.Value));
        if (_fireAtStart)
            Fire();
    }

    private string CurrentTag() =>
        _loaded[((_tagIndex % _loaded.Count) + _loaded.Count) % _loaded.Count];

    private void UseTag(string tag)
    {
        AtlasSet atlas = _atlases[tag];
        _tank.Atlas = atlas;
        _field.Atlas = atlas;
        _profile = MovementProfile.For(tag);
        // the rumble reaches full strength at half cruise, whatever cruise is
        // for this class, so a heavy tank is not stuck in the thinned-out band
        _rumble.FullSpeed = _profile.TopSpeed * 0.5;
        // per class, so a heavy at its own cruise is as worked as a light at
        // its own instead of idling along because it happens to be slower
        _tremble.TopSpeed = _profile.TopSpeed;
        _tremble.Reset();
        _scan.Reset();
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
        _speed = 0.0;
        _field.Highlight = Array.Empty<Vector2I>();
        _field.QueueRedraw();
    }

    /// <summary>Distance left in the current straight run, and the speed to be
    /// doing by the end of it.
    ///
    /// The run stops at the first bend on purpose: the tank has to slow there
    /// to swing round, so looking past the bend would start the braking too
    /// late. What it slows *to* depends on what the bend is: the crawl at a
    /// corner, a full stop only at the destination.</summary>
    private (double Distance, double EndSpeed) RemainingRun()
    {
        double total = (_origin + _field.CellAnchor(_path[_pathStep]) - _tank.Position).Length();
        int heading = HexField.HeadingTo(_cell, _path[_pathStep]);
        int i = _pathStep;
        for (; i + 1 < _path.Count; i++)
        {
            if (HexField.HeadingTo(_path[i], _path[i + 1]) != heading)
                break;
            total += (_field.CellAnchor(_path[i + 1]) - _field.CellAnchor(_path[i])).Length();
        }
        bool endOfPath = i + 1 >= _path.Count;
        return (total, endOfPath ? 0.0 : _profile.CornerSpeed);
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

        double diff = WrapAngle(heading - _tank.HullFacing);
        double accelRatio;

        if (Math.Abs(diff) > 0.5)
        {
            // Slow to the cornering crawl and swing round while still creeping.
            // The crawl is a floor, never a target to speed up to, so a standing
            // start still pivots in place - which is what a tank does - while a
            // bend taken at speed stays continuous.
            double crawl = _profile.CornerSpeed;
            accelRatio = _speed > crawl ? -1.0 : 0.0;
            if (_speed > crawl)
                _speed = Math.Max(crawl, _speed - _profile.Accel * delta);
            double budget = _profile.TurnRate * delta;
            _tank.TurnHull(Math.Abs(diff) <= budget ? diff : Math.Sign(diff) * budget);
        }
        else
        {
            (double remaining, double endSpeed) = RemainingRun();
            double brakeDistance =
                (_speed * _speed - endSpeed * endSpeed) / (2.0 * _profile.Accel);
            bool braking = remaining <= brakeDistance;
            accelRatio = braking ? -1.0 : _speed < _profile.TopSpeed ? 1.0 : 0.0;
            _speed = Math.Clamp(_speed + accelRatio * _profile.Accel * delta,
                braking ? endSpeed : 0.0, _profile.TopSpeed);
        }

        UpdatePitch(accelRatio, delta);
        UpdateRumble(delta);

        Vector2 goal = _origin + _field.CellAnchor(next);
        Vector2 to = goal - _tank.Position;
        var budgetPx = (float)(_speed * delta);
        if (to.Length() <= budgetPx || (budgetPx <= 0.0f && to.Length() < 0.5f))
        {
            _tank.Position = goal;
            _cell = next;
            _pathStep++;
            if (!Moving)
            {
                _field.Highlight = Array.Empty<Vector2I>();
                _field.QueueRedraw();
            }
        }
        else if (budgetPx > 0.0f)
        {
            _tank.Position += to.Normalized() * budgetPx;
        }
        _tank.QueueRedraw();
    }

    private void UpdatePitch(double accelRatio, double delta)
    {
        if (!_pitchEnabled)
        {
            _pitch.Reset();
            _tank.Pitch = 0.0;
            return;
        }
        _pitch.Update(accelRatio, delta);
        _tank.Pitch = _pitch.Angle;
    }

    private void UpdateRumble(double delta)
    {
        if (!_rumbleEnabled)
        {
            _rumble.Reset();
            _tank.Shake = 0;
            _tank.Roll = 0.0;
            return;
        }
        _rumble.Advance(_speed * delta, _speed);
        _tank.Shake = _rumble.Offset;
        _tank.Roll = _rumble.Roll;
    }

    /// <summary>Runs every frame, moving or not - that is the whole point of it.
    /// The rumble is keyed on distance and goes silent the instant the tank
    /// stops; the engine does not. Speed only moves the frequency, so there is
    /// no threshold anywhere for the effect to switch on or off at.</summary>
    private void UpdateTremble(double delta)
    {
        if (!_trembleEnabled)
        {
            if (_tank.TremblePitch == 0.0 && _tank.TrembleRoll == 0.0)
                return;
            _tremble.Reset();
            _tank.TremblePitch = 0.0;
            _tank.TrembleRoll = 0.0;
            _tank.QueueRedraw();
            return;
        }
        _tremble.Advance(_speed, delta);
        _tank.TremblePitch = _tremble.Pitch;
        _tank.TrembleRoll = _tremble.Roll;
        _tank.QueueRedraw();
    }

    /// <summary>Fire. Restarts the flash from frame zero rather than being
    /// ignored while one is running, so holding the key reads as a rate of fire
    /// instead of doing nothing.</summary>
    private void Fire()
    {
        _shotFrame = 0;
        _recoil.Fire(WrapAngle(_tank.TurretFacing - _tank.HullFacing));
    }

    /// <summary>The shot runs on screen frames, not seconds. The sheet is a
    /// hand-timed sequence of held frames, so counting frames is what it is
    /// timed in; under --capture the clock is fixed at 1/60 anyway, which is
    /// what makes a shot land on the same sheet frame in two runs.</summary>
    private void UpdateShot(double delta)
    {
        _recoil.Update(delta);
        _tank.RecoilPitch = _recoil.Pitch;
        _tank.RecoilRoll = _recoil.Roll;

        // Both clocks run, whichever source is showing. They are separate
        // tables - sixteen sheet frames against eight rendered phases - but
        // they add up to the same 34 frames, so flipping V mid-shot swaps the
        // picture without moving the moment, which is what makes an A/B
        // comparison one.
        int frame = _shotFrame < 0 ? -1 : FlashSheet.FrameAt(_shotFrame);
        int phase = _shotFrame < 0 ? -1 : EffectLayer.PhaseAt(_shotFrame);
        if (_shotFrame >= 0)
        {
            _shotFrame++;
            if (frame < 0 && phase < 0)
                _shotFrame = -1;
        }
        if (frame != _tank.FlashFrame || phase != _tank.ShotPhase || _recoil.Moving)
        {
            _tank.FlashFrame = frame;
            _tank.ShotPhase = phase;
            _tank.QueueRedraw();
        }
    }

    /// <summary>Suspended while under way, and while anything else is driving
    /// the turret. A locked turret holding its world heading through a
    /// manoeuvre is the feature this harness exists to show; a scan quietly
    /// walking it off that heading would look exactly like the lock failing.</summary>
    private void UpdateScan(double delta)
    {
        if (!_scanEnabled || Moving || _spinning || _aimWithMouse)
            return;
        double move = _scan.Advance(360.0 / _tank.Atlas!.Count, delta);
        if (move == 0.0)
            return;
        _tank.TurretFacing = Mod(_tank.TurretFacing + move, 360.0);
        _tank.QueueRedraw();
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

        // A capture run steps at a fixed rate so two runs land on the same
        // state at the same frame number. On real deltas they drift a frame or
        // two apart, and comparing a pitch-on shot against a pitch-off shot
        // then measures the drift instead: caught once when the two runs were
        // mid-pivot on either side of a 30 deg sprite boundary and "differed"
        // by 91k pixels.
        if (_capturePath is not null || _traceFrames > 0)
            delta = 1.0 / 60.0;

        if (_traceFrames > 0)
        {
            GD.Print($"{_frames,4}  hull {_tank.HullFacing,6:F1}  speed {_speed,6:F1}"
                     + $"  pitch {_tank.Pitch,8:F5}  shake {_tank.Shake,2}"
                     + $"  roll {_tank.Roll,8:F5}  trem {_tank.TremblePitch,8:F5}"
                     + $"/{_tank.TrembleRoll,8:F5}  scan {_scan.Offset,6:F1}"
                     + $"  turret {_tank.TurretFacing,6:F1}  shot {_tank.FlashFrame,3}"
                     + $"  recoil {_recoil.Pitch,8:F5}/{_recoil.Roll,8:F5}"
                     + $"  cell ({_cell.X},{_cell.Y})");
            if (++_frames >= _traceFrames)
            {
                GetTree().Quit();
                return;
            }
        }

        if (Moving)
            AdvanceOrder(delta);
        else if (_pitch.Angle != 0.0 || _speed != 0.0 || _tank.Shake != 0)
        {
            // let the body settle after the stop instead of snapping level
            _speed = 0.0;
            UpdatePitch(0.0, delta);
            UpdateRumble(delta);
            _tank.QueueRedraw();
        }

        UpdateTremble(delta);
        UpdateScan(delta);
        UpdateShot(delta);

        if (!Moving && _spinning)
        {
            _tank.TurretFacing = Mod(_tank.TurretFacing + SpinSpeed * delta, 360.0);
            _tank.QueueRedraw();
        }
        else if (!Moving && _aimWithMouse)
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
            case Key.P:
                _pitchEnabled = !_pitchEnabled;
                UpdatePitch(0.0, 0.0);
                break;
            case Key.B:
                _rumbleEnabled = !_rumbleEnabled;
                UpdateRumble(0.0);
                break;
            case Key.I:
                _trembleEnabled = !_trembleEnabled;
                UpdateTremble(0.0);
                break;
            case Key.N:
                _scanEnabled = !_scanEnabled;
                if (!_scanEnabled)
                    _scan.Reset();
                break;
            case Key.K: _tank.TurretStabilised = !_tank.TurretStabilised; break;
            case Key.Z: Fire(); break;
            case Key.V:
                _tank.Source = _tank.Source == FlashSource.Rendered
                    ? FlashSource.Sheet
                    : FlashSource.Rendered;
                _tank.QueueRedraw();
                break;
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
                _pitch.Reset();
                _tank.Pitch = 0.0;
                _rumble.Reset();
                _tank.Shake = 0;
                _tank.Roll = 0.0;
                _tremble.Reset();
                _tank.TremblePitch = 0.0;
                _tank.TrembleRoll = 0.0;
                _scan.Reset();
                _recoil.Reset();
                _shotFrame = -1;
                _tank.FlashFrame = -1;
                _tank.ShotPhase = -1;
                _tank.Source = _flashSource;
                _tank.RecoilPitch = 0.0;
                _tank.RecoilRoll = 0.0;
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
            $"speed {_speed,6:F0} / {_profile.TopSpeed:F0} px/s"
                + $"   ramp {_profile.RampTime:F2}s over {_profile.RampDistance:F0}px"
                + $"   turn {_profile.TurnRate:F0} deg/s   corner {_profile.CornerSpeed:F0} px/s",
            $"pitch {_tank.Pitch,7:F4}   shake {_tank.Shake,2}px   roll {_tank.Roll,7:F4}"
                + $"   P body pitch: {On(_pitchEnabled)}   B ground rumble: {On(_rumbleEnabled)}"
                + $"   K turret stabiliser: {On(_tank.TurretStabilised)}",
            $"tremble {_tank.TremblePitch,7:F4} / {_tank.TrembleRoll,7:F4}"
                + $" at {_tremble.PitchRateAt(_speed),4:F1} Hz"
                + $"   scan {_scan.Offset,6:F1} deg"
                + $"   I engine tremble: {On(_trembleEnabled)}"
                + $"   N turret scan: {On(_scanEnabled)}",
            $"flash: {(_tank.ActiveSource == FlashSource.Rendered ? "RENDERED (Blender layers, additive)" : "SHEET (painted, rotated)")}"
                + (_tank.Source == FlashSource.Rendered && !_tank.CanRender
                    ? "   [no rendered flash for this tank - split a Barrel]" : "")
                + $"   V swap",
            _tank.ActiveSource == FlashSource.Rendered
                ? $"phase {(_tank.ShotPhase < 0 ? " -" : _tank.ShotPhase.ToString()),2}"
                    + $" / {EffectLayer.Hold.Length}   tile {atlas.TileOf("flash").X}"
                    + $"   recoil {_recoil.Pitch,7:F4} / {_recoil.Roll,7:F4}   Z fire"
                : $"shot frame {(_tank.FlashFrame < 0 ? " -" : _tank.FlashFrame.ToString()),2}"
                    + $" / {FlashSheet.Hold.Length}"
                    + $"   muzzle {atlas.Muzzle(atlas.FrameFor(_tank.TurretFacing))}"
                    + $"   recoil {_recoil.Pitch,7:F4} / {_recoil.Roll,7:F4}   Z fire",
            "",
            "left click: drive there    F turret lock    ESC cancel    A/D hull    Q/E turret",
            "W/S step fwd/back    1/2/3 tank HT/MT/LT    wheel zoom, MMB pan",
            $"SPACE spin turret (axis check)    M mouse aim: {On(_aimWithMouse)}"
                + $"    X axis cross: {On(_tank.ShowAxis)}",
            $"H hull layer: {On(_tank.ShowHull)}    T turret layer: {On(_tank.ShowTurret)}"
                + $"    G field: {On(_field.ShowField)}    R reset",
            "Z fire    V flash source    P pitch    B rumble    I engine tremble"
                + "    N turret scan    K stabiliser",
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
