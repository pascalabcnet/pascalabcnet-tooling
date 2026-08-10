namespace PascalABCNet.LanguageServices;

public interface IPascalLanguageService
{
    DocumentStorage Documents { get; }

    Task<DocumentAnalysis> OpenOrUpdateDocumentAsync(
        string documentId,
        string fileName,
        string text,
        int? version = null,
        CancellationToken cancellationToken = default);

    Task<bool> CloseDocumentAsync(
        string documentId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CompletionItem>> GetCompletionAfterDotAsync(
        string documentId,
        int caretOffset,
        CancellationToken cancellationToken = default);

    Task<HoverInfo?> GetHoverAsync(
        string documentId,
        int offset,
        CancellationToken cancellationToken = default);

    Task<SignatureHelpInfo?> GetSignatureHelpAsync(
        string documentId,
        int caretOffset,
        char triggerCharacter,
        int currentParameter = 1,
        int currentParameterForSelection = 1,
        CancellationToken cancellationToken = default);
}
