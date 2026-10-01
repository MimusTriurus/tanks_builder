using System;
using System.Collections.Generic;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The ram: the tank on the rig drives into a second one and shoves it a hex
/// along - the board's ram (<see cref="TankTick.RamContacts"/>, docs/combat.md
/// «Таран»), on two models.
///
/// <list type="bullet">
/// <item><b>The target</b> (<see cref="Other"/>) - a second model on its own
/// rig, standing on a cell by the rules, with its own springs, ground and scuff.
/// It does not drive, shoot or burn: it is what is rammed.</item>
/// <item><b>Contact by the hulls, not by a share of the leg.</b> The board lands
/// the ram at a third of the last leg (<see cref="TankTick.RamContact"/>), a
/// number read off pictures of two silhouettes because a sprite has no body.
/// Here the two footprints (<see cref="MeasureFootprint"/>, hull and belts) are
/// rectangles on the ground and the ram lands the frame they touch - from
/// any side, at any angle, driven by hand or by <c>--do ram</c>.</item>
/// <item><b>The rules are the board's own</b> - <see cref="Ramming.Of"/>, asked
/// in the frame of contact: the mass table first, then the hex behind. A refusal
/// is a hull stopping against a hull, with the reason said out loud.</item>
/// <item><b>Coupled, then backed off</b>, as <see cref="TankTick.Pushes"/> and
/// <see cref="TankTick.BackOff"/>: both hulls leave the contact at
/// <see cref="TankTick.RamKeep"/> of the speed that arrived and cross the hex as
/// one, the gap they met at held; the target keeps its heading, its belts
/// locked; the rammer waits <see cref="TankTick.RamDwell"/> and backs on to the
/// middle of the hex it took at <see cref="TankTick.RamBack"/> of its speed.
/// A ramp taken from above is slid on to its foot alone.</item>
/// <item><b>What the meeting draws</b> - one kick of the view and the ring after
/// it (the board's ram channel), each hull thrown on its springs by the speed it
/// changed by (<see cref="TankTick.RamJolt"/>, the struck end down), and metal
/// off the seam (<see cref="CelHit.Scrape"/>). What the board could not do: a
/// blow on a side <b>rolls</b> the hull - the sprites have no roll, the model
/// has one - and the fans spray along the seam and up, not along each plate's
/// normal: with depth, that normal points into the other hull and hides them.
/// </item>
/// <item><b>The scuff</b> - the shoved hull's locked belts dragged across the
/// ground: <see cref="CelRuts"/> with no shoe bars, as wide as the belts are
/// across the way they are dragged (<see cref="TrackMarks.Scrub"/>'s picture).</item>
/// </list>
/// </summary>
public sealed partial class Tank3DBench
{
    private sealed class Other
    {
        public required TankModel Model;
        public required Node3D Rig;
        public required MovementProfile Profile;
        public required string Tag;
        public (float Along, float Across, float HalfLen, float HalfWide) Foot;
        /// <summary>Bench degrees, 0 facing the camera.</summary>
        public float Heading;
        /// <summary>Where it stands by the rules - the cell a ram asks about.</summary>
        public Vector2I Cell;
        public Vector3 Up = Vector3.Up;
        public readonly Spring Pitch = new(RockSpring.K, RockSpring.C);
        public readonly Spring Roll = new(RockSpring.K, RockSpring.C);
        /// <summary>The hull on its suspension, model units, up positive.</summary>
        public readonly Spring Heave = new(SinkSpring.K, SinkSpring.C);
        /// <summary>How far it is tipped over a brink, the sine: its trailing
        /// end still on the bank, its middle gone down past it.</summary>
        public float Tip;
        /// <summary>The cells' middles it is being moved through, and the next.</summary>
        public readonly List<Vector3> Legs = new();
        public int Leg;
        /// <summary>Its speed along the legs when nothing is pushing it - the
        /// slide down a ramp's slope after the push.</summary>
        public float Speed;
        public float FallV;
        public bool Falling, Wet;
        /// <summary>How far it was dragged this frame, px.</summary>
        public float Slid;
        public Vector3 Way;
        public CelRuts Scuff = null!;
        /// <summary>Its marks - the dents the rams left (<see cref="Dents"/>).</summary>
        public CelHit Hits = null!;
        public readonly List<CelRuts.Belt> Belts = new();
        /// <summary>The dust its dragged belts plough up (<see cref="Plough"/>),
        /// none under <c>--fx2d</c>.</summary>
        public CelDust? Dust;
        public readonly List<CelDust.Belt> Ploughs = new();
        /// <summary>How fast it is being dragged, as a share of
        /// <see cref="RamLeanAt"/> of its top speed, 0..1.</summary>
        public float Pace, WasPace;
        /// <summary>Time to the next tremble of the drag, s, and how many so far.</summary>
        public float ShudderIn;
        public int Shudders;

        public Vector3 Ahead
        {
            get
            {
                float h = Mathf.DegToRad(Heading);
                return new Vector3(Mathf.Sin(h), 0.0f, Mathf.Cos(h));
            }
        }
    }

    private enum RamPhase { None, Push, Dwell, Back }

    /// <summary>The share of the class's top speed below which a hull only
    /// touches the other: a nudge at a crawl is parking, not a ram.</summary>
    private const float RamFloor = 0.2f;

    private string _otherTag = "LTR";
    /// <summary><c>--target</c> or <c>--target-cell</c> was given: the target
    /// stands on the board from the first frame.</summary>
    private bool _otherWanted;
    private Vector2I? _otherCell;
    private float _otherHeading = 180.0f;
    private Other? _other;
    private RamPhase _ram;

    /// <summary>How far the dragged hull leans the way it is shoved, rad, at
    /// full pace: the belts that lead dig in, and that side and end go down.
    /// Held while it slides, let go at the stop, and the spring swings it back
    /// past level - what says it was moved and did not just glide. Only the
    /// knock at the meeting, a degree for a tenth of a second, and the target
    /// went across the hex like a cut-out (the user asked for it to react);
    /// at half this the lean was two px of a side, nothing on the screen.</summary>
    private const float RamLean = 0.09f;
    /// <summary>The share of the target's top speed counted as full pace for
    /// the lean, the tremble and the dust's size: the pair leave at the keep
    /// of the slower top (<c>_ramCap</c>), never near the top itself.</summary>
    private const float RamLeanAt = 0.45f;
    /// <summary>The drag's tremble: a kick on each spring this often, s, and
    /// this hard at full pace - locked belts grabbing and letting go.</summary>
    private const float RamShudderEvery = 0.07f, RamShudder = 0.6f;
    /// <summary>The target's springs: softer and far less damped than the
    /// hull's kick (<c>KickSpring</c>, 1150/38) - a quarter of a second a
    /// swing and a few swings to rest. On the kick's spring the hull came
    /// back from the lean to level in one move, 0.2 of a degree past it, and
    /// did not rock at all.</summary>
    private static readonly (float K, float C) RockSpring = (700.0f, 14.0f);
    /// <summary>The kick when the drag stops: the hull goes on the way it was
    /// going, past its lean, and swings back.</summary>
    private const float RamSettle = 1.2f;

