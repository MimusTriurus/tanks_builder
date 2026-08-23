using System;
using System.Collections.Generic;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// One tank's frame: driving it along its order, standing it on the cell it
/// reaches, and winding every clock hung off it.
///
/// <b>Lifted out of <see cref="Main"/> whole rather than written a second
/// time, and that is the only reason it exists.</b> A bench that wanted a tank
/// on a hex - <see cref="TankBench"/> - had two ways to get one: copy this
/// sequence, or share it. The copy is what <see cref="WoodBench"/>'s docstring
/// refuses in the same words: a bench that reimplements the thing it is judging
/// is a bench measuring itself, and the two copies agree only until the first
/// edit lands in one of them. So the harness and the bench call the same object
/// and the ordering below is the only ordering there is.
///
/// <b>What it is not is a second scene.</b> It owns no nodes, builds no board
/// and reads no flags. The board, the switches and the tank list are handed in
/// as fields, which is the same arrangement <see cref="Grove"/> and
/// <see cref="Stage3D"/> already use: the caller says what the world is, and
/// this says what a frame does to a tank standing in it.
///
/// <b>The switches are pushed every frame, never pulled.</b> They live where
/// they are declared - a flag, a key and a panel row apiece in the harness, a
/// smaller set in the bench - and <see cref="Bind"/> copies them across before
/// the loop. Pulling them through an interface would have made every one of
/// them a member of a contract, and the contract would then be the place a new
/// switch has to be remembered in as well as the two it already does. Pushing
/// costs the risk that one is forgotten; that risk is one method long and the
/// caller can read it.
///
/// <b>Gunnery is the harness's and is not here.</b> It is about two tanks -
/// which lane, whose armour, whose shell in the air - and this is about one,
/// so a bench with a single tank would carry it and never run it. It still has
/// to run <i>where</i> it ran, between the scan and the shot, or a round fired
/// this frame would be drawn with last frame's flash: hence <see cref="Aim"/>,
/// a hook rather than a call. The harness fills it in; the bench leaves it
/// null.
/// </summary>
public sealed class TankTick
{
    // --- the board it ticks on ----------------------------------------------

    /// <summary>The grid every distance here is measured on.</summary>
    public HexField Field = null!;

    /// <summary>Where that grid sits on the canvas. The tanks are drawn in the
    /// same space, so most of what follows is a point plus this.</summary>
    public Vector2 Origin;

    /// <summary>What the ground is made of, for the ride - see
    /// <see cref="TerrainSet.RideOf"/>. Null is a board with no plates loaded,
    /// which rides as though every cell were the smooth one.</summary>
    public TerrainSet? Terrain;

    /// <summary>The ruts. Null on a bench that does not lay them.</summary>
    public TrackMarks? Marks;

    /// <summary>The wood, which answers a shot and a hit by flinching. Null on
    /// a board with no props.</summary>
    public Grove? Wood;

    /// <summary>The view's spring. A gun going off shoves it - see
    /// <see cref="Fire"/> - and a bench without one simply does not shake.
    /// </summary>
    public CameraShake? Shake;

    /// <summary>Every tank on the board. Read for two things only: clearing a
    /// dead tank out of everyone's sights, and nothing else - so a bench with
    /// one tank hands in a list of one rather than nothing.</summary>
    public IReadOnlyList<Vehicle> Vehicles = Array.Empty<Vehicle>();

    /// <summary>The one being driven, or null. It decides two things and both
    /// are about the board rather than the tank: whose route is highlighted,
    /// and whose scan the hand-driven turret suspends.</summary>
    public Vehicle? Driven;

    /// <summary>What a whole screen pixel is worth to a sprite - the camera's
    /// zoom. Pushed rather than read, because a sprite has no business reading
    /// the camera and the jolt has to be whole against the screen.</summary>
    public float ViewZoom = 1.0f;

    /// <summary>Whether the stage owns the ground. The rumble hands it to the
    /// sprite, which snaps its heave in a different space in each mode.
    /// </summary>
    public bool Staged;

    // --- the switches, pushed by Bind ---------------------------------------

    public bool PitchEnabled = true;
    public bool RumbleEnabled;
    public bool TracksEnabled = true;
    public bool TrembleEnabled = true;
    public double TrembleLevel = 1.0;
    public bool ExhaustEnabled = true;
    public double ExhaustLevel = 1.0;
    public bool ExhaustRamp;
    public bool ScanEnabled;
    public bool RecoilTube = true;
    public bool RecoilShear;
    public bool ShakeOn;

    // --- what it cannot answer for itself -----------------------------------

    /// <summary>Whether this tank's turret is being driven by hand right now -
    /// the spin key, or the mouse. It suspends the scan, and it is asked rather
    /// than held because both of those are the harness's own controls and a
    /// bench may have neither. Null answers no.</summary>
    public Func<Vehicle, bool>? TurretHeld;

    /// <summary>Gunnery, run between the scan and the shot because that is
    /// where it ran. See the class summary: it belongs to a board with two
    /// tanks on it, and a bench with one leaves it null.</summary>
    public Action<Vehicle, double>? Aim;

    // --- the frame -----------------------------------------------------------

    /// <summary>
    /// One tank, one frame, in the order the harness ran these in.
    ///
    /// <b>The order is the contract.</b> The lean is read after the drive
    /// because it reads the position the drive left; the belts are wound from
    /// the travel the same drive reported and the ruts are laid from that same
    /// pair, so that a mark and a track phase cannot disagree about how far the
    /// ground went past; the shot is advanced after the gunnery so a round
    /// fired this frame shows its first flash on this frame rather than the
    /// next.
    /// </summary>
    public void Run(Vehicle v, double delta)
    {
        if (v.Moving)
            AdvanceOrder(v, delta);
        else if (v.Pitch.Angle != 0.0 || v.Speed != 0.0 || v.Sprite.Shake != 0.0)
        {
            // Standing still is not the same as having always been standing:
            // the pitch spring is still ringing down and the last jolt is still
            // on screen, and both have to be run to nothing rather than left.
            v.Speed = 0.0;
            UpdatePitch(v, 0.0, delta);
            UpdateRumble(v, delta);
            v.Sprite.QueueRedraw();
        }

        UpdateLean(v, delta);

        (double Left, double Right) belts = BeltTravel(v, delta);
        UpdateTracks(v, belts, delta);
        Marks?.Lay(v, belts, delta);
        UpdateTremble(v, delta);
        UpdateExhaust(v, delta);
        UpdateWreck(v, delta);
        UpdateBurn(v, delta);
        UpdateScan(v, delta);
        Aim?.Invoke(v, delta);
        UpdateShot(v, delta);
        UpdateHit(v, delta);
        v.Audio?.Update(v, delta);
    }

    /// <summary>Put a tank on its cell.
    ///
    /// The z index comes off the cell's screen height, which is the whole of the
    /// overlap rule: a tank further down the screen is nearer the camera and draws
    /// over one behind it. Tree order cannot say that - it is fixed at build time
    /// and the tanks move - and without it the tank that happens to have been
    /// created last wins every overlap, which reads as one driving through
    /// another.</summary>
    /// <summary>
    /// Where a tank's sprite has to sit for the tank to be standing on a cell.
    ///
    /// On its <em>contact patch</em>, not on its anchor, and that distinction is
    /// worth the method. Every layer of one tank shares the anchor, so drawing a
    /// tank at its cell's anchor is what keeps hull, turret and belts together -
    /// but the anchor is the turret axis lifted to the mid-height of the fitted
    /// bounds, so it floats 50 to 59 px above the ground. Two things then move the
    /// tank off the ground the field is drawing, and both were introduced here
    /// rather than by the renderer, which centres tile and footprint to within
    /// two pixels:
    ///
    /// - the size scales about the anchor, so a tank drawn at 0.85 has that 51px
    ///   of float shortened to 43 and stands 8px high in its hex, and a 1.15 one
    ///   sinks 8px into it;
    /// - the field draws one tile under all three, and the tiles do not agree on
    ///   where the ground is: 49.7px below the anchor on the light's, 59.0 on the
    ///   medium's, which is another 9px.
    ///
    /// They happened to cancel on the heavy and to add on the light, so the tank
    /// that looked wrong was the one where the two errors agreed - which is why
    /// this reads as "sometimes off centre" rather than as an offset.
    ///
    /// Asking instead for the ground point to land on the cell's ground centre
    /// answers both at once, at any size and with any tile under it.
    /// </summary>
    public Vector2 StandOn(Vehicle vehicle, Vector2I cell) =>
        Origin + Field.CellAnchor(cell) + Field.CentreOffset
        - vehicle.Atlas.GroundOffset * vehicle.Sprite.BodyScale;

