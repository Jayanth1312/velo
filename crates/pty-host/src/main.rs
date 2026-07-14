//! velo-pty-host: detached process that owns ConPTY sessions so shells (and
//! whatever runs in them) survive the Velo UI closing. The UI talks to it over
//! named pipes and reattaches on the next launch.
//!
//! Pipes:
//! - `\\.\pipe\velo-pty-host-ctl` — one request per connection:
//!     [1] Spawn  {cols u16, rows u16, len u32, cmd utf8} -> id u32 (MAX = fail)
//!     [3] Resize {id u32, cols u16, rows u16}            -> u8 1
//!     [4] Kill   {id u32}                                -> u8 1
//!     [5] List   {}                                      -> count u32, id u32 × count
//! - `\\.\pipe\velo-pty-out-<id>` — host → client: ring-buffer replay, then live
//!   output. Recreated after each disconnect so a future UI can reattach.
//! - `\\.\pipe\velo-pty-in-<id>`  — client → host: raw stdin bytes.
//!
//! The process exits when its last session ends. A second instance exits
//! immediately (the ctl pipe acts as the single-instance lock).

#[cfg(not(windows))]
fn main() {}

#[cfg(windows)]
fn main() {
    imp::run();
}

#[cfg(windows)]
mod imp {
    use std::collections::{HashMap, VecDeque};
    use std::fs::File;
    use std::io::{Read, Write};
    use std::os::windows::io::FromRawHandle;
    use std::sync::atomic::{AtomicBool, AtomicU32, Ordering};
    use std::sync::{Arc, Condvar, Mutex};
    use std::time::Duration;

    use windows::core::HSTRING;
    use windows::Win32::Foundation::{CloseHandle, HANDLE, INVALID_HANDLE_VALUE};
    use windows::Win32::Storage::FileSystem::PIPE_ACCESS_DUPLEX;
    use windows::Win32::System::Pipes::{
        ConnectNamedPipe, CreateNamedPipeW, DisconnectNamedPipe, PeekNamedPipe,
        PIPE_READMODE_BYTE, PIPE_TYPE_BYTE, PIPE_WAIT,
    };

    /// Replay buffer cap per session (screen + recent scrollback approximation).
    const RING_CAP: usize = 256 * 1024;

    /// Append-only debug log at %TEMP%\velo-pty-host.log.
    fn hlog(msg: &str) {
        if let Ok(mut f) = std::fs::OpenOptions::new()
            .create(true)
            .append(true)
            .open(std::env::temp_dir().join("velo-pty-host.log"))
        {
            let _ = writeln!(f, "[{:?}] {msg}", std::time::SystemTime::now());
        }
    }

    /// Output state shared between the PTY reader and the out-pipe thread.
    /// One mutex for both buffers so replay + live handoff has no gap or dupe.
    struct OutBuf {
        ring: VecDeque<u8>, // everything recent (replayed to a new client)
        live: Vec<u8>,      // pending bytes for the currently attached client
        eof: bool,
    }

    struct Session {
        _pty: pty_win::Pty, // owns the child; dropping kills it
        buf: Arc<(Mutex<OutBuf>, Condvar)>,
        alive: Arc<AtomicBool>,
    }

    type Sessions = Arc<Mutex<HashMap<u32, Session>>>;

    pub fn run() {
        // Single-instance lock: whoever owns the ctl pipe is the host.
        let ctl = match pipe_instance("velo-pty-host-ctl") {
            Some(h) => h,
            None => return, // another host is running
        };
        let sessions: Sessions = Arc::new(Mutex::new(HashMap::new()));
        let next_id = AtomicU32::new(1);
        let mut ctl = ctl;
        loop {
            if unsafe { ConnectNamedPipe(ctl.0, None) }.is_err() {
                // Client may have connected between create and connect; that
                // surfaces as ERROR_PIPE_CONNECTED which `windows` maps to Err.
            }
            let mut f = ctl.into_file();
            handle_ctl(&mut f, &sessions, &next_id);
            // FlushFileBuffers blocks until the client has read the reply;
            // disconnecting earlier can throw the buffered bytes away.
            let _ = f.sync_all();
            disconnect(&f);
            drop(f);
            ctl = match pipe_instance("velo-pty-host-ctl") {
                Some(h) => h,
                None => return,
            };
        }
    }

    /// DisconnectNamedPipe on a File-wrapped pipe handle (the File still owns
    /// and closes the handle afterwards).
    fn disconnect(f: &File) {
        let h = HANDLE(std::os::windows::io::AsRawHandle::as_raw_handle(f) as _);
        unsafe { let _ = DisconnectNamedPipe(h); }
    }

