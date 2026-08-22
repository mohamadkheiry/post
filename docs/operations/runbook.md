# Runbook عملیاتی

## هدف و قواعد ایمنی

این runbook برای on-call و operator است. ابتدا از توقف گسترش خطا و حفظ داده مطمئن شوید. secret، token، connection string، pre-signed URL، image و payload خصوصی را در chat/ticket/log کپی نکنید. عملیات destructive، bulk replay و schema rollback بدون approval و backup ممنوع است.

## شناسه‌های تشخیص

در شروع incident ثبت کنید:

- زمان UTC و محیط؛
- service/Edge ID و software/config version؛
- correlation ID و operation ID؛
- health status؛
- pending/outbox/dead-letter count و available disk؛
- آخرین sync موفق؛
- deployment/migration version؛
- dependency affected، بدون credential یا endpoint خصوصی.

## severity پیشنهادی

| severity | نمونه | اقدام |
| --- | --- | --- |
| SEV-1 | توقف خط، data loss محتمل، auth گسترده خراب | incident commander، freeze change، containment فوری |
| SEV-2 | backlog رو به رشد، یک مرکز degraded، dead-letter زیاد | on-call + owner dependency، mitigation کنترل‌شده |
| SEV-3 | یک device/عملیات، workaround موجود | ticket و بررسی در ساعات کاری |

thresholdهای عددی پس از NFR/SLO رسمی تکمیل شوند.

## بررسی اولیه

```powershell
Invoke-RestMethod http://127.0.0.1:7080/health
Invoke-RestMethod http://127.0.0.1:5298/health
Get-Volume
Get-Process -Name dotnet -ErrorAction SilentlyContinue
```

در host مدیریت‌شده از ابزار استاندارد platform استفاده کنید. log query باید correlation ID را هدف بگیرد و output پیش از اشتراک redacted شود.

## Edge unavailable

1. برق/OS/network/process و فضای volume را بررسی کنید.
2. مطمئن شوید instance دوم روی همان SQLite file فعال نیست.
3. آخرین log امن و migration error را بررسی کنید.
4. اگر DB lock است، process مالک را شناسایی کنید؛ فایل lock/WAL را دستی حذف نکنید.
5. پیش از تغییر binary یا DB snapshot/backup هماهنگ بگیرید.
6. service را یک‌بار restart و health/session/single inbound smoke test کنید.
7. اگر restart تکراری failure دارد، rollback مطابق deployment guide و incident را escalate کنید.

## disk pressure یا disk full در Edge

1. پذیرش traffic جدید را طبق procedure مرکز متوقف کنید؛ write ناقص را force نکنید.
2. اندازه‌ی DB/WAL/log/outbox/dead-letter را اندازه‌گیری کنید.
3. فایل DB، WAL، SHM یا evidence فعال را حذف نکنید.
4. logهای خارج از retention مصوب را فقط با approval rotate/archive کنید.
5. backlog dependency را رفع کنید تا WAL/checkpoint و outbox کاهش یابد.
6. volume را گسترش یا state را با backup/restore کنترل‌شده منتقل کنید.
7. integrity check و smoke test سپس traffic را تدریجی بازگردانید.

## outbox backlog

1. Core network/DNS/TLS/JWT expiry و response class را بررسی کنید.
2. oldest age، attempt distribution، target و message type را ثبت کنید.
3. rate limit/`Retry-After` را رعایت کنید؛ poll/batch را ناگهانی بالا نبرید.
4. dependency را رفع و worker را observe کنید.
5. duplicate effect را با operation ID در مقصد reconcile کنید.
6. اگر backlog از ظرفیت disk عبور می‌کند، عملیات مرکز را طبق severity محدود کنید.

## dead-letter

1. message را quarantine و error redacted را بررسی کنید.
2. schema version و adapter compatibility را با همان deployment تطبیق دهید.
3. side effect مقصد را با idempotency key بررسی کنید.
4. علت را رفع و فقط یک پیام آزمایشی را requeue کنید.
5. outcome را audit؛ سپس batch کوچک با توقف‌پذیری replay کنید.
6. dead-letter را برای خالی‌کردن dashboard حذف نکنید.

