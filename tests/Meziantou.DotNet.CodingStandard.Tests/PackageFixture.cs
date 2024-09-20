using System.Diagnostics;
using Meziantou.Framework;

namespace Meziantou.DotNet.CodingStandard.Tests;

public sealed class PackageFixture : IAsyncLifetime
{
    private readonly TemporaryDirectory _packageDirectory = TemporaryDirectory.Create();

    public FullPath PackageDirectory => _packageDirectory.FullPath;

    public async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("CI") != null && Environment.GetEnvironmentVariable("NuGetDirectory") is { } path)
        {
            var files = Directory.GetFiles(path, "*.nupkg");
            if (files.Length > 0)
            {
                foreach (var file in files)
                {
                    File.Copy(file, _packageDirectory.FullPath / Path.GetFileName(file));
                }

                return;
            }
        }

        // Build NuGet package
        var nugetPath = FullPath.GetTempPath() / $"nuget-{Guid.NewGuid()}.exe";
        await DownloadFileAsync("https://dist.nuget.org/win-x86-commandline/latest/nuget.exe", nugetPath);
        var nuspecPath = PathHelpers.GetRootDirectory() / "Meziantou.DotNet.CodingStandard.nuspec";

        var psi = new ProcessStartInfo(nugetPath);
        psi.ArgumentList.AddRange(["pack", nuspecPath, "-ForceEnglishOutput", "-Version", "999.9.9", "-OutputDirectory", _packageDirectory.FullPath]);
        await psi.RunAsTaskAsync();
    }

    public async Task DisposeAsync()
    {
        await _packageDirectory.DisposeAsync();
    }

    private static async Task DownloadFileAsync(string url, FullPath path)
    {
        path.CreateParentDirectory();
        await using var nugetStream = await SharedHttpClient.Instance.GetStreamAsync(url);
        await using var fileStream = File.Create(path);
        await nugetStream.CopyToAsync(fileStream);
    }
}
