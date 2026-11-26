import importlib.util
import io
from pathlib import Path
import unittest
from unittest.mock import patch

SRC = Path(__file__).resolve().parents[2] / 'server/linux/invites/browser.py'
spec = importlib.util.spec_from_file_location('swvault_browser_test', SRC)
m = importlib.util.module_from_spec(spec)
spec.loader.exec_module(m)

class ApiError(Exception):
    def __init__(self, status, text):
        super().__init__(text)
        self.status = status

class FakeTeam:
    ORG = 'team'
    REPO = 'cad'
    GITEA = 'http://localhost:3000'
    ApiError = ApiError
    def log(self, message):
        pass

class Handler:
    def __init__(self):
        self.headers = []
        self.status = None
        self.wfile = io.BytesIO()
    def send_response(self, status):
        self.status = status
    def send_header(self, name, value):
        self.headers.append((name, value))
    def end_headers(self):
        pass

class DownloadTests(unittest.TestCase):
    def setUp(self):
        m.T = FakeTeam()
        self.handler = Handler()
        self.session = {'auth': 'Basic dGVzdA==', 'login': 'alice'}
    def test_lfs_web_download_redirects_to_private_r2(self):
        oid = 'a' * 64
        ptr = f'version https://git-lfs.github.com/spec/v1\noid sha256:{oid}\nsize 1048576000\n'.encode()
        target = 'https://' + 'b'*32 + '.r2.cloudflarestorage.com/private/lfs/file?X-Amz-Signature=short-lived'
        with patch.object(m, '_stored_blob_preview', return_value=ptr), \
             patch.object(m, '_direct_r2_action', return_value=target):
            m.send_download(self.handler, self.session, 'part.SLDPRT', None, None)
        self.assertEqual(302, self.handler.status)
        self.assertIn(('Location', target), self.handler.headers)
        self.assertEqual(b'', self.handler.wfile.getvalue())
    def test_non_lfs_large_web_download_is_blocked(self):
        with patch.object(m, '_stored_blob_preview', return_value=b'X' * 4097):
            with self.assertRaises(ApiError) as error:
                m.send_download(self.handler, self.session, 'not-lfs-big.bin', None, None)
        self.assertEqual(409, error.exception.status)
    def test_signed_r2_url_must_match_our_account(self):
        oid = 'a' * 64
        wrong_url = 'https://evil.example.net/private/file?X-Amz-Signature=test'
        batch = {'objects': [{'oid': oid, 'actions': {'download': {'href': wrong_url}}}]}
        with patch.dict(m.os.environ, {'R2_ACCOUNT_ID': 'b' * 32}), patch.object(m, '_lfs', return_value=(200, batch)):
            with self.assertRaises(ApiError):
                m._direct_r2_action(self.session, oid, 100)
