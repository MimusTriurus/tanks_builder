using System;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The smoke a shot leaves at the muzzle, built here rather than read off the
/// atlas - <c>muzzle_flash.SMOKE</c>, transcribed.
///
/// <b>Not a configuration of <see cref="ProcSmoke"/>, and the difference is in
/// the model rather than in the numbers.</b> The plume and the burning tank's
/// column are one class because in Blender they are one file - the fire imports
/// the plume's path outright. This one shares nothing with either:
///
/// <list type="bullet">
/// <item>its phase axis is a <em>table</em> of eight (size, alpha) pairs, not an
/// age. There is no cycle to close, because a shot is an event: it has a first
/// frame and a last one, which is exactly what a plume does not have.</item>
/// <item>there is no path. Puffs are scattered along the bore between -1.0 and
/// +2.2 radii and sit there; nothing integrates, nothing rises, nothing
/// slows.</item>
/// <item>every length is quoted in <em>bore radii</em>, not in hull lengths.
/// That is what lets one <c>muzzle_flash.CONFIG</c> fit three tanks, and it is
/// the whole reason <c>stamp_bore.py</c> exists.</item>
/// </list>
///
/// <b>The section is shared and the model is not, which is the honest split.</b>
/// A puff is a sphere seen through the same material either way, so
/// <see cref="ProcSmoke.Section"/> draws this too: the facing, the stacking, the
/// depth test and the argument about one surface per element all live in one
/// place. What differs is where the circles come from.
///
/// <b>Framed by the turret, and that decides three things at once.</b> The vent
/// is welded to the engine deck so a plume is indexed by the hull; a muzzle is
/// welded to the gun. So the puffs are projected at
/// <see cref="TankSprite.TurretFacing"/>, sheared with the turret, and ordered
/// against the turret rather than held out of it - see <see cref="OverTurret"/>.
///
/// <b>Placed on the measured muzzle, not on a projected point.</b>
/// <see cref="AtlasSet.Project"/> rotates about the origin where the render
/// spins about the stamped ring axis, which is up to 3.3px on <c>LTP</c>. The
/// muzzle in pixels is measured off the barrel layer per heading and carries no
/// such error, so the cluster is hung off that and only its <em>offsets</em> are
/// projected - and a rotated offset is a rotated offset whatever the pivot is.
/// The stamped point is still read, for its <c>z</c>: that is what the height
/// map is compared against and the projection cannot give it.
///
/// <b>Nothing occludes a shot, and that is measured rather than assumed.</b> It
/// was the reason this pair looked hard to build - a turret-indexed layer has
/// the <em>hull</em> baked into its alpha at one relative bearing, which
/// <c>draw_order.py</c> names as a defect needing its own measurement that was
/// never made. The measurement says there is nothing there to fix. The muzzle
/// stands 0.097 to 0.137 world <em>above</em> the height map's ceiling on all
/// three tanks - the map covers the hull and the belts, so its top is the hull
/// roof, and the gun is over it - and the cloud reaches at most 0.06 down from
/// there. Higher is nearer under this camera, so the hull can never be in front
/// of a shot. The turret is not in the map either, and it does not need to be:
/// across every heading of the peak phase the rendered flash overlaps the turret
/// on 0.0 to 0.9 percent of its pixels.
///
/// So the depth test below is wired and measures as a no-op, and it is kept for
/// that reason rather than in spite of it: it costs one texture read, it is the
/// same call the plume makes, and a model that ever dips lower gets the right
/// answer without anybody remembering to add it. Recorded here because "the
/// holdout is the hard part" was my own reading before the numbers, and a worry
/// that turned out empty is worth as much written down as one that did not.
/// </summary>
public sealed partial class ProcFume : Node2D
{
    /// <summary>Most puffs the shared section will composite. The model asks for
    /// fourteen.</summary>
    public const int MaxPuffs = ProcSmoke.MaxPuffs;

    // --- muzzle_flash.SMOKE, transcribed -------------------------------------
    // Every length below is in bore radii and multiplied through by Size, which
    // is that file's "scale" folded into the phase table rather than standing
    // beside it. Quoting them against anything else is the mistake the forest's
    // port paid for once already.

    /// <summary>Overall size, over the phase table. <c>muzzle_flash.SMOKE</c>'s
    /// <c>scale</c>.</summary>
    public float Scale = 1.30f;

