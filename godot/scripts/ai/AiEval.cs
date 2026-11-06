using System;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// How good a match is for one side (docs/ai-plan.md, «Оценка позиции»):
/// <see cref="Quick"/> - the material, the units on the control point, the
/// way to the enemy, the stalemate's clock - asked of every play and every
/// reply; <see cref="Full"/> - that and the threat map (<see cref="AiThreat"/>):
/// what the enemy's next activation can take, what ours could after.
/// Positive is good for <c>me</c>. The weights are this file's until the
/// levels are calibrated (И5, <c>data/ai.json</c>).
/// </summary>
public static class AiEval
{
    /// <summary>A side wins or loses: past anything the material can say.</summary>
    public const double Decided = 10000.0;

    /// <summary>A unit alight, of its worth: the fire takes it unless its next activation puts it out.</summary>
    public const double AlightShare = 0.55;
    /// <summary>A unit knocked out or drowned, of its worth: it still blocks,
    /// goes up in a chain, can be repaired.</summary>
    public const double OutShare = 0.2;

    /// <summary>A draw, for the side asking: it would rather fight on.</summary>
    private const double Drawn = -4.0;
    /// <summary>Rounds with nobody harmed after which a draw costs nothing more.</summary>
    private const int Stale = 8;
    /// <summary>A quiet round on the stalemate's clock.</summary>
    private const double Quiet = 0.4;
    /// <summary>A step short of (or past) where a unit wants to be from the nearest enemy.</summary>
    private const double Approach = 0.2;
    /// <summary>Two own active hulls side by side.</summary>
    private const double Huddle = 0.15;
    /// <summary>The control point: the points left (scaled up as they run
    /// out), holding it, a step of the nearest unit toward it.</summary>
    private const double PointLeft = 1.0, Hold = 1.5, ToPoint = 0.15;
    /// <summary>A quiet round's extra weight on a point of material lead.</summary>
    private const double Lead = 0.15;
    /// <summary>The enemy's best activation, and the rest of its units' best:
    /// one of them acts next, the others after - a move that ends under the
    /// guns of several is a bad one even when only one fires next («перебежка»
    /// от укрытия к укрытию, docs/ai-plan.md, И4). Others was 0.1; at 0.3 the
    /// fighter won 33:21 against it, the veteran 20:6 (2026-11-05); 0.5 was
    /// worse than 0.3. Others counts each target once since 2026-11-06; 0.45
    /// and 0.6 then measured within noise of 0.3.</summary>
    private const double Worst = 0.9, Others = 0.3;
    /// <summary>Our own best activation after: the initiative, the enemy's turn between.</summary>
    private const double Initiative = 0.25;

    /// <summary>What a unit is worth (ai-plan.md, вопрос 2): LT 3, MT 4,
    /// HT 6, TD 5, HM 5 by its gun, a quarter a piece of kit on top.</summary>
    public static double Value(BattleUnit u)
    {
        double cls = u.Class.Might switch { 1 => 3.0, 2 => 4.0, 3 => 6.0, 4 => 5.0, _ => 5.0 };
        return cls + 0.25 * System.Numerics.BitOperations.PopCount((uint)u.Kit);
    }

    /// <summary>How much of its worth a unit still is.</summary>
    public static double Share(BattleUnit u) => u.Fate switch
    {
        UnitFate.Alive => u.Burning ? AlightShare : 1.0,
        UnitFate.Knocked or UnitFate.Drowned => OutShare,
        _ => 0.0,
    };

    /// <summary>The material: <paramref name="me"/>'s units' worth less the enemy's.</summary>
    public static double Material(BattleState s, Camp me)
    {
        double sum = 0.0;
        foreach (BattleUnit u in s.Units)
            sum += (u.Side == me ? 1.0 : -1.0) * Value(u) * Share(u);
        return sum;
    }

