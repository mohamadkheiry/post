#!/usr/bin/env bash
set -euo pipefail

test -n "${MSSQL_SA_PASSWORD:-}"
test -n "${POST_AG_CERT_PASSWORD:-}"
test -n "${POST_SQL_TDE_CERT_PASSWORD:-}"
export SQLCMDPASSWORD="${MSSQL_SA_PASSWORD}"
sqlcmd=/opt/mssql-tools18/bin/sqlcmd
databases=(PostParcelLifecycle PostDeviceConfig PostEdgeHealth PostOperationalResult PostIdentity)

wait_for_databases() {
  local attempts=0
  while (( attempts < 60 )); do
    local count
    count="$(${sqlcmd} -C -S sql-primary -U sa -h -1 -W -Q \
      "SET NOCOUNT ON; SELECT COUNT(*) FROM sys.databases WHERE name IN ('PostParcelLifecycle','PostDeviceConfig','PostEdgeHealth','PostOperationalResult','PostIdentity')" \
      | tr -d '[:space:]')"
    if [[ "${count}" == "5" ]]; then return 0; fi
    attempts=$((attempts + 1))
    sleep 5
  done
  echo "Five migrated application databases were not available before the AG timeout." >&2
  return 1
}

escape_sql() { printf '%s' "$1" | sed "s/'/''/g"; }
cert_password="$(escape_sql "${POST_AG_CERT_PASSWORD}")"
tde_password="$(escape_sql "${POST_SQL_TDE_CERT_PASSWORD}")"
rm -f /var/opt/mssql/ag/primary.cer /var/opt/mssql/ag/secondary.cer /var/opt/mssql/ag/*.seed.bak

cat >/tmp/primary-endpoint.sql <<SQL
USE master;
IF NOT EXISTS (SELECT 1 FROM sys.symmetric_keys WHERE name = '##MS_DatabaseMasterKey##')
  CREATE MASTER KEY ENCRYPTION BY PASSWORD = '${cert_password}';
IF NOT EXISTS (SELECT 1 FROM sys.certificates WHERE name = 'post_primary_cert')
  CREATE CERTIFICATE post_primary_cert WITH SUBJECT = 'post full-dev primary AG endpoint';
BACKUP CERTIFICATE post_primary_cert TO FILE = '/var/opt/mssql/ag/primary.cer';
IF NOT EXISTS (SELECT 1 FROM sys.database_mirroring_endpoints WHERE name = 'post_hadr_endpoint')
  CREATE ENDPOINT post_hadr_endpoint STATE = STARTED AS TCP (LISTENER_PORT = 5022)
  FOR DATABASE_MIRRORING (AUTHENTICATION = CERTIFICATE post_primary_cert, ENCRYPTION = REQUIRED ALGORITHM AES, ROLE = ALL);
ALTER ENDPOINT post_hadr_endpoint STATE = STARTED;
SQL

cat >/tmp/secondary-endpoint.sql <<SQL
USE master;
IF NOT EXISTS (SELECT 1 FROM sys.symmetric_keys WHERE name = '##MS_DatabaseMasterKey##')
  CREATE MASTER KEY ENCRYPTION BY PASSWORD = '${cert_password}';
IF NOT EXISTS (SELECT 1 FROM sys.certificates WHERE name = 'post_secondary_cert')
  CREATE CERTIFICATE post_secondary_cert WITH SUBJECT = 'post full-dev secondary AG endpoint';
BACKUP CERTIFICATE post_secondary_cert TO FILE = '/var/opt/mssql/ag/secondary.cer';
IF NOT EXISTS (SELECT 1 FROM sys.database_mirroring_endpoints WHERE name = 'post_hadr_endpoint')
  CREATE ENDPOINT post_hadr_endpoint STATE = STARTED AS TCP (LISTENER_PORT = 5022)
  FOR DATABASE_MIRRORING (AUTHENTICATION = CERTIFICATE post_secondary_cert, ENCRYPTION = REQUIRED ALGORITHM AES, ROLE = ALL);
ALTER ENDPOINT post_hadr_endpoint STATE = STARTED;
SQL

${sqlcmd} -C -S sql-primary -U sa -b -i /tmp/primary-endpoint.sql >/dev/null
${sqlcmd} -C -S sql-secondary -U sa -b -i /tmp/secondary-endpoint.sql >/dev/null

cat >/tmp/primary-peer.sql <<SQL
USE master;
IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = 'post_secondary_login')
  CREATE LOGIN post_secondary_login WITH PASSWORD = '${cert_password}', CHECK_POLICY = OFF;
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = 'post_secondary_user')
  CREATE USER post_secondary_user FOR LOGIN post_secondary_login;
IF NOT EXISTS (SELECT 1 FROM sys.certificates WHERE name = 'post_secondary_peer')
  CREATE CERTIFICATE post_secondary_peer AUTHORIZATION post_secondary_user FROM FILE = '/var/opt/mssql/ag/secondary.cer';
GRANT CONNECT ON ENDPOINT::post_hadr_endpoint TO post_secondary_login;
SQL

cat >/tmp/secondary-peer.sql <<SQL
USE master;
IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = 'post_primary_login')
  CREATE LOGIN post_primary_login WITH PASSWORD = '${cert_password}', CHECK_POLICY = OFF;
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = 'post_primary_user')
  CREATE USER post_primary_user FOR LOGIN post_primary_login;
IF NOT EXISTS (SELECT 1 FROM sys.certificates WHERE name = 'post_primary_peer')
  CREATE CERTIFICATE post_primary_peer AUTHORIZATION post_primary_user FROM FILE = '/var/opt/mssql/ag/primary.cer';
GRANT CONNECT ON ENDPOINT::post_hadr_endpoint TO post_primary_login;
SQL

${sqlcmd} -C -S sql-primary -U sa -b -i /tmp/primary-peer.sql >/dev/null
${sqlcmd} -C -S sql-secondary -U sa -b -i /tmp/secondary-peer.sql >/dev/null
wait_for_databases

cat >/tmp/secondary-tde.sql <<SQL
USE master;
IF NOT EXISTS (SELECT 1 FROM sys.certificates WHERE name = 'post_full_dev_tde_cert')
  CREATE CERTIFICATE post_full_dev_tde_cert
    FROM FILE = '/var/opt/mssql/ag/post_full_dev_tde.cer'
    WITH PRIVATE KEY (
      FILE = '/var/opt/mssql/ag/post_full_dev_tde.pvk',
      DECRYPTION BY PASSWORD = '${tde_password}');
SQL
${sqlcmd} -C -S sql-secondary -U sa -b -i /tmp/secondary-tde.sql >/dev/null

cat >/tmp/create-ag.sql <<'SQL'
USE master;
IF NOT EXISTS (SELECT 1 FROM sys.availability_groups WHERE name = 'post_dev_ag')
BEGIN
  CREATE AVAILABILITY GROUP post_dev_ag WITH (CLUSTER_TYPE = NONE)
  FOR REPLICA ON
    N'sql-primary' WITH (
      ENDPOINT_URL = N'TCP://sql-primary:5022',
      AVAILABILITY_MODE = SYNCHRONOUS_COMMIT,
      FAILOVER_MODE = MANUAL,
      SEEDING_MODE = AUTOMATIC,
      SECONDARY_ROLE (ALLOW_CONNECTIONS = ALL)),
    N'sql-secondary' WITH (
      ENDPOINT_URL = N'TCP://sql-secondary:5022',
      AVAILABILITY_MODE = SYNCHRONOUS_COMMIT,
      FAILOVER_MODE = MANUAL,
      SEEDING_MODE = AUTOMATIC,
      SECONDARY_ROLE (ALLOW_CONNECTIONS = ALL));
END
SQL
${sqlcmd} -C -S sql-primary -U sa -b -i /tmp/create-ag.sql >/dev/null

cat >/tmp/join-ag.sql <<'SQL'
USE master;
IF NOT EXISTS (SELECT 1 FROM sys.availability_groups WHERE name = 'post_dev_ag')
  ALTER AVAILABILITY GROUP post_dev_ag JOIN WITH (CLUSTER_TYPE = NONE);
ALTER AVAILABILITY GROUP post_dev_ag GRANT CREATE ANY DATABASE;
SQL
${sqlcmd} -C -S sql-secondary -U sa -b -i /tmp/join-ag.sql >/dev/null

for database in "${databases[@]}"; do
  in_group="$(${sqlcmd} -C -S sql-primary -U sa -h -1 -W -Q \
    "SET NOCOUNT ON; SELECT COUNT(*) FROM sys.availability_databases_cluster WHERE database_name=N'${database}'" \
    | tr -d '[:space:]')"
  if [[ "${in_group}" == "0" ]]; then
    ${sqlcmd} -C -S sql-primary -U sa -b -Q \
      "ALTER DATABASE [${database}] SET RECOVERY FULL; BACKUP DATABASE [${database}] TO DISK=N'/var/opt/mssql/ag/${database}.seed.bak' WITH INIT, COMPRESSION; ALTER AVAILABILITY GROUP post_dev_ag ADD DATABASE [${database}];" >/dev/null
  fi
done

attempts=0
while (( attempts < 60 )); do
  synchronized="$(${sqlcmd} -C -S sql-primary -U sa -h -1 -W -Q \
    "SET NOCOUNT ON; SELECT COUNT(*) FROM sys.dm_hadr_database_replica_states WHERE is_local=0 AND synchronization_state_desc='SYNCHRONIZED'" \
    | tr -d '[:space:]')"
  if [[ "${synchronized}" == "5" ]]; then exit 0; fi
  attempts=$((attempts + 1))
  sleep 5
done

echo "Always On AG formed, but all five secondary databases did not synchronize before timeout." >&2
exit 1
