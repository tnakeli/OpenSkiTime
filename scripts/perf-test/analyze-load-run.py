"""Combines the load generators' raw samples and the panel's Server load log into one summary (summary.json).

Usage: python -I analyze.py <run folder with results/<label>/*.csv.gz and server-load.tsv> <publisher clock correction ms>
"""
import csv
import gzip
import io
import json
import math
import re
import sys
from collections import defaultdict
from datetime import datetime, timezone, timedelta
from pathlib import Path

root = Path(sys.argv[1])
clock_ms = float(sys.argv[2])  # added to (receive - publisher stamp): the publisher computer's clock runs ahead by this much
TICKS_MS = 10_000
EPOCH = datetime(1, 1, 1, tzinfo=timezone.utc)


def pct(values, p):
    if not values:
        return None
    s = sorted(values)
    return s[max(0, min(len(s) - 1, math.ceil(p / 100 * len(s)) - 1))]


def dist(values):
    return {'n': len(values), 'p50': pct(values, 50), 'p95': pct(values, 95), 'p99': pct(values, 99), 'max': max(values) if values else None}


def utc(ticks):
    return EPOCH + timedelta(microseconds=ticks // 10)


states, attempts = [], []
labels = sorted(p.name for p in (root / 'results').iterdir())
for label in labels:
    folder = root / 'results' / label
    with gzip.open(folder / 'states.csv.gz', 'rt') as f:
        for r in csv.DictReader(f):
            states.append((label, int(r['viewer']), int(r['version']), int(r['received_utc_ticks']), int(r['updated_utc_ticks']), int(r['bytes']), r['first'] == '1'))
    with gzip.open(folder / 'attempts.csv.gz', 'rt') as f:
        for r in csv.DictReader(f):
            attempts.append({'label': label, 'viewer': int(r['viewer']), 'started': int(r['started_utc_ticks']),
                             'first': int(r['first_state_utc_ticks']) if r['first_state_utc_ticks'] else None,
                             'ended': int(r['ended_utc_ticks']) if r['ended_utc_ticks'] else None, 'failure': r['failure'] or None})

broadcast = [s for s in states if not s[6] and s[2] >= 0]
first_of = {}
for s in broadcast:
    first_of[s[2]] = min(first_of.get(s[2], s[3]), s[3])
spread = [(s[3] - first_of[s[2]]) / TICKS_MS for s in broadcast]
per_version = defaultdict(list)
for s in broadcast:
    per_version[s[2]].append(s)
worst = sorted(((v, len(xs), max(x[3] for x in xs) - first_of[v], max(x[5] for x in xs)) for v, xs in per_version.items()), key=lambda x: -x[2])
e2e = [(s[3] - s[4]) / TICKS_MS + clock_ms for s in broadcast if abs(s[3] - s[4]) < 600 * 10_000_000]

# Drops: a watching connection that ended with a failure before the end of the run; the time to the next state on a
# new connection, and to the first state with results (a restarted server first answers with an empty state until the
# publisher restores the race).
results_by_viewer = defaultdict(list)
for s in states:
    if s[2] >= 0:
        results_by_viewer[(s[0], s[1])].append(s[3])
by_viewer = defaultdict(list)
for a in attempts:
    by_viewer[(a['label'], a['viewer'])].append(a)
drops = []
for key, xs in by_viewer.items():
    xs.sort(key=lambda a: a['started'])
    received = sorted(results_by_viewer[key])
    for i, a in enumerate(xs[:-1]):
        if a['first'] is None or a['ended'] is None:
            continue
        nxt = next((b for b in xs[i + 1:] if b['first'] is not None), None)
        later = next((r for r in received if r > a['ended']), None)
        drops.append({'at': a['ended'], 'reason': a['failure'], 'recovery_ms': (nxt['first'] - a['ended']) / TICKS_MS if nxt else None,
                      'results_ms': (later - a['ended']) / TICKS_MS if later else None})
failures = defaultdict(int)
for a in attempts:
    if a['failure']:
        failures[a['failure']] += 1
first_attempts = [xs[0] for xs in by_viewer.values()]

start = min(a['started'] for a in attempts)
end = max(s[3] for s in states)
buckets = []
step = 30 * 10_000_000
t = start - start % step
while t < end:
    inb = [s for s in states if t <= s[3] < t + step]
    sp = [(s[3] - first_of[s[2]]) / TICKS_MS for s in inb if not s[6] and s[2] >= 0]
    lat = [(s[3] - s[4]) / TICKS_MS + clock_ms for s in inb if not s[6] and s[2] >= 0 and abs(s[3] - s[4]) < 600 * 10_000_000]
    mid = t + step
    watching = sum(1 for a in attempts if a['first'] is not None and a['first'] < mid and (a['ended'] is None or a['ended'] >= mid))
    buckets.append({'utc': utc(t).strftime('%H:%M:%S'), 'watching': watching, 'messages': len(inb), 'mbit_s': sum(s[5] for s in inb) * 8 / 30 / 1e6,
                    'updates': len({s[2] for s in inb if not s[6] and s[2] >= 0}), 'spread_p95': pct(sp, 95), 'spread_max': max(sp) if sp else None,
                    'latency_p50': pct(lat, 50), 'latency_p95': pct(lat, 95),
                    'drops': sum(1 for d in drops if t <= d['at'] < t + step)})
    t += step

load = []
pattern = re.compile(r'Viewers (\d+) now.*?Response avg ([\d.]+) ms, p95 ([\d.]+) ms, max ([\d.]+) ms \((\d+) requests.*?(?:round trip (\d+) ms)?.*?CPU ([\d.]+) % of (\d+) cores · memory (\d+) MB \(heap (\d+) MB\) of (\d+) MB · queued work (\d+)')
for line in (root / 'server-load.tsv').read_text(encoding='utf-8').splitlines()[1:]:
    parts = line.split('\t')
    m = pattern.search(parts[3]) if len(parts) == 4 else None
    if m:
        load.append({'local': parts[0], 'status': parts[1], 'viewers': int(m[1]), 'resp_avg': float(m[2]), 'resp_p95': float(m[3]), 'resp_max': float(m[4]),
                     'requests': int(m[5]), 'round_trip': int(m[6]) if m[6] else None, 'cpu': float(m[7]), 'cores': int(m[8]),
                     'memory': int(m[9]), 'heap': int(m[10]), 'limit': int(m[11]), 'queued': int(m[12])})

summary = {
    'generators': labels,
    'start_utc': utc(start).isoformat(), 'end_utc': utc(end).isoformat(),
    'viewers': len(by_viewer), 'attempts': len(attempts),
    'first_attempt_to_state_ms': dist([(a['first'] - a['started']) / TICKS_MS for a in first_attempts if a['first']]),
    'first_attempt_failed': sum(1 for a in first_attempts if a['first'] is None),
    'failures': dict(failures),
    'updates': len(per_version), 'broadcast_messages': len(broadcast), 'initial_states': sum(1 for s in states if s[6]),
    'null_states': sum(1 for s in states if s[2] < 0),
    'megabytes': sum(s[5] for s in states) / 1e6,
    'message_kb': dist([s[5] / 1000 for s in broadcast]),
    'reached_per_update': dist([len(xs) for xs in per_version.values()]),
    'spread_ms': dist(spread),
    'worst_update_ms': dist([w[2] / TICKS_MS for w in worst]),
    'worst_updates': [{'version': w[0], 'reached': w[1], 'max_ms': w[2] / TICKS_MS, 'kb': w[3] / 1000} for w in worst[:10]],
    'publisher_to_viewer_ms': dist(e2e),
    'clock_correction_ms': clock_ms,
    'drops': len(drops), 'drop_reasons': dict(defaultdict(int, {r: sum(1 for d in drops if d['reason'] == r) for r in {d['reason'] for d in drops}})),
    'recovery_ms': dist([d['recovery_ms'] for d in drops if d['recovery_ms'] is not None]),
    'results_again_ms': dist([d['results_ms'] for d in drops if d['results_ms'] is not None]),
    'drops_at_utc': [utc(min(d['at'] for d in drops)).isoformat(), utc(max(d['at'] for d in drops)).isoformat()] if drops else None,
    'timeline': buckets, 'server_load': load,
}
(root / 'summary.json').write_text(json.dumps(summary, indent=1), encoding='utf-8')
print(json.dumps({k: v for k, v in summary.items() if k not in ('timeline', 'server_load')}, indent=1))
