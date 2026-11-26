"""
SwVault web file browser: the vault's files in a web browser, for anyone on the team.

Served by the team service (invites.py) at https://<server>/swvault/ (also /swvault-invites/browse).
The page (web/) searches the whole vault as you type, shows thumbnails, everything SwVault records
about a file, its version history and its reviews, downloads any version, and hands the SOLIDWORKS
commands (open, get latest, check out, check in, history, change state) to SwVault on the person's
PC through swvault:// links.

Sign-in is the person's vault account (Gitea). The password is checked against Gitea and kept only
in this process's memory for the session, which lives in an HttpOnly cookie; everything is read
with the person's own access. Python standard library only.

  POST /api/login           {username, password} -> session cookie
  POST /api/logout
  GET  /api/me              {login, fullName, team, vaultUrl}
  GET  /api/index           every file at the head commit with its sidecar, thumbnails, workflow states
  GET  /api/locks           current check-outs
  POST /api/admin/release   {path}: admin releases someone's check-out (their PC is gone)
  POST /api/checkout        {path}: check the file out to the signed-in user, without SwVault on this device
  POST /api/undo-checkout   {path}: let go of the signed-in user's own check-out
  GET  /api/cars            cars and subsystems (folders and responsible engineers)
  GET  /api/leads           subteam leads, for review requests
  GET  /api/history?path=   the file's versions and workflow changes, newest first
  GET  /api/thumb?path=[&ref=]      the preview saved with a version (image/jpeg)
  GET  /api/download?path=[&ref=]   the file's content (a version when ref is given)
  GET  /api/reviews?path=   review requests on the file with their discussion
  POST /api/reviews         {path, kind, lead, message} -> new review request
  POST /api/reviews/<n>/respond   {decision: approve|changes|comment|cancel, comment}

Vault admins only (the Accounts page):
  GET  /api/admin/accounts  every account on the server: name, email, role, team role, last sign-in
  POST /api/admin/accounts/<login>   {action: disable|enable|role|email|reset-password, role?, email?}
  GET  /api/admin/update    automatic updates: {enabled, status, published} (server/linux/auto-update.sh)
  POST /api/admin/update    start one now: GitHub builds main, the server publishes what changed

A team's own logo goes in web/logo.svg (dark background) and web/logo-light.svg (light background);
without them the SwVault icon is shown.

POST requests need the X-SwVault header, so other sites can't send them with the cookie.
"""

import base64
import gzip
import hashlib
import json
import mimetypes
import os
import re
import secrets
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
from collections import OrderedDict
from concurrent.futures import ThreadPoolExecutor
from http import HTTPStatus

T = None  # the team service module (invites.py): gitea(), ApiError, log, throttling, people, cars

WEB_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), "web")
COOKIE = "swvault_session"
SESSION_HOURS = 12
META_PREFIX = ".swvault/meta/"
THUMB_PREFIX = ".swvault/thumbs/"
CONFIG_PATH = ".swvault/vault.json"
LFS_POINTER_RE = re.compile(rb"^version https://git-lfs\.github\.com/spec/v1\noid sha256:([0-9a-f]{64})\nsize (\d+)\n?$")
STATIC = {"/": "index.html", "/browse": "index.html", "/index.html": "index.html",
          "/app.js": "app.js", "/fuzzy.js": "fuzzy.js", "/app.css": "app.css", "/icon.svg": "icon.svg",
          "/logo.svg": "logo.svg", "/logo-light.svg": "logo-light.svg"}
CSP = ("default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' data: blob:; connect-src 'self'; "
       "frame-ancestors 'none'; base-uri 'none'; form-action 'self'")

_sessions = {}        # id -> {auth, login, fullName, expires, head?}
_sessions_lock = threading.Lock()
_sessions_swept = 0.0
_index_lock = threading.Lock()
_index = {"commit": None, "body": None, "gzip": None, "etag": None, "payload": None, "byPath": {}}
_blob_cache = {}      # blob sha -> parsed sidecar (dict), pointer [oid, size], or None
_blob_cache_dirty = False
_raw_cache = OrderedDict()  # (ref, path) -> bytes or None, for immutable commit refs; least recently used first
_raw_cache_bytes = 0
_raw_cache_lock = threading.Lock()
_RAW_CACHE_BYTES = 64 * 1024 ** 2
HEAD_SECONDS = 10     # a page loading many thumbnails asks Gitea for the head commit once, not per image
POINTER_MAX = 1024    # LFS pointers are ~130 bytes; a bigger blob is file content kept in git itself
_static_cache = {}    # file name -> (mtime_ns, body, gzipped, etag)
_labels = {"ids": None, "expires": 0.0}
_review_locks = {}    # (requester, path, kind, lead) -> lock held while that request is being created
_review_recent = {}   # same key -> (monotonic time, review) for requests just created
_review_lock = threading.Lock()
REVIEW_REPEAT_SECONDS = 60  # the same request again this soon is a double click, not a new review


def setup(team):
    global T
    T = team
    _load_blob_cache()


def handles(route):
    return route in STATIC or route.startswith("/api/")


# ------------------------------------------------------------------ sessions

def _cookie_value(handler):
    for part in (handler.headers.get("Cookie") or "").split(";"):
        name, _, value = part.strip().partition("=")
        if name == COOKIE:
            return value
    return None


def current_session(handler):
    global _sessions_swept
    sid = _cookie_value(handler)
    with _sessions_lock:
        now = time.time()
        if now - _sessions_swept > 60:
            _sessions_swept = now
            for key in [k for k, s in _sessions.items() if s["expires"] < now]:
                del _sessions[key]
        session = _sessions.get(sid) if sid else None
        if session and session["expires"] < now:
            del _sessions[sid]
            session = None
    if not session:
        raise T.ApiError(HTTPStatus.UNAUTHORIZED, "Sign in first.")
    return session


def login(handler, body):
    key = handler.throttle_key("signin")
    T.check_throttle(key, "Too many failed sign-ins. Try again in 15 minutes.")
    username = str(body.get("username") or "").strip()
    password = str(body.get("password") or "")
    if not username or not password or len(username) > 200 or len(password) > 500:
        raise T.ApiError(HTTPStatus.BAD_REQUEST, "Enter your vault user name and password.")
    auth = "Basic " + base64.b64encode(f"{username}:{password}".encode()).decode()
    status, user = T.gitea("GET", "user", auth=auth)
    if status != 200 or not user:
        T.record_failure(key)
        raise T.ApiError(HTTPStatus.UNAUTHORIZED, "Wrong user name or password.")
    login_name = user["login"]
    status, _ = T.gitea("GET", f"repos/{T.ORG}/{T.REPO}", auth=auth)
    if status != 200:
        raise T.ApiError(HTTPStatus.FORBIDDEN, f"{login_name} can't see the {T.TEAM_NAME} vault. Ask an admin to add you to the team.")
    sid = secrets.token_urlsafe(32)
    with _sessions_lock:
        _sessions[sid] = {"auth": auth, "login": login_name, "fullName": user.get("full_name") or "",
                          "expires": time.time() + SESSION_HOURS * 3600}
    T.log(f"web browser: {login_name} signed in")
    cookie = f"{COOKIE}={sid}; Path=/; HttpOnly; Secure; SameSite=Strict; Max-Age={SESSION_HOURS * 3600}"
    return {"login": login_name, "fullName": user.get("full_name") or ""}, cookie


def logout(handler):
    sid = _cookie_value(handler)
    with _sessions_lock:
        _sessions.pop(sid, None)
    return f"{COOKIE}=; Path=/; HttpOnly; Secure; SameSite=Strict; Max-Age=0"


# ------------------------------------------------------------------ Gitea access

def _repo_api(path):
    return f"repos/{T.ORG}/{T.REPO}/{path}"


def _quote_path(path):
    return "/".join(urllib.parse.quote(part, safe="") for part in path.split("/"))


def _open(url, auth, accept="*/*"):
    request = urllib.request.Request(url)
    request.add_header("Authorization", auth)
    request.add_header("Accept", accept)
    return urllib.request.urlopen(request, timeout=60)


