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

    /// <summary>
    /// A multiplier over the pair of rates, not a rate.
    ///
    /// The same argument as <see cref="EngineTremble.Level"/> and the class
    /// sizes: the two figures are apart on purpose - 6.5 at rest is smoke
    /// drifting off an idling engine and 11.0 is one under load - and a slider
    /// that set the rate directly would flatten that 1.7x on the first drag. It
    /// scales both ends and leaves the ratio where it was measured.
    ///
    /// It does not touch the density, which is the other half of the load ramp.
    /// How thick the smoke is is not the question this knob asks, and answering
    /// two questions with one control makes any A/B taken on it a measurement of
    /// both - the objection that keeps the tracer's size and its smoke's on two
    /// sliders.
    /// </summary>
    public double Level = 1.0;

    /// <summary>Frames one phase is held for at the ceiling, and the whole of
    /// why this slider has a limit worth printing. The plume is a rendered
    /// frame stepped a few times a second, so it has no Nyquist to answer to the
    /// way the tremble's waveform does - what it has is a conveyor of twelve
    /// poses, and below about two screen frames a pose the loop stops reading as
    /// smoke moving and starts reading as a flicker. There is nothing for it to
    /// blur into the way a track has its own smear.</summary>
    public const double FloorFramesPerPhase = 2.0;

    /// <summary>Screen frames one phase lasts at a given rate. Named here rather
    /// than divided out at the caption, so the number the slider prints and the
    /// number the assertion checks are the same number.</summary>
    public static double FramesPerPhase(double rate) =>
        rate <= 0.0 ? double.PositiveInfinity : 60.0 / rate;

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
        _phase = Mod(_phase + RateAt(speed) * delta, Phases);
        // Off the ramp rather than off the rate, because the level does not
        // reach it: a plume turned down to a crawl is still an engine under
        // load, and still smokes like one.
        Density = IdleDensity + (DriveDensity - IdleDensity) * Load(speed);
    }

    /// <summary>Phases per second at a given speed. Exposed so the panel and the
    /// assertions can read the rate rather than recompute the ramp.</summary>
    public double RateAt(double speed) => Level * Tuned(speed);

    /// <summary>The rate at a given speed and a given level, without asking what
    /// level this instance is on - see <see cref="EngineTremble.TravelAt"/>. The
    /// caption is showing the board's level, which reaches the vehicles a frame
    /// later and does not reach them at all while the exhaust is switched off,
    /// so one read off an instance would freeze under the drag describing
    /// it.</summary>
    public double RateAt(double speed, double level) => level * Tuned(speed);

    private double Tuned(double speed) =>
        IdleRate + (DriveRate - IdleRate) * Load(speed);

    /// <summary>Shared with the tremble and the engine note - see
    /// <see cref="MovementProfile.LoadAt"/>.</summary>
    public double Load(double speed) => MovementProfile.LoadAt(speed, TopSpeed);

    public void Reset()
    {
        _phase = 0.0;
        Density = IdleDensity;
    }

    private static double Mod(double a, double n) => (a % n + n) % n;
}
