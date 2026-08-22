# Core API نسخه‌ی ۱

Gateway مسیرهای `/api/auth/*`, `/api/edge/*` و `/api/admin/*` را به bounded hostها route می‌کند. OpenAPI هر host در `/swagger/v1/swagger.json` source of truth runtime است. جدول زیر surface Phase 1 و policy مورد انتظار را ثبت می‌کند؛ در صورت اختلاف، build/contract tests باید پیش از release این سند را همگام کنند.

## conventions

- JSON با camelCase و enumهای قراردادی ثابت.
- `Content-Type: application/json` مگر upload مستقیم object.
- `X-Correlation-ID` اختیاری در request و همیشه در response؛ مقدار نامعتبر جایگزین می‌شود.
- عملیات durable که در route map با «idempotency» مشخص شده‌اند، header `Idempotency-Key` پایدار می‌خواهند؛ registration/heartbeat به‌صورت state update طبیعی و pre-sign به‌صورت stateless مستثنا هستند.
- خطاها `application/problem+json` با `traceId` و بدون stack/secret هستند.
- timestampها ISO-8601 با offset و در server UTC می‌شوند.
- health و root anonymous؛ business endpointها authenticated.

## authentication و policy

| caller | credential | policy/role |
| --- | --- | --- |
| Edge | bearer JWT کوتاه‌عمر | `Edge` |
| مدیر | bearer JWT | `SystemAdministrator`, `GlobalAdministrator` یا `CenterAdministrator` |
| health probe | ندارد | anonymous |

Center administrator فقط داده‌ی centerهای مجاز در claim/scope را می‌بیند. Gateway validation کافی نیست؛ backend policy نیز باید enforce شود.

YARP policy به‌ازای route تنظیم می‌شود: routeهای صدور token عمداً anonymous هستند و خود Identity credential را validate می‌کند؛ business route بدون bearer معتبر `401` می‌گیرد. blanket authorization روی کل reverse proxy مجاز نیست، چون bootstrap احراز هویت را غیرممکن می‌کند.

## route map

### Identity

| method | route | policy | هدف |
| --- | --- | --- | --- |
| POST | `/api/auth/edge-token` | anonymous + credential validation | صدور Edge JWT |
| POST | `/api/auth/token` | anonymous + credential validation | صدور admin JWT |
| POST | `/api/admin/edges` | Administrator + center scope + idempotency | provision credential و برگرداندن secret فقط یک‌بار |
| POST | `/api/admin/edges/{deviceId}/rotate-secret` | Administrator + center scope + idempotency | rotate کردن secret و برگرداندن مقدار جدید فقط یک‌بار |
| POST | `/api/admin/edges/{deviceId}/disable` | Administrator + center scope + idempotency | غیرفعال‌سازی credential و audit |

بدنه‌ی provisioning:

```json
{
  "deviceId": "33333333-3333-4333-8333-333333333333",
  "logicalDeviceCode": "EDGE-12345-01",
  "exchangeCenterCode": "12345"
}
```

request provisioning به `Idempotency-Key` نیاز دارد و center باید در scope مدیر باشد. پاسخ اول `201` شامل `deviceId`, `deviceSecret`, `isReplay=false` و `recoveryRequired=false` است. retry همان request با همان key پاسخ `200` با `isReplay=true`, `recoveryRequired=true` و `deviceSecret=null` می‌گیرد؛ key یکسان با payload متفاوت `409` است. persistence فقط verifier hash‌شده، receipt بدون secret، audit و outbox را نگه می‌دارد.

`POST /api/admin/edges/{deviceId}/rotate-secret` body ندارد، `Idempotency-Key` تازه می‌خواهد و scope مرکز credential موجود را enforce می‌کند. پاسخ اول `200` secret جایگزین را دقیقاً یک‌بار نمایش می‌دهد و secret قبلی پس از commit دیگر token نمی‌گیرد. replay همان key نیز secret را بازنمی‌گرداند و `recoveryRequired=true` است. caller باید پاسخ اول provisioning/rotation را مستقیماً به secret manager مقصد منتقل کند، logging/body capture و Postman console را خاموش نگه دارد و buffer موقت را پاک کند. اگر secret ضبط نشد، با key جدید دوباره rotate کنید؛ از replay مقدار خام بازیابی نمی‌شود. `disable` پاسخ `204` دارد.

`disable` نیز body ندارد، idempotency key می‌خواهد و در اجرا و replay پاسخ `204` دارد.

### Data ingestion

