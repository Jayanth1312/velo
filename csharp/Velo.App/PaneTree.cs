using System.Collections.Generic;
using System.Linq;

namespace Velo.App;

/// <summary>A pane layout is a binary tree: a <see cref="Leaf"/> holds one tab,
/// a <see cref="Branch"/> splits its area between two children. Mixed layouts
/// (e.g. two columns on top, one full-width row below) fall out of nesting.</summary>
abstract class PaneNode { }

sealed class Leaf : PaneNode
{
    public uint TabId;
    public Leaf(uint tabId) => TabId = tabId;
}

sealed class Branch : PaneNode
{
    public bool Vertical;       // true = stacked rows, false = side-by-side columns
    public double? Ratio;       // first child's share; null = auto (leaf-count weight)
    public PaneNode A = null!, B = null!;
}

/// <summary>Pure operations over the pane tree. All return the (possibly new) root;
/// callers reassign. In-order = first child before second, so left/top come first —
/// this is the leaf order the pane slots and pill row follow.</summary>
static class PaneTree
{
    public static IEnumerable<uint> Leaves(PaneNode n)
    {
        if (n is Leaf l)
        {
            yield return l.TabId;
            yield break;
        }
        var b = (Branch)n;
        foreach (var t in Leaves(b.A)) yield return t;
        foreach (var t in Leaves(b.B)) yield return t;
    }

    public static bool Contains(PaneNode n, uint id) => Leaves(n).Contains(id);

    public static int Count(PaneNode n)
        => n is Leaf ? 1 : Count(((Branch)n).A) + Count(((Branch)n).B);

    public static Leaf? Find(PaneNode n, uint id)
    {
        if (n is Leaf l) return l.TabId == id ? l : null;
        var b = (Branch)n;
        return Find(b.A, id) ?? Find(b.B, id);
    }

    /// <summary>Drop leaf <paramref name="id"/>; when a branch loses a child, the
    /// surviving child takes the branch's place. Returns the new root, or null when
    /// the whole tree was that one leaf.</summary>
    public static PaneNode? Remove(PaneNode n, uint id)
    {
        if (n is Leaf l)
            return l.TabId == id ? null : n;
        var b = (Branch)n;
        var a = Remove(b.A, id);
        var c = Remove(b.B, id);
        if (a is null) return c;   // A collapsed → promote B
        if (c is null) return a;   // B collapsed → promote A
        b.A = a; b.B = c;
        return b;
    }

    /// <summary>Split the leaf holding <paramref name="target"/> into a branch that
    /// keeps it and adds <paramref name="newTab"/>. <paramref name="front"/> puts the
    /// new tab first (left/top). Returns the new root.</summary>
    public static PaneNode SplitLeaf(PaneNode n, uint target, uint newTab, bool vertical, bool front)
    {
        if (n is Leaf l && l.TabId == target)
        {
            var added = new Leaf(newTab);
            return front
                ? new Branch { Vertical = vertical, A = added, B = l }
                : new Branch { Vertical = vertical, A = l, B = added };
        }
        if (n is Branch b)
        {
            b.A = SplitLeaf(b.A, target, newTab, vertical, front);
            b.B = SplitLeaf(b.B, target, newTab, vertical, front);
            return b;
        }
        return n;
    }

    /// <summary>Left-leaning tree of `tabs` as side-by-side columns (used by Join).</summary>
    public static PaneNode Columns(IReadOnlyList<uint> tabs)
    {
        PaneNode root = new Leaf(tabs[0]);
        for (int i = 1; i < tabs.Count; i++)
            root = new Branch { Vertical = false, A = root, B = new Leaf(tabs[i]) };
        return root;
    }

    /// <summary>Runtime self-check (no test framework): SplitLeaf then Remove
    /// round-trips and Leaves() order is stable. Throws if the tree ops regress.</summary>
    public static void SelfCheck()
    {
        PaneNode root = new Leaf(1);
        root = SplitLeaf(root, 1, 2, vertical: false, front: false);   // 1 | 2
        root = SplitLeaf(root, 2, 3, vertical: true, front: false);    // 1 | (2 / 3)
        var order = Leaves(root).ToList();
        if (!order.SequenceEqual(new uint[] { 1, 2, 3 }))
            throw new System.InvalidOperationException($"PaneTree leaf order wrong: {string.Join(",", order)}");
        if (Count(root) != 3)
            throw new System.InvalidOperationException("PaneTree Count wrong");
        var after = Remove(root, 2)!;                                  // 2 gone → 1 | 3
        if (!Leaves(after).SequenceEqual(new uint[] { 1, 3 }))
            throw new System.InvalidOperationException("PaneTree Remove/promote wrong");
        if (Remove(new Leaf(9), 9) is not null)
            throw new System.InvalidOperationException("PaneTree Remove last leaf should be null");
    }
}
