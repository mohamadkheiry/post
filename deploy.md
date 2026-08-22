# راهنمای استقرار و بهره‌برداری

این سند مسیر استقرار قابل تکرار برای Edge و Core را تعریف می‌کند. commandها نمونه‌اند و هیچ secret عملیاتی در repository قرار نمی‌گیرد. نام hostها و portهای local با `src/Core/Post.Core.Gateway/appsettings.json` هماهنگ است.

## پیش‌نیازها

### مشترک

- .NET SDK مطابق `global.json` برای build؛ ASP.NET Core Runtime همان major/minor برای اجرای framework-dependent publish.
- PowerShell 7 برای scriptهای repository.
- Node.js/npm فقط برای اجرای markdownlint نسخه‌پین‌شده با `npx`؛ dependency پروژه‌ی runtime نیست.
- Docker Engine/Desktop با Compose V2 برای topology local containerized.
- ساعت سیستم همگام با NTP؛ تمام timestampها UTC هستند.
- TLS certificate معتبر و DNS داخلی در محیط‌های غیرتوسعه‌ای.
- حساب سرویس با کمترین سطح دسترسی و directory قابل نوشتن فقط برای state موردنیاز.

### Edge

- یک host پایدار نزدیک خط تفکیک؛ Phase 1 به‌صورت single active node طراحی شده است.
- storage محلی پایدار با فضای کافی برای SQLite، WAL، outbox، dead-letter و logهای محلی.
- در Connected: egress کنترل‌شده به Core، API مقصد، SQL پستی و SOAP پستی.
- backup agent سازگار با SQLite online backup یا snapshot هماهنگ‌شده.

### Core

- SQL Server برای پنج database منطقی: ParcelLifecycle، DeviceConfig، EdgeHealth، OperationalResult و Identity.
- object storage سازگار با S3 برای تصویرها/ضمائم.
- broker سازگار با Kafka در صورتی که durable publisher production فعال است.
- Redis برای read-model/idempotency mirror قابل بازسازی و SQL مشترک برای idempotency چند replica. `Inbox`/`IdempotencyRecord` و unique constraint همیشه منبع حقیقت‌اند؛ cache hit/miss/stale/down نباید نتیجهٔ business را تغییر دهد.
- reverse proxy/load balancer در جلوی Gateway و network policy بین Gateway و backendها.

## ماتریس محیط

| محیط | Edge integration | persistence | authentication | dependency بیرونی | هدف |
| --- | --- | --- | --- | --- | --- |
| Unit test | fake/in-memory | SQLite test/in-memory | test issuer | ندارد | تست سریع و deterministic |
| Local Mock | `Mock` | SQLite file | auth-off + identity مصنوعی | ندارد | توسعه‌ی end-to-end |
| Local hybrid | `LocalConnected` | SQLite file | auth-off + identity مصنوعی | Destination مجاز؛ Postal/Core mock | تست adapter مقصد |
| Full development | Edge→Core واقعی | SQLite + پنج DB روی SQL Server | profile امن با auth فعال؛ profile تشخیصی auth-off جدا | Redis، Redpanda، MinIO | E2E واقعی/recovery/load |
| Local Connected | `Connected` | SQLite + SQL Server dev | JWT فعال | dependencyهای test/staging مجاز | contract/integration test |
| Staging | `Connected` | volume + SQL Server staging | JWT/TLS فعال | staging dependencies | soak/UAT/migration rehearsal |
| Production | `Connected` | managed volumes + HA SQL | JWT/TLS/rotation | production dependencies | عملیات واقعی |

غیرفعال‌کردن authentication فقط در `Development` یا `Testing` مجاز است و hostها در محیط دیگر fail-fast می‌شوند.

## configuration و secretها

configuration با ترتیب استاندارد ASP.NET Core خوانده می‌شود. در environment variable، جداکننده‌ی section دو underscore است؛ برای مثال `Auth__Issuer`.

### کلیدهای عمومی Core

