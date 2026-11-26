using Coder.App.Services;
using Coder.Presentation.Settings;

namespace Coder.App.Tests;

public sealed class JsonSettingsStoreTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("coder-settings-").FullName;

    private string FilePath => Path.Combine(_directory, "nested", "settings.json");

    [Fact]
    public void Settings_round_trip_through_json()
    {
        var store = new JsonSettingsStore(FilePath);
        var saved = new UserSettings
        {
            RecentFolders = ["C:/a", "C:/b"],
            EditorFontSize = 16,
            GpuAcceleration = false,
            Window = new WindowPlacement(10, 20, 900, 700, IsMaximized: true),
        };

        store.Save(saved);
        var loaded = store.Load();

        Assert.Equal(saved.RecentFolders, loaded.RecentFolders);
        Assert.Equal(16, loaded.EditorFontSize);
        Assert.False(loaded.GpuAcceleration);
        Assert.Equal(saved.Window, loaded.Window);
    }

    [Fact]
    public void Missing_or_corrupt_files_fall_back_to_defaults()
    {
        var store = new JsonSettingsStore(FilePath);
        Assert.Equal(UserSettings.DefaultEditorFontSize, store.Load().EditorFontSize);

        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, "{ not json");
        Assert.True(store.Load().GpuAcceleration);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
