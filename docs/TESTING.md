# راهنمای کامل تست

این راهنما برای توسعه‌دهنده، QA و اپراتور staging نوشته شده است. تست‌ها را ابتدا در Mock و سپس فقط با مجوز و credential بیرونی در Connected اجرا کنید. هیچ credential واقعی نباید در command history، screenshot، TRX، log یا Git قرار گیرد.

## سطوح تست

| سطح | هدف | dependency |
| --- | --- | --- |
| Unit | invariant و branch هر متد دست‌نویس | ندارد/fake |
| Contract | JSON، enum wire values، version و mapping | ندارد |
| Application | orchestration، idempotency، timeout و cancellation | fake port |
| Infrastructure | EF mapping، transaction، SQLite، adapter behavior | SQLite/local fake server |
| API integration | route، auth، Problem Details، health و DI | test host |
| Architecture | dependency direction و naming/boundary | assembly metadata |
| Manual Mock | جریان واقعی SignalR/REST بدون secret سازمانی | local processes |
| Connected contract | سازگاری sandbox SQL/SOAP/HTTP/S3/Core | credential مجاز |
| Resilience | outage/restart/replay/disk/backlog | staging isolated |
| Performance | baseline pilot حداقل ۲ مرسوله/ثانیه + throughput/latency/soak | baseline قابل تست؛ sign-off پس از NFR رسمی |

## پیش‌نیاز

```powershell
dotnet --version
dotnet tool restore
git status --short
```

نسخه‌ی SDK باید با `global.json` سازگار باشد. test evidence زمانی معتبر است که commit/working tree و environment ثبت شده باشد.

اجرای scriptها به PowerShell 7 نیاز دارد؛ Node.js/npm فقط برای markdownlint نسخه‌پین‌شده مصرف می‌شود. Docker Compose برای smoke کانتینری لازم است، اما smoke process-based با publish محلی مستقل از Docker Engine است.

## اجرای خودکار استاندارد

از root repository:

```powershell
.\scripts\run-tests.ps1 -Configuration Release
```

این script:

1. solution را restore و با analyzerهای `latest-recommended` و warnings-as-errors build می‌کند؛
2. همه‌ی test projectها را بدون filter اجرا می‌کند؛
3. TRX و Cobertura می‌سازد؛
4. نتیجه، environment و checksum را در `artifacts/test-results/` ثبت می‌کند؛
5. تمام assemblyهای production، از جمله composition rootها و simulator، را در
   coverage می‌بیند و با هر failure/skip، assembly instrumentنشده، متد دست‌نویس
   کاملاً بدون پوشش یا عبور نکردن از ۸۰٪ line، ۷۰٪ branch و ۸۵٪ method خروجی
   non-zero می‌دهد.

برای commit کردن نتیجهٔ redacted و منظم، ابتدا source/docs را commit کنید، اجرای
بالا را روی working tree تمیز انجام دهید و سپس بزنید:

```powershell
$runtimeEvidence = Get-ChildItem .\artifacts\full-dev\full-dev-*.json |
  Sort-Object LastWriteTimeUtc -Descending |
  Select-Object -First 1
.\scripts\publish-test-evidence.ps1 `
  -InputDirectory artifacts/test-results/<run-directory> `
  -RuntimeEvidencePath $runtimeEvidence.FullName
```

خروجی curated زیر `docs/test-results/<date>-<commit>/` به همان source commit
متصل است. runtime JSON نیز فقط برای PASS همان commit تمیز، اجرای واقعی سه profile
connected-edge/observability/HA و baseline خطای صفر و حداقل دو مرسوله/ثانیه
پذیرفته می‌شود. TRX، Cobertura خام، log و path محلی وارد Git نمی‌شوند.

گیت‌های مکمل:

```powershell
dotnet format .\Post.sln --no-restore --verify-no-changes --severity warn
.\scripts\verify-diagrams.ps1
.\scripts\verify-doc-links.ps1
.\scripts\verify-migrations.ps1 -Configuration Release -NoBuild
.\scripts\verify-api-samples.ps1
.\scripts\verify-no-secrets.ps1
.\scripts\verify-dotnet-packages.ps1
.\scripts\infra\verify-image-locks.ps1
.\scripts\verify-operational-secrets-absent.ps1 `
  -EnvSecretDirectory .secrets/full-dev `
  -ComposeProject post-platform-final
npx --yes markdownlint-cli2@0.23.2 --config docs/.markdownlint.jsonc AGENTS.md README.md SECURITY.md deploy.md "docs/**/*.md" "infra/**/*.md"
docker compose -p post-platform-final -f compose.yaml config --quiet
docker compose -p post-platform-final -f compose.yaml -f compose.full.yaml config --quiet
```

گیت package با خروجی JSON خود SDK تمام dependencyهای مستقیم و transitive را در
برابر vulnerability و deprecation metadata منبع NuGet بررسی می‌کند و در خطای
دریافت metadata نیز fail می‌شود. گیت دوم secret، مقدارهای عملیاتی موجود در
user-secrets Edge را بدون چاپ آن‌ها به‌صورت exact در فایل‌های قابل commit،
logهای local runtime و تمام blobهای Git history جست‌وجو می‌کند. دو credential
مقصد برای این gate الزامی‌اند؛ مقدار secret، token یا password در خروجی report
ذخیره نمی‌شود.

## اجرای انتخابی

```powershell
dotnet test .\tests\Post.Shared.Tests -c Release
dotnet test .\tests\Post.Edge.Domain.Tests -c Release
dotnet test .\tests\Post.Edge.Application.Tests -c Release
dotnet test .\tests\Post.Edge.Infrastructure.Tests -c Release
dotnet test .\tests\Post.Core.Domain.Tests -c Release
dotnet test .\tests\Post.Core.Application.Tests -c Release
dotnet test .\tests\Post.Core.Infrastructure.Tests -c Release
dotnet test .\tests\Post.Api.IntegrationTests -c Release
dotnet test .\tests\Post.Architecture.Tests -c Release
```

filter برای چرخه‌ی توسعه مفید است، اما evidence نهایی باید بدون filter باشد:

```powershell
dotnet test .\Post.sln -c Release --filter "FullyQualifiedName~Bag"
```

## استاندارد تست تمام متدهای دست‌نویس .NET

برای هر متد public/internal دست‌نویس متناسب با رفتار این ماتریس بررسی می‌شود:

| دسته | موارد لازم |
| --- | --- |
| constructor/factory | مقدار معتبر، null/empty، boundary، normalization |
| state mutation | transition مجاز، transition ممنوع، invariant و atomicity |
| query/calculation | empty، single، boundary، overflow/ordering |
| async service | success، dependency failure، timeout، cancellation، retry/replay |
| serializer/mapper | golden wire shape، تمام enumها، unknown value |
| repository | round-trip، unique/index conflict، rollback و cancellation |
| security | credential درست/غلط، scope/role، redaction و expiry |
| worker | due/not-due، lock expiry، backoff، dead-letter، poison payload |

موارد generated مانند EF migration و compiler-generated property از behavioral coverage کنار گذاشته می‌شوند. `Program.cs` و endpoint handlerهای دست‌نویس مستثنا نیستند و باید با host/integration test اجرا شوند. gate خودکار حداقل `80%` line، `70%` branch و `85%` method coverage را روی کد باقی‌مانده الزام می‌کند، هیچ متد دست‌نویس کاملاً اجرا‌نشده و هیچ failed/skipped test را نمی‌پذیرد. سازندهٔ استاتیک فقط وقتی مستثنا می‌شود که در source سازندهٔ صریحی وجود نداشته و کامپایلر آن را برای مقداردهی field ساخته باشد. «یک test که فقط method را call کند» پوشش حرفه‌ای محسوب نمی‌شود؛ assertion باید observable outcome و عدم side effect ناخواسته را ثابت کند.

در performance report، latency sorting تعاملی، زمان component-level Redis read و latency انتهابه‌انتها را سه metric جدا ثبت کنید. هدف Redis زیر یک میلی‌ثانیه را به کل request تعمیم ندهید و threshold ساختگی ۲ ms نسازید. تا تصویب percentile/burst/concurrency/payload mix، عبور از baseline pilot دو مرسوله/ثانیه فقط smoke ظرفیت است، نه SLO acceptance.

