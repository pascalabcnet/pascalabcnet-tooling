using CodeCompletion;
using Languages.Facade;
using Languages.Pascal;

namespace PascalABCNet.LanguageServices;

public sealed class PascalLanguageService : IPascalLanguageService
{
    private const string LibrarySourceDirectoryKey = "%LIBSOURCEDIRECTORY%";
    private static readonly TimeSpan AnalysisDebounce = TimeSpan.FromMilliseconds(200);
    private static readonly SemaphoreSlim SemanticGate = new(1, 1);

    private readonly string _documentationLanguageIso;
    private readonly ILanguage _language;
    private readonly CodeCompletionController _controller = new();
    private readonly Dictionary<string, DocumentState> _states = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CancellationTokenSource> _pendingAnalyses = new(StringComparer.Ordinal);
    private readonly object _pendingAnalysesSync = new();

    public PascalLanguageService(string documentationLanguageIso = "en")
        : this(documentationLanguageIso, FindStandardLibraryDirectory())
    {
    }

    public PascalLanguageService(
        string documentationLanguageIso,
        string standardLibraryDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentationLanguageIso);
        ArgumentException.ThrowIfNullOrWhiteSpace(standardLibraryDirectory);
        _documentationLanguageIso = documentationLanguageIso;

        var fullStandardLibraryDirectory = Path.GetFullPath(standardLibraryDirectory);
        if (!File.Exists(Path.Combine(fullStandardLibraryDirectory, "PABCSystem.pas")))
        {
            throw new DirectoryNotFoundException(
                $"PascalABC.NET standard library was not found in '{fullStandardLibraryDirectory}'.");
        }

