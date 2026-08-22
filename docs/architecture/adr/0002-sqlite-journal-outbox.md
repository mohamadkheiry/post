# ADR-0002: SQLite WAL، operation journal و transactional outbox

- Status: Accepted
- Date: 2026-08-16

## Context

Edge تک‌node به persistence کم‌هزینه، محلی و قابل بازیابی نیاز دارد و delivery شبکه‌ای at-least-once است.

## Decision

SQLite با WAL/foreign keys/busy timeout استفاده می‌شود. پاسخ عملیات با operation ID در journal ذخیره و side effectها در outbox همان transaction ثبت می‌شوند. worker با claim timeout، backoff و dead-letter منتشر می‌کند.

## Consequences

- اجرای دو instance روی یک file پشتیبانی نمی‌شود.
- disk monitoring و backup هماهنگ الزامی است.
- exactly-once transport ادعا نمی‌شود؛ مقصد باید idempotent باشد.
- dead-letter نیازمند runbook و audit است.

## Verification

restart، lock expiry، retry، max attempts، replay و SQLite migration با test پوشش داده می‌شوند.
