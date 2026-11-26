using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Coder.App.Theme;

namespace Coder.App.Editor;

/// <summary>
/// Shows an image file: fitted to the view (never enlarged past its own size), on a checkerboard so
/// transparency is visible. Ctrl+wheel zooms around the pointer; double-click returns to "fit".
/// Only the shown image is decoded; switching away releases it.
/// </summary>
public sealed class ImageViewer : Panel
{
    private const double CheckerSize = 8;

    private readonly ImageCanvas _canvas = new();
    private readonly TextBlock _info = new()
    {
        FontSize = Typography.SmallSize,
        Foreground = Palette.TextSecondary,
        HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Bottom,
        Margin = new Thickness(Spacing.M),
    };

    private readonly TextBlock _error = new()
    {
        Foreground = Palette.TextSecondary,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        IsVisible = false,
    };

    public ImageViewer()
    {
        Background = Palette.EditorBackground;
        Children.Add(_canvas);
        Children.Add(_error);
        Children.Add(new Border
        {
            Background = Palette.Elevated,
            CornerRadius = new CornerRadius(4),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(Spacing.M),
            Padding = new Thickness(Spacing.S, 3),
            Child = _info,
        });
        _info.Margin = default;
    }

    /// <summary>The image file to show, or null to release the current one.</summary>
    public string? Path
    {
        get => _canvas.Path;
        set
        {
            _error.IsVisible = false;
            _info.Text = string.Empty;
            _canvas.Load(value);
            if (value is null)
            {
                return;
            }

            if (_canvas.Bitmap is { } bitmap)
            {
                var size = new FileInfo(value).Length;
                _info.Text = $"{bitmap.PixelSize.Width.ToString(CultureInfo.InvariantCulture)} × {bitmap.PixelSize.Height.ToString(CultureInfo.InvariantCulture)} · {FormatSize(size)}";
            }
            else
            {
                _error.Text = $"{System.IO.Path.GetFileName(value)} could not be shown as an image.";
                _error.IsVisible = true;
            }
        }
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / 1024.0 / 1024.0:0.#} MB",
    };

    /// <summary>Draws the checkerboard and the image at the current zoom.</summary>
    private sealed class ImageCanvas : Control
    {
        private double? _zoom; // null = fit

        public ImageCanvas() => ClipToBounds = true;

        public string? Path { get; private set; }

        public Bitmap? Bitmap { get; private set; }

        public void Load(string? path)
        {
            Bitmap?.Dispose();
            Bitmap = null;
            Path = path;
            _zoom = null;
            if (path is not null)
            {
                try
                {
                    Bitmap = new Bitmap(path);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
                {
                    Bitmap = null; // shown as a message by the viewer
                }
            }

            InvalidateVisual();
        }

        public override void Render(DrawingContext context)
        {
            if (Bitmap is not { } bitmap)
            {
                return;
            }

            var size = new Size(bitmap.Size.Width * Zoom(bitmap), bitmap.Size.Height * Zoom(bitmap));
            var rect = new Rect(new Point((Bounds.Width - size.Width) / 2, (Bounds.Height - size.Height) / 2), size);
            DrawChecker(context, rect);
            context.DrawImage(bitmap, rect);
        }

        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            base.OnPointerWheelChanged(e);
            if (Bitmap is { } bitmap && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                _zoom = Math.Clamp(Zoom(bitmap) * (e.Delta.Y > 0 ? 1.25 : 0.8), 0.05, 32);
                InvalidateVisual();
                e.Handled = true;
            }
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (e.ClickCount == 2)
            {
                _zoom = null; // back to fit
                InvalidateVisual();
            }
        }

        /// <summary>Fit: scaled down to the view with a margin, but never enlarged past 100%.</summary>
        private double Zoom(Bitmap bitmap) => _zoom ?? Math.Min(1, Math.Min(
            Math.Max(1, Bounds.Width - 48) / bitmap.Size.Width,
            Math.Max(1, Bounds.Height - 48) / bitmap.Size.Height));

        private static void DrawChecker(DrawingContext context, Rect rect)
        {
            using (context.PushClip(rect))
            {
                context.FillRectangle(Palette.Elevated, rect);
                for (var y = rect.Top; y < rect.Bottom; y += CheckerSize)
                {
                    var odd = (int)((y - rect.Top) / CheckerSize) % 2 == 1;
                    for (var x = rect.Left + (odd ? CheckerSize : 0); x < rect.Right; x += CheckerSize * 2)
                    {
                        context.FillRectangle(Palette.Hover, new Rect(x, y, CheckerSize, CheckerSize));
                    }
                }
            }
        }
    }
}
