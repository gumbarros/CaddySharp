"""Linux resource and runtime collectors used by benchmark/profile experiments."""
import json, os, signal, subprocess, time, urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

def wait(url):
    for _ in range(100):
        try:
            with urllib.request.urlopen(url, timeout=.5) as response:
                response.read()
            return
        except Exception:
            time.sleep(.1)
    raise RuntimeError('Server did not start: ' + url)

def stop(processes):
    for process in reversed(processes):
        if process.poll() is None:
            process.terminate()
        try:
            process.wait(timeout=15)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait()

def stats(pid):
    try:
        # comm can contain spaces: fields after the final ')' start at field 3.
        fields = (Path('/proc') / str(pid) / 'stat').read_text().rsplit(')', 1)[1].split()
        status = (Path('/proc') / str(pid) / 'status').read_text().splitlines()
        values = {key: int(next(line.split()[1] for line in status if line.startswith(key + ':')))
                  for key in ('VmRSS', 'Threads')}
        return dict(cpu_ticks=int(fields[11]) + int(fields[12]), rss_kb=values['VmRSS'], threads=values['Threads'])
    except (FileNotFoundError, StopIteration, ProcessLookupError):
        return dict(cpu_ticks=0, rss_kb=0, threads=0)

def host_cpu():
    fields = list(map(int, Path('/proc/stat').read_text().splitlines()[0].split()[1:9]))
    return sum(fields), fields[3] + fields[4]

def go_stats():
    with urllib.request.urlopen('http://localhost:12019/debug/vars', timeout=3) as response:
        data = json.load(response)
    mem = data['memstats']
    return {**{key: mem[key] for key in ('TotalAlloc', 'Mallocs', 'NumGC', 'PauseTotalNs', 'HeapAlloc')},
            'cgo_calls': data.get('caddysharp_cgo_calls')}

class Servers:
    def __init__(self, mode, directory, counters=None):
        self.mode, self.directory, self.counters_tool = mode, Path(directory), counters
        self.processes, self.logs, self.collectors = [], [], []
        self.directory.mkdir(parents=True, exist_ok=True)
        self.base = 'http://127.0.0.1:' + ('8081' if mode == 'direct' else '18080')

    def launch(self, command, name):
        log = (self.directory / (name + '.log')).open('w')
        self.logs.append(log)
        process = subprocess.Popen(command, cwd=ROOT, stdout=log, stderr=log)
        self.processes.append(process)
        return process

    def __enter__(self):
        try:
            if self.mode != 'integrated':
                managed = self.launch(['dotnet', str(ROOT / 'src/CaddySharp.Sample/bin/Release/net10.0/CaddySharp.Sample.dll'),
                                       '--urls', 'http://127.0.0.1:8081'], 'kestrel')
                wait('http://127.0.0.1:8081/health')
            if self.mode != 'direct':
                config = subprocess.check_output(['python3', str(ROOT / 'scripts/caddyfile.py'), '--mode', self.mode,
                                                  '--port', '18080', '--admin-port', '12019'], text=True)
                path = self.directory / 'Caddyfile'
                path.write_text(config)
                caddy = self.launch([str(ROOT / 'bin/caddysharp'), 'run', '--config', str(path)], 'caddy')
                wait(self.base + '/health')
                if self.mode == 'integrated':
                    managed = caddy
            self.managed_pid = managed.pid
            if self.counters_tool:
                log = (self.directory / 'counters.log').open('w')
                self.logs.append(log)
                collector = subprocess.Popen([self.counters_tool, 'collect', '-p', str(managed.pid), '--counters',
                    'EventCounters\\System.Runtime', '--format', 'csv', '-o', str(self.directory / 'managed.csv')],
                    stdout=log, stderr=log, stdin=subprocess.DEVNULL)
                self.collectors.append(collector)
                time.sleep(2)
                if collector.poll() is not None:
                    raise RuntimeError('dotnet-counters failed: ' + (self.directory / 'counters.log').read_text())
            return self
        except BaseException:
            self.__exit__(None, None, None)
            raise

    def __exit__(self, *args):
        for collector in self.collectors:
            if collector.poll() is None:
                collector.send_signal(signal.SIGINT)
            try:
                collector.wait(timeout=10)
            except subprocess.TimeoutExpired:
                collector.kill()
                collector.wait()
        stop(self.processes)
        for log in self.logs:
            log.close()
