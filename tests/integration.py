#!/usr/bin/env python3
import concurrent.futures
import http.client
import json
import os
from pathlib import Path
import signal
import socket
import urllib.error
import subprocess
import tempfile
import time
import urllib.request

ROOT=Path(__file__).resolve().parents[1]
CADDY=ROOT/'bin/caddysharp'
PORT=18080
ADMIN=12019

def req(path, method='GET', body=None, headers=None):
 c=http.client.HTTPConnection('127.0.0.1', PORT, timeout=15)
 c.request(method,path,body=body,headers=headers or {})
 r=c.getresponse(); data=r.read(); result=(r.status,data,r.getheaders()); c.close(); return result

def main():
 config=subprocess.check_output(['python3',str(ROOT/'scripts/caddyfile.py'),'--port',str(PORT),'--admin-port',str(ADMIN)],text=True)
 with tempfile.TemporaryDirectory() as td:
  path=Path(td)/'Caddyfile';path.write_text(config)
  p=subprocess.Popen([str(CADDY),'run','--config',str(path)],cwd=ROOT,stdout=subprocess.DEVNULL,stderr=(Path(td)/'caddy.log').open('w'))
  try:
   for _ in range(80):
    try:
     if req('/health')[0]==200:break
    except Exception:time.sleep(.1)
   else:raise AssertionError('Caddy did not start: '+(Path(td)/'caddy.log').read_text())
   assert req('/text')[:2]==(200,b'hello caddysharp')
   assert json.loads(req('/json')[1])['ok']
   assert req('/echo','POST',bytes(range(256)))[1]==bytes(range(256))
   for size in (0, 1024, 65536, 1024*1024):
    payload=(bytes(range(256))*((size+255)//256))[:size]
    assert req('/echo','POST',payload)[:2]==(200,payload)
   with concurrent.futures.ThreadPoolExecutor(max_workers=16) as pool:
    payload=bytes(range(256))*256
    assert all(result[:2]==(200,payload) for result in pool.map(lambda _:req('/echo','POST',payload),range(64)))
   status,data,_=req('/inspect/%CE%A9?q=%E2%9C%93',headers={'X-Test':'one'})
   assert status==200 and json.loads(data)['path']=='/inspect/Ω' and json.loads(data)['query']=='?q=%E2%9C%93'
   assert json.loads(req('/config')[1])['value']=='example'
   assert req('/error')[0]==500 and b'controlled' not in req('/error')[1]
   assert req('/echo','POST',b'x'*(1024*1024+1))[0]==413
   assert req('/large')[0]==500
   assert req('/empty')[:2]==(204,b'')
   assert req('/inspect/abc','HEAD')[0]==200
   status,data,headers=req('/cookies');assert len([v for k,v in headers if k.lower()=='set-cookie'])==2
   status,data,headers=req('/callbacks')
   assert status==200 and data==b'callbacks'
   assert [v for k,v in headers if k.lower()=='x-order']==['second','first']
   for _ in range(30):
    if json.loads(req('/callback-count')[1])>=1:break
    time.sleep(.01)
   else:raise AssertionError('OnCompleted not called')
   with concurrent.futures.ThreadPoolExecutor(max_workers=16) as pool:
    values=list(pool.map(lambda _:req('/text')[:2],range(100)))
   assert all(v==(200,b'hello caddysharp') for v in values)
   sock=socket.create_connection(('127.0.0.1',PORT));sock.sendall(b'GET /wait HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n');time.sleep(.2);sock.close()
   for _ in range(30):
    if json.loads(req('/cancel-count')[1])>=1:break
    time.sleep(.1)
   else:raise AssertionError('RequestAborted was not signaled')
   pid=json.loads(req('/health')[1])['pid'];assert pid==p.pid
   children=(Path('/proc')/str(pid)/'task'/str(pid)/'children').read_text().strip();assert not children,children
   listeners=subprocess.check_output(['ss','-ltnp'],text=True);assert ':8081 ' not in listeners
   urllib.request.urlopen(urllib.request.Request(f'http://localhost:{ADMIN}/load',data=subprocess.check_output([str(CADDY),'adapt','--config',str(path)],stderr=subprocess.DEVNULL),headers={'Content-Type':'application/json'})).read()
   assert req('/text')[1]==b'hello caddysharp'
   assert json.loads(req('/startup-count')[1])==1
   altered=Path(td)/'Changed.Caddyfile';altered.write_text(config.replace('example','changed'))
   changed=subprocess.check_output([str(CADDY),'adapt','--config',str(altered)],stderr=subprocess.DEVNULL)
   try:urllib.request.urlopen(urllib.request.Request(f'http://localhost:{ADMIN}/load',data=changed,headers={'Content-Type':'application/json'}),timeout=5);raise AssertionError('incompatible reload accepted')
   except urllib.error.HTTPError as ex:assert ex.code>=400
   assert req('/text')[1]==b'hello caddysharp'
   print('integration: PASS; PID',pid)
  finally:
   try:urllib.request.urlopen(urllib.request.Request(f'http://localhost:{ADMIN}/stop',data=b'',method='POST'),timeout=5).read()
   except Exception:p.terminate()
   try:p.wait(timeout=10)
   except subprocess.TimeoutExpired:p.kill();p.wait()
   if p.returncode:print((Path(td)/'caddy.log').read_text()[-4000:])

if __name__=='__main__':main()
