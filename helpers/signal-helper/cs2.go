package main

import (
	"bytes"
	"context"
	"crypto/rand"
	"crypto/rsa"
	"crypto/sha1"
	"crypto/tls"
	"crypto/x509"
	"crypto/x509/pkix"
	"encoding/json"
	"encoding/pem"
	"errors"
	"io"
	"math/big"
	"net"
	"net/http"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"sync"
	"time"
	"unsafe"

	"golang.org/x/sys/windows"
)

// CS2 Game State Integration, compatible with SignalCompanion:
//   - CS2 POSTs to http://127.0.0.1:<port> (cfg gamestate_integration_signalcompanion.cfg)
//   - the "CS2 Reactive" SignalRGB effect POSTs to https://127.0.0.1:3443/state
//     (HTTPS with a local CA appended to SignalRGB's Ultralight cacert.pem)

// Both servers only run while cs2.exe is running (plus a short grace period).
const (
	caMarker     = "\n# SignalCompanion CS2 Local CA\n"
	cs2Process   = "cs2.exe"
	cs2GraceTime = 30 * time.Second
)

type CS2Status struct {
	Enabled      bool           `json:"enabled"`
	GameRunning  bool           `json:"gameRunning"`
	Listening    bool           `json:"listening"`
	HTTPSRunning bool           `json:"httpsRunning"`
	Port         int            `json:"port"`
	HTTPSPort    int            `json:"httpsPort"`
	Error        string         `json:"error,omitempty"`
	CATrusted    []string       `json:"caTrusted"`
	CAPatchedNow bool           `json:"caPatchedNow"`
	Posts        int            `json:"posts"`
	LastPostAt   *time.Time     `json:"lastPostAt,omitempty"`
	State        map[string]any `json:"state"`
}

type CS2 struct {
	app   *App
	mu    sync.Mutex
	st    CS2Status
	state map[string]any
	http  *http.Server
	https *http.Server
	key   string    // port/token the running servers were started with
	up    bool      // servers started
	seen  time.Time // last time cs2.exe was running
	kick  chan struct{}
}

func newCS2(app *App) *CS2 {
	c := &CS2{app: app, state: map[string]any{"connected": false, "ts": 0}, kick: make(chan struct{}, 1)}
	go c.idleWatch()
	go c.gameWatch()
	return c
}

func (c *CS2) Status() CS2Status {
	c.mu.Lock()
	defer c.mu.Unlock()
	st := c.st
	if st.CATrusted == nil {
		st.CATrusted = []string{}
	}
	st.State = c.snapshotLocked()
	return st
}

func (c *CS2) snapshotLocked() map[string]any {
	out := make(map[string]any, len(c.state))
	for k, v := range c.state {
		out[k] = v
	}
	return out
}

func (c *CS2) snapshotJSON() []byte {
	c.mu.Lock()
	defer c.mu.Unlock()
	b, _ := json.Marshal(c.state)
	return b
}

// Apply re-evaluates the servers right away after a config change.
func (c *CS2) Apply() {
	select {
	case c.kick <- struct{}{}:
	default:
	}
}

// gameWatch starts the servers while CS2 runs and stops them afterwards.
func (c *CS2) gameWatch() {
	ticker := time.NewTicker(3 * time.Second)
	defer ticker.Stop()
	for {
		c.check()
		select {
		case <-ticker.C:
		case <-c.kick:
		}
	}
}

func (c *CS2) check() {
	cfg := c.app.Config().CS2
	running := processRunning(cs2Process)
	key := sprintf("%d|%d|%s", cfg.Port, cfg.HTTPSPort, cfg.Token)
	c.mu.Lock()
	if running {
		c.seen = time.Now()
	}
	c.st.Enabled, c.st.GameRunning, c.st.Port, c.st.HTTPSPort = cfg.Enabled, running, cfg.Port, cfg.HTTPSPort
	want := cfg.Enabled && (running || time.Since(c.seen) < cs2GraceTime)
	up, sameKey := c.up, c.key == key
	c.mu.Unlock()

	switch {
	case want && up && !sameKey:
		logf("cs2: settings changed, restarting servers")
		c.Stop()
		c.start(cfg.Port, cfg.HTTPSPort, cfg.Token, key)
	case want && !up:
		logf("cs2: %s running, starting servers", cs2Process)
		c.start(cfg.Port, cfg.HTTPSPort, cfg.Token, key)
	case !want && up:
		logf("cs2: %s not running anymore, stopping servers", cs2Process)
		c.Stop()
	}
}

