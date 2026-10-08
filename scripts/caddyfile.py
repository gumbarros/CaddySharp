#!/usr/bin/env python3
import argparse
from pathlib import Path
p=argparse.ArgumentParser()
p.add_argument('--mode',choices=['integrated','proxy','buffered-proxy'],default='integrated')
p.add_argument('--port',type=int,default=8080)
p.add_argument('--admin-port',type=int,default=2019)
a=p.parse_args()
root=Path(__file__).resolve().parents[1]
bridge=root/'src/CaddySharp/bin/Release/net10.0/linux-x64'
sample=root/'src/CaddySharp.Sample/bin/Release/net10.0'
if a.mode in ('proxy','buffered-proxy'):
 buffers = ' {\n  request_buffers 1MB\n  response_buffers 4MB\n }' if a.mode=='buffered-proxy' else ''
 print(f'''{{\n admin localhost:{a.admin_port}\n auto_https off\n}}\nhttp://127.0.0.1:{a.port} {{\n request_body {{\n  max_size 1MB\n }}\n reverse_proxy 127.0.0.1:8081{buffers}\n}}''')
else:
 print(f'''{{
 admin localhost:{a.admin_port}
 auto_https off
 aspnetcore {{
  app sample {{
   assembly {sample/'CaddySharp.Sample.dll'}
   runtime_config {bridge/'CaddySharp.runtimeconfig.json'}
   native_library {bridge/'CaddySharpNE.so'}
   content_root {root/'src/CaddySharp.Sample'}
   environment Production
   env SampleSettings__Value example
   max_request_body 1MB
   shutdown_timeout 30s
  }}
 }}
}}
http://127.0.0.1:{a.port} {{\n aspnetcore sample\n}}''')
