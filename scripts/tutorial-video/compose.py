"""Compose a narrated tutorial from a TutorialRecorder recording (docs/tutorial-videos.md).

Reads <language folder>/timing.json and the spoken steps (narrate.py) and <recording>/raw.mkv, recording.json and
pointer.json (tests/OpenSkiTime.Desktop.E2E/TutorialRecorder.cs). Every frame of raw.mkv carries its Unix time, so the
speech of a step starts exactly where the driver started the step. Waits for the application are shortened, names of
people are covered, the camera zooms where the driver asked for it, and a large pointer with click rings is drawn from
the recorded track. No text is drawn over the recording; subtitles go to an .srt file for YouTube. A title card and a
closing card with the links frame the video, with music that is quiet under the speech.

Needs ffmpeg and numpy.

Usage: python scripts/tutorial-video/compose.py fis-event --language fi --recording <dir> --music <file.mp3> --output <dir>
"""
import argparse
import collections
from concurrent.futures import ThreadPoolExecutor
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile

import numpy as np

HERE = Path(__file__).resolve().parent
W, H, SCREEN_H, FPS, RATE = 1920, 1080, 1032, 30, 48000
NAVY, ACCENT, GOLD = (0x14, 0x23, 0x4A), (0x25, 0x63, 0xEB), (0xF3, 0xC9, 0x69)
FONT = 'C\\:/Windows/Fonts/segoeui.ttf'
FONT_BOLD = 'C\\:/Windows/Fonts/segoeuib.ttf'
FONT_LIGHT = 'C\\:/Windows/Fonts/segoeuil.ttf'
INTRO, OUTRO = 3.4, 7.0
STILL = 0.55               # a still picture (a wait for the application or a measured change) lasts at most this long
COVER_ROW = 46             # cells up to this height get a bar; larger areas are only filled
COVER_PAD = 0.3            # a cover also applies this long before its time
CAMERA_SECONDS = 0.8
MIN_VIEW = 1080            # narrowest camera view in screen pixels, which limits the zoom to 1.8
VOICE_LUFS, MUSIC_LUFS, OUTPUT_LUFS = -16, -22, -14
LINKS = ['openskiti.me', 'youtube.com/@OpenSkiTime', 'instagram.com/openskitime', 'github.com/tnakeli/OpenSkiTime']
TAGLINE = {'fi': 'Ilmainen avoimen lähdekoodin alppihiihdon ajanotto', 'en': 'Free, open-source alpine race timing'}


def run(args, **kwargs):
    return subprocess.run(['ffmpeg', '-y', '-hide_banner', '-loglevel', 'error', *args], check=True, **kwargs)


def ease(t):
    t = np.clip(t, 0, 1)
    return t * t * t * (t * (t * 6 - 15) + 10)


