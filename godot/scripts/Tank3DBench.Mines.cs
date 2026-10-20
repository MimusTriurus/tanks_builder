using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The minefields on the board, in the model's look: a TM-62 lying on every
/// mined cell, and what happens when a hull comes over it.
///
/// <b>The rules are GDD field.md, «мины»</b>: seen by both players always, set
/// off by driving through the hex as well as by stopping on it, spent after,
/// and hurting the tank on their own cell alone - that tank is «Подбит». A
/// tank shoved on to one by a ram goes up the same.
///
/// <b>A mine is a point, and the 2D board's point.</b> Where on its cell it lies
/// is <see cref="Mines.Offset"/>'s hash, so the 3D board and the sprite board
/// put the same mine in the same place; and it goes off the frame the hull's
/// footprint (<see cref="MeasureFootprint"/>) first covers that point, under
/// whichever end got there first - forward that is the nose, shoved backwards
/// the tail. Which end is what the blast throws up.
///
/// <b>Larger than life, on purpose.</b> A TM-62 is 0.32 m across and a hex here
/// about ten metres (the medium's hull is <see cref="HullOfHex"/> of it): true
/// to scale it would be four pixels at board zoom. <see cref="MineWide"/> is
/// the share of the hex it is drawn at instead; the model stays true and is
/// scaled whole.
///
/// <b>The blast is the ground burst's</b> (<see cref="CelBurst"/>): fire and
/// earth out from under the hull and a crater where the mine lay, which the
/// suspension then feels (Tank3DBench.Pits); the tank is knocked out as by
/// <see cref="KnockOut"/>, but by the charge under it rather than a round in
/// its front - the leading end thrown up, the hull jolted, the belts slack.
/// </summary>
public sealed partial class Tank3DBench
{
    /// <summary>How wide the mine is drawn, a share of the hex width - about
    /// four and a half times life, judged so it reads at board zoom and stays
    /// well inside the shadow of a hull parked on it.</summary>
    private const float MineWide = BoardDress.MineWide;

    /// <summary>How hard the charge is against the guns' firepower I..V -
    /// the crater and the burst's size (<see cref="CraterShare"/>): 10 kg of TNT
    /// digs like the heaviest round.</summary>
    private const int MineMight = 5;

    /// <summary>The leading end's throw, rad/s on the pitch spring: about
    /// three times a penetration's.</summary>
    private const float MineThrow = 9.0f;

    private sealed class Laid
    {
        public required Vector2I Cell;
        public required Node3D Node;
        /// <summary>Where it lies, world - its bottom centre on the ground.</summary>
        public required Vector3 At;
        public bool Spent;
        /// <summary>Laid again under a hull (<see cref="MinesReset"/>): not armed
        /// until that hull has left it.</summary>
        public bool Asleep;
    }

    private readonly List<Laid> _mines = new();

    /// <summary>
    /// Lay a mine on every minefield the board has (<see cref="Mines.Marked"/>).
    /// A missing model leaves the cells bare and says so; the rule still holds
    /// on them, which is the bench's bargain with an asset folder that may be
    /// absent.
    /// </summary>
    private void BuildMines()
    {
        if (_field is null || _tile is null || !_show.Mines)
            return;
        List<Vector2I> due = Mines.Marked(_field);
        if (due.Count == 0)
            return;
        if (BoardDress.Mine(out string? note) is not { } art)
        {
            GD.Print($"tank3d: {note} - {due.Count} minefield(s) left bare");
            return;
        }
        if (note is not null)
            GD.Print($"tank3d: {note}");
        foreach (Vector2I cell in due)
        {
            Node3D node = BoardDress.LayMine(this, art, _field, cell, _tile.HexRect.Size, _pbr, Squash, RiseFactor,
                                             out Vector3 at);
            _mines.Add(new Laid { Cell = cell, Node = node, At = at });
            GD.Print($"tank3d: mine on {cell} at ({at.X:F0}, {at.Z:F0}), {MineWide * HexWidth:F0} px across");
        }
    }

