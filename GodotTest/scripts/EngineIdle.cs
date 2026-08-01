using System;

namespace TankSpriteTest;

/// <summary>
/// The tremble of a running engine: a small, fast tilt of the body that never
/// quite repeats.
///
/// It is a different animal from <see cref="BodyRumble"/> and the difference is
/// the whole reason it exists as its own thing. The rumble is driven by
/// distance, because bumps are in the ground and a tank standing still meets
/// none - which is correct, and which is exactly why a stopped tank goes
/// completely dead. The engine is still running. Its input is a clock, not the
/// odometer.
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
public sealed class EngineIdle
{
    /// <summary>Shear amplitude, in the same units as <see cref="BodyPitch.Gain"/>:
    /// about 68px of lever from the contact point to the hull roof, so 0.008 is
    /// roughly half a pixel of roof travel either way.
    ///
    /// Sub-pixel is the point. A whole-pixel tremble at this rate is a buzz, and
    /// the sprite filters linearly (the pitch shear needed that), so a fraction
    /// of a pixel actually shows. Much below this it reads as the sprite being
    /// slightly out of focus rather than as motion.
    ///
    /// 0.012 was the first setting and read as too much, for a reason the roof
    /// figure hides: the lever is measured to the hull roof, and the aerial
    /// stands twice that high, so it was swinging a good pixel and a half while
    /// the hull moved under one. The turret is out of the tremble now (see
    /// <see cref="TankSprite.TurretStabilised"/>), which takes the aerial with
    /// it, and this is the amplitude that suits what is left.</summary>
    public double Gain = 0.008;

    public double PitchHz = 11.5;
    public double RollHz = 7.3;

    /// <summary>Speed at which the tremble is fully gone, px/s. Set from the
    /// tank's profile.
    ///
    /// The engine does not stop working once the tank rolls, but the ground
    /// starts feeding in a jolt an order of magnitude larger and the two on top
    /// of each other just read as noise. So this hands over rather than adding:
    /// standing still it is the only thing moving, under way the rumble has it.</summary>
    public double FadeSpeed = 60.0;

    /// <summary>Tilt about the lateral axis, same sign convention as
    /// <see cref="BodyPitch.Angle"/>.</summary>
    public double Pitch { get; private set; }

    /// <summary>Tilt about the longitudinal axis, positive to the right of the
    /// hull heading.</summary>
    public double Roll { get; private set; }

    private double _time;

    public void Advance(double speed, double delta)
    {
        _time += delta;
        double fade = 1.0 - Math.Clamp(Math.Abs(speed) / Math.Max(FadeSpeed, 1e-6), 0.0, 1.0);
        double gain = Gain * fade;
        Pitch = gain * Math.Sin(_time * PitchHz * Math.Tau);
        // offset so the two axes do not start together and give one big lurch
        // on the first frame
        Roll = gain * Math.Sin(_time * RollHz * Math.Tau + 1.7);
    }

    public void Reset()
    {
        _time = 0.0;
        Pitch = 0.0;
        Roll = 0.0;
    }
}