    /// <summary>
    /// The same place part way from one cell to the next: the contact point on the
    /// <b>ground</b> between them.
    ///
    /// <b>It replaces driving along the straight line between the two anchors, and
    /// the belt marks are what proved that line wrong.</b> The straight line is the
    /// chord of <see cref="HexField.HeightBetween"/>, and a ramp's surface is not a
    /// chord - it runs centre to shared edge to centre, a quarter of a level above
    /// the chord at the boundary (see <see cref="HexField.SurfaceBetween"/>, which
    /// already carried this for anything lying in the ground). The chord was kept
    /// because for a billboard it looked like a fine approximation: the error is
    /// vertical, and a card drawn 16px low still stands on its hex.
    ///
    /// <b>It is not vertical once the leg is diagonal, and that is the measurement
    /// that retired it.</b> A screen row is a place on the ground, so 16.19px of
    /// row is 32px of ground along the view. On a leg straight up or down the screen
    /// that slides the tank along its own path and shows as nothing; on a diagonal
    /// leg the shared edge is a slanted line, so the same error slides the crossing
    /// <i>along that edge</i> - measured on (9,2)->(10,3) of this board, 21.6px off
    /// the edge's midpoint, 52% of the way to its corner. The tank drove into the
    /// hex through the wrong part of its face, and the ruts, laid at the contact
    /// point, drew it.
    ///
    /// The two ends are untouched: <see cref="HexField.SurfaceBetween"/> is
    /// <c>TopAt</c> at nought and at one, so this is <see cref="StandOn"/> there and
    /// arriving still parks on the same pixel it always did.
    /// </summary>
    public Vector2 StandBetween(Vehicle vehicle, Vector2I from, Vector2I onto,
                                 float done) =>
        Origin + Main.GroundBetween(Field, from, onto, done)
        - vehicle.Atlas.GroundOffset * vehicle.Sprite.BodyScale;

    /// <param name="placed">Whether the tank is being <i>put</i> here rather than
    /// having driven here. It decides one thing - whether the body is sat on the
    /// face it stands on or left to the spring - and getting it wrong is loud: a
    /// cell is parked on at the end of <b>every</b> leg, so sitting the body on
    /// arrival snapped a tank cresting a ramp flat in one frame, and the whole
    /// point of <see cref="ClimbLean"/> is that it does not do that. Placing is
    /// start-up, a reset, and the key that drops a tank on a cell; those have no
    /// motion to preserve and want the lean already right on the first frame.
    /// </param>
    public void Park(Vehicle vehicle, bool placed = true)
    {
        vehicle.Cell = Field.ClampCell(vehicle.Cell);
        Field.Position = Origin;
        // The surface, not the floor: the two are half a level apart on a ramp,
        // and this has to agree with the drawn position - HexField.CellAnchor is
        // lifted off the same number, and Depth subtracts this one back out of
        // that one to get the flat row. Written as the level's lift, the depth
        // term and the drawn row disagreed by half a lift on every ramp cell.
        vehicle.Height = Field.TopAt(vehicle.Cell);
        vehicle.Standing = vehicle.Height;
        // A tank standing on a cell stands at its centre, and there the chord and
        // the surface are the same point - but the clearance is still owed, because
        // marks are laid on the frame a leg ends too. See HexField.MarkAt.
        vehicle.Ground = Field.MarkAt(vehicle.Cell);
        // Not cleared but asked, for the reason OnSlope below is: parking is where
        // a tank comes to rest in a ford, and that is the case this exists for.
        vehicle.Waterline = Field.IsWater(vehicle.Cell)
            ? Field.WaterTop(vehicle.Cell) : float.NegativeInfinity;
        vehicle.Trailing = vehicle.Height;
        vehicle.Travel = Vector2.Zero;
        vehicle.Levelling = false;
        // Not cleared but asked: parking is where a tank comes to rest on a ramp,
        // and that is the case this exists for.
        vehicle.OnSlope = Field.IsRamp(vehicle.Cell);
        vehicle.Sprite.Position = StandOn(vehicle, vehicle.Cell);
        // Cleared before the lean is asked: the leg that just ended left this at
        // one, and on the cell arrived at the two faces being mixed are the same
        // one anyway - but the next leg's first frame must not begin already
        // crossed.
        vehicle.LegBlend = 0.0f;
        // And the leg's own progress, for the same reason and one more: it is what
        // the position is now derived from, so a leg that began with the previous
        // one's progress would put the tank part way along itself on its first
        // frame - see StandBetween.
        vehicle.LegDone = 0.0f;
        // Sat on the face it is standing on rather than sprung up to it, but only
        // for a tank being put here - see the parameter. Asked after the position,
        // because SurfaceGrade reads the heading off the sprite.
        if (placed)
        {
            vehicle.Lean.Reset(SurfaceSlope(vehicle));
            vehicle.Sprite.Climb =
                vehicle.Lean.Angle(vehicle.Sprite.HullFacing, Field.RiseFactor);
            vehicle.Sprite.Slope = ClimbLean.Print(vehicle.Lean.Slope,
                                                   Field.Squash,
                                                   Field.RiseFactor);
        }
        // Moved rather than driven, so the ribbon must break here or the jump is
        // drawn as a line across the board - and only then, which is the same
        // question the parameter above already answers. A cell is parked on at the
        // end of *every* leg, so lifting the pen unconditionally broke the ribbon
        // at every cell boundary of an ordinary drive: with the belt-smoothing
        // window as long as a run, each cell's worth of trail collapsed towards its
        // own middle and a four-cell route was drawn as four blobs. Measured on the
        // 3D board, where the marks are geometry and the collapse is plain: 1081
        // rut pixels in two patches 52px apart against 3476 over the whole 186px
        // of the route. The old peak was the darker of the two (67 against 34)
        // because a collapsed run draws its every segment on top of itself.
        if (placed)
            Marks?.Lift(vehicle);
        Depth(vehicle);
        // Belt travel is read back off the heading rather than reported by
        // whatever turned it, so the baseline has to be laid down wherever the
        // heading is set from outside. Left stale, the first frame after a reset
        // sees the whole of the difference as one frame's swing and the belts
        // jump.
        vehicle.LastHullFacing = vehicle.Sprite.HullFacing;
        // Same baseline, same reason, one line down: the ring's rate is read back
        // off the angle, so a reset that moved the turret and left this stale
        // would report the whole move as one frame's traverse and blip the motor.
        vehicle.LastTurretOffset =
            Main.WrapAngle(vehicle.Sprite.TurretFacing - vehicle.Sprite.HullFacing);
        // And the third baseline, for the third thing read back rather than
        // announced. Left stale, a tank put back on its home cell would count
        // the whole jump as one frame's travel and shoulder the wood aside on
        // the frame after a reset.
        vehicle.LastGroundPoint = vehicle.GroundPoint - Origin;
        vehicle.Sprite.QueueRedraw();
    }

    /// <summary>The overlap order, off where the tank actually is rather than off
    /// the cell it last reached. Taken from the live position because a tank spends
    /// most of a move between two cells: stamped on arrival only, one crossing the
    /// row of another would hold the wrong order for the whole step, which is
    /// exactly the frame you would be looking at.
    ///
    /// From the contact patch, not from the sprite's origin, for the same reason
    /// <see cref="StandOn"/> exists: what decides which tank is nearer the camera
    /// is where each one stands, and the origin sits a scaled float above that. Off
    /// the origin, three tanks on one row got three different depths - the light by
    /// 16 - and the order between them was decided by their sizes.</summary>
    public void Depth(Vehicle vehicle)
    {
        // The row it would stand on with the board flattened. The position
        // already carries the lift - CellAnchor is lifted - so putting it back is
        // how the two terms of the depth get separated again, and there is only
        // ever one place the height is subtracted.
        float row = vehicle.GroundPoint.Y - Origin.Y + vehicle.Height;
        vehicle.Sprite.ZIndex = Mathf.RoundToInt(Field.Depth(row, vehicle.Height));
        if (vehicle.Cap is null)
            return;
        vehicle.Cap.FlatRow = row;
        vehicle.Cap.Standing = vehicle.Height;
        vehicle.Cap.Box = TankBox(vehicle);
        vehicle.Cap.QueueRedraw();
    }

