Proposed changes (grounded in current project behavior)
A) Universe semantics: stop pretending “universe == tradable set”

Problem today: “Universe” gets treated like a single list that drives everything, but you actually have tiers already:

scanner candidates

classified eligible common stocks

probe subscriptions (tape/L1)

depth+tick evaluation set (3)

active strategy universe (depth-enabled)

Change: Make the tiers explicit in code + logs (even if you keep same classes).

UniverseService outputs EligibleCandidates (e.g., 30 common out of 50)

MarketDataSubscriptionManager maintains:

ProbeSet (tape/L1 wide)

EvalSet (depth slots = 3)

ActiveUniverse = EvalSet filtered by gates ready

✅ This matches your logs: candidates=25 tape=25 depth=3 tickByTick=3 active=3.

B) Make “depth upgrade” a first-class operation with invariants

You already fixed the biggest bug: upgrade path now works (depthId no longer null). Nice.

Problem now: upgrade is still treated like “subscribe again,” which causes subtle state hazards:

losing tick-by-tick id on upgrade (you attempted to preserve it, but current logic can still drop it in some flows)

losing depth exchange metadata

failing to update subscription manager’s internal tracking consistently

Change: Add a single canonical operation:

UpgradeProbeToDepth(symbol)
that never touches existing L1 requestId and only modifies depth/tbt fields.

Invariants to enforce (log error if violated):

If isUpgrade == true, mktDataId MUST remain unchanged

An upgrade MUST NOT null out TickByTickRequestId if it existed

Cancel paths must clean both id map + manager state

C) Fix the tape staleness confusion by standardizing “time source”

Your logs show the real monster:

earlier: timeSource=UnixEpoch, using event timestamp → huge staleness

later: timeSource=ReceiptTime but you still show:
skewMs=734721 (12 minutes!) between event time and receipt time

This means: IB tick timestamps are not reliable for freshness gating in your system.

Change (policy + implementation):

Gate freshness on receipt time (the moment your wrapper receives the tick), not exchange/event time.

Keep event time for analytics only.

Store both:

lastTapeRecvMs

lastTapeEventMs

skewMs = recv - event

Then the gate uses:

nowMs - lastTapeRecvMs <= staleWindowMs

✅ Your latest log already prints lastTapeRecvMs and timeSource=ReceiptTime — good. Make it authoritative everywhere.

D) Stop “signal spam” at the source (not only with cooldown)

You got accepted once, then you got a barrage of repeated BUY signals rejected by CooldownActive.

That means:

the signal condition is “sticky” (keeps firing every snapshot)

cooldown is acting as a band-aid

Change: add a “rising edge” requirement
Only emit a signal when the score crosses threshold from below → above, or when a key condition flips state.

Example:

QI crosses above threshold

absorption/wall detected newly

tape accel bursts newly

This prevents:

30 identical signals in 1 second

overcounting strategy opportunities

log floods

Cooldown remains, but it shouldn’t be doing all the work.

E) Rotate depth evaluations intentionally (not incidentally)

You said it clearly:

If depth+tape are must and we have 3 slots, evaluate those three and move on.

Right now, “move on” is mostly driven by timeouts/staleness and whatever the pairing logic picks.

Change: introduce an evaluation window policy

Each depth slot has an explicit evaluation window (e.g., 60–180s)

Exit conditions:

signal emitted (accepted/rejected)

data invalid for >X seconds

window expired

On exit:

cancel depth + tick-by-tick

put symbol into cooldown (depth cooldown, not tape cooldown)

immediately select next candidate

This converts the system from:

“3 symbols glued forever until something happens”
to:

“3 moving microscopes scanning the battlefield”

F) Improve candidate ranking using cheap “microstructure-ish” proxies (before depth)

You asked for creative ideas here. Here are IB-feasible pre-depth filters:

You can’t get real L2 microstructure without subscribing, but you can get proxies cheaply:

Pre-depth scoring candidates (cheap):

L1 spread proxy (bid/ask if available; if not, last vs midpoint)

trade rate (prints/sec from tick-by-tick if you allocate 3 tbt slots only)

volatility burst proxy (rolling 5–15s return variance)

“activity” (did we receive any trades in last 10s)

Plan:

Probe 30–80 symbols with L1 (within line limits)

Maintain an “activity scoreboard”

Only upgrade top 3 by:

high activity

tight-ish spread

price range / volume / float filter already applied

This makes your depth slots more valuable.

G) Make the universe list explicitly “refreshed” not “grown”

You asked: is universe grown or refreshed?
Given your logs (Universe loaded from IbkrScanner with 30 symbols. and periodic scanner runs), you’re effectively refreshing the set.

Change: Make this explicit:

UniverseService returns a snapshot each refresh cycle

MarketDataSubscriptionManager diffs:

unsubscribe probes that fell out (unless in eval)

never drop eval symbols mid-window unless hard invalid

Also: keep a short “recently dropped” cache to avoid churn.