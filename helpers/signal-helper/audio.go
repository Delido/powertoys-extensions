package main

import (
	"errors"
	"fmt"
	"runtime"
	"syscall"
	"unsafe"

	"golang.org/x/sys/windows"
)

// Windows Core Audio via raw COM vtable calls: default microphone mute and
// playback device switching (IPolicyConfig, the undocumented interface used by
// EarTrumpet, SoundVolumeView and the original SignalCompanion audio router).
// All calls run on one OS thread with COM initialised.

var (
	ole32                 = windows.NewLazySystemDLL("ole32.dll")
	procCoCreateInstance  = ole32.NewProc("CoCreateInstance")
	procPropVariantClear  = ole32.NewProc("PropVariantClear")
	procCoTaskMemFree     = ole32.NewProc("CoTaskMemFree")
	clsidMMDeviceEnum     = mustGUID("{BCDE0395-E52F-467C-8E3D-C4579291692E}")
	iidMMDeviceEnum       = mustGUID("{A95664D2-9614-4F35-A746-DE8DB63617E6}")
	iidAudioEndpointVol   = mustGUID("{5CDF2C82-841E-4546-9722-0CF74078229A}")
	clsidPolicyConfig     = mustGUID("{870af99c-171d-4f9e-af0d-e63df40c2bc9}")
	iidPolicyConfig       = mustGUID("{f8679f50-850a-41cf-9c72-430f290290c8}")
	pkeyFriendlyNameFmtID = mustGUID("{a45c254e-df1c-4efd-8020-67d146a850e0}")
)

const (
	clsctxAll         = 0x17
	eRender           = 0
	eCapture          = 1
	eMultimedia       = 1
	deviceStateActive = 1
	vtLPWSTR          = 31
)

func mustGUID(s string) windows.GUID {
	g, err := windows.GUIDFromString(s)
	if err != nil {
		panic(err)
	}
	return g
}

// vcall calls method `index` of a COM object: the object's first word points to its
// vtable. Reading it through &obj avoids converting a uintptr back to a pointer.
func vcall(obj uintptr, index int, args ...uintptr) uintptr {
	vtbl := **(***[64]uintptr)(unsafe.Pointer(&obj))
	r, _, _ := syscall.SyscallN(vtbl[index], append([]uintptr{obj}, args...)...)
	return r
}

func release(obj uintptr) {
	if obj != 0 {
		vcall(obj, 2)
	}
}

func hresult(r uintptr, what string) error {
	if int32(r) < 0 {
		return fmt.Errorf("%s failed: 0x%08x", what, uint32(r))
	}
	return nil
}

type comThread struct{ jobs chan func() }

func newComThread() *comThread {
	t := &comThread{jobs: make(chan func())}
	go func() {
		runtime.LockOSThread()
		windows.CoInitializeEx(0, windows.COINIT_MULTITHREADED)
		for job := range t.jobs {
			job()
		}
	}()
	return t
}

func (t *comThread) do(f func()) {
	done := make(chan struct{})
	t.jobs <- func() {
		defer close(done)
		defer func() {
			if r := recover(); r != nil {
				logf("audio: COM call panicked: %v", r)
			}
		}()
		f()
	}
	<-done
}

type AudioDevice struct {
	ID         string `json:"id"`
	Name       string `json:"name"`
	Default    bool   `json:"default"`
	InRotation bool   `json:"in_rotation"`
}

type Audio struct{ com *comThread }

func newAudio() *Audio { return &Audio{com: newComThread()} }

func createInstance(clsid, iid *windows.GUID) (uintptr, error) {
	var obj uintptr
	r, _, _ := procCoCreateInstance.Call(uintptr(unsafe.Pointer(clsid)), 0, clsctxAll,
		uintptr(unsafe.Pointer(iid)), uintptr(unsafe.Pointer(&obj)))
	if err := hresult(r, "CoCreateInstance"); err != nil {
		return 0, err
	}
	return obj, nil
}

func deviceID(dev uintptr) string {
	var p *uint16
	if vcall(dev, 5, uintptr(unsafe.Pointer(&p))) != 0 || p == nil {
		return ""
	}
	defer procCoTaskMemFree.Call(uintptr(unsafe.Pointer(p)))
	return windows.UTF16PtrToString(p)
}