    /// <summary>What a tank occupies, in the field's local space, for the one
    /// question that needs it: which cells could hide it. The frame its layers
    /// are drawn from, at the class's size, about the anchor - so it is wider than
    /// the tank and narrower than the gap between two columns of the grid, which
    /// is the property that matters. Exact would mean the union of eight layers,
    /// re-measured every frame, to decide whether to repaint a cell that is beside
    /// the tank rather than on it.</summary>
    public Rect2 TankBox(Vehicle vehicle)
    {
        Vector2 size = (Vector2)vehicle.Atlas.Tile * vehicle.Sprite.BodyScale;
        Vector2 at = vehicle.Sprite.Position - Origin;
        return new Rect2(at - size * 0.5f, size);
    }

    /// <summary>
    /// Roughly half the track gauge, as a fraction of the hull's broadside span.
    ///
    /// The fallback now, and only for a tank with no belt layers to measure -
    /// see <see cref="AtlasSet.TrackArm"/>. It was the answer, and it was wrong
    /// by 12-32%: <see cref="AtlasSet.HullSpan"/> is the widest frame, which is
    /// the hull's *length*, and the three hulls are one length to within 3%
    /// while their gauges range over a fifth. Kept because a tank without belts
    /// still turns, and named rather than buried so it can be argued with.
    /// </summary>
    private const double PivotRadiusFraction = 0.25;

    /// <summary>
    /// Ground the belts covered this frame: what the tank drove, plus what it
    /// swung.
    ///
    /// The second half was missing and it showed. A tank pivoting from a standing
    /// start has <c>Speed</c> of zero - the cornering crawl is a floor, not a
    /// target - and so did the belts, so the hull swung round on tracks that were
    /// not moving. Turning on the spot is the most track-heavy thing a tank does
    /// and it was the one case with no belt motion at all.
    ///
    /// One number rather than a separate path for the sound, which is the whole
    /// reason it is fixed here: <see cref="TrackLoop"/> takes the travel, the
    /// sprite takes its phase from that and <see cref="VehicleAudio"/> takes its
    /// rate from the same clock, so the belts cannot be heard turning while they
    /// are seen standing still.
    ///
    /// Signed, and per side, which is the debt this used to carry openly. A real
    /// pivot runs the two belts in opposite directions; one unsigned number for
    /// both wound them the same way, so one of the two was always climbing its
    /// loop backwards. The fix is arithmetic rather than a case: a belt at
    /// <c>arm</c> from the centre covers <c>drive + omega*arm</c>, and the two
    /// sides differ only in the sign of <c>arm</c>.
    ///
    /// It was the ruts that forced it. A spinning belt only suggests a direction
    /// and the eye forgives it; a mark left on the ground records one, and two
    /// arcs curling the same way where they should be counter-rotating is the
    /// first thing anyone would see.
    ///
    /// The radius is <see cref="AtlasSet.TrackArm"/>, measured off those two
    /// layers, and only falls back to a fraction of the hull when there are no
    /// belts to measure. The fraction was wrong by 12-32%, worst on the heavy,
    /// and wrong in a way it could not have been right: the three hulls are one
    /// length to within 3% and the three gauges are not.
    /// </summary>
    private (double Left, double Right) BeltTravel(Vehicle v, double delta)
    {
        double swing = Main.WrapAngle(v.Sprite.HullFacing - v.LastHullFacing);
        v.LastHullFacing = v.Sprite.HullFacing;
        double radius = PivotArm(v);
        return TrackLoop.Split(v.Speed * delta, swing, radius);
    }

    /// <summary>The radius each belt winds about when the hull turns - half the
    /// gauge, on screen.</summary>
    private static double PivotArm(Vehicle v) =>
        (v.Atlas.TrackArm > 0.0
            ? v.Atlas.TrackArm
            : v.Atlas.HullSpan * PivotRadiusFraction) * v.Sprite.BodyScale;

    public void CancelOrder(Vehicle v)
    {
        v.Path = new List<Vector2I>();
        v.PathStep = 0;
        v.Speed = 0.0;
        if (v != Driven)
            return;
        Field.Highlight = Array.Empty<Vector2I>();
        Field.QueueRedraw();
    }

    /// <summary>Distance left in the current straight run, and the speed to be
    /// doing by the end of it.
    ///
    /// The run stops at the first bend on purpose: the tank has to slow there
    /// to swing round, so looking past the bend would start the braking too
    /// late. What it slows *to* depends on what the bend is: the crawl at a
    /// corner, a full stop only at the destination.</summary>
    private (double Distance, double EndSpeed) RemainingRun(Vehicle v)
    {
        double total = (StandOn(v, v.Path[v.PathStep]) - v.Sprite.Position).Length();
        int heading = HexField.HeadingTo(v.Cell, v.Path[v.PathStep]);
        int i = v.PathStep;
        for (; i + 1 < v.Path.Count; i++)
        {
            if (HexField.HeadingTo(v.Path[i], v.Path[i + 1]) != heading)
                break;
            total += (Field.CellAnchor(v.Path[i + 1])
                      - Field.CellAnchor(v.Path[i])).Length();
        }
        bool endOfPath = i + 1 >= v.Path.Count;
        return (total, endOfPath ? 0.0 : v.Profile.CornerSpeed);
    }

    /// <summary>The fastest this tank may cruise at where it is now: its class
    /// figure on the level, two thirds of it going up or down a step, 45% of it
    /// through water - see <see cref="MovementProfile.GradeFraction"/> and
    /// <see cref="MovementProfile.WaterFraction"/>.
    ///
    /// A method rather than the expression inlined at the one place it drives the
    /// speed, because the trace and the panel have to be able to say which of the
    /// two is in force. A tank crawling up a bank and a tank whose order has gone
    /// stale are the same picture, and the cap is the only number that tells them
    /// apart.
    ///
    /// <b>The lower of the two where both apply, and never their product.</b> A
    /// leg is a slope or it is not and it is wet or it is not; multiplying would
    /// invent a third terrain nothing on the board is made of, and the one place
    /// the two meet on this map - the beach at the mouth of the pit - is exactly
    /// where that invention would show.</summary>
    public double SpeedCap(Vehicle v)
    {
        double cap = v.Profile.TopSpeed;
        if (!v.Moving)
            return cap;
        Vector2I next = v.Path[v.PathStep];
        if (Field.IsGrade(v.Cell, next))
            cap = Math.Min(cap, v.Profile.GradeSpeed);
        if (Field.IsWet(v.Cell, next))
            cap = Math.Min(cap, v.Profile.WaterSpeed);
        return cap;
    }

    /// <summary>What is holding this tank back, in one word - or in nothing at all
    /// where the answer is its own class.
    ///
    /// <b>Beside <see cref="SpeedCap"/> rather than inside the two places that
    /// print it</b>, because a cap and the reason for it are one answer: a trace
    /// reading "160" with the panel reading "on a grade" while the tank is in the
    /// water is two statements about one number, and the second one is the one
    /// somebody acts on.</summary>
    public string SpeedCapWhy(Vehicle v)
    {
        if (!v.Moving)
            return "";
        Vector2I next = v.Path[v.PathStep];
        bool grade = Field.IsGrade(v.Cell, next), wet = Field.IsWet(v.Cell, next);
        return grade && wet ? "wading a grade" : wet ? "wading"
            : grade ? "grade" : "";
    }

