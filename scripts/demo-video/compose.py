"""Compose the OpenSkiTime demonstration video from a real screen recording.

Input (--recording directory): raw.mp4 (1920x1032 recording of the running application), chapters.json written by the
recording driver ([{"start", "end", "title", "caption", "speed"}]) and optionally pdf-pages/*.png (rendered pages of
the PDFs the application generated, shown as a closing showcase).
Output: a 1920x1080 H.264 MP4 with title and closing cards, a lower-third caption band per chapter, short fades,
accelerated sequences (marked) and the PDF showcase. Only ffmpeg is required.

Usage: python scripts/demo-video/compose.py --recording <dir> --output <file.mp4>
"""
import argparse
import json
import shutil
import subprocess
import tempfile
from pathlib import Path

FONT = 'C\\:/Windows/Fonts/segoeui.ttf'
FONT_BOLD = 'C\\:/Windows/Fonts/segoeuib.ttf'
FONT_LIGHT = 'C\\:/Windows/Fonts/segoeuil.ttf'
WIDTH, HEIGHT, BAND = 1920, 1080, 48
FPS = 30
NAVY, TEAL, GOLD = '0x0B2430', '0x087E78', '0xF3C969'
ENCODE = ['-c:v', 'libx264', '-preset', 'medium', '-crf', '19', '-pix_fmt', 'yuv420p', '-r', str(FPS)]
SILENCE = ['-f', 'lavfi', '-i', 'anullsrc=r=48000:cl=stereo']


def escape(text):
    return text.replace('\\', '\\\\').replace(':', '\\:').replace("'", '’').replace('%', '\\%').replace(',', '\\,')


def run(args):
    subprocess.run(args, check=True)


def text(content, font, size, color, x, y, alpha=None):
    a = f':alpha=\'{alpha}\'' if alpha else ''
    return f"drawtext=fontfile='{font}':text='{escape(content)}':fontsize={size}:fontcolor={color}:x={x}:y={y}{a}"


def card(path, lines, seconds):
    """A gradient card; lines = [(text, font, size, color, y)]."""
    fade = f'fade=t=in:st=0:d=0.5,fade=t=out:st={seconds - 0.5}:d=0.5'
    draw = ','.join(text(t, f, s, c, '(w-text_w)/2', y) for t, f, s, c, y in lines)
    vf = f"drawbox=x=(iw-140)/2:y=330:w=140:h=8:color={TEAL}:t=fill,{draw},{fade}"
    run(['ffmpeg', '-y', '-loglevel', 'error', '-f', 'lavfi',
         '-i', f'gradients=s={WIDTH}x{HEIGHT}:c0={NAVY}:c1=0x0E5F5A:x0=0:y0=0:x1={WIDTH}:y1={HEIGHT}:d={seconds}:r={FPS}:speed=0.002',
         *SILENCE, '-t', str(seconds), '-vf', vf, *ENCODE, '-c:a', 'aac', '-shortest', str(path)])


def redact_filters(chapter, redactions):
    """Blur file-dialog areas (they list the user's own folders) for the time they were on screen."""
    filters = []
    for r in redactions:
        a, b = max(r['start'], chapter['start']), min(r['end'], chapter['end'])
        if a >= b:
            continue
        x, y = max(r['x'] - 20, 0), max(r['y'] - 20, 0)
        w, h = min(r['w'] + 40, 1920 - x), min(r['h'] + 40, 1032 - y)
        filters.append((a - chapter['start'], b - chapter['start'], x, y, w, h))
    return filters


