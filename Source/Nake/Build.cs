using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

using Dotnet.Script.DependencyModel.Logging;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Emit;

using Nake.Magic;
using Nake.Scripting;

namespace Nake
{
    class BuildInput
    {
        public readonly ScriptSource Script;
        public readonly IDictionary<string, string> Substitutions;
        public readonly bool Debug;

        public BuildInput(ScriptSource script, IDictionary<string, string> substitutions, bool debug)
        {
            Script = script;
            Substitutions = substitutions;
            Debug = debug;
        }

        public void Include(AssemblyReference[] dependencies) => 
            Dependencies = dependencies;

        public AssemblyReference[] Dependencies { get; private set; }
    }

    class BuildEngine
    {
        readonly Logger logger;
        readonly IEnumerable<AssemblyReference> references;
        readonly IEnumerable<string> namespaces;

        public BuildEngine(
            Logger logger,
            IEnumerable<AssemblyReference> references = null,
            IEnumerable<string> namespaces = null)
        {
            this.logger = logger;
            this.references = references ?? Enumerable.Empty<AssemblyReference>();
            this.namespaces = namespaces ?? Enumerable.Empty<string>();
        }

        public BuildResult Build(BuildInput input)
        {
            var magic = new PixieDust(Compile(input.Script, input.Dependencies));
            return magic.Apply(input.Substitutions, input.Debug);
        }

        CompiledScript Compile(ScriptSource source, AssemblyReference[] dependencies)
        {
            var script = new Script(logger);

            foreach (var each in references)
                script.AddReference(each);

            foreach (var each in namespaces)
                script.ImportNamespace(each);

            return script.Compile(source, dependencies);
        }
    }

    class PixieDust
    {
        readonly CompiledScript script;

        public PixieDust(CompiledScript script)
        {
            this.script = script;
        }

        public BuildResult Apply(IDictionary<string, string> substitutions, bool debug)
        {
            var analyzer = new Analyzer(script.Compilation, substitutions);
            var analyzed = analyzer.Analyze();

            var rewriter = new Rewriter(script.Compilation, analyzed);
            var rewritten = rewriter.Rewrite();

            byte[] assembly;
            byte[] symbols = null;

            if (debug)
                EmitDebug(rewritten, out assembly, out symbols);
            else
                Emit(rewritten, out assembly);

            return new BuildResult(
                analyzed.Tasks.ToArray(), 
                script.References.ToArray(), 
                rewriter.Captured.ToArray(), 
                assembly, symbols
            );            
        }

        void Emit(Compilation compilation, out byte[] assembly)
        {
            using var assemblyStream = new MemoryStream();
            
            Check(compilation, compilation.Emit(assemblyStream));
            
            assembly = assemblyStream.GetBuffer();
        }

        void EmitDebug(Compilation compilation, out byte[] assembly, out byte[] symbols)
        {
            using var assemblyStream = new MemoryStream();
            using var symbolStream = new MemoryStream();
            
            Check(compilation, compilation.Emit(assemblyStream, pdbStream: symbolStream));
            
            assembly = assemblyStream.GetBuffer();
            symbols = symbolStream.GetBuffer();
        }

        void Check(Compilation compilation, EmitResult result)
        {
            if (result.Success)
                return;

            var errors = result.Diagnostics
                .Where(x => x.Severity == DiagnosticSeverity.Error)
                .ToArray();

            if (errors.Any())
                throw new RewrittenScriptCompilationException(SourceText(script.Compilation), SourceText(compilation), errors);

            static string SourceText(Compilation arg) => arg.SyntaxTrees.First().ToString();
        }
    }

    class BuildResult
    {
        public readonly Task[] Tasks;
        public readonly AssemblyReference[] References;
        public readonly EnvironmentVariable[] Variables;
        public readonly Assembly Assembly;
        public readonly byte[] AssemblyBytes;
        public readonly byte[] SymbolBytes;

        public BuildResult(
            Task[] tasks,
            AssemblyReference[] references,
            EnvironmentVariable[] variables,
            byte[] assembly,
            byte[] symbols)
        {
            Tasks = tasks;
            References = references;
            AssemblyBytes = assembly;
            SymbolBytes = symbols;
            Variables = variables;
            Assembly = Load();
            Reflect();
        }

        Assembly Load()
        {
            AssemblyResolver.Register();

            foreach (var reference in References)
                AssemblyResolver.Add(reference);

            return SymbolBytes != null
                       ? Assembly.Load(AssemblyBytes, SymbolBytes)
                       : Assembly.Load(AssemblyBytes);
        }

        void Reflect()
        {
            foreach (var task in Tasks)
                task.Reflect(Assembly);
        }
    }
}