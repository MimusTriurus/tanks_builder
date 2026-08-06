using System;
using System.Collections.Generic;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// One tank's voice: its own players, driven off its own clocks.
///
/// Per vehicle for the reason every clock in <see cref="Vehicle"/> is per
/// vehicle, and it is more audible here than anywhere else. One shared engine
/// node would put three tanks in phase, and three engines beating as one is not
/// three engines - it is one engine three times as loud, which is exactly what a
/// bench built to compare three tanks must not produce.
///
/// Nothing here decides anything. Every value is pulled from the vehicle at the
/// moment of the frame, the way <see cref="EffectLayer"/> pulls the phase it
/// draws: the clocks already know how fast the belt is turning and how hard the
/// engine is working, and a second opinion about either is a second thing to keep
/// in agreement.
///
/// <see cref="AudioStreamPlayer2D"/> rather than the plain player because the
/// three stand in different places, and the node is moved to the vehicle's ground
/// point - not to the sprite's origin, which floats a scaled fifty-odd pixels
/// above the ground. Inaudible at these distances, and done anyway because
/// <see cref="Vehicle.GroundPoint"/> is where the tank *is*, and a second idea
/// about that is how the sprite came to stand 17px off its hex.
/// </summary>
public sealed partial class VehicleAudio : Node2D
{
    /// <summary>This class's own sounds - engine, belt, gun.</summary>
    public required SoundSet Own { get; init; }

    /// <summary>What happens to any tank - hits, burning, death. Shared.</summary>
    public required SoundSet Common { get; init; }

    /// <summary>
    /// Off silences everything without unloading it, the way every other effect
    /// toggle in the harness leaves its layer loaded.
    ///
    /// It has to silence the loops through their volume rather than by stopping
    /// them - see <see cref="_gate"/>.
    /// </summary>
    public bool Enabled = true;

    /// <summary>Master trim in dB, so the whole bench can be brought down without
    /// touching the balance between events.</summary>
    public float MasterDb;

    /// <summary>
    /// Whether this is the tank being driven. The other two are trimmed by
    /// <see cref="UnfocusedDb"/>.
    ///
    /// This is the opposite of what the bench decided for the *picture*, and the
    /// difference is worth stating because the earlier decision was right too.
    /// <see cref="SelectionRing"/> exists because dimming the other tanks was
    /// rejected: the sprites are the thing under judgement, and a tank shown
    /// faint is a different picture, so the mark went on the ground instead where
    /// no pixel of any tank is touched.
    ///
    /// Sound has no ground to put a mark on, and worse, it has no separation to
    /// begin with. Three engines at one level do not sit side by side the way
    /// three sprites do - they sum, and what comes out is not three engines but
    /// one indistinct one. Attenuation is the only handle there is, and unlike
    /// dimming it does not alter what the near tank sounds like, only what is
    /// under it.
    ///
    /// Which is why it is a level and not a mute: at 0 dB the mix is flat again
    /// and the three can be compared against each other, which is the thing the
    /// bench is for.
    /// </summary>
    public bool Focused = true;

    /// <summary>How far the tanks that are not being driven sit back, in dB.
    ///
    /// Enough to tell which engine is yours, not so much that the others stop
    /// being present - a field of one audible tank and two silent ones is a bench
    /// that has thrown away two thirds of its subject.</summary>
    public float UnfocusedDb = -9.0f;

    /// <summary>
    /// The trim for this tank's continuous voices - the engine, the belts, the
    /// fire.
    ///
    /// **Only those.** The focus trim is an answer to one problem, and the
    /// problem is that three engines at one level sum into one indistinct
    /// engine; it is a statement about ambience, about which machine you are
    /// sitting in. A gun going off and a shell striking armour are not ambience,
    /// they are what is happening in the battle, and holding them back by nine
    /// decibels because they happened to somebody else is the wrong answer to a
    /// question nobody asked.
    ///
    /// It is also worse than merely wrong here, because in an engagement the
    /// tank being driven is nearly always the shooter. Trimmed, the only impacts
    /// heard clearly are the ones on your own hull - so a light tank plinking
    /// away at a medium produces a report at -4dB and a ricochet at -15dB
    /// starting on the same frame, and the ricochet is simply not there. Which
    /// is exactly how it was reported.
    /// </summary>
    private float Trim => MasterDb + (Focused ? 0.0f : UnfocusedDb);

    /// <summary>The trim for one-shots - the gun, the impact, the wreck. No
    /// focus in it: an event is an event whoever it happened to.</summary>
    private float EventTrim => MasterDb;

    /// <summary>The trim actually being applied, in dB, for <c>--trace</c>. A
    /// focus that silently failed to move would send you looking at the mixer
    /// rather than at the selection - the same reason the recoil mode prints
    /// '@turret' or '@body'.</summary>
    public float AppliedDb => Trim;

