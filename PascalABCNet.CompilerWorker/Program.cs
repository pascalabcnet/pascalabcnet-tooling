using System;
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
            if (!File.Exists(fullFileName))
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

            if (!string.IsNullOrWhiteSpace(request.outputDirectory))
            {
                var outputDirectory = Path.GetFullPath(request.outputDirectory);
                Directory.CreateDirectory(outputDirectory);
                options.OutputDirectory = outputDirectory;
            }

            compiler.Reload();
            var outputFileName = compiler.Compile(options);

            if (outputFileName != null)
            {
                response.AppendLine("OK");
                response.AppendLine(outputFileName);
            }
            else
            {
                response.AppendLine("ERROR");
                if (compiler.ErrorsList.Count == 0)
                {
                    response.AppendLine("Компилятор не создал выходной файл");
                }
                else
                {
                    foreach (var error in compiler.ErrorsList)
                        response.AppendLine(EnhanceErrorMessage(error));
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
