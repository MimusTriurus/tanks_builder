using System;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The model in deep water: it swims, and it drowns - GDD states.md «Плывёт» and
/// «Утоплен», the sprite board's <see cref="TankTick.Buoyancy"/>,
/// <see cref="TankTick.SunkAt"/> and <see cref="Bubbles"/> carried over to a
/// hull that is really in the pond rather than drawn smaller.
///
/// <b>Depth is position here, and that is the one thing the sprite could not
/// have.</b> The board draws deep water as a pit (<see cref="HexField.DeepBed"/>
/// a level under the cell's own) and the pond's surface reads its thickness off
/// the depth buffer, so a model let down into it is covered by the water it is
/// in, tinted by how much of it is over each pixel - no waterline to measure,
/// no shrink (<see cref="Stage3D.Shrunk"/>). The heights are the sprite's own
/// numbers: afloat at <c>surface - draught</c> of the deck, floored at the drawn
/// bed; drowned, down to that bed over <see cref="TankTick.SinkSeconds"/>.
///
/// <b>In off any bank, out by the ramp alone</b> - that is
/// <see cref="HexField.Passable"/> and nothing here. The step off the bank is a
/// fall (<see cref="HexField.Drops"/>): the hull keeps the bank's height until
/// its middle is over the water, then drops, splashes (<see cref="Stage3D.Plunge"/>)
/// and bobs on <see cref="Buoy"/>'s spring.
/// </summary>
public sealed partial class Tank3DBench
{
    /// <summary><c>--no-amphibious</c>: no wading gear - deep water stops the
    /// engine and the tank drowns where it went in (<see cref="TankTick.Amphibious"/>).
    /// On by default, so a drive into the pond shows the swim and a knock-out
    /// there the drowning.</summary>
    private bool _amphibious = true;

    /// <summary><c>--cell q,r</c>: the cell the tank opens on, over the map's
    /// first parking.</summary>
    private Vector2I? _startCell;

    /// <summary>How high the hull's deck stands over the ground, lift px - the
    /// sprite's <see cref="AtlasSet.DeckHeightPx"/>, measured off the model's
    /// Hull mesh (<see cref="MeasureDeck"/>). The draught is the class's share
    /// of it.</summary>
    private float _deckPx;

    /// <summary>How high its roof stands, lift px - <see cref="MeasureRoof"/>.</summary>
    private float _roofPx;

    /// <summary>The ride without the bob: where the rig stands this frame, world
    /// Y - the ground, the water, or the way down between them.</summary>
    private float _ride;
    private bool _falling;
    private float _fallV;
    private Vector2I _cellWas = new(int.MinValue, int.MinValue);
    private readonly Buoy _buoy = new() { Phase = 1.3 };

    /// <summary>Seconds the hull has been out in deep water - the drowning's own
    /// clock. Not the wreck's: <see cref="Wreck.Kill"/> restarts that one, and a
    /// knocked-out hull finished off halfway down would bob back up.</summary>
    private float _sinkClock;
    private Bubbles? _air;
    private bool _airTold;

    /// <summary>How fast a hull drops off the bank, world px/s². About a real
    /// fall at the model's scale, a little quicker - the sprite board's drop
    /// is a quarter of a leg.</summary>
    private const float FallGravity = 700.0f;

    /// <summary>The fall's pitch: the leading end goes over first. Radians a
    /// second into the kick spring.</summary>
    private const float FallTip = 2.2f;

    /// <summary>The cell under the rig's middle.</summary>
    private Vector2I CellHere => _field?.FlatCellAt(Board(_rig.Position)) ?? Vector2I.Zero;

    private bool DeepHere => _field is not null && _field.IsDeep(CellHere);

    /// <summary>Whether this hull is drowned rather than knocked out: out, not
    /// destroyed, in deep water - <see cref="TankTick.DrownedAt"/>, asked of the
    /// cell as the board asks it.</summary>
    private bool Drowning => _wreck.Disabled && !_wreck.Dead && DeepHere;

