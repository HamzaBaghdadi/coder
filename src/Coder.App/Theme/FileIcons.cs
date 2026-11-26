using System.Globalization;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Coder.Core.Syntax;

namespace Coder.App.Theme;

/// <summary>
/// File and folder icons from the Material Icon Theme (MIT, see <c>assets/material-icons/LICENSE</c>),
/// shipped with Coder. <c>tools/Coder.MaterialIcons</c> converts the icon set into vector layers
/// (<c>icons.txt</c>) and name mappings (<c>manifest.json</c>), both embedded.
/// <para>
/// Cheap by design: the icon text stays in the executable's mapped image (only line offsets are
/// indexed), and an icon's drawing is built the first time it is shown, then shared.
/// </para>
/// </summary>
public static class FileIcons
{
    private static readonly Lazy<Catalog> Icons = new(Catalog.Load);
    private static readonly Dictionary<string, IImage> Drawings = [];

    /// <summary>The icon for a file, by its name (e.g. <c>package.json</c>, <c>app.component.ts</c>).</summary>
    public static IImage ForFile(string path) => Draw(Icons.Value.FileIconName(Path.GetFileName(path), !Palette.Current.IsDark));

    public static IImage ForFolder(string path, bool expanded) =>
        Draw(Icons.Value.FolderIconName(Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)), expanded, !Palette.Current.IsDark));

    /// <summary>A sized image of a file's icon, for rows and tabs.</summary>
    public static Image File(string path, double size = 16, Thickness margin = default) =>
        new() { Source = ForFile(path), Width = size, Height = size, Margin = margin, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };

    /// <summary>For tests: the icon name chosen for a file or folder.</summary>
    internal static string IconNameFor(string name, bool isFolder, bool expanded = false, bool light = false) =>
        isFolder ? Icons.Value.FolderIconName(name, expanded, light) : Icons.Value.FileIconName(name, light);

    /// <summary>For tests: every icon in the set, and every icon a mapping refers to.</summary>
    internal static (IReadOnlyCollection<string> Available, IReadOnlyCollection<string> Referenced) Inventory() => Icons.Value.Inventory();

    /// <summary>For tests: builds an icon's drawing (null when missing).</summary>
    internal static IImage? Build(string name) => Icons.Value.Build(name);

    private static IImage Draw(string name)
    {
        if (!Drawings.TryGetValue(name, out var image))
        {
            image = Icons.Value.Build(name) ?? Icons.Value.Build(Icons.Value.DefaultFile)!;
            Drawings[name] = image;
        }

        return image;
    }

    /// <summary>The embedded icon set: mappings, plus where each icon's line is.</summary>
    private sealed class Catalog
    {
        private readonly Stream _icons;
        private readonly Dictionary<string, (long Offset, int Length)> _lines;
        private readonly Mappings _dark;
        private readonly Mappings _light;

        private Catalog(Stream icons, Dictionary<string, (long, int)> lines, Mappings dark, Mappings light, string defaultFile, string folder, string folderExpanded)
        {
            _icons = icons;
            _lines = lines;
            _dark = dark;
            _light = light;
            DefaultFile = defaultFile;
            DefaultFolder = folder;
            DefaultFolderExpanded = folderExpanded;
        }

        public string DefaultFile { get; }

        public string DefaultFolder { get; }

        public string DefaultFolderExpanded { get; }

        public static Catalog Load()
        {
            var assembly = typeof(FileIcons).Assembly;
            var icons = assembly.GetManifestResourceStream("Coder.MaterialIcons.icons.txt")
                        ?? throw new InvalidOperationException("The embedded icon set is missing.");
            using var manifestStream = assembly.GetManifestResourceStream("Coder.MaterialIcons.manifest.json")
                                       ?? throw new InvalidOperationException("The embedded icon manifest is missing.");
            using var manifest = JsonDocument.Parse(manifestStream);
            var root = manifest.RootElement;
            var light = root.GetProperty("light");
            var dark = Mappings.Read(root);
            return new Catalog(icons, Index(icons), dark, Mappings.Read(light, fallback: dark),
                root.GetProperty("file").GetString()!, root.GetProperty("folder").GetString()!, root.GetProperty("folderExpanded").GetString()!);
        }

        /// <summary>Exact file name, then the longest matching extension ("component.ts" before "ts"), then the language.</summary>
        public string FileIconName(string fileName, bool light)
        {
            var mappings = light ? _light : _dark;
            var lower = fileName.ToLowerInvariant();
            if (mappings.FileNames.TryGetValue(lower, out var byName))
            {
                return byName;
            }

            for (var dot = lower.IndexOf('.', StringComparison.Ordinal); dot >= 0 && dot < lower.Length - 1; dot = lower.IndexOf('.', dot + 1))
            {
                if (mappings.Extensions.TryGetValue(lower[(dot + 1)..], out var byExtension))
                {
                    return byExtension;
                }
            }

            // Only a recognized language counts: an unknown extension is "plain text", which is not a document.
            var language = Languages.FromFileName(fileName);
            return language != Languages.PlainText && mappings.Languages.TryGetValue(language.Id, out var byLanguage) ? byLanguage : DefaultFile;
        }

        public string FolderIconName(string folderName, bool expanded, bool light)
        {
            var mappings = light ? _light : _dark;
            var lower = folderName.ToLowerInvariant();
            return (expanded ? mappings.FoldersExpanded : mappings.Folders).TryGetValue(lower, out var name)
                ? name
                : expanded ? DefaultFolderExpanded : DefaultFolder;
        }

        public (IReadOnlyCollection<string>, IReadOnlyCollection<string>) Inventory()
        {
            var referenced = new HashSet<string>(StringComparer.Ordinal) { DefaultFile, DefaultFolder, DefaultFolderExpanded };
            foreach (var mappings in new[] { _dark, _light })
            {
                referenced.UnionWith(mappings.FileNames.Values.Concat(mappings.Extensions.Values).Concat(mappings.Folders.Values)
                    .Concat(mappings.FoldersExpanded.Values).Concat(mappings.Languages.Values));
            }

            return (_lines.Keys, referenced);
        }

        /// <summary>Builds an icon's drawing from its line; null when the set has no such icon.</summary>
        public DrawingImage? Build(string name)
        {
            if (!_lines.TryGetValue(name, out var line))
            {
                return null;
            }

            var bytes = new byte[line.Length];
            lock (_icons)
            {
                _icons.Position = line.Offset;
                _icons.ReadExactly(bytes);
            }

            var fields = Encoding.UTF8.GetString(bytes).Split('\t');
            var box = fields[1].Split(' ').Select(Number).ToArray();
            var bounds = new Rect(box[0], box[1], box[2], box[3]);
            var group = new DrawingGroup { ClipGeometry = new RectangleGeometry(bounds) }; // nothing draws outside the view box

            // A transparent backdrop pins the image's bounds to the view box, so icons align and scale alike.
            group.Children.Add(new GeometryDrawing { Brush = Brushes.Transparent, Geometry = new RectangleGeometry(bounds) });
            foreach (var layer in fields[2].Split(';'))
            {
                group.Children.Add(Layer(layer.Split('|')));
            }

            return new DrawingImage(group);
        }

        /// <summary>F|color|opacity|rule|matrix|data or S|color|opacity|width,cap,join|matrix|data.</summary>
        private static GeometryDrawing Layer(string[] f)
        {
            var brush = new SolidColorBrush(Color.Parse(f[1]), Number(f[2]));
            var m = f[4].Split(' ').Select(Number).ToArray();
            var isFill = f[0] == "F";

            // Path markup defaults to even-odd; "F1" asks for SVG's default, nonzero.
            var geometry = Geometry.Parse((isFill && f[3] == "e" ? "F0 " : "F1 ") + f[5]);
            geometry.Transform = new MatrixTransform(new Matrix(m[0], m[1], m[2], m[3], m[4], m[5]));
            if (isFill)
            {
                return new GeometryDrawing { Brush = brush, Geometry = geometry };
            }

            var stroke = f[3].Split(',');
            return new GeometryDrawing
            {
                Geometry = geometry,
                Pen = new Pen(brush, Number(stroke[0]))
                {
                    LineCap = stroke[1] switch { "round" => PenLineCap.Round, "square" => PenLineCap.Square, _ => PenLineCap.Flat },
                    LineJoin = stroke[2] switch { "round" => PenLineJoin.Round, "bevel" => PenLineJoin.Bevel, _ => PenLineJoin.Miter },
                },
            };
        }

        /// <summary>Where each line starts and how long it is, keyed by the icon name before the first tab.</summary>
        private static Dictionary<string, (long, int)> Index(Stream icons)
        {
            var index = new Dictionary<string, (long, int)>(StringComparer.Ordinal);
            var buffer = new byte[64 * 1024];
            var name = new StringBuilder();
            long lineStart = 0, position = 0;
            var inName = true;
            int read;
            icons.Position = 0;
            while ((read = icons.Read(buffer, 0, buffer.Length)) > 0)
            {
                for (var i = 0; i < read; i++, position++)
                {
                    var b = buffer[i];
                    if (b == '\n')
                    {
                        index[name.ToString()] = (lineStart, (int)(position - lineStart));
                        name.Clear();
                        lineStart = position + 1;
                        inName = true;
                    }
                    else if (inName)
                    {
                        if (b == '\t')
                        {
                            inName = false;
                        }
                        else
                        {
                            name.Append((char)b);
                        }
                    }
                }
            }

            return index;
        }

        private static double Number(string value) => double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    /// <summary>Name → icon maps for one theme variant (light entries fall back to the dark ones).</summary>
    private sealed record Mappings(
        IReadOnlyDictionary<string, string> FileNames,
        IReadOnlyDictionary<string, string> Extensions,
        IReadOnlyDictionary<string, string> Folders,
        IReadOnlyDictionary<string, string> FoldersExpanded,
        IReadOnlyDictionary<string, string> Languages)
    {
        public static Mappings Read(JsonElement element, Mappings? fallback = null) => new(
            Map(element, "fileNames", fallback?.FileNames),
            Map(element, "fileExtensions", fallback?.Extensions),
            Map(element, "folderNames", fallback?.Folders),
            Map(element, "folderNamesExpanded", fallback?.FoldersExpanded),
            Map(element, "languageIds", fallback?.Languages));

        private static Dictionary<string, string> Map(JsonElement element, string name, IReadOnlyDictionary<string, string>? fallback)
        {
            var map = fallback is null ? new Dictionary<string, string>(StringComparer.Ordinal) : new Dictionary<string, string>(fallback, StringComparer.Ordinal);
            if (element.TryGetProperty(name, out var values))
            {
                foreach (var entry in values.EnumerateObject())
                {
                    map[entry.Name.ToLowerInvariant()] = entry.Value.GetString()!;
                }
            }

            return map;
        }
    }
}
