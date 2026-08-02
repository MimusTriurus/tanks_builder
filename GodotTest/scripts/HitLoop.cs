using System;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The clock behind a shell arriving: an event, not a loop.
///
/// Shaped like the shot's rather than like <see cref="ExhaustLoop"/> or
/// <see cref="BurnLoop"/>. A hit has a first frame and a last one and then it is
/// over, so this is a table of held frames that runs out into -1 - the same
/// shape as <see cref="EffectLayer.Hold"/>, and separate from it only because
/// the hit is rendered with ten phases against the shot's eight.
///
/// Uneven for the reason the shot's is uneven, and more so. The burst is gone by
/// phase four and the dust outlives it by six, so the early phases are one
/// screen frame each and the late ones are held for eight: a flat tempo either
/// stretches the flash into a bonfire or cuts the dust off in the middle. The
/// whole thing runs 40 frames, two thirds of a second at 60fps.
///
/// Which plate was hit is carried here rather than on the tank, because it
/// belongs to the event: the hull can turn while the dust is still settling, and
/// the hit has to stay on the plate it landed on rather than following the
/// heading round.
///
/// The calibre is here for the same reason, and it was not at first. It sat on
/// the tank as a live setting that the layer read on every draw, so it described
/// the knob rather than the shell: turning the dial while the dust was still in
/// the air resized the round that had already landed, and one shell went off at
/// two calibres. Everything about a hit that is decided when the trigger goes
/// belongs to the hit - the plate, where along it the round came in, and how big
/// the round was.
/// </summary>
public sealed class HitLoop
{
    public static readonly int[] Hold = { 1, 1, 2, 2, 3, 4, 5, 6, 7, 9 };

    public static int Duration
    {
        get
        {
            int total = 0;
            foreach (int hold in Hold)
                total += hold;
            return total;
        }
    }

    /// <summary>Phase to show <paramref name="elapsed"/> screen frames into a
    /// hit, or -1 once it is over.</summary>
    public static int PhaseAt(int elapsed)
    {
        if (elapsed < 0)
            return -1;
        int t = 0;
        for (int i = 0; i < Hold.Length; i++)
        {
            t += Hold[i];
            if (elapsed < t)
                return i;
        }
        return -1;
    }

    /// <summary>Which plate the live hit landed on. Empty when nothing is
    /// running.</summary>
    public string Face { get; private set; } = "";

    /// <summary>Screen pixels the hit is nudged along the plate, so two hits on
    /// the same face do not land on the same pixel. Along the plate's own
    /// tangent, which is what keeps the scatter on the metal.</summary>
    public float Scatter { get; private set; }

    /// <summary>The calibre this shell went off at, as a multiplier on the
    /// rendered burst. Fixed when it lands and untouched for the rest of its
    /// life, whatever the dial does in the meantime.</summary>
    public float Scale { get; private set; } = 1.0f;

    private int _frame = -1;

    public int Phase => PhaseAt(_frame);

    public bool Live => Phase >= 0;

    /// <summary>Start a hit on <paramref name="face"/>. Restarts from phase
    /// zero rather than being ignored while one is running - a second shell
    /// landing is a second hit, and the useful thing to see is the newest.
    /// </summary>
    public void Strike(string face, float scatter = 0.0f, float scale = 1.0f)
    {
        Face = face;
        Scatter = scatter;
        Scale = scale;
        _frame = 0;
    }

    public void Advance()
    {
        if (_frame >= 0)
            _frame++;
        if (Phase < 0)
            _frame = -1;
    }

    public void Reset()
    {
        _frame = -1;
        Face = "";
        Scatter = 0.0f;
        Scale = 1.0f;
    }
}
