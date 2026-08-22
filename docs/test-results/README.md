# انتشار شواهد تست

این directory محل گزارش curated و قابل بازبینی همان commit است. خروجی حجیم و machine-specific در `artifacts/test-results/` تولید می‌شود و به‌طور پیش‌فرض commit نمی‌شود.

## آخرین اجرای تأییدشده

| field | value |
| --- | --- |
| Evidence | [`20260822-ed77486`](20260822-ed77486) |
| Commit | `ed77486` |
| Generated UTC | `2026-08-22T10:39:12.4271682Z` |
| .NET SDK | `10.0.400` |
| Configuration | `Release` |
| Passed | `601` |
| Failed | `0` |
| Skipped | `0` |
| Total | `601` |
| Handwritten methods | `1244/1244` |
| Entirely uncovered handwritten methods | `0` |
| Line coverage | `91.51%` |
| Branch coverage | `76.89%` |
| Method coverage | `99.8%` |

این نتیجه متعلق به اجرای کامل solution و full-development E2E روی commit بالا است؛
آمار زیرمجموعه یا commit متفاوت جایگزین آن نیست.

شاهد مستقل publish و اجرای محلی Mock/LocalConnected روی نسخهٔ نهایی runner در
[`20260822-local-publish-8aa357f.md`](20260822-local-publish-8aa357f.md) ثبت شده است.

## تولید گزارش

```powershell
.\scripts\run-tests.ps1 -Configuration Release -OutputRoot artifacts/test-results
```

اسکریپت این artefactها را تولید می‌کند:

- یک TRX برای هر test project؛
- Cobertura XML برای coverage؛
- `summary.md` با جمع testها و coverage aggregate؛
- `environment.json` با SDK/OS/commit/command؛
- `checksums.sha256` برای تشخیص تغییر artefact.

پس از یک اجرای سبز روی working tree تمیز، نسخهٔ curated و قابل commit را بدون
TRX/Cobertura، pathهای محیطی و آرگومان‌های تست‌های پارامتری منتشر کنید:

```powershell
$runtimeEvidence = Get-ChildItem .\artifacts\full-dev\full-dev-*.json |
  Sort-Object LastWriteTimeUtc -Descending |
  Select-Object -First 1
.\scripts\publish-test-evidence.ps1 `
  -InputDirectory artifacts/test-results `
  -RuntimeEvidencePath $runtimeEvidence.FullName
```

این command فقط وقتی می‌پذیرد که commit اجرای تست با `HEAD` یکسان، working tree
در زمان اجرا تمیز، failed/skipped صفر و تعداد متدهای دست‌نویس کاملاً بدون پوشش
صفر باشد. خروجی versioned زیر `docs/test-results/<date>-<commit>/` شامل summary،
environment، inventory نام تست‌ها با Theory argumentهای redacted، نتیجهٔ اجرای
واقعی full-development به‌صورت redacted
و checksum همهٔ این فایل‌هاست و نتیجهٔ موجود را overwrite نمی‌کند. runtime فقط
اگر PASS، متعلق به همان commit تمیز، شامل connected-edge/observability/HA و دارای
baseline حداقل دو مرسوله/ثانیه با خطای صفر باشد پذیرفته می‌شود.

## promotion به Git

1. مطمئن شوید working tree و commit مورد گزارش مشخص است.
2. test script را روی Release و بدون test filter اجرا کنید.
3. failure و skip غیرمنتظره را رفع کنید؛ فقط خروجی سبز قابل انتشار است.
4. `summary.md` و `environment.json` را از نظر نبود path/secret/private input بررسی کنید؛
   publisher آرگومان‌های Theory را با `<arguments-redacted>` جایگزین می‌کند.
5. جدول بالا را با همان run به‌روزرسانی کنید.
6. `publish-test-evidence.ps1` را اجرا و directory ساخته‌شده را بازبینی کنید؛
   TRX/Cobertura خام commit نمی‌شوند.
7. جدول «آخرین اجرای تأییدشده» را به همان directory/commit همگام کنید.
8. `scripts/verify-no-secrets.ps1` و exact operational-secret scan را دوباره اجرا کنید.

## قواعد تمامیت

- test result بدون commit ID معتبر نیست.
- rerun باید artefact قبلی را overwrite یا در directory versioned جدا ذخیره کند.
- checksum باید پس از آخرین redaction تولید شود.
- coverage یک شاخص است، نه جایگزین assertion quality یا requirement coverage.
- generated code، migration و auto-property trivial ممکن است از behavioral coverage خارج شوند؛ `Program.cs` و endpoint handlerهای دست‌نویس مستثنا نیستند و exclusion باید در script شفاف باشد.
- هیچ credential، authorization header، connection string، payload تصویر یا متن خصوصی منبع در evidence قرار نمی‌گیرد.