    /// <summary>Overall solidity, over the phase table. That file's
    /// <c>density</c>.</summary>
    public float Density = 1.20f;

    public int Puffs = 14;

    /// <summary>Across the bore, in radii.</summary>
    public float Spread = 1.35f;

    /// <summary>Along the bore, in radii: behind the muzzle to ahead of it. It
    /// starts negative because the reference's smoke centre sits at -44px
    /// against the flame's -27 - the smoke hangs <em>around and behind</em> the
    /// muzzle rather than flying out with the fire.</summary>
    public Vector2 Along = new(-1.0f, 2.2f);

    /// <summary>One puff's radius, in bore radii.</summary>
    public Vector2 PuffRadius = new(0.35f, 0.80f);

    /// <summary>Per-vertex lumping in the render, which a circle cannot have; it
    /// is spent on the radius here so a puff covers about the same ground. The
    /// same trade <see cref="ProcSmoke"/> makes.</summary>
    public float Wobble = 0.35f;

    /// <summary>How much one puff's tone wanders. That file multiplies its
    /// colour by <c>0.7 + 0.5*hash</c> per puff.</summary>
    public Vector2 Tone = new(0.7f, 0.5f);

    public int Seed = 21;

    /// <summary>
    /// Size and solidity per phase, as the table rather than as a curve.
    ///
    /// <b>Smoke grows while the fire dies and outlives it</b>: on the reference
    /// the last frame is 3353px of smoke against 1136 of flame, and its alpha
    /// falls 0.99 to 0.43. It never shrinks - it thins, which is why size is
    /// monotonic here and alpha is not.
    ///
    /// Eight entries because the rendered layer has eight phases and
    /// <see cref="EffectLayer.Hold"/> already says how long each is held. The
    /// built pair reads the same clock for the reason the two flash sources do:
    /// a pair whose tempos differ measures the tempo instead of the artwork.
    /// </summary>
    public static readonly Vector2[] Phases =
    {
        new(0.30f, 0.50f), new(0.58f, 0.78f), new(0.80f, 0.90f),
        new(0.95f, 0.90f), new(1.05f, 0.84f), new(1.12f, 0.72f),
        new(1.16f, 0.58f), new(1.20f, 0.40f),
    };

    /// <summary>
    /// The colour of the smoke, measured off the shipped layer rather than read
    /// out of the config.
    ///
    /// <b>Measured for the reason the plume's ink is</b>: the config's numbers
    /// are linear and feed a tone mapper, while this composites in sRGB, so the
    /// two are not the same quantity. Flat across the whole alpha range on all
    /// three tanks: (124.6, 114.4, 105.6) at the faintest against (120.4, 109.9,
    /// 101.2) at the densest on <c>LTP</c>, and the same within a level on the
    /// other two. Warm - R minus B is 19 against the plume's 5.
    ///
    /// <b>The rim is not carried, and that is a measurement too.</b> The
    /// material's own docstring says the edge darkens toward <c>rim_colour</c>,
    /// but the node it describes feeds <c>facing**rim_reach</c> straight into a
    /// mix whose second colour is the rim - which puts the rim colour in the
    /// <em>middle</em>. What the render actually put down is the four percent
    /// above, in that direction, and four percent is under the threshold at
    /// which anything here gets modelled. Said out loud because the source reads
    /// as though it does more.
    /// </summary>
    public Color Ink = new(124.0f / 255.0f, 114.0f / 255.0f, 105.0f / 255.0f);

    /// <summary>Alpha goes as <c>facing**RimFalloff</c>, which is Layer Weight's
    /// Facing raised to the config's number.</summary>
    public float RimFalloff = 1.1f;

    /// <summary>How solid one surface is head-on, before the puffs stack.</summary>
    public float Opacity = 0.68f;

    public TankSprite? Tank;

    private ShaderMaterial? _shader;
    private static Texture2D? _white;

    /// <summary>The quad, in px off the shared anchor.</summary>
    public Rect2 Extent { get; private set; }

    /// <summary>Whether the last draw put anything up.</summary>
    public bool Showing { get; private set; }

    /// <summary>What the last draw put up, in one line. Printed once, the way
    /// <see cref="ProcSmoke.Note"/> is: a picture alone cannot separate "the
    /// model is small" from "the model is not drawing".</summary>
    public string Note { get; private set; } = "";

