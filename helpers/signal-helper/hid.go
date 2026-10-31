package main

import (
	"errors"
	"strings"
	"sync"
	"time"
	"unsafe"

	"golang.org/x/sys/windows"
)

// Minimal HID access via hid.dll: find a top-level collection by VID/PID and
// usage page/usage, then read/write raw reports. Input reports are delivered to
// every open handle, so this works next to SignalRGB and iCUE.

var (
	hidDLL               = windows.NewLazySystemDLL("hid.dll")
	procHidGetPreparsed  = hidDLL.NewProc("HidD_GetPreparsedData")
	procHidFreePreparsed = hidDLL.NewProc("HidD_FreePreparsedData")
	procHidGetCaps       = hidDLL.NewProc("HidP_GetCaps")
	procHidFlushQueue    = hidDLL.NewProc("HidD_FlushQueue")
	guidDevinterfaceHID  = windows.GUID{Data1: 0x4D1E55B2, Data2: 0xF16F, Data3: 0x11CF, Data4: [8]byte{0x88, 0xCB, 0x00, 0x11, 0x11, 0x00, 0x00, 0x30}}
	errHIDNotFound       = errors.New("device not found")
)

type hidCaps struct {
	Usage, UsagePage                uint16
	InputLen, OutputLen, FeatureLen uint16
	_                               [17]uint16
	_                               [10]uint16
}

type HIDDevice struct {
	Path      string
	Handle    windows.Handle
	InputLen  int
	OutputLen int
}

// openHID opens the collection with the given IDs and usage. Read and write
// handles share the device with other programs.
func openHID(vid, pid, usagePage, usage uint16) (*HIDDevice, error) {
	paths, err := windows.CM_Get_Device_Interface_List("", &guidDevinterfaceHID, windows.CM_GET_DEVICE_INTERFACE_LIST_PRESENT)
	if err != nil {
		return nil, err
	}
	needle := strings.ToLower(sprintf("vid_%04x&pid_%04x", vid, pid))
	for _, path := range paths {
		if !strings.Contains(strings.ToLower(path), needle) {
			continue
		}
		p, _ := windows.UTF16PtrFromString(path)
		h, err := windows.CreateFile(p, windows.GENERIC_READ|windows.GENERIC_WRITE,
			windows.FILE_SHARE_READ|windows.FILE_SHARE_WRITE, nil, windows.OPEN_EXISTING, 0, 0)
		if err != nil {
			continue
		}
		var pp uintptr
		var caps hidCaps
		if r, _, _ := procHidGetPreparsed.Call(uintptr(h), uintptr(unsafe.Pointer(&pp))); r == 0 {
			windows.CloseHandle(h)
			continue
		}
		procHidGetCaps.Call(pp, uintptr(unsafe.Pointer(&caps)))
		procHidFreePreparsed.Call(pp)
		if caps.UsagePage == usagePage && caps.Usage == usage {
			return &HIDDevice{Path: path, Handle: h, InputLen: int(caps.InputLen), OutputLen: int(caps.OutputLen)}, nil
		}
		windows.CloseHandle(h)
	}
	return nil, errHIDNotFound
}

func hidPresent(vid, pid uint16) bool {
	paths, err := windows.CM_Get_Device_Interface_List("", &guidDevinterfaceHID, windows.CM_GET_DEVICE_INTERFACE_LIST_PRESENT)
	if err != nil {
		return false
	}
	needle := strings.ToLower(sprintf("vid_%04x&pid_%04x", vid, pid))
	for _, path := range paths {
		if strings.Contains(strings.ToLower(path), needle) {
			return true
		}
	}
	return false
}

// Write sends one output report; data[0] is the report ID. The buffer is padded
// to the device's output report length.
func (d *HIDDevice) Write(data []byte) error {
	n := d.OutputLen
	if n < len(data) {
		n = len(data)
	}
	buf := make([]byte, n)
	copy(buf, data)
	var written uint32
	return windows.WriteFile(d.Handle, buf, &written, nil)
}

// Read blocks until the next input report arrives. Byte 0 is the report ID.
func (d *HIDDevice) Read() ([]byte, error) {
	buf := make([]byte, max(d.InputLen, 64))
	var got uint32
	if err := windows.ReadFile(d.Handle, buf, &got, nil); err != nil {
		return nil, err
	}
	return buf[:got], nil
}

func (d *HIDDevice) Flush() {
	procHidFlushQueue.Call(uintptr(d.Handle))
}

// Close cancels a pending Read on another goroutine, then closes the handle.
func (d *HIDDevice) Close() {
	if d != nil && d.Handle != 0 {
		windows.CancelIoEx(d.Handle, nil)
		windows.CloseHandle(d.Handle)
		d.Handle = 0
	}
}

// Collect reads input reports until the timeout and returns all of them.
func (d *HIDDevice) Collect(timeout time.Duration) [][]byte {
	var mu sync.Mutex
	var reports [][]byte
	done := make(chan struct{})
	go func() {
		defer close(done)
		for {
			data, err := d.Read()
			if err != nil {
				return
			}
			mu.Lock()
			reports = append(reports, data)
			mu.Unlock()
		}
	}()
	select {
	case <-done:
	case <-time.After(timeout):
		windows.CancelIoEx(d.Handle, nil)
		<-done
	}
	mu.Lock()
	defer mu.Unlock()
	return reports
}
