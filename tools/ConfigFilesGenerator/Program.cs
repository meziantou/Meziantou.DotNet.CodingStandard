﻿using System.Globalization;
using System.Runtime.Loader;
using System.Xml.Linq;
using Meziantou.Framework;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Frameworks;
using NuGet.Packaging.Core;
using NuGet.Packaging.Signing;
using NuGet.Protocol.Core.Types;
using NuGet.Protocol;
using NuGet.Versioning;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

var rootFolder = GetRootFolderPath();

var writtenFiles = 0;
var packages = await GetAllReferencedNuGetPackages();
await Parallel.ForEachAsync(packages, async (item, cancellationToken) =>
{
    var (packageId, packageVersion) = item;

    Console.WriteLine(packageId + "@" + packageVersion);
    var configurationFilePath = rootFolder / "src" / "configuration" / ("Analyzer." + packageId + ".editorconfig");

    var rules = new HashSet<AnalyzerRule>();
    foreach (var assembly in await GetAnalyzerReferences(packageId, packageVersion))
    {
        foreach (var type in assembly.GetTypes())
        {
            if (type.IsAbstract || type.IsInterface)
                continue;

            if (!typeof(DiagnosticAnalyzer).IsAssignableFrom(type))
                continue;

            var analyzer = (DiagnosticAnalyzer)Activator.CreateInstance(type)!;
            foreach (var diagnostic in analyzer.SupportedDiagnostics)
            {
                rules.Add(new AnalyzerRule(diagnostic.Id, diagnostic.Title.ToString(CultureInfo.InvariantCulture).Trim(), diagnostic.HelpLinkUri, diagnostic.IsEnabledByDefault, diagnostic.DefaultSeverity, diagnostic.IsEnabledByDefault ? diagnostic.DefaultSeverity : null));
            }
        }
    }

    if (rules.Count > 0)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# global_level must be higher than the NET Analyzer files");
        sb.AppendLine("is_global = true");
        sb.AppendLine("global_level = 0");

        var currentConfiguration = GetConfiguration(configurationFilePath);

        if (currentConfiguration.Unknowns.Length > 0)
        {
            foreach (var unknown in currentConfiguration.Unknowns)
            {
                sb.AppendLine(unknown);
            }
        }
        else
        {
            sb.AppendLine();
        }

        foreach (var rule in rules.OrderBy(rule => rule.Id))
        {
            var currentRuleConfiguration = currentConfiguration.Rules.FirstOrDefault(r => r.Id == rule.Id);
            var severity = currentRuleConfiguration != null ? currentRuleConfiguration.Severity : rule.DefaultEffectiveSeverity;

            sb.AppendLine($"# {rule.Id}: {rule.Title}");
            if (!string.IsNullOrEmpty(rule.Url))
            {
                sb.AppendLine($"# Help link: {rule.Url}");
            }

            sb.AppendLine($"# Enabled: {rule.Enabled}, Severity: {GetSeverity(rule.DefaultSeverity)}");

            if (currentRuleConfiguration?.Comments.Length > 0)
            {
                foreach (var comment in currentRuleConfiguration.Comments)
                {
                    sb.AppendLine(comment);
                }
            }

            sb.AppendLine($"dotnet_diagnostic.{rule.Id}.severity = {GetSeverity(severity)}");
            sb.AppendLine();
        }

        var text = sb.ToString().ReplaceLineEndings("\n");
        if (File.Exists(configurationFilePath))
        {
            if (File.ReadAllText(configurationFilePath).ReplaceLineEndings("\n") == text)
                return;
        }

        configurationFilePath.CreateParentDirectory();
        await File.WriteAllTextAsync(configurationFilePath, text, cancellationToken);
        Interlocked.Increment(ref writtenFiles);

        static string GetSeverity(DiagnosticSeverity? severity)
        {
            return severity switch
            {
                null => "none",
                DiagnosticSeverity.Hidden => "silent",
                DiagnosticSeverity.Info => "suggestion",
                DiagnosticSeverity.Warning => "warning",
                DiagnosticSeverity.Error => "error",
                _ => throw new Exception($"Severity '{severity}' is not supported"),
            };
        }
    }
});

