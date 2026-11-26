using System.Runtime;
using Avalonia.Threading;

namespace Coder.App.Services;

/// <summary>
/// While Coder is minimized nobody is waiting on it, so it gives memory back: a full, compacting
/// collection (large objects included) returns freed heap pages to Windows. Done once, shortly after
/// minimizing, on the UI thread when it is idle.
/// </summary>
internal static class MemoryTrimmer
{
    private static DispatcherTimer? _timer;

    public static void TrimSoon()
    {
        _timer ??= new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) =>
        {
            _timer!.Stop();
            Trim();
        });
        _timer.Stop();
        _timer.Start();
    }

    public static void Trim()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
    }
}
