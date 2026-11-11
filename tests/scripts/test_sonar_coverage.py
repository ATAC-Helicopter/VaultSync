import contextlib
import importlib.util
import io
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location('sonar_coverage', ROOT / 'scripts/validate_sonar_coverage.py')
coverage = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(coverage)


class SonarCoverageTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.report = Path(self.temp.name) / 'coverage.xml'

    def check(self, xml=None):
        if xml is not None:
            self.report.write_text(xml)
        with contextlib.redirect_stderr(io.StringIO()), contextlib.redirect_stdout(io.StringIO()):
            return coverage.main([str(self.report)])

    @staticmethod
    def report_xml(module='vaultsync.core.dll', covered='yes', source_id='0', start='10', end='11',
                   source='/home/runner/work/VaultSync/VaultSync/src/VaultSync.Core/Services/Example.cs'):
        return f'''<results><modules><module name="{module}"><functions><function><ranges>
        <range source_id="{source_id}" covered="{covered}" start_line="{start}" end_line="{end}" />
        </ranges></function></functions><source_files><source_file id="0" path="{source}" />
        </source_files></module></modules></results>'''

    def test_accepts_actual_core_ranges_with_linux_or_windows_paths_and_module_case(self):
        self.assertEqual(0, self.check(self.report_xml()))
        self.assertEqual(0, self.check(self.report_xml(module='VaultSync.Core.dll', covered='partial',
                         source=r'C:\repo\src\VaultSync.Core\Services\Example.cs')))

    def test_missing_report_fails(self):
        self.assertEqual(1, self.check())

    def test_malformed_or_wrong_format_fails(self):
        for xml in ('', '<results>', '<coverage />'):
            with self.subTest(xml=xml):
                self.assertEqual(1, self.check(xml))

    def test_profiler_empty_report_fails(self):
        self.assertEqual(1, self.check('<results><modules /></results>'))

    def test_test_assembly_coverage_cannot_mask_missing_product_coverage(self):
        self.assertEqual(1, self.check(self.report_xml(module='VaultSync.Core.Tests.dll')))

    def test_uncovered_core_ranges_fail(self):
        self.assertEqual(1, self.check(self.report_xml(covered='no')))

    def test_missing_or_wrong_source_mapping_fails(self):
        self.assertEqual(1, self.check(self.report_xml(source_id='1')))
        self.assertEqual(1, self.check(self.report_xml(source='/repo/tests/VaultSync.Core.Tests/Example.cs')))

    def test_invalid_line_numbers_fail(self):
        for start, end in [('0', '1'), ('11', '10'), ('invalid', '11')]:
            with self.subTest(start=start, end=end):
                self.assertEqual(1, self.check(self.report_xml(start=start, end=end)))

    def test_usage_error_is_distinct(self):
        with contextlib.redirect_stderr(io.StringIO()):
            self.assertEqual(2, coverage.main([]))


if __name__ == '__main__':
    unittest.main()
