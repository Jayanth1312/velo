# Velo Plan 2 — AI-era features


Foundation asset: OSC 133/7 plumbing already delivers per-command boundaries, exit codes, durations, cwd. Order below = suggested build order.

1. **Command blocks (Warp-style)** — group output by OSC 133 marks: per-block hover chrome in the terminal margin, actions: copy command, copy output, rerun, collapse; jump prev/next block (Ctrl+Up/Down). Needs: term-core records mark rows in scrollback coordinates; renderer draws block gutter; C# context menu.
2. **Agent awareness** — detect long-running `claude`/`codex`/`gemini` etc from OSC 133 C command text: tab shows spinner + elapsed; on command end (or output stall → likely awaiting input) while window unfocused → Windows toast + taskbar flash. Support OSC 9 / OSC 777 notify sequences too.
3. **Click-to-open** — regex scan rendered rows for URLs + `path:line[:col]`; Ctrl+Click opens browser / configured editor (`code -g path:line`). OSC 8 hyperlink support in term-core.
4. **AI actions on history** — palette commands: "Explain last error" (last block with exit≠0 → prompt with command+output+cwd to Claude API), "Natural language → command" inline composer. Settings: API key. (claude-api skill for integration details at build time.)
5. **Session restore** — persist tabs (shell, cwd, title, groups) to settings dir on close; reopen on launch.
6. **Quake mode** — global hotkey (Win+`) show/hide with slide animation; single instance.
7. **Broadcast input** — toggle: keystrokes go to all visible panes (multi-agent workflows).
8. **OSC 9;4 progress** → taskbar progress via `ITaskbarList3`.
9. **Polish**: bell → subtle tab flash; paste multi-line warning dialog; drag-drop file onto terminal pastes quoted path.

Each feature = its own brainstorm/design pass before implementation (per superpowers flow); this list is the roadmap, not the spec.
