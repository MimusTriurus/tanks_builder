using System;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// What a shot would do, asked without doing it (docs/ai-plan.md, И2 -
/// «быстрый вопрос выстрела»): the round's flight of <see cref="Fire"/> -
/// walls, hulls by their plate, the rising ground, woods and smoke, the
/// destroyer's pass, the ricochet off a side, the chain of a hull going up -
/// read off the state and written into marks, one per unit, nothing in the
/// match touched and nothing allocated. The shooter may be asked to stand on
/// another cell than its own (where its move would take it) and to face the
/// axis. <c>--battle-run MAP --probe-check</c> holds it to <see cref="Fire"/>
/// on a copy.
/// </summary>
public static partial class Battle
{
    /// <summary>A unit's mark after a probe: nothing, set alight, out of the
    /// fight (knocked or drowned), destroyed.</summary>
    public const int ProbeNone = 0, ProbeAlight = 1, ProbeOut = 2, ProbeGone = 3;

    /// <summary>
    /// The round <paramref name="shooter"/> would fire from
    /// <paramref name="from"/> along <paramref name="heading"/> at
    /// <paramref name="aim"/>: each unit's mark in <paramref name="marks"/>,
    /// by its index in <see cref="BattleState.Units"/> (cleared first). The
    /// gates of the order - the distance, a casemate's hull - are the
    /// caller's. <paramref name="full"/> off: no ricochet and no chain - a
    /// knocked-out hull hit goes up alone (the fighter's sight, ai-plan.md).
    /// </summary>
    public static void Probe(BattleState s, BattleUnit shooter, Vector2I from, int heading, Vector2I aim,
                             int[] marks, bool full = true)
    {
        Array.Clear(marks, 0, Math.Min(marks.Length, s.Units.Count));
        var p = new Prober(s, shooter, from, marks, full);
        if (shooter.Class.Lobs)
        {
            p.Lob(aim);
            return;
        }
        p.Fly(from, heading, shooter.Class.Might, p.LevelOf(shooter, from),
              passes: shooter.Class.Might >= 4 && !shooter.Class.Turreted ? 1 : 0, glance: true, aim: aim,
              thermal: (shooter.Kit & Kit.Thermal) != 0, woodPass: (shooter.Kit & Kit.ThroughWood) != 0);
    }

    /// <summary>The fight's worth of the probe's marks, as the AI weighs a
    /// unit (<paramref name="worth"/>: whole, alight, out): what
    /// <paramref name="gainer"/>'s side gains - the enemy's loss less its own.</summary>
    public static double ProbeGain(BattleState s, int[] marks, Camp gainer, Func<BattleUnit, double> value,
                                   double alight, double outShare)
    {
        double sum = 0.0;
        for (int i = 0; i < s.Units.Count; i++)
        {
            int m = marks[i];
            if (m == ProbeNone)
                continue;
            BattleUnit u = s.Units[i];
            double before = u.Fate == UnitFate.Alive ? (u.Burning ? alight : 1.0) : u.Out ? outShare : 0.0;
            double after = m switch
            {
                ProbeAlight => u.Fate == UnitFate.Alive ? alight : before,
                ProbeOut => Math.Min(before, outShare),
                _ => 0.0,
            };
            sum += (u.Side == gainer ? -1.0 : 1.0) * value(u) * (before - after);
        }
        return sum;
    }

