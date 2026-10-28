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
///
/// <b>Afloat, they bob</b> (2026-10-28): the target and the standing units
/// each ride a <see cref="Buoy"/> of their own (<see cref="FloatOf"/>), as
/// the rig rides its - the swell, and the target's plunge when a ram throws
/// it in. Only the rig bobbed: a hull rammed into the pond lay dead still on
/// it until it was taken. Kept per model, so the bob goes with a hull from
/// target to standing and back.
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

    /// <summary>A hull's bob off the rig, and how much of it its rig's height
    /// carries now, world Y - taken off before the height is judged again.</summary>
    private sealed class Float
    {
        public readonly Buoy Bob = new();
        public float Applied;
    }

    private readonly Dictionary<TankModel, Float> _floats = new();

    private Float FloatOf(TankModel model)
    {
        if (!_floats.TryGetValue(model, out Float? f))
        {
            // each hull at its own place in the swell, as Fleet.Crew sets it
            f = new Float();
            f.Bob.Phase = (model.GetInstanceId() % 628) * 0.01;
            _floats[model] = f;
        }
        return f;
    }

    /// <summary>The bob of <paramref name="model"/> this frame on top of
    /// <paramref name="rest"/>, world Y.</summary>
    private float Bobbed(TankModel model, float rest, float dt)
    {
        Float f = FloatOf(model);
        f.Bob.Update(dt);
        f.Applied = (float)(f.Bob.Offset + f.Bob.Idle) / RiseFactor;        return rest + f.Applied;
    }

    /// <summary>Every frame: the standing units afloat ride their bob. A
    /// knocked-out one is <see cref="Founder"/>'s, going down.</summary>
    private void FloatTick(float dt)
    {
        for (int i = 0; i < _parked.Count; i++)
        {
            Parked p = _parked[i];
            if (p.Fate != Fate.Alive || AfloatY(p.Cell, p.Model, ClassFor(p.Tag)) is not float rest)
                continue;
            Vector3 at = p.Rig.Position;
            p.Rig.Position = new Vector3(at.X, Bobbed(p.Model, rest, dt), at.Z);
        }
    }

    /// <summary>Where a hull of <paramref name="model"/> floats on
    /// <paramref name="cell"/>, world Y - or null when it does not: dry land, a
    /// ramp's face, no wading gear.</summary>
    private float? AfloatY(Vector2I cell, TankModel model, MovementProfile profile) =>
        _amphibious ? SwimY(cell, model, profile) : null;

    /// <summary>Where a hull of <paramref name="model"/> would float on
    /// <paramref name="cell"/> with the gear, world Y, the gear asked or not -
    /// or null on dry land or a ramp's face. The ram's preview shows the sink
    /// from it (<c>Tank3DBench.RamPlan</c>).</summary>
    private float? SwimY(Vector2I cell, TankModel model, MovementProfile profile)
    {
        if (_field is null || !_field.InBounds(cell) || !_field.IsDeep(cell)
            || _field.RampHeading(cell) >= 0)
            return null;
        if (!_decks.TryGetValue(model, out float deck))
            _decks[model] = deck = MeasureDeck(model);
        float swim = _field.WaterTop(cell) - deck * (float)profile.Draught;
        return Mathf.Max(BedPx(cell), swim) / RiseFactor;
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
        float bed = BedPx(cell) / RiseFactor;
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
        Log.Print($"tank3d: {_modelTag} climbs out of the pond {MapAt(from)} -> {MapAt(to)}");
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
