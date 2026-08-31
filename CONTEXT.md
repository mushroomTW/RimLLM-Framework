# RimLLM Framework Context

RimLLM Framework provides a robust, failover-tolerant LLM bridge between RimWorld game mods and external/local AI providers.

## Language

**Provider Adapter**:
An adapter at the framework seam that satisfies the provider interface by creating a configured `IChatClient` and handling connectivity diagnostics and model discovery for a specific LLM service.
_Avoid_: Provider service, LLM handler, LLM worker

**Fallback Chain**:
An ordered sequence of provider-model pairs evaluated successively when an active provider encounters errors or circuit breaker trips.
_Avoid_: Retry list, backup pipeline, failover list

**Health Ledger**:
An in-memory tracking ledger that records provider call outcomes, determining provider availability against rolling cooldowns and exponential circuit backoff rules.
_Avoid_: Error tracker, blacklist, breaker state

**Request**:
A generation or chat prompt submitted by a game subsystem or downstream mod with priority, timeout, and cancellation tokens.
_Avoid_: Job, task, payload

**Usage Ledger**:
An accounting store tracking in-memory session tokens, estimated costs, and persistent daily/monthly usage against configured budget caps.
_Avoid_: Telemetry manager, cost checker, quota manager
