"""Compose the OpenSkiTime demonstration video from a real screen recording.

Input: the raw recording of the running application (raw.mp4, 1920x1032 window area) and the chapter log written by
the recording driver (chapters.json: [{"start": s, "end": s, "title": ..., "caption": ..., "speed": n}, ...]).
Output: a 1920x1080 H.264 MP4 with an intro card, chapter titles, captions in a bottom band, accelerated sequences
(speed > 1, marked on screen) and a closing card. Only ffmpeg is required.

Usage: python scripts/demo-video/compose.py --recording <dir> --output <file.mp4>
"""
import argparse
import json
import shutil
import subprocess
from pathlib import Path

FONT = 'C\\:/Windows/Fonts/segoeui.ttf'
FONT_BOLD = 'C\\:/Windows/Fonts/segoeuib.ttf'
WIDTH, HEIGHT, BAND = 1920, 1080, 48
FPS = 30


def escape(text):
    return text.replace('\\', '\\\\').replace(':', '\\:').replace("'", "’").replace('%', '\\%').replace(',', '\\,')


def run(args):
    print(' '.join(str(a) for a in args[:12]), '...')
    subprocess.run(args, check=True)


def card(path, title, subtitle, seconds):
    vf = (f"drawtext=fontfile='{FONT_BOLD}':text='{escape(title)}':fontcolor=white:fontsize=72:x=(w-text_w)/2:y=h/2-90,"
          f"drawtext=fontfile='{FONT}':text='{escape(subtitle)}':fontcolor=0xB8C9D1:fontsize=34:x=(w-text_w)/2:y=h/2+10,"
          f"drawbox=x=(w-120)/2:y=h/2-130:w=120:h=6:color=0x087E78:t=fill")
    run(['ffmpeg', '-y', '-loglevel', 'error', '-f', 'lavfi', '-i', f'color=c=0x102F3B:s={WIDTH}x{HEIGHT}:r={FPS}:d={seconds}',
         '-f', 'lavfi', '-i', f'anullsrc=r=48000:cl=stereo', '-t', str(seconds), '-vf', vf,
         '-c:v', 'libx264', '-preset', 'medium', '-crf', '20', '-pix_fmt', 'yuv420p', '-c:a', 'aac', '-shortest', str(path)])


def segment(raw, path, chapter, index, total):
    raw = raw.parent / chapter.get('source', raw.name)
    start, end, speed = chapter['start'], chapter['end'], chapter.get('speed', 1)
    label = f"{index}/{total}  {chapter['title']}"
    caption = chapter['caption'] + (f'   ·   shown {speed}× faster' if speed > 1 else '')
    vf = (f"setpts=(PTS-STARTPTS)/{speed},fps={FPS},"
          f"pad={WIDTH}:{HEIGHT}:0:0:color=0x102F3B,"
          f"drawbox=x=0:y=ih-{BAND}:w=iw:h={BAND}:color=0x102F3B:t=fill,"
          f"drawtext=fontfile='{FONT}':text='{escape(caption)}':fontcolor=white:fontsize=24:x=24:y=h-{BAND}+(({BAND}-text_h)/2),"
          f"drawbox=x=iw-560:y=ih-{BAND}+8:w=536:h={BAND - 16}:color=0x087E78@0.95:t=fill,"
          f"drawtext=fontfile='{FONT_BOLD}':text='{escape(label)}':fontcolor=white:fontsize=22:x=w-548:y=h-{BAND}+(({BAND}-text_h)/2)")
    duration = (end - start) / speed
    run(['ffmpeg', '-y', '-loglevel', 'error', '-ss', f'{start:.3f}', '-to', f'{end:.3f}', '-i', str(raw),
         '-f', 'lavfi', '-i', 'anullsrc=r=48000:cl=stereo', '-vf', vf, '-t', f'{duration:.3f}',
         '-c:v', 'libx264', '-preset', 'medium', '-crf', '20', '-pix_fmt', 'yuv420p', '-r', str(FPS), '-c:a', 'aac', '-shortest', str(path)])
    return duration


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--recording', required=True)
    parser.add_argument('--output', required=True)
    args = parser.parse_args()
    if shutil.which('ffmpeg') is None:
        raise SystemExit('ffmpeg is required (winget install Gyan.FFmpeg).')
    recording = Path(args.recording)
    chapters = json.loads((recording / 'chapters.json').read_text(encoding='utf-8'))
    # A separately recorded closing chapter replaces the main recording's chapter of the same title.
    results = recording / 'chapters-results.json'
    if results.exists():
        replacement = json.loads(results.read_text(encoding='utf-8'))
        titles = {c['title'] for c in replacement}
        chapters = [c for c in chapters if c['title'] not in titles] + replacement
    work = recording / 'parts'
    work.mkdir(exist_ok=True)
    parts = [work / '00-intro.mp4']
    card(parts[0], 'OpenSkiTime', 'Alpine race timing · a complete synthetic two-run slalom with 100 athletes', 6)
    total = 0.0
    for index, chapter in enumerate(chapters, 1):
        path = work / f'{index:02d}.mp4'
        total += segment(recording / 'raw.mp4', path, chapter, index, len(chapters))
        parts.append(path)
    parts.append(work / '99-outro.mp4')
    card(parts[-1], 'OpenSkiTime', 'Synthetic data only · recorded from the running application', 5)
    (work / 'list.txt').write_text(''.join(f"file '{p.name}'\n" for p in parts), encoding='utf-8')
    run(['ffmpeg', '-y', '-loglevel', 'error', '-f', 'concat', '-safe', '0', '-i', str(work / 'list.txt'),
         '-c', 'copy', '-movflags', '+faststart', str(Path(args.output))])
    print(f'Composed {len(chapters)} chapters, {total + 11:.0f} s -> {args.output}')


if __name__ == '__main__':
    main()
