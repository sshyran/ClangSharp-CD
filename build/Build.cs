using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Nuke.Common;
using Nuke.Common.CI.GitHubActions;
using Nuke.Common.Execution;
using Nuke.Common.Git;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.DotNet;
using Nuke.Common.Utilities.Collections;
using static Nuke.Common.EnvironmentInfo;
using static Nuke.Common.IO.FileSystemTasks;
using static Nuke.Common.IO.PathConstruction;
using static Nuke.Common.Tools.DotNet.DotNetTasks;

[CheckBuildProjectConfigurations]
[UnsetVisualStudioEnvironmentVariables]
class Build : NukeBuild
{
    /// Support plugins are available for:
    ///   - JetBrains ReSharper        https://nuke.build/resharper
    ///   - JetBrains Rider            https://nuke.build/rider
    ///   - Microsoft VisualStudio     https://nuke.build/visualstudio
    ///   - Microsoft VSCode           https://nuke.build/vscode
    public static int Main() => Execute<Build>(x => x.PackIfNecessary);

    [Parameter("Configuration to build - Default is 'Debug' (local) or 'Release' (server)")]
    readonly string Configuration = IsLocalBuild ? "Debug" : "Release";

    [Solution] readonly Solution Solution;
    [GitRepository] readonly GitRepository GitRepository;
    [Parameter("The API key used to push packages and symbols packages to NuGet")] readonly string NugetApiKey;
    [Parameter("NuGet -NoServiceEndpoint")] readonly bool NugetNoServiceEndpoint;
    [Parameter("NuGet feed")] readonly string NugetFeed = "https://api.nuget.org/v3/index.json";
    [Parameter("NuGet username")] readonly string NugetUsername;
    [Parameter("NuGet password")] readonly string NugetPassword;

    AbsolutePath ArtifactsDirectory => RootDirectory / "artifacts";

    string RevParse(string workingDirectory)
    {
        var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = "rev-parse HEAD",
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            }
        };

        proc.Start();
        while (!proc.StandardOutput.EndOfStream)
        {
            return proc.StandardOutput.ReadLine();
        }

        return null;
    }

    Target PackIfNecessary => _ => _
        .Executes(() =>
        {
            var revBefore = RevParse(RootDirectory / "upstream");
            Process.Start(new ProcessStartInfo
                    {FileName = "git", Arguments = "pull origin master", WorkingDirectory = RootDirectory / "upstream"})
                ?.WaitForExit();
            var revAfter = RevParse(RootDirectory / "upstream");
            if (revBefore != revAfter)
            {
                DotNetPack(s =>
                    s.SetOutputDirectory(ArtifactsDirectory)
                        .SetProperty("Version",
                            $"{DateTime.UtcNow.Year}.{DateTime.UtcNow.Month}.{DateTime.UtcNow.Day}-nightly")
                        .SetProperty("PackageId", "Ultz.Private.ClangSharp")
                        .SetProject(RootDirectory / "upstream" / "sources" / "ClangSharp" / "ClangSharp.csproj"));
            }
        });

    Target PushToNuGet => _ => _
        .DependsOn(PackIfNecessary)
        .Executes(async () =>
        {
            var exceptions = new List<Exception>();
            var feed = NuGetInterface.OpenNuGetFeed(NugetFeed, NugetUsername, NugetPassword);
            var uploadResource = await NuGetInterface.GetUploadResourceAsync(feed);
            var symbolsResource = await NuGetInterface.GetSymbolsUploadResourceAsync(feed);
            foreach (var file in Directory.GetFiles(ArtifactsDirectory, "*.nupkg"))
            {
                try
                {
                    await NuGetInterface.UploadPackageAsync(uploadResource, NugetNoServiceEndpoint, file, NugetApiKey,
                        symbolsResource);
                }
                catch (Exception ex)
                {
                    exceptions.Add(new($"Failed to push package \"{file}\"", ex));
                }
            }

            if (exceptions.Count > 0)
            {
                throw new AggregateException(exceptions);
            }
        });

    Target UpdateGitRepo => _ => _
        .DependsOn(PackIfNecessary)
        .Executes(() =>
        {
            ProcessTasks.StartProcess("git",
                    $"commit -am \"Build {DateTime.UtcNow.Year}.{DateTime.UtcNow.Month}.{DateTime.UtcNow.Day}\"",
                    RootDirectory)
                .WaitForExit();
            ProcessTasks.StartProcess("git", "push origin main", RootDirectory).WaitForExit();
        });

    Target FullRun => _ => _.DependsOn(PushToNuGet, UpdateGitRepo);
}