def raw_file(path, ref, auth):
    """A file's content as stored in git at ref (a commit or branch), or None when it isn't there."""
    global _raw_cache_bytes
    immutable = bool(re.fullmatch(r"[0-9a-f]{40,64}", ref or ""))
    key = (ref, path)
    if immutable:
        with _raw_cache_lock:
            if key in _raw_cache:
                _raw_cache.move_to_end(key)
                return _raw_cache[key]
    url = f"{T.GITEA}/api/v1/{_repo_api('raw/' + _quote_path(path))}?ref={urllib.parse.quote(ref or '')}"
    try:
        with _open(url, auth) as response:
            data = response.read()
    except urllib.error.HTTPError as e:
        if e.code != 404:
            raise T.ApiError(HTTPStatus.BAD_GATEWAY, f"The vault server answered HTTP {e.code}.")
        data = None
    except urllib.error.URLError as e:
        raise T.ApiError(HTTPStatus.BAD_GATEWAY, f"The vault server isn't answering ({e.reason}).")
    if immutable and len(data or b"") <= _RAW_CACHE_BYTES // 16:
        with _raw_cache_lock:
            if key not in _raw_cache:
                _raw_cache[key] = data
                _raw_cache_bytes += len(data or b"") + 100
                while _raw_cache_bytes > _RAW_CACHE_BYTES and _raw_cache:
                    _, old = _raw_cache.popitem(last=False)
                    _raw_cache_bytes -= len(old or b"") + 100
    return data


def _repo_info(auth):
    status, repo = T.gitea("GET", f"repos/{T.ORG}/{T.REPO}", auth=auth)
    if status == 401:
        raise T.ApiError(HTTPStatus.UNAUTHORIZED, "Your sign-in has expired. Sign in again.")
    if status != 200 or not repo:
        raise T.ApiError(HTTPStatus.BAD_GATEWAY, f"Couldn't read the vault (HTTP {status}).")
    return repo


def _head_commit(session):
    """(repo, branch, head commit or None), asked of Gitea with the person's own access. Kept with
    the session for a few seconds, so a page of thumbnails doesn't ask once per image."""
    cached = session.get("head")
    if cached and cached[0] > time.monotonic():
        return cached[1]
    auth = session["auth"]
    repo = _repo_info(auth)
    branch = repo.get("default_branch") or "main"
    status, info = T.gitea("GET", _repo_api(f"branches/{urllib.parse.quote(branch, safe='')}"), auth=auth)
    if status == 404 or repo.get("empty"):
        result = (repo, branch, None)
    elif status == 401:
        raise T.ApiError(HTTPStatus.UNAUTHORIZED, "Your sign-in has expired. Sign in again.")
    elif status != 200:
        raise T.ApiError(HTTPStatus.BAD_GATEWAY, f"Couldn't read branch {branch} (HTTP {status}).")
    else:
        result = (repo, branch, info["commit"]["id"])
    session["head"] = (time.monotonic() + HEAD_SECONDS, result)
    return result


# ------------------------------------------------------------------ the index

def _load_blob_cache():
    global _blob_cache
    try:
        with open(os.path.join(T.STATE_DIR, "browser-blobs.json"), encoding="utf-8") as f:
            _blob_cache = json.load(f)
    except (FileNotFoundError, ValueError):
        _blob_cache = {}


def _save_blob_cache(keep=None):
    """Writes the cache to disk; with keep, only those blobs (the current head's) are kept."""
    global _blob_cache, _blob_cache_dirty
    if keep is not None and len(_blob_cache) > len(keep):
        _blob_cache = {sha: _blob_cache[sha] for sha in keep if sha in _blob_cache}
        _blob_cache_dirty = True
    if not _blob_cache_dirty:
        return
    path = os.path.join(T.STATE_DIR, "browser-blobs.json")
    tmp = path + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(_blob_cache, f)
    os.replace(tmp, path)
    _blob_cache_dirty = False


def _read_blob(sha, auth):
    status, blob = T.gitea("GET", _repo_api(f"git/blobs/{sha}"), auth=auth)
    if status != 200 or not blob:
        return None
    return base64.b64decode(blob.get("content") or "")


def _parse_blob(kind, data):
    if data is None:
        return None
    if kind == "meta":
        try:
            meta = json.loads(data.decode("utf-8"))
            return meta if isinstance(meta, dict) else None
        except (ValueError, UnicodeDecodeError):
            return None
    match = LFS_POINTER_RE.match(data)
    return [match.group(1).decode(), int(match.group(2))] if match else ["git:", len(data)]


def _tree(commit, auth):
    entries, page, total = [], 1, None
    while True:
        status, tree = T.gitea("GET", _repo_api(f"git/trees/{commit}?recursive=true&per_page=1000&page={page}"), auth=auth)
        if status != 200 or not tree:
            raise T.ApiError(HTTPStatus.BAD_GATEWAY, f"Couldn't list the vault's files (HTTP {status}).")
        batch = tree.get("tree") or []
        entries += batch
        total = tree.get("total_count") or total
        if not tree.get("truncated") and (total is None or len(entries) >= total) or not batch or page > 500:
            return entries
        page += 1


def _meta_field(meta, name):
    """Sidecars are written by .NET (camelCase); accept PascalCase too."""
    if name in meta:
        return meta[name]
    return meta.get(name[0].upper() + name[1:])


def _build_index(repo, branch, commit, auth):
    global _blob_cache_dirty
    entries = _tree(commit, auth)
    config, metas, thumbs, files = {}, {}, set(), []
    for e in entries:
        if e.get("type") != "blob":
            continue
        path = e["path"]
        if path == CONFIG_PATH:
            data = _read_blob(e["sha"], auth)
            try:
                config = json.loads(data.decode("utf-8")) if data else {}
            except (ValueError, UnicodeDecodeError):
                config = {}
        elif path.startswith(META_PREFIX) and path.endswith(".json"):
            metas[path[len(META_PREFIX):-len(".json")].lower()] = e["sha"]
        elif path.startswith(THUMB_PREFIX) and path.endswith(".jpg"):
            thumbs.add(path[len(THUMB_PREFIX):-len(".jpg")].lower())
        elif not (path == ".gitattributes" or path.startswith(".swvault/") or path.startswith(".swvault-local/")):
            files.append(e)

    # Sidecars (and pointers of files without one) by blob sha: immutable, so cached across commits.
    # A blob too big to be an LFS pointer is the file itself; the tree already says its size.
    wanted, missing = set(), []
    for f in files:
        lower = f["path"].lower()
        kind, sha = ("meta", metas[lower]) if lower in metas else ("pointer", f["sha"])
        wanted.add(sha)
        if sha in _blob_cache:
            continue
        if kind == "pointer" and (f.get("size") or 0) > POINTER_MAX:
            _blob_cache[sha] = ["git:", f["size"]]
            _blob_cache_dirty = True
        else:
            missing.append((kind, sha))
    if missing:
        with ThreadPoolExecutor(max_workers=8) as pool:
            for (kind, sha), data in zip(missing, pool.map(lambda item: _read_blob(item[1], auth), missing)):
                _blob_cache[sha] = _parse_blob(kind, data)
        _blob_cache_dirty = True
    _save_blob_cache(keep=wanted)

    workflow = config.get("workflow") or config.get("Workflow") or {}
    initial = workflow.get("initialState") or workflow.get("InitialState") or "WIP"
    result = []
    for f in files:
        meta_sha = metas.get(f["path"].lower())
        meta = _blob_cache.get(meta_sha) if meta_sha else None
        item = {"path": f["path"], "thumb": f["path"].lower() in thumbs}
        if isinstance(meta, dict):
            refs = _meta_field(meta, "references") or []
            item.update({
                "version": _meta_field(meta, "version") or 1,
                "oid": _meta_field(meta, "oid"),
                "size": _meta_field(meta, "size") or 0,
                "by": _meta_field(meta, "checkedInBy"),
                "at": _meta_field(meta, "checkedInAt"),
                "comment": _meta_field(meta, "comment"),
                "state": _meta_field(meta, "state") or initial,
                "revision": _meta_field(meta, "revision"),
                "revisions": _meta_field(meta, "revisionHistory"),
                "properties": _meta_field(meta, "properties"),
                "configurations": _meta_field(meta, "configurations"),
                "references": [{"path": _meta_field(r, "path"), "version": _meta_field(r, "version")} for r in refs if isinstance(r, dict)],
                "externalReferences": _meta_field(meta, "externalReferences"),
                "swVersion": _meta_field(meta, "swVersion"),
                "derivedFrom": _meta_field(meta, "derivedFrom"),
                "approvedBy": _meta_field(meta, "approvedBy"),
                "approvedAt": _meta_field(meta, "approvedAt"),
                "approvedVersion": _meta_field(meta, "approvedVersion"),
                "approvedWith": _meta_field(meta, "approvedWith"),
                "approvalNote": _meta_field(meta, "approvalNote"),
            })
        else:
            pointer = _blob_cache.get(f["sha"]) or ["", 0]
            item.update({"version": 1, "oid": pointer[0] or None, "size": pointer[1], "state": initial})
        result.append({k: v for k, v in item.items() if v not in (None, [], {}, "")})

    return {
        "commit": commit,
        "branch": branch,
        "team": T.TEAM_NAME,
        "vault": config.get("name") or config.get("Name") or T.TEAM_NAME,
        "vaultUrl": repo.get("html_url"),
        "webUrl": repo.get("html_url"),
        "states": workflow.get("states") or workflow.get("States") or ["WIP", "InReview", "Released", "Obsolete"],
        "readOnlyStates": workflow.get("readOnlyStates") or workflow.get("ReadOnlyStates") or [],
        "initialState": initial,
        "files": result,
    }


