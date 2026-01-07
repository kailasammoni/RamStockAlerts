# User Acceptance Testing Guidelines for RamStockAlerts Agent

## Agent Behavior Directives

This document defines the User Acceptance Testing (UAT) guidelines for the RamStockAlerts trading signal system. The testing agent must execute comprehensive validation across all production-critical systems to ensure reliability, performance, security, and correctness before releasing to production.

---

## I. Core Testing Principles

1. **End-to-End Validation** — Test complete signal flow: feed ingestion → signal validation → database persistence → alert delivery
2. **Failure Simulation** — Intentionally break dependencies (API outages, WebSocket disconnections, database unavailability) to verify recovery mechanisms
3. **Performance Baseline** — Measure latency (feed→signal→alert) and ensure it stays within acceptable thresholds
4. **Security Verification** — Confirm no secrets leak in logs, all credentials use Key Vault, rate limiting prevents abuse
5. **Data Integrity** — Validate signal persistence, event store replay, backtest accuracy
6. **Scalability Checks** — Test system behavior under 10x normal volume and concurrent feed streams

---

## II. Test Categories & Acceptance Criteria

### **A. Feed Reliability Tests**

**Test A1: Alpaca WebSocket Connection & Message Processing**
- **Setup:** Start application with Alpaca stream enabled
- **Actions:**
  - Subscribe to 3 test tickers (AAPL, TSLA, NVDA)
  - Verify WebSocket connects within 5 seconds
  - Receive at least 10 trade updates within 60 seconds
  - Verify all trades are logged with timestamp, price, size
- **Acceptance Criteria:**
  - ✅ Connection established with "authenticated" message
  - ✅ At least 10 trades received per minute per ticker
  - ✅ No duplicate trades (check message IDs)
  - ✅ All trades have valid timestamp (within 2 seconds of current UTC time)
  - ✅ Structured logs with CorrelationId tracking
- **Failure Handling:**
  - ❌ If no messages received for 10 seconds: log error, trigger circuit breaker
  - ❌ If feed lag > 5 seconds: circuit breaker should suspend, log warning

**Test A2: Alpaca WebSocket Reconnection After Outage**
- **Setup:** Application running with active Alpaca stream
- **Actions:**
  - Simulate network disconnection (kill WebSocket connection at OS level or firewall rule)
  - Wait 30 seconds
  - Restore network connectivity
  - Verify automatic reconnection
  - Verify subscriptions are re-established
  - Verify no duplicate subscriptions after reconnect
- **Acceptance Criteria:**
  - ✅ Reconnection attempted within 5 seconds of detecting disconnection
  - ✅ Exponential backoff applied (1s, 2s, 4s, 8s, 16s, 30s max)
  - ✅ Jitter applied to backoff (±20%) to prevent thundering herd
  - ✅ Subscriptions re-sent exactly once after reconnect
  - ✅ Custom metrics emitted for reconnection event
  - ✅ No duplicate trades processed during reconnection window
- **Failure Handling:**
  - ❌ If reconnection fails after 10 attempts: activate circuit breaker for 30 minutes
  - ❌ Log failure with timestamps and attempt count

**Test A3: Polygon REST API (Daily Aggregates)**
- **Setup:** Application running with Polygon client enabled
- **Actions:**
  - Trigger manual poll of AAPL previous day aggregate
  - Verify HTTP 200 response
  - Verify OHLCV data contains valid price/volume/VWAP
  - Verify data is parsed and logged
- **Acceptance Criteria:**
  - ✅ HTTP 200 response received within 1 second
  - ✅ Response contains close, high, low, open, volume, vwap fields
  - ✅ VWAP is reasonable (within close ±5%)
  - ✅ Logged with ticker, price, VWAP, spread, score
  - ✅ Retry logic: retries on non-200 (except 404) with exponential backoff
- **Failure Handling:**
  - ❌ If 401 Unauthorized: check API key configuration, fail with clear error
  - ❌ If rate limited (429): respect Retry-After header, delay next request
  - ❌ If timeout (>5 seconds): retry up to 3 times, then skip ticker for this cycle

