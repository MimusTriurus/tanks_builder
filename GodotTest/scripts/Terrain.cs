using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// What a cell is made of: the floor it stands on.
///
/// <b>One slot, and the values are exclusive</b> - a cell is sand or it is
/// shallow water, never both. That is what separates this from the height,
/// which is a third axis and stays one: a sandy rise and a stony rise are two
/// different cells, and a foundation value spelled "hill" could not tell them
/// apart. Height is <see cref="HexField.LevelAt"/> and the ramp beside it; this
/// is the material.
///
/// <see cref="Void"/> is off the board - the cells outside
/// <see cref="HexField.Plot"/>. A value rather than a fourth grid, because
/// "nothing may drive here" is exactly what the other undrivable values say and
/// a reader that had to ask two questions would eventually ask one.
/// </summary>
public enum Foundation
{
    /// <summary>Ordinary ground - what a board that said nothing is made of.
    ///
    /// <b>First, so that it is the default of the type.</b> Every board hands
    /// its floors over as a fresh array and fills in only the cells it has
    /// something to say about, so whatever sits at zero is what the rest of the
    /// board is made of - and an enum with the void at zero would have made an
    /// unspoken cell one nothing may drive on.</summary>
    Solid,

    Sand,

    /// <summary>Water a tank fords.</summary>
    Shallow,

    /// <summary>Water a tank does not.</summary>
    Deep,

    /// <summary>Rock: a rise that is not meant to be driven.</summary>
    Rock,

    /// <summary>Off the board.</summary>
    Void,
}

/// <summary>
/// What stands on the cell.
///
/// <b>One slot, exclusive, and that is a decision rather than a limit of the
/// storage.</b> Two covers on one cell are two answers to "does this stop a
/// round" and "what happens when it burns", and the rule for adding them would
/// have to be invented rather than derived. A wooded rampart is one thing on the
/// board and wants its own name, not two names stacked.
///
/// Nothing here is art. <see cref="Forest"/> has trees drawn on it by
/// <see cref="Grove"/> and <see cref="Walls"/> has masonry laid by
/// <see cref="WallRig"/>, but <see cref="Trench"/> and <see cref="Minefield"/>
/// have neither and are covers in exactly the same way - which is the test that
/// this layer is rules and not a list of props.
/// </summary>
public enum Cover
{
    None,
    Forest,
    Trench,
    Minefield,
    Walls,
}

/// <summary>
/// What the cover is doing, or what has been done to it.
///
/// <b>A noun, never a clock.</b> Fire spreads, masonry falls and a trench fills
/// over time, and every one of those processes already has an owner
/// (<see cref="Wildfire"/>, <see cref="WallFall"/>). What the cell carries is
/// the state those processes leave behind, so that a reader asking "does this
/// stop a round" gets one answer and not a simulation.
///
/// <b>Three of them are spent</b> - <see cref="Burnt"/>, <see cref="Breached"/>
/// and <see cref="Cleared"/> - and a spent cover blocks nothing and screens
/// nothing while still being what it was: a burnt wood is a burnt wood on the
/// map and a hole in the line of fire, and those are the same fact said twice.
/// See <see cref="TerrainRules.Standing"/>.
///
/// <see cref="Flooded"/> is a trench with water in it, and it is here rather
/// than in <see cref="Foundation"/> on purpose: flooding a <i>cell</i> is a
/// change of floor - solid becomes shallow - and it goes through
/// <see cref="HexField.SetWater"/> as it always has. Flooding a trench leaves
/// the floor alone and changes what the trench is worth.
/// </summary>
public enum CoverState
{
    Intact,
    Burning,
    Burnt,
    Breached,
    Flooded,
    Cleared,
}

/// <summary>
/// A whole cell in one value: both slots, the cover's state, and the height axis
/// beside them.
///
/// <b>Here so that there is one read.</b> Before this every consumer asked the
/// field its own question - the tick asked <c>IsWater</c>, the grove asked
/// <c>KindAt</c>, the pathing asked levels and ramps, the walls were not asked
/// at all - and each of them remembered a different subset of what a cell is. A
/// struct is not a rule; what it buys is that a new rule has one place to read
/// from.
/// </summary>
public readonly struct HexFace
{
    public required Foundation Ground { get; init; }

    public required Cover Over { get; init; }

    public required CoverState State { get; init; }

    /// <summary>The height axis, carried alongside rather than folded in - see
    /// <see cref="Foundation"/>.</summary>
    public required int Level { get; init; }

    /// <summary>The ramp's heading, or -1 on flat ground.</summary>
    public required int Ramp { get; init; }

    public bool OnRamp => Ramp >= 0;

    /// <summary>Whether the floor is water. Off the foundation rather than a
    /// flag beside it, which is the whole point of the floor being one
    /// slot.</summary>
    public bool Wet => TerrainRules.Of(Ground).Wet;

    /// <summary>Whether the cover still stands - see
    /// <see cref="TerrainRules.Standing"/>.</summary>
    public bool Standing => TerrainRules.Standing(Over, State);

    public override string ToString() =>
        Ground.ToString().ToLowerInvariant()
        + (Over == Cover.None
            ? ""
            : "/" + Over.ToString().ToLowerInvariant()
              + (State == CoverState.Intact
                  ? "" : ":" + State.ToString().ToLowerInvariant()))
        + $" at {Level:+0;-0;0}" + (OnRamp ? $" ramp {Ramp}" : "");
}

