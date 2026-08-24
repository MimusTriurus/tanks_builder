using System;
using System.Collections.Generic;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// Where a tank's feet are on the board: the contact point between two cells,
/// the cells that patch covers, how high each end of the hull counts as
/// standing on a step, and the screen line the stage splits it along.
///
/// <b>Lifted out of <see cref="Main"/> whole, for <see cref="TankTick"/>'s
/// reason.</b> Every one of these is a function of a <see cref="HexField"/> and
/// a pair of cells - no node, no frame, no switch - and they sat on the harness
/// scene, so the tick that drives a tank reached through a <c>Node2D</c> to ask
/// where the ground was. A bench with one tank on it needs the same answers and
/// should not have to load a harness to get them.
///
/// <b>Beside <see cref="HexField"/> rather than inside it, and the split is the
/// question each answers.</b> The field is the grid: which cells there are, how
/// high each stands, which are passable. This is what a <i>vehicle</i> standing
/// on that grid is entitled to - the crown it rides, the cover it earns, the
/// patch it occupies - and none of it is true of a tree or a wall.
/// </summary>
public static class Footing
{
	/// <summary>The contact point itself, in the field's own space and with no tank
	/// in it: the flat line between the two centres, dropped by the ground's height
	/// along it.
	///
	/// A static of the field for <see cref="StepHeights"/>'s reason - the check has
	/// to be able to ask the same question. It asks it of the board's own faces
	/// (<see cref="HexField.TopAtPoint"/> reads the corner planes, not the edge
	/// means), so that "the tank is driven on the ground" is two descriptions
	/// agreeing rather than one restating itself.</summary>
	public static Vector2 GroundBetween(HexField field, Vector2I from,
										Vector2I onto, float done) =>
		field.FlatAnchor(from.X, from.Y).Lerp(field.FlatAnchor(onto.X, onto.Y),
											  Mathf.Clamp(done, 0.0f, 1.0f))
		+ field.CentreOffset
		- new Vector2(0.0f, field.SurfaceBetween(from, onto, done));

	/// <summary>
	/// The cells one contact patch covers: the point it stands on and six probes
	/// a keep-out radius around it, all in flat space.
	///
	/// Named and static so the flat-space rule above can be checked rather than
	/// looked at - the same reason <see cref="Stage3D.Covers"/> and
	/// <see cref="Stage3D.Overlook"/> are. It is the whole of the failure: a
	/// picker answering a screen point is right about the picture and wrong about
	/// the ground, and both answers are cells on the board, so nothing downstream
	/// can tell them apart.
	///
	/// The centre is clamped and the rim is not, and that is
	/// <see cref="HexField.CellUnder"/> against
	/// <see cref="HexField.FlatCellAt"/>: a tank is always somewhere, a probe
	/// stepping off the board is nowhere.
	/// </summary>
	internal static IEnumerable<Vector2I> Patch(HexField field, Vector2 at,
											   float lift, double reach,
											   double squash)
	{
		yield return field.CellUnder(new Vector2(at.X, at.Y + lift));
		for (int k = 0; k < 6; k++)
		{
			double a = Math.PI * k / 3.0;
			var rim = new Vector2(
				at.X + (float)(Math.Cos(a) * reach),
				at.Y + (float)(Math.Sin(a) * reach * squash) + lift);
			Vector2I cell = field.FlatCellAt(rim);
			if (field.InBounds(cell))
				yield return cell;
		}
	}

	/// <summary>The raised ground a climbing tank passes beside: of the two
	/// cells adjacent to both ends of the leg, the one nearer the camera - the
	/// only one whose rim can cover the tank. Null on a board edge. Found by
	/// adjacency rather than heading arithmetic, because the heading numbers do
	/// not go round the ring in steps of sixty - 330 is down-right and 90 is
	/// straight down - and a +-60 guess picked the cell straight behind the
	/// leg, which laid the seam across the hull instead of along the boundary.
	/// The self-test only half-caught that one: it made the same guess, and
	/// the two wrongs agreed - which is why it now asks this method rather
	/// than its own copy.</summary>
	public static Vector2I? SeamFlank(HexField field, Vector2I cell,
		Vector2I next)
	{
		Vector2I? best = null;
		foreach (int h in HexField.EdgeHeadings)
		{
			Vector2I cand = HexField.Step(cell, h);
			if (cand == next || !field.InBounds(cand)
				|| HexField.HeadingTo(cand, next) < 0)
				continue;
			if (best is null
				|| field.GroundRow(cand) > field.GroundRow(best.Value))
				best = cand;
		}
		return best;
	}

