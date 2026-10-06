#!/usr/bin/env python3
"""Repeated, seeded mode rotation; raw per-process and runtime data retained."""
import argparse, csv, datetime, json, os, platform, random, shutil, subprocess, time
from pathlib import Path
from measurement import ROOT, Servers, stats, host_cpu, go_stats


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--duration', default='10s')
    parser.add_argument('--repeat', type=int, default=3)
    parser.add_argument('--concurrency', default='1,16,64')
    parser.add_argument('--modes', default='direct,proxy,buffered-proxy,integrated')
    parser.add_argument('--endpoints', default='/text,/json,/echo')
    parser.add_argument('--output', default='benchmark-results.json')
    parser.add_argument('--seed', type=int, default=20261005)
    parser.add_argument('--generator-procs', type=int, default=4)
    parser.add_argument('--generator-sweep', help='Comma-separated GOMAXPROCS capacity sensitivity sweep')
    parser.add_argument('--counters', default=shutil.which('dotnet-counters'))
    args = parser.parse_args()
    output = Path(args.output).resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    raw = output.parent / (output.stem + '-raw')
    rng = random.Random(args.seed)
    modes = args.modes.split(',')
    rng.shuffle(modes)
    results = []
    hardware = {'platform':platform.platform(), 'cpu':next(x.split(':',1)[1].strip() for x in Path('/proc/cpuinfo').read_text().splitlines() if x.startswith('model name')),
                'dotnet':subprocess.check_output(['dotnet','--version'],text=True).strip(), 'go':subprocess.check_output([shutil.which('go') or str(Path.home()/'.local/lib/go/bin/go'),'version'],text=True).strip(),
                'caddy':'2.11.7','dnne':'2.1.2','logical_cpus':os.cpu_count(),'affinity':sorted(os.sched_getaffinity(0)), 'clock_ticks_per_second':os.sysconf('SC_CLK_TCK')}
    report = {'hardware':hardware,'settings':vars(args),'mode_orders':[], 'results':results,
              'limitations':['Shared workstation and closed-loop localhost load; CPU saturation flags are heuristics. Use generator capacity sensitivity runs before claiming server ceilings.',
                             'Managed EventCounters use one-second intervals; boundary intervals are excluded from per-run summaries and raw CSV is retained.']}
    def save():
        output.write_text(json.dumps(report,indent=2)+'\n')
    for rep in range(args.repeat):
        order = modes[rep % len(modes):] + modes[:rep % len(modes)]
        report['mode_orders'].append(order)
        for mode in order:
            directory=raw/f'rep-{rep+1}-{mode}'
            block=[]
            with Servers(mode,directory,args.counters) as server:
                generator_settings=[int(x) for x in args.generator_sweep.split(',')] if args.generator_sweep else [args.generator_procs]
                scenarios=[(endpoint,int(c),procs) for endpoint in args.endpoints.split(',') for c in args.concurrency.split(',') for procs in generator_settings]
                rng.shuffle(scenarios)
                for endpoint,concurrency,procs in scenarios:
                    command=[str(ROOT/'bin/caddysharp-load'),'--url',server.base+endpoint,'--concurrency',str(concurrency),'--procs',str(procs)]
                    subprocess.run(command+['--duration','2s'],check=True,stdout=subprocess.DEVNULL)
                    before=[stats(p.pid) for p in server.processes]
                    go_before=go_stats() if mode!='direct' else None
                    host_before=host_cpu()
                    started=time.time()
                    load=subprocess.Popen(command+['--duration',args.duration],text=True,stdout=subprocess.PIPE)
                    samples=[]
                    while load.poll() is None:
                        samples.append({'time':time.time(),'servers':[stats(p.pid) for p in server.processes], 'generator':stats(load.pid)})
                        time.sleep(.2)
                    stdout=load.communicate()[0]
                    if load.returncode: raise RuntimeError('load generator failed')
                    finished=time.time()
                    after=[stats(p.pid) for p in server.processes]
                    host_after=host_cpu()
                    go_after=go_stats() if mode!='direct' and all(p.poll() is None for p in server.processes) else None
                    measurement=json.loads(stdout)
                    measurement['server_exit_codes']=[p.poll() for p in server.processes]
                    host_total=host_after[0]-host_before[0]
                    host_util=1-(host_after[1]-host_before[1])/host_total if host_total else 0
                    cpu_ticks=sum(a['cpu_ticks']-b['cpu_ticks'] for a,b in zip(after,before))
                    measurement.update(mode=mode,endpoint=endpoint,repeat=rep+1,started_unix=started,finished_unix=finished,
                        cpu_ticks=cpu_ticks,cpu_s=cpu_ticks/os.sysconf('SC_CLK_TCK'),
                        rss_kb=sum(s['rss_kb'] for s in after),
                        peak_rss_kb=max(sum(s['rss_kb'] for s in sample['servers']) for sample in samples),
                        threads=sum(s['threads'] for s in after),peak_threads=max(sum(s['threads'] for s in sample['servers']) for sample in samples),
                        host_cpu_utilization=host_util,host_saturation_suspected=host_util>.85,
                        process_before=before,process_after=after,samples=samples,managed_counters_csv=str(directory/'managed.csv') if args.counters else None)
                    if go_before and go_after:
                        measurement['go_delta']={key:go_after[key]-go_before[key] if go_after[key] is not None and go_before[key] is not None else None for key in go_before}
                    results.append(measurement);block.append(measurement)
                    print(mode,endpoint,concurrency,rep+1,round(measurement['rps']),measurement['p95_us'],'us',measurement['errors'],flush=True)
                    save()
                    if any(code is not None for code in measurement['server_exit_codes']):
                        raise RuntimeError('Server exited during measurement; raw sample saved')
            if args.counters:
                attach_counters(directory/'managed.csv',block)
                save()
    save()


def attach_counters(path, measurements):
    rows=list(csv.DictReader(path.open()))
    for measurement in measurements:
        counters={}
        for row in rows:
            stamp=row.get('Timestamp','')
            try:
                timestamp=datetime.datetime.strptime(stamp,'%m/%d/%Y %H:%M:%S').timestamp()
            except ValueError:
                try: timestamp=datetime.datetime.fromisoformat(stamp).timestamp()
                except ValueError: continue
            if measurement['started_unix']+1 <= timestamp <= measurement['finished_unix']-1:
                key=row.get('Counter Name', row.get('Name','unknown'))
                try:value=float(row['Mean/Increment'])
                except (KeyError,ValueError):continue
                counters.setdefault(key,[]).append(value)
        measurement['managed_counters']={key:{'samples':len(values),'sum':sum(values),'mean':sum(values)/len(values),'max':max(values)} for key,values in counters.items()}
        measurement['managed_counter_parse_ok']=bool(counters)

if __name__=='__main__':main()