| کلید | حساس | توضیح |
| --- | --- | --- |
| `Api:AuthenticationEnabled` | خیر | فقط در Development/Testing می‌تواند `false` باشد |
| `Api:DevelopmentIdentity:Enabled` | خیر | فقط همراه auth-off محلی؛ principal مصنوعی صریح را فعال می‌کند |
| `Api:DevelopmentIdentity:EdgeId`, `ExchangeCenterCode` | خیر | در حالت بالا الزامی؛ خالی/نامعتبر startup را fail می‌کند |
| `Api:SwaggerEnabled` | خیر | Swagger در Production فقط با opt-in |
| `Api:MetricsEnabled` | خیر | Prometheus endpoint؛ دسترسی شبکه محدود شود |
| `Auth:Issuer`, `Auth:Audience` | خیر | validation در API/Gateway |
| `Auth:SigningKey` | بله | حداقل ۳۲ بایت؛ از secret manager |
| `Core:Jwt:*` | بخشی | fallback legacy صادرکننده؛ برای config جدید `Auth:*` ترجیح دارد |
| `Core:Database:Provider` | خیر | `Sqlite` فقط Development/Testing؛ Production برابر SQL Server |
| `Core:Database:Directory` | خیر | directory فایل‌های SQLite توسعه/تست |
| `Core:Database:ApplyMigrations` | خیر | Production پیش‌فرض `true`؛ job کنترل‌شده بر startup چند replica ترجیح دارد |
| `Core:Outbox:Enabled` | خیر | برای profileهای stateful؛ DataIngestion/ObjectStorage outbox ندارند |
| `Core:Outbox:PollingSeconds`, `BatchSize`, `MaximumAttempts` | خیر | retry/worker bounds؛ با ظرفیت و SLO تنظیم شود |
| `Core:PendingAttachments:*` | خیر | polling/retry/age/batch و topic مربوط به reconciliation تصویر |
| `Core:EdgeHealthMonitor:CheckIntervalSeconds`, `StaleThresholdSeconds` | خیر | sweep دوره‌ای و آستانه‌ی Offline شدن Edge |
| `ConnectionStrings:ParcelLifecycle` | بله | SQL Server |
| `ConnectionStrings:DeviceConfig` | بله | SQL Server |
| `ConnectionStrings:EdgeHealth` | بله | SQL Server |
| `ConnectionStrings:OperationalResult` | بله | SQL Server |
| `ConnectionStrings:Identity` | بله | SQL Server |
| `Core:S3:Endpoint`, `Region`, `Bucket` | خیر | object storage target؛ Endpoint در Production الزاماً HTTPS است |
| `Core:S3:AccessKey`, `SecretKey` | بله | credential کوتاه‌دسترسی |
| `Redis:Enabled` | خیر | mirror قابل بازسازی را فعال می‌کند؛ SQL همچنان authority است |
| `Redis:ConnectionString` | بله | endpoint و ACL username/password؛ در Production با TLS و secret provider و بدون کاربر anonymous |
| `Redis:RequireTls`, `InstancePrefix` | خیر | TLS تولیدی و namespace مستقل deployment |
| `Redis:EntryTtl`, `CleanupInterval`, `CleanupBatchSize` | خیر | TTL فقط cache را پاک می‌کند و هرگز SQL idempotency record را حذف نمی‌کند |
| `Messaging:Kafka:*` | بخشی | bootstrap/security/topic؛ producer با idempotence و `acks=all` |

پیش‌فرض‌های ایمنی pending attachment عبارت‌اند از polling برابر ۳۰ ثانیه، retry پایه ۵ ثانیه، retry حداکثر ۳۰۰ ثانیه، ۱۰ تلاش، حداکثر سن ۲۴ ساعت، batch برابر ۵۰ و topic برابر `image.metadata.pending.dlq`. رکورد dead-letter حذف خودکار نمی‌شود. این مقادیر policy موقت عملیاتی‌اند، نه SLA یا retention/legal-hold مصوب؛ تغییر یا پاک‌سازی آن‌ها به تأیید مالک داده نیاز دارد. health monitor نیز به‌صورت پیش‌فرض هر ۳۰ ثانیه بررسی و Edge بدون heartbeat را پس از ۳۰۰ ثانیه Offline می‌کند؛ این دو مقدار هم SLO availability نیستند.

Signing key در issuer و تمام validatorها باید یک secret version هماهنگ باشد. rotation باید با overlap دو key یا restart هماهنگ انجام شود؛ hot swap تک‌کلیدی بدون overlap tokenهای صادرشده را invalid می‌کند.

credential ارتباط Edge → Core از `POST /api/admin/edges` و با Administrator scoped provision می‌شود. rotation با method/path دقیق `POST /api/admin/edges/{deviceId}/rotate-secret` انجام می‌شود؛ هر قصد جدید به `Idempotency-Key` تازه نیاز دارد. secret خام provisioning فقط در پاسخ اول `201` و secret جایگزین rotation فقط در پاسخ اول `200` دیده می‌شود؛ replay همان key پاسخی با `deviceSecret=null` و `recoveryRequired=true` می‌دهد. مقدار را مستقیم در secret manager Edge بنویسید و هیچ فایل واسط، log یا ticket نسازید. Core فقط verifier و receipt بدون secret نگه می‌دارد. در افشا/فقدان، برای containment از `POST /api/admin/edges/{deviceId}/disable` با idempotency key تازه و برای replacement از rotate با change record و key جدید استفاده کنید.

disable یا rotate کردن credential، صدور token جدید را متوقف می‌کند اما JWT بدون state که قبلاً صادر شده تا پایان عمر خودش معتبر می‌ماند؛ سقف فعلی Edge token برابر ۹۰۰ ثانیه است. containment فوری علاوه بر disable به قطع session/مسیر شبکه یا rotation هماهنگ signing key نیاز دارد؛ rotation signing key همه‌ی tokenهای وابسته را تحت‌تأثیر قرار می‌دهد و باید incident procedure داشته باشد.

token دستگاه sorting نیز در registration فقط یک‌بار دیده می‌شود و با `POST /api/admin/devices/{deviceId}/rotate-secret` و `Idempotency-Key` تازه rotate می‌شود. پاسخ اول باید مستقیم در secret store provisioning دستگاه ثبت شود؛ replay token را بازنمی‌گرداند.

