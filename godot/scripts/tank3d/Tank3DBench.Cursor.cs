using System.IO;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The mouse pointer says what a click will do (docs/hud3d-plan.md,
/// «Курсоры», 2026-10-30):
/// <list type="bullet">
/// <item><b>The ram's burst</b> the whole time the ram is on (Tab, the
/// «Таран» button) - wherever the pointer is, since the mode is what the
/// left button now means.</item>
/// <item><b>The reticle</b> on a target under the pointer (<c>_overTarget</c>,
/// <c>UnderTick</c>) - the right button there is the shot. In a match its
/// colour says whether there is one (the author's note, 2026-10-30): red on
/// an enemy the chosen tank can shoot, from here or after its move, orange
/// on one of ours it can - friendly fire, with «Атака» on only - and grey on
/// any it cannot: out of reach (<c>_inReach</c>, the same reading as the red
/// or grey outline), the attack spent, or no tank chosen. Outside a match,
/// red. With «Атака» on (Tank3DBench.Acts) the grey one off a target too:
/// the mode is the left button's meaning, as the ram's.</item>
/// <item><b>The bench's own arrow</b> everywhere else (the author's note,
/// 2026-10-31: the system's one stood apart from the drawn HUD).</item>
/// </list>
/// The pictures are <c>assets/Images/Cursors/{arrow,attack,attack_ally,attack_off,ram}_{32,64}.png</c>,
/// read off disk like the rest of the art (<see cref="AssetRoot.Cursors"/>);
/// the 64 px ones on a screen past 120 dpi. Hotspot in the centre, the
/// arrow's at its tip. A missing file leaves the system's arrow, and says so once.
/// </summary>
public sealed partial class Tank3DBench
{
    private enum Pointer { System, Arrow, Attack, AttackAlly, AttackOff, Ram }

    private Texture2D? _cursorArrow, _cursorAttack, _cursorAttackAlly, _cursorAttackOff, _cursorRam;
    private Vector2 _cursorSpot, _arrowSpot;
    private Pointer _pointer = Pointer.System;

    private void BuildCursors()
    {
        int px = DisplayServer.ScreenGetDpi() > 120 ? 64 : 32;
        _cursorSpot = new Vector2(px / 2, px / 2);
        // the arrow's tip, (3, 2) of its 32
        _arrowSpot = new Vector2(3, 2) * (px / 32);
        _cursorArrow = CursorArt("arrow", px);
        _cursorAttack = CursorArt("attack", px);
        _cursorAttackAlly = CursorArt("attack_ally", px);
        _cursorAttackOff = CursorArt("attack_off", px);
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
            : _overTarget && _fate == Fate.Alive && !RamRunning ? Reticle()
            : _attackMode ? Pointer.AttackOff
            : Pointer.Arrow;
        if (want == _pointer)
            return;
        _pointer = want;
        Texture2D? art = want switch
        {
            Pointer.Arrow => _cursorArrow,
            Pointer.Attack => _cursorAttack,
            Pointer.AttackAlly => _cursorAttackAlly,
            Pointer.AttackOff => _cursorAttackOff,
            Pointer.Ram => _cursorRam,
            _ => null,
        };
        Vector2 spot = art is null ? Vector2.Zero : want == Pointer.Arrow ? _arrowSpot : _cursorSpot;
        Input.SetCustomMouseCursor(art, Input.CursorShape.Arrow, spot);
    }

    /// <summary>The reticle's colour on the target under the pointer: red, an
    /// enemy to be shot; orange, one of ours to be shot; grey, no shot.</summary>
    private Pointer Reticle()
    {
        if (!Match || _other is not { } o)
            return Pointer.Attack;
        if (!_chosen || MatchShotSpent || !_inReach.Contains(o.Model))
            return Pointer.AttackOff;
        return o.Side == _side ? Pointer.AttackAlly : Pointer.Attack;
    }

    /// <summary>The system's arrow back, on the way out: a custom pointer
    /// outlives the scene that set it.</summary>
    private void CursorReset()
    {
        if (_pointer != Pointer.System)
            Input.SetCustomMouseCursor(null);
        _pointer = Pointer.System;
    }
}
