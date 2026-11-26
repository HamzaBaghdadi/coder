using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Coder.App.Theme;

namespace Coder.App.Controls;

/// <summary>Displays the app logo at any size, downscaled with high-quality filtering.</summary>
public sealed class LogoView : Image
{
    public LogoView(double size)
    {
        Source = Brand.Logo;
        Width = Height = size;
        IsHitTestVisible = false;
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.HighQuality);
    }
}