## آماده‌سازی Mock Edge

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:Edge__Integrations__Profile = 'Mock'
$env:Edge__Storage__ConnectionString = 'Data Source=.\artifacts\manual\edge.db'
$env:Edge__Mock__CenterCode = '12345'
$env:Edge__Mock__Devices__0__DeviceId = 'manual-device'
$env:Edge__Mock__Devices__0__LogicalId = 'MANUAL-01'
$env:Edge__Mock__Devices__0__Token = 'local-test-value-only'
$env:Edge__Security__AllowUnboundDevices = 'true' # فقط Development Mock
$env:Edge__LocalAdmin__AuthenticationEnabled = 'false' # فقط smoke loopback
$env:ASPNETCORE_URLS = 'http://127.0.0.1:7080'
dotnet run --project .\src\Edge\Post.Edge.Api
```

در terminal دوم:

```powershell
Invoke-RestMethod http://127.0.0.1:7080/health
Invoke-RestMethod http://127.0.0.1:7080/health/ready
```

SQLite state را بین سناریوهای مستقل reuse نکنید؛ یک path تازه بسازید یا backup مشخص restore کنید. حذف database تنها وقتی مجاز است که process Edge متوقف و target دقیقاً artefact تست باشد.

جایگزین containerized برای همین profile:

```powershell
docker compose -p post-platform-mock -f compose.yaml config --quiet
docker compose -p post-platform-mock -f compose.yaml up --detach --build
Invoke-RestMethod http://127.0.0.1:7088/health
Invoke-RestMethod http://127.0.0.1:7080/health
```

پایان تست با `docker compose -p post-platform-mock -f compose.yaml down` volumeها
را نگه می‌دارد. compose local شامل credential مصنوعی و authentication خاموش است
و نباید معیار security acceptance یا Connected contract test باشد.

گیت containerized واقعی مستقل از Mock است:

```powershell
$env:POST_DOTNET = (Get-Command dotnet).Source
.\scripts\infra\test-full-dev.ps1 `
  -ResetData `
  -IncludeObservability `
  -IncludeHa `
  -RequireCleanRevision
