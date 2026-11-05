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
/// The button stands left of «Конец хода», bottom right; Backspace (and Ctrl+Z).
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
        public (int A, int B, int C, int Shots)? Watch;
    }

    private MoveSnap? _moveSnap;
    private Button? _undoButton;
    private string _undoShown = "?";

    /// <summary>The corner's two buttons, sides in the layout, px: «Конец хода»
    /// the bigger (the author's note, 2026-11-01), and their icons.</summary>
    private const float PassSide = 64.0f, UndoSide = 48.0f;
    private const int PassIconPx = 40, UndoIconPx = 28;

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
            return L.T("undo.over");
        if (_side != _mt.Turn || _moveSnap is not { } s || !_mt.Drove)
            return L.T("undo.nothing");
        if (_fate != Fate.Alive)
            return L.T("undo.knocked");
        if (_passIn is not null)
            return L.T("undo.passing");
        if (MatchBusy)
            return L.T("undo.wait_order");
        if (_mt.Fired != s.Fired || _mt.Bonus != s.Bonus)
            return L.T("undo.acted");
        if (_razed.Count != s.Razed)
            return L.T("undo.wood_razed");
        if (MinesSpent() != s.MinesSpent)
            return L.T("undo.mine");
        // a round was fired at it on the way: the enemy's ambush is spent (Д7)
        if (_ambushShots != s.AmbushShots)
            return L.T("undo.ambush");
        // smoke driven into is gone (Д8)
        if (_smokeDispersed != s.SmokeDispersed)
            return L.T("undo.smoke");
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
        _matchNote = L.T("undo.done");
        Choose(true);
        GlideTo(_rig.Position);
        Log.Print($"tank3d: match: {SideName(_side)} {_modelTag} move taken back to {MapAt(s.Cell)}");
    }

    private void BuildUndo(CanvasLayer layer)
    {
        // left of «Конец хода», smaller, their bottoms in line: the turn's two
        // buttons in the corner, clear of the acts in the bottom centre
        float right = -12.0f - PassSide - 10.0f;
        var undo = new Button
        {
            AnchorLeft = 1.0f, AnchorRight = 1.0f, AnchorTop = 1.0f, AnchorBottom = 1.0f,
            OffsetLeft = right - UndoSide, OffsetRight = right, OffsetTop = -12.0f - UndoSide, OffsetBottom = -12.0f,
            FocusMode = Control.FocusModeEnum.None,
            Disabled = true,
        };
        undo.Pressed += UndoMove;
        TurnButton(undo, "undo", UndoIconPx, "Backspace");
        layer.AddChild(undo);
        _undoButton = undo;
    }

    /// <summary>Every frame: the button lit or greyed, and why in its tip -
    /// set only when the reason changes.</summary>
    private void UndoTick()
    {
        // the end of the turn beside it, greyed with the match over (Д6)
        // ActOn: its icon greyed with it
        if (_passButton is not null)
            ActOn(_passButton, !MatchOver && !AiTurn);
        if (_undoButton is null)
            return;
        // no move to take back before the first turn (Tank3DBench.Deploy)
        // nor on the AI's turn: its moves are not the player's to take back (Tank3DBench.Ai)
        _undoButton.Visible = !Deploying && !AiTurn;
        string why = UndoWhy();
        if (ReferenceEquals(why, _undoShown))
            return;
        _undoShown = why;
        // why it is grey - on its card (Tank3DBench.ActTips)
        ActOn(_undoButton, why.Length == 0);
    }
}
