"""Check actual signed .NET hosts across restarts and a changed code directory, with UI forbidden."""
import json
import os
import pathlib
import shutil
import subprocess
import tempfile
import uuid

root = pathlib.Path(__file__).resolve().parent.parent
report = root / 'artifacts/keychain-macos.json'
checks = {}
key = 'signing-selftest:' + uuid.uuid4().hex
source = root / 'artifacts/package/YuWangSync.app/Contents/MacOS/Engine'

def run(*args):
    return subprocess.run(args, check=True, timeout=40, capture_output=True, text=True)

def probe(engine, action):
    run(str(engine), '--keychain-probe', action, '--key', key)

def signature(engine, option):
    result = run('codesign', '-d', option, str(engine))
    return result.stdout + result.stderr

if not os.environ.get('YUWANG_SIGNING_IDENTITY'):
    report.write_text(json.dumps({'passed': True, 'skipped': True, 'reason': 'No persistent signing identity; ad-hoc builds cannot verify upgrade trust.'}))
    print('Keychain signing upgrade check skipped for ad-hoc build.')
else:
    with tempfile.TemporaryDirectory(prefix='YuWangSync-signing-') as directory:
        work = pathlib.Path(directory)
        before = work / 'before'
        after = work / 'after'
        shutil.copytree(source, before)
        shutil.copytree(source, after)
        first, second = before / 'YuWangSync.Engine', after / 'YuWangSync.Engine'
        entitlement = work / 'probe.plist'
        entitlement.write_text('<?xml version="1.0"?><!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd"><plist version="1.0"><dict><key>com.apple.security.cs.allow-jit</key><true/></dict></plist>')
        run('bash', str(root / 'tools/sign-macos.sh'), str(second), 'io.github.TOBYmiz.YuWangSync-macOS.engine', '--entitlements', str(entitlement))
        def requirement(binary):
            return next(line for line in signature(binary, '-r-').splitlines() if line.startswith('designated =>'))
        checks['same-designated-requirement'] = requirement(first) == requirement(second)
        hashes = []
        for binary in (first, second):
            hashes.append(next(line for line in signature(binary, '--verbose=4').splitlines() if line.startswith('CDHash=')))
        checks['different-code-hashes'] = hashes[0] != hashes[1]
        # An end user does not trust our private preview CA. Match that environment.
        certificate = root / 'docs/signing/preview-signing.pem'
        run('sudo', 'security', 'remove-trusted-cert', '-d', str(certificate))
        run('sudo', 'security', 'delete-certificate', '-Z', os.environ['YUWANG_SIGNING_IDENTITY'], '/Library/Keychains/System.keychain')
        trust = subprocess.run(['security', 'verify-cert', '-c', str(certificate), '-p', 'codesigning'], capture_output=True, timeout=30)
        checks['no-system-certificate-trust'] = trust.returncode != 0
        error = ''
        try:
            probe(first, 'write')
            probe(first, 'read'); checks['restart-read-without-ui'] = True
            probe(first, 'read'); checks['second-restart-read-without-ui'] = True
            probe(second, 'read'); checks['upgrade-read-without-ui'] = True
            probe(second, 'read'); checks['upgraded-restart-read-without-ui'] = True
            probe(second, 'delete'); checks['delete-without-ui'] = True
        except (subprocess.SubprocessError, OSError) as failure:
            error = type(failure).__name__
        finally:
            try:
                probe(first, 'delete')
            except (subprocess.SubprocessError, OSError):
                pass
        passed = len(checks) == 8 and all(checks.values()) and not error
        report.write_text(json.dumps({'passed': passed, 'skipped': False, 'checks': checks, 'error': error}, indent=2))
        print(report.read_text())
        if not passed:
            raise SystemExit(1)