### pending metadata تصویر

1. backlog `PendingAttachment`، oldest age، attempt و `NextAttemptAt` را بدون نمایش payload حساس ثبت کنید.
2. ابتدا بررسی کنید reading متناظر در ParcelLifecycle رسیده و center/Edge/object-key با metadata سازگار است.
3. consumer پس از handoff پایدار pending باید delivery اولیه را commit کرده باشد؛ Kafka DLQ و pending DLQ را با هم اشتباه نگیرید.
4. پیش‌فرض reconciler برابر polling ۳۰ ثانیه، backoff از ۵ تا ۳۰۰ ثانیه، ۱۰ تلاش، سن ۲۴ ساعت و batch ۵۰ است؛ این اعداد SLA یا retention مصوب نیستند.
5. رکورد dead-letter یا object orphan را تا دریافت policy retention/legal hold حذف نکنید؛ recovery را با replay تک‌موردی و event ID اصلی انجام دهید.

## SQL پستی unavailable یا commit نامعلوم

1. عملیات affected و operation ID را ثبت کنید.
2. timeout را به‌معنای failure قطعی فرض نکنید.
3. با query/SP reconciliation فقط read-only بررسی کنید که اثر commit شده یا نه.
4. اگر وضعیت نامعلوم است، retry blind نکنید؛ pending/manual review.
5. رفتار Device را طبق truth table مصوب اعمال کنید؛ این table تا دریافت contract رسمی blocker است.
6. پس از recovery duplicate و counterهای bag/dispatch را reconcile کنید.

## SOAP یا Destination API failure

- DNS/TLS/certificate/clock و status/fault class را بررسی کنید.
- برای Destination، credential نام‌کاربری/گذرواژهٔ OAuth با `grant_type=password` مخصوص lookup بارکد ۱۴ رقمی → `ToCityID` و access-token cache/refresh skew را بررسی کنید؛ آن را با OAuth Client Credentials grant یا device/Core/SQL/SOAP credential جایگزین نکنید.
- credential را rotate/test کنید بدون log کردن مقدار؛ credential آشکارشده در archive اولیه پس از validation باید revoke/rotate شود.
- token refresh storm و 401 loop را متوقف کنید.
- payload malformed را redacted capture و با WSDL/schema رسمی مقایسه کنید.
- circuit/retry فقط برای failureهای transient و عملیات امن است.

## Core API یا Gateway failure

1. Gateway health و سپس backend مقصد را جدا بررسی کنید.
2. route cluster، DNS، readiness و deployment version را بررسی کنید.
3. `401` را از `403` و `429` تفکیک کنید.
4. clock/issuer/audience/signing-key version را مقایسه کنید.
5. backend ناسالم را از load balancer خارج و نسخه‌ی قبل را canary کنید.
6. Edge باید queue کند؛ backlog و disk آن را همزمان monitor کنید.

## Redis

Redis در فاز ۱ mirror فعال ولی غیرمرجع است؛ SQL مشترک و unique constraint نتیجهٔ replay/conflict را تعیین می‌کنند. در outage، health مربوط به cache باید Degraded شود اما business write تأییدشده از SQL نباید fail یا دوباره اجرا شود. SQL row count، conflict/replay و cache-error metric را بررسی کنید، سپس Redis را با همان namespace و credential امن بازیابی کنید. Production باید TLS و ACL با username/password صریح داشته باشد و کاربر anonymous/default غیرفعال بماند. miss/stale در دسترسی بعدی از SQL repair می‌شود و TTL cleanup فقط cache/index را پاک می‌کند. flush یا حذف SQL idempotency record برای بازسازی cache ممنوع است؛ رجوع به ADR-0008.

## broker failure

- durable acknowledgment برنگشته را موفق علامت نزنید؛
- producer/broker health، authorization و partition را بررسی کنید؛
- outbox را حفظ و retry را bounded نگه دارید؛
- بعد از recovery ordering/idempotency consumer را verify کنید.