### کلیدهای Edge

| section | کلیدهای اصلی | نکته |
| --- | --- | --- |
| `Edge:Integrations` | `Profile` با مقدار Mock، LocalConnected، Connected یا Custom و component overrides | `Mode`/`Real` فقط alias سازگاری؛ Connected همه‌ی adapterها را Real می‌کند |
| `Edge:Storage` | connection/migration، encryption، backup/restore و نام env کلید جاری/قبلی | DB/WAL/SHM و backup با کلید بیرونی؛ shared cache ممنوع |
| `Edge:LocalAdmin` | enable، نام bootstrap، session lifetime و rate/body bounds | password اولیه فقط از `ADMIN_INITIAL_PASSWORD`/secret provider؛ verifier PBKDF2 در DB |
| `Edge:TransportTls` | `RequireTls` | Production همیشه TLS و certificate معتبر می‌خواهد؛ Device باید با WSS متصل شود |
| `Edge:Resilience` | retry/timeout/circuit bounds برای HTTP و SQL | retry فقط برای operationهای safe/idempotent؛ commit-unknown دستی reconcile می‌شود |
| `Edge:AutoDispatch` | enable، scan interval، age/count و allow-listها | پیش‌فرض خاموش؛ policy از Core همگام و محدود می‌شود |
| `Edge:Workers` | batch/retry/poll، `CleanupEnabled`, `CleanupInterval`, `RetentionPeriod`, `IdempotencyRetentionPeriod` | cleanup پیش‌فرض خاموش است؛ windowها policy حقوقی نیستند |
| `Edge:Security` | `AllowUnboundDevices` | پیش‌فرض `false`؛ فقط local Mock می‌تواند صریح `true` باشد |
| `Edge:DeviceTransport` | receive bytes، connection rate/window، auth deadline و close delay | receive پیش‌فرض ۱۹۲ MiB و hard max ۲۵۶ MiB؛ برای قرارداد ۳۲×۵M کم نشود |
| `Edge:Mock` | center، deviceها، mapها و failure switches | فقط non-production |
| `Edge:DestinationApi` | base/path، username/password، token lifetime | username/password حساس‌اند |
| `Edge:CoreApi` | base، routeهای inbound/parcel/bag/dispatch/event/result/image/config و bearer | bearer حساس است |
| `Edge:PostalSql` | connection، شناسه‌ها و poll interval | connection حساس است |
| `Edge:PostalSoap` | endpoint و SOAP action | endpoint ممکن است داخلی باشد |

فایل `.env.example` فقط inventory نام secretهاست. مقداردهی local را در user-secrets، process environment یا vault انجام دهید؛ `.env` و `appsettings.Local.json` عمداً ignore و forbidden هستند.

رمزنگاری Edge با `EncryptionEnabled=true` فعال و کلید جاری از environment variable
نام‌گذاری‌شده در `EncryptionKeyEnvironmentVariable` (پیش‌فرض
`POST_EDGE_SQLITE_KEY`) خوانده می‌شود. مقدار ۳۲ تا ۲۵۶ character باید در secret
manager بیرون repository پایدار بماند؛ connection string حق حمل password ندارد.
برای rotation، کلید قبلی را موقتاً در `POST_EDGE_SQLITE_PREVIOUS_KEY` و کلید جدید
را در متغیر جاری بگذارید؛ startup پس از checkpoint WAL، rekey و بازخوانی با کلید
جدید انجام می‌شود. پس از backup و restart موفق، کلید قبلی را حذف کنید. DB، WAL،
SHM و backup باید یک مجموعهٔ رمزگذاری/بازیابی تلقی شوند و هیچ مقدار کلید در log،
state file یا command line نوشته نمی‌شود.
جزئیات invariantهای provider واحد، fail-closed، rekey و backup در
[ADR-0009](docs/architecture/adr/0009-edge-sqlite-encryption-and-external-keys.md)
ثبت شده است.

fallback کد در غیاب key، `Profile=LocalConnected` است؛ `appsettings.json` متعهد و Compose برای safety local مقدار `Mock` را صریح دارند. offline development/CI نیز باید `Mock` را صریح نگه دارند. در `Custom` سه کلید `Destination`, `Postal`, `Core` باید جداگانه Mock/Real باشند. وقتی Destination واقعی است، readiness check با نام `edge_destination_oauth` و tagهای `ready/external/destination` credential/token endpoint را می‌سنجد.

receive بزرگ Device برای پشتیبانی قرارداد ۳۲ تصویر با سقف پنج میلیون Base64 character برای هر تصویر است. این ظرفیت به معنی مجوز concurrency نامحدود نیست: rate limit اتصال، یک invocation همزمان برای هر client، auth deadline و memory/container limits را حفظ کنید. NFR عددی هنوز باز است؛ پیش از Production با payload واقعی load/soak کنید و کاهش سقف wire را فقط با contract version جدید انجام دهید.

