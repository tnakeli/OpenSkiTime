"""Publish to the OpenSkiTime Instagram account through the Instagram API with Instagram Login.

Maintainer tool for Windows. The access token is kept in this Windows user's Credential Manager,
never in the repository, configuration or output. Instagram downloads media from a public URL,
so local files are staged in an Azure Storage container behind a short-lived read-only SAS URL
and deleted after publishing. Without --yes a publish command only prints what it would do.

    python scripts/social/instagram.py login
    python scripts/social/instagram.py status
    python scripts/social/instagram.py refresh
    python scripts/social/instagram.py post photo.png --caption-file caption.txt [--alt-text ...] [--yes]
    python scripts/social/instagram.py post a.png b.png c.png --caption "..." [--yes]   (carousel)
    python scripts/social/instagram.py reel clip.mp4 --caption-file caption.txt [--cover cover.png] [--yes]
"""
import argparse
from datetime import datetime, timedelta, timezone
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid

import credentials

API = 'https://graph.instagram.com/v26.0'
CREDENTIAL_TARGET = 'OpenSkiTime.Social.Instagram'
TOKEN_DAYS = 60
STORAGE_ACCOUNT = os.environ.get('OPENSKITIME_SOCIAL_STORAGE', 'openskitimesocial')
STORAGE_CONTAINER = 'instagram-staging'
CAPTION_LIMIT = 2200
HASHTAG_LIMIT = 30
CAROUSEL_LIMIT = 20
# Formats System.Drawing can decode for the JPEG conversion.
IMAGE_TYPES = {'.jpg', '.jpeg', '.png', '.bmp'}
VIDEO_TYPES = {'.mp4', '.mov'}


class InstagramError(Exception):
    pass


# --- Windows Credential Manager -------------------------------------------------------------

def save_credential(record):
    try:
        credentials.save(CREDENTIAL_TARGET, record['username'], record)
    except credentials.CredentialError as error:
        raise InstagramError(str(error)) from None


def load_credential():
    try:
        return credentials.load(CREDENTIAL_TARGET, 'Run: python scripts/social/instagram.py login')
    except credentials.CredentialError as error:
        raise InstagramError(str(error)) from None


# --- Graph API ------------------------------------------------------------------------------

def call(method, path, token, params=None, base=API):
    """Calls the API with the token in the body (POST) or query (GET); errors never include the token."""
    fields = dict(params or {}, access_token=token)
    encoded = urllib.parse.urlencode(fields)
    if method == 'GET':
        request = urllib.request.Request(f'{base}{path}?{encoded}')
    else:
        request = urllib.request.Request(f'{base}{path}', data=encoded.encode('ascii'), method=method)
    try:
        with urllib.request.urlopen(request, timeout=60) as response:
            return json.load(response)
    except urllib.error.HTTPError as error:
        raise InstagramError(f'{method} {path} failed: {describe_error(error.read())}') from None
    except urllib.error.URLError as error:
        raise InstagramError(f'{method} {path} failed: {error.reason}') from None


def describe_error(body):
    try:
        error = json.loads(body)['error']
    except (ValueError, KeyError, TypeError):
        return body[:300].decode('utf-8', 'replace') if isinstance(body, bytes) else str(body)[:300]
    parts = [error.get('message', 'unknown error')]
    if error.get('error_user_msg'):
        parts.append(error['error_user_msg'])
    parts.append(f"(code {error.get('code')}{', subcode ' + str(error['error_subcode']) if error.get('error_subcode') else ''})")
    return ' '.join(parts)


def token_record(token, now):
    profile = call('GET', '/me', token, {'fields': 'user_id,username'})
    return {'token': token, 'user_id': profile['user_id'], 'username': profile['username'], 'saved': now.isoformat()}


def token_age_days(record, now):
    return (now - datetime.fromisoformat(record['saved'])).total_seconds() / 86400


# --- Captions and media ---------------------------------------------------------------------

def check_caption(caption):
    """Returns the problems Instagram would reject or silently truncate."""
    problems = []
    if len(caption) > CAPTION_LIMIT:
        problems.append(f'caption has {len(caption)} characters, the limit is {CAPTION_LIMIT}')
    hashtags = sum(1 for word in caption.split() if word.startswith('#') and len(word) > 1)
    if hashtags > HASHTAG_LIMIT:
        problems.append(f'caption has {hashtags} hashtags, the limit is {HASHTAG_LIMIT}')
    return problems


