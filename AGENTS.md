# Agent guidance

## Project

CaddySharp is an in-process bridge from Caddy (Go) to an ASP.NET Core app (.NET 10). Caddy receives HTTP requests and passes them through a Go/C ABI to the managed server; there is no loopback Kestrel proxy in this mode. Read `README.md` for usage and `docs/decisions.md` before changing the bridge or lifecycle behavior.

## Layout

- `go/caddysharp/`: Caddy module, native bridge, and diagnostics.
- `src/CaddySharp/`: DNNE exports and ASP.NET Core `IServer` implementation.
- `src/CaddySharp.Sample/`: sample Minimal API used by integration tests and benchmarks.
- `cmd/`: Caddy launcher and helper programs.
- `scripts/`: build, Caddyfile generation, benchmark, profiling, and soak tools.
- `tests/integration.py`: end-to-end HTTP checks against a built local binary.

## Build and checks

The targeted environment is Linux x64 with .NET SDK 10, Go 1.26+, `clang`, `g++`, Python 3, and the .NET 10 ASP.NET Core runtime. Build from the repository root with `./scripts/build.sh`; set `GO_BIN` if Go is not on `PATH`. This builds the managed bridge, sample, package, and Go binaries into `bin/`.

After a completed build, run `go test ./go/caddysharp` for Go unit tests and `python3 tests/integration.py` for HTTP integration checks. For sustained-load changes, run `python3 scripts/soak.py --seconds 60`. Benchmark with `python3 scripts/benchmark.py --duration 10s --repeat 3 --concurrency 1,16,64` only when measuring performance; report the environment and results rather than assuming a universal speedup.

## Implementation constraints

- Keep the Go/C/managed ABI, ownership, and completion/cancellation lifetimes consistent on both sides. Go owns C-allocated request memory until an export returns; managed code copies request data before retaining it. Release request handles only after completion, including cancellation.
- `builder.Host.UseCaddyServer()` selects the Caddy-backed server when launched through the bridge and leaves direct app launches on Kestrel. Preserve both paths when changing host registration.

