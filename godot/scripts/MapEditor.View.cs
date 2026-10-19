using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The editor's view in 3D (docs/editor3d-plan.md, stage 2b): the board turned
/// about the middle of the window, the cell under the pointer found by a ray,
/// and the marks drawn where the camera sees them.
///
/// <b>Turned in sixths, and only the editor.</b> <c>,</c> and <c>.</c> turn
/// the board a hex side either way (<see cref="Stage3D.Yaw"/>), eased over a
/// fraction of a second; Home turns it back. The game's camera never turns,
/// so a board is judged at the game's angle first - turning is for the far
/// side of a hill or a wood, which the game's angle hides.
///
/// <b>Picked by a ray, not by the canvas.</b> The 2D field's inverse
/// (<see cref="HexField.CellAt"/>) knows one angle. The ray from the stage's
/// camera meets the board's tops from the highest down
/// (<see cref="RayCell"/>): the first top it meets over a cell of that height
/// is the cell - no colliders, the way ratel's editor finds its ground.
///
/// <b>The marks are drawn on the screen, off the world.</b> Each point the
/// overlay used to put on the 2D field - a cell's top, an edge's ends - is the
/// stage's world point projected through its camera (<see cref="Screen"/>),
/// so the marks follow any turn and stay over the trees, where they were.
/// </summary>
public sealed partial class MapEditor
{
    /// <summary>Where the turn is going and where it is, radians.</summary>
    private float _yawWant, _yaw;

    /// <summary>A turn's speed, radians a second.</summary>
    private const float YawRate = 6.0f;

    /// <summary>Ease the turn and hand it to the stage; the marks are redrawn
    /// every frame, because the camera moves under them.</summary>
    private void Turn(float dt)
    {
        _yaw = Mathf.MoveToward(_yaw, _yawWant, YawRate * dt);
        if (_stage is not null)
            _stage.Yaw = _yaw;
        _marks.QueueRedraw();
    }

    /// <summary>A hex side either way.</summary>
    private void TurnBy(int sides) => _yawWant += sides * Mathf.Pi / 3.0f;

    /// <summary>Back to the game's angle, at once - Home's half.</summary>
    private void Unturn() => _yaw = _yawWant = 0.0f;

    /// <summary>A middle-button drag in screen px, as the camera's move on the
    /// board: across the screen and down it, turned with the board.</summary>
    private Vector2 Panned(Vector2 relative)
    {
        float squash = Mathf.Max(_field.Squash, 0.0001f);
        Vector3 right = Vector3.Right.Rotated(Vector3.Up, _yaw);
        Vector3 down = Vector3.Back.Rotated(Vector3.Up, _yaw) / squash;
        Vector3 moved = (right * relative.X + down * relative.Y) / _camera.Zoom.X;
        return new Vector2(moved.X, moved.Z * squash);
    }

    /// <summary>A point of the board - flat px, the field's own frame before
    /// any lift, and <paramref name="lift"/> px up - where the stage's camera
    /// puts it on the screen.
    ///
    /// <b>Off the flat point, not the drawn one.</b> The 2D field's drawn
    /// points carry their lift in the row, and the editor's <c>Top</c> and the
    /// field's <c>EdgeEnds</c> carry it twice (the anchor is lifted, and the
    /// lift is taken off again) - measured, a level's height over the top it
    /// marks. On the old canvas that was a mark floating a level up; turned,
    /// the same float went sideways, off its cell.</summary>
    private Vector2 Screen(Vector2 flat, float lift)
    {
        if (_stage is null)
            return flat - new Vector2(0.0f, lift);
        return _stage.Camera.UnprojectPosition(_stage.World(flat, lift));
    }

    /// <summary>A cell's middle on the field, flat.</summary>
    private Vector2 Flat(Vector2I cell) => _field.FlatAnchor(cell) + _field.CentreOffset;

    /// <summary>A cell's top middle on the screen.</summary>
    private Vector2 TopOnScreen(Vector2I cell) => Screen(Flat(cell), _field.TopAt(cell));

    /// <summary>An edge of a cell's top on the screen, <paramref name="rise"/>
    /// px over it - the ghost's prism lifts it. The ends are the field's own
    /// (<see cref="HexField.EdgeEnds"/>) with their lifts given back.</summary>
    private (Vector2 A, Vector2 B) EdgeOnScreen(Vector2I cell, int heading, float rise = 0.0f)
    {
        (Vector2 a, Vector2 b) = _field.EdgeEnds(cell, heading);
        float[] top = _field.TopCorners(cell);
        int i = HexField.EdgeIndex(heading), j = (i + 1) % 6;
        float drawn = _field.TopAt(cell);
        return (Screen(a + new Vector2(0.0f, drawn + top[i]), top[i] + rise),
                Screen(b + new Vector2(0.0f, drawn + top[j]), top[j] + rise));
    }

    /// <summary>
    /// The cell under a screen point: the ray from the stage's camera against
    /// every height a top stands at, the highest first - a cell's top at that
    /// height, under where the ray crosses it, is the answer. The 2D inverse
    /// when there is no stage.
    /// </summary>
    private Vector2I RayCell(Vector2 screen)
    {
        if (_stage is null || _field.Atlas is null)
            return _field.CellAt(_field.ToLocal(GetGlobalMousePosition()));
        Camera3D eye = _stage.Camera;
        Vector3 from = eye.ProjectRayOrigin(screen);
        Vector3 way = eye.ProjectRayNormal(screen);
        if (Mathf.Abs(way.Y) < 1e-5f)
            return new Vector2I(-1, -1);
        var tops = new SortedSet<float>(Comparer<float>.Create((x, y) => y.CompareTo(x)));
        for (int q = 0; q < _field.Columns; q++)
        for (int r = 0; r < _field.Rows; r++)
            if (_field.InBounds(new Vector2I(q, r)))
                tops.Add(_field.TopAt(new Vector2I(q, r)));
        float rise = Mathf.Max(_field.RiseFactor, 0.0001f);
        float squash = Mathf.Max(_field.Squash, 0.0001f);
        foreach (float top in tops)
        {
            float t = (top / rise - from.Y) / way.Y;
            // No t < 0 cut: the camera is orthographic, its ray's origin lies on
            // the near plane, and the top of a rock stands nearer than that - a
            // line of sight, not a ray from an eye, and measured: ABBEY's rocks
            // were picked as the cell behind them.
            Vector3 at = from + way * t;
            Vector2 flat = new Vector2(at.X, at.Z * squash);
            Vector2I cell = _field.FlatCellAt(flat);
            if (_field.InBounds(cell) && Mathf.Abs(_field.TopAt(cell) - top) < 0.5f)
                return cell;
        }
        // Past the plot: the ground's datum, so a brush can still put a cell on
        // the board from off it.
        float t0 = -from.Y / way.Y;
        Vector3 ground = from + way * t0;
        return _field.FlatCellAt(new Vector2(ground.X, ground.Z * squash));
    }
}
