"""ElevenLabs calls of the tutorial videos. The key stays in this Windows user's Credential Manager: the record that
OpenSkiTime saves in Settings -> Live timing -> Race radio provider keys -> ElevenLabs. It is never printed or written.
"""
import base64
import ctypes
import json
from pathlib import Path
import sys
import urllib.error
import urllib.parse
import urllib.request

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / 'social'))
import credentials  # noqa: E402

TARGET = 'OpenSkiTime.LiveTiming.Radio:elevenlabs'
API = 'https://api.elevenlabs.io/'
MODEL = 'eleven_v4'


class ElevenLabsError(Exception):
    pass


def _key():
    pointer = ctypes.POINTER(credentials._Credential)()
    if not ctypes.windll.advapi32.CredReadW(TARGET, 1, 0, ctypes.byref(pointer)):
        raise ElevenLabsError('No ElevenLabs key. Save it in OpenSkiTime: Settings -> Live timing -> Race radio provider keys.')
    try:
        blob = ctypes.string_at(pointer.contents.CredentialBlob, pointer.contents.CredentialBlobSize)
    finally:
        ctypes.windll.advapi32.CredFree(pointer)
    return (blob.decode('utf-16-le') if b'\0' in blob else blob.decode('utf-8')).strip()


def call(path, body=None):
    key = _key()
    request = urllib.request.Request(API + path, data=None if body is None else json.dumps(body).encode('utf-8'),
                                     headers={'xi-api-key': key, 'Content-Type': 'application/json'})
    try:
        with urllib.request.urlopen(request, timeout=180) as response:
            return json.loads(response.read())
    except urllib.error.HTTPError as error:
        detail = error.read().decode('utf-8', 'replace').replace(key, '***')[:300]
        raise ElevenLabsError(f'ElevenLabs HTTP {error.code}: {detail}') from None
    except urllib.error.URLError as error:
        raise ElevenLabsError(f'ElevenLabs cannot be reached: {error.reason}') from None


def voice_id(name):
    """A voice ID as it is, or a voice of the account by its exact name (the key needs the Voices read permission)."""
    if len(name) == 20 and name.isalnum():
        return name
    voices = call('v2/voices?page_size=100&search=' + urllib.parse.quote(name))['voices']
    found = [v['voice_id'] for v in voices if v['name'].strip().lower() == name.lower()]
    if len(found) != 1:
        raise ElevenLabsError(f'{len(found)} voices are named "{name}".')
    return found[0]


def speak(text, voice, language):
    """Returns (mp3 bytes, start time of every character, end of the last one)."""
    reply = call(f'v1/text-to-speech/{voice}/with-timestamps?output_format=mp3_44100_128',
                 {'text': text, 'model_id': MODEL, 'language_code': language})
    alignment = reply['alignment']
    if ''.join(alignment['characters']) != text:
        raise ElevenLabsError('The alignment does not match the text.')
    return base64.b64decode(reply['audio_base64']), alignment['character_start_times_seconds'], alignment['character_end_times_seconds']
