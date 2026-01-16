using RamStockAlerts.Services;

namespace RamStockAlerts.Tests.TestDoubles;

internal sealed class TestRequestIdSource : IRequestIdSource
{
    private int _nextId = 1000;
    public int NextId() => ++_nextId;
}
