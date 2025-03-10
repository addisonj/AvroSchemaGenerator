using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using Nuke.Common;
using Nuke.Common.CI;
using Nuke.Common.CI.GitHubActions;
using Nuke.Common.Execution;
using Nuke.Common.Git;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tools.DotNet;
using Nuke.Common.Tools.GitVersion;
using Nuke.Common.Utilities;
using Nuke.Common.Utilities.Collections;
using static Nuke.Common.IO.PathConstruction;
using static Nuke.Common.Tools.DotNet.DotNetTasks;
using static Nuke.Common.IO.FileSystemTasks;
using static Nuke.Common.Tools.DocFX.DocFXTasks;
using static Nuke.Common.ChangeLog.ChangelogTasks;
using static Nuke.Common.Tools.Git.GitTasks;
using Nuke.Common.ChangeLog;
using System.Collections.Generic;
using Octokit;

[CheckBuildProjectConfigurations]
[DotNetVerbosityMapping]
[ShutdownDotNetAfterServerBuild]
partial class Build : NukeBuild
{
    /// Support plugins are available for:
    ///   - JetBrains ReSharper        https://nuke.build/resharper
    ///   - JetBrains Rider            https://nuke.build/rider
    ///   - Microsoft VisualStudio     https://nuke.build/visualstudio
    ///   - Microsoft VSCode           https://nuke.build/vscode

    public static int Main() => Execute<Build>(x => x.Pack);

    [CI] readonly GitHubActions GitHubActions;

    [Parameter("Configuration to build - Default is 'Debug' (local) or 'Release' (server)")]
    readonly Configuration Configuration = IsLocalBuild ? Configuration.Debug : Configuration.Release;

    [Solution] readonly Solution Solution;
    [GitRepository] readonly GitRepository GitRepository;
    [Required] [GitVersion(Framework = "net6.0")] readonly GitVersion GitVersion;

    [Parameter] string NugetApiUrl = "https://api.nuget.org/v3/index.json";

    [Parameter] [Secret] string NuGetApiKey;

    AbsolutePath OutputTests => RootDirectory / "TestResults";

    AbsolutePath OutputPerfTests => RootDirectory / "PerfResults";
    AbsolutePath DocSiteDirectory => RootDirectory / "docs/_site";
    public string ChangelogFile => RootDirectory / "CHANGELOG.md";
    public AbsolutePath DocFxDir => RootDirectory / "docs";
    public string DocFxDirJson => DocFxDir / "docfx.json";
    AbsolutePath OutputNuget => Output / "nuget";
    AbsolutePath Output => RootDirectory / "output";
    AbsolutePath ArtifactsDirectory => RootDirectory / "artifacts";
    AbsolutePath TestSourceDirectory => RootDirectory / "AvroSchemaGenerator.Tests";
    GitHubClient GitHubClient;
    public ChangeLog Changelog => ReadChangelog(ChangelogFile);

    public ReleaseNotes LatestVersion => Changelog.ReleaseNotes.OrderByDescending(s => s.Version).FirstOrDefault() ?? throw new ArgumentException("Bad Changelog File. Version Should Exist");
    public string ReleaseVersion => LatestVersion.Version?.ToString() ?? throw new ArgumentException("Bad Changelog File. Define at least one version");

    Target Clean => _ => _
        .Before(Restore)
        .Executes(() =>
        {
            RootDirectory
            .GlobDirectories("**/bin", "**/obj", Output, OutputTests, OutputPerfTests, OutputNuget, DocSiteDirectory)
            .ForEach(DeleteDirectory);
            EnsureCleanDirectory(ArtifactsDirectory);
        });
    IEnumerable<string> ChangelogSectionNotes => ExtractChangelogSectionNotes(ChangelogFile);

    Target RunChangelog => _ => _
        .OnlyWhenDynamic(() => GitVersion.BranchName == "main")
        //.OnlyWhenStatic(() => InvokedTargets.Contains(nameof(RunChangelog)))
        .Executes(() =>
        {
            FinalizeChangelog(ChangelogFile, GitVersion.SemVer, GitRepository);
            Git($"add {ChangelogFile}");
            Git($"commit -m S \"Finalize {Path.GetFileName(ChangelogFile)} for {GitVersion.SemVer}.\"");
            Git($"tag -f {GitVersion.SemVer}");
        });

    Target Restore => _ => _
        .Executes(() =>
        {
            try
            {
                DotNetRestore(s => s
               .SetProjectFile(Solution));
            }
            catch (Exception ex)
            {
                Information(ex.ToString());
            }
        });

    Target Compile => _ => _
        .DependsOn(Restore)
        .Executes(() =>
        {            
            DotNetBuild(s => s
                .SetProjectFile(Solution)
                .SetFileVersion(GitVersion.MajorMinorPatch)
                //.SetVersion(version)
                .SetConfiguration(Configuration)
                .SetAssemblyVersion(GitVersion.AssemblySemVer)
                .SetFileVersion(GitVersion.AssemblySemFileVer)
                .SetInformationalVersion(GitVersion.InformationalVersion)
                .SetVersion(GitVersion.NuGetVersionV2)
                .EnableNoRestore());
        });
    Target Test => _ => _
        .DependsOn(Compile)
        .Executes(() =>
        {
            var projectName = "AvroSchemaGenerator.Tests";
            var project = Solution.GetProjects("*.Tests").First();
            Information($"Running tests from {projectName}");
            foreach (var fw in project.GetTargetFrameworks())
            {
                Information($"Running tests from {projectName} framework '{fw}'");
                DotNetTest(c => c
                       .SetProjectFile(project)
                       .SetConfiguration(Configuration.ToString())
                       .SetFramework(fw)
                       .SetVerbosity(verbosity: DotNetVerbosity.Normal)
                       .EnableNoBuild());
            }
        });

