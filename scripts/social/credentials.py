"""JSON records in this Windows user's Credential Manager, shared by the social publishing tools."""
import ctypes
import ctypes.wintypes as wt
import json


class CredentialError(Exception):
    pass


class _Credential(ctypes.Structure):
    _fields_ = [('Flags', wt.DWORD), ('Type', wt.DWORD), ('TargetName', wt.LPWSTR), ('Comment', wt.LPWSTR),
                ('LastWritten', wt.FILETIME), ('CredentialBlobSize', wt.DWORD),
                ('CredentialBlob', ctypes.POINTER(ctypes.c_char)), ('Persist', wt.DWORD),
                ('AttributeCount', wt.DWORD), ('Attributes', ctypes.c_void_p),
                ('TargetAlias', wt.LPWSTR), ('UserName', wt.LPWSTR)]


_CRED_TYPE_GENERIC = 1
_CRED_PERSIST_LOCAL_MACHINE = 2
_ERROR_NOT_FOUND = 1168
# CRED_MAX_CREDENTIAL_BLOB_SIZE: 5 * 512 bytes.
MAX_BLOB_BYTES = 2560


def save(target, username, record):
    blob = json.dumps(record).encode('utf-8')
    if len(blob) > MAX_BLOB_BYTES:
        raise CredentialError(f'{target}: record is {len(blob)} bytes, Credential Manager allows {MAX_BLOB_BYTES}.')
    buffer = ctypes.create_string_buffer(blob, len(blob))
    credential = _Credential(Type=_CRED_TYPE_GENERIC, TargetName=target, UserName=username,
                             CredentialBlobSize=len(blob), CredentialBlob=ctypes.cast(buffer, ctypes.POINTER(ctypes.c_char)),
                             Persist=_CRED_PERSIST_LOCAL_MACHINE)
    if not ctypes.windll.advapi32.CredWriteW(ctypes.byref(credential), 0):
        raise CredentialError(f'Could not save {target} to Credential Manager (error {ctypes.GetLastError()}).')


def delete(target):
    """Removes a record; returns False when there was none."""
    if ctypes.windll.advapi32.CredDeleteW(target, _CRED_TYPE_GENERIC, 0):
        return True
    if ctypes.GetLastError() == _ERROR_NOT_FOUND:
        return False
    raise CredentialError(f'Could not delete {target} from Credential Manager (error {ctypes.GetLastError()}).')


def load(target, missing_hint):
    """Returns the saved record; missing_hint tells the operator how to create it."""
    pointer = ctypes.POINTER(_Credential)()
    if not ctypes.windll.advapi32.CredReadW(target, _CRED_TYPE_GENERIC, 0, ctypes.byref(pointer)):
        if ctypes.GetLastError() == _ERROR_NOT_FOUND:
            raise CredentialError(f'Nothing saved as {target}. {missing_hint}')
        raise CredentialError(f'Could not read {target} from Credential Manager (error {ctypes.GetLastError()}).')
    try:
        credential = pointer.contents
        return json.loads(ctypes.string_at(credential.CredentialBlob, credential.CredentialBlobSize).decode('utf-8'))
    finally:
        ctypes.windll.advapi32.CredFree(pointer)
