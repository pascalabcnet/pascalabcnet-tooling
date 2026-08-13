using Microsoft.VisualStudio.LanguageServer.Protocol;
using PascalABCNet.LanguageServices;
using StreamJsonRpc;
using LspCompletionItem = Microsoft.VisualStudio.LanguageServer.Protocol.CompletionItem;

namespace PascalABCNet.LanguageServer;

internal sealed class PascalLanguageServerTarget
{
    private static readonly HashSet<char> SignatureTriggers = new() { '(', '[', ',' };

    private readonly IPascalLanguageService _languageService;
    private readonly SerialRequestDispatcher _dispatcher;
    private readonly TaskCompletionSource<bool> _exitSignal;

    public PascalLanguageServerTarget(
        IPascalLanguageService languageService,
        SerialRequestDispatcher dispatcher,
        TaskCompletionSource<bool> exitSignal)
    {
        _languageService = languageService;
        _dispatcher = dispatcher;
        _exitSignal = exitSignal;
    }

    [JsonRpcMethod("initialize", UseSingleObjectParameterDeserialization = true)]
    public Task<InitializeResult> InitializeAsync(
        InitializeParams request,
        CancellationToken cancellationToken) =>
        _dispatcher.RunAsync(
            () => Task.FromResult(new InitializeResult
            {
                Capabilities = new ServerCapabilities
                {
                    TextDocumentSync = new TextDocumentSyncOptions
                    {
                        OpenClose = true,
                        Change = TextDocumentSyncKind.Incremental,
                        Save = new SaveOptions { IncludeText = false }
                    },
                    CompletionProvider = new CompletionOptions
                    {
                        ResolveProvider = false,
                        TriggerCharacters = new[] { "." }
                    },
                    HoverProvider = true,
                    SignatureHelpProvider = new SignatureHelpOptions
                    {
                        TriggerCharacters = new[] { "(", "[", "," }
                    }
                }
            }),
            cancellationToken);

    [JsonRpcMethod("initialized", UseSingleObjectParameterDeserialization = true)]
    public Task InitializedAsync(InitializedParams notification, CancellationToken cancellationToken) =>
        _dispatcher.RunAsync(() => Task.CompletedTask, cancellationToken);

    [JsonRpcMethod("shutdown")]
    public Task<object?> ShutdownAsync(CancellationToken cancellationToken) =>
        _dispatcher.RunAsync<object?>(() => Task.FromResult<object?>(null), cancellationToken);

    [JsonRpcMethod("exit")]
    public Task ExitAsync(CancellationToken cancellationToken) =>
        _dispatcher.RunAsync(() =>
        {
            _exitSignal.TrySetResult(true);
            return Task.CompletedTask;
        }, cancellationToken);

    [JsonRpcMethod("textDocument/didOpen", UseSingleObjectParameterDeserialization = true)]
    public Task DidOpenAsync(
        DidOpenTextDocumentParams notification,
        CancellationToken cancellationToken) =>
        _dispatcher.RunAsync(
            () => _languageService.OpenOrUpdateDocumentAsync(
                DocumentConversions.GetDocumentId(notification.TextDocument.Uri),
                DocumentConversions.GetFileName(notification.TextDocument.Uri),
                notification.TextDocument.Text,
                notification.TextDocument.Version,
                cancellationToken),
            cancellationToken);

