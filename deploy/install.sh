#!/usr/bin/env bash
#
# DEPRECATED — kept as a thin shim for muscle memory. Use ./deploy/deploy.sh instead.
#
# The canonical path is now deploy/deploy.sh: it builds as the invoking (service-owning) user and
# sudo's ONLY the systemd steps (the old install.sh published as root, which left root-owned
# obj/bin in the source tree), rewrites the unit's User=/Group= to your user, and verifies a real
# GET /health over the metrics socket. deploy.sh always enables + starts the unit, so the old
# `--enable` flag is now the default and is ignored here.
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
