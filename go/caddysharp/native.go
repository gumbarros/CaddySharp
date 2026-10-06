package caddysharp

/*
#cgo LDFLAGS: -ldl
#define _GNU_SOURCE
#include <dlfcn.h>
#include <stdlib.h>
#include <stdint.h>
typedef struct { unsigned char* data; int32_t length; } cs_bytes;
typedef struct { cs_bytes name; cs_bytes value; } cs_header;
static void* library;
static int (*preload)(void);
static int32_t (*probe)(void);
static int32_t (*init_app)(cs_bytes,cs_bytes,cs_bytes,cs_header*,int32_t);
static intptr_t (*start)(cs_bytes,cs_bytes,cs_bytes,cs_bytes,cs_bytes,cs_bytes,cs_bytes,cs_bytes,cs_header*,int32_t,int64_t);
static int32_t (*wait_request)(intptr_t);
static int32_t (*is_completed)(intptr_t);
static void (*cancel_request)(intptr_t);
static int32_t (*status)(intptr_t);
static int32_t (*body_length)(intptr_t);
static int32_t (*copy_body)(intptr_t,unsigned char*,int32_t);
static int32_t (*copy_headers)(intptr_t,unsigned char*,int32_t);
static int32_t (*header_count)(intptr_t);
static int32_t (*copy_header)(intptr_t,int32_t,unsigned char*,int32_t,unsigned char*,int32_t);
static void (*free_request)(intptr_t);
static int32_t (*complete_request)(intptr_t);
static int32_t (*shutdown_app)(void);
static int load_native(const char* path) {
 if (library) return 0;
 library=dlopen(path,RTLD_NOW|RTLD_LOCAL);
 if (!library) return -1;
 #define GET(v,n) v=dlsym(library,n); if (!v) return -2;
 GET(preload,"try_preload_runtime") GET(probe,"caddysharp_probe") GET(init_app,"caddysharp_init")
 GET(start,"caddysharp_start") GET(wait_request,"caddysharp_wait") GET(is_completed,"caddysharp_is_completed") GET(cancel_request,"caddysharp_cancel")
 GET(status,"caddysharp_status") GET(body_length,"caddysharp_body_length") GET(copy_body,"caddysharp_copy_body")
 GET(copy_headers,"caddysharp_copy_headers") GET(header_count,"caddysharp_header_count") GET(copy_header,"caddysharp_copy_header") GET(free_request,"caddysharp_free") GET(complete_request,"caddysharp_complete") GET(shutdown_app,"caddysharp_shutdown")
 return 0;
}
static int do_preload(void) { return preload(); }
static int do_probe(void) { return probe(); }
static int do_init(cs_bytes a,cs_bytes r,cs_bytes e,cs_header* h,int n) { return init_app(a,r,e,h,n); }
static intptr_t do_start(cs_bytes m,cs_bytes s,cs_bytes h,cs_bytes p,cs_bytes raw,cs_bytes q,cs_bytes r,cs_bytes b,cs_header* hs,int n,int64_t max) { return start(m,s,h,p,raw,q,r,b,hs,n,max); }
static int do_wait(intptr_t h) { return wait_request(h); }
static int do_is_completed(intptr_t h) { return is_completed(h); }
static void do_cancel(intptr_t h) { cancel_request(h); }
static int do_status(intptr_t h) { return status(h); }
static int do_body_length(intptr_t h) { return body_length(h); }
static int do_copy_body(intptr_t h,unsigned char* b,int n) { return copy_body(h,b,n); }
static int do_copy_headers(intptr_t h,unsigned char* b,int n) { return copy_headers(h,b,n); }
static int do_header_count(intptr_t h) { return header_count(h); }
static int do_copy_header(intptr_t h,int i,unsigned char* n,int nc,unsigned char* v,int vc) { return copy_header(h,i,n,nc,v,vc); }
static void do_free(intptr_t h) { free_request(h); }
static int do_complete(intptr_t h) { return complete_request(h); }
static int do_shutdown(void) { return shutdown_app(); }
static const char* native_error(void) { return dlerror(); }
*/
import "C"
import (
	"encoding/binary"
	"fmt"
	"time"
	"unsafe"
)

func nativeBytes(s []byte) C.cs_bytes {
	if len(s) == 0 {
		return C.cs_bytes{}
	}
	return C.cs_bytes{data: (*C.uchar)(C.CBytes(s)), length: C.int32_t(len(s))}
}
func releaseBytes(b C.cs_bytes) { C.free(unsafe.Pointer(b.data)) }

type header struct{ name, value string }

