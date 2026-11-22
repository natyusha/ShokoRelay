using System.Text.Json;

namespace ShokoRelay.Config;

public partial class ConfigProvider
{
    #region Plex Secrets & Tokens

    /// <summary>Data structure representing saved Plex tokens and server discovery details on disk.</summary>
    private sealed class TokenFile
    {
        /// <summary>The saved Plex authentication token.</summary>
        public string? Token { get; set; }

        /// <summary>The unique client identifier for this installation.</summary>
        public string? ClientIdentifier { get; set; }

        /// <summary>The cached username of the Plex account admin.</summary>
        public string? AdminUsername { get; set; }

        /// <summary>List of discovered Plex servers.</summary>
        public List<PlexAvailableServer>? Servers { get; set; }

        /// <summary>List of discovered Plex libraries.</summary>
        public List<PlexAvailableLibrary>? Libraries { get; set; }

        /// <summary>The auto-detected base URL of the Shoko server.</summary>
        public string? AutoShokoServerUrl { get; set; }
    }

    /// <summary>Deletes the Plex token/secrets file from disk.</summary>
    public void DeleteTokenFile()
    {
        try
        {
            lock (_settingsLock)
            {
                Volatile.Write(ref _suppressTokenWatcherUntil, Environment.TickCount64 + 500);
                if (File.Exists(_tokenPath))
                    File.Delete(_tokenPath);
                _cachedTokenFile = null;
                _cachedServers = null;
                _cachedAdminUsername = null;
            }
        }
        catch (Exception exception)
        {
            _logger?.LogWarning(exception, "Config: Failed to delete token file");
        }
    }

    /// <summary>Retrieves the saved Plex authentication token.</summary>
    /// <returns>The Plex authentication token string.</returns>
    public string GetPlexToken() => ReadTokenFile().Token ?? "";

    /// <summary>Retrieves or generates the unique Plex client identifier.</summary>
    /// <returns>The Plex client identifier string.</returns>
    public string GetPlexClientIdentifier()
    {
        lock (_settingsLock)
        {
            var tokenFile = ReadTokenFile();
            if (string.IsNullOrWhiteSpace(tokenFile.ClientIdentifier))
            {
                tokenFile.ClientIdentifier = Guid.NewGuid().ToString("N");
                WriteTokenFile(tokenFile.Token, tokenFile.ClientIdentifier, tokenFile.AdminUsername, tokenFile.Servers, tokenFile.Libraries, tokenFile.AutoShokoServerUrl);
            }
            return tokenFile.ClientIdentifier;
        }
    }

    /// <summary>Retrieves the list of discovered Plex servers from the token file.</summary>
    /// <returns>A list of <see cref="PlexAvailableServer"/> instances.</returns>
    public List<PlexAvailableServer> GetPlexDiscoveredServers()
    {
        lock (_settingsLock)
            return _cachedServers ??= ReadTokenFile().Servers ?? [];
    }

    /// <summary>Retrieves the list of discovered Plex libraries from the token file.</summary>
    /// <returns>A list of <see cref="PlexAvailableLibrary"/> instances.</returns>
    public List<PlexAvailableLibrary> GetPlexDiscoveredLibraries() => ReadTokenFile().Libraries ?? [];

    /// <summary>Retrieves the cached Plex admin username.</summary>
    /// <returns>The admin username, or null if not yet discovered.</returns>
    public string? GetAdminUsername()
    {
        lock (_settingsLock)
            return _cachedAdminUsername ??= ReadTokenFile().AdminUsername;
    }

    /// <summary>Refreshes the admin username from the Plex API and updates the local storage.</summary>
    /// <param name="auth">Plex authentication service.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the refresh operation.</returns>
    public async Task RefreshAdminUsername(PlexAuth auth, CancellationToken cancellationToken)
    {
        if (await auth.GetAccountInfoAsync(GetPlexToken(), cancellationToken) is { } info)
        {
            _cachedAdminUsername = info.Title ?? info.Username;
            UpdatePlexTokenInfo(adminName: _cachedAdminUsername);
        }
    }

