using Nuke.Common;
using Nuke.Common.Execution;
using Nuke.Common.Git;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.DocFX;
using Nuke.Common.Tools.DotNet;
using Nuke.Common.Tools.GitVersion;
using Nuke.Common.Tools.NuGet;
using Nuke.Common.Tools.SignTool;
using Nuke.Common.Utilities.Collections;
using Nuke.GitHub;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Nuke.Common.Tools.Git;
using Octokit;
using static Nuke.Common.IO.CompressionTasks;
using static Nuke.Common.IO.FileSystemTasks;
using static Nuke.Common.IO.PathConstruction;
using static Nuke.Common.IO.TextTasks;
using static Nuke.Common.Tools.DocFX.DocFXTasks;
using static Nuke.Common.Tools.DotNet.DotNetTasks;
using static Nuke.Common.Tools.NuGet.NuGetTasks;
using static Nuke.GitHub.GitHubTasks;

[CheckBuildProjectConfigurations]
[UnsetVisualStudioEnvironmentVariables]
class Build : NukeBuild
{
  public static int Main() => Execute<Build>(x => x.BuildDocumentation);

  [Parameter("Configuration to build - Default is 'Debug' (local) or 'Release' (server)")]
  readonly string Configuration = IsLocalBuild ? "Debug" : "Release";

  [Parameter] readonly string GitHubAuthenticationToken;
  [Parameter] readonly string CodeSigningCertBase64;
  [Parameter] readonly string CodeSigningCertPassword;

  [PackageExecutable("Tools.InnoSetup", "tools/ISCC.exe")] readonly Tool InnoSetup;

  [Solution] readonly Solution Solution;
  [GitRepository] readonly GitRepository GitRepository;
  [GitVersion(Framework = "netcoreapp3.1")] readonly GitVersion GitVersion;

  AbsolutePath OutputDirectory => RootDirectory / "output";
  AbsolutePath DocFxFile => RootDirectory / "docs" / "docfx.json";
  AbsolutePath ChangelogFile => RootDirectory / "CHANGELOG.md";
  AbsolutePath DocsDirectory => RootDirectory / "docs";

  private static HashSet<string> _alreadySignedFiles = new HashSet<string>();

  private void SignFilesIfRequirementsMet(string filePath = null)
  {
    if (string.IsNullOrWhiteSpace(CodeSigningCertBase64))
    {
      Logger.Normal("Skipping file signing due to no certificate being provided");
      return;
    }

    var filesToSign = filePath == null
       // If no file path is specified, we sign all *.exe and *.dll files
       // in the output directory that have 'OpenProject' in their filename
       ? GlobFiles(OutputDirectory, "**/*OpenProject*.exe")
        .Concat(GlobFiles(OutputDirectory, "**/*OpenProject*.dll"))
        .Where(file => !_alreadySignedFiles.Contains(file))
        .Distinct()
        .ToList()
        // In case a file path is given directly, we're using that
        : new[] { filePath }.ToList();

    filesToSign.ForEach(f => _alreadySignedFiles.Add(f));

    if (filesToSign.Any())
    {
      var certFilePath = OutputDirectory / $"{Guid.NewGuid()}-cert.pfx";
      var certContent = Convert.FromBase64String(CodeSigningCertBase64);
      WriteAllBytes(certFilePath, certContent);

      Logger.Normal($"Signing files:{Environment.NewLine}{filesToSign.Aggregate((c, n) => c + Environment.NewLine + n)}");

      try
      {
        SignToolTasks
            .SignTool(c => c
              .SetFileDigestAlgorithm("SHA256")
              .SetFile(certFilePath)
              .SetFiles(filesToSign)
              .SetPassword(CodeSigningCertPassword)
              .SetTimestampServerDigestAlgorithm("SHA256")
              .SetRfc3161TimestampServerUrl("http://timestamp.digicert.com")
            );
      }
      finally
      {
        DeleteFile(certFilePath);
      }
    }
  }

