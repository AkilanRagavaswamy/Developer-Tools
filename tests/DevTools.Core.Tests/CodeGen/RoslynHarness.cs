using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace DevTools.Core.Tests.CodeGen;

/// <summary>
/// Compiles generated C# in-process so the tests can assert that the generator produces code
/// that <em>builds</em>, rather than code that merely looks right.
/// </summary>
internal static class RoslynHarness
{
    private static readonly Lazy<IReadOnlyList<MetadataReference>> References = new(() =>
    {
        var trusted = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty;

        return
        [
            .. trusted
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Where(static p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                .Select(static p => (MetadataReference)MetadataReference.CreateFromFile(p)),
        ];
    });

    public sealed record CompileOutcome(Assembly? Assembly, IReadOnlyList<string> Errors)
    {
        public bool Succeeded => Assembly is not null;

        public string ErrorText => string.Join(Environment.NewLine, Errors);
    }

    public static CompileOutcome Compile(string source, string assemblyName = "Generated")
    {
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest));

        var compilation = CSharpCompilation.Create(
            $"{assemblyName}_{Guid.NewGuid():N}",
            [tree],
            References.Value,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable,
                optimizationLevel: OptimizationLevel.Debug));

        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);

        if (!result.Success)
        {
            var errors = result.Diagnostics
                .Where(static d => d.Severity == DiagnosticSeverity.Error)
                .Select(static d => $"{d.Id}: {d.GetMessage()} ({d.Location.GetLineSpan().StartLinePosition})")
                .ToList();

            return new CompileOutcome(null, errors);
        }

        stream.Position = 0;
        return new CompileOutcome(Assembly.Load(stream.ToArray()), []);
    }

    /// <summary>Renders source with line numbers, so an assertion failure is readable.</summary>
    public static string Numbered(string source)
    {
        var builder = new StringBuilder();
        var lines = source.Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            builder.Append((i + 1).ToString().PadLeft(3)).Append("  ").AppendLine(lines[i].TrimEnd('\r'));
        }

        return builder.ToString();
    }
}
