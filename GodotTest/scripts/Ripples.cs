using System;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The pond's surface as a thing that is advanced rather than authored: a damped
/// wave field on a grid, pushed by whatever drives through it.
///
/// <b>What it buys is the one thing the hand-authored answers cannot do, which is
/// propagation.</b> <see cref="Swell"/> says what a hexagon is doing and
/// <see cref="Wake"/> lays stamps along a path; both are right about where the
/// tank was and silent about what the water does next. Rings that spread, reflect
/// off a bank and interfere between two tanks are not a finer version of either -
/// they are a different kind of statement, and the only way to make it is to keep
/// state on the water itself and step it.
///
/// <b>The datum stays the authority, and that is the whole of how this is allowed
/// to exist.</b> The board's rule is that one body of water is one surface, and
/// every reader that decides anything - the per-column cut on a hull, the pond's
/// side wall, the ring on the ground, whether a tank is wading, the speed
/// ceiling - reads <see cref="HexField.WaterTop"/> and must go on reading it. So
/// this field is a displacement <i>about</i> that plane and is read by exactly
/// three things, all of them ways of drawing: the surface's normals, its foam,
/// and how far the water runs up a beach. Nothing that decides is allowed to ask.
/// The mesh does not move either, for the same reason and for a second one: a
/// vertex wave puts the pond at two heights, which is the failure the water's own
/// guard refuses by name.
///
/// <b>On the CPU, and that is a testability decision before it is a performance
/// one.</b> A ping-pong of viewports is the obvious build and it would be faster;
/// it would also be a simulation nothing outside the renderer can ask a question
/// of. Here the whole interesting half - that a ring spreads at the speed it was
/// told to, that land reflects it, that it dies, that two runs agree bit for bit -
/// is asserted with no board, no art and no frame drawn, which is the same reason
/// <see cref="Swell"/> takes a cell and a number instead of a tank.
///
/// <b>The grid is square on screen, not square on the water.</b> This camera
/// foreshortens z by <see cref="Stage3D.Squash"/>, so a texel as wide in world z
/// as it is in world x is half a texel on screen: the resolution is spent where
/// nothing can be seen. Stretching it by the squash halves the cell count for no
/// visible loss - and it costs only that the wave step weights its two axes
/// apart, which is arithmetic and not an approximation.
/// </summary>
public sealed class Ripples
{
    /// <summary>Whether the water is stepped at all. Off flattens the field and
    /// stops the texture being refreshed, so what the surface samples is zeroes
    /// and every reader of it is a no-op - which is the pond as it was before
    /// this, and the A/B it is judged by. See --no-ripples.</summary>
    public bool Enabled = true;

    /// <summary>How wide a texel is on screen, in the harness's own px. Seven
    /// because that is a feature every few pixels of a field whose whole visible
    /// output is a normal and a soft foam threshold - the ash map's grain is eight
    /// on the same argument - and because a hull is 183px broadside, which is
    /// twenty-six texels of bow to spend on a bow wave.</summary>
    public const float Grain = 7.0f;

    /// <summary>How many texels the field may hold, whatever the board. A cap
    /// because this is stepped on the CPU every frame, and a named one because
    /// the failure it prevents is a stutter rather than a wrong picture: at this
    /// grain the twelve-by-twelve pond is about 119k, so the cap leaves room for
    /// a board two thirds again as large before the grain has to give.</summary>
    public const int MaxTexels = 200_000;

