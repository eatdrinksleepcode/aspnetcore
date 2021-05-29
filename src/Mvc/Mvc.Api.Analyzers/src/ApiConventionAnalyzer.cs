// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Microsoft.AspNetCore.Mvc.Api.Analyzers
{
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public class ApiConventionAnalyzer : DiagnosticAnalyzer
    {
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(
            ApiDiagnosticDescriptors.API1000_ActionReturnsUndocumentedStatusCode,
            ApiDiagnosticDescriptors.API1001_ActionReturnsUndocumentedSuccessResult,
            ApiDiagnosticDescriptors.API1002_ActionDoesNotReturnDocumentedStatusCode);

        public override void Initialize(AnalysisContext context)
        {
            context.EnableConcurrentExecution();
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);

            context.RegisterCompilationStartAction(compilationStartAnalysisContext =>
            {
                if (!ApiControllerSymbolCache.TryCreate(compilationStartAnalysisContext.Compilation, out var symbolCache))
                {
                    // No-op if we can't find types we care about.
                    return;
                }

                InitializeWorker(compilationStartAnalysisContext, symbolCache);
            });
        }

        private void InitializeWorker(CompilationStartAnalysisContext compilationStartAnalysisContext, ApiControllerSymbolCache symbolCache)
        {
            // compilationStartAnalysisContext.RegisterOperationBlockStartAction(blockStartContext =>
            // {
            //     var method = blockStartContext.OwningSymbol as IMethodSymbol;
            //     if (method == null || !ApiControllerFacts.IsApiControllerAction(symbolCache, method))
            //     {
            //         return;
            //     }
            //
            //     var declaredResponseMetadata = SymbolApiResponseMetadataProvider.GetDeclaredResponseMetadata(symbolCache, method);
            //     var hasUnreadableStatusCodes = !ActualApiResponseMetadataFactory.TryGetActualResponseMetadata(symbolCache, blockStartContext.Compilation.GetSemanticModel(), (MethodDeclarationSyntax) method, out var actualResponseMetadata);
            //
            //     blockStartContext.RegisterOperationBlockEndAction(blockEndContext =>
            //     {
            //         ReportDiagnosticsForMethod(actualResponseMetadata, declaredResponseMetadata, blockEndContext.ReportDiagnostic, hasUnreadableStatusCodes, blockEndContext.OwningSymbol.Locations.FirstOrDefault());
            //     });
            // });

            compilationStartAnalysisContext.RegisterSyntaxNodeAction(syntaxNodeContext =>
            {
                var cancellationToken = syntaxNodeContext.CancellationToken;
                var methodSyntax = (MethodDeclarationSyntax)syntaxNodeContext.Node;
                var semanticModel = syntaxNodeContext.SemanticModel;
                var method = semanticModel.GetDeclaredSymbol(methodSyntax, syntaxNodeContext.CancellationToken);

                if (!ApiControllerFacts.IsApiControllerAction(symbolCache, method))
                {
                    return;
                }

                var declaredResponseMetadata = SymbolApiResponseMetadataProvider.GetDeclaredResponseMetadata(symbolCache, method);
                var hasUnreadableStatusCodes = !ActualApiResponseMetadataFactory.TryGetActualResponseMetadata(symbolCache, semanticModel, methodSyntax, out var actualResponseMetadata);

                ReportDiagnosticsForMethod(actualResponseMetadata, declaredResponseMetadata, syntaxNodeContext.ReportDiagnostic, hasUnreadableStatusCodes, methodSyntax.Identifier.GetLocation());
            }, SyntaxKind.MethodDeclaration);
        }

        private static void ReportDiagnosticsForMethod(IList<ActualApiResponseMetadata> actualResponseMetadata, IList<DeclaredApiResponseMetadata> declaredResponseMetadata,
            Action<Diagnostic> reportDiagnostic, bool hasUnreadableStatusCodes, Location methodLocation)
        {
            var hasUndocumentedStatusCodes = false;
            foreach (var actualMetadata in actualResponseMetadata)
            {
                var location = actualMetadata.ReturnExpression.Syntax.GetLocation();

                if (!DeclaredApiResponseMetadata.Contains(declaredResponseMetadata, actualMetadata))
                {
                    hasUndocumentedStatusCodes = true;
                    if (actualMetadata.IsDefaultResponse)
                    {
                        reportDiagnostic(Diagnostic.Create(
                            ApiDiagnosticDescriptors.API1001_ActionReturnsUndocumentedSuccessResult,
                            location));
                    }
                    else
                    {
                        reportDiagnostic(Diagnostic.Create(
                            ApiDiagnosticDescriptors.API1000_ActionReturnsUndocumentedStatusCode,
                            location,
                            actualMetadata.StatusCode));
                    }
                }
            }

            if (hasUndocumentedStatusCodes || hasUnreadableStatusCodes)
            {
                // If we produced analyzer warnings about undocumented status codes, don't attempt to determine
                // if there are documented status codes that are missing from the method body.
                return;
            }

            for (var i = 0; i < declaredResponseMetadata.Count; i++)
            {
                var declaredMetadata = declaredResponseMetadata[i];
                if (!Contains(actualResponseMetadata, declaredMetadata))
                {
                    reportDiagnostic(Diagnostic.Create(
                        ApiDiagnosticDescriptors.API1002_ActionDoesNotReturnDocumentedStatusCode,
                        methodLocation, // TODO: make this the attribute location
                        declaredMetadata.StatusCode));
                }
            }
        }

        internal static bool Contains(IList<ActualApiResponseMetadata> actualResponseMetadata, DeclaredApiResponseMetadata declaredMetadata)
        {
            for (var i = 0; i < actualResponseMetadata.Count; i++)
            {
               if (declaredMetadata.Matches(actualResponseMetadata[i]))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
