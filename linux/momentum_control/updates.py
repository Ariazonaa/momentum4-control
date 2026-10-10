from dataclasses import dataclass
import json
import os
from pathlib import Path
import selectors
import subprocess
import sys
import re
import time
from urllib.error import HTTPError, URLError
from urllib.request import HTTPRedirectHandler, ProxyHandler, Request, build_opener


ENDPOINT = 'https://api.github.com/repos/Ariazonaa/momentum4-control/releases/latest'
TAG_ROOT = 'https://github.com/Ariazonaa/momentum4-control/releases/tag/'
MAX_BYTES = 256 * 1024


class ReleaseError(ValueError):
    pass


@dataclass(frozen=True)
class ProjectRelease:
    tag: str
    url: str


class NoRedirect(HTTPRedirectHandler):
    def redirect_request(self, request, response, code, message, headers, url):
        raise ReleaseError('Project release redirects are refused')


def _open(request, timeout):
    return build_opener(ProxyHandler({}), NoRedirect()).open(request, timeout=timeout)


def _object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ReleaseError('Invalid project release JSON')
        result[key] = value
    return result


def _fetch_release(cancelled=None) -> ProjectRelease:
    deadline = time.monotonic() + 10
    def remaining():
        budget = deadline - time.monotonic()
        if cancelled is not None and cancelled():
            raise ReleaseError('Project release check cancelled')
        if budget <= 0:
            raise ReleaseError('Project release check timed out')
        return min(3, budget)
    request = Request(ENDPOINT, headers={'Accept': 'application/vnd.github+json', 'User-Agent': 'Momentum-Control-Linux'})
    try:
        with _open(request, timeout=remaining()) as response:
            if response.status != 200:
                raise ReleaseError('Project release service unavailable')
            chunks = []
            size = 0
            while True:
                budget = remaining()
                socket = getattr(getattr(getattr(response, 'fp', None), 'raw', None), '_sock', None)
                if socket is not None:
                    socket.settimeout(budget)
                reader = getattr(response, 'read1', response.read)
                chunk = reader(min(16384, MAX_BYTES + 1 - size))
                remaining()
                if not chunk:
                    break
                size += len(chunk)
                if size > MAX_BYTES:
                    raise ReleaseError('Project release response exceeds size limit')
                chunks.append(chunk)
        document = json.loads(b''.join(chunks), object_pairs_hook=_object,
            parse_constant=lambda value: (_ for _ in ()).throw(ReleaseError('Invalid project release JSON')))
        if type(document) is not dict:
            raise ReleaseError('Invalid project release JSON')
        tag = document.get('tag_name')
        if type(tag) is not str or re.fullmatch(r'[A-Za-z0-9_.-]{1,64}', tag, re.ASCII) is None:
            raise ReleaseError('Invalid project release tag')
        url = TAG_ROOT + tag
        if 'html_url' in document and document['html_url'] != url:
            raise ReleaseError('Invalid project release URL')
        return ProjectRelease(tag, url)
    except ReleaseError:
        raise
    except (HTTPError, URLError, OSError, ValueError, RecursionError) as error:
        raise ReleaseError('Project release check failed') from error


def _release_result(data):
    document = json.loads(data, object_pairs_hook=_object)
    if type(document) is not dict or set(document) != {'tag', 'url'}:
        raise ReleaseError('Invalid project release result')
    tag = document['tag']
    if type(tag) is not str or re.fullmatch(r'[A-Za-z0-9_.-]{1,64}', tag, re.ASCII) is None:
        raise ReleaseError('Invalid project release tag')
    if document['url'] != TAG_ROOT + tag:
        raise ReleaseError('Invalid project release URL')
    return ProjectRelease(tag, document['url'])


def check_project_release(cancelled=None) -> ProjectRelease:
    deadline = time.monotonic() + 10
    process = None
    def remaining():
        if cancelled is not None and cancelled():
            raise ReleaseError('Project release check cancelled')
        budget = deadline - time.monotonic()
        if budget <= 0:
            raise ReleaseError('Project release check timed out')
        return budget
    try:
        remaining()
        process = subprocess.Popen(
            [sys.executable, '-I', '-S', str(Path(__file__).resolve())],
            stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
            close_fds=True, env={}, cwd='/')
        os.set_blocking(process.stdout.fileno(), False)
        data = bytearray()
        eof = False
        with selectors.DefaultSelector() as selector:
            selector.register(process.stdout, selectors.EVENT_READ)
            while True:
                budget = remaining()
                if eof and process.poll() is not None:
                    break
                for key, events in selector.select(min(.05, budget)):
                    chunk = os.read(key.fd, 1025 - len(data))
                    if not chunk:
                        eof = True
                        selector.unregister(process.stdout)
                    data.extend(chunk)
                    if len(data) > 1024:
                        raise ReleaseError('Project release result exceeds size limit')
            remaining()
        if process.returncode != 0:
            raise ReleaseError('Project release check failed')
        return _release_result(data)
    except ReleaseError:
        raise
    except (OSError, ValueError, RecursionError) as error:
        raise ReleaseError('Project release check failed') from error
    finally:
        if process is not None:
            try:
                if process.poll() is None:
                    process.terminate()
                    try:
                        process.wait(timeout=.2)
                    except subprocess.TimeoutExpired:
                        process.kill()
                process.wait()
            finally:
                process.stdout.close()


if __name__ == '__main__':
    try:
        release = _fetch_release()
        print(json.dumps({'tag': release.tag, 'url': release.url}, separators=(',', ':')))
    except Exception:
        print('{"error":"failed"}')
        sys.exit(1)
