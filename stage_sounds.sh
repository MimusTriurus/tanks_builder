#!/usr/bin/env bash
#
# Stage the bench's sound set from a Blitzkrieg 2 checkout.
#
# The bytes are not in this repository and must not be: Nival's licence for the
# Blitzkrieg 2 release is non-commercial, revocable on three days' notice, and
# carries a penalty clause. So this script is the record instead - it says where
# every file came from and what was done to it, and `Sounds/` is gitignored.
#
# That makes these prototype pixels' equivalent: material to work out *how* the
# audio should behave - which events, what lengths, what has to be separable -
# on real recordings rather than on placeholders. Once the mechanism is right,
# swapping in a licensed library is a re-run of this table.
#
# Why sox: 36 of the 114 files under Units/Ground are MS-ADPCM, and Godot refuses
# those outright - "Format not supported for WAVE file (not PCM)". Everything is
# normalised to PCM signed 16-bit 44.1kHz mono so the loader never has to care.
#
# The per-class picks are *measured*, not chosen by ear, because a class ladder
# wants a real spread and there are fourteen engines to choose from. `sox stat`
# rough frequency across all fourteen runs 429Hz to 2632Hz; the three below are
# 429 / 605 / 972, which is roughly 1 : 1.4 : 2.3 with every loop over 3s.
# The same argument as MovementProfile.Size, and the same caveat: rough frequency
# is a crude estimate and engine character is not one number, so these are a
# starting point to be corrected by ear, not an answer.
#
# The gun ladder is measured the same way and does *not* follow the file names:
# 130mm is 826Hz, 450mm is 394Hz, 300mm is 231Hz. Ordered by what it sounds like.
#
# Usage:  ./stage_sounds.sh [path-to-Blitzkrieg-2] [output-dir]

set -u

SRC="${1:-D:/Projects/C++/Blitzkrieg-2}/Complete/Sounds"
OUT="${2:-$(cd "$(dirname "$0")" && pwd)/Sounds}"

command -v sox >/dev/null || { echo "sox not on PATH - needed to decode MS-ADPCM"; exit 1; }
[ -d "$SRC" ] || { echo "no such source: $SRC"; exit 1; }

made=0
skipped=0

# take <relative-source> <destination-under-OUT>
take() {
    # ${3:-} rather than $3 - the script runs under `set -u` and most calls pass
    # no level, meaning "leave it as the source had it".
    local from="$SRC/$1" to="$OUT/$2" level="${3:-}"
    if [ ! -f "$from" ]; then
        echo "  MISSING  $1"
        skipped=$((skipped + 1))
        return
    fi
    mkdir -p "$(dirname "$to")"
    # -c1 mono, -r44100, -b16 signed PCM: one shape for every file in the set
    if sox "$from" -c 1 -r 44100 -b 16 -e signed-integer "$to" 2>/dev/null; then
        [ -n "$level" ] && match_rms "$to" "$level"
        printf "  %-34s <- %s\n" "$2" "$1"
        made=$((made + 1))
    else
        echo "  FAILED   $1"
        skipped=$((skipped + 1))
    fi
}

# Bring a file to a target RMS.
#
# The set arrived at whatever level each source happened to sit at, and the
# spread is 15dB: the three gun reports are peak-normalised to full scale
# (rms 0.41-0.47) while the impacts came in at 0.09-0.18. That makes the mixer's
# dB constants trims on top of an uncontrolled mix rather than the mix itself.
#
# Only the pairs are matched here, and the reason is narrow enough to be worth
# stating: two takes of one event exist so that two shells do not sound
# identical, which means they are meant to differ in *character*. The ricochet
# pair differed by 4.2dB in level as well, and against a gun report starting on
# the very same frame that is the difference between a sound and no sound - it
# was reported as the ricochet firing every other shot, which is exactly what it
# was. The quiet take was the one that went missing.
#
# RMS and not peak: hit1 and hit2 have crest factors of 6.9 and 4.5, so peak
# normalisation would leave them 3.8dB apart in the thing an ear actually hears.
# 0.14 is the highest target that clears full scale on the peakiest of the four.
match_rms() {
    local file="$1" want="$2" have gain
    have=$(sox "$file" -n stat 2>&1 | awk -F: '/RMS *amplitude/{print $2+0}')
    [ -z "$have" ] && return
    gain=$(awk -v h="$have" -v w="$want" 'BEGIN{printf "%.3f", 20*log(w/h)/log(10)}')
    # -t wav on the scratch file: sox takes the format from the extension and
    # does not know ".lvl", so without it this fails and leaves the level alone -
    # silently, because the levels it would have written are not checked anywhere
    # except by ear.
    sox "$file" -t wav -c 1 -r 44100 -b 16 -e signed-integer "$file.lvl" \
        gain "$gain" && mv "$file.lvl" "$file"
}

