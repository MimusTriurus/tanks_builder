using Godot;

namespace TankSpriteTest;

/// <summary>
/// The move taken back (docs/hud3d-plan.md, «Отмена хода»; the author's rule,
/// 2026-10-30, against match.md's old «отменить движение нельзя»): the unit on
/// the rig back on the cell and heading it set out from, its move to be made
/// again, while it is still whole.
/// <list type="bullet">
/// <item><b>A snapshot as the move is given</b> (<see cref="MatchMoved"/>): the
/// cell, the heading, the turret and the gun, the mud on the hull, what the
/// turn had spent - and what of the board the move may spend, the mines and
/// the wood a heavy drives into.</item>
/// <item><b>Off once the unit is out</b>: knocked out by a mine on the way (on
/// the board a mine knocks out, GDD field.md) or drowned - the author's
/// «уничтожен миной или засадой». Ambush is not in the bench yet.</item>
/// <item><b>Off once the turn has done more than the move</b>: a shot, a ram,
/// a fire put out after it, a wood razed - those changed the board, and the
/// snapshot is of the unit alone. The move's own trail is left: the ruts fade
/// by themselves.</item>
/// </list>
/// The button stands over «Конец хода», bottom right; Ctrl+Z.
/// </summary>
public sealed partial class Tank3DBench
{
    private sealed class MoveSnap
    {
        public Vector2I Cell;
        public float Heading, Yaw, Elevation, MudCoat, MudDry;
        public bool Fired, Bonus;
        public int Razed, MinesSpent, AmbushShots, SmokeDispersed;
        /// <summary>Its ambush before the move lifted it (Tank3DBench.Ambush).</summary>
        public (int A, int B, int Shots)? Watch;
    }

    private MoveSnap? _moveSnap;
    private Button? _undoButton;
    private string _undoShown = "?";

    private const string UndoText = "Отменить ход   Ctrl+Z";

    /// <summary>As <see cref="MatchMoved"/> marks the move: the unit as it set out.</summary>
    private void UndoSnap()
    {
        _moveSnap = new MoveSnap
        {
            Cell = CellHere, Heading = _heading, Yaw = _model.Yaw, Elevation = _model.Elevation,
            MudCoat = _mudCoat, MudDry = _mudDry, Fired = _mt.Fired, Bonus = _mt.Bonus,
            Razed = _razed.Count, MinesSpent = MinesSpent(), AmbushShots = _ambushShots, SmokeDispersed = _smokeDispersed,
            Watch = _ambush.TryGetValue(_unitId, out var watch) ? watch : null,
        };
    }

    private int MinesSpent()
    {
        int n = 0;
        for (int i = 0; i < _mines.Count; i++)
            if (_mines[i].Spent)
                n++;
        return n;
    }

    /// <summary>Why the move may not be taken back now, or empty. Constant
    /// words only: the button asks it every frame.</summary>
    private string UndoWhy()
    {
        if (!Match || MatchOver)
            return "партия окончена";
        if (_side != _mt.Turn || _moveSnap is not { } s || !_mt.Drove)
            return "отменять нечего — хода ещё не было";
        if (_fate != Fate.Alive)
            return "юнит подбит — ход не отменить";
        if (_passIn is not null)
            return "ход передаётся";
        if (MatchBusy)
            return "дождись конца приказа";
        if (_mt.Fired != s.Fired || _mt.Bonus != s.Bonus)
            return "после хода было действие — ход не отменить";
        if (_razed.Count != s.Razed)
            return "лес снесён — ход не отменить";
        if (MinesSpent() != s.MinesSpent)
            return "сработала мина — ход не отменить";
        // a round was fired at it on the way: the enemy's ambush is spent (Д7)
        if (_ambushShots != s.AmbushShots)
            return "сработала засада — ход не отменить";
        // smoke driven into is gone (Д8)
        if (_smokeDispersed != s.SmokeDispersed)
            return "дым рассеян — ход не отменить";
        return "";
    }

    /// <summary>Ctrl+Z and the button: the unit back where it set out from.</summary>
    private void UndoMove()
    {
        if (UndoWhy() is { Length: > 0 } why)
        {
            _matchNote = why;
            return;
        }
        MoveSnap s = _moveSnap!;
        CancelOrder();
        // the core's turn goes too, taken again at the next action (Tank3DBench.Shadow)
        ShadowDrop();
        _coastTo = null;
        _speed = 0;
        _home = s.Cell;
        Heading = s.Heading;
        _model.Yaw = s.Yaw;
        _model.Elevation = s.Elevation;
        _model.Apply();
        _mudCoat = s.MudCoat;
        _mudDry = s.MudDry;
        Park();
        // put back, it has not come on to anything (Tank3DBench.Rules, Ambush)
        _hexWas = CellHere;
        // the ambush the move lifted is back with it: the move never was
        if (s.Watch is { } watch)
            _ambush[_unitId] = watch;
        // the trail stops here: no rut drawn from where it stood to where it was
        _ruts?.Lift();
        _mt.Drove = false;
        _moveSnap = null;
        _matchNote = "ход отменён";
        Choose(true);
        GlideTo(_rig.Position);
        Log.Print($"tank3d: match: {SideName(_side)} {_modelTag} move taken back to {MapAt(s.Cell)}");
    }

    private void BuildUndo(CanvasLayer layer)
    {
        // over «Конец хода», the same width: the turn's two buttons in the
        // corner, clear of the acts in the bottom centre
        var undo = new Button
        {
            Text = UndoText,
            AnchorLeft = 1.0f, AnchorRight = 1.0f, AnchorTop = 1.0f, AnchorBottom = 1.0f,
            OffsetLeft = -246.0f, OffsetRight = -12.0f, OffsetTop = -56.0f - 8.0f - 40.0f, OffsetBottom = -56.0f - 8.0f,
            FocusMode = Control.FocusModeEnum.None,
            Disabled = true,
        };
        undo.AddThemeFontSizeOverride("font_size", 15);
        undo.Pressed += UndoMove;
        layer.AddChild(undo);
        _undoButton = undo;
    }

    /// <summary>Every frame: the button lit or greyed, and why in its tip -
    /// set only when the reason changes.</summary>
    private void UndoTick()
    {
        // the end of the turn beside it, greyed with the match over (Д6)
        if (_passButton is not null && _passButton.Disabled != MatchOver)
            _passButton.Disabled = MatchOver;
        if (_undoButton is null)
            return;
        string why = UndoWhy();
        if (ReferenceEquals(why, _undoShown))
            return;
        _undoShown = why;
        _undoButton.Disabled = why.Length > 0;
        _undoButton.TooltipText = why.Length > 0 ? why : "вернуть юнит туда, откуда он поехал";
    }
}
