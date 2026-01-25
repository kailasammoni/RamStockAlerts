using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace RamStockAlerts.Services;

public sealed class SystemSleepPreventer : BackgroundService
{
    private readonly ILogger<SystemSleepPreventer> _logger;
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromMinutes(5);
    private const uint ExecutionStateFlags = 0x80000000 | 0x00000001;

    public SystemSleepPreventer(ILogger<SystemSleepPreventer> logger)
    {
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            _logger.LogDebug("[System] Keep-awake helper skipped on non-Windows platform.");
            return;
        }

        _logger.LogInformation("[System] Keep-awake helper running, refreshing execution state every {Interval} minutes.", KeepAliveInterval.TotalMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            var result = SetThreadExecutionState(ExecutionStateFlags);
            if (result == 0)
            {
                _logger.LogWarning("[System] SetThreadExecutionState failed (sleep prevention may not be active).");
            }
            else
            {
                _logger.LogInformation("[System] Prevented system sleep at {Time}.", DateTimeOffset.UtcNow);
            }

            try
            {
                await Task.Delay(KeepAliveInterval, stoppingToken);
            }
            catch (TaskCanceledException)
            {
                return;
            }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SetThreadExecutionState(uint esFlags);
}
