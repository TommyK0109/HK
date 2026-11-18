#!/usr/bin/env bash
set -euo pipefail

# Run from the solution directory. The database name matches the staging runbook.
backup_dir="${1:?Provide an existing backup directory}"
recipient="${HKSHOP_AGE_RECIPIENT:?Set the age public key for backup encryption}"
stamp="$(date -u +%Y%m%dT%H%M%SZ)"
work="$(mktemp -d)"
trap 'rm -rf -- "$work"' EXIT

compose=(docker compose --env-file deploy/.env -f deploy/compose.yaml --profile staging)
"${compose[@]}" exec -T sql sh -eu -c '
    mkdir -p /var/opt/mssql/backup
    export SQLCMDPASSWORD="$MSSQL_SA_PASSWORD"
    exec /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -b' <<'SQL'
BACKUP DATABASE [HKShop] TO DISK = N'/var/opt/mssql/backup/HKShop.bak' WITH COPY_ONLY, INIT, CHECKSUM;
RESTORE VERIFYONLY FROM DISK = N'/var/opt/mssql/backup/HKShop.bak' WITH CHECKSUM;
GO
SQL
"${compose[@]}" cp sql:/var/opt/mssql/backup/HKShop.bak "$work/HKShop.bak"
"${compose[@]}" run --rm -T --no-deps --entrypoint tar app -C /var/lib/hkshop -czf - media keys > "$work/app-data.tar.gz"
tar -C "$work" -czf - HKShop.bak app-data.tar.gz | age -r "$recipient" -o "$backup_dir/hkshop-$stamp.tar.gz.age"
"${compose[@]}" exec -T sql rm -f /var/opt/mssql/backup/HKShop.bak
find "$backup_dir" -maxdepth 1 -type f -name 'hkshop-*.tar.gz.age' -mtime +6 -delete
