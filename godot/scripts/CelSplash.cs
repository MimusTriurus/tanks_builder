using System.Collections.Generic;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The water a hull throws going into the pond, in the model's look: sheets of
/// water standing up off the nose and both flanks, and a ring of foam running
/// out over the surface - <see cref="Plunge"/>'s event, drawn the way the
/// model's other effects are drawn. <see cref="Plunge"/> is soft white puffs
/// with no line and no depth, over everything: its far fan lay across the deck,
/// and a pile of soft white on a cel board read as steam.
///
/// <b>A sheet is a row of jets, not a cloud.</b> Each jet is a tapered capsule
/// from its foot on the water to its tip, thrown once - out and up - and
/// falling back under <see cref="Gravity"/>, the tip on a closed-form arc as
/// <see cref="Plunge.Drop"/> writes a drop's. The jets of one sheet are flowed
/// into one shape by a smooth union of their distances on a quad facing the
/// eye - <see cref="CelCloud"/>'s machinery - so the sheet has one ink line
/// round it and a ragged crown of tips, which is what says water rather than
/// smoke: puffs are round, a splash is pointed. Three sheets, the bow wave and
/// the two flanks' fans, each its own quad, so the one behind the hull is
/// behind it: the depth is written per pixel off the capsules' fronts, and the
/// model's own depth hides the far fan and is hidden by the near one.
///
/// <b>It goes by tearing, not fading.</b> Past its apex a jet is eaten from the
/// tip down by a noise along it - the sheet breaks up as it falls back - and
/// what is left sinks into the water, which covers it by its own thickness.
///
/// <b>Three tones, the model's ramp</b> (<see cref="Toon.RampCode"/>) on the
/// capsules' normals: the pond's pale water lit, its teal in shade, and a white
/// crest over the top of each jet, where the sheet is thinnest and catches the
/// light. The ink is the water's own teal, dark, not black.
///
/// <b>A round into the water</b> (<see cref="Spout"/>) is the same water in
/// another shape, the way the jackal port's preview throws one (ratel,
/// <c>Level3DRocket._splash</c>): a column - a few tall jets straight up in
/// the middle, a ring of shorter ones round them leaning out, the back half of
/// the ring a sheet behind the column and the front half one before it; a
/// crown of drops thrown out and up all round, each a short capsule along the
/// way it flies, falling back in; and <b>four rings of foam one after
/// another</b> (<see cref="Rings"/>) - a patch at first, opening into a ring
/// that slows, thins and is gone as a line, the first going furthest (a later
/// one going further would cross it), the last the foam where the column
/// came down. The rings have no ink: the water is left soft, and a dark line
/// round every ripple drew a target on it (ratel's lesson).
///
/// <b>The ring</b> is a flat band on the water in the hull's shape - a rounded
/// rectangle a margin out from the hull, as the board's waterline is the
/// hull's silhouette rather than an ellipse round it (docs/water.md) - that
/// runs out over the first second and breaks into patches.
/// </summary>
public sealed partial class CelSplash : Node3D
{
    /// <summary>World px a second squared, the tips' fall: about
    /// <see cref="Plunge.Gravity"/>, so a fan is back in the water inside a
    /// second.</summary>
    public const float Gravity = 720.0f;

    /// <summary>Jets per flank and across the bow.</summary>
    public const int FanJets = 12, BowJets = 9;

    /// <summary>Up speeds, world px/s, at might one: the fans stand about 40 px
    /// over the water, the bow wave lower and pushed further out.</summary>
    public const float FanUp = 245.0f, BowUp = 215.0f;

    /// <summary>How far out a jet goes for every px it goes up.</summary>
    public const float FanOut = 0.35f, BowOut = 0.8f;

    /// <summary>Seconds the ring runs, and how far out it gets past the hull,
    /// world px at might one.</summary>
    public const float RingLife = 1.6f, RingReach = 95.0f;

    /// <summary>A round's column (<see cref="Spout"/>): the jets round it and
    /// in its middle, their up speed, world px/s at might one - the middle
    /// some 150 px tall at might one, four times a fan - how far out a ring jet leans for
    /// every px up, and the column's foot, world px across the middle.</summary>
    public const int SpoutJets = 16, SpoutCore = 5, SpoutDrops = 14;
    public const float SpoutUp = 470.0f, SpoutOut = 0.16f, SpoutRadius = 11.0f;

