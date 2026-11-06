using System;

namespace TankSpriteTest;

/// <summary>
/// A doctrine as the AI's character (docs/ai-plan.md, Р-3, И4; GDD meta.md,
/// «Доктрины»: «Бронегруппа идёт в лоб, Разведбат избегает лба, Артдивизион
/// держит дистанцию 3–5»). Not orders: weights over <see cref="AiEval"/> - the
/// search still takes material where it finds it, the style tilts what is
/// left. <see cref="Plain"/> is the score as it was before doctrines.
/// Measured 2026-11-05 against the plain AI, Fighter against Fighter, 36
/// matches a doctrine (drill10, field10, stress10; either side, either first
/// move): every tilt costs matches, so the weights are small - pulled on
/// to the enemy, a side walks into the other's first shot (armour at pull
/// 1.6 lost 5:27, at 1.15 still 12:19). docs/ai-plan.md, И4.
/// </summary>
/// <param name="Pull">The pull toward the enemy, times the plain one.</param>
/// <param name="Reach">Cells added to the distance a gun wants from the
/// nearest enemy (the mortar keeps its band).</param>
/// <param name="Fear">The enemy's next activation's threat, times the plain weight.</param>
/// <param name="Front">Each own active unit on the axis an enemy gun faces,
/// within six cells: a penalty - its lines of fire and ambushes are along it.</param>
/// <param name="Watch">Each own unit watching in ambush: a bonus.</param>
/// <param name="Huddle">Two own hulls side by side, times the plain penalty.</param>
/// <param name="Spread">The enemy's other units' threats (all but its best
/// activation's), times the plain weight (<c>AiEval.Others</c>, the bound
/// from cover to cover).</param>
public sealed record AiStyle(string Id, double Pull, int Reach, double Fear, double Front, double Watch, double Huddle,
                             double Spread = 1.0, AiRoles Roles = AiRoles.None)
{
    public static readonly AiStyle Plain = new("plain", 1.0, 0, 1.0, 0.0, 0.0, 1.0);

    /// <summary>Бронегруппа: trades boldly, the threat weighed lighter, the
    /// formation tight; no pull - that lost. Paired against plain
    /// (2026-11-06): 4:3 in pairs apart, 49 of 72 twins - the fear does it,
    /// the huddle all but never moves a play.</summary>
    public static readonly AiStyle Armour = new("armour", 1.0, 0, 0.85, 0.0, 0.0, 0.75);

    /// <summary>Разведбат: keeps off the guns' front, waits in ambush, fears
    /// a little more (paired against plain: 19:14 in pairs apart).</summary>
    public static readonly AiStyle Recon = new("recon", 1.0, 0, 1.1, 0.15, 0.15, 1.0);

    /// <summary>Артдивизион: a cell further, the threat weighed heavier, the
    /// ambush valued (paired against plain: 17:21 in pairs apart, none twins).</summary>
    public static readonly AiStyle Artillery = new("artillery", 0.9, 1, 1.15, 0.1, 0.1, 1.0);

    public static readonly AiStyle[] All = { Plain, Armour, Recon, Artillery };

    /// <summary>The style of a doctrine's id (<c>QuickSquad.Doctrine.Id</c>),
    /// plain for none; <c>id+hm+lt</c> adds roles to it (<see cref="AiRoles"/>,
    /// <c>+roles</c> all of them) - for a paired measurement on <c>--battle-run</c>.</summary>
    public static AiStyle Of(string? id)
    {
        string[] parts = (id ?? "").Split('+');
        AiStyle found = Plain;
        foreach (AiStyle s in All)
            if (string.Equals(s.Id, parts[0], StringComparison.OrdinalIgnoreCase))
                found = s;
        AiRoles roles = found.Roles;
        for (int i = 1; i < parts.Length; i++)
            roles |= parts[i].ToLowerInvariant() switch
            {
                "hm" => AiRoles.Mortar,
                "lt" => AiRoles.Flank,
                "ht" => AiRoles.Anchor,
                "td" => AiRoles.Axis,
                "roles" => AiRoles.All,
                _ => AiRoles.None,
            };
        return roles == found.Roles ? found : found with { Id = id!, Roles = roles };
    }
}

/// <summary>
/// The squad's roles by class (docs/ai-plan.md, «Координация отряда», И4):
/// a small bonus in <see cref="AiEval.Quick"/> for a cell that suits the
/// class - not an order, the search's material still comes first.
/// </summary>
[Flags]
public enum AiRoles
{
    None = 0,
    /// <summary>HM: behind the front - another own hull nearer the enemy.</summary>
    Mortar = 1,
    /// <summary>LT: on an enemy's axis other than its front - at its side or rear.</summary>
    Flank = 2,
    /// <summary>HT: the anchor ahead - the own hull nearest the enemy.</summary>
    Anchor = 4,
    /// <summary>TD: an enemy on the axis its hull faces, within six cells.</summary>
    Axis = 8,
    All = Mortar | Flank | Anchor | Axis,
}
