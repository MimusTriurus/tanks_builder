using System;

namespace TankSpriteTest;

/// <summary>
/// The clock behind a burning tank: two phases going round, at two rates.
///
/// Like <see cref="ExhaustLoop"/> it walks a loop the renderer already closed on
/// itself, so there is no table of held frames to run out - a wreck burns for as
/// long as it is on screen, and the shot's <see cref="EffectLayer.Hold"/> shape
/// does not apply.
///
/// Two things separate it from the exhaust's clock, and both are the point.
///
/// There is no load ramp. An engine under load smokes harder, which is what
/// makes the plume say something about the tank; a fire does not burn harder
/// because the tank is moving, so tying it to speed would be an effect lying
/// about what it shows. Speed is not an input here at all.
///
/// The flame and the column run at different rates off the same rendered
/// phases. Nothing ties them: they are separate layers with separate frame
/// indices, and each closes on itself independently, so any rate works for
/// either. They want very different tempos - flame flickers, smoke drifts - and
/// one rate for both means choosing between a smouldering flame and a column
/// going up like a jet. The renderer cannot fix this for us; it renders one
/// phase count per job.
/// </summary>
public sealed class BurnLoop
{
    /// <summary>Phases per second for the flame. Twelve at 10 is a lap every
    /// 1.2s, which reads as burning rather than as a lamp left on.</summary>
    public double FireRate = 10.0;

    /// <summary>Phases per second for the column. Twelve at 5 is 2.4s a lap,
    /// and over this column's hull and a half of rise that is smoke climbing at
    /// about 95px a second.</summary>
    public double SmokeRate = 5.0;

    /// <summary>Phases the atlas holds. Read off the layer, never assumed - the
    /// renderer's count is a config value, and a clock that wraps anywhere but
    /// the seam pops there.</summary>
    public int Phases = 12;

    private double _fire;
    private double _smoke;

    /// <summary>Continuous positions round the two loops, in phases. Exposed for
    /// the HUD and the assertions.</summary>
    public double FirePhase => _fire;
    public double SmokePhase => _smoke;

    public int FireFrame => FrameOf(_fire);
    public int SmokeFrame => FrameOf(_smoke);

    private int FrameOf(double phase) => Phases <= 0
        ? -1
        : Math.Clamp((int)Math.Floor(phase), 0, Phases - 1);

    public void Advance(double delta)
    {
        if (Phases <= 0)
            return;
        _fire = Mod(_fire + FireRate * delta, Phases);
        _smoke = Mod(_smoke + SmokeRate * delta, Phases);
    }

    public void Reset()
    {
        _fire = 0.0;
        _smoke = 0.0;
    }

    private static double Mod(double a, double n) => (a % n + n) % n;
}