    /// <summary>The water's colours: lit, the shaded teal (the ramp's shade),
    /// the crest and the ink.</summary>
    public static readonly Color Body = new(0.62f, 0.83f, 0.87f);
    public static readonly Color Shade = new(0.30f, 0.52f, 0.58f);
    public static readonly Color Crest = new(0.97f, 1.0f, 1.0f);
    public static readonly Color Ink = new(0.08f, 0.24f, 0.26f);

    private const int Pool = 28;

    /// <summary>The column's rings: reach, world px at might one, life and
    /// delay, s - ratel's four, on a column some 150 px tall where theirs is
    /// two metres.</summary>
    private static readonly (float Reach, float Life, float Delay)[] Rings =
    {
        (210.0f, 1.8f, 0.0f), (158.0f, 1.6f, 0.25f), (105.0f, 1.4f, 0.5f), (60.0f, 0.9f, 1.0f),
    };

    private readonly List<(MeshInstance3D Quad, ShaderMaterial Look)> _ripples = new();

    private sealed class Sheet
    {
        public MeshInstance3D Quad = null!;
        public ShaderMaterial Look = null!;
        public readonly Vector4[] Foot = new Vector4[Pool];
        public readonly Vector4[] Tip = new Vector4[Pool];
        public readonly Vector4[] Looks = new Vector4[Pool];
        public int N;
    }

    private readonly Sheet[] _sheets = new Sheet[3];
    private MeshInstance3D _ring = null!;
    private ShaderMaterial _ringLook = null!;
    private float _clock = -1.0f;
    private float _might = 1.0f;
    private Vector3 _seat;
    private Vector3 _along = Vector3.Back, _across = Vector3.Right;
    private float _halfLen = 70.0f, _halfWide = 45.0f;
    private int _seed;
    private bool _column;
    private float _squash = 0.5f, _rise = 0.86f;

    public bool Busy => _clock >= 0.0f;

    public void Build(float squash, float rise)
    {
        _squash = squash;
        _rise = rise;
        for (int i = 0; i < 3; i++)
        {
            var look = new ShaderMaterial { Shader = SheetShader };
            look.SetShaderParameter("blend", 6.0f);
            look.SetShaderParameter("ink_width", Toon.InkWidth);
            look.SetShaderParameter("ink_min_px", Toon.InkMinPx);
            look.SetShaderParameter("body", Body);
            look.SetShaderParameter("shade", new Vector3(Shade.R, Shade.G, Shade.B));
            look.SetShaderParameter("crest", Crest);
            look.SetShaderParameter("ink", Ink);
            var quad = new MeshInstance3D
            {
                Name = $"Sheet{i}",
                Mesh = new QuadMesh { Size = Vector2.One },
                MaterialOverride = look,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                Visible = false,
            };
            AddChild(quad);
            _sheets[i] = new Sheet { Quad = quad, Look = look };
        }
        _ringLook = new ShaderMaterial { Shader = RingShader, RenderPriority = Stage3D.DressOrder };
        _ringLook.SetShaderParameter("foam", Crest);
        _ringLook.SetShaderParameter("edge", Body);
        _ring = new MeshInstance3D
        {
            Name = "Ring",
            Mesh = new PlaneMesh { Size = Vector2.One },
            MaterialOverride = _ringLook,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            // After the pond, which is transparent and drawn late: the wet
            // ruts' reason (CelRuts).
            SortingOffset = -100000.0f,
            Visible = false,
        };
        AddChild(_ring);
        foreach (var _ in Rings)
        {
            var look = new ShaderMaterial { Shader = RippleShader, RenderPriority = Stage3D.DressOrder };
            look.SetShaderParameter("foam", Crest);
            look.SetShaderParameter("wash", Body);
            var quad = new MeshInstance3D
            {
                Name = $"Ripple{_ripples.Count}", Mesh = new PlaneMesh { Size = Vector2.One * 2.0f },
                MaterialOverride = look, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                // After the pond, the ring's reason.
                SortingOffset = -100000.0f, Visible = false,
            };
            AddChild(quad);
            _ripples.Add((quad, look));
        }
    }

