namespace Condux.Core.Http;

/// <summary>
/// A request body read whole, and the share of a <see cref="ByteBudget"/> it holds while the request works
/// on it. Disposing it gives the share back, so the request that read it decides when it is done.
/// </summary>
public sealed class HeldBody : IDisposable
{
    private readonly ByteBudget _budget;
    private int _released;

    // Only the reader that charged the budget for these bytes may hand them over, or a body could give back
    // what it never took.
    internal HeldBody(byte[] content, ByteBudget budget)
    {
        Content = content;
        _budget = budget;
    }

    public byte[] Content { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
        {
            _budget.Release(Content.Length);
        }
    }
}

/// <summary>Why a body was not read whole.</summary>
public enum BodyRefusal
{
    /// <summary>It was read: the <see cref="BodyRead.Body"/> holds it.</summary>
    None,

    /// <summary>It passed the most bytes the request may carry.</summary>
    TooLarge,

    /// <summary>Its bytes would have taken more of the budget than is free.</summary>
    OverBudget,

    /// <summary>Its bytes did not decode as the encoding the request declared, such as a body sent as gzip
    /// that is not gzip: the decoding stream in front of the read refused them. The sender's to fix.</summary>
    Undecodable,
}

/// <summary>The body, or why it was refused.</summary>
public readonly record struct BodyRead(HeldBody? Body, BodyRefusal Refusal);
