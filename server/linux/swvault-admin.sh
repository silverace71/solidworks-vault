#!/usr/bin/env bash
# Day-to-day administration of the SwVault server set up by setup.sh. Run with sudo.
#
#   sudo ./swvault-admin.sh invite --uses 20 --days 14    # one invite link for the whole team
#   sudo ./swvault-admin.sh invite --viewer               # single-use, read-only
#   sudo ./swvault-admin.sh invites | revoke-invite <code>
#   sudo ./swvault-admin.sh publish-installer SwVault-FSAE-0.2.0.zip [--setup SwVault-FSAE-0.2.0-Setup.exe] [--required] [--notes "what's new"]
#   sudo ./swvault-admin.sh add-user alice --email alice@colorado.edu   # designer (check out / check in)
#   sudo ./swvault-admin.sh add-user bob --viewer         # read-only
#   sudo ./swvault-admin.sh add-user carol --approver     # designer who can release files
#   sudo ./swvault-admin.sh reset-password alice          # a new random password
#   sudo ./swvault-admin.sh set-password alice            # type the new password yourself
#   sudo ./swvault-admin.sh set-email alice alice@colorado.edu   # where their review emails go ("sender": the server's own address)
#   sudo ./swvault-admin.sh disable-user alice            # e.g. graduated (history is kept)
#   sudo ./swvault-admin.sh enable-user alice
#   sudo ./swvault-admin.sh list-users
#   sudo ./swvault-admin.sh set-role alice admin|approver [--remove]
#   sudo ./swvault-admin.sh set-lead alice Chassis | set-lead alice --member
#   sudo ./swvault-admin.sh approvals                     # lead / responsible-engineer requests waiting
#   sudo ./swvault-admin.sh approve alice lead | approve alice "Front Suspension"
#   sudo ./swvault-admin.sh decline alice lead | decline alice "Front Suspension"
#   sudo ./swvault-admin.sh projects                      # projects, subsystems and responsible engineers
#   sudo ./swvault-admin.sh test-email you@colorado.edu
#   sudo ./swvault-admin.sh status                        # is everything up and reachable?
#   sudo ./swvault-admin.sh backup                        # run the nightly backup now
#   sudo ./swvault-admin.sh update                        # update cloudflared/Caddy and pinned Gitea
#   sudo ./swvault-admin.sh auto-update on <owner>/<repo> [--at 04:00] | off | now | status   # build main on GitHub nightly and publish it
#   sudo ./swvault-admin.sh client-config                 # rewrite team.json
#   sudo ./swvault-admin.sh logs [gitea|cloudflared|proxy|invites]
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

usage() { sed -n '2,29p' "$0" | sed 's/^# \{0,1\}//'; exit "${1:-0}"; }

# approve|decline <user> lead|<subsystem name or id>
cmd_decide() {
    local verdict="$1" user="${2:-}" what="${*:3}" body subsystem
    [[ -n "$user" && -n "$what" ]] || die "Usage: $verdict <user> lead   or   $verdict <user> <subsystem>"
    local approve=false
    [[ "$verdict" == approve ]] && approve=true
    if [[ "$what" == lead ]]; then
        body="$(jq -n --arg l "$user" --argjson a "$approve" '{kind: "lead", login: $l, approve: $a}')"
    else
        subsystem="$(invites_api GET cars | jq -r --arg w "$what" \
            '[.cars[].subsystems[] | select(.id == $w or (.name | ascii_downcase) == ($w | ascii_downcase))] | if length == 1 then .[0].id elif length > 1 then "many" else "" end')"
        [[ "$subsystem" == many ]] && die "Several subsystems are called '$what'; use its id (see: projects)."
        [[ -n "$subsystem" ]] || die "No subsystem called '$what' (see: projects)."
        body="$(jq -n --arg l "$user" --arg s "$subsystem" --argjson a "$approve" '{kind: "engineer", login: $l, subsystem: $s, approve: $a}')"
    fi
    invites_api POST approvals "$body" | jq -r '"\(.login): \(.kind) request " + (if .approved then "approved" else "declined" end)'
}

