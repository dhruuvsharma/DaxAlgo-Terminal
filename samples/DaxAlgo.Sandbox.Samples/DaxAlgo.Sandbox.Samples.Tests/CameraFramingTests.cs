using DaxAlgo.Sdk.Quant;
using Xunit;

namespace DaxAlgo.Sandbox.Samples.Tests;

/// <summary>
/// A fitted camera has to actually fit — asserted on the PROJECTED pixels, not on the distance it
/// chose, because the distance is the working and the pixels are the claim.
///
/// <para>Both 3D scenes in the first benchmark batch projected correctly and then drifted half out of
/// the panel: the camera was a hard-coded position copied from an exemplar, and the data was nowhere
/// near a unit cube. Correct and off-screen reads exactly like broken.</para>
/// </summary>
public sealed class CameraFramingTests
{
    private const double Width = 900d;
    private const double Height = 500d;

    public static TheoryData<double, double, double> Clouds => new()
    {
        // A unit cube at the origin, a far-away one, a tall thin one, and prices in the tens of
        // thousands — the case that actually broke, since a crypto height field is nowhere near 1.
        { 1d, 0d, 0d },
        { 1d, 500d, -300d },
        { 0.01d, 0d, 0d },
        { 4000d, 77000d, 0d },
    };

    [Theory]
    [MemberData(nameof(Clouds))]
    public void EverythingFitsInsideThePanel(double size, double offsetY, double offsetZ)
    {
        var points = Cloud(size, offsetY, offsetZ);
        var camera = Camera3.Framing(points);
        var projection = Projection3.Of(camera, Width, Height);

        foreach (var p in points)
        {
            var at = projection.Project(p);

            Assert.True(at.InFront, "a fitted camera must have every point in front of it");
            Assert.InRange(at.X, 0d, Width);
            Assert.InRange(at.Y, 0d, Height);
        }
    }

    [Fact]
    public void TheDataIsRoughlyCentred()
    {
        // Fitting is not enough on its own: a camera far enough away fits anything, in a dot in the
        // middle. This checks the picture actually USES the panel.
        var points = Cloud(3d, 0d, 0d);
        var projection = Projection3.Of(Camera3.Framing(points), Width, Height);

        double top = double.MaxValue, bottom = double.MinValue;
        foreach (var p in points)
        {
            var at = projection.Project(p);
            top = Math.Min(top, at.Y);
            bottom = Math.Max(bottom, at.Y);
        }

        Assert.True(bottom - top > Height * 0.35d,
            $"the data should fill a decent share of the height, spanned {bottom - top:F0} of {Height}");
    }

    [Fact]
    public void AnOrbitKeepsItFramed()
    {
        // The radius is half the box DIAGONAL precisely so a spin cannot swing a corner out of frame.
        var points = Cloud(2d, 0d, 0d);
        var fitted = Camera3.Framing(points);

        for (var step = 0; step < 8; step++)
        {
            var projection = Projection3.Of(fitted.Orbit(step * Math.PI / 4d), Width, Height);
            foreach (var p in points)
            {
                var at = projection.Project(p);
                Assert.True(at.InFront);
                Assert.InRange(at.X, 0d, Width);
                Assert.InRange(at.Y, 0d, Height);
            }
        }
    }

    [Fact]
    public void NothingToFrameLeavesTheDefault()
    {
        Assert.Equal(Camera3.Default, Camera3.Framing([]));
        Assert.Equal(Camera3.Default, Camera3.Framing([new Vec3(double.NaN, 0d, 0d)]));
    }

    [Fact]
    public void OnePointStillProduces_AUsableCamera()
    {
        // A degenerate box has no radius, so the distance would be zero and every projection would sit
        // on the camera plane. Substituting a unit radius keeps it drawable.
        var projection = Projection3.Of(Camera3.Framing([new Vec3(5d, 5d, 5d)]), Width, Height);
        var at = projection.Project(new Vec3(5d, 5d, 5d));

        Assert.True(at.InFront);
        Assert.InRange(at.X, 0d, Width);
        Assert.InRange(at.Y, 0d, Height);
    }

    private static Vec3[] Cloud(double size, double offsetY, double offsetZ)
    {
        var points = new List<Vec3>();
        for (var x = -1; x <= 1; x++)
            for (var y = -1; y <= 1; y++)
                for (var z = -1; z <= 1; z++)
                    points.Add(new Vec3(x * size, offsetY + y * size, offsetZ + z * size));
        return [.. points];
    }
}
