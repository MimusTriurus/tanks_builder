using Godot;

namespace TankSpriteTest;

/// <summary>
/// A unit's state over its hull in a match (2026-10-28, GDD states.md «Семь
/// состояний»): the HUD's row of sides (▲ ◐ ✕) said on the board itself, in
/// the blast preview's hand (<c>Tank3DBench.BlastPlan</c>). Active is the
/// norm and wears nothing; a destroyed hull is its wreck and wears nothing
/// either.
/// <list type="bullet">
/// <item><b>Горит</b> - the flame. On its owner's turn it beats: put it out,
/// or it burns at the end of the turn.</item>
/// <item><b>Подбит</b> - a cracked shield (<see cref="Shield"/>), the sign
/// the shot's forecast is to take for "it will pierce".</item>
/// <item><b>Утоплен</b> - knocked out in deep water: the circle with water
/// over its lower half.</item>
/// <item><b>Плывёт</b> - alive in deep water, on the gear: a lifebuoy.</item>
/// </list>
/// A hull the blast preview marks wears its mark instead.
/// </summary>
public sealed partial class Tank3DBench
{
    private enum Badge { None, Burning, Knocked, Drowned, Afloat }

    private static readonly Color BadgeKnocked = new(0.88f, 0.88f, 0.85f);
    private static readonly Color BadgeWater = new(0.36f, 0.64f, 0.95f);
    private static readonly Color BadgeBuoy = new(0.95f, 0.36f, 0.20f);
    private static readonly Color BadgeToken = new(0.10f, 0.11f, 0.12f, 0.82f);

    /// <summary>The flame's beat on its owner's turn, a second.</summary>
    private const float BadgeBeat = 1.1f;

    private float _badgeClock;
    private readonly Vector2[] _badgeWet = new Vector2[18];

    /// <summary>Over the board, under the previews: each unit's state.</summary>
    private void DrawBadges(Control ink)
    {
        if (!Match || _field is null)
            return;
        _badgeClock += (float)GetProcessDeltaTime();
        float r = Mathf.Max(8.0f, HexScreen() * 0.11f);
        if (_fate != Fate.Destroyed)
            BadgeOn(ink, _model, _rig, _side, _modelTag, _fate, CellHere, r);
        if (_other is { Unit: true } o)
            BadgeOn(ink, o.Model, o.Rig, o.Side, o.Tag, o.Fate, o.Cell, r);
        for (int i = 0; i < _parked.Count; i++)
        {
            Parked p = _parked[i];
            BadgeOn(ink, p.Model, p.Rig, p.Side, p.Tag, p.Fate, p.Cell, r);
        }
    }

    private void BadgeOn(Control ink, TankModel model, Node3D rig, Side side, string tag, Fate fate, Vector2I cell, float r)
    {
        Badge badge = BadgeOf(side, tag, fate, cell);
        if (badge == Badge.None || BlastMarked(model) || ShotMarked(model))
            return;
        Vector2 at = Over(model, rig);
        // the drowned on the bed: over the water, not over the hull under it,
        // which would put the sign on whoever stands in front
        if (badge == Badge.Drowned)
        {
            Vector3 p = rig.GlobalPosition;
            p.Y = Mathf.Max(p.Y, _field!.WaterTop(cell) / RiseFactor);
            at = _camera.UnprojectPosition(p + Vector3.Up * (model.Size.Y * model.PixelsPerUnit * 1.15f));
        }
        // A fire beats on its owner's turn - it is the owner's to answer; with
        // the end of the turn under the pointer it is crossed out: it burns
        // as the turn is passed (GDD states.md «Горит»).
        bool mine = badge == Badge.Burning && side == _turn;
        float beat = mine ? 0.5f - 0.5f * Mathf.Cos(Mathf.Tau * _badgeClock / BadgeBeat) : 0.0f;
        Sign(ink, badge, at, r, beat);
        if (mine && PassUnder)
            Doomed(ink, at, r);
    }