    private bool _told;

    public override void _Ready()
    {
        TextureFilter = TextureFilterEnum.Linear;
        _shader = new ShaderMaterial { Shader = ProcSmoke.Section };
        Material = _shader;
        _white ??= MakeWhite();
    }

    private static Texture2D MakeWhite()
    {
        Image pixel = Image.CreateEmpty(1, 1, false, Image.Format.Rgba8);
        pixel.Fill(Colors.White);
        return ImageTexture.CreateFromImage(pixel);
    }

    public override void _Process(double delta)
    {
        // Redrawn while there is anything to show and once more after there is
        // not - EffectLayer's rule: a layer that stops asking the moment it has
        // nothing never gets the chance to draw nothing.
        if (Wanted || Showing)
            QueueRedraw();
    }

    /// <summary>Whether a shot is on screen and this is the source drawing it.
    /// Keyed on the phase rather than on the flag, because the phase already
    /// holds every reason there is no shot - between rounds, effects off, a set
    /// with no shot layers at all.</summary>
    private bool Wanted => Tank is { ShotPhase: >= 0 } tank
                           && tank.ActiveSource == FlashSource.Built
                           && tank.Atlas is { HasBore: true };

    // --- the model -----------------------------------------------------------

    /// <summary>One puff: where it sits relative to the muzzle in the bore's own
    /// frame, how big, how solid, and how its tone wandered.</summary>
    public readonly record struct Puff(Vector3 Offset, float Radius, float Alpha,
                                       float Tone);

    /// <summary>
    /// Every puff of one shot at <paramref name="phase"/>.
    ///
    /// <b>The scatter is hashed on the puff and never on the phase.</b>
    /// <see cref="ProcSmoke"/>'s trap, and it bites harder here: hash in the
    /// phase and the cloud is a different cloud every frame, which over eight
    /// frames reads as static rather than as smoke.
    /// </summary>
    public Puff[] Build(AtlasSet.Port bore, int phase)
    {
        int count = Math.Clamp(Puffs, 1, MaxPuffs);
        Vector2 step = Phases[Math.Clamp(phase, 0, Phases.Length - 1)];
        float size = step.X * Scale;
        float alpha = Math.Min(step.Y * Density, 1.0f);
        var (u, v) = Plumes.Across(bore.Dir);
        var made = new Puff[count];
        for (int k = 0; k < count; k++)
        {
            float along = bore.Radius * size
                          * Mathf.Lerp(Along.X, Along.Y, Plumes.Hash01(k, Seed + 1));
            float across = bore.Radius * Spread * size;
            float ou = across * (2.0f * Plumes.Hash01(k, Seed + 2) - 1.0f);
            float ov = across * (2.0f * Plumes.Hash01(k, Seed + 3) - 1.0f);
            float radius = bore.Radius * size
                           * Mathf.Lerp(PuffRadius.X, PuffRadius.Y,
                                        Plumes.Hash01(k, Seed + 4));
            made[k] = new Puff(bore.Dir * along + u * ou + v * ov, radius, alpha,
                               Tone.X + Tone.Y * Plumes.Hash01(k, Seed + 6));
        }
        // Lowest first: higher is nearer under this camera, so the top of the
        // cloud is what the depth buffer would have put on top. ProcSmoke sorts
        // by age because its path only ever climbs; there is no path here, so
        // the height is asked for directly.
        Array.Sort(made, (a, b) => a.Offset.Z.CompareTo(b.Offset.Z));
        return made;
    }

