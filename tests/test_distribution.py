import ast
import contextlib
import hashlib
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import tarfile
import tempfile
import tomllib
import unittest
from zipfile import ZipFile

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts"))
import build_release as build
import publish


class DistributionTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temp = tempfile.TemporaryDirectory()
        cls.output = Path(cls.temp.name) / "dist"
        with contextlib.redirect_stdout(io.StringIO()):
            cls.artifacts = build.build("1.3.42", cls.output)

    @classmethod
    def tearDownClass(cls):
        cls.temp.cleanup()

    def test_upm_and_vpm_package(self):
        name = "dev.nyades.blender-round-trip-1.3.42.zip"
        with ZipFile(io.BytesIO(self.artifacts[name])) as archive:
            manifest = json.loads(archive.read("package.json"))
            self.assertEqual(manifest["version"], "1.3.42")
            self.assertEqual(manifest["url"], f"https://github.com/N1ades/BlenderRoundTrip/releases/download/v1.3.42/{name}")
            self.assertEqual(manifest["legacyFolders"][build.INSTALL_PATH], build.ROOT_GUID)
            assembly = json.loads(archive.read("Editor/Nyades.BlenderRoundTrip.Editor.asmdef"))
            self.assertEqual(assembly["includePlatforms"], ["Editor"])
            self.assertIn("Editor/PackageUpdates.cs", archive.namelist())
            for path in archive.namelist():
                self.assertFalse(any(part in path for part in ("__pycache__", ".git/", "tests/", "scripts/", ".pyc")), path)
                self.assertEqual(archive.read(path), (self.output / "package" / path).read_bytes())
                if Path(path).suffix in (".md", ".cs", ".py", ".json", ".toml"):
                    self.assertNotRegex(archive.read(path).decode("utf-8"), r"[\u0400-\u052f]", path)

    def test_blender_versions_and_embedded_zip(self):
        blender = self.artifacts["nyades_ft_toolkit-1.3.42.zip"]
        with ZipFile(io.BytesIO(blender)) as archive:
            manifest = tomllib.loads(archive.read("blender_manifest.toml").decode())
            self.assertEqual(manifest["version"], "1.3.42")
            tree = ast.parse(archive.read("__init__.py"))
            info = next(ast.literal_eval(n.value) for n in tree.body if isinstance(n, ast.Assign) and any(isinstance(t, ast.Name) and t.id == "bl_info" for t in n.targets))
            self.assertEqual(info["version"], (1, 3, 42))
            self.assertIn("LICENSE", archive.namelist())
            self.assertFalse(any(n.endswith(".meta") for n in archive.namelist()))
        with ZipFile(io.BytesIO(self.artifacts["dev.nyades.blender-round-trip-1.3.42.zip"])) as archive:
            self.assertEqual(archive.read("Editor/Blender/nyades_ft_toolkit.zip"), blender)

    def test_unitypackage_preserves_guid_and_contents(self):
        with tarfile.open(fileobj=io.BytesIO(self.artifacts["Nyades-BlenderRoundTrip-1.3.42.unitypackage"]), mode="r:gz") as archive:
            files = {item.name: archive.extractfile(item).read() for item in archive.getmembers()}
        paths = {}
        for path, content in files.items():
            if path.endswith("/pathname"):
                guid = path.split("/")[0]
                self.assertEqual(build.guid_of(files[f"{guid}/asset.meta"]), guid)
                paths[content.decode()] = guid
        source_guid = build.guid_of((build.ROOT / "Editor/BlenderRoundTrip.cs.meta").read_bytes())
        self.assertEqual(paths[build.INSTALL_PATH], build.ROOT_GUID)
        self.assertEqual(paths[build.INSTALL_PATH + "/Editor/BlenderRoundTrip.cs"], source_guid)
        for path, guid in paths.items():
            if f"{guid}/asset" in files:
                relative = path[len(build.INSTALL_PATH) + 1:]
                self.assertEqual(files[f"{guid}/asset"], (self.output / "package" / relative).read_bytes())

    def test_assets_zip_and_checksums(self):
        with ZipFile(io.BytesIO(self.artifacts["Nyades-BlenderRoundTrip-1.3.42-Assets.zip"])) as archive:
            self.assertIn(build.INSTALL_PATH + ".meta", archive.namelist())
            self.assertIn(build.INSTALL_PATH + "/Editor/BlenderRoundTrip.cs", archive.namelist())
        for line in self.artifacts["SHA256SUMS.txt"].decode().splitlines():
            digest, name = line.split("  ", 1)
            self.assertEqual(digest, hashlib.sha256(self.artifacts[name]).hexdigest())

    def test_reproducible_build(self):
        with contextlib.redirect_stdout(io.StringIO()):
            other = build.build("1.3.42", Path(self.temp.name) / "second")
        self.assertEqual(self.artifacts, other)

    def test_rejects_invalid_version_and_duplicate_guids(self):
        for version in ("1.3", "v1.3.0", "1.3.01", "../bad", "1.3.1-beta"):
            with self.assertRaises(ValueError):
                build.version_tuple(version)
        metadata = build.meta("a" * 32)
        with self.assertRaisesRegex(ValueError, "Duplicate GUID"):
            build.unitypackage({"a.cs": b"a", "a.cs.meta": metadata, "b.cs": b"b", "b.cs.meta": metadata})

    def test_git_packages_and_vcc_history(self):
        # Real Git roundtrip against a local bare remote: no network or GitHub credentials.
        with tempfile.TemporaryDirectory() as temp:
            folder = Path(temp)
            remote = folder / "remote.git"
            checkout = folder / "checkout"
            def run(*args, cwd=folder):
                return subprocess.run(["git", *args], cwd=cwd, text=True, capture_output=True, check=True).stdout.strip()
            run("init", "--bare", str(remote))
            run("init", "-b", "main", str(checkout))
            run("config", "user.name", "Test", cwd=checkout)
            run("config", "user.email", "test@example.invalid", cwd=checkout)
            run("remote", "add", "origin", str(remote), cwd=checkout)
            (checkout / "source.txt").write_text("source")
            run("add", ".", cwd=checkout)
            run("commit", "-m", "source", cwd=checkout)
            source = run("rev-parse", "HEAD", cwd=checkout)
            previous = Path.cwd()
            try:
                os.chdir(checkout)
                with contextlib.redirect_stdout(io.StringIO()):
                    publish.package_git(self.output / "package", "1.3.42", source)
                    publish.package_git(self.output / "package", "1.3.42", source)  # Safe retry.
                    publish.listing(self.output / "package.json")
                    older_output = folder / "older"
                    build.build("1.3.41", older_output)
                    older = json.loads((older_output / "package.json").read_text())
                    publish.package_git(older_output / "package", "1.3.41", source)
                    publish.listing(older_output / "package.json")
                index = json.loads(run("--git-dir=" + str(remote), "show", "vpm:index.json"))
                self.assertEqual(set(index["packages"][older["name"]]["versions"]), {"1.3.41", "1.3.42"})
                self.assertEqual(json.loads(run("--git-dir=" + str(remote), "show", "upm:package.json"))["version"], "1.3.42")
                self.assertEqual(json.loads(run("--git-dir=" + str(remote), "show", "v1.3.42:package.json"))["version"], "1.3.42")
                self.assertEqual(json.loads(run("--git-dir=" + str(remote), "show", "v1.3.41:package.json"))["version"], "1.3.41")
                self.assertEqual(run("rev-parse", "HEAD", cwd=checkout), source)  # Never mutate main or its index.
                self.assertFalse(run("status", "--porcelain", cwd=checkout))
            finally:
                os.chdir(previous)


if __name__ == "__main__":
    unittest.main()
