using System.Collections.Generic;
using System.Linq;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The round's second leg past the target: a ricochet (GDD units.md,
/// «Рикошет») - held by a flank, it leaves along one of the six axes - or a
/// destroyer's pass (classes.md, «Тяжёлый снаряд TD»; <see cref="PassLeg"/>)
/// - it holed the target with its pass in hand and goes straight on with one
/// less might. Either flies on to the first thing on its axis
/// (<see cref="Walk"/>). One reading for the HUD's line of fire
/// (<c>Tank3DBench.Aim</c>) and for the round itself (<c>Tank3DBench.Shell</c>,
/// <c>Strike</c>), so the line drawn before the shot is the way the round
/// goes after it. A pass is spent either way: the next wall stops it, brick
/// or concrete, and a wall passed short of the target leaves no leg past it.
///
/// <list type="bullet">
/// <item><b>The axis is the rules' and not the mesh's.</b> The side plate is a
/// mirror lying along the hull: the round's axis in, mirrored about the
/// hull's, <c>2·hull − in</c> - came from NE onto a hull looking N, left
/// toward SE (the table in units.md, <see cref="Gunnery.Deflect"/>). Both are
/// snapped to the six first: the hull to the side it points at, the way in to
/// the axis nearest it. Before this the bench mirrored the round about the
/// struck triangle's normal and let it fall 0.8 s - a spark, not a shot.</item>
/// <item><b>What stops it</b>, walked a cell at a time from the target's: a
/// standing wall on the edge - broken, the round spent (field.md); a wood or
/// smoke - spent, asked as the direct shot's lane asks it
/// (<see cref="HexField.Screened"/>, so a board whose terrain.json lets the
/// wood not screen lets both legs through it); ground higher than the
/// target's - into the slope; a standing tank
/// (<c>Tank3DBench.Parked</c>) - by its armour, with the same might and no
/// second ricochet (a flank that holds spends it); the board's edge - gone.
/// "Один пустой гекс" does not hold for it: the next cell is fair.</item>
/// <item><b>Not on the bench yet</b>: a standing tank holed is marked, not
/// knocked out - the parked ones have no fate; the rules list the wood alone,
/// smoke stopping it is the lane's reading; the own tank is never on the leg - a
/// mirror about a hull side cannot send a round back along its own axis.</item>
/// </list>
/// </summary>
public sealed partial class Tank3DBench
{
    private enum LegEnd { Edge, Wall, Wood, Ground, Tank }

    /// <summary>The second leg: its axis, from the plate to where it ends, what
    /// ends it, and on a tank the plate and the depth.</summary>
    private sealed record Leg(int Axis, Vector3 From, Vector3 End, LegEnd Ends, Vector2I Cell, int Might)
    {
        public WallProp? Wall { get; init; }
        public Parked? Hull { get; init; }
        public MeshInstance3D? Part { get; init; }
        public Vector3 N { get; init; } = Vector3.Up;
        public string Face { get; init; } = "";
        public Gunnery.Plate Plate { get; init; }
        public int Level { get; init; }
        public bool Smoke { get; init; }
        /// <summary>A destroyer's round gone through the target
        /// (<see cref="PassLeg"/>), not one turned off a flank.</summary>
        public bool Through { get; init; }

        public bool Pierces => Ends == LegEnd.Tank && Level > 0;

        /// <summary>It goes on past where the line stops - off the board.</summary>
        public bool Fades => Ends == LegEnd.Edge;

        /// <summary>The leg's colour short of a tank: how it left the target -
        /// through it, or off its flank.</summary>
        public Color Lead => Through ? AimPierce : AimGlance;

        public Color Ink => Ends != LegEnd.Tank ? Lead : Pierces ? AimPierce : AimHeld;

        public string Says => Ends switch
        {
            LegEnd.Edge => "уйдёт за край",
            LegEnd.Wall => $"в стену {MapAt(Cell)}",
            LegEnd.Wood => $"в {(Smoke ? "дым" : "лес")} {MapAt(Cell)}",
            LegEnd.Ground => $"в склон {MapAt(Cell)}",
            _ => $"{Hull!.Tag} {FaceWord(Face)}: {(Pierces ? $"пробьёт ({Level})" : "не пробьёт")}",
        };

