//! velo: headless benchmark harness.
//!
//! The interactive terminal now lives in the `velo_core` cdylib (`crates/ffi`),
//! driven by the C# WinUI 3 shell (`csharp/Velo.App`). This `app` bin keeps only
//! the cross-platform `bench` harness used for parser/renderer numbers.

mod bench;

/// Default cell font size, in points. 20pt reads comfortably on HiDPI panels.
/// Multiplied by the per-monitor DPI scale before building the font.
pub const FONT_SIZE_PT: f32 = 20.0;

fn main() -> anyhow::Result<()> {
    env_logger::init();
    if std::env::args().nth(1).as_deref() == Some("bench") {
        return bench::run();
    }
    eprintln!("velo: interactive terminal moved to the C# WinUI shell (velo_core.dll).");
    eprintln!("Run `velo bench` for the headless benchmark harness.");
    Ok(())
}
