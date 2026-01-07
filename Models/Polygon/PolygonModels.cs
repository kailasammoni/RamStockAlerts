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
/// Response from Polygon.io /v3/snapshot endpoint
/// </summary>
public class PolygonSnapshotResponse
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
