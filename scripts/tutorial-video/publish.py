"""Uploads a finished tutorial video and its subtitles to the OpenSkiTime YouTube channel.

The title, description and tags are the script's "youtube" entry for the language. The video is uploaded with
scripts/social/youtube.py (sign-in, Credential Manager and resumable upload, docs/social-media.md), then the SRT
written by compose.py is added as the subtitle track of the language. The video ID is saved next to the video
(<video>.youtube.json), so a second run never uploads the same video again; it only adds subtitles that are missing.

    python scripts/tutorial-video/publish.py fis-event --language fi --output C:/OpenSkiTimeVideos/fis-event/fi/output [--privacy private] [--yes]
"""
import argparse
import json
from pathlib import Path
import secrets
import sys

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent / 'social'))
import youtube  # noqa: E402


def main(argv=None):
    parser = argparse.ArgumentParser(description='Upload a tutorial video to YouTube.')
    parser.add_argument('name')
    parser.add_argument('--language', choices=['fi', 'en'], required=True)
    parser.add_argument('--output', required=True, help='folder of the composed video (compose.py --output)')
    parser.add_argument('--privacy', choices=youtube.PRIVACY, default='private')
    parser.add_argument('--yes', action='store_true')
    args = parser.parse_args(argv)
    script = json.loads((HERE / 'tutorials' / f'{args.name}.json').read_text(encoding='utf-8'))
    entry = script['youtube'][args.language]
    output = Path(args.output)
    video = output / f'OpenSkiTime-{args.name}-{args.language}.mp4'
    subtitles = video.with_suffix('.srt')
    state_file = video.with_name(video.stem + '.youtube.json')
    state = json.loads(state_file.read_text(encoding='utf-8')) if state_file.is_file() else {}
    metadata = youtube.video_metadata(argparse.Namespace(
        title=entry['title'], description_file=None, tags=','.join(entry['tags']), category='tech',
        privacy=args.privacy, language=args.language))
    metadata['snippet']['description'] = entry['description']
    # The narration voice and the music are made with AI: YouTube's altered or synthetic content label.
    metadata['status']['containsSyntheticMedia'] = True
    youtube.fail_on(youtube.check_video_metadata(entry['title'], entry['description'], entry['tags']))
    if not video.is_file() or not subtitles.is_file():
        raise youtube.YouTubeError(f'{video} or its subtitles are missing: run compose.py first')
    if 'video' in state:
        print(f"Already uploaded: https://www.youtube.com/watch?v={state['video']}")
    else:
        print(f'Upload {video.name}:')
        youtube.print_video(metadata)
    if 'captions' not in state:
        print(f'Add {args.language} subtitles from {subtitles.name}.')
    if not args.yes:
        print('Preview only; nothing was uploaded. Add --yes to upload.')
        return 0
    if 'video' not in state:
        uploaded = youtube.upload_video(youtube.access_token(), str(video), metadata, renew_token=youtube.access_token)
        state['video'] = uploaded['id']
        state_file.write_text(json.dumps(state, indent=2) + '\n', encoding='utf-8')
        print(f"Uploaded: https://www.youtube.com/watch?v={uploaded['id']} ({uploaded['status']['privacyStatus']})")
    if 'captions' not in state:
        # A track that reached YouTube although the answer was lost is not inserted twice (409 captionExists).
        tracks = youtube.call('GET', '/captions', youtube.access_token(), {'part': 'snippet', 'videoId': state['video']}).get('items', [])
        if any(x['snippet'].get('language') == args.language and x['snippet'].get('trackKind') == 'standard' for x in tracks):
            state['captions'] = args.language
            state_file.write_text(json.dumps(state, indent=2) + '\n', encoding='utf-8')
            print('Subtitles already on YouTube.')
    if 'captions' not in state:
        boundary = secrets.token_hex(16)
        youtube.call('POST', '/captions', youtube.access_token(), {'part': 'snippet', 'uploadType': 'multipart'},
                     base=youtube.UPLOAD_API, content_type=f'multipart/related; boundary={boundary}',
                     data=youtube.captions_body(state['video'], args.language, '', subtitles.read_bytes(), boundary))
        state['captions'] = args.language
        state_file.write_text(json.dumps(state, indent=2) + '\n', encoding='utf-8')
        print('Subtitles added.')
    return 0


if __name__ == '__main__':
    try:
        sys.exit(main())
    except youtube.YouTubeError as error:
        print(f'Error: {error}', file=sys.stderr)
        sys.exit(1)
