// launcher-key turns a short tap of the Windows key into opening Command Palette.
//
// It runs as its own small process (not inside the extension process that hosts all other extensions),
// so a busy or restarted extension process never delays keyboard input or loses the hook.
//
//	launcher-key launch -settings <file> -log <file>   start "run" detached and exit
//	launcher-key run    -settings <file> -log <file>   the hook (single instance)
//	launcher-key stop                                  end a running instance
//
// How the key is handled:
//   - Win down and Win + another key (Win+E, Win+L …) pass through unchanged.
//   - A tap (Win alone, at most -tap ms): the real Win-up is held back and, still inside the hook,
//     "unassigned key 0xE8 down, up, Win up" is sent instead. Windows sees Win + another key and does
//     not open the Start menu; then Command Palette opens (or closes when it is already in front).
//     (Sending the mask key right after Win-down does not stop the Start menu on Windows 11.)
//   - Afterwards the helper checks that Win is really released and sends another Win-up if not, so
//     the key can never stay logically pressed.
//   - Only while Command Palette runs: once it is closed, Win is passed on at once (Start menu, or another
//     launcher like Raycast) and the helper ends; the extension starts it again with Command Palette.
//   - When an elevated window is in front, Windows drops injected keys (UIPI); the helper then leaves
//     the key alone and Win opens the Start menu as usual.
package main

import (
	"encoding/json"
	"flag"
	"fmt"
	"log"
	"os"
	"runtime"
	"strings"
	"sync/atomic"
	"syscall"
	"time"
	"unsafe"

	"golang.org/x/sys/windows"
)

const (
	whKeyboardLl  = 13
	wmKeydown     = 0x0100
	wmKeyup       = 0x0101
	wmSyskeydown  = 0x0104
	wmSyskeyup    = 0x0105
	wmQuit        = 0x0012
	wmTimer       = 0x0113
	wmHotkey      = 0x0312
	llkhfInjected = 0x10
	vkLwin        = 0x5B
	vkRwin        = 0x5C
	vkMask        = 0xE8 // unassigned
	inputKeyboard = 1
	keyeventfUp   = 0x0002
	keyeventfExt  = 0x0001

	// A key-repeat of a held Win key arrives at least every second; a longer gap means a new press
	// whose release the hook never saw (e.g. Win+L switched to the lock screen).
	repeatGap = 1100

	paletteAppID = "Microsoft.CommandPalette_8wekyb3d8bbwe!App"
	mutexName    = `Local\Delido.PowerToysExtensions.LauncherKey`
	stopName     = `Local\Delido.PowerToysExtensions.LauncherKey.Stop`
)

var (
	user32                  = windows.NewLazySystemDLL("user32.dll")
	ole32                   = windows.NewLazySystemDLL("ole32.dll")
	procSetWindowsHookExW   = user32.NewProc("SetWindowsHookExW")
	procUnhookWindowsHookEx = user32.NewProc("UnhookWindowsHookEx")
	procCallNextHookEx      = user32.NewProc("CallNextHookEx")
	procGetMessageW         = user32.NewProc("GetMessageW")
	procPostThreadMessageW  = user32.NewProc("PostThreadMessageW")
	procPostMessageW        = user32.NewProc("PostMessageW")
	procSendInput           = user32.NewProc("SendInput")
	procGetForegroundWindow = user32.NewProc("GetForegroundWindow")
	procGetWindowThreadPid  = user32.NewProc("GetWindowThreadProcessId")
	procSetTimer            = user32.NewProc("SetTimer")
	procGetAsyncKeyState    = user32.NewProc("GetAsyncKeyState")
	procCoCreateInstance    = ole32.NewProc("CoCreateInstance")
	clsidActivationManager  = windows.GUID{Data1: 0x45BA127D, Data2: 0x10A8, Data3: 0x46EA, Data4: [8]byte{0x8A, 0xB7, 0x56, 0xEA, 0x90, 0x78, 0x94, 0x3C}}
	iidActivationManager    = windows.GUID{Data1: 0x2e941141, Data2: 0x7f97, Data3: 0x4756, Data4: [8]byte{0xba, 0x1d, 0x9d, 0xec, 0xde, 0x89, 0x4a, 0x3d}}
	hookProc                = windows.NewCallback(onKey)
	hook                    uintptr
	hookThread              uint32
	tapMs                   uint32 = 400
	winDown, other          bool
	paletteRunning          atomic.Bool
	downTime, lastWinTime   uint32
	opener                  = make(chan uint16, 4)
	logger                  = log.New(os.Stderr, "", log.LstdFlags)
)