func (c *CS2) start(port, httpsPort int, token, key string) {
	c.mu.Lock()
	c.up, c.key = true, key
	c.st.Error, c.st.CATrusted = "", []string{}
	c.mu.Unlock()
	c.startReceiver(port, token)
	c.startBridge(httpsPort)
}

func (c *CS2) Stop() {
	c.mu.Lock()
	srvs := []*http.Server{c.http, c.https}
	c.http, c.https = nil, nil
	c.up = false
	c.st.Listening, c.st.HTTPSRunning = false, false
	c.state = map[string]any{"connected": false, "ts": nowSeconds()}
	c.mu.Unlock()
	for _, s := range srvs {
		if s != nil {
			ctx, cancel := context.WithTimeout(context.Background(), time.Second)
			_ = s.Shutdown(ctx)
			cancel()
		}
	}
}

func (c *CS2) setError(err string) {
	c.mu.Lock()
	if c.st.Error == "" {
		c.st.Error = err
	} else {
		c.st.Error += "; " + err
	}
	c.mu.Unlock()
	logf("cs2: %s", err)
}

func nowSeconds() float64 { return float64(time.Now().UnixNano()) / 1e9 }

func cors(w http.ResponseWriter) {
	w.Header().Set("Access-Control-Allow-Origin", "*")
	w.Header().Set("Access-Control-Allow-Headers", "Content-Type")
}

func (c *CS2) startReceiver(port int, token string) {
	mux := http.NewServeMux()
	mux.HandleFunc("/", func(w http.ResponseWriter, r *http.Request) {
		switch r.Method {
		case http.MethodOptions:
			cors(w)
			w.WriteHeader(http.StatusNoContent)
		case http.MethodGet:
			cors(w)
			w.Header().Set("Content-Type", "application/json")
			_, _ = w.Write(c.snapshotJSON())
		case http.MethodPost:
			body, _ := io.ReadAll(io.LimitReader(r.Body, 4<<20))
			w.WriteHeader(http.StatusOK) // CS2 always expects 200
			var payload map[string]any
			if json.Unmarshal(body, &payload) != nil {
				return
			}
			if token != "" {
				auth, _ := payload["auth"].(map[string]any)
				if t, _ := auth["token"].(string); t != token {
					logf("cs2: rejected POST with bad auth token")
					return
				}
			}
			state := parseGSI(payload)
			now := time.Now()
			c.mu.Lock()
			c.state = state
			c.st.Posts++
			c.st.LastPostAt = &now
			c.mu.Unlock()
		default:
			w.WriteHeader(http.StatusMethodNotAllowed)
		}
	})
	ln, err := net.Listen("tcp", sprintf("127.0.0.1:%d", port))
	if err != nil {
		c.setError(sprintf("receiver port %d busy: %v", port, err))
		return
	}
	srv := &http.Server{Handler: mux, ReadHeaderTimeout: 5 * time.Second}
	c.mu.Lock()
	c.http, c.st.Listening = srv, true
	c.mu.Unlock()
	logf("cs2: GSI receiver on http://127.0.0.1:%d", port)
	go func() { _ = srv.Serve(ln) }()
}