require_setup() {
    [[ -f "$ENV_FILE" ]] || die "Not set up yet. Run: sudo ./setup.sh"
    docker info >/dev/null 2>&1 || die "Can't talk to Docker. Run with sudo."
}

ORG="$(env_get ORG)"
REPO="$(env_get REPO)"

check_name() { [[ "$1" =~ ^[A-Za-z0-9][A-Za-z0-9._-]*$ ]] || die "User name '$1' may only contain letters, digits, - . _"; }

print_card() {
    local user="$1" password="$2" access="$3"
    cat <<EOF

------------------------------------------------------------------
 SwVault account for $(env_get TEAM_NAME)
   User name:  $user
   Password:   $password
   Access:     $access

 1. Close SOLIDWORKS and run the SwVault setup you were sent
    (https://$(env_get VAULT_HOSTNAME)/swvault-invites/download).
 2. Sign in with the user name and password above when SwVault asks.
    Your files download to $(env_get LOCAL_ROOT) by themselves.
 3. Open SOLIDWORKS and use the SwVault tab.

 Change your password any time at https://$(env_get VAULT_HOSTNAME)/user/settings/account
------------------------------------------------------------------
Send this to $user privately (not in a group chat).
EOF
}

# Adds or removes LOGIN in a role list of .swvault/vault.json with one commit (Gitea contents API).
edit_role() {
    local user="$1" role="$2" mode="$3" file current sha content updated
    file=".swvault%2Fvault.json"
    API_OK_CODES="404"
    current="$(api GET "repos/$ORG/$REPO/contents/$file?ref=main")"
    API_OK_CODES=""
    sha="$(jq -r '.sha // empty' <<<"$current")"
    if [[ -z "$sha" ]]; then
        warn "The vault isn't set up yet, so $user wasn't made $role. Sign in once from SwVault as $(env_get ADMIN_USER), then run: sudo $0 set-role $user $role"
        return 1
    fi
    content="$(jq -r '.content' <<<"$current" | base64 -d)"
    if [[ "$mode" == add ]]; then
        updated="$(jq --arg r "$role" --arg u "$user" '.roles[$r] = (((.roles[$r] // []) + [$u]) | unique)' <<<"$content")"
    else
        updated="$(jq --arg r "$role" --arg u "$user" '.roles[$r] = ((.roles[$r] // []) | map(select(ascii_downcase != ($u | ascii_downcase))))' <<<"$content")"
    fi
    [[ "$updated" != "$content" ]] || { say "No change to the $role role."; return; }
    api PUT "repos/$ORG/$REPO/contents/$file" "$(jq -n --arg c "$(printf '%s\n' "$updated" | base64 -w0)" --arg s "$sha" \
        --arg m "$([[ $mode == add ]] && echo "Add $user as $role" || echo "Remove $user as $role")" \
        '{content: $c, sha: $s, message: $m, branch: "main"}')" >/dev/null
    say "$([[ $mode == add ]] && echo "$user is now $role" || echo "$user is no longer $role"). SwVault picks this up within a minute."
}

cmd_add_user() {
    local user="" role=Designers approver=no admin=no full_name="" email=""
    while (($#)); do
        case "$1" in
            --viewer) role=Viewers ;;
            --approver) approver=yes ;;
            --admin) admin=yes; approver=yes ;;
            --name) full_name="${2:?--name needs a value}"; shift ;;
            --email) email="${2:?--email needs an address}"; shift ;;
            -*) die "Unknown option $1" ;;
            *) user="$1" ;;
        esac
        shift
    done
    [[ -n "$user" ]] || die "Usage: add-user <name> [--viewer|--approver|--admin] [--name \"Full Name\"] [--email address]"
    check_name "$user"
    local password
    password="$(gen_password)"
    # A real address gets review emails; without one they only see reviews in SwVault.
    api POST admin/users "$(jq -n --arg u "$user" --arg p "$password" --arg n "$full_name" --arg e "$email" \
        '{username: $u, login_name: $u, email: (if $e == "" then $u + "@noreply.localhost" else $e end), password: $p, full_name: $n,
          must_change_password: false, send_notify: false, visibility: "private"}')" >/dev/null
    local team
    team="$(team_id "$ORG" "$role")"
    [[ -n "$team" ]] || die "Team $role not found in $ORG. Run setup.sh again."
    api PUT "teams/$team/members/$user" >/dev/null
    local access
    access="$([[ $role == Viewers ]] && echo "view and download" || echo "check out and check in")"
    [[ "$admin" == yes ]] && access="$access, admin"
    [[ "$approver" == yes ]] && access="$access, approve releases"
    print_card "$user" "$password" "$access"
    echo
    if [[ "$admin" == yes ]]; then
        api PUT "teams/$(team_id "$ORG" Owners)/members/$user" >/dev/null
        edit_role "$user" admin add || true
    fi
    if [[ "$approver" == yes ]]; then
        edit_role "$user" approver add || true
    fi
}