کف برنامه‌ریزی pilot دو مرسوله/ثانیه است، نه SLO کامل. هدف چندصد میلی‌ثانیه‌ای تعامل sorting و هدف زیر یک میلی‌ثانیه‌ای read-model Redis را جدا اندازه بگیرید؛ هیچ SLA انتهابه‌انتها ۲ ms تعریف نشده است. rollout به percentile، burst، concurrency، payload mix و soak target مصوب نیاز دارد.

کلیدهای دقیق transport عبارت‌اند از `Edge:DeviceTransport:MaximumReceiveMessageSizeBytes` (پیش‌فرض `201326592` با validation حداکثر `268435456`)، `ConnectionPermitLimit` (۲۰)، `ConnectionRateWindow` (یک دقیقه)، `AuthenticationDeadline` (۱۰ ثانیه) و `FailedAuthenticationCloseDelay` (۱۰۰ میلی‌ثانیه). افزایش hard max بدون review امنیت/capacity مجاز نیست.

`Edge:DestinationApi:Username/Password` دقیقاً credential نام‌کاربری/گذرواژهٔ OAuth سرویس جست‌وجوی مقصد Iran Post است که با `grant_type=password` بارکد ۱۴ رقمی را به `ToCityID` نگاشت می‌کند؛ این مقدار OAuth Client Credentials grant نیست. فقط Edge destination adapter در `LocalConnected`/`Connected` یا component واقعی `Custom` آن را مصرف و access token را تا refresh skew در حافظه cache می‌کند. این مقادیر device token، bearer/JWT Core، SQL credential یا SOAP credential نیستند. برای local با .NET user-secrets و برای staging/production با secret manager تزریق شوند. آرشیو اولیه یک نسخه‌ی plaintext را آشکار کرده بود؛ پس از validation آن credential را revoke/rotate کنید و مقدار قبلی را هرگز به config یا history منتقل نکنید.

## build و publish محلی

```powershell
dotnet restore .\Post.sln
dotnet build .\Post.sln -c Release --no-restore
.\scripts\run-tests.ps1 -Configuration Release -NoRestore
.\scripts\verify-migrations.ps1 -Configuration Release -NoBuild
.\scripts\publish-local.ps1 -Configuration Release
```

خروجی ۱۰ deployable در `artifacts/publish/<ProjectName>/` ساخته می‌شود. برای SDK خارج از `PATH` می‌توان فقط در همان session مقدار `POST_DOTNET` را به executable معتبر داد.

پس از publish، مسیر تک‌فرمانی پیشنهادی برای local smoke این است:

```powershell
.\scripts\start-published-local.ps1 -EdgeProfile Mock
Invoke-RestMethod http://127.0.0.1:7088/health/ready
Invoke-RestMethod http://127.0.0.1:7080/health/ready
.\scripts\stop-published-local.ps1
```

start script هفت Core backend، Gateway و Edge را به‌صورت process مخفی و loopback-only با portهای `7101..7107`, `7088` و `7080` اجرا می‌کند. این profile فقط Development است، auth را خاموش می‌کند، SQLite و logها را زیر `artifacts/local-runtime/` ایزوله می‌کند و فقط پس از سبزشدن readiness هر نه host برمی‌گردد. فایل state در `artifacts/local-runtime/processes.json` است؛ instance دوم تا پیش از stop رد می‌شود.

برای اجرای همان artifact با SQLite رمز‌شده، کلید پایدار را بدون قرار دادن در
command history از secret manager به environment همان session تزریق کنید:

```powershell
$env:POST_EDGE_SQLITE_KEY = Read-Host 'Stable local Edge storage key' -MaskInput
.\scripts\start-published-local.ps1 -EdgeProfile Mock -EncryptEdgeStorage
Invoke-RestMethod http://127.0.0.1:7080/health/ready
.\scripts\stop-published-local.ps1
Remove-Item Env:POST_EDGE_SQLITE_KEY
```

همان کلید برای restart همان `RuntimeRoot` لازم است؛ key موقت و فراموش‌شده یعنی
database عمداً قابل بازیابی نیست. runner مقدار را فقط به process Edge forward
می‌کند و آن را در `processes.json` یا log نمی‌نویسد. برای rotation، متغیر قبلی
را فقط در یک startup کنترل‌شده نیز تزریق و پس از backup/restart موفق حذف کنید.

`-EdgeProfile LocalConnected` فقط در برابر endpointی مجاز است که مالک سامانه صریحاً برای این تست اجازه داده است. پیش از آن، credential نام‌کاربری/گذرواژهٔ OAuth مقصد را با user-secrets پروژهٔ Edge فراهم کنید؛ readiness به token endpoint واقعی دسترسی می‌زند و در نبود credential/network باید fail شود. `stop-published-local.ps1` پیش از توقف، PID، start time و command DLL را تطبیق می‌دهد تا process نامرتبط متوقف نشود. database و log را عمداً نگه می‌دارد؛ پاک‌سازی احتمالی فقط پس از بررسی target دقیق و پایان processها انجام شود.

اجرای یک artifact framework-dependent:

```powershell
$env:ASPNETCORE_URLS = 'http://127.0.0.1:7101'
dotnet .\artifacts\publish\Post.Core.Identity.Api\Post.Core.Identity.Api.dll
```