**Test A4: Feed Latency Measurement**
- **Setup:** Application running with both feeds
- **Measurement:**
  - Measure time from Alpaca trade message timestamp to local reception
  - Measure time from signal validation to alert sent
  - Measure time from database insert to confirmation
- **Acceptance Criteria:**
  - ✅ Alpaca feed latency < 2 seconds (from trade timestamp to app receives message)
  - ✅ Signal generation latency < 500ms (from raw data to signal object created)
  - ✅ Alert delivery latency < 2 seconds (from signal generated to alert endpoint called)
  - ✅ Database write latency < 100ms (from insert to confirmation)
  - ✅ Total end-to-end latency < 3 seconds (feed message → alert delivered)

---

### **B. Signal Generation & Validation Tests**

**Test B1: Valid Signal Detection (Happy Path)**
- **Setup:** Application running with test data
- **Actions:**
  - Manually inject AAPL trade: price=262.00, quantity=50k, VWAP=263.00, spread=0.002
  - Wait 5 seconds for processing
  - Check database for generated signal
- **Acceptance Criteria:**
  - ✅ Signal generated with correct ticker (AAPL)
  - ✅ Signal has valid entry price (262.00)
  - ✅ Signal has valid stop loss (calculated correctly)
  - ✅ Signal has valid take profit (calculated correctly)
  - ✅ Signal score is calculated (>= threshold 1.5)
  - ✅ Signal status is "Active"
  - ✅ Signal timestamp is within 1 second of current time
- **Failure Handling:**
  - ❌ If signal score < threshold: signal should be rejected (no DB write)
  - ❌ If spread > MaxSpread: signal should be rejected

**Test B2: Signal Deduplication**
- **Setup:** Valid signal already exists for AAPL within throttle window
- **Actions:**
  - Inject identical trade conditions again
  - Wait for signal generation attempt
  - Check database for duplicate signals
- **Acceptance Criteria:**
  - ✅ Only 1 signal in database (original)
  - ✅ Throttling service blocks duplicate within 60-second window
  - ✅ Log shows "Signal rejected: duplicate within throttle window"
  - ✅ Alert is not sent for duplicate signal
- **Failure Handling:**
  - ❌ If duplicate signal is created: data integrity failure, investigate throttling logic

**Test B3: Price Filter Validation**
- **Setup:** Universe configured with MinPrice=10, MaxPrice=80
- **Actions:**
  - Test signal for ticker at $9.99 (below min) → should be filtered
  - Test signal for ticker at $10.01 (above min) → should be allowed
  - Test signal for ticker at $80.01 (above max) → should be filtered
  - Test signal for ticker at $79.99 (below max) → should be allowed
- **Acceptance Criteria:**
  - ✅ Out-of-range tickers are not in active universe
  - ✅ In-range tickers generate signals (if other conditions met)
  - ✅ Filtering is applied at universe build time
- **Failure Handling:**
  - ❌ If signal generated outside price range: filtering logic broken

**Test B4: Volume & Float Filtering**
- **Setup:** Universe configured with MinRelativeVolume=2, MaxFloat=150M
- **Actions:**
  - Test ticker with low relative volume (< 2x average) → should be filtered
  - Test ticker with high float (> 150M) → should be filtered
  - Test ticker with normal volume and float → should be included
- **Acceptance Criteria:**
  - ✅ Low-volume tickers excluded from universe
  - ✅ High-float tickers excluded from universe
  - ✅ Universe rebuilt every 24 hours (at 9:25 AM ET)
- **Failure Handling:**
  - ❌ If filtered tickers still generate signals: filtering not applied correctly

---

### **C. Database Persistence Tests**

**Test C1: Signal Persistence to PostgreSQL**
- **Setup:** PostgreSQL database running and configured
- **Actions:**
  - Generate a signal
  - Query database directly: `SELECT * FROM TradeSignals WHERE Ticker='AAPL' ORDER BY CreatedAt DESC LIMIT 1`
  - Verify all fields match in-memory signal
