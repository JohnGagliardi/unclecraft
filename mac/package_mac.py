"""Wraps a `dotnet publish -r osx-*` folder into UncleCraft.app and zips it with Unix permissions.
Windows zip tools drop the executable bit, which macOS needs, so the zip is written here with explicit modes.
Usage: python -I package_mac.py <publish_dir> <arch-label> <icns> <out_zip> <version>"""
import sys
import time
import zipfile
from pathlib import Path

publish, arch, icns, out_zip, version = Path(sys.argv[1]), sys.argv[2], Path(sys.argv[3]), Path(sys.argv[4]), sys.argv[5]

# Info.plist.template is shared with build-mac.sh; @VERSION@ comes from Directory.Build.props via the caller.
PLIST = (Path(__file__).parent / 'Info.plist.template').read_text(encoding='utf-8').replace('@VERSION@', version)

now = time.localtime()[:6]
def add(z, name, data, mode):
    info = zipfile.ZipInfo(name, date_time=now)
    info.create_system = 3                      # Unix, so macOS honours the mode bits
    info.external_attr = (mode & 0xFFFF) << 16
    if name.endswith('/'):
        info.external_attr |= 0x10              # MS-DOS directory flag
    info.compress_type = zipfile.ZIP_DEFLATED
    z.writestr(info, data)

out_zip.parent.mkdir(parents=True, exist_ok=True)
root = 'UncleCraft.app/'
with zipfile.ZipFile(out_zip, 'w') as z:
    for d in ['', 'Contents/', 'Contents/MacOS/', 'Contents/Resources/']:
        add(z, root + d, b'', 0o40755)
    add(z, root + 'Contents/Info.plist', PLIST.encode(), 0o100644)
    add(z, root + 'Contents/PkgInfo', b'APPL????', 0o100644)
    add(z, root + 'Contents/Resources/UncleCraft.icns', icns.read_bytes(), 0o100644)
    for f in sorted(publish.rglob('*')):
        rel = f.relative_to(publish).as_posix()
        if f.is_dir():
            add(z, root + 'Contents/MacOS/' + rel + '/', b'', 0o40755)
            continue
        if rel.endswith(('.pdb', '.xml')):
            continue
        executable = rel == 'UncleCraft' or rel.endswith('.dylib')
        add(z, root + 'Contents/MacOS/' + rel, f.read_bytes(), 0o100755 if executable else 0o100644)
print(f"{out_zip} ({arch}): {out_zip.stat().st_size / 1e6:.1f} MB")