## ترتیب راه‌اندازی Core

1. secretها، connection stringها و دسترسی S3/broker را mount یا inject کنید.
2. backup قابل بازیابی از databaseهای فعلی بگیرید.
3. migrationها را با job یک‌بارمصرف اجرا کنید؛ API replicaها نباید همزمان schema را تغییر دهند.
4. backendها را روی network داخلی بالا بیاورید.
5. `/health/ready` هر backend را بررسی کنید.
6. Gateway را بالا بیاورید و routing/auth/rate-limit smoke test را اجرا کنید.
7. یک Edge staging را register و heartbeat/config sync را تأیید کنید.
8. canary را گسترش دهید و queue/dead-letter/error rate را زیر نظر بگیرید.

portهای پیش‌فرض Gateway local:

| service | port |
| --- | ---: |
| Identity | 7101 |
| DataIngestion | 7102 |
| ParcelLifecycle | 7103 |
| DeviceConfig | 7104 |
| ObjectStorage | 7105 |
| EdgeHealth | 7106 |
| OperationalResult | 7107 |

Gateway خودش می‌تواند با `ASPNETCORE_URLS` روی port دلخواه اجرا شود. در Production آدرس clusterهای YARP باید به service discovery داخلی اشاره کنند، نه `localhost`.

## ترتیب راه‌اندازی Edge

1. از `edge.db` و فایل‌های WAL/SHM سازگار backup بگیرید یا service را برای file-copy متوقف کنید.
2. artifact جدید و configuration غیرحساس را کنار نسخه‌ی قبل stage کنید.
3. secretها را از vault/process environment inject کنید.
4. database path و دسترسی write/rename/fsync را بررسی کنید.
5. Edge را ابتدا در `Mock` smoke-test و سپس در Connected sandbox contract-test کنید.
6. migration را اجرا کنید و service را بالا بیاورید.
7. health، authentication یک device آزمایشی، یک inbound و backlog را بررسی کنید.
8. فقط پس از sign-off، دستگاه‌های خط را به endpoint جدید هدایت کنید.

پیش از اتصال دستگاه واقعی، IP binding آن را از endpoint مدیریتی local ثبت کنید. config sync registry/hash را به‌روزرسانی می‌کند اما binding محلی را پاک نمی‌کند. `AllowUnboundDevices=true` در staging/production ممنوع است.

مسیر Device → Edge باید مستقیم یا در لایه‌ی شبکه source-preserving باشد تا `RemoteIpAddress` همان IP واقعی دستگاه بماند. NAT یا reverse proxy/LB معمولی که IP مبدأ را جایگزین کند برای این trust boundary پشتیبانی نمی‌شود؛ Edge عمداً به `X-Forwarded-For` اعتماد نمی‌کند. TLS را روی خود Edge یا یک مسیر L4 شفاف و تأییدشده terminate کنید و پیش از rollout، IP مشاهده‌شده را با binding ثبت‌شده تطبیق دهید.

Edge startup در حالت migration، EF migrationها را اعمال و سپس `journal_mode=WAL`، foreign key و busy timeout را فعال می‌کند. اجرای دو instance روی همان SQLite file پشتیبانی‌شده نیست.

## migrationها

### Edge SQLite

design-time factory در پروژه‌ی Infrastructure وجود دارد. migration جدید باید همراه test و rollback rehearsal ساخته شود:

```powershell
dotnet tool restore
dotnet ef migrations add DescriptiveName `
  --project .\src\Edge\Post.Edge.Infrastructure `
  --startup-project .\src\Edge\Post.Edge.Api `
  --context EdgeDbContext
dotnet ef migrations script `
  --project .\src\Edge\Post.Edge.Infrastructure `
  --startup-project .\src\Edge\Post.Edge.Api `
  --context EdgeDbContext `
  --output .\artifacts\migrations\edge.sql
```

provider SQLite تولید script با `--idempotent` را پشتیبانی نمی‌کند؛ migrationها را به ترتیب chain تولید کنید و script خروجی را روی copy واقعی database با backup و rollback rehearsal تست کنید.

### Core SQL Server

Core پنج DbContext و migration chain مستقل دارد. هر host فقط connection string context مالک خود را می‌خواهد. Production initializer فقط `MigrateAsync` و Testing فقط `EnsureCreated` اجرا می‌کند؛ `EnsureCreated` هرگز جایگزین migration production نیست.

```powershell
dotnet ef migrations script `
  --project .\src\Core\Post.Core.Infrastructure `
  --startup-project .\src\Core\Post.Core.ParcelLifecycle.Api `
  --context ParcelLifecycleDbContext `
  --idempotent `
  --output .\artifacts\migrations\parcel-lifecycle.sql
```

برای DeviceConfig، EdgeHealth، OperationalResult و Identity نیز script جدا بسازید. repository برای هر پنج context chain واقعی دارد؛ migrationهای follow-up مربوط به idempotency provisioning/rotation در DeviceConfig و Identity و idempotency event در EdgeHealth نیز جزو همان chain مالک‌اند. تا review کل script و rehearsal همان migrationها روی staging مقصد، rollout Core **Blocked** است.

