import importlib.util
import json
import tempfile
import unittest
from pathlib import Path

spec = importlib.util.spec_from_file_location('localization_audit', Path(__file__).resolve().parents[2] / 'scripts/localization_audit.py')
audit = importlib.util.module_from_spec(spec)
spec.loader.exec_module(audit)


class LocalizationAuditTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.locales = self.root / 'Localization'
        self.source = self.root / 'UI'
        self.locales.mkdir()
        self.source.mkdir()
        self.write('en', {'Ui.Label': 'Open', 'Ui.Count': '{0} of {1:N0}'})
        self.write('it', {'Ui.Label': 'Apri', 'Ui.Count': '{0} di {1:N0}'})

    def write(self, locale, values):
        (self.locales / f'strings.{locale}.json').write_text(json.dumps(values))

    def errors(self):
        return audit.validation_errors(audit.audit(self.locales, self.source))

    def test_missing_code_and_xaml_references_fail(self):
        (self.source / 'View.cs').write_text('Title = L("Ui.Absent", "Missing");')
        (self.source / 'View.axaml').write_text('<TextBlock Text="{infra:LocalizedString Key=Ui.Other}"/>')
        errors = self.errors()
        self.assertTrue(any('Ui.Absent' in e and 'Ui.Other' in e for e in errors))

    def test_fixed_ui_strings_fail_but_glyphs_bindings_and_format_tokens_pass(self):
        (self.source / 'View.axaml').write_text('<Panel><TextBlock Text="Fixed label"/><TextBlock Text="×"/><TextBox PlaceholderText="HH:mm"/></Panel>')
        self.assertTrue(any('Fixed label' in e for e in self.errors()))
        (self.source / 'View.axaml').write_text('<Panel><TextBlock Text="{infra:LocalizedString Key=Ui.Label}"/><TextBlock Text="×"/><TextBox PlaceholderText="HH:mm"/></Panel>')
        (self.source / 'View.cs').write_text('Title = L("Ui.Label", "Open"); Text = "TXT";')
        self.assertEqual([], self.errors())

    def test_setter_and_interpolated_sentence_are_not_a_bypass(self):
        (self.source / 'View.axaml').write_text('<Setter Property="Text" Value="Fixed status"/>')
        (self.source / 'View.cs').write_text('Title = $"Hello {name}";')
        errors = self.errors()
        self.assertTrue(any('Fixed status' in e and 'Hello' in e for e in errors))
        (self.source / 'View.axaml').write_text('<Setter Property="Text" Value="{infra:LocalizedString Key=Ui.Label}"/>')
        (self.source / 'View.cs').write_text('Title = $"{projectName} · {kind}";')
        self.assertEqual([], self.errors())

    def test_missing_declared_language_is_not_hidden_by_deleting_locale_file(self):
        (self.source / 'LocalizationService.cs').write_text('Code = "en"; Code = "it"; Code = "fr";')
        self.assertTrue(any('missing_locale_files' in e and 'fr' in e for e in self.errors()))

    def test_missing_extra_blank_and_changed_placeholders_fail(self):
        for values in ({'Ui.Label': 'Apri'}, {'Ui.Label': '', 'Ui.Count': '{0} di {1:N0}'},
                       {'Ui.Label': 'Apri', 'Ui.Count': '{0} di {2:N0}'},
                       {'Ui.Label': 'Apri', 'Ui.Count': '{0} di {1:N0}', 'Extra': 'x'}):
            self.write('it', values)
            self.assertTrue(self.errors())

    def test_reordered_placeholders_and_legitimate_identical_words_are_valid(self):
        self.write('it', {'Ui.Label': 'Open', 'Ui.Count': '{1:N0} : {0}'})
        self.assertEqual([], self.errors())
        self.assertEqual(1, len(audit.audit(self.locales)['locales']['it']['exact_english_candidates']))

    def test_duplicate_or_blank_english_baseline_fails(self):
        p = self.locales / 'strings.en.json'
        p.write_text('{"Ui.Label":"Open","Ui.Label":"Other","Ui.Count":"{0} of {1:N0}"}')
        self.assertTrue(any('baseline_duplicate_keys' in e for e in self.errors()))
        self.write('en', {'Ui.Label': '', 'Ui.Count': '{0} of {1:N0}'})
        self.assertTrue(any('baseline_blank_or_non_string_values' in e for e in self.errors()))

    def test_array_locale_and_missing_source_fail_closed(self):
        self.write('it', [['Ui.Label', 'Apri']])
        with self.assertRaises(ValueError):
            self.errors()
        with self.assertRaises(ValueError):
            audit.source_audit(self.root / 'missing', {})