```

این اجرا از secretهای تصادفی ignored استفاده می‌کند و auth/provisioning، پنج SQL
DB، TDE، Edge SQLite encryption، Redis outage/two-replica، Kafka restart/DLQ،
S3 PUT/SSE/link، simulator کامل، baseline دو مرسوله/ثانیه، Prometheus/Loki/Grafana
و AG failover را روی dependency واقعی می‌سنجد. `-ResetData` destructive ولی فقط
برای project synthetic و scope-validated است؛ روی state لازم حذف شود. برای
عیب‌یابی می‌توان `-KeepRunning` افزود، سپس فقط با command project-scoped موجود در
`deploy.md` آن را پایین آورد. Destination و Postal این gate Mock هستند و نتیجهٔ
آن جای sandbox contract رسمی پست نیست.
برای debug پیش از commit می‌توان `-RequireCleanRevision` را حذف کرد، اما چنین
اجرایی اجازهٔ publication در `docs/test-results` را ندارد.

برای smoke همهٔ hostها از artifact منتشرشده:

```powershell
.\scripts\publish-local.ps1 -Configuration Release
.\scripts\start-published-local.ps1 -EdgeProfile Mock
.\scripts\verify-api-samples.ps1
Invoke-RestMethod http://127.0.0.1:7088/health/ready
Invoke-RestMethod http://127.0.0.1:7080/health/ready
.\scripts\stop-published-local.ps1
```

runner readiness هر نه host را بررسی و runtime state/log/SQLite را زیر `artifacts/local-runtime/` نگه می‌دارد. پایان همیشه با stop script باشد؛ آن script PID، start time و DLL command را validate می‌کند و database/logها را پاک نمی‌کند. `LocalConnected` به OAuth credential مقصد و endpoint/network صریحاً مجاز نیاز دارد و readiness آن عمداً external check را اجرا می‌کند.

برای پوشش artifact رمز‌شده، یک کلید local پایدار با حداقل ۳۲ character را از
secret manager در `POST_EDGE_SQLITE_KEY` به session بدهید و runner را با
`-EncryptEdgeStorage` اجرا کنید. تست باید restart با همان کلید، رد کلید اشتباه،
ناخوانا بودن DB/WAL/SHM بدون کلید و backup/restore را ثابت کند؛ مقدار کلید در
command، log یا evidence ثبت نمی‌شود.

## تست SignalR

### روش پیشنهادی: Device Simulator

Simulator واقعی دو سناریوی `auth` و `full` دارد و token را از environment می‌گیرد تا در argument/history چاپ نشود:

```powershell
dotnet run --project .\tools\Post.DeviceSimulator -- --help
$env:POST_DEVICE_URL = 'http://127.0.0.1:7080/ws/device'
$env:POST_DEVICE_ID = 'manual-device'
$env:POST_DEVICE_TOKEN = Read-Host 'Local synthetic device token' -MaskInput
dotnet run --project .\tools\Post.DeviceSimulator -- --scenario auth
dotnet run --project .\tools\Post.DeviceSimulator -- --scenario full --timeout-seconds 60
Remove-Item Env:POST_DEVICE_TOKEN -ErrorAction SilentlyContinue
```

`full` به‌ترتیب Auth، RegisterInbound، AssignDestination، CloseBag و CloseDispatch را با contractهای `Post.Contracts` اجرا می‌کند و token را چاپ نمی‌کند. token بالا باید fixture غیرعملیاتی همان Mock باشد؛ برای Connected آن را فقط از secret store به process تزریق کنید.

### ترتیب session

1. WebSocket/SignalR connection را باز کنید.
2. `Auth` را با envelope `auth` و payload `deviceId/deviceToken` invoke کنید.
3. `status=0`، `sessionId` غیرخالی، `edgeServerTime` و correlation echo را assert کنید.
4. یک `RegisterInbound` با operation ID ثابت ارسال کنید.
5. `AssignDestination` را فقط پس از read موفق اجرا کنید.
6. `CloseBag` را با array filterها و count اطلاعاتی اجرا کنید.
7. `CloseDispatch` را برای همان destination اجرا کنید.
8. connection را ببندید و session cleanup را بررسی کنید.

### تست idempotency

- request یکسان را با همان `operationId` و correlation جدید دوباره بفرستید.
- response semantic باید همان باشد و row/bag/dispatch دوم ساخته نشود.
- همان operation ID با payload متفاوت باید validation/conflict امن بدهد.
- request بدون operation ID را دوباره با correlation ثابت تست کنید؛ response effectful باید fallback همان correlation را در `operationId` و `operationIdIsBestEffort=true` در payload بدهد. سپس retry با correlation جدید را به‌عنوان ریسک duplicate ثبت کنید؛ این مسیر فقط compatibility است.
- با bypass خاموش، device بدون binding باید رد شود؛ سپس binding را ثبت و auth را تکرار کنید. config sync نباید binding را حذف کند.

### تست status و protocol

| سناریو | انتظار |
| --- | --- |
| Auth معتبر | status `0` |
| Auth نامعتبر | status `2`، بدون session و سپس connection بسته |
| عملیات قبل از Auth | protocol/auth error، بدون side effect |
| version ناشناخته | protocol error |
| message type ناسازگار با method | protocol error |
| correlation یا operation GUID خالی | validation error |
| inbound mock pending barcode | status `1` و pending durable |
| inbound returned/refused fixture | status `3`/`4` |
| destination موفق/خطا | status `1`/`2` اختصاصی |
| bag بدون parcel | `NoEligibleParcels` |
| همه‌ی parcelها failed | `AllParcelsFailed` + errors |
| label dependency قطع | `LabelPending` با status `0`، فقط طبق policy |
| dispatch failure | `Error`؛ هرگز `Queued` |

### boundary تصویر و memory

- ۳۳ image، Base64 بیش از ۵٬۰۰۰٬۰۰۰ character یا decoded content بیش از ۳٬۷۵۰٬۰۰۰ byte باید `VALIDATION_ERROR` بگیرد.
- ۳۲ image که هرکدام در سقف per-image هستند از نظر V1 معتبرند؛ aggregate cap پایین‌تر مجاز نیست.
- Host پیش‌فرض تا ۱۹۲ MiB برای یک پیام و حداکثر پیکربندی ۲۵۶ MiB را می‌پذیرد تا ۱۶۰ میلیون character به‌علاوه‌ی JSON overhead جا شود.
- تست payload نزدیک سقف فقط روی agent اختصاصی با memory monitor، connection concurrency محدود و بدون logging body اجرا شود؛ این تست حدود صدها MiB allocation موقت ایجاد می‌کند و برای workstation مشترک یا Production مناسب نیست.
- هم‌زمان ۳۲-image request، reconnect storm و slow client را پس از تعیین NFR در load/soak تست کنید؛ rate limit اتصال جایگزین capacity planning نیست.

### بررسی raw wire در صورت نیاز

SignalR JSON protocol قبل از اولین frame یک handshake و جداکننده‌ی record `0x1e` دارد. packet capture فقط در محیط ایزوله و با token مصنوعی مجاز است. capture واقعی credential یا تصویر نباید ذخیره/commit شود.

در تست خطا دو boundary را جدا ثبت کنید: envelope قابل bind با version/type/field نادرست باید `protocol.error` canonical بدهد؛ JSON/argument غیرقابل bind یا Hub target ناشناخته در Phase 1 safe SignalR Completion/connection error می‌دهد. در هیچ‌کدام stack trace، token یا payload حساس نباید افشا شود؛ ادعای `MALFORMED_JSON` envelope در typed Hub فعلی نکنید.

## تست دستی REST Core

دو مجموعه‌ی آماده بدون secret در repository وجود دارد:

- [post-platform.http](api/post-platform.http) برای VS Code REST Client و Rider؛
- [Postman collection](api/post-platform.postman_collection.json) به‌همراه [environment نمونه‌ی خالی](api/post-platform.postman_environment.example.json).

در Postman، environment نمونه را import و با نام local کپی کنید، credentialها را فقط در Current/Secret values بگذارید و environment پرشده را export/commit نکنید. requestهای token در صورت موفقیت access token را فقط در environment فعال ذخیره می‌کنند.

requestهای «Provision Edge credential» و «Rotate Edge credential» به Administrator و `Idempotency-Key` نیاز دارند و `deviceSecret` را فقط در پاسخ اول می‌دهند. replay همان key باید `isReplay=true`, `recoveryRequired=true` و `deviceSecret=null` برگرداند؛ key یکسان با payload متفاوت باید `409` شود. آن‌ها را فقط در محیط ایزوله اجرا کنید، response logging/Postman Console و proxy capture را خاموش کنید، مقدار را بلافاصله در secret store مقصد ثبت و response tab را پاک کنید. collection عمداً secret پاسخ را خودکار در environment ذخیره نمی‌کند. برای تست صدور Edge token، secret را دستی فقط در Current value متغیر secret `edgeDeviceSecret` بگذارید؛ environment پرشده را export نکنید. پس از rotation، secret قبلی باید `401` و secret جدید token معتبر بگیرد. اگر مقدار جدید ضبط نشد، با key تازه دوباره rotate کنید؛ replay مقدار را بازیابی نمی‌کند.

`Disable Edge credential` نیز idempotency key مستقل می‌خواهد؛ اجرای اول و replay باید `204`، درخواست مدیر مرکز دیگر `403` و صدور token پس از disable باید `401` شود.

request «Rotate sorting-device secret» نیز با همان one-time/replay semantics اجرا می‌شود. پس از آن، bootstrap version تازه با verifier جدید را بررسی کنید، raw token نباید در snapshot باشد و احراز دستگاه با token قبلی باید رد شود.

### startup local

هر backend را روی port مورد انتظار Gateway در terminal جدا اجرا کنید. برای test ساده‌ی host می‌توان authentication را فقط در Development خاموش کرد:

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:Api__AuthenticationEnabled = 'false'
$env:Api__DevelopmentIdentity__Enabled = 'true'
$env:Api__DevelopmentIdentity__EdgeId = '33333333-3333-4333-8333-333333333333'
$env:Api__DevelopmentIdentity__ExchangeCenterCode = '12345'
$env:ASPNETCORE_URLS = 'http://127.0.0.1:7106'
dotnet run --project .\src\Core\Post.Core.EdgeHealth.Api
```

