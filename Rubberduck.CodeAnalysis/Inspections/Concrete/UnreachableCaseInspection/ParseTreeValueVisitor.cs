﻿using Antlr4.Runtime;
using Antlr4.Runtime.Tree;
using Rubberduck.Parsing;
using Rubberduck.Parsing.Grammar;
using Rubberduck.Parsing.Symbols;
using System;
using System.Collections.Generic;
using System.Linq;
using Rubberduck.VBEditor;

namespace Rubberduck.Inspections.Concrete.UnreachableCaseInspection
{
    public interface IParseTreeValueVisitor
    {
        IParseTreeVisitorResults VisitChildren(QualifiedModuleName module, IRuleNode node);
    }

    public class EnumMember
    {
        public EnumMember(VBAParser.EnumerationStmt_ConstantContext constContext, long initValue)
        {
            ConstantContext = constContext;
            Value = initValue;
            HasAssignment = constContext.children.Any(ch => ch.Equals(constContext.GetToken(VBAParser.EQ, 0)));
        }
        public VBAParser.EnumerationStmt_ConstantContext ConstantContext { get; }
        public long Value { set; get; }
        public bool HasAssignment { get; }
    }

    public class ParseTreeValueVisitor : IParseTreeValueVisitor
    {
        private readonly IParseTreeValueFactory _valueFactory;
        private readonly Func<Declaration, (bool, string, string)> _valueDeclarationEvaluator;
        private readonly IReadOnlyList<QualifiedContext<VBAParser.EnumerationStmtContext>> _enumStmtContexts;

        public ParseTreeValueVisitor(
            IParseTreeValueFactory valueFactory,
            IReadOnlyList<QualifiedContext<VBAParser.EnumerationStmtContext>> allEnums, 
            Func<QualifiedModuleName, ParserRuleContext, (bool success, IdentifierReference idRef)> identifierReferenceRetriever, 
            Func<Declaration, (bool, string, string)> valueDeclarationEvaluator = null)
        {
            _valueFactory = valueFactory;
            IdentifierReferenceRetriever = identifierReferenceRetriever;
            _enumStmtContexts = allEnums;
            _valueDeclarationEvaluator = valueDeclarationEvaluator ?? GetValuedDeclaration;
        }

        //This does not use a qualified context in order to avoid constant boxing and unboxing.
        private Func<QualifiedModuleName, ParserRuleContext, (bool success, IdentifierReference idRef)> IdentifierReferenceRetriever { get; }

        public IParseTreeVisitorResults VisitChildren(QualifiedModuleName module, IRuleNode ruleNode)
        {
            var newResults = new ParseTreeVisitorResults();
            return VisitChildren(module, ruleNode, newResults);
        }

        //The known results get passed along instead of aggregating from the bottom since other contexts can get already visited when resolving the value of other contexts.
        //Passing the results along avoids performing the resolution multiple times.
        private IMutableParseTreeVisitorResults VisitChildren(QualifiedModuleName module, IRuleNode node, IMutableParseTreeVisitorResults knownResults)
        {
            if (!(node is ParserRuleContext context))
            {
                return knownResults;
            }

            var valueResults = knownResults;
            foreach (var child in context.children)
            {
                valueResults = Visit(module, child, valueResults);
            }

            return valueResults;
        }

        private IMutableParseTreeVisitorResults Visit(QualifiedModuleName module, IParseTree tree, IMutableParseTreeVisitorResults knownResults)
        {
            var valueResults = knownResults;
            if (tree is ParserRuleContext context && !(context is VBAParser.WhiteSpaceContext))
            {
                valueResults =  Visit(module, context, valueResults);
            }

            return valueResults;
        }

        private IMutableParseTreeVisitorResults Visit(QualifiedModuleName module, ParserRuleContext parserRuleContext, IMutableParseTreeVisitorResults knownResults)
        {
            switch (parserRuleContext)
            {
                case VBAParser.LExprContext lExpr:
                    return Visit(module, lExpr, knownResults);
                case VBAParser.LiteralExprContext litExpr:
                    return Visit(litExpr, knownResults);
                case VBAParser.CaseClauseContext caseClause:
                    var caseClauseResults = VisitChildren(module, caseClause, knownResults);
                    caseClauseResults.AddIfNotPresent(caseClause, _valueFactory.Create(caseClause.GetText()));
                    return caseClauseResults;
                case VBAParser.RangeClauseContext rangeClause:
                    var rangeClauseResults = VisitChildren(module, rangeClause, knownResults);
                    rangeClauseResults.AddIfNotPresent(rangeClause, _valueFactory.Create(rangeClause.GetText()));
                    return rangeClauseResults;
                case VBAParser.LogicalNotOpContext _:
                case VBAParser.UnaryMinusOpContext _:
                    return VisitUnaryOpEvaluationContext(module, parserRuleContext, knownResults);
                default:
                    if (IsUnaryResultContext(parserRuleContext))
                    {
                        return VisitUnaryResultContext(module, parserRuleContext, knownResults);
                    }
                    if (IsBinaryOpEvaluationContext(parserRuleContext))
                    {
                        return VisitBinaryOpEvaluationContext(module, parserRuleContext, knownResults);
                    }

                    return knownResults;
            }
        }

