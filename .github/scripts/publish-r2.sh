#!/usr/bin/env bash
# Upload both installer assets to R2, read them back, verify their SHA-256 hashes,
# and confirm their public URLs work. No GitHub release should be created on failure.
set -euo pipefail

version="${1:?Usage: publish-r2.sh VERSION [DIRECTORY]}"
dir="${2:-dist}"
: "${R2_ACCOUNT_ID:?Set the R2_ACCOUNT_ID Actions secret}"
: "${R2_ACCESS_KEY_ID:?Set the R2_ACCESS_KEY_ID Actions secret}"
: "${R2_SECRET_ACCESS_KEY:?Set the R2_SECRET_ACCESS_KEY Actions secret}"
: "${R2_PUBLIC_BASE:?Set the R2_PUBLIC_BASE Actions variable, e.g. https://downloads.lynxmotorsports.racing}"
bucket="${R2_BUCKET:-swvault-installers}"
endpoint="https://${R2_ACCOUNT_ID}.r2.cloudflarestorage.com"
public_base="${R2_PUBLIC_BASE%/}"
export AWS_ACCESS_KEY_ID="$R2_ACCESS_KEY_ID" AWS_SECRET_ACCESS_KEY="$R2_SECRET_ACCESS_KEY"
export AWS_DEFAULT_REGION=auto AWS_EC2_METADATA_DISABLED=true AWS_RETRY_MODE=standard AWS_MAX_ATTEMPTS=5

[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo 'Invalid version' >&2; exit 1; }
[[ "$public_base" == https://* ]] || { echo 'R2_PUBLIC_BASE must be an HTTPS URL' >&2; exit 1; }
[[ "$bucket" =~ ^[a-z0-9][a-z0-9.-]*$ ]] || { echo 'Invalid bucket name' >&2; exit 1; }

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
python3 .github/scripts/r2_release_manifest.py --version "$version" --directory "$dir" --output "$work/manifest.json"

readarray -t files < <(python3 - "$work/manifest.json" <<'PY'
import json, sys
m = json.load(open(sys.argv[1]))
print(m['zip']['file'])
print(m['setup']['file'])
PY
)

for file in "${files[@]}"; do
    case "$file" in
        *.zip) ctype=application/zip ;;
        *-Setup.exe) ctype=application/octet-stream ;;
        *) echo "Unexpected file: $file" >&2; exit 1 ;;
    esac
    key="releases/$version/$file"
    echo "Uploading $file -> R2 $bucket/$key"
    aws --endpoint-url "$endpoint" s3 cp "$dir/$file" "s3://$bucket/$key" --no-progress --content-type "$ctype" --cache-control 'public, max-age=31536000, immutable'

    # Read the file back to verify real bytes, not just R2 ETags (multipart ETags differ from SHA-256).
    echo "Reading back and checking SHA-256 for $file"
    aws --endpoint-url "$endpoint" s3 cp "s3://$bucket/$key" "$work/verified" --no-progress
    cmp "$dir/$file" "$work/verified" || { echo "R2 content mismatch: $file" >&2; exit 1; }
    rm -f "$work/verified"

    # Public URL must work before the GitHub release becomes available to clients.
    url="$public_base/$key"
    echo "Checking public download: $url"
    headers="$work/headers"
    curl --fail --silent --show-error --location --max-time 60 --head "$url" -D "$headers" -o /dev/null
    remote_size="$(tr -d '\r' < "$headers" | awk 'tolower($1)=="content-length:" {print $2}' | tail -1)"
    local_size="$(stat -c '%s' "$dir/$file")"
    [[ "$remote_size" == "$local_size" ]] || { echo "Public HEAD size mismatch for $file: remote='$remote_size' expected='$local_size'" >&2; exit 1; }
    # Test a small Range request without pulling the entire file across the internet.
    curl --fail --silent --show-error --location --max-time 60 --range 0-1023 --output "$work/range" "$url"
    head -c 1024 "$dir/$file" > "$work/expected"
    cmp "$work/range" "$work/expected" || { echo "Public byte-range content mismatch for $file" >&2; exit 1; }
done

key="releases/$version/manifest.json"
aws --endpoint-url "$endpoint" s3 cp "$work/manifest.json" "s3://$bucket/$key" --no-progress --content-type application/json --cache-control 'no-store'
aws --endpoint-url "$endpoint" s3 cp "s3://$bucket/$key" "$work/manifest-readback.json" --no-progress
cmp "$work/manifest.json" "$work/manifest-readback.json" || { echo 'R2 manifest readback mismatch' >&2; exit 1; }
curl --fail --silent --show-error --location --max-time 60 "$public_base/$key" -o "$work/manifest-public.json"
cmp "$work/manifest.json" "$work/manifest-public.json" || { echo 'Public R2 manifest mismatch' >&2; exit 1; }
echo "SUCCESS: Both $version installers and manifest exist in R2, match original bytes, and are publicly downloadable."
