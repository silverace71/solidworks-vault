#!/usr/bin/env python3
"""
SwVault team service: invites, school-email verification and member profiles, next to Gitea.

Runs as docker-compose.yml service "invites", published by Tailscale Funnel under
https://<server>/swvault-invites/. Python standard library only.

Joining (public)
  GET  /invite/<code>       {team, valid, role, emailDomains, subteams} for the sign-in window
  POST /verify              {code, email} -> emails a 6-digit code (when EMAIL_DOMAINS is set)
  POST /redeem              {code, username, password, fullName?, email?, emailCode?,
                             memberType: member|lead, subteam?} -> creates the Gitea account,
                            adds it to the invite's team (Designers or Viewers), saves the profile
  GET  /download[?invite=C] in a browser: a page with the setup steps and a download button.
                            &format=exe: the setup wizard (the zip when none is published);
                            otherwise (SwVault agents updating): the installer zip. Downloads are
                            named with the invite code, which the installer picks up so the member
                            doesn't have to type it
  GET  /installer/latest    {version, sha256, size, published, required, notes} of that zip; every
                            SwVault agent checks this and offers the update to its user

Members (HTTP Basic: Gitea user name + SwVault token or password; must be in the organization)
  GET  /people              everyone on the team with their profile (member or lead, subteam,
                            and a lead request still waiting for an admin)
  PUT  /people/me           {memberType: member|lead, subteam?}; becoming a lead waits for an admin

  Projects are called "cars" in these paths and in subsystems.json, from the first team's use.
  GET  /cars                projects, their subsystems (each a vault folder) and responsible engineers
  POST /cars                {name, folder?} -> new project
  PUT  /cars/<id>           {name?, folder?} -> rename it (admins, or whoever added it)
  DELETE /cars/<id>         remove it and its subsystems (files in the vault are not touched)
  POST /cars/<id>/subsystems            {name, folder?} -> new subsystem of that project
  PUT  /subsystems/<id>     {name?, folder?} -> rename it or point it at another folder
  DELETE /subsystems/<id>   remove it (files in the vault are not touched)
  POST /subsystems/<id>/folders         {name} -> a folder inside the subsystem's folder (e.g.
                            "Fasteners", "COTS Parts"); every PC creates it, even while it is empty
  DELETE /subsystems/<id>/folders?name=N   take it off the list (admins, whoever added the
                            subsystem, and its responsible engineers)
  POST /subsystems/<id>/engineers/me    ask to be a responsible engineer (waits for an admin)
  DELETE /subsystems/<id>/engineers/<login>   step down / withdraw a request (admins: remove anyone)

  POST /reviews/<n>/notify  emails the lead (new request) or the requester (approved / changes
                            requested, with the lead's feedback); a general member's new request is
                            also cc'd to the responsible engineers of the file's subsystem; each
                            event is emailed once

Admins (as members, and in the organization's Owners team)
  POST   /invites           {role: designer|viewer, uses, days, note?} -> new invite
  GET    /invites           active invites
  DELETE /invites/<code>    revoke
  PUT    /installer[?required=1&notes=...]   upload the installer zip (scripts/package.ps1 -Publish);
                            required: agents keep reminding people until they install it
  PUT    /installer/setup?version=V          upload the setup wizard (.exe) of the published version
  PUT    /people/<login>    set someone else's profile (no approval needed)
  GET    /approvals         lead and responsible-engineer requests waiting for an admin
  POST   /approvals         {kind: lead|engineer, login, subsystem?, approve: true|false}
  POST   /test-email        {to} -> sends a test message (swvault-admin.sh test-email)

Web file browser (browser.py, web/): https://<server>/swvault/ (also /swvault-invites/browse)
  The vault's files for anyone on the team in a web browser: search, thumbnails, details, history,
  reviews and downloads; SOLIDWORKS commands go to SwVault on the person's PC as swvault:// links.

Admins' own lead and engineer requests are approved immediately. Admins are emailed when a
request needs them; the requester is emailed the decision.
"""

import hashlib
import hmac
import html
import json
import os
import re
import secrets
import shutil
import smtplib
import ssl
import sys
import threading
import time
import urllib.error
import urllib.request
import zipfile
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timedelta, timezone
from email.message import EmailMessage
from http import HTTPStatus
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

import browser

GITEA = os.environ.get("GITEA_URL", "http://127.0.0.1:3000").rstrip("/")
ORG = os.environ["ORG"]
TEAM_NAME = os.environ.get("TEAM_NAME", ORG)
REPO = os.environ.get("REPO", "cad")
TOKEN_FILE = os.environ.get("ADMIN_TOKEN_FILE", "/run/swvault/admin-token")
STATE_DIR = os.environ.get("STATE_DIR", "/data")
INVITES_FILE = os.path.join(STATE_DIR, "invites.json")
PEOPLE_FILE = os.path.join(STATE_DIR, "people.json")
NOTIFIED_FILE = os.path.join(STATE_DIR, "review-emails.json")
SUBSYSTEMS_FILE = os.path.join(STATE_DIR, "subsystems.json")
INSTALLER_FILE = os.path.join(STATE_DIR, "installer", "SwVault-installer.zip")
INSTALLER_META = os.path.join(STATE_DIR, "installer", "installer.json")
# The same version as a setup wizard, for people; agents update from the zip.
SETUP_FILE = os.path.join(STATE_DIR, "installer", "SwVault-Setup.exe")
PORT = int(os.environ.get("PORT", "3100"))
PREFIX = "/swvault-invites"
BROWSER_PREFIX = "/swvault"  # the web file browser's own mount (tailscale-serve.json)

# New members must prove they own an address at one of these domains (e.g. "colorado.edu").
# Empty: no email check.
EMAIL_DOMAINS = [d.strip().lower().lstrip("@") for d in os.environ.get("EMAIL_DOMAINS", "").split(",")
                 if d.strip() and d.strip().lower() != "none"]
SMTP_HOST = os.environ.get("SMTP_HOST", "")
SMTP_PORT = int(os.environ.get("SMTP_PORT") or 587)
SMTP_USER = os.environ.get("SMTP_USER", "")
SMTP_PASSWORD = os.environ.get("SMTP_PASSWORD", "")
SMTP_FROM = os.environ.get("SMTP_FROM", "") or SMTP_USER

MAX_UPLOAD = 2 * 1024 ** 3  # installer zip
CODE_ALPHABET = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ"  # no 0/O, 1/I
USERNAME_RE = re.compile(r"^[A-Za-z0-9](?:[A-Za-z0-9._-]{0,38}[A-Za-z0-9])?$")
EMAIL_RE = re.compile(r"^[A-Za-z0-9._%+'-]+@([A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)+)$")
ROLES = {"designer": "Designers", "viewer": "Viewers"}
MEMBER_TYPES = ("member", "lead")

VERIFY_MINUTES = 15
VERIFY_ATTEMPTS = 5
RESEND_SECONDS = 60
SENDS_PER_HOUR = 5

_lock = threading.Lock()
_throttle_lock = threading.Lock()  # _failures only, so throttling never waits for _lock (held during Gitea calls)
_failures = {}       # "bucket:ip" -> [timestamps] of failed attempts
_verifications = {}  # email (lowercase) -> {code, invite, expires, attempts, sent: [timestamps]}
FAIL_WINDOW = 15 * 60
FAIL_LIMIT = 10
_last_sweep = 0.0

# Short-lived answers from Gitea that every request would otherwise ask again (team ids, who is an
# admin, users' names and emails, whose credentials are good). Changes made here clear them.
_cache = {}          # key -> (expires, value)
_cache_lock = threading.Lock()
SIGNIN_SECONDS = 30  # how long good credentials are trusted without asking Gitea again
_token = (None, "")  # (mtime_ns, admin token)

# Email goes out in the background so a slow SMTP server doesn't hold up requests.
_mailer = ThreadPoolExecutor(max_workers=2, thread_name_prefix="email")


def log(message):
    print(f"{datetime.now().isoformat(timespec='seconds')} {message}", flush=True)


class ApiError(Exception):
    def __init__(self, status, message):
        super().__init__(message)
        self.status = status
        self.message = message


# ------------------------------------------------------------------ state

def load(path, empty):
    try:
        with open(path, encoding="utf-8") as f:
            return json.load(f)
    except FileNotFoundError:
        return empty


def save(path, data):
    os.makedirs(STATE_DIR, exist_ok=True)
    tmp = path + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(data, f, indent=2)
    os.replace(tmp, path)


def load_invites():
    return load(INVITES_FILE, {"invites": []})


def load_people():
    return load(PEOPLE_FILE, {"people": {}})


