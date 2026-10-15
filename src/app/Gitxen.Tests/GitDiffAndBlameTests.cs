// SPDX-License-Identifier: GPL-3.0-or-later
using AwesomeAssertions;
using Gitxen.Git;
using NUnit.Framework;

namespace Gitxen.Tests;

[TestFixture]
public sealed class GitDiffAndBlameTests
{
    [Test]
    public void ParseChanges_should_read_a_rename_as_old_then_new_path()
    {
        IReadOnlyList<GitChange> changes = GitDiff.ParseChanges("M\0a.txt\0R100\0old.txt\0new.txt\0A\0n.txt\0");

        changes.Should().HaveCount(3);
        changes[1].Should().Be(new GitChange("new.txt", "old.txt", GitChangeKind.Renamed, IsStaged: false));
        changes[2].Kind.Should().Be(GitChangeKind.Added);
    }

    [Test]
    public void Blame_Parse_should_read_a_line_with_its_commit()
    {
        string id = new('a', 40);
        string porcelain =
            $"{id} 1 1 1\nauthor Ada\nauthor-mail <ada@example.com>\nauthor-time 1700000000\nauthor-tz +0000\n" +
            "committer Ada\ncommitter-time 1700000000\nsummary first commit\nfilename a.txt\n\thello\n";

        IReadOnlyList<GitBlameLine> lines = GitBlame.Parse(porcelain);

        GitBlameLine line = lines.Should().ContainSingle().Subject;
        line.LineNumber.Should().Be(1);
        line.Author.Should().Be("Ada");
        line.Summary.Should().Be("first commit");
        line.Text.Should().Be("hello");
        line.IsUncommitted.Should().BeFalse();
    }

    [Test]
    public void Blame_Parse_should_not_mistake_file_content_for_a_header()
    {
        string id = new('b', 40);
        string porcelain = $"{id} 1 1 1\nauthor Ada\nauthor-time 1\nsummary s\nfilename a.txt\n\tauthor Mallory\n";

        GitBlame.Parse(porcelain).Should().ContainSingle().Which.Author.Should().Be("Ada");
    }

    [Test]
    public void Blame_Parse_should_flag_uncommitted_lines()
    {
        string id = new('0', 40);
        string porcelain = $"{id} 1 1 1\nauthor Not Committed Yet\nauthor-time 1\nsummary s\nfilename a.txt\n\tnew line\n";

        GitBlame.Parse(porcelain).Should().ContainSingle().Which.IsUncommitted.Should().BeTrue();
    }
}
