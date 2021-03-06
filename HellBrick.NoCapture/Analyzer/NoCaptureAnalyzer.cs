﻿using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace HellBrick.NoCapture.Analyzer
{
	[DiagnosticAnalyzer( LanguageNames.CSharp )]
	public class NoCaptureAnalyzer : DiagnosticAnalyzer
	{
		public const string DiagnosticId = "HBNoCapture";
		private const string _noCaptureAttributeName = nameof( NoCaptureAttribute );

		private static readonly DiagnosticDescriptor _descriptor
			= new DiagnosticDescriptor
			(
				DiagnosticId,
				"Capturing is not allowed",
				"{0}( {1} ) requires a non-capturing lambda. Captured variables: {2}.",
				"Performance",
				DiagnosticSeverity.Error,
				isEnabledByDefault: true
			);

		private static readonly ImmutableArray<SyntaxKind> _targetNodeTypes
			= ImmutableArray.Create
			(
				SyntaxKind.SimpleLambdaExpression,
				SyntaxKind.ParenthesizedLambdaExpression
			);

		public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create( _descriptor );

		public override void Initialize( AnalysisContext context )
		{
			context.ConfigureGeneratedCodeAnalysis( GeneratedCodeAnalysisFlags.None );
			context.RegisterSyntaxNodeAction( c => ExamineNode( c ), _targetNodeTypes );

			void ExamineNode( SyntaxNodeAnalysisContext nodeContext )
			{
				if ( TryGetSymbols( nodeContext.Node, nodeContext.SemanticModel, out IMethodSymbol methodSymbol, out IParameterSymbol parameterSymbol ) )
				{
					if ( HasNoCaptureAttribute( methodSymbol ) || HasNoCaptureAttribute( parameterSymbol ) )
					{
						IEnumerable<ISymbol> capturedSymbols = GetCapturedSymbols();
						if ( capturedSymbols.Any() )
						{
							Diagnostic diagnostic
								= Diagnostic.Create
								(
									_descriptor,
									nodeContext.Node.GetLocation(),
									methodSymbol.Name,
									parameterSymbol.Name,
									String.Join( ", ", capturedSymbols.Select( s => s.Name ) )
								);

							nodeContext.ReportDiagnostic( diagnostic );
						}
					}

					bool HasNoCaptureAttribute( ISymbol symbol )
						=> symbol.GetAttributes().Any( a => a.AttributeClass.Name == _noCaptureAttributeName );

					IEnumerable<ISymbol> GetCapturedSymbols()
					{
						DataFlowAnalysis dataFlow = nodeContext.SemanticModel.AnalyzeDataFlow( nodeContext.Node );
						ImmutableArray<ISymbol> capturedSymbols = dataFlow.Captured.IntersectWith( dataFlow.ReadInside );

						if ( capturedSymbols.IsEmpty )
							return Enumerable.Empty<ISymbol>();

						IEnumerable<ISymbol> childDeclared = nodeContext
							.Node
							.DescendantNodesAndSelf( descendIntoChildren: _ => true, descendIntoTrivia: false )
							.Where( child => _targetNodeTypes.Contains( child.Kind() ) )
							.Select( child => nodeContext.SemanticModel.AnalyzeDataFlow( child ) )
							.Where( dataFlow => dataFlow.Succeeded )
							.Select( dataFlow => dataFlow.VariablesDeclared )
							.Where( declared => declared.Length > 0 )
							.SelectMany( x => x )
						;

						return capturedSymbols
							.Except( childDeclared )
						;
					}
				}
			}

			bool TryGetSymbols( SyntaxNode node, SemanticModel semanticModel, out IMethodSymbol method, out IParameterSymbol parameter )
			{
				if
				(
					node.Parent is ArgumentSyntax argument
					&& argument.Parent is ArgumentListSyntax argumentList
					&& argumentList.Parent is InvocationExpressionSyntax invocation
					&& semanticModel.GetSingleSymbol( invocation.Expression ) is IMethodSymbol methodSymbol
				)
				{
					method = methodSymbol;
					parameter
						= argument.NameColon is NameColonSyntax nameColon
						? GetParameterByName()
						: GetParameterByOrder();

					return true;

					IParameterSymbol GetParameterByName() => methodSymbol.Parameters.First( p => p.Name == nameColon.Name.Identifier.Text );

					IParameterSymbol GetParameterByOrder()
					{
						int argumentIndex = argumentList.Arguments.IndexOf( argument );

						/// params[] method invocations might have more arguments than parameters.
						/// If they do, all extra arguments are actually passed to the last parameter.
						int parameterIndex = argumentIndex < methodSymbol.Parameters.Length ? argumentIndex : methodSymbol.Parameters.Length - 1;
						return methodSymbol.Parameters[ parameterIndex ];
					}
				}
				else
				{
					method = null;
					parameter = null;
					return false;
				}
			}
		}
	}
}
