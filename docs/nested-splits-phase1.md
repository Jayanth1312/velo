# Nested splits — Phase 1 implementation plan

Goal: replace the flat `SplitGroup` engine with a **binary pane tree** that
supports **mixed layouts** (e.g. 2 columns on top, 1 full-width row below),
driven by **per-pane edge drops**. Reuse the existing 4 `SwapChainPanel`s as the
leaf pool (cap stays 4). Free-drag resize (GridSplitter), >4 panes (dynamic
panel pool), and layout serialization are later phases.

Non-negotiable: the app must keep building and typing/focus/rendering must work
across any tree. Do NOT regress single-pane use or the tab/group/editor paths.

## 1. New file `PaneTree.cs`

```csharp
namespace Velo.App;

abstract class PaneNode { }

sealed class Leaf : PaneNode
{
    public uint TabId;
    public Leaf(uint tabId) => TabId = tabId;
}

sealed class Branch : PaneNode
{
    public bool Vertical;          // true = stacked rows, false = side-by-side columns
    public double Ratio = 0.5;     // first child's share (fixed 0.5 this phase)
    public PaneNode A = null!, B = null!;
}
```

Helpers (static or on the window):
- `IEnumerable<Leaf> Leaves(PaneNode n)` — in-order walk (left/top first). Drives
  pill order and panel assignment.
- `Leaf? FindLeaf(PaneNode n, uint tabId)`.
- `bool Remove(ref PaneNode root, uint tabId)` — drop a leaf; when a `Branch`
  loses a child, **promote the surviving child** into the branch's place.
  Returns false if the tree becomes empty.
- `void SplitLeaf(ref PaneNode root, uint targetTab, uint newTab, Edge edge)` —
  find the target leaf, replace it with a `Branch` (orientation + child order
  from `edge`) holding the old leaf and a new `Leaf(newTab)`.

## 2. Replace `_splits` with per-view roots

- Remove `SplitGroup` / `List<SplitGroup> _splits` and its helpers.
- Add `readonly Dictionary<uint, PaneNode> _roots = new();` keyed by the view's
  **anchor tab** (the tab that "owns" the split). A tab with no split has no
  entry (implicitly a lone leaf). Keep a `PaneNode? _viewRoot` = the tree the
  body currently shows.
- `SplitOf(tabId)` equivalent: `RootContaining(uint tabId)` → the root whose
  `Leaves()` include `tabId`, or null.
- When a split collapses to one leaf, drop its `_roots` entry (back to a lone
  tab).

## 3. Layout — recursive nested Grid (`BuildLayout`)

Replace `ApplyLayout`. Keep the 4 `SwapChainPanel`s (`_panels[0..3]`) as a pool.

- Assign each `Leaf` (in in-order sequence) a panel index 0..3; that panel binds
  the leaf's core pane (`_slotCore`/`_slotTab` stay, indexed by leaf order).
- Build a tree of `Grid`s into `PaneHost`:
  - `Leaf` → the assigned `SwapChainPanel`.
  - `Branch` → a `Grid` with two star tracks (`Ratio` : `1-Ratio`) split by
    orientation; child A in track 0, child B in track 1.
- `PaneHost` stops being the 2×2 grid; it holds a single root child (the built
  tree) plus the existing overlays (`SplitOverlay`, `PaneCtl`, `ZoomToast`).
  Overlays must sit ABOVE the tree and span it — put the tree in a child `Grid`
  and keep overlays as siblings on top.
- After building, push native sizes to each leaf panel (existing `PushPaneSize`),
  and re-bind swapchains (`BindSwapchain`) for reused panels.

Panels not used by the current tree → `Visibility.Collapsed`.

## 4. Rewire the split entry points

- `ShowView(tabId)`: set `_viewRoot` = `RootContaining(tabId)` or a lone
  `Leaf(tabId)`; assign panels to leaves in order; `_focusedPane` = index of the
  leaf whose `TabId == tabId`; `BuildLayout()`; bind + focus. Keep the
  `_viewTab`/`_prevViewTab` tracking already there.
- `AddPane(dropped, edge, anchorHint)`: resolve the **target leaf** = the pane
  under the drop (Phase 1 wires this from the drop hit-test — see §5). If the
  anchor view is a lone tab, its root becomes `Leaf(anchor)` first. Then
  `SplitLeaf(ref root, targetTab, dropped.Id, edge)`; register/replace the
  `_roots` entry under the anchor; remove `dropped` from any other root first.
  `ShowView(dropped.Id)`, `RefreshTabList()`, `SelectById`, focus.
- `HandleTabGone(id)` / close: `Remove(ref root, id)`; if root becomes a single
  leaf, dissolve the `_roots` entry; if empty, fall through to existing
  tab-removal. Rebuild view if the closed tab was visible.
- Pane controls: **close/full-screen** = `Remove` the other leaves (full-screen
  keeps one leaf); **swap** = exchange two leaves' `TabId`s in the tree; unjoin =
  flatten root to lone tabs (drop `_roots` entry, keep tabs).
- Pill row (`BuildSplitPills` / `RefreshTabList`): a split row's members =
  `Leaves(root).Select(l => l.TabId)` mapped to `TabVM`s, in order. Unchanged
  otherwise.

## 5. Per-pane edge drops (the new capability)

- `PaneAt(point)` already returns a leaf/panel index from actual element bounds —
  keep using panel bounds (each leaf = one panel, so bounds are correct in the
  nested grid).
- During `DragOver`: hit-test the **panel under the pointer** → its leaf's
  `TabId` = target. Pick nearest of 4 edges **within that panel's rect** (reuse
  `EdgeFromPoint` but relative to the panel, not the whole body). Preview:
  highlight that panel's half (`ShowDropRect` in panel-local coords → PaneHost
  coords) and, for the live shrink, temporarily split just that leaf in a
  preview clone of the tree (or nested-grid the one panel into two tracks with
  the empty half as the drop zone).
- On drop: `AddPane(dropped, edge, targetTab)` splits **only** that leaf →
  mixed layouts.
- Lone-pane view (no split yet): target = the only leaf; behaves like today.

## 6. Cleanup / guards

- Delete dead flat-model code (`_splitVertical` usage folds into per-branch
  `Vertical`; keep the field only if still referenced by non-tree paths, else
  remove).
- Keep `MaxPanes = 4`; block drops that would exceed 4 leaves.
- Editor mode, tab groups (`TabGroup`), multi-select join/unjoin must still work:
  `JoinTabs(list)` builds a balanced left-leaning tree of the tabs;
  `UnjoinSplit` flattens.

## 7. Verify (the fork must do this)

1. `dotnet.exe build` in `csharp/Velo.App` → **Build succeeded**, zero errors.
   Kill `Velo.App.exe` first if locked.
2. Sanity self-check for the tree ops: add a tiny `assert`-style check (e.g. a
   `#if DEBUG` method or a comment-guarded unit check) proving `SplitLeaf` then
   `Remove` round-trips and `Leaves()` order is stable. No test framework — one
   runnable check.
3. Report exactly what changed and any deferred edge cases.

Do not touch Rust (`crates/`) — core panes are already an unbounded `Vec`.