def vault_index(session):
    """The index at the head commit: {body, gzip, etag, payload, byPath (lowercase path -> file)}.
    Built once per commit and shared; treat it as read-only."""
    repo, branch, commit = _head_commit(session)
    with _index_lock:
        if _index["commit"] != commit or _index["body"] is None:
            if commit is None:
                payload = {"commit": None, "branch": branch, "team": T.TEAM_NAME, "vault": T.TEAM_NAME,
                           "vaultUrl": repo.get("html_url"), "states": [], "files": []}
            else:
                started = time.time()
                payload = _build_index(repo, branch, commit, session["auth"])
                T.log(f"web browser: indexed {len(payload['files'])} files at {commit[:8]} in {time.time() - started:.1f}s")
            body = json.dumps(payload, separators=(",", ":")).encode()
            _index.update(commit=commit, body=body, gzip=gzip.compress(body, 6), payload=payload,
                          etag='"%s"' % hashlib.sha256(body).hexdigest()[:32],
                          byPath={f["path"].lower(): f for f in payload["files"]})
        return dict(_index)


def locks(session):
    url = f"{T.GITEA}/{T.ORG}/{T.REPO}.git/info/lfs/locks?limit=1000"
    result, cursor = [], None
    for _ in range(20):
        try:
            with _open(url + (f"&cursor={urllib.parse.quote(cursor)}" if cursor else ""), session["auth"], "application/vnd.git-lfs+json") as response:
                data = json.loads(response.read() or b"{}")
        except urllib.error.HTTPError as e:
            raise T.ApiError(HTTPStatus.BAD_GATEWAY, f"Couldn't read check-outs (HTTP {e.code}).")
        except urllib.error.URLError as e:
            raise T.ApiError(HTTPStatus.BAD_GATEWAY, f"The vault server isn't answering ({e.reason}).")
        for lock in data.get("locks") or []:
            result.append({"id": lock.get("id"), "path": lock.get("path"), "owner": (lock.get("owner") or {}).get("name"), "lockedAt": lock.get("locked_at")})
        cursor = data.get("next_cursor")
        if not cursor:
            break
    return {"locks": result}


def _lfs(method, path, auth, body=None):
    """Calls the vault's LFS lock API. Returns (status, json-or-None)."""
    request = urllib.request.Request(f"{T.GITEA}/{T.ORG}/{T.REPO}.git/info/lfs/{path}",
                                     data=json.dumps(body).encode() if body is not None else None, method=method)
    request.add_header("Authorization", auth)
    request.add_header("Accept", "application/vnd.git-lfs+json")
    if body is not None:
        request.add_header("Content-Type", "application/vnd.git-lfs+json")
    try:
        with urllib.request.urlopen(request, timeout=60) as response:
            return response.status, json.loads(response.read() or b"null")
    except urllib.error.HTTPError as e:
        try:
            return e.code, json.loads(e.read() or b"null")
        except ValueError:
            return e.code, None
    except urllib.error.URLError as e:
        raise T.ApiError(HTTPStatus.BAD_GATEWAY, f"The vault server isn't answering ({e.reason}).")


def _vault_path(body):
    path = str(body.get("path") or "").strip().replace("\\", "/").strip("/")
    if not path or len(path) > 1000:
        raise T.ApiError(HTTPStatus.BAD_REQUEST, "path is required.")
    return path


def checkout(session, body):
    """Checks a file out to the signed-in user from the web page: the same lock SwVault takes.
    The file on their PC becomes editable when they check it out there (it's already theirs)."""
    path = _vault_path(body)
    built = vault_index(session)
    index, entry = built["payload"], built["byPath"].get(path.lower())
    if entry is None:
        raise T.ApiError(HTTPStatus.NOT_FOUND, "That file isn't in the vault (was it renamed or deleted?).")
    state = entry.get("state")
    if state and state.lower() in [s.lower() for s in index.get("readOnlyStates") or []]:
        raise T.ApiError(HTTPStatus.CONFLICT, f"{entry['name'] if 'name' in entry else path} is {state}. Use Change State > Change request in SOLIDWORKS before checking it out.")
    status, data = _lfs("POST", "locks", session["auth"], {"path": entry["path"], "ref": {"name": "refs/heads/" + (index.get("branch") or "main")}})
    if status in (200, 201) and data and data.get("lock"):
        lock = data["lock"]
        T.log(f"web browser: {session['login']} checked out {entry['path']}")
        return {"id": lock.get("id"), "path": lock.get("path"), "owner": (lock.get("owner") or {}).get("name") or session["login"], "lockedAt": lock.get("locked_at")}
    if status == 409:
        owner = ((data or {}).get("lock") or {}).get("owner", {}).get("name") or "someone else"
        raise T.ApiError(HTTPStatus.CONFLICT, f"Already checked out by {owner}.")
    if status == 403:
        raise T.ApiError(HTTPStatus.FORBIDDEN, "Your account is read-only (a viewer), so it can't check files out.")
    if status == 401:
        raise T.ApiError(HTTPStatus.UNAUTHORIZED, "Your sign-in has expired. Sign in again.")
    raise T.ApiError(HTTPStatus.BAD_GATEWAY, f"Couldn't check the file out (HTTP {status}).")


def undo_checkout(session, body):
    """Lets go of the signed-in user's own check-out. Nothing is uploaded or changed in the vault."""
    path = _vault_path(body)
    mine = next((l for l in locks(session)["locks"] if (l["path"] or "").lower() == path.lower()), None)
    if mine is None:
        raise T.ApiError(HTTPStatus.NOT_FOUND, "That file isn't checked out any more.")
    if (mine["owner"] or "").lower() != session["login"].lower():
        raise T.ApiError(HTTPStatus.FORBIDDEN, f"That check-out belongs to {mine['owner']}.")
    status, _ = _lfs("POST", f"locks/{urllib.parse.quote(str(mine['id']))}/unlock", session["auth"], {"force": False})
    if status not in (200, 201):
        raise T.ApiError(HTTPStatus.BAD_GATEWAY, f"Couldn't undo the check-out (HTTP {status}).")
    T.log(f"web browser: {session['login']} undid their check-out of {mine['path']}")
    return {"ok": True, "path": mine["path"]}


