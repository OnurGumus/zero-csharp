#!/usr/bin/env python3
"""Generate docs/reference/*.md from the public types in lib/*.cs.

Each page starts with the file's header comment, then lists the type's public members (and those of
its public nested types) with their signatures as written and their /// comments. The pages are
generated; edit lib/ instead.
"""
import re
import sys
import textwrap
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
LIB = ROOT / 'lib'
OUT = ROOT / 'docs/reference'
WIDTH = 80

# Page order: (file, types documented on the page, page name, one-line description for the index).
# The file can be a tuple of files, for partial classes split across them: the page's header comment
# is the first file's, and a type's members come from every file, in order.
PAGES = [
    ('Arena.cs', ['Arena'], 'arena', 'Growable bump allocator: allocation, scopes, reset, debug checks.'),
    ('Scratch.cs', ['Arena', 'ScratchScope', 'ScratchList', 'ScratchText', 'ScratchDict', 'ScratchHashSet', 'ScratchPool'], 'scratch', 'Temporary memory whose lifetime the compiler checks: Arena.Scratch, ScratchScope and its lists, text, dictionaries, sets and pools.'),
    ('List.cs', ['List'], 'list', 'Growable array of unmanaged items in an arena.'),
    ('Pool.cs', ['Pool', 'Handle'], 'pool', 'Items removed one by one, found by handles that notice when they are out of date: Pool<T> and Handle<T>.'),
    ('LinkedList.cs', ['LinkedList', 'LinkedListNode'], 'linkedlist', 'Doubly linked list in an arena, built on Pool<T>.'),
    ('Ptr.cs', ['Ptr', 'Arena'], 'ptr', 'Pointers between items in an arena, for linked lists, trees and graphs: Ptr<T> and Arena.New.'),
    ('Dict.cs', ['Dict', 'DefaultEqualityComparer', 'ByteEqualityComparer'], 'dict', 'Hash map with unmanaged keys and values in an arena, and the comparers for its keys.'),
    ('HashSet.cs', ['HashSet'], 'hashset', 'Set of unmanaged items in an arena.'),
    ('Queue.cs', ['Queue', 'Stack', 'PriorityQueue'], 'queue', 'Queue, stack and priority queue of unmanaged items in an arena.'),
    ('RefList.cs', ['RefList'], 'reflist', 'List of references: strings, classes, arrays.'),
    ('StrDict.cs', ['StrDict'], 'strdict', 'Hash map with string keys compared by text, values of any type.'),
    ('RefDict.cs', ['RefDict'], 'refdict', 'Hash map with unmanaged keys and reference values.'),
    ('Regex.cs', ['Regex', 'RegexMatch', 'RegexGroups', 'RegexGroup', 'RegexMatches', 'RegexSplit', 'RegexOptions'], 'regex', 'Regular expressions compiled into an arena, matched in linear time.'),
    ('Seq.cs', ['Seq', 'Query'], 'seq', 'Allocation-free query operators over spans: Where, Select, Sum, ...'),
    ('Threads.cs', ['Parallel'], 'parallel', 'Loops whose iterations run on every processor.'),
    ('Str.cs', ['Str', 'MemoryExtensions'], 'str', 'Parsing, comparing and arena strings; span and string operations.'),
    ('StrBuilder.cs', ['StrBuilder'], 'strbuilder', 'Growable string buffer in an arena.'),
    ('Out.cs', ['Out'], 'out', 'Buffered, UTF-8 standard output with $"..." formatting.'),
    ('In.cs', ['In'], 'in', 'Reading lines from standard input.'),
    (('File.cs', 'FileAsync.cs'), ['File', 'FileInfo'], 'file', 'Reading and writing files, whole or piece by piece; copying, moving and looking at them.'),
    ('FileSystem.cs', ['FileError'], 'file-error', 'Why a file or directory operation failed.'),
    (('Streams.cs', 'FileAsync.cs'), ['Stream', 'MemoryStream', 'FileStream', 'SeekOrigin', 'FileMode', 'FileAccess', 'FileShare', 'IOException', 'EndOfStreamException'], 'stream', 'System.IO streams: Stream, MemoryStream and FileStream.'),
    ('Net.cs', ['IPAddress', 'IPEndPoint', 'EndPoint', 'Dns'], 'ipaddress', 'IP addresses, endpoints and name lookup (System.Net).'),
    (('Sockets.cs', 'SocketsAsync.cs'), ['Socket', 'TcpListener', 'TcpClient', 'UdpClient', 'NetworkStream', 'SocketError', 'SocketException', 'AddressFamily', 'SocketType', 'ProtocolType', 'SocketFlags', 'SocketShutdown', 'SocketReceiveFromResult', 'UdpReceiveResult'], 'socket', 'TCP and UDP sockets that work with fibers (System.Net.Sockets).'),
    ('CryptoHashes.cs', ['SHA256', 'SHA384', 'SHA512', 'HMACSHA256', 'HMACSHA384', 'HMACSHA512', 'IncrementalHash', 'HKDF', 'HashAlgorithmName', 'CryptographicOperations', 'CryptographicException'], 'hashes', 'SHA-2 hashes, HMAC, HKDF and constant-time comparison (System.Security.Cryptography).'),
    ('CryptoChaCha.cs', ['ChaCha20Poly1305', 'AuthenticationTagMismatchException'], 'chacha20poly1305', 'Encryption with authentication: ChaCha20-Poly1305 (RFC 8439).'),
    ('CryptoX25519.cs', ['X25519'], 'x25519', 'Key agreement over an open channel: X25519 (RFC 7748).'),
    ('CryptoRandom.cs', ['RandomNumberGenerator'], 'random-number-generator', 'Random bytes from the operating system, for keys and nonces.'),
    ('EncryptedStream.cs', ['EncryptedStream'], 'encrypted-stream', 'A stream that encrypts what goes through it, in TLS 1.3 records.'),
    ('Path.cs', ['Path'], 'path', 'The parts of a path, joining paths, and tidying them up.'),
    ('Directory.cs', ['Directory', 'DirectoryEntries', 'DirectoryEntry'], 'directory', 'Creating, listing, moving and deleting directories; the current directory.'),
    ('Clock.cs', ['Clock', 'CalendarTime'], 'clock', 'The monotonic clock for durations, and the date and time of day.'),
    ('TimeProvider.cs', ['TimeProvider', 'ITimer'], 'time-provider', 'Where time comes from: the system clock, or a fake one in tests.'),
    ('Random.cs', ['Random'], 'random', 'Random numbers with .NET\'s method names, the same on every target.'),
    ('Kernels.cs', ['Kernels'], 'kernels', 'SIMD math for inference: dot products, softmax, norms.'),
    ('Dos.cs', ['Dos'], 'dos', 'DOS only: VGA graphics, keyboard, mouse, speaker, files, clock.'),
    ('Uefi.cs', ['Uefi'], 'uefi', 'UEFI only: the same members as Dos, plus Shutdown and Reboot.'),
    ('C64.cs', ['C64'], 'c64', 'Commodore 64 only: the screen and its colors, sprites, the raster line, keyboard, joystick, clock.'),
]
# Public nested types that are compiler plumbing: readers never name them.
PLUMBING = {'Enumerator', 'InterpolatedHandler'}
TYPE_DECL = re.compile(r'^(\s*)((?:public|internal|private)\s+)?((?:static|readonly|ref|unsafe|sealed|partial|abstract)\s+)*'
                       r'(class|struct|interface|enum|record(?:\s+struct|\s+class)?)\s+(\w+)')