    /// <summary>How far down the hull has gone, nought to one - the smoothstep
    /// of <see cref="TankTick.SunkAt"/> over its own clock. A destroyed hull
    /// goes down too: what is left of it has nothing to float on.</summary>
    private float Sink => Mathf.SmoothStep(0.0f, (float)TankTick.SinkSeconds, _sinkClock);

    /// <summary>Whether the hull is on the water rather than on its tracks.</summary>
    private bool Swimming => _field is not null && DeepHere && _field.RampHeading(CellHere) < 0;

    /// <summary>
    /// Where the rig stands over a cell, world Y: the face on dry land and in a
    /// ford; in deep water afloat at <c>surface - draught</c> no lower than the
    /// drawn bed (a ramp's face where the pond runs up one - on the ramp it is
    /// on its tracks), and let down to that bed as it drowns.
    ///
    /// <b>And into the bed, by what the water lacks.</b> The drowned goes under
    /// entire (docs/water.md «Утоплен»), and a roof taller than the water over
    /// the bed would stand in the surface: the sprite shrinks by the excess
    /// (<see cref="Stage3D.Shrunk"/>) because a screen row cannot say depth; a
    /// model can, and settles into the silt instead, the belts under the drawn
    /// floor, until its roof is <see cref="Stage3D.SunkenMargin"/> of the water
    /// down. Measured on the events pond: 86 px of water, and only the medium's
    /// cupola, at 87, wants it.
    /// </summary>
    private float RideOn(Vector2I cell, Vector2 flat, out bool afloat)
    {
        afloat = false;
        float face = _field!.TopOn(cell, flat);
        if (!_field.IsDeep(cell))
            return face / RiseFactor;
        bool ramp = _field.RampHeading(cell) >= 0;
        float bed = ramp ? face : _field.BedAt(cell);
        float top = _field.WaterTop(cell);
        float swim = top - _deckPx * (float)_profile.Draught;
        float up = Mathf.Max(bed, swim);
        afloat = swim > bed + 0.5f;
        float silt = ramp ? 0.0f : Mathf.Max(0.0f, _roofPx - Stage3D.SunkenMargin * (top - bed));
        return Mathf.Lerp(up, bed - silt, Sink) / RiseFactor;
    }

    /// <summary>
    /// The rig's height this frame: the ride, reached by a fall when the step
    /// under the middle was a drop off a bank, and the bob on it while it floats.
    /// </summary>
    private float Ride(Vector2I cell, float target, bool afloat, float dt, bool snap)
    {
        if (snap || _field is null)
        {
            _falling = false;
            _fallV = 0.0f;
            _cellWas = cell;
            _ride = target;
            _buoy.Reset();
            _wading = Wading(cell);
            return target;
        }
        if (cell != _cellWas)
        {
            Vector2I was = _cellWas;
            _cellWas = cell;
            if (_field.InBounds(was))
                Entered(was, cell);
        }
        if (_falling)
        {
            _fallV -= FallGravity * dt;
            _ride += _fallV * dt;
            if (_ride <= target)
            {
                _ride = target;
                _falling = false;
                Landed(cell, -_fallV);
            }
        }
        else
            _ride = target;
        // Down the ramp into the pond: the tracks take it in, two fifths of a
        // plunge (TankTick.RampWash) - on the frame the hull's belly goes under,
        // not on the frame its middle crosses onto the ramp, whose head is dry.
        bool wading = Wading(cell);
        if (wading && !_wading && !_falling)
            Wetted(cell, TankTick.RampWash);
        _wading = wading || _falling;
        _buoy.Update(dt);
        float bob = _field.IsDeep(cell) ? (float)_buoy.Offset : 0.0f;
        if (afloat && Sink <= 0.0f)
            bob += (float)_buoy.Idle;
        return _ride + bob / RiseFactor;
    }

    /// <summary>The middle crossed from one cell to the next.</summary>
    private void Entered(Vector2I from, Vector2I to)
    {
        if (_field is null)
            return;
        // Water of any depth puts a fire out - GDD field.md.
        if (_burning && _field.IsWater(to))
        {
            _burning = false;
            GD.Print($"tank3d: {_modelTag} into the water at {to} - the fire is out");
        }
        if (_field.Drops(from, to))
        {
            _falling = true;
            _fallV = 0.0f;
            _kickPitch.Kick(FallTip * Mathf.Sign(_speed == 0.0f ? 1.0f : _speed));
            return;
        }
    }

