using System.Threading.Channels;
using PinballWizard.Application.OpsTriage;

namespace PinballWizard.Infrastructure.OpsTriage;

public sealed class ChannelOpsTriageWorkChannel : IOpsTriageWorkChannel
{
    private readonly Channel<(string TriageRunId, string AlertDay)> _channel =
        Channel.CreateUnbounded<(string, string)>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

    public ValueTask EnqueueAsync(string triageRunId, string alertDay, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(triageRunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(alertDay);
        return _channel.Writer.WriteAsync((triageRunId, alertDay), cancellationToken);
    }

    public ValueTask<(string TriageRunId, string AlertDay)> DequeueAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAsync(cancellationToken);
}