    /// <summary>
    /// A hull goes in: <paramref name="seat"/> is the water's surface under its
    /// middle, world; <paramref name="ahead"/> the way it was going on the
    /// ground (the bow wave goes that way); the half-lengths its hull's, world
    /// px; <paramref name="might"/> <see cref="TankTick.PlungeMight"/> times the
    /// share (two fifths down a ramp). Speeds go by its square root.
    /// </summary>
    public void Fire(Vector3 seat, Vector3 ahead, float halfLen, float halfWide, float might,
                     Vector3? hex = null, float hexRadius = 0.0f)
    {
        _seat = seat;
        _hex = hex;
        _hexRadius = hexRadius;
        var flat = new Vector3(ahead.X, 0.0f, ahead.Z);
        _along = flat.LengthSquared() > 1e-6f ? flat.Normalized() : Vector3.Back;
        _across = new Vector3(_along.Z, 0.0f, -_along.X);
        _halfLen = Mathf.Max(halfLen, 1.0f);
        _halfWide = Mathf.Max(halfWide, 1.0f);
        _might = Mathf.Max(might, 0.05f);
        _seed++;
        _clock = 0.0f;
        _column = false;
    }

    /// <summary>A round goes into the water at <paramref name="seat"/> (the
    /// surface, world): the column and a round ring - see the class note.</summary>
    public void Spout(Vector3 seat, float might = 1.0f)
    {
        Fire(seat, Vector3.Back, SpoutRadius, SpoutRadius, might);
        _column = true;
    }

    public void Reset()
    {
        _clock = -1.0f;
        foreach (Sheet s in _sheets)
            if (s is not null)
                s.Quad.Visible = false;
        if (_ring is not null)
            _ring.Visible = false;
        foreach (var (quad, _) in _ripples)
            quad.Visible = false;
    }

    /// <summary>How long the event is: the latest-born, fastest jet back in the
    /// water, or the ring gone.</summary>
    private float Length => Mathf.Max(0.2f + 2.0f * (_column ? SpoutUp * 1.3f : FanUp * 1.2f) * Mathf.Sqrt(_might) / Gravity,
                                      _column ? 1.9f : RingLife);

    public void Tick(float dt, Basis eye)
    {
        if (_clock < 0.0f)
            return;
        _clock += dt;
        if (_clock > Length)
        {
            Reset();
            return;
        }
        float speed = Mathf.Sqrt(_might);
        Sheet bow = _sheets[0], left = _sheets[1], right = _sheets[2];
        bow.N = left.N = right.N = 0;
        if (_column)
        {
            Column(speed, eye);
            foreach (Sheet s in _sheets)
                Draw(s, eye);
            _ring.Visible = false;
            Ripples(speed);
            return;
        }
        // The flanks: jets along each side, the front ones in first - the nose
        // goes over the edge before the tail - and standing highest a little
        // ahead of the middle, where the hull hits hardest.
        float spacing = 2.0f * _halfLen * 1.3f / FanJets;
        for (int side = 0; side < 2; side++)
        {
            float sign = side == 0 ? 1.0f : -1.0f;
            for (int j = 0; j < FanJets; j++)
            {
                float s = (j + 0.5f) / FanJets;
                float h1 = Hash(j, 1 + side), h2 = Hash(j, 3 + side), h3 = Hash(j, 5 + side);
                Vector3 foot = _seat + _along * (_halfLen * (-0.6f + 1.3f * s + 0.08f * (h1 - 0.5f)))
                               + _across * (sign * _halfWide * 1.02f);
                Vector3 outward = (_across * sign + _along * (0.35f * (s - 0.4f))).Normalized();
                float profile = 0.55f + 0.45f * Mathf.Sin(Mathf.Pi * Mathf.Clamp(s * 1.1f, 0.0f, 1.0f));
                float up = FanUp * profile * (0.55f + 0.8f * h2 * h2) * speed;
                float born = 0.02f + 0.16f * (1.0f - s) + 0.03f * h3;
                Jet(side == 0 ? left : right, foot, outward, up, FanOut, born, spacing * 0.72f, j + 20 * side);
            }
        }
        // The bow wave: across the nose, fanned out the way it goes.
        float bowSpacing = 2.0f * _halfWide * 1.2f / BowJets;
        for (int j = 0; j < BowJets; j++)
        {
            float u = (j + 0.5f) / BowJets * 2.0f - 1.0f;
            float h1 = Hash(j, 7), h2 = Hash(j, 8);
            Vector3 foot = _seat + _along * (_halfLen * (1.0f - 0.25f * u * u + 0.15f * (h2 - 0.5f))) + _across * (_halfWide * 1.2f * u);
            Vector3 outward = (_along + _across * (0.9f * u)).Normalized();
            float up = BowUp * (1.0f - 0.3f * u * u) * (0.5f + 0.9f * h1 * h1) * speed;
            float born = 0.01f + 0.12f * h2;
            Jet(bow, foot, outward, up, BowOut, born, bowSpacing * 0.8f, 60 + j);
        }
        foreach (Sheet s in _sheets)
            Draw(s, eye);
        Ring(speed);
    }

