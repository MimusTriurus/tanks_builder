using System;
using System.Threading.Tasks;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// A veil over a scene change, with the build's progress on it: the editor's
/// Run and the bench's Edit (<see cref="Session"/>).
///
/// <b>The change is not asynchronous; the build after it is cut into
/// stages.</b> <c>ChangeSceneToFile</c> loads on the main thread, and the
/// scene that comes in builds its whole board in <c>_Ready</c> - relief,
/// grass, models, seconds of it - so the window used to sit on the last frame
/// of the scene going out, which reads as a hang. The veil is put on screen
/// first and the change waits until it has been drawn. The scene then builds
/// stage by stage (<see cref="Stage"/>), and every stage gives the veil a
/// frame to move its bar in. Inside one stage nothing moves - the thread is
/// busy - so a stage that is long is a stage to cut.
///
/// <b>Without a veil a stage costs nothing.</b> Launched straight, a scene's
/// <see cref="Stage"/> returns a finished task, the await goes straight on,
/// and the whole build happens in <c>_Ready</c> as it always did: captures,
/// perf runs and the self test count frames from a board that is already
/// there.
///
/// <b>Hung off the tree's root, not the scene</b>, so the change that frees
/// the scene leaves it standing. It lifts once the new scene says it is built
/// (<see cref="Built"/>) and has drawn a few frames - the first ones also
/// compile its shaders - and frees itself when it has faded. A scene that
/// never reports a stage is waited for by frames alone.
/// </summary>
public sealed partial class Loading : CanvasLayer
{
    /// <summary>Frames drawn before the change, so the veil is on screen
    /// when the thread blocks; and after the build, before the veil lifts.</summary>
    private const int Settle = 3;

    /// <summary>Seconds the veil takes to fade.</summary>
    private const float Fade = 0.25f;

    private string _scene = "";
    private string _what = "";
    private Node? _from;
    private bool _changed;
    private bool _staged;
    private bool _built;
    private bool _lifting;
    private int _frames;
    private ColorRect _veil = null!;
    private ProgressBar _bar = null!;
    private Label _step = null!;

    private static Loading? _on;

    /// <summary>Change to <paramref name="scene"/> under a veil that says
    /// <paramref name="what"/>.</summary>
    public static void Go(SceneTree tree, string scene, string what)
    {
        // Once: a second F5 in the frames before the change would queue a
        // second change behind it. One already lifting does not count - the
        // way back can be asked for while the veil it came under still fades.
        if (_on is not null && IsInstanceValid(_on) && !_on._lifting)
            return;
        _on = new Loading { Layer = 128, _scene = scene, _what = what };
        // Deferred: --menu run presses Run from the editor's _Ready, while the
        // root is still adding its children and refuses one more.
        tree.Root.CallDeferred(Node.MethodName.AddChild, _on);
    }

    // --- the build's stages ---------------------------------------------------

    private static string? _stageName;
    private static ulong _stageAt;
    private static ulong _buildMs;
    private static string _stages = "";

    /// <summary>The veil the scene coming in is building under, or null.</summary>
    private static Loading? Building =>
        _on is not null && IsInstanceValid(_on) && _on._changed && !_on._lifting
            ? _on : null;