    [JsonRpcMethod("textDocument/didChange", UseSingleObjectParameterDeserialization = true)]
    public Task DidChangeAsync(
        DidChangeTextDocumentParams notification,
        CancellationToken cancellationToken) =>
        _dispatcher.RunAsync(async () =>
        {
            var documentId = DocumentConversions.GetDocumentId(notification.TextDocument.Uri);
            if (!_languageService.Documents.TryGet(documentId, out var document) ||
                document is null ||
                !DocumentConversions.TryApplyContentChanges(
                    document.Text,
                    notification.ContentChanges,
                    out var updatedText))
            {
                return;
            }

            await _languageService.QueueDocumentUpdateAsync(
                documentId,
                DocumentConversions.GetFileName(notification.TextDocument.Uri),
                updatedText,
                notification.TextDocument.Version,
                cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    [JsonRpcMethod("textDocument/didClose", UseSingleObjectParameterDeserialization = true)]
    public Task DidCloseAsync(
        DidCloseTextDocumentParams notification,
        CancellationToken cancellationToken) =>
        _dispatcher.RunAsync(
            () => _languageService.CloseDocumentAsync(
                DocumentConversions.GetDocumentId(notification.TextDocument.Uri),
                cancellationToken),
            cancellationToken);

    [JsonRpcMethod("textDocument/didSave", UseSingleObjectParameterDeserialization = true)]
    public Task DidSaveAsync(
        DidSaveTextDocumentParams notification,
        CancellationToken cancellationToken) =>
        _dispatcher.RunAsync(() => Task.CompletedTask, cancellationToken);

    [JsonRpcMethod("textDocument/completion", UseSingleObjectParameterDeserialization = true)]
    public Task<CompletionList> CompletionAsync(
        CompletionParams request,
        CancellationToken cancellationToken) =>
        _dispatcher.RunAsync(async () =>
        {
            if (!TryGetTriggerOffset(request.TextDocument.Uri, request.Position, '.', out var caretOffset))
                return EmptyCompletionList();

            var items = await _languageService.GetCompletionAfterDotAsync(
                DocumentConversions.GetDocumentId(request.TextDocument.Uri),
                caretOffset,
                cancellationToken).ConfigureAwait(false);

            return new CompletionList
            {
                IsIncomplete = false,
                Items = items.Select(item => new LspCompletionItem
                {
                    Label = item.Label,
                    Detail = item.Detail,
                    Documentation = item.Documentation,
                    Kind = MapCompletionKind(item.Kind),
                    InsertText = item.Label.EndsWith("<>", StringComparison.Ordinal)
                        ? item.Label[..^2]
                        : item.Label
                }).ToArray()
            };
        }, cancellationToken);

    [JsonRpcMethod("textDocument/hover", UseSingleObjectParameterDeserialization = true)]
    public Task<Hover?> HoverAsync(
        TextDocumentPositionParams request,
        CancellationToken cancellationToken) =>
        _dispatcher.RunAsync(async () =>
        {
            if (!DocumentConversions.TryGetOffset(
                    _languageService,
                    request.TextDocument.Uri,
                    request.Position,
                    out var offset))
            {
                return null;
            }

            var hover = await _languageService.GetHoverAsync(
                DocumentConversions.GetDocumentId(request.TextDocument.Uri),
                offset,
                cancellationToken).ConfigureAwait(false);

            return hover is null
                ? null
                : new Hover
                {
                    Contents = new MarkupContent
                    {
                        Kind = MarkupKind.PlainText,
                        Value = hover.Contents
                    }
                };
        }, cancellationToken);

    [JsonRpcMethod("textDocument/signatureHelp", UseSingleObjectParameterDeserialization = true)]
    public Task<SignatureHelp?> SignatureHelpAsync(
        SignatureHelpParams request,
        CancellationToken cancellationToken) =>
        _dispatcher.RunAsync(async () =>
        {
            if (!DocumentConversions.TryGetOffset(
                    _languageService,
                    request.TextDocument.Uri,
                    request.Position,
                    out var caretOffset) ||
                !_languageService.Documents.TryGet(
                    DocumentConversions.GetDocumentId(request.TextDocument.Uri),
                    out var document) ||
                document is null ||
                caretOffset == 0)
            {
                return null;
            }

            var trigger = document.Text[caretOffset - 1];
            if (!SignatureTriggers.Contains(trigger))
                return null;

            var result = await _languageService.GetSignatureHelpAsync(
                DocumentConversions.GetDocumentId(request.TextDocument.Uri),
                caretOffset,
                trigger,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            return result is null
                ? null
                : new SignatureHelp
                {
                    ActiveSignature = result.ActiveSignature,
                    ActiveParameter = Math.Max(0, result.ActiveParameter - 1),
                    Signatures = result.Signatures.Select(CreateSignatureInformation).ToArray()
                };
        }, cancellationToken);

    private bool TryGetTriggerOffset(Uri uri, Position position, char trigger, out int caretOffset)
    {
        caretOffset = 0;
        return DocumentConversions.TryGetOffset(_languageService, uri, position, out caretOffset) &&
               _languageService.Documents.TryGet(
                   DocumentConversions.GetDocumentId(uri),
                   out var document) &&
               document is not null &&
               caretOffset > 0 &&
               document.Text[caretOffset - 1] == trigger;
    }

    private static CompletionList EmptyCompletionList() => new()
    {
        IsIncomplete = false,
        Items = Array.Empty<LspCompletionItem>()
    };

    private static CompletionItemKind MapCompletionKind(string kind) => kind switch
    {
        "Class" => CompletionItemKind.Class,
        "Interface" => CompletionItemKind.Interface,
        "Namespace" => CompletionItemKind.Module,
        "Method" or "Function" or "Procedure" => CompletionItemKind.Method,
        "Property" => CompletionItemKind.Property,
        "Field" => CompletionItemKind.Field,
        "Variable" or "Parameter" => CompletionItemKind.Variable,
        "Constant" => CompletionItemKind.Constant,
        "Enum" => CompletionItemKind.Enum,
        "Keyword" => CompletionItemKind.Keyword,
        _ => CompletionItemKind.Text
    };

    private static SignatureInformation CreateSignatureInformation(SignatureInfo signature)
    {
        return new SignatureInformation
        {
            Label = signature.Label,
            Documentation = signature.Documentation,
            Parameters = signature.Parameters
                .Select(parameter => new ParameterInformation { Label = parameter })
                .ToArray()
        };
    }
}
