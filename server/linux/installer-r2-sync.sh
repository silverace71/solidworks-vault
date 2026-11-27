#!/usr/bin/env bash
# Installer-only GitHub Releases -> R2 -> local SwVault invite service synchronization.
# Designed for Cloudflare SWVAULT deployments: does NOT replace live server code.
# Requires a PUBLIC GitHub repository; GitHub tokens/secrets are NOT needed.
# R2 credentials stay in a root-owned local rclone config, never in GitHub.
set -euo pipefail
[[ ${EUID:-$(id -u)} -eq 0 ]] || { echo 'Run as root (sudo).' >&2; exit 1; }
cd "$(dirname "${BASH_SOURCE[0]}")"
source ./lib.sh

config="$DATA_DIR/installer-r2-sync.env"
[[ -f "$config" ]] || die "Missing $config; follow docs/INSTALLER-R2-NO-GITHUB-SECRETS.md"
[[ "$(stat -c %u "$config")" == 0 ]] || die 'Installer sync config must be owned by root'
[[ ! -n "$(find "$config" -perm /022 -print)" ]] || die 'Installer sync config must not be group/world writable'
# shellcheck disable=SC1090
source "$config"
repo="${GITHUB_RELEASE_REPO:-}"
bucket="${INSTALLER_R2_BUCKET:-swvault-installers}"
public="${INSTALLER_R2_PUBLIC_BASE:-}"
rclone_config="${INSTALLER_R2_RCLONE_CONFIG:-$DATA_DIR/r2-rclone.conf}"
required="${INSTALLER_REQUIRED:-false}"
[[ "$repo" =~ ^[A-Za-z0-9._-]+/[A-Za-z0-9._-]+$ ]] || die 'Set GITHUB_RELEASE_REPO=owner/repo (public repository)'
[[ "$bucket" =~ ^[a-z0-9][a-z0-9.-]{1,61}[a-z0-9]$ ]] || die 'Invalid INSTALLER_R2_BUCKET'
[[ "$public" =~ ^https://[A-Za-z0-9.-]+$ ]] || die 'Set INSTALLER_R2_PUBLIC_BASE=https://downloads.your-domain.example (without trailing slash)'
[[ "$required" == true || "$required" == false ]] || die 'INSTALLER_REQUIRED must be true or false'
[[ -r "$rclone_config" ]] || die "No readable rclone config: $rclone_config"
for cmd in curl jq rclone sha256sum stat flock cmp python3; do command -v "$cmd" >/dev/null || die "Missing command: $cmd"; done
[[ -x ./swvault-admin.sh ]] || die 'Expected swvault-admin.sh alongside this script'
[[ $# -le 1 && ( $# -eq 0 || "$1" == '--dry-run' ) ]] || die 'Usage: sudo ./installer-r2-sync.sh [--dry-run]'
dry_run=false; [[ "${1:-}" == --dry-run ]] && dry_run=true

mkdir -p -m 700 "$DATA_DIR"
exec 9>"$DATA_DIR/installer-r2-sync.lock"
flock -n 9 || { echo 'Installer sync already running; exiting.'; exit 0; }
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

say "Checking public GitHub release $repo"
curl --fail --silent --show-error --location --retry 3 --connect-timeout 15 --max-time 60 \
    -H 'Accept: application/vnd.github+json' "https://api.github.com/repos/$repo/releases?per_page=30" -o "$work/releases.json" \
    || die 'Could not read public GitHub releases. The repository must be public; no credentials are used.'
# The newest GitHub release might contain only server files. Select the newest release
# carrying a complete installer set; never deploy server files from this script.
jq -e 'type == "array"' "$work/releases.json" >/dev/null || die 'Unexpected GitHub releases response'
jq '[.[] | select(.draft == false and .prerelease == false) | select(.tag_name | test("^v[0-9]+[.][0-9]+[.][0-9]+$")) | select(any(.assets[]?; .name == "manifest.json")) | select(any(.assets[]?; .name | endswith(".zip"))) | select(any(.assets[]?; .name | endswith("-Setup.exe")))] | first // {}' "$work/releases.json" > "$work/release.json"
version="$(jq -r '.tag_name // ""' "$work/release.json")"
if [[ -z "$version" ]]; then
    say 'No public installer release with manifest found in the newest 30 releases; nothing to sync.'
    exit 0
fi
version="${version#v}"

published="$(curl --silent --show-error --max-time 20 -o "$work/published.json" -w '%{http_code}' 'http://127.0.0.1:3100/installer/latest')" \
    || die 'Local invite service is not responding'
case "$published" in
    200) current="$(jq -r '.version // "0.0.0"' "$work/published.json")" ;;
    404) current='0.0.0' ;;
    *) die "Invite service responded HTTP $published" ;;
esac
[[ "$current" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || die "Unrecognized published version: $current"
if [[ "$(printf '%s\n%s\n' "$version" "$current" | sort -V | tail -n 1)" != "$version" || "$version" == "$current" ]]; then
    say "Already published $current, GitHub latest is $version; no change."
    exit 0
fi

# For consistency with the release workflow, require a manifest with checked hashes.
manifest_url="$(jq -r '.assets[]? | select(.name=="manifest.json") | .browser_download_url' "$work/release.json" | head -n 1)"
[[ "$manifest_url" == "https://github.com/$repo/releases/download/v$version/manifest.json" ]] \
    || die 'Release missing manifest.json from matching public GitHub release. Refusing unverified installer.'
curl -fSsL --retry 3 --connect-timeout 15 --max-time 120 "$manifest_url" -o "$work/manifest.json" \
    || die 'Failed to download release manifest'
jq -e --arg version "$version" '.schema == 1 and .version == $version and (.zip.file | type == "string") and (.setup.file | type == "string")' "$work/manifest.json" >/dev/null \
    || die 'Invalid release manifest schema or version'
zip="$(jq -r .zip.file "$work/manifest.json")"
setup="$(jq -r .setup.file "$work/manifest.json")"
[[ "$zip" =~ ^SwVault-[A-Za-z0-9_-]+-[0-9]+\.[0-9]+\.[0-9]+\.zip$ && "$zip" == *"-$version.zip" ]] || die 'Unsafe or unexpected ZIP asset filename'
[[ "$setup" == "${zip%.zip}-Setup.exe" ]] || die 'Setup EXE does not match the ZIP team/version name'
for kind in zip setup; do
    if [[ "$kind" == zip ]]; then file="$zip"; else file="$setup"; fi
    sha="$(jq -r --arg k "$kind" '.[$k].sha256 // ""' "$work/manifest.json")"
    size="$(jq -r --arg k "$kind" '.[$k].size // ""' "$work/manifest.json")"
    [[ "$sha" =~ ^[0-9a-f]{64}$ && "$size" =~ ^[0-9]+$ && "$size" -gt 0 ]] || die "Invalid $kind SHA-256/size in manifest"
    url="$(jq -r --arg n "$file" '.assets[]? | select(.name==$n) | .browser_download_url' "$work/release.json" | head -n 1)"
    [[ "$url" == "https://github.com/$repo/releases/download/v$version/$file" ]] || die "GitHub release missing matching $file"
    api_size="$(jq -r --arg n "$file" '.assets[]? | select(.name==$n) | .size' "$work/release.json" | head -n 1)"
    [[ "$api_size" == "$size" ]] || die "Release asset size differs from manifest for $file"
    say "Downloading $file from GitHub"
    curl -fSsL --retry 3 --connect-timeout 20 --max-time 1800 "$url" -o "$work/$file" || die "Could not download $file"
    [[ "$(stat -c %s "$work/$file")" == "$size" ]] || die "Incomplete GitHub asset: $file"
    [[ "$(sha256sum "$work/$file" | cut -d ' ' -f1)" == "$sha" ]] || die "Checksum mismatch for $file"
    if [[ "$kind" == setup ]]; then
        [[ "$(head -c 2 "$work/$file")" == MZ ]] || die 'Installer EXE does not have the MZ signature'
    fi
done
# Ensure embedded ZIP version matches tag/manifest too.
python3 - "$work/$zip" "$version" <<'PY'
import json, sys, zipfile
with zipfile.ZipFile(sys.argv[1]) as z:
    pkg = json.loads(z.read('swvault-package.json').decode('utf-8-sig'))
if pkg.get('version') != sys.argv[2]:
    raise SystemExit('Installer ZIP contains a different version than the manifest')
PY

if [[ "$dry_run" == true ]]; then
    say "DRY RUN PASS: Verified $version from GitHub, no R2 files or client metadata changed."
    exit 0
fi

say "Uploading verified $version installers to R2 $bucket"
rclone --config "$rclone_config" lsf "r2:$bucket" --max-depth 1 >/dev/null || die "R2 credentials cannot access $bucket; configure bucket-scoped installer credentials on Linux"
for file in "$zip" "$setup" manifest.json; do
    key="releases/$version/$file"
    rclone --config "$rclone_config" copyto "$work/$file" "r2:$bucket/$key" --retries 3 --low-level-retries 5 --stats 0 || die "R2 upload failed for $file"
    rclone --config "$rclone_config" copyto "r2:$bucket/$key" "$work/readback-$file" --retries 3 --stats 0 || die "R2 readback failed for $file"
    cmp -s "$work/$file" "$work/readback-$file" || die "R2 bytes differ from original for $file"
    url="$public/$key"
    headers="$work/headers-$file"
    curl -fSsL --retry 3 --connect-timeout 20 --max-time 120 --head "$url" -D "$headers" -o /dev/null || die "Public R2 download not reachable: $url"
    if [[ "$file" != manifest.json ]]; then
        expected="$(stat -c %s "$work/$file")"
        actual="$(tr -d '\r' < "$headers" | awk 'tolower($1)=="content-length:" {print $2}' | tail -n 1)"
        [[ "$expected" == "$actual" ]] || die "Public download size mismatch for $file: $actual != $expected"
        curl -fSsL --retry 3 --max-time 120 -r 0-1023 "$url" -o "$work/public-first" || die "Public R2 byte-range failed: $url"
        head -c 1024 "$work/$file" > "$work/local-first"
        cmp -s "$work/public-first" "$work/local-first" || die "Public R2 content mismatch for $file"
    else
        curl -fSsL --retry 3 --max-time 120 "$url" -o "$work/public-manifest.json" || die 'Public manifest request failed'
        cmp -s "$work/manifest.json" "$work/public-manifest.json" || die 'Public manifest differs from verified GitHub manifest'
    fi
done

say "R2 public URLs verified; now publishing $version into the existing local SWVAULT update service"
args=(--setup "$work/$setup" --notes "Published from verified GitHub release $repo v$version")
[[ "$required" == true ]] && args+=(--required)
"$SWVAULT_DIR/swvault-admin.sh" publish-installer "$work/$zip" "${args[@]}" \
    || die 'Local metadata publication failed. Previous version may remain; check /installer/latest before retrying.'
meta="$(curl -fSsS --max-time 20 'http://127.0.0.1:3100/installer/latest')" || die 'Local publication did not respond'
[[ "$(jq -r '.version' <<<"$meta")" == "$version" ]] || die 'Local installer version mismatch after publishing'
[[ "$(jq -r '.sha256' <<<"$meta")" == "$(jq -r '.zip.sha256' "$work/manifest.json")" ]] || die 'Published ZIP hash does not match R2 manifest'
say "SUCCESS: $version is in R2, downloadable, and advertised to existing SWVAULT clients."