	/// <summary>
	/// The screen-space line along which the stage splits a climbing tank's
	/// depth, as <c>(a, b, c)</c> with <c>a*x + b*y &gt;= c</c> the mounted
	/// cell's side: the shared edge between the cell being mounted and the
	/// raised flanking neighbour nearest the camera, extended.
	///
	/// The edge and nothing simpler, because the simpler lines were tried and
	/// each left a bite. A vertical through the leg's midpoint cannot separate
	/// two neighbouring hexes at all - they overlap by half a width in x - so
	/// the mounted hex's own corner reached across it and sat on the tank
	/// riding up. The shared edge is the one line with the mounted cell wholly
	/// on one side and the covering neighbour wholly on the other, a convex
	/// hex lying entirely on its own side of each of its edges: the mounted
	/// hex never covers the tank mounting it, and the neighbour's cover runs
	/// exactly to the rim it draws.
	///
	/// Two hex centres are equidistant from their shared edge, so the line is
	/// the perpendicular bisector of the two anchors, dropped by the
	/// neighbour's lift to where that neighbour's rim is drawn.
	/// <b>Perpendicular in the flat world, not on the screen:</b> the camera
	/// squashes the ground and does not preserve right angles, and the
	/// bisector taken in screen coordinates missed the edge by enough to spill
	/// hex corners across it - the self-test caught it on its first run. The
	/// screen normal of a flat-perpendicular line is <c>(dx*s, dy/s)</c> for a
	/// screen centre difference <c>(dx, dy)</c> and squash <c>s</c>. A leg
	/// with no flanker to read - the board's edge - falls back to the
	/// perpendicular through the leg's own midpoint, where there is nothing to
	/// cover either way.
	/// </summary>
	public static Vector3 SeamLine(HexField field, Vector2 origin,
		Vector2I cell, Vector2I next)
	{
		if (SeamFlank(field, cell, next) is Vector2I flank)
			return Bisector(field,
				origin + field.FlatAnchor(next) + field.CentreOffset,
				origin + field.FlatAnchor(flank) + field.CentreOffset,
				field.LevelAt(flank) * field.Lift);
		return SeamEdge(field, origin, cell, next);
	}

	/// <summary>The leg's own shared edge, in the flat world. A descent's
	/// wipe (see <c>Main.Climb</c>) runs parallel to it - only the normal
	/// is read, the offset is swept - and a climb on a board edge falls back
	/// to it, where there is no flanker and nothing to cover either way.
	/// Flat rather than dropped to a drawn rim, because two lifts could
	/// claim the drop and neither owns this line.</summary>
	public static Vector3 SeamEdge(HexField field, Vector2 origin,
		Vector2I cell, Vector2I next) =>
		Bisector(field,
			origin + field.FlatAnchor(next) + field.CentreOffset,
			origin + field.FlatAnchor(cell) + field.CentreOffset,
			0.0f);

	/// <summary>The shared hex edge as a screen half-plane, with the side of
	/// <paramref name="cn"/> - the cell being driven onto, whose fragments
	/// carry <c>Vehicle.Standing</c> - positive. Perpendicular in the flat
	/// world, not on the screen (the camera does not preserve right angles),
	/// and dropped by <paramref name="lift"/> to where the covering rim is
	/// drawn.</summary>
	private static Vector3 Bisector(HexField field, Vector2 cn, Vector2 other,
		float lift)
	{
		Vector2 d = cn - other;
		float s = Mathf.Max(field.Squash, 0.0001f);
		Vector2 n = new Vector2(d.X * s, d.Y / s).Normalized();
		Vector2 mid = (cn + other) * 0.5f - new Vector2(0.0f, lift);
		return new Vector3(n.X, n.Y, n.Dot(mid));
	}

