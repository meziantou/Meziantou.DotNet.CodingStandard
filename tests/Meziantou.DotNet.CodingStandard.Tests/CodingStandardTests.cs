using System.Diagnostics;
using System.IO.Compression;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Xml.Linq;
using Meziantou.Framework;
using Microsoft.Build.Logging.StructuredLogger;
using NuGet.Packaging;
using Task = System.Threading.Tasks.Task;

namespace Meziantou.DotNet.CodingStandard.Tests;

public sealed class CodingStandardTests(PackageFixture fixture, ITestOutputHelper testOutputHelper) : IClassFixture<PackageFixture>
{
    [Fact]
    public async Task ImplicitUsings()
    {
        await using var project = new ProjectBuilder(fixture, testOutputHelper, this);
        project.AddCsprojFile();
        project.AddFile("sample.cs", """_ = new StringBuilder();""");
        var data = await project.BuildAndGetOutput();
        Assert.False(data.HasError());
    }

    [Fact]
    public async Task BannedSymbolsAreReported()
    {
        await using var project = new ProjectBuilder(fixture, testOutputHelper, this);
        project.AddCsprojFile();
        project.AddFile("sample.cs", """_ = System.DateTime.Now;""");
        var data = await project.BuildAndGetOutput();
        Assert.True(data.HasWarning("RS0030"));

        var files = data.GetBinLogFiles();
        Assert.Contains(files, f => f.EndsWith("BannedSymbols.txt", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WarningsAsErrorOnGitHubActions()
    {
        await using var project = new ProjectBuilder(fixture, testOutputHelper, this);
        project.AddCsprojFile();
        project.AddFile("sample.cs", """_ = System.DateTime.Now;""");
        var data = await project.BuildAndGetOutput(["/p:GITHUB_ACTIONS=true"]);
        Assert.True(data.HasError("RS0030"));
    }

    [Fact]
    public async Task NamingConvention_Invalid()
    {
        await using var project = new ProjectBuilder(fixture, testOutputHelper, this);
        project.AddCsprojFile();
        project.AddFile("sample.cs", """
            _ = "";

            class Sample
            {
                private readonly int field;

                public Sample(int a) => field = a;

                public int A() => field;
            }
            """);
        var data = await project.BuildAndGetOutput(["--configuration", "Release"]);
        Assert.True(data.HasError("IDE1006"));
    }

    [Fact]
    public async Task NamingConvention_Valid()
    {
        await using var project = new ProjectBuilder(fixture, testOutputHelper, this);
        project.AddCsprojFile();
        project.AddFile("sample.cs", """
            _ = "";

            class Sample
            {
                private int _field;
            }
            """);
        var data = await project.BuildAndGetOutput(["--configuration", "Release"]);
        Assert.False(data.HasError("IDE1006"));
        Assert.False(data.HasWarning("IDE1006"));
    }

    [Fact]
    public async Task CodingStyle_UseExpression()
    {
        await using var project = new ProjectBuilder(fixture, testOutputHelper, this);
        project.AddCsprojFile();
        project.AddFile("Program.cs", """
            A();

            static void A()
            {
                System.Console.WriteLine();
            }
            """);
        var data = await project.BuildAndGetOutput(["--configuration", "Release"]);
        Assert.False(data.HasWarning());
        Assert.False(data.HasError());
    }

    [Fact]
    public async Task CodingStyle_ExpressionIsNeverUsed()
    {
        await using var project = new ProjectBuilder(fixture, testOutputHelper, this);
        project.AddCsprojFile();
        project.AddFile("Program.cs", """
            var sb = new System.Text.StringBuilder();
            sb.AppendLine();

            """);
        var data = await project.BuildAndGetOutput(["--configuration", "Release"]);
        Assert.False(data.HasWarning());
        Assert.False(data.HasError());
    }

    [Fact]
    public async Task LocalEditorConfigCanOverrideSettings()
    {
        await using var project = new ProjectBuilder(fixture, testOutputHelper, this);
        project.AddCsprojFile();
        project.AddFile("Program.cs", """
            _ = "";

            class Sample
            {
                public static void A()
                {
                    B();

                    static void B()
                    {
                        System.Console.WriteLine();
                    }
                }
            }
            
            """);
        project.AddFile(".editorconfig", """
            [*.cs]      
            csharp_style_expression_bodied_local_functions = true:warning
            """);

        var data = await project.BuildAndGetOutput(["--configuration", "Debug"]);
        Assert.True(data.HasWarning());
        Assert.False(data.HasError());
    }

    [Fact]
    public async Task NuGetAuditIsReportedAsErrorOnGitHubActions()
    {
        await using var project = new ProjectBuilder(fixture, testOutputHelper, this);
        project.AddCsprojFile(nuGetPackages: [("System.Net.Http", "4.3.3")]);
        project.AddFile("Program.cs", """System.Console.WriteLine();""");
        var data = await project.BuildAndGetOutput(["/p:GITHUB_ACTIONS=true"]);
        Assert.True(data.OutputContains("error NU1903", StringComparison.Ordinal));
        Assert.Equal(1, data.ExitCode);
    }

    [Fact]
    public async Task NuGetAuditIsReportedAsWarning()
    {
        await using var project = new ProjectBuilder(fixture, testOutputHelper, this);
        project.AddCsprojFile(nuGetPackages: [("System.Net.Http", "4.3.3")]);
        project.AddFile("Program.cs", """System.Console.WriteLine();""");
        var data = await project.BuildAndGetOutput();
        Assert.True(data.OutputContains("warning NU1903", StringComparison.Ordinal));
        Assert.True(data.OutputDoesNotContain("error NU1903", StringComparison.Ordinal));
        Assert.Equal(0, data.ExitCode);
    }

    [Fact]
    public async Task MSBuildWarningsAsError()
    {
        await using var project = new ProjectBuilder(fixture, testOutputHelper, this);
        project.AddFile("Program.cs", """
            System.Console.WriteLine();
            
            """);
        project.AddCsprojFile(additionalProjectElements: [
            new XElement("Target", new XAttribute("Name", "Custom"), new XAttribute("BeforeTargets", "Build"),
                new XElement("Warning", new XAttribute("Text", "CustomWarning")))]);
        var data = await project.BuildAndGetOutput(["--configuration", "Release"]);

        Assert.True(data.OutputContains("error : CustomWarning"));
    }

    [Fact]
    public async Task MSBuildWarningsAsError_NotEnableOnDebug()
    {
        await using var project = new ProjectBuilder(fixture, testOutputHelper, this);
        project.AddFile("Program.cs", """System.Console.WriteLine();""");
        project.AddCsprojFile(additionalProjectElements: [
            new XElement("Target", new XAttribute("Name", "Custom"), new XAttribute("BeforeTargets", "Build"),
                new XElement("Warning", new XAttribute("Text", "CustomWarning")))]);
        var data = await project.BuildAndGetOutput(["--configuration", "Debug"]);

        Assert.True(data.OutputContains("warning : CustomWarning"));
    }

    [Fact]
    public async Task CA1708_NotReportedForFileLocalTypes()
    {
        await using var project = new ProjectBuilder(fixture, testOutputHelper, this);
        project.AddCsprojFile();
        project.AddFile("Sample1.cs", """
            System.Console.WriteLine();

            class A {}
            
            file class Sample
            {
            }
            """);
        project.AddFile("Sample2.cs", """
            class B {}

            file class sample
            {
            }
            """);
        var data = await project.BuildAndGetOutput(["--configuration", "Release"]);
        Assert.False(data.HasError("CA1708"));
        Assert.False(data.HasWarning("CA1708"));
    }

    [Fact]
    public async Task PdbShouldBeEmbedded_Dotnet_Build()
    {
        await using var project = new ProjectBuilder(fixture, testOutputHelper, this);
        project.AddCsprojFile();
        project.AddFile("Program.cs", """
            Console.WriteLine();

            """);

        var data = await project.BuildAndGetOutput(["--configuration", "Release"]);

        var outputFiles = Directory.GetFiles(project.RootFolder / "bin", "*", SearchOption.AllDirectories);
        await AssertPdbIsEmbedded(outputFiles);
    }

    [Fact]
    public async Task PdbShouldBeEmbedded_Dotnet_Pack()
    {
        await using var project = new ProjectBuilder(fixture, testOutputHelper, this);
        project.AddCsprojFile();
        project.AddFile("Program.cs", """
            Console.WriteLine();

            """);

        var data = await project.PackAndGetOutput(["--configuration", "Release"]);

        var extractedPath = project.RootFolder / "extracted";
        var files = Directory.GetFiles(project.RootFolder / "bin" / "Release");
        Assert.Single(files); // Only the .nupkg should be generated
        var nupkg = files.Single(f => f.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase));
        ZipFile.ExtractToDirectory(nupkg, extractedPath);

        var outputFiles = Directory.GetFiles(extractedPath, "*", SearchOption.AllDirectories);
        await AssertPdbIsEmbedded(outputFiles);
    }

    [Fact]
    public async Task DotnetTestSkipAnalyzers()
    {
        await using var project = new ProjectBuilder(fixture, testOutputHelper, this);
        project.AddCsprojFile(
            properties: [("IsTestProject", "true")],
            nuGetPackages: [("Microsoft.NET.Test.Sdk", "17.14.1"), ("xunit", "2.9.3"), ("xunit.runner.visualstudio", "3.1.1")]
        );
        project.AddFile("sample.cs", """
            public class Sample
            {
                [Xunit.Fact]
                public void Test()
                {
                    _ = System.DateTime.Now; // This should not be reported as an error
                }
            }
            """);
        var data = await project.TestAndGetOutput();
        Assert.False(data.HasWarning("RS0030"));
    }

    [Fact]
    public async Task DotnetTestSkipAnalyzers_OptOut()
    {
        await using var project = new ProjectBuilder(fixture, testOutputHelper, this);
        project.AddCsprojFile(
            properties: [("IsTestProject", "true"), ("OptimizeVsTestRun", "false")],
            nuGetPackages: [("Microsoft.NET.Test.Sdk", "17.14.1"), ("xunit", "2.9.3"), ("xunit.runner.visualstudio", "3.1.1")]
        );
        project.AddFile("sample.cs", """
            public class Sample
            {
                [Xunit.Fact]
                public void Test()
                {
                    _ = System.DateTime.Now; // This should not be reported as an error
                }
            }
            """);
        var data = await project.TestAndGetOutput();
        Assert.True(data.HasWarning("RS0030"));
    }

    [Fact]
    public async Task NonMeziantouCsproj()
    {
        await using var project = new ProjectBuilder(fixture, testOutputHelper, this);
        project.AddCsprojFile(filename: "sample.csproj");
        project.AddFile("Program.cs", """Console.WriteLine();""");
        project.AddFile("LICENSE.txt", """dummy""");
        var data = await project.PackAndGetOutput();
        Assert.Equal(0, data.ExitCode);

        var package = Directory.GetFiles(project.RootFolder, "*.nupkg", SearchOption.AllDirectories).Single();
        using var packageReader = new PackageArchiveReader(package);
        var nuspecReader = await packageReader.GetNuspecReaderAsync(TestContext.Current.CancellationToken);
        Assert.NotEqual("meziantou", nuspecReader.GetAuthors());
        Assert.NotEqual("icon.png", nuspecReader.GetIcon());
        Assert.DoesNotContain("icon.png", packageReader.GetFiles());
    }

    [Fact]
    public async Task MeziantouCsproj()
    {
        await using var project = new ProjectBuilder(fixture, testOutputHelper, this);
        project.AddCsprojFile();
        project.AddFile("Program.cs", """Console.WriteLine();""");
        project.AddFile("LICENSE.txt", """dummy""");
        var data = await project.PackAndGetOutput();
        Assert.Equal(0, data.ExitCode);

        var package = Directory.GetFiles(project.RootFolder, "*.nupkg", SearchOption.AllDirectories).Single();
        using var packageReader = new PackageArchiveReader(package);
        var nuspecReader = await packageReader.GetNuspecReaderAsync(TestContext.Current.CancellationToken);
        Assert.Equal("meziantou", nuspecReader.GetAuthors());
        Assert.Equal("icon.png", nuspecReader.GetIcon());
        Assert.Contains("icon.png", packageReader.GetFiles());
        Assert.Contains("LICENSE.txt", packageReader.GetFiles());
    }

    private static async Task AssertPdbIsEmbedded(string[] outputFiles)
    {
        Assert.DoesNotContain(outputFiles, f => f.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase));
        var dllPath = outputFiles.Single(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
        await using var stream = File.OpenRead(dllPath);
        var peReader = new PEReader(stream);
        var debug = peReader.ReadDebugDirectory();
        Assert.Contains(debug, entry => entry.Type == DebugDirectoryEntryType.EmbeddedPortablePdb);
    }

    private sealed class ProjectBuilder : IAsyncDisposable
    {
        private const string SarifFileName = "BuildOutput.sarif";

        private readonly TemporaryDirectory _directory;
        private readonly ITestOutputHelper _testOutputHelper;
        private readonly CodingStandardTests _test;

        public FullPath RootFolder => _directory.FullPath;

        public ProjectBuilder(PackageFixture fixture, ITestOutputHelper testOutputHelper, CodingStandardTests test)
        {
            _testOutputHelper = testOutputHelper;
            _test = test;
            _directory = TemporaryDirectory.Create();
            _directory.CreateTextFile("NuGet.config", $"""
                <configuration>
                  <config>
                    <add key="globalPackagesFolder" value="{fixture.PackageDirectory}/packages" />
                  </config>
                  <packageSources>
                    <clear />    
                    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
                    <add key="TestSource" value="{fixture.PackageDirectory}" />
                  </packageSources>
                  <packageSourceMapping>
                    <packageSource key="nuget.org">
                        <package pattern="*" />
                    </packageSource>
                    <packageSource key="TestSource">
                        <package pattern="Meziantou.DotNet.CodingStandard" />
                    </packageSource>
                  </packageSourceMapping>
                </configuration>
                """);
        }

        public ProjectBuilder AddFile(string relativePath, string content)
        {
            File.WriteAllText(_directory.FullPath / relativePath, content);
            return this;
        }

        public ProjectBuilder AddCsprojFile((string Name, string Value)[] properties = null, (string Name, string Version)[] nuGetPackages = null, XElement[] additionalProjectElements = null, string filename = "Meziantou.TestProject.csproj")
        {
            var propertiesElement = new XElement("PropertyGroup");
            if (properties != null)
            {
                foreach (var prop in properties)
                {
                    propertiesElement.Add(new XElement(prop.Name, prop.Value));
                }
            }

            var packagesElement = new XElement("ItemGroup");
            if (nuGetPackages != null)
            {
                foreach (var package in nuGetPackages)
                {
                    packagesElement.Add(new XElement("PackageReference", new XAttribute("Include", package.Name), new XAttribute("Version", package.Version)));
                }
            }

            var content = $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <ComputeNETCoreBuildOutputFiles>false</ComputeNETCoreBuildOutputFiles>
                    <OutputType>exe</OutputType>
                    <TargetFramework>net$(NETCoreAppMaximumVersion)</TargetFramework>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                    <ErrorLog>{SarifFileName},version=2.1</ErrorLog>
                  </PropertyGroup>
                  {propertiesElement}
                  {packagesElement}
                  <ItemGroup>
                    <PackageReference Include="Meziantou.DotNet.CodingStandard" Version="*" />
                  </ItemGroup>
                  {string.Join('\n', additionalProjectElements?.Select(e => e.ToString()) ?? [])}
                </Project>                
                """;

            File.WriteAllText(_directory.FullPath / filename, content);
            return this;
        }

        public Task<BuildResult> BuildAndGetOutput(string[] buildArguments = null)
        {
            return this.ExecuteDotnetCommandAndGetOutput("build", buildArguments);
        }

        public Task<BuildResult> PackAndGetOutput(string[] buildArguments = null)
        {
            return this.ExecuteDotnetCommandAndGetOutput("pack", buildArguments);
        }

        public Task<BuildResult> TestAndGetOutput(string[] buildArguments = null)
        {
            return this.ExecuteDotnetCommandAndGetOutput("test", buildArguments);
        }

        private async Task<BuildResult> ExecuteDotnetCommandAndGetOutput(string command, string[] buildArguments = null)
        {
            var globaljsonPsi = new ProcessStartInfo("dotnet", "new global.json")
            {
                WorkingDirectory = _directory.FullPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,

                RedirectStandardError = true,
            };
            var result = await globaljsonPsi.RunAsTaskAsync();
            _testOutputHelper.WriteLine("Process exit code: " + result.ExitCode);
            _testOutputHelper.WriteLine(result.Output.ToString());

            var psi = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = _directory.FullPath,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add(command);
            if (buildArguments != null)
            {
                foreach (var arg in buildArguments)
                {
                    psi.ArgumentList.Add(arg);
                }
            }

            psi.ArgumentList.Add("/bl");

            // Remove parent environment variables
            psi.Environment.Remove("CI");
            psi.Environment.Remove("GITHUB_ACTIONS");

            result = await psi.RunAsTaskAsync();
            _testOutputHelper.WriteLine("Process exit code: " + result.ExitCode);
            _testOutputHelper.WriteLine(result.Output.ToString());

            FullPath sarifPath = _directory.FullPath / SarifFileName;
            SarifFile sarif = null;
            if (File.Exists(sarifPath))
            {
                var bytes = File.ReadAllBytes(sarifPath);
                sarif = JsonSerializer.Deserialize<SarifFile>(bytes);
                _testOutputHelper.WriteLine("Sarif result:\n" + string.Join("\n", sarif.AllResults().Select(r => r.ToString())));
            }
            else
            {
                _testOutputHelper.WriteLine("Sarif file not found: " + sarifPath);
            }

            var binlogContent = File.ReadAllBytes(_directory.FullPath / "msbuild.binlog");
            TestContext.Current.AddAttachment("msbuild.binlog", binlogContent, "application/octet-stream");
            return new BuildResult(result.ExitCode, result.Output, sarif, binlogContent);
        }

        public ValueTask DisposeAsync() => _directory.DisposeAsync();
    }

    private sealed record BuildResult(int ExitCode, ProcessOutputCollection ProcessOutput, SarifFile SarifFile, byte[] BinaryLogContent)
    {
        public bool OutputContains(string value, StringComparison stringComparison = StringComparison.Ordinal) => ProcessOutput.Any(line => line.Text.Contains(value, stringComparison));
        public bool OutputDoesNotContain(string value, StringComparison stringComparison = StringComparison.Ordinal) => !ProcessOutput.Any(line => line.Text.Contains(value, stringComparison));

        public bool HasError() => SarifFile.AllResults().Any(r => r.Level == "error");
        public bool HasError(string ruleId) => SarifFile.AllResults().Any(r => r.Level == "error" && r.RuleId == ruleId);
        public bool HasWarning() => SarifFile.AllResults().Any(r => r.Level == "warning");
        public bool HasWarning(string ruleId) => SarifFile.AllResults().Any(r => r.Level == "warning" && r.RuleId == ruleId);
        public bool HasNote(string ruleId) => SarifFile.AllResults().Any(r => r.Level == "note" && r.RuleId == ruleId);

        public IReadOnlyCollection<string> GetBinLogFiles()
        {
            using var stream = new MemoryStream(BinaryLogContent);
            var build = Serialization.ReadBinLog(stream);
            return [.. build.SourceFiles.Select(file => file.FullPath)];
        }
    }

    private sealed class SarifFile
    {
        [JsonPropertyName("runs")]
        public SarifFileRun[] Runs { get; set; }

        public IEnumerable<SarifFileRunResult> AllResults() => Runs.SelectMany(r => r.Results);
    }

    private sealed class SarifFileRun
    {
        [JsonPropertyName("results")]
        public SarifFileRunResult[] Results { get; set; }
    }

    private sealed class SarifFileRunResult
    {
        [JsonPropertyName("ruleId")]
        public string RuleId { get; set; }

        [JsonPropertyName("level")]
        public string Level { get; set; }

        [JsonPropertyName("message")]
        public SarifFileRunResultMessage Message { get; set; }

        public override string ToString()
        {
            return $"{Level}:{RuleId} {Message}";
        }
    }

    private sealed class SarifFileRunResultMessage
    {
        [JsonPropertyName("text")]
        public string Text { get; set; }

        public override string ToString()
        {
            return Text;
        }
    }
}