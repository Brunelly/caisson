using Caisson.Drivers.MikroTik.Transport;
using FluentAssertions;
using Xunit;

namespace Caisson.Drivers.MikroTik.Tests;

/// <summary>
/// Finding #10: the RouterOS reader caps each word's length, but a peer could still amplify wire bytes
/// into heap roughly 20x via an unbounded WORD COUNT or ROW COUNT. These tests exercise the three added
/// caps: <see cref="RouterOsSentence.MaxWordsPerSentence"/>, <see cref="RouterOsSentence.MaxSentenceBytes"/>
/// (the aggregate-byte budget alongside the existing per-word cap) and
/// <see cref="RouterOsApiClient.MaxRowsPerReply"/>.
/// </summary>
public sealed class ReaderCapsTests
{
    [Fact]
    public async Task A_sentence_with_more_words_than_the_cap_is_rejected()
    {
        var words = Enumerable.Repeat("a", RouterOsSentence.MaxWordsPerSentence + 1).ToArray();
        using var stream = new MemoryStream();
        await RouterOsSentence.WriteAsync(stream, words, CancellationToken.None);
        stream.Position = 0;

        var act = () => RouterOsSentence.ReadAsync(stream, CancellationToken.None);

        await act.Should().ThrowAsync<RouterOsApiException>().WithMessage("*word cap*");
    }

    [Fact]
    public async Task A_sentence_whose_aggregate_word_bytes_exceed_the_budget_is_rejected()
    {
        // Two words each just under the per-word cap sum to just under the aggregate budget; a third,
        // tiny word tips the running total over MaxSentenceBytes even though every individual word is
        // comfortably under MaxWordLength.
        var big = new string('a', RouterOsSentence.MaxWordLength - 1);
        var words = new[] { big, big, "abcde" };
        using var stream = new MemoryStream();
        await RouterOsSentence.WriteAsync(stream, words, CancellationToken.None);
        stream.Position = 0;

        var act = () => RouterOsSentence.ReadAsync(stream, CancellationToken.None);

        await act.Should().ThrowAsync<RouterOsApiException>().WithMessage("*byte aggregate cap*");
    }

    [Fact]
    public async Task A_reply_with_more_rows_than_the_cap_is_rejected()
    {
        using var buffer = new MemoryStream();
        for (var i = 0; i <= RouterOsApiClient.MaxRowsPerReply; i++)
        {
            await RouterOsSentence.WriteAsync(buffer, new[] { "!re" }, CancellationToken.None);
        }

        await RouterOsSentence.WriteAsync(buffer, new[] { "!done" }, CancellationToken.None);

        var settings = new RouterOsConnectionSettings(
            "10.0.0.1", 8729, UseTls: true, "reader", "pass", TimeSpan.FromSeconds(5));
        await using var client = new RouterOsApiClient(
            settings, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, new OneWayReplyStream(buffer.ToArray()));

        var act = () => client.SendCommandAsync("/interface/print", CancellationToken.None);

        await act.Should().ThrowAsync<RouterOsApiException>().WithMessage("*row cap*");
    }
}
