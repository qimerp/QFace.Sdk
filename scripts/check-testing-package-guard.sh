#!/usr/bin/env bash
# Packs the Testing package under a throwaway version and proves its QIMTEST001 guard ships and fires.
set -euo pipefail

sdk_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
version="0.0.0-guardcheck.$(date +%s)"
work=$(mktemp -d)
packages_root="${NUGET_PACKAGES:-$HOME/.nuget/packages}"

cleanup() {
  rm -rf "$work" \
    "$packages_root/qimerp.shared.common/$version" \
    "$packages_root/qimerp.shared.common.testing/$version"
}
trap cleanup EXIT

fail() {
  echo "FAIL  $*" >&2
  exit 1
}

zip_entries() {
  if command -v unzip >/dev/null 2>&1; then
    unzip -Z1 "$1"
  else
    python3 -c 'import sys, zipfile; print("\n".join(zipfile.ZipFile(sys.argv[1]).namelist()))' "$1"
  fi
}

zip_read() {
  if command -v unzip >/dev/null 2>&1; then
    unzip -p "$1" "$2"
  else
    python3 -c 'import sys, zipfile; sys.stdout.write(zipfile.ZipFile(sys.argv[1]).read(sys.argv[2]).decode())' "$1" "$2"
  fi
}

targets="src/QimErp.Shared.Common.Testing/build/QimErp.Shared.Common.Testing.targets"
if git -C "$sdk_root" rev-parse --is-inside-work-tree >/dev/null 2>&1 && git -C "$sdk_root" check-ignore -q "$targets"; then
  fail "$targets is ignored by .gitignore, so it never reaches CI"
fi

echo "Packing $version..."
for project in QimErp.Shared.Common QimErp.Shared.Common.Testing; do
  dotnet pack "$sdk_root/src/$project/$project.csproj" -c Release -o "$work/feed" \
    -p:QimErpSharedCommonVersion="$version" -nologo -v q >"$work/pack-$project.log" 2>&1 \
    || { cat "$work/pack-$project.log" >&2; fail "dotnet pack $project"; }
done

nupkg="$work/feed/QimErp.Shared.Common.Testing.$version.nupkg"
[[ -f "$nupkg" ]] || fail "missing $nupkg"

entries=$(zip_entries "$nupkg")
for entry in build/QimErp.Shared.Common.Testing.targets buildTransitive/QimErp.Shared.Common.Testing.targets; do
  grep -qx "$entry" <<<"$entries" || fail "$entry is not in the package"
  echo "ok    package contains $entry"
done

nuspec=$(zip_read "$nupkg" QimErp.Shared.Common.Testing.nuspec)
grep -q "id=\"QimErp.Shared.Common\" version=\"\[$version\]\"" <<<"$nuspec" \
  || fail "QimErp.Shared.Common dependency is not pinned to [$version]"
echo "ok    depends on QimErp.Shared.Common [$version]"

cat >"$work/nuget.config" <<EOF
<configuration>
  <packageSources>
    <clear />
    <add key="guardcheck" value="$work/feed" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
EOF

make_consumer() {
  local name=$1 extra=$2
  mkdir -p "$work/$name"
  cat >"$work/$name/$name.csproj" <<EOF
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    $extra
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="QimErp.Shared.Common.Testing" Version="$version" />
  </ItemGroup>
</Project>
EOF
  echo "namespace GuardCheck; public static class Probe { }" >"$work/$name/Probe.cs"
}

make_consumer production ""
make_consumer tests "<IsTestProject>true</IsTestProject>"

if dotnet build "$work/production/production.csproj" -nologo >"$work/production.log" 2>&1; then
  fail "a non-test consumer built without QIMTEST001"
fi
grep -q "QIMTEST001" "$work/production.log" || { cat "$work/production.log" >&2; fail "non-test build failed for another reason"; }
echo "ok    non-test consumer fails with QIMTEST001"

dotnet build "$work/tests/tests.csproj" -nologo >"$work/tests.log" 2>&1 \
  || { cat "$work/tests.log" >&2; fail "test consumer did not build"; }
echo "ok    test consumer builds"
