using System.Collections.Generic;
using System.Linq;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The placement before the first turn (GDD match.md, «Расстановка и первый
/// ход»; docs/battle-core-plan.md, Д9). A side's tanks start on its homes - the
/// row the map drew in its zone - and before the first turn the side may set
/// each anywhere in its zone and turn it to any of the six sides, for nothing,
/// then press «В бой». Player 1 first (the toss, Tank3DBench.Match), then the other.
/// <list type="bullet">
/// <item><b>Blind.</b> While a side places, the other side's tanks are not on
/// the board, and between the two a curtain covers the screen - in hot-seat
/// the screen is handed over hidden. Both are shown when player 2 press «В бой»,
/// and player 1 takes the first turn.</item>
/// <item><b>Only on a map with a zone.</b> A map that marks none starts on its
/// homes, as every match did; a side with no zone of its own places among its
/// homes' cells.</item>
/// <item><b>The mouse.</b> The left button on an own tank takes it, on a cell
/// of the zone sets the taken one there; the right button turns it toward the
/// cell. Enter or the button - «В бой».</item>
/// <item><b>The commands</b> (<c>Tank3DBench.Commands</c>):
/// <c>place:ID:Q,R[:SIDE]</c> and <c>ready</c>; a pass is «В бой» too.</item>
/// </list>
/// The rules core is not told: placement is before its first turn, and the
/// shadow reads the units off the bench when that turn begins.
/// </summary>
public sealed partial class Tank3DBench
{
    /// <summary>The side placing now; null once the match is under way.</summary>
    private Side? _deploy;

    private bool Deploying => _deploy is not null;

    /// <summary>The curtain between the sides is up.</summary>
    private bool _curtainUp;

    private Control? _curtain;
    private Label? _curtainSays;
    private readonly List<Vector2I> _deployCells = new();
    /// <summary>The rose's arrow under the pointer: the side the ghost shows
    /// the tank turned to, on its own cell.</summary>
    private int? _deployArrow;
    /// <summary>Where the left button went down on an own tank while it is
    /// held: the tank carried, set where the button comes up.</summary>
    private Vector2? _deployDrag;
    /// <summary>How far the pointer goes, px, before a press on a tank is a carry and not a take.</summary>
    private const float DragPx = 6.0f;

    // --- the placing side as ghosts ------------------------------------------------

    /// <summary>A surface's own dress, kept while it wears the ghost's.</summary>
    private readonly record struct Dress(GeometryInstance3D Mesh, Material? Override,
                                         GeometryInstance3D.ShadowCastingSetting Shadow);

    /// <summary>The placing side's models dressed as ghosts (the author's
    /// choice, 2026-11-01): not on the field yet, until «В бой». Each with
    /// what its surfaces wore, to give back.</summary>
    private readonly Dictionary<TankModel, GhostDress> _deployDressed = new();

    /// <summary>A model's surfaces as they were, its ghost's fill (its ink
    /// the next pass), and whether that is the taken one's or the rest's.</summary>
    private sealed class GhostDress
    {
        public required List<Dress> Kept;
        public required ShaderMaterial Fill;
        public bool Taken;
    }

    /// <summary>The ghost's fill for the taken one and the rest: the taken
    /// firmer and paler.</summary>
    private static void GhostFillFor(ShaderMaterial fill, bool taken)
    {
        fill.Param("tint", taken ? SelectionRing.Friendly.Lerp(Colors.White, 0.45f) : SelectionRing.Friendly);
        fill.Param("alpha", taken ? 0.66f : GhostFirm);
    }

    /// <summary>Every frame of placement: each of the placing side's models
    /// in the ghost's dress - the one on the rig too, which a take may have
    /// loaded afresh. Allocates only for a model not dressed yet.</summary>
    private void DeployDressTick()
    {
        if (_noUi)
            return;
        DeployDress(_model, true);
        for (int i = 0; i < _parked.Count; i++)
            if (_parked[i].Side == _deploy && _parked[i].Rig.Visible)
                DeployDress(_parked[i].Model, false);
    }

    private void DeployDress(TankModel model, bool taken)
    {
        if (!IsInstanceValid(model))
            return;
        if (_deployDressed.TryGetValue(model, out GhostDress? was))
        {
            // taken or let go since: the other look
            if (was.Taken != taken)
            {
                was.Taken = taken;
                GhostFillFor(was.Fill, taken);
            }
            return;
        }
        // The move's ghost's look, firm: the cursor's own stays the faint one.
        // A pair to each model: the selection's rim (Toon.Halo) hangs after
        // the ink pass it finds, and on an ink all the ghosts shared one hover
        // rimmed them all (the author's note, 2026-11-01).
        var ink = new ShaderMaterial { Shader = GhostInkShader, RenderPriority = Stage3D.RingOrder + 2 };
        var fill = new ShaderMaterial { Shader = GhostFillShader, RenderPriority = Stage3D.RingOrder + 1, NextPass = ink };
        GhostFillFor(fill, taken);
        // a rim hung before the dress hangs on the model's own ink, which the
        // dress hides: off, and hung afresh on the next need - on both inks
        if (_halos.Remove(model, out Toon.Rim? rim))
            rim?.Set(0.0f, SelectionRing.Friendly);
        var kept = new List<Dress>();
        foreach (Node n in model.FindChildren("*", "GeometryInstance3D", true, false))
            if (n is MeshInstance3D or MultiMeshInstance3D && n is GeometryInstance3D g)
            {
                kept.Add(new Dress(g, g.MaterialOverride, g.CastShadow));
                g.MaterialOverride = fill;
                g.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
            }
        _deployDressed[model] = new GhostDress { Kept = kept, Fill = fill, Taken = taken };
    }

    /// <summary>Every ghost dressed by placement given its own dress back.</summary>
    private void DeployUndress()
    {
        foreach (GhostDress dressed in _deployDressed.Values)
            foreach (Dress d in dressed.Kept)
                if (IsInstanceValid(d.Mesh))
                {
                    d.Mesh.MaterialOverride = d.Override;
                    d.Mesh.CastShadow = d.Shadow;
                }
        _deployDressed.Clear();
    }

    /// <summary>The sides stood: placement, when the map marks a zone.</summary>
    private void DeployStart()
    {
        if (!Match || _lineup.Count == 0
            || (_arena.Zone(Parking.Green).Count == 0 && _arena.Zone(Parking.Brown).Count == 0))
            return;
        Log.Print("tank3d: match: placement - green zone " + Zoned(Side.Green) + ", brown zone " + Zoned(Side.Brown));
        // player 1 first (Tank3DBench.Match, the toss)
        DeploySide(_mt.First);
    }

    private string Zoned(Side side) =>
        _arena.Zone(Parking.Sides[side == Side.Green ? 0 : 1]).Count is int n and > 0 ? $"{n} cells" : "its homes";

    /// <summary>A side's zone: the map's, else its homes' cells.</summary>
    private IReadOnlyList<Vector2I> DeployZone(Side side)
    {
        IReadOnlyList<Vector2I> zone = _arena.Zone(side == Side.Green ? Parking.Green : Parking.Brown);
        if (zone.Count > 0)
            return zone;
        return _lineup.Where(u => SideOf(u) == side).Select(u => u.Cell).ToList();
    }

    /// <summary>The side to place: its tanks shown and one on the rig, the
    /// other's off the board, the curtain up.</summary>
    private void DeploySide(Side side)
    {
        // the side placed before back in its own dress, hidden as it goes
        DeployUndress();
        _deployDrag = null;
        _rig.Visible = true;
        DeployCarryEnd();
        _deploy = side;
        _mt.Turn = side;
        if (_side != side && _parked.FirstOrDefault(p => p.Side == side && p.Fate == Fate.Alive) is { } first)
            TakeOver(first);
        DeployShow(side);
        _deployCells.Clear();
        _deployCells.AddRange(DeployZone(side));
        // the view over the zone, not over the tank on the rig: a side
        // stood in a corner kept the zone half off the screen (2026-11-01)
        if (_deployCells.Count > 0)
        {
            Vector3 mid = Vector3.Zero;
            for (int i = 0; i < _deployCells.Count; i++)
                mid += CellMiddle(_deployCells[i]);
            GlideTo(mid / _deployCells.Count);
        }
        // straight down on the board, the side's own edge at the bottom of
        // the screen (the author's note, 2026-11-01) - at once, behind the curtain
        DeployLookDown(side);
        // the toss said on the first curtain (Tank3DBench.Match)
        CurtainUp((side == _mt.First && _tossed ? $"Жребий: первыми ходят {SideName(side)}\n\n" : "")
                  + $"Расстановка: {SideName(side)}\n{(side == Side.Green ? "Коричневые" : "Зелёные")} не смотрят\n\nщелчок или Enter — начать");
        Log.Print($"tank3d: match: {SideName(side)} place their tanks");
    }

    /// <summary>Only <paramref name="side"/>'s tanks on the board; null, all.</summary>
    private void DeployShow(Side? side)
    {
        foreach (Parked p in _parked)
            p.Rig.Visible = side is null || p.Side == side;
    }

    /// <summary>Why the taken tank may not be set on <paramref name="cell"/>, or empty.</summary>
    private string DeployWhy(Vector2I cell)
    {
        if (_field is null || !Deploying)
            return "расстановки нет";
        if (!_deployCells.Contains(cell))
            return $"{MapAt(cell)} не в зоне расстановки {SideName(_deploy!.Value)}";
        if (cell == CellHere)
            return "";
        if (_parked.Any(p => p.Cell == cell))
            return $"на {MapAt(cell)} уже стоит танк";
        if (_field.IsWater(cell) || _field.IsRamp(cell) || _field.FoundationAt(cell) == Foundation.Rock)
            return $"на {MapAt(cell)} танк не поставить";
        return "";
    }

    /// <summary>The tank on the rig set on <paramref name="cell"/>, facing
    /// <paramref name="side"/> or as it faced.</summary>
    private void DeployPut(Vector2I cell, int? side = null)
    {
        int face = side ?? HexSideOf(CellHere, Heading);
        _home = cell;
        Park();
        Heading = HeadingFor(cell, face);
        _orderNote = "";
    }

    /// <summary>A standing tank of the placing side set on a cell.</summary>
    private void DeployStand(Parked p, Vector2I cell, int? side)
    {
        int face = side ?? HexSideOf(p.Cell, p.Heading);
        p.Cell = cell;
        p.Rig.Position = CellMiddle(cell);
        p.Heading = HeadingFor(cell, face);
        p.Rig.Rotation = new Vector3(0.0f, Mathf.DegToRad(p.Heading), 0.0f);
    }

    /// <summary>The <c>place</c> command's gate.</summary>
    private string PlaceWhy(int id, Vector2I cell, int side)
    {
        if (!Deploying)
            return "расстановка окончена";
        if (side >= 0 && !HexField.EdgeHeadings.Contains(side))
            return $"{side} — не грань гекса";
        if (id != _unitId && UnitStanding(id) is not { } p)
            return $"юнита #{id} нет на доске";
        if (id != _unitId && UnitStanding(id)!.Side != _deploy)
            return $"#{id} не {SideName(_deploy!.Value)}";
        if (id != _unitId && UnitStanding(id)!.Cell == cell)
            return "";
        // onto an own tank: the two change places
        if (DeployUnitAt(cell) >= 0)
            return "";
        return DeployWhy(cell);
    }

    /// <summary><c>place</c>: the unit set on the cell - onto an own tank,
    /// the two change places, each keeping its facing.</summary>
    private void Place(int id, Vector2I cell, int side)
    {
        int face = side >= 0 ? side : DeployFace(id);
        Vector2I from = UnitCell(id);
        int mate = cell == from ? -1 : DeployUnitAt(cell);
        if (mate >= 0)
        {
            int mateFace = DeployFace(mate);
            DeploySet(id, cell, face);
            DeploySet(mate, from, mateFace);
            Log.Print($"tank3d: match: #{id} and #{mate} change places, {MapAt(from)} and {MapAt(cell)}");
            return;
        }
        DeploySet(id, cell, face);
    }

    /// <summary>Unit <paramref name="id"/> of the placing side set on
    /// <paramref name="cell"/> facing <paramref name="face"/> - unchecked.</summary>
    private void DeploySet(int id, Vector2I cell, int face)
    {
        if (id == _unitId)
            DeployPut(cell, face);
        else if (UnitStanding(id) is { } p)
            DeployStand(p, cell, face);
    }

    /// <summary>The side unit <paramref name="id"/> faces.</summary>
    private int DeployFace(int id) =>
        id == _unitId ? HexSideOf(CellHere, Heading)
        : UnitStanding(id) is { } p ? HexSideOf(p.Cell, p.Heading) : HexField.EdgeHeadings[0];

    /// <summary>The placing side's unit on <paramref name="cell"/>, or -1. No
    /// allocation: read every frame.</summary>
    private int DeployUnitAt(Vector2I cell)
    {
        if (cell == CellHere)
            return _unitId;
        for (int i = 0; i < _parked.Count; i++)
            if (_parked[i].Cell == cell && _parked[i].Side == _deploy && _parked[i].Rig.Visible)
                return _parked[i].Id;
        return -1;
    }

    /// <summary>Whether the tank on the rig may be set on <paramref name="cell"/>
    /// - <see cref="DeployWhy"/> without its words, read every frame.</summary>
    private bool DeployFree(Vector2I cell)
    {
        if (_field is null || cell == CellHere || !_deployCells.Contains(cell))
            return false;
        for (int i = 0; i < _parked.Count; i++)
            if (_parked[i].Cell == cell)
                return false;
        return !_field.IsWater(cell) && !_field.IsRamp(cell) && _field.FoundationAt(cell) != Foundation.Rock;
    }

    /// <summary>«Вернуть ряд»: the placing side's tanks back on their homes,
    /// facing as the map says.</summary>
    private void DeployReset()
    {
        if (_deploy is not Side side)
            return;
        for (int i = 0; i < _lineup.Count; i++)
            if (SideOf(_lineup[i]) == side)
                DeploySet(i, _lineup[i].Cell, _lineup[i].Facing);
        _orderNote = "";
        Log.Print($"tank3d: match: {SideName(side)} back in their row");
    }

    /// <summary>How many of the placing side's tanks stand off their home or
    /// turned from its facing. No allocation.</summary>
    private int DeployMoved()
    {
        int n = 0;
        for (int i = 0; i < _lineup.Count; i++)
            if (SideOf(_lineup[i]) == _deploy && (UnitCell(i) != _lineup[i].Cell || DeployFace(i) != _lineup[i].Facing))
                n++;
        return n;
    }

    /// <summary>«В бой»: green's done, brown place; brown's done, both shown
    /// and green take the first turn.</summary>
    private void DeployReady()
    {
        if (_deploy is not Side side)
            return;
        Log.Print($"tank3d: match: {SideName(side)} are placed - "
                 + string.Join(", ", Units().Where(u => u.Side == side).Select(u => $"{u.Tag}#{u.Id} {MapAt(UnitCell(u.Id))}")));
        if (side == _mt.First)
        {
            DeploySide(Across(side));
            return;
        }
        _deploy = null;
        _deployDrag = null;
        _rig.Visible = true;
        DeployCarryEnd();
        // on the field now: the ghosts' dress off
        DeployUndress();
        CurtainDown();
        DeployShow(null);
        _deployCells.Clear();
        // DeployTick runs no more: what it kept up is put away here
        DeployPanelTick();
        _mt.Turn = _mt.First;
        if (_side != _mt.First && FirstReady(_mt.First) is { } first)
            TakeOver(first);
        Banner($"В бой · раунд 1 · ходят {SideName(_mt.Turn)}", SideInk(_mt.Turn));
        // from straight down into the isometric view from the side to move, smoothly
        CameraTurnTo(IsoYawOf(_mt.Turn), Mathf.DegToRad(IsoPitchDeg));
        Log.Print($"tank3d: match: both sides placed - in battle, {SideName(_mt.First)} to move");
    }

    /// <summary>Where unit <paramref name="id"/> stands now.</summary>
    private Vector2I UnitCell(int id) =>
        id == _unitId ? CellHere : UnitStanding(id)?.Cell ?? (_other is { Unit: true } o && o.Id == id ? OtherCell(o) : default);

    /// <summary>Every frame of placement: the zone drawn, the commands given.</summary>
    private void DeployTick(bool busy)
    {
        if (_field is not null && _tile is not null && !_noUi)
        {
            DeployDressTick();
            DeployCarryTick();
            DeployGhost();
        }
        DeployPanelTick();
        PointDraw();
        CommandsTick(busy);
    }

    /// <summary>
    /// The zone, from <c>CourseTick</c> as a bonus act's hexes are
    /// (<c>PickZone</c>): drawn as the move's zone - one rim round the whole
    /// of it, on the relief stood off the hexes' edges (<c>ZoneGap</c>) - in
    /// the side's colour (the author's note, 2026-11-01: each hex rimmed on
    /// its own did not read as one zone). Under the curtain, none.
    /// </summary>
    private void DeployZoneDraw(ImmediateMesh mesh)
    {
        if (_deploy is not Side side || _curtainUp || _field is null || _tile is null || _deployCells.Count == 0)
            return;
        _zoneFill.Clear();
        _zoneFill.AddRange(_deployCells);
        Color ink = SideInk(side);
        if (_relief is not null)
        {
            ReliefZoneShow(ink);
            return;
        }
        Vector2[] corner = Corners();
        mesh.SurfaceBegin(Mesh.PrimitiveType.Triangles);
        var tint = new Color(ink, ZoneTone);
        foreach (Vector2I cell in _zoneFill)
            ZoneCell(mesh, tint, cell, corner);
        ZoneBand(corner, 1);
        var line = new Color(ink, 0.95f);
        for (int i = 0; i < _band.Count; i++)
        {
            mesh.SurfaceSetColor(line);
            mesh.SurfaceAddVertex(_band[i]);
        }
        mesh.SurfaceEnd();
    }

    /// <summary>The ghost, after <c>CourseTick</c> has put it away: on the
    /// rig's own cell turned to the arrow under the pointer, or on the free
    /// cell under it as the tank faces now - where a click would set it.</summary>
    private void DeployGhost()
    {
        _deployArrow = null;
        // shown again unless an arrow under the pointer puts it away below
        _rig.Visible = true;
        if (_curtainUp || _fate != Fate.Alive || _field is null)
            return;
        Vector2 mouse = GetViewport().GetMousePosition();
        // A tank carried (Tank3DBench.Deploy, DeployCarryTick): the tank
        // itself hangs under the pointer; the ghost stands on the hex the
        // release will set it on - firm where that hex takes it (free, or an
        // own tank to change places with), faint where it does not.
        if (_deployDrag is Vector2 from && mouse.DistanceTo(from) >= DragPx)
        {
            if (PickAt(mouse) is var (held, _, _))
            {
                Vector2I near = _field.FlatCellAt(Board(held));
                bool fits = DeployFree(near) || (near != CellHere && DeployUnitAt(near) >= 0);
                if (near != CellHere)
                    GhostAt(near, HullSide(), fits, overOwn: true);
            }
            return;
        }
        // a tank carried crosses the arrows, it does not turn on them
        if (_deployDrag is null && _carryAt is null && DeployArrowAt(mouse) is int side)
        {
            _deployArrow = side;
            // the turn shown by the ghost alone: the hull as it stands is
            // put away meanwhile (the author's note, 2026-11-01)
            _rig.Visible = false;
            GhostAt(CellHere, side, true, overOwn: true);
            return;
        }
        if (PickAt(mouse) is var (point, _, _) && _field.FlatCellAt(Board(point)) is var cell && DeployFree(cell))
            GhostAt(cell, HullSide(), false);
    }

    // --- the tank picked up, as a piece off a chessboard ---------------------------

    /// <summary>How high a picked-up tank hangs, in hex widths, and how fast it
    /// goes up, down and after the pointer, 1/s.</summary>
    private const float LiftHexes = 0.32f, LiftRate = 11.0f, CarryRate = 16.0f;

    /// <summary>Where the picked-up tank's model is, world, on the ground's
    /// level - null when it rests on its rig's cell.</summary>
    private Vector3? _carryAt;
    /// <summary>How far up it is, 0..1.</summary>
    private float _lift;

    /// <summary>
    /// The carry (the author's note, 2026-11-01): as a chess piece - the press
    /// lifts the tank off its cell, it follows the pointer with a little lag
    /// while the button is held, and the release lowers it onto the hex the
    /// rig is set on (or back onto its own, refused). Only the model moves,
    /// inside its rig: the rig, which the cell is read off (<c>CellHere</c>),
    /// stays put until the release sets it.
    /// </summary>
    private void DeployCarryTick()
    {
        bool held = _deployDrag is not null;
        if (!held && _carryAt is null)
            return;
        float dt = (float)GetProcessDeltaTime();
        Vector3 rest = _rig.GlobalPosition;
        Vector3 want = rest;
        Vector2 mouse = GetViewport().GetMousePosition();
        if (_deployDrag is Vector2 from && mouse.DistanceTo(from) >= DragPx && PickAt(mouse) is var (point, _, _))
            want = new Vector3(point.X, rest.Y, point.Z);
        _lift = Mathf.Lerp(_lift, held ? 1.0f : 0.0f, 1.0f - Mathf.Exp(-LiftRate * dt));
        Vector3 at = (_carryAt ?? rest).Lerp(want, 1.0f - Mathf.Exp(-CarryRate * dt));
        // down and home: at rest again
        if (!held && _lift < 0.01f && at.DistanceTo(rest) < 0.01f * HexWidth)
        {
            DeployCarryEnd();
            return;
        }
        _carryAt = at;
        // the lift eased, so the piece leaves and meets the board softly
        float up = Mathf.SmoothStep(0.0f, 1.0f, _lift) * LiftHexes * HexWidth;
        _model.Position = _rig.ToLocal(at + Vector3.Up * up);
    }

    /// <summary>The model back in its rig, at rest: before another is taken
    /// (it goes to stand as it is), and when placement ends.</summary>
    private void DeployCarryEnd()
    {
        _carryAt = null;
        _lift = 0.0f;
        _model.Position = Vector3.Zero;
    }

    /// <summary>The rose's arrow under <paramref name="screen"/>: one to each
    /// of the six sides round the tank on the rig, the one it faces too -
    /// the turn is free in placement.</summary>
    private int? DeployArrowAt(Vector2 screen)
    {
        if (!Deploying || _curtainUp || _field is null || _tile is null)
            return null;
        float r = RoseDisc();
        Vector2I cell = CellHere;
        Vector2 mid = Screen(cell);
        // all six in placement, the one the hull faces too (the author's note, 2026-11-01)
        foreach (int side in HexField.EdgeHeadings)
            if (SpinDisc(cell, mid, side).DistanceTo(screen) <= r * 1.3f)
                return side;
        return null;
    }

    /// <summary>The placement's rose (<c>DrawCourse</c>): the turn on the spot's
    /// arrows, always up round the tank on the rig, the one under the
    /// pointer lit.</summary>
    private void DrawDeploy(Control ink)
    {
        // none while a tank is up off its cell
        if (!Deploying || _curtainUp || _fate != Fate.Alive || _carryAt is not null)
            return;
        float r = RoseDisc();
        Vector2I cell = CellHere;
        Vector2 mid = Screen(cell);
        foreach (int side in HexField.EdgeHeadings)
        {
            Vector2 at = SpinDisc(cell, mid, side);
            RoseDraw(ink, at, at - mid, r, 1.0f, side == _deployArrow, side == _deployArrow);
        }
    }

    /// <summary>The mouse and the keys in placement; true when taken.</summary>
    private bool DeployInput(InputEvent e)
    {
        if (!Deploying)
            return false;
        if (_curtainUp)
        {
            if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }
                or InputEventKey { Pressed: true, Echo: false, Keycode: Key.Enter or Key.KpEnter or Key.Space })
                CurtainDown();
            return e is InputEventMouseButton or InputEventKey;
        }
        // Enter, as the turn's end in the battle (with Ctrl, as before)
        if (e is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Enter or Key.KpEnter })
        {
            Execute(new Command(CommandKind.Ready));
            return true;
        }
        // the carried tank let go: set where the pointer is, onto an own tank
        // the two change places (Place); a press let go where it began is a take
        if (e is InputEventMouseButton { Pressed: false, ButtonIndex: MouseButton.Left } drop && _deployDrag is Vector2 from)
        {
            _deployDrag = null;
            // back on the board, where the release sets it or where it stood
            _rig.Visible = true;
            if (drop.Position.DistanceTo(from) >= DragPx && _field is not null
                && PickAt(drop.Position) is var (at, _, _) && _field.FlatCellAt(Board(at)) is var to && to != CellHere)
                Execute(new Command(CommandKind.Place, to, -1, _unitId));
            return true;
        }
        if (e is not InputEventMouseButton { Pressed: true } click
            || click.ButtonIndex is not (MouseButton.Left or MouseButton.Right))
            return false;
        // an arrow of the rose first: it may stand over a neighbour's hull
        if (click.ButtonIndex == MouseButton.Left && DeployArrowAt(click.Position) is int arrow)
        {
            DeployPut(CellHere, arrow);
            return true;
        }
        if (click.ButtonIndex == MouseButton.Left && ParkedAt(click.Position) is { } own && own.Side == _deploy)
        {
            // Shift: the tank on the rig and this one change places
            if (click.ShiftPressed)
                Execute(new Command(CommandKind.Place, own.Cell, -1, _unitId));
            else
            {
                // taken, and carried while the button is held (2026-11-01);
                // the one still coming down set at rest first - it goes to stand
                DeployCarryEnd();
                TakeOver(own);
                _deployDrag = click.Position;
            }
            return true;
        }
        // the tank on the rig pressed: carried
        if (click.ButtonIndex == MouseButton.Left && !click.ShiftPressed && Drawn(_rig, _model, click.Position) is not null)
        {
            _deployDrag = click.Position;
            return true;
        }
        if (PickAt(click.Position) is not var (point, _, onOwn) || _field is null)
            return true;
        Vector2I cell = _field.FlatCellAt(Board(point));
        if (click.ButtonIndex == MouseButton.Right)
        {
            if (cell != CellHere)
                DeployPut(CellHere, SideToward(CellHere, cell));
            return true;
        }
        if (onOwn || cell == CellHere)
            return true;
        string why = DeployWhy(cell);
        if (why.Length > 0)
            _orderNote = why;
        else
            DeployPut(cell);
        return true;
    }

    /// <summary>The side of <paramref name="from"/> that looks most toward <paramref name="to"/>.</summary>
    private int SideToward(Vector2I from, Vector2I to)
    {
        Vector3 way = (CellWorld(to) - CellWorld(from)).Normalized();
        int best = HexField.EdgeHeadings[0];
        float dot = -2.0f;
        foreach (int side in HexField.EdgeHeadings)
        {
            float d = HexWay(from, side).Dot(way);
            if (d > dot)
            {
                dot = d;
                best = side;
            }
        }
        return best;
    }

    // --- the curtain ---------------------------------------------------------------

    /// <summary>The curtain between the sides (<see cref="BuildMatchUi"/>).</summary>
    private void BuildCurtain(CanvasLayer layer)
    {
        var curtain = new ColorRect
        {
            // opaque: a curtain one sees the board through is no curtain
            Color = new Color(0.07f, 0.08f, 0.09f),
            MouseFilter = Control.MouseFilterEnum.Stop,
            // over the action panel's group marks, which stand over their frames (ZIndex 1)
            ZIndex = 10,
            Visible = false,
        };
        curtain.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        curtain.GuiInput += e =>
        {
            if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
                CurtainDown();
        };
        var says = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        says.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        says.AddThemeFontSizeOverride("font_size", 34);
        curtain.AddChild(says);
        layer.AddChild(curtain);
        _curtain = curtain;
        _curtainSays = says;
        if (_curtainUp)
            CurtainUp(_curtainText);
    }

    private string _curtainText = "";

    private void CurtainUp(string text)
    {
        _curtainUp = true;
        _curtainText = text;
        if (_curtain is null || _curtainSays is null)
            return;
        _curtainSays.Text = text;
        _curtainSays.AddThemeColorOverride("font_color", _deploy is Side s ? SideInk(s) : Colors.White);
        _curtain.Visible = true;
    }

    private void CurtainDown()
    {
        _curtainUp = false;
        if (_curtain is not null)
            _curtain.Visible = false;
    }

    /// <summary>The HUD's line in placement.</summary>
    private string DeployHud() =>
        $"Расстановка: {SideName(_deploy!.Value)} · {_modelTag} на {MapAt(CellHere)}"
        + (_orderNote.Length > 0 ? $" · {_orderNote}" : "");

    // --- the camera: straight down in placement, isometric into battle ------------

    /// <summary>Straight down, short of the pole, where the yaw stops meaning
    /// anything, degrees above the ground.</summary>
    private const float DeployPitchDeg = 89.5f;
    /// <summary>How long the view takes from straight down into the battle's, s.</summary>
    private const float CameraTurnTime = 1.4f;

    /// <summary>The view's turn under way (<see cref="CameraTurnTo"/>): from
    /// and to, the orbit's own yaw and pitch, and how far along, 0..1 - below
    /// nought, none.</summary>
    private float _turnYawFrom, _turnPitchFrom, _turnYawTo, _turnPitchTo, _turnT = -1.0f;

    /// <summary>The isometric view from <paramref name="side"/>'s edge: the
    /// bench's default for green, half a turn round for brown.</summary>
    private static float IsoYawOf(Side side) =>
        Mathf.Wrap(Mathf.DegToRad(IsoYawDeg) + (side == Side.Brown ? Mathf.Pi : 0.0f), -Mathf.Pi, Mathf.Pi);

    /// <summary>Placement's view (the author's note, 2026-11-01): straight down
    /// on the board, <paramref name="side"/>'s own edge at the bottom of the
    /// screen - green look up the board, brown down it.</summary>
    private void DeployLookDown(Side side)
    {
        _turnT = -1.0f;
        _angleDefault = false;
        _orbitYaw = side == Side.Brown ? Mathf.Pi : 0.0f;
        _orbitPitch = Mathf.DegToRad(DeployPitchDeg) - Mathf.Asin(Squash);
    }

    /// <summary>The view turned to <paramref name="yaw"/> and
    /// <paramref name="pitch"/> (radians, the pitch above the ground) over
    /// <see cref="CameraTurnTime"/>, eased at both ends, the short way round.</summary>
    private void CameraTurnTo(float yaw, float pitch)
    {
        _angleDefault = false;
        _turnYawFrom = _orbitYaw;
        _turnPitchFrom = _orbitPitch;
        _turnYawTo = _orbitYaw + Mathf.Wrap(yaw - _orbitYaw, -Mathf.Pi, Mathf.Pi);
        _turnPitchTo = pitch - Mathf.Asin(Squash);
        _turnT = 0.0f;
    }

    /// <summary>
    /// The turn handed over (<c>HandOver</c>; the author's note, 2026-11-01):
    /// the view round, smoothly, so the side to move has its own units
    /// nearest the screen - half a turn when it looks from the other side's
    /// half of the board (green's edge is at the bottom of the screen at yaw
    /// 0, brown's at 180), nothing when it already looks from this one's. The
    /// step the player turned it to is kept, mirrored; the pitch too.
    /// </summary>
    private void ViewToSide(Side side)
    {
        float yaw = _turnT >= 0.0f ? _turnYawTo : YawStep(_orbitYaw);
        float home = side == Side.Brown ? Mathf.Pi : 0.0f;
        if (Mathf.Abs(Mathf.Wrap(yaw - home, -Mathf.Pi, Mathf.Pi)) <= 0.5f * Mathf.Pi + 1e-3f)
            return;
        float pitch = Mathf.Asin(Squash) + (_turnT >= 0.0f ? _turnPitchTo : _orbitPitch);
        CameraTurnTo(yaw + Mathf.Pi, pitch);
    }

    /// <summary>Every frame of a match (<c>MatchTick</c>): the turn along; the
    /// middle button or Home ends it.</summary>
    private void CameraTurnTick(float dt)
    {
        if (_turnT < 0.0f)
            return;
        _turnT = Mathf.Min(1.0f, _turnT + dt / CameraTurnTime);
        float k = Mathf.SmoothStep(0.0f, 1.0f, _turnT);
        _orbitYaw = Mathf.Wrap(Mathf.Lerp(_turnYawFrom, _turnYawTo, k), -Mathf.Pi, Mathf.Pi);
        _orbitPitch = Mathf.Lerp(_turnPitchFrom, _turnPitchTo, k);
        if (_turnT >= 1.0f)
            _turnT = -1.0f;
    }

    // --- the placement's panel -----------------------------------------------------

    private PanelContainer? _deployPanel;
    private Label? _deploySays;
    /// <summary>What the panel's line says: the moved count, redrawn when it changes.</summary>
    private int _deployMovedShown = -1;

    /// <summary>The panel under the board in placement, where the action
    /// panel stands in the battle (<see cref="BuildMatchUi"/>): the side, how
    /// many of its tanks are off their row, «Вернуть ряд», and the hands.</summary>
    private void BuildDeployPanel(CanvasLayer layer)
    {
        var panel = new PanelContainer
        {
            Name = "DeployPanel",
            AnchorLeft = 0.5f, AnchorRight = 0.5f, AnchorTop = 1.0f, AnchorBottom = 1.0f,
            GrowHorizontal = Control.GrowDirection.Both,
            GrowVertical = Control.GrowDirection.Begin,
            OffsetTop = -12.0f, OffsetBottom = -12.0f,
            MouseFilter = Control.MouseFilterEnum.Stop,
            Visible = false,
        };
        if (_peekBox is not null)
            panel.AddThemeStyleboxOverride("panel", _peekBox);
        // the unit card's box has no room inside it: the panel's own
        var pad = new MarginContainer();
        pad.AddThemeConstantOverride("margin_left", 16);
        pad.AddThemeConstantOverride("margin_right", 10);
        pad.AddThemeConstantOverride("margin_top", 8);
        pad.AddThemeConstantOverride("margin_bottom", 8);
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 18);
        var says = new Label { VerticalAlignment = VerticalAlignment.Center };
        says.AddThemeFontSizeOverride("font_size", 15);
        says.AddThemeColorOverride("font_color", CardPale);
        row.AddChild(says);
        var reset = new Button
        {
            Text = "Вернуть ряд",
            FocusMode = Control.FocusModeEnum.None,
            TooltipText = "все танки стороны — на свои места ряда, как стояли",
        };
        reset.AddThemeFontSizeOverride("font_size", 15);
        reset.Pressed += DeployReset;
        row.AddChild(reset);
        pad.AddChild(row);
        panel.AddChild(pad);
        layer.AddChild(panel);
        _deployPanel = panel;
        _deploySays = says;
    }

    /// <summary>Every frame of placement: the panel up with the curtain down,
    /// its line rewritten only when the count changes.</summary>
    private void DeployPanelTick()
    {
        if (_deployPanel is null || _deploySays is null)
            return;
        bool up = Deploying && !_curtainUp;
        _deployPanel.Visible = up;
        if (!up)
        {
            _deployMovedShown = -1;
            return;
        }
        int moved = DeployMoved();
        if (moved == _deployMovedShown)
            return;
        _deployMovedShown = moved;
        int all = 0;
        for (int i = 0; i < _lineup.Count; i++)
            if (SideOf(_lineup[i]) == _deploy)
                all++;
        _deploySays.Text = $"Расстановка: {SideName(_deploy!.Value)} · переставлено {moved} из {all}\n"
                           + "ЛКМ: танк — взять, клетка — поставить, стрелка — развернуть · Shift+ЛКМ по танку — поменять местами";
    }
}
