using Godot;

namespace TankSpriteTest;

/// <summary>
/// The incendiary round on the bench (docs/battle-core-plan.md, Д8; GDD
/// upgrades.md «Зажигательный снаряд»). The rule is the core's
/// (<see cref="Battle.Fire"/> with <c>incendiary</c>); the bench fires the
/// same round as ever and reads what it meets as the core does.
/// <list type="bullet">
/// <item><b>Chosen</b> for the next shot of a unit with the rounds: the button
/// «Зажиг.» by the attack's plate, then the right button as for any shot; or
/// <c>burn:Q,R</c>. An ambush holds a plain round back.</item>
/// <item><b>What it does</b>: a hull it does not pierce catches fire (the
/// shot's, the ricochet's, the pass's); a living wood the round is spent in
/// catches - the direct round's or the mortar's bomb landing on it, a leg
/// ending in it. Pierced is knocked out as ever; a burnt wood does not catch
/// again.</item>
/// </list>
/// </summary>
public sealed partial class Tank3DBench
{
    /// <summary>The panel's «Зажиг.» on: the next shot is incendiary.</summary>
    private bool _fieryMode;
    /// <summary>The round in the air is incendiary - from its launch until the
    /// turn goes over (one shot a turn; the ambush's are played apart).</summary>
    private bool _fiery;
    private Button? _actFiery;

    private bool RigBurns => (KitOf(_unitId) & Kit.Incendiary) != 0;

    /// <summary>«Зажиг.» on or off.</summary>
    private void FieryToggle()
    {
        if (!_fieryMode && (MatchShotSpent || !RigBurns))
        {
            _orderNote = !RigBurns ? L.T("incendiary.none", _modelTag) : L.T("incendiary.shot_spent");
            return;
        }
        _fieryMode = !_fieryMode;
        if (_fieryMode)
            _ambushMode = _smokeShotMode = _onWayMode = false;
        _orderNote = _fieryMode ? L.T("incendiary.mode_on") : L.T("incendiary.mode_off");
    }

    /// <summary>The round leaves: incendiary if the mode was on, and the mode
    /// spent with it.</summary>
    private bool FieryLaunch()
    {
        _fiery = _fieryMode && RigBurns;
        _fieryMode = false;
        if (_fiery)
            Log.Print($"tank3d: match: {SideName(_side)} {_modelTag}#{_unitId} fires an incendiary round");
        return _fiery;
    }

    /// <summary>The incendiary round held by a hull still fighting: alight.</summary>
    private void FieryHull(int id, Side side, string tag, Vector2I cell, Fate fate)
    {
        if (!_fiery || !Match || fate != Fate.Alive)
            return;
        Log.Print($"tank3d: match: the incendiary round sets {SideName(side)} {tag}#{id} alight");
        Ignite(id, side, tag, cell);
    }

    /// <summary>The incendiary round spent on <paramref name="cell"/>: a living wood there catches.</summary>
    private void FieryWood(Vector2I cell)
    {
        if (!_fiery || !Match || _field is null || _wildfire is null)
            return;
        if (_field.CoverAt(cell) != Cover.Forest || _field.CoverStateAt(cell) != CoverState.Intact)
            return;
        if (_wildfire.Light(cell))
            Log.Print($"tank3d: match: the incendiary round sets the wood on {MapAt(cell)} alight");
    }

    /// <summary>The same where a round came down, world.</summary>
    private void FieryWood(Vector3 at)
    {
        if (_fiery && _field is not null)
            FieryWood(_field.FlatCellAt(Board(at)));
    }
}
