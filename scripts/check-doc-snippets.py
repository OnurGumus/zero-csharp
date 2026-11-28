#!/usr/bin/env python3
"""Check (or --update) the C# and F# excerpts in docs/ against the sample regions they quote.

A sample marks a region with comment lines:

    // docs:read-answer
    ...
    // docs:end

and a page quotes it by putting a marker on the line before a fence:

    <!-- sample: samples/Quiz.cs read-answer -->
    ```csharp
    ...
    ```

The region `file` is the whole file without its region markers. Every `csharp` and `fsharp` fence
needs a marker, except one opened as ```csharp no-sample (a signature or fragment no sample can hold).
F# sources mark regions with the same `// docs:` comments.

Usage:  scripts/check-doc-snippets.py [--update] [page.md ...]   (every page if none given)
"""
import re
import sys
import textwrap
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DOCS = ROOT / 'docs'
SECTIONS = ['get-started', 'tutorial', 'concepts', 'how-to', 'reference']
WIDTH = 80  # what a code frame shows at desktop width without scrolling
CHECKED = {'csharp', 'cs', 'c#', 'fsharp', 'fs', 'f#'}
OPEN = re.compile(r'^( *)(`{3,}|~{3,})([^`\s]*)(.*)$')
MARKER = re.compile(r'^ *<!-- sample: (\S+) ([\w-]+) -->$')
REGION_LINE = re.compile(r'^[ \t]*// docs:([\w-]+)[ \t]*$')


def regions(path):
    """{name: text} for a sample, or raises ValueError for unclosed, nested or repeated regions."""
    lines = path.read_text(encoding='utf-8').splitlines()
    where = path.relative_to(ROOT)
    found, current, start = {}, None, 0
    for number, line in enumerate(lines, 1):
        marker = REGION_LINE.match(line)
        if not marker:
            continue
        if marker[1] == 'end':
            if current is None:
                raise ValueError(f'{where}:{number}: docs:end without a region')
            found[current] = textwrap.dedent('\n'.join(lines[start:number - 1])).strip('\n')
            current = None
        elif current is not None:
            raise ValueError(f'{where}:{number}: region {marker[1]} starts inside {current}')
        elif marker[1] in found or marker[1] == 'file':
            raise ValueError(f'{where}:{number}: region {marker[1]} is defined twice ("file" is the whole file)')
        else:
            current, start = marker[1], number
    if current is not None:
        raise ValueError(f'{where}: region {current} has no docs:end')
    found['file'] = '\n'.join(line for line in lines if not REGION_LINE.match(line)).strip('\n')
    return found


def fences(lines):
    """Yield (open line index, close line index, indent, language, options) for each fenced block.

    A fence may be indented (inside a list item); it closes at a line of the same character, at least
    as long, and nothing else. Blocks inside a longer fence (```` around ```) aren't fences of their own.
    """
    i = 0
    while i < len(lines):
        opening = OPEN.match(lines[i])
        if not opening:
            i += 1
            continue
        indent, mark, lang, options = len(opening[1]), opening[2], opening[3].lower(), opening[4].split()
        close = re.compile(rf'^ *{re.escape(mark[0])}{{{len(mark)},}}[ \t]*$')
        j = i + 1
        while j < len(lines) and not close.match(lines[j]):
            j += 1
        yield i, j, indent, lang, options
        i = j + 1


def pages(only=()):
    """Every page, or only the given ones (paths, as docs/how-to/x.md)."""
    if only:
        yield from (Path(page).resolve() for page in only)
        return
    for section in SECTIONS:
        yield from sorted((DOCS / section).glob('*.md'))


def main():
    update = '--update' in sys.argv[1:]
    only = [arg for arg in sys.argv[1:] if not arg.startswith('--')]
    errors, updated, checked = [], 0, 0
    cache = {}
    for page in pages(only):
        lines = page.read_text(encoding='utf-8').split('\n')
        name = page.relative_to(ROOT)
        replacements = []
        markers_used = set()
        for first, last, indent, lang, options in fences(lines):
            line = first + 1
            body = '\n'.join(l[indent:] if l[:indent].strip() == '' else l.lstrip() for l in lines[first + 1:last])

            def check_width(code_text):
                # Reference pages are generated from lib/'s comments: a wide line there scrolls.
                if page.parent.name == 'reference':
                    return
                for number, code in enumerate(code_text.splitlines(), line + 1):
                    if len(code) > WIDTH:
                        errors.append(f'{name}:{number}: {len(code)} columns, more than {WIDTH}')

            if lang not in CHECKED:
                continue
            if 'no-sample' in options:
                check_width(body)
                continue
            marker = MARKER.match(lines[first - 1]) if first > 0 else None
            if not marker:
                errors.append(f'{name}:{line}: {lang} fence without a <!-- sample: ... --> marker on the '
                              f'line before it (or open it with ```{lang} no-sample)')
                continue
            markers_used.add(first - 1)
            sample, region = ROOT / marker[1], marker[2]
            if not sample.is_file():
                errors.append(f'{name}:{line}: no sample {marker[1]}')
                continue
            try:
                if sample not in cache:
                    cache[sample] = regions(sample)
            except ValueError as error:
                errors.append(str(error))
                cache[sample] = {}
                continue
            if region not in cache[sample]:
                errors.append(f'{name}:{line}: no region {region} in {marker[1]}')
                continue
            checked += 1
            want = cache[sample][region]
            check_width(want if update else body)
            if body.strip('\n') != want:
                if update:
                    pad = ' ' * indent
                    replacements.append((first + 1, last, [pad + l if l else l for l in want.split('\n')]))
                else:
                    errors.append(f'{name}:{line}: differs from {marker[1]} region {region} '
                                  '(run scripts/check-doc-snippets.py --update)')
        for number, text in enumerate(lines):
            if MARKER.match(text) and number not in markers_used:
                errors.append(f'{name}:{number + 1}: sample marker not followed by a C# or F# fence')
        if replacements:
            for start, end, new in reversed(replacements):
                lines[start:end] = new
            page.write_text('\n'.join(lines), encoding='utf-8')
            updated += len(replacements)
    for error in errors:
        print(error, file=sys.stderr)
    if errors:
        sys.exit(1)
    print(f'Checked {checked} excerpts' + (f', updated {updated}' if update else '') + '.')


if __name__ == '__main__':
    main()
