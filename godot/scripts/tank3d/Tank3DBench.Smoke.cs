using System.Collections.Generic;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The smoke grenade on the bench (docs/battle-core-plan.md, Д8; GDD
/// upgrades.md «Дымовая шашка», field.md «дым»). The rule is the core's
/// (<see cref="Battle.Smoke"/>); the board carries the smoke as a layer of the
/// match (<see cref="HexField.Smoked"/>), so every lane the bench reads - the
/// shot's gate, the HUD's line of fire, a glance's leg, an ambush's axis -
/// stops at it the way it stops at a wood (<see cref="BoardRules.Screened"/>),
/// and the mortar's bomb goes over it as over a wood.
/// <list type="bullet">
/// <item><b>Laid</b> by the bonus act of a unit with the grenade: the button
/// «Дым», then a hex beside; or <c>smoke:Q,R</c>. Not on a tank, a wood in any
/// state, walls, a minefield or smoke.</item>
/// <item><b>Gone</b> when a unit comes on to the hex or drives through it, or
/// at the second round's end (<see cref="SmokeRound"/>, the field's tick).</item>
/// <item><b>Drawn</b> as one cel cloud on the hex (<see cref="CelCloud"/>),
/// rising as it is laid and thinning out as it goes.</item>
/// </list>
/// </summary>
public sealed partial class Tank3DBench
{
    private const int SmokeRounds = 2;
    /// <summary>Seconds a cloud takes to come up, and to go.</summary>
    private const float SmokeRise = 0.7f, SmokeFade = 0.9f;
    private const int SmokePuffs = 14;

    /// <summary>Each smoke on the board and the rounds it has left.</summary>
    private readonly Dictionary<Vector2I, int> _smokeLeft = new();

    private sealed class SmokeCloud
    {
        public required CelCloud Cloud;
        public Vector2I Cell;
        public float Age;
        /// <summary>Seconds since it began to go; negative while it stands.</summary>
        public float Going = -1.0f;
        public bool Free = true;
    }

    private readonly List<SmokeCloud> _smokeClouds = new();
    /// <summary>Smoke a unit drove into in the match: a move that did is not taken back.</summary>
    private int _smokeDispersed;
    /// <summary>The panel's «Дым» on: a click on a hex beside lays it.</summary>
    private bool _smokeMode;
    private Button? _actSmoke;

    private bool RigSmokes => (KitOf(_unitId) & Kit.Smoke) != 0;

    /// <summary>Why the unit on the rig may not smoke <paramref name="cell"/> now, or empty.</summary>
    private string SmokeWhy(Vector2I cell)
    {
        if (_field is null)
            return "нет доски";
        if (_fate != Fate.Alive)
            return $"{_modelTag} не на ходу";
        if (_side != _mt.Turn)
            return $"ходят {SideName(_mt.Turn)}";
        if (!RigSmokes)
            return $"у {_modelTag} нет дымовой шашки";
        if (MatchBonusSpent)
            return "доп. действие этого хода уже сделано";
        if (HexField.HeadingTo(CellHere, cell) < 0 || !_field.InBounds(cell))
            return "дым — только на соседний гекс";
        if (UnitStanding(cell) is not null || (_other is { Unit: true } o && o.Fate != Fate.Destroyed && OtherCell(o) == cell))
            return "на гекс с танком дым не ложится";
        if (_field.CoverAt(cell) != Cover.None)
            return "гекс занят: лес, стена или мина";
        if (_field.Smoked(cell))
            return "там уже дым";
        return "";
    }

    /// <summary>The grenade thrown (the command's executor): the core told,
    /// the bonus act spent, the board smoked, the cloud up.</summary>
    private void SmokeLay(Vector2I cell)
    {
        ShadowAct($"smoke on {MapAt(cell)}", s => Battle.Smoke(s, cell));
        _mt.Bonus = true;
        _smokeMode = false;
        _field!.SetSmoke(cell, true);
        _smokeLeft[cell] = SmokeRounds;
        SmokeCloudAt(cell);
        _matchNote = "дымовая шашка";
        Log.Print($"tank3d: match: {SideName(_side)} {_modelTag}#{_unitId} lays smoke on {MapAt(cell)} for {SmokeRounds} rounds");
    }

    /// <summary>The smoke on <paramref name="cell"/> gone, saying why.</summary>
    private void SmokeGone(Vector2I cell, string why)
    {
        if (!_smokeLeft.Remove(cell))
            return;
        _field?.SetSmoke(cell, false);
        foreach (SmokeCloud c in _smokeClouds)
            if (!c.Free && c.Cell == cell && c.Going < 0.0f)
                c.Going = 0.0f;
        Log.Print($"tank3d: match: the smoke on {MapAt(cell)} is gone - {why}");
    }

    /// <summary>A hull came on to <paramref name="cell"/>: its smoke is dispersed.</summary>
    private void SmokeEntered(Vector2I cell)
    {
        if (!_smokeLeft.ContainsKey(cell))
            return;
        _smokeDispersed++;
        SmokeGone(cell, "a unit came on to it");
    }

    /// <summary>The round's end: each smoke a round shorter, gone at nought.</summary>
    private void SmokeRound()
    {
        if (_smokeLeft.Count == 0)
            return;
        foreach (Vector2I cell in new List<Vector2I>(_smokeLeft.Keys))
        {
            int left = _smokeLeft[cell] - 1;
            if (left > 0)
                _smokeLeft[cell] = left;
            else
                SmokeGone(cell, "blown away at the round's end");
        }
    }

    private void SmokeCloudAt(Vector2I cell)
    {
        SmokeCloud? slot = null;
        foreach (SmokeCloud c in _smokeClouds)
            if (c.Free)
            {
                slot = c;
                break;
            }
        if (slot is null)
        {
            slot = new SmokeCloud
            {
                Cloud = new CelCloud(this, $"Smoke{_smokeClouds.Count}", new Color(0.86f, 0.87f, 0.84f), 0.09f * HexWidth),
            };
            _smokeClouds.Add(slot);
        }
        slot.Cell = cell;
        slot.Age = 0.0f;
        slot.Going = -1.0f;
        slot.Free = false;
    }

    /// <summary>Every frame: each cloud's puffs round its hex's middle, slowly
    /// rolling, swelling as it comes up and eaten away as it goes. Allocates nothing.</summary>
    private void SmokeTick(float dt)
    {
        if (_smokeClouds.Count == 0 || _field is null)
            return;
        Basis eye = _camera.GlobalBasis;
        float hex = HexWidth;
        foreach (SmokeCloud c in _smokeClouds)
        {
            if (c.Free)
                continue;
            c.Age += dt;
            float erode = 0.1f;
            if (c.Going >= 0.0f)
            {
                c.Going += dt;
                erode = Mathf.Lerp(0.1f, 1.0f, Mathf.Clamp(c.Going / SmokeFade, 0.0f, 1.0f));
                if (c.Going >= SmokeFade)
                {
                    c.Cloud.Hide();
                    c.Free = true;
                    continue;
                }
            }
            float up = Mathf.SmoothStep(0.0f, 1.0f, Mathf.Clamp(c.Age / SmokeRise, 0.0f, 1.0f));
            Vector3 mid = Foot(CellWorld(c.Cell));
            c.Cloud.Clear();
            for (int i = 0; i < SmokePuffs; i++)
            {
                float seed = CelPuff.Hash(i, c.Cell.X * 31 + c.Cell.Y);
                float turn = Mathf.Tau * (i / (float)SmokePuffs) + 0.15f * c.Age * (seed - 0.5f);
                float ring = hex * (0.14f + 0.22f * CelPuff.Hash(i, 3));
                float rise = hex * (0.10f + 0.30f * CelPuff.Hash(i, 5)) * up
                             + hex * 0.015f * Mathf.Sin(0.9f * c.Age + 6.0f * seed);
                var at = mid + new Vector3(Mathf.Cos(turn) * ring, rise, Mathf.Sin(turn) * ring);
                float r = hex * (0.13f + 0.07f * CelPuff.Hash(i, 7)) * (0.35f + 0.65f * up);
                c.Cloud.Add(at, r, 0.46f + 0.12f * seed, seed, erode, Mathf.Clamp(c.Age / 4.0f, 0.0f, 0.9f));
            }
            c.Cloud.Draw(eye);
        }
    }

    // --- the panel ---------------------------------------------------------------------

    /// <summary>«Дым» on or off: on, a click on a hex beside lays it.</summary>
    private void SmokeToggle()
    {
        if (!_smokeMode && MatchBonusSpent)
        {
            _orderNote = "доп. действие этого хода уже сделано";
            return;
        }
        _smokeMode = !_smokeMode;
        if (_smokeMode)
        {
            _ramMode = false;
            _gearMode = Gear.None;
            _ambushMode = false;
        }
        _orderNote = _smokeMode ? "дым: щёлкни по соседнему гексу" : "дым выключен";
    }

    /// <summary>The left button with «Дым» on: smoke on the hex under it.</summary>
    private bool SmokeClick(Vector2 screen)
    {
        if (!_smokeMode)
            return false;
        if (PickAt(screen) is not var (point, onTank, onOwn) || _field is null)
            return true;
        Vector2I cell = onTank && _other is { } t ? OtherCell(t) : onOwn ? CellHere : _field.FlatCellAt(Board(point));
        Execute(new Command(CommandKind.Smoke, cell));
        return true;
    }
}
