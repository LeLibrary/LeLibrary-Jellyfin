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
import datetime,hashlib,json,os,pathlib,re,urllib.parse,xml.etree.ElementTree as ET,zipfile
root=pathlib.Path(os.environ['STANDALONE_REPO_ROOT']);out=pathlib.Path(os.environ['STANDALONE_REPO_OUT'])
base=os.environ.get('STANDALONE_REPO_BASE','http://127.0.0.1:8098').rstrip('/')
release=os.environ.get('STANDALONE_REPO_RELEASE') == '1'
previous=os.environ.get('STANDALONE_EXISTING_MANIFEST')
if release and (urllib.parse.urlparse(base).scheme != 'https' or not previous):
 raise SystemExit('Release packaging requires an HTTPS STANDALONE_REPO_BASE and STANDALONE_EXISTING_MANIFEST')
providers=[('TorBox','TB','eedbc5ae-56c4-4145-9a2b-c12716963c95'),('RealDebrid','RD','6d1185f2-1db4-4f9d-bca8-1cb24aa1fdb7'),('AllDebrid','AD','7aa38d7a-bd3f-4b1d-81be-49f9cc1bf09d'),('Premiumize','PM','d0c65588-3905-487d-989b-d0d6b601b01d')]
manifest=json.loads(pathlib.Path(previous).read_text()) if previous else []
if not isinstance(manifest,list): raise SystemExit('Existing manifest must contain an array')
ids={p[2].lower() for p in providers}
prior={str(entry.get('guid','')).lower(): entry for entry in manifest if str(entry.get('guid','')).lower() in ids}
manifest=[entry for entry in manifest if str(entry.get('guid','')).lower() not in ids]
for code,label,guid in providers:
 project=root/f'Jellyfin.Plugin.Standalone.{code}'; dll=f'Jellyfin.Plugin.Standalone.{code}.dll'
 csproj=ET.parse(project/f'Jellyfin.Plugin.Standalone.{code}.csproj')
 version=csproj.findtext('./PropertyGroup/Version')
 if not version: raise SystemExit(f'Missing version for {code}')
 refs=[p.get('Version') for p in csproj.findall('.//PackageReference') if p.get('Include') == 'Jellyfin.Controller']
 if len(refs) != 1 or not re.fullmatch(r'\d+\.\d+\.\d+',refs[0] or ''):
  raise SystemExit(f'Cannot determine Jellyfin API version for {code}')
 abi=refs[0]+'.0'
 old_versions=prior.get(guid.lower(),{}).get('versions',[])
 if not isinstance(old_versions,list): raise SystemExit(f'Invalid version history for {code}')
 if release and any(old.get('version') == version for old in old_versions):
  manifest.append(prior[guid.lower()])
  continue
 zipname=f'LeLibrary-{label}-{version}.zip'; archive=out/zipname
 with zipfile.ZipFile(archive,'w',zipfile.ZIP_DEFLATED) as z:
  z.write(project/'bin/Release/net10.0'/dll,dll)
  z.write(project/'thumb.png','thumb.png')
 digest=hashlib.md5(archive.read_bytes(),usedforsecurity=False).hexdigest()
 versions=[{'version':version,'changelog':('Provider plugin release; see release notes.' if release else 'Development test build.'),'targetAbi':abi,'sourceUrl':f'{base}/{zipname}','checksum':digest,'timestamp':datetime.datetime.now(datetime.timezone.utc).strftime('%Y-%m-%dT%H:%M:%SZ')}]
 versions.extend(old for old in old_versions if old.get('version') != version)
 manifest.append({'guid':guid,'name':f'LeLibrary {label}','description':f'Direct {code} connection for Jellyfin.',
  'overview':('Connects directly to the configured provider; Jellyfin fetches metadata and artwork.' if release else 'DEV TEST ONLY.'),
  'owner':'LeLibrary','category':'General','versions':versions})
(out/'manifest.json').write_text(json.dumps(manifest,indent=2)+'\n')
print(('Prepared release repository:' if release else 'Local development repository:'),out)
PY
