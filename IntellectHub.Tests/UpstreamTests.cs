using IntellectHub;
using Xunit;

namespace IntellectHub.Tests;

public class UpstreamTests
{
    [Fact]
    public void ExtractSessionId_ReadsNestedId()
    {
        var json = "{\"data\":{\"biz_data\":{\"chat_session\":{\"id\":\"sess-1\"}}}}";
        Assert.Equal("sess-1", DeepSeekJson.ExtractSessionId(json));
    }

    [Fact]
    public void ExtractSessionId_FallsBackToBizId()
    {
        var json = "{\"data\":{\"biz_data\":{\"id\":\"sess-2\"}}}";
        Assert.Equal("sess-2", DeepSeekJson.ExtractSessionId(json));
    }

    [Fact]
    public void ExtractChallenge_ParsesFields()
    {
        var json = "{\"data\":{\"biz_data\":{\"challenge\":{\"algorithm\":\"sha3\",\"challenge\":\"abc\",\"salt\":\"s\",\"expire_at\":123,\"difficulty\":4,\"signature\":\"sig\"}}}}";
        var ch = DeepSeekJson.ExtractChallenge(json);
        Assert.NotNull(ch);
        Assert.Equal("sha3", ch!.Algorithm);
        Assert.Equal("abc", ch.Challenge);
        Assert.Equal("s", ch.Salt);
        Assert.Equal(123, ch.ExpireAt);
        Assert.Equal(4, ch.Difficulty);
        Assert.Equal("sig", ch.Signature);
    }

    [Fact]
    public void PowPayload_RoundTripsBase64()
    {
        var ch = new DeepSeekChallenge { Algorithm = "sha3", Challenge = "abc", Salt = "s", Signature = "sig" };
        var b64 = PowPayload.BuildBase64(ch, 42, "/api/v0/chat/completion");
        var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(b64));
        Assert.Contains("\"answer\":42", json);
        Assert.Contains("\"target_path\":\"/api/v0/chat/completion\"", json);
        Assert.Contains("\"challenge\":\"abc\"", json);
    }

    [Fact]
    public void Sse_AccumulatesContentFrames()
    {
        var acc = new SseAccumulator();
        acc.Consume("data: {\"p\":\"response/content\",\"v\":\"Hello \"}");
        acc.Consume("data: {\"p\":\"response/content\",\"v\":\"world\"}");
        Assert.Equal("Hello world", acc.Content);
    }

    [Fact]
    public void Sse_CapturesMessageIdAndFinishReason()
    {
        var acc = new SseAccumulator();
        acc.Consume("data: {\"response_message_id\":\"m1\"}");
        acc.Consume("data: {\"p\":\"response/content\",\"v\":\"hi\"}");
        acc.Consume("data: {\"p\":\"response/finish_reason\",\"v\":\"stop\"}");
        Assert.Equal("m1", acc.MessageId);
        Assert.Equal("stop", acc.FinishReason);
        Assert.Equal("hi", acc.Content);
    }

    [Fact]
    public void Sse_IgnoresNonDataLines()
    {
        var acc = new SseAccumulator();
        Assert.False(acc.Consume("event: hint"));
        Assert.False(acc.Consume(""));
        Assert.Equal("", acc.Content);
    }

    [Fact]
    public void Sse_ObjectValueOnContentPath_DoesNotAppend()
    {
        var acc = new SseAccumulator();
        acc.Consume("data: {\"p\":\"response/content\",\"v\":{\"nested\":1}}");
        Assert.Equal("", acc.Content);
    }
}
