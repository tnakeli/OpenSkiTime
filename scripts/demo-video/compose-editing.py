"""Compose the competitor editing tutorial videos from CompetitorEditingVideoTests recordings.

Each recording directory, written by tests/OpenSkiTime.Desktop.E2E/CompetitorEditingVideoTests.cs, holds raw.mkv
(1920x1032 screen, frames stamped with Unix time), chapters.json ([{"start", "end", "title", "caption", "speed"}]) and
keys.json ([{"at", "label"}], shortcuts to show as key badges) in seconds of the recording clock, window.json (the
recorded window's screen area and clockStart, the Unix time of the clock's zero) and optionally pdf-pages/*.png
(rendered pages of the generated entry lists, shown before the closing card).
--landscape is a recording with maximized windows and gives <name>-16x9.mp4 (1920x1080, YouTube); --portrait is one made
with OPENSKITIME_EDITING_VIDEO_WIDTH (a narrow window, so the Save and Undo buttons stay in the frame) and gives
<name>-9x16.mp4 (1080x1920, Instagram Reels and YouTube Shorts), showing the whole window. Both are H.264 with a silent
AAC track at 30 fps. --tempo speeds up every chapter (the vertical cut must stay under three minutes).
Only ffmpeg is required.

Usage: python scripts/demo-video/compose-editing.py --landscape <dir> --portrait <dir> --output <dir> [--tempo 1.15]
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
FPS = 30
NAVY, ACCENT, GOLD, MUTED, DIM = '0x14234A', '0x2563EB', '0xF3C969', '0xC3CEE6', '0x8796BC'
ENCODE = ['-c:v', 'libx264', '-preset', 'medium', '-crf', '19', '-pix_fmt', 'yuv420p', '-r', str(FPS)]
AUDIO = ['-c:a', 'aac', '-b:a', '128k', '-ar', '48000', '-ac', '2']
SILENCE = ['-f', 'lavfi', '-i', 'anullsrc=r=48000:cl=stereo']
SCREEN_H = 1032
KEY_SECONDS = 1.3
INTRO = ('Editing competitors', 'Copy & paste from a spreadsheet · edit · undo · multi-edit · PDF')


def escape(value):
    return value.replace('\\', '\\\\').replace(':', '\\:').replace("'", '’').replace('%', '\\%').replace(',', '\\,')


def run(args):
    subprocess.run(['ffmpeg', '-y', '-loglevel', 'error', *args], check=True)


def text(content, font, size, color, x, y, enable=None):
    on = f":enable='{enable}'" if enable else ''
    return f"drawtext=fontfile='{font}':text='{escape(content)}':fontsize={size}:fontcolor={color}:x={x}:y={y}{on}"


def wrap(caption, width):
    lines, line = [], ''
    for word in caption.split():
        if line and len(line) + 1 + len(word) > width:
            lines.append(line)
            line = word
        else:
            line = f'{line} {word}'.strip()
    return lines + ([line] if line else [])


class Layout:
    def __init__(self, vertical, window=None):
        self.vertical = vertical
        self.window = window
        self.w, self.h = (1080, 1920) if vertical else (1920, 1080)

    def band(self, title, caption, index):
        """Chapter band: at the top of the vertical frame, as a lower third under the 1032 px screen in the landscape one."""
        if self.vertical:
            lines = wrap(caption, 46)
            return [f'drawbox=x=0:y=0:w=iw:h=260:color={NAVY}:t=fill', f'drawbox=x=0:y=0:w=8:h=260:color={ACCENT}:t=fill',
                    text(f'{index:02d}', FONT_BOLD, 34, GOLD, 44, 46), text(title, FONT_BOLD, 46, 'white', 110, 38),
                    *[text(line, FONT, 32, MUTED, 44, 118 + i * 44) for i, line in enumerate(lines[:3])]]
        return [f'drawbox=x=0:y=ih-48:w=iw:h=48:color={NAVY}:t=fill', f'drawbox=x=0:y=ih-48:w=6:h=48:color={ACCENT}:t=fill',
                text(f'{index:02d}', FONT_BOLD, 22, GOLD, 24, 'h-48+12'), text(title, FONT_BOLD, 22, 'white', 70, 'h-48+12'),
                text(caption, FONT, 20, MUTED, f'70+{len(title) * 12 + 40}', 'h-48+14')]

    def frame(self):
        """Places the screen in the output frame: the recorded window, scaled, below the band (vertical) or as is."""
        if not self.vertical:
            return [f'pad={self.w}:{self.h}:0:0:color={NAVY}']
        x, y, w, h = (self.window[k] for k in ('x', 'y', 'w', 'h'))
        height = round(h * 1080 / w / 2) * 2
        top = 260 + (1440 - height) // 2
        return [f'crop={w}:{h}:{x}:{y}', f'scale=1080:{height}:flags=lanczos', f'pad=1080:1920:0:{top}:color={NAVY}']

    def key(self, label, a, b):
        """A key badge for a shortcut, shown between output times a and b."""
        enable = f'between(t,{a:.2f},{b:.2f})'
        size = 48 if self.vertical else 34
        width = int(len(label) * size * 0.62) + 2 * size
        height = int(size * 1.9)
        x = (self.w - width) // 2
        y = 1700 if self.vertical else SCREEN_H - height - 40
        return [f"drawbox=x={x - 4}:y={y - 4}:w={width + 8}:h={height + 8}:color={ACCENT}:t=fill:enable='{enable}'",
                f"drawbox=x={x}:y={y}:w={width}:h={height}:color=white:t=fill:enable='{enable}'",
                text(label, FONT_BOLD, size, NAVY, f'{x}+({width}-text_w)/2', f'{y}+({height}-text_h)/2', enable)]


def card(layout, path, lines, seconds):
    fade = f'fade=t=in:st=0:d=0.4,fade=t=out:st={seconds - 0.4}:d=0.4'
    bar_y = lines[0][4] - 50
    draw = ','.join(text(t, f, s, c, '(w-text_w)/2', y) for t, f, s, c, y in lines)
    run(['-f', 'lavfi', '-i', f'gradients=s={layout.w}x{layout.h}:c0={NAVY}:c1=0x1E3A8A:x0=0:y0=0:x1={layout.w}:y1={layout.h}:d={seconds}:r={FPS}:speed=0.002',
         *SILENCE, '-t', str(seconds), '-vf', f'drawbox=x=(iw-140)/2:y={bar_y}:w=140:h=8:color={ACCENT}:t=fill,{draw},{fade}',
         *ENCODE, *AUDIO, '-shortest', str(path)])


def intro(layout, path):
    title, subtitle = INTRO
    if layout.vertical:
        lines = [('OpenSkiTime', FONT_BOLD, 110, 'white', 700), (title, FONT_LIGHT, 72, MUTED, 860),
                 ('Copy & paste from a spreadsheet', FONT, 40, GOLD, 1000), ('edit · undo · multi-edit · PDF', FONT, 40, GOLD, 1060)]
    else:
        lines = [('OpenSkiTime', FONT_BOLD, 100, 'white', 370), (title, FONT_LIGHT, 56, MUTED, 510), (subtitle, FONT, 32, GOLD, 600)]
    card(layout, path, lines, 3.5)


def outro(layout, path):
    if layout.vertical:
        lines = [('OpenSkiTime', FONT_BOLD, 110, 'white', 760), ('Free, open-source alpine', FONT_LIGHT, 52, MUTED, 910),
                 ('race timing', FONT_LIGHT, 52, MUTED, 980), ('openskiti.me', FONT_BOLD, 52, GOLD, 1090),
                 ('All athletes and clubs are synthetic.', FONT, 30, DIM, 1720)]
    else:
        lines = [('OpenSkiTime', FONT_BOLD, 96, 'white', 380), ('Free, open-source alpine race timing', FONT_LIGHT, 40, MUTED, 510),
                 ('openskiti.me', FONT_BOLD, 38, GOLD, 590), ('All athletes and clubs in this video are synthetic.', FONT, 24, DIM, 960)]
    card(layout, path, lines, 4)


def probe_start(path):
    out = subprocess.run(['ffprobe', '-v', 'error', '-show_entries', 'format=start_time', '-of', 'csv=p=0', str(path)],
                         check=True, capture_output=True, text=True).stdout
    return float(out.strip())


def segment(layout, raw, path, speed, chapter, index, keys):
    """chapter and keys are in seconds of the video (clock times already shifted to the first captured frame)."""
    a, b = max(chapter['start'], 0), chapter['end']
    seconds = (b - a) / speed
    tag = f'{speed:g}× faster' if speed > 1.3 else ''
    filters = [f'setpts=(PTS-STARTPTS)/{speed}', f'fps={FPS}', *layout.frame(), *layout.band(chapter['title'], chapter['caption'], index)]
    if tag and not layout.vertical:
        filters.append(text(tag, FONT_BOLD, 18, GOLD, 'w-text_w-24', 'h-48+15'))
    for k in keys:
        if a <= k['at'] < b:
            start = (k['at'] - a) / speed
            filters += layout.key(k['label'], start, min(start + KEY_SECONDS, seconds))
    filters += ['fade=t=in:st=0:d=0.25', f'fade=t=out:st={max(seconds - 0.25, 0):.3f}:d=0.25']
    run(['-ss', f'{a:.3f}', '-to', f'{b:.3f}', '-i', str(raw), *SILENCE, '-vf', ','.join(filters),
         '-map', '0:v', '-map', '1:a', '-t', f'{seconds:.3f}', *ENCODE, *AUDIO, '-shortest', str(path)])
    return seconds


def showcase(layout, pages, path, index, seconds=7):
    """The generated entry lists: side by side (landscape) or the first two stacked with an overlap (vertical)."""
    if layout.vertical:
        pages = pages[:2]
    inputs = []
    for page in pages:
        inputs += ['-loop', '1', '-t', str(seconds), '-i', str(page)]
    chain = ['[0:v]format=yuv420p[bg]']
    last = 'bg'
    if layout.vertical:
        page_w = 760
        page_h = int(page_w * 1.414)
        spots = [(60, 300), (1080 - page_w - 60, 1920 - page_h - 230)]
    else:
        page_h = 760
        page_w = int(page_h / 1.414)
        gap = 60
        x0 = (layout.w - len(pages) * page_w - (len(pages) - 1) * gap) // 2
        spots = [(x0 + i * (page_w + gap), (layout.h - page_h) // 2 - 30) for i in range(len(pages))]
    for i, (x, y) in enumerate(spots[:len(pages)]):
        chain.append(f'[{i + 1}:v]scale={page_w}:{page_h},pad={page_w + 8}:{page_h + 8}:4:4:color=0x000000@0.35[p{i}]')
        chain.append(f'[{last}][p{i}]overlay=x={x - 4}:y={y - 4}[o{i}]')
        last = f'o{i}'
    band = layout.band('Entry lists as PDF', 'Series entries with one column per race, and entries by nation, on the organizer letterhead', index)
    chain.append(f"[{last}]zoompan=z='min(zoom+0.0002,1.025)':d={seconds * FPS}:s={layout.w}x{layout.h}:fps={FPS}:x='iw/2-(iw/zoom/2)':y='ih/2-(ih/zoom/2)',"
                 + ','.join(band) + f',fade=t=in:st=0:d=0.4,fade=t=out:st={seconds - 0.4}:d=0.4[v]')
    run(['-f', 'lavfi', '-i', f'gradients=s={layout.w}x{layout.h}:c0={NAVY}:c1=0x22356A:x0=0:y0=0:x1={layout.w}:y1={layout.h}:d={seconds}:r={FPS}',
         *inputs, *SILENCE, '-filter_complex', ';'.join(chain), '-map', '[v]', '-map', f'{len(pages) + 1}:a',
         '-t', str(seconds), *ENCODE, *AUDIO, '-shortest', str(path)])
    return seconds


def compose(layout, recording, output, tempo):
    chapters = json.loads((recording / 'chapters.json').read_text(encoding='utf-8'))
    keys = json.loads((recording / 'keys.json').read_text(encoding='utf-8'))
    # Clock time t is video time t - shift: the first frame was captured `shift` seconds after the clock started.
    shift = probe_start(recording / 'raw.mkv') - json.loads((recording / 'window.json').read_text(encoding='utf-8'))['clockStart']
    chapters = [dict(c, start=c['start'] - shift, end=c['end'] - shift) for c in chapters]
    keys = [dict(k, at=k['at'] - shift) for k in keys]
    # A fresh work folder per composition: nothing that already exists is deleted or overwritten.
    work = Path(tempfile.mkdtemp(prefix='compose-editing-', dir=recording))
    parts = [work / '00-intro.mp4']
    intro(layout, parts[0])
    total = 3.5
    for index, chapter in enumerate(chapters, 1):
        speed = float(chapter.get('speed', 1)) * tempo
        parts.append(work / f'{index:02d}.mp4')
        total += segment(layout, recording / 'raw.mkv', parts[-1], speed, chapter, index, keys)
    pages = sorted((recording / 'pdf-pages').glob('*.png')) if (recording / 'pdf-pages').exists() else []
    if pages:
        parts.append(work / '98-pdf.mp4')
        total += showcase(layout, pages[:3], parts[-1], len(chapters))
    parts.append(work / '99-outro.mp4')
    outro(layout, parts[-1])
    total += 4
    (work / 'list.txt').write_text(''.join(f"file '{p.name}'\n" for p in parts), encoding='utf-8')
    run(['-f', 'concat', '-safe', '0', '-i', str(work / 'list.txt'), '-c', 'copy', '-movflags', '+faststart', str(output)])
    return total


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--landscape', required=True)
    parser.add_argument('--portrait', required=True)
    parser.add_argument('--output', required=True)
    parser.add_argument('--name', default='OpenSkiTime-editing-competitors')
    parser.add_argument('--tempo', type=float, default=1.0)
    args = parser.parse_args()
    if shutil.which('ffmpeg') is None:
        raise SystemExit('ffmpeg is required (winget install Gyan.FFmpeg).')
    output = Path(args.output)
    output.mkdir(parents=True, exist_ok=True)
    for source, vertical, suffix in ((args.landscape, False, '16x9'), (args.portrait, True, '9x16')):
        recording = Path(source)
        window = json.loads((recording / 'window.json').read_text(encoding='utf-8')) if vertical else None
        target = output / f'{args.name}-{suffix}.mp4'
        seconds = compose(Layout(vertical, window), recording, target, args.tempo)
        print(f'Composed {target} ({seconds:.0f} s)')


if __name__ == '__main__':
    main()
