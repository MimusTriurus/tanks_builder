using System;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The chosen unit's card, bottom left (docs/hud3d-plan.md, «Карточка юнита»,
/// 2026-10-29; the panel since 2026-10-30): what the unit is and how much
/// turn it has left, read without a hover. It is the panel the pointer opens
/// beside any other unit (<see cref="UnitPanel"/>, <c>Tank3DBench.Peek</c>) -
/// class and name, might, steps, mass and the three plates as marks - with
/// the turn in it: the steps row lights what the way under the pointer
/// spends and empties with a move made, and the state's sign (the one over
/// its hull, <see cref="Sign"/>) stands in the title row.
/// </summary>
public sealed partial class Tank3DBench
{
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
    /// <summary>The names off the lineup, looked up when the rig changes
    /// hands and not every frame.</summary>
    private string _cardModel = "", _cardTitle = "", _cardName = "";

    /// <summary>The steps the way shown costs, its turn at the end with them
    /// (<c>CourseTick</c>); nought with no way.</summary>
    private int _wayCost;

    private void BuildCard(CanvasLayer layer)
    {
        if (_noUi)
            return;
        _card = new Control
        {
            Name = "UnitCard",
            MouseFilter = Control.MouseFilterEnum.Ignore,
            AnchorTop = 1.0f, AnchorBottom = 1.0f,
            OffsetLeft = 12.0f, OffsetRight = 12.0f + PeekW,
            OffsetTop = -12.0f - PeekH, OffsetBottom = -12.0f,
        };
        _card.Draw += DrawCard;
        layer.AddChild(_card);
    }

    /// <summary>Every frame, after <c>AimTick</c>: shown while a unit is
    /// chosen, drawn again as the way moves.</summary>
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
        if (_card is not { } c || _peekBox is null || _field is null)
            return;
        if (!ReferenceEquals(_cardModel, _modelTag))
        {
            _cardModel = _modelTag;
            (_cardTitle, _cardName) = PanelNames(_profile, _modelTag);
        }
        Badge badge = _fate == Fate.Knocked || Match ? BadgeOf(_side, _modelTag, _fate, CellHere) : Badge.None;
        UnitPanel(c, ThemeDB.FallbackFont, new Rect2(Vector2.Zero, c.Size), _profile, _cardTitle, _cardName,
                  _side, badge, turn: true);
    }
}