    /// <summary>The suspension the hull lands on: a sink and a bounce or two.</summary>
    private static readonly (float K, float C) SinkSpring = (600.0f, 12.0f);
    /// <summary>A landing off a bank, at the fall's <see cref="LandAt"/> of
    /// speed: how far the hull sinks on its suspension, hull lengths; how hard
    /// the end and side that land first are thrown down, rad/s; and the dust's
    /// pace (<see cref="CelDust.Belt.Pace"/>) - small, a puff round the hull.
    /// Before this the only kick was the pitch by the shove's share along the
    /// hull, nothing for a hull shoved off broadside - the rammed one's way -
    /// and it came down like a brick.</summary>
    private const float LandSink = 0.05f, LandTip = 1.4f, LandDust = 0.6f;
    /// <summary>The fall speed counted as one landing, px/s - off one level.</summary>
    private const float LandAt = 268.0f;
    /// <summary>How much higher the landing's dust climbs than a belt's.</summary>
    private const float LandLift = 3.5f;
    /// <summary>The steepest the hull tips over a brink, rad.</summary>
    private const float TipMost = 0.6f;
    /// <summary>The flat side of the hex the ram goes along, world, unit.</summary>
    private Vector3 _ramWay;
    /// <summary>The rammer's place off the target's, held while they are one.</summary>
    private Vector3 _ramGap;
    /// <summary>Where the rammer backs on to: the middle of the hex it took.</summary>
    private Vector3 _ramPark;
    private float _ramSpeed, _ramClock, _ramCap;
    /// <summary>The hulls touched and have not come apart since: pressing on
    /// is pushing against it, not another ram.</summary>
    private bool _ramTouching;
    /// <summary><c>--do ram</c> is driving: it lets go of the throttle when the
    /// ram is over, one way or the other.</summary>
    private bool _ramScripted;
    private string _ramNote = "";
    private readonly CelHit?[] _fans = new CelHit?[2];
    private float _fansFor;

    private bool RamRunning => _ram != RamPhase.None;

    // --- the target ---------------------------------------------------------

    /// <summary>The target on <paramref name="cell"/>, facing
    /// <paramref name="heading"/>: made (or remade, if its tag changed) and put
    /// down whole.</summary>
    private void PlaceOther(Vector2I cell, float heading)
    {
        if (_field is null || !_field.InBounds(cell))
            return;
        if (_other is null || !string.Equals(_other.Tag, _otherTag, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                BuildOther(_otherTag);
            }
            catch (Exception e)
            {
                GD.Print($"tank3d: target {_otherTag}: {e.Message}");
                _ramNote = $"цель {_otherTag} не загрузилась";
                return;
            }
        }
        Other o = _other!;
        _otherCell = cell;
        _otherHeading = heading;
        o.Cell = cell;
        o.Heading = Mathf.PosMod(heading, 360.0f);
        o.Legs.Clear();
        o.Leg = 0;
        o.Speed = o.FallV = 0.0f;
        o.Falling = false;
        o.Pitch.Reset();
        o.Roll.Reset();
        o.Pace = o.WasPace = 0.0f;
        o.Heave.Reset();
        o.Tip = 0.0f;
        o.Dust?.Reset();
        Vector3 at = CellWorld(cell);
        o.Rig.Position = new Vector3(at.X, OtherGround(at, out Vector3 up), at.Z);
        o.Up = up;
        o.Wet = _field.IsDeep(cell);
        o.Scuff.Lift();
        OtherPose(0.0f);
        _ram = RamPhase.None;
        _ramTouching = false;
    }

    private void BuildOther(string tag)
    {
        FreeOther();
        MovementProfile profile = ClassFor(tag);
        TankModel model = TankModel.Load(tag, toon: !_pbr);
        model.ScaleTo(PixelsFor(model, profile));
        var rig = new Node3D { Name = "Target" };
        AddChild(rig);
        rig.AddChild(model);
        model.Apply();
        var scuff = new CelRuts
        {
            Name = "Scuff",
            // A dragged belt presses no shoes: the strip and its walls only.
            Bar = new Color(0.0f, 0.0f, 0.0f, 0.0f),
            // Denser than a rut: ground torn by a dragged belt, not pressed.
            Soil = new Color(0.18f, 0.14f, 0.10f, 0.42f), Wall = new Color(0.12f, 0.09f, 0.07f, 0.55f),
        };
        AddChild(scuff);
        var hits = new CelHit { Name = "TargetHits" };
        AddChild(hits);
        hits.Build(model.HullLength * model.PixelsPerUnit);
        hits.Targets(model, model.Cel);
        hits.Solids = _solids;
        CelDust? dust = null;
        if (!_fx2d)
        {
            dust = new CelDust { Name = "TargetDust", Solids = _solids };
            AddChild(dust);
            dust.Build(model.HullLength * model.PixelsPerUnit);
        }
        _other = new Other
        {
            Model = model, Rig = rig, Profile = profile, Tag = tag.ToUpperInvariant(),
            Foot = MeasureFootprint(model, rig, tag.ToUpperInvariant()), Scuff = scuff, Hits = hits,
            Dust = dust,
        };
        GD.Print($"tank3d: target {_other.Tag} class {profile.Tag} x{profile.Size:F2}, mass {profile.Mass}");
    }

    private void FreeOther()
    {
        if (_other is null)
            return;
        _other.Rig.QueueFree();
        _other.Scuff.QueueFree();
        _other.Hits.QueueFree();
        _other.Dust?.QueueFree();
        _other = null;
        _ram = RamPhase.None;
    }

    /// <summary>The panel's pick and <c>--do target=</c>: another model for the
    /// target, on the cell and heading it has now.</summary>
    private void PickOther(string tag)
    {
        _otherTag = tag;
        if (_other is not null && !RamRunning)
            PlaceOther(_other.Cell, _other.Heading);
    }

    /// <summary>The target off the board, and the ram with it.</summary>
    private void RemoveOther()
    {
        FreeOther();
        _otherCell = null;
        _ramNote = "";
        _ramScripted = false;
    }

    /// <summary>A cell's middle in the world, on the datum (<see cref="Foot"/>
    /// or <see cref="OtherGround"/> gives it its height).</summary>
    private Vector3 CellWorld(Vector2I cell)
    {
        Vector2 flat = _field!.FlatAnchor(cell) + _field.CentreOffset;
        return new Vector3(flat.X, 0.0f, flat.Y / Squash);
    }

