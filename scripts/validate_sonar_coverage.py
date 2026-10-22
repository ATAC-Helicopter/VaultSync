#!/usr/bin/env python3
"""Reject missing/empty Microsoft XML coverage before Sonar imports it.

This checks collection health, not a coverage percentage or release gate.
Sonar remains responsible for the unchanged quality thresholds.
"""
import sys
import xml.etree.ElementTree as ET
from pathlib import Path


def validate(path):
    root = ET.parse(path).getroot()
    if root.tag != 'results':
        raise ValueError('Expected Microsoft coverage XML results.')
    for module in root.findall('./modules/module'):
        if module.get('name', '').casefold() != 'vaultsync.core.dll':
            continue
        sources = {source.get('id') for source in module.findall('./source_files/source_file')
                   if '/src/VaultSync.Core/' in source.get('path', '').replace('\\', '/')}
        for span in module.findall('./functions/function/ranges/range'):
            if span.get('covered') not in ('yes', 'partial') or span.get('source_id') not in sources:
                continue
            start, end = int(span.get('start_line', '0')), int(span.get('end_line', '0'))
            if 0 < start <= end:
                return
    raise ValueError('No covered VaultSync.Core source ranges: coverage collection is unavailable.')


def main(argv=None):
    args = sys.argv[1:] if argv is None else argv
    if len(args) != 1:
        print('Usage: validate_sonar_coverage.py coverage.xml', file=sys.stderr)
        return 2
    try:
        validate(Path(args[0]))
    except (OSError, ET.ParseError, ValueError) as error:
        print(f'Sonar coverage refused: {error}', file=sys.stderr)
        return 1
    print('Sonar coverage collection contains covered VaultSync.Core source ranges.')
    return 0


if __name__ == '__main__':
    sys.exit(main())
