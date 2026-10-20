using System.Collections.Generic;
using System.Linq;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The ricochet's second leg (GDD units.md, «Рикошет»): a round held by a
/// flank leaves along one of the six axes and flies on to the first thing on
/// it. One reading for the HUD's line of fire (<c>Tank3DBench.Aim</c>) and
/// for the round itself (<c>Tank3DBench.Shell</c>, <c>Strike</c>), so the
/// line drawn before the shot is the way the round goes after it.
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

        public bool Pierces => Ends == LegEnd.Tank && Level > 0;

        /// <summary>It goes on past where the line stops - off the board.</summary>
        public bool Fades => Ends == LegEnd.Edge;

        public Color Ink => Ends != LegEnd.Tank ? AimGlance : Pierces ? AimPierce : AimHeld;

        public string Says => Ends switch
        {
            LegEnd.Edge => "уйдёт за край",
            LegEnd.Wall => $"в стену {Cell}",
            LegEnd.Wood => $"в {(Smoke ? "дым" : "лес")} {Cell}",
            LegEnd.Ground => $"в склон {Cell}",
            _ => $"{Hull!.Tag} {FaceWord(Face)}: {(Pierces ? $"пробьёт ({Level})" : "не пробьёт")}",
        };

        public string Log => Ends == LegEnd.Tank
            ? $"into {Hull!.Tag} on {Cell} {Face} ({Plate}), level {Level}"
            : $"{Ends} at {Cell}";
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
        Vector3 dir = HexWay(cell, axis);
        // The leg leaves at the plate's height, along the axis through the
        // target's cell.
        Vector3 start = at;
        Vector3 Cross(Vector2I a, Vector2I b)
        {
            Vector3 rim = 0.5f * (CellWorld(a) + CellWorld(b));
            return start + dir * Mathf.Max(dir.Dot(rim - start), 0.0f);
        }
        int level0 = _field.LevelAt(cell);
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
            if (_parked.Find(p => p.Cell == next) is { } standing)
            {
                int level = LevelOn(AheadOf(standing.Heading), ClassFor(standing.Tag), dir, might,
                                    out string face, out Gunnery.Plate plate);
                Vector3 end = standing.Rig.Position + Vector3.Up * (0.45f * standing.Model.Size.Y * standing.Model.PixelsPerUnit);
                MeshInstance3D? part = null;
                Vector3 n = -dir;
                if (HitsOf(standing).Cast(start, dir) is { } hit)
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
        float speed = Shell.Speed * GlanceSpeed;
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
        if (leg.Hull is not { } p || !IsInstanceValid(p.Model) || leg.Part is not { } part || !IsInstanceValid(part))
            return;
        CelHit hits = HitsOf(p);
        if (leg.Pierces)
        {
            hits.Leave(part, leg.End, leg.N, way, CelHit.Kind.Hole);
            hits.Pierce(part, leg.End, leg.N);
        }
        else
        {
            hits.Leave(part, leg.End, leg.N, way, CelHit.Kind.Gouge, 0.8f);
            hits.Ricochet(leg.End, leg.N, way - 2.0f * way.Dot(leg.N) * leg.N);
        }
        GD.Print($"tank3d: ricochet on {p.Tag} {leg.Face} ({leg.Plate}), might {leg.Might}, level {leg.Level}: "
                 + (leg.Pierces ? "pierced" : "held, spent"));
    }
}