def release_checkout(session, body):
    """An admin lets go of someone's check-out, e.g. their PC is lost. Nothing is uploaded: the
    vault keeps the last checked-in version and whatever was changed on that PC stays there."""
    if not T.is_admin(session["login"]):
        raise T.ApiError(HTTPStatus.FORBIDDEN, "Only vault admins can release someone else's check-out.")
    path = str(body.get("path") or "").strip().replace("\\", "/").strip("/")
    if not path:
        raise T.ApiError(HTTPStatus.BAD_REQUEST, "path is required.")
    admin = f"token {T.admin_token()}"
    lock = next((l for l in locks({"auth": admin})["locks"] if (l["path"] or "").lower() == path.lower()), None)
    if lock is None:
        raise T.ApiError(HTTPStatus.NOT_FOUND, "That file isn't checked out any more.")
    request = urllib.request.Request(f"{T.GITEA}/{T.ORG}/{T.REPO}.git/info/lfs/locks/{urllib.parse.quote(str(lock['id']))}/unlock",
                                     data=json.dumps({"force": True}).encode(), method="POST")
    request.add_header("Authorization", admin)
    request.add_header("Accept", "application/vnd.git-lfs+json")
    request.add_header("Content-Type", "application/vnd.git-lfs+json")
    try:
        urllib.request.urlopen(request, timeout=60).close()
    except urllib.error.HTTPError as e:
        raise T.ApiError(HTTPStatus.BAD_GATEWAY, f"Couldn't release the check-out (HTTP {e.code}).")
    except urllib.error.URLError as e:
        raise T.ApiError(HTTPStatus.BAD_GATEWAY, f"The vault server isn't answering ({e.reason}).")
    T.log(f"web browser: {session['login']} released {lock['owner']}'s check-out of {lock['path']}")
    return {"ok": True, "path": lock["path"], "owner": lock["owner"]}


# ------------------------------------------------------------------ history

def history(session, path):
    path = _clean_path(path)
    auth = session["auth"]
    _, branch, commit = _head_commit(session)
    if commit is None:
        return {"versions": []}
    meta_path = META_PREFIX + path + ".json"
    commits, page = [], 1
    while page <= 20:
        status, batch = T.gitea("GET", _repo_api(
            f"commits?sha={urllib.parse.quote(branch)}&path={urllib.parse.quote(meta_path)}&limit=50&page={page}&stat=false&verification=false&files=false"), auth=auth)
        if status != 200 or not batch:
            break
        commits += batch
        if len(batch) < 50:
            break
        page += 1

    def load(c):
        data = raw_file(meta_path, c["sha"], auth)
        return _parse_blob("meta", data) if data is not None else None

    with ThreadPoolExecutor(max_workers=8) as pool:
        metas = list(pool.map(load, commits))
    entries = [(c, m) for c, m in zip(commits, metas) if isinstance(m, dict) and _meta_field(m, "oid")]

    versions = []
    for i, (c, meta) in enumerate(entries):
        older = entries[i + 1][1] if i + 1 < len(entries) else None
        version = _meta_field(meta, "version") or 1
        oid = _meta_field(meta, "oid")
        is_new = older is None or (_meta_field(older, "version") or 1) != version or _meta_field(older, "oid") != oid
        approved_at = _meta_field(meta, "approvedAt")
        released = bool(approved_at) and approved_at != (_meta_field(older, "approvedAt") if older else None)
        info = c.get("commit") or {}
        author = (info.get("author") or {}).get("name") or ""
        subject = (info.get("message") or "").split("\n", 1)[0].strip()
        comment = _meta_field(meta, "comment")
        refs = _meta_field(meta, "references") or []
        versions.append({
            "version": version,
            "commit": c["sha"],
            "oid": oid,
            "size": _meta_field(meta, "size") or 0,
            "by": (_meta_field(meta, "checkedInBy") if is_new and _meta_field(meta, "checkedInBy") else author or _meta_field(meta, "checkedInBy")),
            "at": (info.get("author") or {}).get("date") or c.get("created"),
            "comment": ((comment or "").replace("\r\n", "\n").strip() or f"Version {version}") if is_new else subject,
            "state": _meta_field(meta, "state"),
            "revision": _meta_field(meta, "revision"),
            "properties": _meta_field(meta, "properties"),
            "references": [{"path": _meta_field(r, "path"), "version": _meta_field(r, "version")} for r in refs if isinstance(r, dict)],
            "configurations": _meta_field(meta, "configurations"),
            "approvedBy": _meta_field(meta, "approvedBy") if released else None,
            "approvalNote": _meta_field(meta, "approvalNote") if released else None,
            "isNewVersion": is_new,
        })
    return {"versions": versions}


# ------------------------------------------------------------------ files

def _clean_path(path):
    path = (path or "").replace("\\", "/").strip().strip("/")
    while "//" in path:
        path = path.replace("//", "/")
    parts = path.split("/")
    if not path or any(p in ("", ".", "..") for p in parts) or path.lower().startswith((".swvault/", ".swvault-local/")) or path == ".gitattributes":
        raise T.ApiError(HTTPStatus.BAD_REQUEST, "Not a vault file.")
    return path


def _clean_ref(ref):
    if not ref:
        return None
    if not re.fullmatch(r"[0-9a-f]{7,64}", ref):
        raise T.ApiError(HTTPStatus.BAD_REQUEST, "Not a version.")
    return ref


def thumbnail(session, path, ref):
    path, ref = _clean_path(path), _clean_ref(ref)
    if ref is None:
        _, _, ref = _head_commit(session)
        if ref is None:
            return None, False
        immutable = False
    else:
        immutable = True
    return raw_file(THUMB_PREFIX + path + ".jpg", ref, session["auth"]), immutable


def _direct_r2_action(session, oid, size, filename):
    """Get a short-lived download capability using this user's Gitea credentials."""
    account = os.getenv('R2_ACCOUNT_ID', '').strip().lower()
    if not re.fullmatch(r'[0-9a-f]{32}', account):
        raise T.ApiError(HTTPStatus.SERVICE_UNAVAILABLE, 'R2 direct downloads are not configured.')
    status, batch = _lfs('POST', 'objects/batch', session['auth'], {
        'operation': 'download', 'transfers': ['basic'],
        'objects': [{'oid': oid, 'size': size}],
    })
    if status != 200 or not isinstance(batch, dict):
        raise T.ApiError(HTTPStatus.BAD_GATEWAY, 'Gitea could not authorize an R2 download.')
    objects = batch.get('objects') or []
    obj = objects[0] if objects else {}
    action = (obj.get('actions') or {}).get('download') or {}
    url = action.get('href') or ''
    parsed = urllib.parse.urlsplit(url)
    if (obj.get('oid') != oid or parsed.scheme != 'https' or
        parsed.hostname != f'{account}.r2.cloudflarestorage.com' or
        parsed.port not in (None, 443) or parsed.username or parsed.password or
        not parsed.query):
        raise T.ApiError(HTTPStatus.BAD_GATEWAY, 'Gitea did not return a valid signed private-R2 URL.')
    # A redirect to Gitea's default signed URL downloads using the SHA-256 key
    # as its filename. Re-sign through our *internal-only* R2 coordinator with a
    # Content-Disposition response override, while keeping the download in R2.
    bucket = os.getenv('R2_BUCKET', '').strip()
    if not re.fullmatch(r'[a-z0-9][a-z0-9-]{1,61}[a-z0-9]', bucket):
        # Gitea has already authorized this signed R2 URL. Do not break file
        # downloads just because the optional filename override is unavailable.
        T.log('web browser: named R2 download skipped (bucket config); using authenticated R2 URL')
        return url
    # Gitea uses S3 path addressing: /bucket/lfs/.../<sha256>. Validate the
    # canonical prefix and SHA before asking the coordinator to sign anything.
    key_prefix = '/' + bucket + '/lfs/'
    r2_path = urllib.parse.unquote(parsed.path)
    key = r2_path[len('/' + bucket + '/'):]
    if (not r2_path.startswith(key_prefix) or
        not key.startswith('lfs/') or
        not key.endswith('/' + oid) or
        any(x in ('', '.', '..') for x in key.split('/'))):
        raise T.ApiError(HTTPStatus.BAD_GATEWAY, 'Unexpected R2 file key returned by Gitea.')
    try:
        token_path = os.getenv('ADMIN_TOKEN_FILE', '/run/swvault/admin-token')
        with open(token_path, 'r', encoding='utf-8') as f:
            internal_token = f.read().strip()
        if not internal_token:
            raise ValueError('Missing internal token')
        body = json.dumps({'key': key, 'oid': oid, 'filename': filename}).encode('utf-8')
        request = urllib.request.Request('http://127.0.0.1:3200/internal/browser-download',
                                         data=body, method='POST',
                                         headers={'Content-Type': 'application/json',
                                                  'X-SwVault-Internal-Token': internal_token})
        with urllib.request.urlopen(request, timeout=15) as response:
            result = json.load(response)
        destination = result.get('url', '')
        r2_target = urllib.parse.urlsplit(destination)
        if (r2_target.scheme != 'https' or r2_target.hostname != parsed.hostname or
            r2_target.port not in (None, 443) or not r2_target.query):
            raise ValueError('Unexpected R2 sign result')
        return destination
    except (OSError, ValueError, KeyError, urllib.error.URLError) as error:
        # The Gitea action above has already verified the signed private R2
        # destination and authenticated user. The filename is optional; a
        # broken internal signer must not turn a valid download into HTTP 502.
        # Never log exception text: an HTTPError may contain a signed URL.
        reason = (f'HTTP {error.code}' if isinstance(error, urllib.error.HTTPError)
                  else type(error).__name__)
        T.log(f'web browser: named R2 signing failed ({reason}); using authenticated R2 URL')
        return url


