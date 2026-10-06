package caddysharp

import (
	"expvar"
	"runtime"
)

func init() {
	// Read only, available through Caddy's existing protected admin endpoint.
	expvar.Publish("caddysharp_cgo_calls", expvar.Func(func() any { return runtime.NumCgoCall() }))
}
