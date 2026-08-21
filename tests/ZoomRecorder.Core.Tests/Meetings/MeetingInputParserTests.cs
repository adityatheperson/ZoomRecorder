using ZoomRecorder.Core.Meetings;

namespace ZoomRecorder.Core.Tests.Meetings;

public sealed class MeetingInputParserTests
{
    [Theory]
    [InlineData("123 456 7890", "1234567890", null)]
    [InlineData("https://zoom.us/j/1234567890?pwd=abc", "1234567890", "abc")]
    [InlineData("https://acme.zoom.us/wc/9876543210/join", "9876543210", null)]
    public void Parse_extracts_meeting_id_and_embedded_passcode(string input, string expectedId, string? expectedPasscode)
    {
        var result = MeetingInputParser.Parse(input, null);

        Assert.Equal(expectedId, result.MeetingId);
        Assert.Equal(expectedPasscode, result.Passcode);
    }

    [Fact]
    public void Parse_explicit_passcode_overrides_link_passcode()
    {
        var result = MeetingInputParser.Parse("https://zoom.us/j/1234567890?pwd=link", "typed");

        Assert.Equal("typed", result.Passcode);
    }

    [Theory]
    [InlineData("", "Enter a Zoom link or meeting ID.")]
    [InlineData("123", "Enter a valid Zoom link or meeting ID.")]
    public void Parse_rejects_invalid_meeting_input(string input, string expectedMessage)
    {
        var exception = Assert.Throws<MeetingInputException>(() => MeetingInputParser.Parse(input, null));

        Assert.Equal(expectedMessage, exception.Message);
    }

    [Fact]
    public void Meeting_id_does_not_require_a_display_name()
    {
        var request = MeetingInputParser.Parse("123 456 7890", " pass ");

        Assert.Equal("1234567890", request.MeetingId);
        Assert.Equal("pass", request.Passcode);
    }
}