- **Acceptance Criteria:**
  - ✅ Signal record exists in TradeSignals table
  - ✅ Ticker, EntryPrice, StopLoss, TakeProfit, Score all match
  - ✅ CreatedAt timestamp is correct (within 1 second)
  - ✅ Status is "Active"
  - ✅ CorrelationId matches logs
- **Failure Handling:**
  - ❌ If write fails: exception logged, signal is held in queue for retry
  - ❌ If timeout (> 5 seconds): transaction rolled back, retry after 5 seconds

**Test C2: Signal Lifecycle Transitions**
- **Setup:** Signal in database with status "Active"
- **Actions:**
  - Update signal to "ManualClosed" with reason
  - Query database to verify status change
  - Update signal to "Completed" with exit price
  - Query to verify final state
- **Acceptance Criteria:**
  - ✅ Each status transition recorded as separate SignalLifecycle entry
  - ✅ Timestamps correct for each transition
  - ✅ Reasons/exit prices stored in database
  - ✅ Can query full lifecycle history
- **Failure Handling:**
  - ❌ If lifecycle record missing: audit trail incomplete

**Test C3: Event Store Persistence**
- **Setup:** PostgreSQL event store enabled
- **Actions:**
  - Generate 5 signals
  - Query EventStoreEntries table
  - Verify 5 events recorded with AggregateId, EventType, Payload, Timestamp
  - Stop application and restart
  - Verify events still in database (not lost)
- **Acceptance Criteria:**
  - ✅ Event created for each signal with AggregateId=SignalId
  - ✅ Payload contains complete signal JSON
  - ✅ CorrelationId included in event
  - ✅ Events persist across application restart
  - ✅ No events lost on shutdown
- **Failure Handling:**
  - ❌ If events lost on restart: persistence layer broken

**Test C4: Backtest Event Replay**
- **Setup:** 10 events in event store from past 24 hours
- **Actions:**
  - Call `/api/backtest/replay?startTime=2026-01-06T10:00Z&endTime=2026-01-07T10:00Z&speedMultiplier=10`
  - Monitor signal generation from replayed events
  - Compare replayed signals against original signals
- **Acceptance Criteria:**
  - ✅ Backtest generates signals at 10x speed
  - ✅ Signal counts match (all original signals generated)
  - ✅ Signal parameters identical (entry price, score, etc.)
  - ✅ Replay completes in < 10 seconds for 24 hours of data at 10x speed
  - ✅ Backtest report shows precision, recall, win rate metrics
- **Failure Handling:**
  - ❌ If replayed signals don't match originals: replay logic broken

---

### **D. Alert Delivery Tests**

**Test D1: Discord Alert Delivery**
- **Setup:** Discord webhook configured in Key Vault
- **Actions:**
  - Generate a signal
  - Verify Discord message received in test channel within 5 seconds
  - Check message format includes: Ticker, EntryPrice, StopLoss, TakeProfit, Score, Timestamp
  - Verify message is pinned/highlighted appropriately
- **Acceptance Criteria:**
  - ✅ Discord message delivered within 2 seconds of signal creation
  - ✅ Message format is readable (embeds or formatted text)
  - ✅ All signal details present in message
  - ✅ Timestamp and signal ID included for traceability
  - ✅ Retry logic: retries on 429 (rate limit) respecting Retry-After header
- **Failure Handling:**
  - ❌ If webhook returns 404 Not Found: alert fails, error logged, no retry
  - ❌ If webhook returns 429: respect backoff, retry after specified delay
  - ❌ If webhook timeout (> 5 seconds): fail after 3 retries, trigger failover to SMS

**Test D2: SMS Alert Delivery (Twilio)**
- **Setup:** Twilio credentials configured in Key Vault, Discord webhook failing
- **Actions:**
  - Disable Discord webhook (simulate outage)
  - Generate a signal
  - Verify SMS received on test phone number within 10 seconds
  - Check SMS includes ticker, entry price, entry time