type kbdLlHook struct {
	Vk, Scan, Flags, Time uint32
	ExtraInfo             uintptr
}

type keyboardInput struct {
	Type      uint32
	_         uint32
	Vk, Scan  uint16
	Flags     uint32
	Time      uint32
	_         uint32
	ExtraInfo uintptr
	_         [8]byte // INPUT is as large as its biggest member (MOUSEINPUT)
}

type msg struct {
	Hwnd    uintptr
	Message uint32
	WParam  uintptr
	LParam  uintptr
	Time    uint32
	X, Y    int32
	_       uint32
}

func onKey(code int, wParam uintptr, k *kbdLlHook) uintptr {
	if code >= 0 && k.Flags&llkhfInjected == 0 && handle(k, uint32(wParam)) {
		return 1 // swallowed
	}
	r, _, _ := procCallNextHookEx.Call(0, uintptr(code), wParam, uintptr(unsafe.Pointer(k)))
	return r
}

// handle returns true when the event is swallowed.
func handle(k *kbdLlHook, message uint32) bool {
	down := message == wmKeydown || message == wmSyskeydown
	up := message == wmKeyup || message == wmSyskeyup
	if k.Vk != vkLwin && k.Vk != vkRwin {
		if down && winDown {
			other = true
		}
		return false
	}

	if down {
		if !winDown || k.Time-lastWinTime > repeatGap {
			winDown, other = true, false
			downTime = k.Time
		}
		lastWinTime = k.Time
		return false
	}

	if !up || !winDown {
		return false
	}
	winDown = false
	if other || k.Time-downTime > tapMs || !paletteRunning.Load() {
		return false
	}
	if foregroundElevated() {
		logger.Printf("tap left alone: elevated window in front")
		return false
	}

	// queued right here in the hook, before anything the user types next
	inputs := [3]keyboardInput{
		{Type: inputKeyboard, Vk: vkMask},
		{Type: inputKeyboard, Vk: vkMask, Flags: keyeventfUp},
		{Type: inputKeyboard, Vk: uint16(k.Vk), Flags: keyeventfUp | keyeventfExt},
	}
	if sent, _, _ := procSendInput.Call(3, uintptr(unsafe.Pointer(&inputs[0])), unsafe.Sizeof(inputs[0])); sent != 3 {
		logger.Printf("SendInput sent %d of 3, letting the real Win-up through", sent)
		return false
	}
	select {
	case opener <- uint16(k.Vk):
	default:
	}
	return true
}

// ensureReleased sends another Win-up should Windows still see Win as pressed after a tap
// (an injected key can be dropped, e.g. when an elevated window comes to the front meanwhile).
func ensureReleased(vk uint16) {
	time.Sleep(60 * time.Millisecond)
	if state, _, _ := procGetAsyncKeyState.Call(uintptr(vk)); state&0x8000 == 0 {
		return
	}
	logger.Printf("Win still pressed after a tap, releasing it")
	inputs := [1]keyboardInput{{Type: inputKeyboard, Vk: vk, Flags: keyeventfUp | keyeventfExt}}
	procSendInput.Call(1, uintptr(unsafe.Pointer(&inputs[0])), unsafe.Sizeof(inputs[0]))
}

func foregroundPid() uint32 {
	w, _, _ := procGetForegroundWindow.Call()
	if w == 0 {
		return 0
	}
	var pid uint32
	procGetWindowThreadPid.Call(w, uintptr(unsafe.Pointer(&pid)))
	return pid
}

// foregroundElevated: a process whose token we may not read is elevated (or otherwise above us).
func foregroundElevated() bool {
	pid := foregroundPid()
	if pid == 0 {
		return false
	}
	p, err := windows.OpenProcess(windows.PROCESS_QUERY_LIMITED_INFORMATION, false, pid)
	if err != nil {
		return true
	}
	defer windows.CloseHandle(p)
	var token windows.Token
	if windows.OpenProcessToken(p, windows.TOKEN_QUERY, &token) != nil {
		return true
	}
	defer token.Close()
	return token.IsElevated() && !windows.GetCurrentProcessToken().IsElevated()
}

