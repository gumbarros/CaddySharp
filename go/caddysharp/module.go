package caddysharp

import (
	"encoding/json"
	"fmt"
	"io"
	"log"
	"net/http"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"sync"
	"sync/atomic"
	"time"

	"github.com/caddyserver/caddy/v2"
	"github.com/caddyserver/caddy/v2/caddyconfig"
	"github.com/caddyserver/caddy/v2/caddyconfig/caddyfile"
	"github.com/caddyserver/caddy/v2/caddyconfig/httpcaddyfile"
	"github.com/caddyserver/caddy/v2/modules/caddyhttp"
)

type AppConfig struct {
	Assembly        string            `json:"assembly"`
	RuntimeConfig   string            `json:"runtime_config"`
	NativeLibrary   string            `json:"native_library"`
	ContentRoot     string            `json:"content_root"`
	Environment     string            `json:"environment"`
	Env             map[string]string `json:"env,omitempty"`
	MaxRequestBody  int64             `json:"max_request_body"`
	MaxResponseBody int64             `json:"max_response_body"`
	ShutdownTimeout string            `json:"shutdown_timeout"`
}
type App struct {
	Apps      map[string]AppConfig `json:"apps"`
	signature string
	active    sync.WaitGroup
	pending   atomic.Int64
	ready     chan struct{}
	slots     chan struct{}
	startErr  error
}
type Handler struct {
	Name string `json:"name"`
	app  *App
}

var global struct {
	sync.Mutex
	signature string
	started   bool
	refs      int
}

