"""Real-time race replay into a raw ALGE ASCII file, for training and rehearsals with the Replay file timing source.

Reads one CSV per run (rows in start order: Bib and/or Code, Status, Time, optional Inter1..InterN) and appends a
start impulse, the intermediates the racer reached and, for finishers, a finish impulse per racer to the output file at
the moment their device time of day is reached on this PC. Every line carries the bib explicitly (`0012*`), so the
impulses reach the right racers without operator queue handling. DNS has no impulse; DSQ starts and finishes like a
finisher (the decision is the operator's). Runs follow each other with a break after the last impulse of the previous
run. The console prints operator cues in real time: a DNF a few seconds after the racer's last timing point, and the
moment to prepare and open the next run, 10 s after the previous run's last impulse.

Racers are named by Bib, or by FIS Code together with the Run 1 start list exported from OpenSkiTime (--start-list):
then the app's drawn bibs apply to every run and its Run 1 order is used; later runs keep their CSV order.

In OpenSkiTime choose Replay file for primary timing with the output path (Start on the start channel, Finish on the
finish channel, intermediates on --inter-channels). Live capture follows the file, so connect timing before or during
the replay. Act on the cues: mark DNFs, and during the break create Run 2's start list and switch to Run 2.

Run times are whole hundredths. A finish lies 0-99 ten-thousandths after start + time, so every rule of truncation to
hundredths yields the given time. All times are integers in ten-thousandths of a second; no floating point.

Use synthetic or public data only, and keep personal race data and generated files out of Git.

Example:
  python scripts/race-replay.py --out C:/race/replay.txt --start-list C:/race/startlist-run1.tsv --run C:/race/run1.csv --run C:/race/run2.csv
"""
import argparse
import csv
import datetime
from pathlib import Path
import sys
import time

UNITS = 10_000  # ten-thousandths of a second
DAY = 24 * 3600 * UNITS


def parse_time(text):
    """'1:02.34', '62.34' or '52.40' as ten-thousandths; whole hundredths only."""
    text = text.strip()
    minutes, _, rest = text.rpartition(':')
    seconds, dot, hundredths = rest.partition('.')
    if not seconds.isdigit() or not dot or len(hundredths) != 2 or not hundredths.isdigit() or (minutes and not minutes.isdigit()):
        raise ValueError(f'Run time {text!r} is not m:ss.hh or ss.hh.')
    if minutes and int(seconds) >= 60:
        raise ValueError(f'Run time {text!r} has 60 or more seconds.')
    return ((int(minutes or 0) * 60 + int(seconds)) * 100 + int(hundredths)) * 100


def read_table(path, delimiter):
    with open(path, newline='', encoding='utf-8-sig') as file:
        for number, row in enumerate(csv.DictReader(file, delimiter=delimiter), start=2):
            yield number, {k.strip().lower(): (v or '').strip() for k, v in row.items() if k}


def read_run(path):
    """Rows (bib or None, FIS code or None, status, time, intermediates) in file order. A row names its racer by Bib,
    Code or both; optional Inter1..InterN columns hold intermediate times from the start (blank when not reached)."""
    rows = []
    for number, row in read_table(path, ','):
        try:
            bib = int(row['bib']) if row.get('bib') else None
            code = row.get('code') or None
            if bib is None and code is None:
                raise ValueError('Give the racer\'s Bib or Code.')
            if bib is not None and bib <= 0:
                raise ValueError(f'Bib {bib} is not positive.')
            status = row.get('status', '').upper() or 'FINISHED'
            if status not in ('FINISHED', 'DNF', 'DNS', 'DSQ'):
                raise ValueError(f'Status {status!r} is not Finished, DNF, DNS or DSQ.')
            run_time = parse_time(row.get('time', '')) if status in ('FINISHED', 'DSQ') else None
            names = sorted((k for k in row if k.startswith('inter')), key=lambda k: int(k[5:]))
            inters = [parse_time(row[k]) if row[k] and status != 'DNS' else None for k in names]
            if any(x is not None and (run_time is not None and x >= run_time or i and inters[i - 1] is not None and x <= inters[i - 1])
                   for i, x in enumerate(inters)):
                raise ValueError('Intermediate times must increase and stay below the run time.')
        except ValueError as error:
            raise SystemExit(f'{path} line {number}: {error}')
        if any(bib is not None and x[0] == bib or code is not None and x[1] == code for x in rows):
            raise SystemExit(f'{path} line {number}: the racer repeats.')
        rows.append((bib, code, status, run_time, inters))
    if not rows:
        raise SystemExit(f'{path} has no racers.')
    return rows