    /// <summary>Whether the hull stands in deep water deeper than a third of its
    /// draught - what the ramp's splash waits for.</summary>
    private bool Wading(Vector2I cell) =>
        _field is not null && _field.IsDeep(cell)
        && _ride * RiseFactor < _field.WaterTop(cell) - 0.35f * _deckPx * (float)_profile.Draught;

    private bool _wading;

    /// <summary>The fall is over: on dry ground a jolt, in the pond the plunge.</summary>
    private void Landed(Vector2I cell, float speed)
    {
        _kickPitch.Kick(-0.6f * FallTip * Mathf.Sign(_speed == 0.0f ? 1.0f : _speed));
        if (_field is not null && _field.IsDeep(cell))
            Wetted(cell, 1.0f);
        else
            _heave.Kick(-0.002f * speed);
    }

    /// <summary>The hull went into deep water, by a fall (<paramref name="share"/>
    /// one) or down the ramp: the bow wave and the fans, heavier by class
    /// (<see cref="TankTick.PlungeMight"/>), the bob's kick - and without the
    /// gear, the engine.</summary>
    private void Wetted(Vector2I cell, float share)
    {
        if (_field is null)
            return;
        float top = _field.WaterTop(cell);
        float h = Mathf.DegToRad(_heading);
        var ahead = new Vector3(Mathf.Sin(h), 0.0f, Mathf.Cos(h));
        Vector3 way = _speed < 0.0f ? -ahead : ahead;
        float might = (0.7f + 0.25f * Math.Max(0, _profile.Mass - 1)) * share;
        float halfLen = _model.HullLength * _model.PixelsPerUnit * 0.5f;
        // The model's own splash (CelSplash); the board's Plunge under --fx2d.
        // Kept inside the hex it went into - the cell's centre and its corner
        // radius, the board's hexes being regular in the world.
        Vector2 mid = _field.FlatAnchor(cell) + _field.CentreOffset;
        if (_splash is not null)
            _splash.Fire(new Vector3(_rig.Position.X, top / RiseFactor, _rig.Position.Z), way,
                         halfLen, _model.HullWidth * _model.PixelsPerUnit * 0.5f, might,
                         new Vector3(mid.X, top / RiseFactor, mid.Y / Squash), HexWidth * 0.5f);
        else
            _stage?.Plunge(Board(_rig.Position) - new Vector2(0.0f, top), top, Along(way),
                           halfLen, might, Plunge.Style.Cinematic);
        _buoy.Jolt(TankTick.PlungeKick * share);
        Rings(way, halfLen, might);
        GD.Print($"tank3d: {_modelTag} into the pond at {cell}: water {top:F1}, bed {_field.BedAt(cell):F1}, "
                 + $"deck {_deckPx:F1} px, draught {_deckPx * _profile.Draught:F1} px, might {might:F2}"
                 + (_amphibious ? "" : " - no wading gear, the engine is drowned"));
        if (!_amphibious)
            Drown();
    }

    /// <summary>
    /// The pond's wave field (<see cref="Ripples"/>), handed to the stage as its
    /// <see cref="Stage3D.Wash"/>: the stage fits it to the water and reads it
    /// for the surface's normals, glints and foam. Here it is pushed - by the
    /// hull going through (<see cref="Ripples.Note"/>), by the splash, by the
    /// air - and stepped once a frame, as the board does it.
    /// </summary>
    private readonly Ripples _ripples = new();

    /// <summary>The field drawn in the model's look - see <see cref="CelRipples"/>.</summary>
    private CelRipples? _celRipples;

    /// <summary>The splash's rings: <see cref="Stage3D.Plunge"/>'s three strikes -
    /// the nose hardest, the flanks half - so the ring the pond carries to the
    /// bank is the hull's shape and not a point's.</summary>
    private void Rings(Vector3 way, float halfLen, float might)
    {
        var at = new Vector2(_rig.Position.X, _rig.Position.Z);
        var along = new Vector2(way.X, way.Z).Normalized();
        var across = new Vector2(-along.Y, along.X);
        _ripples.Strike(at + along * halfLen * 0.8f, might * Stage3D.Waves);
        _ripples.Strike(at + across * halfLen * 0.55f, might * Stage3D.Waves * 0.5f);
        _ripples.Strike(at - across * halfLen * 0.55f, might * Stage3D.Waves * 0.5f);
    }

