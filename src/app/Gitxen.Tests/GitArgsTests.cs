// SPDX-License-Identifier: GPL-3.0-or-later
using AwesomeAssertions;
using Gitxen.Git;
using NUnit.Framework;

namespace Gitxen.Tests;

[TestFixture]
public sealed class GitArgsTests
{
    [Test]
    public void AddPaths_should_end_options_and_use_forward_slashes()
    {
        GitArgs args = new GitArgs("add").AddPaths("src\\a.cs", "b.txt");

        args.Values.Should().Equal("add", "--", "src/a.cs", "b.txt");
    }

    [Test]
    public void AddIf_should_add_only_when_the_condition_holds()
    {
        new GitArgs("log").AddIf(false, "--all").Values.Should().Equal("log");
        new GitArgs("log").AddIf(true, "--all").Values.Should().Equal("log", "--all");
    }

    [Test]
    public void AddIfNotEmpty_should_skip_blank_values()
    {
        new GitArgs("log").AddIfNotEmpty("  ").Values.Should().Equal("log");
        new GitArgs("log").AddIfNotEmpty("main").Values.Should().Equal("log", "main");
    }

    [Test]
    public void ToString_should_quote_values_with_spaces_for_display_only()
    {
        GitArgs args = new GitArgs("commit").Add("-m", "two words");

        args.ToString().Should().Be("commit -m \"two words\"");
        args.Values.Should().Contain("two words");
    }
}