این principal مصنوعی فقط در Development/Testing و برای تست محلی است؛ routeهای Edge، `device_id` و center را از همین مقادیر می‌گیرند و routeهای admin با نقش scoped مرکز اجرا می‌شوند. Production auth-off را در startup رد می‌کند.

در Production-like test، auth را خاموش نکنید. یک token کوتاه‌عمر از Identity بگیرید و فقط در variable process نگه دارید:

```powershell
$headers = @{
  Authorization = "Bearer $env:POST_TEST_ACCESS_TOKEN"
  'X-Correlation-ID' = [guid]::NewGuid().ToString()
}
Invoke-RestMethod `
  -Uri 'https://gateway.test.invalid/api/admin/edges/health' `
  -Headers $headers
```

از commandهایی که token را literal در history می‌گذارند استفاده نکنید.

### smoke matrix

- `/health/live`, `/health/ready`, `/health` هر backend؛ `/health`, `/health/ready` در Gateway؛ و scrape آزمایشی `/metrics`؛
- root metadata؛
- Swagger فقط در محیط مجاز؛
- endpoint محافظت‌شده بدون token: `401`؛
- token معتبر با role اشتباه: `403`؛
- issuer/audience/expiry غلط: `401`؛
- request نامعتبر: `400` Problem Details با trace ID؛
- duplicate idempotency key/payload یکسان: replay؛
- key یکسان/payload متفاوت: `409`؛
- rate limit: `429` و `Retry-After` در صورت metadata؛
- allowed-center scope: داده‌ی مرکز غیرمجاز دیده نشود.

