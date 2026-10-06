package main

/*
#cgo LDFLAGS: -ldl
#include <dlfcn.h>
#include <stdlib.h>
#include <stdint.h>
typedef int (*preload_fn)(void);
typedef int32_t (*probe_fn)(void);
static int run_probe(const char *path, int *preload) {
 void *lib = dlopen(path, RTLD_NOW|RTLD_LOCAL);
 if (!lib) return -1;
 preload_fn p = (preload_fn)dlsym(lib, "try_preload_runtime");
 probe_fn f = (probe_fn)dlsym(lib, "caddysharp_probe");
 if (!p || !f) return -2;
 *preload = p();
 if (*preload < 0) return -3;
 return f();
}
*/
import "C"
import (
	"fmt"
	"os"
	"unsafe"
)

func main() {
	if len(os.Args) != 2 {
		panic("usage: probe /path/to/CaddySharpNE.so")
	}
	path := C.CString(os.Args[1])
	defer C.free(unsafe.Pointer(path))
	var preload C.int
	result := C.run_probe(path, &preload)
	fmt.Printf("preload=%d probe=%x\n", int(preload), int(result))
	if result != 0x43534801 {
		os.Exit(1)
	}
}
