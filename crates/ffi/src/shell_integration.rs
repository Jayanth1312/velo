//! Shell integration (OSC 133) injection for WSL shells.
//!
//! Velo's Outline populates from OSC 133 command marks decoded in term-core. The
//! core never injects anything — it relies on the shell emitting the marks. WSL
//! bash/zsh emit nothing by default, so the Outline stays empty. This module
//! rewrites the spawn cmdline for a WSL invocation so the interactive shell loads
//! a small rc that sources the user's real config, then installs OSC 133 hooks.
//!
//! Scope: ONLY plain WSL invocations (`wsl.exe`, optionally with `-d/-u/--cd`
//! prefix flags) launching bash or zsh. Anything else — PowerShell, cmd, native
//! Git Bash, an explicit `wsl.exe -- <cmd>` — passes through UNCHANGED, so the
//! default shell path is never touched. Windows/WSL only (never built on Linux).
//!
//! The mark format matches term-core's decoder (`133;C;<cmdline>` carries the
//! command text; `133;D;<exit>`; `133;A` prompt start).

use std::collections::HashMap;
use std::io::Write;
use std::os::windows::process::CommandExt;
use std::process::{Command, Stdio};
use std::sync::{Mutex, OnceLock};

/// Spawned from a GUI process, a console app gets a fresh visible console that
/// flashes on screen and steals focus — every probe popped a cmd window.
const CREATE_NO_WINDOW: u32 = 0x0800_0000;

/// zsh rc placed at `$ZDOTDIR/.zshrc`. Resets ZDOTDIR to the real home (so the
/// user's config + any nested zsh behave), sources the user's env/rc, then
/// installs the hooks. D is only emitted when a command actually ran, so the
/// first prompt doesn't log a phantom entry.
const ZSH_RC: &str = r#"ZDOTDIR="$HOME"
[ -f "$HOME/.zshenv" ] && source "$HOME/.zshenv"
[ -f "$HOME/.zshrc" ] && source "$HOME/.zshrc"
_velo_preexec() { _velo_active=1; printf '\033]133;C;%s\007' "$1"; }
_velo_precmd() {
  local e=$?
  [ -n "$_velo_active" ] && printf '\033]133;D;%s\007' "$e"
  _velo_active=
  printf '\033]133;A\007'
}
if autoload -Uz add-zsh-hook 2>/dev/null; then
  add-zsh-hook preexec _velo_preexec
  add-zsh-hook precmd _velo_precmd
else
  preexec_functions+=(_velo_preexec)
  precmd_functions+=(_velo_precmd)
fi
"#;

/// bash rc passed via `--rcfile`. bash has no native preexec, so a DEBUG trap
/// emits the command mark; `_velo_in_prompt` guards it from firing on the
/// PROMPT_COMMAND internals.
const BASH_RC: &str = r#"[ -f "$HOME/.bashrc" ] && source "$HOME/.bashrc"
_velo_preexec() {
  [ -n "$COMP_LINE" ] && return
  [ -n "$_velo_in_prompt" ] && return
  _velo_active=1
  printf '\033]133;C;%s\007' "$BASH_COMMAND"
}
_velo_precmd() {
  local e=$?
  _velo_in_prompt=1
  [ -n "$_velo_active" ] && printf '\033]133;D;%s\007' "$e"
  _velo_active=
  printf '\033]133;A\007'
  _velo_in_prompt=
}
trap '_velo_preexec' DEBUG
case "$PROMPT_COMMAND" in
  *_velo_precmd*) ;;
  *) PROMPT_COMMAND="_velo_precmd${PROMPT_COMMAND:+;$PROMPT_COMMAND}" ;;
esac
"#;

enum Entry {
    Pending,
    Done(Option<String>),
}

static CACHE: OnceLock<Mutex<HashMap<String, Entry>>> = OnceLock::new();

/// Return the effective spawn cmdline: a rewritten WSL invocation with OSC 133
/// integration when recognized, else the original string unchanged.
///
/// NEVER blocks: wrap_wsl runs up to three wsl.exe round-trips (seconds), and
/// spawn_session is called on the UI thread — inlining the probes froze the
/// window ~5s per new tab, long enough for DWM to drop the SystemBackdrop
/// (black window). The probe result is persisted to disk, so the first tab of
/// a run reuses the previous run's cmdline immediately while a background
/// re-probe refreshes it (distro or login shell may have changed). Only the
/// very first WSL tab EVER (no disk cache yet) misses Outline marks.
pub fn prepare(shell: &str) -> String {
    let cache = CACHE.get_or_init(|| Mutex::new(HashMap::new()));
    {
        let mut map = cache.lock().unwrap();
        if let Some(entry) = map.get(shell) {
            return match entry {
                Entry::Done(Some(wrapped)) => wrapped.clone(),
                _ => shell.to_string(),
            };
        }
        map.insert(shell.to_string(), Entry::Pending);
    }
    // A previous run's probe result is usable right away; the re-probe below
    // still refreshes it.
    let disk = cache_path(shell).and_then(|p| std::fs::read_to_string(p).ok());
    if let Some(w) = &disk {
        cache
            .lock()
            .unwrap()
            .insert(shell.to_string(), Entry::Done(Some(w.clone())));
    }
    let s = shell.to_string();
    std::thread::spawn(move || {
        let wrapped = wrap_wsl(&s);
        // ponytail: a failed re-probe (None) drops a cached cmdline back to the
        // plain shell rather than keeping a possibly-stale wrapper — a stale
        // `wsl.exe -d gone …` would break every future tab, losing Outline
        // marks until the next successful probe merely degrades.
        if let Some(p) = cache_path(&s) {
            match &wrapped {
                Some(w) => {
                    if let Some(dir) = p.parent() {
                        let _ = std::fs::create_dir_all(dir);
                    }
                    let _ = std::fs::write(p, w);
                }
                None => {
                    let _ = std::fs::remove_file(p);
                }
            }
        }
        CACHE
            .get()
            .unwrap()
            .lock()
            .unwrap()
            .insert(s, Entry::Done(wrapped));
    });
    disk.unwrap_or_else(|| shell.to_string())
}

