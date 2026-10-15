// SPDX-License-Identifier: GPL-3.0-or-later
namespace Gitxen.Services;

/// <summary>The reads behind the Overview page; the work is in <see cref="OverviewReader"/>, shared with <c>gx</c>.</summary>
internal sealed partial class RepositoryLoader
{
    public RepositoryOverview ReadOverview(RepositoryOverview? previous) => OverviewReader.Read(_git, previous);
}
