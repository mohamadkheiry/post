# قابلیت اطمینان و امنیت

## مدل تحویل و idempotency

شبکه و workerها at-least-once هستند. هدف سامانه «یک اثر کسب‌وکاری برای یک operation ID» است، نه ادعای exactly-once transport.

- `operationId`: کلید پایدار یک عملیات اثرگذار؛ client آن را در retry نگه می‌دارد.
- `correlationId`: شناسه‌ی trace یک request/attempt؛ پاسخ همان مقدار را echo می‌کند.
- `messageId/eventId`: هویت delivery برای inbox/outbox.
- idempotency key: scope + value در Core/adapterها؛ request hash از reuse متناقض جلوگیری می‌کند.

اگر device قدیمی `operationId` ندهد، Edge فقط در همان attempt می‌تواند `correlationId` را fallback کند و پاسخ باید best-effort بودن آن را مشخص کند. طبق قرارداد V1، `correlationId` هر retry شناسهٔ attempt تازه است؛ بنابراین چنین client قدیمی idempotency قابل اتکا ندارد و نباید برای عملیات اثرگذار retry کور انجام دهد. مسیر صحیح، ارسال `operationId` پایدار و `correlationId` تازه در هر retry است.

## transactional outbox

1. Domain state، operation journal و outbox row در transaction محلی commit می‌شوند.
2. worker پیام due را با lock expiry claim می‌کند.
3. adapter مقصد پیام را با idempotency key می‌فرستد.
4. acknowledgment پایدار پیام را published می‌کند.
5. failure با bounded exponential backoff retry می‌شود.
6. پس از سقف attempt، پیام dead-letter می‌شود و نیازمند بررسی انسانی است.

requeue باید تک‌پیام، auditشده و پس از رفع علت باشد. bulk replay یا پاک‌کردن dead-letter بدون backup ممنوع است.

metadata تصویر پس از پذیرش durable در topic اختصاصی مصرف می‌شود. اگر reading هنوز نرسیده باشد، metadata به pending پایدار منتقل و delivery اصلی commit می‌شود تا Kafka retry و DLQ کاذب تولید نشود. reconciler آن را با backoff محدود دوباره متصل می‌کند؛ poison/expired/max-attempt به topic dead-letter اختصاصی می‌رود و رکورد پایدار برای بررسی نگه داشته می‌شود. تنظیمات ۳۰s/۵s/۳۰۰s/۱۰/۲۴h/۵۰ فقط default ایمنی قابل تنظیم‌اند و جایگزین SLA یا retention policy نیستند.

## timeout و cancellation

timeout transport با نتیجه‌ی کسب‌وکار یکی نیست. اگر پس از timeout معلوم نیست upstream commit کرده یا نه، state باید `Pending/Unknown` یا reconciliation-required باشد؛ اجرای مجدد blind ممکن است duplicate بسازد. فقط CloseDispatch V1 نتیجه‌ی queued ندارد و به Device `Error` برمی‌گردد، هرچند reconciliation داخلی می‌تواند ادامه یابد.

## status isolation

Device status عددی است:

- عمومی: success `0`، pending `1`، error `2`، returned `3`، refused `4`؛
- destination: confirmed `1`، error `2`؛
- auth: فقط `0` یا `2`؛
- bag result string و dispatch result string جدول جدا دارند.

Core status معنای persistence/reporting دارد و enum جداست. تبدیل فقط از mapper صریح و exhaustive انجام می‌شود؛ cast عددی بین enumها ممنوع است.

## مرز اعتماد

| مرز | کنترل لازم |
| --- | --- |
| Device → Edge | TLS، auth اولین پیام، binding session/connection/device، input limit |
| Local Admin → Edge | TLS، credential store محلی، PBKDF2، session محدود، rate limit و audit؛ مستقل از Core JWT |
| Edge → Core | short-lived Edge JWT، role `Edge`، audience/issuer، idempotency |
| Admin → Core | JWT، role Admin، allowed-center scope، audit |
| Edge → postal systems | least privilege account، allow-list شبکه، timeout، redaction |
| Core → S3 | short-lived pre-sign، content constraints، opaque object key |
| Gateway → backend | private network، forwarded identity محدود، no public backend |

## secret lifecycle