- **Acceptance Criteria:**
  - ✅ SMS delivered within 5 seconds of failover triggered
  - ✅ SMS is concise (< 160 characters preferred)
  - ✅ Includes ticker and entry price (minimum viable info)
  - ✅ Includes phone number to verify sender identity
- **Failure Handling:**
  - ❌ If SMS fails and email is configured: failover to email
  - ❌ If all channels fail: alert is queued for retry, error logged with CorrelationId

**Test D3: Multi-Channel Failover Chain**
- **Setup:** All three channels configured (Discord → SMS → Email)
- **Actions:**
  - Disable Discord webhook
  - Generate signal, verify SMS delivered
  - Re-enable Discord, disable SMS
  - Generate signal, verify Discord delivered
  - Disable both Discord and SMS
  - Generate signal, verify email received
- **Acceptance Criteria:**
  - ✅ Alert always delivered via first available channel
  - ✅ Failover logic works in sequence
  - ✅ No duplicate alerts sent across channels
  - ✅ Metrics tracked for each channel (success rate, delivery latency)
- **Failure Handling:**
  - ❌ If all channels fail: alert queued with exponential retry backoff

**Test D4: Alert Throttling**
- **Setup:** Throttle configured for 1 alert per minute per ticker
- **Actions:**
  - Generate 5 signals for same ticker within 1 minute
  - Check alerts sent (should be 1)
  - Wait 60 seconds
  - Generate 1 more signal for same ticker
  - Verify alert is sent
- **Acceptance Criteria:**
  - ✅ Only 1 alert sent per ticker per 60-second window
  - ✅ Subsequent signals rejected with debug log "throttled"
  - ✅ After throttle window expires, new alert can be sent
- **Failure Handling:**
  - ❌ If duplicate alerts sent: throttling not working

---

### **E. Configuration & Secrets Tests**

**Test E1: Azure Key Vault Integration**
- **Setup:** Application running with Key Vault configured
- **Actions:**
  - Check appsettings.Development.json (should be empty/placeholder for secrets)
  - Verify all secrets loaded from Key Vault (Polygon__ApiKey, Discord__WebhookUrl, Alpaca__Key, Alpaca__Secret)
  - Search logs for any hardcoded secret values
  - Search logs for Key Vault access errors
- **Acceptance Criteria:**
  - ✅ appsettings.Development.json contains no actual API keys
  - ✅ All secrets successfully loaded from Key Vault
  - ✅ Logs do NOT contain any secret values (keys redacted)
  - ✅ Key Vault authentication succeeds without errors
  - ✅ Configuration is cached to avoid excessive Key Vault calls
- **Failure Handling:**
  - ❌ If secrets in logs: security breach, investigation required
  - ❌ If Key Vault auth fails: application fails to start with clear error message

**Test E2: Configuration Validation on Startup**
- **Setup:** Intentionally remove required configuration
- **Actions:**
  - Scenario 1: Remove Polygon__ApiKey from Key Vault
    - Start application
    - Verify application logs error: "Polygon API key not configured" and continues (Polygon disabled)
  - Scenario 2: Remove Discord__WebhookUrl
    - Start application
    - Verify application logs error and gracefully degrades
  - Scenario 3: Invalid database connection string
    - Start application
    - Verify application fails fast with clear error: "Cannot connect to database"
- **Acceptance Criteria:**
  - ✅ Missing optional configs (like Polygon key) log warning but don't crash
  - ✅ Missing critical configs (like database) fail fast on startup
  - ✅ Error messages are actionable (tell user what to fix)
  - ✅ Application doesn't start with degraded functionality without operator knowledge
- **Failure Handling:**
  - ❌ If app starts with missing critical config: monitoring will fail, signals won't be saved

