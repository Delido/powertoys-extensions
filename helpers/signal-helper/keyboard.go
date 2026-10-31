package main

import (
	"sync"
	"time"
)

// Corsair Vanguard Pro 96: hardware game mode on the command interface FF42/0001,
// report 0x00 + 00 01 02 01 E1 00 <0|1> (same bytes as SignalRGB's setHardwareGameMode).
const (
	keyboardVID  = 0x1B1C
	keyboardPID  = 0x2B0E
	keyboardName = "Corsair Vanguard Pro 96"
	// Game state from the Power Plan extension expires if it stops reporting.
	gameLease = 60 * time.Second
)

type KeyboardStatus struct {
	Name       string     `json:"name"`
	Present    bool       `json:"present"`
	GameMode   *bool      `json:"gameMode"`
	GameActive bool       `json:"gameActive"`
	Game       string     `json:"game,omitempty"`
	ReportedAt *time.Time `json:"reportedAt,omitempty"`
	LastError  string     `json:"lastError,omitempty"`
}

type Keyboard struct {
	app  *App
	mu   sync.Mutex
	st   KeyboardStatus
	stop chan struct{}
}

func newKeyboard(app *App) *Keyboard {
	return &Keyboard{app: app, st: KeyboardStatus{Name: keyboardName}, stop: make(chan struct{})}
}

func (k *Keyboard) Status() KeyboardStatus {
	k.mu.Lock()
	defer k.mu.Unlock()
	st := k.st
	st.Present = hidPresent(keyboardVID, keyboardPID)
	return st
}

// ReportGame is called with every report from the Power Plan extension.
func (k *Keyboard) ReportGame(active bool, name string) {
	now := time.Now()
	k.mu.Lock()
	k.st.GameActive, k.st.Game, k.st.ReportedAt = active, name, &now
	k.mu.Unlock()
	k.sync()
}

func (k *Keyboard) Start() {
	go func() {
		ticker := time.NewTicker(2 * time.Second)
		defer ticker.Stop()
		for {
			select {
			case <-k.stop:
				return
			case <-ticker.C:
				k.sync()
			}
		}
	}()
}

func (k *Keyboard) Stop() {
	select {
	case <-k.stop:
	default:
		close(k.stop)
	}
}

func (k *Keyboard) desired() bool {
	k.mu.Lock()
	defer k.mu.Unlock()
	fresh := k.st.ReportedAt != nil && time.Since(*k.st.ReportedAt) < gameLease
	return k.app.Config().GameMode.Enabled && k.st.GameActive && fresh
}

// sync writes only on transitions; the initial "off" isn't written, so a game
// mode switched on by hand on the keyboard stays until a game starts and ends.
func (k *Keyboard) sync() {
	want := k.desired()
	k.mu.Lock()
	current := k.st.GameMode
	k.mu.Unlock()
	if (current == nil && !want) || (current != nil && *current == want) {
		return
	}
	err := setKeyboardGameMode(want)
	k.mu.Lock()
	defer k.mu.Unlock()
	if err != nil {
		k.st.LastError = err.Error()
		return
	}
	k.st.LastError = ""
	k.st.GameMode = boolPtr(want)
	logf("keyboard: game mode %v (game %q)", want, k.st.Game)
}

func setKeyboardGameMode(on bool) error {
	dev, err := openHID(keyboardVID, keyboardPID, vendorUsagePage, commandUsage)
	if err != nil {
		return err
	}
	defer dev.Close()
	v := byte(0)
	if on {
		v = 1
	}
	return dev.Write([]byte{0x00, 0x00, 0x01, 0x02, 0x01, 0xE1, 0x00, v})
}
