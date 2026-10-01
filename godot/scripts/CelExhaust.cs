using System.Collections.Generic;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// A running 3D tank's exhaust, in the model's own look: <b>one cloud</b> on
/// the model's ramp with one ink line round it, made of puffs born at each
/// <c>Exhaust.N</c> node, going out along its axis (the sidecar's
/// <c>points</c>, +Y) and up, bent by the wind and eaten away. The sprites'
/// plume (<see cref="ProcSmoke.Plume"/>) is left as it is for the 2D tanks; on
/// the model it was a pale haze across the turret, and the 3D bench hides it
/// and runs this.
///
/// <b>A cloud, not balls.</b> The puffs are not drawn one by one: they are
/// discs on one quad facing the eye, flowed into one shape by a smooth union
/// of their distances (<see cref="CelCloud"/>). Drawn as the burning
/// column's spheres, each inked on its own, the exhaust was a heap of cartoon
/// balls.
///
/// <b>Two states, as the sprites' plume has</b> (<see cref="ExhaustLoop.Binary"/>):
/// idling, a puff every so often, pale and slow; working, puffs close on one
/// another, bigger, greyer and quicker. Nothing in between - on the sprites
/// the ramp was tried three times and never read. The step up has a
/// <b>kick</b>: for <see cref="KickTime"/> the engine taking the load throws a
/// few dark puffs close together, and they fade into the working grey.
///
/// <b>Puffs, not a closed form.</b> Each is born with the state of its moment
/// - size, speed, grey, life - and keeps it, so a change of state changes the
/// puffs born after it, not the ones already in the air; and each stays where
/// it was born, in the world, so a tank driving off leaves its exhaust behind
/// it. The fixed step makes it repeat to the pixel all the same.
///
/// All lengths are shares of the hull's length on the board (<see cref="Build"/>).
/// </summary>
public sealed partial class CelExhaust : Node3D
{
    /// <summary>The tanks its puffs may not stand through, or none.</summary>
    public CelSolids? Solids;

    /// <summary>Puffs in the air at once, at most, over all ports - the
    /// cloud's (<see cref="CelCloud.Pool"/>).</summary>
    public const int Pool = CelCloud.Pool;

    /// <summary>Puffs a second from each port, idling and working.</summary>
    public float IdleRate = 4.0f, WorkRate = 13.0f;
    /// <summary>A puff's life, s. Working, it is the trail's length over the
    /// tank's speed - the puffs stand in the world: at 0.9 s a tank on the
    /// move dragged a tail of its own length (the user showed it).</summary>
    public float IdleLife = 1.3f, WorkLife = 0.5f;
    /// <summary>A puff's width at birth and at the end, hull lengths.</summary>
    public float IdleBorn = 0.04f, IdleGrown = 0.13f;
    public float WorkBorn = 0.06f, WorkGrown = 0.22f;
    /// <summary>How far a puff goes over its life, hull lengths: out of the
    /// port along its axis.</summary>
    public float IdleRise = 0.22f, WorkRise = 0.40f;
    /// <summary>The puffs' grey, idling and working, and their tint: the sun
    /// on the ramp lifts a lit top well over its grey - at 0.72 the idle puffs
    /// were white, cotton wool over the deck.</summary>
    public float IdleTone = 0.46f, WorkTone = 0.40f;
    public Color Tint = new(0.90f, 0.94f, 1.0f);

    /// <summary>The kick when the engine takes the load: how long, its puffs a
    /// second from each port, its grey at the start, and how much bigger its
    /// puffs are. It fades into the working state over its time.</summary>
    public float KickTime = 0.5f, KickRate = 20.0f, KickTone = 0.20f, KickSize = 1.3f;
    /// <summary>How long the engine must have idled before taking the load
    /// kicks, s: a tank arriving crosses the threshold a few times in a tenth
    /// of a second, and each crossing was a kick of its own.</summary>
    public float KickRest = 1.0f;

