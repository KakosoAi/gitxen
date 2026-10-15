// SPDX-License-Identifier: GPL-3.0-or-later
using AwesomeAssertions;
using Gitxen.Services;
using NUnit.Framework;

namespace Gitxen.Tests;

[TestFixture]
public sealed class GitDirectoryTests
{
    private string _root = "";

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "gitxen-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
    {
        Directory.Delete(_root, recursive: true);
    }

    private string Repository(string head)
    {
        string git = Path.Combine(_root, ".git");
        Directory.CreateDirectory(git);
        File.WriteAllText(Path.Combine(git, "HEAD"), head);
        return _root;
    }

    [Test]
    public void IsRepository_should_accept_a_git_folder_with_a_head()
    {
        GitDirectory.IsRepository(Repository("ref: refs/heads/main\n")).Should().BeTrue();
    }

    [Test]
    public void IsRepository_should_reject_a_git_folder_without_a_head()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));

        GitDirectory.IsRepository(_root).Should().BeFalse();
    }

    [Test]
    public void IsRepository_should_accept_a_worktree_or_submodule_pointer()
    {
        File.WriteAllText(Path.Combine(_root, ".git"), "gitdir: ../main/.git/worktrees/feature\n");

        GitDirectory.IsRepository(_root).Should().BeTrue();
    }

    [Test]
    public void IsRepository_should_reject_a_git_file_that_is_not_a_pointer()
    {
        File.WriteAllText(Path.Combine(_root, ".git"), "something else\n");

        GitDirectory.IsRepository(_root).Should().BeFalse();
    }

    [Test]
    public void IsRepository_should_reject_a_plain_or_missing_folder()
    {
        GitDirectory.IsRepository(_root).Should().BeFalse();
        GitDirectory.IsRepository(Path.Combine(_root, "missing")).Should().BeFalse();
    }

    [Test]
    public void ReadBranch_should_return_the_checked_out_branch()
    {
        GitDirectory.ReadBranch(Repository("ref: refs/heads/feature/login\n")).Should().Be("feature/login");
    }

    [Test]
    public void ReadBranch_should_describe_a_detached_head()
    {
        GitDirectory.ReadBranch(Repository("3cdc9ec0a1b2c3d4e5f60718293a4b5c6d7e8f90\n")).Should().Be("detached at 3cdc9ec");
    }

    [Test]
    public void ReadBranch_should_return_nothing_for_a_pointer_or_a_plain_folder()
    {
        GitDirectory.ReadBranch(_root).Should().BeEmpty();

        File.WriteAllText(Path.Combine(_root, ".git"), "gitdir: elsewhere\n");
        GitDirectory.ReadBranch(_root).Should().BeEmpty();
    }
}
