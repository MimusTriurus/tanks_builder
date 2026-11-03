using Godot;

namespace TankSpriteTest;

/// <summary>
/// A machine's level in its branch as rank chevrons (author 2026-11-03), the
/// hangar's caption over each machine: <see cref="Of"/> stacked chevrons
/// pointing up, the first <see cref="Level"/> in <see cref="Lit"/>, the rest
/// in <see cref="Dim"/> - how far it went and how far it can go.
/// </summary>
public partial class QuickRank : Control
{
    public int Level, Of = 3;
    public Color Lit, Dim, Ink = new(0.06f, 0.06f, 0.05f);
    /// <summary>One chevron's width, px; its height is half that, the stack's step a third.</summary>
    public float Wide = 16;

    public QuickRank()
    {
        MouseFilter = MouseFilterEnum.Ignore;
    }

    public override Vector2 _GetMinimumSize() => new(Wide + 4, Wide * 0.5f + (Of - 1) * Wide * 0.36f + 4);

    public override void _Draw()
    {
        float h = Wide * 0.5f, step = Wide * 0.36f, w = Mathf.Max(2.0f, Wide * 0.17f);
        // the lowest chevron is the first level, as on a sleeve
        for (int i = 0; i < Of; i++)
        {
            float top = 2 + (Of - 1 - i) * step;
            var line = new[] { new Vector2(2, top + h), new Vector2(2 + Wide * 0.5f, top), new Vector2(2 + Wide, top + h) };
            DrawPolyline(line, Ink, w + 2.0f, true);
            DrawPolyline(line, i < Level ? Lit : Dim, w, true);
        }
    }
}
