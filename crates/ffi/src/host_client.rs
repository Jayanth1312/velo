//! Client for velo-pty-host: sessions live in a detached process so they
//! survive the UI closing. Mirrors the `pty_win::spawn` shape (bytes out via
//! an event callback, bytes in via a writer) so the engine can treat local
//! and remote PTYs alike. See crates/pty-host/src/main.rs for the protocol.

#![cfg(windows)]

use std::fs::File;
use std::io::{Read, Write};
use std::sync::{Arc, Mutex};
use std::time::Duration;

use anyhow::{bail, Context, Result};

const CTL: &str = r"\\.\pipe\velo-pty-host-ctl";

/// A session owned by the host process. Dropping this DETACHES (the child
/// keeps running); call [`RemotePty::kill`] to actually end it.
pub struct RemotePty {
    pub id: u32,
    stdin: Arc<Mutex<File>>,
}

impl RemotePty {
    pub fn write_all(&self, b: &[u8]) -> std::io::Result<()> {
        let mut f = self.stdin.lock().expect("remote stdin mutex poisoned");
        f.write_all(b)?;
        f.flush()
    }

    pub fn writer(&self) -> Arc<Mutex<File>> {
        self.stdin.clone()
    }

    pub fn resize(&self, cols: u16, rows: u16) -> Result<()> {
        let mut req = vec![3u8];
        req.extend_from_slice(&self.id.to_le_bytes());
        req.extend_from_slice(&cols.to_le_bytes());
        req.extend_from_slice(&rows.to_le_bytes());
        let _ = ctl_roundtrip(&req, 1)?;
        Ok(())
    }

    pub fn kill(&self) {
        let mut req = vec![4u8];
        req.extend_from_slice(&self.id.to_le_bytes());
        let _ = ctl_roundtrip(&req, 1);
    }
}

/// Spawn `cmd` inside the host (starting the host if needed).
pub fn spawn_remote(
    cmd: &str,
    cols: u16,
    rows: u16,
    on_event: impl FnMut(pty_win::PtyEvent) + Send + 'static,
) -> Result<RemotePty> {
    ensure_host()?;
    let mut req = vec![1u8];
    req.extend_from_slice(&cols.to_le_bytes());
    req.extend_from_slice(&rows.to_le_bytes());
    req.extend_from_slice(&(cmd.len() as u32).to_le_bytes());
    req.extend_from_slice(cmd.as_bytes());
    let (resp, _f) = ctl_roundtrip(&req, 4)?;
    let id = u32::from_le_bytes(resp[..4].try_into().unwrap());
    if id == u32::MAX {
        bail!("host failed to spawn '{cmd}'");
    }
    open_session(id, on_event)
}

/// Reattach to a session that survived a previous UI. Errors when it's gone.
pub fn attach_remote(
    id: u32,
    on_event: impl FnMut(pty_win::PtyEvent) + Send + 'static,
) -> Result<RemotePty> {
    if !list_remote()?.contains(&id) {
        bail!("host session {id} no longer exists");
    }
    open_session(id, on_event)
}

/// Ids of sessions currently alive in the host (empty when no host runs).
pub fn list_remote() -> Result<Vec<u32>> {
    if connect(CTL).is_err() {
        return Ok(Vec::new()); // no host -> no sessions
    }
    let (head, mut f) = ctl_roundtrip(&[5u8], 4)?;
    let count = u32::from_le_bytes(head[..4].try_into().unwrap()) as usize;
    let mut ids = Vec::with_capacity(count);
    for _ in 0..count {
        let mut b = [0u8; 4];
        f.read_exact(&mut b).context("host list truncated")?;
        ids.push(u32::from_le_bytes(b));
    }
    Ok(ids)
}

/// Open the per-session data pipes and pump output into `on_event`.
fn open_session(
    id: u32,
    mut on_event: impl FnMut(pty_win::PtyEvent) + Send + 'static,
) -> Result<RemotePty> {
    let stdin = connect_retry(&format!(r"\\.\pipe\velo-pty-in-{id}"))?;
    let mut out = connect_retry(&format!(r"\\.\pipe\velo-pty-out-{id}"))?;
    std::thread::spawn(move || {
        let mut buf = [0u8; 65536];
        loop {
            match out.read(&mut buf) {
                Ok(0) | Err(_) => {
                    on_event(pty_win::PtyEvent::Eof);
                    break;
                }
                Ok(n) => on_event(pty_win::PtyEvent::Data(buf[..n].to_vec())),
            }
        }
    });
    Ok(RemotePty { id, stdin: Arc::new(Mutex::new(stdin)) })
}

/// One request/response over a fresh control connection.
/// Returns the first `resp_len` bytes plus the still-open pipe (List reads more).
fn ctl_roundtrip(req: &[u8], resp_len: usize) -> Result<(Vec<u8>, File)> {
    let mut f = connect_retry(CTL)?;
    f.write_all(req).context("host ctl write")?;
    f.flush().ok();
    let mut resp = vec![0u8; resp_len];
    f.read_exact(&mut resp).context("host ctl read")?;
    Ok((resp, f))
}

/// Start the host if its control pipe is not answering.
pub fn ensure_host() -> Result<()> {
    if connect(CTL).is_ok() {
        return Ok(());
    }
    let exe = std::env::current_exe()
        .ok()
        .and_then(|p| p.parent().map(|d| d.join("velo-pty-host.exe")))
        .context("cannot locate velo-pty-host.exe")?;
    use std::os::windows::process::CommandExt;
    // DETACHED_PROCESS | CREATE_NO_WINDOW | CREATE_NEW_PROCESS_GROUP:
    // the host must not die with the UI process tree.
    std::process::Command::new(&exe)
        .creation_flags(0x0000_0008 | 0x0800_0000 | 0x0000_0200)
        .spawn()
        .with_context(|| format!("starting {}", exe.display()))?;
    for _ in 0..40 {
        if connect(CTL).is_ok() {
            return Ok(());
        }
        std::thread::sleep(Duration::from_millis(50));
    }
    bail!("velo-pty-host did not come up");
}

fn connect(path: &str) -> std::io::Result<File> {
    std::fs::OpenOptions::new().read(true).write(true).open(path)
}

/// The host recreates pipe instances between clients; ride out the gap.
fn connect_retry(path: &str) -> Result<File> {
    for _ in 0..40 {
        if let Ok(f) = connect(path) {
            return Ok(f);
        }
        std::thread::sleep(Duration::from_millis(50));
    }
    bail!("cannot connect {path}");
}
