using System;
using System.Linq;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The board drawn as Run plays it (docs/editor3d-plan.md, «Переход редактора
/// на рельеф», Р3 and Р4): the map in its sea (<see cref="BoardMap.RingOf"/>)
/// and one smooth ground over it (<see cref="TankSpriteTest.Relief"/>).
///
/// <b>Two fields, and only one of them is the map.</b> <c>_field</c> is the
/// map's own, in its own cells: every rule, mark, pick, tree and tank asks it,
/// so nothing that talks about a cell learns a shift. <c>_board</c> is the map
/// ringed by the sea, and only the stage draws it. The stage stands it at
/// <see cref="Stage3D.Origin"/> minus the ring's offset, so a flat point of
/// the map and the same point of the ring are one place in the world. The
/// ring moves the map by an even number of columns, so the shift is one
/// offset for every cell.
///
/// <b>What stands on the ground asks the relief through the map's field</b>
/// (<see cref="HexField.Surface"/>, moved by the offset): trees, grass, mines
/// and tanks stand on the drawn ground. The rules do not read it, as on the
/// bench.
/// </summary>
public sealed partial class MapEditor
{
    /// <summary>The map in its sea, the field the stage draws.</summary>
    private HexField _board = null!;

    /// <summary>How the map lies in its sea.</summary>
    private BoardMap.Ring? _ring;

    /// <summary>The smooth ground over the ring, or null where it could not be
    /// built.</summary>
    private Relief? _relief;

    /// <summary>What the relief was last built from: a stroke that moved no
    /// floor, level, water or cover (a tank carried, a home turned) does not
    /// build it again.</summary>
    private string _reliefAs = "";

    /// <summary>
    /// The ring laid from the draft the map's field was just given, and the
    /// relief over it when anything it is built from moved.
    /// </summary>
    private void Ringed(MapRules.Draft draft, bool[] water, string?[] kinds)
    {
        if (_field.Atlas is null)
            return;
        BoardMap.Ring ring = BoardMap.RingOf(_edit.Columns, _edit.Rows, _edit.OnBoard);
        _ring = ring;
        // no walls in the cover either, as on the bench: an unseen wall is an
        // obstacle nothing explains
        var cover = new Cover[_edit.Columns * _edit.Rows];
        for (int r = 0; r < _edit.Rows; r++)
        for (int q = 0; q < _edit.Columns; q++)
        {
            Cover over = _edit.GlyphAt(new Vector2I(q, r)).Over;
            cover[r * _edit.Columns + q] = over == Cover.Walls ? Cover.None : over;
        }
        int[] levels = ring.Spread(draft.Levels, _edit.Plinth - 1);
        bool[] wet = ring.Spread(water, true);
        Foundation[] ground = ring.Spread(draft.Ground, Foundation.Deep);
        string?[] painted = ring.Spread(kinds, null);
        Cover[] covered = ring.Spread(cover, Cover.None);
        string paint = _field.Paint ?? "";
        // the rocks' notes and the climate, on the ring's cells (format 3,
        // docs/rocks-plan.md С5): a note is a new relief, as a level is
        var rocking = _edit.Rocking().ToDictionary(kv => kv.Key + ring.Shift, kv => kv.Value);
        RockStyles? rocks = RockStyles.Of(rocking, _edit.Climate);
        // the soil's looks on the ring's cells, the sea wearing none
        char[]? looks = _edit.Looks() is { } own ? ring.Spread(own, TankSpriteTest.Soil.As) : null;
        string want = $"{ring.Columns}x{ring.Rows}:{paint}:"
                      + string.Join(",", levels) + "|" + string.Concat(wet.Select(w => w ? '1' : '0'))
                      + "|" + string.Concat(ground.Select(g => (char)('a' + (int)g)))
                      + "|" + string.Join(",", painted.Select(k => k ?? "")) + "|"
                      + string.Concat(covered.Select(c => (char)('a' + (int)c)))
                      + "|" + (looks is null ? "" : new string(looks))
                      + "|" + _edit.Climate + "|"
                      + string.Join(";", rocking.OrderBy(kv => kv.Key.Y).ThenBy(kv => kv.Key.X)
                                                .Select(kv => $"{kv.Key}{kv.Value}"));
        if (want == _reliefAs)
            return;
        _reliefAs = want;

        try
        {
            // The bench's order (Tank3DBench.BuildBoard), the levels first so
            // the water's guard asks the new board, never the last one's size.
            _board.Surface = null;
            _board.Columns = ring.Columns;
            _board.Rows = ring.Rows;
            _board.Plot = ring.Plot;
            _board.Sea = ring.Sea;
            _board.Paint = _field.Paint;
            _board.Slopes = true;
            _board.SetRelief(levels);
            _board.SetKinds(painted);
            _board.SetGround(ground);
            _board.SetCover(covered);
            _board.SetWater(wet);
            _board.SetSoil(looks);
        }
        catch (InvalidOperationException e)
        {
            // the water's guard, which the map's own rules have named already
            GD.Print($"editor: the ring refused: {e.Message}");
            return;
        }

        _relief = Relief.Build(_board, rocks: rocks);
        Vector2 offset = _board.FlatAnchor(ring.Shift) - _field.FlatAnchor(Vector2I.Zero);
        Relief? relief = _relief;
        _board.Surface = relief is null ? null : relief.HeightAt;
        _field.Surface = relief is null ? null : flat => relief.HeightAt(flat + offset);
        if (_stage is not null)
        {
            _stage.RockIce = _edit.Climate.Ice;
            _stage.SoilWind = _edit.Climate.Wind;
            _stage.Origin = -offset;
            _stage.Relief = relief;
            // the ring and its frame from the middle, along the camera's
            // ground, with the bench's spare for a pan (Tank3DBench.DepthSpare)
            float deep = (ring.Rows + 1) * _field.Atlas.HexRect.Size.Y / Mathf.Max(_field.Squash, 0.0001f);
            _stage.Depth = 0.5f * deep + 600.0f;
        }
        GD.Print(relief is null
            ? "editor: no relief - the prisms instead"
            : $"editor: relief {relief.Nx}x{relief.Ny} points on {ring.Columns}x{ring.Rows} "
              + $"(shift {ring.Shift.X},{ring.Shift.Y}) in {relief.Seconds * 1000:0} ms");
    }
}
