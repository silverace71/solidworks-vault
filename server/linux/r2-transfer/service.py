#!/usr/bin/env python3
"""SWVAULT 1.4 private R2 multipart upload coordinator.

Client file bytes go directly to signed R2 UploadPart PUT URLs.  The server
validates the staged object into local cold storage (SHA-256) and uses the
supported Gitea LFS upload endpoint to register it.  No direct DB edits.

This is intentionally a separate, opt-in service: do not expose port 3200
outside Caddy and do not run on a production dataset without a restore test.
"""
import base64
import hashlib
import hmac
import json
import logging
import os
from pathlib import Path
import re
import secrets
import sqlite3
import threading
import time
import urllib.parse
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlparse, parse_qs

import boto3
from botocore.config import Config
import requests

LOG = logging.getLogger('swvault-transfer')
OID_RE = re.compile(r'^[0-9a-f]{64}$')
SESSION_RE = re.compile(r'^[a-f0-9]{48}$')
PART_BYTES = 32 * 1024 * 1024
MAX_FILE_BYTES = int(os.getenv('MAX_FILE_BYTES', str(4 * 1024**3)))
MAX_ACTIVE_PER_USER = 3
SESSION_TTL = 24 * 3600
REQUEST_LIMIT = 16 * 1024
DB_LOCK = threading.RLock()
FINALIZE_LIMIT = threading.Semaphore(2)

R2_BUCKET = os.environ.get('R2_BUCKET', '')
R2_ACCOUNT_ID = os.environ.get('R2_ACCOUNT_ID', '')
ORG = os.environ.get('ORG', '')
REPO = os.environ.get('REPO', '')
ADMIN_USER = os.environ.get('ADMIN_USER', '')
ADMIN_TOKEN_FILE = os.environ.get('ADMIN_TOKEN_FILE', '/run/swvault/admin-token')
DATA_DIR = Path(os.environ.get('TRANSFER_DATA_DIR', '/state'))
COLD_DIR = Path(os.environ.get('COLD_LFS_DIR', '/cold-lfs'))
GITEA_URL = os.environ.get('GITEA_URL', 'http://127.0.0.1:3000').rstrip('/')


def create_s3():
    return boto3.client(
        's3',
        endpoint_url=f'https://{R2_ACCOUNT_ID}.r2.cloudflarestorage.com',
        aws_access_key_id=os.environ['R2_ACCESS_KEY_ID'],
        aws_secret_access_key=os.environ['R2_SECRET_ACCESS_KEY'],
        region_name='auto',
        config=Config(signature_version='s3v4', s3={'addressing_style': 'path'},
                      retries={'max_attempts': 4},
                      request_checksum_calculation='when_required',
                      response_checksum_validation='when_required'),
    )


def open_db():
    DATA_DIR.mkdir(parents=True, exist_ok=True)
    con = sqlite3.connect(DATA_DIR / 'transfers.db', timeout=30)
    con.row_factory = sqlite3.Row
    con.execute('PRAGMA busy_timeout=30000')
    con.execute('''CREATE TABLE IF NOT EXISTS uploads (
        id TEXT PRIMARY KEY, oid TEXT NOT NULL, size INTEGER NOT NULL,
        username TEXT NOT NULL, r2_key TEXT NOT NULL, upload_id TEXT NOT NULL,
        status TEXT NOT NULL, created INTEGER NOT NULL, error TEXT NOT NULL DEFAULT ''
    )''')
    # Schema migration for early 1.4 prerelease installations only.
    cols = {r[1] for r in con.execute('PRAGMA table_info(uploads)')}
    if 'error' not in cols:
        con.execute("ALTER TABLE uploads ADD COLUMN error TEXT NOT NULL DEFAULT ''")
    con.commit()
    return con


def admin_token():
    return Path(ADMIN_TOKEN_FILE).read_text(encoding='utf-8').strip()