    /// <summary>
    /// The build of <paramref name="scene"/> moves on to <paramref name="what"/>,
    /// <paramref name="done"/> of the way through (0..1). Logs what the stage
    /// before cost - the frame given to the veil is not counted - and, under
    /// a veil, shows this one and lets a frame be drawn before it starts.
    /// </summary>
    public static async Task Stage(Node scene, string what, float done)
    {
        Close();
        if (Building is { } on)
        {
            on._staged = true;
            on._bar.Value = Math.Clamp(done, 0.0f, 1.0f);
            on._step.Text = what;
            await scene.ToSignal(scene.GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        _stageName = what;
        _stageAt = Time.GetTicksMsec();
    }

    /// <summary>Whether a veil is up over a scene being built: the editor's
    /// cue to lay its first grass a few cells a frame (MapEditor.Dress).</summary>
    public static bool Veiled => Building is not null;

    /// <summary>
    /// <see cref="Stage"/> for work that runs a piece a frame on its own
    /// rather than awaiting one: shows <paramref name="what"/> at
    /// <paramref name="done"/> and starts its clock, which
    /// <see cref="Rest"/> stops before the frame is drawn.
    /// </summary>
    public static void Step(Node scene, string what, float done)
    {
        Close();
        if (Building is { } on)
        {
            on._staged = true;
            on._bar.Value = Math.Clamp(done, 0.0f, 1.0f);
            on._step.Text = what;
        }
        _stageName = what;
        _stageAt = Time.GetTicksMsec();
    }

    /// <summary>The piece of a <see cref="Step"/> is done for this frame.</summary>
    public static void Rest() => Close();

    /// <summary>The build of <paramref name="scene"/> is done: the last stage
    /// is logged with the total, and the veil may lift.</summary>
    public static void Built(Node scene)
    {
        Close();
        if (_stages.Length > 0)
            Log.Print($"load: {scene.Name} built in {_buildMs} ms -{_stages}");
        _stages = "";
        _buildMs = 0;
        if (Building is { } on)
        {
            on._built = true;
            on._bar.Value = 1.0;
        }
    }

    private static void Close()
    {
        if (_stageName is null)
            return;
        ulong ms = Time.GetTicksMsec() - _stageAt;
        _buildMs += ms;
        _stages += $" {_stageName} {ms}";
        _stageName = null;
    }

    // --- the veil -------------------------------------------------------------

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        _from = GetTree().CurrentScene;
        _veil = new ColorRect
        {
            Color = new Color(0.07f, 0.08f, 0.09f),
            // Clicks and the wheel stop here: the scene going out must not
            // take an order in the frames before it goes, nor the one coming
            // in while it builds.
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        _veil.SetAnchorsPreset(Control.LayoutPreset.FullRect);

        var middle = new CenterContainer();
        middle.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 14);
        var title = new Label
        {
            Text = _what,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        title.AddThemeFontSizeOverride("font_size", 28);
        _bar = new ProgressBar
        {
            MinValue = 0.0, MaxValue = 1.0, Step = 0.0,
            ShowPercentage = false,
            CustomMinimumSize = new Vector2(420.0f, 8.0f),
        };
        // The theme's track is near the veil's own colour: drawn here, a
        // groove a shade lighter and a pale fill.
        _bar.AddThemeStyleboxOverride("background", new StyleBoxFlat
        {
            BgColor = new Color(1, 1, 1, 0.08f),
            CornerRadiusTopLeft = 4, CornerRadiusTopRight = 4,
            CornerRadiusBottomLeft = 4, CornerRadiusBottomRight = 4,
        });
        _bar.AddThemeStyleboxOverride("fill", new StyleBoxFlat
        {
            BgColor = new Color(0.85f, 0.88f, 0.82f),
            CornerRadiusTopLeft = 4, CornerRadiusTopRight = 4,
            CornerRadiusBottomLeft = 4, CornerRadiusBottomRight = 4,
        });
        _step = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            Modulate = new Color(1, 1, 1, 0.6f),
        };
        _step.AddThemeFontSizeOverride("font_size", 16);
        column.AddChild(title);
        column.AddChild(_bar);
        column.AddChild(_step);
        middle.AddChild(column);
        _veil.AddChild(middle);
        AddChild(_veil);
    }

    public override void _Process(double delta)
    {
        if (!_changed)
        {
            if (++_frames < Settle)
                return;
            _changed = true;
            _frames = 0;
            GetTree().ChangeSceneToFile(_scene);
            return;
        }
        Node? now = GetTree().CurrentScene;
        if (now is null || now == _from || (_staged && !_built) || ++_frames < Settle)
            return;
        _lifting = true;
        // Clamped: the first frame after the build carries its whole time as
        // delta, and the fade would be over before it was drawn.
        float step = Math.Min((float)delta, 1.0f / 30.0f) / Fade;
        float alpha = _veil.Modulate.A - step;
        if (alpha <= 0.0f)
        {
            QueueFree();
            return;
        }
        _veil.Modulate = new Color(1, 1, 1, alpha);
        _veil.MouseFilter = Control.MouseFilterEnum.Ignore;
    }
}
