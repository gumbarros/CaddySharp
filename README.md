# CaddySharp

Run an ASP.NET Core app inside a Caddy process. Caddy accepts the HTTP request and passes it to ASP.NET Core through a Go/C bridge, without a separate Kestrel server or a loopback proxy connection.

**Status:** experimental MVP for Linux x64, .NET 10, and one ASP.NET Core app per process. It is not ready as a general Kestrel replacement.

## Why

Caddy already handles the public HTTP connection. CaddySharp tests whether it can also host the ASP.NET Core pipeline in the same process, removing the local Caddy → Kestrel hop.
The sample uses a normal Minimal API, dependency injection, and configuration. One host extension selects CaddySharp's server when Caddy launches the app.

The current implementation is useful for experimenting with this architecture and measuring its costs. It buffers request and response bodies, so it is a poor fit for streaming workloads.

## Get started

### Docker

```sh
docker build -t caddysharp .
docker run -d --rm --name caddysharp-demo -p 8080:8080 caddysharp
curl http://127.0.0.1:8080/health
docker stop caddysharp-demo
```

### Build locally

Requires Linux x64, .NET SDK 10, Go 1.26+, `clang`, `g++`, and Python 3. The .NET 10 ASP.NET Core runtime must be installed to run the result.

```sh
./scripts/build.sh
python3 scripts/caddyfile.py > Caddyfile.generated
bin/caddysharp run --config Caddyfile.generated
```

In another terminal:

```sh
curl http://127.0.0.1:8080/health
curl http://127.0.0.1:8080/text
```

The generated configuration uses local HTTP; it needs no domain or certificate. If Go is not on `PATH`, set `GO_BIN` to its executable before running the build script.

To try your own `WebApplication` app, build it for .NET 10 and add the local integration package produced by `./scripts/build.sh`:

```sh
dotnet add path/to/YourApp.csproj package CaddySharp --source ./bin/packages
```

Then add this after `WebApplication.CreateBuilder(args)`:

```csharp
using CaddySharp;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseCaddyServer();
```

Then point the `assembly` and `content_root` settings in the generated Caddyfile at your app. The `native_library` and `runtime_config` settings point at the bridge build; `runtime_config` must be the file next to the native library. See [the sample app](src/CaddySharp.Sample/Program.cs) and [the Caddyfile generator](scripts/caddyfile.py). The bridge calls your app's normal `Main` method. `UseCaddyServer()` has no effect when the app runs directly, so direct runs still use Kestrel.

## Benchmark

**In the latest local run, CaddySharp beat Caddy → Kestrel for all three sample endpoints at 16 clients.** These are median values from three 2-second measurements after a 2-second warm-up, on an Intel Core 7 150U Linux workstation:

| Endpoint | Caddy → Kestrel | CaddySharp | p95 latency: proxy → CaddySharp |
| --- | ---: | ---: | ---: |
| Text | 13,177 req/s | 27,258 req/s | 2,780 → 1,260 µs |
| JSON | 13,014 req/s | 27,480 req/s | 2,895 → 1,181 µs |
| 1 KiB echo | 13,743 req/s | 26,035 req/s | 2,819 → 1,298 µs |

All 54 measured samples across 1, 16, and 64 clients had zero errors. This is a short, closed-loop localhost test on a shared workstation. It does not establish production throughput or a general performance advantage. The latest comparison did not include direct Kestrel. Treat these figures as preliminary local measurements.

To run the comparison yourself after building:

```sh
python3 scripts/benchmark.py --duration 10s --repeat 3 --concurrency 1,16,64
```

The script compares direct Kestrel, Caddy → Kestrel, a buffered proxy, and CaddySharp by default. It writes per-run throughput, latency, errors, CPU, and memory data to `benchmark-results.json`.

## What's missing

- **Streaming:** request and response bodies are fully buffered. There is no SSE or unbounded streaming support. The sample configuration caps requests at 1 MiB and responses at 4 MiB; oversized requests get 413 and oversized responses get 500.
- **Upgraded and long-lived protocols:** no WebSockets, SignalR, or gRPC support.
- **Multiple apps and runtime replacement:** exactly one app is supported per process. A config reload can reuse that app, but changing the app or runtime requires a process restart. CoreCLR is not unloaded.
- **Broad platform and deployment support:** only Linux x64, .NET 10, and framework-dependent deployment have been targeted.
- **Production validation:** sustained mixed-runtime load needed scheduler safeguards (`GOMAXPROCS=1`, Go asynchronous preemption disabled, and at most 32 simultaneous ABI waits). Longer stability tests, profiling, and wider workload benchmarks are still needed.

Run `python3 tests/integration.py` for HTTP integration checks and `python3 scripts/soak.py --seconds 60` for a short sustained-load check. Both require a completed local build. For implementation decisions and ABI details, see [docs/decisions.md](docs/decisions.md).