    /// <summary>The way across one flat side of a cell, in the world - the same
    /// for every cell: the board's hexes are regular there.</summary>
    private Vector3 HexWay(Vector2I cell, int heading)
    {
        Vector3 d = CellWorld(HexField.Step(cell, heading)) - CellWorld(cell);
        d.Y = 0.0f;
        return d.Normalized();
    }

    /// <summary>The bench's heading for a way on the ground.</summary>
    private static float HeadingOf(Vector3 way) => Mathf.RadToDeg(Mathf.Atan2(way.X, way.Z));

    /// <summary>
    /// The ground under the target and its up: the face it is on, as
    /// <see cref="Settle"/> takes it, and in deep water the drawn bed - a hull
    /// thrown into the pond goes to the bottom (the board's drowning, without
    /// the swim: the target has no engine to swim with).
    /// </summary>
    private float OtherGround(Vector3 w, out Vector3 up)
    {
        up = Vector3.Up;
        if (_field is null)
            return 0.0f;
        Vector2 flat = Board(w);
        Vector2I cell = _field.CellUnder(flat);
        if (_field.IsDeep(cell) && _field.RampHeading(cell) < 0)
            return _field.BedAt(cell) / RiseFactor;
        float d = 0.25f * HexWidth;
        float Y(Vector2 f) => _field.TopOn(cell, f) / RiseFactor;
        float sx = (Y(flat + new Vector2(d, 0.0f)) - Y(flat - new Vector2(d, 0.0f))) / (2.0f * d);
        float sz = (Y(flat + new Vector2(0.0f, d * Squash)) - Y(flat - new Vector2(0.0f, d * Squash))) / (2.0f * d);
        up = new Vector3(-sx, 1.0f, -sz).Normalized();
        return Y(flat);
    }

    // --- the footprints -------------------------------------------------------

    /// <summary>A hull's footprint on the ground: its middle and its two axes
    /// (world XZ) and half-lengths.</summary>
    private readonly record struct Box(Vector2 C, Vector2 A, Vector2 L, float HalfLen, float HalfWide);

    private static Box BoxOf(Vector3 at, float headingDeg,
                             (float Along, float Across, float HalfLen, float HalfWide) foot, float grow = 0.0f)
    {
        float h = Mathf.DegToRad(headingDeg);
        var a = new Vector2(Mathf.Sin(h), Mathf.Cos(h));
        // The hull's left, as _hullLeft has it: (ahead.z, -ahead.x).
        var l = new Vector2(a.Y, -a.X);
        Vector2 c = new Vector2(at.X, at.Z) + a * foot.Along + l * foot.Across;
        return new Box(c, a, l, foot.HalfLen + grow, foot.HalfWide + grow);
    }

    /// <summary>Two footprints overlapping - the separating axes of two
    /// rectangles, which are their four sides.</summary>
    private static bool Overlap(in Box p, in Box q)
    {
        foreach (Vector2 ax in new[] { p.A, p.L, q.A, q.L })
        {
            float rp = p.HalfLen * Mathf.Abs(p.A.Dot(ax)) + p.HalfWide * Mathf.Abs(p.L.Dot(ax));
            float rq = q.HalfLen * Mathf.Abs(q.A.Dot(ax)) + q.HalfWide * Mathf.Abs(q.L.Dot(ax));
            if (Mathf.Abs((p.C - q.C).Dot(ax)) > rp + rq)
                return false;
        }
        return true;
    }

    /// <summary>The point of a footprint nearest another point.</summary>
    private static Vector2 Nearest(in Box b, Vector2 p)
    {
        Vector2 d = p - b.C;
        return b.C + b.A * Mathf.Clamp(d.Dot(b.A), -b.HalfLen, b.HalfLen)
                   + b.L * Mathf.Clamp(d.Dot(b.L), -b.HalfWide, b.HalfWide);
    }

    private Box OtherBox(float grow = 0.0f) => BoxOf(_other!.Rig.Position, _other.Heading, _other.Foot, grow);

    /// <summary>
    /// The drive's step against the target: <paramref name="next"/> brought
    /// back to where the two footprints touch, and the ram - or, below
    /// <see cref="RamFloor"/> and while they are still touching from the last
    /// one, a hull that simply will not go through another. False when the step
    /// meets nothing.
    /// </summary>
    private bool RamMeets(ref Vector3 next)
    {
        if (_other is null)
            return false;
        Box them = OtherBox();
        if (_ramTouching && !Overlap(BoxOf(_rig.Position, _heading, _foot, 3.0f), them))
            _ramTouching = false;
        if (!Overlap(BoxOf(next, _heading, _foot), them))
            return false;
        Vector3 from = _rig.Position;
        if (Overlap(BoxOf(from, _heading, _foot), them))
        {
            // Turned into it on the spot: no further in.
            next = from;
            _speed = 0.0f;
            return true;
        }
        float lo = 0.0f, hi = 1.0f;
        for (int i = 0; i < 10; i++)
        {
            float mid = 0.5f * (lo + hi);
            if (Overlap(BoxOf(from.Lerp(next, mid), _heading, _foot), them))
                hi = mid;
            else
                lo = mid;
        }
        next = from.Lerp(next, lo);
        if (!_ramTouching && _fate == Fate.Alive && Mathf.Abs(_speed) >= RamFloor * MaxSpeed)
            Contact(next);
        else
            _speed = 0.0f;
        _ramTouching = true;
        return true;
    }

    // --- the meeting ---------------------------------------------------------

