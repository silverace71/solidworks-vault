#!/usr/bin/env bash
# Automatic updates: builds main on GitHub, then publishes the result to the team.
#
#   1. Asks GitHub to build and test main (.github/workflows/release.yml) and waits for it. The
#      build makes a release only when main changed since the last one.
#   2. Copies new server files (this folder and ../backup) from the newest release and restarts
#      the team service; if it doesn't come back, the old files are restored.
#   3. Publishes the newest installer, so everyone's SwVault offers the update.
#
# Runs nightly (swvault-autoupdate.timer) and when an admin clicks "Check for updates now" on the
# website's Accounts page (swvault-autoupdate.path). Turn it on with:
#   sudo ./swvault-admin.sh auto-update on <owner>/<repository>
# By hand: sudo ./swvault-admin.sh auto-update now
#
# Not updated here: docker-compose.yml and tailscale-serve.json (they need the whole server
# restarted), .env and data/.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

GITHUB_API="${GITHUB_API:-https://api.github.com}"
WORKFLOW="release.yml"
TOKEN_FILE="$DATA_DIR/github-token"
STATUS_FILE="$DATA_DIR/invites/auto-update.json"
REQUEST_FILE="$DATA_DIR/invites/auto-update.request"
SERVER_VERSION_FILE="$DATA_DIR/auto-update-server-version"
INVITES="${INVITES_URL:-http://127.0.0.1:3100}"
BUILD_MINUTES="${BUILD_MINUTES:-60}"

trigger="${1:-timer}"
started="$(date -u +%FT%TZ)"
step="Starting"
finished=no
work=""
notes=()

# status STATE MESSAGE  -> what the website's Accounts page shows.
status() {
    local tmp="$STATUS_FILE.tmp"
    mkdir -p "$(dirname "$STATUS_FILE")"
    jq -n --arg state "$1" --arg message "$2" --arg started "$started" --arg trigger "$trigger" --arg now "$(date -u +%FT%TZ)" \
        '{state: $state, message: $message, started: $started, trigger: $trigger, updated: $now}
         + (if $state == "running" then {} else {finished: $now} end)' > "$tmp"
    mv -f "$tmp" "$STATUS_FILE"
}

progress() { step="$1"; say "$1"; status running "$1"; }
finish() { finished=yes; say "$1"; status ok "$1"; }
die() { finished=yes; printf 'error: %s\n' "$*" >&2; status failed "$*"; exit 1; }

cleanup() {
    local code=$?
    [[ -z "$work" ]] || rm -rf "$work"
    if [[ "$finished" == no ]]; then
        status failed "Stopped while: $step. On the server: sudo ./swvault-admin.sh auto-update status" || true
    fi
    exit "$code"
}

# gh [curl options] URL  -> GitHub, signed in with the saved token (kept off the command line).
gh() { curl -fsSL --retry 2 -m "${GH_TIMEOUT:-60}" -H @"$work/headers" "$@"; }
gh_json() { gh -H 'Accept: application/vnd.github+json' "$@"; }

wait_invites() {
    local deadline=$((SECONDS + $1))
    until curl -sS -m 5 -o /dev/null "$INVITES/health" 2>/dev/null; do
        (( SECONDS < deadline )) || return 1
        sleep 3
    done
}

newest_run() { gh_json "$GITHUB_API/repos/$repo/actions/workflows/$WORKFLOW/runs?event=workflow_dispatch&per_page=5" | jq -c '.workflow_runs | max_by(.id) // {id: 0}'; }

# Asks GitHub to build the branch and waits. A build that fails (tests included) publishes nothing.
build() {
    local before run deadline
    progress "Asking GitHub to build $branch"
    if ! before="$(newest_run)" || ! gh_json -X POST -d "$(jq -n --arg r "$branch" '{ref: $r}')" "$GITHUB_API/repos/$repo/actions/workflows/$WORKFLOW/dispatches" >/dev/null; then
        notes+=("GitHub didn't accept the request to build $branch (the token needs Actions: read and write), so only releases that already exist were used.")
        warn "${notes[-1]}"
        return 0
    fi
    before="$(jq -r .id <<<"$before")"
    deadline=$((SECONDS + BUILD_MINUTES * 60))
    while :; do
        sleep "${BUILD_POLL_SECONDS:-20}"
        if run="$(newest_run)" && (( $(jq -r .id <<<"$run") > before )); then
            if [[ "$(jq -r .status <<<"$run")" == completed ]]; then
                [[ "$(jq -r .conclusion <<<"$run")" == success ]] \
                    || die "The build of $branch failed, so nothing was published. Details: $(jq -r .html_url <<<"$run")"
                return 0
            fi
            status running "Building and testing $branch on GitHub (about 10 minutes when there are changes)"
        fi
        (( SECONDS < deadline )) || die "GitHub didn't finish building $branch within $BUILD_MINUTES minutes. Try again later; see the Actions tab of $repo."
    done
}

