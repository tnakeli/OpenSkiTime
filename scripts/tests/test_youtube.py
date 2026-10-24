"""Tests for the YouTube channel tool that need no network, OAuth client or token."""
import argparse
import base64
import contextlib
import hashlib
import io
from pathlib import Path
import sys
import tempfile
import unittest
from unittest import mock
import urllib.error

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'social'))
import youtube  # noqa: E402


class PkceTests(unittest.TestCase):
    def test_challenge_is_s256_of_verifier(self):
        verifier, challenge = youtube.pkce_pair()
        self.assertTrue(43 <= len(verifier) <= 128)
        expected = base64.urlsafe_b64encode(hashlib.sha256(verifier.encode()).digest()).rstrip(b'=').decode()
        self.assertEqual(challenge, expected)
        self.assertNotEqual(youtube.pkce_pair()[0], verifier)


class KeywordTests(unittest.TestCase):
    def test_multi_word_keywords_are_quoted_and_round_trip(self):
        words = ['alpine skiing', 'FIS', 'race timing software']
        text = youtube.format_keywords(words)
        self.assertEqual(text, '"alpine skiing" FIS "race timing software"')
        self.assertEqual(youtube.parse_keywords(text), words)


class ValidationTests(unittest.TestCase):
    def test_accepts_valid_video_metadata(self):
        self.assertEqual(youtube.check_video_metadata('Demo', 'Two runs.', ['slalom', 'FIS timing']), [])

    def test_rejects_limits_and_angle_brackets(self):
        problems = youtube.check_video_metadata('x' * 101, 'a <b>', ['t' * 501])
        self.assertTrue(any('101 characters' in p for p in problems))
        self.assertTrue(any('description contains <' in p for p in problems))
        self.assertTrue(any('tags take' in p for p in problems))
        self.assertIn('title is empty', youtube.check_video_metadata(' ', None, None))

    def test_description_limit_counts_bytes(self):
        self.assertEqual(youtube.check_video_metadata('T', 'a' * 5000), [])
        self.assertTrue(youtube.check_video_metadata('T', 'ä' * 2501))

    def test_channel_limits(self):
        self.assertTrue(youtube.check_channel_metadata('x' * 1001))
        self.assertEqual(youtube.check_channel_metadata('ok', ['a', 'b c']), [])

    def test_image_checks(self):
        with tempfile.TemporaryDirectory() as directory:
            gif = Path(directory) / 'a.gif'
            gif.write_bytes(b'x')
            png = Path(directory) / 'a.png'
            png.write_bytes(b'x' * (2 * 1024 * 1024 + 1))
            self.assertIn('PNG or JPEG', youtube.check_image(gif, 'banner')[0])
            self.assertIn('limit', youtube.check_image(png, 'thumbnail')[0])
            self.assertEqual(youtube.check_image(png, 'banner'), [])


class BrandingTests(unittest.TestCase):
    def test_merge_keeps_unchanged_settings(self):
        current = {'channel': {'title': 'OpenSkiTime', 'description': 'old', 'country': 'FI'},
                   'image': {'bannerExternalUrl': 'https://old'}}
        merged = youtube.merged_branding(current, description='new', keywords=['a b'])
        self.assertEqual(merged['channel'], {'title': 'OpenSkiTime', 'description': 'new', 'country': 'FI', 'keywords': '"a b"'})
        self.assertEqual(merged['image']['bannerExternalUrl'], 'https://old')
        self.assertEqual(current['channel']['description'], 'old')


class VideoMetadataTests(unittest.TestCase):
    def args(self, **values):
        defaults = {'title': None, 'description_file': None, 'tags': None, 'category': None, 'privacy': None}
        return argparse.Namespace(**{**defaults, **values})

    def test_update_keeps_current_writable_values_and_drops_read_only_ones(self):
        current = {'snippet': {'title': 'Old', 'description': 'D', 'categoryId': '17', 'tags': ['a'],
                               'thumbnails': {}, 'channelId': 'c', 'publishedAt': 'p'},
                   'status': {'privacyStatus': 'unlisted', 'embeddable': False, 'uploadStatus': 'processed'}}
        metadata = youtube.video_metadata(self.args(title='New'), current)
        self.assertEqual(metadata['snippet'], {'title': 'New', 'description': 'D', 'categoryId': '17', 'tags': ['a']})
        self.assertEqual(metadata['status'], {'privacyStatus': 'unlisted', 'embeddable': False})

    def test_publishing_a_scheduled_video_drops_the_schedule(self):
        current = {'snippet': {'title': 'T', 'categoryId': '17'},
                   'status': {'privacyStatus': 'private', 'publishAt': '2026-11-01T10:00:00Z'}}
        self.assertEqual(youtube.video_metadata(self.args(privacy='public'), current)['status'], {'privacyStatus': 'public'})
        self.assertIn('publishAt', youtube.video_metadata(self.args(title='New'), current)['status'])

    def test_new_upload_is_not_made_for_kids(self):
        metadata = youtube.video_metadata(self.args(title='Demo', category='sports', privacy='private'))
        self.assertEqual(metadata['status'], {'privacyStatus': 'private', 'selfDeclaredMadeForKids': False})
        self.assertEqual(metadata['snippet']['categoryId'], '17')


