"""Bounded local-only MCP check; arguments: published executable, stderr log path."""
import http.server
import json
import os
from pathlib import Path
import subprocess
import sys
import threading
from concurrent.futures import ThreadPoolExecutor

binary = Path(sys.argv[1]).resolve()
html = ('<html><body><main>' + ''.join('<p>Observation %d <a href="/entry/%d">Observed link %d</a></p>' % (i,i,i) for i in range(30)) + '</main></body></html>').encode()
class Page(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        self.send_response(200)
        self.send_header('Content-Type','text/html; charset=utf-8')
        self.send_header('Content-Length',str(len(html)))
        self.end_headers()
        self.wfile.write(html)
    def log_message(self, *args):
        pass
server = http.server.ThreadingHTTPServer(('127.0.0.1',0),Page)
threading.Thread(target=server.serve_forever,daemon=True).start()
url = 'http://127.0.0.1:%d/' % server.server_port
env = dict(os.environ, Browser__Headless='true', Browser__SlowMoMs='0', Browser__HoldOpenMs='0', Browser__KeepBrowserOpen='false', Browser__AllowedHosts__0='127.0.0.1', OpenTelemetry__Enabled='false')
with open(sys.argv[2], 'w') as errors, ThreadPoolExecutor(max_workers=1) as pool:
    process = subprocess.Popen([str(binary)],cwd=binary.parent,env=env,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=errors,text=True)
    counter = 0
    def rpc(method, params):
        global counter
        counter += 1
        process.stdin.write(json.dumps({'jsonrpc':'2.0','id':counter,'method':method,'params':params})+'\n')
        process.stdin.flush()
        while True:
            line = pool.submit(process.stdout.readline).result(timeout=30)
            assert line, 'Published MCP exited without reply'
            reply=json.loads(line)
            if reply.get('id')==counter:
                assert 'error' not in reply, reply
                return reply['result']
    def call(name, **arguments):
        return rpc('tools/call',{'name':name,'arguments':arguments})
    def content(result):
        assert not result.get('isError'), result
        return result.get('structuredContent') or json.loads(result['content'][0]['text'])
    try:
        rpc('initialize',{'protocolVersion':'2025-11-25','capabilities':{},'clientInfo':{'name':'cursor-published-smoke','version':'1.0'}})
        process.stdin.write(json.dumps({'jsonrpc':'2.0','method':'notifications/initialized'})+'\n');process.stdin.flush()
        tools={t['name']:t for t in rpc('tools/list',{})['tools']}
        assert 'either observation format' in tools['browser_get_content']['description']
        limited=content(call('browser_get_content',url=url,waitUntil='domcontentloaded',selector='main',format='observation_pages',maxRecords=2,maxCharacters=2400))['observationManifest']
        assert limited['manifestTruncated'] and not limited['captureTruncated'], limited
        manifest=content(call('browser_get_content',selector='main',format='observation_pages',maxRecords=4,maxCharacters=2400))['observationManifest']
        assert len(manifest['pages'])>3 and not manifest['captureTruncated'] and not manifest['manifestTruncated'],manifest
        assert manifest['recordCount']==limited['recordCount']==60
        records=[]
        for page in manifest['pages']:
            first=content(call('browser_get_content',format='observation',cursor=page['cursor']))
            second=content(call('browser_get_content',format='observation_pages',cursor=page['cursor']))
            assert first==second
            records.extend(first['observation']['records'])
        assert len(records)==manifest['recordCount']
        for fmt in ('observation','observation_pages'):
            assert call('browser_get_content',format=fmt,cursor=manifest['pages'][0]['cursor'],maxRecords=2).get('isError')
        content(call('browser_get_content',url=url,waitUntil='domcontentloaded',format='observation_pages'))
        for fmt in ('observation','observation_pages'):
            assert call('browser_get_content',format=fmt,cursor=manifest['pages'][0]['cursor']).get('isError')
        content(call('browser_close'))
        assert call('browser_get_content').get('isError')
        print(json.dumps({'passed':True,'transport':'published MCP stdio','pages':len(manifest['pages']),'records':len(records),'equivalent_formats':True,'manifest_cap_reported':True,'bounds_and_stale_cursors_rejected':True,'cleanup_verified':True,'model_calls':0}))
    finally:
        process.terminate();process.wait(timeout=30);server.shutdown()