## Docker Compose

repository دو topology جدا دارد:

1. `compose.yaml`: smoke سبک **Development/Mock** با Core SQLite، auth خاموش و
   fixture مصنوعی؛
2. `compose.yaml + compose.full.yaml`: توسعهٔ متصل **auth-on** با SQL Server
   برای پنج DB، Redis، Redpanda، MinIO، migration/health gate و Edge→Core واقعی.

هیچ‌کدام manifest تولیدی نیستند. full-development برای اثبات integration،
recovery و امنیت روی workstation ایزوله است؛ Destination/Postal آن Mock می‌مانند
تا credential/قرارداد سازمانی بدون مجوز مصرف نشود.

### Mock سبک

```powershell
docker compose version
docker compose -p post-platform-mock -f compose.yaml config --quiet
docker compose -p post-platform-mock -f compose.yaml up --detach --build
Invoke-RestMethod http://127.0.0.1:7088/health
Invoke-RestMethod http://127.0.0.1:7080/health
docker compose -p post-platform-mock -f compose.yaml ps
```

این mode فقط loopback است؛ Core/Edge auth، local-admin و IP binding برای fixture
مصنوعی صریحاً bypass می‌شوند و معیار پذیرش امنیت یا Connected نیستند.

### full-development واقعی

حداقل Docker Compose V2، حدود ۸ GiB حافظه برای topology پایه و حدود ۱۲ GiB
برای profile ترتیبی HA/observability در نظر بگیرید. ابتدا model و image lockها را
بدون اجرای سرویس validate کنید:

```powershell
docker compose -p post-platform-final -f compose.yaml -f compose.full.yaml config --quiet
.\scripts\infra\verify-image-locks.ps1
```

تمام imageهای بیرونی و دو base image دات‌نت هم tag نسخه‌دار و هم digest manifest
دارند؛ mismatch tag/digest gate را fail می‌کند. سپس gate یک‌فرمانی را اجرا کنید:

```powershell
$env:POST_DOTNET = (Get-Command dotnet).Source
.\scripts\infra\test-full-dev.ps1 `
  -ResetData `
  -IncludeObservability `
  -IncludeHa `
  -RequireCleanRevision `
  -KeepRunning
```

`-ResetData` فقط project صریح `post-platform-final` را پس از validation نام/label
با volumeهای synthetic آن reset می‌کند و secretهای تصادفی تازه زیر مسیر ignored
`.secrets/full-dev/` می‌سازد. روی محیط حاوی دادهٔ لازم از این switch استفاده
نکنید. بدون آن، state موجود حفظ و credential یک‌بارهٔ Edge/device به شکل امن
rotate می‌شود. مقدار هیچ password، token، signing key، connection string یا
pre-signed URL چاپ نمی‌شود.

runner به‌ترتیب این موارد را gate می‌کند:

- SQL/Redis/Redpanda/MinIO، پنج migration و readiness تمام Core hostها؛
- auth واقعی Admin، provision یک‌بارهٔ Edge، JWT با center scope و config sync؛
- fail-closed بودن Edge پیش از provisioning، سپس SignalR simulator کامل؛
- persistence مرسوله/تصویر/کیسه/دیسپچ در SQL و مسیر واقعی Kafka/S3؛
- Redis hit/stale/outage/rebuild و idempotency مشترک بین دو replica با authority
  نهایی SQL؛
- امضای PUT و رد header حذف‌شده/دستکاری‌شده، SSE-S3 و اتصال metadata تصویر؛
- restart/redelivery Kafka، duplicate بدون اثر دوم، retry محدود و poison→DLQ؛
- TDE هر پنج DB، backup/restore/failover profile محلی و restart persistence؛
- baseline حداقل دو مرسوله/ثانیه با report latency/error/resource بدون ادعای SLO؛
- scrape واقعی Prometheus، query correlation در Loki و dashboard provisionشده؛
- نبود secretهای synthetic در filesystem/history/config نهایی imageها.

`-KeepRunning` برای بررسی دستی stack سبز را نگه می‌دارد. بدون آن، runner در
`finally` فقط containerهای همین project را پایین می‌آورد و volumeها را نگه
می‌دارد. برای اجرای کوتاه‌تر می‌توان `IncludeHa` یا `IncludeObservability` را حذف
کرد، اما چنین اجرايی جای evidence نهایی full gate نیست. `SkipBuild` فقط وقتی
مجاز است که imageهای همان source revision قبلاً ساخته و lock شده باشند.
`-RequireCleanRevision` اجرای release/evidence را به یک HEAD چهل‌رقمی و working
tree تمیز مقید می‌کند؛ فقط برای عیب‌یابی پیش از commit آن را حذف کنید.

### endpoint، profile و health

فقط دو port روی loopback host منتشر می‌شوند:

| component | host URL | container port |
| --- | --- | ---: |
| Core Gateway | `http://127.0.0.1:7088` | 8080 |
| Edge API/SignalR | `http://127.0.0.1:7080` | 8080 |

هفت backend، SQL، Redis، broker و object storage port عمومی ندارند. network
full-development داخلی است. profileهای اختیاری:

| profile | سرویس‌ها | هدف |
| --- | --- | --- |
| `connected-edge` | Edge با Core واقعی و SQLite رمز‌شده | E2E device→Edge→Core |
| `observability` | Prometheus، Loki، Alloy، Grafana | metric/log/dashboard gate |
| `ha` | SQL secondary و AG configurator | TDE restore/sync/failover محلی |
| `tools` | HTTP client محدود | عیب‌یابی داخل network |

```powershell
Invoke-RestMethod http://127.0.0.1:7088/health/ready
Invoke-RestMethod http://127.0.0.1:7080/health/ready
docker compose -p post-platform-final -f compose.yaml -f compose.full.yaml ps
docker compose -p post-platform-final -f compose.yaml -f compose.full.yaml logs --tail 200 gateway edge
```

log را قبل از share redaction کنید. access token، payload، pre-signed URL و
secret نباید برای عیب‌یابی چاپ شوند.

### state، encryption و volumeها

| volume | مالک/محتوا |
| --- | --- |
| `sql-primary-data` | پنج DB authoritative و TDE key metadata |
| `sql-secondary-data` | replica profile محلی HA |
| `sql-ag-share` | certificate/backupهای synthetic برای TDE/AG rehearsal |
| `redpanda-data` | topic، offset و DLQ واقعی |
| `minio-data` | objectهای image با bucket-default SSE-S3 |
| `edge-data` | Edge SQLite، WAL، SHM و backup رمز‌شده |
| `prometheus-data`, `loki-data`, `alloy-data`, `grafana-data` | observability محلی |

volumeهای Core SQLite در base Mock وجود دارند؛ در overlay full، پنج SQL DB مرجع
هستند. Redis عمداً persistence ندارد چون mirror قابل بازسازی است و حذف/TTL آن
نباید idempotency recordهای SQL را پاک کند. SQL Developer، MinIO، Redpanda، Redis
و Grafana stack فقط انتخاب توسعه‌ای‌اند؛ license و vendor/topology Production باید
جداگانه تأیید شود.

Edge full با `POST_EDGE_SQLITE_KEY` synthetic بیرونی رمز می‌شود؛ DB/WAL/SHM و
backup بدون کلید قابل خواندن نیستند. MinIO bucket، encryption SSE-S3 را require
می‌کند و SQL configurator هر پنج DB را TDE می‌کند. فایل‌های `.secrets/full-dev`
در build context، Git و evidence ممنوع‌اند.

cleanup خودکار Edge با `Edge:Workers:CleanupEnabled=false` پیش‌فرض خاموش است. اگر
اپراتور آن را پس از approval فعال کند، defaultهای فنی `CleanupInterval=1h`،
`RetentionPeriod=30d` و `IdempotencyRetentionPeriod=90d` فقط safety window قابل
تنظیم‌اند: سرویس تنها chainهای کاملاً تأییدشده، outbox پردازش‌شده و رکوردهای
terminal امن را prune می‌کند و pending فعال یا dead-letter را حذف نمی‌کند. این
اعداد retention/legal-hold کسب‌وکار نیستند.

### توقف بدون حذف داده

```powershell
docker compose -p post-platform-mock -f compose.yaml down --remove-orphans

docker compose -p post-platform-final -f compose.yaml -f compose.full.yaml `
  --profile connected-edge --profile observability --profile ha `
  down --remove-orphans
```

`down` بدون `--volumes` state را نگه می‌دارد. حذف volume فقط برای محیط synthetic
و disposable از طریق `test-full-dev.ps1 -ResetData` و پس از backup/حل scope دقیق
مجاز است؛ دستور حذف volume عمومی در runbook روزمره وجود ندارد.

### مرز Production

- app imageها non-root، read-only، `no-new-privileges` و بدون capability هستند؛
- full-development از HTTP/TrustServerCertificate فقط داخل network داخلی توسعه
  استفاده می‌کند؛ Production به TLS/certificate/KMS/secret provider واقعی نیاز دارد؛
- SQL Server Developer و topology تک-host/دو-container مجوز یا HA تولیدی نیست؛
- Alloy برای discovery محلی Docker socket را read-only می‌گیرد که هنوز privileged
  است و نباید عیناً به Production منتقل شود؛
- migration تولیدی job جدا، lock، backup و script reviewشده می‌خواهد؛ API replicaها
  نباید هم‌زمان migration اجرا کنند؛
- فقط Gateway/Edge از ingress منتشر می‌شوند و endpointهای health/metrics نیز باید
  با network policy محدود شوند.

## Kubernetes-ready notes

manifest یا Helm chart در این revision وجود ندارد. design target:

- Core APIها stateless و قابل Deployment/HPA؛ Gateway جداگانه و پشت ingress TLS؛
- migration به‌صورت pre-deploy Job با lock و timeout؛
- SQL/S3/broker به‌صورت managed service یا operator موردتأیید سازمان؛
- `startupProbe`, `readinessProbe`, `livenessProbe` جدا؛
- PodDisruptionBudget، topology spread و anti-affinity برای Core؛
- ServiceAccount بدون token mount غیرضروری، NetworkPolicy deny-by-default و Secret CSI؛
- Edge روی Kubernetes توصیه نمی‌شود مگر storage/USB/network و single-writer semantics تضمین شوند؛
- autoscaling فقط پس از تعریف metric و NFR؛ backlog بدون capacity model مبنای امن HPA نیست.

