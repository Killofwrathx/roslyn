// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.GenerateFromMembers;
using Microsoft.CodeAnalysis.Internal.Log;
using Microsoft.CodeAnalysis.LanguageServices;
using Microsoft.CodeAnalysis.PickMembers;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Text;

namespace Microsoft.CodeAnalysis.GenerateConstructorFromMembers
{
    [ExportCodeRefactoringProvider(LanguageNames.CSharp, LanguageNames.VisualBasic,
        Name = PredefinedCodeRefactoringProviderNames.GenerateConstructorFromMembers), Shared]
    [ExtensionOrder(Before = PredefinedCodeRefactoringProviderNames.GenerateEqualsAndGetHashCodeFromMembers)]
    internal partial class GenerateConstructorFromMembersCodeRefactoringProvider : AbstractGenerateFromMembersCodeRefactoringProvider
    {
        private const string AddNullChecksId = nameof(AddNullChecksId);

        private readonly IPickMembersService _pickMembersService_forTesting;

        public GenerateConstructorFromMembersCodeRefactoringProvider() : this(null)
        {
        }

        /// <summary>
        /// For testing purposes only.
        /// </summary>
        internal GenerateConstructorFromMembersCodeRefactoringProvider(IPickMembersService pickMembersService_forTesting)
        {
            _pickMembersService_forTesting = pickMembersService_forTesting;
        }

        public override async Task ComputeRefactoringsAsync(CodeRefactoringContext context)
        {
            var document = context.Document;
            var textSpan = context.Span;
            var cancellationToken = context.CancellationToken;

            if (document.Project.Solution.Workspace.Kind == WorkspaceKind.MiscellaneousFiles)
            {
                return;
            }

            var actions = await this.GenerateConstructorFromMembersAsync(
                document, textSpan, addNullChecks: false, cancellationToken: cancellationToken).ConfigureAwait(false);
            context.RegisterRefactorings(actions);

            if (actions.IsDefaultOrEmpty && textSpan.IsEmpty)
            {
                await HandleNonSelectionAsync(context).ConfigureAwait(false);
            }
        }

        private async Task HandleNonSelectionAsync(CodeRefactoringContext context)
        {
            var document = context.Document;
            var textSpan = context.Span;
            var cancellationToken = context.CancellationToken;

            var syntaxFacts = document.GetLanguageService<ISyntaxFactsService>();
            var sourceText = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);

            // We offer the refactoring when the user is either on the header of a class/struct,
            // or if they're between any members of a class/struct and are on a blank line.
            if (!syntaxFacts.IsOnTypeHeader(root, textSpan.Start) &&
                !syntaxFacts.IsBetweenTypeMembers(sourceText, root, textSpan.Start))
            {
                return;
            }

            var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);

            // Only supported on classes/structs.
            var containingType = GetEnclosingNamedType(semanticModel, root, textSpan.Start, cancellationToken);
            if (containingType?.TypeKind != TypeKind.Class && containingType?.TypeKind != TypeKind.Struct)
            {
                return;
            }

            // No constructors for static classes.
            if (containingType.IsStatic)
            {
                return;
            }

            // Find all the possible writable instance fields/properties.  If there are any, then
            // show a dialog to the user to select the ones they want.  Otherwise, if there are none
            // just offer to generate the no-param constructor if they don't already have one.
            var viableMembers = containingType.GetMembers().WhereAsArray(IsWritableInstanceFieldOrProperty);
            if (viableMembers.Length == 0)
            {
                var noParamConstructor = containingType.InstanceConstructors.FirstOrDefault(c => c.Parameters.Length == 0);
                if (noParamConstructor == null ||
                    noParamConstructor.IsImplicitlyDeclared)
                {
                    // Offer to just make the no-param-constructor directly.
                    var state = State.TryGenerate(this, document, textSpan, containingType,
                        ImmutableArray<ISymbol>.Empty, cancellationToken);

                    if (state != null)
                    {
                        context.RegisterRefactoring(
                            new FieldDelegatingCodeAction(this, document, state, addNullChecks: false));
                    }
                }

                // already had an explicit, no-param constructor.  No need to offer anything.
                return;
            }

            var pickMemberOptions = ArrayBuilder<PickMembersOption>.GetInstance();
            var canAddNullCheck = viableMembers.Any(
                m => m.GetSymbolType().CanAddNullCheck());

            if (canAddNullCheck)
            {
                var options = await document.GetOptionsAsync(cancellationToken).ConfigureAwait(false);
                var optionValue = options.GetOption(GenerateConstructorFromMembersOptions.AddNullChecks);

                pickMemberOptions.Add(new PickMembersOption(
                    AddNullChecksId,
                    FeaturesResources.Add_null_checks,
                    optionValue));
            }

            context.RegisterRefactoring(
                new GenerateConstructorWithDialogCodeAction(
                    this, document, textSpan, containingType, viableMembers,
                    pickMemberOptions.ToImmutableAndFree()));
        }

        public async Task<ImmutableArray<CodeAction>> GenerateConstructorFromMembersAsync(
            Document document, TextSpan textSpan, bool addNullChecks, CancellationToken cancellationToken)
        {
            using (Logger.LogBlock(FunctionId.Refactoring_GenerateFromMembers_GenerateConstructorFromMembers, cancellationToken))
            {
                var info = await GetSelectedMemberInfoAsync(document, textSpan, cancellationToken).ConfigureAwait(false);
                if (info != null)
                {
                    var state = State.TryGenerate(this, document, textSpan, info.ContainingType, info.SelectedMembers, cancellationToken);
                    if (state != null && state.MatchingConstructor == null)
                    {
                        return GetCodeActions(document, state, addNullChecks);
                    }
                }

                return default(ImmutableArray<CodeAction>);
            }
        }

        private ImmutableArray<CodeAction> GetCodeActions(Document document, State state, bool addNullChecks)
        {
            var result = ArrayBuilder<CodeAction>.GetInstance();

            result.Add(new FieldDelegatingCodeAction(this, document, state, addNullChecks));
            if (state.DelegatedConstructor != null)
            {
                result.Add(new ConstructorDelegatingCodeAction(this, document, state, addNullChecks));
            }

            return result.ToImmutableAndFree();
        }
    }
}