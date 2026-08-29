using System;
using System.Globalization;
using System.Collections.Generic;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// One cell with a brick wall generated on it, and a key that knocks it down.
///
/// Separate from the harness for <see cref="WoodBench"/>'s reason, which is
/// <c>flash_bench.sheet()</c>'s: what is judged here is what a wall looks like
/// and how it falls, and judging that inside <see cref="Main"/> means three
/// atlases, eighteen layers, sound, a panel and fifty-four cells of board on
/// which the wall would be one prop. Here the whole subject is one wall.
///
///     Godot_v4.7.2-stable_mono_win64.exe --path GodotTest res://Wall.tscn
///
/// <b>The ground under it is the harness's own.</b> <see cref="Stage3D"/> builds
/// the prism, the shader marches the sun over it, the camera is the board's. A
/// bench that drew its own ground would be judging the wall against a backdrop
/// nothing else on the board shares - and the first question about this wall is
/// whether it belongs on that ground. It stands a level up so that prism has
/// walls to show: see <c>SetRelief</c> in <see cref="_Ready"/>, and
/// <c>--flat</c> for the plane it used to be.
///
/// <b>It is the same wall as the Blender prop, built the other way round.</b>
/// Over there the ruin is modelled once and rendered to sprites; here it is a
/// seed. What that buys is on show: a different wall per seed, a wall that stands
/// on real terrain rather than on a tile baked flat, and no atlas at all. What it
/// costs is the thing that has to be re-earned rather than ported - the look was
/// measured against <c>Images/raw/wall.png</c>, and the shading model here is a
/// different one.
/// </summary>
public sealed partial class WallBench : SceneRoot
{
	/// <summary>Thirty frames, and never fewer than two - see the clamp in
	/// <see cref="ReadFlags"/> for why one is not a picture.</summary>
	public WallBench() : base(30) { }

	private static readonly string TerrainsRoot = AssetRoot.Terrains;
	private static readonly string SpritesRoot = AssetRoot.Sprites;

	/// <summary>The atlas the tile comes off. No tank is drawn - it is loaded for
	/// <see cref="AtlasSet.HexRect"/>, which every distance on this board is
	/// measured in, exactly as the wood bench loads it.</summary>
	private const string TileTag = "MTP";

	/// <summary>The middle of a three by three, because the wall's cell now has
	/// neighbours to throw rubble onto.</summary>
	public static readonly Vector2I Middle = new(1, 1);

	/// <summary>The cell and its six neighbours, and nothing else.
	///
	/// <b>One cell was right until the rubble was allowed off it.</b> A brick
	/// that leaves a lone hexagon falls past the board and disappears, which is
	/// not "it landed next door" - it is the same picture as a brick that was
	/// never simulated. The ring is the smallest board on which the thing that
	/// was asked for can be seen at all.
	///
	/// Named through <c>Step</c> rather than written out as offsets: which cell
	/// is next to which is the field's answer, and a second one is wrong on the
	/// odd columns only, which reads as the wall throwing rubble crooked.
	/// </summary>
	public static HashSet<Vector2I> Board(Vector2I mid)
	{
		var plot = new HashSet<Vector2I> { mid };
		// EdgeHeadings, never multiples of sixty: on a flat-top hexagon the six
		// edges face 30, 90, 150 and so on, and Step hands back the cell
		// unchanged for a heading that points at a corner. Asked with 0, 60, 120
		// it returns the middle six times and the board is one cell, which looks
		// exactly like a Plot that was ignored.
		foreach (int heading in HexField.EdgeHeadings)
			plot.Add(HexField.Step(mid, heading));
		return plot;
	}

	private HexField _field = null!;
	private Stage3D? _stage;
	private Camera2D _camera = null!;
	private Label? _hud;

	/// <summary>The wall on its cell, and everything about standing one there.
	///
	/// <b>Lifted out rather than kept here, and the bench is the poorer half of
	/// the split on purpose</b> - see <see cref="WallProp"/>. What is left in
	/// this file is what a bench is: the dials, the keys, the panel and the
	/// readout. Which recipe, which side and how hard are the caller's; what a
	/// wall on a cell <i>is</i> is the prop's.</summary>
	private WallProp? _prop;

	private WallStack? _wall => _prop?.Stack;
	private WallKit.Plan? _plan => _prop?.Plan;
	private float _scale => _prop?.Scale ?? 1.0f;

	private readonly WallKit.Recipe _recipe = new();

	/// <summary>What <c>walls.json</c> calls this bench's wall - the table
	/// <see cref="WallConfig"/> fills in, of which this is the one entry the
	/// tank bench does not lay.</summary>
	public const string WallName = "bench";
	/// <summary>How much of the side the wall takes, and how far out it stands
	/// - one number for both, see <see cref="WallKit.Fit"/>. 0.97 leaves about
	/// two pixels of tile between each end of the wall and the cell's vertex,
	/// which is the whole of what keeps it off the edge.</summary>
	private float _coverage = 0.97f;
	private WallFall.Shot _shot = WallFall.Shot.Mine;
	private WallRig? _rig => _prop?.Rig;
	private ControlPanel? _panel;
	private WallRig.Strike _strike = WallRig.Strike.Ram;
	private float _force = 1.0f;

	/// <summary>The top of the force dial.
	///
	/// <b>A dial has to be able to show too much, and three could not.</b> At
	/// three the wall comes down whichever shot is chosen and the top half of
	/// the travel says nothing new - so the number that matters, where a shot
	/// stops being a breach and starts being a demolition, was off the end of
	/// the scale rather than on it. Ten puts an AP round through the far side, a
	/// burst out to 2.6m, and a tank clean across the cell and off the other
	/// edge, and every one of those is a picture worth being able to reach.
	///
	/// It is a multiplier over the three shots' own numbers and never a force in
	/// its own right: what one setting buys is written under the slider, in the
	/// unit the chosen shot is measured in.</summary>
	public const float MostForce = 10.0f;

