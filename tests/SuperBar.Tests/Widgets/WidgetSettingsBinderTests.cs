using System.Text.Json.Nodes;
using SuperBar.Widgets;

namespace SuperBar.Tests.Widgets;

public class WidgetSettingsBinderTests
{
    private static Sample Bind(string json) => WidgetSettingsBinder.Bind<Sample>(JsonNode.Parse(json)!.AsObject());

    [Fact]
    public void Missing_settings_keep_their_defaults()
    {
        var defaults = WidgetSettingsBinder.Bind<Sample>(null);
        Assert.Equal(25, defaults.Minutes);
        Assert.True(defaults.Sound);

        var partial = Bind("""{ "minutes": 40 }""");
        Assert.Equal(40, partial.Minutes);
        Assert.True(partial.Sound);
        Assert.Equal(Density.Compact, partial.Density);
        Assert.Equal("Focus", partial.Label);
    }

    [Fact]
    public void Typed_json_values_are_bound_by_case_insensitive_name()
    {
        var s = Bind("""{ "MINUTES": 50, "sound": false, "Density": "Detailed", "opacity": 0.5, "label": "Deep work", "tags": ["a", "b"] }""");

        Assert.Equal(50, s.Minutes);
        Assert.False(s.Sound);
        Assert.Equal(Density.Detailed, s.Density);
        Assert.Equal(0.5, s.Opacity);
        Assert.Equal("Deep work", s.Label);
        Assert.Equal(["a", "b"], s.Tags);
    }

    [Fact]
    public void Values_stored_as_strings_by_the_first_settings_format_are_converted()
    {
        var s = Bind("""
            {
              "minutes": "40", "sound": "false", "density": "detailed", "opacity": "0.75",
              "limit": "12", "pause": "00:05:00", "tags": "[\"x\",\"y\"]"
            }
            """);

        Assert.Equal(40, s.Minutes);
        Assert.False(s.Sound);
        Assert.Equal(Density.Detailed, s.Density);
        Assert.Equal(0.75, s.Opacity);
        Assert.Equal(12, s.Limit);
        Assert.Equal(TimeSpan.FromMinutes(5), s.Pause);
        Assert.Equal(["x", "y"], s.Tags);
    }

    [Theory]
    [InlineData("\"true\"", true)]
    [InlineData("\"True\"", true)]
    [InlineData("\"1\"", true)]
    [InlineData("\"on\"", true)]
    [InlineData("\"FALSE\"", false)]
    [InlineData("\"0\"", false)]
    [InlineData("\"off\"", false)]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public void Booleans_are_read_from_every_spelling(string json, bool expected) =>
        Assert.Equal(expected, Bind($$"""{ "sound": {{json}} }""").Sound);

    [Fact]
    public void A_value_that_cannot_be_converted_only_resets_its_own_setting()
    {
        var s = Bind("""{ "minutes": "soon", "sound": "maybe", "density": "Huge", "opacity": [1], "limit": "x", "label": "Kept" }""");

        // Nothing threw; the bad values fell back to their defaults ...
        Assert.Equal(25, s.Minutes);
        Assert.True(s.Sound);
        Assert.Equal(Density.Compact, s.Density);
        Assert.Equal(1.0, s.Opacity);
        Assert.Null(s.Limit);

        // ... and the good one next to them survived.
        Assert.Equal("Kept", s.Label);
    }

    [Fact]
    public void Null_values_and_read_only_properties_are_left_alone()
    {
        var s = Bind("""{ "minutes": null, "label": null, "computed": 99 }""");

        Assert.Equal(25, s.Minutes);
        Assert.Equal("Focus", s.Label);
        Assert.Equal(1, s.Computed);
    }

    [Fact]
    public void A_number_stored_where_text_is_expected_becomes_text()
    {
        Assert.Equal("7", Bind("""{ "label": 7 }""").Label);
    }

    private enum Density
    {
        Compact,
        Detailed,
    }

    private sealed class Sample
    {
        public int Minutes { get; set; } = 25;
        public bool Sound { get; set; } = true;
        public Density Density { get; set; } = Density.Compact;
        public double Opacity { get; set; } = 1.0;
        public int? Limit { get; set; }
        public TimeSpan Pause { get; set; } = TimeSpan.FromMinutes(1);
        public string Label { get; set; } = "Focus";
        public List<string> Tags { get; set; } = new();
        public int Computed => 1;
    }
}
