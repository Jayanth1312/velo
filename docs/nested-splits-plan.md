# Phase — Nested split tree (arbitrary pane layouts)

Status: planned, not started. Owner: split-view work on `perf-fixes`.

## Problem

Today a split is **flat**: `SplitGroup { List<uint> Tabs; bool Vertical }` — one
orientation for every pane in the group. Dropping a 3rd tab can only add another
column *or* re-orient the whole group to rows (the current behavior). You can
never get "2 panes side-by-side on top, 1 full-width below" — a **mixed** layout.

The user wants browser/tmux/VS-Code-style splits: drop a tab against **one
pane's edge** and split *only that pane*, leaving the rest untouched.

## Why flat can't do it

A single `Vertical` bool forces all siblings onto one axis. Mixed layouts need
different orientations at different depths → a **tree**, not a list.

## Research — how others model this

| Tool | Model |
|------|-------|
| tmux | Binary tree; each node splits H or V with a size; layout serializes to a string |
| Windows Terminal | Binary `Pane`: two children + split direction + ratio; leaves hold a terminal |
| VS Code | Recursive "grid" of rows/columns of editor groups (n-ary branches) |
| i3 / sway | Tree of containers, each container has a layout (split-h / split-v / tabbed) |

Consensus: **binary tree**, leaf = pane, internal node = (orientation, ratio,
childA, childB). Mixed layouts fall out for free:

```
root (Vertical = rows)
├── branch (Horizontal = columns)
│   ├── leaf A
│   └── leaf B
└── leaf C          ← full-width row under the A|B columns
```

## Target model (C#)

Replace `SplitGroup` / `_splits` with a per-view tree:

```csharp
abstract class PaneNode { }
sealed class Leaf   : PaneNode { public uint TabId; }
sealed class Branch : PaneNode
{
    public bool Vertical;      // true = stacked rows, false = side-by-side columns
    public double Ratio = 0.5; // first child's share
    public PaneNode A, B;
}
```

- A top-level tab owns a root `PaneNode`. Alone → root is a `Leaf`. Registry:
  `Dictionary<uint, PaneNode> _roots` keyed by the owning tab, or a single
  "active view root". (Decide: is a split owned by a tab, or is it a first-class
  workspace? Recommend: the split is the view; the tab list shows its leaves as
  a pill row, same as now.)
- `MaxPanes` becomes a soft cap (6–8) or is dropped. Rust core already stores
  panes in an unbounded `Vec<Option<Pane>>`, so no core change for the count.

## Layout — nested Grid + GridSplitter

Render the tree recursively into nested `Grid`s:

- `Branch` → a `Grid` with two star tracks sized by `Ratio` and a `GridSplitter`
  between them (free drag-to-resize, writes `Ratio` back).
- `Leaf` → the `SwapChainPanel` bound to that pane.

Kills the fixed 4-panel XAML array. Panels become a **dynamic pool**:
`Dictionary<uint /*paneId*/, SwapChainPanel>`, created on leaf appearance,
reused/retired as the tree changes. `ApplyLayout` is replaced by a recursive
`BuildTree(node) -> FrameworkElement`.

Caveat: `SwapChainPanel` is not hit-test-visible (DComp), so `GridSplitter`
handles and per-pane drop hit-testing must live in an **input overlay** on
`PaneHost` (we already route all pane pointer input through `PaneHost` + a
`PaneAt(point)` walk — extend `PaneAt` to walk the tree's computed rects).

## Drag-drop — the actual new capability

While dragging over the body, hit-test which **leaf** the pointer is over, then
pick the nearest of 5 zones **within that leaf**: left / right / top / bottom
half, or center.

- Edge zone → split **that leaf** into a `Branch` (orientation from the edge),
  inserting the dropped tab on that side. Only that pane subdivides.
- Center (future) → tab the dropped terminal *into* the pane (stacked tabs).

Preview: highlight the target leaf's half; optionally live-shrink by inserting a
temporary branch with the preview ratio (pure XAML relayout, no native resize —
same rule as today, native resize during the drag modal loop wedges input).

## Resize / focus / controls

- `GridSplitter` drag → update `Branch.Ratio` → native `velo_pane_resize` on
  the two affected leaves (throttled, same coalescing as sidebar animation).
- Focus: focused leaf drives `FocusedCore()`; input routing unchanged.
- Per-pane controls (swap / full-screen / close): become tree ops —
  **close/full** = collapse a `Branch` (promote the sibling), **swap** = exchange
  two leaves. "Unjoin" = flatten the whole tree back to separate tabs.
- Tab-list pill row: still lists the tree's leaves left-to-right (in-order walk).

## Suggested sub-phases

1. **Tree + layout**: model, recursive nested-Grid builder, dynamic panel pool,
   `PaneAt` over tree rects. Build splits via a temporary command/keybind (no
   drag yet). Ship + verify rendering/focus/typing across an arbitrary tree.
2. **Per-pane edge drops**: 5-zone hit-test, preview, split-one-leaf insert.
3. **GridSplitter resize** + ratio persistence.
4. **Tree-aware controls**: swap / collapse (close, full-screen) / unjoin.
5. *(optional)* Serialize the layout to `settings.json`, restore on reopen.

## Risks

- Dynamic `SwapChainPanel` create/destroy + swapchain (re)binding lifecycle.
- `GridSplitter` interacting with non-hit-test panels → handles in the overlay,
  or keep the current manual pointer-drag approach for seams.
- `PaneAt` correctness once panes are arbitrary rects (was a fixed 2×2 grid).
- Everything split-related is touched: `ShowView`, `ApplyLayout`, `AddPane`,
  `PreviewSplit`, pill rows, pane controls, `HandleTabGone`. Large diff — do it
  behind the sub-phases above, not in one pass.

## Interim (until this ships)

The flat model stays. A 3rd drop sets the **whole** split's orientation from the
drop edge (predictable, but uniform — not mixed). This is the documented
limitation the tree phase removes.