    /// <summary>Whether <paramref name="point"/> is under a hull standing at
    /// <paramref name="rig"/> heading <paramref name="ahead"/>, and how far
    /// along it: positive toward the nose.</summary>
    private static bool UnderHull(Vector3 point, Vector3 rig, Vector3 ahead,
                                  (float Along, float Across, float HalfLen, float HalfWide) foot, out float along)
    {
        var left = new Vector3(ahead.Z, 0.0f, -ahead.X);
        Vector3 d = point - rig;
        along = d.Dot(ahead) - foot.Along;
        float across = d.Dot(left) - foot.Across;
        return Mathf.Abs(along) <= foot.HalfLen && Mathf.Abs(across) <= foot.HalfWide;
    }

    /// <summary>Every frame: a live mine under the bench's hull or under the
    /// ram's target goes off.</summary>
    private void MinesTick()
    {
        if (_mines.Count == 0)
            return;
        float h = Mathf.DegToRad(_heading);
        var ahead = new Vector3(Mathf.Sin(h), 0.0f, Mathf.Cos(h));
        foreach (Laid mine in _mines)
        {
            if (mine.Spent)
                continue;
            if (mine.Asleep)
            {
                // Armed the frame no hull is over it any more.
                bool under = UnderHull(mine.At, _rig.Position, ahead, _foot, out _)
                             || (_other is { } hull && UnderHull(mine.At, hull.Rig.Position, hull.Ahead, hull.Foot, out _));
                if (under)
                    continue;
                mine.Asleep = false;
                GD.Print($"tank3d: mine on {mine.Cell} armed");
            }
            // Only a hull that is moving finds it: a mine is laid on a free
            // cell, so one standing on it has been put there (a reset, a pick).
            if (_fate == Fate.Alive && (Mathf.Abs(_speed) > 0.5f || RamRunning)
                && UnderHull(mine.At, _rig.Position, ahead, _foot, out float along))
            {
                Blow(mine);
                MinedOwn(mine, along);
            }
            else if (_other is { } o && o.Slid > 0.0f
                     && UnderHull(mine.At, o.Rig.Position, o.Ahead, o.Foot, out float alongOther))
            {
                Blow(mine);
                MinedOther(o, alongOther);
            }
        }
    }

    /// <summary>The charge goes: the burst and its crater where it lay, the
    /// cell's minefield spent (GDD «мина израсходована»).</summary>
    private void Blow(Laid mine)
    {
        mine.Spent = true;
        mine.Node.Visible = false;
        _field?.SetCoverState(mine.Cell, CoverState.Cleared);
        float share = CraterShare(MineMight);
        WoodBlast(mine.At, 0.14f * share, 150.0f * share);
        if (_celBurst is not null)
        {
            _celBurst.Craters.SunWay = _sun.GlobalBasis.Z;
            var dig = InCell(mine.At, CraterRadius * new Craters().Wide * share * HexWidth);
            _celBurst.Burst(mine.At, dig.Radius, dig.At, dig.Inside);
            WoodCrater(dig.At, dig.Radius);
        }
        _shake.Blast(_profile.ShotShake * 1.2);
        GD.Print($"tank3d: mine on {mine.Cell} went off");
    }

    /// <summary>The bench's own tank over the charge: the end over it thrown
    /// up, the hull heaved, and «Подбит» - <see cref="KnockOut"/>'s fate without
    /// its round in the front. The way it was making is not lost with it: the
    /// hull rolls on to the middle of the mined cell (<see cref="CoastDrive"/>).</summary>
    private void MinedOwn(Laid mine, float along)
    {
        // Pitch negative is nose up: a charge under the nose throws the nose up.
        float end = along >= 0.0f ? 1.0f : -1.0f;
        _kickPitch.Kick(-MineThrow * end * Mathf.Clamp(Mathf.Abs(along) / Mathf.Max(_foot.HalfLen, 1.0f), 0.4f, 1.0f));
        _heave.Kick(0.15f);
        _fate = Fate.Knocked;
        _sinceFate = 0.0f;
        CancelOrder();
        _coastTo = CellWorld(mine.Cell);
        // Too slow to carry it there by itself - a creep - it rolls at a crawl.
        _speed = (_speed < 0.0f ? -1.0f : 1.0f) * Mathf.Max(Mathf.Abs(_speed), 0.2f * MaxSpeed);
        _driveScripted = 0.0f;
        FxKnocked();
        RockedOwn(1.0f);
        _note = "knocked out by a mine";
    }

