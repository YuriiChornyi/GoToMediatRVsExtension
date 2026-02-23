using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.LanguageServices;
using Microsoft.VisualStudio.Shell;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace VSIXExtension.Helpers
{
    public class RoslynSymbolHelper
    {
        public static async Task<(SemanticModel semanticModel, INamedTypeSymbol typeSymbol)> GetSymbolsFromFilePathAsync(string filePath)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            // Get the Visual Studio workspace
            var componentModel = (IComponentModel)Package.GetGlobalService(typeof(SComponentModel));
            var workspace = componentModel.GetService<VisualStudioWorkspace>();

            // Find the document in the workspace
            var document = workspace.CurrentSolution.GetDocumentIdsWithFilePath(filePath).FirstOrDefault();
            if (document == null)
            {
                throw new FileNotFoundException($"Document not found in workspace: {filePath}");
            }

            var doc = workspace.CurrentSolution.GetDocument(document);
            if (doc == null)
            {
                throw new InvalidOperationException("Could not get document from workspace");
            }

            // Get the semantic model
            var semanticModel = await doc.GetSemanticModelAsync();
            if (semanticModel == null)
            {
                throw new InvalidOperationException("Could not get semantic model");
            }

            // Get the syntax tree
            var syntaxTree = await doc.GetSyntaxTreeAsync();
            var root = await syntaxTree.GetRootAsync();

            // Find the first class/interface/struct declaration
            var typeDeclaration = root.DescendantNodes()
                .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax>()
                .FirstOrDefault();

            if (typeDeclaration == null)
            {
                throw new InvalidOperationException("No type declaration found in the file");
            }

            // Get the type symbol
            var typeSymbol = semanticModel.GetDeclaredSymbol(typeDeclaration) as INamedTypeSymbol;
            if (typeSymbol == null)
            {
                throw new InvalidOperationException("Could not get type symbol");
            }

            return (semanticModel, typeSymbol);
        }

    }
}
