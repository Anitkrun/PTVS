﻿// Python Tools for Visual Studio
// Copyright(c) Microsoft Corporation
// All rights reserved.
//
// Licensed under the Apache License, Version 2.0 (the License); you may not use
// this file except in compliance with the License. You may obtain a copy of the
// License at http://www.apache.org/licenses/LICENSE-2.0
//
// THIS CODE IS PROVIDED ON AN  *AS IS* BASIS, WITHOUT WARRANTIES OR CONDITIONS
// OF ANY KIND, EITHER EXPRESS OR IMPLIED, INCLUDING WITHOUT LIMITATION ANY
// IMPLIED WARRANTIES OR CONDITIONS OF TITLE, FITNESS FOR A PARTICULAR PURPOSE,
// MERCHANTABLITY OR NON-INFRINGEMENT.
//
// See the Apache Version 2.0 License for specific language governing
// permissions and limitations under the License.

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.PythonTools.Interpreter;
using Microsoft.PythonTools.Parsing;
using Microsoft.PythonTools.Parsing.Ast;

namespace Microsoft.PythonTools.Analysis.Values {
    abstract class Protocol : AnalysisValue, IHasRichDescription {
        private Dictionary<string, IAnalysisSet> _members;

        public Protocol(ProtocolInfo self) {
            Self = self;
        }

        public ProtocolInfo Self { get; private set; }

        public virtual Protocol Clone(ProtocolInfo newSelf) {
            var p = ((Protocol)MemberwiseClone());
            p._members = null;
            p.Self = Self;
            return p;
        }

        protected void EnsureMembers() {
            if (_members == null) {
                var m = new Dictionary<string, IAnalysisSet>();
                EnsureMembers(m);
                _members = m;
            }
        }

        protected virtual void EnsureMembers(IDictionary<string, IAnalysisSet> members) {
        }

        protected IAnalysisSet MakeMethod(string qualname, IAnalysisSet returnValue) {
            return MakeMethod(qualname, Array.Empty<IAnalysisSet>(), returnValue);
        }

        protected IAnalysisSet MakeMethod(string qualname, IReadOnlyList<IAnalysisSet> arguments, IAnalysisSet returnValue) {
            var v = new ProtocolInfo(Self.DeclaringModule, Self.State);
            v.AddProtocol(new CallableProtocol(v, qualname, arguments, returnValue, PythonMemberType.Method));
            return v;
        }

        public override PythonMemberType MemberType => PythonMemberType.Unknown;

        public override IAnalysisSet GetInstanceType() => null;

        public override IDictionary<string, IAnalysisSet> GetAllMembers(IModuleContext moduleContext, GetMemberOptions options = GetMemberOptions.None) {
            EnsureMembers();
            return _members;
        }

        public override IAnalysisSet GetMember(Node node, AnalysisUnit unit, string name) {
            var res = base.GetMember(node, unit, name);
            EnsureMembers();
            if (_members.TryGetValue(name, out var m)) {
                return (m as Protocol)?.GetMember(node, unit, name) ?? m;
            }
            return res;
        }

        public override void SetMember(Node node, AnalysisUnit unit, string name, IAnalysisSet value) {
            base.SetMember(node, unit, name, value);
            EnsureMembers();
            if (_members.TryGetValue(name, out var m)) {
                (m as Protocol)?.SetMember(node, unit, name, value);
            }
        }

        public virtual IEnumerable<KeyValuePair<string, string>> GetRichDescription() {
            yield return new KeyValuePair<string, string>(WellKnownRichDescriptionKinds.Name, Name);
        }
    }

    class NameProtocol : Protocol {
        private readonly string _name, _doc;
        private readonly BuiltinTypeId _typeId;

        public NameProtocol(ProtocolInfo self, string name, string documentation = null, BuiltinTypeId typeId = BuiltinTypeId.Object) : base(self) {
            _name = name;
            _doc = documentation;
            _typeId = typeId;
        }

        public NameProtocol(ProtocolInfo self, IPythonType type) : base(self) {
            _name = type.Name;
            _doc = type.Documentation;
            _typeId = type.TypeId;
        }

        public override string Name => _name;
        public override string Documentation => _doc;
        internal override BuiltinTypeId TypeId => _typeId;
    }

