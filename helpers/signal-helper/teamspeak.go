package main

import (
	"context"
	"encoding/json"
	"errors"
	"os"
	"path/filepath"
	"sync"
	"time"

	"github.com/coder/websocket"
)

// TeamSpeak 5/6 "Remote Apps" API (ws://127.0.0.1:5899, see docs/Remote Apps.md in the
// TeamSpeak install folder). The client must approve the app once; the returned API key is
// stored in the data folder. (The bundled docs say "keyPress", the client actually expects
// "buttonPress".) TeamSpeak can't be muted directly through this API, only via a
// "remote key press" that the user binds to "Toggle Microphone Mute" in TeamSpeak. We know
// TeamSpeak's own mute state from its events (inputMuted), so we only press when it differs.

const (
	tsURL        = "ws://127.0.0.1:5899"
	tsIdentifier = "net.mendyka.signalrgb.companion"
	tsButton     = "signalrgb.mic.toggle"
)

type TeamSpeakStatus struct {
	Enabled bool `json:"enabled"`
	// offline (TeamSpeak not running), approval (waiting for "Allow" in TeamSpeak), connected
	State       string     `json:"state"`
	Servers     int        `json:"servers"`
	InputMuted  *bool      `json:"inputMuted"`
	Nickname    string     `json:"nickname,omitempty"`
	LastError   string     `json:"lastError,omitempty"`
	KeyBound    *bool      `json:"keyBound"`             // nil = unknown, false = the key doesn't toggle mute properly
	KeyProblem  string     `json:"keyProblem,omitempty"` // "no-effect" or "switches-back"
	LastPressAt *time.Time `json:"lastPressAt,omitempty"`
}

type tsConnection struct {
	clientID string
	muted    *bool
	nickname string
}

type TeamSpeak struct {
	app     *App
	mu      sync.Mutex
	st      TeamSpeakStatus
	conns   map[string]*tsConnection // by connectionId
	current string
	conn    *websocket.Conn
	pending *bool // desired state of a press still waiting for TeamSpeak's event
	seen    map[string]bool
}

func newTeamSpeak(app *App) *TeamSpeak {
	t := &TeamSpeak{app: app, st: TeamSpeakStatus{State: "offline"}, conns: map[string]*tsConnection{}, seen: map[string]bool{}}
	if t.loadStore().KeyBound {
		t.st.KeyBound = boolPtr(true) // confirmed earlier, survives restarts
	}
	return t
}

func (t *TeamSpeak) keyFile() string { return filepath.Join(t.app.dataDir, "teamspeak.json") }

type tsStore struct {
	APIKey   string `json:"apiKey"`
	KeyBound bool   `json:"keyBound"`
}

func (t *TeamSpeak) loadStore() tsStore {
	var v tsStore
	if data, err := os.ReadFile(t.keyFile()); err == nil {
		_ = json.Unmarshal(data, &v)
	}
	return v
}

func (t *TeamSpeak) writeStore(v tsStore) {
	data, _ := json.Marshal(v)
	_ = os.WriteFile(t.keyFile(), data, 0o600)
}

func (t *TeamSpeak) loadKey() string { return t.loadStore().APIKey }

func (t *TeamSpeak) saveKey(key string) {
	v := t.loadStore()
	v.APIKey = key
	if key == "" {
		v.KeyBound = false // a new approval starts the setup over
	}
	t.writeStore(v)
}

func (t *TeamSpeak) saveKeyBound(bound bool) {
	v := t.loadStore()
	if v.KeyBound != bound {
		v.KeyBound = bound
		t.writeStore(v)
	}
}

func (t *TeamSpeak) Status() TeamSpeakStatus {
	t.mu.Lock()
	defer t.mu.Unlock()
	st := t.st
	st.Enabled = t.app.Config().TeamSpeak.Enabled
	st.Servers = len(t.conns)
	if c := t.conns[t.current]; c != nil {
		st.InputMuted, st.Nickname = c.muted, c.nickname
	} else {
		for _, c := range t.conns {
			st.InputMuted, st.Nickname = c.muted, c.nickname
			break
		}
	}
	return st
}

func (t *TeamSpeak) setState(state, errText string) {
	t.mu.Lock()
	defer t.mu.Unlock()
	if t.st.State != state {
		logf("teamspeak: %s", state)
	}
	t.st.State, t.st.LastError = state, errText
}

// Start keeps a connection to TeamSpeak while it runs (it is retried every few seconds).
func (t *TeamSpeak) Start() {
	go func() {
		for {
			if t.app.Config().TeamSpeak.Enabled && processRunning("TeamSpeak.exe") {
				if err := t.session(); err != nil {
					t.setState("offline", err.Error())
				}
			} else {
				t.setState("offline", "")
			}
			time.Sleep(5 * time.Second)
		}
	}()
}

