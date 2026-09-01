namespace DaxAlgo.Sdk.Quant;

/// <summary>
/// A point or direction in three dimensions.
///
/// <para>Here so a unit can draw a three-dimensional picture with the two-dimensional primitives it
/// already has: it places its data in space, projects each point itself through
/// <see cref="Projection3"/>, and draws lines, rectangles and markers at the results. Nothing new
/// reaches the host — a unit still never touches a control, which is the whole reason it can be
/// sandboxed.</para>
/// </summary>
/// <param name="X">Rightwards.</param>
/// <param name="Y">Upwards.</param>
/// <param name="Z">Away from the viewer.</param>
public readonly record struct Vec3(double X, double Y, double Z)
{
    /// <summary>The origin.</summary>
    public static Vec3 Zero { get; }

    /// <summary>Straight up. The conventional <c>Up</c> for a camera that is not rolled.</summary>
    public static Vec3 Up { get; } = new(0d, 1d, 0d);

    public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    public static Vec3 operator *(Vec3 v, double scale) => new(v.X * scale, v.Y * scale, v.Z * scale);

    public static Vec3 operator *(double scale, Vec3 v) => v * scale;

    public double Length => Math.Sqrt(Dot(this, this));

    /// <summary>The same direction at unit length. A zero-length vector returns itself rather than a
    /// vector of <c>NaN</c>: a degenerate camera is a mistake in the unit, and a frame of non-finite
    /// coordinates is much harder to diagnose than a frame that simply looks wrong.</summary>
    public Vec3 Normalized()
    {
        var length = Length;
        return length < Num.Epsilon ? this : this * (1d / length);
    }

    public static double Dot(Vec3 a, Vec3 b) => (a.X * b.X) + (a.Y * b.Y) + (a.Z * b.Z);

    /// <summary>The vector perpendicular to both, right-handed.</summary>
    public static Vec3 Cross(Vec3 a, Vec3 b) => new(
        (a.Y * b.Z) - (a.Z * b.Y),
        (a.Z * b.X) - (a.X * b.Z),
        (a.X * b.Y) - (a.Y * b.X));
}

/// <summary>Where the viewer stands and what they are looking at. Pass to
/// <see cref="Projection3.Of"/>. <c>Default</c> looks at the origin from in front and slightly
/// above, which frames a unit cube sensibly.</summary>
/// <param name="Position">Where the viewer is.</param>
/// <param name="Target">What they are looking at.</param>
/// <param name="Up">Which way is up; <see cref="Vec3.Up"/> unless the camera is rolled.</param>
/// <param name="FieldOfViewDegrees">Vertical field of view. Clamped to a sane range when projecting.</param>
public readonly record struct Camera3(
    Vec3 Position, Vec3 Target, Vec3 Up, double FieldOfViewDegrees)
{
    public static Camera3 Default { get; } =
        new(new Vec3(2.2d, 1.6d, -2.6d), Vec3.Zero, Vec3.Up, 50d);

    /// <summary>The same camera moved around the target on a circle, at a fixed height and distance —
    /// what a unit spins from <c>surface.Now</c> when it wants the scene to turn.</summary>
    /// <param name="radians">Angle around the target, measured from the current position.</param>
    public Camera3 Orbit(double radians)
    {
        var offset = Position - Target;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);

        // Rotated about the up axis only, so the horizon stays level however far it spins.
        return this with
        {
            Position = Target + new Vec3(
                (offset.X * cos) + (offset.Z * sin),
                offset.Y,
                (offset.Z * cos) - (offset.X * sin)),
        };
    }
}

