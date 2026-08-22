# راهنمای اجرایی برای Agentهای بعدی

این فایل guardrail عملیاتی repository است. متن آرشیو نیازمندی و هیچ credential را در prompt، log، test evidence، commit یا مستندات بازنشر نکنید.

## ترتیب مطالعه و مرجع حقیقت

1. این فایل، سپس `README.md` و `deploy.md`؛
2. `docs/requirements/traceability.md` و blocker register؛
3. `docs/architecture/overview.md`، `domain-model.md`، `reliability-security.md` و ADRها؛
4. `docs/api/device-protocol.md` و `docs/api/core-api.md`؛
5. برای رفتار دقیق، کد و test همان revision؛ برای wire persisted، `src/Shared/Post.Contracts`؛ برای REST runtime، OpenAPI همان build.

اگر سند با کد/قرارداد تست‌شده تعارض داشت، تغییر را متوقف نکنید: علت را مشخص، test سازگاری را اضافه و کد، traceability، ADR/API doc و نمونه‌های HTTP/Postman را در همان change همگام کنید. ادعای `Implemented` بدون composition root قابل اجرا و test مجاز نیست.

## مرزهای معماری

```text
Domain -> Shared contracts only
Application -> Domain + Shared contracts/building blocks
Infrastructure -> Application ports + Domain + Shared
API host -> Application + Infrastructure + ApiSupport
Gateway -> BuildingBlocks + YARP/authentication
Tests -> project under test
```

- invariant در Domain، orchestration و port در Application، I/O در Infrastructure و wiring/wire mapping در Host می‌ماند.
- Device فقط به Edge وصل می‌شود؛ Edge اجرای محلی/تداوم سرویس را مالک است؛ Core تاریخچه، هویت، config و control plane را مالک است؛ سامانه‌ی پستی بالادست مرجع رسمی عملیات پستی است.
- EF entity، client HTTP/SQL/SOAP و framework hosting نباید به Domain نشت کنند.
- هر bounded Core host فقط profile و DbContext خودش را register می‌کند؛ Gateway منطق business ندارد.

## invariantهای غیرقابل مذاکره

- Device endpoint برابر `/ws/device` و transport فقط WebSockets است. methodها: `Auth`, `RegisterInbound`, `AssignDestination`, `CloseBag`, `CloseDispatch`.
- envelope V1 شامل version/type/correlation/timestamp/payload و `operationId` اختیاری است. برای retry اثرگذار، operation ID پایدار می‌ماند؛ correlation ID شناسه‌ی attempt/trace است.
- semantic envelope error با `protocol.error` canonical برمی‌گردد؛ JSON/argument غیرقابل bind یا target ناشناخته در typed Hub فاز ۱ safe SignalR Completion/connection error است. ادعای envelope برای pre-binding failure بدون transport redesign ممنوع است.
- Auth ورودی `deviceId + deviceToken` و خروجی فقط status `0` یا `2` است؛ `sessionId` و `edgeServerTime` فقط در موفقیت حضور دارند و failure پس از تحویل پاسخ connection را می‌بندد.
- status عمومی Device برابر `0..4` و status مقصد `1/2` است. Auth/Bag/Dispatch فقط `0/2` دارند و `LabelPending` با status `0` برمی‌گردد. enumهای Device و Core جدا هستند؛ cast عددی ممنوع و mapping باید exhaustive/tested باشد.
- `CloseBag.count` اطلاعاتی است، سقف انتخاب نیست؛ filterهای `chuteIds`, `parcelTypes`, `serviceTypes` array هستند و `AllParcelsFailed` نتیجه‌ای مستقل است.
- Device در `CloseDispatch` فقط destination می‌فرستد؛ Edge کیسه‌های undispatched همان device/destination را کشف می‌کند؛ wire result فقط `Completed` یا `Error` است.
- inbound حداکثر ۳۲ تصویر و هر Base64 حداکثر ۵٬۰۰۰٬۰۰۰ character دارد؛ هر ۳۲ تصویر در سقف per-image باید معتبر بمانند. receive پیش‌فرض ۱۹۲ MiB/hard max ۲۵۶ MiB است؛ aggregate cap پایین‌تر بدون V2 ممنوع و payload تصویر یا supplementary data هرگز log نمی‌شود.
- bootstrap هرگز raw device token یا IP binding نمی‌دهد؛ فقط PBKDF2 verifier versioned است. IP binding state محلی Edge است و config sync آن را حذف نمی‌کند. unbound device به‌طور پیش‌فرض رد می‌شود؛ bypass فقط Development/Mock و صریح است.
- provisioning sorting-device و provisioning/rotation اعتبار Edge secret خام را فقط در پاسخ اول برمی‌گردانند؛ persistence/snapshot فقط verifier/receipt بدون secret دارد و replay مقدار را بازنمی‌گرداند. response secret log/commit نمی‌شود؛ در فقدان/افشا، credential disable و با key تازه rotate می‌شود.
- idempotency/replay برای عملیات اثرگذار، transactional outbox، retry محدود و dead-letter حفظ می‌شوند. exactly-once end-to-end ادعا نکنید.
- route صدور token در Gateway anonymous است؛ business routeها authenticated هستند. health/root/metrics در application anonymous و در شبکه محدود می‌شوند.

## integration profileها

| profile | Destination | Postal | Core | کاربرد |
| --- | --- | --- | --- | --- |
| `Mock` | Mock | Mock | Mock | offline development/CI |
| `LocalConnected` | Real | Mock | Mock | default کد؛ contract-test مقصد |
| `Connected` | Real | Real | Real | staging/production؛ validation سخت و HTTPS |
| `Custom` | صریح | صریح | صریح | تست ترکیبی کنترل‌شده |

