"""Manage the OpenSkiTime YouTube channel through the YouTube Data API v3.

Maintainer tool for Windows. The OAuth client and the channel's refresh token are kept in this
Windows user's Credential Manager, never in the repository, configuration or output. Commands
that change the channel only print what they would do unless --yes is given.

    python scripts/social/youtube.py client-import client_secret.json
    python scripts/social/youtube.py login
    python scripts/social/youtube.py status
    python scripts/social/youtube.py channel [--description-file F] [--keywords "a b"] [--banner banner.png] [--yes]
    python scripts/social/youtube.py upload video.mp4 --title T --description-file F [--tags a,b] [--privacy private] [--thumbnail t.png] [--yes]
    python scripts/social/youtube.py update VIDEO_ID [--title T] [--description-file F] [--tags a,b] [--privacy P] [--yes]
    python scripts/social/youtube.py thumbnail VIDEO_ID image.png [--yes]
"""
import argparse
import base64
from datetime import datetime, timezone
import hashlib
import http.client
import http.server
import json
import mimetypes
import os
from pathlib import Path
import secrets
import shlex
import sys
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
import webbrowser

import credentials

API = 'https://www.googleapis.com/youtube/v3'
UPLOAD_API = 'https://www.googleapis.com/upload/youtube/v3'
AUTHORIZE_URL = 'https://accounts.google.com/o/oauth2/v2/auth'
TOKEN_URL = 'https://oauth2.googleapis.com/token'
# One scope covers uploads, video and channel metadata, thumbnails and banners.
SCOPE = 'https://www.googleapis.com/auth/youtube'
CLIENT_TARGET = 'OpenSkiTime.Social.YouTube.Client'
TOKEN_TARGET = 'OpenSkiTime.Social.YouTube'
PRIVACY = ('private', 'unlisted', 'public')
# Categories that fit the channel: Sports and Science & Technology.
CATEGORIES = {'sports': '17', 'tech': '28'}
TITLE_LIMIT = 100
DESCRIPTION_BYTES = 5000
TAGS_LIMIT = 500
CHANNEL_DESCRIPTION_LIMIT = 1000
CHANNEL_KEYWORDS_LIMIT = 500
IMAGE_BYTES = {'banner': 6 * 1024 * 1024, 'thumbnail': 2 * 1024 * 1024}
UPLOAD_CHUNK = 16 * 1024 * 1024
UPLOAD_RETRIES = 3


class YouTubeError(Exception):
    pass


# --- Credentials and OAuth ------------------------------------------------------------------

def load(target, hint):
    try:
        return credentials.load(target, hint)
    except credentials.CredentialError as error:
        raise YouTubeError(str(error)) from None


def save(target, username, record):
    try:
        credentials.save(target, username, record)
    except credentials.CredentialError as error:
        raise YouTubeError(str(error)) from None


def load_client():
    return load(CLIENT_TARGET, 'Run: python scripts/social/youtube.py client-import <client_secret.json>')


def load_token():
    return load(TOKEN_TARGET, 'Run: python scripts/social/youtube.py login')


def pkce_pair():
    verifier = secrets.token_urlsafe(64)
    challenge = base64.urlsafe_b64encode(hashlib.sha256(verifier.encode('ascii')).digest()).rstrip(b'=').decode('ascii')
    return verifier, challenge


def post_form(url, fields):
    request = urllib.request.Request(url, data=urllib.parse.urlencode(fields).encode('ascii'), method='POST')
    try:
        with urllib.request.urlopen(request, timeout=60) as response:
            return json.load(response)
    except urllib.error.HTTPError as error:
        body = error.read()
        try:
            details = json.loads(body)
            message = f"{details.get('error')}: {details.get('error_description', '')}".strip(': ')
        except ValueError:
            message = body[:300].decode('utf-8', 'replace')
        if 'invalid_grant' in message:
            message += ' – sign in again: python scripts/social/youtube.py login'
        raise YouTubeError(f'Google sign-in failed: {message}') from None