        private IMutableParseTreeVisitorResults Visit(QualifiedModuleName module, VBAParser.LExprContext context, IMutableParseTreeVisitorResults knownResults)
        {
            if (knownResults.Contains(context))
            {
                return knownResults;
            }

            var valueResults = knownResults;

            IParseTreeValue newResult = null;
            if (TryGetLExprValue(module, context, ref valueResults, out string lExprValue, out string declaredType))
            {
                newResult = _valueFactory.CreateDeclaredType(lExprValue, declaredType);
            }
            else
            {
                var simpleName = context.GetDescendent<VBAParser.SimpleNameExprContext>();
                if (TryGetIdentifierReferenceForContext(module, simpleName, out var reference))
                {
                    var declarationTypeName = GetBaseTypeForDeclaration(reference.Declaration);
                    newResult = _valueFactory.CreateDeclaredType(context.GetText(), declarationTypeName);
                }
            }

            if (newResult != null)
            {
                valueResults.AddIfNotPresent(context, newResult);
            }

            return valueResults;
        }

        private IMutableParseTreeVisitorResults Visit(VBAParser.LiteralExprContext context, IMutableParseTreeVisitorResults knownResults)
        {
            if (knownResults.Contains(context))
            {
                return knownResults;
            }

            var valueResults = knownResults;
            var nResult = _valueFactory.Create(context.GetText());
            valueResults.AddIfNotPresent(context, nResult);

            return valueResults;
        }

        private IMutableParseTreeVisitorResults VisitBinaryOpEvaluationContext(QualifiedModuleName module, ParserRuleContext context, IMutableParseTreeVisitorResults knownResults)
        {
            var valueResults = VisitChildren(module, context, knownResults);

            var (lhs, rhs, operatorSymbol) = RetrieveOpEvaluationElements(context, valueResults);
            if (lhs is null || rhs is null)
            {
                return valueResults;
            }
            if (lhs.IsOverflowExpression)
            {
                valueResults.AddIfNotPresent(context, lhs);
                return valueResults;
            }

            if (rhs.IsOverflowExpression)
            {
                valueResults.AddIfNotPresent(context, rhs);
                return valueResults;
            }

            var calculator = new ParseTreeExpressionEvaluator(_valueFactory, context.IsOptionCompareBinary());
            var result = calculator.Evaluate(lhs, rhs, operatorSymbol);
            valueResults.AddIfNotPresent(context, result);

            return valueResults;
        }

        private IMutableParseTreeVisitorResults VisitUnaryOpEvaluationContext(QualifiedModuleName module, ParserRuleContext context, IMutableParseTreeVisitorResults knownResults)
        {
            var valueResults = VisitChildren(module, context, knownResults);

            var (lhs, rhs, operatorSymbol) = RetrieveOpEvaluationElements(context, valueResults);
            if (lhs is null || rhs != null)
            {
                return valueResults;
            }

            var calculator = new ParseTreeExpressionEvaluator(_valueFactory, context.IsOptionCompareBinary());
            var result = calculator.Evaluate(lhs, operatorSymbol);
            valueResults.AddIfNotPresent(context, result);

            return valueResults;
        }

        private static (IParseTreeValue LHS, IParseTreeValue RHS, string Symbol) RetrieveOpEvaluationElements(ParserRuleContext context, IMutableParseTreeVisitorResults knownResults)
        {
            (IParseTreeValue LHS, IParseTreeValue RHS, string Symbol) operandElements = (null, null, string.Empty);
            foreach (var child in NonWhitespaceChildren(context))
            {
                if (child is ParserRuleContext childContext)
                {
                    if (operandElements.LHS is null)
                    {
                        operandElements.LHS = knownResults.GetValue(childContext);
                    }
                    else if (operandElements.RHS is null)
                    {
                        operandElements.RHS = knownResults.GetValue(childContext);
                    }
                }
                else
                {
                    operandElements.Symbol = child.GetText();
                }
            }

            return operandElements;
        }

        private IMutableParseTreeVisitorResults VisitUnaryResultContext(QualifiedModuleName module, ParserRuleContext parserRuleContext, IMutableParseTreeVisitorResults knownResults)
        {
            var valueResults = VisitChildren(module, parserRuleContext, knownResults);

            var firstChildWithValue = ParserRuleContextChildren(parserRuleContext)
                .FirstOrDefault(childContext => valueResults.Contains(childContext));

            if (firstChildWithValue != null)
            {
                valueResults.AddIfNotPresent(parserRuleContext, valueResults.GetValue(firstChildWithValue));
            }

            return valueResults;
        }