type tsMessage struct {
	Type    string          `json:"type"`
	Payload json.RawMessage `json:"payload"`
	Status  *struct {
		Code    int    `json:"code"`
		Message string `json:"message"`
	} `json:"status"`
}

func (t *TeamSpeak) session() error {
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	dialCtx, dialCancel := context.WithTimeout(ctx, 5*time.Second)
	conn, _, err := websocket.Dial(dialCtx, tsURL, nil)
	dialCancel()
	if err != nil {
		return errors.New("Remote Apps not reachable (TeamSpeak → Settings → Remote Apps enabled?)")
	}
	conn.SetReadLimit(64 << 20)
	defer conn.CloseNow()

	key := t.loadKey()
	auth, _ := json.Marshal(map[string]any{"type": "auth", "payload": map[string]any{
		"identifier":  tsIdentifier,
		"version":     version,
		"name":        "SignalRGB Companion",
		"description": "Mutes TeamSpeak together with the headset mute button",
		"content":     map[string]string{"apiKey": key},
	}})
	if err := conn.Write(ctx, websocket.MessageText, auth); err != nil {
		return err
	}
	t.setState("approval", "")
	t.mu.Lock()
	t.conn = conn
	t.mu.Unlock()
	defer func() {
		t.mu.Lock()
		t.conn, t.conns, t.current = nil, map[string]*tsConnection{}, ""
		t.mu.Unlock()
	}()

	for {
		if !t.app.Config().TeamSpeak.Enabled {
			return nil
		}
		_, data, err := conn.Read(ctx)
		if err != nil {
			return errors.New("connection to TeamSpeak closed")
		}
		var msg tsMessage
		if json.Unmarshal(data, &msg) != nil {
			continue
		}
		switch msg.Type {
		case "auth":
			if msg.Status != nil && msg.Status.Code != 0 {
				// Key revoked or invalid: forget it, the next attempt asks for approval again.
				t.saveKey("")
				return errors.New("TeamSpeak refused the app: " + msg.Status.Message)
			}
			t.onAuth(msg.Payload)
		case "clientSelfPropertyUpdated":
			t.onSelfProperty(msg.Payload)
		case "talkStatusChanged", "proto.ClientEvent":
			// noisy, not needed
		default:
			t.mu.Lock()
			first := !t.seen[msg.Type]
			t.seen[msg.Type] = true
			t.mu.Unlock()
			if first {
				logf("teamspeak: event %s %s", msg.Type, truncate(string(data), 300))
			}
			// Connections opened or closed later: the simplest way to stay correct is to
			// re-authenticate, which sends the full state again.
			if msg.Type == "connectStatusChanged" || msg.Type == "serverPropertiesUpdated" {
				_ = conn.Write(ctx, websocket.MessageText, auth)
			}
		}
	}
}

func truncate(s string, n int) string {
	if len(s) > n {
		return s[:n] + "…"
	}
	return s
}

func (t *TeamSpeak) onAuth(raw json.RawMessage) {
	var p struct {
		APIKey              string `json:"apiKey"`
		CurrentConnectionID any    `json:"currentConnectionId"`
		Connections         []struct {
			ID          any `json:"id"`
			ClientID    any `json:"clientId"`
			ClientInfos []struct {
				ID         any `json:"id"`
				Properties struct {
					InputMuted bool   `json:"inputMuted"`
					Nickname   string `json:"nickname"`
				} `json:"properties"`
			} `json:"clientInfos"`
		} `json:"connections"`
	}
	if json.Unmarshal(raw, &p) != nil {
		return
	}
	if p.APIKey != "" && p.APIKey != t.loadKey() {
		t.saveKey(p.APIKey)
		logf("teamspeak: app approved, key stored")
	}
	conns := map[string]*tsConnection{}
	for _, c := range p.Connections {
		tc := &tsConnection{clientID: sprintf("%v", c.ClientID)}
		for _, ci := range c.ClientInfos {
			if sprintf("%v", ci.ID) == tc.clientID {
				tc.muted, tc.nickname = boolPtr(ci.Properties.InputMuted), ci.Properties.Nickname
			}
		}
		conns[sprintf("%v", c.ID)] = tc
	}
	t.mu.Lock()
	t.conns, t.current = conns, sprintf("%v", p.CurrentConnectionID)
	t.mu.Unlock()
	t.setState("connected", "")
}