func nativeHeaders(h []header) (*C.cs_header, func()) {
	if len(h) == 0 {
		return nil, func() {}
	}
	size := C.size_t(len(h)) * C.size_t(unsafe.Sizeof(C.cs_header{}))
	ptr := (*C.cs_header)(C.malloc(size))
	arr := unsafe.Slice(ptr, len(h))
	for i, v := range h {
		arr[i].name = nativeBytes([]byte(v.name))
		arr[i].value = nativeBytes([]byte(v.value))
	}
	return ptr, func() {
		for _, v := range arr {
			releaseBytes(v.name)
			releaseBytes(v.value)
		}
		C.free(unsafe.Pointer(ptr))
	}
}
func loadNative(path string) error {
	p := C.CString(path)
	defer C.free(unsafe.Pointer(p))
	if rc := C.load_native(p); rc != 0 {
		return fmt.Errorf("dlopen/dlsym (%d): %s", int(rc), C.GoString(C.native_error()))
	}
	if rc := C.do_preload(); rc < 0 {
		return fmt.Errorf("DNNE preload: %d", int(rc))
	}
	if C.do_probe() != 0x43534801 {
		return fmt.Errorf("ABI probe mismatch")
	}
	return nil
}
func initNative(assembly, root, environment string, config []header) error {
	a, r, e := nativeBytes([]byte(assembly)), nativeBytes([]byte(root)), nativeBytes([]byte(environment))
	defer releaseBytes(a)
	defer releaseBytes(r)
	defer releaseBytes(e)
	hs, done := nativeHeaders(config)
	defer done()
	if rc := C.do_init(a, r, e, hs, C.int(len(config))); rc != 0 {
		return fmt.Errorf("managed init: %d", int(rc))
	}
	return nil
}
func requestNative(method, scheme, host, path, rawTarget, query, remote string, body []byte, headers []header, max int64) (C.intptr_t, error) {
	fields := []C.cs_bytes{nativeBytes([]byte(method)), nativeBytes([]byte(scheme)), nativeBytes([]byte(host)), nativeBytes([]byte(path)), nativeBytes([]byte(rawTarget)), nativeBytes([]byte(query)), nativeBytes([]byte(remote)), nativeBytes(body)}
	defer func() {
		for _, b := range fields {
			releaseBytes(b)
		}
	}()
	hs, done := nativeHeaders(headers)
	defer done()
	h := C.do_start(fields[0], fields[1], fields[2], fields[3], fields[4], fields[5], fields[6], fields[7], hs, C.int(len(headers)), C.int64_t(max))
	if h == 0 {
		return 0, fmt.Errorf("managed start failed")
	}
	return h, nil
}
func waitNative(h C.intptr_t) error {
	// caddysharp_wait blocks in managed code. Poll the explicit completion
	// export instead so no native thread remains parked for the request's
	// lifetime. The final wait is only reached after completion and therefore
	// remains a short error/result handoff.
	for {
		if C.do_is_completed(h) != 0 {
			break
		}
		time.Sleep(500 * time.Microsecond)
	}
	if C.do_wait(h) != 0 {
		return fmt.Errorf("managed request failed")
	}
	return nil
}
func cancelNative(h C.intptr_t) { C.do_cancel(h) }
func freeNative(h C.intptr_t)   { C.do_free(h) }
func completeNative(h C.intptr_t) error {
	if C.do_complete(h) != 0 {
		return fmt.Errorf("OnCompleted failed")
	}
	return nil
}
func shutdownNative() error {
	if C.do_shutdown() != 0 {
		return fmt.Errorf("managed shutdown failed")
	}
	return nil
}
func responseNative(h C.intptr_t) (int, []byte, []header, error) {
	n := int(C.do_body_length(h))
	if n < 0 {
		return 0, nil, nil, fmt.Errorf("invalid body length")
	}
	body := make([]byte, n)
	if n > 0 && int(C.do_copy_body(h, (*C.uchar)(unsafe.Pointer(&body[0])), C.int(n))) != n {
		return 0, nil, nil, fmt.Errorf("copy body")
	}
	nh := int(C.do_copy_headers(h, nil, 0))
	if nh < 0 {
		return 0, nil, nil, fmt.Errorf("invalid headers length")
	}
	block := make([]byte, nh)
	if nh > 0 && int(C.do_copy_headers(h, (*C.uchar)(unsafe.Pointer(&block[0])), C.int(nh))) != nh {
		return 0, nil, nil, fmt.Errorf("copy headers")
	}
	headers, err := decodeHeaders(block)
	if err != nil {
		return 0, nil, nil, err
	}
	return int(C.do_status(h)), body, headers, nil
}

// Length-prefixed UTF-8 pairs preserve ordering, duplicates, and empty values.
func decodeHeaders(block []byte) ([]header, error) {
	var headers []header
	for len(block) > 0 {
		if len(block) < 8 {
			return nil, fmt.Errorf("truncated header lengths")
		}
		n := uint64(binary.LittleEndian.Uint32(block))
		v := uint64(binary.LittleEndian.Uint32(block[4:]))
		block = block[8:]
		if n > 32767 || v > 65535 || n+v > uint64(len(block)) {
			return nil, fmt.Errorf("invalid header lengths")
		}
		headers = append(headers, header{string(block[:n]), string(block[n : n+v])})
		block = block[n+v:]
	}
	return headers, nil
}
