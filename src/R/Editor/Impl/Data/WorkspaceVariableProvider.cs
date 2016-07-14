﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Common.Core;
using Microsoft.R.Components.ContentTypes;
using Microsoft.R.DataInspection;
using Microsoft.R.Editor.Completion.Definitions;
using Microsoft.R.Editor.Data;
using Microsoft.R.Host.Client;
using Microsoft.R.StackTracing;
using Microsoft.R.Support.Help;
using Microsoft.VisualStudio.Utilities;
using static Microsoft.R.DataInspection.REvaluationResultProperties;

namespace Microsoft.VisualStudio.R.Package.DataInspect {
    /// <summary>
    /// Provides name of variables and members declared in REPL workspace
    /// </summary>
    [Export(typeof(IVariablesProvider))]
    [ContentType(RContentTypeDefinition.ContentType)]
    internal sealed class WorkspaceVariableProvider : RSessionChangeWatcher, IVariablesProvider {
        private static readonly char[] _selectors = { '$', '@' };
        private const int _maxWaitTime = 2000;
        private const int _maxResults = 100;

        /// <summary>
        /// Collection of top-level variables
        /// </summary>
        private Dictionary<string, IRSessionDataObject> _topLevelVariables = new Dictionary<string, IRSessionDataObject>();
        private bool _updating;

        [ImportingConstructor]
        public WorkspaceVariableProvider(IRSessionProvider sessionProvider) : base(sessionProvider) { }

        #region IVariablesProvider
        /// <summary>
        /// Given variable name determines number of members
        /// </summary>
        /// <param name="variableName">Variable name or null if global scope</param>
        public int GetMemberCount(string variableName) {
            if (string.IsNullOrEmpty(variableName)) {
                // Global scope
                return _topLevelVariables.Values.Count;
            }

            // TODO: do estimate
            return _maxResults;
        }

        /// <summary>
        /// Given variable name returns variable members
        /// adhering to specified criteria. Last member name
        /// may be partial such as abc$def$g
        /// </summary>
        /// <param name="variableName">
        /// Variable name such as abc$def$g. 'g' may be partially typed
        /// in which case providers returns members of 'def' filtered to 'g' prefix.
        /// </param>
        /// <param name="maxCount">Max number of members to return</param>
        public IReadOnlyCollection<INamedItemInfo> GetMembers(string variableName, int maxCount) {
            try {
                // Split abc$def$g into parts. String may also be empty or end with $ or @.
                string[] parts = variableName.Split(_selectors);

                if ((parts.Length == 0 || parts[0].Length == 0) && variableName.Length > 0) {
                        // Something odd like $$ or $@ so we got empty parts
                        // and yet variable name is not empty. Don't show anything.
                        return new INamedItemInfo[0];
                }

                if (parts.Length == 0 || parts[0].Length == 0 || variableName.IndexOfAny(_selectors) < 0) {
                    // Global scope
                    return _topLevelVariables.Values
                        .Where(x => !x.IsHidden)
                        .Take(maxCount)
                        .Select(m => new VariableInfo(m))
                        .ToArray();
                }

                // May be a package object line mtcars$
                variableName = TrimToTrailingSelector(variableName);
                var sessionProvider = SessionProvider;
                var session = sessionProvider.GetOrCreate(GuidList.InteractiveWindowRSessionGuid);
                IReadOnlyList<IREvaluationResultInfo> infoList = null;
                try {
                    infoList = session.DescribeChildrenAsync(REnvironments.GlobalEnv, 
                               variableName, 
                               REvaluationResultProperties.HasChildrenProperty | REvaluationResultProperties.AccessorKindProperty,
                               null, _maxResults).WaitTimeout(_maxWaitTime);
                } catch(TimeoutException) { }

                if (infoList != null) {
                    return infoList
                                .Where(m => m is IRValueInfo && 
                                               (((IRValueInfo)m).AccessorKind == RChildAccessorKind.At ||
                                                ((IRValueInfo)m).AccessorKind == RChildAccessorKind.Dollar))
                                .Take(maxCount)
                                .Select(m => new VariableInfo(TrimLeadingSelector(m.Name), string.Empty))
                                .ToArray();
                }
            } catch (OperationCanceledException) { } catch (RException) { } catch (MessageTransportException) { }

            return new VariableInfo[0];
        }
        #endregion

        private static string TrimToTrailingSelector(string name) {
            int i = name.Length - 1;
            for (; i >= 0; i--) {
                if(_selectors.Contains(name[i])) {
                    return name.Substring(0, i);
                }
            }
            return string.Empty;
        }

        private static string TrimLeadingSelector(string name) {
            if (name.StartsWithOrdinal("$") || name.StartsWithOrdinal("@")) {
                return name.Substring(1);
            }
            return name;
        }

        protected override void SessionMutated() {
            UpdateList().DoNotWait();
        }

        private async Task UpdateList() {
            if (_updating) {
                return;
            }

            try {
                _updating = true;
                // May be null in tests
                var sessionProvider = SessionProvider;
                var session = sessionProvider.GetOrCreate(GuidList.InteractiveWindowRSessionGuid);
                if (session.IsHostRunning) {
                    var stackFrames = await session.TracebackAsync();

                    var globalStackFrame = stackFrames.FirstOrDefault(s => s.IsGlobal);
                    if (globalStackFrame != null) {
                        const REvaluationResultProperties properties =
                            ExpressionProperty |
                            AccessorKindProperty |
                            TypeNameProperty |
                            ClassesProperty |
                            LengthProperty |
                            SlotCountProperty |
                            AttributeCountProperty |
                            DimProperty |
                            FlagsProperty;
                        var evaluation = await globalStackFrame.TryEvaluateAndDescribeAsync("base::environment()", "Global Environment", properties, RValueRepresentations.Str());
                        var e = new RSessionDataObject(evaluation);  // root level doesn't truncate children and return every variables

                        _topLevelVariables.Clear();

                        var children = await e.GetChildrenAsync();
                        if (children != null) {
                            foreach (var x in children) {
                                _topLevelVariables[x.Name] = x; // TODO: BUGBUG: this doesn't address removed variables
                            }
                        }
                    }
                }
            } finally {
                _updating = false;
            }
        }

        class VariableInfo : INamedItemInfo {
            public VariableInfo(IRSessionDataObject e) :
                this(e.Name, e.TypeName) { }

            public VariableInfo(string name, string typeName) {
                this.Name = name;
                if (typeName.EqualsOrdinal("closure") || typeName.EqualsOrdinal("builtin")) {
                    ItemType = NamedItemType.Function;
                } else {
                    ItemType = NamedItemType.Variable;
                }
            }

            public string Description { get; } = string.Empty;

            public NamedItemType ItemType { get; private set; }

            public string Name { get; set; }

            public string ActualName {
                get { return Name; }
            }
        }
    }
}
