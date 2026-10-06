using System.Diagnostics;
using System.Threading;

namespace SFTLauncher.Download;

/// <summary>
/// 进度感知的下载停滞看门狗。移植自 NyaLauncher.Core.Download.InstallStallWatchdog
/// </summary>
internal sealed class InstallStallWatchdog : IDisposable
{
    private readonly CancellationTokenSource _owner;
    private readonly TimeSpan _timeout;
    private readonly Timer _timer;
    private long _lastActivityTimestamp = Stopwatch.GetTimestamp();
    private int _disposed;

    public InstallStallWatchdog(CancellationTokenSource owner, TimeSpan timeout)
    {
        _owner = owner;
        _timeout = timeout;
        _timer = new Timer(
            static self => ((InstallStallWatchdog)self!).CheckStall(),
            this,
            TimeSpan.FromSeconds(15),
            TimeSpan.FromSeconds(15));
    }

    public void Touch() => Volatile.Write(ref _lastActivityTimestamp, Stopwatch.GetTimestamp());

    private void CheckStall()
    {
        try
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            var idleSeconds = (Stopwatch.GetTimestamp() - Volatile.Read(ref _lastActivityTimestamp))
                              / (double)Stopwatch.Frequency;
            if (idleSeconds >= _timeout.TotalSeconds)
                _owner.Cancel();
        }
        catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        Volatile.Write(ref _disposed, 1);
        _timer.Dispose();
    }
}
