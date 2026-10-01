using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Languages.Integration;
using NetMQ;
using NetMQ.Sockets;
using PascalABCCompiler;
using PascalABCCompiler.Errors;
#if NETFRAMEWORK
using System.Web.Script.Serialization;
#else
using System.Text.Json;
#endif

namespace PascalABCNet.CompilerWorker;

internal static class Program
{
    private sealed class CompileRequest
    {
        public string? fileName { get; set; }
        public string? outputDirectory { get; set; }
        public string? runtimeModule { get; set; }
        public SourceFileRequest[]? sourceFiles { get; set; }
    }

    private sealed class SourceFileRequest
    {
        public string? fileName { get; set; }
        public string? text { get; set; }
    }

    private sealed class SourceFileSnapshot
    {
        private readonly Dictionary<string, string> files;

        public SourceFileSnapshot(SourceFileRequest[] sourceFiles)
        {
            files = new Dictionary<string, string>(PathComparer);
            foreach (var sourceFile in sourceFiles)
            {
                if (string.IsNullOrWhiteSpace(sourceFile.fileName))
                    throw new InvalidDataException(
                        "В sourceFiles не задано поле fileName");
                if (!Path.IsPathRooted(sourceFile.fileName))
                    throw new InvalidDataException(
                        "sourceFiles.fileName должен быть абсолютным путём: " +
                        sourceFile.fileName);
                files[NormalizePath(sourceFile.fileName!)] = sourceFile.text ?? "";
            }
        }

        public IEnumerable<string> FileNames => files.Keys;

        public bool Contains(string fileName) =>
            files.ContainsKey(NormalizePath(fileName));

        public object? Provide(string fileName, PascalABCCompiler.CoreUtils.SourceFileOperation operation)
        {
            var normalized = NormalizePath(fileName);
            if (files.TryGetValue(normalized, out var text))
            {
                switch (operation)
                {
                    case PascalABCCompiler.CoreUtils.SourceFileOperation.GetText:
                        return text;
                    case PascalABCCompiler.CoreUtils.SourceFileOperation.Exists:
                        return true;
                    case PascalABCCompiler.CoreUtils.SourceFileOperation.GetLastWriteTime:
                        return DateTime.MaxValue;
                    case PascalABCCompiler.CoreUtils.SourceFileOperation.FileEncoding:
                        return new UTF8Encoding(false);
                }
            }

            return PascalABCCompiler.CoreUtils.SourceFilesProviders
                .DefaultSourceFilesProvider(normalized, operation);
        }

        private static StringComparer PathComparer =>
            Path.DirectorySeparatorChar == '\\'
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;

        private static string NormalizePath(string fileName) =>
            Path.GetFullPath(fileName);
    }

    private static CompileRequest DeserializeCompileRequest(string json)
    {
#if NETFRAMEWORK
        return new JavaScriptSerializer().Deserialize<CompileRequest>(json)
               ?? throw new InvalidDataException("JSON-запрос не содержит объекта");
#else
        return JsonSerializer.Deserialize<CompileRequest>(json)
               ?? throw new InvalidDataException("JSON-запрос не содержит объекта");
#endif
    }

    private static string CompileFile(Compiler compiler, string requestJson)
    {
        var response = new StringBuilder();

        try
        {
            var request = DeserializeCompileRequest(requestJson);
            var fileName = request.fileName ?? "";
            var fullFileName = Path.GetFullPath(fileName);
            var snapshot = request.sourceFiles != null
                ? new SourceFileSnapshot(request.sourceFiles)
                : null;
            if (!File.Exists(fullFileName) &&
                (snapshot == null || !snapshot.Contains(fullFileName)))
            {
                response.AppendLine("ERROR");
                response.AppendLine("Файл не найден: " + fullFileName);
                return response.ToString();
            }

            var options = new CompilerOptions(
                fullFileName,
                CompilerOptions.OutputType.ConsoleApplicaton)
            {
                UseDllForSystemUnits = true,
                Debug = false,
                ForDebugging = false
            };

            if (snapshot != null)
            {
                options.SavePCU = false;
            }

            if (!string.IsNullOrWhiteSpace(request.outputDirectory))
            {
                var outputDirectory = Path.GetFullPath(request.outputDirectory);
                Directory.CreateDirectory(outputDirectory);
                options.OutputDirectory = outputDirectory;
            }

            var runtimeModule = request.runtimeModule;
            if (!string.IsNullOrWhiteSpace(runtimeModule))
            {
                foreach (var languageModules in options.StandardModules)
                {
                    languageModules.Value.Add(new CompilerOptions.StandardModule(
                        runtimeModule!.Trim(),
                        CompilerOptions.StandardModuleAddMethod.RightToMain,
                        languageModules.Key));
                }
            }

            var requestCompiler = compiler;
            if (snapshot != null)
            {
                requestCompiler = new Compiler();
                requestCompiler.SourceFilesProvider = snapshot.Provide;
                requestCompiler.OnChangeCompilerState += (_, state, currentFileName) =>
                {
                    if (state == CompilerState.BeginCompileFile)
                        RegisterSnapshotPaths(
                            requestCompiler, snapshot, currentFileName);
                };
            }

            requestCompiler.Reload();
            var outputFileName = requestCompiler.Compile(options);

            if (outputFileName != null)
            {
                response.AppendLine("OK");
                response.AppendLine(outputFileName);
            }
            else
            {
                response.AppendLine("ERROR");
                if (requestCompiler.ErrorsList.Count == 0)
                {
                    response.AppendLine("Компилятор не создал выходной файл");
                }
                else
                {
                    foreach (var error in requestCompiler.ErrorsList)
                        AppendDiagnostic(response, error, fullFileName);
                }
            }
        }
        catch (Exception exception)
        {
            response.Clear();
            response.AppendLine("FATAL");
            response.AppendLine(exception.ToString());
        }

        return response.ToString();
    }

