using Godot;

namespace TankSpriteTest;

/// <summary>
/// The smoke round on the bench (GDD upgrades.md «Дымовой снаряд», field.md
/// «дым»). The rule is the core's (<see cref="Battle.Fire"/> with
/// <c>smoke</c>); the bench lays the gun or the mortar on the hex as for any
/// shot, and where the round comes down the smoke goes up - the grenade's
/// smoke (Tank3DBench.Smoke), two rounds, gone when a unit comes on to it.
/// <list type="bullet">
/// <item><b>Chosen</b> for the next shot of a unit with the rounds: the button
/// «Дым. сн.» by the attack's plate, then the right button on a hex; or
/// <c>smokeshot:Q,R</c>. It is the attack: the shot of the turn is spent.</item>
/// <item><b>Where</b>: an empty hex - no tank, wood, walls, minefield or
/// smoke. A gun's on its axis short of the first thing on it, where the line of
/// fire reaches (no destroyer's pass, a wall stops it); the mortar's 3 to 5 out
/// (3 to 6 with the bracket), over everything.</item>
/// <item>The round touches nothing on its way and on the hex: no hull struck,
/// no wall down, no crater. Fired with no hex laid on (by hand, Space) it is a
/// plain round, and the choice spent.</item>
/// </list>
/// </summary>
public sealed partial class Tank3DBench
{
    /// <summary>The panel's «Дым. сн.» on: the next shot is the smoke round.</summary>
    private bool _smokeShotMode;
    private Button? _actSmokeShot;

    private bool RigSmokeShells => (KitOf(_unitId) & Kit.SmokeShell) != 0;

    /// <summary>«Дым. сн.» on or off.</summary>
    private void SmokeShotToggle()
    {
        if (!_smokeShotMode && (MatchShotSpent || !RigSmokeShells))
        {
            _orderNote = !RigSmokeShells ? L.T("smokeshell.none", _modelTag) : L.T("incendiary.shot_spent");
            return;
        }
        _smokeShotMode = !_smokeShotMode;
        if (_smokeShotMode)
            _ambushMode = _fieryMode = false;
        _orderNote = _smokeShotMode ? L.T("smokeshell.mode_on") : L.T("smokeshell.mode_off");
    }

    /// <summary>Why the smoke round may not go on to <paramref name="cell"/>,
    /// the shot's own gates passed (<see cref="ShotWhy"/>), or empty: the hex
    /// taken, or a gun's lane to it shut - by a hull, a wood, smoke, the rising
    /// ground or a wall, with no pass through any.</summary>
    private string SmokeShotWhy(Vector2I own, Vector2I cell, int axis, int cells)
    {
        if (_field is null)
            return L.T("smoke.no_board");
        if (!RigSmokeShells)
            return L.T("smokeshell.none", _modelTag);
        if (UnitStanding(cell) is not null || (_other is { Unit: true } o && o.Fate != Fate.Destroyed && OtherCell(o) == cell))
            return L.T("smoke.not_on_tank");
        if (_field.CoverAt(cell) != Cover.None)
            return L.T("smoke.hex_taken");
        if (_field.Smoked(cell))
            return L.T("smoke.already");
        if (_profile.Lobs)
            return "";
        if (WallOnAxis(own, axis, cells) is not null)
            return L.T("smokeshell.wall");
        Shot shot = LaneSolve(own, cell, 0);
        return !shot.Clear && shot.BlockedAt is Vector2I at ? LaneWhy(shot, at) : "";
    }

    /// <summary>The round leaves: the hex it lays smoke on if the mode was on
    /// and a hex was laid on, and the mode spent with it.</summary>
    private Vector2I? SmokeShotLaunch()
    {
        bool on = _smokeShotMode && RigSmokeShells && _shadowAim is not null;
        _smokeShotMode = false;
        if (!on)
            return null;
        Log.Print($"tank3d: match: {SideName(_side)} {_modelTag}#{_unitId} fires a smoke round at {MapAt(_shadowAim!.Value)}");
        return _shadowAim;
    }

    /// <summary>The smoke round down: smoke on its hex for two rounds.</summary>
    private void SmokeShotLands(Vector2I cell)
    {
        if (_field is null || _field.Smoked(cell))
            return;
        _field.SetSmoke(cell, true);
        _smokeLeft[cell] = SmokeRounds;
        SmokeCloudAt(cell);
        _matchNote = L.T("smokeshell.laid");
        Log.Print($"tank3d: match: the smoke round lays smoke on {MapAt(cell)} for {SmokeRounds} rounds");
    }
}
