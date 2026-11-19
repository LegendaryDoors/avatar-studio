// XenosRecomp's HLSL recompiler (hedge-dev/XenosRecomp 990d03b, shader_recompiler.cpp), ported to C#.
// Copyright (c) 2025 hedge-dev and contributors, MIT (XenosRecomp.LICENSE).
// This file: the shared types (shader.h, constant_table.h, shader_code.h) and the output buffer.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace AvatarStudio
{
    /// <summary>One shader container to HLSL, as XenosRecomp's single-file mode writes it.</summary>
    sealed partial class XenosRecompiler
    {
        /// <summary>The HLSL for `container`, with `include` (shader_common.h) embedded.</summary>
        public static string Recompile(byte[] container, string include)
        {
            XenosRecompiler r = new XenosRecompiler();
            r.recompile(container, include);
            return r.@out.ToString();
        }

        // ---- ShaderRecompiler's fields (shader_recompiler.h) ----------------------------------------

        private readonly StringBuilder @out = new StringBuilder();
        private uint indentation;
        private bool isPixelShader;
        private byte[] shaderData;                       // the whole container
        private int constantTableData;                   // offset of the ConstantTable in shaderData
        private readonly Dictionary<uint, VertexElement> vertexElements = new Dictionary<uint, VertexElement>();
        private readonly Dictionary<uint, string> interpolators = new Dictionary<uint, string>();
        private readonly Dictionary<uint, ConstantInfo> float4Constants = new Dictionary<uint, ConstantInfo>();
        private readonly Dictionary<uint, string> boolConstants = new Dictionary<uint, string>();
        private readonly Dictionary<uint, string> samplers = new Dictionary<uint, string>();
        private readonly Dictionary<uint, uint> ifEndLabels = new Dictionary<uint, uint>();
        private uint specConstantsMask;

        // shader_common.h, the C++ side (UNLEASHED_RECOMP not defined)
        private const uint SPEC_CONSTANT_R11G11B10_NORMAL = 1u << 0;
        private const uint SPEC_CONSTANT_ALPHA_TEST = 1u << 1;

        // std::unordered_map::emplace: never replaces an existing key
        private static void Emplace<TK, TV>(Dictionary<TK, TV> d, TK k, TV v) { if (!d.ContainsKey(k)) d.Add(k, v); }

        private void indent()
        {
            for (uint i = 0; i < indentation; i++)
                @out.Append('\t');
        }

        // ---- StringBuffer: fmt's {} {N} {:X} {N:X} and {{ }} --------------------------------------------

        private void print(string fmt, params object[] args) { Format(@out, fmt, args); }

        private void println(string fmt, params object[] args) { Format(@out, fmt, args); @out.Append('\n'); }

        /// <summary>fmt::format for the specs the recompiler uses.</summary>
        private static string format(string fmt, params object[] args)
        {
            StringBuilder sb = new StringBuilder();
            Format(sb, fmt, args);
            return sb.ToString();
        }

        private static void Format(StringBuilder sb, string fmt, object[] args)
        {
            int next = 0;
            for (int i = 0; i < fmt.Length; i++)
            {
                char c = fmt[i];
                if (c == '{' && i + 1 < fmt.Length && fmt[i + 1] == '{') { sb.Append('{'); i++; continue; }
                if (c == '}' && i + 1 < fmt.Length && fmt[i + 1] == '}') { sb.Append('}'); i++; continue; }
                if (c == '}') throw new FormatException("unmatched } in " + fmt);
                if (c != '{') { sb.Append(c); continue; }
                int end = fmt.IndexOf('}', i);
                if (end < 0) throw new FormatException("unmatched { in " + fmt);
                string field = fmt.Substring(i + 1, end - i - 1);
                int colon = field.IndexOf(':');
                string index = colon < 0 ? field : field.Substring(0, colon), spec = colon < 0 ? "" : field.Substring(colon + 1);
                int n = index.Length == 0 ? next++ : int.Parse(index, CultureInfo.InvariantCulture);
                sb.Append(FormatArg(args[n], spec));
                i = end;
            }
        }

        private static string FormatArg(object a, string spec)
        {
            if (spec == "X")
            {
                if (a is uint) return ((uint)a).ToString("X", CultureInfo.InvariantCulture);
                if (a is int) return ((int)a).ToString("X", CultureInfo.InvariantCulture);
                if (a is ushort) return ((ushort)a).ToString("X", CultureInfo.InvariantCulture);
                throw new FormatException("{:X} of " + a.GetType().Name);
            }
            if (spec.Length != 0) throw new FormatException("format spec {:" + spec + "} not supported");
            if (a is string) return (string)a;
            if (a is char) return ((char)a).ToString();
            if (a is bool) return (bool)a ? "true" : "false";
            if (a is uint) return ((uint)a).ToString(CultureInfo.InvariantCulture);
            if (a is int) return ((int)a).ToString(CultureInfo.InvariantCulture);
            if (a is ushort) return ((ushort)a).ToString(CultureInfo.InvariantCulture);
            if (a is ulong) return ((ulong)a).ToString(CultureInfo.InvariantCulture);
            if (a is long) return ((long)a).ToString(CultureInfo.InvariantCulture);
            // an enum must be cast first, as the C++ casts it (uint32_t(x))
            throw new FormatException("cannot format a " + a.GetType().Name);
        }

        // ---- strings and bitfields ---------------------------------------------------------------------

        /// <summary>The NUL-terminated string at `o`.</summary>
        private static string CString(byte[] d, int o)
        {
            int e = o;
            while (d[e] != 0) e++;
            return Latin1.GetString(d, o, e - o);
        }

        /// <summary>`bits` bits of `w` from bit `lo` (MSVC bitfields: the first field is the lowest bits).</summary>
        internal static uint Bits(uint w, int lo, int bits) { return (w >> lo) & (bits == 32 ? 0xFFFFFFFFu : (1u << bits) - 1); }

        /// <summary>A signed bitfield, sign-extended.</summary>
        internal static int SBits(uint w, int lo, int bits) { return (int)(w << (32 - lo - bits)) >> (32 - bits); }

        // ---- shader_recompiler.cpp's tables -------------------------------------------------------------

        // names are copied as raw bytes, as the C++ copies them
        internal static readonly Encoding Latin1 = Encoding.GetEncoding(28591);

        private static readonly char[] SWIZZLES = { 'x', 'y', 'z', 'w', '0', '1', '_', '_' };

        private static readonly string[] USAGE_TYPES =
        {
            "float4", "float4", "uint4", "uint4", "float4", "float4", "uint4", "uint4", "float4", "float4", "float4", "float4", "float4", "float4",
        };

        private static readonly string[] USAGE_VARIABLES =
        {
            "Position", "BlendWeight", "BlendIndices", "Normal", "PointSize", "TexCoord", "Tangent", "Binormal", "TessFactor", "PositionT",
            "Color", "Fog", "Depth", "Sample",
        };

        private static readonly string[] USAGE_SEMANTICS =
        {
            "POSITION", "BLENDWEIGHT", "BLENDINDICES", "NORMAL", "PSIZE", "TEXCOORD", "TANGENT", "BINORMAL", "TESSFACTOR", "POSITIONT",
            "COLOR", "FOG", "DEPTH", "SAMPLE",
        };

        private struct DeclUsageLocation
        {
            public DeclUsage usage; public uint usageIndex; public uint location;
            public DeclUsageLocation(DeclUsage u, uint i, uint l) { usage = u; usageIndex = i; location = l; }
        }

        private static readonly DeclUsageLocation[] USAGE_LOCATIONS =
        {
            new DeclUsageLocation(DeclUsage.Position, 0, 0), new DeclUsageLocation(DeclUsage.Normal, 0, 1),
            new DeclUsageLocation(DeclUsage.Tangent, 0, 2), new DeclUsageLocation(DeclUsage.Binormal, 0, 3),
            new DeclUsageLocation(DeclUsage.TexCoord, 0, 4), new DeclUsageLocation(DeclUsage.TexCoord, 1, 5),
            new DeclUsageLocation(DeclUsage.TexCoord, 2, 6), new DeclUsageLocation(DeclUsage.TexCoord, 3, 7),
            new DeclUsageLocation(DeclUsage.Color, 0, 8), new DeclUsageLocation(DeclUsage.BlendIndices, 0, 9),
            new DeclUsageLocation(DeclUsage.BlendWeight, 0, 10), new DeclUsageLocation(DeclUsage.Color, 1, 11),
            new DeclUsageLocation(DeclUsage.TexCoord, 4, 12), new DeclUsageLocation(DeclUsage.TexCoord, 5, 13),
            new DeclUsageLocation(DeclUsage.TexCoord, 6, 14), new DeclUsageLocation(DeclUsage.TexCoord, 7, 15),
            new DeclUsageLocation(DeclUsage.Position, 1, 15),
        };

        // std::pair<DeclUsage, size_t>: first, second
        private static readonly KeyValuePair<DeclUsage, uint>[] INTERPOLATORS = MakeInterpolators();

        private static KeyValuePair<DeclUsage, uint>[] MakeInterpolators()
        {
            List<KeyValuePair<DeclUsage, uint>> l = new List<KeyValuePair<DeclUsage, uint>>();
            for (uint i = 0; i < 16; i++) l.Add(new KeyValuePair<DeclUsage, uint>(DeclUsage.TexCoord, i));
            l.Add(new KeyValuePair<DeclUsage, uint>(DeclUsage.Color, 0));
            l.Add(new KeyValuePair<DeclUsage, uint>(DeclUsage.Color, 1));
            return l.ToArray();
        }

        private static readonly string[] TEXTURE_DIMENSIONS = { "2D", "3D", "Cube" };

        private static FetchDestinationSwizzle getDestSwizzle(uint dstSwizzle, uint index)
        {
            return (FetchDestinationSwizzle)((dstSwizzle >> (int)(index * 3)) & 0x7);
        }

        // ---- constant_table.h ------------------------------------------------------------------------------

        enum ParameterClass : ushort { Scalar, Vector, MatrixRows, MatrixColumns, Object, Struct }

        enum ParameterType : ushort
        {
            Void, Bool, Int, Float, String, Texture, Texture1D, Texture2D, Texture3D, TextureCube, Sampler, Sampler1D, Sampler2D,
            Sampler3D, SamplerCube, PixelShader, VertexShader, PixelFragment, VertexFragment, Unsupported
        }

        enum RegisterSet : ushort { Bool, Int4, Float4, Sampler }

        /// <summary>D3DXSHADER_TYPEINFO (16 bytes, big-endian), read at `o`.</summary>
        struct TypeInfo
        {
            public ParameterClass parameterClass; public ParameterType parameterType;
            public ushort rows, columns, elements, structMembers; public uint structMemberInfo;

            public const int Size = 16;

            public static TypeInfo Read(byte[] d, int o)
            {
                return new TypeInfo
                {
                    parameterClass = (ParameterClass)Be.U16(d, o), parameterType = (ParameterType)Be.U16(d, o + 2), rows = Be.U16(d, o + 4),
                    columns = Be.U16(d, o + 6), elements = Be.U16(d, o + 8), structMembers = Be.U16(d, o + 10), structMemberInfo = Be.U32(d, o + 12),
                };
            }
        }

        /// <summary>D3DXSHADER_CONSTANTINFO (20 bytes, big-endian); `Name` is the string at `name` in the constant table.</summary>
        sealed class ConstantInfo
        {
            public uint name; public RegisterSet registerSet; public ushort registerIndex, registerCount, reserved; public uint typeInfo, defaultValue;
            public string Name;

            public const int Size = 20;

            /// <summary>The record at `o`; `table` is the constant table's offset, which `name` and `typeInfo` count from.</summary>
            public static ConstantInfo Read(byte[] d, int o, int table)
            {
                ConstantInfo c = new ConstantInfo
                {
                    name = Be.U32(d, o), registerSet = (RegisterSet)Be.U16(d, o + 4), registerIndex = Be.U16(d, o + 6), registerCount = Be.U16(d, o + 8),
                    reserved = Be.U16(d, o + 10), typeInfo = Be.U32(d, o + 12), defaultValue = Be.U32(d, o + 16),
                };
                int e = table + (int)c.name;
                while (d[e] != 0) e++;
                c.Name = Latin1.GetString(d, table + (int)c.name, e - table - (int)c.name);
                return c;
            }
        }

        /// <summary>D3DXSHADER_CONSTANTTABLE (28 bytes, big-endian).</summary>
        struct ConstantTable
        {
            public uint size, creator, version, constants, constantInfo, flags, target;

            public static ConstantTable Read(byte[] d, int o)
            {
                return new ConstantTable
                {
                    size = Be.U32(d, o), creator = Be.U32(d, o + 4), version = Be.U32(d, o + 8), constants = Be.U32(d, o + 12),
                    constantInfo = Be.U32(d, o + 16), flags = Be.U32(d, o + 20), target = Be.U32(d, o + 24),
                };
            }
        }

        // ---- shader.h -------------------------------------------------------------------------------------

        /// <summary>ShaderContainer (36 bytes, big-endian) at offset 0.</summary>
        struct ShaderContainer
        {
            public uint flags, virtualSize, physicalSize, fieldC, constantTableOffset, definitionTableOffset, shaderOffset, field1C, field20;

            public static ShaderContainer Read(byte[] d)
            {
                return new ShaderContainer
                {
                    flags = Be.U32(d, 0), virtualSize = Be.U32(d, 4), physicalSize = Be.U32(d, 8), fieldC = Be.U32(d, 12),
                    constantTableOffset = Be.U32(d, 16), definitionTableOffset = Be.U32(d, 20), shaderOffset = Be.U32(d, 24),
                    field1C = Be.U32(d, 28), field20 = Be.U32(d, 32),
                };
            }
        }

        /// <summary>Shader (24 bytes) at shaderOffset; VertexShader and PixelShader follow it.</summary>
        struct Shader
        {
            public uint physicalOffset, size, field8, fieldC, field10, interpolatorInfo;

            public const int Size = 24;

            public static Shader Read(byte[] d, int o)
            {
                return new Shader
                {
                    physicalOffset = Be.U32(d, o), size = Be.U32(d, o + 4), field8 = Be.U32(d, o + 8), fieldC = Be.U32(d, o + 12),
                    field10 = Be.U32(d, o + 16), interpolatorInfo = Be.U32(d, o + 20),
                };
            }
        }

        /// <summary>VertexShader: Shader, then field18, vertexElementCount, field20 and the u32 array
        /// vertexElementsAndInterpolators at o + 36.</summary>
        struct VertexShader
        {
            public Shader shader; public uint field18, vertexElementCount, field20; public int vertexElementsAndInterpolators;   // offset of [0]

            public static VertexShader Read(byte[] d, int o)
            {
                return new VertexShader
                {
                    shader = Shader.Read(d, o), field18 = Be.U32(d, o + 24), vertexElementCount = Be.U32(d, o + 28), field20 = Be.U32(d, o + 32),
                    vertexElementsAndInterpolators = o + 36,
                };
            }
        }

        /// <summary>PixelShader: Shader, then field18, outputs and the u32 array interpolators at o + 32.</summary>
        struct PixelShader
        {
            public Shader shader; public uint field18; public PixelShaderOutputs outputs; public int interpolators;   // offset of [0]

            public static PixelShader Read(byte[] d, int o)
            {
                return new PixelShader
                {
                    shader = Shader.Read(d, o), field18 = Be.U32(d, o + 24), outputs = (PixelShaderOutputs)Be.U32(d, o + 28), interpolators = o + 32,
                };
            }
        }

        [Flags]
        enum PixelShaderOutputs : uint
        {
            PIXEL_SHADER_OUTPUT_COLOR0 = 0x1, PIXEL_SHADER_OUTPUT_COLOR1 = 0x2, PIXEL_SHADER_OUTPUT_COLOR2 = 0x4,
            PIXEL_SHADER_OUTPUT_COLOR3 = 0x8, PIXEL_SHADER_OUTPUT_DEPTH = 0x10
        }

        // Float4Definition (8 bytes): registerIndex u16, count u16, physicalOffset u32.
        // Int4Definition: registerIndex u16, count u16, then `count` u32 values.
        // DefinitionTable: field0, field4, field8, fieldC, size (u32 each), then the definitions (u32 words)
        // at +20: float4, int4 and bool runs, each ended by a zero word. Read them with Be.U16 / Be.U32.

        enum DeclUsage : uint
        {
            Position = 0, BlendWeight = 1, BlendIndices = 2, Normal = 3, PointSize = 4, TexCoord = 5, Tangent = 6, Binormal = 7,
            TessFactor = 8, PositionT = 9, Color = 10, Fog = 11, Depth = 12, Sample = 13
        }

        /// <summary>VertexElement from one u32: address:12, usage:4, usageIndex:4.</summary>
        struct VertexElement
        {
            public readonly uint w;
            public VertexElement(uint v) { w = v; }
            public uint address { get { return XenosRecompiler.Bits(w, 0, 12); } }
            public DeclUsage usage { get { return (DeclUsage)XenosRecompiler.Bits(w, 12, 4); } }
            public uint usageIndex { get { return XenosRecompiler.Bits(w, 16, 4); } }
        }

        /// <summary>Interpolator from one u32: usageIndex:4, usage:4, reg:4.</summary>
        struct Interpolator
        {
            public readonly uint w;
            public Interpolator(uint v) { w = v; }
            public uint usageIndex { get { return XenosRecompiler.Bits(w, 0, 4); } }
            public DeclUsage usage { get { return (DeclUsage)XenosRecompiler.Bits(w, 4, 4); } }
            public uint reg { get { return XenosRecompiler.Bits(w, 8, 4); } }
        }

        static class Be
        {
            public static uint U32(byte[] d, int o) { return (uint)(d[o] << 24 | d[o + 1] << 16 | d[o + 2] << 8 | d[o + 3]); }
            public static ushort U16(byte[] d, int o) { return (ushort)(d[o] << 8 | d[o + 1]); }
        }

        // ---- shader_code.h: control flow (two 48-bit instructions in three words) ----------------------------

        enum ControlFlowOpcode : uint
        {
            Nop = 0, Exec = 1, ExecEnd = 2, CondExec = 3, CondExecEnd = 4, CondExecPred = 5, CondExecPredEnd = 6, LoopStart = 7,
            LoopEnd = 8, CondCall = 9, Return = 10, CondJmp = 11, Alloc = 12, CondExecPredClean = 13, CondExecPredCleanEnd = 14,
            MarkVsFetchDone = 15,
        }

        /// <summary>ControlFlowInstruction: `lo` is its first 32 bits, `hi` the next 16 (code0/code1 or code2/code3).</summary>
        struct ControlFlowInstruction
        {
            public readonly uint lo, hi;
            public ControlFlowInstruction(uint lo, uint hi) { this.lo = lo; this.hi = hi; }

            public ControlFlowOpcode opcode { get { return (ControlFlowOpcode)XenosRecompiler.Bits(hi, 12, 4); } }
            public CfExec exec { get { return new CfExec(lo, hi); } }
            public CfCondExec condExec { get { return new CfCondExec(lo, hi); } }
            public CfExecPred condExecPred { get { return new CfExecPred(lo, hi); } }
            public CfLoopStart loopStart { get { return new CfLoopStart(lo, hi); } }
            public CfLoopEnd loopEnd { get { return new CfLoopEnd(lo, hi); } }
            public CfCondCall condCall { get { return new CfCondCall(lo, hi); } }
            public CfReturn ret { get { return new CfReturn(lo, hi); } }
            public CfCondJmp condJmp { get { return new CfCondJmp(lo, hi); } }
            public CfAlloc alloc { get { return new CfAlloc(lo, hi); } }
        }

        /// <summary>ControlFlowExecInstruction (and ControlFlowExecPredInstruction / ControlFlowCondExecPredInstruction: CfExecPred).</summary>
        struct CfExec
        {
            readonly uint lo, hi;
            public CfExec(uint lo, uint hi) { this.lo = lo; this.hi = hi; }
            public uint address { get { return XenosRecompiler.Bits(lo, 0, 12); } }
            public uint count { get { return XenosRecompiler.Bits(lo, 12, 3); } }
            public uint isYield { get { return XenosRecompiler.Bits(lo, 15, 1); } }
            public uint sequence { get { return XenosRecompiler.Bits(lo, 16, 12); } }
            public uint vertexCacheHigh { get { return XenosRecompiler.Bits(lo, 28, 4); } }
            public uint vertexCacheLow { get { return XenosRecompiler.Bits(hi, 0, 2); } }
            public uint isPredicateClean { get { return XenosRecompiler.Bits(hi, 9, 1); } }
            public uint absoluteAddressing { get { return XenosRecompiler.Bits(hi, 11, 1); } }
            public ControlFlowOpcode opcode { get { return (ControlFlowOpcode)XenosRecompiler.Bits(hi, 12, 4); } }
        }

        /// <summary>ControlFlowExecPredInstruction and ControlFlowCondExecPredInstruction (the same layout).</summary>
        struct CfExecPred
        {
            readonly uint lo, hi;
            public CfExecPred(uint lo, uint hi) { this.lo = lo; this.hi = hi; }
            public uint address { get { return XenosRecompiler.Bits(lo, 0, 12); } }
            public uint count { get { return XenosRecompiler.Bits(lo, 12, 3); } }
            public uint isYield { get { return XenosRecompiler.Bits(lo, 15, 1); } }
            public uint sequence { get { return XenosRecompiler.Bits(lo, 16, 12); } }
            public uint vertexCacheHigh { get { return XenosRecompiler.Bits(lo, 28, 4); } }
            public uint vertexCacheLow { get { return XenosRecompiler.Bits(hi, 0, 2); } }
            public uint isPredicateClean { get { return XenosRecompiler.Bits(hi, 9, 1); } }
            public uint condition { get { return XenosRecompiler.Bits(hi, 10, 1); } }
            public uint absoluteAddressing { get { return XenosRecompiler.Bits(hi, 11, 1); } }
            public ControlFlowOpcode opcode { get { return (ControlFlowOpcode)XenosRecompiler.Bits(hi, 12, 4); } }
        }

        /// <summary>ControlFlowCondExecInstruction.</summary>
        struct CfCondExec
        {
            readonly uint lo, hi;
            public CfCondExec(uint lo, uint hi) { this.lo = lo; this.hi = hi; }
            public uint address { get { return XenosRecompiler.Bits(lo, 0, 12); } }
            public uint count { get { return XenosRecompiler.Bits(lo, 12, 3); } }
            public uint isYield { get { return XenosRecompiler.Bits(lo, 15, 1); } }
            public uint sequence { get { return XenosRecompiler.Bits(lo, 16, 12); } }
            public uint vertexCacheHigh { get { return XenosRecompiler.Bits(lo, 28, 4); } }
            public uint vertexCacheLow { get { return XenosRecompiler.Bits(hi, 0, 2); } }
            public uint boolAddress { get { return XenosRecompiler.Bits(hi, 2, 8); } }
            public uint condition { get { return XenosRecompiler.Bits(hi, 10, 1); } }
            public uint absoluteAddressing { get { return XenosRecompiler.Bits(hi, 11, 1); } }
            public ControlFlowOpcode opcode { get { return (ControlFlowOpcode)XenosRecompiler.Bits(hi, 12, 4); } }
        }

        struct CfLoopStart
        {
            readonly uint lo, hi;
            public CfLoopStart(uint lo, uint hi) { this.lo = lo; this.hi = hi; }
            public uint address { get { return XenosRecompiler.Bits(lo, 0, 13); } }
            public uint isRepeat { get { return XenosRecompiler.Bits(lo, 13, 1); } }
            public uint loopId { get { return XenosRecompiler.Bits(lo, 16, 5); } }
            public uint absoluteAddressing { get { return XenosRecompiler.Bits(hi, 11, 1); } }
            public ControlFlowOpcode opcode { get { return (ControlFlowOpcode)XenosRecompiler.Bits(hi, 12, 4); } }
        }

        struct CfLoopEnd
        {
            readonly uint lo, hi;
            public CfLoopEnd(uint lo, uint hi) { this.lo = lo; this.hi = hi; }
            public uint address { get { return XenosRecompiler.Bits(lo, 0, 13); } }
            public uint loopId { get { return XenosRecompiler.Bits(lo, 16, 5); } }
            public uint isPredicatedBreak { get { return XenosRecompiler.Bits(lo, 21, 1); } }
            public uint condition { get { return XenosRecompiler.Bits(hi, 10, 1); } }
            public uint absoluteAddressing { get { return XenosRecompiler.Bits(hi, 11, 1); } }
            public ControlFlowOpcode opcode { get { return (ControlFlowOpcode)XenosRecompiler.Bits(hi, 12, 4); } }
        }

        struct CfCondCall
        {
            readonly uint lo, hi;
            public CfCondCall(uint lo, uint hi) { this.lo = lo; this.hi = hi; }
            public uint address { get { return XenosRecompiler.Bits(lo, 0, 13); } }
            public uint isUnconditional { get { return XenosRecompiler.Bits(lo, 13, 1); } }
            public uint isPredicated { get { return XenosRecompiler.Bits(lo, 14, 1); } }
            public uint boolAddress { get { return XenosRecompiler.Bits(hi, 2, 8); } }
            public uint condition { get { return XenosRecompiler.Bits(hi, 10, 1); } }
            public uint absoluteAddressing { get { return XenosRecompiler.Bits(hi, 11, 1); } }
            public ControlFlowOpcode opcode { get { return (ControlFlowOpcode)XenosRecompiler.Bits(hi, 12, 4); } }
        }

        struct CfReturn
        {
            readonly uint lo, hi;
            public CfReturn(uint lo, uint hi) { this.lo = lo; this.hi = hi; }
            public uint absoluteAddressing { get { return XenosRecompiler.Bits(hi, 11, 1); } }
            public ControlFlowOpcode opcode { get { return (ControlFlowOpcode)XenosRecompiler.Bits(hi, 12, 4); } }
        }

        struct CfCondJmp
        {
            readonly uint lo, hi;
            public CfCondJmp(uint lo, uint hi) { this.lo = lo; this.hi = hi; }
            public uint address { get { return XenosRecompiler.Bits(lo, 0, 13); } }
            public uint isUnconditional { get { return XenosRecompiler.Bits(lo, 13, 1); } }
            public uint isPredicated { get { return XenosRecompiler.Bits(lo, 14, 1); } }
            public uint direction { get { return XenosRecompiler.Bits(hi, 1, 1); } }
            public uint boolAddress { get { return XenosRecompiler.Bits(hi, 2, 8); } }
            public uint condition { get { return XenosRecompiler.Bits(hi, 10, 1); } }
            public uint absoluteAddressing { get { return XenosRecompiler.Bits(hi, 11, 1); } }
            public ControlFlowOpcode opcode { get { return (ControlFlowOpcode)XenosRecompiler.Bits(hi, 12, 4); } }
        }

        struct CfAlloc
        {
            readonly uint lo, hi;
            public CfAlloc(uint lo, uint hi) { this.lo = lo; this.hi = hi; }
            public uint size { get { return XenosRecompiler.Bits(lo, 0, 3); } }
            public uint isUnserialized { get { return XenosRecompiler.Bits(hi, 8, 1); } }
            public uint allocType { get { return XenosRecompiler.Bits(hi, 9, 2); } }
            public ControlFlowOpcode opcode { get { return (ControlFlowOpcode)XenosRecompiler.Bits(hi, 12, 4); } }
        }

        // ---- shader_code.h: fetch and ALU instructions (three words each, code0..code2) ---------------------

        enum FetchOpcode : uint
        {
            VertexFetch = 0, TextureFetch = 1, GetTextureBorderColorFrac = 16, GetTextureComputedLod = 17, GetTextureGradients = 18,
            GetTextureWeights = 19, SetTextureLod = 24, SetTextureGradientsHorz = 25, SetTextureGradientsVert = 26
        }

        enum FetchDestinationSwizzle : uint { X = 0, Y = 1, Z = 2, W = 3, Zero = 4, One = 5, Keep = 7 }

        struct VertexFetchInstruction
        {
            public readonly uint w0, w1, w2;
            public VertexFetchInstruction(uint a, uint b, uint c) { w0 = a; w1 = b; w2 = c; }
            public FetchOpcode opcode { get { return (FetchOpcode)XenosRecompiler.Bits(w0, 0, 5); } }
            public uint srcRegister { get { return XenosRecompiler.Bits(w0, 5, 6); } }
            public uint srcRegisterAm { get { return XenosRecompiler.Bits(w0, 11, 1); } }
            public uint dstRegister { get { return XenosRecompiler.Bits(w0, 12, 6); } }
            public uint dstRegisterAam { get { return XenosRecompiler.Bits(w0, 18, 1); } }
            public uint mustBeOne { get { return XenosRecompiler.Bits(w0, 19, 1); } }
            public uint constIndex { get { return XenosRecompiler.Bits(w0, 20, 5); } }
            public uint constIndexSelect { get { return XenosRecompiler.Bits(w0, 25, 2); } }
            public uint prefetchCount { get { return XenosRecompiler.Bits(w0, 27, 3); } }
            public uint srcSwizzle { get { return XenosRecompiler.Bits(w0, 30, 2); } }
            public uint dstSwizzle { get { return XenosRecompiler.Bits(w1, 0, 12); } }
            public uint formatCompAll { get { return XenosRecompiler.Bits(w1, 12, 1); } }
            public uint numFormatAll { get { return XenosRecompiler.Bits(w1, 13, 1); } }
            public uint signedRfModeAll { get { return XenosRecompiler.Bits(w1, 14, 1); } }
            public uint isIndexRounded { get { return XenosRecompiler.Bits(w1, 15, 1); } }
            public uint format { get { return XenosRecompiler.Bits(w1, 16, 6); } }
            public uint reserved2 { get { return XenosRecompiler.Bits(w1, 22, 2); } }
            public int expAdjust { get { return XenosRecompiler.SBits(w1, 24, 6); } }
            public uint isMiniFetch { get { return XenosRecompiler.Bits(w1, 30, 1); } }
            public uint isPredicated { get { return XenosRecompiler.Bits(w1, 31, 1); } }
            public uint stride { get { return XenosRecompiler.Bits(w2, 0, 8); } }
            public int offset { get { return XenosRecompiler.SBits(w2, 8, 23); } }
            public uint predicateCondition { get { return XenosRecompiler.Bits(w2, 31, 1); } }
        }

        enum TextureDimension : uint { Texture1D, Texture2D, Texture3D, TextureCube }

        struct TextureFetchInstruction
        {
            public readonly uint w0, w1, w2;
            public TextureFetchInstruction(uint a, uint b, uint c) { w0 = a; w1 = b; w2 = c; }
            public FetchOpcode opcode { get { return (FetchOpcode)XenosRecompiler.Bits(w0, 0, 5); } }
            public uint srcRegister { get { return XenosRecompiler.Bits(w0, 5, 6); } }
            public uint srcRegisterAm { get { return XenosRecompiler.Bits(w0, 11, 1); } }
            public uint dstRegister { get { return XenosRecompiler.Bits(w0, 12, 6); } }
            public uint dstRegisterAm { get { return XenosRecompiler.Bits(w0, 18, 1); } }
            public uint fetchValidOnly { get { return XenosRecompiler.Bits(w0, 19, 1); } }
            public uint constIndex { get { return XenosRecompiler.Bits(w0, 20, 5); } }
            public uint texCoordDenorm { get { return XenosRecompiler.Bits(w0, 25, 1); } }
            public uint srcSwizzle { get { return XenosRecompiler.Bits(w0, 26, 6); } }
            public uint dstSwizzle { get { return XenosRecompiler.Bits(w1, 0, 12); } }
            public uint magFilter { get { return XenosRecompiler.Bits(w1, 12, 2); } }
            public uint minFilter { get { return XenosRecompiler.Bits(w1, 14, 2); } }
            public uint mipFilter { get { return XenosRecompiler.Bits(w1, 16, 2); } }
            public uint anisoFilter { get { return XenosRecompiler.Bits(w1, 18, 3); } }
            public uint arbitraryFilter { get { return XenosRecompiler.Bits(w1, 21, 3); } }
            public uint volMagFilter { get { return XenosRecompiler.Bits(w1, 24, 2); } }
            public uint volMinFilter { get { return XenosRecompiler.Bits(w1, 26, 2); } }
            public uint useCompLod { get { return XenosRecompiler.Bits(w1, 28, 1); } }
            public uint useRegLod { get { return XenosRecompiler.Bits(w1, 29, 1); } }
            public uint isPredicated { get { return XenosRecompiler.Bits(w1, 31, 1); } }
            public uint useRegGradients { get { return XenosRecompiler.Bits(w2, 0, 1); } }
            public uint sampleLocation { get { return XenosRecompiler.Bits(w2, 1, 1); } }
            public int lodBias { get { return XenosRecompiler.SBits(w2, 2, 7); } }
            public TextureDimension dimension { get { return (TextureDimension)XenosRecompiler.Bits(w2, 14, 2); } }
            public int offsetX { get { return XenosRecompiler.SBits(w2, 16, 5); } }
            public int offsetY { get { return XenosRecompiler.SBits(w2, 21, 5); } }
            public int offsetZ { get { return XenosRecompiler.SBits(w2, 26, 5); } }
            public uint predCondition { get { return XenosRecompiler.Bits(w2, 31, 1); } }
        }

        enum AluScalarOpcode : uint
        {
            Adds = 0, AddsPrev = 1, Muls = 2, MulsPrev = 3, MulsPrev2 = 4, Maxs = 5, Mins = 6, Seqs = 7, Sgts = 8, Sges = 9, Snes = 10,
            Frcs = 11, Truncs = 12, Floors = 13, Exp = 14, Logc = 15, Log = 16, Rcpc = 17, Rcpf = 18, Rcp = 19, Rsqc = 20, Rsqf = 21,
            Rsq = 22, MaxAs = 23, MaxAsf = 24, Subs = 25, SubsPrev = 26, SetpEq = 27, SetpNe = 28, SetpGt = 29, SetpGe = 30,
            SetpInv = 31, SetpPop = 32, SetpClr = 33, SetpRstr = 34, KillsEq = 35, KillsGt = 36, KillsGe = 37, KillsNe = 38,
            KillsOne = 39, Sqrt = 40, Mulsc0 = 42, Mulsc1 = 43, Addsc0 = 44, Addsc1 = 45, Subsc0 = 46, Subsc1 = 47, Sin = 48,
            Cos = 49, RetainPrev = 50
        }

        enum AluVectorOpcode : uint
        {
            Add = 0, Mul = 1, Max = 2, Min = 3, Seq = 4, Sgt = 5, Sge = 6, Sne = 7, Frc = 8, Trunc = 9, Floor = 10, Mad = 11,
            CndEq = 12, CndGe = 13, CndGt = 14, Dp4 = 15, Dp3 = 16, Dp2Add = 17, Cube = 18, Max4 = 19, SetpEqPush = 20,
            SetpNePush = 21, SetpGtPush = 22, SetpGePush = 23, KillEq = 24, KillGt = 25, KillGe = 26, KillNe = 27, Dst = 28, MaxA = 29
        }

        enum ExportRegister : uint
        {
            VSInterpolator0 = 0, VSInterpolator1, VSInterpolator2, VSInterpolator3, VSInterpolator4, VSInterpolator5, VSInterpolator6,
            VSInterpolator7, VSInterpolator8, VSInterpolator9, VSInterpolator10, VSInterpolator11, VSInterpolator12, VSInterpolator13,
            VSInterpolator14, VSInterpolator15, VSPosition = 62, VSPointSizeEdgeFlagKillVertex = 63, PSColor0 = 0, PSColor1, PSColor2,
            PSColor3, PSDepth = 61, ExportAddress = 32, ExportData0 = 33, ExportData1, ExportData2, ExportData3, ExportData4,
        }

        struct AluInstruction
        {
            public readonly uint w0, w1, w2;
            public AluInstruction(uint a, uint b, uint c) { w0 = a; w1 = b; w2 = c; }
            public uint vectorDest { get { return XenosRecompiler.Bits(w0, 0, 6); } }
            public uint vectorDestRelative { get { return XenosRecompiler.Bits(w0, 6, 1); } }
            public uint absConstants { get { return XenosRecompiler.Bits(w0, 7, 1); } }
            public uint scalarDest { get { return XenosRecompiler.Bits(w0, 8, 6); } }
            public uint scalarDestRelative { get { return XenosRecompiler.Bits(w0, 14, 1); } }
            public uint exportData { get { return XenosRecompiler.Bits(w0, 15, 1); } }
            public uint vectorWriteMask { get { return XenosRecompiler.Bits(w0, 16, 4); } }
            public uint scalarWriteMask { get { return XenosRecompiler.Bits(w0, 20, 4); } }
            public uint vectorSaturate { get { return XenosRecompiler.Bits(w0, 24, 1); } }
            public uint scalarSaturate { get { return XenosRecompiler.Bits(w0, 25, 1); } }
            public AluScalarOpcode scalarOpcode { get { return (AluScalarOpcode)XenosRecompiler.Bits(w0, 26, 6); } }
            public uint src3Swizzle { get { return XenosRecompiler.Bits(w1, 0, 8); } }
            public uint src2Swizzle { get { return XenosRecompiler.Bits(w1, 8, 8); } }
            public uint src1Swizzle { get { return XenosRecompiler.Bits(w1, 16, 8); } }
            public uint src3Negate { get { return XenosRecompiler.Bits(w1, 24, 1); } }
            public uint src2Negate { get { return XenosRecompiler.Bits(w1, 25, 1); } }
            public uint src1Negate { get { return XenosRecompiler.Bits(w1, 26, 1); } }
            public uint predicateCondition { get { return XenosRecompiler.Bits(w1, 27, 1); } }
            public uint isPredicated { get { return XenosRecompiler.Bits(w1, 28, 1); } }
            public uint constAddressRegisterRelative { get { return XenosRecompiler.Bits(w1, 29, 1); } }
            public uint const1Relative { get { return XenosRecompiler.Bits(w1, 30, 1); } }
            public uint const0Relative { get { return XenosRecompiler.Bits(w1, 31, 1); } }
            public uint src3Register { get { return XenosRecompiler.Bits(w2, 0, 8); } }
            public uint src2Register { get { return XenosRecompiler.Bits(w2, 8, 8); } }
            public uint src1Register { get { return XenosRecompiler.Bits(w2, 16, 8); } }
            public AluVectorOpcode vectorOpcode { get { return (AluVectorOpcode)XenosRecompiler.Bits(w2, 24, 5); } }
            public uint src3Select { get { return XenosRecompiler.Bits(w2, 29, 1); } }
            public uint src2Select { get { return XenosRecompiler.Bits(w2, 30, 1); } }
            public uint src1Select { get { return XenosRecompiler.Bits(w2, 31, 1); } }
        }
    }
}