## backup و restore

### Edge

- RPO/RTO هنوز ورودی باز است؛ interval را خودسرانه تثبیت نکنید.
- برای online backup از SQLite backup API استفاده کنید؛ برای file copy، service را متوقف و `db`, `-wal`, `-shm` را یک مجموعه در نظر بگیرید.
- backup را encrypted، versioned و خارج از host نگه دارید.
- restore را روی host جدا انجام، integrity check و سپس countهای outbox/dead-letter را ثبت کنید.
- replay ممکن است side effect ایجاد کند؛ فقط با operation/idempotency key پایدار و sign-off اپراتور انجام شود.

### Core

- پنج database باید با یک runbook هماهنگ backup شوند؛ consistency cross-context از طریق event/idempotency حاصل می‌شود، نه transaction توزیع‌شده.
- object metadata و object storage باید lifecycle/backup هماهنگ داشته باشند.
- restore rehearsal، point-in-time recovery و credential rotation پس از restore الزامی است.
- policy retention/legal hold هنوز Blocked/Open است.

## rollback

1. rollout را متوقف و traffic را از replica ناسالم خارج کنید.
2. شواهد incident، correlation IDها و migration version را حفظ کنید.
3. اگر schema backward-compatible است، binary قبلی را redeploy کنید.
4. اگر destructive migration اجرا شده، از rollback script review‌شده یا restore استفاده کنید؛ down migration خودکار روی production مجاز نیست.
5. در Edge، ابتدا device traffic را متوقف کنید تا دو binary روی یک DB ننویسند.
6. پس از rollback، backlog و idempotency conflict را بررسی و فقط پیام‌های تأییدشده را replay کنید.

هر release باید حداقل یک نسخه backward-compatible با contract V1 و schema قبلی داشته باشد. قرارداد wire persisted را rename نکنید.

## health و smoke test

```powershell
Invoke-RestMethod http://127.0.0.1:7101/health/live
Invoke-RestMethod http://127.0.0.1:7101/health/ready
Invoke-RestMethod http://127.0.0.1:5298/health
```

بعد از health:

1. JWT با audience/issuer درست بگیرید؛
2. endpoint محافظت‌شده را بدون token و با role اشتباه تست کنید؛
3. Edge config bootstrap را با version فعلی تست کنید؛
4. یک عملیات با operation ID ثابت دو بار ارسال و replay بدون side effect را بررسی کنید؛
5. dependency موقت را قطع و رشد pending/outbox و سپس recovery را مشاهده کنید؛
6. logها را از نبود secret و وجود correlation ID بازبینی کنید.

## عیب‌یابی سریع

| علامت | بررسی | اقدام امن |
| --- | --- | --- |
| startup با missing configuration شکست می‌خورد | نام env و process identity | secret را از provider صحیح inject کنید؛ default تولیدی نسازید |
| `401` | issuer/audience/time/signing-key version | clock و token claims را بدون log کردن token بررسی کنید |
| `403` | role و allowed center | policy و scope کاربر/Edge را بررسی کنید |
| `429` | Gateway partition و Retry-After | client backoff؛ limit را بدون capacity evidence بالا نبرید |
| SQLite locked | instance دوم، WAL، disk latency | single writer را برقرار و process مزاحم را شناسایی کنید |
| outbox رشد می‌کند | Core connectivity/auth و last error redacted | dependency را رفع، سپس worker را observe کنید |
| dead-letter رشد می‌کند | poison payload/contract mismatch | مورد را quarantine؛ requeue گروهی نکنید |
| migration شکست می‌خورد | schema history و permission | rollout را متوقف و backup/rollback plan را اجرا کنید |
| pre-signed upload خطا دارد | clock، endpoint، region، bucket و headers | URL را log نکنید؛ clock/headerها را اصلاح کنید |
| Connected bag/dispatch ناسازگار است | SQL SP semantics/WSDL/label rule | عملیات را متوقف و blocker رسمی را resolve کنید |

جزئیات incident، dead-letter، disk pressure و recovery در [docs/operations/runbook.md](docs/operations/runbook.md) است.

## gate انتشار

یک release فقط وقتی قابل promotion است که:

- build با analyzerها و warnings-as-errors سبز باشد؛
- همه‌ی testها پاس و evidence مربوط به همان commit تولید شده باشد؛
- audit machine-readable تمام packageهای مستقیم/transitive بدون vulnerability یا deprecation سبز باشد؛
- diagram source/render همگام باشد؛
- secret hygiene پاس شود؛
- exact scan مقدارهای عملیاتی user-secrets در repository، runtime log و تمام Git history blobها پاس شود؛
- migration/backup/rollback روی staging rehearsal شده باشد؛
- blockerهای وابسته به محیط مقصد sign-off شده باشند؛
- هیچ credential، private input یا production endpoint حساس در artifact مستندات نباشد.
