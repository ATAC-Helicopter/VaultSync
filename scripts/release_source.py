#!/usr/bin/env python3
"""Explicit beta source authorization; never a qualification or publication grant."""
import argparse
import re

PREPARATION_REF = 'refs/heads/work/1.9-roadmap-realignment'


def beta_source_allowed(version: str, source_ref: str) -> bool:
    if not re.fullmatch(r'[0-9]+\.[0-9]+\.[0-9]+-[A-Za-z0-9]+(?:[.-][A-Za-z0-9]+)*', version):
        return False
    if source_ref == 'refs/heads/Dev':
        return True
    match = re.fullmatch(r'1\.9\.0-beta\.([1-9][0-9]{0,2})', version)
    return source_ref == PREPARATION_REF and match is not None and int(match[1]) <= 998


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--version', required=True)
    parser.add_argument('--source-ref', required=True)
    args = parser.parse_args()
    if not beta_source_allowed(args.version, args.source_ref):
        parser.exit(1, 'Source ref is not authorized for this beta version.\n')


if __name__ == '__main__':
    main()
