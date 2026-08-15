using System;
using System.Collections.Generic;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// How stirred up each flooded cell is, from calm to breaking.
///
/// <b>The sheet's own ladder, read as a state rather than as an animation.</b>
/// The art escalates monotonically over twelve frames - a flat calm, a chop, a
/// breaking sea - and the twelve <i>do not</i> close as one loop: their wrap is
/// 41.5 against a worst normal step of 33.2. Each quartet does, at 1.14, 1.11
/// and 1.21 of its own worst inside step. So the loop is four frames long and
/// the escalation is a third axis: which four you are looping over.
///
/// That axis is what this holds. Calm is the pond nobody has touched; the chop
/// is a cell a tank is driving through; the break is a shoreline that has just
/// been crossed.
///
/// <b>Per cell, and that is not the thing the synchronised phase forbids.</b>
/// The pond runs on one phase because it is one body of water and desynchronised
/// cells disagree across every edge they share. This is the other half of the
/// same statement: the swell is shared, and an <i>event</i> is not the swell. A
/// cell that has just been driven into is genuinely different from its
/// neighbours, and the edge between them is the right place to say so.
///
/// <b>One number per cell, and the two sources fold into it by max.</b> Churn
/// and splash are not added: a splash settles into chop and then into calm, so
/// the break band already passes through the chop band on its way down, and
/// adding them would push a splashing cell that is also being driven through
/// past the top of the ladder for no picture.
/// </summary>
public sealed class Swell
{
    public required HexField Field;

    /// <summary>Whether the water answers the tanks at all. Off leaves every
    /// cell calm, which is the pond as it was before this - see --still-water.
    /// </summary>
    public bool Enabled = true;

    /// <summary>
    /// How long a crossing keeps the water broken, in seconds.
    ///
    /// A splash is an event, so it is a clock that runs out, and the length is
    /// the one number in it that is a judgement: long enough to be seen at a
    /// glance, short enough that a tank crossing the pond does not leave the
    /// whole thing breaking. At 0.9s a tank at the ford's 108px/s is most of a
    /// cell away before its own splash has settled.
    /// </summary>
    public float SplashFor = 0.9f;

    /// <summary>How long the chop takes to come up under a tank, and to go back
    /// down after it. <b>Up fast and down slow</b>, because water is stirred at
    /// once and settles at its leisure - the same asymmetry the belt marks fade
    /// on. Equal times read as the cell being a switch under the tank.</summary>
    public float ChurnRise = 0.20f;
    public float ChurnFall = 1.40f;

    /// <summary>What the ladder's rungs are worth, in bands. Named rather than
    /// written into the arithmetic: the sheet has three bands, and a chop that
    /// reached the top of the ladder would leave a splash nothing to say.
    /// </summary>
    public const float ChurnBand = 1.0f;
    public const float SplashBand = 2.0f;

    private float[] _churn = Array.Empty<float>();
    private float[] _splash = Array.Empty<float>();
    private bool[] _stirred = Array.Empty<bool>();
    private readonly Dictionary<int, Vector2I> _was = new();

    /// <summary>The state of every flooded cell, in
    /// <see cref="HexField.WaterCells"/>' order, 0 for calm through
    /// <see cref="SplashBand"/> for breaking.</summary>
    public float[] State { get; private set; } = Array.Empty<float>();

    /// <summary>What a cell is doing, 0 for dry ground as well as for calm water
    /// - a dry cell has no surface to be stirred, so both answers are the same
    /// picture.</summary>
    public float StateAt(Vector2I cell)
    {
        int i = Field.WaterIndex(cell);
        return i >= 0 && i < State.Length ? State[i] : 0.0f;
    }

    /// <summary>The busiest cell on the board, for the panel to report. The
    /// bench measures this and nobody sets it, which is what a readout is.
    /// </summary>
    public float Peak
    {
        get
        {
            float top = 0.0f;
            foreach (float v in State)
                top = Mathf.Max(top, v);
            return top;
        }
    }

    /// <summary>
    /// Say where one tank is standing this frame, and whether it is driving.
    ///
    /// <b>A cell and a number, not a Vehicle</b>, and that is not squeamishness:
    /// what stirs water is a thing standing somewhere and moving, so taking any
    /// more than that would be this class knowing about tanks in order to ignore
    /// most of what it knew. It also means the whole of the interesting part -
    /// when a crossing counts - can be asserted without building a tank.
    ///
    /// <b>The crossing is read off the cell under the contact point, not off the
    /// wading flag.</b> Wading is one flag for a whole pond: it flips once going
    /// in and once coming out, which is exactly what keeps a rut out of the water,
    /// and a splash happens at every shoreline crossed - including the internal
    /// ones a five-cell pond is made of. The cell changes at each of them.
    /// </summary>
    public void Note(int who, Vector2I cell, bool moving)
    {
        Size();
        bool moved = _was.TryGetValue(who, out Vector2I before) && before != cell;
        _was[who] = cell;
        if (!Enabled)
            return;

        int here = Field.WaterIndex(cell);
        if (here >= 0 && moving)
            _stirred[here] = true;
        if (!moved)
            return;
        // Both ends of the step, because leaving a cell throws as much water as
        // entering one, and a shoreline is crossed by exactly one of them being
        // dry. A step from water to water is an internal edge and splashes twice
        // over, which is what a tank ploughing across a pond does.
        foreach (Vector2I at in new[] { before, cell })
        {
            int i = Field.WaterIndex(at);
            if (i >= 0)
                _splash[i] = 1.0f;
        }
    }

    /// <summary>Move every cell on by one frame. Called after every tank has been
    /// noted, because the chop is "is anybody in this cell now" and that is not
    /// known until they all have.</summary>
    public void Tick(double delta)
    {
        Size();
        float step = (float)delta;
        for (int i = 0; i < State.Length; i++)
        {
            float want = _stirred[i] && Enabled ? 1.0f : 0.0f;
            float rate = want > _churn[i] ? ChurnRise : ChurnFall;
            _churn[i] = Mathf.MoveToward(_churn[i], want,
                                         step / Mathf.Max(rate, 1e-4f));
            _splash[i] = Mathf.Max(
                0.0f, _splash[i] - step / Mathf.Max(SplashFor, 1e-4f));
            State[i] = Mathf.Max(_churn[i] * ChurnBand, _splash[i] * SplashBand);
            _stirred[i] = false;
        }
    }

    private void Size()
    {
        int n = Field.WaterCells.Count;
        if (_churn.Length == n)
            return;
        _churn = new float[n];
        _splash = new float[n];
        _stirred = new bool[n];
        State = new float[n];
        _was.Clear();
    }

    /// <summary>Below this a tank is parked rather than driving, in px/s. The
    /// ford's own ceiling is 108 for the medium, so this is well under a crawl
    /// and only excludes a tank that has actually stopped.</summary>
    public const float StirAbove = 4.0f;

    /// <summary>Put every cell back to calm and forget where the tanks were.
    /// Called when the board is reset or the water is taken off it: a state left
    /// standing would come back as a pond that was breaking before anybody
    /// drove into it.</summary>
    public void Settle()
    {
        Array.Clear(_churn);
        Array.Clear(_splash);
        Array.Clear(State);
        _was.Clear();
    }
}
