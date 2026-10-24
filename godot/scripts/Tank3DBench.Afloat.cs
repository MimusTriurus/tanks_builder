using System.Collections.Generic;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// Deep water for the hulls the rig is not (2026-10-24): the rammed target and
/// the standing units float with the wading gear as the rig does
/// (<see cref="RideOn"/>), and go under once knocked out - GDD states.md
/// «Плывёт», «Утоплен»: "любой танк, подбитый на глубокой воде". Thrown into
/// the pond by a ram, the target used to drop to the bed with its gear on.
///
/// And the rig's way out (<see cref="Climb"/>): the hull takes the bank's
/// height over a moment, nose up, the water running off it - its middle
/// crossing on to the bank had the ride jump a level in one frame.
/// </summary>
public sealed partial class Tank3DBench
{
    /// <summary>How fast the hull climbs out of the pond, lift px a second.</summary>
    private const float ClimbRate = 60.0f;

    /// <summary>The nose-up of the climb, into the kick spring.</summary>
    private const float ClimbTip = 1.6f;

    /// <summary>Each model's deck, measured once - <see cref="MeasureDeck"/>
    /// reads the mesh.</summary>
    private readonly Dictionary<TankModel, float> _decks = new();

    /// <summary>The rig is on its way up out of the pond.</summary>
    private bool _climbing;

    /// <summary>Where a hull of <paramref name="model"/> floats on
    /// <paramref name="cell"/>, world Y - or null when it does not: dry land, a
    /// ramp's face, no wading gear.</summary>
    private float? AfloatY(Vector2I cell, TankModel model, MovementProfile profile)
    {
        if (_field is null || !_amphibious || !_field.InBounds(cell) || !_field.IsDeep(cell)
            || _field.RampHeading(cell) >= 0)
            return null;
        if (!_decks.TryGetValue(model, out float deck))
            _decks[model] = deck = MeasureDeck(model);
        float swim = _field.WaterTop(cell) - deck * (float)profile.Draught;
        return Mathf.Max(_field.BedAt(cell), swim) / RiseFactor;
    }

    /// <summary>A knocked-out hull standing in the pond goes down to the bed.</summary>
    private void Founder(TankModel model, Vector2I cell)
    {
        if (_field is null || !_field.IsDeep(cell) || _field.RampHeading(cell) >= 0
            || model.GetParent() is not Node3D rig)
            return;
        // the target's own frame lets it down (OtherTick); a standing one is
        // taken down here
        if (_other is { } o && ReferenceEquals(o.Model, model))
            return;
        float bed = _field.BedAt(cell) / RiseFactor;
        CreateTween().TweenProperty(rig, "position:y", bed, (float)TankTick.SinkSeconds)
                     .SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
    }

    /// <summary>The middle crossed from the pond on to higher ground: the
    /// climb begins.</summary>
    private void ClimbOut(Vector2I from, Vector2I to)
    {
        if (_field is null || !_field.IsDeep(from) || _field.RampHeading(from) >= 0
            || _field.LevelAt(to) <= _field.LevelAt(from))
            return;
        _climbing = true;
        float sign = Mathf.Sign(_speed == 0.0f ? 1.0f : _speed);
        _kickPitch.Kick(-ClimbTip * sign);
        // the water off the hull, at the edge it leaves by
        float top = _field.WaterTop(from);
        float h = Mathf.DegToRad(_heading);
        var ahead = new Vector3(Mathf.Sin(h), 0.0f, Mathf.Cos(h));
        Vector3 way = _speed < 0.0f ? -ahead : ahead;
        float halfLen = _model.HullLength * _model.PixelsPerUnit * 0.5f;
        Vector3 mid = CellWorld(from);
        _splash?.Fire(new Vector3(_rig.Position.X, top / RiseFactor, _rig.Position.Z), -way,
                      halfLen, _model.HullWidth * _model.PixelsPerUnit * 0.5f, 0.35f,
                      new Vector3(mid.X, top / RiseFactor, mid.Z), HexWidth * 0.5f);
        Rings(-way, halfLen, 0.35f);
        GD.Print($"tank3d: {_modelTag} climbs out of the pond {from} -> {to}");
    }

    /// <summary>The ride while climbing: up to <paramref name="target"/> at
    /// <see cref="ClimbRate"/>, then a settle on the tracks.</summary>
    private float Climb(float target, float dt)
    {
        // at the crawl while it climbs: the hull does not run on into the bank
        _speed = Mathf.Sign(_speed) * Mathf.Min(Mathf.Abs(_speed), (float)_profile.CornerSpeed);
        float next = Mathf.MoveToward(_ride, target, ClimbRate / RiseFactor * dt);
        if (Mathf.IsEqualApprox(next, target) || next >= target)
        {
            _climbing = false;
            _kickPitch.Kick(0.5f * ClimbTip * Mathf.Sign(_speed == 0.0f ? 1.0f : _speed));
            _heave.Kick(-0.002f * ClimbRate);
            return target;
        }
        return next;
    }
}