func friendlyName(dev uintptr) string {
	var store uintptr
	if vcall(dev, 4, 0, uintptr(unsafe.Pointer(&store))) != 0 || store == 0 { // STGM_READ
		return ""
	}
	defer release(store)
	key := struct {
		fmtid windows.GUID
		pid   uint32
	}{pkeyFriendlyNameFmtID, 14}
	var pv [24]byte
	if vcall(store, 5, uintptr(unsafe.Pointer(&key)), uintptr(unsafe.Pointer(&pv[0]))) != 0 {
		return ""
	}
	defer procPropVariantClear.Call(uintptr(unsafe.Pointer(&pv[0])))
	if *(*uint16)(unsafe.Pointer(&pv[0])) != vtLPWSTR {
		return ""
	}
	return windows.UTF16PtrToString(*(**uint16)(unsafe.Pointer(&pv[8])))
}

func defaultEndpoint(enum uintptr, flow int) (uintptr, error) {
	var dev uintptr
	if err := hresult(vcall(enum, 4, uintptr(flow), eMultimedia, uintptr(unsafe.Pointer(&dev))), "GetDefaultAudioEndpoint"); err != nil {
		return 0, err
	}
	return dev, nil
}

// withMicVolume runs f with the IAudioEndpointVolume of the default microphone.
func (a *Audio) withMicVolume(f func(vol uintptr, id, name string) error) (err error) {
	a.com.do(func() {
		enum, e := createInstance(&clsidMMDeviceEnum, &iidMMDeviceEnum)
		if e != nil {
			err = e
			return
		}
		defer release(enum)
		dev, e := defaultEndpoint(enum, eCapture)
		if e != nil {
			err = errors.New("no default microphone")
			return
		}
		defer release(dev)
		var vol uintptr
		if e := hresult(vcall(dev, 3, uintptr(unsafe.Pointer(&iidAudioEndpointVol)), clsctxAll, 0, uintptr(unsafe.Pointer(&vol))), "Activate"); e != nil {
			err = e
			return
		}
		defer release(vol)
		err = f(vol, deviceID(dev), friendlyName(dev))
	})
	return err
}

type MicInfo struct {
	ID    string `json:"id"`
	Name  string `json:"name"`
	Muted bool   `json:"muted"`
}

func (a *Audio) Mic() (info MicInfo, err error) {
	err = a.withMicVolume(func(vol uintptr, id, name string) error {
		var muted int32
		if err := hresult(vcall(vol, 15, uintptr(unsafe.Pointer(&muted))), "GetMute"); err != nil {
			return err
		}
		info = MicInfo{ID: id, Name: name, Muted: muted != 0}
		return nil
	})
	return info, err
}

func (a *Audio) SetMicMute(muted bool) error {
	return a.withMicVolume(func(vol uintptr, _, _ string) error {
		v := uintptr(0)
		if muted {
			v = 1
		}
		return hresult(vcall(vol, 14, v, 0), "SetMute")
	})
}

// Outputs lists active playback devices.
func (a *Audio) Outputs() (list []AudioDevice, err error) {
	a.com.do(func() {
		enum, e := createInstance(&clsidMMDeviceEnum, &iidMMDeviceEnum)
		if e != nil {
			err = e
			return
		}
		defer release(enum)
		defaultID := ""
		if dev, e := defaultEndpoint(enum, eRender); e == nil {
			defaultID = deviceID(dev)
			release(dev)
		}
		var coll uintptr
		if e := hresult(vcall(enum, 3, eRender, deviceStateActive, uintptr(unsafe.Pointer(&coll))), "EnumAudioEndpoints"); e != nil {
			err = e
			return
		}
		defer release(coll)
		var count uint32
		vcall(coll, 3, uintptr(unsafe.Pointer(&count)))
		list = []AudioDevice{}
		for i := uint32(0); i < count; i++ {
			var dev uintptr
			if vcall(coll, 4, uintptr(i), uintptr(unsafe.Pointer(&dev))) != 0 {
				continue
			}
			id := deviceID(dev)
			list = append(list, AudioDevice{ID: id, Name: friendlyName(dev), Default: id == defaultID})
			release(dev)
		}
	})
	return list, err
}

// SetDefaultOutput makes the device the default for all three roles.
func (a *Audio) SetDefaultOutput(id string) (err error) {
	a.com.do(func() {
		pc, e := createInstance(&clsidPolicyConfig, &iidPolicyConfig)
		if e != nil {
			err = e
			return
		}
		defer release(pc)
		wid, _ := windows.UTF16PtrFromString(id)
		for role := uintptr(0); role < 3; role++ {
			if e := hresult(vcall(pc, 13, uintptr(unsafe.Pointer(wid)), role), "SetDefaultEndpoint"); e != nil {
				err = e
				return
			}
		}
	})
	return err
}
