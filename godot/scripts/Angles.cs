using System;

namespace TankSpriteTest;

/// <summary>
/// Angles on a flat-top hex board: wrapping one, and naming the side of a cell
/// a bearing arrives on.
///
/// <b>Lifted out of <see cref="Main"/> for <see cref="TankTick"/>'s reason.</b>
/// These are arithmetic - no node, no board, no frame - and they were sitting
/// on the harness scene, so <see cref="TankTick"/>, <see cref="TankBench"/> and
/// the self test all reached through a <c>Node2D</c> to divide by 360. A bench
/// that wants a bearing snapped should not have to load a harness to get one.
///
/// <b>Nothing forwards from the old home.</b> Two names for one function is the
/// arrangement this project spends its docstrings refusing; the call sites moved
/// instead.
/// </summary>
public static class Angles
{
	/// <summary>Euclidean remainder - the one that is never negative. C#'s <c>%</c>
	/// keeps the sign of the dividend, which turns every backwards turn into a
	/// negative bearing the moment it crosses zero.</summary>
	public static double Mod(double a, double n) => (a % n + n) % n;

	/// <summary>A turn expressed as the short way round: -180 to +180. What "how
	/// far has the turret swung since last frame" has to be measured in, or a
	/// hull crossing north reports 359 degrees of swing.</summary>
	public static double WrapAngle(double degrees) => Mod(degrees + 180.0, 360.0) - 180.0;

	/// <summary>
	/// The hex side a bearing arrives on, as an index into
	/// <see cref="HexField.EdgeHeadings"/>. Any angle from anywhere - the command
	/// line, a future game - is answered with one of the six.
	///
	/// Six bearings, not any bearing: a tank is hit from a neighbouring cell, and
	/// a neighbour is across a flat side. So the six directions a tank can drive
	/// in are exactly the six it can be shot from, and there is one list of them
	/// rather than a movement list and a gunnery list.
	/// </summary>
	public static int SideFor(double bearing)
	{
		int best = 0;
		double bestGap = double.MaxValue;
		for (int i = 0; i < HexField.EdgeHeadings.Length; i++)
		{
			double gap = Math.Abs(Mod(HexField.EdgeHeadings[i] - bearing + 180.0, 360.0)
								  - 180.0);
			if (gap < bestGap)
			{
				bestGap = gap;
				best = i;
			}
		}
		return best;
	}
}
