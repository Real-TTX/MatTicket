#!/usr/bin/env bash
# Rebuilds the MatTicket container and starts the local stack (docker-compose.local.yml). --clean removes the volumes first.
set -euo pipefail
cd "$(dirname "$0")/.."
if [ "${1:-}" = "--clean" ]; then docker compose -f docker-compose.local.yml down -v --remove-orphans; fi
docker compose -f docker-compose.local.yml build --build-arg "BuildDate=$(date -u +%Y%m%d)"
docker compose -f docker-compose.local.yml up -d --remove-orphans
echo "MatTicket laeuft auf http://localhost:2740 (pgAdmin: http://localhost:2741)"
