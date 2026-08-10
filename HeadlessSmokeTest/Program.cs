using CodeCompletion;
using Languages.Facade;
using Languages.Pascal;
using PascalABCNet.LanguageServices;

const string fileName = "HeadlessSmoke.pas";
const string source = """
program HeadlessSmoke;

var
  text: string;

begin
  text := 'PascalABC.NET';
  var result := text.Substring(1);
  Writeln(result);
end.
""";

var language = PascalLanguageRegistration.RegisterPascalLanguage();
var secondRegistration = PascalLanguageRegistration.RegisterPascalLanguage();
Check(ReferenceEquals(language, secondRegistration), "Pascal registration is idempotent");
Check(ReferenceEquals(language, LanguageProvider.Instance.MainLanguage), "Pascal is the main language");
Check(
    ReferenceEquals(language, LanguageProvider.Instance.SelectLanguageByExtension(fileName)),
    "A .pas document resolves to the explicitly registered Pascal language");
Console.WriteLine("PASS registration without LoadAllLanguages()");

CodeCompletionBootstrap.Initialize("ru");
Check(
    PascalABCCompiler.StringResourcesLanguage.CurrentTwoLetterISO == "ru",
    "Russian headless bootstrap is active");
CodeCompletionBootstrap.Initialize("en");
Check(
    PascalABCCompiler.StringResourcesLanguage.CurrentTwoLetterISO == "en",
    "English headless bootstrap is active");
Console.WriteLine("PASS bootstrap for ru and en");

Check(source.Length > 0, "Pascal source text is loaded");
Console.WriteLine("PASS Pascal source text loaded");

var controller = new CodeCompletionController();
var domConverter = controller.Compile(fileName, source);
Check(domConverter is not null && domConverter.is_compiled, "DomConverter was built");
Check(domConverter!.LastConversionError is null, "Successful DOM conversion has no error");
Console.WriteLine("PASS DomConverter created");

const string memberAccess = "text.Substring";
var memberStart = source.IndexOf(memberAccess, StringComparison.Ordinal);
Check(memberStart >= 0, "Completion marker exists");
var dotOffset = memberStart + "text.".Length;
var completionPrefix = source[..dotOffset];
var completionPosition = GetPosition(completionPrefix, completionPrefix.Length);
var completionContext = new CompletionTriggerContext { DotPressed = true };
var expressionInfo = CompletionExpressionService.AnalyzeAtCaret(
    dotOffset,
    completionPrefix,
    completionPosition.Line,
    completionPosition.Column,
    language.LanguageIntellisenseSupport,
    completionContext,
    PascalABCCompiler.Parsers.KeywordKind.None);
var parseResult = CompletionExpressionService.Parse(
    language.Parser,
    fileName,
    expressionInfo.ExpressionText,
    completionContext);
Check(!parseResult.ShouldAbortCompletion && parseResult.Expression is not null, "Completion expression parsed");
var completion = CompletionSymbolService.GetSymbols(
    domConverter,
    completionPosition.Line,
    completionPosition.Column,
    completionContext,
    language.CaseSensitive,
    expressionInfo.ExpressionText,
    expressionInfo.CtrlOrShiftSpaceAfterDot,
    expressionInfo.InsidePatternWithDots,
    expressionInfo.Pattern,
    parseResult.Expression,
    expressionInfo.Keyword,
    smartIntellisense: true,
    namespaceVisibleRange: 0);
var completionNames = completion.Symbols is null
    ? "<null>"
    : string.Join(", ", completion.Symbols.Where(symbol => symbol is not null).Select(symbol => symbol.name));
Check(
    completion.Symbols?.Any(symbol => symbol is not null && symbol.name == "Substring") == true,
    $"Completion after text. contains Substring (expression: {expressionInfo.ExpressionText}; symbols: {completionNames})");
Console.WriteLine("PASS completion after dot");

