# فهرست تصمیم‌های معماری

ADRها append-only هستند. تصمیم superseded حذف نمی‌شود؛ وضعیت و جایگزین آن به‌روزرسانی می‌شود.

| ADR | عنوان | وضعیت |
| --- | --- | --- |
| [0001](adr/0001-edge-first-source-of-truth.md) | Edge-first operations و مرجع حقیقت | Accepted |
| [0002](adr/0002-sqlite-journal-outbox.md) | SQLite WAL، journal و outbox | Accepted |
| [0003](adr/0003-versioned-contracts-idempotency.md) | قرارداد نسخه‌دار و operation ID | Accepted |
| [0004](adr/0004-core-boundaries-gateway.md) | Core bounded services پشت Gateway | Accepted |
| [0005](adr/0005-integration-modes.md) | Mock و Connected adapter modes | Accepted with blockers |
| [0006](adr/0006-presigned-object-upload.md) | direct object upload با pre-signed URL | Accepted |
| [0007](adr/0007-sql-authoritative-idempotency.md) | SQL-authoritative idempotency؛ Redis deferred | Superseded by 0008 |
| [0008](adr/0008-redis-rebuildable-idempotency-mirror.md) | Redis mirror قابل بازسازی؛ SQL authoritative | Accepted |
| [0009](adr/0009-edge-sqlite-encryption-and-external-keys.md) | SQLite3MC واحد، کلید بیرونی و rotation/backup رمز‌شده Edge | Accepted |

هر ADR جدید باید context، decision، consequences، alternatives و verification را ثبت و traceability را به‌روزرسانی کند.
