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
                m._direct_r2_action(self.session, oid, 100, "test.SLDASM")

    def test_named_signer_failure_keeps_authorized_direct_r2_download(self):
        import tempfile
        oid = 'a' * 64
        host = 'b' * 32 + '.r2.cloudflarestorage.com'
        gitea_url = f'https://{host}/private-bucket/lfs/{oid[:2]}/{oid[2:4]}/{oid[4:]}?X-Amz-Signature=already-authorized'
        batch = {'objects': [{'oid': oid, 'actions': {'download': {'href': gitea_url}}}]}
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / 'admin-token'; path.write_text('secret')
            with patch.dict(m.os.environ, {'R2_ACCOUNT_ID': 'b' * 32,
                         'R2_BUCKET': 'private-bucket', 'ADMIN_TOKEN_FILE': str(path)}), \
                 patch.object(m, '_lfs', return_value=(200, batch)), \
                 patch.object(m, 'ipv4_local_urlopen', side_effect=OSError('signer unavailable')):
                result = m._direct_r2_action(self.session, oid, 1315, 'Steering.SLDASM')
        self.assertEqual(result, gitea_url)

    def test_untrusted_r2_url_never_falls_back(self):
        oid = 'a' * 64
        gitea_url = 'https://another.example.com/file?signature=bad'
        batch = {'objects': [{'oid': oid, 'actions': {'download': {'href': gitea_url}}}]}
        with patch.dict(m.os.environ, {'R2_ACCOUNT_ID': 'b' * 32}):
            with patch.object(m, '_lfs', return_value=(200, batch)):
                with self.assertRaises(ApiError):
                    m._direct_r2_action(self.session, oid, 1315, 'Steering.SLDASM')

    def test_gitea_storage_path_must_match_entire_oid(self):
        oid = 'a' * 64
        host = 'b' * 32 + '.r2.cloudflarestorage.com'
        # This URL incorrectly stores all 64 hash characters as the filename.
        # The endpoint must never sign an unexpected key.
        wrong_url = f'https://{host}/private-bucket/lfs/{oid[:2]}/{oid[2:4]}/{oid}?X-Amz-Signature=old'
        batch = {'objects': [{'oid': oid, 'actions': {'download': {'href': wrong_url}}}]}
        with patch.dict(m.os.environ, {'R2_ACCOUNT_ID': 'b' * 32, 'R2_BUCKET': 'private-bucket'}), \
             patch.object(m, '_lfs', return_value=(200, batch)):
            with self.assertRaises(ApiError) as exc:
                m._direct_r2_action(self.session, oid, 1315, 'Steering.SLDASM')
        self.assertEqual(502, exc.exception.status)

    def test_browser_signer_preserves_filename_without_proxying_file_bytes(self):
        from unittest.mock import MagicMock
        import tempfile
        oid = 'a' * 64
        host = 'b' * 32 + '.r2.cloudflarestorage.com'
        gitea_url = f'https://{host}/private-bucket/lfs/{oid[:2]}/{oid[2:4]}/{oid[4:]}?X-Amz-Signature=old'
        final_url = f'https://{host}/private-bucket/lfs/{oid[:2]}/{oid[2:4]}/{oid[4:]}?X-Amz-Signature=new'
        batch = {'objects': [{'oid': oid, 'actions': {'download': {'href': gitea_url}}}]}
        payloads = []
        class FakeResponse:
            def __enter__(self): return self
            def __exit__(self, *_): pass
            def read(self): return json.dumps({'url': final_url}).encode()
        def fake_open(req, timeout):
            payloads.append(json.loads(req.data))
            self.assertEqual('http://127.0.0.1:3200/internal/browser-download', req.full_url)
            self.assertEqual('secret', req.get_header('X-swvault-internal-token'))
            return FakeResponse()
        import json
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder)/'admin-token'; path.write_text('secret')
            with patch.dict(m.os.environ, {'R2_ACCOUNT_ID':'b'*32, 'R2_BUCKET':'private-bucket', 'ADMIN_TOKEN_FILE':str(path)}), \
                 patch.object(m, '_lfs', return_value=(200, batch)), \
                 patch.object(m, 'ipv4_local_urlopen', side_effect=fake_open):
                result = m._direct_r2_action(self.session, oid, 1315, 'Brakes and Pedals.SLDASM')
        self.assertEqual(final_url, result)
        self.assertEqual('Brakes and Pedals.SLDASM', payloads[0]['filename'])
        self.assertEqual(oid, payloads[0]['oid'])

class IPv4NetworkTests(unittest.TestCase):
    def test_refuses_external_destinations_and_ipv6_localhost(self):
        for address in ('http://localhost:3000/api/v1/user',
                        'http://[::1]:3000/api/v1/user',
                        'https://127.0.0.1:3000/api/v1/user',
                        'http://127.0.0.1:9000/api/v1/user',
                        'http://example.com:3000/api/v1/user'):
            with self.subTest(address=address):
                with self.assertRaises(ValueError):
                    m.ipv4_local_urlopen(m.urllib.request.Request(address))

    def test_uses_af_inet_socket_to_contact_loopback(self):
        from unittest.mock import MagicMock
        import socket
        connection = m._IPv4LoopbackConnection('127.0.0.1', 3000, timeout=6)
        fake_sock = MagicMock()
        with patch.object(m.socket, 'socket', return_value=fake_sock) as factory:
            connection.connect()
        factory.assert_called_once_with(socket.AF_INET, socket.SOCK_STREAM)
        fake_sock.connect.assert_called_once_with(('127.0.0.1', 3000))
        self.assertIs(connection.sock, fake_sock)

    def test_ipv4_local_opener_never_uses_environment_proxy(self):
        with patch.dict(m.os.environ, {'HTTP_PROXY':'http://example.com:9999'}), \
             patch.object(m._LOCAL_OPENER, 'open', side_effect=OSError('sent direct')) as open_call:
            with self.assertRaises(OSError):
                m.ipv4_local_urlopen('http://127.0.0.1:3000/api/v1/user')
        open_call.assert_called_once()
