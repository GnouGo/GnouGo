#!/usr/bin/env python3
"""Black-box checks against published Flow CLI/server binaries; never uses repository databases."""
import argparse
import asyncio
from concurrent.futures import ThreadPoolExecutor
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
parser.add_argument('--copilot', type=Path)
parser.add_argument('--data-directory', type=Path)
parser.add_argument('--python-client', action='store_true', help='Also exercise the installed schema-9 Python run client')
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
    run_client = None
    if args.python_client:
        from gnougo_flow_core import WorkflowRunClient
        run_client = WorkflowRunClient(address, 'smoke')
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
        human_yaml = '''version: 1
workflows:
  main:
    steps:
      - id: ask
        type: human.input
        input: {prompt: "Approve the published fixture", timeout_ms: 15000}
    outputs:
      answer: "${data.steps.ask.response}"
'''
        def stream():
            payload = {'runId': 'human', 'workflow': human_yaml}
            req = urllib.request.Request(address + '/api/workflow/run/stream', data=json.dumps(payload).encode(), headers={'Content-Type': 'application/json'})
            with urllib.request.urlopen(req, timeout=20) as response:
                return [json.loads(line) for line in response if line.strip()]
        with ThreadPoolExecutor(max_workers=1) as pool:
            pending_stream = pool.submit(stream)
            for _ in range(100):
                pending = request('/api/tenants/smoke/runs')
                pending = next((r for r in pending if r['runId'] == 'human'), None)
                dialogs = [] if pending is None else [v for v in pending['invocations'].values() if v['status'] == 'waiting_for_human']
                if dialogs: break
                if pending_stream.done(): raise AssertionError(pending_stream.result())
                time.sleep(.1)
            assert len(dialogs) == 1, pending
            invocation = next(k for k, v in pending['invocations'].items() if v['status'] == 'waiting_for_human')
            answer = {'expectedRevision': pending['revision'] - 1, 'invocationId': invocation, 'response': {'response': 'approved'}}
            request('/api/tenants/smoke/runs/human/human-input', answer, expected=409)
            answer['expectedRevision'] = pending['revision']
            if run_client:
                asyncio.run(run_client.answer_async('human', answer['expectedRevision'], invocation, answer['response']))
            else:
                request('/api/tenants/smoke/runs/human/human-input', answer)
            events = pending_stream.result(timeout=20)
            final = next(e for e in events if e['type'] == 'workflow.result')
            assert final['data']['response']['success'], final
            assert final['data']['response']['outputs']['answer'] == 'approved', final
            assert request('/api/tenants/smoke/runs/human')['status'] == 'completed'
    finally:
        server.terminate(); server.wait(timeout=30); output.close()
    server, output = start()
    try:
        assert saved == request('/api/tenants/smoke/runs/server')
        if run_client:
            assert asyncio.run(run_client.read_async('server')) == saved
            assert saved in asyncio.run(run_client.list_async())
            assert asyncio.run(run_client.command_async('server', 'resume', saved['revision'])) == saved
        else:
            request('/api/tenants/smoke/runs/server/resume', {'expectedRevision': saved['revision']})
        assert saved == request('/api/tenants/smoke/runs/server')
    finally:
        server.terminate(); server.wait(timeout=30); output.close()
    print('PASS published server: encrypted restart recovery, streamed durable human answers, tenant isolation and revision checks')
    if run_client:
        print('PASS Python schema-9 client: native host journal inspection, durable human answers and receipt reuse after restart')

if args.copilot:
    mcp_env = dict(env, Code__DefaultWorkingDirectory=str(root), Code__AllowedWorkingRoots__0=str(root))
    with open(root / 'copilot-mcp.log', 'w') as errors, ThreadPoolExecutor(max_workers=1) as pool:
        mcp = subprocess.Popen([str(args.copilot.resolve())], cwd=args.copilot.resolve().parent,
                               env=mcp_env, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=errors, text=True)
        def rpc(identity, method, params):
            mcp.stdin.write(json.dumps({'jsonrpc': '2.0', 'id': identity, 'method': method, 'params': params}) + '\n'); mcp.stdin.flush()
            while True:
                line = pool.submit(mcp.stdout.readline).result(timeout=30)
                assert line, 'Published MCP exited before replying'
                response = json.loads(line)
                if response.get('id') == identity:
                    assert 'error' not in response, response
                    return response['result']
        def content(response):
            assert not response.get('isError'), response
            return response.get('structuredContent') or json.loads(response['content'][0]['text'])
        try:
            rpc(1, 'initialize', {'protocolVersion': '2025-11-25', 'capabilities': {}, 'clientInfo': {'name': 'flow-v9-published', 'version': '1.0'}})
            mcp.stdin.write(json.dumps({'jsonrpc': '2.0', 'method': 'notifications/initialized'}) + '\n'); mcp.stdin.flush()
            assert content(rpc(2, 'tools/call', {'name': 'copilot_task_contract', 'arguments': {}}))['schemaVersion'] == 9
            context = {'tenant_id': 'smoke', 'run_id': 'rejected-fixture', 'invocation_id': 'main/task', 'task': {
                'runner': 'coding', 'objective': 'This invalid scope must never dispatch inference.', 'workspace': str(root),
                'inputs': {}, 'output_schema': {'type': 'object'}, 'capabilities': ['unsupported-fixture-capability'],
                'budget': {'max_model_calls': 1, 'max_total_tokens': 1000, 'max_elapsed_milliseconds': 1000},
                'verification': []}}
            arguments = {'contextJson': json.dumps(context)}
            assert content(rpc(3, 'tools/call', {'name': 'copilot_task_validate', 'arguments': arguments}))['errors']
            receipt = content(rpc(4, 'tools/call', {'name': 'copilot_task_run', 'arguments': arguments}))
            assert receipt['schemaVersion'] == 9 and receipt['result']['status'] == 'failed'
            assert receipt['result']['usage']['model_calls'] == 0
        finally:
            mcp.terminate(); mcp.wait(timeout=30)
    print('PASS published Copilot MCP: bounded protocol, unsupported-scope refusal before inference, terminal failed receipt envelope')

for path in root.glob('*.db*'):
    assert marker.encode() not in path.read_bytes(), f'Unencrypted payload in {path.name}'
print('PASS no workflow content in database plaintext; evidence directory:', root)