    /// <summary>
    /// The hulls meet with the rammer at <paramref name="at"/>: the metal, the
    /// springs and the view, whatever the rules then say (the board's order -
    /// two tanks met, and a refusal is about what follows), then the rules, and
    /// the push or the stop.
    /// </summary>
    private void Contact(Vector3 at)
    {
        Other o = _other!;
        // The ram goes along a flat side of the target's hex, the one looking
        // back at the rammer - on the board the last leg of a route is a step on
        // to a neighbour, a side by construction; here the way in is anybody's,
        // and the side nearest it is the step it stands for.
        Vector3 toward = o.Rig.Position - at;
        toward.Y = 0.0f;
        int heading = HexField.EdgeHeadings[0];
        float best = float.MinValue;
        foreach (int h in HexField.EdgeHeadings)
        {
            float dot = HexWay(o.Cell, h).Dot(toward);
            if (dot > best)
            {
                best = dot;
                heading = h;
            }
        }
        Vector3 way = HexWay(o.Cell, heading);
        double keep = TankTick.RamKeep(_profile, o.Profile);

        Box me = BoxOf(at, _heading, _foot), them = OtherBox();
        Vector2 seam2 = 0.5f * (Nearest(me, them.C) + Nearest(them, me.C));
        var seamFlat = new Vector3(seam2.X, 0.0f, seam2.Y);
        float hullPx = _model.HullLength * _model.PixelsPerUnit;
        Vector3 seam = Foot(seamFlat) + Vector3.Up * (TankTick.RamSparkHigh * hullPx);
        Fans(seam, way, hullPx * TankTick.RamSparkFor(_profile));
        Dents(me, them, seam.Y, way, o);

        // Each thrown by the speed it changed by, the struck end down and, on a
        // side, the struck side down - the roll the board has not got.
        float h0 = Mathf.DegToRad(_heading);
        var ahead = new Vector3(Mathf.Sin(h0), 0.0f, Mathf.Cos(h0));
        var left = new Vector3(ahead.Z, 0.0f, -ahead.X);
        float mine = (float)(TankTick.RamJolt * (1.0 - keep));
        _kickPitch.Kick(mine * ahead.Dot(way));
        _kickRoll.Kick(-mine * left.Dot(way));
        var oLeft = new Vector3(o.Ahead.Z, 0.0f, -o.Ahead.X);
        float theirs = (float)(TankTick.RamJolt * keep);
        o.Pitch.Kick(theirs * o.Ahead.Dot(-way));
        o.Roll.Kick(-theirs * oLeft.Dot(-way));
        // The ram's channel on the board: one kick along the ram, unnormalised
        // so a ram into the screen shakes less than one across it, and the
        // ring after it (TankTick.Shakes, Shook.Ram).
        _shake.Fire(Drawn(way), _profile.ShotShake);
        _shake.Blast(_profile.ShotShake * TankTick.RamRumble);

        Vector2I mineCell = _field!.FlatCellAt(Board(at));
        Ramming.Shove push = Ramming.Of(_field, c => c == mineCell, _profile, o.Profile, o.Cell, heading);
        if (!push.Allowed)
        {
            GD.Print($"ram: frame {_frame}, {_modelTag} does not shove {o.Tag} at {o.Cell} along {heading} - "
                     + Ramming.Because(push.Why));
            _ramNote = $"отказ: {Ramming.Because(push.Why)}";
            _speed = 0.0f;
            RamOver();
            return;
        }
        // Back along its own hull, to the point of that line nearest the
        // hex's middle: the board's rammer came in along the side it backs out
        // by, and one that came in at an angle would have slid sideways.
        {
            float hr = Mathf.DegToRad(_heading);
            var axis = new Vector3(Mathf.Sin(hr), 0.0f, Mathf.Cos(hr));
            Vector3 middle = CellWorld(o.Cell);
            _ramPark = at + axis * (middle - at).Dot(axis);
        }
        GD.Print($"ram: frame {_frame}, {_modelTag} shoves {o.Tag} {o.Cell} -> {string.Join(" -> ", push.Legs)} along {heading}, "
                 + $"keep {keep:F2}, at {Mathf.Abs(_speed):F0} px/s");
        _ramNote = $"{_modelTag} сдвинул {o.Tag}: {o.Cell} → {push.Legs[^1]}";
        o.Legs.Clear();
        foreach (Vector2I c in push.Legs)
            o.Legs.Add(CellWorld(c));
        o.Leg = 0;
        o.Cell = push.Legs[^1];
        o.Way = way;
        _otherCell = o.Cell;
        // The belts drag: across the way they go, as wide as they are across it.
        if (o.Model.Tracks.Count > 0)
        {
            TankModel.Track belt = o.Model.Tracks[0];
            float ppu = o.Model.PixelsPerUnit;
            var across = new Vector3(way.Z, 0.0f, -way.X);
            float len = belt.Path.Length > 0 ? PathLength(belt) * ppu : o.Foot.HalfLen * 2.0f;
            float wide = belt.Links.Multimesh.Mesh.GetAabb().Size.X * ppu;
            float span = Mathf.Abs(o.Ahead.Dot(across)) * len + Mathf.Abs(oLeft.Dot(across)) * wide;
            o.Scuff.Build(4.0f, Mathf.Max(span, wide));
            o.Scuff.Lift();
        }
        _ramGap = at - o.Rig.Position;
        _ramGap.Y = 0.0f;
        // Both leave at one speed - RamKeep: the rammer is checked to it and
        // the target started at it.
        _ramSpeed = Mathf.Abs(_speed) * (float)keep;
        _speed *= (float)keep;
        _ramCap = Mathf.Min(MaxSpeed, (float)o.Profile.TopSpeed) * (float)keep;
        _ram = RamPhase.Push;
    }

    /// <summary>How far a belt's path reaches along the hull - its contact
    /// patch's length, near enough.</summary>
    private static float PathLength(TankModel.Track belt)
    {
        float lo = float.MaxValue, hi = float.MinValue;
        foreach (Vector3 p in belt.Path)
        {
            lo = Mathf.Min(lo, p.Z);
            hi = Mathf.Max(hi, p.Z);
        }
        return hi - lo;
    }

    /// <summary>
    /// The metal off the seam: two fans, one out of each side of it along the
    /// seam and up. The board sprays each hull's along its own plate's normal;
    /// with depth that normal points into the other hull, which hides the fan,
    /// and the metal of two hulls ground together comes out of the seam's ends.
    /// Sized by the rammer's class for both (<see cref="TankTick.RamSparkFor"/>).
    /// </summary>
    private void Fans(Vector3 seam, Vector3 way, float sizePx)
    {
        if (_fx2d)
            return;
        if (_fans[0] is null || !Mathf.IsEqualApprox(_fansFor, sizePx))
        {
            for (int i = 0; i < _fans.Length; i++)
            {
                _fans[i]?.QueueFree();
                var fan = new CelHit { Name = $"RamFan{i}" };
                AddChild(fan);
                fan.Build(sizePx);
                _fans[i] = fan;
            }
            _fansFor = sizePx;
        }
        var along = new Vector3(way.Z, 0.0f, -way.X);
        for (int i = 0; i < _fans.Length; i++)
        {
            float side = i == 0 ? 1.0f : -1.0f;
            Vector3 plate = i == 0 ? way : -way;
            Vector3 glance = along * side + Vector3.Up * 0.55f + plate * 0.2f;
            _fans[i]!.Scrape(seam, (along * side + Vector3.Up * 0.3f).Normalized(), glance);
        }
    }

