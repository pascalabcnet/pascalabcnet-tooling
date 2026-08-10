namespace PascalABCNet.LanguageServices;

public sealed record DocumentAnalysis(
    string DocumentId,
    int? Version,
    bool IsCompiled,
    string? LastConversionError);

public sealed record CompletionItem(
    string Label,
    string? Detail,
    string Kind);

public sealed record HoverInfo(string Contents);

public sealed record SignatureHelpInfo(
    IReadOnlyList<string> Signatures,
    int ActiveSignature,
    int ActiveParameter,
    int ParameterCount);