  Target Clean => _ => _
      .Before(Restore)
      .Executes(() =>
      {
        GlobDirectories(RootDirectory, "**/bin", "**/obj")
              // Excluding the build directory from cleanup, since this solution is set up with
              // no subdirectories for the actual sources
              .Where(d => !d.StartsWith(RootDirectory / "build"))
              .ForEach(DeleteDirectory);
        EnsureCleanDirectory(OutputDirectory);
      });

  Target Restore => _ => _
      .Executes(() =>
      {
        RestorePackages();
      });

  private void RestorePackages()
  {
    DotNetRestore(s => s
      .SetProjectFile(Solution));
    // This separate call uses the NuGet.CommandLine package to do a traditional
    // MSBuild restore. This is required to correctly store the conditional references
    // in the OpenProject.Revit/packages.config file, which restores different Revit API
    // NuGet packages depending on the build configuration
    NuGetRestore(c => c.SetSolutionDirectory(RootDirectory));
  }

  Target CreateEmbeddedLandingPageZip => _ => _
    .DependsOn(Clean)
    .Executes(() =>
    {
      if (!DirectoryExists(OutputDirectory))
      {
        Directory.CreateDirectory(OutputDirectory);
      }

      var landingPageFolder = RootDirectory / "src" / "OpenProject.Browser" / "WebViewIntegration" / "LandingPage";
      var landingPageIndexPath = landingPageFolder / "index.html";
      var originalLandingPageIndexContent = ReadAllText(landingPageIndexPath);
      try
      {
        WriteAllText(landingPageIndexPath, originalLandingPageIndexContent.Replace("@@PLUGIN_VERSION@@", GitVersion.NuGetVersion));
        var zipOutputPath = landingPageFolder / "LandingPage.zip";
        if (File.Exists(zipOutputPath))
        {
          DeleteFile(zipOutputPath);
        }

        var tempZipPath = OutputDirectory / $"{Guid.NewGuid()}.zip";
        ZipFile.CreateFromDirectory(landingPageFolder, tempZipPath);
        MoveFile(tempZipPath, zipOutputPath);
      }
      finally
      {
        WriteAllText(landingPageIndexPath, originalLandingPageIndexContent);
      }
    });