def _stored_blob_preview(path, ref, auth):
    """Read at most 4097 Git blob bytes; large non-LFS files must never hit this proxy."""
    url = f"{T.GITEA}/api/v1/{_repo_api('raw/' + _quote_path(path))}?ref={urllib.parse.quote(ref or '')}"
    try:
        with _open(url, auth) as response:
            return response.read(4097)
    except urllib.error.HTTPError as e:
        raise T.ApiError(HTTPStatus.NOT_FOUND if e.code == 404 else HTTPStatus.BAD_GATEWAY,
                         'File not found.' if e.code == 404 else f'Gitea returned HTTP {e.code}.')
    except urllib.error.URLError:
        raise T.ApiError(HTTPStatus.BAD_GATEWAY, 'Gitea is unavailable.')


def send_download(handler, session, path, ref, version):
    path, ref = _clean_path(path), _clean_ref(ref)
    stored = _stored_blob_preview(path, ref, session['auth'])
    pointer = LFS_POINTER_RE.fullmatch(stored)
    if pointer:
        oid = pointer.group(1).decode('ascii')
        size = int(pointer.group(2))
        name = path.rsplit('/', 1)[-1]
        if version and re.fullmatch(r'\d{1,6}', str(version)):
            stem, dot, ext = name.rpartition('.')
            name = f'{stem} (v{version}).{ext}' if dot else f'{name} (v{version})'
        url = _direct_r2_action(session, oid, size, name)
        # Browser fetches file bytes from R2 itself; never stream them through the VM.
        handler.send_response(HTTPStatus.FOUND)
        handler.send_header('Location', url)
        handler.send_header('Cache-Control', 'private, no-store')
        handler.send_header('Referrer-Policy', 'no-referrer')
        handler.send_header('Content-Length', '0')
        handler.end_headers()
        T.log(f"web browser: {session['login']} redirected LFS {oid[:12]} to private R2")
        return
    # Legacy small Git blobs aren't LFS objects and do not exist in R2.
    # Keep those accessible; NEVER proxy a large Git blob from the VM.
    if len(stored) > 4096:
        raise T.ApiError(HTTPStatus.CONFLICT, 'Large non-LFS file: re-import it through the Windows client to store it in R2.')
    name = path.rsplit('/', 1)[-1]
    if version and re.fullmatch(r'\d{1,6}', str(version)):
        stem, dot, ext = name.rpartition('.')
        name = f'{stem} (v{version}).{ext}' if dot else f'{name} (v{version})'
    ascii_name = re.sub(r'[^A-Za-z0-9._() -]', '_', name)
    handler.send_response(HTTPStatus.OK)
    handler.send_header('Content-Type', mimetypes.guess_type(name)[0] or 'application/octet-stream')
    handler.send_header('Content-Length', str(len(stored)))
    handler.send_header('Content-Disposition', f'attachment; filename="{ascii_name}"; filename*=UTF-8\'\'{urllib.parse.quote(name)}')
    handler.send_header('Cache-Control', 'private, no-store')
    handler.send_header('X-Content-Type-Options', 'nosniff')
    handler.end_headers()
    handler.wfile.write(stored)
    T.log(f"web browser: {session['login']} downloaded small legacy Git file {path}")


# ------------------------------------------------------------------ reviews (same issues as SwVault's Reviews.cs)

REVIEW_LABELS = {
    "review": "#6f42c1", "review: design": "#0366d6", "review: simulation": "#0e8a16", "review: drawing": "#d93f0b",
    "review: approved": "#2cbe4e", "review: changes requested": "#fbca04", "review: cancelled": "#8b949e",
}
KIND_TEXT = {"design": "Design review", "simulation": "Simulation review", "drawing": "Drawing review"}
QUOTE_RE = re.compile(r"^\s*>\s?(.*)$", re.M)
CC_RE = re.compile(r"^cc((?:\s+@[A-Za-z0-9._-]+)+)", re.M)


def _parse_review(issue):
    body = issue.get("body") or ""
    header = T.REVIEW_HEADER_RE.search(body)
    if not header:
        return None
    try:
        info = json.loads(header.group(1))
    except ValueError:
        return None
    labels = {l.get("name", "").lower() for l in issue.get("labels") or []}
    closed = issue.get("state") == "closed"
    status = ("Approved" if "review: approved" in labels
              else "Cancelled" if "review: cancelled" in labels or closed
              else "Changes requested" if "review: changes requested" in labels
              else "Waiting for review")
    kind = info.get("kind") if info.get("kind") in KIND_TEXT else "design"
    assignees = issue.get("assignees") or []
    cc = CC_RE.search(body)
    return {
        "number": issue["number"],
        "kind": KIND_TEXT[kind],
        "kindKey": kind,
        "path": info.get("path") or "",
        "version": info.get("version") or 0,
        "requester": (issue.get("user") or {}).get("login", ""),
        "lead": assignees[0].get("login", "") if assignees else "",
        "status": status,
        "message": "\n".join(m.group(1) for m in QUOTE_RE.finditer(body)) or None,
        "created": issue.get("created_at"),
        "updated": issue.get("updated_at"),
        "webUrl": issue.get("html_url"),
        "cc": [c.lstrip("@") for c in cc.group(1).split()] if cc else [],
    }


def reviews(session, path):
    path = _clean_path(path)
    auth = session["auth"]
    name = urllib.parse.quote(path.rsplit("/", 1)[-1])
    found = []
    for page in range(1, 6):
        status, batch = T.gitea("GET", _repo_api(f"issues?type=issues&state=all&labels=review&q={name}&limit=50&page={page}"), auth=auth)
        if status != 200 or not batch:
            break
        found += [r for r in map(_parse_review, batch) if r and r["path"].lower() == path.lower()]
        if len(batch) < 50:
            break
    found.sort(key=lambda r: r["updated"] or "", reverse=True)
    found = found[:20]

    def comments(review):
        status, items = T.gitea("GET", _repo_api(f"issues/{review['number']}/comments"), auth=auth)
        review["comments"] = [{"author": (c.get("user") or {}).get("login", ""), "at": c.get("created_at"), "text": c.get("body") or ""}
                              for c in (items or [])] if status == 200 else []
        return review

    with ThreadPoolExecutor(max_workers=6) as pool:
        found = list(pool.map(comments, found))
    return {"reviews": found}