def check_user_authorization(header):
    """Validate Gitea token and repository WRITE permission on EACH action."""
    if not header or not header.startswith('Basic '):
        raise ClientError(401, 'Gitea Basic token required')
    try:
        raw = base64.b64decode(header[6:], validate=True).decode('utf-8')
        username, secret = raw.split(':', 1)
    except (ValueError, UnicodeError):
        raise ClientError(401, 'Malformed authentication')
    if not username or not secret or len(raw) > 4096:
        raise ClientError(401, 'Invalid authentication')
    # Requests are loopback only; never forward credentials to an untrusted host.
    authorization = {'Authorization': header, 'Accept': 'application/json'}
    try:
        user_resp = requests.get(f'{GITEA_URL}/api/v1/user', headers=authorization, timeout=15,
                                 allow_redirects=False)
        if user_resp.status_code != 200:
            raise ClientError(401, 'Gitea credentials rejected')
        actual = user_resp.json().get('login', '')
        repo_resp = requests.get(f'{GITEA_URL}/api/v1/repos/{ORG}/{REPO}',
                                 headers=authorization, timeout=15, allow_redirects=False)
        if repo_resp.status_code != 200:
            raise ClientError(403, 'Repository unavailable')
        permissions = repo_resp.json().get('permissions') or {}
        if not permissions.get('push') and not permissions.get('admin'):
            raise ClientError(403, 'Repository write permission required')
    except requests.RequestException:
        raise ClientError(503, 'Gitea authentication unavailable')
    if not actual or actual.casefold() != username.casefold():
        raise ClientError(401, 'Gitea user mismatch')
    return actual


class ClientError(Exception):
    def __init__(self, status, message):
        self.status = status
        super().__init__(message)


def get_session(con, session_id, username):
    if not SESSION_RE.fullmatch(session_id):
        raise ClientError(400, 'Invalid transfer session')
    row = con.execute('SELECT * FROM uploads WHERE id=?', (session_id,)).fetchone()
    if row is None or row['username'].casefold() != username.casefold():
        raise ClientError(404, 'Transfer not found')
    if int(time.time()) - row['created'] > SESSION_TTL and row['status'] == 'uploading':
        raise ClientError(410, 'Upload session expired; restart check-in')
    return row