return writtenFiles;


async Task<(string Id, NuGetVersion Version)[]> GetAllReferencedNuGetPackages()
{
    var foundPackages = new HashSet<SourcePackageDependencyInfo>();

    var cache = new SourceCacheContext();
    var repository = Repository.Factory.GetCoreV3("https://api.nuget.org/v3/index.json");
    var resource = await repository.GetResourceAsync<PackageMetadataResource>();

    foreach (var package in GetReferencedNuGetPackages())
    {
        // Find the latest version if no version is specified
        var version = package.Version is null ? null : NuGetVersion.Parse(package.Version);
        if (version is null)
        {
            var metadata = await resource.GetMetadataAsync(package.Id, includePrerelease: true, includeUnlisted: false, cache, NullLogger.Instance, CancellationToken.None);
            version = metadata.MaxBy(metadata => metadata.Identity.Version)!.Identity.Version;
        }

        var packageIdentity = new PackageIdentity(package.Id, version);
        await ListAllPackageDependencies(packageIdentity, [repository], NuGetFramework.AnyFramework, cache, NullLogger.Instance, foundPackages, CancellationToken.None);
    }

    return foundPackages.Select(p => (p.Id, p.Version)).ToArray();

    static async Task ListAllPackageDependencies(
        PackageIdentity package,
        IEnumerable<SourceRepository> repositories,
        NuGetFramework framework,
        SourceCacheContext cache,
        ILogger logger,
        HashSet<SourcePackageDependencyInfo> dependencies,
        CancellationToken cancellationToken)
    {
        if (dependencies.Contains(package))
        {
            return;
        }

        foreach (var repository in repositories)
        {
            var dependencyInfoResource = await repository.GetResourceAsync<DependencyInfoResource>();
            var dependencyInfo = await dependencyInfoResource.ResolvePackage(package, framework, cache, logger, cancellationToken);

            if (dependencyInfo == null)
            {
                continue;
            }

            if (dependencies.Add(dependencyInfo))
            {
                foreach (var dependency in dependencyInfo.Dependencies)
                {
                    await ListAllPackageDependencies(
                        new PackageIdentity(dependency.Id, dependency.VersionRange.MinVersion),
                        repositories,
                        framework,
                        cache,
                        logger,
                        dependencies,
                        cancellationToken);
                }
            }
        }
    }
}

IEnumerable<(string Id, string? Version)> GetReferencedNuGetPackages()
{
    var nuspecPath = rootFolder / "Meziantou.DotNet.CodingStandard.nuspec";
    var document = XDocument.Load(nuspecPath);
    var ns = document.Root!.Name.Namespace;
    foreach (var value in document.Descendants(ns + "dependency").Select(node => (node.Attribute("id")!.Value, node.Attribute("version")!.Value)))
    {
        yield return value;
    }
    
    // Add analyzers from the .NET SDK
    foreach (var package in new[] { "Microsoft.CodeAnalysis.NetAnalyzers", /* "Microsoft.CodeAnalysis.CSharp.CodeStyle" */ })
    {
        yield return (package, null);
    }
}

static FullPath GetRootFolderPath()
{
    var path = FullPath.CurrentDirectory();
    while (!path.IsEmpty)
    {
        if (Directory.Exists(path / ".git"))
            return path;

        path = path.Parent;
    }

    if (path.IsEmpty)
        throw new InvalidOperationException("Cannot find the root folder");

    return path;
}

