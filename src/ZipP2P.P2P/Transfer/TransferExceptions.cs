namespace ZipP2P.P2P.Transfer;

/// <summary>A conexão com o par caiu ou ficou inativa. A transferência pode ser retomada.</summary>
public sealed class ConnectionLostException : Exception
{
    public ConnectionLostException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>O par encerrou a transferência intencionalmente (cancelamento ou erro do lado dele).</summary>
public sealed class TransferAbortedException : Exception
{
    public TransferAbortedException(string reason)
        : base(reason)
    {
        Reason = reason;
    }

    public string Reason { get; }
}
