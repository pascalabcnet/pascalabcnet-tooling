using Microsoft.VisualStudio.LanguageServer.Protocol;
using PascalABCNet.LanguageServices;

namespace PascalABCNet.LanguageServer;

internal static class DocumentConversions
{
    public static string GetDocumentId(Uri uri) => uri.AbsoluteUri;

    public static string GetFileName(Uri uri)
    {
        var path = uri.IsFile ? uri.LocalPath : string.Empty;
        return string.Equals(Path.GetExtension(path), ".pas", StringComparison.OrdinalIgnoreCase)
            ? path
            : "Untitled.pas";
    }

    public static bool TryGetOffset(
        IPascalLanguageService languageService,
        Uri uri,
        Position position,
        out int offset)
    {
        offset = 0;
        if (!languageService.Documents.TryGet(GetDocumentId(uri), out var document) || document is null)
            return false;

        try
        {
            offset = TextCoordinates.GetOffset(
                document.Text,
                new TextPosition((int)position.Line, (int)position.Character));
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }
}