/// <summary>How one foundation behaves. Four plain figures; see
/// <see cref="TerrainRules"/> for who reads them.</summary>
public sealed class GroundRule
{
    public bool Drive { get; set; } = true;

    /// <summary>
    /// Ceiling on the drive, as a fraction of the class's top speed. One means
    /// the floor costs nothing.
    ///
    /// <b>Water is one, and that is not an oversight.</b> Wading already has its
    /// own ceiling - <see cref="MovementProfile.WaterSpeed"/>, taken by
    /// <see cref="TankTick.SpeedCap"/> off <see cref="HexField.IsWet"/> - and a
    /// second figure here would be the same terrain counted twice.
    /// </summary>
    public float Speed { get; set; } = 1.0f;

    /// <summary>What the ground does to the ride, multiplying the figure
    /// <see cref="TerrainSet.RideOf"/> takes off the plate's own grain. One
    /// leaves the drawn ground in charge, which is where it has been.</summary>
    public float Ride { get; set; } = 1.0f;

    public bool Wet { get; set; }
}

/// <summary>How one cover behaves.</summary>
public sealed class CoverRule
{
    /// <summary>Whether it refuses to be driven through. Read by the pathing as
    /// a barred cell rather than by <see cref="HexField.Passable"/>, and that
    /// distinction is load-bearing - see
    /// <see cref="HexField.Barricades"/>.</summary>
    public bool Blocks { get; set; }

    /// <summary>Whether it stops a round crossing the cell.</summary>
    public bool Screens { get; set; }

    /// <summary>Ceiling on the drive through it, as a fraction of top
    /// speed.</summary>
    public float Speed { get; set; } = 1.0f;

    /// <summary>Whether fire takes hold in it.</summary>
    public bool Burns { get; set; }

    /// <summary>Whether the enemy is meant not to see it. Carried and read by
    /// nothing yet: who knows what about a cell is a third axis rather than a
    /// state, and it is named here so that the minefield does not quietly
    /// acquire one.</summary>
    public bool Hidden { get; set; }

    /// <summary>The states this cover may be in. Declared rather than shared,
    /// because a breached wood and a burning wall are both nonsense and the
    /// place to say so is the table, not a switch in the caller.</summary>
    public CoverState[] States { get; set; } = { CoverState.Intact };
}

/// <summary>
/// The two tables: what each foundation and each cover is worth.
///
/// <b>The file fills in a table the code declares; it does not declare the
/// table</b> - <see cref="WallConfig"/>'s doctrine, and it fails the same way if
/// it is broken. Which foundations and covers exist is the two enums, because a
/// value with no rule behind it is a cell nothing can answer for; what
/// <c>terrain.json</c> supplies is the numbers, which are the part that gets
/// argued with.
///
/// Static and lazily loaded rather than handed down through constructors: this
/// is a read-only lookup wanted in the pathing, the gunnery, the tick and the
/// self test, and threading a reference through those would be four parameters
/// for a table with no state in it. The reason <see cref="Gunnery"/> is static.
///
/// <b>Today's shipped boards read exactly as they did before this file
/// existed</b>, on purpose: every value a shipped board carries has speed one
/// and ride one, so the mechanism is wired and measurable while the tuning is
/// still ahead. <see cref="Foundation.Sand"/> carries the only figure that is
/// not neutral and no map authors sand yet - which is what makes it provable on
/// a made-up board and invisible on a real one.
/// </summary>
public static class TerrainRules
{
    public const string FileName = "terrain.json";

    /// <summary>Whether a file was found and parsed. False means every rule is
    /// running on the figures compiled into it - which is a usable board, so
    /// this is printed and not thrown.</summary>
    public static bool Loaded { get; private set; }

    /// <summary>Why not, when not.</summary>
    public static string Error { get; private set; } = "";

    private static readonly Dictionary<Foundation, GroundRule> Floors = new()
    {
        [Foundation.Void] = new GroundRule { Drive = false },
        [Foundation.Solid] = new GroundRule(),
        [Foundation.Sand] = new GroundRule { Speed = 0.75f, Ride = 1.15f },
        [Foundation.Shallow] = new GroundRule { Wet = true },
        [Foundation.Deep] = new GroundRule { Drive = false, Wet = true },
        [Foundation.Rock] = new GroundRule { Drive = false },
    };

