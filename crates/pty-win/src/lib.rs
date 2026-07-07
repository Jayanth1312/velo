//! pty-win: Windows ConPTY backend — spawn a child over a pseudoconsole, read
//! its output on a background thread, write input, resize, and close cleanly.
//!
//! The entire implementation is `#[cfg(windows)]`. On other targets only the
//! [`PtyEvent`] type exists, so the workspace still builds (the terminal stack is
//! Windows-only).

/// Output events surfaced from the reader thread.
pub enum PtyEvent {
    /// A chunk of bytes read from the child's stdout/stderr.
    Data(Vec<u8>),
    /// The conout pipe reached EOF (child exited / console closed).
    Eof,
}

#[cfg(windows)]
mod imp {
    use std::ffi::OsStr;
    use std::fs::File;
    use std::io::{Read, Write};
    use std::iter::once;
    use std::os::windows::ffi::OsStrExt;
    use std::os::windows::io::{FromRawHandle, RawHandle};
    use std::sync::{Arc, Mutex};
    use std::thread::JoinHandle;

    use anyhow::{Context, Result};
    use windows::core::PWSTR;
    use windows::Win32::Foundation::{CloseHandle, HANDLE};
    use windows::Win32::System::Console::{
        ClosePseudoConsole, CreatePseudoConsole, ResizePseudoConsole, COORD, HPCON,
    };
    use windows::Win32::System::Pipes::CreatePipe;
    use windows::Win32::System::Threading::{
        CreateProcessW, InitializeProcThreadAttributeList, TerminateProcess,
        UpdateProcThreadAttribute, EXTENDED_STARTUPINFO_PRESENT, LPPROC_THREAD_ATTRIBUTE_LIST,
        PROCESS_INFORMATION, PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, STARTF_USESTDHANDLES,
        STARTUPINFOEXW, STARTUPINFOW,
    };

    use super::PtyEvent;

    /// Thread-safe writer for the child's stdin (conin write end).
    #[derive(Clone)]
    pub struct PtyWriter(Arc<Mutex<File>>);

    impl PtyWriter {
        pub fn write_all(&self, data: &[u8]) -> std::io::Result<()> {
            let mut file = self.0.lock().expect("pty writer mutex poisoned");
            file.write_all(data)?;
            file.flush()
        }
    }

    pub struct Pty {
        hpcon: HPCON,
        child: HANDLE,
        writer: PtyWriter,
        reader: Option<JoinHandle<()>>,
    }

    // HPCON/HANDLE are plain handles, safe to move between threads (as alacritty
    // does for its `Conpty`).
    unsafe impl Send for Pty {}

    fn wide_null(s: &str) -> Vec<u16> {
        OsStr::new(s).encode_wide().chain(once(0)).collect()
    }

    unsafe fn file_from_handle(handle: HANDLE) -> File {
        File::from_raw_handle(handle.0 as RawHandle)
    }

