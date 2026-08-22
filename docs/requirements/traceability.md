# ماتریس ردیابی نیازمندی‌ها

این ماتریس requirement را به implementation و verification متصل می‌کند. وضعیت `Conditional` یعنی کد adapter وجود دارد اما پذیرش به artefact یا محیط بیرونی وابسته است. نام test class ممکن است refactor شود؛ test project و رفتار مورد انتظار باید ثابت بماند.

| ID | نیازمندی | کد/مرز مالک | تست/شاهد | وضعیت |
| --- | --- | --- | --- | --- |
| BUS-001 | احراز هویت device با deviceId/token | Contracts Device؛ Edge DeviceConnectionService؛ API Hub | Shared contract + Edge application/API integration tests | Implemented |
| BUS-002 | پاسخ auth با status 0/2؛ `sessionId` و `edgeServerTime` فقط در موفقیت | DeviceAuthenticateResponseV1؛ Hub mapper | serialization/status/API tests | Implemented |
| BUS-003 | ثبت inbound چند barcode و ویژگی فیزیکی | RegisterInbound V1؛ InboundRegistrationService؛ EdgeParcel | Shared/Edge domain/application tests | Implemented |
| BUS-004 | status inbound 0..4 | DeviceStatusesV1؛ explicit mapper | exhaustive mapping/contract tests | Implemented |
| BUS-005 | تعیین مقصد/chute با status اختصاصی 1/2 | DestinationAssignmentService؛ contract V1 | domain/application/contract tests | Implemented |
| BUS-006 | CloseBag با array filter و count اطلاعاتی | CloseBagRequestV1؛ selection/BagClosingService | contract + selection + bag service tests | Implemented; SQL conditional |
| BUS-007 | نتیجه‌های کامل CloseBag و n/p/m/q/errors | BagCloseResultTypeV1؛ EdgeBag/service | serialization/domain/application tests | Implemented; label blocked |
| BUS-008 | CloseDispatch فقط Completed/Error | CloseDispatch V1؛ DispatchClosingService | contract/domain/application tests | Implemented; SQL conditional |
| BUS-009 | عملیات فقط برای device/session مالک | DeviceSession؛ bag/dispatch queries | authorization/ownership tests | Implemented |
| BUS-010 | ادامه‌ی Edge هنگام قطع Core | SQLite + outbox + workers | infrastructure outage/restart tests | Implemented |
| REL-001 | operation ID پایدار و replay بدون اثر دوم | envelope V1؛ OperationIdentity؛ journal | Shared + Edge replay/idempotency tests | Implemented |
| REL-002 | correlation end-to-end | BuildingBlocks middleware/context؛ envelopes | middleware/contract/API tests | Implemented |
| REL-003 | transactional outbox، backoff و dead-letter | BuildingBlocks Messaging؛ Edge outbox/worker | state/backoff/infrastructure tests | Implemented |
| REL-004 | durable ingestion، consumerهای parcel/image و inbox/idempotency Core | InboundValidationService؛ Kafka delivery processors؛ Core stores/domain | Core application/infrastructure tests | Implemented |
| REL-005 | SQLite WAL و migration startup | EdgeDatabaseInitializer؛ migrations | Infrastructure startup/migration tests | Implemented |
| REL-006 | پنج persistence boundary و migration chain مستقل Core | CoreDbContexts؛ migrations پنج‌گانه؛ profile-aware DI | Core infrastructure/architecture tests + SQL generation | Implemented; staging rollout gate |
| REL-007 | Redis read-model فعال، TTLدار و قابل بازسازی بدون تضعیف authority SQL | Post.Core.Caching؛ coordinatorهای lifecycle/operational result؛ cleanup worker | hit/miss/stale/conflict/outage/post-commit/two-replica tests + full-development recovery | Implemented; E2E verified |
| REL-008 | retry/timeout/circuit breaker محدود و جلوگیری از تکرار کور اثر پستی | Edge HTTP/SQL resilience؛ operation journal؛ intent/receipt saga | transient/non-transient/break/recovery/commit-unknown tests | Implemented |
| CFG-001 | registry، activation و one-time secret rotation دستگاه | DeviceManagementService؛ DeviceConfig API/context | Core domain/application/infrastructure tests + API sample | Implemented |
| CFG-002 | snapshot config versioned بدون raw token | ConfigurationService؛ ConfigurationSnapshot contracts | hash/serialization/application tests | Implemented |
| CFG-003 | pull/bootstrap و Edge sync | CoreConfigurationSource؛ ConfigurationSyncService | Edge application/infrastructure/API tests | Implemented/conditional |
| SEC-001 | JWT issuer/audience/lifetime/role | Gateway؛ ApiSupport؛ Core security adapters | security unit/API integration tests | Implemented |
| SEC-002 | scope مرکز برای admin query | AccessScope؛ tracking/device services | Core application/API authorization tests | Implemented |
| SEC-003 | hash credential و عدم log secret | Pbkdf2SecretHasher؛ SecretRedactor | security/redaction tests + secret script | Implemented |
| SEC-004 | security headers و Problem Details | Gateway؛ ApiSupport | API integration tests | Implemented |
| SEC-005 | provision/rotate/disable credential Edge با Admin/scope، idempotency عملیات secret و نمایش یک‌باره | Identity API؛ EdgeAuthService؛ verifier/receipt/audit/outbox | Identity API/application/infrastructure tests + manual secret-handling review | Implemented |
| SEC-006 | local-admin مستقل Edge با bootstrap امن، PBKDF2، session محدود، rate limit و audit | Edge LocalAdmin domain/application/store/API | Edge domain/infrastructure/API auth tests + restart/non-disclosure | Implemented |
| SEC-007 | TLS/WSS و رمزنگاری DB/WAL/SHM/backup Edge با کلید بیرونی و rotation | Edge host/storage؛ SQLite3MC؛ ADR-0009 | startup/rekey/old-key rejection/backup/restore/runtime inspection | Implemented; E2E verified |
| SEC-008 | mutation کیسه/final-status فقط توسط مرکز آخرین reading، نه هر reading تاریخی | ParcelDossier.LatestReading؛ BagStorageService؛ ParcelEventConsumer | late-arrival/equal-time/cross-center service + host no-side-effect tests | Implemented |
| OBS-001 | health live/ready/detail | ApiSupport و Gateway | API integration + manual probes | Implemented |
| OBS-002 | Edge heartbeat/backlog/last sync | EdgeCore heartbeat؛ EdgeHealthService | contract/application/infrastructure tests | Implemented |
| OBS-003 | structured telemetry، scrape مرکزی، correlation query و dashboard | JSON host logging؛ `/metrics`؛ Alloy/Loki/Prometheus/Grafana profile | host tests + full-development scrape/log/dashboard gate | Implemented local; Production NFR/topology conditional |
| CORE-001 | parcel lifecycle/tracking | ParcelLifecycle domain/context/services | Core domain/application/infrastructure tests | Implemented |
| CORE-002 | bag/dispatch archive | Bag/Dispatch storage services/context | Core application/infrastructure/API tests | Implemented |
| CORE-003 | operational result audit | OperationalResult service/context | Core tests | Implemented |
| CORE-004 | Edge registration/health/events | EdgeHealth services/context/API | Core/API tests | Implemented |
| MED-001 | pre-signed S3 upload با object-key مشتق‌شده از Edge/center و اتصال metadata | PresignedUrlService؛ S3SignatureV4Presigner؛ attachment consumer/reconciler | signature/service/API/consumer tests | Implemented; external conditional |
| INT-001 | destination HTTP token/cache/lookup | Destination adapter/options | Edge infrastructure contract tests | Conditional |
| INT-002 | postal inbound/bag/dispatch SQL | PostalSqlGateway و clients | fake gateway + sandbox contract tests | Blocked by SQL contract |
| INT-003 | parcel info SOAP | PostalSoapClient | parser/fault + sandbox contract tests | Blocked by WSDL |
| API-001 | Gateway routing/rate limiting | Core Gateway config/Program | API integration tests | Implemented |
| API-002 | versioned Core REST و Swagger | Core API hosts + ApiSupport | API integration/OpenAPI review | Implemented |
| API-003 | نمونه‌ی اجرایی تمام routeهای Phase 1 بدون secret | HTTP/Postman artefactهای `docs/api` | JSON parse + تطبیق خودکار method/URL دو collection + manual smoke | Implemented |
| TST-001 | هر متد دست‌نویس .NET تست دارد | تمام `tests/*` | 601/601 test و 1244/1244 متد در `docs/test-results/20260822-ed77486` | Verified |
| DOC-001 | مستندات business/architecture/deployment/testing | README، `deploy.md`، docs | markdown/link/diagram review | Implemented |
| DOC-002 | source و SVG دیاگرام‌ها | `docs/diagrams/*.mmd/*.svg` | `verify-diagrams.ps1` | Implemented |
| OPS-001 | backup/rollback/runbook | `deploy.md` + operations runbook | full-development TDE/backup/restart/HA rehearsal evidence | Verified locally |
| OPS-002 | composition توسعه‌ای کامل با auth-on، پنج SQL DB، Redis، Kafka-compatible، S3-compatible، Edge رمز‌شده و observability/HA profile | `compose.full.yaml`؛ `infra/`؛ `scripts/infra/test-full-dev.ps1` | real broker/object-store/outage/restart/load/TDE/SSE/HA evidence | Implemented; E2E verified |
| UI-001 | UI مدیریتی/تحلیلی | ندارد | ندارد | Out of Phase 1 |

