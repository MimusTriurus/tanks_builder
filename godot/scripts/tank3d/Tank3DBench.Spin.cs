using Godot;

namespace TankSpriteTest;

/// <summary>
/// The turn on the spot (docs/hud3d-plan.md, «Разворот на месте»,
/// 2026-10-29): the rose round the chosen tank itself. The pointer on the
/// tank with no order in hand opens it - up to five arrows, one to each side
/// but the one the hull faces that the budget has the turn for
/// (<see cref="Steps.Turning"/>, the short way round; <see cref="RoseDraw"/>)
/// - and it stays open while the pointer is within reach of them. The
/// pointer on an arrow stands the ghost on the own cell turned that way; a
/// click on one is the order, at once - an arrow is a choice made, not a
/// cell that could be missed. A/D open it too and walk the pick round, Enter
/// orders the pick, ESC lets the pick go. The wheel stays the zoom's. Both
/// roses come and go over <see cref="RoseFadeTime"/>, not at once.
///
/// A turn on the spot is the turn's move, not its action (GDD match.md,
/// «Структура хода» п. 2-3): after it the unit drives no more, and fires.
/// The keys used to turn sixty degrees an order, and in a match the first
/// spent the move - a second was refused, so a hull could not come round
/// further than one side.
///
/// <c>--do spin</c> holds it open, <c>spin-hover=S</c> holds the pointer on
/// the disc of side S (30..330), <c>spin-to=S</c> clicks it.
/// </summary>
public sealed partial class Tank3DBench
{
    /// <summary>How long a rose takes to come or go, s.</summary>
    private const float RoseFadeTime = 0.18f;

    private bool _spinOpen;
    /// <summary>How much of the rose is drawn, 0..1, on its way in or out.</summary>
    private float _spinFade;
    /// <summary>The side A/D walked to, ordered by Enter; it holds the rose
    /// open without the pointer.</summary>
    private int? _spinPick;
    /// <summary><c>--do spin-hover</c>: a disc held under the pointer.</summary>
    private int? _spinHeld;
    /// <summary><c>--do spin</c>: open without the pointer.</summary>
    private bool _spinPinned;

    /// <summary>The rose round the own tank may be had: chosen, its move
    /// still to make, nothing ordered.</summary>
    private bool SpinMay =>
        CourseShown && _reach is not null && _lockCell is null && !RouteMoving && _turnGoal is null
        && _ramWaiting is null && _shotWaiting is null;

    /// <summary>The pick, the pin and the held disc let go: the rose stays
    /// only while the pointer keeps it.</summary>
    private void SpinLetGo()
    {
        _spinPick = null;
        _spinHeld = null;
        _spinPinned = false;
    }

    /// <summary>What turning to <paramref name="side"/> costs, or null past
    /// the budget.</summary>
    private int? SpinCost(int side)
    {
        if (_reach is null)
            return null;
        int cost = Steps.Turning(HullSide(), side);
        return cost <= _reach.Budget ? cost : null;
    }

    /// <summary>The disc the pointer is on, the one held for a capture, or
    /// the one A/D walked to - the side the ghost shows.</summary>
    private int? SpinShown()
    {
        if (!_spinOpen)
            return null;
        return _spinHeld ?? SpinAt(GetViewport().GetMousePosition()) ?? _spinPick;
    }

    /// <summary>The disc under <paramref name="screen"/>, if the rose is open.</summary>
    private int? SpinAt(Vector2 screen)
    {
        if (!_spinOpen || _field is null || _tile is null)
            return null;
        float r = RoseDisc();
        Vector2I cell = CellHere;
        Vector2 mid = Screen(cell);
        int here = HullSide();
        foreach (int side in HexField.EdgeHeadings)
            if (side != here && SpinDisc(cell, mid, side).DistanceTo(screen) <= r * 1.3f && SpinCost(side) is not null)
                return side;
        return null;
    }

    /// <summary>The pointer within reach of the discs - on the way from the
    /// hull out to one of them, the rose stays.</summary>
    private bool SpinNear(Vector2 screen) =>
        Screen(CellHere).DistanceTo(screen) <= HexScreen() * 1.25f + RoseDisc() * 1.6f;

    /// <summary>A rose's disc on the screen - this one's and the fixed cell's
    /// (Tank3DBench.Ghost): a quarter past the middle of the edge toward
    /// <paramref name="side"/>.</summary>
    private Vector2 SpinDisc(Vector2I cell, Vector2 mid, int side)
    {
        Vector2 flat = CellFlat(cell);
        Vector2 way = _camera.UnprojectPosition(OnTop(cell, flat + (CellFlat(HexField.Step(cell, side)) - flat) * 0.5f)) - mid;
        return mid + way * 1.25f;
    }

    /// <summary>The order: the hull round to <paramref name="side"/>, out of
    /// the move's steps.</summary>
    private void SpinTo(int side)
    {
        if (!SpinMay || _fate != Fate.Alive)
            return;
        int? cost = SpinCost(side);
        if (cost is null)
        {
            _orderNote = "на этот разворот шагов нет";
            return;
        }
        SpinLetGo();
        if (cost == 0)
            return;
        if (MatchMoveWhy() is { Length: > 0 } no)
        {
            _orderNote = no;
            return;
        }
        MatchMoved();
        _route = null;
        _turnGoal = HeadingFor(CellHere, side);
        _orderNote = $"разворот: {cost} из {_reach!.Budget}";
        Log.Print($"tank3d: turn on the spot {MapAt(CellHere)} to {side}, {cost} step(s)");
    }

