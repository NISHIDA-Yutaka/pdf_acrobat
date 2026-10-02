using PdfAcrobat.Pdfium.Interop;

namespace PdfAcrobat.Pdfium;

/// <summary>
/// Process-wide PDFium state. PDFium is not thread-safe, so every native call goes through
/// <see cref="Acquire"/>, which serializes access with a single re-entrant lock.
/// Long-running work (page rendering) releases the lock between progressive steps so that
/// short interactive calls (text hit-testing, metadata) are not starved.
/// </summary>
public static class PdfiumLibrary
{
    private static readonly object Sync = new();
    private static int _waiting;
    private static volatile bool _initialized;

    public static void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        lock (Sync)
        {
            if (_initialized)
            {
                return;
            }

            PdfiumNative.FPDF_InitLibrary();
            _initialized = true;
        }
    }

    /// <summary>Acquires the global PDFium lock. Dispose the returned scope to release it.</summary>
    public static Scope Acquire()
    {
        EnsureInitialized();
        Interlocked.Increment(ref _waiting);
        Monitor.Enter(Sync);
        Interlocked.Decrement(ref _waiting);
        return new Scope();
    }

    /// <summary>True when another thread is blocked waiting for the lock.</summary>
    internal static bool HasWaiters => Volatile.Read(ref _waiting) > 0;

    /// <summary>Called between progressive steps by a lock holder that just released the lock.</summary>
    internal static void YieldToWaiters()
    {
        for (var i = 0; i < 64 && HasWaiters; i++)
        {
            Thread.Sleep(0);
        }
    }

    public readonly struct Scope : IDisposable
    {
        public void Dispose() => Monitor.Exit(Sync);
    }
}