func (c *CS2) startBridge(port int) {
	chain, key, caPEM, err := ensureCerts(filepath.Join(c.app.dataDir, "certs"))
	if err != nil {
		c.setError("certificates: " + err.Error())
		return
	}
	trusted, patched := patchCACert(caPEM)
	c.mu.Lock()
	c.st.CATrusted, c.st.CAPatchedNow = trusted, patched
	c.mu.Unlock()
	if patched {
		logf("cs2: trusted local CA in SignalRGB (restart SignalRGB once)")
	}
	cert, err := tls.X509KeyPair(chain, key)
	if err != nil {
		c.setError("certificate: " + err.Error())
		return
	}
	mux := http.NewServeMux()
	mux.HandleFunc("/", func(w http.ResponseWriter, r *http.Request) {
		origin := r.Header.Get("Origin")
		if origin == "" {
			origin = "*"
		}
		h := w.Header()
		h.Set("Access-Control-Allow-Origin", origin)
		h.Set("Access-Control-Allow-Private-Network", "true")
		h.Set("Access-Control-Allow-Methods", "GET, POST, OPTIONS")
		h.Set("Access-Control-Allow-Headers", "*")
		h.Set("Access-Control-Max-Age", "600")
		h.Set("Vary", "Origin")
		if r.Method == http.MethodOptions {
			w.WriteHeader(http.StatusNoContent)
			return
		}
		_, _ = io.Copy(io.Discard, r.Body)
		body := c.snapshotJSON()
		h.Set("Content-Type", "application/json")
		h.Set("Cache-Control", "no-store, no-cache, must-revalidate, max-age=0")
		h.Set("Pragma", "no-cache")
		h.Set("Expires", "0")
		h.Set("Content-Length", strconv.Itoa(len(body)))
		_, _ = w.Write(body)
	})
	ln, err := net.Listen("tcp", sprintf("127.0.0.1:%d", port))
	if err != nil {
		c.setError(sprintf("HTTPS port %d busy: %v", port, err))
		return
	}
	srv := &http.Server{Handler: mux, TLSConfig: &tls.Config{Certificates: []tls.Certificate{cert}}, ReadHeaderTimeout: 5 * time.Second}
	c.mu.Lock()
	c.https, c.st.HTTPSRunning = srv, true
	c.mu.Unlock()
	logf("cs2: HTTPS bridge on https://127.0.0.1:%d", port)
	go func() { _ = srv.ServeTLS(ln, "", "") }()
}

// idleWatch marks the state disconnected when CS2 stops posting (game closed).
func (c *CS2) idleWatch() {
	for range time.Tick(2 * time.Second) {
		c.mu.Lock()
		if connected, _ := c.state["connected"].(bool); connected {
			ts, _ := c.state["ts"].(float64)
			if nowSeconds()-ts > 10 {
				c.state["connected"] = false
			}
		}
		c.mu.Unlock()
	}
}

func m(v any) map[string]any {
	if mm, ok := v.(map[string]any); ok {
		return mm
	}
	return map[string]any{}
}

func toFloat(v any) any {
	switch x := v.(type) {
	case float64:
		return x
	case string:
		if f, err := strconv.ParseFloat(x, 64); err == nil {
			return f
		}
	}
	return nil
}

func orDefault(v any, d any) any {
	if v == nil {
		return d
	}
	return v
}

// parseGSI maps a CS2 GSI payload to the flat schema the effect reads.
func parseGSI(p map[string]any) map[string]any {
	player := m(p["player"])
	ps := m(player["state"])
	rnd := m(p["round"])
	mp := m(p["map"])
	stats := m(player["match_stats"])
	bomb := m(p["bomb"])
	pc := m(p["phase_countdowns"])

	bombCD := toFloat(bomb["countdown"])
	if bombCD == nil && pc["phase"] == "bomb" {
		bombCD = toFloat(pc["phase_ends_in"])
	}
	var weapon, clip, reserve any
	for _, w := range m(player["weapons"]) {
		wm := m(w)
		if wm["state"] == "active" {
			weapon, clip, reserve = wm["name"], wm["ammo_clip"], wm["ammo_reserve"]
			break
		}
	}
	bombState := bomb["state"]
	if bombState == nil || bombState == "" {
		bombState = rnd["bomb"]
	}
	return map[string]any{
		"connected":      true,
		"ts":             nowSeconds(),
		"health":         ps["health"],
		"armor":          ps["armor"],
		"helmet":         ps["helmet"],
		"flashed":        orDefault(ps["flashed"], 0),
		"smoked":         orDefault(ps["smoked"], 0),
		"burning":        orDefault(ps["burning"], 0),
		"round_kills":    ps["round_kills"],
		"team":           player["team"],
		"activity":       player["activity"],
		"round_phase":    rnd["phase"],
		"bomb":           bombState,
		"bomb_countdown": bombCD,
		"phase_ends_in":  pc["phase_ends_in"],
		"win_team":       rnd["win_team"],
		"map_phase":      mp["phase"],
		"round":          mp["round"],
		"weapon":         weapon,
		"ammo_clip":      clip,
		"ammo_reserve":   reserve,
		"kills":          stats["kills"],
		"deaths":         stats["deaths"],
	}
}

