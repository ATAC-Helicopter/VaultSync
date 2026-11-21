import unittest
import itertools
import json
import re
import os
import subprocess
from pathlib import Path


class ReleaseWorkflowTests(unittest.TestCase):
    def test_updater_matrix_qualifies_every_enabled_platform(self):
        workflow = (Path(__file__).resolve().parents[2] / ".github/workflows/release-assets.yml").read_text()
        match = re.search(r'updater_matrix="\$\(jq .*?\n\s+\]\}.*?\n', workflow, re.S)
        self.assertIsNotNone(match)
        runners = {
            "windows": ["windows-latest"],
            "macos": ["macos-latest", "macos-15-intel"],
            "linux": ["ubuntu-latest", "ubuntu-24.04-arm"],
        }
        for flags in itertools.product((False, True), repeat=len(runners)):
            with self.subTest(flags=flags):
                result = subprocess.run(
                    ["bash", "-e", "-c", match.group(0) + '\nprintf "%s" "$updater_matrix"'],
                    env={**os.environ, **{f"INCLUDE_{name.upper()}_PATCHES": json.dumps(enabled)
                                         for name, enabled in zip(runners, flags)}},
                    capture_output=True, text=True, check=True,
                )
                expected = [{"os": runner, "artifact": f"{name}-release-assets"}
                            for (name, options), enabled in zip(runners.items(), flags) if enabled
                            for runner in options]
                self.assertEqual({"include": expected}, json.loads(result.stdout))
        job = workflow.split("  updater-qualification:\n", 1)[1].split("  release-manifest:\n", 1)[0]
        self.assertIn("inputs.include_windows_patches || inputs.include_macos_patches || inputs.include_linux_patches", job)
        self.assertIn("name: ${{ matrix.artifact }}", job)

    def test_final_drafts_require_authorized_source_and_preserve_stable_provenance(self):
        workflow = (Path(__file__).resolve().parents[2] /
                    ".github/workflows/release-assets.yml").read_text()
        self.assertIn('"$branch" != "Stable"', workflow)
        self.assertIn('"$release_candidate" == "true"', workflow)
        self.assertIn('scripts/release_draft.py source', workflow)
        self.assertIn('[[ "$(git rev-parse HEAD)" == "$source_sha" ]]', workflow)
        self.assertIn('--source-ref "$SOURCE_REF"', workflow)
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


    def test_release_packages_use_only_the_validated_build_channel(self):
        workflow = (Path(__file__).resolve().parents[2] /
                    ".github/workflows/release-assets.yml").read_text()
        values = re.findall(r"[/\-]p:VaultSyncReleaseChannel=([^\n`]+)", workflow)
        self.assertEqual(7, len(values))
        self.assertTrue(all(v.strip() == "${{ needs.validate.outputs.build_channel }}" for v in values))
        self.assertIn("build_channel: ${{ steps.validate_inputs.outputs.build_channel }}", workflow)

    def test_channel_selection_executes_actual_workflow_policy(self):
        workflow = (Path(__file__).resolve().parents[2] /
                    ".github/workflows/release-assets.yml").read_text()
        match = re.search(r'case "\$channel:\$release_candidate" in.*?esac', workflow, re.S)
        self.assertIsNotNone(match)
        for channel, candidate, expected in [("beta", "false", "beta"),
                                             ("stable", "false", "stable"),
                                             ("stable", "true", "candidate"),
                                             ("beta", "true", None),
                                             ("unknown", "false", None),
                                             ("stable", "unknown", None)]:
            with self.subTest(channel=channel, candidate=candidate):
                result = subprocess.run(["bash", "-c", match.group(0) + '\nprintf "%s" "$build_channel"'],
                    env={**os.environ, "channel": channel, "release_candidate": candidate},
                    capture_output=True, text=True)
                if expected is None:
                    self.assertNotEqual(0, result.returncode)
                else:
                    self.assertEqual(0, result.returncode)
                    self.assertEqual(expected, result.stdout)
