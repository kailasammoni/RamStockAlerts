using System.Text.Json.Serialization;

namespace RamStockAlerts.Models.Polygon;

/// <summary>
/// Response from Polygon.io /v2/aggs/ticker/{ticker}/prev endpoint
/// </summary>
public class PolygonAggregateResponse
{
    [JsonPropertyName("ticker")]
    public string? Ticker { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("resultsCount")]
    public int ResultsCount { get; set; }

    [JsonPropertyName("results")]
    public List<PolygonAggregate>? Results { get; set; }
}

public class PolygonAggregate
{
    [JsonPropertyName("T")]
    public string? Ticker { get; set; }

    [JsonPropertyName("v")]
    public decimal Volume { get; set; }

    [JsonPropertyName("vw")]
    public decimal Vwap { get; set; }

    [JsonPropertyName("o")]
    public decimal Open { get; set; }

    [JsonPropertyName("c")]
    public decimal Close { get; set; }

    [JsonPropertyName("h")]
    public decimal High { get; set; }

    [JsonPropertyName("l")]
    public decimal Low { get; set; }

    [JsonPropertyName("t")]
    public long Timestamp { get; set; }

    [JsonPropertyName("n")]
    public int NumberOfTransactions { get; set; }
}

/// <summary>
/// Response from Polygon.io /v3/quotes/{ticker} endpoint
/// </summary>
public class PolygonQuoteResponse
{
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("results")]
    public PolygonQuote? Results { get; set; }
}

public class PolygonQuote
{
    [JsonPropertyName("P")]
    public decimal AskPrice { get; set; }

    [JsonPropertyName("S")]
    public decimal AskSize { get; set; }

    [JsonPropertyName("p")]
    public decimal BidPrice { get; set; }

    [JsonPropertyName("s")]
    public decimal BidSize { get; set; }

    [JsonPropertyName("t")]
    public long Timestamp { get; set; }
}

/// <summary>
/// Response from Polygon.io /v3/reference/tickers endpoint
/// </summary>
public class PolygonTickerListResponse
{
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("results")]
    public List<PolygonTickerInfo>? Results { get; set; }

    [JsonPropertyName("count")]
    public int Count { get; set; }

    [JsonPropertyName("next_url")]
    public string? NextUrl { get; set; }
}

public class PolygonTickerInfo
{
    [JsonPropertyName("ticker")]
    public string? Ticker { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("market")]
    public string? Market { get; set; }

    [JsonPropertyName("locale")]
    public string? Locale { get; set; }

    [JsonPropertyName("primary_exchange")]
    public string? PrimaryExchange { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("active")]
    public bool Active { get; set; }

    [JsonPropertyName("currency_name")]
    public string? CurrencyName { get; set; }

    [JsonPropertyName("updated")]
    public string? Updated { get; set; }
}

/// <summary>
/// Response from Polygon.io /v3/snapshot/stocks endpoint (live market data)
/// </summary>
public class PolygonSnapshotResponse
{
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("results")]
    public List<PolygonStockSnapshot>? Results { get; set; }

    [JsonPropertyName("count")]
    public int Count { get; set; }

    [JsonPropertyName("next_url")]
    public string? NextUrl { get; set; }
}

/// <summary>
/// Individual stock snapshot from /v3/snapshot/stocks
/// </summary>
public class PolygonStockSnapshot
{
    [JsonPropertyName("ticker")]
    public string? Ticker { get; set; }

    [JsonPropertyName("lastTrade")]
    public PolygonSnapshotTrade? LastTrade { get; set; }

    [JsonPropertyName("lastQuote")]
    public PolygonSnapshotQuote? LastQuote { get; set; }

    [JsonPropertyName("bid")]
    public PolygonBidAsk? Bid { get; set; }

    [JsonPropertyName("ask")]
    public PolygonBidAsk? Ask { get; set; }

    [JsonPropertyName("preMarket")]
    public PolygonMarketSession? PreMarket { get; set; }

    [JsonPropertyName("postMarket")]
    public PolygonMarketSession? PostMarket { get; set; }

    [JsonPropertyName("regularMarket")]
    public PolygonMarketSession? RegularMarket { get; set; }

    [JsonPropertyName("updated")]
    public long Updated { get; set; }
}

public class PolygonBidAsk
{
    [JsonPropertyName("price")]
    public decimal Price { get; set; }

    [JsonPropertyName("size")]
    public double Size { get; set; }

    [JsonPropertyName("exchange")]
    public int Exchange { get; set; }

    [JsonPropertyName("timeframe")]
    public string? Timeframe { get; set; }
}

public class PolygonMarketSession
{
    [JsonPropertyName("change")]
    public decimal Change { get; set; }

    [JsonPropertyName("changePercent")]
    public decimal ChangePercent { get; set; }

    [JsonPropertyName("volume")]
    public double? Volume { get; set; }

    [JsonPropertyName("vwap")]
    public decimal? Vwap { get; set; }

    [JsonPropertyName("updated")]
    public long Updated { get; set; }
}

/// <summary>
/// Legacy snapshot response (kept for backward compatibility)
/// </summary>
public class PolygonSnapshotResponseLegacy
{
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("tickers")]
    public List<PolygonTickerSnapshot>? Tickers { get; set; }
}

public class PolygonTickerSnapshot
{
    [JsonPropertyName("ticker")]
    public string? Ticker { get; set; }

    [JsonPropertyName("day")]
    public PolygonAggregate? Day { get; set; }

    [JsonPropertyName("lastQuote")]
    public PolygonSnapshotQuote? LastQuote { get; set; }

    [JsonPropertyName("lastTrade")]
    public PolygonSnapshotTrade? LastTrade { get; set; }

    [JsonPropertyName("prevDay")]
    public PolygonAggregate? PrevDay { get; set; }
}

public class PolygonSnapshotQuote
{
    [JsonPropertyName("P")]
    public decimal AskPrice { get; set; }

    [JsonPropertyName("S")]
    public decimal AskSize { get; set; }

    [JsonPropertyName("p")]
    public decimal BidPrice { get; set; }

    [JsonPropertyName("s")]
    public decimal BidSize { get; set; }
}

public class PolygonSnapshotTrade
{
    [JsonPropertyName("p")]
    public decimal Price { get; set; }

    [JsonPropertyName("s")]
    public decimal Size { get; set; }

    [JsonPropertyName("t")]
    public long Timestamp { get; set; }
}
