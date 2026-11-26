using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Coder.App.Theme;

/// <summary>
/// Brand assets. The logo artwork lives in <c>assets/source</c>; <c>tools/Coder.IconTool</c> turns it
/// into <c>assets/coder.png</c> (embedded here) and <c>assets/coder.ico</c> (the executable icon).
/// </summary>
public static class Brand
{
    private static readonly Lazy<Bitmap> LazyLogo = new(() =>
    {
        using var stream = typeof(Brand).Assembly.GetManifestResourceStream("Coder.Logo.png")
            ?? throw new InvalidOperationException("The embedded logo resource is missing.");
        return new Bitmap(stream);
    });

    /// <summary>The app logo with a transparent background, 512×512.</summary>
    public static Bitmap Logo => LazyLogo.Value;

    /// <summary>The logo's signature blue → cyan sweep, for decorative accents (never behind text).</summary>
    public static readonly IBrush Gradient = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.Parse("#1E5BFF"), 0),
            new GradientStop(Color.Parse("#3D8BFF"), 0.5),
            new GradientStop(Color.Parse("#5AC8FA"), 1),
        },
    };

    /// <summary>
    /// Fill for primary buttons: the brand blues, kept dark enough for white text to stay readable
    /// (at least 4.5:1 contrast across the whole gradient).
    /// </summary>
    public static readonly IBrush ButtonGradient = Diagonal("#2462E0", "#1A74D6");

    public static readonly IBrush ButtonGradientHover = Diagonal("#2A6BEA", "#1F7CDF");

    /// <summary>
    /// The faint diagonal light bands from the logo's tile, used as a backdrop on large empty surfaces.
    /// </summary>
    public static readonly IBrush DiagonalSheen = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.Parse("#000C1424"), 0),
            new GradientStop(Color.Parse("#140D2A5C"), 0.42),
            new GradientStop(Color.Parse("#000C1424"), 0.5),
            new GradientStop(Color.Parse("#000C1424"), 0.62),
            new GradientStop(Color.Parse("#100D2A5C"), 0.7),
            new GradientStop(Color.Parse("#000C1424"), 0.78),
        },
    };

    private static LinearGradientBrush Diagonal(string from, string to) => new()
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.Parse(from), 0), new GradientStop(Color.Parse(to), 1) },
    };
}
