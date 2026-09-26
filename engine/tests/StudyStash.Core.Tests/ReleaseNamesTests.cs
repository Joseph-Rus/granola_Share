namespace StudyStash.Core.Tests;

/// <summary>Study Stash's four installer names and its checksums file name live in one place, <see
/// cref="Updates"/> (D2). ci.yml's release step and the build scripts that produce those files must use the
/// same names, or a release could ship something the updater doesn't recognize.</summary>
public class ReleaseNamesTests
{
    [Fact]
    public void Ci_releases_every_installer_and_the_checksums_file_by_its_real_name()
    {
        string ci = Read(".github", "workflows", "ci.yml");
        foreach (string name in Updates.Installers)
            Assert.Contains(name, ci);
        Assert.Contains(Updates.ChecksumsAsset, ci);
    }

    [Fact]
    public void The_Mac_build_script_names_both_DMGs()
    {
        // build-app.sh builds each DMG's name from "Study-Stash-" plus a role argument ("Laptop"/"Library"),
        // rather than spelling out the whole file name, so check it uses the shared prefix and both roles.
        string buildApp = Read("macos", "build-app.sh");
        Assert.Contains("Study-Stash-", buildApp);
        Assert.Contains(RolePart(Updates.MacLaptopAsset), buildApp);
        Assert.Contains(RolePart(Updates.MacLibraryAsset), buildApp);
    }

    static string RolePart(string dmgName) => dmgName["Study-Stash-".Length..^".dmg".Length];

    [Fact]
    public void The_Windows_installer_and_build_script_name_both_Setup_exe()
    {
        string setupIss = Read("windows", "setup.iss");
        string buildPs1 = Read("windows", "build.ps1");
        string both = setupIss + "\n" + buildPs1;
        // setup.iss builds the two names from a #define, so it's enough that each half appears somewhere.
        Assert.Contains(RoleHalf(Updates.WindowsLaptopAsset), both);
        Assert.Contains(RoleHalf(Updates.WindowsLibraryAsset), both);
    }

    [Fact]
    public void Ci_mentions_none_of_the_retired_release_shapes()
    {
        string ci = Read(".github", "workflows", "ci.yml");
        foreach (string retired in new[]
        {
            "Study-Stash-engine-", "Study-Stash-mac.zip", "helper-windows", "App-preview", "python", "pytest", "uv ",
        })
            Assert.DoesNotContain(retired, ci, StringComparison.OrdinalIgnoreCase);
    }

    static string RoleHalf(string setupExeName) => setupExeName[..^".exe".Length];

    static string Read(params string[] pathFromRoot) => File.ReadAllText(Path.Combine([RepoRoot(), .. pathFromRoot]));

    /// <summary>The repo root, found by walking up from where the tests run until engine/Directory.Build.props
    /// turns up (the same trick VersionTests uses).</summary>
    static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "engine", "Directory.Build.props")))
                return d.FullName;
        throw new FileNotFoundException("couldn't find the repo root (engine/Directory.Build.props)");
    }
}
