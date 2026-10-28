using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// Selection, the clipboard, moving a piece and stamps (stage 2 of
/// docs/editor-ux-plan.md).
///
/// <b>A tool beside the brushes, not a tab.</b> <c>R</c> takes up the select
/// tool on any tab: a left drag marks a box of cells - shift adds to what is
/// marked, alt takes away - and a drag that starts inside the marked cells
/// carries them. The marked cells are outlined on the board.
///
/// <b>What is lifted is <see cref="MapEdit.Piece"/>, all axes at once</b>:
/// letter, ramp, wall and the homes on it. ctrl+C keeps one, ctrl+X keeps it
/// and clears the cells, ctrl+V puts it in hand - a ghost under the pointer,
/// coloured as what it would lay - and a click lays it, as many times as
/// clicked, until a right click or Escape drops it. ctrl+shift+V leaves the
/// tanks behind. A move, a paste and a cut are one undo each.
///
/// <b>Down on the hex grid, not the offset one</b> - see
/// <see cref="MapEdit.Shift"/>: a piece moved an odd number of columns keeps
/// which cells touch.
///
/// <b>Stamps are pieces on disk</b>, in <c>maps/stamps/</c>, which no scene
/// reads (<see cref="MapFile.SaveStamp"/>); Edit lists them, and picking one
/// puts it in hand.
/// </summary>
public sealed partial class MapEditor
{
    private readonly HashSet<Vector2I> _selected = new();

    /// <summary>Where a box drag started, while one is going.</summary>
    private Vector2I? _boxFrom;

    private enum Boxing { Replace, Add, Remove }
    private Boxing _boxing;

    /// <summary>What ctrl+C kept. Survives opening another board: copying
    /// a piece of one map into another is what it is for.</summary>
    private MapEdit.Piece? _clip;

    /// <summary>The piece following the pointer - a paste, a stamp, or the
    /// selection being dragged (<see cref="_moving"/>).</summary>
    private MapEdit.Piece? _held;
    private bool _heldHomes = true;
    private bool _moving;
    private Vector2I _moveFrom;
    private string _heldName = "";

    private static readonly Color SelectInk = new(1.0f, 0.88f, 0.35f);

    /// <summary>Whether the pointer belongs to the selection rather than the
    /// brush: the brush's cells are not painted and its ghost is not drawn.</summary>
    private bool Selecting => _selectTool || _held is not null;

    // --- input ---------------------------------------------------------------

    /// <summary>The pointer and the buttons while selecting or holding a
    /// piece. Answers whether the event was the selection's.</summary>
    private bool SelectInput(InputEvent what)
    {
        if (!Selecting)
            return false;
        if (what is InputEventMouseMotion)
        {
            if (_boxFrom is null && _held is null)
                return false;
            _under = Pointed();
            _marks.QueueRedraw();
            Say();
            return true;
        }
        if (what is not InputEventMouseButton click
            || click.ButtonIndex is not (MouseButton.Left or MouseButton.Right))
            return false;
        _under = Pointed();

        if (click.ButtonIndex == MouseButton.Right)
        {
            if (!click.Pressed)
                return true;
            if (_held is not null)
                Drop(_moving ? "move called off" : "put down nothing");
            else
            {
                _selected.Clear();
                _said = "nothing marked";
            }
            Say();
            return true;
        }

        if (click.Pressed)
        {
            if (_held is not null && !_moving)
            {
                Lay();
                return true;
            }
            if (!_edit.Inside(_under))
                return true;
            if (!click.ShiftPressed && !click.AltPressed && _selected.Contains(_under))
            {
                // a drag from inside the marked cells carries them
                _held = _edit.Copy(_selected, _under);
                _heldHomes = true;
                _moving = true;
                _moveFrom = _under;
                return true;
            }
            _boxFrom = _under;
            _boxing = click.ShiftPressed ? Boxing.Add : click.AltPressed ? Boxing.Remove : Boxing.Replace;
            return true;
        }

        // released
        if (_moving)
        {
            Moved();
            return true;
        }
        if (_boxFrom is Vector2I from)
        {
            IReadOnlyList<Vector2I> box = _edit.Box(from, _edit.Inside(_under) ? _under : from);
            if (_boxing == Boxing.Replace)
                _selected.Clear();
            foreach (Vector2I cell in box)
                if (_edit.OnBoard(cell))
                {
                    if (_boxing == Boxing.Remove)
                        _selected.Remove(cell);
                    else
                        _selected.Add(cell);
                }
            _boxFrom = null;
            _said = $"{_selected.Count} cell(s) marked";
            Say();
        }
        return true;
    }

