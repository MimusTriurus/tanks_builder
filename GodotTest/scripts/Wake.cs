using System;
using System.Collections.Generic;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The trail a tank leaves on the water behind it.
///
/// <b>Stamps along the path, not a shape behind the tank.</b> A wake hung at a
/// fixed offset behind the hull travels with it and reads as a comet - the same
/// failure the tracer's smoke had, and the same fix: a stamp belongs to the
/// place it was made, is born when the tank passes it, and ages from then on.
/// Which is also what makes it continuous at any speed, because nothing about it
/// depends on how fast the thing that made it was going.
///
/// <b>Spacing is a contract with the radius, and it is checked.</b> A fresh stamp
/// has to be wider than the gap to the next one or the trail is a dotted line -
/// <see cref="Seed"/> against <see cref="Step"/>, the rule the tracer's smoke
/// states in the same words. It is worth stating because both numbers look
/// independently reasonable and the failure only shows on the slowest tank.
///
/// <b>A point and two flags, not a Vehicle.</b> What makes a wake is something
/// that is somewhere, moving, and in the water; taking more than that would be
/// this class knowing about tanks in order to ignore most of what it knew - the
/// argument <see cref="Swell"/> already makes, and it buys the same thing, which
/// is that the interesting part can be asserted without building a tank.
///
/// <b>It does not know about cells, and that is the difference from the swell.</b>
/// The swell answers "what is this hexagon doing" and is therefore quantised to
/// hexagons; a wake is a line across them. They are two statements about the
/// same event at two resolutions, and neither can be got from the other.
/// </summary>
public sealed class Wake
{
    /// <summary>Whether a trail is laid at all. Off leaves the water as it was
    /// before this - see --no-wake, which is the A/B it is judged by.</summary>
    public bool Enabled = true;

    /// <summary>
    /// How long a stamp lives, in seconds.
    ///
    /// <b>Set against how short the ford is, not against how fast a tank goes.</b>
    /// The first numbers were reasoned from the ford's 108px/s and were wrong for
    /// a reason the trace gave: a tank crosses this pond in about 200px and slows
    /// to a crawl going into the turn on the far side, so a two-second life over
    /// a thirty-unit step left <b>two</b> stamps on the water for the whole
    /// crossing. A trail wants to outlast the crossing that made it.
    /// </summary>
    public const float Life = 4.5f;

    /// <summary>How far the tank moves between stamps, in world units. Measured
    /// off the crossing rather than picked: at 108px/s this is a stamp every
    /// seven frames, so the shortest useful trip through the water still lays a
    /// dozen and a half of them.</summary>
    public const float Step = 12.0f;

    /// <summary>How wide a stamp starts, in world units. Sized against the hull
    /// and not chosen: the pond's hexagon is about 93 units to a corner and a
    /// tank is about 110px broadside, so the first numbers - a nine-unit stamp -
    /// drew a trail four pixels wide down the screen and measured 64 changed
    /// pixels in the whole pond. <b>Over half of
    /// <see cref="Step"/> on purpose</b>: below that the trail is a row of dots
    /// rather than a line, and no amount of opacity fixes it.</summary>
    public const float Seed = 40.0f;

    /// <summary>How wide it has ended up by the time it dies. Water spreads what
    /// is dropped in it; a stamp that keeps its width reads as a painted stripe.
    /// </summary>
    public const float Spread = 110.0f;

    /// <summary>How many stamps the board can hold at once. A cap because a
    /// shader array has to have one, and named rather than left at whatever
    /// looked big - at the ford's speed one tank holds about twenty-seven of
    /// them, so the board has room for one crossing and the tail of another.
    /// </summary>
    public const int Max = 48;

    /// <summary>Below this a tank is parked rather than driving, in px/s. The
    /// swell's own threshold, and it has to be the same one: a tank that stirs
    /// the water it stands in and leaves no trail through it, or the other way
    /// round, is two answers to one question.</summary>
    public const float DriveAbove = Swell.StirAbove;

    private readonly List<Vector2> _at = new();
    private readonly List<float> _lift = new();
    private readonly List<float> _age = new();
    private readonly Dictionary<int, Vector2> _last = new();

    /// <summary>Where every live stamp is, in the tanks' own space, oldest
    /// first.</summary>
    public IReadOnlyList<Vector2> At => _at;

    /// <summary>How high off the datum the water each stamp was left on stands,
    /// in screen px, in the same order.
    ///
    /// <b>Carried because the point is a drawn row.</b> A drawn row has the lift
    /// taken out of it already, so a reader that hands it to
    /// <c>Stage3D.World</c> with a lift of zero puts the stamp a whole level too
    /// deep, and the plane it is compared against is horizontal - so the trail
    /// lands a level below the tank that laid it.
    ///
    /// <b>The water's own height and not the ground's</b>, which is a second
    /// thing the same number could have been and is off by the depth of the ford:
    /// see <c>Stage3D.Ground</c>, where that reads as the trail clinging to the
    /// far track. Carried rather than applied for the reason <see cref="Swell"/>
    /// keeps the tanks' own coordinates: which camera is looking at them is not
    /// this class's business.</summary>
    public IReadOnlyList<float> Lift => _lift;

    /// <summary>How far through its life each stamp is, 0 fresh through 1 gone,
    /// in the same order.</summary>
    public IReadOnlyList<float> Age => _age;

    /// <summary>How many stamps are live. Reported because a trail that is being
    /// computed and never drawn and a trail that is not being made at all are the
    /// same empty water.</summary>
    public int Count => _at.Count;

    /// <summary>
    /// Say where one tank is, and whether it is driving through water.
    ///
    /// <b>Distance decides, not time.</b> A stamp every so many seconds gives a
    /// dense trail behind a crawling tank and a dotted one behind a fast one -
    /// the ruts and the belt phase are laid off distance for the same reason, and
    /// it is the same reason: what the ground and the water remember is where
    /// something went, not how long it took.
    /// </summary>
    public void Note(int who, Vector2 at, float lift, bool moving, bool wet)
    {
        if (!Enabled || !moving || !wet)
        {
            // Forgotten rather than kept, so a tank that leaves the water and
            // comes back does not draw a stamp across the dry ground between.
            _last.Remove(who);
            return;
        }
        if (_last.TryGetValue(who, out Vector2 was)
            && at.DistanceSquaredTo(was) < Step * Step)
            return;
        _last[who] = at;
        if (_at.Count >= Max)
        {
            _at.RemoveAt(0);
            _lift.RemoveAt(0);
            _age.RemoveAt(0);
        }
        _at.Add(at);
        _lift.Add(lift);
        _age.Add(0.0f);
    }

    /// <summary>Age every stamp and drop the dead. Called once after every tank
    /// has been noted, for <see cref="Swell.Tick"/>'s reason: what the board
    /// holds is not known until they all have.</summary>
    public void Tick(double delta)
    {
        float step = (float)delta / Mathf.Max(Life, 1e-4f);
        for (int i = _age.Count - 1; i >= 0; i--)
        {
            _age[i] += step;
            if (_age[i] >= 1.0f)
            {
                _at.RemoveAt(i);
                _lift.RemoveAt(i);
                _age.RemoveAt(i);
            }
        }
    }

    /// <summary>Take the trail off the water and forget where everybody was.
    /// Called on a reset or when the water is taken off the board: a trail left
    /// standing would come back as a wake nobody had made.</summary>
    public void Clear()
    {
        _at.Clear();
        _lift.Clear();
        _age.Clear();
        _last.Clear();
    }
}
