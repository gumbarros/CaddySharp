#!/usr/bin/env python3
"""Sustained, content-validated load with bounded phases and retained failure logs."""
import argparse,json,subprocess,time,urllib.request
from pathlib import Path
from measurement import ROOT, Servers, stats, go_stats


def main():
    parser=argparse.ArgumentParser()
    parser.add_argument('--seconds',type=int,default=180)
    parser.add_argument('--concurrency',type=int,default=16)
    parser.add_argument('--endpoints',default='/text,/json,/echo')
    parser.add_argument('--output',default='soak-results.json')
    args=parser.parse_args()
    if args.seconds<=0 or args.concurrency<=0:parser.error('seconds and concurrency must be positive')
    output=Path(args.output).resolve();output.parent.mkdir(parents=True,exist_ok=True)
    raw=output.parent/(output.stem+'-raw')
    server=Servers('integrated',raw)
    result={'seconds':args.seconds,'concurrency':args.concurrency,'rows':[]}
    failure=None
    try:
        with server:
            result['pid']=server.managed_pid
            remaining=args.seconds;index=0;endpoints=args.endpoints.split(',')
            while remaining>0:
                duration=min(10,remaining);endpoint=endpoints[index%len(endpoints)]
                before=stats(server.managed_pid);go_before=go_stats()
                load=subprocess.Popen([str(ROOT/'bin/caddysharp-load'),'--url',server.base+endpoint,
                                      '--concurrency',str(args.concurrency),'--duration',f'{duration}s','--procs','4'],stdout=subprocess.PIPE,text=True)
                samples=[]
                while load.poll() is None:
                    samples.append(stats(server.managed_pid));time.sleep(.25)
                data=json.loads(load.communicate()[0])
                after=stats(server.managed_pid)
                row={'phase':index+1,'endpoint':endpoint,'before':before,'after':after,'samples':samples,'load':data}
                result['rows'].append(row)
                if server.processes[0].poll() is not None:raise RuntimeError('server exited under sustained load')
                go_after=go_stats();row['go_delta']={k:go_after[k]-go_before[k] if go_before[k] is not None and go_after[k] is not None else None for k in go_before}
                output.write_text(json.dumps(result,indent=2)+'\n')
                print(index+1,endpoint,after,data['errors'],flush=True)
                if load.returncode or data['errors']:raise RuntimeError('load generator reported errors')
                index+=1;remaining-=duration
            urllib.request.urlopen(urllib.request.Request('http://localhost:12019/stop',data=b'',method='POST'),timeout=10).read()
    except Exception as exc:
        failure=repr(exc)
    result['exit_code']=server.processes[0].returncode if server.processes else None
    logs=(raw/'caddy.log').read_text() if (raw/'caddy.log').exists() else ''
    result['pending_log']=next((line for line in logs.splitlines() if 'pending_handles=' in line),None)
    result['failure']=failure
    result['passed']=failure is None and result['exit_code']==0 and result['pending_log'] is not None and 'pending_handles=0' in result['pending_log']
    output.write_text(json.dumps(result,indent=2)+'\n')
    if not result['passed']:raise SystemExit('soak failed; see '+str(output))

if __name__=='__main__':main()