    private static readonly Dictionary<Cover, CoverRule> Overs = new()
    {
        [Cover.None] = new CoverRule(),
        [Cover.Forest] = new CoverRule
        {
            Burns = true,
            States = new[]
            {
                CoverState.Intact, CoverState.Burning, CoverState.Burnt,
            },
        },
        [Cover.Trench] = new CoverRule
        {
            States = new[]
            {
                CoverState.Intact, CoverState.Flooded, CoverState.Cleared,
            },
        },
        [Cover.Minefield] = new CoverRule
        {
            Hidden = true,
            States = new[] { CoverState.Intact, CoverState.Cleared },
        },
        [Cover.Walls] = new CoverRule
        {
            Blocks = true,
            Screens = true,
            States = new[] { CoverState.Intact, CoverState.Breached },
        },
    };

    /// <summary>The states that mean the cover is no longer there. One list
    /// rather than a flag on each state, because "is it still standing" is the
    /// question every reader asks and these are the answer to it.</summary>
    private static readonly CoverState[] Spent =
    {
        CoverState.Burnt, CoverState.Breached, CoverState.Cleared,
    };

    public static GroundRule Of(Foundation ground)
    {
        Ready();
        return Floors[ground];
    }

    public static CoverRule Of(Cover over)
    {
        Ready();
        return Overs[over];
    }

    /// <summary>Whether the table has a rule for this value at all. Here so the
    /// self test can hold the two enums and the two tables against each other
    /// without a lookup that throws being the way it finds out.</summary>
    public static bool Knows(Foundation ground)
    {
        Ready();
        return Floors.ContainsKey(ground);
    }

    public static bool Knows(Cover over)
    {
        Ready();
        return Overs.ContainsKey(over);
    }

    /// <summary>Whether the cover is still on the cell in the way that matters:
    /// a standing cover blocks and screens, a spent one does neither. A burning
    /// wood is standing - it is still trees, and on fire - and a burnt one is
    /// not.</summary>
    public static bool Standing(Cover over, CoverState state) =>
        over != Cover.None && Array.IndexOf(Spent, state) < 0;

    /// <summary>Whether this cover is allowed to be in this state - the table's
    /// own <see cref="CoverRule.States"/>, asked rather than assumed. A cover
    /// that cannot be in a state is not a cover that quietly is.</summary>
    public static bool Allows(Cover over, CoverState state) =>
        over == Cover.None
            ? state == CoverState.Intact
            : Array.IndexOf(Of(over).States, state) >= 0;

    /// <summary>Whether anything may drive on to this floor at all.</summary>
    public static bool Drivable(Foundation ground) => Of(ground).Drive;

    /// <summary>Whether the cover refuses to be driven through.</summary>
    public static bool Blocks(Cover over, CoverState state) =>
        Standing(over, state) && Of(over).Blocks;

    /// <summary>Whether the cover stops a round crossing the cell.</summary>
    public static bool Screens(Cover over, CoverState state) =>
        Standing(over, state) && Of(over).Screens;

    /// <summary>
    /// What the cell does to the drive, as a fraction of top speed.
    ///
    /// <b>The lower of the two, never their product</b> - the rule
    /// <see cref="TankTick.SpeedCap"/> already holds over the grade and the
    /// water, and for its reason: a floor and what stands on it are two
    /// terrains, and multiplying them invents a third that nothing on the board
    /// is made of.
    /// </summary>
    public static float Speed(HexFace face) =>
        Math.Min(Of(face.Ground).Speed,
                 face.Standing ? Of(face.Over).Speed : 1.0f);

    /// <summary>What the cell does to the ride, multiplying the plate's grain.
    /// The cover has no say: the ride is the ground under the tracks.</summary>
    public static float Ride(HexFace face) => Of(face.Ground).Ride;

    /// <summary>The cell in one word, for a trace or a panel - or nothing at all
    /// where it costs nothing. Beside the numbers for
    /// <see cref="TankTick.SpeedCapWhy"/>'s reason: a cap and the reason for it
    /// are one answer.</summary>
    public static string Why(HexFace face)
    {
        if (face.Standing && Of(face.Over).Speed < Of(face.Ground).Speed)
            return face.Over.ToString().ToLowerInvariant();
        return Of(face.Ground).Speed < 1.0f
            ? face.Ground.ToString().ToLowerInvariant() : "";
    }

    /// <summary>Every cover there is, in the enum's own order. Here so the self
    /// test can walk the table rather than list it.</summary>
    public static IEnumerable<Cover> Covers => Enum.GetValues<Cover>();