    class CallableProtocol : Protocol {
        private readonly Lazy<OverloadResult[]> _overloads;

        public CallableProtocol(ProtocolInfo self, string qualname, IReadOnlyList<IAnalysisSet> arguments, IAnalysisSet returnType, PythonMemberType memberType = PythonMemberType.Function)
            : base(self) {
            Name = qualname ?? "callable";
            Arguments = arguments;
            ReturnType = returnType;
            _overloads = new Lazy<OverloadResult[]>(GenerateOverloads);
            MemberType = memberType;
        }

        public override string Name { get; }

        internal override BuiltinTypeId TypeId => BuiltinTypeId.Function;
        public override PythonMemberType MemberType { get; }

        protected override void EnsureMembers(IDictionary<string, IAnalysisSet> members) {
            members["__call__"] = Self;
        }

        private OverloadResult[] GenerateOverloads() {
            return new[] {
                new OverloadResult(Arguments.Select(ToParameterResult).ToArray(), Name)
            };
        }

        private ParameterResult ToParameterResult(IAnalysisSet set, int i) {
            return new ParameterResult($"${i + 1}", $"Parameter {i + 1}", string.Join(", ", set.GetShortDescriptions()));
        }

        public IReadOnlyList<IAnalysisSet> Arguments { get; }
        public IAnalysisSet ReturnType { get; }

        public override IAnalysisSet Call(Node node, AnalysisUnit unit, IAnalysisSet[] args, NameExpression[] keywordArgNames) {
            var def = base.Call(node, unit, args, keywordArgNames);
            return ReturnType ?? def;
        }

        public override IEnumerable<OverloadResult> Overloads => _overloads.Value;

        public override IEnumerable<KeyValuePair<string, string>> GetRichDescription() {
            yield return new KeyValuePair<string, string>(WellKnownRichDescriptionKinds.Name, Name);
            yield return new KeyValuePair<string, string>(WellKnownRichDescriptionKinds.Misc, "(");
            int argNumber = 1;
            foreach (var a in Arguments) {
                if (argNumber > 1) {
                    yield return new KeyValuePair<string, string>(WellKnownRichDescriptionKinds.Comma, ", ");
                }
                yield return new KeyValuePair<string, string>(WellKnownRichDescriptionKinds.Parameter, $"${argNumber}");

                foreach (var kv in a.GetRichDescriptions(" : ")) {
                    yield return kv;
                }
            }
            yield return new KeyValuePair<string, string>(WellKnownRichDescriptionKinds.Misc, ")");

            foreach (var kv in ReturnType.GetRichDescriptions(" -> ")) {
                yield return kv;
            }
        }
    }

    class IterableProtocol : Protocol {
        protected readonly IAnalysisSet _iterator;
        protected readonly IAnalysisSet _yielded;

        public IterableProtocol(ProtocolInfo self, IAnalysisSet yielded) : base(self) {
            _yielded = yielded;

            var iterator = new ProtocolInfo(Self.DeclaringModule, Self.State);
            iterator.AddProtocol(new IteratorProtocol(iterator, _yielded));
            _iterator = iterator;
        }

        protected override void EnsureMembers(IDictionary<string, IAnalysisSet> members) {
            members["__iter__"] = MakeMethod("__iter__", _iterator);
        }

        public override IAnalysisSet GetIterator(Node node, AnalysisUnit unit) => _iterator;
        public override IAnalysisSet GetEnumeratorTypes(Node node, AnalysisUnit unit) => _yielded;

        public override string Name => "iterable";

        public override IEnumerable<KeyValuePair<string, string>> GetRichDescription() {
            yield return new KeyValuePair<string, string>(WellKnownRichDescriptionKinds.Name, Name);
            if (_yielded.Any()) {
                yield return new KeyValuePair<string, string>(WellKnownRichDescriptionKinds.Misc, "[");
                foreach (var kv in _yielded.GetRichDescriptions()) {
                    yield return kv;
                }
                yield return new KeyValuePair<string, string>(WellKnownRichDescriptionKinds.Misc, "]");
            }
        }
    }

    class IteratorProtocol : Protocol {
        protected readonly IAnalysisSet _yielded;

        public IteratorProtocol(ProtocolInfo self, IAnalysisSet yielded) : base(self) {
            _yielded = yielded;
        }