def _ensure_labels():
    """Label name -> id, creating the review labels that are missing. Remembered for 10 minutes."""
    if _labels["ids"] is not None and _labels["expires"] > time.monotonic():
        return _labels["ids"]
    status, existing = T.gitea("GET", _repo_api("labels?limit=100"))
    found = {l["name"].lower(): l["id"] for l in existing or []} if status == 200 else {}
    for name, color in REVIEW_LABELS.items():
        if name not in found:
            status, created = T.gitea("POST", _repo_api("labels"), {"name": name, "color": color, "description": "SwVault review requests"})
            if status in (200, 201) and created:
                found[name] = created["id"]
    if all(name in found for name in REVIEW_LABELS):
        _labels.update(ids=found, expires=time.monotonic() + 600)
    return found


def _notify_later(login, number):
    def run():
        try:
            T.notify_review(login, number)
        except Exception as e:  # email is best effort; the request itself is done
            T.log(f"web browser: review #{number} notification failed: {e!r}")
    threading.Thread(target=run, daemon=True).start()


def request_review(session, body):
    path = _clean_path(body.get("path"))
    kind = str(body.get("kind") or "design").lower()
    if kind not in KIND_TEXT:
        raise T.ApiError(HTTPStatus.BAD_REQUEST, "kind must be design, simulation or drawing.")
    lead = str(body.get("lead") or "").strip()
    if not re.fullmatch(r"[A-Za-z0-9._-]{1,40}", lead):
        raise T.ApiError(HTTPStatus.BAD_REQUEST, "Pick the lead to ask.")
    message = str(body.get("message") or "").strip()[:4000]
    current = vault_index(session)["byPath"].get(path.lower())
    if current is None or not current.get("oid"):
        raise T.ApiError(HTTPStatus.BAD_REQUEST, "That file isn't checked in to the vault yet.")
    path, version, me = current["path"], current.get("version") or 1, session["login"]

    # A double click (or a retry while the page waits) must not open the same review twice:
    # requests with the same key wait their turn, and one repeated soon after returns the first.
    key = (me.lower(), path.lower(), kind, lead.lower())
    with _review_lock:
        lock = _review_locks.setdefault(key, threading.Lock())
    with lock:
        with _review_lock:
            now = time.monotonic()
            for k in [k for k, (at, _) in _review_recent.items() if now - at > REVIEW_REPEAT_SECONDS]:
                del _review_recent[k]
            if key in _review_recent:
                return _review_recent[key][1]
        review = _create_review(session, path, version, kind, lead, message)
        with _review_lock:
            _review_recent[key] = (time.monotonic(), review)
        return review


def _create_review(session, path, version, kind, lead, message):
    me = session["login"]
    labels = _ensure_labels()
    found, cc = T.review_cc(me, lead, path)
    header = json.dumps({"kind": kind, "path": path, "version": version}, separators=(",", ":"))
    kind_title = KIND_TEXT[kind].split(" ")[0]
    lines = [f"<!-- swvault-review {header} -->",
             f"**{kind_title} review** requested by @{me} for `{path}` (version {version}).", ""]
    if cc:
        car, subsystem = found
        lines += ["cc " + " ".join("@" + c for c in cc) + f" (responsible engineers of {car['name']} / {subsystem['name']})", ""]
    if message:
        lines += ["> " + line.rstrip("\r") for line in message.split("\n")]
    lines += ["", "_Respond from SOLIDWORKS (SwVault tab > Reviews), the SwVault web browser, or comment here._"]
    status, issue = T.gitea("POST", _repo_api("issues"), {
        "title": f"{kind_title} review: {path.rsplit('/', 1)[-1]} v{version}",
        "body": "\n".join(lines) + "\n",
        "assignees": [lead],
        "labels": [labels[l] for l in ("review", f"review: {kind}") if l in labels],
    }, auth=session["auth"])
    if status not in (200, 201) or not issue:
        raise T.ApiError(HTTPStatus.BAD_REQUEST if status == 422 else HTTPStatus.BAD_GATEWAY,
                         "The server didn't accept the request (is the lead still on the team?)." if status == 422 else f"Couldn't create the review request (HTTP {status}).")
    T.log(f"web browser: review #{issue['number']} ({kind}) of {path} v{version} requested by {me} from {lead}")
    _notify_later(me, issue["number"])
    return _parse_review(issue)


def respond(session, number, body):
    decision = str(body.get("decision") or "").lower()
    comment = str(body.get("comment") or "").strip()[:8000]
    texts = {"approve": "✅ **Approved**", "changes": "✏️ **Changes requested**", "cancel": "Request cancelled", "comment": ""}
    if decision not in texts:
        raise T.ApiError(HTTPStatus.BAD_REQUEST, "decision must be approve, changes, comment or cancel.")
    if decision in ("comment", "changes") and not comment:
        raise T.ApiError(HTTPStatus.BAD_REQUEST, "Write what to change." if decision == "changes" else "Write a comment first.")
    auth, me = session["auth"], session["login"]
    status, issue = T.gitea("GET", _repo_api(f"issues/{number}"), auth=auth)
    review = _parse_review(issue) if status == 200 and issue else None
    if review is None:
        raise T.ApiError(HTTPStatus.NOT_FOUND, "No such review request.")
    if decision in ("approve", "changes") and review["lead"].lower() != me.lower():
        raise T.ApiError(HTTPStatus.FORBIDDEN, f"Only the reviewer ({review['lead']}) can approve or ask for changes.")
    if decision == "cancel" and review["requester"].lower() != me.lower():
        raise T.ApiError(HTTPStatus.FORBIDDEN, "Only the person who asked can cancel the request.")

    text = texts[decision]
    if comment:
        text = comment if not text else text + "\n\n" + comment
    status, _ = T.gitea("POST", _repo_api(f"issues/{number}/comments"), {"body": text}, auth=auth)
    if status not in (200, 201):
        raise T.ApiError(HTTPStatus.BAD_GATEWAY, f"Couldn't post that (HTTP {status}).")
    if decision != "comment":
        labels = _ensure_labels()
        state_label = {"approve": "review: approved", "changes": "review: changes requested", "cancel": "review: cancelled"}[decision]
        keep = [labels[l] for l in ("review", f"review: {review['kindKey']}", state_label) if l in labels]
        T.gitea("PUT", _repo_api(f"issues/{number}/labels"), {"labels": keep}, auth=auth)
        T.gitea("PATCH", _repo_api(f"issues/{number}"), {"state": "open" if decision == "changes" else "closed"}, auth=auth)
        if decision in ("approve", "changes"):
            _notify_later(me, number)
    T.log(f"web browser: review #{number}: {decision} by {me}")
    return {"ok": True}


def leads():
    _, people = T.list_people()
    return {"leads": [{"login": p["login"], "fullName": p["fullName"], "subteam": p["subteam"]}
                      for p in people["people"] if p.get("memberType") == "lead"]}


# ------------------------------------------------------------------ accounts (vault admins)

LOGIN_RE = re.compile(r"[A-Za-z0-9][A-Za-z0-9._-]{0,39}")
PASSWORD_CHARS = "abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789"  # no look-alikes


def _require_admin(session):
    if not T.is_admin(session["login"]):
        raise T.ApiError(HTTPStatus.FORBIDDEN, "Only vault admins can see and manage accounts.")


def _team_logins(name):
    logins, page, team = set(), 1, T.team_id(name)
    while page <= 40:
        status, batch = T.gitea("GET", f"teams/{team}/members?limit=50&page={page}")
        if status != 200 or not batch:
            break
        logins.update(m["login"].lower() for m in batch)
        if len(batch) < 50:
            break
        page += 1
    return logins


