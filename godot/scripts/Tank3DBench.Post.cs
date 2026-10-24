using System.Collections.Generic;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// Fire after the move (2026-10-24). With the pointer on a target the gun
/// cannot reach from where the tank stands, but can from a cell of the move,
/// the move and the shot are shown as one order: the way to that cell, the
/// ghost on it turned as it will fire, the line of fire from the ghost. The
/// right button orders both - go, then fire on arrival.
///
/// <b>It is the fixed cell of HUD stage 3, fixed by the bench</b> rather than
/// by a click - a soft lock (<see cref="_postLock"/>). The ribbon, the ghost,
/// the line judged from the fixed cell and the right button's "go, then fire"
/// are the fixed cell's own (<c>Tank3DBench.Ghost</c>, <c>Tank3DBench.Aim</c>,
/// <see cref="ShotOrder"/>), so the picture and the order cannot part. The
/// pointer off the target lets it go; a cell the player fixed is never taken
/// over; the rose is not drawn - the post's side is the one it fires from.
///
/// <b>Which cell</b> (<see cref="FindPost"/>): the best outcome there -
/// pierce, then ricochet, then a round that holds; then a way with no risk on
/// it; then the fewest steps, the turn on arrival with them; then the
/// straightest way. A casemate fires where its hull points, so it turns on to
/// the target's axis on arrival out of the same budget: a cell without the
/// steps for that turn is no post. LT, which may fire first, is shown a post
/// only when it cannot fire from where it stands.
///
/// No post anywhere in reach: the line stays grey and the HUD says so
/// («ни с одной клетки хода»).
/// </summary>
public sealed partial class Tank3DBench
{
    /// <summary>The fixed cell is the bench's firing post, not the player's.</summary>
    private bool _postLock;

    private sealed record Post(Vector2I Cell, int Way, int Face, int Steps, Judged Shot);

    private Post? _post;
    /// <summary>What the post was found for - the reach, the target where it
    /// stands and how it faces, the tank: found again when one of them moves.</summary>
    private string _postKey = "";
    /// <summary>The target is out of the gun's reach from every cell of the move.</summary>
    private bool _postNone;

    /// <summary>Every frame from <see cref="AimTick"/>, before the shot is
    /// judged: the post fixed while the pointer is on a target that wants
    /// one, let go when it does not.</summary>
    private void PostTick()
    {
        _postNone = false;
        bool asks = _rules && _chosen && _fate == Fate.Alive && _overTarget && !RamRunning && !MatchShotSpent
                    && _other is { } o && o.Fate != Fate.Destroyed
                    && CourseShown && _reach is not null && (_lockCell is null || _postLock);
        if (!asks)
        {
            ReleasePost();
            return;
        }
        Other target = _other!;
        Vector2I cell = OtherCell(target);
        // From where it stands: no move needed (and LT keeps its own way of
        // showing the shot after the move).
        if (ShotWhy(CellHere, cell, out _, out _, out _).Length == 0)
        {
            ReleasePost();
            return;
        }
        string key = $"{_reachVersion}|{cell}|{target.Heading:F0}|{target.Fate}|{_modelTag}";
        if (key != _postKey)
        {
            _postKey = key;
            _post = FindPost(target, cell);
            GD.Print(_post is { } found
                ? $"tank3d: post for {target.Tag} {cell}: {found.Cell} facing {found.Face}, {found.Steps} of {_reach!.Budget} steps, {found.Shot.Says}"
                : $"tank3d: post for {target.Tag} {cell}: none in reach");
        }
        if (_post is not { } p)
        {
            _postNone = true;
            ReleasePost();
            return;
        }
        if (_postLock && _lockCell == p.Cell)
            return;
        _lockCell = p.Cell;
        _hoverCell = p.Cell;
        _ways = _reach!.RoutesTo(p.Cell);
        _wayIx = p.Way;
        _lockFace = p.Face;
        _postLock = true;
    }

    /// <summary>The post let go: the pointer's own cell is the way's again.</summary>
    private void ReleasePost()
    {
        if (!_postLock)
            return;
        Unlock();
        _hoverCell = null;
        _ways.Clear();
        // the course reads the pointer again even if it has not moved
        _hoverMouse = new Vector2(-1.0f, -1.0f);
    }

    /// <summary>The best cell of the move to fire on the target from, and the
    /// way and the side to get there; null when there is none.</summary>
    private Post? FindPost(Other o, Vector2I target)
    {
        Steps.Reach reach = _reach!;
        Post? best = null;
        (int, int, int, int) bestRank = (int.MaxValue, 0, 0, 0);
        foreach ((Vector2I cell, int _) in reach.Cells)
        {
            if (cell == reach.Start.Cell || ShotWhy(cell, target, out int axis, out _, out _).Length > 0)
                continue;
            Judged shot = Judge(cell, o);
            if (!shot.Fires)
                continue;
            int outcome = shot.Pierces ? 0 : shot.Glances ? 1 : 2;
            List<Steps.Route> ways = reach.RoutesTo(cell);
            for (int w = 0; w < ways.Count; w++)
            {
                Steps.Route way = ways[w];
                int face = way.End.Heading, turn = 0;
                if (!_model.Turreted)
                {
                    // the hull on to the axis, out of the same steps
                    if (reach.TurnTo(way, axis) is not int t)
                        continue;
                    face = axis;
                    turn = t;
                }
                var rank = (outcome, way.Risk == Steps.Risk.None ? 0 : 1, way.Spent + turn, w);
                if (best is not null && rank.CompareTo(bestRank) >= 0)
                    continue;
                bestRank = rank;
                best = new Post(cell, w, face, way.Spent + turn, shot);
            }
        }
        return best;
    }
}