def media_kind(path):
    suffix = Path(path).suffix.lower()
    if suffix in IMAGE_TYPES:
        return 'image'
    if suffix in VIDEO_TYPES:
        return 'video'
    raise InstagramError(f'{path}: unsupported file type {suffix or "(none)"}')


def to_jpeg(path, directory):
    """Instagram accepts only JPEG images; other formats are converted with System.Drawing."""
    if Path(path).suffix.lower() in {'.jpg', '.jpeg'}:
        return Path(path)
    target = Path(directory) / (Path(path).stem + '.jpg')
    script = ('$ErrorActionPreference="Stop";Add-Type -AssemblyName System.Drawing;'
              '$source=[System.Drawing.Image]::FromFile($env:OST_SOURCE);'
              '$canvas=New-Object System.Drawing.Bitmap $source.Width,$source.Height;'
              '$g=[System.Drawing.Graphics]::FromImage($canvas);$g.Clear([System.Drawing.Color]::White);'
              '$g.DrawImage($source,0,0,$source.Width,$source.Height);'
              '$codec=[System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders()|Where-Object MimeType -eq "image/jpeg";'
              '$quality=New-Object System.Drawing.Imaging.EncoderParameters 1;'
              '$quality.Param[0]=[System.Drawing.Imaging.EncoderParameter]::new([System.Drawing.Imaging.Encoder]::Quality,[long]92);'
              '$canvas.Save($env:OST_TARGET,$codec,$quality);$g.Dispose();$canvas.Dispose();$source.Dispose()')
    run(['pwsh', '-NoProfile', '-NonInteractive', '-Command', script],
        env=dict(os.environ, OST_SOURCE=str(Path(path).resolve()), OST_TARGET=str(target.resolve())))
    if not target.is_file() or target.stat().st_size == 0:
        raise InstagramError(f'{path}: conversion to JPEG produced no image')
    return target


def run(command, env=None):
    executable = shutil.which(command[0])
    if not executable:
        raise InstagramError(f'{command[0]} was not found on PATH.')
    result = subprocess.run([executable, *command[1:]], capture_output=True, text=True, env=env)
    if result.returncode != 0:
        raise InstagramError(f'{command[0]} {command[1] if len(command) > 1 else ""} failed: {result.stderr.strip()[-500:]}')
    return result.stdout.strip()


class Staging:
    """Uploads files to a private Azure blob container and hands Instagram short-lived read-only URLs."""

    def __init__(self, account=STORAGE_ACCOUNT, container=STORAGE_CONTAINER):
        self.account, self.container, self.blobs = account, container, []

    def _az(self, *arguments):
        return run(['az', 'storage', 'blob', *arguments, '--account-name', self.account,
                    '--container-name', self.container, '--auth-mode', 'login', '--only-show-errors'])

    def url(self, path):
        name = f'{uuid.uuid4().hex}{Path(path).suffix.lower()}'
        content_type = 'image/jpeg' if media_kind(path) == 'image' else 'video/mp4' if Path(path).suffix.lower() == '.mp4' else 'video/quicktime'
        self._az('upload', '--name', name, '--file', str(path), '--content-type', content_type, '--overwrite')
        self.blobs.append(name)
        expiry = (datetime.now(timezone.utc) + timedelta(hours=2)).strftime('%Y-%m-%dT%H:%MZ')
        return self._az('generate-sas', '--name', name, '--permissions', 'r', '--expiry', expiry,
                        '--as-user', '--full-uri', '--https-only', '-o', 'tsv')

    def __enter__(self):
        return self

    def __exit__(self, *exception):
        for name in self.blobs:
            try:
                self._az('delete', '--name', name)
            except InstagramError as error:
                print(f'Warning: staged file {name} was not deleted: {error}', file=sys.stderr)


# --- Publishing -----------------------------------------------------------------------------