### نمونه heartbeat با داده‌ی مصنوعی

```powershell
$body = @{
  edgeId = '44444444-4444-4444-4444-444444444444'
  exchangeCenterCode = '12345'
  softwareVersion = 'manual-test'
  configurationVersion = 1
  pendingCount = 0
  failedCount = 0
  deadLetterCount = 0
  heartbeatAt = [DateTimeOffset]::UtcNow
} | ConvertTo-Json

Invoke-RestMethod `
  -Method Post `
  -Uri 'https://gateway.test.invalid/api/edge/heartbeat' `
  -Headers $headers `
  -ContentType 'application/json' `
  -Body $body
```

route و schema دقیق همان build را از Swagger بررسی کنید؛ هر اختلاف با artefactهای اجرایی این مخزن یک release blocker است.

## تست Connected بدون ثبت secret

Connected فقط روی dependencyهای test/staging یا endpoint دیگری که مالک سامانه صریحاً برای تست مجاز کرده است اجرا می‌شود.

username/password این بخش credential نام‌کاربری/گذرواژهٔ OAuth جست‌وجوی مقصد Iran Post است که با `grant_type=password` برای تبدیل بارکد ۱۴ رقمی به `ToCityID` مصرف می‌شود؛ OAuth Client Credentials grant نیست. فقط destination adapter در profileهای دارای Destination واقعی آن را مصرف و access token را cache می‌کند؛ آن را با device token، Core JWT، SQL connection credential یا SOAP credential جایگزین نکنید. چون archive ورودی مقدار plaintext داشته است، تست باید با credential rotate‌شده انجام شود.

### ورود credential در حافظه‌ی process

PowerShell credential prompt مقدار را در history نمی‌نویسد:

```powershell
$postCredential = Get-Credential -Message 'Sandbox postal API credential'
$env:Edge__DestinationApi__Username = $postCredential.UserName
$env:Edge__DestinationApi__Password = $postCredential.GetNetworkCredential().Password
$env:Edge__Integrations__Profile = 'Connected'
```

برای مقدارهای غیر username/password مانند connection string، از secret manager سازمان یا prompt secure استفاده کنید. مقدار decrypted فقط به process child داده شود و echo نشود. پس از test:

```powershell
Remove-Item Env:Edge__DestinationApi__Username -ErrorAction SilentlyContinue
Remove-Item Env:Edge__DestinationApi__Password -ErrorAction SilentlyContinue
Remove-Variable postCredential -ErrorAction SilentlyContinue
```

Environment variable در همان process plaintext است؛ برای workstation مشترک یا Production از vault/managed identity/secret file mount با ACL استفاده کنید.

روش پایدارتر برای workstation توسعه، .NET user-secrets همان Host است:

```powershell
$destinationUser = Read-Host 'Destination OAuth username'
$destinationPassword = Read-Host 'Destination OAuth password' -MaskInput
dotnet user-secrets set 'Edge:DestinationApi:Username' `
  $destinationUser `
  --project .\src\Edge\Post.Edge.Api
