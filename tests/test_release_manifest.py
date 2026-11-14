import importlib.util
import pathlib
import unittest

spec = importlib.util.spec_from_file_location("publisher", pathlib.Path(__file__).resolve().parents[1] / "tools/publish-release.py")
publisher = importlib.util.module_from_spec(spec); spec.loader.exec_module(publisher)


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