var hoverOffset = memberStart + "text.".Length + 1;
var hoverPosition = GetPosition(source, hoverOffset);
var hover = HoverService.GetDescription(
    language,
    domConverter,
    fileName,
    source,
    hoverOffset,
    hoverPosition.Line,
    hoverPosition.Column);
Check(!string.IsNullOrWhiteSpace(hover), "Hover returned a description");
Check(hover.Contains("Substring", StringComparison.OrdinalIgnoreCase), "Hover describes Substring");
Console.WriteLine("PASS hover over identifier");

var openParenthesisOffset = memberStart + memberAccess.Length + 1;
var signaturePrefix = source[..openParenthesisOffset];
var signaturePosition = GetPosition(signaturePrefix, signaturePrefix.Length);
var signatureHelp = SignatureHelpService.GetSignatureHelpAtCaret(
    language,
    domConverter,
    fileName,
    signaturePrefix,
    openParenthesisOffset,
    signaturePosition.Line,
    signaturePosition.Column,
    '(',
    currentParameter: 1,
    currentParameterForSelection: 1);
Check(signatureHelp?.Signatures?.Length > 0, "Signature help returned at least one signature");
Check(
    signatureHelp!.Signatures.Any(signature => signature.Contains("Substring", StringComparison.OrdinalIgnoreCase)),
    "Signature help describes Substring");
Console.WriteLine("PASS signature help for method call");

const string invalidSource = "program Broken; begin var value := ; end.";
var invalidDomConverter = controller.Compile("Broken.pas", invalidSource);
Check(!invalidDomConverter.is_compiled, "Invalid source must not produce a compiled DOM");
Console.WriteLine(
    invalidDomConverter.LastConversionError is null
        ? "PASS invalid source rejected before DOM conversion"
        : $"PASS invalid source rejected during DOM conversion: {invalidDomConverter.LastConversionError.GetType().Name}");

const string documentId = "file:///HeadlessSmoke.pas";
var languageService = new PascalLanguageService("en");
var analysis = await languageService.OpenOrUpdateDocumentAsync(documentId, fileName, source, version: 1);
Check(analysis.IsCompiled && analysis.LastConversionError is null, "LanguageServices compiled the document");
Check(
    languageService.Documents.TryGet(documentId, out var storedDocument) && storedDocument?.Version == 1,
    "LanguageServices stores the open document snapshot");

var completionTask = languageService.GetCompletionAfterDotAsync(documentId, dotOffset);
var hoverTask = languageService.GetHoverAsync(documentId, hoverOffset);
var signatureTask = languageService.GetSignatureHelpAsync(documentId, openParenthesisOffset, '(');
await Task.WhenAll(completionTask, hoverTask, signatureTask);

Check(
    completionTask.Result.Any(item => item.Label == "Substring"),
    "LanguageServices completion contains Substring");
Check(
    hoverTask.Result?.Contents.Contains("Substring", StringComparison.OrdinalIgnoreCase) == true,
    "LanguageServices hover describes Substring");
Check(
    signatureTask.Result?.Signatures.Any(
        signature => signature.Contains("Substring", StringComparison.OrdinalIgnoreCase)) == true,
    "LanguageServices signature help describes Substring");

var invalidAnalysis = await languageService.OpenOrUpdateDocumentAsync(
    "file:///Broken.pas",
    "Broken.pas",
    invalidSource,
    version: 1);
Check(!invalidAnalysis.IsCompiled, "LanguageServices preserves failed conversion state");
Check(await languageService.CloseDocumentAsync(documentId), "LanguageServices closes an open document");
Console.WriteLine("PASS serialized PascalABCNet.LanguageServices round-trip");

Console.WriteLine("All headless IntelliSense smoke checks passed.");

static (int Line, int Column) GetPosition(string text, int offset)
{
    var line = 0;
    var column = 0;

    for (var index = 0; index < offset; index++)
    {
        if (text[index] == '\n')
        {
            line++;
            column = 0;
        }
        else
        {
            column++;
        }
    }

    return (line, column);
}

static void Check(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException($"Smoke check failed: {message}");
}
