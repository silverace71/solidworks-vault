import hashlib
import importlib.util
import io
import json
from pathlib import Path
import tempfile
import time
import unittest
from unittest.mock import patch

SRC = Path(__file__).resolve().parents[2] / 'server/linux/r2-transfer/service.py'
spec = importlib.util.spec_from_file_location('swvault_r2_service', SRC)
m = importlib.util.module_from_spec(spec)
spec.loader.exec_module(m)


class FakeS3:
    def __init__(self):
        self.staged = {}
        self.current = {}
        self.started = 0

    def create_multipart_upload(self, Bucket, Key, ContentType):
        self.started += 1
        self.current[Key] = {}
        return {'UploadId': 'opaque-' + str(self.started)}

    def generate_presigned_url(self, method, Params, ExpiresIn, HttpMethod):
        assert method == 'upload_part' and HttpMethod == 'PUT'
        return f'https://{m.R2_ACCOUNT_ID}.r2.cloudflarestorage.com/bucket/stage?partNumber={Params["PartNumber"]}'

    def list_parts(self, Bucket, Key, UploadId, PartNumberMarker):
        parts = self.current[Key]
        return {'IsTruncated': False, 'Parts': [
            {'PartNumber': n, 'Size': len(content), 'ETag': '"dummy"'}
            for n, content in sorted(parts.items())]}

    def complete_multipart_upload(self, Bucket, Key, UploadId, MultipartUpload):
        self.staged[Key] = b''.join(p for _, p in sorted(self.current[Key].items()))
        return {}

    def head_object(self, Bucket, Key):
        if Key not in self.staged:
            raise KeyError(Key)
        return {'ContentLength': len(self.staged[Key])}

    def get_object(self, Bucket, Key):
        return {'Body': io.BytesIO(self.staged[Key])}

    def delete_object(self, Bucket, Key):
        self.staged.pop(Key, None)


class TransferTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        root = Path(self.tmp.name)
        m.DATA_DIR = root / 'state'
        m.COLD_DIR = root / 'cold'
        m.R2_BUCKET = 'private-bucket'
        m.R2_ACCOUNT_ID = 'a' * 32
        m.ORG = 'team'
        m.REPO = 'cad'
        self.s3 = FakeS3()

    def tearDown(self):
        self.tmp.cleanup()

    def test_invalid_oids_and_oversized_objects_are_rejected(self):
        with self.assertRaises(m.ClientError):
            m.begin_upload(self.s3, 'alice', '../garbage', 123)
        with self.assertRaises(m.ClientError):
            m.begin_upload(self.s3, 'alice', 'f' * 64, m.MAX_FILE_BYTES + 1)
        self.assertEqual(0, self.s3.started)

    def test_user_cannot_sign_another_users_part(self):
        oid = '1' * 64
        started = m.begin_upload(self.s3, 'alice', oid, 42)
        with self.assertRaises(m.ClientError):
            m.sign_part(self.s3, 'bob', started['session_id'], 1)
        signed = m.sign_part(self.s3, 'alice', started['session_id'], 1)
        self.assertIn('.r2.cloudflarestorage.com', signed['url'])
        with self.assertRaises(m.ClientError):
            m.sign_part(self.s3, 'alice', started['session_id'], 2)

    def test_multipart_verified_cold_stored_registered_and_cleaned(self):
        content = b'a very important CAD file\x00' * 100
        oid = hashlib.sha256(content).hexdigest()
        begun = m.begin_upload(self.s3, 'alice', oid, len(content))
        session = begun['session_id']
        key = f'stage/{session}/{oid}'
        self.s3.current[key][1] = content
        self.assertEqual('finalizing', m.finish_upload(self.s3, 'alice', session)['status'])
        # Native Gitea registration is mocked, so this test checks coordinator
        # state/hash/cold-storage boundaries without claiming live interoperability.
        with patch.object(m, 'register_with_gitea', return_value=None) as register:
            for _ in range(100):
                if m.upload_status('alice', session)['status'] != 'finalizing':
                    break
                time.sleep(0.02)
            self.assertEqual('done', m.upload_status('alice', session)['status'])
            register.assert_called_once()
        self.assertEqual(content, m.cold_path(oid).read_bytes())
        self.assertNotIn(key, self.s3.staged)
        self.assertEqual('done', m.finish_upload(self.s3, 'alice', session)['status'])

    def test_gitea_native_registration_uses_oid_and_byte_size(self):
        from unittest.mock import MagicMock
        import requests
        content = b'cad piece data'
        oid = hashlib.sha256(content).hexdigest()
        entry = {'oid': oid, 'size': len(content)}
        fake_put = MagicMock(status_code=200)
        fake_batch = MagicMock(status_code=200)
        fake_batch.json.return_value = {'objects': [{'actions': {'download': {
            'href': f'https://{m.R2_ACCOUNT_ID}.r2.cloudflarestorage.com/private/lfs/file?X-Amz-Signature=example'
        }}}]}
        m.ORG = 'team'; m.REPO = 'cad'; m.ADMIN_USER = 'vault-admin'
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / 'source.dat'; path.write_bytes(content)
            with patch.object(m, 'admin_token', return_value='not-a-real-secret'):
                with patch.object(requests, 'put', return_value=fake_put) as put:
                    with patch.object(requests, 'post', return_value=fake_batch):
                        m.register_with_gitea(path, entry)
                        url = put.call_args.args[0]
                        self.assertTrue(url.endswith(f'/team/cad.git/info/lfs/objects/{oid}/{len(content)}'))
                        self.assertEqual(('vault-admin', 'not-a-real-secret'), put.call_args.kwargs['auth'])

    def test_corrupt_remote_data_is_never_registered(self):
        oid = hashlib.sha256(b'correct').hexdigest()
        begun = m.begin_upload(self.s3, 'alice', oid, 7)
        key = f'stage/{begun["session_id"]}/{oid}'
        self.s3.current[key][1] = b'garbage'
        with patch.object(m, 'register_with_gitea') as register:
            m.finish_upload(self.s3, 'alice', begun['session_id'])
            for _ in range(100):
                if m.upload_status('alice', begun['session_id'])['status'] != 'finalizing':
                    break
                time.sleep(0.02)
            self.assertEqual('failed', m.upload_status('alice', begun['session_id'])['status'])
            register.assert_not_called()
        self.assertFalse(m.cold_path(oid).exists())

    def test_named_r2_download_is_signed_with_content_disposition(self):
        from unittest.mock import MagicMock
        oid = 'a' * 64
        s3 = MagicMock()
        s3.generate_presigned_url.return_value = (
            f'https://{m.R2_ACCOUNT_ID}.r2.cloudflarestorage.com/private-bucket/lfs/aa/{oid}?X-Amz-Signature=fake')
        result = m.sign_named_browser_download(s3, f'lfs/aa/{oid}', oid, 'Brakes and Pedals.SLDASM')
        self.assertEqual('https', result['url'].split(':')[0])
        kwargs = s3.generate_presigned_url.call_args.kwargs
        self.assertEqual('get_object', s3.generate_presigned_url.call_args.args[0])
        self.assertIn('filename="Brakes and Pedals.SLDASM"', kwargs['Params']['ResponseContentDisposition'])
        self.assertEqual(900, kwargs['ExpiresIn'])
        with self.assertRaises(m.ClientError):
            m.sign_named_browser_download(s3, f'lfs/aa/{oid}', oid, '../invalid.SLDASM')


if __name__ == '__main__':
    unittest.main()
