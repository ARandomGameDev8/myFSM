"""Tests for retaining project-local Unity GUIDs across clean package updates."""
import importlib.util
from pathlib import Path
import shutil
import tempfile
import unittest


INSTALLER_PATH = Path(__file__).resolve().parents[1] / "install.py"
SPEC = importlib.util.spec_from_file_location("myfsm_unity_installer", str(INSTALLER_PATH))
installer = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(installer)


class MetaPreservationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.payload = self.root / "payload"
        self.destination = self.root / "Assets" / "MyFSM"
        self.source_script = self.payload / "Runtime" / "Core" / "AIInstance.cs"
        self.source_script.parent.mkdir(parents=True)
        self.source_script.write_text("// current runtime\n", encoding="utf-8")
        self.manifest = {"files": ["Runtime/"], "exclude": ["Runtime/Sandbox"]}

    def tearDown(self):
        self.temp.cleanup()

    def write_destination_meta(self, relative, contents):
        path = self.destination / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(contents)
        return path

    def test_clean_reinstall_keeps_matching_guids_and_drops_orphans(self):
        script_meta = self.write_destination_meta(
            "Runtime/Core/AIInstance.cs.meta", b"guid: original-script-guid\n")
        folder_meta = self.write_destination_meta(
            "Runtime.meta", b"guid: original-runtime-folder-guid\n")
        stale_meta = self.write_destination_meta(
            "Runtime/Removed.cs.meta", b"guid: stale-guid\n")
        sandbox_meta = self.write_destination_meta(
            "Runtime/Sandbox/Probe.cs.meta", b"guid: excluded-guid\n")

        saved = installer.collect_existing_meta_files(str(self.destination))
        shutil.rmtree(self.destination)
        copied = installer.copy_entries(str(self.payload), self.manifest, str(self.destination))
        restored = installer.restore_matching_meta_files(
            saved, str(self.payload), self.manifest, str(self.destination))

        self.assertEqual(copied, 1)
        self.assertEqual(restored, 2)
        self.assertEqual(script_meta.read_bytes(), b"guid: original-script-guid\n")
        self.assertEqual(folder_meta.read_bytes(), b"guid: original-runtime-folder-guid\n")
        self.assertFalse(stale_meta.exists())
        self.assertFalse(sandbox_meta.exists())

    def test_existing_project_guids_win_over_payload_sidecars(self):
        self.write_destination_meta(
            "Runtime/Core/AIInstance.cs.meta", b"guid: old-script-guid\n")
        self.write_destination_meta(
            "Runtime.meta", b"guid: old-folder-guid\n")
        (self.source_script.parent / "AIInstance.cs.meta").write_text(
            "guid: payload-script-guid\n", encoding="utf-8")
        (self.payload / "Runtime.meta").write_text(
            "guid: payload-folder-guid\n", encoding="utf-8")

        saved = installer.collect_existing_meta_files(str(self.destination))
        shutil.rmtree(self.destination)
        installer.copy_entries(str(self.payload), self.manifest, str(self.destination))
        restored = installer.restore_matching_meta_files(
            saved, str(self.payload), self.manifest, str(self.destination))

        self.assertEqual(restored, 2)
        self.assertEqual(
            (self.destination / "Runtime/Core/AIInstance.cs.meta").read_text(encoding="utf-8"),
            "guid: old-script-guid\n")
        self.assertEqual(
            (self.destination / "Runtime.meta").read_text(encoding="utf-8"),
            "guid: old-folder-guid\n")

    def test_preservation_does_not_create_meta_for_removed_assets(self):
        self.write_destination_meta("Runtime/Removed.cs.meta", b"guid: stale-guid\n")
        saved = installer.collect_existing_meta_files(str(self.destination))
        shutil.rmtree(self.destination)
        installer.copy_entries(str(self.payload), self.manifest, str(self.destination))
        restored = installer.restore_matching_meta_files(
            saved, str(self.payload), self.manifest, str(self.destination))

        self.assertEqual(restored, 0)
        self.assertFalse((self.destination / "Runtime/Removed.cs.meta").exists())


if __name__ == "__main__":
    unittest.main()
