import copy
import json
import pathlib
import sys
import unittest
from datetime import date
from unittest.mock import patch

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[2] / 'scripts'))
import work_tracking_audit as audit
from work_tracking import CommandResult, TrackingError, build_parser


class ReleaseTrackingAuditTests(unittest.TestCase):
    def setUp(self):
        self.release = dict(version='1.9.0', workIds=['VS-1905'])
        self.roadmap = '- [ ] `VS-1905` `P0` Restore safety.\n'
        self.issue = dict(number=493, title='VS-1905: Restore safety', state='open',
                          labels=[dict(name=name) for name in ('priority:p0', 'release:1.9.0', 'status:in-progress')],
                          milestone=dict(title='1.9.0'), assignees=[dict(login='maintainer')])
        self.fields = {'Status': 'In progress', 'Priority': 'P0', 'Release': '1.9.x',
                       'Area': 'Core', 'Start date': '2026-10-15', 'Target date': '2027-03-26'}
        self.item = dict(id='PVTI_fixture', project=dict(number=7, owner=dict(login='ATAC-Helicopter')), fieldValues={})

    def records(self):
        item = copy.deepcopy(self.item)
        item['fieldValues'].update(pageInfo=dict(hasNextPage=False), nodes=[
            dict(field=dict(name=name), **({'date': value} if 'date' in name or name == 'Completed on' else {'name': value}))
            for name, value in self.fields.items()])
        return {493: dict(number=493, projectItems=dict(pageInfo=dict(hasNextPage=False), nodes=[item]))}

    def findings(self, issues=None, records=None):
        return audit.audit_records(self.release, self.roadmap,
            [self.issue] if issues is None else issues, self.records() if records is None else records,
            7, date(2026, 10, 15), 'ATAC-Helicopter')[0]['findings']

    def test_complete_open_release_gated_work_passes(self):
        self.assertEqual([], self.findings())

    def test_missing_or_duplicate_owning_issue_is_not_hidden_by_mentions(self):
        self.assertTrue(self.findings([]))
        self.assertTrue(self.findings([self.issue, dict(self.issue, number=999)]))
        self.assertTrue(self.findings([dict(self.issue, title='Other work references VS-1905')]))

    def test_wrong_milestone_owner_and_conflicting_labels_are_reported(self):
        self.issue.update(milestone=None, assignees=[])
        self.issue['labels'].append(dict(name='priority:p2'))
        self.issue['labels'].append(dict(name='status:todo'))
        findings = self.findings()
        for fragment in ('milestone', 'assignee', 'Priority labels', 'Status labels'):
            self.assertTrue(any(fragment in finding for finding in findings))

    def test_missing_native_item_and_duplicate_item_are_errors(self):
        for nodes in ([], [self.records()[493]['projectItems']['nodes'][0]] * 2):
            records = self.records()
            records[493]['projectItems']['nodes'] = nodes
            self.assertTrue(self.findings(records=records))

    def test_partial_remote_data_cannot_pass(self):
        with self.assertRaises(TrackingError):
            self.findings(records={})
        for field in ('memberships', 'fields'):
            records = self.records()
            location = records[493]['projectItems']
            if field == 'fields':
                location = location['nodes'][0]['fieldValues']
            location['pageInfo']['hasNextPage'] = True
            with self.assertRaises(TrackingError):
                self.findings(records=records)

    def test_same_project_number_in_another_account_is_not_the_canonical_board(self):
        records = self.records()
        records[493]['projectItems']['nodes'][0]['project']['owner']['login'] = 'another-owner'
        self.assertTrue(self.findings(records=records))

    def test_missing_fields_and_future_or_reversed_dates_are_errors(self):
        self.fields.update({'Start date': '2026-10-16', 'Target date': '2026-10-14', 'Completed on': '2026-10-15'})
        del self.fields['Area']
        del self.fields['Release']
        self.assertEqual(5, len(self.findings()))
        self.fields['Start date'] = 'not-a-date'
        self.assertTrue(any('invalid Start date' in finding for finding in self.findings()))

    def test_done_requires_closed_issue_and_matching_completion_date(self):
        self.fields['Status'] = 'Done'
        self.issue['labels'][-1] = dict(name='status:done')
        self.assertEqual(2, len(self.findings()))
        self.issue.update(state='closed', closed_at='2026-10-15T09:00:00Z')
        self.fields['Completed on'] = '2026-10-15'
        self.assertEqual([], self.findings())
        self.fields['Status'] = 'In progress'
        self.assertTrue(self.findings())

    def test_fetch_paginates_issues_excludes_prs_and_is_read_only(self):
        pages = [[self.issue, dict(self.issue, number=999, pull_request={})]]
        payload = dict(data=dict(repository=dict(i493=self.records()[493])))
        with patch.object(audit, 'run', side_effect=[CommandResult(json.dumps(pages), '', 0),
                CommandResult(json.dumps(payload), '', 0)]) as execute:
            issues, records = audit.fetch_records(pathlib.Path('.'), 'owner/repo', ['VS-1905'])
        self.assertEqual([self.issue], issues)
        self.assertEqual(self.records(), records)
        self.assertIn('--paginate', execute.call_args_list[0].args[0])
        for call in execute.call_args_list:
            self.assertNotIn('mutation', ' '.join(call.args[0]))

    def test_graphql_errors_and_empty_issue_pagination_fail_closed(self):
        for pages, payload in (([], {}), ([[self.issue]], {'errors': [{'message': 'denied'}]})):
            with patch.object(audit, 'run', side_effect=[CommandResult(json.dumps(pages), '', 0),
                    CommandResult(json.dumps(payload), '', 0)]):
                with self.assertRaises(TrackingError):
                    audit.fetch_records(pathlib.Path('.'), 'owner/repo', ['VS-1905'])

    def test_audit_scope_is_explicit(self):
        args = build_parser().parse_args(['audit', '--release', '1.9.0'])
        self.assertEqual('1.9.0', args.release)
        self.assertFalse(args.all_releases)
        self.assertTrue(build_parser().parse_args(['audit', '--all-releases']).all_releases)


if __name__ == '__main__':
    unittest.main()
