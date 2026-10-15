// SPDX-License-Identifier: GPL-3.0-or-later
namespace Gitxen.Services;

/// <summary>git's speed-ups for this repository; the work is in <see cref="RepositorySpeed"/>, shared with <c>gx</c>.</summary>
internal sealed partial class RepositoryLoader
{
    public SpeedSettings ReadSpeedSettings() => RepositorySpeed.Read(_git, _workingDirectory);

    public GitOperationResult ApplySpeedUps(bool fileWatcher) => RepositorySpeed.Apply(_git, fileWatcher);

    public GitOperationResult StopFileWatcher() => RepositorySpeed.StopFileWatcher(_git);
}
