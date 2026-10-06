package caddysharp

import (
	"encoding/binary"
	"reflect"
	"testing"
)

func TestDecodeHeaders(t *testing.T) {
	want := []header{{"Set-Cookie", "a=1"}, {"Set-Cookie", "b=2"}, {"X-Empty", ""}, {"X-Value", "Ω"}}
	var block []byte
	for _, h := range want {
		block = binary.LittleEndian.AppendUint32(block, uint32(len(h.name)))
		block = binary.LittleEndian.AppendUint32(block, uint32(len(h.value)))
		block = append(block, h.name...)
		block = append(block, h.value...)
	}
	got, err := decodeHeaders(block)
	if err != nil || !reflect.DeepEqual(got, want) {
		t.Fatalf("got %v, %v", got, err)
	}
	for _, bad := range [][]byte{{0}, {0, 0, 0, 0, 1, 0, 0, 0}, {255, 255, 255, 255, 255, 255, 255, 255}} {
		if _, err := decodeHeaders(bad); err == nil {
			t.Fatalf("accepted malformed block: %v", bad)
		}
	}
}
