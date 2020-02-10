﻿using System.Collections.Generic;
using System.Linq;
using Rubberduck.Inspections.Abstract;
using Rubberduck.Inspections.Results;
using Rubberduck.Parsing.Grammar;
using Rubberduck.Parsing.Inspections.Abstract;
using Rubberduck.Resources.Inspections;
using Rubberduck.Parsing.Symbols;
using Rubberduck.Parsing.VBA;
using Rubberduck.Inspections.Inspections.Extensions;
using Rubberduck.Parsing;
using Rubberduck.Parsing.VBA.DeclarationCaching;
using Rubberduck.VBEditor;

namespace Rubberduck.Inspections.Concrete
{
    /// <summary>
    /// Warns about member calls against an extensible interface, that cannot be validated at compile-time.
    /// </summary>
    /// <why>
    /// Extensible COM types can have members attached at run-time; VBA cannot bind these member calls at compile-time.
    /// If there is an early-bound alternative way to achieve the same result, it should be preferred.
    /// </why>
    /// <example hasResults="true">
    /// <![CDATA[
    /// Public Sub DoSomething(ByVal adoConnection As ADODB.Connection)
    ///     adoConnection.SomeStoredProcedure 42
    /// End Sub
    /// ]]>
    /// </example>
    /// <example hasResults="false">
    /// <![CDATA[
    /// Public Sub DoSomething(ByVal adoConnection As ADODB.Connection)
    ///     Dim adoCommand As ADODB.Command
    ///     Set adoCommand.ActiveConnection = adoConnection
    ///     adoCommand.CommandText = "SomeStoredProcedure"
    ///     adoCommand.CommandType = adCmdStoredProc
    ///     adoCommand.Parameters.Append adocommand.CreateParameter(Value:=42)
    ///     adoCommand.Execute
    /// End Sub
    /// ]]>
    /// </example>
    public sealed class MemberNotOnInterfaceInspection : DeclarationInspectionBase<Declaration>
    {
        public MemberNotOnInterfaceInspection(RubberduckParserState state)
            : base(state)
        { }

        protected override IEnumerable<Declaration> RelevantDeclarationsInModule(QualifiedModuleName module, DeclarationFinder finder)
        {
            return finder.UnresolvedMemberDeclarations(module);
        }

        protected override (bool isResult, Declaration properties) IsResultDeclarationWithAdditionalProperties(Declaration declaration, DeclarationFinder finder)
        {
            if (!(declaration is UnboundMemberDeclaration member))
            {
                return (false, null);
            }

            var callingContext = member.CallingContext is VBAParser.NewExprContext newExprContext
                ? (newExprContext.expression() as VBAParser.LExprContext)?.lExpression()
                : member.CallingContext;

            if (callingContext == null)
            {
                return (false, null);
            }

            var callingContextSelection = new QualifiedSelection(declaration.QualifiedModuleName, callingContext.GetSelection());
            var usageReferences = finder.IdentifierReferences(callingContextSelection);
            var calledDeclaration = usageReferences
                .Select(reference => reference.Declaration)
                .FirstOrDefault(usageDeclaration => usageDeclaration != null
                                                    && HasResultType(usageDeclaration));
            var isResult = calledDeclaration != null
                           && calledDeclaration.DeclarationType != DeclarationType.Control; //TODO - remove this exception after resolving #2592. Also simplify to inspect the type directly.

            return (isResult, calledDeclaration?.AsTypeDeclaration);
        }

        private static bool HasResultType(Declaration declaration)
        {
            var typeDeclaration = declaration.AsTypeDeclaration;
            return typeDeclaration != null
                   && !typeDeclaration.IsUserDefined
                   && typeDeclaration is ClassModuleDeclaration classTypeDeclaration
                   && classTypeDeclaration.IsExtensible;
        }

        protected override string ResultDescription(Declaration declaration, Declaration typeDeclaration)
        {
            var memberName = declaration.IdentifierName;
            var typeName = typeDeclaration?.IdentifierName ?? string.Empty;
            return string.Format(
                InspectionResults.MemberNotOnInterfaceInspection,
                memberName,
                typeName);
        }
    }
}