    /// One request per connection.
    fn handle_ctl(f: &mut File, sessions: &Sessions, next_id: &AtomicU32) {
        let mut op = [0u8; 1];
        if f.read_exact(&mut op).is_err() {
            return;
        }
        hlog(&format!("ctl op={} pid={}", op[0], std::process::id()));
        match op[0] {
            1 => {
                // Spawn
                let (Ok(cols), Ok(rows), Ok(len)) = (read_u16(f), read_u16(f), read_u32(f))
                else { return };
                let mut cmd = vec![0u8; len as usize];
                if f.read_exact(&mut cmd).is_err() {
                    return;
                }
                let cmd = String::from_utf8_lossy(&cmd).into_owned();
                let id = next_id.fetch_add(1, Ordering::Relaxed);
                let ok = spawn_session(id, &cmd, cols, rows, sessions);
                let _ = f.write_all(&(if ok { id } else { u32::MAX }).to_le_bytes());
            }
            3 => {
                // Resize (via the pty handle; safe from any thread)
                let (Ok(id), Ok(cols), Ok(rows)) = (read_u32(f), read_u16(f), read_u16(f))
                else { return };
                if let Some(s) = sessions.lock().unwrap().get(&id) {
                    let _ = s._pty.resize(cols, rows);
                }
                let _ = f.write_all(&[1u8]);
            }
            4 => {
                // Kill: dropping the Session drops the Pty which kills the child.
                // Remove FIRST and drop outside the lock: Pty::drop joins the
                // reader thread, and the reader's Eof handler takes this lock.
                let Ok(id) = read_u32(f) else { return };
                let sess = sessions.lock().unwrap().remove(&id);
                if let Some(s) = sess {
                    s.alive.store(false, Ordering::Release);
                    s.buf.1.notify_all();
                    drop(s);
                }
                let _ = f.write_all(&[1u8]);
                exit_if_empty(sessions);
            }
            5 => {
                // List
                let ids: Vec<u32> = sessions.lock().unwrap().keys().copied().collect();
                hlog(&format!("list -> {ids:?}"));
                let _ = f.write_all(&(ids.len() as u32).to_le_bytes());
                for id in ids {
                    let _ = f.write_all(&id.to_le_bytes());
                }
            }
            _ => {}
        }
    }

    fn spawn_session(id: u32, cmd: &str, cols: u16, rows: u16, sessions: &Sessions) -> bool {
        let buf = Arc::new((
            Mutex::new(OutBuf { ring: VecDeque::new(), live: Vec::new(), eof: false }),
            Condvar::new(),
        ));
        let alive = Arc::new(AtomicBool::new(true));

        let ev_buf = buf.clone();
        let ev_alive = alive.clone();
        let ev_sessions = sessions.clone();
        let on_event = move |ev: pty_win::PtyEvent| match ev {
            pty_win::PtyEvent::Data(b) => {
                let mut g = ev_buf.0.lock().unwrap();
                g.live.extend_from_slice(&b);
                g.ring.extend(b.iter().copied());
                while g.ring.len() > RING_CAP {
                    g.ring.pop_front();
                }
                drop(g);
                ev_buf.1.notify_all();
            }
            pty_win::PtyEvent::Eof => {
                ev_buf.0.lock().unwrap().eof = true;
                ev_alive.store(false, Ordering::Release);
                ev_buf.1.notify_all();
                // Removal drops the Session -> Pty::drop joins the READER
                // thread — which is the thread running this handler. Defer to
                // a helper thread so we never self-join.
                let sessions = ev_sessions.clone();
                std::thread::spawn(move || {
                    let sess = sessions.lock().unwrap().remove(&id);
                    drop(sess);
                    exit_if_empty(&sessions);
                });
            }
        };

        let pty = match pty_win::spawn(cmd, cols, rows, on_event) {
            Ok(p) => p,
            Err(_) => return false,
        };

        // Data pipe servers must exist before the Spawn reply goes out, or the
        // client's immediate connect would race them.
        out_thread(id, buf.clone(), alive.clone());
        in_thread(id, pty.writer(), alive.clone());

        sessions.lock().unwrap().insert(id, Session { _pty: pty, buf, alive });
        true
    }