**Test E3: User Secrets for Local Development**
- **Setup:** Local development environment
- **Actions:**
  - Run `dotnet user-secrets list` (should show local secrets)
  - Verify secrets are NOT in appsettings.Development.json
  - Verify application loads local secrets correctly
- **Acceptance Criteria:**
  - ✅ User secrets stored locally (not in Git)
  - ✅ Application successfully loads and uses local secrets
  - ✅ No contamination of committed files with secrets
- **Failure Handling:**
  - ❌ If secrets committed to Git: security failure, rotate all secrets immediately

---

### **F. Rate Limiting & Quota Tests**

**Test F1: Massive (Polygon) API Quota Tracking**
- **Setup:** Application running, quota tracker enabled
- **Actions:**
  - Monitor requests to Massive API over 1 hour
  - Verify quota counter increments correctly
  - Check `/admin/quota` endpoint for quota usage
- **Acceptance Criteria:**
  - ✅ Quota counter accurate (matches actual API calls made)
  - ✅ `/admin/quota` shows current usage, remaining, reset time
  - ✅ Quota persisted in PostgreSQL (survives app restart)
  - ✅ Metrics emitted: `massive_api_quota_utilization_percent`
- **Failure Handling:**
  - ❌ If quota counter inaccurate: quota management broken

**Test F2: Rate Limiting Before Hitting Quota**
- **Setup:** Quota tracker enabled, approaching tier limit (80% utilization)
- **Actions:**
  - Simulate approaching quota limit (e.g., 800 requests out of 1000 per day)
  - Monitor alert: custom metric should show `massive_api_quota_utilization_percent = 80`
  - Application Insights should show warning alert
  - Try to make more requests: should be throttled/delayed
- **Acceptance Criteria:**
  - ✅ Alert fired when quota > 80%
  - ✅ Requests delayed (token bucket algorithm) before hitting hard limit
  - ✅ No 429 rate limit errors from Massive (indicates proactive rate limiting works)
- **Failure Handling:**
  - ❌ If API returns 429: rate limiting isn't proactive enough

**Test F3: Token Bucket Rate Limiter Accuracy**
- **Setup:** Token bucket configured for 30 requests/minute
- **Actions:**
  - Make 30 requests as fast as possible (within 1 second)
  - Verify all 30 succeed (tokens available)
  - Try to make 31st request
  - Verify 31st request is delayed until next token becomes available
  - Wait 2 seconds (should generate 1-2 new tokens)
  - Try 31st and 32nd requests, verify they proceed
- **Acceptance Criteria:**
  - ✅ Token bucket allows burst up to limit
  - ✅ Request 31 is delayed (rate limited)
  - ✅ Tokens replenish at configured rate (1 per 2 seconds = 30/min)
  - ✅ No requests exceed limit
- **Failure Handling:**
  - ❌ If 31st request succeeds immediately: rate limiter broken

---

### **G. Health Checks & Monitoring Tests**

**Test G1: Health Check Endpoints**
- **Setup:** Application running
- **Actions:**
  - Call `/health/live` (liveness check)
    - Should return 200 OK immediately
  - Call `/health/ready` (readiness check)
    - Should return 200 OK when all dependencies healthy
  - Call `/health` (basic health check)
    - Should return 200 OK
- **Acceptance Criteria:**
  - ✅ `/health/live` responds within 100ms (should be instant)
  - ✅ `/health/ready` checks database connectivity (returns 503 if DB unavailable)
  - ✅ `/health/ready` checks WebSocket state (returns 503 if disconnected > 30 sec)
  - ✅ Response includes status: "Healthy" / "Degraded" / "Unhealthy"
- **Failure Handling:**
  - ❌ If health checks fail: container orchestration can restart/replace instance

**Test G2: Health Check Details**
- **Setup:** Application running
- **Actions:**
  - Call `/health/ready?verbose=true` (or `/healthchecks-ui`)
  - Check output includes:
    - Database connectivity
    - Alpaca WebSocket state
    - Polygon API reachability
    - Discord webhook reachability
    - Circuit breaker status
