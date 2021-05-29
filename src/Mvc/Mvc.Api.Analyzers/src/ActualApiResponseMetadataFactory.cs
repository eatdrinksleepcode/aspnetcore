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
    public static class ActualApiResponseMetadataFactory
    {
        private static readonly Func<SyntaxNode, bool> _shouldDescendIntoChildren = ShouldDescendIntoChildren;

        internal static bool TryGetActualResponseMetadata(
            in ApiControllerSymbolCache symbolCache,
            SemanticModel semanticModel,
            OperationBlockStartAnalysisContext blockStartContext,
            out IList<ActualApiResponseMetadata> actualResponseMetadata)
        {
            var localActualResponseMetadata = new List<ActualApiResponseMetadata>();

            var allReturnStatementsReadable = true;
            var localSymbolCache = symbolCache;

            blockStartContext.RegisterOperationAction(returnContext =>
            {
                // if (returnStatementSyntax.IsMissing || returnStatementSyntax.Expression == null || returnStatementSyntax.Expression.IsMissing)
                // {
                //     // Ignore malformed return statements.
                //     allReturnStatementsReadable = false;
                //     continue;
                // }

                var returnOp = (IReturnOperation)returnContext.Operation;
                var responseMetadata = InspectReturnStatementSyntax(
                    localSymbolCache,
                    returnOp.ReturnedValue);

                foreach (var metadata in responseMetadata)
                {
                    if (metadata != null)
                    {
                        localActualResponseMetadata.Add(metadata.Value);
                    }
                    else
                    {
                        allReturnStatementsReadable = false;
                    }
                }
            }, OperationKind.Return);

            actualResponseMetadata = localActualResponseMetadata;

            return allReturnStatementsReadable;
        }

        /// <summary>
        /// This method looks at individual return statments and attempts to parse the status code and the return type.
        /// Given a <see cref="MethodDeclarationSyntax"/> for an action, this method inspects return statements in the body.
        /// If the returned type is not assignable from IActionResult, it assumes that an "object" value is being returned. e.g. return new Person();
        /// For return statements returning an action result, it attempts to infer the status code and return type. Helper methods in controller,
        /// values set in initializer and new-ing up an IActionResult instance are supported.
        /// </summary>
        internal static bool TryGetActualResponseMetadata(
            in ApiControllerSymbolCache symbolCache,
            SemanticModel semanticModel,
            MethodDeclarationSyntax methodSyntax,
            out IList<ActualApiResponseMetadata> actualResponseMetadata)
        {
            var localActualResponseMetadata = new List<ActualApiResponseMetadata>();

            var allReturnStatementsReadable = true;
            var localSymbolCache = symbolCache;

            void AnalyzeResponseExpression(ExpressionSyntax expressionSyntax)
            {
                var responseMetadata = InspectReturnStatementSyntax(
                    localSymbolCache,
                    semanticModel.GetOperation(expressionSyntax));

                foreach (var metadata in responseMetadata)
                {
                    if (metadata != null)
                    {
                        localActualResponseMetadata.Add(metadata.Value);
                    }
                    else
                    {
                        allReturnStatementsReadable = false;
                    }
                }
            }

            foreach (var returnStatementSyntax in methodSyntax.DescendantNodes(_shouldDescendIntoChildren).OfType<ReturnStatementSyntax>())
            {
                if (returnStatementSyntax.IsMissing || returnStatementSyntax.Expression == null || returnStatementSyntax.Expression.IsMissing)
                {
                    // Ignore malformed return statements.
                    allReturnStatementsReadable = false;
                    continue;
                }

                AnalyzeResponseExpression(returnStatementSyntax.Expression);
            }

            if (methodSyntax.ExpressionBody != null)
            {
                if (methodSyntax.ExpressionBody.IsMissing || methodSyntax.ExpressionBody.Expression.IsMissing)
                {
                    // Ignore malformed expression bodies.
                    allReturnStatementsReadable = false;
                }
                else
                {
                    AnalyzeResponseExpression(methodSyntax.ExpressionBody.Expression);
                }
            }

            actualResponseMetadata = localActualResponseMetadata;

            return allReturnStatementsReadable;
        }

        internal static IEnumerable<ActualApiResponseMetadata?> InspectReturnStatementSyntax(
            in ApiControllerSymbolCache symbolCache,
            IReturnOperation returnOp)
        {
            return InspectReturnStatementSyntax(symbolCache, returnOp.ReturnedValue);
        }

        private static IEnumerable<ActualApiResponseMetadata?> InspectReturnStatementSyntax(
            in ApiControllerSymbolCache symbolCache,
            IOperation returnedValue)
        {
            if (returnedValue.Kind == OperationKind.Conditional)
            {
                var conditionalExpression = (IConditionalOperation) returnedValue;
                return InspectReturnStatementSyntax(symbolCache, conditionalExpression.WhenTrue)
                    .Concat(InspectReturnStatementSyntax(symbolCache, conditionalExpression.WhenFalse));
            }
            else
            {
                return new [] {InspectReturnStatementSyntaxSingle(symbolCache, returnedValue)};
            }
        }

        private static ActualApiResponseMetadata? InspectReturnStatementSyntaxSingle(
            in ApiControllerSymbolCache symbolCache,
            IOperation returnedValue)
        {
            var returnedValueType = returnedValue.Type;

            if (returnedValueType.TypeKind == TypeKind.Error)
            {
                return null;
            }

            if (!symbolCache.IActionResult.IsAssignableFrom(returnedValueType))
            {
                // Return expression is not an instance of IActionResult. Must be returning the "model".
                return new ActualApiResponseMetadata(returnedValue, returnedValueType);
            }

            var defaultStatusCodeAttribute = returnedValueType
                .GetAttributes(symbolCache.DefaultStatusCodeAttribute, inherit: true)
                .FirstOrDefault();

            var statusCode = GetDefaultStatusCode(defaultStatusCodeAttribute);
            ITypeSymbol? returnType = null;
            switch (returnedValue)
            {
                case IInvocationOperation invocation:
                    {
                        // Covers the 'return StatusCode(200)' case.
                        var result = InspectMethodArguments(invocation.TargetMethod, invocation.Arguments);
                        statusCode = result.statusCode ?? statusCode;
                        returnType = result.returnType;
                        break;
                    }

                case IObjectCreationOperation creation:
                    {
                        if (creation.Arguments == null)
                        {
                            throw new ArgumentNullException(nameof(creation.Arguments));
                        }
                        // Read values from 'return new StatusCodeResult(200) case.
                        var result = InspectMethodArguments(creation.Constructor, creation.Arguments);
                        statusCode = result.statusCode ?? statusCode;
                        returnType = result.returnType;

                        // Read values from property assignments e.g. 'return new ObjectResult(...) { StatusCode = 200 }'.
                        // Property assignments override constructor assigned values and defaults.
                        result = InspectInitializers(symbolCache, creation.Initializer);
                        statusCode = result.statusCode ?? statusCode;
                        returnType = result.returnType ?? returnType;
                        break;
                    }
            }

            if (statusCode == null)
            {
                return null;
            }

            return new ActualApiResponseMetadata(returnedValue, statusCode.Value, returnType);
        }

        private static (int? statusCode, ITypeSymbol? returnType) InspectInitializers(
            in ApiControllerSymbolCache symbolCache,
            IObjectOrCollectionInitializerOperation initializer)
        {
            int? statusCode = null;
            ITypeSymbol? typeSymbol = null;

            for (var i = 0; initializer != null && i < initializer.Initializers.Length; i++)
            {
                var expression = initializer.Initializers[i];

                if (!(expression is IAssignmentOperation assignment) ||
                    !(assignment.Target is IPropertyReferenceOperation propertyRef))
                {
                    continue;
                }

                var property = propertyRef.Property;
                if (IsInterfaceImplementation(property, symbolCache.StatusCodeActionResultStatusProperty) &&
                    TryGetExpressionStatusCode(assignment.Value, out var statusCodeValue))
                {
                    // Look for assignments to IStatusCodeActionResult.StatusCode
                    statusCode = statusCodeValue;
                }
                else if (HasAttributeNamed(property, ApiSymbolNames.ActionResultObjectValueAttribute))
                {
                    // Look for assignment to a property annotated with [ActionResultObjectValue]
                    typeSymbol = GetExpressionObjectType(assignment.Value);
                }
            }

            return (statusCode, typeSymbol);
        }

        private static (int? statusCode, ITypeSymbol? returnType) InspectMethodArguments(
            IMethodSymbol method,
            ImmutableArray<IArgumentOperation> argumentList)
        {
            int? statusCode = null;
            ITypeSymbol? typeSymbol = null;

            for (var i = 0; i < method.Parameters.Length; i++)
            {
                var parameter = method.Parameters[i];
                if (HasAttributeNamed(parameter, ApiSymbolNames.ActionResultStatusCodeAttribute))
                {
                    var argument = argumentList[parameter.Ordinal];
                    if (TryGetExpressionStatusCode(argument.Value, out var statusCodeValue))
                    {
                        statusCode = statusCodeValue;
                    }
                }

                if (HasAttributeNamed(parameter, ApiSymbolNames.ActionResultObjectValueAttribute))
                {
                    var argument = argumentList[parameter.Ordinal];
                    typeSymbol = GetExpressionObjectType(argument.Value);
                }
            }

            return (statusCode, typeSymbol);
        }

        private static ITypeSymbol? GetExpressionObjectType(IOperation expression)
        {
            return expression.Type;
        }

        private static bool TryGetExpressionStatusCode(
            IOperation expression,
            out int statusCode)
        {
            // HACK ConstantValue == null
            if (expression is ILiteralOperation literal && literal.ConstantValue.Value is int literalStatusCode)
            {
                // Covers the 'return StatusCode(200)' case.
                statusCode = literalStatusCode;
                return true;
            }

            if (expression is ILocalReferenceOperation localReference)
            {
                if (localReference.Local.HasConstantValue && localReference.Local.ConstantValue is int localStatusCode)
                {
                    // Covers the 'return StatusCode(statusCode)' case, where 'statusCode' is a local constant.
                    statusCode = localStatusCode;
                    return true;
                }
            }
            else if (expression is IFieldReferenceOperation fieldReference)
            {
                if (fieldReference.Field.HasConstantValue && fieldReference.Field.ConstantValue is int constantStatusCode)
                {
                    // Covers the 'return StatusCode(StatusCodes.Status200OK)' case.
                    // It also covers the 'return StatusCode(StatusCode)' case, where 'StatusCode' is a constant field.
                    statusCode = constantStatusCode;
                    return true;
                }
            }

            statusCode = default;
            return false;
        }

        private static bool ShouldDescendIntoChildren(SyntaxNode syntaxNode)
        {
            return !syntaxNode.IsKind(SyntaxKind.LocalFunctionStatement) &&
                !syntaxNode.IsKind(SyntaxKind.ParenthesizedLambdaExpression) &&
                !syntaxNode.IsKind(SyntaxKind.SimpleLambdaExpression) &&
                !syntaxNode.IsKind(SyntaxKind.AnonymousMethodExpression);
        }

        internal static int? GetDefaultStatusCode(AttributeData attribute)
        {
            if (attribute != null &&
                attribute.ConstructorArguments.Length == 1 &&
                attribute.ConstructorArguments[0].Kind == TypedConstantKind.Primitive &&
                attribute.ConstructorArguments[0].Value is int statusCode)
            {
                return statusCode;
            }

            return null;
        }

        private static bool IsInterfaceImplementation(IPropertySymbol property, IPropertySymbol statusCodeActionResultStatusProperty)
        {
            if (property.Name != statusCodeActionResultStatusProperty.Name)
            {
                return false;
            }

            for (var i = 0; i < property.ExplicitInterfaceImplementations.Length; i++)
            {
                if (SymbolEqualityComparer.Default.Equals(property.ExplicitInterfaceImplementations[i], statusCodeActionResultStatusProperty))
                {
                    return true;
                }
            }

            var implementedProperty = property.ContainingType.FindImplementationForInterfaceMember(statusCodeActionResultStatusProperty);
            return SymbolEqualityComparer.Default.Equals(implementedProperty, property);
        }

        private static bool HasAttributeNamed(ISymbol symbol, string attributeName)
        {
            var attributes = symbol.GetAttributes();
            var length = attributes.Length;
            for (var i = 0; i < length; i++)
            {
                if (attributes[i].AttributeClass.Name == attributeName)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
