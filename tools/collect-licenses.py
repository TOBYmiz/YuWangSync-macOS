"""Collect notices from the exact runtime pack selected by the pinned SDK."""
import json
from pathlib import Path
import subprocess
import sys

root = Path(__file__).resolve().parents[1]
output = Path(sys.argv[1])
resolved = json.loads(subprocess.check_output([
    'dotnet', 'msbuild', str(root / 'src/YuWangSync.Engine/YuWangSync.Engine.csproj'),
    '-p:RuntimeIdentifier=osx-arm64', '-p:SelfContained=true', '-t:ProcessFrameworkReferences',
    '-getItem:RuntimePack', '-getProperty:NuGetPackageRoot', '-verbosity:quiet'
], text=True, encoding='utf-8'))
pack = next(item for item in resolved['Items']['RuntimePack'] if item['FrameworkName'] == 'Microsoft.NETCore.App')
runtime = Path(resolved['Properties']['NuGetPackageRoot']) / pack['NuGetPackageId'].lower() / pack['NuGetPackageVersion']
sources = [
    ('YuWangSync-macOS / TOBYmiz / MIT', root / 'LICENSE'),
    ('Microsoft.Data.Sqlite / MIT / .NET Foundation and Contributors', root / 'docs/licenses/Microsoft.Data.Sqlite-MIT.txt'),
    ('SQLitePCLRaw / Apache-2.0 / Copyright 2014-2024 SourceGear, LLC', root / 'docs/licenses/SQLitePCLRaw-Apache-2.0.txt'),
    ('SQLite / Public Domain', root / 'docs/licenses/SQLite-Public-Domain.txt'),
    ('.NET Runtime ' + pack['NuGetPackageVersion'] + ' / MIT', runtime / 'LICENSE.TXT'),
    ('.NET Runtime third-party notices', runtime / 'THIRD-PARTY-NOTICES.TXT'),
]
sections = ['Open Source Notices\n育网云 macOS · 作者 TOBYmiz\n']
for title, source in sources:
    text = source.read_text(encoding='utf-8-sig')
    if len(text.strip()) < 50:
        raise RuntimeError('Missing or incomplete license: ' + str(source))
    sections.append('\n' + '=' * 72 + '\n' + title + '\n' + '=' * 72 + '\n\n' + text)
output.parent.mkdir(parents=True, exist_ok=True)
output.write_text('\n'.join(sections), encoding='utf-8')
print('Collected license notices for .NET runtime ' + pack['NuGetPackageVersion'])
