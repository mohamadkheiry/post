# ADR-0006: upload مستقیم object با URL امضاشده

- Status: Accepted
- Date: 2026-08-16

## Context

عبور image bytes از Core هزینه‌ی حافظه، bandwidth و scale را بالا می‌برد.

## Decision

Core پس از authorization یک object key opaque و URL کوتاه‌عمر S3-compatible صادر می‌کند؛ uploader داده را مستقیم می‌فرستد و فقط metadata/digest به lifecycle متصل می‌شود.

## Consequences

- URL امضاشده secret است و log نمی‌شود.
- content type و length در امضای PUT bind می‌شوند؛ افزودن checksum/digest به قرارداد یک hardening بعدی است.
- orphan cleanup و retention policy لازم است.
- clock skew و CORS/storage policy بخشی از deployment هستند.

## Verification

signature vector، expiry، invalid endpoint، required header و metadata linkage تست می‌شوند.
