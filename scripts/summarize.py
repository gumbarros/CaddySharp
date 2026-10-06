#!/usr/bin/env python3
"""Summarize independent scenario repetitions without pooling percentiles."""
import argparse,json,statistics
from pathlib import Path

p=argparse.ArgumentParser();p.add_argument('input');p.add_argument('--output');a=p.parse_args()
data=json.loads(Path(a.input).read_text())
results=data['results'];modes=['direct','proxy','buffered-proxy','integrated']
lines=['| Endpoint | Clients | Direct req/s | Proxy req/s | Buffered proxy req/s | Integrated req/s | Proxy / integrated p95 µs | Integrated beats proxy in every repeat? |',
       '|---|---:|---:|---:|---:|---:|---:|---|']
for endpoint in ['/text','/json','/echo']:
 for concurrency in sorted(set(r['concurrency'] for r in results)):
  rows={m:sorted([r for r in results if r['mode']==m and r['endpoint']==endpoint and r['concurrency']==concurrency],key=lambda x:x['repeat']) for m in modes}
  if not any(rows.values()):continue
  rates=[f"{statistics.median(r['rps'] for r in rows[m]):,.0f}" if rows[m] else '—' for m in modes]
  p95=[f"{statistics.median(r['p95_us'] for r in rows[m]):,.0f}" if rows[m] else '—' for m in ['proxy','integrated']]
  proxy={r['repeat']:r for r in rows['proxy']}
  wins=[r['rps']>proxy[r['repeat']]['rps'] and r['p95_us']<=proxy[r['repeat']]['p95_us'] and r['errors']==0 and proxy[r['repeat']]['errors']==0
        for r in rows['integrated'] if r['repeat'] in proxy]
  verdict='Yes' if len(wins)>=3 and all(wins) else 'No' if wins else 'Not compared'
  lines.append('| '+ ' | '.join([endpoint,str(concurrency),*rates,' / '.join(p95),verdict])+' |')
lines += ['',f"Errors: {sum(r['errors'] for r in results)} across {len(results)} measured samples.",
          f"Generator CPU flags: {sum(bool(r.get('generator_cpu_saturation_suspected')) for r in results)}. Host CPU flags: {sum(bool(r.get('host_saturation_suspected')) for r in results)}."]
text='\n'.join(lines)+'\n'
if a.output:Path(a.output).write_text(text)
print(text)