    /// <summary>The flight on a read-only state: <see cref="Fly"/>,
    /// <see cref="Strike"/>, <see cref="Explode"/> and <see cref="Lob"/> with
    /// their writes turned into marks.</summary>
    private readonly struct Prober
    {
        private readonly BattleState _s;
        private readonly BattleUnit _shooter;
        private readonly Vector2I _from;
        private readonly int[] _marks;
        private readonly bool _full;

        public Prober(BattleState s, BattleUnit shooter, Vector2I from, int[] marks, bool full)
        {
            _s = s;
            _shooter = shooter;
            _from = from;
            _marks = marks;
            _full = full;
        }

        private Vector2I CellOf(BattleUnit u) => ReferenceEquals(u, _shooter) ? _from : u.Cell;

        /// <summary>The standing unit on <paramref name="c"/> as the probe has
        /// it - the shooter moved, the destroyed gone - and its index.</summary>
        private BattleUnit? At(Vector2I c, out int index)
        {
            for (int i = 0; i < _s.Units.Count; i++)
            {
                BattleUnit u = _s.Units[i];
                if (u.Standing && _marks[i] != ProbeGone && CellOf(u) == c)
                {
                    index = i;
                    return u;
                }
            }
            index = -1;
            return null;
        }

        private bool Alive(BattleUnit u, int i) => u.Fate == UnitFate.Alive && _marks[i] is ProbeNone or ProbeAlight;
        private bool Out(BattleUnit u, int i) => u.Out || _marks[i] == ProbeOut;
        private bool Burning(BattleUnit u, int i) => u.Burning || _marks[i] == ProbeAlight;

        /// <summary><see cref="Level(BattleState, BattleUnit)"/> on the cell the probe has the unit on.</summary>
        public int LevelOf(BattleUnit u, Vector2I cell) =>
            _s.Board.LevelAt(cell) + (u.Fate == UnitFate.Alive && _s.Board.IsDeep(cell) && _s.Board.RampHeading(cell) < 0 ? 1 : 0);

        private int LevelOf(BattleUnit u, int i) =>
            _s.Board.LevelAt(CellOf(u)) + (Alive(u, i) && _s.Board.IsDeep(CellOf(u)) && _s.Board.RampHeading(CellOf(u)) < 0 ? 1 : 0);

        public void Fly(Vector2I from, int heading, int might, int fromLevel, int passes, bool glance, Vector2I? aim,
                        bool thermal = false, bool woodPass = false)
        {
            int ontoLevel = fromLevel;
            if (aim is Vector2I a && _s.Board.InBounds(a))
                ontoLevel = At(a, out int ai) is { } t ? LevelOf(t, ai) : _s.Board.LevelAt(a);
            Vector2I at = from;
            for (int step = 0; step < 64; step++)
            {
                Vector2I c = HexField.Step(at, heading);
                if (c == at || !_s.Board.InBounds(c))
                    return;
                if (_s.Board.Walled(at, heading))
                {
                    if (passes <= 0)
                        return;
                    passes--;
                    might--;
                }
                at = c;
                int floor = Math.Min(fromLevel, ontoLevel);
                if (At(c, out int hi) is { } hit && LevelOf(hit, hi) >= floor)
                {
                    bool through = passes > 0;
                    Strike(hit, hi, heading, might, fromLevel, glance && !through);
                    if (!through)
                        return;
                    passes--;
                    might--;
                    continue;
                }
                if (Gunnery.Overtops(_s.Board, c, fromLevel, ontoLevel))
                    return;
                if (_s.Board.LevelAt(c) >= floor && BoardRules.Screened(_s.Board, c, thermal))
                {
                    if (woodPass && passes > 0 && _s.Board.CoverAt(c) == Cover.Forest)
                    {
                        passes--;
                        might--;
                        continue;
                    }
                    return;
                }
            }
        }

        private void Strike(BattleUnit hit, int i, int heading, int might, int fromLevel, bool glance)
        {
            if (Out(hit, i))
            {
                Explode(hit, i);
                return;
            }
            Gunnery.Plate plate = PlateOf(heading, hit.Heading, fromLevel > LevelOf(hit, i));
            if (Gunnery.Penetration(might, hit.Class, plate) > 0)
            {
                _marks[i] = ProbeOut;
                return;
            }
            if (!_full || !glance || !Gunnery.Ricochets(plate))
                return;
            int onward = (((2 * hit.Heading - heading) % 360) + 360) % 360;
            Fly(CellOf(hit), onward, might, LevelOf(hit, i), passes: 0, glance: false, aim: null);
        }

        private void Explode(BattleUnit u, int i)
        {
            if (_marks[i] == ProbeGone)
                return;
            _marks[i] = ProbeGone;
            if (!_full)
                return;
            Vector2I cell = CellOf(u);
            int level = _s.Board.LevelAt(cell);
            foreach (int h in HexField.EdgeHeadings)
            {
                Vector2I n = HexField.Step(cell, h);
                if (!_s.Board.InBounds(n) || _s.Board.LevelAt(n) != level)
                    continue;
                if (At(n, out int ni) is not { } near)
                    continue;
                if (Out(near, ni))
                    Explode(near, ni);
                else if (Alive(near, ni)
                         && ((near.Kit & Kit.SpallLiner) == 0
                             // the blast lights the wood it stands in, and that lights it
                             || (_s.Board.CoverAt(n) == Cover.Forest && _s.Board.CoverStateAt(n) == CoverState.Intact)))
                    Ignite(near, ni);
            }
        }

        private void Ignite(BattleUnit u, int i)
        {
            if (!Alive(u, i) || Burning(u, i) || _s.Board.IsWater(CellOf(u)))
                return;
            _marks[i] = ProbeAlight;
        }

        public void Lob(Vector2I aim)
        {
            if (At(aim, out int i) is not { } hit)
                return;
            if (Out(hit, i))
                Explode(hit, i);
            else
                _marks[i] = ProbeOut;
        }
    }
}