    /// <summary>
    /// Whether the shot is in front of the turret at this bearing.
    ///
    /// <b>Worked out rather than read, because <c>over_turret</c> was
    /// deliberately never stamped onto these two layers.</b>
    /// <c>draw_order.py</c> left the flash and its smoke holding the turret out
    /// of their alpha, on the grounds that a turret-indexed layer has the turret
    /// exactly right - which is true of the render and useless to a built one,
    /// whose alpha holds nothing out of anything.
    ///
    /// It needs no stamp either way. The muzzle is a fixed point in the turret's
    /// own frame, so whether it is nearer than the mount is a function of the
    /// turret's bearing alone: the bore drawn pointing down the screen is the
    /// bore pointing at the camera, and down the screen is nearer. One
    /// comparison on the direction this class already projects.
    ///
    /// The switch lands where the gun is broadside, which is where the shot
    /// stands beside the turret rather than over or behind it - the cheapest
    /// heading there is to be wrong on, and the one <c>draw_order</c> reports
    /// margin for on every other layer.
    ///
    /// <b>It decides very little.</b> Measured over every heading of the peak
    /// phase, the rendered flash covers 0.0 to 0.9 percent of its own pixels
    /// with the turret. Here because the answer is one comparison on a vector
    /// this class already has, not because the picture was waiting on it.
    /// </summary>
    public static bool OverTurret(AtlasSet atlas, AtlasSet.Port bore,
                                  double turretFacing)
        => atlas.Project(bore.Dir, turretFacing).Y > 0.0f;

    // --- drawing -------------------------------------------------------------

    public override void _Draw()
    {
        Showing = false;
        if (Tank?.Atlas is not { } atlas || !Wanted || _shader is null)
            return;
        _white ??= MakeWhite();
        AtlasSet.Port bore = atlas.Bore;
        double turret = Tank.TurretFacing;
        ZIndex = TankSprite.ZFor("smoke", OverTurret(atlas, bore, turret));

        // Measured, not projected: see the class note on the pivot.
        Vector2 muzzle = atlas.Muzzle(atlas.FrameFor(turret)) - atlas.Anchor;

        var puffs = new Godot.Collections.Array();
        var lifts = new Godot.Collections.Array();
        Rect2? box = null;
        foreach (Puff puff in Build(bore, Tank.ShotPhase))
        {
            if (puff.Alpha <= 0.002f || puffs.Count >= MaxPuffs)
                continue;
            // The offset is projected as a vector, which is exact whatever the
            // render span about; the muzzle it hangs off is measured.
            Vector2 at = muzzle + atlas.Project(puff.Offset, turret);
            float radius = (float)(puff.Radius * (1.0 + Wobble * 0.5)
                                   / atlas.UnitsPerPixel);
            var one = new Rect2(at - Vector2.One * radius,
                                Vector2.One * radius * 2.0f);
            box = box is null ? one : box.Value.Merge(one);
            puffs.Add(new Vector4(at.X, at.Y, radius, puff.Alpha * puff.Tone));
            lifts.Add(new Vector2(
                Plumes.LiftOf(atlas, bore.Point.Z + puff.Offset.Z),
                Plumes.BulgeOf(atlas, puff.Radius)));
        }
        if (box is null || puffs.Count == 0)
            return;
        Rect2 quad = box.Value.Grow(1.0f);
        Extent = quad;
        Showing = true;
        if (!_told)
        {
            _told = true;
            Note = string.Format(
                "proc smoke: {0} puffs, {1:0}x{2:0}px at ({3:0},{4:0}) off the "
                + "anchor, muzzle ({5:0},{6:0}), bore r {7:0.0000} = {8:0.0}px",
                puffs.Count, quad.Size.X, quad.Size.Y, quad.Position.X,
                quad.Position.Y, muzzle.X, muzzle.Y, bore.Radius,
                bore.Radius / atlas.UnitsPerPixel);
            GD.Print(Note);
        }

        _shader.SetShaderParameter("puffs", puffs);
        _shader.SetShaderParameter("lift", lifts);
        _shader.SetShaderParameter("count", puffs.Count);
        _shader.SetShaderParameter("origin", quad.Position);
        _shader.SetShaderParameter("span", quad.Size);
        _shader.SetShaderParameter("ink", new Vector3(Ink.R, Ink.G, Ink.B));
        _shader.SetShaderParameter("rim_falloff", RimFalloff);
        _shader.SetShaderParameter("opacity", Opacity);
        // The hull's map at the hull's own heading, while this draws at the
        // turret's. That split is the point: it is what the rendered layer
        // cannot do, having baked one hull bearing into its alpha.
        Plumes.SetDepth(_shader, atlas, Tank.HullFacing);

        DrawSetTransformMatrix(Tank.ShearFor(true));
        Vector2 bump = Tank.HeaveShift(true, false);
        DrawTextureRect(_white!, new Rect2(quad.Position + bump, quad.Size), false);
        DrawSetTransformMatrix(Transform2D.Identity);
    }
}