def read_start_list(path):
    """OpenSkiTime's start list TSV export: FIS code -> (position, bib)."""
    entries = {}
    for number, row in read_table(path, '\t'):
        try:
            entries[row['code']] = (int(row['position']), int(row['bib']))
        except (KeyError, ValueError):
            raise SystemExit(f'{path} line {number}: expected the Position, Bib and Code columns of a start list export.')
    return entries


def resolve(runs, start_list=None, first_run=1):
    """Each run as (bib, status, time) in start order. With the app's Run 1 start list, racers are matched by FIS code:
    its bibs apply to every run and its order to Run 1. Without it, the rows' own bibs and order are used."""
    if start_list is None:
        if any(bib is None for run in runs for bib, *_ in run):
            raise SystemExit('Every row needs a Bib, or give the Run 1 start list exported from OpenSkiTime (--start-list).')
        return [[(bib, status, run_time, inters) for bib, _, status, run_time, inters in run] for run in runs]
    resolved = []
    for number, run in enumerate(runs, start=first_run):
        unknown = [code or f'bib {bib}' for bib, code, *_ in run if code not in start_list]
        if unknown:
            raise SystemExit(f'Run {number}: not in the start list: {", ".join(unknown)}.')
        rows = [(start_list[code][1], status, run_time, inters, start_list[code][0]) for _, code, status, run_time, inters in run]
        if number == 1:
            missing = sorted(set(start_list) - {code for _, code, *_ in run})
            if missing:
                raise SystemExit(f'Run 1 has no row for start list codes: {", ".join(missing)}.')
            rows.sort(key=lambda x: x[4])
        resolved.append([row[:4] for row in rows])
    return resolved


DNF_CUE, NEXT_RUN_CUE = -1, -2