    /// <summary>
    /// The dents the meeting leaves, on both hulls - <b>cosmetic</b>: the rules
    /// say a ram only moves ("повреждений ни один из двух не получает"), so
    /// nothing is counted and nothing is knocked out; the metal is only shown
    /// to have met. Sized by the board's own table read the way
    /// <c>--ram-dents</c> reads it (<see cref="Gunnery.RamLevel"/>, each way
    /// round): a level of one presses a dent, nought only scrapes the paint - a
    /// light hull running into a heavy one scratches it. The rammer's class
    /// sizes both, as it sizes the sparks (<see cref="TankTick.RamSparkFor"/>).
    ///
    /// <b>The shape is what struck it</b>: the other hull's face that met this
    /// one, whose edge presses a trough along itself - as long as the two
    /// footprints overlap along that edge, which is where they touched. Nose
    /// on into a side, the rammer's whole bow edge; at an angle a short one,
    /// nearly a wedge.
    /// </summary>
    private void Dents(in Box me, in Box them, float seamY, Vector3 way, Other o)
    {
        float scale = TankTick.RamSparkFor(_profile) / TankTick.RamSpark;
        var flat = new Vector2(way.X, way.Z);
        // The rammer's face that struck and the target's that was struck.
        Vector2 mine = Face(me, flat), theirs = Face(them, -flat);
        // Each runs at the height of what struck it: the part of the other hull
        // that stands furthest toward it - the rammer's bow edge, the target's
        // skirts. At the seam's one height (the board's belt line) the trough
        // lay on a skirt's top edge, its upper wall off on the belt, and the
        // dent was a pale band with no shade to it.
        float band = 0.03f * _model.HullLength * _model.PixelsPerUnit;
        float byMe = _celHit?.Leading(way, band) ?? seamY;
        float byThem = o.Hits.Leading(-way, band) ?? seamY;
        // Each dent's plate is asked of the struck hull's own side: the one of
        // its footprint looking back at the other hull.
        Press(o.Hits, Touch(me, them, mine, byMe), way, Out(them, -flat),
              Gunnery.RamLevel(_profile, o.Profile), scale, o.Model.HullLength * o.Model.PixelsPerUnit);
        Press(_celHit, Touch(me, them, theirs, byThem), -way, Out(me, flat),
              Gunnery.RamLevel(o.Profile, _profile), scale, _model.HullLength * _model.PixelsPerUnit);
    }

    /// <summary>The way along the side of a footprint that looks most along
    /// <paramref name="toward"/> - its edge, flat.</summary>
    private static Vector2 Face(in Box b, Vector2 toward)
    {
        // Endwise, the edge runs across the hull; broadside, along it.
        return Mathf.Abs(b.A.Dot(toward)) >= Mathf.Abs(b.L.Dot(toward)) ? b.L : b.A;
    }

    /// <summary>That side's outward normal, world, flat.</summary>
    private static Vector3 Out(in Box b, Vector2 toward)
    {
        Vector2 o = Mathf.Abs(b.A.Dot(toward)) >= Mathf.Abs(b.L.Dot(toward)) ? b.A : b.L;
        if (o.Dot(toward) < 0.0f)
            o = -o;
        return new Vector3(o.X, 0.0f, o.Y);
    }

    /// <summary>Where the two footprints touch along an edge: the middle of
    /// their overlap on it (world, at the seam's height) and its half-length.</summary>
    private static (Vector3 At, float Half, Vector2 Edge) Touch(in Box p, in Box q, Vector2 edge, float y)
    {
        (float lo, float hi) Span(in Box b)
        {
            float c = b.C.Dot(edge), e = b.HalfLen * Mathf.Abs(b.A.Dot(edge)) + b.HalfWide * Mathf.Abs(b.L.Dot(edge));
            return (c - e, c + e);
        }
        var (plo, phi) = Span(p);
        var (qlo, qhi) = Span(q);
        float lo = Mathf.Max(plo, qlo), hi = Mathf.Min(phi, qhi);
        float mid = 0.5f * (lo + hi);
        // Across the edge: halfway between the two hulls' middles' lines.
        var normal = new Vector2(-edge.Y, edge.X);
        float off = 0.5f * (p.C.Dot(normal) + q.C.Dot(normal));
        Vector2 at = edge * mid + normal * off;
        // The seam between the two faces rather than the hulls' middles: the
        // nearest points of each, across the edge.
        Vector2 a = Nearest(p, at), b = Nearest(q, at);
        at = edge * mid + normal * (0.5f * (a.Dot(normal) + b.Dot(normal)));
        return (new Vector3(at.X, y, at.Y), Mathf.Max(0.5f * (hi - lo), 0.0f), edge);
    }

    /// <summary>
    /// One hull's mark: a line from where they touched into it along the ram,
    /// on the first plate it meets that looks back the way the blow came
    /// (<paramref name="side"/>, its footprint's struck side) - raised a step at
    /// a time and moved along the seam while what it meets first is a belt, or
    /// the end of a skirt panel: a thin plate's end looks along the hull, and a
    /// trough laid on it went across the side and spilt over every panel of
    /// that paint. A trough along the striking edge where the table says it
    /// dents, a scratch of paint where it does not.
    /// </summary>
    private static void Press(CelHit? hits, (Vector3 At, float Half, Vector2 Edge) touch, Vector3 way,
                              Vector3 side, int level, float scale, float hullPx)
    {
        if (hits is null)
            return;
        var axis = new Vector3(touch.Edge.X, 0.0f, touch.Edge.Y);
        foreach (float up in new[] { 0.0f, 0.08f, 0.16f, 0.24f })
            foreach (float along in new[] { 0.0f, 0.3f, -0.3f, 0.6f, -0.6f })
            {
                Vector3 from = touch.At + Vector3.Up * (up * hullPx) + axis * (along * touch.Half)
                               - way * (0.5f * hullPx);
                if (hits.Cast(from, way) is not { } hit)
                    continue;
                // Facing the blow within about fifty degrees, up or down (a
                // sloped glacis) or round (a skirt canted out).
                if (hit.N.Dot(side) < 0.64f)
                    continue;
                // The plate's slope from the mesh, its bearing from the side:
                // the mesh's own normal at one point may be a bevel's - LTR's
                // skirt panels are cut at 45 degrees at their ends - and a
                // trough laid in a bevel's plane was clipped square across the
                // side it spilt on to.
                float rise = Mathf.Clamp(hit.N.Y, -0.95f, 0.95f);
                Vector3 n = (side * Mathf.Sqrt(1.0f - rise * rise) + Vector3.Up * rise).Normalized();
                if (level >= 1)
                    hits.Dent(hit.Part, hit.At, n, axis, touch.Half, scale);
                else
                    hits.Leave(hit.Part, hit.At, n, way, CelHit.Kind.Gouge, 0.8f);
                return;
            }
    }

    /// <summary>The ram over, one way or the other: the script lets go.</summary>
    private void RamOver()
    {
        _ram = RamPhase.None;
        if (_ramScripted)
        {
            _driveScripted = 0.0f;
            _ramScripted = false;
        }
    }

    // --- the push, the wait and the back-off -----------------------------------