    /// <summary>Where the wind takes a puff, hull lengths per rise, in the
    /// world - the burning column's wind (<see cref="CelBurn.Drift"/>).</summary>
    public Vector3 Drift = new(0.45f, 0.0f, -0.20f);
    /// <summary>How far apart two puffs still flow into one, hull lengths -
    /// the smooth union's width.</summary>
    public float Blend = 0.06f;
    /// <summary>How long a puff keeps the speed of the tank it left, s: the gas
    /// comes out moving with the tank and the air stops it. Left standing
    /// where it was born, a moving tank's exhaust was a row of separate
    /// blobs strung out behind it.</summary>
    public float CarryTime = 0.3f;
    /// <summary>Where a puff is born over its port, hull lengths.</summary>
    public float Seat = 0.03f;
    /// <summary>When a puff starts to be eaten, as a share of its life.</summary>
    public float ErodeFrom = 0.15f;
    /// <summary>The whole exhaust's size, against the lengths above: its puffs,
    /// how far they go, their seat and wander all at once, so it shrinks in
    /// proportion - the user halved it. The puffs come <c>1/Scale</c> times as
    /// often and the blend keeps its width: halved alone, the puffs no longer
    /// met and a moving tank left a row of grey pellets.</summary>
    public float Scale = 0.5f;
    /// <summary>How far astern of its port a puff is born, hull lengths, along
    /// <see cref="Astern"/>: the user moved it back off the turret toward the
    /// stern.</summary>
    public float Aft = 0.1f;

    /// <summary>Astern along the hull, in the world, unit - set by the owner
    /// each frame; nought leaves the ports where they are.</summary>
    public Vector3 Astern;

    // ------------------------------------------------------------ the inputs

    /// <summary>The engine runs: puffs are born. Off, the last ones finish.</summary>
    public bool Running;
    /// <summary>The engine works - the tank pulls - rather than idles. Not
    /// while it brakes: off the throttle a diesel idles, and a tank that had
    /// stopped still smoked as if driving (the bench gives it no hold either -
    /// one of 0.35 s, against the arrival's chatter, was the same fault).</summary>
    public bool Working;

    // ------------------------------------------------------------ the machinery

    private struct Puff
    {
        public Vector3 At, Out, Carry;
        public float Born, Life, From, To, Rise, Tone, Seed, Side;
    }

    private readonly List<Puff> _air = new();
    private readonly List<float> _due = new();
    private readonly List<Vector3> _last = new();
    private float _hull = 150.0f;
    private float _clock;
    private float _kick;
    private bool _working;
    private float _restFor = float.MaxValue;
    private int _births;
    private CelCloud? _cloud;

    public void Build(float hullPx)
    {
        _hull = Mathf.Max(hullPx, 1.0f);
        _cloud = new CelCloud(this, "Exhaust", Tint, Blend * _hull);
    }

    /// <summary>Everything in the air gone, the clocks back to nought.</summary>
    public void Reset()
    {
        _air.Clear();
        _due.Clear();
        _last.Clear();
        _clock = 0.0f;
        _kick = 0.0f;
        _working = false;
        _restFor = float.MaxValue;
        _births = 0;
        _cloud?.Hide();
    }