def now():
    return datetime.now(timezone.utc)


def stamp():
    return now().isoformat(timespec="seconds")


def is_active(invite):
    return invite["usesLeft"] > 0 and datetime.fromisoformat(invite["expires"]) > now()


def normalize_code(code):
    return re.sub(r"[^A-Z0-9]", "", (code or "").upper())


def find_invite(state, code):
    wanted = normalize_code(code)
    return next((i for i in state["invites"] if normalize_code(i["code"]) == wanted), None) if wanted else None


def new_code():
    raw = "".join(secrets.choice(CODE_ALPHABET) for _ in range(12))
    return f"{raw[0:4]}-{raw[4:8]}-{raw[8:12]}"


def public_invite(invite):
    return {k: invite[k] for k in ("code", "role", "usesLeft", "uses", "expires", "note", "createdBy", "createdAt") if k in invite}


def clean_profile(body):
    member_type = (body.get("memberType") or "member").lower()
    if member_type not in MEMBER_TYPES:
        raise ApiError(HTTPStatus.BAD_REQUEST, "memberType must be member or lead.")
    subteam = re.sub(r"\s+", " ", (body.get("subteam") or "")).strip()[:60]
    if member_type == "lead" and not subteam:
        raise ApiError(HTTPStatus.BAD_REQUEST, "Say which subteam you lead (e.g. Chassis, Aero, Powertrain).")
    return member_type, subteam


def known_subteams(people):
    names = {p["subteam"] for p in people["people"].values() if p.get("subteam")}
    names |= {p["pendingLead"]["subteam"] for p in people["people"].values() if p.get("pendingLead")}
    return sorted(names, key=str.lower)


def set_profile(login, member_type, subteam, approved):
    """
    Saves a profile and returns it. Becoming a lead needs an admin: until one approves, the request
    is kept as pendingLead and the person keeps their current role. Stepping down to general member
    takes effect right away.
    """
    with _lock:
        people = load_people()
        key = login.lower()
        current = people["people"].get(key) or {"memberType": "member", "subteam": ""}
        profile = dict(current, login=login, updated=stamp())
        already = current.get("memberType") == "lead" and (current.get("subteam") or "").lower() == subteam.lower()
        if member_type == "lead" and not approved and not already:
            profile["pendingLead"] = {"subteam": subteam, "requested": stamp()}
        else:
            profile["memberType"], profile["subteam"] = member_type, subteam
            profile.pop("pendingLead", None)
        people["people"][key] = profile
        save(PEOPLE_FILE, people)
        return profile


# ------------------------------------------------------------------ caching

def cached(key, seconds, compute):
    """compute()'s value, remembered for a few seconds. Errors aren't remembered."""
    now_ = time.monotonic()
    with _cache_lock:
        hit = _cache.get(key)
        if hit and hit[0] > now_:
            return hit[1]
    value = compute()
    with _cache_lock:
        if len(_cache) > 5000:
            _cache.clear()
        _cache[key] = (now_ + seconds, value)
    return value


def forget(prefix=""):
    """Drops cached answers whose key starts with prefix (everything when empty)."""
    with _cache_lock:
        for key in [k for k in _cache if k.startswith(prefix)]:
            del _cache[key]


# ------------------------------------------------------------------ throttling

# Separate buckets per client address (redeem guesses, email sends, sign-ins), so someone guessing
# invite codes can't lock the admin out. None = local request (admin script), never throttled.

def sweep_failures():
    """Forgets old failures now and then, so addresses seen once don't pile up. Call with _throttle_lock held."""
    global _last_sweep
    current = time.time()
    if current - _last_sweep < 300:
        return
    _last_sweep = current
    for key in [k for k, times in _failures.items() if not times or times[-1] <= current - FAIL_WINDOW]:
        del _failures[key]


def sweep_verifications():
    """Drops email codes that expired over an hour ago. Call with _lock held."""
    current = time.time()
    for address in [a for a, v in _verifications.items() if v["expires"] < current and (not v["sent"] or v["sent"][-1] <= current - 3600)]:
        del _verifications[address]


def throttled(key):
    if key is None:
        return False
    with _throttle_lock:
        sweep_failures()
        recent = [t for t in _failures.get(key, []) if t > time.time() - FAIL_WINDOW]
        _failures[key] = recent
        return len(recent) >= FAIL_LIMIT


def record_failure(key):
    if key is None:
        return
    with _throttle_lock:
        _failures.setdefault(key, []).append(time.time())


def check_throttle(key, message="Too many failed attempts. Try again in 15 minutes."):
    if throttled(key):
        raise ApiError(HTTPStatus.TOO_MANY_REQUESTS, message)


# ------------------------------------------------------------------ email

def email_domain_allowed(email):
    match = EMAIL_RE.match(email or "")
    if not match:
        return False
    domain = match.group(1).lower()
    return any(domain == d or domain.endswith("." + d) for d in EMAIL_DOMAINS)


def domains_text():
    return " or ".join("@" + d for d in EMAIL_DOMAINS)


def send_email(to, subject, text):
    if not SMTP_HOST:
        raise ApiError(HTTPStatus.SERVICE_UNAVAILABLE, "This server can't send email yet. Ask your admin to set up email (run setup.sh again).")
    message = EmailMessage()
    message["From"] = f"SwVault {TEAM_NAME} <{SMTP_FROM}>"
    message["To"] = to
    message["Subject"] = subject
    message.set_content(text)
    context = ssl.create_default_context()
    try:
        if SMTP_PORT == 465:
            server = smtplib.SMTP_SSL(SMTP_HOST, SMTP_PORT, timeout=30, context=context)
        else:
            server = smtplib.SMTP(SMTP_HOST, SMTP_PORT, timeout=30)
            server.ehlo()
            if server.has_extn("starttls"):
                server.starttls(context=context)
                server.ehlo()
        with server:
            if SMTP_USER:
                server.login(SMTP_USER, SMTP_PASSWORD)
            server.send_message(message)
    except (smtplib.SMTPException, OSError) as e:
        log(f"sending email to {to} failed: {e!r}")
        raise ApiError(HTTPStatus.BAD_GATEWAY, "Couldn't send the email right now. Try again in a few minutes, or ask your admin.")


def start_verification(handler, body):
    key = handler.throttle_key("email")
    check_throttle(key)
    email = (body.get("email") or "").strip()
    if not EMAIL_DOMAINS:
        return HTTPStatus.OK, {"required": False}
    with _lock:
        invite = find_invite(load_invites(), body.get("code"))
    if not invite or not is_active(invite):
        record_failure(key)
        raise ApiError(HTTPStatus.NOT_FOUND, "That invite code isn't valid (it may have expired or been used up). Ask your admin for a new one.")
    if not email_domain_allowed(email):
        raise ApiError(HTTPStatus.BAD_REQUEST, f"Use your {domains_text()} email address.")
    if email_has_account(email):
        raise ApiError(HTTPStatus.CONFLICT, f"There's already an account for {email}. Choose \"I have an account\" and sign in, or ask your admin to reset your password.")

    address = email.lower()
    with _lock:
        sweep_verifications()
        pending = _verifications.get(address, {})
        sent = [t for t in pending.get("sent", []) if t > time.time() - 3600]
        if sent and sent[-1] > time.time() - RESEND_SECONDS:
            raise ApiError(HTTPStatus.TOO_MANY_REQUESTS, "We just sent a code. Check your inbox (and junk folder), or wait a minute to send another.")
        if len(sent) >= SENDS_PER_HOUR:
            raise ApiError(HTTPStatus.TOO_MANY_REQUESTS, "Too many codes sent to this address. Try again in an hour.")
        code = f"{secrets.randbelow(1_000_000):06d}"
        _verifications[address] = {
            "code": code, "invite": normalize_code(invite["code"]), "attempts": 0,
            "expires": time.time() + VERIFY_MINUTES * 60, "sent": sent + [time.time()],
        }
    send_email(email, f"Your {TEAM_NAME} SwVault code: {code}",
               f"Your code to join the {TEAM_NAME} vault is:\n\n    {code}\n\n"
               f"Type it into the SwVault window. It works for {VERIFY_MINUTES} minutes.\n\n"
               "If you didn't ask for this, you can ignore this email.\n")
    log(f"verification code sent to {address} for invite {invite['code']}")
    return HTTPStatus.OK, {"required": True, "sent": True, "minutes": VERIFY_MINUTES}


