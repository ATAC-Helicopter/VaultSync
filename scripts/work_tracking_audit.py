"""Read-only release tracking audit; never creates/closes issues or guesses progress."""
from __future__ import annotations

import json
import re
from datetime import date, datetime, timezone
from pathlib import Path

from work_tracking import TrackingError, fetch_remote_work_items, github_repo, load_config, repo_root, run


def audit_records(release, roadmap, issues, project_records, project_number, today, project_owner):
    priorities = dict(re.findall(r'^- \[[ xX]\] `(\w+-\d+)` `(P[0-2])`', roadmap, re.M))
    rows = []
    family_label = '.'.join(release['version'].split('.')[:2]) + '.x'
    for work_id in release['workIds']:
        matches = [issue for issue in issues if re.match(r'^' + re.escape(work_id) + r'\b', issue['title'])]
        row = dict(workId=work_id, findings=[])
        rows.append(row)
        findings = row['findings']
        if len(matches) != 1:
            findings.append(f'Expected one owning issue, found {len(matches)}')
            continue
        issue = matches[0]
        number = issue['number']
        row.update(issue=number, state=issue['state'], stateReason=issue.get('state_reason'))
        labels = {label['name'] for label in issue['labels']}
        if (issue.get('milestone') or {}).get('title') != release['version']:
            findings.append('Missing or wrong release milestone')
        if not issue.get('assignees'):
            findings.append('Missing assignee')
        expected_priority = priorities.get(work_id)
        if expected_priority is None:
            findings.append('Missing canonical roadmap priority')
        elif {label for label in labels if label.startswith('priority:')} != {'priority:' + expected_priority.lower()}:
            findings.append('Priority labels disagree with roadmap')
        if 'release:' + release['version'] not in labels:
            findings.append('Missing exact release label')
        data = project_records.get(number)
        if data is None or data['projectItems']['pageInfo']['hasNextPage']:
            raise TrackingError(f'Incomplete Project membership for issue #{number}')
        items = [item for item in data['projectItems']['nodes'] if item['project']['number'] == project_number
                 and item['project']['owner']['login'] == project_owner]
        if len(items) != 1:
            findings.append(f'Expected one Project #{project_number} item, found {len(items)}')
            continue
        item = items[0]
        if item['fieldValues']['pageInfo']['hasNextPage']:
            raise TrackingError(f'Incomplete Project fields for issue #{number}')
        fields = {value['field']['name']: value.get('date', value.get('name'))
                  for value in item['fieldValues']['nodes'] if value.get('field')}
        row.update(projectItem=item['id'], fields=fields)
        status = fields.get('Status')
        if status not in ('Todo', 'In progress', 'Done'):
            findings.append('Missing or unknown Project status')
        elif (issue['state'] == 'closed') != (status == 'Done'):
            findings.append('Issue state disagrees with Project status')
        expected_status = {'Todo': 'status:todo', 'In progress': 'status:in-progress', 'Done': 'status:done'}.get(status)
        if expected_status and {label for label in labels if label.startswith('status:')} != {expected_status}:
            findings.append('Status labels disagree with Project status')
        if fields.get('Priority') != expected_priority:
            findings.append('Project priority disagrees with roadmap')
        if fields.get('Release') != family_label:
            findings.append('Missing or wrong Project release family')
        if not fields.get('Area'):
            findings.append('Missing Project area')
        dates = {}
        for name in ('Start date', 'Target date'):
            try:
                dates[name] = date.fromisoformat(fields.get(name, ''))
            except (ValueError, TypeError):
                findings.append(f'Missing or invalid {name}')
        if len(dates) == 2 and dates['Target date'] < dates['Start date']:
            findings.append('Target date precedes Start date')
        if status in ('In progress', 'Done') and dates.get('Start date', today) > today:
            findings.append('Started work has a future Start date')
        completed = fields.get('Completed on')
        if status == 'Done':
            if not issue.get('closed_at') or completed != issue['closed_at'][:10]:
                findings.append('Completed on must match the retained issue closure date')
        elif completed:
            findings.append('Unfinished work has a Completed on date')
    return rows


def fetch_records(root, repository, work_ids):
    issues = fetch_remote_work_items(root, repository)
    selected = [issue for issue in issues if any(re.match(r'^' + re.escape(wid) + r'\b', issue['title']) for wid in work_ids)]
    owner, name = repository.split('/')
    records = {}
    for start in range(0, len(selected), 15):
        fields = '''id project { number owner { ... on User { login } ... on Organization { login } } } fieldValues(first:30) {
          pageInfo { hasNextPage } nodes {
          ... on ProjectV2ItemFieldSingleSelectValue { name field { ... on ProjectV2SingleSelectField { name } } }
          ... on ProjectV2ItemFieldDateValue { date field { ... on ProjectV2Field { name } } } } }'''
        selections = ' '.join(f'i{i["number"]}:issue(number:{int(i["number"])}) {{ number projectItems(first:20) {{ '
            f'pageInfo {{ hasNextPage }} nodes {{ {fields} }} }} }}' for i in selected[start:start + 15])
        query = f'query {{ repository(owner:{json.dumps(owner)},name:{json.dumps(name)}) {{ {selections} }} }}'
        result = json.loads(run(['gh', 'api', 'graphql', '-f', 'query=' + query], cwd=root).stdout)
        if result.get('errors') or not result.get('data', {}).get('repository'):
            raise TrackingError('Project audit unavailable or returned partial data')
        for record in result['data']['repository'].values():
            if record is None:
                raise TrackingError('Project audit returned a missing issue')
            records[record['number']] = record
    return issues, records


def cmd_audit(args):
    root = repo_root()
    config = load_config(root)
    try:
        manifest = json.loads((root / 'release/1.9-family.json').read_text(encoding='utf-8'))
        version = args.release or json.loads((root / 'release/release-metadata.json').read_text(encoding='utf-8'))['activeRelease']['version'].split('-')[0]
        releases = [release for release in manifest['releases'] if args.all_releases or release['version'] == version]
        if not releases:
            raise TrackingError(f'No family release matches {version!r}')
        issues, records = fetch_records(root, github_repo(root), [wid for release in releases for wid in release['workIds']])
        today = datetime.now(timezone.utc).date()
        roadmap = (root / 'ROADMAP.md').read_text(encoding='utf-8-sig')
        report = dict(recordedUtc=datetime.now(timezone.utc).isoformat(), releases=[])
        for release in releases:
            report['releases'].append(dict(version=release['version'], items=audit_records(
                release, roadmap, issues, records, config['project_number'], today, config['project_owner'])))
    except (OSError, ValueError, KeyError, TypeError) as error:
        raise TrackingError('Tracking audit incomplete; no passing result: ' + str(error)) from error
    count = sum(len(release['items']) for release in report['releases'])
    findings = [(release['version'], row) for release in report['releases'] for row in release['items'] if row['findings']]
    if args.output:
        try:
            Path(args.output).write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
        except OSError as error:
            raise TrackingError('Cannot retain tracking audit report: ' + str(error)) from error
    for version, row in findings:
        print(f'{version} {row["workId"]}: {"; ".join(row["findings"])}')
    print(f'Tracking audit: {count} work items, {len(findings)} with discrepancies. Read-only; release acceptance is separate.')
    return 1 if findings else 0
