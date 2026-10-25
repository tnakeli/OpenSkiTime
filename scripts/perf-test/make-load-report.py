"""Builds the printable load test report (HTML) from docs/perf-test/data and its screenshots.

Usage: python -I make_report.py <docs/perf-test folder> <output html>
"""
import base64
import csv
import html
import sys
from pathlib import Path

root = Path(sys.argv[1])
out = Path(sys.argv[2])
data = root / 'data'


def rows(name):
    with open(data / name, encoding='utf-8') as f:
        return list(csv.DictReader(f))


def num(v):
    return None if v in ('', None) else float(v)


def secs(t):
    p = [int(x) for x in t.split(':')]
    return p[0] * 3600 + p[1] * 60 + (p[2] if len(p) > 2 else 0)


INK, MUTED, GRID, C0, C1, C2 = '#1b2433', '#5d6878', '#e3e7ee', '#2563eb', '#d97706', '#16a34a'


def chart(points, series, title, note, y_label, bars=False, width=760, height=230, xmin=None, xmax=None, ymax=None, marks=()):
    m = dict(l=52, r=16, t=12, b=30)
    xs = [p['x'] for p in points]
    x0, x1 = xmin or min(xs), xmax or max(xs)
    top = ymax or max((p[s['key']] or 0) for p in points for s in series) * 1.1 or 1
    step = [1, 2, 2.5, 5, 10]
    mag = 10 ** (len(str(int(top))) - 1)
    tick = next(mag * s for s in step if top / (mag * s) <= 6)
    top = tick * -(-top // tick)
    X = lambda v: m['l'] + (v - x0) / (x1 - x0) * (width - m['l'] - m['r'])
    Y = lambda v: height - m['b'] - v / top * (height - m['t'] - m['b'])
    svg = [f'<svg viewBox="0 0 {width} {height}" width="100%" xmlns="http://www.w3.org/2000/svg" font-family="Segoe UI, Arial, sans-serif" font-size="11">']
    v = 0
    while v <= top + 1e-9:
        svg.append(f'<line x1="{m["l"]}" x2="{width - m["r"]}" y1="{Y(v):.1f}" y2="{Y(v):.1f}" stroke="{GRID}"/>')
        svg.append(f'<text x="{m["l"] - 6}" y="{Y(v) + 4:.1f}" text-anchor="end" fill="{MUTED}">{v:,.0f}</text>')
        v += tick
    t = x0 - x0 % 300 + 300
    while t < x1:
        svg.append(f'<text x="{X(t):.1f}" y="{height - 10}" text-anchor="middle" fill="{MUTED}">{t // 3600:02d}:{t % 3600 // 60:02d}</text>')
        t += 300
    for at, label in marks:
        svg.append(f'<line x1="{X(at):.1f}" x2="{X(at):.1f}" y1="{m["t"]}" y2="{height - m["b"]}" stroke="{MUTED}" stroke-dasharray="4 3"/>')
        svg.append(f'<text x="{X(at) - 4:.1f}" y="{m["t"] + 10}" text-anchor="end" fill="{MUTED}">{html.escape(label)}</text>')
    for s in series:
        pts = [p for p in points if p[s['key']] is not None]
        if bars:
            bw = max(2, (width - m['l'] - m['r']) / len(points) - 2)
            for p in pts:
                svg.append(f'<rect x="{X(p["x"]) - bw / 2:.1f}" y="{Y(p[s["key"]]):.1f}" width="{bw:.1f}" height="{Y(0) - Y(p[s["key"]]):.1f}" rx="1.5" fill="{s["color"]}"/>')
        else:
            d = ' '.join(f'{"M" if i == 0 else "L"}{X(p["x"]):.1f},{Y(p[s["key"]]):.1f}' for i, p in enumerate(pts))
            svg.append(f'<path d="{d}" fill="none" stroke="{s["color"]}" stroke-width="2" stroke-linejoin="round" stroke-linecap="round"/>')
    svg.append(f'<text x="12" y="{m["t"] + (height - m["t"] - m["b"]) / 2}" transform="rotate(-90 12 {m["t"] + (height - m["t"] - m["b"]) / 2})" text-anchor="middle" fill="{MUTED}">{html.escape(y_label)}</text>')
    svg.append('</svg>')
    legend = ''.join(f'<span><i style="background:{s["color"]}"></i>{html.escape(s["name"])}</span>' for s in series) if len(series) > 1 else ''
    return f'<figure><h3>{html.escape(title)}</h3><p class="note">{html.escape(note)}</p><div class="legend">{legend}</div>{"".join(svg)}</figure>'


head = rows('headline.csv')[0]
dist = {r['metric']: r for r in rows('distributions.csv')}
timeline = [{'x': secs(r['utc']), 'spread': num(r['spread_p95_ms']), 'delay': num(r['delivery_p50_ms']), 'mbit': num(r['mbit_s']), 'viewers': num(r['viewers'])} for r in rows('timeline.csv')]
load = [{'x': secs(r['utc']), 'cpu': num(r['cpu_pct_of_4_cores']) * 8, 'mem': num(r['working_set_mb']), 'viewers': num(r['viewers'])} for r in rows('server-load.csv')]
azure = [{'x': secs(r['utc']), 'cpu': (num(r['cpu_cores']) or 0) * 1000, 'out': num(r['network_out_mb'])} for r in rows('azure-metrics.csv')]
slow = rows('slowest-updates.csv')
restart = secs('10:44:57')
race = [p['cpu'] for p in load if secs('10:19:00') <= p['x'] <= secs('10:44:30')]


def shot(name):
    return 'data:image/png;base64,' + base64.b64encode((root / 'screenshots' / name).read_bytes()).decode()


def d(metric, key):
    return f'{float(dist[metric][key]):,.0f}'


tiles = [
    ('Simultaneous viewers', f'{int(head["viewers"]):,}', 'all connected at the first attempt'),
    ('Failed or dropped during the race', head['race_failures'], f'of {int(head["messages"]):,} messages'),
    ('Fan-out spread p95', d('Fan-out spread after first viewer', 'p95') + ' ms', 'after the first viewer of an update'),
    ('Desktop to viewer p50', d('Desktop publish to viewer', 'p50') + ' ms', "includes the desktop's 250 ms cycle"),
    ('Back after restart p95', f'{float(dist["Restart: drop to next state"]["p95"]) / 1000:.1f} s', 'all 900 viewers recovered'),
    ('Peak server CPU in race', f'{max(race):.0f} %', 'of the 0.5 vCPU allocation (5 s samples)'),
]
dist_rows = ''.join(
    f'<tr><td>{html.escape(r["metric"])}</td><td>{html.escape(r["group"])}</td><td>{int(float(r["n"])):,}</td><td>{float(r["p50"]):,.0f}</td><td>{float(r["p95"]):,.0f}</td><td>{float(r["max"]):,.0f}</td><td>{r["unit"]}</td></tr>'
    for r in rows('distributions.csv'))
slow_rows = ''.join(f'<tr><td>{r["version"]}</td><td>{r["viewers"]}</td><td>{float(r["slowest_after_first_ms"]):,.0f}</td><td>{r["size_kb"]}</td></tr>' for r in slow[:6])

page = f'''<!doctype html><html lang="en"><head><meta charset="utf-8"><title>Live timing load test — 25 October 2026</title>
<style>
@page {{ size: A4; margin: 14mm 14mm 16mm; }}
body {{ font-family: "Segoe UI", Arial, sans-serif; color: {INK}; font-size: 10.5pt; line-height: 1.45; margin: 0; }}
h1 {{ font-family: Georgia, "Times New Roman", serif; font-weight: 400; font-size: 24pt; margin: 0 0 4pt; }}
h2 {{ font-size: 13pt; margin: 16pt 0 6pt; border-bottom: 1px solid {GRID}; padding-bottom: 3pt; }}
h3 {{ font-size: 11pt; margin: 0 0 2pt; }}
p {{ margin: 0 0 6pt; }}
.lead {{ font-size: 12pt; }}
.meta, .note {{ color: {MUTED}; font-size: 9pt; }}
.tiles {{ display: grid; grid-template-columns: repeat(3, 1fr); gap: 8pt; margin: 12pt 0; }}
.tile {{ border: 1px solid {GRID}; border-radius: 6px; padding: 8pt 10pt; }}
.tile .label, .tile .sub {{ color: {MUTED}; font-size: 8.5pt; }}
.tile .big {{ font-family: Georgia, serif; font-size: 20pt; }}
figure {{ margin: 0 0 12pt; break-inside: avoid; }}
.legend {{ display: flex; gap: 14pt; font-size: 8.5pt; color: {MUTED}; margin: 2pt 0; }}
.legend i {{ display: inline-block; width: 12pt; height: 2pt; vertical-align: middle; margin-right: 4pt; }}
table {{ border-collapse: collapse; width: 100%; font-size: 9pt; font-variant-numeric: tabular-nums; break-inside: avoid; }}
th, td {{ border-bottom: 1px solid {GRID}; padding: 3pt 5pt; text-align: right; }}
th:first-child, td:first-child, th:nth-child(2), td:nth-child(2) {{ text-align: left; }}
th {{ color: {MUTED}; font-weight: 600; }}
ul {{ margin: 0 0 6pt; padding-left: 14pt; }} li {{ margin-bottom: 3pt; }}
.shots {{ display: grid; grid-template-columns: 1fr 1fr; gap: 10pt; break-inside: avoid; }}
.shots img {{ width: 100%; border: 1px solid {GRID}; }}
.break {{ break-before: page; }}
</style></head><body>
<h1>Live timing load test</h1>
<p class="meta">25 October 2026, 10:16–10:47 UTC · live.openskiti.me (Azure Container Apps, Sweden Central, 1 replica, 0.5 vCPU, 1 GiB) · OpenSkiTime desktop publishing through the live timing control panel · synthetic race data</p>
<p class="lead">One 0.5 vCPU server delivered every one of {int(head["updates"])} updates of a 100-racer slalom to {int(head["viewers"]):,} simultaneous viewers with no failed connection during the race, using a fraction of its CPU and memory. After a forced restart all viewers were back within 12 seconds and nobody saw an empty page.</p>
<div class="tiles">{''.join(f'<div class="tile"><div class="label">{a}</div><div class="big">{b}</div><div class="sub">{c}</div></div>' for a, b, c in tiles)}</div>

<h2>Setup</h2>
<ul>
<li><b>Publisher:</b> the real OpenSkiTime desktop application timed the 100-racer synthetic slalom in real time with the training simulator (a start every 15 s, run times 47–60 s) and published to Cloud through the live timing control panel (309 updates).</li>
<li><b>Viewers:</b> 900 simulated browsers in five Azure Container Instances (Sweden Central), 180 each from five different public addresses, so the production limit of 200 viewers per address applied unchanged. Each viewer loads the race page and its scripts and then follows the race over SignalR exactly like <code>live.js</code>: 15 s pings, a 35 s silence timeout and a jittered 0.5–15 s reconnect.</li>
<li><b>Server:</b> production configuration unchanged (1,000 connections, 200 per address, 3,000 requests per minute per address). A few real visitors opened the race from the public front page (up to 906 viewers).</li>
<li><b>Restart:</b> after the last finish the revision was restarted at 10:44:57 UTC to measure recovery.</li>
</ul>

<h2>Delivery</h2>
{chart(timeline, [dict(key='spread', name='Fan-out spread p95 (ms)', color=C0), dict(key='delay', name='Desktop to viewer p50 (ms)', color=C1)],
       'Delivery delay per 30 s', 'Fan-out spread: how much later than the first viewer the others received the same update. Desktop to viewer: from the publishing computer\'s stamp, corrected for its measured clock offset of +0.72 s. Both grow with the results table.', 'milliseconds', marks=[(restart, 'restart')])}
{chart(timeline, [dict(key='mbit', name='Mbit/s', color=C0)], 'Traffic to all viewers per 30 s', 'Every update is a full snapshot (40–66 kB) sent to every viewer, so traffic grows with viewers × snapshot size × updates.', 'Mbit/s', bars=True)}

<div class="break"></div>
<h2>Server</h2>
{chart(load, [dict(key='cpu', name='CPU % of 0.5 vCPU', color=C0)],
       'Server CPU (control panel Server load, every 5 s)', 'CPU converted to a share of the 0.5 vCPU allocation (the panel shows a share of the 4 cores .NET sees). The spike is 900 viewers reconnecting to the freshly started replica.', '% of 0.5 vCPU', marks=[(restart, 'restart')])}
{chart(load, [dict(key='mem', name='Working set MB', color=C1)],
       'Server memory (working set, every 5 s)', 'Grows with the results table and the viewer connections; the restart starts a fresh process.', 'MB', marks=[(restart, 'restart')])}
{chart([p for p in load if p['x'] >= secs('10:40:00')], [dict(key='viewers', name='Viewers', color=C2)], 'Viewers through the restart', 'Viewers counted by the server. The new replica started at 10:44:57; all old connections closed at 10:45:19 and were back within seconds. The drop at the end is the load generators stopping.', 'viewers', ymax=1000, marks=[(restart, 'restart')])}
{chart(azure, [dict(key='out', name='Network out MB per minute', color=C1)], 'Network out per minute (Azure Monitor)', 'Platform view of the same container: up to 825 MB a minute (110 Mbit/s on average over that minute), 15.0 GB in total. CPU peaked at 104 millicores a minute (21 % of 500).', 'MB per minute', bars=True)}

<h2>Measured distributions</h2>
<table><thead><tr><th>Measure</th><th>Area</th><th>Samples</th><th>p50</th><th>p95</th><th>Max</th><th>Unit</th></tr></thead><tbody>{dist_rows}</tbody></table>
<p class="note">Combined over all five load generators. A local rehearsal on one computer (no network) gave a desktop-to-viewer p50 of 270 ms, so most of that delay is the desktop's 250 ms live snapshot cycle.</p>
<h3>Slowest updates</h3>
<table><thead><tr><th>Version</th><th>Viewers reached</th><th>Slowest after first (ms)</th><th>Size (kB)</th></tr></thead><tbody>{slow_rows}</tbody></table>

<div class="break"></div>
<h2>What it means</h2>
<ul>
<li><b>900 viewers fit comfortably on one replica.</b> CPU averaged about 13 % and peaked at about 40 % of the allocation during the race; memory stayed under 300 MB of 1 GiB. The limits that bind first are settings: 1,000 connections in total and 200 per client address, which a shared venue Wi-Fi reaches first.</li>
<li><b>Fan-out time grows with viewers × snapshot size.</b> The load generators used only 25–54 of their 750 millicores and every generator's first viewer received an update within about 1 ms of the first viewer overall, so the 100–700 ms spread arises at the server and its ingress (about 48 MB of writes per update).</li>
<li><b>Bandwidth is the figure to watch for bigger events.</b> One viewer following a whole run used about 15 MB. Larger fields make every snapshot larger; sending only changed rows would cut traffic many times over.</li>
<li><b>Restarts are handled well.</b> The control panel restored the race on the new replica before the viewers returned, so no viewer received an empty state. One viewer connected to the draining replica was recovered by the 35 s silence timeout.</li>
<li><b>The panel's CPU figure</b> is a share of the 4 cores .NET sees, not of the 0.5 vCPU allocation: multiply by eight. Container Apps let the process burst to 0.72 cores while 900 viewers reconnected.</li>
</ul>
<h2>Limits of the test</h2>
<ul>
<li>One race session, no race radio, fields of 100 racers only.</li>
<li>Viewers were protocol-level clients in Azure, not browsers on mobile networks.</li>
<li>Desktop-to-viewer delay depends on the publishing computer's clock, measured against time.windows.com before and after (−0.703 s and −0.728 s).</li>
</ul>
<h2>Cost</h2>
<p>The test cost well under one euro (five Container Instances for about 75 minutes, a Basic container registry and a storage account for a few hours, all deleted afterwards; the live server itself stays within the Container Apps monthly free grant). In normal use the live server costs nothing between races (scale to zero); per race the main variable cost is outbound traffic beyond the first 100 GB a month.</p>

<h2>The operator's view</h2>
<div class="shots">
<figure><h3>900 viewers connected, before the first start</h3><img src="{shot('02-viewers-connected.png')}" alt="Control panel with 900 viewers"></figure>
<figure><h3>After the server restart</h3><img src="{shot('04-after-restart.png')}" alt="Control panel with 902 viewers after the restart"></figure>
</div>
</body></html>'''
out.write_text(page, encoding='utf-8')
print(out, len(page))
