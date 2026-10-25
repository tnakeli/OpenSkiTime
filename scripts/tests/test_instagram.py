"""Tests for the Instagram publishing tool that need no network, token or Azure access."""
import contextlib
import io
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'social'))
import instagram  # noqa: E402


class CaptionTests(unittest.TestCase):
    def test_accepts_caption_within_limits(self):
        self.assertEqual(instagram.check_caption('Race day. #alpineskiing #racetiming'), [])

    def test_rejects_long_caption_and_too_many_hashtags(self):
        problems = instagram.check_caption('x' * 2201)
        self.assertIn('2201 characters', problems[0])
        problems = instagram.check_caption(' '.join(f'#tag{n}' for n in range(31)))
        self.assertIn('31 hashtags', problems[0])

    def test_lone_hash_is_not_a_hashtag(self):
        self.assertEqual(instagram.check_caption(' '.join(['#'] * 40)), [])


class MediaTests(unittest.TestCase):
    def test_classifies_media_by_extension(self):
        self.assertEqual(instagram.media_kind('a.PNG'), 'image')
        self.assertEqual(instagram.media_kind('a.mp4'), 'video')
        for unsupported in ('a.gif', 'a.webp'):
            with self.assertRaises(instagram.InstagramError):
                instagram.media_kind(unsupported)

    def test_jpeg_is_used_as_is(self):
        self.assertEqual(instagram.to_jpeg('photo.JPG', '.'), Path('photo.JPG'))


class ErrorTests(unittest.TestCase):
    def test_describes_graph_error_without_request_details(self):
        body = json.dumps({'error': {'message': 'Invalid OAuth access token', 'code': 190, 'error_subcode': 463}}).encode()
        self.assertEqual(instagram.describe_error(body), 'Invalid OAuth access token (code 190, subcode 463)')

    def test_describes_non_json_error(self):
        self.assertEqual(instagram.describe_error(b'Bad gateway'), 'Bad gateway')


class PreviewTests(unittest.TestCase):
    def test_without_yes_nothing_is_uploaded_or_published(self):
        with tempfile.TemporaryDirectory() as directory:
            image = Path(directory) / 'photo.jpg'
            image.write_bytes(b'not checked in preview')
            output = io.StringIO()
            with mock.patch.object(instagram, 'load_credential', return_value={'username': 'openskitime', 'user_id': '1', 'token': 't'}), \
                    mock.patch.object(instagram, 'publish') as publish, mock.patch.object(instagram, 'call') as call, \
                    contextlib.redirect_stdout(output):
                code = instagram.main(['post', str(image), '--caption', 'Hello'])
            self.assertEqual(code, 0)
            publish.assert_not_called()
            call.assert_not_called()
            self.assertIn('Preview only', output.getvalue())

    def test_invalid_caption_stops_before_reading_the_token(self):
        with tempfile.TemporaryDirectory() as directory:
            image = Path(directory) / 'photo.jpg'
            image.write_bytes(b'x')
            with mock.patch.object(instagram, 'load_credential') as load, contextlib.redirect_stderr(io.StringIO()):
                code = instagram.main(['post', str(image), '--caption', 'x' * 2201, '--yes'])
            self.assertEqual(code, 1)
            load.assert_not_called()

    def test_story_without_yes_nothing_is_uploaded(self):
        with tempfile.TemporaryDirectory() as directory:
            clip = Path(directory) / 'clip.mp4'
            clip.write_bytes(b'not checked in preview')
            output = io.StringIO()
            with mock.patch.object(instagram, 'load_credential', return_value={'username': 'openskitime', 'user_id': '1', 'token': 't'}),                     mock.patch.object(instagram, 'publish') as publish, contextlib.redirect_stdout(output):
                code = instagram.main(['story', str(clip)])
            self.assertEqual(code, 0)
            publish.assert_not_called()
            self.assertIn('Preview only', output.getvalue())

    def test_reel_requires_video(self):
        with contextlib.redirect_stderr(io.StringIO()) as error:
            self.assertEqual(instagram.main(['reel', 'cover.png', '--caption', 'x']), 1)
        self.assertIn('needs a video', error.getvalue())


class PublishTests(unittest.TestCase):
    def test_publishes_only_after_container_is_finished(self):
        calls = []

        def fake_call(method, path, token, params=None, base=None):
            calls.append((method, path))
            if path == '/1/media':
                return {'id': 'c1'}
            if path == '/c1':
                return {'status_code': 'FINISHED'}
            if path == '/1/media_publish':
                return {'id': 'm1'}
            if path == '/m1':
                return {'permalink': 'https://www.instagram.com/p/x/'}
            raise AssertionError(path)

        staging = mock.MagicMock()
        staging.__enter__.return_value.url.return_value = 'https://example.invalid/blob.jpg?sas'
        with mock.patch.object(instagram, 'call', side_effect=fake_call), \
                mock.patch.object(instagram, 'Staging', return_value=staging):
            link = instagram.publish({'token': 't', 'user_id': '1'}, ['photo.jpg'], 'Hello', alt_text='Timing screen')
        self.assertEqual(link, 'https://www.instagram.com/p/x/')
        self.assertEqual(calls, [('POST', '/1/media'), ('GET', '/c1'), ('POST', '/1/media_publish'), ('GET', '/m1')])
        staging.__exit__.assert_called_once()

    def test_story_is_published_without_caption(self):
        created = []

        def fake_call(method, path, token, params=None, base=None):
            if path == '/1/media':
                created.append(params)
                return {'id': 'c1'}
            if path == '/c1':
                return {'status_code': 'FINISHED'}
            if path == '/1/media_publish':
                return {'id': 'm1'}
            if path == '/m1':
                return {'permalink': 'https://www.instagram.com/stories/openskitime/m1/'}
            raise AssertionError(path)

        staging = mock.MagicMock()
        staging.__enter__.return_value.url.return_value = 'https://example.invalid/blob.mp4?sas'
        with mock.patch.object(instagram, 'call', side_effect=fake_call),                 mock.patch.object(instagram, 'Staging', return_value=staging):
            link = instagram.publish({'token': 't', 'user_id': '1'}, ['clip.mp4'], None, story=True)
        self.assertEqual(link, 'https://www.instagram.com/stories/openskitime/m1/')
        self.assertEqual(created, [{'media_type': 'STORIES', 'video_url': 'https://example.invalid/blob.mp4?sas'}])

    def test_processing_error_publishes_nothing(self):
        def fake_call(method, path, token, params=None, base=None):
            if path == '/1/media':
                return {'id': 'c1'}
            if path == '/c1':
                return {'status_code': 'ERROR', 'status': 'Error: unsupported image'}
            raise AssertionError(f'unexpected {method} {path}')

        staging = mock.MagicMock()
        staging.__enter__.return_value.url.return_value = 'https://example.invalid/blob.jpg?sas'
        with mock.patch.object(instagram, 'call', side_effect=fake_call), \
                mock.patch.object(instagram, 'Staging', return_value=staging):
            with self.assertRaises(instagram.InstagramError):
                instagram.publish({'token': 't', 'user_id': '1'}, ['photo.jpg'], 'Hello')
        staging.__exit__.assert_called_once()


if __name__ == '__main__':
    unittest.main()
