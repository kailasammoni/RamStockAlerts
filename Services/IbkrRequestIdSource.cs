using System;
using Microsoft.Extensions.Configuration;
using System.Threading;

namespace RamStockAlerts.Services;

public interface IRequestIdSource
{
    int NextId();
}

public sealed class IbkrRequestIdSource : IRequestIdSource
{
    private int _nextId;

    public IbkrRequestIdSource(IConfiguration configuration)
    {
        _nextId = Math.Max(0, configuration.GetValue("IBKR:RequestIdSeed", 1000));
    }

    public int NextId()
    {
        return Interlocked.Increment(ref _nextId);
    }
}
