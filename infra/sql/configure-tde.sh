#!/usr/bin/env bash
set -euo pipefail

test -n "${MSSQL_SA_PASSWORD:-}"
test -n "${POST_SQL_TDE_CERT_PASSWORD:-}"
export SQLCMDPASSWORD="${MSSQL_SA_PASSWORD}"
sqlcmd=/opt/mssql-tools18/bin/sqlcmd
databases=(PostParcelLifecycle PostDeviceConfig PostEdgeHealth PostOperationalResult PostIdentity)

escape_sql() { printf '%s' "$1" | sed "s/'/''/g"; }
tde_password="$(escape_sql "${POST_SQL_TDE_CERT_PASSWORD}")"

attempts=0
while (( attempts < 120 )); do
  count="$(${sqlcmd} -C -S sql-primary -U sa -h -1 -W -Q \
    "SET NOCOUNT ON; SELECT COUNT(*) FROM sys.databases WHERE name IN ('PostParcelLifecycle','PostDeviceConfig','PostEdgeHealth','PostOperationalResult','PostIdentity') AND state_desc = 'ONLINE' AND HAS_DBACCESS(name) = 1" \
    | tr -d '[:space:]')"
  if [[ "${count}" == "5" ]]; then break; fi
  attempts=$((attempts + 1))
  sleep 5
done
if (( attempts == 120 )); then
  echo "Five migrated application databases were not available before the TDE timeout." >&2
  exit 1
fi

rm -f /var/opt/mssql/ag/post_full_dev_tde.cer /var/opt/mssql/ag/post_full_dev_tde.pvk
cat >/tmp/configure-tde.sql <<SQL
USE master;
IF NOT EXISTS (SELECT 1 FROM sys.symmetric_keys WHERE name = '##MS_DatabaseMasterKey##')
  CREATE MASTER KEY ENCRYPTION BY PASSWORD = '${tde_password}';
IF NOT EXISTS (SELECT 1 FROM sys.certificates WHERE name = 'post_full_dev_tde_cert')
  CREATE CERTIFICATE post_full_dev_tde_cert
    WITH SUBJECT = 'post full-dev SQL TDE certificate', EXPIRY_DATE = '20991231';
BACKUP CERTIFICATE post_full_dev_tde_cert
  TO FILE = '/var/opt/mssql/ag/post_full_dev_tde.cer'
  WITH PRIVATE KEY (
    FILE = '/var/opt/mssql/ag/post_full_dev_tde.pvk',
    ENCRYPTION BY PASSWORD = '${tde_password}');
SQL
${sqlcmd} -C -S sql-primary -U sa -b -i /tmp/configure-tde.sql

for database in "${databases[@]}"; do
  attempts=0
  while (( attempts < 120 )); do
    cat >/tmp/enable-tde.sql <<SQL
USE [${database}];
IF NOT EXISTS (SELECT 1 FROM sys.dm_database_encryption_keys WHERE database_id = DB_ID())
  CREATE DATABASE ENCRYPTION KEY WITH ALGORITHM = AES_256
    ENCRYPTION BY SERVER CERTIFICATE post_full_dev_tde_cert;
IF (SELECT is_encrypted FROM sys.databases WHERE name = N'${database}') = 0
  ALTER DATABASE [${database}] SET ENCRYPTION ON;
SQL
    if ${sqlcmd} -C -S sql-primary -U sa -b -i /tmp/enable-tde.sql; then
      break
    fi
    attempts=$((attempts + 1))
    sleep 5
  done
  if (( attempts == 120 )); then
    echo "Database ${database} did not become ready for SQL TDE before timeout." >&2
    exit 1
  fi
done

attempts=0
while (( attempts < 120 )); do
  encrypted="$(${sqlcmd} -C -S sql-primary -U sa -h -1 -W -Q \
    "SET NOCOUNT ON; SELECT COUNT(*) FROM sys.databases WHERE name IN ('PostParcelLifecycle','PostDeviceConfig','PostEdgeHealth','PostOperationalResult','PostIdentity') AND is_encrypted=1" \
    | tr -d '[:space:]')"
  if [[ "${encrypted}" == "5" ]]; then exit 0; fi
  attempts=$((attempts + 1))
  sleep 5
done

echo "All five application databases did not reach SQL TDE encrypted state before timeout." >&2
exit 1