    /// <summary>Updates specific fields in the Plex token/secrets file.</summary>
    /// <param name="token">Optional new Plex token.</param>
    /// <param name="clientIdentifier">Optional new client identifier.</param>
    /// <param name="adminName">Optional new admin username.</param>
    /// <param name="servers">Optional list of discovered servers.</param>
    /// <param name="libraries">Optional list of discovered libraries.</param>
    /// <param name="autoUrl">Optional auto-detected Shoko Server URL.</param>
    public void UpdatePlexTokenInfo(
        string? token = null,
        string? clientIdentifier = null,
        string? adminName = null,
        List<PlexAvailableServer>? servers = null,
        List<PlexAvailableLibrary>? libraries = null,
        string? autoUrl = null
    )
    {
        lock (_settingsLock)
        {
            var existing = ReadTokenFile();
            WriteTokenFile(
                token ?? existing.Token,
                clientIdentifier ?? existing.ClientIdentifier,
                adminName ?? existing.AdminUsername,
                servers ?? existing.Servers,
                libraries ?? existing.Libraries,
                autoUrl ?? existing.AutoShokoServerUrl
            );
        }
    }

    /// <summary>Checks if the provided server UUID matches a managed server known to the provider.</summary>
    /// <param name="uuid">The server UUID to check.</param>
    /// <returns>True if the server is in the discovered list.</returns>
    public bool IsManagedServer(string? uuid) => !string.IsNullOrWhiteSpace(uuid) && GetPlexDiscoveredServers().Any(server => string.Equals(server.Id, uuid, StringComparison.OrdinalIgnoreCase));

    #endregion

    #region User Management

    /// <summary>Represents a configured mapping between a Shoko username and one or more Plex users.</summary>
    /// <param name="ShokoUser">The target Shoko username.</param>
    /// <param name="PlexUsers">The Plex usernames and optional 4-digit PINs assigned to this Shoko user.</param>
    public sealed record AdvancedUserMappingEntry(string ShokoUser, List<(string PlexUser, string? Pin)> PlexUsers);