    /// <summary>The trim a one-shot gets, exposed beside <see cref="AppliedDb"/>
    /// so "the focus holds back the ambience and not the battle" is asserted
    /// rather than left as a comment. It is the kind of rule that drifts back:
    /// one trim is obviously simpler until a ricochet goes missing.</summary>
    public float AppliedEventDb => EventTrim;

    // Loops. Started once in _Ready and never restarted - see Gate().
    private AudioStreamPlayer2D? _engine;
    private AudioStreamPlayer2D? _track;
    private AudioStreamPlayer2D? _burn;

    // One-shots. One player per kind of event, so a second shell arriving while
    // the first is still ringing cuts it off rather than layering. A limit, named
    // because it is one: the bench fires one round at a time by hand, and a pool
    // would be three more moving parts for a case nobody can produce from the
    // keyboard.
    private AudioStreamPlayer2D? _gun;
    private AudioStreamPlayer2D? _armour;
    private AudioStreamPlayer2D? _death;

    /// <summary>
    /// Roughly how loud each loop sits when it is fully on, in dB.
    ///
    /// The engine is under everything and has to stay under it; the belt is the
    /// detail on top; the fire is an exceptional state and is allowed to be loud.
    /// </summary>
    public float EngineDb = -12.0f;
    public float TrackDb = -14.0f;
    public float BurnDb = -10.0f;
    public float GunDb = -4.0f;
    public float ArmourDb = -6.0f;
    public float DeathDb = -2.0f;

    /// <summary>Engine note at rest and at cruise, as a playback rate.
    ///
    /// A quarter up over the ramp: enough that pulling away is heard, little
    /// enough that the recording still sounds like the same engine. The rate also
    /// drags the timbre with it, which is why this is narrow where
    /// <see cref="EngineTremble.LoadFactor"/> can afford 1.35 - that one shifts a
    /// synthesised wobble, this one resamples a diesel.</summary>
    public float EngineIdlePitch = 0.85f;
    public float EngineDrivePitch = 1.10f;

    /// <summary>How far down the belt is trimmed when it is barely turning, in dB
    /// relative to <see cref="TrackDb"/>. Paired with the pitch floor - see
    /// <see cref="TrackMinPitch"/>.</summary>
    public float TrackCrawlTrim = -10.0f;


    /// <summary>Engine note gain at rest and at cruise, in dB relative to
    /// <see cref="EngineDb"/>. An idling engine is quieter as well as lower.</summary>
    public float EngineIdleTrim = -6.0f;

    /// <summary>
    /// Slowest the belt recording is allowed to be resampled, as a playback rate.
    ///
    /// Unity is not a tuning choice here: the rattle is pitched against
    /// <see cref="TrackLoop.MaxPhaseRate"/>, the fastest the belt will ever be
    /// *shown* turning, so 1.0 lands exactly where the picture stops speeding up
    /// and the two flatten off together. Which leaves only the bottom end to
    /// choose, and it is a floor rather than a range: a rattle resampled far
    /// below unity stops sounding like metal and starts sounding like a bag of
    /// gravel.
    ///
    /// Below this the belt is turning slowly enough that its own quiet does the
    /// work instead - the gate is proportional too.
    /// </summary>
    public float TrackMinPitch = 0.55f;

    /// <summary>
    /// Seconds for a loop to fade in or out.
    ///
    /// Loops are never stopped and restarted while the bench is running, because
    /// Play() begins at sample zero: a belt told to play afresh every time the
    /// stick is nudged rattles from the top each time, which reads as a skipping
    /// record rather than as a track. So they run continuously and this ramps
    /// their volume. Short enough to feel immediate, long enough not to click.
    /// </summary>
    public double FadeSeconds = 0.12;

    private double _engineGate;
    private double _trackGate;
    private double _burnGate;

    /// <summary>
    /// What the loops are doing, for <c>--trace</c>.
    ///
    /// Sound has no screenshot, so the trace is the only channel where "is it
    /// actually coming out" can be answered - which makes it the equivalent of
    /// the check sheets, and the same rule applies: a set that loaded is not a
    /// set that is audible. Printed rather than shown in the panel for the reason
    /// the zoom is: a capture has no panel, and a diagnostic that disappears with
    /// --no-ui is not a diagnostic.
    /// </summary>
    public double EngineGate => _engineGate;
    public double TrackGate => _trackGate;
    public double BurnGate => _burnGate;
    public float EnginePitch => _engine?.PitchScale ?? 0.0f;
    public float TrackPitch => _track?.PitchScale ?? 0.0f;

