namespace ZipP2P.Contracts;

/// <summary>Tipo de descrição SDP (oferta ou resposta).</summary>
public enum SignalDescriptionType
{
    Offer,
    Answer
}

/// <summary>Descrição SDP trocada entre os pares via sinalização.</summary>
public sealed record SignalDescription(string Sdp, SignalDescriptionType Type);

/// <summary>Candidato ICE trocado entre os pares via sinalização.</summary>
public sealed record SignalCandidate(string Content, string Mid);
