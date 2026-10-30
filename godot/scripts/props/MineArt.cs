using Godot;

namespace TankSpriteTest;

/// <summary>
/// The mine as it stands on the board before anybody drives over it.
///
/// <b>The rules require it and the board did not have it.</b> GDD field.md says a
/// minefield is seen by both players always - <c>TerrainRules.Of(Cover.Minefield)
/// .Hidden</c> is false and has a check on it - and until this there was nothing
/// to see: the cell was ordinary ground until a hull found the charge. That is the
/// F5 row of the effects plan, and the one thing it insists on is that <b>the
/// marker and the burst read the same point</b>: both come off
/// <see cref="Mines.At"/>, so a mine cannot be drawn in one place and go off in
/// another.
///
/// <b>A standing billboard, not a decal, because the art is drawn as an
/// object.</b> The picture is a mine seen from where this camera stands, with a
/// lit top and a shaded side; laid flat on the ground it would be that view
/// squashed again, which reads as a sticker. So it is the quad a prop uses -
/// <see cref="Stage3D.Stem"/> for the mesh, <see cref="Stage3D.Trunk"/> for the
/// seat - with its foot at the bottom middle of what the art actually covers.
///
/// <b>Its own file rather than a place in the prop set</b>, and that is a debt
/// named rather than paid: <see cref="PropSet"/> already reads art off disk with a
/// measured foot, a bleed and mipmaps, but it reads it as <c>family/tier</c> of the
/// random dressing - things that sway, burn and are sown by hash. A mine is placed
/// by the board's own cover and stands still. If a second marker of this kind ever
/// appears (a smoke pot, a wreck marker), the two should merge into that loader
/// rather than become three copies of this one.
/// </summary>
public sealed partial class MineArt : Node3D
{
    /// <summary>
    /// Which rung of <see cref="Stage3D"/>'s ladder it sorts on: the dressing's,
    /// under everything that stands on the board.
    ///
    /// <b>A tank parks on top of a mine - that is the whole event - so the marker
    /// has to give up the sort against a hull</b>, which is <c>DressOrder</c>'s own
    /// argument for the bush a tank has driven over. The mine's burst answers the
    /// same question two ways (see <c>ProcSlam.Lit</c>); the marker does not need
    /// to, because it is under the tank in every case that matters: the frame the
    /// hull covers it is the frame it stops existing.
    /// </summary>
    public const int Rung = Stage3D.DressOrder;

    /// <summary>How wide the thing is drawn, in tile widths - the art's own used
    /// rect scaled to this. Judged on the bench: a marker has to read at board
    /// zoom on any ground and must not be mistaken for the crater that replaces
    /// it, and it is a mine, not a haystack.</summary>
    public const float Wide = 0.22f;

    /// <summary>The file, in <c>AssetRoot.Markers</c>.</summary>
    public const string File = "mine";

    /// <summary>Whether the art is in memory - <see cref="Read"/>. False leaves
    /// every marker undrawn and the board as it was, which is the same bargain the
    /// hand-drawn ground and the sounds make: an asset folder may simply be
    /// absent.</summary>
    public static bool Loaded => _art is not null;

    private static Texture2D? _art;
    private static Vector2 _foot;
    private static Vector2 _frame;

    /// <summary>
    /// Read the art off disk, once. True when there is something to draw.
    ///
    /// <b>Static, because the picture is one picture</b>: every marker on the board
    /// is the same mine, and a texture per cell would be the same megabyte a dozen
    /// times. The geometry rides with it for the same reason - the foot is a
    /// property of the drawing, not of the cell it stands on.
    ///
    /// The foot is the bottom middle of the <em>used</em> rect rather than of the
    /// file: the art is a small thing in a large square, and a foot at the file's
    /// edge would hang the mine a quarter of a tile above its own point. Mipmaps
    /// because it is drawn at a tenth of the size it is painted at -
    /// <see cref="PropSet"/>'s reason, unchanged.
    /// </summary>
    public static bool Read(string root)
    {
        if (_art is not null)
            return true;
        string path = System.IO.Path.Combine(root, File + ".png");
        if (!System.IO.File.Exists(path))
            return false;
        Image? art = Image.LoadFromFile(path);
        if (art is null)
            return false;
        Rect2I used = art.GetUsedRect();
        if (used.Size.X <= 0 || used.Size.Y <= 0)
            return false;
        _frame = new Vector2(art.GetWidth(), art.GetHeight());
        _foot = new Vector2(used.Position.X + used.Size.X * 0.5f,
                            used.Position.Y + used.Size.Y);
        // How many px of art make one px of board, so the used rect comes out
        // Wide of a tile - set when a marker is built, not here, because the tile
        // is the field's and this is the file's.
        _span = used.Size.X;
        art.GenerateMipmaps();
        _art = ImageTexture.CreateFromImage(art);
        return true;
    }

    private static float _span = 1.0f;

    /// <summary>What the art covers, in file px - so a check can say the foot sits
    /// inside it rather than trusting the arithmetic above.</summary>
    public static float Span => _span;

    /// <inheritdoc cref="Span"/>
    public static Vector2 Foot => _foot;

    private MeshInstance3D? _quad;

    /// <summary>Build the quad. The tile is the hex's own width in screen px and
    /// the two camera terms are the field's, handed in rather than read - see
    /// <c>PitArt.Build</c>, whose shape this is.</summary>
    public void Build(float tile, float rise)
    {
        if (!Loaded)
            return;
        float k = Wide * tile / Mathf.Max(_span, 1.0f);
        _quad = new MeshInstance3D
        {
            Mesh = Stage3D.Stem(_foot * k, _frame * k, rise),
            // Sorted by where the node is rather than by the middle of its own
            // box - the tanks' and the props' rule, and here it is the mine's own
            // point that has to decide.
            SortingUseAabbCenter = false,
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                AlbedoTexture = _art,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                TextureFilter =
                    BaseMaterial3D.TextureFilterEnum.LinearWithMipmaps,
                RenderPriority = Rung,
            },
            Visible = false,
        };
        AddChild(_quad);
    }

    /// <summary>Stand it on the board. <paramref name="spot"/> is board space -
    /// <see cref="Mines.At"/> plus the stage's origin - and <paramref name="lift"/>
    /// the ground's height there.</summary>
    public void Show(Vector2 spot, float lift, float squash, float rise)
    {
        if (_quad is null)
            return;
        Transform = Stage3D.Trunk(spot, lift, 0.0f, squash, rise);
        _quad.Visible = true;
    }

    public void Hide()
    {
        if (_quad is not null)
            _quad.Visible = false;
    }

    /// <summary>Whether this one is on the board - for the check, which cannot
    /// read a node's visibility off a picture.</summary>
    public bool Standing => _quad?.Visible == true;
}