def access_token():
    """Exchanges the saved refresh token for a short-lived access token kept only in memory."""
    client, token = load_client(), load_token()
    return post_form(TOKEN_URL, {'client_id': client['client_id'], 'client_secret': client['client_secret'],
                                 'refresh_token': token['refresh_token'], 'grant_type': 'refresh_token'})['access_token']


def receive_code(state):
    """Runs a one-shot loopback server and returns (redirect_uri, wait) for the authorization code."""
    result = {}
    done = threading.Event()

    class Handler(http.server.BaseHTTPRequestHandler):
        def do_GET(self):
            query = urllib.parse.parse_qs(urllib.parse.urlparse(self.path).query)
            if query.get('state', [''])[0] != state:
                self.send_response(400)
                self.end_headers()
                return
            result.update({key: values[0] for key, values in query.items()})
            self.send_response(200)
            self.send_header('Content-Type', 'text/html; charset=utf-8')
            self.end_headers()
            self.wfile.write('<!doctype html><title>OpenSkiTime</title><p>Sign-in finished. You can close this tab.</p>'.encode('utf-8'))
            done.set()

        def log_message(self, *_):
            pass

    server = http.server.HTTPServer(('127.0.0.1', 0), Handler)
    threading.Thread(target=server.serve_forever, daemon=True).start()

    def wait(timeout_seconds=300):
        try:
            if not done.wait(timeout_seconds):
                raise YouTubeError('Sign-in was not finished within five minutes.')
        finally:
            server.shutdown()
            server.server_close()
        if 'error' in result:
            raise YouTubeError(f"Sign-in was cancelled: {result['error']}")
        return result['code']

    return f'http://127.0.0.1:{server.server_port}', wait


# --- API ------------------------------------------------------------------------------------

def request(method, path, token, params=None, body=None, base=API, data=None, content_type=None, headers=None):
    """Calls the API; the access token travels only in the Authorization header."""
    url = f'{base}{path}' + (f'?{urllib.parse.urlencode(params)}' if params else '')
    all_headers = {'Authorization': f'Bearer {token}', **(headers or {})}
    if body is not None:
        data, content_type = json.dumps(body).encode('utf-8'), 'application/json; charset=utf-8'
    if content_type:
        all_headers['Content-Type'] = content_type
    req = urllib.request.Request(url, data=data, method=method, headers=all_headers)
    try:
        with urllib.request.urlopen(req, timeout=300) as response:
            payload = response.read()
            return (json.loads(payload) if payload else {}), response.headers
    except urllib.error.HTTPError as error:
        raise YouTubeError(f'{method} {path} failed ({error.code}): {describe_error(error.read())}') from None
    except urllib.error.URLError as error:
        raise YouTubeError(f'{method} {path} failed: {error.reason}') from None


def call(method, path, token, params=None, body=None, **options):
    return request(method, path, token, params, body, **options)[0]


def describe_error(body):
    try:
        error = json.loads(body)['error']
    except (ValueError, KeyError, TypeError):
        return body[:300].decode('utf-8', 'replace') if isinstance(body, bytes) else str(body)[:300]
    reasons = ', '.join(item.get('reason', '') for item in error.get('errors', []) if item.get('reason'))
    return error.get('message', 'unknown error') + (f' ({reasons})' if reasons else '')


def my_channel(token, parts='snippet,statistics,brandingSettings'):
    items = call('GET', '/channels', token, {'part': parts, 'mine': 'true'}).get('items', [])
    if not items:
        raise YouTubeError('The signed-in Google account has no YouTube channel.')
    return items[0]


# --- Validation and formatting --------------------------------------------------------------

def format_keywords(words):
    """Channel keywords are space-separated; multi-word keywords are quoted."""
    return ' '.join(f'"{word}"' if ' ' in word else word for word in words)


def parse_keywords(text):
    return shlex.split(text or '')