patch_user() {
    local user="$1" json="$2"
    api PATCH "admin/users/$user" "$(jq -n --arg u "$user" --argjson extra "$json" '{login_name: $u, source_id: 0} + $extra')" >/dev/null
}

cmd_reset_password() {
    local user="${1:?Usage: reset-password <name>}" password
    password="$(gen_password)"
    patch_user "$user" "$(jq -n --arg p "$password" '{password: $p, must_change_password: false}')"
    say "New password for $user: $password"
    echo "They sign in again from the SwVault tray icon > Vaults... if SwVault asks."
}

cmd_set_password() {
    local user="${1:?Usage: set-password <name>}" password again
    [[ -t 0 ]] || die "set-password asks for the password, so run it in a terminal."
    read -rsp "New password for $user: " password; echo
    (( ${#password} >= 8 )) || die "Use at least 8 characters."
    read -rsp "Again: " again; echo
    [[ "$password" == "$again" ]] || die "Those didn't match; nothing was changed."
    patch_user "$user" "$(jq -n --arg p "$password" '{password: $p, must_change_password: false}')"
    # The password setup.sh generated no longer works, so don't leave it lying around as if it did.
    [[ "$user" == "$(env_get ADMIN_USER)" ]] && rm -f "$DATA_DIR/admin-credentials.txt"
    say "Password changed for $user."
    echo "They sign in again from the SwVault tray icon > Vaults... if SwVault asks."
}

cmd_set_email() {
    local user="${1:?Usage: set-email <name> <address>|sender}" email="${2:?Usage: set-email <name> <address>|sender}"
    [[ "$email" == sender ]] && email="$(env_get SMTP_FROM)"
    [[ "$email" =~ ^[^@[:space:]]+@[^@[:space:]]+\.[^@[:space:]]+$ ]] || die "'$email' isn't an email address."
    patch_user "$user" "$(jq -n --arg e "$email" '{email: $e}')"
    say "$user's email is now $email (review and approval emails go there)."
}

cmd_list_users() {
    local designers viewers owners people
    designers="$(api GET "teams/$(team_id "$ORG" Designers)/members?limit=100" | jq -r '.[].login')"
    viewers="$(api GET "teams/$(team_id "$ORG" Viewers)/members?limit=100" | jq -r '.[].login')"
    owners="$(api GET "teams/$(team_id "$ORG" Owners)/members?limit=100" | jq -r '.[].login')"
    people="$(invites_api GET people 2>/dev/null || echo '{"people": []}')"
    printf '%-18s %-22s %-9s %-16s %s\n' USER NAME STATUS ACCESS "TEAM ROLE"
    # Unit separator, not tabs: read collapses consecutive tabs, which shifts empty names.
    api GET "admin/users?limit=200" | jq -r '.[] | [.login, (.full_name // ""), (if .prohibit_login then "disabled" else "active" end)] | join("\u001f")' |
        while IFS=$'\x1f' read -r login name status; do
            local access=() role
            grep -qxF "$login" <<<"$owners" && access+=(admin)
            grep -qxF "$login" <<<"$designers" && access+=(designer)
            grep -qxF "$login" <<<"$viewers" && access+=(viewer)
            role="$(jq -r --arg l "$login" '.people[] | select(.login == $l) | (if .memberType == "lead" then "lead: \(.subteam)" elif .memberType == "member" then "member" else "-" end) + (if .pendingLead then " (wants lead: \(.pendingLead))" else "" end)' <<<"$people")"
            printf '%-18s %-22s %-9s %-16s %s\n' "$login" "${name:0:22}" "$status" "$(IFS=,; echo "${access[*]:-none}")" "${role:--}"
        done
}

cmd_status() {
    local host ok=yes
    host="$(env_get VAULT_HOSTNAME)"
    say "Containers"
    compose ps
    say "Cloudflare Tunnel"
    if compose ps --status running --services 2>/dev/null | grep -qx cloudflared; then
        echo "  cloudflared container running"
    else
        echo "  tunnel not running"; ok=no
    fi
    say "LFS storage"
    echo "  R2 bucket: $(env_get R2_BUCKET) (signed direct downloads enabled)"
    if curl -fsS -m 5 http://127.0.0.1:3200/swvault-transfer/health >/dev/null 2>&1; then
        echo '  Direct multipart coordinator: healthy'
    else
        echo '  Direct multipart coordinator: NOT answering'; ok=no
    fi
    say "Gitea (local)"
    if curl -fsS -m 5 http://127.0.0.1:3000/api/healthz >/dev/null 2>&1; then echo "  healthy"; else echo "  NOT answering"; ok=no; fi
    say "Invite service (local)"
    if curl -fsS -m 5 http://127.0.0.1:3100/health >/dev/null 2>&1; then echo "  healthy"; else echo "  NOT answering"; ok=no; fi
    echo "  web file browser: https://$host/swvault/"
    if [[ -f "$DATA_DIR/invites/installer/SwVault-installer.zip" ]]; then
        echo "  installer published: https://$host/swvault-invites/download"
    else
        echo "  installer not published yet (scripts\\package.ps1 -Publish, or publish-installer <zip>)"
    fi
    say "From the internet"
    if curl -fsS -m 15 "https://$host/api/healthz" >/dev/null 2>&1; then echo "  https://$host is reachable"; else echo "  https://$host is NOT reachable"; ok=no; fi
    say "Disk"
    du -sh "$DATA_DIR/gitea/git/lfs" 2>/dev/null | awk '{print "  file content (LFS): " $1}'
    df -h "$DATA_DIR" | awk 'NR == 2 {print "  free on this disk: " $4 " of " $2}'
    local backup_dir last
    backup_dir="$(env_get BACKUP_DIR)"
    last="$(ls -1t "$backup_dir"/daily/gitea-*.tar.gz 2>/dev/null | head -n 1 || true)"
    say "Automatic updates"
    if systemctl is-enabled --quiet swvault-autoupdate.timer 2>/dev/null; then
        echo "  on, nightly at $(env_get AUTO_UPDATE_AT) from $(env_get GITHUB_REPO)"
        [[ ! -f "$DATA_DIR/invites/auto-update.json" ]] || jq -r '"  last run: \(.state)\(if .finished then " at \(.finished)" else "" end): \(.message)"' "$DATA_DIR/invites/auto-update.json"
    else
        echo "  off (auto-update on <owner>/<repository>)"
    fi
    say "Backups"
    echo "  last: ${last:-none yet} (folder $backup_dir)"
    [[ "$ok" == yes ]] || exit 1
}

cmd_watchdog() {
    # Called every 5 minutes by swvault-watchdog.timer.
    if ! compose ps --status running --services 2>/dev/null | grep -qx cloudflared; then
        compose up -d
        sleep 20
    fi
    if ! curl -fsS -m 10 http://127.0.0.1:3000/api/healthz >/dev/null 2>&1; then
        # Gitea shares cloudflared's network namespace; after cloudflared restarts, Gitea must too.
        echo "Gitea not answering; restarting it."
        compose up -d
        compose restart gitea
    fi
    # The team service shares that network namespace too. Any HTTP answer means it is reachable.
    if ! curl -sS -m 10 -o /dev/null http://127.0.0.1:3100/health 2>/dev/null; then
        echo "Team service not answering; restarting it."
        compose restart invites
    fi
    if ! curl -fsS -m 10 http://127.0.0.1:3200/swvault-transfer/health >/dev/null 2>&1; then
        echo 'R2 multipart coordinator not responding; restarting it.'
        compose restart r2-transfer
    fi
}

cmd_update() {
    say "Pulling new images"
    compose pull --ignore-buildable
    # Compose recreates dependents if the cloudflared network namespace changes.
    compose up -d --build --force-recreate
    wait_gitea 300
    say "Updated. (Gitea's version is pinned in docker-compose.yml; bump GITEA_TAG in .env after reading its release notes.)"
}

AUTOUPDATE_UNITS=(swvault-autoupdate.timer swvault-autoupdate.path)
AUTOUPDATE_STATUS="$DATA_DIR/invites/auto-update.json"

# Automatic updates (auto-update.sh): GitHub builds main, this server publishes the result.
cmd_auto_update() {
    local action="${1:-status}"; shift || true
    case "$action" in
        on)
            [[ "$(env_get CF_DEPLOYMENT)" != cloudflare ]] || die 'Legacy server-file auto-updates are disabled on this Cloudflare variant. Update reviewed changes manually.'
            local repo="" at="" token code out
            while (($#)); do
                case "$1" in
                    --at) shift; at="${1:-}" ;;
                    */*) repo="$1" ;;
                    *) die "Usage: auto-update on <owner>/<repository> [--at HH:MM]" ;;
                esac
                shift
            done
            repo="${repo:-$(env_get GITHUB_REPO)}"
            at="${at:-$(env_get AUTO_UPDATE_AT)}"; at="${at:-04:00}"
            [[ "$repo" =~ ^[A-Za-z0-9._-]+/[A-Za-z0-9._-]+$ ]] || die "Usage: auto-update on <owner>/<repository> [--at HH:MM]   e.g. auto-update on Wes-Miller/SwVault"
            [[ "$at" =~ ^([01][0-9]|2[0-3]):[0-5][0-9]$ ]] || die "--at takes a time of day like 04:00 (this server's clock)."
            [[ -t 0 ]] || die "auto-update on asks for the GitHub token, so run it in a terminal."
            cat <<EOF
This server needs a GitHub token to start builds of $repo and download them.
On github.com: Settings > Developer settings > Personal access tokens > Fine-grained tokens >
Generate new token. Repository access: only $repo. Repository permissions:
  Actions: Read and write        Contents: Read-only
EOF
            if [[ -s "$DATA_DIR/github-token" ]]; then
                read -rsp "GitHub token (hidden; Enter keeps the saved one): " token; echo
                token="${token:-$(cat "$DATA_DIR/github-token")}"
            else
                read -rsp "GitHub token (hidden): " token; echo
            fi
            token="$(tr -d ' \r\n' <<<"$token")"
            [[ -n "$token" ]] || die "No token entered; nothing was changed."
            out="$(mktemp)"
            (umask 077; printf 'Authorization: Bearer %s\nX-GitHub-Api-Version: 2022-11-28\n' "$token" > "$out")
            code="$(curl -sS -m 30 -o /dev/null -w '%{http_code}' -H @"$out" "https://api.github.com/repos/$repo/releases?per_page=1" || true)"
            [[ "$code" == 200 ]] || { rm -f "$out"; die "GitHub answered HTTP ${code:-nothing} for $repo with that token (needs Contents: Read-only on $repo); nothing was changed."; }
            code="$(curl -sS -m 30 -o /dev/null -w '%{http_code}' -H @"$out" "https://api.github.com/repos/$repo/actions/workflows/release.yml" || true)"
            rm -f "$out"
            [[ "$code" == 200 ]] || warn "That token can't see the build of $repo (HTTP $code): it needs Actions: Read and write, and $repo needs .github/workflows/release.yml. Until then only releases that already exist are published."
            (umask 077; printf '%s\n' "$token" > "$DATA_DIR/github-token")
            env_set GITHUB_REPO "$repo"
            env_set AUTO_UPDATE_AT "$at"
            chmod +x "$SWVAULT_DIR/auto-update.sh"
            cat > /etc/systemd/system/swvault-autoupdate.service <<EOF
[Unit]
Description=SwVault automatic update (builds main on GitHub and publishes it)
After=docker.service network-online.target

[Service]
Type=oneshot
ExecStart=$SWVAULT_DIR/auto-update.sh
TimeoutStartSec=3h
EOF
            cat > /etc/systemd/system/swvault-autoupdate.timer <<EOF
[Unit]
Description=SwVault nightly automatic update

[Timer]
OnCalendar=*-*-* $at
Persistent=true
RandomizedDelaySec=5m

[Install]
WantedBy=timers.target
EOF
            # The website's "Check for updates now" leaves this file; systemd then starts the update.
            cat > /etc/systemd/system/swvault-autoupdate.path <<EOF
[Unit]
Description=SwVault update requested from the website

[Path]
PathExists=$DATA_DIR/invites/auto-update.request
Unit=swvault-autoupdate.service

[Install]
WantedBy=multi-user.target
EOF
            mkdir -p "$DATA_DIR/invites"
            rm -f "$DATA_DIR/invites/auto-update.request"
            [[ -f "$AUTOUPDATE_STATUS" ]] || jq -n '{state: "idle", message: "No update has run yet."}' > "$AUTOUPDATE_STATUS"
            systemctl daemon-reload
            systemctl enable --now "${AUTOUPDATE_UNITS[@]}" >/dev/null
            say "Automatic updates are on: every night at $at this server builds $repo and publishes what changed."
            echo "  Admins can also start one from the website: Accounts > Check for updates now."
            echo "  Run one now: sudo ./swvault-admin.sh auto-update now" ;;
        off)
            systemctl disable --now "${AUTOUPDATE_UNITS[@]}" >/dev/null 2>&1 || true
            rm -f /etc/systemd/system/swvault-autoupdate.{service,timer,path} "$DATA_DIR/github-token" "$AUTOUPDATE_STATUS" "$DATA_DIR/invites/auto-update.request"
            systemctl daemon-reload
            say "Automatic updates are off and the GitHub token was removed. Publish by hand with publish-installer." ;;
        now) exec "$SWVAULT_DIR/auto-update.sh" manual ;;
        status)
            if ! systemctl is-enabled --quiet swvault-autoupdate.timer 2>/dev/null; then
                echo "Automatic updates are off. Turn them on: sudo ./swvault-admin.sh auto-update on <owner>/<repository>"
                return 0
            fi
            echo "On: builds $(env_get GITHUB_REPO) and publishes it every night at $(env_get AUTO_UPDATE_AT)."
            systemctl list-timers swvault-autoupdate.timer --no-pager 2>/dev/null | sed -n '1,2p' || true
            [[ ! -f "$AUTOUPDATE_STATUS" ]] || jq -r '"Last run: \(.state)\(if .started then " (started \(.started), by \(.trigger))" else "" end)\n  \(.message)"' "$AUTOUPDATE_STATUS"
            say "Log"
            journalctl -u swvault-autoupdate.service -n 25 --no-pager 2>/dev/null || true ;;
        *) die "Usage: auto-update on <owner>/<repository> [--at HH:MM] | off | now | status" ;;
    esac
}

# invites_api METHOD PATH [JSON|@file]  -> the invite service on this machine, as the admin.
invites_api() {
    local method="$1" path="$2" body="${3:-}" out code
    out="$(mktemp)"
    local args=(-sS -o "$out" -w '%{http_code}' -X "$method" -H "Authorization: token $(admin_token)")
    if [[ "$body" == @* ]]; then
        args+=(-H 'Content-Type: application/zip' --data-binary "$body")
    elif [[ -n "$body" ]]; then
        args+=(-H 'Content-Type: application/json' --data "$body")
    fi
    code="$(curl "${args[@]}" "${INVITES_URL:-http://127.0.0.1:3100}/$path")" || { rm -f "$out"; die "The invite service isn't answering (swvault-admin.sh status / logs invites)."; }
    if (( code >= 400 )); then
        local msg
        msg="$(jq -r '.error // .' "$out" 2>/dev/null || cat "$out")"
        rm -f "$out"
        die "$msg"
    fi
    cat "$out"
    rm -f "$out"
}

invite_message() {
    local code="$1" role="$2" expires="$3" host
    host="$(env_get VAULT_HOSTNAME)"
    cat <<EOF
You're invited to the $(env_get TEAM_NAME) vault (SwVault PDM for SOLIDWORKS).

1. Open https://$host/swvault-invites/download?invite=${code}
2. Close SOLIDWORKS, then download and run SwVault Setup from that page.
3. Choose a user name and password when SwVault asks. Invite code (if asked): $code
Your files then download by themselves. Open SOLIDWORKS and use the SwVault tab.

(Access: $role. This invite expires ${expires%%T*}.)
EOF
}

cmd_invite() {
    local role=designer uses=1 days=7 note=""
    while (($#)); do
        case "$1" in
            --viewer) role=viewer ;;
            --uses) uses="${2:?--uses needs a number}"; shift ;;
            --days) days="${2:?--days needs a number}"; shift ;;
            --note) note="${2:?--note needs text}"; shift ;;
            *) die "Usage: invite [--viewer] [--uses N] [--days N] [--note text]" ;;
        esac
        shift
    done
    local invite
    invite="$(invites_api POST invites "$(jq -n --arg r "$role" --argjson u "$uses" --argjson d "$days" --arg n "$note" '{role: $r, uses: $u, days: $d, note: $n}')")"
    say "Invite $(jq -r .code <<<"$invite"): $role, $uses use(s), expires $(jq -r .expires <<<"$invite")"
    echo
    echo "---- send this (fine for a team chat; it only works $uses time(s)) ----"
    invite_message "$(jq -r .code <<<"$invite")" "$role" "$(jq -r .expires <<<"$invite")"
}

cmd="${1:-help}"
shift || true
case "$cmd" in
    help|-h|--help) usage ;;
    invite) require_setup; cmd_invite "$@" ;;
    invites)
        require_setup
        printf '%-16s %-10s %-8s %-12s %-14s %s\n' CODE ACCESS LEFT EXPIRES BY NOTE
        invites_api GET invites | jq -r '.[] | [.code, .role, "\(.usesLeft)/\(.uses)", (.expires | split("T")[0]), .createdBy, (.note // "")] | join("\u001f")' |
            while IFS=$'\x1f' read -r code role left expires by note; do
                printf '%-16s %-10s %-8s %-12s %-14s %s\n' "$code" "$role" "$left" "$expires" "$by" "$note"
            done ;;
    revoke-invite) require_setup; invites_api DELETE "invites/${1:?Usage: revoke-invite <code>}" >/dev/null; say "Invite $1 no longer works." ;;
    set-lead)
        require_setup
        [[ $# -ge 2 ]] || die "Usage: set-lead <user> <subteam>   or   set-lead <user> --member"
        if [[ "$2" == --member ]]; then body='{"memberType": "member"}'; else body="$(jq -n --arg s "${*:2}" '{memberType: "lead", subteam: $s}')"; fi
        invites_api PUT "people/$1" "$body" | jq -r '"\(.login) is now " + (if .memberType == "lead" then "lead of \(.subteam)" else "a general member" end)' ;;
    approvals)
        require_setup
        invites_api GET approvals | jq -r '
            (if (.leads | length) + (.engineers | length) == 0 then "Nothing waiting." else empty end),
            (.leads[] | "\(.login)\t(\(.fullName))\twants to be the \(.subteam) lead\t-> approve \(.login) lead"),
            (.engineers[] | "\(.login)\t(\(.fullName))\twants to be responsible engineer of \(.car) / \(.subsystem)\t-> approve \(.login) \"\(.subsystem)\"")' ;;
    approve|decline) require_setup; cmd_decide "$cmd" "$@" ;;
    projects|cars)
        require_setup
        invites_api GET cars | jq -r '
            if (.cars | length) == 0 then "No projects yet. Members add them in SwVault (task pane > Projects)." else
            .cars[] | "\(.name)  [\(.folder)]", (.subsystems[] | "    \(.name)  [\(.folder)]  id \(.id)  RE: " +
                ([.engineers[] | .login + (if .status == "pending" then " (pending)" else "" end)] | if length == 0 then "-" else join(", ") end)) end' ;;
    test-email)
        require_setup
        invites_api POST test-email "$(jq -n --arg t "${1:?Usage: test-email <address>}" '{to: $t}')" >/dev/null
        say "Sent. Check $1 (and its junk folder)." ;;
    publish-installer)
        require_setup
        zip="${1:-}"; shift || true
        [[ -f "$zip" ]] || die "Usage: publish-installer <SwVault-...zip> [--setup <SwVault-...-Setup.exe>] [--required] [--notes \"what's new\"]"
        query=()
        setup=""
        while (($#)); do
            case "$1" in
                --required) query+=("required=1") ;;
                --setup) shift; setup="${1:-}"; [[ -f "$setup" ]] || die "No setup program at '$setup'." ;;
                --notes) shift; query+=("notes=$(jq -rn --arg n "${1:-}" '$n|@uri')") ;;
                *) die "Unknown option $1" ;;
            esac
            shift
        done
        path="installer"; ((${#query[@]})) && path+="?$(IFS='&'; echo "${query[*]}")"
        version="$(invites_api PUT "$path" "@$zip" | jq -r .version)"
        # The wizard people download; without it the download page offers the zip.
        [[ -z "$setup" ]] || invites_api PUT "installer/setup?version=$version" "@$setup" >/dev/null
        say "Published SwVault $version. Download link: https://$(env_get VAULT_HOSTNAME)/swvault-invites/download"
        echo "  Everyone's SwVault offers the update within a few hours (tray icon > Install update)." ;;
    watchdog) require_setup; cmd_watchdog ;;
    add-user) require_setup; cmd_add_user "$@" ;;
    reset-password) require_setup; cmd_reset_password "$@" ;;
    set-password) require_setup; cmd_set_password "$@" ;;
    disable-user) require_setup; patch_user "${1:?Usage: disable-user <name>}" '{"prohibit_login": true}'; say "${1} can no longer sign in. Their check-outs stay; release them from SOLIDWORKS as an admin (swvault unlock) if needed." ;;
    enable-user) require_setup; patch_user "${1:?Usage: enable-user <name>}" '{"prohibit_login": false}'; say "${1} can sign in again." ;;
    list-users) require_setup; cmd_list_users ;;
    set-email) require_setup; cmd_set_email "$@" ;;
    set-role)
        require_setup
        [[ $# -ge 2 && ( "$2" == admin || "$2" == approver ) ]] || die "Usage: set-role <name> admin|approver [--remove]"
        edit_role "$1" "$2" "$([[ "${3:-}" == --remove ]] && echo remove || echo add)" ;;
    status) require_setup; cmd_status ;;
    backup) require_setup; "$SWVAULT_DIR/../backup/backup-gitea.sh" "$SWVAULT_DIR" "$(env_get BACKUP_DIR)" ;;
    update) require_setup; cmd_update ;;
    auto-update) require_setup; cmd_auto_update "$@" ;;
    client-config) require_setup; write_team_json; say "Wrote $TEAM_FILE"; cat "$TEAM_FILE" ;;
    logs) require_setup; compose logs --tail 200 ${1:+"$1"} ;;
    *) warn "Unknown command '$cmd'"; usage 1 ;;
esac
