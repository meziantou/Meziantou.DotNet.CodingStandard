using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using NuGet.Versioning;

namespace Meziantou.DotNet.CodingStandard.Tests;

internal static class DotnetVersions
{
    private static readonly ConcurrentDictionary<string, Task<string>> Cache = new(StringComparer.Ordinal);

    public static Task<string> GetLatestVersionAsync(string channel)
    {
        return Cache.GetOrAdd(channel, GetLatestVersionCore);

        static async Task<string> GetLatestVersionCore(string channel)
        {
            var channelData = await SharedHttpClient.Instance.GetFromJsonAsync<ChannelData>($"https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/{channel}/releases.json");
            return channelData.Releases.Where(r => r.Sdk.Version is not null).OrderBy(r => SemanticVersion.Parse(r.Sdk.Version)).Last().Sdk.Version;
        }
    }

    private sealed class DotNetReleaseEntry
    {
        [JsonPropertyName("releases.json")]
        public string ReleaseJson { get; set; } = null!;
    }

    private sealed class ChannelData
    {
        [JsonPropertyName("channel-version")]
        public string ChannelVersion { get; set; } = default!;

        [JsonPropertyName("releases")]
        public ChannelRelease[] Releases { get; set; } = default!;
    }

    private sealed class ChannelRelease
    {
        [JsonPropertyName("sdk")]
        public ChannelReleaseSdk Sdk { get; set; }

        [JsonPropertyName("release-notes")]
        public string ReleaseNotes { get; set; }

        [JsonPropertyName("release-date")]
        public DateOnly ReleaseDate { get; set; }
    }

    private sealed class ChannelReleaseSdk
    {
        [JsonPropertyName("version")]
        public string Version { get; set; }
    }
}
