"""Build dependency-free, reproducible Unity, VPM/UPM and Blender distributions."""
import argparse
import ast
import gzip
import hashlib
import io
import json
from pathlib import Path
import re
import tarfile
import tomllib
from zipfile import ZipFile, ZipInfo, ZIP_DEFLATED

ROOT = Path(__file__).resolve().parents[1]
INSTALL_PATH = "Assets/Nyades/BlenderRoundTrip"
ROOT_GUID = "7b2ca170741df694b932a1e309f21009"
REPOSITORY = "N1ades/BlenderRoundTrip"


def version_tuple(version):
    if not re.fullmatch(r"(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)", version):
        raise ValueError("Version must be major.minor.patch")
    return tuple(map(int, version.split(".")))


def meta(guid, folder=False):
    return (f"fileFormatVersion: 2\nguid: {guid}\n" + ("folderAsset: yes\n" if folder else "") +
            "DefaultImporter:\n  externalObjects: {}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n").encode()


def guid_of(data):
    match = re.search(rb"^guid: ([0-9a-f]{32})\s*$", data, re.M)
    if not match:
        raise ValueError("Missing or invalid Unity GUID")
    return match[1].decode()


def zip_bytes(files):
    buffer = io.BytesIO()
    with ZipFile(buffer, "w", compression=ZIP_DEFLATED, compresslevel=9) as archive:
        for name, data in sorted(files.items()):
            info = ZipInfo(name, (2020, 1, 1, 0, 0, 0))
            info.compress_type = ZIP_DEFLATED
            info.external_attr = 0o100644 << 16
            archive.writestr(info, data)
    return buffer.getvalue()


def source_files(root):
    files = {}
    for path in sorted((root / "Editor").rglob("*")):
        relative = path.relative_to(root)
        if not path.is_file() or any(p.startswith(".") or p == "__pycache__" for p in relative.parts):
            continue
        if path.name == "__pycache__.meta" or path.suffix == ".pyc" or path.name.endswith(".pyc.meta"):
            continue
        if relative.as_posix() == "Editor/Blender/nyades_ft_toolkit.zip":
            continue  # Always rebuild from source; never distribute a stale archive.
        files[relative.as_posix()] = path.read_bytes()
    for name in ("Editor.meta", "README.md", "README.md.meta", "package.json", "package.json.meta"):
        files[name] = (root / name).read_bytes()
    files["Editor/Blender/nyades_ft_toolkit.zip"] = b""  # Replaced with the source build below.
    for name in list(files):
        if name.endswith(".meta"):
            if name[:-5] not in files and not (root / name[:-5]).is_dir():
                raise ValueError(f"Orphan metadata: {name}")
        elif name + ".meta" not in files:
            raise ValueError(f"Missing metadata: {name}")
        if name.endswith(".py"):
            ast.parse(files[name].decode("utf-8-sig"), filename=name)
    return files


def unitypackage(files):
    records = {}
    seen = set()
    folders = {INSTALL_PATH: meta(ROOT_GUID, True)}
    for name, data in files.items():
        if name.endswith(".meta") and name[:-5] not in files:
            folders[f"{INSTALL_PATH}/{name[:-5]}"] = data
    for name, data in files.items():
        if not name.endswith(".meta"):
            path = f"{INSTALL_PATH}/{name}"
            metadata = files[name + ".meta"]
            guid = guid_of(metadata)
            if guid in seen:
                raise ValueError(f"Duplicate GUID: {path}")
            seen.add(guid)
            records[f"{guid}/asset"] = data
            records[f"{guid}/asset.meta"] = metadata
            records[f"{guid}/pathname"] = path.encode()
    for path, metadata in folders.items():
        guid = guid_of(metadata)
        if guid in seen:
            raise ValueError(f"Duplicate folder GUID: {path}")
        seen.add(guid)
        records[f"{guid}/asset.meta"] = metadata
        records[f"{guid}/pathname"] = path.encode()
    buffer = io.BytesIO()
    with gzip.GzipFile(fileobj=buffer, mode="wb", filename="", mtime=0) as compressed:
        with tarfile.open(fileobj=compressed, mode="w", format=tarfile.USTAR_FORMAT) as archive:
            for name, data in sorted(records.items()):
                item = tarfile.TarInfo(name)
                item.size = len(data)
                item.mode = 0o644
                archive.addfile(item, io.BytesIO(data))
    return buffer.getvalue()


def build(version, output, root=ROOT):
    parts = version_tuple(version)
    files = source_files(root)
    manifest = json.loads(files["package.json"])
    manifest["version"] = version
    asset_name = f"{manifest['name']}-{version}.zip"
    manifest["url"] = f"https://github.com/{REPOSITORY}/releases/download/v{version}/{asset_name}"
    files["package.json"] = (json.dumps(manifest, ensure_ascii=False, indent=2) + "\n").encode()
    blender_root = "Editor/Blender/FTToolkit/"
    init_name = blender_root + "__init__.py"
    init, replacements = re.subn(r'("version"\s*:\s*)\(\d+,\s*\d+,\s*\d+\)',
                                 lambda m: m[1] + str(parts), files[init_name].decode(), count=1)
    if replacements != 1:
        raise ValueError("Cannot find Blender bl_info version")
    files[init_name] = init.encode()
    manifest_name = blender_root + "blender_manifest.toml"
    blender_manifest, replacements = re.subn(r'^version = "[^"]+"', f'version = "{version}"',
                                            files[manifest_name].decode(), count=1, flags=re.M)
    if replacements != 1 or tomllib.loads(blender_manifest)["version"] != version:
        raise ValueError("Cannot update Blender extension version")
    files[manifest_name] = blender_manifest.encode()
    blender_files = {name[len(blender_root):]: data for name, data in files.items()
                     if name.startswith(blender_root) and not name.endswith(".meta")}
    blender = zip_bytes(blender_files)
    files["Editor/Blender/nyades_ft_toolkit.zip"] = blender
    output.mkdir(parents=True, exist_ok=True)
    staging = output / "package"
    staging.mkdir(exist_ok=True)
    for name, data in files.items():
        destination = staging / name
        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_bytes(data)
    artifacts = {
        asset_name: zip_bytes(files),
        f"Nyades-BlenderRoundTrip-{version}.unitypackage": unitypackage(files),
        f"Nyades-BlenderRoundTrip-{version}-Assets.zip": zip_bytes({f"{INSTALL_PATH}/{n}": d for n, d in files.items()} | {INSTALL_PATH + ".meta": meta(ROOT_GUID, True)}),
        f"nyades_ft_toolkit-{version}.zip": blender,
        "package.json": files["package.json"],
    }
    artifacts["SHA256SUMS.txt"] = "".join(f"{hashlib.sha256(data).hexdigest()}  {name}\n" for name, data in sorted(artifacts.items())).encode()
    for name, data in artifacts.items():
        (output / name).write_bytes(data)
        print(name)
    return artifacts


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--version", required=True)
    parser.add_argument("--output", type=Path, default=ROOT / "dist")
    args = parser.parse_args()
    build(args.version, args.output)
