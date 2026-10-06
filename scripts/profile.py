#!/usr/bin/env python3
"""Separate instrumented runs: never mix profiler samples into throughput claims."""
import argparse, concurrent.futures, json, os, shutil, subprocess, time, urllib.request
from pathlib import Path
from measurement import ROOT, Servers, stats, go_stats


def fetch(path, target):
    with urllib.request.urlopen('http://localhost:12019/debug/pprof/'+path,timeout=45) as response:
        target.write_bytes(response.read())


def main():
    parser=argparse.ArgumentParser()
    parser.add_argument('--output',required=True)
    parser.add_argument('--endpoints',default='/text,/json,/echo')
    parser.add_argument('--concurrency',type=int,default=16)
    parser.add_argument('--profiler',choices=['go','managed','alloc'],default='go')
    parser.add_argument('--counters',default='/tmp/caddysharp-diagnostics/dotnet-counters')
    parser.add_argument('--trace',default='/tmp/caddysharp-diagnostics/dotnet-trace')
    args=parser.parse_args()
    directory=Path(args.output).resolve();directory.mkdir(parents=True,exist_ok=True)
    go=shutil.which('go') or str(Path.home()/'.local/lib/go/bin/go')
    for endpoint in args.endpoints.split(','):
        target=directory/endpoint.strip('/')
        with Servers('integrated',target,args.counters) as server:
            cmd=[str(ROOT/'bin/caddysharp-load'),'--url',server.base+endpoint,'--concurrency',str(args.concurrency)]
            subprocess.run(cmd+['--duration','3s'],check=True,stdout=subprocess.DEVNULL)
            before=go_stats()
            process_before=stats(server.managed_pid)
            fetch('allocs',target/'allocs-before.pb.gz')
            trace_log=(target/'trace.log').open('w')
            trace=subprocess.Popen([args.trace,'collect','-p',str(server.managed_pid),'--profile','dotnet-sampled-thread-time,gc-verbose',
                                   '--duration','00:00:12','-o',str(target/'managed.nettrace')],stdout=trace_log,stderr=trace_log) if args.profiler=='managed' else None
            load=subprocess.Popen(cmd+['--duration','15s'],stdout=subprocess.PIPE,text=True)
            with concurrent.futures.ThreadPoolExecutor() as pool:
                cpu=pool.submit(fetch,'profile?seconds=10',target/'cpu.pb.gz') if args.profiler=='go' else None
                time.sleep(4)
                fetch('goroutine?debug=2',target/'goroutines.txt')
                fetch('threadcreate?debug=1',target/'native-threads.txt')
                waits={}
                for task in (Path('/proc')/str(server.managed_pid)/'task').iterdir():
                    try: waits[task.name]=(task/'wchan').read_text().strip()
                    except FileNotFoundError: pass
                (target/'thread-waits.json').write_text(json.dumps(waits,indent=2)+'\n')
                samples=[]
                while load.poll() is None:
                    samples.append(stats(server.managed_pid));time.sleep(.25)
                if cpu: cpu.result()
            measurement=json.loads(load.communicate()[0])
            after=go_stats()
            fetch('allocs',target/'allocs-after.pb.gz')
            if trace: trace.wait(timeout=30)
            trace_log.close()
            measurement.update(go_before=before,go_after=after,go_delta={k:after[k]-before[k] if before[k] is not None and after[k] is not None else None for k in before},
                               process_before=process_before,process_after=stats(server.managed_pid),samples=samples)
            (target/'measurement.json').write_text(json.dumps(measurement,indent=2)+'\n')
            for name,options in [('cpu',['-top','-nodecount=40',str(target/'cpu.pb.gz')]),
                                 ('alloc-space',['-top','-nodecount=40','-sample_index=alloc_space','-base',str(target/'allocs-before.pb.gz'),str(target/'allocs-after.pb.gz')])]:
                if name=='cpu' and args.profiler!='go': continue
                with (target/(name+'.txt')).open('w') as output:
                    subprocess.run([go,'tool','pprof',*options],stdout=output,stderr=subprocess.STDOUT,check=True)
            if trace:
             with (target/'managed-top.txt').open('w') as output:
                subprocess.run([args.trace,'report',str(target/'managed.nettrace'),'topN','-n','40'],stdout=output,stderr=subprocess.STDOUT)
            print(endpoint,round(measurement['rps']),measurement['go_delta'],flush=True)

if __name__=='__main__':main()
