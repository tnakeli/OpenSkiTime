# Social media profiles

OpenSkiTime has an Instagram account and a YouTube channel. Both are run by the
maintainer under their own logins: the YouTube channel is a Brand Account channel
and the Instagram account is a Professional (Business) account in the same
Accounts Center as the maintainer's personal account. Profile content is in English.

## Images

Sources and rendered images are in `assets/social/`. The colors come from the
application icon `assets/icons/openskitime.svg`.

| File | Size | Use |
| --- | --- | --- |
| `profile.png` | 800 × 800 | Instagram profile picture and YouTube channel picture. Platforms crop it to a circle; the mark stays inside it. |
| `youtube-banner.png` | 2560 × 1440 | YouTube channel banner. Text and mark stay inside the 1546 × 423 area that every device shows. |

The banner text uses Segoe UI, so render the PNGs on Windows. To re-render
after changing an SVG, open it in headless Edge at its native size:

```powershell
msedge --headless=new --hide-scrollbars --force-device-scale-factor=1 --window-size=2560,1440 --screenshot=youtube-banner.png file:///C:/path/to/page.html
```

where `page.html` contains only `<img src="youtube-banner.svg">` with zero body margin.

The profiles are linked from the website footer and structured data (the
`social` list in `website/build.mjs`), `website/llms.txt` and the README.

## Instagram

- **Account:** [@openskitime](https://www.instagram.com/openskitime/)
- **Name:** OpenSkiTime
- **Category:** Software
- **Bio** (max. 150 characters):

  > Free, open-source alpine ski race timing for Windows. Start lists, ALGE timing, FIS XML, PDF reports and live results. Works offline. ⏱️⛷️

- **Link:** https://openskiti.me

## YouTube

- **Channel:** [@OpenSkiTime](https://www.youtube.com/@OpenSkiTime)
- **Channel name:** OpenSkiTime
- **Description:**

  > OpenSkiTime is free, open-source race-office software for alpine skiing. It brings registration, start lists, timing and results into one workspace on Windows, and race operation works offline.
  >
  > On this channel: demos, how-to videos and release walkthroughs — from FIS entries and the draw to ALGE timing, FIS XML results, PDF reports and live results.
  >
  > All demos use fictional athletes and simulated timing.

- **Keywords:** alpine skiing, ski race timing, race timing software, FIS, slalom, giant slalom, ALGE, live results, open source
- **Links:** Website https://openskiti.me · GitHub https://github.com/tnakeli/OpenSkiTime

## Content rules

Show only synthetic or anonymized race data, as in the repository screenshots.
Do not post real athletes' names, FIS lists, start lists or results without the
organizer's permission.