	/// <summary>The tops of the two size dials.
	///
	/// <b>Both are past what looks right, for the force dial's reason.</b> A dial
	/// that cannot show too much cannot show that the setting on it is the right
	/// one - so the height goes up to where a ruin becomes a tower that overruns
	/// its own cell on screen, and the thickness to where the leaves eat the room
	/// the mitre needs at a corner and the end bricks start disappearing into it.
	/// The second is the interesting one, and it is why <c>Mitred</c> is
	/// reported: thickness is the only setting that can take a wall apart at its
	/// corners rather than merely make it larger.</summary>
	public const int MostCourses = 16;
	public const int MostLeaves = 5;

	private bool _solved;
	/// <summary>Which flat side it comes from - and, since the wall stands on a
	/// side, which side that is.
	///
	/// <b>One dial, because a wall on a boundary is the boundary being
	/// crossed.</b> The two used to be independent, and could be: the prop was
	/// fitted along the hexagon's vertex axis in the middle of the cell, so two
	/// of the six bearings met its face and the other four took it end on -
	/// measured, 45-49 of 58 pieces broadside against 17-24 along. Standing it on
	/// a side ends that arrangement, and not by preference: the ram's travel is
	/// clamped at the cell's middle so that the tank is guaranteed to take the
	/// cell and guaranteed not to take the next one, and a wall on the far
	/// boundary is a metre past the end of that. Measured before the two were
	/// tied together: at force 1 the nose stops 2 m short of it, and only past
	/// force 3 does it arrive at all. Tying them keeps every side meaning the
	/// same thing, so the dial changes where the heap lands rather than whether
	/// anything happens - which is what it already changed for the ram.
	///
	/// The turntable is the free one, and it stays free: it turns the prop under
	/// a fixed sun and a fixed camera, which is how the other five sides are
	/// looked at.
	///
	/// 270 rather than 90 because the shooter is then on the camera's side, and
	/// because the wall then presents its outer face: from 90 the camera would be
	/// looking at the back leaf and the tank would break through towards it.
	/// What that costs is the apron, which has to fall into the cell and is
	/// therefore behind the wall from here - see <see cref="WallKit.Scatter"/>.
	/// </summary>
	private float _bearing = 270.0f;

	/// <summary>What the view opens on and what R comes back to.
	/// <c>--zoom</c> overrides it - see <see cref="SceneRoot.ZoomAt"/>.</summary>
	private float HomeZoom => ZoomAt ?? 2.4f;
	private int _frame;
	private bool _fellAtStart;
	private int _plinth = 1;
	private Vector2? _leftFrom;
	private bool _reported;
	private float _spin;
	private bool _turning;

	/// <summary>How fast the turntable goes, in degrees a second, and how far a
	/// key nudges it.
	///
	/// A sixth of a turn a second: nine seconds for the round trip, which is slow
	/// enough to read a face as it comes past and quick enough that nobody waits
	/// for the back. The nudge is a twelfth of a turn, so twelve of them come back
	/// to where they started exactly - a turntable that drifts off its own zero
	/// cannot be used to compare two sides.</summary>
	private const float TurnRate = 40.0f;
	private const float TurnStep = 30.0f;

	/// <summary>Which way the prop is turned, all in: the side it stands on plus
	/// the turntable on top of that.
	///
	/// <b>Everything that crosses between the board and the prop goes through
	/// this and never through the turntable alone.</b> The layout is built
	/// standing on its own +z side, so putting it on the board's side <i>b</i> is
	/// a turn of <c>b + 90</c> - the angle that carries <c>d(270)</c>, which is
	/// what local +z is, onto <c>d(b)</c>. Four places need it and they are all
	/// the same sentence: the shot's direction coming into the prop's frame, the
	/// ground triangles going the same way, the sun's run inside
	/// <c>WallStack</c>, and the heap measured against the cell. Give any one of
	/// them the bare turntable and it is right only while the bench sits on its
	/// opening side, which is the one state a bench is never looked at in.
	/// </summary>
	private float Lay => _prop?.Lay ?? 0.0f;

	/// <summary>Which of the six flat sides the shot comes from.
	///
	/// <b>The sides, never sixths of a circle.</b> A flat-top hexagon's edges
	/// face 30, 90, 150 and so on - <see cref="HexField.EdgeHeadings"/> - and a
	/// direction that is not one of those is a shot arriving through a corner,
	/// which is not a place a tank can stand or a gun can be. The board already
	/// says this in two other places: the harness gives a hit six bearings, and
	/// the rosette this bench stands on is built out of the same list.</summary>
	private int Side
	{
		get => SideOf(_bearing);
		set
		{
			// Locked once something is in the air, for the turntable's reason and
			// more so: this one moves the wall itself, not just the view of it.
			if (_rig is { Struck: true })
				return;
			_bearing = HexField.EdgeHeadings[
				Mathf.Clamp(value, 0, HexField.EdgeHeadings.Length - 1)];
			// The wall stands on this side, so changing it turns the prop - and
			// the solver has to be handed the ground again, because the triangles
			// it was given came through the old turn. Both in WallProp.Stand.
			_prop?.Stand(_bearing);
		}
	}

	/// <summary>Which of the six an arbitrary angle is nearest. Its own method
	/// because the flag snaps through it as well as the readout: an angle that
	/// only <i>displays</i> as a side while the shot goes somewhere else is the
	/// worst of the three possible states.</summary>
	private static int SideOf(float bearing)
	{
		int best = 0;
		float near = 360.0f;
		for (int k = 0; k < HexField.EdgeHeadings.Length; k++)
		{
			float d = Mathf.Abs(Mathf.AngleDifference(
				Mathf.DegToRad(bearing),
				Mathf.DegToRad(HexField.EdgeHeadings[k])));
			if (d < near)
			{
				near = d;
				best = k;
			}
		}
		return best;
	}

