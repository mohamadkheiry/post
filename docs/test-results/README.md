# انتشار شواهد تست

این directory محل گزارش curated و قابل بازبینی همان commit است. خروجی حجیم و machine-specific در `artifacts/test-results/` تولید می‌شود و به‌طور پیش‌فرض commit نمی‌شود.

## آخرین اجرای تأییدشده

| field | value |
| --- | --- |
| Commit | `TBD_AFTER_FINAL_RUN` |
| Generated UTC | `TBD_AFTER_FINAL_RUN` |
| .NET SDK | `TBD_AFTER_FINAL_RUN` |
| Configuration | `Release` |
| Passed | `TBD_AFTER_FINAL_RUN` |
| Failed | `TBD_AFTER_FINAL_RUN` |
| Skipped | `TBD_AFTER_FINAL_RUN` |
| Total | `TBD_AFTER_FINAL_RUN` |
| Line coverage | `TBD_AFTER_FINAL_RUN` |
| Branch coverage | `TBD_AFTER_FINAL_RUN` |
| Method coverage | `TBD_AFTER_FINAL_RUN` |

Placeholderها عمداً تا اجرای نهایی کل solution حفظ شده‌اند؛ آمار یک زیرمجموعه یا commit متفاوت نباید به‌عنوان نتیجه‌ی کل پروژه ثبت شود.

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
TRX/Cobertura و pathهای محیطی منتشر کنید:

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
environment، inventory نام تست‌ها، نتیجهٔ redacted اجرای واقعی full-development
و checksum همهٔ این فایل‌هاست و نتیجهٔ موجود را overwrite نمی‌کند. runtime فقط
اگر PASS، متعلق به همان commit تمیز، شامل connected-edge/observability/HA و دارای
baseline حداقل دو مرسوله/ثانیه با خطای صفر باشد پذیرفته می‌شود.

## promotion به Git

1. مطمئن شوید working tree و commit مورد گزارش مشخص است.
2. test script را روی Release و بدون test filter اجرا کنید.
3. failure و skip غیرمنتظره را رفع کنید؛ فقط خروجی سبز قابل انتشار است.
4. `summary.md` و `environment.json` را از نظر نبود path/secret/private input بررسی کنید.
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
