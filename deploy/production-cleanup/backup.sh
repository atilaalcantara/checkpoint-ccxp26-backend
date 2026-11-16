#!/usr/bin/env bash
# Prepared only. Run with bash, never bash -x: exports sensitive database data.
set -euo pipefail
test "${HLG_VALIDATED:-}" = yes || { printf '%s\n' 'HLG validation required before backup' >&2; exit 2; }
test "${CONFIRM_BACKUP:-}" = EXPORT_CHECKPOINT || { printf '%s\n' 'Explicit CONFIRM_BACKUP required' >&2; exit 2; }
test -n "${BACKUP_DIR:-}" && [[ "$BACKUP_DIR" = /* ]] && test -d "$BACKUP_DIR" || {
  printf '%s\n' 'BACKUP_DIR must be an existing absolute PRIVATE directory outside all repositories' >&2; exit 2;
}
backup_dir="$(cd -- "$BACKUP_DIR" && pwd -P)"
if git -C "$backup_dir" rev-parse --show-toplevel >/dev/null 2>&1; then
  printf '%s\n' 'Refusing backup inside a Git worktree' >&2; exit 2
fi
command -v pg_restore >/dev/null || { printf '%s\n' 'Local PostgreSQL 16 pg_restore required' >&2; exit 2; }
[[ "$(pg_restore --version)" = *' 16.'* ]] || { printf '%s\n' 'Use PostgreSQL 16 pg_restore' >&2; exit 2; }
umask 077
chmod 700 "$backup_dir"
bundle="$(mktemp -d "$backup_dir/checkpoint-$(date -u +%Y%m%dT%H%M%SZ)-XXXXXX")"
chmod 700 "$bundle"
complete=no
trap 'if [[ "$complete" != yes ]]; then printf "INCOMPLETE backup retained privately; do not use: %s\n" "$bundle" >&2; fi' EXIT
# Remote target is PRD, not checkpoint_hlg. No dependency on the SSH login cwd,
# deploy script location, remote .env or a remote dump file. Stream via no-PTY SSH.
# bytea original/thumbnail and all admin_passkeys columns are ordinary table data.
ssh -T -i "$HOME/.ssh/ssh-key-2026-05-22.key" -o BatchMode=yes -o StrictHostKeyChecking=yes -o IdentitiesOnly=yes ubuntu@oracle.atilaalcantara.com \
  "docker exec -e PGOPTIONS='-c default_transaction_read_only=on -c lock_timeout=5000' personal-postgres pg_dump -U postgres -d personal_db --schema=checkpoint --strict-names --format=custom --no-owner --no-acl" \
  > "$bundle/checkpoint.dump" 2> "$bundle/backup.log"
test -s "$bundle/checkpoint.dump"
pg_restore --list "$bundle/checkpoint.dump" > "$bundle/restore.list" 2>> "$bundle/backup.log"
# Require data entries for photos, markers and authentication, not only schema DDL.
for table in photos photo_submissions admin_users admin_passkeys; do
  found=no
  while IFS= read -r entry; do
    if [[ "$entry" = *" TABLE DATA checkpoint $table "* ]]; then found=yes; break; fi
  done < "$bundle/restore.list"
  if [[ "$found" != yes ]]; then
    printf 'Missing TABLE DATA entry: %s\n' "$table" >&2; exit 1
  fi
done
(cd -- "$bundle" && shasum -a 256 checkpoint.dump restore.list > SHA256SUMS)
chmod 600 "$bundle/checkpoint.dump" "$bundle/restore.list" "$bundle/backup.log" "$bundle/SHA256SUMS"
complete=yes
printf 'Backup local privado (NOT YET restore-verified): %s\n' "$bundle"
printf '%s\n' 'Validate SHA256SUMS and restore in isolated PostgreSQL 16 per README before authorizing cleanup.'