    pub fn spawn(
        cmdline: &str,
        cols: u16,
        rows: u16,
        mut on_event: impl FnMut(PtyEvent) + Send + 'static,
    ) -> Result<Pty> {
        unsafe {
            // 1. Two anonymous pipes: conout (child -> us) and conin (us -> child).
            let mut conout_read = HANDLE::default();
            let mut conout_write = HANDLE::default();
            CreatePipe(&mut conout_read, &mut conout_write, None, 0)
                .context("CreatePipe(conout)")?;

            let mut conin_read = HANDLE::default();
            let mut conin_write = HANDLE::default();
            CreatePipe(&mut conin_read, &mut conin_write, None, 0)
                .context("CreatePipe(conin)")?;

            // 2. Pseudoconsole over the PTY-side ends.
            let size = COORD {
                X: cols.max(1) as i16,
                Y: rows.max(1) as i16,
            };
            let hpcon: HPCON = CreatePseudoConsole(size, conin_read, conout_write, 0)
                .context("CreatePseudoConsole")?;

            // The PTY duplicated the ends it needs; close our copies.
            let _ = CloseHandle(conin_read);
            let _ = CloseHandle(conout_write);

            // 3. Startup info with a proc-thread attribute list carrying the PTY.
            let mut startup_info: STARTUPINFOEXW = std::mem::zeroed();
            startup_info.StartupInfo.cb = std::mem::size_of::<STARTUPINFOEXW>() as u32;
            startup_info.StartupInfo.dwFlags |= STARTF_USESTDHANDLES;

            // Sizing call: expected to "fail" while reporting the required size.
            let mut bytes_required: usize = 0;
            let _ = InitializeProcThreadAttributeList(
                None,
                1,
                None,
                &mut bytes_required,
            );
            let mut attr_list = vec![0u8; bytes_required];
            startup_info.lpAttributeList =
                LPPROC_THREAD_ATTRIBUTE_LIST(attr_list.as_mut_ptr() as *mut _);
            InitializeProcThreadAttributeList(
                Some(startup_info.lpAttributeList),
                1,
                None,
                &mut bytes_required,
            )
            .context("InitializeProcThreadAttributeList")?;

            UpdateProcThreadAttribute(
                startup_info.lpAttributeList,
                0,
                PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE as usize,
                Some(hpcon.0 as *const std::ffi::c_void),
                std::mem::size_of::<HPCON>(),
                None,
                None,
            )
            .context("UpdateProcThreadAttribute")?;

            // 4. Launch the child. CreateProcessW may write to the command line buffer.
            let mut cmdline_wide = wide_null(cmdline);
            let mut proc_info: PROCESS_INFORMATION = std::mem::zeroed();
            CreateProcessW(
                None,
                Some(PWSTR(cmdline_wide.as_mut_ptr())),
                None,
                None,
                false,
                EXTENDED_STARTUPINFO_PRESENT,
                None,
                None,
                &startup_info.StartupInfo as *const STARTUPINFOW,
                &mut proc_info,
            )
            .context("CreateProcessW")?;

            // The thread handle is not needed.
            let _ = CloseHandle(proc_info.hThread);

            // 5. Wrap our pipe ends as std files.
            let writer = PtyWriter(Arc::new(Mutex::new(file_from_handle(conin_write))));
            let mut reader_file = file_from_handle(conout_read);

            // 6. Reader thread: pump conout to events until EOF/error.
            let reader = std::thread::spawn(move || {
                let mut buf = [0u8; 65536];
                loop {
                    match reader_file.read(&mut buf) {
                        Ok(0) | Err(_) => {
                            on_event(PtyEvent::Eof);
                            break;
                        }
                        Ok(n) => on_event(PtyEvent::Data(buf[..n].to_vec())),
                    }
                }
            });

            Ok(Pty {
                hpcon,
                child: proc_info.hProcess,
                writer,
                reader: Some(reader),
            })
        }
    }

    impl Pty {
        pub fn resize(&self, cols: u16, rows: u16) -> Result<()> {
            let size = COORD {
                X: cols.max(1) as i16,
                Y: rows.max(1) as i16,
            };
            unsafe { ResizePseudoConsole(self.hpcon, size) }.context("ResizePseudoConsole")?;
            Ok(())
        }

        pub fn writer(&self) -> PtyWriter {
            self.writer.clone()
        }
    }

    impl Drop for Pty {
        fn drop(&mut self) {
            // Kill the child shell first so the conout pipe gets an EOF immediately.
            // Without this, ClosePseudoConsole blocks until the shell drains all
            // buffered output, which can take several seconds per tab on close.
            unsafe { let _ = TerminateProcess(self.child, 1); }

            // Order matters: ClosePseudoConsole blocks until the conout pipe is
            // drained, so the reader thread MUST still be alive to read it to EOF.
            // Closing the reader first would deadlock. (See alacritty PR #3084.)
            unsafe { ClosePseudoConsole(self.hpcon) };
            if let Some(handle) = self.reader.take() {
                let _ = handle.join();
            }
            unsafe {
                let _ = CloseHandle(self.child);
            }
            // The conin write File drops with the last PtyWriter clone.
        }
    }
}

#[cfg(windows)]
pub use imp::{spawn, Pty, PtyWriter};
