#!/usr/bin/env python3
"""Black-box checks against published Flow CLI/server binaries; never uses repository databases."""
import argparse
import json
import os
from pathlib import Path
import socket
import sqlite3
import subprocess
import tempfile
import time
import urllib.error
import urllib.request

parser = argparse.ArgumentParser()
parser.add_argument('--cli', type=Path, required=True)
parser.add_argument('--server', type=Path)
parser.add_argument('--data-directory', type=Path)
args = parser.parse_args()
root = args.data_directory or Path(tempfile.mkdtemp(prefix='gnougo-flow-v9-published-'))
root.mkdir(parents=True, exist_ok=True)
marker = 'private-published-journal-f920dd'
yaml = '''version: 1
workflows:
  main:
    inputs:
      marker: {type: string, required: true}
    steps:
      - id: copy
        type: set
        input: {value: "${data.inputs.marker}"}
    finally:
      - id: cleanup
        type: set
        input: {done: true}
    outputs:
      value: "${data.steps.copy.value}"
'''
workflow = root / 'smoke.yaml'
workflow.write_text(yaml)
env = dict(os.environ, KeyVault__DatabasePath=str(root / 'vault.db'),
           Flow__Execution__IndexPath=str(root / 'index.db'), Flow__Execution__OwnerPath=str(root / 'owners'),
           OpenTelemetry__Enabled='false', OpenTelemetry__TenantId='smoke')

def cli(*arguments, success=True):
    result = subprocess.run([str(args.cli.resolve()), *arguments], cwd=root, env=env, text=True, capture_output=True, timeout=60)
    if (result.returncode == 0) != success:
        raise AssertionError(result.stdout + result.stderr)
    return result.stdout

cli('run', str(workflow), '--mock', '--run-id', 'cli', '--input-json', json.dumps({'marker': marker}))
run = json.loads(cli('runs', '--tenant', 'smoke', '--id', 'cli'))
assert run['schemaVersion'] == 9 and run['status'] == 'completed' and run['finalizationCompleted']
assert run['result']['outputs']['value'] == marker
assert len(run['invocations']) == 2
assert all(i['completedAt'] for i in run['invocations'].values())
assert json.loads(cli('runs', '--tenant', 'other', '--id', 'cli')) is None
cli('run', str(workflow), '--mock', '--run-id', 'cli', '--input-json', json.dumps({'marker': marker}), success=False)
cli('run', str(workflow), '--mock', '--run-id', 'cli', '--resume-revision', str(run['revision']))
assert run == json.loads(cli('runs', '--tenant', 'smoke', '--id', 'cli'))
cli('run', str(workflow), '--mock', '--run-id', 'cli', '--resume-revision', str(run['revision'] - 1), success=False)
with sqlite3.connect(root / 'index.db') as db:
    assert db.execute('SELECT TenantId, RunId, Revision, Status FROM WorkflowRuns').fetchall() == [('smoke', 'cli', run['revision'], 'completed')]
    db.execute('DROP TABLE WorkflowRuns')
assert len(json.loads(cli('runs', '--tenant', 'smoke'))) == 1
with sqlite3.connect(root / 'index.db') as db:
    assert db.execute('SELECT COUNT(*) FROM WorkflowRuns').fetchone()[0] == 1
print('PASS published CLI: encrypted receipts, native EF query/index rebuild, tenant isolation, retained results and revision checks')

if args.server:
    with socket.socket() as probe:
        probe.bind(('127.0.0.1', 0)); port = probe.getsockname()[1]
    address = f'http://127.0.0.1:{port}'
    def request(path, data=None, expected=200):
        req = urllib.request.Request(address + path, data=None if data is None else json.dumps(data).encode(), headers={'Content-Type': 'application/json'})
        try:
            with urllib.request.urlopen(req, timeout=20) as response:
                body = response.read(); code = response.status
        except urllib.error.HTTPError as error:
            body = error.read(); code = error.code
        assert code == expected, (path, code, body.decode())
        return json.loads(body) if body else None
    def start():
        output = open(root / 'server.log', 'a')
        server = subprocess.Popen([str(args.server.resolve()), f'--urls={address}'], cwd=args.server.resolve().parent, env=env, stdout=output, stderr=output)
        for _ in range(200):
            if server.poll() is not None: raise AssertionError((root / 'server.log').read_text())
            try:
                request('/api/tenants/smoke/runs'); return server, output
            except (OSError, urllib.error.URLError): time.sleep(.1)
        server.terminate(); raise AssertionError('Published server startup timed out')
    server, output = start()
    try:
        result = request('/api/workflow/run', {'runId': 'server', 'workflow': yaml, 'inputs': json.dumps({'marker': marker})})
        assert result['success'] and result['outputs']['value'] == marker
        saved = request('/api/tenants/smoke/runs/server')
        assert saved['finalizationCompleted'] and saved['status'] == 'completed'
        request('/api/tenants/other/runs/server', expected=404)
        request('/api/tenants/smoke/runs/server/resume', {'expectedRevision': saved['revision'] - 1}, expected=409)
    finally:
        server.terminate(); server.wait(timeout=30); output.close()
    server, output = start()
    try:
        assert saved == request('/api/tenants/smoke/runs/server')
        request('/api/tenants/smoke/runs/server/resume', {'expectedRevision': saved['revision']})
        assert saved == request('/api/tenants/smoke/runs/server')
    finally:
        server.terminate(); server.wait(timeout=30); output.close()
    print('PASS published server: persisted encrypted journal across restart, tenant isolation and revision-checked resume')

for path in root.glob('*.db*'):
    assert marker.encode() not in path.read_bytes(), f'Unencrypted payload in {path.name}'
print('PASS no workflow content in database plaintext; evidence directory:', root)
