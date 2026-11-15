using Microsoft.Extensions.Options;
using PinballWizard.Application.OpsTriage;
using PinballWizard.Core.Configuration;

namespace PinballWizard.Infrastructure.OpsTriage;

/// <summary>Process-local rolling hourly cap for Ops Triage Foundry calls.</summary>
public sealed class HourlyOpsTriageInvocationBudget : IOpsTriageInvocationBudget
{
    private readonly OpsTriageOptions _options;
    private readonly object _gate = new();
    private DateTimeOffset _windowStartUtc = DateTimeOffset.UtcNow;
    private int _count;

    public HourlyOpsTriageInvocationBudget(IOptions<OpsTriageOptions> options)
    {
        _options = options.Value;
    }

    public bool TryAcquire(out string? denyReason)
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            if (now - _windowStartUtc >= TimeSpan.FromHours(1))
            {
                _windowStartUtc = now;
                _count = 0;
            }

            if (_count >= _options.MaxDiagnosesPerHour)
            {
                denyReason =
                    $"Ops Triage hourly diagnosis budget exhausted ({_options.MaxDiagnosesPerHour}/hour).";
                return false;
            }

            _count++;
            denyReason = null;
            return true;
        }
    }
}