def check_verification(key, email, email_code, invite):
    """Call with _lock held. The caller removes the pending code once the account exists, so a
    taken user name doesn't cost the member their email code."""
    address = (email or "").strip().lower()
    pending = _verifications.get(address)
    if not pending or pending["expires"] < time.time() or pending["invite"] != normalize_code(invite["code"]):
        raise ApiError(HTTPStatus.BAD_REQUEST, "Get a code sent to your email first (or ask for a new one; codes last 15 minutes).")
    pending["attempts"] += 1
    if not hmac.compare_digest(pending["code"], re.sub(r"\D", "", email_code or "")):
        record_failure(key)
        if pending["attempts"] >= VERIFY_ATTEMPTS:
            del _verifications[address]
            raise ApiError(HTTPStatus.BAD_REQUEST, "Too many wrong codes. Ask for a new one.")
        raise ApiError(HTTPStatus.BAD_REQUEST, "That email code isn't right. Check the latest email we sent.")


def email_has_account(email):
    status, found = gitea("GET", "admin/emails/search?limit=10&q=" + urllib.request.quote(email))
    return status == 200 and any((e.get("email") or "").lower() == email.lower() for e in found or [])


# ------------------------------------------------------------------ Gitea

def admin_token():
    global _token
    try:
        mtime = os.stat(TOKEN_FILE).st_mtime_ns
    except FileNotFoundError:
        mtime = None
    if mtime is None or mtime != _token[0]:
        with open(TOKEN_FILE, encoding="utf-8") as f:
            _token = (mtime, f.read().strip())
    token = _token[1]
    if not token:
        raise ApiError(HTTPStatus.SERVICE_UNAVAILABLE, "The server isn't set up yet.")
    return token


def gitea(method, path, body=None, auth=None):
    """Calls the Gitea API (as the admin unless auth is given). Returns (status, json-or-None)."""
    data = json.dumps(body).encode() if body is not None else None
    request = urllib.request.Request(f"{GITEA}/api/v1/{path}", data=data, method=method)
    request.add_header("Accept", "application/json")
    if data is not None:
        request.add_header("Content-Type", "application/json")
    request.add_header("Authorization", auth or f"token {admin_token()}")
    try:
        with browser.ipv4_local_urlopen(request, timeout=30) as response:
            raw = response.read()
            return response.status, (json.loads(raw) if raw else None)
    except urllib.error.HTTPError as e:
        raw = e.read()
        try:
            payload = json.loads(raw) if raw else None
        except ValueError:
            payload = {"message": raw.decode(errors="replace")}
        return e.code, payload
    except urllib.error.URLError as e:
        raise ApiError(HTTPStatus.BAD_GATEWAY, f"The vault server isn't answering ({e.reason}).")


def team_ids():
    status, teams = gitea("GET", f"orgs/{ORG}/teams?limit=50")
    if status != 200:
        raise ApiError(HTTPStatus.BAD_GATEWAY, f"Couldn't list teams (HTTP {status}).")
    return {team["name"]: team["id"] for team in teams}


def team_id(name):
    found = cached("teams", 600, team_ids).get(name)
    if found is None:
        raise ApiError(HTTPStatus.INTERNAL_SERVER_ERROR, f"Team {name} is missing; run setup.sh again.")
    return found


def org_members():
    members, page = [], 1
    while True:
        status, batch = gitea("GET", f"orgs/{ORG}/members?limit=50&page={page}")
        if status != 200:
            raise ApiError(HTTPStatus.BAD_GATEWAY, f"Couldn't list members (HTTP {status}).")
        members += batch
        if len(batch) < 50:
            return members
        page += 1


def require_member(handler):
    """The caller's own credentials (user + SwVault token or password), checked against Gitea."""
    key = handler.throttle_key("signin")
    check_throttle(key, "Too many failed sign-ins. Try again in 15 minutes.")
    auth = handler.headers.get("Authorization", "")
    if not auth.lower().startswith(("basic ", "token ")):
        raise ApiError(HTTPStatus.UNAUTHORIZED, "Sign in first.")
    # SwVault agents poll several endpoints every couple of minutes; checking the same credentials
    # with Gitea each time (a password hash, for passwords) is most of the cost of those requests.
    signin = "signin:" + hashlib.sha256(auth.encode()).hexdigest()
    with _cache_lock:
        hit = _cache.get(signin)
    if hit and hit[0] > time.monotonic():
        return hit[1]
    status, user = gitea("GET", "user", auth=auth)
    if status != 200 or not user:
        record_failure(key)
        raise ApiError(HTTPStatus.UNAUTHORIZED, "Sign-in failed.")
    login = user["login"]
    status, _ = gitea("GET", f"orgs/{ORG}/members/{login}")
    if status not in (200, 204):
        raise ApiError(HTTPStatus.FORBIDDEN, f"{login} isn't on the {TEAM_NAME} team.")
    with _cache_lock:
        _cache[signin] = (time.monotonic() + SIGNIN_SECONDS, login)
    return login


def forget_signins():
    """After an account is disabled or its password reset: its credentials are checked again."""
    forget("signin:")


def is_admin(login):
    def ask():
        status, _ = gitea("GET", f"teams/{team_id('Owners')}/members/{urllib.request.quote(login)}")
        return status in (200, 204)
    return cached("admin:" + login.lower(), 30, ask)


def admin_logins():
    status, members = gitea("GET", f"teams/{team_id('Owners')}/members?limit=50")
    return [m["login"] for m in members or []] if status == 200 else []


def forget_roles():
    """After someone's role changes."""
    forget("admin:")
    forget("teams")


def require_admin(handler):
    login = require_member(handler)
    if not is_admin(login):
        raise ApiError(HTTPStatus.FORBIDDEN, f"{login} isn't a vault admin. Ask an admin to do this.")
    return login


# ------------------------------------------------------------------ handlers

def redeem(handler, body):
    ip = handler.client_ip()
    key = handler.throttle_key("redeem")
    check_throttle(key)
    code = body.get("code", "")
    username = (body.get("username") or "").strip()
    password = body.get("password") or ""
    full_name = (body.get("fullName") or "").strip()[:100]
    email = (body.get("email") or "").strip()
    member_type, subteam = clean_profile(body)

    with _lock:
        state = load_invites()
        invite = find_invite(state, code)
        if not invite or not is_active(invite):
            record_failure(key)
            raise ApiError(HTTPStatus.NOT_FOUND, "That invite code isn't valid (it may have expired or been used up). Ask your admin for a new one.")
        if not USERNAME_RE.match(username) or ".." in username:
            raise ApiError(HTTPStatus.BAD_REQUEST, "User names can use letters, digits and - . _ (up to 40 characters), and must start and end with a letter or digit.")
        if len(password) < 8:
            raise ApiError(HTTPStatus.BAD_REQUEST, "Choose a password of at least 8 characters.")
        if EMAIL_DOMAINS:
            if not email_domain_allowed(email):
                raise ApiError(HTTPStatus.BAD_REQUEST, f"Use your {domains_text()} email address.")
            check_verification(key, email, body.get("emailCode"), invite)
        else:
            email = ""

        status, payload = gitea("POST", "admin/users", {
            "username": username, "login_name": username, "email": email or f"{username}@noreply.localhost",
            "password": password, "full_name": full_name, "must_change_password": False,
            "send_notify": False, "visibility": "private",
        })
        text = json.dumps(payload or {}).lower()
        if status == 422 and "email" in text and ("already" in text or "used" in text):
            raise ApiError(HTTPStatus.CONFLICT, f"There's already an account for {email}. Sign in with it instead, or ask your admin to reset your password.")
        if status == 422 and "already exists" in text:
            raise ApiError(HTTPStatus.CONFLICT, f"The user name {username} is taken. Pick another one.")
        if status == 422:
            raise ApiError(HTTPStatus.BAD_REQUEST, "The server didn't accept that user name or password: " + (payload or {}).get("message", ""))
        if status not in (200, 201):
            raise ApiError(HTTPStatus.BAD_GATEWAY, f"Couldn't create the account (HTTP {status}).")

        team = ROLES.get(invite["role"], "Designers")
        status, _ = gitea("PUT", f"teams/{team_id(team)}/members/{username}")
        if status not in (200, 204):
            raise ApiError(HTTPStatus.BAD_GATEWAY, f"The account was created but couldn't be added to {team} (HTTP {status}). Ask your admin.")

        invite["usesLeft"] -= 1
        invite.setdefault("usedBy", []).append({"user": username, "email": email or None, "at": stamp()})
        save(INVITES_FILE, state)
        _verifications.pop(email.lower(), None)
    set_profile(username, member_type, subteam, approved=False)
    log(f"invite {invite['code']} redeemed by {username} ({team}, {member_type}{' ' + subteam if subteam else ''}) from {ip}")
    if member_type == "lead":
        tell_admins(f"{full_name or username} wants to be the {subteam} lead",
                    f"{full_name or username} ({username}) just joined {TEAM_NAME} and says they lead {subteam}.\n")
    return HTTPStatus.CREATED, {"username": username, "team": TEAM_NAME, "role": invite["role"]}