- **Acceptance Criteria:**
  - ✅ Each dependency status shown (healthy/degraded/unhealthy)
  - ✅ Include response times for external calls
  - ✅ Include last successful connection timestamp
  - ✅ Clear description of any failures
- **Failure Handling:**
  - ❌ If health check doesn't detect failures: blind spot in monitoring

**Test G3: Application Insights Metrics**
- **Setup:** Application Insights configured and running
- **Actions:**
  - Generate 10 signals over 5 minutes
  - Check Application Insights dashboard for:
    - Request count and response times
    - Dependency call count (Alpaca, Polygon, Discord) and latencies
    - Exception count
    - Custom metric: signal generation rate
    - Custom metric: alert delivery latency
    - Custom metric: API quota utilization
- **Acceptance Criteria:**
  - ✅ Request latencies tracked (P50, P95, P99)
  - ✅ Dependency latencies visible for each external call
  - ✅ Exception stack traces captured
  - ✅ Custom metrics visible and accurate
  - ✅ CorrelationId ties together related events
- **Failure Handling:**
  - ❌ If metrics missing: blind monitoring, can't detect performance degradation

**Test G4: Circuit Breaker Status Monitoring**
- **Setup:** Application running with circuit breaker enabled
- **Actions:**
  - Simulate circuit breaker activation (e.g., spread spike, feed lag)
  - Verify `/health/ready` shows "Degraded" (not "Unhealthy")
  - Check Application Insights for circuit breaker activation event
  - Check logs for reason of activation
  - Wait for auto-recovery timeout (default 30 min or configured time)
  - Verify `/health/ready` returns to "Healthy"
- **Acceptance Criteria:**
  - ✅ Circuit breaker activation causes health check to degrade (not fail)
  - ✅ Activation reason clearly logged with specific threshold exceeded
  - ✅ Auto-recovery after timeout without manual intervention
  - ✅ Recovery logged with timestamp
- **Failure Handling:**
  - ❌ If circuit breaker doesn't auto-recover: operator intervention needed

---

### **H. Graceful Shutdown Tests**

**Test H1: Signal Completion Before Shutdown**
- **Setup:** Application processing signals
- **Actions:**
  - Generate 3 signals while app is running
  - Verify all 3 saved to database
  - Send shutdown signal (SIGTERM or Ctrl+C)
  - Monitor for logs indicating:
    - "Graceful shutdown initiated"
    - "Waiting for in-flight operations to complete"
    - "X pending signals flushed"
  - Verify no signals lost
  - Start application again, query database
- **Acceptance Criteria:**
  - ✅ All 3 signals saved before shutdown completes
  - ✅ In-flight database transactions committed
  - ✅ No signals lost or orphaned
  - ✅ Logs show graceful shutdown sequence
- **Failure Handling:**
  - ❌ If any signals lost: data loss bug, investigate transaction handling

**Test H2: WebSocket Connection Cleanup**
- **Setup:** Application with Alpaca WebSocket connected
- **Actions:**
  - Send shutdown signal
  - Monitor for logs:
    - "Sending unsubscribe message"
    - "Closing WebSocket connection"
    - "WebSocket closed"
  - Verify no "connection refused" or "broken pipe" errors
  - Verify server-side unsubscribe received
- **Acceptance Criteria:**
  - ✅ Unsubscribe message sent before close
  - ✅ WebSocket closed cleanly (no error logs)
  - ✅ Graceful shutdown completes within 10 seconds (configurable timeout)
- **Failure Handling:**
  - ❌ If timeout exceeded: force close after 30 seconds, log warning