def segment(raw, path, chapter, index, total, redactions=()):
    start, end, speed = chapter['start'], chapter['end'], float(chapter.get('speed', 1))
    duration = (end - start) / speed
    tag = f'{speed:g}× faster' if speed > 1.05 else ''
    vf = ','.join([
        f'setpts=(PTS-STARTPTS)/{speed}', f'fps={FPS}',
        f'pad={WIDTH}:{HEIGHT}:0:0:color={NAVY}',
        # the window title bar shows local file paths; replace it with a neutral bar
        f'drawbox=x=0:y=0:w=iw:h=24:color=0x0E2A35:t=fill',
        text('OpenSkiTime  ·  Synthetic Alpine Cup 2026', FONT, 15, '0xB8C9D1', 14, 4),
        # the application's status line (just above the band) can show local paths after an export
        *([f'drawbox=x=0:y=1008:w=iw:h=24:color=0xFFFFFF:t=fill'] if chapter.get('maskStatus') else []),
        # lower-third band: chapter number pill, title and caption
        f'drawbox=x=0:y=ih-{BAND}:w=iw:h={BAND}:color={NAVY}:t=fill',
        f'drawbox=x=0:y=ih-{BAND}:w=6:h={BAND}:color={TEAL}:t=fill',
        text(f'{index:02d}', FONT_BOLD, 22, GOLD, 24, f'h-{BAND}+12'),
        text(chapter['title'], FONT_BOLD, 22, 'white', 70, f'h-{BAND}+12'),
        text(chapter['caption'], FONT, 20, '0xCFE3E6', f'70+{len(chapter["title"]) * 12 + 40}', f'h-{BAND}+14'),
        *([text(tag, FONT_BOLD, 18, GOLD, 'w-text_w-24', f'h-{BAND}+15')] if tag else []),
        f'fade=t=in:st=0:d=0.25', f'fade=t=out:st={max(duration - 0.25, 0):.3f}:d=0.25',
    ])
    blur = ''
    for i, (a, b, x, y, w, h) in enumerate(redact_filters(chapter, redactions)):
        blur += (f'[v{i}]split[v{i}a][v{i}b];[v{i}b]crop={w}:{h}:{x}:{y},boxblur=24:3[r{i}];'
                 f"[v{i}a][r{i}]overlay={x}:{y}:enable='between(t,{a:.2f},{b:.2f})'[v{i + 1}];")
        last = i + 1
    graph = f'[0:v]setpts=PTS-STARTPTS[v0];{blur}[v{last if blur else 0}]{vf}[out]'
    run(['ffmpeg', '-y', '-loglevel', 'error', '-ss', f'{start:.3f}', '-to', f'{end:.3f}', '-i', str(raw), *SILENCE,
         '-filter_complex', graph, '-map', '[out]', '-map', '1:a', '-t', f'{duration:.3f}', *ENCODE, '-c:a', 'aac', '-shortest', str(path)])
    return duration


