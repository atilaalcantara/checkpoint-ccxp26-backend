#!/usr/bin/env bash
# Thin local client; the server's forced command owns guards, lock and rollback.
set -euo pipefail
usage() { echo 'Usage: bash deploy/hlg.sh status|rollback-api|deploy-api IMAGE' >&2; exit 2; }
case "${1:-}" in
  status|rollback-api) [[ $# == 1 ]] || usage ;;
  deploy-api)
    [[ $# == 2 && "$2" =~ ^ghcr\.io/atilaalcantara/checkpoint-api:hlg-[0-9a-f]{40}$ ]] || usage ;;
  *) usage ;;
esac
exec ssh -o BatchMode=yes -o IdentitiesOnly=yes -o StrictHostKeyChecking=yes \
  -o ConnectTimeout=15 checkpoint-hlg-deploy@oracle.atilaalcantara.com "$@" </dev/null