        protected override void EnsureMembers(IDictionary<string, IAnalysisSet> members) {
            if (Self.DeclaringModule?.Tree?.LanguageVersion.Is3x() ?? true) {
                members["__next__"] = MakeMethod("__next__", _yielded);
            } else {
                members["next"] = MakeMethod("next", _yielded);
            }
            members["__iter__"] = MakeMethod("__iter__", Self);
        }

        public override IAnalysisSet GetEnumeratorTypes(Node node, AnalysisUnit unit) {
            return _yielded;
        }

        public override string Name => "iterator";

        public override IEnumerable<KeyValuePair<string, string>> GetRichDescription() {
            yield return new KeyValuePair<string, string>(WellKnownRichDescriptionKinds.Name, Name);
            if (_yielded.Any()) {
                yield return new KeyValuePair<string, string>(WellKnownRichDescriptionKinds.Misc, "[");
                foreach (var kv in _yielded.GetRichDescriptions()) {
                    yield return kv;
                }
                yield return new KeyValuePair<string, string>(WellKnownRichDescriptionKinds.Misc, "]");
            }
        }
    }

    class GetItemProtocol : Protocol {
        private readonly IAnalysisSet _keyType, _valueType;

        public GetItemProtocol(ProtocolInfo self, IAnalysisSet keys, IAnalysisSet values) : base(self) {
            _keyType = keys ?? self.AnalysisUnit.State.ClassInfos[BuiltinTypeId.Int].Instance;
            _valueType = values;
        }

        protected override void EnsureMembers(IDictionary<string, IAnalysisSet> members) {
            members["__getitem__"] = MakeMethod("__getitem__", _valueType);
        }

        public override IEnumerable<KeyValuePair<IAnalysisSet, IAnalysisSet>> GetItems() {
            yield return new KeyValuePair<IAnalysisSet, IAnalysisSet>(_keyType, _valueType);
        }

        public override IAnalysisSet GetIndex(Node node, AnalysisUnit unit, IAnalysisSet index) {
            if (index.IsObjectOrUnknown() || index.Intersect(_keyType, UnionComparer.Instances[1]).Any()) {
                return _valueType;
            }
            return base.GetIndex(node, unit, index);
        }

        public override string Name => "container";

        public override IEnumerable<KeyValuePair<string, string>> GetRichDescription() {
            yield return new KeyValuePair<string, string>(WellKnownRichDescriptionKinds.Name, Name);
            if (_valueType.Any()) {
                yield return new KeyValuePair<string, string>(WellKnownRichDescriptionKinds.Misc, "[");
                if (_keyType.Any(k => k.TypeId != BuiltinTypeId.Int)) {
                    foreach (var kv in _keyType.GetRichDescriptions(unionPrefix: "[", unionSuffix: "]")) {
                        yield return kv;
                    }
                    yield return new KeyValuePair<string, string>(WellKnownRichDescriptionKinds.Comma, ", ");
                }
                foreach (var kv in _valueType.GetRichDescriptions(unionPrefix: "[", unionSuffix: "]")) {
                    yield return kv;
                }
                yield return new KeyValuePair<string, string>(WellKnownRichDescriptionKinds.Misc, "]");
            }
        }
    }

    class TupleProtocol : IterableProtocol {
        private readonly IAnalysisSet[] _values;

        public TupleProtocol(ProtocolInfo self, IEnumerable<IAnalysisSet> values) : base(self, AnalysisSet.UnionAll(values)) {
            _values = values.ToArray();
        }

        protected override void EnsureMembers(IDictionary<string, IAnalysisSet> members) {
            members["__getitem__"] = MakeMethod("__getitem__", new[] { Self.AnalysisUnit.State.ClassInfos[BuiltinTypeId.Int].GetInstanceType() }, _yielded);
        }

        public override IAnalysisSet GetIndex(Node node, AnalysisUnit unit, IAnalysisSet index) {
            int i = -1;
            try {
                var constants = index.OfType<ConstantInfo>().Select(ci => ci.Value).OfType<int>().ToArray();
                if (constants.Length == 1) {
                    i = constants[0];
                }
            } catch (InvalidOperationException) {
                i = -1;
            }
            if (i < 0) {
                return _yielded;
            } else if (i < _values.Length) {
                return _values[i];
            } else {
                return AnalysisSet.Empty;
            }
        }