    private void AdvanceOrder(Vehicle v, double delta)
    {
        Vector2I next = v.Path[v.PathStep];
        int heading = HexField.HeadingTo(v.Cell, next);
        if (heading < 0)
        {
            CancelOrder(v);     // path went stale - do not drive off the grid
            return;
        }

        double diff = Main.WrapAngle(heading - v.Sprite.HullFacing);
        double accelRatio;

        if (Math.Abs(diff) > 0.5)
        {
            // Slow to the cornering crawl and swing round while still creeping.
            // The crawl is a floor, never a target to speed up to, so a standing
            // start still pivots in place - which is what a tank does - while a
            // bend taken at speed stays continuous.
            double crawl = v.Profile.CornerSpeed;
            accelRatio = v.Speed > crawl ? -1.0 : 0.0;
            if (v.Speed > crawl)
                v.Speed = Math.Max(crawl, v.Speed - v.Profile.Accel * delta);
            double budget = v.Profile.TurnRate * delta;
            v.Sprite.TurnHull(Math.Abs(diff) <= budget
                ? diff
                : Math.Sign(diff) * budget);
        }
        else
        {
            (double remaining, double endSpeed) = RemainingRun(v);
            // A ceiling rather than a brake pedal, and the difference is the
            // whole of why a tank used to arrive still doing a third of its
            // cruise and then stop dead in one frame.
            //
            // The old form asked "is the distance left below the braking
            // distance" once a frame and then applied full retardation. The test
            // is only as fine as the step: at cruise the tank covers 4px between
            // asks, so braking began up to 4px late, and 4px of missed braking is
            // sqrt(2 * 420 * 4) = 58px/s still on the clock at the goal. Measured
            // at 72. Nothing was wrong with the arithmetic - the trigger was late,
            // and a trigger can always be late.
            //
            // This is the same curve read the other way round: the fastest this
            // tank may be going and still be able to stop in what is left. It has
            // no moment of engagement to miss, and as `remaining` runs out the
            // ceiling runs down to `endSpeed` on its own, so the deceleration
            // finishes instead of being cut off by arrival.
            double ceiling = Math.Sqrt(endSpeed * endSpeed
                                       + 2.0 * v.Profile.Accel * Math.Max(remaining, 0.0));
            // And no faster than a step allows, whichever way the step goes - see
            // SpeedCap.
            double allowed = Math.Min(SpeedCap(v), ceiling);
            double before = v.Speed;
            // Slowed into rather than snapped to, and the two are told apart here
            // because only one of them can be. The braking ceiling descends at the
            // tank's own retardation by construction, so clamping to it was already
            // gradual; the grade's cap appears the frame a leg onto a bank begins and
            // is 80px/s below cruise, which as a bare Min would be a stop dead in one
            // frame - eleven times what the engine can do - and would report itself
            // to the pitch as a single frame of full brake where the tank has a
            // smooth dip.
            v.Speed = v.Speed > allowed
                ? Math.Max(allowed, v.Speed - v.Profile.Accel * delta)
                : Math.Min(v.Speed + v.Profile.Accel * delta, allowed);
            // What the body actually felt, rather than a flag saying which branch
            // was taken. The pitch is driven by acceleration, and under the
            // ceiling the retardation varies instead of being all or nothing -
            // reporting -1 throughout would give the nose a constant dip where
            // the tank has a smooth one.
            accelRatio = delta > 0.0
                ? Math.Clamp((v.Speed - before) / (v.Profile.Accel * delta), -1.0, 1.0)
                : 0.0;
        }

        UpdatePitch(v, accelRatio, delta);
        UpdateRumble(v, delta);

        // The same StandOn the parking uses, or the tank would drive to where its
        // anchor belongs and stop the height of its float short of the cell.
        Vector2 goal = StandOn(v, next);
        Vector2 to = goal - v.Sprite.Position;
        var budgetPx = (float)(v.Speed * delta);
        // Along the leg rather than toward the goal, because the drawn path is the
        // ground's surface and bends at the shared edge - see StandBetween. The
        // budget is spent on the leg's flat length, which is the ground the tank
        // covers; on a leg with no ramp at either end that is the straight line the
        // march used to walk, so nothing off a ramp changes by a pixel.
        float legSpan = (Field.FlatAnchor(next.X, next.Y)
                         - Field.FlatAnchor(v.Cell.X, v.Cell.Y)).Length();
        float legStep = legSpan > 0.001f ? budgetPx / legSpan : 1.0f;
        if (v.LegDone + legStep >= 1.0f
            || (budgetPx <= 0.0f && to.Length() < 0.5f))
        {
            v.Cell = next;
            v.PathStep++;
            // Park rather than a bare Position: arriving in a cell is also where
            // the depth order changes, and a tank that drove past another without
            // its z index following would pass through it. Driven, not placed, so
            // the lean is left to its spring - see Park.
            Park(v, placed: false);
            if (!v.Moving && v == Driven)
            {
                Field.Highlight = Array.Empty<Vector2I>();
                Field.QueueRedraw();
            }
        }
        else if (budgetPx > 0.0f)
        {
            v.LegDone += legStep;
            v.Sprite.Position = StandBetween(v, v.Cell, next, v.LegDone);
            Climb(v, next);
            Depth(v);
        }
        v.Sprite.QueueRedraw();
    }

    /// <summary>
    /// The height, part way from one cell to the next.
    ///
    /// <see cref="Vehicle.Height"/> is taken from how far along the leg the tank
    /// is rather than integrated frame by frame, for the reason the belts read
    /// their travel back off the heading: a height stepped forward alongside the
    /// driving would have to be remembered by everything else that moves a tank -
    /// the W/S keys, a reset, an order cancelled mid-step. It is exact at both ends
    /// of the leg whatever happened in between.
    ///
    /// <b>It is the surface and no longer a chord of it, and it has to be the same
    /// expression <see cref="StandBetween"/> drew with.</b> Height means the lift
    /// baked into the drawn position - <see cref="Depth"/> subtracts it back out to
    /// recover the flat row, and the waterline takes its depth off it - so a height
    /// that disagreed with the position would not be an approximation, it would be
    /// a wrong answer about which row the tank is standing on. That the two used to
    /// be the same chord is why nothing noticed.
    ///
    /// <b>One height, and there were two.</b> The occlusion rule used to be asked
    /// against the higher of the two ends, so that a tank halfway up a wall was
    /// called up already - which reads well for the cell it is climbing onto and
    /// is wrong about every other cell at that level. Climbing (3,2) to (4,3) the
    /// tank was promoted to the crown at the first pixel of the leg and spent the
    /// whole climb drawn over (3,3), a cell it had not reached and was not level
    /// with. The rule is that a tank covers a hex when it has come up to that
    /// hex's level, so the height that answers it is the height it is drawn at,
    /// and the two only ever differed while that answer was wrong.
    /// </summary>
    private void Climb(Vehicle v, Vector2I next)
    {
        Vector2 from = StandOn(v, v.Cell), goal = StandOn(v, next);
        float span = (goal - from).Length();
        // The leg's own progress rather than a distance read back off the drawn
        // position: the drawn path bends at the shared edge now, so the position no
        // longer says how far along it is - see Vehicle.LegDone.
        float done = Mathf.Clamp(v.LegDone, 0.0f, 1.0f);
        // Which cell's water it is in, if any - the one its contact point is over,
        // which changes at the shared edge halfway along the leg. A body of water
        // is one flat surface, so there is nothing to interpolate: a tank is in it
        // or it is not, and the moment it becomes so is the shoreline the board
        // draws.
        //
        // Ahead of the relief gate, because a flooded board need not be a raised
        // one - and because a waterline left over from last frame is a tank still
        // wading a hundred pixels up the beach.
        Vector2I under = done >= 0.5f ? next : v.Cell;
        v.Waterline = Field.IsWater(under) ? Field.WaterTop(under)
                                            : float.NegativeInfinity;
        if (!Field.HasRelief)
            return;
        v.Height = Field.SurfaceBetween(v.Cell, next, done);
        // And the same ground with the flat mark's clearance on it - see
        // Vehicle.Ground. The two used to be a quarter of a level apart because this
        // one was the surface and that one a chord of it; now they differ by the
        // clearance alone, which is what HexField.MarkClear says it is for.
        v.Ground = Field.MarkBetween(v.Cell, next, done);
        // The heights are the step's, not the frame's - see StepHeights, which
        // carries the whole of why the two ends of one hull are two numbers.
        // The gait is how far the contact point leads the trailing end, as a
        // fraction of this leg: half a hull, in the leg's own units.
        float gait = span <= 0.001f ? 0.0f
            : v.Atlas.HullSpan * v.Sprite.BodyScale * 0.5f / span;
        (v.Standing, v.Trailing) = Main.StepHeights(Field, v.Cell, next, done, gait,
            Main.GearCover * v.Sprite.BodyScale);
        v.Travel = span <= 0.001f ? Vector2.Zero : (goal - from) / span;
        // How much of the hull has crossed onto the far cell's face - see
        // Vehicle.LegBlend. Nought until the nose reaches the seam at 0.5 - gait,
        // one when the tail has passed it at 0.5 + gait, so the span of the change
        // is the hull's own length in the leg's units and needs no number of its
        // own. The same gait as the two ends of the hull above, for the same
        // reason: it is where the hull is.
        v.LegBlend = gait <= 0.001f ? (done >= 0.5f ? 1.0f : 0.0f)
            : Mathf.Clamp((done - (0.5f - gait)) / (2.0f * gait), 0.0f, 1.0f);
        // The climb's split fades driving straight up or down the screen: its
        // seam is the flanker's edge, and there the mounted hex and the ground
        // being passed share the sprite's columns, so the only right split is
        // none - the crown for the whole sprite, the old behaviour. The
        // descent's seam is the leg's own rim and never degenerates, which is
        // why the fade lives here, next to the knowledge of which leg this is,
        // and not in the stage.
        v.Levelling = Field.LevelAt(next) != Field.LevelAt(v.Cell);
        v.OnSlope = Field.IsRamp(v.Cell) || Field.IsRamp(next);
        bool down = Field.LevelAt(next) < Field.LevelAt(v.Cell);
        if (!down)
        {
            v.Trailing = v.Standing
                + (v.Trailing - v.Standing) * Mathf.Abs(v.Travel.X);
            v.SeamLine = Main.SeamLine(Field, Origin, v.Cell, next);
            return;
        }
        // A descent's seam is not pinned to the board at all - it is a wipe.
        // The board's own lines were both tried and each dumped the crown
        // side in a lump: a fragment over the plateau's top is visible at
        // exactly the crown and swallowed whole a hair under it, so whatever
        // single moment flips a region, that whole region blinks out on that
        // frame - measured on (4,3)->(4,2) three ways (six hundred to eight
        // hundred pixels in a frame or two, wherever the moment was put).
        // The only smooth transition available is spatial: a line parallel
        // to the leg's shared edge sweeping down the sprite over the second
        // half of the leg, from above everything (the whole tank still holds
        // the crown, as it did while on the top) to below everything (the
        // whole tank on the drawn ramp, which at the far anchor is exactly
        // the parked picture - so parking pops nothing). Fragments flip one
        // row at a time, and each flip is a few rows a frame.
        // Bottom first, which is why the normal is turned around: the wipe's
        // crown side (visible, still up on the top) must be the sprite's
        // upper part and the sinking nose side its lower, or the mid-hull is
        // cut at the rim while the tracks are still drawn on the plateau
        // below it - a tank torn in two, measured and seen. Swept over the
        // whole leg, because the drawn ramp sinks the sprite from the first
        // pixel and half a leg at cruise is a handful of frames.
        Vector3 axis = Main.SeamEdge(Field, Origin, v.Cell, next);
        float reach = v.Atlas.HullSpan * v.Sprite.BodyScale;
        // The range is the rows where the two depths actually differ - from
        // just under the shadow's far edge to the top of the parked cut.
        // Wider spends sweep on rows where nothing shows and concentrates
        // the visible part of the wipe into fewer frames.
        float slide = Mathf.Lerp(0.25f * reach, -0.35f * reach, done);
        v.SeamLine = new Vector3(-axis.X, -axis.Y,
            -(axis.X * v.GroundPoint.X + axis.Y * v.GroundPoint.Y) + slide);
    }