func paletteInForeground() (uintptr, bool) {
	w, _, _ := procGetForegroundWindow.Call()
	if w == 0 {
		return 0, false
	}
	var pid uint32
	procGetWindowThreadPid.Call(w, uintptr(unsafe.Pointer(&pid)))
	p, err := windows.OpenProcess(windows.PROCESS_QUERY_LIMITED_INFORMATION, false, pid)
	if err != nil {
		return 0, false
	}
	defer windows.CloseHandle(p)
	buf := make([]uint16, windows.MAX_PATH)
	size := uint32(len(buf))
	if windows.QueryFullProcessImageName(p, 0, &buf[0], &size) != nil {
		return 0, false
	}
	return w, strings.EqualFold(baseName(windows.UTF16ToString(buf[:size])), "Microsoft.CmdPal.UI.exe")
}

func baseName(path string) string {
	if i := strings.LastIndexAny(path, `\/`); i >= 0 {
		return path[i+1:]
	}
	return path
}

// runOpener opens or closes the palette off the hook thread, so the hook always returns at once.
func runOpener() {
	runtime.LockOSThread()
	windows.CoInitializeEx(0, windows.COINIT_MULTITHREADED)
	appID, _ := windows.UTF16PtrFromString(paletteAppID)
	for vk := range opener {
		go ensureReleased(vk) // checked alongside, so opening the palette is not delayed
		// Like Raycast, a second tap closes the palette: it toggles on its own root hotkey (index 0).
		if w, ok := paletteInForeground(); ok {
			procPostMessageW.Call(w, wmHotkey, 0, 0)
			continue
		}
		if err := activate(appID); err != nil {
			logger.Printf("activating Command Palette failed: %v", err)
		}
	}
}

// activate starts Command Palette like a second launch, which makes it show its window.
func activate(appID *uint16) error {
	var manager *struct{ vtbl *[6]uintptr }
	const clsctxAll = 0x17
	hr, _, _ := procCoCreateInstance.Call(uintptr(unsafe.Pointer(&clsidActivationManager)), 0, clsctxAll, uintptr(unsafe.Pointer(&iidActivationManager)), uintptr(unsafe.Pointer(&manager)))
	if hr != 0 {
		return fmt.Errorf("CoCreateInstance 0x%x", uint32(hr))
	}
	defer syscall.SyscallN(manager.vtbl[2], uintptr(unsafe.Pointer(manager))) // Release
	var pid uint32
	hr, _, _ = syscall.SyscallN(manager.vtbl[3], uintptr(unsafe.Pointer(manager)), uintptr(unsafe.Pointer(appID)), 0, 0, uintptr(unsafe.Pointer(&pid)))
	if hr != 0 {
		return fmt.Errorf("ActivateApplication 0x%x", uint32(hr))
	}
	return nil
}

func installHook() uintptr {
	h, _, err := procSetWindowsHookExW.Call(whKeyboardLl, hookProc, 0, 0)
	if h == 0 {
		logger.Printf("SetWindowsHookEx failed: %v", err)
	}
	return h
}

// watchSettings ends the helper when the extension setting is switched off, even if the extension
// process itself is not running.
func watchSettings(path string) {
	for range time.Tick(3 * time.Second) {
		data, err := os.ReadFile(path)
		if err != nil {
			continue
		}
		var s map[string]any
		if json.Unmarshal(data, &s) != nil {
			continue
		}
		if v := fmt.Sprint(s["enabled"]); v != "true" {
			logger.Printf("disabled in the settings")
			quit()
			return
		}
	}
}

// paletteProcessRunning: is Microsoft.CmdPal.UI.exe running at all?
func paletteProcessRunning() bool {
	snapshot, err := windows.CreateToolhelp32Snapshot(windows.TH32CS_SNAPPROCESS, 0)
	if err != nil {
		return true // unknown: keep working
	}
	defer windows.CloseHandle(snapshot)
	var entry windows.ProcessEntry32
	entry.Size = uint32(unsafe.Sizeof(entry))
	for err = windows.Process32First(snapshot, &entry); err == nil; err = windows.Process32Next(snapshot, &entry) {
		if strings.EqualFold(windows.UTF16ToString(entry.ExeFile[:]), "Microsoft.CmdPal.UI.exe") {
			return true
		}
	}
	return false
}

