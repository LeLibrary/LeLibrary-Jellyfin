#!/usr/bin/env bash
# Builds the Jellyfin plugin repository locally: one zip per target plus the
# manifest.json Jellyfin reads. Output: ./artifacts/repo (override with OUT).
#
# Local testing only; the GitHub workflow builds and publishes the same layout.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT="${OUT:-$ROOT/artifacts/repo}"
VERSION="${VERSION:-$(grep -oP '(?<=<Version>)[^<]+' "$ROOT/Jellyfin.Plugin.LeLibrary/Jellyfin.Plugin.LeLibrary.csproj")}"
DOTNET="${DOTNET:-dotnet}"

rm -rf "$OUT"
mkdir -p "$OUT"

# targetAbi is the Jellyfin version each build runs on, not the .NET version.
targets=(
  "net9.0:10.11.0.0"
  "net10.0:12.0.0.0"
)

versions_json=""
for entry in "${targets[@]}"; do
  tfm="${entry%%:*}"
  abi="${entry##*:}"
  work="$ROOT/artifacts/$tfm"
  rm -rf "$work"
  "$DOTNET" publish "$ROOT/Jellyfin.Plugin.LeLibrary/Jellyfin.Plugin.LeLibrary.csproj" \
    -c Release -f "$tfm" -o "$work" --nologo >/dev/null

  name="LeLibrary_${VERSION}_${abi}.zip"
  # Only our assembly goes in the zip: Jellyfin ships its own dependencies, and
  # packing them would shadow the server's.
  (cd "$work" && zip -q "$OUT/$name" Jellyfin.Plugin.LeLibrary.dll)
  checksum="$(md5sum "$OUT/$name" | cut -d' ' -f1)"

  versions_json+=$(printf '%s' "{
      \"version\": \"${VERSION}\",
      \"changelog\": \"See the release notes.\",
      \"targetAbi\": \"${abi}\",
      \"sourceUrl\": \"${SOURCE_BASE:-file://$OUT}/$name\",
      \"checksum\": \"$checksum\",
      \"timestamp\": \"$(date -u +%Y-%m-%dT%H:%M:%SZ)\"
    },")
done

cat > "$OUT/manifest.json" <<EOF
[
  {
    "guid": "8858565b-f937-4d4d-b081-57044455fda1",
    "name": "LeLibrary",
    "description": "Turns your debrid library into Jellyfin media folders.",
    "overview": "LeLibrary for Jellyfin lists your debrid account through LeLibrary's matching and writes playable .strm files. Nothing is downloaded and LeLibrary never sees your key.",
    "owner": "LeLibrary",
    "category": "General",
    "imageUrl": "",
    "versions": [
      ${versions_json%,}
    ]
  }
]
EOF

echo "repo written to $OUT"
ls -l "$OUT"
