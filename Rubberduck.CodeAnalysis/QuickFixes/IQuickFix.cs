﻿using System;
using System.Collections.Generic;
using Rubberduck.CodeAnalysis.Inspections;
using Rubberduck.Parsing.Rewriter;
using Rubberduck.Parsing.VBA.Parsing;

namespace Rubberduck.CodeAnalysis.QuickFixes
{
    public interface IQuickFix
    {
        void Fix(IInspectionResult result, IRewriteSession rewriteSession);
        string Description(IInspectionResult result);

        bool CanFixInProcedure { get; }
        bool CanFixInModule { get; }
        bool CanFixInProject { get; }

        IReadOnlyCollection<Type> SupportedInspections { get; }
        CodeKind TargetCodeKind { get; }

        void RegisterInspections(params Type[] inspections);
        void RemoveInspections(params Type[] inspections);
    }
}