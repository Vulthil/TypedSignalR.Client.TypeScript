using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Cocona;
using Microsoft.Build.Framework;
using Microsoft.Build.Logging;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Extensions.Logging;
using Tapper;
using TypedSignalR.Client.TypeScript.TypeMappers;

namespace TypedSignalR.Client.TypeScript;

public class App : CoconaConsoleAppBase
{
    private readonly ILogger<App> _logger;

    public App(ILogger<App> logger)
    {
        _logger = logger;
    }

    public async Task Transpile(
        [Option('p', Description = "Path to the project file (Xxx.csproj)")]
        string project,
        [Option('o', Description ="Output directory")]
        string output,
        [Option("eol", Description ="End of line")]
        NewLineOption newLine = NewLineOption.Lf,
        [Option("asm", Description ="Flag whether to extend the transpile target to the referenced assembly.")]
        bool assemblies = false,
        [Option('s', Description ="The output type will be suitable for the selected serializer.")]
        SerializerOption serializer = SerializerOption.Json,
        [Option('n', Description ="Naming style. If none is selected, the C# name is used as it is.")]
        NamingStyle namingStyle = NamingStyle.CamelCase,
        [Option(Description ="Enum representation style")]
        EnumStyle @enum = EnumStyle.Value,
        [Option('m', Description = "Method naming style. If none is selected, the C# name is used as it is.")]
        MethodStyle method = MethodStyle.CamelCase,
        [Option("attr", Description ="The flag whether attributes such as JsonPropertyName should affect transpilation.")]
        bool attribute = true)
    {
        _logger.Log(LogLevel.Information, "Start loading the csproj of {path}.", Path.GetFullPath(project));

        output = Path.GetFullPath(output);

        try
        {
            var compilation = await this.CreateCompilationAsync(project);

            await TranspileCore(compilation, output, newLine, 4, assemblies, serializer, namingStyle, @enum, method, attribute);

            _logger.Log(LogLevel.Information, "======== Transpilation is completed. ========");
            _logger.Log(LogLevel.Information, "Please check the output folder: {output}", output);
        }
        catch (Exception ex)
        {
            _logger.Log(LogLevel.Information, "======== Exception ========");
            _logger.Log(LogLevel.Error, "{ex}", ex);
        }
    }

    private async Task<Compilation> CreateCompilationAsync(string projectPath)
    {
        var logger = new ConsoleLogger(LoggerVerbosity.Quiet);
        using var workspace = MSBuildWorkspace.Create();

        var msBuildProject = await workspace.OpenProjectAsync(projectPath, logger, null, this.Context.CancellationToken);

        _logger.Log(LogLevel.Information, "Create Compilation...");
        var compilation = await msBuildProject.GetCompilationAsync(this.Context.CancellationToken);

        if (compilation is null)
        {
            throw new InvalidOperationException("Failed to create compilation.");
        }

        return compilation;
    }

    private async Task TranspileCore(
        Compilation compilation,
        string outputDir,
        NewLineOption newLine,
        int indent,
        bool referencedAssembliesTranspilation,
        SerializerOption serializerOption,
        NamingStyle namingStyle,
        EnumStyle enumStyle,
        MethodStyle methodStyle,
        bool enableAttributeReference)
    {
        var typeMapperProvider = new DefaultTypeMapperProvider(compilation, referencedAssembliesTranspilation);

        typeMapperProvider.AddTypeMapper(new TaskTypeMapper(compilation));
        typeMapperProvider.AddTypeMapper(new GenericTaskTypeMapper(compilation));

        // By default, netstandard2.0 does not include IAsyncEnumerable<T> and ChannelReader<T>
        var asyncEnumerableTypeMapper = new AsyncEnumerableTypeMapper(compilation);

        if (asyncEnumerableTypeMapper.IsSupported())
        {
            typeMapperProvider.AddTypeMapper(asyncEnumerableTypeMapper);
        }

        var channelReaderTypeMapper = new ChannelReaderTypeMapper(compilation);

        if (channelReaderTypeMapper.IsSupported())
        {
            typeMapperProvider.AddTypeMapper(channelReaderTypeMapper);
        }

        var options = new TypedSignalRTranspilationOptions(
            compilation,
            typeMapperProvider,
            serializerOption,
            namingStyle,
            enumStyle,
            methodStyle,
            newLine,
            indent,
            referencedAssembliesTranspilation,
            enableAttributeReference
        );

        // Tapper
        var transpiler = new Transpiler(compilation, options, _logger);

        var generatedSourceCodes = transpiler.Transpile();

        // TypedSignalR.Client.TypeScript
        var signalrCodeGenerator = new TypedSignalRCodeGenerator(compilation, options, _logger);

        var generatedSignalRSourceCodes = signalrCodeGenerator.Generate();

        await OutputToFiles(outputDir, generatedSourceCodes.Concat(generatedSignalRSourceCodes), newLine);
    }

    private async Task OutputToFiles(string outputDir, IEnumerable<GeneratedSourceCode> generatedSourceCodes, NewLineOption newLine)
    {
        if (Directory.Exists(outputDir))
        {
            var tsFiles = Directory.GetFiles(outputDir, "*.ts");

            _logger.Log(LogLevel.Information, "Cleanup old files...");

            foreach (var tsFile in tsFiles)
            {
                File.Delete(tsFile);
            }

            var signalrDir = Path.Join(outputDir, "TypedSignalR.Client");

            if (Directory.Exists(signalrDir))
            {
                var tsSignalRFiles = Directory.GetFiles(signalrDir, "*.ts");

                foreach (var tsFile in tsSignalRFiles)
                {
                    File.Delete(tsFile);
                }
            }
            else
            {
                Directory.CreateDirectory(Path.Join(outputDir, "TypedSignalR.Client"));
            }
        }
        else
        {
            Directory.CreateDirectory(outputDir);
            Directory.CreateDirectory(Path.Join(outputDir, "TypedSignalR.Client"));
        }

        var newLineString = newLine.ToNewLineString();

        foreach (var generatedSourceCode in generatedSourceCodes)
        {
            await using var fs = File.Create(Path.Join(outputDir, generatedSourceCode.SourceName));
            await fs.WriteAsync(Encoding.UTF8.GetBytes(generatedSourceCode.Content.NormalizeNewLines(newLineString)));
        }
    }
}