    /// <summary>The cheap score, asked of every play and every reply.</summary>
    public static double Quick(BattleState s, Camp me, AiStyle? style = null)
    {
        AiStyle st = style ?? AiStyle.Plain;
        if (s.Won is Camp w)
            return w == me ? Decided : -Decided;
        // a draw: the AI fights to the end (meta.md, «ИИ противника и мета») -
        // worse than level, better only when well behind; but once nobody has
        // been harmed for long, the match is going nowhere and the draw costs
        // what is on the board, no more - else the AI would keep a dead match
        // alive with rounds that glance off a tank (each puts the
        // stalemate's clock back, match.md)
        if (s.Drawn)
            return s.HarmlessFor(me) >= Stale ? Material(s, me) : Drawn;
        double score = Material(s, me);
        // the stalemate on count (BattleState.StaleByCount, a trial): the
        // side with more units able to fight wins it, so the clock is its
        // friend - no push on it, the win nearer by each quiet round; the
        // side behind has the push and the loss coming
        int count = s.StaleByCount ? CountLead(s, me) : 0;
        if (count != 0)
            score += Math.Sign(count) * StaleWin * Math.Min(1.0, s.QuietRounds / (double)MatchRules.QuietRoundsDraw);
        // the stalemate's clock, read as rounds with nobody harmed - a round
        // that glances off a tank does not wind it back: quiet rounds weigh on
        // the side asking, so it goes and takes something - the more, the
        // further ahead it is (ai-plan.md, «Пат»: the leader has a win to lose)
        int quiet = count > 0 ? 0 : s.HarmlessFor(me);
        score -= quiet * (Quiet + Lead * Math.Max(0.0, score));
        foreach (BattleUnit u in s.Units)
        {
            if (u.Fate != UnitFate.Alive)
                continue;
            double sign = u.Side == me ? 1.0 : -1.0;
            // to the enemy: the mortar to its band, the guns to the nearest
            int near = Nearest(s, u);
            // the doctrine's (AiStyle) for our own units; the enemy's read plain
            AiStyle my = u.Side == me ? st : AiStyle.Plain;
            if (near < int.MaxValue)
            {
                int want = u.Class.Lobs ? 4 : Math.Max(1, (u.Class.Turreted ? 2 : 3) + my.Reach);
                // the pull grows with the stalemate's clock: a side that sits
                // still the whole match is drawn
                score -= sign * my.Pull * Approach * (1.0 + 0.5 * Math.Min(quiet, 4)) * Math.Abs(near - want);
            }
            if (u.Side == me)
            {
                if (st.Front > 0.0 && Faced(s, u))
                    score -= st.Front;
                if (st.Watch > 0.0 && u.Ambushing)
                    score += st.Watch;
                if (st.Roles != AiRoles.None)
                    score += Role(s, u, st.Roles);
            }
            // the formation: an own hull beside is a chain waiting for a spark
            // (states.md, «Уничтожен»), each pair once
            foreach (BattleUnit o in s.Units)
                if (o.Id > u.Id && o.Side == u.Side && o.Fate == UnitFate.Alive && HexField.Distance(u.Cell, o.Cell) == 1)
                    score -= sign * my.Huddle * Huddle;
        }
        if (s.Point is { } point && point.Length > 0)
            score += PointScore(s, me, point);
        return score;
    }

    /// <summary>
    /// The control point (match.md, «Удержание точки»): the points left on
    /// either side, the more the fewer are left; holding it - the side that
    /// does not loses a point a round; the nearest active unit of each side
    /// drawn to it.
    /// </summary>
    private static double PointScore(BattleState s, Camp me, Vector2I[] point)
    {
        Camp enemy = Across(me);
        int mine = s.Score[(int)me], theirs = s.Score[(int)enemy];
        double score = PointLeft * (1.0 / Math.Max(1, theirs) - 1.0 / Math.Max(1, mine)) * 4.0;
        Camp? held = Battle.HeldBy(s);
        if (held == me)
            score += Hold;
        else if (held == enemy)
            score -= Hold;
        int nearMe = int.MaxValue, nearThem = int.MaxValue;
        foreach (BattleUnit u in s.Units)
        {
            if (u.Fate != UnitFate.Alive)
                continue;
            int d = int.MaxValue;
            foreach (Vector2I c in point)
                d = Math.Min(d, HexField.Distance(u.Cell, c));
            if (u.Side == me)
                nearMe = Math.Min(nearMe, d);
            else
                nearThem = Math.Min(nearThem, d);
        }
        if (nearMe < int.MaxValue)
            score -= ToPoint * nearMe;
        if (nearThem < int.MaxValue)
            score += ToPoint * nearThem;
        return score;
    }

    /// <summary>
    /// The full score: <see cref="Quick"/> less what the enemy's next
    /// activation can take - its best most, for one unit acts; the fear eases
    /// as the stalemate nears - and a little for what ours could take after.
    /// </summary>
    public static double Full(BattleState s, Camp me, AiThreat threats, AiStyle? style = null)
    {
        double score = Quick(s, me, style);
        if (s.Over)
            return score;
        (double worst, double others) = threats.Of(s, Across(me));
        AiStyle st = style ?? AiStyle.Plain;
        // the fear eases as the draw nears - not for a side the stalemate on count would crown
        int harmless = s.StaleByCount && CountLead(s, me) > 0 ? 0 : s.HarmlessFor(me);
        double fear = st.Fear * Math.Max(0.3, 1.0 - 0.25 * harmless);
        score -= fear * (Worst * worst + st.Spread * Others * others);
        return score + Initiative * threats.Of(s, me).Worst;
    }

    /// <summary>The leaf after the enemy's reply: <see cref="Quick"/> and our
    /// best activation, which is next.</summary>
    public static double AfterReply(BattleState s, Camp me, AiThreat threats, AiStyle? style = null) =>
        s.Over ? Quick(s, me, style) : Quick(s, me, style) + Initiative * threats.Of(s, me).Worst;