## blocker register

| Blocker | مالک ورودی | شرط بسته‌شدن | test لازم پس از دریافت |
| --- | --- | --- | --- |
| قالب label و printer language | عملیات پست/سخت‌افزار | template versioned و نمونه‌ی تأییدشده | golden render + printer acceptance |
| SQL connection/schema/SP | DBA/مالک سامانه پستی | least-privilege sandbox و contract رسمی | parameter/result/idempotency/failure contract tests |
| SOAP WSDL/fault catalog | مالک SOAP | WSDL version، action، namespace، TLS/auth | generated/manual client contract tests |
| Excelهای مرجع | مالک business data | فایل versioned، schema و owner | import validation + checksum + routing fixtures |
| check digit/canonical barcode | مالک استاندارد بارکد | rule رسمی و نمونه‌های مثبت/منفی | exhaustive boundary/property tests |
| NFR/SLO | product/operations | baseline pilot دو مرسوله/ثانیه موجود؛ percentile latency انتهابه‌انتها، burst/concurrency/availability/RPO/RTO/capacity باز | baseline load + load/soak/failover tests پس از تصویب |
| retention/legal hold | security/legal/data owner | TTL و deletion/audit policy | expiry/archive/restore/legal-hold tests |

## روش نگهداری ماتریس

1. هر requirement جدید ID پایدار می‌گیرد.
2. قبل از merge حداقل یک مرز کد و یک روش verification درج می‌شود.
3. requirement breaking یک ADR و contract version جدید می‌خواهد.
4. `Implemented` بدون runnable composition root و test مجاز نیست.
5. blocker تنها با artefact قابل ارجاع و test سبز بسته می‌شود، نه با فرض شفاهی.
