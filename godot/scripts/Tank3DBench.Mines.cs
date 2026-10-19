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
    private const float MineWide = 0.14f;

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
    }

    private readonly List<Laid> _mines = new();
    /// <summary><c>--no-mines</c>: the minefields stay ground.</summary>
    private bool _noMines;

    /// <summary>
    /// Lay a mine on every minefield the board has (<see cref="Mines.Marked"/>).
    /// A missing model leaves the cells bare and says so; the rule still holds
    /// on them, which is the bench's bargain with an asset folder that may be
    /// absent.
    /// </summary>
    private void BuildMines()
    {
        if (_field is null || _tile is null || _noMines)
            return;
        List<Vector2I> due = Mines.Marked(_field);
        if (due.Count == 0)
            return;
        string glb = AssetRoot.Mine + "/tm62.glb";
        var doc = new GltfDocument();
        var state = new GltfState();
        if (doc.AppendFromFile(glb, state) != Error.Ok)
        {
            GD.Print($"tank3d: no mine model at {glb} - {due.Count} minefield(s) left bare");
            return;
        }
        float across = 0.328f;   // tm62.json's size.diameter, if it is not there
        try
        {
            using JsonDocument side = JsonDocument.Parse(File.ReadAllText(AssetRoot.Mine + "/tm62.json"));
            across = side.RootElement.GetProperty("size").GetProperty("diameter").GetSingle();
        }
        catch (System.Exception e)
        {
            GD.Print($"tank3d: tm62.json: {e.Message} - {across} m across");
        }
        float scale = MineWide * HexWidth / across;
        foreach (Vector2I cell in due)
        {
            var model = (Node3D)doc.GenerateScene(state);
            if (!_pbr)
                Toon.Dress(model);
            Vector2 flat = _field.FlatAnchor(cell) + _field.CentreOffset + Mines.Offset(cell, _tile.HexRect.Size);
            Vector3 at = Foot(new Vector3(flat.X, 0.0f, flat.Y / Squash));
            // Turned by the cell's hash too, so a row of them is not a stamp.
            float turn = Plumes.Hash01(cell.Y * 1021 + cell.X, 271) * Mathf.Tau;
            var node = new Node3D
            {
                Name = $"Mine_{cell.X}_{cell.Y}",
                Transform = new Transform3D(new Basis(Vector3.Up, turn).Scaled(Vector3.One * scale), at),
            };
            node.AddChild(model);
            AddChild(node);
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
            // Only a hull that is moving finds it: a mine is laid on a free
            // cell, so one standing on it has been put there (a reset, a pick).
            if (_fate == Fate.Alive && (Mathf.Abs(_speed) > 0.5f || RamRunning)
                && UnderHull(mine.At, _rig.Position, ahead, _foot, out float along))
            {
                Blow(mine);
                MinedOwn(along);
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
    /// its round in the front.</summary>
    private void MinedOwn(float along)
    {
        // Pitch negative is nose up: a charge under the nose throws the nose up.
        float end = along >= 0.0f ? 1.0f : -1.0f;
        _kickPitch.Kick(-MineThrow * end * Mathf.Clamp(Mathf.Abs(along) / Mathf.Max(_foot.HalfLen, 1.0f), 0.4f, 1.0f));
        _heave.Kick(0.15f);
        _fate = Fate.Knocked;
        _sinceFate = 0.0f;
        _speed = 0.0f;
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

    /// <summary>Backspace: the mines back on their cells, live.</summary>
    private void MinesReset()
    {
        foreach (Laid mine in _mines)
        {
            mine.Spent = false;
            mine.Node.Visible = true;
            _field?.SetCoverState(mine.Cell, CoverState.Intact);
        }
    }
}