| method | route | policy | هدف |
| --- | --- | --- | --- |
| POST | `/api/edge/parcel-events` | Edge + idempotency | final parcel event و durable `202` acknowledgment |
| POST | `/api/edge/images/metadata` | Edge + idempotency | validation/produce metadata تصویر و `202` |
| POST | `/api/edge/errors` | Edge + idempotency | validation/produce خطا، timeout و retry با `202` |

هر سه ورودی علاوه بر event ID، idempotency key، correlation ID، زمان و payload typed، `edgeId` و `exchangeCenterCode` دارند. Core آن‌ها را با claimهای `device_id` و تنها `allowed_center` توکن تطبیق می‌دهد و هویت را داخل event حمل می‌کند؛ mismatch با `403` رد می‌شود. acknowledgment فقط پس از durable persistence/publish موفق برگردد.

### Parcel lifecycle

| method | route | policy | هدف |
| --- | --- | --- | --- |
| POST | `/api/edge/parcels/inbound-query` | Edge + idempotency | DB commit؛ `200` با `isReplay` |
| POST | `/api/edge/bags` | Edge + idempotency | `201` اولیه، `200` replay، `409` conflict |
| POST | `/api/edge/dispatches` | Edge + idempotency | `201` اولیه، `200` replay، `409` conflict |
| GET | `/api/admin/parcels/{barcode}` | Administrator + scope | tracking یک parcel |
| GET | `/api/admin/parcels` | Administrator + scope | search/page projection |
| GET | `/api/admin/bags/{barcode}` | Administrator + scope | tracking یک bag |
| GET | `/api/admin/dispatches/{id}` | Administrator + scope | tracking یک dispatch |

ایجاد dispatch صرفاً برای Edge مرکز مبدأ تمام bagها مجاز است؛ قابل‌مشاهده‌بودن bag برای مرکز مقصد مجوز mutation نیست و replay نیز دوباره ownership را کنترل می‌کند.

### Device configuration

| method | route | policy | هدف |
| --- | --- | --- | --- |
| POST | `/api/admin/devices` | Administrator + idempotency | register sorting device و برگرداندن Device secret فقط یک‌بار |
| GET | `/api/admin/devices` | Administrator | list scoped devices |
| PUT | `/api/admin/devices/{deviceId}` | Administrator + idempotency | update metadata |
| PATCH | `/api/admin/devices/{deviceId}` | Administrator + idempotency | تغییر activation/status و audit |
| POST | `/api/admin/devices/{deviceId}/rotate-secret` | Administrator + center scope + idempotency | rotate Device token و نمایش یک‌بارهٔ مقدار جدید |
| POST | `/api/admin/config` | Administrator + scope + idempotency | انتشار snapshot versioned |
| GET | `/api/admin/config?exchangeCenterCode=...` | Administrator + scope | snapshot فعلی یک center |
| GET | `/api/edge/config/bootstrap?exchangeCenterCode=...&currentVersion=...` | Edge | آخرین snapshot معتبر با ETag؛ `304` برای نسخه‌ی جاری |

snapshot خروجی raw device token ندارد؛ verifier hash‌شده و policy/reference data versioned است. policy شامل `autoDispatchEnabled` و `maximumOpenBagMinutes` است و پیش‌فرض backward-compatible آن‌ها به‌ترتیب `false` و `60` دقیقه است. rotation دستگاه sorting body ندارد، پاسخ اول `200` شامل `deviceSecret` جدید است، replay همان `Idempotency-Key` مقدار `null` و `recoveryRequired=true` می‌دهد و snapshot verifier با version تازه منتشر می‌شود. برای rotation بدون قطع سرویس، snapshot علاوه بر verifier جاری می‌تواند verifier قبلی و `previousSecretValidUntil` را فقط تا پایان grace محدود (پیش‌فرض ۱۵ دقیقه، حداکثر ۲۴ ساعت) حمل کند؛ بعد از آن verifier قبلی دیگر منتشر یا پذیرفته نمی‌شود.

bootstrap همیشه هویت Edge با دقیقاً یک `allowed_center` را enforce می‌کند؛ query center فقط می‌تواند خالی یا همان center باشد. پاسخ تغییرکرده `200` و ETag قطعی به‌شکل quoted `"cfg-{center}-{version}"` دارد. اگر `currentVersion` برابر نسخه آخر یا `If-None-Match` مطابق ETag باشد پاسخ `304` بدون body است؛ نسخه client بزرگ‌تر از server برای recovery پاسخ کامل `200` می‌گیرد. مقدار منفی/malformed با `400` رد می‌شود.

### Object storage

| method | route | policy | هدف |
| --- | --- | --- | --- |
| POST | `/api/edge/images/presigned-url` | Edge | URL کوتاه‌عمر upload مستقیم |

