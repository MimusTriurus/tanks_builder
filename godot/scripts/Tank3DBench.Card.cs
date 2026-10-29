using System;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The chosen unit's card, bottom left (docs/hud3d-plan.md, «Карточка юнита»,
/// 2026-10-29): what the unit is and how much turn it has left, read without
/// a hover.
/// <list type="bullet">
/// <item><b>The class and the side</b> on the first line, the state's sign
/// (<see cref="Sign"/>, the one over its hull) under them.</item>
/// <item><b>The armour as a hex</b> laid as its cell is on the screen: the
/// six faces projected through the camera, the hull's front marked, each
/// face in its plate's colour and number, the roof in the middle
/// (<see cref="Gunnery.ArmourAt"/>). Where the unit is weak is where the
/// board says it is.</item>
/// <item><b>The might and the mass</b> in the GDD's numerals.</item>
/// <item><b>The steps</b> as dots: the way under the pointer lights the
/// ones it spends, a move made empties them.</item>
/// </list>
/// </summary>
public sealed partial class Tank3DBench
{
    private const float CardW = 262.0f, CardH = 152.0f;
    private const float CardHexR = 40.0f;

    private static readonly string[] Roman = { "0", "I", "II", "III", "IV", "V" };

    /// <summary>A plate's colour by its value, 0 to III: red where any gun
    /// gets in, to the heavy's deep green.</summary>
    private static readonly Color[] PlateInk =
    {
        new(0.89f, 0.29f, 0.29f), new(0.94f, 0.62f, 0.15f), new(0.59f, 0.77f, 0.35f), new(0.11f, 0.62f, 0.46f),
    };

    private static readonly Color CardPale = new(0.91f, 0.90f, 0.86f);
    private static readonly Color CardDim = new(0.64f, 0.63f, 0.59f);
    private static readonly Color CardSpend = new(0.36f, 0.79f, 0.65f);

    private static readonly string[] ClassShort = Array.ConvertAll(MovementProfile.All, p => p.Tag[..2]);

    private Control? _card;
    private StyleBoxFlat? _cardBox;
    private readonly Vector2[] _cardFace = new Vector2[6];
    private readonly Vector2[] _cardCorner = new Vector2[6];
    private readonly Vector2[] _cardTip = new Vector2[3];
    /// <summary>The model's name off the lineup, looked up when the rig
    /// changes hands and not every frame.</summary>
    private string _cardModel = "", _cardName = "";

    /// <summary>The steps the way shown costs, its turn at the end with them
    /// (<c>CourseTick</c>); nought with no way.</summary>
    private int _wayCost;

    private void BuildCard(CanvasLayer layer)
    {
        if (_noUi)
            return;
        _cardBox = new StyleBoxFlat
        {
            BgColor = new Color(0.08f, 0.085f, 0.075f, 0.86f),
            BorderColor = new Color(0.34f, 0.35f, 0.31f),
        };
        _cardBox.SetBorderWidthAll(1);
        _cardBox.SetCornerRadiusAll(10);
        _card = new Control
        {
            Name = "UnitCard",
            MouseFilter = Control.MouseFilterEnum.Ignore,
            AnchorTop = 1.0f, AnchorBottom = 1.0f,
            OffsetLeft = 12.0f, OffsetRight = 12.0f + CardW,
            OffsetTop = -12.0f - CardH, OffsetBottom = -12.0f,
        };
        _card.Draw += DrawCard;
        layer.AddChild(_card);
    }

    /// <summary>Every frame, after <c>AimTick</c>: shown while a unit is
    /// chosen, drawn again as the camera and the way move.</summary>
    private void CardTick()
    {
        if (_card is null)
            return;
        bool shown = _chosen && _field is not null && _tile is not null && _fate != Fate.Destroyed;
        _card.Visible = shown;
        if (shown)
            _card.QueueRedraw();
    }