warnings = []


def header_comment(lines):
    """The file's leading // comment as Markdown: text as paragraphs, indented lines as code."""
    comment = []
    for line in lines:
        if not line.startswith('//'):
            break
        comment.append(line[3:] if line.startswith('// ') else line[2:])
    blocks, current, code, bullet = [], [], False, False
    for line in comment + ['']:
        if bullet and line.startswith('  ') and line.strip():
            current[-1] += ' ' + line.strip()  # a bullet's continuation line
            continue
        bullet = line.startswith('- ')
        if bullet and current and not code:
            blocks.append(('text', ' '.join(current)))  # each bullet is its own line
            current = []
        is_code = bool(line.startswith('  ') and line.strip())
        if not line.strip() or is_code != code:
            if current:
                blocks.append(('code', textwrap.dedent('\n'.join(current))) if code else ('text', ' '.join(current)))
            current = []
            code = bool(is_code)
            if not line.strip():
                continue
        current.append(line if code else line.strip())
    out = []
    for kind, text in blocks:
        out.append(f'```csharp no-sample\n{text}\n```' if kind == 'code' else text)
    return '\n\n'.join(out)


def inline(element):
    """Text of an XML doc element with <c>, <see>, <paramref> as Markdown."""
    parts = [element.text or '']
    for child in element:
        if child.tag == 'c':
            parts.append(f'`{child.text or ""}`')
        elif child.tag == 'see':
            target = child.get('cref') or child.get('langword') or ''
            parts.append(f'`{target.split(":")[-1]}`')
        elif child.tag in ('paramref', 'typeparamref'):
            parts.append(f'`{child.get("name")}`')
        elif child.tag == 'code':
            parts.append(f'\n\n```csharp no-sample\n{textwrap.dedent(child.text or "").strip()}\n```\n\n')
        elif child.tag == 'para':
            parts.append(f'\n\n{inline(child)}\n\n')
        else:
            parts.append(inline(child))
        parts.append(child.tail or '')
    text = ''.join(parts)
    # Collapse the comment's line breaks, but keep code blocks as they are.
    # A line starting with "- " is a list item: keep it on its own line.
    pieces = re.split(r'(```.*?```)', text, flags=re.S)
    def collapse(piece):
        # Inside a list, an unindented line that isn't an item starts a new paragraph.
        out, in_list = [], False
        for raw in piece.split('\n'):
            if raw.lstrip().startswith('- '):
                in_list = True
            elif in_list and raw.strip() and not raw.startswith((' ', '\t')):
                out.append('\x01')
                in_list = False
            out.append(raw)
        piece = '\n'.join(out)
        piece = re.sub(r'\n[ \t]*- ', '\x00- ', piece)
        piece = re.sub(r'[ \t]*\n[ \t]*(?!\n)', ' ', piece)
        return re.sub(r'\s*\x01\s*', '\n\n', piece.replace('\x00- ', '\n\n- '))
    return ''.join(p if p.startswith('```') else collapse(p) for p in pieces).strip()