    /// <summary>
    /// How fast a ripple travels, in world units a second.
    ///
    /// <b>Chosen off the board and not off the physics, and the physics is worth
    /// writing down so the choice is visible.</b> Shallow-water gravity waves go
    /// <c>sqrt(g*d)</c>, and this pond is 0.55 of a level deep - 29.5 world units,
    /// which at the board's own 4.07m per cell radius is 0.97m, so 3.1 m/s or 94
    /// units a second. That is right and slow: a ring would take two and a half
    /// seconds to cross a cell, by which time the tank that made it is two cells
    /// away. This is the number that crosses one cell width a second, which is
    /// what a ring has to do to read as one thing spreading rather than as a stain
    /// appearing.
    ///
    /// <b>It is invariant to the grain by construction</b> - the step's coupling
    /// is <c>(c*dt/dx)^2</c>, so a coarser grid gets a larger coupling and the
    /// same speed - which is what keeps a bigger board from quietly having slower
    /// water.
    /// </summary>
    public const float Speed = 240.0f;

    /// <summary>How long a ripple takes to lose half its height, in seconds.
    /// Long enough that a crossing is still visible on the far bank, short enough
    /// that a pond somebody drove through a minute ago is calm again.</summary>
    public const float Fade = 1.6f;

    /// <summary>
    /// The largest coupling the step is allowed, and it is a stability bound
    /// rather than a taste.
    ///
    /// The five-point stencil is stable while <c>c^2 dt^2 (1/dx^2 + 1/dz^2)</c> is
    /// under a half; past it the field does not wobble, it detonates - amplitude
    /// doubling every step, which on a texture reads as the pond turning into
    /// white noise in a quarter of a second. Held under with a margin, and
    /// <b>clamped rather than trusted</b>: the grain is derived from the board, so
    /// a board that shrinks it far enough would otherwise blow the field up on
    /// arrival. Clamping costs speed instead, and <see cref="Slowed"/> says so.
    /// </summary>
    public const float Bound = 0.45f;

    /// <summary>
    /// How hard a hull pushes the water, in world units of height a second at
    /// full speed.
    ///
    /// <b>A dipole and not a splash, which is what makes a bow wave a bow wave.</b>
    /// A hull under way piles water in front of itself and leaves a trough behind:
    /// the same displaced volume, twice, with a sign. Pushed that way the field
    /// makes the crest ahead, the hollow behind and the two diverging arms between
    /// them on its own - and it makes them out of a moving source, which is the
    /// only way to get arms that trail at the right angle without drawing a V.
    ///
    /// A rate rather than an impulse, because a frame is not a unit of anything:
    /// what a hull does to water in a second is the same whether the frame took a
    /// sixtieth of it or a thirtieth.
    /// </summary>
    public const float Push = 5.6f;

    /// <summary>
    /// A multiplier over <see cref="Push"/>, and over that alone.
    ///
    /// <b>The one dial the field has, and it is deliberate that there is only
    /// one.</b> Everything else about the shape of what a hull does to water is an
    /// aspect ratio - how far the lobes straddle it, how wide they are, how fast
    /// the answer travels, how long it lives - and those are what make a bow wave
    /// look like a bow wave rather than a ring. Scale them and it is a different
    /// wave; scale the forcing and it is the same wave, harder. So the slider moves
    /// the forcing, and the pair of numbers that decide the shape stays where it
    /// was measured - the argument the tremble level and the class size dial both
    /// make.
    /// </summary>
    public float Level = 1.0f;

    /// <summary>How far ahead of the contact point the crest lobe sits, and the
    /// hollow behind it, in world units. Half a hull: the pair has to straddle the
    /// tank or it is one lump at its middle, and a hull is about 183 units across.
    /// </summary>
    public const float Reach = 90.0f;

    /// <summary>How wide each lobe is, in world units. Sized against the hull it
    /// belongs to rather than against the grid: narrower than this and the push is
    /// a point source, which radiates a ring and never a bow.</summary>
    public const float Spread = 70.0f;

    /// <summary>The step the field is advanced by, in seconds. A fixed step and
    /// not the frame's own, because this is the one thing on the board that
    /// carries state forward: a variable step changes the coupling, and a
    /// simulation whose wave speed depends on how long the frame took is a
    /// simulation two captures of cannot be compared.</summary>
    public const float Step = 1.0f / 60.0f;

