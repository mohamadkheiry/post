# ADR-0007: SQL-authoritative idempotency; Redis deferred

- Status: Superseded by ADR-0008
- Date: 2026-08-16

## Context

The Phase-1 implementation had Redis and in-memory idempotency adapters registered in dependency injection, but no application path consumed them. Every replay/conflict decision was already made from the service-owned SQL idempotency/inbox record. Describing that unused registration as shared multi-replica idempotency was misleading, and making a cache authoritative would allow stale or lost cache data to change business correctness.

## Decision

Phase 1 uses the service-owned SQL unique constraints, idempotency records, and inbox rows as the only replay/conflict authority. The unused Redis/in-memory idempotency adapters and production dependency are removed. Redis remains a possible future rebuildable read-model or post-SQL response optimization, but it is not an active Phase-1 component.

Any reintroduction requires a separate ADR and tests proving that:

- SQL is consulted before a replay is accepted;
- a cache outage cannot create a duplicate business effect;
- a stale or conflicting entry is ignored/invalidated in favor of SQL;
- multi-replica behavior and cache recovery are covered.

## Consequences

- Core idempotency remains correct across replicas that share the same SQL database.
- Redis is not a Phase-1 deployment, readiness, or incident-response dependency.
- There is no implemented Redis latency SLO to report; planning notes about a sub-millisecond read-model remain future performance input, not a current system claim.

## Verification

Application and host tests exercise SQL-backed replay, changed-payload conflict, inbox deduplication, and concurrent unique constraints. Dependency-injection tests assert that no inactive Redis service is registered.
