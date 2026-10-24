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

### Publishing through the API

`scripts/social/instagram.py` publishes image posts, carousels and reels through the
[Instagram API with Instagram Login](https://developers.facebook.com/docs/instagram-platform/content-publishing).
It is a maintainer tool for Windows (Python standard library, PowerShell 7, Azure CLI).

One-time setup:

1. The Instagram account must be a public Professional (Business) account.
2. In the [Meta App Dashboard](https://developers.facebook.com/apps), create an app with
   the use case *Manage messaging & content on Instagram* (no business portfolio needed)
   and choose *API setup with Instagram login*. Required permissions:
   `instagram_business_basic` and `instagram_business_content_publish`.
3. Under *App roles*, add @openskitime with the **Instagram Tester** role (the admin and
   developer roles need a Facebook account), and accept the invite at
   instagram.com → *Apps and websites* → *Tester invites*.
4. Under *Generate access tokens*, generate a token for @openskitime. The app stays in
   development mode; Standard Access covers accounts the app owner manages, so no App
   Review is needed.
5. Run `python scripts/social/instagram.py login` and paste the token into the masked
   window. It is checked against the API and saved in this Windows user's Credential
   Manager as `OpenSkiTime.Social.Instagram`. Never paste it into a terminal, chat or file.
6. Instagram downloads media from a public URL. Local files are uploaded to the private
   blob container `instagram-staging` in the storage account `openskitimesocial`
   (override with `OPENSKITIME_SOCIAL_STORAGE`), passed to Instagram as a two-hour
   read-only user-delegation SAS URL and deleted after publishing. The signed-in Azure
   user needs *Storage Blob Data Contributor* on the account.

Use:

```powershell
python scripts/social/instagram.py status            # account, 24 h publishing quota, token age
python scripts/social/instagram.py post shot.png --caption-file caption.txt --alt-text "..."
python scripts/social/instagram.py post a.png b.png --caption "..."   # carousel, 2–20 items
python scripts/social/instagram.py reel clip.mp4 --caption-file caption.txt --cover cover.png
python scripts/social/instagram.py refresh           # extend the token by 60 days
```

Publishing commands only print a preview unless `--yes` is given. Non-JPEG images are
converted to JPEG. A post is reported as published only after Instagram has processed
the media and returned its permalink. Refresh the token at least every 60 days;
`status` shows its age. Tests: `python -m unittest scripts/tests/test_instagram.py`.

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