    /// <summary>
    /// One jet this frame: its tip on its arc, and its tail - on the water
    /// while it rises, then lifting off it on a slower arc of its own, so past
    /// the apex the jet is a short column of water falling back rather than a
    /// spike from the surface to wherever the tip has flown. The first cut kept
    /// the foot on the water to the end, and the tallest jet of each fan stood
    /// as a horn a hull long. Eaten from the tip down as it falls.
    /// </summary>
    private void Jet(Sheet sheet, Vector3 foot, Vector3 outward, float up, float outShare,
                     float born, float radius, int k)
    {
        float t = _clock - born;
        if (t <= 0.0f || sheet.N >= Pool)
            return;
        float life = 2.0f * up / Gravity;
        if (t >= life)
            return;
        // Inside the hex the hull went into (the user's ask): a jet whose foot
        // is past its rim - the stern still over the bank as the middle drops -
        // is not thrown, and the rest land short of the rim. The rise stays: a
        // sheet falling back into its own hex is still that tall.
        if (_hex is Vector3 centre)
        {
            float room = Room(foot, outward, centre) - radius * 0.6f;
            if (room <= 0.0f)
                return;
            outShare = Mathf.Min(outShare, room / (up * life));
        }
        float a = t / life;
        float rise = up * t - 0.5f * Gravity * t * t;
        float run = up * outShare * t;
        float slow = up * 0.85f * Mathf.SmoothStep(0.2f, 0.65f, a);
        float tailRise = slow * t - 0.5f * Gravity * t * t;
        Vector3 root = foot + outward * (run * 0.3f + slow * outShare * t * 0.7f)
                       + Vector3.Up * Mathf.Max(tailRise, -4.0f);
        Vector3 tip = foot + outward * run + Vector3.Up * Mathf.Max(rise, 0.0f);
        // Rising the jet is whole; from a little before its apex it tears
        // from the tip down, and the last of it is eaten outright.
        float cut = Mathf.Lerp(1.15f, 0.1f, Mathf.SmoothStep(0.5f, 1.0f, a));
        float thin = 1.0f - Mathf.SmoothStep(0.55f, 0.92f, a);
        float r0 = radius * (0.75f + 0.5f * Hash(k, 11)) * Mathf.Min(1.0f, 0.4f + 3.0f * a) * thin;
        float r1 = r0 * 0.45f;
        sheet.Foot[sheet.N] = new Vector4(root.X, root.Y, root.Z, r0);
        sheet.Tip[sheet.N] = new Vector4(tip.X, tip.Y, tip.Z, r1);
        sheet.Looks[sheet.N] = new Vector4(Hash(k, 12) * 7.0f + _seed * 3.1f, cut, a, 0.0f);
        sheet.N++;
    }

