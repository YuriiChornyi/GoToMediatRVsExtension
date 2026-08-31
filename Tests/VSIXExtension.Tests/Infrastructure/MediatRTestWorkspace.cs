using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace VSIXExtension.Tests.Infrastructure
{
    /// <summary>
    /// A source file in a test fixture. The file name matters: the production code filters syntax
    /// trees by extension, so use ".cs" / ".razor.g.cs" / ".razor" deliberately.
    /// </summary>
    public sealed class TestSource
    {
        public TestSource(string fileName, string text)
        {
            FileName = fileName;
            Text = text;
        }

        public string FileName { get; }
        public string Text { get; }
    }

    /// <summary>
    /// A project in a test fixture. Setting <see cref="ReferencesMediatR"/> to false lets a test
    /// prove that projects which do not reference MediatR are handled correctly.
    /// </summary>
    public sealed class TestProject
    {
        public TestProject(string name, params TestSource[] sources)
        {
            Name = name;
            Sources = sources;
        }

        public string Name { get; }
        public IReadOnlyList<TestSource> Sources { get; }

        public bool ReferencesMediatR { get; set; } = true;

        /// <summary>Names of other projects in the fixture that this one references.</summary>
        public string[] ProjectReferences { get; set; } = Array.Empty<string>();
    }

    /// <summary>
    /// An in-memory Roslyn solution that looks enough like a real one for the extension's analysis
    /// code: real MediatR metadata, real file paths, real compilations.
    /// </summary>
    public sealed class MediatRTestWorkspace : IDisposable
    {
        private const string FixtureRoot = @"C:\Fixtures";

        private static readonly ImmutableArray<MetadataReference> FrameworkReferences = CreateFrameworkReferences();

        // MediatR 12 splits its surface across two assemblies: the marker interfaces (IRequest,
        // INotification, IStreamRequest) live in MediatR.Contracts, the handler and pipeline
        // interfaces in MediatR. Fixtures need both.
        private static readonly ImmutableArray<MetadataReference> MediatRReferences = ImmutableArray.Create(
            (MetadataReference)MetadataReference.CreateFromFile(typeof(MediatR.IRequest).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(MediatR.IMediator).Assembly.Location));

        private readonly AdhocWorkspace _workspace;

        private MediatRTestWorkspace(AdhocWorkspace workspace) => _workspace = workspace;

        public Workspace Workspace => _workspace;

        public Solution Solution => _workspace.CurrentSolution;

        /// <summary>Single-project fixture. File names default to Source0.cs, Source1.cs, ...</summary>
        public static MediatRTestWorkspace Create(params string[] sources)
        {
            var files = sources.Select((s, i) => new TestSource($"Source{i}.cs", s)).ToArray();
            return Create(new TestProject("TestProject", files));
        }

        public static MediatRTestWorkspace Create(params TestSource[] sources)
            => Create(new TestProject("TestProject", sources));

        public static MediatRTestWorkspace Create(params TestProject[] projects)
        {
            var workspace = new AdhocWorkspace();
            var solution = workspace.CurrentSolution;
            var projectIds = projects.ToDictionary(p => p.Name, p => ProjectId.CreateNewId(p.Name));

            foreach (var project in projects)
            {
                var projectId = projectIds[project.Name];
                var references = project.ReferencesMediatR
                    ? FrameworkReferences.AddRange(MediatRReferences)
                    : FrameworkReferences;

                solution = solution.AddProject(ProjectInfo.Create(
                    projectId,
                    VersionStamp.Create(),
                    project.Name,
                    project.Name,
                    LanguageNames.CSharp,
                    compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
                    parseOptions: new CSharpParseOptions(LanguageVersion.Latest),
                    metadataReferences: references,
                    projectReferences: project.ProjectReferences.Select(name =>
                        new ProjectReference(projectIds.TryGetValue(name, out var id)
                            ? id
                            : throw new InvalidOperationException($"Unknown project reference '{name}'.")))));

                foreach (var source in project.Sources)
                {
                    var filePath = Path.Combine(FixtureRoot, project.Name, source.FileName);
                    solution = solution.AddDocument(
                        DocumentId.CreateNewId(projectId, source.FileName),
                        source.FileName,
                        SourceText.From(source.Text, Encoding.UTF8),
                        filePath: filePath);
                }
            }

            if (!workspace.TryApplyChanges(solution))
                throw new InvalidOperationException("Failed to build the test solution.");

            return new MediatRTestWorkspace(workspace);
        }

        /// <summary>
        /// Resolves a type declared in the fixture, together with the semantic model of the tree
        /// that declares it. Throws if the fixture does not compile, so a typo in a fixture fails
        /// loudly instead of quietly producing "no handlers found".
        /// </summary>
        public async Task<TestType> GetTypeAsync(string metadataName)
        {
            await AssertFixtureCompilesAsync();

            foreach (var project in Solution.Projects)
            {
                var compilation = await project.GetCompilationAsync();
                var symbol = compilation.GetTypeByMetadataName(metadataName);
                if (symbol == null)
                    continue;

                var tree = symbol.DeclaringSyntaxReferences.FirstOrDefault()?.SyntaxTree;
                var semanticModel = tree != null ? compilation.GetSemanticModel(tree) : null;
                return new TestType(symbol, semanticModel);
            }

            throw new InvalidOperationException(
                $"Type '{metadataName}' was not found in the fixture. GetTypeByMetadataName needs " +
                "the fully qualified name, and generic types need the arity suffix (e.g. `1).");
        }

        /// <summary>Fails the test with compiler diagnostics if any fixture source is broken.</summary>
        public async Task AssertFixtureCompilesAsync()
        {
            foreach (var project in Solution.Projects)
            {
                var compilation = await project.GetCompilationAsync();
                var errors = compilation.GetDiagnostics()
                    .Where(d => d.Severity == DiagnosticSeverity.Error)
                    .ToList();

                if (errors.Count > 0)
                {
                    throw new InvalidOperationException(
                        $"Fixture project '{project.Name}' does not compile:{Environment.NewLine}" +
                        string.Join(Environment.NewLine, errors.Select(e => "  " + e)));
                }
            }
        }

        public void Dispose() => _workspace.Dispose();

        private static ImmutableArray<MetadataReference> CreateFrameworkReferences()
        {
            // The fixtures only need core BCL surface. Pulling in the whole trusted-platform list
            // would also drag this test assembly - and its linked copy of the production types -
            // into every fixture compilation.
            var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "System.Private.CoreLib",
                "System.Runtime",
                "netstandard",
                "System.Collections",
                "System.Linq",
                "System.Linq.Expressions",
                "System.Threading",
                "System.Threading.Tasks",
                "System.Runtime.Extensions",
                "System.ObjectModel",
                "System.Console",
                "Microsoft.Extensions.DependencyInjection.Abstractions"
            };

            var trustedAssemblies = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? string.Empty)
                .Split(Path.PathSeparator)
                .Where(path => !string.IsNullOrEmpty(path) &&
                               wanted.Contains(Path.GetFileNameWithoutExtension(path)))
                .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path));

            return trustedAssemblies.ToImmutableArray();
        }
    }

    /// <summary>A type symbol resolved from a fixture, plus the semantic model that declares it.</summary>
    public sealed class TestType
    {
        public TestType(INamedTypeSymbol symbol, SemanticModel semanticModel)
        {
            Symbol = symbol;
            SemanticModel = semanticModel;
        }

        public INamedTypeSymbol Symbol { get; }
        public SemanticModel SemanticModel { get; }

        public void Deconstruct(out INamedTypeSymbol symbol, out SemanticModel semanticModel)
        {
            symbol = Symbol;
            semanticModel = SemanticModel;
        }
    }
}