static async Task<Assembly[]> GetAnalyzerReferences(string packageId, NuGetVersion  version)
{
    ILogger logger = NullLogger.Instance;
    CancellationToken cancellationToken = CancellationToken.None;

    var settings = Settings.LoadDefaultSettings(null);
    var globalPackagesFolder = SettingsUtility.GetGlobalPackagesFolder(settings);
    var source = "https://api.nuget.org/v3/index.json";

    var cache = new SourceCacheContext();
    var repository = Repository.Factory.GetCoreV3(source);
    var resource = await repository.GetResourceAsync<FindPackageByIdResource>();

    var package = GlobalPackagesFolderUtility.GetPackage(new PackageIdentity(packageId, version), globalPackagesFolder);
    if (package is null || package.Status is DownloadResourceResultStatus.NotFound)
    {
        // Download the package
        using var packageStream = new MemoryStream();
        await resource.CopyNupkgToStreamAsync(
            packageId,
            version,
            packageStream,
            cache,
            logger,
            cancellationToken);

        packageStream.Seek(0, SeekOrigin.Begin);

        // Add it to the global package folder
        package = await GlobalPackagesFolderUtility.AddPackageAsync(
            source,
            new PackageIdentity(packageId, version),
            packageStream,
            globalPackagesFolder,
            parentId: Guid.Empty,
            ClientPolicyContext.GetClientPolicy(settings, logger),
            logger,
            cancellationToken);
    }

    var result = new List<Assembly>();
    var groups = package.PackageReader.GetFiles("analyzers").GroupBy(Path.GetDirectoryName).ToArray();
    foreach (var group in groups)
    {
        var context = new AssemblyLoadContext(null);
        foreach (var file in group)
        {
            try
            {
                using var stream = package.PackageReader.GetStream(file);
                result.Add(context.LoadFromStream(stream));
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
        }
    }

    return result.ToArray();
}

static (AnalyzerConfiguration[] Rules, string[] Unknowns) GetConfiguration(FullPath editorconfig)
{
    var rules = new List<AnalyzerConfiguration>();
    var unknowns = new List<string>();

    var currentComment = new List<string>();
    try
    {
        var lines = File.ReadAllLines(editorconfig);

        foreach (var line in lines)
        {
            try
            {
                if (line.StartsWith('#'))
                {
                    if (line.StartsWith("# Enabled: ", StringComparison.Ordinal))
                        continue;

                    if (line.StartsWith("# Default severity: ", StringComparison.Ordinal))
                        continue;

                    if (line.StartsWith("# Help link: ", StringComparison.Ordinal))
                        continue;

                    currentComment.Add(line);
                    continue;
                }

                if (line.StartsWith("is_global", StringComparison.Ordinal))
                    continue;

                if (line.StartsWith("global_level", StringComparison.Ordinal))
                    continue;

                var match = Regex.Match(line, @"^dotnet_diagnostic\.(?<RuleId>[a-zA-Z0-9]+).severity\s*=\s*(?<Severity>[a-z]+)");
                if (match.Success)
                {
                    DiagnosticSeverity? diagnosticSeverity = null;
                    var severityValue = match.Groups["Severity"].Value;
                    if (severityValue == "silent")
                    {
                        diagnosticSeverity = DiagnosticSeverity.Hidden;
                    }
                    else if (severityValue == "suggestion")
                    {
                        diagnosticSeverity = DiagnosticSeverity.Info;
                    }
                    else if (Enum.TryParse<DiagnosticSeverity>(severityValue, ignoreCase: true, out var severity))
                    {
                        diagnosticSeverity = severity;
                    }

                    rules.Add(new AnalyzerConfiguration(match.Groups["RuleId"].Value, currentComment.Skip(1).ToArray(), diagnosticSeverity));
                }
                else
                {
                    foreach (var comment in currentComment)
                    {
                        unknowns.Add(comment);
                    }

                    if (rules.Count == 0 || !string.IsNullOrEmpty(line))
                    {
                        unknowns.Add(line);
                    }
                }

            }
            finally
            {
                if (!line.StartsWith('#'))
                {
                    currentComment.Clear();
                }
            }
        }
    }
    catch
    {
    }

    return (rules.ToArray(), unknowns.ToArray());
}

sealed record AnalyzerConfiguration(string Id, string[] Comments, DiagnosticSeverity? Severity);

sealed record AnalyzerRule(string Id, string Title, string? Url, bool Enabled, DiagnosticSeverity DefaultSeverity, DiagnosticSeverity? DefaultEffectiveSeverity);