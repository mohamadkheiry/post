# ADR-0009: رمزنگاری SQLite در Edge با کلید بیرونی

- Status: Accepted
- Date: 2026-08-16

## زمینه

Edge دادهٔ عملیاتی، journal، outbox، تصویر pending، binding شبکه و credential
verifier محلی را روی SQLite نگه می‌دارد. رمزنگاری صرف volume کنترل application،
rotation قابل آزمون و حفاظت مستقل DB/WAL/SHM/backup را ثابت نمی‌کند. در عین حال
load شدن دو bundle بومی SQLite در processهای integration می‌تواند provider را
ناسازگار یا غیرقابل پیش‌بینی کند.

## تصمیم

- تمام مصرف‌کننده‌های SQLite solution از
  `Microsoft.EntityFrameworkCore.Sqlite.Core 10.0.11` و دقیقاً یک bundle بومی
  `SQLite3MC.PCLRaw.bundle 2.4.0` استفاده می‌کنند؛ `e_sqlite3` هم‌زمان ممنوع است.
- Production Edge فقط با `Edge:Storage:EncryptionEnabled=true` بالا می‌آید.
  کلید در connection string، JSON، Git یا DB ذخیره نمی‌شود؛ provider آن را از
  environment variable نام‌گذاری‌شده (پیش‌فرض `POST_EDGE_SQLITE_KEY`) می‌گیرد.
- کلید باید ۳۲ تا ۲۵۶ character، بدون control character و متعلق به secret
  manager بیرونی باشد. نبود/خرابی کلید fail-closed است.
- rotation با current+previous key انجام می‌شود: WAL checkpoint، بازکردن با کلید
  قبلی، `rekey`، پاک‌سازی pool و اثبات بازخوانی با کلید جدید. کلید قبلی فقط تا
  backup و restart موفق نگه داشته می‌شود.
- backup با SQLite backup API به DB مقصد رمز‌شده ساخته می‌شود. restore تنها از
  backup قابل خواندن با current/previous key و به فایل مقصد تازه مجاز است.
- Core در Production همچنان SQL Server است؛ SQLite Core فقط Testing/Development
  امن را پشتیبانی می‌کند، ولی همان bundle واحد را برای جلوگیری از collision دارد.
- shared-cache connection mode برای Edge ممنوع و single-writer/WAL invariant حفظ
  می‌شود.

## پیامدها

- DB، WAL، SHM و backup بدون کلید قابل خواندن نیستند؛ از دست رفتن همهٔ نسخه‌های
  حفاظت‌شدهٔ کلید، بازیابی را عمداً ناممکن می‌کند.
- rotation یک تغییر دو مرحله‌ای عملیاتی و نیازمند backup/rollback rehearsal است؛
  جایگزینی ناگهانی کلید جاری بدون previous key مجاز نیست.
- image/runtime هر platform باید native asset سازگار را در publish و container
  داشته باشد؛ package graph و vulnerability/deprecation metadata در release gate
  بررسی می‌شوند.
- رمزنگاری application جایگزین ACL، disk encryption، TLS، backup encryption یا
  secret manager نیست؛ لایه‌ای مستقل به آن‌ها اضافه می‌کند.

## alternatives

- فقط full-disk/volume encryption: به‌عنوان دفاع مکمل حفظ شد، ولی rotation و
  backup مستقل application را پوشش نمی‌دهد.
- SQLCipher bundle جدا فقط در Edge: رد شد، چون integration process هم‌زمان Core
  و Edge را load می‌کند و دو native bundle قابل اتکا نیست.
- قراردادن password در connection string/appsettings: به‌دلیل نشت config/log و
  نبود lifecycle بیرونی رد شد.

## اثبات

- تست DB/WAL/SHM نبود header/plaintext sentinel را ثابت می‌کند؛
- کلید اشتباه رد، rekey با کلید جدید پذیرفته و کلید قبلی پس از rotation رد می‌شود؛
- backup/restore و restart persistence با کلید صحیح تست می‌شوند؛
- startup Production بدون encryption/key شکست می‌خورد؛
- package graph نبود `e_sqlite3` و وجود تنها یک SQLite3MC bundle را ثابت می‌کند؛
- full-development E2E همان Edge رمز‌شده را اجرا و پس از restart دوباره بررسی
  می‌کند.