def check_video_metadata(title=None, description=None, tags=None):
    problems = []
    if title is not None:
        if not title.strip():
            problems.append('title is empty')
        if len(title) > TITLE_LIMIT:
            problems.append(f'title has {len(title)} characters, the limit is {TITLE_LIMIT}')
    for name, value in (('title', title), ('description', description)):
        if value and ('<' in value or '>' in value):
            problems.append(f'{name} contains < or >, which YouTube rejects')
    if description is not None and len(description.encode('utf-8')) > DESCRIPTION_BYTES:
        problems.append(f'description is {len(description.encode("utf-8"))} bytes, the limit is {DESCRIPTION_BYTES}')
    if tags:
        # YouTube counts quotes around tags that contain spaces.
        length = sum(len(tag) + (2 if ' ' in tag else 0) for tag in tags) + len(tags) - 1
        if length > TAGS_LIMIT:
            problems.append(f'tags take {length} characters, the limit is {TAGS_LIMIT}')
    return problems


def check_channel_metadata(description=None, keywords=None):
    problems = []
    if description is not None and len(description) > CHANNEL_DESCRIPTION_LIMIT:
        problems.append(f'channel description has {len(description)} characters, the limit is {CHANNEL_DESCRIPTION_LIMIT}')
    if keywords is not None and len(format_keywords(keywords)) > CHANNEL_KEYWORDS_LIMIT:
        problems.append(f'keywords take {len(format_keywords(keywords))} characters, the limit is {CHANNEL_KEYWORDS_LIMIT}')
    return problems


def check_image(path, kind):
    path = Path(path)
    if not path.is_file():
        return [f'{path}: file not found']
    if path.suffix.lower() not in {'.png', '.jpg', '.jpeg'}:
        return [f'{path}: {kind} must be PNG or JPEG']
    if path.stat().st_size > IMAGE_BYTES[kind]:
        return [f'{path}: {kind} is {path.stat().st_size} bytes, the limit is {IMAGE_BYTES[kind]}']
    return []


def merged_branding(current, description=None, keywords=None, banner_url=None):
    """channels.update replaces the whole brandingSettings part, so changes are applied to a copy of it."""
    branding = json.loads(json.dumps(current or {}))
    channel = branding.setdefault('channel', {})
    if description is not None:
        channel['description'] = description
    if keywords is not None:
        channel['keywords'] = format_keywords(keywords)
    if banner_url is not None:
        branding.setdefault('image', {})['bannerExternalUrl'] = banner_url
    return branding


def read_text(path):
    return Path(path).read_text(encoding='utf-8').strip() if path else None


def parse_tags(text):
    return [tag.strip() for tag in text.split(',') if tag.strip()] if text is not None else None


def fail_on(problems):
    if problems:
        raise YouTubeError('; '.join(problems))


# --- Uploads --------------------------------------------------------------------------------

