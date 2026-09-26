using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StudyStash.Core.Tests;

/// <summary>The Python behaviour both engines depend on, checked against what Python itself printed.</summary>
public class PyTests
{
    static double D(JsonNode? n) => double.Parse(n!.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture);

    [Fact]
    public void Floats_are_written_the_way_python_writes_them()
    {
        foreach (var c in Golden.Cases("floats"))
            Assert.Equal(c![1].S(), Py.FloatRepr(D(c[0])));
        Assert.Equal("nan", Py.FloatRepr(double.NaN));
        Assert.Equal("-inf", Py.FloatRepr(double.NegativeInfinity));
    }

    [Fact]
    public void Two_decimals_round_the_exact_value_half_to_even()
    {
        foreach (var c in Golden.Cases("fixed2"))
            Assert.Equal(c![1].S(), Py.FormatFixed(D(c[0]), 2));
        Assert.Equal("3", Py.FormatFixed(2.5000001, 0));
        Assert.Equal("2", Py.FormatFixed(2.5, 0));
        Assert.Equal("-0.50", Py.FormatFixed(-0.5, 2));
    }

    [Fact]
    public void Json_comes_out_as_json_dumps_writes_it()
    {
        foreach (var c in Golden.Cases("dumps"))
            Assert.Equal(c![1].S(), PyJson.Dumps(JsonNode.Parse(c[0].S())));
        Assert.Equal("[\"a\", \"\\u00e9\"]", PyJson.Dumps(["a", "é"]));
    }

    [Fact]
    public void Json_columns_parse_in_both_directions()
    {
        // Numbers Python keeps exactly (a 20-digit int) and floats it rewrites (2.50 → 2.5) stay valid JSON here too.
        var raw = (JsonObject)JsonNode.Parse("""{"big": 12345678901234567890, "f": 2.50}""")!;
        Assert.Equal("{\"big\": 12345678901234567890, \"f\": 2.5}", PyJson.Dumps(raw));
        Assert.Equal(JsonValueKind.Object, JsonDocument.Parse(PyJson.Dumps(raw)).RootElement.ValueKind);
    }

    [Fact]
    public void Splitlines_knows_every_line_break_python_does()
    {
        Assert.Equal(["a", "b", "", "c", "d", "e"], Py.SplitLines("a\nb\r\n\rc\u2028d\u001ce\n"));
        Assert.Empty(Py.SplitLines(""));
        Assert.Equal([""], Py.SplitLines("\n"));
    }

    [Fact]
    public void Strip_takes_the_same_whitespace_as_python()
    {
        Assert.Equal("x", Py.Strip("\u001f \u00a0\u2003x\t\n\u3000"));
        Assert.Equal("", Py.Strip(null));
    }

    [Fact]
    public void Cutting_text_never_splits_an_emoji()
    {
        Assert.Equal("ab", Py.Head("ab😀", 3));
        Assert.Equal("😀c", Py.Tail("ab😀c", 2));
        Assert.Equal("abc", Py.Head("abc", 10));
    }

    [Fact]
    public void Paths_are_spelled_as_pathlib_spells_them()
    {
        Assert.Equal("/srv/notes/fall", Py.NormPosix("/srv//notes/./fall/"));
        Assert.Equal("//host/x", Py.NormPosix("//host/x"));
        Assert.Equal("/x", Py.NormPosix("///x"));
        Assert.Equal("notes", Py.NormPosix("./notes"));
        Assert.Equal(".", Py.NormPosix(""));
        Assert.Equal(@"C:\Users\sam\notes", Py.NormWindows("C:/Users//sam/notes/"));
        Assert.Equal(@"C:\", Py.NormWindows("C:/"));
        Assert.Equal("C:", Py.NormWindows("C:"));
        Assert.Equal(@"\\server\share\x", Py.NormWindows("//server/share/x/"));
        Assert.Equal(@"\srv\notes", Py.NormWindows("/srv/notes"));
    }

    [Fact]
    public void Tilde_means_your_home_folder()
    {
        Assert.Equal(Py.NormPath(Path.Combine(Py.UserHome(), "Study Stash")), Py.ExpandUser("~/Study Stash"));
        Assert.Equal(Py.NormPath(Py.UserHome()), Py.ExpandUser("~"));
        Assert.Equal(Py.NormPath("~other/x"), Py.ExpandUser("~other/x"));
    }

    [Fact]
    public void Python_str_and_truthiness_of_json_values()
    {
        Assert.Equal("True", Py.Str(JsonNode.Parse("true")));
        Assert.Equal("1.5", Py.Str(JsonNode.Parse("1.50")));
        Assert.Equal("None", Py.Str(null));
        Assert.Equal("['a', 1, {'k': None}]", Py.Str(JsonNode.Parse("[\"a\", 1, {\"k\": null}]")));
        Assert.Equal("\"it's\"", Py.StrRepr("it's"));
        Assert.Equal("'caf\u00e9\\n\\x00'", Py.StrRepr("caf\u00e9\n\0"));
        foreach (string falsy in new[] { "0", "0.0", "\"\"", "[]", "{}", "false", "null" })
            Assert.False(Py.Truthy(JsonNode.Parse(falsy)), falsy);
        foreach (string truthy in new[] { "1", "-0.5", "\" \"", "[0]", "{\"a\": 0}", "true" })
            Assert.True(Py.Truthy(JsonNode.Parse(truthy)), truthy);
    }

    [Fact]
    public void Text_files_read_and_write_like_pythons_text_mode()
    {
        using var dir = new TempDir();
        File.WriteAllBytes(dir["a.md"], "one\r\ntwo\rthree\n"u8.ToArray());
        Assert.Equal("one\ntwo\nthree\n", Py.ReadText(dir["a.md"]));
        Py.WriteText(dir["b.md"], "x\ny\n");
        Assert.Equal(OperatingSystem.IsWindows() ? "x\r\ny\r\n" : "x\ny\n", File.ReadAllText(dir["b.md"]));
    }
}