    /// <summary>
    /// The sign on its token, dark with a pale rim: it reads over the hull's
    /// own fire and smoke, which a bare flame drowned in. <paramref name="beat"/>
    /// swells the token and reddens the rim, 0 to 1; <paramref name="a"/>
    /// fades it all - a forecast coming up (the ram's).
    /// </summary>
    private void Sign(Control ink, Badge badge, Vector2 at, float r, float beat = 0.0f, float a = 1.0f)
    {
        float token = r * (1.0f + 0.12f * beat);
        ink.DrawCircle(at, token + 1.5f, new Color(0.0f, 0.0f, 0.0f, 0.45f * a));
        ink.DrawCircle(at, token, new Color(BadgeToken, BadgeToken.A * a));
        ink.DrawArc(at, token, 0.0f, Mathf.Tau, 32, new Color(BadgeKnocked.Lerp(BlastFire, beat), a), 1.5f + 1.0f * beat, true);
        float icon = r * 0.68f;
        switch (badge)
        {
            case Badge.Burning:
                Flame(ink, at + new Vector2(0.0f, icon * 0.2f), icon * 0.8f, BlastFire, a);
                break;
            case Badge.Knocked:
                Shield(ink, at, icon, BadgeKnocked, a);
                break;
            case Badge.Drowned:
                Drowned(ink, at, icon, a);
                break;
            case Badge.Afloat:
                Buoy(ink, at, icon, a);
                break;
        }
    }

    /// <summary>A red cross over a token: it will be gone.</summary>
    private static void Doomed(Control ink, Vector2 at, float r)
    {
        float x = r * 0.95f;
        foreach (bool under in Both)
        {
            Color c = under ? new Color(0.0f, 0.0f, 0.0f, 0.6f) : AimPierce;
            float w = under ? 6.0f : 3.0f;
            ink.DrawLine(at - new Vector2(x, x), at + new Vector2(x, x), c, w, true);
            ink.DrawLine(at - new Vector2(x, -x), at + new Vector2(x, -x), c, w, true);
        }
    }

    private static readonly bool[] Both = { true, false };

    private Badge BadgeOf(Side side, string tag, Fate fate, Vector2I cell)
    {
        bool deep = _field!.InBounds(cell) && _field.IsDeep(cell) && _field.RampHeading(cell) < 0;
        return fate switch
        {
            Fate.Knocked => deep ? Badge.Drowned : Badge.Knocked,
            Fate.Alive when Burning(side, tag) => Badge.Burning,
            Fate.Alive when deep => Badge.Afloat,
            _ => Badge.None,
        };
    }

    /// <summary>The blast preview has its own sign over this hull.</summary>
    private bool BlastMarked(TankModel model)
    {
        if (_blastAt is null)
            return false;
        if (_other is { } o && ReferenceEquals(o.Model, model))
            return true;
        for (int i = 0; i < _blastMarks.Count; i++)
            if (ReferenceEquals(_blastMarks[i].Model, model) && _blastMarks[i].Hit is BlastHit.Fire or BlastHit.Chain)
                return true;
        return false;
    }

    /// <summary>The shot's forecast stands over this hull (<see cref="DrawShotSign"/>).</summary>
    private bool ShotMarked(TankModel model) =>
        _judged is { Fires: true } shot && _other is { Fate: Fate.Alive } o && _blastAt is null
        && (ReferenceEquals(o.Model, model)
            || (shot.Onward is { Ends: LegEnd.Tank, Hull: { } hull } && ReferenceEquals(hull.Model, model)));