def wait_until_ready(container_id, token, timeout_seconds, interval_seconds=10):
    deadline = time.monotonic() + timeout_seconds
    while True:
        status = call('GET', f'/{container_id}', token, {'fields': 'status_code,status'})
        code = status.get('status_code')
        if code == 'FINISHED':
            return
        if code in {'ERROR', 'EXPIRED'}:
            raise InstagramError(f'Instagram could not process the media: {code} {status.get("status", "")}'.strip())
        if time.monotonic() >= deadline:
            raise InstagramError(f'Media was still {code} after {timeout_seconds} s; nothing was published.')
        time.sleep(interval_seconds)


def publish(record, files, caption, alt_text=None, cover=None, story=False):
    """Creates the media containers, waits for processing and publishes; returns the post permalink."""
    token, user = record['token'], record['user_id']
    with tempfile.TemporaryDirectory(prefix='ost-instagram-') as directory, Staging() as staging:
        def item(path, **extra):
            if media_kind(path) == 'image':
                return {'image_url': staging.url(to_jpeg(path, directory)), **extra}
            return {'media_type': 'VIDEO' if extra.get('is_carousel_item') else 'REELS', 'video_url': staging.url(path), **extra}

        if story:
            # A story has no caption; its media type replaces the image or reel type.
            container = call('POST', f'/{user}/media', token, {**item(files[0]), 'media_type': 'STORIES'})['id']
            wait_until_ready(container, token, 600 if media_kind(files[0]) == 'video' else 300)
        elif len(files) == 1:
            params = item(files[0], caption=caption)
            if alt_text and media_kind(files[0]) == 'image':
                params['alt_text'] = alt_text
            if cover:
                params['cover_url'] = staging.url(to_jpeg(cover, directory))
            container = call('POST', f'/{user}/media', token, params)['id']
            wait_until_ready(container, token, 600 if media_kind(files[0]) == 'video' else 300)
        else:
            children = []
            for path in files:
                child = call('POST', f'/{user}/media', token, item(path, is_carousel_item='true'))['id']
                wait_until_ready(child, token, 600 if media_kind(path) == 'video' else 300)
                children.append(child)
            container = call('POST', f'/{user}/media', token, {'media_type': 'CAROUSEL', 'children': ','.join(children), 'caption': caption})['id']
            wait_until_ready(container, token, 300)
        media_id = call('POST', f'/{user}/media_publish', token, {'creation_id': container})['id']
    return call('GET', f'/{media_id}', token, {'fields': 'permalink'}).get('permalink', f'(media id {media_id})')


# --- Commands -------------------------------------------------------------------------------

def ask_token():
    """Reads the token from a masked window so it never appears in a terminal, history or transcript."""
    import tkinter as tk
    window = tk.Tk()
    window.title('OpenSkiTime – Instagram token')
    window.attributes('-topmost', True)
    tk.Label(window, text='Paste the access token from the Meta App Dashboard\n(Instagram › API setup with Instagram login › Generate token)',
             justify='left').pack(padx=16, pady=(16, 8), anchor='w')
    value = tk.StringVar()
    entry = tk.Entry(window, textvariable=value, show='•', width=60)
    entry.pack(padx=16, fill='x')
    entry.focus_set()
    result = {}

    def accept(_=None):
        result['token'] = value.get().strip()
        window.destroy()

    entry.bind('<Return>', accept)
    tk.Button(window, text='Save', command=accept, width=10).pack(padx=16, pady=16, anchor='e')
    window.mainloop()
    return result.get('token', '')


def command_login(_):
    token = ask_token()
    if not token:
        raise InstagramError('No token entered; nothing was saved.')
    record = token_record(token, datetime.now(timezone.utc))
    save_credential(record)
    print(f"Saved the token for @{record['username']} to Credential Manager ({CREDENTIAL_TARGET}).")


def command_status(_):
    record, now = load_credential(), datetime.now(timezone.utc)
    profile = call('GET', '/me', record['token'], {'fields': 'username,account_type,media_count,followers_count'})
    limit = call('GET', f"/{record['user_id']}/content_publishing_limit", record['token'], {'fields': 'quota_usage,config'})
    usage = (limit.get('data') or [{}])[0]
    age = token_age_days(record, now)
    print(f"@{profile['username']} ({profile.get('account_type', '?')}): {profile.get('media_count', '?')} posts, "
          f"{profile.get('followers_count', '?')} followers")
    print(f"Published through the API in the last 24 h: {usage.get('quota_usage', '?')} / "
          f"{usage.get('config', {}).get('quota_total', '?')}")
    print(f"Token saved {age:.0f} days ago; it expires about {TOKEN_DAYS - age:.0f} days from now unless refreshed.")


