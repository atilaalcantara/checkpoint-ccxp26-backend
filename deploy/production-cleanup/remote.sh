#!/usr/bin/env bash
set -euo pipefail
base="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
ssh_args=(-T -i "$HOME/.ssh/ssh-key-2026-05-22.key" -o BatchMode=yes -o StrictHostKeyChecking=yes -o IdentitiesOnly=yes ubuntu@oracle.atilaalcantara.com)
case "${1:-diagnostic}" in
  diagnostic|dry-run|postcheck)
    # Local absolute input path; remote cwd does not need this package installed.
    # dry-run deliberately sends diagnostic.sql, never cleanup.sql with rollback.
    sql_file="$base/diagnostic.sql"
    if [[ "${1:-diagnostic}" = postcheck ]]; then sql_file="$base/postcheck.sql"; fi
    ssh "${ssh_args[@]}" "docker exec -i -e PGOPTIONS='-c default_transaction_read_only=on -c statement_timeout=30000' personal-postgres psql -X -U postgres -d personal_db -v ON_ERROR_STOP=1" < "$sql_file"
    ;;
  cleanup)
    test "${HLG_VALIDATED:-}" = yes || { printf '%s\n' 'HLG validation required' >&2; exit 2; }
    test "${CONFIRM_CLEANUP:-}" = DELETE_POLLS_VOTES_UPLOADED_PHOTOS || { printf '%s\n' 'New explicit user authorization required before deletion' >&2; exit 2; }
    test "${BACKUP_VERIFIED:-}" = yes || { printf '%s\n' 'Verified restorable backup required' >&2; exit 2; }
    ssh "${ssh_args[@]}" "docker exec -i personal-postgres psql -X -U postgres -d personal_db -v ON_ERROR_STOP=1" < "$base/cleanup.sql"
    ;;
  *) printf '%s\n' 'Usage: bash remote.sh diagnostic|dry-run|postcheck|cleanup' >&2; exit 2 ;;
esac
