import copy
import json
import pathlib
import sys
import unittest
from unittest.mock import patch

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[2] / 'scripts'))
import work_tracking as tracking


def node(number=1):
    return dict(number=number, title=f'BUG-19036: item {number}', body='Refs VS-1907',
                state='OPEN', stateReason=None, closedAt=None, milestone=dict(title='1.9.0'),
                labels=dict(pageInfo=dict(hasNextPage=False), nodes=[dict(name='priority:p0')]),
                assignees=dict(pageInfo=dict(hasNextPage=False), nodes=[dict(login='maintainer')]))


def response(nodes, total=None, more=False, cursor=None, collection='issues'):
    page = dict(totalCount=len(nodes) if total is None else total, nodes=nodes,
                pageInfo=dict(hasNextPage=more, endCursor=cursor))
    return tracking.CommandResult(json.dumps(dict(data=dict(repository={collection:page}))), '', 0)


class RemoteWorkItemsTests(unittest.TestCase):
    def fetch(self, responses, **options):
        with patch.object(tracking, 'run', side_effect=responses) as execute:
            result = tracking.fetch_remote_work_items(pathlib.Path('.'), 'owner/repo', **options)
        return result, execute

    def test_reads_all_cursors_and_normalizes_metadata_without_requesting_bodies(self):
        items, execute = self.fetch([response([node()], 2, True, 'first'), response([node(2)], 2)])
        self.assertEqual([1, 2], [x['number'] for x in items])
        self.assertEqual('open', items[0]['state'])
        self.assertEqual([dict(login='maintainer')], items[0]['assignees'])
        self.assertNotIn('body', items[0])
        requests = [json.loads(call.kwargs['input_text']) for call in execute.call_args_list]
        self.assertIsNone(requests[0]['variables']['cursor'])
        self.assertEqual('first', requests[1]['variables']['cursor'])
        self.assertNotIn(' body ', requests[0]['query'])
        self.assertTrue(all(call.args[0][:3] == ['gh','api','graphql'] for call in execute.call_args_list))

    def test_allocation_includes_pr_titles_and_bodies_and_preserves_closed_reason(self):
        issue = node(); issue.update(state='CLOSED', stateReason='NOT_PLANNED', closedAt='2026-10-22T10:00:00Z')
        items, _ = self.fetch([response([issue]), response([node(2)], collection='pullRequests')],
                              include_prs=True, include_body=True)
        self.assertEqual('not_planned', items[0]['state_reason'])
        self.assertEqual('2026-10-22T10:00:00Z', items[0]['closed_at'])
        self.assertEqual('Refs VS-1907', items[1]['body'])
        self.assertNotIn('pull_request', items[0])
        self.assertIn('pull_request', items[1])

    def test_empty_complete_connections_are_valid(self):
        self.assertEqual([], self.fetch([response([])])[0])

    def test_missing_pages_cannot_be_accepted_as_a_complete_snapshot(self):
        with self.assertRaises(tracking.TrackingError):
            self.fetch([response([node()], total=2)])

    def test_changing_total_cannot_be_accepted(self):
        with self.assertRaises(tracking.TrackingError):
            self.fetch([response([node()], 2, True, 'first'), response([node(2)], 3)])

    def test_duplicate_issue_numbers_cannot_pass(self):
        with self.assertRaises(tracking.TrackingError):
            self.fetch([response([node(), node()])])

    def test_duplicate_numbers_across_issue_and_pr_connections_cannot_pass(self):
        with self.assertRaises(tracking.TrackingError):
            self.fetch([response([node()]), response([node()], collection='pullRequests')], include_prs=True)

    def test_repeated_or_missing_cursors_cannot_loop_or_pass(self):
        for cursor in (None, '', 3):
            with self.subTest(cursor=cursor), self.assertRaises(tracking.TrackingError):
                self.fetch([response([node()], 2, True, cursor)])
        with self.assertRaises(tracking.TrackingError):
            self.fetch([response([node()], 3, True, 'first'), response([node(2)], 3, True, 'first')])

    def test_empty_nonterminal_page_cannot_skip_work(self):
        with self.assertRaises(tracking.TrackingError):
            self.fetch([response([], 2, True, 'first')])

    def test_partial_labels_or_assignees_cannot_pass(self):
        for field in ('labels', 'assignees'):
            item = node(); item[field]['pageInfo']['hasNextPage'] = True
            with self.subTest(field=field), self.assertRaises(tracking.TrackingError):
                self.fetch([response([item])])
            for invalid in ([None], [{}], ['missing']):
                item = node(); item[field]['nodes'] = invalid
                with self.subTest(field=field, invalid=invalid), self.assertRaises(tracking.TrackingError):
                    self.fetch([response([item])])

    def test_missing_body_cannot_reserve_an_incomplete_id_inventory(self):
        item = node(); item.pop('body')
        with self.assertRaises(tracking.TrackingError):
            self.fetch([response([item])], include_body=True)

    def test_invalid_identity_or_count_or_flags_cannot_pass(self):
        for value in (0, True, '1', None):
            item = node(); item['number'] = value
            with self.subTest(number=value), self.assertRaises(tracking.TrackingError):
                self.fetch([response([item])])
        for value in (-1, True, '1', 10001):
            with self.subTest(total=value), self.assertRaises(tracking.TrackingError):
                self.fetch([response([node()], value)])
        bad = json.loads(response([node()]).stdout); bad['data']['repository']['issues']['pageInfo']['hasNextPage'] = 1
        with self.assertRaises(tracking.TrackingError):
            self.fetch([tracking.CommandResult(json.dumps(bad), '', 0)])

    def test_closed_issue_dates_and_valid_states_are_required(self):
        for state, closed in [('CLOSED', None), ('CLOSED', ''), ('unknown', None)]:
            item = node(); item.update(state=state, closedAt=closed)
            with self.subTest(state=state, closed=closed), self.assertRaises(tracking.TrackingError):
                self.fetch([response([item])])

    def test_graphql_errors_missing_repository_and_malformed_json_fail_closed(self):
        for text in ('', '[]', '{}', '{"errors":[{"message":"denied"}]}', '{"data":{"repository":null}}'):
            with self.subTest(text=text), self.assertRaises(tracking.TrackingError):
                self.fetch([tracking.CommandResult(text, '', 0)])

    def test_transport_failure_does_not_return_prior_pages(self):
        with self.assertRaises(tracking.TrackingError):
            self.fetch([response([node()], 2, True, 'first'), tracking.TrackingError('API unavailable')])
