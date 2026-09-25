using System.Text.Json.Nodes;

namespace StudyStash.Core.Tests;

/// <summary>Reading lectures: tests/test_granola.py's date, participant and normalize tests, plus Python's own answers.</summary>
public class MeetingTests
{
    static JsonObject Dict(Meeting m) => (JsonObject)JsonNode.Parse(Granola.MeetingJson(m))!;

    [Fact]
    public void Normalize_maps_alternate_keys()
    {
        var m = Granola.NormalizeMeeting((JsonObject)JsonNode.Parse("""
            {"document_id": "doc1", "title": "Bio 101 - Cells", "start_time": "2026-09-14T10:00:00Z",
             "summary_markdown": "# Cells\n- membranes", "attendees": [{"name": "Alex", "email": "j@x"}, "Sam"],
             "folder": {"name": "Biology"}, "creator": {"name": "Alex"}}
            """)!);
        Assert.Equal("doc1", m.Id);
        Assert.Equal("Bio 101 - Cells", m.Title);
        Assert.StartsWith("2026-09-14", m.Date);
        Assert.StartsWith("# Cells", m.NotesMarkdown);
        Assert.Equal(["Alex", "Sam"], m.Attendees);
        Assert.Equal("Biology", m.Folder);
        Assert.Equal("Alex", m.Owner);
    }

    [Fact]
    public void Normalize_matches_python()
    {
        foreach (var c in Golden.Cases("normalize"))
        {
            var expected = c![1]!.AsObject();
            try
            {
                var got = Dict(Granola.NormalizeMeeting(c[0]!.AsObject()));
                Assert.True(expected.ContainsKey("ok"), $"Python refused {c[0]!.ToJsonString()}");
                Assert.True(JsonNode.DeepEquals(expected["ok"], got), $"{expected["ok"]!.ToJsonString()}\n{got.ToJsonString()}");
            }
            catch (PayloadException)
            {
                Assert.Equal("ValueError", expected["error"]?.GetValue<string>());
            }
        }
    }

    [Fact]
    public void Participant_lines()
    {
        Assert.Equal(("John Doe", true, "john@acme.com"), Granola.ParseParticipant("John Doe (note creator) from Acme <john@acme.com>"));
        Assert.Equal(("Jane Smith", false, "jane@acme.com"), Granola.ParseParticipant("Jane Smith from Acme <jane@acme.com>"));
        Assert.Equal(("solo@x.com", false, "solo@x.com"), Granola.ParseParticipant("<solo@x.com>"));
        Assert.Equal(("", false, ""), Granola.ParseParticipant(""));
        foreach (var c in Golden.Cases("participant"))
        {
            var (name, creator, email) = Granola.ParseParticipant(c![0].S());
            var want = c[1]!.AsArray();
            Assert.Equal((want[0].S(), want[1]!.GetValue<bool>(), want[2].S()), (name, creator, email));
        }
    }

    [Fact]
    public void Participant_blocks_match_python()
    {
        foreach (var c in Golden.Cases("participants"))
        {
            var (names, creator) = Granola.ParseParticipants(c![0]);
            var want = c[1]!["ok"]!.AsArray();
            Assert.Equal(want[0]!.AsArray().Select(n => n.S()), names);
            Assert.Equal(want[1].S(), creator);
        }
    }

    [Fact]
    public void Granola_dates()
    {
        Assert.Equal("2026-02-04T19:30:00", Granola.ParseGranolaDate("Feb 4, 2026 7:30 PM"));
        Assert.Equal("2026-02-04", Granola.ParseGranolaDate("Feb 4, 2026"));
        Assert.Equal("2026-02-04T19:30:00Z", Granola.ParseGranolaDate("2026-02-04T19:30:00Z")); // ISO passes through
        Assert.Equal("sometime next week", Granola.ParseGranolaDate("sometime next week"));
        // PM is not a timezone: dropping it would move a 7:30 PM lecture to the morning
        Assert.Equal("2026-09-15T14:20:00", Granola.ParseGranolaDate("Sep 15, 2026 2:20 PM PDT"));
        Assert.Equal("2026-02-04T07:30:00", Granola.ParseGranolaDate("Feb 4, 2026 7:30 AM"));
        foreach (var c in Golden.Cases("dates"))
            Assert.Equal(c![1].S(), Granola.ParseGranolaDate(c[0].S()));
    }

    [Fact]
    public void The_wire_format_matches_python_byte_for_byte()
    {
        foreach (var c in Golden.Cases("from_dict"))
        {
            var expected = c![1]!.AsObject();
            try
            {
                string got = Granola.MeetingJson(Granola.MeetingFromJson(c[0]));
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
        Assert.Throws<PayloadException>(() => Granola.MeetingFromJson(JsonNode.Parse("[1]")));
        Assert.Throws<PayloadException>(() => Granola.MeetingFromJson(null));
        Assert.Throws<PayloadException>(() => Granola.NormalizeMeeting(new JsonObject { ["title"] = "x" }));
    }
}
