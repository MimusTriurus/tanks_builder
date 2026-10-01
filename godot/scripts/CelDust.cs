using System.Collections.Generic;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The dust a 3D tank's belts raise, in the model's own look: <b>one cloud per
/// belt</b> (<see cref="CelCloud"/>) the colour of the shot's ground dust, with
/// one ink line round it. The sprites' drift (<see cref="TrackDust"/> on
/// <see cref="ProcKick"/>) is left as it is for the 2D tanks; on the model it
/// was the one soft haze left among inked shapes, and the 3D bench runs this
/// instead (<c>--fx2d</c> brings it back).
///
/// <b>Off the belt's own travel</b>, as <see cref="TrackDust"/> has it: a puff
/// every <see cref="Step"/> of belt run, not of time and not of the hull's
/// displacement, so a pivot on the spot - the belts churning against each
/// other - raises dust while a hull rolling at a crawl raises little.
///
/// <b>Born at the trailing end of the belt</b> - the rear under way, the front
/// when the belt winds backwards - where the ground leaves the track, and
/// <b>left where it was born</b>: the belt's underside does not move over the
/// ground, so the dust does not ride with the tank. Thrown astern and out
/// from under the belt, slowing, it spreads low, climbs a little, is taken by
/// the wind and eaten from the rim.
///
/// All lengths are shares of the hull's length on the board (<see cref="Build"/>).
/// </summary>
public sealed partial class CelDust : Node3D
{
    /// <summary>The tanks its puffs may not stand through, or none.</summary>
    public CelSolids? Solids;

    /// <summary>Puffs in the air at once per belt, at most - the cloud's.</summary>
    public const int Pool = CelCloud.Pool;

    /// <summary>Belt run between puffs, hull lengths.</summary>
    public float Step = 0.09f;
    /// <summary>A puff's life, s - and so the trail's length over the
    /// tank's speed, the puffs standing in the world: at 1.7 s the trail ran
    /// four hull lengths behind a light tank, and the user halved it.</summary>
    public float Life = 0.85f;
    /// <summary>A puff's width at birth and at the end, hull lengths, at full
    /// pace. At 0.24 the trail was two ropes on the ground: puffs that barely
    /// grew, one behind the other.</summary>
    public float Born = 0.08f, Grown = 0.40f;
    /// <summary>How much of the full size a puff at a crawl still has.</summary>
    public float CrawlSize = 0.50f;
    /// <summary>How far it is thrown astern and out from under the belt, hull
    /// lengths, at full pace, and how long the air takes to stop it, s.</summary>
    public float Back = 0.18f, Out = 0.12f, ThrowTime = 0.30f;
    /// <summary>How far it climbs over its life, hull lengths.</summary>
    public float Rise = 0.12f;
    /// <summary>Where the wind takes it, hull lengths a second, in the world.</summary>
    public Vector3 Wind = new(0.10f, 0.0f, -0.05f);
    /// <summary>Its grey and tint - <see cref="CelShot"/>'s ground dust tint, so
    /// the shot and the belts raise one earth, but darker: the sun on the ramp
    /// lifts a lit top well over its grey, and at the shot's 0.62 a long trail
    /// was cream on brown ground.</summary>
    public float Tone = 0.50f;
    public Color Tint = new(0.92f, 0.82f, 0.64f);
    /// <summary>How far apart two puffs still flow into one, hull lengths.</summary>
    public float Blend = 0.05f;
    /// <summary>When a puff starts to be eaten, as a share of its life.</summary>
    public float ErodeFrom = 0.25f;

    /// <summary>One belt this frame: its trailing end on the ground, the way
    /// the dust leaves it (flat, unit), the way out from under the hull (flat,
    /// unit), how far the belt ran this frame (px) and its pace as a share of
    /// the class's top speed. A belt on wet ground hands no run.</summary>
    public readonly record struct Belt(Vector3 At, Vector3 Astern, Vector3 Outward,
                                       float Run, float Pace);

    private struct Puff
    {
        public Vector3 At, Astern, Outward;
        public float Born, Life, Size, Throw, Tone, Seed;
    }

    private readonly List<List<Puff>> _air = new();
    private readonly List<float> _spent = new();
    private readonly List<CelCloud> _clouds = new();
    private float _hull = 150.0f;
    private float _clock;
    private int _births;