**Test H3: Event Store Flush**
- **Setup:** Application with pending events not yet persisted
- **Actions:**
  - Generate signal
  - Immediately send shutdown signal (don't wait for event save)
  - Check logs for "Flushing event store"
  - Verify event persisted before shutdown completes
- **Acceptance Criteria:**
  - ✅ Event store flushed during shutdown
  - ✅ Pending events persisted to database
  - ✅ No events lost
- **Failure Handling:**
  - ❌ If events not flushed: data loss

**Test H4: Shutdown Under Load**
- **Setup:** Application processing high volume (10 signals/sec simulated)
- **Actions:**
  - Trigger shutdown while actively processing
  - Monitor shutdown sequence
  - Verify graceful completion (all in-flight operations honored)
- **Acceptance Criteria:**
  - ✅ All in-flight operations complete (not abandoned)
  - ✅ Graceful shutdown takes < 30 seconds
  - ✅ No data loss under load
- **Failure Handling:**
  - ❌ If data lost during shutdown under load: bug in shutdown logic

---

### **I. Performance & Scalability Tests**

**Test I1: Latency Under Normal Load**
- **Setup:** Normal trading hours, 3 tickers, normal feed volume
- **Actions:**
  - Measure end-to-end latency (feed message → alert sent) for 100 signals
  - Record P50, P95, P99 latencies
  - Compare to baseline (should be consistent)
- **Acceptance Criteria:**
  - ✅ P50 latency < 1.5 seconds
  - ✅ P95 latency < 2.5 seconds
  - ✅ P99 latency < 3.5 seconds
  - ✅ No latency trend (not degrading over time)
- **Failure Handling:**
  - ❌ If latency degrades over time: memory leak or resource exhaustion, investigate

**Test I2: Throughput Under Peak Load**
- **Setup:** Simulate 10x normal volume (e.g., market open/close)
- **Actions:**
  - Inject 30 signals/sec for 5 minutes
  - Monitor: CPU usage, memory usage, database write rate
  - Verify no signals dropped
  - Check alert delivery rate (should match signal rate, ±2%)
- **Acceptance Criteria:**
  - ✅ CPU usage < 80% of available
  - ✅ Memory usage < 500MB (target for containerized app)
  - ✅ No signals dropped (ingress = egress)
  - ✅ Alert delivery rate within 5% of signal generation rate
- **Failure Handling:**
  - ❌ If signals dropped: queue overflow, increase buffer or scale horizontally
  - ❌ If memory grows unbounded: memory leak, investigate

**Test I3: Database Performance Under Load**
- **Setup:** PostgreSQL running, steady stream of signal writes
- **Actions:**
  - Write 1000 signals to database in 1 minute
  - Monitor query times for:
    - SELECT COUNT(*) FROM TradeSignals
    - SELECT * FROM TradeSignals ORDER BY CreatedAt DESC LIMIT 100
  - Check index usage
- **Acceptance Criteria:**
  - ✅ Average write latency < 50ms
  - ✅ Queries execute in < 100ms even during high write load
  - ✅ Database connection pool not exhausted
- **Failure Handling:**
  - ❌ If queries slow down: add indexes or increase connection pool

**Test I4: Multi-Instance Scalability**
- **Setup:** 2+ instances of RamStockAlerts running against same PostgreSQL and Redis
- **Actions:**
  - Both instances subscribe to Alpaca stream
  - Verify no duplicate signals generated
  - Verify circuit breaker state shared across instances (via Redis)
  - Verify API quota tracking consistent across instances
  - Verify health checks work across instances
- **Acceptance Criteria:**
  - ✅ No duplicate signals across instances (deduplication via database unique constraints)
  - ✅ Circuit breaker state synchronized (if one instance activates, other respects it)
  - ✅ Quota tracking global (not per-instance)
  - ✅ Load distributed fairly
- **Failure Handling:**
  - ❌ If duplicates generated: distributed deduplication needed

---

### **J. Security Tests**

**Test J1: Secrets Not Leaked in Logs**
- **Setup:** Application running
- **Actions:**
  - Generate signals, process alerts, interact with APIs
  - Export all logs (stdout, Application Insights)
  - Search for: API keys, webhook URLs, database passwords, secrets
  - Verify each secret is redacted or absent
- **Acceptance Criteria:**
  - ✅ No API keys visible in logs
  - ✅ No webhook URLs visible in logs
  - ✅ Database connection strings redacted
  - ✅ Exceptions sanitized (don't leak secrets)
- **Failure Handling:**
  - ❌ If secrets found: immediate remediation required

**Test J2: Authentication for Admin Endpoints**
- **Setup:** `/admin/quota`, `/api/backtest/replay` endpoints configured
- **Actions:**
  - Call `/admin/quota` without authentication
    - Should return 401 Unauthorized
  - Call with valid API key in header
    - Should return 200 OK
- **Acceptance Criteria:**
  - ✅ Admin endpoints require authentication
  - ✅ API key stored securely (hash, not plaintext)
  - ✅ API key rotation supported
- **Failure Handling:**
  - ❌ If unauthenticated request succeeds: security breach

**Test J3: Input Validation**
- **Setup:** Backtest endpoint accessible
- **Actions:**
  - Call `/api/backtest/replay?startTime=invalid`
    - Should return 400 Bad Request
  - Call `/api/backtest/replay?startTime=2026-01-01T10:00Z&endTime=2026-01-01T09:00Z` (end before start)
    - Should return 400 Bad Request
  - Call `/api/backtest/replay?speedMultiplier=1000` (too high)
    - Should return 400 Bad Request with reason
- **Acceptance Criteria:**
  - ✅ All inputs validated on entry
  - ✅ Invalid inputs rejected with clear error messages
  - ✅ No crashes from unexpected input
- **Failure Handling:**
  - ❌ If invalid input crashes app: input validation missing

**Test J4: Rate Limiting on Public Endpoints**
- **Setup:** `/health` endpoint accessible
- **Actions:**
  - Make 100 requests to `/health` in 1 second
  - Verify some requests return 429 Too Many Requests after limit exceeded
- **Acceptance Criteria:**
  - ✅ Rate limit enforced (e.g., 100 requests/second)
  - ✅ Rate limit resets appropriately
  - ✅ Responses include `Retry-After` header
- **Failure Handling:**
  - ❌ If no rate limiting: DOS vulnerability

---

## III. Test Execution & Reporting

### **Automated Test Execution**
- Tests should be executed in CI/CD pipeline on every commit to main branch
- Execution order: A (Feeds) → B (Signals) → C (Database) → D (Alerts) → E-J (Supporting)
- Parallel execution where possible (tests E-J can run in parallel after B completes)
- Timeout: Each test category should complete in < 5 minutes (unless load testing)

### **Test Results & Reporting**
- **Pass Criteria:** All acceptance criteria met, no failures
- **Fail Criteria:** Any acceptance criterion failed, test aborted
- **Report Generated:** 
  - Test execution summary (pass/fail count)
  - Latency metrics (P50/P95/P99)
  - Resource usage (CPU, memory, disk)
  - Deployment readiness (READY / NOT READY)

### **Deployment Gate**
- Application can only be deployed to production if:
  - ✅ All tests pass
  - ✅ E2E latency within acceptable bounds
  - ✅ No memory leaks detected
  - ✅ No secrets leaked
  - ✅ Health checks passing

---

## IV. Pre-Deployment Checklist

- [ ] All UAT tests passing
- [ ] Performance baseline established (latency/throughput)
- [ ] Security audit completed (no secrets in logs)
- [ ] PostgreSQL database migrated and validated
- [ ] Azure Key Vault secrets configured
- [ ] Application Insights dashboard created
- [ ] Alerting rules configured (for quota > 80%, feed lag, circuit breaker activation)
- [ ] Runbooks created (how to respond to common alerts)
- [ ] Disaster recovery plan documented
- [ ] Monitoring verified (can observe all system metrics)
- [ ] Team trained on operational procedures

---

## V. Post-Deployment Validation

- [ ] Production health checks passing
- [ ] Signals generating at expected rate
- [ ] Alerts delivering successfully
- [ ] No unexpected errors in Application Insights
- [ ] Latency metrics stable
- [ ] Team on-call and responsive to alerts