    private static void RegisterSnapshotPaths(
        Compiler compiler,
        SourceFileSnapshot snapshot,
        string? currentFileName)
    {
        var currentDirectory = string.IsNullOrWhiteSpace(currentFileName)
            ? compiler.CompilerOptions.SourceFileDirectory
            : Path.GetDirectoryName(Path.GetFullPath(currentFileName));
        var normalizedCurrentDirectory = currentDirectory?.ToLowerInvariant();

        foreach (var snapshotFileName in snapshot.FileNames)
        {
            var names = new List<string>
            {
                Path.GetFileNameWithoutExtension(snapshotFileName),
                Path.GetFileName(snapshotFileName),
                Path.ChangeExtension(snapshotFileName, null),
                snapshotFileName
            };

            if (currentDirectory != null)
            {
                var relativeName = MakeRelativePath(currentDirectory, snapshotFileName);
                names.Add(relativeName);
                names.Add(Path.ChangeExtension(relativeName, null));
            }

            foreach (var name in names)
            {
                var key = Tuple.Create(
                    name.ToLowerInvariant(), normalizedCurrentDirectory);
                if (!compiler.SourceFileNamesDictionary.ContainsKey(key))
                {
                    compiler.SourceFileNamesDictionary[key] = Tuple.Create(
                        snapshotFileName, 0);
                }
            }
        }
    }

    private static string MakeRelativePath(string directory, string fileName)
    {
        var directoryUri = new Uri(AppendDirectorySeparator(directory));
        var fileUri = new Uri(fileName);
        if (!string.Equals(
                directoryUri.Scheme, fileUri.Scheme,
                StringComparison.OrdinalIgnoreCase))
            return fileName;
        return Uri.UnescapeDataString(
                directoryUri.MakeRelativeUri(fileUri).ToString())
            .Replace('/', Path.DirectorySeparatorChar);
    }

    private static string AppendDirectorySeparator(string directory) =>
        directory.EndsWith(Path.DirectorySeparatorChar.ToString(),
            StringComparison.Ordinal)
            ? directory
            : directory + Path.DirectorySeparatorChar;

    private static void AppendDiagnostic(
        StringBuilder response,
        object error,
        string defaultFileName)
    {
        var locatedError = error as LocatedError;
        var location = locatedError?.SourceLocation;
        var fileName = location?.FileName ?? locatedError?.FileName ?? defaultFileName;
        var line = location?.BeginPosition.Line ?? 1;
        var column = location?.BeginPosition.Column ?? 1;
        var message = EnhanceErrorMessage(error);

        response.Append("DIAGNOSTIC\t");
        response.Append(EncodeBase64(fileName));
        response.Append('\t');
        response.Append(line);
        response.Append('\t');
        response.Append(column);
        response.Append('\t');
        response.AppendLine(EncodeBase64(message));
    }

    private static string EncodeBase64(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    private static string EnhanceErrorMessage(object error)
    {
        var message = error.ToString() ?? "";
        var openParenthesis = message.IndexOf('(');
        var closeParenthesis = message.IndexOf(')');
        var position = "";

        if (openParenthesis >= 0 && closeParenthesis >= openParenthesis)
        {
            position = message.Substring(
                openParenthesis,
                closeParenthesis - openParenthesis + 1);
        }

        var messageStart = closeParenthesis;
        if (messageStart >= 0 && messageStart < message.Length)
            messageStart = message.IndexOf(':', messageStart);
        if (messageStart >= 0 && messageStart < message.Length - 1)
            messageStart = message.IndexOf(':', messageStart + 1);

        var result = messageStart >= 0 && messageStart < message.Length - 1
            ? message.Substring(messageStart + 1).Trim()
            : message.Trim();

        if (position.Length > 0)
            result = position + ": " + result;

        if (error is SemanticError semanticError && semanticError.Location != null)
        {
            position = "(" + semanticError.Location.begin_line_num + "," +
                       semanticError.Location.begin_column_num + ")";
            result = position + ": " + result;
        }

        return result;
    }

    private static int Main(string[] args)
    {
        Console.InputEncoding = new UTF8Encoding(false);
        Console.OutputEncoding = new UTF8Encoding(false);

        if (args.Length == 0)
        {
            Console.Error.WriteLine(
                "Ошибка: требуется аргумент с номером TCP-порта");
            return 1;
        }

        if (!int.TryParse(args[0], out var port) || port is < 1 or > 65535)
        {
            Console.Error.WriteLine(
                "Ошибка: некорректный номер TCP-порта: " + args[0]);
            return 1;
        }

        var address = "tcp://127.0.0.1:" + port;

        try
        {
#if NET10_0
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
#endif
            StringResourcesLanguage.LoadDefaultConfig();
            LanguageIntegrator.LoadAllLanguages();

            var compiler = new Compiler();
            using var server = new ResponseSocket();
            server.Bind(address);

            Console.WriteLine("PascalABC.NET compiler server started");
            Console.WriteLine("Address: " + address);

            var running = true;
            while (running)
            {
                var request = server.ReceiveFrameString();
                switch (request)
                {
                    case "#PING":
                        server.SendFrame("PONG");
                        break;

                    case "#SHUTDOWN":
                        server.SendFrame("BYE");
                        running = false;
                        break;

                    default:
                        server.SendFrame(CompileFile(compiler, request));
                        break;
                }
            }

            Console.WriteLine("PascalABC.NET compiler server stopped");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
        finally
        {
            NetMQConfig.Cleanup();
        }
    }
}