// watchPalette: the Windows key belongs to Command Palette only while it runs. Once it is closed,
// Win is passed on at once (to the Start menu or another launcher such as Raycast) and the helper
// ends a few seconds later; the extension starts it again with the next Command Palette.
func watchPalette() {
	missing := 0
	for range time.Tick(time.Second) {
		running := paletteProcessRunning()
		paletteRunning.Store(running)
		if running {
			missing = 0
			continue
		}
		if missing++; missing >= 5 {
			logger.Printf("Command Palette is not running anymore")
			quit()
			return
		}
	}
}

func quit() { procPostThreadMessageW.Call(uintptr(hookThread), wmQuit, 0, 0) }

func run(settings string) {
	mutex, err := windows.CreateMutex(nil, false, windows.StringToUTF16Ptr(mutexName))
	if err == windows.ERROR_ALREADY_EXISTS {
		return // already running
	}
	defer windows.CloseHandle(mutex)

	stop, _ := windows.CreateEvent(nil, 1, 0, windows.StringToUTF16Ptr(stopName))
	windows.ResetEvent(stop)

	runtime.LockOSThread()
	hookThread = windows.GetCurrentThreadId()
	go runOpener()
	go func() {
		windows.WaitForSingleObject(stop, windows.INFINITE)
		logger.Printf("stop requested")
		quit()
	}()
	if settings != "" {
		go watchSettings(settings)
	}
	paletteRunning.Store(true)
	go watchPalette()

	if hook = installHook(); hook == 0 {
		os.Exit(1)
	}
	logger.Printf("hook installed (pid %d)", os.Getpid())

	// Windows silently removes a low-level hook that ever answers too slowly; installing it again
	// every few minutes makes sure it is always there.
	procSetTimer.Call(0, 0, 5*60*1000, 0)
	var m msg
	for {
		r, _, _ := procGetMessageW.Call(uintptr(unsafe.Pointer(&m)), 0, 0, 0)
		if int32(r) <= 0 {
			break
		}
		if m.Message == wmTimer {
			if fresh := installHook(); fresh != 0 {
				procUnhookWindowsHookEx.Call(hook)
				hook = fresh
			}
		}
	}
	procUnhookWindowsHookEx.Call(hook)
	logger.Printf("hook removed")
}

// launch starts "run" detached and outside the job of the caller, so it outlives the extension process.
func launch(args []string) {
	exe, _ := os.Executable()
	cmd, _ := windows.UTF16PtrFromString(windows.ComposeCommandLine(append([]string{exe, "run"}, args...)))
	var si windows.StartupInfo
	si.Cb = uint32(unsafe.Sizeof(si))
	var pi windows.ProcessInformation
	const breakaway, detached, newGroup = 0x01000000, 0x00000008, 0x00000200
	err := windows.CreateProcess(nil, cmd, nil, nil, false, breakaway|detached|newGroup, nil, nil, &si, &pi)
	if err != nil {
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
		fmt.Println("usage: launcher-key run|launch|stop [-settings <file>] [-log <file>] [-tap <ms>]")
		os.Exit(2)
	}
	mode := os.Args[1]
	switch mode {
	case "launch":
		launch(os.Args[2:])
		return
	case "stop":
		if e, err := windows.OpenEvent(windows.EVENT_MODIFY_STATE, false, windows.StringToUTF16Ptr(stopName)); err == nil {
			windows.SetEvent(e)
			windows.CloseHandle(e)
		}
		return
	}

	fs := flag.NewFlagSet(mode, flag.ExitOnError)
	settings := fs.String("settings", "", "settings.json of the extension (helper ends when disabled)")
	logFile := fs.String("log", "", "log file")
	tap := fs.Uint("tap", 400, "longest press in ms that counts as a tap")
	fs.Parse(os.Args[2:])
	tapMs = uint32(*tap)
	if *logFile != "" {
		if f, err := os.OpenFile(*logFile, os.O_CREATE|os.O_APPEND|os.O_WRONLY, 0o644); err == nil {
			logger.SetOutput(f)
		}
	}
	run(*settings)
}
