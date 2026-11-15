#!/usr/bin/env python3
"""Build a theme ZIP and its independent GitHub release manifest."""
import argparse, hashlib, json, re, zipfile
from pathlib import Path
p = argparse.ArgumentParser()
p.add_argument('--version', required=True)
p.add_argument('--min-host-version', default='0.1.0')
p.add_argument('--output', default='artifacts/theme')
a = p.parse_args()
if not re.fullmatch(r'\d+\.\d+\.\d+', a.version):
    p.error('version must be major.minor.patch')
root = Path(__file__).resolve().parents[1]
out = Path(a.output).resolve()
out.mkdir(parents=True, exist_ok=True)
name = f'pond-garden-{a.version}.zip'
with zipfile.ZipFile(out / name, 'w', zipfile.ZIP_DEFLATED) as archive:
    for source in sorted((root / 'themes/pond-garden').iterdir()):
        if source.is_file():
            archive.write(source, source.name)
blob = (out / name).read_bytes()
manifest = dict(schema=1, id='pond-garden', version=a.version,
                minHostVersion=a.min_host_version, saveSchema=2,
                url=f'https://github.com/qshanx/wildflower-desktop/releases/download/theme-pond-garden-{a.version}/{name}',
                size=len(blob), sha256=hashlib.sha256(blob).hexdigest())
(out / 'theme.json').write_text(json.dumps(manifest, indent=2) + '\n')
print(json.dumps(manifest, indent=2))