    /// <summary>How many steps one frame may run, however long it was. The
    /// wildfire's rule in the same words and for the same reason: a sixty-second
    /// frame must not advance the water by an hour, so a long frame loses time
    /// rather than freezing. Four is two frames of slack at 30fps.</summary>
    public const int MaxSteps = 4;

    private float[] _now = Array.Empty<float>();
    private float[] _was = Array.Empty<float>();
    private float[] _push = Array.Empty<float>();
    private bool[] _wet = Array.Empty<bool>();
    private byte[] _bytes = Array.Empty<byte>();
    private Image? _image;
    private float _clock;
    private long _stamp = long.MinValue;

    /// <summary>How many texels across and down the field is, or zero where there
    /// is no water to hold one.</summary>
    public int Wide { get; private set; }

    /// <summary>How many texels down the field is - see <see cref="Wide"/>.
    /// </summary>
    public int Tall { get; private set; }

    /// <summary>The world-XZ box the field covers: where its first texel's corner
    /// is, and how far it reaches. What the surface needs to turn a fragment into
    /// a lookup, and what a test needs to put a push in a known place.</summary>
    public Rect2 Box { get; private set; }

    /// <summary>How many world units one texel spans, x and z apart - see the
    /// class note on being square on screen rather than on the water.</summary>
    public Vector2 Cell { get; private set; } = Vector2.One;

    /// <summary>The coupling the last step ran with, and whether
    /// <see cref="Bound"/> had to take it down. Reported because a field that is
    /// quietly slower than <see cref="Speed"/> asked for looks exactly like a
    /// field that is working.</summary>
    public float Coupling { get; private set; }

    /// <summary>Whether the bound clamped the coupling - see
    /// <see cref="Coupling"/>.</summary>
    public bool Slowed { get; private set; }

    /// <summary>How many steps the last <see cref="Tick"/> ran. A readout, and the
    /// one that says whether a long frame was capped.</summary>
    public int Steps { get; private set; }

    /// <summary>The texture the surface samples, or null while there is no field.
    /// One channel of float: eight bits would band in the derivative, and the
    /// derivative is what is actually looked at.</summary>
    public ImageTexture? Sheet { get; private set; }

    /// <summary>The tallest displacement anywhere on the field, in world units.
    /// A readout: a field that is being stepped and never pushed and a field that
    /// is not being stepped at all are the same flat water.</summary>
    public float Peak
    {
        get
        {
            float top = 0.0f;
            foreach (float v in _now)
                top = Mathf.Max(top, Mathf.Abs(v));
            return top;
        }
    }

    /// <summary>How much displacement there is in total, in world units. What says
    /// a ring is still travelling rather than still standing where it was made.
    /// </summary>
    public float Energy
    {
        get
        {
            float sum = 0.0f;
            foreach (float v in _now)
                sum += Mathf.Abs(v);
            return sum;
        }
    }

