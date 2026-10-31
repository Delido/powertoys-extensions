// signal-helper replaces the SignalCompanion tray app for the SignalRGB extensions
// (Raycast and PowerToys). It runs in the background (started and supervised by them) and
// provides:
//
//   - Mic Mute Mirror: headset mute button <-> Windows default microphone
//   - Battery Alert:   headset battery polling with a sound below a threshold
//   - Game Mode:       keyboard hardware game mode while a game runs
//     (game state is reported by the Power Plan extension)
//   - CS2 Integration: Game State Integration receiver (:3000) and the HTTPS
//     bridge (:3443) read by the "CS2 Reactive" SignalRGB effect
//   - Audio Router:    the old SignalCompanion audio API on :3010 (/next, /set …)
//
// Usage:
//
//	signal-helper run    -config <file> -data <dir>   run in the foreground
//	signal-helper launch -config <file> -data <dir>   start "run" detached and exit
package main

import (
	"encoding/json"
	"flag"
	"fmt"
	"io"
	"log"
	"net"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"syscall"
	"time"
	"unsafe"

	"golang.org/x/sys/windows"
)

const version = "1.0.0"

var sprintf = fmt.Sprintf

var logger *log.Logger

func logf(format string, args ...any) {
	if logger != nil {
		logger.Printf(format, args...)
	}
}

type Config struct {
	Mic struct {
		Enabled           bool `json:"enabled"`
		HardwareToWindows bool `json:"hardwareToWindows"`
		WindowsToHardware bool `json:"windowsToHardware"`
	} `json:"mic"`
	Battery struct {
		Enabled        bool    `json:"enabled"`
		Threshold      int     `json:"threshold"`
		ReAlertMinutes float64 `json:"reAlertMinutes"`
		ConfirmSamples int     `json:"confirmSamples"`
		PollSeconds    float64 `json:"pollSeconds"`
		SoundPath      string  `json:"soundPath"`
	} `json:"battery"`
	GameMode struct {
		Enabled bool `json:"enabled"`
	} `json:"gameMode"`
	CS2 struct {
		Enabled   bool   `json:"enabled"`
		Port      int    `json:"port"`
		HTTPSPort int    `json:"httpsPort"`
		Token     string `json:"token"`
	} `json:"cs2"`
	Audio struct {
		Rotation []string `json:"rotation"`
	} `json:"audio"`
	TeamSpeak struct {
		Enabled         bool `json:"enabled"`
		FollowTeamSpeak bool `json:"followTeamSpeak"`
	} `json:"teamspeak"`
}

func defaultConfig() Config {
	var c Config
	c.Mic.Enabled, c.Mic.HardwareToWindows, c.Mic.WindowsToHardware = true, true, true
	c.Battery.Enabled, c.Battery.Threshold, c.Battery.ReAlertMinutes = true, 20, 30
	c.Battery.ConfirmSamples, c.Battery.PollSeconds = 2, 60
	c.GameMode.Enabled = true
	c.CS2.Enabled, c.CS2.Port, c.CS2.HTTPSPort, c.CS2.Token = true, 3000, 3443, "signalcompanion"
	c.TeamSpeak.Enabled, c.TeamSpeak.FollowTeamSpeak = true, true
	return c
}

type App struct {
	configPath string
	dataDir    string
	started    time.Time

	mu      sync.RWMutex
	cfg     Config
	cfgTime time.Time

	audio     *Audio
	headset   *Headset
	keyboard  *Keyboard
	cs2       *CS2
	teamspeak *TeamSpeak
}

func (a *App) Config() Config {
	a.mu.RLock()
	defer a.mu.RUnlock()
	return a.cfg
}

func (a *App) loadConfig() bool {
	info, err := os.Stat(a.configPath)
	if err != nil {
		return false
	}
	a.mu.RLock()
	same := info.ModTime().Equal(a.cfgTime)
	a.mu.RUnlock()
	if same {
		return false
	}
	data, err := os.ReadFile(a.configPath)
	if err != nil {
		return false
	}
	cfg := defaultConfig()
	if err := json.Unmarshal(data, &cfg); err != nil {
		logf("config: %v", err)
		return false
	}
	a.mu.Lock()
	a.cfg, a.cfgTime = cfg, info.ModTime()
	a.mu.Unlock()
	logf("config loaded from %s", a.configPath)
	return true
}

