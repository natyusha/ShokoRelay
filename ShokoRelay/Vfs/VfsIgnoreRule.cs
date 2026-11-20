using Shoko.Abstractions.Video;
using Shoko.Abstractions.Video.Services;

namespace ShokoRelay.Vfs;

/// <summary>Automatically ignores Shoko Relay's internal VFS and local asset directories during Shoko's import scans.</summary>
public class VfsIgnoreRule(IVideoService videoService, ConfigProvider configProvider) : IManagedFolderIgnoreRule
{
    #region Ignore Rule

    /// <inheritdoc/>
    public string Name => "Shoko Relay Ignore Rule";

    /// <inheritdoc/>
    public bool ShouldIgnore(IManagedFolder folder, FileSystemInfo fileSystemInfo) =>
        VfsShared.IsPathIgnored(fileSystemInfo.FullName, videoService, configProvider.GetSettings(), relativeTo: folder?.Path, isDirectory: fileSystemInfo is DirectoryInfo);

    #endregion
}
