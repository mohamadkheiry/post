# بستهٔ تحویل به تستر

این بسته برای تست محلی Windows x64 آماده می‌شود. شامل خروجی‌های Release ده host،
runtime قابل‌حمل .NET، simulator، دادهٔ کاملاً مصنوعی، نمونه‌های HTTP/Postman،
دیاگرام‌ها، گزارش تست و checksum است. source repository یا credential واقعی در آن
وجود ندارد.

## پیش‌نیاز و ظرفیت VM

برای اجرای بستهٔ Mock بدون Docker: Windows Server 2022/Windows 11 x64، PowerShell
7، چهار vCPU، هشت GiB RAM و 60 GiB فضای آزاد SSD کافی است. runtime مورد نیاز
داخل بسته است؛ نصب .NET لازم نیست.

برای full-development Docker که SQL Server، Redis، Kafka-compatible و S3-compatible
را هم اجرا می‌کند، شش vCPU، 16 GiB RAM و 120 GiB SSD پیشنهاد می‌شود. برای اجرای
هم‌زمان HA و observability، هشت vCPU، 24 GiB RAM و 200 GiB SSD در نظر بگیرید.
اعداد 8 GiB برای topology پایه و حدود 12 GiB برای سرویس‌های HA/observability در
`deploy.md` حداقل‌های container هستند؛ سیستم‌عامل و cache Docker به headroom نیاز
دارند.

## اجرای Mock قابل تکرار

1. ZIP را در یک مسیر کوتاه، مثلاً `C:\PostTester`، extract کنید.
2. PowerShell 7 را در همان مسیر باز کنید و فقط برای همان process policy را bypass
   کنید:

   ```powershell
   Set-ExecutionPolicy -Scope Process Bypass
   .\scripts\tester\Start-TesterLocal.ps1 -Profile Mock -ResetData
   .\scripts\tester\Run-TesterSmoke.ps1
   ```

3. smoke باید پیام موفقیت برای authentication، inbound، destination، bag و dispatch
   نشان دهد. URLهای محلی gateway و Edge به‌ترتیب `http://127.0.0.1:7088` و
   `http://127.0.0.1:7080` هستند.
4. در پایان، processها را با دستور زیر متوقف کنید. log و database برای بررسی حفظ
   می‌شوند:

   ```powershell
   .\scripts\tester\Stop-Tester.ps1
   ```

`-ResetData` فقط `artifacts\tester-runtime` همان بسته را پاک می‌کند و قبل از آن
وجود processهای ثبت‌شده را بررسی می‌کند. کلید رمزگذاری و token مصنوعی هنگام اولین
اجرا به‌شکل DPAPI محلی در `artifacts\tester-local-secrets` ساخته می‌شوند؛ این
فایل‌ها در ZIP اولیه وجود ندارند و هرگز در log چاپ نمی‌شوند.

## داده و سناریوی مصنوعی

fixture پیش‌فرض مرکز `12345`، device `tester-device` و logical ID `TESTER-01` را
می‌سازد. simulator یک مرسولهٔ مصنوعی، تصویر سه‌بایتی، یک bag و یک dispatch می‌سازد.
این داده برای acceptance محلی است و نباید با داده یا credential عملیاتی مخلوط شود.

برای Postman، collection و environment آماده در این مسیرها هستند:

- `docs\api\post-platform.postman_collection.json`
- `docs\api\post-platform.postman_environment.tester.json`

نمونهٔ قابل‌خواندن با REST Client نیز `docs\api\post-platform.http` است. محیط Mock
به‌صورت عمدی auth Core را bypass می‌کند و معیار Production security نیست.

## LocalConnected

اگر مجوز endpoint مقصد را دارید، اجرا کنید:

```powershell
.\scripts\tester\Start-TesterLocal.ps1 -Profile LocalConnected -ResetData
```

username و password OAuth مقصد فقط در prompt دریافت و فقط به process Edge منتقل
می‌شوند؛ ذخیره، چاپ یا افزودن به ZIP نمی‌شوند. این profile فقط زمانی مجاز است که
تستر اجازهٔ استفاده از مقصد واقعی را دارد. Postal و Core در این launcher همچنان
fixture محلی‌اند.

## صحت بسته و گزارش اشکال

قبل از اجرا SHA-256 فایل ZIP را با فایل هم‌نام `.sha256` کنترل کنید. پس از extract،
`checksums.sha256` hash تمام فایل‌های بسته را نگه می‌دارد.

برای گزارش defect، نسخهٔ `VERSION.json`، زمان UTC، command اجراشده، نتیجه، trace یا
correlation ID و فایل log مربوط از `artifacts\tester-runtime\logs` را ارسال کنید.
token، password، database رمز‌شده، تصویر واقعی یا کلید DPAPI را ارسال نکنید.

راهنمای کامل استقرار در `deploy.md` و روش‌های کامل تست در `docs\TESTING.md` قرار
دارند.
