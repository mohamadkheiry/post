# پروتکل Device ↔ Edge نسخه‌ی ۱

این سند قرارداد عمومی دستگاه را خلاصه می‌کند. source of truth قابل کامپایل در `src/Shared/Post.Contracts/Device` و ثابت‌های method/message در `ContractVersions.cs` است.

## transport

- SignalR hub: `/ws/device`؛ transport در Phase 1 به WebSockets محدود است.
- protocol version: `1.0`
- روش‌ها: `Auth`, `RegisterInbound`, `AssignDestination`, `CloseBag`, `CloseDispatch`
- اولین invocation کاربردی هر connection باید `Auth` باشد.
- ping/keepalive متعلق به SignalR است و message type کاربردی V1 ندارد.
- concurrent operation روی یک device/session باید توسط Edge serialize یا با business lock کنترل شود.

## envelope

تمام request/responseها envelope زیر را دارند:

```json
{
  "protocolVersion": "1.0",
  "messageType": "inbound.register",
  "correlationId": "11111111-1111-1111-1111-111111111111",
  "timestamp": "2026-08-16T08:00:00Z",
  "payload": {},
  "operationId": "22222222-2222-2222-2222-222222222222"
}
```

`operationId` یک extension اختیاری و backward-compatible است. برای `RegisterInbound`, `AssignDestination`, `CloseBag` و `CloseDispatch` باید در اولین تلاش تولید و در تمام retryها ثابت بماند. `correlationId` برای trace همان تلاش است و ممکن است عوض شود. GUID خالی معتبر نیست.

| field | الزامی | معنا |
| --- | --- | --- |
| `protocolVersion` | بله | دقیقاً `1.0` |
| `messageType` | بله | ثابت مطابق جدول method |
| `correlationId` | بله | شناسه‌ی request/response |
| `timestamp` | بله | ISO-8601 با offset؛ server آن را authoritative clock نمی‌داند |
| `payload` | بله | DTO مربوط به method |
| `operationId` | خیر | idempotency key پایدار |

response همان correlation را echo می‌کند. اگر caller، `operationId` پایدار فرستاده باشد، response همان مقدار را دارد. برای سازگاری با client قدیمی، عملیات effectful فاقد `operationId` از `correlationId` به‌عنوان fallback استفاده، آن را در envelope پاسخ می‌گذارند و `operationIdIsBestEffort=true` در payload می‌دهند. این fallback در retry با correlation جدید idempotency قوی ندارد؛ client جدید باید همیشه operation ID بفرستد. Auth فقط operation ID ارسال‌شده را echo می‌کند. protocol-level validation error با `messageType=protocol.error` و payload شامل `code`, `message`, `details` امن برمی‌گردد.

کدهای ثابت خطای ساختاری V1 فقط این مواردند:

| code | کاربرد |
| --- | --- |
| `MALFORMED_JSON` | کد canonical/reserved برای boundary‌ای که پس از JSON خراب هنوز می‌تواند envelope پاسخ بسازد |
| `MISSING_REQUIRED_FIELD` | field الزامی envelope یا payload غایب است |
| `UNKNOWN_MESSAGE_TYPE` | `messageType` envelope با Hub method شناخته‌شده سازگار نیست |
| `UNSUPPORTED_PROTOCOL_VERSION` | نسخه دقیقاً `1.0` نیست |
| `VALIDATION_ERROR` | سایر خطاهای ساختاری |

خطای کسب‌وکاری با status همان operation برمی‌گردد و نباید به Completion transport error تبدیل شود. `protocol.error` connection را نمی‌بندد؛ تنها Auth ناموفق پس از تحویل پاسخ connection را خاتمه می‌دهد.

محدودیت transport فاز ۱: JSON نامعتبر، argument غیرقابل bind یا target کاملاً ناشناخته پیش از ورود به typed Hub توسط ASP.NET SignalR رد می‌شود؛ client در این حالت safe SignalR Completion/connection error می‌بیند و `protocol.error` تضمین نمی‌شود. `MALFORMED_JSON` به‌عنوان code canonical برای transport adapter آینده رزرو مانده است. در مقابل، invocation متد شناخته‌شده با envelope قابل bind و version/type/field نادرست، `protocol.error` canonical می‌گیرد.

## جدول message type

