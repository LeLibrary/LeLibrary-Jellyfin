#!/usr/bin/env bash
# Build a local-only repository for integration testing; this does not publish.
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
out="${STANDALONE_REPO_OUT:-$root/artifacts/standalone-repo}"
dotnet="${DOTNET:-dotnet}"
mkdir -p "$out"
for provider in TorBox RealDebrid AllDebrid Premiumize; do
  "$dotnet" build "$root/Jellyfin.Plugin.Standalone.$provider/Jellyfin.Plugin.Standalone.$provider.csproj" -c Release --nologo --verbosity quiet
done
STANDALONE_REPO_ROOT="$root" STANDALONE_REPO_OUT="$out" python3 - <<'PY'
import hashlib,json,os,pathlib,zipfile
root=pathlib.Path(os.environ['STANDALONE_REPO_ROOT']);out=pathlib.Path(os.environ['STANDALONE_REPO_OUT'])
base=os.environ.get('STANDALONE_REPO_BASE','http://127.0.0.1:8098').rstrip('/')
providers=[('TorBox','TorBox','eedbc5ae-56c4-4145-9a2b-c12716963c95'),('RealDebrid','Real-Debrid','6d1185f2-1db4-4f9d-bca8-1cb24aa1fdb7'),('AllDebrid','AllDebrid','7aa38d7a-bd3f-4b1d-81be-49f9cc1bf09d'),('Premiumize','Premiumize','d0c65588-3905-487d-989b-d0d6b601b01d')]
manifest=[]
for code,label,guid in providers:
 project=root/f'Jellyfin.Plugin.Standalone.{code}'; dll=f'Jellyfin.Plugin.Standalone.{code}.dll'; zipname=f'Standalone-{code}-0.0.1.0.zip'; archive=out/zipname
 with zipfile.ZipFile(archive,'w',zipfile.ZIP_DEFLATED) as z:
  z.write(project/'bin/Release/net10.0'/dll,dll)
  z.write(project/'thumb.png','thumb.png')
 digest=hashlib.md5(archive.read_bytes(),usedforsecurity=False).hexdigest()
 manifest.append({'guid':guid,'name':f'Standalone {label}','description':f'Development build for {label}.','overview':'DEV TEST ONLY.','owner':'Standalone','category':'General','versions':[{'version':'0.0.1.0','changelog':'Development test build.','targetAbi':'12.4.0.0','sourceUrl':f'{base}/{zipname}','checksum':digest,'timestamp':'2026-11-11T00:00:00Z'}]})
(out/'manifest.json').write_text(json.dumps(manifest,indent=2)+'\n')
print('Local development repository:',out)
PY