func (t *TeamSpeak) onSelfProperty(raw json.RawMessage) {
	var p struct {
		ConnectionID any    `json:"connectionId"`
		Flag         string `json:"flag"`
		NewValue     any    `json:"newValue"`
	}
	if json.Unmarshal(raw, &p) != nil || p.Flag != "inputMuted" {
		return
	}
	muted, _ := p.NewValue.(bool)
	id := sprintf("%v", p.ConnectionID)
	t.mu.Lock()
	c := t.conns[id]
	if c == nil {
		c = &tsConnection{}
		t.conns[id] = c
	}
	c.muted = boolPtr(muted)
	fromUs := t.pending != nil && *t.pending == muted
	// A change right after our own press that we didn't ask for is an echo: TeamSpeak toggled
	// again on key release (hold action such as push-to-mute) or the key is bound twice.
	echo := !fromUs && t.st.LastPressAt != nil && time.Since(*t.st.LastPressAt) < 1500*time.Millisecond
	switch {
	case fromUs:
		t.pending = nil
		t.st.KeyBound, t.st.KeyProblem = boolPtr(true), ""
		go t.saveKeyBound(true)
	case echo:
		t.pending = nil
		t.st.KeyBound, t.st.KeyProblem = boolPtr(false), "switches-back"
		go t.saveKeyBound(false)
	}
	t.mu.Unlock()
	logf("teamspeak: microphone %s", muteWord(muted))
	if echo {
		logf("teamspeak: switched back right after our key press: bind the key only once and only to 'Toggle Microphone Mute' (not a hold action); ignoring it")
		return // never let it undo the headset/Windows mute
	}

	// Muted in TeamSpeak itself: follow with Windows and the headset.
	cfg := t.app.Config()
	if !fromUs && cfg.TeamSpeak.Enabled && cfg.TeamSpeak.FollowTeamSpeak {
		if info, err := t.app.audio.Mic(); err == nil && info.Muted != muted {
			_ = t.app.audio.SetMicMute(muted) // the Windows watcher mirrors it to the headset
		}
	}
}

// SetMuted brings TeamSpeak to the given state with the bound toggle key, if it differs.
func (t *TeamSpeak) SetMuted(muted bool) {
	if !t.app.Config().TeamSpeak.Enabled {
		return
	}
	st := t.Status()
	if st.State != "connected" || st.InputMuted == nil || *st.InputMuted == muted {
		return
	}
	if st.KeyBound != nil && !*st.KeyBound {
		return // misbound key: pressing it would only flip TeamSpeak back and forth; fix via the setup's Test
	}
	// The headset button and the Windows mute both trigger this; pressExpecting refuses a
	// second press while TeamSpeak hasn't confirmed the first one.
	if err := t.pressExpecting(muted); err != nil && !errors.Is(err, errPressPending) {
		logf("teamspeak: key press failed: %v", err)
	}
}

// Test presses the key once and reports via KeyBound whether TeamSpeak toggled its mute.
func (t *TeamSpeak) Test() error {
	st := t.Status()
	if st.State != "connected" || st.InputMuted == nil {
		return errors.New("TeamSpeak is not connected to a server")
	}
	t.mu.Lock()
	t.st.KeyBound, t.st.KeyProblem = nil, ""
	t.mu.Unlock()
	return t.pressExpecting(!*st.InputMuted)
}

// pressExpecting presses the key and expects TeamSpeak to report the target state within 2 s;
// otherwise the key is marked as not bound to "Toggle Microphone Mute".
var errPressPending = errors.New("a key press is still waiting for TeamSpeak")

func (t *TeamSpeak) pressExpecting(target bool) error {
	t.mu.Lock()
	if t.pending != nil {
		t.mu.Unlock()
		return errPressPending
	}
	t.pending = boolPtr(target)
	t.mu.Unlock()
	if err := t.Press(); err != nil {
		t.mu.Lock()
		t.pending = nil
		t.mu.Unlock()
		return err
	}
	go func() {
		time.Sleep(2 * time.Second)
		t.mu.Lock()
		defer t.mu.Unlock()
		if t.pending != nil && *t.pending == target {
			t.pending = nil
			t.st.KeyBound, t.st.KeyProblem = boolPtr(false), "no-effect"
			go t.saveKeyBound(false)
			logf("teamspeak: key press had no effect, bind it to 'Toggle Microphone Mute' in TeamSpeak")
		}
	}()
	return nil
}

// Press sends one press of the remote key (down and up).
func (t *TeamSpeak) Press() error {
	t.mu.Lock()
	conn := t.conn
	now := time.Now()
	t.st.LastPressAt = &now
	t.mu.Unlock()
	if conn == nil {
		return errors.New("not connected to TeamSpeak")
	}
	ctx, cancel := context.WithTimeout(context.Background(), 2*time.Second)
	defer cancel()
	for _, down := range []bool{true, false} {
		msg, _ := json.Marshal(map[string]any{"type": "buttonPress", "payload": map[string]any{"button": tsButton, "state": down}})
		if err := conn.Write(ctx, websocket.MessageText, msg); err != nil {
			return err
		}
		time.Sleep(60 * time.Millisecond)
	}
	return nil
}