def describe_invite(code):
    invite = find_invite(load_invites(), code)
    valid = bool(invite and is_active(invite))
    result = {"team": TEAM_NAME, "valid": valid, "role": invite["role"] if valid else None, "emailDomains": EMAIL_DOMAINS}
    if valid:
        result["subteams"] = known_subteams(load_people())
    return HTTPStatus.OK, result


def list_people():
    profiles = load_people()["people"]
    people = []
    for member in org_members():
        profile = profiles.get(member["login"].lower(), {})
        people.append({
            "login": member["login"], "fullName": member.get("full_name") or "",
            "memberType": profile.get("memberType"), "subteam": profile.get("subteam") or "",
            "pendingLead": (profile.get("pendingLead") or {}).get("subteam"),
        })
    people.sort(key=lambda p: (p["memberType"] != "lead", (p["subteam"] or "").lower(), (p["fullName"] or p["login"]).lower()))
    return HTTPStatus.OK, {"people": people, "subteams": known_subteams({"people": profiles})}


def update_profile(login, body, approved):
    member_type, subteam = clean_profile(body)
    profile = set_profile(login, member_type, subteam, approved)
    pending = (profile.get("pendingLead") or {}).get("subteam")
    if pending:
        log(f"{login} asked to be the {pending} lead (waiting for an admin)")
        tell_admins(f"{display_name(login)} wants to be the {pending} lead",
                    f"{display_name(login)} ({login}) asked to be the {pending} subteam lead.\n")
    else:
        log(f"{login} is now {member_type}{' of ' + subteam if subteam else ''}")
    return HTTPStatus.OK, {"login": login, "memberType": profile.get("memberType"), "subteam": profile.get("subteam") or "",
                           "pendingLead": pending}


# ------------------------------------------------------------------ projects, subsystems, engineers

# A project (e.g. "2027 Car"; "car" in the data and the API) has subsystems (e.g. "Front
# Suspension"), each a folder in the vault.
# Members add them; responsible engineers (several per subsystem) are approved by an admin.

def load_cars():
    return load(SUBSYSTEMS_FILE, {"cars": []})


def clean_name(text, what):
    name = re.sub(r"\s+", " ", (text or "")).strip()[:60]
    if not name:
        raise ApiError(HTTPStatus.BAD_REQUEST, f"Give the {what} a name.")
    return name


def clean_folder(text, default):
    """A vault-relative folder with forward slashes, safe on Windows."""
    raw = ((text or "").strip() or default).replace("\\", "/")
    parts = [re.sub(r'[<>:"|?*\x00-\x1f]', "", p).strip().rstrip(".") for p in raw.split("/")]
    parts = [p for p in parts if p and p != ".."]
    if not parts or parts[0].lower() in (".swvault", ".swvault-local"):
        raise ApiError(HTTPStatus.BAD_REQUEST, "Pick a folder inside the vault.")
    return "/".join(parts)[:200]


def clean_subfolders(value):
    """Folders inside a subsystem's folder ("Fasteners", "COTS Parts/Bearings"), without duplicates."""
    result = []
    for item in (value if isinstance(value, list) else [])[:50]:
        try:
            name = clean_folder(str(item), "")
        except ApiError:
            continue
        if name.lower() not in (r.lower() for r in result):
            result.append(name)
    return result


def find_car(cars, car_id):
    car = next((c for c in cars["cars"] if c["id"] == car_id), None)
    if not car:
        raise ApiError(HTTPStatus.NOT_FOUND, "No such project.")
    return car


def find_subsystem(cars, subsystem_id):
    for car in cars["cars"]:
        for subsystem in car["subsystems"]:
            if subsystem["id"] == subsystem_id:
                return car, subsystem
    raise ApiError(HTTPStatus.NOT_FOUND, "No such subsystem.")


def subsystem_for(path):
    """The (car, subsystem) whose folder holds this vault path; the deepest folder wins."""
    best = None
    lower = path.lower()
    for car in load_cars()["cars"]:
        for subsystem in car["subsystems"]:
            folder = subsystem["folder"].lower().rstrip("/") + "/"
            if lower.startswith(folder) and (best is None or len(folder) > len(best[1]["folder"]) + 1):
                best = (car, subsystem)
    return best


def list_cars():
    return HTTPStatus.OK, load_cars()


def add_car(login, body):
    name = clean_name(body.get("name"), "project")
    folder = clean_folder(body.get("folder"), name)
    with _lock:
        cars = load_cars()
        if any(c["name"].lower() == name.lower() for c in cars["cars"]):
            raise ApiError(HTTPStatus.CONFLICT, f"There's already a project called {name}.")
        car = {"id": "c-" + secrets.token_hex(4), "name": name, "folder": folder, "createdBy": login, "created": stamp(), "subsystems": []}
        cars["cars"].append(car)
        save(SUBSYSTEMS_FILE, cars)
    log(f"{login} added project {name} ({folder})")
    return HTTPStatus.CREATED, car


def may_edit(login, item):
    """Admins, and whoever added it."""
    if (item.get("createdBy") or "").lower() != login.lower() and not is_admin(login):
        raise ApiError(HTTPStatus.FORBIDDEN, "Only a vault admin or the person who added it can change this.")


def edit_car(login, car_id, body):
    with _lock:
        cars = load_cars()
        car = find_car(cars, car_id)
        may_edit(login, car)
        name = clean_name(body.get("name") or car["name"], "project")
        folder = clean_folder(body.get("folder"), car["folder"])
        if any(c is not car and c["name"].lower() == name.lower() for c in cars["cars"]):
            raise ApiError(HTTPStatus.CONFLICT, f"There's already a project called {name}.")
        was = car["name"]
        car.update(name=name, folder=folder)
        save(SUBSYSTEMS_FILE, cars)
    log(f"{login} changed project {was} to {name} ({folder})")
    return HTTPStatus.OK, car


def remove_car(login, car_id):
    with _lock:
        cars = load_cars()
        car = find_car(cars, car_id)
        may_edit(login, car)
        cars["cars"].remove(car)
        save(SUBSYSTEMS_FILE, cars)
    log(f"{login} removed project {car['name']} and its {len(car['subsystems'])} subsystem(s)")
    return HTTPStatus.OK, car


def edit_subsystem(login, subsystem_id, body):
    with _lock:
        cars = load_cars()
        car, subsystem = find_subsystem(cars, subsystem_id)
        may_edit(login, subsystem)
        name = clean_name(body.get("name") or subsystem["name"], "subsystem")
        folder = clean_folder(body.get("folder"), subsystem["folder"])
        if any(s is not subsystem and s["name"].lower() == name.lower() for s in car["subsystems"]):
            raise ApiError(HTTPStatus.CONFLICT, f"{car['name']} already has a {name} subsystem.")
        taken = next((s for c in cars["cars"] for s in c["subsystems"] if s is not subsystem and s["folder"].lower() == folder.lower()), None)
        if taken:
            raise ApiError(HTTPStatus.CONFLICT, f"The folder {folder} already belongs to the {taken['name']} subsystem.")
        was = subsystem["name"]
        subsystem.update(name=name, folder=folder)
        save(SUBSYSTEMS_FILE, cars)
    log(f"{login} changed subsystem {car['name']} / {was} to {name} ({folder})")
    return HTTPStatus.OK, subsystem


def add_subsystem_folder(login, subsystem_id, body):
    name = clean_folder(body.get("name"), "")
    with _lock:
        cars = load_cars()
        car, subsystem = find_subsystem(cars, subsystem_id)
        folders = subsystem.setdefault("folders", [])
        if name.lower() in (x.lower() for x in folders):
            return HTTPStatus.OK, subsystem
        if len(folders) >= 50:
            raise ApiError(HTTPStatus.BAD_REQUEST, f"{subsystem['name']} already has 50 folders.")
        folders.append(name)
        save(SUBSYSTEMS_FILE, cars)
    log(f"{login} added folder {name} to {car['name']} / {subsystem['name']}")
    return HTTPStatus.CREATED, subsystem


def remove_subsystem_folder(login, subsystem_id, name):
    with _lock:
        cars = load_cars()
        car, subsystem = find_subsystem(cars, subsystem_id)
        if not any(e["login"].lower() == login.lower() and e["status"] == "approved" for e in subsystem["engineers"]):
            may_edit(login, subsystem)
        folders = subsystem.get("folders", [])
        kept = [x for x in folders if x.lower() != (name or "").strip().lower()]
        if len(kept) == len(folders):
            raise ApiError(HTTPStatus.NOT_FOUND, f"{subsystem['name']} has no folder called {name}.")
        subsystem["folders"] = kept
        save(SUBSYSTEMS_FILE, cars)
    log(f"{login} removed folder {name} from {car['name']} / {subsystem['name']}")
    return HTTPStatus.OK, subsystem


