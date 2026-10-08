package main

import (
	"os"
	"runtime"
	"strings"
	"syscall"

	caddycmd "github.com/caddyserver/caddy/v2/cmd"
	_ "github.com/caddyserver/caddy/v2/modules/standard"
	_ "github.com/gumbarros/caddysharp/go/caddysharp"
)

func main() {
	// CoreCLR and Go both install signal handlers. Disable Go's signal-based
	// asynchronous preemption before the Go runtime initializes.
	settings := os.Getenv("GODEBUG")
	parts := strings.Split(settings, ",")
	preemptionDisabled := false
	for _, part := range parts {
		if strings.HasPrefix(part, "asyncpreemptoff=") {
			preemptionDisabled = part == "asyncpreemptoff=1"
		}
	}
	if !preemptionDisabled {
		if settings != "" {
			settings += ","
		}
		settings += "asyncpreemptoff=1"
		executable, err := os.Executable()
		if err != nil {
			panic(err)
		}
		environment := make([]string, 0, len(os.Environ())+1)
		for _, entry := range os.Environ() {
			if !strings.HasPrefix(entry, "GODEBUG=") {
				environment = append(environment, entry)
			}
		}
		environment = append(environment, "GODEBUG="+settings)
		environment = append(environment, "GOMAXPROCS=1")
		if err := syscall.Exec(executable, os.Args, environment); err != nil {
			panic(err)
		}
	}
	// The re-exec path sets this in the environment, but callers may already
	// provide asyncpreemptoff=1 and bypass that path.
	runtime.GOMAXPROCS(1)
	caddycmd.Main()
}