class Clock:
    """Maps recording time (Unix seconds) to output time.

    A step's speech starts at the step's start and every click on a cue word is an anchor: it is shown exactly when its
    word is spoken. Between two anchors the still pictures of the recording (freezes and waits for the application) are
    shortened first; the rest is stretched or pressed to fit. A step lasts at least as long as its speech.
    """

    def __init__(self, data, timing, lead=0.6, tail=1.2, gap=0.45):
        steps = {s['id']: s['start'] for s in data['steps']}
        order = [(t['id'], steps[t['id']], t['seconds']) for t in timing['steps']]
        # A wait in which the operator clicked is not a still picture.
        waits = [(w['from'], w['to']) for w in data['waits'] if not any(w['from'] < c['at'] < w['to'] for c in data['clicks'])]
        still = sorted([(f['from'], f['to']) for f in data['freezes']] + waits)
        merged = []
        for a, b in still:
            if merged and a <= merged[-1][1]:
                merged[-1][1] = max(merged[-1][1], b)
            else:
                merged.append([a, b])
        self.source, self.output = [order[0][1] - lead], [0.0]
        position = lead
        for n, (step, start, speech) in enumerate(order):
            end = order[n + 1][1] if n + 1 < len(order) else start + speech + tail
            cues = timing['steps'][n]['cues']
            anchors = [(start, 0.0)]
            for click in sorted((c for c in data['clicks'] + data.get('events', []) if c.get('cue') and c['step'] == step), key=lambda c: c['at']):
                if cues[click['cue']] > anchors[-1][1]:
                    anchors.append((click['at'], cues[click['cue']]))
            for (a, at), (b, bt) in zip(anchors, anchors[1:]):
                self._interval(merged, a, b, position + at, position + bt)
            a, at = anchors[-1]
            stills = sum(min(y - x, STILL) for x, y in self._within(merged, a, end))
            moving = (end - a) - sum(y - x for x, y in self._within(merged, a, end))
            length = max(speech + gap, at + stills + min(moving, 1.5))
            if n + 1 == len(order):
                length = speech + tail
            self._interval(merged, a, end, position + at, position + length)
            position += length
        self.source, self.output = np.array(self.source), np.array(self.output)
        self.length = float(self.output[-1])

    @staticmethod
    def _within(still, a, b):
        return [(max(x, a), min(y, b)) for x, y in still if min(y, b) > max(x, a)]

    def _interval(self, still, a, b, out_a, out_b):
        parts = self._within(still, a, b)
        moving = (b - a) - sum(y - x for x, y in parts)
        # Little time: the stills get at most half of it.
        fit = min(1.0, (out_b - out_a) * 0.5 / max(sum(min(y - x, STILL) for x, y in parts), 1e-9))
        short = [min(y - x, STILL) * fit for x, y in parts]
        rate = ((out_b - out_a) - sum(short)) / max(moving, 0.01)
        self._add(a, out_a)
        for (x, y), length in zip(parts, short):
            at = self.output[-1] + (x - self.source[-1]) * rate
            self._add(x, at)
            self._add(y, at + length)
        if self.source[-1] >= b:
            self.output[-1] = out_b
        self._add(b, out_b)

    def _add(self, source, output):
        if source > self.source[-1]:
            self.source.append(source)
            self.output.append(max(output, self.output[-1] + 1e-4))

    def to_output(self, t):
        return np.interp(t, self.source, self.output)

    def to_source(self, t):
        return np.interp(t, self.output, self.source)


def frames(raw):
    """Yields (Unix time, frame) of the recording in order."""
    probe = subprocess.run(['ffprobe', '-v', 'error', '-select_streams', 'v', '-show_entries', 'packet=pts_time', '-of', 'csv=p=0', str(raw)],
                           check=True, capture_output=True, text=True).stdout
    times = sorted(float(x.strip(',')) for x in probe.split() if x.strip(','))
    process = subprocess.Popen(['ffmpeg', '-loglevel', 'quiet', '-i', str(raw), '-fps_mode', 'passthrough', '-f', 'rawvideo', '-pix_fmt', 'rgb24', '-'],
                               stdout=subprocess.PIPE)
    size = W * SCREEN_H * 3
    for t in times:
        data = process.stdout.read(size)
        if len(data) < size:
            break
        yield t, np.frombuffer(data, np.uint8).reshape(SCREEN_H, W, 3)
    process.stdout.close()
    process.kill()
    process.wait()


def cover(image, areas):
    """Every cell becomes its own background colour with a bar of fixed shape: nothing of the text is left."""
    for x, y, w, h in areas:
        x0, y0, x1, y1 = max(x, 0), max(y, 0), min(x + w, W), min(y + h, SCREEN_H)
        if x1 <= x0 or y1 <= y0:
            continue
        part = image[y0:y1, x0:x1]
        background = np.median(part.reshape(-1, 3), axis=0)
        part[:] = background.astype(np.uint8)
        if h <= COVER_ROW:
            top, bottom = y0 + int((y1 - y0) * 0.3), y1 - int((y1 - y0) * 0.3)
            left, right = x0 + 8, x0 + 8 + int((x1 - x0 - 16) * 0.8)
            if bottom - top > 4 and right - left > 8:
                image[top:bottom, left:right] = (background * 0.80).astype(np.uint8)