    /// <summary>Parse a comma-separated string of Plex user entries, optionally containing 4-digit PINs.</summary>
    /// <param name="rawInput">The raw configuration string to parse.</param>
    /// <returns>A list of tuples containing the username and optional PIN.</returns>
    public static List<(string Name, string? Pin)> ParseExtraPlexUsers(string? rawInput) =>
        [.. (rawInput ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(ParseUserAndPin).Where(entry => !string.IsNullOrWhiteSpace(entry.Name))];

    /// <summary>Parses a pipe-delimited string of Shoko-to-Plex user mappings (<c>ShokoUser,PlexUser1;PIN,PlexUser2|ShokoUser2,PlexUser3</c>).</summary>
    /// <param name="rawInput">The raw configuration string to parse.</param>
    /// <returns>A list of parsed <see cref="AdvancedUserMappingEntry"/> records.</returns>
    public static List<AdvancedUserMappingEntry> ParseAdvancedUserMappings(string? rawInput)
    {
        var entries = new List<AdvancedUserMappingEntry>();
        var seenPlexUsers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var segment in (rawInput ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var tokens = segment.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (tokens.Length < 2 || string.IsNullOrWhiteSpace(tokens[0]))
                continue;

            string shokoUser = tokens[0].Trim();
            var plexUsers = new List<(string PlexUser, string? Pin)>();
            for (int tokenIndex = 1; tokenIndex < tokens.Length; tokenIndex++)
            {
                var (plexUser, pin) = ParseUserAndPin(tokens[tokenIndex]);
                if (string.IsNullOrWhiteSpace(plexUser) || !seenPlexUsers.Add(plexUser))
                    continue;

                plexUsers.Add((plexUser, pin));
            }

            if (plexUsers.Count > 0)
                entries.Add(new AdvancedUserMappingEntry(shokoUser, plexUsers));
        }

        return entries;
    }

    /// <summary>Returns the parsed and cached list of extra Plex users configured in settings.</summary>
    /// <returns>A list of extra user name and PIN tuples.</returns>
    public List<(string Name, string? Pin)> GetExtraPlexUserEntries()
    {
        lock (_settingsLock)
            return _cachedExtraUsers ??= ParseExtraPlexUsers(GetSettings().Automation.ExtraPlexUsers);
    }

    /// <summary>Returns the parsed and cached list of Shoko-to-Plex user mappings configured in settings.</summary>
    /// <returns>A list of <see cref="AdvancedUserMappingEntry"/> records.</returns>
    public List<AdvancedUserMappingEntry> GetAdvancedUserMappingEntries()
    {
        lock (_settingsLock)
            return _cachedAdvancedUserMappings ??= ParseAdvancedUserMappings(GetSettings().Automation.AdvancedUserMappings);
    }

    /// <summary>Resolves the mapped Shoko username for a given Plex user, checking both literal names and the Plex admin alias.</summary>
    /// <param name="plexUser">The Plex username or <c>admin</c>.</param>
    /// <param name="adminUsername">Optional resolved Plex admin username.</param>
    /// <param name="isOwner">Whether the caller represents the Plex server owner/admin.</param>
    /// <returns>The mapped Shoko username, or <c>null</c> if unmapped.</returns>
    public string? ResolveMappedShokoUsername(string plexUser, string? adminUsername = null, bool isOwner = false)
    {
        foreach (var group in GetAdvancedUserMappingEntries())
        foreach (var (mappedPlex, _) in group.PlexUsers)
        {
            if ((isOwner || !string.Equals(mappedPlex, "admin", StringComparison.OrdinalIgnoreCase)) && string.Equals(mappedPlex, plexUser, StringComparison.OrdinalIgnoreCase))
                return group.ShokoUser;
            if (
                isOwner
                && (
                    string.Equals(mappedPlex, "admin", StringComparison.OrdinalIgnoreCase)
                    || (!string.IsNullOrWhiteSpace(adminUsername) && string.Equals(mappedPlex, adminUsername, StringComparison.OrdinalIgnoreCase))
                )
            )
                return group.ShokoUser;
        }
        return null;
    }

    #endregion

    #region Internal Helpers

    /// <summary>Reads and deserializes the Plex token secrets file from disk.</summary>
    /// <returns>A populated <see cref="TokenFile"/> instance or a new empty structure on failure.</returns>
    private TokenFile ReadTokenFile()
    {
        lock (_settingsLock)
        {
            if (_cachedTokenFile != null)
                return _cachedTokenFile;
            try
            {
                return _cachedTokenFile = File.Exists(_tokenPath)
                    ? JsonSerializer.Deserialize<TokenFile>(ReadFileWithRetry(_tokenPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new()
                    : new();
            }
            catch
            {
                return new();
            }
        }
    }

    /// <summary>Serializes and writes updated Plex token and discovery details to disk.</summary>
    /// <param name="token">Plex token.</param>
    /// <param name="clientIdentifier">Client identifier.</param>
    /// <param name="adminUsername">Admin username.</param>
    /// <param name="servers">Discovered servers list.</param>
    /// <param name="libraries">Discovered libraries list.</param>
    /// <param name="autoUrl">Auto-detected Shoko Server URL.</param>
    private void WriteTokenFile(string? token, string? clientIdentifier, string? adminUsername, List<PlexAvailableServer>? servers = null, List<PlexAvailableLibrary>? libraries = null, string? autoUrl = null)
    {
        var tokenFile = new TokenFile
        {
            Token = token ?? "",
            ClientIdentifier = clientIdentifier ?? "",
            AdminUsername = adminUsername ?? "",
            Servers = servers ?? [],
            Libraries = libraries ?? [],
            AutoShokoServerUrl = autoUrl ?? "",
        };
        lock (_settingsLock)
        {
            Volatile.Write(ref _suppressTokenWatcherUntil, Environment.TickCount64 + 500);
            VfsShared.WriteTextAtomic(_tokenPath, JsonSerializer.Serialize(tokenFile, s_options));
            _cachedTokenFile = tokenFile;
            _cachedServers = tokenFile.Servers;
            _cachedAdminUsername = tokenFile.AdminUsername;
        }
    }

    /// <summary>Parses a single user token containing a username and optional semicolon-delimited 4-digit PIN.</summary>
    /// <param name="rawEntry">The raw user entry string.</param>
    /// <returns>A tuple containing the trimmed username and validated 4-digit PIN, or <c>null</c> if absent.</returns>
    private static (string Name, string? Pin) ParseUserAndPin(string rawEntry)
    {
        var parts = rawEntry.Split(';', 2);
        string name = parts[0].Trim();
        string? pin = parts.Length > 1 && parts[1].Trim() is { Length: 4 } trimmedPin && trimmedPin.All(char.IsDigit) ? trimmedPin : null;
        return (name, pin);
    }

    #endregion
}
