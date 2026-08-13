namespace PascalABCNet.LanguageServices;

public sealed record DocumentAnalysis(
    string DocumentId,
    int? Version,
    bool IsCompiled,
    string? LastConversionError);

public sealed record CompletionItem(
    string Label,
    string? Detail,
    string? Documentation,
    string Kind);

public sealed record HoverInfo(string Contents);

public sealed record SignatureInfo(
    string Label,
    string? Documentation,
    IReadOnlyList<string> Parameters);

public sealed record SignatureHelpInfo(
    IReadOnlyList<SignatureInfo> Signatures,
    int ActiveSignature,
    int ActiveParameter,
    int ParameterCount);