	/// <summary>Which way the round travels, in the prop's own frame.
	///
	/// <b>Reversed, because the bearing says where it comes from</b> - and both
	/// halves of that turn live in <see cref="WallProp.Arriving"/> now, because
	/// the tank bench needs the same one and two copies of it are two chances to
	/// be a quarter turn out.</summary>
	private Vector3 Into() => _prop?.Arriving(_bearing) ?? Vector3.Zero;


	private void ReadFlags()
	{
		// Before the switch, never after it: --seed and its four neighbours
		// write the same fields, and a flag is the more specific statement -
		// see WallConfig.
		var walls = WallConfig.Load();
		GD.Print(!walls.Loaded
				 ? $"wall: walls built-in ({walls.Error})"
				 : walls.Apply(WallName, _recipe)
				   ? "wall: walls json"
				   : $"wall: walls built-in (nothing named {WallName})");
		string[] args = OS.GetCmdlineUserArgs();
		for (int i = 0; i < args.Length; i++)
		{
			// The five every root here takes - see SceneRoot.
			if (ReadCommonFlag(args, ref i))
				continue;
			if (args[i] == "--spin" && i + 1 < args.Length
				&& float.TryParse(args[i + 1], System.Globalization.NumberStyles.Float,
								  System.Globalization.CultureInfo.InvariantCulture,
								  out float deg))
			{
				// Invariant culture, because this machine's is comma-decimal and a
				// bare TryParse quietly returns false on "22.5" - the flag then
				// does nothing and two captures come back byte identical, which
				// reads as an angle that changes nothing rather than as an angle
				// that never arrived.
				_spin = Mathf.DegToRad(deg);
				i++;
			}
			else if (args[i] == "--turntable")
				_turning = true;
			else if (args[i] == "--seed" && i + 1 < args.Length
					 && int.TryParse(args[i + 1], out int seed))
			{
				_recipe.Seed = seed;
				i++;
			}
			else if (args[i] == "--columns" && i + 1 < args.Length
					 && int.TryParse(args[i + 1], out int cols))
			{
				_recipe.Columns = Mathf.Clamp(cols, 2, 16);
				i++;
			}
			else if (args[i] == "--courses" && i + 1 < args.Length
					 && int.TryParse(args[i + 1], out int rows))
			{
				_recipe.Courses = Mathf.Clamp(rows, 1, MostCourses);
				i++;
			}
			else if (args[i] == "--sides" && i + 1 < args.Length
					 && int.TryParse(args[i + 1], out int runs))
			{
				_recipe.Sides = Mathf.Clamp(runs, 1, 6);
				i++;
			}
			else if (args[i] == "--leaves" && i + 1 < args.Length
					 && int.TryParse(args[i + 1], out int leaves))
			{
				_recipe.Leaves = Mathf.Clamp(leaves, 1, MostLeaves);
				i++;
			}
			else if (args[i] == "--coverage" && i + 1 < args.Length
					 && float.TryParse(args[i + 1], NumberStyles.Float,
									   CultureInfo.InvariantCulture, out float cov))
			{
				// Invariant and never the machine's: on a comma locale the plain
				// parse fails, the default stands, and two runs at two values come
				// back byte for byte identical - which reads as a flag that does
				// nothing rather than one that was not understood.
				_coverage = Mathf.Clamp(cov, 0.4f, 1.2f);
				i++;
			}
			else if (args[i] == "--fallen" && i + 1 < args.Length
					 && int.TryParse(args[i + 1], out int fell))
			{
				_recipe.Fallen = Mathf.Max(0, fell);
				i++;
			}
			else if (args[i] == "--chips" && i + 1 < args.Length
					 && int.TryParse(args[i + 1], out int chips))
			{
				// Zero is the useful value: the masonry on its own is the only
				// way to tell a defect in the courses from one in the apron, and
				// the two look alike at a distance.
				_recipe.Chips = Mathf.Max(0, chips);
				i++;
			}
			else if (args[i] == "--shot" && i + 1 < args.Length)
			{
				string want = args[++i];
				_shot = want == "ram" ? WallFall.Shot.Ram : WallFall.Shot.Mine;
				if (want != "ram" && want != "mine")
					GD.PushWarning($"--shot {want} is neither mine nor ram");
			}
			else if (args[i] == "--level" && i + 1 < args.Length
					 && int.TryParse(args[i + 1], out int level))
			{
				_plinth = Mathf.Clamp(level, 0, 4);
				i++;
			}
			else if (args[i] == "--flat")
				_plinth = 0;
			else if (args[i] == "--strike" && i + 1 < args.Length)
			{
				string want = args[++i];
				_strike = want switch
				{
					"he" => WallRig.Strike.He,
					"ap" => WallRig.Strike.Ap,
					_ => WallRig.Strike.Ram,
				};
				if (want is not ("ram" or "he" or "ap"))
					GD.PushWarning($"--strike {want} is none of ram, he, ap");
			}
			else if (args[i] == "--bearing" && i + 1 < args.Length
					 && float.TryParse(args[i + 1], NumberStyles.Float,
									   CultureInfo.InvariantCulture, out float from))
			{
				// Snapped, so the flag cannot quietly put a round through a
				// corner: the six are where a tank can stand, and an angle
				// between two of them is not a weaker version of either.
				Side = SideOf(from);
				i++;
			}
			else if (args[i] == "--force" && i + 1 < args.Length
					 && float.TryParse(args[i + 1], NumberStyles.Float,
									   CultureInfo.InvariantCulture, out float force))
			{
				_force = Mathf.Clamp(force, 0.0f, MostForce);
				i++;
			}
			else if (args[i] == "--solved")
				_solved = true;
			// The rubble stays where it landed. A capture of the heap is what
			// every one of this bench's numbers is read off, so being able to
			// stop it clearing itself is the A/B the effect is judged by.
			else if (args[i] == "--no-crumble")
				WallRig.Crumbles = false;
			// The solver's own boxes, drawn over the picture. Off by default and
			// a flag rather than a key for WallHulls' reason: a debug frame that
			// wandered into an A/B would be measuring itself, and a capture is
			// the proof, so putting one in must not need a hand on the mouse.
			else if (args[i] == "--hulls")
				WallHulls.Shown = true;
			// The ram lets masonry go and never pushes it, the way it used to -
			// the A/B the shunt is judged by.
			else if (args[i] == "--no-shunt")
				WallRig.Shunts = false;
			// The wall only loses what the strike itself reached, the way it used
			// to - the A/B a breach is judged by.
			else if (args[i] == "--no-breach")
				WallRig.Breaches = false;
			// The blast crosses sections again, the way it used to - the A/B a
			// segment is judged by.
			else if (args[i] == "--wide-blast")
				WallRig.Segmented = false;
			// Every piece takes the depth buffer, the way it used to - the A/B
			// rubble giving that up is judged by.
			else if (args[i] == "--solid-rubble")
				WallStack.Dressed = false;
			else if (args[i] == "--fell")
				_fellAtStart = true;
		}
		SettleForProof(CapturePath is not null);
		// Never the first frame. The viewport has nothing in it until something
		// has been drawn into it, so frame 1 comes back one flat colour - a
		// capture that succeeds, writes a file and shows nothing, which is the
		// worst way for a proof to fail. Measured: 1 unique colour at frame 1,
		// 15 126 from frame 2 on. Clamped here rather than in the base, because
		// it is this bench that starts empty.
		CaptureAt = Mathf.Max(2, CaptureAt);
	}