class UploadTests(unittest.TestCase):
    def test_resumes_from_the_range_youtube_confirms(self):
        sent = []

        class Response(io.BytesIO):
            def __enter__(self):
                return self

            def __exit__(self, *_):
                return False

        def fake_urlopen(req, timeout=None):
            sent.append(req.headers['Content-range'])
            if len(sent) == 1:
                # YouTube kept only the first 3 bytes of the first chunk.
                raise urllib.error.HTTPError(req.full_url, 308, 'Resume Incomplete', {'Range': 'bytes=0-2'}, io.BytesIO())
            return Response(b'{"id": "v1", "status": {"privacyStatus": "private", "uploadStatus": "uploaded"}}')

        with tempfile.TemporaryDirectory() as directory:
            video = Path(directory) / 'demo.mp4'
            video.write_bytes(b'0123456789')
            with mock.patch.object(youtube, 'request', return_value=({}, {'Location': 'https://upload.example/session'})), \
                    mock.patch.object(youtube, 'UPLOAD_CHUNK', 8), \
                    mock.patch.object(youtube.urllib.request, 'urlopen', side_effect=fake_urlopen), \
                    contextlib.redirect_stdout(io.StringIO()):
                video_resource = youtube.upload_video('token', str(video), {'snippet': {}, 'status': {}})
        self.assertEqual(video_resource['id'], 'v1')
        self.assertEqual(sent, ['bytes 0-7/10', 'bytes 3-9/10'])

    def test_failed_chunk_reports_nothing_published(self):
        def fake_urlopen(req, timeout=None):
            raise urllib.error.HTTPError(req.full_url, 500, 'Backend Error', {}, io.BytesIO(b'{"error": {"message": "Backend Error"}}'))

        with tempfile.TemporaryDirectory() as directory:
            video = Path(directory) / 'demo.mp4'
            video.write_bytes(b'0123456789')
            with mock.patch.object(youtube, 'request', return_value=({}, {'Location': 'https://upload.example/session'})), \
                    mock.patch.object(youtube.urllib.request, 'urlopen', side_effect=fake_urlopen):
                with self.assertRaisesRegex(youtube.YouTubeError, 'Nothing was published'):
                    youtube.upload_video('token', str(video), {'snippet': {}, 'status': {}})


class PreviewTests(unittest.TestCase):
    def test_upload_without_yes_signs_in_to_nothing(self):
        with tempfile.TemporaryDirectory() as directory:
            video = Path(directory) / 'demo.mp4'
            video.write_bytes(b'x')
            description = Path(directory) / 'description.txt'
            description.write_text('A demo.', encoding='utf-8')
            output = io.StringIO()
            with mock.patch.object(youtube, 'access_token') as token, mock.patch.object(youtube, 'upload_video') as upload, \
                    contextlib.redirect_stdout(output):
                code = youtube.main(['upload', str(video), '--title', 'Demo', '--description-file', str(description)])
            self.assertEqual(code, 0)
            token.assert_not_called()
            upload.assert_not_called()
            self.assertIn('privacy: private', output.getvalue())
            self.assertIn('Preview only', output.getvalue())

    def test_invalid_title_stops_before_sign_in(self):
        with tempfile.TemporaryDirectory() as directory:
            video = Path(directory) / 'demo.mp4'
            video.write_bytes(b'x')
            description = Path(directory) / 'description.txt'
            description.write_text('A demo.', encoding='utf-8')
            with mock.patch.object(youtube, 'access_token') as token, contextlib.redirect_stderr(io.StringIO()):
                code = youtube.main(['upload', str(video), '--title', 'x' * 101, '--description-file', str(description), '--yes'])
            self.assertEqual(code, 1)
            token.assert_not_called()

    def test_client_import_requires_desktop_client(self):
        with tempfile.TemporaryDirectory() as directory:
            web = Path(directory) / 'client.json'
            web.write_text('{"web": {"client_id": "x", "client_secret": "y"}}', encoding='utf-8')
            with mock.patch.object(youtube, 'save') as save, contextlib.redirect_stderr(io.StringIO()) as error:
                self.assertEqual(youtube.main(['client-import', str(web)]), 1)
            save.assert_not_called()
            self.assertIn('Desktop app', error.getvalue())


if __name__ == '__main__':
    unittest.main()
