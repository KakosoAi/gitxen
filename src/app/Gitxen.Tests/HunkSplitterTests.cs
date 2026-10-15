// SPDX-License-Identifier: GPL-3.0-or-later
using AwesomeAssertions;
using Gitxen.Diff;
using NUnit.Framework;

namespace Gitxen.Tests;

[TestFixture]
public sealed class HunkSplitterTests
{
    private const string TwoHunks =
        "diff --git a/a.txt b/a.txt\nindex 1..2 100644\n--- a/a.txt\n+++ b/a.txt\n" +
        "@@ -1,2 +1,2 @@\n-a\n+b\n c\n" +
        "@@ -10,2 +10,2 @@\n-x\n+y\n z\n";

    [Test]
    public void Split_should_return_one_self_contained_patch_per_hunk()
    {
        IReadOnlyList<DiffHunk> hunks = HunkSplitter.Split(TwoHunks);

        hunks.Should().HaveCount(2);
        hunks[0].Patch.Should().StartWith("diff --git a/a.txt b/a.txt").And.Contain("+b").And.NotContain("+y");
        hunks[1].Patch.Should().StartWith("diff --git a/a.txt b/a.txt").And.Contain("+y").And.NotContain("+b");
        hunks[1].Header.Should().StartWith("@@ -10,2 +10,2 @@");
    }

    [Test]
    public void Split_should_return_nothing_for_a_diff_without_hunks()
    {
        HunkSplitter.Split("Binary files a/x.png and b/x.png differ").Should().BeEmpty();
        HunkSplitter.Split("").Should().BeEmpty();
    }
}