| Hub method | message type | request | response |
| --- | --- | --- | --- |
| `Auth` | `auth` | `DeviceAuthenticateRequestV1` | `DeviceAuthenticateResponseV1` |
| `RegisterInbound` | `inbound.register` | `RegisterInboundRequestV1` | `RegisterInboundResponseV1` |
| `AssignDestination` | `destination.assign` | `AssignDestinationRequestV1` | `AssignDestinationResponseV1` |
| `CloseBag` | `bag.close` | `CloseBagRequestV1` | `CloseBagResponseV1` |
| `CloseDispatch` | `dispatch.close` | `CloseDispatchRequestV1` | `CloseDispatchResponseV1` |

## statusها

### جدول عمومی Device

| مقدار wire | نام | استفاده |
| ---: | --- | --- |
| 0 | Success | عملیات موفق |
| 1 | Pending | نتیجه‌ی عملیاتی هنوز نهایی نیست |
| 2 | Error | خطا |
| 3 | Returned | طبقه‌بندی برگشتی |
| 4 | Refused | امتناع/بازگشت به مبدأ |

Auth، CloseBag و CloseDispatch فقط `0` یا `2` برمی‌گردانند؛ `LabelPending` نام result عملیاتی است اما status عددی آن همچنان `0` است. Destination جدول مستقل دارد: `1=Confirmed`, `2=Error`. این enumها با statusهای Core یکسان نیستند و cast عددی ممنوع است.

## Auth

Request payload:

```json
{
  "deviceId": "device-local-01",
  "deviceToken": "value-supplied-outside-source-control"
}
```

Response موفق:

```json
{
  "status": 0,
  "sessionId": "33333333-3333-3333-3333-333333333333",
  "edgeServerTime": "2026-08-16T08:00:01Z"
}
```

در خطا `status=2` و `errorMessage` امن است؛ `sessionId` و `edgeServerTime` فقط موفقیت‌اند و در failure حذف/null می‌شوند. token، hash و دلیل قابل سوءاستفاده برگردانده نمی‌شود و پس از تحویل پاسخ، connection ناموفق بسته می‌شود. session به connection فعلی bind می‌شود و پس از disconnect معتبر نیست.

## RegisterInbound

Request payload شامل:

- `barcodes`: یک یا چند barcode ۱۴ یا ۲۴ رقمی طبق protocol موجود؛ check-digit رسمی هنوز blocker است؛
- `readTimestamp`: زمان خوانش؛
- `physicalAttributes`: وزن گرم و ابعاد میلی‌متر، اختیاری؛
- `parcelType`, `supplementaryData`, `images`, `timeoutMs`: اختیاری؛
- حداکثر تعداد image در schema فعلی ۳۲ و سقف **هر** رشته‌ی Base64 پنج میلیون character (حدود ۳٫۷۵ MiB decoded) است. ترکیب ۳۲ تصویر در همین سقف per-image از نظر V1 معتبر است و سقف aggregate پایین‌تری نباید اعمال شود. Host برای JSON overhead، سقف receive پیش‌فرض ۱۹۲ MiB و hard maximum پیکربندی ۲۵۶ MiB دارد؛ memory budget، concurrency و throughput نهایی هنوز NFR می‌خواهد.

نمونه:

```json
{
  "barcodes": ["12345678901234"],
  "readTimestamp": "2026-08-16T08:01:00Z",
  "physicalAttributes": {
    "weightGrams": 1250,
    "dimensions": { "lengthMm": 300, "widthMm": 200, "heightMm": 120 }
  },
  "parcelType": "parcel",
  "timeoutMs": 3000
}
```

Response عمومی `status` و در صورت وجود `originCode`, `destinationCode`, `serviceType`, discrepancy، پیام امن و `operationIdIsBestEffort` را حمل می‌کند. این marker در پاسخ‌های AssignDestination/CloseBag/CloseDispatch نیز وجود دارد. Pending یعنی state محلی durable و reconciliation لازم است؛ معنای دقیق outage باید قبل از Production تصویب شود.

## AssignDestination

Request:

```json
{
  "barcode": "12345678901234",
  "destinationCenterCode": "12345",
  "chuteId": "CH-07"
}
```

Response فقط status اختصاصی `1` یا `2` و `errorMessage` اختیاری دارد. عملیات برای barcode ناشناخته، session نامعتبر یا state بسته‌شده رد می‌شود.

## CloseBag

Request:

```json
{
  "destinationCenterCode": "12345",
  "sealNumber": "SEAL-LOCAL-001",
  "transportType": "Ground",
  "chuteIds": ["CH-07", "CH-08"],
  "parcelTypes": ["parcel"],
  "serviceTypes": [1, 2],
  "count": 10,
  "lastBarcode": "12345678901234"
}
```

قواعد مهم:

- `chuteIds`, `parcelTypes`, `serviceTypes` آرایه و filter هستند.
- `count` فقط تعداد مورد انتظار/اطلاعاتی است؛ **limit انتخاب نیست**.
- lock بر device/destination از بسته‌شدن همزمان جلوگیری می‌کند.
- `bagBarcode` و `bagLabel` فقط وقتی upstream آن‌ها را صادر کرده معتبرند.

`resultType` یکی از این مقادیر string است:

| result | status | معنا |
| --- | ---: | --- |
| `Completed` | 0 | کیسه و لیبل نهایی |
| `NoEligibleParcels` | 0 | مورد واجد شرایط نبود |
| `AllParcelsFailed` | 0 | call انجام شد ولی همه‌ی parcelها failure داشتند |
| `LabelPending` | 0 | بسته‌شدن فیزیکی ثبت شده، barcode/label هنوز pending |
| `Error` | 2 | عملیات بسته نشد |

`counts` چهار شمارنده‌ی `n/p/m/q` دارد و تعداد پذیرفته‌شده از `(n - m) + (p - q)` محاسبه می‌شود. وقتی `m` یا `q` غیرصفر است، `errors` باید خطای per-parcel را بدهد.

## CloseDispatch

Request فقط `destinationCenterCode` دارد. Edge bagهای بسته‌شده‌ی متعلق به device/session و همان مقصد را انتخاب می‌کند.

V1 فقط دو نتیجه دارد:

- `Completed` با status `0`، `dispatchId`, `bagCount`, `totalParcels` و problem list اختیاری؛
- `Error` با status `2`.

نتیجه‌ی `Queued` در V1 وجود ندارد. retry باید با همان operation ID انجام شود.

## compatibility rules

- field اختیاری جدید به V1 فقط وقتی افزوده می‌شود که readerهای قدیمی آن را نادیده بگیرند.
- حذف/rename/change type یا تغییر عدد enum نیازمند V2 است.
- `messageType`های persisted هرگز rename نمی‌شوند.
- time در wire با offset و در persistence به UTC normalize می‌شود.
- serializer defaults camelCase و statusهای عمومی numeric هستند؛ bag/dispatch result string هستند.

## خطا و امنیت

- schema/authorization error نباید connection-wide secret یا stack trace فاش کند.
- بعد از auth ناموفق، عملیات دیگر مجاز نیست.
- raw image، token و supplementary data در log قرار نمی‌گیرند.
- clock اختلاف زیاد باید telemetry ایجاد کند، نه اینکه به‌تنهایی idempotency را بشکند.
- payload limit per-image/count و receive ceiling، connection rate limit، invocation serialization و auth deadline در Host اعمال می‌شوند.
- یک inbound قانونی می‌تواند بسیار بزرگ باشد؛ Edge باید memory headroom و concurrency محدود داشته باشد. تغییر سقف قراردادی ۳۲×۵٬۰۰۰٬۰۰۰ breaking است و بدون version/sign-off مجاز نیست.

## endpointهای عملیاتی Edge

این endpointها device protocol نیستند و فقط برای probe/admin مجازند:

| method | route | caller | هدف |
| --- | --- | --- | --- |
| GET | `/health` | probe | health detail |
| GET | `/health/ready` | probe | readiness |
| PUT | `/admin/devices/{id}/ip` | Edge administrator | ثبت/تغییر IP binding دستگاه با audit |
| GET | `/api/edge/status` | Edge administrator | وضعیت safe پیکربندی/profile محلی |

endpoint مدیریتی باید authentication و authorization جدا از device session داشته باشد. IP به‌تنهایی credential نیست و جایگزین device token نمی‌شود.
binding در دیتابیس محلی Edge نگهداری و با config sync پاک نمی‌شود. `Edge:Security:AllowUnboundDevices` پیش‌فرض `false` است؛ فعال‌کردن آن فقط برای Development/Mock مجاز است.

بدنه‌ی جایگزینی binding یک collection کامل است و مقادیر قبلی را atomically جایگزین می‌کند:

```json
{
  "ipAddresses": ["127.0.0.1"]
}
```