    /// <summary>The column this frame: the middle's tall jets on one sheet, the
    /// ring's split between the sheet behind them and the one before.</summary>
    private void Column(float speed, Basis eye)
    {
        Sheet core = _sheets[0], behind = _sheets[1], before = _sheets[2];
        Vector3 back = eye.Z.Normalized();
        for (int j = 0; j < SpoutCore; j++)
        {
            float h1 = Hash(j, 21), h2 = Hash(j, 22), h3 = Hash(j, 23);
            float ang = h1 * Mathf.Tau;
            var lean = new Vector3(Mathf.Cos(ang), 0.0f, Mathf.Sin(ang));
            float up = SpoutUp * (0.75f + 0.3f * h2) * speed;
            Jet(core, _seat + lean * (SpoutRadius * 0.45f * h3), lean, up, 0.04f, 0.008f * j, SpoutRadius * 1.25f, 100 + j);
        }
        for (int j = 0; j < SpoutJets; j++)
        {
            float h1 = Hash(j, 24), h2 = Hash(j, 25), h3 = Hash(j, 26);
            float ang = (j + 0.6f * h1) / SpoutJets * Mathf.Tau;
            var outward = new Vector3(Mathf.Cos(ang), 0.0f, Mathf.Sin(ang));
            float up = SpoutUp * (0.35f + 0.35f * h2) * speed;
            float born = 0.01f + 0.05f * h3;
            Sheet sheet = outward.Dot(back) > 0.0f ? before : behind;
            Jet(sheet, _seat + outward * (SpoutRadius * 1.1f), outward, up, SpoutOut * (0.6f + 0.9f * h1),
                born, SpoutRadius * 0.9f, 120 + j);
        }
        // The crown: drops out and up all round, back into the water.
        for (int j = 0; j < SpoutDrops; j++)
        {
            float h1 = Hash(j, 27), h2 = Hash(j, 28), h3 = Hash(j, 29);
            float ang = (j + 0.6f * h1) / SpoutDrops * Mathf.Tau;
            var outward = new Vector3(Mathf.Cos(ang), 0.0f, Mathf.Sin(ang));
            Vector3 v = outward * ((110.0f + 90.0f * h2) * speed) + Vector3.Up * ((230.0f + 120.0f * h3) * speed);
            float t = _clock - 0.03f - 0.04f * h1;
            float life = 2.0f * v.Y / Gravity;
            if (t <= 0.0f || t >= life)
                continue;
            Vector3 vel = v + Vector3.Down * (Gravity * t);
            Vector3 at = _seat + outward * (SpoutRadius * 1.3f) + v * t + Vector3.Down * (0.5f * Gravity * t * t);
            // Long along the way it flies: a drop, not a pebble.
            float r = (2.6f + 1.4f * h2) * (1.0f - 0.4f * t / life);
            Vector3 tail = at - vel.Normalized() * (r * 2.6f);
            Sheet sheet = outward.Dot(back) > 0.0f ? before : behind;
            if (sheet.N >= Pool)
                continue;
            sheet.Foot[sheet.N] = new Vector4(tail.X, tail.Y, tail.Z, r * 0.7f);
            sheet.Tip[sheet.N] = new Vector4(at.X, at.Y, at.Z, r);
            sheet.Looks[sheet.N] = new Vector4(Hash(j, 30) * 7.0f + _seed * 3.1f, 1.2f, 0.0f, 0.0f);
            sheet.N++;
        }
    }

    /// <summary>The column's four rings this frame (see the class note).</summary>
    private void Ripples(float speed)
    {
        for (int i = 0; i < Rings.Length && i < _ripples.Count; i++)
        {
            var (reach0, life, delay) = Rings[i];
            var (quad, look) = _ripples[i];
            float k = (_clock - delay) / life;
            if (k < 0.0f || k > 1.0f)
            {
                quad.Visible = false;
                continue;
            }
            float reach = reach0 * speed;
            // Out fast and slowing; the band from a patch to a line.
            float radius = Mathf.Lerp(reach * 0.12f, reach, 1.0f - (1.0f - k) * (1.0f - k));
            float band = reach * Mathf.Lerp(0.14f, 0.012f, k);
            // A hair over the water, not the board's lift toward the eye: lifted
            // that far it stood in front of the bank, and the rings ran out
            // over the dry ground; on the water the bank hides them where the
            // shore is, as ratel's land does.
            quad.GlobalTransform = new Transform3D(Basis.Identity.Scaled(new Vector3(radius, 1.0f, radius)),
                                                   _seat + Vector3.Up * 0.5f);
            look.SetShaderParameter("inner", Mathf.Clamp(1.0f - band / Mathf.Max(radius, 1e-3f), 0.0f, 1.0f)
                                             * (i == Rings.Length - 1 ? Mathf.SmoothStep(0.0f, 1.0f, k) : 1.0f));
            look.SetShaderParameter("seed", _seed * 7.3f + i * 13.1f);
            quad.Visible = true;
        }
    }

