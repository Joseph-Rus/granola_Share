using System.Text.RegularExpressions;

namespace StudyStash.Core.Tests;

/// <summary>Study Stash's version lives in one place, engine/Directory.Build.props, and the build reports that one.</summary>
public class VersionTests
{
    [Fact]
    public void The_engine_reports_the_version_directory_build_props_holds()
    {
        string props = PropsFile();
        var m = Regex.Match(File.ReadAllText(props), "<StudyStashVersion>([^<]*)</StudyStashVersion>");
        Assert.True(m.Success, $"no StudyStashVersion in {props}");
        Assert.Matches(@"^\d+\.\d+\.\d+$", m.Groups[1].Value);
        Assert.Equal(m.Groups[1].Value, Engine.Version);
    }

    /// <summary>engine/Directory.Build.props, found by walking up from where the tests run.</summary>
    static string PropsFile()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            string props = Path.Combine(d.FullName, "engine", "Directory.Build.props");
            if (File.Exists(props)) return props;
        }
        throw new FileNotFoundException("engine/Directory.Build.props");
    }
}