def doc_markdown(comment, where):
    if not comment:
        return ''
    try:
        root = ET.fromstring('<doc>' + '\n'.join(comment) + '</doc>')
    except ET.ParseError:
        warnings.append(f'{where}: /// comment is not valid XML; shown as text')
        return ' '.join(comment)
    out = []
    for element in root:
        text = inline(element)
        if element.tag == 'summary':
            out.append(text)
        elif element.tag == 'param':
            out.append(f'- `{element.get("name")}`: {text}')
        elif element.tag == 'typeparam':
            out.append(f'- `{element.get("name")}` (type): {text}')
        elif element.tag == 'returns':
            out.append(f'**Returns:** {text}')
        elif element.tag == 'remarks':
            out.append(text)
        elif element.tag == 'safety':
            out.append(f'**Safety:** {text}')
        else:
            out.append(text)
    return '\n\n'.join(out)


def signature(lines, start):
    """The declaration starting at lines[start], without its body, and wrapped to WIDTH."""
    text = ''
    for i in range(start, len(lines)):
        line = re.sub(r'\s*//.*$', '', lines[i])  # a trailing comment isn't part of the declaration
        text += (' ' if text else '') + line.strip()
        if re.search(r'(=>|\{|;)', line):
            break
        following = lines[i + 1].strip() if i + 1 < len(lines) else '{'
        # A declaration ends where its parentheses balance and a body, an arrow or a constraint follows.
        if text.count('(') == text.count(')') and following.startswith(('{', '=>', 'where', '///', '[')):
            break
        if not following or following.startswith('//'):
            break
    text = text.split(';', 1)[0]
    text = re.split(r'\s*=>', text, 1)[0]
    text = re.split(r'\s*\{', text, 1)[0]
    text = text.rstrip(';').strip()
    # Parameter attributes ([InterpolatedStringHandlerArgument("arena")]) are for the compiler.
    text = re.sub(r'\[\w+(\([^\]]*\))?\]\s*', '', text)
    if len(text) > WIDTH and '(' in text:
        head, rest = text.split('(', 1)
        args, tail = rest.rsplit(')', 1)
        params, depth, current = [], 0, ''
        for char in args:
            depth += char in '<([' and 1 or char in '>)]' and -1 or 0
            if char == ',' and depth == 0:
                params.append(current.strip())
                current = ''
            else:
                current += char
        params.append(current.strip())
        text = head + '(\n' + ',\n'.join('    ' + p for p in params) + ')' + tail
    return text


def member_name(sig):
    if re.search(r'\bthis\s*\[', sig):
        return 'this[]'
    operator = re.search(r'\boperator\s*([^\s(]+)', sig)
    if operator:
        return f'operator {operator[1]}'
    # const int Width = 320, Height = 200: every name it declares.
    if re.match(r'public\s+(static\s+|readonly\s+)*const\b', sig) and ',' in sig:
        names = re.findall(r'(\w+)\s*=', sig)
        if names:
            return ', '.join(names)
    head = sig.split('(')[0].split('=')[0]
    # Name<TState, TResult>: drop the type parameters before splitting on
    # spaces, which the one after the comma would otherwise break.
    while re.search(r'<[^<>]*>', head):
        head = re.sub(r'<[^<>]*>', '', head)
    first = head.split()
    name = first[-1] if first else sig
    if name.startswith('this['):
        return 'this[]'
    return re.sub(r'<.*', '', name)


