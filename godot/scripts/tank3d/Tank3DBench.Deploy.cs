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
/// cell. Ctrl+Enter or the button - «В бой».</item>
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
    private MeshInstance3D? _deployMark;
    private readonly List<Vector2I> _deployCells = new();

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
        _deploy = side;
        _mt.Turn = side;
        if (_side != side && _parked.FirstOrDefault(p => p.Side == side && p.Fate == Fate.Alive) is { } first)
            TakeOver(first);
        DeployShow(side);
        _deployCells.Clear();
        _deployCells.AddRange(DeployZone(side));
        if (_passButton is not null)
            _passButton.Text = "В бой   Ctrl+Enter";
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
        return DeployWhy(cell);
    }

    private void Place(int id, Vector2I cell, int side)
    {
        int? face = side >= 0 ? side : null;
        if (id == _unitId)
            DeployPut(cell, face);
        else if (UnitStanding(id) is { } p)
            DeployStand(p, cell, face);
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
        CurtainDown();
        DeployShow(null);
        _deployCells.Clear();
        if (_deployMark is not null)
            CellMarks(_deployMark, _deployCells, default, default);
        _mt.Turn = _mt.First;
        if (_side != _mt.First && FirstReady(_mt.First) is { } first)
            TakeOver(first);
        if (_passButton is not null)
            _passButton.Text = "Конец хода   Ctrl+Enter";
        Banner($"В бой · раунд 1 · ходят {SideName(_mt.Turn)}", SideInk(_mt.Turn));
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
            _deployMark ??= CellMarker("Deploy");
            Color ink = SideInk(_deploy!.Value);
            CellMarks(_deployMark, _curtainUp ? System.Array.Empty<Vector2I>() : _deployCells,
                      new Color(ink, 0.20f), new Color(ink, 0.85f));
        }
        PointDraw();
        CommandsTick(busy);
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
        if (e is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Enter or Key.KpEnter } enter && enter.CtrlPressed)
        {
            Execute(new Command(CommandKind.Ready));
            return true;
        }
        if (e is not InputEventMouseButton { Pressed: true } click
            || click.ButtonIndex is not (MouseButton.Left or MouseButton.Right))
            return false;
        if (click.ButtonIndex == MouseButton.Left && ParkedAt(click.Position) is { } own && own.Side == _deploy)
        {
            TakeOver(own);
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
        + " · ЛКМ по своему танку — взять, ЛКМ по клетке зоны — поставить, ПКМ — развернуть к клетке · Ctrl+Enter — «В бой»"
        + (_orderNote.Length > 0 ? $" · {_orderNote}" : "");
}
