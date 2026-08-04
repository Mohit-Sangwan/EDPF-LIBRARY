using Edpf.Abstractions.Communication;
using Edpf.Abstractions.Primitives;
using Edpf.Communication;

namespace Edpf.ConformanceTests;

/// <summary>
/// The pickup-directory email channel, against a real directory.
/// </summary>
/// <remarks>
/// Here rather than in the unit tests because it writes files (Z.7). Without
/// this, "EDPF sends email" would be a claim resting on a class that had never
/// once produced a message — the precise failure mode already found six times
/// in this programme.
/// </remarks>
public sealed class PickupDirectoryEmailChannelTests : IDisposable
{
    private readonly string _pickup = Path.Combine(
        Path.GetTempPath(), "edpf-pickup", Guid.NewGuid().ToString("N"));

    private readonly PickupDirectoryEmailChannel _channel;

    public PickupDirectoryEmailChannelTests()
        => _channel = new PickupDirectoryEmailChannel(_pickup, MessageAddress.ForEmail("clinic@example.com"));

    [Fact]
    public async Task SendAsync_WritesAWellFormedMessageToThePickupDirectory()
    {
        var message = new OutboundMessage(
            MessageAddress.ForEmail("alex@example.com"),
            "Appointment reminder",
            "Hello Alex, you have an appointment on 14 August.",
            DataClassificationLevel.Internal);

        Assert.True((await _channel.SendAsync(message, default)).IsSuccess);

        string file = Assert.Single(Directory.GetFiles(_pickup, "*.eml"));
        string content = await File.ReadAllTextAsync(file);

        Assert.StartsWith("From: clinic@example.com\r\n", content, StringComparison.Ordinal);
        Assert.Contains("To: alex@example.com\r\n", content, StringComparison.Ordinal);
        Assert.Contains("Subject: Appointment reminder\r\n", content, StringComparison.Ordinal);

        // Headers end at the first blank line. Everything after it is body, and
        // the body is the only place caller text may introduce structure.
        int separator = content.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        Assert.True(separator > 0);
        Assert.Equal(message.Body, content.Substring(separator + 4));
    }

    [Fact]
    public async Task SendAsync_TwiceProducesTwoMessages()
    {
        // Filenames are generated, so a second reminder to the same person on
        // the same day does not silently overwrite the first.
        var message = new OutboundMessage(
            MessageAddress.ForEmail("alex@example.com"), "Reminder", "body", DataClassificationLevel.Public);

        await _channel.SendAsync(message, default);
        await _channel.SendAsync(message, default);

        Assert.Equal(2, Directory.GetFiles(_pickup, "*.eml").Length);
    }

    [Fact]
    public void Channel_DeclaresACeilingBelowPhi()
    {
        // Mail between organisations is opportunistically encrypted at best.
        // The default has to be conservative, because the deployment that
        // forgets to think about this is the one that needs the default.
        Assert.True(_channel.MaximumClassification < DataClassificationLevel.Phi);
    }

    [Fact]
    public void Channel_RefusesASenderThatIsNotAnEmailAddress()
    {
        Assert.Throws<ArgumentException>(
            () => new PickupDirectoryEmailChannel(_pickup, MessageAddress.ForPhone("+441234567890")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_pickup))
        {
            Directory.Delete(_pickup, recursive: true);
        }
    }
}