        public override string Name => "tuple";

        public override IEnumerable<KeyValuePair<string, string>> GetRichDescription() {
            yield return new KeyValuePair<string, string>(WellKnownRichDescriptionKinds.Name, Name);
            if (_values.Any()) {
                yield return new KeyValuePair<string, string>(WellKnownRichDescriptionKinds.Misc, "[");
                bool needComma = false;
                foreach (var v in _values) {
                    if (needComma) {
                        yield return new KeyValuePair<string, string>(WellKnownRichDescriptionKinds.Comma, ", ");
                    }
                    needComma = true;
                    foreach (var kv in v.GetRichDescriptions()) {
                        yield return kv;
                    }
                }
                yield return new KeyValuePair<string, string>(WellKnownRichDescriptionKinds.Misc, "]");
            }
        }
    }

    class MappingProtocol : IterableProtocol {
        private readonly IAnalysisSet _keyType, _valueType, _itemType;

        public MappingProtocol(ProtocolInfo self, IAnalysisSet keys, IAnalysisSet values, IAnalysisSet items) : base(self, keys) {
            _keyType = keys;
            _valueType = values;
            _itemType = items;
        }

        private IAnalysisSet MakeIterable(IAnalysisSet values) {
            var pi = new ProtocolInfo(DeclaringModule, Self.State);
            pi.AddProtocol(new IterableProtocol(pi, values));
            return pi;
        }

        private IAnalysisSet MakeView(IPythonType type, IAnalysisSet values) {
            var pi = new ProtocolInfo(DeclaringModule, Self.State);
            pi.AddProtocol(new NameProtocol(pi, type));
            pi.AddProtocol(new IterableProtocol(pi, values));
            return pi;
        }

        protected override void EnsureMembers(IDictionary<string, IAnalysisSet> members) {
            var state = Self.State;
            var itemsIter = MakeIterable(_itemType);

            if (state.LanguageVersion.Is3x()) {
                members["keys"] = MakeMethod("keys", MakeView(state.Types[BuiltinTypeId.DictKeys], _keyType));
                members["values"] = MakeMethod("values", MakeView(state.Types[BuiltinTypeId.DictValues], _valueType));
                members["items"] = MakeMethod("items", MakeView(state.Types[BuiltinTypeId.DictItems], _itemType));
            } else {
                members["viewkeys"] = MakeMethod("viewkeys", MakeView(state.Types[BuiltinTypeId.DictKeys], _keyType));
                members["viewvalues"] = MakeMethod("viewvalues", MakeView(state.Types[BuiltinTypeId.DictValues], _valueType));
                members["viewitems"] = MakeMethod("viewitems", MakeView(state.Types[BuiltinTypeId.DictItems], _itemType));
                var keysIter = MakeIterable(_keyType);
                members["keys"] = MakeMethod("keys", keysIter);
                members["iterkeys"] = MakeMethod("iterkeys", keysIter);
                var valuesIter = MakeIterable(_valueType);
                members["values"] = MakeMethod("values", valuesIter);
                members["itervalues"] = MakeMethod("itervalues", valuesIter);
                members["items"] = MakeMethod("items", itemsIter);
                members["iteritems"] = MakeMethod("iteritems", itemsIter);
            }

            members["clear"] = MakeMethod("clear", AnalysisSet.Empty);
            members["get"] = MakeMethod("get", new[] { _keyType }, _valueType);
            members["pop"] = MakeMethod("pop", new[] { _keyType }, _valueType);
            members["popitem"] = MakeMethod("popitem", new[] { _keyType }, _itemType);
            members["setdefault"] = MakeMethod("setdefault", new[] { _keyType, _valueType }, _valueType);
            members["update"] = MakeMethod("update", new[] { AnalysisSet.UnionAll(new IAnalysisSet[] { this, itemsIter }) }, AnalysisSet.Empty);
        }

        public override IEnumerable<KeyValuePair<IAnalysisSet, IAnalysisSet>> GetItems() {
            yield return new KeyValuePair<IAnalysisSet, IAnalysisSet>(_keyType, _valueType);
        }