    public static IEnumerable<Foundation> Grounds =>
        Enum.GetValues<Foundation>();

    // --- the file ----------------------------------------------------------

    private static bool _read;

    /// <summary>
    /// Load the file once, on the first question anyone asks. Idempotent and
    /// safe to call from anywhere, which is what lets the readers stay free of
    /// plumbing; naming a path reloads, which is what the self test wants.
    ///
    /// Read from an absolute path rather than imported as a resource, the reason
    /// the atlases and <c>panel.json</c> are: edit, restart, see it, nothing to
    /// rebuild.
    /// </summary>
    public static void Ready(string? path = null)
    {
        if (_read && path is null)
            return;
        _read = true;
        Loaded = false;
        Error = "";
        try
        {
            path ??= Path.Combine(ProjectSettings.GlobalizePath("res://"),
                                  FileName);
            if (!File.Exists(path))
            {
                Error = $"no {Path.GetFileName(path)} beside the project";
                return;
            }
            var file = JsonSerializer.Deserialize<FileShape>(
                File.ReadAllText(path),
                new JsonSerializerOptions
                {
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    PropertyNameCaseInsensitive = true,
                });
            if (file is null)
            {
                Error = "nothing in the file";
                return;
            }
            var wrong = new List<string>();
            if (file.Foundation is not null)
                foreach (KeyValuePair<string, GroundEntry> entry in
                         file.Foundation)
                {
                    if (!Enum.TryParse(entry.Key, true, out Foundation which))
                    {
                        wrong.Add($"no foundation called {entry.Key}");
                        continue;
                    }
                    Apply(Floors[which], entry.Value);
                }
            if (file.Cover is not null)
                foreach (KeyValuePair<string, CoverEntry> entry in file.Cover)
                {
                    if (!Enum.TryParse(entry.Key, true, out Cover which))
                    {
                        wrong.Add($"no cover called {entry.Key}");
                        continue;
                    }
                    Apply(Overs[which], entry.Value, which, wrong);
                }
            Error = string.Join("; ", wrong);
            Loaded = true;
        }
        catch (Exception e)
        {
            Error = e.Message;
        }
    }

    /// <summary>A figure that is absent or out of range is silence rather than
    /// an instruction - <see cref="WallConfig.Apply"/>'s rule. A floor of no
    /// speed is not a slower floor, it is a floor nothing crosses, and a file is
    /// not the place to discover that from.</summary>
    private static void Apply(GroundRule rule, GroundEntry entry)
    {
        if (entry.Drive is bool drive)
            rule.Drive = drive;
        if (entry.Speed is float speed and > 0.0f and <= 1.0f)
            rule.Speed = speed;
        if (entry.Ride is float ride and >= 0.5f and <= 2.0f)
            rule.Ride = ride;
        if (entry.Wet is bool wet)
            rule.Wet = wet;
    }

    private static void Apply(CoverRule rule, CoverEntry entry, Cover which,
                              List<string> wrong)
    {
        if (entry.Blocks is bool blocks)
            rule.Blocks = blocks;
        if (entry.Screens is bool screens)
            rule.Screens = screens;
        if (entry.Speed is float speed and > 0.0f and <= 1.0f)
            rule.Speed = speed;
        if (entry.Burns is bool burns)
            rule.Burns = burns;
        if (entry.Hidden is bool hidden)
            rule.Hidden = hidden;
        if (entry.States is null)
            return;
        var states = new List<CoverState>();
        foreach (string name in entry.States)
            if (Enum.TryParse(name, true, out CoverState state))
                states.Add(state);
            else
                wrong.Add($"{which}: no state called {name}");
        // Intact is not optional: a cover that cannot be intact is one that is
        // broken the moment it is laid, and a file saying so has said something
        // about the world rather than about the numbers.
        if (states.Count == 0)
            return;
        if (states.Contains(CoverState.Intact))
            rule.States = states.Distinct().ToArray();
        else
            wrong.Add($"{which}: a cover has to be able to stand intact");
    }

    private sealed class FileShape
    {
        [JsonPropertyName("foundation")]
        public Dictionary<string, GroundEntry>? Foundation { get; set; }

        [JsonPropertyName("cover")]
        public Dictionary<string, CoverEntry>? Cover { get; set; }
    }

    private sealed class GroundEntry
    {
        public bool? Drive { get; set; }
        public float? Speed { get; set; }
        public float? Ride { get; set; }
        public bool? Wet { get; set; }
    }

    private sealed class CoverEntry
    {
        public bool? Blocks { get; set; }
        public bool? Screens { get; set; }
        public float? Speed { get; set; }
        public bool? Burns { get; set; }
        public bool? Hidden { get; set; }
        public string[]? States { get; set; }
    }
}
