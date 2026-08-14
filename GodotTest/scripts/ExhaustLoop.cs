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
///
/// Two states rather than a ramp, and the ramp is what it replaced: see
/// <see cref="Binary"/> for why the analogue one kept losing, and
/// <see cref="RampShape"/> for the two rounds of trying to save it.
/// </summary>
public sealed class ExhaustLoop
{
    /// <summary>Phases per second at a standstill - the whole of the resting
    /// state under <see cref="Binary"/>. Twelve phases at 6.5 is a lap every
    /// 1.85s, which at this plume's 37px of rise is smoke drifting up rather
    /// than a jet.</summary>
    public double IdleRate = 6.5;

    /// <summary>
    /// Phases per second at <see cref="TopSpeed"/> - two and a half times the
    /// idle, a lap in three quarters of a second.
    ///
    /// It was 11.0, which is 1.7x, and 1.7x turned out not to be visible. Well
    /// short of the trouble the tremble runs into either way: that one is a
    /// waveform sampled at 60fps and has Nyquist to answer to, while this steps
    /// a rendered frame at a few hertz. What it does have to answer to is
    /// <see cref="FloorFramesPerPhase"/>, and at 16.25 a pose is held for 3.7
    /// screen frames - clear of it, but no longer clear by a mile, so this is
    /// the end of what the rate alone can buy.
    /// </summary>
    public double DriveRate = 16.25;

    /// <summary>How much of the layer's alpha survives, at rest and at speed. An
    /// idling engine breathes a haze and a working one smokes.</summary>
    public double IdleDensity = 0.70;
    public double DriveDensity = 1.00;

    /// <summary>Speed at which the engine is at full song, px/s. Per class, like
    /// <see cref="EngineTremble.TopSpeed"/>, so a heavy at its own cruise works
    /// as hard as a light at its.</summary>
    public double TopSpeed = 240.0;

    /// <summary>
    /// Two states - resting and working - rather than a ramp between them.
    ///
    /// It is the default because the analogue model lost three times running,
    /// and each loss was the same shape: a quantity that varies continuously,
    /// shown through a conveyor of twelve rendered poses, is a change nobody can
    /// see. 1.7x straight was invisible. 2.5x straight was invisible for a
    /// second reason - the grid, see <see cref="RampShape"/>. 2.5x bent was
    /// visible and then measured out at the ceiling for about half of every
    /// order longer than one cell, so most of what the curve bought was spent
    /// arriving somewhere it then sat.
    ///
    /// So the honest reading of what survived is that the plume has two states,
    /// and the ramp was an accurate model of a thing the medium cannot carry.
    /// The step is legible on every order at every length, which is the property
    /// the ramp never had.
    ///
    /// What is given up is named: the plume no longer says how fast, only
    /// whether. That was already most of the truth - the measured spread across
    /// a whole move was x2.01 to x2.32 between classes, tighter than the step it
    /// is now made of - and the effects that do say how fast are the tremble,
    /// which is continuous by nature, and the belts, which are tied to the
    /// ground. The analogue model stays behind the flag rather than being
    /// deleted, for the reason the muzzle-flash sheet does: "the step reads
    /// better" is a claim, and a claim needs both sides on screen.
    /// </summary>
    public bool Binary = BinaryByDefault;

    /// <summary>Named once, not left in the field initialiser, because a default
    /// nobody can point at is a default that quietly flips back - the argument
    /// <see cref="Recoil.ShearOnByDefault"/> makes.</summary>
    public const bool BinaryByDefault = true;

    /// <summary>
    /// The speed, as a fraction of <see cref="TopSpeed"/>, above which the tank
    /// counts as working. Only <see cref="Binary"/> reads it.
    ///
    /// Small, because there is nothing to tune here: the question is where a
    /// tank stops being stopped, and speed passes through this once on the way
    /// out and once on the way in. It cannot chatter - the mover accelerates and
    /// brakes monotonically, and the one speed it holds below cruise is the
    /// creep at a path bend, which is 12% of cruise and therefore working. A
    /// tank grinding round a corner is working, so that is the right answer and
    /// not a lucky one.
    ///
    /// Not zero, because a tank arriving is a few hundredths of a pixel a second
    /// for a frame or two, and a plume that steps up for those frames is a flick
    /// at the end of every order.
    /// </summary>
    public double MoveThreshold = 0.02;

    /// <summary>
    /// How early in the speed range the plume answers the throttle. Below 1 it
    /// answers early; at 1 it is the straight ramp this started as. Read only
    /// when <see cref="Binary"/> is off.
    ///
    /// The straight ramp is why widening the rates was not enough on its own,
    /// and the reason is the grid rather than the effect:
    /// <see cref="MovementProfile.RampDistance"/> is about 69px against a row
    /// step of 107, so a single-cell order never reaches cruise at all and a
    /// longer one spends its first half getting there. A plume that carries most
    /// of its change in the top of the range therefore spends most of its life
    /// showing the bottom of it, and what reaches the screen is an idling engine
    /// on a tank that is plainly moving.
    ///
    /// A real diesel does the same thing, and for a nearer reason than symmetry:
    /// the smoke jumps as the governor opens and then goes on much the same, so
    /// the interesting part of the curve is at the bottom.
    ///
    /// The cost is named rather than argued away - the idle look now belongs to
    /// a genuine standstill, because at a quarter of cruise the plume is already
    /// halfway up. That is the trade asked for: the change has to be visible at
    /// the speeds the tank is actually at.
    /// </summary>
    public double RampShape = 0.5;

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
    /// drifting off an idling engine and 16.25 is one under load - and a slider
    /// that set the rate directly would flatten that 2.5x on the first drag. It
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
        // Off the response and not off the rate, because the level does not
        // reach it: a plume turned down to a crawl is still an engine under
        // load, and still smokes like one.
        //
        // The same curve as the rate, though, and deliberately: both halves are
        // one engine answering one throttle, and putting them on two shapes
        // would be a second thing to hold in mind for no gain - the plume would
        // be quick and thin at the very speeds the shape is there to cover.
        Density = IdleDensity + (DriveDensity - IdleDensity) * Response(speed);
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
        IdleRate + (DriveRate - IdleRate) * Response(speed);

    /// <summary>Shared with the tremble and the engine note - see
    /// <see cref="MovementProfile.LoadAt"/>. Raw, and it stays raw: the shape
    /// below is this plume's answer to the throttle and not a different opinion
    /// about how hard the engine is working, which is what bending the shared
    /// ramp here would quietly make it.</summary>
    public double Load(double speed) => MovementProfile.LoadAt(speed, TopSpeed);

    /// <summary>
    /// How far up its own range the plume is at a given speed: under
    /// <see cref="Binary"/> a 0 or a 1, otherwise the load through
    /// <see cref="RampShape"/>.
    ///
    /// One function for both models, and every consumer goes through it - the
    /// rate, the density, the caption and the assertions. Two models mean two
    /// chances to disagree about what the engine is doing, and the way that
    /// failure looks is a plume cycling like a working one while breathing like
    /// an idling one.
    ///
    /// Exposed because the curve has to be assertable at the speeds the tank is
    /// actually at: at the two ends every shape agrees, so an ends-only check
    /// passes the straight ramp, which is the thing that was complained about.
    /// </summary>
    public double Response(double speed) => Binary
        ? (Load(speed) > MoveThreshold ? 1.0 : 0.0)
        : Math.Pow(Load(speed), Math.Max(RampShape, 1e-6));

    public void Reset()
    {
        _phase = 0.0;
        Density = IdleDensity;
    }

    private static double Mod(double a, double n) => (a % n + n) % n;
}


