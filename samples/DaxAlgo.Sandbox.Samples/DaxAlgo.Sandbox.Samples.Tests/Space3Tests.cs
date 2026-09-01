using DaxAlgo.Sdk.Quant;
using Xunit;

namespace DaxAlgo.Sandbox.Samples.Tests;

/// <summary>
/// The projection maths an authored unit uses to draw in three dimensions with two-dimensional
/// primitives.
///
/// <para>Every case here is one a naive implementation gets wrong, because the arithmetic itself is
/// four lines and is not what needs pinning. What needs pinning is the behaviour at the edges: behind
/// the camera, a degenerate viewport, a camera aimed along its own up axis.</para>
/// </summary>
public sealed class Space3Tests
{
    private static readonly Camera3 Front = new(new Vec3(0d, 0d, -4d), Vec3.Zero, Vec3.Up, 60d);

    [Fact]
    public void The_point_a_camera_looks_at_lands_in_the_middle()
    {
        var projected = Projection3.Of(Front, 400d, 300d).Project(Vec3.Zero);

        Assert.True(projected.InFront);
        Assert.Equal(200d, projected.X, 6);
        Assert.Equal(150d, projected.Y, 6);
        Assert.Equal(4d, projected.Depth, 6);
    }

    [Fact]
    public void Screen_Y_grows_downwards_while_world_Y_grows_up()
    {
        // The sign error every first draft makes, and it is invisible in a symmetric scene.
        var projection = Projection3.Of(Front, 400d, 300d);

        var above = projection.Project(new Vec3(0d, 1d, 0d));
        var below = projection.Project(new Vec3(0d, -1d, 0d));

        Assert.True(above.Y < 150d, "a point above the target must draw higher on the screen");
        Assert.True(below.Y > 150d);
    }

    [Fact]
    public void A_point_behind_the_camera_is_flagged_rather_than_drawn_somewhere_plausible()
    {
        // The trap the whole InFront flag exists for: without it this projects to a perfectly
        // reasonable-looking position on the far side of the picture, so a unit draws geometry where
        // there is none and nothing anywhere reports a problem.
        var projected = Projection3.Of(Front, 400d, 300d).Project(new Vec3(0d, 0d, -9d));

        Assert.False(projected.InFront);
        Assert.True(double.IsFinite(projected.X) && double.IsFinite(projected.Y));
    }

    [Fact]
    public void Nearer_things_have_smaller_depth_so_a_descending_sort_draws_them_last()
    {
        // The painter's-algorithm contract, stated as a test because the doc tells authors to sort by
        // it and a reversed sense would silently put the far wall in front of everything.
        var projection = Projection3.Of(Front, 400d, 300d);

        var near = projection.Project(new Vec3(0d, 0d, -1d));
        var far = projection.Project(new Vec3(0d, 0d, 6d));

        Assert.True(near.Depth < far.Depth);
    }

    [Fact]
    public void A_camera_aimed_along_its_own_up_axis_still_produces_a_finite_frame()
    {
        // Looking straight down with Up still pointing up: the cross product is zero and every
        // coordinate downstream becomes NaN. The host refuses non-finite coordinates silently, so the
        // symptom is an empty panel and no message anywhere.
        var overhead = new Camera3(new Vec3(0d, 5d, 0d), Vec3.Zero, Vec3.Up, 60d);

        var projected = Projection3.Of(overhead, 400d, 300d).Project(new Vec3(1d, 0d, 0d));

        Assert.True(double.IsFinite(projected.X));
        Assert.True(double.IsFinite(projected.Y));
    }

    [Fact]
    public void A_zero_sized_panel_projects_finite_coordinates()
    {
        // A collapsed panel, a window restored from minimised, a layout pass before measurement — all
        // report zero size, and DrawProbe now drives exactly this frame.
        var projected = Projection3.Of(Front, 0d, 0d).Project(new Vec3(1d, 1d, 0d));

        Assert.True(double.IsFinite(projected.X));
        Assert.True(double.IsFinite(projected.Y));
    }

    [Fact]
    public void Orbit_keeps_the_distance_and_the_height()
    {
        // What a unit spins from surface.Now. A rotation that drifted the radius would walk the camera
        // into the scene over a few minutes of a window being left open.
        var turned = Front.Orbit(Math.PI / 3d);

        Assert.Equal((Front.Position - Front.Target).Length, (turned.Position - turned.Target).Length, 9);
        Assert.Equal(Front.Position.Y, turned.Position.Y, 9);
    }

    [Fact]
    public void A_zero_vector_normalises_to_itself_rather_than_to_NaN()
    {
        Assert.Equal(Vec3.Zero, Vec3.Zero.Normalized());
    }

    [Fact]
    public void Cross_is_right_handed()
    {
        Assert.Equal(new Vec3(0d, 0d, 1d), Vec3.Cross(new Vec3(1d, 0d, 0d), new Vec3(0d, 1d, 0d)));
    }
}