## S3/pre-signed upload failure

1. clock، expiry، region، bucket، path-style و required headers را بررسی کنید.
2. URL کامل را در ticket/log قرار ندهید.
3. object موجود را با metadata/digest reconcile کنید.
4. orphan object یا metadata را فقط طبق retention policy پاک کنید.
5. credential compromise محتمل را revoke/rotate و audit کنید.

## authentication incident

- signing key leak محتمل: issuance را متوقف، key را rotate، tokenها را revoke/expire و audit کنید؛
- Device secret leak: credential همان device را disable/provision و sessionهای آن را قطع کنید؛
- Edge → Core secret گم‌شده یا افشاشده: برای containment فوری `POST /api/admin/edges/{deviceId}/disable` را با idempotency key تازه اجرا کنید؛ برای replacement، `POST /api/admin/edges/{deviceId}/rotate-secret` را با ticket/key مستقل اجرا و secret یک‌بارنمایش را مستقیم در secret manager مقصد ثبت کنید؛ replay همان key مقدار را بازنمی‌گرداند و recovery از hash ممکن نیست؛
- disable/rotate صدور Edge token بعدی را می‌بندد، اما token از قبل صادرشده می‌تواند تا expiry حداکثر ۹۰۰ ثانیه معتبر بماند؛ در incident فوری session/network را قطع یا signing key را با ارزیابی اثر سراسری rotate کنید؛
- admin compromise: user را disable، scope/roles و audit trail را review کنید؛
- raw secret را برای اثبات incident جمع‌آوری نکنید؛ secret ID/version کافی است.

## backup و restore Edge

1. device traffic را متوقف و process را drain/stop کنید یا SQLite backup API به‌کار ببرید.
2. DB/WAL/SHM را consistent capture و encrypt کنید.
3. checksum و timestamp/version را ثبت کنید.
4. روی host جدا restore و integrity/schema version را بررسی کنید.
5. outbox/dead-letter counts و چند query domain را compare کنید.
6. با network بیرونی بسته یک dry run انجام دهید تا replay ناخواسته نشود.
7. پس از approval connectivity را باز و backlog را تدریجی پردازش کنید.

## backup و restore Core

پنج database، object metadata و object storage را به‌عنوان recovery set مدیریت کنید. restore باید point-in-time، migration compatibility، identity key rotation و replay inbox/outbox را پوشش دهد. RPO/RTO نهایی هنوز Open است.

## rollback release

- traffic/canary را freeze کنید؛
- migration compatibility را تعیین کنید؛
- binary قبلی را فقط روی schema backward-compatible اجرا کنید؛
- destructive rollback را با restore انجام دهید؛
- در Edge هرگز دو نسخه را همزمان روی DB اجرا نکنید؛
- پس از rollback health، auth، idempotent replay و backlog را smoke-test کنید.

## support bundle امن

قابل جمع‌آوری:

- version/commit/config version بدون secret؛
- health JSON؛
- snapshot کوتاه metricهای `/metrics` بدون label حساس؛
- count و age aggregate صف‌ها؛
- logهای redacted محدود به correlation؛
- schema migration list؛
- OS/runtime/disk metadata؛
- test/checksum evidence.

غیرقابل جمع‌آوری:

- `.env`، appsettings local، token/authorization header؛
- connection string، private key، password یا device token؛
- pre-signed URL؛
- image/base64 و متن خصوصی اسناد ورودی؛
- device IP یا network binding واقعی؛
- database کامل مگر با مسیر رسمی داده و encryption.

## پایان incident

1. health و business smoke test سبز؛
2. backlog/dead-letter پایدار یا صفر طبق baseline؛
3. duplicate/data reconciliation کامل؛
4. secretهای در معرض rotate؛
5. timeline، root cause، impact و corrective action ثبت؛
6. test regression و traceability/ADR/runbook به‌روزرسانی؛
7. retention artefact incident طبق policy مصوب.
