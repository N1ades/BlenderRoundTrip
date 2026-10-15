"""Publish versioned Git installs and merge the VCC index without losing older versions."""
import argparse
import json
import os
from pathlib import Path
import subprocess
import tempfile

REPOSITORY = "N1ades/BlenderRoundTrip"
LISTING_URL = f"https://raw.githubusercontent.com/{REPOSITORY}/vpm/index.json"


def git(*args, env=None, check=True):
    return subprocess.run(["git", *args], check=check, text=True, capture_output=True, env=env)


def remote_head(branch):
    result = git("ls-remote", "--heads", "origin", f"refs/heads/{branch}")
    if not result.stdout.strip():
        return None
    git("fetch", "--no-tags", "origin", f"refs/heads/{branch}")
    return git("rev-parse", "FETCH_HEAD").stdout.strip()


def commit_directory(directory, parent, message):
    with tempfile.TemporaryDirectory() as temp:
        env = dict(os.environ, GIT_INDEX_FILE=str(Path(temp) / "index"))
        git("read-tree", "--empty", env=env)
        git("--work-tree=" + str(directory.resolve()), "add", "--all", "--force", env=env)
        tree = git("write-tree", env=env).stdout.strip()
        parents = ["-p", parent] if parent else []
        return git("commit-tree", tree, *parents, "-m", message).stdout.strip()


def package_git(staging, version, source):
    # A release tag always contains the actual versioned package, including the Blender ZIP.
    tag = f"v{version}"
    existing = git("ls-remote", "--tags", "origin", f"refs/tags/{tag}").stdout.strip()
    if existing:
        return
    for _ in range(5):
        parent = remote_head("upm")
        current = None
        if parent:
            current = json.loads(git("show", f"{parent}:package.json").stdout)["version"]
        newer = current is None or tuple(map(int, version.split("."))) > tuple(map(int, current.split(".")))
        commit = commit_directory(staging, parent or source, f"Package {tag} (source {source})")
        refs = [f"{commit}:refs/tags/{tag}"]
        if newer:
            refs.append(f"{commit}:refs/heads/upm")
        result = git("push", "--atomic", "origin", *refs, check=False)
        if result.returncode == 0:
            print(f"Published Git package {tag}")
            return
    raise RuntimeError("Could not publish package branch after concurrent updates")


def listing(manifest_path):
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    for _ in range(5):
        parent = remote_head("vpm")
        if parent:
            index = json.loads(git("show", f"{parent}:index.json").stdout)
        else:
            index = {"name": "Nyades Blender Round Trip", "id": "dev.nyades.blender-round-trip.listing",
                     "url": LISTING_URL, "author": "Nyades", "packages": {}}
        versions = index["packages"].setdefault(manifest["name"], {"versions": {}})["versions"]
        if manifest["version"] in versions:
            return
        versions[manifest["version"]] = manifest
        with tempfile.TemporaryDirectory() as temp:
            folder = Path(temp)
            (folder / "index.json").write_text(json.dumps(index, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
            commit = commit_directory(folder, parent, f"List {manifest['name']} {manifest['version']}")
        result = git("push", "origin", f"{commit}:refs/heads/vpm", check=False)
        if result.returncode == 0:
            print("Updated VCC listing: " + LISTING_URL)
            return
    raise RuntimeError("Could not update VCC index after concurrent updates")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    package = sub.add_parser("package-git")
    package.add_argument("--staging", type=Path, required=True)
    package.add_argument("--version", required=True)
    package.add_argument("--source", required=True)
    index = sub.add_parser("listing")
    index.add_argument("--manifest", type=Path, required=True)
    args = parser.parse_args()
    if args.command == "package-git":
        package_git(args.staging, args.version, args.source)
    else:
        listing(args.manifest)