	/// <summary>
	/// How high the two ends of the hull count as standing, <paramref name="done"/>
	/// of the way from one cell to the next, with the contact point leading the
	/// trailing end by <paramref name="gait"/> of the leg.
	///
	/// A named static rather than four lines in <c>Main.Climb</c> for
	/// <c>ImpactFor</c>'s reason: the self-test walks every climb on the board
	/// through it, and a copy of the arithmetic in the test would be a second
	/// truth to keep in agreement.
	///
	/// <b>The nose is the crown of the step while the tank is on the wall</b>,
	/// then down onto what it is arriving at. A staircase has no ramp on it, so
	/// a tank drawn halfway up one at its honest height is a tank halfway inside
	/// the block - and the stage, unlike a z index, says so, cutting the sprite
	/// along that block's top plane. The wall belongs to the higher cell, and a
	/// tank on it is not inside it, so for as long as it is on the wall it
	/// counts as up on the crown. Climbing, the higher cell is the one being
	/// driven onto and the tank is never off it, so the crown holds for the
	/// whole leg. Descending toward the viewer the tank is on the near face, in
	/// plain sight the whole way, so the crown holds there too - a single pixel
	/// of early descent puts it under the level of the shoulder it is passing,
	/// measured on (5,3)->(6,4), where the corner of (5,4)'s top face took a
	/// wedge out of the hull. Whether a wall faces the camera is which of the
	/// two cells is nearer, not which is higher.
	///
	/// <b>Descending behind the rim the split reappears, the other way up.</b>
	/// The single number tried both answers here and each was a visible jolt:
	/// the drawn ramp bites the foot out of the plateau it is still standing
	/// on, and the crown squeezed the whole descent into the second half of
	/// the leg at double rate - measured on (4,3)->(4,2), the rim swallowed
	/// thirty percent of the silhouette in four frames and handed part of it
	/// back by arrival. So the nose rides the ramp the sprite is drawn on and
	/// sinks behind the rim exactly as fast as it is drawn sinking, while the
	/// tail holds the crown for as long as it is over the top - the same two
	/// statements the climb makes, read in the other order. The seam is the
	/// leg's own shared edge (<see cref="SeamEdge"/>): the climb cuts along
	/// the flanker's rim because that is the rim doing the covering, and here
	/// the covering rim is the one being driven over.
	///
	/// <b>The tail is still down where the hull came from.</b> One depth for the
	/// whole sprite could not say so: a drawn row fixes the family
	/// <c>(row + L, L)</c>, so the promotion that stands the nose out of the
	/// block it is entering also stood the tail level with ground it had not
	/// come up to - promoted at the first pixel of (3,2)->(4,3), the tank spent
	/// the whole leg drawn over (3,3), a lateral hex it was not level with until
	/// the end, and the hex stopped covering it the moment the leg began.
	/// Climbing, the trailing end therefore reads its height off the leg half a
	/// hull behind the contact point, so the low end of the sprite stays below
	/// the level it has not reached and the ground at that level goes back over
	/// it - the same statement the 2D board's climb rule makes, asked of the end
	/// of the tank it is true of. The 2D board bought both answers with a
	/// per-cell sort key taken at the far edge; the stage buys them with a
	/// height per end, laid along the travel axis by <c>Stage3D.Body</c>.
	/// Parked, level and descending in plain sight the tail is the nose: the
	/// two ends have nothing to disagree about, and a split of zero is the old
	/// single number.
	///
	/// <b>But the ground ahead may take the running gear and no more</b> -
	/// <paramref name="cover"/> floors the tail that far under the nose. The
	/// fully honest tail was built and looked wrong for a reason no geometry
	/// fixes: on (3,2)->(4,3) the step ahead honestly hid half the tank, but the
	/// wall doing the hiding faces away from the camera and its top wears the
	/// same dirt as the flat ground, so the picture read as level ground painted
	/// over a tank rather than a tank behind a rise. Capped at the gear, the
	/// tracks and the shadow tuck behind the rim while the hull and turret stay
	/// whole - a tank cresting an edge, which is the reading a climb wants.
	/// </summary>
	public static (float Nose, float Tail) StepHeights(HexField field,
		Vector2I cell, Vector2I next, float done, float gait, float cover)
	{
		// Surface heights, not levels: a ramp's ends are half a level up, so the
		// crown of a leg onto one is half a lift and the level test would call
		// that leg flat. Every cell of a board with no ramps on it reports the
		// same number either way, which is why this reads as a rename.
		float onto = field.TopAt(next);
		float crown = Mathf.Max(field.TopAt(cell), onto);
		bool behind = field.GroundRow(next) < field.GroundRow(cell);
		// Descending behind the rim, the split works the other way up: the
		// nose rides the ramp the sprite is drawn on, and the tail holds the
		// crown - not until some moment, but for the whole leg. There is no
		// moment to pick: a fragment drawn over the plateau's top face is
		// visible at exactly the crown and swallowed whole a hair under it,
		// so any tail that descends by number - the old half-leg lerp, a
		// climbing-style catch-up, anything - dumps everything still drawn
		// over the top in a single frame. Measured on (4,3)->(4,2) twice:
		// the lerp cost thirty percent of the silhouette in four frames, the
		// catch-up six hundred pixels in one. What drains the crown side
		// instead is the seam, swept down the sprite by Climb - fragment by
		// fragment, a few rows a frame - which is why this split needs no
		// closing: by the time the leg parks, the sweep has left nothing on
		// the crown side to pop.
		// No cover cap here either: the parked tank behind this rim wears
		// the full cut already, and a floor would pop off at parking.
		if (behind && onto < field.TopAt(cell))
			return (field.HeightBetween(cell, next, done), crown);
		// Everything else stands on the crown: climbing it is the cell being
		// entered, level it is both, and descending toward the camera it is
		// the near face the tank is in plain sight on - a pixel of early
		// descent there put it under the shoulder it was passing, measured on
		// (5,3)->(6,4). The park at the far anchor snaps that crown to the
		// floor, and it is allowed to: measured on the two worst legs, not
		// one pixel of the tank changes - by the time it parks it is a full
		// half-cell clear of anything the crown was holding it out of.
		float nose = crown;
		// From a gait behind the contact at the near anchor to level with the
		// nose at the far one, so the split closes exactly where the leg parks:
		// held at a plain lag it would still be half a lift down on arrival,
		// and parking would pop the tail's depth.
		float tail = onto > field.TopAt(cell)
			? Mathf.Max(
				field.HeightBetween(cell, next, done * (1.0f + gait) - gait),
				nose - cover)
			: nose;
		return (nose, tail);
	}

	/// <summary>How much of a climbing tank the ground ahead may hide, in
	/// atlas px of depth under the crown - about the height of the running
	/// gear, scaled by the class before use. See <see cref="StepHeights"/> for
	/// why the honest number was retired.</summary>
	public const float GearCover = 24.0f;
}