        private IMutableParseTreeVisitorResults VisitChildren(QualifiedModuleName module, ParserRuleContext context, IMutableParseTreeVisitorResults knownResults)
        {
            if (knownResults.Contains(context))
            {
                return knownResults;
            }

            var valueResults = knownResults;
            foreach (var childContext in ParserRuleContextChildren(context))
            {
                valueResults = Visit(module, childContext, valueResults);
            }

            return valueResults;
        }

        private static IEnumerable<ParserRuleContext> ParserRuleContextChildren(ParserRuleContext ptParent)
            => NonWhitespaceChildren(ptParent).Where(ch => ch is ParserRuleContext).Cast<ParserRuleContext>();

        private static IEnumerable<IParseTree> NonWhitespaceChildren(ParserRuleContext ptParent)
            => ptParent.children.Where(ch => !(ch is VBAParser.WhiteSpaceContext));

        private bool TryGetLExprValue(QualifiedModuleName module, VBAParser.LExprContext lExprContext, ref IMutableParseTreeVisitorResults knownResults, out string expressionValue, out string declaredTypeName)
        {
            expressionValue = string.Empty;
            declaredTypeName = string.Empty;
            if (lExprContext.TryGetChildContext(out VBAParser.MemberAccessExprContext memberAccess))
            {
                var member = memberAccess.GetChild<VBAParser.UnrestrictedIdentifierContext>();
                var (typeName, valueText, resultValues) = GetContextValue(module, member, knownResults);
                knownResults = resultValues;
                declaredTypeName = typeName;
                expressionValue = valueText;
                return true;
            }

            if (lExprContext.TryGetChildContext(out VBAParser.SimpleNameExprContext smplName))
            {
                var (typeName, valueText, resultValues) = GetContextValue(module, smplName, knownResults);
                knownResults = resultValues;
                declaredTypeName = typeName;
                expressionValue = valueText;
                return true;
            }

            if (lExprContext.TryGetChildContext(out VBAParser.IndexExprContext idxExpr)
                && ParseTreeValue.TryGetNonPrintingControlCharCompareToken(idxExpr.GetText(), out string comparableToken))
            {
                declaredTypeName = Tokens.String;
                expressionValue = comparableToken;
                return true;
            }

            return false;
        }

        private (bool IsType, string ExpressionValue, string TypeName) GetValuedDeclaration(Declaration declaration)
        {
            if (!(declaration is ValuedDeclaration valuedDeclaration))
            {
                return (false, null, null);
            }

            var typeName = GetBaseTypeForDeclaration(declaration);
            return (true, valuedDeclaration.Expression, typeName);
        }

        private (string declarationTypeName, string expressionValue, IMutableParseTreeVisitorResults resultValues) GetContextValue(QualifiedModuleName module, ParserRuleContext context, IMutableParseTreeVisitorResults knownResults)
        {
            if (!TryGetIdentifierReferenceForContext(module, context, out var rangeClauseIdentifierReference))
            {
                return (string.Empty, context.GetText(), knownResults);
            }
            
            var declaration = rangeClauseIdentifierReference.Declaration;
            var expressionValue = rangeClauseIdentifierReference.IdentifierName;
            var declaredTypeName = GetBaseTypeForDeclaration(declaration);

            var (isValuedDeclaration, valuedExpressionValue, typeName) = _valueDeclarationEvaluator(declaration);
            if (isValuedDeclaration)
            {
                if (ParseTreeValue.TryGetNonPrintingControlCharCompareToken(valuedExpressionValue, out string resolvedValue))
                {
                    return (Tokens.String, resolvedValue, knownResults);
                }

                if (long.TryParse(valuedExpressionValue, out _))
                {
                    return (typeName, valuedExpressionValue, knownResults);
                }

                expressionValue = valuedExpressionValue;
                declaredTypeName = typeName;
            }

            if (declaration.DeclarationType.HasFlag(DeclarationType.Constant))
            {
                var (constantTokenExpressionValue, resultValues) = GetConstantContextValueToken(module, declaration.Context, knownResults);
                return (declaredTypeName, constantTokenExpressionValue, resultValues);
            }

            if (declaration.DeclarationType.HasFlag(DeclarationType.EnumerationMember))
            {
                var (constantExpressionValue, resultValues) = GetConstantContextValueToken(module, declaration.Context, knownResults);
                if (!constantExpressionValue.Equals(string.Empty))
                {
                    return (Tokens.Long, constantExpressionValue, resultValues);
                }

                var (enumMembers, valueResults) = EnumMembers(resultValues);
                var enumValue = enumMembers.SingleOrDefault(dt => dt.ConstantContext == declaration.Context);
                var enumExpressionValue = enumValue?.Value.ToString() ?? string.Empty;
                return (Tokens.Long, enumExpressionValue, valueResults);
            }

            return (declaredTypeName, expressionValue, knownResults);
        }