def upload_video(token, path, metadata, renew_token=None):
    """Resumable upload in chunks; returns the video resource once YouTube has the whole file.

    renew_token returns a fresh access token when the current one expires during a long upload."""
    size = Path(path).stat().st_size
    content_type = mimetypes.guess_type(path)[0] or 'application/octet-stream'
    _, headers = request('POST', '/videos', token, {'uploadType': 'resumable', 'part': 'snippet,status'}, metadata,
                         base=UPLOAD_API, headers={'X-Upload-Content-Type': content_type, 'X-Upload-Content-Length': str(size)})
    session = headers['Location']

    def put(data, content_range):
        """Returns ('done', video), ('partial', next offset) or ('error', description)."""
        nonlocal token
        req = urllib.request.Request(session, data=data, method='PUT', headers={
            'Authorization': f'Bearer {token}', 'Content-Type': content_type, 'Content-Range': content_range})
        try:
            with urllib.request.urlopen(req, timeout=600) as response:
                return 'done', json.load(response)
        except urllib.error.HTTPError as error:
            if error.code == 308:
                received = error.headers.get('Range')
                return 'partial', int(received.split('-')[1]) + 1 if received else 0
            if error.code == 401 and renew_token:
                # The access token expired; the retry continues the same session with a new one.
                token = renew_token()
            return 'error', f'{error.code}: {describe_error(error.read())}'
        except (OSError, http.client.HTTPException, ValueError) as error:
            # Includes a connection closed mid-response and a truncated JSON body: the outcome is unknown.
            return 'error', str(getattr(error, 'reason', error)) or type(error).__name__

    with open(path, 'rb') as stream:
        offset, failures = 0, 0
        while True:
            stream.seek(offset)
            chunk = stream.read(UPLOAD_CHUNK)
            kind, value = put(chunk, f'bytes {offset}-{offset + len(chunk) - 1}/{size}')
            while kind == 'error':
                # The failed request may still have reached YouTube, so nothing is resent until the session says where it stands.
                failures += 1
                if failures > UPLOAD_RETRIES:
                    raise YouTubeError(f'Upload stopped at byte {offset} of {size} ({value}). YouTube may still have '
                                       'received the video: check YouTube Studio before uploading again.')
                time.sleep(2 ** failures)
                kind, value = put(b'', f'bytes */{size}')
            if kind == 'done':
                return value
            if value > offset:
                failures = 0
            offset = value
            print(f'  {offset * 100 // size} % uploaded', flush=True)


def upload_image(token, path, endpoint, params=None):
    data = Path(path).read_bytes()
    content_type = 'image/png' if Path(path).suffix.lower() == '.png' else 'image/jpeg'
    return call('POST', endpoint, token, params, base=UPLOAD_API, data=data, content_type=content_type)


# --- Commands -------------------------------------------------------------------------------

def command_client_import(args):
    data = json.loads(Path(args.file).read_text(encoding='utf-8'))
    client = data.get('installed')
    if not client:
        raise YouTubeError('This is not a Desktop app OAuth client file (no "installed" section).')
    save(CLIENT_TARGET, client['client_id'], {'client_id': client['client_id'], 'client_secret': client['client_secret']})
    print(f'Saved the OAuth client to Credential Manager ({CLIENT_TARGET}). You can now delete {args.file}.')


def command_login(_):
    client = load_client()
    verifier, challenge = pkce_pair()
    state = secrets.token_urlsafe(16)
    redirect_uri, wait = receive_code(state)
    url = AUTHORIZE_URL + '?' + urllib.parse.urlencode({
        'client_id': client['client_id'], 'redirect_uri': redirect_uri, 'response_type': 'code', 'scope': SCOPE,
        'access_type': 'offline', 'prompt': 'consent select_account', 'state': state,
        'code_challenge': challenge, 'code_challenge_method': 'S256'})
    print('Opening Google sign-in in the browser. Choose the OpenSkiTime channel, not a personal one.')
    webbrowser.open(url)
    code = wait()
    tokens = post_form(TOKEN_URL, {'client_id': client['client_id'], 'client_secret': client['client_secret'], 'code': code,
                                   'code_verifier': verifier, 'redirect_uri': redirect_uri, 'grant_type': 'authorization_code'})
    if 'refresh_token' not in tokens:
        raise YouTubeError('Google returned no refresh token; remove the app at myaccount.google.com/permissions and sign in again.')
    channel = my_channel(tokens['access_token'], 'snippet')
    save(TOKEN_TARGET, channel['snippet']['title'], {'refresh_token': tokens['refresh_token'], 'channel_id': channel['id'],
                                                      'title': channel['snippet']['title'], 'saved': datetime.now(timezone.utc).isoformat()})
    print(f"Signed in as the channel \"{channel['snippet']['title']}\" ({channel['snippet'].get('customUrl', channel['id'])}).")


