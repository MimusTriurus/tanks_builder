using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// Which 3D tank stands on which home of a board, and how big and which way:
/// the one answer the match (<c>Tank3DBench.Match</c>) plays and the editor
/// shows on its homes (<c>MapEditor.Tanks</c>), so the editor draws the board
/// the match will open on.
///
/// <b>A shared module, not either root's.</b> Both roots read it; neither knows
/// the other (godot/CLAUDE.md, «Корни сцен»).
/// </summary>
public static class Lineup
{
    /// <summary>
    /// Which class each model drives as, and what the panel calls it.
    ///
    /// <b>Written down, because the sidecar does not say it</b> (see
    /// docs/tank3d.md). The class gives the tank its speed and its size - no
    /// sprite set is read for either.
    /// </summary>
    public static readonly (string Model, string Class, string Name)[] Pairs =
    {
        ("LTR", "LTP", "лёгкий"),
        ("MTR", "MTP", "средний"),
        ("HTR", "HTP", "тяжёлый"),
        ("TDR", "TDP", "ПТ-САУ"),
        ("HMR", "HMP", "мортира"),
    };

    /// <summary>Each side's five, in its homes' order.</summary>
    public static readonly string[] Roster = { "LTR", "MTR", "HTR", "TDR", "HMR" };

    public static int PairAt(string model) =>
        Array.FindIndex(Pairs, p => string.Equals(p.Model, model, StringComparison.OrdinalIgnoreCase));

    /// <summary>The pair's class, or the medium's for a model not in the table:
    /// the medium is the class every other figure is read against.</summary>
    public static MovementProfile ClassFor(string model)
    {
        string want = PairAt(model) is int i and >= 0 ? Pairs[i].Class : "MTP";
        MovementProfile? medium = null;
        foreach (MovementProfile p in MovementProfile.All)
        {
            if (string.Equals(p.Tag, want, StringComparison.OrdinalIgnoreCase))
                return p;
            if (p.Tag == "MTP")
                medium = p;
        }
        return medium ?? MovementProfile.Light;
    }

    /// <summary>
    /// How long the medium's hull is against the hex it stands on.
    ///
    /// <b>The sprite bench's own proportion, taken once and then left to the
    /// hex</b>: MTP draws its 0.883-unit hull at 167.7 px a unit, 148 px on a
    /// 248 px hex - 0.60. Every model's hull is brought to this, then scaled by
    /// its class's <see cref="MovementProfile.Size"/>, which is how the 2D
    /// benches size theirs (<see cref="Fleet.Resize"/>): the generator makes
    /// every tank about one unit long, so its own length says nothing about
    /// which is the heavy.
    /// </summary>
    public const float HullOfHex = 0.60f;

    /// <summary>Model units to world px for a tank of <paramref name="profile"/>
    /// on a hex <paramref name="hexWidth"/> px across.</summary>
    public static float PixelsFor(TankModel model, MovementProfile profile, float hexWidth) =>
        HullOfHex * hexWidth * (float)profile.Size / Mathf.Max(model.HullLength, 1e-3f);

    /// <summary>One unit of a match as the board's homes place it: which home
    /// (<see cref="Slot"/>), its side, model and the edge it faces.</summary>
    public readonly record struct Unit(int Slot, Vector2I Cell, string Side, string Model, int Facing);

    /// <summary>A side's way across the board when its home says none: green
    /// looks up the board, brown down it.</summary>
    public static int Across(string side) => side == Parking.Brown ? 270 : 90;

    /// <summary>
    /// The match's units, one on every home, in the homes' order.
    ///
    /// <b>The homes say it, and silence is the old split.</b> A home's side is
    /// its own or its order's (<see cref="Parking.SidesOf"/>); a side fields as
    /// many as it has homes (2026-10-26; it was five). A home naming a class
    /// gets that class's model, however many others on its side named it too;
    /// the rest take what the side has not got, in <see cref="Roster"/>'s
    /// order, and the roster round again past five. A home's facing is its own, else
    /// <see cref="Across"/>. A board that names none of it lines up as the
    /// drill always did.
    /// </summary>
    public static List<Unit> Of(IReadOnlyList<Parking> homes)
    {
        string[] sides = Parking.SidesOf(homes);
        int count = homes.Count;
        var models = new string?[count];
        // the classes asked for first - as many of one as the homes ask - then
        // the rest: what the side has not got yet in the roster's order, and
        // past all five the roster round again
        for (int i = 0; i < count; i++)
        {
            if (homes[i].Class is not { Length: > 0 } wanted)
                continue;
            int p = Array.FindIndex(Pairs, x => string.Equals(x.Class, wanted, StringComparison.OrdinalIgnoreCase));
            if (p >= 0)
                models[i] = Pairs[p].Model;
        }
        int[] round = new int[2];
        for (int i = 0; i < count; i++)
            if (models[i] is null)
                models[i] = Roster.FirstOrDefault(m => !Taken(models, sides, sides[i], m))
                            ?? Roster[round[sides[i] == Parking.Green ? 0 : 1]++ % Roster.Length];
        var units = new List<Unit>();
        for (int i = 0; i < count; i++)
            units.Add(new Unit(i, homes[i].Cell, sides[i], models[i]!, homes[i].Facing ?? Across(sides[i])));
        return units;
    }

    private static bool Taken(string?[] models, string[] sides, string side, string model)
    {
        for (int i = 0; i < models.Length; i++)
            if (sides[i] == side && string.Equals(models[i], model, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>A rig's yaw, degrees, for a tank on <paramref name="cell"/>
    /// facing the edge <paramref name="edge"/>: towards the next cell's middle
    /// on the ground (x the flat x, z the flat y unsquashed), the 3D benches'
    /// frame - the model's front is +Z.</summary>
    public static float Yaw(HexField field, Vector2I cell, int edge, float squash)
    {
        Vector2 d = field.FlatAnchor(HexField.Step(cell, edge)) - field.FlatAnchor(cell);
        return Mathf.RadToDeg(Mathf.Atan2(d.X, d.Y / Mathf.Max(squash, 1e-4f)));
    }

    /// <summary>Paint a model its side (<c>side_brown</c>, <see cref="Toon"/>).</summary>
    public static void Paint(TankModel model, string side)
    {
        foreach (ShaderMaterial m in model.Cel)
            m.Param("side_brown", side == Parking.Brown ? 1.0f : 0.0f);
    }
}