    // The shield, unit size, y down: its outline, and the two halves the crack
    // splits it into - the crack a zigzag from the top edge to the point.
    private static readonly Vector2[] ShieldWhole =
    {
        new(-0.78f, -0.82f), new(0.78f, -0.82f), new(0.78f, 0.0f), new(0.72f, 0.35f), new(0.5f, 0.65f),
        new(0.25f, 0.85f), new(0.0f, 0.95f), new(-0.25f, 0.85f), new(-0.5f, 0.65f), new(-0.72f, 0.35f),
        new(-0.78f, 0.0f),
    };
    private static readonly Vector2[] ShieldLeft =
    {
        new(-0.78f, -0.82f), new(0.04f, -0.82f), new(-0.12f, -0.38f), new(0.08f, 0.02f), new(-0.08f, 0.42f),
        new(0.0f, 0.95f), new(-0.25f, 0.85f), new(-0.5f, 0.65f), new(-0.72f, 0.35f), new(-0.78f, 0.0f),
    };
    private static readonly Vector2[] ShieldRight =
    {
        new(0.04f, -0.82f), new(0.78f, -0.82f), new(0.78f, 0.0f), new(0.72f, 0.35f), new(0.5f, 0.65f),
        new(0.25f, 0.85f), new(0.0f, 0.95f), new(-0.08f, 0.42f), new(0.08f, 0.02f), new(-0.12f, -0.38f),
    };
    private readonly Vector2[] _shieldWhole = new Vector2[ShieldWhole.Length];
    private readonly Vector2[] _shieldLeft = new Vector2[ShieldLeft.Length];
    private readonly Vector2[] _shieldRight = new Vector2[ShieldRight.Length];

    /// <summary>
    /// A cracked shield: the armour gone through. The knocked-out unit's sign,
    /// and the one to say "it will pierce" before a shot - hence its own
    /// colour and fade. The halves sit apart on a dark silhouette, the crack
    /// between them dark. Not <paramref name="cracked"/> - the armour holds.
    /// </summary>
    private void Shield(Control ink, Vector2 at, float r, Color c, float a = 1.0f, bool cracked = true)
    {
        for (int i = 0; i < ShieldWhole.Length; i++)
            _shieldWhole[i] = at + ShieldWhole[i] * (r * 1.12f) + new Vector2(0.0f, r * 0.02f);
        ink.DrawColoredPolygon(_shieldWhole, new Color(0.0f, 0.0f, 0.0f, 0.6f * a));
        if (!cracked)
        {
            for (int i = 0; i < ShieldWhole.Length; i++)
                _shieldWhole[i] = at + ShieldWhole[i] * r;
            ink.DrawColoredPolygon(_shieldWhole, new Color(c, c.A * a));
            return;
        }
        // the halves apart and tipped a little off the crack
        Vector2 apart = new(r * 0.07f, 0.0f);
        for (int i = 0; i < ShieldLeft.Length; i++)
            _shieldLeft[i] = at - apart + ShieldLeft[i].Rotated(-0.06f) * r;
        for (int i = 0; i < ShieldRight.Length; i++)
            _shieldRight[i] = at + apart + ShieldRight[i].Rotated(0.06f) * r;
        var fill = new Color(c, c.A * a);
        ink.DrawColoredPolygon(_shieldLeft, fill);
        ink.DrawColoredPolygon(_shieldRight, fill);
    }

    /// <summary>A ring with water over its lower half, the water's top a wave.</summary>
    private void Drowned(Control ink, Vector2 at, float r, float alpha = 1.0f)
    {
        const int wave = 7;
        int n = _badgeWet.Length;
        float rim = r * 0.86f;
        float top = -r * 0.05f;
        // the wave's ends on the rim and still, so the outline does not cross itself
        float end = Mathf.Sqrt(rim * rim - top * top);
        for (int i = 0; i < wave; i++)
        {
            float k = Mathf.Lerp(-1.0f, 1.0f, i / (float)(wave - 1));
            _badgeWet[i] = at + new Vector2(k * end, top + Mathf.Sin(k * Mathf.Pi) * (1.0f - k * k) * r * 0.25f);
        }
        // and back round the bottom, right to left, the rim's ends left to the wave
        float from = Mathf.Asin(top / rim);
        int arc = n - wave;
        for (int i = 0; i < arc; i++)
        {
            float a = Mathf.Lerp(from, Mathf.Pi - from, (i + 1) / (float)(arc + 1));
            _badgeWet[wave + i] = at + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * rim;
        }
        ink.DrawColoredPolygon(_badgeWet, new Color(BadgeWater, alpha));
        ink.DrawArc(at, r * 0.92f, 0.0f, Mathf.Tau, 28, new Color(BadgeKnocked, alpha), 2.0f, true);
    }

