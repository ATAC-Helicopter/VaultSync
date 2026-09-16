import unittest
from pathlib import Path


class ReleaseWorkflowTests(unittest.TestCase):
    def test_final_draft_assets_require_stable_build_provenance(self):
        workflow = (Path(__file__).resolve().parents[2] /
                    ".github/workflows/release-assets.yml").read_text()
        self.assertIn('"$branch" != "Stable"', workflow)
        self.assertIn('"$release_candidate" == "true"', workflow)
        self.assertIn('.head_branch == "Stable"', workflow)
        self.assertIn('git merge-base --is-ancestor "$source_sha" origin/Stable', workflow)

    def test_store_is_part_of_manifest_and_supply_chain_inputs(self):
        workflow = (Path(__file__).resolve().parents[2] /
                    ".github/workflows/release-assets.yml").read_text()
        manifest = workflow.split("  release-manifest:\n", 1)[1].split("  supply-chain-proof:\n", 1)[0]
        proof = workflow.split("  supply-chain-proof:\n", 1)[1]
        self.assertIn("name: windows-store-upload", manifest)
        self.assertIn("optional_args+=( --include-store-upload )", manifest)
        self.assertIn("name: windows-store-upload", proof)
        self.assertIn("Attest Microsoft Store upload SBOM", proof)
        self.assertIn("dist/store/VaultSync-Store-*-x64.msixupload", workflow)
        self.assertNotIn("dist/store/*.msixupload", workflow)

    def test_store_build_embeds_its_own_distribution_identity(self):
        workflow = (Path(__file__).resolve().parents[2] /
                    ".github/workflows/release-assets.yml").read_text()
        store = workflow.split("      - name: Build Microsoft Store upload package\n", 1)[1]
        store = store.split("      - name: Upload Windows artifacts\n", 1)[0]
        for property_value in ("VaultSyncPackageKind=microsoft-store-msix",
                               "VaultSyncUpdateSource=microsoft-store",
                               "VaultSyncOfficialBuild=true", "SourceRevisionId=${{ github.sha }}"):
            self.assertIn(property_value, store)