# download ASSET-JSON FILE  -> one release file, checked against the size and checksum GitHub lists.
download() {
    local name size digest
    name="$(jq -r .name <<<"$1")"; size="$(jq -r .size <<<"$1")"; digest="$(jq -r .digest <<<"$1")"
    GH_TIMEOUT=1200 gh -H 'Accept: application/octet-stream' -o "$2" "$GITHUB_API/repos/$repo/releases/assets/$(jq -r .id <<<"$1")" \
        || die "Downloading $name from GitHub failed."
    [[ "$(stat -c %s "$2")" == "$size" ]] || die "$name arrived incomplete ($(stat -c %s "$2") of $size bytes)."
    if [[ "$digest" == sha256:* ]]; then
        [[ "$(sha256sum "$2" | cut -d' ' -f1)" == "${digest#sha256:}" ]] || die "$name doesn't match its checksum on GitHub."
    fi
}

# apply_server_files TAR  -> copies changed files into place; prints nothing, sets changed[] and held[].
apply_server_files() {
    local stage="$work/server" backup="$work/backup" root owner rel src dst
    changed=(); held=()
    mkdir -p "$stage" "$backup"
    tar -xzf "$1" -C "$stage" --no-same-owner
    [[ -f "$stage/server/linux/invites/invites.py" && -f "$stage/server/linux/swvault-admin.sh" ]] || die "The release's server files don't look like SwVault's."
    root="$(dirname "$SWVAULT_DIR")"
    owner="$(stat -c %u:%g "$SWVAULT_DIR")"
    while IFS= read -r -d '' rel; do
        rel="${rel#./}"
        src="$stage/server/$rel"; dst="$root/$rel"
        case "$rel" in
            linux/docker-compose.yml | linux/tailscale-serve.json)
                cmp -s "$src" "$dst" 2>/dev/null || held+=("${rel#linux/}")
                continue ;;
            linux/* | backup/*) ;;
            *) continue ;; # other kinds of server (docker, kubernetes, windows)
        esac
        cmp -s "$src" "$dst" 2>/dev/null && continue
        if [[ -e "$dst" ]]; then
            mkdir -p "$backup/$(dirname "$rel")"
            cp -p "$dst" "$backup/$rel"
        fi
        install -D -o "${owner%:*}" -g "${owner#*:}" -m "$(stat -c %a "$src")" "$src" "$dst.new"
        mv -f "$dst.new" "$dst"
        changed+=("$rel")
    done < <(cd "$stage/server" && find . -type f -print0)

    local restart=no
    for rel in ${changed[@]+"${changed[@]}"}; do
        if [[ "$rel" == linux/invites/* ]]; then restart=yes; fi
    done
    [[ "$restart" == yes ]] || return 0
    status running "Restarting the team service with the new files"
    compose restart invites >/dev/null
    if ! wait_invites 90; then
        for rel in "${changed[@]}"; do
            if [[ -e "$backup/$rel" ]]; then cp -p "$backup/$rel" "$root/$rel"; else rm -f "$root/$rel"; fi
        done
        compose restart invites >/dev/null || true
        wait_invites 90 || true
        die "The team service didn't start with the new server files, so the old ones were put back. See: sudo ./swvault-admin.sh logs invites"
    fi
}

main() {
    [[ $EUID -eq 0 ]] || { echo "Run with sudo." >&2; exit 1; }
    # A click on the website leaves this file, and systemd starts us while it exists: take it first.
    if [[ -e "$REQUEST_FILE" ]]; then
        trigger="website ($(head -c 60 "$REQUEST_FILE" | tr -cd 'A-Za-z0-9._-'))"
        rm -f "$REQUEST_FILE"
    fi
    repo="$(env_get GITHUB_REPO)"
    branch="$(env_get GITHUB_BRANCH)"; branch="${branch:-main}"
    [[ -n "$repo" && -s "$TOKEN_FILE" ]] || { echo "Automatic updates aren't set up. Run: sudo ./swvault-admin.sh auto-update on <owner>/<repository>" >&2; exit 1; }

    exec 9> "$DATA_DIR/auto-update.lock"
    flock -n 9 || { echo "An update is already running."; exit 0; }

    rm -rf "$DATA_DIR"/auto-update.tmp.*
    work="$(mktemp -d "$DATA_DIR/auto-update.tmp.XXXXXX")"
    trap cleanup EXIT
    (umask 077; printf 'Authorization: Bearer %s\nX-GitHub-Api-Version: 2022-11-28\n' "$(tr -d ' \r\n' < "$TOKEN_FILE")" > "$work/headers")

    progress "Checking the server"
    wait_invites 300 || die "The team service isn't answering (sudo ./swvault-admin.sh status)."

    build

    progress "Looking at the releases of $repo"
    local releases newest
    releases="$(gh_json "$GITHUB_API/repos/$repo/releases?per_page=30")" \
        || die "Couldn't read the releases of $repo on GitHub. If the token expired, make a new one and run: sudo ./swvault-admin.sh auto-update on"
    releases="$(jq -c '[.[] | select((.draft or .prerelease) | not) | select(.tag_name | test("^v[0-9]+\\.[0-9]+\\.[0-9]+$"))
        | {version: .tag_name[1:], body: (.body // ""), assets: [.assets[] | {id, name, size, digest: (.digest // "")}]}]
        | sort_by(.version | split(".") | map(tonumber))' <<<"$releases")"
    newest="$(jq -c 'last // empty' <<<"$releases")"
    [[ -n "$newest" ]] || { finish "Nothing to do: $repo has no releases yet. ${notes[*]-}"; return 0; }

    # Server files first, so the website already knows what the new SwVault expects.
    local version asset did=()
    version="$(jq -r .version <<<"$newest")"
    asset="$(jq -c '[.assets[] | select(.name == "swvault-server.tar.gz")] | first // empty' <<<"$newest")"
    if [[ -n "$asset" && "$(cat "$SERVER_VERSION_FILE" 2>/dev/null || true)" != "$version" ]]; then
        progress "Updating the server files to $version"
        download "$asset" "$work/server.tar.gz"
        apply_server_files "$work/server.tar.gz"
        printf '%s\n' "$version" > "$SERVER_VERSION_FILE"
        (( ${#changed[@]} == 0 )) || did+=("Updated ${#changed[@]} server file(s) to $version.")
        (( ${#held[@]} == 0 )) || notes+=("${held[*]} changed in $version and wasn't touched; copy it by hand, then: sudo ./swvault-admin.sh update")
    fi

    # The newest release that carries an installer (a release with only server changes has none).
    local candidate published code zip setup summary
    candidate="$(jq -c '[.[] | select(any(.assets[]; .name | test("-Setup\\.exe$")) and any(.assets[]; .name | test("\\.zip$")))] | last // empty' <<<"$releases")"
    code="$(curl -sS -m 20 -o "$work/latest.json" -w '%{http_code}' "$INVITES/installer/latest")" || die "The team service isn't answering (sudo ./swvault-admin.sh status)."
    case "$code" in
        200) published="$(jq -r '.version // "0.0.0"' "$work/latest.json")" ;;
        404) published="0.0.0" ;;
        *) die "The team service answered HTTP $code when asked which SwVault is published." ;;
    esac
    if [[ -n "$candidate" ]]; then
        version="$(jq -r .version <<<"$candidate")"
        if [[ "$version" != "$published" && "$(printf '%s\n%s\n' "$published" "$version" | sort -V | tail -n 1)" == "$version" ]]; then
            progress "Downloading SwVault $version"
            zip="$work/$(jq -r '[.assets[] | select(.name | test("\\.zip$"))] | first | .name | gsub("[^A-Za-z0-9._-]"; "_")' <<<"$candidate")"
            setup="${zip%.zip}-Setup.exe"
            download "$(jq -c '[.assets[] | select(.name | test("\\.zip$"))] | first' <<<"$candidate")" "$zip"
            download "$(jq -c '[.assets[] | select(.name | test("-Setup\\.exe$"))] | first' <<<"$candidate")" "$setup"
            progress "Publishing SwVault $version"
            summary="$(jq -r '.body | split("\n") | map(gsub("\r"; "") | select(startswith("- ")) | .[2:]) | .[:3] | join("; ") | .[:300]' <<<"$candidate")"
            "$SWVAULT_DIR/swvault-admin.sh" publish-installer "$zip" --setup "$setup" --notes "$summary" \
                || die "Publishing SwVault $version failed; SwVault $published is still the published version."
            [[ "$(curl -sS -m 20 "$INVITES/installer/latest" | jq -r .version)" == "$version" ]] \
                || die "SwVault $version was uploaded but the server doesn't report it as published."
            published="$version"
            did+=("Published SwVault $version; everyone's SwVault offers it within a few hours.")
        fi
    fi

    (( ${#did[@]} > 0 )) || did=("Already up to date: SwVault $published is published and $branch has nothing newer.")
    finish "${did[*]}${notes[0]+ ${notes[*]}}"
}

main