/// Disk cache for a shell string's wrapped cmdline:
/// `%LOCALAPPDATA%\Velo\wslwrap-<hash>.txt`.
fn cache_path(shell: &str) -> Option<std::path::PathBuf> {
    use std::hash::{Hash, Hasher};
    let mut h = std::collections::hash_map::DefaultHasher::new();
    shell.hash(&mut h);
    let dir = std::path::PathBuf::from(std::env::var_os("LOCALAPPDATA")?).join("Velo");
    Some(dir.join(format!("wslwrap-{:016x}.txt", h.finish())))
}

fn wrap_wsl(shell: &str) -> Option<String> {
    let toks: Vec<&str> = shell.split_whitespace().collect();
    let prog = *toks.first()?;
    let progl = prog.to_ascii_lowercase();
    let is_wsl = progl == "wsl" || progl == "wsl.exe" || progl.ends_with("\\wsl.exe");
    if !is_wsl {
        return None;
    }

    // Only distro/user/cd prefix flags are allowed. An explicit command
    // (`-e`, `--`, or a bare argument) means the user chose exactly what to run —
    // leave it untouched rather than risk mangling it.
    let rest = &toks[1..];
    let mut i = 0;
    while i < rest.len() {
        match rest[i] {
            "-d" | "--distribution" | "-u" | "--user" | "--cd" => i += 2,
            _ => return None,
        }
    }
    let prefix: Vec<String> = rest.iter().map(|s| s.to_string()).collect();

    // One round-trip for both probes: each wsl.exe launch is expensive (can
    // boot the distro), so don't pay it twice.
    let probed = wsl_capture(prog, &prefix, "printf '%s\\n%s' \"$HOME\" \"$SHELL\"")?;
    let mut lines = probed.lines();
    let home = lines.next().unwrap_or("").trim().to_string();
    if home.is_empty() {
        return None;
    }
    let shpath = lines.next().unwrap_or("").trim().to_string();
    let base = shpath.rsplit('/').next().unwrap_or("");

    let dir = format!("{home}/.velo");
    let tail = match base {
        "zsh" => {
            wsl_write(prog, &prefix, &format!("{dir}/.zshrc"), ZSH_RC)?;
            format!("-- env ZDOTDIR={dir} zsh -i")
        }
        "bash" => {
            wsl_write(prog, &prefix, &format!("{dir}/velo.bash"), BASH_RC)?;
            format!("-- bash --rcfile {dir}/velo.bash -i")
        }
        _ => return None,
    };

    let mut cmd = String::from(prog);
    for a in &prefix {
        cmd.push(' ');
        cmd.push_str(a);
    }
    cmd.push(' ');
    cmd.push_str(&tail);
    Some(cmd)
}

/// Run `sh -c <script>` inside WSL and capture stdout. argv (not a cmdline
/// string) so there is no Windows quoting to fight.
fn wsl_capture(prog: &str, prefix: &[String], script: &str) -> Option<String> {
    let out = Command::new(prog)
        .args(prefix)
        .arg("--")
        .arg("sh")
        .arg("-c")
        .arg(script)
        .creation_flags(CREATE_NO_WINDOW)
        .stdin(Stdio::null())
        .stderr(Stdio::null())
        .output()
        .ok()?;
    if !out.status.success() {
        return None;
    }
    Some(String::from_utf8_lossy(&out.stdout).into_owned())
}

/// Write `content` to a WSL-side path via `cat` on stdin, so the file body needs
/// no shell quoting (only the path does; home paths have no quotes/spaces).
fn wsl_write(prog: &str, prefix: &[String], path: &str, content: &str) -> Option<()> {
    let dir = path.rsplit_once('/').map(|(d, _)| d).unwrap_or(".");
    let script = format!("mkdir -p '{dir}' && cat > '{path}'");
    let mut child = Command::new(prog)
        .args(prefix)
        .arg("--")
        .arg("sh")
        .arg("-c")
        .arg(&script)
        .creation_flags(CREATE_NO_WINDOW)
        .stdin(Stdio::piped())
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .spawn()
        .ok()?;
    child.stdin.take()?.write_all(content.as_bytes()).ok()?;
    child.wait().ok()?.success().then_some(())
}
