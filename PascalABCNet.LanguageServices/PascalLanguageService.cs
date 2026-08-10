using CodeCompletion;
using Languages.Facade;
using Languages.Pascal;

namespace PascalABCNet.LanguageServices;

public sealed class PascalLanguageService : IPascalLanguageService
{
    private static readonly SemaphoreSlim SemanticGate = new(1, 1);

    private readonly string _documentationLanguageIso;
    private readonly ILanguage _language;
    private readonly CodeCompletionController _controller = new();
    private readonly Dictionary<string, DocumentState> _states = new(StringComparer.Ordinal);

    public PascalLanguageService(string documentationLanguageIso = "en")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentationLanguageIso);
        _documentationLanguageIso = documentationLanguageIso;

        SemanticGate.Wait();
        try
        {
            _language = PascalLanguageRegistration.RegisterPascalLanguage();
            CodeCompletionBootstrap.Initialize(_documentationLanguageIso);
        }
        finally
        {
            SemanticGate.Release();
        }
    }

    public DocumentStorage Documents { get; } = new();

    public Task<DocumentAnalysis> OpenOrUpdateDocumentAsync(
        string documentId,
        string fileName,
        string text,
        int? version = null,
        CancellationToken cancellationToken = default)
    {
        ValidateDocument(documentId, fileName, text);

        return ExecuteSerializedAsync(() =>
        {
            var document = Documents.Set(documentId, fileName, text, version);
            var domConverter = _controller.Compile(fileName, text);
            _states[documentId] = new DocumentState(document, domConverter);

            return new DocumentAnalysis(
                documentId,
                version,
                domConverter.is_compiled,
                domConverter.LastConversionError?.ToString());
        }, cancellationToken);
    }

    public Task<bool> CloseDocumentAsync(
        string documentId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);

        return ExecuteSerializedAsync(() =>
        {
            _states.Remove(documentId);
            return Documents.Remove(documentId);
        }, cancellationToken);
    }

    public Task<IReadOnlyList<CompletionItem>> GetCompletionAfterDotAsync(
        string documentId,
        int caretOffset,
        CancellationToken cancellationToken = default)
    {
        return ExecuteSerializedAsync<IReadOnlyList<CompletionItem>>(() =>
        {
            var state = GetCompiledState(documentId);
            if (state is null)
                return Array.Empty<CompletionItem>();

            var text = state.Document.Text;
            if (caretOffset <= 0 || caretOffset > text.Length || text[caretOffset - 1] != '.')
                throw new ArgumentException("The caret must be immediately after '.'.", nameof(caretOffset));

            var position = TextCoordinates.GetPosition(text, caretOffset);
            var textBeforeCaret = text[..caretOffset];
            var context = new CompletionTriggerContext { DotPressed = true };
            var expressionInfo = CompletionExpressionService.AnalyzeAtCaret(
                caretOffset,
                textBeforeCaret,
                position.Line,
                position.Character,
                _language.LanguageIntellisenseSupport,
                context,
                PascalABCCompiler.Parsers.KeywordKind.None);
            var parseResult = CompletionExpressionService.Parse(
                _language.Parser,
                state.Document.FileName,
                expressionInfo.ExpressionText,
                context);

            if (parseResult.ShouldAbortCompletion || parseResult.Expression is null)
                return Array.Empty<CompletionItem>();

            var result = CompletionSymbolService.GetSymbols(
                state.DomConverter,
                position.Line,
                position.Character,
                context,
                _language.CaseSensitive,
                expressionInfo.ExpressionText,
                expressionInfo.CtrlOrShiftSpaceAfterDot,
                expressionInfo.InsidePatternWithDots,
                expressionInfo.Pattern,
                parseResult.Expression,
                expressionInfo.Keyword,
                smartIntellisense: true,
                namespaceVisibleRange: 0);

            if (result.ShouldAbortCompletion || result.Symbols is null)
                return Array.Empty<CompletionItem>();

            _language.LanguageIntellisenseSupport.RenameOrExcludeSpecialNames(result.Symbols);

            return result.Symbols
                .Where(symbol => symbol is not null && !symbol.not_include)
                .Select(symbol => new CompletionItem(
                    string.IsNullOrEmpty(symbol.aliasName) ? symbol.name : symbol.aliasName,
                    symbol.description,
                    symbol.kind.ToString()))
                .DistinctBy(item => (item.Label.ToUpperInvariant(), item.Kind))
                .ToArray();
        }, cancellationToken);
    }

    public Task<HoverInfo?> GetHoverAsync(
        string documentId,
        int offset,
        CancellationToken cancellationToken = default)
    {
        return ExecuteSerializedAsync(() =>
        {
            var state = GetCompiledState(documentId);
            if (state is null)
                return null;

            var position = TextCoordinates.GetPosition(state.Document.Text, offset);
            var description = HoverService.GetDescription(
                _language,
                state.DomConverter,
                state.Document.FileName,
                state.Document.Text,
                offset,
                position.Line,
                position.Character);

            return string.IsNullOrWhiteSpace(description) ? null : new HoverInfo(description);
        }, cancellationToken);
    }

    public Task<SignatureHelpInfo?> GetSignatureHelpAsync(
        string documentId,
        int caretOffset,
        char triggerCharacter,
        int currentParameter = 1,
        int currentParameterForSelection = 1,
        CancellationToken cancellationToken = default)
    {
        return ExecuteSerializedAsync(() =>
        {
            var state = GetCompiledState(documentId);
            if (state is null)
                return null;

            var text = state.Document.Text;
            var position = TextCoordinates.GetPosition(text, caretOffset);
            var result = SignatureHelpService.GetSignatureHelpAtCaret(
                _language,
                state.DomConverter,
                state.Document.FileName,
                text[..caretOffset],
                caretOffset,
                position.Line,
                position.Character,
                triggerCharacter,
                currentParameter,
                currentParameterForSelection);

            if (result?.Signatures is null || result.Signatures.Length == 0)
                return null;

            return new SignatureHelpInfo(
                result.Signatures,
                result.DefaultIndex,
                result.CurrentParameter,
                result.ParameterCount);
        }, cancellationToken);
    }

    private async Task<T> ExecuteSerializedAsync<T>(Func<T> action, CancellationToken cancellationToken)
    {
        await SemanticGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CodeCompletionBootstrap.Initialize(_documentationLanguageIso);
            return action();
        }
        finally
        {
            SemanticGate.Release();
        }
    }

    private DocumentState? GetCompiledState(string documentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);

        return _states.TryGetValue(documentId, out var state) && state.DomConverter.is_compiled
            ? state
            : null;
    }

    private static void ValidateDocument(string documentId, string fileName, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(text);

        if (!string.Equals(Path.GetExtension(fileName), ".pas", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Only PascalABC.NET .pas documents are supported.", nameof(fileName));
    }

    private sealed record DocumentState(DocumentSnapshot Document, DomConverter DomConverter);
}
