using System;
using System.Collections.Generic;
using System.IO;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The bench's audio, loaded off disk the way <see cref="AtlasSet"/> loads
/// pixels: absolute path, no import step, so replacing a .wav is heard on the
/// next run.
///
/// Two kinds, and the split is the same one <see cref="AtlasSet"/> and
/// <see cref="FlashSheet"/> already draw. What a tank *is* differs per class and
/// is loaded per class - its engine, its belt, its gun. What happens *to* a tank
/// is the same whoever it happens to, so the armour hits, the burning and the
/// death are loaded once and shared, exactly as the painted flash sheet is. A
/// stream is a resource and carries no playback position, so sharing one between
/// several players is free; what must not be shared is the *clock*, and clocks
/// live on the vehicle.
///
/// The files come from <c>stage_sounds.sh</c>, which is also the only record of
/// where they came from - the bytes are deliberately not in this repository.
/// </summary>
public sealed class SoundSet
{
    /// <summary>
    /// What a tank is: one set per class, under Sounds/&lt;TAG&gt;/.
    ///
    /// Four of the seven are staged and deliberately not played, and the belt
    /// pair is the one worth the paragraph, because layering it over the running
    /// loop was tried and is worse than nothing.
    ///
    /// Two reasons, and the second cannot be tuned away. First, these are not
    /// events over a background: start/cycle/stop are one *sequence* the source
    /// game swapped between, all three recordings of the same belt, so laying the
    /// take-up over a loop that is already fading in plays the same material
    /// twice out of phase - which is heard as tearing rather than as a start.
    ///
    /// Second, the settle has nothing to overlap. It is a sound of motion
    /// running out, and it can only be triggered once the motion has gone, so it
    /// is late by construction. Measured on a one-cell order: braking carries the
    /// tank down to 72px/s and then arrival drops it to zero in a single frame,
    /// the gate crosses 50ms later, and a full second of settling begins on a
    /// tank that has been standing still the whole time. No threshold fixes that
    /// - there is no deceleration left at the end to put the sound under, and
    /// giving it one is a movement change, not an audio change.
    ///
    /// The engine pair is simpler: the bench has no state in which a tank's
    /// engine is off, so there is nothing for a starter or a run-down to attach
    /// to.
    /// </summary>
    public static readonly string[] ClassNames =
    {
        "engine_start", "engine_cycle", "engine_stop",
        "track_start", "track_cycle", "track_stop",
        "turret_cycle",
        "gun_shot",
    };

    /// <summary>
    /// What happens to a tank: one set for all of them, under Sounds/common/.
    ///
    /// The armour pair is why this bench went looking for recordings rather than
    /// a generator. A ricochet and a penetration are different *events*, not one
    /// event at two volumes, and the scar levels already say so in pixels - soot,
    /// gouge, hole. Two files make the calibre mean something in the ear as well,
    /// which is the same gap that existed in the sprites while calibre only
    /// scaled the burst.
    /// </summary>
    public static readonly string[] CommonNames =
    {
        "armour_ricochet1", "armour_ricochet2",
        "armour_hit1", "armour_hit2",
        "shell_hard1", "shell_hard2", "shell_hard3",
        "burn_cycle",
        "destroyed1", "destroyed2", "destroyed3",
    };

    /// <summary>
    /// The three that run until told to stop; everything else fires once.
    ///
    /// Named here rather than guessed from the length, because the distinction is
    /// about the event and not about the file: <c>gun_shot</c> at 3.4s is longer
    /// than <c>engine_cycle</c> at 3.1s and is still a one-shot.
    /// </summary>
    public static readonly string[] Looped =
    {
        "engine_cycle", "track_cycle", "turret_cycle", "burn_cycle",
    };

    private readonly Dictionary<string, AudioStreamWav> _streams = new();

    /// <summary>Which class this set belongs to, or "common" for the shared one.</summary>
    public string Tag { get; private set; } = "";

    /// <summary>Set when the directory itself is not there. Never fatal - see below.</summary>
    public string Error { get; private set; } = "";

    /// <summary>
    /// Named files that were not found or would not load, in order.
    ///
    /// Not an error, and the bench must run without them. A harness whose job is
    /// to judge sprites cannot be stopped by a missing sound, and `Sounds/` is
    /// gitignored, so a fresh checkout has none at all until stage_sounds.sh is
    /// run. Silence is a state this has to reach cleanly rather than crash into.
    /// </summary>
    public List<string> Missing { get; } = new();

    /// <summary>True when at least one stream loaded, i.e. there is anything to play.</summary>
    public bool Any => _streams.Count > 0;

    public int Count => _streams.Count;

    /// <summary>
    /// The stream under <paramref name="name"/>, or null if it did not load.
    ///
    /// Null rather than an exception on purpose: every caller is a player that
    /// can simply not play, and the alternative is a bench that dies over a file
    /// nobody was going to hear.
    /// </summary>
    public AudioStreamWav? this[string name] =>
        _streams.TryGetValue(name, out AudioStreamWav? s) ? s : null;

    public static SoundSet Load(string root, string tag) =>
        Load(root, tag, tag == CommonTag ? CommonNames : ClassNames);

    public const string CommonTag = "common";

