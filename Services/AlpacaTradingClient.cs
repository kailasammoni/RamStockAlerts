using Alpaca.Markets;
using RamStockAlerts.Models;

namespace RamStockAlerts.Services;

/// <summary>
/// Alpaca paper trading client for automated order execution.
/// </summary>
public class AlpacaTradingClient
{
    private IAlpacaTradingClient? _alpacaClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AlpacaTradingClient> _logger;
    private readonly bool _enableAutoTrading;
    private readonly decimal _maxPositionSize;
    private readonly decimal _minScore;

    public AlpacaTradingClient(
        IConfiguration configuration,
        ILogger<AlpacaTradingClient> logger)
    {
        _configuration = configuration;
        _logger = logger;
        
        var apiKey = configuration["Alpaca:Key"] ?? "";
        var apiSecret = configuration["Alpaca:Secret"] ?? "";
        var usePaperTrading = configuration.GetValue("Alpaca:UsePaperTrading", true);
        
        _enableAutoTrading = configuration.GetValue("Alpaca:EnableAutoTrading", false);
        _maxPositionSize = configuration.GetValue("Alpaca:MaxPositionSize", 1000m);
        _minScore = configuration.GetValue("Alpaca:MinScoreForAutoTrade", 8.5m);

        if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(apiSecret))
        {
            _logger.LogWarning("Alpaca trading credentials not configured. Auto-trading is disabled.");
            return;
        }

        // Initialize Alpaca client
        var secretKey = new SecretKey(apiKey, apiSecret);
        var alpacaMarkets = Alpaca.Markets.Environments.Paper;
        _alpacaClient = alpacaMarkets.GetAlpacaTradingClient(secretKey);

        if (usePaperTrading)
        {
            _logger.LogInformation("Alpaca paper trading client initialized. Auto-trading: {Enabled}", _enableAutoTrading);
        }
        else
        {
            _logger.LogWarning("Live trading mode detected - ensure this is intentional!");
        }
    }

    /// <summary>
    /// Place a bracket order (entry + stop + target) based on a trade signal.
    /// </summary>
    public async Task<string?> PlaceBracketOrderAsync(TradeSignal signal, CancellationToken cancellationToken = default)
    {
        if (_alpacaClient == null)
        {
            _logger.LogWarning("Alpaca client not initialized. Cannot place order for {Ticker}", signal.Ticker);
            return null;
        }

        if (!_enableAutoTrading)
        {
            _logger.LogInformation(
                "Auto-trading disabled. Would have placed order for {Ticker} @ ${Entry}",
                signal.Ticker, signal.Entry);
            return null;
        }

        if (signal.Score < _minScore)
        {
            _logger.LogInformation(
                "Signal score {Score} below minimum {MinScore} for auto-trading. Skipping {Ticker}",
                signal.Score, _minScore, signal.Ticker);
            return null;
        }

        if (!signal.PositionSize.HasValue || signal.PositionSize.Value <= 0)
        {
            _logger.LogWarning("Signal for {Ticker} has no position size. Cannot place order.", signal.Ticker);
            return null;
        }

        // Cap position size at configured maximum
        var quantity = Math.Min(signal.PositionSize.Value, (int)_maxPositionSize);

        try
        {
            // Check if market is open
            var clock = await _alpacaClient.GetClockAsync(cancellationToken);
            if (!clock.IsOpen)
            {
                _logger.LogWarning("Market is closed. Cannot place order for {Ticker}", signal.Ticker);
                return null;
            }

            // Check if we already have a position in this ticker
            var existingPosition = await GetPositionAsync(signal.Ticker, cancellationToken);
            if (existingPosition != null)
            {
                _logger.LogWarning(
                    "Already have position in {Ticker} ({Qty} shares). Skipping new order.",
                    signal.Ticker, existingPosition.Quantity);
                return null;
            }

            // Create bracket order (entry + stop loss + take profit)
            var orderRequest = new NewOrderRequest(
                symbol: signal.Ticker,
                quantity: OrderQuantity.Fractional((decimal)quantity),
                side: OrderSide.Buy,
                type: OrderType.Limit,
                duration: TimeInForce.Day)
            {
                LimitPrice = signal.Entry,
                StopPrice = signal.Stop,
                TakeProfitLimitPrice = signal.Target
            };

            var order = await _alpacaClient.PostOrderAsync(orderRequest, cancellationToken);

            _logger.LogInformation(
                "✅ Bracket order placed: {Ticker} | Entry: ${Entry} | Stop: ${Stop} | Target: ${Target} | Qty: {Qty} | OrderId: {OrderId}",
                signal.Ticker, signal.Entry, signal.Stop, signal.Target, quantity, order.OrderId);

            return order.OrderId.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, 
                "Failed to place order for {Ticker} @ ${Entry}",
                signal.Ticker, signal.Entry);
            return null;
        }
    }

    /// <summary>
    /// Get current account information.
    /// </summary>
    public async Task<IAccount?> GetAccountAsync(CancellationToken cancellationToken = default)
    {
        if (_alpacaClient == null)
            return null;

        try
        {
            return await _alpacaClient.GetAccountAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch account information");
            return null;
        }
    }

    /// <summary>
    /// Get current position for a ticker.
    /// </summary>
    public async Task<IPosition?> GetPositionAsync(string ticker, CancellationToken cancellationToken = default)
    {
        if (_alpacaClient == null)
            return null;

        try
        {
            return await _alpacaClient.GetPositionAsync(ticker, cancellationToken);
        }
        catch (RestClientErrorException ex) when (ex.ErrorCode == 40410000)
        {
            // Position not found - this is expected
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch position for {Ticker}", ticker);
            return null;
        }
    }

    /// <summary>
    /// Get all open positions.
    /// </summary>
    public async Task<IReadOnlyList<IPosition>> GetAllPositionsAsync(CancellationToken cancellationToken = default)
    {
        if (_alpacaClient == null)
            return Array.Empty<IPosition>();

        try
        {
            return await _alpacaClient.ListPositionsAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch positions");
            return Array.Empty<IPosition>();
        }
    }

    /// <summary>
    /// Cancel an order.
    /// </summary>
    public async Task<bool> CancelOrderAsync(Guid orderId, CancellationToken cancellationToken = default)
    {
        if (_alpacaClient == null)
            return false;

        try
        {
            await _alpacaClient.CancelOrderAsync(orderId, cancellationToken);
            _logger.LogInformation("Order {OrderId} cancelled", orderId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cancel order {OrderId}", orderId);
            return false;
        }
    }

    /// <summary>
    /// Get order status.
    /// </summary>
    public async Task<IOrder?> GetOrderAsync(Guid orderId, CancellationToken cancellationToken = default)
    {
        if (_alpacaClient == null)
            return null;

        try
        {
            return await _alpacaClient.GetOrderAsync(orderId, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch order {OrderId}", orderId);
            return null;
        }
    }
}