func init() {
	caddy.RegisterModule(App{})
	caddy.RegisterModule(Handler{})
	httpcaddyfile.RegisterGlobalOption("aspnetcore", parseGlobal)
	httpcaddyfile.RegisterHandlerDirective("aspnetcore", parseHandler)
	httpcaddyfile.RegisterDirectiveOrder("aspnetcore", httpcaddyfile.Before, "respond")
}
func (App) CaddyModule() caddy.ModuleInfo {
	return caddy.ModuleInfo{ID: "aspnetcore", New: func() caddy.Module { return new(App) }}
}
func (Handler) CaddyModule() caddy.ModuleInfo {
	return caddy.ModuleInfo{ID: "http.handlers.aspnetcore", New: func() caddy.Module { return new(Handler) }}
}
func parseGlobal(d *caddyfile.Dispenser, existing any) (any, error) {
	app := &App{Apps: map[string]AppConfig{}}
	if old, ok := existing.(httpcaddyfile.App); ok {
		if err := json.Unmarshal(old.Value, app); err != nil {
			return nil, err
		}
	}
	for d.Next() {
		if len(d.RemainingArgs()) != 0 {
			return nil, d.ArgErr()
		}
		for d.NextBlock(0) {
			if d.Val() != "app" {
				return nil, d.Errf("expected app block")
			}
			var name string
			if !d.Args(&name) {
				return nil, d.ArgErr()
			}
			if _, exists := app.Apps[name]; exists {
				return nil, d.Errf("duplicate app %s", name)
			}
			cfg := AppConfig{Environment: "Production", MaxRequestBody: 1 << 20, MaxResponseBody: 4 << 20, ShutdownTimeout: "30s", Env: map[string]string{}}
			for d.NextBlock(1) {
				key := d.Val()
				args := d.RemainingArgs()
				switch key {
				case "assembly", "runtime_config", "native_library", "content_root", "environment", "max_request_body", "max_response_body", "shutdown_timeout":
					if len(args) != 1 {
						return nil, d.ArgErr()
					}
					switch key {
					case "assembly":
						cfg.Assembly = args[0]
					case "runtime_config":
						cfg.RuntimeConfig = args[0]
					case "native_library":
						cfg.NativeLibrary = args[0]
					case "content_root":
						cfg.ContentRoot = args[0]
					case "environment":
						cfg.Environment = args[0]
					case "shutdown_timeout":
						cfg.ShutdownTimeout = args[0]
					case "max_request_body":
						n, e := parseSize(args[0])
						if e != nil {
							return nil, e
						}
						cfg.MaxRequestBody = n
					case "max_response_body":
						n, e := parseSize(args[0])
						if e != nil {
							return nil, e
						}
						cfg.MaxResponseBody = n
					}
				case "env":
					if len(args) != 2 {
						return nil, d.ArgErr()
					}
					cfg.Env[args[0]] = args[1]
				default:
					return nil, d.Errf("unknown aspnetcore option %s", key)
				}
			}
			app.Apps[name] = cfg
		}
	}
	return httpcaddyfile.App{Name: "aspnetcore", Value: caddyconfig.JSON(app, nil)}, nil
}
func parseSize(s string) (int64, error) {
	upper := strings.ToUpper(s)
	mult := int64(1)
	for _, u := range []struct {
		suffix string
		m      int64
	}{{"GB", 1 << 30}, {"MB", 1 << 20}, {"KB", 1 << 10}, {"B", 1}} {
		if strings.HasSuffix(upper, u.suffix) {
			upper = strings.TrimSuffix(upper, u.suffix)
			mult = u.m
			break
		}
	}
	n, e := strconv.ParseInt(upper, 10, 64)
	if e != nil || n <= 0 {
		return 0, fmt.Errorf("invalid body limit %q", s)
	}
	return n * mult, nil
}
func parseHandler(h httpcaddyfile.Helper) (caddyhttp.MiddlewareHandler, error) {
	m := new(Handler)
	if err := m.UnmarshalCaddyfile(h.Dispenser); err != nil {
		return nil, err
	}
	return m, nil
}
func (h *Handler) UnmarshalCaddyfile(d *caddyfile.Dispenser) error {
	d.Next()
	if !d.Args(&h.Name) {
		return d.ArgErr()
	}
	return nil
}
func (h *Handler) Provision(ctx caddy.Context) error {
	v, e := ctx.App("aspnetcore")
	if e != nil {
		return e
	}
	h.app = v.(*App)
	if _, ok := h.app.Apps[h.Name]; !ok {
		return fmt.Errorf("unknown aspnetcore app %q", h.Name)
	}
	return nil
}
func (a *App) Provision(ctx caddy.Context) error {
	a.ready = make(chan struct{})
	// Bound simultaneous ABI waits. Each request otherwise consumes a native
	// thread while CoreCLR processes it, and excessive cgo waiters can exhaust
	// the mixed runtime's scheduler under sustained load.
	a.slots = make(chan struct{}, 32)
	if len(a.Apps) != 1 {
		return fmt.Errorf("exactly one aspnetcore app is supported")
	}
	for _, v := range a.Apps {
		for key, path := range map[string]string{"assembly": v.Assembly, "runtime_config": v.RuntimeConfig, "native_library": v.NativeLibrary, "content_root": v.ContentRoot} {
			if path == "" {
				return fmt.Errorf("%s required", key)
			}
			if !filepath.IsAbs(path) {
				return fmt.Errorf("%s must be absolute", key)
			}
			if _, e := os.Stat(path); e != nil {
				return fmt.Errorf("%s: %w", key, e)
			}
		}
		if v.MaxRequestBody <= 0 || v.MaxResponseBody <= 0 {
			return fmt.Errorf("body limits must be positive")
		}
		if _, e := time.ParseDuration(v.ShutdownTimeout); e != nil {
			return e
		}
		expected := filepath.Join(filepath.Dir(v.NativeLibrary), "CaddySharp.runtimeconfig.json")
		expectedReal, e := filepath.EvalSymlinks(expected)
		if e != nil {
			return e
		}
		configuredReal, e := filepath.EvalSymlinks(v.RuntimeConfig)
		if e != nil {
			return e
		}
		if expectedReal != configuredReal {
			return fmt.Errorf("runtime_config must point to the DNNE adjacent runtimeconfig %s", expected)
		}
	}
	bytes, _ := json.Marshal(a.Apps)
	a.signature = string(bytes)
	return nil
}
func (a *App) Start() (err error) {
	defer func() { a.startErr = err; close(a.ready) }()
	global.Lock()
	defer global.Unlock()
	if global.started {
		if global.signature != a.signature {
			return fmt.Errorf("ASP.NET runtime/app changed; restart Caddy")
		}
		global.refs++
		return nil
	}
	for _, v := range a.Apps {
		if e := loadNative(v.NativeLibrary); e != nil {
			return e
		}
		env := make([]header, 0, len(v.Env))
		for k, value := range v.Env {
			env = append(env, header{k, value})
		}
		if e := initNative(v.Assembly, v.ContentRoot, v.Environment, env); e != nil {
			return e
		}
	}
	global.signature = a.signature
	global.started = true
	global.refs = 1
	return nil
}
func (a *App) Stop() error {
	for _, v := range a.Apps {
		duration, _ := time.ParseDuration(v.ShutdownTimeout)
		done := make(chan struct{})
		go func() { a.active.Wait(); close(done) }()
		select {
		case <-done:
		case <-time.After(duration):
			return fmt.Errorf("aspnetcore shutdown timed out")
		}
	}
	global.Lock()
	defer global.Unlock()
	global.refs--
	if global.refs == 0 {
		log.Printf("caddysharp pending_handles=%d", a.pending.Load())
		return shutdownNative()
	}
	return nil
}
func (h *Handler) ServeHTTP(w http.ResponseWriter, r *http.Request, next caddyhttp.Handler) error {
	select {
	case <-h.app.ready:
		if h.app.startErr != nil {
			return h.app.startErr
		}
	case <-r.Context().Done():
		return r.Context().Err()
	}
	cfg := h.app.Apps[h.Name]
	body, e := io.ReadAll(io.LimitReader(r.Body, cfg.MaxRequestBody+1))
	if e != nil {
		return e
	}
	if int64(len(body)) > cfg.MaxRequestBody {
		http.Error(w, "request body too large", http.StatusRequestEntityTooLarge)
		return nil
	}
	hs := []header{}
	for k, values := range r.Header {
		for _, v := range values {
			hs = append(hs, header{k, v})
		}
	}
	scheme := "http"
	if r.TLS != nil {
		scheme = "https"
	}
	path := r.URL.Path
	if path == "" {
		path = "/"
	}
	query := ""
	if r.URL.RawQuery != "" {
		query = "?" + r.URL.RawQuery
	}
	select {
	case h.app.slots <- struct{}{}:
	case <-r.Context().Done():
		return r.Context().Err()
	}
	defer func() { <-h.app.slots }()
	handle, e := requestNative(r.Method, scheme, r.Host, path, r.RequestURI, query, r.RemoteAddr, body, hs, cfg.MaxResponseBody)
	if e != nil {
		http.Error(w, "internal server error", 500)
		return e
	}
	h.app.active.Add(1)
	h.app.pending.Add(1)
	defer func() {
		if err := completeNative(handle); err != nil {
			log.Printf("caddysharp completion: %v", err)
		}
		freeNative(handle)
		h.app.pending.Add(-1)
		h.app.active.Done()
	}()
	done := make(chan error, 1)
	go func() { done <- waitNative(handle) }()
	select {
	case e = <-done:
	case <-r.Context().Done():
		cancelNative(handle)
		e = <-done
	}
	if e != nil {
		http.Error(w, "internal server error", 500)
		return e
	}
	status, output, headers, e := responseNative(handle)
	if e != nil {
		http.Error(w, "internal server error", 500)
		return e
	}
	for _, v := range headers {
		w.Header().Add(v.name, v.value)
	}
	if r.Method == "HEAD" && w.Header().Get("Content-Length") == "" {
		w.Header().Set("Content-Length", strconv.Itoa(len(output)))
	}
	w.WriteHeader(status)
	if r.Method != "HEAD" && status != 204 && status != 304 {
		_, e = w.Write(output)
	}
	return e
}

var _ caddy.App = (*App)(nil)
var _ caddy.Provisioner = (*App)(nil)
var _ caddyhttp.MiddlewareHandler = (*Handler)(nil)
var _ caddyfile.Unmarshaler = (*Handler)(nil)