    /// <summary>
    /// Lay a field over this much water, at this camera's squash, with this test
    /// for which of it is wet.
    ///
    /// <b>The squash is here because the grid is square on screen</b> - see the
    /// class note - and the wet test because land is a wall: a texel that is not
    /// water is left out of the neighbour sum, which is a reflecting boundary and
    /// is what makes a ring come back off a bank instead of leaking through it.
    ///
    /// <b>The same pond keeps its field, and that is not an optimisation.</b> The
    /// caller is whatever rebuilds the board's meshes, and a board is rebuilt for
    /// reasons that have nothing to do with the water - so re-laying on every call
    /// empties the pond at moments nothing on screen can account for. Measured
    /// before it was believed: the ripples a crossing had made vanished, to the
    /// pixel, on one frame near the end of the order, and stayed gone - which reads
    /// as a simulation that forgets rather than as a board that was rebuilt.
    /// <see cref="Swell"/> guards its own arrays the same way, on the same
    /// argument.
    ///
    /// <b>Told apart by geometry and by a stamp, because geometry alone is not
    /// enough.</b> The box is derived from the flooded cells, so draining one in
    /// the middle of a pond leaves it identical while the wet mask it implies has a
    /// hole in it - and a stale mask is a wall the water cannot see. The stamp is
    /// the caller's own signature of which cells are wet, which it has to hand
    /// anyway; it costs nothing and it closes that case exactly.
    /// </summary>
    public void Fit(Rect2 box, float squash, long stamp, Func<Vector2, bool> wet)
    {
        float dx = Grain;
        float dz = Grain / Mathf.Max(squash, 0.0001f);
        int w = Mathf.Max(1, Mathf.CeilToInt(box.Size.X / dx));
        int d = Mathf.Max(1, Mathf.CeilToInt(box.Size.Y / dz));
        // Coarsened together rather than clipped, so the field still covers the
        // whole pond: a cap that cropped it would leave the far end of a big board
        // with water that never answers.
        //
        // <b>And coarsened until it fits, rather than once.</b> The square root of
        // the overshoot is the right factor for a continuous grid and both axes
        // round up afterwards, so one pass lands just over the cap as often as
        // just under - measured: a 20000 by 40000 pond came back at 200704 against
        // a cap of 200000, which is a cap that does not hold.
        bool coarse = w * d > MaxTexels;
        while (w * d > MaxTexels)
        {
            float grow = Mathf.Max(1.01f,
                                   Mathf.Sqrt((float)(w * d) / MaxTexels));
            dx *= grow;
            dz *= grow;
            w = Mathf.Max(1, Mathf.CeilToInt(box.Size.X / dx));
            d = Mathf.Max(1, Mathf.CeilToInt(box.Size.Y / dz));
        }
        // Said out loud but not as a warning, and the difference is what the cap
        // costs. The flooded-cell cap breaks the picture - cells past it share a
        // state and churn when it does - so it pushes a warning; this one only
        // coarsens, and the field still covers every drop of the pond. A warning
        // that fires on a board working as designed is a warning that stops being
        // read.
        if (coarse)
            GD.Print($"ripples: {box.Size.X:0} x {box.Size.Y:0} of water is past "
                     + $"the {MaxTexels} texels the field holds, so its grain is "
                     + $"{dx:0.0} rather than {Grain:0.0}");
        var laid = new Rect2(box.Position, new Vector2(w * dx, d * dz));
        if (Wide == w && Tall == d && Box == laid && stamp == _stamp)
            return;
        Wide = w;
        Tall = d;
        _stamp = stamp;
        Cell = new Vector2(dx, dz);
        Box = laid;
        int n = w * d;
        _now = new float[n];
        _was = new float[n];
        _push = new float[n];
        _wet = new bool[n];
        _bytes = new byte[n * 4];
        for (int j = 0; j < d; j++)
        for (int i = 0; i < w; i++)
            _wet[j * w + i] = wet(Middle(i, j));
        _image = Image.CreateFromData(w, d, false, Image.Format.Rf, _bytes);
        Sheet = ImageTexture.CreateFromImage(_image);
        _clock = 0.0f;
        Coupling = 0.0f;
        Slowed = false;
        Steps = 0;
    }

    /// <summary>Take the field off the board, so nothing is laid over the water at
    /// all. What a board with no pond in it holds.</summary>
    public void Drain()
    {
        Wide = 0;
        Tall = 0;
        _stamp = long.MinValue;
        _now = Array.Empty<float>();
        _was = Array.Empty<float>();
        _push = Array.Empty<float>();
        _wet = Array.Empty<bool>();
        _bytes = Array.Empty<byte>();
        _image = null;
        Sheet = null;
    }

    /// <summary>Where the middle of one texel is, in world XZ.</summary>
    public Vector2 Middle(int i, int j) =>
        Box.Position + new Vector2((i + 0.5f) * Cell.X, (j + 0.5f) * Cell.Y);