# One level for every take of one event - see match_rms.
IMPACT_RMS=0.14

# Make a recorded loop meet itself, by folding its tail over its head.
#
# A loop plays end to start with no crossfade, so the two ends have to join. Five
# of the six recorded loops already did, by luck rather than by anyone's
# intention - and the sixth did not: the heavy's engine joined with a step six
# times a typical one, which is a click once a lap on a sound that never stops.
# Found by measurement, not by ear, and only because the traverse motor had to be
# built and the seam therefore had to be looked at.
#
# The fold: the last x seconds, fading out, are mixed over the first x, fading
# in, and the tail is then dropped. The new file starts on what used to be the
# sample at L-x, which is also where it now ends - so the join is a join the
# recording already contained. Costs x of length, which for a three second loop
# is one part in seventy-five.
#
# Applied to the recorded loops only. The synthesised one closes by arithmetic
# (see synth_turret) and folding it would blur the exact phase that arithmetic
# bought.
# Two things had to be right and the first attempt had neither, both silently:
#
#  * **samples, not seconds.** `sox stat` reports the length rounded, and a
#    middle cut to a rounded length misses the join by tens of samples. The fold
#    then lands on unrelated material and the seam comes out *worse* than it
#    started - measured 0.0 to 12.7 on the light's engine, which had been perfect.
#  * **`sox -m` halves.** Mixing two files without `-v 1` attenuates each by half,
#    so the whole crossfade played at -6dB: a dip with a step at each end of it,
#    which is two seams where there was one. Measured on the tail: rms 0.076
#    mixed against 0.153 at unity.
#
# With both fixed the heavy's engine joins at 1.5 typical steps, down from 6.0,
# and peaks at 0.72 so the unity-gain sum does not clip.
close_loop() {
    local file="$1" x=1764 tmp="$OUT/.loop" n
    mkdir -p "$tmp"
    n=$(sox --i -s "$file")
    # Nothing sensible to fold on something barely longer than the fold.
    [ "$n" -gt $((4 * x)) ] || { rm -rf "$tmp"; return; }
    sox "$file" "$tmp/tail.wav" trim -${x}s fade t 0 ${x}s ${x}s
    sox "$file" "$tmp/head.wav" trim 0 ${x}s fade t ${x}s
    sox -m -v 1 "$tmp/tail.wav" -v 1 "$tmp/head.wav" "$tmp/join.wav"
    sox "$file" "$tmp/mid.wav" trim ${x}s $((n - 2 * x))s
    sox "$tmp/join.wav" "$tmp/mid.wav" -t wav -c 1 -r 44100 -b 16 \
        -e signed-integer "$file.loop" && mv "$file.loop" "$file"
    rm -rf "$tmp"
}