        public string Log => Ends == LegEnd.Tank
            ? $"into {Hull!.Tag} on {MapAt(Cell)} {Face} ({Plate}), level {Level}"
            : $"{Ends} at {MapAt(Cell)}";
    }

    private static string FaceWord(string face) => face switch
    {
        "front" => "лоб",
        "rear" => "корма",
        "roof" => "крыша",
        _ => "борт",
    };

    /// <summary>How far a leg is walked, cells, before it is let go.</summary>
    private const int LegLongest = 40;

    /// <summary>
    /// Where a round held by <paramref name="o"/>'s flank at
    /// <paramref name="at"/>, travelling <paramref name="way"/> with
    /// <paramref name="might"/>, goes on to - or none if the plate it met is
    /// not a flank by the rules' axes.
    /// </summary>
    private Leg? GlanceLeg(Other o, Vector3 at, Vector3 way, int might)
    {
        if (_field is null)
            return null;
        Vector2I cell = OtherCell(o);
        int hull = Nearest(cell, o.Ahead);
        int arrives = Nearest(cell, way);
        int axis = Mathf.PosMod(2 * hull - arrives, 360);
        // a flank by the axes: the way in neither along the hull nor against it
        if (!HexField.EdgeHeadings.Contains(axis) || arrives == hull || arrives == HexField.Reverse(hull))
            return null;
        return Walk(cell, axis, at, might);
    }

    /// <summary>
    /// Where a destroyer's round that holed <paramref name="o"/> at
    /// <paramref name="at"/>, travelling <paramref name="way"/>, goes on to
    /// with <paramref name="might"/> left (GDD classes.md, «Тяжёлый снаряд TD»):
    /// straight on along the axis it came by - its one pass spent, so whatever
    /// it meets next stops it, a brick wall too.
    /// </summary>
    private Leg? PassLeg(Other o, Vector3 at, Vector3 way, int might)
    {
        if (_field is null)
            return null;
        Vector2I cell = OtherCell(o);
        return Walk(cell, Nearest(cell, way), at, might) with { Through = true };
    }

    /// <summary>The leg's walk out of <paramref name="cell"/> along
    /// <paramref name="axis"/> from <paramref name="start"/> - the plate it
    /// left, at its height - to the first thing on it.</summary>
    private Leg Walk(Vector2I cell, int axis, Vector3 start, int might)
    {
        Vector3 dir = HexWay(cell, axis);
        Vector3 Cross(Vector2I a, Vector2I b)
        {
            Vector3 rim = 0.5f * (CellWorld(a) + CellWorld(b));
            return start + dir * Mathf.Max(dir.Dot(rim - start), 0.0f);
        }
        int level0 = _field!.LevelAt(cell);
        Vector2I here = cell;
        for (int k = 0; k < LegLongest; k++)
        {
            Vector2I next = HexField.Step(here, axis);
            if (WallOnAxis(here, axis, 1) is { } wall)
                return new Leg(axis, start, Cross(here, next) with { Y = wall.At.Y }, LegEnd.Wall, wall.Cell, might)
                    { Wall = wall.Prop };
            if (!_field.InBounds(next))
                return new Leg(axis, start, Cross(here, next), LegEnd.Edge, here, might);
            if (_field.LevelAt(next) > level0)
                return new Leg(axis, start, Cross(here, next), LegEnd.Ground, next, might);
            if (_field.Screened(next))
                return new Leg(axis, start, Cross(here, next), LegEnd.Wood, next, might)
                    { Smoke = _field.CoverAt(next) != Cover.Forest };
            if (_parked.Find(p => p.Cell == next && p.Fate != Fate.Destroyed) is { } standing)
            {
                int level = LevelOn(AheadOf(standing.Heading), ClassFor(standing.Tag), dir, might,
                                    out string face, out Gunnery.Plate plate);
                // a knocked-out one goes up whatever its armour (Tank3DBench.Match)
                if (standing.Fate == Fate.Knocked)
                    level = Mathf.Max(level, 1);
                Vector3 end = standing.Rig.Position + Vector3.Up * (0.45f * standing.Model.Size.Y * standing.Model.PixelsPerUnit);
                MeshInstance3D? part = null;
                Vector3 n = -dir;
                // Along the axis at the plate's height; a lower hull than the
                // one it glanced off has that line pass over its roof, so
                // then at its middle - else the round met nothing and the hit
                // was lost, the hull unmarked and its fate untouched.
                if ((HitsOf(standing).Cast(start, dir) ?? HitsOf(standing).Cast(start, end - start)) is { } hit)
                {
                    end = hit.At;
                    part = hit.Part;
                    n = hit.N;
                }
                return new Leg(axis, start, end, LegEnd.Tank, next, might)
                    { Hull = standing, Part = part, N = n, Face = face, Plate = plate, Level = level };
            }
            here = next;
        }
        return new Leg(axis, start, start + dir * (CellStep * LegLongest), LegEnd.Edge, here, might);
    }

