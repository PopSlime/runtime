// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Tracing;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

[assembly: System.Resources.NeutralResourcesLanguage("en-us")]

namespace System.Text.RegularExpressions.Generator
{
    /// <summary>Generates C# source code to implement regular expressions.</summary>
    [Generator(LanguageNames.CSharp)]
    public partial class RegexGenerator : IIncrementalGenerator
    {
        /// <summary>Name of the type emitted to contain helpers used by the generated code.</summary>
        private const string HelpersTypeName = "Utilities";
        /// <summary>Namespace containing all the generated code.</summary>
        private const string GeneratedNamespace = "System.Text.RegularExpressions.Generated";
        /// <summary>Code for a [GeneratedCode] attribute to put on the top-level generated members.</summary>
        private static readonly string s_generatedCodeAttribute = $"GeneratedCodeAttribute(\"{typeof(RegexGenerator).Assembly.GetName().Name}\", \"{typeof(RegexGenerator).Assembly.GetName().Version}\")";
        /// <summary>Header comments and usings to include at the top of every generated file.</summary>
        private static readonly string[] s_headers = new string[]
        {
            "// <auto-generated/>",
            "#nullable enable",
            "#pragma warning disable CS0162 // Unreachable code",
            "#pragma warning disable CS0164 // Unreferenced label",
            "#pragma warning disable CS0219 // Variable assigned but never used",
        };

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            // Produces one entry per generated regex.  This may be:
            // - Diagnostic in the case of a failure that should end the compilation
            // - (RegexMethod regexMethod, string runnerFactoryImplementation, Dictionary<string, string[]> requiredHelpers) in the case of valid regex
            // - (RegexMethod regexMethod, string reason, Diagnostic diagnostic) in the case of a limited-support regex
            IncrementalValueProvider<ImmutableArray<object?>> codeOrDiagnostics =
                context.SyntaxProvider

                // Find all MethodDeclarationSyntax nodes attributed with RegexGenerator and gather the required information.
                .CreateSyntaxProvider(IsSyntaxTargetForGeneration, GetSemanticTargetForGeneration)
                .Where(static m => m is not null)

                // Generate the RunnerFactory for each regex, if possible.  This is where the bulk of the implementation occurs.
                .Select((state, _) =>
                {
                    if (state is not RegexMethod regexMethod)
                    {
                        Debug.Assert(state is Diagnostic);
                        return state;
                    }

                    // If we're unable to generate a full implementation for this regex, report a diagnostic.
                    // We'll still output a limited implementation that just caches a new Regex(...).
                    if (!regexMethod.Tree.Root.SupportsCompilation(out string? reason))
                    {
                        return (regexMethod, reason, Diagnostic.Create(DiagnosticDescriptors.LimitedSourceGeneration, regexMethod.MethodSyntax.GetLocation()));
                    }

                    // Generate the core logic for the regex.
                    Dictionary<string, string[]> requiredHelpers = new();
                    var sw = new StringWriter();
                    var writer = new IndentedTextWriter(sw);
                    writer.Indent += 3;
                    writer.WriteLine();
                    EmitRegexDerivedTypeRunnerFactory(writer, regexMethod, requiredHelpers);
                    writer.Indent -= 3;
                    return (regexMethod, sw.ToString(), requiredHelpers);
                })
                .Collect();

            // To avoid invalidating every regex's output when anything from the compilation changes,
            // we extract from it the only things we care about: whether unsafe code is allowed,
            // and a name based on the assembly's name, and only that information is then fed into
            // RegisterSourceOutput along with all of the cached generated data from each regex.
            IncrementalValueProvider<(bool AllowUnsafe, string? AssemblyName)> compilationDataProvider =
                context.CompilationProvider
                .Select((x, _) => (x.Options is CSharpCompilationOptions { AllowUnsafe: true }, x.AssemblyName));

