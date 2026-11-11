#!/usr/bin/env python3
"""Bind two native receipts to the requested source; kernel proof stays with their wrappers."""
import json
import os
import re
import stat
import sys
from pathlib import Path

MAXIMUM_BYTES = 65536
SOURCE_FIELDS = ('sourceCommit', 'compiledSourceCommit', 'checkoutRevision')
RECEIPTS = (('recovery-storage', 'native-storage-evidence.json'),
            ('recovery-admission', 'native-admission-evidence.json'))


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError('duplicate JSON field')
        result[key] = value
    return result


def invalid_constant(_value):
    raise ValueError('invalid JSON constant')


def validate(expected, runner_temp):
    if not isinstance(expected, str) or re.fullmatch(r'[0-9a-f]{40}', expected) is None:
        raise ValueError('invalid expected source SHA')
    root = Path(runner_temp)
    if not root.is_absolute() or '..' in root.parts or root.is_symlink():
        raise ValueError('invalid runner root')
    root = root.resolve(strict=True)
    if root == Path(root.anchor) or not root.is_dir():
        raise ValueError('invalid runner root')
    for directory, name in RECEIPTS:
        path = root / directory / name
        resolved = path.resolve(strict=True)
        if not resolved.is_relative_to(root) or resolved != path:
            raise ValueError('receipt escapes its fixed runner location')
        before = path.lstat()
        if not stat.S_ISREG(before.st_mode) or before.st_nlink != 1:
            raise ValueError('receipt must be a single regular file')
        flags = os.O_RDONLY | getattr(os, 'O_NOFOLLOW', 0) | getattr(os, 'O_NONBLOCK', 0) | getattr(os, 'O_BINARY', 0)
        with os.fdopen(os.open(path, flags), 'rb') as stream:
            held = os.fstat(stream.fileno())
            if not stat.S_ISREG(held.st_mode) or held.st_nlink != 1 or (held.st_dev, held.st_ino) != (before.st_dev, before.st_ino):
                raise ValueError('receipt identity changed')
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
    if len(args) != 1:
        print('Usage: validate_native_evidence_source.py EXPECTED_SHA', file=sys.stderr)
        return 2
    try:
        runner_temp = os.environ.get('RUNNER_TEMP')
        if not runner_temp:
            raise ValueError('runner root unavailable')
        validate(args[0], runner_temp)
    except (OSError, ValueError, RuntimeError):
        # Do not echo paths or untrusted receipt text into CI diagnostics.
        print('Native evidence source refused: invalid, unavailable or mismatched receipt.', file=sys.stderr)
        return 1
    print('Both native receipts match the expected source SHA; kernel authority is checked separately.')
    return 0


if __name__ == '__main__':
    sys.exit(main())
