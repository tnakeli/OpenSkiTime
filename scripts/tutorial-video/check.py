"""Check a composed tutorial before anyone else sees it (docs/tutorial-videos.md).

Privacy: no surname, first name or FIS code of the series file, and no technical delegate, is readable in the finished
video. Frames are read with Windows OCR; as a control the same names must be found in the raw recording, so a blind OCR
never passes. Sync: how late every click came after its word. Sound: loudness and peak. Frames stay on this computer.

Usage: python scripts/tutorial-video/check.py --video <mp4> --recording <dir> [--every 1.0]
"""
import argparse
import collections
import json
import os
from pathlib import Path
import re
import sqlite3
import subprocess
import sys
import tempfile
import unicodedata

import compose

HERE = Path(__file__).resolve().parent


def plain(value):
    return ''.join(c for c in unicodedata.normalize('NFKD', value) if not unicodedata.combining(c)).casefold()


def delegate_texts(value):
    """The names and numbers of people stored in JSON, such as the technical delegate of a competition's calendar data."""
    try:
        data = json.loads(value)
    except (TypeError, ValueError):
        return []
    found = []

    def walk(item, person=False):
        if isinstance(item, dict):
            for key, child in item.items():
                if person and isinstance(child, str) and key.casefold() != 'nation':
                    found.append(child)
                else:
                    walk(child, person or re.search(r'delegate', key, re.I) is not None)
        elif isinstance(item, list):
            for child in item:
                walk(child, person)
    walk(data)
    return found


def private_words(series):
    """Names and codes of the people in the series file, as single words of four letters or more."""
    db = sqlite3.connect(f'file:{Path(series).as_posix()}?mode=ro', uri=True)
    try:
        words, tables = set(), [r[0] for r in db.execute("select name from sqlite_master where type='table'")]
        for table in tables:
            columns = [r[1] for r in db.execute(f'pragma table_info("{table}")')]
            wanted = [c for c in columns if re.search(r'surname|first_?name|last_?name|federation_?code|td_?(name|number)|delegate', c, re.I)]
            for column in wanted:
                for (value,) in db.execute(f'select distinct "{column}" from "{table}" where "{column}" is not null'):
                    words |= {w for w in re.findall(r'\w+', plain(str(value))) if len(w) >= 4}
            for column in [c for c in columns if re.search(r'json$', c, re.I)]:
                for (value,) in db.execute(f'select distinct "{column}" from "{table}" where "{column}" is not null'):
                    for text in delegate_texts(value):
                        words |= {w for w in re.findall(r'\w+', plain(text)) if len(w) >= 4}
    finally:
        db.close()
    return words


def read(video, every, folder, start=None, seconds=None):
    cut = [] if start is None else ['-ss', str(start), '-t', str(seconds)]
    # Twice the size: the operator can read small text better than OCR does.
    compose.run([*cut, '-i', str(video), '-vf', f'fps=1/{every},scale=iw*1.5:-1:flags=lanczos', str(folder / '%05d.png')])
    result = subprocess.run(['powershell.exe', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', str(HERE / 'Read-FrameText.ps1'), '-Folder', str(folder)],
                            check=True, capture_output=True).stdout.decode('utf-8', 'replace')
    return {name: plain(text) for name, _, text in (line.partition('\t') for line in result.splitlines() if '\t' in line)}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--video', required=True)
    parser.add_argument('--recording', required=True)
    parser.add_argument('--every', type=float, default=1.0, help='seconds between checked frames')
    args = parser.parse_args()
    recording = Path(args.recording)
    data = json.loads((recording / 'recording.json').read_text(encoding='utf-8'))
    failed = False
    # The Windows account name can appear where the application defaults to it (the operator of timing corrections).
    account = {w for w in re.findall(r'\w+', plain(os.environ.get('USERNAME', ''))) if len(w) >= 4}
    if data['facts'].get('fictional') == 'true':
        with tempfile.TemporaryDirectory(dir=recording) as a:
            seen = read(args.video, args.every, Path(a)) if account else {}
        hits = sorted({round((int(Path(n).stem) - 1) * args.every) for n, t in seen.items() if set(re.findall(r'\w+', t)) & account})
        print(f'Privacy: the event is fictional; the Windows account name is readable in {len(hits)} of {len(seen)} frames.')
        if hits:
            print(f'  REVIEW the account name at {hits[:6]} s')
            failed = True
    else:
        words = private_words(data['facts']['seriesFile']) | account
        # Words of the application and the narration are not names even if an athlete is called the same.
        allowed = {'levi', 'race', 'alpine', 'club', 'final', 'mark', 'page', 'event', 'start', 'list', 'team'}
        words -= allowed
        with tempfile.TemporaryDirectory(dir=recording) as a, tempfile.TemporaryDirectory(dir=recording) as b:
            seen = read(args.video, args.every, Path(a))
            hits = collections.defaultdict(list)
            for name, text in seen.items():
                for word in set(re.findall(r'\w+', text)) & words:
                    hits[word].append(round((int(Path(name).stem) - 1) * args.every))
            # The control: the same reading of the raw recording, around the moment the names were first on screen.
            shown = [f for f in data['freezes']][-1]['to'] - data['steps'][0]['start']
            control = read(recording / 'raw.mkv', 1, Path(b), start=shown + 3, seconds=3)
            found = set().union(*(set(re.findall(r'\w+', t)) for t in control.values())) & words if control else set()
        print(f'Privacy: {len(words)} private words, {len(seen)} frames read; readable in the video: {len(hits)}; control found {len(found)} in the raw recording.')
        if len(found) < 5:
            print('  FAILED: the control did not find names in the raw recording, so this check proves nothing.')
            failed = True
        for word, times in sorted(hits.items()):
            print(f'  REVIEW "{word}" at {times[:6]} s')
            failed = True
    # The composed video: where each click on a word is, compared with the moment the word is spoken.
    timing = json.loads((recording.parent / 'timing.json').read_text(encoding='utf-8'))
    clock = compose.Clock(data, timing)
    starts, cues = {s['id']: s['start'] for s in data['steps']}, {s['id']: s['cues'] for s in timing['steps']}
    late = sorted(((float(clock.to_output(c['at']) - clock.to_output(starts[c['step']])) - cues[c['step']][c['cue']], c['step'], c['cue'])
                   for c in data['clicks'] + data.get('events', []) if c.get('cue')), key=lambda x: -abs(x[0]))
    print(f'Sync: {len(late)} clicks on a word, worst ' + ', '.join(f'{step}/{cue} {x:+.2f} s' for x, step, cue in late[:3])
          + f'; recorded {max(c["late"] for c in data["clicks"] + data.get("events", []) if c.get("late") is not None):.1f} s late at most')
    failed |= bool(late) and abs(late[0][0]) > 0.15
    level, peak = compose.loudness(Path(args.video))
    print(f'Sound: {level:.1f} LUFS, peak {peak:.1f} dBFS')
    failed |= not -15.5 <= level <= -12.5 or peak > -0.5
    print('FAILED: see above.' if failed else 'All checks passed.')
    return 1 if failed else 0


if __name__ == '__main__':
    sys.exit(main())