    public void Build(float hullPx) => _hull = Mathf.Max(hullPx, 1.0f);

    /// <summary>Everything in the air gone, the clocks back to nought.</summary>
    public void Reset()
    {
        foreach (List<Puff> air in _air)
            air.Clear();
        for (int i = 0; i < _spent.Count; i++)
            _spent[i] = 0.0f;
        foreach (CelCloud c in _clouds)
            c.Hide();
        _clock = 0.0f;
        _births = 0;
    }

    /// <summary>A frame: puffs born on <paramref name="belts"/> as far as each
    /// ran, every puff in the air put where it is, the dead ones dropped.
    /// <paramref name="eye"/> is the camera's basis.</summary>
    public void Tick(float dt, IReadOnlyList<Belt> belts, Basis eye)
    {
        _clock += dt;
        while (_air.Count < belts.Count)
        {
            _air.Add(new List<Puff>());
            _spent.Add(0.0f);
            _clouds.Add(new CelCloud(this, $"Dust{_clouds.Count}", Tint, Blend * _hull));
        }

        float step = Step * _hull;
        for (int i = 0; i < belts.Count; i++)
        {
            Belt b = belts[i];
            if (b.Run <= 0.0f)
            {
                // Forgotten rather than saved up: a tank that stands for a
                // minute and then moves an inch opens with no puff it owed.
                _spent[i] = 0.0f;
                continue;
            }
            _spent[i] += b.Run;
            // One a frame and the remainder carried - TrackDust's rule: two
            // puffs born on one frame stand at one point.
            if (_spent[i] < step)
                continue;
            _spent[i] = Mathf.Min(_spent[i] - step, step);
            Spawn(_air[i], b);
        }

        for (int i = 0; i < _air.Count; i++)
        {
            List<Puff> air = _air[i];
            air.RemoveAll(p => _clock - p.Born >= p.Life);
            CelCloud cloud = _clouds[i];
            cloud.Clear();
            foreach (Puff p in air)
            {
                float age = _clock - p.Born;
                float a = Mathf.Clamp(age / p.Life, 0.0f, 1.0f);
                float grow = 1.0f - Mathf.Pow(1.0f - a, 2.0f);
                float r = 0.5f * _hull * Mathf.Lerp(Born, Grown, grow) * p.Size
                          * Mathf.SmoothStep(0.0f, 0.06f, a);
                float thrown = p.Throw * (1.0f - Mathf.Exp(-age / Mathf.Max(ThrowTime, 1e-3f)));
                // Sat with most of its body over the ground: centred on it, the
                // ground's depth cut every puff in half.
                Vector3 at = p.At
                             + p.Astern * (Back * _hull * thrown)
                             + p.Outward * (Out * _hull * thrown)
                             + Vector3.Up * (0.7f * r + Rise * _hull * a)
                             + Wind * (_hull * age);
                cloud.Add(at, r, p.Tone, p.Seed, Mathf.SmoothStep(ErodeFrom, 1.0f, a), a);
            }
            cloud.Draw(eye, Solids);
        }
    }

    private void Spawn(List<Puff> air, Belt b)
    {
        if (air.Count >= Pool)
            air.RemoveAt(0);
        int k = _births++;
        float h1 = CelPuff.Hash(k, 23), h2 = CelPuff.Hash(k, 29), h3 = CelPuff.Hash(k, 31);
        float pace = Mathf.Clamp(b.Pace, 0.0f, 1.0f);
        air.Add(new Puff
        {
            // Across the belt's own width a little: the contact patch is a patch.
            At = b.At + b.Outward * ((h1 - 0.5f) * 0.05f * _hull),
            Astern = b.Astern,
            // Each out by its own share, some none at all: all out alike, the
            // puffs lay in one line.
            Outward = b.Outward * (1.4f * CelPuff.Hash(k, 37)),
            Born = _clock,
            Life = Life * (0.8f + 0.4f * h2),
            Size = Mathf.Lerp(CrawlSize, 1.0f, pace) * (0.8f + 0.4f * h3),
            Throw = (0.4f + 0.6f * pace) * (0.7f + 0.6f * h1),
            Tone = Tone * (0.92f + 0.16f * h2),
            Seed = h3,
        });
    }
}