    /// <summary>
    /// The plane under a tank, as a gradient in world ground coordinates, for
    /// <see cref="ClimbLean"/>.
    ///
    /// <b>A plane and not a grade, because two things are drawn from it and only
    /// one of them is the pitch.</b> The body takes the component along its
    /// heading; the contact shadow lies in the face and takes the whole of it,
    /// including on the headings where the component is nought. A grade along the
    /// travel cannot be turned back into a plane - divide by the cosine and it
    /// blows up in exactly the case that matters, a tank standing broadside on a
    /// face, where the travel grade is zero and the face is not.
    ///
    /// <b>Read off the face the tank is standing on, not off the ends of its
    /// leg</b>, and that is what lets a tank stopped on a ramp stay leaning. A
    /// slope is a property of the ground; asked as "how much higher is the cell I
    /// am going to", the question has no answer once the tank has arrived, so the
    /// spring levelled it out - a tank standing on a visible slope, drawn flat.
    ///
    /// A ramp's top is <see cref="HexField.StepGrade"/> steep and its gradient
    /// points along its axis, so what the hull feels is that steepness projected
    /// onto its heading: full up the axis, negated coming down it, and nothing
    /// across it. The projection also removes the sign that used to be taken from
    /// which end was higher - one statement where there were two.
    ///
    /// <b>Across the slope it says level, and that is the class rather than the
    /// map.</b> A tank standing broadside on a ramp really is rolled, and
    /// <see cref="ClimbLean"/> is one rotation about the pitch axis - the roll
    /// would want the other one, and the two do not compose into a rigid motion of
    /// a flat sprite any more than the foreshortening does. Named because it is
    /// visible: park across a face and the body stands square on a slanted tile.
    /// Its shadow, which can express the whole plane, is not square on it.
    ///
    /// Steps between two flat levels keep their old answer, the difference in
    /// levels laid along the travel: there is no face there, only a wall, and a
    /// tank crossing it is climbing nothing - it is being carried up. Given a
    /// direction anyway, so the shadow follows the body it belongs to rather than
    /// staying flat under a hull the same fiction has tipped.
    /// </summary>
    public Vector2 SurfaceSlope(Vehicle v)
    {
        if (!Field.HasRelief)
            return Vector2.Zero;
        Vector2I next = v.Onto;
        // Mixed between the two cells by how much of the hull has crossed, not
        // taken from whichever of them is a ramp - see Vehicle.LegBlend. Driving
        // onto a face this rises from nothing as the nose reaches it; cresting
        // onto the flat top it falls away the same way, which is the same
        // statement read backwards. Mixed as planes rather than as grades, which
        // is the same number for the body - a lerp of the projections is the
        // projection of the lerp - and the only one of the two the shadow can use.
        if (Field.IsRamp(v.Cell) || Field.IsRamp(next))
            return FaceSlope(v.Cell).Lerp(FaceSlope(next), v.LegBlend);
        int heading = HexField.HeadingTo(v.Cell, next);
        if (!v.Moving || heading < 0)
            return Vector2.Zero;
        return ClimbLean.Plane(
            (Field.LevelAt(next) - Field.LevelAt(v.Cell)) * Field.StepGrade,
            heading);
    }

    /// <summary>The gradient of one cell's own top: a ramp's steepness pointed up
    /// its axis, and nothing at all for a cell whose top is flat. Asked of the
    /// cell and not of the tank, because a face is a property of the ground - the
    /// projection onto whoever is standing on it happens later, in
    /// <see cref="ClimbLean.Along"/>.</summary>
    private Vector2 FaceSlope(Vector2I cell)
    {
        int face = Field.RampHeading(cell);
        return face < 0 ? Vector2.Zero
                        : ClimbLean.Plane(Field.StepGrade, face);
    }

    /// <summary>What the sprung plane is worth along a hull's heading, for the
    /// trace: the number the pitch is drawn from.</summary>
    public double SurfaceGrade(Vehicle v) =>
        ClimbLean.Along(SurfaceSlope(v), v.Sprite.HullFacing);

    private void UpdateLean(Vehicle v, double delta)
    {
        v.Lean.Update(SurfaceSlope(v), delta);
        v.Sprite.Climb = v.Lean.Angle(v.Sprite.HullFacing, Field.RiseFactor);
        // The same plane as the line above, in the terms a layer printed on the
        // ground needs it in - see TankSprite.Slope. One spring, two readings, so
        // the shadow cannot lag the hull standing on it.
        v.Sprite.Slope = ClimbLean.Print(v.Lean.Slope, Field.Squash,
                                         Field.RiseFactor);
        v.Sprite.Rise = Field.Lift <= 0.0f
            ? 1.0f
            : 1.0f + TankSprite.RisePerLevel * (v.Height / Field.Lift);
        if (v.Sprite.Climbing)
            v.Sprite.QueueRedraw();
    }

    public void UpdatePitch(Vehicle v, double accelRatio, double delta)
    {
        if (!PitchEnabled)
        {
            v.Pitch.Reset();
            v.Sprite.Pitch = 0.0;
            return;
        }
        v.Pitch.Update(accelRatio, delta);
        v.Sprite.Pitch = v.Pitch.Angle;
    }

    public void UpdateRumble(Vehicle v, double delta)
    {
        // What the jolt has to be whole against, and it is pushed rather than
        // pulled because the sprite has no business reading the camera. Every
        // frame, for the reason the tremble level is: a tank built after a zoom
        // would otherwise sit on the old factor.
        v.Sprite.ViewZoom = ViewZoom;
        v.Sprite.Painted = Staged;
        if (!RumbleEnabled)
        {
            v.Rumble.Reset();
            v.Sprite.Shake = 0.0;
            v.Sprite.Roll = 0.0;
            return;
        }
        // What the ground is doing to the ride. A pond bottom is not a field -
        // see BodyRumble.WetDamping, which also says what the ford's full ride
        // was doing to the waterline.
        v.Rumble.Damping = v.Wading ? BodyRumble.WetDamping : 1.0;
        // Where it is standing rather than how far it has come, and in flat space
        // rather than drawn space - the lift goes back on, the way every other
        // caller that asks the board about a point puts it back. See
        // BodyRumble.Advance and HexField.Bare.
        var ground = new Vector2(v.GroundPoint.X - Origin.X,
                                 v.GroundPoint.Y - Origin.Y + Field.Bare(v.Ground));
        // And what that ground is made of, asked of the field rather than worked
        // out here: HexField.KindAt is the one answer to which kind a cell is, and
        // a second one would disagree with the picture on the mixed board. Clamped
        // (CellUnder, not FlatCellAt) because a tank is always standing somewhere.
        Vector2I patchCell = Field.CellUnder(ground);
        v.Rumble.Roughness = Terrain is null ? 1.0
            : Terrain.RideOf(Field.KindAt(patchCell));
        // The cell goes in with the point, and it is the same cell the kind was
        // just read off - the patch is the square and the cell, so the kind takes
        // effect at the edge where it changes rather than at the next square. The
        // delta goes in for the hold, not for the bump: which bump is the place,
        // how long the body carries it is time - see BodyRumble.HoldSeconds.
        v.Rumble.Advance(ground, patchCell, v.Speed, delta);
        v.Sprite.Shake = v.Rumble.Heave;
        v.Sprite.Roll = v.Rumble.Roll;
    }