    /// <summary>Whether <paramref name="u"/> stands on the axis an enemy's
    /// gun faces, within six cells - its front, where its shots and its
    /// ambush go (<see cref="AiStyle.Front"/>).</summary>
    private static bool Faced(BattleState s, BattleUnit u)
    {
        foreach (BattleUnit e in s.Units)
        {
            if (e.Side == u.Side || e.Fate != UnitFate.Alive || e.Class.Lobs)
                continue;
            Vector2I at = e.Cell;
            for (int k = 0; k < 6; k++)
            {
                Vector2I next = HexField.Step(at, e.Heading);
                if (next == at)
                    break;
                if (next == u.Cell)
                    return true;
                at = next;
            }
        }
        return false;
    }

    /// <summary>A cell that suits the class's role (<see cref="AiRoles"/>).</summary>
    private const double RoleBonus = 0.3;

    /// <summary>The role's bonus for own <paramref name="u"/> where it stands.</summary>
    private static double Role(BattleState s, BattleUnit u, AiRoles roles)
    {
        switch (u.Class.Might)
        {
            case 5 when (roles & AiRoles.Mortar) != 0:
                // behind the front: another own hull nearer the enemy than it
                return Ahead(s, u, out bool other) && other ? -RoleBonus : 0.0;
            case 3 when (roles & AiRoles.Anchor) != 0:
                // the anchor: none of its own nearer the enemy
                return Ahead(s, u, out _) ? RoleBonus : 0.0;
            case 1 when (roles & AiRoles.Flank) != 0:
                foreach (BattleUnit e in s.Units)
                    if (e.Side != u.Side && e.Fate == UnitFate.Alive && AxisOf(e, u.Cell) is int h && h != e.Heading)
                        return RoleBonus;
                return 0.0;
            case 4 when (roles & AiRoles.Axis) != 0:
                foreach (BattleUnit e in s.Units)
                    if (e.Side != u.Side && e.Fate == UnitFate.Alive && AxisOf(u, e.Cell) == u.Heading)
                        return RoleBonus;
                return 0.0;
        }
        return 0.0;
    }

    /// <summary>Whether no other own active hull is nearer its nearest enemy
    /// than <paramref name="u"/>; <paramref name="other"/> - whether there is
    /// another own active hull at all.</summary>
    private static bool Ahead(BattleState s, BattleUnit u, out bool other)
    {
        int mine = Nearest(s, u);
        other = false;
        foreach (BattleUnit o in s.Units)
        {
            if (o == u || o.Side != u.Side || o.Fate != UnitFate.Alive)
                continue;
            other = true;
            if (Nearest(s, o) < mine)
                return false;
        }
        return true;
    }

    /// <summary>The axis from <paramref name="from"/> that reaches
    /// <paramref name="cell"/> within six steps, or null.</summary>
    private static int? AxisOf(BattleUnit from, Vector2I cell)
    {
        foreach (int h in HexField.EdgeHeadings)
        {
            Vector2I at = from.Cell;
            for (int k = 0; k < 6; k++)
            {
                at = HexField.Step(at, h);
                if (at == cell)
                    return h;
            }
        }
        return null;
    }

    /// <summary>The stalemate on count's worth at the stalemate's line, scaled
    /// by how near the clock is to it.</summary>
    private const double StaleWin = 3.0;

    /// <summary><paramref name="me"/>'s units able to fight less the enemy's.</summary>
    private static int CountLead(BattleState s, Camp me)
    {
        int lead = 0;
        foreach (BattleUnit u in s.Units)
            if (u.Fate == UnitFate.Alive)
                lead += u.Side == me ? 1 : -1;
        return lead;
    }

    public static Camp Across(Camp c) => c == Camp.Green ? Camp.Brown : Camp.Green;

    /// <summary>Steps from <paramref name="u"/> to the nearest enemy able to
    /// fight - with none left the match is over (GameModes.md), the nearest
    /// still standing is a fallback only - or <see cref="int.MaxValue"/> for none.</summary>
    public static int Nearest(BattleState s, BattleUnit u) =>
        NearestEnemy(s, u, u.Cell) is { } e ? HexField.Distance(u.Cell, e.Cell) : int.MaxValue;

    /// <summary>The enemy of <paramref name="u"/> nearest <paramref name="from"/>:
    /// one able to fight, else one still standing; null for none.</summary>
    public static BattleUnit? NearestEnemy(BattleState s, BattleUnit u, Godot.Vector2I from)
    {
        BattleUnit? alive = null, standing = null;
        int da = int.MaxValue, ds = int.MaxValue;
        foreach (BattleUnit o in s.Units)
        {
            if (o.Side == u.Side || !o.Standing)
                continue;
            int d = HexField.Distance(from, o.Cell);
            if (o.Fate == UnitFate.Alive && d < da)
            {
                da = d;
                alive = o;
            }
            else if (o.Fate != UnitFate.Alive && d < ds)
            {
                ds = d;
                standing = o;
            }
        }
        return alive ?? standing;
    }
}