    /// <summary>The selection keys, ahead of the brushes' switch. Answers
    /// whether the key was one of them.</summary>
    private bool SelectKey(InputEventKey key)
    {
        switch (key.Keycode)
        {
            case Key.R when !key.CtrlPressed:
                SelectTool(!_selectTool);
                break;
            case Key.C when key.CtrlPressed: CopyMarked(); break;
            case Key.X when key.CtrlPressed: CutMarked(); break;
            case Key.V when key.CtrlPressed: Hold(_clip, "clipboard", homes: !key.ShiftPressed); break;
            case Key.A when key.CtrlPressed: MarkAll(); break;
            case Key.D when key.CtrlPressed:
                _selected.Clear();
                _said = "nothing marked";
                break;
            case Key.Delete: ClearMarked(); break;
            default: return false;
        }
        _field.QueueRedraw();
        _marks.QueueRedraw();
        Say();
        return true;
    }

    private void SelectTool(bool on)
    {
        _selectTool = on;
        _boxFrom = null;
        _said = on ? "select: drag a box, shift adds, alt takes away, drag the marked cells to move them"
                   : "back to the brush";
    }

    // --- what is done with the marked cells ------------------------------------

    /// <summary>The marked cell nearest the middle of them: what a piece is
    /// held by, so it comes up centred under the pointer.</summary>
    private Vector2I Middle(IReadOnlyCollection<Vector2I> cells)
    {
        Vector2 sum = Vector2.Zero;
        foreach (Vector2I c in cells)
            sum += new Vector2(c.X, c.Y + 0.5f * (c.X & 1));
        Vector2 mid = sum / Math.Max(1, cells.Count);
        return cells.OrderBy(c => new Vector2(c.X, c.Y + 0.5f * (c.X & 1)).DistanceSquaredTo(mid))
                    .ThenBy(c => c.Y).ThenBy(c => c.X).First();
    }

    private bool Marked()
    {
        if (_selected.Count > 0)
            return true;
        _said = "nothing marked - R, then drag a box (ctrl+A marks the board)";
        return false;
    }

    private void CopyMarked()
    {
        if (!Marked())
            return;
        _clip = _edit.Copy(_selected, Middle(_selected));
        _said = $"copied {_clip.Cells.Count} cell(s)"
                + (_clip.Homes.Count > 0 ? $" and {_clip.Homes.Count} tank(s)" : "");
    }

    private void CutMarked()
    {
        if (!Marked())
            return;
        CopyMarked();
        _edit.Begin();
        _edit.Clear(_selected);
        _edit.End();
        _dirty = true;
        _said = _said.Replace("copied", "cut");
        Settle();
    }

    private void ClearMarked()
    {
        if (!Marked())
            return;
        _edit.Begin();
        int n = _edit.Clear(_selected);
        _dirty |= _edit.End();
        _said = $"{n} cell(s) cleared to plain ground";
        Settle();
    }

    private void MarkAll()
    {
        _selected.Clear();
        for (int r = 0; r < _edit.Rows; r++)
        for (int q = 0; q < _edit.Columns; q++)
            if (_edit.OnBoard(new Vector2I(q, r)))
                _selected.Add(new Vector2I(q, r));
        _said = $"{_selected.Count} cell(s) marked";
    }

    /// <summary>A piece put in hand: it follows the pointer until a click
    /// lays it.</summary>
    private void Hold(MapEdit.Piece? piece, string name, bool homes = true)
    {
        if (piece is null)
        {
            _said = "nothing copied yet - mark cells and ctrl+C";
            return;
        }
        _held = piece;
        _heldHomes = homes;
        _heldName = name;
        _moving = false;
        _boxFrom = null;
        _said = $"{name} in hand: click lays it, right click or Esc drops it"
                + (!homes && piece.Homes.Count > 0 ? " - without its tanks" : "");
    }