    /// <summary>
    /// The rammer's frame while a ram runs, in place of the drive: pushing the
    /// target across the hex, waiting against it, backing on to the middle of
    /// the hex it took. Moves the rig, sets <c>_speed</c> (along the hull) and
    /// returns the belts' step, model units.
    /// </summary>
    private float RamDrive(float dt)
    {
        Other? o = _other;
        if (o is null)
        {
            RamOver();
            return 0.0f;
        }
        float h = Mathf.DegToRad(_heading);
        var ahead = new Vector3(Mathf.Sin(h), 0.0f, Mathf.Cos(h));
        Vector3 moved = Vector3.Zero;
        switch (_ram)
        {
            case RamPhase.Push:
            {
                Vector3 d = o.Legs[0] - o.Rig.Position;
                d.Y = 0.0f;
                float rem = d.Length();
                // One speed for the pair, under the lower of the two tops times
                // the keep, and down to nothing at the hex's middle.
                _ramSpeed = Mathf.MoveToward(_ramSpeed, _ramCap, Accel * dt);
                _ramSpeed = Mathf.Min(_ramSpeed, Mathf.Sqrt(2.0f * Accel * rem) + 1.0f);
                float move = Mathf.Min(_ramSpeed * dt, rem);
                Vector3 shift = rem > 1e-3f ? d / rem * move : Vector3.Zero;
                o.Rig.Position += shift;
                o.Slid += move;
                o.Speed = _ramSpeed;
                Vector3 to = o.Rig.Position + _ramGap;
                moved = new Vector3(to.X - _rig.Position.X, 0.0f, to.Z - _rig.Position.Z);
                _rig.Position = new Vector3(to.X, _rig.Position.Y, to.Z);
                if (rem - move < 0.5f)
                {
                    o.Rig.Position = new Vector3(o.Legs[0].X, o.Rig.Position.Y, o.Legs[0].Z);
                    o.Leg = 1;
                    _ram = RamPhase.Dwell;
                    _ramClock = 0.0f;
                    _ramSpeed = 0.0f;
                }
                break;
            }
            case RamPhase.Dwell:
                _ramClock += dt;
                if (_ramClock >= TankTick.RamDwell)
                    _ram = RamPhase.Back;
                break;
            case RamPhase.Back:
            {
                Vector3 d = _ramPark - _rig.Position;
                d.Y = 0.0f;
                float rem = d.Length();
                _ramSpeed = Mathf.MoveToward(_ramSpeed, (float)TankTick.RamBack * MaxSpeed, Accel * dt);
                _ramSpeed = Mathf.Min(_ramSpeed, Mathf.Sqrt(2.0f * Accel * rem) + 1.0f);
                float move = Mathf.Min(_ramSpeed * dt, rem);
                moved = rem > 1e-3f ? d / rem * move : Vector3.Zero;
                _rig.Position += moved;
                if (rem - move < 0.5f)
                {
                    _rig.Position = new Vector3(_ramPark.X, _rig.Position.Y, _ramPark.Z);
                    _ramSpeed = 0.0f;
                    RamOver();
                }
                break;
            }
        }
        _speed = dt > 0.0f ? moved.Dot(ahead) / dt : 0.0f;
        return moved.Dot(ahead) / _model.PixelsPerUnit;
    }

    // --- the target's frame -----------------------------------------------------

    /// <summary>
    /// The target's frame: a slide on its own after the push (a ramp taken from
    /// above), its fall off a bank and into the pond, its springs, its pose, and
    /// the scuff of its locked belts.
    /// </summary>
    private void OtherTick(float dt)
    {
        Other? o = _other;
        if (o is null)
            return;
        o.Slid = 0.0f;
        if (_ram == RamPhase.Push)
            o.Slid = o.Speed * dt;
        else if (o.Leg >= 1 && o.Leg < o.Legs.Count)
        {
            // Down the slope and on to its foot, braking to the middle of it.
            float rest = 0.0f;
            for (int i = o.Leg; i < o.Legs.Count; i++)
                rest += (o.Legs[i] - (i == o.Leg ? o.Rig.Position : o.Legs[i - 1])).Length();
            o.Speed = Mathf.Min(o.Speed, Mathf.Sqrt(2.0f * (float)o.Profile.Accel * rest) + 1.0f);
            Vector3 d = o.Legs[o.Leg] - o.Rig.Position;
            d.Y = 0.0f;
            float rem = d.Length();
            float move = Mathf.Min(o.Speed * dt, rem);
            if (rem > 1e-3f)
                o.Rig.Position += d / rem * move;
            o.Slid = move;
            if (rem - move < 0.5f)
            {
                o.Rig.Position = new Vector3(o.Legs[o.Leg].X, o.Rig.Position.Y, o.Legs[o.Leg].Z);
                o.Leg++;
                if (o.Leg >= o.Legs.Count)
                    o.Speed = 0.0f;
            }
        }

        // The ground: followed on a face, fallen to off a bank.
        float ground = OtherGround(o.Rig.Position, out Vector3 up);
        float y = o.Rig.Position.Y;
        Vector3 way = new(o.Way.X, 0.0f, o.Way.Z);
        way = way.LengthSquared() > 1e-6f ? way.Normalized() : o.Ahead;
        float reach = ReachAlong(o, way);
        o.Tip = 0.0f;
        if (y > ground + 0.5f)
        {
            if (!o.Falling)
            {
                o.Falling = true;
                o.FallV = 0.0f;
            }
            o.FallV += FallGravity * dt;
            y -= o.FallV * dt;
            // Over the brink: the trailing end still on the bank holds itself
            // up on its edge while the middle goes down, so the hull turns
            // about the edge, leading end down, as far as the middle has
            // dropped below it - and no further than the ground under the
            // leading end lets it. Off the bank's edge, it is let go. Fallen
            // level as a whole, it hung half over the drop and then came down
            // flat, a lift going down.
            float bank = OtherGround(o.Rig.Position - way * reach, out _);
            if (bank > y + 0.5f && reach > 1.0f)
            {
                float below = OtherGround(o.Rig.Position + way * reach, out _);
                float tip = Mathf.Min((bank - y) / reach, Mathf.Max(0.0f, (y - below) / reach));
                o.Tip = Mathf.Clamp(tip, 0.0f, Mathf.Sin(TipMost));
            }
            if (y <= ground + 0.5f)
            {
                y = ground;
                o.Falling = false;
                Land(o, way, o.FallV / LandAt);
            }
        }
        else
        {
            // Within half a px of it is on it: a fall that ended a frame
            // there, not under, used to come down this way and stay falling,
            // never landed - no kick, the up held level, every bank since.
            if (o.Falling)
            {
                o.Falling = false;
                Land(o, way, o.FallV / LandAt);
            }
            y = ground;
        }
        o.Rig.Position = new Vector3(o.Rig.Position.X, y, o.Rig.Position.Z);
        // Tipped, the up leans the way it falls: the hull's line along the
        // way goes down ahead. Held to the brink fast - it is the edge that
        // holds it - and back to the ground's face as before.
        Vector3 want = !o.Falling ? up
                     : (Vector3.Up * Mathf.Sqrt(1.0f - o.Tip * o.Tip) + way * o.Tip).Normalized();
        o.Up = o.Up.Lerp(want, 1.0f - Mathf.Exp(-(o.Falling ? 30.0f : 10.0f) * dt)).Normalized();

        // Into the pond: the plunge, off the hex it went into.
        Vector2I cell = _field?.FlatCellAt(Board(o.Rig.Position)) ?? o.Cell;
        bool wet = _field is not null && _field.InBounds(cell) && _field.IsDeep(cell);
        if (wet && !o.Wet && _field is not null)
        {
            float top = _field.WaterTop(cell);
            Vector3 mid = CellWorld(cell);
            _splash?.Fire(new Vector3(o.Rig.Position.X, top / RiseFactor, o.Rig.Position.Z), o.Way,
                          o.Foot.HalfLen, o.Foot.HalfWide, 0.7f + 0.25f * Math.Max(0, o.Profile.Mass - 1),
                          new Vector3(mid.X, top / RiseFactor, mid.Z), HexWidth * 0.5f);
            _ripples.Strike(new Vector2(o.Rig.Position.X, o.Rig.Position.Z), Stage3D.Waves);
            GD.Print($"tank3d: target {o.Tag} thrown into the pond at {cell}");
        }
        o.Wet = wet;

        o.WasPace = o.Pace;
        o.Pace = dt > 0.0f && !o.Falling
            ? Mathf.Clamp(o.Slid / dt / (RamLeanAt * (float)o.Profile.TopSpeed), 0.0f, 1.0f) : 0.0f;
        // Stopped dead: the hull goes on, past its lean, and rocks back.
        if (o.WasPace > 0.0f && o.Pace <= 0.0f && !o.Falling)
        {
            Vector3 w = new(o.Way.X, 0.0f, o.Way.Z), l = new(o.Ahead.Z, 0.0f, -o.Ahead.X);
            o.Pitch.Kick(RamSettle * o.Ahead.Dot(w));
            o.Roll.Kick(-RamSettle * l.Dot(w));
        }
        OtherPose(dt);
        Scuff(dt, o);
        Plough(dt, o);
    }

