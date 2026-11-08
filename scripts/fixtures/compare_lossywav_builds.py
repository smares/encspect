"""Compares the outputs of the lossyWAV fixture workflow's two jobs, as data: the Linux job (the POSIX port, the
lossywav-fixtures artifact) and the Windows job (the official build, the lossywav-windows artifact), unpacked into
LINUX and WINDOWS. Samples (data chunks), fact text apart from its time, file and RIFF sizes, chunk order, exit codes,
the probes (--check, --stdout, STDIN), the help text, and the figures of lossywav_figures.py on the WAVs both kept.
It never runs lossyWAV. Prints the counts compared and every difference, and writes them to compare-diffs.json.

Usage: python3 scripts/fixtures/compare_lossywav_builds.py scripts/fixtures LINUX WINDOWS
"""
import sys, json, re, hashlib, pathlib, collections
sys.path.insert(0, sys.argv[1]); import lossywav_figures as F
L, W = pathlib.Path(sys.argv[2]), pathlib.Path(sys.argv[3])
ml, mw = json.load(open(L / 'manifest.json')), json.load(open(W / 'manifest.json'))
DATE = re.compile(r'@ \d\d/\d\d/\d{4} \d\d:\d\d:\d\d')
diffs = collections.defaultdict(list); stats = collections.Counter()

def chunks(path):
    d = path.read_bytes(); return d, dict((i, b) for i, b in __import__('make_lossywav_fixtures').riff_chunks(d))

def code(c):
    """An exit code as one value on both systems: -1 for 255 (POSIX keeps 8 bits) and 4294967295 (Windows, unsigned), and
    "crash" for a signal 11 (POSIX) or 0xC0000005, an access violation (Windows)."""
    return "crash" if c in (-11, 0xC0000005) else -1 if c in (255, 0xFFFFFFFF) else c

def norm(t): return DATE.sub('@ <date>', t) if t else t

print('tools', ml['tools']['lossywav']['sha256'][:16], mw['tools']['lossywav']['sha256'][:16])
# inputs identical?
for n, e in mw['inputs'].items():
    if ml['inputs'].get(n, {}).get('sha256') != e['sha256']: diffs['input'].append(n)
stats['inputs'] = len(mw['inputs'])
# runs
rl = {(r['case'], r['input']): r for r in ml['runs']}
for r in mw['runs']:
    k = (r['case'], r['input']); l = rl.get(k)
    if l is None: diffs['run-missing'].append(k); continue
    stats['runs'] += 1
    stats['exit-' + str(code(r['exit']))] += 1
    if code(l['exit']) != code(r['exit']): diffs['exit'].append((k, l['exit'], r['exit']))
    fl = {pathlib.Path(f['path']).name: f for f in l['files']}; fw = {pathlib.PurePosixPath(f['path'].replace(chr(92), '/')).name: f for f in r['files']}
    if set(fl) != set(fw): diffs['files'].append((k, sorted(fl), sorted(fw))); continue
    for name in fl:
        a, b = fl[name], fw[name]; stats['files'] += 1
        if a.get('dataSha256') != b.get('dataSha256'): diffs['samples'].append((k, name))
        if norm(a.get('fact')) != norm(b.get('fact')): diffs['fact'].append((k, name, a.get('fact'), b.get('fact')))
        if a['size'] != b['size']: diffs['size'].append((k, name, a['size'], b['size']))
# kept WAVs: RIFF header and figures on both sides
for p in sorted((W / 'lossy').rglob('*.wav')):
    q = L / p.relative_to(W)
    if not q.exists(): continue
    da, ca = chunks(q); db, cb = chunks(p); stats['kept'] += 1
    if da[4:8] != db[4:8]: diffs['riff'].append((str(p.relative_to(W)), int.from_bytes(da[4:8], 'little'), int.from_bytes(db[4:8], 'little')))
    if [i for i in ca] != [i for i in cb]: diffs['chunk-order'].append(str(p.relative_to(W)))
    if ca.get(b'data') != cb.get(b'data'): diffs['kept-samples'].append(str(p.relative_to(W)))
    if '.lwcdf' not in p.name and stats['kept-fig'] < 12:
        fa, fb = F.figures(q), F.figures(p); stats['kept-fig'] += 1
        for key in ('lowBitsUnused', 'distribution', 'changes', 'grid', 'changesOnGrid', 'thinSpans', 'timesWithEqualCounts'):
            if fa[key] != fb[key]: diffs['figures'].append((str(p.relative_to(W)), key, fa[key], fb[key]))
