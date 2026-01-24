namespace RamStockAlerts.Execution.Services;

using System.Collections.Concurrent;
using IBApi;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RamStockAlerts.Execution.Contracts;
using RamStockAlerts.Execution.Interfaces;

/// <summary>
/// IBKR broker client for Paper/Live execution via TWS/Gateway API.
/// </summary>
public sealed class IbkrBrokerClient : IBrokerClient, IDisposable
{
    private readonly ILogger<IbkrBrokerClient> _logger;
    private readonly IConfiguration _configuration;

    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private EClientSocket? _socket;
    private EReaderSignal? _signal;
    private EReader? _reader;
    private Wrapper? _wrapper;
    private CancellationTokenSource? _readerCts;

    private TaskCompletionSource<int> _nextValidIdTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int _nextOrderId;

    public string Name => "IBKR";

    public IbkrBrokerClient(ILogger<IbkrBrokerClient> logger, IConfiguration configuration)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    }

    public async Task<ExecutionResult> PlaceAsync(OrderIntent intent, CancellationToken ct = default)
    {
        if (intent is null)
            throw new ArgumentNullException(nameof(intent));

        try
        {
            await EnsureConnectedAsync(ct);

            var orderId = ReserveOrderIds(1);
            var contract = BuildStockContract(intent);
            var order = BuildOrder(intent);

            _socket!.placeOrder(orderId, contract, order);

            return new ExecutionResult
            {
                IntentId = intent.IntentId,
                Status = ExecutionStatus.Submitted,
                BrokerName = Name,
                BrokerOrderIds = new List<string> { orderId.ToString() },
                TimestampUtc = DateTimeOffset.UtcNow
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "[IBKR] PlaceAsync failed for {Symbol}", intent.Symbol);
            return new ExecutionResult
            {
                IntentId = intent.IntentId,
                Status = ExecutionStatus.Error,
                RejectionReason = ex.Message,
                BrokerName = Name,
                TimestampUtc = DateTimeOffset.UtcNow
            };
        }
    }

    public async Task<ExecutionResult> PlaceBracketAsync(BracketIntent intent, CancellationToken ct = default)
    {
        if (intent is null)
            throw new ArgumentNullException(nameof(intent));

        try
        {
            await EnsureConnectedAsync(ct);

            var entry = intent.Entry ?? throw new ArgumentException("BracketIntent.Entry is required", nameof(intent));

            var orderIds = ReserveOrderIds(3);
            var parentId = orderIds;
            var stopId = orderIds + 1;
            var tpId = orderIds + 2;

            var contract = BuildStockContract(entry);

            var brokerOrderIds = new List<string> { parentId.ToString() };

            var parent = BuildOrder(entry);
            parent.Transmit = intent.StopLoss is null && intent.TakeProfit is null;
            _socket!.placeOrder(parentId, contract, parent);

            if (intent.TakeProfit is not null)
            {
                var tp = BuildOrder(intent.TakeProfit);
                tp.ParentId = parentId;
                tp.Transmit = intent.StopLoss is null;
                _socket.placeOrder(tpId, contract, tp);
                brokerOrderIds.Add(tpId.ToString());
            }

            if (intent.StopLoss is not null)
            {
                var stop = BuildOrder(intent.StopLoss);
                stop.ParentId = parentId;
                stop.Transmit = true;
                _socket.placeOrder(stopId, contract, stop);
                brokerOrderIds.Add(stopId.ToString());
            }

            return new ExecutionResult
            {
                IntentId = entry.IntentId,
                Status = ExecutionStatus.Submitted,
                BrokerName = Name,
                BrokerOrderIds = brokerOrderIds,
                TimestampUtc = DateTimeOffset.UtcNow
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "[IBKR] PlaceBracketAsync failed for {Symbol}", intent.Entry?.Symbol);
            return new ExecutionResult
            {
                IntentId = intent.Entry?.IntentId ?? Guid.Empty,
                Status = ExecutionStatus.Error,
                RejectionReason = ex.Message,
                BrokerName = Name,
                TimestampUtc = DateTimeOffset.UtcNow
            };
        }
    }

    public async Task<ExecutionResult> CancelAsync(string brokerOrderId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(brokerOrderId))
            throw new ArgumentException("Broker order ID cannot be empty", nameof(brokerOrderId));

        try
        {
            await EnsureConnectedAsync(ct);

            if (!int.TryParse(brokerOrderId, out var id))
            {
                throw new ArgumentException("IBKR brokerOrderId must be an integer orderId", nameof(brokerOrderId));
            }

            _socket!.cancelOrder(id, new OrderCancel());

            return new ExecutionResult
            {
                Status = ExecutionStatus.Cancelled,
                BrokerName = Name,
                BrokerOrderIds = new List<string> { brokerOrderId },
                TimestampUtc = DateTimeOffset.UtcNow
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "[IBKR] CancelAsync failed for orderId={OrderId}", brokerOrderId);
            return new ExecutionResult
            {
                Status = ExecutionStatus.Error,
                RejectionReason = ex.Message,
                BrokerName = Name,
                BrokerOrderIds = new List<string> { brokerOrderId },
                TimestampUtc = DateTimeOffset.UtcNow
            };
        }
    }

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (_socket?.IsConnected() == true)
        {
            return;
        }

        await _connectLock.WaitAsync(ct);
        try
        {
            if (_socket?.IsConnected() == true)
            {
                return;
            }

            CleanupConnection();
            _nextValidIdTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

            var host = _configuration["IBKR:Host"] ?? "127.0.0.1";
            var port = _configuration.GetValue<int?>("IBKR:Port") ?? 7497;
            var baseClientId = _configuration.GetValue<int?>("IBKR:ClientId") ?? 1;
            var execClientId = _configuration.GetValue<int?>("IBKR:ExecutionClientId") ?? (baseClientId + 10);

            _logger.LogInformation("[IBKR] Connecting execution client to {Host}:{Port} clientId={ClientId}", host, port, execClientId);

            _signal = new EReaderMonitorSignal();
            _wrapper = new Wrapper(_logger, _nextValidIdTcs);
            _socket = new EClientSocket(_wrapper, _signal);
            _socket.eConnect(host, port, execClientId);

            if (!_socket.IsConnected())
            {
                throw new InvalidOperationException($"IBKR execution connect failed to {host}:{port}");
            }

            _socket.startApi();

            _reader = new EReader(_socket, _signal);
            _reader.Start();

            _readerCts = new CancellationTokenSource();
            _ = Task.Run(() => ReadLoop(_reader, _signal, _readerCts.Token), _readerCts.Token);

            // Wait for nextValidId
            var id = await _nextValidIdTcs.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            _nextOrderId = id;

            _logger.LogInformation("[IBKR] Execution client ready. nextValidId={NextValidId}", _nextOrderId);
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private int ReserveOrderIds(int count)
    {
        if (count <= 0)
            throw new ArgumentOutOfRangeException(nameof(count));

        var start = Interlocked.Add(ref _nextOrderId, count) - count;
        return start;
    }

    private static Contract BuildStockContract(OrderIntent intent)
    {
        var symbol = intent.Symbol?.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(symbol))
        {
            throw new ArgumentException("Symbol is required", nameof(intent));
        }

        var exchange = intent.Tags?.TryGetValue("Exchange", out var exch) == true && !string.IsNullOrWhiteSpace(exch)
            ? exch.Trim().ToUpperInvariant()
            : "SMART";

        var currency = intent.Tags?.TryGetValue("Currency", out var ccy) == true && !string.IsNullOrWhiteSpace(ccy)
            ? ccy.Trim().ToUpperInvariant()
            : "USD";

        var primaryExchange = intent.Tags?.TryGetValue("PrimaryExchange", out var pex) == true && !string.IsNullOrWhiteSpace(pex)
            ? pex.Trim().ToUpperInvariant()
            : null;

        return new Contract
        {
            Symbol = symbol,
            SecType = "STK",
            Currency = currency,
            Exchange = exchange,
            PrimaryExch = primaryExchange ?? string.Empty
        };
    }

    private static Order BuildOrder(OrderIntent intent)
    {
        var qty = ResolveQuantity(intent);

        var order = new Order
        {
            Action = intent.Side == OrderSide.Buy ? "BUY" : "SELL",
            TotalQuantity = qty,
            Tif = intent.Tif == TimeInForce.Gtc ? "GTC" : "DAY",
            Transmit = true
        };

        switch (intent.Type)
        {
            case OrderType.Market:
                order.OrderType = "MKT";
                break;
            case OrderType.Limit:
                order.OrderType = "LMT";
                order.LmtPrice = (double)(intent.LimitPrice ?? throw new ArgumentException("LimitPrice is required for Limit orders", nameof(intent)));
                break;
            case OrderType.Stop:
                order.OrderType = "STP";
                order.AuxPrice = (double)(intent.StopPrice ?? throw new ArgumentException("StopPrice is required for Stop orders", nameof(intent)));
                break;
            case OrderType.StopLimit:
                order.OrderType = "STP LMT";
                order.AuxPrice = (double)(intent.StopPrice ?? throw new ArgumentException("StopPrice is required for StopLimit orders", nameof(intent)));
                order.LmtPrice = (double)(intent.LimitPrice ?? throw new ArgumentException("LimitPrice is required for StopLimit orders", nameof(intent)));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(intent), $"Unsupported order type: {intent.Type}");
        }

        return order;
    }

    private static decimal ResolveQuantity(OrderIntent intent)
    {
        if (intent.Quantity is not null)
        {
            var q = Math.Floor(intent.Quantity.Value);
            if (q <= 0)
                throw new ArgumentException("Quantity must be > 0", nameof(intent));
            return q;
        }

        if (intent.NotionalUsd is not null)
        {
            if (intent.LimitPrice is null || intent.LimitPrice <= 0)
                throw new ArgumentException("NotionalUsd requires a positive LimitPrice to derive share quantity", nameof(intent));

            var q = Math.Floor(intent.NotionalUsd.Value / intent.LimitPrice.Value);
            if (q <= 0)
                throw new ArgumentException("NotionalUsd too small to derive at least 1 share", nameof(intent));
            return q;
        }

        throw new ArgumentException("Either Quantity or NotionalUsd must be specified", nameof(intent));
    }

    private static void ReadLoop(EReader reader, EReaderSignal signal, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            signal.waitForSignal();
            reader.processMsgs();
        }
    }

    private void CleanupConnection()
    {
        try
        {
            _readerCts?.Cancel();
        }
        catch
        {
            // ignore
        }

        try
        {
            if (_socket?.IsConnected() == true)
            {
                _socket.eDisconnect();
            }
        }
        catch
        {
            // ignore
        }

        _readerCts?.Dispose();
        _readerCts = null;
        _reader = null;
        _signal = null;
        _socket = null;
        _wrapper = null;
    }

    public void Dispose()
    {
        CleanupConnection();
        _connectLock.Dispose();
    }

    private sealed class Wrapper : DefaultEWrapper
    {
        private readonly ILogger _logger;
        private readonly TaskCompletionSource<int> _nextValidIdTcs;

        public Wrapper(ILogger logger, TaskCompletionSource<int> nextValidIdTcs)
        {
            _logger = logger;
            _nextValidIdTcs = nextValidIdTcs;
        }

        public override void nextValidId(int orderId) => _nextValidIdTcs.TrySetResult(orderId);

        public override void error(int id, long errorTime, int errorCode, string errorMsg, string advancedOrderRejectJson)
        {
            if (errorCode is 2104 or 2106 or 2158 or 2107)
            {
                return;
            }

            _logger.LogWarning("[IBKR] error id={Id} code={Code} msg={Msg}", id, errorCode, errorMsg);
        }

        public override void error(string str) => _logger.LogWarning("[IBKR] error: {Message}", str);
        public override void error(Exception e) => _logger.LogWarning(e, "[IBKR] exception");

        public override void connectionClosed() => _logger.LogWarning("[IBKR] Connection closed");

        // Optional fill tracking (best-effort).
        public override void orderStatus(int orderId, string status, decimal filled, decimal remaining, double avgFillPrice, long permId, int parentId, double lastFillPrice, int clientId, string whyHeld, double mktCapPrice) { }
        public override void execDetails(int reqId, Contract contract, IBApi.Execution execution) { }
        public override void commissionAndFeesReport(CommissionAndFeesReport commissionAndFeesReport) { }
    }
}