// knownFolder resolves a Windows known folder via the shell API. Environment variables like
// %APPDATA% are not always set when Raycast starts the helper.
func knownFolder(id *windows.KNOWNFOLDERID, env string) string {
	if p, err := windows.KnownFolderPath(id, 0); err == nil && p != "" {
		return p
	}
	return os.Getenv(env)
}

// cacertPaths finds SignalRGB's Ultralight trust bundles.
func cacertPaths() []string {
	var paths []string
	for _, base := range []string{
		knownFolder(windows.FOLDERID_LocalAppData, "LOCALAPPDATA"),
		knownFolder(windows.FOLDERID_ProgramFiles, "PROGRAMFILES"),
		knownFolder(windows.FOLDERID_ProgramFilesX86, "PROGRAMFILES(X86)"),
	} {
		if base != "" {
			found, _ := filepath.Glob(filepath.Join(base, "VortxEngine", "app-*", "Signal-x64", "cacert.pem"))
			paths = append(paths, found...)
		}
	}
	return paths
}

// certDERs returns the DER bytes of all certificates in a PEM bundle.
func certDERs(data []byte) [][]byte {
	var out [][]byte
	for {
		var block *pem.Block
		block, data = pem.Decode(data)
		if block == nil {
			return out
		}
		if block.Type == "CERTIFICATE" {
			out = append(out, block.Bytes)
		}
	}
}

// trustedIn lists the bundles that already contain the CA (compared by DER, so line endings don't matter).
func trustedIn(ca []byte, paths []string) []string {
	ders := certDERs(ca)
	if len(ders) == 0 {
		return nil
	}
	var hits []string
	for _, p := range paths {
		data, err := os.ReadFile(p)
		if err != nil {
			continue
		}
		for _, d := range certDERs(data) {
			if bytes.Equal(d, ders[0]) {
				hits = append(hits, p)
				break
			}
		}
	}
	return hits
}

type certSet struct{ chain, key, ca []byte }

func readCerts(dir string) (certSet, bool) {
	ca, e1 := os.ReadFile(filepath.Join(dir, "ca.pem"))
	ch, e2 := os.ReadFile(filepath.Join(dir, "server_chain.pem"))
	k, e3 := os.ReadFile(filepath.Join(dir, "server_key.pem"))
	if errors.Join(e1, e2, e3) != nil {
		return certSet{}, false
	}
	if _, err := tls.X509KeyPair(ch, k); err != nil {
		return certSet{}, false
	}
	return certSet{ch, k, ca}, true
}

func writeCerts(dir string, c certSet) {
	_ = os.MkdirAll(dir, 0o755)
	_ = os.WriteFile(filepath.Join(dir, "ca.pem"), c.ca, 0o644)
	_ = os.WriteFile(filepath.Join(dir, "server_chain.pem"), c.chain, 0o644)
	_ = os.WriteFile(filepath.Join(dir, "server_key.pem"), c.key, 0o600)
}

// ensureCerts picks the certificates for the HTTPS bridge. Preferred is a set whose CA SignalRGB
// already trusts (own ones, or SignalCompanion's), so no SignalRGB restart is needed. Otherwise
// an existing set is used, or a new CA is generated (patchCACert then adds it to SignalRGB).
func ensureCerts(dir string) (chain, key, ca []byte, err error) {
	companion := filepath.Join(knownFolder(windows.FOLDERID_RoamingAppData, "APPDATA"), "SignalCompanion", "certs")
	paths := cacertPaths()
	var first *certSet
	firstDir := ""
	for _, d := range []string{dir, companion} {
		c, ok := readCerts(d)
		if !ok {
			continue
		}
		if len(trustedIn(c.ca, paths)) > 0 {
			if d != dir {
				writeCerts(dir, c)
				logf("cs2: using SignalCompanion certificates (already trusted by SignalRGB)")
			}
			return c.chain, c.key, c.ca, nil
		}
		if first == nil {
			first, firstDir = &c, d
		}
	}
	if first != nil {
		if firstDir != dir {
			writeCerts(dir, *first)
		}
		return first.chain, first.key, first.ca, nil
	}
	ch, k, newCA, err := generateCerts()
	if err != nil {
		return nil, nil, nil, err
	}
	writeCerts(dir, certSet{ch, k, newCA})
	logf("cs2: generated new local CA")
	return ch, k, newCA, nil
}

