#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 1 ]]; then
  echo "usage: $0 <template-repo-dir>" >&2
  exit 2
fi

template_repo=$(cd "$1" && pwd)
sdk_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
fixtures="$sdk_root/tests/QimErp.Shared.Common.Tests/Notifications/Fixtures"
commit=1111111111111111111111111111111111111111
source_time=2026-11-07T00:00:00Z
release_id="rel-$commit"

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

# The release tooling writes beside its inputs, so it runs on a copy and never inside the template repo.
mkdir -p "$work/repo"
for item in dist intents intents.lock.json assets config.json scripts; do
  cp -R "$template_repo/$item" "$work/repo/"
done

release() {
  (cd "$work/repo" && PYTHONDONTWRITEBYTECODE=1 python3 -B scripts/release.py "$@")
}

store="local:$work/store"
release admit --storage "$store" --source-commit "$commit" --source-time "$source_time" --apply --principal fixture
release stage --release-id "$release_id" --sequence 1 --source-commit "$commit" --source-time "$source_time" --out-dir "$work/staged"
release upload --storage "$store" --release-id "$release_id" --out-dir "$work/staged" --source-commit "$commit" --source-time "$source_time" --apply --principal fixture
release activate --storage "$store" --release-id "$release_id" --apply --principal fixture

rm -rf "$fixtures/registry" "$fixtures/preview" "$fixtures/intents"
mkdir -p "$fixtures/registry/email-templates" "$fixtures/preview" "$fixtures/intents"
cp -R "$work/store/email-templates/releases" "$fixtures/registry/email-templates/"
cp "$template_repo"/preview/fixtures/* "$fixtures/preview/"
cp "$template_repo"/intents/*.json "$fixtures/intents/"

echo "Refreshed fixtures for $release_id in ${fixtures#"$sdk_root/"}"