    public override void _Ready()
    {
        _engine = Loop("engine_cycle", Own);
        _track = Loop("track_cycle", Own);
        _burn = Loop("burn_cycle", Common);
        _gun = OneShot();
        _armour = OneShot();
        _death = OneShot();
    }

    private AudioStreamPlayer2D? Loop(string name, SoundSet set)
    {
        AudioStreamWav? stream = set[name];
        if (stream is null)
            return null;
        var player = new AudioStreamPlayer2D
        {
            Stream = stream,
            // Silent to begin with; Gate() brings it up. Starting audible and
            // correcting on the first frame is one frame of full-volume engine
            // before anything has moved.
            VolumeDb = -80.0f,
            // The field is a few hundred pixels across and these are meant to be
            // heard, not localised realistically. Wide enough that a tank at the
            // far edge is still present.
            MaxDistance = 4000.0f,
        };
        AddChild(player);
        player.Play();
        return player;
    }

    private AudioStreamPlayer2D OneShot()
    {
        var player = new AudioStreamPlayer2D { MaxDistance = 4000.0f };
        AddChild(player);
        return player;
    }

    /// <summary>
    /// Follow the vehicle for one frame.
    ///
    /// Called from Main's per-vehicle pass, after the clocks have been advanced,
    /// for the same reason <see cref="EffectLayer"/> reads its phase late: the
    /// numbers this uses are results of this frame's step, and reading them before
    /// it is reading last frame's.
    /// </summary>
    public void Update(Vehicle v, double delta)
    {
        Position = v.GroundPoint;

        double load = MovementProfile.LoadAt(v.Speed, v.Profile.TopSpeed);

        // The engine is always turning, so its gate is on whenever sound is. It
        // is the one loop with no condition of its own, which is also why the
        // bench has no "engine off" - a tank on the field is a tank running.
        Gate(_engine, ref _engineGate, Enabled, delta,
             EngineDb + (float)((1.0 - load) * EngineIdleTrim));
        if (_engine is not null)
            _engine.PitchScale =
                EngineIdlePitch + (float)((EngineDrivePitch - EngineIdlePitch) * load);

        // The belt. Rate from the clock's *achieved* phase rate, so the rattle
        // and the tread agree even where the tread is deliberately slipping - see
        // TrackLoop.PhaseRate. And gated on the belt turning rather than on the
        // tank being under orders: a tank held still has silent tracks, which is
        // the one thing about this layer nobody has to be told.
        double turning = Math.Abs(v.Track.PhaseRate);
        double wound = turning / Math.Max(v.Track.MaxPhaseRate, 1e-6);
        Gate(_track, ref _trackGate, Enabled && turning > 0.01, delta,
             // Quieter as well as lower when the belt is barely moving. Pitch
             // alone cannot carry it, because the floor stops it going all the
             // way down and a crawling tank would still rattle at full volume.
             TrackDb + (float)((1.0 - Math.Min(wound, 1.0)) * TrackCrawlTrim));
        if (_track is not null && turning > 0.01)
            _track.PitchScale = (float)Math.Clamp(wound, TrackMinPitch, 1.0);

        Gate(_burn, ref _burnGate, Enabled && v.Burning, delta, BurnDb);
    }

    /// <summary>
    /// Ramp one loop towards on or off.
    ///
    /// The gate is a 0..1 level rather than a boolean so that switching sound off
    /// while a tank is at speed fades rather than cuts, and so that the two ends
    /// of the ramp are the same code. Below the floor the player is paused, which
    /// costs nothing and keeps a silent bench genuinely silent rather than mixing
    /// three inaudible engines.
    /// </summary>
    private void Gate(AudioStreamPlayer2D? player, ref double gate, bool want,
                      double delta, float fullDb)
    {
        if (player is null)
            return;
        double step = FadeSeconds <= 0.0 ? 1.0 : delta / FadeSeconds;
        gate = Math.Clamp(gate + (want ? step : -step), 0.0, 1.0);
        if (gate <= 0.001)
        {
            player.StreamPaused = true;
            return;
        }
        player.StreamPaused = false;
        // Linear in amplitude, not in dB: a dB ramp from -80 spends most of its
        // travel inaudible and then arrives all at once.
        player.VolumeDb = Trim + fullDb + (float)(20.0 * Math.Log10(gate));
    }

    /// <summary>The gun. Same trigger as the flash and the tube, third clock -
    /// see <see cref="RecoilLoop"/> for why one counter cannot serve them.</summary>
    public void Fire()
    {
        Play(_gun, Own["gun_shot"], GunDb, EventTrim);
    }