    /// <summary>The target's rig and model as it stands now.</summary>
    private void OtherPose(float dt)
    {
        Other o = _other!;
        float h = Mathf.DegToRad(o.Heading);
        var ahead = new Vector3(Mathf.Sin(h), 0.0f, Mathf.Cos(h));
        Vector3 up = o.Up;
        Vector3 z = (ahead - up * ahead.Dot(up)).Normalized();
        o.Rig.Basis = new Basis(up.Cross(z), up, z);
        // Leaning the way it goes while it is dragged: + pitch is the nose
        // down, + roll the left side up (the meeting's kick, Contact, reads
        // them the same way).
        var left = new Vector3(ahead.Z, 0.0f, -ahead.X);
        Vector3 way = new(o.Way.X, 0.0f, o.Way.Z);
        // Whole while it is dragged at all, gone at the stop: the push brakes
        // to the hex's middle, and a lean following the pace down went to
        // level as gently, with nothing to rock back from.
        float lean = RamLean * Mathf.SmoothStep(0.0f, 0.15f, o.Pace);
        if (o.Pace > 0.0f)
        {
            o.ShudderIn -= dt;
            if (o.ShudderIn <= 0.0f)
            {
                o.ShudderIn += RamShudderEvery;
                int k = o.Shudders++;
                o.Pitch.Kick(RamShudder * o.Pace * (2.0f * CelPuff.Hash(k, 83) - 1.0f));
                o.Roll.Kick(RamShudder * o.Pace * (2.0f * CelPuff.Hash(k, 89) - 1.0f));
            }
        }
        o.Model.Pitch = o.Pitch.Step(dt, lean * ahead.Dot(way));
        o.Model.Roll = o.Roll.Step(dt, -lean * left.Dot(way));
        o.Model.Heave = o.Heave.Step(dt);
        o.Model.Apply();
    }

    /// <summary>The locked belts dragged over the ground: a stitch per few px
    /// of the drag at each belt's middle, across the way it goes.</summary>
    private void Scuff(float dt, Other o)
    {
        o.Belts.Clear();
        var across = new Vector3(o.Way.Z, 0.0f, -o.Way.X);
        if (across.LengthSquared() < 1e-6f)
            across = new Vector3(o.Ahead.Z, 0.0f, -o.Ahead.X);
        Vector3 ahead = o.Ahead, left = new(ahead.Z, 0.0f, -ahead.X);
        foreach (TankModel.Track t in o.Model.Tracks)
        {
            Vector3 off = t.Node.GlobalPosition - o.Rig.GlobalPosition;
            Vector3 at = Foot(o.Rig.GlobalPosition + left * off.Dot(left) + ahead * off.Dot(ahead));
            Vector2I cell = _field?.FlatCellAt(Board(at)) ?? Vector2I.Zero;
            bool water = _field?.IsWater(cell) ?? false;
            o.Belts.Add(new CelRuts.Belt(at, Stage3D.Clear(Squash, RiseFactor), across,
                                         o.Slid, !water && !o.Falling && o.Slid > 0.0f, false));
        }
        o.Scuff.Tick(dt, o.Belts);
    }

    /// <summary>
    /// The dust the dragged belts plough up: at the leading edge of each belt,
    /// the side that goes first, at both its ends and a little past them - a
    /// belt dragged sideways is a blade its whole length, and the soil it
    /// pushes spills off its ends. Along the edge alone the dust was under the
    /// hull or behind the far belt, out of sight, and what was under the hull
    /// <see cref="CelSolids"/> thinned away. Thrown on the way it goes and out
    /// past the belt's ends. None on water or in the air.
    /// </summary>
    private void Plough(float dt, Other o)
    {
        if (o.Dust is null)
            return;
        o.Ploughs.Clear();
        Vector3 way = new(o.Way.X, 0.0f, o.Way.Z);
        if (way.LengthSquared() < 1e-6f)
            way = Vector3.Forward;
        way = way.Normalized();
        Vector3 ahead = o.Ahead, left = new(ahead.Z, 0.0f, -ahead.X);
        float ppu = o.Model.PixelsPerUnit;
        foreach (TankModel.Track t in o.Model.Tracks)
        {
            Vector3 off = t.Node.GlobalPosition - o.Rig.GlobalPosition;
            Vector3 mid = o.Rig.GlobalPosition + left * off.Dot(left) + ahead * off.Dot(ahead);
            float len = (t.Path.Length > 0 ? PathLength(t) : o.Model.HullLength) * ppu;
            float wide = t.Links.Multimesh.Mesh.GetAabb().Size.X * ppu;
            // The belt's edge that leads: half its width out along the way, as
            // the belt's box has it across and along.
            Vector3 edge = left * (Mathf.Sign(left.Dot(way)) * 0.5f * wide * Mathf.Abs(left.Dot(way)))
                           + ahead * (Mathf.Sign(ahead.Dot(way)) * 0.5f * len * Mathf.Abs(ahead.Dot(way)));
            foreach (float end in new[] { 0.52f, -0.52f })
            {
                Vector3 at = Foot(mid + edge + ahead * (end * len));
                bool wet = _field?.IsWater(_field.FlatCellAt(Board(at))) ?? false;
                bool dusty = !wet && !o.Falling && o.Slid > 0.0f;
                o.Ploughs.Add(new CelDust.Belt(at, way, end > 0.0f ? ahead : -ahead,
                                               dusty ? o.Slid : 0.0f, o.Pace));
            }
        }
        o.Dust.Tick(dt, o.Ploughs, _camera.GlobalBasis);
    }

