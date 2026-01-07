# RamStockAlerts - Production-Grade Migration Guide

This document describes the production-grade features implemented in RamStockAlerts.

## Features Implemented

### 1. Database Support (SQLite & PostgreSQL)

**Configuration:**
- Set `UsePostgreSQL` to `true` in `appsettings.json` to use PostgreSQL
- Configure connection string in `ConnectionStrings:PostgreSQL`
- Migrations are automatically applied on startup

**Example:**
```json
{
  "UsePostgreSQL": true,
  "ConnectionStrings": {
    "PostgreSQL": "Host=localhost;Database=ramstockalerts;Username=postgres;Password=yourpassword"
  }
}
```

### 2. Structured Logging with Serilog

**Features:**
- Console logging with timestamps
- File logging with daily rotation in `logs/` directory
- Application Insights integration (optional)

**Configuration:**
```json
{
  "ApplicationInsights": {
    "ConnectionString": "InstrumentationKey=your-key;..."
  }
}
```

### 3. Health Checks

**Endpoints:**
- `/health/ready` - Readiness probe (checks database connectivity)
- `/health/live` - Liveness probe (checks if app is running)

**Usage:**
```bash
curl http://localhost:5000/health/ready
curl http://localhost:5000/health/live
```

### 4. Multi-Channel Alerting with Failover

**Channels (in order):**
1. Discord (webhook)
2. SMS (Twilio)
3. Email (SMTP)

**Configuration:**
```json
{
  "Discord": {
    "WebhookUrl": "https://discord.com/api/webhooks/..."
  },
  "Twilio": {
    "AccountSid": "AC...",
    "AuthToken": "...",
    "FromPhoneNumber": "+1234567890",
    "ToPhoneNumber": "+1234567890"
  },
  "Email": {
    "SmtpHost": "smtp.gmail.com",
    "SmtpPort": 587,
    "Username": "your@email.com",
    "Password": "app-password",
    "FromEmail": "alerts@yourcompany.com",
    "ToEmail": "you@email.com"
  }
}
```

### 5. API Quota Tracking

**Features:**
- Token bucket algorithm for rate limiting
- Per-minute and per-day tracking
- Automatic delay when approaching limits

**Configuration:**
```json
{
  "Polygon": {
    "QuotaPerMinute": 5,
    "QuotaPerDay": 100
  }
}
```

**Endpoint:**
```bash
GET /admin/quota
```

**Response:**
```json
{
  "minuteUtilizationPercent": 60.0,
  "dayUtilizationPercent": 45.0,
  "canMakeRequest": true,
  "requiredDelay": "00:00:00"
}
```

### 6. Event Store & Backtest System

**Features:**
- PostgreSQL-backed event persistence
- Event replay for backtesting
- Historical event analysis

**Endpoints:**

Replay events:
```bash
POST /api/backtest/replay
Content-Type: application/json

{
  "startTime": "2026-01-01T00:00:00Z",
  "endTime": "2026-01-07T00:00:00Z",
  "speedMultiplier": 10.0
}
```

Get events:
```bash
GET /api/backtest/events?from=2026-01-01T00:00:00Z&limit=100
```

Get signal events:
```bash
GET /api/signals/events/replay?from=2026-01-01T00:00:00Z&limit=100
```

### 7. WebSocket Resilience

**Features:**
- Ping/pong heartbeat (configurable interval)
- Dead connection detection
- Jittered exponential backoff for reconnection
- Graceful shutdown with unsubscribe

**Configuration:**
```json
{
  "Alpaca": {
    "PingIntervalSeconds": 30,
    "PongTimeoutSeconds": 10
  }
}
```

### 8. Graceful Shutdown

**Features:**
- Flushes pending events
- Closes WebSocket connections cleanly
- Completes in-flight database writes
- Configurable shutdown timeout (default 30 seconds)

## Deployment

### Azure App Service (Recommended for Small/Medium Scale)

1. **Create Azure PostgreSQL Database:**
   ```bash
   az postgres flexible-server create \
     --name ramstockalerts-db \
     --resource-group ramstockalerts-rg \
     --location eastus \
     --admin-user pgadmin \
     --admin-password <password> \
     --sku-name Standard_B1ms
   ```

2. **Configure App Settings:**
   - Add connection string to Key Vault
   - Set `UsePostgreSQL=true`
   - Configure Application Insights

3. **Deploy:**
   ```bash
   dotnet publish -c Release
   az webapp deploy --resource-group ramstockalerts-rg \
     --name ramstockalerts-api \
     --src-path ./bin/Release/net10.0/publish.zip
   ```

### Kubernetes (For Large Scale)

See `k8s/` directory for Kubernetes manifests (if created).

## Monitoring

### Application Insights

All logs, metrics, and traces are sent to Application Insights when configured.

**Key Metrics:**
- Feed latency
- Message throughput
- API quota utilization
- Alert delivery success rate
- Reconnection events

### Health Checks

Configure Kubernetes probes:

```yaml
livenessProbe:
  httpGet:
    path: /health/live
    port: 8080
  initialDelaySeconds: 30
  periodSeconds: 10

readinessProbe:
  httpGet:
    path: /health/ready
    port: 8080
  initialDelaySeconds: 10
  periodSeconds: 5
```

## Troubleshooting

### Database Connection Issues

Check connection string and ensure PostgreSQL is accessible:
```bash
psql -h localhost -U postgres -d ramstockalerts
```

### WebSocket Disconnections

Monitor logs for:
- Feed lag warnings
- Pong timeout errors
- Reconnection attempts

Increase ping interval if needed:
```json
{
  "Alpaca": {
    "PingIntervalSeconds": 60
  }
}
```

### Rate Limit Exceeded

Check quota utilization:
```bash
curl http://localhost:5000/admin/quota
```

Increase quota limits in configuration or contact Polygon support.

## Security

- Store secrets in Azure Key Vault or AWS Secrets Manager
- Never commit API keys or passwords to git
- Use environment variables for sensitive configuration
- Enable HTTPS in production
- Use managed identities where possible

## Performance

### Database Optimization

Indexes are automatically created for:
- TradeSignals: Ticker, Timestamp
- SignalLifecycles: SignalId, OccurredAt
- EventStoreEntries: AggregateId, EventType, Timestamp, CorrelationId

### Scaling

- **Single instance:** Use SQLite or PostgreSQL
- **Multiple instances:** Use PostgreSQL with distributed state (Redis recommended)
- **High volume:** Consider adding Redis for circuit breaker and quota tracking

## License

[Your License]
