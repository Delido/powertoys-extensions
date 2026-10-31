package main

import (
	"bufio"
	"encoding/json"
	"io"
	"net"
	"net/http"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"time"
)

func writeJSON(w http.ResponseWriter, code int, v any) {
	w.Header().Set("Content-Type", "application/json")
	w.Header().Set("Access-Control-Allow-Origin", "*")
	w.WriteHeader(code)
	_ = json.NewEncoder(w).Encode(v)
}

func fail(w http.ResponseWriter, code int, msg string) {
	writeJSON(w, code, map[string]any{"ok": false, "error": msg})
}

// rotationMembers: configured devices that are active, else all active devices.
func rotationMembers(cfg Config, active []AudioDevice) []AudioDevice {
	byID := map[string]AudioDevice{}
	for _, d := range active {
		byID[d.ID] = d
	}
	var picked []AudioDevice
	for _, id := range cfg.Audio.Rotation {
		if d, ok := byID[id]; ok {
			picked = append(picked, d)
		}
	}
	if len(picked) == 0 {
		return active
	}
	return picked
}

func serveAPI(app *App, ln net.Listener) error {
	mux := http.NewServeMux()

	devices := func() ([]AudioDevice, error) {
		list, err := app.audio.Outputs()
		if err != nil {
			return nil, err
		}
		cfg := app.Config()
		members := map[string]bool{}
		for _, d := range rotationMembers(cfg, list) {
			members[d.ID] = true
		}
		for i := range list {
			list[i].InRotation = members[list[i].ID]
		}
		return list, nil
	}

	switchTo := func(w http.ResponseWriter, d AudioDevice) {
		if err := app.audio.SetDefaultOutput(d.ID); err != nil {
			fail(w, 500, err.Error())
			return
		}
		logf("audio: output -> %s", d.Name)
		d.Default = true
		writeJSON(w, 200, map[string]any{"ok": true, "device": d})
	}

	rotate := func(dir int) http.HandlerFunc {
		return func(w http.ResponseWriter, r *http.Request) {
			list, err := devices()
			if err != nil || len(list) == 0 {
				fail(w, 500, "no active output devices")
				return
			}
			mem := rotationMembers(app.Config(), list)
			idx := -1
			for i, d := range mem {
				if d.Default {
					idx = i
				}
			}
			next := mem[0]
			if idx >= 0 {
				next = mem[((idx+dir)%len(mem)+len(mem))%len(mem)]
			}
			switchTo(w, next)
		}
	}

	// ── SignalCompanion audio router compatible endpoints ──
	mux.HandleFunc("/next", rotate(1))
	mux.HandleFunc("/toggle", rotate(1))
	mux.HandleFunc("/prev", rotate(-1))
	mux.HandleFunc("/set", func(w http.ResponseWriter, r *http.Request) {
		list, err := devices()
		if err != nil {
			fail(w, 500, err.Error())
			return
		}
		q := r.URL.Query()
		for _, d := range list {
			if (q.Has("id") && d.ID == q.Get("id")) ||
				(q.Has("name") && strings.Contains(strings.ToLower(d.Name), strings.ToLower(q.Get("name")))) {
				switchTo(w, d)
				return
			}
		}
		if q.Has("i") {
			mem := rotationMembers(app.Config(), list)
			if i, err := strconv.Atoi(q.Get("i")); err == nil && i >= 0 && i < len(mem) {
				switchTo(w, mem[i])
				return
			}
		}
		fail(w, 404, "device not found")
	})
	mux.HandleFunc("/current", func(w http.ResponseWriter, r *http.Request) {
		list, err := devices()
		if err != nil {
			fail(w, 500, err.Error())
			return
		}
		for _, d := range list {
			if d.Default {
				writeJSON(w, 200, map[string]any{"ok": true, "current": d})
				return
			}
		}
		writeJSON(w, 200, map[string]any{"ok": true, "current": nil})
	})
	mux.HandleFunc("/devices", func(w http.ResponseWriter, r *http.Request) {
		list, err := devices()
		if err != nil {
			fail(w, 500, err.Error())
			return
		}
		writeJSON(w, 200, map[string]any{"ok": true, "devices": list})
	})

	// ── Raycast control API ──
	mux.HandleFunc("/api/status", func(w http.ResponseWriter, r *http.Request) {
		writeJSON(w, 200, map[string]any{
			"ok":        true,
			"version":   version,
			"pid":       os.Getpid(),
			"started":   app.started,
			"config":    app.configPath,
			"dataDir":   app.dataDir,
			"headset":   app.headset.Status(),
			"keyboard":  app.keyboard.Status(),
			"cs2":       app.cs2.Status(),
			"teamspeak": app.teamspeak.Status(),
		})
	})
	mux.HandleFunc("/api/mic", func(w http.ResponseWriter, r *http.Request) {
		info, err := app.audio.Mic()
		if err != nil {
			fail(w, 500, err.Error())
			return
		}
		muted := info.Muted
		switch r.URL.Query().Get("muted") {
		case "1", "true":
			muted = true
		case "0", "false":
			muted = false
		case "toggle", "":
			if r.Method == http.MethodPost {
				muted = !info.Muted
			}
		}
		if muted != info.Muted {
			if err := app.audio.SetMicMute(muted); err != nil {
				fail(w, 500, err.Error())
				return
			}
			info.Muted = muted
		}
		writeJSON(w, 200, map[string]any{"ok": true, "mic": info})
	})
	mux.HandleFunc("/api/game", func(w http.ResponseWriter, r *http.Request) {
		var body struct {
			Active bool   `json:"active"`
			Name   string `json:"name"`
		}
		_ = json.NewDecoder(io.LimitReader(r.Body, 1<<16)).Decode(&body)
		app.keyboard.ReportGame(body.Active, body.Name)
		writeJSON(w, 200, map[string]any{"ok": true, "keyboard": app.keyboard.Status()})
	})
	mux.HandleFunc("/api/battery", func(w http.ResponseWriter, r *http.Request) {
		level := app.headset.ReadBattery()
		if level < 0 {
			fail(w, 503, "battery could not be read (headset off or asleep?)")
			return
		}
		writeJSON(w, 200, map[string]any{"ok": true, "battery": level})
	})
	// Sends the TeamSpeak remote key after a delay, so it can be recorded in TeamSpeak's key bindings.
	mux.HandleFunc("/api/teamspeak/press", func(w http.ResponseWriter, r *http.Request) {
		if r.Method != http.MethodPost {
			fail(w, 405, "POST required")
			return
		}
		delay, _ := strconv.Atoi(r.URL.Query().Get("delay"))
		if st := app.teamspeak.Status(); st.State != "connected" {
			fail(w, 409, "TeamSpeak is not connected ("+st.State+")")
			return
		}
		go func() {
			time.Sleep(time.Duration(min(max(delay, 0), 30)) * time.Second)
			if err := app.teamspeak.Press(); err != nil {
				logf("teamspeak: %v", err)
			}
		}()
		writeJSON(w, 200, map[string]any{"ok": true, "delay": delay})
	})
	// Presses the key once and checks whether TeamSpeak toggled (result in status.teamspeak.keyBound).
	mux.HandleFunc("/api/teamspeak/test", func(w http.ResponseWriter, r *http.Request) {
		if r.Method != http.MethodPost {
			fail(w, 405, "POST required")
			return
		}
		if err := app.teamspeak.Test(); err != nil {
			fail(w, 409, err.Error())
			return
		}
		writeJSON(w, 200, map[string]any{"ok": true})
	})
	mux.HandleFunc("/api/test-sound", func(w http.ResponseWriter, r *http.Request) {
		playSound(app.Config().Battery.SoundPath)
		writeJSON(w, 200, map[string]any{"ok": true})
	})
	mux.HandleFunc("/api/log", func(w http.ResponseWriter, r *http.Request) {
		n, _ := strconv.Atoi(r.URL.Query().Get("lines"))
		if n <= 0 {
			n = 200
		}
		f, err := os.Open(filepath.Join(app.dataDir, "helper.log"))
		if err != nil {
			fail(w, 404, err.Error())
			return
		}
		defer f.Close()
		var lines []string
		sc := bufio.NewScanner(f)
		for sc.Scan() {
			lines = append(lines, sc.Text())
			if len(lines) > n {
				lines = lines[1:]
			}
		}
		writeJSON(w, 200, map[string]any{"ok": true, "lines": lines})
	})
	mux.HandleFunc("/api/quit", func(w http.ResponseWriter, r *http.Request) {
		if r.Method != http.MethodPost {
			fail(w, 405, "POST required")
			return
		}
		writeJSON(w, 200, map[string]any{"ok": true})
		go app.Shutdown()
	})
	mux.HandleFunc("/", func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path != "/" {
			fail(w, 404, "not found")
			return
		}
		writeJSON(w, 200, map[string]any{"ok": true, "service": "signal-helper", "version": version,
			"endpoints": []string{"/next", "/prev", "/set?name=", "/set?id=", "/set?i=", "/current", "/devices",
				"/api/status", "/api/mic", "/api/game", "/api/battery", "/api/log", "/api/quit"}})
	})

	srv := &http.Server{Handler: mux, ReadHeaderTimeout: 5 * time.Second}
	return srv.Serve(ln)
}
