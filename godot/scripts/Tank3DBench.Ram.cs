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
        public readonly Spring Pitch = new(KickSpring.K, KickSpring.C);
        public readonly Spring Roll = new(KickSpring.K, KickSpring.C);
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
        public readonly List<CelRuts.Belt> Belts = new();

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
        _other = new Other
        {
            Model = model, Rig = rig, Profile = profile, Tag = tag.ToUpperInvariant(),
            Foot = MeasureFootprint(model, rig, tag.ToUpperInvariant()), Scuff = scuff,
        };
        GD.Print($"tank3d: target {_other.Tag} class {profile.Tag} x{profile.Size:F2}, mass {profile.Mass}");
    }

    private void FreeOther()
    {
        if (_other is null)
            return;
        _other.Rig.QueueFree();
        _other.Scuff.QueueFree();
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
        if (y > ground + 0.5f)
        {
            if (!o.Falling)
            {
                o.Falling = true;
                o.FallV = 0.0f;
            }
            o.FallV += FallGravity * dt;
            y -= o.FallV * dt;
            if (y <= ground)
            {
                y = ground;
                o.Falling = false;
                // Landed: the leading end down, the fall's number, and a little
                // of the view (the board's landing, TankTick.Bumped).
                o.Pitch.Kick((float)TankTick.RamJolt * 0.6f * o.Ahead.Dot(o.Way));
                _shake.Fire(new Vector2(0.0f, 1.0f), o.Profile.ShotShake * 0.45);
                o.Scuff.Lift();
            }
        }
        else
            y = ground;
        o.Rig.Position = new Vector3(o.Rig.Position.X, y, o.Rig.Position.Z);
        o.Up = o.Up.Lerp(o.Falling ? Vector3.Up : up, 1.0f - Mathf.Exp(-10.0f * dt)).Normalized();

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

        OtherPose(dt);
        Scuff(dt, o);
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
        o.Model.Pitch = o.Pitch.Step(dt);
        o.Model.Roll = o.Roll.Step(dt);
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
            _other.Scuff.Clear();
    }

    private void RamFrame(float dt, Basis eye)
    {
        OtherTick(dt);
        foreach (CelHit? fan in _fans)
            fan?.Tick(dt, eye);
    }
}