func generateCerts() (chain, key, ca []byte, err error) {
	serial := func() *big.Int { n, _ := rand.Int(rand.Reader, new(big.Int).Lsh(big.NewInt(1), 120)); return n }
	now := time.Now()
	caKey, err := rsa.GenerateKey(rand.Reader, 2048)
	if err != nil {
		return
	}
	caSKI := sha1.Sum(x509.MarshalPKCS1PublicKey(&caKey.PublicKey))
	caTpl := &x509.Certificate{
		SerialNumber: serial(), Subject: pkix.Name{CommonName: "SignalCompanion CS2 Local CA"},
		NotBefore: now.Add(-24 * time.Hour), NotAfter: now.AddDate(10, 0, 0),
		IsCA: true, BasicConstraintsValid: true, KeyUsage: x509.KeyUsageCertSign | x509.KeyUsageCRLSign | x509.KeyUsageDigitalSignature,
		SubjectKeyId: caSKI[:],
	}
	caDER, err := x509.CreateCertificate(rand.Reader, caTpl, caTpl, &caKey.PublicKey, caKey)
	if err != nil {
		return
	}
	caCert, _ := x509.ParseCertificate(caDER)
	srvKey, err := rsa.GenerateKey(rand.Reader, 2048)
	if err != nil {
		return
	}
	srvTpl := &x509.Certificate{
		SerialNumber: serial(), Subject: pkix.Name{CommonName: "127.0.0.1"},
		NotBefore: now.Add(-24 * time.Hour), NotAfter: now.AddDate(10, 0, 0),
		BasicConstraintsValid: true, ExtKeyUsage: []x509.ExtKeyUsage{x509.ExtKeyUsageServerAuth},
		KeyUsage:    x509.KeyUsageDigitalSignature | x509.KeyUsageKeyEncipherment,
		IPAddresses: []net.IP{net.ParseIP("127.0.0.1")}, DNSNames: []string{"localhost"},
	}
	srvDER, err := x509.CreateCertificate(rand.Reader, srvTpl, caCert, &srvKey.PublicKey, caKey)
	if err != nil {
		return
	}
	ca = pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: caDER})
	chain = append(pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: srvDER}), ca...)
	key = pem.EncodeToMemory(&pem.Block{Type: "RSA PRIVATE KEY", Bytes: x509.MarshalPKCS1PrivateKey(srvKey)})
	return chain, key, ca, nil
}

// patchCACert appends the CA to every SignalRGB (Ultralight) cacert.pem, like
// SignalCompanion did: rebuilt from a .bak each time, so SignalRGB updates get
// re-trusted. Returns the trusted files and whether a file was changed.
func patchCACert(ca []byte) (trusted []string, changed bool) {
	trusted = []string{}
	paths := cacertPaths()
	if len(paths) == 0 {
		logf("cs2: no SignalRGB cacert.pem found, the effect can't trust the HTTPS bridge")
		return trusted, false
	}
	already := map[string]bool{}
	for _, p := range trustedIn(ca, paths) {
		already[p] = true
	}
	block := append([]byte(caMarker), ca...)
	for _, p := range paths {
		if already[p] {
			trusted = append(trusted, p)
			continue
		}
		// Rebuild from the original bundle so older appended CAs don't pile up.
		bak := p + ".signalcompanion.bak"
		base, err := os.ReadFile(bak)
		if err != nil {
			if base, err = os.ReadFile(p); err != nil {
				continue
			}
			_ = os.WriteFile(bak, base, 0o644)
		}
		if err := os.WriteFile(p, append(append([]byte{}, base...), block...), 0o644); err != nil {
			logf("cs2: could not patch %s: %v", p, err)
			continue
		}
		logf("cs2: added local CA to %s", p)
		trusted = append(trusted, p)
		changed = true
	}
	return trusted, changed
}

func processRunning(name string) bool {
	snap, err := windows.CreateToolhelp32Snapshot(windows.TH32CS_SNAPPROCESS, 0)
	if err != nil {
		return false
	}
	defer windows.CloseHandle(snap)
	var e windows.ProcessEntry32
	e.Size = uint32(unsafe.Sizeof(e))
	for err = windows.Process32First(snap, &e); err == nil; err = windows.Process32Next(snap, &e) {
		if strings.EqualFold(windows.UTF16ToString(e.ExeFile[:]), name) {
			return true
		}
	}
	return false
}