    /// <summary>Put the water back to flat. A field left standing would come back
    /// as a pond somebody had driven through.</summary>
    public void Settle()
    {
        Array.Clear(_now);
        Array.Clear(_was);
        Array.Clear(_push);
        _clock = 0.0f;
        Bake();
    }

    /// <summary>
    /// Say that something is driving through the water here, this way, this hard.
    ///
    /// <b>A point, a direction and a number, not a Vehicle</b> - the argument
    /// <see cref="Swell.Note"/> makes, and it buys the same thing: the dipole can
    /// be asserted without building a tank. World XZ, because a ring is round on
    /// the water and the tanks' own space is the one the camera already squashed.
    ///
    /// Accumulated rather than applied, so that every tank has had its say before
    /// anything is stepped - and so that two tanks in one place add up, which is
    /// interference and is half of what a field is for.
    /// </summary>
    public void Note(Vector2 at, Vector2 run, float push)
    {
        if (!Enabled || Wide == 0 || push <= 0.0f || run.LengthSquared() <= 0.0f)
            return;
        Vector2 way = run.Normalized();
        Lobe(at + way * Reach, +push * Push * Level);
        Lobe(at - way * Reach, -push * Push * Level);
    }

    /// <summary>One half of the pair: a smooth blob of forcing, in height a
    /// second. Skips dry texels, so a hull half out of the water pushes only the
    /// half of it that is in.</summary>
    private void Lobe(Vector2 at, float rate)
    {
        int i0 = Mathf.FloorToInt((at.X - Spread - Box.Position.X) / Cell.X);
        int i1 = Mathf.CeilToInt((at.X + Spread - Box.Position.X) / Cell.X);
        int j0 = Mathf.FloorToInt((at.Y - Spread - Box.Position.Y) / Cell.Y);
        int j1 = Mathf.CeilToInt((at.Y + Spread - Box.Position.Y) / Cell.Y);
        for (int j = Mathf.Max(j0, 0); j <= Mathf.Min(j1, Tall - 1); j++)
        for (int i = Mathf.Max(i0, 0); i <= Mathf.Min(i1, Wide - 1); i++)
        {
            int k = j * Wide + i;
            if (!_wet[k])
                continue;
            float r = Middle(i, j).DistanceTo(at) / Spread;
            if (r >= 1.0f)
                continue;
            // Cosine rather than a step, because the field differentiates what it
            // is given: a forcing with a hard rim puts a crease in the surface at
            // its own boundary, and then the eye reads the shape of the source.
            _push[k] += rate * (0.5f + 0.5f * Mathf.Cos(r * Mathf.Pi));
        }
    }

    /// <summary>
    /// Advance the field. Called once after every tank has been noted, for
    /// <see cref="Swell.Tick"/>'s reason: what the water is being asked to do is
    /// not known until they all have.
    /// </summary>
    public void Tick(double delta)
    {
        Steps = 0;
        if (Wide == 0)
            return;
        if (!Enabled)
        {
            if (Peak > 0.0f)
                Settle();
            Array.Clear(_push);
            return;
        }
        _clock += (float)delta;
        while (_clock >= Step && Steps < MaxSteps)
        {
            _clock -= Step;
            Steps++;
            Advance();
        }
        // Whatever is left past the cap is dropped rather than carried, so a frame
        // that took a second does not spend the next four catching up: the water
        // is late, which is what a dropped frame looks like everywhere else.
        if (Steps >= MaxSteps)
            _clock = 0.0f;
        Array.Clear(_push);
        if (Steps > 0)
            Bake();
    }