	public override void _Ready()
	{
		ReadFlags();

		TerrainSet terrain = TerrainSet.Load(TerrainsRoot);
		GD.Print("wall: terrain " + terrain.Note);
		// Said out loud every run, because a physics engine that is not the one
		// the numbers were tuned against is invisible: the collapse still
		// happens, it is just a different collapse, and every jolt_ setting in
		// project.godot is quietly doing nothing.
		GD.Print("wall: physics "
				 + ProjectSettings.GetSetting("physics/3d/physics_engine",
											  "DEFAULT")
				 + ", slop " + ProjectSettings.GetSetting(
					 "physics/jolt_physics_3d/simulation/penetration_slop", "?"));

		AtlasSet? tile = null;
		try
		{
			tile = AtlasSet.Load(SpritesRoot, TileTag);
			GD.Print($"wall: tile off {TileTag}, "
					 + $"{tile.HexRect.Size.X}x{tile.HexRect.Size.Y}");
		}
		catch (Exception e)
		{
			// Said out loud rather than left to be a blank window: without a tile
			// there is no distance on this board at all, so the stage returns
			// before it builds and the wall has no cell to be a fraction of.
			GD.PushWarning($"wall: no {TileTag} atlas ({e.Message}) - no board");
		}

		_field = new HexField
		{
			Atlas = tile,
			Terrain = terrain,
			Paint = TerrainSet.Plain,
			Columns = 3,
			Rows = 3,
			Plot = Board(Middle),
		};
		// Stand the cell on a level, because a level is what gives it sides.
		//
		// Stage3D extrudes a prism from a cell's top face down to the board's
		// floor and skips the walls when the two are the same height - so a lone
		// cell on the datum is one hexagon of ground and nothing else, which is
		// the flat sprite this bench used to draw. The board's floor is always
		// the datum (HexField.LevelRange starts at zero whatever the map says),
		// so one level up is all it takes and the wall rides with it: the anchor
		// in Build() already reads LevelAt.
		//
		// A level rather than a thickness of my choosing, for the reason nothing
		// here is in pixels: how tall a hex stands is the board's number, and a
		// second one measured against the art would be a wall standing on a cell
		// no tank could ever climb.
		// The whole rosette, not just the middle: a plateau shows its walls at
		// the rim and lets the rubble land next door at the height it left,
		// whereas a single raised cell is a plinth with a drop all round it.
		int[] levels = new int[9];
		for (int k = 0; k < 9; k++)
			levels[k] = _plinth;
		_field.SetRelief(_plinth > 0 ? levels : null);
		AddChild(_field);

		_camera = new Camera2D
		{
			Zoom = new Vector2(HomeZoom, HomeZoom),
			Enabled = true,
			Position = ViewHome,
		};
		AddChild(_camera);

		_stage = new Stage3D { Field = _field, Origin = Vector2.Zero, Eye = _camera };
		AddChild(_stage);
		_field.ShowField = false;

		if (tile is not null)
			Build();

		if (!NoUi)
		{
			var layer = new CanvasLayer();
			AddChild(layer);
			Panel(layer);
			_hud = new Label { Position = new Vector2(16.0f, 12.0f) };
			_hud.AddThemeColorOverride("font_color", new Color(0.92f, 0.95f, 1.0f));
			_hud.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.85f));
			_hud.AddThemeConstantOverride("outline_size", 5);
			layer.AddChild(_hud);
		}

		if (_fellAtStart)
			Fell();
	}

	/// <summary>Lay a wall, fit it to the cell, and stand it up.
	///
	/// The order is the point: fitting is a multiply over the finished layout,
	/// because nothing in <see cref="WallKit"/> is an absolute length. The Blender
	/// prop needs two builds for this, and got a different wall out of the second
	/// one for a year before the last absolute number was found in it.</summary>
	private void Build()
	{
		if (_field.Atlas is null || _stage is null)
			return;
		_lockSaid = false;
		if (_prop is null)
		{
			_prop = new WallProp
			{
				Field = _field,
				Stage = _stage,
				Cell = Middle,
				Recipe = _recipe,
				// The board's own tank for the ram to arrive in. This bench has
				// no Vehicle at all, which is the whole reason the sprite is
				// borrowed - see WallStack.Tank.
				Borrow = _field.Atlas,
			};
			AddChild(_prop);
		}
		_prop.Coverage = _coverage;
		_prop.Solved = _solved;
		_prop.Bearing = _bearing;
		_prop.Spin = _spin;
		_prop.Build();
		if (_plan is null)
			return;
		float radius = _field.Atlas.HexRect.Size.X * 0.5f;

		GD.Print($"wall: seed {_recipe.Seed}, fit {_scale:F4} -> {_plan.Note()}");
		GD.Print($"wall: plot {_field.Plot?.Count ?? 0} cells, "
				 + $"tris {(_stage?.GroundTriangles().Count ?? 0) / 3}, "
				 + $"awake {_rig?.Awake ?? -1}");
		if (_plan.Overlap * radius > Grit)
			// Said out loud, because a wall that intersects itself is the one
			// defect a still picture cannot show - it looks correct and comes
			// apart the moment anything touches it.
			GD.PushWarning($"wall: seed {_recipe.Seed} overlaps by "
						   + $"{_plan.Overlap * radius:F2}px ({_plan.OverlapPair})");
	}

	/// <summary>The side panel: what hits the wall, how hard, from where, and
	/// the button that does it.
	///
	/// <b>The harness's own panel, not a second one.</b> <see cref="ControlPanel"/>
	/// takes a heading and a handful of rows and knows nothing about tanks, so
	/// there was nothing to port - and the alternative, a panel of this bench's
	/// own, would be a second answer to how a row is wired, how it stays in step
	/// and what takes keyboard focus. Every row here is the same pair of
	/// delegates onto the field a key already writes, so <c>S</c> and the
	/// dropdown cannot disagree.
	///
	/// <b>Nothing is added to <c>panel.json</c>, and that is deliberate.</b> The
	/// file is checked against the harness's panel in both directions, so an
	/// entry for a row only this bench builds would fail that check as a
	/// description of nothing. These rows carry their own captions and take the
	/// built-in fallback, which is what <see cref="PanelText.Title"/> is for.
	///
	/// The corner readout stays. In the harness the panel replaced it, because
	/// it repeated it; here it is the only figure visible while the panel is
	/// shut, and every number quoted about this wall was read off it.</summary>
	private void Panel(CanvasLayer layer)
	{
		_panel = new ControlPanel();
		_panel.Prepare();

		// Opened, not collapsed like the harness's. That rule is about fifty
		// rows in one column; here there are nine in three groups, and the group
		// the bench exists for should not need a click to be found.
		_panel.Heading("wall.strike", "strike");
		_panel.Choice("wall.strike.kind", "what hits it",
					  new[] { "ram - a tank drives in", "HE - burst on the face",
							  "AP - a round straight through" },
					  () => (int)_strike,
					  i => _strike = (WallRig.Strike)Mathf.Clamp(i, 0, 2));
		_panel.Slide("wall.strike.force", "force", 0.0, MostForce, 0.05,
					 () => _force, v => _force = (float)v, "x",
					 () => WallRig.Costs(_strike, _force));
		_panel.Radio("wall.strike.side",
					 () => $"from {_bearing:F0} deg - the wall stands on "
						   + $"side {Side + 1} of 6",
					 System.Array.ConvertAll(HexField.EdgeHeadings, d => $"{d}"),
					 () => Side, i => Side = i);
		_panel.Press("wall.strike.go", "Action", Fell);
		_panel.Readout("wall.strike.state", () =>
			_rig is null ? "solved flights, no solver"
			: !_rig.Struck ? "standing, nothing fired"
			: $"{_rig.Clock:F2}s, {_rig.Awake} moving, {_rig.Loose} let go"
			  + (_rig.Broken > 0 ? $", {_rig.Broken} breached" : ""));
		_panel.Toggle("wall.strike.shunt", "the ram shoves what it frees",
					  () => WallRig.Shunts, on => WallRig.Shunts = on);
		_panel.Toggle("wall.strike.breach", "a breach takes the whole side",
					  () => WallRig.Breaches, on => WallRig.Breaches = on);
		_panel.Toggle("wall.strike.crumble", "fallen pieces clear",
					  () => WallRig.Crumbles, on => WallRig.Crumbles = on);
		_panel.Readout("wall.strike.clear", () =>
			$"lies {WallRig.Linger:F1}s, goes over {WallRig.Crumble:F1}s"
			+ (_rig is null ? "" : $" - {_rig.Crumbled} gone"));

		_panel.Heading("wall.stack", "wall");
		// How much wall there is, in the three numbers that are the wall's own
		// and not the cell's: how far round it goes, how high, how thick. Each
		// relays the whole prop, because every one of them changes the layout
		// rather than the view of it - and each is locked while pieces are in
		// the air, for the turntable's reason twice over.
		_panel.Slide("wall.stack.sides", "sides", 1.0, 6.0, 1.0,
					 () => _recipe.Sides,
					 v => Relay(() => _recipe.Sides = (int)Mathf.Round(v)), "",
					 () => SidesNote());
		_panel.Slide("wall.stack.courses", "height", 1.0, MostCourses, 1.0,
					 () => _recipe.Courses,
					 v => Relay(() => _recipe.Courses = (int)Mathf.Round(v)),
					 " courses", () => CoursesNote());
		_panel.Slide("wall.stack.leaves", "thickness", 1.0, MostLeaves, 1.0,
					 () => _recipe.Leaves,
					 v => Relay(() => _recipe.Leaves = (int)Mathf.Round(v)),
					 " leaves", () => LeavesNote());
		_panel.Readout("wall.stack.plan", () =>
			_plan is null ? "no board"
			: $"seed {_recipe.Seed}: {_plan.Blocks.Count} pieces, "
			  + $"{_plan.Courses} courses"
			  + (_plan.Mitred > 0 ? $", {_plan.Mitred} mitred" : ""));
		_panel.Readout("wall.stack.pile", () =>
		{
			if (_rig is null)
				return "-";
			(float reach, float top, int off, _) = _rig.Pile(Lay);
			return $"reach {reach:F2}, top {top:F2}, {off} off the cell";
		});
		_panel.PressPair("wall.stack.seed", "next seed", () =>
		{
			_recipe.Seed++;
			Build();
			_camera.Position = ViewHome;
		}, "reset", Reset);

		_panel.Heading("wall.view", "view");
		_panel.Slide("wall.view.turn", "turntable", 0.0, 330.0, TurnStep,
					 () => Mathf.RadToDeg(_spin),
					 v => Turn(Mathf.DegToRad((float)v)), "deg",
					 () => _rig is { Struck: true }
						 ? "locked - the bodies are in the prop's frame"
						 : "turns the prop, never the camera");
		_panel.Toggle("wall.view.spin", "keep turning", () => _turning,
					  on => _turning = on);
		_panel.Toggle("wall.view.hulls", "draw the colliders", () => WallHulls.Shown,
					  on => WallHulls.Shown = on);

		// Both subjects open. The harness's rule - everything collapsed - is
		// about fifty rows in one column; here there are twelve in three groups,
		// and the bench now has two things it exists for: what the wall is, and
		// what hits it. A group that has to be found by clicking is a group
		// nobody compares against.
		_panel.Expand("wall.strike", true);
		_panel.Expand("wall.stack", true);
		layer.AddChild(_panel);
		_panel.AddHandle();
	}

	/// <summary>Hand the laid wall to the solver, standing and asleep.
	///
	/// <b>The ground comes over in the prop's own units, spin and all.</b> The
	/// bodies live in world space with no parent transform over them - the rule
	/// the Blender prop states as "Wall.World has to sit at the origin while the
	/// physics is on", because a simulation writes world transforms and a moving
	/// parent lays its own matrix over them. Here the prop's frame is what the
	/// bodies are in, so the board has to be brought into it: turned by the spin,
	/// shifted to the anchor, divided by the radius.
	///
	/// Which is also why the turntable locks once the wall has been struck. Turn
	/// it while pieces are in the air and you have turned the world under them.
	/// </summary>
	private void Rig() => _prop?.Raise();

	/// <summary>A tenth of a pixel, under which two bricks are apart.
	///
	/// <b>The threshold is the point, not the tidiness.</b> The separating-axis
	/// test works in fractions of a cell over fifteen axes and comes back with
	/// float noise rather than a clean zero, so a guard written as "&gt; 0" fires
	/// on a wall that is provably apart and prints "overlaps by 0,00px" - which
	/// is what it did on every seed. A guard that cries at nothing stops being
	/// read, and then it is not there for the seed that really does overlap.
	/// The same argument as the Blender build's worst_overlap, which had to be
	/// satisfiable before it was worth reporting.</summary>
	private const float Grit = 0.1f;

	private void Fell()
	{
		if (_wall is null || _plan is null)
			return;
		if (_rig is not null)
		{
			if (_rig.Struck)
			{
				// A second shot into rubble is a different experiment, and the
				// one being run here is what one shot does. Re-lay first.
				Build();
				_camera.Position = ViewHome;
			}
			_turning = false;
			_reported = false;
			_prop!.Fire(_strike, Into(), _force);
			return;
		}
		// The same coverage the wall was fitted to, so the rubble is allowed
		// exactly the cell the standing prop was allowed and not a hand's width
		// more. One number, asked twice.
		_reported = false;
		_prop!.Fell(_shot);
	}

	/// <summary>Change what the wall is, and stand the new one up.
	///
	/// <b>Every size is a relay and never a nudge</b>, because none of them is a
	/// property of the finished prop: how many sides it runs along, how many
	/// courses high and how many leaves thick are all read while the courses are
	/// being laid, so there is nothing to move afterwards. Locked while pieces
	/// are in the air for the same reason the turntable is, and more so - this
	/// replaces the bodies rather than turning the world under them.
	///
	/// The view comes home with it: a wall that just grew four courses has its
	/// crest off the top of the window otherwise, which reads as the setting
	/// having broken something.</summary>
	private void Relay(Action change)
	{
		if (_rig is { Struck: true })
		{
			// Refused out loud, once per wall - the tank bench's line, for its
			// reason: a dial that is silently held reads as a dial that does
			// nothing.
			if (!_lockSaid)
				GD.Print("wall dials are locked while the wall lies - "
						 + "R lays a new one");
			_lockSaid = true;
			return;
		}
		change();
		Build();
		_camera.Position = ViewHome;
	}

	/// <summary>Whether the locked-dials line has been printed for this wall.
	/// Once per wall, not per turn of the dial.</summary>
	private bool _lockSaid;

	/// <summary>How far round the cell the wall goes, in the unit that is not a
	/// count: a side is one cell radius, so the run is the count times the
	/// coverage, and six closes the ring.</summary>
	private string SidesNote()
	{
		int n = Mathf.Clamp(_recipe.Sides, 1, 6);
		float run = n * _coverage;
		return $"{run:F2}R of run, {run * WallRig.MetresPerCell:F1} m, "
			   + (n == 6 ? "a closed ring" : n == 1 ? "no corner"
													: $"{n - 1} corners");
	}

	/// <summary>What the height dial buys, in the units the picture is in.
	/// Measured off what got built rather than off the count asked for: the
	/// profile runs out of bricks before it runs out of courses, so the two part
	/// company at the top of the dial and the built one is the honest number.
	/// </summary>
	private string CoursesNote()
	{
		if (_plan is null)
			return "no board";
		float radius = _field.Atlas?.HexRect.Size.X * 0.5f ?? 1.0f;
		return $"{_plan.Courses} laid, {_plan.Top * radius:F0}px, "
			   + $"{_plan.Top:F2} of a cell";
	}

	/// <summary>What the thickness dial buys, and what it costs at a corner.
	///
	/// <b>The second half is the point.</b> Thickness is the one size that a
	/// corner has to pay for: two slabs meeting at 120 degrees cross behind their
	/// vertex by the wall's own thickness, so every leaf added is another brick's
	/// worth cut off both ends of every side that has a neighbour. On one side
	/// there is no neighbour and it costs nothing, which is why the note says
	/// which case it is in.</summary>
	private string LeavesNote()
	{
		if (_plan is null)
			return "no board";
		float radius = _field.Atlas?.HexRect.Size.X * 0.5f ?? 1.0f;
		float thick = (WallKit.BrickDeep + (_recipe.Leaves - 1)
					   * (WallKit.BrickDeep + WallKit.Perpend)) * _scale;
		return $"{thick * radius:F0}px, {thick * WallRig.MetresPerCell:F2} m"
			   + (_recipe.Sides > 1
				   ? $" - {_plan.Mitred} mitred, {_plan.MitreCut * radius:F0}px cut"
				   : " - no corner to pay for");
	}

	/// <summary>Put the bench back as it opened. What R does, and what the
	/// panel's second button does - one method, because two would drift and the
	/// one that drifted would be the one nobody pressed.
	///
	/// The turntable comes home with everything else: a reset that leaves the
	/// wall at 200 degrees is not the bench as it opened.</summary>
	private void Reset()
	{
		_turning = false;
		// Written straight rather than through Turn, which is locked once
		// anything is in the air: that lock is there so the world cannot be
		// turned under moving bodies, and after a strike it left R with a wall
		// still on its old angle - the one state a reset must not keep. Build
		// stands a new prop at this spin, so there is nothing to turn.
		_spin = 0.0f;
		// A new wall gets a new settle line: left set, the collapse after a
		// reset prints nothing and reads as a wall that never came apart.
		_reported = false;
		Build();
		_camera.Position = ViewHome;
		_camera.Zoom = new Vector2(HomeZoom, HomeZoom);
	}

	/// <summary>Put the turntable somewhere, wrapped.
	///
	/// Wrapped rather than left to grow, because the angle is what the readout
	/// prints and "740 degrees" is not an answer to "which side am I looking at".
	/// </summary>
	private void Turn(float radians)
	{
		// Locked once something is in the air: the bodies are in the prop's
		// frame, so turning the prop now is turning the world under them.
		if (_rig is { Struck: true })
			return;
		_spin = Mathf.Wrap(radians, 0.0f, Mathf.Tau);
		_prop?.Turn(_spin);
	}

	public override void _Process(double delta)
	{
		if (_turning)
			// The bench's fixed step when there is one, so a turntable capture is
			// the same picture twice - the rule FrameClock.FixedStep exists for.
			Turn(_spin + Mathf.DegToRad(TurnRate) * (float)(FrameClock.FixedStep ?? delta));

		if (_hud is not null)
			_hud.Text = Note();

		_frame++;
		// Said once, when the last piece has stopped: the rubble is judged on
		// whether it lies down inside the cell, and a heap of sixty bricks on a
		// cell 109px tall reads as a mass either way.
		if (_rig is { Struck: true, Ramming: false } && !_reported
			&& _rig.Clock > 0.6f && _rig.Awake == 0)
		{
			// Nothing moving is the only end a live collapse has - there is no
			// baked length to wait for, which is the price of not baking.
			_reported = true;
			(float reach, float top, int off, _) = _rig.Pile(Lay);
			GD.Print($"wall: {_strike} from {_bearing:F0} settled at "
					 + $"{_rig.Clock:F2}s - reach {reach:F3}, top {top:F3} "
					 + $"of a cell, {off} of {_rig.Count} off the cell");
		}
		if (_wall is { Falling: true } && !_reported && _wall.Clock >= _wall.Length)
		{
			_reported = true;
			(float reach, float top, int aloft, int layers) = _wall.Pile();
			GD.Print($"wall: settled at {_wall.Length:F2}s - reach {reach:F3}, "
					 + $"top {top:F3} of a cell, {aloft} aloft, {layers} deep");
		}
		if (_rig is not null && CapturePath is not null && _frame == CaptureAt)
		{
			(float worst, float median, int moved) = _rig.Drift();
			GD.Print($"wall: drift at frame {_frame} - worst {worst:F3}m, "
					 + $"median {median:F3}m, {moved} of {_rig.Count} moved, "
					 + $"{_rig.Awake} awake");
		}
		if (CapturePath is not null && _frame >= CaptureAt)
		{
			Capture(CapturePath);
			GetTree().Quit();
		}
	}

	/// <summary>
	/// What the wall is, in the numbers that can fail.
	///
	/// The guard first, because a generated wall has one failure a picture cannot
	/// show: pieces inside each other. Then how much of the cell it takes, which
	/// is the other thing a seed can get wrong.
	/// </summary>
	private string Note()
	{
		if (_plan is null)
			return "no board";
		float radius = _field.Atlas?.HexRect.Size.X * 0.5f ?? 1.0f;
		string fall = "standing";
		if (_rig is not null)
		{
			(float reach, float top, int off, int awake) = _rig.Pile(Lay);
			fall = $"{_strike} x{_force:F2} from {_bearing:F0} deg  "
				   + (_rig.Struck ? $"{_rig.Clock:F2}s, {awake} moving  " : "ready  ")
				   + $"reach {reach:F2}, top {top:F2}, {off} off the cell"
				   // How much of the heap has cleared itself. Printed because a
				   // cleared cell and a shot that never moved anything are the
				   // same empty picture - and because reach and top go on
				   // describing bodies nobody can see any more, which is worth
				   // being able to see said.
				   + (_rig.Crumbled > 0 ? $", {_rig.Crumbled} gone" : "");
		}
		else if (_wall is { Falling: true })
		{
			(float reach, float top, int aloft, int layers) = _wall.Pile();
			fall = $"{_shot} {_wall.Clock:F2}/{_wall.Length:F2}s  "
				   + $"pile reach {reach:F2}, top {top:F2}, "
				   + $"{aloft} aloft, {layers} deep";
		}
		return $"seed {_recipe.Seed}  {_plan.Bricks} bricks, "
			   + $"{_plan.Courses} courses, {_plan.Sides} sides, "
			   + $"{_plan.Leaves} leaves, {_plan.Blocks.Count} pieces"
			   + (_plan.Mitred > 0 ? $", {_plan.Mitred} mitred" : "") + "\n"
			   + $"fit {_scale:F3}, reach {_plan.Reach:F3} of the cell, "
			   + $"overlap {_plan.Overlap * radius:+0.00;-0.00;0.00}px\n"
			   + $"{fall}\n"
			   + $"on side {_bearing:F0}, level {_plinth}, "
			   + $"turned {Mathf.RadToDeg(_spin):F0} deg"
			   + (_turning ? ", turning" : "") + "\n"
			   + "SPACE fires, S shot, B bearing, N next seed, R resets\n"
			   + "1/2 sides, 3/4 height, 5/6 thickness\n"
			   + "TAB opens the panel, left drag turns it, Q/E step, T turntable\n"
			   + "middle drag pans, wheel zooms, F12 shoots";
	}

	/// <summary>Where the view sits: the cell, raised by half the wall on it.
	/// Measured off what got built rather than set to a number, for the wood
	/// bench's reason - a view centred on the ground puts the cell in the middle
	/// of the window and the top course off the edge of it.</summary>
	private Vector2 ViewHome
	{
		get
		{
			Vector2 flat = _field.CellCentre(Middle);
			float top = (_plan?.Top ?? 0.0f)
						* (_field.Atlas?.HexRect.Size.X * 0.5f ?? 0.0f);
			return flat - new Vector2(0.0f, top * _field.Squash * 0.5f);
		}
	}

	public override void _UnhandledInput(InputEvent @event)
	{
		if (@event is InputEventKey { Pressed: true, Echo: false } key)
		{
			switch (key.Keycode)
			{
				case Key.Space:
					Fell();
					return;
				case Key.N:
					_recipe.Seed++;
					Build();
					_camera.Position = ViewHome;
					return;
				case Key.S:
					if (_rig is not null)
					{
						_strike = _strike switch
						{
							WallRig.Strike.Ram => WallRig.Strike.He,
							WallRig.Strike.He => WallRig.Strike.Ap,
							_ => WallRig.Strike.Ram,
						};
						return;
					}
					_shot = _shot == WallFall.Shot.Mine
						? WallFall.Shot.Ram : WallFall.Shot.Mine;
					if (_wall is { Falling: true })
						Fell();
					return;
				case Key.B:
					// The next flat side, taken off the list rather than by
					// adding sixty: adding stays on the six only while the value
					// started on them, which is a key that is right because of
					// where the field was initialised.
					Side = (Side + 1) % HexField.EdgeHeadings.Length;
					return;
				case Key.Key1:
				case Key.Key2:
					// The three sizes on pairs of digits, because they are the
					// only settings here that count rather than choose - and a
					// count wants a step in each direction, not a cycle that has
					// to be walked all the way round to come back one.
					Relay(() => _recipe.Sides =
						Mathf.Clamp(_recipe.Sides + (key.Keycode == Key.Key2 ? 1 : -1),
									1, 6));
					return;
				case Key.Key3:
				case Key.Key4:
					Relay(() => _recipe.Courses =
						Mathf.Clamp(_recipe.Courses + (key.Keycode == Key.Key4 ? 1 : -1),
									1, MostCourses));
					return;
				case Key.Key5:
				case Key.Key6:
					Relay(() => _recipe.Leaves =
						Mathf.Clamp(_recipe.Leaves + (key.Keycode == Key.Key6 ? 1 : -1),
									1, MostLeaves));
					return;
				case Key.R:
					Reset();
					return;
				case Key.Tab:
					_panel?.Flip();
					return;
				case Key.Q:
					Turn(_spin - Mathf.DegToRad(TurnStep));
					return;
				case Key.E:
					Turn(_spin + Mathf.DegToRad(TurnStep));
					return;
				case Key.T:
					_turning = !_turning;
					return;
				case Key.F12:
					Capture(AssetRoot.Out + "/wall_" + _recipe.Seed + ".png");
					return;
			}
		}

		// Left drag turns it. The left button does nothing else on this bench -
		// there is one prop and it is already selected - and dragging the thing
		// you are inspecting is how a turntable is expected to work. Horizontal
		// only: the camera's elevation is the board's and does not move.
		if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Left } left)
		{
			_leftFrom = left.Pressed ? GetGlobalMousePosition() : null;
			if (left.Pressed)
				_turning = false;
			return;
		}
		if (@event is InputEventMouseMotion drag
			&& (drag.ButtonMask & MouseButtonMask.Left) != 0)
		{
			// A screen width is a turn and a half, which puts a face change inside
			// a comfortable wrist movement at any zoom - the drag is in screen
			// pixels on purpose, because the hand is.
			Turn(_spin + drag.Relative.X * Mathf.Tau * 1.5f
						 / Mathf.Max(GetViewportRect().Size.X, 1.0f));
			return;
		}

		if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Middle } middle)
		{
			// The same two-gestures-one-button arrangement the harness and the
			// wood bench use, resolved on release with the same slack, so a pan
			// across the cell does not fire the charge - SceneRoot.MiddleTapped.
			if (MiddleTapped(middle, out Vector2 _))
				Fell();
			return;
		}
		if (@event is InputEventMouseMotion motion
			&& (motion.ButtonMask & MouseButtonMask.Middle) != 0)
		{
			_camera.Position -= motion.Relative / _camera.Zoom;
			return;
		}
		if (@event is InputEventMouseButton { Pressed: true } wheel)
		{
			float factor = wheel.ButtonIndex switch
			{
				MouseButton.WheelUp => 1.1f,
				MouseButton.WheelDown => 1.0f / 1.1f,
				_ => 1.0f,
			};
			if (factor != 1.0f)
			{
				float z = Mathf.Clamp(_camera.Zoom.X * factor, 0.25f, 8.0f);
				_camera.Zoom = new Vector2(z, z);
			}
		}
	}

}