        SemanticGate.Wait();
        try
        {
            _language = PascalLanguageRegistration.RegisterPascalLanguage();
            CodeCompletionBootstrap.Initialize(_documentationLanguageIso);
            CodeCompletionController.StandartDirectories[LibrarySourceDirectoryKey] =
                fullStandardLibraryDirectory;
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
            var domConverter = CompileAndStore(document);

            if (domConverter.is_compiled)
                RecompileOpenDependents(document);

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
            CancelPendingAnalysis(documentId);
            Documents.TryGet(documentId, out var document);
            _states.Remove(documentId);
            var removed = Documents.Remove(documentId);

            if (document is not null)
                RestoreClosedDocumentFromDisk(document);

            return removed;
        }, cancellationToken);
    }

    public Task QueueDocumentUpdateAsync(
        string documentId,
        string fileName,
        string text,
        int? version = null,
        CancellationToken cancellationToken = default)
    {
        ValidateDocument(documentId, fileName, text);

        return ExecuteSerializedAsync(() =>
        {
            if (Documents.TryGet(documentId, out var currentDocument) &&
                currentDocument?.Version is int currentVersion &&
                version is int incomingVersion &&
                incomingVersion <= currentVersion)
            {
                return true;
            }

            var document = Documents.Set(documentId, fileName, text, version);

            if (_states.TryGetValue(documentId, out var state))
            {
                _states[documentId] = state with
                {
                    Document = document,
                    IsStale = true,
                    IsDirty = true
                };
                ScheduleAnalysis(document);
            }
            else
            {
                var domConverter = CompileAndStore(document);
                if (domConverter.is_compiled)
                    RecompileOpenDependents(document);
            }

            return true;
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

        EnsureDirtyDocumentsFresh();

        return _states.TryGetValue(documentId, out var state) && state.DomConverter.is_compiled
            ? state
            : null;
    }

    private DomConverter CompileAndStore(DocumentSnapshot document)
    {
        var domConverter = _controller.Compile(document.FileName, document.Text);

        if (domConverter.is_compiled)
        {
            var generation = _states.TryGetValue(document.DocumentId, out var previousState)
                ? previousState.Generation + 1
                : 1;
            _states[document.DocumentId] = new DocumentState(
                document,
                domConverter,
                GetDirectDependencies(domConverter),
                generation,
                IsStale: false,
                IsDirty: false,
                LastConversionError: null);
        }
        else if (_states.TryGetValue(document.DocumentId, out var lastSuccessfulState) &&
                 lastSuccessfulState.DomConverter.is_compiled)
        {
            _states[document.DocumentId] = lastSuccessfulState with
            {
                Document = document,
                IsStale = true,
                IsDirty = false,
                LastConversionError = domConverter.LastConversionError?.ToString()
            };
        }
        else
        {
            _states[document.DocumentId] = new DocumentState(
                document,
                domConverter,
                GetDirectDependencies(domConverter),
                Generation: 0,
                IsStale: true,
                IsDirty: false,
                LastConversionError: domConverter.LastConversionError?.ToString());
        }

        return domConverter;
    }

    private void RecompileOpenDependents(DocumentSnapshot changedDocument)
    {
        var changedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            NormalizeFileName(changedDocument.FileName)
        };
        var rebuiltDocuments = new HashSet<string>(StringComparer.Ordinal)
        {
            changedDocument.DocumentId
        };

        while (true)
        {
            var dependents = _states.Values
                .Where(state =>
                    !rebuiltDocuments.Contains(state.Document.DocumentId) &&
                    state.Dependencies.Any(changedFiles.Contains))
                .ToArray();

            if (dependents.Length == 0)
                return;

            foreach (var dependent in dependents)
            {
                rebuiltDocuments.Add(dependent.Document.DocumentId);
                var domConverter = CompileAndStore(dependent.Document);

                if (domConverter.is_compiled)
                    changedFiles.Add(NormalizeFileName(dependent.Document.FileName));
            }
        }
    }

    private void RestoreClosedDocumentFromDisk(DocumentSnapshot closedDocument)
    {
        RemoveCachedModule(closedDocument.FileName);

        if (TryReadFile(closedDocument.FileName, out var diskText))
        {
            var diskConverter = _controller.Compile(
                closedDocument.FileName,
                diskText);
            if (!diskConverter.is_compiled)
                RemoveCachedModule(closedDocument.FileName);
        }

        RecompileOpenDependents(closedDocument);
    }

    private static void RemoveCachedModule(string fileName)
    {
        CodeCompletionController.comp_modules.Remove(fileName);

        var normalizedFileName = NormalizeFileName(fileName);
        if (!string.Equals(fileName, normalizedFileName, StringComparison.OrdinalIgnoreCase))
            CodeCompletionController.comp_modules.Remove(normalizedFileName);
    }

    private static bool TryReadFile(string fileName, out string text)
    {
        text = string.Empty;
        if (!File.Exists(fileName))
            return false;

        try
        {
            text = File.ReadAllText(fileName);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static IReadOnlySet<string> GetDirectDependencies(DomConverter domConverter)
    {
        if (!domConverter.is_compiled)
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        return new[]
            {
                domConverter.visitor.entry_scope?.used_units,
                domConverter.visitor.impl_scope?.used_units
            }
            .Where(usedUnits => usedUnits is not null)
            .SelectMany(usedUnits => usedUnits!)
            .Select(scope => scope.file_name)
            .Where(fileName => !string.IsNullOrWhiteSpace(fileName))
            .Select(fileName => NormalizeFileName(fileName!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string NormalizeFileName(string fileName) => Path.GetFullPath(fileName);

    private static string FindStandardLibraryDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            foreach (var candidate in new[]
                     {
                         Path.Combine(directory.FullName, "Lib"),
                         Path.Combine(directory.FullName, "pascalabcnet", "bin", "Lib")
                     })
            {
                if (File.Exists(Path.Combine(candidate, "PABCSystem.pas")))
                    return candidate;
            }
        }

        throw new DirectoryNotFoundException(
            "Could not locate the PascalABC.NET standard library containing PABCSystem.pas.");
    }

    private void EnsureDirtyDocumentsFresh()
    {
        while (true)
        {
            var dirtyStates = _states.Values.Where(state => state.IsDirty).ToArray();
            if (dirtyStates.Length == 0)
                return;

            var dirtyFileNames = dirtyStates
                .Select(state => NormalizeFileName(state.Document.FileName))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var state = dirtyStates.FirstOrDefault(candidate =>
                            !candidate.Dependencies.Any(dirtyFileNames.Contains))
                        ?? dirtyStates[0];

            CancelPendingAnalysis(state.Document.DocumentId);
            var domConverter = CompileAndStore(state.Document);
            if (domConverter.is_compiled)
                RecompileOpenDependents(state.Document);
        }
    }

    private void ScheduleAnalysis(DocumentSnapshot document)
    {
        var cancellation = new CancellationTokenSource();

        lock (_pendingAnalysesSync)
        {
            if (_pendingAnalyses.TryGetValue(document.DocumentId, out var previous))
                previous.Cancel();
            _pendingAnalyses[document.DocumentId] = cancellation;
        }

        _ = AnalyzeAfterDebounceAsync(document, cancellation);
    }

    private async Task AnalyzeAfterDebounceAsync(
        DocumentSnapshot scheduledDocument,
        CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(AnalysisDebounce, cancellation.Token).ConfigureAwait(false);
            await ExecuteSerializedAsync(() =>
            {
                if (!Documents.TryGet(scheduledDocument.DocumentId, out var latestDocument) ||
                    latestDocument is null ||
                    latestDocument != scheduledDocument ||
                    !_states.TryGetValue(scheduledDocument.DocumentId, out var state) ||
                    !state.IsDirty)
                {
                    return true;
                }

                var domConverter = CompileAndStore(latestDocument);
                if (domConverter.is_compiled)
                    RecompileOpenDependents(latestDocument);
                return true;
            }, cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await ExecuteSerializedAsync(() =>
            {
                if (_states.TryGetValue(scheduledDocument.DocumentId, out var state) &&
                    state.Document == scheduledDocument)
                {
                    _states[scheduledDocument.DocumentId] = state with
                    {
                        IsStale = true,
                        IsDirty = false,
                        LastConversionError = exception.ToString()
                    };
                }

                return true;
            }, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            lock (_pendingAnalysesSync)
            {
                if (_pendingAnalyses.TryGetValue(scheduledDocument.DocumentId, out var current) &&
                    ReferenceEquals(current, cancellation))
                {
                    _pendingAnalyses.Remove(scheduledDocument.DocumentId);
                }
            }

            cancellation.Dispose();
        }
    }

    private void CancelPendingAnalysis(string documentId)
    {
        lock (_pendingAnalysesSync)
        {
            if (_pendingAnalyses.Remove(documentId, out var cancellation))
                cancellation.Cancel();
        }
    }

    private static void ValidateDocument(string documentId, string fileName, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(text);

        if (!string.Equals(Path.GetExtension(fileName), ".pas", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Only PascalABC.NET .pas documents are supported.", nameof(fileName));
    }

    private sealed record DocumentState(
        DocumentSnapshot Document,
        DomConverter DomConverter,
        IReadOnlySet<string> Dependencies,
        long Generation,
        bool IsStale,
        bool IsDirty,
        string? LastConversionError);
}