    /// <summary>
    /// A shell on the armour, told apart by how deep it went.
    ///
    /// This is the pairing the whole search for recordings was for: a ricochet and
    /// a penetration are different events, so the shallow levels get one file and
    /// the deep one another. Volume alone would have said "a bigger version of the
    /// same thing", which is what calibre meant in the sprites for as long as it
    /// only scaled the burst.
    ///
    /// <paramref name="level"/> is the damage level the plate reached, 0 based,
    /// or -1 from a tank with no marks to leave - which still took the shell, so
    /// it still rings, at the shallowest of the two.
    ///
    /// <paramref name="shell"/> is how many rounds this hull has taken, and it
    /// picks which of the two takes plays. It used to be the level that did, and
    /// that was wrong in a way that is invisible from outside: there are three
    /// levels and the split is at 2, so the penetration branch was only ever
    /// reachable at level 2 and <c>(2 % 2) + 1</c> is always 1. `armour_hit2` was
    /// staged, loaded, and could not be heard. Two shells through the same plate
    /// simply sounded identical, which reads as one event rather than as a take
    /// that never plays - and the class matchup makes it worse rather than
    /// better, because level 2 is now the *only* penetration.
    ///
    /// The count rather than a random pick because --capture and --trace fix the
    /// time step so two runs can be compared, and a random one would differ
    /// between them. The victim's count, so it is shells into this hull however
    /// many guns are pointed at it.
    /// </summary>
    public void Struck(int level, int shell)
    {
        int deep = Math.Max(level, 0);
        string name = deep >= 2 ? "armour_hit" : "armour_ricochet";
        int[] takes = ImpactTakes[name];
        int which = takes[Math.Abs(shell) % takes.Length];
        Play(_armour, Common[$"{name}{which}"] ?? Common[$"{name}1"], ArmourDb,
             EventTrim, PitchOf(shell));
    }

    /// <summary>
    /// Which takes of each impact are actually played.
    ///
    /// Two takes exist so that two shells do not sound identical - and that only
    /// works if they are **interchangeable**, which is an assumption about the
    /// recordings rather than about the design. It does not hold for the
    /// ricochet, and both halves of finding that out were reported by ear:
    ///
    ///  * with the take chosen by damage level, a light tank on a medium is
    ///    always level 0 and so always played take 1 - "no ricochet at all";
    ///  * with it chosen by the shell count, take 2 came in every other shot -
    ///    "the ricochet fires every other time".
    ///
    /// Both point at take 1, and the measurement agrees: it sits at 1531Hz
    /// against take 2's 2627Hz, nearly an octave lower and that much nearer the
    /// 826Hz gun report going off on the same frame. Levelling them to equal RMS
    /// did not save it, because what is left is not level but where they sit.
    ///
    /// So the ricochet plays the one that reads, and the variety comes from
    /// <see cref="PitchOf"/> instead - same sound, never quite the same twice,
    /// and no take that can go missing. The penetration pair is left alternating:
    /// its two are 1500 and 1073Hz, half the gap, and nobody has reported one of
    /// them absent. Changing what has not been heard to be wrong is how the thing
    /// that was wrong gets lost.
    /// </summary>
    private static readonly Dictionary<string, int[]> ImpactTakes = new()
    {
        ["armour_ricochet"] = new[] { 2 },
        ["armour_hit"] = new[] { 1, 2 },
    };

    /// <summary>
    /// A small deterministic pitch wobble per shell, so one recording does not
    /// read as one canned sound.
    ///
    /// +-4% in 2% steps, off a stride of 7 into 5 so it does not fall into the
    /// two-shot pattern the takes just came out of. Deterministic for the reason
    /// the scatter is: --capture and --trace fix the time step so two runs can be
    /// diffed, and a random wobble would be measuring itself.
    /// </summary>
    public static float PitchOf(int shell) =>
        1.0f + ((Math.Abs(shell) * 7) % 5 - 2) * 0.02f;

    /// <summary>Loaded but not yet triggered by anything: the bench has no
    /// destroyed state. Here because the file is staged and the alternative is a
    /// sound nobody can find, and it costs one player.</summary>
    public void Destroyed(int which)
    {
        Play(_death, Common[$"destroyed{Math.Clamp(which, 1, 3)}"], DeathDb, EventTrim);
    }

    /// <summary><paramref name="trim"/> is <see cref="Trim"/> for a loop and
    /// <see cref="EventTrim"/> for a one-shot, and passing it rather than reading
    /// it is what makes the caller say which it is.</summary>
    private void Play(AudioStreamPlayer2D? player, AudioStreamWav? stream, float db,
                      float trim, float pitch = 1.0f)
    {
        if (!Enabled || player is null || stream is null)
            return;
        player.Stream = stream;
        player.VolumeDb = trim + db;
        player.PitchScale = pitch;
        player.Play();
    }
}
