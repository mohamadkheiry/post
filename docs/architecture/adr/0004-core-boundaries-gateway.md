# ADR-0004: Core bounded services پشت API Gateway

- Status: Accepted
- Date: 2026-08-16

## Context

هویت، ingestion، lifecycle، config، storage و health نرخ تغییر و مالکیت متفاوت دارند، اما Phase 1 نباید هزینه‌ی جداسازی کامل microservice را تحمیل کند.

## Decision

هفت API host پشت YARP Gateway قرار می‌گیرند و کتابخانه‌های Core مشترک‌اند. پنج DbContext مستقل مرز persistence را حفظ می‌کنند. backendها public exposure مستقیم ندارند.

## Consequences

- deployment و scaling هر host مستقل ممکن است.
- library/database coupling هنوز نیازمند discipline و architecture test است.
- Gateway نقطه‌ی auth/rate-limit و routing است؛ backend نیز authorization policy خود را enforce می‌کند.
- transaction cross-context وجود ندارد.

## Verification

architecture reference tests، Gateway route tests، auth policy tests و migration مستقل هر context لازم‌اند.