    private void Drop(string said)
    {
        _held = null;
        _moving = false;
        _said = said;
        _marks.QueueRedraw();
    }

    /// <summary>The piece in hand laid under the pointer; it stays in hand.
    /// What it laid becomes the marked cells, ready to be moved.</summary>
    private void Lay()
    {
        if (_held is null || !_edit.Inside(_under))
            return;
        _edit.Begin();
        int laid = _edit.Paste(_held, _under, _heldHomes, out int off);
        _dirty |= _edit.End();
        Mark(_held, _under);
        _said = $"laid {laid} cell(s) of {_heldName}" + (off > 0 ? $", {off} fell off the grid" : "");
        Settle();
    }

    /// <summary>The selection dragged: cleared where it was and laid where it
    /// was let go, as one undo.</summary>
    private void Moved()
    {
        MapEdit.Piece piece = _held!;
        Vector2I to = _under;
        _held = null;
        _moving = false;
        if (!_edit.Inside(to) || to == _moveFrom)
        {
            _said = to == _moveFrom ? $"{_selected.Count} cell(s) marked" : "moved nowhere - let go off the grid";
            Say();
            return;
        }
        _edit.Begin();
        _edit.Clear(_selected);
        _edit.Paste(piece, to, homes: true, out int off);
        _dirty |= _edit.End();
        Mark(piece, to);
        _said = $"moved {piece.Cells.Count} cell(s)" + (off > 0 ? $", {off} fell off the grid" : "");
        Settle();
    }

    /// <summary>The cells a piece covers laid at <paramref name="at"/>, marked.</summary>
    private void Mark(MapEdit.Piece piece, Vector2I at)
    {
        _selected.Clear();
        foreach (MapEdit.Bit bit in piece.Cells)
        {
            Vector2I to = MapEdit.Shift(bit.Cell, piece.Anchor, at);
            if (_edit.OnBoard(to))
                _selected.Add(to);
        }
    }

    /// <summary>A new board in front of the author: the marks belonged to the
    /// old one. What is in hand and the clipboard stay.</summary>
    private void Unmark()
    {
        _selected.Clear();
        _boxFrom = null;
        if (_moving)
            Drop("");
    }

    // --- stamps ----------------------------------------------------------------

    private ConfirmationDialog _stampDialog = null!;
    private LineEdit _stampName = null!;
    private PopupMenu _stamps = null!;

    /// <summary>Edit's selection items, its stamp submenu, and the dialog that
    /// names a stamp.</summary>
    private void SelectMenu(PopupMenu edit)
    {
        edit.AddSeparator();
        edit.AddItem("Select tool       R", (int)More.Select);
        edit.AddItem("Mark all     ctrl+A", (int)More.MarkAll);
        edit.AddItem("Copy         ctrl+C", (int)More.Copy);
        edit.AddItem("Cut          ctrl+X", (int)More.Cut);
        edit.AddItem("Paste        ctrl+V", (int)More.Paste);
        edit.AddItem("Clear marked   Del", (int)More.Clear);
        edit.AddSeparator();
        edit.AddItem("Save marked as stamp...", (int)More.SaveStamp);
        _stamps = new PopupMenu { Name = "Stamps" };
        _stamps.AddThemeFontSizeOverride("font_size", 16);
        _stamps.IdPressed += id =>
        {
            string name = _stamps.GetItemText(_stamps.GetItemIndex((int)id));
            try
            {
                Hold(MapFile.LoadStamp(name), "stamp " + name);
            }
            catch (Exception e)
            {
                Tell($"Stamp {name} refused.\n\n{e.Message}");
            }
            Say();
        };
        edit.AddChild(_stamps);
        edit.AddSubmenuNodeItem("Stamps", _stamps);
        // Listed as the menu opens: a stamp saved a moment ago is on it.
        edit.AboutToPopup += () =>
        {
            _stamps.Clear();
            IReadOnlyList<string> names = MapFile.StampNames();
            for (int i = 0; i < names.Count; i++)
                _stamps.AddItem(names[i], i);
            if (names.Count == 0)
            {
                _stamps.AddItem("(none yet - mark cells, then Save marked as stamp)", 0);
                _stamps.SetItemDisabled(0, true);
            }
        };

        _stampDialog = new ConfirmationDialog { Title = "Save as stamp" };
        var named = new VBoxContainer();
        _stampName = new LineEdit();
        named.AddChild(Row("name", _stampName));
        var where = new Label { Text = "kept in maps/stamps, which no scene reads" };
        where.AddThemeFontSizeOverride("font_size", 12);
        named.AddChild(where);
        _stampDialog.AddChild(named);
        _stampDialog.RegisterTextEnter(_stampName);
        _stampDialog.Confirmed += () =>
        {
            string name = Clean(_stampName.Text);
            try
            {
                MapFile.SaveStamp(name, _edit.Copy(_selected, Middle(_selected)));
                _said = $"stamp {name} saved - Edit > Stamps";
            }
            catch (Exception e)
            {
                _said = "stamp NOT saved: " + e.Message;
                Tell("Stamp not saved.\n\n" + e.Message);
            }
            Say();
        };
        _layer.AddChild(_stampDialog);
    }