def remove_subsystem(login, subsystem_id):
    with _lock:
        cars = load_cars()
        car, subsystem = find_subsystem(cars, subsystem_id)
        may_edit(login, subsystem)
        car["subsystems"].remove(subsystem)
        save(SUBSYSTEMS_FILE, cars)
    log(f"{login} removed subsystem {car['name']} / {subsystem['name']}")
    return HTTPStatus.OK, subsystem


def add_subsystem(login, car_id, body):
    name = clean_name(body.get("name"), "subsystem")
    with _lock:
        cars = load_cars()
        car = find_car(cars, car_id)
        folder = clean_folder(body.get("folder"), car["folder"] + "/" + name)
        if any(s["name"].lower() == name.lower() for s in car["subsystems"]):
            raise ApiError(HTTPStatus.CONFLICT, f"{car['name']} already has a {name} subsystem.")
        taken = next((s for c in cars["cars"] for s in c["subsystems"] if s["folder"].lower() == folder.lower()), None)
        if taken:
            raise ApiError(HTTPStatus.CONFLICT, f"The folder {folder} already belongs to the {taken['name']} subsystem.")
        subsystem = {"id": "s-" + secrets.token_hex(4), "name": name, "folder": folder, "createdBy": login, "created": stamp(),
                     "engineers": [], "folders": clean_subfolders(body.get("folders"))}
        car["subsystems"].append(subsystem)
        save(SUBSYSTEMS_FILE, cars)
    log(f"{login} added subsystem {name} to {car['name']} ({folder})")
    return HTTPStatus.CREATED, subsystem


def claim_engineer(login, subsystem_id):
    """Ask to be a responsible engineer. Admins are approved at once; everyone else waits."""
    approved = is_admin(login)
    with _lock:
        cars = load_cars()
        car, subsystem = find_subsystem(cars, subsystem_id)
        entry = next((e for e in subsystem["engineers"] if e["login"].lower() == login.lower()), None)
        if entry and entry["status"] == "approved":
            return HTTPStatus.OK, subsystem
        if entry is None:
            entry = {"login": login}
            subsystem["engineers"].append(entry)
        entry.update(status="approved" if approved else "pending", requested=stamp())
        if approved:
            entry.update(decidedBy=login, decided=stamp())
        save(SUBSYSTEMS_FILE, cars)
    log(f"{login} {'is now' if approved else 'asked to be'} responsible engineer of {car['name']} / {subsystem['name']}")
    if not approved:
        tell_admins(f"{display_name(login)} wants to be responsible engineer of {subsystem['name']}",
                    f"{display_name(login)} ({login}) asked to be a responsible engineer of {car['name']} / {subsystem['name']} "
                    f"(folder {subsystem['folder']}).\n")
    return HTTPStatus.OK, subsystem


def remove_engineer(caller, subsystem_id, login):
    if caller.lower() != login.lower() and not is_admin(caller):
        raise ApiError(HTTPStatus.FORBIDDEN, "Only admins can remove someone else as responsible engineer.")
    with _lock:
        cars = load_cars()
        car, subsystem = find_subsystem(cars, subsystem_id)
        before = len(subsystem["engineers"])
        subsystem["engineers"] = [e for e in subsystem["engineers"] if e["login"].lower() != login.lower()]
        if len(subsystem["engineers"]) == before:
            raise ApiError(HTTPStatus.NOT_FOUND, f"{login} isn't a responsible engineer of {subsystem['name']}.")
        save(SUBSYSTEMS_FILE, cars)
    log(f"{caller} removed {login} as responsible engineer of {car['name']} / {subsystem['name']}")
    return HTTPStatus.OK, subsystem


def pending_approvals():
    people = load_people()["people"].values()
    leads = [{"login": p["login"], "fullName": display_name(p["login"]), "subteam": p["pendingLead"]["subteam"],
              "requested": p["pendingLead"].get("requested")} for p in people if p.get("pendingLead")]
    engineers = [{"login": e["login"], "fullName": display_name(e["login"]), "carId": car["id"], "car": car["name"],
                  "subsystemId": s["id"], "subsystem": s["name"], "folder": s["folder"], "requested": e.get("requested")}
                 for car in load_cars()["cars"] for s in car["subsystems"] for e in s["engineers"] if e["status"] == "pending"]
    return HTTPStatus.OK, {"leads": leads, "engineers": engineers}


def decide(admin, body):
    kind = (body.get("kind") or "").lower()
    login = (body.get("login") or "").strip()
    approve = body.get("approve") is True
    verdict = "approved" if approve else "declined"
    if kind == "lead":
        with _lock:
            people = load_people()
            profile = people["people"].get(login.lower())
            pending = (profile or {}).get("pendingLead")
            if not pending:
                raise ApiError(HTTPStatus.NOT_FOUND, f"{login} hasn't asked to be a lead.")
            if approve:
                profile.update(memberType="lead", subteam=pending["subteam"], approvedBy=admin, updated=stamp())
            profile.pop("pendingLead", None)
            save(PEOPLE_FILE, people)
        what = f"the {pending['subteam']} lead"
    elif kind == "engineer":
        with _lock:
            cars = load_cars()
            car, subsystem = find_subsystem(cars, body.get("subsystem") or "")
            entry = next((e for e in subsystem["engineers"] if e["login"].lower() == login.lower() and e["status"] == "pending"), None)
            if not entry:
                raise ApiError(HTTPStatus.NOT_FOUND, f"{login} hasn't asked to be responsible engineer of {subsystem['name']}.")
            if approve:
                entry.update(status="approved", decidedBy=admin, decided=stamp())
            else:
                subsystem["engineers"].remove(entry)
            save(SUBSYSTEMS_FILE, cars)
        what = f"a responsible engineer of {car['name']} / {subsystem['name']}"
    else:
        raise ApiError(HTTPStatus.BAD_REQUEST, "kind must be lead or engineer.")
    log(f"{admin} {verdict} {login} as {what}")
    email_quietly(login, f"You're now {what}" if approve else f"Request declined: {what}",
                  f"{display_name(admin)} {verdict} your request to be {what}.\n")
    return HTTPStatus.OK, {"login": login, "kind": kind, "approved": approve}


def email_quietly(login, subject, text):
    """Best effort, in the background: requests and decisions are saved whether or not email works."""
    if not SMTP_HOST:
        return

    def run():
        try:
            address = user_email(login)
            if address:
                send_email(address, f"[{TEAM_NAME}] {subject}", text)
        except ApiError as e:
            log(f"email to {login} failed: {e.message}")
        except Exception as e:
            log(f"email to {login} failed: {e!r}")
    _mailer.submit(run)


def tell_admins(subject, text):
    if not SMTP_HOST:
        return

    def run():
        try:
            admins = admin_logins()
        except Exception as e:
            log(f"couldn't list admins to email: {e!r}")
            return
        for admin in admins:
            email_quietly(admin, subject, text + "\nApprove or decline it in SwVault: tray icon > Approvals.\n")
    _mailer.submit(run)


# ------------------------------------------------------------------ review emails

REVIEW_HEADER_RE = re.compile(r"<!--\s*swvault-review\s+(\{.*?\})\s*-->", re.S)


def user_info(login):
    """The Gitea account (as the admin sees it), or None. Remembered for a minute."""
    def ask():
        status, user = gitea("GET", f"users/{urllib.request.quote(login)}")
        return user if status == 200 and user else None
    return cached("user:" + login.lower(), 60, ask)


def user_email(login):
    """The address to notify, or None (accounts added by the admin without --email have none)."""
    email = (user_info(login) or {}).get("email") or ""
    return None if not email or email.endswith("@noreply.localhost") else email


def display_name(login):
    return (user_info(login) or {}).get("full_name") or login