- secret در source، appsettings committed، test evidence، diagram یا ticket قرار نمی‌گیرد.
- local فقط user-secrets یا process environment؛ staging/production فقط vault/secret manager.
- token/password/connection string/pre-signed URL هرگز log نمی‌شود.
- Device snapshot raw token ندارد؛ verifier شامل algorithm/salt/hash/iterations است.
- device unbound به‌طور پیش‌فرض رد می‌شود؛ IP binding محلی است و bypass فقط برای Development/Mock قابل قبول است.
- trust boundary دستگاه به IP واقعی socket متکی است؛ مسیر باید direct یا source-preserving باشد و Edge برای جلوگیری از spoof به forwarded header اعتماد نمی‌کند.
- signing key، DB password و S3 credential دوره‌ای rotate می‌شوند.
- کلید SQLite Edge فقط از secret manager/process environment خوانده می‌شود؛ current key در `POST_EDGE_SQLITE_KEY` و previous key موقت فقط برای rotation است. مقدار هیچ‌کدام وارد connection string، log، evidence یا Git نمی‌شود.
- DB، WAL، SHM و backup Edge رمز می‌شوند. Production بدون encryption و کلید معتبر بالا نمی‌آید؛ rotation پس از checkpoint/backup با اثبات رد کلید قبلی کامل می‌شود ([ADR-0009](adr/0009-edge-sqlite-encryption-and-external-keys.md)).
- local-admin Edge credential مستقلی دارد: bootstrap فقط first-run از secret source، password فقط به verifier PBKDF2 تبدیل و session Bearer محدود صادر می‌شود. auth-off فقط برای profile توسعه‌ای loopback و synthetic مجاز است.
- `Edge:DestinationApi` username/password فقط credential نام‌کاربری/گذرواژهٔ OAuth سرویس destination lookup با `grant_type=password` است؛ OAuth Client Credentials grant نیست. Edge آن را برای دریافت/caching access token مصرف می‌کند و نباید با device token، Core JWT، SQL یا SOAP credential reuse شود.
- archive ورودی این credential را plaintext آشکار کرده بود؛ پس از validation باید revoke/rotate و history/evidence از نبود مقدار آن scan شود.
- دسترسی operator به secret manager و database audit می‌شود.
- disable/rotate credential جلوی صدور token بعدی را می‌گیرد، اما JWT بدون state قبلی تا expiry معتبر است؛ عمر Edge token فعلاً حداکثر ۹۰۰ ثانیه است. containment فوری به قطع session/network یا rotation هماهنگ signing key نیاز دارد.

## input و abuse controls

- envelope version/message type/correlation/operation ID validate می‌شود؛
- body، image count/size، barcode count و string length باید سقف داشته باشند؛
- قرارداد ۳۲ تصویر × پنج میلیون Base64 character را معتبر می‌داند؛ receive بزرگ با rate/concurrency/memory limit مهار می‌شود و aggregate cap پایین‌تر بدون version جدید مجاز نیست؛
- JSON unknown/invalid status با protocol error امن پاسخ می‌گیرد؛
- Gateway rate limiting partition بر subject یا IP دارد؛
- SQL command پارامتری است و نام SP/table از config uncontrolled گرفته نمی‌شود؛
- SOAP/XML باید XXE غیرفعال، size محدود و fault sanitize شود؛
- path/object key از traversal و header injection پاک‌سازی می‌شود.

سقف‌های عددی نهایی جز موارد contract‌شده باید با NFR تصویب شوند.

## logging و حریم داده

حداقل fieldهای log: timestamp UTC، service، level، correlation ID، operation ID در صورت وجود، event name، outcome و duration. barcode در صورت الزام audit می‌تواند mask/hash شود؛ payload تصویر و supplementary data به‌طور پیش‌فرض log نمی‌شود.

Device IP نیز داده‌ی حساس عملیاتی است و نباید log شود. Retention، masking classification و legal hold هنوز Open است. تا تصویب، کمینه‌سازی داده مقدم است.

cleanup داده‌ی موقت Edge به‌صورت پیش‌فرض خاموش است. windowهای فنی ۳۰ روز برای داده و ۹۰ روز برای idempotency فقط حداقل safety قابل تنظیم‌اند؛ فعال‌سازی نیازمند approval اپراتور است و فقط chainهای کامل و Core-confirmed، outbox پردازش‌شده و stateهای terminal امن را شامل می‌شود. pending فعال و dead-letter حذف نمی‌شوند و این windowها جای policy retention/legal-hold را نمی‌گیرند.

## failure containment

- قطع Core خط device را متوقف نمی‌کند تا سقف local capacity؛
- disk-full در Edge یک failure بحرانی است و پذیرش جدید باید fail-safe شود؛
- SQL مشترک مرجع idempotency چند replica است؛ Redis mirror فعال، قابل بازسازی و در outage غیرمرجع می‌ماند (ADR-0008)؛
- broker/S3 failure نباید transaction اصلی را silently موفق نشان دهد؛
- poison message به dead-letter می‌رود؛
- migration failure startup را fail می‌کند و traffic نمی‌گیرد.
- شکستن circuit هر dependency از dependency دیگر جداست؛ retry فقط برای خطای transient و عملیات امن/idempotent انجام می‌شود. نتیجهٔ commit-unknown در عملیات پستی وارد saga/reconciliation می‌شود و کورکورانه تکرار نمی‌شود.

## verification gates

- unit/contract/integration/architecture testها؛
- replay و duplicate tests؛
- serializer golden tests برای قرارداد V1؛
- cancellation/timeout/failure injection؛
- dependency outage و recovery؛
- secret scan و log inspection؛
- migration/restore rehearsal؛
- اثبات رمزنگاری DB/WAL/SHM، rekey، backup/restore و restart با کلید بیرونی؛
- full-development واقعی با SQL/Redis/Kafka/S3، outage/recovery، دو replica، load و HA؛
- load/soak بعد از دریافت NFR.
