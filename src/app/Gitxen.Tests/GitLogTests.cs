// SPDX-License-Identifier: GPL-3.0-or-later
using AwesomeAssertions;
using Gitxen.Git;
using NUnit.Framework;

namespace Gitxen.Tests;

[TestFixture]
public sealed class GitLogTests
{
    private const char Sep = '\u001f';

    [Test]
    public void Parse_should_read_every_field()
    {
        string record = string.Join(Sep, "abcdef0123456789abcdef0123456789abcdef01", "p1 p2", "Ada Lovelace", "ada@example.com", "1700000000", "Subject line", "Body line\n");

        GitCommit? commit = GitLog.Parse(record);

        commit.Should().NotBeNull();
        commit!.Author.Should().Be("Ada Lovelace");
        commit.AuthorEmail.Should().Be("ada@example.com");
        commit.Subject.Should().Be("Subject line");
        commit.Body.Should().Be("Body line");
        commit.ShortId.Should().Be("abcdef01");
        commit.IsMerge.Should().BeTrue();
        commit.ParentIds.Should().Equal("p1", "p2");
    }

    [Test]
    public void Parse_should_treat_a_root_commit_as_having_no_parent()
    {
        string record = string.Join(Sep, "abc", "", "A", "a@b", "1", "root", "");

        GitLog.Parse(record)!.HasParent.Should().BeFalse();
    }

    [Test]
    public void Parse_should_keep_a_separator_inside_the_body_with_the_body()
    {
        string record = string.Join(Sep, "abc", "", "A", "a@b", "1", "s", "one" + Sep + "two");

        GitLog.Parse(record)!.Body.Should().Be("one" + Sep + "two");
    }

    [Test]
    public void Parse_should_return_null_for_something_that_is_not_a_record()
    {
        GitLog.Parse("").Should().BeNull();
    }
}