    /// <summary>Edit's selection items, through the keys' own calls.</summary>
    private bool SelectAct(More what)
    {
        switch (what)
        {
            case More.Select: SelectTool(!_selectTool); break;
            case More.MarkAll: MarkAll(); break;
            case More.Copy: CopyMarked(); break;
            case More.Cut: CutMarked(); break;
            case More.Paste: Hold(_clip, "clipboard"); break;
            case More.Clear: ClearMarked(); break;
            case More.SaveStamp:
                if (!Marked())
                    break;
                _stampName.Text = _edit.Name + "_piece";
                _stampDialog.PopupCentered();
                _stampName.SelectAll();
                _stampName.GrabFocus();
                break;
            default: return false;
        }
        return true;
    }

    // --- the probe ---------------------------------------------------------------

    /// <summary>
    /// <c>--map abbey --menu clip</c>: stage 2's "done when", in one run. The
    /// river off ABBEY's east side (columns 15-18, an odd first column) copied
    /// into a blank board, laid an odd number of columns over; then every pair
    /// of cells that touched must still touch, the rules must find nothing
    /// fatal, one undo must leave the blank board, a move must be one undo, and
    /// a stamp must come back off the disk as the same piece. With
    /// <c>--capture</c> the piece is left in hand under <c>--hover</c>.
    /// </summary>
    private void ClipProbe()
    {
        // cliptanks: the tanks ride with the cells - the whole board copied,
        // cleared, and laid back where it was must be the board it was, its
        // homes in the same sides and classes
        if (_open == "cliptanks" && _frame == 10)
        {
            string[] was = _edit.Ground();
            string homes = string.Join(" ", Lineup.Of(_edit.Homes).Select(u => $"{u.Cell.X},{u.Cell.Y}{u.Side[0]}{u.Model}").OrderBy(s => s));
            MarkAll();
            CopyMarked();
            MapEdit.Piece clip = _clip!;
            ClearMarked();
            int left = _edit.Homes.Count;
            _under = clip.Anchor;
            Hold(_clip, "clipboard");
            Lay();
            _held = null;
            string back = string.Join(" ", Lineup.Of(_edit.Homes).Select(u => $"{u.Cell.X},{u.Cell.Y}{u.Side[0]}{u.Model}").OrderBy(s => s));
            GD.Print($"cliptanks: copied {clip.Homes.Count} tank(s), {left} left after the clear; "
                     + $"laid back the ground is the same {_edit.Ground().SequenceEqual(was)}, the tanks the same {back == homes}"
                     + (back == homes ? "" : $"\n    was  {homes}\n    now  {back}"));
        }
        if (_open != "clip")
            return;
        if (_frame == 10)
        {
            _selectTool = true;
            foreach (Vector2I cell in _edit.Box(new Vector2I(15, 5), new Vector2I(18, 12)))
                if (_edit.OnBoard(cell))
                    _selected.Add(cell);
            CopyMarked();
            MapEdit.Piece clip = _clip!;
            var at = new Vector2I(4, 4);
            int touching = 0, parted = 0;
            foreach (MapEdit.Bit a in clip.Cells)
            foreach (MapEdit.Bit b in clip.Cells)
                if (HexField.HeadingTo(a.Cell, b.Cell) >= 0)
                {
                    touching++;
                    if (HexField.HeadingTo(MapEdit.Shift(a.Cell, clip.Anchor, at),
                                           MapEdit.Shift(b.Cell, clip.Anchor, at)) < 0)
                        parted++;
                }
            GD.Print($"clip: copied {clip.Cells.Count} cell(s) held at ({clip.Anchor.X},{clip.Anchor.Y}), "
                     + $"{clip.Homes.Count} tank(s); {touching} touching pair(s), {parted} parted laid at ({at.X},{at.Y})");

            _edit = MapEdit.Blank("clipped", MapEdit.DefaultColumns, MapEdit.DefaultRows);
            Opened("probe");
            string[] blank = _edit.Ground();
            int before = _faults.Count(f => f.Fatal);
            _under = at;
            Hold(_clip, "clipboard");
            Lay();
            _held = null;
            GD.Print($"clip: {_said}; undo {_edit.Depth}; fatal {before} -> {_faults.Count(f => f.Fatal)}, "
                     + $"warnings: {string.Join(", ", _faults.Where(f => !f.Fatal).Select(f => f.Rule).Distinct())}");
            GD.Print("clip: board after the paste\n    " + string.Join("\n    ", _edit.Ground()));
            string[] laid = _edit.Ground();

            _edit.Undo();
            Settle();
            GD.Print($"clip: one undo, the board is blank again: {_edit.Ground().SequenceEqual(blank)}");
            _edit.Redo();
            Settle();

            // a drag of the marked cells a column right: one undo takes it back
            int depth = _edit.Depth;
            _held = _edit.Copy(_selected, at);
            _moving = true;
            _moveFrom = at;
            _under = at + new Vector2I(1, 0);
            Moved();
            bool moved = !_edit.Ground().SequenceEqual(laid);
            _edit.Undo();
            Settle();
            GD.Print($"clip: move changed the board {moved}, cost {_edit.Depth + 1 - depth} undo, "
                     + $"undone back to the paste: {_edit.Ground().SequenceEqual(laid)}");

            // a stamp off the disk is the same piece
            string path = System.IO.Path.Combine(MapFile.StampDir, "zz_probe" + MapFile.Extension);
            string same;
            try
            {
                MapFile.SaveStamp("zz_probe", clip);
                MapEdit.Piece back = MapFile.LoadStamp("zz_probe");
                string Said(MapEdit.Piece p) => string.Join(";", p.Cells
                    .Select(b => (MapEdit.Shift(b.Cell, p.Anchor, at), b.Letter, b.Ramp))
                    .OrderBy(x => x.Item1.Y).ThenBy(x => x.Item1.X)
                    .Select(x => $"{x.Item1.X},{x.Item1.Y}{x.Letter}{(x.Ramp ? "r" : "")}"));
                same = Said(back) == Said(clip) ? "the same piece" : "ANOTHER piece";
            }
            catch (Exception e)
            {
                same = "refused: " + e.Message;
            }
            if (System.IO.File.Exists(path))
                System.IO.File.Delete(path);
            GD.Print($"clip: stamp round trip - {same}");
            Say();
        }
        // in hand for the capture, under --hover
        if (_frame == 12 && _hovered is Vector2I hover)
        {
            _under = hover;
            Hold(_clip, "clipboard");
            Say();
        }
    }