    /// <summary>The ram's target shoved over the charge: thrown and left in the
    /// knocked-out pose - the gun on its stop, the belts slack.</summary>
    private void MinedOther(Other o, float along)
    {
        float end = along >= 0.0f ? 1.0f : -1.0f;
        o.Pitch.Kick(-MineThrow * end * 0.3f);
        o.Heave.Kick(0.1f);
        o.Model.Droop = 1.0f;
        o.Model.Slackness = 1.0f;
        _note = $"{o.Tag} knocked out by a mine";
    }

    /// <summary>
    /// Where a hull knocked out on the move rolls to: the middle of the cell
    /// it went up on - the board's place for a tank, which a wreck left on the
    /// cell's edge was not. Null when nothing is rolling.
    /// </summary>
    private Vector3? _coastTo;

    /// <summary>
    /// One frame of the roll: no drive any more, only the way the hull had,
    /// spent evenly - the deceleration that stops it exactly on the middle,
    /// <c>v²/2d</c>, asked again every frame - on a straight line there. The
    /// hull eases round on to that line at half the turn rate, nose first if
    /// it was going forward, tail first if backing, so a tank that went up
    /// off the cell's axis does not slide sideways into place.
    /// Out: what the frame does with any drive.
    /// </summary>
    private void CoastDrive(float dt, out float target, out float a, out float turn, out float step)
    {
        target = a = turn = step = 0.0f;
        if (_coastTo is not Vector3 goal)
            return;
        Vector3 pos = _rig.Position;
        Vector3 to = Flat(goal - pos);
        float left = to.Length();
        float back = _speed < 0.0f ? -1.0f : 1.0f;
        float v = Mathf.Abs(_speed);
        if (left <= 0.5f || dt <= 0.0f)
        {
            _coastTo = null;
            _speed = 0.0f;
            GD.Print($"tank3d: rolled to the middle of {CellHere}");
            return;
        }
        float before = Mathf.Abs(_speed);
        v = Mathf.Max(0.0f, v - v * v / (2.0f * left) * dt);
        _speed = back * v;
        a = -Mathf.Clamp((before - v) / (Accel * dt), 0.0f, 1.0f) * back;
        target = 0.0f;
        Vector3 way = to / left;
        if (left > 1.0f)
        {
            float diff = (float)Angles.WrapAngle(HeadingOf(way * back) - _heading);
            float budget = TurnRate * 0.5f * dt;
            float d = Mathf.Abs(diff) <= budget ? diff : Mathf.Sign(diff) * budget;
            Heading += d;
            turn = d / dt;
        }
        float move = Mathf.Min(v * dt, left);
        Vector3 next = pos + way * move;
        if (!CanDrive(pos, next, way))
        {
            _coastTo = null;
            _speed = 0.0f;
            return;
        }
        _rig.Position = next;
        step = back * move / _model.PixelsPerUnit;
    }

    /// <summary>Backspace: the mines back on their cells, live.</summary>
    private void MinesReset()
    {
        _coastTo = null;
        float h = Mathf.DegToRad(_heading);
        var ahead = new Vector3(Mathf.Sin(h), 0.0f, Mathf.Cos(h));
        foreach (Laid mine in _mines)
        {
            mine.Spent = false;
            // Put back under a hull standing on it - a tank that went up on it
            // and was reset where it stood: the next start must not set it off
            // (a mine is laid on a free cell, GDD field.md).
            mine.Asleep = UnderHull(mine.At, _rig.Position, ahead, _foot, out _)
                          || (_other is { } o && UnderHull(mine.At, o.Rig.Position, o.Ahead, o.Foot, out _));
            mine.Node.Visible = true;
            _field?.SetCoverState(mine.Cell, CoverState.Intact);
        }
    }
}
