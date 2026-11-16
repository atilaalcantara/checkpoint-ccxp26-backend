#!/usr/bin/env bash
# HLG only. Run through bash; no executable bit is needed.
set -euo pipefail
readonly name=checkpoint-hlg-api previous=checkpoint-hlg-api-previous
readonly network=checkpoint-hlg-net envfile=/home/ubuntu/apps/checkpoint-hlg/.env
exec 9>/home/ubuntu/apps/checkpoint-hlg/.api-deploy.lock
flock -n 9 || { echo 'HLG API deploy already running' >&2; exit 1; }

healthy() {
  local status
  for ((i=0; i<50; i++)); do
    status=$(docker inspect -f '{{.State.Health.Status}}' "$name") || return 1
    [[ "$status" == healthy ]] && return 0
    [[ "$status" == unhealthy ]] && return 1
    sleep 3
  done
  return 1
}
rollback() {
  docker inspect "$previous" >/dev/null || return 1
  docker rm -f "$name" >/dev/null 2>&1 || true
  docker rename "$previous" "$name" || return 1
  docker start "$name" >/dev/null || return 1
  healthy
}
if [[ "${1:-}" == --rollback ]]; then rollback; exit; fi
[[ "${1:-}" =~ ^ghcr\.io/atilaalcantara/checkpoint-api:hlg-[0-9a-f]{40}$ ]] || {
  echo 'Only immutable HLG SHA tags are accepted' >&2; exit 1;
}
readonly image=$1
docker network inspect "$network" >/dev/null
docker volume inspect checkpoint-hlg-data-protection >/dev/null
test -r "$envfile"
# Parse Docker env-file format, never source/eval credentials. Fail on duplicates.
seen=' '
target= aspnet= dotnet=Homologation keyring= connection=
shopt -s nocasematch
while IFS= read -r line || [[ -n "$line" ]]; do
  [[ -z "$line" || "$line" == \#* ]] && continue
  [[ "$line" == *=* && "$line" != *$'\r'* ]] || exit 1
  key=${line%%=*}
  [[ "$key" =~ ^[A-Za-z_][A-Za-z0-9_]*$ ]] || exit 1
  [[ "$seen" != *" $key "* ]] || { echo 'Duplicate env key' >&2; exit 1; }
  seen+="$key "
  case "$key" in
    CHECKPOINT_DEPLOY_TARGET) target=${line#*=};;
    ASPNETCORE_ENVIRONMENT) aspnet=${line#*=};;
    DOTNET_ENVIRONMENT) dotnet=${line#*=};;
    DataProtection__KeyRingPath) keyring=${line#*=};;
    ConnectionStrings__Checkpoint) connection=${line#*=};;
  esac
done < "$envfile"
shopt -u nocasematch
if [[ "$target" != homologation || "$aspnet" != Homologation ||
      "$dotnet" != Homologation || "$keyring" != /var/lib/checkpoint-data-protection ]]; then
  echo 'HLG environment/keyring guard failed' >&2
  exit 1
fi
# Dedicated DB AND role are mandatory; do not accept duplicate/alias connection keys.
db_count=0 user_count=0
IFS=';' read -ra parts <<< "$connection"
shopt -s nocasematch
for part in "${parts[@]}"; do
  [[ -z "$part" ]] && continue
  case "$part" in
    Database=checkpoint_hlg) db_count=$((db_count+1));;
    Username=checkpoint_hlg_app) user_count=$((user_count+1));;
    Host=*|Port=*|Password=*|SSL\ Mode=*|Trust\ Server\ Certificate=*|Timeout=*|Pooling=*) ;;
    *) echo 'Noncanonical HLG connection string; consult deploy/HLG.md' >&2; exit 1;;
  esac
done
[[ "$db_count" == 1 && "$user_count" == 1 ]] || {
  echo 'HLG database/role guard failed' >&2; exit 1;
}
shopt -u nocasematch
docker pull "$image"
# Runs only after all guards; failure leaves the current API untouched.
docker run --rm --network "$network" --env-file "$envfile" \
  -e DOTNET_ENVIRONMENT=Homologation -e ASPNETCORE_ENVIRONMENT=Homologation \
  --mount source=checkpoint-hlg-data-protection,target=/var/lib/checkpoint-data-protection \
  "$image" --migrate-only
had_previous=false
if docker inspect "$name" >/dev/null 2>&1; then
  docker rm -f "$previous" >/dev/null 2>&1 || true
  docker stop "$name" >/dev/null
  if ! docker rename "$name" "$previous"; then
    docker start "$name" >/dev/null
    exit 1
  fi
  had_previous=true
fi
recover() {
  rc=$?
  trap - EXIT
  if ((rc != 0)); then
    docker logs --tail 80 "$name" >&2 2>/dev/null || true
    if "$had_previous"; then rollback || echo 'HLG rollback failed; operator action required' >&2;
    else docker rm -f "$name" >/dev/null 2>&1 || true; fi
  fi
  exit "$rc"
}
trap recover EXIT
docker run -d --name "$name" --restart unless-stopped --network "$network" \
  --env-file "$envfile" -e DOTNET_ENVIRONMENT=Homologation -e ASPNETCORE_ENVIRONMENT=Homologation \
  --mount source=checkpoint-hlg-data-protection,target=/var/lib/checkpoint-data-protection \
  --memory 768m --cpus 1 "$image" >/dev/null
healthy
echo "HLG API healthy: $image"
