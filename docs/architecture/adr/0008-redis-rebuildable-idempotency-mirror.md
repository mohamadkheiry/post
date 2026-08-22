# ADR-0008: Redis rebuildable idempotency mirror

- Status: Accepted
- Date: 2026-08-16
- Supersedes: ADR-0007 only where it deferred Redis from Phase 1

## Context

ADR-0007 correctly made the service-owned SQL record and unique constraint the
only authority for replay, conflict and side effects. It incorrectly deferred
Redis, while the approved Phase-1 architecture and backlog require an active,
rebuildable Redis read-model plus TTL cleanup. Making Redis authoritative would
still be unsafe because a miss, stale value, eviction or outage must never
permit a duplicate effect.

## Decision

Phase 1 deploys Redis as a non-authoritative idempotency mirror. The application
always resolves the authoritative record from SQL. A cache hit is compared with
that record; a missing or stale entry is repaired after the SQL read. A cache
entry is written only after the SQL commit succeeds. Redis errors degrade cache
health and emit bounded telemetry, but do not change the SQL result or fail a
completed business transaction.

Cache keys are namespaced per deployment and scope. Values are versioned,
bounded snapshots without raw credentials or request payloads. TTL and the
cleanup worker delete only cache entries and their expiry index; SQL records,
Inbox rows and unique constraints follow their own approved retention policy.
Production Redis transport requires TLS and credentials from a secret provider.

## Consequences

- SQL remains the sole correctness boundary across replicas.
- Redis is an active Phase-1 deployment and observable dependency, but an outage
  degrades performance/health rather than business correctness.
- Cache content can be discarded and reconstructed from SQL.
- A cache hit currently does not bypass SQL; any future fast-path requires a new
  decision and proof that it cannot weaken replay/conflict semantics.
- Capacity and the planning target for the Redis read-model are measured
  separately from end-to-end latency; no two-millisecond system SLA is implied.

## Verification

Automated tests cover cache hit, miss, stale/conflicting value, corrupt value,
Redis outage, post-commit mirroring, failed SQL commit, TTL cleanup, cleanup
failure backoff and concurrent replicas sharing SQL/Redis. The connected local
stack additionally exercises Redis outage/recovery and verifies that replay
results and business row counts remain unchanged.