    // --- drawn -----------------------------------------------------------------

    /// <summary>The marked cells' outline, the box being dragged, and the piece
    /// in hand - over the board, under the brush's ghost.</summary>
    private void DrawSelection(Overlay on)
    {
        if (_boxFrom is Vector2I from && _edit.Inside(_under))
        {
            var box = new HashSet<Vector2I>(_edit.Box(from, _under));
            Color ink = _boxing == Boxing.Remove ? new Color(1.0f, 0.6f, 0.5f) : SelectInk;
            Outline(on, box, new Color(ink, 0.7f), 2.0f);
        }
        if (_selected.Count > 0 && !_moving)
            Outline(on, _selected, SelectInk, 3.0f);
        if (_held is not null && _edit.Inside(_under))
            DrawPiece(on, _held, _under);
    }

    /// <summary>The edges of a set of cells that do not face another of them.</summary>
    private void Outline(Overlay on, IReadOnlySet<Vector2I> cells, Color ink, float width)
    {
        foreach (Vector2I cell in cells)
        {
            if (!_field.InBounds(cell))
                continue;
            foreach (int heading in HexField.EdgeHeadings)
            {
                if (cells.Contains(HexField.Step(cell, heading)))
                    continue;
                (Vector2 a, Vector2 b) = EdgeOnScreen(cell, heading);
                on.DrawLine(a, b, ink, width);
            }
        }
    }