    private Vector3? _hex;
    private float _hexRadius;

    /// <summary>
    /// How far a point may go along <paramref name="way"/> before it leaves the
    /// hex round <paramref name="centre"/>, world px - nought if it is out
    /// already. The board's hexes are flat-topped and regular in the world
    /// (the squash is the camera's): corners on the X axis, the six edges'
    /// normals at 30 + 60k degrees, <see cref="_hexRadius"/> to a corner.
    /// </summary>
    private float Room(Vector3 at, Vector3 way, Vector3 centre)
    {
        float inner = _hexRadius * Mathf.Sqrt(3.0f) * 0.5f;
        var p = new Vector2(at.X - centre.X, at.Z - centre.Z);
        var d = new Vector2(way.X, way.Z);
        float room = float.MaxValue;
        for (int e = 0; e < 6; e++)
        {
            float angle = Mathf.DegToRad(30.0f + 60.0f * e);
            var n = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
            float gap = inner - p.Dot(n);
            if (gap <= 0.0f)
                return 0.0f;
            float toward = d.Dot(n);
            if (toward > 1e-4f)
                room = Mathf.Min(room, gap / toward);
        }
        return room;
    }

    private static void Draw(Sheet s, Basis eye)
    {
        if (s.N == 0)
        {
            s.Quad.Visible = false;
            return;
        }
        Vector3 right = eye.X.Normalized(), up = eye.Y.Normalized(), back = eye.Z.Normalized();
        Vector3 mid = Vector3.Zero;
        for (int i = 0; i < s.N; i++)
            mid += new Vector3(s.Foot[i].X + s.Tip[i].X, s.Foot[i].Y + s.Tip[i].Y, s.Foot[i].Z + s.Tip[i].Z) * 0.5f;
        mid /= s.N;
        float wide = 0.0f, tall = 0.0f, front = 0.0f;
        for (int i = 0; i < s.N; i++)
            foreach (Vector4 e in new[] { s.Foot[i], s.Tip[i] })
            {
                var at = new Vector3(e.X, e.Y, e.Z);
                float reach = s.Foot[i].W * 1.3f + 10.0f;
                wide = Mathf.Max(wide, Mathf.Abs((at - mid).Dot(right)) + reach);
                tall = Mathf.Max(tall, Mathf.Abs((at - mid).Dot(up)) + reach);
                front = Mathf.Max(front, (at - mid).Dot(back) + s.Foot[i].W);
            }
        s.Quad.GlobalTransform = new Transform3D(
            new Basis(right * (2.0f * wide), up * (2.0f * tall), back), mid + back * front);
        s.Quad.Visible = true;
        s.Look.SetShaderParameter("feet", s.Foot);
        s.Look.SetShaderParameter("tips", s.Tip);
        s.Look.SetShaderParameter("looks", s.Looks);
        s.Look.SetShaderParameter("count", s.N);
    }

    /// <summary>The foam running out over the water in the hull's shape.</summary>
    private void Ring(float speed)
    {
        float a = Mathf.Clamp(_clock / RingLife, 0.0f, 1.0f);
        float reach = RingReach * speed;
        float span = Mathf.Max(_halfLen, _halfWide) + reach + 20.0f;
        _ring.GlobalTransform = new Transform3D(
            new Basis(_across * (2.0f * span), Vector3.Up, _along * (2.0f * span)),
            _seat + Stage3D.Clear(_squash, _rise));
        _ring.Visible = true;
        _ringLook.SetShaderParameter("half_len", _halfLen);
        _ringLook.SetShaderParameter("half_wide", _halfWide);
        _ringLook.SetShaderParameter("span", span);
        // Out fast, then slowing - a ring spent by the water it pushes.
        _ringLook.SetShaderParameter("front", 6.0f + reach * (1.0f - (1.0f - a) * (1.0f - a)));
        _ringLook.SetShaderParameter("age", a);
        _ringLook.SetShaderParameter("seed", _seed * 1.7f);
    }

    private int HashSeed => _seed * 131;

    private float Hash(int k, int salt)
    {
        unchecked
        {
            uint h = (uint)(k * 374761393 + salt * 668265263 + HashSeed * 1274126177);
            h = (h ^ (h >> 13)) * 1274126177u;
            return ((h ^ (h >> 16)) & 0xFFFFFF) / 16777215.0f;
        }
    }

