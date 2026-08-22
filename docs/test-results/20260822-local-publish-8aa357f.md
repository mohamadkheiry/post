# شاهد انتشار و اجرای محلی

- زمان اجرا: ۲۰۲۶-۰۸-۲۲
- source revision: `8aa357fc093377e1a9d513a1bbdebbd2954f9e91`
- محیط: Windows x64، .NET SDK `10.0.400`، configuration برابر `Release`
- نتیجه: `PASS`

## سناریوهای اجراشده

1. `scripts/publish-local.ps1` با locked restore اجرا و هر ۱۰ deployable شامل هشت
   Core/Gateway host، Edge API و Device Simulator در `artifacts/publish` تولید شد.
2. ۹ host اجرایی با profile برابر `Mock`، SQLite3MC و کلید بیرونی موقت بالا آمدند؛
   readiness گیت Gateway و Edge پاسخ ۲۰۰ داد و header فایل Edge با SQLite plaintext
   magic یکسان نبود.
3. همان ۹ host با profile برابر `LocalConnected` بالا آمدند. credential مقصد فقط از
   .NET user-secrets محلی خوانده شد؛ check نام‌دار `edge_destination_oauth` و readiness
   کل Edge سبز شدند. Postal و Core مطابق تعریف این profile در حالت Mock باقی ماندند.
4. stop runner، PID، زمان شروع و نام DLL هر process را پیش از توقف تطبیق داد و هر ۹
   process را متوقف کرد. هیچ stderr غیرخالی باقی نماند.
5. exact-value hygiene scan پس از توقف اجرا شد: دو credential پیکربندی‌شده در ۴۹۵
   فایل repository، ۴۵۷ blob تاریخچه و logهای runtime یافت نشدند.

هیچ مقدار credential، access token، authorization header یا کلید SQLite در این شاهد
ذخیره نشده است. چون credential مقصد در آرشیو ورودی اولیه به‌صورت plaintext دیده شده
بود، پس از اعتبارسنجی باید در سامانه مالک revoke/rotate شود.