def notify_review(caller, number):
    """
    Called by SwVault right after a review request is opened or answered. The event is worked out
    from the issue itself (not from the caller), and each one is emailed once.
    """
    status, issue = gitea("GET", f"repos/{ORG}/{REPO}/issues/{number}")
    if status != 200 or not issue:
        raise ApiError(HTTPStatus.NOT_FOUND, "No such review request.")
    header = REVIEW_HEADER_RE.search(issue.get("body") or "")
    if not header:
        raise ApiError(HTTPStatus.BAD_REQUEST, "That isn't a SwVault review request.")
    info = json.loads(header.group(1))
    requester = (issue.get("user") or {}).get("login", "")
    leads = [a["login"] for a in issue.get("assignees") or []]
    if caller.lower() not in [requester.lower()] + [l.lower() for l in leads]:
        raise ApiError(HTTPStatus.FORBIDDEN, "Only the requester and the lead can do that.")
    labels = {l["name"] for l in issue.get("labels") or []}
    lead = leads[0] if leads else ""
    kind = {"simulation": "simulation", "drawing": "drawing"}.get(info.get("kind"), "design")
    path, version = info.get("path", ""), info.get("version", 0)
    file_name = path.rsplit("/", 1)[-1]
    web = issue.get("html_url") or ""
    how = "Open it in SOLIDWORKS: SwVault tab > Reviews (or the SwVault tray icon > Reviews)."

    if "review: cancelled" in labels:
        return HTTPStatus.OK, {"sent": False}
    def latest_from_lead(marker):
        """The lead's newest comment and its text after the SwVault marker (e.g. "Approved")."""
        _, comments = gitea("GET", f"repos/{ORG}/{REPO}/issues/{number}/comments")
        from_lead = [c for c in (comments or []) if (c.get("user") or {}).get("login", "").lower() == lead.lower()]
        latest = from_lead[-1] if from_lead else None
        return latest, re.sub(r"^.*?" + marker + r"\**\s*", "", (latest or {}).get("body", ""), count=1, flags=re.S).strip()

    if "review: approved" in labels:
        _, note = latest_from_lead("Approved")
        event, to_login = "approved", requester
        subject = f"Approved: {kind} review of {file_name}"
        text = (f"{display_name(lead)} approved your {kind} review request for {path} (version {version}).\n"
                + ("\n" + "\n".join("    " + line for line in note.splitlines()) + "\n" if note else ""))
    elif "review: changes requested" in labels:
        latest, feedback = latest_from_lead("Changes requested")
        event, to_login = f"changes:{latest['id'] if latest else 0}", requester
        subject = f"Changes requested: {kind} review of {file_name}"
        text = (f"{display_name(lead)} reviewed {path} (version {version}) and asked for changes:\n\n"
                + "\n".join("    " + line for line in (feedback or "(no details given)").splitlines()) + "\n\n"
                "Make the changes, check the file in, and request another review.\n")
    else:
        event, to_login = "opened", lead
        message = "\n".join(line[1:].strip() for line in (issue.get("body") or "").splitlines() if line.startswith(">"))
        subject = f"{display_name(requester)} asked you for a {kind} review of {file_name}"
        text = (f"{display_name(requester)} asked you, as a subteam lead, for a {kind} review of {path} (version {version}).\n"
                + (f"\nTheir note:\n" + "\n".join("    " + l for l in message.splitlines()) + "\n" if message else ""))

    footer = f"\n{how}\n" + (f"On the web: {web}\n" if web else "")
    result = {"sent": False}
    if send_review_email_once(f"{number}:{event}", to_login, f"[{TEAM_NAME}] {subject}", text + footer, number, event):
        result = {"sent": True, "to": to_login}

    if event == "opened":
        found, engineers = review_cc(requester, lead, path)
        if found:
            car, subsystem = found
            cc_text = (f"{display_name(requester)} asked {display_name(lead)} for a {kind} review of {path} (version {version}).\n"
                       f"You're cc'd as a responsible engineer of {car['name']} / {subsystem['name']}.\n")
            result["cc"] = [e for e in engineers
                            if send_review_email_once(f"{number}:cc:{e.lower()}", e, f"[{TEAM_NAME}] cc: {subject}", cc_text + footer, number, "cc")]
    return HTTPStatus.OK, result


def review_cc(requester, lead, path):
    """
    Responsible engineers to cc on a new review request: general members' requests about a file in
    a subsystem go to that subsystem's approved engineers too (not to the requester or the lead).
    """
    if load_people()["people"].get(requester.lower(), {}).get("memberType") == "lead":
        return None, []
    found = subsystem_for(path)
    if not found:
        return None, []
    skip = {requester.lower(), lead.lower()}
    return found, [e["login"] for e in found[1]["engineers"] if e["status"] == "approved" and e["login"].lower() not in skip]


def send_review_email_once(key, to_login, subject, text, number, event):
    with _lock:
        if key in load(NOTIFIED_FILE, {"sent": []})["sent"]:
            return False
    address = user_email(to_login) if to_login else None
    if not address:
        log(f"review #{number} {event}: {to_login or 'nobody'} has no email address; not emailed")
        return False
    send_email(address, subject, text)
    with _lock:
        notified = load(NOTIFIED_FILE, {"sent": []})
        notified["sent"] = (notified["sent"] + [key])[-5000:]
        save(NOTIFIED_FILE, notified)
    log(f"review #{number} {event}: emailed {to_login}")
    return True


def create_invite(admin, body):
    role = (body.get("role") or "designer").lower()
    if role not in ROLES:
        raise ApiError(HTTPStatus.BAD_REQUEST, "role must be designer or viewer.")
    uses = int(body.get("uses") or 1)
    days = int(body.get("days") or 7)
    if not 1 <= uses <= 500 or not 1 <= days <= 90:
        raise ApiError(HTTPStatus.BAD_REQUEST, "uses must be 1-500 and days 1-90.")
    invite = {
        "code": new_code(), "role": role, "uses": uses, "usesLeft": uses,
        "expires": (now() + timedelta(days=days)).isoformat(timespec="seconds"),
        "note": (body.get("note") or "").strip()[:200], "createdBy": admin, "createdAt": stamp(),
    }
    with _lock:
        state = load_invites()
        # Drop long-dead invites so the file doesn't grow forever.
        cutoff = now() - timedelta(days=180)
        state["invites"] = [i for i in state["invites"] if datetime.fromisoformat(i["expires"]) > cutoff]
        state["invites"].append(invite)
        save(INVITES_FILE, state)
    log(f"invite {invite['code']} created by {admin}: {role}, {uses} use(s), {days} day(s)")
    return HTTPStatus.CREATED, public_invite(invite)


def package_version(path):
    """The SwVault version inside an installer zip (its swvault-package.json), or None."""
    try:
        with zipfile.ZipFile(path) as z:
            names = {n.replace("\\", "/"): n for n in z.namelist()}
            if "swvault-package.json" in names:
                version = json.loads(z.read(names["swvault-package.json"]).decode("utf-8-sig")).get("version")
                return str(version) if version and re.fullmatch(r"\d+(\.\d+){1,3}", str(version)) else None
            # Packages from before swvault-package.json: still a SwVault installer, version unknown.
            return "0.0.0" if any(n.endswith("install.ps1") for n in names) else None
    except (zipfile.BadZipFile, ValueError, OSError):
        return None


def latest_installer():
    if not os.path.exists(INSTALLER_FILE):
        raise ApiError(HTTPStatus.NOT_FOUND, "Your admin hasn't published the installer yet.")
    meta = load(INSTALLER_META, {})
    return HTTPStatus.OK, {k: meta.get(k) for k in ("version", "sha256", "size", "published", "required", "notes")}