    private void DrawCard()
    {
        if (_card is not { } c || _cardBox is null || _field is null)
            return;
        Font font = ThemeDB.FallbackFont;
        c.DrawStyleBox(_cardBox, new Rect2(Vector2.Zero, c.Size));

        // the class, the model's name, the side
        int cls = Array.IndexOf(MovementProfile.All, _profile);
        string title = cls >= 0 ? ClassShort[cls] : _profile.Tag;
        if (!ReferenceEquals(_cardModel, _modelTag))
        {
            _cardModel = _modelTag;
            int pair = Lineup.PairAt(_modelTag);
            _cardName = pair >= 0 ? Lineup.Pairs[pair].Name : _modelTag;
        }
        c.DrawString(font, new Vector2(14.0f, 30.0f), title, HorizontalAlignment.Left, -1, 22, CardPale);
        float tw = font.GetStringSize(title, HorizontalAlignment.Left, -1, 22).X;
        c.DrawString(font, new Vector2(22.0f + tw, 30.0f), _cardName, HorizontalAlignment.Left, -1, 14, CardDim);
        if (Match)
            c.DrawString(font, new Vector2(0.0f, 30.0f), SideName(_side), HorizontalAlignment.Right,
                         CardW - 14.0f, 14, SideInk(_side));

        CardArmour(c, font, new Vector2(66.0f, 96.0f));

        // the might and the mass
        const float col = 132.0f, val = 196.0f;
        c.DrawString(font, new Vector2(col, 66.0f), "мощь", HorizontalAlignment.Left, -1, 13, CardDim);
        c.DrawString(font, new Vector2(val, 66.0f), Roman[Math.Clamp(_profile.Might, 0, 5)], HorizontalAlignment.Left, -1, 17, CardPale);
        c.DrawString(font, new Vector2(col, 90.0f), "масса", HorizontalAlignment.Left, -1, 13, CardDim);
        c.DrawString(font, new Vector2(val, 90.0f), Roman[Math.Clamp(_profile.Mass, 0, 5)], HorizontalAlignment.Left, -1, 17, CardPale);

        // the state, in the sign it wears on the board
        Badge badge = _fate == Fate.Knocked || Match ? BadgeOf(_side, _modelTag, _fate, CellHere) : Badge.None;
        if (badge != Badge.None)
        {
            Sign(c, badge, new Vector2(col + 8.0f, 109.0f), 8.0f);
            c.DrawString(font, new Vector2(col + 22.0f, 114.0f), BadgeWord(badge), HorizontalAlignment.Left, -1, 13,
                         badge == Badge.Burning ? PlateInk[1] : CardPale);
        }

        CardSteps(c, font, new Vector2(col, 138.0f));
    }

    private static string BadgeWord(Badge b) => b switch
    {
        Badge.Burning => "горит",
        Badge.Knocked => "подбит",
        Badge.Drowned => "утоплен",
        Badge.Afloat => "плывёт",
        _ => "",
    };

