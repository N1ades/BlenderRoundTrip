"""Build the portable Blender extension shipped with Round Trip."""
from pathlib import Path
from zipfile import ZIP_DEFLATED, ZipFile

folder = Path(__file__).resolve().parent
with ZipFile(folder / 'nyades_ft_toolkit.zip', 'w', ZIP_DEFLATED) as archive:
    for name in ('__init__.py', 'osc.py', 'live_preview.py', 'blender_manifest.toml', 'README.md', 'LICENSE'):
        archive.write(folder / 'FTToolkit' / name, name)
print('FT_TOOLKIT_ZIP_OK')