    /// <summary>
    /// A sheet: the jets as tapered capsules in the eye's plane, flowed into one
    /// shape by a smooth union (<c>blend</c> px), one ink line round it. A
    /// capsule's point on its axis <c>h</c> (0 foot, 1 tip) is what the tearing
    /// and the crest read: past <c>cut</c> along it, and a little before with a
    /// noise, the capsule is gone; over the top of what is left, white. Normal
    /// and depth are the capsule's round front, blended with the union's
    /// weights, so the model's ramp steps one lit side and one shaded over the
    /// whole sheet and the hull hides what is behind it.
    /// </summary>
    private static readonly Shader SheetShader = new()
    {
        Code = @"
shader_type spatial;
render_mode cull_disabled, specular_disabled, ambient_light_disabled, shadows_disabled;
uniform vec4 feet[" + Pool + @"];
uniform vec4 tips[" + Pool + @"];
uniform vec4 looks[" + Pool + @"];
uniform int count = 0;
uniform float blend = 7.0;
uniform vec3 body : source_color = vec3(0.8, 0.93, 0.95);
uniform vec3 crest : source_color = vec3(0.97, 1.0, 1.0);
uniform vec3 ink : source_color = vec3(0.08, 0.24, 0.26);
uniform float ink_width = 1.1;
uniform float ink_min_px = 1.0;
" + Toon.NoiseCode + Toon.RampCode + @"
void fragment() {
    vec2 p = VERTEX.xy;
    float sd = 1e9;
    float wsum = 0.0, zsum = 0.0, csum = 0.0;
    vec3 nsum = vec3(0.0);
    float px = 2.0 / (PROJECTION_MATRIX[1][1] * VIEWPORT_SIZE.y);
    float line = max(ink_width, ink_min_px * px);
    for (int i = 0; i < " + Pool + @"; i++) {
        if (i >= count) break;
        vec3 a = (VIEW_MATRIX * vec4(feet[i].xyz, 1.0)).xyz;
        vec3 b = (VIEW_MATRIX * vec4(tips[i].xyz, 1.0)).xyz;
        vec4 lk = looks[i];
        vec2 ab = b.xy - a.xy;
        float h = clamp(dot(p - a.xy, ab) / max(dot(ab, ab), 1e-4), 0.0, 1.0);
        // Torn from the tip down: past the cut along the axis, ragged by a
        // noise round the cut so the sheet's crown breaks into points.
        float rag = 0.22 * (noise3(vec3(p * 0.09, lk.x)) - 0.5);
        float gone = smoothstep(lk.y - 0.12, lk.y + 0.02, h + rag);
        float r = mix(feet[i].w, tips[i].w, h) * (1.0 - gone);
        if (r < 1.5 * line) continue;
        vec2 q = p - (a.xy + ab * h);
        float len = length(q);
        // Lumps along the rim, so a jet is water and not a tube.
        float bump = noise3(vec3(p * 0.07, lk.x + 3.0)) - 0.5;
        float di = len - r * (1.0 + 0.3 * bump);
        float front = sqrt(max(0.0, 1.0 - len * len / (r * r)));
        float w = exp(-clamp(di, -r, 3.0 * blend) / blend);
        nsum += normalize(vec3(q / r, max(front, 0.2))) * w;
        zsum += (mix(a.z, b.z, h) + r * front) * w;
        // The crest: the top of what is left of the jet, and the tip itself.
        float top = min(lk.y, 1.0);
        csum += step(top - 0.28, h) * w;
        wsum += w;
        float k = clamp(0.5 + 0.5 * (di - sd) / blend, 0.0, 1.0);
        sd = mix(di, sd, k) - blend * k * (1.0 - k);
    }
    if (sd > 0.0 || wsum <= 0.0) discard;
    vec4 clip = PROJECTION_MATRIX * vec4(p, zsum / wsum, 1.0);
    DEPTH = clip.z / clip.w * 0.5 + 0.5;
    if (sd > -line) {
        ALBEDO = vec3(0.0);
        EMISSION = ink;
    } else if (csum / wsum > 0.5) {
        ALBEDO = vec3(0.0);
        EMISSION = crest;
    } else {
        ALBEDO = body;
        EMISSION = body * shade;
        NORMAL = normalize(nsum);
    }
}
",
    };

