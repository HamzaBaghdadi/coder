using Avalonia.Media.Imaging;

namespace Coder.App.Services;

/// <summary>
/// Extension icons for the Extensions view: downloaded once per address (or read from an installed
/// extension's folder), decoded at the size shown so a page of results costs little memory.
/// </summary>
internal static class ExtensionIcons
{
    private const int PixelSize = 64; // 32 px at 200% scaling
    private const int MaxBytes = 512 * 1024;
    private static readonly HttpClient Http = CreateHttp();
    private static readonly Dictionary<string, Task<Bitmap?>> Cache = [];

    public static Task<Bitmap?> LoadAsync(string address)
    {
        if (!Cache.TryGetValue(address, out var task))
        {
            task = Cache[address] = FetchAsync(address);
        }

        return task;
    }

    private static async Task<Bitmap?> FetchAsync(string address)
    {
        try
        {
            byte[] bytes;
            if (address.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                using var response = await Http.GetAsync(address, HttpCompletionOption.ResponseHeadersRead);
                if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxBytes)
                {
                    return null;
                }

                bytes = await response.Content.ReadAsByteArrayAsync();
            }
            else
            {
                bytes = new FileInfo(address).Length > MaxBytes ? [] : await File.ReadAllBytesAsync(address);
            }

            if (bytes.Length is 0 or > MaxBytes)
            {
                return null;
            }

            using var stream = new MemoryStream(bytes);
            return Bitmap.DecodeToWidth(stream, PixelSize);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException or ArgumentException or InvalidOperationException)
        {
            return null; // an SVG or broken image: the row keeps its placeholder
        }
    }

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Coder/0.1");
        return http;
    }
}