    /// <summary>One step of the damped wave equation, weighted per axis.</summary>
    private void Advance()
    {
        float cdt = Speed * Step;
        float kx = cdt * cdt / (Cell.X * Cell.X);
        float kz = cdt * cdt / (Cell.Y * Cell.Y);
        float over = (kx + kz) / Bound;
        if (over > 1.0f)
        {
            kx /= over;
            kz /= over;
        }
        Coupling = kx + kz;
        Slowed = over > 1.0f;
        // Per step, off the half-life: a field faded per frame would fade at the
        // frame rate.
        //
        // <b>Twice the exponent, because this is a two-step recurrence and the
        // factor is not the envelope.</b> Damping the whole right-hand side makes
        // the amplification factor satisfy <c>A^2 - damp*(2-2S)*A + damp = 0</c>,
        // so <c>|A|^2 = damp</c> and one step costs the wave <c>sqrt(damp)</c>, not
        // damp. Written the obvious way the field lasts twice as long as the
        // constant says - measured: three half-lives came back at 30% of the height
        // instead of an eighth, and five at 27%, which reads as water that does not
        // forget rather than as a half-life meaning half of what it claims.
        float damp = Mathf.Pow(0.5f, 2.0f * Step / Mathf.Max(Fade, 1e-4f));
        float[] now = _now;
        float[] was = _was;
        for (int j = 0; j < Tall; j++)
        {
            int row = j * Wide;
            for (int i = 0; i < Wide; i++)
            {
                int k = row + i;
                if (!_wet[k])
                {
                    was[k] = 0.0f;
                    continue;
                }
                float h = now[k];
                // A dry neighbour contributes the middle's own value, which is the
                // same as leaving it out: no slope across the boundary, so the
                // wave turns round rather than draining into the bank.
                float sx = (i > 0 && _wet[k - 1] ? now[k - 1] : h)
                         + (i + 1 < Wide && _wet[k + 1] ? now[k + 1] : h)
                         - 2.0f * h;
                float sz = (j > 0 && _wet[k - Wide] ? now[k - Wide] : h)
                         + (j + 1 < Tall && _wet[k + Wide] ? now[k + Wide] : h)
                         - 2.0f * h;
                was[k] = (2.0f * h - was[k] + kx * sx + kz * sz) * damp
                         + _push[k] * Step;
            }
        }
        // Swapped rather than copied: what was the previous field is where the
        // next one was written, which is the whole of why there are two.
        _now = was;
        _was = now;
    }

    /// <summary>The displacement at a point in world XZ, in world units, sampled
    /// the way the surface samples it. Zero outside the field, which is what makes
    /// every reader of it a no-op on a board with no water.</summary>
    public float Height(Vector2 at)
    {
        if (Wide == 0)
            return 0.0f;
        float u = (at.X - Box.Position.X) / Cell.X - 0.5f;
        float v = (at.Y - Box.Position.Y) / Cell.Y - 0.5f;
        int i = Mathf.FloorToInt(u);
        int j = Mathf.FloorToInt(v);
        float fx = u - i;
        float fz = v - j;
        return Mathf.Lerp(Mathf.Lerp(At(i, j), At(i + 1, j), fx),
                          Mathf.Lerp(At(i, j + 1), At(i + 1, j + 1), fx), fz);
    }

    private float At(int i, int j) =>
        i < 0 || j < 0 || i >= Wide || j >= Tall ? 0.0f : _now[j * Wide + i];

    /// <summary>Whether one texel is water, for the boundary's own check.</summary>
    public bool Wet(int i, int j) =>
        i >= 0 && j >= 0 && i < Wide && j < Tall && _wet[j * Wide + i];

    /// <summary>Put the field in its texture. Float, so the surface can difference
    /// it: a byte per texel bands the slope into steps, and the slope is what the
    /// normals are made of.</summary>
    private void Bake()
    {
        if (_image is null || Sheet is null)
            return;
        Buffer.BlockCopy(_now, 0, _bytes, 0, _bytes.Length);
        _image.SetData(Wide, Tall, false, Image.Format.Rf, _bytes);
        Sheet.Update(_image);
    }
}