# fixture inputs
for r in mw['fixtureRuns']:
    stats['fixture-runs'] += 1
    for f in r['files']:
        f['path'] = f['path'].replace(chr(92), '/')
        q = L / f['path']
        if not q.exists(): diffs['fixture-missing'].append(f['path']); continue
        d, c = chunks(q)
        if hashlib.sha256(c.get(b'data', b'')).hexdigest() != f.get('dataSha256'): diffs['fixture-samples'].append(f['path'])
        if norm(c[b'fact'].decode('latin-1') if b'fact' in c else None) != norm(f.get('fact')): diffs['fixture-fact'].append(f['path'])
        if len(d) != f['size']: diffs['fixture-size'].append((f['path'], len(d), f['size']))
        if f['path'].endswith('.lossy.wav') and stats['fixture-fig'] < 40:
            stats['fixture-fig'] += 1; fa = F.figures(q); fb = F.figures(W / f['path'])
            for key in ('lowBitsUnused', 'distribution', 'changes', 'grid', 'changesOnGrid', 'timesWithEqualCounts'):
                if fa[key] != fb[key]: diffs['fixture-figures'].append((f['path'], key))
# probes
pl = {(p['probe'], p.get('input')): p for p in ml['probes']}
for p in mw['probes']:
    k = (p['probe'], p.get('input')); l = pl.get(k); stats['probes'] += 1
    if l is None: diffs['probe-missing'].append(k); continue
    for key in ('exit', 'size', 'riffSize', 'chunks', 'files'):
        if key in p or key in l:
            if key == 'files' and p['probe'] == 'reprocess': continue
            if (code(l.get(key)) != code(p.get(key))) if key == 'exit' else (l.get(key) != p.get(key)): diffs['probe'].append((k, key, l.get(key), p.get(key)))
    if p['probe'].startswith('std') and 'stderr' in p:
        a = [x for x in l['stderr'].splitlines() if x.startswith(('Settings', 'File Info'))]; b = [x for x in p['stderr'].splitlines() if x.startswith(('Settings', 'File Info'))]
        if a != b: diffs['probe-stderr'].append((k, a, b))
for name in ('stdout-music-44k-16-st.wav', 'stdin-stdout-music-44k-16-st.wav'):
    a, b = (L / 'probes' / name).read_bytes(), (W / 'probes' / name).read_bytes(); stats['stdout-compared'] += 1
    if a != b: diffs['stdout-bytes'].append(name)
# check probe text
for p in sorted((W / 'probes').glob('check-*.txt')):
    a = (L / 'probes' / p.name).read_text(errors='replace'); b = p.read_text(errors='replace')
    la = [x.strip() for x in a.splitlines() if 'FACT' in x]; lb = [x.strip() for x in b.splitlines() if 'FACT' in x]
    stats['check-texts'] += 1
    if [norm(x) for x in la] != [norm(x) for x in lb]: diffs['check-text'].append((p.name, la, lb))
# help text
for p in sorted((W / 'tools').glob('help-*.txt')):
    # Line endings apart: Windows writes CR LF, and lossyWAV's own CR LF then reads as CR CR LF.
    a = (L / 'tools' / p.name).read_bytes().replace(b'\r', b''); b = p.read_bytes().replace(b'\r', b'')
    if a != b: diffs['help'].append(p.name)
print(dict(stats)); print({k: len(v) for k, v in diffs.items()})
json.dump({k: v for k, v in diffs.items()}, open('compare-diffs.json', 'w'), indent=1, default=str)
for k, v in diffs.items(): print(k, v[:6])
