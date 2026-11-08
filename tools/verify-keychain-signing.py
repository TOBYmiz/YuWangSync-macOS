"""Prepare signed hosts, then verify them on a fresh runner without signing keys or certificate trust."""
import json
import os
import pathlib
import shutil
import subprocess
import sys
import uuid

root = pathlib.Path(__file__).resolve().parent.parent
work = root / 'artifacts/keychain-probe'
report = root / 'artifacts/keychain-macos.json'
identifier = 'io.github.TOBYmiz.YuWangSync-macOS.engine'
source = root / 'artifacts/package/YuWangSync.app/Contents/MacOS/Engine'

def run(*args):
    return subprocess.run(args, check=True, timeout=40, capture_output=True, text=True)

if sys.argv[1:] == ['prepare']:
    if not os.environ.get('YUWANG_SIGNING_IDENTITY'):
        print('No persistent identity; skipping upgrade fixture on this ad-hoc build.')
        raise SystemExit(0)
    work.mkdir(parents=True)
    shutil.copytree(source, work / 'before')
    shutil.copytree(source, work / 'after')
    entitlement = work / 'probe.plist'
    entitlement.write_text('<?xml version="1.0"?><!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd"><plist version="1.0"><dict><key>com.apple.security.cs.allow-jit</key><true/></dict></plist>')
    run('bash', str(root / 'tools/sign-macos.sh'), str(work / 'after/YuWangSync.Engine'), identifier, '--entitlements', str(entitlement))
    run('ditto', '-c', '-k', '--keepParent', str(work), str(root / 'artifacts/keychain-probe.zip'))
    print('Prepared signed Keychain upgrade fixture; contains no private key or credentials.')
elif sys.argv[1:] == ['verify']:
    checks, error = {}, ''
    key = 'signing-selftest:' + uuid.uuid4().hex
    first, second = work / 'before/YuWangSync.Engine', work / 'after/YuWangSync.Engine'

    def probe(engine, action):
        run(str(engine), '--keychain-probe', action, '--key', key)

    def signature(engine, option):
        result = run('codesign', '-d', option, str(engine))
        return result.stdout + result.stderr

    def requirement(binary):
        return next(line for line in signature(binary, '-r-').splitlines() if line.startswith('designated =>'))

    try:
        certificate = root / 'docs/signing/preview-signing.pem'
        fingerprint = run('openssl', 'x509', '-in', str(certificate), '-noout', '-fingerprint', '-sha1').stdout.strip().split('=')[1].replace(':', '')
        for binary in (first, second):
            run('codesign', '--verify', '--strict', str(binary))
        before, after = requirement(first), requirement(second)
        checks['same-designated-requirement'] = before == after
        checks['pinned-preview-certificate'] = identifier in before and fingerprint.lower() in before.lower()
        hashes = [next(line for line in signature(binary, '--verbose=4').splitlines() if line.startswith('CDHash=')) for binary in (first, second)]
        checks['different-code-hashes'] = hashes[0] != hashes[1]
        trust = subprocess.run(['security', 'verify-cert', '-c', str(certificate), '-p', 'codeSign'], capture_output=True, timeout=30)
        checks['no-system-certificate-trust'] = trust.returncode != 0
        if not all(checks.values()):
            raise ValueError('The fixture does not model an untrusted-certificate update.')
        probe(first, 'legacy-write')
        probe(first, 'read'); checks['legacy-service-migration'] = True
        probe(first, 'legacy-delete')
        probe(first, 'read'); checks['migrated-item-independent-of-legacy'] = True
        probe(first, 'delete')
        probe(first, 'write')
        probe(first, 'read'); checks['restart-read-without-ui'] = True
        probe(first, 'read'); checks['second-restart-read-without-ui'] = True
        try:
            probe(second, 'read')
            checks['upgrade-requires-authorization'] = False
        except subprocess.CalledProcessError as failure:
            checks['upgrade-requires-authorization'] = failure.returncode == 1 and 'authorization was not retained' in failure.stderr
        # A fresh save models the authorized new version. Only synthetic CI data is recreated.
        probe(first, 'delete')
        probe(second, 'write')
        probe(second, 'read'); checks['new-version-restart-read-without-ui'] = True
        probe(second, 'read'); checks['new-version-second-restart-read-without-ui'] = True
        probe(second, 'delete'); checks['delete-without-ui'] = True
    except (subprocess.SubprocessError, OSError, StopIteration, ValueError) as failure:
        error = str(failure)
    finally:
        try:
            probe(first, 'delete')
        except (subprocess.SubprocessError, OSError):
            pass
    passed = len(checks) == 12 and all(checks.values()) and not error
    report.write_text(json.dumps({'passed': passed, 'upgradeAuthorizationRequired': True, 'checks': checks, 'error': error}, indent=2))
    print(report.read_text())
    if not passed:
        raise SystemExit(1)
else:
    raise SystemExit('Use prepare on the signing runner and verify on a fresh runner.')