def view(area):
    """The 16:9 camera rectangle of an area; None is the whole picture."""
    if area is None:
        return np.array([0, 0, W, H], float)
    x, y, w, h = area
    width = min(max(w, h * 16 / 9, MIN_VIEW), SCREEN_H * 16 / 9)
    height = width * 9 / 16
    return np.array([np.clip(x + w / 2 - width / 2, 0, W - width), np.clip(y + h / 2 - height / 2, 0, SCREEN_H - height), width, height])


class Camera:
    def __init__(self, moves):
        self.moves = [(-1e18, view(None))] + [(m['at'], view(m['area'])) for m in sorted(moves, key=lambda m: m['at'])]

    def at(self, t):
        """The move under way starts from where the previous one had come."""
        position = self.moves[0][1]
        for i in range(1, len(self.moves)):
            start, target = self.moves[i]
            if t < start:
                break
            until = min(t, self.moves[i + 1][0]) if i + 1 < len(self.moves) else t
            position = position + (target - position) * ease((until - start) / CAMERA_SECONDS)
        return position


def zoom(image, rect):
    x, y, w, h = rect
    if w >= W - 0.5:
        return image.copy()
    xs = np.clip(x + (np.arange(W) + 0.5) * w / W - 0.5, 0, W - 1.001)
    ys = np.clip(y + (np.arange(H) + 0.5) * h / H - 0.5, 0, H - 1.001)
    x0, y0 = xs.astype(int), ys.astype(int)
    fx = ((xs - x0) * 256).astype(np.uint32)[None, :, None]
    fy = ((ys - y0) * 256).astype(np.uint32)[:, None, None]
    top, left = y0.min(), x0.min()
    part = image[top:y0.max() + 2, left:x0.max() + 2].astype(np.uint32)
    x0, y0 = x0 - left, y0 - top
    rows = (part[:, x0] * (256 - fx) + part[:, x0 + 1] * fx)
    return ((rows[y0] * (256 - fy) + rows[y0 + 1] * fy) >> 16).astype(np.uint8)


def arrow():
    """The pointer as (colour, alpha) with its tip at pixel (6, 6)."""
    points = np.array([(0, 0), (0, 17), (4, 13.2), (6.6, 19.2), (9.2, 18.1), (6.6, 12.2), (12, 12)]) * 2.1
    n, s = 60, 4
    grid = (np.indices((n * s, n * s)).transpose(1, 2, 0)[..., ::-1] + 0.5) / s - 6
    inside = np.zeros((n * s, n * s), bool)
    for (ax, ay), (bx, by) in zip(points, np.roll(points, -1, axis=0)):
        crosses = (ay > grid[..., 1]) != (by > grid[..., 1])
        with np.errstate(divide='ignore', invalid='ignore'):
            inside ^= crosses & (grid[..., 0] < (bx - ax) * (grid[..., 1] - ay) / (by - ay) + ax)
    distance = np.full(inside.shape, 1e9)
    for (ax, ay), (bx, by) in zip(points, np.roll(points, -1, axis=0)):
        d = np.array([bx - ax, by - ay])
        k = np.clip(((grid - (ax, ay)) @ d) / (d @ d), 0, 1)[..., None]
        distance = np.minimum(distance, np.hypot(*np.moveaxis(grid - (np.array([ax, ay]) + k * d), -1, 0)))
    outline = inside | (distance < 2.2)
    def smooth(mask):
        return mask.reshape(n, s, n, s).mean(axis=(1, 3))
    white, alpha = smooth(inside & (distance > 0.6)), smooth(outline)
    colour = white[..., None] * 255 + (1 - white[..., None]) * np.array([12, 18, 38])
    return colour, alpha[..., None]


ARROW = arrow()


def blend(image, x, y, colour, alpha):
    h, w = alpha.shape[:2]
    x0, y0, x1, y1 = max(x, 0), max(y, 0), min(x + w, W), min(y + h, H)
    if x1 <= x0 or y1 <= y0:
        return
    a = alpha[y0 - y:y1 - y, x0 - x:x1 - x]
    c = colour[y0 - y:y1 - y, x0 - x:x1 - x] if np.ndim(colour) == 3 else np.array(colour)
    part = image[y0:y1, x0:x1]
    image[y0:y1, x0:x1] = (part * (1 - a) + c * a).astype(np.uint8)


