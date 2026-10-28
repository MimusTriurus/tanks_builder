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
        if (badge == Badge.None || BlastMarked(model))
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
        // a token, dark with a pale rim: the sign reads over the hull's own
        // fire and smoke, which a bare flame drowned in. A fire beats on its
        // owner's turn - it is the owner's to answer.
        float beat = badge == Badge.Burning && side == _turn
            ? 0.5f - 0.5f * Mathf.Cos(Mathf.Tau * _badgeClock / BadgeBeat) : 0.0f;
        float token = r * (1.0f + 0.12f * beat);
        ink.DrawCircle(at, token + 1.5f, new Color(0.0f, 0.0f, 0.0f, 0.45f));
        ink.DrawCircle(at, token, BadgeToken);
        ink.DrawArc(at, token, 0.0f, Mathf.Tau, 32, badge == Badge.Burning ? BadgeKnocked.Lerp(BlastFire, beat) : BadgeKnocked,
                    1.5f + 1.0f * beat, true);
        float icon = r * 0.68f;
        switch (badge)
        {
            case Badge.Burning:
                Flame(ink, at + new Vector2(0.0f, icon * 0.2f), icon * 0.8f, BlastFire);
                break;
            case Badge.Knocked:
                Shield(ink, at, icon, BadgeKnocked);
                break;
            case Badge.Drowned:
                Drowned(ink, at, icon);
                break;
            case Badge.Afloat:
                Buoy(ink, at, icon);
                break;
        }
    }

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
    /// between them dark.
    /// </summary>
    private void Shield(Control ink, Vector2 at, float r, Color c, float a = 1.0f)
    {
        for (int i = 0; i < ShieldWhole.Length; i++)
            _shieldWhole[i] = at + ShieldWhole[i] * (r * 1.12f) + new Vector2(0.0f, r * 0.02f);
        ink.DrawColoredPolygon(_shieldWhole, new Color(0.0f, 0.0f, 0.0f, 0.6f * a));
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
    private void Drowned(Control ink, Vector2 at, float r)
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
        ink.DrawColoredPolygon(_badgeWet, BadgeWater);
        ink.DrawArc(at, r * 0.92f, 0.0f, Mathf.Tau, 28, BadgeKnocked, 2.0f, true);
    }

    /// <summary>A lifebuoy: a thick ring, red and white by quarters.</summary>
    private static void Buoy(Control ink, Vector2 at, float r)
    {
        float w = r * 0.38f;
        float mid = r - w * 0.5f;
        for (int q = 0; q < 4; q++)
        {
            float a = Mathf.Pi * 0.25f + Mathf.Pi * 0.5f * q;
            ink.DrawArc(at, mid, a, a + Mathf.Pi * 0.5f, 8, q % 2 == 0 ? BadgeBuoy : BadgeKnocked, w, true);
        }
    }
}