def showcase(pages, path, seconds=8):
    """The generated PDFs side by side on a gradient, with a slow push-in."""
    inputs = []
    for page in pages:
        inputs += ['-loop', '1', '-t', str(seconds), '-i', str(page)]
    count = len(pages)
    page_h = 760
    page_w = int(page_h / 1.414)
    gap = 60
    x0 = (WIDTH - count * page_w - (count - 1) * gap) // 2
    chain = [f'[0:v]format=yuv420p[bg]']
    last = 'bg'
    for i in range(count):
        chain.append(f'[{i + 1}:v]scale={page_w}:{page_h},pad={page_w + 8}:{page_h + 8}:4:4:color=0x000000@0.35[p{i}]')
        chain.append(f'[{last}][p{i}]overlay=x={x0 + i * (page_w + gap) - 4}:y={(HEIGHT - page_h) // 2 - 30}[o{i}]')
        last = f'o{i}'
    chain.append(f"[{last}]zoompan=z='min(zoom+0.0002,1.025)':d={seconds * FPS}:s={WIDTH}x{HEIGHT}:fps={FPS}:x='iw/2-(iw/zoom/2)':y='ih/2-(ih/zoom/2)',"
                 f"drawbox=x=0:y=ih-{BAND}:w=iw:h={BAND}:color={NAVY}:t=fill,drawbox=x=0:y=ih-{BAND}:w=6:h={BAND}:color={TEAL}:t=fill,"
                 + text('PDF', FONT_BOLD, 22, GOLD, 24, f'h-{BAND}+12') + ','
                 + text('Generated reports on the organizer letterhead', FONT_BOLD, 22, 'white', 80, f'h-{BAND}+12') + ','
                 + text('start list · official results · FIS referee report — rendered from the PDFs the application wrote', FONT, 20, '0xCFE3E6', 640, f'h-{BAND}+14')
                 + f',fade=t=in:st=0:d=0.4,fade=t=out:st={seconds - 0.4}:d=0.4[v]')
    run(['ffmpeg', '-y', '-loglevel', 'error', '-f', 'lavfi',
         '-i', f'gradients=s={WIDTH}x{HEIGHT}:c0={NAVY}:c1=0x123B4A:x0=0:y0=0:x1={WIDTH}:y1={HEIGHT}:d={seconds}:r={FPS}',
         *inputs, *SILENCE, '-filter_complex', ';'.join(chain), '-map', '[v]', '-map', f'{count + 1}:a',
         '-t', str(seconds), *ENCODE, '-c:a', 'aac', '-shortest', str(path)])
    return seconds


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--recording', required=True)
    parser.add_argument('--output', required=True)
    args = parser.parse_args()
    if shutil.which('ffmpeg') is None:
        raise SystemExit('ffmpeg is required (winget install Gyan.FFmpeg).')
    recording = Path(args.recording)
    chapters = json.loads((recording / 'chapters.json').read_text(encoding='utf-8'))
    redactions_file = recording / 'redactions.json'
    redactions = json.loads(redactions_file.read_text(encoding='utf-8')) if redactions_file.exists() else []
    # A fresh work folder per composition: nothing that already exists is deleted or overwritten.
    work = Path(tempfile.mkdtemp(prefix='compose-', dir=recording))
    parts = [work / '00-intro.mp4']
    card(parts[0], [('OpenSkiTime', FONT_BOLD, 112, 'white', 370),
                    ('Alpine race timing for the race office', FONT_LIGHT, 44, '0xCFE3E6', 520),
                    ('FIS data · fair draw · live timing · results · branded reports — offline first', FONT, 30, GOLD, 600),
                    ('A complete two-run slalom with 100 synthetic athletes, recorded from the running application', FONT, 24, '0x9FB8BF', 960)], 4.5)
    total = 4.5
    for index, chapter in enumerate(chapters, 1):
        path = work / f'{index:02d}.mp4'
        total += segment(recording / chapter.get('source', 'raw.mp4'), path, chapter, index, len(chapters), redactions)
        parts.append(path)
    pages = sorted((recording / 'pdf-pages').glob('*.png')) if (recording / 'pdf-pages').exists() else []
    if pages:
        parts.append(work / '98-pdf.mp4')
        total += showcase(pages[:3], parts[-1])
    parts.append(work / '99-outro.mp4')
    card(parts[-1], [('OpenSkiTime', FONT_BOLD, 96, 'white', 380),
                     ('Open source alpine race timing', FONT_LIGHT, 40, '0xCFE3E6', 510),
                     ('openskiti.me', FONT_BOLD, 34, GOLD, 590),
                     ('All athletes, clubs and results in this video are synthetic.', FONT, 22, '0x9FB8BF', 960)], 4)
    total += 4
    (work / 'list.txt').write_text(''.join(f"file '{p.name}'\n" for p in parts), encoding='utf-8')
    run(['ffmpeg', '-y', '-loglevel', 'error', '-f', 'concat', '-safe', '0', '-i', str(work / 'list.txt'),
         '-c', 'copy', '-movflags', '+faststart', str(Path(args.output))])
    print(f'Composed {len(chapters)} chapters{" + PDF showcase" if pages else ""}, {total:.0f} s -> {args.output}')


if __name__ == '__main__':
    main()