def disc(radius, inner=0.0):
    size = int(radius) + 3
    yy, xx = np.mgrid[-size:size + 1, -size:size + 1]
    d = np.hypot(xx, yy)
    return (np.clip(radius - d + 0.5, 0, 1) * np.clip(d - inner + 0.5, 0, 1))[..., None], size


HALO, HALO_SIZE = disc(32)


def pointer(image, x, y, since_click):
    x, y = int(round(x)), int(round(y))
    blend(image, x - HALO_SIZE, y - HALO_SIZE, GOLD, HALO * 0.38)
    if 0 <= since_click < 0.5:
        k = since_click / 0.5
        ring, size = disc(16 + 34 * k, 9 + 34 * k)
        blend(image, x - size, y - size, ACCENT, ring * (0.95 * (1 - k)))
    blend(image, x - 6, y - 6, *ARROW)


def picture(recording, data, clock, output):
    camera = Camera(data['camera'])
    track = np.array(json.loads((recording / 'pointer.json').read_text(encoding='utf-8')))
    clicks = np.array(sorted(c['at'] for c in data['clicks']))
    covers = sorted(data['covers'], key=lambda c: c['at'])
    freezes = sorted(data['freezes'], key=lambda f: f['from'])
    starts = np.array([c['at'] for c in covers])

    def areas(t):
        """The covers that apply at t and those that start within the padding after it."""
        result, first = [], np.searchsorted(starts, t, side='right') - 1
        for i in range(max(first, 0), len(covers)):
            if covers[i]['at'] > t + COVER_PAD:
                break
            result += [tuple(a) for a in covers[i]['areas']]
        return set(result)

    encoder = subprocess.Popen(['ffmpeg', '-y', '-loglevel', 'error', '-f', 'rawvideo', '-pix_fmt', 'rgb24', '-s', f'{W}x{H}', '-r', str(FPS), '-i', '-',
                                '-vf', f'fade=t=in:d=0.4,fade=t=out:st={clock.length - 0.5:.3f}:d=0.5', '-c:v', 'libx264', '-preset', 'medium',
                                '-crf', '17', '-pix_fmt', 'yuv420p', str(output)], stdin=subprocess.PIPE)
    source = frames(recording / 'raw.mkv')
    time, current = next(source)
    ahead = next(source, None)
    base, key = None, None
    count = int(clock.length * FPS)

    def finish(base, rect, x, y, since_click):
        image = zoom(base, rect)
        pointer(image, x, y, since_click)
        return image.tobytes()

    # numpy releases the interpreter lock, so frames are finished in parallel and written in order.
    pending = collections.deque()
    with ThreadPoolExecutor(max(2, (os.cpu_count() or 4) - 2)) as pool:
        for n in range(count):
            t = float(clock.to_source(n / FPS))
            shown = next((f['from'] for f in freezes if f['from'] <= t < f['to']), t)
            while ahead is not None and ahead[0] <= shown:
                time, current = ahead
                ahead = next(source, None)
            # Covers of the shown picture's own time (a frozen picture keeps the covers it had).
            wanted = areas(shown)
            if key != (time, wanted):
                base = np.empty((H, W, 3), np.uint8)
                base[SCREEN_H:] = NAVY
                base[:SCREEN_H] = current
                cover(base, wanted)
                key = (time, wanted)
            rect = camera.at(t)
            px, py = np.interp(t, track[:, 0], track[:, 1]), np.interp(t, track[:, 0], track[:, 2])
            last = np.searchsorted(clicks, t, side='right') - 1
            pending.append(pool.submit(finish, base, rect, (px - rect[0]) * W / rect[2], (py - rect[1]) * H / rect[3], t - clicks[last] if last >= 0 else -1))
            if len(pending) > 48:
                encoder.stdin.write(pending.popleft().result())
            if n % 900 == 0:
                print(f'  {n / FPS:.0f} of {clock.length:.0f} s', flush=True)
        while pending:
            encoder.stdin.write(pending.popleft().result())
    encoder.stdin.close()
    if encoder.wait():
        raise SystemExit('Encoding the picture failed.')


