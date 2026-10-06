package main

import (
	"bytes"
	"encoding/json"
	"flag"
	"fmt"
	"io"
	"net/http"
	"os"
	"runtime"
	"sort"
	"strings"
	"sync"
	"syscall"
	"time"
)

type workerResult struct {
	latencies []int64
	errors    int64
}

func cpuSeconds() float64 {
	var usage syscall.Rusage
	_ = syscall.Getrusage(syscall.RUSAGE_SELF, &usage)
	return float64(usage.Utime.Sec+usage.Stime.Sec) + float64(usage.Utime.Usec+usage.Stime.Usec)/1e6
}
func main() {
	url := flag.String("url", "http://127.0.0.1:8080/text", "Target URL")
	duration := flag.Duration("duration", 10*time.Second, "Measured duration")
	workers := flag.Int("concurrency", 16, "Concurrent closed-loop clients")
	size := flag.Int("echo-bytes", 1024, "Echo request bytes")
	procs := flag.Int("procs", 0, "Generator GOMAXPROCS (0 keeps runtime default)")
	flag.Parse()
	if *workers < 1 || *duration <= 0 || *size < 0 {
		panic("invalid load settings")
	}
	if *procs > 0 {
		runtime.GOMAXPROCS(*procs)
	}
	transport := &http.Transport{MaxIdleConnsPerHost: *workers, MaxConnsPerHost: *workers}
	client := &http.Client{Transport: transport, Timeout: 10 * time.Second}
	payload := bytes.Repeat([]byte("x"), *size)
	echo := strings.HasSuffix(*url, "/echo")
	expected := []byte(nil)
	if echo {
		expected = payload
	} else if strings.HasSuffix(*url, "/text") {
		expected = []byte("hello caddysharp")
	} else if strings.HasSuffix(*url, "/json") {
		expected = []byte(`{"name":"CaddySharp","ok":true}`)
	}
	results := make([]workerResult, *workers)
	var before, after runtime.MemStats
	runtime.ReadMemStats(&before)
	cpuBefore := cpuSeconds()
	start := time.Now()
	stop := start.Add(*duration)
	var wg sync.WaitGroup
	for i := range results {
		wg.Add(1)
		go func(result *workerResult) {
			defer wg.Done()
			result.latencies = make([]int64, 0, 10000)
			// Each client owns its sample slice: no per-request cross-client lock.
			for time.Now().Before(stop) {
				var body io.Reader
				method := "GET"
				if echo {
					method = "POST"
					body = bytes.NewReader(payload)
				}
				t := time.Now()
				req, err := http.NewRequest(method, *url, body)
				if err != nil {
					panic(err)
				}
				resp, err := client.Do(req)
				if err == nil {
					var data []byte
					data, err = io.ReadAll(resp.Body)
					resp.Body.Close()
					if resp.StatusCode != 200 || expected != nil && !bytes.Equal(data, expected) {
						err = fmt.Errorf("unexpected response")
					}
				}
				if err != nil {
					result.errors++
				}
				result.latencies = append(result.latencies, time.Since(t).Microseconds())
			}
		}(&results[i])
	}
	wg.Wait()
	elapsed := time.Since(start).Seconds()
	cpu := cpuSeconds() - cpuBefore
	runtime.ReadMemStats(&after)
	var count int
	var errors int64
	for _, r := range results {
		count += len(r.latencies)
		errors += r.errors
	}
	latencies := make([]int64, 0, count)
	for _, r := range results {
		latencies = append(latencies, r.latencies...)
	}
	sort.Slice(latencies, func(i, j int) bool { return latencies[i] < latencies[j] })
	pct := func(p float64) int64 {
		if len(latencies) == 0 {
			return 0
		}
		return latencies[int(float64(len(latencies)-1)*p)]
	}
	json.NewEncoder(os.Stdout).Encode(map[string]any{
		"url": *url, "concurrency": *workers, "duration_s": elapsed, "requests": count, "errors": errors,
		"rps": float64(count) / elapsed, "p50_us": pct(.5), "p95_us": pct(.95), "p99_us": pct(.99),
		"generator_cpu_s": cpu, "generator_cpu_cores": cpu / elapsed, "generator_procs": runtime.GOMAXPROCS(0),
		"generator_cpu_saturation_suspected": cpu/elapsed >= .8*float64(runtime.GOMAXPROCS(0)),
		"generator_alloc_bytes":              after.TotalAlloc - before.TotalAlloc, "generator_gc": after.NumGC - before.NumGC,
	})
}