    /// <summary>The water stops the engine: out with no round in it - no flash,
    /// the turret on its ring (<see cref="TankTick.Park"/>'s own call).</summary>
    private void Drown()
    {
        if (_fate != Fate.Alive)
            return;
        _fate = Fate.Knocked;
        _sinceFate = 0.0f;
        _speed = 0.0f;
        _burning = false;
        _wreck.Disable(seated: true);
    }

    /// <summary>
    /// The drowning's clock, and the air: nothing while any of the tank still
    /// shows, a steady seep once the water is over it - <see cref="Bubbles"/>,
    /// seated on the water where the deck is seen through it.
    /// </summary>
    private void WaterTick(float dt)
    {
        if (_field is null)
            return;
        WakeTick(dt);
        bool deep = DeepHere;
        if (_wreck.Out && deep)
            _sinkClock += dt;
        if (_wreck.Out && deep && _burning)
            _burning = false;
        float sink = deep && _wreck.Out ? Sink : 0.0f;
        if (sink <= 0.0f && _air is not { Busy: true })
            return;
        if (_air is null)
        {
            _air = new Bubbles { Name = "Air" };
            AddChild(_air);
            _air.Build(7);
            _air.Struck = (at, might) => _ripples.Strike(at, might);
        }
        Vector2I cell = CellHere;
        float top = _field.WaterTop(cell);
        // <b>Where the deck is seen, not over it.</b> Straight up from a hull
        // on the bed is most of a level up the screen - pops beyond the tank.
        // Along the view ray the surface point is the one in front of the deck
        // the eye sees the tank through, the same reason the ruts in a ford
        // were lifted along it (CelRuts).
        Vector3 deck = _rig.GlobalPosition + _rig.GlobalBasis.Y * (_deckPx / RiseFactor);
        Vector3 seen = deck + View * ((top / RiseFactor - deck.Y) / View.Y);
        float h = Mathf.DegToRad(_heading);
        var ahead = new Vector3(Mathf.Sin(h), 0.0f, Mathf.Cos(h));
        float half = _model.HullLength * _model.PixelsPerUnit * 0.5f;
        _air.Sit(Board(seen) - new Vector2(0.0f, top), top, Along(ahead),
                 half, _model.HullWidth * _model.PixelsPerUnit * 0.5f, Squash, RiseFactor);
        _air.Feed(sink);
        _air.Tick(dt);
        if (sink >= 1.0f && !_airTold)
        {
            _airTold = true;
            float roof = _rig.Position.Y * RiseFactor + _roofPx;
            GD.Print($"tank3d: {_modelTag} on the bed at {cell}: roof {roof:F1}, water {top:F1} px "
                     + $"({(roof < top ? "under" : "OUT")} by {Mathf.Abs(top - roof):F1})");
        }
    }

    /// <summary>The wake's pace, eased: the bow wave builds and dies with the
    /// hull's way on, not with the throttle.</summary>
    private float _wakePace;

