#!/usr/bin/env python3
"""Bind two native receipts to the requested source; kernel proof stays with their wrappers."""
import json
import re
import sys
from pathlib import Path

MAXIMUM_BYTES = 65536
SOURCE_FIELDS = ('sourceCommit', 'compiledSourceCommit', 'checkoutRevision')


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError('duplicate JSON field')
        result[key] = value
    return result


def invalid_constant(_value):
    raise ValueError('invalid JSON constant')


def validate(expected, receipts):
    if not isinstance(expected, str) or re.fullmatch(r'[0-9a-f]{40}', expected) is None:
        raise ValueError('invalid expected source SHA')
    for path in receipts:
        with Path(path).open('rb') as stream:
            encoded = stream.read(MAXIMUM_BYTES + 1)
        if not 0 < len(encoded) <= MAXIMUM_BYTES:
            raise ValueError('receipt size bound')
        receipt = json.loads(encoded.decode('utf-8'), object_pairs_hook=unique_object,
                             parse_constant=invalid_constant)
        if not isinstance(receipt, dict):
            raise ValueError('receipt must be an object')
        for field in SOURCE_FIELDS:
            value = receipt.get(field)
            if not isinstance(value, str) or value != expected:
                raise ValueError('receipt source identity does not match')


def main(argv=None):
    args = sys.argv[1:] if argv is None else argv
    if len(args) != 3:
        print('Usage: validate_native_evidence_source.py EXPECTED_SHA receipt1.json receipt2.json', file=sys.stderr)
        return 2
    try:
        validate(args[0], args[1:])
    except (OSError, ValueError, RecursionError):
        # Do not echo paths or untrusted receipt text into CI diagnostics.
        print('Native evidence source refused: invalid, unavailable or mismatched receipt.', file=sys.stderr)
        return 1
    print('Both native receipts match the expected source SHA; kernel authority is checked separately.')
    return 0


if __name__ == '__main__':
    sys.exit(main())
