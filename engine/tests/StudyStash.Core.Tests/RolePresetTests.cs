namespace StudyStash.Core.Tests;

/// <summary>D3: the role a Mac bundle or Windows install was set up for, and D4's "is this an installed copy" checks
/// that the updater (T3) will use.</summary>
public class RolePresetTests
{
    /// <summary>A bundle at X/Study Stash.app/Contents/{Info.plist, MacOS/arm64}, returning the arm64 tree - what
    /// AppContext.BaseDirectory is when the app is actually running.</summary>
    static string Bundle(TempDir t, string plistContent)
    {
        string contents = Path.Combine(t.Path, "Study Stash.app", "Contents");
        string tree = Path.Combine(contents, "MacOS", "arm64");
        Directory.CreateDirectory(tree);
        File.WriteAllText(Path.Combine(contents, "Info.plist"), plistContent);
        return tree;
    }

    static string Plist(string role) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
            <key>CFBundleIdentifier</key><string>com.study-stash.app</string>
            <key>StudyStashRole</key><string>{role}</string>
        </dict>
        </plist>
        """;

    const string PlistMissingRole = """
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
            <key>CFBundleIdentifier</key><string>com.study-stash.app</string>
        </dict>
        </plist>
        """;

    [Fact]
    public void Mac_role_comes_from_the_bundle_Info_plist()
    {
        using var library = new TempDir();
        Assert.Equal("library", Apps.RolePreset(Bundle(library, Plist("library")), "Darwin"));

        using var laptop = new TempDir();
        Assert.Equal("laptop", Apps.RolePreset(Bundle(laptop, Plist("laptop")), "Darwin"));

        using var missingKey = new TempDir();
        Assert.Null(Apps.RolePreset(Bundle(missingKey, PlistMissingRole), "Darwin"));

        using var garbage = new TempDir();
        Assert.Null(Apps.RolePreset(Bundle(garbage, "not a plist at all \0\x1\x2"), "Darwin"));

        using var unknown = new TempDir();
        Assert.Null(Apps.RolePreset(Bundle(unknown, Plist("laptop-and-library")), "Darwin"));
    }

    [Fact]
    public void A_build_folder_has_no_mac_role_preset()
    {
        using var t = new TempDir();
        string bin = Path.Combine(t.Path, "bin", "Debug", "net10.0");
        Directory.CreateDirectory(bin);
        Assert.Null(Apps.RolePreset(bin, "Darwin"));
    }

    [Fact]
    public void Windows_role_comes_from_study_stash_ini()
    {
        using var plain = new TempDir();
        File.WriteAllText(plain["study-stash.ini"], "[app]\nrole=library\nversion=0.4.4\n");
        Assert.Equal("library", Apps.RolePreset(plain.Path, "Windows"));

        using var spacedCase = new TempDir();
        File.WriteAllText(spacedCase["study-stash.ini"], "[App]\nRole = Library\n");
        Assert.Equal("library", Apps.RolePreset(spacedCase.Path, "Windows"));

        using var crlfBom = new TempDir();
        byte[] bom = [0xEF, 0xBB, 0xBF];
        File.WriteAllBytes(crlfBom["study-stash.ini"], [.. bom, .. "[app]\r\nrole=library\r\n"u8.ToArray()]);
        Assert.Equal("library", Apps.RolePreset(crlfBom.Path, "Windows"));

        using var missing = new TempDir();
        Assert.Null(Apps.RolePreset(missing.Path, "Windows"));
    }

    [Fact]
    public void An_unrecognised_system_has_no_role_preset()
    {
        using var t = new TempDir();
        Assert.Null(Apps.RolePreset(t.Path, "Linux"));
    }

    [Fact]
    public void MacBundleOf_needs_the_real_bundle_id_and_no_translocation()
    {
        using var ok = new TempDir();
        Assert.NotNull(Apps.MacBundleOf(Bundle(ok, Plist("laptop"))));

        using var wrongId = new TempDir();
        string wrongPlist = Plist("laptop").Replace("com.study-stash.app", "com.example.other");
        Assert.Null(Apps.MacBundleOf(Bundle(wrongId, wrongPlist)));

        // a path still under Gatekeeper's quarantine translocation folder never resolves, even with a real bundle
        using var translocated = new TempDir();
        string translocatedContents = Path.Combine(translocated.Path, "AppTranslocation", "D1E2F3", "d", "Study Stash.app", "Contents");
        Directory.CreateDirectory(Path.Combine(translocatedContents, "MacOS", "arm64"));
        File.WriteAllText(Path.Combine(translocatedContents, "Info.plist"), Plist("laptop"));
        Assert.Null(Apps.MacBundleOf(Path.Combine(translocatedContents, "MacOS", "arm64")));
    }

    [Fact]
    public void A_build_folder_is_not_a_mac_bundle()
    {
        using var t = new TempDir();
        string bin = Path.Combine(t.Path, "bin", "Debug", "net10.0");
        Directory.CreateDirectory(bin);
        Assert.Null(Apps.MacBundleOf(bin));
    }

    [Fact]
    public void WindowsInstallOf_needs_the_uninstaller_beside_the_exe()
    {
        using var t = new TempDir();
        File.WriteAllText(t["StudyStash.exe"], "");
        Assert.Null(Apps.WindowsInstallOf(t.Path));

        File.WriteAllText(t["unins000.exe"], "");
        Assert.Equal(t.Path, Apps.WindowsInstallOf(t.Path));
    }
}
