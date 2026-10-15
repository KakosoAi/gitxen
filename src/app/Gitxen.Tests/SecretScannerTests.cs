// SPDX-License-Identifier: GPL-3.0-or-later
using AwesomeAssertions;
using Gitxen.Services;
using NUnit.Framework;

namespace Gitxen.Tests;

[TestFixture]
public sealed class SecretScannerTests
{
    // Assembled at run time so this file does not itself look like a leak.
    private static readonly string AwsKey = "AKIA" + "ABCDEFGHIJKLMNOP";

    [Test]
    public void ScanPatch_should_find_a_cloud_key_on_an_added_line()
    {
        string patch = $"+++ config.txt\n@@ -0,0 +1,2 @@\n+aws = {AwsKey}\n+nothing here\n";

        IReadOnlyList<ScanFinding> findings = SecretScanner.ScanPatch(patch);

        ScanFinding finding = findings.Should().ContainSingle().Subject;
        finding.Kind.Should().Be(FindingKind.Secret);
        finding.Path.Should().Be("config.txt");
        finding.Line.Should().Be(1);
        finding.Excerpt.Should().NotContain(AwsKey);
    }

    [Test]
    public void ScanPatch_should_ignore_removed_lines()
    {
        string patch = $"+++ config.txt\n@@ -1 +0,0 @@\n-aws = {AwsKey}\n";

        SecretScanner.ScanPatch(patch).Should().BeEmpty();
    }

    [Test]
    public void ScanPatch_should_honour_the_allow_marker()
    {
        string patch = $"+++ docs.md\n@@ -0,0 +1 @@\n+example {AwsKey} gitxen:allow-secret\n";

        SecretScanner.ScanPatch(patch).Should().BeEmpty();
    }

    [Test]
    public void ScanFiles_should_flag_sensitive_names_and_large_files()
    {
        IReadOnlyList<ScanFinding> findings = SecretScanner.ScanFiles(
        [
            (".env", 10L),
            (".env.example", 10L),
            ("data/big.bin", SecretScanner.LargeFileBytes + 1),
            ("src/a.cs", 100L)
        ]);

        findings.Should().HaveCount(2);
        findings.Should().Contain(finding => finding.Path == ".env" && finding.Kind == FindingKind.SensitiveFile);
        findings.Should().Contain(finding => finding.Path == "data/big.bin" && finding.Kind == FindingKind.LargeFile);
    }
}
