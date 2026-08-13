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
/// <b>It shakes the stern, and only the stern.</b> The engine is in one end of
/// the tank, so that is the end that moves: a pitch about the bow, plus a yaw
/// about it, and the bow itself all but planted. It was a tilt of the whole body
/// about the contact patch, which is right for a hull rocking on its suspension
/// and wrong for a motor bolted in the back - and it had a fault of its own worth
/// the read in <see cref="TankSprite.SternWeight"/>: the lever was the screen row,
/// which mixes height with depth, so whichever end faced away from the camera
/// shook seven times as hard as the near one and the shaking end swapped as the
/// hull turned.
///
/// A yaw rather than a roll for the second axis, and that follows from the first.
/// A roll about the hull's own length moves both ends alike, so it cannot be part
/// of an effect whose whole statement is that one end moves; the tail wagging is
/// what an engine's torque reaction does to a hull it sits in the back of.
///
/// Two incommensurate frequencies, one per axis. A single sine reads as a
/// mechanism cycling; two that never line up read as an engine loping. They are
/// deliberately low - a real idle fires far above 30Hz and would alias into
/// nonsense at 60fps - so this is the body's response to the engine rather than
/// the engine itself, which is the only part a 60fps sprite can carry anyway.
/// </summary>
public sealed class EngineTremble
{
    /// <summary>What one unit of amplitude is worth at the stern, px. The lever
    /// these numbers act on, and so the only way to say what one of them means.
    /// Named here because three places need the same one: the self-test's
    /// thresholds, the panel's caption, and the reasoning in the gain comments
    /// below.
    ///
    /// <b>Sixty-eight, which is what it was as the roof lever, and keeping the
    /// number is the point.</b> The whole tuning history below - the pair of gains,
    /// the level going to 2.5, every pixel figure measured against them - was
    /// judged in travel, not in amplitude, so holding the travel constant while the
    /// lever moves from the roof to the stern keeps all of it true. It is not the
    /// hull's length: that differs by class (188/183/182px) and would make how hard
    /// an engine shakes a property of which tank it is in. The length only shapes
    /// the falloff - see <see cref="TankSprite.SternWeight"/>.</summary>
    public const double SternLeverPx = 68.0;

    /// <summary>Shear amplitude at a standstill, in the same units as
    /// <see cref="BodyPitch.Gain"/>: about 68px of lever out to the stern, so
    /// 0.005 is about a third of a pixel of stern travel either way.
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
    /// reason the pixel figure hid: the lever was then measured to the hull roof,
    /// and the aerial stands twice that high, so it was swinging a good pixel and a
    /// half while the hull moved under one. The turret is out of the tremble now
    /// (see <see cref="TankSprite.TurretStabilised"/>), which takes the aerial
    /// with it, so the same number on the hull alone is a good deal quieter than
    /// the one that was complained about. The aerial cannot come back into it
    /// either: the lever runs along the hull now, so height buys no amplitude at
    /// all.</summary>
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
    /// is not much of a knob: around 2 the stern travel goes over a pixel and a
    /// half and the engine turns into a buzz.
    /// </summary>
    public double Level = 1.0;

    /// <summary>Frequencies at rest, Hz. One per axis, and incommensurate: see the
    /// class summary.</summary>
    public double PitchHz = 11.5;
    public double YawHz = 7.3;

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

    /// <summary>Pitch about the bow: positive lifts the stern. Multiplied by
    /// <see cref="SternLeverPx"/> it is the stern's travel up the screen, and a
    /// world vertical projects to a screen vertical at any heading, so that much is
    /// exact.</summary>
    public double Pitch { get; private set; }

    /// <summary>Yaw about the bow, positive wagging the stern to the left of the
    /// hull heading - the side <see cref="AtlasSet.GroundDirection"/> of
    /// <c>HullFacing + 90</c> points, since headings count anticlockwise.</summary>
    public double Yaw { get; private set; }

    // Phase is integrated rather than computed as frequency times elapsed time,
    // and that is not a stylistic preference. With a rate that changes, sin(f*t)
    // moves the whole waveform when f moves: pulling away from a stop would step
    // the phase by however much the frequency changed times however long the
    // tank had been sitting there, which is a pop at exactly the moment the
    // effect is meant to be smoothing the start.
    private double _pitchPhase;
    private double _yawPhase;

    public void Advance(double speed, double delta)
    {
        double rate = 1.0 + (LoadFactor - 1.0) * Load(speed);
        _pitchPhase += PitchHz * rate * delta;
        _yawPhase += YawHz * rate * delta;
        double gain = GainAt(speed);
        Pitch = gain * Math.Sin(_pitchPhase * Math.Tau);
        // offset so the two axes do not start together and give one big lurch
        // on the first frame
        Yaw = gain * Math.Sin(_yawPhase * Math.Tau + 1.7);
    }

    /// <summary>How hard the engine is working, 0 at rest and 1 at cruise. One
    /// ramp feeds both the amplitude and the frequency, because both come from
    /// the same thing turning faster - and it is
    /// <see cref="MovementProfile.LoadAt"/> rather than an expression of its own,
    /// because the plume and the engine note ask the same question.</summary>
    private double Load(double speed) => MovementProfile.LoadAt(speed, TopSpeed);

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
        _yawPhase = 0.0;
        Pitch = 0.0;
        Yaw = 0.0;
    }
}