    Target Pack => _ => _
      .DependsOn(Test)
      //.DependsOn(RunChangelog) requires authrntication in github action
      .Executes(() =>
      {
          var branchName = GitRepository.Branch;

          if (branchName.Equals("main", StringComparison.OrdinalIgnoreCase)
          && !GitVersion.MajorMinorPatch.Equals(LatestVersion.Version.ToString()))
          {
              // Force CHANGELOG.md in case it skipped the mind
              Assert.Fail($"CHANGELOG.md needs to be update for final release. Current version: '{LatestVersion.Version}'. Next version: {GitVersion.MajorMinorPatch}");
          }
          var releaseNotes = branchName.Equals("main", StringComparison.OrdinalIgnoreCase)
                             ? GetNuGetReleaseNotes(ChangelogFile, GitRepository)
                             : ParseReleaseNote();
          var version = GitVersion.SemVer;
          var project = Solution.GetProject("AvroSchemaGenerator");
          DotNetPack(s => s
              .SetProject(project)
              .SetConfiguration(Configuration)
              .EnableNoBuild()

              .EnableNoRestore()
              .SetAssemblyVersion(version)
              .SetVersion(version)
              .SetPackageReleaseNotes(releaseNotes)
              .SetDescription("Generate Avro Schema with support for RECURSIVE SCHEMA")
              .SetPackageTags("Avro", "Schema Generator")
              .AddAuthors("Ebere Abanonu (@mestical)")
              .SetPackageProjectUrl("https://github.com/eaba/AvroSchemaGenerator")
              .SetOutputDirectory(OutputNuget));

      });
    Target Release => _ => _
      .DependsOn(Pack)
      .Requires(() => NugetApiUrl)
      .Requires(() => !NuGetApiKey.IsNullOrEmpty())
      .Requires(() => Configuration.Equals(Configuration.Release))
      .Triggers(GitHubRelease)
      .Executes(() =>
      {
          GlobFiles(OutputNuget, "*.nupkg")
              .Where(x => !x.EndsWith("symbols.nupkg"))
              .ForEach(x =>
              {
                  Assert.NotNullOrEmpty(x);
                  DotNetNuGetPush(s => s
                      .SetTargetPath(x)
                      .SetSource(NugetApiUrl)
                      .SetApiKey(NuGetApiKey)
                  );
              });
      });

    Target AuthenticatedGitHubClient => _ => _
        .Unlisted()
        .OnlyWhenDynamic(() => !string.IsNullOrWhiteSpace(GitHubActions.Token))
        .Executes(() =>
        {
            GitHubClient = new GitHubClient(new ProductHeaderValue("nuke-build"))
            {
                Credentials = new Credentials(GitHubActions.Token, AuthenticationType.Bearer)
            };
        });
    Target GitHubRelease => _ => _
        .Unlisted()
        .Description("Creates a GitHub release (or amends existing) and uploads the artifact")
        .OnlyWhenDynamic(() => !string.IsNullOrWhiteSpace(GitHubActions.Token))
        .DependsOn(AuthenticatedGitHubClient)
        .Executes(async () =>
        {
            var version = GitVersion.NuGetVersionV2;
            var releaseNotes = GetNuGetReleaseNotes(ChangelogFile);
            Release release;


            var identifier = GitRepository.Identifier.Split("/");
            var (gitHubOwner, repoName) = (identifier[0], identifier[1]);
            try
            {
                release = await GitHubClient.Repository.Release.Get(gitHubOwner, repoName, version);
            }
            catch (NotFoundException)
            {
                var newRelease = new NewRelease(version)
                {
                    Body = releaseNotes,
                    Name = version,
                    Draft = false,
                    Prerelease = GitRepository.IsOnReleaseBranch()
                };
                release = await GitHubClient.Repository.Release.Create(gitHubOwner, repoName, newRelease);
            }

            foreach (var existingAsset in release.Assets)
            {
                await GitHubClient.Repository.Release.DeleteAsset(gitHubOwner, repoName, existingAsset.Id);
            }

            Information($"GitHub Release {version}");
            var packages = OutputNuget.GlobFiles("*.nupkg", "*.symbols.nupkg").NotNull();
            foreach (var artifact in packages)
            {
                var releaseAssetUpload = new ReleaseAssetUpload(artifact.Name, "application/zip", File.OpenRead(artifact), null);
                var releaseAsset = await GitHubClient.Repository.Release.UploadAsset(release, releaseAssetUpload);
                Information($"  {releaseAsset.BrowserDownloadUrl}");
            }
        });
    string ParseReleaseNote()
    {
        return XmlTasks.XmlPeek(RootDirectory / "Directory.Build.props", "//Project/PropertyGroup/PackageReleaseNotes").FirstOrDefault();
    }
    static void Information(string info)
    {
        Serilog.Log.Information(info);
    }
}
