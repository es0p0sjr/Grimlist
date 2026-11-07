"""Serial, identifiable HTTP with robots checks, validators and bounded retries.

No credentials, cookies, alternate proxies, challenge solvers or redirect following.
Access denial blocks the host for the rest of the run. Offline/dry-run never call it.
"""
import email.utils
import time
import urllib.error
import urllib.parse
import urllib.request
import urllib.robotparser
from .models import VERSION


class AcquisitionError(RuntimeError):
    def __init__(self, message, result, uri='', status=None):
        super().__init__(message)
        self.result, self.uri, self.status = result, uri, status


class AccessDenied(AcquisitionError):
    def __init__(self, message, uri='', status=None, result='AccessDenied'):
        super().__init__(message, result, uri, status)


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


class Fetcher:
    user_agent = 'GrimListReferenceCollector/' + VERSION

    def __init__(self, store, delay=3.0, timeout=30, retries=2, transport=None, sleep=time.sleep):
        self.store, self.delay, self.timeout, self.retries = store, max(1.0, delay), timeout, min(3, max(0, retries))
        self.transport = transport or self._transport
        self.sleep = sleep
        self.last_request = 0.0
        self.robots, self.blocked, self.responses = {}, {}, {}

    def _transport(self, uri, headers):
        request = urllib.request.Request(uri, headers=headers)
        opener = urllib.request.build_opener(NoRedirect())
        try:
            response = opener.open(request, timeout=self.timeout)
        except urllib.error.HTTPError as error:
            response = error
        with response:
            if int(response.headers.get('Content-Length', '0')) > 16_000_000:
                raise ValueError('Source exceeds 16 MB limit')
            raw = response.read(16_000_001)
            if len(raw) > 16_000_000: raise ValueError('Source exceeds 16 MB limit')
            return response.code, dict(response.headers), raw

    def request(self, uri, headers=None):
        parsed = urllib.parse.urlparse(uri)
        if parsed.scheme != 'https' or parsed.username or parsed.password:
            raise AccessDenied('Only public HTTPS source URIs without credentials are supported')
        host = parsed.netloc.lower()
        if host in self.blocked: raise self.blocked[host]
        headers = {'User-Agent': self.user_agent, 'Accept': 'text/html, text/plain, application/xml', **(headers or {})}
        for attempt in range(self.retries + 1):
            self.sleep(max(0, self.delay - (time.monotonic() - self.last_request)))
            self.last_request = time.monotonic()
            try:
                status, response_headers, raw = self.transport(uri, headers)
            except (OSError, urllib.error.URLError) as error:
                if attempt == self.retries:
                    raise AcquisitionError(str(error), 'NetworkUnavailable', uri) from error
                self.sleep(2 ** (attempt + 1))
                continue
            response_headers = {k.lower(): v for k, v in response_headers.items()}
            self.responses[uri] = status
            if status in (401, 403, 429) or 300 <= status < 400 and status != 304:
                error = AccessDenied(f'HTTP {status}; stop automated acquisition for {host}. Use normally saved source files.',
                                     uri, status, 'RateLimited' if status == 429 else 'AccessDenied')
                self.blocked[host] = error
                raise error
            lower = raw[:200_000].lower()
            if any(marker in lower for marker in (b'cf-chl-', b'g-recaptcha', b'hcaptcha', b'verify you are human', b'access denied')):
                error = AccessDenied('Challenge/access-denial document; stop automated acquisition', uri, status)
                self.blocked[host] = error
                raise error
            if status in (500, 502, 503, 504) and attempt < self.retries:
                retry = response_headers.get('retry-after', '')
                if retry.isdigit(): wait = float(retry)
                elif retry:
                    try: wait = max(0, email.utils.parsedate_to_datetime(retry).timestamp() - time.time())
                    except (ValueError, TypeError): wait = 2 ** (attempt + 1)
                else: wait = 2 ** (attempt + 1)
                if wait > 60: raise RuntimeError(f'Source requests {wait:.0f}s backoff; defer to a later run')
                self.sleep(max(self.delay, wait))
                continue
            return status, response_headers, raw
        raise RuntimeError('Request retries exhausted')

    def allowed(self, uri, edition):
        parsed = urllib.parse.urlparse(uri)
        origin = f'{parsed.scheme}://{parsed.netloc}'
        if origin not in self.robots:
            status, headers, raw = self.request(origin + '/robots.txt')
            if status != 200:
                error = AccessDenied(f'Cannot establish source robots rules (HTTP {status})', origin + '/robots.txt', status)
                self.blocked[parsed.netloc.lower()] = error
                raise error
            self.store.save('http-policy', edition, origin + '/robots.txt', raw, media_type='text/plain')
            policy = urllib.robotparser.RobotFileParser()
            policy.parse(raw.decode('utf-8-sig').splitlines())
            self.robots[origin] = policy
        policy = self.robots[origin]
        if not policy.can_fetch(self.user_agent, uri):
            error = AccessDenied('Source robots rules disallow: ' + uri, uri)
            self.blocked[parsed.netloc.lower()] = error
            raise error
        self.delay = max(self.delay, policy.crawl_delay(self.user_agent) or 0)
        rate = policy.request_rate(self.user_agent)
        if rate and rate.requests: self.delay = max(self.delay, rate.seconds / rate.requests)

    def acquire(self, adapter, edition, uri, faction=''):
        self.allowed(uri, edition)
        cached = self.store.latest(adapter, edition, uri)
        headers = {}
        if cached:
            meta = cached['Metadata']
            if meta['ETag']: headers['If-None-Match'] = meta['ETag']
            if meta['LastModified']: headers['If-Modified-Since'] = meta['LastModified']
        status, headers, raw = self.request(uri, headers)
        if status == 304 and cached:
            self.store.checked(adapter, edition, uri)
            return cached, 'cached'
        if status != 200:
            raise AcquisitionError(f'HTTP {status}: {uri}', 'NotFound' if status == 404 else 'HttpFailure', uri, status)
        snapshot = self.store.save(adapter, edition, uri, raw, faction=faction,
            media_type=headers.get('content-type', 'text/html'), etag=headers.get('etag', ''), modified=headers.get('last-modified', ''))
        return snapshot, 'cached' if cached and cached['Metadata']['ContentHash'] == snapshot['Metadata']['ContentHash'] else 'acquired'
