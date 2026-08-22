# ADR-0001: عملیات Edge-first و مرجع حقیقت

- Status: Accepted
- Date: 2026-08-16

## Context

خط تفکیک نباید با قطع موقت Core متوقف شود، ولی سامانه‌ی پستی بالادست مرجع رسمی پذیرش و بسته‌بندی است.

## Decision

Edge use caseها و state محلی را durable اجرا و رخدادها را asynchronous به Core sync می‌کند. Postal upstream مرجع رسمی نتیجه‌ی پستی، Edge مرجع اجرای local و Core مرجع داخلی تاریخچه/config/reporting است.

## Consequences

- Core از critical path بیشتر عملیات حذف می‌شود.
- reconciliation و conflict policy ضروری است.
- outage policy dependencyهای پستی باید برای هر use case تصویب شود.
- هیچ projection یا Redis cache مرجع رسمی نیست.

## Verification

تست قطع Core، رشد outbox، restart Edge و sync بدون duplicate باید پاس شود.