    /// <summary>
    /// Winds the belts on by the ground that went past.
    ///
    /// Keyed on distance like the rumble, and unlike it that is not a reading of
    /// the terrain but the belt's definition: it is the part in contact with the
    /// ground. So this one is driven from the same <c>_speed * delta</c> and
    /// goes still the moment the tank does, which is correct - a stationary
    /// tank's tracks are stationary, whatever the engine is doing.
    /// </summary>
    public void UpdateTracks(Vehicle v, (double Left, double Right) travel,
                              double delta)
    {
        if (!TracksEnabled || v.Atlas.HasTracks != true)
        {
            if (!v.Sprite.TracksRunning)
                return;
            v.TrackLeft.Reset();
            v.TrackRight.Reset();
            v.Sprite.TrackPhaseLeft = -1;
            v.Sprite.TrackPhaseRight = -1;
            v.Sprite.TrackBlurLeft = 0.0;
            v.Sprite.TrackBlurRight = 0.0;
            v.Sprite.QueueRedraw();
            return;
        }
        v.TrackLeft.Advance(travel.Left, delta);
        v.TrackRight.Advance(travel.Right, delta);
        v.Sprite.TrackPhaseLeft = v.TrackLeft.Frame;
        v.Sprite.TrackPhaseRight = v.TrackRight.Frame;
        v.Sprite.TrackBlurLeft = v.TrackLeft.Blur;
        v.Sprite.TrackBlurRight = v.TrackRight.Blur;
    }

    /// <summary>Runs every frame, moving or not - that is the whole point of it.
    /// The rumble is keyed on distance and goes silent the instant the tank
    /// stops; the engine does not. Speed only moves the frequency, so there is
    /// no threshold anywhere for the effect to switch on or off at.</summary>
    public void UpdateTremble(Vehicle v, double delta)
    {
        // A dead engine does not tremble. Of every switch this state throws,
        // this is the one that carries most: it is on by default precisely
        // because a tank with nothing moving on it reads as wrong, and that is
        // exactly what a wreck is supposed to read as.
        if (!TrembleEnabled || v.Wreck.Dead)
        {
            if (v.Sprite.TremblePitch == 0.0 && v.Sprite.TrembleYaw == 0.0)
                return;
            v.Tremble.Reset();
            v.Sprite.TremblePitch = 0.0;
            v.Sprite.TrembleYaw = 0.0;
            v.Sprite.QueueRedraw();
            return;
        }
        // Here rather than at the drag, so every tank carries the same level and a
        // tank built after it was set cannot be left on the tuned figure - see
        // TrembleLevel.
        v.Tremble.Level = TrembleLevel;
        v.Tremble.Advance(v.Speed, delta);
        v.Sprite.TremblePitch = v.Tremble.Pitch;
        v.Sprite.TrembleYaw = v.Tremble.Yaw;
        v.Sprite.QueueRedraw();
    }

    /// <summary>Runs every frame, moving or not, like the tremble and for the
    /// same reason: it is the engine, not the ground. Speed picks one of two
    /// states, or walks the ramp under --exhaust-ramp.</summary>
    public void UpdateExhaust(Vehicle v, double delta)
    {
        // Both settings before the early return, not after it. The panel's
        // caption reads the rate off this instance, so a clock left on last
        // frame's model while the exhaust is switched off would freeze under the
        // very control describing it - and switching the model back on with the
        // plume off, then on, would show the stale one for a frame.
        v.Exhaust.Level = ExhaustLevel;
        v.Exhaust.Binary = !ExhaustRamp;
        // A wreck's engine is not idling, and this is the plainest of the
        // several ways the bench says so.
        if (!ExhaustEnabled || v.Wreck.Dead || !v.Atlas.HasExhaust)
        {
            if (v.Sprite.ExhaustPhase < 0)
                return;
            v.Exhaust.Reset();
            v.Sprite.ExhaustPhase = -1;
            v.Sprite.QueueRedraw();
            return;
        }
        v.Exhaust.Advance(v.Speed, delta);
        v.Sprite.ExhaustPhase = v.Exhaust.Frame;
        v.Sprite.ExhaustDensity = (float)v.Exhaust.Density;
    }

    /// <summary>
    /// Kill one tank.
    ///
    /// Three rounds past the paint do it, and nothing else - see
    /// <see cref="Land"/> and <see cref="Gunnery.PenetrationsToKill"/>. That is
    /// still an assumption and worth naming as one: the bench has no hit points,
    /// and inventing some would be a second damage model beside the one the class
    /// matchup already is. Counting penetrations leaves the matchup table deciding
    /// *who* can kill whom, which is what it was written to say - a light gun that
    /// can only ever scorch a heavy cannot destroy one however long it keeps at it
    /// - and leaves the count deciding *when*.
    ///
    /// The first version had the deepest scar level be death, and the two
    /// questions were one: a heavy killed a medium with its first shell and a
    /// medium killed a light never, so every cell of the table was one shot or
    /// nothing. Three rounds is what put the middle back.
    ///
    /// Everything here is the tank ceasing to be a machine. Most of the read is
    /// in this list rather than in any pixel: on this field a live tank trembles,
    /// smokes, scans with its turret and rocks when it moves.
    /// </summary>
    public void Kill(Vehicle v)
    {
        if (!v.Wreck.Kill())
            return;
        v.Path.Clear();
        v.PathStep = 0;
        v.Speed = 0.0;
        v.Target = null;
        v.Scan.Suspend();
        // It burns because it has just been destroyed, not because the key was
        // pressed - and the key can no longer put it out, since a wreck that
        // stops smouldering on a keystroke is a switch pretending to be a state.
        v.Burning = true;
        // Nobody goes on shooting at a wreck. Otherwise the engagement runs for
        // ever against a target that cannot answer, which reads as a gunnery
        // fault rather than as a fight that is over.
        foreach (Vehicle other in Vehicles)
            if (other.Target == v)
                other.Target = null;
        // Counted off the shells this hull took, like the impact takes are: the
        // three recordings are variety, and the same one every time reads as one
        // event rather than as three tanks dying.
        v.Audio?.Destroyed(1 + v.HitCount % 3);
        // The biggest of the three shoves, and it goes out from the hull that
        // just let go. In Kill rather than in Land so it happens once: the round
        // that did it has already sent its own, and a wreck taking further fire
        // does not blow up again.
        if (Wood is not null)
            Wood.Shock(v.GroundPoint - Origin, Wood.DeathBlast);
    }

    /// <summary>
    /// The wreck settling, once a frame.
    ///
    /// Everything it writes is a function of one age - see <see cref="Wreck"/> -
    /// so there is no table here and no second clock. It runs before the burn so
    /// the densities it sets are this frame's rather than last frame's, the same
    /// ordering the audio and the layers already need.
    /// </summary>
    private static void UpdateWreck(Vehicle v, double delta)
    {
        if (!v.Wreck.Dead)
            return;
        v.Wreck.Update(delta);
        TankSprite s = v.Sprite;
        // The pose swaps on the hit and the paint blackens over the second after
        // it: the turret dropping is the event, the char is the aftermath. Set
        // here rather than in Kill so that a reset which clears the wreck clears
        // this too, through the one path that owns the state.
        s.Wrecked = true;
        s.Char = v.Wreck.Char;
        s.FireDensity = (float)v.Wreck.Blaze;
        s.SmokeDensity = (float)v.Wreck.Smoke;
        // Once the flame is out there is nothing left to advance but the column,
        // and it is still asked for: the smoke is what says where a tank died
        // from the other side of the board.
        s.QueueRedraw();
    }

