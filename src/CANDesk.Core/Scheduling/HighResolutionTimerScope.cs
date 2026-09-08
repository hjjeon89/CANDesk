using System.Runtime.Versioning;

namespace CANDesk.Core.Scheduling;

/// <summary>
/// Reference-counted <c>timeBeginPeriod(1)</c> scope. Windows' default timer resolution
/// (~15.6ms) causes visible jitter on short-period (1-10ms) cyclic TX jobs; raising it to 1ms
/// while any cyclic job is running keeps <see cref="PeriodicTimer"/> ticks close to the
/// requested period. No-op on non-Windows platforms.
/// </summary>
internal static partial class HighResolutionTimerScope
{
    [System.Runtime.InteropServices.LibraryImport("winmm.dll")]
    [SupportedOSPlatform("windows")]
    private static partial uint timeBeginPeriod(uint uPeriod);

    [System.Runtime.InteropServices.LibraryImport("winmm.dll")]
    [SupportedOSPlatform("windows")]
    private static partial uint timeEndPeriod(uint uPeriod);

    private static readonly object Gate = new();
    private static int _refCount;

    public static IDisposable Acquire()
    {
        if (!OperatingSystem.IsWindows())
        {
            return NullScope.Instance;
        }

        lock (Gate)
        {
            if (_refCount++ == 0)
            {
                timeBeginPeriod(1);
            }
        }

        return new ReleaseScope();
    }

    [SupportedOSPlatform("windows")]
    private sealed class ReleaseScope : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            lock (Gate)
            {
                if (--_refCount == 0)
                {
                    timeEndPeriod(1);
                }
            }
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose()
        {
        }
    }
}
