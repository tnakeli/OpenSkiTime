"""Create the synthetic organizer letterhead used as the PDF Factory background in the demonstration.

One A4 page: a header band with the (fictional) event identity and a footer band with timing and organizer lines.
PDF Factory places race data between them when its top/bottom margins are set to 40 mm / 24 mm.
Usage: python scripts/demo-video/make-letterhead.py <output.pdf>
"""
import sys
from pathlib import Path
from playwright.sync_api import sync_playwright

HTML = """<!doctype html><html><head><meta charset="utf-8"><style>
@page { size: A4; margin: 0 }
html, body { margin: 0; width: 210mm; height: 297mm; font-family: 'Segoe UI', Arial, sans-serif; }
.header { position: absolute; top: 0; left: 0; width: 210mm; height: 32mm; background: linear-gradient(115deg, #0b2430 0%, #123b4a 55%, #0e5f5a 100%); color: white; overflow: hidden; }
.header svg { position: absolute; right: 0; bottom: 0; width: 120mm; height: 32mm; }
.mark { position: absolute; left: 12mm; top: 8mm; width: 14mm; height: 14mm; border-radius: 2.5mm; background: #087E78; display: flex; align-items: center; justify-content: center; font-weight: 700; font-size: 9mm; }
.title { position: absolute; left: 31mm; top: 7.5mm; font-size: 6.2mm; font-weight: 700; letter-spacing: .2mm; }
.subtitle { position: absolute; left: 31mm; top: 16.5mm; font-size: 3.4mm; color: #b8d6d9; letter-spacing: .3mm; text-transform: uppercase; }
.stripe { position: absolute; top: 32mm; left: 0; width: 210mm; height: 1.4mm; background: linear-gradient(90deg, #087E78, #3FB3AC 60%, #F3C969); }
.footer { position: absolute; bottom: 0; left: 0; width: 210mm; height: 15mm; background: #0b2430; color: #cfe3e6; font-size: 2.9mm; }
.footer .left { position: absolute; left: 12mm; top: 4.2mm; }
.footer .right { position: absolute; right: 12mm; top: 4.2mm; text-align: right; }
.footer b { color: white; }
.footstripe { position: absolute; bottom: 15mm; left: 0; width: 210mm; height: .8mm; background: #087E78; }
</style></head><body>
<div class="header">
  <svg viewBox="0 0 400 110" preserveAspectRatio="none">
    <polygon points="0,110 70,48 115,78 175,20 235,70 280,38 340,82 400,44 400,110" fill="#ffffff" opacity=".10"/>
    <polygon points="40,110 120,62 170,90 230,40 300,92 360,60 400,80 400,110" fill="#ffffff" opacity=".14"/>
    <polyline points="175,20 188,32 200,27 213,40" stroke="#ffffff" stroke-opacity=".5" stroke-width="2" fill="none"/>
  </svg>
  <div class="mark">S</div>
  <div class="title">Synthetic Alpine Cup 2026</div>
  <div class="subtitle">Synthetic Fell &nbsp;·&nbsp; 12 December 2026 &nbsp;·&nbsp; FIS Women's Slalom</div>
</div>
<div class="stripe"></div>
<div class="footstripe"></div>
<div class="footer">
  <div class="left"><b>Organizer</b> Synthetic Ski Club &nbsp;·&nbsp; Synthetic Fell race office</div>
  <div class="right">Timing and results <b>OpenSkiTime</b> &nbsp;·&nbsp; synthetic demonstration data</div>
</div>
</body></html>"""


def main():
    output = Path(sys.argv[1]).resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    with sync_playwright() as playwright:
        browser = playwright.chromium.launch()
        page = browser.new_page()
        page.set_content(HTML)
        page.pdf(path=str(output), format='A4', print_background=True, margin={'top': '0', 'bottom': '0', 'left': '0', 'right': '0'})
        browser.close()
    print(output)


if __name__ == '__main__':
    main()