def schedule(runs, first_start, interval, pause, start_channel=0, finish_channel=1, first_run=1, inter_channels=()):
    """Impulses (device time, run, bib, channel) in time order. Times are ten-thousandths since midnight. Channel -1 is
    an operator cue to mark the racer DNF, -2 the cue to prepare and open the next run (bib 0)."""
    events = []
    start = first_start
    for run_number, rows in enumerate(runs, start=first_run):
        last = start
        for index, (bib, status, run_time, inters) in enumerate(rows):
            if status == 'DNS':
                continue
            # Starts land on hundredths spread over the second; finishes add a sub-hundredth part.
            at = start + index * interval + (bib * 7 % 100) * 100
            events.append((at, run_number, bib, start_channel))
            last = max(last, at)
            reached = at
            for number, inter in enumerate(inters):
                if inter is not None and number < len(inter_channels):
                    reached = at + inter + (bib * 53 + number * 7) % 100
                    events.append((reached, run_number, bib, inter_channels[number]))
                    last = max(last, reached)
            if status == 'DNF':
                # Out of the course a few seconds after the last timing point the racer passed.
                out = reached + (5 if reached > at else 8) * UNITS
                events.append((out, run_number, bib, DNF_CUE))
                last = max(last, out)
            if run_time is not None:
                finish = at + run_time + (bib * 37 + run_number * 11) % 100
                events.append((finish, run_number, bib, finish_channel))
                last = max(last, finish)
        # The next run starts on a whole second at least the break after this run's last impulse; 10 s after that
        # impulse the next run is prepared and opened.
        if run_number < first_run + len(runs) - 1:
            events.append((last + 10 * UNITS, run_number + 1, 0, NEXT_RUN_CUE))
        start = -(-(last + pause) // UNITS) * UNITS
    events.sort()
    if events and events[-1][0] >= DAY:
        raise SystemExit('The replay would pass midnight. Start earlier or shorten the interval.')
    return events


def time_of_day(at):
    seconds, fraction = divmod(at, UNITS)
    return f'{seconds // 3600:02d}:{seconds // 60 % 60:02d}:{seconds % 60:02d}.{fraction:04d}'


def line(at, bib, channel):
    return f' {bib:04d}* C{channel} {time_of_day(at)}\r'


def cue(bib, channel, run):
    return f'CUE mark bib {bib} DNF' if channel == DNF_CUE else f'CUE prepare and open Run {run}'


def clock(text):
    hours, minutes, seconds = (int(x) for x in text.split(':'))
    if not (0 <= hours < 24 and 0 <= minutes < 60 and 0 <= seconds < 60):
        raise argparse.ArgumentTypeError(f'{text!r} is not HH:MM:SS.')
    return (hours * 3600 + minutes * 60 + seconds) * UNITS


def now_units():
    now = datetime.datetime.now()
    return ((now.hour * 3600 + now.minute * 60 + now.second) * 1_000_000 + now.microsecond) // 100


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.split('\n\n')[0])
    parser.add_argument('--out', required=True, type=Path, help='raw ALGE ASCII file to append to (created if missing)')
    parser.add_argument('--run', required=True, action='append', type=Path,
                        help='CSV of one run (Bib and/or Code, Status, Time) in start order; repeat per run')
    parser.add_argument('--start-list', type=Path,
                        help="Run 1 start list TSV exported from OpenSkiTime: match racers by Code, use the app's bibs and Run 1 order")
    parser.add_argument('--first-run', type=int, default=1, help='number of the first --run (default 1; 2 to replay Run 2 alone)')
    parser.add_argument('--interval', type=int, default=30, help='seconds between starts (default 30)')
    parser.add_argument('--break', dest='pause', type=int, default=120, help='seconds between runs (default 120)')
    parser.add_argument('--first-start', type=clock, help='device time HH:MM:SS of the first start (default: now + lead)')
    parser.add_argument('--lead', type=int, default=15, help='seconds from now to the first start (default 15)')
    parser.add_argument('--start-channel', type=int, default=0)
    parser.add_argument('--finish-channel', type=int, default=1)
    parser.add_argument('--inter-channels', default='2,3', help='channels of Intermediate 1, 2, ... (default 2,3)')
    parser.add_argument('--dry-run', action='store_true', help='print the impulses without waiting or writing')
    args = parser.parse_args(argv)
    if args.interval <= 0 or args.pause < 0 or args.lead < 0 or args.first_run < 1:
        parser.error('interval and first run must be positive; break and lead must not be negative.')
    inter_channels = tuple(int(x) for x in args.inter_channels.split(',') if x.strip())
    channels = (args.start_channel, args.finish_channel, *inter_channels)
    if not all(0 <= x <= 8 for x in channels) or len(set(channels)) != len(channels):
        parser.error('start, finish and intermediate channels must be different channels 0-8.')
    runs = resolve([read_run(path) for path in args.run], read_start_list(args.start_list) if args.start_list else None, args.first_run)
    first = args.first_start if args.first_start is not None else -(-(now_units() + args.lead * UNITS) // UNITS) * UNITS
    events = schedule(runs, first, args.interval * UNITS, args.pause * UNITS, args.start_channel, args.finish_channel, args.first_run,
                      inter_channels)
    names = {args.start_channel: 'start', args.finish_channel: 'finish', **{c: f'inter{i + 1}' for i, c in enumerate(inter_channels)}}
    for number, rows in enumerate(runs, start=args.first_run):
        run_events = [x for x in events if x[1] == number and x[3] >= 0]
        count = lambda channel: sum(1 for x in run_events if x[3] == channel)
        span = f'{time_of_day(run_events[0][0])}-{time_of_day(run_events[-1][0])}' if run_events else 'no impulses'
        print(f'Run {number}: {len(rows)} racers, {count(args.start_channel)} starts, '
              + ', '.join(f'{count(c)} {names[c]}' for c in inter_channels)
              + f', {count(args.finish_channel)} finishes, {sum(1 for x in events if x[1] == number and x[3] == DNF_CUE)} DNF, {span}')
    if args.dry_run:
        for at, run, bib, channel in events:
            print(f'run {run} {line(at, bib, channel).strip()}' if channel >= 0 else f'run {run} {cue(bib, channel, run)} at {time_of_day(at)}')
        return 0
    with open(args.out, 'ab', buffering=0) as out:
        for at, run, bib, channel in events:
            wait = (at - now_units()) / UNITS  # pacing only; device times stay integers
            if wait > 0:
                time.sleep(wait)
            if channel < 0:
                print(f'run {run} {cue(bib, channel, run)} {time_of_day(at)}', flush=True)
                continue
            out.write(line(at, bib, channel).encode('ascii'))
            print(f'run {run} bib {bib:>3} {names[channel]:<6} {time_of_day(at)}', flush=True)
    print('Replay complete.')
    return 0


if __name__ == '__main__':
    try:
        sys.exit(main())
    except KeyboardInterrupt:
        print('Stopped.')
        sys.exit(130)