# Build a turret traverse motor, because there is not one to take.
#
# The source has 4182 wav files and no turret rotation among them: that game's
# vehicle audio is engine plus track and nothing else. So this one is synthesised
# rather than lifted, which makes it the only file in the set with no licence
# question attached - and the only one that can be tuned by changing a number
# here instead of by going to look for another recording.
#
# A traverse motor is a hum with a whine over it, so: a sawtooth fundamental, its
# octave, and a thin ninth harmonic for the gear, lowpassed and given a slow
# tremolo to keep it from reading as a test tone.
#
# **Seamlessness is the whole difficulty and it is arithmetic, not taste.** The
# loop plays end to start with no crossfade, so the two ends have to meet. Two
# things had to be got right, and the first attempt got neither:
#
#  * the period must be a whole number of samples. 3s of 108Hz is 324 whole
#    periods and still does not loop, because 44100/108 is 408.33 samples - the
#    waveform never lands on a sample boundary. 105Hz is 420 samples exactly, and
#    132300 samples is 315 of them.
#  * the slice must come out of the middle of an already-filtered signal. A
#    sawtooth's reset is a discontinuity by nature; the lowpass smears it over a
#    dozen samples, and a loop cut at the reset jumps into the middle of that
#    smear. Filtering six seconds and trimming one second in leaves the filter
#    settled at both ends of the slice, and one second is 105 whole periods, so
#    the phase still lines up.
#
# Measured on the result: the step across the seam is 0.019 against a typical
# sample-to-sample step of 0.011 and a worst normal step of 0.211. The first
# attempt measured 0.494 there - forty times a typical step, which is a click.
synth_turret() {
    local tag="$1" f="$2" to="$OUT/$tag/turret_cycle.wav"
    local tmp="$OUT/.synth"
    mkdir -p "$(dirname "$to")" "$tmp"
    sox -n -c 1 -r 44100 -b 16 "$tmp/a.wav" synth 6 sawtooth "$f"      gain -8
    sox -n -c 1 -r 44100 -b 16 "$tmp/b.wav" synth 6 sine $((f * 2))    gain -16
    sox -n -c 1 -r 44100 -b 16 "$tmp/c.wav" synth 6 sine $((f * 9))    gain -22
    sox -m "$tmp/a.wav" "$tmp/b.wav" "$tmp/c.wav" "$tmp/m.wav"
    sox "$tmp/m.wav" "$tmp/f.wav" lowpass 3200 tremolo 6 14
    # 1s in, 3s long: both boundaries at phase zero for every partial and for the
    # tremolo, with the filter settled either side.
    sox "$tmp/f.wav" -t wav -c 1 -r 44100 -b 16 -e signed-integer "$to" \
        trim 44100s 132300s gain -n -6
    rm -rf "$tmp"
    printf "  %-34s <- synthesised, %sHz\n" "$tag/turret_cycle.wav" "$f"
    made=$((made + 1))
}

# --- per class: engine, belt, gun -----------------------------------------
# tag  engine-dir  belt-size  gun  turret-motor-Hz
while read -r tag engine belt gun turret; do
    [ -z "$tag" ] && continue
    echo "== $tag"
    take "Units/Ground/$engine/start.wav"                  "$tag/engine_start.wav"
    take "Units/Ground/$engine/cycle.wav"                  "$tag/engine_cycle.wav"
    close_loop "$OUT/$tag/engine_cycle.wav"
    take "Units/Ground/$engine/stop.wav"                   "$tag/engine_stop.wav"
    take "Units/Ground/Caterpillar/$belt/cycle1.wav"       "$tag/track_cycle.wav"
    close_loop "$OUT/$tag/track_cycle.wav"
    take "Units/Ground/Caterpillar/$belt/start1.wav"       "$tag/track_start.wav"
    take "Units/Ground/Caterpillar/$belt/stop1.wav"        "$tag/track_stop.wav"
    take "Weapons/Cannons/Shots/$gun.wav"                  "$tag/gun_shot.wav"
    # Not taken - built. See synth_turret. Ordered the way the class figures are,
    # the heavy lowest: a bigger ring is a slower, deeper drive, and the three
    # frequencies are 350, 420 and 525 samples per period so all of them close
    # their loop exactly.
    synth_turret "$tag" "$turret"
done <<'CLASSES'
LTP tank9 Small  130mm 126
MTP tank2 Medium 450mm 105
HTP tank3 Big    300mm  84
CLASSES

# --- shared: what happens *to* a tank rather than what it is ---------------
# The armour pair is the one this bench specifically needed and free packs do not
# have: a ricochet and a penetration recorded separately, so the scar levels can
# differ in kind and not only in volume.
echo "== common"
take "Hit/armorrico1.wav"                    "common/armour_ricochet1.wav" $IMPACT_RMS
take "Hit/armorrico2.wav"                    "common/armour_ricochet2.wav" $IMPACT_RMS
take "Hit/armorhit1.wav"                     "common/armour_hit1.wav"      $IMPACT_RMS
take "Hit/armorhit2.wav"                     "common/armour_hit2.wav"      $IMPACT_RMS
take "Weapons/Cannons/Hits/Hard/hit1.wav"    "common/shell_hard1.wav"
take "Weapons/Cannons/Hits/Hard/hit2.wav"    "common/shell_hard2.wav"
take "Weapons/Cannons/Hits/Hard/hit3.wav"    "common/shell_hard3.wav"
# 15.6s of a burning tank - long enough that the loop is not the thing you hear
take "Hit/tankfire.wav"                      "common/burn_cycle.wav"
close_loop "$OUT/common/burn_cycle.wav"
take "Explosion/tankexplosion01.wav"         "common/destroyed1.wav"
take "Explosion/tankexplosion02.wav"         "common/destroyed2.wav"
take "Explosion/tankexplosion03.wav"         "common/destroyed3.wav"

echo
echo "$made written, $skipped skipped, into $OUT"
