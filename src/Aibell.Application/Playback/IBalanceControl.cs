namespace Aibell.Application.Playback;

/// <summary>Optional engine capability: the left/right balance, -1 (left only) to 1 (right only), 0 centred.</summary>
public interface IBalanceControl
{
    double Balance { get; }

    Task SetBalanceAsync(double balance, CancellationToken cancellationToken = default);
}