def join_page(code):
    """What an invite link shows in a browser: the setup steps and a download button."""
    meta = load(INSTALLER_META, {})
    wizard = os.path.exists(SETUP_FILE)
    size = os.path.getsize(SETUP_FILE if wizard else INSTALLER_FILE)
    team = html.escape(TEAM_NAME)
    dashed = f"{code[0:4]}-{code[4:8]}-{code[8:12]}" if len(code) == 12 else ""
    invite = find_invite(load_invites(), code) if dashed else None
    if dashed and invite and is_active(invite):
        access = "view and download files" if invite["role"] == "viewer" else "check files out and in"
        lead = f"<p class=lead>You're invited to the <b>{team}</b> vault. With this invite you can {access}.</p>"
    elif dashed:
        lead = ("<p class=warn>This invite has expired or has been used up. Ask a team admin for a new one. "
                "If you already have an account, you can still install SwVault and sign in.</p>")
    else:
        lead = f"<p class=lead>SwVault for the <b>{team}</b> vault.</p>"
    if wizard:
        run = ("Open the downloaded <b>SwVault Setup</b> and follow the wizard. If Windows shows "
               "&ldquo;Windows protected your PC&rdquo;, click <b>More info</b>, then <b>Run anyway</b>. "
               "Approve the administrator prompt.")
    else:
        run = ("Right-click the downloaded zip and choose <b>Extract All</b> (keep the folder name), then "
               "double-click <b>Install SwVault.cmd</b> in the extracted folder. Approve the administrator prompt.")
    account = ["choose <b>I'm new and have an invite</b>"]
    if dashed:
        account.append(f"the invite code is filled in for you (it is <code>{dashed}</code>)")
    if EMAIL_DOMAINS:
        account.append("verify your " + html.escape(" or ".join("@" + d for d in EMAIL_DOMAINS)) + " email with the code SwVault sends you")
    account.append("pick a user name and password")
    link = "download?format=exe" + (f"&amp;invite={dashed}" if dashed else "")
    version = html.escape(str(meta.get("version") or ""))
    megabytes = max(1, round(size / 1024 ** 2))
    return f"""<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>SwVault for {team}</title>
<style>
:root {{ color-scheme: light dark; --fg: #1c2128; --muted: #5b6470; --bg: #f5f6f8; --card: #fff; --line: #dde1e6; --accent: #0b5cad; --warn: #8a4b00; --warnbg: #fff4dc; }}
@media (prefers-color-scheme: dark) {{ :root {{ --fg: #e6e9ee; --muted: #9aa4b1; --bg: #14171b; --card: #1d2127; --line: #2f353d; --accent: #2f7fd6; --warn: #f1c27a; --warnbg: #3a2c12; }} }}
body {{ margin: 0; padding: 32px 16px; background: var(--bg); color: var(--fg); font: 16px/1.55 "Segoe UI", system-ui, sans-serif; }}
main {{ max-width: 640px; margin: 0 auto; background: var(--card); border: 1px solid var(--line); border-radius: 10px; padding: 28px 32px 24px; }}
h1 {{ margin: 0 0 4px; font-size: 26px; }}
h2 {{ margin: 28px 0 8px; font-size: 18px; }}
.lead {{ margin: 0 0 20px; color: var(--muted); }}
.warn {{ margin: 0 0 20px; padding: 10px 14px; border-radius: 6px; background: var(--warnbg); color: var(--warn); }}
.button {{ display: inline-block; padding: 12px 22px; border-radius: 6px; background: var(--accent); color: #fff; font-weight: 600; text-decoration: none; }}
.button:hover, .button:focus {{ filter: brightness(1.1); }}
.meta {{ margin: 8px 0 0; color: var(--muted); font-size: 14px; }}
ol {{ margin: 0; padding-left: 22px; }}
li {{ margin: 0 0 10px; }}
code {{ padding: 1px 6px; border: 1px solid var(--line); border-radius: 4px; font: 15px Consolas, monospace; }}
footer {{ margin-top: 24px; padding-top: 14px; border-top: 1px solid var(--line); color: var(--muted); font-size: 14px; }}
</style>
</head>
<body>
<main>
<h1>SwVault</h1>
{lead}
<a class="button" href="{link}">Download SwVault {version}</a>
<p class="meta">For Windows 10 or 11 with SOLIDWORKS. {megabytes} MB.</p>
<h2>Set it up</h2>
<ol>
<li>Close SOLIDWORKS.</li>
<li>{run}</li>
<li>In the SwVault window that opens, {", ".join(account)}. Already have an account? Choose <b>I have an account</b> and sign in.</li>
<li>Your team's files download by themselves; the SwVault icon near the clock says when they are all there.</li>
<li>Open SOLIDWORKS. The <b>SwVault</b> tab and task pane are ready.</li>
</ol>
<footer>No SwVault tab in SOLIDWORKS? Go to Tools &gt; Add-Ins and tick SwVault. For anything else, ask a team admin.</footer>
</main>
</body>
</html>
"""


def list_invites():
    return HTTPStatus.OK, [public_invite(i) for i in load_invites()["invites"] if is_active(i)]


def revoke_invite(admin, code):
    with _lock:
        state = load_invites()
        invite = find_invite(state, code)
        if not invite:
            raise ApiError(HTTPStatus.NOT_FOUND, "No such invite.")
        invite["usesLeft"] = 0
        save(INVITES_FILE, state)
    log(f"invite {invite['code']} revoked by {admin}")
    return HTTPStatus.OK, public_invite(invite)


def test_email(admin, body):
    to = (body.get("to") or "").strip()
    if not EMAIL_RE.match(to):
        raise ApiError(HTTPStatus.BAD_REQUEST, "Give an email address to send the test to.")
    send_email(to, f"SwVault {TEAM_NAME}: test email", f"Email from your SwVault server works. (Sent by {admin}.)\n")
    return HTTPStatus.OK, {"sent": True}


