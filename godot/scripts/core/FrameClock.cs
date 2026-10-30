namespace TankSpriteTest;

/// <summary>
/// The step every animated thing on a bench takes, or null to run on the real
/// one. Pinned by whichever root is being run the moment a capture or a trace
/// is asked for.
///
/// <b>Static because the pin has to reach past one node, and once did not.</b>
/// It used to be a local in the harness's <c>_Process</c>, which pinned the
/// tanks and left <see cref="Stage3D"/> - a sibling with a <c>_Process</c> of
/// its own - turning the pond on the wall clock. So the water was never the
/// same twice under --capture, and every A/B taken over it carried that as
/// noise: measured at <b>58 446 px, peak 10</b>, in a box that is exactly the
/// pond. Anything a bench animates reads this, or it is outside the guarantee
/// --capture exists to give.
///
/// <b>Its own file rather than a field on <see cref="Main"/>, which is where it
/// was.</b> Five of the six roots animate, and four of them are not the
/// harness: <see cref="Stage3D"/>, <see cref="WallStack"/>,
/// <see cref="TankBench"/>, <see cref="WoodBench"/> and <see cref="WallBench"/>
/// all read it. Global clocks belonging to one of the scenes they govern is the
/// arrangement this pass is taking apart.
/// </summary>
public static class FrameClock
{
	/// <summary>Seconds per frame while a capture or a trace is running, else
	/// null. Read as <c>FrameClock.FixedStep ?? delta</c> - never assigned outside a
	/// root's argument parsing.</summary>
	public static double? FixedStep;
}
