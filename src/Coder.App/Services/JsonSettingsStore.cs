using System.Text.Json;
using System.Text.Json.Serialization;
using Coder.Presentation.Settings;

namespace Coder.App.Services;

/// <summary>
/// Stores settings as JSON in the user's app-data folder (e.g. <c>%APPDATA%\Coder\settings.json</c>).
/// A missing or corrupt file never blocks startup: defaults are used instead.
/// </summary>
public sealed class JsonSettingsStore(string filePath) : ISettingsStore
{
    public string FilePath => filePath;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Coder", "settings.json");

    public UserSettings Load()
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            return Repair(JsonSerializer.Deserialize(stream, SettingsJsonContext.Default.UserSettings) ?? new UserSettings());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new UserSettings();
        }
    }

    /// <summary>
    /// A key missing from the file (written by an older version, or by hand) or set to null comes back
    /// as a null list, not the default: those become empty lists, so the rest of the app never sees null.
    /// </summary>
    private static UserSettings Repair(UserSettings settings) => settings with
    {
        RecentFolders = settings.RecentFolders ?? [],
        ColorTheme = settings.ColorTheme ?? "dark",
        LanguageServers = (settings.LanguageServers ?? [])
            .Where(server => server is not null)
            .Select(server => server with
            {
                Name = server.Name ?? string.Empty,
                Command = server.Command ?? string.Empty,
                Languages = server.Languages ?? [],
                Arguments = server.Arguments ?? [],
                RootMarkers = server.RootMarkers ?? [],
            })
            .ToList(),
    };

    public void Save(UserSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            var temp = filePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, SettingsJsonContext.Default.UserSettings));
            File.Move(temp, filePath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Losing preferences is not worth interrupting the user while the app closes.
        }
    }
}

/// <summary>Compile-time JSON serializer (no reflection), so settings stay trim- and AOT-safe.</summary>
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(UserSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