def command_status(_):
    token = access_token()
    channel = my_channel(token)
    snippet, stats = channel['snippet'], channel.get('statistics', {})
    branding = channel.get('brandingSettings', {})
    print(f"{snippet['title']} ({snippet.get('customUrl', '')}, {channel['id']})")
    print(f"{stats.get('subscriberCount', '?')} subscribers, {stats.get('videoCount', '?')} videos, {stats.get('viewCount', '?')} views")
    description = branding.get('channel', {}).get('description', '')
    print(f"Description: {len(description)} characters; keywords: {branding.get('channel', {}).get('keywords', '(none)')}")
    print(f"Banner: {'set' if branding.get('image', {}).get('bannerExternalUrl') else 'not set'}")


def command_channel(args):
    description = read_text(args.description_file)
    keywords = parse_keywords(args.keywords) if args.keywords is not None else None
    problems = check_channel_metadata(description, keywords) + (check_image(args.banner, 'banner') if args.banner else [])
    fail_on(problems)
    if description is None and keywords is None and not args.banner:
        raise YouTubeError('Nothing to change; give --description-file, --keywords or --banner.')
    token = access_token()
    channel = my_channel(token, 'snippet,brandingSettings')
    print(f"Channel \"{channel['snippet']['title']}\":")
    if description is not None:
        print(f'  description ({len(description)} characters):\n{description}\n')
    if keywords is not None:
        print(f'  keywords: {format_keywords(keywords)}')
    if args.banner:
        print(f'  banner: {args.banner}')
    if not args.yes:
        print('Preview only; nothing was changed. Add --yes to apply.')
        return
    banner_url = upload_image(token, args.banner, '/channelBanners/insert')['url'] if args.banner else None
    branding = merged_branding(channel.get('brandingSettings'), description, keywords, banner_url)
    call('PUT', '/channels', token, {'part': 'brandingSettings'}, {'id': channel['id'], 'brandingSettings': branding})
    print('Channel updated.')


WRITABLE_SNIPPET = ('title', 'description', 'tags', 'categoryId', 'defaultLanguage', 'defaultAudioLanguage')
WRITABLE_STATUS = ('privacyStatus', 'embeddable', 'license', 'publicStatsViewable', 'publishAt', 'selfDeclaredMadeForKids',
                   'containsSyntheticMedia')


def video_metadata(args, current=None):
    """videos.update replaces whole parts, so an update starts from the video's current writable values."""
    snippet = {key: value for key, value in (current or {}).get('snippet', {}).items() if key in WRITABLE_SNIPPET}
    status = {key: value for key, value in (current or {}).get('status', {}).items() if key in WRITABLE_STATUS}
    if args.title is not None:
        snippet['title'] = args.title
    description = read_text(args.description_file)
    if description is not None:
        snippet['description'] = description
    tags = parse_tags(args.tags)
    if tags is not None:
        snippet['tags'] = tags
    if getattr(args, 'category', None):
        snippet['categoryId'] = CATEGORIES[args.category]
    if args.privacy:
        status['privacyStatus'] = args.privacy
        if args.privacy != 'private':
            # A scheduled publication time is only valid on a private video.
            status.pop('publishAt', None)
    if current is None:
        status['selfDeclaredMadeForKids'] = False
        snippet.setdefault('defaultLanguage', 'en')
    fail_on(check_video_metadata(snippet.get('title'), snippet.get('description'), snippet.get('tags')))
    return {'snippet': snippet, 'status': status}


def print_video(metadata):
    snippet, status = metadata['snippet'], metadata['status']
    print(f"  title: {snippet.get('title')}")
    print(f"  privacy: {status.get('privacyStatus')}, category {snippet.get('categoryId')}, tags: {', '.join(snippet.get('tags', [])) or '(none)'}")
    print(f"  description ({len(snippet.get('description', ''))} characters):\n{snippet.get('description', '')}\n")