/// <summary>One projected point, in panel pixels, with what a unit needs to draw it correctly.</summary>
/// <param name="X">Panel X.</param>
/// <param name="Y">Panel Y, already flipped — screen Y grows downwards while world Y grows up.</param>
/// <param name="Depth">Distance from the camera. Sort DESCENDING and draw in that order: painter's
/// algorithm, far things first, so near things cover them.</param>
/// <param name="InFront">
/// False when the point is behind the camera, and it must be checked.
///
/// <para>A point behind the camera projects to a perfectly plausible-looking position on the other
/// side of the picture, so a unit that draws it puts geometry where none exists — and every naive
/// implementation draws it. Skip a point, or a whole shape, when this is false.</para>
/// </param>
public readonly record struct Projected(double X, double Y, double Depth, bool InFront);

/// <summary>
/// Turns world coordinates into panel coordinates, so a unit can draw in three dimensions with the
/// two-dimensional primitives.
///
/// <para><b>This is arithmetic, not a renderer.</b> There is no scene, no mesh, no light and no
/// z-buffer — a unit projects its own points, sorts them by <see cref="Projected.Depth"/>, and draws
/// them far to near. That is exact for scattered markers and for a height field walked back to front,
/// which covers the pictures worth drawing this way. It sorts <i>wrongly</i> for shapes that
/// interpenetrate, and no amount of sorting fixes that; a scene that needs true occlusion needs a
/// different tool.</para>
/// </summary>
public readonly record struct Projection3
{
    private readonly Vec3 _eye;
    private readonly Vec3 _right;
    private readonly Vec3 _up;
    private readonly Vec3 _forward;
    private readonly double _halfWidth;
    private readonly double _halfHeight;
    private readonly double _scale;

    private Projection3(Camera3 camera, double width, double height)
    {
        _eye = camera.Position;
        _forward = (camera.Target - camera.Position).Normalized();

        // A camera looking straight along its own up axis has no derivable right vector — the cross
        // product is zero and every coordinate downstream becomes NaN. Substituting a fallback keeps a
        // badly aimed camera producing a wrong picture rather than an empty one.
        var right = Vec3.Cross(_forward, camera.Up);
        _right = (right.Length < Num.Epsilon ? new Vec3(1d, 0d, 0d) : right).Normalized();
        _up = Vec3.Cross(_right, _forward).Normalized();

        _halfWidth = width / 2d;
        _halfHeight = height / 2d;

        var fov = Math.Clamp(camera.FieldOfViewDegrees, 1d, 179d) * Math.PI / 180d;
        _scale = _halfHeight / Math.Tan(fov / 2d);
    }

    /// <summary>
    /// A projection for one panel. Take the size from <c>surface.Viewport</c> so the picture is framed
    /// correctly at whatever size the host gives the panel.
    /// </summary>
    /// <param name="camera">Where the viewer stands.</param>
    /// <param name="width">Panel width in pixels.</param>
    /// <param name="height">Panel height in pixels.</param>
    public static Projection3 Of(Camera3 camera, double width, double height) =>
        new(camera, double.IsFinite(width) ? Math.Max(0d, width) : 0d,
                    double.IsFinite(height) ? Math.Max(0d, height) : 0d);

    /// <summary>
    /// One world point in panel pixels.
    ///
    /// <para>Check <see cref="Projected.InFront"/> before drawing, and sort by
    /// <see cref="Projected.Depth"/> descending so nearer things are drawn last.</para>
    /// </summary>
    public Projected Project(Vec3 world)
    {
        var relative = world - _eye;
        var depth = Vec3.Dot(relative, _forward);

        // Behind the camera, or exactly on the plane through it: no meaningful projection exists. The
        // coordinates returned are finite so a caller that ignores the flag draws in the wrong place
        // rather than poisoning the frame with NaN, which the host would refuse silently.
        if (depth < Num.Epsilon)
            return new Projected(_halfWidth, _halfHeight, depth, InFront: false);

        var scale = _scale / depth;

        return new Projected(
            _halfWidth + (Vec3.Dot(relative, _right) * scale),
            _halfHeight - (Vec3.Dot(relative, _up) * scale),
            depth,
            InFront: true);
    }
}
