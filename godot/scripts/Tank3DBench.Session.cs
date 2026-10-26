using System;
using System.Linq;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The board this bench plays and the way back to the editor that sent it.
///
/// <b>The editor's Run lands here</b> (<c>MapEditor.Play</c>): it saves, hands
/// the board's name over through <see cref="Session"/> and changes scene to
/// <c>Tank3D.tscn</c>. The handoff wins over <c>--map</c> for the harness's
/// reason - the flag came from the launch that started the session and is
/// stale by the time a name is handed over. <c>Edit  F5</c> hands it back.
/// A handed board always plays on the relief (<c>--relief</c> implied), with
/// no bench target (<c>--no-target</c> implied) and the side panel folded.
///
/// <b>A name nobody wrote is refused, not played as another board.</b>
/// <see cref="BoardMap.ByName"/> answers an unknown name with the harness's
/// bench board, which is the harness's choice; here it came up as a board the
/// author never drew, and the run said nothing. The bench prints the refusal
/// and quits non-zero, the harness's <c>REFUSED</c>.
///
/// <b>A board the editor handed over says which tank.</b> The first parking's
/// class, when it names one, picks the model - the editor's Home group is where
/// the author says it. Only then: <c>events</c> parks MTP first, and the bench
/// opening on MTR would move every capture recipe written against its LTR.
/// One tank still: the bench's own tank is its
/// fields, and a tank per parking is its own step (docs/editor3d-plan.md).
/// </summary>
public sealed partial class Tank3DBench
{
    /// <summary><c>--model</c> was given: the board's parking does not pick.</summary>
    private bool _modelNamed;

    /// <summary>The board came through <see cref="Session"/>.</summary>
    private bool _handed;

    /// <summary><c>--edit-at N</c>: Edit on that frame, the harness's flag -
    /// a capture run has nobody to press the button.</summary>
    private int _editAt = -1;

    /// <summary>Whether the editor is what sent us, so the way back is offered.
    /// Read once at start: <see cref="Edit"/> turns it off on the way out.</summary>
    private bool _editing;

    /// <summary>After the flags: the handoff over <c>--map</c>.</summary>
    private void TakeSession()
    {
        if (Session.Take() is { Length: > 0 } handed)
        {
            _mapName = handed;
            _handed = true;
            // a board from the editor always plays on the relief, --relief
            // or not (BOT-11, docs/relief.md)
            _reliefOn = true;
            // and with the tanks its homes say, not the bench's own target
            // three hexes out (Tank3DBench.Aim, StandTarget)
            _targetDefault = false;
        }
        _editing = Session.Editing;
    }

    /// <summary>The board <see cref="_mapName"/> names, or null with the
    /// refusal printed and the quit asked for.</summary>
    private BoardMap? MapToPlay()
    {
        // The events bench's board is compiled apart from the named ones -
        // ByName would answer "events" with the harness's bench board.
        if (string.Equals(_mapName, "events", StringComparison.OrdinalIgnoreCase))
            return BoardMap.Events;
        if (!BoardMap.Names.Contains(_mapName, StringComparer.OrdinalIgnoreCase))
            return Refuse($"no such board - known: events, {string.Join(", ", BoardMap.Names)}");
        try
        {
            return BoardMap.ByName(_mapName);
        }
        catch (Exception e)
        {
            return Refuse(e.Message);
        }
    }

    private BoardMap? Refuse(string why)
    {
        Log.Print($"tank3d: map {_mapName}: REFUSED {why}");
        GetTree().Quit(1);
        return null;
    }

    /// <summary>The model the board's first parking asks for, when the editor
    /// handed the board over, it names a class and <c>--model</c> did not name
    /// a tank.</summary>
    private void TankFromBoard(BoardMap map)
    {
        if (!(_handed || _parkedWanted) || _modelNamed || _startCell is not null || map.Parked.Count == 0
            || map.Parked[0].Class is not { Length: > 0 } wanted)
            return;
        int i = Array.FindIndex(Pairs, p => string.Equals(p.Class, wanted, StringComparison.OrdinalIgnoreCase));
        if (i < 0)
        {
            Log.Print($"tank3d: the board's first parking wants {wanted}, which no model plays - {_modelTag}");
            return;
        }
        _modelTag = Pairs[i].Model;
        Log.Print($"tank3d: the board's first parking wants {wanted} - {_modelTag}");
    }

    /// <summary>The way back, a button beside the hud - only when the editor
    /// sent us, the harness's rule: a bench that jumped into the editor on a
    /// keypress would lose a session to a typo.</summary>
    private void BuildBack(CanvasLayer layer)
    {
        if (!_editing || _noUi)
            return;
        var back = new Button
        {
            Text = "Edit   F5",
            // Under the hud's three lines, which run the width of the board.
            Position = new Vector2(12.0f, 96.0f),
        };
        back.AddThemeFontSizeOverride("font_size", 16);
        back.Pressed += Edit;
        layer.AddChild(back);
    }

    /// <summary>Hand the board back to the editor, the way it came.</summary>
    private void Edit()
    {
        if (!_editing)
            return;
        _editing = false;
        Session.Map = _mapName;
        Session.Editing = false;
        Log.Print($"tank3d: back to the editor on {_mapName}");
        GetTree().ChangeSceneToFile("res://Editor.tscn");
    }
}
