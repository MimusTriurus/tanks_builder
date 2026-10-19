using System.Collections.Generic;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// Mud on the board: a mask of which ground is wet, read by the soil shader.
///
/// <b>The ash map's arrangement and not a tile of its own.</b> The tops all
/// sample one ground picture, stopped short of its own rim (<see cref="Bleed"/>),
/// and a second picture for mud cells would put the hexagon back round every
/// one of them. A mask laid over the board in world XZ has no idea where a
/// cell ends: a disc per cell, its rim torn by the shader's noise, and two mud
/// cells side by side are one patch.
///
/// <b>Which floor is mud.</b> <see cref="Foundation.Mud"/> - its own floor since
/// the editor's stage 4 (docs/editor3d-plan.md); sand stood in for it before,
/// and the events board's mud is where that sand was. Baked with the faces,
/// so the editor's board, whose faces are built again on a stroke, has it
/// laid again too.
/// </summary>
public sealed partial class Stage3D
{
    /// <summary>How far a mud cell's disc reaches, in circumradii, and how
    /// much of it is fade.
    ///
    /// <b>The torn edge is inside the cell, and that was measured, not
    /// chosen.</b> First cut past the cell's own sides, it came back a clean
    /// hexagon: the grass next door (<see cref="Grass3D"/>, a painted layer
    /// over each grass cell) covers whatever the mud spilt on to it, and the
    /// hexagon is where it stops. So the shader's cut sits at about 0.83 of a
    /// circumradius and wanders 0.73-0.93 with the noise: a shore torn into
    /// the cell's own ground, the cell's dirt border still round it.</summary>
    private const float MireReach = 1.15f;
    private const float MireRim = 0.55f;

    /// <summary>The disc over the seam between two mud cells, so that two of
    /// them read as one patch rather than two puddles with a strip of grass
    /// between. In circumradii, centred on the shared side.</summary>
    private const float MireBridge = 0.7f;

    private ImageTexture? _mireMap;

    private void Mire()
    {
        if (Field.Atlas is null)
            return;
        float circum = Field.Atlas.HexRect.Size.X * 0.5f;
        float loX = float.MaxValue, hiX = float.MinValue;
        float loZ = float.MaxValue, hiZ = float.MinValue;
        bool any = false;
        for (int q = 0; q < Field.Columns; q++)
        for (int r = 0; r < Field.Rows; r++)
        {
            var cell = new Vector2I(q, r);
            if (!Field.InBounds(cell))
                continue;
            Vector3 top = CellTop(cell);
            loX = Mathf.Min(loX, top.X - circum); hiX = Mathf.Max(hiX, top.X + circum);
            loZ = Mathf.Min(loZ, top.Z - circum); hiZ = Mathf.Max(hiZ, top.Z + circum);
            any |= Field.FoundationAt(cell) == Foundation.Mud;
        }
        // The sides are left out on purpose: a wall's face is one line in XZ,
        // so a cliff next to a mud cell would come out mud from top to foot.
        if (!any || loX > hiX)
        {
            MireTell("mire_ink", 0.0f);
            return;
        }

        int wide = Mathf.Clamp(Mathf.CeilToInt((hiX - loX) / AshGrain), 8, 2048);
        int tall = Mathf.Clamp(Mathf.CeilToInt((hiZ - loZ) / AshGrain), 8, 2048);
        var frame = new Vector4(loX, loZ, 1.0f / (hiX - loX), 1.0f / (hiZ - loZ));
        var wet = new byte[wide * tall];
        float perX = wide * frame.Z, perZ = tall * frame.W;
        var mud = new List<Vector2I>();
        for (int q = 0; q < Field.Columns; q++)
        for (int r = 0; r < Field.Rows; r++)
        {
            var cell = new Vector2I(q, r);
            if (Field.InBounds(cell) && Field.FoundationAt(cell) == Foundation.Mud)
                mud.Add(cell);
        }
        foreach (Vector2I cell in mud)
            Daub(wet, wide, tall, frame, CellTop(cell), MireReach * circum, 1.0f,
                 perX, perZ, MireRim);
        for (int i = 0; i < mud.Count; i++)
        for (int j = i + 1; j < mud.Count; j++)
            if (HexField.HeadingTo(mud[i], mud[j]) >= 0)
                Daub(wet, wide, tall, frame, (CellTop(mud[i]) + CellTop(mud[j])) * 0.5f,
                     MireBridge * circum, 1.0f, perX, perZ, MireRim);

        Image img = Image.CreateFromData(wide, tall, false, Image.Format.R8, wet);
        if (_mireMap is null)
            _mireMap = ImageTexture.CreateFromImage(img);
        else
            _mireMap.SetImage(img);
        MireTell("mire", _mireMap);
        MireTell("mire_map", frame);
        MireTell("mire_ink", 1.0f);
    }

    /// <summary><see cref="Tell"/> for the tops and the glaze and not the
    /// sides - see <see cref="Mire"/>. The glaze has to hear everything the
    /// tops hear, or a glazed cell reads as a patch.</summary>
    private void MireTell(string name, Variant value)
    {
        foreach (MeshInstance3D face in new[] { _tops, _glaze })
            if (face?.MaterialOverride is ShaderMaterial ink)
                ink.SetShaderParameter(name, value);
    }
}