درخواست شامل `eventId`، `readingRecordId`، `attachmentType`، `contentType`، `contentLength` و lifetime است و عمداً `objectKey` نمی‌پذیرد. Core از Edge/center احراز‌شده و شناسه‌های opaque یک key deterministic می‌سازد و در پاسخ کنار URL/expiry و map `requiredHeaders` برمی‌گرداند. امضای PUT، `Content-Type`، `Content-Length` و تمام headerهای این map را bind می‌کند؛ Production همیشه `x-amz-server-side-encryption: AES256` می‌خواهد و Edge باید آن را عیناً در PUT بفرستد. URL پاسخ secret موقت است و نباید log، analytics یا error detail آن را ذخیره کند؛ checksum/digest هنوز field قرارداد V1 نیست.

### Edge health

| method | route | policy | هدف |
| --- | --- | --- | --- |
| POST | `/api/edge/registration` | Edge | register نسخه/config Edge |
| POST | `/api/edge/heartbeat` | Edge | heartbeat و backlog metrics |
| POST | `/api/edge/events` | Edge + idempotency | operational event |
| GET | `/api/admin/edges/health?page=...&pageSize=...` | Administrator | list health scoped و paged |

registration مقدار `configurationVersion >= 0` می‌خواهد. heartbeat علاوه بر backlog counters مقدار غیرمنفی `availableDiskBytes` را ثبت می‌کند. `/api/edge/events` فقط operational connection/auth event است و enum شامل `Connected`, `Disconnected`, `AuthenticationRejected`, `DeviceIpBindingChanged` و `Authenticated` است؛ parcel event، image metadata و error به routeهای صریح DataIngestion می‌روند. Gateway contract test باید این تفکیک را تثبیت کند.

### Operational result

| method | route | policy | هدف |
| --- | --- | --- | --- |
| POST | `/api/edge/operational-results` | Edge + idempotency | `201` commit اول، `200` replay و `409` conflict؛ بدون Location غیرقابل GET |

بدنه‌ی operational result باید `edgeId` و `exchangeCenterCode` داشته باشد. هر دو با `device_id` و دقیقاً یک `allowed_center` توکن تطبیق داده و به‌عنوان provenance پایدار ذخیره می‌شوند؛ Edge مرکز یا شناسه دیگر `403` می‌گیرد.

## health و discovery

hostهای دارای `Post.ApiSupport`:

| route | معنا |
| --- | --- |
| `/` | نام سرویس و UTC؛ diagnostic ساده |
| `/health/live` | process زنده است؛ dependency check ندارد |
| `/health/ready` | فقط checkهای tag شده‌ی ready |
| `/health` | summary JSON سلامت |
| `/metrics` | Prometheus exposition؛ در API host با `Api:MetricsEnabled` |
| `/swagger` | non-production یا `Api:SwaggerEnabled=true` |

Gateway فعلی `/health`، `/health/ready` و `/metrics` دارد و `/` وضعیت خودش را می‌دهد. metrics anonymous در سطح application است و باید با network policy/ingress allow-list محافظت شود.

## idempotency و replay

- یک key با request hash متفاوت باید `409 Conflict` باشد.
- key درحال پردازش باید retry hint یا conflict قابل تشخیص بدهد.
- replay موفق response semantic قبلی را بدون side effect دوم بازمی‌گرداند.
- record پایدار/inbox و unique constraint source تصمیم‌اند؛ Redis فقط mirror فعال و قابل بازسازی است و cache hit هم authority SQL را دور نمی‌زند (ADR-0008).
- هیچ endpoint write با retry خودکار blind روی SQL/SOAP مجاز نیست.
- registration/heartbeat آخرین state را با identity claim-bound به‌روز می‌کنند و pre-sign فقط capability کوتاه‌عمر می‌سازد؛ برای این سه route در Phase 1 idempotency header تعریف نشده است.

## pagination و filtering

سه collection مربوط به parcel، sorting device و Edge health، filter/scope/count/order/`Skip`/`Take` را در query لایه persistence و SQL اجرا می‌کنند. `page` باید مثبت و `pageSize` بین ۱ و ۲۰۰ باشد؛ پاسخ `items`, `page`, `pageSize`, `totalCount` دارد. center scope قبل از count و pagination اعمال و sort پایدار و deterministic است تا page drift کاهش یابد.

## موارد باز API

- schema نهایی request/response برای APIهای سازمانی پس از WSDL/SQL/reference data؛
- سقف body/image/page/rate طبق NFR؛
- versioning strategy در URL/header برای V2؛
- retention و deletion/legal hold endpointها.