    /// <summary>
    /// A ring of foam on the water (ratel's <c>level3d_ripple.gdshader</c>): a
    /// square two across about its middle, drawn only between <c>inner</c> and
    /// 1, its edges wandering by a noise round it as far as a share of its
    /// width; two flat bands, white foam outside where it breaks and the pale
    /// water behind. <c>inner</c> 0 is a patch. No ink, no light.
    /// </summary>
    private static readonly Shader RippleShader = new()
    {
        Code = @"
shader_type spatial;
render_mode unshaded, blend_mix, depth_draw_never, cull_disabled, shadows_disabled;
uniform vec3 foam : source_color = vec3(0.97, 1.0, 1.0);
uniform vec3 wash : source_color = vec3(0.62, 0.83, 0.87);
uniform float inner = 0.8;
uniform float seed = 0.0;
" + Toon.NoiseCode + @"
varying vec2 local;
void vertex() {
    local = VERTEX.xz;
}
void fragment() {
    float r = length(local);
    float band = 1.0 - inner;
    vec2 around = local / max(r, 1e-4);
    float d = r + (noise3(vec3(around * 2.5, seed)) - 0.5) * band * 0.8;
    if (d > 1.0 || d < inner) discard;
    ALBEDO = d > inner + band * 0.45 ? foam : wash;
    ALPHA = 1.0;
}
",
    };

    /// <summary>
    /// The ring: a band on the water between <c>front - width</c> and
    /// <c>front</c> of distance out from the hull's rounded rectangle, in the
    /// hull's frame (UV across and along), torn into patches by a noise that
    /// takes more of it as it ages; white inside, the pale water at its edge.
    /// Inside the band, the first moments, the water churned white against the
    /// hull. Hard edges, antialiased by the derivative.
    /// </summary>
    private static readonly Shader RingShader = new()
    {
        Code = @"
shader_type spatial;
render_mode unshaded, blend_mix, depth_draw_never, cull_disabled, shadows_disabled;
uniform vec3 foam : source_color = vec3(0.97, 1.0, 1.0);
uniform vec3 edge : source_color = vec3(0.8, 0.93, 0.95);
uniform float half_len = 70.0;
uniform float half_wide = 45.0;
uniform float span = 200.0;
uniform float front = 10.0;
uniform float age = 0.0;
uniform float seed = 0.0;
" + Toon.NoiseCode + @"
void fragment() {
    // The plane's UV in world px about the hull: x across, y along.
    vec2 g = (UV - 0.5) * 2.0 * span;
    float corner = min(half_wide, half_len) * 0.6;
    vec2 d = abs(vec2(g.x, g.y)) - vec2(half_wide, half_len) + corner;
    float dist = length(max(d, 0.0)) + min(max(d.x, d.y), 0.0) - corner;
    if (dist < -2.0) discard;
    // Wavy and uneven from the first frame: a band at an even distance all
    // round was a selection frame round the tank.
    float wob = noise3(vec3(g * 0.028, seed + 9.0)) - 0.5;
    dist += 14.0 * wob * (0.6 + age);
    float width = mix(12.0, 5.0, age) * (0.55 + 0.9 * noise3(vec3(g * 0.05, seed + 2.0)));
    float n = noise3(vec3(g * 0.045, seed)) * 0.65 + noise3(vec3(g * 0.11, seed + 5.0)) * 0.35;
    float band = (front - dist) / width;
    // Churned water against the hull, gone in the first half second.
    float churn = (1.0 - smoothstep(0.0, 0.35, age)) * step(dist, front - width);
    float fw = max(fwidth(dist) / width, 1e-3);
    float in_band = smoothstep(0.0, fw, band) * (1.0 - smoothstep(1.0 - fw, 1.0, band));
    float keep = smoothstep(0.02, 0.0, (0.3 + 0.4 * age * age) - n);
    float cover = max(in_band, churn * step(0.6, n)) * keep;
    if (cover <= 0.01) discard;
    float core = step(0.35, band) * step(band, 0.85);
    ALBEDO = mix(edge, foam, max(core, churn));
    ALPHA = cover * (1.0 - smoothstep(0.8, 1.0, age));
}
",
    };
}