    /// <summary>
    /// The wake and the bow wave (<see cref="CelWake"/>) for a hull moving
    /// through water of any depth - a ford as well as the pond - with its belly
    /// under the surface. The pace is the speed against the water's own top
    /// speed: afloat the swimming share (<see cref="MovementProfile.SwimFraction"/>),
    /// in a ford the class's.
    /// </summary>
    private void WakeTick(float dt)
    {
        if (_wake is null || _field is null)
            return;
        Vector2I cell = CellHere;
        bool water = _field.InBounds(cell) && _field.IsWater(cell);
        float top = water ? _field.WaterTop(cell) : 0.0f;
        bool wet = water && _rig.Position.Y * RiseFactor < top - 1.0f;
        float cap = MaxSpeed * (Swimming ? (float)MovementProfile.SwimFraction : 1.0f);
        float want = wet ? Mathf.Clamp(Mathf.Abs(_speed) / Mathf.Max(cap, 1.0f), 0.0f, 1.0f) : 0.0f;
        _wakePace = Mathf.MoveToward(_wakePace, want, dt * (want > _wakePace ? 3.0f : 1.5f));
        float h = Mathf.DegToRad(_heading);
        var ahead = new Vector3(Mathf.Sin(h), 0.0f, Mathf.Cos(h));
        Vector3 way = _speed < 0.0f ? -ahead : ahead;
        float halfLen = _model.HullLength * _model.PixelsPerUnit * 0.5f;
        float halfWide = _model.Size.X * _model.PixelsPerUnit * 0.5f;
        var middle = new Vector3(_rig.Position.X, top / RiseFactor, _rig.Position.Z);
        _wake.Lay(middle + way * (halfLen * 0.85f), middle - way * (halfLen * 0.95f), _hullLeft,
                  halfWide, Mathf.Abs(_speed), _wakePace, wet && Mathf.Abs(_speed) > 2.0f);
        _wake.Bow(middle, way, halfLen, halfWide, wet ? _wakePace : 0.0f, Mathf.Abs(_speed));
        _wake.Tick(dt);
        // The hull shoves the water it goes through - the board's dipole
        // (Ripples.Note), by the same pace as the wake.
        if (wet && Mathf.Abs(_speed) > 2.0f)
            _ripples.Note(new Vector2(middle.X, middle.Z), new Vector2(way.X, way.Z), _wakePace);
        _ripples.Tick(dt);
        _celRipples?.Show(_ripples);
        Waterline(dt, ahead, halfLen);
    }

    private float _waterOn;

    /// <summary>
    /// The foam on the armour where the water cuts it - <see cref="Toon"/>'s
    /// <c>water_*</c>, on every cel material of the model. The surface is the
    /// highest water under the hull's middle and its two ends, one body of water
    /// being one surface: a hull nosing into a ford has the line on its bow
    /// before its middle is wet. There while any of it is under, gone as it
    /// drowns (the sprite's foam goes by <c>1 - sink</c> too), thicker the
    /// faster it goes through.
    /// </summary>
    private void Waterline(float dt, Vector3 ahead, float halfLen)
    {
        if (_field is null || _model.Cel.Count == 0)
            return;
        float level = float.NegativeInfinity;
        foreach (float along in new[] { 0.0f, 0.9f, -0.9f })
        {
            Vector2I c = _field.FlatCellAt(Board(_rig.Position + ahead * (halfLen * along)));
            if (_field.InBounds(c) && _field.IsWater(c))
                level = Mathf.Max(level, _field.WaterTop(c));
        }
        bool wet = level > _rig.Position.Y * RiseFactor + 0.5f;
        float want = wet ? 1.0f - (DeepHere && _wreck.Out ? Sink : 0.0f) : 0.0f;
        _waterOn = Mathf.MoveToward(_waterOn, want, dt * 4.0f);
        if (wet)
            _waterLevel = level;
        // Not on the belts' links: a link is a few px of steel with a gap to the
        // next, so a band laid across the upper run came out as a dotted line -
        // the gaps the user found in the waterline. The water's own edge foam
        // lies along the belt there already.
        _beltPaint ??= BeltPaint();
        foreach (ShaderMaterial cel in _model.Cel)
        {
            cel.SetShaderParameter("water_on", _beltPaint.Contains(cel) ? 0.0f : _waterOn);
            cel.SetShaderParameter("water_y", _waterLevel / RiseFactor);
            cel.SetShaderParameter("water_pace", _wakePace);
        }
    }

    private float _waterLevel;

    /// <summary>The cel materials the belts' links are drawn with - kept out of
    /// the waterline. Made once per model: <see cref="Mount"/> clears it.</summary>
    private System.Collections.Generic.HashSet<ShaderMaterial>? _beltPaint;