    /// <summary>A piece where it would land, each cell tinted as what it would
    /// become and the tanks on it as rings in their side's colour.</summary>
    private void DrawPiece(Overlay on, MapEdit.Piece piece, Vector2I at)
    {
        var corners = new Vector2[6];
        var landed = new HashSet<Vector2I>();
        foreach (MapEdit.Bit bit in piece.Cells)
        {
            Vector2I to = MapEdit.Shift(bit.Cell, piece.Anchor, at);
            if (!_edit.Inside(to))
                continue;
            landed.Add(to);
            foreach (int heading in HexField.EdgeHeadings)
                corners[HexField.EdgeIndex(heading)] = EdgeOnScreen(to, heading).A;
            on.DrawColoredPolygon(corners, new Color(Tint(bit.Letter), 0.55f));
        }
        Outline(on, landed, new Color(BrushInk, 0.9f), 2.0f);
        if (_heldHomes)
            foreach (Parking home in piece.Homes)
            {
                Vector2I to = MapEdit.Shift(home.Cell, piece.Anchor, at);
                if (_edit.Inside(to))
                    on.DrawArc(TopOnScreen(to), 22.0f * _camera.Zoom.X, 0.0f, Mathf.Tau, 20,
                               home.Side == Parking.Brown ? BrownInk : GreenInk, 2.5f);
            }
        string word = _moving ? "move" : _heldName;
        on.DrawString(ThemeDB.FallbackFont, TopOnScreen(at) + new Vector2(18.0f, -18.0f), word,
                      HorizontalAlignment.Left, -1, 15, BrushInk);
    }

    /// <summary>The colour a letter is shown in on a piece's ghost.</summary>
    private static Color Tint(char letter)
    {
        MapFile.Glyph g = MapFile.Of(letter) ?? MapFile.Of(BoardMap.Plain)!.Value;
        if (g.Water)
            return g.Floor == Foundation.Deep ? new Color(0.20f, 0.35f, 0.85f) : new Color(0.40f, 0.60f, 0.95f);
        if (g.Cliff)
            return new Color(0.55f, 0.55f, 0.58f);
        if (g.Wall)
            return BrickInk;
        if (g.Wood)
            return new Color(0.25f, 0.60f, 0.28f);
        return g.Over switch
        {
            Cover.Trench => new Color(0.55f, 0.45f, 0.35f),
            Cover.Minefield => new Color(0.85f, 0.30f, 0.30f),
            _ => g.Floor switch
            {
                Foundation.Sand => new Color(0.92f, 0.85f, 0.55f),
                Foundation.Mud => new Color(0.45f, 0.35f, 0.25f),
                _ => g.Level switch
                {
                    > 0 => new Color(0.80f, 0.70f, 0.45f),
                    < 0 => new Color(0.50f, 0.42f, 0.30f),
                    _ => new Color(0.75f, 0.80f, 0.70f),
                },
            },
        };
    }
}