            // When there something to output, take all the generated strings and concatenate them to output,
            // and raise all of the created diagnostics.
            context.RegisterSourceOutput(codeOrDiagnostics.Combine(compilationDataProvider), static (context, compilationDataAndResults) =>
            {
                ImmutableArray<object?> results = compilationDataAndResults.Left;

                // Report any top-level diagnostics.
                bool allFailures = true;
                foreach (object? result in results)
                {
                    if (result is Diagnostic d)
                    {
                        context.ReportDiagnostic(d);
                    }
                    else
                    {
                        allFailures = false;
                    }
                }
                if (allFailures)
                {
                    return;
                }

                // At this point we'll be emitting code.  Create a writer to hold it all.
                var sw = new StringWriter();
                IndentedTextWriter writer = new(sw);

                // Add file headers and required usings.
                foreach (string header in s_headers)
                {
                    writer.WriteLine(header);
                }
                writer.WriteLine();

                // For every generated type, we give it an incrementally increasing ID, in order to create
                // unique type names even in situations where method names were the same, while also keeping
                // the type names short.  Note that this is why we only generate the RunnerFactory implementations
                // earlier in the pipeline... we want to avoid generating code that relies on the class names
                // until we're able to iterate through them linearly keeping track of a deterministic ID
                // used to name them.  The boilerplate code generation that happens here is minimal when compared to
                // the work required to generate the actual matching code for the regex.
                int id = 0;
                string generatedClassName = $"__{ComputeStringHash(compilationDataAndResults.Right.AssemblyName ?? ""):x}";

                // To minimize generated code in the event of duplicated regexes, we only emit one derived Regex type per unique
                // expression/options/timeout.  A Dictionary<(expression, options, timeout), RegexMethod> is used to deduplicate, where the value of the
                // pair is the implementation used for the key.
                var emittedExpressions = new Dictionary<(string Pattern, RegexOptions Options, int? Timeout), RegexMethod>();

                // If we have any (RegexMethod regexMethod, string generatedName, string reason, Diagnostic diagnostic), these are regexes for which we have
                // limited support and need to simply output boilerplate.  We need to emit their diagnostics.
                // If we have any (RegexMethod regexMethod, string generatedName, string runnerFactoryImplementation, Dictionary<string, string[]> requiredHelpers),
                // those are generated implementations to be emitted.  We need to gather up their required helpers.
                Dictionary<string, string[]> requiredHelpers = new();
                foreach (object? result in results)
                {
                    RegexMethod? regexMethod = null;
                    if (result is ValueTuple<RegexMethod, string, Diagnostic> limitedSupportResult)
                    {
                        context.ReportDiagnostic(limitedSupportResult.Item3);
                        regexMethod = limitedSupportResult.Item1;
                    }
                    else if (result is ValueTuple<RegexMethod, string, Dictionary<string, string[]>> regexImpl)
                    {
                        foreach (KeyValuePair<string, string[]> helper in regexImpl.Item3)
                        {
                            if (!requiredHelpers.ContainsKey(helper.Key))
                            {
                                requiredHelpers.Add(helper.Key, helper.Value);
                            }
                        }

                        regexMethod = regexImpl.Item1;
                    }

                    if (regexMethod is not null)
                    {
                        var key = (regexMethod.Pattern, regexMethod.Options, regexMethod.MatchTimeout);
                        if (emittedExpressions.TryGetValue(key, out RegexMethod? implementation))
                        {
                            regexMethod.IsDuplicate = true;
                            regexMethod.GeneratedName = implementation.GeneratedName;
                        }
                        else
                        {
                            regexMethod.IsDuplicate = false;
                            regexMethod.GeneratedName = $"{regexMethod.MethodName}_{id++}";
                            emittedExpressions.Add(key, regexMethod);
                        }

                        EmitRegexPartialMethod(regexMethod, writer, generatedClassName);
                        writer.WriteLine();
                    }
                }

                // At this point we've emitted all the partial method definitions, but we still need to emit the actual regex-derived implementations.
                // These are all emitted inside of our generated class.
                // TODO https://github.com/dotnet/csharplang/issues/5529:
                // When C# provides a mechanism for shielding generated code from the rest of the project, it should be employed
                // here for the generated class.  At that point, the generated class wrapper can be removed, and all of the types
                // generated inside of it (one for each regex as well as the helpers type) should be shielded.

                writer.WriteLine($"namespace {GeneratedNamespace}");
                writer.WriteLine($"{{");

                // We emit usings here now that we're inside of a namespace block and are no longer emitting code into
                // a user's partial type.  We can now rely on binding rules mapping to these usings and don't need to
                // use global-qualified names for the rest of the implementation.
                writer.WriteLine($"    using System;");
                writer.WriteLine($"    using System.CodeDom.Compiler;");
                writer.WriteLine($"    using System.Collections;");
                writer.WriteLine($"    using System.ComponentModel;");
                writer.WriteLine($"    using System.Globalization;");
                writer.WriteLine($"    using System.Runtime.CompilerServices;");
                writer.WriteLine($"    using System.Text.RegularExpressions;");
                writer.WriteLine($"    using System.Threading;");
                writer.WriteLine($"");
                if (compilationDataAndResults.Right.AllowUnsafe)
                {
                    writer.WriteLine($"    [SkipLocalsInit]");
                }
                writer.WriteLine($"    [{s_generatedCodeAttribute}]");
                writer.WriteLine($"    [EditorBrowsable(EditorBrowsableState.Never)]");
                writer.WriteLine($"    internal static class {generatedClassName}");
                writer.WriteLine($"    {{");

                // Emit each Regex-derived type.
                writer.Indent += 2;
                foreach (object? result in results)
                {
                    if (result is ValueTuple<RegexMethod, string, Diagnostic> limitedSupportResult)
                    {
                        if (!limitedSupportResult.Item1.IsDuplicate)
                        {
                            EmitRegexLimitedBoilerplate(writer, limitedSupportResult.Item1, limitedSupportResult.Item2);
                            writer.WriteLine();
                        }
                    }
                    else if (result is ValueTuple<RegexMethod, string, Dictionary<string, string[]>> regexImpl)
                    {
                        if (!regexImpl.Item1.IsDuplicate)
                        {
                            EmitRegexDerivedImplementation(writer, regexImpl.Item1, regexImpl.Item2);
                            writer.WriteLine();
                        }
                    }
                }
                writer.Indent -= 2;

                // If any of the Regex-derived types asked for helper methods, emit those now.
                if (requiredHelpers.Count != 0)
                {
                    writer.Indent += 2;
                    writer.WriteLine($"/// <summary>Helper methods used by generated <see cref=\"Regex\"/>-derived implementations.</summary>");
                    writer.WriteLine($"private static class {HelpersTypeName}");
                    writer.WriteLine($"{{");
                    writer.Indent++;
                    bool sawFirst = false;
                    foreach (KeyValuePair<string, string[]> helper in requiredHelpers)
                    {
                        if (sawFirst)
                        {
                            writer.WriteLine();
                        }
                        sawFirst = true;

                        foreach (string value in helper.Value)
                        {
                            writer.WriteLine(value);
                        }
                    }
                    writer.Indent--;
                    writer.WriteLine($"}}");
                    writer.Indent -= 2;
                }

                writer.WriteLine($"    }}");
                writer.WriteLine($"}}");

                // Save out the source
                context.AddSource("RegexGenerator.g.cs", sw.ToString());
            });
        }

        /// <summary>Computes a hash of the string.</summary>
        /// <remarks>
        /// Currently an FNV-1a hash function. The actual algorithm used doesn't matter; just something
        /// simple to create a deterministic, pseudo-random value that's based on input text.
        /// </remarks>
        private static uint ComputeStringHash(string s)
        {
            uint hashCode = 2166136261;
            foreach (char c in s)
            {
                hashCode = (c ^ hashCode) * 16777619;
            }
            return hashCode;
        }
    }
}