dotnet user-secrets set 'Edge:DestinationApi:Password' `
  $destinationPassword `
  --project .\src\Edge\Post.Edge.Api
Remove-Variable destinationUser, destinationPassword -ErrorAction SilentlyContinue
```

`Post.Edge.Api` از قبل `UserSecretsId` دارد. history در این روش فقط نام variableها را می‌بیند؛ مقدار هنگام اجرای CLI در حافظه/process argument حضور کوتاه دارد. مقدار را literal وارد نکنید. user-secrets encrypted vault نیست و فقط از commit شدن جلوگیری می‌کند؛ ACL workstation و rotation همچنان لازم است.

### contract checklist

| dependency | تست |
| --- | --- |
| Destination API | token acquisition/refresh skew، 401 refresh-once، timeout، safe error |
| Postal SQL inbound | parameter type/order، duplicate operation، commit unknown، permission denied |
| Postal SQL bag | array filter semantics، n/p/m/q، partial/all failure، barcode/label |
| Postal SQL dispatch | device/destination ownership، duplicate retry، problem parcels |
| Postal SOAP | WSDL namespace/action، valid/fault/timeout/malformed response |
| Core | JWT renewal، config ETag/version، event ack، outage/recovery |
| S3 | signature/expiry/header/digest، upload و metadata link |

تا دریافت WSDL رسمی، SQL schema/SP و label/check-digit/reference fixtures، نتیجه‌ی این بخش sign-off نهایی نیست.

## تست resilience

1. Core را بعد از یک inbound موفق قطع کنید؛ Edge باید state را حفظ و outbox را retry کند.
2. Edge را هنگام backlog restart کنید؛ lockهای منقضی recover و duplicate ایجاد نشود.
3. dependency را تا عبور از max attempts قطع کنید؛ dead-letter و alert را بررسی کنید.
4. پس از رفع علت فقط یک پیام را requeue کنید؛ audit و نتیجه را بررسی کنید.
5. فضای disk را در یک volume آزمایشی محدود کنید؛ پذیرش جدید باید fail-safe و health unhealthy شود.
6. clock skew کنترل‌شده اعمال کنید؛ token expiry/pre-sign/heartbeat را بررسی کنید.
7. دو CloseBag همزمان برای یک destination بفرستید؛ lock و انتخاب یکتا را assert کنید.
8. cancellation client را وسط call اعمال کنید؛ state نیمه‌commit نباشد.

هیچ fault injection روی production یا SQL واقعی بدون change ticket مجاز نیست.

## تست migration و recovery

- database نسخه‌ی قبلی را از fixture بسازید؛
- migration up را اجرا و schema/data را assert کنید؛
- binary جدید را smoke-test کنید؛
- rollback binary روی schema جدید را برای compatibility تست کنید؛
- backup را روی host جدا restore و integrity/read/outbox counts را بررسی کنید؛
- migration destructive فقط با copy و rollback/restore rehearsal پذیرفته می‌شود.

## پذیرش نهایی

- همه‌ی test projectها سبز و بدون skip غیرمصوب؛
- analyzer/format/diagram/secret gates سبز؛
- requirement traceability بدون ردیف حیاتی `Untested`؛
- Mock end-to-end و Connected sandbox contract test ثبت‌شده؛
- restart/outage/replay/dead-letter/restore تست‌شده؛
- هیچ secret یا private source content در evidence؛
- NFRهای باز قبل از performance sign-off مصوب؛
- گزارش نهایی طبق [test-results/README.md](test-results/README.md) منتشر شده باشد.
