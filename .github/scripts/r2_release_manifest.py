#!/usr/bin/env python3
"""Validate a packaged SwVault release and write the R2 manifest.

Safe to run locally; does not need network access or credentials.
"""
import argparse
import hashlib
import json
from pathlib import Path
import re
import sys
import zipfile


def artifact_info(path: Path):
    h = hashlib.sha256()
    with path.open('rb') as inp:
        for chunk in iter(lambda: inp.read(4 * 1024 * 1024), b''):
            h.update(chunk)
    return {'file': path.name, 'sha256': h.hexdigest(), 'size': path.stat().st_size}


def build_manifest(directory: Path, version: str):
    if not re.fullmatch(r'\d+\.\d+\.\d+', version):
        raise ValueError('Version must be MAJOR.MINOR.PATCH')
    zip_files = list(directory.glob(f'SwVault-*-{version}.zip'))
    setup_files = list(directory.glob(f'SwVault-*-{version}-Setup.exe'))
    if len(zip_files) != 1 or len(setup_files) != 1:
        raise ValueError(f'Expected exactly one matching zip and setup for {version}; found {zip_files} and {setup_files}')
    zip_path, setup_path = zip_files[0], setup_files[0]
    if setup_path.name != zip_path.stem + '-Setup.exe':
        raise ValueError('ZIP and setup EXE do not have matching team/version names')
    with setup_path.open('rb') as exe:
        signature = exe.read(2)
    if signature != b'MZ':
        raise ValueError('Setup EXE does not begin with an MZ header')
    with zipfile.ZipFile(zip_path) as z:
        package = json.loads(z.read('swvault-package.json').decode('utf-8-sig'))
    if package.get('version') != version:
        raise ValueError(f'ZIP embedded version is {package.get("version")!r}, expected {version}')
    return {'schema': 1, 'version': version, 'zip': artifact_info(zip_path), 'setup': artifact_info(setup_path)}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--version', required=True)
    parser.add_argument('--directory', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    try:
        manifest = build_manifest(args.directory, args.version)
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(manifest, indent=2) + '\n', encoding='utf-8')
        print('Validated SwVault release:', args.version)
        for kind in ('zip', 'setup'):
            asset = manifest[kind]
            print(f'  {kind}: {asset["file"]} ({asset["size"]} bytes), sha256={asset["sha256"]}')
        print('Manifest:', args.output)
    except (OSError, ValueError, KeyError, zipfile.BadZipFile, json.JSONDecodeError) as error:
        parser.exit(1, f'error: {error}\n')


if __name__ == '__main__':
    main()
