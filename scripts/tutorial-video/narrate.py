"""Speak a tutorial script and write the timing that the recording driver and the composer follow.

A script (tutorials/<name>.json) has steps with one text per language. {cue} marks the word at which something happens
on screen; [shown|spoken] gives a pronunciation that differs from the subtitle. Each step is one request; a step whose
spoken text, voice and model are unchanged is reused from the work folder, so editing one sentence costs one sentence.

Output in <work>/<name>/<language>: <step>.mp3, <step>.json (cache) and timing.json
({"steps": [{"id", "audio", "seconds", "cues": {name: seconds}, "captions": [{"start", "end", "text"}]}]}).

Usage: python scripts/tutorial-video/narrate.py fis-event --language fi --work C:/OpenSkiTimeVideos [--dry-run]
"""
import argparse
import hashlib
import json
from pathlib import Path
import re
import subprocess

import elevenlabs

HERE = Path(__file__).resolve().parent
TOKEN = re.compile(r'\{(\w+)\}|\[([^|\]]+)\|([^\]]+)\]')


def pronounce(text, say):
    """Applies the script's word pronunciations ("say") to text outside [shown|spoken], longest word first."""
    for word in sorted(say, key=len, reverse=True):
        text = re.sub(rf'(?<![\w|\[]){re.escape(word)}(?!\w|[^\[]*\])', f'[{word}|{say[word]}]', text)
    return text


def parse(text):
    """Returns (spoken text, {cue: index in spoken}, [(spoken index, shown text piece)])."""
    spoken, cues, shown = '', {}, []
    position = 0
    for match in TOKEN.finditer(text):
        plain = text[position:match.start()]
        shown += [(len(spoken) + i, c) for i, c in enumerate(plain)]
        spoken += plain
        if match.group(1):
            if match.group(1) in cues:
                raise SystemExit(f'Cue {match.group(1)} is used twice: {text}')
            cues[match.group(1)] = len(spoken)
        else:
            shown.append((len(spoken), match.group(2)))
            spoken += match.group(3)
        position = match.end()
    plain = text[position:]
    shown += [(len(spoken) + i, c) for i, c in enumerate(plain)]
    return spoken + plain, cues, shown


def captions(shown, starts, ends):
    """Subtitle lines: sentences, split at a comma or colon when longer than 84 characters."""
    lines, current, first = [], '', None
    def flush(last):
        nonlocal current, first
        if current.strip():
            lines.append({'start': round(starts[first], 3), 'end': round(ends[last], 3), 'text': current.strip()})
        current, first = '', None
    full = ''.join(piece for _, piece in shown)
    for i, (index, piece) in enumerate(shown):
        if first is None and piece.strip():
            first = index
        current += piece
        last = (shown[i + 1][0] if i + 1 < len(shown) else len(starts)) - 1
        following = shown[i + 1][1] if i + 1 < len(shown) else ' '
        if following[:1] == ' ' and (piece[-1:] in '.!?' or (piece[-1:] in ',:' and len(current) > 42 and len(full) > 84)):
            flush(last)
    flush(len(starts) - 1)
    return lines


def duration(path):
    return float(subprocess.run(['ffprobe', '-v', 'error', '-show_entries', 'format=duration', '-of', 'csv=p=0', str(path)],
                                check=True, capture_output=True, text=True).stdout)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('name')
    parser.add_argument('--language', required=True, choices=('fi', 'en'))
    parser.add_argument('--work', required=True)
    parser.add_argument('--dry-run', action='store_true', help='print what would be spoken, without calling ElevenLabs')
    args = parser.parse_args()
    script = json.loads((HERE / 'tutorials' / f'{args.name}.json').read_text(encoding='utf-8'))
    folder = Path(args.work) / args.name / args.language
    folder.mkdir(parents=True, exist_ok=True)
    voice, timing, paid = None, [], 0
    for step in script['steps']:
        spoken, cues, shown = parse(pronounce(step[args.language], script.get('say', {}).get(args.language, {})))
        digest = hashlib.sha256(json.dumps([spoken, script['voice'], elevenlabs.MODEL, args.language]).encode()).hexdigest()
        audio, cache = folder / f'{step["id"]}.mp3', folder / f'{step["id"]}.json'
        saved = json.loads(cache.read_text(encoding='utf-8')) if cache.exists() and audio.exists() else {}
        if saved.get('digest') != digest:
            paid += len(spoken)
            print(f'{step["id"]}: {spoken}')
            if args.dry_run:
                continue
            voice = voice or elevenlabs.voice_id(script['voice'])
            data, starts, ends = elevenlabs.speak(spoken, voice, args.language)
            audio.write_bytes(data)
            saved = {'digest': digest, 'starts': starts, 'ends': ends}
            cache.write_text(json.dumps(saved), encoding='utf-8')
        timing.append({'id': step['id'], 'audio': audio.name, 'seconds': round(duration(audio), 3),
                       'cues': {k: round(saved['starts'][min(v, len(saved['starts']) - 1)], 3) for k, v in cues.items()},
                       'captions': captions(shown, saved['starts'], saved['ends'])})
    print(f'{paid} characters {"would be" if args.dry_run else "were"} spoken.')
    if not args.dry_run:
        (folder / 'timing.json').write_text(json.dumps({'tutorial': args.name, 'language': args.language, 'steps': timing},
                                                       indent=1, ensure_ascii=False), encoding='utf-8')
        print(f'{folder / "timing.json"}: {sum(s["seconds"] for s in timing):.0f} s of speech in {len(timing)} steps')


if __name__ == '__main__':
    main()
