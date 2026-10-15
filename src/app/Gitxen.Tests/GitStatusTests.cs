// SPDX-License-Identifier: GPL-3.0-or-later
using AwesomeAssertions;
using Gitxen.Git;
using NUnit.Framework;

namespace Gitxen.Tests;

[TestFixture]
public sealed class GitStatusTests
{
    [Test]
    public void Parse_should_return_nothing_for_empty_output()
    {
        GitStatus.Parse("").Should().BeEmpty();
    }

    [Test]
    public void Parse_should_read_an_unstaged_modification()
    {
        IReadOnlyList<GitChange> changes = GitStatus.Parse("1 .M N... 100644 100644 100644 aaa bbb src/a.cs\0");

        changes.Should().ContainSingle().Which.Should().Be(new GitChange("src/a.cs", null, GitChangeKind.Modified, IsStaged: false));
    }

    [Test]
    public void Parse_should_split_a_file_changed_on_both_sides_into_two_entries()
    {
        IReadOnlyList<GitChange> changes = GitStatus.Parse("1 MM N... 100644 100644 100644 aaa bbb a.txt\0");

        changes.Should().HaveCount(2);
        changes.Should().Contain(change => change.IsStaged && change.Kind == GitChangeKind.Modified);
        changes.Should().Contain(change => !change.IsStaged && change.Kind == GitChangeKind.Modified);
    }

    [Test]
    public void Parse_should_take_a_renames_original_path_from_the_next_record()
    {
        IReadOnlyList<GitChange> changes = GitStatus.Parse("2 R. N... 100644 100644 100644 aaa bbb R100 new name.txt\0old name.txt\0? other.txt\0");

        changes.Should().HaveCount(2);
        changes[0].Should().Be(new GitChange("new name.txt", "old name.txt", GitChangeKind.Renamed, IsStaged: true));
        changes[1].Should().Be(new GitChange("other.txt", null, GitChangeKind.Untracked, IsStaged: false));
    }

    [Test]
    public void Parse_should_keep_spaces_in_a_path()
    {
        IReadOnlyList<GitChange> changes = GitStatus.Parse("1 A. N... 000000 100644 100644 aaa bbb my folder/my file.txt\0");

        changes.Should().ContainSingle().Which.Path.Should().Be("my folder/my file.txt");
    }

    [Test]
    public void Parse_should_report_a_conflict_as_one_unstaged_entry()
    {
        IReadOnlyList<GitChange> changes = GitStatus.Parse("u UU N... 100644 100644 100644 100644 a b c conflict.txt\0");

        changes.Should().ContainSingle().Which.Should().Be(new GitChange("conflict.txt", null, GitChangeKind.Conflicted, IsStaged: false));
    }

    [Test]
    public void Parse_should_skip_headers_and_ignored_files()
    {
        GitStatus.Parse("# branch.head main\0! ignored.log\0").Should().BeEmpty();
    }
}