    private System.Collections.Generic.HashSet<ShaderMaterial> BeltPaint()
    {
        var paint = new System.Collections.Generic.HashSet<ShaderMaterial>();
        foreach (TankModel.Track t in _model.Tracks)
        {
            if (t.Links.Multimesh?.Mesh is not Mesh link)
                continue;
            for (int s = 0; s < link.GetSurfaceCount(); s++)
                if (link.SurfaceGetMaterial(s) is ShaderMaterial m)
                    paint.Add(m);
            if (t.Links.MaterialOverride is ShaderMaterial o)
                paint.Add(o);
        }
        return paint;
    }

    /// <summary>What a thrown piece lands on, world Y: the ground, or in deep
    /// water the drawn bed under it - the face there is the level the rules
    /// read, and the water stands over a floor a level lower.</summary>
    private float BedUnder(Vector3 w)
    {
        if (_field is null)
            return Foot(w).Y;
        Vector2I cell = _field.FlatCellAt(Board(w));
        return _field.InBounds(cell) && _field.IsDeep(cell) && _field.RampHeading(cell) < 0
            ? _field.BedAt(cell) / RiseFactor
            : Foot(w).Y;
    }

    /// <summary>The hull's own water goes with a reset: afloat again, or dry.</summary>
    private void WaterReset()
    {
        _sinkClock = 0.0f;
        _airTold = false;
        _air?.Feed(0.0f);
    }

    /// <summary>
    /// How high the deck stands over the ground, lift px: the area-weighted
    /// median height of the Hull mesh's up-facing faces - the roofs the water
    /// would wash over. <see cref="AtlasSet.DeckHeightPx"/> is the median of the
    /// height map's roof codes, and this is the same question put to the mesh.
    /// A casemate's hull carries its fighting compartment, and its roof then
    /// weighs in as the sprite's did not; the median still lands on the deck
    /// while the engine deck and the sponsons are the larger share.
    /// </summary>
    private float MeasureDeck()
    {
        if (_model.Hull is not MeshInstance3D hull || hull.Mesh is null)
            return _model.Size.Y * 0.5f * _model.PixelsPerUnit * RiseFactor;
        Transform3D toTank = _model.Tank.GlobalTransform.AffineInverse() * hull.GlobalTransform;
        var roofs = new System.Collections.Generic.List<(float Y, float Area)>();
        float total = 0.0f;
        for (int s = 0; s < hull.Mesh.GetSurfaceCount(); s++)
        {
            var arrays = hull.Mesh.SurfaceGetArrays(s);
            var v = (Vector3[])arrays[(int)Mesh.ArrayType.Vertex];
            int[] idx = arrays[(int)Mesh.ArrayType.Index].VariantType == Variant.Type.Nil
                ? Array.Empty<int>() : (int[])arrays[(int)Mesh.ArrayType.Index];
            int n = idx.Length > 0 ? idx.Length : v.Length;
            for (int i = 0; i + 2 < n; i += 3)
            {
                Vector3 a = toTank * v[idx.Length > 0 ? idx[i] : i];
                Vector3 b = toTank * v[idx.Length > 0 ? idx[i + 1] : i + 1];
                Vector3 c = toTank * v[idx.Length > 0 ? idx[i + 2] : i + 2];
                Vector3 cross = (b - a).Cross(c - a);
                float area = cross.Length() * 0.5f;
                if (area <= 0.0f || Mathf.Abs(cross.Normalized().Y) < 0.8f)
                    continue;
                roofs.Add(((a.Y + b.Y + c.Y) / 3.0f, area));
                total += area;
            }
        }
        if (roofs.Count == 0)
            return _model.Size.Y * 0.5f * _model.PixelsPerUnit * RiseFactor;
        roofs.Sort((p, q) => p.Y.CompareTo(q.Y));
        // Up-facing faces at the bottom are the belly: only those in the upper
        // half of the hull count as roofs.
        float top = roofs[^1].Y;
        float from = top * 0.5f;
        float sum = 0.0f, upper = 0.0f;
        foreach (var r in roofs)
            if (r.Y >= from)
                upper += r.Area;
        float median = top;
        foreach (var r in roofs)
        {
            if (r.Y < from)
                continue;
            sum += r.Area;
            if (sum >= upper * 0.5f)
            {
                median = r.Y;
                break;
            }
        }
        return median * _model.PixelsPerUnit * RiseFactor;
    }

