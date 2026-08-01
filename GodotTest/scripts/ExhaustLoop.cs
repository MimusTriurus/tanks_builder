using System;

namespace TankSpriteTest;

/// <summary>
/// The clock behind the engine plume: a phase that goes round and round.
///
/// It is the odd one out among the effect clocks, and the difference is not a
/// detail. A shot has a first frame and a last one, so
/// <see cref="EffectLayer.Hold"/> is a table of held frames that runs out; the
/// exhaust has neither, so there is nothing to hold and nothing to run out. The
/// renderer built its phases as a conveyor that closes on itself - a puff's age
/// is <c>frac(...)</c>, so phase N is phase 0 - and all this has to do is walk
/// round it at a sensible rate.
///
/// Phase is integrated, not computed as rate times elapsed time, for the reason
/// <see cref="EngineTremble"/> spells out: with a rate that changes, the second
/// form moves the whole waveform when the rate moves. Pulling away after ten
/// seconds at a standstill would step the phase by the rate difference times
/// those ten seconds - here, several whole laps - and the plume would jump at
/// exactly the moment the tank started to work for it.
///
/// Rate and density both follow one load ramp, because both come from the same
/// engine turning harder. That an engine under load smokes more is the plainest
/// fact about diesel exhaust there is, and it is the thing that makes the effect
/// say something about the tank rather than just sitting there.
/// </summary>
public sealed class ExhaustLoop
{
    /// <summary>Phases per second at a standstill. Twelve phases at 6.5 is a lap
    /// every 1.85s, which at this plume's 37px of rise is smoke drifting up
    /// rather than a jet.</summary>
    public double IdleRate = 6.5;

    /// <summary>Phases per second at <see cref="TopSpeed"/> - a lap in about a
    /// second. Well short of the trouble the tremble runs into: that one is a
    /// waveform sampled at 60fps and has Nyquist to answer to, while this steps
    /// a rendered frame at a few hertz.</summary>
    public double DriveRate = 11.0;

    /// <summary>How much of the layer's alpha survives, at rest and at speed. An
    /// idling engine breathes a haze and a working one smokes.</summary>
    public double IdleDensity = 0.70;
    public double DriveDensity = 1.00;

    /// <summary>Speed at which the engine is at full song, px/s. Per class, like
    /// <see cref="EngineTremble.TopSpeed"/>, so a heavy at its own cruise works
    /// as hard as a light at its.</summary>
    public double TopSpeed = 240.0;

    /// <summary>How many phases the atlas actually holds. Read off the layer
    /// rather than assumed: the renderer's phase count is a config value and the
    /// clock has to follow it, or the loop wraps somewhere that is not the
    /// seam.</summary>
    public int Phases = 12;

    private double _phase;

    /// <summary>Continuous position round the loop, in phases.</summary>
    public double Phase => _phase;

    /// <summary>The frame to show, or -1 when there are no phases to show.</summary>
    public int Frame => Phases <= 0
        ? -1
        : Math.Clamp((int)Math.Floor(_phase), 0, Phases - 1);

    public double Density { get; private set; } = 1.0;

    public void Advance(double speed, double delta)
    {
        if (Phases <= 0)
            return;
        double load = Load(speed);
        _phase = Mod(_phase + (IdleRate + (DriveRate - IdleRate) * load) * delta,
            Phases);
        Density = IdleDensity + (DriveDensity - IdleDensity) * load;
    }

    /// <summary>Phases per second at a given speed. Exposed so the HUD and the
    /// assertions can read the rate rather than recompute the ramp.</summary>
    public double RateAt(double speed) =>
        IdleRate + (DriveRate - IdleRate) * Load(speed);

    private double Load(double speed) =>
        Math.Clamp(Math.Abs(speed) / Math.Max(TopSpeed, 1e-6), 0.0, 1.0);

    public void Reset()
    {
        _phase = 0.0;
        Density = IdleDensity;
    }

    private static double Mod(double a, double n) => (a % n + n) % n;
}
