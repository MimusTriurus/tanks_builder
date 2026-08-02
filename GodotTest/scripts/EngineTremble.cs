using System;

namespace TankSpriteTest;

/// <summary>
/// The tremble of a running engine: a small, fast tilt of the body that never
/// quite repeats. Running whenever the engine is, which is always.
///
/// It is a different animal from <see cref="BodyRumble"/> and the difference is
/// the whole reason it exists as its own thing. The rumble is driven by
/// distance, because bumps are in the ground and a tank standing still meets
/// none - which is correct, and which is exactly why a stopped tank went
/// completely dead. The engine is still running. Its input is a clock, not the
/// odometer, so this covers a standing tank and a moving one with one mechanism
/// instead of leaving a hole at zero speed.
///
/// It is a tilt rather than a heave, and that is not an arbitrary choice. Engine
/// vibration reaches the hull through the mounts and the suspension while the
/// tracks stay planted on the ground, so what moves is the top of the vehicle
/// and what does not is the contact patch. That is a shear about the contact
/// point, which is the transform the body pitch already built.
///
/// Two incommensurate frequencies, one per axis. A single sine reads as a
/// mechanism cycling; two that never line up read as an engine loping. They are
/// deliberately low - a real idle fires far above 30Hz and would alias into
/// nonsense at 60fps - so this is the body's response to the engine rather than
/// the engine itself, which is the only part a 60fps sprite can carry anyway.
/// </summary>
public sealed class EngineTremble
{
    /// <summary>Distance from the contact patch to the hull roof, px - the lever
    /// a shear amplitude acts on, and so the only way to say what one of these
    /// numbers is worth. Named here because three places need it and want the
    /// same one: the self-test's thresholds, the panel's caption, and the
    /// reasoning in the gain comments below.</summary>
    public const double RoofLeverPx = 68.0;

    /// <summary>Shear amplitude at a standstill, in the same units as
    /// <see cref="BodyPitch.Gain"/>: about 68px of lever from the contact point
    /// to the hull roof, so 0.005 is about a third of a pixel of roof travel
    /// either way.
    ///
    /// Sub-pixel is the point. A whole-pixel tremble at this rate is a buzz, and
    /// the sprite filters linearly (the pitch shear needed that), so a fraction
    /// of a pixel actually shows. This is close to the floor, though: much below
    /// it and the sprite reads as slightly out of focus rather than as
    /// moving.</summary>
    public double RestGain = 0.005;

    /// <summary>Shear amplitude at <see cref="TopSpeed"/>.
    ///
    /// Bigger than the rest figure for two reasons that point the same way. The
    /// engine is under load and genuinely shaking the hull harder; and the tank
    /// is sliding across the screen, which hides a small vibration, so the same
    /// amplitude that reads at a standstill goes unnoticed under way.
    ///
    /// 0.012 was once the flat setting for both and read as too much, for a
    /// reason the roof figure hides: the lever is measured to the hull roof, and
    /// the aerial stands twice that high, so it was swinging a good pixel and a
    /// half while the hull moved under one. The turret is out of the tremble now
    /// (see <see cref="TankSprite.TurretStabilised"/>), which takes the aerial
    /// with it, so the same number on the hull alone is a good deal quieter than
    /// the one that was complained about.</summary>
    public double DriveGain = 0.012;

    /// <summary>
    /// A plain multiplier over both gains, and the one number the panel puts on
    /// a slider.
    ///
    /// A multiplier rather than an absolute amplitude, because the two gains are
    /// not independent. Standing and moving are far apart on purpose - a still
    /// tank is the only still thing on screen and a third of a pixel registers,
    /// while one under way slides past hexes and the same travel disappears into
    /// it - and a slider that set the amplitude directly would flatten that
    /// difference on the first drag. This scales the pair and leaves the ratio
    /// where it was measured.
    ///
    /// It reaches well past 1 on purpose. The tuned setting is the interesting
    /// one to leave it at, but a knob that cannot show what too much looks like
    /// is not much of a knob: around 2 the roof travel goes over a pixel and a
    /// half, the aerial with it, and the engine turns into a buzz.
    /// </summary>
    public double Level = 1.0;

    /// <summary>Frequencies at rest, Hz.</summary>
    public double PitchHz = 11.5;
    public double RollHz = 7.3;

    /// <summary>
    /// What the frequencies are multiplied by at <see cref="TopSpeed"/>: the
    /// engine turning faster under load.
    ///
    /// The headroom here is small and hard. A real engine goes from perhaps 800
    /// rpm to 2000 between idle and cruise, but 11.5Hz times that is 29Hz, which
    /// at 60fps is a hair under Nyquist and samples into a slow wobble that has
    /// nothing to do with the signal. 1.35 puts cruise at 15.5Hz, still four
    /// frames to the cycle, and that is about as far as this can be taken
    /// without rendering faster than the screen.
    /// </summary>
    public double LoadFactor = 1.35;

    /// <summary>Speed at which the engine is at full song, px/s. Set from the
    /// tank's profile so a heavy at its cruise sounds as worked as a light at
    /// its own, rather than being stuck near idle because it is slower.</summary>
    public double TopSpeed = 240.0;

    /// <summary>Tilt about the lateral axis, same sign convention as
    /// <see cref="BodyPitch.Angle"/>.</summary>
    public double Pitch { get; private set; }

    /// <summary>Tilt about the longitudinal axis, positive to the right of the
    /// hull heading.</summary>
    public double Roll { get; private set; }

    // Phase is integrated rather than computed as frequency times elapsed time,
    // and that is not a stylistic preference. With a rate that changes, sin(f*t)
    // moves the whole waveform when f moves: pulling away from a stop would step
    // the phase by however much the frequency changed times however long the
    // tank had been sitting there, which is a pop at exactly the moment the
    // effect is meant to be smoothing the start.
    private double _pitchPhase;
    private double _rollPhase;

    public void Advance(double speed, double delta)
    {
        double rate = 1.0 + (LoadFactor - 1.0) * Load(speed);
        _pitchPhase += PitchHz * rate * delta;
        _rollPhase += RollHz * rate * delta;
        double gain = GainAt(speed);
        Pitch = gain * Math.Sin(_pitchPhase * Math.Tau);
        // offset so the two axes do not start together and give one big lurch
        // on the first frame
        Roll = gain * Math.Sin(_rollPhase * Math.Tau + 1.7);
    }

    /// <summary>How hard the engine is working, 0 at rest and 1 at cruise. One
    /// ramp feeds both the amplitude and the frequency, because both come from
    /// the same thing turning faster.</summary>
    private double Load(double speed) =>
        Math.Clamp(Math.Abs(speed) / Math.Max(TopSpeed, 1e-6), 0.0, 1.0);

    /// <summary>Shear amplitude at a given speed. Exposed alongside
    /// <see cref="PitchRateAt"/> so the seam cost can be asserted at the speed
    /// it actually occurs at, rather than against a single worst number.</summary>
    public double GainAt(double speed) =>
        Level * (RestGain + (DriveGain - RestGain) * Load(speed));

    /// <summary>Frequency of the pitch axis at a given speed, Hz. Exposed so the
    /// Nyquist headroom can be asserted rather than recalculated by hand every
    /// time someone reaches for <see cref="LoadFactor"/>.</summary>
    public double PitchRateAt(double speed) =>
        PitchHz * (1.0 + (LoadFactor - 1.0) * Load(speed));

    public void Reset()
    {
        _pitchPhase = 0.0;
        _rollPhase = 0.0;
        Pitch = 0.0;
        Roll = 0.0;
    }
}
