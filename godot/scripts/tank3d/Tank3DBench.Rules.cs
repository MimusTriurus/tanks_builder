using Godot;

namespace TankSpriteTest;

/// <summary>
/// The rules the bench lacked against the GDD, brought level with the rules
/// core (docs/battle-core-plan.md, Д4; <see cref="Battle"/> says each first):
/// <list type="bullet">
/// <item><b>What a hex does to a unit coming on to it</b> (П-14, П-15): a
/// burning hull puts a wood it drives into alight; a wood alight sets the
/// hull driving through it alight; water puts its fire out at once. Read when
/// a hull's hex changes - the own tank's and the target's, the one a ram
/// pushes (<see cref="HexEffectsTick"/>).</item>
/// <item><b>The field's tick</b> (П-16): at the round's end the fire is told
/// (<see cref="Wildfire.Round"/>), and whoever stands in a wood it reached
/// catches.</item>
/// <item><b>The heavy's wood</b> (П-17): the wood under a heavy that ends its
/// turn on it is ground on the board (<c>Cleared</c>), not only in the trees
/// that fell.</item>
/// <item><b>The stalemate</b> (П-21, Д6): rounds without a shot at a tank
/// counted (<see cref="TankShot"/>), and the match drawn at
/// <see cref="MatchRules.QuietRoundsDraw"/> (<see cref="StalemateTick"/>).</item>
/// </list>
/// </summary>
public sealed partial class Tank3DBench
{
    private Vector2I? _hexWas, _otherHexWas;
    /// <summary>Whose hexes those are: a hand-over moves the rig to another
    /// unit's hex, and that is not a unit coming on to it.</summary>
    private int _hexUnit = -1, _otherHexUnit = -1;

    /// <summary>Every frame of a match: the own tank's and the target's hex,
    /// and what the new one does when it changes.</summary>
    private void HexEffectsTick()
    {
        if (!Match || _field is null)
            return;
        Vector2I here = CellHere;
        if (_hexUnit != _unitId)
        {
            _hexUnit = _unitId;
            _hexWas = here;
        }
        if (_hexWas != here)
        {
            if (_hexWas is not null && _fate == Fate.Alive)
                HexEffects(_unitId, _side, _modelTag, here);
            _hexWas = here;
        }
        if (_other is { Unit: true } o)
        {
            Vector2I there = OtherCell(o);
            if (_otherHexUnit != o.Id)
            {
                _otherHexUnit = o.Id;
                _otherHexWas = there;
            }
            if (_otherHexWas != there)
            {
                if (_otherHexWas is not null && o.Fate == Fate.Alive)
                    HexEffects(o.Id, o.Side, o.Tag, there);
                _otherHexWas = there;
            }
        }
        else
            _otherHexWas = null;
    }

    /// <summary>What <paramref name="cell"/> does to unit <paramref name="id"/>
    /// come on to it (field.md, «огонь\пожар»; states.md, «Горит»).</summary>
    private void HexEffects(int id, Side side, string tag, Vector2I cell)
    {
        if (_field!.IsWater(cell))
        {
            if (Burning(id))
            {
                Quench(id, steam: true);
                Log.Print($"tank3d: match: {SideName(side)} {tag}#{id} into the water on {MapAt(cell)} - its fire is out");
            }
            return;
        }
        if (_field.CoverAt(cell) != Cover.Forest)
            return;
        if (_field.CoverStateAt(cell) == CoverState.Burning || (_wildfire?.LitAt(cell) ?? false))
            Ignite(id, side, tag, cell);
        else if (Burning(id) && _field.CoverStateAt(cell) == CoverState.Intact && _wildfire is not null
                 && _wildfire.Light(cell))
            Log.Print($"tank3d: match: burning {SideName(side)} {tag}#{id} sets the wood on {MapAt(cell)} alight");
    }

    /// <summary>The round's end (match.md, «Ход и раунд»): the fire told, and
    /// whoever stands in a wood it reached alight.</summary>
    private void FieldTick()
    {
        if (!Match || _wildfire is null || !_wildfire.Ruled)
            return;
        foreach (Vector2I cell in _wildfire.Round())
        {
            Log.Print($"tank3d: match: the fire spreads to {MapAt(cell)}");
            if (UnitOn(cell) is { Fate: Fate.Alive } u)
                Ignite(u.Id, u.Side, u.Tag, cell);
        }
    }

    /// <summary>П. 6 for the heavy (classes.md, «Бульдозер HT»): the wood it
    /// ends its turn on, living or burnt, is ground; a burning one is not.</summary>
    private void HeavyClears()
    {
        if (!Match || _field is null || _fate != Fate.Alive || !_profile.Bulldozes || _side != _mt.Turn)
            return;
        Vector2I here = CellHere;
        if (_field.CoverAt(here) == Cover.Forest
            && _field.CoverStateAt(here) is CoverState.Intact or CoverState.Burnt
            && _field.SetCoverState(here, CoverState.Cleared))
            Log.Print($"tank3d: match: the heavy ends its turn on {MapAt(here)} - the wood is ground");
    }

    /// <summary>A round on a tank - the shot's, a ricochet's, a pass's, the
    /// mortar's bomb - puts the stalemate's clock back (match.md, «Пат —
    /// ничья»). A mine, a fire, a blast are not shots, and do not.</summary>
    private void TankShot()
    {
        if (Match)
            _mt.LastStrike = _mt.Round;
    }

    /// <summary>Every frame of a match: <see cref="MatchRules.QuietRoundsDraw"/>
    /// whole rounds without a shot at a tank end it drawn (П-21). Read once the
    /// hand-over has settled - the turn's burning out gone up - so the last tank
    /// burnt out on the same tick is a win, as the core has it
    /// (<c>Battle.Stalemate</c>).</summary>
    private void StalemateTick(bool busy)
    {
        if (MatchOver || busy || _passIn is not null || _mt.QuietRounds < MatchRules.QuietRoundsDraw)
            return;
        _mt.Drawn = true;
        _commands.Clear();
        Banner($"Ничья по пату · раундов без выстрела по танку: {_mt.QuietRounds}", Colors.White, stay: true);
        Log.Print($"tank3d: match: {_mt.QuietRounds} rounds without a shot at a tank - drawn in round {_mt.Round}");
    }
}
