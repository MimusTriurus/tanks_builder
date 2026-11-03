using Godot;

namespace TankSpriteTest;

/// <summary>
/// A machine's level in its branch as rank chevrons (author 2026-11-03), beside
/// the hangar's caption over each machine: <see cref="Of"/> stacked chevrons
/// pointing up, the first <see cref="Level"/> filled in <see cref="Lit"/>, the
/// rest faint in <see cref="Dim"/> - how far it went and how far it can go.
/// </summary>
public partial class QuickRank : Control
{
    public int Level, Of = 3;
    public Color Lit, Dim, Ink = new(0.06f, 0.06f, 0.05f);
    /// <summary>One chevron's width, px; its height is 0.34 of that, the stack's step 0.44.</summary>
    public float Wide = 16;
    /// <summary>The whole stack's height, px, when it must match something beside it;
    /// zero keeps <see cref="Wide"/>.</summary>
    public float High;

    private const float Rise = 0.34f, Step = 0.44f;

    public QuickRank()
    {
        MouseFilter = MouseFilterEnum.Ignore;
    }

    private float W => High > 0 ? (High - 4) / (Rise + (Of - 1) * Step) : Wide;

    public override Vector2 _GetMinimumSize() => new(W + 4, W * (Rise + (Of - 1) * Step) + 4);

    public override void _Draw()
    {
        float wide = W, h = wide * Rise, step = wide * Step, w = Mathf.Max(3.0f, wide * 0.19f);
        // the lowest chevron is the first level, as on a sleeve; the gap between
        // two is about a chevron's own stroke, so they count at a glance
        for (int i = 0; i < Of; i++)
        {
            float top = 2 + (Of - 1 - i) * step;
            var line = new[] { new Vector2(2, top + h), new Vector2(2 + wide * 0.5f, top), new Vector2(2 + wide, top + h) };
            if (i < Level)
            {
                DrawPolyline(line, Ink, w + 2.0f, true);
                DrawPolyline(line, Lit, w, true);
            }
            else
                // a step not yet taken: faint and unrimmed, so the lit ones lead
                DrawPolyline(line, Dim, w, true);
        }
    }
}
