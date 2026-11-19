"""Create a new operator-chosen evidence file without clobbering existing data."""
import os
import re
from pathlib import Path


def write_text_exclusive(path: Path, content: str) -> None:
    # CLI callers may choose an absolute directory; parent traversal and ambiguous
    # filenames are not needed for release/research evidence. Never replace data.
    if '..' in path.parts or not re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9._-]{0,127}', path.name):
        raise ValueError('Evidence output requires an explicit filename without parent traversal')
    path.parent.mkdir(parents=True, exist_ok=True)
    destination = path.parent.resolve(strict=True) / path.name
    flags = os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, 'O_NOFOLLOW', 0)
    descriptor = os.open(destination, flags, 0o600)
    with os.fdopen(descriptor, 'w', encoding='utf-8', newline='\n') as output:
        output.write(content)