def escape(value):
    return value.replace('\\', '\\\\').replace(':', '\\:').replace("'", '’').replace('%', '\\%').replace(',', '\\,')


def text(content, font, size, color, y):
    return f"drawtext=fontfile='{font}':text='{escape(content)}':fontsize={size}:fontcolor={color}:x=(w-text_w)/2:y={y}"


def card(path, lines, seconds):
    draw = ','.join(text(*line) for line in lines)
    bar = lines[0][4] - 50
    run(['-f', 'lavfi', '-i', f'gradients=s={W}x{H}:c0=0x14234A:c1=0x1E3A8A:x0=0:y0=0:x1={W}:y1={H}:d={seconds}:r={FPS}:speed=0.002',
         '-t', str(seconds), '-vf', f'drawbox=x=(iw-140)/2:y={bar}:w=140:h=8:color=0x2563EB:t=fill,{draw},fade=t=in:d=0.5,fade=t=out:st={seconds - 0.4}:d=0.4',
         '-c:v', 'libx264', '-preset', 'medium', '-crf', '17', '-pix_fmt', 'yuv420p', '-r', str(FPS), str(path)])


def samples(path):
    data = run(['-i', str(path), '-f', 'f32le', '-ac', '2', '-ar', str(RATE), '-'], capture_output=True).stdout
    return np.frombuffer(data, np.float32).reshape(-1, 2)


def write_wav(path, audio):
    run(['-f', 'f32le', '-ac', '2', '-ar', str(RATE), '-i', '-', '-c:a', 'pcm_f32le', str(path)], input=audio.astype(np.float32).tobytes())


def loudness(path):
    result = subprocess.run(['ffmpeg', '-hide_banner', '-i', str(path), '-vn', '-af', 'ebur128=peak=true:framelog=quiet', '-f', 'null', '-'],
                            capture_output=True, text=True, errors='replace').stderr
    return float(re.findall(r'I:\s+(-?[\d.]+) LUFS', result)[-1]), float(re.findall(r'Peak:\s+(-?[\d.]+) dBFS', result)[-1])