    private static SoundSet Load(string root, string tag, string[] names)
    {
        var set = new SoundSet { Tag = tag };
        string dir = $"{root}/{tag}";
        if (!Directory.Exists(dir))
        {
            set.Error = $"no such directory {dir} - run stage_sounds.sh";
            foreach (string name in names)
                set.Missing.Add(name);
            return set;
        }

        foreach (string name in names)
        {
            string path = $"{dir}/{name}.wav";
            if (!File.Exists(path))
            {
                set.Missing.Add(name);
                continue;
            }

            // Load plain and set the loop afterwards. Asking the loader for it
            // does not work, and fails in the quiet direction - twice over:
            //
            //  * the options dictionary takes ResourceImporterWav's properties,
            //    and that importer's loop_mode enum has an extra value at 0
            //    ("Detect From WAV"), so its scale is offset by one from this
            //    property's. Measured: importer 1 -> Disabled, 2 -> Forward,
            //    3 -> PingPong. Passing the value that reads as Forward here
            //    asks for Disabled there.
            //  * and at *any* importer value loop_end comes back 0, so even the
            //    right number leaves the loop covering nothing.
            //
            // Either way the sound plays once and stops, which reads as an engine
            // that cuts out rather than as a loop that was never switched on.
            AudioStreamWav? stream = AudioStreamWav.LoadFromFile(path);
            if (stream is null)
            {
                // The one cause worth naming: Godot refuses non-PCM WAV outright
                // ("Format not supported for WAVE file (not PCM)"), and 36 of the
                // 114 engine files in the source set are MS-ADPCM.
                // stage_sounds.sh normalises everything through sox, so a failure
                // here means a file that did not come through it.
                set.Missing.Add(name);
                continue;
            }

            if (System.Array.IndexOf(Looped, name) >= 0)
            {
                stream.LoopMode = AudioStreamWav.LoopModeEnum.Forward;
                stream.LoopBegin = 0;
                // 16-bit mono throughout, which stage_sounds.sh guarantees, so
                // two bytes to the frame.
                stream.LoopEnd = stream.Data.Length / 2 - 1;
            }

            set._streams[name] = stream;
        }

        return set;
    }

    /// <summary>
    /// One line per set for the log: what loaded, and what did not.
    ///
    /// Printed rather than shown in the panel because the panel is a view on the
    /// bench's live state and this is a fact about start-up, and because a capture
    /// has no panel at all.
    /// </summary>
    /// <summary>
    /// The RMS of a loaded sample, 0..1, or -1 if it is not here.
    ///
    /// Nothing in the bench looked at what is *in* a sample until two bugs in a
    /// row turned out to be about exactly that - a take that could never be
    /// chosen, and a take 4.2dB under its partner that went missing behind the
    /// gun report. Both were invisible from inside the code, because from inside
    /// a stream that loaded is a stream that is fine.
    ///
    /// RMS rather than peak because it is what an ear weighs against a report
    /// playing over it, and because two takes of one event can share a peak and
    /// still be several decibels apart - hit1 and hit2 have crest factors of 6.9
    /// and 4.5.
    /// </summary>
    public double Rms(string name)
    {
        AudioStreamWav? stream = this[name];
        if (stream is null)
            return -1.0;
        byte[] data = stream.Data;
        if (data.Length < 2)
            return -1.0;
        // 16-bit signed little-endian mono - the one shape stage_sounds.sh
        // writes, and the loader would have refused anything else.
        double sum = 0.0;
        int samples = data.Length / 2;
        for (int i = 0; i < samples; i++)
        {
            double v = (short)(data[i * 2] | (data[i * 2 + 1] << 8)) / 32768.0;
            sum += v * v;
        }
        return Math.Sqrt(sum / samples);
    }

    /// <summary>
    /// How hard the join is on a looping sample: the step from its last sample
    /// back to its first, against the average step inside it. Below about one it
    /// is invisible; a click is tens.
    ///
    /// Worth measuring rather than trusting, because a loop plays end to start
    /// with no crossfade and nothing else in the bench looks at it. The
    /// synthesised traverse motor is the case that needed it - the first attempt
    /// measured 42, forty times a typical step, because 44100/108 is not a whole
    /// number of samples so the waveform never landed on a sample boundary. The
    /// recorded loops come in at 0.0 to 0.4 and the built ones at 1.1 to 2.5.
    ///
    /// -1 when the sample is not here.
    /// </summary>
    public double SeamRatio(string name)
    {
        AudioStreamWav? stream = this[name];
        if (stream is null)
            return -1.0;
        byte[] data = stream.Data;
        int samples = data.Length / 2;
        if (samples < 3)
            return -1.0;
        double Value(int i) =>
            (short)(data[i * 2] | (data[i * 2 + 1] << 8)) / 32768.0;
        double steps = 0.0;
        for (int i = 1; i < samples; i++)
            steps += Math.Abs(Value(i) - Value(i - 1));
        double mean = steps / samples;
        return mean <= 0.0 ? -1.0
            : Math.Abs(Value(0) - Value(samples - 1)) / mean;
    }

    public string Summary()
    {
        if (Error.Length > 0)
            return $"sound {Tag}: {Error}";
        string missing = Missing.Count == 0 ? "" : $", missing {string.Join(" ", Missing)}";
        return $"sound {Tag}: {_streams.Count} loaded{missing}";
    }
}