  Target WriteVersionAndRepoInfo => _ => _
    .Executes(() =>
    {
      var versionServiceFilePath = RootDirectory / "src" / "OpenProject.Shared" / "VersionsService.cs";
      var buildDate = DateTime.UtcNow;
      var currentDateUtc = $"new DateTime({buildDate.Year}, {buildDate.Month}, {buildDate.Day}, {buildDate.Hour}, {buildDate.Minute}, {buildDate.Second}, DateTimeKind.Utc)";

      var versionServiceContent = $@"using System;
namespace OpenProject.Shared
{{
    // This file is automatically generated
    [System.CodeDom.Compiler.GeneratedCode(""GitVersionBuild"", """")]
    public static class VersionsService
    {{
        public static string Version => ""{GitVersion.NuGetVersionV2}"";
        public static string CommitInfo => ""{GitVersion.FullBuildMetaData}"";
        public static string CommitDate => ""{GitVersion.CommitDate}"";
        public static string CommitHash => ""{GitVersion.Sha}"";
        public static string InformationalVersion => ""{GitVersion.InformationalVersion}"";
        public static DateTime BuildDateUtc {{ get; }} = {currentDateUtc};
    }}
}}";
      WriteAllText(versionServiceFilePath, versionServiceContent);

      var repositoryInfo = GetGitHubRepositoryInfo(GitRepository);
      var repoInfoFilePath = RootDirectory / "src" / "OpenProject.Shared" / "RepositoryInfo.cs";
      var repoInfoContent = $@"using System;
namespace OpenProject.Shared
{{
    // This file is automatically generated
    [System.CodeDom.Compiler.GeneratedCode(""GitVersionBuild"", """")]
    public static class RepositoryInfo
    {{
        public static string GitHubOwner => ""{repositoryInfo.gitHubOwner}"";
        public static string GitHubRepository => ""{repositoryInfo.repositoryName}"";
    }}
}}";
      WriteAllText(repoInfoFilePath, repoInfoContent);
    });

  Target Compile => _ => _
      .After(Clean)
      .DependsOn(Restore)
      .DependsOn(CreateEmbeddedLandingPageZip)
      .DependsOn(WriteVersionAndRepoInfo)
      .Executes(() =>
      {
        DotNetBuild(s => s
              .SetProjectFile(Solution)
              .SetConfiguration(Configuration)
              .SetAssemblyVersion(GitVersion.AssemblySemVer)
              .SetFileVersion(GitVersion.AssemblySemFileVer)
              .SetInformationalVersion(GitVersion.InformationalVersion)
              .EnableNoRestore());
      });

  Target Test => _ => _
       .DependsOn(Clean)
       .DependsOn(Restore)
       .Executes(() =>
        {
          DotNetTest(c => c
            .SetConfiguration("Debug-2022")
            .SetProjectFile(RootDirectory / "test" / "OpenProject.Tests" / "OpenProject.Tests.csproj")
            .SetTestAdapterPath(".")
            .SetLoggers($"xunit;LogFilePath={OutputDirectory / "testresults.xml"}"));
        });

  Target CompileReleaseConfigurations => _ => _
      .DependsOn(Clean)
      .DependsOn(Restore)
      .DependsOn(CreateEmbeddedLandingPageZip)
      .DependsOn(WriteVersionAndRepoInfo)
      .Executes(() =>
      {
        DotNetPublish(c => c
          .SetProject(RootDirectory / "src" / "OpenProject.Browser" / "OpenProject.Browser.csproj")
          .SetConfiguration("Release x64")
          .SetAssemblyVersion(GitVersion.AssemblySemVer)
          .SetFileVersion(GitVersion.AssemblySemFileVer)
          .SetInformationalVersion(GitVersion.InformationalVersion)
          .SetOutput(OutputDirectory / "OpenProject.Browser")
          .SetSelfContained(true)
          .SetRuntime("win-x64"));

        var revitConfigurations = new[]
        {
          "Release-2019",
          "Release-2020",
          "Release-2021",
          "Release-2022"
        };

        DotNetBuild(c => c
          .SetProjectFile(RootDirectory / "src" / "OpenProject.Revit" / "OpenProject.Revit.csproj")
          .SetAssemblyVersion(GitVersion.AssemblySemVer)
          .SetFileVersion(GitVersion.AssemblySemFileVer)
          .SetInformationalVersion(GitVersion.InformationalVersion)
          .CombineWith(cc => revitConfigurations
            .Select(config => cc
              .SetConfiguration(config)
              .SetOutputDirectory(OutputDirectory / "OpenProject.Revit" / config))));

        OutputDirectory.GlobFiles("**/*.pdb",
            "**/*.txt",
            "**/*.xml",
            "**/*.XML")
          .ForEach(DeleteFile);

        SignFilesIfRequirementsMet();
      });

  Target CreateSetup => _ => _
      .DependsOn(CompileReleaseConfigurations)
      .Executes(() =>
      {
        // The Inno Setup tool generates a single, self contained setup application
        // in the root directory as OpenProject.exe. This can be distributed for installation
        InnoSetup($"{RootDirectory / "InnoSetup" / "OpenProject.iss"}");
        SignFilesIfRequirementsMet(OutputDirectory / "OpenProject.Revit.exe");
      });

  static bool ShouldBuildRelease()
  {
    const string flag = "[release skip]";

    var message = GitTasks.Git("log -1 --pretty=format:%B", logOutput: false)
      .Select(output => output.Text)
      .Aggregate((s, s1) => $"{s}\n{s1}");

    return !message.Contains(flag);
  }

  Target PublishGitHubRelease => _ => _
      .OnlyWhenDynamic(() => ShouldBuildRelease())
      .DependsOn(CreateSetup)
      .DependsOn(BuildDocumentation)
      .Requires(() => GitHubAuthenticationToken)
      .Executes(async () =>
      {
        var releaseTag = $"v{GitVersion.SemVer}";
        var isStableRelease = GitVersion.BranchName.Equals("master") || GitVersion.BranchName.Equals("origin/master") || GitVersion.BranchName.Equals("main") || GitVersion.BranchName.Equals("origin/main");

        var (gitHubOwner, repositoryName) = GetGitHubRepositoryInfo(GitRepository);
        var installationFile = GlobFiles(OutputDirectory, "OpenProject.Revit.exe").NotEmpty().Single();

        var artifactPaths = new[]
        {
          installationFile,
          OutputDirectory / "InstallationInstructions.pdf",
          OutputDirectory / "Documentation.zip"
        };

        await PublishRelease(x => x
              .SetPrerelease(!isStableRelease)
              .SetArtifactPaths(artifactPaths)
              .SetCommitSha(GitVersion.Sha)
              .SetRepositoryName(repositoryName)
              .SetRepositoryOwner(gitHubOwner)
              .SetTag(releaseTag)
              .SetToken(GitHubAuthenticationToken));
      });

  Target BuildLandingPageHtml => _ => _
    .Executes(() =>
    {
      var landingPageFolder = RootDirectory / "Bcfier" / "WebViewIntegration" / "LandingPage";
      var assetsFolder = landingPageFolder / "assets";

      var originalHtml = ReadAllText(landingPageFolder / "index.html");
      var htmlDoc = new HtmlAgilityPack.HtmlDocument();
      htmlDoc.LoadHtml(originalHtml);

      // Inline css
      foreach (var cssDeclaration in htmlDoc.DocumentNode.Descendants()
        .Where(d => d.Name == "link" && d.GetAttributes("rel")?.FirstOrDefault().Value == "stylesheet")
        .Where(d => d.GetAttributes("href")?.Any() ?? false)
        .ToList())
      {
        var cssLink = cssDeclaration.GetAttributes("href")
          .First()
          .Value
          .Substring(9); // "./assets/" length
        var css = ReadAllText(assetsFolder / cssLink);

        var styleNode = htmlDoc.CreateElement("style");
        styleNode.InnerHtml = css;

        cssDeclaration.ParentNode.InsertBefore(styleNode, cssDeclaration);
        cssDeclaration.Remove();
      }

      // Inline javascript
      foreach (var scriptDeclaration in htmlDoc.DocumentNode.Descendants()
        .Where(d => d.Name == "script" && d.GetAttributes("src")?.FirstOrDefault() != null)
        .ToList())
      {
        var scriptLink = scriptDeclaration.GetAttributes("src")
          .First()
          .Value
          .Substring(9); // "./assets/" length
        var script = ReadAllText(assetsFolder / scriptLink);

        var scriptNode = htmlDoc.CreateElement("script");
        scriptNode.SetAttributeValue("type", "text/javascript");
        scriptNode.InnerHtml = script;

        scriptDeclaration.ParentNode.InsertBefore(scriptNode, scriptDeclaration);
        scriptDeclaration.Remove();
      }

      var transformedHtml = htmlDoc.DocumentNode.OuterHtml;
      WriteAllText(landingPageFolder / "generated.html", transformedHtml);
    });

  Target BuildDocumentation => _ => _
      .DependsOn(Clean)
      .After(CreateSetup)
      .Executes(async () =>
      {
        CopyFile(RootDirectory / "README.md", DocsDirectory / "index.md", FileExistsPolicy.Overwrite);

        DocFXBuild(x => x
              .SetProcessEnvironmentVariable("DOCFX_SOURCE_BRANCH_NAME", GitVersion.BranchName)
              .SetOutputFolder(OutputDirectory / "docs")
              .SetConfigFile(DocFxFile));

        var pdfDocsGenerator = new PdfDocsGenerator(OutputDirectory);
        // This is currently only building the installation instructions
        await pdfDocsGenerator.BuildPdfDocsAsync();

        Compress(OutputDirectory / "docs", OutputDirectory / "Documentation.zip");

        DeleteFile(DocsDirectory / "index.md");
      });
}