def accounts(session):
    _require_admin(session)
    users, page = [], 1
    while page <= 40:
        status, batch = T.gitea("GET", f"admin/users?limit=50&page={page}")
        if status != 200:
            raise T.ApiError(HTTPStatus.BAD_GATEWAY, f"Couldn't list accounts (HTTP {status}).")
        users += batch or []
        if not batch or len(batch) < 50:
            break
        page += 1
    with ThreadPoolExecutor(max_workers=3) as pool:
        owners, designers, viewers = pool.map(_team_logins, ("Owners", "Designers", "Viewers"))
    profiles = T.load_people()["people"]
    result = []
    for u in users:
        login = u["login"]
        key = login.lower()
        email = u.get("email") or ""
        last = u.get("last_login") or ""
        profile = profiles.get(key, {})
        result.append({
            "login": login,
            "fullName": u.get("full_name") or "",
            "email": "" if email.endswith("@noreply.localhost") else email,
            "role": "admin" if key in owners else "designer" if key in designers else "viewer" if key in viewers else "none",
            "disabled": bool(u.get("prohibit_login")),
            "created": u.get("created"),
            "lastSignIn": None if last.startswith("0001-") or last.startswith("1970-") else last or None,
            "memberType": profile.get("memberType"),
            "subteam": profile.get("subteam") or "",
            "pendingLead": (profile.get("pendingLead") or {}).get("subteam"),
            "you": key == session["login"].lower(),
        })
    result.sort(key=lambda a: a["login"].lower())
    return {"accounts": result, "emailDomains": T.EMAIL_DOMAINS}


def _patch_account(login, fields):
    status, _ = T.gitea("PATCH", f"admin/users/{urllib.parse.quote(login)}", dict({"login_name": login, "source_id": 0}, **fields))
    if status != 200:
        raise T.ApiError(HTTPStatus.BAD_REQUEST if status == 422 else HTTPStatus.BAD_GATEWAY,
                         "The server didn't accept that (is the email already used by another account?)." if status == 422
                         else f"Couldn't change the account (HTTP {status}).")


def _set_vault_admin(login, admin, by):
    """Adds LOGIN to (or removes it from) roles.admin in .swvault/vault.json, which is what SwVault
    on people's PCs goes by. One commit; no-op when it's already so or the vault isn't set up yet."""
    file = "repos/%s/%s/contents/.swvault%%2Fvault.json" % (T.ORG, T.REPO)
    status, current = T.gitea("GET", file + "?ref=main")
    if status == 404:
        return
    if status != 200 or not current:
        raise T.ApiError(HTTPStatus.BAD_GATEWAY, f"Couldn't read the vault's settings (HTTP {status}).")
    config = json.loads(base64.b64decode(current["content"]).decode("utf-8-sig"))
    roles = config.setdefault("roles", {})
    admins = [a for a in roles.get("admin") or [] if a.lower() != login.lower()]
    if admin:
        admins = sorted(admins + [login])
    if admins == (roles.get("admin") or []):
        return
    roles["admin"] = admins
    status, _ = T.gitea("PUT", file, {
        "content": base64.b64encode((json.dumps(config, indent=2) + "\n").encode()).decode(), "sha": current["sha"], "branch": "main",
        "message": f"Add {login} as admin (by {by})" if admin else f"Remove {login} as admin (by {by})"})
    if status not in (200, 201):
        raise T.ApiError(HTTPStatus.BAD_GATEWAY, f"Couldn't save the vault's settings (HTTP {status}).")


def manage_account(session, login, body):
    _require_admin(session)
    me = session["login"]
    if not LOGIN_RE.fullmatch(login or ""):
        raise T.ApiError(HTTPStatus.BAD_REQUEST, "Not an account name.")
    status, user = T.gitea("GET", f"users/{urllib.parse.quote(login)}")
    if status != 200 or not user:
        raise T.ApiError(HTTPStatus.NOT_FOUND, f"There's no account called {login}.")
    login = user["login"]
    action = str(body.get("action") or "")
    result = {"ok": True}

    if action in ("disable", "enable"):
        if action == "disable" and login.lower() == me.lower():
            raise T.ApiError(HTTPStatus.BAD_REQUEST, "You can't disable your own account.")
        _patch_account(login, {"prohibit_login": action == "disable"})
        if action == "disable":  # and sign them out of the web browser and the team service
            with _sessions_lock:
                for sid in [k for k, s in _sessions.items() if s["login"].lower() == login.lower()]:
                    del _sessions[sid]
            T.forget_signins()
    elif action == "role":
        role = str(body.get("role") or "").lower()
        if role not in ("admin", "designer", "viewer"):
            raise T.ApiError(HTTPStatus.BAD_REQUEST, "role must be admin, designer or viewer.")
        was_admin = login.lower() in _team_logins("Owners")
        if was_admin and role != "admin" and login.lower() == me.lower():
            raise T.ApiError(HTTPStatus.BAD_REQUEST, "You can't take away your own admin access. Ask another admin.")
        if role == "admin" and user.get("prohibit_login"):
            raise T.ApiError(HTTPStatus.BAD_REQUEST, f"{login} is disabled. Enable the account first.")
        member = urllib.parse.quote(login)
        # An admin is in Owners (the server's permission) and in the vault's admin list (what
        # SwVault checks), and also a designer so check-out works like everyone else's.
        join, leave = ("Viewers", "Designers") if role == "viewer" else ("Designers", "Viewers")
        status, _ = T.gitea("PUT", f"teams/{T.team_id(join)}/members/{member}")
        if status not in (200, 204):
            raise T.ApiError(HTTPStatus.BAD_GATEWAY, f"Couldn't add {login} to {join} (HTTP {status}).")
        T.gitea("DELETE", f"teams/{T.team_id(leave)}/members/{member}")
        if role == "admin":
            status, _ = T.gitea("PUT", f"teams/{T.team_id('Owners')}/members/{member}")
            if status not in (200, 204):
                raise T.ApiError(HTTPStatus.BAD_GATEWAY, f"Couldn't make {login} an admin (HTTP {status}).")
            _set_vault_admin(login, True, me)
        elif was_admin:
            _set_vault_admin(login, False, me)
            status, _ = T.gitea("DELETE", f"teams/{T.team_id('Owners')}/members/{member}")
            if status not in (200, 204):
                raise T.ApiError(HTTPStatus.BAD_GATEWAY, f"Couldn't remove {login} as an admin (HTTP {status}).")
        T.forget_roles()
    elif action == "email":
        email = str(body.get("email") or "").strip()
        if not T.EMAIL_RE.match(email) or len(email) > 200:
            raise T.ApiError(HTTPStatus.BAD_REQUEST, "That isn't an email address.")
        _patch_account(login, {"email": email})
        T.forget("user:" + login.lower())
    elif action == "reset-password":
        password = "-".join("".join(secrets.choice(PASSWORD_CHARS) for _ in range(4)) for _ in range(4))
        _patch_account(login, {"password": password, "must_change_password": False})
        if login.lower() != me.lower():
            with _sessions_lock:
                for sid in [k for k, s in _sessions.items() if s["login"].lower() == login.lower()]:
                    del _sessions[sid]
        T.forget_signins()
        result["password"] = password
    else:
        raise T.ApiError(HTTPStatus.BAD_REQUEST, "action must be disable, enable, role, email or reset-password.")
    T.log(f"web browser: {me} changed account {login}: {action}" + (f" -> {body.get('role')}" if action == "role" else ""))
    return result


# ------------------------------------------------------------------ HTTP

def _not_modified(handler, etag, cache, extra=None):
    """Answers 304 when the browser already has this version (If-None-Match); returns whether it did."""
    if not etag or etag not in (handler.headers.get("If-None-Match") or ""):
        return False
    handler.send_response(HTTPStatus.NOT_MODIFIED)
    handler.send_header("ETag", etag)
    handler.send_header("Cache-Control", cache)
    handler.send_header("Vary", "Accept-Encoding")
    for name, value in (extra or {}).items():
        handler.send_header(name, value)
    handler.end_headers()
    return True