    /// <summary>
    /// How high the tank's roof stands over the ground, lift px - the turret's
    /// or the casemate's, the aerial and the gun left out: the sprite's
    /// <see cref="AtlasSet.RoofRisePx"/>, which is a high percentile of the
    /// height map for the same reason. Here the highest height under which
    /// all but <see cref="RoofSpare"/> of the model's up-facing area lies: an
    /// aerial's tip and a rivet's head are no area at all.
    /// </summary>
    private float MeasureRoof()
    {
        Transform3D toTank = _model.Tank.GlobalTransform.AffineInverse();
        var roofs = new System.Collections.Generic.List<(float Y, float Area)>();
        float total = 0.0f;
        foreach (MeshInstance3D mesh in Meshes(_model))
        {
            if (mesh is { Visible: false } || mesh.Mesh is not ArrayMesh)
                continue;
            Transform3D x = toTank * mesh.GlobalTransform;
            for (int s = 0; s < mesh.Mesh.GetSurfaceCount(); s++)
            {
                var arrays = mesh.Mesh.SurfaceGetArrays(s);
                var v = (Vector3[])arrays[(int)Mesh.ArrayType.Vertex];
                int[] idx = arrays[(int)Mesh.ArrayType.Index].VariantType == Variant.Type.Nil
                    ? Array.Empty<int>() : (int[])arrays[(int)Mesh.ArrayType.Index];
                int n = idx.Length > 0 ? idx.Length : v.Length;
                for (int i = 0; i + 2 < n; i += 3)
                {
                    Vector3 a = x * v[idx.Length > 0 ? idx[i] : i];
                    Vector3 b = x * v[idx.Length > 0 ? idx[i + 1] : i + 1];
                    Vector3 c = x * v[idx.Length > 0 ? idx[i + 2] : i + 2];
                    Vector3 cross = (b - a).Cross(c - a);
                    float area = cross.Length() * 0.5f;
                    if (area <= 0.0f || cross.Normalized().Y < 0.5f)
                        continue;
                    roofs.Add((Mathf.Max(a.Y, Mathf.Max(b.Y, c.Y)), area));
                    total += area;
                }
            }
        }
        if (roofs.Count == 0)
            return _model.Size.Y * _model.PixelsPerUnit * RiseFactor;
        roofs.Sort((p, q) => q.Y.CompareTo(p.Y));
        float above = 0.0f;
        foreach (var r in roofs)
        {
            above += r.Area;
            if (above >= total * RoofSpare)
                return r.Y * _model.PixelsPerUnit * RiseFactor;
        }
        return roofs[^1].Y * _model.PixelsPerUnit * RiseFactor;
    }

    /// <summary>The share of up-facing area let stand over the roof.</summary>
    private const float RoofSpare = 0.01f;

    /// <summary>The first bank cell with deep water straight toward the camera
    /// from it, a level down - where «к пруду» puts the tank, facing it. The
    /// events board's is (10,4) over (10,5), the pond's far row: a hull
    /// drowned in the near row stands behind the bank (<see cref="HexField.DeepBed"/>).</summary>
    private Vector2I? PondBank()
    {
        if (_field is null)
            return null;
        for (int q = 0; q < _field.Columns; q++)
            for (int r = 0; r < _field.Rows; r++)
            {
                var bank = new Vector2I(q, r);
                if (_field.IsWater(bank) || _field.RampHeading(bank) >= 0)
                    continue;
                int heading = 270;   // toward the camera, down the column
                Vector2I pond = HexField.Step(bank, heading);
                if (_field.InBounds(pond) && _field.IsDeep(pond) && _field.RampHeading(pond) < 0
                    && _field.Passable(bank, heading))
                    return bank;
            }
        return null;
    }

    /// <summary>The panel's «к пруду»: the tank whole again, on the bank, facing
    /// the water.</summary>
    private void ToPond()
    {
        if (PondBank() is not Vector2I bank)
        {
            _note = "no pond on this board";
            return;
        }
        ResetTank();
        _home = bank;
        _heading = 0.0f;
        Park();
    }
}
