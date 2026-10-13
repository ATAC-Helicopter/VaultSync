"""A locale-only source change must invalidate reused release packages."""
import shlex
import subprocess
import tempfile
import unittest
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]


class ReleaseLocaleInputTests(unittest.TestCase):
    def test_draft_source_gate_rejects_translation_changes_since_original_build(self):
        workflow = (REPO_ROOT / '.github/workflows/release-assets.yml').read_text()
        line = next(line.strip() for line in workflow.splitlines()
                    if line.strip().startswith('git diff --exit-code "$source_sha" HEAD --'))
        command = shlex.split(line)
        input_paths = command[command.index('--') + 1:]
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            def git(*args):
                return subprocess.run(['git', *args], cwd=root, check=True,
                                      capture_output=True, text=True).stdout.strip()
            git('init', '-q')
            git('config', 'user.name', 'VaultSync release validation')
            git('config', 'user.email', 'validation@localhost')
            (root / 'Localization').mkdir()
            locale = root / 'Localization/strings.en.json'
            locale.write_text('{"Ui.Label":"Before"}')
            git('add', '.')
            git('commit', '-qm', 'Initial release inputs')
            source = git('rev-parse', 'HEAD')
            locale.write_text('{"Ui.Label":"After"}')
            git('add', '.')
            git('commit', '-qm', 'Change only the shipped UI text')
            result = subprocess.run(['git', 'diff', '--exit-code', source, 'HEAD', '--', *input_paths],
                                    cwd=root, capture_output=True, text=True)
            self.assertEqual(1, result.returncode, 'Reused artifacts silently omit new translations')
            self.assertIn('Localization/strings.en.json', result.stdout)