    /// <summary>Which of the six a way on the ground lies nearest.</summary>
    private int Nearest(Vector2I cell, Vector3 way)
    {
        way.Y = 0.0f;
        int best = HexField.EdgeHeadings[0];
        float dot = -2.0f;
        foreach (int side in HexField.EdgeHeadings)
        {
            float d = HexWay(cell, side).Dot(way.Normalized());
            if (d > dot)
            {
                dot = d;
                best = side;
            }
        }
        return best;
    }

    private static Vector3 AheadOf(float heading)
    {
        float h = Mathf.DegToRad(heading);
        return new Vector3(Mathf.Sin(h), 0.0f, Mathf.Cos(h));
    }

    // --- the round on the leg -----------------------------------------------

    /// <summary>A standing tank's marks, hung on first need.</summary>
    private readonly Dictionary<TankModel, CelHit> _parkedHits = new();

    private CelHit HitsOf(Parked p)
    {
        foreach (TankModel gone in _parkedHits.Keys.Where(m => !IsInstanceValid(m)).ToList())
        {
            _parkedHits[gone].QueueFree();
            _parkedHits.Remove(gone);
        }
        if (_parkedHits.TryGetValue(p.Model, out CelHit? hits))
            return hits;
        hits = new CelHit { Name = $"ParkedHits_{p.Tag}" };
        AddChild(hits);
        hits.Build(p.Model.HullLength * p.Model.PixelsPerUnit);
        hits.Targets(p.Model, p.Model.Cel);
        hits.Solids = _solids;
        _parkedHits[p.Model] = hits;
        return hits;
    }

    /// <summary>The round down <paramref name="leg"/>, at what a ricochet has
    /// left of its speed, and what it does where the leg ends.</summary>
    private void FlyLeg(Leg leg)
    {
        ShellsBuild();
        Vector3 dir = (leg.End - leg.From).Normalized();
        // a ricochet has given the plate most of itself; a pass goes on at speed
        float speed = Shell.Speed * (leg.Through ? 1.0f : GlanceSpeed);
        System.Action? landed = leg.Ends switch
        {
            LegEnd.Wall when leg.Wall is { } prop => WallStruck(prop, leg.End, dir, inside: false, lob: false, through: false),
            LegEnd.Wood or LegEnd.Ground => () => Lands(new Vector3(leg.End.X, ShellGround(leg.End, out _), leg.End.Z), false, false),
            LegEnd.Tank => () => StrikeParked(leg, dir),
            _ => null,
        };
        // in Throw's equal steps of time: Fly paces the round by the points
        Vector3 from = leg.From + dir * 2.0f;
        float length = from.DistanceTo(leg.End);
        int steps = Mathf.Max(1, Mathf.CeilToInt(length / ShellStep));
        var path = new List<Vector3>();
        for (int k = 0; k <= steps; k++)
            path.Add(from.Lerp(leg.End, k / (float)steps));
        _shells!.Fly(path, speed, TracerCal, SmokeCal, false, landed, length / steps / speed);
    }

