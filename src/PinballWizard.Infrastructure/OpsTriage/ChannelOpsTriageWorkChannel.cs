using System.Threading.Channels;
using Microsoft.Extensions.Options;
using PinballWizard.Application.OpsTriage;
using PinballWizard.Core.Configuration;

namespace PinballWizard.Infrastructure.OpsTriage;

public sealed class ChannelOpsTriageWorkChannel : IOpsTriageWorkChannel
{
    private readonly int _capacity;
    private readonly Channel<(string TriageRunId, string AlertDay)> _channel;

    public ChannelOpsTriageWorkChannel(IOptions<OpsTriageOptions> options)
    {
        _capacity = Math.Clamp(options.Value.QueueCapacity, 1, 1000);
        _channel = Channel.CreateBounded<(string, string)>(new BoundedChannelOptions(_capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
    }

    public ValueTask EnqueueAsync(string triageRunId, string alertDay, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(triageRunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(alertDay);

        // TryWrite so a full queue fails fast (webhook → 503) instead of
        // blocking the Monitor callback (AGENT-12).
        if (!_channel.Writer.TryWrite((triageRunId, alertDay)))
        {
            throw new InvalidOperationException(
                $"Ops triage queue is full (capacity={_capacity}).");
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<(string TriageRunId, string AlertDay)> DequeueAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAsync(cancellationToken);
}
