using System.Text.Json.Nodes;

namespace StudyStash.Core.Tests;

/// <summary>The wire format a lecture travels in, from the laptop to the library, plus Python's own answers.</summary>
public class MeetingTests
{
    [Fact]
    public void The_wire_format_matches_python_byte_for_byte()
    {
        foreach (var c in Golden.Cases("from_dict"))
        {
            var expected = c![1]!.AsObject();
            try
            {
                string got = Wire.MeetingJson(Wire.MeetingFromJson(c[0]));
                Assert.Equal(expected["ok"].S(), got);
            }
            catch (PayloadException)
            {
                Assert.Equal("ValueError", expected["error"]?.GetValue<string>());
            }
        }
    }

    [Fact]
    public void A_payload_that_is_not_a_lecture_is_refused()
    {
        Assert.Throws<PayloadException>(() => Wire.MeetingFromJson(JsonNode.Parse("[1]")));
        Assert.Throws<PayloadException>(() => Wire.MeetingFromJson(null));
        Assert.Throws<PayloadException>(() => Wire.MeetingFromJson(new JsonObject { ["title"] = "x" }));
    }
}
