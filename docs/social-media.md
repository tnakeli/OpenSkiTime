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
python scripts/social/instagram.py story clip.mp4                  # one image or video (max. 60 s), 24 hours, no caption
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

### Managing the channel through the API

`scripts/social/youtube.py` uploads videos, changes video metadata and thumbnails, and
sets the channel description, keywords and banner through the
[YouTube Data API v3](https://developers.google.com/youtube/v3). It is a maintainer tool for
Windows using only the Python standard library. The channel picture cannot be set through
the API.

One-time setup, in the Google account that owns the OpenSkiTime Brand Account:

1. In the [Google Cloud console](https://console.cloud.google.com), create the project
   *OpenSkiTime Publisher* and enable *YouTube Data API v3*.
2. Under *Google Auth Platform*, configure the consent screen with audience *External*,
   then *Publish app* (status *In production*). While an app is in *Testing*, Google
   expires its refresh tokens after seven days. The app is not verified; the sign-in shows
   a warning that the owner passes with *Advanced → Go to OpenSkiTime Publisher*.
3. Under *Clients*, create an OAuth client of type *Desktop app* and download its JSON.
4. Run `python scripts/social/youtube.py client-import <file>`; the client is saved in
   Credential Manager as `OpenSkiTime.Social.YouTube.Client`. Delete the downloaded file.
5. Run `python scripts/social/youtube.py login` and choose the **OpenSkiTime** channel in
   the account chooser (Google lists it after the account as a brand account). The tool
   also prints the sign-in address in case no browser opens; open it on the same computer.
   The browser returns to a one-shot `127.0.0.1` listener (PKCE, scope `youtube`); the
   refresh token is saved as `OpenSkiTime.Social.YouTube`. Only `@OpenSkiTime` is accepted:
   a sign-in to another channel, such as a personal one on the same Google account, is
   revoked and not saved, and a saved sign-in for another channel is refused.
6. Custom thumbnails require a channel verified at https://www.youtube.com/verify.

Use:

```powershell
python scripts/social/youtube.py status
python scripts/social/youtube.py channel --description-file description.txt --keywords 'FIS "alpine skiing"' --banner assets/social/youtube-banner.png
python scripts/social/youtube.py upload demo.mp4 --title "..." --description-file description.txt --tags "slalom,FIS timing" --thumbnail thumb.png
python scripts/social/youtube.py update VIDEO_ID --privacy public
python scripts/social/youtube.py thumbnail VIDEO_ID thumb.png
```

Every command that changes the channel only prints a preview unless `--yes` is given.
Uploads are private unless `--privacy` says otherwise, are not made for kids, and use the
Sports category by default (`--category tech` for Science & Technology). Updates start
from the video's current values, because the API replaces whole parts. Uploads use
resumable 16 MiB chunks; an upload is reported only when YouTube returns the video. After
a failed chunk the tool asks the upload session how much YouTube received before resending,
so a lost response does not create a duplicate. If the outcome stays unknown it says so:
check YouTube Studio before uploading again.
Projects get 100 uploads a day. Tests: `python -m unittest scripts/tests/test_youtube.py`.

## Content rules

Show only synthetic or anonymized race data, as in the repository screenshots.
Do not post real athletes' names, FIS lists, start lists or results without the
organizer's permission.
