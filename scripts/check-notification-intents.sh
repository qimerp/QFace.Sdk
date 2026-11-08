#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 1 ]]; then
  echo "usage: $0 <template-repo-dir>" >&2
  exit 2
fi

template_repo=$(cd "$1" && pwd)
sdk_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
notifications="$sdk_root/src/QimErp.Shared.Common/Services/Notifications"
status=0

check() {
  local expected=$1 actual=$2
  if cmp -s "$expected" "$actual"; then
    echo "ok    ${actual#"$sdk_root/"}"
  else
    echo "DRIFT ${actual#"$sdk_root/"} differs from $expected" >&2
    status=1
  fi
}

check "$template_repo/dist/sdk/NotificationIntents.g.cs" "$notifications/NotificationIntents.g.cs"
check "$template_repo/intents.lock.json" "$notifications/NotificationIntents.lock.json"

exit $status