def command_refresh(_):
    record = load_credential()
    refreshed = call('GET', '/refresh_access_token', record['token'], {'grant_type': 'ig_refresh_token'},
                     base='https://graph.instagram.com')
    save_credential(token_record(refreshed['access_token'], datetime.now(timezone.utc)))
    print(f"Refreshed the token for @{record['username']}; valid for {refreshed.get('expires_in', 0) // 86400} days.")


def read_caption(args):
    if args.caption_file:
        return Path(args.caption_file).read_text(encoding='utf-8').strip()
    return (args.caption or '').strip()


def command_publish(args, files, cover=None):
    caption = read_caption(args)
    for path in files + ([cover] if cover else []):
        if not Path(path).is_file():
            raise InstagramError(f'{path}: file not found')
        media_kind(path)
    if len(files) > CAROUSEL_LIMIT:
        raise InstagramError(f'A carousel takes at most {CAROUSEL_LIMIT} items, got {len(files)}.')
    problems = check_caption(caption)
    if problems:
        raise InstagramError('; '.join(problems))
    record = load_credential()
    kind = 'reel' if args.command == 'reel' else 'carousel' if len(files) > 1 else 'image post'
    print(f"{kind} to @{record['username']}: {', '.join(str(path) for path in files)}"
          + (f' (cover {cover})' if cover else ''))
    print(f'Caption ({len(caption)} characters):\n{caption}\n')
    if not args.yes:
        print('Preview only; nothing was uploaded. Add --yes to publish.')
        return
    print(f'Published: {publish(record, files, caption, getattr(args, "alt_text", None), cover)}')


def command_story(args):
    path = args.files[0]
    if not Path(path).is_file():
        raise InstagramError(f'{path}: file not found')
    media_kind(path)
    record = load_credential()
    print(f"story to @{record['username']}: {path}")
    if not args.yes:
        print('Preview only; nothing was uploaded. Add --yes to publish.')
        return
    print(f'Published: {publish(record, [path], None, story=True)}')


def main(argv=None):
    parser = argparse.ArgumentParser(description='Publish to the OpenSkiTime Instagram account.')
    commands = parser.add_subparsers(dest='command', required=True)
    commands.add_parser('login', help='save an access token to Credential Manager')
    commands.add_parser('status', help='show the account, publishing quota and token age')
    commands.add_parser('refresh', help='extend the token by another 60 days')
    for name, help_text in [('post', 'publish one image (or video), or a carousel of 2-20 items'), ('reel', 'publish one video as a reel')]:
        command = commands.add_parser(name, help=help_text)
        command.add_argument('files', nargs='+' if name == 'post' else 1)
        caption = command.add_mutually_exclusive_group(required=True)
        caption.add_argument('--caption')
        caption.add_argument('--caption-file')
        command.add_argument('--yes', action='store_true', help='publish; without it only a preview is printed')
        if name == 'post':
            command.add_argument('--alt-text', help='accessibility text for a single image')
        else:
            command.add_argument('--cover', help='cover image for the reel')
    story = commands.add_parser('story', help='publish one image or video (at most 60 s) as a story for 24 hours')
    story.add_argument('files', nargs=1)
    story.add_argument('--yes', action='store_true', help='publish; without it only a preview is printed')
    args = parser.parse_args(argv)
    try:
        if args.command == 'login':
            command_login(args)
        elif args.command == 'status':
            command_status(args)
        elif args.command == 'refresh':
            command_refresh(args)
        elif args.command == 'story':
            command_story(args)
        elif args.command == 'reel':
            if media_kind(args.files[0]) != 'video':
                raise InstagramError('A reel needs a video file.')
            command_publish(args, args.files, args.cover)
        else:
            command_publish(args, args.files)
    except InstagramError as error:
        print(f'Error: {error}', file=sys.stderr)
        return 1
    return 0


if __name__ == '__main__':
    sys.exit(main())
