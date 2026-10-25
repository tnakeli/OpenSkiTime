"""Compose the short Live Timing and race radio videos from a LiveDemoVideoTests recording.

Input (--recording directory), written by tests/OpenSkiTime.Desktop.E2E/LiveDemoVideoTests.cs and live-viewer.py:
raw.mkv (1920x1032 screen, frames stamped with Unix time), marks.json, audio.wav + viewer.json (loopback audio and the
Unix time of its first sample), phone/*.jpg + phone.json (the phone-sized page with frame times) and cut.json, the
edit chosen after watching: {"panel": [{"from", "to", "speed"}, ...], "live": [{"from", "to", "caption"}, ...]} in Unix seconds.
Output: <name>-16x9.mp4 (1920x1080, YouTube) and <name>-9x16.mp4 (1080x1920, Instagram Reels and YouTube Shorts), both
H.264/AAC at 30 fps with the radio audio of the live part normalized to -14 LUFS. Only ffmpeg is required.

Usage: python scripts/demo-video/compose-live.py --recording <dir> --output <dir> [--music <mp3 under the intro>]
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
NAVY, ACCENT, GOLD, MUTED = '0x14234A', '0x2563EB', '0xF3C969', '0xC3CEE6'
ENCODE = ['-c:v', 'libx264', '-preset', 'medium', '-crf', '19', '-pix_fmt', 'yuv420p', '-r', str(FPS)]
AUDIO = ['-c:a', 'aac', '-b:a', '192k', '-ar', '48000', '-ac', '2']
# The panel window's right column (race radio settings) in screen pixels, at the 9:16 frame's 1080:1680 aspect.
PANEL_CROP = (960, 0, 600, 933)


def escape(text):
    return text.replace('\\', '\\\\').replace(':', '\\:').replace("'", '’').replace('%', '\\%').replace(',', '\\,')


def run(args):
    subprocess.run(['ffmpeg', '-y', '-loglevel', 'error', *args], check=True)


def text(content, font, size, color, x, y):
    return f"drawtext=fontfile='{font}':text='{escape(content)}':fontsize={size}:fontcolor={color}:x={x}:y={y}"


def probe_start(path):
    out = subprocess.run(['ffprobe', '-v', 'error', '-show_entries', 'format=start_time', '-of', 'csv=p=0', str(path)],
                         check=True, capture_output=True, text=True).stdout
    return float(out.strip())


class Layout:
    def __init__(self, vertical):
        self.vertical = vertical
        self.w, self.h = (1080, 1920) if vertical else (1920, 1080)

    def band(self, title, caption, index):
        """Caption band: at the top of the vertical frame (240 px), as a lower third (48 px) in the landscape frame."""
        if self.vertical:
            lines = wrap(caption, 44)
            return ','.join([f'drawbox=x=0:y=0:w=iw:h=240:color={NAVY}:t=fill', f'drawbox=x=0:y=0:w=8:h=240:color={ACCENT}:t=fill',
                             text(f'{index:02d}', FONT_BOLD, 34, GOLD, 44, 40), text(title, FONT_BOLD, 46, 'white', 110, 32),
                             *[text(line, FONT, 32, MUTED, 44, 110 + i * 44) for i, line in enumerate(lines[:3])]])
        return ','.join([f'drawbox=x=0:y=ih-48:w=iw:h=48:color={NAVY}:t=fill', f'drawbox=x=0:y=ih-48:w=6:h=48:color={ACCENT}:t=fill',
                         text(f'{index:02d}', FONT_BOLD, 22, GOLD, 24, 'h-48+12'), text(title, FONT_BOLD, 22, 'white', 70, 'h-48+12'),
                         text(caption, FONT, 20, MUTED, f'70+{len(title) * 12 + 40}', 'h-48+14')])


def wrap(caption, width):
    lines, line = [], ''
    for word in caption.split():
        if line and len(line) + 1 + len(word) > width:
            lines.append(line)
            line = word
        else:
            line = f'{line} {word}'.strip()
    return lines + ([line] if line else [])


def card(layout, path, lines, seconds):
    fade = f'fade=t=in:st=0:d=0.4,fade=t=out:st={seconds - 0.4}:d=0.4'
    bar_y = lines[0][4] - 50
    draw = ','.join(text(t, f, s, c, '(w-text_w)/2', y) for t, f, s, c, y in lines)
    run(['-f', 'lavfi', '-i', f'gradients=s={layout.w}x{layout.h}:c0={NAVY}:c1=0x1E3A8A:x0=0:y0=0:x1={layout.w}:y1={layout.h}:d={seconds}:r={FPS}:speed=0.002',
         '-f', 'lavfi', '-i', 'anullsrc=r=48000:cl=stereo', '-t', str(seconds),
         '-vf', f'drawbox=x=(iw-140)/2:y={bar_y}:w=140:h=8:color={ACCENT}:t=fill,{draw},{fade}', *ENCODE, *AUDIO, '-shortest', str(path)])


def intro(layout, path):
    if layout.vertical:
        lines = [('OpenSkiTime', FONT_BOLD, 120, 'white', 720), ('Live Timing', FONT_LIGHT, 72, MUTED, 880),
                 ('with AI race radio', FONT_LIGHT, 72, MUTED, 970), ('Results in any browser,', FONT, 40, GOLD, 1110),
                 ('commentary in Finnish', FONT, 40, GOLD, 1165)]
    else:
        lines = [('OpenSkiTime Live Timing', FONT_BOLD, 100, 'white', 380), ('with AI race radio', FONT_LIGHT, 52, MUTED, 520),
                 ('Live results in any browser, spoken commentary in Finnish', FONT, 32, GOLD, 610)]
    card(layout, path, lines, 3.5)


def outro(layout, path):
    if layout.vertical:
        lines = [('OpenSkiTime', FONT_BOLD, 110, 'white', 760), ('Free, open-source alpine', FONT_LIGHT, 52, MUTED, 910),
                 ('race timing', FONT_LIGHT, 52, MUTED, 980), ('openskiti.me', FONT_BOLD, 52, GOLD, 1090),
                 ('Synthetic athletes, simulated timing.', FONT, 30, '0x8796BC', 1700), ('Radio is AI-generated.', FONT, 30, '0x8796BC', 1745)]
    else:
        lines = [('OpenSkiTime', FONT_BOLD, 96, 'white', 380), ('Free, open-source alpine race timing', FONT_LIGHT, 40, MUTED, 510),
                 ('openskiti.me', FONT_BOLD, 38, GOLD, 590),
                 ('Synthetic athletes and simulated timing. The radio commentary is AI-generated.', FONT, 24, '0x8796BC', 960)]
    card(layout, path, lines, 4)


def panel_part(layout, recording, start, path, a, b, speed, index):
    """A stretch of the screen recording with the control panel (no radio audio yet), optionally faster."""
    seconds = (b - a) / speed
    if layout.vertical:
        x, y, w, h = PANEL_CROP
        video = f'crop={w}:{h}:{x}:{y},scale=1080:1680:flags=lanczos,pad=1080:1920:0:240:color={NAVY}'
    else:
        video = f'pad=1920:1080:0:0:color={NAVY}'
    vf = ','.join([f'setpts=(PTS-STARTPTS)/{speed}', f'fps={FPS}', video,
                   layout.band('Live timing control panel', 'The race radio uses the operator’s own provider keys, voices, event background and budget', index),
                   'fade=t=in:st=0:d=0.25', f'fade=t=out:st={seconds - 0.25:.3f}:d=0.25'])
    run(['-ss', f'{a - start:.3f}', '-t', f'{b - a:.3f}', '-i', str(recording / 'raw.mkv'),
         '-f', 'lavfi', '-i', 'anullsrc=r=48000:cl=stereo', '-vf', vf, '-map', '0:v', '-map', '1:a', '-t', f'{seconds:.3f}',
         *ENCODE, *AUDIO, str(path)])
    return seconds


def phone_frames(recording, work, a, b, name):
    """The phone screencast between two Unix times as a concat list (each frame shown until the next one)."""
    # ffmpeg resolves concat entries against the list's own folder, so the entries are absolute.
    recording = recording.resolve()
    frames = json.loads((recording / 'phone.json').read_text(encoding='utf-8'))
    before = [f for f in frames if f['at'] <= a]
    chosen = ([before[-1]] if before else []) + [f for f in frames if a < f['at'] < b]
    lines = []
    for i, f in enumerate(chosen):
        begin = max(f['at'], a)
        end = chosen[i + 1]['at'] if i + 1 < len(chosen) else b
        lines.append(f"file '{(recording / 'phone' / f['file']).as_posix()}'\nduration {end - begin:.4f}\n")
    lines.append(f"file '{(recording / 'phone' / chosen[-1]['file']).as_posix()}'\n")
    path = work / f'{name}.txt'
    path.write_text(''.join(lines), encoding='utf-8')
    return path


def live_part(layout, recording, start, audio_start, work, path, cut, index):
    a, b = cut['from'], cut['to']
    seconds = b - a
    title = cut.get('title', 'Live page with the race radio')
    fades = f'fade=t=in:st=0:d=0.3,fade=t=out:st={seconds - 0.3:.3f}:d=0.3'
    afades = f'afade=t=in:st=0:d=0.3,afade=t=out:st={seconds - 0.4:.3f}:d=0.4'
    if layout.vertical:
        listing = phone_frames(recording, work, a, b, path.stem)
        source = ['-f', 'concat', '-safe', '0', '-i', str(listing)]
        video = f'fps={FPS},scale=1080:1680,pad=1080:1920:0:240:color={NAVY}'
    else:
        source = ['-ss', f'{a - start:.3f}', '-t', f'{seconds:.3f}', '-i', str(recording / 'raw.mkv')]
        video = f'setpts=PTS-STARTPTS,fps={FPS},pad=1920:1080:0:0:color={NAVY}'
    run([*source, '-ss', f'{a - audio_start:.3f}', '-t', f'{seconds:.3f}', '-i', str(recording / 'audio.wav'),
         '-filter_complex', f'[0:v]{video},{layout.band(title, cut["caption"], index)},{fades}[v];[1:a]{afades}[a]',
         '-map', '[v]', '-map', '[a]', '-t', f'{seconds:.3f}', *ENCODE, *AUDIO, str(path)])
    return seconds


def compose(layout, recording, cut, output, music):
    start = probe_start(recording / 'raw.mkv')
    audio_start = json.loads((recording / 'viewer.json').read_text(encoding='utf-8'))['audioStart']
    work = Path(tempfile.mkdtemp(prefix='compose-live-', dir=recording))
    parts = [work / '00-intro.mp4']
    intro(layout, parts[0])
    index = 1
    lead = 3.5
    for i, panel in enumerate(cut['panel']):
        parts.append(work / f'10-panel-{i}.mp4')
        lead += panel_part(layout, recording, start, parts[-1], panel['from'], panel['to'], panel.get('speed', 1), index)
    for i, live in enumerate(cut['live']):
        index += 1
        parts.append(work / f'20-live-{i}.mp4')
        live_part(layout, recording, start, audio_start, work, parts[-1], live, index)
    parts.append(work / '99-outro.mp4')
    outro(layout, parts[-1])
    (work / 'list.txt').write_text(''.join(f"file '{p.name}'\n" for p in parts), encoding='utf-8')
    joined = work / 'joined.mp4'
    run(['-f', 'concat', '-safe', '0', '-i', str(work / 'list.txt'), '-c', 'copy', str(joined)])
    # Loudness for social platforms; a quiet music bed under the intro and the panel, fading out before the live page.
    if music:
        graph = (f'[1:a]volume=0.22,atrim=0:{lead + 0.5:.2f},afade=t=out:st={lead - 1.2:.2f}:d=1.5[m];'
                 f'[0:a][m]amix=inputs=2:duration=first:normalize=0,loudnorm=I=-14:TP=-1.5:LRA=11[a]')
        run(['-i', str(joined), '-i', str(music), '-filter_complex', graph, '-map', '0:v', '-map', '[a]', '-c:v', 'copy', *AUDIO,
             '-movflags', '+faststart', str(output)])
    else:
        run(['-i', str(joined), '-af', 'loudnorm=I=-14:TP=-1.5:LRA=11', '-c:v', 'copy', *AUDIO, '-movflags', '+faststart', str(output)])


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--recording', required=True)
    parser.add_argument('--output', required=True)
    parser.add_argument('--name', default='OpenSkiTime-live-radio')
    parser.add_argument('--music')
    args = parser.parse_args()
    if shutil.which('ffmpeg') is None:
        raise SystemExit('ffmpeg is required (winget install Gyan.FFmpeg).')
    recording = Path(args.recording)
    cut = json.loads((recording / 'cut.json').read_text(encoding='utf-8'))
    output = Path(args.output)
    output.mkdir(parents=True, exist_ok=True)
    for vertical, suffix in ((False, '16x9'), (True, '9x16')):
        target = output / f'{args.name}-{suffix}.mp4'
        compose(Layout(vertical), recording, cut, target, args.music)
        print('Composed', target)


if __name__ == '__main__':
    main()