    /// <summary>How far the target's footprint reaches from its middle along
    /// <paramref name="way"/> (flat, unit), px.</summary>
    private static float ReachAlong(Other o, Vector3 way)
    {
        Vector3 left = new(o.Ahead.Z, 0.0f, -o.Ahead.X);
        return Mathf.Abs(o.Ahead.Dot(way)) * o.Foot.HalfLen + Mathf.Abs(left.Dot(way)) * o.Foot.HalfWide;
    }

    /// <summary>
    /// The target down off a bank, at <paramref name="hard"/> of one landing:
    /// the hull sinks on its suspension and bounces, the end and side that
    /// came down first are thrown down, the view gets a little of it (the
    /// board's landing, TankTick.Bumped) and a ring of dust goes out over the
    /// ground round the hull - none into water.
    /// </summary>
    private void Land(Other o, Vector3 way, float hard)
    {
        float s = Mathf.Clamp(hard, 0.3f, 1.6f);
        Vector3 left = new(o.Ahead.Z, 0.0f, -o.Ahead.X);
        o.Heave.Kick(-LandSink * o.Model.HullLength * Mathf.Sqrt(SinkSpring.K) * s);
        o.Pitch.Kick(LandTip * s * o.Ahead.Dot(way));
        o.Roll.Kick(-LandTip * s * left.Dot(way));
        _shake.Fire(new Vector2(0.0f, 1.0f), o.Profile.ShotShake * 0.45 * s);
        o.Scuff.Lift();
        GD.Print($"tank3d: target {o.Tag} lands at {o.FallV:F0} px/s ({hard:F2} of a landing)");
        if (o.Dust is null || _field is null)
            return;
        Vector2I cell = _field.FlatCellAt(Board(o.Rig.Position));
        if (_field.IsWater(cell))
            return;
        // Round the footprint, a little out from it: under the hull,
        // CelSolids thins a puff away.
        var spots = new List<CelDust.Belt>();
        Vector3 mid = o.Rig.Position + o.Ahead * o.Foot.Along + left * o.Foot.Across;
        float grow = 0.06f * o.Model.HullLength * o.Model.PixelsPerUnit;
        const int n = 16;
        for (int i = 0; i < n; i++)
        {
            float a = Mathf.Tau * (i + 0.5f * CelPuff.Hash(i, 97)) / n;
            // A point on the rectangle the way a, and the way out of it there.
            float ca = Mathf.Cos(a), sa = Mathf.Sin(a);
            float hl = o.Foot.HalfLen + grow, hw = o.Foot.HalfWide + grow;
            float k = 1.0f / Mathf.Max(Mathf.Abs(ca) / hl, Mathf.Abs(sa) / hw);
            Vector3 at = mid + o.Ahead * (ca * k) + left * (sa * k);
            Vector3 outward = (o.Ahead * ca + left * sa).Normalized();
            Vector3 along = new(outward.Z, 0.0f, -outward.X);
            spots.Add(new CelDust.Belt(Foot(at), outward, along, 0.0f, LandDust * s));
        }
        o.Dust.Burst(spots, 8, LandLift);
    }

    // --- setting it up ----------------------------------------------------------

    /// <summary>
    /// A ram lined up: the rammer on the middle of its cell facing the side of
    /// it nearest its heading along which there is room to run a hex, and the
    /// target two hexes that way, broadside on. What the ground behind the
    /// target is, is left to it - that is what the rules are asked.
    /// </summary>
    private bool LineUp()
    {
        if (_field is null || RamRunning)
            return false;
        Vector2I here = _field.FlatCellAt(Board(_rig.Position));
        var tried = new List<int>(HexField.EdgeHeadings);
        float h = Mathf.DegToRad(_heading);
        var ahead = new Vector3(Mathf.Sin(h), 0.0f, Mathf.Cos(h));
        tried.Sort((a, b) => HexWay(here, b).Dot(ahead).CompareTo(HexWay(here, a).Dot(ahead)));
        foreach (int heading in tried)
        {
            Vector2I one = HexField.Step(here, heading), two = HexField.Step(one, heading);
            if (!_field.InBounds(one) || !_field.InBounds(two)
                || !_field.Passable(here, heading) || !_field.Passable(one, heading)
                || _field.IsWater(one) || _field.IsWater(two))
                continue;
            Vector3 way = HexWay(here, heading);
            ResetTank();
            _rig.Position = Foot(CellWorld(here));
            Heading = HeadingOf(way);
            Settle(0.0f, snap: true);
            _ruts?.Lift();
            PlaceOther(two, HeadingOf(way) + 90.0f);
            _ramNote = $"цель {_otherTag} на {two}, таран вдоль {heading}";
            return true;
        }
        _ramNote = "некуда поставить цель: нет двух гексов прямо";
        return false;
    }

    /// <summary><c>--do ram</c>, the panel's button and <c>T</c>: lined up if
    /// there is no target yet, then full ahead until the ram is over.</summary>
    private void RamGo()
    {
        if (RamRunning || _fate != Fate.Alive)
            return;
        if (_other is null && !LineUp())
            return;
        _ramScripted = true;
        _driveScripted = 1.0f;
        _turnScripted = 0.0f;
    }

    /// <summary>The ram put back as it was lined up: the target on its cell.</summary>
    private void RamReset()
    {
        _ram = RamPhase.None;
        _ramTouching = false;
        _ramScripted = false;
        foreach (CelHit? fan in _fans)
            fan?.Reset();
        if (_other is not null)
        {
            _other.Scuff.Clear();
            _other.Hits.Reset();
            _other.Dust?.Reset();
        }
    }

    private void RamFrame(float dt, Basis eye)
    {
        OtherTick(dt);
        _other?.Hits.Tick(dt, eye);
        foreach (CelHit? fan in _fans)
            fan?.Tick(dt, eye);
    }
}