def command_upload(args):
    if not Path(args.file).is_file():
        raise YouTubeError(f'{args.file}: file not found')
    fail_on(check_image(args.thumbnail, 'thumbnail') if args.thumbnail else [])
    metadata = video_metadata(args)
    print(f'Upload {args.file} ({Path(args.file).stat().st_size // (1024 * 1024)} MB):')
    print_video(metadata)
    if args.thumbnail:
        print(f'  thumbnail: {args.thumbnail}')
    if not args.yes:
        print('Preview only; nothing was uploaded. Add --yes to upload.')
        return
    token = access_token()
    video = upload_video(token, args.file, metadata, renew_token=access_token)
    print(f"Uploaded: https://www.youtube.com/watch?v={video['id']} ({video['status']['privacyStatus']}, "
          f"{video['status'].get('uploadStatus')})")
    if args.thumbnail:
        # A long upload may have outlived the first access token.
        upload_image(access_token(), args.thumbnail, '/thumbnails/set', {'videoId': video['id']})
        print('Thumbnail set.')


def command_update(args):
    token = access_token()
    items = call('GET', '/videos', token, {'part': 'snippet,status', 'id': args.video_id}).get('items', [])
    if not items:
        raise YouTubeError(f'Video {args.video_id} was not found on this channel.')
    metadata = video_metadata(args, items[0])
    print(f'Update https://www.youtube.com/watch?v={args.video_id}:')
    print_video(metadata)
    if not args.yes:
        print('Preview only; nothing was changed. Add --yes to apply.')
        return
    call('PUT', '/videos', token, {'part': 'snippet,status'}, {'id': args.video_id, **metadata})
    print('Video updated.')


def command_thumbnail(args):
    fail_on(check_image(args.image, 'thumbnail'))
    print(f'Set the thumbnail of https://www.youtube.com/watch?v={args.video_id} to {args.image}.')
    if not args.yes:
        print('Preview only; nothing was changed. Add --yes to apply.')
        return
    upload_image(access_token(), args.image, '/thumbnails/set', {'videoId': args.video_id})
    print('Thumbnail set.')


def main(argv=None):
    parser = argparse.ArgumentParser(description='Manage the OpenSkiTime YouTube channel.')
    commands = parser.add_subparsers(dest='command', required=True)
    command = commands.add_parser('client-import', help='save the Desktop app OAuth client to Credential Manager')
    command.add_argument('file')
    commands.add_parser('login', help='sign in to the channel in the browser and save the refresh token')
    commands.add_parser('status', help='show the channel')
    command = commands.add_parser('channel', help='change the channel description, keywords or banner')
    command.add_argument('--description-file')
    command.add_argument('--keywords', help='space-separated; quote multi-word keywords')
    command.add_argument('--banner', help='PNG or JPEG, 16:9, at least 2048 x 1152, at most 6 MB')
    command.add_argument('--yes', action='store_true')
    for name in ('upload', 'update'):
        command = commands.add_parser(name, help='upload a video' if name == 'upload' else 'change a video')
        command.add_argument('file' if name == 'upload' else 'video_id')
        command.add_argument('--title', required=name == 'upload')
        command.add_argument('--description-file', required=name == 'upload')
        command.add_argument('--tags', help='comma-separated')
        command.add_argument('--privacy', choices=PRIVACY, default='private' if name == 'upload' else None)
        command.add_argument('--category', choices=sorted(CATEGORIES), default='sports' if name == 'upload' else None)
        if name == 'upload':
            command.add_argument('--thumbnail')
        command.add_argument('--yes', action='store_true')
    command = commands.add_parser('thumbnail', help='set a video thumbnail')
    command.add_argument('video_id')
    command.add_argument('image')
    command.add_argument('--yes', action='store_true')
    args = parser.parse_args(argv)
    handlers = {'client-import': command_client_import, 'login': command_login, 'status': command_status,
                'channel': command_channel, 'upload': command_upload, 'update': command_update, 'thumbnail': command_thumbnail}
    try:
        handlers[args.command](args)
    except YouTubeError as error:
        print(f'Error: {error}', file=sys.stderr)
        return 1
    return 0


if __name__ == '__main__':
    sys.exit(main())
