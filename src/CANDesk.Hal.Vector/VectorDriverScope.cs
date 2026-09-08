using System.Runtime.Versioning;

namespace CANDesk.Hal.Vector;

/// <summary>Reference-counted <c>xlOpenDriver</c>/<c>xlCloseDriver</c> scope: the XL Driver Library
/// must be opened once per process before any port is used and closed once no channel needs it
/// anymore. Mirrors <c>CANDesk.Core.Scheduling.HighResolutionTimerScope</c>'s pattern so multiple
/// Vector channels opened concurrently share one driver session.</summary>
[SupportedOSPlatform("windows")]
internal static class VectorDriverScope
{
    private static readonly object Gate = new();
    private static int _refCount;

    public static IDisposable Acquire()
    {
        lock (Gate)
        {
            if (_refCount++ == 0)
            {
                VectorXlNative.OpenDriver();
            }
        }

        return new ReleaseScope();
    }

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
                    VectorXlNative.CloseDriver();
                }
            }
        }
    }
}