    /// <summary>A/D: the pick a side round, past the sides the budget does
    /// not have - the rose open with it.</summary>
    private void SpinStep(bool left)
    {
        if (!SpinMay)
        {
            _orderNote = MatchMoveWhy() is { Length: > 0 } no ? no : "";
            return;
        }
        _spinOpen = true;
        int from = _spinPick ?? HullSide();
        for (int k = 1; k < 6; k++)
        {
            int side = ((from + (left ? 60 : -60) * k) % 360 + 360) % 360;
            if (SpinCost(side) is not null)
            {
                _spinPick = side;
                return;
            }
        }
    }

    /// <summary>Every frame from <c>CourseTick</c>: open while the pointer is
    /// on the tank or near its discs, a pick or a pin holds it; shut when it
    /// may no longer be had (the move made, the tank let go, another taken).
    /// The pointer on the tank is the last frame's (<c>UnderTick</c> comes
    /// after).</summary>
    private void SpinTick()
    {
        bool may = SpinMay;
        bool keep = may && (_spinPinned || _spinPick is not null || _spinHeld is not null || _overOwn
                            || (_spinOpen && SpinNear(GetViewport().GetMousePosition())));
        _spinOpen = keep;
        if (!keep)
            SpinLetGo();
        _spinFade = Mathf.MoveToward(_spinFade, keep ? 1.0f : 0.0f, (float)GetProcessDeltaTime() / RoseFadeTime);
    }

    private void DrawSpin(Control ink)
    {
        if (_spinFade <= 0.0f || _reach is null || _field is null || _tile is null)
            return;
        float r = RoseDisc();
        Vector2I cell = CellHere;
        Vector2 mid = Screen(cell);
        int here = HullSide();
        int? shown = SpinShown();
        foreach (int side in HexField.EdgeHeadings)
        {
            // the side the hull faces already has no disc: a turn to it is
            // none; nor has one past the budget (the author's notes, 2026-10-29)
            if (side == here || SpinCost(side) is null)
                continue;
            Vector2 at = SpinDisc(cell, mid, side);
            // the side shown blue, as the fixed cell's rose shows the ghost's
            RoseDraw(ink, at, at - mid, r, _spinFade, side == shown, side == shown);
        }
    }

    /// <summary>A rose's arrow, filled and its outline closed: kept, not new
    /// each frame.</summary>
    private static readonly Vector2[] RoseTri = new Vector2[3], RoseRim = new Vector2[4];

    /// <summary>
    /// One mark of a rose: a filled arrowhead pointing out along
    /// <paramref name="way"/>, white on a soft dark one, its edge in the
    /// course's blue; all blue when <paramref name="on"/> (the side the ghost
    /// faces), a thicker edge and a little bigger when
    /// <paramref name="picked"/>. It was a disc with the turn's cost on it
    /// (+1..+3): an arrow says «turn here» without being read, and the cost
    /// is the card's step dots under the pointer (the author's call,
    /// 2026-10-29). A side past the budget has no mark. <paramref name="a"/>
    /// is the rose's coming and going: the mark grows from a little smaller
    /// as it fades in. Filled, not a chevron: the way's own chevrons are
    /// lines (<c>DrawWay</c>).
    /// </summary>
    private static void RoseDraw(Control ink, Vector2 at, Vector2 way, float r, float a, bool on, bool picked)
    {
        if (way.LengthSquared() < 1e-6f)
            return;
        float k = Mathf.SmoothStep(0.0f, 1.0f, a);
        r *= (picked ? 1.2f : 1.0f) * Mathf.Lerp(0.6f, 1.0f, k);
        Vector2 d = way.Normalized(), n = new(-d.Y, d.X);
        Color fill = on ? CourseInk : new Color(0.92f, 0.95f, 0.98f);
        Color edge = on ? new Color(0.92f, 0.95f, 0.98f) : CourseInk;
        fill.A *= k;
        edge.A *= k;
        // the soft dark under it, for the grass and the water
        Arrow(at, d, n, r * 1.25f);
        ink.DrawColoredPolygon(RoseTri, new Color(0.0f, 0.0f, 0.0f, 0.35f * k));
        Arrow(at, d, n, r);
        ink.DrawColoredPolygon(RoseTri, fill);
        RoseRim[0] = RoseTri[0];
        RoseRim[1] = RoseTri[1];
        RoseRim[2] = RoseTri[2];
        RoseRim[3] = RoseTri[0];
        ink.DrawPolyline(RoseRim, edge, picked ? 3.0f : 1.5f, true);
    }

    /// <summary>The arrowhead about <paramref name="at"/> into
    /// <see cref="RoseTri"/>: its tip out along <paramref name="d"/>.</summary>
    private static void Arrow(Vector2 at, Vector2 d, Vector2 n, float r)
    {
        RoseTri[0] = at + d * (r * 1.05f);
        RoseTri[1] = at - d * (r * 0.65f) + n * (r * 0.95f);
        RoseTri[2] = at - d * (r * 0.65f) - n * (r * 0.95f);
    }
}