    /// <summary>Runs every frame like the exhaust, but takes no speed: see
    /// <see cref="BurnLoop"/>. Both halves are required before anything is
    /// shown, because the flame without its column is the washed-out half of the
    /// effect rather than a cheaper version of it.</summary>
    public void UpdateBurn(Vehicle v, double delta)
    {
        if (!v.Burning || !v.Atlas.HasBurning)
        {
            if (!v.Sprite.Burning && v.Sprite.FirePhase < 0 && v.Sprite.BurnPhase < 0)
                return;
            v.Burn.Reset();
            v.Sprite.Burning = false;
            v.Sprite.FirePhase = -1;
            v.Sprite.BurnPhase = -1;
            v.Sprite.QueueRedraw();
            return;
        }
        v.Burn.Advance(delta);
        v.Sprite.Burning = true;
        v.Sprite.FirePhase = v.Burn.FireFrame;
        v.Sprite.BurnPhase = v.Burn.SmokeFrame;
    }

    /// <summary>
    /// One round out of one tank's gun.
    ///
    /// Takes the vehicle rather than working on the driven one, because a tank
    /// left engaging a target goes on firing after you have selected somebody
    /// else - which is the whole of the attack scene. Key Z is the same thing
    /// aimed at nothing in particular.
    /// </summary>
    public void Fire(Vehicle v)
    {
        v.ShotFrame = 0;
        v.ReloadLeft = v.Profile.ReloadTime;
        // The sprite shear only if it is asked for: the tube's own recoil is the
        // real article now, and the two together show one true movement and one
        // invented one.
        if (RecoilShear)
            v.Recoil.Fire(Main.WrapAngle(v.Sprite.TurretFacing - v.Sprite.HullFacing));
        // The tube goes back on the same trigger and on its own clock: the two
        // events last different lengths of time, so one counter would give one of
        // them the wrong tempo. See RecoilLoop.
        v.Barrel.Fire();
        // Fourth thing off the one trigger, and the only one that is not about
        // the tank at all: the view. Along the gun's own ground direction and
        // unnormalised, so a shot into the screen jolts less than one across it.
        // Any tank's shot, not just the driven one - a gun going off beside you
        // is the same event as your own, and the bench has all three on screen.
        if (ShakeOn && v.Atlas is not null)
            Shake?.Fire(v.Atlas.GroundDirection(v.Sprite.TurretFacing),
                        v.Profile.ShotShake);
        // And the wood, which is the same event told to the other thing on the
        // board that can answer it. Off the tank's contact point rather than the
        // muzzle: the muzzle stands 60-70px out along the gun, and nothing grows
        // within 83 of a cell's centre, so the source and the tube's end are on
        // the same side of every tree there is - it would move the number and
        // not the picture. Any tank's gun, not just the driven one, for the
        // reason the camera shake gives just above.
        if (Wood is not null)
            Wood.Shock(v.GroundPoint - Origin,
                         v.Profile.ShotShake * Wood.ShotBlast);
        // Third thing off one trigger, and a third clock, for the same reason:
        // the gun's report is 1.2s on the light and 3.4s on the heavy, and neither
        // is the 34 frames the flash runs or the 28 the tube takes to come home.
        v.Audio?.Fire();
    }

    /// <summary>The shot runs on screen frames, not seconds. The sheet is a
    /// hand-timed sequence of held frames, so counting frames is what it is
    /// timed in; under --capture the clock is fixed at 1/60 anyway, which is
    /// what makes a shot land on the same sheet frame in two runs.</summary>
    private void UpdateShot(Vehicle v, double delta)
    {
        if (RecoilShear)
        {
            v.Recoil.Update(delta);
            v.Sprite.RecoilPitch = v.Recoil.Pitch;
            v.Sprite.RecoilRoll = v.Recoil.Roll;
        }
        else if (v.Recoil.Moving || v.Sprite.RecoilPitch != 0.0
                 || v.Sprite.RecoilRoll != 0.0)
        {
            // Switched off part way through a ring-down, which has to put the
            // body back rather than leave it leaning: the spring is the only
            // effect here that holds a pose with nothing driving it.
            v.Recoil.Reset();
            v.Sprite.RecoilPitch = 0.0;
            v.Sprite.RecoilRoll = 0.0;
            v.Sprite.QueueRedraw();
        }

        // Both clocks run, whichever source is showing. They are separate
        // tables - sixteen sheet frames against eight rendered phases - but
        // they add up to the same 34 frames, so flipping V mid-shot swaps the
        // picture without moving the moment, which is what makes an A/B
        // comparison one.
        int frame = v.ShotFrame < 0 ? -1 : FlashSheet.FrameAt(v.ShotFrame);
        int phase = v.ShotFrame < 0 ? -1 : EffectLayer.PhaseAt(v.ShotFrame);
        if (v.ShotFrame >= 0)
        {
            v.ShotFrame++;
            if (frame < 0 && phase < 0)
                v.ShotFrame = -1;
        }
        // The gun tube, on its own clock and its own count of frames. Switched
        // off it holds the rest pose rather than disappearing - the gun is still
        // a gun, it just stops moving, which is the A/B against every tank
        // rendered before this layer existed.
        int tube = 0;
        if (v.Atlas.HasRecoil && RecoilTube)
        {
            v.Barrel.Advance();
            tube = v.Barrel.Phase;
        }
        else
        {
            v.Barrel.Reset();
        }

        if (frame != v.Sprite.FlashFrame || phase != v.Sprite.ShotPhase
            || tube != v.Sprite.RecoilPhase || v.Recoil.Moving)
        {
            v.Sprite.FlashFrame = frame;
            v.Sprite.ShotPhase = phase;
            v.Sprite.RecoilPhase = tube;
            v.Sprite.QueueRedraw();
        }
    }

    /// <summary>
    /// One shell into one tank, from a world bearing.
    ///
    /// The victim is a parameter for the reason the shooter is: a tank is shot
    /// at by another tank now, and the one being driven is as likely to be the
    /// gunner as the target. Everything else is unchanged - which is the point,
    /// because the armour model is exactly as good at answering a shell that
    /// came from a neighbouring tank as one that came from a keypress.
    ///
    /// <paramref name="level"/> is the ceiling the round cannot get past, or null
    /// for a round with no ceiling, which digs by <paramref name="bite"/>
    /// instead. Exactly one of the two is in force, and which one is the whole
    /// difference between a shell fired by a tank and a shell asked for by a key
    /// - see <see cref="TankSprite.DamageTo"/>.
    /// </summary>
    public void TakeHit(Vehicle victim, double fromBearing, float calibre,
                         int? level, int bite)
    {
        TankSprite sprite = victim.Sprite;
        AtlasSet atlas = victim.Atlas;
        if (!atlas.HasHit)
            return;
        // Snapped, not taken as given: a shell arrives from a neighbouring
        // cell, so every angle anything hands in resolves to one of the six
        // sides rather than to itself.
        double from = HexField.EdgeHeadings[Main.SideFor(fromBearing)];
        // Deterministic under --capture and --trace, which fix the time step so
        // two runs can be diffed; a random scatter would put the two hits in
        // different places and the diff would measure that instead.
        victim.HitCount++;
        // How far along the plate this one landed, as a fraction of its
        // half-width. It is passed to both halves - the burst that shows the
        // shell arriving and the mark it leaves - because it is one shell, and
        // a hole appearing in the middle of a plate the flash went off the end
        // of is the whole reason this is a shared number rather than two.
        //
        // +-0.45 rather than the burst's old +-0.6: the mark has to stay on the
        // armour, and it is about 17px wide against half-widths of 38 to 61.
        //
        // Two axes, because one made every round on a plate land at the same
        // height - see RiseAt for why the vertical range is the tighter of the two.
        float scatter = ScatterAt(victim.HitCount);
        float rise = RiseAt(victim.HitCount);
        string face = atlas.FaceFor(from, sprite.HullFacing);
        Land(victim, face, scatter, rise, calibre, level, bite);
    }

