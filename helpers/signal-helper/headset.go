package main

import (
	"sync"
	"time"
	"unsafe"

	"golang.org/x/sys/windows"
)

// Corsair Virtuoso XT Wireless (protocol from SignalCompanion / SignalRGB plugin):
//
//	event channel   FF42/0002: mute button -> 03 01 01 46 00 <value>
//	command channel FF42/0001: report 0x02, conn byte 0x09 (wireless)
//	  SET  02 09 01 <register> 00 <value>      (mute: LED echo 0x8E, then mic 0x46)
//	  READ 02 09 02 0F 00 -> response with byte[2]==0x02, battery = LE bytes[4..6] / 10
const (
	headsetVID       = 0x1B1C
	headsetPID       = 0x0A64
	headsetName      = "Corsair Virtuoso XT Wireless"
	vendorUsagePage  = 0xFF42
	eventUsage       = 0x0002
	commandUsage     = 0x0001
	micRegister      = 0x46
	ledEchoRegister  = 0x8E
	connWireless     = 0x09
	batteryProp      = 0x0F
	readOpcode       = 0x02
	batteryValueByte = 4
)

var (
	winmm         = windows.NewLazySystemDLL("winmm.dll")
	procPlaySound = winmm.NewProc("PlaySoundW")
	user32h       = windows.NewLazySystemDLL("user32.dll")
	procBeep      = user32h.NewProc("MessageBeep")
)

type HeadsetStatus struct {
	Name           string     `json:"name"`
	Present        bool       `json:"present"`
	Listening      bool       `json:"listening"`
	HardwareMuted  *bool      `json:"hardwareMuted"`
	Battery        *int       `json:"battery"`
	BatteryAt      *time.Time `json:"batteryAt,omitempty"`
	BatteryLow     bool       `json:"batteryLow"`
	LastAlertAt    *time.Time `json:"lastAlertAt,omitempty"`
	AlertCount     int        `json:"alertCount"`
	WindowsMuted   *bool      `json:"windowsMuted"`
	MicName        string     `json:"micName,omitempty"`
	LastMuteChange *time.Time `json:"lastMuteChange,omitempty"`
	LastMuteSource string     `json:"lastMuteSource,omitempty"`
}

type Headset struct {
	app   *App
	mu    sync.Mutex
	st    HeadsetStatus
	cmdMu sync.Mutex // one command-channel roundtrip at a time
	below bool
}

func newHeadset(app *App) *Headset {
	return &Headset{app: app, st: HeadsetStatus{Name: headsetName}}
}

func (h *Headset) Status() HeadsetStatus {
	h.mu.Lock()
	defer h.mu.Unlock()
	st := h.st
	st.Present = hidPresent(headsetVID, headsetPID)
	return st
}

func (h *Headset) Start() {
	go h.eventLoop()
	go h.windowsLoop()
	go h.batteryLoop()
}

func boolPtr(b bool) *bool { return &b }

func (h *Headset) setListening(v bool) {
	h.mu.Lock()
	h.st.Listening = v
	h.mu.Unlock()
}

// eventLoop listens for the mute button on the passive event channel.
func (h *Headset) eventLoop() {
	for {
		dev, err := openHID(headsetVID, headsetPID, vendorUsagePage, eventUsage)
		if err != nil {
			h.setListening(false)
			time.Sleep(5 * time.Second)
			continue
		}
		logf("headset: listening on event channel")
		h.setListening(true)
		for {
			data, err := dev.Read()
			if err != nil {
				break
			}
			if muted, ok := parseMuteEvent(data); ok {
				h.onHardwareMute(muted)
			}
		}
		dev.Close()
		h.setListening(false)
		logf("headset: event channel closed, reopening")
		time.Sleep(2 * time.Second)
	}
}

// parseMuteEvent accepts the frame with or without a leading report-ID byte.
func parseMuteEvent(data []byte) (muted bool, ok bool) {
	for _, off := range []int{0, 1} {
		if len(data) >= off+6 && data[off] == 0x03 && data[off+1] == 0x01 && data[off+2] == 0x01 && data[off+3] == micRegister {
			return data[off+5] != 0, true
		}
	}
	return false, false
}

func (h *Headset) onHardwareMute(muted bool) {
	h.mu.Lock()
	changed := h.st.HardwareMuted == nil || *h.st.HardwareMuted != muted
	h.st.HardwareMuted = boolPtr(muted)
	if changed {
		now := time.Now()
		h.st.LastMuteChange, h.st.LastMuteSource = &now, "headset"
	}
	h.mu.Unlock()
	if !changed {
		return
	}
	cfg := h.app.Config()
	go h.app.teamspeak.SetMuted(muted)
	if !cfg.Mic.Enabled || !cfg.Mic.HardwareToWindows {
		logf("headset: button -> %s (not mirrored)", muteWord(muted))
		return
	}
	if err := h.app.audio.SetMicMute(muted); err != nil {
		logf("headset: button -> %s, Windows mute failed: %v", muteWord(muted), err)
		return
	}
	h.mu.Lock()
	h.st.WindowsMuted = boolPtr(muted)
	h.mu.Unlock()
	logf("headset: button -> %s -> Windows mic synced", muteWord(muted))
}

func muteWord(m bool) string {
	if m {
		return "MUTED"
	}
	return "UNMUTED"
}

