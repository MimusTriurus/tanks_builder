using System.Collections.Generic;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// Where the ground has been blown open: the marks a burst leaves behind it.
///
/// <b>The unit is a point, not a cell</b> - <see cref="TrackMarks"/>'s argument,
/// and here it is even harder to argue with. A shell lands where it lands: a
/// round going into open field stops a third of the way across a hexagon, two
/// rounds can land on one cell, and a crater is a third of a cell wide. Marked
/// per cell it would be a black hexagon, and the note on <c>Stage3D.AshGrain</c>
/// is already about exactly that failure - a mask made from a per-cell number
/// draws the grid.
///
/// <b>State of the board, drawn by the stage</b>, the same split <c>Wildfire</c>
/// and <see cref="TrackMarks"/> live under: what has been dug is a fact, and
/// which map it is rasterised into is a question about the picture. It carries no
/// nodes and draws nothing, so it works the same whichever board is up - it is
/// only the drawing that the flat board cannot do.
///
/// <b>Rasterised into the ash map rather than owning one.</b> Burnt ground and
/// blown ground are one statement to the ground shader - <c>ash</c>, read in
/// world XZ, with a soft round falloff whose whole point is that "a round mark on
/// the ground is one that does not announce which cell it belongs to". A second
/// map for the second kind of dark patch would be a second answer to how dark
/// ground is drawn, and the two would part at whichever of them nobody touched.
///
/// <b>Bounded, and it forgets the oldest</b> - <see cref="TrackMarks.Capacity"/>'s
/// arrangement. The map is rebuilt whole every frame, so what this costs is one
/// disc splat per pit per frame; a board that has been shelled for ten minutes
/// would otherwise pay for every shot of it forever.
///
/// <b>No fade, and that is a decision rather than an omission.</b> Burnt ground
/// does not recover either - <c>Wildfire</c>'s ash stays - and a crater healing
/// while the match runs is the one thing a crater must not do. The forgetting
/// above is a bound on cost, not a claim that the ground repaired itself, and the
/// two want different words: the oldest mark vanishes at once rather than dimming
/// its way out, because a mark that dims is a mark that says the earth is filling
/// in.
/// </summary>
public sealed class Craters
{
    /// <summary>How many marks the board remembers. Forty-eight is some four
    /// minutes of one tank firing as fast as it reloads.</summary>
    public const int Capacity = 48;

    /// <summary>How wide a mark is and how dark, in tile widths and in the ash
    /// map's own ink. Narrower than a burst is tall: what a shell leaves is a
    /// scald round the hole, not the whole footprint of the dust that flew.
    /// </summary>
    /// <summary>The two as the board is currently set: fields rather than
    /// constants so a bench can turn them, seeded with what they were.
    ///
    /// <c>Ink</c> is lighter than the ceiling it used to sit at, because the dust
    /// in front of it stopped being opaque: a mark darker than the cloud that made
    /// it reads as a hole with smoke over it rather than as scorched ground.
    /// </summary>
    public float Wide = WideDefault;
    public float Ink = InkDefault;

    public const float WideDefault = 0.17f;
    public const float InkDefault = 0.80f;

    /// <summary>One mark: where on the board, how high the ground was there, how
    /// dark and how wide. The lift travels with it because the map is indexed in
    /// world XZ and a cell two levels up projects to a different row - the same
    /// reason every other ground mark on this board is given one.</summary>
    public readonly record struct Pit(Vector2 Spot, float Lift, float Much,
                                      float Wide);

    private readonly List<Pit> _pits = new();

    public IReadOnlyList<Pit> Pits => _pits;

    public bool Any => _pits.Count > 0;

    /// <summary>Mark the ground. <paramref name="spot"/> is in board space, the
    /// space a shell's own landing point is in.</summary>
    /// <summary>Mark the ground as the board is currently set.</summary>
    public void Dig(Vector2 spot, float lift) => Dig(spot, lift, Ink, Wide);

    public void Dig(Vector2 spot, float lift, float much, float wide)
    {
        if (much <= 0.0f || wide <= 0.0f)
            return;
        if (_pits.Count >= Capacity)
            _pits.RemoveAt(0);
        _pits.Add(new Pit(spot, lift, much, wide));
    }

    /// <summary>Ground with nothing on it - the reset. Named for what it does to
    /// the board rather than to the list: a reset that leaves the craters is a
    /// board that was not reset.</summary>
    public void Fill() => _pits.Clear();
}