def _send(handler, status, body, content_type, extra=None, cache="no-store", etag=None):
    gzip_ok = "gzip" in (handler.headers.get("Accept-Encoding") or "")
    handler.send_response(status)
    handler.send_header("Content-Type", content_type)
    if etag:
        handler.send_header("ETag", etag)
    if isinstance(body, tuple):  # (plain, gzipped)
        body = body[1] if gzip_ok else body[0]
        if gzip_ok:
            handler.send_header("Content-Encoding", "gzip")
        handler.send_header("Vary", "Accept-Encoding")
    handler.send_header("Content-Length", str(len(body)))
    handler.send_header("Cache-Control", cache)
    handler.send_header("X-Content-Type-Options", "nosniff")
    handler.send_header("Referrer-Policy", "same-origin")
    for name, value in (extra or {}).items():
        handler.send_header(name, value)
    handler.end_headers()
    handler.wfile.write(body)


def _json(handler, payload, status=HTTPStatus.OK, extra=None):
    data = json.dumps(payload, separators=(",", ":")).encode()
    _send(handler, status, data, "application/json", extra)


def _static(handler, name):
    path = os.path.join(WEB_DIR, name)
    if name.startswith("logo") and not os.path.exists(path):
        path = os.path.join(WEB_DIR, "logo.svg" if os.path.exists(os.path.join(WEB_DIR, "logo.svg")) else "icon.svg")
    # Read and compressed once (again when the file changes); browsers revalidate with the ETag.
    try:
        mtime = os.stat(path).st_mtime_ns
        hit = _static_cache.get(path)
        if not hit or hit[0] != mtime:
            with open(path, "rb") as f:
                data = f.read()
            hit = (mtime, data, gzip.compress(data, 9), '"%s"' % hashlib.sha256(data).hexdigest()[:32])
            _static_cache[path] = hit
    except FileNotFoundError:
        raise T.ApiError(HTTPStatus.NOT_FOUND, "Not found.")
    _, data, zipped, etag = hit
    content_type = {"html": "text/html; charset=utf-8", "js": "text/javascript; charset=utf-8",
                    "css": "text/css; charset=utf-8", "svg": "image/svg+xml"}[name.rsplit(".", 1)[-1]]
    headers = {"Content-Security-Policy": CSP, "X-Frame-Options": "DENY"}
    if _not_modified(handler, etag, "no-cache", headers):
        return
    _send(handler, HTTPStatus.OK, (data, zipped), content_type, headers, cache="no-cache", etag=etag)


# ------------------------------------------------------------------ updates (vault admins)
#
# The update itself runs on the server, outside this container (server/linux/auto-update.sh): it
# writes auto-update.json as it goes, and starts when auto-update.request appears.

UPDATE_QUEUE_SECONDS = 120    # the server picks a request up within seconds
UPDATE_STALE_SECONDS = 45 * 60


def _update_files():
    return os.path.join(T.STATE_DIR, "auto-update.json"), os.path.join(T.STATE_DIR, "auto-update.request")


def update_status(session):
    _require_admin(session)
    status_file, _ = _update_files()
    try:
        _, published = T.latest_installer()
    except T.ApiError:
        published = None
    try:
        status = T.load(status_file, None)
        age = time.time() - os.path.getmtime(status_file) if status else 0
    except (OSError, ValueError):
        # The file is being rewritten right now; the page asks again
        return {"enabled": True, "status": {"state": "running", "message": "Working..."}, "published": published}
    if status and status.get("state") == "running":
        if status.get("queued") and age > UPDATE_QUEUE_SECONDS:
            status = dict(status, state="failed", message="The server didn't start the update. On the server: sudo ./swvault-admin.sh auto-update status")
        elif age > UPDATE_STALE_SECONDS:
            status = dict(status, state="failed", message="The update stopped reporting. On the server: sudo ./swvault-admin.sh auto-update status")
    return {"enabled": status is not None, "status": status, "published": published}


def request_update(session):
    current = update_status(session)
    if not current["enabled"]:
        raise T.ApiError(HTTPStatus.CONFLICT, "Automatic updates aren't turned on. On the server: sudo ./swvault-admin.sh auto-update on <owner>/<repository>")
    if current["status"].get("state") == "running":
        raise T.ApiError(HTTPStatus.CONFLICT, "An update is already running.")
    status_file, request_file = _update_files()
    login_name = session["login"]
    T.save(status_file, {"state": "running", "queued": True, "message": "Waiting for the server to start the update",
                         "started": T.stamp(), "trigger": f"website ({login_name})", "updated": T.stamp()})
    with open(request_file, "w", encoding="utf-8") as f:
        f.write(login_name)
    T.log(f"update requested by {login_name}")
    return update_status(session)


def handle(handler, method, route):
    """Serves one request for the web browser. Errors are raised as T.ApiError."""
    if method == "GET" and route in STATIC:
        if handler.path.split("?", 1)[0] == "/swvault":  # relative links need the trailing slash
            handler.send_response(HTTPStatus.MOVED_PERMANENTLY)
            handler.send_header("Location", "/swvault/")
            handler.send_header("Content-Length", "0")
            handler.end_headers()
            return
        return _static(handler, STATIC[route])
    if method == "POST" and handler.headers.get("X-SwVault") != "1":
        raise T.ApiError(HTTPStatus.FORBIDDEN, "Missing X-SwVault header.")

    if method == "POST" and route == "/api/login":
        user, cookie = login(handler, handler.read_json())
        return _json(handler, user, extra={"Set-Cookie": cookie})
    if method == "POST" and route == "/api/logout":
        return _json(handler, {"ok": True}, extra={"Set-Cookie": logout(handler)})

    session = current_session(handler)
    query = lambda name: handler.query(name)
    if method == "GET" and route == "/api/me":
        repo, _, _ = _head_commit(session)
        return _json(handler, {"login": session["login"], "fullName": session["fullName"], "team": T.TEAM_NAME, "vaultUrl": repo.get("html_url"),
                               "admin": T.is_admin(session["login"])})
    if method == "GET" and route == "/api/admin/accounts":
        return _json(handler, accounts(session))
    match = re.fullmatch(r"/api/admin/accounts/([^/]+)", route)
    if method == "POST" and match:
        return _json(handler, manage_account(session, match.group(1), handler.read_json()))
    if route == "/api/admin/update" and method in ("GET", "POST"):
        if method == "POST":
            handler.read_json()
        return _json(handler, update_status(session) if method == "GET" else request_update(session))
    if method == "GET" and route == "/api/index":
        # The page refreshes every minute; unless something was checked in, it gets a 304.
        built = vault_index(session)
        if _not_modified(handler, built["etag"], "private, no-cache"):
            return
        return _send(handler, HTTPStatus.OK, (built["body"], built["gzip"]), "application/json", cache="private, no-cache", etag=built["etag"])
    if method == "GET" and route == "/api/locks":
        return _json(handler, locks(session))
    if method == "POST" and route == "/api/checkout":
        return _json(handler, checkout(session, handler.read_json()))
    if method == "POST" and route == "/api/undo-checkout":
        return _json(handler, undo_checkout(session, handler.read_json()))
    if method == "POST" and route == "/api/admin/release":
        return _json(handler, release_checkout(session, handler.read_json()))
    if method == "GET" and route == "/api/cars":
        return _json(handler, T.list_cars()[1])
    if method == "GET" and route == "/api/leads":
        return _json(handler, leads())
    if method == "GET" and route == "/api/history":
        return _json(handler, history(session, query("path")))
    if method == "GET" and route == "/api/thumb":
        data, immutable = thumbnail(session, query("path"), query("ref"))
        if data is None:
            raise T.ApiError(HTTPStatus.NOT_FOUND, "No preview saved for that version.")
        return _send(handler, HTTPStatus.OK, data, "image/jpeg", cache="private, max-age=604800, immutable" if immutable else "private, max-age=60")
    if method == "GET" and route == "/api/download":
        return send_download(handler, session, query("path"), query("ref"), query("version"))
    if method == "GET" and route == "/api/reviews":
        return _json(handler, reviews(session, query("path")))
    if method == "POST" and route == "/api/reviews":
        return _json(handler, request_review(session, handler.read_json()))
    match = re.fullmatch(r"/api/reviews/(\d+)/respond", route)
    if method == "POST" and match:
        return _json(handler, respond(session, int(match.group(1)), handler.read_json()))
    raise T.ApiError(HTTPStatus.NOT_FOUND, "Not found.")