func openLog(dir string) {
	_ = os.MkdirAll(dir, 0o755)
	path := filepath.Join(dir, "helper.log")
	if info, err := os.Stat(path); err == nil && info.Size() > 1<<20 {
		_ = os.Rename(path, path+".old")
	}
	f, err := os.OpenFile(path, os.O_CREATE|os.O_APPEND|os.O_WRONLY, 0o644)
	var w io.Writer = os.Stderr
	if err == nil {
		w = f
	}
	logger = log.New(w, "", log.LstdFlags)
}

// launch starts "run" as a detached process outside Raycast's job object, so it
// keeps running after the Raycast command that started it has finished.
func launch(args []string) {
	exe, _ := os.Executable()
	cmdline := windows.ComposeCommandLine(append([]string{exe, "run"}, args...))
	cmd, _ := windows.UTF16PtrFromString(cmdline)
	var si windows.StartupInfo
	si.Cb = uint32(unsafe.Sizeof(si))
	var pi windows.ProcessInformation
	const breakaway, detached, newGroup = 0x01000000, 0x00000008, 0x00000200
	err := windows.CreateProcess(nil, cmd, nil, nil, false, breakaway|detached|newGroup, nil, nil, &si, &pi)
	if err != nil {
		// The job may not allow breakaway; start normally then.
		err = windows.CreateProcess(nil, cmd, nil, nil, false, detached|newGroup, nil, nil, &si, &pi)
	}
	if err != nil {
		fmt.Printf("{\"ok\":false,\"error\":%q}\n", err.Error())
		os.Exit(1)
	}
	windows.CloseHandle(pi.Thread)
	windows.CloseHandle(pi.Process)
	fmt.Printf("{\"ok\":true,\"pid\":%d}\n", pi.ProcessId)
}

func main() {
	if len(os.Args) < 2 {
		fmt.Println("usage: signal-helper run|launch -config <file> -data <dir>")
		os.Exit(2)
	}
	mode := os.Args[1]
	fs := flag.NewFlagSet(mode, flag.ExitOnError)
	configPath := fs.String("config", "", "config JSON written by the extension")
	dataDir := fs.String("data", "", "folder for log, certificates and state")
	_ = fs.Parse(os.Args[2:])
	if *dataDir == "" {
		*dataDir = filepath.Join(knownFolder(windows.FOLDERID_LocalAppData, "LOCALAPPDATA"), "SignalRGBHelper")
	}

	switch mode {
	case "launch":
		launch(os.Args[2:])
		return
	case "run":
	default:
		fmt.Println("unknown mode", mode)
		os.Exit(2)
	}

	openLog(*dataDir)
	// One instance only: the API port is the lock.
	ln, err := net.Listen("tcp", "127.0.0.1:3010")
	if err != nil {
		logf("port 3010 busy (already running or SignalCompanion still open): %v", err)
		os.Exit(3)
	}
	logf("signal-helper %s starting (pid %d)", version, os.Getpid())

	app := &App{configPath: *configPath, dataDir: *dataDir, started: time.Now(), cfg: defaultConfig()}
	app.loadConfig()
	app.audio = newAudio()
	app.headset = newHeadset(app)
	app.keyboard = newKeyboard(app)
	app.cs2 = newCS2(app)
	app.teamspeak = newTeamSpeak(app)

	app.headset.Start()
	app.keyboard.Start()
	app.cs2.Apply()
	app.teamspeak.Start()

	go func() {
		for range time.Tick(3 * time.Second) {
			if app.loadConfig() {
				app.cs2.Apply()
			}
		}
	}()

	if err := serveAPI(app, ln); err != nil {
		logf("api server stopped: %v", err)
	}
}

// Shutdown is called by POST /api/quit.
func (a *App) Shutdown() {
	logf("quit requested")
	a.cs2.Stop()
	a.keyboard.Stop()
	time.Sleep(200 * time.Millisecond)
	os.Exit(0)
}

func isPortInUse(err error) bool {
	return err != nil && (strings.Contains(err.Error(), "bind") || err == syscall.Errno(10048))
}
