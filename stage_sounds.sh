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
    local from="$SRC/$1" to="$OUT/$2"
    if [ ! -f "$from" ]; then
        echo "  MISSING  $1"
        skipped=$((skipped + 1))
        return
    fi
    mkdir -p "$(dirname "$to")"
    # -c1 mono, -r44100, -b16 signed PCM: one shape for every file in the set
    if sox "$from" -c 1 -r 44100 -b 16 -e signed-integer "$to" 2>/dev/null; then
        printf "  %-34s <- %s\n" "$2" "$1"
        made=$((made + 1))
    else
        echo "  FAILED   $1"
        skipped=$((skipped + 1))
    fi
}

# --- per class: engine, belt, gun -----------------------------------------
# tag  engine-dir  belt-size  gun
while read -r tag engine belt gun; do
    [ -z "$tag" ] && continue
    echo "== $tag"
    take "Units/Ground/$engine/start.wav"                  "$tag/engine_start.wav"
    take "Units/Ground/$engine/cycle.wav"                  "$tag/engine_cycle.wav"
    take "Units/Ground/$engine/stop.wav"                   "$tag/engine_stop.wav"
    take "Units/Ground/Caterpillar/$belt/cycle1.wav"       "$tag/track_cycle.wav"
    take "Units/Ground/Caterpillar/$belt/start1.wav"       "$tag/track_start.wav"
    take "Units/Ground/Caterpillar/$belt/stop1.wav"        "$tag/track_stop.wav"
    take "Weapons/Cannons/Shots/$gun.wav"                  "$tag/gun_shot.wav"
done <<'CLASSES'
LTP tank9 Small  130mm
MTP tank2 Medium 450mm
HTP tank3 Big    300mm
CLASSES

# --- shared: what happens *to* a tank rather than what it is ---------------
# The armour pair is the one this bench specifically needed and free packs do not
# have: a ricochet and a penetration recorded separately, so the scar levels can
# differ in kind and not only in volume.
echo "== common"
take "Hit/armorrico1.wav"                    "common/armour_ricochet1.wav"
take "Hit/armorrico2.wav"                    "common/armour_ricochet2.wav"
take "Hit/armorhit1.wav"                     "common/armour_hit1.wav"
take "Hit/armorhit2.wav"                     "common/armour_hit2.wav"
take "Weapons/Cannons/Hits/Hard/hit1.wav"    "common/shell_hard1.wav"
take "Weapons/Cannons/Hits/Hard/hit2.wav"    "common/shell_hard2.wav"
take "Weapons/Cannons/Hits/Hard/hit3.wav"    "common/shell_hard3.wav"
# 15.6s of a burning tank - long enough that the loop is not the thing you hear
take "Hit/tankfire.wav"                      "common/burn_cycle.wav"
take "Explosion/tankexplosion01.wav"         "common/destroyed1.wav"
take "Explosion/tankexplosion02.wav"         "common/destroyed2.wav"
take "Explosion/tankexplosion03.wav"         "common/destroyed3.wav"

echo
echo "$made written, $skipped skipped, into $OUT"