    /// <summary>The leg's round on a standing tank: a hole and the pierce, or
    /// a gouge and its sparks - the round spent either way. Marked, not
    /// knocked out: a standing tank has no fate on the bench.</summary>
    private void StrikeParked(Leg leg, Vector3 way)
    {
        if (leg.Hull is not { } p || !IsInstanceValid(p.Model))
            return;
        CelHit hits = HitsOf(p);
        // no part, when neither ray found the hull: the mark goes, the hit does not
        MeshInstance3D? part = leg.Part is { } found && IsInstanceValid(found) ? found : null;
        if (leg.Pierces)
        {
            if (part is not null)
            {
                hits.Leave(part, leg.End, leg.N, way, CelHit.Kind.Hole);
                hits.Pierce(part, leg.End, leg.N);
            }
        }
        else
        {
            if (part is not null)
                hits.Leave(part, leg.End, leg.N, way, CelHit.Kind.Gouge, 0.8f);
            hits.Ricochet(leg.End, leg.N, way - 2.0f * way.Dot(leg.N) * leg.N);
        }
        // the hull thrown on its springs as the target's is (Tank3DBench.Shell, Strike)
        JoltParked(p, way, leg.Pierces ? 3.2f : 2.8f);
        Log.Print($"tank3d: {(leg.Through ? "pass" : "ricochet")} on {p.Tag} {leg.Face} ({leg.Plate}), might {leg.Might}, level {leg.Level}: "
                 + (leg.Pierces ? "pierced" : "held, spent"));
        // a match's unit has a fate (Tank3DBench.Match)
        UnitStruck(p, leg.Pierces);
    }

    /// <summary>A standing hull's rock: the springs the target has
    /// (<see cref="Other.Pitch"/>, <see cref="Other.Roll"/>), lent while it
    /// settles. A standing hull leaves its model's pitch and roll at nought,
    /// so they are free for it.</summary>
    private sealed class Jolt
    {
        public required TankModel Model;
        public readonly Spring Pitch = new(RockSpring.K, RockSpring.C);
        public readonly Spring Roll = new(RockSpring.K, RockSpring.C);
    }

    private readonly List<Jolt> _jolts = new();

    /// <summary>A round on a standing hull, travelling <paramref name="way"/>:
    /// the struck end and side thrown down by <paramref name="k"/>, as
    /// <c>Strike</c> throws the target's. Before this a ricochet's second
    /// tank took the round without a stir.</summary>
    private void JoltParked(Parked p, Vector3 way, float k)
    {
        Jolt? j = null;
        for (int i = 0; i < _jolts.Count; i++)
            if (ReferenceEquals(_jolts[i].Model, p.Model))
                j = _jolts[i];
        if (j is null)
        {
            j = new Jolt { Model = p.Model };
            _jolts.Add(j);
        }
        Vector3 ahead = AheadOf(p.Heading), left = new(ahead.Z, 0.0f, -ahead.X);
        j.Pitch.Kick(k * ahead.Dot(way));
        j.Roll.Kick(-k * left.Dot(way));
    }

    /// <summary>Every frame: the jolted hulls rock and settle; settled, or made
    /// the target (whose own springs then hold the model), they are let go.</summary>
    private void JoltTick(float dt)
    {
        for (int i = _jolts.Count - 1; i >= 0; i--)
        {
            Jolt j = _jolts[i];
            if (!IsInstanceValid(j.Model) || (_other is { } o && ReferenceEquals(o.Model, j.Model)))
            {
                _jolts.RemoveAt(i);
                continue;
            }
            j.Model.Pitch = j.Pitch.Step(dt);
            j.Model.Roll = j.Roll.Step(dt);            bool still = Mathf.Abs(j.Pitch.X) + Mathf.Abs(j.Roll.X) < 1e-4f
                         && Mathf.Abs(j.Pitch.V) + Mathf.Abs(j.Roll.V) < 1e-3f;
            if (still)
            {
                j.Model.Pitch = 0.0f;
                j.Model.Roll = 0.0f;
                _jolts.RemoveAt(i);
            }
            j.Model.Apply();
        }
    }
}