// windowsLoop polls the Windows default mic and pushes changes to the headset.
func (h *Headset) windowsLoop() {
	var last *bool
	for range time.Tick(500 * time.Millisecond) {
		info, err := h.app.audio.Mic()
		if err != nil {
			continue
		}
		h.mu.Lock()
		h.st.WindowsMuted, h.st.MicName = boolPtr(info.Muted), info.Name
		hw := h.st.HardwareMuted
		h.mu.Unlock()
		if last != nil && *last != info.Muted {
			go h.app.teamspeak.SetMuted(info.Muted)
			cfg := h.app.Config()
			// Skip the echo of a change that came from the headset button itself.
			if cfg.Mic.Enabled && cfg.Mic.WindowsToHardware && (hw == nil || *hw != info.Muted) {
				if err := h.setHardwareMute(info.Muted); err != nil {
					logf("headset: Windows mic -> %s, headset sync failed: %v", muteWord(info.Muted), err)
				} else {
					now := time.Now()
					h.mu.Lock()
					h.st.HardwareMuted = boolPtr(info.Muted)
					h.st.LastMuteChange, h.st.LastMuteSource = &now, "windows"
					h.mu.Unlock()
					logf("headset: Windows mic -> %s -> headset synced", muteWord(info.Muted))
				}
			}
		}
		last = boolPtr(info.Muted)
	}
}

func (h *Headset) setHardwareMute(muted bool) error {
	h.cmdMu.Lock()
	defer h.cmdMu.Unlock()
	dev, err := openHID(headsetVID, headsetPID, vendorUsagePage, commandUsage)
	if err != nil {
		return err
	}
	defer dev.Close()
	v := byte(0)
	if muted {
		v = 1
	}
	if err := dev.Write([]byte{0x02, connWireless, 0x01, ledEchoRegister, 0x00, v}); err != nil {
		return err
	}
	return dev.Write([]byte{0x02, connWireless, 0x01, micRegister, 0x00, v})
}

// ReadBattery returns the level in percent, or -1 if it couldn't be read.
// 0 % is treated as a failed read: the headset reports it while waking up.
func (h *Headset) ReadBattery() int {
	h.cmdMu.Lock()
	defer h.cmdMu.Unlock()
	dev, err := openHID(headsetVID, headsetPID, vendorUsagePage, commandUsage)
	if err != nil {
		return -1
	}
	defer dev.Close()
	dev.Flush()
	result := make(chan [][]byte, 1)
	go func() { result <- dev.Collect(time.Second) }()
	time.Sleep(20 * time.Millisecond)
	if err := dev.Write([]byte{0x02, connWireless, readOpcode, batteryProp, 0x00}); err != nil {
		<-result
		return -1
	}
	for _, r := range <-result {
		if len(r) > batteryValueByte+2 && r[2] == readOpcode {
			raw := int(r[batteryValueByte]) | int(r[batteryValueByte+1])<<8 | int(r[batteryValueByte+2])<<16
			percent := (raw + 5) / 10
			if percent > 0 && percent <= 100 {
				now := time.Now()
				h.mu.Lock()
				h.st.Battery, h.st.BatteryAt = &percent, &now
				h.mu.Unlock()
				return percent
			}
		}
	}
	return -1
}

func (h *Headset) batteryLoop() {
	time.Sleep(3 * time.Second)
	for {
		cfg := h.app.Config()
		if cfg.Battery.Enabled && hidPresent(headsetVID, headsetPID) {
			h.batteryTick(cfg)
		}
		wait := cfg.Battery.PollSeconds
		if wait < 15 {
			wait = 15
		}
		time.Sleep(time.Duration(wait * float64(time.Second)))
	}
}

func (h *Headset) batteryTick(cfg Config) {
	threshold := cfg.Battery.Threshold
	level := h.ReadBattery()
	if level > threshold {
		h.setLow(false)
		return
	}
	// Low or failed: confirm with a few more reads spread over a minute.
	n := max(1, cfg.Battery.ConfirmSamples)
	spacing := time.Duration(60/float64(n+1)) * time.Second
	var valid []int
	for i := 0; i < n; i++ {
		time.Sleep(spacing)
		if v := h.ReadBattery(); v > 0 {
			valid = append(valid, v)
		}
	}
	if len(valid) == 0 {
		return
	}
	for _, v := range valid {
		if v > threshold {
			h.setLow(false)
			return
		}
	}
	h.setLow(true)
	h.maybeAlert(cfg)
}

func (h *Headset) setLow(low bool) {
	h.mu.Lock()
	h.st.BatteryLow = low
	if !low {
		h.below = false
	}
	h.mu.Unlock()
}

func (h *Headset) maybeAlert(cfg Config) {
	h.mu.Lock()
	now := time.Now()
	reAlert := time.Duration(cfg.Battery.ReAlertMinutes * float64(time.Minute))
	first := !h.below
	due := reAlert > 0 && h.st.LastAlertAt != nil && now.Sub(*h.st.LastAlertAt) >= reAlert
	if !first && !due {
		h.mu.Unlock()
		return
	}
	h.below = true
	h.st.LastAlertAt = &now
	h.st.AlertCount++
	h.mu.Unlock()
	logf("battery low confirmed (<= %d%%) -> alert", cfg.Battery.Threshold)
	playSound(cfg.Battery.SoundPath)
}

func playSound(path string) {
	if path != "" {
		p, _ := windows.UTF16PtrFromString(path)
		const sndAsync, sndNoDefault, sndFilename = 0x0001, 0x0002, 0x00020000
		if r, _, _ := procPlaySound.Call(uintptr(unsafe.Pointer(p)), 0, sndAsync|sndNoDefault|sndFilename); r != 0 {
			return
		}
	}
	procBeep.Call(0xFFFFFFFF)
}