def begin_upload(s3, username, oid, size):
    if not isinstance(oid, str) or not OID_RE.fullmatch(oid):
        raise ClientError(400, 'Invalid SHA-256 object ID')
    if type(size) is not int or size < 1 or size > MAX_FILE_BYTES:
        raise ClientError(413, 'File size is unsupported')
    with DB_LOCK, open_db() as con:
        active = con.execute("SELECT count(*) FROM uploads WHERE username=? AND status IN ('uploading','finalizing')",
                             (username,)).fetchone()[0]
        if active >= MAX_ACTIVE_PER_USER:
            raise ClientError(429, 'Too many simultaneous uploads')
        session_id = secrets.token_hex(24)
        key = f'stage/{session_id}/{oid}'
        created = s3.create_multipart_upload(Bucket=R2_BUCKET, Key=key, ContentType='application/octet-stream')
        con.execute('INSERT INTO uploads (id,oid,size,username,r2_key,upload_id,status,created) VALUES (?,?,?,?,?,?,?,?)',
                    (session_id, oid, size, username, key, created['UploadId'], 'uploading', int(time.time())))
        return {'session_id': session_id, 'part_size': PART_BYTES,
                'part_count': (size + PART_BYTES - 1) // PART_BYTES}


def sign_part(s3, username, session_id, part_number):
    with DB_LOCK, open_db() as con:
        row = get_session(con, session_id, username)
        if row['status'] != 'uploading':
            raise ClientError(409, 'Upload is not accepting parts')
        count = (row['size'] + PART_BYTES - 1) // PART_BYTES
        if type(part_number) is not int or not (1 <= part_number <= count):
            raise ClientError(400, 'Invalid part number')
        signed = s3.generate_presigned_url('upload_part', Params={
            'Bucket': R2_BUCKET, 'Key': row['r2_key'], 'UploadId': row['upload_id'],
            'PartNumber': part_number}, ExpiresIn=900, HttpMethod='PUT')
        # Never leak an object URL to a client not authorized to write this repo.
        parsed = urlparse(signed)
        if parsed.scheme != 'https' or parsed.hostname != f'{R2_ACCOUNT_ID}.r2.cloudflarestorage.com':
            raise RuntimeError('Unexpected presigned upload destination')
        return {'url': signed, 'expires_in': 900}


def list_all_parts(s3, row):
    parts = []
    cursor = 0
    while True:
        result = s3.list_parts(Bucket=R2_BUCKET, Key=row['r2_key'], UploadId=row['upload_id'],
                               PartNumberMarker=cursor)
        pages = result.get('Parts', [])
        parts.extend(pages)
        if not result.get('IsTruncated'):
            break
        if not pages or pages[-1]['PartNumber'] <= cursor:
            raise RuntimeError('Invalid multipart listing pagination')
        cursor = pages[-1]['PartNumber']
    count = (row['size'] + PART_BYTES - 1) // PART_BYTES
    if len(parts) != count or sum(p['Size'] for p in parts) != row['size']:
        raise ClientError(409, 'Some upload parts are missing or the size does not match')
    for index, p in enumerate(parts):
        expected = min(PART_BYTES, row['size'] - index * PART_BYTES)
        if p['PartNumber'] != index + 1 or p['Size'] != expected:
            raise ClientError(409, 'A multipart part is incorrect')
    return [{'PartNumber': p['PartNumber'], 'ETag': p['ETag']} for p in parts]


def cold_path(oid):
    return COLD_DIR / oid[:2] / oid[2:4] / oid


def verify_into_cold_storage(s3, row):
    final = cold_path(row['oid'])
    final.parent.mkdir(parents=True, exist_ok=True)
    # Verify existing backups before trusting them. Never overwrite an existing
    # correct copy on retries; it may be referenced by other uploaded revisions.
    if final.exists():
        hashval = hashlib.sha256()
        with final.open('rb') as source:
            for block in iter(lambda: source.read(1024 * 1024), b''):
                hashval.update(block)
        if final.stat().st_size == row['size'] and hashval.hexdigest() == row['oid']:
            return final
        raise ClientError(500, 'Existing cold-storage object has the wrong checksum; investigate before continuing')
    temp = final.with_name(final.name + '.' + secrets.token_hex(8) + '.part')
    obj = s3.get_object(Bucket=R2_BUCKET, Key=row['r2_key'])
    digest = hashlib.sha256()
    size = 0
    try:
        with temp.open('xb') as out:
            body = obj['Body']
            try:
                for block in iter(lambda: body.read(1024 * 1024), b''):
                    out.write(block)
                    digest.update(block)
                    size += len(block)
                    if size > row['size']:
                        raise ClientError(422, 'Uploaded object exceeds declared size')
                out.flush()
                os.fsync(out.fileno())
            finally:
                body.close()
        if size != row['size'] or digest.hexdigest() != row['oid']:
            raise ClientError(422, 'Uploaded file failed SHA-256/size verification')
        os.replace(temp, final)
        return final
    finally:
        temp.unlink(missing_ok=True)


def register_with_gitea(path, row):
    """Use Gitea's supported LFS PUT, never write its SQLite metadata ourselves.

    The staged object is already in local cold storage; Gitea validates the
    content and stores it at the canonical R2 LFS key. This implies one extra
    VM-to-R2 transfer, not a client upload through Cloudflare Tunnel.
    """
    if not ADMIN_USER:
        raise RuntimeError('Missing internal admin identity')
    url = f'{GITEA_URL}/{ORG}/{REPO}.git/info/lfs/objects/{row["oid"]}/{row["size"]}'
    with path.open('rb') as source:
        response = requests.put(url, data=source,
                                headers={'Content-Type': 'application/octet-stream',
                                         'Content-Length': str(row['size']),
                                         'Accept': 'application/vnd.git-lfs+json'},
                                auth=(ADMIN_USER, admin_token()), timeout=(20, 3600),
                                allow_redirects=False)
    if response.status_code not in (200, 201, 204):
        raise RuntimeError(f'Gitea LFS registration failed (HTTP {response.status_code})')
    # The native batch must now find this OID and return a signed direct GET.
    body = {'operation': 'download', 'transfers': ['basic'],
            'objects': [{'oid': row['oid'], 'size': row['size']}]}
    batch = requests.post(f'{GITEA_URL}/{ORG}/{REPO}.git/info/lfs/objects/batch',
                          json=body, headers={'Content-Type': 'application/vnd.git-lfs+json',
                                              'Accept': 'application/vnd.git-lfs+json'},
                          auth=(ADMIN_USER, admin_token()), timeout=30, allow_redirects=False)
    if batch.status_code != 200:
        raise RuntimeError('Gitea LFS validation batch failed')
    actions = batch.json()['objects'][0].get('actions', {})
    download = actions.get('download', {}).get('href', '')
    parsed = urlparse(download)
    if parsed.scheme != 'https' or parsed.hostname != f'{R2_ACCOUNT_ID}.r2.cloudflarestorage.com':
        raise RuntimeError('Gitea has not returned a signed direct-R2 download URL')


def _finalize_worker(s3, session_id):
    """Runs outside the Cloudflare proxy's HTTP response timeout."""
    with FINALIZE_LIMIT:
        try:
            with DB_LOCK, open_db() as con:
                row = con.execute('SELECT * FROM uploads WHERE id=?', (session_id,)).fetchone()
                if row is None or row['status'] != 'finalizing':
                    return
            # A previous finalize may have completed the multipart request before
            # an interruption. HEAD first so retries do not complete it twice.
            exists = False
            try:
                obj = s3.head_object(Bucket=R2_BUCKET, Key=row['r2_key'])
                exists = obj['ContentLength'] == row['size']
            except Exception:
                pass
            if not exists:
                parts = list_all_parts(s3, row)
                s3.complete_multipart_upload(Bucket=R2_BUCKET, Key=row['r2_key'],
                    UploadId=row['upload_id'], MultipartUpload={'Parts': parts})
            path = verify_into_cold_storage(s3, row)
            register_with_gitea(path, row)
            with DB_LOCK, open_db() as con:
                con.execute("UPDATE uploads SET status='done', error='' WHERE id=?", (session_id,))
            try:
                s3.delete_object(Bucket=R2_BUCKET, Key=row['r2_key'])
            except Exception:
                LOG.warning('Staging cleanup deferred for session %s', session_id)
        except Exception as ex:
            LOG.exception('Failed to finalize transfer %s', session_id)
            # Never expose secrets or underlying boto3 response data to clients.
            message = str(ex) if isinstance(ex, ClientError) else 'Finalization failed; contact administrator'
            with DB_LOCK, open_db() as con:
                con.execute('UPDATE uploads SET status=?,error=? WHERE id=?', ('failed', message, session_id))


def finish_upload(s3, username, session_id):
    with DB_LOCK, open_db() as con:
        row = get_session(con, session_id, username)
        if row['status'] == 'done':
            return {'status': 'done', 'oid': row['oid']}
        if row['status'] == 'finalizing':
            return {'status': 'finalizing', 'oid': row['oid']}
        if row['status'] not in ('uploading', 'failed'):
            raise ClientError(409, 'Invalid upload state')
        con.execute("UPDATE uploads SET status='finalizing',error='' WHERE id=?", (session_id,))
    thread = threading.Thread(target=_finalize_worker, args=(s3, session_id), daemon=True)
    thread.start()
    return {'status': 'finalizing', 'oid': row['oid']}


def upload_status(username, session_id):
    with DB_LOCK, open_db() as con:
        row = get_session(con, session_id, username)
        return {'status': row['status'], 'oid': row['oid'], 'error': row['error']}


def sign_named_browser_download(s3, key, oid, filename):
    # The caller already verified that Gitea authorized this exact LFS OID.
    if (not isinstance(oid, str) or not OID_RE.fullmatch(oid) or
        not isinstance(key, str) or not key.startswith('lfs/') or
        not key.endswith('/' + oid) or
        any(x in ('', '.', '..') for x in key.split('/'))):
        raise ClientError(400, 'Invalid LFS object key')
    if (not isinstance(filename, str) or not filename or len(filename) > 220 or
        any(ord(c) < 32 or ord(c) == 127 for c in filename) or
        '/' in filename or '\\' in filename or filename in ('.', '..')):
        raise ClientError(400, 'Invalid download filename')
    ascii_name = re.sub(r'[^A-Za-z0-9._() -]', '_', filename).replace('"', '_')
    disposition = ('attachment; filename="' + ascii_name +
                   '"; filename*=UTF-8\'\'' + urllib.parse.quote(filename, safe=''))
    signed = s3.generate_presigned_url('get_object', Params={
        'Bucket': R2_BUCKET, 'Key': key,
        'ResponseContentDisposition': disposition,
        'ResponseContentType': 'application/octet-stream',
    }, ExpiresIn=900, HttpMethod='GET')
    parsed = urlparse(signed)
    if (parsed.scheme != 'https' or
        parsed.hostname != f'{R2_ACCOUNT_ID}.r2.cloudflarestorage.com'):
        raise RuntimeError('Unexpected signed R2 download host')
    return {'url': signed}


class Handler(BaseHTTPRequestHandler):
    server_version = 'SwVaultR2Transfer/1.4'

    def respond(self, status, obj):
        data = json.dumps(obj).encode('utf-8')
        self.send_response(status)
        self.send_header('Content-Type', 'application/json')
        self.send_header('Cache-Control', 'no-store')
        self.send_header('Content-Length', str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        parsed = urlparse(self.path)
        if parsed.path == '/swvault-transfer/health':
            self.respond(200, {'ok': True, 'version': '1.4', 'direct_r2_uploads': True})
        elif parsed.path == '/swvault-transfer/migration-ready':
            marker = DATA_DIR / 'migration-ready.json'
            if not marker.is_file():
                self.respond(404, {'ready': False})
            else:
                try:
                    value = json.loads(marker.read_text(encoding='utf-8'))
                    if value.get('ready') is True:
                        self.respond(200, value)
                    else:
                        self.respond(404, {'ready': False})
                except (ValueError, OSError):
                    self.respond(503, {'ready': False})
        elif parsed.path == '/swvault-transfer/status':
            try:
                username = check_user_authorization(self.headers.get('Authorization'))
                session_id = parse_qs(parsed.query).get('session_id', [''])[0]
                self.respond(200, upload_status(username, session_id))
            except ClientError as ex:
                self.respond(ex.status, {'error': str(ex)})
            except Exception:
                LOG.exception('Transfer status failed')
                self.respond(503, {'error': 'Transfer status unavailable'})
        else:
            self.respond(404, {'error': 'Not found'})

    def do_POST(self):
        try:
            if self.path == '/internal/browser-download':
                # Deliberately NOT routed by Caddy. Only the local invites
                # process can call this path using the shared internal token.
                provided = self.headers.get('X-SwVault-Internal-Token', '')
                expected = admin_token()
                if not expected or not hmac.compare_digest(provided, expected):
                    raise ClientError(403, 'Forbidden')
                try:
                    length = int(self.headers.get('Content-Length', '-1'))
                except ValueError:
                    length = -1
                if not 0 <= length <= REQUEST_LIMIT:
                    raise ClientError(413, 'Invalid request size')
                data = json.loads(self.rfile.read(length))
                if not isinstance(data, dict):
                    raise ClientError(400, 'Expected JSON object')
                self.respond(200, sign_named_browser_download(
                    self.server.s3, data.get('key'), data.get('oid'), data.get('filename')))
                return
            if self.path not in ('/swvault-transfer/init', '/swvault-transfer/part',
                                 '/swvault-transfer/complete'):
                raise ClientError(404, 'Not found')
            user = check_user_authorization(self.headers.get('Authorization'))
            try:
                length = int(self.headers.get('Content-Length', '-1'))
            except ValueError:
                length = -1
            if not 0 <= length <= REQUEST_LIMIT:
                raise ClientError(413, 'Invalid control request size')
            data = json.loads(self.rfile.read(length))
            if not isinstance(data, dict):
                raise ClientError(400, 'Expected JSON object')
            if self.path.endswith('/init'):
                result = begin_upload(self.server.s3, user, data.get('oid'), data.get('size'))
            elif self.path.endswith('/part'):
                result = sign_part(self.server.s3, user, data.get('session_id'), data.get('part_number'))
            else:
                result = finish_upload(self.server.s3, user, data.get('session_id'))
            self.respond(202 if self.path.endswith('/complete') else 200, result)
        except ClientError as ex:
            self.respond(ex.status, {'error': str(ex)})
        except (json.JSONDecodeError, TypeError, KeyError):
            self.respond(400, {'error': 'Malformed request'})
        except Exception:
            LOG.exception('R2 transfer request failed; no secrets written to client')
            self.respond(503, {'error': 'Transfer service unavailable; retry after checking server logs'})

    def log_message(self, format_string, *args):
        # Do not log part URLs, query strings, credentials, or signed links.
        LOG.info('%s HTTP request completed', self.client_address[0])


def main():
    required = [R2_BUCKET, R2_ACCOUNT_ID, ORG, REPO, ADMIN_USER,
                os.getenv('R2_ACCESS_KEY_ID'), os.getenv('R2_SECRET_ACCESS_KEY')]
    if any(not v for v in required):
        raise SystemExit('Missing required R2/Gitea environment variables')
    logging.basicConfig(level=logging.INFO, format='%(asctime)s %(levelname)s %(message)s')
    COLD_DIR.mkdir(parents=True, exist_ok=True)
    with open_db() as con:
        # A restart cannot safely resume an in-memory finalization thread.
        # Keep the complete stage object; the client can start a new check-in.
        con.execute("UPDATE uploads SET status='failed', error='Coordinator restarted; retry check-in' WHERE status='finalizing'")
    server = ThreadingHTTPServer(('127.0.0.1', 3200), Handler)
    server.s3 = create_s3()
    LOG.info('SWVAULT 1.4 transfer coordinator running on loopback')
    server.serve_forever(poll_interval=0.5)


if __name__ == '__main__':
    main()
