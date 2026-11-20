import importlib.util
import pathlib
import unittest
from unittest.mock import Mock

spec = importlib.util.spec_from_file_location("publisher", pathlib.Path(__file__).resolve().parents[1] / "tools/publish-release.py")
publisher = importlib.util.module_from_spec(spec); spec.loader.exec_module(publisher)
upload_spec = importlib.util.spec_from_file_location("uploader", pathlib.Path(__file__).resolve().parents[1] / "tools/upload-release.py")
uploader = importlib.util.module_from_spec(upload_spec); upload_spec.loader.exec_module(uploader)
finalizer_spec = importlib.util.spec_from_file_location("finalizer", pathlib.Path(__file__).resolve().parents[1] / "tools/finalize-staged-release.py")
finalizer = importlib.util.module_from_spec(finalizer_spec); finalizer_spec.loader.exec_module(finalizer)


class ReleaseVersionTests(unittest.TestCase):
    def test_allows_hyphenated_rc_hotfix_version(self):
        for version in ("2.0.0-rc1-hotfix", "2.0.0-rc1-hotfix.2", "2.0.0-rc.1", "2.0.0"):
            with self.subTest(version=version):
                self.assertIsNotNone(finalizer.RELEASE_VERSION.fullmatch(version))

    def test_rejects_path_characters_and_empty_version_segments(self):
        for version in ("2.0.0-rc1-hotfix/../rc.1", "2.0.0-rc1-hotfix\\alt", "2.0.0-rc1-hotfix..2"):
            with self.subTest(version=version):
                self.assertIsNone(finalizer.RELEASE_VERSION.fullmatch(version))


class ReleaseIdentityTests(unittest.TestCase):
    def test_known_draft_id_does_not_depend_on_tag_listing(self):
        session = Mock()
        session.get.return_value.json.return_value = {"id": 42, "tag_name": "v2.0.0-beta.13", "draft": True}
        result = uploader.resolve_release(session, "owner/repo", "v2.0.0-beta.13", 42)
        self.assertTrue(result["draft"])
        session.get.assert_called_once_with("https://api.github.com/repos/owner/repo/releases/42", timeout=uploader.TIMEOUT)

    def test_known_id_cannot_upload_into_another_version(self):
        session = Mock()
        session.get.return_value.json.return_value = {"id": 42, "tag_name": "v2.0.0-beta.12"}
        with self.assertRaises(RuntimeError):
            uploader.resolve_release(session, "owner/repo", "v2.0.0-beta.13", 42)


class PublishedManifestTests(unittest.TestCase):
    def setUp(self):
        self.name = "OrC-KSD-2.0.0-beta.12-win-x64.zip"
        self.release = {"draft": False, "tag_name": "v2.0.0-beta.12", "body": "说明", "assets": [{"name": self.name, "size": 123, "browser_download_url": "https://github.com/Shangt1145/Godot-orcKsd/releases/download/v2.0.0-beta.12/" + self.name}]}

    def test_public_metadata_produces_real_version_download_url(self):
        result = publisher.published_manifest(self.release, [self.name])
        self.assertEqual(result["assets"][0]["url"], self.release["assets"][0]["browser_download_url"])
        self.assertEqual(result["tag"], "v2.0.0-beta.12")

    def test_draft_cannot_become_update_channel(self):
        self.release["draft"] = True
        with self.assertRaises(RuntimeError): publisher.published_manifest(self.release, [self.name])

    def test_old_draft_link_after_publication_is_rejected(self):
        self.release["assets"][0]["browser_download_url"] = self.release["assets"][0]["browser_download_url"].replace("v2.0.0-beta.12", "untagged-717e82c5578b928eb1f4")
        with self.assertRaises(RuntimeError): publisher.published_manifest(self.release, [self.name])

    def test_missing_asset_cannot_silently_disappear_from_channel(self):
        with self.assertRaises(KeyError): publisher.published_manifest(self.release, [self.name, "missing.apk"])