    /// <summary>A lifebuoy: a thick ring, red and white by quarters.</summary>
    private static void Buoy(Control ink, Vector2 at, float r, float alpha = 1.0f)
    {
        float w = r * 0.38f;
        float mid = r - w * 0.5f;
        for (int q = 0; q < 4; q++)
        {
            float a = Mathf.Pi * 0.25f + Mathf.Pi * 0.5f * q;
            ink.DrawArc(at, mid, a, a + Mathf.Pi * 0.5f, 8, new Color(q % 2 == 0 ? BadgeBuoy : BadgeKnocked, alpha), w, true);
        }
    }
}

/// <summary>
/// The forecasts (2026-10-28): the same signs, put up before an order on the
/// one it will befall - over the shot's target what the round does to it,
/// over the ram's target what the water does, and the end of the turn under
/// the pointer crosses out the own fires (<see cref="Doomed"/>). Each comes up
/// once, over <see cref="BlastRise"/>, and stays while the order is shown -
/// no loop, no blinking.
/// </summary>
public sealed partial class Tank3DBench
{
    private float _shotSignUp, _ramSignUp;
    private readonly Vector2[] _bounce = new Vector2[3];

    /// <summary>Over the shot's target: a cracked shield - it pierces, a whole
    /// one - it holds, a whole one with a round bounced off - a ricochet. And,
    /// smaller, over the second tank the round goes on to.</summary>
    private void DrawShotSign(Control ink)
    {
        if (_judged is not { Fires: true } shot || _other is not { Fate: Fate.Alive } o || _blastAt is not null)
        {
            _shotSignUp = 0.0f;
            return;
        }
        float up = SignUp(ref _shotSignUp);
        float r = Mathf.Max(10.0f, HexScreen() * 0.16f) * Mathf.Lerp(0.75f, 1.0f, up);
        ShotSign(ink, Over(o.Model, o.Rig), r, shot.Pierces, shot.Glances, up);
        if (shot.Onward is { Ends: LegEnd.Tank, Hull: { } hull } leg)
            ShotSign(ink, Over(hull.Model, hull.Rig), r * 0.75f, leg.Pierces, false, up);
    }

    private void ShotSign(Control ink, Vector2 at, float r, bool pierces, bool glances, float a)
    {
        if (pierces)
        {
            Shield(ink, at, r * 0.8f, AimPierce, a);
            return;
        }
        Shield(ink, at, r * 0.8f, glances ? AimGlance : AimHeld, a, cracked: false);
        if (!glances)
            return;
        // the round across the shield's face: in, off it at a right angle and
        // away - pale on a dark edge, over the shield, so it does not melt
        // into its yellow
        Vector2 hit = at + new Vector2(0.0f, 0.25f) * r;
        Vector2 tip = hit + new Vector2(0.8f, -0.8f) * r;
        Vector2 way = new Vector2(1.0f, -1.0f).Normalized();
        // the head a filled triangle and the shaft stopped at its base: two
        // strokes for a head ended square at the tip and cut it off
        float head = r * 0.5f;
        Vector2 baseMid = tip - way * head;
        Vector2 side = new Vector2(-way.Y, way.X) * (head * 0.62f);
        _bounce[0] = hit + new Vector2(-0.8f, -0.8f) * r;
        _bounce[1] = hit;
        _bounce[2] = baseMid + way * 1.0f;
        _head[0] = tip;
        _head[1] = baseMid + side;
        _head[2] = baseMid - side;
        // the dark edge: each side moved out by the edge's width, each corner
        // out along its bisector as far as that takes - not grown about the
        // middle, which ran the sharp tip on past itself
        const float edge = 1.6f;
        for (int i = 0; i < 3; i++)
        {
            Vector2 p = _head[i];
            Vector2 u1 = (p - _head[(i + 1) % 3]).Normalized();
            Vector2 u2 = (p - _head[(i + 2) % 3]).Normalized();
            float half = Mathf.Sqrt(Mathf.Max(0.05f, (1.0f - u1.Dot(u2)) * 0.5f));
            _headUnder[i] = p + (u1 + u2).Normalized() * (edge / half);
        }
        var dark = new Color(0.0f, 0.0f, 0.0f, 0.7f * a);
        var pale = new Color(BadgeKnocked, a);
        ink.DrawPolyline(_bounce, dark, 5.5f, true);
        ink.DrawColoredPolygon(_headUnder, dark);
        ink.DrawPolyline(_bounce, pale, 2.5f, true);
        ink.DrawColoredPolygon(_head, pale);
    }