class Handler(BaseHTTPRequestHandler):
    server_version = "SwVaultTeam/1"

    def log_message(self, fmt, *args):
        pass  # requests are logged by the handlers that matter

    def client_ip(self):
        # The proxy (Tailscale) appends the real client address; earlier entries come from the client.
        forwarded = self.headers.get("X-Forwarded-For", "")
        return forwarded.split(",")[-1].strip() or self.client_address[0]

    def throttle_key(self, bucket):
        # Internet traffic can only arrive through Tailscale Funnel, which adds X-Forwarded-For.
        # Without it, the request came from this machine (swvault-admin.sh via 127.0.0.1:3100).
        if not self.headers.get("X-Forwarded-For"):
            return None
        return f"{bucket}:{self.client_ip()}"

    def route(self):
        path = self.path.split("?", 1)[0]
        if path.startswith(PREFIX):
            path = path[len(PREFIX):]  # Tailscale may or may not strip the mount point
        elif path == BROWSER_PREFIX or path.startswith(BROWSER_PREFIX + "/"):
            path = path[len(BROWSER_PREFIX):]
        return "/" + urllib.request.unquote(path).strip("/")

    def query(self, name):
        if "?" not in self.path:
            return None
        for part in self.path.split("?", 1)[1].split("&"):
            key, _, value = part.partition("=")
            if key == name:
                return urllib.request.unquote(value)
        return None

    def read_json(self):
        length = int(self.headers.get("Content-Length") or 0)
        if length > 64 * 1024:
            raise ApiError(HTTPStatus.REQUEST_ENTITY_TOO_LARGE, "Request too large.")
        raw = self.rfile.read(length) if length else b"{}"
        try:
            body = json.loads(raw or b"{}")
        except ValueError:
            raise ApiError(HTTPStatus.BAD_REQUEST, "Expected JSON.")
        if not isinstance(body, dict):
            raise ApiError(HTTPStatus.BAD_REQUEST, "Expected a JSON object.")
        return body

    def send_json(self, status, payload):
        data = json.dumps(payload).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(data)

    def handle_request(self, method):
        try:
            route = self.route()
            if browser.handles(route):
                return browser.handle(self, method, route)
            if method == "GET" and route == "/health":
                result = (HTTPStatus.OK, {"ok": True, "emailDomains": EMAIL_DOMAINS, "emailConfigured": bool(SMTP_HOST)})
            elif method == "GET" and route.startswith("/invite/"):
                result = describe_invite(route[len("/invite/"):])
            elif method == "POST" and route == "/verify":
                result = start_verification(self, self.read_json())
            elif method == "POST" and route == "/redeem":
                result = redeem(self, self.read_json())
            elif method == "GET" and route == "/download":
                return self.download()
            elif method == "GET" and route == "/installer/latest":
                result = latest_installer()
            elif method == "GET" and route == "/people":
                require_member(self)
                result = list_people()
            elif method == "PUT" and route == "/people/me":
                login = require_member(self)
                result = update_profile(login, self.read_json(), approved=is_admin(login))
            elif method == "PUT" and route.startswith("/people/"):
                require_admin(self)  # admins can set anyone's profile (e.g. mark the subteam leads)
                result = update_profile(route[len("/people/"):], self.read_json(), approved=True)
            elif method == "GET" and route == "/cars":
                require_member(self)
                result = list_cars()
            elif method == "POST" and route == "/cars":
                result = add_car(require_member(self), self.read_json())
            elif method == "PUT" and re.fullmatch(r"/cars/[^/]+", route):
                result = edit_car(require_member(self), route.split("/")[2], self.read_json())
            elif method == "DELETE" and re.fullmatch(r"/cars/[^/]+", route):
                result = remove_car(require_member(self), route.split("/")[2])
            elif method == "POST" and re.fullmatch(r"/subsystems/[^/]+/folders", route):
                result = add_subsystem_folder(require_member(self), route.split("/")[2], self.read_json())
            elif method == "DELETE" and re.fullmatch(r"/subsystems/[^/]+/folders", route):
                result = remove_subsystem_folder(require_member(self), route.split("/")[2], self.query("name"))
            elif method == "PUT" and re.fullmatch(r"/subsystems/[^/]+", route):
                result = edit_subsystem(require_member(self), route.split("/")[2], self.read_json())
            elif method == "DELETE" and re.fullmatch(r"/subsystems/[^/]+", route):
                result = remove_subsystem(require_member(self), route.split("/")[2])
            elif method == "POST" and re.fullmatch(r"/cars/[^/]+/subsystems", route):
                result = add_subsystem(require_member(self), route.split("/")[2], self.read_json())
            elif method == "POST" and re.fullmatch(r"/subsystems/[^/]+/engineers/me", route):
                result = claim_engineer(require_member(self), route.split("/")[2])
            elif method == "DELETE" and re.fullmatch(r"/subsystems/[^/]+/engineers/[^/]+", route):
                parts = route.split("/")
                result = remove_engineer(require_member(self), parts[2], parts[4])
            elif method == "GET" and route == "/approvals":
                require_admin(self)
                result = pending_approvals()
            elif method == "POST" and route == "/approvals":
                result = decide(require_admin(self), self.read_json())
            elif method == "POST" and re.fullmatch(r"/reviews/\d+/notify", route):
                result = notify_review(require_member(self), int(route.split("/")[2]))
            elif method == "POST" and route == "/invites":
                result = create_invite(require_admin(self), self.read_json())
            elif method == "GET" and route == "/invites":
                require_admin(self)
                result = list_invites()
            elif method == "DELETE" and route.startswith("/invites/"):
                result = revoke_invite(require_admin(self), route[len("/invites/"):])
            elif method == "PUT" and route == "/installer":
                return self.upload(require_admin(self))
            elif method == "PUT" and route == "/installer/setup":
                return self.upload_setup(require_admin(self))
            elif method == "POST" and route == "/test-email":
                result = test_email(require_admin(self), self.read_json())
            else:
                result = (HTTPStatus.NOT_FOUND, {"error": "Not found."})
            self.send_json(*result)
        except ApiError as e:
            self.send_json(e.status, {"error": e.message})
        except Exception as e:  # never leak a stack trace to the internet
            log(f"error handling {method} {self.path}: {e!r}")
            self.send_json(HTTPStatus.INTERNAL_SERVER_ERROR, {"error": "Internal error in the SwVault team service."})

    def download(self):
        if not os.path.exists(INSTALLER_FILE):
            raise ApiError(HTTPStatus.NOT_FOUND, "Your admin hasn't published the installer yet.")
        code = normalize_code(self.query("invite") or "")
        wanted = (self.query("format") or "").lower()
        # A person following an invite link gets the setup steps and a download button. SwVault
        # agents (no Accept: text/html) get the zip they update from.
        if not wanted and "text/html" in (self.headers.get("Accept") or ""):
            return self.send_page(join_page(code))
        wizard = wanted == "exe" and os.path.exists(SETUP_FILE)
        path = SETUP_FILE if wizard else INSTALLER_FILE
        safe_team = re.sub(r"[^A-Za-z0-9_-]", "", TEAM_NAME) or "Team"
        # The installer reads the invite code from its own name (the wizard) or its folder name
        # ("Extract All" names the folder after the zip), so the member doesn't have to type it.
        name = (f"SwVault-{safe_team}" + ("-Setup" if wizard else "")
                + (f"-invite-{code[0:4]}-{code[4:8]}-{code[8:12]}" if len(code) == 12 else "")
                + (".exe" if wizard else ".zip"))
        size = os.path.getsize(path)
        # "Range: bytes=N-" resumes a download that was cut off (the agent's updater asks for it).
        start = 0
        match = re.fullmatch(r"bytes=(\d+)-", (self.headers.get("Range") or "").strip())
        if match:
            start = int(match.group(1))
            if start >= size:
                self.send_response(HTTPStatus.REQUESTED_RANGE_NOT_SATISFIABLE)
                self.send_header("Content-Range", f"bytes */{size}")
                self.send_header("Content-Length", "0")
                self.end_headers()
                return
        self.send_response(HTTPStatus.PARTIAL_CONTENT if start else HTTPStatus.OK)
        self.send_header("Content-Type", "application/vnd.microsoft.portable-executable" if wizard else "application/zip")
        self.send_header("Content-Length", str(size - start))
        self.send_header("Accept-Ranges", "bytes")
        if start:
            self.send_header("Content-Range", f"bytes {start}-{size - 1}/{size}")
        self.send_header("Content-Disposition", f'attachment; filename="{name}"')
        self.end_headers()
        with open(path, "rb") as f:
            f.seek(start)
            shutil.copyfileobj(f, self.wfile, 1024 * 1024)

    def send_page(self, page):
        data = page.encode()
        self.send_response(HTTPStatus.OK)
        self.send_header("Content-Type", "text/html; charset=utf-8")
        self.send_header("Content-Length", str(len(data)))
        self.send_header("Cache-Control", "no-store")
        self.send_header("X-Content-Type-Options", "nosniff")
        self.send_header("Content-Security-Policy", "default-src 'none'; style-src 'unsafe-inline'")
        self.end_headers()
        self.wfile.write(data)

    def receive(self, tmp, what):
        """Saves the request body to tmp; returns (length, sha256)."""
        length = int(self.headers.get("Content-Length") or 0)
        if not 0 < length <= MAX_UPLOAD:
            raise ApiError(HTTPStatus.BAD_REQUEST, f"Send the {what} as the request body.")
        os.makedirs(os.path.dirname(tmp), exist_ok=True)
        remaining = length
        digest = hashlib.sha256()
        with open(tmp, "wb") as f:
            while remaining > 0:
                chunk = self.rfile.read(min(remaining, 1024 * 1024))
                if not chunk:
                    raise ApiError(HTTPStatus.BAD_REQUEST, "Upload was cut off.")
                f.write(chunk)
                digest.update(chunk)
                remaining -= len(chunk)
        return length, digest

    def upload_setup(self, admin):
        tmp = SETUP_FILE + ".upload"
        length, digest = self.receive(tmp, "setup program")
        meta = load(INSTALLER_META, {})
        version = self.query("version") or ""
        with open(tmp, "rb") as f:
            is_program = f.read(2) == b"MZ"
        if not is_program or not os.path.exists(INSTALLER_FILE) or version != meta.get("version"):
            os.remove(tmp)
            if not is_program:
                raise ApiError(HTTPStatus.BAD_REQUEST, "That isn't a SwVault setup program (build it with scripts/package.ps1).")
            raise ApiError(HTTPStatus.CONFLICT, f"Publish the installer zip of version {version or '?'} first (the server has {meta.get('version') or 'none'}).")
        meta["setup"] = {"sha256": digest.hexdigest(), "size": length}
        os.replace(tmp, SETUP_FILE)
        save(INSTALLER_META, meta)
        log(f"setup wizard {version} published by {admin} ({length} bytes)")
        self.send_json(HTTPStatus.OK, {"version": version, **meta["setup"]})

    def upload(self, admin):
        tmp = INSTALLER_FILE + ".upload"
        length, digest = self.receive(tmp, "installer zip")
        version = package_version(tmp)
        if version is None:
            os.remove(tmp)
            raise ApiError(HTTPStatus.BAD_REQUEST, "That isn't a SwVault installer zip (build it with scripts/package.ps1).")
        meta = {
            "version": version,
            "sha256": digest.hexdigest(),
            "size": length,
            "published": stamp(),
            "publishedBy": admin,
            "required": (self.query("required") or "").lower() in ("1", "true", "yes"),
            "notes": (self.query("notes") or "").strip()[:500],
        }
        os.replace(tmp, INSTALLER_FILE)
        if os.path.exists(SETUP_FILE):
            os.remove(SETUP_FILE)  # an older version's wizard; package.ps1 uploads the new one next
        save(INSTALLER_META, meta)
        log(f"installer {version} published by {admin} ({length} bytes{', required' if meta['required'] else ''})")
        self.send_json(HTTPStatus.OK, meta)

    def do_GET(self):
        self.handle_request("GET")

    def do_POST(self):
        self.handle_request("POST")

    def do_PUT(self):
        self.handle_request("PUT")

    def do_DELETE(self):
        self.handle_request("DELETE")


def main():
    os.makedirs(STATE_DIR, exist_ok=True)
    browser.setup(sys.modules[__name__])
    ThreadingHTTPServer.request_queue_size = 128  # every agent on the team polls; don't refuse a burst
    server = ThreadingHTTPServer(("0.0.0.0", PORT), Handler)
    email = f"members verify {domains_text()} addresses" + ("" if SMTP_HOST else " (but SMTP isn't set up!)") if EMAIL_DOMAINS else "no email check"
    log(f"SwVault team service for {TEAM_NAME} ({ORG}) on port {PORT}; {email}")
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        sys.exit(0)


if __name__ == "__main__":
    main()