    /// <summary>A round arriving, with everything about it already settled.
    ///
    /// Split out of <see cref="TakeHit"/> when the shell got a flight: the key
    /// press works out which plate at the moment it lands, and a fired round
    /// worked it out at the trigger and carried it. Both end here, so there is
    /// one place where a tank takes a hit however the hit was arranged.</summary>
    public void Land(Vehicle victim, string face, float scatter, float rise,
                      float calibre, int? level, int bite)
    {
        TankSprite sprite = victim.Sprite;
        // The calibre goes the same way as the scatter and for the same reason:
        // both are settled the moment the round leaves, so both travel with it
        // rather than being looked up again while it is on screen.
        victim.Hit.Strike(face, scatter, rise, calibre);
        // How deep it goes is the calibre's business - see TankSprite.Damage.
        // A light round still walks a plate scorch -> gouge -> breach over three
        // hits, which is what shows that the phase axis is damage and not three
        // renders of one drawing; a heavy one gets there in one.
        int got = level is int cap
            ? sprite.DamageTo(face, scatter, rise, cap)
            : sprite.Damage(face, scatter, rise, bite);
        // The sound is chosen by the same level the mark is, so a round that
        // bounces is heard bouncing and one that goes through is heard going
        // through. Not the calibre directly: a light round on armour already
        // twice hit does go through, and the ear should be told the same thing
        // the plate is.
        //
        // On the tank that was hit rather than on the one that fired: the ricochet
        // rings off this armour, and its player is positioned there.
        //
        // The shell count picks which of the two takes plays - see Struck. It is
        // the victim's, so it counts shells into this hull however many guns are
        // pointed at it.
        victim.Audio?.Struck(got, victim.HitCount);
        // The wood answers the burst, from the hull it went off against. Every
        // round, not only the ones that go through: a shell breaking up on
        // armour is the louder of the two events, and a wood that only flinched
        // at penetrations would be reading the damage table off the trees.
        if (Wood is not null)
            Wood.Shock(victim.GroundPoint - Origin, Wood.HitBlast);
        // Three rounds past the paint kill, and the matchup decides which rounds
        // those are. The two halves are deliberately apart - see
        // Gunnery.PenetrationsToKill. The tally is counted inside Damage/DamageTo,
        // so this asks the tank what it has taken rather than judging the shell
        // that just landed: a plate a heavy has already breached goes no deeper,
        // and a kill read off this round's level would stall there.
        if (sprite.Penetrations >= Gunnery.PenetrationsToKill)
            Kill(victim);
    }

    /// <summary>Where the nth shell lands along its plate, as a fraction of the
    /// plate's half-width. Deterministic on purpose: --capture and --trace fix
    /// the time step so two runs can be diffed, and a random scatter would be
    /// measuring itself.</summary>
    internal static float ScatterAt(int n) => (((n * 37) % 100) / 100.0f - 0.5f) * 0.9f;

    /// <summary>
    /// How far along its plate a hit may sit, as a fraction of the half-width.
    ///
    /// It bounds <see cref="ScatterAt"/> above, which is why they are one number:
    /// the hash was written to reach exactly this far, and a fired round now has
    /// its offset *solved* rather than hashed - see
    /// <see cref="Gunnery.ScatterOntoBore"/> - so the limit had to become a thing
    /// with a name instead of a 0.9 buried in a hash. Both paths land on the same
    /// armour and the mark is the same size on it.
    /// </summary>
    internal const float ScatterLimit = 0.45f;

    /// <summary>
    /// How far a fired round disperses about where the gun was actually aimed,
    /// as a fraction of the plate's half-width.
    ///
    /// A quarter of <see cref="ScatterLimit"/>, and the difference between the
    /// two numbers is the whole change: the old hash reached the full 0.45
    /// because it *was* the placement, and 0.45 of a 28 to 53px half-width runs
    /// almost exactly across the shot, so it was up to 24px of visible daylight
    /// between the tube and its own hole. A group of rounds fired at one tank
    /// from one place is a group, not a plate-wide spread.
    /// </summary>
    internal const float ScatterSpread = 0.12f;

    /// <summary>
    /// Where the nth round from this gun lands along that plate: aimed, then
    /// dispersed, then kept on the armour.
    ///
    /// One function rather than the three steps written out at the trigger,
    /// because the self-test has to be able to ask what actually happens. A check
    /// that re-assembled the same three steps for itself would keep passing after
    /// the trigger stopped doing one of them - the shape of failure this project
    /// names as a check whose answer goes nowhere.
    /// </summary>
    internal static float AimedScatter(Vector2 muzzle, Vector2 bore,
                                       Vector2 centroid, Vector2 tangent,
                                       Vector2 slope, float rise, int n) =>
        Math.Clamp(
            Gunnery.ScatterOntoBore(muzzle, bore, centroid, tangent, slope, rise,
                                    ScatterLimit, ScatterAt(n))
            + ScatterSpread * (ScatterAt(n) / ScatterLimit),
            -ScatterLimit, ScatterLimit);

    /// <summary>
    /// And where up the plate's slope, as a fraction of its half-height.
    ///
    /// **+-0.30 against the tangent's +-0.45, and the asymmetry is the plate's,
    /// not a preference.** A plate is two to three times wider than it is tall
    /// (tangent 28-53px against slope 7-22px across the three tanks), so even an
    /// equal fraction would already draw an ellipse - which is right, a group of
    /// rounds on a wide short plate *is* wider than it is tall. What makes the
    /// vertical fraction the smaller one as well is that the mark is nearly as
    /// tall as the plate is: <c>scar_fit</c> runs 0.62 to 1.30, so vertical
    /// headroom is 5-8% of a half-height on the rear plates and negative on HTP's.
    /// The mark exceeding its plate is not the same as leaving the tank - it slides
    /// onto the deck above or the lower hull - but it is the scarce direction, and
    /// this is the number to pull back if <c>scar_off_armour</c> ever complains.
    ///
    /// A different modulus, not just a different multiplier, and that is not
    /// fussiness: 61n and 37n are both invertible mod 100, so 61n = 53*(37n) and
    /// the vertical would be a *function* of the horizontal - every pair sitting
    /// on one of a handful of lines. 97 is prime and coprime to 100, so the pair
    /// does not repeat for 9700 rounds.
    /// </summary>
    internal static float RiseAt(int n) => (((n * 61) % 97) / 97.0f - 0.5f) * 0.6f;

    private void UpdateHit(Vehicle v, double delta)
    {
        AtlasSet atlas = v.Atlas;
        TankSprite sprite = v.Sprite;
        HitLoop hit = v.Hit;
        int phase = hit.Phase;
        if (phase >= 0)
        {
            // Read every frame, not once at the strike: the hull can turn while
            // the dust is still settling, and the hit has to stay on the plate
            // it landed on rather than sliding round with the heading.
            sprite.HitOffset = atlas.HitOffset(hit.Face, sprite.HullFacing)
                               + atlas.HitTangent(hit.Face, sprite.HullFacing)
                                 * hit.Scatter
                               + atlas.HitSlope(hit.Face, sprite.HullFacing)
                                 * hit.Rise;
            sprite.HitBehind = atlas.HitFacing(hit.Face, sprite.HullFacing) <= 0.0;
            // The shell's calibre, not the dial's. Pushed from here alongside
            // the other two things the layer needs and cannot work out for
            // itself, so what is drawn is what was fired.
            sprite.HitScale = hit.Scale;
        }
        if (phase != sprite.HitPhase)
        {
            sprite.HitPhase = phase;
            sprite.QueueRedraw();
        }
        hit.Advance();
    }

    /// <summary>Suspended while under way, and while anything else is driving
    /// the turret. A locked turret holding its world heading through a
    /// manoeuvre is the feature this harness exists to show; a scan quietly
    /// walking it off that heading would look exactly like the lock failing.</summary>
    public void UpdateScan(Vehicle v, double delta)
    {
        // The spin and the mouse only ever drive the tank being controlled, so
        // they only suspend that one's scan: a tank standing off to the side keeps
        // looking around while you spin the turret of the one you are driving.
        // A tank with a target is laying its gun, and the scan is what it does
        // with nothing to look at - so the target suspends it on that tank only,
        // exactly as the spin and the mouse suspend it on the driven one.
        if (!ScanEnabled || v.Moving || v.Target is not null || v.Wreck.Dead
            || (v == Driven && TurretHeld?.Invoke(v) == true))
        {
            v.Scan.Suspend();
            return;
        }
        double step = 360.0 / v.Atlas.Count;
        if (!v.Scan.Based)
        {
            // The sway is an offset, so it needs a bearing to be an offset from,
            // and the honest one is where the turret has just been left - a tank
            // that has finished laying its gun down a lane sways about that lane.
            // Snapped to a rendered bearing, which costs nothing on screen: the
            // sprite is drawn at the nearest rendered heading regardless, so this
            // moves the number and not the picture.
            double onto = Main.Mod(Math.Round(v.Sprite.TurretFacing / step) * step, 360.0);
            v.Scan.Rest(onto);
            if (onto != v.Sprite.TurretFacing)
            {
                v.Sprite.TurretFacing = onto;
                v.Sprite.QueueRedraw();
            }
        }
        double move = v.Scan.Advance(step, delta);
        if (move == 0.0)
            return;
        v.Sprite.TurretFacing = Main.Mod(v.Sprite.TurretFacing + move, 360.0);
        v.Sprite.QueueRedraw();
    }
}