def sound(folder, timing, data, clock, music, total, work, output):
    voice = np.zeros((int(total * RATE), 2), np.float32)
    for step in timing['steps']:
        start = next(s['start'] for s in data['steps'] if s['id'] == step['id'])
        at = int((INTRO + float(clock.to_output(start))) * RATE)
        audio = samples(folder / step['audio'])[:len(voice) - at]
        voice[at:at + len(audio)] += audio
    write_wav(work / 'voice-raw.wav', voice)
    run(['-i', str(work / 'voice-raw.wav'), '-af', f'volume={VOICE_LUFS - loudness(work / "voice-raw.wav")[0]:.2f}dB,alimiter=limit=0.8:level=disabled:latency=1',
         '-c:a', 'pcm_f32le', str(work / 'voice.wav')])
    # The music loops with two-second crossfades; its level is constant and speech lowers it.
    piece, fade = samples(music), 2 * RATE
    ramp = np.linspace(0, 1, fade)[:, None]
    bed = piece.copy()
    while len(bed) < len(voice):
        bed = np.concatenate([bed[:-fade], bed[-fade:] * np.cos(ramp * np.pi / 2) + piece[:fade] * np.sin(ramp * np.pi / 2), piece[fade:]])
    bed = bed[:len(voice)] * 10 ** ((MUSIC_LUFS - loudness(music)[0]) / 20)
    bed[:RATE // 2] *= np.linspace(0, 1, RATE // 2)[:, None]
    bed[-3 * RATE:] *= np.linspace(1, 0, 3 * RATE)[:, None]
    write_wav(work / 'music.wav', bed)
    run(['-i', str(work / 'music.wav'), '-i', str(work / 'voice.wav'), '-filter_complex',
         '[0:a][1:a]sidechaincompress=threshold=0.008:ratio=2.5:attack=60:release=900:knee=4', '-c:a', 'pcm_f32le', str(work / 'bed.wav')])
    run(['-i', str(work / 'voice.wav'), '-i', str(work / 'bed.wav'), '-filter_complex', 'amix=inputs=2:normalize=0:duration=first', '-c:a', 'pcm_f32le', str(work / 'mix.wav')])
    run(['-i', str(work / 'mix.wav'), '-af', f'volume={OUTPUT_LUFS - loudness(work / "mix.wav")[0]:.2f}dB,alimiter=limit=0.76:level=disabled:latency=1',
         '-ar', str(RATE), '-c:a', 'pcm_s16le', str(output)])


def subtitles(timing, data, clock, path):
    def stamp(t):
        ms = round(t * 1000)
        return f'{ms // 3600000:02d}:{ms // 60000 % 60:02d}:{ms // 1000 % 60:02d},{ms % 1000:03d}'
    lines = []
    for step in timing['steps']:
        start = next(s['start'] for s in data['steps'] if s['id'] == step['id'])
        offset = INTRO + float(clock.to_output(start))
        lines += [(offset + c['start'], offset + c['end'], c['text']) for c in step['captions']]
    path.write_text('\n'.join(f'{i}\n{stamp(a)} --> {stamp(b)}\n{t}\n' for i, (a, b, t) in enumerate(lines, 1)), encoding='utf-8')


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('name')
    parser.add_argument('--language', required=True, choices=('fi', 'en'))
    parser.add_argument('--work', default='C:/OpenSkiTimeVideos')
    parser.add_argument('--recording', required=True)
    parser.add_argument('--music', required=True)
    parser.add_argument('--output', required=True)
    args = parser.parse_args()
    script = json.loads((HERE / 'tutorials' / f'{args.name}.json').read_text(encoding='utf-8'))
    folder, recording, output = Path(args.work) / args.name / args.language, Path(args.recording), Path(args.output)
    output.mkdir(parents=True, exist_ok=True)
    timing = json.loads((folder / 'timing.json').read_text(encoding='utf-8'))
    data = json.loads((recording / 'recording.json').read_text(encoding='utf-8'))
    clock = Clock(data, timing)
    work = Path(tempfile.mkdtemp(prefix='compose-', dir=recording))
    card(work / 'intro.mp4', [('OpenSkiTime', FONT_BOLD, 100, 'white', 400), (script['title'][args.language], FONT_LIGHT, 60, '0xC3CEE6', 545)], INTRO)
    card(work / 'outro.mp4', [('OpenSkiTime', FONT_BOLD, 96, 'white', 300), (TAGLINE[args.language], FONT_LIGHT, 40, '0xC3CEE6', 430),
                              *[(link, FONT_BOLD if i == 0 else FONT, 40, '0xF3C969' if i == 0 else 'white', 560 + 68 * i) for i, link in enumerate(LINKS)]], OUTRO)
    picture(recording, data, clock, work / 'main.mp4')
    (work / 'list.txt').write_text("file 'intro.mp4'\nfile 'main.mp4'\nfile 'outro.mp4'\n", encoding='utf-8')
    total = INTRO + clock.length + OUTRO
    sound(folder, timing, data, clock, Path(args.music), total, work, work / 'sound.wav')
    target = output / f'OpenSkiTime-{args.name}-{args.language}.mp4'
    run(['-f', 'concat', '-safe', '0', '-i', str(work / 'list.txt'), '-i', str(work / 'sound.wav'), '-map', '0:v', '-map', '1:a', '-c:v', 'copy',
         '-c:a', 'aac', '-b:a', '192k', '-t', f'{total:.3f}', '-movflags', '+faststart', str(target)])
    subtitles(timing, data, clock, target.with_suffix('.srt'))
    (output / f'{target.stem}.json').write_text(json.dumps({'recording': str(recording), 'seconds': round(total, 2), 'lufs': loudness(target)[0], 'peak': loudness(target)[1]}, indent=1), encoding='utf-8')
    print(f'{target} ({total:.0f} s)')


if __name__ == '__main__':
    sys.exit(main())