    /// <summary>
    /// A frame: puffs born at <paramref name="ports"/> (where each is and the
    /// way it points, in the world) as the engine asks, every puff in the air
    /// put where it is, the dead ones dropped. <paramref name="eye"/> is the
    /// camera's basis: the cloud's quad faces it.
    /// </summary>
    public void Tick(float dt, IReadOnlyList<(Vector3 At, Vector3 Out)> ports, Basis eye)
    {
        if (_cloud is null)
            return;
        _clock += dt;
        // A kick only off a real standstill.
        bool working = Working;
        if (working && !_working && Running && _restFor >= KickRest)
            _kick = KickTime;
        _restFor = working ? 0.0f : _restFor + dt;
        _working = working;
        _kick = Mathf.Max(0.0f, _kick - dt);

        while (_due.Count < ports.Count)
            // Out of step with one another from the first puff.
            _due.Add(CelPuff.Hash(_due.Count, 71));
        while (_last.Count < ports.Count)
            _last.Add(ports[_last.Count].At);
        if (Running)
        {
            float kick = KickTime > 0.0f ? _kick / KickTime : 0.0f;
            float rate = (_working ? Mathf.Lerp(WorkRate, KickRate, kick) : IdleRate) / Mathf.Max(Scale, 0.1f);
            for (int i = 0; i < ports.Count; i++)
            {
                _due[i] += rate * dt;
                Vector3 carry = dt > 0.0f ? (ports[i].At - _last[i]) / dt : Vector3.Zero;
                while (_due[i] >= 1.0f)
                {
                    _due[i] -= 1.0f;
                    Born(ports[i], carry, kick);
                }
            }
        }

        for (int i = 0; i < ports.Count; i++)
            _last[i] = ports[i].At;
        _air.RemoveAll(p => _clock - p.Born >= p.Life);
        _cloud.Clear();
        int n = Mathf.Min(_air.Count, Pool);
        for (int i = 0; i < n; i++)
        {
            Puff p = _air[_air.Count - n + i];
            float a = Mathf.Clamp((_clock - p.Born) / p.Life, 0.0f, 1.0f);
            // Out fast and slowing, bent by the wind as it goes.
            float gone = 1.0f - Mathf.Pow(1.0f - a, 2.2f);
            float size = Scale * _hull;
            float rise = p.Rise * size;
            Vector3 side = new(Mathf.Cos(p.Side), 0.0f, Mathf.Sin(p.Side));
            float age = _clock - p.Born;
            Vector3 at = p.At + p.Carry * (CarryTime * (1.0f - Mathf.Exp(-age / Mathf.Max(CarryTime, 1e-3f))))
                         + p.Out * (Seat * size + rise * gone)
                         + Drift * (rise * Mathf.Pow(a, 1.4f))
                         + side * (0.04f * size * Mathf.Sqrt(a));
            float r = 0.5f * size * Mathf.Lerp(p.From, p.To, gone) * Mathf.SmoothStep(0.0f, 0.08f, a);
            _cloud.Add(at, r, p.Tone, p.Seed, Mathf.SmoothStep(ErodeFrom, 1.0f, a), a);
        }
        _cloud.Draw(eye, Solids);
    }

    /// <summary>A puff at <paramref name="port"/>, with the state of this
    /// moment, moving as the port moves (<paramref name="carry"/>, px/s);
    /// <paramref name="kick"/> is how much of the kick is left, 0..1.</summary>
    private void Born((Vector3 At, Vector3 Out) port, Vector3 carry, float kick)
    {
        if (_air.Count >= Pool)
            _air.RemoveAt(0);
        int k = _births++;
        float h1 = CelPuff.Hash(k, 11), h2 = CelPuff.Hash(k, 13), h3 = CelPuff.Hash(k, 17);
        float big = (0.8f + 0.4f * h2) * (_working ? Mathf.Lerp(1.0f, KickSize, kick) : 1.0f);
        _air.Add(new Puff
        {
            At = port.At + Astern * (Aft * _hull),
            Out = port.Out,
            Carry = carry,
            Born = _clock,
            Life = (_working ? WorkLife : IdleLife) * (0.85f + 0.3f * h3),
            From = (_working ? WorkBorn : IdleBorn) * big,
            To = (_working ? WorkGrown : IdleGrown) * big,
            Rise = (_working ? WorkRise : IdleRise) * (0.85f + 0.3f * h1),
            Tone = _working ? Mathf.Lerp(WorkTone, KickTone, kick) : IdleTone,
            Seed = h1,
            Side = h2 * Mathf.Tau,
        });
    }
}
