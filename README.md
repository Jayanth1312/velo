# Velo

Velo is a Windows-native, GPU-accelerated terminal emulator with first-class
vertical tabs. It renders the grid through wgpu (DirectX 12, with a Vulkan
fallback) and shapes text with cosmic-text, wrapping `alacritty_terminal` for
parsing and ConPTY for the process backend.

## Features

- GPU cell grid with smooth scrolling (pixel glide with overscan rows, no
  edge gaps or jitter during fast output)
- Vertical tab pane with rename, theme-tinted chrome, and the active tab
  title centered in the title bar
- Built-in editor mode: multi-file tabs (Ctrl+W to close), tree-sitter
  syntax highlighting for 8 languages, autosave, and a toggleable bottom
  terminal (Ctrl+J)
- Files sidebar: single click opens any file in the editor; Reveal opens
  the containing folder (WSL paths translated to Windows)
- Live themes: switching recolors the terminal, editor, and open files
  immediately
- Zoom (Ctrl+scroll / Ctrl+=/-) without scroll bounce

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