        private bool TryGetIdentifierReferenceForContext(QualifiedModuleName module, ParserRuleContext context, out IdentifierReference referenceForContext)
        {
            if (IdentifierReferenceRetriever == null)
            {
                referenceForContext = null;
                return false;
            }

            var (success, reference) = IdentifierReferenceRetriever(module, context);
            referenceForContext = reference;
            return success;
        }

        private (string valueText, IMutableParseTreeVisitorResults valueResults) GetConstantContextValueToken(QualifiedModuleName module, ParserRuleContext context, IMutableParseTreeVisitorResults knownResults)
        {
            if (context is null)
            {
                return (string.Empty, knownResults);
            }

            var declarationContextChildren = context.children.ToList();
            var equalsSymbolIndex = declarationContextChildren.FindIndex(ch => ch.Equals(context.GetToken(VBAParser.EQ, 0)));

            var contextsOfInterest = new List<ParserRuleContext>();
            for (int idx = equalsSymbolIndex + 1; idx < declarationContextChildren.Count; idx++)
            {
                var childCtxt = declarationContextChildren[idx];
                if (!(childCtxt is VBAParser.WhiteSpaceContext))
                {
                    contextsOfInterest.Add((ParserRuleContext)childCtxt);
                }
            }

            foreach (var child in contextsOfInterest)
            {
                knownResults = Visit(module, child, knownResults);
                if (knownResults.TryGetValue(child, out var value))
                {
                    return (value.Token, knownResults);
                }
            }
            return (string.Empty, knownResults);
        }

        private string GetBaseTypeForDeclaration(Declaration declaration)
        {
            var localDeclaration = declaration;
            var iterationGuard = 0;
            while (!(localDeclaration is null) 
                && !localDeclaration.AsTypeIsBaseType 
                && iterationGuard++ < 5)
            {
                localDeclaration = localDeclaration.AsTypeDeclaration;
            }
            return localDeclaration is null ? declaration.AsTypeName : localDeclaration.AsTypeName;
        }

        private static bool IsUnaryResultContext<T>(T context)
        {
            return context is VBAParser.SelectStartValueContext
                || context is VBAParser.SelectEndValueContext
                || context is VBAParser.ParenthesizedExprContext
                || context is VBAParser.SelectExpressionContext;
        }

        private static bool IsBinaryOpEvaluationContext<T>(T context)
        {
            if (context is VBAParser.ExpressionContext expressionContext)
            {

                return expressionContext.IsBinaryMathContext()
                    || expressionContext.IsBinaryLogicalContext()
                    || context is VBAParser.ConcatOpContext;
            }
            return false;
        }

        private (IReadOnlyList<EnumMember> enumMembers, IMutableParseTreeVisitorResults resultValues) EnumMembers(IMutableParseTreeVisitorResults knownResults)
        {
            if (knownResults.EnumMembers.Count > 0)
            {
                return (knownResults.EnumMembers, knownResults);
            }

            var resultValues = LoadEnumMemberValues(_enumStmtContexts, knownResults);
            return (resultValues.EnumMembers, resultValues);
        }

        //The enum members incrementally to the parse tree visitor result are used within the call to Visit.
        private IMutableParseTreeVisitorResults LoadEnumMemberValues(IReadOnlyList<QualifiedContext<VBAParser.EnumerationStmtContext>> enumStmtContexts, IMutableParseTreeVisitorResults knownResults)
        {
            var valueResults = knownResults;
            foreach (var qualifiedEnumStmt in enumStmtContexts)
            {
                var module = qualifiedEnumStmt.ModuleName;
                var enumStmt = qualifiedEnumStmt.Context;
                long enumAssignedValue = -1;
                var enumConstContexts = enumStmt.children
                    .OfType<VBAParser.EnumerationStmt_ConstantContext>();
                foreach (var enumConstContext in enumConstContexts)
                {
                    enumAssignedValue++;
                    var enumMember = new EnumMember(enumConstContext, enumAssignedValue);
                    if (enumMember.HasAssignment)
                    {
                        valueResults = Visit(module, enumMember.ConstantContext, valueResults);

                        var (valueText, resultValues) = GetConstantContextValueToken(module, enumMember.ConstantContext, valueResults);
                        valueResults = resultValues;
                        if (!valueText.Equals(string.Empty))
                        {
                            enumMember.Value = long.Parse(valueText);
                            enumAssignedValue = enumMember.Value;
                        }
                    }
                    valueResults.Add(enumMember);
                }
            }

            return valueResults;
        }
    }
}
