using Godot;

namespace TankSpriteTest;

/// <summary>
/// The hull over the craters the rounds left (<see cref="CelCrater"/>): the
/// way the jackal port's preview has its vehicles feel them (ratel,
/// <c>crater_profile</c>, <c>_hull_ground_at</c>) - the ground measured under
/// the belts' ends and middles, the craters' rise and fall added to it.
///
/// <list type="bullet">
/// <item><b>The running gear follows it</b>: each belt a rigid line on the
/// highest of <see cref="PitPoints"/> points down it - its front half's and
/// its rear half's - and the rig on the two belts: their mean height, the
/// pitch of their lines, the roll of one against the other, on top of the
/// ground's own (<see cref="Settle"/>), so the belts
/// climb the rim, tip over the crest and nose down into the bowl. A crater
/// smaller than the gap between the belts is bridged.</item>
/// <item><b>The sprung mass lags it</b>: when the gear pitches, rolls or
/// rises, the hull over it is left behind by <see cref="PitInertia"/> of the
/// move and swings back on its own springs (<see cref="PitSpring"/>), past
/// level and back - the suspension working, which is the user's ask. On the
/// flat nothing changes and nothing moves.</item>
/// </list>
/// The target of a ram is not lifted (its fall off a bank is its own,
/// <see cref="OtherTick"/>).
/// </summary>
public sealed partial class Tank3DBench
{
    /// <summary>The suspension's springs over the craters: a few swings to
    /// rest - softer and less damped than the shot's kick.</summary>
    private static readonly (float K, float C) PitSpring = (620.0f, 15.0f);

    private readonly Spring _pitPitch = new(PitSpring.K, PitSpring.C);
    private readonly Spring _pitRoll = new(PitSpring.K, PitSpring.C);
    /// <summary>The heave, world px; <see cref="_Process"/> hands it to the
    /// model in its own units.</summary>
    private readonly Spring _pitHeave = new(PitSpring.K, PitSpring.C);

    /// <summary>How much of the gear's move the sprung mass is left behind
    /// by: its inertia against the springs.</summary>
    private const float PitInertia = 0.65f;

    /// <summary>The belts' points measured, as shares of the footprint: their
    /// ends a little in from the tips, where the end wheels bear.</summary>
    private const float PitEnds = 0.85f, PitSides = 0.8f;

    /// <summary>Points measured down each belt: a rim's crest is some 17 px
    /// across on LTR's crater, and they must not step over it.</summary>
    private const int PitPoints = 9;

    /// <summary>How fast the gear takes up what the belts meet, 1/s.</summary>
    private const float PitEase = 30.0f;

    /// <summary>The gear's lift and tilt last frame, for the change.</summary>
    private float _pitNose, _pitLeft, _pitLift;
    private bool _pitHas;

    /// <summary>
    /// The craters under the hull this frame: the lift, world px, to add to
    /// the rig's height; <paramref name="want"/> (the ground's up) tipped by
    /// their tilt; the suspension kicked by the change.
    /// </summary>
    private float Pits(float dt, ref Vector3 want, bool snap)
    {
        CelCrater? pits = _celBurst?.Craters;
        if (pits is null)
        {
            PitsLeave();
            return 0.0f;
        }
        float h = Mathf.DegToRad(_heading);
        var ahead = new Vector3(Mathf.Sin(h), 0.0f, Mathf.Cos(h));
        var left = new Vector3(ahead.Z, 0.0f, -ahead.X);
        Vector3 middle = _rig.Position + ahead * _foot.Along + left * _foot.Across;
        float len = Mathf.Max(_foot.HalfLen * PitEnds, 1.0f), wide = Mathf.Max(_foot.HalfWide * PitSides, 1.0f);
        // Each belt is a rigid line on the highest of the points under it:
        // the highest in its front half and the highest in its rear, the
        // line through the two. On the mean of the points, a rim under one
        // end lifted that end by a sixth of the rim, and the points stepping
        // on and off the narrow crest made it chatter; a belt rests on what
        // stands up under it and bridges what falls away.
        float BeltAt(float v, out float slope)
        {
            float hf = float.MinValue, hr = float.MinValue, uf = len, ur = -len;
            for (int i = 0; i < PitPoints; i++)
            {
                float u = (i / (float)(PitPoints - 1) * 2.0f - 1.0f) * len;
                float hgt = pits.HeightAt(middle + ahead * u + left * v);
                if (u >= 0.0f && hgt > hf) { hf = hgt; uf = Mathf.Max(u, 0.25f * len); }
                if (u <= 0.0f && hgt > hr) { hr = hgt; ur = Mathf.Min(u, -0.25f * len); }
            }
            slope = (hf - hr) / (uf - ur);
            return hr - slope * ur;
        }
        float hl = BeltAt(wide, out float sl), hrt = BeltAt(-wide, out float sr);
        float liftNow = 0.5f * (hl + hrt);
        float noseNow = Mathf.Atan(0.5f * (sl + sr));
        float sideNow = Mathf.Atan((hl - hrt) / (2.0f * wide));
        // The gear's springs and the belts' own give: a little easing, so a
        // rim met is climbed, not stepped on.
        float ease = snap || !_pitHas ? 1.0f : 1.0f - Mathf.Exp(-PitEase * dt);
        float lift = Mathf.Lerp(_pitLift, liftNow, ease);
        float nose = Mathf.Lerp(_pitNose, noseNow, ease);
        float side = Mathf.Lerp(_pitLeft, sideNow, ease);
        if (nose != 0.0f || side != 0.0f)
            want = (want - ahead * Mathf.Tan(nose) - left * Mathf.Tan(side)).Normalized();
        if (_pitHas && !snap && dt > 0.0f)
        {
            // The gear moved; the hull over it did not, at first. + pitch is
            // the nose down, + roll the left side up (TankModel).
            float most = 3.0f;   // rad/s: a crater dug under the hull, not driven into
            _pitPitch.Kick(Mathf.Clamp(PitInertia * (nose - _pitNose) / dt, -most, most));
            _pitRoll.Kick(Mathf.Clamp(-PitInertia * (side - _pitLeft) / dt, -most, most));
            _pitHeave.Kick(Mathf.Clamp(-PitInertia * (lift - _pitLift) / dt, -400.0f, 400.0f));
        }
        _pitNose = nose;
        _pitLeft = side;
        _pitLift = lift;
        _pitHas = true;
        return lift;
    }

    /// <summary>Off the ground - afloat, falling: the next crater met starts
    /// from where it is, not with a jump from the last.</summary>
    private void PitsLeave() => _pitHas = false;
}
