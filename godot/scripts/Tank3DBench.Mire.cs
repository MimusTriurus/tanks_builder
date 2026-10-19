using Godot;

namespace TankSpriteTest;

/// <summary>
/// The tank in mud: it settles into it and it drags.
///
/// <b>Mud is <see cref="Foundation.Sand"/></b>, the floor the board paints as
/// mud (<c>Stage3D.Mire</c>) - the GDD's "грязь\песок". The rule's own figure
/// (<c>terrain.json</c>, speed 0.75) caps the leg into the cell and nothing
/// once the hull is in it, and the free drive not at all, so in the picture
/// the tank went through mud as through grass. This is the bench's: a drag
/// for as long as the hull stands in it, both drives, and the hull let down
/// into it. Both ease by <see cref="_mire"/>, so the tank sinks as it goes in
/// and climbs out as it leaves rather than stepping at the seam.
/// </summary>
public sealed partial class Tank3DBench
{
    /// <summary>Top speed in mud, a share of the class's - under the rule's
    /// 0.75, because at that the slowdown did not read at all.</summary>
    private const float MireSpeed = 0.45f;

    /// <summary>How deep the hull settles, a share of its deck's height
    /// (<see cref="_deckPx"/>): the belts down to about half, and the ground
    /// hides what is under it.</summary>
    private const float MireSink = 0.22f;

    /// <summary>How fast it settles in, and how fast it climbs out - one over
    /// seconds. Slower in than out: mud takes the weight over a moment, and
    /// leaving is the belts on firm ground.</summary>
    private const float MireIn = 1.2f;
    private const float MireOut = 2.5f;

    /// <summary>How far into the mud the hull is, 0 to 1.</summary>
    private float _mire;

    private bool OnMire(Vector2I cell) =>
        _field is not null && _field.InBounds(cell)
        && _field.FoundationAt(cell) == Foundation.Sand
        && !_field.IsRamp(cell) && !_field.IsWater(cell);

    /// <summary>Ease <see cref="_mire"/> toward the cell under the middle and
    /// answer how far the hull is let down, world Y.</summary>
    private float MireSettle(Vector2I cell, float dt, bool snap)
    {
        float want = OnMire(cell) ? 1.0f : 0.0f;
        _mire = snap ? want
            : Mathf.MoveToward(_mire, want, dt * (want > _mire ? MireIn : MireOut));
        float sunk = _mire * MireSink * _deckPx / RiseFactor;
        if (!snap)
            MudTick(dt, sunk);
        return sunk;
    }

    // --- mud on the armour (Toon's mud_*) -------------------------------------

    /// <summary>How high the coat reaches at its fullest, a share of the deck's
    /// height: the belts and the lower plates, not the deck.</summary>
    private const float MudReach = 0.8f;

    /// <summary>How much coat a hull picks up a second at full speed in mud.
    /// Two cells' crossing at the mud's speed is about two seconds, which
    /// takes it most of the way up.</summary>
    private const float MudPick = 0.4f;

    /// <summary>What standing in it leaves with no driving at all - the foot
    /// that went down into it.</summary>
    private const float MudFoot = 0.3f;

    /// <summary>Seconds for wet mud to dry pale once out of it, and for a full
    /// coat to fall off once dry - it sheds as it dries, so from leaving the
    /// mud to a clean hull is about forty seconds. Water washes it off at half
    /// a coat a second.</summary>
    private const float MudDries = 12.0f;
    private const float MudSheds = 30.0f;
    private const float MudWash = 0.5f;

    /// <summary>A fleck's size, a share of the deck's height.</summary>
    private const float MudGrain = 0.06f;

    private float _mudCoat, _mudDry;

    private void MudTick(float dt, float sunk)
    {
        if (_model.Cel.Count == 0)
            return;
        if (_mire > 0.5f)
        {
            float pace = Mathf.Abs(_speed) / Mathf.Max(MaxSpeed * MireSpeed, 1.0f);
            _mudCoat = Mathf.Max(_mudCoat, MudFoot * _mire);
            _mudCoat = Mathf.Min(1.0f, _mudCoat + MudPick * Mathf.Min(pace, 1.0f) * dt);
            _mudDry = Mathf.MoveToward(_mudDry, 0.0f, dt * 2.0f);
        }
        else
        {
            _mudDry = Mathf.MoveToward(_mudDry, 1.0f, dt / MudDries);
            // Wet mud holds; dry, it cracks off - the coat goes down from the
            // top as it does, so the edge sinks back toward the belts.
            _mudCoat = Mathf.MoveToward(_mudCoat, 0.0f, _mudDry * dt / MudSheds);
        }
        // Into water, it comes off (Tank3DBench.Water's line on the armour).
        if (_waterOn > 0.5f)
            _mudCoat = Mathf.MoveToward(_mudCoat, 0.0f, dt * MudWash);
        float deck = _deckPx / RiseFactor;
        foreach (ShaderMaterial cel in _model.Cel)
        {
            cel.SetShaderParameter("mud_on", _mudCoat);
            // The foot is the rig's height, which is the sunk one in mud - the
            // coat starts at the belts' bottom wherever the ground is.
            cel.SetShaderParameter("mud_y", _rig.Position.Y);
            cel.SetShaderParameter("mud_reach", MudReach * deck + sunk);
            cel.SetShaderParameter("mud_grain", MudGrain * deck);
            cel.SetShaderParameter("mud_dry", _mudDry);
        }
    }

    private void MudClear()
    {
        _mudCoat = 0.0f;
        _mudDry = 0.0f;
        foreach (ShaderMaterial cel in _model.Cel)
            cel.SetShaderParameter("mud_on", 0.0f);
    }

    /// <summary>The share of top speed the mud leaves, 1 out of it.</summary>
    private float MireCap => Mathf.Lerp(1.0f, MireSpeed, _mire);
}
