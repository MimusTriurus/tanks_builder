using System.IO;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The mouse pointer says what a click will do (docs/hud3d-plan.md,
/// «Курсоры», 2026-10-30):
/// <list type="bullet">
/// <item><b>The ram's burst</b> the whole time the ram is on (Tab, <b>1</b>,
/// the «Таран» button) - wherever the pointer is, since the mode is what
/// the left button now means.</item>
/// <item><b>The reticle</b> on a target under the pointer (<c>_overTarget</c>,
/// <c>UnderTick</c>) - the right button there is the shot.</item>
/// <item>The system's arrow everywhere else.</item>
/// </list>
/// The pictures are <c>assets/Images/Cursors/{attack,ram}_{32,64}.png</c>,
/// read off disk like the rest of the art (<see cref="AssetRoot.Cursors"/>);
/// the 64 px ones on a screen past 120 dpi. Hotspot in the centre of both.
/// A missing file leaves the arrow, and says so once.
/// </summary>
public sealed partial class Tank3DBench
{
    private enum Pointer { Arrow, Attack, Ram }

    private Texture2D? _cursorAttack, _cursorRam;
    private Vector2 _cursorSpot;
    private Pointer _pointer = Pointer.Arrow;

    private void BuildCursors()
    {
        int px = DisplayServer.ScreenGetDpi() > 120 ? 64 : 32;
        _cursorSpot = new Vector2(px / 2, px / 2);
        _cursorAttack = CursorArt("attack", px);
        _cursorRam = CursorArt("ram", px);
        Log.Print($"tank3d: cursors {px} px");
    }

    private static Texture2D? CursorArt(string name, int px)
    {
        string path = $"{AssetRoot.Cursors}/{name}_{px}.png";
        if (!File.Exists(path))
        {
            Log.Print($"tank3d: cursor: no {path}, the arrow stays");
            return null;
        }
        return Image.LoadFromFile(path) is { } image ? ImageTexture.CreateFromImage(image) : null;
    }

    /// <summary>Every frame, after <c>AimTick</c>; the system is told only on a change.</summary>
    private void CursorTick()
    {
        Pointer want = _ramMode ? Pointer.Ram
            : _overTarget && _fate == Fate.Alive && !RamRunning ? Pointer.Attack
            : Pointer.Arrow;
        if (want == _pointer)
            return;
        _pointer = want;
        Texture2D? art = want switch
        {
            Pointer.Attack => _cursorAttack,
            Pointer.Ram => _cursorRam,
            _ => null,
        };
        Input.SetCustomMouseCursor(art, Input.CursorShape.Arrow, art is null ? Vector2.Zero : _cursorSpot);
    }

    /// <summary>The arrow back, on the way out: a custom pointer outlives the
    /// scene that set it.</summary>
    private void CursorReset()
    {
        if (_pointer != Pointer.Arrow)
            Input.SetCustomMouseCursor(null);
        _pointer = Pointer.Arrow;
    }
}
