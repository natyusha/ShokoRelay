using Shoko.Abstractions.FileSystem;
using Shoko.Abstractions.Video;
using Shoko.Abstractions.Video.Services;

namespace ShokoRelay.Vfs;

/// <summary>Automatically ignores Shoko Relay's internal VFS and local asset directories during Shoko's import scans.</summary>
/// <param name="videoService">Shoko video service.</param>
/// <param name="configProvider">Configuration provider.</param>
/// <param name="fileSystem">Shoko file system service.</param>
public class VfsIgnoreRule(IVideoService videoService, ConfigProvider configProvider, IFileSystem fileSystem) : IManagedFolderIgnoreRule
{
    #region Ignore Rule

    /// <inheritdoc/>
    public string Name => "Shoko Relay Ignore Rule";

    /// <inheritdoc/>
    public bool ShouldIgnore(IManagedFolder folder, FileSystemEntry entry) =>
        VfsShared.IsPathIgnored(entry.Path, videoService, configProvider.GetSettings(), relativeTo: folder?.Path, fileSystem: fileSystem, isDirectoryResolver: () => entry.IsDirectory);

    #endregion
}
