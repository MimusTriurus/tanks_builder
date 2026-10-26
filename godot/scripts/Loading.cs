using System;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// A veil over a scene change: the editor's Run and the bench's Edit
/// (<see cref="Session"/>).
///
/// <b>The change is not asynchronous, and this does not make it so.</b>
/// <c>ChangeSceneToFile</c> loads on the main thread, and the scene that
/// comes in builds its whole board in <c>_Ready</c> - relief, grass, models,
/// a second or three - so the window used to sit on the last frame of the
/// scene going out, which reads as a hang. The veil is put on screen first,
/// the change waits until it has been drawn, and the blocked frames show it
/// instead. Nothing on it moves: nothing could, the thread is busy.
///
/// <b>Hung off the tree's root, not the scene</b>, so the change that frees
/// the scene leaves it standing; it lifts itself once the new scene has
/// drawn a few frames (the first ones also compile its shaders) and frees
/// itself when it has faded.
/// </summary>
public sealed partial class Loading : CanvasLayer
{
    /// <summary>Frames drawn before the change, so the veil is on screen
    /// when the thread blocks; and after it, before the veil lifts.</summary>
    private const int Settle = 3;

    /// <summary>Seconds the veil takes to fade.</summary>
    private const float Fade = 0.25f;

    private string _scene = "";
    private string _what = "";
    private Node? _from;
    private bool _changed;
    private int _frames;
    private ColorRect _veil = null!;

    /// <summary>Change to <paramref name="scene"/> under a veil that says
    /// <paramref name="what"/>.</summary>
    public static void Go(SceneTree tree, string scene, string what)
    {
        // Once: a second F5 in the frames before the change would queue a
        // second change behind it. One already lifting does not count - the
        // way back can be asked for while the veil it came under still fades.
        if (_on is not null && IsInstanceValid(_on) && !_on._changed)
            return;
        _on = new Loading { Layer = 128, _scene = scene, _what = what };
        // Deferred: --menu run presses Run from the editor's _Ready, while the
        // root is still adding its children and refuses one more.
        tree.Root.CallDeferred(Node.MethodName.AddChild, _on);
    }

    private static Loading? _on;

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        _from = GetTree().CurrentScene;
        _veil = new ColorRect
        {
            Color = new Color(0.07f, 0.08f, 0.09f),
            // Clicks and the wheel stop here: the scene going out must not
            // take an order in the frames before it goes.
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        _veil.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        var label = new Label
        {
            Text = _what,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        label.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        label.AddThemeFontSizeOverride("font_size", 28);
        _veil.AddChild(label);
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
        if (now is null || now == _from || ++_frames < Settle)
            return;
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
