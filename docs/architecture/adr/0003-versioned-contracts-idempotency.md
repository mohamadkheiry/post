# ADR-0003: قراردادهای نسخه‌دار و operation ID پایدار

- Status: Accepted
- Date: 2026-08-16

## Context

Device و Edge/Core مستقل deploy می‌شوند و retry یک عملیات نباید اثر دوم بسازد.

## Decision

تمام payloadهای عمومی در `Post.Contracts` با V1 و message type ثابت تعریف می‌شوند. envelope دارای `correlationId` و extension اختیاری `operationId` است. status enumهای Device و Core جدا و mapping exhaustive است.

## Consequences

- rename یا تغییر معنای field persisted در V1 ممنوع است.
- تغییر breaking به V2 و dual-read/migration نیاز دارد.
- client باید operation ID را در retry ثابت نگه دارد.
- fallback به correlation ID فقط best-effort است.

## Verification

golden JSON، enum wire values، unknown enum rejection و mapping table تست می‌شوند.