    /// <summary>
    /// The armour on the cell's hex as the camera shows it: each face's way
    /// out of the cell projected to the screen and scaled to the card, so a
    /// face sits where the board's does and turns with the camera. A corner is
    /// two thirds of the sum of its two faces' midpoints - a regular hex's own
    /// sum, and a linear one, so it holds through the projection.
    /// </summary>
    private void CardArmour(Control c, Font font, Vector2 mid)
    {
        // on the cell's own top, halfway to each neighbour: the board's hex as
        // it is drawn, whatever the neighbour's height
        Vector2I cell = CellHere;
        Vector2 flat = CellFlat(cell);
        Vector2 o = _camera.UnprojectPosition(OnTop(cell, flat));
        for (int k = 0; k < 6; k++)
        {
            Vector2 way = CellFlat(HexField.Step(cell, HexField.EdgeHeadings[k])) - flat;
            _cardFace[k] = _camera.UnprojectPosition(OnTop(cell, flat + way * 0.5f)) - o;
        }
        float wide = 0.0f, high = 0.0f;
        for (int k = 0; k < 6; k++)
        {
            _cardCorner[k] = (_cardFace[k] + _cardFace[(k + 1) % 6]) * (2.0f / 3.0f);
            wide = Mathf.Max(wide, Mathf.Abs(_cardCorner[k].X));
            high = Mathf.Max(high, Mathf.Abs(_cardCorner[k].Y));
        }
        if (wide < 1e-3f || high < 1e-3f)
            return;
        // The board's hex squats to half its height under the camera; on the
        // card that leaves no room for the numbers, so it is stood up to a
        // regular one - the faces keep their places, only the squat goes.
        var scale = new Vector2(1.0f, Mathf.Clamp(0.866f * wide / high, 1.0f, 3.0f));
        float far = 0.0f;
        for (int k = 0; k < 6; k++)
            far = Mathf.Max(far, (_cardCorner[k] * scale).Length());
        scale *= CardHexR / far;
        for (int k = 0; k < 6; k++)
        {
            _cardFace[k] *= scale;
            _cardCorner[k] = mid + _cardCorner[k] * scale;
        }
        c.DrawColoredPolygon(_cardCorner, new Color(0.05f, 0.055f, 0.05f, 0.9f));

        int front = Array.IndexOf(HexField.EdgeHeadings, HullSide());
        for (int k = 0; k < 6; k++)
        {
            int plate = Plate(k, front);
            c.DrawLine(_cardCorner[(k + 5) % 6], _cardCorner[k], PlateInk[Math.Clamp(plate, 0, 3)], 2.5f + 1.8f * plate, true);
        }
        // the front: a tip out of its face
        if (front >= 0)
        {
            Vector2 n = _cardFace[front].Normalized();
            float a = _cardFace[front].Length();
            _cardTip[0] = mid + n * (a + 12.0f);
            _cardTip[1] = mid + n * (a + 5.0f) + n.Orthogonal() * 5.0f;
            _cardTip[2] = mid + n * (a + 5.0f) - n.Orthogonal() * 5.0f;
            c.DrawColoredPolygon(_cardTip, CardPale);
        }
        for (int k = 0; k < 6; k++)
            CardNumber(c, font, mid + _cardFace[k] * 0.62f, Roman[Plate(k, front)], 12, CardPale);
        // the roof, over the hull
        int roof = Gunnery.ArmourAt(_profile, Gunnery.Plate.Roof);
        c.DrawCircle(mid, 10.0f, PlateInk[Math.Clamp(roof, 0, 3)]);
        CardNumber(c, font, mid, Roman[roof], 12, new Color(0.06f, 0.06f, 0.05f));
    }

    /// <summary>The plate on face <paramref name="k"/> of a hull fronting
    /// <paramref name="front"/>.</summary>
    private int Plate(int k, int front)
    {
        int off = front < 0 ? 1 : (k - front + 6) % 6;
        return Gunnery.ArmourAt(_profile, off == 0 ? Gunnery.Plate.Front : off == 3 ? Gunnery.Plate.Rear : Gunnery.Plate.Side);
    }

    private static void CardNumber(Control c, Font font, Vector2 at, string text, int size, Color ink)
    {
        const float w = 40.0f;
        c.DrawString(font, at + new Vector2(-w * 0.5f, size * 0.36f), text, HorizontalAlignment.Center, w, size, ink);
    }

    /// <summary>
    /// The turn's steps as dots: whole ones left, the way shown lights those
    /// it would spend, the spent ones hollow. The budget is the reach's, the
    /// road's step and the mud's one in it (Steps.BudgetAt); a move made in a
    /// match spends the lot - there is no second.
    /// </summary>
    private void CardSteps(Control c, Font font, Vector2 at)
    {
        c.DrawString(font, at + new Vector2(0.0f, 4.0f), "шаги", HorizontalAlignment.Left, -1, 13, CardDim);
        bool live = CourseShown && _reach is not null;
        int budget = live ? _reach!.Budget : _profile.Steps;
        int left = Match && _drove ? 0 : budget;
        int spend = live ? Math.Min(_wayCost, left) : 0;
        const float r = 5.0f, gap = 14.0f;
        for (int i = 0; i < budget; i++)
        {
            Vector2 p = at + new Vector2(64.0f + i * gap, 0.0f);
            if (i >= left)
                c.DrawArc(p, r - 0.75f, 0.0f, Mathf.Tau, 16, CardDim, 1.5f, true);
            else
                c.DrawCircle(p, r, i >= left - spend ? CardSpend : CardPale);
        }
    }
}
