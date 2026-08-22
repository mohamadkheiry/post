# گزارش تحویل بستهٔ تستر — 2026-08-22

- source revision: `e2f9b4b98dc1b0e03076c427f8b407a6f7deb7b6`
- archive: `post-platform-tester-e2f9b4b-win-x64.zip`
- archive SHA-256: `400ca8594fd01386c7d8e925550730ad9545f2fcaca85b8e838ef97b037a72af`
- target: Windows x64؛ runtime قابل‌حمل .NET 10.0.11 درون بسته
- محتوا: ده خروجی Release، simulator، راهنما، HTTP/Postman، دیاگرام، evidence و
  checksum برای 1287 فایل

## verification اجراشده

1. entryهای اجباری ZIP و SHA-256 تمام فایل‌های extract‌شده بررسی شد: **PASS**.
2. ZIP در مسیر جدا extract و با
   `Start-TesterLocal.ps1 -Profile Mock -ResetData` اجرا شد: 9 process host ready
   شدند: **PASS**.
3. `Run-TesterSmoke.ps1 -TimeoutSeconds 90` روی نسخهٔ extract‌شده اجرا شد:
   WebSocket، authentication، inbound همراه retry idempotent، destination، bag و
   dispatch: **PASS**.
4. `Stop-Tester.ps1` با PID/start-time/command-line validation نه process را امن
   متوقف کرد: **PASS**.
5. credential hygiene: password عملیاتی به‌صورت exact در همهٔ 1287 فایل جست‌وجو
   شد و username فقط در contextهای credential در فایل‌های text/config بررسی شد:
   **PASS**. دو occurrence باینریِ بدون context از username کوتاه، credential leak
   محسوب نمی‌شوند. هیچ credential واقعی در archive قرار نگرفت.

این artifact برای Mock acceptance محلی است. `LocalConnected` credential را فقط در
prompt می‌گیرد، persist نمی‌کند و تنها با مجوز endpoint واقعی قابل استفاده است.