    private readonly Vector2[] _head = new Vector2[3];
    private readonly Vector2[] _headUnder = new Vector2[3];

    /// <summary>Over the ram's target, where it lands in deep water: the
    /// lifebuoy - it swims, with the gear; the circle under water - it drowns,
    /// without (<see cref="RamIntoDeep"/>).</summary>
    private void DrawRamSign(Control ink)
    {
        RamPlan? plan = _ramWaiting ?? _ramPlan;
        if (plan is null || !RamIntoDeep(plan) || _other is not { } o || _field is null)
        {
            _ramSignUp = 0.0f;
            return;
        }
        float up = SignUp(ref _ramSignUp);
        Vector2I onto = plan.Push.Legs[^1];
        Vector3 p = CellMiddle(onto);
        p.Y = _field.WaterTop(onto) / RiseFactor;
        Vector2 at = _camera.UnprojectPosition(p + Vector3.Up * (o.Model.Size.Y * o.Model.PixelsPerUnit * 1.15f));
        float r = Mathf.Max(8.0f, HexScreen() * 0.11f) * Mathf.Lerp(0.75f, 1.0f, up);
        Sign(ink, RamFloats ? Badge.Afloat : Badge.Drowned, at, r, 0.0f, up);
    }

    /// <summary>Over the move's ghost, what the way does to it there
    /// (<see cref="GhostAt"/>, in place of the way's words - «мина», «утонет»,
    /// «огонь» - 2026-10-29): on deep water the lifebuoy or the circle under
    /// water, over the water line - not over the sinking hull, which would take
    /// the sign down with it; on a mine the cracked shield, and the mine's
    /// flash under the hull as it goes off; through fire the flame.</summary>
    private void DrawGhostSign(Control ink)
    {
        if (_ghostSign is not var (kind, anchor) || _ghost is null || _ghostRig is not { Visible: true })
        {
            _ghostSignUp = 0.0f;
            return;
        }
        float hex = HexScreen();
        if (_ghostBurst is var (blast, a))
            Burst(ink, _camera.UnprojectPosition(blast), Mathf.Max(12.0f, hex * 0.22f) * (1.4f - 0.4f * a), BlastFire,
                  a * _ghostClearNow);
        float up = SignUp(ref _ghostSignUp);
        Vector2 at = _camera.UnprojectPosition(anchor + Vector3.Up * (_ghost.Size.Y * _ghost.PixelsPerUnit * 1.15f));
        float r = Mathf.Max(8.0f, hex * 0.11f) * Mathf.Lerp(0.75f, 1.0f, up);
        Sign(ink, kind, at, r, 0.0f, up * _ghostClearNow);
    }

    private float _ghostSignUp;

    /// <summary>A forecast coming up: its clock on by a frame, and how far up
    /// it is, 0 to 1.</summary>
    private float SignUp(ref float clock)
    {
        clock = Mathf.Min(1.0f, clock + (float)GetProcessDeltaTime() / BlastRise);
        return Mathf.SmoothStep(0.0f, 1.0f, clock);
    }
}
