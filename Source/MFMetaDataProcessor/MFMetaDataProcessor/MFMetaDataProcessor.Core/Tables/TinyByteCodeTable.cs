﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Mono.Cecil;

namespace MFMetaDataProcessor
{
    /// <summary>
    /// Encapsulates logic for storing method bodies (byte code) list and writing
    /// this collected list into target assembly in .NET Micro Framework format.
    /// </summary>
    public sealed class TinyByteCodeTable : ITinyTable
    {
        /// <summary>
        /// Helper class for calculating native methods CRC value.
        /// </summary>
        private readonly NativeMethodsCrc _nativeMethodsCrc;

        /// <summary>
        /// Binary writer for writing byte code in correct endianess.
        /// </summary>
        private readonly TinyBinaryWriter _writer;

        /// <summary>
        /// String literals table (used for obtaining string literal ID).
        /// </summary>
        private readonly TinyStringTable _stringTable;

        /// <summary>
        /// Methods references table (used for obtaining method reference id).
        /// </summary>
        private readonly TinyMemberReferenceTable _methodReferenceTable;

        /// <summary>
        /// Methods definitions table (used for obtaining method definition id).
        /// </summary>
        private readonly TinyMethodDefinitionTable _methodDefinitionTable;

        /// <summary>
        /// Typess references table (used for obtaining type reference id).
        /// </summary>
        private readonly TinyTypeReferenceTable _typeReferenceTable;

        /// <summary>
        /// Typess definitions table (used for obtaining type definition id).
        /// </summary>
        private TinyTypeDefinitionTable _typeDefinitionTable;

        /// <summary>
        /// Maps method bodies (in form of byte array) to method identifiers.
        /// </summary>
        private readonly IList<MethodDefinition> _methods = new List<MethodDefinition>();

        /// <summary>
        /// Maps method full names to method RVAs (offsets in resutling table).
        /// </summary>
        private readonly IDictionary<String, UInt16> _rvasByMethodNames =
            new Dictionary<String, UInt16>(StringComparer.Ordinal);

        /// <summary>
        /// Temprorary string table for code generators used duing initial load.
        /// </summary>
        private readonly TinyStringTable _fakeStringTable = new TinyStringTable();

        /// <summary>
        /// Last available method RVA.
        /// </summary>
        private UInt16 _lastAvailableRva;

        /// <summary>
        /// Creates new instance of <see cref="TinyByteCodeTable"/> object.
        /// </summary>
        /// <param name="nativeMethodsCrc">Helper class for native methods CRC.</param>
        /// <param name="writer">Binary writer for writing byte code in correct endianess.</param>
        /// <param name="stringTable">String references table (for obtaining string ID).</param>
        /// <param name="methodReferenceTable">External methods references table.</param>
        /// <param name="signaturesTable">Methods and fields signatures table.</param>
        /// <param name="typeReferenceTable"></param>
        /// <param name="typeDefinitionTable"></param>
        /// <param name="methodsDefinitions">Methods defintions list in Mono.Cecil format.</param>
        public TinyByteCodeTable(
            NativeMethodsCrc nativeMethodsCrc,
            TinyBinaryWriter writer,
            TinyStringTable stringTable,
            TinyMemberReferenceTable methodReferenceTable,
            TinySignaturesTable signaturesTable,
            TinyTypeReferenceTable typeReferenceTable,
            IEnumerable<MethodDefinition> methodsDefinitions)
        {
            _nativeMethodsCrc = nativeMethodsCrc;
            _writer = writer;
            _stringTable = stringTable;
            _methodReferenceTable = methodReferenceTable;
            _typeReferenceTable = typeReferenceTable;

            _methodDefinitionTable = new TinyMethodDefinitionTable(
                methodsDefinitions, stringTable, this, signaturesTable);
        }

        /// <summary>
        /// Gets instance of <see cref="TinyMethodDefinitionTable"/> object.
        /// </summary>
        public TinyMethodDefinitionTable MethodDefinitionTable
        {
            [DebuggerStepThrough]
            get { return _methodDefinitionTable; }
        }

        /// <summary>
        /// Next method identifier. Used for reproducing strange original MetadataProcessor behavior.
        /// </summary>
        public UInt16 NextMethodId { get { return (UInt16)_methods.Count; } }

        /// <summary>
        /// Returns method reference ID (index in methods definitions table) for passed method definition.
        /// </summary>
        /// <param name="method">Method definition in Mono.Cecil format.</param>
        /// <returns>
        /// New method reference ID (byte code also prepared for writing as part of process).
        /// </returns>
        public UInt16 GetMethodId(
            MethodDefinition method)
        {
            var rva = method.HasBody ? _lastAvailableRva : (UInt16)0xFFFF;
            var id = (UInt16)_methods.Count;

            _nativeMethodsCrc.UpdateCrc(method);
            var byteCode = CreateByteCode(method, _fakeStringTable, true);

            _methods.Add(method);
            _lastAvailableRva += (UInt16)byteCode.Length;

            _rvasByMethodNames.Add(method.FullName, rva);
            return id;
        }

        /// <summary>
        /// Returns method RVA (offset in byte code table) for passed method reference.
        /// </summary>
        /// <param name="method">Method reference in Mono.Cecil format.</param>
        /// <returns>
        /// Method RVA (method should be generated using <see cref="GetMethodId"/> before this call.
        /// </returns>
        public UInt16 GetMethodRva(
            MethodReference method)
        {
            UInt16 rva;
            return (_rvasByMethodNames.TryGetValue(method.FullName, out rva) ? rva : (UInt16)0xFFFF);
        }

        /// <inheritdoc/>
        public void Write(
            TinyBinaryWriter writer)
        {
            foreach (var method in _methods)
            {
                writer.WriteBytes(CreateByteCode(method, _stringTable, false));
            }
        }

        /// <summary>
        /// Updates main string table with strings stored in temp string table before code generation.
        /// </summary>
        internal void UpdateStringTable()
        {
            _stringTable.MergeValues(_fakeStringTable);
        }

        /// <summary>
        /// Helper method for injecting dependency. We unable to do it via constructor.
        /// </summary>
        /// <param name="typeDefinitionTable">Type definitions table.</param>
        internal void SetTypeDefinitionTable(
            TinyTypeDefinitionTable typeDefinitionTable)
        {
            _typeDefinitionTable = typeDefinitionTable;
        }

        private Byte[] CreateByteCode(
            MethodDefinition method,
            TinyStringTable stringTable,
            Boolean fixOperationsOffsets)
        {
            if (!method.HasBody)
            {
                return new Byte[0];
            }

            using(var stream = new MemoryStream())
            {
                var writer = new  CodeWriter(
                    method, _writer.GetMemoryBasedClone(stream),
                    stringTable, _methodReferenceTable, _methodDefinitionTable,
                    _typeReferenceTable, _typeDefinitionTable,
                    fixOperationsOffsets);
                writer.WriteMethodBody();
                return stream.ToArray();
            }
        }
    }
}