def collect(path, wanted):
    """{type name: (kind, [(name, signature, markdown)])} for the wanted types and their public nested types."""
    lines = path.read_text(encoding='utf-8').splitlines()
    owners = {}      # indent -> type declared at that indent (the innermost one seen so far)
    public = {}      # indent -> whether that type is public and documented
    found = {}
    comment = []
    for index, line in enumerate(lines):
        stripped = line.strip()
        if stripped.startswith('///'):
            comment.append(stripped[3:].removeprefix(' '))
            continue
        if stripped.startswith('[') and comment:
            continue  # an attribute between a comment and its declaration
        indent = len(line) - len(line.lstrip())
        decl = TYPE_DECL.match(line)
        if decl:
            name = decl[5]
            parent = owners.get(indent - 4)
            is_public = (decl[2] or '').strip() == 'public'
            if indent == 0 or (parent is None and indent == 4):
                documented = is_public and name in wanted
                key = name
            else:
                documented = is_public and public.get(indent - 4) and name not in PLUMBING
                key = f'{owners[indent - 4]}.{name}' if indent - 4 in owners else name
            owners[indent] = key
            public[indent] = documented
            for deeper in [k for k in owners if k > indent]:
                del owners[deeper], public[deeper]
            if documented:
                found[key] = (decl[3] or '').strip() + decl[4], doc_markdown(comment, f'{path.name}:{index + 1}'), []
            comment = []
            continue
        if stripped.startswith('public ') and public.get(indent - 4):
            sig = signature(lines, index)
            owner = owners[indent - 4]
            if not comment:
                warnings.append(f'{path.name}:{index + 1}: {owner}.{member_name(sig)} has no /// comment')
            found[owner][2].append((member_name(sig), sig, doc_markdown(comment, f'{path.name}:{index + 1}')))
        if stripped and not stripped.startswith('//'):
            comment = []
    return lines, found


def page(number, filenames, types, slug):
    if isinstance(filenames, str):
        filenames = (filenames,)
    lines, found = None, {}
    for filename in filenames:
        file_lines, file_found = collect(LIB / filename, set(types))
        lines = lines or file_lines
        for key, (kind, doc, members) in file_found.items():
            if key in found:
                found[key][2].extend(members)  # a partial class's other part
            else:
                found[key] = (kind, doc, members)
    filename = filenames[0]
    title = types[0]
    sources = ', '.join(f'[`lib/{f}`](https://github.com/OnurGumus/zero-csharp/blob/master/lib/{f})' for f in filenames)
    out = [f'---\ntitle: {title}\nindex: {number + 1}\n---\n', f'# {title}\n',
           f'Source: {sources}\n',
           'This page is generated from the source by `scripts/gen-reference.py`.\n']
    intro = header_comment(lines)
    if intro:
        out.append(intro + '\n')
    # The page's own type first, then the others in source order.
    for key in sorted(found, key=lambda k: k != title):
        kind, doc, members = found[key]
        if key != title:
            heading = 'Span and string operations' if key == 'MemoryExtensions' else key
            out.append(f'## {heading}\n')
            if key == 'MemoryExtensions':
                out.append('Extension methods on `ReadOnlySpan<char>` and `string`, under the names '
                           '.NET uses (`System.MemoryExtensions`).\n')
            if doc:
                out.append(doc + '\n')
        elif doc:
            out.append(doc + '\n')
        level = '###' if key != title else '##'
        if key == title:
            out.append('## Members\n')
            level = '###'
        last = None
        for name, sig, text in members:
            if name != last:
                out.append(f'{level} {name}\n')
                last = name
            out.append(f'```csharp no-sample\n{sig}\n```\n')
            if text:
                out.append(text + '\n')
    missing = set(types) - set(found)
    if missing:
        sys.exit(f'lib/{filename}: no public type {", ".join(sorted(missing))}')
    return '\n'.join(out)


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    for old in OUT.glob('*.md'):
        old.unlink()
    index = ['---\ntitle: Reference\nindex: 0\n---\n', '# Reference\n',
             'One page per type in `lib/`, the library zero-mode programs build on. The pages are generated '
             'from the source and its `///` comments. For how the pieces fit together, start with the '
             '[concepts](../concepts/index.html).\n',
             '| Type | What it is |', '|---|---|']
    for number, (filename, types, slug, summary) in enumerate(PAGES):
        (OUT / f'{slug}.md').write_text(page(number, filename, types, slug), encoding='utf-8')
        index.append(f'| [`{types[0]}`]({slug}.html) | {summary} |')
    (OUT / 'index.md').write_text('\n'.join(index) + '\n', encoding='utf-8')
    for warning in warnings:
        print('warning: ' + warning, file=sys.stderr)
    print(f'Generated {len(PAGES)} reference pages ({len(warnings)} warnings).')


if __name__ == '__main__':
    main()