alias قدیمی `Edge:Integrations:Mode=Mock|Real` فقط برای سازگاری است. fallback کد `LocalConnected` است، اما committed appsettings و Compose برای local safety مقدار `Mock` را صریح دارند؛ offline/CI نیز این key را حفظ می‌کند. credentialهای username/password در `Edge:DestinationApi` فقط نام‌کاربری/گذرواژهٔ OAuth با `grant_type=password` برای جست‌وجوی مقصد Iran Post و تبدیل بارکد ۱۴ رقمی به `ToCityID` هستند، نه OAuth Client Credentials grant؛ Edge destination adapter token را cache می‌کند. این مقادیر device token، Core JWT، SQL یا SOAP credential نیستند و local فقط با .NET user-secrets فراهم می‌شوند. چون آرشیو اولیه مقدار plaintext داشته است، پس از validation باید credential rotate شود.

## migration و persistence

- Edge: SQLite/WAL، single active writer و migration قبل از traffic؛ DB/WAL/SHM را یک recovery set بدانید.
- Core: پنج chain مستقل `ParcelLifecycle`, `DeviceConfig`, `EdgeHealth`, `OperationalResult`, `Identity`؛ migration به context مالک تعلق دارد.
- Production فقط SQL Server و `Migrate`/script review‌شده؛ `EnsureCreated` فقط Testing/SQLite است.
- migration destructive، down خودکار Production یا اجرای همزمان migration توسط replicaها ممنوع است. backup، idempotent SQL، staging rehearsal و rollback/restore plan لازم‌اند.

## gate اجباری تغییر

```powershell
dotnet build .\Post.sln -c Release
.\scripts\run-tests.ps1 -Configuration Release
.\scripts\verify-migrations.ps1 -Configuration Release -NoBuild
.\scripts\verify-diagrams.ps1
.\scripts\verify-doc-links.ps1
.\scripts\verify-api-samples.ps1
.\scripts\verify-no-secrets.ps1
.\scripts\verify-dotnet-packages.ps1
.\scripts\infra\verify-image-locks.ps1
npx --yes markdownlint-cli2@0.23.2 --config docs/.markdownlint.jsonc AGENTS.md README.md SECURITY.md deploy.md "docs/**/*.md" "infra/**/*.md"
docker compose -p post-platform-final -f compose.yaml config --quiet
docker compose -p post-platform-final -f compose.yaml -f compose.full.yaml config --quiet
```

- analyzerها و warnings-as-errors باید بدون warning سبز باشند.
- هر متد hand-written .NET بر اساس رفتار، boundary، failure و cancellation تست می‌شود؛ serializer/mapper نیز golden/exhaustive test می‌خواهد.
- تغییر contract باید additive/versioned و همراه serialization/status mapping test باشد.
- تغییر نمودار، هم `.mmd` و هم SVG رندرشده را به‌روزرسانی می‌کند.
- نتیجه‌ی نهایی فقط از full unfiltered run همان commit در `docs/test-results/README.md` ثبت می‌شود؛ آمار زیرمجموعه جایگزین آن نیست.
- runtime نهایی همان commit تمیز باید با `test-full-dev.ps1 -ResetData -IncludeObservability -IncludeHa -RequireCleanRevision` اجرا و JSON redacted آن همراه نتیجهٔ تست از `publish-test-evidence.ps1` منتشر شود.
- Markdown، linkها، JSONهای API و requestهای بدون secret را نیز validate کنید.
- smoke artifact local از `start-published-local.ps1`/`stop-published-local.ps1` استفاده می‌کند؛ processهای ثبت‌شده را دستی kill نکنید و `artifacts/local-runtime/` را هنگام اجرا پاک نکنید.

## فایل‌ها و داده‌های ممنوع در Git

- `.env*` به‌جز template خالی مجاز، `appsettings.Local.json`، user-secrets، vault export؛
- token/password/connection string/pre-signed URL، certificate/private key و Authorization header؛
- `_requirements/` و archive ورودی، متن خصوصی منبع یا screenshot/capture حاوی داده‌ی واقعی؛
- `*.db`, `*.db-wal`, `*.db-shm`, log، dump، packet capture و backup؛
- `bin/`, `obj/`, `artifacts/`, `TestResults/`, TRX/coverage خام یا environment پرشده؛ فقط evidence curated و redacted طبق policy.

پیش از commit، staged diff و history را با secret scanner بازبینی کنید. redaction فایل جدید، secret لو‌رفته را از history پاک نمی‌کند؛ revoke/rotate و incident process لازم است.

## blockerهای بیرونی باز

- قالب/version label و printer language؛
- SQL connection/schema/stored procedure و semantics رسمی idempotency؛
- SOAP WSDL/action/namespace/fault catalog؛
- Excelهای مرجع routing/service/postal point؛
- الگوریتم رسمی check digit و canonical barcode؛
- NFR/SLO عددی: baseline pilot فقط دو مرسوله/ثانیه است؛ latency تعاملی چندصد میلی‌ثانیه و Redis زیر یک میلی‌ثانیه scopeهای جدا هستند. هیچ SLA انتهابه‌انتها ۲ ms ادعا نکنید؛ percentile/burst/concurrency/availability/capacity/RPO/RTO بازند؛
- retention، deletion و legal hold؛
- topology نهایی Production/HA و sign-offهای امنیت/عملیات.

این موارد را با mock یا حدس «حل‌شده» علامت نزنید. شرط بسته‌شدن هر مورد و test لازم در traceability ثبت شده است.
