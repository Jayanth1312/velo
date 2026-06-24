# Velo

Velo is a Windows-native, GPU-accelerated terminal emulator with first-class
vertical tabs. It renders the grid through wgpu (DirectX 12, with a Vulkan
fallback) and shapes text with cosmic-text, wrapping `alacritty_terminal` for
parsing and ConPTY for the process backend.

## Target

Targets Windows 10 1809+ / `x86_64-pc-windows-msvc`. ConPTY requires
Win10 1809+. The workspace also compiles on Linux for development, but the
terminal/PTY features are Windows-only.

## Architecture

Hybrid: a Rust core (`term-core` parse + `pty-win` ConPTY + `renderer`/`text`
wgpu) compiled to the `velo_core.dll` cdylib (`crates/ffi`), driven by a C# WinUI
3 shell (`csharp/Velo.App`) that owns the window, the custom title bar, and the
collapsible vertical-tab pane. The shell hosts the wgpu output in a child HWND
(child-HWND airspace) and talks to the core over a small C ABI. WinUI was chosen
for the chrome because native WinUI support in Rust is still minimal.

## Build & run (Windows)

```
cargo build --release -p velo_core      # -> target/release/velo_core.dll
dotnet build csharp/Velo.App            # copies the dll next to the exe
```

Run the built `Velo.App` exe. The interactive terminal is Windows-only (ConPTY +
WinUI + GPU). On other hosts only the headless benchmark runs:

```
cargo run -p app -- bench
```
