#!/usr/bin/env bash
#
# DEPRECATED — a thin shim for muscle memory. Use ./deploy/deploy.sh instead.
#
# The canonical path is deploy/setup.sh (once per host, asks for sudo) + deploy/deploy.sh (every
# deploy, no sudo, no prompts). deploy.sh builds as the invoking service-owning user, rewrites the
# unit's User=/Group= to that user, enables and starts the unit, and verifies a real GET /health
# over the metrics socket. Any `--enable` argument is ignored: enabling is unconditional.
#
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

if [[ "${EUID:-$(id -u)}" -eq 0 ]]; then
    echo "!! install.sh is deprecated and deploy.sh must NOT run as root." >&2
    echo "   Run it as the service user:  ./deploy/deploy.sh" >&2
    exit 1
fi

echo ">> install.sh is deprecated → running ./deploy/deploy.sh"
exec "$HERE/deploy.sh"
