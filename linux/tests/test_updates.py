import io
import json
import unittest
from unittest.mock import patch
try:
    from momentum_control import updates
except ImportError:
    updates = None


class Reply(io.BytesIO):
    status = 200
    def __init__(self, data):
        super().__init__(data)
        self.headers = {}


class UpdateTests(unittest.TestCase):
    def setUp(self):
        self.assertIsNotNone(updates, "Release checker implementation is required")

    def run_check(self, document):
        reply = Reply(document)
        with patch.object(updates, '_open', return_value=reply) as transport:
            result = updates._fetch_release()
        self.assertEqual(transport.call_args.args[0].full_url, 'https://api.github.com/repos/Ariazonaa/momentum4-control/releases/latest')
        self.assertLessEqual(transport.call_args.kwargs['timeout'], 3)
        return result

    def test_release_uses_fixed_public_tag_link(self):
        release = self.run_check(json.dumps({'tag_name': 'v1.4.2', 'html_url': 'https://github.com/Ariazonaa/momentum4-control/releases/tag/v1.4.2', 'assets': [{'browser_download_url': 'https://evil.invalid'}]}).encode())
        self.assertEqual(release.tag, 'v1.4.2')
        self.assertEqual(release.url, 'https://github.com/Ariazonaa/momentum4-control/releases/tag/v1.4.2')

    def test_bad_tag_url_json_and_oversized_replies_fail(self):
        for value in (b'[]', b'{', b'{"tag_name":1}', b'{"tag_name":"../secret"}', b'{"tag_name":"v1", "html_url":"https://evil.invalid"}', b'{"tag_name":"v1", "tag_name":"v2"}', json.dumps({'tag_name': 'v1', 'padding': 'x' * 262144}).encode()):
            with self.subTest(value=value[:80]), self.assertRaises(updates.ReleaseError):
                self.run_check(value)

    def test_cancelled_never_opens_transport(self):
        with patch.object(updates, '_open', side_effect=AssertionError('transport accessed')), self.assertRaises(updates.ReleaseError):
            updates._fetch_release(lambda: True)

    def test_redirect_refused(self):
        with self.assertRaises(updates.ReleaseError):
            updates.NoRedirect().redirect_request(None, None, 302, '', {}, 'https://evil.invalid')

    def test_deadline_checked_between_reads(self):
        with patch.object(updates, '_open', return_value=Reply(b'{"tag_name":"v1"}')), patch.object(updates.time, 'monotonic', side_effect=[0, 1] + [11] * 8), self.assertRaises(updates.ReleaseError):
            updates._fetch_release()

    def test_non_success_status_is_rejected(self):
        reply = Reply(b'{"tag_name":"v1"}')
        reply.status = 201
        with patch.object(updates, '_open', return_value=reply), self.assertRaises(updates.ReleaseError):
            updates._fetch_release()
