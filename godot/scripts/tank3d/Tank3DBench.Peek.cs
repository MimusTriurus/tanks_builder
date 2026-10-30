using System;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The card of the unit under the pointer (docs/hud3d-plan.md, «Карточка под
/// курсором», 2026-10-30): a window beside the hull, joined to it by a leader
/// that breaks once - out of the hull at 45°, then level into the card.
/// <list type="bullet">
/// <item><b>What it is</b> - the class and the model's name, the side's
/// colour down the card's edge in a match.</item>
/// <item><b>What it can do</b> - might, steps, mass and the three plates
/// (front, side, rear) as marks and not as numbers: a row of slots, as many as
/// the figure can ever be across the five classes, filled up to this one's. A
/// glance compares two units by the length of their rows.</item>
/// </list>
/// Any unit but the chosen one - that one has the same panel bottom left
/// (<c>Tank3DBench.Card</c>), with the turn in it. The figures here are the
/// class's, not the turn's.
/// </summary>
public sealed partial class Tank3DBench
{
    private const float PeekW = 188.0f, PeekH = 196.0f;
    /// <summary>The leader's slant leg and how far the card stands off its
    /// knee, px.</summary>
    private const float PeekLeg = 46.0f, PeekReach = 22.0f;
    private const float PeekFadeTime = 0.12f;

    /// <summary>How many slots each row has: the most any class has of it
    /// (might V of the mortar, steps 5 of the light, mass and front III of
    /// the heavy).</summary>
    private const int PeekMight = 5, PeekSteps = 5, PeekMass = 3, PeekArmour = 3;

    private static readonly Color PeekFire = new(0.95f, 0.55f, 0.20f);
    private static readonly Color PeekLine = new(0.91f, 0.90f, 0.86f, 0.9f);
    private static readonly Color PeekShade = new(0.0f, 0.0f, 0.0f, 0.55f);

    private Control? _peek;
    private StyleBoxFlat? _peekBox;

    // the unit shown, filled each frame from what is under the pointer
    private TankModel? _peekModel;
    private Node3D? _peekRig;
    private MovementProfile _peekProfile = MovementProfile.Light;
    private string _peekTag = "";
    private Side _peekSide;
    /// <summary>The tag the name below was looked up for.</summary>
    private string _peekNamed = "", _peekTitle = "", _peekName = "";
    private float _peekUp;
    /// <summary>A standing unit's class, and the tag it was looked up for.</summary>
    private string _peekClassOf = "";
    private MovementProfile _peekClass = MovementProfile.Light;

    private void BuildPeek(CanvasLayer layer)
    {
        if (_noUi)
            return;
        _peekBox = new StyleBoxFlat
        {
            BgColor = new Color(0.08f, 0.085f, 0.075f, 0.9f),
            BorderColor = new Color(0.34f, 0.35f, 0.31f),
        };
        _peekBox.SetBorderWidthAll(1);
        _peekBox.SetCornerRadiusAll(8);
        // the whole screen: the leader runs from the hull to the card
        _peek = new Control { Name = "UnitPeek", MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        _peek.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _peek.Draw += DrawPeek;
        layer.AddChild(_peek);
    }

    /// <summary>Every frame, after <c>AimTick</c> has said what is under the
    /// pointer.</summary>
    private void PeekTick()
    {
        if (_peek is null)
            return;
        TankModel? was = _peekModel;
        _peekModel = null;
        _peekRig = null;
        if (_overParked is { } p && p.Fate != Fate.Destroyed)
        {
            // the class off the lineup when the tag changes: its lookup
            // allocates, and this runs every frame of a hover
            if (!ReferenceEquals(_peekClassOf, p.Tag))
            {
                _peekClassOf = p.Tag;
                _peekClass = ClassFor(p.Tag);
            }
            PeekAt(p.Model, p.Rig, _peekClass, p.Tag, p.Side);
        }
        else if (_overTarget && _other is { } o && o.Fate != Fate.Destroyed)
            PeekAt(o.Model, o.Rig, o.Profile, o.Tag, o.Side);
        else if (_overOwn && !_chosen && _fate != Fate.Destroyed)
            PeekAt(_model, _rig, _profile, _modelTag, _side);

        if (_peekModel is null || !IsInstanceValid(_peekModel) || _peekRig is null)
        {
            _peekUp = 0.0f;
            _peek.Visible = false;
            return;
        }
        // a new unit fades in afresh
        if (!ReferenceEquals(was, _peekModel))
            _peekUp = 0.0f;
        _peekUp = Mathf.Min(1.0f, _peekUp + (float)GetProcessDeltaTime() / PeekFadeTime);
        _peek.Modulate = new Color(1.0f, 1.0f, 1.0f, _peekUp);
        _peek.Visible = true;
        _peek.QueueRedraw();
    }

    private void PeekAt(TankModel model, Node3D rig, MovementProfile profile, string tag, Side side)
    {
        _peekModel = model;
        _peekRig = rig;
        _peekProfile = profile;
        _peekTag = tag;
        _peekSide = side;
    }

    private void DrawPeek()
    {
        if (_peek is not { } c || _peekBox is null || _peekModel is not { } model || _peekRig is not { } rig)
            return;
        Font font = ThemeDB.FallbackFont;
        Vector2 screen = c.Size;

        // the leader's foot: on the hull, a little over its middle
        Vector2 foot = _camera.UnprojectPosition(rig.GlobalPosition
                                                 + Vector3.Up * (model.Size.Y * model.PixelsPerUnit * 0.6f));
        // right and up by default; to the other side where the card would
        // leave the screen
        float sx = foot.X + PeekLeg + PeekReach + PeekW + 8.0f > screen.X ? -1.0f : 1.0f;
        float sy = foot.Y - PeekLeg - PeekH * 0.5f - 8.0f < 0.0f ? 1.0f : -1.0f;
        var knee = foot + new Vector2(sx * PeekLeg, sy * PeekLeg);
        // the leader comes in at the title's height on the near edge
        float left = sx > 0.0f ? knee.X + PeekReach : knee.X - PeekReach - PeekW;
        float top = Mathf.Clamp(knee.Y - 26.0f, 8.0f, Mathf.Max(8.0f, screen.Y - PeekH - 8.0f));
        left = Mathf.Clamp(left, 8.0f, Mathf.Max(8.0f, screen.X - PeekW - 8.0f));
        var box = new Rect2(left, top, PeekW, PeekH);
        float into = Mathf.Clamp(knee.Y, top + 10.0f, top + PeekH - 10.0f);
        var edge = new Vector2(sx > 0.0f ? left : left + PeekW, into);

        PeekLeader(c, foot, knee, edge);

        // the class and the model's name, looked up when the tag changes
        if (!ReferenceEquals(_peekNamed, _peekTag))
        {
            _peekNamed = _peekTag;
            (_peekTitle, _peekName) = PanelNames(_peekProfile, _peekTag);
        }
        UnitPanel(c, font, box, _peekProfile, _peekTitle, _peekName, _peekSide, Badge.None, turn: false);
    }

    /// <summary>The class's short name and the model's, off the lineup -
    /// which allocates, so a caller keeps them until the tag changes.</summary>
    private static (string Title, string Name) PanelNames(MovementProfile profile, string tag)
    {
        int cls = Array.IndexOf(MovementProfile.All, profile);
        int pair = Lineup.PairAt(tag);
        return (cls >= 0 ? ClassShort[cls] : profile.Tag, pair >= 0 ? Lineup.Pairs[pair].Name : tag);
    }

    /// <summary>
    /// The unit's panel in <paramref name="box"/>: the one beside a hull under
    /// the pointer and the chosen one's bottom left are this. <paramref name="turn"/>
    /// - the chosen one's: its steps row says the turn rather than the class
    /// (what is left, what the way under the pointer spends), and its state's
    /// sign stands in the title row.
    /// </summary>
    private void UnitPanel(Control c, Font font, Rect2 box, MovementProfile profile, string title, string name,
                           Side side, Badge badge, bool turn)
    {
        float left = box.Position.X, top = box.Position.Y;
        c.DrawStyleBox(_peekBox!, box);

        // the side's colour down the edge, in a match
        if (Match)
            c.DrawRect(new Rect2(left + 1.0f, top + 8.0f, 3.0f, box.Size.Y - 16.0f), SideInk(side));

        var at = new Vector2(left + 14.0f, top + 28.0f);
        c.DrawString(font, at, title, HorizontalAlignment.Left, -1, 20, CardPale);
        float tw = font.GetStringSize(title, HorizontalAlignment.Left, -1, 20).X;
        c.DrawString(font, at + new Vector2(tw + 8.0f, 0.0f), name, HorizontalAlignment.Left, -1, 14, CardDim);
        if (badge != Badge.None)
            Sign(c, badge, new Vector2(left + box.Size.X - 20.0f, top + 22.0f), 9.0f);
        c.DrawLine(new Vector2(left + 12.0f, top + 40.0f), new Vector2(left + box.Size.X - 12.0f, top + 40.0f),
                   new Color(0.34f, 0.35f, 0.31f), 1.0f);

        // the figures: a label, then the slots
        float x = left + 14.0f, slots = left + 78.0f;
        float y = top + 60.0f;
        PeekRow(c, font, x, slots, y, "огонь", profile.Might, PeekMight, PeekMark.Dot, PeekFire);
        y += 20.0f;
        if (turn)
            TurnSteps(c, font, x, slots, y);
        else
            PeekRow(c, font, x, slots, y, "ход", profile.Steps, PeekSteps, PeekMark.Dot, CardPale);
        y += 20.0f;
        PeekRow(c, font, x, slots, y, "вес", profile.Mass, PeekMass, PeekMark.Square, CardPale);
        // the plates under their own heading, each on its line
        y += 24.0f;
        c.DrawString(font, new Vector2(x, y + 4.0f), "броня", HorizontalAlignment.Left, -1, 13, CardDim);
        y += 18.0f;
        PeekPlate(c, font, profile, x + 10.0f, slots, y, "лоб", Gunnery.Plate.Front);
        y += 18.0f;
        PeekPlate(c, font, profile, x + 10.0f, slots, y, "борт", Gunnery.Plate.Side);
        y += 18.0f;
        PeekPlate(c, font, profile, x + 10.0f, slots, y, "корма", Gunnery.Plate.Rear);
    }

    /// <summary>
    /// The chosen one's steps: the turn's budget (the road's step and the
    /// mud's one in it, Steps.BudgetAt) in slots, the way shown lights those it
    /// would spend, the spent ones hollow - a move made in a match spends the
    /// lot, there is no second.
    /// </summary>
    private void TurnSteps(Control c, Font font, float x, float slots, float y)
    {
        c.DrawString(font, new Vector2(x, y + 4.0f), "ход", HorizontalAlignment.Left, -1, 13, CardDim);
        bool live = CourseShown && _reach is not null;
        int budget = live ? _reach!.Budget : _profile.Steps;
        int left = Match && _drove ? 0 : budget;
        int spend = live ? Math.Min(_wayCost, left) : 0;
        // the lit ones are the last of the whole ones, as the old dots were
        PeekSlots(c, slots, y, left - spend, Math.Max(PeekSteps, budget), PeekMark.Dot, CardPale, CardDim);
        if (spend > 0)
            PeekSlots(c, slots + (left - spend) * 15.0f, y, spend, spend, PeekMark.Dot, CardSpend, CardDim);
    }

    /// <summary>The leader: a dot on the hull, a slant to the knee, level into
    /// the card - dark under pale, so it reads over grass and over snow.</summary>
    private static void PeekLeader(Control c, Vector2 foot, Vector2 knee, Vector2 edge)
    {
        c.DrawLine(foot, knee, PeekShade, 4.0f, true);
        c.DrawLine(knee, edge, PeekShade, 4.0f, true);
        c.DrawCircle(foot, 5.0f, PeekShade);
        c.DrawLine(foot, knee, PeekLine, 1.6f, true);
        c.DrawLine(knee, edge, PeekLine, 1.6f, true);
        c.DrawCircle(foot, 3.2f, PeekLine);
    }

    private enum PeekMark { Dot, Square }

    private static void PeekRow(Control c, Font font, float x, float slots, float y, string label,
                                int value, int max, PeekMark mark, Color ink)
    {
        c.DrawString(font, new Vector2(x, y + 4.0f), label, HorizontalAlignment.Left, -1, 13, CardDim);
        PeekSlots(c, slots, y, value, max, mark, ink, CardDim);
    }

    /// <summary>One plate's row: its name, then squares in the plate's colour
    /// (red to deep green, as on the chosen one's hex); a bare plate is its
    /// empty slots ringed in red.</summary>
    private static void PeekPlate(Control c, Font font, MovementProfile profile, float x, float slots, float y,
                                  string label, Gunnery.Plate plate)
    {
        int value = Gunnery.ArmourAt(profile, plate);
        Color ink = PlateInk[Math.Clamp(value, 0, 3)];
        c.DrawString(font, new Vector2(x, y + 4.0f), label, HorizontalAlignment.Left, -1, 12, CardDim);
        PeekSlots(c, slots, y, value, PeekArmour, PeekMark.Square, ink, value == 0 ? PlateInk[0] : CardDim);
    }

    private static void PeekSlots(Control c, float x, float y, int value, int max, PeekMark mark, Color fill, Color empty)
    {
        const float r = 4.5f, gap = 15.0f;
        for (int i = 0; i < max; i++)
        {
            var p = new Vector2(x + i * gap, y);
            bool full = i < value;
            if (mark == PeekMark.Dot)
            {
                if (full)
                    c.DrawCircle(p, r, fill);
                else
                    c.DrawArc(p, r - 0.75f, 0.0f, Mathf.Tau, 16, empty, 1.5f, true);
            }
            else
            {
                var sq = new Rect2(p.X - r, p.Y - r, r * 2.0f, r * 2.0f);
                if (full)
                    c.DrawRect(sq, fill);
                else
                    c.DrawRect(sq.Grow(-0.75f), empty, false, 1.5f);
            }
        }
    }
}
