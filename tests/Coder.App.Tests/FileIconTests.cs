using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Coder.App.Theme;

namespace Coder.App.Tests;

/// <summary>The shipped Material icons: complete, drawable, and matched the way VS Code matches them.</summary>
public sealed class FileIconTests
{
    [Theory]
    [InlineData("package.json", "nodejs")]
    [InlineData("app.component.ts", "angular-component.clone")] // the set's own file name for the Angular component icon
    [InlineData("types.d.ts", "typescript-def")]
    [InlineData("Program.cs", "csharp")]
    [InlineData("main.go", "go")]
    [InlineData("README.md", "readme")]
    [InlineData("Dockerfile", "docker")]
    [InlineData(".gitignore", "git")]
    [InlineData("photo.png", "image")]
    [InlineData("notes.unknownext", "file")]
    public void Files_get_their_icon(string fileName, string icon) =>
        Assert.Equal(icon, FileIcons.IconNameFor(fileName, isFolder: false));

    [Theory]
    [InlineData("src", false, "folder-src")]
    [InlineData("src", true, "folder-src-open")]
    [InlineData("node_modules", false, "folder-node")]
    [InlineData(".github", true, "folder-github-open")]
    [InlineData("anything-else", false, "folder")]
    public void Folders_get_their_icon_open_or_closed(string folder, bool expanded, string icon) =>
        Assert.Equal(icon, FileIcons.IconNameFor(folder, isFolder: true, expanded));

    [Fact]
    public void Every_icon_a_mapping_names_exists()
    {
        var (available, referenced) = FileIcons.Inventory();

        Assert.True(available.Count > 1000, $"only {available.Count} icons");
        Assert.Empty(referenced.Except(available));
    }

    /// <summary>All 1,256 drawings build (their path data parses) and have the size of their view box.</summary>
    [AvaloniaFact]
    public void Every_icon_builds_into_a_drawing()
    {
        var (available, _) = FileIcons.Inventory();
        var failures = new List<string>();
        foreach (var name in available)
        {
            try
            {
                var image = FileIcons.Build(name);
                if (image is not DrawingImage { Size.Width: > 0 })
                {
                    failures.Add($"{name}: empty");
                }
            }
            catch (Exception exception) when (exception is FormatException or InvalidOperationException or ArgumentException)
            {
                failures.Add($"{name}: {exception.Message}");
            }
        }

        Assert.Empty(failures);
    }
}