    /// Host → client output pipe: replay the ring, then stream live bytes.
    /// Recreates the pipe instance after each disconnect for reattach.
    fn out_thread(id: u32, buf: Arc<(Mutex<OutBuf>, Condvar)>, alive: Arc<AtomicBool>) {
        std::thread::spawn(move || {
            let name = format!("velo-pty-out-{id}");
            loop {
                let Some(h) = pipe_instance(&name) else {
                    hlog(&format!("out-{id}: pipe_instance FAILED"));
                    return;
                };
                hlog(&format!("out-{id}: waiting for client"));
                if unsafe { ConnectNamedPipe(h.0, None) }.is_err() { /* already connected */ }
                let mut f = h.into_file();
                // Atomic replay handoff: snapshot ring, drop pending live bytes
                // (they are already inside the ring snapshot).
                let replay: Vec<u8> = {
                    let mut g = buf.0.lock().unwrap();
                    g.live.clear();
                    g.ring.iter().copied().collect()
                };
                hlog(&format!("out-{id}: client connected, replaying {} bytes", replay.len()));
                let mut broken = f.write_all(&replay).is_err();
                if broken {
                    hlog(&format!("out-{id}: replay write FAILED"));
                }
                // NO blocking read may ever park on this handle: synchronous
                // pipe I/O serializes per instance, so a parked reader blocks
                // every write below (the "one chunk then silence" bug). A quiet
                // shell still needs client-gone detection, so on each idle wait
                // timeout poll PeekNamedPipe — it errors once the client closed.
                'client: while !broken {
                    let chunk = {
                        let mut g = buf.0.lock().unwrap();
                        while g.live.is_empty() && !g.eof {
                            let (ng, t) = buf
                                .1
                                .wait_timeout(g, Duration::from_millis(500))
                                .unwrap();
                            g = ng;
                            if t.timed_out() && client_gone(&f) {
                                break 'client;
                            }
                        }
                        if g.live.is_empty() && g.eof {
                            return; // session over: closing the pipe EOFs the client
                        }
                        std::mem::take(&mut g.live)
                    };
                    broken = f.write_all(&chunk).is_err();
                }
                // Client went away (UI closed). Loop for the next attach.
                hlog(&format!("out-{id}: client disconnected"));
                disconnect(&f);
                drop(f);
                if !alive.load(Ordering::Acquire) {
                    return;
                }
            }
        });
    }

    /// Client → host stdin pipe.
    fn in_thread(id: u32, writer: pty_win::PtyWriter, alive: Arc<AtomicBool>) {
        std::thread::spawn(move || {
            let name = format!("velo-pty-in-{id}");
            loop {
                let Some(h) = pipe_instance(&name) else { return };
                if unsafe { ConnectNamedPipe(h.0, None) }.is_err() { /* already connected */ }
                hlog(&format!("in-{id}: client connected"));
                let mut f = h.into_file();
                let mut b = [0u8; 4096];
                loop {
                    match f.read(&mut b) {
                        Ok(0) | Err(_) => break,
                        Ok(n) => {
                            if writer.write_all(&b[..n]).is_err() {
                                hlog(&format!("in-{id}: pty stdin write FAILED"));
                                break;
                            }
                        }
                    }
                }
                hlog(&format!("in-{id}: client disconnected"));
                disconnect(&f);
                drop(f);
                if !alive.load(Ordering::Acquire) {
                    return;
                }
            }
        });
    }

    /// True once the client end of a connected pipe has closed. Non-blocking
    /// (unlike a read, which would serialize against writes on this handle).
    fn client_gone(f: &File) -> bool {
        let h = HANDLE(std::os::windows::io::AsRawHandle::as_raw_handle(f) as _);
        unsafe { PeekNamedPipe(h, None, 0, None, None, None) }.is_err()
    }

    fn exit_if_empty(sessions: &Sessions) {
        if sessions.lock().unwrap().is_empty() {
            std::process::exit(0);
        }
    }

    /// New (single) instance of `\\.\pipe\<name>`; None when it already exists
    /// elsewhere or creation fails.
    fn pipe_instance(name: &str) -> Option<OwnedPipe> {
        let path = HSTRING::from(format!(r"\\.\pipe\{name}"));
        let h = unsafe {
            CreateNamedPipeW(
                &path,
                PIPE_ACCESS_DUPLEX,
                PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT,
                1,
                256 * 1024,
                256 * 1024,
                0,
                None,
            )
        };
        if h == INVALID_HANDLE_VALUE {
            return None;
        }
        Some(OwnedPipe(h))
    }

    /// HANDLE wrapper so early-return paths close the pipe; `into_file`
    /// transfers ownership to a `File` (which closes it) exactly once.
    struct OwnedPipe(HANDLE);
    impl OwnedPipe {
        fn into_file(self) -> File {
            let h = self.0;
            std::mem::forget(self);
            unsafe { File::from_raw_handle(h.0 as _) }
        }
    }
    impl Drop for OwnedPipe {
        fn drop(&mut self) {
            unsafe { let _ = CloseHandle(self.0); }
        }
    }

    fn read_u16(f: &mut File) -> std::io::Result<u16> {
        let mut b = [0u8; 2];
        f.read_exact(&mut b)?;
        Ok(u16::from_le_bytes(b))
    }

    fn read_u32(f: &mut File) -> std::io::Result<u32> {
        let mut b = [0u8; 4];
        f.read_exact(&mut b)?;
        Ok(u32::from_le_bytes(b))
    }
}