        public override IAnalysisSet GetIndex(Node node, AnalysisUnit unit, IAnalysisSet index) {
            return _valueType;
        }

        public override string Name => "dict";

        public override IEnumerable<KeyValuePair<string, string>> GetRichDescription() {
            yield return new KeyValuePair<string, string>(WellKnownRichDescriptionKinds.Name, Name);
            if (_valueType.Any()) {
                yield return new KeyValuePair<string, string>(WellKnownRichDescriptionKinds.Misc, "[");
                if (_keyType.Any(k => k.TypeId != BuiltinTypeId.Int)) {
                    foreach (var kv in _keyType.GetRichDescriptions(unionPrefix: "[", unionSuffix: "]")) {
                        yield return kv;
                    }
                    yield return new KeyValuePair<string, string>(WellKnownRichDescriptionKinds.Comma, ", ");
                }
                foreach (var kv in _valueType.GetRichDescriptions(unionPrefix: "[", unionSuffix: "]")) {
                    yield return kv;
                }
                yield return new KeyValuePair<string, string>(WellKnownRichDescriptionKinds.Misc, "]");
            }
        }
    }

    class GeneratorProtocol : IteratorProtocol {
        private readonly IAnalysisSet _sent, _returned;

        public GeneratorProtocol(ProtocolInfo self, IAnalysisSet yields, IAnalysisSet sends, IAnalysisSet returns) : base(self, yields) {
            _sent = sends;
            _returned = returns;
        }

        protected override void EnsureMembers(IDictionary<string, IAnalysisSet> members) {
            base.EnsureMembers(members);
        }

        public override string Name => "generator";

        public override IEnumerable<KeyValuePair<string, string>> GetRichDescription() {
            yield return new KeyValuePair<string, string>(WellKnownRichDescriptionKinds.Name, Name);
            if (_yielded.Any() || _sent.Any() || _returned.Any()) {
                yield return new KeyValuePair<string, string>(WellKnownRichDescriptionKinds.Misc, "[");
                if (_yielded.Any()) {
                    foreach (var kv in _yielded.GetRichDescriptions(unionPrefix: "[", unionSuffix: "]")) {
                        yield return kv;
                    }
                } else {
                    yield return new KeyValuePair<string, string>(WellKnownRichDescriptionKinds.Misc, "[]");
                }

                if (_sent.Any()) {
                    yield return new KeyValuePair<string, string>(WellKnownRichDescriptionKinds.Comma, ", ");
                    foreach (var kv in _sent.GetRichDescriptions(unionPrefix: "[", unionSuffix: "]")) {
                        yield return kv;
                    }
                } else if (_returned.Any()) {
                    yield return new KeyValuePair<string, string>(WellKnownRichDescriptionKinds.Comma, ", ");
                    yield return new KeyValuePair<string, string>(WellKnownRichDescriptionKinds.Misc, "[]");
                }

                if (_returned.Any()) {
                    yield return new KeyValuePair<string, string>(WellKnownRichDescriptionKinds.Comma, ", ");
                    foreach (var kv in _sent.GetRichDescriptions(unionPrefix: "[", unionSuffix: "]")) {
                        yield return kv;
                    }
                }
                yield return new KeyValuePair<string, string>(WellKnownRichDescriptionKinds.Misc, "]");
            }
        }
    }

    class NamespaceProtocol : Protocol {
        private readonly string _name;
        private readonly VariableDef _values;

        public NamespaceProtocol(ProtocolInfo self, string name) : base(self) {
            _name = name;
            _values = new VariableDef();
        }

        public override Protocol Clone(ProtocolInfo newSelf) {
            var np = new NamespaceProtocol(newSelf, _name);
            _values.CopyTo(np._values);
            return np;
        }

        protected override void EnsureMembers(IDictionary<string, IAnalysisSet> members) {
            members[_name] = this;
        }

        public override string Name => _name;

        public override IAnalysisSet GetMember(Node node, AnalysisUnit unit, string name) {
            if (name == _name) {
                _values.AddDependency(unit);
                return _values.Types;
            }
            return AnalysisSet.Empty;
        }

        public override void SetMember(Node node, AnalysisUnit unit, string name, IAnalysisSet value) {
            _values.AddTypes(unit, value);
        }
    }
}
